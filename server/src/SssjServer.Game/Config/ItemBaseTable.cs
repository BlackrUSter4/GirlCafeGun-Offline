using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一条物品模板（客户端 <c>Game.ItemBaseInfo</c>，dump.cs:541600 的字段逐一对应）。
///
/// 主键是 <c>(G,D,P,L)</c> 四元组 —— 全表 <b>2379</b> 行 → 2379 个唯一键，零重复
/// （★ 第十轮从新版 <c>setting.u</c> 重解，旧快照是 2120，见 `docs/05` §37.1）；
/// <c>ClassName</c> **不唯一**（例：<c>ItemGoldAuto</c> 同时出现在主货币与红包道具上），
/// 所以任何查找都必须走四元组，不能按 ClassName。
/// </summary>
public sealed class ItemTemplate
{
    public uint G { get; init; }
    public uint D { get; init; }
    public uint P { get; init; }
    public uint L { get; init; }

    /// <summary>稀有度颜色（1..4），客户端 <c>GetItemOrder(itemInfo.ColorFlag)</c> 的来源。</summary>
    public int RarityColor { get; init; }

    /// <summary>入包即自动使用。货币类道具（<c>*Auto</c> 一族）全部为 true。</summary>
    public bool AutoUse { get; init; }

    /// <summary>
    /// 原服务端的道具行为类名。客户端只硬编码认识三个泛型代币类
    /// （<see cref="ItemBaseTable.CoinAutoClassNames"/>），其余 <c>*Auto</c> 类名
    /// 在客户端里**不是** C# 类型（dump.cs 里搜不到 <c>class ItemGoldAuto</c>），
    /// 只在原服务端存在，所以它们对应哪种货币要另找取证（见
    /// <see cref="ItemBaseTable.KnownMoneyItems"/>）。
    /// </summary>
    public string ClassName { get; init; } = "";

    /// <summary>名称/描述的多语言 key（客户端 <c>Localization.Get("item." + ...)</c> 一族）。</summary>
    public string DescriptionIndex { get; init; } = "";

    public int SellingPrice { get; init; }

    public string IconId { get; init; } = "";

    public string ExtParam1 { get; init; } = "";

    /// <summary>
    /// 扩展参数 2。对 <see cref="ItemBaseTable.CoinAutoClassNames"/> 里的三个类名，
    /// 它就是货币 id（取证 <c>extracted/lua/ItemLogic.lua:69..88</c>：
    /// <c>Genre==15</c> 且 ClassName 为那三个之一时 <c>tbItemCoinItem[ExtParam2] = 该道具</c>）。
    /// </summary>
    public string ExtParam2 { get; init; } = "";

    public string ExtParam3 { get; init; } = "";

    public int ShowType { get; init; }

    /// <summary>
    /// 禁用掉落途径（<c>DisableDropWay</c> 列，BOOL）。原表中文列名「禁用掉落途径」，
    /// 与 <c>UI_UnlockItemDropWay</c>（SystemUnlock.txt）配合决定物品详情页是否显示「获取途径」。
    /// 服务端不用它做逻辑，收录以便将来查询。
    /// </summary>
    public bool DisableDropWay { get; init; }

    /// <summary>
    /// 限时秒数（<c>LimitTime</c> 列，原表中文列名「限时(秒)」）。
    /// 客户端 <c>BaseItem.get_IsLimit</c>（RVA 0x174E338）：
    /// <c>LimitTime &gt;= 1 &amp;&amp; !CloseLimit &amp;&amp; (CreateTime + LimitTime) &lt; 服务器时间</c>
    /// → 即「创建时间 + LimitTime」就是过期时刻。0 表示永久。
    /// </summary>
    public int LimitTime { get; init; }

