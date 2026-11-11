using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一件内购商品（<c>Purchase/PurchaseIBShop.txt</c> 的一行）——
/// 「特殊补给 / 地球币矿池 / 十面妆造」三个入口卖的都是它。
///
/// 表头 3 行（<c>IBLogic.lua:78</c> <c>LoadTabFile('Purchase/PurchaseIBShop.txt', 3)</c>）。
/// </summary>
public sealed class IbShopGoods
{
    /// <summary><c>Id</c> 列。上行 <c>DoRecharge{Type=1, Id=&lt;这个&gt;}</c>。</summary>
    public int Id { get; init; }

    /// <summary><c>GDPL</c> 列，形如 <c>[13,2,1,6026,1]</c>（★ **逗号**分隔，不是短横）。</summary>
    public int[]? Gdpl { get; init; }

    /// <summary>
    /// <c>Currency</c> 列（表头原文「货币类型,-1货币-2钻石-3金币 &gt;0对应代币表」）。
    ///
    /// ★★★ <b><c>-1</c> 是「真钱」的哨兵</b>：客户端只在 <c>Currency == -1</c> 时走
    /// <c>Game.Login.XGCharge</c> 真实支付 SDK（<c>IBLogic.lua:813/851</c>）；
    /// 其余一律走 <c>else</c> 分支，服务端回 <c>tbItem</c> 就直接发货
    /// ⇒ **私服只能做 <c>Currency != -1</c> 的商品**。
    /// 真机自报分布：<c>-1</c> 299 件（真钱，做不了）/ <c>62</c> 20 件 / <c>63</c> 19 件 / <c>-2</c> 3 件。
    /// </summary>
    public int Currency { get; init; }

    /// <summary>原价（<c>Cost</c> 列）。</summary>
    public int Cost { get; init; }

    /// <summary>台服价（<c>CostTC</c> 列，本服不用）。</summary>
    public int CostTC { get; init; }

    /// <summary>购买等级门槛（<c>Level</c> 列，0 = 无门槛）。</summary>
    public int Level { get; init; }

    /// <summary>
    /// 限购类型（<c>LimitType</c> 列，表头原文「0或不填为无限制，1日限制，2周限制,3月限制,4总计限购」）。
    /// </summary>
    public int LimitType { get; init; }

    /// <summary>限购次数（<c>LimitTimes</c> 列）。</summary>
    public int LimitTimes { get; init; }

    /// <summary>
    /// 标签页（<c>LabelIdx</c> 列，表头原文「默认0周期礼包1外景礼包2 妆扮商店101102」）。
    /// <c>101</c> = 十面妆造（妆扮商店）、<c>102</c> = 妆扮礼包。
    /// </summary>
    public int LabelIdx { get; init; }

    /// <summary>上架/下架时间（<c>StartTime</c>/<c>EndTime</c>，unix 秒；0 = 未配）。</summary>
    public long StartTime { get; init; }
    public long EndTime { get; init; }

    /// <summary>是否在商店隐藏（<c>IsHide</c> 列）。</summary>
    public bool IsHide { get; init; }

    /// <summary>折扣标签（<c>OffFlag</c>，最多 2 个）。</summary>
    public int[] OffFlags { get; init; } = Array.Empty<int>();

    /// <summary>折扣后价格（<c>OffCost</c>，与 <see cref="OffFlags"/> 一一对应）。</summary>
    public int[] OffCosts { get; init; } = Array.Empty<int>();

    /// <summary>折扣时段（<c>OffStartTime</c>/<c>OffEndTime</c>）。</summary>
    public long OffStartTime { get; init; }
    public long OffEndTime { get; init; }

    /// <summary>是否需要真实支付 SDK（私服做不了）。</summary>
    public bool IsRealMoney => Currency == CurrencyRealMoney;

    /// <summary>真钱哨兵值。</summary>
    public const int CurrencyRealMoney = -1;

    /// <summary>钻石（表头「-2钻石」）。</summary>
    public const int CurrencyDiamond = -2;

