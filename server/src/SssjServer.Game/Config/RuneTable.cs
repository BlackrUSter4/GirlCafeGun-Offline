using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一枚符文（基因模块）的模板配置（<c>Item/Rune.txt</c> 的一行）。
/// </summary>
/// <param name="G">genre，恒 3（<c>emGenreType.Rune</c>）。</param>
/// <param name="D">Detail。</param>
/// <param name="P">Particular。</param>
/// <param name="L">Level（阶）。</param>
/// <param name="Rarity">
/// 星级。★★★ **强化费用按它分档**：客户端
/// <c>ItemLogic:GetItemExp(level, 5, rarity)</c>（<c>UI_ModuleMain.lua:466</c>）
/// 会去查 <c>ItemExp.txt</c> 的 <b><c>NeedExp5_&lt;rarity&gt;</c></b> 列
/// （<c>ItemLogic.lua:103..110</c>：<c>rarity</c> 非 nil 时列名拼成 <c>"NeedExp"..type.."_"..rarity</c>）。
/// ⇒ 用 <c>NeedExp5</c> 会让 2/3/4★ 符文**都按 1★ 收费**（实测 1★ 500 / 2★ 750 / 3★ 1250 / 4★ 2500）。
/// </param>
/// <param name="BreakConfigId">
/// 突破配置 id（<c>BreakConfigID</c> 列）→ 索引 <see cref="RuneTable.BreakOf"/>。
/// <c>RuneLogic:GetBreakId</c>（<c>Rune.lua:171..173</c>）。
/// </param>
/// <param name="RecastItems">
/// 重铸（洗练）消耗的道具 <c>[G,D,P,L,N]</c>（<c>RecastGDPLN</c> 列，可空）。
/// </param>
/// <param name="RecastMoneyId">重铸消耗的货币 id（<c>RecastMoney</c> 列第 1 位，0 = 不消耗）。</param>
/// <param name="RecastMoneyCount">重铸消耗的货币数量（<c>RecastMoney</c> 列第 2 位）。</param>
/// <param name="SellCurrencyId">退役（分解）返还的代币 id。</param>
/// <param name="SellCurrencyCount">退役返还的代币数量。</param>
public sealed record RuneRow(
    int G, int D, int P, int L,
    int Rarity,
    int BreakConfigId,
    IReadOnlyList<GdplN> RecastItems,
    int RecastMoneyId,
    int RecastMoneyCount,
    int SellCurrencyId,
    int SellCurrencyCount)
{
    public override string ToString()
        => $"符文 {G}-{D}-{P}-{L}（{Rarity}★ breakId={BreakConfigId}）";
}

/// <summary>突破一档的消耗与上限（<c>RuneBreak.txt</c> 的 <c>BreakItem&lt;n&gt;</c> 那组列）。</summary>
/// <param name="Items">消耗道具（<c>BreakItem&lt;n&gt;</c>）。</param>
/// <param name="CurrencyId">
/// 消耗的货币 id。★★ 取自 <c>CurrencyCount&lt;n&gt;</c> 列的**第 1 位** ——
/// 那一列不是纯数量，而是 <c>[货币id-数量]</c>（实测 <c>[2-20000]</c> = 金币 20000）。
/// 按「纯数量」解析会把 <b>2</b> 当成消耗量 ⇒ 突破几乎免费。
/// </param>
/// <param name="CurrencyCount">消耗数量（<c>CurrencyCount&lt;n&gt;</c> 列的第 2 位）。</param>
/// <param name="MaxLevel">该次突破后的等级上限（<c>MaxLevel&lt;n&gt;</c>）。</param>
/// <param name="DisItems">该档的分解返还（<c>DisItem&lt;n&gt;</c>，本域暂未使用）。</param>
public sealed record RuneBreakStep(
    IReadOnlyList<GdplN> Items,
    int CurrencyId,
    int CurrencyCount,
    int MaxLevel,
    IReadOnlyList<GdplN> DisItems);

/// <summary>一个突破配置（<c>RuneBreak.txt</c> 的一行，按 <c>BreakConfigID</c> 索引）。</summary>
public sealed record RuneBreakRow(
    int BreakConfigId,
    int Rarity,
    int MaxLevel0,
    IReadOnlyList<RuneBreakStep> Steps)
{
    /// <summary>
    /// 最多能突破几次 = 「配了 <c>MaxLevel&lt;n&gt;</c>」的档位数。
    /// 判据 <c>RuneLogic:IsBreakMax</c>（<c>Rune.lua:198..207</c>）：
    /// <c>MaxLevel&lt;n&gt;</c> 有而 <c>MaxLevel&lt;n+1&gt;</c> 没有 ⇒ 已到上限。
    /// </summary>
    public int MaxBreak => Steps.Count;
}

