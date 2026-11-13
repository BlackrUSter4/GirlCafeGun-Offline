using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The server's answer to mission conditions (AchieveType.txt). A condition is either read from current state or from
/// a counter in Extra["missionCounters"], fed by MissionCountersFeature and Player's money tracking. MissionFeature's
/// pulse turns the answers into task values, so a counter only has to be bumped.
///
/// Conditions that only a live multiplayer match or a real payment can produce (3P9C, chicken, Darwin, recharge total)
/// return -1 and stay at zero.
/// </summary>
internal static class EventMissionProgress
{
    private const string ClearsKey = "towerClears", CountersKey = "missionCounters";
    internal const string VigourSpentKey = "vigourSpent";

    private const int CardGenre = 1, WeaponGenre = 2;

    /// <summary>Set at startup. Currency vs item stack needs the item templates, which Player does not hold.</summary>
    internal static GameFeatureContext? Context { get; set; }

    // ------------------------------------------------------------ counters

    private static JsonObject Counters(Player player)
    {
        if (player.Extra.GetValueOrDefault(CountersKey) is not JsonObject counters)
            player.Extra[CountersKey] = counters = new JsonObject();
        return counters;
    }

    internal static int Counter(Player player, string key)
        => (player.Extra.GetValueOrDefault(CountersKey) as JsonObject)?[key]?.GetValue<int>() ?? 0;

    internal static void Bump(Player player, string key, int amount = 1)
    {
        if (amount <= 0) return;
        var counters = Counters(player);
        counters[key] = (int)Math.Min(int.MaxValue, (long)Counter(player, key) + amount);
    }

    private const string LoginDayKey = "loginDay";

    /// <summary>
    /// Counts one login day (AchieveType 43, counter "43") per server day that starts at 04:00 UTC,
    /// the same day line as SignUpFeature.SignDay. Safe to call many times a day.
    /// </summary>
    internal static bool RecordLoginDay(Player player, DateTimeOffset now)
    {
        var d = now.AddSeconds(-SignUpFeature.DayResetOffsetSeconds).UtcDateTime;
        int today = d.Year * 10000 + d.Month * 100 + d.Day;
        if (player.Extra.GetValueOrDefault(LoginDayKey)?.GetValue<int>() == today) return false;
        player.Extra[LoginDayKey] = today;
        Bump(player, "43");
        return true;
    }

    internal static void Reset(Player player, string key) => Counters(player).Remove(key);

    internal static void Raise(Player player, string key, int value)
    {
        if (value > Counter(player, key)) Counters(player)[key] = value;
    }

    /// <summary>Called from Player for every money change, which covers all rewards and all costs in one place.</summary>
    internal static void MoneyChanged(Player player, int money, int delta)
    {
        if (delta > 0) Bump(player, $"gain:{money}", delta);
        else if (delta < 0) Bump(player, $"spent:{money}", -delta);
    }

    internal static void RecordTowerClear(Player player, int tower, int activityType, int level, int levelType = 0)
    {
        if (player.Extra.GetValueOrDefault(ClearsKey) is not JsonObject clears)
            player.Extra[ClearsKey] = clears = new JsonObject();
        foreach (string key in new[] { $"{tower}:{activityType}", $"{tower}:{activityType}:{level}" })
            clears[key] = (clears[key]?.GetValue<int>() ?? 0) + 1;

        // Level types: 0 action, 1 SP challenge, 2 story. Story levels are read, not cleared.
        if (levelType == 2) return;
        Bump(player, "levelClear");
        Bump(player, $"105:{activityType}");
        if (activityType == SmallTowerType) Bump(player, "65");
        if (levelType == 0)
        {
            Bump(player, $"110:{activityType}");
        }
        else
        {
            Bump(player, $"181:{tower}:{activityType}");
        }
    }

    private const int SmallTowerType = 2;

    private static int Clears(Player player, string key)
        => (player.Extra.GetValueOrDefault(ClearsKey) as JsonObject)?[key]?.GetValue<int>() ?? 0;

    // ------------------------------------------------------------ answers

