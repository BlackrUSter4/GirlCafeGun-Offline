using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 「主城面板第一批」：登录进主城（<c>UI_Main:OnOpen</c>）后客户端立刻发、原先一律打
/// <c>unhandled LuaCall</c> 的那批命令里的 5 条。实测上行（work/server.log:27..57）：
/// <code>
/// cmd=94  (Player_GetVisitingCardData) tbParam=[]                      work/server.log:27
/// cmd=230 (Pldge_ReqIsOpen)            tbParam=[]                      work/server.log:30
/// cmd=170 (RequesRandomEvent)          tbParam={"type":"ShangXian"}    work/server.log:42
/// cmd=202 (GirlTest_OpenQi)            tbParam=[]                      work/server.log:51
/// cmd=231 (Pldge_ReqGirls)             tbParam=[]                      work/server.log:54
/// </code>
/// 注意 Lua 的空表被 <c>Json.Encode</c> 编成 <c>[]</c>（不是 <c>{}</c>），所以 tbParam
/// 可能是 JsonArray —— 本模块的读取一律走 <see cref="ReadInt"/>/<see cref="ReadString"/>，对数组安全。
///
/// ★ <b>cmd 80 <c>Friend_ReqList</c> 已迁出本模块</b>（原先这里只回 5 个空数组）：
/// 好友域整体在 <see cref="FriendFeature"/> 里实现（80..90 + 101，含确定性机器人好友）。
/// <c>LuaCallRouter.Map</c> 是 <c>_map[cmd] = handler</c>（<c>LuaCallRouter.cs:78..81</c>），
/// 两个 feature 同时注册 80 会让**后装配的静默覆盖前者**，而装配顺序来自
/// <c>GameFeatureLoader.Discover()</c> 的类型名排序（<c>GameFeature.cs:181..199</c>）
/// ⇒ 绝不能两边都注册。
///
/// 本批全部是「查询类」命令，原服都只是回一张表。共同结论（取证在各 handler 上）：
///  ① 客户端 <c>LuaCall:CallClient</c>（LuaCall.lua:512-519）在原服端会把 <c>nil</c> 兜成 <c>{}</c>、
///     把**数字**兜成 <c>{nError = n}</c> —— 所以客户端 else 分支永远拿到一张**非 nil 的表**，
///     我们必须至少回 <c>{}</c>，不能不回；
///  ② <c>LuaCall:Dispatcher</c>（LuaCall.lua:560-561）进门就 <c>Game.Blocking.Hide()</c>，
///     即「回包」本身还负责收掉转圈遮罩；
///  ③ Lua 里 <c>0</c> 和 <c>""</c> 都是 **truthy**，所以凡是客户端写
///     <c>if tbParam.nError then 弹错误框</c> 的命令，<b>绝不能</b>回 <c>nError=0</c>（见 cmd 94）。
///
/// 每玩家状态放 <c>Player.Extra["maincity"]</c>（随存档 round-trip，见 PlayerStore.PlayerDto.Extra），
/// 目前只有名片装扮三件套（cmd 94）。
/// </summary>
public sealed class MainCityFeature : IGameFeature
{
    // ---------------------------------------------------------------- LuaCall id
    // 取证：extracted/lua/LuaCall.lua 的 id 定义行（与 docs/luacall-table.json 一致）

    /// <summary>LuaCall.Player_GetVisitingCardData = 94，获取名片/个性装扮（LuaCall.lua:94）。</summary>
    public const int GetVisitingCardDataCmd = 94;

    /// <summary>LuaCall.RequesRandomEvent = 170，请求随机事件详情（LuaCall.lua:151）。</summary>
    public const int RequesRandomEventCmd = 170;

    /// <summary>LuaCall.GirlTest_OpenQi = 202，角色试玩开放期数（LuaCall.lua:175）。</summary>
    public const int GirlTestOpenQiCmd = 202;

    /// <summary>LuaCall.Pldge_ReqIsOpen = 230，誓约入口是否打开（LuaCall.lua:194）。</summary>
    public const int PldgeReqIsOpenCmd = 230;

    /// <summary>
    /// LuaCall.Pldge_ReqGirls = 231（<c>LuaCall.lua:195</c>）。
    /// ★ 第十五轮起**由 <see cref="PledgeFeature"/> 处理**（那里接真实存档数据）；
    /// 本常量仅留作文档对照，本模块**不再注册**它 —— <c>MapLuaCall</c> 是覆盖式的，
    /// 两处都注册会互相吃掉且无 unhandled 日志（AGENTS.md §5）。
    /// </summary>
    public const int PldgeReqGirlsCmd = PledgeFeature.CmdReqGirls;

