using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 「访问别人的咖啡馆」域：<b>pf 1107 <c>OtherHouseInfoReq</c> → pf 1108 <c>OtherHouseInfoRsp</c></b>，
/// 外加同域的 <b>pf 1109 <c>HouseRandomListReq</c> → pf 1110 <c>HouseRandomListRsp</c></b>（「随便逛逛」）。
///
/// <para><b>★ 0. 为什么要做：这是唯一「玩家点得到但毫无反应」的断头路</b></para>
/// 好友域（<see cref="FriendFeature"/>）做出机器人好友之后，好友资料页的「访问」按钮
/// 第一次真的可点（<c>UI_FriendInfo.lua:117..123</c>）：
/// <code>
/// Game.NCafeManager.Data.VisitType = -2;                          -- :121
/// Game.NCafeNetController.ReqOtherHouseInfo(self.tbInfo.roleid);  -- :122
/// </code>
/// 而服务端此前没注册 pf 1107 ⇒ 客户端打
/// <c>Unprocessed message received. PF : 1107</c>、界面停在原地。
/// 上行取证（<c>Game.NCafeNetController$$ReqOtherHouseInfo</c>，RVA <c>0x1D3354C</c>）：
/// <code>
/// 0x01D335D8  bl #0x337EC3C   ; gsproto.OtherHouseInfoReq$$.ctor
/// 0x01D335F4  bl #0x337E894   ; set_roleid            ← req 只有这一个字段
/// 0x01D3361C  mov w0, #0x453                          ← pf = 0x453 = 1107
/// 0x01D3362C  b  #0x1B4F8C4   ; UNet.Net$$Send
/// </code>
///
/// <para><b>★ 1. 客户端怎么消费 pf 1108（三个硬判据）</b></para>
/// <c>Game.NCafeNetController.OnOtherHouseInfoRsp</c>（<c>dump.cs:532710</c>，RVA <c>0x1D31F14</c>）：
/// <code>
/// 0x1D31FA8  if ((ushort)code != 0) { MessageBox.Simple(Localization.Get($"ui.Error.{code}")); return; }
/// 0x1D320B0  if (rsp.roleid == Player.me.id) return;              // ★★ 静默 return，连日志都没有
/// 0x1D320D8  NCafeController.Data.UpdateOtherHouseInfo(rsp);      // 0x1D69E0C
/// 0x1D32190  GlobalEvent.DispatchLuaEvent(0x11 = 17, [1]);
/// 0x1D321BC  Game.Map.Load(6);                                    // ★ 直接切进咖啡馆场景
/// 0x1D321D8  if (rsp.musicinfo == null) return;                   // musicinfo 是唯一可省的
/// </code>
/// ⇒ 两条设计约束：
/// <list type="number">
///   <item><b><c>rsp.roleid</c> 必须 ≠ 自己的 id</b>。所以本域回的一律是**请求里的目标 id**
///     （校验通过之后），并显式拒绝「访问自己」；</item>
///   <item><b>回包即切场景</b>，没有第二次握手。也就是说 <c>houseinfo</c>/<c>cafeinfo</c>
///     必须一次就能过 map 6 的加载 —— 这也是本实现「复用玩家自己那套已验证可用的构造器」
///     （<see cref="HouseFeature.BuildResponse"/> / <see cref="CafeFeature"/>）的唯一理由。</item>
/// </list>
///
/// <para><b>★ 2. <c>UpdateOtherHouseInfo</c>（RVA <c>0x1D69E0C</c>）逐字段消费清单</b></para>
/// 完整反汇编（<c>tools/il2cpp_fn.py --max 1400</c>，共 909 行）里的具名调用序列：
/// <code>
/// 0x1D69EF4  get_roleid      → set_CafeRoleId                 ★ 无 has_ 判断
/// 0x1D69F38  get_entrusttime → set_EntrustTime                ★ 无 has_ 判断
/// 0x1D69F68  get_rolename    → NCafeOtherHouseInfo.set_CafeRoleName（0x1D34364）
/// 0x1D69F9C  InitFixedData(0x1D61500) + InitDefaultData(0x1D61834)   ← ★ 系统家具客户端自建
/// 0x1D69FC0  get_houseinfo → HouseCache.roomList      → OnUpdateRoom (0x1D696F0)
/// 0x1D6A0C8  get_houseinfo → HouseCache.furnitureList → OnUpdateItem (0x1D692A0)
/// 0x1D6A1D0  get_houseinfo → HouseCache.themeList     → OnUpdateTheme
/// 0x1D6A2F0  get_cafeinfo  → has_basetime/get_basetime
///                            has_level/get_level      → set_CafeLevel (0x1D66708)
///                            seatlist   → UpdateSeatGuest (0x1D67248)
///                            ClearGuestQueue → customerqueue → AddGuest2Queue (0x1D67A6C)
///                            ClearCoffeeItems → coffeelist → AddCoffeeItems (0x1D65E68)
///                            roomgirlslist → UpdateAreaGirl
///                            has_hot/get_hot、has_comfort/get_comfort → SetCafeComfort
/// </code>
/// 五条由此确定的实现口径：
/// <list type="bullet">
///   <item>★ <b><c>houseinfo</c> 必须给实例</b>：三段（<c>roomList</c>/<c>furnitureList</c>/
///     <c>themeList</c>）**各自重新 <c>get_houseinfo()</c> 且都不判 nil</b>，
///     <c>0x1D69FC8 cbnz x21,+8; bl #0x13F30AC</c> 就是抛 NRE 的桩。
///     三个 repeated 本身是懒创建的，所以「空 <c>HouseCache</c>」是安全的。</item>
///   <item>★★ <b><c>cafeinfo</c> 其实<u>可以</u>为 nil —— 这一条更正 <c>docs/05</c> §19.7</b>：
///     <c>0x1D6A2F8 cbz x19, #0x1D6AC18</c> 直接跳到函数收尾
///     （<c>0x1D6AC18..0x1D6AC34</c> 是 <c>sub sp,x29,#0x50</c> + 5 条 <c>ldp</c> + <c>ret</c>）。
///     即 §19.7 说的「五个必填」里 <c>cafeinfo</c> 是**四必填 + 一可省**。
///     本实现仍然照发 —— 省掉它等于「等级/舒适度/顾客/咖啡/少女全部停留在 <c>Clear()</c> 后的空状态」，
///     而 <c>cafeLevel</c> 挂在 controller 上（不在 <c>houseInfo</c> 里）、<c>Clear()</c> 不动它
///     ⇒ 会沿用**玩家自己店**的等级，表现为「每个人的店等级都一样」。</item>
///   <item>★ <c>cafeinfo</c> 内部是**逐字段 <c>has_</c> 判断**（<c>basetime</c>
///     <c>0x1D6A304</c> / <c>level</c> <c>0x1D6A324</c> / <c>hot</c> <c>0x1D6ABD0</c> /
///     <c>comfort</c> <c>0x1D6ABF8</c>，都先 <c>has_</c> 再 <c>get_</c>）⇒ 标量漏了不崩、只是不更新。
///     理由同上，四个照样全写。</item>
///   <item>★★ <b>系统家具由客户端 <c>InitFixedData(false)</c>/<c>InitDefaultData(false)</c> 自建</b>
///     （<c>0x1D69F94 mov w1,wzr</c> 传 <c>isSelf=false</c>，两个函数只读本地
///     <c>Settings.SystemItems</c>/<c>NoRoomItems</c>/<c>DefaultItems</c>、不读 rsp）
///     ⇒ 与 pf 1048 那条路完全同构：服务端只发玩家家具（<c>objId ≥ 1000</c>），
///     一件系统家具都不能发。这正是 <see cref="HouseFeature"/> 已经在做的事。</item>
///   <item>★ <b>每次 1108 到达都会先整体 <c>Clear()</c></b>（<c>0x1D69EE4 blr x9</c> =
///     <c>NCafeHouseInfo.Clear()</c> 虚 slot 4，<c>dump.cs:530256</c> RVA <c>0x1D26940</c>）
///     ⇒ 下行必须是**全量快照**，增量推送无效。</item>
/// </list>
///
/// <para><b>★ 2b. 访客模式在 C# 侧不是布尔开关，而是 <c>CafeRoleId != Player.me.id</c></b></para>
/// <c>get_HouseInfo</c>（RVA <c>0x1D622C8</c>）按 <c>IsInSelfCafe()</c> 分流字段：
/// <code>
/// 0x1D6231C  bl #0x1D5C514   ; IsInSelfCafe
/// 0x1D62324  ldr x0,[x19,#0x28]   ; 真 → playerHouseInfo（自己的）
/// 0x1D6232C  ldr x0,[x19,#0x30]   ; 假 → otherHouseInfo（访客的）
/// </code>
/// 而 <c>IsInSelfCafe</c>（<c>0x1D5C514</c>）就一句 <c>cmp get_CafeRoleId(), Player.me.id; cset eq</c>。
/// ⇒ <b><c>UpdateOtherHouseInfo</c> 的第一步 <c>set_CafeRoleId(rsp.roleid)</c>（<c>0x1D69F00</c>）
/// 一旦写入 ≠ 自己的 id，此后所有 <c>Data.HouseInfo</c>/<c>Items</c>/<c>Areas</c>/<c>Seats</c>
/// 自动切到访客那一份</b>。这也解释了为什么「不能访问自己」是硬约束：
/// <c>roleid == me.id</c> 会让整套数据写进**自己**的容器，等于污染玩家自己的店。
///
/// <para><b>★ 3. 访客模式（<c>VisitType</c>）的 map 6 加载路径比「自己的店」更宽松</b></para>
/// <c>NCafe.lua:16..59</c>（map 6 的 <c>OnEnter</c>）按 <c>IsInSelfCafe()</c> 分叉：
/// <code>
/// if (NCafeMgr.Data:IsInSelfCafe()) then self:FixSeatOverLimit(); end     -- :18  ★ 访客跳过
/// NCafeMgr.Ins:OnEnter(function ()
///     if (NCafeMgr.Data:IsInSelfCafe()) then
///         GirlVigourManager.Init(); RandomEvent:DoRandomEvent();          -- :25..27 ★ 访客跳过
///         GirlMissionManager:Star(); UI.Open('UI_NCafe'); ...
///     else
///         UI.Open('UI_NCafeVisit');                                      -- :39  访客界面
///     end
///     Map.ExecLoadingEndCall(); self.super:OnEnter();                     -- :44..45 收加载条
/// end)
/// self:CheckRandomVisitList();                                           -- :56  ★ 无条件
/// local outDoorItem = ...:GetOutDoorItem(); U3D.RenderSettings.fog = ... -- :57..58 ★ 无条件
/// </code>
/// ⇒ 访客模式**跳过**了两个最凶的点：
/// <list type="number">
///   <item><c>FixSeatOverLimit</c> → <c>CafeEditor.lua:103..116</c> 那个「等级 &gt; <c>Limit</c>
///     列长度就 <c>error()</c>」（<see cref="CafeFeature"/> 类注释 §0b 第 1 条）；</item>
///   <item><c>GirlVigourManager.Init</c> → 每秒 <c>conf.ReduceVigor</c> 裸取值
///     （同 §0b 第 3 条）。</item>
/// </list>
/// 但等级仍然照 <see cref="CafeFeature.ReadState"/> 的口径钳位 —— 一是访客界面
/// <c>UI_NCafeVisit.lua:206..213</c> 的 <c>switchToRoom</c> 会拿 <c>CafeLevel</c> 与
/// <c>CafeLogic:GetOpenRestLevel()</c> 比，二是玩家从访客模式返回自己店时
/// <c>NCafeDataController</c> 里残留的等级会被沿用。
///
/// <para><b>★ 3b. 访客路径的咖啡橱窗反而<u>有</u> nil 保护（与 LuaCall 112 不同）</b></para>
/// LuaCall 112 那条路是 Lua 的 <c>NCafeNet:updateCoffeeData</c>（<c>NCafeNet.lua:136</c>）
/// 直接 <c>template.Time</c> —— <b>无</b> nil 保护（<see cref="CafeFeature"/> 里专门钳过）。
/// 而 pf 1108 这条路走 C# <c>0x1D6A848 Game.Lua$$Call&lt;int&gt;("CoffeeCommon.GetCoffeeTime")</c>
/// （字符串常量 @<c>0x52BDD50</c>），对应 <c>CoffeeCommon.lua:658..665</c>：
/// <code>
/// function CoffeeCommon.GetCoffeeTime(coffeeType)
///     local coffee = CoffeeCommon:GetCoffeeListConfig(coffeeType);
///     if (coffee) then return coffee.Time; end
///     return 0;                                  -- ★ 有兜底
/// end
/// </code>
/// ⇒ 非法 <c>coffeetype</c> 在这条路上只会「时间 0」而不抛。
/// 本实现仍然只发校验过的咖啡（数据来源同一个 <see cref="CafeFeature.ReadContent"/>），
/// 因为同一份数据也会经 LuaCall 112 下发给店主自己。
///
/// <para><b>★ 4. 访客模式会发出的上行：只有一条，且已实现</b></para>
/// 逐行扫 <c>UI_NCafeVisit.lua</c>（<c>grep -n "LuaCall\|SendCmd\|NCafeNet"</c>）：
/// <list type="bullet">
///   <item><c>:400</c> <c>LuaCall:CallServer(LuaCall.Friend_Add, ...)</c> —— 「加好友」按钮，
///     <see cref="FriendFeature"/> 已实现（LuaCall 80）；</item>
///   <item><c>:328</c> <c>ReqOtherHouseInfo(roleId)</c> —— 「访问下一个」，还是 pf 1107（本域）；</item>
///   <item><c>:141</c> <c>NCafeNet.ReqDownloadCafeData(...)</c> —— 返回自己店时的 LuaCall 112（已实现）；</item>
///   <item><c>:99</c> <c>getEntrustRewardTimes()</c> = <c>GetTaskValue</c>，**纯本地读任务值**，不发包。</item>
/// </list>
/// ⇒ <b>本域不需要顺带实现任何 LuaCall</b>。返回主城/好友界面走
/// <c>UI_NCafeVisit.lua:126..137</c> 的 <c>Map.LoadById(2)</c>（<c>VisitType == -2</c> 时
/// 额外 <c>UI.CloseAndOpen('UI_Friend')</c>），也不发包。
///
/// <para><b>★ 4b. 进店期间的两条「非 LuaCall」上行（都已实现）</b></para>
/// <list type="bullet">
///   <item><b>pf 1109</b>（<c>0x1D34224 mov w0,#0x455</c>）—— <c>NCafe.lua:56</c> 无条件调
///     <c>CheckRandomVisitList()</c>，但那个函数 <c>:200</c> 的
///     <c>if (not randomList) then return</c> 会**提前返回** ⇒ ★ <b>进店其实不发</b>，
///     真正的发包点是 <c>Game.Player.Login</c>（<c>0x185400C</c>）。完整取证与实测更正见 §6；</item>
///   <item><c>sFunction=NCafePetLogic</c> 的 <c>GetCafeFoodNum</c> 与 <c>UpdateFoodBoxs</c>
///     —— <b>没有 <c>IsInSelfCafe</c> 守卫</b>（<c>NCafePetController.OnEnter</c> <c>0x1A3435C</c> /
///     <c>InitCharacters</c> state3 <c>0x1D60C1C</c>）⇒ 访客模式同样会发，
///     由 <see cref="CafePetFeature"/> 处理（真机实测两条都正常回包）。</item>
/// </list>
///
/// <para><b>★★ 4c. 咖啡馆经营 117/124/125 与本域<u>无关</u>，不必为它而做</b></para>
/// <c>AGENTS.md</c> 记「咖啡馆经营还剩 117/124/125 未实现」，容易误以为访客模式会踩到。
/// 逐条查过守卫（取证 <c>work/visit-cafe-forensics.md</c> §5.2）：
/// <list type="bullet">
///   <item><b>117</b> <c>Cafe_NewSeatPosEmpty</c> 只由 <c>RspCustomerQueue</c>(<c>NCafeNet.lua:245</c>) /
///     <c>RspEndCustomer</c>(<c>:335</c>) 调用，二者都要先有 115 的回包 ⇒ 访客不触发；</item>
///   <item><b>124</b> 属顾客生成链，同上不触发；</item>
///   <item><b>125</b> <c>Cafe_OnLeave</c> 的唯一发起点是 <c>UI_NCafe.lua:665</c> 的
///     <c>OnRelease</c>，而 <b><c>UI_NCafe</c> 在访客模式从未打开</b>
///     （<c>NCafe.lua:39</c> 只开 <c>UI_NCafeVisit</c>）⇒ 那个 <c>OnRelease</c> 不会跑。
///     <c>UI_NCafeVisit</c> 全文只有 1 个 <c>CallServer</c>（<c>:400</c> 加好友）。</item>
/// </list>
/// ⇒ 访客模式**零 unhandled**（真机实测确认）。
///
/// <para><b>★ 5. <c>VisitType</c> 语义（决定「访问下一个」按钮从哪取 id）</b></para>
/// <c>UI_NCafeVisit.lua:335..357</c> <c>getNextRoleId()</c>：
/// <list type="table">
///   <item><term>-2</term><description>好友资料页进入（<c>UI_FriendInfo.lua:121</c>）——
///     返回时回好友界面；「下一个」取 <c>FriendLogic.HYList</c>。</description></item>
///   <item><term>-1 / 3</term><description>聊天链接进入（<c>ChatCommon.lua:172/174</c>）——
///     返回时回主城。</description></item>
///   <item><term>0</term><description>咖啡馆访问界面「我的好友」页签
///     （<c>UI_CafeVisitMain.lua:593</c>），「下一个」取 <c>FriendLogic.HYList</c>。</description></item>
///   <item><term>1</term><description>同界面「我的访客」页签（<c>:595</c>），
///     「下一个」取 <c>GetVisitedInfoList()</c> —— 本服 <c>visitedList</c> 故意留空
///     （<c>docs/05</c> §19.6），所以这一支永远拿不到 id，属预期。</description></item>
///   <item><term>2</term><description>「随便逛逛」（<c>:622</c> <c>visitRandom</c>），
///     取 <c>GetRandomList()</c> ⇒ 依赖 <b>pf 1109/1110</b>，见 §6。</description></item>
/// </list>
/// ★ <c>VisitType</c> 全部在客户端本地设置，**服务端看不到也不需要知道**；
/// 服务端只管「谁的咖啡馆」。
/// ★★ 更强的一条：<c>VisitType</c> 在 <b>C# 侧零处读取</b>
/// （<c>il2cpp_callers.py</c> 查两个 accessor <c>0x1D622B8</c>/<c>0x1D622C0</c>，
/// 调用者只有 XLua 桥）—— 它纯粹是给 Lua 记「从哪进来的、返回去哪」用的 UI 变量。
/// 真正的访客判据始终是 <c>CafeRoleId != Player.me.id</c>（见 §2b）。
///
/// <para><b>★ 5b. 两条本轮实测/取证确认的行为（都不需要服务端配合）</b></para>
/// <list type="bullet">
///   <item><b><c>VisitType == -2</c> 时「访问下一个」按钮会自动隐藏</b>：
///     <c>getNextRoleId()</c>（<c>UI_NCafeVisit.lua:333..357</c>）与
///     <c>getRoleListCount()</c> 都只有 <c>0/1/2</c> 三个分支，<c>-2</c> 三个都不匹配
///     ⇒ 计数 0 ⇒ <c>:95 SetActive(count > 1)</c> 为假。
///     ★ 本轮截图正好印证：`work/visit-cafe.png`（<c>-2</c>，无「下一个」）
///     vs `work/visit-cafe-next.png`（改成 <c>2</c> 后，「下一个」出现）。
///     ⇒ 从好友资料页进来的玩家**不会**再发第二个 pf 1107。</item>
///   <item><b>离店时客户端自己复位访客状态</b>：<c>NCafeDataController.OnLeave</c>
///     （RVA <c>0x1D61B84</c>）判 <c>Map.loadingId != 6</c>（<c>0x1D61C20 cmp w0,#6</c>）
///     就调 <c>ClearOtherHouseInfo</c>(<c>0x1D61C58</c>) + <c>set_CafeRoleId(RoleId)</c>
///     ⇒ 自动回到「自己的店」语义。这解释了真机实测的「返回主城后
///     <c>IsInSelfCafe()</c> 变回 true」，且**服务端一个包都不用发**。</item>
/// </list>
///
/// <para><b>★ 6. 顺带补 pf 1109/1110：不补「随便逛逛」永远是空的</b></para>
/// <c>NCafe.lua:56</c> 的 <c>CheckRandomVisitList()</c>（<c>:198..229</c>）在每次进 map 6 时
/// 按「列表长度 + 距上次请求的时长」决定是否重拉：
/// <code>
/// local randomList = NCafeMgr.Data:GetRandomList();
/// if (not randomList) then return; end                    -- :200 ★ 见下面的更正
/// if (randomeCount &lt;= 10)  then if (diff &gt;= 300)  then reqAgain = true; end
/// elseif (randomeCount &lt;= 30)  then if (diff &gt;= 1800) then reqAgain = true; end
/// elseif (randomeCount &lt;= 100) then if (diff &gt;= 3600) then reqAgain = true; end
/// if (reqAgain) then Game.NCafeNetController.ReqHouseRandomList(); end
/// </code>
/// ★★ <b>真实的发包时机（本轮实测更正，别照抄「每次进店必发」那个说法）</b>：
/// <c>ReqHouseRandomList</c> 的调用者只有 <b>两个</b>
/// （<c>il2cpp_callers.py of 'Game.NCafeNetController$$ReqHouseRandomList'</c>）：
/// <c>Game.Player.Login</c>（RVA <c>0x185400C</c>）与 XLua 桥。
/// 而 <c>GetRandomList</c>（<c>0x1D6AD38</c>）返回的
/// <c>NCafePlayerHouseInfo.RandomVisitList</c>(+0x68) <b>不在 <c>NCafeHouseInfo..ctor</c>
/// （<c>0x1D26AB8</c>）初始化的 7 个容器里</b>（那里只 <c>str</c> 了 +0x10..+0x40）
/// ⇒ 服务端从未回过 1110 时它是 <b>null</b> ⇒ <c>:200</c> 直接 return
/// ⇒ <b>进店其实不会发 1109</b>，只有**登录时**无条件发一次。
/// 真机日志印证：本轮全程只有 2 次 <c>&lt;= pf=1109</c> —— 一次在登录（serial=4），
/// 一次是 GM 手动触发；进 map 6 那一刻没有。
/// ⇒ 所以本域的价值不是「消掉白发的包」，而是<b>让「随便逛逛」与「访问下一个」
/// 在 <c>VisitType</c> 0/1/2 路径下真的能用</b>（此前 <c>Count</c> 恒 0 ⇒
/// <c>UI_CafeVisitMain.lua:607..610</c> 恒弹 <c>UI_CafeVisit.NoRoom</c>「没有可访问的房间」）。
/// 客户端消费（<c>OnHouseRandomListRsp</c> RVA <c>0x1D333B8</c>）极简：
/// <code>
/// 0x1D33438  rolelist → 0x1D6AC3C NCafeDataController.AddRandomList
/// </code>
/// ⇒ 只要一串 uint64。本域回**机器人池里全部 <c>cafeflag</c> 已解锁的人**（含非好友），
/// 与「随便逛逛」的语义一致；并显式剔除自己（否则 <c>0x1D320B0</c> 会让那一次访问静默失败）。
///
/// <para><b>★ 7. 好友的咖啡馆数据从哪来：按 roleid 确定性派生</b></para>
/// 机器人好友只存在于服务端（<see cref="FriendFeature.BuildBots"/>），没有存档。
/// 本域按 <b>目标 roleid</b> 派生一份稳定的咖啡馆：
/// <list type="bullet">
///   <item>家具：直接用 <see cref="HouseFeature.CreateDefault"/> 的那 13 件默认摆设
///     （<c>NCafeDefautItems.txt</c>）—— 这份**已经在真机上验证过能过 map 6**，
///     是「一次就要对」的唯一保险做法；</item>
///   <item>等级/人气/舒适度：从 roleid 用 <see cref="Mix"/> 派生，并按
///     <see cref="CafeTable"/> 钳位（口径与 <see cref="CafeFeature.ReadState"/> 相同）；</item>
///   <item>座位/排队/咖啡/少女：一律**空**。理由见 §7b。</item>
/// </list>
///
/// <para><b>★ 7b. 为什么别人的店里不摆顾客与少女（这是有意的，不是偷懒）</b></para>
/// <list type="number">
///   <item><b>座位有「静默丢弃」硬约束</b>：<c>UpdateSeatGuest</c>（RVA <c>0x1D67248</c>）
///     先过 <c>CheckSeatValid</c>（<c>0x1D67590</c>）= <c>GetItem(position / 10) != null &amp;&amp;
///     it.Room == 1</c>，即 <c>position</c> 必须是**本次 <c>houseinfo</c> 里真实存在**的餐位家具
///     <c>objId × 10</c>。派生数据一旦与家具表不同源就会被静默丢掉
///     （见 <see cref="CafeFeature"/> 类注释 <c>BuildSeatList</c> 那一条）；</item>
///   <item><b>少女属性在访客模式下取不到</b>：<c>GirlCommon:Get</c>（<c>GirlCommon.lua:92</c>）
///     开头就是 <c>if not GirlVarOffset[sKey] or not Game.NCafeManager.Data:IsInSelfCafe() then</c>
///     ⇒ 不在自己咖啡馆时读不到任何少女属性（<c>AGENTS.md</c> §3 已记这条坑）。
///     摆了少女只会得到「模型在、属性全 nil」的半成品；</item>
///   <item>空清单本身是**合法且安全**的：<c>UpdateOtherHouseInfo</c> 对四个 repeated 字段
///     都是「先 Clear 再 foreach」，空 = 干净的空店。</item>
/// </list>
/// ⇒ 玩家看到的是「别人的咖啡馆布局 + 等级/舒适度」，没有顾客与少女。
/// 这是本服在「一次回包就要过 map 6」的约束下最稳的形态。
///
/// <para><b>★ 7c. ★★ 如果将来要填 <c>roomgirlslist</c>：它是<u>位置编码</u>的，且区域表硬编码在 C# 里</b></para>
/// <c>0x1D6A91C..0x1D6A9D4</c> 现场 <c>new List&lt;NCafeAreaType&gt;()</c> 然后
/// <c>Add(1)</c>=<c>Cafe</c> / <c>Add(2)</c>=<c>Rest</c> / <c>Add(6)</c>=<c>Dorm</c>，
/// 循环里用**外层下标 <c>i</c>** 去索引它（<c>0x1D6AB2C</c>），再
/// <c>UpdateAreaGirl(areaList[i], girls[j], modelid[j])</c>（<c>0x1D6AB64</c>，
/// 签名 <c>dump.cs:532035</c>）：
/// <list type="bullet">
///   <item><c>roomgirlslist[0] → Cafe(1)</c>、<c>[1] → Rest(2)</c>、<c>[2] → Dorm(6)</c>；</item>
///   <item>⇒ 想只在某个区放少女也**必须发满 3 个 <c>girlswrap</c>**（不放就发空的），
///     否则位置错位；</item>
///   <item>⇒ 发**第 4 个会让 <c>areaList[3]</c> 越界</b>抛 <c>ArgumentOutOfRangeException</c>；</item>
///   <item><c>modelid</c> 可以短或空（<c>0x1D6AB10 mov w27,wzr</c> 兜底 0），
///     内层循环上限是 <b><c>girls.Count</c></b>（<c>0x1D6AB7C</c>）。</item>
/// </list>
/// 本实现发**空 <c>roomgirlslist</c>**（<c>Count == 0</c> ⇒ 外层循环 0 次），
/// 所以不受这条约束影响；写在这里是为了将来有人要填时不用重新反汇编。
/// ★ 附带一条对「访客模式少女」的取证结论（<c>work/visit-cafe-forensics.md</c> §4）：
/// 少女**属性**完全不用发 —— <c>GirlCommon:Get</c> 在访客模式恒返回 0、
/// <c>IsGirlTraining</c> 恒 false、<c>GetHappyMode</c> 恒 <c>Happy</c>，全都有兜底；
/// 真正必填的只有 <c>girls[j]</c>（少女 id，要能在本地配置里找到模型）与
/// <c>modelid[j]</c>（访客模式直接用它当外观，为 0 走默认皮、不崩）。
/// 同理两条实参映射方向**极易搞反**（写服务端时的高频错误）：
/// <code>
/// seatlist:      UpdateSeatGuest(guid ← position, customer ← customeridx, type ← customertype, ...)
/// customerqueue: AddGuest2Queue (id   ← customertype, guid ← customeridx, start ← starttime)
/// </code>
/// 即座位的第 1 参收 <c>position</c>，而队列的第 1 参收 <c>customertype</c>、第 2 参才是 <c>customeridx</c>
/// （<c>0x1D6A604</c>/<c>0x1D6A620</c>/<c>0x1D6A654</c>，签名 <c>dump.cs:532071</c>）。
/// 另：座位的 <c>customeridx == 0</c> 被 C# 当**空座位**（<c>0x1D6A3DC cbz w0</c>），
/// 与 LuaCall 112 那边「省略键」的口径一致。
///
/// <para><b>★ 8. 为什么不用 <c>ctx.MapPf</c> 覆盖也能生效</b></para>
/// pf 1107 <b>GameModule 压根没注册</b>（<c>GameModule.cs:110..118</c> 只有 1102/1001/1008/
/// 1105/1048/1109/1022/1006/1027），所以 1107 是纯新增。
/// 1109 则与 <see cref="HouseFeature"/> 处理 1048 的情形相同 —— 用 <c>ctx.MapPf</c>
/// **覆盖** <c>GameModule</c> 那条「回空包」的注册（<c>PfRouter.Map</c> 是覆盖赋值，
/// 且 feature 装配发生在 <c>GameModule</c> 自己那批 <c>router.Map</c> 之后）。
/// </summary>
public sealed class VisitCafeFeature : IGameFeature
{
    /// <summary>
    /// 派生咖啡馆等级的上限。★ 故意**不用** <c>CafeTable.MaxCafeLevel</c>（现网 30）：
    /// 别人的店只是「看一眼」，等级越高越容易碰到未取证的等级相关分支
    /// （<c>UI_NCafeVisit.lua:206..213</c> 的 <c>switchToRoom</c> 会按等级判断宿舍是否开放）。
    /// 取 10 足够让访客界面有区分度，且必然落在 <c>NCafeLevels.txt</c> 实有键里。
    /// </summary>
    public const int MaxDerivedLevel = 10;

