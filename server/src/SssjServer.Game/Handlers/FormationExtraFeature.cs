using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 编队更新的另外两个入口：<b>LuaCall 22 <c>ChooseEquip_Update</c></b>（选择武器界面）与
/// <b>LuaCall 23 <c>ChooseGirl_Update</c></b>（选择角色卡界面）。
///
/// <para><b>0. 为什么它们和 21 是同一件事</b></para>
/// 三个命令在原服**共用同一个函数**，上行参数也完全一样 —— 取证
/// <c>extracted/lua/LuaCall.lua</c>：
/// <code>
/// -- :741 请求更新编队配置                （cmd 21 Formation_Update，已由 FormationHandler 实现）
/// -- :760 请求更新编队配置(选择角色卡界面)  cmd 23
/// RegisterCMD(LuaCall.ChooseGirl_Update, function(sCmd, tbParam)
///     if MODULE_SERVER then
///         local tbRet = FormationLogic:UpdateFormation(tbParam.Id, tbParam.Info);   -- ★ 同一个
///         LuaCall:CallClient(sCmd, tbRet);
///     else
///         UI.Call("UI_ChooseGirl", "OnUpdateEnd");
///         if UI.IsOpen('UI_Formation') then UI.Call("UI_Formation", "UpdateTips"); end
///         if UI.IsOpen("UI_BattleMain2") then UI.Call("UI_BattleMain2", "UpdateSolution"); end
///     end
/// end)
/// -- :776 请求更新编队配置(选择武器界面)    cmd 22
/// RegisterCMD(LuaCall.ChooseEquip_Update, function(sCmd, tbParam)
///     if MODULE_SERVER then
///         local tbRet = FormationLogic:UpdateFormation(tbParam.Id, tbParam.Info);   -- ★ 同一个
///         LuaCall:CallClient(sCmd, tbRet);
///     else
///         UI.Call("UI_ChooseEquip", "OnUpdateEnd");
///         if UI.IsOpen('UI_Formation') then UI.Call("UI_Formation", "UpdateTips"); end
///     end
/// end)
/// </code>
/// 差别只在**客户端收到回包后刷新哪个界面**（21→UI_Formation、22→UI_ChooseEquip、
/// 23→UI_ChooseGirl+UI_BattleMain2），服务端要做的事一模一样：
/// 存 <c>{Id, Info}</c> → 推 pf 1033 → 回执。
///
/// <para><b>1. ★ 回执的 sCmd 必须与上行一致</b></para>
/// 客户端是 <c>LuaCall:CallClient(sCmd, tbRet)</c> —— <b>回原样的 sCmd</b>。
/// 如果 22 的请求回成 21，客户端会去刷 <c>UI_Formation</c> 而不是 <c>UI_ChooseEquip</c>，
/// 表现就是「在选武器界面点了确定，界面不关、也不刷新」。
/// 所以这里不能直接调 <see cref="FormationHandler.OnUpdate"/>（它硬编码回 21），
/// 而是复用它的存档逻辑后按 <paramref name="cmd"/> 回执。
///
/// <para><b>2. 为什么单独成一个 IGameFeature</b></para>
/// <see cref="FormationHandler"/> 是早期写法，由 <c>GameModule</c> 手工 new + 注册；
/// 本类走 <see cref="IGameFeature"/> 反射装配，**不需要改 GameModule**
/// （<c>ctx.Store</c> / <c>ctx.LuaCalls</c> 已经把它要的两个依赖都给了）。
///
/// <para><b>3. 真机频次</b></para>
/// 这两个命令在本轮真机日志里累计 <b>17 次 unhandled</b>（23 十二次 / 22 五次），
/// 是「客户端真的会发但服务端没实现」里排第二的（第一是商店 44 次），
/// 而且它卡在**出击前换卡/换武器**这一步 —— 见 <c>work/unhandled-roadmap.md</c>。
/// </summary>
public sealed class FormationExtraFeature : IGameFeature
{
    /// <summary>LuaCall.ChooseEquip_Update = 22（LuaCall.lua:27，选择武器界面点确定）。</summary>
    public const int ChooseEquipUpdateCmd = 22;

    /// <summary>LuaCall.ChooseGirl_Update = 23（LuaCall.lua:28，选择角色卡界面点确定）。</summary>
    public const int ChooseGirlUpdateCmd = 23;

    private FormationHandler _inner = null!;

    public string Name => "编队更新的另两个入口（LuaCall 22 ChooseEquip / 23 ChooseGirl）";

    public void Install(GameFeatureContext ctx)
    {
        // 复用 FormationHandler 的存档 + pf 1033 推送逻辑（它要 PlayerStore 与 LuaCallRouter）
        _inner = new FormationHandler(ctx.Store, ctx.LuaCalls);

        ctx.MapLuaCall(ChooseEquipUpdateCmd, Handle);
        ctx.MapLuaCall(ChooseGirlUpdateCmd, Handle);
    }

    /// <summary>
    /// 22/23 的公共处理：直接转给 <see cref="FormationHandler.OnUpdate"/>。
    /// 那个函数已经按传入的 <paramref name="cmd"/> 回执（本轮顺手修正的一处不一致：
    /// 它先前硬编码回 21），所以这里**只需原样转发**，不要再补发第二条回执。
    /// </summary>
    private Task Handle(ClientSession session, int cmd, JsonNode? param)
    {
        string which = cmd == ChooseEquipUpdateCmd ? "UI_ChooseEquip（选武器）" : "UI_ChooseGirl（选角色卡）";
        Log.Info($"[session {session.Id}] 编队更新 cmd={cmd}（{which}）→ 复用 Formation_Update 逻辑");
        return _inner.OnUpdate(session, cmd, param);
    }
}
