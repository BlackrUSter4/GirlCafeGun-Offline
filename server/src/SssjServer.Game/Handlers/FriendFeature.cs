using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 好友域：<c>LuaCall</c> <b>80..90</b>（11 条）+ 同源的 <b>101</b> <c>RequestPlayerInfo</c>。
/// 完整取证见 <c>work/friend-domain-forensics.md</c>（1311 行 / 301 处 <c>文件:行号</c>），
/// 本注释只复述实现必须遵守的部分，每条都带取证。
///
/// <para><b>0. 整域零 protobuf，100% 走 pf 1022/1024 的 LuaCall JSON 通道</b></para>
/// <c>docs/proto-schema.json</c> 的 248 个消息里**一个 <c>FriendInfo</c> 都没有**
/// （<c>FriendInfo</c>/<c>FriendCardInfo</c> 只以注释形式存在于 <c>UI_Friend.lua:201..223</c>）。
/// 客户端半边是 <c>extracted/lua/Friend.lua</c>（238 行）＋ <c>UI_Friend.lua</c>；
/// 服务端半边（11 个 <c>FriendLogic:Friend_*</c>）不在包里，要自己写 —— 就是本文件。
///
/// <para><b>1. 错误约定：LuaCall 数字族，成功绝不带 <c>nError</c></b></para>
/// 好友域的 11 个 <c>else</c> 分支**没有一个读 <c>nError</c></c>
/// （<c>UI_Friend.lua:960..1011</c> 的 9 个 <c>OnFriend_*</c> 全是 <c>print</c>+<c>Lib_Dump</c>；
/// 80 是 <c>Friend.lua:77..106</c> 按 <c>reqfriendtype</c> 分流；90 是
/// <c>UI_IDAdd.lua:45..66</c> 只读 4 个字段）。⇒ <c>nError</c> 在本域**完全惰性**。
/// 但仍然坚持「成功不带 <c>nError</c>」：① 与全项目约定一致（<c>AGENTS.md</c> §5）；
/// ② 万一将来有人给这些 <c>On*</c> 加上标准判错分支，不会一起翻车。
/// 失败一律走独立通道 <c>S2C.CallClientTip</c>（<c>S2CCall.lua:95..101</c>，参数是**裸数组**）
/// 或 <c>S2C.CallClientError</c>（<c>:90..93</c>，<c>{"err":&lt;5 位码&gt;}</c>）。
///
/// <para><b>2. ★★ 六个「漏了就抛」的字段</b></para>
/// 取证文档 §0.1 用反汇编 + Lua 语义逐条确认过「少一个字段是崩还是只显示空白」：
/// <list type="table">
///   <item><term><c>level</c></term><description><c>"LV." .. tbInfo.level</c> —— 抛
///     （<c>UI_Friend.lua:299/373/442/503</c>、<c>UI_FriendInfo.lua:212/593</c>）</description></item>
///   <item><term><c>off_times</c></term><description><c>strSecsTime</c> 第一句
///     <c>GetTime() - tbInfo.off_times</c>，**在判 <c>state</c> 之前** —— 抛
///     （<c>UI_Friend.lua:225</c> 算术，<c>:226</c> 才判 state）</description></item>
///   <item><term><c>signinfo</c></term><description><c>Game.TextFilter.Instance:Filter(nil)</c>
///     抛 NRE（RVA <c>0x1823814</c>：<c>0x18238B8 cbnz x28</c> / <c>0x18238BC mov x0, xzr</c> /
///     <c>0x18238C0 bl #0x13F30AC</c> il2cpp 空引用桩）；空字符串 <c>""</c> 是合法值</description></item>
///   <item><term><c>headframeid</c></term><description><c>Borders[CardCommon:GetBorderID(id)].Icon</c>
///     无 nil 保护（<c>UI_Friend.lua:294/369/438/499</c>、<c>UI_FriendInfo.lua:166</c>、<c>UI_IDAdd.lua:62</c>）。
///     ★ <c>GetBorderID(nil)</c> 返回 **nil** 不是 0（<c>CardCommon.lua:862..867</c>
///     <c>if id == 0 then id = id + 1 end</c>，而 <c>nil == 0</c> 为假）⇒ 必须发合法 id。
///     本服固定发 <see cref="BotHeadFrameId"/> = 1（<c>HeadFrame.txt:4</c>，一定存在）</description></item>
///   <item><term>HMD 的 <c>flag</c></term><description><c>roleInfo.flag &amp; 0x1 &gt; 0</c>
///     位运算无保护（<c>UI_Friend.lua:150</c>、<c>Friend.lua:155/169</c>）—— 抛；
///     而且 <c>flag == 0</c> 的条目会被 <c>:150</c> **过滤掉、界面看不见**（静默丢数据）</description></item>
///   <item><term><c>cardlist[i].skilllist</c></term><description>
///     <c>for _, tbskill in pairs(friendCard.skilllist)</c>（<c>UI_FriendInfo.lua:567</c>）——
///     <c>pairs(nil)</c> 抛。空数组 <c>[]</c> 合法（<c>GameManager.lua:739</c> 就是 <c>skilllist = {}</c>）</description></item>
/// </list>
/// 其余字段（<c>rolename</c>/<c>cardnum</c>/<c>girliconid</c>/<c>comfortscore</c>…）漏了只是显示空白
/// （<c>Text.text = nil</c> 不抛，RVA <c>0x2A5800C</c> 无类型守卫；<c>Game.Girl.IconPath(nil)</c>
/// 不抛，RVA <c>0x2889330</c>），但仍然全发 —— 「显示空白」在验收时看起来同样像 bug。
///
/// <para><b>3. ★★ 两条「必须补发 cmd 80」的陷阱</b></para>
/// <list type="number">
///   <item><b>83 <c>ChangeFX</c></b>：<c>UI_Friend:OnFriend_ChangeFX</c>（<c>UI_Friend.lua:972..975</c>）
///     是空实现 ⇒ 只回 83 的话「换一批」按钮点了**永远没反应**。必须紧接着发
///     <c>sCmd=80 / reqfriendtype=2</c> + 新 <c>FXList</c>，才会走 <c>Friend.lua:84</c> 写入
///     <c>FriendLogic.FXList</c> → <c>UI_Friend.lua:935 ShowFriendList(2)</c> 重绘。</item>
///   <item><b>85 <c>Pass</c></b>：<c>UI_Friend.lua:688</c> 一键通过结尾是
///     <c>self:ShowFriendList(true)</c> —— 把 <c>curType</c> 写成了**布尔 true**
///     （<c>:122..123</c> <c>self.curType = curType or self.curType</c>）⇒ 四个
///     <c>objXXContent:SetActive(self.curType == FriendLogic.TypeXX)</c> 全 false
///     ⇒ **面板变全空白**。唯一能救回来的是服务端再回一条 <c>sCmd=80</c>
///     （<c>OnCMDFriend_ReqList</c> → <c>ShowFriendList(tbParam.reqfriendtype)</c> 把 curType 修回数字）。</item>
/// </list>
/// 另外 86/87/88/89 虽然客户端本地也会移除元素，但都有静默失败（<c>Friend.lua:185..194</c>
/// 的 89 只从 <b>FXList</b> 移除、从不往 HMDList 加；<c>UI_FriendInfo.lua:521</c> 的 88
/// 写错成 <c>SQList[k] = nil</c>）⇒ 本实现对 83/85/86/87/88/89 **一律补发**对应的 80。
///
/// <para><b>4. 电力（= 体力，货币 id 1，不是 id 13）</b></para>
/// <c>MoneyLogic.lua:11</c> <c>eMoneyType_Vigour = 1 --体力</c>；<c>MoneyDef.txt:4</c> 的
/// id 1 <c>ErrorCode=20014</c> 正是 <c>ui.Error.20014 = '电力不足'</c>。
/// ★ 命名陷阱：内部叫「体力/Vigour」，**界面文案一律显示「电力」**（<c>ui.Type_TiLi = '电力'</c>）；
/// <c>MoneyDef.txt:16</c> 的 id 13 才是另一条链（咖啡馆吧台 <c>LuaCall.ReceiveDianli</c>）的
/// 「电力（额外体力）」，好友域**不碰它**。
/// 每日收取上限 <see cref="MaxGainEnergyNum"/> = 10、计数存任务值
/// <c>(group 1, taskId 20/21)</c> —— 因为**客户端自己会算这个上限**
/// （<c>UI_Main.lua:9</c> <c>MaxGainEnergyNum = 10</c>、<c>:871..881 GetGainEnergyNum</c>
/// 读 <c>(1,20)</c> 记录日与 <c>(1,21)</c> 今日次数、<c>:883..896 ShowFriendNew</c>
/// 只在没到上限时才亮「有电力可收」红点）。这两个键在整个客户端里**只有读、零处写**
/// ⇒ 纯服务端维护，通道是 pf 1026 <see cref="GameNotify.PushTaskValues"/>。
///
/// <para><b>5. 机器人（假好友）</b></para>
/// 单机本地服没有第二个真玩家，四个页签必须靠机器人才有内容。做法照客户端自己造假数据的
/// <c>GameManager:ShowRobotInfo</c>（<c>GameManager.lua:711..751</c>）：6 位假 roleid
/// （<c>:714</c> <c>math.random(100000, 999999)</c>）、<c>signinfo = ""</c>（<c>:724</c>）、
/// <c>cardlist</c> 元素带 <c>skilllist = {}</c>（<c>:739</c>）、**没有 <c>visitingcarddata</c>**
/// ⇒ 资料页自动走 Panel 路径（<c>UI_FriendInfo.lua:132..135</c> 的 <c>skinId &gt; 0</c> 为假）。
/// 本实现的差别只有一处：**id 与全部属性都由账号哈希确定性派生**（<see cref="SeedOf"/>），
/// 不用随机数 —— 否则同一账号每次登录看到的是不同的人，客户端 PlayerPrefs 缓存会错乱。
///
/// ★ <c>visitedList</c>（咖啡馆访客）本轮**保持空**：<c>UI_CafeVisitMain.lua:411..415</c>
/// 是 Friend/Visitor 两个页签**共用的尾部**（在 <c>:371..393</c> 的 if/elseif 之外），
/// 它读 <c>data.headframeid</c>，而访客元素是 C# <c>NCafeVisitedInfo</c>
/// （<c>dump.cs:530356..530361</c> 只有 <c>roleId/name/time/girlId/modelId/comfortValue</c>
/// 六个字段）⇒ <c>tbHeadConfig[nil]</c> = nil ⇒ <c>:413 headCfg.Icon</c> 抛。
/// 好友列表元素带上 <c>comfortscore</c>/<c>cafeflag</c> 就够让「咖啡馆访问-我的好友」页签有内容，
/// 且没有这个风险（取证文档 §7.2b）。
///
/// <para><b>6. 存档</b></para>
/// 全部状态在 <c>Player.Extra["friend"]</c>（随 <c>PlayerStore.PlayerDto.Extra</c> round-trip），
/// 形状见 <see cref="ReadState"/>。机器人档案**不落盘**，只存一个 <c>seed</c> 后按需重算。
/// </summary>
public sealed class FriendFeature : IGameFeature
{
    // ================================================================ LuaCall id
    // 取证：extracted/lua/LuaCall.lua:79..89（id 定义）、:1321..1454（注册体）

    /// <summary>80 <c>Friend_ReqList</c>：请求各种好友列表（<c>LuaCall.lua:79</c>，注册体 <c>:1323</c>）。</summary>
    public const int CmdReqList = 80;

    /// <summary>81 <c>Friend_RecvEnergy</c>：收取电力（+ 自动回赠，<c>:80</c> / <c>:1331</c>）。</summary>
    public const int CmdRecvEnergy = 81;

    /// <summary>82 <c>Friend_SendEnergy</c>：赠送电力（<c>:81</c> / <c>:1343</c>）。</summary>
    public const int CmdSendEnergy = 82;

    /// <summary>83 <c>Friend_ChangeFX</c>：换一批推送好友（<c>:82</c> / <c>:1355</c>）。</summary>
    public const int CmdChangeFx = 83;

    /// <summary>84 <c>Friend_Add</c>：添加好友（<c>:83</c> / <c>:1368</c>）。</summary>
    public const int CmdAdd = 84;

    /// <summary>85 <c>Friend_Pass</c>：通过好友申请（<c>:84</c> / <c>:1381</c>）。</summary>
    public const int CmdPass = 85;

    /// <summary>86 <c>Friend_Ignore</c>：忽略好友申请（<c>:85</c> / <c>:1393</c>）。</summary>
    public const int CmdIgnore = 86;

    /// <summary>87 <c>Friend_Del</c>：删除好友（<c>:86</c> / <c>:1406</c>）。</summary>
    public const int CmdDel = 87;