    /// <summary>金币（表头「-3金币」）。</summary>
    public const int CurrencyGold = -3;

    /// <summary>
    /// 该商品此刻是否在上架期（<c>IBLogic:CheckTime</c>，<c>IBLogic.lua:179..196</c>）。
    ///
    /// ★ 口径逐条照抄：<b>两端都闭</b>，且 <b>0 表示「不限」</b>：
    /// <code>
    /// if nStartTime and nStartTime > 0 then if GetTime() &lt; nStartTime then bGet = false end end
    /// if nEndTime   and nEndTime   > 0 then if GetTime() &gt; nEndTime   then bGet = false end end
    /// </code>
    /// ⇒ <c>EndTime = 0</c> ⇒ **永久在售**（现网十面妆造的在售品全是这样）。
    /// </summary>
    public bool IsOnSaleAt(long now)
    {
        if (StartTime > 0 && now < StartTime)
            return false;
        if (EndTime > 0 && now > EndTime)
            return false;
        return true;
    }

    /// <summary>
    /// 此刻的实付价格（<c>IBLogic:GetIBShopOffInfo</c>，<c>IBLogic.lua:198..219</c>）。
    ///
    /// ★★ 折扣缺省值必须是 <b><see cref="Cost"/></b> 而不是 0 ——
    /// `docs/05` §5.1.1 记过常规商店的同款坑：**漏发 discount 的后果是 0 折免费**。
    /// 这里同理：<c>OffCost</c> 只有在「配了且 &gt; 0 且在折扣时段内」才生效。
    /// </summary>
    public int PriceAt(long now)
    {
        // 第 1 组：用 OffStartTime/OffEndTime 判时段
        if (OffCosts.Length >= 1 && OffCosts[0] > 0 && now >= OffStartTime
            && (OffEndTime == 0 || now <= OffEndTime))
        {
            return OffCosts[0];
        }
        // 第 2 组：用商品自己的上架时段判
        if (OffCosts.Length == 2 && OffCosts[1] > 0 && now >= StartTime
            && (EndTime == 0 || now <= EndTime))
        {
            return OffCosts[1];
        }
        return Cost;
    }

    public override string ToString()
        => $"#{Id} label={LabelIdx} cur={Currency} cost={Cost} " +
           $"gdpl={(Gdpl is null ? "?" : string.Join('-', Gdpl))}";
}

