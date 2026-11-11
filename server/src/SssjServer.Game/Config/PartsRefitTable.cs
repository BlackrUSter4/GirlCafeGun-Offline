using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 零件覆写规则的一行（<c>PartsOverwrite.txt</c>）。
///
/// 客户端同构实现 <c>OriginalLogic:LoadOverwriteConf</c>
/// （<c>extracted/lua/OriginalLogic.lua:27..50</c>），加载
/// <c>LoadTabFile('Item/PartsOverwrite.txt', 3)</c> ⇒ **三行表头**。
///
/// 实测 8 行 = 4 种零件类型 × 2 个星级。
/// </summary>
public sealed class PartsOverwriteRow
{
    /// <summary>零件类型（<c>Type</c>）：1 枪管 / 2 弹匣 / 3 瞄准仪 / 4 握把。</summary>
    public int Type { get; init; }

    /// <summary>星级（<c>Rarity</c>）。实测值域 {3, 4}。</summary>
    public int Rarity { get; init; }

    /// <summary>
    /// 覆写所需点数 <c>ItemNum</c> = <c>[总点数, 单个紫零件点数, 单个金零件点数]</c>
    /// （**逗号**分隔，`Str2IntArr`）。实测 <c>[30,10,20]</c>。
    /// </summary>
    public IReadOnlyList<int> ItemNum { get; init; } = Array.Empty<int>();

    /// <summary>覆写消耗的货币 id（<c>CurrencyId</c>）。实测 2 = 金币。</summary>
    public int CurrencyId { get; init; }

    /// <summary>覆写消耗的货币数（<c>CurrencyNum</c>）。</summary>
    public int CurrencyNum { get; init; }

    /// <summary>
    /// 激活所需的组套道具（<c>ActiveItem</c>，**短横**五元组 `Parse2Item`，
    /// 例 <c>7-5-1-4-1</c> = G,D,P,L,N）。
    /// ★ 与 <see cref="ItemNum"/> 分隔符**不同**，AGENTS.md §5 记过这个坑。
    /// </summary>
    public IReadOnlyList<int> ActiveItem { get; init; } = Array.Empty<int>();

    /// <summary>激活消耗的货币数（<c>ActiveCost</c>）。</summary>
    public int ActiveCost { get; init; }

    /// <summary>充能所需的组套道具（<c>ChargeItem</c>，短横五元组）。</summary>
    public IReadOnlyList<int> ChargeItem { get; init; } = Array.Empty<int>();

    /// <summary>充能消耗的货币数（<c>ChargeCost</c>）。</summary>
    public int ChargeCost { get; init; }

    /// <summary>属性改写所需点数（<c>RewriteItem</c>，逗号分隔，同 <see cref="ItemNum"/> 结构）。</summary>
    public IReadOnlyList<int> RewriteItem { get; init; } = Array.Empty<int>();

    /// <summary>属性改写消耗的货币数（<c>RewriteCost</c>）。</summary>
    public int RewriteCost { get; init; }

    /// <summary>按 <paramref name="opType"/>（351 的 nType）取该操作的货币消耗。</summary>
    public int CostOf(int opType) => opType switch
    {
        PartsRefitTable.OpOverwrite => CurrencyNum,
        PartsRefitTable.OpActive => ActiveCost,
        PartsRefitTable.OpCharge => ChargeCost,
        PartsRefitTable.OpRewrite => RewriteCost,
        _ => 0,
    };

    public override string ToString() => $"overwrite type={Type} rarity={Rarity} cost={CurrencyNum}";
}

/// <summary>
/// 一条零件词缀（<c>PartsAffix.txt</c>，实测 22 行 = 词条池）。
/// </summary>
public sealed class PartsAffixRow
{
    /// <summary>词缀类型（<c>Type</c>），也是「词条 id」——回包 <c>id</c> 用它。</summary>
    public int Type { get; init; }

    /// <summary>关联技能 id（<c>Skills</c>，逗号分隔）。</summary>
    public IReadOnlyList<int> Skills { get; init; } = Array.Empty<int>();

    /// <summary>多语言名 key（<c>Name</c>）。仅日志用。</summary>
    public string Name { get; init; } = "";

    /// <summary>
    /// 四档激活点数区间（<c>LevelPoint1..4</c>，各是**闭区间** <c>[下限,上限]</c>）。
    /// 实测 <c>[3,6] [7,11] [12,19] [20,99]</c>。
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> LevelPoints { get; init; }
        = Array.Empty<IReadOnlyList<int>>();

    /// <summary>按总点数算词缀等级（1..4）；不足最低档返回 0。</summary>
    public int LevelOf(int totalPoint)
    {
        for (int i = LevelPoints.Count - 1; i >= 0; i--)
        {
            var seg = LevelPoints[i];
            if (seg.Count >= 1 && totalPoint >= seg[0])
                return i + 1;
        }
        return 0;
    }