    /// <summary>
    /// 过期即删除（<c>ExpiredDelete</c> 列，原表中文列名「过期删除」）。
    /// 客户端 <c>EquipManager.DealLimitedItem</c>（RVA 0x1D9B460）在登录后把
    /// 「<c>IsLimit</c> 且 <c>ExpiredDelete</c>」的实例收集起来，发
    /// <c>ItemFlushFlag{nCmd=4, tbGuid=[…]}</c> 请服务端删除（<c>ItemLogic.lua:149..160</c>）。
    /// </summary>
    public bool ExpiredDelete { get; init; }

    /// <summary>该模板是否限时（<see cref="LimitTime"/> ≥ 1）。</summary>
    public bool IsTimeLimited => LimitTime >= 1;

    /// <summary>
    /// 按客户端口径判断某个实例是否已过期：<c>createTime + LimitTime &lt; now</c>。
    /// 非限时模板恒返回 false。（<c>CloseLimit</c> 是客户端运行期开关，服务端侧无此概念。）
    /// </summary>
    public bool IsExpired(uint createTime, long nowUnix)
        => IsTimeLimited && createTime + (long)LimitTime < nowUnix;

    /// <summary>中文名（zh_CN，来自旁表 ItemName.txt，仅服务端日志用；缺表时为空串）。</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>日志友好名：优先中文名，退化到多语言 key，再退化到四元组。</summary>
    public string LogName
        => DisplayName.Length > 0 ? DisplayName
         : DescriptionIndex.Length > 0 ? DescriptionIndex
         : $"({G},{D},{P},{L})";

    public override string ToString()
        => $"({G},{D},{P},{L}) {LogName} class={ClassName}";
}

/// <summary>
/// 物品模板表：<c>(G,D,P,L)</c> → <see cref="ItemTemplate"/>，并回答「这个四元组是不是货币」。
///
/// ---- 数据来源取证 ----
/// 客户端读表的入口是 <c>extracted/lua/ItemLogic.lua:69</c>
/// <c>Game.FileItemBaseInfoReader.LoadFile()</c>。这个 C# 静态类在
/// <c>dump/dump.cs:559174</c>，其 <c>FilePath == "Item/ItemList.txt"</c>（dump.cs:559180），
/// 但 <c>LoadFile()</c>（RVA <c>0x178BEBC</c>）实际做的是：
/// <code>
///   "Item/ItemList.txt".Replace(".txt", ".asset")
///     → "Assets/_Game/SettingGen/" + 它
///     → Game.Assets.Load&lt;FileItemBaseInfo&gt;(path).list
/// </code>
/// 即运行期读的是**烘进 setting.u 的 ScriptableObject**，包里没有这张 txt，
/// 所以 <c>extracted/setting/</c> 里原本也没有它（<c>grep -ril ItemNormalCoinAuto extracted/setting</c> 空）。
/// 本仓库已把它解出来落成同口径的三行表头文本：
/// <code>
///   work/apk/assets/setting.u --(tools/bundle_decrypt.py)--> work/setting.dec.u
///     --(UnityPy, MonoBehaviour path_id=-1112959065552850940, m_Name="ItemList")-->
///     work/parse_itemlist.py     → work/itemlist_dump.tsv     (2379 行，字节精确闭合)
///     ★ path_id 在新包里是 -2989788756228539293（旧包的常量已失效，要按 m_Name 反查）
///     work/itemlist_to_setting.py→ extracted/setting/ItemList.txt
///     work/itemname_to_setting.py→ extracted/setting/ItemName.txt（zh_CN 名称旁表）
/// </code>
/// 字段顺序按 <c>dump.cs:541600 ItemBaseInfo</c> 的声明顺序（Unity 二进制序列化顺序），
/// 2379 条变长记录读完刚好把 raw 消耗到 0 字节剩余，布局无歧义。
///
/// ---- 缺表降级 ----
/// <see cref="Load"/> 找不到 ItemList.txt 时返回空表并打 WARN：
/// <see cref="Get"/> 一律返回 null，但 <see cref="TryGetMoneyId"/> 仍能靠
/// <see cref="KnownMoneyItems"/> 认出金币/钻石/体力等主货币（chapter 奖励只用到 (15,1,1,1) 与 (15,2,1,1)），
/// 所以关卡结算不会因为缺表而崩或错发。
/// </summary>
public sealed class ItemBaseTable
{
    /// <summary>ItemList.txt 在 setting 目录下的文件名（客户端口径 "Item/ItemList.txt"，此目录是扁平化的）。</summary>
    public const string FileName = "ItemList.txt";

