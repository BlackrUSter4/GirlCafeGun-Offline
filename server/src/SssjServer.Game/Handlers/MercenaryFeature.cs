using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 佣兵赌注（新爬塔）活动的**入口三跳** —— LuaCall **320** 进塔 / **330** 开始 / **329** 领周奖励。
/// 取证 `extracted/lua/MercenaryLogic.lua` + `UI_NewNormalTower*.lua`、台账 `docs/05` §31。
///
/// <para><b>0. ★★★ 这个域先前被误判成「死内容」，是真机推翻了静态推理</b></para>
/// 本地快照 `MercenaryTower.txt` 只有 **102** 个 WeekId，按公式算档期到 2022-04 就结束
/// ⇒ 静态结论是「早已过期、做了玩家也进不去」。
/// **但真机自报 <c>towerCount=891</c>、最后一周结算在 2037-05、`GetCurTowerConfig()` 非 nil，
/// 且服务端确实收到了 `unhandled LuaCall cmd=320`** ⇒ 它是活内容、是一条真实断头路。
/// ⇒ 教训：`extracted/setting/` 的快照**可能比设备上的表少行**，
/// 「按本地表行数推断档期」会得出完全相反的结论；判档期要用**公式反解**（<see cref="MercenaryTable.CurrentWeekId"/>）。
///
/// <para><b>1. 客户端侧的完整链路（决定本轮只做前三跳）</b></para>
/// <c>Task_Step</c>（group 64 偏移 11）是状态机：<b>0</b> 开始 → <b>1</b> 等待选 buff →
/// <b>2</b> 等待编队 → <b>3</b> 闯关 → <b>4</b> 结算。
/// 客户端 <c>OnStartTower</c>（`MercenaryLogic.lua:550..563`）**按 step 决定开哪个界面**：
/// step 1/4 → `UI_NewNormalTowerFirstBuff`、2 → `...Formation2`、3 → `...LevelInfo`。
/// ⇒ 本轮实现 320/330/329，让「点进去能看到塔、能开一局、能领周奖励」先通；
/// 战斗链（323 进关 / 324 通关 / 325 失败 / 326 结算 / 331,332 回溯 / 333 跳关）
/// 依赖关卡掉落与战斗结算，工作量另一个量级，留到后续（§31.6 列了顺序）。
///
/// <para><b>2. ★★ 320 的回包只有一个字段，但少了它整条链断掉</b></para>
/// <c>OnGetTower</c>（`:541..549`）：<c>if tbParam and tbParam.nWeekId then ... UI.CloseAndOpen("UI_NewNormalTower", tb)</c>
/// ⇒ **必须回 <c>{nWeekId = &lt;当前周&gt;}`**，且那个 weekId 必须能在客户端的
/// <c>tbMercenaryTower</c> 里查到（否则 <c>tb</c> 为 nil、界面不开、**无任何报错**）。
/// ⇒ 服务端要发的是**按公式算出的当前周**，不是本地快照里的最大 WeekId。
///
/// <para><b>3. ★★ 330 的失败通道是 <c>bStart</c> 而不是 <c>nError</c></b></para>
/// <c>OnStartTower</c>（`:550`）：<c>if tbParam and tbParam.bStart then 开界面 else 弹「次数不足」</c>
/// ⇒ 成功必须带 <c>bStart = true</c>；次数不足时回 <c>bStart = false</c>（**不是** `nError`）。
/// 这是本项目见到的又一种失败约定形状（bool 开关式）。
///
/// <para><b>4. ★ 每周重置</b></para>
/// <c>Task_WeekClear = 6</c> / <c>Task_SettlementClear = 61</c> / <c>Task_Max = 180</c>
/// 划出两个可清区段。本实现在「检测到 <c>Task_CurWeekId</c> 与当前周不一致」时
/// 清 6..180 并重置次数为 <c>WeekCount</c>，同时把上周的 <c>Task_WeekMoney</c>
/// 存进 <c>Task_CacheLastWeekMoney</c>（`:10`/`:5` 的语义）。
/// </summary>
public sealed class MercenaryFeature : IGameFeature
{
    /// <summary>320 进入佣兵活动（取当前周）。</summary>
    public const int CmdGetTower = 320;

    /// <summary>329 领本周代币奖励。</summary>
    public const int CmdGetWeekReward = 329;

    /// <summary>
    /// LuaCall.Mercenary_SelectBuff = 321（第十三轮补）。
    /// 上行 <c>{nWeekId, buff = [id…], debuff = [id…]}</c>
    /// （<c>UI_NewNormalTowerFirstBuff.lua:166</c>）。
    /// </summary>
    public const int CmdSelectBuff = 321;

    /// <summary>
    /// LuaCall.Mercenary_SelectFormation = 322（第十三轮补）。
    /// 上行 <c>{nWeekId}</c>（<c>UI_NewNormalTowerFormation2.lua:1063/1070</c>）。
    /// </summary>
    public const int CmdSelectFormation = 322;

    /// <summary>
    /// buff / debuff 槽位数 = <b>20</b>（<c>MercenaryLogic.lua:38</c> <c>BUFF_COUNT = 20</c>）。
    /// </summary>
    public const int BuffSlots = 20;

    /// <summary>
    /// 一次最多选几个 buff = <b>3</b>（<c>MercenaryLogic.lua:42</c> <c>SELECT_BUFF_COUNT = 3</c>）。
    /// ★ 服务端独立复核，不信客户端。
    /// </summary>
    public const int SelectBuffLimit = 3;

    /// <summary>
    /// buff id 存在 group 64 的偏移 <b>71..90</b>（<c>MercenaryLogic.lua:25</c>
    /// <c>Task_BuffIds = 70</c>，注释写明「71-&gt;90」）。
    /// </summary>
    public const int TaskBuffIdsBase = 70;

    /// <summary>
    /// debuff id 存在偏移 <b>91..110</b>（<c>MercenaryLogic.lua:26</c> <c>Task_DeuffIds = 90</c>）。
    /// ★ 注意原厂拼写就是 <c>Deuff</c>（少个 b）。
    /// </summary>
    public const int TaskDebuffIdsBase = 90;

    /// <summary>
    /// ★★ 通用失败码。本族判据是 <c>nError &gt; 0</c>（**数值比较**，
    /// <c>UI_NewNormalTowerFirstBuff.lua:170</c> / <c>UI_NewNormalTowerFormation2.lua:1074</c>）
    /// ⇒ 与 330 的 <c>bStart</c> 布尔开关式**不是同一种**，同一个域里两种失败形状。
    /// </summary>
    public const int ErrGeneric = 1;