    /// <summary>88 <c>Friend_HMDDel</c>：解除黑名单（<c>:87</c> / <c>:1418</c>）。</summary>
    public const int CmdHmdDel = 88;

    /// <summary>89 <c>Friend_HMDAdd</c>：加入黑名单（<c>:88</c> / <c>:1430</c>）。</summary>
    public const int CmdHmdAdd = 89;

    /// <summary>90 <c>Friend_IDAddInfo</c>：编号添加时取个人信息（<c>:89</c> / <c>:1442</c>）。</summary>
    public const int CmdIdAddInfo = 90;

    /// <summary>
    /// 101 <c>RequestPlayerInfo</c>：查看个人信息（<c>LuaCall.lua:101</c> / 注册体 <c>:1487</c>）。
    /// ★ 与 90 **共用同一个服务端函数** <c>FriendLogic:GenRoleInfoFrOffLineData(tbParam.roleid)</c>
    /// ⇒ 同一套回包字段，只有「查不到人」时的表现不同（见 <see cref="OnRoleInfo"/>）。
    /// </summary>
    public const int CmdRequestPlayerInfo = 101;

    // ================================================================ 好友列表类型
    // 取证：extracted/lua/Friend.lua:11..15（FriendLogic.TypeXX），与 LuaCall.lua:1322 注释一致。
    // ★ 没有 5。

    /// <summary>好友（<c>FriendLogic.TypeHY = 1</c>，<c>Friend.lua:11</c>）。</summary>
    public const int TypeHY = 1;

    /// <summary>发现/推荐（<c>TypeFX = 2</c>，<c>Friend.lua:12</c>）。</summary>
    public const int TypeFX = 2;

    /// <summary>申请（<c>TypeSQ = 3</c>，<c>Friend.lua:13</c>）。</summary>
    public const int TypeSQ = 3;

    /// <summary>黑名单/屏蔽（<c>TypeHMD = 4</c>，<c>Friend.lua:14</c>）。</summary>
    public const int TypeHMD = 4;

    /// <summary>绑定活动的活跃玩家列表（<c>TypeBindList = 6</c>，<c>Friend.lua:15</c>）。</summary>
    public const int TypeBindList = 6;

    // ================================================================ 黑名单位域
    // 取证：Friend.lua:155（IsBlack: v.flag & 0x1）、:169（IsBeBlack: v.flag & 0x2）

    /// <summary>bit0 = 我拉黑了他。★ 只有这一位为 1 的条目才会被 <c>UI_Friend.lua:150</c> 显示出来。</summary>
    public const int HmdFlagIBlockedHim = 0x1;

    /// <summary>bit1 = 他拉黑了我（本地服没有对端 ⇒ 永不设置）。</summary>
    public const int HmdFlagHeBlockedMe = 0x2;

    // ================================================================ 电力

    /// <summary>
    /// 每日收取电力次数上限 = 10。★ 客户端硬编码 <c>UI_Main.lua:9</c>
    /// <c>local MaxGainEnergyNum = 10;</c>，<c>extracted/setting/</c> 里**没有**对应配置列
    /// （取证文档 §5.2 已 grep 全表），所以服务端也只能写常量。
    /// </summary>
    public const int MaxGainEnergyNum = 10;

    /// <summary>
    /// 单次收取/赠送的电力数量。**未取证**：客户端唯一线索是
    /// <c>ui.UI_Friend.GetEnergyValue = '成功收取%d电力'</c>（有 <c>%d</c> ⇒ 数量可变），
    /// 配置表全表 grep 无「好友电力」列（取证文档 §10.1「没找到的」）。取 1（10 次/日 = 10 点）。
    /// </summary>
    public const int EnergyPerRecv = 1;

    /// <summary>「今日收取记录日」的任务值坐标（<c>UI_Main.lua:874</c> <c>me:GetTaskValue(1, 20)</c>）。</summary>
    public const int EnergyTaskGroup = 1;

    /// <summary>记录日（<c>GetLocalDay4AM()</c> 的日序）。</summary>
    public const int EnergyDayTaskId = 20;

    /// <summary>今日已收取次数（<c>UI_Main.lua:877</c> <c>me:GetTaskValue(1, 21)</c>）。</summary>
    public const int EnergyCountTaskId = 21;

    // ================================================================ 机器人

    /// <summary>
    /// 机器人池大小 = 16。
    ///
    /// <para><b>为什么是 16 而不是「界面上看到的那几个」</b></para>
    /// 界面一次最多只看到 <see cref="InitialFriendCount"/>=4（好友）/
    /// <see cref="FxPageSize"/>=4（发现）/ <see cref="InitialRequestCount"/>=3（申请）个，
    /// 与取证文档 §8.2-5 的建议（HY 3~5 / SQ 2~3 / FX 6~10 / HMD 0 起步）同量级。
    /// 池要比可见数大，是 83 <c>ChangeFX</c> 的**硬约束**：
    /// 「发现」页是从空闲池里取一个长度 <c>p</c> 的环形窗口，换一批把游标前移 <c>p</c>，
    /// 前后两个窗口**互不重叠的充要条件是空闲池 <c>n ≥ 2p</c>**。
    /// 这里 <c>n = 16 - 4 - 3 = 9 ≥ 2×4</c> ⇒ 每次「换一批」都是**完全新的一批**，
    /// 而且游标要走 9 步才回到原点（不是在两批之间来回跳）。
    /// 池太小的后果是「换一批点了列表几乎不变」——
    /// 而客户端 <c>table.sort</c>（<c>Friend.lua:85</c>）还会把顺序抹平，
    /// 陷阱 11 在界面上就完全验不出来。
    /// </summary>
    public const int BotPoolSize = 16;

    /// <summary>初始好友数（HY）。</summary>
    public const int InitialFriendCount = 4;

    /// <summary>初始收到的申请数（SQ）。</summary>
    public const int InitialRequestCount = 3;

    /// <summary>
    /// 「发现」页一次展示几个（从空闲池里按游标取环形窗口，83 换一批时游标前移一页）。
    /// 与 <see cref="BotPoolSize"/> 的关系见那里的注释：空闲池必须 ≥ 2 倍页长。
    /// </summary>
    public const int FxPageSize = 4;

    /// <summary>
    /// 机器人 roleid 区间下界。与客户端自己造机器人 id 的口径一致
    /// （<c>GameManager.lua:714</c> <c>math.random(100000, 999999)</c>），
    /// 且真玩家 roleid 来自 <see cref="World.DeriveRoleId"/>（FNV-1a % (2^53-1) + 1，
    /// <c>World.cs:159..171</c>）—— 数值极大，几乎不可能撞进这 6 位区间。
    /// </summary>
    public const ulong BotRoleIdMin = 100000;

    /// <summary>机器人 roleid 区间容量（100000..999999）。</summary>
    public const ulong BotRoleIdSpan = 900000;

    /// <summary>
    /// 机器人头像框 id 固定 1。<c>HeadFrame.txt:4</c> 就是 ID=1 / <c>Icon=Item/icon_border00.png</c>，
    /// <c>tbHeadConfig[1]</c> 一定存在。★ 合法集合是 <c>1..15, 20..25, 30..59, 61..69, 101..150</c>
    /// （110 个），**会抛的取值是 <c>nil</c>、16..19、26..29、60、70..100、151+**（0 安全，
    /// 因为 <c>GetBorderID(0) = 1</c>）。固定 1 是最省心的合法值。
    /// </summary>
    public const int BotHeadFrameId = 1;

    /// <summary>
    /// 机器人头像少女 id 的取值上界：<c>1..16</c>。
    /// <c>GirlInfo.txt</c> 有 46 个 GirlId（<c>1..16, 101..124, 127, 201..204, 999</c>），
    /// 但**只有 27 个有 <c>Npc/icon_n_girlNN.png</c> 图**（<c>1..16, 103, 113, 123, 201..204</c>）；
    /// 其余会显示 <c>Num/what.png</c> 问号（不崩，但看起来像 bug）。主线 1..16 全都有图。
    /// ★ 另外 <c>UI_BindEvent.lua:93</c> 是字符串拼接 <c>'Npc/icon_n_girl' .. id .. '.png'</c>
    /// （nil 会抛），所以这个字段一定要给。
    /// </summary>
    public const int BotGirlIconMax = 16;

    /// <summary>机器人咖啡馆已解锁（<c>cafeflag</c> ≥ 1）。★ 0 或缺失会让他从
    /// 「咖啡馆访问-我的好友」列表里静默消失（<c>UI_CafeVisitMain.lua:234</c>
    /// <c>if v.cafeflag and v.cafeflag &gt; CafeFlag.Locked</c>，<c>CafeFlag.Locked = 0</c>）。</summary>
    public const int BotCafeFlag = 1;

    /// <summary>机器人展示的角色卡张数（资料页有 3 个卡槽，<c>UI_FriendInfo.lua:191</c>）。</summary>
    public const int BotCardCount = 3;

    /// <summary>
    /// 机器人「离线多久」的候选偏移（秒）。
    /// ★ 必须避开 <c>86400*3 = 259200</c> 与 <c>86400*7 = 604800</c> 这两个整点：
    /// <c>strSecsTime</c>（<c>UI_Friend.lua:224..242</c>）的边界用的是 <c>&lt;</c> 而不是 <c>&lt;=</c>
    /// （<c>:235</c> <c>86400*3 &lt; secs and secs &lt; 86400*7</c>、<c>:237</c> 同构），
    /// 正好等于这两个值时**所有分支都不命中 ⇒ 函数返回 nil ⇒ <c>txtOffTime.text = nil</c>**
    /// （不抛，但文案变空白）。下面 8 个值分别落在「一小时内 / 一小时前 / 一天前 / 三天前 / 一周前」。
    /// </summary>
    internal static readonly int[] BotOffSecondsLadder =
    {
        900,        // <=3600      → ui.UI_Friend.1Hour    一小时内
        5400,       // 3600..86400 → 1HourAgo              一小时前
        43200,      // 同上
        100000,     // 86400..3d   → 1DayAgo               一天前
        250000,     // 同上（259200 是雷，避开）
        500000,     // 3d..7d      → 3DayAgo               三天前
        700000,     // >7d         → 7DayAgo               一周前（604800 是雷，避开）
        1000000,    // 同上
    };

    /// <summary>
    /// 机器人昵称池（16 个，与 <see cref="BotPoolSize"/> 等长 ⇒ 同一账号内不重名）。
    /// 客户端对 <c>rolename</c> 没有任何格式要求（<c>Text.text = nil</c> 都不抛，RVA <c>0x2A5800C</c>），
    /// 唯一的语义约束是**不能等于 <c>"Not Active"</c>** —— 那是「该 id 无此玩家」的哨兵值
    /// （<c>UI_Friend.lua:1049</c> <c>if tbInfo.rolename == "Not Active" and tbInfo.level == 1</c>、
    /// <c>UI_FriendInfo.lua:585</c> 同构、服务端侧 <c>LuaCall.lua:1445</c>）。
    /// </summary>
    internal static readonly string[] BotNames =
    {
        "小南", "阿澈", "柚子茶", "月见草", "星野三号", "不加糖", "晚安喵", "雨宫栞",
        "白露未干", "苏打水", "鹿野院", "夜航船", "橘子汽水", "半糖去冰", "一颗奇异果", "岛风号",
    };

    /// <summary>
    /// 机器人个性签名池。第一个是**空字符串** —— 这是合法值（<c>GameManager.lua:724</c>
    /// <c>tbParam.signinfo = ""</c>），此时 <c>UI_Friend.lua:276</c>
    /// <c>objSign:SetActive(tbInfo.signinfo and tbInfo.signinfo ~= "")</c> 会把签名行隐藏，
    /// 资料页则显示 <c>ui.UI_Friend.SignNone = '这个人很懒，什么都没有留下'</c>
    /// （<c>UI_FriendInfo.lua:171</c>）。★ 但**绝不能是 nil**（<c>TextFilter.Filter(nil)</c> 抛 NRE）。
    /// </summary>
    internal static readonly string[] BotSigns =
    {
        "",
        "今天也要元气满满！",
        "咖啡馆常客，欢迎来喝一杯",
        "求带飞，谢谢店长",
        "夜猫子，凌晨在线",
        "路过，留个爪",
    };

    // ================================================================ Extra / tip key

    /// <summary><see cref="Player.Extra"/> 里本域的 key（与 "maincity"/"cafe" 并列）。</summary>
    public const string ExtraKey = "friend";

    /// <summary>好友 roleid 数组。</summary>
    internal const string NodeHy = "hy";

    /// <summary>收到的申请 roleid 数组。</summary>
    internal const string NodeSq = "sq";

