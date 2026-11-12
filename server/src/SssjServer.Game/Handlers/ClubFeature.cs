using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 社团域（<c>LuaCall.Club_*</c>）。本轮实现 **15 条**数字命令，全部走
/// pf 1022/1024 + <c>sFunction="LuaCall"</c> —— 社团**没有任何专用 pf / proto 消息 / sFunction**
/// （取证：`docs/pf-table.txt` 全表零命中、`docs/proto-schema.json` 消息名零命中、
/// <c>grep -i club extracted/lua/S2CCall.lua</c> exit 1）。
///
/// 完整取证在 `work/club-domain-forensics.md`（1603 行），实现说明与实测差异在
/// `work/club-domain-impl.md`。本注释只记「写代码时必须知道的」。
///
/// <para><b>① 覆盖关系：10002 覆盖 <c>GameModule</c> 的占位实现</b></para>
/// <c>GameModule.cs:120..127</c>（构造期）已经用 <c>RegisterLuaCall(10002, …)</c> 注册了一个
/// 「恒回空对象」的占位；本类用 <see cref="GameFeatureContext.MapLuaCall"/> 再注册一次，
/// 因为 features 的反射装配发生在 <c>GameModule.cs:171</c>、**晚于**那些 <c>RegisterLuaCall</c>
/// ⇒ 后注册胜出，**不需要改 <c>GameModule.cs</c>**。
/// 回归护栏见 <c>ClubFeatureTests.ReqClubInfo_覆盖GameModule的10002占位</c>。
/// ★★ 注册必须用 <c>ctx.MapLuaCall</c>：<c>GameModule.cs:432</c> 的分发门禁是
/// <c>_handledLuaCall.Contains(cmd)</c>，只有 <c>MapLuaCall</c> 会登记
/// （<c>GameFeature.cs:106</c> → <c>GameModule.cs:473</c>）；用 <c>ctx.LuaCalls.Map</c>
/// 注册的 handler **永远不会被调用**，只打 unhandled。
///
/// <para><b>② ★★ 第六种错误约定「族 B：非空表即成功」</b></para>
/// 社团域内部**混用 6 种判据**，绝不能一刀切。最危险的是族 B（判据
/// <c>if next(tbParam) then</c>，完全不看 <c>nError</c>）：
/// <c>LuaCall.lua:2273/2288/2306/2321/2335/2349/2379/2395/2409</c> 共 9 个命令。
/// 对它们回 <c>{nError=n}</c> 会因为「表非空」被判成 **成功**，进而
/// <c>ClubCommon:OnServer_MyClubInfo({nError=n})</c> →
/// <c>ClubCommon.lua:454..459</c> <c>not club.primary</c> → <c>MyClubInfo={}</c>、<b><c>me.clubid = 0</c></b>
/// → <c>UI_ClubMember.lua:645..649</c> <c>uiPlayer:Home()</c> **把玩家退回主城**。
/// 症状极像「存档坏了」。
/// ⇒ <b>本实现对所有失败一律回空表 <c>{}</c>（或不回包），错误文案改走
/// <c>sFunction="CallClientTip"</c></b>（见 <see cref="Tip"/>）。这个策略在 6 个族里都安全：
/// 族 A/C/D/E 的 <c>nError</c> 缺失一律判成功、族 B/F 的空表一律静默。
///
/// <para><b>③ ★★★ <see cref="Player.ClubId"/> 与社团对象必须原子写</b></para>
/// <c>GameModule.cs:590</c> 已无条件把 <c>ClubId</c> 塞进 pf 1005 <c>PlayerNtf.clubid</c>。
/// 一旦 <c>ClubId != 0</c> 而 10002 回不出完整社团对象：<c>UI_ClubReward.lua:35</c> 的
/// <c>if me.clubid ~= 0 and next(ClubCommon.MyClubInfo)</c> → <c>next(nil)</c> 抛；
/// <c>DropWayRegister.lua:596</c> 的 <c>me.clubid &gt; 0</c> 分支还会再抛一次。
/// ⇒ 两者的写入被强制收进 <see cref="ClubBag.Write"/> 一处（它同时写
/// <c>Extra["club"]</c> 与 <c>player.ClubId</c>），并在
/// <see cref="EnsureClubIdInvariant"/>（<c>OnPlayerPrepare</c> 钩子，pf 1005 **之前**）
/// 做一次断言性自愈 + WARN。
///
/// <para><b>④ ★★ 三种「指数」道具不能直接 <c>Grant</c></b></para>
/// 服务端只映射了 <c>(15,14,1,1) → 货币 11</c>（<c>Config/ItemBaseTable.cs:202</c>）；
/// 另外三种同族道具**没有映射**，直接 <c>Grant</c> 会被当普通堆叠道具塞进背包
/// （genre 15 ≥ 5 走 <c>RewardService.cs:359..368</c>）：
/// <c>15-13</c> 团队指数 = 社团经验、<c>15-12</c> 活跃指数 = 社团活跃点、
/// <c>15-36</c> 出击次数 = 任务值 <c>(1,552)</c>。
/// ⇒ 发奖前必须先过 <see cref="SplitIndexAwards"/>。
///
/// <para><b>⑤ <c>tbParam</c> 有 4 种顶层 JSON 类型，<c>param["key"]</c> 只对第 1 种安全</b></para>
/// <c>JsonObject</c>（多数）/ <c>JsonArray</c>（10016、10019 全领）/ <c>JsonValue(bool)</c>（10025）/
/// <b><c>null</c></b>（10002/10003/10012/10015/10018/10020/10023/10028/10029）。
/// 真机实测 10003 的上行连 <c>tbParam</c> 键都没有（`work/probe-three-domains.md` §2），
/// 而 <c>JsonNode["key"]</c> 对 <c>JsonArray</c> **会抛**（AGENTS.md 记录过的真机崩溃）
/// ⇒ 每个 handler 读参数前必须 <c>is JsonObject</c>，见 <see cref="AsObject"/>。
///
/// <para><b>⑥ 遮罩：社团 30 个数字命令一条都不会卡遮罩</b></para>
/// <c>LuaCall:CallServer</c> 只在 <c>tonumber(blockTime)</c> 为真时 <c>Blocking.Show</c>
/// （<c>LuaCall.lua:505..508</c>），而社团里传第三参的三处传的都是 <b><c>true</c></b>
/// （<c>UI_Search.lua:75</c>/<c>:268</c>/<c>:353</c>），<c>tonumber(true) == nil</c>。
/// ⇒ 「不回包」是安全的（只是按钮点了没反应），这也是 10026 在「没有社团」时
/// **刻意不回包**的前提（<c>ClubCommon.lua:436</c> 无保护读 <c>primary.send_mail_times</c>）。
///
/// <para><b>⑦ boss 子系统整域不实现（有决定性论据）</b></para>
/// <c>ClubBossTime.txt</c> 9 期**全部过期**（末期 <c>ClearTime = 2021-09-27-0400</c>）
/// ⇒ <c>ClubCommon:GetTimeConfig()</c>（<c>ClubCommon.lua:528..536</c>）恒 nil，而
/// <c>UI_ClubBossReward.lua:129/177</c> 与 <c>UI_ClubBossMap.lua:338</c> 三处**无 nil 保护**
/// 地取它的字段 ⇒ 即使硬造数据也必炸。
/// 同时两个入口（<c>UI_Club.lua:381</c>、<c>DropWay.lua:331</c>）都**先判 <c>IsClubBossShow()</c>**
/// 才发命令 ⇒ 10015 根本发不出来。
/// ⇒ 本实现**刻意省略回包里的 <c>clubboss</c> 字段**，让 <c>ClubCommon.lua:462</c> 的
/// <c>club.clubboss and club.clubboss.stageid or 0</c> 得到 0 ⇒ 不加载任何 boss 配置。
/// </summary>
public sealed class ClubFeature : IGameFeature
{
    // =====================================================================
    //  LuaCall 命令 id
    //  ★ 放在本类里而不是 LuaCallIds ——「各功能域自己独占的 id 请放在对应 Feature 类里」
    //    （LuaCallIds.cs:9 的约定）。id 定义取证 extracted/lua/LuaCall.lua:455..487。
    // =====================================================================

    /// <summary>10001 <c>Club_CreateClub</c>「创建一个社团」（<c>LuaCall.lua:455</c>）。族 A。</summary>
    public const int CmdCreateClub = 10001;

    /// <summary>10002 <c>Club_ReqClubInfo</c>「请求自己社团数据」。族 A，**无入参**。</summary>
    public const int CmdReqClubInfo = 10002;

    /// <summary>10003 <c>Club_FXClubList</c>「请求社团发现列表」。族 F，上行 <c>param == null</c>。</summary>
    public const int CmdFxClubList = 10003;

    /// <summary>10004 <c>Club_FindAClub</c>「按 id 搜索一个社团」。★ 族 B。</summary>
    public const int CmdFindAClub = 10004;

    /// <summary>10005 <c>Club_ApplyClubList</c>「申请加入社团」（单个 / 一键同形）。★ 族 B。</summary>
    public const int CmdApplyClubList = 10005;

    /// <summary>10008 <c>Club_ModifyNotice</c>「修改公告」。★ 族 B，回包是**裸数组** <c>["新公告"]</c>。</summary>
    public const int CmdModifyNotice = 10008;

    /// <summary>10012 <c>Club_Quit</c>「主动退团」。族 A，**无入参**。</summary>
    public const int CmdQuit = 10012;

    /// <summary>10014 <c>Club_ModifyApply</c>「修改申请条件」。族 A，回包是**整个 primary**。</summary>
    public const int CmdModifyApply = 10014;

    /// <summary>10020 <c>Club_DailyAward</c>「社团每日福利」。族 F（客户端 else 分支为空），**无入参**。</summary>
    public const int CmdDailyAward = 10020;

    /// <summary>10022 <c>Club_ReqDissolve</c>「申请解散 / 取消解散」。族 A，键名是 typo <c>dissoslve</c>。</summary>
    public const int CmdReqDissolve = 10022;

    /// <summary>10026 <c>Club_SendMail</c>「发送社团邮件」。族 F，回 <c>{nCount}</c>。</summary>
    public const int CmdSendMail = 10026;

    /// <summary>10027 <c>Club_ReqClubMember</c>「请求成员列表」。族 F，★ 必须**回显 <c>strType</c>**。</summary>
    public const int CmdReqClubMember = 10027;

    /// <summary>10028 <c>Club_ReqClubApplylist</c>「请求申请列表」。族 F，**无入参**。</summary>
    public const int CmdReqApplylist = 10028;

    /// <summary>10029 <c>Club_ReqClubLog</c>「请求社团日志」。族 F，**无入参**。</summary>
    public const int CmdReqClubLog = 10029;

    /// <summary>10100 <c>Club_CancelApplyClubList</c>「取消申请」。★ 族 B。</summary>
    public const int CmdCancelApply = 10100;

    // =====================================================================
    //  下行提示通道（社团域的错误只能走这里，见类注释 ②）
    // =====================================================================

    /// <summary>
    /// <c>S2C.CallClientTip</c> → <c>ShowMsgByKey(table.unpack(tbParam))</c>，
    /// 参数是**裸数组** <c>[key, fmtArg1, …]</c>（<c>S2CCall.lua:94..101</c>；
    /// 服务端侧封装取证 <c>LuaCall.lua:577..583</c>）。它顺带 <c>Blocking.Hide()</c>。
    ///
    /// ★ 为什么社团必须用它：<c>ui.Error.*</c> 里**没有任何一条提到「社团」**
    /// （取证文档附录 A 的提取脚本，过滤 <c>'社团' in value</c> 结果为空），
    /// 社团业务文案全在 <c>ui.Club.*</c> 命名空间（166 条），只能通过这条通道发。
    /// </summary>
    public const string FnCallClientTip = ShopFeature.FnCallClientTip;

    // ------- ui.Club.* 文案键（全部来自 lang.u，取证文档 §3.4；本地化表不在 extracted/ 里）-------

    /// <summary>「社团名字已经被使用」。</summary>
    public const string TipNameUsed = "ui.Club.ClubNameUsed";

    /// <summary>「已经拥有社团」。</summary>
    public const string TipAlreadyOwnClub = "ui.Club.AlreadyOwnClub";

    /// <summary>「已经申请过该社团」。</summary>
    public const string TipAlreadyApplied = "ui.Club.AleadyApplyCurClub";

    /// <summary>「等级低于社团申请等级」。</summary>
    public const string TipLessThanApplyLevel = "ui.Club.LessThanApplyLevel";

    /// <summary>「满足社团要求，自动加入社团:%s」（带 1 个格式化参数 = 社团名）。</summary>
    public const string TipAutoJoin = "ui.Club.AutoJoinClub";

    /// <summary>「成功退出社团」。</summary>
    public const string TipQuitSuccess = "ui.Club.QuitSuccess";

    /// <summary>「社长只能解散社团，不可退出社团」。</summary>
    public const string TipHeaderCannotQuit = "ui.Club.HeaderOnlyCanDissolve";

    /// <summary>「24小时内只能申请解散社团一次」。</summary>
    public const string TipDissolveLimit = "ui.Club.DissolveLimit";

    /// <summary>「今日发送邮件次数已用尽」。</summary>
    public const string TipMailLimit = "ui.Club.MeetClubEMailLimit";

    /// <summary>「无社团数据」（通用兜底）。</summary>
    public const string TipNoClubData = "ui.Club.NoClubData";

    /// <summary>「没有权限」（客户端自己也在用：<c>UI_ClubMember.lua:520</c>）。</summary>
    public const string TipNoAuthority = "ui.Club.NoAuthority";

    /// <summary>公告超长（客户端 <c>ClubCommon.lua:404</c> 用的就是这个键）。</summary>
    public const string TipMaxNotice = "ui.Club.MaxNotice";

    /// <summary>社团名长度不合法（客户端 <c>UI_Search.lua:313</c> 用的就是这个键）。</summary>
    public const string TipMaxName = "ui.Club.MaxName";

    /// <summary>「允许申请等级不要超过自动准入等级」（客户端 <c>UI_Search.lua:342</c>）。</summary>
    public const string TipErrorCreateLevel = "ui.Club.ErrorCreateLevel";

    /// <summary>钻石不足（<c>MoneyLogic.lua:157</c> <c>Text('ui.Error.20013')</c> —— 这一条在 <c>ui.Error.*</c> 里）。</summary>
    public const string TipNotEnoughDiamond = "ui.Error.20013";

    // =====================================================================
    //  常量（客户端硬编码值，服务端必须复校 —— GM 通道能绕过全部客户端校验）
    // =====================================================================

    /// <summary>社长（<c>ClubCommon.HEADER = 9</c>，<c>ClubCommon.lua:4</c>）。</summary>
    public const int PosHeader = 9;

    /// <summary>副社长（<c>ClubCommon.VICE = 8</c>，<c>ClubCommon.lua:5</c>）。</summary>
    public const int PosVice = 8;

    /// <summary>普通成员（<c>ClubCommon.ORDINARY = 0</c>，<c>ClubCommon.lua:6</c>）。</summary>
    public const int PosOrdinary = 0;

