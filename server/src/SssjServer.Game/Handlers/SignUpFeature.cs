using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 每日签到（<c>UI_SignActivity</c> / <c>UI_SignActivityPop</c>）。
///
/// <para><b>0. 上下行不同名</b></para>
/// <list type="bullet">
/// <item>上行 <c>SignUpMsg</c>：<c>{nType}</c>，7 个发送点
///   （<c>UI_SignActivityPop.lua:210</c> 签到 / <c>:220,:227</c> 补签、
///    <c>UI_SignActivity.lua:222</c> 签到 / <c>:232,:239</c> 补签、
///    <c>UI_EnergyCompensate.lua:106</c> 月卡体力补偿 <c>{nType=10, nId}</c>）。</item>
/// <item>下行 <c>NormalActivityMsg</c>（<c>S2CCall.lua:329</c> →
///   <c>NormalActivityConfig:OnNormalActivityMsg</c>，<c>NormalActivityConfig.lua:1683</c>）。</item>
/// </list>
///
/// <para><b>1. 下行字段（逐行核过 <c>NormalActivityConfig.lua:1683..1740</c>）</b></para>
/// <code>
/// nType    = 3  签到族（4 是「领体力」，另一条链）
/// nSubType = 1  普通签到 / 2 补签
/// bSuccess      **必须为 true**，否则整个分支什么都不做（客户端也不给失败提示）
/// tbAward       奖励数组，直接喂 UI.OpenAsChild('UI_RewardNormal', tbAward)
///               元素形状与 tbAwards 一致：[G,D,P,L,N]
/// isRefreshSign → UI.Call(uiName, "UpdateSigned", tbParam.isRefreshSign)（播放「已签到」动画）
/// </code>
/// 客户端拿到后依次：更新页签 → 0.7s 后弹奖励框 → <c>Refresh()</c> → <c>UpdateSigned()</c>。
/// 其中 <c>Refresh()</c> 读的是**任务值**（见下），所以服务端必须先推 pf 1026 再推本消息。
///
/// <para><b>2. 状态全在任务值 group 20（<c>NormalActivityConfig.lua:11..17</c>）</b></para>
/// <code>
/// GID = 20
/// 11001 TID_SIGNUP    今天的签到状态，0 未签到 / 1 已签，**每天 4 点清空**
/// 11002 TID_MONTH     当月已签到总次数，月末清空
/// 11003 TID_RESIGN    本月已补签次数
/// 11004 TID_MONTHSIGN 月卡签到状态（本实现不涉及）
/// </code>
/// 客户端 <c>UI_SignActivityPop:Refresh</c>（<c>:107..108</c>）就是读 11002 / 11001。
///
/// <para><b>3. 「每天 4 点」的服务端实现</b></para>
/// 客户端算当天用的是 <c>GetTime() - 4*3600</c>（<c>UI_SignActivityPop.lua:103</c>），
/// 即**服务器时间偏移 4 小时后的日历日**。服务端用同一口径（<see cref="SignDay"/>），
/// 并把「上次签到的那个日历日/月」记在 <see cref="Player.Extra"/>["signup"] 里；
/// 登录钩子与每次上行前都做一次跨日/跨月重置，避免依赖定时任务。
/// </summary>
public sealed class SignUpFeature : IGameFeature
{
    /// <summary>上行 sFunction。</summary>
    public const string UpFunction = "SignUpMsg";

    /// <summary>下行 sFunction（<c>S2CCall.lua:329</c>）。</summary>
    public const string DownFunction = "NormalActivityMsg";

    /// <summary>nType=1：普通签到。</summary>
    public const int TypeSign = 1;

    /// <summary>nType=2：补签（消耗补签券或钻石，客户端已在本地确认过）。</summary>
    public const int TypeResign = 2;

    /// <summary>nType=10：月卡体力补偿领取（<c>UI_EnergyCompensate.lua:106</c>，带 nId）。</summary>
    public const int TypeEnergyCompensate = 10;

    /// <summary>下行 nType=3：签到族。</summary>
    public const int MsgTypeSign = 3;

    /// <summary>下行 nSubType=1：普通签到。</summary>
    public const int SubTypeSign = 1;

    /// <summary>下行 nSubType=2：补签。</summary>
    public const int SubTypeResign = 2;

    /// <summary>任务值组（<c>NormalActivityConfig.GID</c>）。</summary>
    public const int TaskGroup = 20;

    /// <summary>今天签没签（<c>TID_SIGNUP</c>）。</summary>
    public const int TaskTodaySigned = 11001;