/// <summary>
/// 符文（基因模块）配置：主表 + 突破表 + 重铸技能池。
///
/// <para><b>1. ★★★ 重铸池 <c>RuneSkillList.txt</c> 是纯服务端表（本轮新发现）</b></para>
/// 旧文档（<c>docs/05</c> §30.1）称「重铸随机池表不在本地快照里」⇒ 判 33/34 做不了 —— **已作废**。
/// 表就在 <c>extracted/setting/RuneSkillList.txt</c>（27 条）。
/// 它没被发现的原因是 <b>客户端零加载点</b>（<c>grep -rn 'RuneSkillList' extracted/lua/</c> 无命中）
/// ⇒ 原服只有服务端读它，客户端只渲染下发结果。
/// ★ 因此它的表头行数**不能**用 <c>LoadTabFile</c> 定案，只能按结构判断
/// （第 1 行类型行 <c>INT…</c>、第 2 行中文说明、第 3 行英文列名 <c>Id/RuneRarity/Rate/…</c>
/// —— 与同目录其它 3 行表头的表一致）。
///
/// <para><b>2. 强化费用按星级分档（最容易写错的一条）</b></para>
/// 见 <see cref="RuneRow.Rarity"/> 的取证：列名是 <c>NeedExp5_&lt;rarity&gt;</c>。
///
/// <para><b>3. ★ 等级上限还与「关卡进度」挂钩</b></para>
/// <c>RuneLogic:GetMaxLevel</c>（<c>Rune.lua:176..195</c>）：
/// <code>
/// maxLevel = 5
/// if 通关 5-12 then maxLevel = 10 end
/// if 通关 6-12 then maxLevel = 15 end
/// if maxLevel == 15 and 有突破配置 then maxLevel = MaxLevel&lt;BreakNum&gt; end
/// </code>
/// ⇒ **没通关 6-12 的号，符文等级上限恒 ≤ 15，突破配置的上限压根不生效**。
/// 服务端按同一公式复核（<see cref="RuneTable.MaxLevelFor"/>）。
/// </summary>
public sealed class RuneTable
{
    /// <summary>符文主表（<c>Rune.lua:19</c> <c>LoadTabFile("Item/Rune.txt", 3)</c>）。</summary>
    public const string RuneFileName = "Rune.txt";

    /// <summary>突破表（<c>Rune.lua:154</c> <c>LoadTabFile("Item/RuneBreak.txt", 3)</c>）。</summary>
    public const string BreakFileName = "RuneBreak.txt";

    /// <summary>重铸技能池（★ 纯服务端表，见类注释 §1）。</summary>
    public const string SkillListFileName = "RuneSkillList.txt";

    /// <summary>符文的 genre（<c>emGenreType.Rune</c>）。</summary>
    public const uint RuneGenre = 3;

    /// <summary>经验表里符文的类型列号 = <b>5</b>（<c>WeaponCommon.lua:59</c> <c>ITEMEXP_Moudle = 5</c>）。</summary>
    public const int ItemExpTypeRune = 5;

    /// <summary>突破最多几档（表里 <c>BreakItem1..4</c>）。</summary>
    public const int MaxBreakSteps = 4;

    /// <summary>没通关 5-12 时的等级上限（<c>Rune.lua:178</c>）。</summary>
    public const int MaxLevelBase = 5;

    /// <summary>通关 5-12 后的等级上限（<c>Rune.lua:182</c>）。</summary>
    public const int MaxLevelAfter512 = 10;

    /// <summary>通关 6-12 后的等级上限（<c>Rune.lua:185</c>）——只有到这一档，突破配置才生效。</summary>
    public const int MaxLevelAfter612 = 15;

    private readonly Dictionary<(int G, int D, int P, int L), RuneRow> _runes = new();
    private readonly Dictionary<int, RuneBreakRow> _break = new();
    private readonly List<RuneSkillRow> _skills = new();

    /// <summary>
    /// (稀有度, 等级) → 从该等级升到下一级所需金币。
    /// ★★★ 列名是 <c>NeedExp5_&lt;rarity&gt;</c>（不是 <c>NeedExp5</c>）——
    /// <c>ItemLogic.lua:103..110</c> 在 rarity 非 nil 时拼 <c>"NeedExp"..type.."_"..rarity</c>。
    /// 实测 Lv1→2：1★ 500 / 2★ 750 / 3★ 1250 / 4★ 2500。
    /// </summary>
    private readonly Dictionary<(int Rarity, int Level), int> _enhanceCost = new();

