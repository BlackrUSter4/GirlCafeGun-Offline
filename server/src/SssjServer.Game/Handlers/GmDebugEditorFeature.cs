using System.Globalization;
using System.Text.RegularExpressions;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// **原服 GM 通道**（<c>sFunction = "DebugEditor"</c>）的兼容实现。
///
/// <para><b>0. 这是什么，为什么值得做</b></para>
/// 原服的 GM 后台不是一套私有协议 —— 它就是「把一段 Lua 源码发给服务端 eval」：
/// <code>
/// Game.Player.CallServer('DebugEditor', 'GM:UnlockAllChapter();');              -- UI_GM.lua:596/:719
/// Game.Player.CallServer('DebugEditor', string.format("GM:SetPlayerLevel(%s);", param));  -- AutoTestCase.lua:211
/// Game.Player.CallServer('DebugEditor', string.format("me:SetTaskValue(%d, %d, %d);", g, id, v)); -- :190
/// </code>
/// 也就是说，**客户端里已经内置了一整套调用原服 GM 的界面与自动化用例**：
/// <list type="bullet">
///   <item><c>extracted/setting/GMTable.txt</c>（199 条）—— 游戏内 GM 面板的命令表，
///     列 <c>Code</c> 是要执行的代码、<c>Target</c> 是执行方
///     （实测 <c>2</c> 一族全是 <c>GM:*</c>/<c>me:SetTaskValue</c> 这类服务端调用）；</item>
///   <item><c>extracted/lua/AutoTestCase.lua</c> —— 原厂自动化测试用例，
///     第 190..539 行集中调了 20 个服务端 GM 命令，是**命令名与参数个数最完整的一份取证**。</item>
/// </list>
/// 服务端认下这些名字之后，游戏内 GM 面板（<c>disablegm=false</c> 时可用，见 AGENTS.md §2.2）
/// 与 <c>AutoTestCase</c> 就能直接驱动本服务端 —— 等于白捡一套 GM 界面 + 回归用例。
///
/// <para><b>1. 不跑 Lua，只认命令名</b></para>
/// 服务端没有内嵌 Lua 运行时（AGENTS.md §4 把「内嵌 Lua 还是 C# 重写」列为待决策）。
/// 这里用正则把 <c>模块:方法(参数…)</c> 拆出来，映射到已实现的 C# 能力；
/// 认不出的命令**原样记一条 WARN**（带完整代码文本），这样它就是「下一个要实现什么」的信号，
/// 与 <c>unhandled LuaCall cmd=</c> 同一套推进方式。
///
/// <para><b>2. 已映射的命令</b></para>
/// <list type="table">
/// <item><term><c>GM:AddAllNormalItem()</c></term><description>→ <see cref="GmUnlock.ApplyItems"/></description></item>
/// <item><term><c>GM:AddAllCardItem()</c></term><description>→ <see cref="GmUnlock.ApplyCards"/></description></item>
/// <item><term><c>GM:UnlockAllChapter()</c></term><description>→ <see cref="GmUnlock.ApplyLevels"/>（无参=全部）</description></item>
/// <item><term><c>GM:UnlockAllChapter(ch,idx,diff)</c></term><description>→ 记单关三星通关</description></item>
/// <item><term><c>GM:SetPlayerLevel(n)</c></term><description>→ 设等级（<c>n</c> 缺省/超界时取满级）</description></item>
/// <item><term><c>GM:SetPlayerExp(n)</c></term><description>→ 设经验</description></item>
/// <item><term><c>GM:SetPlayerGold(n)</c> / <c>SetPlayerDiamond</c> / <c>SetPlayerPayDiamond</c> / <c>SetPlayerVigour</c></term>
///   <description>→ 设对应货币（id 见 <see cref="MoneyType"/>）</description></item>
/// <item><term><c>GM:SetPlayerItem(g,d,p,l,n)</c></term><description>→ 走 <see cref="RewardService"/> 发一份</description></item>
/// <item><term><c>GM:AchievementComplete(type,id)</c></term><description>→ 单条任务/成就置完成（type 1 任务 / 2 成就 / 3 活动，
///   取证 GMTable.txt 的 <c>ENUM.Task.Achievement.Activity|Task:1:Achive:2:Act:3</c>）</description></item>
/// <item><term><c>me:SetTaskValue(g,id,v)</c></term><description>→ 设任务值并推 pf 1026</description></item>
/// </list>
/// 未映射但已知存在的（<see cref="Unmapped"/> 会把它们单独标注出来）：
/// <c>GM:AddAllWeaponItem</c> / <c>GM:AddAllRuneItem</c> / <c>GM:SetAllWeaponLevel</c> /
/// <c>GM:SetAllCardLevel</c> / <c>GM:SetAllRuneLevel</c> / <c>GM:SetAllGirlLevel</c> /
/// <c>GM:AchievementAward</c> / <c>GM:RecastRune</c> / <c>GM:SetAllWeaponSkillLevel</c> /
/// <c>GM:SetAllCardSkillLevel</c> —— 它们要等武器/符文/少女养成域落地。
///
/// <para><b>3. 回包</b></para>
/// 原服会把执行结果回给 <c>S2C.DebugEditor{szRet=...}</c>（S2CCall.lua:74），客户端转交
/// <c>Game.DebugEditor.SendResult</c>。这里照样回一条，内容是本服务端的执行摘要，
/// 于是游戏内 GM 面板/调试客户端能看到「到底做了什么」。
/// </summary>
public sealed class GmDebugEditorFeature : IGameFeature
{
    /// <summary>原服 GM 的上行 sFunction（也是下行名，见 S2CCall.lua:74）。</summary>
    public const string Function = "DebugEditor";