    /// <summary>zh_CN 名称旁表文件名（本仓库自造，客户端无此文件，只影响日志可读性）。</summary>
    public const string NameFileName = "ItemName.txt";

    /// <summary>
    /// 客户端硬编码认识的三个「泛型代币道具」类名 —— 这三类的 <c>ExtParam2</c> 就是货币 id。
    /// 取证 <c>extracted/lua/ItemLogic.lua:74..79</c>：
    /// <code>
    /// if szClass == "ItemBattlePassCoinAuto" or szClass == "ItemMixTowerCoinAuto"
    ///     or szClass == "ItemNormalCoinAuto" then
    ///     ... self.tbItemCoinItem[tbInfo.ExtParam2] = tbInfo
    /// </code>
    /// 另见 <c>extracted/lua/UI_Player.lua:476..477</c>：
    /// <c>ClassName == "ItemNormalCoinAuto" or "ItemMixTowerCoinAuto"</c> 时
    /// 直接 <c>me:GetMoney(tonumber(itemConfig.ExtParam2))</c> 取余额显示。
    /// </summary>
    public static readonly string[] CoinAutoClassNames =
    {
        "ItemNormalCoinAuto",
        "ItemBattlePassCoinAuto",
        "ItemMixTowerCoinAuto",
    };

    /// <summary>
    /// 非泛型的货币道具：<c>(G,D,P,L)</c> → 货币 id。
    ///
    /// 这些道具的 ClassName（<c>ItemGoldAuto</c> / <c>ItemFreeDiamondAuto</c> …）在客户端
    /// **不是 C# 类型**（dump.cs 里搜不到），<c>ExtParam2</c> 也是空的，所以只能靠客户端里
    /// 两处「货币 id ↔ 四元组」的显式映射取证，两处都能与 ItemList.txt 的 ClassName / 图标
    /// 以及 <c>extracted/setting/MoneyDef.txt</c> 的 Icon 列互相印证：
    ///
    /// ① <c>extracted/lua/Lib.lua:1364..1387 GetMoneyGDPL(nId, count)</c>（带中文注释）
    /// <code>
    ///   nId==1 体力     → {15, 4, 1, 1}   ItemList: (15,4,1,1)=ItemEnergyAuto      icon_curr_energy
    ///   nId==2 金币     → {15, 1, 1, 1}   ItemList: (15,1,1,1)=ItemGoldAuto        icon_curr_coin  (MoneyDef id2 同图)
    ///   nId==3 钻石     → {15, 2, 1, 1}   ItemList: (15,2,1,1)=ItemFreeDiamondAuto icon_curr_gem   (MoneyDef id3 同图)
    ///   nId==4 约会点数 → {15, 5, 1, 1}   ItemList: (15,5,1,1)=ItemDatingAuto
    /// </code>
    /// ② <c>extracted/lua/UI_Player.lua:795..841 ClickNew</c>（MoneyLogic 常量 → 四元组），
    ///    其中能与 ExtParam2 对照的条目全部一致（例：TowerSmall=5 → (15,6,1,1) 而该行
    ///    ClassName=ItemNormalCoinAuto、ExtParam2=5）。
    ///
    /// 这里只收录 ExtParam2 之外**还需要**硬映射的那些（其余靠 ExtParam2 自动解析）。
    /// 货币 id 语义见 <see cref="MoneyType"/> 与 <c>extracted/lua/MoneyLogic.lua:10..60</c>。
    ///
    /// 已知冲突（故意不收录）：<c>Lib.lua:1376</c> 说 nId==5（小塔小代币）→ (15,8,1,1)，
    /// 但 ItemList 里 (15,8,1,1) 是 <c>ItemSmallTowerPeopleAuto</c>，而 ExtParam2==5 的是
    /// (15,6,1,1)（<c>ItemNormalCoinAuto</c>，且 UI_Player.lua:796 也说 TowerSmall → (15,6,1,1)）。
    /// 两者不可能同为货币 5，故 (15,8,1,1) 留空 —— 它不会被当成货币，只会进背包。
    /// 主线关卡奖励里的 genre 15 只有 (15,1,1,1) 与 (15,2,1,1)，不受影响。
    /// </summary>
    public static readonly IReadOnlyDictionary<(uint G, uint D, uint P, uint L), int> KnownMoneyItems =
        new Dictionary<(uint, uint, uint, uint), int>
        {
            // Lib.lua:1364..1387 GetMoneyGDPL —— 四条主货币
            [(15, 4, 1, 1)] = MoneyType.Vigour,       // 1  体力         ItemEnergyAuto
            [(15, 1, 1, 1)] = MoneyType.Gold,         // 2  金币(晶币)   ItemGoldAuto
            [(15, 2, 1, 1)] = MoneyType.FreeDiamond,  // 3  钻石(地球币) ItemFreeDiamondAuto
            [(15, 5, 1, 1)] = MoneyType.DatePoint,    // 4  约会点数     ItemDatingAuto

            // ItemList 的 ClassName 明确写着币种、且与 MoneyDef.txt 的 Icon 对得上
            [(15, 3, 1, 1)] = MoneyType.PayDiamond,   // 12 钻石(付费安卓) IBAndroidDiamondAuto icon_curr_gem

            // UI_Player.lua:795..841（MoneyLogic 常量 → 四元组）
            [(15, 10, 1, 1)] = MoneyType.CoffeeCoin,  // 7  咖啡币     ItemCoffeeCoinAuto  icon_curr_cafe
            [(15, 11, 1, 1)] = 10,                    // 10 角色卡代币 ItemCardCoinAuto    icon_curr_cc_coin
            [(15, 14, 1, 1)] = 11,                    // 11 社团贡献   ClubCoinAuto        icon_assin_gongxian
            [(15, 20, 1, 1)] = 16,                    // 16 装备代币   ItemEquipCoinAuto   icon_made_arm06
            [(15, 38, 1, 1)] = 22,                    // 22 道尔顿前体 ItemStuffCoinAuto   icon_curr_da
            [(15, 39, 1, 1)] = 24,                    // 24 演习积分   ItemRankPointAuto   icon_rankpoint
            [(15, 44, 1, 1)] = 27,                    // 27 索引数据块 ItemMercenaryAuto
        };