    /// <summary>空表（配置缺失时的降级实例）。</summary>
    public static RuneTable Empty { get; } = new();

    /// <summary>符文模板数。</summary>
    public int RuneCount => _runes.Count;

    /// <summary>突破配置数。</summary>
    public int BreakCount => _break.Count;

    /// <summary>重铸技能池（按表顺序）。</summary>
    public IReadOnlyList<RuneSkillRow> Skills => _skills;

    /// <summary>取符文模板；不存在返回 null。</summary>
    public RuneRow? RuneOf(int g, int d, int p, int l)
        => _runes.TryGetValue((g, d, p, l), out var r) ? r : null;

    /// <summary>取突破配置；不存在返回 null（⇒ 该符文不可突破）。</summary>
    public RuneBreakRow? BreakOf(int breakConfigId)
        => _break.TryGetValue(breakConfigId, out var r) ? r : null;

    /// <summary>
    /// 符文等级上限，逐字对应 <c>RuneLogic:GetMaxLevel</c>（<c>Rune.lua:176..195</c>）。
    /// </summary>
    /// <param name="breakConfigId">符文的 <c>BreakConfigID</c>。</param>
    /// <param name="breakLevel">已突破次数。</param>
    /// <param name="passed512">是否通关 5-12。</param>
    /// <param name="passed612">是否通关 6-12。</param>
    public int MaxLevelFor(int breakConfigId, int breakLevel, bool passed512, bool passed612)
    {
        int maxLevel = MaxLevelBase;
        if (passed512) maxLevel = MaxLevelAfter512;
        if (passed612) maxLevel = MaxLevelAfter612;

        // ★ 只有到 15 这一档，突破配置才接管（Rune.lua:187）
        if (maxLevel != MaxLevelAfter612)
            return maxLevel;

        var row = BreakOf(breakConfigId);
        if (row is null)
            return maxLevel;
        if (breakLevel <= 0)
            return row.MaxLevel0 > 0 ? row.MaxLevel0 : maxLevel;

        int idx = Math.Min(breakLevel, row.Steps.Count) - 1;
        return idx >= 0 && idx < row.Steps.Count ? row.Steps[idx].MaxLevel : maxLevel;
    }

    /// <summary>
    /// 从 <paramref name="level"/> 级强化到下一级所需金币。
    /// 查不到返回 0（调用方据此拒绝，不要当免费）。
    /// </summary>
    public int EnhanceCost(int rarity, int level)
        => _enhanceCost.TryGetValue((rarity, level), out int c) ? c : 0;

    /// <summary>经验表覆盖的最高等级（用于探针断言）。</summary>
    public int EnhanceCostRows => _enhanceCost.Count;

