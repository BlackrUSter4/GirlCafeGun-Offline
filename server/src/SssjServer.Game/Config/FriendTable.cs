using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 好友上限分档表（客户端路径 <c>Friend/Friend.txt</c>，本仓库扁平化成
/// <c>extracted/setting/Friend.txt</c>，**UTF-8 编码**，151 行数据）。
///
/// <para><b>为什么必须有这张表（不能自定一个常量）</b></para>
/// 客户端在 6 处自己算好友上限，全部走 <c>FriendLogic:GetFriendNumLimit(me.level)</c>：
/// <list type="bullet">
///   <item><c>UI_Friend.lua:166</c> 页头 <c>#friendList .. "/" .. GetFriendNumLimit(me.level)</c>；</item>
///   <item><c>UI_Friend.lua:666</c>+<c>:673</c>（一键通过前本地拦上限）、
///         <c>:868</c>+<c>:869</c>（单个通过）、<c>:1027</c>+<c>:1028</c>（死代码 <c>OnFXBtnAllYes_Test</c>）；</item>
///   <item><c>UI_Main.lua:890..891</c>（好友申请红点：<c>#HYList &lt; limitNum</c> 才亮）；</item>
///   <item><c>UI_PlayerLevelUp.lua:48..54</c>（升级面板「好友上限 +N」）。</item>
/// </list>
/// 服务端用别的数字就会出现「客户端不让点」或「客户端以为成功而服务端拒绝」
/// （取证文档 §4.3 + 陷阱 16）。
///
/// <para><b>表结构</b></para>
/// <c>Friend.txt:1</c> 类型行 <c>INT STR STR</c>、<c>:2</c> 中文行「&lt;=等级时 / 名字 / 好友数量限制」、
/// <c>:3</c> 列名行 <c>rolelevel  Name  FriendNumLimit</c>、<c>:4</c> 起数据。
/// <c>Name</c> 列全表为空。<c>rolelevel</c> = 1..150 连续 + 一行 300（<c>Friend.txt:154</c>，
/// 原始文本是 <c>'300\t\t100\t\t'</c>，尾部两个空列）。
///
/// <para><b>查表语义（逐字照抄 <c>Friend.lua:225..236</c>）</b></para>
/// <code>
/// function FriendLogic:GetFriendCfg(rolelevel)
///     if rolelevel &gt; 1000 or rolelevel &lt; 0 then return end      -- Friend.lua:226
///     for i = rolelevel , 0 , -1 do
///         if self.tbFriendCfg[i] then return self.tbFriendCfg[i] end -- 向下找第一个存在的档
///     end
/// end
/// function FriendLogic:GetFriendNumLimit(rolelevel)
///     local cfg = self:GetFriendCfg(rolelevel)
///     return cfg and cfg.FriendNumLimit or 10                      -- Friend.lua:235 兜底 10
/// end
/// </code>
/// ⇒ 三条推论（都是坑，取证文档 §4.3）：
/// <list type="number">
///   <item><b>LV 151..299 全部回落到 rolelevel=150 ⇒ 上限 99</b>（这一段没有独立配置行）；</item>
///   <item><b>LV 300..1000 ⇒ 100</b>；GM 全解锁账号是 LV.300，正好吃到 100；</item>
///   <item>★★ <b>LV &gt; 1000 ⇒ 返回 nil ⇒ 上限退化成 10</b>。所以千万不要把 GM 号等级设成 9999，
///     好友上限会从 100 掉回 10，而且 <c>UI_PlayerLevelUp.lua:53</c> 会
///     <c>oldFrindCfg.FriendNumLimit</c> 直接索引 nil 而抛。LV 0 同样是 10（<c>tbFriendCfg[0]</c> 不存在）。</item>
/// </list>
///
/// ★ <c>FriendLogic.FriendNumLimit = 100</c>（<c>Friend.lua:29</c>）是个**没人用的死常量**
/// （<c>grep -rn "FriendLogic.FriendNumLimit" extracted/lua/</c> 只命中定义行），别照它写。
///
/// 缺表一律降级成空表 + WARN（与 <see cref="TrainTable"/>/<see cref="ChapterTable"/> 一致），绝不抛。
/// </summary>
public sealed class FriendTable
{
    /// <summary>表文件名（客户端口径 <c>Friend/Friend.txt</c>，本目录是扁平化的）。</summary>
    public const string FileName = "Friend.txt";

    /// <summary>查不到档位时的兜底上限（<c>Friend.lua:235</c> 的 <c>or 10</c>）。</summary>
    public const int FallbackLimit = 10;

    /// <summary>
    /// 查表的等级上界：<c>Friend.lua:226</c> <c>if rolelevel &gt; 1000 … then return end</c>，
    /// 超过它 <c>GetFriendCfg</c> 直接返回 nil ⇒ 上限退化成 <see cref="FallbackLimit"/>。
    /// </summary>
    public const int MaxQueryLevel = 1000;

    private readonly Dictionary<int, int> _byLevel = new();

    private FriendTable() { }

    /// <summary>永久空表（缺表降级用，也是单测基线）。</summary>
    public static FriendTable Empty { get; } = new();

    /// <summary>配置行数（0 = 处于降级状态）。</summary>
    public int Count => _byLevel.Count;

    /// <summary><c>rolelevel</c> → <c>FriendNumLimit</c> 的原始映射（只读，测试用）。</summary>
    public IReadOnlyDictionary<int, int> Rows => _byLevel;

    /// <summary>
    /// 某等级的好友数量上限。语义与 <c>FriendLogic:GetFriendNumLimit</c> 完全一致
    /// （含「向下找档」「&gt;1000 退化成 10」「档位值为 0 也退化成 10」三条）。
    /// </summary>
    public int NumLimit(int roleLevel)
    {
        // Friend.lua:226 —— 越界直接 return（Lua 里返回 nil），随后 :235 的 or 10 生效
        if (roleLevel > MaxQueryLevel || roleLevel < 0)
            return FallbackLimit;

        for (int lv = roleLevel; lv >= 0; lv--)
        {
            if (!_byLevel.TryGetValue(lv, out int limit))
                continue;
            // Friend.lua:235 `cfg and cfg.FriendNumLimit or 10` —— 档位存在但值为 0 时也落到 10
            return limit > 0 ? limit : FallbackLimit;
        }
        return FallbackLimit;
    }

    /// <summary>从 setting 目录加载。缺表返回空表并打 WARN（不抛异常）。</summary>
    public static FriendTable Load(string settingDir)
    {
        var table = new FriendTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失：好友上限查询降级为固定 {FallbackLimit}" +
                     "（与客户端 FriendLogic:GetFriendNumLimit 不一致，界面会出现「不让点」）");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int level = raw.Int(row, "rolelevel", -1);
            if (level < 0)
                continue;
            int limit = raw.Int(row, "FriendNumLimit", 0);
            if (table._byLevel.ContainsKey(level))
            {
                // 与客户端同构的重复检查：Friend.lua:217 打印 ERROR___rolelevel N repeated
                Log.Warn($"[setting] {FileName} rolelevel {level} 重复，保留先出现的那行");
                continue;
            }
            table._byLevel[level] = limit;
        }

        Log.Info($"[setting] {FileName} 载入 {table._byLevel.Count} 档好友上限" +
                 $"（LV1={table.NumLimit(1)} LV150={table.NumLimit(150)} " +
                 $"LV299={table.NumLimit(299)} LV300={table.NumLimit(300)}）");
        return table;
    }
}