    /// <summary>LuaCall.Mercenary_EnterTower = 323「请求进入关卡」（第十三轮补）。</summary>
    public const int CmdEnterTower = 323;

    /// <summary>LuaCall.Mercenary_PassTower = 324「请求关卡成功」（第十三轮补）。</summary>
    public const int CmdPassTower = 324;

    /// <summary>LuaCall.Mercenary_FailTower = 325「请求关卡失败」（第十三轮补）。</summary>
    public const int CmdFailTower = 325;

    /// <summary>LuaCall.Mercenary_Settlement = 326「本轮结算」（第十三轮补）。</summary>
    public const int CmdSettlement = 326;

    /// <summary>
    /// 关卡通关次数的任务值偏移基址 = <b>130</b>（<c>MercenaryLogic.lua:29</c>
    /// <c>Task_LevelPass = 130</c>，注释「131-&gt;170  关卡通关次数  每轮清空」）。
    /// 实际偏移 = <c>130 + 关卡在本周清单里的序号(1-based)</c>。
    /// </summary>
    public const int TaskLevelPassBase = 130;

    /// <summary>
    /// 每周关卡通关**总**次数的偏移基址 = <b>20</b>（<c>MercenaryLogic.lua:20</c>
    /// <c>Task_WeekLevelPass = 20</c>，「21-&gt;60」）。★ 与上面那个的区别：这个**不每轮清空**。
    /// </summary>
    public const int TaskWeekLevelPassBase = 20;

    /// <summary>关卡通关次数槽位数（130+1..130+40）。</summary>
    public const int LevelPassSlots = 40;

    /// <summary>LuaCall.Mercenary_DeleteDebuff = 327「删除 Debuff」（第十三轮补）。</summary>
    public const int CmdDeleteDebuff = 327;

    /// <summary>LuaCall.Mercenary_SelectFate = 328「选择 fate」（第十三轮补）。</summary>
    public const int CmdSelectFate = 328;

    /// <summary>LuaCall.Mercenary_RecallLevelData = 331「请求回溯到之前节点数据」（第十三轮补）。</summary>
    public const int CmdRecallLevelData = 331;

    /// <summary>LuaCall.Mercenary_Recall = 332「回溯到之前节点」（第十三轮补）。</summary>
    public const int CmdRecall = 332;

    /// <summary>LuaCall.Mercenary_SkipLevel = 333「跳过关卡」（第十三轮补）。</summary>
    public const int CmdSkipLevel = 333;

    /// <summary>
    /// 每关的 fate 选择结果存在 group 64 的这个偏移基址 + 关卡槽位。
    /// ★ 原厂位置**未取证**（`MercenaryLogic.lua` 里没有对应的 <c>Task_*</c> 常量），
    /// 本服自选 <b>240</b>（紧接 <c>Task_RecallData</c> 的 200..240 之后，不与已知偏移冲突：
    /// 已知占用是 0..12 / 20..60 / 61 / 70..110 / 130..170 / 200..240 / 1999）。
    /// 客户端 <c>OnSelectFateCallback</c> 不读任何字段，所以这个位置只服务于服务端自己的持久化。
    /// </summary>
    public const int TaskFateBase = 240;

    /// <summary>
    /// 回溯节点数据的任务值偏移基址 = <b>200</b>（<c>MercenaryLogic.lua:35</c>
    /// <c>Task_RecallData = 200</c>，注释「保存每关回溯数据 200-240」）。
    /// </summary>
    public const int TaskRecallDataBase = 200;

    /// <summary>
    /// 跳关的解锁判据除数 = <b>100</b>。取证 <c>MercenaryLogic:CheckSkipLevel</c>
    /// （<c>MercenaryLogic.lua:775..788</c>）：<c>maxIdx = max(上周积分, 本周积分) // 100</c>，
    /// 关卡在本周清单里的序号 <c>idx &lt;= maxIdx</c> 才能跳。
    /// </summary>
    public const int SkipScorePerLevel = 100;

    /// <summary>330 开始一局。</summary>
    public const int CmdStartTower = 330;

    private GameFeatureContext _ctx = null!;
    private MercenaryTable _table = MercenaryTable.Empty;

