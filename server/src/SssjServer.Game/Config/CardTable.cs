using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 角色卡养成域的配置表集合：<b>升级经验曲线 / 突破 / 分解返还 / 经验素材</b>。
///
/// 完整取证见 <c>work/card-domain-forensics.md</c> 与 <c>work/gap-analysis.md</c> §2。
/// 本类只做「读表 + 把 Lua 侧公式翻成 C#」，不含业务流程（那在
/// <see cref="Handlers.CardFeature"/>）。
///
/// <para><b>★ 为什么必须逐字复刻客户端公式</b></para>
/// 客户端在**发包之前**就已经把「要花多少钱 / 能加多少经验 / 会升到几级」算出来给玩家看了
/// （<c>UI_CardList.lua:657</c> 累加 <c>GetCardPrice</c> 填确认框、
/// <c>UI_CardMain.lua:1488/2066</c> 用 <c>GetItemNeedExp</c> 显示进度条）。
/// 服务端算得不一样 ⇒ **玩家看到的与实收不符**（点了扣 3000 却显示 2800），
/// 而且没有任何报错 —— 属于最难查的一类问题。所以下面每个公式都标了 Lua 出处。
/// </summary>
public sealed class CardTable
{
    /// <summary><c>ItemExp.txt</c>：行 = 等级，列 = <c>NeedExp&lt;ExpType&gt;</c>。</summary>
    public const string ExpFileName = "ItemExp.txt";

    /// <summary><c>CharacterCardBreak.txt</c>：突破消耗与等级上限。</summary>
    public const string BreakFileName = "CharacterCardBreak.txt";

    /// <summary><c>CardRefit.txt</c>：哪些卡能改造 + 每档的同步率/任务前置（`CardCommon.lua:101`）。</summary>
    public const string RefitFileName = "CardRefit.txt";

    /// <summary><c>CardRefitCost.txt</c>：改造消耗（`CardCommon.lua:102`）。</summary>
    public const string RefitCostFileName = "CardRefitCost.txt";

    /// <summary>
    /// 改造档位数 = **5**（`CardCommon.lua:340/362` 都是 <c>for j = 1, 5</c>）。
    /// ★ 现网 <c>CardRefitCost.txt</c> 只填到第 4 档（第 5 组列为空），
    /// 解析仍按 5 读 —— 空列自然得到空 Items/0 消耗，由
    /// <see cref="CardRefitRow.MaxRefit"/> 按「有没有配同步率」算出真实上限。
    /// </summary>
    public const int MaxRefitSteps = 5;

    /// <summary><c>ItemRarityItem.txt</c>：分解的金币系数与返还材料。</summary>
    public const string RarityItemFileName = "ItemRarityItem.txt";

    /// <summary>
    /// <c>ItemRarityExp.txt</c>：按**稀有度**的两个关键值（★ 这张表也只有 <b>2 行表头</b>）。
    /// <list type="bullet">
    ///   <item><c>NeedExp1</c> = 「角色卡可以突破几次」（1★:0 / 2★:1 / 3★:2 / 4★:3 / 5★:4）</item>
    ///   <item><c>NeedExp2</c> = 「角色卡喂卡基础返还」= <c>GetItemRarityExp(rarity, 2)</c>
    ///     （<see cref="DecomposeExpType"/> 正是取这一列）</item>
    /// </list>
    /// </summary>
    public const string RarityExpFileName = "ItemRarityExp.txt";

    /// <summary>
    /// 角色卡的 <c>ExpType</c> 恒为 <b>1</b> ⇒ 升级只读 <c>ItemExp.NeedExp1</c> 这一列。
    /// 取证：<c>cut -f21 CharacterCard.txt | sort | uniq -c</c> → <b>269 行全是 1</b>。
    /// </summary>
    public const int CardExpType = 1;

    /// <summary>
    /// 分解时算「基础经验」用的 ExpType（<c>CardCommon.DecomposeExpType = 2</c>，
    /// <c>CardCommon.lua:76</c>）—— 注意它**不是** <see cref="CardExpType"/>。
    /// </summary>
    public const int DecomposeExpType = 2;

    /// <summary>同属性/通用经验素材的经验倍率（<c>CardCommon.SameAttrEXPRate = 1.5</c>，<c>:84</c>）。</summary>
    public const double SameAttrExpRate = 1.5;

    /// <summary>「吃卡」时把素材卡累计经验折算成经验的比例（<c>CardCommon.ExpConversion = 0.3</c>，<c>:78</c>）。</summary>
    public const double ExpConversion = 0.3;

    /// <summary>「吃卡」单张素材能提供的经验上限（<c>CardCommon.MaxEXPProvide = 150000</c>，<c>:80</c>）。</summary>
    public const int MaxExpProvide = 150000;

    /// <summary>
    /// 通用（全属性）经验素材的 <c>P</c> 值。<c>GetExpRate</c>（<c>CardCommon.lua:700..710</c>）里
    /// <c>itemParticular == 4</c> 无条件享受 <see cref="SameAttrExpRate"/>。
    /// </summary>
    public const int UniversalExpItemParticular = 4;

