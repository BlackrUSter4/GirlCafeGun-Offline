using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 聊天域用到的两张「装扮 id 白名单」配置表。
///
/// 存在的**唯一理由**是 `work/chat-domain-forensics.md` 陷阱 T2：
/// pf 1012 `NoticeChattingMsg.headframe` 是**组合值** `头像框id * 1000 + 气泡id`
/// （取证 <c>PlayerLogic.lua:180..185</c>），而客户端渲染头像框那一行**没有 nil 保护**：
///
/// <code>
/// -- extracted/lua/UI_Chat.lua:655..667（ShowLeft；:743..755 ShowRight 同构）
/// local tbHeadCfg = self.Borders[CardCommon:GetBorderID(tbInfo.headid)];
/// if tbHeadCfg then ... end                    -- 只用来决定 SetActive
/// imgBorder.sprite = Sprite(tbHeadCfg.Icon);   -- ★★ 无 nil 保护，nil 直接抛
/// </code>
///
/// `self.Borders` 的键来自 <c>HeadFrame.txt</c>（<c>CardCommon.lua:165..171</c>），
/// 而 <c>CardCommon:GetBorderID(id)</c>（<c>CardCommon.lua:862..867</c>）**只把 0 映射成 1**、
/// 其它原样返回 ⇒ 服务端只要发一个表里没有的头像框 id，那条聊天消息就会
/// 抛 <c>attempt to index a nil value (local 'tbHeadCfg')</c> 渲染中断。
///
/// 现网 <c>HeadFrame.txt</c> 的 110 个 ID **不连续**（`1..15, 20..25, 30..59, 61..69, 101..150`，
/// 缺 16..19 / 26..29 / 60 / 70..100），所以「小于某个上界」这种判断是错的，必须真读表。
///
/// 气泡侧（<c>Babble.txt</c>）客户端**有**守卫
/// （<c>UI_Chat.lua:682</c> / <c>:761</c> 的 <c>and CardCommon.tbBabbleConfig[bubbleid]</c>），
/// 命不中只是退化成普通气泡、不崩；这里一并读进来是为了让日志能说清「为什么退成 0」。
/// </summary>
public sealed class ChatTable
{
    /// <summary>空表（缺文件时的降级值，也是单测基线）。空表下一切装扮 id 都会被退成 0。</summary>
    public static ChatTable Empty { get; } = new();

    /// <summary>头像框表文件名（<c>extracted/setting/HeadFrame.txt</c>）。</summary>
    public const string HeadFrameFileName = "HeadFrame.txt";

    /// <summary>聊天气泡表文件名（<c>extracted/setting/Babble.txt</c>）。</summary>
    public const string BabbleFileName = "Babble.txt";

    /// <summary>
    /// 「没有头像框」的安全值。取证 <c>CardCommon.lua:862..867</c>：
    /// <c>GetBorderID(0)</c> 返回 **1**，而 1 一定在 <c>HeadFrame.txt</c> 里
    /// ⇒ 发 0 永远安全，这也是新号的正常值。
    /// </summary>
    public const uint SafeHeadFrameId = 0;

    private readonly HashSet<uint> _headFrames = new();
    private readonly HashSet<uint> _babbles = new();

    /// <summary><c>HeadFrame.txt</c> 里合法的头像框 ID 全集。</summary>
    public IReadOnlyCollection<uint> HeadFrames => _headFrames;

    /// <summary><c>Babble.txt</c> 里合法的聊天气泡 ID 全集。</summary>
    public IReadOnlyCollection<uint> Babbles => _babbles;

    /// <summary>该头像框 id 是否能安全下发（0 恒安全，见 <see cref="SafeHeadFrameId"/>）。</summary>
    public bool IsHeadFrameSafe(uint id) => id == SafeHeadFrameId || _headFrames.Contains(id);

    /// <summary>该气泡 id 是否在表里（不在也不崩，只是退化成普通气泡）。</summary>
    public bool IsBabbleKnown(uint id) => _babbles.Contains(id);

    /// <summary>
    /// 把「头像框 id + 气泡 id」编成 pf 1012 的 <c>headframe</c> 字段。
    ///
    /// 规则 <c>PlayerLogic.lua:183..184</c>：<c>id1 = floor(headframe/1000)</c>、<c>id2 = headframe%1000</c>。
    /// 不安全的头像框 id 一律退成 <see cref="SafeHeadFrameId"/>（否则客户端抛，见类注释）；
    /// 气泡 id 超过 999 会串到头像框位上，同样退 0。
    ///
    /// ★ 另一个坑（取证 <c>UI_Chat.lua:649..653</c> / <c>:737..741</c>）：客户端对
    /// <c>headid >= 1000</c> 会**再拆一次**（繁中兜底路径），所以头像框 id 本身必须 &lt; 1000，
    /// 现网表里最大 150，天然满足。
    ///
    /// 气泡侧同样过白名单：客户端那里虽然**有**守卫（不崩），但表外 id 只会被无声忽略，
    /// 不如在服务端就退成 0，让日志能说清「为什么没有气泡」。
    /// </summary>
    public uint ComposeHeadframe(uint headFrameId, uint babbleId)
    {
        uint frame = IsHeadFrameSafe(headFrameId) && headFrameId < 1000 ? headFrameId : SafeHeadFrameId;
        uint bubble = babbleId < 1000 && IsBabbleKnown(babbleId) ? babbleId : 0;
        return frame * 1000 + bubble;
    }

    public static ChatTable Load(string settingDir)
    {
        var table = new ChatTable();
        table.LoadIds(Path.Combine(settingDir, HeadFrameFileName), table._headFrames, HeadFrameFileName);
        table.LoadIds(Path.Combine(settingDir, BabbleFileName), table._babbles, BabbleFileName);

        Log.Info($"[setting] 聊天装扮白名单：头像框 {table._headFrames.Count} 个" +
                 $"（★ HeadFrame.txt 的 ID 不连续，发表外 id 会让客户端 UI_Chat.lua:667 抛）、" +
                 $"聊天气泡 {table._babbles.Count} 个");
        return table;
    }

    /// <summary>读表的第 1 列（ID 列）。两张表都是「第 1 列是 ID、前 3 行是表头」的标准格式。</summary>
    private void LoadIds(string path, HashSet<uint> into, string what)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[setting] 找不到 {what}，聊天装扮白名单降级为空（headframe 一律发 0，安全但没有头像框）");
            return;
        }

        int n = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (++n <= 3)
                continue;
            var cells = line.Split('\t');
            if (cells.Length == 0 || !uint.TryParse(cells[0].Trim(), out uint id))
                continue;
            into.Add(id);
        }
    }
}
