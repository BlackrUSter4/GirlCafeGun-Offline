using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Feeds mission counters from one place: the router reports each finished command that got a reply and no error, and
/// this maps it to the conditions it advances. Handlers stay unaware of missions, and a refused action counts nothing.
/// Combat stats come from the tbKillMonster block the client adds to every battle settlement.
/// </summary>
public sealed class MissionCountersFeature : IGameFeature
{
    // Client LevelManager:PackKillMonster field names.
    private const string KillsField = "totalkillMonster", EliteField = "killElite", BossField = "killBoss",
        ShieldField = "breakDown", StreakField = "killnum", KillBlock = "tbKillMonster";

    private const int PassTower = 55, RemakeFight = 363;
    internal const int TeamPowerReport = 9998;
    private const int MaxTeamPower = 1000000;

    /// <summary>LuaCall commands that mean one plain counter. Size means count the request's list, not the command.</summary>
    private static readonly Dictionary<int, (string Key, bool Size)> Commands = new()
    {
        [1] = ("55:1", true),   // Card_Decompose
        [3] = ("27", false), [4] = ("27", false), [5] = ("27", false),
        [6] = ("28", false),    // Card_Break
        [13] = ("93", false),   // Shop_ReqBuyGoods
        [14] = ("137", true),
        [337] = ("104", false), // CoopClient_PassTower
        [31] = ("161", false), [35] = ("162", false),
        [82] = ("62", false),   // Friend_SendEnergy
        [116] = ("76", false),  // Cafe_DrinkOffCoffee
        [171] = ("136", false),
        [173] = ("47", false),  // GirlMissionFinish
        [355] = ("179", false), // FanEvent_Support
        [381] = ("180", false), [382] = ("189", false),
        [388] = ("178", false), // MusicGameComplete
        [10013] = ("107", false),
    };

    private static readonly HashSet<string> Functions = new()
    {
        "WeaponLogicMsg", "GirlLogic", "Lottery", "MissionGetAward", "PhoneMsg", "SignUpMsg", CafePetFeature.Function,
    };

    public string Name => "Mission condition counters";