    /// <summary>黑名单：<c>[{"roleid":n,"flag":1}, …]</c>。</summary>
    internal const string NodeHmd = "hmd";

    /// <summary>电力状态：<c>{"&lt;roleid&gt;":{"take":日序,"send":日序}}</c>。</summary>
    internal const string NodeEnergy = "energy";

    /// <summary>机器人池的种子（落盘以保证跨进程稳定）。</summary>
    internal const string NodeSeed = "seed";

    /// <summary>「发现」页轮换游标（83 换一批时前移 <see cref="FxPageSize"/>）。</summary>
    internal const string NodeFxCursor = "fxcursor";

    /// <summary><c>S2C.CallClientTip</c>：<c>ShowMsgByKey(table.unpack(tbParam))</c>，参数是**裸数组**
    /// （<c>S2CCall.lua:95..101</c>，上行侧封装 <c>LuaCall.lua:577..583</c>）。</summary>
    public const string FnCallClientTip = "CallClientTip";

    /// <summary><c>S2C.CallClientError</c>：<c>ShowMsgByKey("ui.Error." .. err)</c>（<c>S2CCall.lua:90..93</c>）。</summary>
    public const string FnCallClientError = "CallClientError";

    // 好友域可用的提示 key（取证文档 §3.4：work/lang.json 里 9 条客户端零引用的
    // ui.UI_Friend.* 只可能由原服务端通过 SendClientTip 发出，那就是原服每条命令的提示清单）

    /// <summary>84 成功（对方已申请过我 ⇒ 直接成好友）/ 85 成功：<c>好友添加成功</c>。</summary>
    public const string TipAddFriendOk = "ui.UI_Friend.AddFriendOK";

    /// <summary>84/85 失败：<c>好友数量已达上限</c>（客户端 <c>UI_Friend.lua:673/869</c> 也会自己发）。</summary>
    public const string TipLimit = "ui.UI_Friend.Limit";

    /// <summary>83 成功：<c>刷新成功</c>。</summary>
    public const string TipChangeFx = "ui.UI_Friend.ChangeFX";

    /// <summary>81 成功（带数量）：<c>成功收取%d电力</c> ⇒ 必须带格式参数。</summary>
    public const string TipGetEnergyValue = "ui.UI_Friend.GetEnergyValue";

    /// <summary>81 成功（带数量且有自动回赠）：<c>成功收取%d电力，已自动回赠</c>。</summary>
    public const string TipGetEnergyValueFeedback = "ui.UI_Friend.GetEnergyValueFeedback";

    /// <summary>81 失败：<c>今日电力收取已达上限</c>（★ 这条文案直接证明「收取有每日上限」）。</summary>
    public const string TipGetEnergyLimited = "ui.UI_Friend.GetEnergyLimited";

    /// <summary>82 成功：<c>电力赠送成功</c>。</summary>
    public const string TipSendEnergy = "ui.UI_Friend.SendEnergy";

    /// <summary>89 成功：<c>已拉入黑名单</c>。</summary>
    public const string TipAddHmd = "ui.UI_Friend.AddHMDTips";

    /// <summary>89 失败：<c>不能把自己添加到黑名单</c>（服务端必须做这条校验）。</summary>
    public const string TipRejectAddSelfHmd = "ui.UI_Friend.RejectAddSelfHMD";

    /// <summary>90 失败：<c>该编号不存在噢！</c>（<c>LuaCall.lua:1446</c> 的原服做法）。</summary>
    public const string TipNotExitRole = "ui.UI_Friend.NotExitRole";

    /// <summary>
    /// <c>ui.Error.20027 = '拥有电力已达上限，无法继续获得'</c>。
    /// ★ 有意不用 20026（<c>拥有电力已达上限，本次获得%d电力</c>）：<c>CallClientError</c> 只传码、
    /// 不传格式参数，<c>Text(sKey)</c>（<c>Lib.lua:383..389</c>）在没有 v1 时**跳过 string.format**
    /// ⇒ 界面会原样显示 <c>%d</c>。
    /// </summary>
    public const int ErrVigourFull = 20027;

    /// <summary>
    /// 「该 roleid 查不到玩家」的哨兵昵称（<c>LuaCall.lua:1445</c>
    /// <c>if tbRet.rolename == "Not Active" then</c>；客户端 <c>UI_Friend.lua:1049</c> /
    /// <c>UI_FriendInfo.lua:585</c> 用 <c>rolename == "Not Active" and level == 1</c> 画灰头像）。
    /// </summary>
    public const string NotActiveName = "Not Active";

    private GameFeatureContext _ctx = null!;
    private FriendTable _friends = FriendTable.Empty;
    private CharacterCardTable _cards = CharacterCardTable.Empty;

