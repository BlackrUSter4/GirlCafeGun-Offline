using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 道具箱（礼包）的一条内容（<c>Item/ItemBoxInfo.txt</c> 的一行）。
/// </summary>
/// <param name="BoxId"><c>boxId</c> 列 —— 由持箱道具的 <c>ExtParam1</c> 指向。</param>
/// <param name="Gdpl">箱内物品 <c>[G,D,P,L,N]</c>（<c>GDPLN</c> 列，短横分隔）。</param>
/// <param name="ObtainType">
/// 获取方式（<c>obtainType</c> 列，是**字符串**不是数字）：
/// <c>Fix</c> 固定必得 / <c>Random</c> 按权重随机 / <c>Prob</c> 按万分比概率 / <c>Select</c> 多选一。
/// </param>
/// <param name="Weight">权重（<c>Random</c> 用；<c>Prob</c> 时是 rate/1万）。</param>
/// <param name="Broadcast">是否走世界频道公告（服务端目前只记录，不广播）。</param>
public sealed record ItemBoxEntry(
    int BoxId,
    int[] Gdpl,
    string ObtainType,
    int Weight,
    bool Broadcast)
{
    /// <summary>固定必得。</summary>
    public const string TypeFix = "Fix";

    /// <summary>按权重随机抽一个。</summary>
    public const string TypeRandom = "Random";

    /// <summary>按万分比概率各自判定。</summary>
    public const string TypeProb = "Prob";

    /// <summary>多选一（由玩家选，服务端不自动发）。</summary>
    public const string TypeSelect = "Select";

    public override string ToString()
        => $"box{BoxId} {string.Join('-', Gdpl)} {ObtainType}(w={Weight})";
}

/// <summary>
/// 道具箱（礼包）配置表 <c>Item/ItemBoxInfo.txt</c>。台账 `docs/05` §37.3。
///
/// <para><b>0. 为什么服务端需要它（★ 这是「买了皮肤却什么都没得到」的根因）</b></para>
/// 十面妆造（<c>PurchaseIBShop.txt</c> 的 <c>LabelIdx = 101/102</c>）卖的商品，
/// <c>GDPL</c> 指向的是 genre <b>13</b>/<b>14</b> 的**礼包**道具
/// （<c>ClassName = IBGiftAuto</c>），**不是皮肤本体**。
/// 真正的皮肤（genre <b>19</b> <c>SuitItem</c>）在礼包对应的**箱子**里：
/// <code>
/// PurchaseIBShop.GDPL = [13,1,1,6000,1]        -- 礼包道具
///   → ItemList[13-1-1-6000].ExtParam1 = 6000   -- boxId
///     → ItemBoxInfo[boxId=6000] 的 Fix 项：
///         [19-1-1-4-1]   -- ★ 皮肤本体
///         [6-25-2-3-1]   -- 皮肤背景
/// </code>
/// 取证 <c>IBLogic.lua:1195..1205</c>（客户端就是这么反查「这个礼包里有没有外景」的）。
/// ⇒ 服务端不开箱的话，玩家背包里只多一个用不掉的礼包道具，
/// 换装界面里那套皮肤依然是锁着的。
///
/// <para><b>1. ★★ <c>obtainType</c> 是字符串，不是数字</b></para
/// >
/// <c>ItemBox.lua:37..40</c> 直接拿它当 table 的键（<c>box[tb.obtainType]</c>），
/// 值域就是 <c>Fix</c>/<c>Random</c>/<c>Prob</c>/<c>Select</c> 四个字面量。
/// 用 <c>Int()</c> 读会全得 0 ⇒ 整箱内容归到同一类里，**不抛不报错**。
///
/// <para><b>2. 表头 3 行</b></para>
/// <c>ItemBox.lua:20</c> <c>LoadTabFile(self.ItemBoxCfgPATH, 3)</c> —— 权威判据是这个第二实参
/// （AGENTS.md §5：不要数行）。
///
/// <para><b>3. ★ 服务端只自动发 <c>Fix</c>，其余记日志</b></para>
/// <list type="bullet">
///   <item><c>Fix</c> —— 固定必得，直接发；</item>
///   <item><c>Random</c>/<c>Prob</c> —— 需要与客户端**一致的随机源**才不会「显示的和拿到的不一样」，
///     而现网妆造商店的箱子**全是 Fix**（已核对 101/102 两个页签的全部商品）
///     ⇒ 先不做，遇到就记警告（比静默发错东西安全）；</item>
///   <item><c>Select</c> —— 由玩家选，本来就不该服务端自动发。</item>
/// </list>
/// </summary>
public sealed class ItemBoxTable
{
    /// <summary>表文件名。</summary>
    public const string FileName = "ItemBoxInfo.txt";

    private readonly Dictionary<int, List<ItemBoxEntry>> _byBox = new();

    /// <summary>空表（配置缺失时的降级实例）。</summary>
    public static ItemBoxTable Empty { get; } = new();