    public void Install(GameFeatureContext ctx)
    {
        EventMissionProgress.Context = ctx;
        EventMissionProgress.PetTemplates = Config.CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory)).PetTemplates;
        // The client alone computes team power, so it reports the number when a formation is saved (ClientUiFixFeature).
        ctx.MapLuaCall(TeamPowerReport, (session, cmd, param) =>
        {
            if (session.Tag is Player player)
                lock (player)
                    EventMissionProgress.Raise(player, "102", Math.Clamp(EventKit.Number(param, "power"), 0, MaxTeamPower));
            return Task.CompletedTask;
        });
        ctx.LuaCalls.CommandWatch = Watches;
        ctx.LuaCalls.CommandDone = OnDone;
    }

    private static bool Watches(string function, int cmd, JsonNode? param)
        => function == LuaCallRouter.LuaCallFunction
            ? Commands.ContainsKey(cmd) || FindKills(param) is not null
            : Functions.Contains(function) || FindKills(param) is not null;

    private static void OnDone(ClientSession session, CommandScope scope)
    {
        if (session.Tag is not Player player) return;
        lock (player)
        {
            if (scope.Function == LuaCallRouter.LuaCallFunction)
            {
                if (Commands.TryGetValue(scope.Cmd, out var counter))
                    EventMissionProgress.Bump(player, counter.Key, counter.Size ? Size(scope.Param) : 1);
                if (scope.Cmd == 388) RecordMusicGame(player, scope.Param);
                if (scope.Cmd == 355) EventMissionProgress.Bump(player, "187", Math.Max(1, EventKit.Number(scope.Param, "Num")));
                if (scope.Cmd is 381 or 384) EventMissionProgress.Bump(player, "188");
            }
            else
            {
                RecordFunction(player, scope.Function, scope.Param);
            }
            RecordCombat(player, scope);
        }
    }

    private static void RecordFunction(Player player, string function, JsonNode? param)
    {
        var body = param as JsonObject;
        if (function == CafePetFeature.Function && body is not null)
        {
            RecordCafePet(player, body);
            return;
        }
        switch (function)
        {
            case "WeaponLogicMsg":
                switch (EventKit.Number(param, "nCmd"))
                {
                    case WeaponFeature.CmdForge: EventMissionProgress.Bump(player, "20"); break;
                    case WeaponFeature.CmdStrong: EventMissionProgress.Bump(player, "19"); break;
                    case WeaponFeature.CmdDecompose: EventMissionProgress.Bump(player, "55:2", Size(param)); break;
                    case WeaponFeature.CmdBreak: EventMissionProgress.Bump(player, "40"); break;
                }
                break;
            case "GirlLogic":
                if (Equals(body?["sCmd"]?.ToString(), GirlLoveFeature.CmdGirlGift))
                {
                    int gifts = Math.Max(1, EventKit.Number(param, "ItemNum"));
                    EventMissionProgress.Bump(player, "18", gifts);
                    EventMissionProgress.Bump(player, $"68:{EventKit.Number(param, "nId")}", gifts);
                }
                break;
            case "Lottery":
                int pulls = body?["bTen"]?.ToString() is "true" or "True" or "1" ? 10 : 1;
                EventMissionProgress.Bump(player, $"143:{EventKit.Number(param, "nId")}", pulls);
                break;
            case "MissionGetAward":
                EventMissionProgress.Bump(player, "46", Math.Max(1, Size(param)));
                break;
            case "PhoneMsg":
                if (EventKit.Number(param, "nCmd") == 1) EventMissionProgress.Bump(player, "53");
                break;
            case "SignUpMsg":
                EventMissionProgress.RecordLoginDay(player, DateTimeOffset.UtcNow);
                EventMissionProgress.Bump(player, $"43:{EventKit.Number(param, "nId")}");
                break;
        }
    }

    private static void RecordCafePet(Player player, JsonObject body)
    {
        switch (body["sCmd"]?.ToString())
        {
            case CafePetFeature.CmdOnNewPet: EventMissionProgress.Bump(player, "241"); break;
            case CafePetFeature.CmdSetFood: EventMissionProgress.Bump(player, "247"); break;
            case CafePetFeature.CmdOnGiveupPet or CafePetFeature.CmdDelPet: EventMissionProgress.Bump(player, "250"); break;
            case CafePetFeature.CmdPetGift:
                // tbItem is a G-D-P-L-N tuple: genre 22 detail 1 is a toy, detail 2 a wash.
                int detail = EventKit.NumberAt(body["tbItem"], 1);
                int amount = Math.Max(1, EventKit.NumberAt(body["tbItem"], 4));
                if (detail == 1) EventMissionProgress.Bump(player, "248", amount);
                if (detail == 2) EventMissionProgress.Bump(player, "249", amount);
                break;
            case CafePetFeature.CmdCompleteTour:
                EventMissionProgress.Bump(player, "251");
                EventMissionProgress.Bump(player, $"252:{EventKit.Number(body, "nIndex")}");
                break;
        }
    }

    private static void RecordMusicGame(Player player, JsonNode? param)
    {
        int mode = EventKit.Number(param, "nType");
        int score = EventKit.Number(param, "nScore");
        EventMissionProgress.Bump(player, $"177:{mode}");
        EventMissionProgress.Raise(player, $"186:{mode}", score);
    }

    /// <summary>Kills are per run, so they add up. The streak is a personal best, so it only rises.</summary>
    private static void RecordCombat(Player player, CommandScope scope)
    {
        if (FindKills(scope.Param) is not { } kills) return;
        int total = EventKit.Number(kills, KillsField);
        EventMissionProgress.Bump(player, "96", total);
        EventMissionProgress.Bump(player, "97", EventKit.Number(kills, EliteField));
        EventMissionProgress.Bump(player, "98", EventKit.Number(kills, BossField));
        EventMissionProgress.Bump(player, "23", EventKit.Number(kills, ShieldField));
        EventMissionProgress.Raise(player, "33", EventKit.Number(kills, StreakField));
        // The nearest stats the client reports: kills inside one second for a skill kill, and dash skill uses.
        if (EventKit.Number(kills, "killCount") > 1) EventMissionProgress.Bump(player, "31");
        EventMissionProgress.Bump(player, "120", EventKit.Number(kills, "moveSkillCount"));
        if (HasAssist(scope.Param)) EventMissionProgress.Bump(player, "109");
        if (scope.Function != LuaCallRouter.LuaCallFunction) return;
        if (scope.Cmd == PassTower)
        {
            EventMissionProgress.Bump(player, $"138:{EventKit.NumberAt(scope.Param, 1)}", total);
            EventMissionProgress.RecordTimedClear(player, EventKit.NumberAt(scope.Param, 1), EventKit.NumberAt(scope.Param, 0),
                EventKit.NumberAt(scope.Param, 3), EventKit.Number(kills, "useTime"));
        }
        if (scope.Cmd == 372) EventMissionProgress.Bump(player, "183", total);
        if (scope.Cmd == RemakeFight)
        {
            EventMissionProgress.Bump(player, "168", total);
            EventMissionProgress.Bump(player, "169", EventKit.Number(kills, BossField));
        }
    }

    /// <summary>The block sits at a different place per settlement: named on objects, positional on tower arrays.</summary>
    private static JsonObject? FindKills(JsonNode? param)
    {
        switch (param)
        {
            case JsonObject body:
                if (body[KillBlock] is JsonObject named) return named;
                return body.ContainsKey(KillsField) || body.ContainsKey(BossField) ? body : null;
            case JsonArray list:
                foreach (var entry in list)
                    if (entry is JsonObject item && (item.ContainsKey(KillsField) || item.ContainsKey(BossField) || item.ContainsKey(StreakField)))
                        return item;
                return null;
            default:
                return null;
        }
    }

    /// <summary>Named on object settlements, and the last positional value on tower arrays.</summary>
    private static bool HasAssist(JsonNode? param) => param switch
    {
        JsonObject body => EventKit.Number(body, "nAssistId") > 0,
        JsonArray { Count: > 0 } list => list[^1] is JsonValue value && value.TryGetValue<double>(out double id) && id > 0 && list.Count > 6,
        _ => false,
    };

    private static int Size(JsonNode? param)
    {
        if (param is JsonArray direct) return Math.Max(1, direct.Count);
        if (param is JsonObject body)
            foreach (var (_, value) in body)
                if (value is JsonArray list) return Math.Max(1, list.Count);
        return 1;
    }
}
