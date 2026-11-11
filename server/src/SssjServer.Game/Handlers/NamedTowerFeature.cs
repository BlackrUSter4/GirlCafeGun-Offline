using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Event towers with commands of their own: music, buff, linkage, anniversary, second anniversary, boss challenge,
/// war chess, micro tower, armed-inspection (CoopClient) and mixed online. They share one flow and take a named-field
/// request, unlike the positional Activities_* towers in ActivityTowerHandler.
///
/// Each family differs only in its level table and where it marks a clear, so those are data in Families below,
/// copied from each client file's nTask_* constants and CheckLevelPass. What is left per family is the extra it
/// records on a win, which runs as its OnPass.
/// </summary>
public sealed class NamedTowerFeature : IGameFeature
{
    private const int ErrInvalid = 2, ErrVigour = 5, ErrPoint = 8, ErrFormation = 10, ErrGotAward = 13;

    // ActivitiesLogic.ActivityType, echoed where the request omits nType.
    private const int TypeMicro = 12, TypeCoop = 17, TypeMusic = 19, TypeBuff = 20, TypeDiscovery = 22, TypeAnniversary = 23,
        TypeMix = 24, TypeLinkage = 25, TypeSecond = 27, TypeBoss = 28, TypeWarChess = 29;

    private const string AttemptKey = "namedTowerRun";

    private sealed record Level(int Tower, int Id, int Diff, int Node, int Type, int Cost, int MasterExp, int MaxScore,
        List<int[]> First, List<int[]> Fixed);

    /// <summary>What one win changes beyond the shared clear flag and rewards.</summary>
    private sealed record WinContext(Player Player, Level Level, JsonNode? Request, RewardGrantResult Rewards,
        List<KeyValuePair<uint, int>> Changed, JsonObject Reply, bool FirstClear);

    private sealed record Family(string Name, int Enter, int Pass, int Fail, int ActivityType, string LevelFile, int Group,
        Func<Level, int> PassKey, Func<Level, int> PassBit)
    {
        /// <summary>Tower column of the level file. Files for a single running event have none.</summary>
        public bool HasTower { get; init; } = true;
        public Action<WinContext>? OnPass { get; init; }
        public Action<Player, JsonNode?, List<KeyValuePair<uint, int>>>? OnLoss { get; init; }
    }

    private sealed class Attempt
    {
        public int Cmd { get; set; }
        public int Tower { get; set; }
        public int Level { get; set; }
        public int Diff { get; set; }
        public bool Active { get; set; }
        public JsonObject? Reply { get; set; }
        public BattleMainCard[] Cards { get; set; } = Array.Empty<BattleMainCard>();
        public Dictionary<int, int> PledgeSteps { get; set; } = new();
    }

    private GameFeatureContext _ctx = null!;
    private PledgeTable _pledge = null!;
    private readonly Dictionary<int, Family> _byCommand = new();
    private readonly Dictionary<Family, Dictionary<(int Tower, int Level, int Diff), Level>> _levels = new();
    private readonly Dictionary<(int Tower, int Level, int Diff), List<(int Goal, int Points, List<int[]> Reward)>> _microGoals = new();
    private readonly Dictionary<(int Tower, int Level), List<(int Goal, int Points)>> _discoveryGoals = new();
    private readonly Dictionary<(int Tower, int Level), List<(int Points, List<int[]> Reward)>> _discoveryRewards = new();
    private readonly Dictionary<(int Tower, int Level, int Diff), Dictionary<int, int>> _buffScores = new();
    private readonly Dictionary<int, List<(int Score, List<int[]> Reward)>> _buffAwards = new();
    private readonly Dictionary<int, List<(int Score, List<int[]> Reward)>> _bossAwards = new();
    private readonly Dictionary<(int Activity, int Node), Level> _mixNodes = new();
    private string _directory = "";