    /// <summary>箱子个数。</summary>
    public int BoxCount => _byBox.Count;

    /// <summary>全部条目数。</summary>
    public int EntryCount => _byBox.Values.Sum(v => v.Count);

    /// <summary>载入。缺表不抛，只降级。</summary>
    public static ItemBoxTable Load(string settingDir)
    {
        var t = new ItemBoxTable();
        string path = Path.Combine(settingDir, FileName);
        var table = SettingTable.LoadAuto(path, "boxId");
        if (table is null)
        {
            Log.Warn($"[setting] 找不到 {FileName}，礼包开箱不可用（买皮肤会只拿到礼包道具）");
            return t;
        }

        foreach (var row in table.Rows)
        {
            int boxId = table.Int(row, "boxId");
            if (boxId == 0)
                continue;
            var gdpl = ParseGdpln(table.Str(row, "GDPLN"));
            if (gdpl is null)
                continue;

            string type = table.Str(row, "obtainType").Trim();
            var entry = new ItemBoxEntry(
                BoxId: boxId,
                Gdpl: gdpl,
                ObtainType: type,
                Weight: table.Int(row, "Weight"),
                Broadcast: table.Int(row, "broadcast") > 0);

            if (!t._byBox.TryGetValue(boxId, out var list))
                t._byBox[boxId] = list = new List<ItemBoxEntry>();
            list.Add(entry);
        }

        int fixOnly = t._byBox.Count(kv => kv.Value.All(e => e.ObtainType == ItemBoxEntry.TypeFix));
        Log.Info($"[setting] 道具箱：{t.BoxCount} 个箱子 / {t.EntryCount} 条内容"
               + $"（其中 {fixOnly} 个箱子是纯 Fix ⇒ 可安全自动发放）");
        return t;
    }

    /// <summary>取一个箱子的全部内容（不存在返回空）。</summary>
    public IReadOnlyList<ItemBoxEntry> Get(int boxId)
        => _byBox.TryGetValue(boxId, out var list) ? list : Array.Empty<ItemBoxEntry>();

    /// <summary>取一个箱子里的 <c>Fix</c>（固定必得）项。</summary>
    public IEnumerable<ItemBoxEntry> FixedOf(int boxId)
        => Get(boxId).Where(e => e.ObtainType == ItemBoxEntry.TypeFix);

    /// <summary>取一个箱子里的 <c>Select</c>（玩家多选一）项，顺序即客户端的 1-based 索引顺序。</summary>
    public IReadOnlyList<ItemBoxEntry> SelectOf(int boxId)
        => Get(boxId).Where(e => e.ObtainType == ItemBoxEntry.TypeSelect).ToList();

