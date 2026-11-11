using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Remake activity (360-366): two teams walk an 8x8 board and clear what stands on it, ending with a boss.
///
/// INFERRED: the original server generated the boards, and the client ships only the piece list (ItemPrefabPath.txt).
/// So layouts here are placed at random: a boss in the far corner, four fights, and four pickups chosen from the pieces
/// whose level matches the difficulty. Rewards use the activity row's FixAward, and clearing a boss opens the next
/// difficulty. The reply shapes and piece effects are the client's.
/// </summary>
public sealed class RemakeActivityFeature : IGameFeature
{
    private const int GridSize = 8, Fights = 4, Pickups = 4;
    // ItemPrefabPath Type
    private const int TypeTeam = 1, TypeBoss = 2, TypeFight = 3, TypePickup = 4;
    private const int TeamPiece = 999;
    private const int FullEnergy = 100;
    // RemakeError text keys
    private const int ErrNoAct = 1, ErrNoDiff = 2, ErrNoVigour = 3, ErrBadMove = 4;
    private const string StateKey = "remakeRun";

    private sealed record Piece(int Id, int Type, int Param, string Function, List<int[]> Box);
    private sealed record ActRow(int Vigour, int MasterExp, List<int[]> FixAward, List<int[]> FirstAward);

    private sealed class Run
    {
        public int Act { get; set; }
        public int Diff { get; set; }
        public int DiffMax { get; set; } = 1;
        public List<Obj> Objects { get; set; } = new();
        public int PendingPiece { get; set; }
        public HashSet<int> FirstCleared { get; set; } = new();
    }

    private sealed class Obj
    {
        public int Id { get; set; }
        public int Type { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public int Team { get; set; }
        public List<int> CurHp { get; set; } = new();
        public List<int> MaxHp { get; set; } = new();
        public List<int> CurMp { get; set; } = new();
        public List<int> MaxMp { get; set; } = new();
    }

    private GameFeatureContext _ctx = null!;
    private readonly List<Piece> _pieces = new();
    private readonly Dictionary<(int Act, int Diff), ActRow> _acts = new();
    private readonly Dictionary<int, int> _levelDiffs = new();
    private readonly List<int> _openActs = new();

    public string Name => "Remake activity (360-366)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "ItemPrefabPath.txt")) is { } pieces)
            foreach (var row in pieces.Rows)
            {
                int id = pieces.Int(row, "Id");
                if (id <= 0) continue;
                var numbers = TwentyFirstTowerTable.Numbers(pieces.Str(row, "Paras"));
                var box = pieces.Str(row, "FuncName") == "Box"
                    ? numbers.Chunk(6).Where(c => c.Length == 6).Select(c => c).ToList() : new List<int[]>();
                _pieces.Add(new(id, pieces.Int(row, "Type"), numbers.Length > 0 ? numbers[0] : 0, pieces.Str(row, "FuncName"), box));
            }
        if (SettingTable.Load(Path.Combine(directory, "Remake.txt")) is { } acts)
            foreach (var row in acts.Rows)
                if (acts.Int(row, "Id") > 0)
                    _acts[(acts.Int(row, "Id"), acts.Int(row, "Diff"))] = new(acts.Int(row, "Vigour"), acts.Int(row, "MasterExp"),
                        ChapterTable.ParseTuples(acts.Str(row, "FixAward")), ChapterTable.ParseTuples(acts.Str(row, "FirstAward")));
        if (SettingTable.Load(Path.Combine(directory, "_scoped", "activities", "remakeactivity", "levels.txt")) is { } levels)
            foreach (var row in levels.Rows)
                _levelDiffs[levels.Int(row, "Id")] = levels.Int(row, "Diff");
        if (SettingTable.Load(Path.Combine(directory, "OpenTime.txt")) is { } open)
            foreach (var row in open.Rows)
                if (open.Int(row, "Id") > 0 && _acts.Keys.Any(k => k.Act == open.Int(row, "Id")))
                    _openActs.Add(open.Int(row, "Id"));

        ctx.MapLuaCall(360, OnInfo);
        ctx.MapLuaCall(361, OnInfo);
        ctx.MapLuaCall(362, OnMove);
        ctx.MapLuaCall(363, OnFightEnd);
        ctx.MapLuaCall(364, OnQuit);
        ctx.MapLuaCall(365, OnSelectDiff);
        ctx.MapLuaCall(366, OnMissionAward);
    }

    private int CurrentAct()
    {
        if (_openActs.Count == 0) return 0;
        long week = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (7 * 86400);
        return _openActs[(int)(week % _openActs.Count)];
    }

    private Run Read(Player player)
    {
        Run? run = null;
        try { run = player.Extra.GetValueOrDefault(StateKey)?.Deserialize<Run>(); }
        catch (JsonException) { }
        run ??= new Run();
        if (run.Act == 0 || run.Diff == 0) run.Act = CurrentAct();
        return run;
    }

