using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Reward-only events with no battle of their own: mid-autumn cards (410-413), growth fund (385), day-by-day recruit
/// (386/387) and the barbecue lottery (447). Storage comes from the matching client logic files.
///
/// Fund and recruit normally unlock through a real-money gift, which a private server has no way to charge. With
/// --all-events-open their purchase flags are set at login so the rewards can be claimed.
/// </summary>
public sealed class EventClaimsFeature : IGameFeature
{
    private const int ErrGeneric = 1;

    // MidAutumnActivity.lua
    private const int MidGroup = 79, MidCards = 16, MidFlops = 20, MidActive = 21, MidFinished = 22,
        MidPotBase = 50, MidStoryBase = 100, MidAwardBase = 200;

    // FundActivityLogic.lua
    private const int FundGroup = 73, FundBuyTask = 100, FundMaxId = 10, FundSkippedTask = 104;

    // RecruitKRLogic.lua. Below this task, an award flag takes a whole task per day; at or above, two bits.
    private const int RecruitBitsFrom = 220;

    // LotteryActivity.lua
    private const int LotteryGroup = 66, LotteryRewardBase = 1010;

    private sealed record MidCard(int Id, int Girl, int Task, List<int[]> Reward);
    private sealed record FundRow(int Id, int LockLevel, int[] Levels, List<List<int[]>> Awards);
    private sealed record RecruitDay(int Activity, int Day, int BuyTask, int AwardTask, List<int[]> Reward);
    private sealed record LotteryRow(int Index, List<int[]> Item, int Count, int Weight);
    private sealed record LotteryConfig(int Pool, int Cost, int[] CostItem);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, MidCard> _midCards = new();
    private readonly Dictionary<int, (int Count, List<int[]> Reward)> _midCountAwards = new();
    private readonly Dictionary<int, List<int[]>> _midPlots = new();
    private readonly Dictionary<int, FundRow> _funds = new();
    private readonly Dictionary<int, List<RecruitDay>> _recruits = new();
    private readonly Dictionary<int, LotteryConfig> _lotteries = new();
    private readonly Dictionary<int, List<LotteryRow>> _lotteryPools = new();

