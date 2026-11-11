using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 活动/福利页的「限时领电力」（活动 id <b>8</b> <c>activity.power</c>，Template=4，
/// 界面 <c>UI_EnergyGet</c>）。
///
/// <para>取证：<c>work/activity-award-forensics.md</c>（1476 行，子 agent 产出）。</para>
///
/// <para><b>0. 为什么先做这个</b></para>
/// 它是**常驻**活动（<c>ActivityList.txt</c> 的 <c>EndTime</c> 列为空 ⇒ <c>nEndTime = 0</c>
/// ⇒ 客户端 <c>CheckTime</c>(<c>NormalActivityConfig.lua:1630..1642</c>) 的
/// <c>(nTime &lt; nEndTime or nEndTime == 0)</c> 恒真）。
/// 225 个活动里只有 12 个还开放，而这一个是玩家**每天必点两次**的入口，
/// 此前点了完全没反馈。
///
/// <para><b>1. 通道（★ 第八种错误约定：全链路无 <c>nError</c> 检查）</b></para>
/// <list type="bullet">
/// <item>上行 <c>sFunction = "NormalActivityGetAward"</c>，参数只有
///   <c>{nActivityId = 8}</c>（<c>UI_EnergyGet.lua:129..131</c>）——
///   ★ **不带时段号**，服务端必须自己按当前时间判在哪个时段。</item>
/// <item>下行 <c>sFunction = "NormalActivityMsg"</c>，<c>nType = 4</c>
///   （<c>NormalActivityConfig.lua:1692..1700</c>）。</item>
/// </list>
/// ★★ <c>OnNormalActivityMsg</c> 全链路**没有任何 <c>nError</c> 检查</c> ⇒
/// 成功包**不需要**带 <c>nError</c>，失败**只能静默不回**（回了也没有分支处理）。
/// 这与本项目多数字符串命令族（必须带 <c>nError = 0</c>）**相反**。
///
/// <para><b>2. 状态：group 20（与每日签到同组）/ taskId = <c>10000 + 时段Id</c></b></para>
/// 取证 <c>UI_EnergyGet.lua:80</c>：<c>if me:GetTaskValue(20, 10000 + tbCurConfig.nId) == 1</c>
/// —— ★ 判据是 <b><c>== 1</c></b>（不是 <c>&gt; 0</c>）⇒ 只能写 1，别写 2、别写位域。
///
/// ★★ **每日重置的责任在服务端**：客户端从不写这两个 taskId
/// （<c>grep -rn 'SetTaskValue.*10000' extracted/lua/</c> 零命中）。
/// 不清零 ⇒ 第二天永远显示「已领」。这里复用 <see cref="SignUpFeature"/> 的
/// 「服务器时间 - 4 小时的日历日」口径（<see cref="SignUpFeature.SignDay"/>），
/// 两个功能在同一个 group 20 上，重置口径必须一致。
///
/// <para><b>3. ★ <c>bFullAdd</c> 不是「成功/失败」，是「有没有超上限」</b></para>
/// 文案不在任何 txt 里，而在 <c>work/apk/assets/lang.u</c> 这个 AssetBundle 里，
/// 子 agent 解出了原文：
/// <list type="bullet">
/// <item><c>bFullAdd = true</c> → <c>ui.UI_EnergyGet.GetTips</c> = 「<b>成功收取%d电力</b>」</item>
/// <item><c>bFullAdd = false</c> → <c>ui.Error.20026</c> = 「<b>拥有电力已达上限，本次获得%d电力</b>」</item>
/// </list>
/// ⇒ 两条都是**发放成功**的提示，只是后者提示玩家溢出了。所以体力满时**照发**、
/// 只把 <c>bFullAdd</c> 置 false，不要拒绝。
/// </summary>
public sealed class EnergyGetFeature : IGameFeature
{
    /// <summary>上行 sFunction（<c>UI_EnergyGet.lua:130</c>）。</summary>
    public const string UpFunction = "NormalActivityGetAward";

    /// <summary>下行 sFunction（<c>S2CCall.lua:329</c>，与签到共用）。</summary>
    public const string DownFunction = "NormalActivityMsg";

    /// <summary>下行 <c>nType = 4</c>：领电力族（<c>NormalActivityConfig.lua:1692</c>）。</summary>
    public const int MsgTypeEnergy = 4;

    /// <summary>「限时领电力」的活动 id（<c>ActivityList.txt</c> 第 8 行 <c>activity.power</c>）。</summary>
    public const int PowerActivityId = 8;

    /// <summary>任务值组（与签到同组，<c>NormalActivityConfig.GID = 20</c>）。</summary>
    public const int TaskGroup = SignUpFeature.TaskGroup;

