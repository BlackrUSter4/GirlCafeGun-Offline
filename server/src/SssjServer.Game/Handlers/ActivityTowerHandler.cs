using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Every event tower that shares Activities_EnterTower/PassTower/FailTower (54/55/56), plus the node towers'
/// point rewards and lottery (176/177/178) and the swimwear score awards (61/62/63).
///
/// The classes differ only in where they record progress, so ActivityTowerTable holds that as data taken from each
/// *Logic.lua. Level prerequisites stay on the client: CanEnterSmallTower already gates the UI and a private server
/// gains nothing from refusing what the player's own client allowed.
///
/// The EN client carries no battle token, so a settlement is matched to its entry by tower and level. A settlement
/// with no open entry replays the stored reply, which keeps a reconnect from paying rewards twice.
/// </summary>
internal sealed class ActivityTowerHandler
{
    internal const int CmdSwimwearServerScore = 61;
    internal const int CmdSwimwearScoreAward = 62;
    internal const int CmdSwimwearServerAward = 63;
    internal const int CmdPointReward = 176;
    internal const int CmdPondReward = 177;
    internal const int CmdPondReset = 178;

    // ActivitiesLogic.tbErrorCode
    private const int ErrInvalid = 2, ErrVigour = 5, ErrPoint = 8, ErrPondEmpty = 9, ErrFormation = 10, ErrNoAward = 12, ErrGotAward = 13;

    // Node family offsets, relative to the class's nTaskOffset (NodeTowerLogic.lua:129-141).
    private const int NodeScore = 4, PondId = 5, PondResets = 6, PointAward = 20, NodePass = 50, SpScore = 150, PondRecord = 200, SpPassCount = 450;
    private const int PondSize = 20;
    // SwimwearLogic.lua:105-110, relative to the class's offset.
    private const int ServerAwardFlag = 150, ScoreAwardFlag = 200;

    private const string AttemptKey = "activityTowerRun";

    private sealed class Attempt
    {
        public int Tower { get; set; }
        public int Level { get; set; }
        public bool Active { get; set; }
        public int ResultCmd { get; set; }
        public JsonObject? Reply { get; set; }
        public BattleMainCard[] Cards { get; set; } = Array.Empty<BattleMainCard>();
        public Dictionary<int, int> PledgeSteps { get; set; } = new();
    }

    private readonly GameFeatureContext _ctx;
    private readonly ActivityTowerTable _table;
    private readonly PledgeTable _pledge;
    private readonly AnimalizeHandler _cardTowers;

    internal ActivityTowerHandler(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        _table = ActivityTowerTable.Load(directory);
        _pledge = PledgeTable.Load(directory);
        _cardTowers = new AnimalizeHandler(ctx, directory, _pledge);
        ctx.MapLuaCall(CmdPointReward, PointReward);
        ctx.MapLuaCall(CmdPondReward, PondDraw);
        ctx.MapLuaCall(CmdPondReset, PondReset);
        ctx.MapLuaCall(CmdSwimwearServerScore, SwimwearServerScore);
        ctx.MapLuaCall(CmdSwimwearScoreAward, (s, c, p) => ScoreAward(s, c, p, _table.ScoreAwards, ScoreAwardFlag));
        ctx.MapLuaCall(CmdSwimwearServerAward, (s, c, p) => ScoreAward(s, c, p, _table.ServerScoreAwards, ServerAwardFlag));
    }