    /// <summary>本月签到总次数（<c>TID_MONTH</c>）。</summary>
    public const int TaskMonthCount = 11002;

    /// <summary>本月补签次数（<c>TID_RESIGN</c>）。</summary>
    public const int TaskResignCount = 11003;

    /// <summary>「每天 4 点刷新」的偏移秒数（<c>UI_SignActivityPop.lua:103</c>）。</summary>
    public const int DayResetOffsetSeconds = 4 * 3600;

    // ---------------- 补签成本（客户端常量，服务端必须自己复算）----------------
    //
    // 上行只有 nType=2，**不带「用道具还是用钻石」的标志**，所以扣什么由服务端按
    // 「有没有补签券」自己决定 —— 与客户端 UI_SignActivity.lua:225..243 的分支一致：
    //   if GetItemCount(9,3,1,2) > 0 then 扣 1 个补签券
    //   else                             扣 RESIGN_COST(50) 免费钻（走 UseFreeDiamondDialog）
    // 注意客户端那个 Dialog 只是**弹窗确认 + 本地显示**，真正的扣费必须在服务端做。

    /// <summary>补签券的四元组（<c>UI_SignActivity.lua:4 RESIGN_ITEM = {9,3,1,2}</c>）。</summary>
    public static readonly (uint G, uint D, uint P, uint L) ResignItem = (9, 3, 1, 2);

    /// <summary>没有补签券时扣的免费钻数量（<c>UI_SignActivity.lua:3 RESIGN_COST = 50</c>）。</summary>
    public const int ResignDiamondCost = 50;

    /// <summary><see cref="Player.Extra"/> 里本功能的键。</summary>
    public const string ExtraKey = "signup";

    private GameFeatureContext _ctx = null!;
    private SignUpTable _table = SignUpTable.Empty;

    public string Name => "每日签到(SignUpMsg → NormalActivityMsg)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = SignUpTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapFunction(UpFunction, OnSignUpMsg);