    public override string ToString() => $"affix#{Type} {Name}";
}

/// <summary>
/// 零件词条覆写的配置表：<c>PartsOverwrite.txt</c> + <c>PartsAffix.txt</c>。
///
/// <para><b>玩法链</b></para>
/// <c>UI_WeaponStrong</c>（武器强化，常驻）→ 零件改造页「覆写」按钮 →
/// 三条分支分别通向 <c>UI_WeaponOverWriteStore</c>(351) /
/// <c>UI_WeaponRefitChoose</c>(352) / <c>UI_WeaponSkillSelected</c>(352 nType=2)。
///
/// ★ 第十五轮曾把这条链误判成「零入口」—— 因为它用的是 <b><c>UI.CloseAndOpen</c></b>
/// 而不是 <c>UI.Open</c>（<c>UI_WeaponStrong.lua:376/378</c>）。
///
/// <para><b>★★★ 服务端逻辑不在包内</b></para>
/// 随包的 <c>OriginalLogic.lua</c>（148 行）只有客户端侧函数，
/// <b>没有 <c>StartOverwrite</c>、没有 <c>Save</c></b> —— 那两个正是
/// <c>MODULE_SERVER</c> 分支调用的。与 <c>ClubLogic</c>/<c>MercenaryLogic</c> 同型，
/// 服务端逻辑要自己补。取证见 <c>work/parts-refit-forensics.md</c>。
/// </summary>
public sealed class PartsRefitTable
{
    /// <summary>覆写规则表文件名（客户端口径 <c>Item/PartsOverwrite.txt</c>）。</summary>
    public const string OverwriteFileName = "PartsOverwrite.txt";

    /// <summary>词缀池表文件名（客户端口径 <c>Item/PartsAffix.txt</c>）。</summary>
    public const string AffixFileName = "PartsAffix.txt";

    /// <summary>
    /// 列名所在行号（<b>0-based</b>）。两张表都是三行表头
    /// （<c>OriginalLogic.lua:29</c> 的 <c>LoadTabFile(_, 3)</c> 那个 3 是**行数**）。
    /// ★ 第十五轮誓约表在这里踩过：传 3 会把第一行数据当列名 ⇒ 整表静默 0 行。
    /// </summary>
    public const int HeaderRow = 2;

    // ---- 351 的 nType（UI_WeaponOverWriteStore.lua:17..45 的四个页签）----

    /// <summary>1 = 覆写词条（消耗 <c>CurrencyNum</c> + <c>ItemNum</c> 点数）。</summary>
    public const int OpOverwrite = 1;

    /// <summary>2 = 激活（消耗 <c>ActiveCost</c> + <c>ActiveItem</c> 组套道具）。</summary>
    public const int OpActive = 2;

    /// <summary>3 = 充能（消耗 <c>ChargeCost</c> + <c>ChargeItem</c>）。</summary>
    public const int OpCharge = 3;

    /// <summary>4 = 属性改写（消耗 <c>RewriteCost</c> + <c>RewriteItem</c> 点数）。</summary>
    public const int OpRewrite = 4;

    /// <summary>词缀点数上限（SSR），<c>OriginalLogic.lua:4</c>。</summary>
    public const int MaxAffixPointSsr = 10;

    /// <summary>词缀点数上限（SR），<c>OriginalLogic.lua:5</c>。</summary>
    public const int MaxAffixPointSr = 6;

    /// <summary>零件方案存的任务值 group（<c>OriginalLogic.TASK_GID</c>，<c>:11</c>）。</summary>
    public const int PlanTaskGroup = 72;

    // (type, rarity) → row
    private readonly Dictionary<(int Type, int Rarity), PartsOverwriteRow> _overwrite = new();
    private readonly Dictionary<int, PartsAffixRow> _affix = new();

    private PartsRefitTable() { }

    /// <summary>永久空表（缺表降级用）。</summary>
    public static PartsRefitTable Empty { get; } = new();

    /// <summary>覆写规则条数。</summary>
    public int OverwriteCount => _overwrite.Count;

    /// <summary>词缀条数。</summary>
    public int AffixCount => _affix.Count;

    /// <summary>全部词缀（按 Type 升序）。</summary>
    public IReadOnlyList<PartsAffixRow> AllAffixes
        => _affix.Values.OrderBy(a => a.Type).ToList();

    /// <summary>取覆写规则；不存在返回 null（同 <c>OriginalLogic:GetOverwriteConf</c>）。</summary>
    public PartsOverwriteRow? Overwrite(int type, int rarity)
        => _overwrite.TryGetValue((type, rarity), out var r) ? r : null;