    // ---- 第十五轮新增：社团管理五条（10006/10007/10009/10010/10011）----

    /// <summary>10006 <c>Club_Agree</c>：同意入团申请（上行 <c>{agreelist=[roleid,…]}</c>）。</summary>
    public const int CmdAgree = 10006;

    /// <summary>10007 <c>Club_Ignore</c>：拒绝入团申请（上行 <c>{ignorelist=[roleid,…]}</c>）。</summary>
    public const int CmdIgnore = 10007;

    /// <summary>10009 <c>Club_ChangePostion</c>：职位变更（上行 <c>{memberid, srcpos, dstpos}</c>）。</summary>
    public const int CmdChangePosition = 10009;

    /// <summary>10010 <c>Club_HeaderChange</c>：社长转让（上行 <c>{viceid}</c>）。</summary>
    public const int CmdHeaderChange = 10010;

    /// <summary>10011 <c>Club_KickMember</c>：踢出成员（上行 <c>{kickroleid}</c>）。</summary>
    public const int CmdKickMember = 10011;

    /// <summary>社团公告长度上限（<c>GetStringLen</c> 口径，<c>ClubCommon.lua:401</c> <c>nLen &gt; 140</c>）。</summary>
    public const int MaxNoticeWidth = 140;

    /// <summary>社团邮件正文上限（<c>ClubCommon.lua:424</c> <c>nLen &gt; 400</c>）。</summary>
    public const int MaxMailWidth = 400;

    /// <summary>
    /// 社团名长度区间（<c>GetStringLen</c> 口径，简中 <c>{4, 12}</c> = 2..6 个汉字）。
    /// 取证 <c>UI_Search.lua:8..14</c> <c>tbNameLimit[Game.LangType.zh_CN] = {4, 12}</c>；
    /// <c>GetStringLen</c> 对 ASCII 记 1、非 ASCII 记 2（<c>Lib.lua:1268..1290</c>）。
    /// </summary>
    public const int MinNameWidth = 4;

    /// <summary>见 <see cref="MinNameWidth"/>。</summary>
    public const int MaxNameWidth = 12;

    /// <summary>申请等级 / 自动准入等级的合法区间下界（<c>UI_Search.lua:326</c> <c>&lt; 1 or &gt; 999</c>）。</summary>
    public const int MinApplyLevel = 1;

    /// <summary>见 <see cref="MinApplyLevel"/>。</summary>
    public const int MaxApplyLevel = 999;

    /// <summary>每日社团邮件上限（<c>ClubCommon.MaxSendClubTimes = 10</c>，<c>ClubCommon.lua:27</c>）。</summary>
    public const int MaxSendMailTimes = 10;

    /// <summary>
    /// 解散社团的等待时长（秒）：<c>ClubCommon.DissolveTimeLimit = 86400*3-1</c>
    /// （<c>ClubCommon.lua:8</c>，客户端倒计时用的就是它）。
    /// ★ **不是** <c>ClubLevels.DismissTime = 7</c>（那列的中文名是「自动换团长时间(天)」）。
    /// </summary>
    public const long DissolveWaitSeconds = 86400L * 3 - 1;

    /// <summary>「24 小时内只能申请解散社团一次」的冷却（<c>ui.Club.DissolveLimit</c> 文案字面值）。</summary>
    public const long DissolveRequestCooldownSeconds = 86400L;

    // ------- 任务值（全部 group 1，取证文档 §7.4 / ClubCommon.lua:7..27）-------

    /// <summary>社团相关任务值所在 group（<c>ClubCommon.nGroupId = 1</c>，<c>ClubCommon.lua:10</c>）。</summary>
    public const int TaskGroup = 1;

    /// <summary>
    /// (1,28)「上次离团时间戳」。★ 客户端 <c>UI_Search.lua:83</c>/<c>:111</c> 直接读它显示再入团倒计时
    /// （<c>ClubCommon.ApplyTimeLimit = 86400</c>）。**由服务端写**。
    /// </summary>
    public const int TaskLastQuitTime = 28;

    /// <summary>
    /// (1,35)「上次领每日福利的 <c>GetLocalDay4AM()</c>」（<c>ClubCommon.DailyAwardKey</c>）。
    /// ★ 客户端点击时**自己也会写**（<c>UI_ClubReward.lua:76</c>）并经 pf 1027 上行，
    /// 服务端这一份是「GM 直接发 10020 时」的兜底 ⇒ 只能取 <c>max</c>，见 <see cref="OnDailyAward"/>。
    /// </summary>
    public const int TaskDailyAwardDay = 35;

    /// <summary>(1,40)「申请解散社团的时间」（<c>ClubCommon.DissolvedTaskKey</c>）。</summary>
    public const int TaskDissolveTime = 40;

    /// <summary>(1,552)「今日额外参战次数」（<c>ClubCommon.TodayExtraGainTimes</c>）—— 道具 <c>15-36</c> 的落点。</summary>
    public const int TaskTodayExtraGainTimes = 552;

    // ------- 三种「指数」道具的四元组（取证文档 §9.1.1）-------

    /// <summary>道具大类 <c>Genre = 15</c>（自动使用类）。</summary>
    public const int IndexGenre = 15;

    /// <summary><c>15-12</c>「活跃指数」<c>ClassName = ClubActivePointAuto</c> → 社团活跃点。</summary>
    public const int IndexDetailActivePoint = 12;

    /// <summary><c>15-13</c>「团队指数」<c>ClassName = ClubExpAuto</c> → 社团经验。</summary>
    public const int IndexDetailClubExp = 13;

    /// <summary><c>15-36</c>「出击次数」<c>ClassName = ItemAddClubBossFightTimeAuto</c> → 任务值 (1,552)。</summary>
    public const int IndexDetailBossTimes = 36;

    // ------- 社团 id 分配（取证文档 §11 第 13 条：原服分配策略未取证，建议自增，避免与 roleid 混淆）-------

    /// <summary>
    /// **玩家自己创建的**社团 id 基址。由 <see cref="Player.Roleid"/> 派生（见
    /// <see cref="OwnClubIdOf"/>），所以同一账号重建社团会拿到同一个 id ——
    /// 这对单机与可复现测试都是优点。
    /// </summary>
    public const ulong OwnClubIdBase = 200001;

    /// <summary>
    /// 发现列表里那些**确定性假社团**的 id 基址。与 <see cref="OwnClubIdBase"/>
    /// 分段隔开（假社团恒在 <c>100001..100800</c>），保证永不撞号。
    /// </summary>
    public const ulong NpcClubIdBase = 100001;

    /// <summary>假社团社长的 roleid 基址（远离本服 <c>World.DeriveRoleId</c> 的 ~5.2e15 值域）。</summary>
    public const ulong NpcRoleIdBase = 900_000_000;

    /// <summary>发现列表里生成几个假社团（列表可视区约 6 行，8 个刚好能滚一屏）。</summary>
    public const int DiscoverClubCount = 8;

    /// <summary>
    /// 假社团的名字池（12 个，2..6 汉字，满足 <see cref="MinNameWidth"/>..<see cref="MaxNameWidth"/>）。
    /// 按账号确定性地取一个起始偏移再顺序取 <see cref="DiscoverClubCount"/> 个 ⇒ 同一账号每次看到同一批、且不重名。
    /// </summary>
    internal static readonly string[] NpcClubNames =
    {
        // English names kept within the 12-character club-name limit, so they fit the same UI as player clubs.
        "Starvault", "Seventh Unit", "Black Front", "Rose Society", "Deep Space", "Dawn Squad",
        "Nightingale", "White Whale", "Quantum Tea", "Lighthouse", "Red Leaf", "Galaxy Post",
    };

    /// <summary>假社团社长的名字池（与 <see cref="NpcClubNames"/> 同样按偏移取）。</summary>
    internal static readonly string[] NpcHeaderNames =
    {
        "Violet", "Old Chen", "Mr. K", "Natsume", "Alpha", "Kirishima",
        "Shirakawa", "No. 13", "Nangong", "Xiaoman", "Helga", "Tsukimi",
    };

    private GameFeatureContext _ctx = null!;
    private ClubTable _table = ClubTable.Empty;
    private ClubBossHandler? _boss;

    public string Name => "社团（15 条基础 + 管理 5 条：10006/10007/10009/10010/10011）";

    public ClubFeature() { }