    private readonly Dictionary<(uint, uint, uint, uint), ItemTemplate> _byKey = new();

    private ItemBaseTable() { }

    /// <summary>表里的模板条数。0 表示处于降级状态（缺表）。</summary>
    public int Count => _byKey.Count;

    /// <summary>全部模板（顺序即文件顺序）。</summary>
    public IEnumerable<ItemTemplate> All => _byKey.Values;

    /// <summary>永久空表（缺表降级用；<see cref="TryGetMoneyId"/> 仍能认出主货币）。</summary>
    public static ItemBaseTable Empty { get; } = new();

    // ---- Shared：给拿不到 ServerOptions 的调用方（如 ChapterHandler）兜底 ----

    private static readonly Lazy<ItemBaseTable> _shared =
        new(() => Load(ResolveSettingDirectory()), isThreadSafe: true);

    /// <summary>
    /// 进程级共享表（懒加载一次）。目录解析见 <see cref="ResolveSettingDirectory"/>。
    /// 能显式注入的地方请用 <see cref="Load"/>；这里只是为了不改 GameModule.cs 的装配签名。
    /// </summary>
    public static ItemBaseTable Shared => _shared.Value;

    /// <summary>
    /// 显式指定 setting 目录（必须在第一次访问 <see cref="Shared"/> 之前设置才有效）。
    /// 留给以后把它接进 GameModule/ServerOptions 时用；单测里请直接用 <see cref="Load"/>。
    /// </summary>
    public static string? DefaultDirectory { get; set; }

