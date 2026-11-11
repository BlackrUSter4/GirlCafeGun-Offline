namespace SssjServer.Game;

/// <summary>
/// 关卡 id（mapId）与星级位域的编解码，与客户端 <c>Game.MapList</c> 完全一致。
///
/// 取证：
///  * <c>Game.MapList.GetMapId(chapter, index, diff)</c>（RVA <c>0x1D4C640</c>）反汇编末尾即公式：
///    <code>
///    lsl w8, w20, #8            ; index &lt;&lt; 8
///    orr w8, w8, w21, lsl #16   ; | chapter &lt;&lt; 16
///    orr w0, w8, w19            ; | diff
///    </code>
///    即 <c>mapId = (chapter &lt;&lt; 16) | (index &lt;&lt; 8) | diff</c>。
///  * <c>Game.MapList.GetLevel(mapId)</c>（RVA <c>0x1D4C768</c>）读 <c>Game.Player.me</c> 的
///    <c>Dictionary&lt;uint,int&gt;</c>（字段偏移 0x78），缺失返回 -1。
///  * <c>Game.MapList.ReflashLevelList(mapId, star)</c>（RVA <c>0x1D4C8C0</c>）就是该字典的
///    <c>set_Item(mapId, star)</c> —— **整值覆盖**，不做合并，所以服务端下发的 star
///    必须是完整位域。
///  * 位域含义来自 <c>extracted/lua/LevelManager.lua:278 GetLevelState</c>：
///    <code>
///    starOne  = LoadBits(star, 0, 0);
///    starTwo  = LoadBits(star, 1, 1);
///    starThree= LoadBits(star, 2, 2);
///    passNum  = LoadBits(star, 3, 31);
///    </code>
///    即 bit0..2 是三颗星，bit3..31 是通关次数。
/// </summary>
public static class MapList
{
    /// <summary>三颗星所在的低位掩码。</summary>
    public const int StarMask = 0b111;

    /// <summary>通关次数在位域中的起始位。</summary>
    public const int PassCountShift = 3;

    /// <summary>(chapter, index, diff) → mapId。</summary>
    public static uint MapId(int chapter, int index, int diff)
        => unchecked((uint)((chapter << 16) | ((index & 0xFF) << 8) | (diff & 0xFF)));

    /// <summary>mapId → (chapter, index, diff)。</summary>
    public static (int Chapter, int Index, int Diff) Decode(uint mapId)
        => ((int)(mapId >> 16), (int)((mapId >> 8) & 0xFF), (int)(mapId & 0xFF));

    /// <summary>取位域里的三星掩码（bit0..2）。</summary>
    public static int Stars(int star) => star & StarMask;

    /// <summary>取位域里的通关次数（bit3..31）。</summary>
    public static int PassCount(int star) => (int)((uint)star >> PassCountShift);

    /// <summary>把三星掩码与通关次数组合成客户端认的位域值。</summary>
    public static int Compose(int stars, int passCount)
        => (stars & StarMask) | (passCount << PassCountShift);
}