    /// <summary>
    /// 拆 <c>模块:方法(参数)</c>。允许前后空白、结尾分号、参数里带空格/负号/小数。
    /// 一条 sJsonParam 里可能有多条语句（<c>GMTable.txt</c> 的 <c>Code</c> 列有用 <c>;</c> 串联的），
    /// 所以用 <see cref="Regex.Matches"/> 逐条处理。
    /// </summary>
    private static readonly Regex CallPattern = new(
        @"(?<obj>[A-Za-z_][A-Za-z0-9_]*)\s*[:.]\s*(?<fn>[A-Za-z_][A-Za-z0-9_]*)\s*\((?<args>(?:[^()]|\([^()]*\))*)\)",
        RegexOptions.Compiled);

    /// <summary>
    /// 已知存在于原服、但本服务端还没有对应能力的命令名（只记 WARN，不当成「未知命令」）。
    /// 取证：<c>AutoTestCase.lua:190..539</c> + <c>GMTable.txt</c> 的 Target=2 行。
    /// </summary>
    public static readonly string[] Unmapped =
    {
        "AddAllWeaponItem", "AddAllRuneItem",
        "SetAllWeaponLevel", "SetAllCardLevel", "SetAllRuneLevel", "SetAllGirlLevel",
        "SetAllWeaponSkillLevel", "SetAllCardSkillLevel",
        "AchievementAward", "RecastRune", "DiyAddWeapon",
    };

    private GameFeatureContext _ctx = null!;