    /// <summary>派生人气/舒适度的上限（仅用于显示，钳位仍以 <see cref="CafeTable"/> 为准）。</summary>
    public const int MaxDerivedComfort = 4000;

    private GameFeatureContext? _ctx;
    private CafeTable _cafeTable = CafeTable.Empty;
    private HouseTable _houseTable = HouseTable.Empty;
    private CharacterCardTable _cards = CharacterCardTable.Empty;

    public string Name => "访问别人的咖啡馆(pf 1107/1108 + 1109/1110)";

    public VisitCafeFeature() { }

    /// <summary>
    /// 单测用：直接注入配置表，跳过 <see cref="Install"/>。
    /// ★ <paramref name="cards"/> 必须给 —— 机器人池的展示卡从它来，
    /// 传 <see cref="CharacterCardTable.Empty"/> 会让派生出的机器人 <c>Cards</c> 为空，
    /// 与 <see cref="FriendFeature"/> 那边（用真表）逐字段比就会不一致。
    /// </summary>
    internal VisitCafeFeature(CafeTable cafeTable, HouseTable houseTable, CharacterCardTable cards)
    {
        _cafeTable = cafeTable;
        _houseTable = houseTable;
        _cards = cards;
    }

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string dir = Path.GetFullPath(ctx.Options.SettingDirectory);
        // 三张表内部都按目录做了进程级缓存，与 CafeFeature / HouseFeature / FriendFeature 共用同一实例
        _cafeTable = CafeTable.Load(dir);
        _houseTable = HouseTable.Load(dir);
        // ★ 卡表从 ctx 拿（GameModule 全进程只加载一次，见 GameFeatureContext.CharacterCards）
        _cards = ctx.CharacterCards;

