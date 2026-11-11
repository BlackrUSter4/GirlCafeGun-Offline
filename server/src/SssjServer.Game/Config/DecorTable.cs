using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 个性化装扮的一行配置（头像框 / 名片 / 列表标识 / 聊天气泡 四张表共用这个形状）。
///
/// <para><b>★ 为什么四张表能共用一个 record</b></para>
/// 客户端自己就是这么做的：<c>CardCommon:LoadBoardConfig</c>（<c>CardCommon.lua:219..238</c>）与
/// <c>LoadBabbleConfig</c>（<c>:247..267</c>）逐字段一模一样，只有表路径不同；
/// 头像框 <c>LoadHeadFrameConfig</c>（<c>:138..167</c>）多了几个纯表现列（<c>Effect</c>/<c>Coverage</c>…），
/// 但**服务端只关心 <see cref="Id"/> 与 <see cref="Type"/>/<see cref="LimitTime"/>** 这三列。
/// </summary>
/// <param name="Id">装扮 id（<c>ID</c> 列）。</param>
/// <param name="Type">
/// ★★★ **这一列决定「拥有」怎么判**，是本域唯一容易写错的地方。
/// 取证 <c>UI_SkinSelected.lua:323..348</c> <c>IsUnlock(id, btype, btime)</c>：
/// <code>
/// if btype == nil or id == 0 then return true end          -- 未配置 / 0 号一律视为拥有
/// nGetTime = me:GetTaskValue(76|77|78, id)                  -- ← 存的是**获得时间戳**
/// if btype == 1 and nGetTime ~= 0 then return true          -- Type 1 = 永久：非 0 即拥有
/// else return (nGetTime + btime) &gt; GetTime() end            -- Type 2 = 限时：算到期
/// </code>
/// ⇒ <b>任务值里存的不是「有没有」而是「什么时候拿到的」</b>。
/// 写 1 对 <c>Type==2</c> 的装扮等于「1970 年拿到、早已过期」⇒ 客户端判未拥有，
/// 而且 <c>CardCommon:UpdateDressUpInfo</c> 会**反手发一条 Set…(0) 把它脱下来**
/// （<c>CardCommon.lua:1054/1060/1066</c>）—— 症状是「发了装扮却穿不上、还自动变回默认」。
/// </param>
/// <param name="LimitTime">
/// 限时装扮的有效期（秒，<c>LimitTime</c> 列）。<c>Type==1</c> 时无意义。
/// </param>
/// <param name="Source">来源（<c>Source</c>/<c>HeadSource</c> 列，仅日志用）。</param>
public sealed record DecorRow(int Id, int Type, int LimitTime, int Source)
{
    /// <summary>永久装扮（<c>Type == 1</c>）：任务值非 0 即视为拥有。</summary>
    public bool IsPermanent => Type == 1;

    public override string ToString()
        => $"装扮{Id}（{(IsPermanent ? "永久" : $"限时 {LimitTime}s")}）";
}

/// <summary>
/// 个性化装扮四张表（头像框 / 名片 / 列表标识 / 聊天气泡）与它们的任务值 group。
///
/// <para><b>1. 四类装扮的存储位置（★ 三种不同的地方，别搞混）</b></para>
/// <list type="table">
///   <item>
///     <term>头像框</term>
///     <description>
///     **当前穿戴** 存 protobuf <c>PlayerNtf.headframe</c>（field 13）⇒ 改了要推 <b>pf 1030</b>；
///     **拥有记录** 存任务值 group <see cref="HeadFrameGroup"/> = <b>11</b>
///     （<c>CardCommon.PortraitGroupId = 11</c>，<c>CardCommon.lua:88</c>）。
///     </description>
///   </item>
///   <item>
///     <term>名片 / 列表标识 / 气泡</term>
///     <description>
///     **当前穿戴** 存服务端自己的存档 <c>Extra["maincity"]["card"]</c>
///     （<see cref="Handlers.MainCityFeature.CardState"/>，由 cmd 94 下发）；
///     **拥有记录** 存任务值 group <b>76 / 77 / 78</b>（<c>UI_SkinSelected.lua:331..335</c>）。
///     </description>
///   </item>
/// </list>
///
/// <para><b>2. ★★ 任务值语义是「获得时间戳」不是「有没有」</b></para>
/// 见 <see cref="DecorRow.Type"/> 的取证。<see cref="OwnedStamp"/> 封装了正确写法。
///
/// <para><b>3. ★★★ 表头行数写死 2（0-based），不能用 <c>LoadAuto</c> 探 <c>"ID"</c></b></para>
/// 权威判据是客户端自己的加载调用：<c>LoadTabFile("Player/HeadFrame.txt", 3)</c>
/// （<c>CardCommon.lua:140</c>）、<c>LoadTabFile("Player/Board.txt", 3)</c>（<c>:221</c>）、
/// <c>LoadTabFile("Player/Babble.txt", 3)</c>（<c>:249</c>）。
/// ★ 陷阱：这四张表的**第 2 行（中文说明行）首格恰好也是 <c>ID</c>**
/// ⇒ <c>LoadAuto("ID")</c> 会在那一行命中，导致除 <c>ID</c> 外所有列都找不到、
/// <c>Type</c>/<c>LimitTime</c> 静默变 0、**全部装扮被判未拥有且不报错**。
/// 详见 <see cref="Fill"/> 里的注释。
///
/// <para><b>4. ★ 气泡表叫 <c>Babble.txt</c> 不是 <c>Bubble.txt</c></b></para>
/// 原厂拼写如此（<c>CardCommon.lua:249</c>）。仓库里另有个 <c>l2dVoiceBubble.txt</c>
/// 是**立绘语音气泡**，与聊天气泡无关，别接错。
/// </summary>
public sealed class DecorTable
{
    /// <summary>头像框拥有记录的任务值 group（<c>CardCommon.PortraitGroupId</c>，<c>CardCommon.lua:88</c>）。</summary>
    public const int HeadFrameGroup = 11;

