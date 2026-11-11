using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The roguelike card towers: Animalize (tower 4) and EighthTower (tower 8). Commands 64-69 plus their battles on the
/// shared 54/55/56. A run moves through the client's AnimalizeStateType: story, pick difficulty, draw three, reveal the
/// chosen card, and stage end. Everything lives in tasks, so the client rebuilds the screen from them on Refresh.
///
/// INFERRED: nothing in the client says when a stage ends or when the boss card turns up, so the SP card can be drawn
/// from the eighth draw, is forced on the twentieth (the task layout's card limit), and clearing it ends the stage.
/// </summary>
internal sealed class AnimalizeHandler
{
    // AnimalizeLogic.lua, relative to the tower's offset (1000 for Animalize, 3000 for EighthTower).
    private const int Group = 34, Stage = 4, Diff = 5, State = 6, UnlockDiff = 7, Score = 8, DrawCount = 9, DiffAwardFlag = 10,
        Choices = 11, Cards = 14, EpState = 34, Buffs = 54, Achievements = 100, EnergyCount = 201, MoneyCount = 202, ScoreAwards = 230;
    private const int CardSlots = 20, BuffSlots = 40, ResetEnergy = 5, ChoiceCount = 3;
    private const int FirstSpDraw = 8;

    // AnimalizeStateType
    private const int StateEp = 0, StateDiff = 1, StateRandCard = 2, StateCardDetails = 3, StateStageEnd = 4;
    // AnimlaizeCardType
    private const int CardBox = 1, CardFate = 2, CardAct = 3, CardSp = 4;
    // AnimalizeCardBuff EffectType
    private const int BuffEnergy = 2, BuffItems = 3, BuffForceSp = 5;

    private const int ErrInvalid = 2, ErrVigour = 5, ErrFormation = 10;
    private const int LevelStory = 2;

    private sealed record Level(int Diff, int Id, int Type, int Cost, int MasterExp, List<int[]> First, List<int[]> Fixed);
    private sealed record Card(int Id, int Type, string Award);
    private sealed record Buff(int Id, int Effect, string Value);
    private sealed record Achieve(int Id, int Type, int Count, List<int[]> Rewards);

    private sealed class Tower
    {
        public int Offset;
        public int[] Money = Array.Empty<int>();
        public readonly Dictionary<(int Diff, int Level), Level> Levels = new();
        public readonly Dictionary<int, Card> Cards = new();
        public readonly Dictionary<int, Buff> Buffs = new();
        public readonly Dictionary<int, List<int[]>> DiffAwards = new();
        public readonly List<Achieve> Achieves = new();
        public readonly List<(int Id, int Score, List<int[]> Rewards)> ScoreAwards = new();
    }

    private readonly GameFeatureContext _ctx;
    private readonly PledgeTable _pledge;
    private readonly Dictionary<int, Tower> _towers = new();

    internal AnimalizeHandler(GameFeatureContext ctx, string directory, PledgeTable pledge)
    {
        _ctx = ctx;
        _pledge = pledge;
        _towers[4] = Load(directory, "Animalize", 4, 1000);
        _towers[8] = Load(directory, "Eighth", 8, 3000);
        ctx.MapLuaCall(64, OnChooseDiff);
        ctx.MapLuaCall(65, OnChooseCard);
        ctx.MapLuaCall(66, OnClickCard);
        ctx.MapLuaCall(67, OnNextStage);
        ctx.MapLuaCall(68, OnAchieveAward);
        ctx.MapLuaCall(69, OnReset);
    }

    internal bool Owns(int tower) => _towers.ContainsKey(tower);