        // 登录时做一次跨日/跨月重置并把任务值推给客户端，
        // 否则客户端会拿着上次登录时的「今天已签到」渲染。
        ctx.OnPlayerLogin((session, player) =>
        {
            var changed = ResetIfNeeded(player);
            if (changed.Count == 0)
                return;
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, changed);
            Log.Info($"[session {session.Id}] 签到状态跨日/跨月重置：" +
                     string.Join(" ", changed.Select(c => $"{Player.TaskIdOf(c.Key)}={c.Value}")));
        });
    }

    // =================================================================
    //  上行
    // =================================================================

    private Task OnSignUpMsg(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int nType = ReadInt(param?["nType"], 0);
        switch (nType)
        {
            case TypeSign:
                return DoSign(session, player, resign: false);
            case TypeResign:
                return DoSign(session, player, resign: true);
            case TypeEnergyCompensate:
                // 月卡体力补偿：判定要读 TID_MONTHSIGN_Energy(11005)/_Got(11006) 与月卡到期时间
                // （NormalActivityConfig.lua:526），本服务端还没有月卡模型，先只记录。
                Log.Warn($"[session {session.Id}] SignUpMsg nType=10（月卡体力补偿 nId=" +
                         $"{ReadInt(param?["nId"], 0)}）未实现：缺月卡模型，忽略");
                return Task.CompletedTask;
            default:
                Log.Warn($"[session {session.Id}] SignUpMsg 未知 nType={nType} param={param?.ToJsonString()}");
                return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 签到 / 补签。
    ///
    /// 判定：先跨日重置，再看 <c>TID_SIGNUP</c>。
    /// * 普通签到：今天已签 → 什么都不回（客户端 <c>bSuccess</c> 为假时本就没有提示分支，
    ///   而重复签到在正常流程里不可能出现 —— 界面在 <c>UpdateSigned</c> 之后会隐藏按钮）。
    ///   为了排障，这里仍打一条 WARN。
    /// * 补签：**服务端复算扣费**（见 <see cref="TrySpendResignCost"/>）——
    ///   上行只有 <c>nType=2</c>，不带「用道具还是用钻石」的标志，
    ///   所以按客户端 <c>UI_SignActivity.lua:225..243</c> 的同一分支自己判：
    ///   有补签券(9,3,1,2) 扣 1 个，否则扣 50 免费钻；两者都不够则拒绝。
    /// </summary>
    private Task DoSign(ClientSession session, Player player, bool resign)
    {
        foreach (var _ in ResetIfNeeded(player)) { }   // 先重置（返回值稍后统一推）

        int todaySigned = player.GetTask(TaskGroup, TaskTodaySigned);
        if (!resign && todaySigned > 0)
        {
            Log.Warn($"[session {session.Id}] 今天已签到（task 20/11001={todaySigned}），忽略重复签到");
            return Task.CompletedTask;
        }

        var (month, _) = SignDay(DateTimeOffset.UtcNow);
        int newCount = player.GetTask(TaskGroup, TaskMonthCount) + 1;
        int maxCount = _table.MaxCount(month);
        if (maxCount > 0 && newCount > maxCount)
        {
            Log.Warn($"[session {session.Id}] 本月签到次数已达配置上限 {maxCount}，不再发奖（仍记次数）");
        }

        // ---- 补签扣费（普通签到免费）----
        var costChanges = new List<Item>();
        var costRemoved = new List<Item>();
        int costMoneyId = 0;
        if (resign && !TrySpendResignCost(session, player, costChanges, costRemoved, out costMoneyId))
        {
            // 扣费失败：不记次数、不发奖。客户端没有「补签失败」提示分支，
            // 所以这里只能靠服务端日志暴露（正常流程里客户端会先本地拦住）。
            Log.Warn($"[session {session.Id}] 补签失败：既没有补签券(9,3,1,2) 也不够 " +
                     $"{ResignDiamondCost} 免费钻（现有 {player.GetMoney(MoneyType.FreeDiamond)}）");
            return Task.CompletedTask;
        }

        // ---- 记账 ----
        var changed = new List<KeyValuePair<uint, int>>();
        void SetTask(int id, int value)
        {
            player.SetTask(TaskGroup, id, value);
            changed.Add(new(Player.ComposeTaskId(TaskGroup, id), value));
        }

        SetTask(TaskMonthCount, newCount);
        if (resign)
            SetTask(TaskResignCount, player.GetTask(TaskGroup, TaskResignCount) + 1);
        else
            SetTask(TaskTodaySigned, 1);   // 补签不占用「今天已签」

        var record = ReadRecord(player);
        record["month"] = month;
        record["day"] = SignDay(DateTimeOffset.UtcNow).Day;
        WriteRecord(player, record);

        // ---- 发奖 ----
        var awards = _table.Award(month, newCount);
        var granted = _ctx.Rewards.Grant(player, awards, RewardSource.Extra);
        _ctx.Store.Save(player);

        // ---- 下行：先任务值（客户端 Refresh 读它），再货币/背包，最后 NormalActivityMsg ----
        GameNotify.PushTaskValues(session, changed);
        // 补签的扣费也要推下去（券的数量变化 / 钻石余额）
        if (costMoneyId > 0)
            GameNotify.PushMoney(session, player, costMoneyId);
        GameNotify.PushItems(session, costChanges);
        GameNotify.PushItemsRemoved(session, costRemoved);
        GameNotify.PushRewards(session, player, granted);

        var tbAward = new JsonArray();
        foreach (var r in granted.Rewards)
        {
            tbAward.Add(new JsonArray { (int)r.Genre, (int)r.Detail, (int)r.Particular, (int)r.Level, (int)r.Count });
        }

        Log.Info($"[session {session.Id}] {(resign ? "补签" : "签到")}成功：{month} 月第 {newCount} 次 " +
                 $"→ {(granted.IsEmpty ? "无奖励配置" : granted.Describe())}");

        _ctx.LuaCalls.CallClientFunction(session, DownFunction, new JsonObject
        {
            ["nType"] = MsgTypeSign,
            ["nSubType"] = resign ? SubTypeResign : SubTypeSign,
            ["bSuccess"] = true,
            ["tbAward"] = tbAward,
            ["isRefreshSign"] = true,
        });
        return Task.CompletedTask;
    }

    // =================================================================
    //  补签扣费
    // =================================================================

    /// <summary>
    /// 复算补签成本并扣掉。返回 false 表示两种支付方式都不满足（调用方应拒绝补签）。
    ///
    /// 与客户端 <c>UI_SignActivity.lua:225..243</c> 的分支完全一致：
    /// <code>
    /// if GetItemCount(9,3,1,2) > 0 then  -- 有补签券
    ///     弹「消耗 1 个 &lt;券名&gt;」确认框 → SignUpMsg{nType=2}
    /// else
    ///     弹「消耗 50 &lt;免费钻&gt;」确认框 → MoneyLogic:UseFreeDiamondDialog(50, …) → SignUpMsg{nType=2}
    /// end
    /// </code>
    /// 客户端那两个 Dialog 只负责**弹窗确认与本地显示**，真正扣费必须服务端做 ——
    /// 而上行只有 <c>nType=2</c>，没有「用了哪种」的标志，所以只能用同一优先级复算：
    /// **先券后钻**。
    /// </summary>
    /// <param name="costMoneyId">若用钻石支付，返回被扣的货币 id（供推 pf 1032）；否则 0。</param>
    private bool TrySpendResignCost(ClientSession session, Player player,
                                    List<Item> changed, List<Item> removed, out int costMoneyId)
    {
        costMoneyId = 0;

        // ① 优先扣补签券（跨实例按 createtime 升序，与背包分解口径一致）
        var tickets = player.Items
            .Where(i => i.Genre == ResignItem.G && i.Detail == ResignItem.D
                        && i.Particular == ResignItem.P && i.Level == ResignItem.L)
            .Where(i => i.Count > 0)
            .OrderBy(i => i.CreateTime)
            .ThenBy(i => i.Guid)
            .ToList();

        if (tickets.Count > 0)
        {
            var ticket = tickets[0];
            if (ticket.Count <= 1)
            {
                player.Items.Remove(ticket);
                removed.Add(ticket);
            }
            else
            {
                ticket.Count -= 1;
                changed.Add(ticket);
            }
            Log.Info($"[session {session.Id}] 补签扣 1 个补签券 " +
                     $"{_ctx.ItemTemplates.NameOf(ResignItem.G, ResignItem.D, ResignItem.P, ResignItem.L)}" +
                     $"(guid={ticket.Guid})");
            return true;
        }

        // ② 没券则扣免费钻
        int diamond = player.GetMoney(MoneyType.FreeDiamond);
        if (diamond < ResignDiamondCost)
            return false;

        player.SetMoney(MoneyType.FreeDiamond, diamond - ResignDiamondCost);
        costMoneyId = MoneyType.FreeDiamond;
        Log.Info($"[session {session.Id}] 补签扣 {ResignDiamondCost} 免费钻（余 {player.GetMoney(MoneyType.FreeDiamond)}）");
        return true;
    }

    // =================================================================
    //  跨日 / 跨月重置
    // =================================================================

    /// <summary>
    /// 按「服务器时间 - 4 小时」的日历日/月做重置，返回**有变化**的任务值（供 pf 1026 增量推送）。
    /// 跨日 → 清 11001；跨月 → 同时清 11002 / 11003。
    /// </summary>
    internal List<KeyValuePair<uint, int>> ResetIfNeeded(Player player)
    {
        var changed = new List<KeyValuePair<uint, int>>();
        var (month, day) = SignDay(DateTimeOffset.UtcNow);
        var record = ReadRecord(player);
        int lastMonth = ReadInt(record["month"], 0);
        int lastDay = ReadInt(record["day"], 0);

        bool newDay = lastDay != day || lastMonth != month;
        bool newMonth = lastMonth != month;

        void SetTask(int id, int value)
        {
            if (player.GetTask(TaskGroup, id) == value)
                return;
            player.SetTask(TaskGroup, id, value);
            changed.Add(new(Player.ComposeTaskId(TaskGroup, id), value));
        }

        if (newDay)
            SetTask(TaskTodaySigned, 0);
        if (newMonth)
        {
            SetTask(TaskMonthCount, 0);
            SetTask(TaskResignCount, 0);
        }

        if (newDay || newMonth)
        {
            record["month"] = month;
            record["day"] = day;
            WriteRecord(player, record);
        }
        return changed;
    }

    /// <summary>
    /// 「签到用的今天」= 服务器时间减 4 小时后的 (月, 日)。
    /// 口径取证 <c>UI_SignActivityPop.lua:103</c>：<c>local curtime = GetTime() - 4 * 3600</c>，
    /// 随后 <c>os.date("%m"/"%d", curtime)</c> —— 客户端用的是**本地时区**，
    /// 服务端这里统一用 UTC（本地验收环境 UTC 与设备时区差异只影响「几点换日」，不影响链路）。
    /// </summary>
    internal static (int Month, int Day) SignDay(DateTimeOffset now)
    {
        var t = now.AddSeconds(-DayResetOffsetSeconds).UtcDateTime;
        return (t.Month, t.Day);
    }

    private static JsonObject ReadRecord(Player player)
    {
        if (player.Extra.TryGetValue(ExtraKey, out var node) && node is JsonObject obj)
            return obj;
        return new JsonObject();
    }

    private static void WriteRecord(Player player, JsonObject record)
        => player.Extra[ExtraKey] = record;

    private static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try { return node.GetValue<int>(); }
        catch { return double.TryParse(node.ToString(), out var d) ? (int)d : fallback; }
    }
}
