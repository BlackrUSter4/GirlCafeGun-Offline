using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The club war (10015-10019, 10023-10025, 10032). ClubLogic never shipped, so the rules come from what ClubCommon,
/// UI_ClubBossInfo and UI_ClubBossReward read and from the ClubBoss tables.
///
/// Inferred, because no client code states them:
/// - unknow_score is the level's RemindScore, the score at which a grid opens its neighbours.
/// - A run's score lands only when the exit dialog answers yes (10025), and only then does it use a daily attempt.
/// - A grid's GridAward pays out when the grid reaches its TotalScore.
/// There are no other clubs to rank against, so 10023 replies without a rank and the rank awards are never paid.
/// </summary>
internal sealed class ClubBossHandler
{
    private const int NoError = 0, InValid = 1, NotJoin = 2, LockLevel = 3, Unactive = 4, CloseBoss = 8, EnterTimeOver = 9, GridPassed = 10;
    private const int TaskGroup = ClubFeature.TaskGroup;
    private const int TaskTotalEnters = 44, TaskTodayEnters = 45, TaskLastEnterDay = 46, TaskParticipateBase = 47, TaskGridBase = 148, TaskTodayRed = 553;
    private const int DailyEnters = 3;
    private const int UnlimitedLevelType = 3;

    private sealed record Grid(int Id, int Level, int[] Around, int PreGrid, int[]? Award);
    private sealed record Level(int Id, int Type, int Remind, int Total, int Pass, int Buff, List<int[]> Awards, int PreCost, int PostCost);
    private sealed record Participate(int Id, int Number, List<int[]> Awards);
    private sealed record Stage(int Id, DateTime Show, DateTime Begin, DateTime End, DateTime Clear, Dictionary<int, Grid> Grids, Dictionary<int, Level> Levels, List<Participate> Participates);

    private sealed class Pending
    {
        public int Grid { get; set; }
        public int Level { get; set; }
        public int Score { get; set; }
        public bool Ended { get; set; }
    }

    /// <summary>Stored with the club. A club belongs to one player, so these are that player's numbers too.</summary>
    private sealed class BossState
    {
        public int Stage { get; set; }
        public int ClubScore { get; set; }
        public int MyScore { get; set; }
        public int EnterTimes { get; set; }
        public Dictionary<int, int> Grids { get; set; } = new();
        public List<int> Buffs { get; set; } = new();
        public string PathList { get; set; } = "[]";
        public Pending? Run { get; set; }
    }

    private readonly GameFeatureContext _ctx;
    private readonly ClubFeature _club;
    private readonly List<Stage> _stages = new();

    internal ClubBossHandler(GameFeatureContext ctx, ClubFeature club)
    {
        _ctx = ctx;
        _club = club;
        Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(10015, OnCheckIn);
        ctx.MapLuaCall(10016, OnEnter);
        ctx.MapLuaCall(10017, OnEnd);
        ctx.MapLuaCall(10018, OnParticipateAward);
        ctx.MapLuaCall(10019, OnGridAward);
        ctx.MapLuaCall(10023, (session, cmd, param) => EventKit.Reply(_ctx, session, cmd, new JsonObject()));
        ctx.MapLuaCall(10024, OnFightingPlayer);
        ctx.MapLuaCall(10025, OnSave);
        ctx.MapLuaCall(10032, OnEditWay);
        ctx.OnPlayerPrepare((session, player) => ResetDay(player, new()));
    }

    private void Load(string directory)
    {
        if (SettingTable.Load(Path.Combine(directory, "ClubBossTime.txt")) is not { } times) return;
        var participate = SettingTable.Load(Path.Combine(directory, "ClubParticipateAward.txt"));
        foreach (var row in times.Rows)
        {
            int id = times.Int(row, "StageId");
            if (id <= 0
                || SettingTable.Load(Path.Combine(directory, $"ClubBossMap_{id}.txt")) is not { } map
                || SettingTable.Load(Path.Combine(directory, $"ClubBossLevel_{id}.txt")) is not { } levels) continue;
            var grids = new Dictionary<int, Grid>();
            foreach (var g in map.Rows)
                if (map.Int(g, "LevelId") > 0)
                    grids[map.Int(g, "GridId")] = new(map.Int(g, "GridId"), map.Int(g, "LevelId"), TwentyFirstTowerTable.Numbers(map.Str(g, "AroundIds")),
                        map.Int(g, "PreGridId"), ClubTable.ParseAwardList(map.Str(g, "GridAward")).FirstOrDefault());
            var levelRows = new Dictionary<int, Level>();
            foreach (var l in levels.Rows)
                if (levels.Int(l, "LevelId") > 0)
                {
                    int total = levels.Int(l, "TotalScore");
                    levelRows[levels.Int(l, "LevelId")] = new(levels.Int(l, "LevelId"), levels.Int(l, "LevelType"), levels.Int(l, "RemindScore") is > 0 and var remind ? remind : total,
                        total, levels.Int(l, "PassScore"), levels.Int(l, "BuffID"), ClubTable.ParseAwardList(levels.Str(l, "PassAwards")).ToList(),
                        levels.Int(l, "PreCost"), levels.Int(l, "PostCost"));
                }
            var rows = new List<Participate>();
            if (participate is not null)
                foreach (var p in participate.Rows)
                    if (participate.Int(p, "StageId") == id)
                        rows.Add(new(participate.Int(p, "Id"), participate.Int(p, "Number"), ClubTable.ParseAwardList(participate.Str(p, "Award")).ToList()));
            _stages.Add(new(id, Date(times.Str(row, "ShowTime")), Date(times.Str(row, "BeginTime")), Date(times.Str(row, "EndTime")), Date(times.Str(row, "ClearTime")), grids, levelRows, rows));
        }
        _stages.Sort((a, b) => a.Id.CompareTo(b.Id));
    }