    /// <summary>
    /// 解析 setting 目录，依次尝试：
    /// <list type="number">
    ///   <item><see cref="DefaultDirectory"/>（显式注入）</item>
    ///   <item>本进程命令行里的 <c>--setting-dir &lt;路径&gt;</c>（与 <c>SssjServer.App/Program.cs:161</c>
    ///         写入 <c>ServerOptions.SettingDirectory</c> 的那个开关是同一个，
    ///         这样运维换目录时不会出现「关卡表读 A、物品表读 B」的静默不一致）</item>
    ///   <item>环境变量 <c>SSSJ_SETTING_DIR</c></item>
    ///   <item><c>ServerOptions.SettingDirectory</c> 的缺省值（当前是 <c>extracted/setting</c>）相对进程工作目录</item>
    ///   <item>从工作目录与程序所在目录向上各找 8 层里的同名子目录（单测在 bin/Debug/netX 下跑，需要这一步）</item>
    /// </list>
    /// 找不到就返回缺省相对路径，交给 <see cref="Load"/> 打 WARN 后降级。
    /// </summary>
    public static string ResolveSettingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(DefaultDirectory))
            return Path.GetFullPath(DefaultDirectory);

        var fromArgs = FindCommandLineSettingDir();
        if (fromArgs is not null)
            return Path.GetFullPath(fromArgs);

        var fromEnv = Environment.GetEnvironmentVariable("SSSJ_SETTING_DIR");
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        // ServerOptions 的缺省值就是仓库里的相对路径，直接复用它，避免两处写死字符串
        string relative = new ServerOptions().SettingDirectory;

        string direct = Path.GetFullPath(relative);
        if (File.Exists(Path.Combine(direct, FileName)))
            return direct;

        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(Path.Combine(candidate, FileName)))
                    return Path.GetFullPath(candidate);
            }
        }
        return direct;
    }

    /// <summary>从本进程命令行里找 <c>--setting-dir &lt;路径&gt;</c>；没有返回 null。</summary>
    private static string? FindCommandLineSettingDir()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], "--setting-dir", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(args[i + 1]))
                    return args[i + 1];
            }
        }
        catch (NotSupportedException)
        {
            // 某些宿主环境拿不到命令行，忽略即可
        }
        return null;
    }

    /// <summary>
    /// 从 setting 目录加载 ItemList.txt（+ 可选的 ItemName.txt 中文名旁表）。
    /// 缺表返回空表并打 WARN（不抛异常）。
    /// </summary>
    public static ItemBaseTable Load(string settingDir)
    {
        var table = new ItemBaseTable();

        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失（客户端把它烘进了 setting.u，需用 work/parse_itemlist.py 解出）：" +
                     "物品模板查询降级为空表，货币折算只剩 ItemBaseTable.KnownMoneyItems 的硬映射");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            // 四元组任一列缺失/非法就跳过（Genre 恒 >= 1，用 -1 判空）
            int g = raw.Int(row, "Genre", -1);
            int d = raw.Int(row, "Detail", -1);
            int p = raw.Int(row, "Particular", -1);
            int l = raw.Int(row, "Level", -1);
            if (g < 0 || d < 0 || p < 0 || l < 0)
                continue;

            var tpl = new ItemTemplate
            {
                G = (uint)g,
                D = (uint)d,
                P = (uint)p,
                L = (uint)l,
                RarityColor = raw.Int(row, "RarityColor"),
                AutoUse = raw.Int(row, "AutoUse") != 0,
                ClassName = raw.Str(row, "ClassName"),
                DescriptionIndex = raw.Str(row, "DescriptionIndex"),
                SellingPrice = raw.Int(row, "SellingPrice"),
                IconId = raw.Str(row, "IconId"),
                ExtParam1 = raw.Str(row, "ExtParam1"),
                ExtParam2 = raw.Str(row, "ExtParam2"),
                ExtParam3 = raw.Str(row, "ExtParam3"),
                ShowType = raw.Int(row, "ShowType"),
                DisableDropWay = raw.Int(row, "DisableDropWay") != 0,
                LimitTime = raw.Int(row, "LimitTime"),
                ExpiredDelete = raw.Int(row, "ExpiredDelete") != 0,
            };
            table._byKey[(tpl.G, tpl.D, tpl.P, tpl.L)] = tpl;
        }

        // 中文名旁表（可选）：只为日志好读，缺了不影响任何逻辑
        int named = 0;
        var names = SettingTable.Load(Path.Combine(settingDir, NameFileName));
        if (names is not null)
        {
            foreach (var row in names.Rows)
            {
                int g = names.Int(row, "Genre", -1);
                int d = names.Int(row, "Detail", -1);
                int p = names.Int(row, "Particular", -1);
                int l = names.Int(row, "Level", -1);
                if (g < 0 || d < 0 || p < 0 || l < 0)
                    continue;
                if (!table._byKey.TryGetValue(((uint)g, (uint)d, (uint)p, (uint)l), out var tpl))
                    continue;
                var name = names.Str(row, "Name");
                if (name.Length == 0)
                    continue;
                tpl.DisplayName = name;
                named++;
            }
        }

        int coinRows = table._byKey.Values.Count(t => IsCoinAutoClass(t.ClassName));
        Log.Info($"[setting] {FileName} 载入 {table._byKey.Count} 条物品模板" +
                 $"（自动折算货币的 *CoinAuto 行 {coinRows} 条，中文名 {named} 条）");
        return table;
    }

    /// <summary>按四元组查模板；不存在返回 null。</summary>
    public ItemTemplate? Get(uint g, uint d, uint p, uint l)
        => _byKey.TryGetValue((g, d, p, l), out var tpl) ? tpl : null;

    /// <summary>
    /// 这个四元组是不是「货币类道具」（入包即自动折算成货币）。
    ///
    /// 判定顺序：
    /// <list type="number">
    ///   <item>表里该行 ClassName ∈ <see cref="CoinAutoClassNames"/> 且 ExtParam2 是整数 → 用 ExtParam2
    ///         （ItemLogic.lua:69..88 就是这么建 <c>tbItemCoinItem</c> 的）</item>
    ///   <item>否则查 <see cref="KnownMoneyItems"/> 硬映射（Lib.lua:1364 / UI_Player.lua:795）</item>
    /// </list>
    /// 两条规则在重叠处结论一致（例：(15,6,1,1) 表里 ExtParam2==5，UI_Player 也说货币 5）。
    /// 缺表时第 1 条自然失效、第 2 条仍然有效 —— 这就是降级路径。
    /// </summary>
    public bool TryGetMoneyId(uint g, uint d, uint p, uint l, out int moneyId)
    {
        var tpl = Get(g, d, p, l);
        if (tpl is not null && IsCoinAutoClass(tpl.ClassName)
            && int.TryParse(tpl.ExtParam2, out var fromExt) && fromExt > 0)
        {
            moneyId = fromExt;
            return true;
        }

        if (KnownMoneyItems.TryGetValue((g, d, p, l), out var known))
        {
            moneyId = known;
            return true;
        }

        moneyId = 0;
        return false;
    }

    /// <summary>日志用名：有模板取模板名，没有就退化成四元组字符串。</summary>
    public string NameOf(uint g, uint d, uint p, uint l)
        => Get(g, d, p, l)?.LogName ?? $"({g},{d},{p},{l})";

    private static bool IsCoinAutoClass(string className)
        => Array.IndexOf(CoinAutoClassNames, className) >= 0;
}