    public string Name => "Reward events (mid-autumn, fund, recruit, lottery)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(410, OnMidOpen);
        ctx.MapLuaCall(411, OnMidFinish);
        ctx.MapLuaCall(412, OnMidStory);
        ctx.MapLuaCall(413, OnMidCount);
        ctx.MapLuaCall(385, OnFundAward);
        ctx.MapLuaCall(386, OnRecruitAward);
        ctx.MapLuaCall(387, OnRecruitBuy);
        ctx.MapLuaCall(447, OnLottery);
        ctx.OnPlayerPrepare((session, player) =>
        {
            SeedMidCards(player);
            if (ctx.Options.AllEventsOpen) SetPurchaseFlags(player);
        });
    }

    private void Load(string directory)
    {
        SettingTable? Table(string file) => SettingTable.Load(Path.Combine(directory, file + ".txt"));
        if (Table("MidAutumnTask") is { } cards)
            foreach (var row in cards.Rows)
                if (cards.Int(row, "id") > 0)
                    _midCards[cards.Int(row, "id")] = new(cards.Int(row, "id"), cards.Int(row, "girlid"), cards.Int(row, "taskid"),
                        ChapterTable.ParseTuples(cards.Str(row, "reward")));
        if (Table("TaskCountAward") is { } counts)
            foreach (var row in counts.Rows)
                _midCountAwards[counts.Int(row, "count")] = (counts.Int(row, "count"), ChapterTable.ParseTuples(counts.Str(row, "reward")));
        if (Table("MidAutumnPlot") is { } plots)
            foreach (var row in plots.Rows)
                if (plots.Int(row, "id") > 0)
                    _midPlots[plots.Int(row, "id")] = ChapterTable.ParseTuples(plots.Str(row, "reward"));
        if (Table("FundActivity") is { } funds)
            foreach (var row in funds.Rows)
            {
                int id = funds.Int(row, "TypeId");
                if (id <= 0 || id > FundMaxId) continue;
                var levels = new List<int>();
                var awards = new List<List<int[]>>();
                for (int i = 1; i <= 5; i++)
                {
                    int level = funds.Int(row, "Level" + i, -1);
                    if (level < 0) continue;
                    levels.Add(level);
                    awards.Add(ChapterTable.ParseTuples(funds.Str(row, "Award" + i)));
                }
                _funds[id] = new(id, funds.Int(row, "LockLevel"), levels.ToArray(), awards);
            }
        if (Table("TenDaysRecruit") is { } recruit)
            foreach (var row in recruit.Rows)
            {
                int id = recruit.Int(row, "Id");
                if (id <= 0) continue;
                if (!_recruits.TryGetValue(id, out var days)) _recruits[id] = days = new();
                days.Add(new(id, recruit.Int(row, "Day"), recruit.Int(row, "BuyFlagTask", 200), recruit.Int(row, "AwardFlagTask", 201),
                    ChapterTable.ParseTuples(recruit.Str(row, "Reward"))));
            }
        if (Table("LotteryActivity") is { } lotteries)
            foreach (var row in lotteries.Rows)
                if (lotteries.Int(row, "Id") > 0)
                    _lotteries[lotteries.Int(row, "Id")] = new(lotteries.Int(row, "PoolId"), lotteries.Int(row, "OneCost"),
                        TwentyFirstTowerTable.Numbers(lotteries.Str(row, "OneCostItem")));
        if (Table("LotteryPool") is { } pools)
            foreach (var row in pools.Rows)
            {
                int pool = pools.Int(row, "PoolId");
                if (pool <= 0) continue;
                if (!_lotteryPools.TryGetValue(pool, out var list)) _lotteryPools[pool] = list = new();
                list.Add(new(pools.Int(row, "Index"), ChapterTable.ParseTuples(pools.Str(row, "RewardItem")), pools.Int(row, "Count"), pools.Int(row, "Weight")));
            }
    }

    // ================================================================ mid-autumn

    /// <summary>The board is 16 face-down cards. Update() reads each position's card id, so they are dealt once.</summary>
    private void SeedMidCards(Player player)
    {
        if (_midCards.Count == 0 || Enumerable.Range(1, MidCards).Any(i => player.GetTask(MidGroup, i) > 0)) return;
        var deck = _midCards.Keys.OrderBy(_ => Random.Shared.Next()).Take(MidCards).ToArray();
        for (int i = 0; i < deck.Length; i++) player.SetTask(MidGroup, i + 1, deck[i]);
    }

    /// <summary>A girl card starts its mission; a card with no girl pays its reward as soon as it turns over.</summary>
    private Task OnMidOpen(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int pos = EventKit.Number(param, "posID");
        lock (player)
        {
            if (pos < 1 || pos > MidCards || player.GetTask(MidGroup, MidPotBase + pos) > 0
                || !_midCards.TryGetValue(player.GetTask(MidGroup, pos), out var card))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            EventKit.Write(player, changes, MidGroup, MidPotBase + pos, 1);
            EventKit.Write(player, changes, MidGroup, MidFlops, player.GetTask(MidGroup, MidFlops) + 1);
            if (card.Girl > 0) EventKit.Write(player, changes, MidGroup, MidActive, player.GetTask(MidGroup, MidActive) + 1);
            else _ctx.Rewards.Grant(player, card.Reward, RewardSource.Extra, rewards);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>nTaskID is the card's AchievementOther mission. Its claim bit is what IsCardFinish reads back as done.</summary>
    private Task OnMidFinish(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int task = EventKit.Number(param, "nTaskID");
        lock (player)
        {
            if (!_ctx.Missions.AchieveOther.TryGetValue(task, out var mission) || EventKit.HasBit(player, mission.Group, mission.Id, 0))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, mission.Group, mission.Id, 0);
            EventKit.Write(player, changes, MidGroup, MidActive, Math.Max(0, player.GetTask(MidGroup, MidActive) - 1));
            EventKit.Write(player, changes, MidGroup, MidFinished, player.GetTask(MidGroup, MidFinished) + 1);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnMidStory(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int story = EventKit.Number(param, "nStoryId");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            if (player.GetTask(MidGroup, MidStoryBase + story) <= 0)
            {
                _ctx.Rewards.Grant(player, _midPlots.GetValueOrDefault(story), RewardSource.Extra, rewards);
                EventKit.Write(player, changes, MidGroup, MidStoryBase + story, 1);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnMidCount(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int count = EventKit.Number(param, "nTaskCount");
        lock (player)
        {
            if (!_midCountAwards.TryGetValue(count, out var award) || player.GetTask(MidGroup, MidAwardBase + count) > 0
                || player.GetTask(MidGroup, MidFinished) < count)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, award.Reward, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, MidGroup, MidAwardBase + count, 1);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ fund and recruit

    private void SetPurchaseFlags(Player player)
    {
        foreach (int fund in _funds.Keys)
            player.SetTask(FundGroup, FundBuyTask, player.GetTask(FundGroup, FundBuyTask) | 1 << fund);
        foreach (int task in _recruits.Values.SelectMany(days => days).Select(d => d.BuyTask).Distinct())
            if (player.GetTask(FundGroup, task) == 0)
                player.SetTask(FundGroup, task, 1);
    }

    /// <summary>GetAwardFlag: task 100+id holds one bit per reward, with task 104 skipped because it is the red-dot flag.</summary>
    private Task OnFundAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId"), index = EventKit.Number(param, "nIdx");
        lock (player)
        {
            if (!_funds.TryGetValue(id, out var fund) || index < 1 || index > fund.Levels.Length
                || !EventKit.HasBit(player, FundGroup, FundBuyTask, id) || player.Level < fund.Levels[index - 1])
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            int task = FundBuyTask + id;
            if (task >= FundSkippedTask) task++;
            if (EventKit.HasBit(player, FundGroup, task, index))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, fund.Awards[index - 1], RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, FundGroup, task, index);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["nId"] = id, ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>The client only sends this on a path it has disabled in favour of the gift, but its reply is defined.</summary>
    private Task OnRecruitBuy(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId");
        lock (player)
        {
            if (!_recruits.TryGetValue(id, out var days) || days.Count == 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, FundGroup, days[0].BuyTask, 1);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["nId"] = id, ["nIdx"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>A flag value of 1 means claimed under both storage shapes GetAwardFlag reads.</summary>
    private Task OnRecruitAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId"), dayNumber = EventKit.Number(param, "nIdx");
        lock (player)
        {
            if (!_recruits.TryGetValue(id, out var days) || days.FirstOrDefault(d => d.Day == dayNumber) is not { } day
                || player.GetTask(FundGroup, day.BuyTask) == 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            int offset = day.Day - 1;
            var changes = new List<KeyValuePair<uint, int>>();
            if (day.AwardTask < RecruitBitsFrom)
            {
                if (player.GetTask(FundGroup, day.AwardTask + offset) != 0)
                    return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
                EventKit.Write(player, changes, FundGroup, day.AwardTask + offset, 1);
            }
            else
            {
                int bit = offset % 16 * 2;
                if ((player.GetTask(FundGroup, day.AwardTask) >> bit & 3) != 0)
                    return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
                EventKit.Write(player, changes, FundGroup, day.AwardTask, player.GetTask(FundGroup, day.AwardTask) | 1 << bit);
            }
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, day.Reward, RewardSource.Extra, rewards);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes,
                new JsonObject { ["nId"] = id, ["nIdx"] = dayNumber, ["tbAward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ lottery

    /// <summary>
    /// Each row has limited stock (task 1010+index counts draws), and the screen shows what is left. The cost is the
    /// ticket when the player has one, or diamonds otherwise, as DoLottery checks before sending.
    /// INFERRED: WeightActTime and MaxTime are ignored, because no client code shows how they shift the odds.
    /// </summary>
    private Task OnLottery(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "id"), times = Math.Max(1, EventKit.Number(param, "time"));
        lock (player)
        {
            if (!_lotteries.TryGetValue(id, out var lottery) || !_lotteryPools.TryGetValue(lottery.Pool, out var pool))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var stock = pool.Select(r => r.Count - player.GetTask(LotteryGroup, LotteryRewardBase + r.Index)).ToArray();
            times = Math.Min(times, stock.Where(s => s > 0).Sum());
            if (times <= 0) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            if (!EventKit.Spend(_ctx, session, player, lottery.CostItem, times))
            {
                int diamonds = lottery.Cost * times;
                if (player.GetMoney(MoneyType.FreeDiamond) < diamonds)
                    return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
                player.AddMoney(MoneyType.FreeDiamond, -diamonds);
                GameNotify.PushMoney(session, player, MoneyType.FreeDiamond);
            }
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            var weights = pool.Select(r => r.Weight).ToArray();
            for (int n = 0; n < times; n++)
            {
                int pick = EventKit.WeightedPick(weights, i => stock[i] > 0);
                if (pick < 0) break;
                stock[pick]--;
                _ctx.Rewards.Grant(player, pool[pick].Item, RewardSource.Extra, rewards);
                int task = LotteryRewardBase + pool[pick].Index;
                EventKit.Write(player, changes, LotteryGroup, task, player.GetTask(LotteryGroup, task) + 1);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbRewards"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }
}