    public string Name => "好友域（LuaCall 80..90 + 101 个人信息，含确定性机器人好友）";

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        CmdReqList, CmdRecvEnergy, CmdSendEnergy, CmdChangeFx, CmdAdd, CmdPass,
        CmdIgnore, CmdDel, CmdHmdDel, CmdHmdAdd, CmdIdAddInfo, CmdRequestPlayerInfo,
    };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _friends = FriendTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _cards = ctx.CharacterCards;

        ctx.MapLuaCall(CmdReqList, OnReqList);
        ctx.MapLuaCall(CmdRecvEnergy, OnRecvEnergy);
        ctx.MapLuaCall(CmdSendEnergy, OnSendEnergy);
        ctx.MapLuaCall(CmdChangeFx, OnChangeFx);
        ctx.MapLuaCall(CmdAdd, OnAdd);
        ctx.MapLuaCall(CmdPass, OnPass);
        ctx.MapLuaCall(CmdIgnore, OnIgnore);
        ctx.MapLuaCall(CmdDel, OnDel);
        ctx.MapLuaCall(CmdHmdDel, OnHmdDel);
        ctx.MapLuaCall(CmdHmdAdd, OnHmdAdd);
        ctx.MapLuaCall(CmdIdAddInfo, OnRoleInfo);
        ctx.MapLuaCall(CmdRequestPlayerInfo, OnRoleInfo);

        // 机器人池的初始化放 OnPlayerPrepare（pf 1005 之前）：纯数据、不发包，
        // 客户端进主城时自己会发 80（UI_Main.lua:727/728），不需要服务端主动推。
        ctx.OnPlayerPrepare(OnPrepare);
    }

    private void OnPrepare(ClientSession session, Player player)
    {
        var state = LoadState(player);
        var bots = Bots(state.Seed);
        Log.Info($"[session {session.Id}] 好友域就绪：机器人池 {bots.Count} 个（种子 {state.Seed}）、" +
                 $"好友 {state.Hy.Count}/{_friends.NumLimit(player.Level)}（LV.{player.Level}）、" +
                 $"申请 {state.Sq.Count}、黑名单 {state.Hmd.Count}、" +
                 $"今日已收电力 {GainedToday(player, DayIndex(DateTimeOffset.UtcNow))}/{MaxGainEnergyNum}");
    }

    // ================================================================ 80 Friend_ReqList

    /// <summary>
    /// cmd 80 请求好友列表。上行只有一个键 <c>{"reqfriendtype":1}</c>（**没有 roleid**）。
    ///
    /// 服务端分支 <c>LuaCall.lua:1323..1329</c>：<c>FriendLogic:Friend_ReqList(tbParam)</c> ——
    /// 注意这里**没有** <c>LuaCall:CallClient</c>，回包在原服 <c>FriendLogic</c> 内部自己发。
    /// 客户端分支 <c>FriendLogic:OnFriend_ReqList</c>（<c>Friend.lua:77..106</c>）是 5 分支 if/elseif：
    /// <code>
    /// Friend.lua:80  tbParam.reqfriendtype == TypeHY  → FriendLogic.HYList  = tbParam.HYList  or {}
    /// Friend.lua:84                        == TypeFX  → FXList  = tbParam.FXList  or {}
    /// Friend.lua:87                        == TypeSQ  → SQList  = tbParam.SQList  or {}
    /// Friend.lua:90                        == TypeHMD → HMDList = tbParam.HMDList or {}
    /// Friend.lua:93                        == 6       → BindList= tbParam.BindList or {}
    /// Friend.lua:82/85/88/91/94  table.sort(list, _roleSortFunc)
    /// </code>
    /// ⇒ <c>reqfriendtype</c> **必须 echo**，缺了 5 个分支全不命中、列表永不更新（静默失败，陷阱 10）。
    /// ⇒ 一次只有一个槽会被写入，所以只需填「请求的那一类」；其余 4 个 key 仍然照原服形状发空数组
    /// （<c>Friend.lua:49..58</c> 的数据格式注释里 5 个 List 都列了）。
    ///
    /// <c>_roleSortFunc</c>（<c>Friend.lua:36..45</c>）比较 <c>state</c> → <c>level</c>（在线时）
    /// → <c>off_times</c>（离线时）⇒ **列表 ≥ 2 个元素时这三个字段必须都在**（<c>nil &gt; nil</c> 抛）。
    /// 服务端这边也按同一口径预排一次，方便日志核对（客户端仍会自己再排一遍）。
    ///
    /// <b>type 6（BindList）本轮一律回空数组</b>：<c>UI_BindEvent.lua:93</c> 用字符串拼接
    /// <c>'Npc/icon_n_girl' .. tbInfo.girliconid .. '.png'</c>（缺字段抛）、<c>:126</c>
    /// <c>math.min(tbInfo.bindpoint, x)</c>（缺字段抛），而绑定活动本身没实现（取证文档陷阱 22）。
    /// </summary>
    internal Task OnReqList(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Friend_ReqList(80) 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        int type = ReadInt(param, "reqfriendtype", 0);
        if (type == 0)
        {
            // 实测客户端只发 1/2/3/4（四个页签，work/probe-three-domains.md §1）与 6（UI_BindEvent.lua:78）
            Log.Warn($"[session {session.Id}] Friend_ReqList(80) 缺 reqfriendtype，" +
                     $"param={param?.ToJsonString() ?? "nil"} → 照 echo 0 回，5 个分支都不命中");
        }

        ReplyList(session, player, type, cmd);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 组好并发送一条 cmd 80。<paramref name="replyCmd"/> 恒为 80 ——
    /// 单独提出来是因为 83/85/86/87/88/89 之后要**主动补发**同一条（见类注释 §3）。
    /// </summary>
    private void ReplyList(ClientSession session, Player player, int type, int replyCmd = CmdReqList)
    {
        var state = LoadState(player);
        var entries = BuildEntries(player, state, type, DateTimeOffset.UtcNow);
        var payload = BuildFriendListPayload(type, player.Roleid, entries);

        Log.Info($"[session {session.Id}] Friend_ReqList({replyCmd}) reqfriendtype={type}" +
                 $"（{TypeName(type)}）→ {ListKeyOf(type) ?? "无对应列表"} {entries?.Count ?? 0} 项" +
                 $"；好友 {state.Hy.Count}/{_friends.NumLimit(player.Level)}、申请 {state.Sq.Count}、" +
                 $"黑名单 {state.Hmd.Count}、今日已收电力 " +
                 $"{GainedToday(player, DayIndex(DateTimeOffset.UtcNow))}/{MaxGainEnergyNum}");

        _ctx.LuaCalls.CallClient(session, replyCmd, payload);
    }

    /// <summary>
    /// cmd 80 的回包：echo <c>reqfriendtype</c> + <c>roleid</c> + 5 个列表 key，
    /// 只有 <paramref name="reqFriendType"/> 对应的那一个填 <paramref name="entries"/>。
    /// 纯函数（无 session/IO），便于单测精确校验字段名与类型。
    /// </summary>
    internal static JsonObject BuildFriendListPayload(int reqFriendType, ulong roleid, JsonArray? entries)
    {
        var payload = new JsonObject
        {
            // Friend.lua:80 —— 分流依据，必须 echo
            ["reqfriendtype"] = reqFriendType,
            // Friend.lua:53 的数据格式注释里列出的字段；客户端不解引用，仅与原服形状一致
            ["roleid"] = roleid,
            // Friend.lua:81/84/87/90/93 —— 五种列表
            ["HYList"] = new JsonArray(),
            ["FXList"] = new JsonArray(),
            ["SQList"] = new JsonArray(),
            ["HMDList"] = new JsonArray(),
            ["BindList"] = new JsonArray(),
        };

        string? key = ListKeyOf(reqFriendType);
        if (key is not null && entries is not null)
            payload[key] = entries;
        return payload;
    }

    /// <summary>类型 → 回包里的列表 key（<c>Friend.lua:81/84/87/90/93</c>）；未知类型返回 null。</summary>
    internal static string? ListKeyOf(int type) => type switch
    {
        TypeHY => "HYList",
        TypeFX => "FXList",
        TypeSQ => "SQList",
        TypeHMD => "HMDList",
        TypeBindList => "BindList",
        _ => null,
    };

    /// <summary>好友列表类型的中文名（仅日志用）。取证 <c>Friend.lua:11..15</c> 与 <c>LuaCall.lua:1322</c>。</summary>
    internal static string TypeName(int type) => type switch
    {
        TypeHY => "好友",
        TypeFX => "发现",
        TypeSQ => "申请",
        TypeHMD => "黑名单",
        TypeBindList => "绑定活动",
        _ => "未知",
    };

    /// <summary>
    /// 按类型组一份 <c>FriendInfo[]</c>。
    /// type 6（BindList）与未知类型一律返回空数组（理由见 <see cref="OnReqList"/>）。
    /// </summary>
    internal JsonArray? BuildEntries(Player player, FriendState state, int type, DateTimeOffset now)
    {
        if (ListKeyOf(type) is null)
            return null;
        if (type == TypeBindList)
            return new JsonArray();

        var bots = Bots(state.Seed);
        var arr = new JsonArray();
        long unix = now.ToUnixTimeSeconds();
        int today = DayIndex(now);
        int gained = GainedToday(player, today);

        switch (type)
        {
            case TypeHY:
                foreach (var bot in Order(Resolve(bots, state.Hy), unix))
                {
                    var mark = state.MarkOf(bot.Roleid);
                    // 取证文档 §5.3：canrecv = 他今天送了我 && 我今天还没收他的 && 今日总次数未满。
                    // 「他今天送了我」对机器人恒真（本地服没有对端，约定机器人每天都送）。
                    bool canRecv = mark.TakeDay != today && gained < MaxGainEnergyNum;
                    bool canSend = mark.SendDay != today;
                    arr.Add(BuildFriendInfo(bot, unix, canRecv, canSend, null));
                }
                break;

            case TypeSQ:
                foreach (var bot in Order(Resolve(bots, state.Sq), unix))
                    arr.Add(BuildFriendInfo(bot, unix, false, false, null));
                break;

            case TypeFX:
                foreach (var bot in Order(FxPage(bots, state), unix))
                    arr.Add(BuildFriendInfo(bot, unix, false, false, null));
                break;

            case TypeHMD:
                foreach (var entry in state.Hmd)
                {
                    var bot = bots.FirstOrDefault(b => b.Roleid == entry.Roleid);
                    if (bot is null)
                        continue;
                    // ★ flag 是硬必填且是位域：UI_Friend.lua:150 `roleInfo.flag & 0x1 > 0` 无 nil 保护，
                    //   而且 flag=0 的条目会被那一行过滤掉、界面完全看不见（静默丢数据）。
                    arr.Add(BuildFriendInfo(bot, unix, false, false, entry.Flag));
                }
                break;
        }
        return arr;
    }

    /// <summary>
    /// 一条 <c>FriendInfo</c>。字段取舍逐条对着取证文档 §2.1 的表：
    /// <list type="bullet">
    ///   <item><b>硬必填</b>（漏了抛）：<c>level</c> / <c>off_times</c> / <c>signinfo</c> /
    ///     <c>headframeid</c> / HMD 的 <c>flag</c> / <c>cardlist[i].skilllist</c>；</item>
    ///   <item><b>发但不致命</b>：<c>roleid</c>（漏了功能全废：增删改上行会发出 nil 目标）、
    ///     <c>rolename</c> / <c>state</c> / <c>girliconid</c> / <c>cardnum</c> / <c>cardlist</c>；</item>
    ///   <item><b>服务端必须算好的布尔</b>：<c>canrecv</c> / <c>cansend</c> —— 客户端从不计算它们
    ///     （全仓 26 处引用全是「读」或点击后本地置 false），漏了「一键收取/一键赠送」永久灰
    ///     （<c>ResetGetEnergyAllBtn</c> <c>UI_Friend.lua:523..534</c> 全表扫这两个字段）；</item>
    ///   <item><b>咖啡馆访问界面要</b>：<c>comfortscore</c>（<c>UI_CafeVisitMain.lua:252/375/409</c>）、
    ///     <c>cafeflag</c>（<c>:234</c>，缺了或 0 就从「我的好友」列表静默消失）；</item>
    ///   <item><b>有意不发</b>：<c>visitingcarddata</c>（陷阱 7：<c>playerlistskinid &gt; 0</c> 但不在
    ///     <c>Board.txt</c> 里就 <c>.bg</c> 抛；整个不发 ⇒ <c>UI_Friend.lua:247</c> 的
    ///     <c>if tbInfo.visitingcarddata and …</c> 直接走 else，且 <c>UI_FriendInfo</c> 走 Panel 路径）、
    ///     <c>entrustendtime</c>（委托任务子系统未实现，<c>UI_CafeVisitMain.lua:379</c> 有 nil 保护）、
    ///     <c>sid</c>/<c>aid</c>/<c>recvenergydate</c>/<c>takeenergydate</c>/<c>sendenergydate</c>/
    ///     <c>RspType</c>（全仓**无消费点**，grep 只命中注释本身）。</item>
    /// </list>
    /// </summary>
    /// <param name="flag">仅黑名单（type 4）需要；其余类型传 null 表示不发这个字段。</param>
    internal static JsonObject BuildFriendInfo(FriendBot bot, long nowUnix, bool canRecv, bool canSend, int? flag)
    {
        var cardlist = new JsonArray();
        foreach (var c in bot.Cards)
        {
            cardlist.Add(new JsonObject
            {
                // 字段号/名字取证：UI_Friend.lua:201..207 的原始 proto 注释；
                // 消费点 UI_FriendInfo.lua:315 Game.ItemMgr.CreateItem(g,d,p,l)
                ["g"] = c.G,
                ["d"] = c.D,
                ["p"] = c.P,
                ["l"] = c.L,
                ["enhancelevel"] = c.EnhanceLevel,   // UI_FriendInfo.lua:559
                ["breaklevel"] = c.BreakLevel,       // :560
                // ★★ 必须是数组：UI_FriendInfo.lua:567 `for _, tbskill in pairs(friendCard.skilllist)`
                //    —— pairs(nil) 抛「bad argument #1 to 'pairs'」。空数组合法（GameManager.lua:739）。
                ["skilllist"] = new JsonArray(),
            });
        }

        var info = new JsonObject
        {
            ["roleid"] = bot.Roleid,
            ["rolename"] = bot.RoleName,
            ["level"] = bot.Level,                          // ★ 硬必填（"LV." .. nil 抛）
            ["state"] = bot.State,                          // 0 离线 / 1 在线
            ["off_times"] = nowUnix - bot.OffSeconds,       // ★ 硬必填（算术，在判 state 之前）
            ["girliconid"] = bot.GirlIconId,
            ["headframeid"] = bot.HeadFrameId,              // ★ 硬必填且必须命中 HeadFrame.txt
            ["signinfo"] = bot.SignInfo,                    // ★ 硬必填（Filter(nil) 抛 NRE），"" 合法
            ["canrecv"] = canRecv,
            ["cansend"] = canSend,
            ["cardnum"] = bot.CardNum,
            ["comfortscore"] = bot.ComfortScore,
            ["cafeflag"] = BotCafeFlag,
            ["cardlist"] = cardlist,
        };
        if (flag is not null)
            info["flag"] = flag.Value;
        return info;
    }

    /// <summary>
    /// 按客户端 <c>_roleSortFunc</c>（<c>Friend.lua:36..45</c>）的口径排序：
    /// 在线优先 → 在线之间比等级降序 → 离线之间比 <c>off_times</c> 降序（= 最近离线的在前）。
    /// 客户端自己也会 <c>table.sort</c> 一遍，这里排是为了让日志与界面顺序一致、便于核对。
    /// </summary>
    private static IEnumerable<FriendBot> Order(IEnumerable<FriendBot> bots, long nowUnix)
        => bots.OrderByDescending(b => b.State == 1)
               .ThenByDescending(b => b.State == 1 ? b.Level : 0)
               .ThenBy(b => b.State == 1 ? 0 : b.OffSeconds);

    // ================================================================ 81 Friend_RecvEnergy

    /// <summary>
    /// cmd 81 收取电力（**一个包干两件事**：收取 + 自动回赠）。上行
    /// <c>{roleid:&lt;me.id&gt;, recvenergylist:[…], sendenergylist:[…], log_gettype:1|2}</c>
    /// （<c>UI_Friend.lua:739..762</c> 单个 / <c>:551..583</c> 一键）。
    /// <list type="bullet">
    ///   <item><c>log_gettype</c>：1 单个领取（<c>:751</c>）/ 2 一键收取（<c>:559</c>）。</item>
    ///   <item><c>sendenergylist</c> 是**自动回赠**列表：客户端只把「同时 <c>canrecv</c> 且
    ///     <c>cansend</c>」的好友放进去（<c>:567..571</c> / <c>:754..757</c>）。</item>
    ///   <item>★ **一键收取即使两个列表都空也会发**（<c>:575</c> 无 <c>if #… &gt; 0</c> 保护，
    ///     而 82 有 <c>:600</c>）⇒ 服务端必须容忍空列表（陷阱 26）。</item>
    /// </list>
    ///
    /// 回包对界面**零影响**（<c>UI_Friend:OnFriend_RecvEnergy</c>，<c>UI_Friend.lua:960..963</c>
    /// 只有 <c>print</c>+<c>Lib_Dump</c>）。真正的表现来自：① 客户端点击时本地置
    /// <c>canrecv=false</c>（<c>:565/570/756</c>）；② 服务端推 pf 1032 改体力；
    /// ③ 服务端推 pf 1026 改 <c>(1,20)/(1,21)</c> 让主城红点计数正确；
    /// ④ 下一次 cmd 80 的 <c>canrecv</c>/<c>cansend</c> 与本次操作一致。
    ///
    /// ★ <b>本服不给「赠送」收费</b>（是否扣自己的电力**未取证**，取证文档 §10.3 也标了未取证），
    /// 因此 81 里的回赠部分只改 <c>sendenergydate</c> 记账、不动货币。
    /// </summary>
    internal Task OnRecvEnergy(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var recvIds = ReadIdList(param, "recvenergylist");
        var sendIds = ReadIdList(param, "sendenergylist");
        int logType = ReadInt(param, "log_gettype", 0);

        var now = DateTimeOffset.UtcNow;
        int today = DayIndex(now);
        var state = LoadState(player);

        int gained = GainedToday(player, today);
        int cap = _ctx.PlayerExp.MaxVigour(player.Level);
        int cur = player.GetMoney(MoneyType.Vigour);
        // 上限截断：ui.Error.20026/20027 两条文案的存在说明原服会截断（取证文档 §5.1）。
        // 体力上限来自 PlayerExp.txt 的 Spirit 列（World.cs:182 建号时就是这么设的）。
        int room = Math.Max(0, cap - cur);
        int allowedByDay = Math.Max(0, MaxGainEnergyNum - gained);
        int allowedByRoom = EnergyPerRecv > 0 ? room / EnergyPerRecv : 0;

        int taken = 0, skippedNotFriend = 0, skippedAlready = 0;
        bool dayLimited = false, roomLimited = false;
        foreach (ulong id in recvIds.Distinct())
        {
            if (!state.Hy.Contains(id))
            {
                skippedNotFriend++;
                continue;
            }
            var mark = state.MarkOf(id);
            if (mark.TakeDay == today)
            {
                skippedAlready++;
                continue;
            }
            if (taken >= allowedByDay)
            {
                dayLimited = true;
                break;
            }
            if (taken >= allowedByRoom)
            {
                roomLimited = true;
                break;
            }
            mark.TakeDay = today;
            taken++;
        }

        // 自动回赠：与 82 同一套记账（cansend 由 sendenergydate 是否为今天决定）
        int gifted = 0;
        foreach (ulong id in sendIds.Distinct())
        {
            if (!state.Hy.Contains(id))
                continue;
            var mark = state.MarkOf(id);
            if (mark.SendDay == today)
                continue;
            mark.SendDay = today;
            gifted++;
        }

        int energy = taken * EnergyPerRecv;
        if (energy > 0)
            player.AddMoney(MoneyType.Vigour, energy);
        if (taken > 0)
        {
            player.SetTask(EnergyTaskGroup, EnergyDayTaskId, today);
            player.SetTask(EnergyTaskGroup, EnergyCountTaskId, gained + taken);
        }
        WriteState(player, state);
        _ctx.Store.Save(player);

        var pushed = new List<string>();
        if (energy > 0)
        {
            GameNotify.PushMoney(session, player, MoneyType.Vigour);
            pushed.Add("1032(体力)");
        }
        if (taken > 0)
        {
            GameNotify.PushTaskValues(session, new[]
            {
                new KeyValuePair<uint, int>(
                    Player.ComposeTaskId(EnergyTaskGroup, EnergyDayTaskId), today),
                new KeyValuePair<uint, int>(
                    Player.ComposeTaskId(EnergyTaskGroup, EnergyCountTaskId), gained + taken),
            });
            pushed.Add("1026((1,20)/(1,21))");
        }

        // ★ 成功回包绝不带 nError。num 是冗余字段（客户端不读，纯日志/调试用，取证文档 §2.2 建议）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["num"] = energy });
        pushed.Add("1024(81 回包)");

        if (taken > 0 && gifted > 0)
            Tip(session, TipGetEnergyValueFeedback, energy);
        else if (taken > 0)
            Tip(session, TipGetEnergyValue, energy);
        else if (dayLimited || (recvIds.Count > 0 && allowedByDay == 0))
            Tip(session, TipGetEnergyLimited);
        else if (roomLimited || (recvIds.Count > 0 && room == 0 && skippedAlready == 0 && skippedNotFriend == 0))
            Error(session, ErrVigourFull);

        Log.Info($"[session {session.Id}] Friend_RecvEnergy(81) log_gettype={logType}" +
                 $"（{(logType == 2 ? "一键收取" : "单个领取")}）收取列表 {recvIds.Count} 个、回赠列表 {sendIds.Count} 个" +
                 $" → 实收 {taken} 个（+{energy} 体力，{cur}→{player.GetMoney(MoneyType.Vigour)}/上限 {cap}）、" +
                 $"回赠 {gifted} 个；今日计数 {gained}→{gained + taken}/{MaxGainEnergyNum}；" +
                 $"跳过：非好友 {skippedNotFriend}、今日已收 {skippedAlready}" +
                 $"{(dayLimited ? "、触发每日上限" : "")}{(roomLimited ? "、触发体力上限" : "")}" +
                 $"；推送 {string.Join(" + ", pushed)}");
        return Task.CompletedTask;
    }

    // ================================================================ 82 Friend_SendEnergy

    /// <summary>
    /// cmd 82 赠送电力。上行 <c>{roleid:&lt;me.id&gt;, sendenergylist:[…], log_sendtype:1|2}</c>
    /// （<c>UI_Friend.lua:766..779</c> 单个 <c>log_sendtype=1</c> / <c>:585..603</c> 一键 <c>=2</c>），
    /// **没有 <c>recvenergylist</c>**。
    ///
    /// 回包同样是空实现消费（<c>UI_Friend.lua:966..969</c>）⇒ 回 <c>{}</c> 即可，
    /// 表现全靠「下一次 cmd 80 的 <c>cansend=false</c>」+ 客户端本地即时置灰（<c>:778</c>）。
    ///
    /// ★ 两条与「收取」不同的地方，都是有意的：
    /// <list type="number">
    ///   <item><b>不写 <c>(1,20)/(1,21)</c></b>：那两个键的语义是「今日**收取**次数」
    ///     （<c>UI_Main.lua:871..881 GetGainEnergyNum</c>），赠送时写它等于凭空烧掉当日收取额度。</item>
    ///   <item><b>不动货币</b>：赠送是否扣自己的电力**未取证**（取证文档 §10.3）。
    ///     客户端也没有「赠送已达上限」之类的文案 ⇒ 推测赠送不设日上限、不收费，
    ///     只受「每个好友每天一次」（<c>cansend</c> 由 <c>sendenergydate</c> 决定）约束。</item>
    /// </list>
    /// </summary>
    internal Task OnSendEnergy(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var sendIds = ReadIdList(param, "sendenergylist");
        int logType = ReadInt(param, "log_sendtype", 0);

        int today = DayIndex(DateTimeOffset.UtcNow);
        var state = LoadState(player);

        int gifted = 0, skippedNotFriend = 0, skippedAlready = 0;
        foreach (ulong id in sendIds.Distinct())
        {
            if (!state.Hy.Contains(id))
            {
                skippedNotFriend++;
                continue;
            }
            var mark = state.MarkOf(id);
            if (mark.SendDay == today)
            {
                skippedAlready++;
                continue;
            }
            mark.SendDay = today;
            gifted++;
        }

        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        if (gifted > 0)
            Tip(session, TipSendEnergy);

        Log.Info($"[session {session.Id}] Friend_SendEnergy(82) log_sendtype={logType}" +
                 $"（{(logType == 2 ? "一键赠送" : "单个赠送")}）赠送列表 {sendIds.Count} 个 → 实赠 {gifted} 个" +
                 $"（跳过：非好友 {skippedNotFriend}、今日已赠 {skippedAlready}）；" +
                 $"不扣自己电力（未取证）、不动 (1,20)/(1,21)；推送 1024(82 回包)" +
                 $"{(gifted > 0 ? " + tip 电力赠送成功" : "")}");
        return Task.CompletedTask;
    }

    // ================================================================ 83 Friend_ChangeFX

    /// <summary>
    /// cmd 83 换一批推送好友。上行 <c>{roleid:&lt;me.id&gt;}</c>（<c>UI_Friend.lua:605..612</c>）。
    ///
    /// ★★ **陷阱 11**：<c>UI_Friend:OnFriend_ChangeFX</c>（<c>UI_Friend.lua:972..975</c>）是空实现
    /// ⇒ 只回 83 的话「换一批」按钮点了永远没反应。必须紧接着补一条
    /// <c>sCmd=80 / reqfriendtype=2</c> + 新 <c>FXList</c>，才会走 <c>Friend.lua:84</c> 写入
    /// <c>FriendLogic.FXList</c> → <c>UI_Friend.lua:935 ShowFriendList(2)</c> 重绘。
    /// </summary>
    internal Task OnChangeFx(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var state = LoadState(player);
        var bots = Bots(state.Seed);
        int poolSize = FreePool(bots, state).Count;
        int before = state.FxCursor;

        // 游标前移一页；空闲池为空时保持 0（避免除零）
        state.FxCursor = poolSize > 0 ? (state.FxCursor + FxPageSize) % poolSize : 0;
        WriteState(player, state);
        _ctx.Store.Save(player);

        var page = FxPage(bots, state);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        // ★ 必须补发（陷阱 11）
        ReplyList(session, player, TypeFX);
        Tip(session, TipChangeFx);

        Log.Info($"[session {session.Id}] Friend_ChangeFX(83) 空闲池 {poolSize} 个、游标 {before}→{state.FxCursor}" +
                 $" → 新「发现」页 {page.Count} 个（{string.Join(",", page.Select(b => b.Roleid))}）；" +
                 "推送 1024(83 回包) + 1024(补发 cmd 80 type=2) + tip 刷新成功");
        return Task.CompletedTask;
    }

    // ================================================================ 84 Friend_Add

    /// <summary>
    /// cmd 84 添加好友。上行 <c>{askroleid:&lt;me.id&gt;, answerroleid:&lt;目标&gt;, log_addtype:1|2|3}</c>
    /// —— ★ 键名是 <c>askroleid</c>/<c>answerroleid</c>，**不是** <c>roleid</c>。
    /// <c>log_addtype</c>：1 手动输入 ID（<c>UI_IDAdd.lua:86</c>）/ 2 推送列表（<c>UI_Friend.lua:802</c> 等 5 处）
    /// / 3 聊天/资料页（<c>UI_FriendInfo.lua:482</c>）。
    ///
    /// ★ <b>原服这条的回包恒为空对象</b>（可复现的原厂 bug）：<c>LuaCall.lua:1370..1371</c>
    /// <code>
    /// FriendLogic:Friend_Add(tbParam)     -- 返回值没接
    /// LuaCall:CallClient(sCmd, tbRet)     -- tbRet 是未声明的**全局** = nil（其余 10 条都是 local tbRet）
    /// </code>
    /// 经 <c>LuaCall.lua:518 tbParam = tbParam or {}</c> 变成 <c>{}</c>。我们照抄。
    ///
    /// <b>本地服的语义</b>（取证文档 §8.2-7）：没有对端可以「等对方同意」，
    /// 所以目标是机器人时**直接成为好友**（提示 <c>ui.UI_Friend.AddFriendOK</c>），
    /// 比「挂在申请队列里永远没人通过」体验好得多；随后补发 80/type=1 与 80/type=2
    /// （目标要从「发现」页移除）。
    /// </summary>
    internal Task OnAdd(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        ulong target = ReadUlong(param, "answerroleid");
        ulong asker = ReadUlong(param, "askroleid");
        int logType = ReadInt(param, "log_addtype", 0);

        var state = LoadState(player);
        var bots = Bots(state.Seed);
        var bot = bots.FirstOrDefault(b => b.Roleid == target);

        // ★ 不信客户端传上来的「自己」（UI_Friend.lua:800 的 askroleid = me.id），
        //   一律用 session.Tag 上的 Player.Roleid（与 MainCityFeature.cs:172 同做法）。
        string why;
        if (target == 0 || target == player.Roleid || bot is null)
            why = target == player.Roleid ? "目标是自己" : "目标不在机器人池里";
        else if (state.Hmd.Any(h => h.Roleid == target))
            why = "目标在黑名单里";
        else if (state.Hy.Contains(target))
            why = "已经是好友";
        else if (state.Hy.Count >= _friends.NumLimit(player.Level))
            why = "好友数量已达上限";
        else
            why = "";

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());   // 原服恒空对象

        if (why.Length > 0)
        {
            string tip = why switch
            {
                "好友数量已达上限" => TipLimit,
                "已经是好友" => TipAddFriendOk,
                "目标在黑名单里" => TipAddHmd,
                _ => TipNotExitRole,
            };
            Tip(session, tip);
            Log.Info($"[session {session.Id}] Friend_Add(84) askroleid={asker} answerroleid={target}" +
                     $" log_addtype={logType} → 拒绝（{why}），回空对象 + tip {tip}");
            return Task.CompletedTask;
        }

        state.Hy.Add(target);
        state.Sq.Remove(target);
        WriteState(player, state);
        _ctx.Store.Save(player);

        Tip(session, TipAddFriendOk);
        ReplyList(session, player, TypeHY);
        ReplyList(session, player, TypeFX);

        Log.Info($"[session {session.Id}] Friend_Add(84) askroleid={asker} answerroleid={target}" +
                 $"（{bot?.RoleName}）log_addtype={logType} → 本地服直接成为好友，" +
                 $"好友 {state.Hy.Count}/{_friends.NumLimit(player.Level)}；" +
                 "推送 1024(84 空回包) + tip 好友添加成功 + 补发 cmd 80 type=1/type=2");
        return Task.CompletedTask;
    }

    // ================================================================ 85 Friend_Pass

    /// <summary>
    /// cmd 85 通过好友申请。上行 <c>{answerroleid:&lt;me.id&gt;, asklist:[申请人…], log_yestype:1|2}</c>
    /// —— ★ 键名是 <c>answerroleid</c>（我是被申请方），<c>asklist</c> 是**数组**
    /// （<c>UI_Friend.lua:855..862</c> 单个 / <c>:653..663</c> 一键）。
    ///
    /// ★ 客户端本地先拦一次上限（<c>UI_Friend.lua:868..871</c>
    /// <c>if #asklist + hynum &gt; GetFriendNumLimit(me.level) then SendClientTip("ui.UI_Friend.Limit") return</c>）
    /// ⇒ 服务端必须用**同一张表**（<see cref="FriendTable"/>），否则出现「客户端不让点但服务端允许」或反之。
    ///
    /// ★★ <b>陷阱 12（本域最严重的静默失败）</b>：一键通过结尾 <c>UI_Friend.lua:688</c>
    /// <c>self:ShowFriendList(true)</c> 把 <c>curType</c> 写成布尔 <c>true</c>
    /// ⇒ 四个 <c>objXXContent:SetActive(self.curType == TypeXX)</c> 全 false
    /// ⇒ **面板变全空白，看起来像崩了**。必须补发 80/type=1（新 HYList）**和** 80/type=3（新 SQList），
    /// 让 <c>OnCMDFriend_ReqList</c> → <c>ShowFriendList(tbParam.reqfriendtype)</c> 把 curType 修回数字。
    /// </summary>
    internal Task OnPass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var askList = ReadIdList(param, "asklist");
        int logType = ReadInt(param, "log_yestype", 0);

        var state = LoadState(player);
        int limit = _friends.NumLimit(player.Level);

        int passed = 0, skipped = 0;
        bool limited = false;
        foreach (ulong id in askList.Distinct())
        {
            if (!state.Sq.Contains(id))
            {
                skipped++;
                continue;
            }
            if (state.Hy.Count >= limit)
            {
                limited = true;
                break;
            }
            state.Sq.Remove(id);
            if (!state.Hy.Contains(id))
                state.Hy.Add(id);
            passed++;
        }

        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        // ★ 必须补发这两条（陷阱 12）
        ReplyList(session, player, TypeHY);
        ReplyList(session, player, TypeSQ);
        if (limited)
            Tip(session, TipLimit);
        else if (passed > 0)
            Tip(session, TipAddFriendOk);

        Log.Info($"[session {session.Id}] Friend_Pass(85) log_yestype={logType}" +
                 $"（{(logType == 2 ? "一键通过" : "单个通过")}）asklist {askList.Count} 个 → 通过 {passed} 个" +
                 $"（跳过 {skipped} 个不在申请列表{(limited ? "，触发上限" : "")}），" +
                 $"好友 {state.Hy.Count}/{limit}、申请 {state.Sq.Count}；" +
                 "推送 1024(85 空回包) + 补发 cmd 80 type=1 与 type=3" +
                 $"{(limited ? " + tip 好友数量已达上限" : passed > 0 ? " + tip 好友添加成功" : "")}");
        return Task.CompletedTask;
    }

    // ================================================================ 86 Friend_Ignore

    /// <summary>
    /// cmd 86 忽略好友申请。上行 <c>{answerroleid:&lt;me.id&gt;, asklist:[…], log_notype:1|2}</c>
    /// （<c>UI_Friend.lua:825..833</c> 单个 / <c>:625..636</c> 一键）。
    /// 回包空实现消费（<c>:990..993</c>）⇒ 回 <c>{}</c> + 补发 80/type=3（取证文档 §2.4 建议）。
    /// 被忽略的机器人回到「发现」页的空闲池。
    /// </summary>
    internal Task OnIgnore(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var askList = ReadIdList(param, "asklist");
        int logType = ReadInt(param, "log_notype", 0);

        var state = LoadState(player);

        int removed = askList.Distinct().Count(id => state.Sq.Remove(id));
        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        ReplyList(session, player, TypeSQ);

        Log.Info($"[session {session.Id}] Friend_Ignore(86) log_notype={logType}" +
                 $"（{(logType == 2 ? "一键忽略" : "单个忽略")}）asklist {askList.Count} 个 → 移出申请 {removed} 个，" +
                 $"申请剩 {state.Sq.Count}（回到发现页空闲池）；推送 1024(86 空回包) + 补发 cmd 80 type=3");
        return Task.CompletedTask;
    }

    // ================================================================ 87 Friend_Del

    /// <summary>
    /// cmd 87 删除好友。上行 <c>{roleid:&lt;me.id&gt;, deleteroleid:&lt;目标&gt;}</c> ——
    /// ★ **本域唯一一个没有 <c>log_*type</c> 的命令**（<c>UI_FriendInfo.lua:455..460</c>），
    /// 而且唯一发送点是好友资料页的「删除好友」（<c>UI_Friend</c> 里**没有**删除按钮）。
    /// 回包空实现消费（<c>UI_Friend.lua:996..999</c>）⇒ 回 <c>{}</c> + 补发 80/type=1。
    /// 被删的机器人回到「发现」页空闲池（取证文档 §8.2-7）。
    /// </summary>
    internal Task OnDel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        ulong target = ReadUlong(param, "deleteroleid");
        var state = LoadState(player);

        bool removed = state.Hy.Remove(target);
        if (removed)
            state.Energy.Remove(target);      // 电力记账一起清，重新加回来时是干净状态
        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        ReplyList(session, player, TypeHY);

        Log.Info($"[session {session.Id}] Friend_Del(87) deleteroleid={target} → " +
                 $"{(removed ? "已删除" : "不在好友列表，无操作")}，好友剩 " +
                 $"{state.Hy.Count}/{_friends.NumLimit(player.Level)}；推送 1024(87 空回包) + 补发 cmd 80 type=1");
        return Task.CompletedTask;
    }

    // ================================================================ 88 Friend_HMDDel

    /// <summary>
    /// cmd 88 解除黑名单。上行 <c>{roleid:&lt;me.id&gt;, hmdlist:[…], log_deltype:1|2}</c>
    /// （<c>UI_Friend.lua:896..905</c> 单个 / <c>:695..708</c> 一键 / <c>UI_FriendInfo.lua:510..516</c>）。
    ///
    /// ★ <b>陷阱 14（客户端 bug）</b>：<c>UI_FriendInfo.lua:521</c> 解除时写的是
    /// <c>FriendLogic.SQList[k] = nil</c>（应为 <c>HMDList</c>）⇒ 本地根本不会移除
    /// ⇒ **必须**靠服务端补发 80/type=4 才能让黑名单页签更新。
    /// </summary>
    internal Task OnHmdDel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var ids = ReadIdList(param, "hmdlist");
        int logType = ReadInt(param, "log_deltype", 0);

        var state = LoadState(player);

        int removed = 0;
        foreach (ulong id in ids.Distinct())
            removed += state.Hmd.RemoveAll(h => h.Roleid == id);

        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        ReplyList(session, player, TypeHMD);

        Log.Info($"[session {session.Id}] Friend_HMDDel(88) log_deltype={logType}" +
                 $"（{(logType == 2 ? "一键解除" : "单个解除")}）hmdlist {ids.Count} 个 → 解除 {removed} 个，" +
                 $"黑名单剩 {state.Hmd.Count}；推送 1024(88 空回包) + 补发 cmd 80 type=4" +
                 "（客户端 UI_FriendInfo.lua:521 写错了表，本地不会自己移除）");
        return Task.CompletedTask;
    }

    // ================================================================ 89 Friend_HMDAdd

    /// <summary>
    /// cmd 89 加入黑名单。上行 <c>{roleid:&lt;me.id&gt;, hmdlist:[…], log_addtype:1}</c>
    /// —— ★ 这里的 key 又叫 <c>log_addtype</c>（与 84 同名但语义不同）。唯一发送点是
    /// <c>FriendLogic:HMDAdd(hmdroleid)</c>（<c>Friend.lua:176..183</c>），被
    /// <c>UI_FriendInfo.lua:502</c> 与 <c>:535</c> 调用。
    ///
    /// ★★ <b>陷阱 13</b>：<c>Friend.lua:185..194</c> 只从 **FXList** 移除，
    /// **从不往 HMDList 添加** ⇒ 不补发 80/type=4 的话「拉黑」在黑名单页签里根本看不见。
    /// ★ 另外必须做「不能拉黑自己」的校验（<c>ui.UI_Friend.RejectAddSelfHMD</c> 这条文案
    /// 客户端零引用 ⇒ 只可能由原服发出，取证文档 §3.4）。
    /// ★ 加入时 <c>flag</c> 必须置 <see cref="HmdFlagIBlockedHim"/>（bit0）——
    /// <c>flag = 0</c> 的条目会被 <c>UI_Friend.lua:150</c> 过滤掉、界面看不见。
    /// </summary>
    internal Task OnHmdAdd(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var ids = ReadIdList(param, "hmdlist");
        int logType = ReadInt(param, "log_addtype", 0);

        var state = LoadState(player);
        var bots = Bots(state.Seed);

        int added = 0, wasFriend = 0, rejectedSelf = 0, unknown = 0;
        foreach (ulong id in ids.Distinct())
        {
            if (id == 0 || id == player.Roleid)
            {
                rejectedSelf++;
                continue;
            }
            if (bots.All(b => b.Roleid != id))
            {
                unknown++;
                continue;
            }
            if (state.Hmd.Any(h => h.Roleid == id))
                continue;
            if (state.Hy.Remove(id))
                wasFriend++;
            state.Sq.Remove(id);
            state.Hmd.Add(new HmdEntry(id, HmdFlagIBlockedHim));
            added++;
        }

        WriteState(player, state);
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        // ★ 必须补发（陷阱 13）；若有人从好友列表被摘掉，好友页签也要同步
        ReplyList(session, player, TypeHMD);
        if (wasFriend > 0)
            ReplyList(session, player, TypeHY);

        if (rejectedSelf > 0)
            Tip(session, TipRejectAddSelfHmd);
        else if (added > 0)
            Tip(session, TipAddHmd);
        else if (unknown > 0)
            Tip(session, TipNotExitRole);

        Log.Info($"[session {session.Id}] Friend_HMDAdd(89) log_addtype={logType} hmdlist {ids.Count} 个 → " +
                 $"拉黑 {added} 个（其中原本是好友的 {wasFriend} 个）、拒绝拉黑自己 {rejectedSelf} 个、" +
                 $"未知 id {unknown} 个，黑名单 {state.Hmd.Count}（flag=1 = 我拉黑他）；" +
                 $"推送 1024(89 空回包) + 补发 cmd 80 type=4{(wasFriend > 0 ? " 与 type=1" : "")}");
        return Task.CompletedTask;
    }

    // ================================================================ 90 / 101 角色资料

    /// <summary>
    /// cmd 90 <c>Friend_IDAddInfo</c>（编号添加时取个人信息）与 cmd 101 <c>RequestPlayerInfo</c>
    /// （查看个人信息）—— 原服**共用同一个函数** <c>FriendLogic:GenRoleInfoFrOffLineData(tbParam.roleid)</c>
    /// （<c>LuaCall.lua:1444</c> / <c>:1489</c>），所以字段完全一样，差别只有「查不到人」时：
    /// <list type="bullet">
    ///   <item><b>90</b>：<c>LuaCall.lua:1445..1447</c> 判 <c>tbRet.rolename == "Not Active"</c> 就
    ///     <c>return LuaCall:SendClientTip("ui.UI_Friend.NotExitRole")</c> —— **不回 90**，只发一条提示；</item>
    ///   <item><b>101</b>：<c>LuaCall.lua:1489..1490</c> **没有**那个 if，直接回
    ///     <c>{rolename:"Not Active", level:1, …}</c> 让客户端画灰头像
    ///     （<c>UI_FriendInfo.lua:585</c> / <c>UI_Friend.lua:1049</c> 认这个哨兵）。</item>
    /// </list>
    ///
    /// ★★ 上行 <c>{roleid:&lt;目标&gt;}</c> 里的 <c>roleid</c> 是「**要查的目标**」，不是自己
    /// （对比 81/82/83/87/88/89 里 <c>roleid = me.id</c>）：<c>UI_IDAdd.lua:74..76</c>
    /// <c>self.answerroleid = tonumber(self.InputName.text)</c> → <c>{roleid = self.answerroleid}</c>。
    /// 而 <c>tonumber</c> 对乱输入返回 nil ⇒ JSON 里这个键可能整个缺失（陷阱 25）。
    ///
    /// 回包走 <b>Panel 路径</b>（<c>UI_FriendInfo.lua:148..193</c>）：只要**不发**
    /// <c>visitingcarddata</c>，<c>:132..135</c> 的 <c>skinId</c> 就是 0 ⇒ 不走 Skin 路径
    /// （那条路还要 <c>weaponnum</c>/<c>pet_num</c> 且无 <c>or 0</c> 兜底）。
    /// 字段集照客户端自己造机器人资料的 <c>GameManager:ShowRobotInfo</c>（<c>GameManager.lua:711..751</c>）。
    /// </summary>
    internal Task OnRoleInfo(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        ulong target = ReadUlong(param, "roleid");
        var state = LoadState(player);
        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (target != 0 && target == player.Roleid)
        {
            var self = BuildSelfRoleInfoPayload(player);
            _ctx.LuaCalls.CallClient(session, cmd, self);
            Log.Info($"[session {session.Id}] {(cmd == CmdIdAddInfo ? "Friend_IDAddInfo(90)" : "RequestPlayerInfo(101)")}" +
                     $" roleid={target} → 是自己，回本人资料（LV.{player.Level}，Panel 路径）");
            return Task.CompletedTask;
        }

        var bot = Bots(state.Seed).FirstOrDefault(b => b.Roleid == target);
        if (bot is not null)
        {
            _ctx.LuaCalls.CallClient(session, cmd, BuildRoleInfoPayload(bot, unix));
            Log.Info($"[session {session.Id}] {(cmd == CmdIdAddInfo ? "Friend_IDAddInfo(90)" : "RequestPlayerInfo(101)")}" +
                     $" roleid={target} → 命中机器人「{bot.RoleName}」LV.{bot.Level}" +
                     $"（头像少女 {bot.GirlIconId}、头像框 {bot.HeadFrameId}、展示卡 {bot.Cards.Count} 张）；推送 1024");
            return Task.CompletedTask;
        }

        if (cmd == CmdIdAddInfo)
        {
            // ★ 照原服 LuaCall.lua:1446：**不回 90**，只发一条 tip
            Tip(session, TipNotExitRole);
            Log.Info($"[session {session.Id}] Friend_IDAddInfo(90) roleid={target} → 查不到该编号，" +
                     "照原服做法**不回 90**、只发 tip「该编号不存在噢！」");
            return Task.CompletedTask;
        }

        _ctx.LuaCalls.CallClient(session, cmd, BuildNotActiveRoleInfoPayload(target));
        Log.Info($"[session {session.Id}] RequestPlayerInfo(101) roleid={target} → 查不到，" +
                 "回 rolename=\"Not Active\"/level=1 哨兵（客户端画「长期未登录」灰头像）；推送 1024");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 机器人的「角色资料」回包（90/101 共用）。9 个顶层字段与
    /// <c>GameManager:ShowRobotInfo</c>（<c>GameManager.lua:719..727</c>）一一对应，
    /// 再补上 <c>state</c>/<c>off_times</c>/<c>comfortscore</c>/<c>cafeflag</c>
    /// （让同一份数据也能喂 <c>UI_Friend</c> 与咖啡馆访问界面）。
    /// **有意不发** <c>visitingcarddata</c>（⇒ Panel 路径）与 <c>register_time</c>
    /// （<c>Time2Date(nil)</c> 会退化成当前时间，<c>Lib.lua:1745..1746</c>，安全）。
    /// </summary>
    internal static JsonObject BuildRoleInfoPayload(FriendBot bot, long nowUnix)
        => BuildFriendInfo(bot, nowUnix, false, false, null);

    /// <summary>
    /// 「该 roleid 查不到玩家」的哨兵资料（只有 101 会用，见 <see cref="OnRoleInfo"/>）。
    /// <c>rolename == "Not Active" &amp;&amp; level == 1</c> 两个条件必须同时成立才会走灰头像分支
    /// （<c>UI_FriendInfo.lua:585</c>），其余字段仍要给全 —— Panel 路径里
    /// <c>headframeid</c>(<c>:166</c>) / <c>signinfo</c>(<c>:170</c>) / <c>cardlist</c>(<c>:191</c>)
    /// 都在那个 if 之外、照样会被解引用。
    /// </summary>
    internal static JsonObject BuildNotActiveRoleInfoPayload(ulong roleid) => new()
    {
        ["roleid"] = roleid,
        ["rolename"] = NotActiveName,
        ["level"] = 1,
        ["state"] = 0,
        ["off_times"] = 0,                      // 0 = ui.UI_Friend.2MonAgo「二月前」（UI_Friend.lua:229）
        ["girliconid"] = 1,
        ["headframeid"] = BotHeadFrameId,
        ["signinfo"] = "",
        ["canrecv"] = false,
        ["cansend"] = false,
        ["cardnum"] = 0,
        ["comfortscore"] = 0,
        ["cafeflag"] = 0,
        ["cardlist"] = new JsonArray(),
    };

    /// <summary>
    /// 本人资料（玩家在编号添加里输入自己的 ID，或从聊天点自己的头像时）。
    /// 服务端目前没有「个性签名/展示卡」的存档来源（LuaCall 71/73 由
    /// <c>UserProfileFeature</c> 管，回的是裸数组、且新号为空），所以签名给 <c>""</c>、
    /// <c>cardlist</c> 给空数组 —— 都是合法的「什么都没设置」。
    /// </summary>
    internal static JsonObject BuildSelfRoleInfoPayload(Player player) => new()
    {
        ["roleid"] = player.Roleid,
        ["rolename"] = player.Name,
        ["level"] = player.Level,
        ["state"] = 1,                          // 自己当然在线
        ["off_times"] = (long)player.StartTime,
        ["girliconid"] = 1,
        // PlayerNtf.headframe 默认 0，而 GetBorderID(0) 会被改写成 1（CardCommon.lua:864）⇒ 0 安全
        ["headframeid"] = player.HeadFrame,
        ["signinfo"] = "",
        ["canrecv"] = false,
        ["cansend"] = false,
        ["cardnum"] = player.Items.Count(i => i.Genre == CharacterCardTable.CardGenre),
        ["comfortscore"] = 0,
        ["cafeflag"] = BotCafeFlag,
        ["cardlist"] = new JsonArray(),
    };

    // ================================================================ 机器人生成

    /// <summary>一个机器人（假好友）的完整档案。全部字段由 <see cref="SeedOf"/> 的种子确定性派生。</summary>
    /// <param name="Roleid">6 位假 roleid（与 <c>GameManager.lua:714</c> 同区间）。</param>
    /// <param name="RoleName">昵称（不能是 <see cref="NotActiveName"/>）。</param>
    /// <param name="Level">等级（<c>"LV." .. level</c>，硬必填）。</param>
    /// <param name="GirlIconId">头像少女 id（1..16，全都有图）。</param>
    /// <param name="HeadFrameId">头像框 id（固定 1）。</param>
    /// <param name="SignInfo">个性签名（可以是 <c>""</c>，绝不能是 null）。</param>
    /// <param name="State">0 离线 / 1 在线。</param>
    /// <param name="OffSeconds">「多久以前离线」的秒数；<c>off_times = now - OffSeconds</c>。</param>
    /// <param name="CardNum">角色卡数（资料页显示）。</param>
    /// <param name="ComfortScore">咖啡馆舒适度（咖啡馆访问界面显示）。</param>
    /// <param name="Cards">展示的 3 张角色卡（必须是客户端配置里真实存在的四元组）。</param>
    internal sealed record FriendBot(
        ulong Roleid, string RoleName, int Level, int GirlIconId, int HeadFrameId,
        string SignInfo, int State, long OffSeconds, int CardNum, int ComfortScore,
        IReadOnlyList<BotCard> Cards);

    /// <summary>机器人展示卡的一条（<c>FriendCardInfo</c>，<c>UI_Friend.lua:201..207</c>）。</summary>
    internal sealed record BotCard(uint G, uint D, uint P, uint L, int EnhanceLevel, int BreakLevel);

    private readonly Dictionary<uint, List<FriendBot>> _botCache = new();

    /// <summary>关闭机器人时返回的空池（复用同一实例，避免每次分配）。</summary>
    private static readonly List<FriendBot> EmptyBots = new();

    /// <summary>
    /// 取某个种子对应的机器人池（进程内缓存；同一账号每次登录、每条命令都是同一批人）。
    /// ★ 缓存的是不可变 record，不是 <see cref="JsonObject"/> ——
    /// <c>JsonNode</c> 只能有一个父节点，缓存 JSON 会在第二次塞进回包时抛
    /// 「The node already has a parent」。
    /// </summary>
    internal List<FriendBot> Bots(uint seed)
    {
        // ★ 生产环境（--bots false）不造机器人 —— 好友列表只保留真实玩家。
        //   在**唯一的生成入口**拦一刀，6 个调用点（442/576/913/964/1207/1296）全部生效，
        //   且各处本来就要处理「池为空」的情形（分页、FirstOrDefault）⇒ 无需改消费侧。
        if (_ctx is not null && !_ctx.Options.EnableBots)
            return EmptyBots;

        lock (_botCache)
        {
            if (_botCache.TryGetValue(seed, out var cached))
                return cached;
            var built = BuildBots(seed, _cards);
            _botCache[seed] = built;
            return built;
        }
    }

    /// <summary>
    /// 从种子确定性构造机器人池。纯函数（不碰 session/存档），便于单测校验「同种子同结果」。
    /// </summary>
    internal static List<FriendBot> BuildBots(uint seed, CharacterCardTable cards, int count = BotPoolSize)
    {
        // 卡模板按 (D,P,L) 排序后取，保证与字典枚举顺序无关（Dictionary 顺序不是契约）
        var pool = cards.All.OrderBy(c => c.D).ThenBy(c => c.P).ThenBy(c => c.L).ToList();

        var bots = new List<FriendBot>(count);
        var used = new HashSet<ulong>();
        int nameBase = (int)(Mix(seed, 0xA1) % (uint)BotNames.Length);

        for (int i = 0; i < count; i++)
        {
            uint h = Mix(seed, (uint)i);

            // roleid：撞了就线性探测下一个，保证池内唯一且确定性
            ulong roleid = BotRoleIdMin + (h % BotRoleIdSpan);
            while (!used.Add(roleid))
                roleid = BotRoleIdMin + ((roleid - BotRoleIdMin + 1) % BotRoleIdSpan);

            var botCards = new List<BotCard>(BotCardCount);
            for (int k = 0; k < BotCardCount && pool.Count > 0; k++)
            {
                uint ch = Mix(h, (uint)(0x100 + k));
                var tpl = pool[(int)(ch % (uint)pool.Count)];
                botCards.Add(new BotCard(tpl.G, tpl.D, tpl.P, tpl.L,
                                         1 + (int)(Mix(ch, 1) % 60),
                                         (int)(Mix(ch, 2) % 6)));
            }

            bots.Add(new FriendBot(
                Roleid: roleid,
                RoleName: BotNames[(nameBase + i) % BotNames.Length],
                Level: 20 + (int)(Mix(h, 3) % 131),                 // 20..150
                GirlIconId: 1 + (int)(Mix(h, 4) % BotGirlIconMax),  // 1..16（全都有图）
                HeadFrameId: BotHeadFrameId,
                SignInfo: BotSigns[(int)(Mix(h, 5) % (uint)BotSigns.Length)],
                State: Mix(h, 6) % 5 == 0 ? 1 : 0,                  // 约 1/5 在线
                OffSeconds: BotOffSecondsLadder[(int)(Mix(h, 7) % (uint)BotOffSecondsLadder.Length)],
                CardNum: 1 + (int)(Mix(h, 8) % 120),
                ComfortScore: 200 + (int)(Mix(h, 9) % 3800),
                Cards: botCards));
        }
        return bots;
    }

    /// <summary>
    /// 账号 → 机器人池种子。FNV-1a 32 位（与 <see cref="World.DeriveRoleId"/> 的 64 位版同族），
    /// 保证「同一账号每次登录看到同一批好友」；0 被抬成 1（0 当「未初始化」哨兵）。
    /// </summary>
    internal static uint SeedOf(string account)
    {
        const uint offset = 2166136261u;
        const uint prime = 16777619u;
        uint h = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(account ?? ""))
        {
            h ^= b;
            h *= prime;
        }
        return h == 0 ? 1u : h;
    }

    /// <summary>确定性混淆（splitmix32），用来从一个种子派生任意多个互不相关的属性。</summary>
    private static uint Mix(uint seed, uint salt)
    {
        unchecked
        {
            uint x = seed + salt * 0x9E3779B9u;
            x ^= x >> 16;
            x *= 0x21F0AAADu;
            x ^= x >> 15;
            x *= 0x735A2D97u;
            x ^= x >> 15;
            return x;
        }
    }

    /// <summary>把 roleid 列表映射回机器人档案（丢弃已经不在池里的 id）。</summary>
    private static List<FriendBot> Resolve(IReadOnlyList<FriendBot> bots, IEnumerable<ulong> ids)
    {
        var result = new List<FriendBot>();
        foreach (ulong id in ids)
        {
            var bot = bots.FirstOrDefault(b => b.Roleid == id);
            if (bot is not null)
                result.Add(bot);
        }
        return result;
    }

    /// <summary>「发现」页的空闲池：既不是好友、也不在申请里、也没被拉黑的机器人。</summary>
    internal static List<FriendBot> FreePool(IReadOnlyList<FriendBot> bots, FriendState state)
        => bots.Where(b => !state.Hy.Contains(b.Roleid)
                        && !state.Sq.Contains(b.Roleid)
                        && state.Hmd.All(h => h.Roleid != b.Roleid))
               .ToList();

    /// <summary>
    /// 当前「发现」页要展示的一批：从空闲池按 <see cref="FriendState.FxCursor"/> 环形取
    /// <see cref="FxPageSize"/> 个。★ 不落盘（只存游标）—— 否则 84/87/89 改动好友关系后，
    /// 存下来的 fx 列表会包含已经成为好友/被拉黑的人。
    /// </summary>
    internal static List<FriendBot> FxPage(IReadOnlyList<FriendBot> bots, FriendState state)
    {
        var pool = FreePool(bots, state);
        var page = new List<FriendBot>();
        if (pool.Count == 0)
            return page;
        int n = Math.Min(FxPageSize, pool.Count);
        int start = ((state.FxCursor % pool.Count) + pool.Count) % pool.Count;
        for (int i = 0; i < n; i++)
            page.Add(pool[(start + i) % pool.Count]);
        return page;
    }

    // ================================================================ 存档

    /// <summary>黑名单一条：roleid + 位域 flag（bit0 我拉黑他 / bit1 他拉黑我）。</summary>
    internal sealed record HmdEntry(ulong Roleid, int Flag);

    /// <summary>某个好友的电力记账（两个「日序」，口径 = <see cref="DayIndex"/>）。</summary>
    internal sealed class EnergyMark
    {
        /// <summary>我收取他电力的那一天（<c>takeenergydate</c>）。</summary>
        public int TakeDay { get; set; }

        /// <summary>我赠送他电力的那一天（<c>sendenergydate</c>）。</summary>
        public int SendDay { get; set; }
    }

    /// <summary>
    /// <c>Player.Extra["friend"]</c> 的内存视图。
    ///
    /// <para><b>与取证文档 §8.1 建议形状的两处差异（都是有意的）</b></para>
    /// <list type="number">
    ///   <item><b>没有 <c>fx</c> 与 <c>bots</c></b>：<c>fx</c> 由 <c>fxcursor</c> + 空闲池算出
    ///     （见 <see cref="FxPage"/>），<c>bots</c> 由 <c>seed</c> 确定性重算
    ///     （见 <see cref="BuildBots"/>）—— 存冗余快照只会带来「存档与算法不一致」的坑。</item>
    ///   <item><b>没有 <c>out</c></b>（我发出去还没被处理的申请）：本地服没有对端，
    ///     84 <c>Friend_Add</c> 直接成为好友（取证文档 §8.2-7），这个列表永远是空的。</item>
    /// </list>
    /// <c>energy</c> 里也不存 <c>recv</c>（他送我电力的日子）：机器人**每天都送**，
    /// 所以那一项恒等于「今天」，存下来没有信息量（见 <see cref="BuildEntries"/> 里 canrecv 的算法）。
    /// </summary>
    internal sealed class FriendState
    {
        /// <summary>好友 roleid（有序，界面顺序另由 <c>_roleSortFunc</c> 决定）。</summary>
        public List<ulong> Hy { get; } = new();

        /// <summary>收到的好友申请 roleid。</summary>
        public List<ulong> Sq { get; } = new();

        /// <summary>黑名单。</summary>
        public List<HmdEntry> Hmd { get; } = new();

        /// <summary>电力记账：roleid → (今日是否已收 / 已赠)。</summary>
        public Dictionary<ulong, EnergyMark> Energy { get; } = new();

        /// <summary>机器人池种子（0 = 还没初始化）。</summary>
        public uint Seed { get; set; }

        /// <summary>「发现」页轮换游标。</summary>
        public int FxCursor { get; set; }

        /// <summary>取（必要时新建）某个好友的电力记账。</summary>
        public EnergyMark MarkOf(ulong roleid)
        {
            if (Energy.TryGetValue(roleid, out var mark))
                return mark;
            mark = new EnergyMark();
            Energy[roleid] = mark;
            return mark;
        }
    }

    /// <summary>
    /// 从 <c>Player.Extra["friend"]</c> 读状态。存档被手改成别的形状（例如数组）时
    /// 退回空状态而不是抛 —— 与 <c>MainCityFeature.ReadCardState</c> 同口径。
    /// </summary>
    internal static FriendState ReadState(Player player)
    {
        var state = new FriendState();
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject root)
            return state;

        if (root[NodeHy] is JsonArray hy)
            foreach (var el in hy)
                if (AsUlong(el) is ulong id && id != 0)
                    state.Hy.Add(id);

        if (root[NodeSq] is JsonArray sq)
            foreach (var el in sq)
                if (AsUlong(el) is ulong id && id != 0)
                    state.Sq.Add(id);

        if (root[NodeHmd] is JsonArray hmd)
        {
            foreach (var el in hmd)
            {
                if (el is not JsonObject o || AsUlong(o["roleid"]) is not ulong id || id == 0)
                    continue;
                int flag = ReadInt(o, "flag", HmdFlagIBlockedHim);
                // flag=0 的条目在界面上会被 UI_Friend.lua:150 过滤掉 ⇒ 读回来时兜成 bit0
                state.Hmd.Add(new HmdEntry(id, flag == 0 ? HmdFlagIBlockedHim : flag));
            }
        }

        if (root[NodeEnergy] is JsonObject energy)
        {
            foreach (var kv in energy)
            {
                if (!ulong.TryParse(kv.Key, out ulong id) || kv.Value is not JsonObject o)
                    continue;
                state.Energy[id] = new EnergyMark
                {
                    TakeDay = ReadInt(o, "take", 0),
                    SendDay = ReadInt(o, "send", 0),
                };
            }
        }

        // ★ 种子是 uint：超过 int.MaxValue 时按 int 读会饱和成 2147483647 ⇒
        //   重启后算出**另一批机器人**（存档里的 roleid 全部失配、四个页签一起变空）。必须按 long 读。
        state.Seed = (uint)Math.Clamp(ReadLong(root, NodeSeed, 0), 0L, uint.MaxValue);
        state.FxCursor = ReadInt(root, NodeFxCursor, 0);
        return state;
    }

    /// <summary>把状态写回 <c>Player.Extra["friend"]</c>（调用方负责 <c>ctx.Store.Save</c>）。</summary>
    internal static void WriteState(Player player, FriendState state)
    {
        var hy = new JsonArray();
        foreach (ulong id in state.Hy)
            hy.Add(id);

        var sq = new JsonArray();
        foreach (ulong id in state.Sq)
            sq.Add(id);

        var hmd = new JsonArray();
        foreach (var e in state.Hmd)
            hmd.Add(new JsonObject { ["roleid"] = e.Roleid, ["flag"] = e.Flag });

        var energy = new JsonObject();
        foreach (var (id, mark) in state.Energy.OrderBy(kv => kv.Key))
        {
            if (mark.TakeDay == 0 && mark.SendDay == 0)
                continue;                       // 全 0 等于没记账，不落盘
            energy[id.ToString()] = new JsonObject
            {
                ["take"] = mark.TakeDay,
                ["send"] = mark.SendDay,
            };
        }

        player.Extra[ExtraKey] = new JsonObject
        {
            [NodeHy] = hy,
            [NodeSq] = sq,
            [NodeHmd] = hmd,
            [NodeEnergy] = energy,
            // seed 是 uint，JSON 里存成 long 以免超过 int.MaxValue 时溢出
            [NodeSeed] = (long)state.Seed,
            [NodeFxCursor] = state.FxCursor,
        };
    }

    /// <summary>
    /// 首次进入本域时播种：定种子 + 分配初始好友/申请。
    /// 返回 true 表示状态有变化（调用方要 <c>WriteState</c> + <c>Save</c>）。幂等。
    /// </summary>
    internal bool EnsureSeeded(Player player, FriendState state)
        => EnsureSeeded(player, state, _cards);

    /// <summary>可注入卡表的版本（单测用）。</summary>
    internal static bool EnsureSeeded(Player player, FriendState state, CharacterCardTable cards)
    {
        if (state.Seed != 0)
            return false;

        state.Seed = SeedOf(player.Account);
        var bots = BuildBots(state.Seed, cards);
        for (int i = 0; i < bots.Count; i++)
        {
            if (i < InitialFriendCount)
                state.Hy.Add(bots[i].Roleid);
            else if (i < InitialFriendCount + InitialRequestCount)
                state.Sq.Add(bots[i].Roleid);
            // 其余留在「发现」页的空闲池里（FxPage 按游标取一页）
        }
        state.FxCursor = 0;
        return true;
    }

    /// <summary>
    /// 读状态 + 必要时播种并**立刻落盘**。所有 handler 的入口都走它。
    ///
    /// ★ 为什么落盘要放在这一层：种子（<see cref="FriendState.Seed"/>）决定整个机器人池，
    /// 而每个 handler 都会重新 <see cref="ReadState"/>。任何一条「早退」分支
    /// （例如 84 被好友上限拒绝、90 查不到人）如果只在内存里播了种却没写回，
    /// 下一条命令会再播一次种 —— 而 <see cref="ReplyList"/> 里又是另一次，
    /// 表现为「好友列表时有时无、roleid 每次都变」。
    /// </summary>
    private FriendState LoadState(Player player)
    {
        var state = ReadState(player);
        if (EnsureSeeded(player, state))
        {
            WriteState(player, state);
            _ctx.Store.Save(player);
        }
        return state;
    }

    // ================================================================ 小工具

    /// <summary>
    /// 本地「4 点日」的日序号。★ 直接复用 <see cref="CafeFeature.CafeDayIndex"/>，
    /// 与客户端 <c>GetLocalDay4AM</c>（<c>Lib.lua:1701..1707</c>：<c>n4AM_Sec = 3600*4</c>，
    /// <c>floor((utc + 3600*Zone - n4AM_Sec) / 86400)</c>）同口径 —— 不要再造一个 <c>/86400</c>。
    /// </summary>
    internal static int DayIndex(DateTimeOffset now) => CafeFeature.CafeDayIndex(now);

    /// <summary>
    /// 今日已收取的电力次数。语义与客户端 <c>UI_Main:GetGainEnergyNum</c>
    /// （<c>UI_Main.lua:871..881</c>）完全一致：记录日 <c>(1,20)</c> 不是今天就当 0。
    /// </summary>
    internal static int GainedToday(Player player, int today)
        => player.GetTask(EnergyTaskGroup, EnergyDayTaskId) == today
            ? player.GetTask(EnergyTaskGroup, EnergyCountTaskId)
            : 0;

    /// <summary>发一条 <c>S2C.CallClientTip</c>（参数是**裸数组**，可带 <c>string.format</c> 实参）。</summary>
    private void Tip(ClientSession session, string key, params object[] args)
    {
        var arr = new JsonArray { key };
        foreach (var a in args)
            arr.Add(JsonValue.Create(a));
        _ctx.LuaCalls.CallClientFunction(session, FnCallClientTip, arr);
    }

    /// <summary>发一条 <c>S2C.CallClientError</c>（客户端自己拼 <c>"ui.Error."</c> 前缀）。</summary>
    private void Error(ClientSession session, int err)
        => _ctx.LuaCalls.CallClientFunction(session, FnCallClientError, new JsonObject { ["err"] = err });

    /// <summary>
    /// 从 tbParam 读 int。★ tbParam 可能是 <see cref="JsonArray"/>（Lua 的空表被
    /// <c>Json.Encode</c> 编成 <c>[]</c>）或 null（真机见过整个 <c>tbParam</c> 键都没有的上行，
    /// 见 <c>work/probe-three-domains.md</c> §2），而 <c>JsonNode["key"]</c> 对 JsonArray **会抛**
    /// （AGENTS.md 记录的真机崩溃）⇒ 必须先判 <c>is JsonObject</c>。
    /// </summary>
    internal static int ReadInt(JsonNode? node, string name, int fallback)
    {
        if (node is not JsonObject obj || obj[name] is not JsonNode v)
            return fallback;
        try
        {
            return v.GetValue<int>();
        }
        catch
        {
            return double.TryParse(v.ToString(), out var d) ? (int)d : fallback;
        }
    }

    /// <summary>
    /// 读 int64。存档里的 <c>seed</c> 是 uint（可能 &gt; <see cref="int.MaxValue"/>），
    /// 用 <see cref="ReadInt"/> 读会被 <c>(int)double</c> 饱和成 2147483647 ——
    /// 那等于换了一批机器人，存档里记的 roleid 全部失配、四个页签一起变空。
    /// </summary>
    internal static long ReadLong(JsonNode? node, string name, long fallback)
    {
        if (node is not JsonObject obj || obj[name] is not JsonNode v)
            return fallback;
        try
        {
            return v.GetValue<long>();
        }
        catch
        {
            return double.TryParse(v.ToString(), out var d) ? (long)d : fallback;
        }
    }

    /// <summary>从 tbParam 读 uint64 roleid（同 <see cref="ReadInt"/> 的容错口径）。</summary>
    internal static ulong ReadUlong(JsonNode? node, string name, ulong fallback = 0)
    {
        if (node is not JsonObject obj)
            return fallback;
        return AsUlong(obj[name]) ?? fallback;
    }

    /// <summary>
    /// 从 tbParam 读一个 roleid 数组（<c>recvenergylist</c>/<c>sendenergylist</c>/<c>asklist</c>/<c>hmdlist</c>）。
    /// 键缺失、不是数组、元素非法一律安静跳过，绝不抛。
    /// </summary>
    internal static List<ulong> ReadIdList(JsonNode? node, string name)
    {
        var result = new List<ulong>();
        if (node is not JsonObject obj || obj[name] is not JsonArray arr)
            return result;
        foreach (var el in arr)
        {
            if (AsUlong(el) is ulong id && id != 0)
                result.Add(id);
        }
        return result;
    }

    /// <summary>JsonNode → ulong（容忍数字字符串与浮点，失败返回 null）。</summary>
    private static ulong? AsUlong(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<ulong>();
        }
        catch
        {
            string s = node.ToString();
            if (ulong.TryParse(s, out var v))
                return v;
            // Lua 的 number 是 double，大整数可能被编成 1.0E+5 之类
            return double.TryParse(s, out var d) && d >= 0 && d <= ulong.MaxValue ? (ulong)d : null;
        }
    }
}
