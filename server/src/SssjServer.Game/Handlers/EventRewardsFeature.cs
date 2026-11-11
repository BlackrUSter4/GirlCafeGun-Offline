using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Event missions (month 436, music 374), linkage rewards (404/405), Gemini birthday (515-519), weapon card (416/417),
/// handwork (392/393), return events (310-312, 400) and mixed-online extras (454/455).
/// </summary>
public sealed class EventRewardsFeature : IGameFeature, IGamePulseFeature
{
    private const int ErrGeneric = 20045;

    // MonthMission.lua / MusicMission.lua
    private const int MonthGroup = 85, MusicGroup = 70;
    // LinkageActivityLogic.lua
    private const int LinkageGroup = 63, LinkageGachaBase = 3200, LinkagePondSize = 20;
    // GeminiBirthdayLogic.lua
    private const int GeminiGroup = 84, BlessYuki = 51, BlessWei = 52, GeminiMissions = 53, GeminiDailyBase = 60, GeminiPond1 = 101, GeminiPond2 = 201;
    // WeaponWeeklyCardLogic.lua
    private const int FundGroup = 73, CardBuyTask = 222, CardAwardTask = 223, CardDays = 7;
    // HandWorkLogic.lua
    private const int HandGroup = 66, HandMakeTask = 401, HandNodeTask = 402;
    // ReturnActivityLogic.lua
    private const int ReturnGroup = 63, ReturnTimeTask = 1, ReturnPointTask = 2, ReturnFlagTask = 3;
    /// <summary>INFERRED: the original server held how many active points a return mission pays.</summary>
    private const int ReturnMissionPoints = 5;
    // ReturnActivityLogic2.lua
    private const int Return2Flag = 1001, Return2Claimed = 1002;
    private static readonly int[][] Return2Rewards = { new[] { 15, 2, 1, 1, 200 }, new[] { 10, 1, 1, 2, 4 }, new[] { 7, 1, 4, 3, 5 }, new[] { 7, 3, 1, 3, 5 }, new[] { 15, 1, 1, 1, 20000 } };

    private sealed record EventMission(int Group, int Id, int AchieveType, List<int> Counts, int Key, List<int[]> Rewards);
    private sealed record PondRow(List<int[]> Item, bool Sp, int Weight, int Count);
    private sealed record GeminiConfig(int Pool1, int Pool2, int Currency, int BlessMax, int[] DailyMissions, int DailyCount);
    private sealed record GeminiGift(int Index, int[] Gdpl, int Bless1, int Bless2, int LimitCount, int LimitlessCount, int Stuff1, int Stuff1Num, int Stuff2, int Stuff2Num, int BuyCost);
    private sealed record GeminiMission(int Girl, int Bless, int Currency, List<int[]> Rewards);
    private sealed record GeminiPoolRow(int Pond, bool Limitless, List<int[]> Item, int Weight, int Count);
    private sealed record HandConfig(int Currency, int Cost, int[] NorGift, int[] GachaNums, List<List<int[]>> GachaRewards);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<(int Group, int Id), EventMission> _missions = new();
    private readonly Dictionary<int, List<PondRow>> _linkagePonds = new();
    private (int Cost, int Money, int Times)? _linkageToken;
    private readonly Dictionary<int, GeminiConfig> _gemini = new();
    private readonly Dictionary<(int Act, int Index), GeminiGift> _geminiGifts = new();
    private readonly Dictionary<(int Act, int Mission), GeminiMission> _geminiMissions = new();
    private readonly Dictionary<int, List<GeminiPoolRow>> _geminiPools = new();
    private readonly Dictionary<int, List<int[]>> _cardDays = new();
    private readonly Dictionary<int, HandConfig> _handwork = new();
    private readonly List<int> _returnSign = new();
    private readonly Dictionary<int, (int Points, List<int[]> Rewards)> _returnAwards = new();

