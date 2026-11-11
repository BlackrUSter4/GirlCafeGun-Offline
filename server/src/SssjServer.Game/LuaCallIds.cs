namespace SssjServer.Game;

/// <summary>
/// 服务端会**主动下发**或跨功能域共用的 LuaCall 数字命令 id。
///
/// 与 <c>LuaCallNames.g.cs</c>（354 个 id 的名称表，只用于日志）区分：那张表是自动生成的
/// 「id → 名字/中文注释」查表，本类是**代码里真的会引用**的少量常量，带取证。
/// 各功能域自己独占的 id 请放在对应 Feature 类里（如 <c>BagFeature.CmdSellItem</c>）。
/// </summary>
public static class LuaCallIds
{
    /// <summary>
    /// 75 <c>ItemBox_Open</c>「获得物品」弹窗 —— **纯服务端推送**，客户端无上行。
    /// 定义 <c>LuaCall.lua:74</c>，注册 <c>LuaCall.lua:1302</c>，客户端分支
    /// <c>ItemBox:OnOpenBox</c>（<c>ItemBox.lua:53</c>）。tbParam = <c>[[G,D,P,L,N],…]</c>。
    /// 见 <see cref="GameNotify.ShowRewardBox(Core.LuaCallRouter, Core.ClientSession, System.Collections.Generic.IReadOnlyList{int[]})"/>。
    /// </summary>
    public const int ItemBoxOpen = 75;

    /// <summary>
    /// 76 <c>ItemBox_Open_MO</c>「手动开箱」（有上行：<c>UI_ItemBox.lua:332</c> 等）。
    /// 上行 tbParam = <c>{gdpln:[G,D,P,L,num], select:<自选项>}</c>；
    /// 下行元素是 8 元 <c>[G,D,P,L,N,bHasGirl,bHasSuit,bHasCard]</c>（<c>ItemBox.lua:157..167</c>）。
    /// **未实现**（需要 <c>Item/ItemBoxInfo.txt</c> 的掉落池，见 <c>ItemBox.lua:17..47</c>）。
    /// </summary>
    public const int ItemBoxOpenManual = 76;

    /// <summary>
    /// 200 <c>MissionNotify</c>「成就和任务通知」—— 纯服务端推送（<c>LuaCall.lua:2828</c>
    /// 只有 <c>MODULE_CLIENT</c> 分支）。实现见 <c>MissionFeature.TryPushMissionNotify</c>。
    /// </summary>
    public const int MissionNotify = 200;

    /// <summary>
    /// 252 <c>DoSettle</c>「结算统一外壳」：<c>{sCmd=<内层字符串命令>, tbParam={…}}</c>。
    /// 客户端 else 分支（<c>LuaCall.lua:3187</c>）会先清 <c>GameManager.tbMissionParam/sMissionCmd</c>
    /// （避免重连重发）再转 <c>S2C[sCmd]</c>。实现见 <c>ChapterHandler.DoSettleCmd</c>。
    /// </summary>
    public const int DoSettle = 252;

    /// <summary>999999 <c>DoLuaString</c>「服务器指定客户端执行 Lua」（GM 通道，<c>LuaCall.lua:489</c>）。</summary>
    public const int DoLuaString = 999999;
}