    /// <summary>取词缀配置。</summary>
    public PartsAffixRow? Affix(int type) => _affix.TryGetValue(type, out var a) ? a : null;

    /// <summary>
    /// 词条点数是「低 16 位 = 原生 / 高 16 位 = 充能」的复合编码
    /// （<c>OriginalLogic:GetPointVal</c>，<c>:60..64</c>）。
    /// ★ 与 <c>AffixLevel</c> 复用同一字段 ⇒ **必须按位读写**，不能当普通整数加减。
    /// </summary>
    public static (int Org, int Charge) SplitPoint(int val)
        => (val & 0xFFFF, (val >> 16) & 0xFFFF);

    /// <summary>合成点数编码（<see cref="SplitPoint"/> 的逆）。</summary>
    public static int MakePoint(int org, int charge)
        => (Math.Clamp(org, 0, 0xFFFF)) | (Math.Clamp(charge, 0, 0xFFFF) << 16);

    /// <summary>总点数（<c>OriginalLogic:GetTotalPoint</c>）。</summary>
    public static int TotalPoint(int val)
    {
        var (o, c) = SplitPoint(val);
        return o + c;
    }

    /// <summary>从 setting 目录加载。缺表返回空表并打 WARN（不抛）。</summary>
    public static PartsRefitTable Load(string settingDir)
    {
        var table = new PartsRefitTable();

        var ow = SettingTable.Load(Path.Combine(settingDir, OverwriteFileName), HeaderRow);
        if (ow is null)
        {
            Log.Warn($"[setting] {OverwriteFileName} 缺失：零件覆写域降级为空表");
        }
        else
        {
            foreach (var row in ow.Rows)
            {
                int type = ow.Int(row, "Type");
                int rarity = ow.Int(row, "Rarity");
                if (type <= 0 || rarity <= 0)
                    continue;       // 同 OriginalLogic.lua:43 的守卫
                table._overwrite[(type, rarity)] = new PartsOverwriteRow
                {
                    Type = type,
                    Rarity = rarity,
                    // ★ ItemNum / RewriteItem 是逗号分隔（Str2IntArr）
                    ItemNum = ParseCommaInts(ow.Str(row, "ItemNum")),
                    CurrencyId = ow.Int(row, "CurrencyId"),
                    CurrencyNum = ow.Int(row, "CurrencyNum"),
                    // ★ ActiveItem / ChargeItem 是短横五元组（Parse2Item）—— 分隔符不同！
                    ActiveItem = ParseDashInts(ow.Str(row, "ActiveItem")),
                    ActiveCost = ow.Int(row, "ActiveCost"),
                    ChargeItem = ParseDashInts(ow.Str(row, "ChargeItem")),
                    ChargeCost = ow.Int(row, "ChargeCost"),
                    RewriteItem = ParseCommaInts(ow.Str(row, "RewriteItem")),
                    RewriteCost = ow.Int(row, "RewriteCost"),
                };
            }
        }

        var af = SettingTable.Load(Path.Combine(settingDir, AffixFileName), HeaderRow);
        if (af is null)
        {
            Log.Warn($"[setting] {AffixFileName} 缺失：零件词缀池降级为空");
        }
        else
        {
            foreach (var row in af.Rows)
            {
                int type = af.Int(row, "Type");
                if (type <= 0)
                    continue;
                var pts = new List<IReadOnlyList<int>>();
                for (int i = 1; i <= 4; i++)
                {
                    var seg = ParseCommaInts(af.Str(row, $"LevelPoint{i}"));
                    if (seg.Count > 0)
                        pts.Add(seg);
                }
                table._affix[type] = new PartsAffixRow
                {
                    Type = type,
                    Skills = ParseCommaInts(af.Str(row, "Skills")),
                    Name = af.Str(row, "Name"),
                    LevelPoints = pts,
                };
            }
        }

        Log.Info($"[setting] 零件覆写配置：规则 {table.OverwriteCount} 条" +
                 $"（{table._overwrite.Keys.Select(k => k.Type).Distinct().Count()} 种零件类型）、" +
                 $"词缀池 {table.AffixCount} 条");
        return table;
    }

    /// <summary>**逗号**分隔整数（<c>Str2IntArr</c>）。</summary>
    internal static List<int> ParseCommaInts(string value) => SplitInts(value, ',');

    /// <summary>**短横**分隔整数（<c>Parse2Item</c>）。</summary>
    internal static List<int> ParseDashInts(string value) => SplitInts(value, '-');

    private static List<int> SplitInts(string value, char sep)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(value))
            return result;
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return result;
        foreach (var tok in body.Split(sep, StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(tok.Trim(), out int n))
                result.Add(n);
        }
        return result;
    }
}