    /// <summary>名片皮肤拥有记录的任务值 group（<c>UI_SkinSelected.lua:331</c>）。</summary>
    public const int SkinGroup = 76;

    /// <summary>列表标识拥有记录的任务值 group（<c>UI_SkinSelected.lua:333</c>）。</summary>
    public const int BoardGroup = 77;

    /// <summary>聊天气泡拥有记录的任务值 group（<c>UI_SkinSelected.lua:335</c>）。</summary>
    public const int BabbleGroup = 78;

    /// <summary>
    /// 默认头像框 id。取证 <c>CardCommon.lua:1020</c>：头像框过期时客户端自己发
    /// <c>Player_SetHeadFrame, 1</c> ⇒ <b>1</b> 是原厂认可的「无装扮」安全值。
    /// ★ 注意名片/标识/气泡的安全值是 <b>0</b>（<c>CardCommon.lua:1054/1060/1066</c>），与这里不同。
    /// </summary>
    public const int DefaultHeadFrame = 1;

    private readonly Dictionary<int, DecorRow> _heads = new();
    private readonly Dictionary<int, DecorRow> _skins = new();
    private readonly Dictionary<int, DecorRow> _boards = new();
    private readonly Dictionary<int, DecorRow> _babbles = new();

    /// <summary>头像框配置（<c>Player/HeadFrame.txt</c>）。</summary>
    public IReadOnlyDictionary<int, DecorRow> Heads => _heads;

    /// <summary>名片皮肤配置（<c>Player/Skin.txt</c>）。</summary>
    public IReadOnlyDictionary<int, DecorRow> Skins => _skins;

    /// <summary>列表标识配置（<c>Player/Board.txt</c>）。</summary>
    public IReadOnlyDictionary<int, DecorRow> Boards => _boards;

    /// <summary>聊天气泡配置（<c>Player/Babble.txt</c>）。</summary>
    public IReadOnlyDictionary<int, DecorRow> Babbles => _babbles;

    /// <summary>
    /// 按装扮种类取对应的配置字典与任务值 group。
    /// </summary>
    public (IReadOnlyDictionary<int, DecorRow> Rows, int Group) For(DecorKind kind) => kind switch
    {
        DecorKind.HeadFrame => (_heads, HeadFrameGroup),
        DecorKind.Skin => (_skins, SkinGroup),
        DecorKind.Board => (_boards, BoardGroup),
        DecorKind.Babble => (_babbles, BabbleGroup),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知装扮种类"),
    };

    /// <summary>
    /// 从 <c>extracted/setting/</c> 载入四张表。缺表不抛（该类装扮就当没有可选项），
    /// 与本项目其它配置表一致。
    /// </summary>
    public static DecorTable Load(string settingDir)
    {
        var table = new DecorTable();
        Fill(table._heads, Path.Combine(settingDir, "HeadFrame.txt"));
        Fill(table._skins, Path.Combine(settingDir, "Skin.txt"));
        Fill(table._boards, Path.Combine(settingDir, "Board.txt"));
        Fill(table._babbles, Path.Combine(settingDir, "Babble.txt"));

        Log.Info($"[setting] 个性化装扮配置：头像框 {table._heads.Count} 条"
                 + $"（永久 {table._heads.Values.Count(r => r.IsPermanent)}）、"
                 + $"名片 {table._skins.Count} 条、标识 {table._boards.Count} 条、"
                 + $"气泡 {table._babbles.Count} 条");
        return table;
    }