    private static Tower Load(string directory, string prefix, int id, int offset)
    {
        var tower = new Tower { Offset = offset };
        SettingTable? Table(string name) => SettingTable.Load(Path.Combine(directory, prefix + name + ".txt"));
        bool Mine(SettingTable t, string[] row) => t.Int(row, "ActiveId") == id;
        if (SettingTable.Load(Path.Combine(directory, "Tower.txt")) is { } towers)
            foreach (var row in towers.Rows)
                if (towers.Int(row, "Id") == id)
                    tower.Money = TwentyFirstTowerTable.Numbers(towers.Str(row, "Money1GDPL"));
        if (Table("Level") is { } levels)
            foreach (var row in levels.Rows)
                if (Mine(levels, row) && levels.Int(row, "LevelId") > 0)
                    tower.Levels[(levels.Int(row, "Diff"), levels.Int(row, "LevelId"))] = new(levels.Int(row, "Diff"), levels.Int(row, "LevelId"),
                        levels.Int(row, "LevelType"), levels.Int(row, "PreCost"), levels.Int(row, "MasterExp"),
                        ChapterTable.ParseTuples(levels.Str(row, "FirstAward")), ChapterTable.ParseTuples(levels.Str(row, "FixAward")));
        if (Table("Card") is { } cards)
            foreach (var row in cards.Rows)
                if (Mine(cards, row) && cards.Int(row, "Id") > 0)
                    tower.Cards[cards.Int(row, "Id")] = new(cards.Int(row, "Id"), cards.Int(row, "Type"), cards.Str(row, "Award"));
        if (Table("CardBuff") is { } buffs)
            foreach (var row in buffs.Rows)
                if (buffs.Int(row, "BuffId") > 0)
                    tower.Buffs[buffs.Int(row, "BuffId")] = new(buffs.Int(row, "BuffId"), buffs.Int(row, "EffectType"), buffs.Str(row, "Value"));
        if (Table("DiffAward") is { } awards)
            foreach (var row in awards.Rows)
                if (Mine(awards, row))
                    tower.DiffAwards[awards.Int(row, "Diff")] = ChapterTable.ParseTuples(awards.Str(row, "Awards"));
        if (Table("Achieve") is { } achieves)
            foreach (var row in achieves.Rows)
                if (Mine(achieves, row))
                    tower.Achieves.Add(new(achieves.Int(row, "Id"), achieves.Int(row, "Type"), achieves.Int(row, "Count"), ChapterTable.ParseTuples(achieves.Str(row, "GDPL"))));
        if (Table("ScoreAward") is { } score)
            foreach (var row in score.Rows)
                if (Mine(score, row))
                    tower.ScoreAwards.Add((score.Int(row, "Id"), score.Int(row, "Score"), ChapterTable.ParseTuples(score.Str(row, "GDPL"))));
        return tower;
    }

    private Tower? TowerOf(JsonNode? param) => _towers.GetValueOrDefault(EventKit.Number(param, "TowerId"));

    private static int Get(Player player, Tower tower, int field) => player.GetTask(Group, tower.Offset + field);

    private static void Set(Player player, Tower tower, int field, int value, List<KeyValuePair<uint, int>> changes)
        => EventKit.Write(player, changes, Group, tower.Offset + field, value);

    /// <summary>A card slot holds CardId in the high bits and "was chosen" in bit 0, as GetAnimalizeRandomCard reads it.</summary>
    private static JsonArray ChoicesJson(Player player, Tower tower)
    {
        var list = new JsonArray();
        for (int i = 0; i < ChoiceCount; i++)
        {
            int value = Get(player, tower, Choices + i);
            list.Add(new JsonObject { ["CardId"] = value >> 1, ["Type"] = value & 1 });
        }
        return list;
    }

    private static bool HasBuff(Player player, Tower tower, int effect)
    {
        for (int i = 0; i < BuffSlots; i++)
        {
            int value = Get(player, tower, Buffs + i) & 0xFFFF;
            if (value > 0 && tower.Buffs.TryGetValue(value, out var buff) && buff.Effect == effect) return true;
        }
        return false;
    }

    private static void Deal(Player player, Tower tower, List<KeyValuePair<uint, int>> changes)
    {
        int draws = Get(player, tower, DrawCount) + 1;
        Set(player, tower, DrawCount, draws, changes);
        bool spAllowed = draws >= FirstSpDraw || HasBuff(player, tower, BuffForceSp);
        var pool = tower.Cards.Values.Where(c => spAllowed || c.Type != CardSp).ToList();
        var picks = pool.OrderBy(_ => Random.Shared.Next()).Take(ChoiceCount).ToList();
        if (draws >= CardSlots && picks.All(c => c.Type != CardSp) && tower.Cards.Values.FirstOrDefault(c => c.Type == CardSp) is { } sp && picks.Count > 0)
            picks[0] = sp;
        for (int i = 0; i < ChoiceCount; i++)
            Set(player, tower, Choices + i, i < picks.Count ? picks[i].Id << 1 : 0, changes);
        Set(player, tower, State, StateRandCard, changes);
    }

