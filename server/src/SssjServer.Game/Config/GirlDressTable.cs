using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一条 Spine 皮肤配置（<c>Girl/SpineSuit.txt</c> 的一行）。
///
/// ★ 这张表是「皮肤」的主表，与角色卡自带的服装（<c>CharacterCard.txt</c> 的
/// <c>ModelId</c> 列）是**两个来源**，但两者都用同一套 <c>SuitValue</c> 标记
/// （任务值 group 4，见 <see cref="GmUnlock.SuitGroup"/>）。
/// </summary>
/// <param name="GirlId">少女 id（<c>GirlId</c> 列）。</param>
/// <param name="SuitId">
/// ★ 表头原文写明「衣服id(**仅用来保存是否获得**)」—— 现网**全部是 1**，
/// 与 <c>ModelId</c> 不是一回事，别拿它当换装参数。
/// </param>
/// <param name="ModelId">
/// ★★ **这才是换装用的 id**（<c>ChangeCloth</c> 的 <c>nSuit</c> / <c>GirlInfo.modelid</c>）。
/// 取证 <c>GirlConfig.lua:340</c> <c>self.tbSuitConfig[GirlId][tb.ModelId] = tb</c>
/// —— 客户端就是按 <c>(GirlId, ModelId)</c> 建索引，所以
/// <c>CheckIsSpineSuit(girlId, suitId)</c> 的第二个实参实际上是 <b>ModelId</b>。
/// </param>
/// <param name="HasModel">
/// <c>IsModel</c> 列（表头「是否有模型」）。★ 决定「战斗服」开关能不能用：
/// <c>UI_GirlClothes.lua:348</c> 皮肤**没有 3D 模型**时把 toggle 隐藏并
/// 自动发一条 <c>SetModelInFight(nId)</c>（不带第二参 ⇒ <c>nUse = 0</c>）。
/// </param>
/// <param name="Gdpl">
/// 对应的皮肤道具 <c>[G,D,P,L]</c>（现网 G 恒 19、ClassName <c>SuitItem</c>）。
/// 用于「发了皮肤道具 → 该套服饰变成已拥有」这条链（<see cref="GirlDressTable.SuitForItem"/>）。
/// </param>
/// <param name="Level">解锁所需好感度等级（<c>Level</c> 列，现网恒 4，未参与服务端校验）。</param>
/// <param name="ShopId">售卖所在商店 id（<c>ShopId</c> 列，现网多为空）。</param>
/// <param name="ClothName">服饰名的多语言 key（日志用）。</param>
public sealed record SpineSuitRow(
    uint GirlId,
    int SuitId,
    int ModelId,
    bool HasModel,
    (uint G, uint D, uint P, uint L)? Gdpl,
    int Level,
    int ShopId,
    string ClothName)
{
    public override string ToString()
        => $"少女{GirlId} 皮肤 model={ModelId}"
         + (Gdpl is { } q ? $"（道具 {q.G}-{q.D}-{q.P}-{q.L}）" : "")
         + (HasModel ? "、有战斗模型" : "、无战斗模型");
}