    /// <summary>
    /// 「该时段今天领过了」的 taskId 基数：<c>10000 + EnergyActivity.Id</c>
    /// （<c>UI_EnergyGet.lua:80</c>）。时段 1 → 10001、时段 2 → 10002。
    /// </summary>
    public const int TaskSlotBase = 10000;

    /// <summary>已领取的标记值 —— ★ 客户端判 <c>== 1</c>，只能是 1。</summary>
    public const int TaskClaimed = 1;

    /// <summary><see cref="Player.Extra"/> 里本功能的键（记「上次重置的日历日」）。</summary>
    public const string ExtraKey = "energyget";

    private GameFeatureContext _ctx = null!;
    private EnergyActivityTable _table = EnergyActivityTable.Empty;

    public string Name => "限时领电力(NormalActivityGetAward 活动 8 → NormalActivityMsg nType=4)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = EnergyActivityTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapFunction(UpFunction, OnGetAward);

        // 登录时做一次跨日重置并推 pf 1026，否则客户端会拿着昨天的「已领」渲染出灰箱子。
        ctx.OnPlayerLogin((session, player) =>
        {
            var changed = ResetIfNeeded(player);
            if (changed.Count == 0)
                return;
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, changed);
            Log.Info($"[session {session.Id}] 领电力状态跨日重置：" +
                     string.Join(" ", changed.Select(c => $"{Player.TaskIdOf(c.Key)}={c.Value}")));
        });
    }

    // =================================================================
    //  上行
    // =================================================================

    /// <summary>
    /// <c>NormalActivityGetAward</c> 是**多个活动共用**的上行（20+ 个构造点），
    /// 这里只认 <c>nActivityId == 8</c>；其余活动交给别的 Feature（或暂不实现时打日志）。
    /// </summary>
    private Task OnGetAward(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ 真机上行可能是 JsonArray（本项目踩过：JsonNode["key"] 对 JsonArray 会抛），
        //   所以先确认是对象再取键。
        var obj = param as JsonObject;
        int activityId = ReadInt(obj?["nActivityId"], 0);

        if (activityId != PowerActivityId)
        {
            // nType=1 的成就奖励走 MissionMgrMsg（另一条链），其余多数属已过期活动。
            Log.Warn($"[session {session.Id}] NormalActivityGetAward 活动 {activityId} 未实现" +
                     $"（本 Feature 只处理 {PowerActivityId} 限时领电力）param={param?.ToJsonString()}");
            return Task.CompletedTask;
        }

        return DoGetEnergy(session, player);
    }

    /// <summary>
    /// 领电力：判时段 → 判今天领过没 → 发电力（可超上限）→ 置任务值 → 推 pf 1026 + 货币 + 回包。
    /// ★ 失败一律**静默不回**（本族无 nError 通道，见类注释）。
    /// </summary>
    private Task DoGetEnergy(ClientSession session, Player player)
    {
        // 先跨日重置，避免「昨天领过」挡住今天。
        var resetChanged = ReadyForToday(session, player);

        // ★ 用本地时间判时段（不是 UTC）：客户端读的是 Game.Utils.DateTime('HHmm')
        //   → Game.ServerStatus.LocalTime（RVA 0x17C1858）= 服务端时间戳 + 本地时区偏移，
        //   即玩家看到的是本地时分。用 UTC 判会与客户端错开整个时区差
        //   （实测设备 CST/UTC+8、宿主 JST/UTC+9 ⇒ 客户端显示「可领」而服务端拒绝）。
        var now = DateTimeOffset.Now;
        var slot = _table.SlotAt(now);
        if (slot is null)
        {
            // 不在任何领取时段内。客户端本来会把按钮置灰（UI_EnergyGet.lua:118..127 有倒计时），
            // 走到这里说明是 GM 直发或客户端时区与服务端不一致 —— 静默忽略。
            Log.Warn($"[session {session.Id}] 领电力：当前 {now.UtcDateTime:HH:mm} 不在任何时段内" +
                     $"（配置 {_table.Describe()}），静默忽略");
            Flush(session, player, resetChanged);
            return Task.CompletedTask;
        }

        int taskId = TaskSlotBase + slot.Id;
        if (player.GetTask(TaskGroup, taskId) == TaskClaimed)
        {
            Log.Warn($"[session {session.Id}] 领电力：时段 {slot.Id} 今天已领过" +
                     $"（task {TaskGroup}/{taskId}=1），静默忽略");
            Flush(session, player, resetChanged);
            return Task.CompletedTask;
        }

        // ---- 发电力 ----
        // ★ 体力满了也照发（bFullAdd = false 只是提示文案不同，见类注释）。
        int before = player.Vigour;
        int max = _ctx.PlayerExp.MaxVigour(player.Level);
        bool wouldOverflow = before + slot.Energy > max;

        player.AddMoney(MoneyType.Vigour, slot.Energy);
        player.SetTask(TaskGroup, taskId, TaskClaimed);
        resetChanged.Add(new(Player.ComposeTaskId(TaskGroup, taskId), TaskClaimed));

        _ctx.Store.Save(player);

        // 顺序：先任务值（客户端 Refresh 读它决定箱子灰不灰）→ 再货币 → 最后回包。
        GameNotify.PushTaskValues(session, resetChanged);
        GameNotify.PushMoney(session, player, MoneyType.Vigour);

        _ctx.LuaCalls.CallClientFunction(session, DownFunction, new JsonObject
        {
            ["nType"] = MsgTypeEnergy,
            // ★ bFullAdd = 「没有超上限」：true → 「成功收取%d电力」；false → 「已达上限，本次获得%d电力」
            ["bFullAdd"] = !wouldOverflow,
            // ★ nEnergy 必发：它是两条文案里的 %d，缺了客户端 string.format 会抛
            ["nEnergy"] = slot.Energy,
        });

        Log.Info($"[session {session.Id}] 领电力成功：时段 {slot.Id}（{slot.Describe()}）" +
                 $"+{slot.Energy} 体力 {before}→{player.Vigour}（上限 {max}，" +
                 $"bFullAdd={!wouldOverflow}），置 task {TaskGroup}/{taskId}=1");
        return Task.CompletedTask;
    }

    /// <summary>跨日重置并返回待推的任务值（不立即推，交由调用方与后续变更合并成一条 pf 1026）。</summary>
    private List<KeyValuePair<uint, int>> ReadyForToday(ClientSession session, Player player)
    {
        var changed = ResetIfNeeded(player);
        if (changed.Count > 0)
            Log.Info($"[session {session.Id}] 领电力：跨日重置 {changed.Count} 项");
        return changed;
    }

    /// <summary>只有重置、没有领取时也要把重置结果推下去（否则客户端箱子一直是灰的）。</summary>
    private void Flush(ClientSession session, Player player, List<KeyValuePair<uint, int>> changed)
    {
        if (changed.Count == 0)
            return;
        _ctx.Store.Save(player);
        GameNotify.PushTaskValues(session, changed);
    }

    // =================================================================
    //  跨日重置
    // =================================================================

    /// <summary>
    /// 单测钩子：只加载配置表，不做其余装配。
    /// <see cref="ResetIfNeeded"/> 只依赖时段列表，用这个足以单独测重置逻辑。
    /// </summary>
    internal void LoadTableForTest(string settingDir)
        => _table = EnergyActivityTable.Load(Path.GetFullPath(settingDir));

    /// <summary>
    /// 「领电力用的今天」= <b>本地时间</b>减 4 小时后的 (月, 日)。
    ///
    /// <para>★ 与 <see cref="SignUpFeature.SignDay"/> 的差别**只在时区基准**：
    /// 那个用 <c>UtcDateTime</c>，这里用本地时间。理由是本功能的**时段判定**必须跟客户端
    /// 的本地时分对齐（见 <see cref="EnergyActivityTable"/> 注释），
    /// 若「换日」再用 UTC，就会出现「时段按本地算、清零按 UTC 算」的错位
    /// —— 例如本地 08:00（UTC 前一天 23:00）时段已经翻新但标记还没清。</para>
    ///
    /// <para>4 小时偏移沿用同一个游戏惯例（<c>UI_SignActivityPop.lua:103</c>
    /// 的 <c>GetTime() - 4*3600</c>），本作所有「每日刷新」都是凌晨 4 点。</para>
    /// </summary>
    internal static (int Month, int Day) EnergyDay(DateTimeOffset now)
    {
        var t = now.AddSeconds(-SignUpFeature.DayResetOffsetSeconds);
        return (t.Month, t.Day);
    }

    /// <summary>
    /// 按 <see cref="EnergyDay"/> 的日历日重置全部时段标记，返回**有变化**的任务值。
    /// </summary>
    internal List<KeyValuePair<uint, int>> ResetIfNeeded(Player player)
    {
        var changed = new List<KeyValuePair<uint, int>>();
        var (month, day) = EnergyDay(DateTimeOffset.Now);
        var record = ReadRecord(player);
        int lastMonth = ReadInt(record["month"], 0);
        int lastDay = ReadInt(record["day"], 0);

        if (lastDay == day && lastMonth == month)
            return changed;

        foreach (var slot in _table.Slots)
        {
            int taskId = TaskSlotBase + slot.Id;
            if (player.GetTask(TaskGroup, taskId) == 0)
                continue;
            player.SetTask(TaskGroup, taskId, 0);
            changed.Add(new(Player.ComposeTaskId(TaskGroup, taskId), 0));
        }

        record["month"] = month;
        record["day"] = day;
        WriteRecord(player, record);
        return changed;
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
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            try { return (int)node.GetValue<double>(); }
            catch { return fallback; }
        }
    }
}