    /// <summary>Returns -1 for condition types this class cannot answer, so callers leave stored progress alone.</summary>
    internal static int Derive(Player player, int type, IReadOnlyList<int> c) => type switch
    {
        // Money, tracked at the Player level.
        24 => player.Extra.GetValueOrDefault(VigourSpentKey)?.GetValue<int>() ?? 0,
        48 => Counter(player, "gain:2"),
        49 => Counter(player, "spent:2"),
        73 => Counter(player, $"gain:{MoneyType.CoffeeCoin}"),
        89 => Counter(player, $"spent:{MoneyType.PayDiamond}"),
        90 or 149 => Counter(player, $"spent:{MoneyType.FreeDiamond}") + Counter(player, $"spent:{MoneyType.PayDiamond}"),
        176 when c.Count >= 2 => Counter(player, $"gain:{c[1]}"),
        259 when c.Count >= 2 => Counter(player, $"spent:{c[1]}"),
        117 => Counter(player, $"gain:{MoneyType.CoffeeCoin}"),

        // Current state.
        45 when c.Count >= 5 => ItemCount(player, c[1], c[2], c[3], c[4]),
        3 when c.Count >= 3 => Owned(player, i => i.Genre == WeaponGenre && i.Detail == c[0] && i.Level >= c[1]),
        21 => player.Girls.Count,
        35 when c.Count >= 2 => player.Items.Where(i => i.Genre == CardGenre && i.Particular == c[1] && i.Count > 0).Select(i => i.Detail).Distinct().Count(),
        37 => player.Girls.Select(g => (int)g.Level).DefaultIfEmpty(0).Max(),
        39 => player.Items.Where(i => i.Genre == CardGenre).Select(i => (int)i.EnhanceLevel).DefaultIfEmpty(0).Max(),
        44 => player.Items.Where(i => i.Genre == CardGenre && i.Count > 0).Select(i => (i.Detail, i.Particular)).Distinct().Count(),
        54 when c.Count >= 2 => Owned(player, i => i.Genre == CardGenre && i.BreakLevel >= c[1]),
        71 when c.Count >= 2 => Owned(player, i => i.Genre == WeaponGenre && i.EnhanceLevel >= c[1]),
        83 when c.Count >= 2 => player.Items.Where(i => i.Genre == CardGenre && i.Detail == c[1]).Select(i => (int)i.EnhanceLevel).DefaultIfEmpty(0).Max(),
        85 when c.Count >= 2 => player.Girls.Count(g => g.Level >= c[1]),
        88 => player.Girls.Select(g => (int)g.Level).DefaultIfEmpty(0).Min(),
        95 when c.Count >= 2 => Owned(player, i => i.Genre == WeaponGenre && i.Level == c[1]),
        100 => Owned(player, i => i.Genre == RuneTableGenre),
        160 => player.Items.Where(i => i.Genre == RuneTableGenre).Select(i => (int)i.EnhanceLevel).DefaultIfEmpty(0).Max(),
        79 => player.GetTask(CafeFeature.LevelTaskGroup, CafeFeature.LevelTaskId),
        34 => FriendFeature.ReadState(player).Hy.Count,
        111 when c.Count >= 2 => TowerPetFeature.LevelOf(player, c[0]),
        124 when c.Count >= 1 => player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId) == c[0] ? 1 : 0,
        145 when c.Count >= 2 => PledgeFeature.LoadStateStatic(player).Girls.TryGetValue(c[1], out var pledge) ? pledge.Step : 0,
        9 when c.Count >= 2 => c.Skip(1).Count(id => Complete(player, id)),

        59 => player.Formations.FirstOrDefault()?.Slots.Count(s => s.UsedCardGuid != 0 && s.UsedCardGuid != s.MainCardGuid) ?? 0,
        75 => CafeFeature.ReadState(player).Comfort,
        77 => (player.Extra.GetValueOrDefault(HouseFeature.ExtraKey)?["furniturelist"] as JsonArray)?.Count ?? 0,
        81 => Enumerable.Range(1, 200).Count(id => (player.GetTask(CafeBizFeature.CoffeeTaskGroup, id) & 1 << CafeBizFeature.CoffeeLearnedBit) != 0),
        242 when c.Count >= 2 => CafePets(player).Count(p => Template(p)?.Type == c[1]),
        243 when c.Count >= 2 => CafePets(player).Count(p => Template(p)?.Rare == c[1]),
        245 => CafePets(player).Sum(p => Math.Max(0, (int)p.Lv - 1)),
        246 when c.Count >= 2 => CafePets(player).Count(p => p.Lv >= c[1]),

        // Tower clears.
        192 when c.Count >= 2 => Clears(player, $"{c[0]}:{c[1]}"),
        219 when c.Count >= 3 => Clears(player, $"{c[0]}:{c[1]}:{c[2]}"),
        105 or 110 or 99 when c.Count >= 2 => Counter(player, $"{type}:{c[1]}"),
        108 when c.Count >= 2 => Counter(player, $"108:{c[0]}"),
        181 when c.Count >= 2 => Counter(player, $"181:{c[0]}:{c[1]}"),
        146 when c.Count >= 2 => Counter(player, $"146:{c[1]}"),
        113 when c.Count >= 2 => Counter(player, $"113:{c[1]}"),
        65 or 114 => Counter(player, type.ToString()),
        122 or 103 => Counter(player, "levelClear"),
        184 when c.Count >= 2 => Counter(player, $"184:{c[0]}:{c[1]}"),
        231 when c.Count >= 2 => Counter(player, $"231:{c[0]}:{c[1]}"),
        232 when c.Count >= 4 => Counter(player, $"232:{c[0]}:{c[1]}:{c[2]}:{c[3]}"),
        233 when c.Count >= 1 => Counter(player, $"233:{c[0]}"),