/// <summary>
/// 换装 / 看板娘域的配置表。台账 `docs/05` §37。
///
/// <para><b>0. 两张表，表头都是 3 行</b></para>
/// <list type="table">
///   <item><term><c>Girl/SpineSuit.txt</c></term>
///     <description>Spine 皮肤主表。`GirlConfig.lua:29` <c>SUIT_PATH</c> + `:279`
///       <c>LoadTabFile(SUIT_PATH, 3)</c>。★ 新版 48 条（旧快照只有 24 条，见 §1）。</description></item>
///   <item><term><c>Girl/SpineNum.txt</c></term>
///     <description>「皮肤收集度」的分母清单。`GirlConfig.lua:53` + `:680`。
///       <c>GirlLogic:GetSpineSuitNum</c>（`GirlLogic.lua:633`）遍历它数已拥有数量。
///       ★ 该表的 <c>SuitId</c> 列存的其实是 <b>ModelId</b>（与 <c>SpineSuit</c> 的
///       <c>ModelId</c> 逐行对齐，实测 37/48 条重合）。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 本轮修掉的长期问题：配置表快照是从旧 <c>setting.u</c> 解的</b></para>
/// `extracted/setting/` 最初解自 **APK 内的 `setting.u`（3 950 873 字节）**，
/// 而设备与 CDN 备份跑的是 **新版（4 226 190 字节）**。
/// 实测差异极大：<c>SpineSuit</c> 24 → **48** 条、<c>PurchaseIBShop</c> 284 → **325** 件、
/// <c>ItemBoxInfo</c> 2365 → **2588** 行、<c>MercenaryTower</c> 102 → **891** 行
/// （后者正好印证 `AGENTS.md` §31.1 记的「真机自报 891 个 WeekId」）。
/// ⇒ `AGENTS.md` §5 那条「本地表可能比设备少行」的**根因就是这个**，
/// 现已用 `tools/extract_setting_tables.py` 从新版重解，不再需要逐表用真机复核。
///
/// <para><b>2. ★★ <c>ModelId</c> 必须过 <c>FixModelId</c> 才能当任务值偏移</b></para>
/// 新表里出现了 <c>3017</c>/<c>5001</c>/<c>5018</c>/<c>7001</c>/<c>7017</c> 这些 ≥3000 的值，
/// 全部落在 <see cref="GmUnlock.FixModelId"/> 的取模分支
/// （<c>m%100 + m/1000*30 + 1500</c>，例 7001 → 1711、3017 → 1607）。
/// ★ 已用探针验证 48 条**映射后无冲突**（同一少女不会有两套皮肤撞到同一个偏移）。
/// 漏这一层的后果是「衣服明明发了却显示未获得」且**无任何报错**。
/// </summary>
public sealed class GirlDressTable
{
    /// <summary><c>SpineSuit.txt</c> 的文件名。</summary>
    public const string SpineSuitFileName = "SpineSuit.txt";

    /// <summary><c>SpineNum.txt</c> 的文件名。</summary>
    public const string SpineNumFileName = "SpineNum.txt";

    /// <summary>皮肤道具的 genre（现网 <c>SpineSuit.G</c> 恒 19，ClassName <c>SuitItem</c>）。</summary>
    public const uint SuitItemGenre = 19;

    // (girlId, modelId) → 行
    private readonly Dictionary<(uint, int), SpineSuitRow> _byGirlModel = new();
    // 道具四元组 → 行（皮肤道具入包时用来解锁服饰）
    private readonly Dictionary<(uint, uint, uint, uint), SpineSuitRow> _byItem = new();
    // SpineNum.txt 的 (girlId, modelId) 清单
    private readonly List<(uint GirlId, int ModelId)> _collection = new();

    /// <summary>空表（配置缺失时的降级实例）。</summary>
    public static GirlDressTable Empty { get; } = new();

    /// <summary>皮肤条数。</summary>
    public int Count => _byGirlModel.Count;

    /// <summary>全部皮肤行。</summary>
    public IEnumerable<SpineSuitRow> All => _byGirlModel.Values;

    /// <summary><c>SpineNum.txt</c> 的收集度清单（<c>GetSpineSuitNum</c> 的分母）。</summary>
    public IReadOnlyList<(uint GirlId, int ModelId)> Collection => _collection;

    /// <summary>
    /// 载入两张表。缺表不抛、只降级（与其它配置表一致）。
    /// </summary>
    /// <param name="settingDir"><c>extracted/setting</c> 目录。</param>
    public static GirlDressTable Load(string settingDir)
    {
        var t = new GirlDressTable();
        t.LoadSpineSuit(Path.Combine(settingDir, SpineSuitFileName));
        t.LoadSpineNum(Path.Combine(settingDir, SpineNumFileName));

        int withModel = t._byGirlModel.Values.Count(r => r.HasModel);
        Log.Info($"[setting] 少女换装配置：Spine 皮肤 {t.Count} 条"
               + $"（{withModel} 条带战斗模型、{t._byItem.Count} 条可由道具解锁）、"
               + $"收集度清单 {t._collection.Count} 条");
        return t;
    }