    /// <summary>
    /// 「角色卡属性 → 同属性经验素材的 P 值」映射（<c>CardCommon.tbMapC2I</c>，<c>CardCommon.lua:33..39</c>）：
    /// <c>0→1, 1→3, 2→2, 3→5, 4→6</c>。
    /// ★ 注意**不是恒等映射**（属性 1 对应素材 P=3、属性 2 对应 P=2），照抄别手改。
    /// </summary>
    public static readonly IReadOnlyDictionary<int, int> AttributeToExpItemParticular =
        new Dictionary<int, int> { [0] = 1, [1] = 3, [2] = 2, [3] = 5, [4] = 6 };

    /// <summary>
    /// 经验素材表（<c>CardCommon.tbExpCard</c>，<c>CardCommon.lua:4..30</c>）——
    /// <b>Lua 里硬编码的常量表，不在 <c>extracted/setting/</c> 里</b>，所以这里也硬编码。
    ///
    /// <para><b>★★ 这张表是「上行 <c>items</c> 里的 id」的唯一解码依据</b></para>
    /// <c>Card_LevelUpCommon</c> 的 <c>items</c> 元素是 <c>[itype, id, num]</c>，
    /// 当 <c>itype != 2</c>（即素材是**道具**而不是卡）时，
    /// <c>id</c> **不是 GDPL 也不是 guid，而是本表的 1-based 下标**
    /// （<c>CardCommon:GetIdxByGDPL</c>，<c>CardCommon.lua:683..696</c>）。
    /// 服务端必须用本表反查回 GDPL 才能扣对道具 —— 这是本域最容易写错的一处。
    ///
    /// 24 项 = <c>P ∈ {1,2,3,5,6,4}</c> × <c>L ∈ {1,2,3,4}</c>，全部 <c>G=7, D=1</c>。
    /// </summary>
    public static readonly IReadOnlyList<(int G, int D, int P, int L)> ExpCards = new[]
    {
        (7, 1, 1, 1), (7, 1, 2, 1), (7, 1, 3, 1), (7, 1, 5, 1), (7, 1, 6, 1), (7, 1, 4, 1),
        (7, 1, 1, 2), (7, 1, 2, 2), (7, 1, 3, 2), (7, 1, 5, 2), (7, 1, 6, 2), (7, 1, 4, 2),
        (7, 1, 1, 3), (7, 1, 2, 3), (7, 1, 3, 3), (7, 1, 5, 3), (7, 1, 6, 3), (7, 1, 4, 3),
        (7, 1, 1, 4), (7, 1, 2, 4), (7, 1, 3, 4), (7, 1, 5, 4), (7, 1, 6, 4), (7, 1, 4, 4),
    };

    /// <summary>
    /// 技能饼干的两个「越界索引」（<c>GetEnhanceCardGDPL</c> 的 else 分支，<c>CardCommon.lua:654..661</c>）：
    /// <c>#tbExpCard + 1</c> → <c>(7,4,1,3)</c>、<c>#tbExpCard + 2</c> → <c>(7,4,1,4)</c>。
    /// ★ 它们**加的是技能经验、不是等级经验**（<c>GetEnhanceCardExp</c> 的 else 分支
    /// 直接 <c>return count</c>，<c>:733</c>）—— 技能等级体系本服未实现，见
    /// <see cref="Handlers.CardFeature"/> 的说明。
    /// </summary>
    public static readonly IReadOnlyList<(int G, int D, int P, int L)> SkillCookies = new[]
    {
        (7, 4, 1, 3), (7, 4, 1, 4),
    };

    private readonly Dictionary<int, Dictionary<int, long>> _needExp = new();   // expType → level → 升到下级所需
    private readonly Dictionary<int, CardBreakRow> _break = new();              // Type → 突破配置
    private readonly Dictionary<(int G, int D, int P, int L), CardRefitRow> _refit = new();  // 卡四元组 → 改造配置
    private readonly Dictionary<int, RarityItemRow> _rarity = new();            // Rarity → 分解配置
    private readonly Dictionary<int, int> _rarityBreakCount = new();            // Rarity → 可突破次数（NeedExp1）
    private readonly Dictionary<int, long> _rarityFeedBase = new();             // Rarity → 喂卡基础返还（NeedExp2）

    private CardTable() { }

    /// <summary>空表（单测/缺表时的降级形态；一切查询都返回「无数据」）。</summary>
    public static CardTable Empty { get; } = new();

    private static readonly Dictionary<string, CardTable> _cache = new(StringComparer.Ordinal);