    public string Name => "Event rewards (missions, linkage, Gemini, weapon card, handwork, return, mix)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        EventMissionProgress.Context = ctx;
        Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(436, (s, c, p) => OnMission(s, c, p, MonthGroup));
        ctx.MapLuaCall(374, (s, c, p) => OnMission(s, c, p, MusicGroup));
        ctx.MapLuaCall(404, OnLinkageMission);
        ctx.MapLuaCall(405, OnLinkageGacha);
        ctx.MapLuaCall(515, OnMakeCake);
        ctx.MapLuaCall(516, OnSendCake);
        ctx.MapLuaCall(517, OnBlessMission);
        ctx.MapLuaCall(518, OnServerBless);
        ctx.MapLuaCall(519, OnGeminiAchievement);
        ctx.MapLuaCall(416, OnCardAward);
        ctx.MapLuaCall(417, OnCardBuy);
        ctx.MapLuaCall(392, OnHandMake);
        ctx.MapLuaCall(393, OnHandReward);
        ctx.MapLuaCall(310, OnReturnSign);
        ctx.MapLuaCall(311, OnReturnMission);
        ctx.MapLuaCall(312, OnReturnPoints);
        ctx.MapLuaCall(400, OnReturn2);
        ctx.MapLuaCall(454, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonObject { ["spCount"] = 0 }));
        ctx.MapLuaCall(455, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonArray()));
        if (ctx.Options.AllEventsOpen) ctx.OnPlayerPrepare((session, player) => Prepare(player));
    }

    private void Load(string directory)
    {
        SettingTable? Table(params string[] path) => SettingTable.Load(Path.Combine(new[] { directory }.Concat(path).ToArray()) + ".txt");
        LoadMissions(Table("MonthMission"), MonthGroup);
        LoadMissions(Table("MusicMission"), MusicGroup);
        if (Table("_scoped", "activities", "linkageactivity", "towerpond") is { } linkage)
            foreach (var row in linkage.Rows)
            {
                int pond = linkage.Int(row, "PondId");
                if (pond <= 0) continue;
                if (!_linkagePonds.TryGetValue(pond, out var list)) _linkagePonds[pond] = list = new();
                list.Add(new(ChapterTable.ParseTuples(linkage.Str(row, "Item")), linkage.Int(row, "Sp") == 1, linkage.Int(row, "Weight"), linkage.Int(row, "Count")));
            }
        if (Table("_scoped", "activities", "linkageactivity", "towerpondtoken") is { } token && token.Rows.Count > 0)
            _linkageToken = (token.Int(token.Rows[0], "CostCount"), token.Int(token.Rows[0], "CostType"), Math.Max(1, token.Int(token.Rows[0], "LotteryTimes")));
        if (Table("GeminiBirthday") is { } gemini)
            foreach (var row in gemini.Rows)
                if (gemini.Int(row, "Id") > 0)
                    _gemini[gemini.Int(row, "Id")] = new(gemini.Int(row, "RewardPoolId1"), gemini.Int(row, "RewardPoolId2"), gemini.Int(row, "CurrencyType"), gemini.Int(row, "BlessMax"),
                        TwentyFirstTowerTable.Numbers(gemini.Str(row, "DailyMission")), gemini.Int(row, "DailyMissionCount"));
        if (Table("GiftItem") is { } gifts)
            foreach (var row in gifts.Rows)
                _geminiGifts[(gifts.Int(row, "ActId"), gifts.Int(row, "Idx"))] = new(gifts.Int(row, "Idx"), TwentyFirstTowerTable.Numbers(gifts.Str(row, "GDPL")),
                    gifts.Int(row, "BlessVal1"), gifts.Int(row, "BlessVal2"), gifts.Int(row, "LimitCount"), gifts.Int(row, "LimitlessCount"),
                    gifts.Int(row, "Stuff1Id"), gifts.Int(row, "Stuff1Num"), gifts.Int(row, "Stuff2Id"), gifts.Int(row, "Stuff2Num"), gifts.Int(row, "BuyCost"));
        if (Table("BlessingMission") is { } bless)
            foreach (var row in bless.Rows)
                _geminiMissions[(bless.Int(row, "ActId"), bless.Int(row, "MissionId"))] = new(bless.Int(row, "Girl"), bless.Int(row, "BlessValue"), bless.Int(row, "CurrencyCount"), ChapterTable.ParseTuples(bless.Str(row, "Rewards")));
        if (Table("RewardsPool") is { } pool)
            foreach (var row in pool.Rows)
            {
                int id = pool.Int(row, "Id");
                if (id <= 0) continue;
                if (!_geminiPools.TryGetValue(id, out var list)) _geminiPools[id] = list = new();
                list.Add(new(pool.Int(row, "PondId"), pool.Int(row, "Limitless") == 1, ChapterTable.ParseTuples(pool.Str(row, "OtherItem")), pool.Int(row, "Weight"), pool.Int(row, "Count")));
            }
        if (Table("WeaponWeeklyCard") is { } card)
            foreach (var row in card.Rows)
                _cardDays[card.Int(row, "DayId", -1)] = ChapterTable.ParseTuples(card.Str(row, "Award"));
        if (Table("HandWorkActivity") is { } hand)
            foreach (var row in hand.Rows)
            {
                int id = hand.Int(row, "Id");
                if (id <= 0) continue;
                var nums = TwentyFirstTowerTable.Numbers(hand.Str(row, "GachaNum"));
                _handwork[id] = new(hand.Int(row, "CurrencyId"), hand.Int(row, "CurrencyCost"), TwentyFirstTowerTable.Numbers(hand.Str(row, "NorGift")), nums,
                    nums.Select((_, i) => ChapterTable.ParseTuples(hand.Str(row, "GachaReward" + (i + 1)))).ToList());
            }
        if (Table("SignActivity") is { } sign)
            foreach (var row in sign.Rows)
                if (sign.Int(row, "Achievement") > 0)
                    _returnSign.Add(sign.Int(row, "Achievement"));
        if (Table("_scoped", "returnactivity", "missionaward") is { } award)
            foreach (var row in award.Rows)
                if (award.Int(row, "Id") > 0)
                    _returnAwards[award.Int(row, "Id")] = (award.Int(row, "Point"), ChapterTable.ParseTuples(award.Str(row, "Rewards")));
    }

    private void LoadMissions(SettingTable? table, int group)
    {
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "missionId");
            if (id <= 0) continue;
            var counts = TwentyFirstTowerTable.Numbers(table.Str(row, "missionMax")).ToList();
            var assist = TwentyFirstTowerTable.Numbers(table.Str(row, "assistparam"));
            _missions[(group, id)] = new(group, id, table.Int(row, "achieveType"), counts, assist.Length > 0 ? assist[0] : 1,
                ChapterTable.ParseTuples(table.Str(row, "awardsList")));
        }
    }

    /// <summary>Marks the player eligible for the return events, and deals the day's Gemini missions.</summary>
    private void Prepare(Player player)
    {
        player.SetTask(ReturnGroup, ReturnTimeTask, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        player.SetTask(ReturnGroup, Return2Flag, 1);
        foreach (var (_, config) in _gemini)
        {
            var missions = config.DailyMissions.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(config.DailyCount, 10)).ToArray();
            if (Enumerable.Range(1, 10).Any(i => player.GetTask(GeminiGroup, GeminiDailyBase + i) > 0)) break;
            for (int i = 0; i < missions.Length; i++) player.SetTask(GeminiGroup, GeminiDailyBase + i + 1, missions[i]);
            break;
        }
    }

    // ================================================================ month and music missions

    void IGamePulseFeature.OnPlayerPulse(ClientSession session, Player player)
    {
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            foreach (var mission in _missions.Values)
            {
                int derived = EventMissionProgress.Derive(player, mission.AchieveType, mission.Counts);
                if (derived < 0) continue;
                int stored = player.GetTask(mission.Group, mission.Id);
                if (derived > MissionFeature.ProgressOf(stored))
                    EventKit.Write(player, changes, mission.Group, mission.Id, derived << 1 | MissionFeature.ClaimedOf(stored));
            }
            if (changes.Count > 0) GameNotify.PushTaskValues(session, changes);
        }
    }

    /// <summary>A request carries nId for one mission or tbList for several. Type is echoed to pick the error wording.</summary>
    private Task OnMission(ClientSession session, int cmd, JsonNode? param, int group)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "Type");
        var ids = (param as JsonObject)?["tbList"] is JsonArray list
            ? ChallengeTowerFeature.ReadPositional(list) : new List<int> { EventKit.Number(param, "nId") };
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            foreach (int id in ids)
            {
                if (!_missions.TryGetValue((group, id), out var mission)) continue;
                int value = player.GetTask(group, id);
                int required = mission.Key >= 1 && mission.Key <= mission.Counts.Count ? mission.Counts[mission.Key - 1] : int.MaxValue;
                if (MissionFeature.ClaimedOf(value) == 1 || MissionFeature.ProgressOf(value) < required) continue;
                _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
                EventKit.SetBit(player, changes, group, id, 0);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Type"] = type, ["tbItem"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>Pays a completed AchievementOther mission once. Null when it is not complete or already claimed.</summary>
    private RewardGrantResult? ClaimOther(Player player, int id, List<KeyValuePair<uint, int>> changes)
    {
        var mission = _ctx.Missions.GetAchieveOther(id);
        if (mission is null) return null;
        int value = player.GetTask(mission.Group, id);
        if (MissionFeature.ClaimedOf(value) == 1 || MissionFeature.ProgressOf(value) < mission.RequiredCount(true)) return null;
        var rewards = new RewardGrantResult();
        _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
        EventKit.SetBit(player, changes, mission.Group, id, 0);
        return rewards;
    }

    // ================================================================ linkage

    private Task OnLinkageMission(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.NumberAt(param, 1);
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            if (ClaimOther(player, id, changes) is not { } rewards)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 12 });
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbReward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>OnGachaRsp treats a sixth tuple value as "rare" for its effect, so only rare slots get one.</summary>
    private Task OnLinkageGacha(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int pond = EventKit.Number(param, "nPondId");
        lock (player)
        {
            if (!_linkagePonds.TryGetValue(pond, out var rows) || _linkageToken is not { } token)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 9 });
            int[] drawn = rows.Select((_, i) => player.GetTask(LinkageGroup, LinkageGachaBase + (pond - 1) * LinkagePondSize + i + 1)).ToArray();
            if (drawn.Where((d, i) => d < rows[i].Count).Count() == 0) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 9 });
            if (player.GetMoney(token.Money) < token.Cost) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 8 });
            player.AddMoney(token.Money, -token.Cost);
            GameNotify.PushMoney(session, player, token.Money);
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            var items = new JsonArray();
            for (int n = 0; n < token.Times; n++)
            {
                int pick = EventKit.WeightedPick(rows.Select(r => r.Weight).ToArray(), i => drawn[i] < rows[i].Count);
                if (pick < 0) break;
                drawn[pick]++;
                _ctx.Rewards.Grant(player, rows[pick].Item, RewardSource.Extra, rewards);
                EventKit.Write(player, changes, LinkageGroup, LinkageGachaBase + (pond - 1) * LinkagePondSize + pick + 1, drawn[pick]);
                foreach (var tuple in rows[pick].Item)
                {
                    var entry = new JsonArray(tuple.Take(5).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    if (rows[pick].Sp) entry.Add(1);
                    items.Add(entry);
                }
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbItem"] = items });
        }
        return Task.CompletedTask;
    }

    // ================================================================ Gemini birthday

    /// <summary>Stuff2 is bought with diamonds for any shortfall, which is the dialog the cake screen shows.</summary>
    private Task OnMakeCake(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int act = EventKit.NumberAt(param, 0), index = EventKit.NumberAt(param, 1), num = EventKit.NumberAt(param, 2);
        lock (player)
        {
            if (num <= 0 || !_geminiGifts.TryGetValue((act, index), out var gift)
                || !_geminiGifts.TryGetValue((act, gift.Stuff1), out var stuff1) || !_geminiGifts.TryGetValue((act, gift.Stuff2), out var stuff2))
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            int need2 = num * gift.Stuff2Num;
            long have2 = player.Items.Where(i => stuff2.Gdpl.Length >= 4 && i.Genre == stuff2.Gdpl[0] && i.Detail == stuff2.Gdpl[1] && i.Particular == stuff2.Gdpl[2] && i.Level == stuff2.Gdpl[3]).Sum(i => (long)i.Count);
            int buy = (int)Math.Max(0, need2 - have2);
            int diamonds = buy * stuff2.BuyCost;
            if (player.GetMoney(MoneyType.FreeDiamond) < diamonds || !EventKit.Spend(_ctx, session, player, stuff1.Gdpl, num * gift.Stuff1Num))
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            if (diamonds > 0)
            {
                player.AddMoney(MoneyType.FreeDiamond, -diamonds);
                GameNotify.PushMoney(session, player, MoneyType.FreeDiamond);
            }
            EventKit.Spend(_ctx, session, player, stuff2.Gdpl, need2 - buy);
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, new[] { new[] { gift.Gdpl[0], gift.Gdpl[1], gift.Gdpl[2], gift.Gdpl[3], num } }, RewardSource.Extra, rewards);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, EventKit.Tuples(rewards));
        }
        return Task.CompletedTask;
    }

    /// <summary>Each cake raises that girl's blessing and draws from her pool, limited slots first, then endless ones.</summary>
    private Task OnSendCake(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int act = EventKit.NumberAt(param, 0), girl = EventKit.NumberAt(param, 1), index = EventKit.NumberAt(param, 2), num = EventKit.NumberAt(param, 3);
        lock (player)
        {
            if (num <= 0 || !_gemini.TryGetValue(act, out var config) || !_geminiGifts.TryGetValue((act, index), out var gift)
                || !EventKit.Spend(_ctx, session, player, gift.Gdpl, num))
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            int task = girl == 1 ? BlessYuki : BlessWei;
            int gain = (girl == 1 ? gift.Bless1 : gift.Bless2) * num;
            EventKit.Write(player, changes, GeminiGroup, task, Math.Min(config.BlessMax, player.GetTask(GeminiGroup, task) + gain));
            int pool = girl == 1 ? config.Pool1 : config.Pool2, pondTask = girl == 1 ? GeminiPond1 : GeminiPond2;
            for (int i = 0; i < num; i++)
                DrawGemini(player, pool, pondTask, gift, rewards, changes);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, EventKit.Tuples(rewards));
        }
        return Task.CompletedTask;
    }

    private void DrawGemini(Player player, int pool, int pondTask, GeminiGift gift, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changes)
    {
        if (!_geminiPools.TryGetValue(pool, out var rows)) return;
        int pond = Math.Max(1, player.GetTask(GeminiGroup, pondTask));
        var limited = rows.Where(r => !r.Limitless && r.Pond == pond).ToList();
        var endless = rows.Where(r => r.Limitless).ToList();
        for (int i = 0; i < gift.LimitCount && limited.Count > 0; i++)
        {
            var drawn = limited.Select((_, s) => player.GetTask(GeminiGroup, pondTask + 1 + s)).ToArray();
            int pick = EventKit.WeightedPick(limited.Select(r => r.Weight).ToArray(), s => drawn[s] < limited[s].Count);
            if (pick < 0) break;
            _ctx.Rewards.Grant(player, limited[pick].Item, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, GeminiGroup, pondTask + 1 + pick, drawn[pick] + 1);
        }
        for (int i = 0; i < gift.LimitlessCount && endless.Count > 0; i++)
            _ctx.Rewards.Grant(player, endless[EventKit.WeightedPick(endless.Select(r => r.Weight).ToArray(), _ => true)].Item, RewardSource.Extra, rewards);
    }

    private Task OnBlessMission(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int act = EventKit.NumberAt(param, 0), id = EventKit.NumberAt(param, 1);
        lock (player)
        {
            if (!_gemini.TryGetValue(act, out var config) || !_geminiMissions.TryGetValue((act, id), out var mission)
                || EventKit.HasBit(player, GeminiGroup, GeminiMissions, id)
                || player.GetMoney(config.Currency) < mission.Currency
                || player.GetTask(GeminiGroup, mission.Girl == 1 ? BlessYuki : BlessWei) < mission.Bless)
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, GeminiGroup, GeminiMissions, id);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, EventKit.Tuples(rewards));
        }
        return Task.CompletedTask;
    }

    /// <summary>Server-wide blessing is {yuki, wei, max}. Here that is this player's own blessing.</summary>
    private Task OnServerBless(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int max = _gemini.Values.Select(g => g.BlessMax).DefaultIfEmpty(0).First();
        return EventKit.Reply(_ctx, session, cmd, new JsonArray(player.GetTask(GeminiGroup, BlessYuki), player.GetTask(GeminiGroup, BlessWei), max));
    }

    private Task OnGeminiAchievement(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            if (ClaimOther(player, EventKit.Number(param, "nId"), changes) is not { } rewards)
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, EventKit.Tuples(rewards));
        }
        return Task.CompletedTask;
    }

    // ================================================================ weapon card

    /// <summary>The card lasts seven days from purchase, and each day's award is one bit (GetAwardFlag).</summary>
    private Task OnCardAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int day = EventKit.Number(param, "Id");
        lock (player)
        {
            if (day is < 1 or > CardDays || !_cardDays.TryGetValue(day, out var award) || EventKit.HasBit(player, FundGroup, CardAwardTask, day))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = 20114 });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, award, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, FundGroup, CardAwardTask, day);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>There is no payment channel, so buying just starts the seven days. Error 0 with no Type ends the trade flow.</summary>
    private Task OnCardBuy(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, FundGroup, CardBuyTask, (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            EventKit.Write(player, changes, FundGroup, CardAwardTask, 0);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    // ================================================================ handwork

    /// <summary>LuaCall 392 has no client branch: the result comes back through 393's handler (OnGetRewardCallback).</summary>
    private Task OnHandMake(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId"), num = EventKit.Number(param, "nNum");
        lock (player)
        {
            if (num <= 0 || !_handwork.TryGetValue(id, out var config) || config.NorGift.Length < 4 || player.GetMoney(config.Currency) < config.Cost * num)
                return Task.CompletedTask;
            player.AddMoney(config.Currency, -config.Cost * num);
            GameNotify.PushMoney(session, player, config.Currency);
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, new[] { new[] { config.NorGift[0], config.NorGift[1], config.NorGift[2], config.NorGift[3], num } }, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, HandGroup, HandMakeTask, player.GetTask(HandGroup, HandMakeTask) + num);
            EventKit.Commit(_ctx, session, player, 393, rewards, changes, new JsonObject { ["tbReward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnHandReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId"), index = EventKit.Number(param, "nIdx");
        lock (player)
        {
            if (!_handwork.TryGetValue(id, out var config) || index < 1 || index > config.GachaNums.Length
                || EventKit.HasBit(player, HandGroup, HandNodeTask, index) || player.GetTask(HandGroup, HandMakeTask) < config.GachaNums[index - 1])
                return EventKit.Reply(_ctx, session, cmd, new JsonObject());
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, config.GachaRewards[index - 1], RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, HandGroup, HandNodeTask, index);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbReward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ return events

    /// <summary>One sign-in per day claims the next unclaimed day, whose mission needs no progress of its own.</summary>
    private Task OnReturnSign(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            string day = DateTimeOffset.Now.AddHours(-4).ToString("yyyyMMdd");
            string? last = player.Extra.GetValueOrDefault("returnSignDay")?.GetValue<string>();
            int next = _returnSign.FirstOrDefault(id => MissionFeature.ClaimedOf(player.GetTask(MissionTable.OtherGroup, id)) == 0);
            var mission = _ctx.Missions.GetAchieveOther(next);
            if (last == day || mission is null) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["tbAwards"] = new JsonArray() });
            player.Extra["returnSignDay"] = day;
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, MissionTable.OtherGroup, next, 0);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAwards"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnReturnMission(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            if (ClaimOther(player, EventKit.Number(param, "nAchieveId"), changes) is not { } rewards)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["tbAwards"] = new JsonArray() });
            EventKit.Write(player, changes, ReturnGroup, ReturnPointTask, player.GetTask(ReturnGroup, ReturnPointTask) + ReturnMissionPoints);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAwards"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnReturnPoints(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId");
        lock (player)
        {
            if (!_returnAwards.TryGetValue(id, out var award) || EventKit.HasBit(player, ReturnGroup, ReturnFlagTask, id)
                || player.GetTask(ReturnGroup, ReturnPointTask) < award.Points)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["tbAwards"] = new JsonArray() });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, award.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, ReturnGroup, ReturnFlagTask, id);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAwards"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnReturn2(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (player.GetTask(ReturnGroup, Return2Claimed) == 1) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["ok"] = false });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, Return2Rewards, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, ReturnGroup, Return2Claimed, 1);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["ok"] = true });
        }
        return Task.CompletedTask;
    }
}
