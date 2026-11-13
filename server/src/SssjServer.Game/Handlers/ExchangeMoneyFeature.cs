using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The stamina and gold "+" buttons (nType 1 and 2) and the Riki coin purchase (nType 61), all sent as ExChangeMoney.
/// Without a handler the client waited on its blocking spinner forever. Price comes from the row for today's purchase
/// count, tasks (1,12) and (1,13), which reset at the 4 AM day boundary.
/// </summary>
public sealed class ExchangeMoneyFeature : IGameFeature
{
    private const int TypeVigour = 1, TypeGold = 2, TypeRikiCoin = 61;
    private const int Group = 1, VigourTimes = 12, GoldTimes = 13;
    private const int RikiCoinCost = 1000;
    private const string DayKey = "exchangeDay";
    // ui.Error keys the client shows: out of purchases, and not enough diamonds or coffee coins.
    private const int ErrNoTimes = 20020, ErrShort = 20013;

    // Gold purchases can crit. The client shows ui.UI_Exchange.GoldBigSuccess and its burst effect when nBigBoom > 0,
    // and achievement 220 (condition 36, tracked as a maximum) wants a 10x crit. No shipped table holds the odds, so
    // these are inferred: rarer as the multiplier grows, with 10x reachable but uncommon. Rolled once per purchase.
    internal static readonly (int Multiplier, int PerThousand)[] GoldCrits = { (10, 10), (5, 30), (2, 100) };

    /// <summary>Swappable so tests can force a roll. Returns 0..999.</summary>
    internal static Func<int> RollPerThousand = () => Random.Shared.Next(1000);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, (int Cost, int Gain)> _vigour = new(), _gold = new();

    public string Name => "Stamina, gold and Riki coin purchases";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        Load(Path.Combine(directory, "EnergyExchange.txt"), "Energy", _vigour);
        Load(Path.Combine(directory, "GoldExchange.txt"), "Gold", _gold);
        ctx.MapFunction("ExChangeMoney", OnExchange);
        ctx.OnPlayerPrepare((session, player) => ResetDay(player, new()));
    }

    private static void Load(string path, string gainColumn, Dictionary<int, (int, int)> target)
    {
        if (SettingTable.Load(path) is not { } table) return;
        foreach (var row in table.Rows)
            if (table.Int(row, "Times") > 0)
                target[table.Int(row, "Times")] = (table.Int(row, "Cost"), table.Int(row, gainColumn));
    }

    private static void ResetDay(Player player, List<KeyValuePair<uint, int>> changes)
    {
        int today = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        if (player.Extra.GetValueOrDefault(DayKey) is JsonValue stored && stored.TryGetValue<int>(out int storedDay) && storedDay == today) return;   // gcg2#16: a wrong stored type no longer throws
        player.Extra[DayKey] = today;
        EventKit.Write(player, changes, Group, VigourTimes, 0);
        EventKit.Write(player, changes, Group, GoldTimes, 0);
    }

    private Task OnExchange(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "nType"), count = Math.Clamp(EventKit.Number(param, "nCount"), 1, 99);
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            ResetDay(player, changes);
            int crit = 0;
            int error = type switch
            {
                TypeVigour => Buy(session, player, _vigour, VigourTimes, MoneyType.Vigour, "16", count, changes, out _),
                TypeGold => Buy(session, player, _gold, GoldTimes, MoneyType.Gold, "17", count, changes, out crit),
                TypeRikiCoin => BuyRikiCoin(session, player, count),
                _ => ErrNoTimes,
            };
            if (error == 0 || changes.Count > 0) _ctx.Store.Save(player);   // gcg2#16: a refused purchase changed nothing
            if (changes.Count > 0) GameNotify.PushTaskValues(session, changes);
            if (type == TypeRikiCoin && error == 0)
                _ctx.LuaCalls.CallClientFunction(session, "ServerPhoneMsg", new JsonObject { ["nCount"] = count });
            else
                _ctx.LuaCalls.CallClientFunction(session, "MoneyBuyMsg", new JsonObject { ["nError"] = error, ["nBigBoom"] = crit });
        }
        return Task.CompletedTask;
    }

    private int Buy(ClientSession session, Player player, Dictionary<int, (int Cost, int Gain)> rows, int timesTask, int money,
        string counter, int count, List<KeyValuePair<uint, int>> changes, out int bestCrit)
    {
        bestCrit = 0;
        int times = player.GetTask(Group, timesTask);
        int cost = 0;
        var gains = new List<int>(count);
        for (int i = 1; i <= count; i++)
        {
            if (!rows.TryGetValue(times + i, out var row)) return ErrNoTimes;
            cost += row.Cost;
            gains.Add(row.Gain);
        }
        var spent = new List<int>();
        if (!ShopFeature.TrySpendMoney(player, MoneyType.FreeDiamond, cost, spent)) return ErrShort;
        foreach (int id in spent) GameNotify.PushMoney(session, player, id);
        int gain = 0;
        foreach (int baseGain in gains)
        {
            // Only gold crits; the roll happens after payment so a refused purchase never consumes one.
            int multiplier = money == MoneyType.Gold ? RollCrit() : 1;
            gain += baseGain * multiplier;
            if (multiplier > 1) bestCrit = Math.Max(bestCrit, multiplier);
        }
        if (bestCrit > 0) EventMissionProgress.Raise(player, "36", bestCrit);
        player.AddMoney(money, gain);
        GameNotify.PushMoney(session, player, money);
        EventKit.Write(player, changes, Group, timesTask, times + count);
        EventMissionProgress.Bump(player, counter, count);
        return 0;
    }

    internal static int RollCrit()
    {
        int roll = RollPerThousand(), edge = 0;
        foreach (var (multiplier, perThousand) in GoldCrits)
        {
            edge += perThousand;
            if (roll < edge) return multiplier;
        }
        return 1;
    }

    private int BuyRikiCoin(ClientSession session, Player player, int count)
    {
        var spent = new List<int>();
        if (!ShopFeature.TrySpendMoney(player, MoneyType.CoffeeCoin, RikiCoinCost * count, spent)) return ErrShort;
        foreach (int id in spent) GameNotify.PushMoney(session, player, id);
        player.AddMoney(TypeRikiCoin, count);
        GameNotify.PushMoney(session, player, TypeRikiCoin);
        return 0;
    }
}