    private static void Save(Player player, Run run) => player.Extra[StateKey] = JsonSerializer.SerializeToNode(run);

    private static JsonObject Act(Run run) => new()
    {
        ["nActId"] = run.Act, ["nDiff"] = run.Diff, ["nDiffMax"] = run.DiffMax, ["nWave"] = 1, ["nMaxWave"] = 1,
    };

    private static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static JsonArray Objects(Run run)
    {
        var list = new JsonArray();
        foreach (var o in run.Objects)
        {
            var node = new JsonObject { ["nId"] = o.Id, ["nType"] = o.Type, ["tbPos"] = new JsonArray(o.X, o.Y) };
            if (o.Team > 0)
            {
                node["nTeamId"] = o.Team;
                node["nEnery"] = FullEnergy;
                if (o.MaxHp.Count > 0)
                {
                    node["tbCurHp"] = Ints(o.CurHp);
                    node["tbMaxHp"] = Ints(o.MaxHp);
                }
                if (o.MaxMp.Count > 0)
                {
                    node["tbCurMp"] = Ints(o.CurMp);
                    node["tbMaxMp"] = Ints(o.MaxMp);
                }
            }
            list.Add(node);
        }
        return list;
    }

    private Task OnInfo(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var run = Read(player);
            var reply = new JsonObject { ["nErr"] = run.Act == 0 ? ErrNoAct : 0, ["tbAct"] = Act(run) };
            if (run.Diff > 0) reply["tbObj"] = Objects(run);
            return EventKit.Reply(_ctx, session, cmd, reply);
        }
    }

    private void BuildBoard(Run run)
    {
        var free = new List<(int X, int Y)>();
        for (int x = 1; x <= GridSize; x++)
            for (int y = 1; y <= GridSize; y++)
                free.Add((x, y));
        void Place(int id, int type, int x, int y, int team = 0)
        {
            run.Objects.Add(new Obj { Id = id, Type = type, X = x, Y = y, Team = team });
            free.Remove((x, y));
        }
        (int X, int Y) TakeRandom()
        {
            var spot = free[Random.Shared.Next(free.Count)];
            free.Remove(spot);
            return spot;
        }
        run.Objects.Clear();
        Place(TeamPiece, TypeTeam, 1, 1, 1);
        Place(TeamPiece, TypeTeam, 1, 2, 2);
        bool ForDiff(Piece p) => _levelDiffs.GetValueOrDefault(p.Param) == run.Diff;
        var boss = _pieces.FirstOrDefault(p => p.Type == TypeBoss && ForDiff(p)) ?? _pieces.FirstOrDefault(p => p.Type == TypeBoss);
        if (boss is not null) Place(boss.Id, TypeBoss, GridSize, GridSize);
        var fights = _pieces.Where(p => p.Type == TypeFight && ForDiff(p)).ToList();
        if (fights.Count == 0) fights = _pieces.Where(p => p.Type == TypeFight).ToList();
        for (int i = 0; i < Fights && fights.Count > 0; i++)
        {
            var spot = TakeRandom();
            run.Objects.Add(new Obj { Id = fights[Random.Shared.Next(fights.Count)].Id, Type = TypeFight, X = spot.X, Y = spot.Y });
        }
        var pickups = _pieces.Where(p => p.Type == TypePickup).ToList();
        for (int i = 0; i < Pickups && pickups.Count > 0; i++)
        {
            var spot = TakeRandom();
            run.Objects.Add(new Obj { Id = pickups[Random.Shared.Next(pickups.Count)].Id, Type = TypePickup, X = spot.X, Y = spot.Y });
        }
    }

    private Task OnSelectDiff(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int diff = EventKit.Number(param, "nDiff");
        lock (player)
        {
            var run = Read(player);
            if (!_acts.TryGetValue((run.Act, diff), out var act) || diff > run.DiffMax)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nErr"] = ErrNoDiff });
            if (_ctx.Options.SpendVigour && act.Vigour > 0)
            {
                if (!player.TrySpendVigour(act.Vigour)) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nErr"] = ErrNoVigour });
                GameNotify.PushMoney(session, player, MoneyType.Vigour);
            }
            run.Diff = diff;
            run.PendingPiece = 0;
            BuildBoard(run);
            Save(player, run);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nErr"] = 0, ["tbAct"] = Act(run), ["tbObj"] = Objects(run), ["tbItem"] = new JsonArray() });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Landing on a pickup uses it up, paying a Box's rewards. Landing on a fight marks it as pending, and the client's
    /// own BeginFight loads the battle when the walk ends.
    /// </summary>
    private Task OnMove(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var request = param as JsonObject;
        int team = EventKit.Number(param, "nTeamId");
        var pos = ChallengeTowerFeature.ReadPositional(request?["tbPos"]);
        lock (player)
        {
            var run = Read(player);
            var mover = run.Objects.FirstOrDefault(o => o.Team == team);
            if (mover is null || pos.Count < 2 || pos[0] is < 1 or > GridSize || pos[1] is < 1 or > GridSize)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nErr"] = ErrBadMove });
            if (request?["tbState"] is JsonObject state)
            {
                var maxHp = ChallengeTowerFeature.ReadPositional(state["tbMaxHp"]);
                if (maxHp.Count > 0 && mover.MaxHp.Count == 0)
                {
                    mover.MaxHp = maxHp;
                    mover.CurHp = maxHp.ToList();
                }
                var maxMp = ChallengeTowerFeature.ReadPositional(state["tbMaxMp"]);
                if (maxMp.Count > 0 && mover.MaxMp.Count == 0)
                {
                    mover.MaxMp = maxMp;
                    mover.CurMp = maxMp.ToList();
                }
            }
            mover.X = pos[0];
            mover.Y = pos[1];
            var rewards = new RewardGrantResult();
            var target = run.Objects.FirstOrDefault(o => o != mover && o.Team == 0 && o.X == pos[0] && o.Y == pos[1]);
            if (target is { Type: TypePickup })
            {
                var piece = _pieces.FirstOrDefault(p => p.Id == target.Id);
                if (piece is { Box.Count: > 0 })
                {
                    var box = piece.Box[EventKit.WeightedPick(piece.Box.Select(b => b[5]).ToArray(), _ => true)];
                    _ctx.Rewards.Grant(player, new[] { box.Take(5).ToArray() }, RewardSource.Extra, rewards);
                }
                run.Objects.Remove(target);
            }
            else if (target is not null) run.PendingPiece = target.Id;
            Save(player, run);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, new JsonObject
            {
                ["nErr"] = 0, ["tbObj"] = Objects(run), ["tbItem"] = EventKit.Tuples(rewards),
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>A win clears the piece the team stands on. tbAward present means victory on the client's result screen.</summary>
    private Task OnFightEnd(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var request = param as JsonObject;
        int team = EventKit.Number(param, "nTeamId");
        bool won = EventKit.Number(param, "nRet") == 1;
        lock (player)
        {
            var run = Read(player);
            var mover = run.Objects.FirstOrDefault(o => o.Team == team);
            if (mover is null) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nErr"] = ErrBadMove });
            if (request?["tbState"] is JsonObject state)
            {
                var hp = ChallengeTowerFeature.ReadPositional(state["tbCurHp"]);
                if (hp.Count > 0) mover.CurHp = hp;
                var mp = ChallengeTowerFeature.ReadPositional(state["tbCurMp"]);
                if (mp.Count > 0) mover.CurMp = mp;
            }
            var reply = new JsonObject { ["nErr"] = 0 };
            RewardGrantResult? rewards = null;
            int masterExp = 0;
            if (won)
            {
                rewards = new RewardGrantResult();
                var target = run.Objects.FirstOrDefault(o => o != mover && o.Team == 0 && o.X == mover.X && o.Y == mover.Y);
                if (target is not null) run.Objects.Remove(target);
                if (_acts.TryGetValue((run.Act, run.Diff), out var act))
                {
                    if (run.FirstCleared.Add(run.Diff)) _ctx.Rewards.Grant(player, act.FirstAward, RewardSource.FirstAward, rewards);
                    _ctx.Rewards.Grant(player, act.FixAward, RewardSource.FixedAward, rewards);
                    masterExp = act.MasterExp;
                    if (masterExp > 0) PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, masterExp);
                }
                if (target is { Type: TypeBoss } && _acts.ContainsKey((run.Act, run.Diff + 1)) && run.DiffMax == run.Diff)
                    run.DiffMax = run.Diff + 1;
                reply["tbAward"] = EventKit.Tuples(rewards);
                reply["nMasterExp"] = masterExp;
                reply["nCardExp"] = 0;
            }
            reply["tbObj"] = Objects(run);
            reply["tbAct"] = Act(run);
            run.PendingPiece = 0;
            Save(player, run);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, reply);
            if (masterExp > 0) GameNotify.PushLevelExp(session, player);
        }
        return Task.CompletedTask;
    }

    private Task OnQuit(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var run = Read(player);
            run.Diff = 0;
            run.Objects.Clear();
            Save(player, run);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nErr"] = 0, ["tbAct"] = Act(run) });
        }
        return Task.CompletedTask;
    }

    private Task OnMissionAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId");
        lock (player)
        {
            var mission = _ctx.Missions.GetAchieveOther(id);
            int value = mission is null ? 0 : player.GetTask(mission.Group, id);
            if (mission is null || MissionFeature.ClaimedOf(value) == 1 || MissionFeature.ProgressOf(value) < mission.RequiredCount(true))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nErr"] = ErrNoAct });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, mission.Group, id, 0);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["nErr"] = 0, ["Reward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }
}