    private static Card? ChosenCard(Player player, Tower tower)
    {
        for (int i = 0; i < ChoiceCount; i++)
        {
            int value = Get(player, tower, Choices + i);
            if ((value & 1) == 1 && tower.Cards.TryGetValue(value >> 1, out var card)) return card;
        }
        return null;
    }

    private Task OnChooseDiff(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int diff = EventKit.Number(param, "Diff");
        lock (player)
        {
            var tower = TowerOf(param);
            int unlocked = tower is null ? 0 : Math.Max(1, Get(player, tower, UnlockDiff) >> 1);
            if (tower is null || diff < 1 || diff > unlocked || !tower.Levels.Keys.Any(k => k.Diff == diff))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = 1 });
            var changes = new List<KeyValuePair<uint, int>>();
            Set(player, tower, Diff, diff, changes);
            Deal(player, tower, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes,
                new JsonObject { ["Error"] = 0, ["Diff"] = diff, ["tbRandom"] = ChoicesJson(player, tower) });
        }
        return Task.CompletedTask;
    }

    private Task OnChooseCard(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int index = EventKit.Number(param, "Index");
        lock (player)
        {
            var tower = TowerOf(param);
            if (tower is null || index < 1 || index > ChoiceCount || Get(player, tower, State) != StateRandCard)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            var changes = new List<KeyValuePair<uint, int>>();
            int value = Get(player, tower, Choices + index - 1);
            Set(player, tower, Choices + index - 1, value | 1, changes);
            for (int slot = 0; slot < CardSlots; slot++)
                if (Get(player, tower, Cards + slot) == 0)
                {
                    Set(player, tower, Cards + slot, value | 1, changes);
                    break;
                }
            Set(player, tower, State, StateCardDetails, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, ChoicesJson(player, tower));
        }
        return Task.CompletedTask;
    }

    /// <summary>Box cards pay and fate cards add a buff, then a new draw starts. Fight cards hand over to the level screen.</summary>
    private Task OnClickCard(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var tower = TowerOf(param);
            if (tower is null || Get(player, tower, State) != StateCardDetails || ChosenCard(player, tower) is not { } card)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            if (card.Type is CardAct or CardSp)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["bNextRandom"] = false, ["cardId"] = card.Id });

            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            if (card.Type == CardBox) EventMissionProgress.Bump(player, BoxesKey(tower));
            if (card.Type == CardBox) _ctx.Rewards.Grant(player, ChapterTable.ParseTuples(card.Award), RewardSource.Extra, rewards);
            if (card.Type == CardFate)
                foreach (int buff in TwentyFirstTowerTable.Numbers(card.Award))
                    for (int i = 0; i < BuffSlots; i++)
                        if (Get(player, tower, Buffs + i) == 0)
                        {
                            Set(player, tower, Buffs + i, buff, changes);
                            break;
                        }
            CreditMoney(player, tower, rewards, changes);
            Deal(player, tower, changes);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes,
                new JsonObject { ["bNextRandom"] = true, ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private static string BoxesKey(Tower tower) => $"roundBoxes:{tower.Offset}";

    private void ClearRun(Player player, Tower tower, List<KeyValuePair<uint, int>> changes)
    {
        EventMissionProgress.Reset(player, BoxesKey(tower));
        for (int i = 0; i < ChoiceCount; i++) Set(player, tower, Choices + i, 0, changes);
        for (int i = 0; i < CardSlots; i++) Set(player, tower, Cards + i, 0, changes);
        for (int i = 0; i < BuffSlots; i++) Set(player, tower, Buffs + i, 0, changes);
        Set(player, tower, DrawCount, 0, changes);
        Set(player, tower, EnergyCount, 0, changes);
        Set(player, tower, MoneyCount, 0, changes);
        Set(player, tower, State, StateDiff, changes);
    }

    private Task OnNextStage(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var tower = TowerOf(param);
            if (tower is null || Get(player, tower, State) != StateStageEnd)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            var changes = new List<KeyValuePair<uint, int>>();
            Set(player, tower, Stage, Math.Max(1, Get(player, tower, Stage)) + 1, changes);
            ClearRun(player, tower, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["nError"] = 0 });
        }
        return Task.CompletedTask;
    }

    private Task OnReset(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var tower = TowerOf(param);
            if (tower is null) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            if (_ctx.Options.SpendVigour)
            {
                if (!player.TrySpendVigour(ResetEnergy)) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrVigour });
                GameNotify.PushMoney(session, player, MoneyType.Vigour);
            }
            var changes = new List<KeyValuePair<uint, int>>();
            ClearRun(player, tower, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["nError"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>The memory screen's one button: every achievement and score award reached so far.</summary>
    private Task OnAchieveAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var tower = TowerOf(param);
            if (tower is null) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            foreach (var achieve in tower.Achieves)
            {
                int value = Get(player, tower, Achievements + achieve.Id);
                if ((value & 1) == 1 || value >> 1 < achieve.Count) continue;
                _ctx.Rewards.Grant(player, achieve.Rewards, RewardSource.Extra, rewards);
                Set(player, tower, Achievements + achieve.Id, value | 1, changes);
            }
            int score = Get(player, tower, Score);
            foreach (var (id, need, reward) in tower.ScoreAwards)
            {
                if (score < need || Get(player, tower, ScoreAwards + id) == 1) continue;
                _ctx.Rewards.Grant(player, reward, RewardSource.Extra, rewards);
                Set(player, tower, ScoreAwards + id, 1, changes);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ battles on 54/55/56

    private const string RunKey = "animalizeRun";

    internal Task Enter(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var args = ChallengeTowerFeature.ReadPositional(param);
        lock (player)
        {
            if (args.Count < 5 || !_towers.TryGetValue(args[1], out var tower) || !tower.Levels.TryGetValue((args[2], args[3]), out var level))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            var cards = Array.Empty<BattleMainCard>();
            if (level.Type != LevelStory)
            {
                var guids = ChapterHandler.SnapshotMainCards(player, new JsonObject { ["nFormationId"] = args[4] }, out bool valid);
                if (!valid || guids.Length == 0) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrFormation });
                cards = guids.Select(id => BattleMainCard.From(player.Items.First(i => i.Guid == id))).ToArray();
            }
            int cost = Math.Max(0, level.Cost - EnergySale(player, tower));
            if (_ctx.Options.SpendVigour && cost > 0)
            {
                if (!player.TrySpendVigour(cost)) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrVigour });
                GameNotify.PushMoney(session, player, MoneyType.Vigour);
            }
            var changes = new List<KeyValuePair<uint, int>>();
            Set(player, tower, EnergyCount, Get(player, tower, EnergyCount) + cost, changes);
            player.Extra[RunKey] = new JsonObject { ["tower"] = args[1], ["diff"] = args[2], ["level"] = args[3], ["active"] = true };
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject
            {
                ["tbData"] = new JsonArray(args[0], args[1], args[2], args[3]),
                ["tbDrop"] = new JsonArray(),
                ["tbDropItems"] = new JsonArray(),
            });
        }
        return Task.CompletedTask;
    }

    private static int EnergySale(Player player, Tower tower)
    {
        int sale = 0;
        for (int i = 0; i < BuffSlots; i++)
        {
            int value = Get(player, tower, Buffs + i) & 0xFFFF;
            if (value > 0 && tower.Buffs.TryGetValue(value, out var buff) && buff.Effect == BuffEnergy && int.TryParse(buff.Value, out int amount))
                sale += amount;
        }
        return sale;
    }

    private void CreditMoney(Player player, Tower tower, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changes)
    {
        if (tower.Money.Length < 4) return;
        int earned = rewards.Rewards.Select(r => r.ToTuple())
            .Where(t => t.Length >= 5 && t[0] == tower.Money[0] && t[1] == tower.Money[1] && t[2] == tower.Money[2] && t[3] == tower.Money[3])
            .Sum(t => t[4]);
        if (earned <= 0) return;
        Set(player, tower, Score, Get(player, tower, Score) + earned, changes);
        Set(player, tower, MoneyCount, Get(player, tower, MoneyCount) + earned, changes);
    }

    private static void BumpAchieve(Player player, Tower tower, int type, List<KeyValuePair<uint, int>> changes, int amount = 1)
    {
        foreach (var achieve in tower.Achieves.Where(a => a.Type == type))
        {
            int value = Get(player, tower, Achievements + achieve.Id);
            Set(player, tower, Achievements + achieve.Id, (value >> 1) + amount << 1 | value & 1, changes);
        }
    }

    internal Task Pass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var args = ChallengeTowerFeature.ReadPositional(param);
        lock (player)
        {
            var run = player.Extra.GetValueOrDefault(RunKey) as JsonObject;
            if (args.Count < 4 || !_towers.TryGetValue(args[1], out var tower) || !tower.Levels.TryGetValue((args[2], args[3]), out var level)
                || run?["active"]?.GetValue<bool>() != true || run["tower"]?.GetValue<int>() != args[1] || run["level"]?.GetValue<int>() != args[3])
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrInvalid });
            run["active"] = false;

            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            int epTask = EpState + level.Diff;
            bool first = !EventKit.HasBit(player, Group, tower.Offset + epTask, level.Id);
            if (first) _ctx.Rewards.Grant(player, level.First, RewardSource.FirstAward, rewards);
            _ctx.Rewards.Grant(player, level.Fixed, RewardSource.FixedAward, rewards);
            EventKit.SetBit(player, changes, Group, tower.Offset + epTask, level.Id);

            if (level.Diff == 0)
                FinishEp(player, tower, level, changes);
            else
                FinishFight(player, tower, level, rewards, changes);
            CreditMoney(player, tower, rewards, changes);
            if (level.MasterExp > 0) PlayerProgress.AddMasterExp(player, _ctx.PlayerExp, level.MasterExp);
            EventMissionProgress.RecordTowerClear(player, args[1], args[0], level.Id);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject
            {
                ["tbData"] = EventKit.Tuples(rewards), ["nMasterExp"] = level.MasterExp, ["nCardExp"] = 0,
            });
            if (level.MasterExp > 0) GameNotify.PushLevelExp(session, player);
        }
        return Task.CompletedTask;
    }

    /// <summary>Opening stories play in level order, one per stage value. After the last one, difficulty selection opens.</summary>
    private static void FinishEp(Player player, Tower tower, Level level, List<KeyValuePair<uint, int>> changes)
    {
        var stories = tower.Levels.Keys.Where(k => k.Diff == 0).Select(k => k.Level).OrderBy(i => i).ToList();
        int next = stories.FirstOrDefault(id => id > level.Id);
        if (next > 0)
        {
            Set(player, tower, Stage, next, changes);
            return;
        }
        Set(player, tower, Stage, 1, changes);
        Set(player, tower, State, StateDiff, changes);
    }

    private void FinishFight(Player player, Tower tower, Level level, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changes)
    {
        foreach (var item in BuffRewards(player, tower)) _ctx.Rewards.Grant(player, new[] { item }, RewardSource.Extra, rewards);
        var card = ChosenCard(player, tower);
        if (card?.Type != CardSp)
        {
            Deal(player, tower, changes);
            return;
        }
        // The boss card ends the stage: its difficulty's reward pays, the next difficulty opens, and rounds count up.
        _ctx.Rewards.Grant(player, tower.DiffAwards.GetValueOrDefault(level.Diff), RewardSource.Extra, rewards);
        int unlocked = Math.Max(1, Get(player, tower, UnlockDiff) >> 1);
        if (level.Diff >= unlocked && tower.Levels.Keys.Any(k => k.Diff == level.Diff + 1))
            Set(player, tower, UnlockDiff, level.Diff + 1 << 1 | 1, changes);
        BumpAchieve(player, tower, 1, changes);
        BumpAchieve(player, tower, 2, changes);
        // Mission conditions 113 (cleared this difficulty) and 114 (rounds cleared).
        EventMissionProgress.Bump(player, $"113:{level.Diff}");
        EventMissionProgress.Bump(player, "114");
        EventMissionProgress.RecordAnimalizeRound(player, EventMissionProgress.Counter(player, BoxesKey(tower)), Get(player, tower, DrawCount));
        Set(player, tower, DiffAwardFlag, 1, changes);
        Set(player, tower, State, StateStageEnd, changes);
    }

    /// <summary>Fate buffs of effect type 3 add these items to every win. Value holds tuples, as GetCardBuffItem reads it.</summary>
    private static IEnumerable<int[]> BuffRewards(Player player, Tower tower)
    {
        for (int i = 0; i < BuffSlots; i++)
        {
            int value = Get(player, tower, Buffs + i) & 0xFFFF;
            if (value > 0 && tower.Buffs.TryGetValue(value, out var buff) && buff.Effect == BuffItems)
                foreach (var tuple in ChapterTable.ParseTuples(buff.Value))
                    yield return tuple;
        }
    }

    internal Task Fail(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
            lock (player)
                if (player.Extra.GetValueOrDefault(RunKey) is JsonObject run) run["active"] = false;
        return EventKit.Reply(_ctx, session, cmd, new JsonObject());
    }
}
