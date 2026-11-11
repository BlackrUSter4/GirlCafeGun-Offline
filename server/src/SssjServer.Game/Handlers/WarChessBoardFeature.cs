using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The war chess board (LuaCall 539, sub-command in nType). The client computes moves and boss paths itself and reads the
/// board back from tasks, so the server applies each move, keeps the round flow and decides when a map ends.
///
/// INFERRED: each team gets one move a round, a boss reaching the cafe loses the map, killing every boss wins it, and
/// hard mode's score is 1000 minus 50 per round and 20 per item. The client shows those results but holds no such rules.
/// </summary>
public sealed class WarChessBoardFeature : IGameFeature
{
    internal const int Group = 97;
    private const int MapTask = 201, StepTask = 202, FightCache = 203, TeamHp1 = 204, TeamHp2 = 205, BossMove = 206, RoundTask = 207,
        FreezeTask = 208, MapPass = 210, MinRound = 250, BossHpBase = 300, MapScore = 320, GridBase = 500, GridEnd = 1000;
    private const int MaxMapCount = 20;

    // WarChessTower.GridStates
    private const int Empty = 0, Cafe = 1, Team1 = 3, Team2 = 4, BossBase = 5, Unmovable = 11, Obstacle = 12;
    // Bits 29-31 of the boss-move task: 0 player round, 1 boss round, 2 won, 3 lost.
    private const int PhaseShift = 29, PhaseBoss = 1, PhaseWon = 2, PhaseLost = 3;
    // WarChessTower.Error
    private const int ErrMapLock = 1, ErrInit = 2, ErrBirth = 3, ErrMove = 4;
    // SpItem.txt SpSkill
    private const int ItemMoveOnce = 1, ItemMoveTwice = 2, ItemObstacle = 3, ItemFreeze = 4;
    private const int StepsPerRound = 1, FullHp = 100, BossFullHp = 100;

    private sealed record MapConfig(int Columns, int Rows, int[][] Unmovable, (int X, int Y, int Boss)[] Bosses, int[] Cafe);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<(int Id, int Map, int Diff), MapConfig> _maps = new();
    private readonly Dictionary<int, int> _itemSkills = new();