    internal Task Enter(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var args = ChallengeTowerFeature.ReadPositional(param);
        if (args.Count > 1 && _cardTowers.Owns(args[1])) return _cardTowers.Enter(session, cmd, param);
        if (args.Count < 5 || !_table.Levels.TryGetValue((args[1], args[3]), out var level))
            return Error(session, cmd, ErrInvalid);
        lock (player)
        {
            var cards = Array.Empty<BattleMainCard>();
            if (level.Type != 2)
            {
                var guids = ChapterHandler.SnapshotMainCards(player, new JsonObject { ["nFormationId"] = args[4] }, out bool valid);
                if (!valid || guids.Length == 0) return Error(session, cmd, ErrFormation);
                cards = guids.Select(id => BattleMainCard.From(player.Items.First(i => i.Guid == id))).ToArray();
            }
            // A resent request while the same fight is open must not spend stamina twice.
            var open = ReadAttempt(player);
            if (open is not { Active: true } || open.Tower != level.Tower || open.Level != level.Id)
            {
                if (_ctx.Options.SpendVigour && level.Cost > 0)
                {
                    if (!player.TrySpendVigour(level.Cost)) return Error(session, cmd, ErrVigour);
                    GameNotify.PushMoney(session, player, MoneyType.Vigour);
                }
                SaveAttempt(player, new Attempt
                {
                    Tower = level.Tower, Level = level.Id, Active = true, Cards = cards,
                    PledgeSteps = PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(g => g.Key, g => g.Value.Step),
                });
                _ctx.Store.Save(player);
            }
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
            {
                ["tbData"] = new JsonArray(args[0], level.Tower, args[2], level.Id),
                ["tbDrop"] = new JsonArray(),
                ["tbDropItems"] = new JsonArray(),
            });
        }
        return Task.CompletedTask;
    }

    internal Task Pass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var args = ChallengeTowerFeature.ReadPositional(param);
        if (args.Count > 1 && _cardTowers.Owns(args[1])) return _cardTowers.Pass(session, cmd, param);
        if (args.Count < 4 || !_table.Levels.TryGetValue((args[1], args[3]), out var level))
            return Error(session, cmd, ErrInvalid);
        lock (player)
        {
            var attempt = ReadAttempt(player);
            if (attempt is null || attempt.Tower != level.Tower || attempt.Level != level.Id)
                return Error(session, cmd, ErrInvalid);
            if (!attempt.Active)
            {
                if (attempt.ResultCmd != cmd || attempt.Reply is null) return Error(session, cmd, ErrInvalid);
                _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply.DeepClone());
                return Task.CompletedTask;
            }

            var cls = _table.Towers[level.Tower];
            var report = param is JsonArray array && array.Count > 5 ? array[5] : null;
            int stars = args.Count > 6 ? args[6] : 0;
            var changed = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();

            int key = LevelTask(cls, level);
            int before = player.GetTask(cls.Group, key);
            if (before <= 0) _ctx.Rewards.Grant(player, level.First, RewardSource.FirstAward, rewards);
            _ctx.Rewards.Grant(player, level.Fixed, RewardSource.FixedAward, rewards);

            int score = ScoreOf(report);
            Write(player, changed, cls.Group, key, cls.Progress switch
            {
                TowerProgress.Score => Math.Max(before, Math.Max(1, score)),
                TowerProgress.PassCount => before + 1,
                _ => Math.Max(before, Math.Max(1, stars)),
            });
            if (cls.Progress == TowerProgress.Stars) RecordNodeProgress(player, cls, level, score, changed);

            var newGoals = new JsonArray();
            if (level.Type == 3 && cls.GoalBase > 0) CompleteGoals(player, args[0], cls, level, report, rewards, changed, newGoals);
            if (cls.ScoreTask > 0) CreditPersonalScore(player, cls, level.Tower, rewards, changed);
            EventMissionProgress.RecordTowerClear(player, level.Tower, args[0], level.Id, level.Type);
            if (level.Type != 2)
                changed.AddRange(ChapterCombatProgress.RecordTowerVictory(_ctx.Missions, _pledge, player, attempt.Cards, attempt.PledgeSteps));
            if (level.MasterExp > 0) PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, level.MasterExp);

            attempt.Active = false;
            attempt.ResultCmd = cmd;
            attempt.Reply = new JsonObject
            {
                ["tbData"] = Tuples(rewards),
                ["nMasterExp"] = level.MasterExp,
                ["nCardExp"] = 0,
                ["newGoalIds"] = newGoals,
                ["nGetScore"] = score,
                ["nCardAddScore"] = 0,
                ["nPetAddScore"] = 0,
            };
            SaveAttempt(player, attempt);
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            if (level.MasterExp > 0) GameNotify.PushLevelExp(session, player);
            _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply.DeepClone());
        }
        return Task.CompletedTask;
    }

    /// <summary>The client shows defeat on its own and ignores this reply. Stamina stays spent, as in mainline.</summary>
    internal Task Fail(ClientSession session, int cmd, JsonNode? param)
    {
        var args = ChallengeTowerFeature.ReadPositional(param);
        if (args.Count > 1 && _cardTowers.Owns(args[1])) return _cardTowers.Fail(session, cmd, param);
        if (session.Tag is Player player)
            lock (player)
            {
                var attempt = ReadAttempt(player);
                if (attempt is { Active: true })
                {
                    attempt.Active = false;
                    attempt.ResultCmd = cmd;
                    attempt.Reply = new JsonObject();
                    SaveAttempt(player, attempt);
                    _ctx.Store.Save(player);
                }
            }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    private int LevelTask(TowerClass cls, ActivityTowerLevel level)
        => _table.VariegatedIndex.TryGetValue((level.Tower, level.Id), out int slot)
            ? cls.LevelBase + slot : cls.LevelBase + level.Id;

    /// <summary>Combat reports nScore inside a table. Story levels report a bare true, and some report nothing.</summary>
    private static int ScoreOf(JsonNode? report)
        => report is JsonObject table && table["nScore"] is JsonValue value && value.TryGetValue<double>(out double score)
            ? (int)score : 0;

    /// <summary>
    /// ACT levels add to the tower's running score and SP levels keep their best; both feed the point rewards.
    /// A node reads as complete once every level in it has been cleared.
    /// </summary>
    private void RecordNodeProgress(Player player, TowerClass cls, ActivityTowerLevel level, int score, List<KeyValuePair<uint, int>> changed)
    {
        int offset = cls.Offset;
        if (level.Type == 0 && score > 0)
            Write(player, changed, cls.Group, offset + NodeScore, player.GetTask(cls.Group, offset + NodeScore) + score);
        if (level.Type == 1)
        {
            Write(player, changed, cls.Group, offset + SpScore + level.Id, Math.Max(player.GetTask(cls.Group, offset + SpScore + level.Id), score));
            Write(player, changed, cls.Group, offset + SpPassCount + level.Id, player.GetTask(cls.Group, offset + SpPassCount + level.Id) + 1);
        }
        if (level.Node <= 0) return;
        bool complete = _table.Levels.Values
            .Where(l => l.Tower == level.Tower && l.Node == level.Node)
            .All(l => player.GetTask(cls.Group, cls.LevelBase + l.Id) > 0);
        if (complete && player.GetTask(cls.Group, offset + NodePass + level.Node) == 0)
            Write(player, changed, cls.Group, offset + NodePass + level.Node, 1);
    }

    /// <summary>
    /// UI_MiniTowerMission: the level score is the sum of reached goals' points, and every threshold at or below it
    /// shows as received, so crossing a threshold pays it here. No separate claim command exists for these.
    /// </summary>
    private void CompleteGoals(Player player, int activityType, TowerClass cls, ActivityTowerLevel level, JsonNode? report,
        RewardGrantResult rewards, List<KeyValuePair<uint, int>> changed, JsonArray newGoals)
    {
        if (!_table.Goals.TryGetValue((level.Tower, level.Id), out var goals)) return;
        int before = player.GetTask(cls.Group, cls.GoalBase + level.Id);
        foreach (var goal in goals)
        {
            var node = report is JsonObject table ? table[goal.Id.ToString()]
                : report is JsonArray list && goal.Id > 0 && goal.Id <= list.Count ? list[goal.Id - 1] : null;
            if (node is not JsonValue value || !value.TryGetValue<bool>(out bool reached) || !reached) continue;
            int flag = cls.GoalBase + level.Id * 100 + goal.Id;
            if (player.GetTask(cls.Group, flag) > 0) continue;
            Write(player, changed, cls.Group, flag, 1);
            newGoals.Add(goal.Id);
        }
        int score = Math.Max(before, goals
            .Where(g => player.GetTask(cls.Group, cls.GoalBase + level.Id * 100 + g.Id) > 0).Sum(g => g.Points));
        Write(player, changed, cls.Group, cls.GoalBase + level.Id, score);
        // Mission conditions 99 and 108 total the SP evaluation points earned per activity type.
        foreach (string key in new[] { "99", "108" })
            EventMissionProgress.Bump(player, $"{key}:{activityType}", score - before);
        if (_table.GoalRewards.TryGetValue((level.Tower, level.Id), out var thresholds))
            foreach (var threshold in thresholds.Where(t => t.Points > before && t.Points <= score))
                _ctx.Rewards.Grant(player, threshold.Rewards, RewardSource.Extra, rewards);
    }

    /// <summary>
    /// Personal score is lifetime event currency earned. The currency itself gets spent in the event shop,
    /// which is why it needs its own counter. INFERRED: no client code names the source of this task value.
    /// </summary>
    private void CreditPersonalScore(Player player, TowerClass cls, int tower, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changed)
    {
        if (!_table.Money1.TryGetValue(tower, out var money)) return;
        int earned = rewards.Rewards.Select(r => r.ToTuple())
            .Where(t => t.Length >= 5 && t[0] == money[0] && t[1] == money[1] && t[2] == money[2] && t[3] == money[3])
            .Sum(t => t[4]);
        if (earned > 0) Write(player, changed, cls.Group, cls.ScoreTask, player.GetTask(cls.Group, cls.ScoreTask) + earned);
    }

    // ================================================================ 176 node point rewards

    /// <summary>The list screen sends rewardIds; the score screen sends none, meaning everything reached.</summary>
    private Task PointReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int tower = Number(param, "activeId");
        lock (player)
        {
            if (!_table.Towers.TryGetValue(tower, out var cls) || !_table.PointRewards.TryGetValue(tower, out var list))
                return Refuse(session, cmd);
            var wanted = (param as JsonObject)?["rewardIds"] is JsonArray ids
                ? ChallengeTowerFeature.ReadPositional(ids).ToHashSet() : null;
            int score = player.GetTask(cls.Group, cls.Offset + NodeScore);
            var due = list.Where(r => r.Goal <= score && (wanted is null || wanted.Contains(r.Id))
                && player.GetTask(cls.Group, cls.Offset + PointAward + r.Id) == 0).ToList();
            if (due.Count == 0) return Refuse(session, cmd);
            var rewards = new RewardGrantResult();
            var changed = new List<KeyValuePair<uint, int>>();
            foreach (var reward in due)
            {
                _ctx.Rewards.Grant(player, reward.Rewards, RewardSource.Extra, rewards);
                Write(player, changed, cls.Group, cls.Offset + PointAward + reward.Id, 1);
            }
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            // UI_TowerScoreInfo compares res with == true, so it must be a real boolean.
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["res"] = true, ["Reward"] = Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task Refuse(ClientSession session, int cmd)
    {
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["res"] = false });
        return Task.CompletedTask;
    }

    // ================================================================ 177/178 lottery pools

    /// <summary>
    /// Each pool holds up to 20 slots, each drawable Count times. tbItem lists the drawn slots (1-based), and
    /// the client looks up what to show by slot. nTowerId is what routes the reply to the right lottery screen.
    /// </summary>
    private Task PondDraw(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int tower = Number(param, "TowerId"), type = Number(param, "nType"), pond = Number(param, "nPondId");
        lock (player)
        {
            if (!_table.Towers.TryGetValue(tower, out var cls) || !_table.Tokens.TryGetValue((tower, type), out var token)
                || CurrentPond(player, cls) != pond || !_table.Ponds.TryGetValue((tower, pond), out var slots))
                return PondError(session, cmd, tower, ErrInvalid);
            if (player.GetMoney(token.Money) < token.Cost) return PondError(session, cmd, tower, ErrPoint);

            var records = Enumerable.Range(0, slots.Count)
                .Select(i => player.GetTask(cls.Group, PondTask(cls, pond, i + 1))).ToArray();
            int left = slots.Select((s, i) => Math.Max(0, s.Count - records[i])).Sum();
            if (left <= 0) return PondError(session, cmd, tower, ErrPondEmpty);

            player.AddMoney(token.Money, -token.Cost);
            var rewards = new RewardGrantResult();
            var changed = new List<KeyValuePair<uint, int>>();
            var drawn = new JsonArray();
            for (int n = Math.Min(token.Times, left); n > 0; n--)
            {
                int slot = PickSlot(slots, records);
                records[slot]++;
                drawn.Add(slot + 1);
                _ctx.Rewards.Grant(player, slots[slot].Item, RewardSource.Extra, rewards);
                Write(player, changed, cls.Group, PondTask(cls, pond, slot + 1), records[slot]);
            }
            _ctx.Store.Save(player);
            GameNotify.PushMoney(session, player, token.Money);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nTowerId"] = tower, ["nPondId"] = pond, ["tbItem"] = drawn });
        }
        return Task.CompletedTask;
    }

    private static int PickSlot(List<TowerPondEntry> slots, int[] records)
    {
        int total = 0;
        for (int i = 0; i < slots.Count; i++)
            if (records[i] < slots[i].Count) total += Math.Max(1, slots[i].Weight);
        int roll = Random.Shared.Next(total);
        for (int i = 0; i < slots.Count; i++)
        {
            if (records[i] >= slots[i].Count) continue;
            roll -= Math.Max(1, slots[i].Weight);
            if (roll < 0) return i;
        }
        throw new InvalidOperationException("pool slot selection ran out of slots");
    }

    /// <summary>nResult 1 is the client's "no next round" message, sent when this was the last pool.</summary>
    private Task PondReset(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int tower = Number(param, "TowerId"), pond = Number(param, "nPondId");
        lock (player)
        {
            if (!_table.Towers.TryGetValue(tower, out var cls) || CurrentPond(player, cls) != pond
                || !_table.Ponds.TryGetValue((tower, pond), out var slots))
                return PondError(session, cmd, tower, ErrInvalid);
            var counts = slots.Select((s, i) => player.GetTask(cls.Group, PondTask(cls, pond, i + 1))).ToArray();
            bool keyDrawn = slots.Select((s, i) => s.Key && counts[i] > 0).Any(k => k);
            bool emptied = slots.Select((s, i) => counts[i] >= s.Count).All(d => d);
            if (!keyDrawn && !emptied) return PondError(session, cmd, tower, ErrInvalid);

            if (!_table.Ponds.ContainsKey((tower, pond + 1)))
            {
                _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nResult"] = 1, ["nPondId"] = pond });
                return Task.CompletedTask;
            }
            var changed = new List<KeyValuePair<uint, int>>();
            Write(player, changed, cls.Group, cls.Offset + PondId, pond + 1);
            Write(player, changed, cls.Group, cls.Offset + PondResets, player.GetTask(cls.Group, cls.Offset + PondResets) + 1);
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, changed);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nResult"] = 0, ["nPondId"] = pond + 1 });
        }
        return Task.CompletedTask;
    }

    /// <summary>GetCurPondId: an unset value means the first pool.</summary>
    private static int CurrentPond(Player player, TowerClass cls)
    {
        int pond = player.GetTask(cls.Group, cls.Offset + PondId);
        return pond == 0 ? 1 : pond;
    }

    private static int PondTask(TowerClass cls, int pond, int slot) => cls.Offset + PondRecord + (pond - 1) * PondSize + slot;

    private Task PondError(ClientSession session, int cmd, int tower, int code)
    {
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nTowerId"] = tower, ["nError"] = code });
        return Task.CompletedTask;
    }

    // ================================================================ 61/62/63 swimwear score

    /// <summary>"Server-wide" score: on a private server the whole server is this player.</summary>
    private Task SwimwearServerScore(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int tower = Number(param, "nTowerId");
        int score = _table.Towers.TryGetValue(tower, out var cls) && cls.ScoreTask > 0 ? player.GetTask(cls.Group, cls.ScoreTask) : 0;
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nTowerId"] = tower, ["Score"] = score });
        return Task.CompletedTask;
    }

    private Task ScoreAward(ClientSession session, int cmd, JsonNode? param, Dictionary<int, List<TowerScoreAward>> awards, int flagOffset)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int tower = Number(param, "nTowerId"), index = Number(param, "Index");
        lock (player)
        {
            if (!_table.Towers.TryGetValue(tower, out var cls) || cls.ScoreTask == 0 || !awards.TryGetValue(tower, out var list)
                || list.FirstOrDefault(a => a.Order == index) is not { } award)
                return PondError(session, cmd, tower, ErrNoAward);
            int flag = cls.Offset + flagOffset + index;
            if (player.GetTask(cls.Group, flag) > 0) return PondError(session, cmd, tower, ErrGotAward);
            if (player.GetTask(cls.Group, cls.ScoreTask) < award.Score) return PondError(session, cmd, tower, ErrPoint);
            var rewards = new RewardGrantResult();
            var changed = new List<KeyValuePair<uint, int>>();
            _ctx.Rewards.Grant(player, award.Rewards, RewardSource.Extra, rewards);
            Write(player, changed, cls.Group, flag, 1);
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nTowerId"] = tower, ["tbAward"] = Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ shared

    private static JsonArray Tuples(RewardGrantResult rewards)
    {
        var data = new JsonArray();
        foreach (var reward in rewards.ToAwardTuples())
            data.Add(new JsonArray(reward.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
        return data;
    }

    private static int Number(JsonNode? param, string field)
        => (param as JsonObject)?[field] is JsonValue value && value.TryGetValue<double>(out double n) ? (int)n : 0;

    private static void Write(Player player, List<KeyValuePair<uint, int>> changes, int group, int key, int value)
    {
        if (player.GetTask(group, key) == value) return;
        player.SetTask(group, key, value);
        changes.Add(new(Player.ComposeTaskId(group, key), value));
    }

    private static Attempt? ReadAttempt(Player player)
    {
        try { return player.Extra.GetValueOrDefault(AttemptKey)?.Deserialize<Attempt>(); }
        catch (JsonException) { return null; }
    }

    private static void SaveAttempt(Player player, Attempt attempt) => player.Extra[AttemptKey] = JsonSerializer.SerializeToNode(attempt);

    private Task Error(ClientSession session, int cmd, int code)
    {
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = code });
        return Task.CompletedTask;
    }
}