    /// <summary>按目录加载（进程级缓存，与其它 *Table 同一套做法）。</summary>
    public static CardTable Load(string settingDir)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(settingDir, out var cached))
                return cached;
            var t = LoadCore(settingDir);
            _cache[settingDir] = t;
            return t;
        }
    }

    private static CardTable LoadCore(string dir)
    {
        var t = new CardTable();

        // ---- ItemExp.txt：列名是 NeedExp1..NeedExp5_4，行首列是 Lv ----
        // ★ 这张表只有 **2 行表头**（列名在第 1 行，不是通常的第 2 行）⇒ 必须用 LoadAuto 探测，
        //   否则会把第一行数据当列名、整表静默读空（查询全返回 0、不抛不报错）。
        var exp = SettingTable.LoadAuto(Path.Combine(dir, ExpFileName), "Lv");
        if (exp is not null)
        {
            foreach (var col in exp.Columns.Keys)
            {
                if (!col.StartsWith("NeedExp", StringComparison.Ordinal))
                    continue;
                // 只认纯数字后缀（NeedExp1/2/3…）；NeedExp5_1 这种符文分档本域用不到，跳过
                if (!int.TryParse(col.AsSpan("NeedExp".Length), out int type))
                    continue;
                var byLevel = new Dictionary<int, long>();
                foreach (var row in exp.Rows)
                {
                    int lv = exp.Int(row, "Lv", -1);
                    if (lv < 0)
                        continue;
                    string raw = exp.Str(row, col);
                    if (raw.Length == 0)
                        continue;
                    if (long.TryParse(raw, out long need))
                        byLevel[lv] = need;
                }
                if (byLevel.Count > 0)
                    t._needExp[type] = byLevel;
            }
        }

        // ---- CharacterCardBreak.txt ----
        var brk = SettingTable.Load(Path.Combine(dir, BreakFileName));
        if (brk is not null)
        {
            foreach (var row in brk.Rows)
            {
                int type = brk.Int(row, "Type", -1);
                if (type < 0)
                    continue;
                var steps = new List<CardBreakStep>();
                for (int n = 1; n <= 3; n++)
                {
                    int maxLv = brk.Int(row, $"MaxLevel{n}", 0);
                    if (maxLv <= 0)
                        continue;
                    steps.Add(new CardBreakStep(
                        Items: ParseGdplList(brk.Str(row, $"BreakItem{n}")),
                        CurrencyId: brk.Int(row, $"CurrencyId{n}", 0),
                        CurrencyCount: brk.Int(row, $"CurrencyCount{n}", 0),
                        MaxLevel: maxLv));
                }
                t._break[type] = new CardBreakRow(type, brk.Int(row, "MaxLevel0", 0), steps);
            }
        }

        // ---- CardRefitCost.txt（改造消耗，先于 CardRefit.txt 载入：后者按 CostType 引用它）----
        // 取证：CardCommon:LoadCardRefitConfit（`CardCommon.lua:333..348`），表头 3 行
        // （`:336` LoadTabFile(refitcost_path, 3)）。
        var refitCost = new Dictionary<int, CardRefitCostRow>();
        var rc = SettingTable.Load(Path.Combine(dir, RefitCostFileName));
        if (rc is not null)
        {
            foreach (var row in rc.Rows)
            {
                int type = rc.Int(row, "Type", 0);
                if (type <= 0)
                    continue;                       // `:345` if tb.nType > 0 then
                var steps = new List<CardRefitCost>();
                for (int n = 1; n <= MaxRefitSteps; n++)
                {
                    steps.Add(new CardRefitCost(
                        // ★ RefitItem<n> 是**短横**分隔的 GDPLN、多项用逗号分隔
                        //   （样例 `[7-2-7-3-5,7-2-7-4-3]`）—— ParseGdplList 两者都吃。
                        Items: ParseGdplList(rc.Str(row, $"RefitItem{n}")),
                        CurrencyId: rc.Int(row, $"CurrencyId{n}", 0),
                        CurrencyCount: rc.Int(row, $"CurrencyCount{n}", 0)));
                }
                refitCost[type] = new CardRefitCostRow(type, steps);
            }
        }

        // ---- CardRefit.txt（哪些卡能改造 + 每档的同步率/任务前置）----
        // 取证：`CardCommon.lua:350..368`，表头 3 行（`:350`）。
        var rf = SettingTable.Load(Path.Combine(dir, RefitFileName));
        if (rf is not null)
        {
            foreach (var row in rf.Rows)
            {
                int g = rf.Int(row, "Genre", 0);
                int d = rf.Int(row, "Detail", 0);
                int p = rf.Int(row, "Particular", 0);
                int l = rf.Int(row, "Level", 0);
                if (g <= 0)
                    continue;

                int costType = rf.Int(row, "CostType", 0);
                var steps = new List<CardRefitStep>();
                for (int n = 1; n <= MaxRefitSteps; n++)
                {
                    steps.Add(new CardRefitStep(
                        MissionId: rf.Int(row, $"MissionId{n}", 0),
                        NeedSync: rf.Int(row, $"RefitSync{n}", 0)));
                }

                t._refit[(g, d, p, l)] = new CardRefitRow(
                    G: g, D: d, P: p, L: l,
                    CostType: costType,
                    Cost: refitCost.TryGetValue(costType, out var cr) ? cr : null,
                    Steps: steps);
            }
        }

        // ---- ItemRarityItem.txt ----
        var rar = SettingTable.Load(Path.Combine(dir, RarityItemFileName));
        if (rar is not null)
        {
            foreach (var row in rar.Rows)
            {
                int rarity = rar.Int(row, "Rarity", -1);
                if (rarity < 0)
                    continue;
                var byType = new Dictionary<int, (double C0, double C1, List<GdplN> Items)>();
                for (int type = 1; type <= 5; type++)
                {
                    var pr = ParseDoubleList(rar.Str(row, $"Param{type}"));
                    var it = ParseGdplList(rar.Str(row, $"DisItem{type}"), allowSixth: true);
                    byType[type] = (pr.Count > 0 ? pr[0] : 0, pr.Count > 1 ? pr[1] : 0, it);
                }
                t._rarity[rarity] = new RarityItemRow(rarity, byType);
            }
        }

        // ---- ItemRarityExp.txt（★ 同样是 2 行表头）----
        var rexp = SettingTable.LoadAuto(Path.Combine(dir, RarityExpFileName), "Rarity");
        if (rexp is not null)
        {
            foreach (var row in rexp.Rows)
            {
                int rarity = rexp.Int(row, "Rarity", -1);
                if (rarity < 0)
                    continue;
                string c1 = rexp.Str(row, "NeedExp1");
                string c2 = rexp.Str(row, "NeedExp2");
                if (c1.Length > 0 && int.TryParse(c1, out int cnt))
                    t._rarityBreakCount[rarity] = cnt;
                if (c2.Length > 0 && long.TryParse(c2, out long feed))
                    t._rarityFeedBase[rarity] = feed;
            }
        }

        Log.Info($"[card] 配置载入：经验曲线 {t._needExp.Count} 种 ExpType" +
                 $"（角色卡用 {CardExpType}，上限 {t.MaxLevel()} 级）、" +
                 $"突破类型 {t._break.Count} 种、分解稀有度 {t._rarity.Count} 档、" +
                 $"稀有度突破次数 {t._rarityBreakCount.Count} 档、喂卡返还 {t._rarityFeedBase.Count} 档、" +
                 $"经验素材 {ExpCards.Count} 项 + 技能饼干 {SkillCookies.Count} 项");
        return t;
    }

    // =================================================================
    //  稀有度维度（ItemRarityExp.txt）
    // =================================================================

    /// <summary>
    /// 该稀有度在 <c>ItemRarityExp.NeedExp1</c> 里记的「可突破几次」
    /// （1★:0 / 2★:1 / 3★:2 / 4★:3 / 5★:4）。
    ///
    /// <para><b>★★ 这是<u>遗留字段</u>，不要用它判突破次数</b></para>
    /// 权威口径是 <see cref="MaxBreakCount"/>（= 突破表配了几档）。判据：
    /// <list type="bullet">
    ///   <item>客户端 <c>CardCommon:GetMaxBreakCount</c>（<c>CardCommon.lua:809..811</c>）是
    ///     <c>return #self.CharacterBreakConfig[nBreakType].tbBreakItem</c>
    ///     —— <b>只数突破表配了几档，压根不读 <c>ItemRarityExp</c></b>；</item>
    ///   <item>唯一可能用到本字段的 <c>CardCommon.BreakCountType</c>（<c>:75</c>）
    ///     <b>全仓 0 处使用</b>（<c>grep -rn BreakCountType extracted/lua/</c> 只有定义那一行）；</item>
    ///   <item>★ 决定性反证：<c>CharacterCardBreak.txt</c> 里**每个** <c>Type</c>（含 1★ 的
    ///     <c>Type=11..14</c>）都配了 <b>3 档</b>（<c>MaxLevel1/2/3 = 50/60/70</c>）。
    ///     若按 <c>NeedExp1</c>，1★ 卡就是 0 次 ⇒ 那 3 档配置全成死数据，与配置意图矛盾。</item>
    /// </list>
    /// 保留本方法只为「将来想做稀有度维度的限制时有现成读数」，业务逻辑**不要调它**。
    /// </summary>
    public int RarityBreakCount(int rarity)
        => _rarityBreakCount.TryGetValue(rarity, out int v) ? v : 0;

    /// <summary>
    /// 「喂卡」时该稀有度素材提供的**基础返还经验**
    /// （<c>ItemRarityExp.NeedExp2</c> = <c>GetItemRarityExp(rarity, 2)</c>，
    /// 因为 <see cref="DecomposeExpType"/> = 2）。
    /// </summary>
    public long RarityFeedBaseExp(int rarity)
        => _rarityFeedBase.TryGetValue(rarity, out long v) ? v : 0;

    /// <summary>
    /// 一张卡最多能突破几次 = <b>突破表为该 <c>Type</c> 配了几档</b>。
    ///
    /// 逐字复刻客户端 <c>CardCommon:GetMaxBreakCount</c>（<c>CardCommon.lua:809..811</c>）：
    /// <code>
    /// return #self.CharacterBreakConfig[nBreakType].tbBreakItem;
    /// </code>
    /// ★ **不要**掺入 <see cref="RarityBreakCount"/>（<c>ItemRarityExp.NeedExp1</c>）——
    /// 那是遗留字段，理由见该方法注释。早期实现取过两者的 min，
    /// 结果 1★ 卡（<c>NeedExp1 = 0</c>）永远不能突破，与「每个 Type 都配了 3 档」矛盾。
    ///
    /// <paramref name="rarity"/> 保留在签名里只为兼容调用方，当前不参与计算。
    /// </summary>
    public int MaxBreakCount(int breakType, int rarity = 0)
        => BreakOf(breakType)?.Steps.Count ?? 0;

    /// <summary>
    /// 「吃卡」时一张素材卡能提供的经验，复刻 <c>CardCommon:GetCardExp</c>
    /// （<c>CardCommon.lua:739..743</c>）：
    /// <code>
    /// floor( min(该卡累计经验 * ExpConversion, MaxEXPProvide) + GetItemRarityExp(rarity, 2) )
    /// </code>
    /// ★ 只适用于**非同名卡**；同名卡走的是「只加技能等级」那条路
    /// （`UI_CardMain.lua:1999..2021`，技能体系本服未实现）。
    /// </summary>
    public long FeedCardExp(int level, int rarity, int expType = CardExpType)
    {
        long total = TotalExpBefore(level, expType);
        double converted = Math.Min(total * ExpConversion, MaxExpProvide);
        return (long)Math.Floor(converted + RarityFeedBaseExp(rarity));
    }

    // =================================================================
    //  升级经验
    // =================================================================

    /// <summary>
    /// 「从 <paramref name="level"/> 级升到下一级」所需经验（<c>ItemExp.NeedExp&lt;expType&gt;</c> 的一格）。
    /// 表里查不到（超出最大等级）返回 <b>0</b> —— 与客户端
    /// <c>CheckItemExpMax</c>（<c>Lib.lua:665..672</c>）「取到 0 就是满级」的口径一致。
    /// </summary>
    public long ExpToNextLevel(int level, int expType = CardExpType)
        => _needExp.TryGetValue(expType, out var byLevel) && byLevel.TryGetValue(level, out long need)
            ? need
            : 0;

    /// <summary>
    /// 本 ExpType 的最大等级 = 表里**该列有值**的最大 <c>Lv</c>。
    ///
    /// <para><b>★ 为什么不是「表的最大 Lv」</b></para>
    /// <c>ItemExp.txt</c> 有 <c>Lv</c> 到 <b>90</b> 行，但 <c>NeedExp1</c>（角色卡那一列）
    /// 在 <b>71..90 行是空值</b>（<c>awk -F'\t' 'NR>=3 && $2!=""{last=$1} END{print last}'</c> → <b>70</b>）
    /// ⇒ 角色卡的真实上限是 **70**。
    /// ★★ 这一点被**另一个独立数据源交叉印证**：<c>CharacterCardBreak.txt</c> 的
    /// <c>MaxLevel3</c>（三次突破后的上限）最大值也正好是 **70**。
    /// 两张互不相干的表给出同一个数字，可以确信。
    ///
    /// 实现上依赖 <see cref="LoadCore"/> 里「空值不入字典」的处理（`raw.Length == 0` 跳过），
    /// 所以这里直接取字典的最大 key 即可。
    /// </summary>
    public int MaxLevel(int expType = CardExpType)
    {
        if (!_needExp.TryGetValue(expType, out var byLevel) || byLevel.Count == 0)
            return 1;
        int max = 1;
        foreach (int lv in byLevel.Keys)
        {
            if (lv > max)
                max = lv;
        }
        return max;
    }

    /// <summary>
    /// 「从 0 级练到 <paramref name="level"/> 级」的累计经验 —— 即客户端
    /// <c>GetPreAllItemExp(nLevel, expType)</c>。分解金币与「吃卡」折算都用它。
    /// </summary>
    public long TotalExpBefore(int level, int expType = CardExpType)
    {
        long sum = 0;
        for (int i = 0; i < level; i++)
            sum += ExpToNextLevel(i, expType);
        return sum;
    }

    /// <summary>
    /// 逐级消化经验：返回 (新等级, 新残余经验)。
    ///
    /// 复刻 <c>GetItemNeedExp</c>（<c>Lib.lua:654..662</c>）的逆运算 ——
    /// 客户端那个函数算「到目标等级还差多少」，服务端要算「给了这么多经验能到几级」。
    /// <paramref name="maxLevel"/> 是**突破后的等级上限**（见 <see cref="MaxLevelFor"/>），
    /// 到顶后多余经验按客户端口径**保留在 <c>enhanceexp</c> 里**
    /// （<c>UI_CardMain.lua:2073</c> 用 <c>GetItemNeedExp(maxLevel, 0, maxLevel+1, ...)</c>
    /// 显示「满级后还在攒」的进度，说明溢出经验不丢）。
    ///
    /// <para><b>★★★ 逐字复刻 <c>GetItemDestLevel</c>（<c>Lib.lua:685..714</c>）的三条边界</b></para>
    /// 那是**升级族共用**函数（角色卡 <c>UI_CardMain.lua:1309</c> / <c>UI_CardSelect.lua:981</c>、
    /// 武器 <c>UI_WeaponStrong.lua:1365</c> 都调它），三条边界行为**互不相同**：
    /// <list type="number">
    ///   <item><b>入口已在突破上限</b> → <c>return maxLevel, srcExp + nAdd</c>
    ///     ⇒ 经验**不截断**照加（<c>enhanceexp</c> 无限涨，等下次突破一次性转等级）；</item>
    ///   <item><b>升级中撞突破上限</b> → <c>return maxLevel, destExp</c>
    ///     ⇒ **保留残余**（这正是「突破的意义」：解开 <c>maxLevel</c> 让囤的经验立刻生效）；</item>
    ///   <item>★ <b>撞硬满级</b>（表里 <c>NeedExp</c> 变 0，角色卡 = Lv70）→
    ///     <c>return destLevel, 0, destExp</c> ⇒ **残余归 0、经验作废**。
    ///     第 3 个返回值就是被丢掉的部分：客户端 <c>UI_WeaponStrong.lua:1369</c>
    ///     把它存进 <c>self.OOExp</c> 并提示玩家 —— 说明这部分经验**真的没了**。
    ///     ⇒ 服务端必须同样归 0，否则 Lv70 之后玩家的 <c>enhanceexp</c> 与客户端显示不一致。</item>
    /// </list>
    ///
    /// <para><b>★★ 一个踩过的坑：不能用 <c>need == 0</c> 判满级</b></para>
    /// <c>ItemExp.txt</c> 的 <b><c>Lv=0</c> 那一行 <c>NeedExp1</c> 就是 0</b>（占位行，
    /// 因为卡从 <b>1 级</b>起 —— <c>Item.EnhanceLevel</c> 默认 1）。
    /// 早期实现写 <c>if (need &lt;= 0) break;</c>，结果从 0 级开始消化时**第一步就退出**、
    /// 等级永远上不去（单测 <c>消化经验_与客户端公式互为逆运算</c> 逮到的就是这个）。
    /// 现在改为：**只用 <paramref name="maxLevel"/> 与表内最大等级判边界**，
    /// <c>need == 0</c> 时按「这一级不要经验」直接进级（与客户端逐格累加的语义一致）。
    /// </summary>
    public (int Level, long Exp) ApplyExp(int level, long exp, long gain, int maxLevel,
                                          int expType = CardExpType)
    {
        int tableMax = MaxLevel(expType);

        // ---- 边界 ①：入口就已经在突破上限 ⇒ 经验**不截断**照加（原作行为）----
        // GetItemDestLevel: `if maxLevel and srcLevel >= maxLevel then return maxLevel, srcExp + nAdd`
        // ⇒ enhanceexp 会无限涨，留着等下次突破解开上限后一次性转等级。
        if (level >= maxLevel)
            return (maxLevel, exp + gain);

        exp += gain;
        int ceiling = Math.Min(maxLevel, tableMax);

        while (level < ceiling)
        {
            long need = ExpToNextLevel(level, expType);
            // need == 0 的两种情形：① Lv0 占位行（要进级）；② 表里没这一级（不能再升）
            if (need == 0 && !HasLevel(level, expType))
                break;
            if (exp < need)
                break;
            exp -= need;
            level++;

            // ---- 边界 ②：撞突破上限 ⇒ **保留残余**（突破后可继续升）----
            if (level >= maxLevel)
                return (maxLevel, exp);

            // ---- 边界 ③：撞硬满级（表里 NeedExp 变 0）⇒ ★ 残余**作废**（归 0）----
            // GetItemDestLevel: `if needExp == 0 then return destLevel, 0, destExp end`
            // 第 3 个返回值 destExp 就是「溢出被丢掉的经验」——客户端 UI_WeaponStrong.lua:1369
            // 把它存进 self.OOExp 并提示玩家，说明这部分经验**真的没了**。
            // ⇒ 服务端必须同样归 0，否则玩家在 Lv70 攒下的 enhanceexp 与客户端显示不一致。
            if (ExpToNextLevel(level, expType) == 0 && HasLevel(level, expType))
                return (level, 0);
        }
        return (level, exp);
    }

    /// <summary>表里是否登记了这一级（用于区分「Lv0 占位的 0」与「超出表范围」）。</summary>
    private bool HasLevel(int level, int expType)
        => _needExp.TryGetValue(expType, out var byLevel) && byLevel.ContainsKey(level);

    // =================================================================
    //  突破
    // =================================================================

    /// <summary>
    /// 突破配置的 key：<b><c>Type = WeaponType * 10 + Rarity</c></b>。
    ///
    /// <para><b>★★ 这条编码规则是程序化验证过的（269/269 全命中）</b></para>
    /// 对 <c>CharacterCard.txt</c> 全部 269 行算 <c>WeaponType(第43列) * 10 + Rarity(第22列)</c>，
    /// 逐个查 <c>CharacterCardBreak.txt</c> 的 <c>Type</c> 全集
    /// <c>{11..14, 21..24, 31..34, 41..44, 51..54}</c>（20 个两位数）⇒ <b>零未命中</b>。
    /// 复现：见 <c>work/gap-analysis.md</c> §2.4②。
    ///
    /// ★ 表里另有 5 个三位数 <c>Type</c>（<c>141/241/341/441/541</c>）**不被任何普通卡命中**，
    /// 属特殊卡，语义**未取证** —— 查不到就按「不可突破」处理，不要瞎猜。
    /// </summary>
    public static int BreakTypeOf(int weaponType, int rarity) => weaponType * 10 + rarity;

    /// <summary>取某个突破类型的配置；未配置返回 null（⇒ 该卡不可突破）。</summary>
    public CardBreakRow? BreakOf(int breakType)
        => _break.TryGetValue(breakType, out var row) ? row : null;

    /// <summary>
    /// 取某张卡的改造配置；未配置返回 null（⇒ 该卡不可改造）。
    /// 对应客户端 <c>CardCommon:GetCardRefitInfo</c> / <c>CanRefit</c>（<c>CardCommon.lua:414</c>）。
    /// ★ 现网只有 **27** 张卡可改造。
    /// </summary>
    public CardRefitRow? RefitOf(int g, int d, int p, int l)
        => _refit.TryGetValue((g, d, p, l), out var row) ? row : null;

    /// <summary>可改造的卡数（日志/探针用）。</summary>
    public int RefitCardCount => _refit.Count;

    /// <summary>
    /// 某卡在 <paramref name="breakLevel"/> 次突破后的**等级上限**。
    /// <c>breakLevel = 0</c> → <c>MaxLevel0</c>；1/2/3 → 对应 <c>MaxLevel1..3</c>。
    /// 没有突破配置时退化成 <see cref="MaxLevel"/>（表驱动的全局上限）。
    /// </summary>
    public int MaxLevelFor(int breakType, int breakLevel)
    {
        var row = BreakOf(breakType);
        if (row is null)
            return MaxLevel();
        if (breakLevel <= 0)
            return row.MaxLevel0 > 0 ? row.MaxLevel0 : MaxLevel();
        int idx = Math.Min(breakLevel, row.Steps.Count) - 1;
        return idx >= 0 && idx < row.Steps.Count ? row.Steps[idx].MaxLevel : row.MaxLevel0;
    }

    // =================================================================
    //  分解
    // =================================================================

    /// <summary>取某稀有度的分解配置；未配置返回 null。</summary>
    public RarityItemRow? RarityOf(int rarity)
        => _rarity.TryGetValue(rarity, out var row) ? row : null;

    /// <summary>
    /// 分解得到的金币，逐字复刻 <c>CardCommon:GetCardPrice</c>（<c>CardCommon.lua:746..750</c>）：
    /// <code>
    /// local coinparam = GetItemRarityItem(nRarity, 1)          -- Param1 = [c0, c1]
    /// local preexp    = GetPreAllItemExp(nLevel, expType)      -- 累计经验
    /// return math.floor(preexp * coinparam[1] + nRarity * coinparam[2])
    /// </code>
    /// ★ 必须与客户端同源：<c>UI_CardList.lua:657</c> 在弹确认框**之前**就用同一公式
    /// 累加出预览金额给玩家看了。
    /// </summary>
    public long DecomposeGold(int level, int rarity, int expType = CardExpType)
    {
        var row = RarityOf(rarity);
        if (row is null)
            return 0;
        var (c0, c1, _) = row.For(1);
        long preExp = TotalExpBefore(level, expType);
        return (long)Math.Floor(preExp * c0 + rarity * c1);
    }

    /// <summary>
    /// 分解返还的材料候选（<c>DisItem1</c>），每项末位是**万分比概率**
    /// （<c>CardCommon:GetDecomposeItem</c>，<c>CardCommon.lua:753..763</c>：
    /// <c>if MathRandom(10000) &lt;= v[6] then 掉落 {v1..v5}</c>）。
    /// ★ 概率为 0 的项**永不掉落**（<c>MathRandom(10000)</c> 最小返回 1）。
    /// </summary>
    public IReadOnlyList<GdplN> DecomposeItems(int rarity)
    {
        var row = RarityOf(rarity);
        return row is null ? Array.Empty<GdplN>() : row.For(1).Items;
    }

    /// <summary>
    /// 同 <see cref="DecomposeItems(int)"/>，但可指定 <c>ItemRarityItem.txt</c> 的**组号**。
    ///
    /// ★ 存在的理由：这张表一行装了 5 组 <c>Param&lt;k&gt;</c>/<c>DisItem&lt;k&gt;</c>，
    /// **不同物品类型用不同组** —— 角色卡用第 <b>1</b> 组（`CardCommon.lua:747` 传 1），
    /// 武器用第 <b>2</b> 组（`WeaponCommon.lua:597` <c>GetItemRarityItem(rarity, 2)</c>）。
    /// 用错组会让分解金币与客户端的预览金额不一致（那边在弹确认框**之前**就算好显示了）。
    /// </summary>
    /// <param name="rarity">稀有度。</param>
    /// <param name="paramGroup">组号（角色卡 1、武器 2）。</param>
    public IReadOnlyList<GdplN> DecomposeItems(int rarity, int paramGroup)
    {
        var row = RarityOf(rarity);
        return row is null ? Array.Empty<GdplN>() : row.For(paramGroup).Items;
    }

    // =================================================================
    //  经验素材索引 ↔ GDPL
    // =================================================================

    /// <summary>
    /// 把上行 <c>items</c> 里的**素材索引**解码成 GDPL
    /// （复刻 <c>CardCommon:GetEnhanceCardGDPL</c>，<c>CardCommon.lua:651..663</c>）。
    /// 索引非法返回 null。
    ///
    /// <list type="bullet">
    ///   <item><c>1 .. ExpCards.Count</c> → <see cref="ExpCards"/>[idx-1]（经验卡）</item>
    ///   <item><c>ExpCards.Count + 1 / + 2</c> → <see cref="SkillCookies"/>（技能饼干）</item>
    /// </list>
    /// </summary>
    public static (int G, int D, int P, int L)? DecodeExpItem(int idx)
    {
        if (idx >= 1 && idx <= ExpCards.Count)
            return ExpCards[idx - 1];
        int off = idx - ExpCards.Count;
        if (off >= 1 && off <= SkillCookies.Count)
            return SkillCookies[off - 1];
        return null;
    }

    /// <summary>该索引是否为技能饼干（加技能经验、不加等级经验）。</summary>
    public static bool IsSkillCookie(int idx)
        => idx > ExpCards.Count && idx <= ExpCards.Count + SkillCookies.Count;

    /// <summary>
    /// 经验素材的经验倍率（<c>CardCommon:GetExpRate</c>，<c>CardCommon.lua:700..710</c>）：
    /// 素材 <c>P == 4</c>（通用）或 <c>P</c> 与卡属性匹配时为 <see cref="SameAttrExpRate"/>，否则 1。
    /// </summary>
    public static double ExpRate(int cardAttribute, int itemParticular)
    {
        if (itemParticular == UniversalExpItemParticular)
            return SameAttrExpRate;
        return AttributeToExpItemParticular.TryGetValue(cardAttribute, out int p) && p == itemParticular
            ? SameAttrExpRate
            : 1.0;
    }

    // =================================================================
    //  解析小工具
    // =================================================================

    /// <summary>
    /// 解析 <c>[G-D-P-L-N,...]</c> 或 <c>[a];[b]</c> 形式的 GDPL 列表。
    /// <paramref name="allowSixth"/> 为真时容忍第 6 段（<c>ItemRarityItem.DisItem*</c> 的概率）。
    /// </summary>
    internal static List<GdplN> ParseGdplList(string raw, bool allowSixth = false)
    {
        var result = new List<GdplN>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var chunk in raw.Split(new[] { '[', ']', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chunk.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
                continue;
            if (!int.TryParse(parts[0], out int g) || !int.TryParse(parts[1], out int d) ||
                !int.TryParse(parts[2], out int p) || !int.TryParse(parts[3], out int l))
                continue;
            int n = parts.Length > 4 && int.TryParse(parts[4], out int nn) ? nn : 1;
            int prob = allowSixth && parts.Length > 5 && int.TryParse(parts[5], out int pp) ? pp : 0;
            result.Add(new GdplN(g, d, p, l, n, prob));
        }
        return result;
    }

    /// <summary>解析 <c>[0.3,500]</c> 形式的浮点列表。</summary>
    internal static List<double> ParseDoubleList(string raw)
    {
        var result = new List<double>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;
        foreach (var s in raw.Split(new[] { '[', ']', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (double.TryParse(s, System.Globalization.NumberStyles.Float,
                                System.Globalization.CultureInfo.InvariantCulture, out double v))
                result.Add(v);
        }
        return result;
    }
}

/// <summary>一个 <c>(G,D,P,L,N)</c> 元组 + 可选的万分比概率（<c>ItemRarityItem.DisItem*</c> 末位）。</summary>
public sealed record GdplN(int G, int D, int P, int L, int N, int Probability = 0);

/// <summary>突破的一段（<c>CharacterCardBreak.txt</c> 的 <c>BreakItem&lt;n&gt;</c> 那一组列）。</summary>
public sealed record CardBreakStep(
    IReadOnlyList<GdplN> Items,
    int CurrencyId,
    int CurrencyCount,
    int MaxLevel);

/// <summary>
/// 一个突破类型（<c>CharacterCardBreak.txt</c> 的一行）。
/// <c>MaxLevel0</c> = 0 次突破时的等级上限；<see cref="Steps"/> 是 1..3 次突破。
/// </summary>
public sealed record CardBreakRow(int Type, int MaxLevel0, IReadOnlyList<CardBreakStep> Steps);

/// <summary>改造一档的消耗（<c>CardRefitCost.txt</c> 的 <c>RefitItem&lt;n&gt;</c> 那组列）。</summary>
public sealed record CardRefitCost(
    IReadOnlyList<GdplN> Items,
    int CurrencyId,
    int CurrencyCount);

/// <summary>一个改造消耗类型（<c>CardRefitCost.txt</c> 的一行，按 <c>Type</c> 索引）。</summary>
public sealed record CardRefitCostRow(int Type, IReadOnlyList<CardRefitCost> Steps);

/// <summary>
/// 改造一档的前置（<c>CardRefit.txt</c> 的 <c>MissionId&lt;n&gt;</c> / <c>RefitSync&lt;n&gt;</c>）。
/// </summary>
/// <param name="MissionId">
/// 需要完成的成就 id（0 = 无前置）。客户端判据 <c>UI_CardMain.lua:2490..2494</c>：
/// <c>MissionManager:GetOtherAchieve(id)</c> + <c>CheckInfoState(conf, 3)</c>。
/// </param>
/// <param name="NeedSync">
/// 需要的同步率（★ 单位是**千分之一**：<c>UI_CardMain.lua:2454</c> 显示时除以 1000）。
/// 判据 <c>:2451</c> <c>self.data.SyncRate &gt;= tbConf['nRefitSync'..idx]</c>。
/// </param>
public sealed record CardRefitStep(int MissionId, int NeedSync);

/// <summary>
/// 一张可改造角色卡的配置（<c>CardRefit.txt</c> 的一行）。
/// ★ 只有出现在这张表里的卡才能改造（<c>CardCommon:CanRefit</c>，<c>CardCommon.lua:414..419</c>）。
/// </summary>
public sealed record CardRefitRow(
    int G, int D, int P, int L,
    int CostType,
    CardRefitCostRow? Cost,
    IReadOnlyList<CardRefitStep> Steps)
{
    /// <summary>
    /// 该卡最多能改造几次 = 「配了同步率门槛」的档位数。
    /// ★ 不能直接用 <see cref="CardTable.MaxRefitSteps"/>（5）——
    /// 现网多数卡只配到第 4 档，按 5 判会让第 5 次改造「条件永远不满足却又不被拒绝」。
    /// </summary>
    public int MaxRefit => Steps.Count(s => s.NeedSync > 0);
}

/// <summary>
/// <c>ItemRarityItem.txt</c> 的一行（按稀有度）。
/// 5 组 <c>Param&lt;n&gt;</c>/<c>DisItem&lt;n&gt;</c> 对应 5 种素材类别，角色卡用 <c>n = 1</c>
/// （<c>CardCommon:GetItemRarityItem(rarity, 1)</c>，<c>CardCommon.lua:853..859</c>）。
/// </summary>
public sealed record RarityItemRow(
    int Rarity,
    IReadOnlyDictionary<int, (double C0, double C1, List<GdplN> Items)> ByType)
{
    /// <summary>取第 <paramref name="type"/> 组系数与掉落项；缺省返回全零/空。</summary>
    public (double C0, double C1, List<GdplN> Items) For(int type)
        => ByType.TryGetValue(type, out var v) ? v : (0, 0, new List<GdplN>());
}