    public string Name => "原服 GM 通道兼容（sFunction DebugEditor）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        // ★ 必须用 MapRawFunction：这条命令的 sJsonParam 是 Lua 源码，不是 JSON。
        ctx.MapRawFunction(Function, Handle);
    }

    private Task Handle(ClientSession session, string function, string rawParam)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] [gm] DebugEditor 到达但会话未登录，忽略：{rawParam}");
            return Task.CompletedTask;
        }

        Log.Info($"[session {session.Id}] [gm] 原服 GM 命令：{rawParam}");

        var matches = CallPattern.Matches(rawParam ?? "");
        if (matches.Count == 0)
        {
            Log.Warn($"[session {session.Id}] [gm] 无法从 DebugEditor 参数里解析出「模块:方法(…)」：{rawParam}");
            Reply(session, "Cannot parse: " + rawParam);
            return Task.CompletedTask;
        }

        var results = new List<string>();
        foreach (Match m in matches)
        {
            string fn = m.Groups["fn"].Value;
            var args = ParseArgs(m.Groups["args"].Value);
            results.Add(Execute(session, player, m.Groups["obj"].Value, fn, args));
        }

        _ctx.Store.Save(player);
        string summary = string.Join(" | ", results);
        Log.Info($"[session {session.Id}] [gm] 执行结果：{summary}");
        Reply(session, summary);
        return Task.CompletedTask;
    }

    /// <summary>执行一条命令，返回一句人读的结果摘要。</summary>
    private string Execute(ClientSession session, Player player, string obj, string fn, List<int> args)
    {
        var gm = _ctx.GmUnlock;

        switch (fn)
        {
            // ---- 全量类（对上本服务端的 GmUnlock 四个域）----
            case "AddAllNormalItem":
                return Push(session, player, gm.ApplyItems(player), "All items");

            case "AddAllCardItem":
                return Push(session, player, gm.ApplyCards(player), "All character cards");

            case "UnlockAllChapter":
            {
                if (args.Count >= 3)
                {
                    uint mapId = MapList.MapId(args[0], args[1], args[2]);
                    int cur = player.GetLevelStar(mapId);
                    int composed = MapList.PassCount(cur) > 0
                        ? MapList.Compose(MapList.StarMask, MapList.PassCount(cur))
                        : MapList.Compose(MapList.StarMask, 1);
                    player.Levels[mapId] = composed;
                    GameNotify.PushChapter(session, mapId, composed);
                    return $"{args[0]}-{args[1]}(d{args[2]}) marked as 3-star cleared";
                }
                return Push(session, player, gm.ApplyLevels(player), "All stages 3-star");
            }

            // ---- 玩家数值 ----
            case "SetPlayerLevel":
            {
                int n = args.Count >= 1 ? args[0] : _ctx.PlayerExp.MaxLevel;
                if (n <= 0)
                    n = _ctx.PlayerExp.MaxLevel;
                player.Level = n;
                player.Exp = 0;
                GameNotify.PushLevelExp(session, player);
                return $"Level → LV.{player.Level}";
            }

            case "SetPlayerExp":
            {
                player.Exp = args.Count >= 1 ? Math.Max(0, args[0]) : 0;
                GameNotify.PushLevelExp(session, player);
                return $"EXP → {player.Exp}";
            }

            case "SetPlayerGold":
                return SetMoney(session, player, MoneyType.Gold, args, "Gold");

            case "SetPlayerDiamond":
                return SetMoney(session, player, MoneyType.FreeDiamond, args, "Free Diamonds");

            case "SetPlayerPayDiamond":
                return SetMoney(session, player, MoneyType.PayDiamond, args, "Paid Diamonds");

            case "SetPlayerVigour":
                return SetMoney(session, player, MoneyType.Vigour, args, "Stamina");

            // ---- In-game GM panel money buttons (GMTable.txt rows, Target=2): MoneyLogic:AddX(n, TLog.REASON_DEBUGEDITOR, 1, seq) ----
            case "AddGold": return AddMoneyCmd(session, player, MoneyType.Gold, args, +1, "Gold");
            case "ReduceGold": return AddMoneyCmd(session, player, MoneyType.Gold, args, -1, "Gold");
            case "AddFreeDiamond": return AddMoneyCmd(session, player, MoneyType.FreeDiamond, args, +1, "Free Diamonds");
            case "ReduceFreeDiamond": return AddMoneyCmd(session, player, MoneyType.FreeDiamond, args, -1, "Free Diamonds");
            case "AddPayDiamond": return AddMoneyCmd(session, player, MoneyType.PayDiamond, args, +1, "Paid Diamonds");
            case "ReducePayDiamond": return AddMoneyCmd(session, player, MoneyType.PayDiamond, args, -1, "Paid Diamonds");
            case "AddCoffeeCoin": return AddMoneyCmd(session, player, MoneyType.CoffeeCoin, args, +1, "Cafe Coins");
            case "ReduceCoffeeCoin": return AddMoneyCmd(session, player, MoneyType.CoffeeCoin, args, -1, "Cafe Coins");
            // MoneyLogic:AddMoney(id, n, ...) and ReduceMoney(id, n, ...): the first argument is the currency id.
            case "AddMoney" when obj == "MoneyLogic" && args.Count >= 2:
                return AddMoneyCmd(session, player, args[0], args.GetRange(1, args.Count - 1), +1, MoneyType.NameOf(args[0]));
            case "ReduceMoney" when obj == "MoneyLogic" && args.Count >= 2:
                return AddMoneyCmd(session, player, args[0], args.GetRange(1, args.Count - 1), -1, MoneyType.NameOf(args[0]));

            case "SetPlayerItem":
            {
                if (args.Count < 5)
                    return $"SetPlayerItem needs 5 arguments (G,D,P,L,N), got {args.Count}";
                var granted = _ctx.Rewards.Grant(player,
                    new[] { new[] { args[0], args[1], args[2], args[3], args[4] } },
                    RewardSource.Extra);
                GameNotify.PushRewards(session, player, granted);
                return $"Granted {granted.Describe()}";
            }

            // ---- 任务 / 成就 ----
            case "AchievementComplete":
            {
                // GMTable.txt: ENUM.Task.Achievement.Activity|Task:1:Achive:2:Act:3 + ID
                if (args.Count < 2)
                    return $"AchievementComplete needs (type,id), got {args.Count} arguments";
                return CompleteOne(session, player, args[0], args[1]);
            }

            case "SetTaskValue":
            {
                if (args.Count < 3)
                    return $"SetTaskValue needs (group,id,value), got {args.Count} arguments";
                player.SetTask(args[0], args[1], args[2]);
                GameNotify.PushTaskValues(session, new[]
                {
                    new KeyValuePair<uint, int>(Player.ComposeTaskId(args[0], args[1]), args[2]),
                });
                return $"Task value ({args[0]},{args[1]}) = {args[2]}";
            }

            default:
                if (Array.IndexOf(Unmapped, fn) >= 0)
                {
                    Log.Warn($"[session {session.Id}] [gm] 原服命令 {obj}:{fn}() 已知但本服务端未实现" +
                             "（等武器/符文/少女养成域落地）");
                    return $"{fn} not implemented (known original-server command)";
                }
                Log.Warn($"[session {session.Id}] [gm] ★ 未映射的原服 GM 命令 {obj}:{fn}(" +
                         $"{string.Join(",", args)}) —— 这是「下一个要实现什么」的信号");
                return $"{fn} not mapped";
        }
    }

    /// <summary>
    /// 单条任务/成就置完成。type 语义取自 <c>GMTable.txt</c> 的
    /// <c>ENUM.Task.Achievement.Activity|Task:1:Achive:2:Act:3</c>。
    /// </summary>
    private string CompleteOne(ClientSession session, Player player, int type, int id)
    {
        var table = _ctx.Missions;
        Config.MissionEntry? entry;
        bool achieveKey;
        switch (type)
        {
            case 1: entry = table.GetDaily(id); achieveKey = false; break;
            case 2: entry = table.GetAchieve(id); achieveKey = true; break;
            case 3: entry = table.GetAchieveOther(id); achieveKey = true; break;
            default: return $"AchievementComplete: unknown type={type} (expected 1 task / 2 achievement / 3 activity)";
        }
        if (entry is null)
            return $"No config entry for type={type} id={id}";

        int need = entry.RequiredCount(achieveKey);
        if (need == int.MaxValue || need <= 0)
            need = 1;
        int value = MissionFeature.ComposeTaskValue(need, _ctx.Options.GmMissionClaimed ? 1 : 0);
        player.SetTask(entry.Group, entry.Id, value);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(entry.Group, entry.Id), value),
        });
        return $"type={type} id={id} set complete (progress {need})";
    }

    /// <summary>Adds (sign +1) or subtracts (sign -1) args[0] of one currency. Result is clamped to 0..int.MaxValue.</summary>
    private static string AddMoneyCmd(ClientSession session, Player player, int moneyId,
                                      List<int> args, int sign, string label)
    {
        if (args.Count < 1)
            return $"{label}: amount missing";
        long next = (long)player.GetMoney(moneyId) + (long)sign * Math.Abs((long)args[0]);
        player.SetMoney(moneyId, (int)Math.Clamp(next, 0L, int.MaxValue));
        GameNotify.PushMoney(session, player, moneyId);
        return $"{label} {(sign > 0 ? "+" : "-")}{Math.Abs((long)args[0])} → {player.GetMoney(moneyId)}";
    }

    private static string SetMoney(ClientSession session, Player player, int moneyId,
                                   List<int> args, string label)
    {
        int n = args.Count >= 1 ? args[0] : 0;
        player.SetMoney(moneyId, n);
        GameNotify.PushMoney(session, player, moneyId);
        return $"{label} → {player.GetMoney(moneyId)}";
    }

    /// <summary>把一次 GmUnlock 的结果推给客户端（运行期触发必须推增量，见 ServerConsoleFeature）。</summary>
    private static string Push(ClientSession session, Player player, GmUnlockReport report, string what)
    {
        GameNotify.PushMoney(session, player, report.ChangedMoney);
        for (int i = 0; i < report.ChangedItems.Count; i += ServerConsoleFeature.ItemPushBatch)
        {
            GameNotify.PushItems(session,
                report.ChangedItems.Skip(i).Take(ServerConsoleFeature.ItemPushBatch).ToList());
        }
        for (int i = 0; i < report.RemovedItems.Count; i += ServerConsoleFeature.ItemPushBatch)
        {
            GameNotify.PushItemsRemoved(session,
                report.RemovedItems.Skip(i).Take(ServerConsoleFeature.ItemPushBatch).ToList());
        }
        if (report.ChangedLevels.Count > 0)
            GameNotify.PushChapters(session, report.ChangedLevels);
        if (report.ChangedTasks.Count > 0)
            GameNotify.PushTaskValues(session, report.ChangedTasks);
        foreach (var girl in report.NewGirls)
            GameNotify.PushGirl(session, girl);
        if (report.LevelChanged)
            GameNotify.PushLevelExp(session, player);
        return $"{what}: {report.Describe()}";
    }

    /// <summary>
    /// 回一条 <c>S2C.DebugEditor{szRet=...}</c>（S2CCall.lua:74 → <c>Game.DebugEditor.SendResult</c>）。
    /// 客户端没有调试客户端连着时是空操作，不会报错。
    /// </summary>
    private void Reply(ClientSession session, string text)
        => _ctx.LuaCalls.CallClientFunction(session, Function, new { szRet = text });

    /// <summary>
    /// 拆参数列表。只取整数（本族命令的参数实测全是整数或整数数组），
    /// 形如 <c>{1,2,3}</c> 的表参数会被摊平成多个整数 —— 与 <c>GM:UnlockAllChapter(1,1,1)</c>
    /// 这种位置参数在语义上等价，够用。非整数（字符串/nil）忽略。
    /// </summary>
    internal static List<int> ParseArgs(string args)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(args))
            return result;

        foreach (var token in args.Split(new[] { ',', '{', '}', ' ', '\t', '\'', '"' },
                                         StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
                result.Add(v);
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                result.Add((int)d);
        }
        return result;
    }
}