/// <summary>
/// 内购商店配置（<c>Purchase/PurchaseIBShop.txt</c>）：
/// 「特殊补给」「地球币矿池」「十面妆造」三个入口的共同数据源。
///
/// <para><b>三个入口其实是「两个界面、一份数据、一条命令」</b></para>
/// <list type="bullet">
///   <item><b>特殊补给</b> <c>UI_ShopEntry.lua:12</c> → <c>IBLogic:ClickGemShop(1)</c>
///     → <c>UI.CloseAndOpen('UI_Charge', 1, nil)</c>（<c>IBLogic.lua:1028</c>）</item>
///   <item><b>地球币矿池</b> <c>UI_ShopEntry.lua:36</c> → <c>IBLogic:ClickGemShop(2)</c>
///     → 同一个 <c>UI_Charge</c>，**只是首参变 2**
///     ⇒ <c>nType</c> 只是页签（<c>UI_Charge.lua:8/9</c>：<c>UI_SHOP1=1</c> 礼包 /
///     <c>UI_SHOP2=2</c> 充值道具）—— **不是两套数据，别写两份 handler**。</item>
///   <item><b>十面妆造</b> <c>UI_ShopEntry.lua:49</c> →
///     <c>UI.CloseAndOpen('UI_ShopNew', nil, 101)</c>，读
///     <c>IBLogic:GetAllIBShop(101)</c> ⇒ 就是本表 <c>LabelIdx == 101</c> 的行。</item>
/// </list>
///
/// <para><b>★★★ 服务端只需要实现「购买」这一条命令</b></para>
/// 商品列表、排序、限购置灰、红点**全部在客户端本地算**
/// （<c>IBLogic:GetAllIBShop</c>（<c>:224</c>）只读 <c>self.tbGoods</c> 与
/// <c>me:GetTaskValue</c>，不发任何包）⇒ 界面本来就有内容，
/// 缺的只有 <c>LuaCall.DoRecharge (10000)</c> 的回包。
///
/// <para><b>购买记录 = 任务值（<c>IBLogic:GetIBTask</c>，<c>IBLogic.lua:356</c>）</b></para>
/// <code>
/// TYPE_IBSHOP(1) → me:GetTaskValue(42, nId + 10000)     -- 礼包商店（本表）
/// TYPE_IBITEM(2) → me:GetTaskValue(42, nId)             -- 充值道具（PurchaseIB.txt）
/// </code>
/// 分段规则来自 <c>IBLogic.lua:5..7</c> 的注释「1-10000 ibitem 购买纪录 /
/// 10001-65535 ibshop 购买纪录」。⇒ 服务端改完必须推 **pf 1026**。
///
/// <para><b>现网可做范围（★ 真机自报）</b></para>
/// <c>tools/gm.sh</c> 让客户端自己数：<c>IBLogic.tbGoods</c> **341 条**，
/// 而本地快照只有 **286 条**（1025..1035/2011/2012 等在本地压根没有）——
/// 又一次 `AGENTS.md` §5「本地表比真机少行」。
/// ⇒ 本类**不依赖行数做任何判定**，且对「表里没有该 id」优雅降级成
/// <c>Error = 20074</c>（客户端 <c>IBLogic.lua:816</c> 自己也是这么判的）。
///
/// 按 <c>LabelIdx</c> 的真机分布：0 → 284 件、1 → 9、2 → 9、**101 → 35**、102 → 4。
/// 其中十面妆造（101）**在售 8 件、全部 <c>Currency = 62</c>（造型U型夹）**
/// ⇒ 零支付阻塞，是本轮首选实现目标。
/// </summary>
public sealed class IbShopTable
{
    /// <summary>文件名（客户端口径 <c>Purchase/PurchaseIBShop.txt</c>，本目录扁平化）。</summary>
    public const string FileName = "PurchaseIBShop.txt";

    /// <summary>妆扮商店（十面妆造）的 <c>LabelIdx</c>（<c>IBLogic.LABELDRESS</c>，<c>IBLogic.lua:43</c>）。</summary>
    public const int LabelDress = 101;

    /// <summary>妆扮礼包（<c>IBLogic.LABELGIFT</c>，<c>IBLogic.lua:44</c>）。</summary>
    public const int LabelGift = 102;

    /// <summary>「区分妆扮商店」的分界（<c>IBLogic.LABELINDEX</c>，<c>IBLogic.lua:42</c>）。</summary>
    public const int LabelIndex = 100;

    private readonly Dictionary<int, IbShopGoods> _goods = new();

    private IbShopTable() { }

    /// <summary>永久空表（缺表降级：购买一律回 20074「商品不存在」）。</summary>
    public static IbShopTable Empty { get; } = new();

    public int Count => _goods.Count;

    public IEnumerable<IbShopGoods> All => _goods.Values.OrderBy(g => g.Id);

    /// <summary>按 id 取商品；不存在返回 null（调用方回 20074）。</summary>
    public IbShopGoods? Get(int id) => _goods.TryGetValue(id, out var g) ? g : null;

    /// <summary>某个标签页此刻在售的商品（口径同 <c>IBLogic:GetAllIBShop</c> 的时间+隐藏判定）。</summary>
    public IEnumerable<IbShopGoods> OnSaleAt(int label, long now)
        => All.Where(g => g.LabelIdx == label && !g.IsHide && g.IsOnSaleAt(now));