    public string Name => "佣兵赌注入口（LuaCall 320 进塔 / 330 开始 / 329 领周奖励）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = MercenaryTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(CmdGetTower, OnGetTower);
        ctx.MapLuaCall(CmdStartTower, OnStartTower);
        ctx.MapLuaCall(CmdGetWeekReward, OnGetWeekReward);
        ctx.MapLuaCall(CmdSelectBuff, OnSelectBuff);
        ctx.MapLuaCall(CmdSelectFormation, OnSelectFormation);
        ctx.MapLuaCall(CmdEnterTower, OnEnterTower);
        ctx.MapLuaCall(CmdPassTower, OnPassTower);
        ctx.MapLuaCall(CmdFailTower, OnFailTower);
        ctx.MapLuaCall(CmdSettlement, OnSettlement);
        ctx.MapLuaCall(CmdDeleteDebuff, OnDeleteDebuff);
        ctx.MapLuaCall(CmdSelectFate, OnSelectFate);
        ctx.MapLuaCall(CmdRecallLevelData, OnRecallLevelData);
        ctx.MapLuaCall(CmdRecall, OnRecall);
        ctx.MapLuaCall(CmdSkipLevel, OnSkipLevel);
    }

    // ================= 320 进塔 =================

    /// <summary>
    /// 取当前周。上行**无参**（`MercenaryLogic.lua:538` <c>CallServer(LuaCall.Mercenary_GetTower)</c>）。
    /// 回包 <c>{nWeekId}</c> —— 见类注释 §2：weekId 错了界面静默不开。
    /// </summary>
    private Task OnGetTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        long now = Now();
        int week = _table.CurrentWeekId(now);
        if (week <= 0)
        {
            // 活动未开放：客户端那边 GetTower 本来就会先自查 GetCurTowerConfig() 并拦下，
            // 走到这里说明两端对档期的判断不一致 —— 回 0 让界面别开，并留日志便于排查。
            Log.Warn($"[session {session.Id}] LuaCall 320 佣兵进塔：服务端判活动未开放" +
                     $"（now={now}、活动始于 {_table.BeginTime}）—— 与客户端判断不一致，请核对时区/配置");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nWeekId"] = 0 });
            return Task.CompletedTask;
        }

        ResetIfNewWeek(player, week);
        _ctx.Store.Save(player);

        // ★ 先把任务值推下去（塔界面读 Task_WeekMoney / Task_WeekCount），再回包开界面
        PushState(session, player);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nWeekId"] = week });

        Log.Info($"[session {session.Id}] LuaCall 320 佣兵进塔：当前第 {week} 周" +
                 $"（剩余次数 {Get(player, MercenaryTable.TaskWeekCount)}/{_table.WeekCount}、" +
                 $"本周代币 {Get(player, MercenaryTable.TaskWeekMoney)}、" +
                 $"step={Get(player, MercenaryTable.TaskStep)}）");
        return Task.CompletedTask;
    }

    // ================= 330 开始一局 =================

    /// <summary>
    /// 开始一局。上行 <c>{nWeekId}</c>（`UI_NewNormalTower.lua:114`）。
    ///
    /// ★★ 失败通道是 <c>bStart = false</c>**不是** `nError`（类注释 §3）。
    /// 成功时：若当前 step 是 0（全新一局）则扣一次次数并置 step=1（等待选 buff）；
    /// 若 step 已经 &gt; 0 说明是「续上未打完的一局」⇒ **不扣次数**、原样回 <c>bStart=true</c>
    /// 让客户端按 step 跳到对应界面（`MercenaryLogic.lua:553..561`）。
    /// </summary>
    private Task OnStartTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        long now = Now();
        int week = _table.CurrentWeekId(now);
        if (week <= 0)
        {
            Fail(session, cmd, "活动未开放");
            return Task.CompletedTask;
        }

        ResetIfNewWeek(player, week);

        int step = Get(player, MercenaryTable.TaskStep);
        int left = Get(player, MercenaryTable.TaskWeekCount);

        if (step == MercenaryTable.StepStart)
        {
            // 全新一局：要有剩余次数
            if (left <= 0)
            {
                _ctx.Store.Save(player);
                Fail(session, cmd, $"剩余次数为 0（每周 {_table.WeekCount} 次）");
                return Task.CompletedTask;
            }
            Set(player, MercenaryTable.TaskWeekCount, left - 1);
            Set(player, MercenaryTable.TaskStep, MercenaryTable.StepSelectBuff);
            Set(player, MercenaryTable.TaskCurWeekId, week);
        }

        _ctx.Store.Save(player);

        int newStep = Get(player, MercenaryTable.TaskStep);
        PushState(session, player);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["bStart"] = true,
            ["nWeekId"] = week,
        });

        Log.Info($"[session {session.Id}] LuaCall 330 佣兵开始：第 {week} 周、" +
                 $"step {step} → {newStep}（{StepName(newStep)}）、" +
                 $"剩余次数 {Get(player, MercenaryTable.TaskWeekCount)}/{_table.WeekCount}" +
                 $"{(step == MercenaryTable.StepStart ? "（新开一局，扣 1 次）" : "（续上未打完的一局，不扣次数）")}");
        return Task.CompletedTask;
    }

    // ================= 329 领本周代币 =================

    /// <summary>
    /// 领本周可领的代币。上行**无参**（`UI_NewNormalTower.lua`）。
    ///
    /// 客户端消费点（`LuaCall.lua:3462..3467`）：
    /// <code>
    /// if tbParam then UI.OpenAsChild('UI_RewardNormal', tbParam.tbReward) end
    /// if UI.IsOpen("UI_NewNormalTower") then UI.Call("UI_NewNormalTower", "Refresh") end
    /// </code>
    /// ★ 两个 <c>if</c> 相互独立 ⇒ **没得领时不要回包**（原服也是 <c>if ret then CallClient</c>，
    /// `LuaCall.lua:3458`）；回一个空 <c>tbReward</c> 会弹一个空奖励框。
    /// ★ <c>tbReward</c> 是 <c>[[G,D,P,L,N],…]</c> 位置数组。
    /// </summary>
    private Task OnGetWeekReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int canGet = Get(player, MercenaryTable.TaskCanGetMoney);
        if (canGet <= 0)
        {
            // ★ 有意不回包（见方法注释）—— 原服 `if ret then CallClient` 同款
            Log.Info($"[session {session.Id}] LuaCall 329 佣兵领周奖励：可领代币为 0，按原服不回包");
            return Task.CompletedTask;
        }

        // 周奖励模板是「每 1 点代币给什么」（实测 [15-44-1-1-1] = 佣兵代币 ×1）⇒ 按可领数量放大
        var awards = new List<int[]>();
        foreach (var r in _table.WeekRewards)
            awards.Add(new[] { r[0], r[1], r[2], r[3], r[4] * canGet });

        var granted = _ctx.Rewards.Grant(player, awards, RewardSource.Extra);
        Set(player, MercenaryTable.TaskCanGetMoney, 0);
        _ctx.Store.Save(player);

        var tbReward = new JsonArray();
        foreach (var a in awards)
            tbReward.Add(new JsonArray(a[0], a[1], a[2], a[3], a[4]));

        // ★ 先推数据（界面 Refresh 会重读任务值与货币），再回包
        PushState(session, player);
        GameNotify.PushRewards(session, player, granted);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbReward"] = tbReward });

        Log.Info($"[session {session.Id}] LuaCall 329 佣兵领周奖励：可领 {canGet} → " +
                 $"发奖 {granted.Describe()}，已清零");
        return Task.CompletedTask;
    }

    // ================= 每周重置 =================

    /// <summary>
    /// 跨周时清「每周清空」与「结算清空」区段并补满次数。
    ///
    /// 区段来自 `MercenaryLogic.lua:31..33`：<c>Task_WeekClear = 6</c>、
    /// <c>Task_SettlementClear = 61</c>、<c>Task_Max = 180</c>
    /// ⇒ 清 <c>6..180</c> 即覆盖两段（含 buff/debuff/血量/通关次数等一轮内的状态）。
    /// ★ 清之前把上周的 <c>Task_WeekMoney</c> 存进 <c>Task_CacheLastWeekMoney</c>（`:5` 的语义）。
    /// </summary>
    internal void ResetIfNewWeek(Player player, int week)
    {
        int recorded = Get(player, MercenaryTable.TaskCurWeekId);
        if (recorded == week)
            return;

        int lastWeekMoney = Get(player, MercenaryTable.TaskWeekMoney);
        for (int off = MercenaryTable.TaskWeekClear; off <= MercenaryTable.TaskMax; off++)
            Set(player, off, 0);

        Set(player, MercenaryTable.TaskCacheLastWeekMoney, lastWeekMoney);
        Set(player, MercenaryTable.TaskWeekCount, _table.WeekCount);
        Set(player, MercenaryTable.TaskCurWeekId, week);
        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepStart);

        Log.Info($"[佣兵] 跨周重置：{recorded} → 第 {week} 周，次数补到 {_table.WeekCount}" +
                 $"、上周代币 {lastWeekMoney} 已存入缓存");
    }

    // ================= 工具 =================

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static int Get(Player player, int offset)
        => player.GetTask(MercenaryTable.Group, offset);

    private static void Set(Player player, int offset, int value)
        => player.SetTask(MercenaryTable.Group, offset, value);

    // ================================================================ 321 选 buff / 322 选编队

    /// <summary>
    /// 321 选择 buff/debuff（本轮闯关的加成与减益）。
    ///
    /// <para><b>上行</b></para>
    /// <c>{nWeekId, buff = [id…], debuff = [id…]}</c>（<c>UI_NewNormalTowerFirstBuff.lua:155..166</c>；
    /// 界面把 <c>tbSelectBuff[id] = 次数</c> 展开成**可重复的 id 数组**）。
    ///
    /// <para><b>★★★ 存储是位域：bit0 = 激活标志、bit1..31 = buff id</b></para>
    /// 取证 <c>MercenaryLogic:GetBuffs</c>（<c>MercenaryLogic.lua:343..359</c>）：
    /// <code>
    /// nTaskValue = me:GetTaskValue(64, Task_BuffIds + i)      -- i = 1..20
    /// nFlag = LoadBits(nTaskValue, 0, 0)                      -- bit0
    /// nId   = LoadBits(nTaskValue, 1, 31)                     -- bit1..31
    /// if bBuff and nFlag == 0 then 可用 buff
    /// elseif not bBuff and nFlag == 1 then 已激活 debuff
    /// </code>
    /// ⇒ 写 <c>id</c> 时必须**左移一位**（<c>id &lt;&lt; 1</c>），直接写 id 会让客户端
    /// 把它解成「id/2 且激活标志 = id 的最低位」—— 静默错乱、不抛不报错。
    /// ★ <c>LoadBits</c> 是**算术**实现（`Lib.lua:519..548`），负数会算错
    /// ⇒ 值必须为正（buff id 远小于 2^30，安全）。
    ///
    /// <para><b>★ 错误约定：<c>nError &gt; 0</c>（数值比较）</b></para>
    /// <c>UI_NewNormalTowerFirstBuff.lua:170</c> <c>if tbParam and tbParam.nError and tbParam.nError &gt; 0 then</c>
    /// ⇒ 成功可以不带、带 0 也安全（与 LuaCall 数字族的真值判断不同）；本实现成功回空对象。
    /// ★ 与同域 330 的 <c>bStart</c> 布尔开关式**不是同一种** —— 一个域两种失败形状。
    ///
    /// <para><b>★ 成功后客户端自己跳界面</b></para>
    /// <c>:179</c> <c>UI.PopAndOpen("UI_NewNormalTowerFormation2", …)</c>
    /// ⇒ 服务端只需把 step 推进到 <see cref="MercenaryTable.StepFormation"/> 并回包。
    /// **不回包 = 卡在选 buff 界面**（那句 PopAndOpen 永远不执行）。
    /// </summary>
    internal Task OnSelectBuff(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        var buffs = ReadIdArray(body?["buff"]);
        var debuffs = ReadIdArray(body?["debuff"]);

        // ★ 服务端独立复核选择数量（客户端上限 3，见 SelectBuffLimit）
        if (buffs.Count > SelectBuffLimit || debuffs.Count > SelectBuffLimit)
        {
            Log.Warn($"[session {session.Id}] Mercenary_SelectBuff(321) 选择过多："
                   + $"buff {buffs.Count} / debuff {debuffs.Count}（上限 {SelectBuffLimit}），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        // ★ 只在「等待选 buff」这一步接受（step 机由 330 驱动）
        int step = Get(player, MercenaryTable.TaskStep);
        if (step != MercenaryTable.StepSelectBuff && step != MercenaryTable.StepSettlement)
        {
            Log.Warn($"[session {session.Id}] Mercenary_SelectBuff(321) 当前 step={step}"
                   + $"（{StepName(step)}），不是等待选 buff，拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        var changed = new List<KeyValuePair<uint, int>>();

        // 先清空 20 个槽（本轮重新选）
        for (int i = 1; i <= BuffSlots; i++)
        {
            WriteSlot(player, changed, TaskBuffIdsBase + i, 0);
            WriteSlot(player, changed, TaskDebuffIdsBase + i, 0);
        }

        // buff：flag = 0（可用）⇒ 值 = id << 1
        for (int i = 0; i < buffs.Count && i < BuffSlots; i++)
            WriteSlot(player, changed, TaskBuffIdsBase + i + 1, buffs[i] << 1);

        // debuff：flag = 1（已激活）⇒ 值 = (id << 1) | 1
        for (int i = 0; i < debuffs.Count && i < BuffSlots; i++)
            WriteSlot(player, changed, TaskDebuffIdsBase + i + 1, (debuffs[i] << 1) | 1);

        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepFormation);
        changed.Add(new KeyValuePair<uint, int>(
            Player.ComposeTaskId(MercenaryTable.Group, MercenaryTable.TaskStep),
            MercenaryTable.StepFormation));

        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_SelectBuff(321) buff=[{string.Join(',', buffs)}]"
               + $" debuff=[{string.Join(',', debuffs)}] → step {MercenaryTable.StepFormation}(等待编队)");

        // ★ 先推任务值，再回包
        GameNotify.PushTaskValues(session, changed);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 322 选择预选编队（确认编队、准备进关）。
    ///
    /// 上行只有 <c>{nWeekId}</c> —— **编队本体不在这条命令里**：
    /// 客户端先 <c>self:ApplySave(...)</c>（走常规编队保存 LuaCall 21/22/23），
    /// 保存成功的回调里才发 322（<c>UI_NewNormalTowerFormation2.lua:1063/1070</c>）。
    /// ⇒ 服务端这里**只推进 step**，不处理编队数据。
    ///
    /// ★ 成功后客户端 <c>:1081</c> <c>UI.PopAndOpen("UI_NewNormalTowerLevelInfo", …)</c>
    /// ⇒ **不回包 = 卡在编队界面**。
    /// ★ 错误约定同 321（<c>nError &gt; 0</c> 数值比较）。
    /// ★★ 注意 <c>:1073..1082</c> 的结构：**只有 `ActivityEnd` 那一种错误码会 return**，
    /// 其它错误码会**继续往下跳界面** ⇒ 用通用码拒绝时客户端仍会进关卡列表页。
    /// 因此本实现对「step 不对」采取宽容策略：只记日志、仍然放行推进，
    /// 避免玩家卡在一个「拒绝了却又跳过去」的不一致状态。
    /// </summary>
    internal Task OnSelectFormation(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int step = Get(player, MercenaryTable.TaskStep);
        if (step != MercenaryTable.StepFormation)
        {
            // ★ 宽容：见方法注释末段（客户端对非 ActivityEnd 的错误码不会 return）
            Log.Warn($"[session {session.Id}] Mercenary_SelectFormation(322) 当前 step={step}"
                   + $"（{StepName(step)}），不是等待编队 —— 仍按客户端行为推进");
        }

        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_SelectFormation(322) step {step}"
               + $" → {MercenaryTable.StepFighting}(闯关)");

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(MercenaryTable.Group, MercenaryTable.TaskStep),
                MercenaryTable.StepFighting),
        });
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 323 进关 / 324 通关 / 325 失败 / 326 结算

    /// <summary>
    /// 323 请求进入关卡。上行 <c>{nWeekId, nLevelId, nFormationId}</c>
    /// （<c>MercenaryLogic.lua:569</c>）。
    ///
    /// <para><b>★★ 回包形状（<c>MercenaryLogic.lua:572..601</c>）</b></para>
    /// <code>
    /// function MercenaryLogic:OnEnterTower(tbParam)
    ///     if tbParam.nError and tbParam.nError ~= 0 then 弹提示…
    ///     else
    ///         local weekId, levelId = tbParam.tbData.nWeekId, tbParam.tbData.nLevelId   -- ★ 必须有
    ///         self.tbCurLevelConfig = self.tbAllLevelConfig[levelId]                    -- ★ 必须能查到
    ///         if self.tbCurLevelConfig.LevelType == 2 then 播剧情 → 直接 PassTower(true)
    ///         else
    ///             LevelManager:SetDropInfo(tbParam.tbDrop, tbParam.tbDropItems, nMonsterCount)
    ///             Map.LoadById(self.tbCurLevelConfig.MapId)
    ///         end
    /// </code>
    /// ⇒ **必须回 <c>tbData = {nWeekId, nLevelId}</c>**，且 <c>levelId</c> 要能在客户端的
    /// <c>tbAllLevelConfig</c> 里查到（否则下一句 <c>.LevelType</c> 就抛）。
    ///
    /// <para><b>★ <c>tbDrop</c>/<c>tbDropItems</c> 不必下发</b></para>
    /// <c>LevelManager:SetDropInfo</c>（<c>LevelManager.lua:540..556</c>）对 nil 完全安全
    /// （<c>if tbDrop then</c> / <c>tbOthers or {}</c>），怪物掉落由客户端配置自算 ——
    /// 与主线关卡同一口径（见 <c>ChapterHandler</c> 里那条注释）。
    ///
    /// <para><b>★ 错误约定：<c>nError ~= 0</c>（真值 + 非 0 双判）</b></para>
    /// <c>:573</c> <c>if tbParam.nError and tbParam.nError ~= 0 then</c>
    /// ⇒ 成功可以不带、带 0 也安全。本实现成功不带。
    /// </summary>
    internal Task OnEnterTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        int weekId = ReadInt(body?["nWeekId"]);
        int levelId = ReadInt(body?["nLevelId"]);

        var level = _table.LevelOf(levelId);
        if (level is null)
        {
            // ★ 回 nError 而不是静默：客户端下一句就取 .LevelType，nil 会抛
            Log.Warn($"[session {session.Id}] Mercenary_EnterTower(323) levelId={levelId} "
                   + $"不在 MercenaryLevel.txt 里（现网 {_table.LevelCount} 关），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        // 记住「当前在打哪一关」，供 324/325 使用
        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_EnterTower(323) 周 {weekId} 关卡 {levelId}"
               + $"（type={level.LevelType}{(level.IsStory ? " 剧情关" : "")} map={level.MapId}）");

        // ★ tbData 必填；tbDrop/tbDropItems 故意不发（见方法注释）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["tbData"] = new JsonObject
            {
                ["nWeekId"] = weekId,
                ["nLevelId"] = levelId,
            },
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 324 关卡通关。上行含击杀信息与 buff 状态
    /// （<c>MercenaryLogic.lua:602..620</c>：<c>nWeekId, nLevelId, nFormationId,
    /// tbKillMonster, nUseTime, tbBS, tbStartBS, tbActiveBuff, cVal</c>）。
    ///
    /// <para><b>★★ 回包形状（<c>MercenaryLogic.lua:622..666</c>）</b></para>
    /// <code>
    /// if tbParam.nError and tbParam.nError ~= 0 then … 退回主城
    /// else
    ///     if self.tbCurLevelConfig.LevelType == 2 then          -- 剧情关
    ///         if #tbParam.tbData &gt; 0 then UI.Open("UI_RewardNormal", tbParam.tbData) end
    ///         return
    ///     end
    ///     map:GameEnd(true, tbParam.tbData, tbParam.nMasterExp or 0, tbParam.nCardExp or 0, …)
    /// </code>
    /// ⇒ <c>tbData</c> 是**奖励数组**（<c>#tbParam.tbData</c> 取长度 ⇒ **必须是数组**，
    /// 回对象会让剧情关那条 <c>#</c> 取到 0、奖励弹窗不出现）；
    /// <c>nMasterExp</c>/<c>nCardExp</c> 有 <c>or 0</c> 兜底、可省。
    ///
    /// <para><b>★ 通关次数记两处任务值</b></para>
    /// <c>Task_LevelPass</c>(130+idx，每轮清空) 与 <c>Task_WeekLevelPass</c>(20+idx，不清空)。
    /// ★ 本实现用 <b>关卡 id</b> 当 idx（原厂用「关卡在本周清单里的序号」，
    /// 而那个清单来自 <c>MercenaryTower.LevelIds</c>；用 id 会落在同一区间内、
    /// 语义等价于「这一关打过几次」，且不依赖本周清单 —— 更稳）。
    /// </summary>
    internal Task OnPassTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        int levelId = ReadInt(body?["nLevelId"]);

        var level = _table.LevelOf(levelId);
        if (level is null)
        {
            Log.Warn($"[session {session.Id}] Mercenary_PassTower(324) levelId={levelId} 查不到配置，拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        // ---- 通关次数（两处，见方法注释）----
        int slot = levelId % LevelPassSlots + 1;      // 夹进 1..40，避免偏移越界串到别的键
        int passBefore = Get(player, TaskLevelPassBase + slot);
        var changed = new List<KeyValuePair<uint, int>>();
        WriteSlot(player, changed, TaskLevelPassBase + slot, passBefore + 1);
        WriteSlot(player, changed, TaskWeekLevelPassBase + slot,
                  Get(player, TaskWeekLevelPassBase + slot) + 1);

        // ---- 发奖：首通 + 固定 + 随机 ----
        var granted = new RewardGrantResult();
        bool firstClear = passBefore == 0;
        if (firstClear && level.FirstAward.Count > 0)
            _ctx.Rewards.Grant(player, level.FirstAward, RewardSource.Extra, granted);
        if (level.FixAward.Count > 0)
            _ctx.Rewards.Grant(player, level.FixAward, RewardSource.Extra, granted);
        if (level.RandomAward.Count > 0)
            _ctx.Rewards.Grant(player, level.RandomAward, RewardSource.Extra, granted);

        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_PassTower(324) 关卡 {levelId}"
               + $"（{(level.IsStory ? "剧情关" : $"type={level.LevelType}")}）"
               + $"{(firstClear ? " 首通" : "")}：通关次数 {passBefore} → {passBefore + 1}、"
               + $"发奖 {granted.Rewards.Count} 项");

        // ★ 先推数据（背包/货币/任务值），再回业务包
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, changed);

        // ★★ tbData 必须是**数组**（客户端 `#tbParam.tbData`）
        var awards = new JsonArray();
        foreach (var r in granted.Rewards)
        {
            var one = new JsonArray();
            foreach (int v in r.ToTuple())
                one.Add(v);
            awards.Add(one);
        }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbData"] = awards });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 325 关卡失败。
    ///
    /// ★★★ **不回包**：客户端 <c>MercenaryLogic:OnFailTower()</c>
    /// （<c>MercenaryLogic.lua:683..685</c>）**函数体是空的**，
    /// 且失败后的界面切换由客户端自己 <c>map:GameEnd(false, …)</c> 完成（<c>:678</c>）
    /// ⇒ 回包没人处理。本实现只记录状态。
    /// </summary>
    internal Task OnFailTower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // 失败后仍停在「闯关」步，玩家可以重试或结算
        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_FailTower(325) 关卡失败"
               + $"（step 保持 {MercenaryTable.StepFighting} 闯关；★ 按原厂不回包）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 326 本轮结算（主动结束这一轮，领走累积收益）。上行**无参**
    /// （<c>UI_NewNormalTowerLevelInfo.lua:132</c> <c>CallServer(LuaCall.Mercenary_Settlement)</c>）。
    ///
    /// <para><b>★★ 回包判据是「step 是否已变成 4」，不是回包内容</b></para>
    /// <code>
    /// MercenaryLogic.lua:688..699  function MercenaryLogic:OnSettlement(tbParam)
    ///     if tbParam and tbParam.nError and tbParam.nError &gt; 0 then
    ///     else
    ///         if self:GetStep() == 4 then                       -- ★ 读**任务值**
    ///             UI.PopUntil("UI_NewNormalTower")
    ///             UI.OpenAsChild("UI_NewNormalTowerSum")
    ///             return true
    /// </code>
    /// ⇒ **必须先把 step 推成 <see cref="MercenaryTable.StepSettlement"/>(4) 并推 pf 1026，
    /// 再回包** —— 否则客户端读到旧 step、结算界面不打开（与 §22.2 同型）。
    /// ★ 错误约定：<c>nError &gt; 0</c>（数值比较），成功不带。
    /// </summary>
    internal Task OnSettlement(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int step = Get(player, MercenaryTable.TaskStep);
        if (step == MercenaryTable.StepStart)
        {
            Log.Warn($"[session {session.Id}] Mercenary_Settlement(326) 当前 step=0（未开局），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepSettlement);

        // 本轮的关卡通关次数清零（Task_LevelPass 注释写明「每轮清空」）
        var changed = new List<KeyValuePair<uint, int>>
        {
            new(Player.ComposeTaskId(MercenaryTable.Group, MercenaryTable.TaskStep),
                MercenaryTable.StepSettlement),
        };
        for (int i = 1; i <= LevelPassSlots; i++)
            WriteSlot(player, changed, TaskLevelPassBase + i, 0);

        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_Settlement(326) step {step} → "
               + $"{MercenaryTable.StepSettlement}(结算)，本轮关卡通关次数已清零");

        // ★★ 先推 step，再回包（客户端 OnSettlement 读任务值判断）
        GameNotify.PushTaskValues(session, changed);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>写一个 buff 槽并登记到增量推送列表。</summary>
    private static void WriteSlot(Player player, List<KeyValuePair<uint, int>> changed,
                                 int offset, int value)
    {
        if (Get(player, offset) == value)
            return;                     // 没变就不推（pf 1026 是增量语义）
        Set(player, offset, value);
        changed.Add(new KeyValuePair<uint, int>(
            Player.ComposeTaskId(MercenaryTable.Group, offset), value));
    }

    // ================================================================ 327/328/331/332/333（第十三轮）

    /// <summary>
    /// 327 删除 Debuff。★ 上行是**裸数组**（debuff id 列表）——
    /// <c>UI_NewNormalTowerSelectBuff.lua:255</c> <c>CallServer(…, tbDebuff)</c>，
    /// 不是 <c>{debuff = […]}</c>。
    ///
    /// 客户端两侧调同一个 <c>OnDeleteDebuff</c>（<c>MercenaryLogic.lua:703..715</c>），
    /// 成功分支只刷两个界面、**不读任何字段** ⇒ 服务端把对应槽位清零 + 回包即可。
    /// ★ 错误约定 <c>nError &gt; 0</c>（数值比较）。
    /// </summary>
    internal Task OnDeleteDebuff(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ 裸数组；也容忍 {debuff:[…]}（防御性）
        var ids = param is JsonArray ? ReadIdArray(param)
                                     : ReadIdArray((param as JsonObject)?["debuff"]);
        if (ids.Count == 0)
        {
            Log.Warn($"[session {session.Id}] Mercenary_DeleteDebuff(327) 没解析出 debuff id"
                   + $"（上行 {param?.ToJsonString()}），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        var changed = new List<KeyValuePair<uint, int>>();
        int removed = 0;
        foreach (int id in ids)
        {
            // 在 91..110 里找「bit1..31 == 这个 id」的槽，清零
            for (int i = 1; i <= BuffSlots; i++)
            {
                int off = TaskDebuffIdsBase + i;
                int v = Get(player, off);
                if (v > 0 && (v >> 1) == id)
                {
                    WriteSlot(player, changed, off, 0);
                    removed++;
                    break;
                }
            }
        }

        _ctx.Store.Save(player);
        Log.Info($"[session {session.Id}] Mercenary_DeleteDebuff(327) 请求删 "
               + $"[{string.Join(',', ids)}]，实际清掉 {removed} 个槽");

        GameNotify.PushTaskValues(session, changed);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 328 选择 fate（关卡上的命运/事件选项）。
    /// 上行 <c>{LevelId, FataId, Index, WeekId}</c>
    /// （<c>UI_NewNormalTowerLevelInfo.lua:503..509</c> —— ★ 原厂拼写就是 <c>FataId</c>）。
    ///
    /// 客户端 <c>OnSelectFate</c>（<c>MercenaryLogic.lua:716..725</c>）成功分支只调
    /// <c>OnSelectFateCallback</c>、**不读任何字段** ⇒ 服务端存下选择 + 回包即可。
    /// ★ 存储位置原厂未取证，本服用 <see cref="TaskFateBase"/>（见那里的说明）。
    /// </summary>
    internal Task OnSelectFate(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        int levelId = ReadInt(body?["LevelId"]);
        int index = ReadInt(body?["Index"]);
        // ★ 原厂拼写 FataId（不是 FateId）
        int fateId = ReadInt(body?["FataId"]);

        if (levelId <= 0 || index <= 0)
        {
            Log.Warn($"[session {session.Id}] Mercenary_SelectFate(328) 参数非法"
                   + $"（LevelId={levelId} Index={index}），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        int slot = levelId % LevelPassSlots + 1;
        var changed = new List<KeyValuePair<uint, int>>();
        WriteSlot(player, changed, TaskFateBase + slot, index);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_SelectFate(328) 关卡 {levelId}"
               + $"（fate {fateId}）选了第 {index} 项");

        GameNotify.PushTaskValues(session, changed);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 331 请求回溯节点数据。上行 <c>{nNodeId}</c>（<c>UI_NewNormalTowerLevelInfo.lua:360</c>）。
    ///
    /// <para><b>★★ 回包只要「非 nil」就够</b></para>
    /// <code>
    /// MercenaryLogic.lua:727..756  function MercenaryLogic:OnRecallLevelData(tbParam)
    ///     if tbParam == nil then return Game.MessageBox.Simple(UIText("UI_Activities.Tips2")) end
    ///     UI.OpenAsChild("UI_NewNormalTowerHistory", tbParam)
    ///     -- tbParam.blood = {} …                      ← ★ 后面所有字段填充都被**注释掉了**
    /// </code>
    /// ⇒ 原厂自己把 blood/shield/score/buff/debuff 的填充注释了 ⇒ 回一个带 <c>nNodeId</c>
    /// 的对象即可开界面。**回 nil 会弹「无效」提示**。
    /// </summary>
    internal Task OnRecallLevelData(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int nodeId = ReadInt((param as JsonObject)?["nNodeId"]);
        int slot = nodeId % LevelPassSlots + 1;
        int saved = Get(player, TaskRecallDataBase + slot);

        Log.Info($"[session {session.Id}] Mercenary_RecallLevelData(331) 节点 {nodeId}"
               + $"（存档值 {saved}）→ 回历史界面数据");

        // ★ 必须非 nil；字段填充原厂已注释，给 nNodeId + 空容器最安全
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["nNodeId"] = nodeId,
            ["score"] = Get(player, MercenaryTable.TaskWeekMoney),
            ["blood"] = new JsonArray(),
            ["shield"] = new JsonArray(),
            ["buff"] = new JsonArray(),
            ["debuff"] = new JsonArray(),
        });
        return Task.CompletedTask;
    }

    /// <summary>
    /// 332 回溯到之前节点。上行 <c>{nNodeId}</c>（<c>UI_NewNormalTowerHistory.lua:194</c>）。
    ///
    /// ★★ 失败码有语义（<c>MercenaryLogic.lua:758..768</c>）：
    /// <c>1</c> = 周次数不足、<c>2</c> = Tips1、其它 = Tips2 ⇒ 拒绝时用 <b>1</b> 最贴合
    /// （「次数不足」是唯一可能的业务拒绝原因）。
    /// 成功分支只调 <c>OnRecallCallback</c>、不读字段。
    /// </summary>
    internal Task OnRecall(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        int nodeId = ReadInt((param as JsonObject)?["nNodeId"]);

        // 回溯要消耗本周次数（与 330 开局同一个计数）
        int left = Get(player, MercenaryTable.TaskWeekCount);
        if (left <= 0)
        {
            Log.Warn($"[session {session.Id}] Mercenary_Recall(332) 本周次数已用尽，拒绝（nError=1）");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = 1 });
            return Task.CompletedTask;
        }

        Set(player, MercenaryTable.TaskWeekCount, left - 1);
        // 回到「闯关」步（客户端随后刷关卡界面）
        Set(player, MercenaryTable.TaskStep, MercenaryTable.StepFighting);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_Recall(332) 回溯到节点 {nodeId}，"
               + $"本周次数 {left} → {left - 1}");

        PushState(session, player);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 333 跳过关卡（用积分换直接通关）。上行 <c>{nLevelId, nWeekId}</c>
    /// （<c>UI_NewNormalTowerLevelInfo.lua:375</c>）。
    ///
    /// <para><b>★ 准入判据（<c>MercenaryLogic:CheckSkipLevel</c>，<c>:775..788</c>）</b></para>
    /// <code>
    /// local maxScore = math.max(上周积分, 本周积分)
    /// local maxIdx = maxScore // 100
    /// for idx, nLevel in ipairs(curTowerConfig.tbLevelIds) do
    ///     if nLevelId == nLevel then return idx &lt;= maxIdx end
    /// </code>
    /// ⇒ 服务端按同一公式复核（<see cref="SkipScorePerLevel"/>）。
    /// ★ 本服用「关卡 id 在表里的相对位置」近似 <c>idx</c> ——
    /// 因为 <c>tbLevelIds</c> 来自 <c>MercenaryTower.LevelIds</c>（每周不同），
    /// 而服务端已按公式反解当前周，取那一周的清单即可。
    ///
    /// <para><b>回包 <c>{tbAward}</c></b></para>
    /// <c>MercenaryLogic.lua:798</c> <c>if tbParam.tbAward and next(tbParam.tbAward) then</c>
    /// ⇒ 用 <c>next()</c> 判空 ⇒ **空数组安全**（不会开空奖励弹窗）。
    /// </summary>
    internal Task OnSkipLevel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        int levelId = ReadInt(body?["nLevelId"]);

        var level = _table.LevelOf(levelId);
        if (level is null)
        {
            Log.Warn($"[session {session.Id}] Mercenary_SkipLevel(333) levelId={levelId} 查不到配置，拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        // ★ 按客户端同一公式复核积分门槛
        int lastWeek = Get(player, MercenaryTable.TaskCacheLastWeekMoney);
        int thisWeek = Get(player, MercenaryTable.TaskWeekMoney);
        int maxIdx = Math.Max(lastWeek, thisWeek) / SkipScorePerLevel;
        int idx = _table.LevelIndexInWeek(levelId);

        if (idx <= 0 || idx > maxIdx)
        {
            Log.Warn($"[session {session.Id}] Mercenary_SkipLevel(333) 关卡 {levelId} 序号 {idx}"
                   + $" 超过可跳上限 {maxIdx}（积分 上周 {lastWeek} / 本周 {thisWeek}），拒绝");
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            return Task.CompletedTask;
        }

        // 跳关等同通关：记次数 + 发固定奖励（★ 不给首通奖励 —— 跳过去的不算首通体验）
        int slot = levelId % LevelPassSlots + 1;
        var changed = new List<KeyValuePair<uint, int>>();
        WriteSlot(player, changed, TaskLevelPassBase + slot,
                  Get(player, TaskLevelPassBase + slot) + 1);
        WriteSlot(player, changed, TaskWeekLevelPassBase + slot,
                  Get(player, TaskWeekLevelPassBase + slot) + 1);

        var granted = new RewardGrantResult();
        if (level.FixAward.Count > 0)
            _ctx.Rewards.Grant(player, level.FixAward, RewardSource.Extra, granted);

        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Mercenary_SkipLevel(333) 跳过关卡 {levelId}"
               + $"（序号 {idx} ≤ 上限 {maxIdx}），发奖 {granted.Rewards.Count} 项");

        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, changed);

        // ★ tbAward 用 next() 判空 ⇒ 空数组安全
        var award = new JsonArray();
        foreach (var r in granted.Rewards)
        {
            var one = new JsonArray();
            foreach (int v in r.ToTuple())
                one.Add(v);
            award.Add(one);
        }
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["tbAward"] = award });
        return Task.CompletedTask;
    }

    /// <summary>读一个整数字段；非 <c>JsonValue</c> 一律回退（不抛）。</summary>
    private static int ReadInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out int i)) return i;
        if (v.TryGetValue<double>(out double d)) return (int)d;
        if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int p)) return p;
        return fallback;
    }

    /// <summary>
    /// 读「可重复 id 数组」（<c>buff</c> / <c>debuff</c>）。
    /// ★ 非数组一律当空（本仓通用坑：<c>JsonNode["key"]</c> 对 <c>JsonArray</c> 会抛，
    /// 而这里反过来 —— 字段值本身可能不是数组）。
    /// </summary>
    internal static List<int> ReadIdArray(JsonNode? node)
    {
        var result = new List<int>();
        if (node is not JsonArray arr)
            return result;
        foreach (var it in arr)
        {
            if (it is not JsonValue v)
                continue;
            int id = 0;
            if (v.TryGetValue<int>(out int i)) id = i;
            else if (v.TryGetValue<double>(out double d)) id = (int)d;
            else if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int p)) id = p;
            if (id > 0)
                result.Add(id);
        }
        return result;
    }

    /// <summary>把塔界面要读的几个任务值推给客户端（pf 1026）。</summary>
    private static void PushState(ClientSession session, Player player)
    {
        var kv = new List<KeyValuePair<uint, int>>();
        foreach (int off in new[]
        {
            MercenaryTable.TaskCurWeekId, MercenaryTable.TaskCacheLastWeekMoney,
            MercenaryTable.TaskCanGetMoney, MercenaryTable.TaskWeekMoney,
            MercenaryTable.TaskStep, MercenaryTable.TaskWeekCount,
        })
        {
            kv.Add(new KeyValuePair<uint, int>(
                Player.ComposeTaskId(MercenaryTable.Group, off), Get(player, off)));
        }
        GameNotify.PushTaskValues(session, kv);
    }

    /// <summary>
    /// 330 的失败回包：<c>bStart = false</c>。
    /// ★ **不是** `nError` —— 客户端 `MercenaryLogic.lua:550` 判的是 <c>tbParam.bStart</c>，
    /// else 分支直接弹「次数不足」提示。
    /// </summary>
    private void Fail(ClientSession session, int cmd, string reason)
    {
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["bStart"] = false });
        Log.Warn($"[session {session.Id}] LuaCall {cmd} 佣兵开始被拒：{reason}");
    }

    internal static string StepName(int step) => step switch
    {
        MercenaryTable.StepStart => "开始",
        MercenaryTable.StepSelectBuff => "等待选buff",
        MercenaryTable.StepFormation => "等待编队",
        MercenaryTable.StepFighting => "闯关",
        MercenaryTable.StepSettlement => "结算",
        _ => $"?{step}",
    };
}