    private static DateTime Date(string text)
        => DateTime.TryParseExact(text, "yyyy-MM-dd-HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : DateTime.MinValue;

    /// <summary>The newest war while every event is open, matching the date patch in GmConsoleFeature.AllEventsOpenLua.</summary>
    private Stage? Current()
    {
        if (_ctx.Options.AllEventsOpen) return _stages.LastOrDefault();
        var now = DateTime.Now;
        return _stages.FirstOrDefault(s => s.Show <= now && now <= s.Clear);
    }

    private bool Fighting(Stage stage)
    {
        if (_ctx.Options.AllEventsOpen) return true;
        var now = DateTime.Now;
        return stage.Begin <= now && now <= stage.End;
    }

    private static BossState Read(ClubState club, Stage stage)
    {
        BossState? state = null;
        try { state = club.BossData?.Deserialize<BossState>(); }
        catch (JsonException) { }
        // A new war starts with a clean map.
        return state is not null && state.Stage == stage.Id ? state : new BossState { Stage = stage.Id };
    }

    private static void Write(ClubState club, BossState state) => club.BossData = JsonSerializer.SerializeToNode(state)!.AsObject();

    /// <summary>Attempts count per 4 AM day. The client only reads the counters, so the server has to clear them.</summary>
    private static void ResetDay(Player player, List<KeyValuePair<uint, int>> changes)
    {
        int today = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        if (player.GetTask(TaskGroup, TaskLastEnterDay) == today) return;
        EventKit.Write(player, changes, TaskGroup, TaskLastEnterDay, today);
        EventKit.Write(player, changes, TaskGroup, TaskTodayEnters, 0);
        EventKit.Write(player, changes, TaskGroup, ClubFeature.TaskTodayExtraGainTimes, 0);
        EventKit.Write(player, changes, TaskGroup, TaskTodayRed, 0);
    }

    // ------------------------------------------------------------ payloads

    /// <summary>Adds the war to a club reply, which also makes the client load the stage's map tables.</summary>
    internal void AddTo(JsonObject payload, ClubState club, Player player)
    {
        if (Current() is not { } stage) return;
        var state = Read(club, stage);
        payload["clubboss"] = BuildBoss(stage, state, player);
        FillMembers(payload["memberlist"] as JsonArray, state, player);
    }

    private JsonObject BuildBoss(Stage stage, BossState state, Player player)
    {
        var list = new JsonArray();
        foreach (var grid in stage.Grids.Values.OrderBy(g => g.Id))
        {
            if (!stage.Levels.TryGetValue(grid.Level, out var level)) continue;
            list.Add(new JsonObject
            {
                ["gridid"] = grid.Id,
                ["levelid"] = grid.Level,
                ["explore_score"] = state.Grids.GetValueOrDefault(grid.Id),
                ["unknow_score"] = level.Remind,
                ["grid_log"] = GridLog(state, grid.Id, player),
            });
        }
        return new JsonObject
        {
            ["stageid"] = stage.Id,
            ["bosslist"] = list,
            ["score"] = state.ClubScore,
            ["attack_path_list"] = JsonNode.Parse(state.PathList),
            ["active_buff_list"] = Ints(state.Buffs),
        };
    }

    private static JsonArray GridLog(BossState state, int grid, Player player)
    {
        int score = state.Grids.GetValueOrDefault(grid);
        return score > 0 ? new JsonArray(new JsonObject { ["name"] = player.Name, ["score"] = score }) : new JsonArray();
    }

    private static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());

    private static void FillMembers(JsonArray? members, BossState state, Player player)
    {
        if (members is null) return;
        int today = player.GetTask(TaskGroup, TaskTodayEnters);
        foreach (var member in members.OfType<JsonObject>())
            if (member["roleid"]?.GetValue<ulong>() == player.Roleid)
            {
                member["boss_explore_score"] = state.MyScore;
                member["clubboss_enter_times"] = state.EnterTimes;
                member["today_clubboss_enter_times"] = today;
            }
    }