    private static void Fill(Dictionary<int, DecorRow> into, string path)
    {
        // ★★★ 这里**必须写死 headerRow=2**，不能用 LoadAuto 探 "ID" —— 本仓踩过：
        // 这四张表的**第 2 行（中文说明行）第一个单元格恰好也是 "ID"**：
        //   row0: INT  INT  INT …                       ← 类型行
        //   row1: ID   来源（0.默认 1.成就 …）  显示顺序 …  ← 中文说明行，★ 首格是 "ID"
        //   row2: ID   Source  DisplayOrder  ShowType  Type  LimitTime …   ← 真正的列名行
        // ⇒ LoadAuto("ID") 会在 row1 就命中并返回，于是除 ID 外**所有列都找不到**，
        //   `Type`/`LimitTime` 静默变 0 ⇒ 全部装扮被判「未拥有」且不抛不报错。
        // 权威依据是客户端自己的加载调用（第 2 个实参 = 表头行数 3，即 0-based 的 2）：
        //   CardCommon.lua:140 LoadTabFile("Player/HeadFrame.txt", 3)
        //   CardCommon.lua:221 LoadTabFile("Player/Board.txt",     3)
        //   CardCommon.lua:249 LoadTabFile("Player/Babble.txt",    3)
        // ⇒ 教训：探针列名必须选一个**只出现在列名行**的（如 "Type"），
        //   或者像这里一样直接写死行号。
        var table = SettingTable.Load(path, headerRow: 2);
        if (table is null) return;

        // 防御：真正的列名行必然有 Type 列；没有就说明表结构变了，宁可空表也不要静默错值。
        if (!table.Columns.ContainsKey("Type"))
        {
            Log.Warn($"[setting] {Path.GetFileName(path)} 第 3 行没有 Type 列，疑似表结构变动，跳过");
            return;
        }

        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "ID");
            if (id <= 0) continue;

            // 头像框的来源列叫 HeadSource，其余三张叫 Source —— 取到哪个算哪个（仅日志用）。
            int source = table.Columns.ContainsKey("Source")
                ? table.Int(row, "Source")
                : table.Int(row, "HeadSource");

            into[id] = new DecorRow(
                Id: id,
                Type: table.Int(row, "Type"),
                LimitTime: table.Int(row, "LimitTime"),
                Source: source);
        }
    }

    /// <summary>
    /// 该装扮是否存在于配置表里。
    /// ★ 不存在的 id **必须拒绝**：客户端 <c>CardCommon:UpdateDressUpInfo</c>
    /// 查不到配置时会反手发 <c>Set…(0)</c> 把它脱下来（<c>CardCommon.lua:1052/1058/1064</c>），
    /// 服务端存了也白存，还会多一轮无谓的上下行。
    /// </summary>
    public bool Exists(DecorKind kind, int id) => For(kind).Rows.ContainsKey(id);

    /// <summary>
    /// 按客户端的 <c>IsUnlock</c> 判定玩家是否拥有该装扮（含限时到期判定）。
    /// 逐字对应 <c>UI_SkinSelected.lua:323..348</c>，取证见 <see cref="DecorRow.Type"/>。
    /// </summary>
    /// <param name="kind">装扮种类。</param>
    /// <param name="id">装扮 id。<b>0</b> 一律视为拥有（= 脱下，客户端同款判断）。</param>
    /// <param name="stamp">该装扮在任务值里记的获得时间戳（Unix 秒）。</param>
    /// <param name="now">当前 Unix 秒。</param>
    public bool IsOwned(DecorKind kind, int id, int stamp, long now)
    {
        // UI_SkinSelected.lua:326 `if btype == nil or id == 0 then return true end`
        if (id == 0) return true;
        if (!For(kind).Rows.TryGetValue(id, out var row)) return true;

        // :338 Type 1 = 永久，任务值非 0 即拥有
        if (row.IsPermanent) return stamp != 0;

        // :341 Type 2 = 限时，(获得时间 + 有效期) > 现在
        return stamp + (long)row.LimitTime > now;
    }

    /// <summary>
    /// 「把某个装扮标记为已拥有」应该写进任务值的数值。
    ///
    /// ★★★ 永久装扮也**不能写 1**：虽然 <c>Type==1</c> 只判 <c>~= 0</c>，
    /// 但同一个 group 里永久与限时装扮**混存**（例 <c>HeadFrame.txt</c> 121 条里两种都有），
    /// 而客户端 <c>GetLeftTime</c>（<c>UI_SkinSelected.lua:395..420</c>）对 <c>Type==2</c>
    /// 会拿这个值算剩余时间并显示。统一写「现在」既满足永久判据，也让限时装扮
    /// 从现在开始计时 —— 这也是原服的语义（任务值列名就是"获得时间"）。
    /// </summary>
    public static int OwnedStamp(long now) => (int)Math.Min(now, int.MaxValue);
}

/// <summary>个性化装扮的四个种类。</summary>
public enum DecorKind
{
    /// <summary>头像框（<c>Player/HeadFrame.txt</c>，group 11，穿戴存 <c>PlayerNtf.headframe</c>）。</summary>
    HeadFrame,

    /// <summary>名片皮肤（<c>Player/Skin.txt</c>，group 76）。</summary>
    Skin,

    /// <summary>玩家列表标识皮肤（<c>Player/Board.txt</c>，group 77）。</summary>
    Board,

    /// <summary>聊天气泡（<c>Player/Babble.txt</c>，group 78）。</summary>
    Babble,
}
