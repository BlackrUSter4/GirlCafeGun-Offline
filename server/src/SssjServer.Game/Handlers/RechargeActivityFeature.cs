using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// First recharge reward (179) and the diamond-bought growth fund (180), from the welfare page. The fund here is
/// ActivityList 25, separate from FundActivity (385) in EventClaimsFeature.
///
/// First recharge is unlocked by a real payment, and this server has no way to take one. So task (1,30) reaches its
/// "can claim" state only through --all-events-open, at login, and the reward is otherwise out of reach.
/// The fund costs in-game diamonds, so it charges them in full.
/// </summary>
public sealed class RechargeActivityFeature : IGameFeature
{
    internal const int Group = 1, FirstRechargeTask = 30, FundTask = 31, FundActivity = 25;
    private const int FirstRechargeRow = 1, NotPaid = 0, CanClaim = 1, Claimed = 2;
    private const int ErrGeneric = 1;
    private const int ErrNotEnoughDiamond = 20013;

    private GameFeatureContext _ctx = null!;
    private List<int[]> _firstRechargeAwards = new();
    private int _fundMoney, _fundCost;

    public string Name => "First recharge reward and growth fund (179, 180)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "FisrtRechargeActivity.txt")) is { } first)
            foreach (var row in first.Rows)
                if (first.Int(row, "Id") == FirstRechargeRow)
                    _firstRechargeAwards = ChapterTable.ParseTuples(first.Str(row, "awardsList"));
        if (SettingTable.Load(Path.Combine(directory, "ActivityList.txt")) is { } list)
            foreach (var row in list.Rows)
                if (list.Int(row, "Id") == FundActivity && TwentyFirstTowerTable.Numbers(list.Str(row, "OtherSetting")) is { Length: 2 } cost)
                    (_fundMoney, _fundCost) = (cost[0], cost[1]);

        ctx.MapLuaCall(179, OnFirstRecharge);
        ctx.MapLuaCall(180, OnBuyFund);
        ctx.OnPlayerPrepare((session, player) =>
        {
            if (ctx.Options.AllEventsOpen && player.GetTask(Group, FirstRechargeTask) == NotPaid)
                player.SetTask(Group, FirstRechargeTask, CanClaim);
        });
    }

    private Task OnFirstRecharge(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (player.GetTask(Group, FirstRechargeTask) != CanClaim)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, Group, FirstRechargeTask, Claimed);
            var rewards = _ctx.Rewards.Grant(player, _firstRechargeAwards, RewardSource.Extra);
            // The client shows the reward list from its own table, so the reply only has to be a table with no nError.
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    private Task OnBuyFund(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (_fundCost <= 0 || player.GetTask(Group, FundTask) == 1)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var spent = new List<int>();
            // Free and paid diamonds count as one pool, the same check the client's buy dialog makes.
            if (!ShopFeature.TrySpendMoney(player, _fundMoney, _fundCost, spent))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrNotEnoughDiamond });
            foreach (int money in spent) GameNotify.PushMoney(session, player, money);
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, Group, FundTask, 1);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }
}
