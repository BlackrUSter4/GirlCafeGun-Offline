using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>The pledge stage shop (229). Each row costs one item or one currency and has a lifetime buy limit.</summary>
public sealed class PledgeExchangeFeature : IGameFeature
{
    // pldgecommon.lua: nTaskGroup and nExchangeStart.
    private const int Group = 60, BoughtBase = 100;
    private const int ErrInvalid = 29998, ErrShort = 20205;

    private sealed record Row(int Id, int Limit, int[] Money, int[] SubItem, int[] Award);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, Row> _rows = new();

    public string Name => "Pledge exchange shop (229)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        if (SettingTable.Load(Path.Combine(Path.GetFullPath(ctx.Options.SettingDirectory), "PldgeExchange.txt")) is { } table)
            foreach (var row in table.Rows)
            {
                var award = TwentyFirstTowerTable.Numbers(table.Str(row, "AwardItem"));
                // The client drops rows without a full, positive award, so they can never be bought.
                if (table.Int(row, "Id") is > 0 and var id && award.Length == 5 && award[4] > 0)
                    _rows[id] = new(id, table.Int(row, "LimitNum"), TwentyFirstTowerTable.Numbers(table.Str(row, "MoneyId")),
                        TwentyFirstTowerTable.Numbers(table.Str(row, "SubItem")), award);
            }
        ctx.MapLuaCall(229, OnExchange);
    }

    private Task OnExchange(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "Id"), num = EventKit.Number(param, "Num");
        lock (player)
        {
            if (!_rows.TryGetValue(id, out var row) || num <= 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrInvalid });
            int bought = player.GetTask(Group, BoughtBase + id);
            if (row.Limit > 0 && bought + num > row.Limit)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrInvalid });
            if (!Pay(session, player, row, num))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrShort });

            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, Group, BoughtBase + id, bought + num);
            var award = new[] { new[] { row.Award[0], row.Award[1], row.Award[2], row.Award[3], row.Award[4] * num } };
            var rewards = _ctx.Rewards.Grant(player, award, RewardSource.Extra);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["tbAwardItem"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>The client prefers the item cost when a row names both.</summary>
    private bool Pay(ClientSession session, Player player, Row row, int num)
    {
        if (row.SubItem.Length == 5)
            return EventKit.Spend(_ctx, session, player, row.SubItem, row.SubItem[4] * num);
        if (row.Money.Length != 2) return false;
        var spent = new List<int>();
        if (!ShopFeature.TrySpendMoney(player, row.Money[0], row.Money[1] * num, spent)) return false;
        foreach (int money in spent) GameNotify.PushMoney(session, player, money);
        return true;
    }
}