    // ---------------------------------------------------------------- Player.Extra 约定

    /// <summary><see cref="Player.Extra"/> 里本模块的 key。</summary>
    internal const string ExtraKey = "maincity";

    /// <summary>Extra["maincity"]["card"] 子节点：名片装扮三件套。</summary>
    internal const string CardNodeKey = "card";

    /// <summary>名片皮肤 id（LuaCall.Player_SetVisitingCard = 91 设置，LuaCall.lua:91）。</summary>
    internal const string VisitingCardIdKey = "VisitingCardID";

    /// <summary>玩家列表背景皮肤 id（LuaCall.Player_SetPlayerListSkin = 92，LuaCall.lua:92）。</summary>
    internal const string PlayerListSkinIdKey = "PlayerListSkinID";

    /// <summary>聊天气泡 id（LuaCall.Player_SetChatBubble = 93，LuaCall.lua:93）。</summary>
    internal const string ChatBubbleIdKey = "ChatBubbleID";

    // ---------------------------------------------------------------- 本服的“关闭/为空”常量

    /// <summary>
    /// 角色试玩当前开放期数。0 = 未开放：客户端 <c>GirlTestLogic:IsOpen()</c>
    /// （GirlTestLogic.lua:37-41）第一条就是 <c>if self._OpenID == 0 then return false,1</c>。
    /// 本服没有实现角色试玩活动，必须回 0；回 nil 会让
    /// <c>GetGirlTest_Team_Now</c>（GirlTestLogic.lua:125）拿 <c>tb_GirlTest_Team[nil]</c> 再索引而崩。
    /// </summary>
    internal const int GirlTestClosedQi = 0;

    /// <summary>The only period in GirlTest.txt. EventMiscFeature serves it when every event is open.</summary>
    internal const int GirlTestOpenQi = 1;

    private bool _girlTestOpen;

    /// <summary>
    /// 誓约入口是否开放。
    ///
    /// <para><b>★★★ 第十五轮改为 true（推翻旧结论）</b></para>
    /// 这里原先写死 <c>false</c>，理由是「打开后玩家点进去就会加载**本服没实现的**誓约地图」。
    /// **那条理由已被实测证伪**：
    /// <code>
    /// 真机 CS.Game.Map.Find(11001)     → 有效 Info，scene = "Promise"
    /// 设备 .../files/map/               → mappromise_prefab.u  存在
    /// 资源站 work/assets-serve/map/     → mappromise_prefab.u  存在
    /// grep -c mappromise version.data   → 2（清单已登记）
    /// </code>
    /// ★ 当初误判是因为用了**不存在的 API** <c>Map.GetInfo</c>（正确是 <c>Map.Find</c>，
    /// <c>dump.cs:555419</c>）拿到 nil ⇒ <b>判「数据不存在」前要先用已知存在的同类值
    /// （map 2/6/7）验证查询手段本身</b>。
    ///
    /// 誓约业务逻辑现由 <see cref="PledgeFeature"/> 实现（231/232/233/234/235）。
    ///
    /// ★ 仍然必须是 JSON 布尔 —— Lua 里 <c>0</c> 是 truthy，类型错会让语义反转。
    /// 客户端另有一道 <c>SystemUnlock:GetUnlock('UI_UnlockPromise')</c>
    /// （<c>GirlPromise.lua:31</c>），GM 全解锁号真机自报为 <c>true</c>。
    /// </summary>
    internal const bool PromiseOpen = true;

    /// <summary>回包通道（<c>ctx.LuaCalls.CallClient</c> → pf 1024 NtfS2CCall），在 Install 时注入。</summary>
    private LuaCallRouter _luaCalls = null!;

    public string Name => "MainCity 主城面板第一批（名片装扮/随机事件/角色试玩/誓约）";