        ctx.MapPf(GamePf.OtherHouseInfo, (s, p) => HandleOtherHouseInfo(new SessionAdapter(s), p));
        // ★ 覆盖 GameModule 那条「回空 HouseRandomListRsp」的注册，理由见类注释 §8
        ctx.MapPf(GamePf.HouseRandomList, (s, p) => HandleHouseRandomList(new SessionAdapter(s), p));
    }

    // =================================================================
    //  pf 1107 OtherHouseInfoReq → pf 1108 OtherHouseInfoRsp
    // =================================================================

    /// <summary>
    /// pf 1107 的处理：校验目标 → 派生（或读出）目标的咖啡馆 → 回 pf 1108。
    ///
    /// <para><b>★ 失败一律「不回包」，不要回一个错误的 1108</b></para>
    /// 三种失败（会话未登录 / 目标是自己 / 目标不在机器人池里）都只记日志、不回包。
    /// 理由：客户端 <c>OnOtherHouseInfoRsp</c> 只要 <c>code == 0 &amp;&amp; roleid != me.id</c>
    /// 就**立刻 <c>Map.Load(6)</c>**（<c>0x1D321BC</c>），回一个半成品会把玩家送进一个
    /// 空咖啡馆场景且退不回去；而不回包时玩家仍停在好友资料页，只是「点了没反应」——
    /// 后者明显更安全。
    ///
    /// ★ 也**不用** <c>code != 0</c> 那条通道（<c>0x1D31FA8</c> 会弹 <c>ui.Error.&lt;code&gt;</c>）：
    /// 那些错误码的文案未取证（<c>AGENTS.md</c> §2.3 只确认了通道存在），
    /// 乱填会弹出一句意义不明的报错。
    /// </summary>
    internal Task HandleOtherHouseInfo(IGameSession session, Packet packet)
    {
        var req = TryParseReq(session, packet);
        if (req is null)
            return Task.CompletedTask;

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] OtherHouseInfoReq(1107) 到达但会话未登录（Tag 非 Player），" +
                     "不回包（回半成品会把玩家送进空的 map 6）");
            return Task.CompletedTask;
        }

        ulong target = req.HasRoleid ? req.Roleid : 0UL;

        if (target == 0)
        {
            Log.Warn($"[session {session.Id}] OtherHouseInfoReq(1107) 没带 roleid（或为 0），不回包");
            return Task.CompletedTask;
        }

        // ★ 0x1D320B0：rsp.roleid == Player.me.id 时客户端直接 return（静默）。
        // 与其让客户端静默丢弃，不如服务端就地拒绝并说清楚。
        if (target == player.Roleid)
        {
            Log.Warn($"[session {session.Id}] OtherHouseInfoReq(1107) 目标是自己 roleid={target}，" +
                     "不回包（客户端 0x1D320B0 会静默丢弃；进自己的店走 LuaCall 112 那条路）");
            return Task.CompletedTask;
        }

        var bot = FindBot(player, target);
        if (bot is null)
        {
            Log.Warn($"[session {session.Id}] OtherHouseInfoReq(1107) 目标 roleid={target} 不在机器人池里，" +
                     "不回包 —— 单机服里除了自己只有 " +
                     $"{FriendFeature.BotPoolSize} 个确定性机器人（FriendFeature.BuildBots）");
            return Task.CompletedTask;
        }

        var rsp = BuildResponse(bot, _cafeTable, _houseTable);

        Log.Info($"[session {session.Id}] 访问咖啡馆下发 roleid={bot.Roleid} name={bot.RoleName}：" +
                 $"lv={rsp.Cafeinfo!.Level} hot={rsp.Cafeinfo.Hot} comfort={rsp.Cafeinfo.Comfort}、" +
                 $"家具 {rsp.Houseinfo!.FurnitureList.Count} 件" +
                 $"（objId {(rsp.Houseinfo.FurnitureList.Count > 0 ? $"{rsp.Houseinfo.FurnitureList[0].ObjId}..{rsp.Houseinfo.FurnitureList[^1].ObjId}" : "-")}）、" +
                 $"分区 {rsp.Houseinfo.RoomList.Count} 个、entrusttime={rsp.Entrusttime}；" +
                 "座位/排队/咖啡/少女一律空（理由见 VisitCafeFeature 类注释 §7b）—— " +
                 "客户端收到后会立刻 Map.Load(6) 进访客模式");

        session.Send(GamePf.OtherHouseInfoRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    private static OtherHouseInfoReq? TryParseReq(IGameSession session, Packet packet)
    {
        try
        {
            return SssjServer.Protocol.ProtoMessage.Parse<OtherHouseInfoReq>(packet.Payload);
        }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] 解析 OtherHouseInfoReq(1107) 失败：{e.Message}");
            return null;
        }
    }

    /// <summary>
    /// 组 pf 1108 的应答。五个必填字段全给（取证见类注释 §1/§2），<c>musicinfo</c> 省略。
    ///
    /// <para>★ <c>entrusttime</c> 固定 <b>0</b></para>
    /// <c>0x1D69F38 get_entrusttime → set_EntrustTime</c> 是无条件读的（所以必须写），
    /// 语义是「委托任务的结束时间戳」（<c>UI_CafeVisitMain.lua:377..383</c> 读
    /// <c>data.entrustendtime</c> 那一套）。委托子系统（<c>Entrust</c>）本服未实现，
    /// 0 = 「没有正在进行的委托」，是这一族最安全的值。
    /// </summary>
    internal static OtherHouseInfoRsp BuildResponse(FriendFeature.FriendBot bot,
                                                    CafeTable cafeTable, HouseTable houseTable)
    {
        var cafe = DeriveCafe(bot, cafeTable);
        var house = HouseFeature.CreateDefault(houseTable);

        // houseinfo：直接借用 pf 1049 的构造器（同一个 HouseCache 形状、同一套已验证约束：
        // 每件家具带独立 objPose、templateId 过表校验、objId ≥ 1000、roomList = {1,2,6}）。
        // ★ 只取它的 HouseInfo；GetHouseInfoRsp 的 roleid/visitinfo 不属于本消息。
        var houseRsp = HouseFeature.BuildResponse(bot.Roleid, house);

        return new OtherHouseInfoRsp
        {
            Roleid = bot.Roleid,
            // ★ 空串兜底纯属冗余防御，留着无害但别当成「必需」：
            // 取证 `OtherHouseInfoRsp..ctor`（0x337FA44）与 `Clear`（0x337F530）都把
            // rolename(+0x20) 设成常量 STR ""（@0x52A44B0）⇒ 客户端侧**不可能**读到 null，
            // 所以「xLua 把 null 转成 nil 会让 Text.set_text 抛」那条担心不成立。
            // 机器人名字来自 FriendFeature.BotNames 常量表，也不会为空。
            Rolename = string.IsNullOrEmpty(bot.RoleName) ? "" : bot.RoleName,
            Houseinfo = houseRsp.HouseInfo,
            Cafeinfo = cafe,
            Entrusttime = 0,
        };
    }

    /// <summary>
    /// 从机器人档案派生一份 <c>CafePb</c>。
    ///
    /// 四个标量都写（虽然客户端逐个 <c>has_</c> 判断、漏了不崩，但
    /// <c>NCafeDataController</c> 是跨场景不重置的单例，漏写会让客户端沿用
    /// **上一次访问**的值 —— 表现为「每个人的店等级都一样」）。
    ///
    /// 四个 repeated 字段一律空，理由见类注释 §7b。
    /// </summary>
    internal static CafePb DeriveCafe(FriendFeature.FriendBot bot, CafeTable table)
    {
        int level = 1 + (int)(Mix((uint)bot.Roleid, 0x51) % MaxDerivedLevel);

        // 与 CafeFeature.ReadState 同口径钳位（那边是硬约束，这边是防御 ——
        // MaxDerivedLevel 已经远低于 MaxCafeLevel，正常不会触发）
        int maxLevel = Math.Max(CafeFeature.MinLevel, table.MaxCafeLevel);
        if (level > maxLevel)
            level = maxLevel;
        if (level < CafeFeature.MinLevel)
            level = CafeFeature.MinLevel;

        // ★ 舒适度直接复用好友档案里那个 ComfortScore：好友列表 / 咖啡馆访问界面
        // 显示的就是它（FriendFeature.BuildFriendInfo 的 ["comfortscore"]），
        // 这里再派生一个不同的数会让「列表里写 3000、进店显示 800」自相矛盾。
        int comfort = Math.Clamp(bot.ComfortScore, 0, Math.Min(MaxDerivedComfort, table.MaxComfort));

        return new CafePb
        {
            Level = (uint)level,
            Hot = Mix((uint)bot.Roleid, 0x52) % 500u,
            Comfort = (uint)comfort,
            Basetime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            // seatlist / customerqueue / coffeelist / roomgirlslist 留空（§7b）
        };
    }

    // =================================================================
    //  pf 1109 HouseRandomListReq → pf 1110 HouseRandomListRsp
    // =================================================================

    /// <summary>
    /// pf 1109 的处理：回一串可随机访问的 roleid（「随便逛逛」的数据源）。
    ///
    /// 客户端消费只有一句（<c>OnHouseRandomListRsp</c> RVA <c>0x1D333B8</c> →
    /// <c>0x1D33438 rolelist → AddRandomList</c>），没有任何字段校验，
    /// 所以这里唯一要保证的是「**不含自己**」—— 含了会让那一次随机访问
    /// 撞上 <c>0x1D320B0</c> 静默 return（表现为「点了没反应」）。
    ///
    /// ★ 未登录时仍然回包（回空列表）：1109 是带 serial 的请求，
    /// 不回会让客户端回调悬挂；空列表的后果只是「随便逛逛」提示没有房间。
    /// </summary>
    internal Task HandleHouseRandomList(IGameSession session, Packet packet)
    {
        var rsp = new HouseRandomListRsp();

        if (session.Tag is Player player)
        {
            foreach (ulong id in RandomVisitTargets(player))
                rsp.Rolelist.Add(id);

            Log.Info($"[session {session.Id}] 随机访问列表下发 {rsp.Rolelist.Count} 个" +
                     $"（{string.Join(',', rsp.Rolelist)}）—— 「随便逛逛」的数据源，已剔除自己 {player.Roleid}");
        }
        else
        {
            Log.Warn($"[session {session.Id}] HouseRandomListReq(1109) 到达但会话未登录（Tag 非 Player），" +
                     "回空列表（不回会让客户端 serial 回调悬挂）");
        }

        session.Send(GamePf.HouseRandomListRsp, packet.Serial, rsp);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 可随机访问的目标 = 机器人池里全部**咖啡馆已解锁**的人，剔除自己。
    ///
    /// ★ 不限于好友：「随便逛逛」的语义就是逛陌生人的店
    /// （<c>UI_CafeVisitMain.lua:601..624</c> <c>visitRandom</c> 与「我的好友」页签是两条路）。
    /// ★ <see cref="FriendFeature.BotCafeFlag"/> 恒为 1（已解锁），所以现在是整池；
    /// 留下这个判断是为了将来 <c>cafeflag</c> 变成逐机器人属性时不用改这里。
    /// </summary>
    internal IEnumerable<ulong> RandomVisitTargets(Player player)
    {
        foreach (var bot in BotsOf(player))
        {
            if (bot.Roleid != player.Roleid && FriendFeature.BotCafeFlag > 0)
                yield return bot.Roleid;
        }
    }

    // =================================================================
    //  机器人池访问
    // =================================================================

    /// <summary>
    /// 取本玩家的机器人池。
    ///
    /// ★ 这里**不复用** <see cref="FriendFeature"/> 的实例（那是另一个 feature 对象、
    /// 有自己的进程内缓存），而是按同一套确定性算法重建：种子来自
    /// <see cref="FriendFeature.SeedOf"/>(account)，构造走
    /// <see cref="FriendFeature.BuildBots"/> —— 两者都是纯函数，
    /// 所以同一账号在两个 feature 里得到的池**逐字段相同**（有单测钉住这条）。
    ///
    /// 之所以不走存档里的 <c>Extra["friend"].seed</c>：那个键要等玩家第一次进好友域
    /// （<c>EnsureSeeded</c>）才写入，而「访问咖啡馆」理论上可以在那之前发生
    /// （比如从聊天链接进）。用 account 直接算种子对两条路都成立。
    /// </summary>
    internal List<FriendFeature.FriendBot> BotsOf(Player player)
    {
        uint seed = FriendFeature.SeedOf(player.Account);
        lock (_botCache)
        {
            if (_botCache.TryGetValue(seed, out var cached))
                return cached;
            var built = FriendFeature.BuildBots(seed, _cards);
            _botCache[seed] = built;
            return built;
        }
    }

    private readonly Dictionary<uint, List<FriendFeature.FriendBot>> _botCache = new();

    /// <summary>按 roleid 找机器人；不在池里返回 null（调用方据此拒绝请求）。</summary>
    internal FriendFeature.FriendBot? FindBot(Player player, ulong roleid)
        => BotsOf(player).FirstOrDefault(b => b.Roleid == roleid);

    /// <summary>
    /// 确定性混淆（splitmix32）。与 <see cref="FriendFeature"/> 里那个同算法 ——
    /// 保证「同一个好友的店，每次进去都一样」。
    /// </summary>
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
}