    /// <summary>
    /// ★★★ 开一次箱子，算出实际获得的物品清单（<c>Fix</c> + <c>Random</c> + <c>Prob</c> + <c>Select</c>）。
    ///
    /// <para><b>为什么服务端可以自己抽（推翻本类旧注释里的顾虑）</b></para>
    /// 旧注释担心「<c>Random</c>/<c>Prob</c> 需要与客户端一致的随机源，否则显示的和拿到的不一样」。
    /// 实测**不需要**：客户端 <c>ItemBox.lua</c> 全文 **0 处** <c>random</c> 调用
    /// （只有第 36 行一句注释里出现过 "随机表" 字样），
    /// <c>OnOpenBox</c>/<c>OnOpenBoxMO</c>（<c>:53</c>/<c>:121</c>）都是
    /// **纯渲染服务端下发的 <c>tbParam</c>**（GDPL 数组）。
    /// 其它 <c>GetBoxCfg</c> 的调用点（<c>UI_ItemBox.lua:94</c>、<c>UI_GachaDetail.lua:77</c>、
    /// <c>UI_GiftBoxTips.lua:273</c>、<c>IBLogic.lua:1198</c>…）都是**展示"可能开出什么"的预览**，
    /// 不参与结果判定。⇒ 抽取完全是服务端职责，与原服一致。
    ///
    /// <para><b>四种 <c>obtainType</c> 的语义</b></para>
    /// <list type="table">
    ///   <item><term><c>Fix</c></term><description>固定必得，全给（1643 条，占比最大）。</description></item>
    ///   <item><term><c>Random</c></term><description>按 <c>Weight</c> **权重抽 1 个**（186 条）。</description></item>
    ///   <item><term><c>Prob</c></term><description>按 <c>Weight</c> 作**万分比**独立判定，每项各抽一次（4 条）。</description></item>
    ///   <item><term><c>Select</c></term><description>玩家指定 1-based 索引（536 条）；<paramref name="select"/> 为 0 时不给。</description></item>
    /// </list>
    /// ★ <c>obtainType</c> **空值有 219 条** —— 客户端 <c>ItemBox.lua:39</c>
    /// <c>if tb.obtainType then box[tb.obtainType] = …</c> 会把它们**整条丢弃**
    /// （nil 不能当 table 键）⇒ 服务端同样必须忽略，否则会多发东西。
    /// </summary>
    /// <param name="boxId">箱子 id。</param>
    /// <param name="times">开箱次数（<c>UI_ItemBox.lua:359</c> 的 <c>num</c>，一次最多 10）。</param>
    /// <param name="select">
    /// <c>Select</c> 组的 1-based 索引（<c>UI_ItemBox.lua:331</c> 的 <c>select</c>）。
    /// 0 = 该箱没有 Select 组 / 玩家未选。
    /// </param>
    /// <param name="rng">随机源（测试可注入确定性实例）。</param>
    /// <returns>五元组 <c>(G,D,P,L,N)</c> 列表，已按 <paramref name="times"/> 累计。</returns>
    public List<int[]> Roll(int boxId, int times, int select, Random rng)
    {
        var result = new List<int[]>();
        var entries = Get(boxId);
        if (entries.Count == 0)
            return result;

        int n = Math.Max(1, times);

        // ① Fix：固定必得，数量 × 开箱次数
        foreach (var e in entries.Where(e => e.ObtainType == ItemBoxEntry.TypeFix))
            result.Add(Scale(e, e.Gdpl[4] * n));

        // ② Random：按权重抽 1 个，**每次开箱各抽一次**
        var randoms = entries.Where(e => e.ObtainType == ItemBoxEntry.TypeRandom).ToList();
        if (randoms.Count > 0)
        {
            int total = randoms.Sum(e => Math.Max(0, e.Weight));
            for (int i = 0; i < n; i++)
            {
                var picked = PickByWeight(randoms, total, rng);
                if (picked is not null)
                    Accumulate(result, picked, picked.Gdpl[4]);
            }
        }

        // ③ Prob：Weight 是万分比，每项独立判定、每次开箱各判一轮
        foreach (var e in entries.Where(e => e.ObtainType == ItemBoxEntry.TypeProb))
        {
            for (int i = 0; i < n; i++)
            {
                if (rng.Next(10000) < Math.Max(0, e.Weight))
                    Accumulate(result, e, e.Gdpl[4]);
            }
        }

        // ④ Select：玩家指定的那一项（1-based）
        if (select > 0)
        {
            var selects = SelectOf(boxId);
            if (select <= selects.Count)
            {
                var e = selects[select - 1];
                Accumulate(result, e, e.Gdpl[4] * n);
            }
            else
            {
                Log.Warn($"[itembox] box {boxId} 的 select={select} 越界"
                       + $"（Select 组只有 {selects.Count} 项）⇒ 忽略");
            }
        }

        return result;
    }

    /// <summary>按权重抽一项；总权重为 0 时退化成均匀抽取（避免整组永远抽不出来）。</summary>
    private static ItemBoxEntry? PickByWeight(List<ItemBoxEntry> pool, int totalWeight, Random rng)
    {
        if (pool.Count == 0)
            return null;
        if (totalWeight <= 0)
            return pool[rng.Next(pool.Count)];

        int roll = rng.Next(totalWeight);
        foreach (var e in pool)
        {
            roll -= Math.Max(0, e.Weight);
            if (roll < 0)
                return e;
        }
        return pool[^1];
    }

    private static int[] Scale(ItemBoxEntry e, int count)
        => new[] { e.Gdpl[0], e.Gdpl[1], e.Gdpl[2], e.Gdpl[3], count };

    /// <summary>把同一个四元组合并计数（避免回包里出现重复项，客户端会画成两格）。</summary>
    private static void Accumulate(List<int[]> into, ItemBoxEntry e, int count)
    {
        foreach (var row in into)
        {
            if (row[0] == e.Gdpl[0] && row[1] == e.Gdpl[1]
                && row[2] == e.Gdpl[2] && row[3] == e.Gdpl[3])
            {
                row[4] += count;
                return;
            }
        }
        into.Add(Scale(e, count));
    }

    /// <summary>箱子是否存在。</summary>
    public bool Has(int boxId) => _byBox.ContainsKey(boxId);

    /// <summary>
    /// 解析 <c>GDPLN</c> 列（<c>[19-1-1-4-1]</c> 这种**短横**分隔的五元组）。
    ///
    /// ★★ 与 <c>Friendliness.BanGirl</c> 的**逗号**风格不同 —— 本项目
    /// AGENTS.md §5 记过这条坑：同一个 <c>INTARR</c> 标注下分隔符可以不同，
    /// 混用**不抛不报错、只静默得到空数组**。
    /// ★ 少于 4 段视为非法；第 5 段（数量）缺省 1。
    /// </summary>
    internal static int[]? ParseGdpln(string raw)
    {
        string s = raw.Trim().Trim('[', ']');
        if (s.Length == 0)
            return null;
        var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4)
            return null;

        var v = new int[5];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i].Trim(), out v[i]))
                return null;
        }
        v[4] = parts.Length >= 5 && int.TryParse(parts[4].Trim(), out int n) && n > 0 ? n : 1;
        return v;
    }
}