    /// <summary>单测用：直接注入配置表，跳过 <see cref="Install"/>（同 <c>HouseFeature</c> 的做法）。</summary>
    internal ClubFeature(ClubTable table) => _table = table;

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        CmdCreateClub, CmdReqClubInfo, CmdFxClubList, CmdFindAClub, CmdApplyClubList,
        CmdModifyNotice, CmdQuit, CmdModifyApply, CmdDailyAward, CmdReqDissolve,
        CmdSendMail, CmdReqClubMember, CmdReqApplylist, CmdReqClubLog, CmdCancelApply,
        CmdAgree, CmdIgnore, CmdChangePosition, CmdHeaderChange, CmdKickMember,
    };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = ClubTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(CmdReqClubInfo, OnReqClubInfo);       // ★ 覆盖 GameModule.cs:123 的占位
        ctx.MapLuaCall(CmdCreateClub, OnCreateClub);
        ctx.MapLuaCall(CmdFxClubList, OnFxClubList);
        ctx.MapLuaCall(CmdFindAClub, OnFindAClub);
        ctx.MapLuaCall(CmdApplyClubList, OnApplyClubList);
        ctx.MapLuaCall(CmdCancelApply, OnCancelApply);
        ctx.MapLuaCall(CmdReqClubMember, OnReqClubMember);
        ctx.MapLuaCall(CmdReqApplylist, OnReqApplylist);
        ctx.MapLuaCall(CmdReqClubLog, OnReqClubLog);
        ctx.MapLuaCall(CmdModifyNotice, OnModifyNotice);
        ctx.MapLuaCall(CmdModifyApply, OnModifyApply);
        ctx.MapLuaCall(CmdQuit, OnQuit);
        ctx.MapLuaCall(CmdReqDissolve, OnReqDissolve);
        ctx.MapLuaCall(CmdSendMail, OnSendMail);
        ctx.MapLuaCall(CmdDailyAward, OnDailyAward);
        // ---- 第十五轮：社团管理五条 ----
        ctx.MapLuaCall(CmdAgree, OnAgree);
        ctx.MapLuaCall(CmdIgnore, OnIgnore);
        ctx.MapLuaCall(CmdChangePosition, OnChangePosition);
        ctx.MapLuaCall(CmdHeaderChange, OnHeaderChange);
        ctx.MapLuaCall(CmdKickMember, OnKickMember);

        // ★ 必须用 OnPlayerPrepare（pf 1005 之前）：clubid 要随 PlayerNtf 一起下发，
        //   而「ClubId 与社团对象必须一致」这条不变量只有在发包之前修才有意义（见类注释 ③）。
        ctx.OnPlayerPrepare(EnsureClubIdInvariant);
        _boss = new ClubBossHandler(ctx, this);
    }

    // =====================================================================
    //  登录钩子：ClubId 不变量 + 解散到期
    // =====================================================================

    /// <summary>
    /// 登录时（pf 1005 之前）校准两件事：
    /// <list type="number">
    /// <item><b>解散到期</b>：<c>reqdissolvetime &gt; 0</c> 且已过
    ///   <see cref="DissolveWaitSeconds"/> ⇒ 真正解散（清社团 + 清 <see cref="Player.ClubId"/>）。
    ///   客户端只做倒计时显示（<c>UI_ClubMember.lua:121</c>），真正的解散是服务端权威动作。</item>
    /// <item><b>★★★ ClubId 不变量</b>（类注释 ③）：<c>ClubId</c> 必须恒等于社团对象的 id
    ///   （没有社团时为 0）。不一致就以**社团对象**为准修正，并打 WARN ——
    ///   这条日志出现意味着「上一次写入没走 <see cref="ClubBag.Write"/>」，是真 bug 的信号。</item>
    /// </list>
    /// 另外把「我」这条成员快照刷新成在线状态（名字/等级可能已经变了）。
    /// </summary>
    internal void EnsureClubIdInvariant(ClientSession session, Player player)
    {
        if (ApplyLoginInvariant(player, $"session {session.Id}"))
            _ctx.Store.Save(player);
    }

    /// <summary>
    /// <see cref="EnsureClubIdInvariant"/> 的**不落盘、不发包**部分（只改 <paramref name="player"/>），
    /// 返回是否发生了变化。拆出来是为了让单测能在不装配 <see cref="GameFeatureContext"/> 的情况下
    /// 直接验「不变量自愈」与「解散到期」这两条最关键的登录期行为。
    /// </summary>
    internal bool ApplyLoginInvariant(Player player, string tag)
    {
        var bag = ClubBag.Read(player);
        long now = Now();
        bool dirty = false;

        if (bag.Club is { } club && club.ReqDissolveTime > 0
            && now - club.ReqDissolveTime >= DissolveWaitSeconds)
        {
            Log.Info($"[{tag}] 社团「{club.Name}」(id={club.Id}) 的解散申请已到期"
                     + $"（申请于 {club.ReqDissolveTime}，等待 {DissolveWaitSeconds}s）⇒ 正式解散，清 ClubId");
            bag.Club = null;
            player.SetTask(TaskGroup, TaskLastQuitTime, (int)now);
            player.SetTask(TaskGroup, TaskDissolveTime, 0);
            dirty = true;
        }

        ulong expected = bag.Club?.Id ?? 0;
        if (player.ClubId != expected)
        {
            Log.Warn($"[{tag}] ★ ClubId 不变量被破坏：Player.ClubId={player.ClubId}"
                     + $" 但 Extra[\"club\"] 里的社团 id={expected}"
                     + "（GameModule.cs:590 会把 ClubId 发进 pf 1005，不一致会让 UI_ClubReward.lua:35 的 next(nil) 抛）"
                     + " ⇒ 以社团对象为准修正");
            dirty = true;
        }

        if (bag.Club is { } mine && RefreshSelfSnapshot(mine, player, online: true))
            dirty = true;

        if (dirty)
            bag.Write(player);
        return dirty;
    }

    /// <summary>
    /// 把「我」这条成员快照对齐到当前玩家状态（名字 / 等级 / 在线 / 头像）。返回是否有变化。
    ///
    /// 为什么要存快照而不是实时查：取证文档 §7.3 —— <c>PlayerStore</c> 按 **account** 命名，
    /// 而成员的键是 **roleid**，本服**没有 roleid → account 的反查表**
    /// （<c>World.DeriveRoleId</c> 是单向哈希，<c>World.cs:159</c>）⇒ 离线成员只能靠快照。
    /// 这个 API 形状是为将来换成全局 <c>ClubStore</c> 预留的。
    /// </summary>
    private bool RefreshSelfSnapshot(ClubState club, Player player, bool online)
    {
        var me = club.Member(player.Roleid);
        if (me is null)
            return false;

        string name = DisplayName(player);
        int level = Math.Max(1, player.Level);
        int state = online ? 1 : 0;
        long offTimes = online ? 0 : Now();
        uint girl = MainGirlOf(player);
        int frame = Math.Max(0, player.HeadFrame);

        bool changed = me.Name != name || me.Level != level || me.State != state
                       || me.GirlIconId != girl || me.HeadFrameId != frame
                       || (!online && me.OffTimes != offTimes);
        me.Name = name;
        me.Level = level;
        me.State = state;
        me.GirlIconId = girl;
        me.HeadFrameId = frame;
        if (!online)
            me.OffTimes = offTimes;
        return changed;
    }

    // =====================================================================
    //  10002 Club_ReqClubInfo —— 族 A（成功绝不带 nError）
    // =====================================================================

    /// <summary>
    /// 请求自己的社团数据。**整个社团域的地基**：
    /// <list type="bullet">
    /// <item>登录 <c>__init</c> 必发（<c>Preload.lua:319</c> <c>ClubCommon:InitClubInfo()</c>）；</item>
    /// <item>主城点「社团」按钮必发（<c>UI_Main.lua:43</c>），回包决定开 <c>UI_Club</c> 还是 <c>UI_Search</c>；</item>
    /// <item><c>me.clubid</c> 在客户端侧**只由它的回包设定**（<c>ClubCommon.lua:459</c>/<c>:463</c>）；</item>
    /// <item>社团任务领奖后客户端会自动重发它刷新 <c>weektaskvalue</c>（<c>MissionManager.lua:841..847</c>）。</item>
    /// </list>
    ///
    /// 上行**无参**（<c>LuaCall.lua:2152</c> 的 <c>ClubLogic:OnCmdReqClubInfo()</c> 也不传参）
    /// ⇒ <c>param</c> 恒为 null，一个字节都不能读。
    ///
    /// 回包：没社团 → <c>{}</c>（<c>ClubCommon.lua:454</c> <c>not club.primary</c> 就是「没社团」的合法答案）；
    /// 有社团 → 完整 <c>club</c> 对象（<see cref="BuildClubPayload"/>）。
    /// </summary>
    internal Task OnReqClubInfo(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        if (bag.Club is null)
        {
            // 自愈：ClubId 非 0 却没有社团对象 = 类注释 ③ 的崩溃前提，必须就地修掉
            if (player.ClubId != 0)
            {
                Log.Warn($"[session {session.Id}] ★ Club_ReqClubInfo(10002)：没有社团对象但 Player.ClubId={player.ClubId}"
                         + " ⇒ 就地清零（否则客户端 me.clubid 非 0 + MyClubInfo 为空，UI_ClubReward.lua:35 必崩）");
                bag.Write(player);
                _ctx.Store.Save(player);
            }
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ReqClubInfo(10002) 无参 → 该玩家没有社团，回空对象 {{}}"
                     + "（客户端走 pFuncNoClub ⇒ 打开 UI_Search）；推 pf 1024×1");
            return Task.CompletedTask;
        }

        var club = bag.Club;
        Reply(session, cmd, ClubPayload(club, player));
        Log.Info($"[session {session.Id}] Club_ReqClubInfo(10002) 无参 → 回完整社团"
                 + $"「{club.Name}」(id={club.Id} lv={club.Level} exp={club.Exp}"
                 + $" 成员 {club.Members.Count}/{_table.Level(club.Level).CountLmt}"
                 + $" 公告长度 {StringWidth(club.Notice)} 日志 {club.Logs.Count} 条"
                 + $" 周任务值 {club.WeekTaskValue} 解散申请 {club.ReqDissolveTime})；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10003 Club_FXClubList —— 族 F（客户端不看 nError）
    // =====================================================================

    /// <summary>
    /// 社团发现列表。<c>UI_Search:OnOpen</c> 自动发（<c>UI_Search.lua:75</c>）、「刷新」按钮再发（<c>:268</c>）。
    ///
    /// ★★ 真机实测上行**连 <c>tbParam</c> 键都没有**（整个 sJsonParam 就是 <c>{"sCmd":10003}</c>，
    /// 见 `work/probe-three-domains.md` §2）⇒ <c>param == null</c>，读参数的代码必须容忍。
    ///
    /// 回包 <c>{applyedlist, fx}</c>，两组元素都只需 6 个字段
    /// （<c>UI_Search.lua:190..203</c> 只读 <c>name/id/level/count/autojoinlevel/applylevel</c>；
    /// <c>appled</c> 由客户端自己写，<c>:138</c>/<c>:160</c>）。
    /// 单机没有真实的其它玩家，这里回**确定性假数据**（同一账号每次看到同一批，见
    /// <see cref="DiscoverClubs"/>）—— 表头六列因此全部有合法值。
    /// </summary>
    internal Task OnFxClubList(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var payload = BuildFxListPayload(DiscoverClubs(player), bag.Applied);
        Reply(session, cmd, payload);

        Log.Info($"[session {session.Id}] Club_FXClubList(10003) 上行 param={Describe(param)}"
                 + $"（真机实测就是 null）→ 回发现列表：applyedlist {((JsonArray)payload["applyedlist"]!).Count} 个"
                 + $"、fx {((JsonArray)payload["fx"]!).Count} 个（确定性假数据，种子=roleid {player.Roleid}）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10004 Club_FindAClub —— ★ 族 B（失败只能回空表）
    // =====================================================================

    /// <summary>
    /// 按 id 搜索一个社团（<c>UI_Search.lua:242..263</c> 的「搜索」按钮，上行 <c>{clubid}</c>）。
    ///
    /// ★ 族 B：客户端判据是 <c>if next(tbParam) then</c>（<c>LuaCall.lua:2273</c>）
    /// ⇒ 「查不到」的正确表达就是**回空表 <c>{}</c>**（客户端静默）。
    /// 回 <c>{nError=n}</c> 会被判成成功、把空 primary 灌进 <c>UI_Search:OnServer_FindAClub</c>，
    /// 那里 <c>UpdateSearchList</c> 读 <c>ClubPrimaryData.count</c> 会抛。
    /// </summary>
    internal Task OnFindAClub(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        ulong wanted = (ulong)Math.Max(0, ReadInt(AsObject(param)?["clubid"], 0));
        var bag = ClubBag.Read(player);

        JsonObject? hit = null;
        string what = "";
        if (wanted != 0 && bag.Club is { } mine && mine.Id == wanted)
        {
            hit = BuildPrimaryLitePayload(mine.Id, mine.Name, mine.Level, mine.Members.Count,
                                          mine.ApplyLevel, mine.AutoJoinLevel);
            what = $"命中自己的社团「{mine.Name}」";
        }
        else if (wanted != 0)
        {
            var npc = DiscoverClubs(player).FirstOrDefault(c => c.Id == wanted);
            if (npc is not null)
            {
                hit = BuildPrimaryLitePayload(npc.Id, npc.Name, npc.Level, npc.Count,
                                              npc.ApplyLevel, npc.AutoJoinLevel);
                what = $"命中发现列表里的「{npc.Name}」";
            }
        }

        if (hit is null)
        {
            Tip(session, TipNoClubData);
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_FindAClub(10004) clubid={wanted} → 查不到，"
                     + "回空表 {}（★ 族 B：失败只能回空表，回 {nError} 会被判成成功）+ CallClientTip 无社团数据；推 pf 1024×2");
            return Task.CompletedTask;
        }

        Reply(session, cmd, hit);
        Log.Info($"[session {session.Id}] Club_FindAClub(10004) clubid={wanted} → {what}，回 primary-lite；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10005 / 10100 申请 / 取消申请 —— ★ 族 B
    // =====================================================================

    /// <summary>
    /// 申请加入社团（单个 <c>UI_Search.lua:218</c> 与一键 <c>:286</c> 同形，上行 <c>{clublist=[id,…]}</c>）。
    ///
    /// 三条分支，全部照原服语义（文案键取证文档 §3.4）：
    /// <list type="number">
    /// <item>已有社团 → <c>{}</c> + <c>ui.Club.AlreadyOwnClub</c>；</item>
    /// <item>玩家等级 &lt; <c>applylevel</c> → <c>ui.Club.LessThanApplyLevel</c>；</item>
    /// <item>玩家等级 ≥ <c>autojoinlevel</c> → <b>自动入团</b>（<c>ui.Club.AutoJoinClub</c>），
    ///   回**完整 club 对象** —— <c>UI_Search:OnServer_Apply</c> 判的是
    ///   <c>tbParam.primary and tbParam.applylist and tbParam.memberlist</c>（<c>UI_Search.lua:390</c>），
    ///   三者齐全才会关掉 <c>UI_Search</c> 打开 <c>UI_Club</c>；</item>
    /// <item>否则记进「申请中」并回 <c>{applyedlist, fx}</c> 让列表刷新（按钮翻成「已申请」）。</item>
    /// </list>
    ///
    /// ★ 单机的取舍：那些社团是确定性假数据、没有真实社长来审批，所以「等审批」的申请
    /// **永远不会被通过**（10006/10007 不实现，客户端那两个按钮也只在有申请人时才可点）。
    /// 自动入团则是完整可玩的：假社团会被**物化**成真实社团对象存进本玩家存档
    /// （<see cref="MaterializeNpcClub"/>），职位 <see cref="PosOrdinary"/>。
    /// </summary>
    internal Task OnApplyClubList(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var wanted = ReadIdList(AsObject(param)?["clublist"]);

        if (bag.Club is not null)
        {
            Tip(session, TipAlreadyOwnClub);
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ApplyClubList(10005) clublist=[{string.Join(",", wanted)}]"
                     + $" → 已在社团「{bag.Club.Name}」中，回空表 {{}} + Tip 已经拥有社团；推 pf 1024×2");
            return Task.CompletedTask;
        }

        var pool = DiscoverClubs(player);
        var joinable = wanted
            .Select(id => pool.FirstOrDefault(c => c.Id == id))
            .Where(c => c is not null)
            .Select(c => c!)
            .ToList();

        if (joinable.Count == 0)
        {
            Tip(session, TipNoClubData);
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ApplyClubList(10005) clublist=[{string.Join(",", wanted)}]"
                     + " → 没有一个 id 在发现列表里，回空表 {} + Tip 无社团数据；推 pf 1024×2");
            return Task.CompletedTask;
        }

        // 优先看有没有「够等级自动入团」的（一键申请会把整屏 id 都发上来）
        var auto = joinable.FirstOrDefault(c => player.Level >= c.AutoJoinLevel
                                                && player.Level >= c.ApplyLevel);
        if (auto is not null)
        {
            long now = Now();
            var club = MaterializeNpcClub(auto, player, now);
            bag.Club = club;
            bag.Applied.Clear();                 // 入团了，其余申请一并作废
            bag.Write(player);                   // ★ 同一次写入里同步 Player.ClubId（类注释 ③）
            BumpJoinClubAchievements(session, player);
            _ctx.Store.Save(player);

            Tip(session, TipAutoJoin, club.Name);
            Reply(session, cmd, ClubPayload(club, player));
            Log.Info($"[session {session.Id}] Club_ApplyClubList(10005) clublist=[{string.Join(",", wanted)}]"
                     + $" → 玩家 LV.{player.Level} ≥ 自动准入 {auto.AutoJoinLevel}，自动加入「{club.Name}」"
                     + $"(id={club.Id} 职位 {PosOrdinary} 普通成员)，Player.ClubId={player.ClubId}；"
                     + "回完整 club 对象 + Tip 自动加入；推 pf 1024×2");
            return Task.CompletedTask;
        }

        int added = 0, tooLow = 0;
        foreach (var c in joinable)
        {
            if (player.Level < c.ApplyLevel)
            {
                tooLow++;
                continue;
            }
            if (bag.Applied.Contains(c.Id))
                continue;
            bag.Applied.Add(c.Id);
            added++;
        }
        bag.Write(player);
        _ctx.Store.Save(player);

        if (added == 0)
            Tip(session, tooLow > 0 ? TipLessThanApplyLevel : TipAlreadyApplied);

        var payload = BuildFxListPayload(pool, bag.Applied);
        Reply(session, cmd, payload);
        Log.Info($"[session {session.Id}] Club_ApplyClubList(10005) clublist=[{string.Join(",", wanted)}]"
                 + $" → 新增申请 {added} 个、等级不够 {tooLow} 个，当前申请中 {bag.Applied.Count} 个；"
                 + "回 {applyedlist, fx} 刷新列表；推 pf 1024×" + (added == 0 ? "2" : "1"));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 取消申请（<c>UI_Search.lua:223</c> 的「已申请」按钮，上行 <c>{clublist=[id]}</c>）。★ 族 B。
    /// 回 <c>{applyedlist, fx}</c> 让列表刷新（按钮翻回「申请」）。
    /// </summary>
    internal Task OnCancelApply(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var wanted = ReadIdList(AsObject(param)?["clublist"]);
        int removed = bag.Applied.RemoveAll(wanted.Contains);
        if (removed > 0)
        {
            bag.Write(player);
            _ctx.Store.Save(player);
        }

        Reply(session, cmd, BuildFxListPayload(DiscoverClubs(player), bag.Applied));
        Log.Info($"[session {session.Id}] Club_CancelApplyClubList(10100) clublist=[{string.Join(",", wanted)}]"
                 + $" → 取消 {removed} 个，剩余申请中 {bag.Applied.Count} 个；回 {{applyedlist, fx}}；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10001 Club_CreateClub —— 族 A
    // =====================================================================

    /// <summary>
    /// 创建社团（<c>UI_Search.lua:347..353</c>，上行 <c>{clubname, applylevel, autojoinlevel}</c>）。
    ///
    /// 客户端已做的校验（<c>UI_Search.lua:301..343</c>）服务端**必须复校** —— GM 通道绕得过：
    /// 名字 <c>GetStringLen</c> ∈ [4,12]（简中 = 2..6 汉字）、两个等级 ∈ [1,999]、
    /// <c>applylevel ≤ autojoinlevel</c>。
    ///
    /// 扣费：<c>ClubLevels.txt</c> 第 1 级的 <c>CreateFee = "03|250"</c> ⇒ 250 免费钻（货币 3）。
    /// ★ 客户端的「买得起吗」判据是 <c>me.freeDiamond + me.payDiamond</c>
    /// （<c>MoneyLogic.lua:150</c>），所以扣款要走同口径的钻石池、**先免费后付费** ——
    /// 直接复用 <see cref="ShopFeature.TrySpendMoney"/>（同一 assembly 的 internal static，
    /// 池定义在 <c>ShopFeature.cs:981</c>），不要自己再写一遍。
    ///
    /// 回包：完整 club 对象 —— 客户端据此 <c>me.clubid = primary.id</c> 并
    /// <c>UI.CloseAndOpen("UI_Club")</c>（<c>ClubCommon.lua:474..488</c>）。
    /// 失败一律回 <c>{}</c> + <see cref="Tip"/>：<c>{}</c> 让
    /// <c>ClubCommon:OnServer_CreateClub</c> 的 <c>next(tbParam) and tbParam.primary</c> 守卫
    /// （<c>ClubCommon.lua:477</c>）静默不动，比回一个语义模糊的 <c>ui.Error.&lt;n&gt;</c> 准确得多。
    /// </summary>
    internal Task OnCreateClub(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        string name = (o is null ? "" : ReadString(o["clubname"])).Trim();
        int applyLv = ReadInt(o?["applylevel"], MinApplyLevel);
        int autoLv = ReadInt(o?["autojoinlevel"], MinApplyLevel);
        string args = $"clubname=「{name}」(宽度 {StringWidth(name)}) applylevel={applyLv} autojoinlevel={autoLv}";

        var bag = ClubBag.Read(player);
        if (bag.Club is not null)
        {
            Reject(session, cmd, TipAlreadyOwnClub,
                   $"Club_CreateClub(10001) {args} → 已经拥有社团「{bag.Club.Name}」");
            return Task.CompletedTask;
        }

        int width = StringWidth(name);
        if (width < MinNameWidth || width > MaxNameWidth)
        {
            Reject(session, cmd, TipMaxName,
                   $"Club_CreateClub(10001) {args} → 名字宽度不在 [{MinNameWidth},{MaxNameWidth}] 内"
                   + "（UI_Search.lua:8..14 tbNameLimit[zh_CN]={4,12}）");
            return Task.CompletedTask;
        }
        if (!IsLegalApplyPair(applyLv, autoLv))
        {
            Reject(session, cmd, TipErrorCreateLevel,
                   $"Club_CreateClub(10001) {args} → 等级不合法（须 1..999 且 applylevel ≤ autojoinlevel，"
                   + "UI_Search.lua:324..343）");
            return Task.CompletedTask;
        }
        if (DiscoverClubs(player).Any(c => string.Equals(c.Name, name, StringComparison.Ordinal)))
        {
            Reject(session, cmd, TipNameUsed,
                   $"Club_CreateClub(10001) {args} → 名字与发现列表里的社团重名");
            return Task.CompletedTask;
        }

        var lv1 = _table.Level(1);
        var changedMoney = new List<int>();
        if (!ShopFeature.TrySpendMoney(player, lv1.CreateFeeType, lv1.CreateFee, changedMoney))
        {
            Reject(session, cmd, TipNotEnoughDiamond,
                   $"Club_CreateClub(10001) {args} → 创建费用不足"
                   + $"（需要 货币{lv1.CreateFeeType}×{lv1.CreateFee}，现有 {player.GetMoney(lv1.CreateFeeType)}）");
            return Task.CompletedTask;
        }

        long now = Now();
        var club = new ClubState
        {
            Id = OwnClubIdOf(player),
            Name = name,
            Level = 1,
            Exp = 0,
            ApplyLevel = applyLv,
            AutoJoinLevel = autoLv,
            // ★ 公告默认空串：空串是安全的（UI_Club.lua:411 的 string.len 只怕 nil），
            //   而客户端会自己显示占位提示 UIText("UI_Club.gonggaotip")（UI_Club.lua:406..410）
            //   —— 比服务端凭空造一句公告更诚实。
            Notice = "",
            ReqDissolveTime = 0,
            SendMailTimes = 0,
            SendMailDay = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow),
            WeekTaskValue = 0,
            CreateTime = now,
            HeaderId = player.Roleid,
            HeaderName = DisplayName(player),
        };
        club.Members.Add(SelfMember(player, PosHeader, now));
        // ★★★ 补一批确定性机器人团员（第十五轮）。
        //   否则自建社团只有社长一个人 ⇒ **社团管理三条（10009 改职位 / 10010 转让 / 10011 踢人）
        //   永远没有可操作对象**，功能等于不可用。
        //   与好友域同一思路：种子取自 roleid，同一账号每次都是同一批人。
        AddNpcMembers(club, player, now);
        club.AddLog(ClubLogType.Create, now, DisplayName(player));

        bag.Club = club;
        bag.Applied.Clear();
        bag.Write(player);                        // ★ ClubId 与社团对象在同一次写入里落地
        BumpJoinClubAchievements(session, player);
        _ctx.Store.Save(player);

        // ★ 先推货币再回包：否则界面上的钻石数不掉，玩家以为没扣（取证文档 §8.6）
        GameNotify.PushMoney(session, player, changedMoney);
        Reply(session, cmd, ClubPayload(club, player));

        Log.Info($"[session {session.Id}] Club_CreateClub(10001) {args} → 创建成功：id={club.Id}"
                 + $" 社长={club.HeaderName}(pos={PosHeader}) 成员 1/{lv1.CountLmt}"
                 + $"，扣 货币{lv1.CreateFeeType}×{lv1.CreateFee}（余 {player.GetMoney(lv1.CreateFeeType)}）"
                 + $"，Player.ClubId={player.ClubId}；推 pf 1032×{changedMoney.Count} + pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10027 / 10028 / 10029 —— 族 F，让社团主页三个按钮活过来
    // =====================================================================

    /// <summary>
    /// 请求成员列表。★ **不是 <c>OnOpen</c> 发** —— <c>UI_Club:OnClickMember</c>（<c>UI_Club.lua:345</c>）
    /// 先发命令，**回包里的 <c>strType</c> 才决定开哪个界面**（<c>LuaCall.lua:2175..2183</c>）：
    /// <c>"OpenMem"</c> → <c>UI.CloseAndOpen('UI_ClubMember')</c>、
    /// <c>"RefreshMem"</c> → <c>UI_ClubMember:OnToggleMember</c>、
    /// <c>"OpenBossMem"</c> → <c>UI.OpenAsChild("UI_ClubBossMember")</c>。
    /// ⇒ <b>不回显 <c>strType</c> 三个分支全不命中，界面根本打不开</b>（无遮罩无报错，最难查）。
    ///
    /// 没有社团时回 <c>{}</c>：客户端 <c>else</c> 分支（<c>LuaCall.lua:2186..2190</c>）会调
    /// <c>DissolvedClub()</c> 弹「社团已解散」并回主城 —— 这正是「社团没了」的正确表达。
    /// </summary>
    internal Task OnReqClubMember(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        string strType = ReadString(AsObject(param)?["strType"]);
        var club = ClubBag.Read(player).Club;
        if (club is null)
        {
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ReqClubMember(10027) strType=「{strType}」→ 没有社团，"
                     + "回空表 {}（客户端 DissolvedClub ⇒ 弹「社团已解散」回主城）；推 pf 1024×1");
            return Task.CompletedTask;
        }

        var payload = BuildMemberListPayload(club, strType);
        Reply(session, cmd, payload);
        Log.Info($"[session {session.Id}] Club_ReqClubMember(10027) strType=「{strType}」→ 回 memberlist"
                 + $" {club.Members.Count} 人 + 原样回显 strType（★ 不回显界面打不开）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 请求申请列表（成员界面「申请」页签，<c>UI_ClubMember.lua:436</c>，**无入参**）。
    /// 单机恒回空数组：没有真实玩家会来申请，客户端对空 <c>applylist</c> 完全安全
    /// （<c>UI_ClubMember.lua:326..355</c>，按钮 <c>interactable = Count &gt; 0</c>）。
    /// </summary>
    internal Task OnReqApplylist(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var club = ClubBag.Read(player).Club;
        if (club is null)
        {
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ReqClubApplylist(10028) 无参 → 没有社团，回空表 {{}}"
                     + "（客户端 DissolvedClub）；推 pf 1024×1");
            return Task.CompletedTask;
        }

        Reply(session, cmd, new JsonObject { ["applylist"] = new JsonArray() });
        Log.Info($"[session {session.Id}] Club_ReqClubApplylist(10028) 无参 → 回 applylist 空数组"
                 + "（单机没有其它玩家会申请，客户端按钮会自动置灰）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 请求社团日志（「社团日志」按钮 <c>UI_Club.lua:359</c>，**无入参**）。
    /// 与 10027 同款：**回包才是打开 <c>UI_ClubBook</c> 的触发器**（<c>LuaCall.lua:2223..2228</c>），
    /// 不回则按钮点了没反应。
    /// </summary>
    internal Task OnReqClubLog(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var club = ClubBag.Read(player).Club;
        if (club is null)
        {
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_ReqClubLog(10029) 无参 → 没有社团，回空表 {{}}"
                     + "（客户端 DissolvedClub）；推 pf 1024×1");
            return Task.CompletedTask;
        }

        Reply(session, cmd, BuildLogListPayload(club));
        Log.Info($"[session {session.Id}] Club_ReqClubLog(10029) 无参 → 回 actionloglist {club.Logs.Count} 条"
                 + $"（上限 {ClubState.MaxLogs}，ui.Club.BookTip「保留最新的50条」）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10008 Club_ModifyNotice —— ★ 族 B + 回包是裸数组
    // =====================================================================

    /// <summary>
    /// 修改社团公告（<c>ClubCommon.lua:413</c>，上行 <c>{newnotice}</c>）。
    ///
    /// ★★ 回包**不是** <c>{notice="…"}</c>，而是**裸数组** <c>["新公告"]</c>：
    /// <c>UI_Club.lua:396</c> 读的是 <c>tbParam[1]</c>。
    /// ★ 族 B：判据 <c>if next(tbParam)</c>（<c>LuaCall.lua:2349</c>）⇒ 失败只能回空表。
    ///
    /// 权限：客户端只给社长/副社长显示这个按钮（<c>UI_Club.lua:217</c>），服务端复校。
    /// </summary>
    internal Task OnModifyNotice(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        string notice = ReadString(AsObject(param)?["newnotice"]);
        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData,
                   $"Club_ModifyNotice(10008) newnotice 宽度 {StringWidth(notice)} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) == PosOrdinary)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ModifyNotice(10008) → 普通成员无权改公告（UI_Club.lua:217 也不显示这个按钮）");
            return Task.CompletedTask;
        }

        int width = StringWidth(notice);
        if (width <= 0 || width > MaxNoticeWidth)
        {
            Reject(session, cmd, TipMaxNotice,
                   $"Club_ModifyNotice(10008) newnotice 宽度 {width} 不在 (0,{MaxNoticeWidth}] 内"
                   + "（ClubCommon.lua:401 同款判据）");
            return Task.CompletedTask;
        }

        long now = Now();
        club.Notice = notice;
        club.AddLog(ClubLogType.ModifyNotice, now, DisplayName(player), notice);
        bag.Write(player);
        _ctx.Store.Save(player);

        Reply(session, cmd, BuildNoticePayload(notice));
        Log.Info($"[session {session.Id}] Club_ModifyNotice(10008) newnotice=「{Preview(notice)}」(宽度 {width})"
                 + $" → 落盘 + 写日志 type={ClubLogType.ModifyNotice}；"
                 + "回**裸数组** [新公告]（★ UI_Club.lua:396 读 tbParam[1]）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10014 Club_ModifyApply —— 族 A + 回包是整个 primary
    // =====================================================================

    /// <summary>
    /// 修改申请条件（成员页「申请设置」<c>UI_ClubMember.lua:553</c>，上行 <c>{applylevel, autojoinlevel}</c>）。
    ///
    /// ★★ 回包**整个 <c>tbParam</c> 就是完整的 <c>primary</c> 对象** ——
    /// <c>UI_ClubMember.lua:663</c> 是 <c>ClubCommon.MyClubInfo.primary = tbParam</c>（整体替换）。
    /// ★ 附带约束：族 A 判据是 <c>if tbParam.nError then</c>（<c>LuaCall.lua:2362</c>）
    /// ⇒ <c>primary</c> 里**绝不能出现名为 <c>nError</c> 的字段**（不会有，但别手滑加调试字段）。
    /// </summary>
    internal Task OnModifyApply(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        var bag = ClubBag.Read(player);
        var club = bag.Club;
        int applyLv = ReadInt(o?["applylevel"], club?.ApplyLevel ?? MinApplyLevel);
        int autoLv = ReadInt(o?["autojoinlevel"], club?.AutoJoinLevel ?? MinApplyLevel);
        string args = $"applylevel={applyLv} autojoinlevel={autoLv}";

        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_ModifyApply(10014) {args} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) == PosOrdinary)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ModifyApply(10014) {args} → 普通成员无权改申请条件"
                   + "（UI_ClubMember.lua:90 也不显示申请页签）");
            return Task.CompletedTask;
        }
        if (!IsLegalApplyPair(applyLv, autoLv))
        {
            Reject(session, cmd, TipErrorCreateLevel,
                   $"Club_ModifyApply(10014) {args} → 等级不合法（须 1..999 且 applylevel ≤ autojoinlevel，"
                   + "UI_ClubMember.lua:531..551）");
            return Task.CompletedTask;
        }

        club.ApplyLevel = applyLv;
        club.AutoJoinLevel = autoLv;
        bag.Write(player);
        _ctx.Store.Save(player);

        Reply(session, cmd, BuildPrimaryPayload(club));
        Log.Info($"[session {session.Id}] Club_ModifyApply(10014) {args} → 落盘；"
                 + "回**整个 primary 对象**（★ UI_ClubMember.lua:663 是整体替换，不是合并）；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10012 Club_Quit —— 族 A
    // =====================================================================

    /// <summary>
    /// 主动退团（成员页「退出社团」<c>UI_ClubMember.lua:492</c>，**无入参**）。
    ///
    /// 社长不能退，只能解散（<c>ui.Club.HeaderOnlyCanDissolve</c>）—— 客户端也只给非社长
    /// 显示退出按钮（<c>UI_ClubMember.lua:100..101</c>），但 GM 通道绕得过，服务端复校。
    ///
    /// 成功回 <c>{}</c>：客户端 <c>ClubCommon:OnServer_MyClubInfo({})</c> →
    /// <c>not club.primary</c> → <c>MyClubInfo={}</c> + <c>me.clubid=0</c>（<c>ClubCommon.lua:458..459</c>），
    /// 再由 <c>UI_ClubMember:OnServer_MemberChange</c> 判 <c>me.clubid == 0</c> 回主城 —— 正是想要的效果。
    ///
    /// ★ 服务端侧同时写任务值 (1,28)：客户端 <c>UI_Search.lua:83</c> 用它显示再入团倒计时。
    /// **但本服不用它做拒绝判据** —— 理由见 `work/club-domain-impl.md`（原服的冷却错误文案键
    /// 在 <c>ui.Club.*</c> 里没有对应条目，且单机没有「刷社团」的滥用面）。
    /// </summary>
    internal Task OnQuit(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, "Club_Quit(10012) 无参 → 本来就没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) == PosHeader)
        {
            Reject(session, cmd, TipHeaderCannotQuit,
                   $"Club_Quit(10012) 无参 → 我是社长（pos={PosHeader}），社长只能解散不能退出");
            return Task.CompletedTask;
        }

        long now = Now();
        string clubName = club.Name;
        ulong clubId = club.Id;
        bag.Club = null;                                 // 单机：社团对象只存在我的存档里，我一走它就没了
        bag.Applied.Clear();
        bag.Write(player);                               // ★ 同步把 Player.ClubId 清 0
        player.SetTask(TaskGroup, TaskLastQuitTime, (int)now);
        _ctx.Store.Save(player);

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, TaskLastQuitTime), (int)now),
        });
        Tip(session, TipQuitSuccess);
        Reply(session, cmd, new JsonObject());

        Log.Info($"[session {session.Id}] Club_Quit(10012) 无参 → 退出社团「{clubName}」(id={clubId})，"
                 + $"Player.ClubId={player.ClubId}，写任务值 (1,{TaskLastQuitTime})={now}；"
                 + "回空表 {}（客户端 me.clubid=0 ⇒ 回主城）；推 pf 1026×1 + pf 1024×2");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10022 Club_ReqDissolve —— 族 A，键名是原始 typo「dissoslve」
    // =====================================================================

    /// <summary>
    /// 申请解散 / 取消解散（成员页 <c>UI_ClubMember.lua:499..523</c>，上行 <c>{dissoslve=0|1}</c>）。
    ///
    /// ★★ 键名就是 <c>dissoslve</c>（原始 typo，<c>UI_ClubMember.lua:514</c>），
    /// 写成 <c>dissolve</c> 会永远读不到值。本实现同时兼容正确拼写，只为 GM 手输方便。
    ///
    /// ★ 10022 的 <c>nError == 1</c> **不是错误**，语义是「社团已解散」→
    /// <c>UI_ClubMember:DissolvedClub()</c>（<c>LuaCall.lua:2441..2444</c>）。本实现不用它 ——
    /// 真正的解散发生在 <see cref="DissolveWaitSeconds"/> 之后，由
    /// <see cref="EnsureClubIdInvariant"/> 在下次登录时执行，那时 10002 自然回 <c>{}</c>。
    ///
    /// 成功回**完整 club 对象**（客户端 <c>OnServer_MyClubInfo</c> + <c>ShowDissolve</c> 都要它，
    /// 后者读 <c>primary.reqdissolvetime</c> 起倒计时）。
    /// </summary>
    internal Task OnReqDissolve(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        // ★ dissoslve 是原始 typo；顺带认一下正确拼写（GM 手输容易写对）
        int flag = ReadInt(o?["dissoslve"] ?? o?["dissolve"], 1);
        var bag = ClubBag.Read(player);
        var club = bag.Club;

        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_ReqDissolve(10022) dissoslve={flag} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) != PosHeader)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ReqDissolve(10022) dissoslve={flag} → 只有社长能解散"
                   + "（UI_ClubMember.lua:100..101 也只给社长显示这个按钮）");
            return Task.CompletedTask;
        }

        long now = Now();
        if (flag != 0)
        {
            if (club.LastDissolveReqTime > 0
                && now - club.LastDissolveReqTime < DissolveRequestCooldownSeconds)
            {
                Reject(session, cmd, TipDissolveLimit,
                       $"Club_ReqDissolve(10022) dissoslve=1 → 距上次申请 "
                       + $"{now - club.LastDissolveReqTime}s < {DissolveRequestCooldownSeconds}s");
                return Task.CompletedTask;
            }
            club.ReqDissolveTime = now;
            club.LastDissolveReqTime = now;
            club.AddLog(ClubLogType.ReqDissolve, now, DisplayName(player));
            player.SetTask(TaskGroup, TaskDissolveTime, (int)now);
        }
        else
        {
            club.ReqDissolveTime = 0;
            club.AddLog(ClubLogType.CancelDissolve, now, DisplayName(player));
            player.SetTask(TaskGroup, TaskDissolveTime, 0);
        }

        bag.Write(player);
        _ctx.Store.Save(player);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, TaskDissolveTime),
                                        (int)club.ReqDissolveTime),
        });
        Reply(session, cmd, ClubPayload(club, player));

        Log.Info($"[session {session.Id}] Club_ReqDissolve(10022) dissoslve={flag}（★ 原始 typo 键名）"
                 + $" → {(flag != 0 ? $"申请解散，reqdissolvetime={now}，{DissolveWaitSeconds}s 后由登录钩子正式解散"
                                    : "取消解散，reqdissolvetime=0")}；"
                 + "回完整 club 对象（ShowDissolve 要读 primary.reqdissolvetime 起倒计时）；推 pf 1026×1 + pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10026 Club_SendMail —— 族 F
    // =====================================================================

    /// <summary>
    /// 发送社团邮件（<c>ClubCommon.lua:433</c>，上行 <c>{title, content}</c>）。
    ///
    /// ★★ **没有社团时绝对不能回包**：<c>ClubCommon:SendMailCallback</c>（<c>ClubCommon.lua:436</c>）
    /// 第一句就是无保护的 <c>ClubCommon.MyClubInfo.primary.send_mail_times</c>。
    /// 社团域不挂遮罩（类注释 ⑥）⇒ 不回包是安全的。
    ///
    /// 回包 <c>{nCount = 今日已发次数}</c>；<c>nCount</c> 缺失时客户端沿用旧值，不会崩。
    /// 上限 10（<c>ClubCommon.MaxSendClubTimes</c>），按 4 点日切重置。
    ///
    /// ★ 单机取舍：**邮件本体不投递**（社团只有自己，且邮件域尚未实现）——
    /// 只做「次数记账 + 回包」，这样界面上的 <c>x/10</c> 是真的。
    /// </summary>
    internal Task OnSendMail(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        string title = ReadString(o?["title"]);
        string content = ReadString(o?["content"]);
        var bag = ClubBag.Read(player);
        var club = bag.Club;

        if (club is null)
        {
            Tip(session, TipNoClubData);
            Log.Warn($"[session {session.Id}] Club_SendMail(10026) title=「{title}」content 宽度 "
                     + $"{StringWidth(content)} → 没有社团，★ 刻意不回包"
                     + "（ClubCommon.lua:436 无保护读 MyClubInfo.primary.send_mail_times，回包必抛）；只推 CallClientTip");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) == PosOrdinary)
        {
            Tip(session, TipNoAuthority);
            Log.Warn($"[session {session.Id}] Club_SendMail(10026) → 普通成员无权发社团邮件"
                     + "（UI_Club.lua:218 也不显示这个按钮），不回包；只推 CallClientTip");
            return Task.CompletedTask;
        }

        int today = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        if (club.SendMailDay != today)
        {
            club.SendMailDay = today;
            club.SendMailTimes = 0;
        }

        int width = StringWidth(content);
        if (width <= 0 || width > MaxMailWidth)
        {
            Reply(session, cmd, new JsonObject { ["nCount"] = club.SendMailTimes });
            Tip(session, TipNoClubData);
            Log.Warn($"[session {session.Id}] Club_SendMail(10026) content 宽度 {width} 不在 (0,{MaxMailWidth}] 内"
                     + $"（ClubCommon.lua:424 同款判据）⇒ 不计次，回 {{nCount={club.SendMailTimes}}}；推 pf 1024×2");
            return Task.CompletedTask;
        }
        if (club.SendMailTimes >= MaxSendMailTimes)
        {
            Tip(session, TipMailLimit);
            Reply(session, cmd, new JsonObject { ["nCount"] = club.SendMailTimes });
            Log.Info($"[session {session.Id}] Club_SendMail(10026) → 今日已发 {club.SendMailTimes}/{MaxSendMailTimes}，"
                     + $"拒绝并回 {{nCount={club.SendMailTimes}}} + Tip 次数已用尽；推 pf 1024×2");
            return Task.CompletedTask;
        }

        club.SendMailTimes++;
        bag.Write(player);
        _ctx.Store.Save(player);
        Reply(session, cmd, new JsonObject { ["nCount"] = club.SendMailTimes });

        Log.Info($"[session {session.Id}] Club_SendMail(10026) title=「{title}」content=「{Preview(content)}」"
                 + $"(宽度 {width}) → 今日第 {club.SendMailTimes}/{MaxSendMailTimes} 封，"
                 + "★ 邮件本体不投递（单机社团只有自己 + 邮件域未实现），只记账；"
                 + $"回 {{nCount={club.SendMailTimes}}}；推 pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  10020 Club_DailyAward —— 族 F（客户端 else 分支为空）+ 真实发奖
    // =====================================================================

    /// <summary>
    /// 社团每日福利（<c>UI_ClubReward.lua:73</c> 的「领取」按钮，**无入参**）。
    ///
    /// 奖励内容来自 <c>ClubLevels.DailyAwards</c>（1 级 = <c>[15-14-1-1-5, 15-1-1-1-2000]</c>
    /// = 5 点社团贡献 + 2000 金币），与客户端渲染的那一条列表同源（<c>UI_ClubReward.lua:38..45</c>）。
    ///
    /// <para><b>三条容易写错的</b></para>
    /// <list type="number">
    /// <item>★★ 发奖前必须过 <see cref="SplitIndexAwards"/>：<c>15-13</c>/<c>15-12</c>/<c>15-36</c>
    ///   会被 <c>RewardService</c> 当普通道具塞进背包（类注释 ④）。1 级的 <c>DailyAwards</c>
    ///   恰好只含已映射的 <c>15-14</c>，但高等级/未来配置不保证 —— 拦截必须无条件做。</item>
    /// <item>★★ 必须写任务值 <c>(1,35)</c> 并推 pf 1026：按钮可见性只看它
    ///   （<c>UI_ClubReward.lua:47..51</c> <c>lastday &lt; curday</c>）。客户端点击时**自己也会写**
    ///   （<c>:76</c>）并经 pf 1027 上行，所以服务端这一份取 <c>max</c> —— 客户端的
    ///   <c>GetLocalDay4AM()</c> 带 <c>3600*Zone</c> 时区偏移（<c>Lib.lua:1702</c>），
    ///   数值只会 ≥ 服务端的日序，直接覆盖会把它调小、导致重登后按钮又亮、可无限领。</item>
    /// <item>发奖必须走 pf 1031/1032（<see cref="GameNotify.PushRewards"/>），
    ///   pf 1104 不派发 Lua 事件 ⇒ 界面不动（AGENTS.md §5）。</item>
    /// </list>
    /// </summary>
    internal Task OnDailyAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Tip(session, TipNoClubData);
            Reply(session, cmd, new JsonObject());
            Log.Warn($"[session {session.Id}] Club_DailyAward(10020) 无参 → 没有社团，"
                     + "回空表 {}（客户端 else 分支为空，回什么都无害）+ Tip；推 pf 1024×2");
            return Task.CompletedTask;
        }

        int today = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow);
        int lastDay = player.GetTask(TaskGroup, TaskDailyAwardDay);
        if (lastDay >= today)
        {
            Reply(session, cmd, new JsonObject());
            Log.Info($"[session {session.Id}] Club_DailyAward(10020) 无参 → 今日已领"
                     + $"（任务值 (1,{TaskDailyAwardDay})={lastDay} ≥ 今日日序 {today}），不发奖；推 pf 1024×1");
            return Task.CompletedTask;
        }

        var levelRow = _table.Level(club.Level);
        var (normal, clubExp, activePoint, bossTimes) = SplitIndexAwards(levelRow.DailyAwards);

        var granted = _ctx.Rewards.Grant(player, normal, RewardSource.Extra);
        var changedTasks = new List<KeyValuePair<uint, int>>();

        ApplyIndexAwards(player, club, clubExp, activePoint, bossTimes, changedTasks);

        player.SetTask(TaskGroup, TaskDailyAwardDay, today);
        changedTasks.Add(new KeyValuePair<uint, int>(
            Player.ComposeTaskId(TaskGroup, TaskDailyAwardDay), today));

        bag.Write(player);
        _ctx.Store.Save(player);

        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, changedTasks);
        Reply(session, cmd, new JsonObject());

        Log.Info($"[session {session.Id}] Club_DailyAward(10020) 无参 → 按社团 {club.Level} 级发每日福利："
                 + $"普通奖励 {normal.Count} 项（货币 {granted.ChangedMoney.Count} 种 / 背包 {granted.ChangedItems.Count} 件）"
                 + $"，拦下的指数：社团经验 +{clubExp}、活跃点 +{activePoint}、出击次数 +{bossTimes}"
                 + $"（★ 这三种若直接 Grant 会变成背包道具）；社团 exp={club.Exp} lv={club.Level}；"
                 + $"写任务值 (1,{TaskDailyAwardDay})={today}；推 pf 1032/1031 + pf 1026×1 + pf 1024×1");
        return Task.CompletedTask;
    }

    // =====================================================================
    //  三种「指数」道具的拦截与落账
    // =====================================================================

    /// <summary>
    /// 把奖励元组拆成「能直接交给 <see cref="RewardService"/> 的」与「三种社团指数」。
    ///
    /// ★★★ 取证文档 §9.1.1：这四个道具的归属**只能靠 <c>ClassName</c> 判定**
    /// （<c>ItemList.txt</c> 的 <c>ExtParam2</c>「CoinAuto类为货币id」这一列四个都是空的）：
    /// <code>
    /// 15-14-1-1-N  战斗指数  ClubCoinAuto                 → 货币 11 社团贡献  ✅ 服务端已映射(ItemBaseTable.cs:202)
    /// 15-13-1-1-N  团队指数  ClubExpAuto                  → 社团经验          ❌ 未映射，必须拦
    /// 15-12-1-1-N  活跃指数  ClubActivePointAuto          → 社团活跃点        ❌ 未映射，必须拦
    /// 15-36-1-1-N  出击次数  ItemAddClubBossFightTimeAuto → 任务值 (1,552)    ❌ 未映射，必须拦
    /// </code>
    /// 不拦的后果：genre 15 ≥ 5 会走 <c>RewardService.cs:359..368</c> 的叠加分支，
    /// 「团队指数 ×50」变成背包里一件叫「团队指数」的道具。
    ///
    /// ★ 另一个易错点：<b>genre 15 的 <c>Detail</c> 与 <c>MoneyDef.ID</c> 不是同一套编号</b>
    /// （<c>15-1</c> = 金币 = MoneyDef 2、<c>15-2</c> = 免费钻 = MoneyDef 3）——
    /// 别把 <c>15-N</c> 的 N 当货币 id 用。
    ///
    /// 纯函数，便于单测。
    /// </summary>
    internal static (List<int[]> Normal, int ClubExp, int ActivePoint, int BossTimes)
        SplitIndexAwards(IEnumerable<int[]>? awards)
    {
        var normal = new List<int[]>();
        int clubExp = 0, activePoint = 0, bossTimes = 0;
        if (awards is null)
            return (normal, clubExp, activePoint, bossTimes);

        foreach (var t in awards)
        {
            if (t is null || t.Length < 4)
                continue;
            int count = t.Length >= 5 ? t[4] : 1;
            bool isIndex = t[0] == IndexGenre && t[2] == 1 && t[3] == 1;
            if (isIndex && t[1] == IndexDetailClubExp)
                clubExp += count;
            else if (isIndex && t[1] == IndexDetailActivePoint)
                activePoint += count;
            else if (isIndex && t[1] == IndexDetailBossTimes)
                bossTimes += count;
            else
                normal.Add(t);
        }
        return (normal, clubExp, activePoint, bossTimes);
    }

    /// <summary>
    /// 把 <see cref="SplitIndexAwards"/> 拦下来的三种指数真正落账：
    /// 社团经验（顺带按 <c>ClubLevels.LvUpExp</c> 升级）、社团活跃点、任务值 (1,552)。
    ///
    /// ★ 「加经验不写日志」：<c>ui.Club.Log9</c>「%s增加社团经验%s点」在
    /// <c>UI_ClubBook.lua:18</c> 被明确标注「未实现」（<c>Log10</c>「社团升到了%s级」同样）
    /// ⇒ 原服也不写这两种日志，本实现照办。
    /// </summary>
    private void ApplyIndexAwards(Player player, ClubState club, int clubExp, int activePoint,
                                  int bossTimes, List<KeyValuePair<uint, int>> changedTasks)
    {
        if (clubExp > 0)
        {
            club.Exp += clubExp;
            int newLevel = Math.Min(_table.MaxLevel, _table.LevelForExp(club.Exp));
            if (newLevel != club.Level)
            {
                Log.Info($"[club] 社团「{club.Name}」exp {club.Exp} ⇒ 等级 {club.Level} → {newLevel}"
                         + "（★ 不写日志：ui.Club.Log10「社团升到了%s级」在 UI_ClubBook.lua:19 标注未实现）");
                club.Level = newLevel;
            }
        }
        if (activePoint > 0)
            club.ActivePoint += activePoint;
        if (bossTimes > 0)
        {
            int v = player.GetTask(TaskGroup, TaskTodayExtraGainTimes) + bossTimes;
            player.SetTask(TaskGroup, TaskTodayExtraGainTimes, v);
            changedTasks.Add(new KeyValuePair<uint, int>(
                Player.ComposeTaskId(TaskGroup, TaskTodayExtraGainTimes), v));
        }
    }

    /// <summary>
    /// 「创建或者加入任意社团」成就（<c>achieveType = 106</c>，<c>AchieveType.txt:109</c>）。
    /// 把 <c>Achievement.txt</c> / <c>AchievementOther.txt</c> 里所有 <c>achieveType == 106</c>
    /// 的条目进度顶到 ≥ 1（现网命中成就 <b>2050</b>，<c>Achievement.txt:1042</c>，<c>missionMax=[1]</c>），
    /// 并推 pf 1026 让红点/可领状态立刻生效。
    ///
    /// 任务值位域用 <c>MissionFeature</c> 的现成口径
    /// （<c>ProgressOf = v&gt;&gt;1</c> / <c>ClaimedOf = v&amp;1</c> / <c>ComposeTaskValue</c>，
    /// 取证 <c>MissionManager.lua:595..606</c>）—— 不要自己再拼一遍位域。
    ///
    /// ★ GM 测试账号已被 <c>GmUnlock</c> 全量置完成，这里对它是幂等空操作；
    /// 存在的意义是让**普通新号**的成就 2050 在建社团后真的可领。
    /// </summary>
    private void BumpJoinClubAchievements(ClientSession session, Player player)
    {
        const int achieveTypeJoinClub = 106;
        var changed = new List<KeyValuePair<uint, int>>();

        foreach (var entry in _ctx.Missions.Achieve.Values.Concat(_ctx.Missions.AchieveOther.Values))
        {
            if (entry.AchieveType != achieveTypeJoinClub)
                continue;
            uint key = Player.ComposeTaskId(entry.Group, entry.Id);
            int old = player.GetTask(entry.Group, entry.Id);
            int progress = MissionFeature.ProgressOf(old);
            if (progress >= 1)
                continue;
            int composed = MissionFeature.ComposeTaskValue(1, MissionFeature.ClaimedOf(old));
            player.Tasks[key] = composed;
            changed.Add(new KeyValuePair<uint, int>(key, composed));
        }

        if (changed.Count == 0)
            return;
        GameNotify.PushTaskValues(session, changed);
        Log.Info($"[session {session.Id}] 社团动作触发成就 achieveType=106「创建或者加入任意社团」："
                 + $"{changed.Count} 条进度置 1（现网命中成就 2050）；推 pf 1026×1");
    }

    // =====================================================================
    //  回包组装（全部纯函数，便于单测）
    // =====================================================================

    /// <summary>
    /// 完整 <c>club</c> 对象 —— 10001 / 10002 / 10005自动入团 / 10022 的回包整体。
    ///
    /// <para><b>四个顶层键</b></para>
    /// <list type="bullet">
    /// <item><c>primary</c>：★ 判定「有没有社团」的**唯一依据**（<c>ClubCommon.lua:454</c>）。</item>
    /// <item><c>memberlist</c>：★ 必填，多处 <c>pairs()</c> 无 nil 保护（<c>UI_Club.lua:194</c>/<c>:306</c>）。</item>
    /// <item><c>applylist</c>：可空数组（<c>UI_ClubMember.lua:327</c> 有 nil 保护），
    ///   但 <c>UI_Search:OnServer_Apply</c> 判「自动入团」时要求它**存在**（<c>UI_Search.lua:390</c>）。</item>
    /// <item><c>actionloglist</c>：10029 必填，这里一并给上（<c>UI_ClubBook.lua:42</c>）。</item>
    /// </list>
    ///
    /// <para><b>两个刻意省略的键</b></para>
    /// <list type="bullet">
    /// <item><b><c>clubboss</c></b>：ClubPayload adds it through ClubBossHandler while a war is open. Without one, 省略 ⇒ <c>ClubCommon.lua:462</c> 的
    ///   <c>club.clubboss and club.clubboss.stageid or 0</c> 得 0 ⇒ <c>SetCurStageId(0)</c>
    ///   ⇒ 不加载任何 boss 配置。这是「关掉 boss 子系统」最干净的做法（类注释 ⑦）。</item>
    /// <item><c>historychatlist</c>：省略。<c>ClubCommon.lua:387</c> 只是把它赋给
    ///   <c>HistoryChatList</c>，消费点 <c>UI_Chat.lua:448</c> 有 <c>and</c> 守卫 ⇒ nil 安全。</item>
    /// </list>
    /// </summary>
    private JsonObject ClubPayload(ClubState club, Player player)
    {
        var payload = BuildClubPayload(club);
        _boss?.AddTo(payload, club, player);
        return payload;
    }

    /// <summary>Pays a club war award, routing the index items the reward service would otherwise bag.</summary>
    internal RewardGrantResult PayClubBossAward(Player player, ClubState club, IEnumerable<int[]> awards, List<KeyValuePair<uint, int>> changes)
    {
        var (normal, clubExp, activePoint, bossTimes) = SplitIndexAwards(awards);
        var granted = _ctx.Rewards.Grant(player, normal, RewardSource.Extra);
        ApplyIndexAwards(player, club, clubExp, activePoint, bossTimes, changes);
        return granted;
    }

    internal static JsonObject BuildClubPayload(ClubState club)
    {
        var members = new JsonArray();
        foreach (var m in club.Members)
            members.Add(BuildMemberPayload(m));

        var logs = new JsonArray();
        foreach (var l in club.Logs)
            logs.Add(BuildLogPayload(l));

        return new JsonObject
        {
            ["primary"] = BuildPrimaryPayload(club),
            ["applylist"] = new JsonArray(),
            ["memberlist"] = members,
            ["actionloglist"] = logs,
        };
    }

    /// <summary>
    /// <c>club.primary</c>。**十二个运行时必需字段**（取证文档 §4.2 的「最小可用 primary」）：
    /// <c>id/name/level/count/exp/applylevel/autojoinlevel/notice/headername/
    /// reqdissolvetime/send_mail_times/weektaskvalue</c>。
    ///
    /// 三条最容易漏的：
    /// <list type="number">
    /// <item>★★★ <c>reqdissolvetime</c>：<c>UI_ClubMember.lua:104</c> 的
    ///   <c>… .reqdissolvetime &gt; 0</c> 对 nil **直接抛**（0 = 未申请）。</item>
    /// <item>★★ <c>weektaskvalue</c>：<c>UI_ClubTask.lua:229/230/242/276/291</c> 全部无 nil 保护。</item>
    /// <item>★ <c>headername</c>：原服注释块（<c>LuaCall.lua:2099..2140</c>）里**没有它**，
    ///   但 <c>UI_Club.lua:202</c> 会显示社长名字。</item>
    /// </list>
    /// <c>status/vice1id/vice2id/data_day/data_week</c> 本包 UI 零读取，填 0 只为与原服注释块同形。
    /// ★ 绝不能出现名为 <c>nError</c> 的键（10014 会把整个 primary 当 tbParam 回给族 A 判据）。
    /// </summary>
    internal static JsonObject BuildPrimaryPayload(ClubState club) => new()
    {
        ["id"] = club.Id,
        ["name"] = club.Name,
        ["level"] = club.Level,
        ["count"] = club.Members.Count,
        ["exp"] = club.Exp,
        ["applylevel"] = club.ApplyLevel,
        ["autojoinlevel"] = club.AutoJoinLevel,
        ["notice"] = club.Notice,
        ["headername"] = club.HeaderName,
        ["headerid"] = club.HeaderId,
        ["reqdissolvetime"] = club.ReqDissolveTime,
        ["send_mail_times"] = club.SendMailTimes,
        ["weektaskvalue"] = club.WeekTaskValue,
        ["status"] = 0,
        ["vice1id"] = 0,
        ["vice2id"] = 0,
        ["data_day"] = 0,
        ["data_week"] = 0,
    };

    /// <summary>
    /// <c>memberlist</c> / <c>applylist</c> 的一个元素。
    ///
    /// ★★ 键名是 <c>pos</c>（9/8/0），**不是**原服注释块 <c>LuaCall.lua:2130</c> 里的 <c>post = 1</c>
    /// —— 全仓 <c>member.post</c> 零命中。详见 <see cref="ClubMemberState"/> 的类注释。
    ///
    /// 三个 boss 专用字段（<c>boss_explore_score</c> / <c>clubboss_enter_times</c> /
    /// <c>today_clubboss_enter_times</c>）虽然只被 boss 界面读（不可达，类注释 ⑦），
    /// 但那三处**都无 nil 保护**（<c>ClubCommon.lua:733</c>、<c>UI_ClubBossMap.lua:443</c>、
    /// <c>UI_ClubBossMember.lua:85</c>）⇒ 一并填 0，代价为零、消掉一整类风险。
    /// </summary>
    internal static JsonObject BuildMemberPayload(ClubMemberState m) => new()
    {
        ["roleid"] = m.Roleid,
        ["name"] = m.Name,
        ["level"] = m.Level,
        ["pos"] = m.Pos,
        ["state"] = m.State,
        ["girliconid"] = m.GirlIconId,
        ["headframeid"] = m.HeadFrameId,
        ["off_times"] = m.OffTimes,
        ["last7_contribute"] = m.Last7Contribute,
        ["daily_contribute"] = m.DailyContribute,
        ["listskinid"] = m.ListSkinId,
        ["join_date"] = m.JoinDate,
        ["leave_date"] = 0,
        ["contribut_log"] = new JsonArray(),
        ["boss_explore_score"] = 0,
        ["clubboss_enter_times"] = 0,
        ["today_clubboss_enter_times"] = 0,
    };

    /// <summary><c>actionloglist</c> 的一个元素（<c>{type, strlist, time}</c>，<c>UI_ClubBook.lua:128..181</c>）。</summary>
    internal static JsonObject BuildLogPayload(ClubLogEntry log)
    {
        var strlist = new JsonArray();
        foreach (var s in log.Strings)
            strlist.Add(s);
        return new JsonObject { ["type"] = log.Type, ["strlist"] = strlist, ["time"] = log.Time };
    }

    /// <summary>10027 的回包：<c>{memberlist, strType}</c>。★ <c>strType</c> 必须**原样回显**。</summary>
    internal static JsonObject BuildMemberListPayload(ClubState club, string strType)
    {
        var members = new JsonArray();
        foreach (var m in club.Members)
            members.Add(BuildMemberPayload(m));
        var payload = new JsonObject { ["memberlist"] = members };
        // strType 缺失时**不要伪造**：客户端三个分支全不命中只是「不开新界面」，
        // 而伪造成 "OpenMem" 会在玩家没点按钮的情况下强行切界面。
        if (strType.Length > 0)
            payload["strType"] = strType;
        return payload;
    }

    /// <summary>10029 的回包：<c>{actionloglist}</c>。</summary>
    internal static JsonObject BuildLogListPayload(ClubState club)
    {
        var logs = new JsonArray();
        foreach (var l in club.Logs)
            logs.Add(BuildLogPayload(l));
        return new JsonObject { ["actionloglist"] = logs };
    }

    /// <summary>
    /// 10008 的回包：★ **裸数组** <c>["新公告"]</c>（<c>UI_Club.lua:396</c> 读 <c>tbParam[1]</c>）。
    /// 裸数组天然不含 <c>nError</c> 键，也天然满足族 B 的 <c>next(tbParam)</c> 非空判据。
    /// </summary>
    internal static JsonArray BuildNoticePayload(string notice) => new(notice);

    /// <summary>
    /// 发现列表 <c>{applyedlist, fx}</c>（10003 / 10005 / 10100 共用）。
    ///
    /// 元素只需 6 个字段（<c>UI_Search.lua:190..203</c>）：<c>name/id/level/count/autojoinlevel/applylevel</c>
    /// —— 正好对应真机界面表头的六列「社团名称 | ID | 社团等级 | 人数 | 自动批准等级/申请所需等级」
    /// （`work/probe-three-domains.md` §2）。<c>appled</c> 由客户端自己写（<c>:138</c>/<c>:160</c>）。
    ///
    /// ★ <c>count</c> 与 <c>autojoinlevel</c>/<c>applylevel</c> **无 nil 保护**
    /// （<c>UI_Search.lua:200</c> 的字符串拼接、<c>:203</c> 的 <c>string.format("%d/%d", …)</c>）。
    /// </summary>
    internal static JsonObject BuildFxListPayload(IReadOnlyList<NpcClub> pool, IReadOnlyList<ulong> applied)
    {
        var appliedArr = new JsonArray();
        var fxArr = new JsonArray();
        foreach (var c in pool)
        {
            var lite = BuildPrimaryLitePayload(c.Id, c.Name, c.Level, c.Count, c.ApplyLevel, c.AutoJoinLevel);
            if (applied.Contains(c.Id))
                appliedArr.Add(lite);
            else
                fxArr.Add(lite);
        }
        return new JsonObject { ["applyedlist"] = appliedArr, ["fx"] = fxArr };
    }

    /// <summary>发现列表 / 搜索结果的元素（primary-lite，6 个字段）。</summary>
    internal static JsonObject BuildPrimaryLitePayload(ulong id, string name, int level, int count,
                                                       int applyLevel, int autoJoinLevel) => new()
    {
        ["id"] = id,
        ["name"] = name,
        ["level"] = level,
        ["count"] = count,
        ["applylevel"] = applyLevel,
        ["autojoinlevel"] = autoJoinLevel,
    };

    // =====================================================================
    //  确定性假社团（发现列表）
    // =====================================================================

    /// <summary>
    /// 发现列表里的一个假社团。**确定性**：同一账号每次看到同一批
    /// （种子来自 <see cref="Player.Roleid"/>，而 roleid 是账号的单向哈希，<c>World.cs:159</c>）。
    /// </summary>
    /// <param name="Id">社团 id（恒在 <c>100001..100800</c>，与自己的社团 id 段隔开）。</param>
    /// <param name="Name">社团名（取自 <see cref="NpcClubNames"/>，同批不重名）。</param>
    /// <param name="Level">社团等级（1..10，必须落在 <c>ClubLevels.txt</c> 内，否则客户端查表得 nil）。</param>
    /// <param name="Count">当前人数（1..<c>CountLmt</c>）。</param>
    /// <param name="ApplyLevel">申请所需等级（1..999）。</param>
    /// <param name="AutoJoinLevel">自动批准等级（≥ <paramref name="ApplyLevel"/>，≤ 999）。</param>
    /// <param name="HeaderId">社长 roleid（假 id，远离本服 roleid 值域）。</param>
    /// <param name="HeaderName">社长名字。</param>
    /// <param name="HeaderGirlIcon">社长头像用的少女 id。</param>
    internal sealed record NpcClub(
        ulong Id, string Name, int Level, int Count, int ApplyLevel, int AutoJoinLevel,
        ulong HeaderId, string HeaderName, uint HeaderGirlIcon);

    /// <summary>
    /// 生成本账号的发现列表（<see cref="DiscoverClubCount"/> 个确定性假社团）。
    ///
    /// <para><b>为什么可以造假</b></para>
    /// 取证文档 §7.1 + 用户要求：社团本质是多人系统，但「别人的社团列表」在单机里
    /// 没有任何真实数据源。造假的硬要求是 <b>①同一账号每次看到同一批</b>（否则每次刷新
    /// 列表都变，像 bug）、<b>②六列全部合法</b>（<c>level</c> 必须在 <c>ClubLevels.txt</c> 内、
    /// <c>count ≤ CountLmt</c>、两个等级 ∈ [1,999] 且 <c>applylevel ≤ autojoinlevel</c>）。
    ///
    /// <para><b>自动批准等级的两种档</b></para>
    /// 偶数位社团给「<c>applylevel + 5..29</c>」（高等级玩家申请即自动入团），
    /// 奇数位给 <b>999</b>（= 谁都不自动批准，所有申请都要审批）——
    /// 两种都是原厂界面上完全正常的设置，这样 <see cref="OnApplyClubList"/> 的两条分支在真机上都可达。
    /// </summary>
    internal IReadOnlyList<NpcClub> DiscoverClubs(Player player)
    {
        // ★ 生产环境（--bots false）不造假社团 —— 发现列表只显示真实玩家建的社团。
        //   在**唯一的生成入口**拦一刀，5 个调用点（534/573/636/719/782）全部生效。
        //   各处本来就能处理空列表：FirstOrDefault / Any / 分页都安全，
        //   且 BuildFxListPayload 收到空列表会回**空数组**（社团族 B 的约定是
        //   「非空表即成功」，空 fx 数组仍在一个非空 payload 里 ⇒ 不会被误判成失败）。
        // ★ `_ctx` 可能为 null：单测会直接 `new ClubFeature()` 后调本方法（不走 Install）,
        //   所以必须判空 —— 未装配时按「开启机器人」处理（与默认值一致）。
        if (_ctx is not null && !_ctx.Options.EnableBots)
            return Array.Empty<NpcClub>();

        var result = new List<NpcClub>(DiscoverClubCount);
        ulong seed = player.Roleid == 0 ? 1 : player.Roleid;
        int nameOffset = (int)(Mix(seed, 7) % (ulong)NpcClubNames.Length);
        int headerOffset = (int)(Mix(seed, 11) % (ulong)NpcHeaderNames.Length);

        for (int i = 0; i < DiscoverClubCount; i++)
        {
            // id 按 i 分块（每块 100），块内取 0..99 ⇒ 8 个 id 互不相同且恒在 100001..100800
            ulong id = NpcClubIdBase + (ulong)(i * 100) + Mix(seed, 200 + i) % 100;
            int level = 1 + (int)(Mix(seed, 300 + i) % 10);
            int countLmt = Math.Max(1, _table.Level(level).CountLmt);
            int count = 1 + (int)(Mix(seed, 400 + i) % (ulong)countLmt);
            int applyLevel = 1 + (int)(Mix(seed, 500 + i) % 60);
            int autoJoin = i % 2 == 0
                ? Math.Min(MaxApplyLevel, applyLevel + 5 + (int)(Mix(seed, 600 + i) % 25))
                : MaxApplyLevel;
            ulong headerId = NpcRoleIdBase + id;
            if (headerId == player.Roleid)
                headerId++;                       // 理论上撞不到（roleid ~5.2e15），仍留一道保险

            result.Add(new NpcClub(
                Id: id,
                Name: NpcClubNames[(nameOffset + i) % NpcClubNames.Length],
                Level: level,
                Count: Math.Min(count, countLmt),
                ApplyLevel: applyLevel,
                AutoJoinLevel: autoJoin,
                HeaderId: headerId,
                HeaderName: NpcHeaderNames[(headerOffset + i) % NpcHeaderNames.Length],
                HeaderGirlIcon: 1 + (uint)(Mix(seed, 700 + i) % 30)));
        }
        return result;
    }

    /// <summary>
    /// 把一个假社团**物化**成真实社团对象（自动入团时用）。
    ///
    /// 成员表 = 假社长（<see cref="PosHeader"/>，离线）+ 我（<see cref="PosOrdinary"/>，在线）。
    /// <c>exp</c> 取该等级的 <c>LvUpExp</c>，保证 <c>ClubTable.LevelForExp(exp) == level</c>
    /// —— 否则客户端显示的 <c>exp/下一级LvUpExp</c> 会出现「经验 0 但已 7 级」的怪相。
    ///
    /// ★ 一处已知的单机简化：物化之后 <c>primary.count</c> 变成**真实成员数 2**，
    /// 而发现列表里显示的是假人数。客户端两处用的其实都是 <c>#memberlist</c>
    /// （<c>UI_Club.lua:206</c>、<c>UI_ClubMember.lua:186</c>），<c>primary.count</c> 只在
    /// 发现列表里显示 ⇒ 只是「进去之后发现人变少了」，不会崩、不影响任何判定。
    /// </summary>
    internal ClubState MaterializeNpcClub(NpcClub npc, Player player, long now)
    {
        var club = new ClubState
        {
            Id = npc.Id,
            Name = npc.Name,
            Level = npc.Level,
            Exp = _table.Level(npc.Level).LvUpExp,
            ApplyLevel = npc.ApplyLevel,
            AutoJoinLevel = npc.AutoJoinLevel,
            Notice = "",
            ReqDissolveTime = 0,
            SendMailTimes = 0,
            SendMailDay = CafeFeature.CafeDayIndex(DateTimeOffset.UtcNow),
            WeekTaskValue = 0,
            CreateTime = now - 86400L * 30,          // 假社团「30 天前成立」，让第一条日志有合理日期
            HeaderId = npc.HeaderId,
            HeaderName = npc.HeaderName,
        };

        club.Members.Add(new ClubMemberState
        {
            Roleid = npc.HeaderId,
            Name = npc.HeaderName,
            Level = Math.Max(1, npc.AutoJoinLevel == MaxApplyLevel ? npc.ApplyLevel + 10 : npc.AutoJoinLevel),
            Pos = PosHeader,
            State = 0,                                // 离线
            GirlIconId = npc.HeaderGirlIcon,
            HeadFrameId = 0,                          // ★ 0 恒安全（GetBorderID(0) → 1，一定在 HeadFrame.txt 里）
            OffTimes = now - 3600,                    // 「1 小时内」离线，避免显示成「2个月前」（off_times==0 的语义）
            Last7Contribute = 0,
            DailyContribute = 0,
            ListSkinId = 0,
            JoinDate = club.CreateTime,
        });
        club.Members.Add(SelfMember(player, PosOrdinary, now));

        club.AddLog(ClubLogType.Create, club.CreateTime, npc.HeaderName);
        club.AddLog(ClubLogType.AutoJoin, now, DisplayName(player));
        return club;
    }

    /// <summary>「我」这条成员快照。</summary>
    private ClubMemberState SelfMember(Player player, int pos, long now) => new()
    {
        Roleid = player.Roleid,
        Name = DisplayName(player),
        Level = Math.Max(1, player.Level),
        Pos = pos,
        State = 1,
        GirlIconId = MainGirlOf(player),
        // ★ Player.HeadFrame 目前全服恒 0（grep 全仓无写入点），而 0 恒安全；
        //   若将来有「换头像框」功能写了非法 id，客户端 UI_ClubMember.lua:237 会抛 ——
        //   那时应该在写入侧过 HeadFrame.txt 白名单，而不是在这里兜。
        HeadFrameId = Math.Max(0, player.HeadFrame),
        OffTimes = 0,
        Last7Contribute = 0,
        DailyContribute = 0,
        ListSkinId = 0,
        JoinDate = now,
    };

    /// <summary>
    /// 玩家自己那个社团的 id：<see cref="OwnClubIdBase"/> + roleid 的低位。
    /// 确定性（同账号重建社团拿同一个 id），且与假社团段 <c>100001..100800</c> 永不重叠。
    /// 原服的分配策略未取证（取证文档 §11 第 13 条），这里只保证「稳定 + 不撞号」。
    /// </summary>
    internal static ulong OwnClubIdOf(Player player)
        => OwnClubIdBase + player.Roleid % 800_000;

    /// <summary>
    /// splitmix64 风格的确定性混淆（**不是** <c>Random</c> —— 必须跨进程可复现）。
    /// </summary>
    internal static ulong Mix(ulong seed, int salt)
    {
        unchecked
        {
            ulong x = seed + 0x9E3779B97F4A7C15UL * (ulong)(salt + 1);
            x ^= x >> 30;
            x *= 0xBF58476D1CE4E5B9UL;
            x ^= x >> 27;
            x *= 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return x;
        }
    }

    // =====================================================================
    //  小工具
    // =====================================================================

    /// <summary>
    /// 「失败」的统一出口：回**空表 <c>{}</c>** + 一条 <c>CallClientTip</c>。
    ///
    /// 为什么统一成这一种：见类注释 ②。空表在 6 个族里都被判成「无事发生」——
    /// 族 A/C/D/E 缺 <c>nError</c> 一律判成功但没有数据可用，族 B/F 的 <c>next({})</c> 为假直接静默。
    /// 而 <c>{nError=n}</c> 只在族 A/C/D/E 安全，在族 B 会引发「玩家被踢出社团」的灾难。
    /// </summary>
    private void Reject(ClientSession session, int cmd, string tipKey, string why)
    {
        Tip(session, tipKey);
        Reply(session, cmd, new JsonObject());
        Log.Warn($"[session {session.Id}] {why} ⇒ 回空表 {{}}（★ 族 B 回 {{nError}} 会被判成成功并清空 me.clubid）"
                 + $" + CallClientTip {tipKey}；推 pf 1024×2");
    }

    private void Reply(ClientSession session, int cmd, JsonNode payload)
        => _ctx.LuaCalls.CallClient(session, cmd, payload);

    /// <summary>发一条 <c>S2C.CallClientTip</c>（参数是**裸数组** <c>[key, fmtArgs…]</c>）。</summary>
    private void Tip(ClientSession session, string key, params string[] formatArgs)
    {
        var arr = new JsonArray { key };
        foreach (var a in formatArgs)
            arr.Add(a);
        _ctx.LuaCalls.CallClientFunction(session, FnCallClientTip, arr);
    }

    /// <summary>
    /// 取会话上的玩家；未登录只记 WARN 不抛（真机上任何顺序都可能到）。
    /// </summary>
    private static bool TryPlayer(ClientSession session, int cmd, out Player player)
    {
        if (session.Tag is Player p)
        {
            player = p;
            return true;
        }
        Log.Warn($"[session {session.Id}] 社团命令 cmd={cmd} 到达但会话未登录（Tag 非 Player），忽略");
        player = null!;
        return false;
    }

    /// <summary>
    /// ★★ 上行 <c>tbParam</c> 可能是 <c>JsonObject</c> / <c>JsonArray</c> / 裸标量 / <c>null</c>
    /// （类注释 ⑤），而 <c>JsonNode["key"]</c> 对 <c>JsonArray</c> **会抛**。
    /// 所有读参数的地方一律先过这个函数。
    /// </summary>
    internal static JsonObject? AsObject(JsonNode? param) => param as JsonObject;

    /// <summary>读 <c>{clublist=[id,…]}</c> 里的 id 数组（非数组/畸形一律得空表，不抛）。</summary>
    internal static List<ulong> ReadIdList(JsonNode? node)
    {
        var result = new List<ulong>();
        if (node is not JsonArray arr)
            return result;
        foreach (var n in arr)
        {
            ulong id = ClubJson.ReadULong(n);
            if (id != 0 && !result.Contains(id))
                result.Add(id);
        }
        return result;
    }

    /// <summary>我在这个社团里的职位（不在成员表里按普通成员算）。</summary>
    internal static int PosOf(ClubState club, Player player)
        => club.Member(player.Roleid)?.Pos ?? PosOrdinary;

    /// <summary>
    /// 客户端 <c>GetStringLen</c> 的等价实现（<c>Lib.lua:1268..1290</c>）：
    /// ASCII（字节值 1..127）记 1，其余每个字符记 2。
    ///
    /// 用途：社团名（4..12）、公告（≤140）、邮件正文（≤400）的长度校验必须与客户端同口径，
    /// 否则会出现「客户端放行、服务端拒绝」的静默失败。
    /// ★ 注意 Lua 版是按 UTF-8 **字节**走的（非 ASCII 一律当 3 字节 / 宽度 2），
    /// 所以对 4 字节 UTF-8（emoji）两边都会算错 —— 这里按 C# 的 UTF-16 码元近似，
    /// 差异只出现在 emoji 上，且方向是「服务端更宽松」，不会造成静默失败。
    /// </summary>
    internal static int StringWidth(string? s)
    {
        if (string.IsNullOrEmpty(s))
            return 0;
        int w = 0;
        foreach (char c in s)
            w += c <= 127 ? 1 : 2;
        return w;
    }

    /// <summary>申请等级 / 自动准入等级是否合法（<c>UI_Search.lua:324..343</c> 同款三条判据）。</summary>
    internal static bool IsLegalApplyPair(int applyLevel, int autoJoinLevel)
        => applyLevel >= MinApplyLevel && applyLevel <= MaxApplyLevel
           && autoJoinLevel >= MinApplyLevel && autoJoinLevel <= MaxApplyLevel
           && applyLevel <= autoJoinLevel;

    /// <summary>
    /// 头像用的少女 id：优先「看板娘」任务值 <c>(2,2)</c>（<c>GmUnlock.cs:236..239</c>），
    /// 其次第一张持有的角色卡，最后兜底 1。
    /// ★ 不能是 0/nil：<c>CardCommon:GetGirlIcon</c>（<c>CardCommon.lua:547</c>）是
    /// <c>string.format("Npc/icon_n_girl%d.png", nGirlID)</c>，nil 会让 <c>string.format</c> 抛。
    /// </summary>
    internal static uint MainGirlOf(Player player)
    {
        int main = player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId);
        if (main > 0)
            return (uint)main;
        if (player.Girls.Count > 0)
            return player.Girls[0].Girlid;
        return 1;
    }

    /// <summary>空名字会让成员列表出现空白行，给一个可见兜底（<c>name</c> 是硬必填）。</summary>
    internal static string DisplayName(Player player)
        => string.IsNullOrEmpty(player.Name) ? (player.Account.Length > 0 ? player.Account : "Nameless") : player.Name;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    // =====================================================================
    //  第十五轮：社团管理五条 —— 全部是**族 B**
    // =====================================================================
    //
    // ★★★ 五条的客户端分支完全同构（LuaCall.lua 各自的 else 分支）：
    //     if next(tbParam) then ClubCommon:OnServer_MyClubInfo(tbParam); ... end
    //   ⇒ **非空表 = 成功**、**空表 = 失败**。
    //   ⇒ 失败时回 {nError=n} 会因为「表非空」被判成**成功**，
    //     进而 OnServer_MyClubInfo 拿一个没有 primary 的表 → 清空 me.clubid → 玩家被踢回主城。
    //   本域统一走 Reject()（回空表 + CallClientTip），它已经内置了这条保护。
    //
    // ★ 成功一律回 BuildClubPayload(club)（完整社团信息）：
    //   客户端 OnServer_MyClubInfo 会整体替换 MyClubInfo，然后
    //   UI_ClubMember:OnServer_MemberChange / UI_Club:OnServer_HeaderChange 重绘列表。

    /// <summary>
    /// 10006 同意入团申请。上行 <c>{agreelist=[roleid,…]}</c>
    /// （<c>UI_ClubMember.lua:612</c> 一键同意 / <c>:617</c> 单个同意）。
    ///
    /// ★ 权限：普通成员无权（客户端 <c>:90</c> 也不显示申请页签，但 GM 通道绕得过 ⇒ 服务端复校）。
    /// ★★ 本服 <c>applylist</c> 恒为空（<see cref="OnReqApplylist"/>：单机没有别的玩家会申请）
    ///    ⇒ 正常流程下这条命令**没有可同意的对象**，按「申请不存在」拒绝。
    ///    保留实现是为了：① GM 造数据时可用；② 将来接入多玩家时不必回头补。
    /// </summary>
    internal Task OnAgree(ClientSession session, int cmd, JsonNode? param)
        => HandleApplyDecision(session, cmd, param, "agreelist", agree: true);

    /// <summary>
    /// 10007 拒绝入团申请。上行 <c>{ignorelist=[roleid,…]}</c>
    /// （<c>UI_ClubMember.lua:600</c> 一键忽略 / <c>:622</c> 单个忽略）。语义同 <see cref="OnAgree"/>。
    /// </summary>
    internal Task OnIgnore(ClientSession session, int cmd, JsonNode? param)
        => HandleApplyDecision(session, cmd, param, "ignorelist", agree: false);

    /// <summary>10006/10007 共用：审批入团申请。</summary>
    private Task HandleApplyDecision(ClientSession session, int cmd, JsonNode? param,
                                     string listKey, bool agree)
    {
        string tag = agree ? "Club_Agree(10006)" : "Club_Ignore(10007)";
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"{tag} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) == PosOrdinary)
        {
            Reject(session, cmd, TipNoAuthority, $"{tag} → 普通成员无权审批入团申请");
            return Task.CompletedTask;
        }

        var ids = ReadIdList(param, listKey);
        if (ids.Count == 0)
        {
            Reject(session, cmd, TipNoClubData, $"{tag} → {listKey} 为空或格式非法：{Describe(param)}");
            return Task.CompletedTask;
        }

        // ★ 本服申请列表恒空 ⇒ 一定找不到；这里明确拒绝而不是假装成功，
        //   否则客户端会把「申请被同意」的假状态渲染出来。
        Reject(session, cmd, TipNoClubData,
               $"{tag} → 申请人 [{string.Join(",", ids)}] 不在申请列表里"
               + "（本服 applylist 恒为空：单机没有其它玩家会申请入团）");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 10009 职位变更。上行 <c>{memberid, srcpos, dstpos}</c>（<c>UI_ClubMember.lua:579</c>）。
    ///
    /// ★ 客户端只在 <b>副社长(8) ↔ 普通(0)</b> 之间切换（<c>:573..578</c>），社长位不参与
    /// —— 转让社长走 10010。服务端按此复校，拒绝把谁改成 9。
    /// ★ 权限：只有**社长**能改职位（<c>:90</c> 的按钮可见性 + 服务端复校）。
    /// </summary>
    internal Task OnChangePosition(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        ulong memberId = ClubJson.ReadULong(o?["memberid"]);
        int dstPos = ReadInt(o?["dstpos"], -1);
        string args = $"memberid={memberId} dstpos={dstPos}";

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_ChangePostion(10009) {args} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) != PosHeader)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ChangePostion(10009) {args} → 只有社长能调整职位");
            return Task.CompletedTask;
        }
        if (dstPos != PosVice && dstPos != PosOrdinary)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ChangePostion(10009) {args} → 目标职位非法"
                   + $"（只允许 {PosVice} 副社长 / {PosOrdinary} 普通；社长位要走 10010 转让）");
            return Task.CompletedTask;
        }

        var target = club.Member(memberId);
        if (target is null)
        {
            Reject(session, cmd, TipNoClubData,
                   $"Club_ChangePostion(10009) {args} → 目标不在本社团成员里");
            return Task.CompletedTask;
        }
        if (target.Roleid == player.Roleid)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ChangePostion(10009) {args} → 不能改自己的职位");
            return Task.CompletedTask;
        }
        if (target.Pos == dstPos)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_ChangePostion(10009) {args} → 目标已经是该职位（pos={dstPos}）");
            return Task.CompletedTask;
        }

        int srcPos = target.Pos;
        target.Pos = dstPos;
        // ★ 日志 type=6 的 strlist 必须是 **4 段**（name, name, srcpos, dstpos），
        //   少一段该行会被渲染成空串（ClubState.cs 的 ClubLogType 注释）。
        club.AddLog(ClubLogType.ChangePos, Now(), DisplayName(player), target.Name,
                    srcPos.ToString(), dstPos.ToString());
        bag.Write(player);
        _ctx.Store.Save(player);

        Reply(session, cmd, ClubPayload(club, player));
        Log.Info($"[session {session.Id}] Club_ChangePostion(10009) {args} → "
                 + $"「{target.Name}」职位 {srcPos} → {dstPos}"
                 + $"（{(dstPos == PosVice ? "提为副社长" : "降为普通成员")}）；"
                 + "回完整社团信息 + 写日志 type=6；推 pf 1024×1");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 10010 社长转让。上行 <c>{viceid}</c>（<c>UI_Club.lua:321</c>，从副社长列表里选）。
    ///
    /// ★ 转让后**自己降为副社长**（原厂语义：社长位只有一个，日志 type=7 是「%s将社长转让%s」）。
    /// ★ 客户端 <c>UI_Club.lua:310..320</c> 只把 <b>副社长</b> 放进候选列表 ⇒ 服务端同样只接受副社长。
    /// </summary>
    internal Task OnHeaderChange(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        ulong viceId = ClubJson.ReadULong(o?["viceid"]);
        string args = $"viceid={viceId}";

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_HeaderChange(10010) {args} → 没有社团");
            return Task.CompletedTask;
        }
        if (PosOf(club, player) != PosHeader)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_HeaderChange(10010) {args} → 只有社长能转让社长位");
            return Task.CompletedTask;
        }

        var target = club.Member(viceId);
        if (target is null)
        {
            Reject(session, cmd, TipNoClubData,
                   $"Club_HeaderChange(10010) {args} → 目标不在本社团成员里");
            return Task.CompletedTask;
        }
        if (target.Roleid == player.Roleid)
        {
            Reject(session, cmd, TipNoAuthority, $"Club_HeaderChange(10010) {args} → 不能转让给自己");
            return Task.CompletedTask;
        }
        if (target.Pos != PosVice)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_HeaderChange(10010) {args} → 目标不是副社长（pos={target.Pos}）"
                   + "；客户端候选列表也只放副社长（UI_Club.lua:310..320）");
            return Task.CompletedTask;
        }

        var me = club.Member(player.Roleid);
        target.Pos = PosHeader;
        if (me is not null)
            me.Pos = PosVice;                 // ★ 原社长降为副社长
        club.HeaderName = target.Name;        // 社团主页显示的社长名

        club.AddLog(ClubLogType.ChangeHeader, Now(), DisplayName(player), target.Name);
        bag.Write(player);
        _ctx.Store.Save(player);

        Reply(session, cmd, ClubPayload(club, player));
        Log.Info($"[session {session.Id}] Club_HeaderChange(10010) {args} → 社长转让给「{target.Name}」，"
                 + "自己降为副社长；回完整社团信息 + 写日志 type=7；推 pf 1024×1");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 10011 踢出成员。上行 <c>{kickroleid}</c>（<c>UI_ClubMember.lua:587</c>，带二次确认框）。
    ///
    /// ★ 权限：社长/副社长可踢；且**不能踢社长、不能踢自己**。
    /// ★ 副社长之间能不能互踢：客户端 <c>:90</c> 的按钮可见性只按「自己是不是管理」判，
    ///   本服按「不能踢**职位不低于自己**的人」复校（社长可踢副社长，副社长只能踢普通成员）。
    /// </summary>
    internal Task OnKickMember(ClientSession session, int cmd, JsonNode? param)
    {
        if (!TryPlayer(session, cmd, out var player))
            return Task.CompletedTask;

        var o = AsObject(param);
        ulong kickId = ClubJson.ReadULong(o?["kickroleid"]);
        string args = $"kickroleid={kickId}";

        var bag = ClubBag.Read(player);
        var club = bag.Club;
        if (club is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_KickMember(10011) {args} → 没有社团");
            return Task.CompletedTask;
        }

        int myPos = PosOf(club, player);
        if (myPos == PosOrdinary)
        {
            Reject(session, cmd, TipNoAuthority, $"Club_KickMember(10011) {args} → 普通成员无权踢人");
            return Task.CompletedTask;
        }

        var target = club.Member(kickId);
        if (target is null)
        {
            Reject(session, cmd, TipNoClubData, $"Club_KickMember(10011) {args} → 目标不在本社团成员里");
            return Task.CompletedTask;
        }
        if (target.Roleid == player.Roleid)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_KickMember(10011) {args} → 不能踢自己（要走 10012 退团 / 10022 解散）");
            return Task.CompletedTask;
        }
        if (target.Pos >= myPos)
        {
            Reject(session, cmd, TipNoAuthority,
                   $"Club_KickMember(10011) {args} → 不能踢职位不低于自己的人"
                   + $"（我 pos={myPos}、对方 pos={target.Pos}）");
            return Task.CompletedTask;
        }

        club.Members.Remove(target);
        club.AddLog(ClubLogType.Kick, Now(), target.Name, DisplayName(player));
        bag.Write(player);
        _ctx.Store.Save(player);

        Reply(session, cmd, ClubPayload(club, player));
        Log.Info($"[session {session.Id}] Club_KickMember(10011) {args} → 踢出「{target.Name}」"
                 + $"（pos={target.Pos}），剩余成员 {club.Members.Count}；"
                 + "回完整社团信息 + 写日志 type=5；推 pf 1024×1");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 从 <c>{key: [roleid, …]}</c> 里读 id 列表。
    /// ★ <c>JsonNode["k"]</c> 对 <c>JsonArray</c> 会抛（AGENTS.md §5）⇒ 先判类型。
    /// </summary>
    private static List<ulong> ReadIdList(JsonNode? param, string key)
    {
        var result = new List<ulong>();
        if (param is not JsonObject o || o[key] is not JsonArray arr)
            return result;
        foreach (var el in arr)
        {
            ulong id = ClubJson.ReadULong(el);
            if (id != 0)
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// 自建社团时补的机器人团员数（不含社长自己）。
    ///
    /// ★ 必须 &lt; 1 级人数上限（<c>CountLmt</c> = 30），留足真人位。
    /// </summary>
    public const int NpcMemberCount = 5;

    /// <summary>
    /// 给新建的社团补一批**确定性**机器人团员。
    ///
    /// <para><b>为什么需要（第十五轮）</b></para>
    /// 自建社团原本只有社长一人、加入的 NPC 社团里只有「NPC 社长 + 我（普通成员）」
    /// ⇒ 两条路径下玩家都**没有可管理的下属**，
    /// 10009 改职位 / 10010 社长转让 / 10011 踢人 全部没有操作对象 ⇒ 功能形同虚设。
    ///
    /// <para><b>约定</b></para>
    /// <list type="bullet">
    ///   <item>种子取自 <c>roleid</c>（同 <see cref="FriendFeature"/> 的做法）⇒ 同一账号每次同一批人；</item>
    ///   <item>全部 <see cref="PosOrdinary"/>（普通成员）—— 副社长由玩家自己用 10009 提拔；</item>
    ///   <item><c>HeadFrameId = 0</c> / <c>ListSkinId = 0</c> 是**恒安全值**
    ///     （<see cref="ClubMemberState"/> 类注释：非 0 的非法 id 会让客户端无保护取 <c>.Icon</c> 抛）；</item>
    ///   <item><c>OffTimes</c> 避开 <c>0</c>（0 的语义是「2个月前」）与
    ///     <c>86400*3</c>/<c>86400*7</c> 两个边界（<c>strSecsTime</c> 用的是严格小于，
    ///     正好等于时所有分支都不命中 ⇒ 文案变空白）。</item>
    /// </list>
    /// </summary>
    internal static void AddNpcMembers(ClubState club, Player player, long now)
    {
        // 离线时长阶梯：避开 0 / 3天 / 7天 三个雷点
        int[] offLadder = { 900, 5400, 100000, 250000, 500000 };

        for (int i = 0; i < NpcMemberCount; i++)
        {
            ulong mixed = Mix(player.Roleid, 900 + i);
            // ★ roleid 必须 ≤ 0xFFFFFFFF（AGENTS.md 的 World.DeriveRoleId 值域约束）
            ulong roleid = (mixed % 0x7FFF_FFFFUL) + 1;
            if (roleid == player.Roleid || club.Member(roleid) is not null)
                roleid = (roleid % 0x7FFF_0000UL) + 0x1000 + (ulong)i;

            club.Members.Add(new ClubMemberState
            {
                Roleid = roleid,
                Name = NpcHeaderNames[(int)(mixed % (ulong)NpcHeaderNames.Length)],
                Level = 30 + (int)(mixed % 200),
                Pos = PosOrdinary,
                State = 0,                                   // 离线
                GirlIconId = (uint)(1 + (int)(mixed % 16)),
                HeadFrameId = 0,
                OffTimes = now - offLadder[i % offLadder.Length],
                Last7Contribute = (int)(mixed % 500),
                DailyContribute = 0,
                ListSkinId = 0,
                JoinDate = club.CreateTime,
            });
        }
    }

    /// <summary>日志用：上行参数的简短描述（<c>null</c> 要明确打出来，它是真机常态）。</summary>
    private static string Describe(JsonNode? param)
        => param is null ? "null（连 tbParam 键都没有）" : param.ToJsonString();

    /// <summary>日志用：长文本截断。</summary>
    private static string Preview(string s)
        => s.Length <= 24 ? s : s[..24] + "…";

    private static int ReadInt(JsonNode? node, int fallback) => ClubJson.ReadInt(node, fallback);

    private static string ReadString(JsonNode? node) => ClubJson.ReadString(node);
}