    /// <summary>
    /// 解析 <c>[a-b]</c> / <c>[a,b]</c> 形式的**二元组**（货币 id + 数量）。
    /// 用在 <c>RecastMoney</c> 与 <c>CurrencyCount&lt;n&gt;</c> 两列上 ——
    /// ★ 后者名字叫 Count 但实际带 id，见 <see cref="RuneBreakStep.CurrencyId"/>。
    /// </summary>
    internal static (int Id, int Count) ParsePair(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return (0, 0);
        var parts = raw.Trim('[', ']')
                       .Split(new[] { '-', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0], out int a) && int.TryParse(parts[1], out int b))
            return (a, b);
        return (0, 0);
    }

    /// <summary>载入三张表。缺表不抛，只降级。</summary>
    public static RuneTable Load(string settingDir)
    {
        var t = new RuneTable();

        // ---- Rune.txt（表头 3 行）----
        var rune = SettingTable.Load(Path.Combine(settingDir, RuneFileName));
        if (rune is not null)
        {
            foreach (var row in rune.Rows)
            {
                int g = rune.Int(row, "Genre", 0);
                if (g <= 0) continue;
                int d = rune.Int(row, "Detail", 0);
                int p = rune.Int(row, "Particular", 0);
                int l = rune.Int(row, "Level", 0);

                // RecastMoney 是 [货币id-数量]（客户端 Parse2Item，短横或逗号分隔）
                var (moneyId, moneyCount) = ParsePair(rune.Str(row, "RecastMoney"));

                t._runes[(g, d, p, l)] = new RuneRow(
                    G: g, D: d, P: p, L: l,
                    Rarity: rune.Int(row, "Rarity", 1),
                    BreakConfigId: rune.Int(row, "BreakConfigID", 0),
                    RecastItems: CardTable.ParseGdplList(rune.Str(row, "RecastGDPLN")),
                    RecastMoneyId: moneyId,
                    RecastMoneyCount: moneyCount,
                    SellCurrencyId: rune.Int(row, "CurrencyId", 0),
                    SellCurrencyCount: rune.Int(row, "CurrencyCount", 0));
            }
        }

        // ---- RuneBreak.txt（表头 3 行）----
        var brk = SettingTable.Load(Path.Combine(settingDir, BreakFileName));
        if (brk is not null)
        {
            foreach (var row in brk.Rows)
            {
                int id = brk.Int(row, "BreakConfigID", 0);
                if (id <= 0) continue;

                var steps = new List<RuneBreakStep>();
                for (int n = 1; n <= MaxBreakSteps; n++)
                {
                    // ★ 判据同 RuneLogic:IsBreakMax —— 有 MaxLevel<n> 才算配了这一档
                    int maxLv = brk.Int(row, $"MaxLevel{n}", 0);
                    if (maxLv <= 0) break;
                    // ★★ CurrencyCount<n> 是 [货币id-数量]，不是纯数量（实测 [2-20000]）
                    var (curId, curCount) = ParsePair(brk.Str(row, $"CurrencyCount{n}"));
                    steps.Add(new RuneBreakStep(
                        Items: CardTable.ParseGdplList(brk.Str(row, $"BreakItem{n}")),
                        CurrencyId: curId,
                        CurrencyCount: curCount,
                        MaxLevel: maxLv,
                        DisItems: CardTable.ParseGdplList(brk.Str(row, $"DisItem{n}"))));
                }

                t._break[id] = new RuneBreakRow(
                    BreakConfigId: id,
                    Rarity: brk.Int(row, "Rarity", 1),
                    MaxLevel0: brk.Int(row, "MaxLevel0", 0),
                    Steps: steps);
            }
        }

        // ---- RuneSkillList.txt（★ 纯服务端表，表头按结构判为 3 行）----
        var skl = SettingTable.Load(Path.Combine(settingDir, SkillListFileName));
        if (skl is not null && skl.Columns.ContainsKey("Id"))
        {
            foreach (var row in skl.Rows)
            {
                int id = skl.Int(row, "Id", 0);
                if (id <= 0) continue;
                t._skills.Add(new RuneSkillRow(
                    Id: id,
                    RuneRarity: skl.Int(row, "RuneRarity", 0),
                    Rate: skl.Int(row, "Rate", 0)));
            }
        }

        // ---- ItemExp.txt 的符文列（★ 表头 2 行：ItemLogic.lua:14 LoadTabFile(..., 2)）----
        var exp = SettingTable.Load(Path.Combine(settingDir, "ItemExp.txt"), headerRow: 1);
        if (exp is not null)
        {
            foreach (var row in exp.Rows)
            {
                int lv = exp.Int(row, "Lv", -1);
                if (lv < 0) continue;
                for (int rarity = 1; rarity <= 4; rarity++)
                {
                    // ★★★ NeedExp5_<rarity>，不是 NeedExp5
                    string col = $"NeedExp{ItemExpTypeRune}_{rarity}";
                    if (!exp.Columns.ContainsKey(col)) continue;
                    int need = exp.Int(row, col, 0);
                    if (need > 0)
                        t._enhanceCost[(rarity, lv)] = need;
                }
            }
        }

        Log.Info($"[setting] 符文配置：模板 {t._runes.Count} 枚、突破配置 {t._break.Count} 组"
                 + $"、重铸技能池 {t._skills.Count} 条、强化费用 {t._enhanceCost.Count} 档");
        return t;
    }
}

/// <summary>
/// 重铸技能池的一条（<c>RuneSkillList.txt</c>）。
///
/// ★ 本轮只接了 <see cref="Id"/>/<see cref="RuneRarity"/>/<see cref="Rate"/> 三列 ——
/// 6 个属性列（<c>CritRate</c>/<c>CritDamageRate</c>/…）的值是
/// <c>[0,50],[1,50],[2,50],[3,50],[4,75]</c> 形式的**「突破等级 → 数值」阶梯**，
/// 语义（尤其「属性叠加到符文哪个字段」）**尚未取证** ⇒ 留待实现 33/34 时再接，
/// 不在这里瞎猜（本仓铁律：不确定就标未取证）。
/// </summary>
/// <param name="Id">技能 id（10001 起）。</param>
/// <param name="RuneRarity">适用的符文星级（1..4）—— 抽取时先按星级过滤。</param>
/// <param name="Rate">权重（同星级内按它归一化抽取）。</param>
public sealed record RuneSkillRow(int Id, int RuneRarity, int Rate);