        // Keyed by a parameter.
        55 when c.Count >= 2 => Counter(player, $"55:{c[1]}"),
        58 when c.Count >= 2 => Counter(player, $"58:{c[0]}:{c[1]}"),
        68 when c.Count >= 1 => Counter(player, $"68:{c[0]}"),
        143 when c.Count >= 1 => Counter(player, $"143:{c[0]}"),
        138 when c.Count >= 2 => Counter(player, $"138:{c[1]}"),
        177 when c.Count >= 2 => Counter(player, $"177:{c[1]}"),
        186 when c.Count >= 1 => Counter(player, $"186:{c[0]}"),
        191 => Counter(player, "191"),
        112 or 115 when c.Count >= 1 => Counter(player, $"{type}:{c[0]}"),
        252 when c.Count >= 2 => Counter(player, $"252:{c[1]}"),
        // AchieveType 43 [count, paid sign activity]: the paid counter only when that activity has one, else login days.
        43 when c.Count >= 2 && Counter(player, $"43:{c[1]}") > 0 => Counter(player, $"43:{c[1]}"),
        43 => Counter(player, "43"),

        // Plain totals, bumped by the feature that names the command or combat stat.
        16 or 17 or 18 or 19 or 20 or 23 or 36 or 27 or 28 or 31 or 33 or 40 or 41 or 42 or 46 or 47 or 53 or 62 or 76 or 93 or 102
            or 109 or 116 or 120 or 187
            or 96 or 97 or 98 or 104 or 107 or 136 or 137 or 161 or 162 or 168 or 169 or 178 or 179 or 180 or 183 or 188
            or 189 or 241 or 247 or 248 or 249 or 250 or 251 => Counter(player, type.ToString()),
        _ => -1,
    };

    private const uint RuneTableGenre = Config.RuneTable.RuneGenre;

    /// <summary>Loaded once at startup. Read-only, so parallel servers in tests can share it.</summary>
    internal static IReadOnlyDictionary<int, Config.PetTemplateRow>? PetTemplates { get; set; }

    private static List<CafePet> CafePets(Player player) => CafePetState.Read(player).Pets;

    private static Config.PetTemplateRow? Template(CafePet pet)
        => PetTemplates is not null && PetTemplates.TryGetValue((int)pet.TemplateId, out var row) ? row : null;

    /// <summary>Time conditions name their limit, so a clear counts toward every limit it beat.</summary>
    internal static void RecordTimedClear(Player player, int tower, int type, int level, int seconds)
    {
        if (Context is not { } ctx || seconds <= 0) return;
        foreach (var mission in ctx.Missions.AchieveOther.Values)
        {
            var c = mission.Counts;
            if (mission.AchieveType == 232 && c.Count >= 4 && c[0] == tower && c[1] == type && c[2] == level && seconds <= c[3])
                Bump(player, $"232:{tower}:{type}:{level}:{c[3]}");
            if (mission.AchieveType == 233 && c.Count >= 1 && seconds <= c[0])
                Bump(player, $"233:{c[0]}");
        }
    }

    /// <summary>A round's stats only count once it ends: fewer draws clears every draw limit above that number.</summary>
    internal static void RecordAnimalizeRound(Player player, int boxes, int draws)
    {
        for (int i = 1; i <= boxes; i++) Bump(player, $"112:{i}");
        for (int i = draws + 1; i <= 40; i++) Bump(player, $"115:{i}");
    }

    /// <summary>Each ten pull counts toward every "this many of this star" condition it reached.</summary>
    internal static void RecordTenPull(Player player, IEnumerable<int> weaponStars)
    {
        var stars = weaponStars.ToList();
        foreach (int star in stars.Distinct())
            for (int need = 1, have = stars.Count(s => s >= star); need <= have; need++)
                Bump(player, $"58:{star}:{need}");
    }

    private static int Owned(Player player, Func<Item, bool> match)
        => (int)Math.Min(int.MaxValue, player.Items.Where(i => i.Count > 0 && match(i)).Sum(i => (long)i.Count));

    private static bool Complete(Player player, int id)
    {
        var missions = Context?.Missions;
        if (missions is null) return false;
        var entry = missions.GetAchieveOther(id) ?? (missions.Achieve.TryGetValue(id, out var normal) ? normal : null);
        return entry is not null && MissionFeature.ProgressOf(player.GetTask(entry.Group, id)) >= entry.RequiredCount(true);
    }

    private static int ItemCount(Player player, int g, int d, int p, int l)
    {
        if (Context?.ItemTemplates.TryGetMoneyId((uint)g, (uint)d, (uint)p, (uint)l, out int money) == true)
            return player.GetMoney(money);
        long total = player.Items.Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l).Sum(i => (long)i.Count);
        return (int)Math.Min(int.MaxValue, total);
    }
}