    public void Install(GameFeatureContext ctx)
    {
        _luaCalls = ctx.LuaCalls;
        ctx.MapLuaCall(GetVisitingCardDataCmd, OnGetVisitingCardData);
        ctx.MapLuaCall(RequesRandomEventCmd, OnRequesRandomEvent);
        _girlTestOpen = ctx.Options.AllEventsOpen;
        ctx.MapLuaCall(GirlTestOpenQiCmd, OnGirlTestOpenQi);
        ctx.MapLuaCall(PldgeReqIsOpenCmd, OnPldgeReqIsOpen);
        // ★★ 231 已移交 PledgeFeature（那里接真实存档数据；这里原本只回空列表）。
        //    ★ MapLuaCall 是**覆盖式**的，两处都注册会让「后装配者吃掉前者」且无 unhandled 日志
        //      （AGENTS.md §5 那条架构坑）⇒ 必须在这里摘掉，不能靠装配顺序碰运气。
    }

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        GetVisitingCardDataCmd, RequesRandomEventCmd,
        GirlTestOpenQiCmd, PldgeReqIsOpenCmd,
    };

    // ================================================================ 94 Player_GetVisitingCardData

    /// <summary>
    /// cmd 94 获取名片 / 个性装扮。
    ///
    /// 服务端分支（LuaCall.lua:1191-1194）：<c>Player:GetVisitingCardData()</c> → <c>CallClient</c>。
    /// LuaCall.lua:1189 的注释直接给出了回包形状：
    /// <c>{ChatBubbleID:5, VisitingCardID:1, PlayerListSkinID:3, }</c> —— 三个字段、这个大小写。
    ///
    /// 客户端分支（LuaCall.lua:1195-1203）按当前打开的界面三分流，三条都必须成立：
    /// <code>
    /// ① UI_SkinSelected:OnRecvId(tbParam)     UI_SkinSelected.lua:91-106
    ///    三个字段都是 `if tbParam.X and tbParam.X > 0`，缺字段安全，回 0 = 都没穿
    /// ② UI_User:OnChangeSkin(tbParam)         UI_User.lua:1294-1312
    ///    1296  if tbParam and tbParam.nError then 弹 ui.Error.<nError> 错误框
    ///          ★ Lua 里 0 是 truthy → 本回包**绝不能**带 nError（哪怕 0）
    ///    1306  if tbParam.ChatBubbleID and tbParam.PlayerListSkinID then ...
    ///    1310  self.skinId = tbParam.VisitingCardID or tbParam[1]
    ///    935/947 OpenInfoShow(): `if self.skinId > 0`
    ///          ★ 所以 VisitingCardID **必须**存在：两个都 nil 时 skinId=nil，
    ///            nil > 0 直接抛「attempt to compare nil with number」
    /// ③ CardCommon:UpdateDressUpInfo(tbParam) CardCommon.lua:1026-1069
    ///    1027 `if not tbParam then return end`；1051/1057/1063 都是 `X and X > 0`。
    ///    ★ 只要某个 id > 0 且客户端配置里查不到/已过期，它会反手
    ///      CallServer(Player_SetVisitingCard/…, 0)（1054/1060/1066）—— 那是 91/92/93，
    ///      本批没实现，会再打 unhandled。所以未取证的装扮 id 一律回 0（本服不发装扮）。
    /// </code>
    /// 进主城走的是 ③（UI_Main.lua:676，此时 UI_SkinSelected/UI_User 都没开）。
    /// </summary>
    internal Task OnGetVisitingCardData(ClientSession session, int cmd, JsonNode? param)
    {
        var state = ReadCardState(session.Tag as Player);
        Log.Info($"[session {session.Id}] Player_GetVisitingCardData(94) → "
                 + $"名片={state.VisitingCardId} 列表皮肤={state.PlayerListSkinId} 气泡={state.ChatBubbleId}");
        _luaCalls.CallClient(session, cmd, BuildVisitingCardPayload(state));
        return Task.CompletedTask;
    }

    /// <summary>名片装扮三件套（cmd 94 的回包内容，也是 Extra["maincity"]["card"] 的存档形状）。</summary>
    internal readonly record struct CardState(int VisitingCardId, int PlayerListSkinId, int ChatBubbleId);

    /// <summary>
    /// 名片装扮回包：三个字段固定齐全，**不带 nError**（见 <see cref="OnGetVisitingCardData"/> 的 ② 号取证）。
    /// </summary>
    internal static JsonObject BuildVisitingCardPayload(CardState state) => new()
    {
        // 字段名与大小写取证：LuaCall.lua:1189 注释 + UI_SkinSelected.lua:97-104 + CardCommon.lua:1051-1063
        [VisitingCardIdKey] = state.VisitingCardId,
        [PlayerListSkinIdKey] = state.PlayerListSkinId,
        [ChatBubbleIdKey] = state.ChatBubbleId,
    };

    /// <summary>
    /// 从 <c>Player.Extra["maincity"]["card"]</c> 读名片装扮；没有存档一律 0（= 没穿任何装扮）。
    /// 0 是安全值：客户端三条分支都是 <c>&gt; 0</c> 才去查配置表（见 OnGetVisitingCardData 取证）。
    /// </summary>
    internal static CardState ReadCardState(Player? player)
    {
        JsonNode? card = null;
        if (player is not null
            && player.Extra.TryGetValue(ExtraKey, out var node)
            && node is JsonObject root)
        {
            // 只认 JsonObject：存档被手改成别的形状时退回全 0，不抛异常
            card = root[CardNodeKey];
        }
        return new CardState(
            ReadInt(card, VisitingCardIdKey, 0),
            ReadInt(card, PlayerListSkinIdKey, 0),
            ReadInt(card, ChatBubbleIdKey, 0));
    }

    /// <summary>
    /// 写 <c>Player.Extra["maincity"]["card"]</c>（调用方负责 <c>PlayerStore.Save</c>）。
    /// 本批 6 个命令都是只读查询，这个写入口是给后续 LuaCall 91/92/93
    /// （Player_SetVisitingCard / SetPlayerListSkin / SetChatBubble，LuaCall.lua:91-93）预留的落点，
    /// 先固定存档位置，避免它们再各挑一个 key。
    /// </summary>
    internal static void WriteCardState(Player player, CardState state)
    {
        if (player.Extra.TryGetValue(ExtraKey, out var existing) && existing is JsonObject root)
        {
            root[CardNodeKey] = BuildVisitingCardPayload(state);
            return;
        }
        player.Extra[ExtraKey] = new JsonObject { [CardNodeKey] = BuildVisitingCardPayload(state) };
    }

    // ================================================================ 170 RequesRandomEvent

    /// <summary>
    /// cmd 170 请求随机事件（咖啡馆里冒泡的那种小事件）。
    ///
    /// 服务端分支（LuaCall.lua:2618-2621）：<c>ServerRandomEvent:GetRandomEvent(tbParam)</c> → <c>CallClient</c>。
    /// 上行两种（都是同一个 handler）：
    /// <code>
    /// {type='ShangXian'}  上线时一次   RandomEvent.lua:50（RandomEvent:Init，由 UI_Main.lua:742 RandomEvent:Star 触发）
    /// {type='ZhengDian'}  整点轮询     RandomEvent.lua:66
    /// </code>
    ///
    /// 客户端分支：<c>RandomEvent:OnReRandomEvent(tbParam)</c>（RandomEvent.lua:71-80）：
    /// <code>
    /// 72  self.RandomEvent_Info = tbParam.random_event_ID   ← tbParam 被直接索引，必须是表
    /// 73  if self.RandomEvent_Info == nil then 「该次未戳中随机事件」（什么都不做）
    /// 76  else 读 .ID → self.RandomEvent_ID_Now → DoRandomEvent()
    /// </code>
    /// 本服不产生随机事件，因此回**空表**：<c>random_event_ID</c> 为 nil，客户端走 73 行的
    /// “本次没戳中”分支，后续 <c>ShowControl</c>(RandomEvent.lua:120) 与
    /// <c>DoRandomEvent</c>(84) 都有 nil/0 前置判断，安全。
    /// 只有事件真的存在时才需要 <c>{random_event_ID={ID,Type,Detail,Plot,GDPLN}}</c>
    /// （字段见 RandomEvent.lua:77/123/125/135/158/188/233/290/344），这些值需要配置表取证，本批不填。
    /// </summary>
    internal Task OnRequesRandomEvent(ClientSession session, int cmd, JsonNode? param)
    {
        string type = ReadString(param, "type", "");
        Log.Info($"[session {session.Id}] RequesRandomEvent(170) type={(type.Length == 0 ? "(空)" : type)}"
                 + " → 回空表（本次未触发随机事件）");
        _luaCalls.CallClient(session, cmd, BuildRandomEventPayload());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 随机事件回包：空对象。<c>tbParam.random_event_ID == nil</c> 即
    /// RandomEvent.lua:73 的“本次未戳中随机事件”，是唯一“空但合法”的答案。
    /// </summary>
    internal static JsonObject BuildRandomEventPayload() => new();

    // ================================================================ 202 GirlTest_OpenQi

    /// <summary>
    /// cmd 202 角色试玩「开放期数」。
    ///
    /// 服务端分支（LuaCall.lua:2845-2848）：<c>ActivitiesLogic:Get_GirlTest_OpenQi(tbParam)</c> → <c>CallClient</c>
    /// （该函数是纯服务端部分，随包的 ActivitiesLogic.lua 里没有）。
    /// 上行 <c>{}</c>：GirlTestLogic.lua:50（由 UI_Main.lua:750 触发）。
    ///
    /// 客户端分支：<c>GirlTestLogic:OnReceive_GirlTest_OpenQi_Now(tbParam)</c>（GirlTestLogic.lua:53-57）：
    /// <code>
    /// 54  if tbParam then
    /// 55      self._OpenID = tbParam.Result      ← 唯一字段，名字就是 Result
    /// </code>
    /// 必须给 0 而不是省略：<c>_OpenID</c> 会被
    /// <c>IsOpen()</c>（GirlTestLogic.lua:39 `if self._OpenID == 0`）、
    /// <c>GetGirlTest_Now()</c>（114 `tb_GirlTest[self._OpenID]`）、
    /// <c>GetGirlTest_Team_Now()</c>（125 `tb_GirlTest_Team[self._OpenID][level]`）读。
    /// 回 nil 时 125 行会对 nil 再索引而抛错；回 0 则在 39 行就短路成「未开放」。
    /// </summary>
    internal Task OnGirlTestOpenQi(ClientSession session, int cmd, JsonNode? param)
    {
        Log.Info($"[session {session.Id}] GirlTest_OpenQi(202) → Result={GirlTestClosedQi}（角色试玩未开放）");
        _luaCalls.CallClient(session, cmd, BuildGirlTestOpenQiPayload(_girlTestOpen ? GirlTestOpenQi : GirlTestClosedQi));
        return Task.CompletedTask;
    }

    /// <summary>角色试玩开放期数回包：<c>{Result:&lt;期数&gt;}</c>，0 = 未开放（GirlTestLogic.lua:55/39）。</summary>
    internal static JsonObject BuildGirlTestOpenQiPayload(int openQi) => new()
    {
        ["Result"] = openQi,
    };

    // ================================================================ 230 Pldge_ReqIsOpen

    /// <summary>
    /// cmd 230 誓约入口是否开放。
    ///
    /// 服务端分支（LuaCall.lua:2986-2989）：<c>Pldge:GetPromiseOpen()</c> → <c>CallClient</c>。
    /// 上行 <c>{}</c>：GirlPromise.lua:524（调用点 UI_Main.lua:700 / UI_FightMode.lua:236 / UI_PopOut.lua:25）。
    ///
    /// 客户端分支：<c>GirlPromise:RecivePromiseTimeIsOpen(tbParam)</c>（GirlPromise.lua:528-537）：
    /// <code>
    /// 529  if not tbParam then return end
    /// 530  Dump(tbParam)
    /// 532  GirlPromise.PromiseOpen = tbParam.PromiseIsOpen    ← 唯一字段
    /// 534  if UI.IsOpen('UI_FightMode') then RefreshPromiseLock()
    /// </code>
    /// 消费方 <c>IsPromiseOpen()</c>（GirlPromise.lua:26）：<c>if not GirlPromise.PromiseOpen then</c>
    /// 提示 <c>ui.UI_Girl.Tips3</c> 并阻止 <c>Go2Promise → Map.LoadById(11001)</c>（39-47）。
    /// ★ 必须回 JSON 布尔 <c>false</c>：Lua 里 <c>0</c> 是 truthy，回 0 等于宣布“已开放”，
    /// 玩家点誓约就会去加载本服完全没实现的 11001 地图。
    /// </summary>
    internal Task OnPldgeReqIsOpen(ClientSession session, int cmd, JsonNode? param)
    {
        Log.Info($"[session {session.Id}] Pldge_ReqIsOpen(230) → PromiseIsOpen={PromiseOpen}" +
                 $"（誓约入口{(PromiseOpen ? "已开放" : "关闭")}）");
        _luaCalls.CallClient(session, cmd, BuildPldgeIsOpenPayload(PromiseOpen));
        return Task.CompletedTask;
    }

    /// <summary>誓约入口回包：<c>{PromiseIsOpen:&lt;bool&gt;}</c>（GirlPromise.lua:532）。</summary>
    internal static JsonObject BuildPldgeIsOpenPayload(bool open) => new()
    {
        ["PromiseIsOpen"] = open,
    };

    // ================================================================ 231 Pldge_ReqGirls


    // ================================================================ 小工具

    /// <summary>
    /// 从 tbParam 里读 int。tbParam 可能是 JsonArray（Lua 空表被 Json.Encode 编成 <c>[]</c>，
    /// 见 work/server.log:27 的 <c>"tbParam":[]</c>），也可能是数字字符串，全部兜底成 fallback。
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
            // Lua 侧可能给 float/字符串
            return double.TryParse(v.ToString(), out var d) ? (int)d : fallback;
        }
    }

    /// <summary>从 tbParam 里读字符串（同 <see cref="ReadInt"/> 的容错口径）。</summary>
    internal static string ReadString(JsonNode? node, string name, string fallback)
    {
        if (node is not JsonObject obj || obj[name] is not JsonNode v)
            return fallback;
        try
        {
            return v.GetValue<string>();
        }
        catch
        {
            return v.ToString();
        }
    }
}