    public string Name => "Named-field event towers (music, buff, linkage, anniversary, boss, war chess, micro, coop, mix)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        _pledge = PledgeTable.Load(_directory);
        LoadExtras();
        foreach (var family in BuildFamilies())
        {
            _byCommand[family.Enter] = family;
            _byCommand[family.Pass] = family;
            _byCommand[family.Fail] = family;
            _levels[family] = LoadLevels(family);
            ctx.MapLuaCall(family.Enter, OnEnter);
            ctx.MapLuaCall(family.Pass, OnPass);
            ctx.MapLuaCall(family.Fail, OnFail);
        }
        // Opening these two towers is a round trip with no state: the client opens the screen from the echo.
        ctx.MapLuaCall(285, (s, c, p) => Echo(s, c, new JsonObject { ["nTowerId"] = Number(p, "nTowerId") }));
        ctx.MapLuaCall(335, (s, c, p) => Echo(s, c, new JsonObject { ["nId"] = Number(p, "nId") }));
        ctx.MapLuaCall(339, OnCoopSaveSp);
        ctx.MapLuaCall(376, (s, c, p) => OnScoreAward(s, c, p, _buffAwards, BuffGroup, BuffTotalScore, BuffGotScore));
        ctx.MapLuaCall(535, (s, c, p) => OnScoreAward(s, c, p, _bossAwards, BossGroup, 0, BossGotScore));
        if (ctx.Options.AllEventsOpen) ctx.OnPlayerPrepare((session, player) => SeedCurrentEvents(player));
    }

    /// <summary>
    /// Linkage and mixed online show whichever event id the server wrote into a task, so with every event open
    /// they need one chosen. Each calendar week moves to the next, which reaches them all over time.
    /// </summary>
    private void SeedCurrentEvents(Player player)
    {
        long week = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (7 * 86400);
        void Seed(string file, int group, int task)
        {
            var table = SettingTable.Load(Path.Combine(_directory, file));
            var ids = table?.Rows.Select(r => table.Int(r, "Id")).Where(i => i > 0).Distinct().OrderBy(i => i).ToArray();
            if (ids is { Length: > 0 }) player.SetTask(group, task, ids[(int)(week % ids.Length)]);
        }
        Seed("LinkageTower.txt", 63, 3001);
        Seed("MixOnlineActivities.txt", 68, MixCurrentId);
    }

    // Client task layouts referenced by more than one place below.
    private const int BuffGroup = 64, BuffTotalScore = 5008, BuffGotScore = 5009, BuffLevelScore = 5200, BuffDiffCount = 6;
    private const int BossGroup = 81, BossGotScore = 30104, BossLevelScore = 30150, BossDiffCount = 6;
    private const int MicroGroup = 59, MicroDiffMax = 5, MicroScore = 200, MicroGoals = 300;
    private const int DiscoveryGroup = 81, DiscoveryGoalBase = 20000;
    private const int CoopGroup = 68, CoopMaxScore = 5;

    private IEnumerable<Family> BuildFamilies()
    {
        yield return new("music", 371, 372, 373, TypeMusic, "MusicLevel", 64, l => 4100 + l.Id, l => l.Diff - 1) { HasTower = false };
        yield return new("linkage", 401, 402, 403, TypeLinkage, "LinkageLevel", 63, l => 3002 + l.Id, l => l.Diff - 1) { HasTower = false };
        yield return new("second anniversary", 529, 530, 531, TypeSecond, "2ndAnniversaryTowerLevel", 81, l => 30010 + l.Id, l => l.Tower - 1);
        yield return new("anniversary", 426, 427, 428, TypeAnniversary, "AnniversaryTowerLevel", 81, l => 1010 + l.Id, l => l.Tower - 1);
        yield return new("war chess", 536, 537, 538, TypeWarChess, "WarChessTowerLevel", WarChessBoardFeature.Group, l => 110 + l.Id, _ => -1)
        {
            OnPass = win => WarChessBoardFeature.BossWon(win.Player, win.Request, win.Changed),
            OnLoss = WarChessBoardFeature.BossFailed,
        };
        yield return new("coop", 336, 337, 338, TypeCoop, "CoopClientLevel", CoopGroup, l => 10 + l.Id, _ => -1) { OnPass = CoopWin };
        yield return new("buff", 377, 378, 379, TypeBuff, "BuffTowerLevel", BuffGroup, l => 5100 + l.Id, l => l.Diff - 1) { OnPass = BuffWin };
        yield return new("boss", 532, 533, 534, TypeBoss, "BossChallengeLevel", BossGroup, l => 30110 + l.Id, l => l.Diff - 1) { OnPass = BossWin };
        yield return new("micro", 286, 287, 288, TypeMicro, "FirstTowerLevel", MicroGroup, l => 100 + l.Id * MicroDiffMax + l.Diff, _ => -1) { OnPass = MicroWin };
        yield return new("discovery", 421, 422, 423, TypeDiscovery, "AnniversaryTowerLevel", DiscoveryGroup, l => 3002 + l.Tower * 100 + l.Id, _ => -1) { OnPass = DiscoveryWin };
        yield return new("mix", 451, 452, 453, TypeMix, "MixOnlineLevel", 68, _ => 1002, l => l.Node) { };
    }

    private Dictionary<(int, int, int), Level> LoadLevels(Family family)
    {
        var result = new Dictionary<(int, int, int), Level>();
        var table = SettingTable.Load(Path.Combine(_directory, family.LevelFile + ".txt"));
        if (table is null) return result;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "LevelId");
            if (id <= 0) continue;
            int tower = family.HasTower ? table.Int(row, "Id") : 0;
            int diff = table.Int(row, "Diff");
            result[(tower, id, diff)] = RowToLevel(table, row, tower, id, diff, 0, "FirstAward");
        }
        return result;
    }

    private static Level RowToLevel(SettingTable table, string[] row, int tower, int id, int diff, int node, string first)
        => new(tower, id, diff, node, table.Int(row, "LevelType"), table.Int(row, "PreCost"), table.Int(row, "MasterExp"),
            table.Int(row, "MaxScore"), ChapterTable.ParseTuples(table.Str(row, first)), ChapterTable.ParseTuples(table.Str(row, "FixAward")));

    private void LoadExtras()
    {
        if (SettingTable.Load(Path.Combine(_directory, "MicroTowerGoal.txt")) is { } micro)
            foreach (var row in micro.Rows)
            {
                var key = (micro.Int(row, "Id"), micro.Int(row, "LevelId"), micro.Int(row, "Diff"));
                if (!_microGoals.TryGetValue(key, out var list)) _microGoals[key] = list = new();
                list.Add((micro.Int(row, "GoalID"), micro.Int(row, "GoalPoint"), ChapterTable.ParseTuples(micro.Str(row, "Reward"))));
            }
        if (SettingTable.Load(Path.Combine(_directory, "AnniversaryTowerGoal.txt")) is { } goals)
            foreach (var row in goals.Rows)
            {
                var key = (goals.Int(row, "Id"), goals.Int(row, "LevelId"));
                if (!_discoveryGoals.TryGetValue(key, out var list)) _discoveryGoals[key] = list = new();
                list.Add((goals.Int(row, "GoalID"), goals.Int(row, "GoalPoint")));
            }
        if (SettingTable.Load(Path.Combine(_directory, "AnniversaryTowerGoalReward.txt")) is { } goalRewards)
            foreach (var row in goalRewards.Rows)
            {
                var key = (goalRewards.Int(row, "Id"), goalRewards.Int(row, "LevelId"));
                if (!_discoveryRewards.TryGetValue(key, out var list)) _discoveryRewards[key] = list = new();
                list.Add((goalRewards.Int(row, "GoalPoint"), ChapterTable.ParseTuples(goalRewards.Str(row, "Reward"))));
            }
        if (SettingTable.Load(Path.Combine(_directory, "BuffTowerBuff.txt")) is { } buffs)
        {
            var index = new Dictionary<(int, int, int), int>();
            foreach (var row in buffs.Rows)
            {
                var key = (buffs.Int(row, "Id"), buffs.Int(row, "LevelId"), buffs.Int(row, "Diff"));
                int position = index[key] = index.GetValueOrDefault(key) + 1;
                if (!_buffScores.TryGetValue(key, out var scores)) _buffScores[key] = scores = new();
                scores[position] = buffs.Int(row, "Score");
            }
        }
        LoadScoreAwards("BuffTowerAward", _buffAwards);
        LoadScoreAwards("BossChallengeScore", _bossAwards);
        LoadMixNodes();
    }

    private void LoadScoreAwards(string file, Dictionary<int, List<(int, List<int[]>)>> into)
    {
        if (SettingTable.Load(Path.Combine(_directory, file + ".txt")) is not { } table) return;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "Id");
            if (id <= 0) continue;
            if (!into.TryGetValue(id, out var list)) into[id] = list = new();
            list.Add((table.Int(row, "Score"), ChapterTable.ParseTuples(table.Str(row, "Awards"))));
        }
    }

    /// <summary>
    /// Mixed online addresses a node rather than a level. A node either plays a cartoon (Type 1) with its own reward
    /// or points at a level as "id,level" in MixOnlineLevel.txt.
    /// </summary>
    private void LoadMixNodes()
    {
        var activities = SettingTable.Load(Path.Combine(_directory, "MixOnlineActivities.txt"));
        var levels = SettingTable.Load(Path.Combine(_directory, "MixOnlineLevel.txt"));
        if (activities is null || levels is null) return;
        var byKey = new Dictionary<(int, int), Level>();
        foreach (var row in levels.Rows)
        {
            int id = levels.Int(row, "Id"), level = levels.Int(row, "LevelId");
            if (level > 0) byKey[(id, level)] = RowToLevel(levels, row, id, level, 0, 0, "FirstAward");
        }
        foreach (var activity in activities.Rows)
        {
            int id = activities.Int(activity, "Id");
            string file = activities.Str(activity, "NodeConf");
            if (id <= 0 || file.Length == 0) continue;
            var nodes = SettingTable.Load(Path.Combine(_directory, file));
            if (nodes is null) continue;
            foreach (var row in nodes.Rows)
            {
                int node = nodes.Int(row, "NodeId");
                if (node <= 0) continue;
                if (nodes.Int(row, "Type") == 1)
                {
                    _mixNodes[(id, node)] = new(id, node, 0, node, 2, 0, 0, 0,
                        ChapterTable.ParseTuples(nodes.Str(row, "CartoonReward")), new());
                    continue;
                }
                var target = TwentyFirstTowerTable.Numbers(nodes.Str(row, "Level"));
                if (target.Length >= 2 && byKey.TryGetValue((target[0], target[1]), out var level))
                    _mixNodes[(id, node)] = level with { Node = node };
            }
        }
    }

    // ================================================================ enter / pass / fail

    private Level? Resolve(Family family, Player player, JsonNode? request)
    {
        if (family.Name == "mix")
            return _mixNodes.GetValueOrDefault((player.GetTask(family.Group, MixCurrentId), Number(request, "nodeId")));
        int tower = !family.HasTower ? 0 : Number(request, "nId") is var id and > 0 ? id : Number(request, "nTowerId");
        return _levels[family].GetValueOrDefault((tower, Number(request, "nLevelId"), Number(request, "nDiff")));
    }

    internal const int MixCurrentId = 1001;

    private Task OnEnter(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var family = _byCommand[cmd];
        lock (player)
        {
            if (Resolve(family, player, param) is not { } level) return Error(session, cmd, ErrInvalid);
            var cards = Array.Empty<BattleMainCard>();
            if (level.Type != 2)
            {
                int formation = Number(param, "nFormationId") is var f and > 0 ? f : Number(param, "formationId");
                var guids = ChapterHandler.SnapshotMainCards(player, new JsonObject { ["nFormationId"] = formation }, out bool valid);
                if (!valid || guids.Length == 0) return Error(session, cmd, ErrFormation);
                cards = guids.Select(id => BattleMainCard.From(player.Items.First(i => i.Guid == id))).ToArray();
            }
            var open = ReadAttempt(player);
            bool resend = open is { Active: true } && open.Cmd == family.Pass && open.Tower == level.Tower
                && open.Level == level.Id && open.Diff == level.Diff;
            if (!resend)
            {
                if (_ctx.Options.SpendVigour && level.Cost > 0)
                {
                    if (!player.TrySpendVigour(level.Cost)) return Error(session, cmd, ErrVigour);
                    GameNotify.PushMoney(session, player, MoneyType.Vigour);
                }
                SaveAttempt(player, new Attempt
                {
                    Cmd = family.Pass, Tower = level.Tower, Level = level.Id, Diff = level.Diff, Active = true, Cards = cards,
                    PledgeSteps = PledgeFeature.LoadStateStatic(player).Girls.ToDictionary(g => g.Key, g => g.Value.Step),
                });
                _ctx.Store.Save(player);
            }
            // The client reads the ids back from tbData, so echo the request and fill what this family omits.
            var data = param is JsonObject request ? (JsonObject)request.DeepClone() : new JsonObject();
            data["nType"] ??= family.ActivityType;
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
            {
                ["tbData"] = data,
                ["tbDrop"] = new JsonArray(),
                ["tbDropItems"] = new JsonArray(),
            });
        }
        return Task.CompletedTask;
    }

    private Task OnPass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var family = _byCommand[cmd];
        lock (player)
        {
            if (Resolve(family, player, param) is not { } level) return Error(session, cmd, ErrInvalid);
            var attempt = ReadAttempt(player);
            if (attempt is null || attempt.Cmd != cmd || attempt.Tower != level.Tower || attempt.Level != level.Id || attempt.Diff != level.Diff)
                return Error(session, cmd, ErrInvalid);
            if (!attempt.Active)
            {
                if (attempt.Reply is null) return Error(session, cmd, ErrInvalid);
                _ctx.LuaCalls.CallClient(session, cmd, attempt.Reply.DeepClone());
                return Task.CompletedTask;
            }

            var changed = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            int key = family.PassKey(level), bit = family.PassBit(level);
            int before = player.GetTask(family.Group, key);
            bool first = bit >= 0 ? (before >> bit & 1) == 0 : before <= 0;
            if (first) _ctx.Rewards.Grant(player, level.First, RewardSource.FirstAward, rewards);
            _ctx.Rewards.Grant(player, level.Fixed, RewardSource.FixedAward, rewards);
            Write(player, changed, family.Group, key, bit >= 0 ? before | 1 << bit : before + 1);

            var reply = new JsonObject
            {
                ["nMasterExp"] = level.MasterExp,
                ["nCardExp"] = 0,
                ["newGoalIds"] = new JsonArray(),
                ["isPass"] = true,
            };
            family.OnPass?.Invoke(new WinContext(player, level, param, rewards, changed, reply, first));
            EventMissionProgress.RecordTowerClear(player, level.Tower, family.ActivityType, level.Id);
            if (level.Type != 2)
                changed.AddRange(ChapterCombatProgress.RecordTowerVictory(_ctx.Missions, _pledge, player, attempt.Cards, attempt.PledgeSteps));
            if (level.MasterExp > 0) PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, level.MasterExp);

            // Micro tower reads tbAwards, every other family tbData.
            reply["tbData"] = Tuples(rewards);
            reply["tbAwards"] = Tuples(rewards);
            attempt.Active = false;
            attempt.Reply = reply;
            SaveAttempt(player, attempt);
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            if (level.MasterExp > 0) GameNotify.PushLevelExp(session, player);
            _ctx.LuaCalls.CallClient(session, cmd, reply.DeepClone());
        }
        return Task.CompletedTask;
    }

    private Task OnFail(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
            lock (player)
            {
                var attempt = ReadAttempt(player);
                if (attempt is { Active: true })
                {
                    attempt.Active = false;
                    attempt.Reply = new JsonObject();
                    SaveAttempt(player, attempt);
                    var changes = new List<KeyValuePair<uint, int>>();
                    _byCommand[cmd].OnLoss?.Invoke(player, param, changes);
                    _ctx.Store.Save(player);
                    GameNotify.PushTaskValues(session, changes);
                }
            }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ per-family win records

    /// <summary>
    /// A level's score is the points of the buffs chosen for that run, kept at its best, and the total sums those bests.
    /// tbBuffs lists chosen positions within the level's BuffTowerBuff rows.
    /// </summary>
    private void BuffWin(WinContext win)
    {
        int score = 0;
        if (_buffScores.TryGetValue((win.Level.Tower, win.Level.Id, win.Level.Diff), out var table)
            && (win.Request as JsonObject)?["tbBuffs"] is JsonArray chosen)
            foreach (int index in ChallengeTowerFeature.ReadPositional(chosen))
                score += table.GetValueOrDefault(index);
        int task = BuffLevelScore + (win.Level.Id - 1) * BuffDiffCount + win.Level.Diff;
        int best = win.Player.GetTask(BuffGroup, task);
        if (score <= best) return;
        Write(win.Player, win.Changed, BuffGroup, task, score);
        Write(win.Player, win.Changed, BuffGroup, BuffTotalScore, win.Player.GetTask(BuffGroup, BuffTotalScore) + score - best);
    }

    /// <summary>
    /// tbRet is {useTime, leftHpRate, score, bNew} for the result screen. INFERRED: no client code holds the scoring
    /// formula, so a clear earns half of MaxScore and the other half scales with the HP left at the end.
    /// </summary>
    private void BossWin(WinContext win)
    {
        var request = win.Request as JsonObject;
        double rate = (request?["leftHpRate"] as JsonValue)?.TryGetValue<double>(out double hp) == true ? Math.Clamp(hp, 0, 1) : 0;
        int useTime = Number(win.Request, "useTime");
        int score = (int)Math.Round(win.Level.MaxScore * (0.5 + 0.5 * rate));
        int task = BossLevelScore + (win.Level.Id - 1) * BossDiffCount + win.Level.Diff;
        bool better = score > win.Player.GetTask(BossGroup, task);
        if (better) Write(win.Player, win.Changed, BossGroup, task, score);
        win.Reply["tbRet"] = new JsonArray(useTime, rate, score, better);
    }

    /// <summary>GetLevelGoals reads goal bits, GetLevelScore the sum of reached points. A goal's reward pays once.</summary>
    private void MicroWin(WinContext win)
    {
        if (!_microGoals.TryGetValue((win.Level.Tower, win.Level.Id, win.Level.Diff), out var goals)) return;
        int slot = win.Level.Id * MicroDiffMax + win.Level.Diff;
        int bits = win.Player.GetTask(MicroGroup, MicroGoals + slot);
        int score = win.Player.GetTask(MicroGroup, MicroScore + slot);
        var report = (win.Request as JsonObject)?["tbGoals"];
        var fresh = new JsonArray();
        foreach (var (goal, points, reward) in goals)
        {
            if ((bits >> goal & 1) == 1 || !GoalReached(report, goal)) continue;
            bits |= 1 << goal;
            score += points;
            _ctx.Rewards.Grant(win.Player, reward, RewardSource.Extra, win.Rewards);
            fresh.Add(goal);
        }
        Write(win.Player, win.Changed, MicroGroup, MicroGoals + slot, bits);
        Write(win.Player, win.Changed, MicroGroup, MicroScore + slot, score);
        win.Reply["newGoalIds"] = fresh;
    }

    /// <summary>Same goal scheme as the small towers: flags per goal, summed points, thresholds paid on crossing.</summary>
    private void DiscoveryWin(WinContext win)
    {
        var key = (win.Level.Tower, win.Level.Id);
        if (!_discoveryGoals.TryGetValue(key, out var goals)) return;
        var report = (win.Request as JsonObject)?["tbFinish"];
        int before = win.Player.GetTask(DiscoveryGroup, DiscoveryGoalBase + win.Level.Id);
        var fresh = new JsonArray();
        foreach (var (goal, _) in goals)
        {
            int flag = DiscoveryGoalBase + win.Level.Id * 100 + goal;
            if (win.Player.GetTask(DiscoveryGroup, flag) > 0 || !GoalReached(report, goal)) continue;
            Write(win.Player, win.Changed, DiscoveryGroup, flag, 1);
            fresh.Add(goal);
        }
        int score = Math.Max(before, goals.Where(g => win.Player.GetTask(DiscoveryGroup, DiscoveryGoalBase + win.Level.Id * 100 + g.Goal) > 0).Sum(g => g.Points));
        Write(win.Player, win.Changed, DiscoveryGroup, DiscoveryGoalBase + win.Level.Id, score);
        if (_discoveryRewards.TryGetValue(key, out var thresholds))
            foreach (var (points, reward) in thresholds.Where(t => t.Points > before && t.Points <= score))
                _ctx.Rewards.Grant(win.Player, reward, RewardSource.Extra, win.Rewards);
        win.Reply["newGoalIds"] = fresh;
    }

    private void CoopWin(WinContext win)
    {
        int damage = Number(win.Request, "nDamage");
        if (damage > win.Player.GetTask(CoopGroup, CoopMaxScore)) Write(win.Player, win.Changed, CoopGroup, CoopMaxScore, damage);
        win.Reply["curScore"] = damage;
        win.Reply["spKillNum"] = Number(win.Request, "nKillNum");
    }

    /// <summary>The result screen only waits for this to continue, with the SP award as its payload.</summary>
    private Task OnCoopSaveSp(ClientSession session, int cmd, JsonNode? param)
        => Echo(session, cmd, new JsonObject { ["tbAward"] = new JsonArray() });

    /// <summary>Thresholds pay in order up to the player's total, and the got-score task records how far they have been paid.</summary>
    private Task OnScoreAward(ClientSession session, int cmd, JsonNode? param,
        Dictionary<int, List<(int Score, List<int[]> Reward)>> awards, int group, int totalTask, int gotTask)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = Number(param, "nId");
        lock (player)
        {
            if (!awards.TryGetValue(id, out var list)) return Error(session, cmd, ErrInvalid);
            int total = totalTask > 0 ? player.GetTask(group, totalTask) : BossTotalScore(player, id);
            int got = player.GetTask(group, gotTask);
            var due = list.Where(a => a.Score > got && a.Score <= total).ToList();
            if (due.Count == 0) return Error(session, cmd, total <= got ? ErrGotAward : ErrPoint);
            var rewards = new RewardGrantResult();
            var changed = new List<KeyValuePair<uint, int>>();
            foreach (var (_, reward) in due) _ctx.Rewards.Grant(player, reward, RewardSource.Extra, rewards);
            Write(player, changed, group, gotTask, due.Max(a => a.Score));
            _ctx.Store.Save(player);
            GameNotify.PushRewards(session, player, rewards);
            GameNotify.PushTaskValues(session, changed);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbAward"] = Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>BossChallengeLogic.GetTotalScore: the best score across each level's difficulties, summed over levels.</summary>
    private int BossTotalScore(Player player, int tower)
    {
        var family = _byCommand[532];
        return _levels[family].Values.Where(l => l.Tower == tower).GroupBy(l => l.Id)
            .Sum(g => g.Max(l => player.GetTask(BossGroup, BossLevelScore + (l.Id - 1) * BossDiffCount + l.Diff)));
    }

    // ================================================================ shared

    /// <summary>Lua sends a goal table as an object or as an array depending on its keys.</summary>
    private static bool GoalReached(JsonNode? report, int goal)
    {
        var node = report is JsonObject table ? table[goal.ToString()]
            : report is JsonArray list && goal > 0 && goal <= list.Count ? list[goal - 1] : null;
        return node is JsonValue value && value.TryGetValue<bool>(out bool reached) && reached;
    }

    private Task Echo(ClientSession session, int cmd, JsonObject reply)
    {
        _ctx.LuaCalls.CallClient(session, cmd, reply);
        return Task.CompletedTask;
    }

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