    public static IbShopTable Load(string settingDir)
    {
        var table = new IbShopTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失：内购商店域降级（购买一律回 20074）");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int id = raw.Int(row, "Id", -1);
            if (id <= 0)
                continue;

            var goods = new IbShopGoods
            {
                Id = id,
                // ★ GDPL 列在本表是**逗号**分隔（[13,2,1,6026,1]），
                //   与 gacha 那边的短横（[2-1-2-1]）不同 —— AGENTS.md §5 记过
                //   「同一张表里两个都标 INTARR 的列分隔符可以不同」，跨表更要逐列确认。
                Gdpl = ParseCommaItem(raw.Str(row, "GDPL")),
                Currency = raw.Int(row, "Currency"),
                Cost = raw.Int(row, "Cost"),
                CostTC = raw.Int(row, "CostTC"),
                Level = raw.Int(row, "Level"),
                LimitType = raw.Int(row, "LimitType"),
                // 客户端缺省是 9999（IBLogic.lua:101 `or 9999`）
                LimitTimes = raw.Int(row, "LimitTimes", 9999),
                LabelIdx = raw.Int(row, "LabelIdx"),
                IsHide = raw.Int(row, "IsHide") == 1,
                StartTime = ParseBracketTime(raw.Str(row, "StartTime")),
                EndTime = ParseBracketTime(raw.Str(row, "EndTime")),
                OffFlags = ParseCommaIntArray(raw.Str(row, "OffFlag")),
                OffCosts = ParseCommaIntArray(raw.Str(row, "OffCost")),
                OffStartTime = ParseBracketTime(raw.Str(row, "OffStartTime")),
                OffEndTime = ParseBracketTime(raw.Str(row, "OffEndTime")),
            };

            // 客户端的入表条件（IBLogic.lua:126）：OffFlag 与 OffCost 长度一致且 <= 2
            if (goods.OffFlags.Length != goods.OffCosts.Length || goods.OffCosts.Length > 2)
            {
                Log.Warn($"[setting] {FileName} 商品 {id} 的 OffFlag/OffCost 长度不匹配" +
                         $"（{goods.OffFlags.Length} vs {goods.OffCosts.Length}），跳过（与客户端同口径）");
                continue;
            }

            _ = table._goods.TryAdd(id, goods);
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int dress = table.OnSaleAt(LabelDress, now).Count();
        int payable = table.All.Count(g => !g.IsRealMoney);
        Log.Info($"[setting] {FileName} 载入 {table._goods.Count} 件内购商品" +
                 $"（非真钱 {payable} 件；十面妆造(label {LabelDress}) 在售 {dress} 件）");
        return table;
    }

    /// <summary>
    /// 解析 <c>[13,2,1,6026,1]</c>（★ **逗号**分隔）。空串返回 null。
    /// 客户端是 <c>Parse2Item(tbRow["GDPL"], ',')</c>（<c>IBLogic.lua:89</c>）——
    /// 第 2 个实参就是分隔符，这是「逗号」的权威取证。
    /// </summary>
    internal static int[]? ParseCommaItem(string value)
    {
        var nums = ParseCommaIntArray(value);
        return nums.Length >= 4 ? nums : null;
    }

    /// <summary>解析 <c>[1,2]</c> 这类逗号整数数组（<c>Str2IntArr</c>）。</summary>
    internal static int[] ParseCommaIntArray(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<int>();
        var body = value.Trim().Trim('[', ']');
        if (body.Length == 0)
            return Array.Empty<int>();

        var result = new List<int>();
        foreach (var tok in body.Split(',', StringSplitOptions.RemoveEmptyEntries
                                           | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(tok, out int n))
                result.Add(n);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 解析 <c>[201912130800]</c> 这类**带方括号**的时间。
    /// 客户端是 <c>tonumber(string.sub(tbRow["StartTime"] or "", 2, -2))</c>
    /// （<c>IBLogic.lua:98</c>：掐掉首尾各一个字符）再交给 <c>GetDate2Time</c>。
    /// 空/非法返回 0（= 不限）。
    /// </summary>
    internal static long ParseBracketTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return 0;
        var body = value.Trim().Trim('[', ']').Trim();
        return body.Length == 0 ? 0 : GachaTable.ParseDate(body);
    }
}