    public string Name => "War chess board (539)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "ChessMap.txt")) is { } maps)
            foreach (var row in maps.Rows)
            {
                var size = TwentyFirstTowerTable.Numbers(maps.Str(row, "MapSize"));
                if (size.Length < 2) continue;
                var bossNumbers = TwentyFirstTowerTable.Numbers(maps.Str(row, "BossBirthPos"));
                var bosses = Enumerable.Range(0, bossNumbers.Length / 3).Select(i => (bossNumbers[i * 3], bossNumbers[i * 3 + 1], bossNumbers[i * 3 + 2])).ToArray();
                var walls = TwentyFirstTowerTable.Numbers(maps.Str(row, "UnmovablePos"));
                _maps[(maps.Int(row, "Id"), maps.Int(row, "MapId"), maps.Int(row, "Diff"))] = new(size[0], size[1],
                    Enumerable.Range(0, walls.Length / 2).Select(i => new[] { walls[i * 2], walls[i * 2 + 1] }).ToArray(),
                    bosses, TwentyFirstTowerTable.Numbers(maps.Str(row, "CafePos")));
            }
        if (SettingTable.Load(Path.Combine(directory, "SpItem.txt")) is { } items)
            foreach (var row in items.Rows)
                _itemSkills[items.Int(row, "ItemId")] = items.Int(row, "SpSkill");
        ctx.MapLuaCall(539, OnChess);
    }

    private static int GridId(MapConfig map, int x, int y) => x + 1 + y * (map.Columns + 1);
    private static int GridCount(MapConfig map) => (map.Columns + 1) * (map.Rows + 1);

    private (int Id, int Map, int Diff) Current(Player player)
    {
        int value = player.GetTask(Group, MapTask);
        return (value & 0x3FF, value >> 10 & 0x3FF, value >> 20 & 0xFFF);
    }

    private Task OnChess(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "nType");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var reply = new JsonObject { ["nType"] = type };
            int error = type switch
            {
                1 => Start(player, param, changes),
                2 => Reset(player, changes),
                3 => MoveTeam(player, param, changes),
                4 => EndPlayerRound(player, changes),
                5 => BossStep(player, param, changes),
                7 => UseItem(player, param, changes),
                _ => 0,
            };
            if (error != 0) reply["nError"] = error;
            // A finished map answers as nType 6: the client refreshes the board and shows the end screen.
            int phase = player.GetTask(Group, BossMove) >>> PhaseShift;
            if (error == 0 && type is 3 or 5 && phase >= PhaseWon) reply["nType"] = 6;
            if (reply["nType"]!.GetValue<int>() is 6 or 8) reply["tbInfo"] = MinRoundInfo(player);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, reply);
        }
        return Task.CompletedTask;
    }

    private JsonObject MinRoundInfo(Player player)
    {
        var info = new JsonObject();
        foreach (var (id, map, diff) in _maps.Keys)
        {
            int best = player.GetTask(Group, MinRound + map + (diff - 1) * MaxMapCount);
            if (best > 0) info[$"{map}_{diff}"] = best;
        }
        return info;
    }

    private int Start(Player player, JsonNode? param, List<KeyValuePair<uint, int>> changes)
    {
        int id = EventKit.Number(param, "id"), mapId = EventKit.Number(param, "mapId"), diff = EventKit.Number(param, "diff");
        int team1 = EventKit.Number(param, "team1"), team2 = EventKit.Number(param, "team2");
        if (!_maps.TryGetValue((id, mapId, diff), out var map)) return ErrInit;
        if (diff > 1 && !EventKit.HasBit(player, Group, MapPass + mapId, diff - 1)) return ErrMapLock;
        if (team1 <= 0 || team2 <= 0 || team1 == team2) return ErrBirth;

        ClearBoard(player, changes);
        EventKit.Write(player, changes, Group, MapTask, id | mapId << 10 | diff << 20);
        foreach (var wall in map.Unmovable) EventKit.Write(player, changes, Group, GridBase + GridId(map, wall[0], wall[1]), Unmovable);
        for (int i = 0; i < map.Bosses.Length; i++)
        {
            EventKit.Write(player, changes, Group, GridBase + GridId(map, map.Bosses[i].X, map.Bosses[i].Y), BossBase + i + 1);
            EventKit.Write(player, changes, Group, BossHpBase + i + 1, BossFullHp);
        }
        if (map.Cafe.Length >= 2) EventKit.Write(player, changes, Group, GridBase + GridId(map, map.Cafe[0], map.Cafe[1]), Cafe);
        EventKit.Write(player, changes, Group, GridBase + team1, Team1);
        EventKit.Write(player, changes, Group, GridBase + team2, Team2);
        int fullTeam = FullHp | FullHp << 8 | FullHp << 16 | FullHp << 24;
        EventKit.Write(player, changes, Group, TeamHp1, fullTeam);
        EventKit.Write(player, changes, Group, TeamHp2, fullTeam);
        EventKit.Write(player, changes, Group, RoundTask, 1);
        EventKit.Write(player, changes, Group, StepTask, StepsPerRound | StepsPerRound << 16);
        return 0;
    }

    private static void ClearBoard(Player player, List<KeyValuePair<uint, int>> changes)
    {
        for (int key = GridBase; key <= GridEnd; key++) EventKit.Write(player, changes, Group, key, 0);
        for (int i = 1; i <= 6; i++) EventKit.Write(player, changes, Group, BossHpBase + i, 0);
        foreach (int key in new[] { MapTask, StepTask, FightCache, TeamHp1, TeamHp2, BossMove, RoundTask, FreezeTask })
            EventKit.Write(player, changes, Group, key, 0);
    }

    private static int Reset(Player player, List<KeyValuePair<uint, int>> changes)
    {
        ClearBoard(player, changes);
        return 0;
    }

    private int Steps(Player player, int team) => team == 1 ? player.GetTask(Group, StepTask) & 0xFFFF : player.GetTask(Group, StepTask) >>> 16;

    private void SetSteps(Player player, int team, int value, List<KeyValuePair<uint, int>> changes)
    {
        int other = team == 1 ? player.GetTask(Group, StepTask) >>> 16 : player.GetTask(Group, StepTask) & 0xFFFF;
        EventKit.Write(player, changes, Group, StepTask, team == 1 ? value | other << 16 : other | value << 16);
    }

    /// <summary>Stepping onto a boss doesn't move the team: it records who fights whom, and the win moves them.</summary>
    private int MoveTeam(Player player, JsonNode? param, List<KeyValuePair<uint, int>> changes)
    {
        int team = EventKit.Number(param, "teamId"), grid = EventKit.Number(param, "gridId");
        if (team is not (1 or 2) || Steps(player, team) < 1 || !_maps.TryGetValue(Current(player), out var map)) return ErrMove;
        int state = player.GetTask(Group, GridBase + grid);
        if (grid < 1 || grid > GridCount(map)) return ErrMove;
        // A refused move must not cost a step, so the step is spent only once the target is known to be legal.
        bool attack = state > BossBase && state <= BossBase + 5;
        if (!attack && state != Empty) return ErrMove;
        SetSteps(player, team, Steps(player, team) - 1, changes);
        if (attack)
        {
            EventKit.Write(player, changes, Group, FightCache, team << 1 | state - BossBase << 6);
            return 0;
        }
        int mine = team == 1 ? Team1 : Team2;
        for (int g = 1; g <= GridCount(map); g++)
            if (player.GetTask(Group, GridBase + g) == mine)
                EventKit.Write(player, changes, Group, GridBase + g, Empty);
        EventKit.Write(player, changes, Group, GridBase + grid, mine);
        return 0;
    }

    private static int EndPlayerRound(Player player, List<KeyValuePair<uint, int>> changes)
    {
        EventKit.Write(player, changes, Group, BossMove, PhaseBoss << PhaseShift);
        return 0;
    }

    /// <summary>With tbMove, one boss steps. Without it every boss is done, so a new player round starts.</summary>
    private int BossStep(Player player, JsonNode? param, List<KeyValuePair<uint, int>> changes)
    {
        if (!_maps.TryGetValue(Current(player), out var map)) return ErrInit;
        if ((param as JsonObject)?["tbMove"] is not JsonObject move)
        {
            EventKit.Write(player, changes, Group, BossMove, 0);
            EventKit.Write(player, changes, Group, FreezeTask, 0);
            EventKit.Write(player, changes, Group, RoundTask, player.GetTask(Group, RoundTask) + 1);
            EventKit.Write(player, changes, Group, StepTask, StepsPerRound | StepsPerRound << 16);
            return 0;
        }
        int boss = EventKit.Number(move, "bossIdx"), from = EventKit.Number(move, "fromGrid"), aim = EventKit.Number(move, "aimGrid");
        int phaseAndMoved = player.GetTask(Group, BossMove) | 1 << boss;
        EventKit.Write(player, changes, Group, BossMove, phaseAndMoved);
        int target = player.GetTask(Group, GridBase + aim);
        if (target == Cafe)
        {
            EventKit.Write(player, changes, Group, BossMove, PhaseLost << PhaseShift);
            return 0;
        }
        if (target is Team1 or Team2)
        {
            // A boss catching a team costs that team the fight it would have had: its HP drops as if it had lost a round.
            EventKit.Write(player, changes, Group, FightCache, 1 | (target == Team1 ? 1 : 2) << 1 | boss << 6);
            return 0;
        }
        EventKit.Write(player, changes, Group, GridBase + from, Empty);
        EventKit.Write(player, changes, Group, GridBase + aim, BossBase + boss);
        return 0;
    }

    private int UseItem(Player player, JsonNode? param, List<KeyValuePair<uint, int>> changes)
    {
        int skill = _itemSkills.GetValueOrDefault(EventKit.Number(param, "itemId"));
        int round = player.GetTask(Group, RoundTask);
        EventKit.Write(player, changes, Group, RoundTask, round + (1 << 16));
        switch (skill)
        {
            case ItemMoveOnce:
            case ItemMoveTwice:
                int team = EventKit.Number(param, "teamIdx");
                if (team is 1 or 2) SetSteps(player, team, Steps(player, team) + skill, changes);
                break;
            case ItemObstacle:
                EventKit.Write(player, changes, Group, GridBase + EventKit.Number(param, "gridId"), Obstacle);
                break;
            case ItemFreeze:
                EventKit.SetBit(player, changes, Group, FreezeTask, EventKit.Number(param, "bossIdx"));
                break;
        }
        return 0;
    }

    // ================================================================ boss fights, called by NamedTowerFeature

    /// <summary>tbTeamState is {hp1, hp2, hp3, shield} in percent, packed into the team's HP task as GetTeamState reads it.</summary>
    internal static void BossWon(Player player, JsonNode? request, List<KeyValuePair<uint, int>> changes)
    {
        int cache = player.GetTask(Group, FightCache);
        int team = cache >> 1 & 0x1F, boss = cache >> 6 & 0x1F;
        if (team is not (1 or 2) || boss == 0) return;
        if ((request as JsonObject)?["tbTeamState"] is JsonArray state && state.Count >= 4)
            EventKit.Write(player, changes, Group, team == 1 ? TeamHp1 : TeamHp2,
                EventKit.NumberAt(state, 0) | EventKit.NumberAt(state, 1) << 8 | EventKit.NumberAt(state, 2) << 16 | EventKit.NumberAt(state, 3) << 24);
        EventKit.Write(player, changes, Group, BossHpBase + boss, 0);
        int mine = team == 1 ? Team1 : Team2;
        bool bossesLeft = false;
        for (int key = GridBase + 1; key <= GridEnd; key++)
        {
            int value = player.GetTask(Group, key);
            if (value == BossBase + boss) EventKit.Write(player, changes, Group, key, mine);
            else if (value == mine) EventKit.Write(player, changes, Group, key, Empty);
            else if (value > BossBase && value <= BossBase + 5) bossesLeft = true;
        }
        EventKit.Write(player, changes, Group, FightCache, 0);
        if (!bossesLeft) MapWon(player, changes);
    }

    /// <summary>bossLeftHp is the percent left on the boss. A lost team is out, with its members at zero.</summary>
    internal static void BossFailed(Player player, JsonNode? request, List<KeyValuePair<uint, int>> changes)
    {
        int cache = player.GetTask(Group, FightCache);
        int team = cache >> 1 & 0x1F, boss = cache >> 6 & 0x1F;
        if (team is not (1 or 2) || boss == 0) return;
        if ((request as JsonObject)?["bossLeftHp"] is JsonValue hp && hp.TryGetValue<double>(out double left))
            EventKit.Write(player, changes, Group, BossHpBase + boss, (int)left);
        EventKit.Write(player, changes, Group, team == 1 ? TeamHp1 : TeamHp2, 0);
        EventKit.Write(player, changes, Group, FightCache, 0);
        int other = team == 1 ? TeamHp2 : TeamHp1;
        if (player.GetTask(Group, other) == 0) EventKit.Write(player, changes, Group, BossMove, PhaseLost << PhaseShift);
    }

    private static void MapWon(Player player, List<KeyValuePair<uint, int>> changes)
    {
        int value = player.GetTask(Group, MapTask);
        int mapId = value >> 10 & 0x3FF, diff = value >> 20 & 0xFFF;
        int rounds = player.GetTask(Group, RoundTask) & 0xFFFF, items = player.GetTask(Group, RoundTask) >>> 16;
        EventKit.Write(player, changes, Group, BossMove, PhaseWon << PhaseShift);
        EventKit.SetBit(player, changes, Group, MapPass + mapId, diff);
        int best = MinRound + mapId + (diff - 1) * MaxMapCount;
        if (player.GetTask(Group, best) == 0 || rounds < player.GetTask(Group, best)) EventKit.Write(player, changes, Group, best, rounds);
        if (diff > 1)
        {
            int score = Math.Max(0, 1000 - 50 * rounds - 20 * items);
            if (score > player.GetTask(Group, MapScore + mapId)) EventKit.Write(player, changes, Group, MapScore + mapId, score);
        }
    }
}