    private JsonArray Members(ClubState club, BossState state, Player player)
    {
        var members = new JsonArray();
        foreach (var member in club.Members) members.Add(ClubFeature.BuildMemberPayload(member));
        FillMembers(members, state, player);
        return members;
    }

    // ------------------------------------------------------------ commands

    private Task OnCheckIn(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var club = ClubBag.Read(player).Club;
            if (club is null) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = NotJoin });
            if (Current() is not { } stage) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = InValid });
            var changes = new List<KeyValuePair<uint, int>>();
            ResetDay(player, changes);
            var state = Read(club, stage);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject
            {
                ["nError"] = NoError,
                ["clubboss"] = BuildBoss(stage, state, player),
                ["memberlist"] = Members(club, state, player),
            });
        }
        return Task.CompletedTask;
    }

    /// <summary>ClubCommon:IsGridUnlock, kept in step because the client trusts this answer for what it shows next.</summary>
    private static bool Unlocked(Stage stage, BossState state, Grid grid)
    {
        bool Reached(int id) => stage.Grids.TryGetValue(id, out var other) && stage.Levels.TryGetValue(other.Level, out var level)
            && state.Grids.GetValueOrDefault(id) >= level.Remind;
        if (grid.Around.Length > 0 && !grid.Around.Any(Reached)) return false;
        return grid.PreGrid <= 0 || !stage.Grids.ContainsKey(grid.PreGrid) || Reached(grid.PreGrid);
    }

    private Task OnEnter(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int gridId = EventKit.NumberAt(param, 0), levelId = EventKit.NumberAt(param, 1);
        lock (player)
        {
            var bag = ClubBag.Read(player);
            int error = TryEnter(player, bag.Club, gridId, levelId);
            if (error != NoError) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = error });
            bag.Write(player);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = NoError });
        }
        return Task.CompletedTask;
    }

    private int TryEnter(Player player, ClubState? club, int gridId, int levelId)
    {
        if (club is null) return NotJoin;
        if (Current() is not { } stage || !Fighting(stage)) return CloseBoss;
        if (!stage.Grids.TryGetValue(gridId, out var grid) || grid.Level != levelId || !stage.Levels.TryGetValue(levelId, out var level)) return InValid;
        var state = Read(club, stage);
        if (!Unlocked(stage, state, grid)) return LockLevel;
        if (level.Type < UnlimitedLevelType && state.Grids.GetValueOrDefault(gridId) >= level.Total) return GridPassed;
        int allowed = DailyEnters + player.GetTask(TaskGroup, ClubFeature.TaskTodayExtraGainTimes);
        if (player.GetTask(TaskGroup, TaskTodayEnters) >= allowed) return EnterTimeOver;
        state.Run = new Pending { Grid = gridId, Level = levelId };
        Write(club, state);
        return NoError;
    }

    /// <summary>Holds the score until the exit dialog answers. A pause exit just abandons the run.</summary>
    private Task OnEnd(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var bag = ClubBag.Read(player);
            if (bag.Club is not { } club) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = NotJoin });
            if (Current() is not { } stage) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = CloseBoss });
            var state = Read(club, stage);
            bool pauseExit = param?["bPauseExit"]?.GetValue<bool>() == true;
            if (pauseExit)
            {
                state.Run = null;
                Write(club, state);
                bag.Write(player);
                _ctx.Store.Save(player);
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = InValid });
            }
            int gridId = EventKit.Number(param, "nGridId");
            if (state.Run is not { Ended: false } run || run.Grid != gridId || run.Level != EventKit.Number(param, "nLevelId")
                || !stage.Levels.TryGetValue(run.Level, out var level))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = InValid });
            bool pass = param?["bPass"]?.GetValue<bool>() == true;
            // The client's formula tops out at PassScore, so anything above it did not come from a real run.
            run.Score = Math.Clamp(EventKit.Number(param, "nScore"), 0, level.Pass);
            run.Ended = true;
            Write(club, state);
            bag.Write(player);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
            {
                ["bPass"] = pass,
                ["nScore"] = run.Score,
                ["nTotalScore"] = state.MyScore,
                ["nGridScore"] = state.Grids.GetValueOrDefault(gridId) + run.Score,
            });
        }
        return Task.CompletedTask;
    }

    private Task OnSave(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        bool keep = param is JsonValue value && value.TryGetValue<bool>(out bool answer) && answer;
        lock (player)
        {
            var bag = ClubBag.Read(player);
            if (bag.Club is not { } club || Current() is not { } stage)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject());
            var state = Read(club, stage);
            var changes = new List<KeyValuePair<uint, int>>();
            if (keep && state.Run is { Ended: true } run && stage.Grids.TryGetValue(run.Grid, out var grid) && stage.Levels.TryGetValue(run.Level, out var level))
                Bank(player, club, state, grid, level, run.Score, changes);
            state.Run = null;
            Write(club, state);
            bag.Write(player);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject
            {
                ["clubboss"] = BuildBoss(stage, state, player),
                ["memberlist"] = Members(club, state, player),
                ["myScore"] = state.MyScore,
                ["nEnterTimes"] = state.EnterTimes,
            });
        }
        return Task.CompletedTask;
    }

    private void Bank(Player player, ClubState club, BossState state, Grid grid, Level level, int score, List<KeyValuePair<uint, int>> changes)
    {
        int before = state.Grids.GetValueOrDefault(grid.Id);
        int after = level.Type < UnlimitedLevelType ? Math.Min(level.Total, before + score) : before + score;
        state.Grids[grid.Id] = after;
        state.ClubScore += after - before;
        state.MyScore += score;
        state.EnterTimes++;
        EventKit.Write(player, changes, TaskGroup, TaskTotalEnters, state.EnterTimes);
        EventKit.Write(player, changes, TaskGroup, TaskTodayEnters, player.GetTask(TaskGroup, TaskTodayEnters) + 1);
        if (before >= level.Total || after < level.Total) return;
        if (level.Buff > 0 && !state.Buffs.Contains(level.Buff)) state.Buffs.Add(level.Buff);
        if (grid.Award is not null) _club.PayClubBossAward(player, club, new[] { grid.Award }, changes);
    }

    private Task OnFightingPlayer(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int gridId = EventKit.Number(param, "nGridId");
        lock (player)
        {
            if (ClubBag.Read(player).Club is not { } club || Current() is not { } stage) return Task.CompletedTask;
            var state = Read(club, stage);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
            {
                ["gridid"] = gridId,
                ["explore_score"] = state.Grids.GetValueOrDefault(gridId),
                ["grid_log"] = GridLog(state, gridId, player),
                ["sumScore"] = state.ClubScore,
                ["active_buff_list"] = Ints(state.Buffs),
                ["roleidlist"] = new JsonArray(),
            });
        }
        return Task.CompletedTask;
    }

    private Task OnEditWay(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var bag = ClubBag.Read(player);
            if (bag.Club is not { } club || Current() is not { } stage || param?["attack_path_list"] is not JsonArray paths)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = InValid });
            var state = Read(club, stage);
            state.PathList = paths.ToJsonString();
            Write(club, state);
            bag.Write(player);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["attack_path_list"] = paths.DeepClone() });
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------ awards

    private Task OnParticipateAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var bag = ClubBag.Read(player);
            if (bag.Club is not { } club || Current() is not { } stage)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = NotJoin });
            var state = Read(club, stage);
            var changes = new List<KeyValuePair<uint, int>>();
            var awards = new List<int[]>();
            foreach (var row in stage.Participates)
            {
                if (state.EnterTimes < row.Number || player.GetTask(TaskGroup, TaskParticipateBase + row.Id) != 0) continue;
                EventKit.Write(player, changes, TaskGroup, TaskParticipateBase + row.Id, 1);
                awards.AddRange(row.Awards);
            }
            if (awards.Count == 0) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = Unactive });
            var rewards = _club.PayClubBossAward(player, club, awards, changes);
            bag.Write(player);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["nError"] = NoError, ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnGridAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int only = EventKit.Number(param, "nGridId");
        lock (player)
        {
            var bag = ClubBag.Read(player);
            if (bag.Club is not { } club || Current() is not { } stage)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = NotJoin });
            var state = Read(club, stage);
            var changes = new List<KeyValuePair<uint, int>>();
            var awards = new List<int[]>();
            var paid = new List<int>();
            foreach (var grid in stage.Grids.Values)
            {
                if (only > 0 && grid.Id != only) continue;
                if (!stage.Levels.TryGetValue(grid.Level, out var level) || level.Awards.Count == 0) continue;
                if (state.Grids.GetValueOrDefault(grid.Id) < level.Total || player.GetTask(TaskGroup, TaskGridBase + level.Id) != 0) continue;
                // Flagged by level: the client reads it that way, and a level on two grids pays once.
                EventKit.Write(player, changes, TaskGroup, TaskGridBase + level.Id, 1);
                awards.AddRange(level.Awards);
                paid.Add(grid.Id);
            }
            if (paid.Count == 0) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = Unactive });
            var rewards = _club.PayClubBossAward(player, club, awards, changes);
            bag.Write(player);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject
            {
                ["nError"] = NoError,
                ["tbAward"] = EventKit.Tuples(rewards),
                ["tbGrid"] = Ints(paid),
            });
        }
        return Task.CompletedTask;
    }
}