    private void LoadSpineSuit(string path)
    {
        var table = SettingTable.LoadAuto(path, "ModelId");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint girlId = (uint)table.Int(row, "GirlId");
            int modelId = table.Int(row, "ModelId");
            if (girlId == 0 || modelId == 0)
                continue;

            (uint, uint, uint, uint)? gdpl = null;
            uint g = (uint)table.Int(row, "G");
            uint d = (uint)table.Int(row, "D");
            uint p = (uint)table.Int(row, "P");
            uint l = (uint)table.Int(row, "L");
            if (g != 0 && d != 0 && p != 0 && l != 0)
                gdpl = (g, d, p, l);

            var entry = new SpineSuitRow(
                GirlId: girlId,
                SuitId: table.Int(row, "SuitId"),
                ModelId: modelId,
                // ★ IsModel 列在无模型的行上是**空字符串**（不是 0），Int 会返回 fallback 0
                HasModel: table.Int(row, "IsModel") > 0,
                Gdpl: gdpl,
                Level: table.Int(row, "Level"),
                ShopId: table.Int(row, "ShopId"),
                ClothName: table.Str(row, "ClothName"));

            _byGirlModel[(girlId, modelId)] = entry;
            if (gdpl is { } q)
                _byItem[q] = entry;
        }
    }

    private void LoadSpineNum(string path)
    {
        var table = SettingTable.LoadAuto(path, "SuitId");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            uint girlId = (uint)table.Int(row, "GirlId");
            // ★ 这张表的列名叫 SuitId，存的却是 ModelId（与 SpineSuit.ModelId 对齐）
            int modelId = table.Int(row, "SuitId");
            if (girlId != 0 && modelId != 0)
                _collection.Add((girlId, modelId));
        }
    }

    /// <summary>
    /// 是否是 Spine 皮肤（客户端 <c>GirlConfig:CheckIsSpineSuit</c>，`GirlConfig.lua:373`）。
    /// 判据就是「<c>tbSuitConfig[girlId][modelId]</c> 存不存在」。
    /// </summary>
    public bool IsSpineSuit(uint girlId, int modelId) => _byGirlModel.ContainsKey((girlId, modelId));

    /// <summary>
    /// 该皮肤有没有 3D 战斗模型（<c>GirlConfig:CheckSuitHasModel</c>，`GirlConfig.lua:386`）。
    /// ★ 不是皮肤时返回 <c>false</c>（照客户端：查不到就 <c>return false</c>）。
    /// </summary>
    public bool SuitHasModel(uint girlId, int modelId)
        => _byGirlModel.TryGetValue((girlId, modelId), out var r) && r.HasModel;

    /// <summary>取一条皮肤配置（不存在返回 null）。</summary>
    public SpineSuitRow? Get(uint girlId, int modelId)
        => _byGirlModel.TryGetValue((girlId, modelId), out var r) ? r : null;

    /// <summary>
    /// 反查：某个皮肤**道具**对应哪套服饰。
    /// 用于「玩家买到 / 得到 genre 19 道具 ⇒ 把 group 4 的 <c>SuitValue</c> 置 1」。
    /// </summary>
    public SpineSuitRow? SuitForItem(uint g, uint d, uint p, uint l)
        => _byItem.TryGetValue((g, d, p, l), out var r) ? r : null;

    /// <summary>某个少女的全部皮肤（<c>GirlConfig:GetSpineSuit</c>）。</summary>
    public IEnumerable<SpineSuitRow> ForGirl(uint girlId)
        => _byGirlModel.Values.Where(r => r.GirlId == girlId).OrderBy(r => r.ModelId);
}
