using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// <c>ClubLevels.txt</c> 的一行（社团等级 1..10）。列名与表里的英文列名一致。
///
/// 客户端侧的同构实现是 <c>ClubCommon:LoadConfig</c>（<c>extracted/lua/ClubCommon.lua:57..84</c>），
/// 加载路径是 <c>LoadTabFile("Club/ClubLevels.txt", 3)</c>（<c>ClubCommon.lua:30</c>）——
/// ★ 客户端的路径带 <c>Club/</c> 前缀，而 <c>extracted/setting/</c> 是**扁平**目录
/// （<c>find extracted/setting -type d</c> 只有它自己），服务端读的是扁平同名文件。
/// </summary>
public sealed class ClubLevelRow
{
    /// <summary>社团等级（<c>Level</c>，1..10），主键。</summary>
    public int Level { get; init; }

    /// <summary>
    /// 升到本级所需的**累计** exp（<c>LvUpExp</c>）。
    ///
    /// ★ 是「累计门槛」不是「本级增量」：10 级依次
    /// <c>0 / 10000 / 20000 / 30000 / 50000 / 70000 / 90000 / 110000 / 130000 / 150000</c>。
    /// 客户端显示的是 <c>exp / 下一级的 LvUpExp</c>
    /// （<c>UI_Club.lua:210..211</c> <c>GetClubLevelConfig(level+1).LvUpExp</c>）。
    /// </summary>
    public int LvUpExp { get; init; }

    /// <summary>
    /// 人数上限（<c>CountLmt</c>，30/32/34/36/38/40/43/45/47/50）。
    ///
    /// ★ 客户端**无 nil 保护**地读它：<c>UI_Club.lua:206</c>
    /// <c>string.format("%d/%d", #memberlist, levelCfg.CountLmt)</c>、
    /// <c>UI_ClubMember.lua:186..187</c>、<c>:348..349</c>
    /// ⇒ 下发的 <c>primary.level</c> 必须落在 1..10 内，否则 <c>levelCfg</c> 为 nil 直接抛。
    /// </summary>
    public int CountLmt { get; init; }

    /// <summary>创建社团的费用货币 id（<c>CreateFee</c> 列 <c>"03|250"</c> 的前半，= 3 免费钻）。</summary>
    public int CreateFeeType { get; init; }

    /// <summary>创建社团的费用数量（<c>"03|250"</c> 的后半 = 250）。</summary>
    public int CreateFee { get; init; }

    /// <summary>
    /// 「自动换团长时间(天)」（<c>DismissTime</c>，恒 7）——
    /// ★ **不是解散时长**（那个是客户端常量 <c>ClubCommon.DissolveTimeLimit = 86400*3-1</c>，
    /// <c>ClubCommon.lua:8</c>）。社长长期不上线自动转让用，本服未实现。
    /// </summary>
    public int DismissTime { get; init; }

    /// <summary>最大商店栏数（<c>MaxShopCloumnNum</c>，恒 17）。社团商店 20006 用，<c>ShopFeature</c> 已处理。</summary>
    public int MaxShopColumnNum { get; init; }

    /// <summary>
    /// 每日福利（<c>DailyAwards</c>，如 <c>[15-14-1-1-5,15-1-1-1-2000]</c>）的 <c>(G,D,P,L,N)</c> 元组表。
    ///
    /// 1 级是「5 点社团贡献（<c>15-14</c> → 货币 11）+ 2000 金币（<c>15-1</c>）」。
    /// 客户端拿它渲染 <c>UI_ClubReward</c> 的奖励条（<c>UI_ClubReward.lua:38..45</c>），
    /// 服务端拿它作为 <c>Club_DailyAward</c>(10020) 的**发奖内容**。
    /// </summary>
    public IReadOnlyList<int[]> DailyAwards { get; init; } = Array.Empty<int[]>();

    /// <summary>Boss 压制加成百分比（<c>Boss</c>，30..60）。boss 域未实现，仅留档。</summary>
    public int Boss { get; init; }

    /// <summary>每日踢人上限（<c>DailyKickLimit</c>，恒 3）。踢人命令未实现，仅留档。</summary>
    public int DailyKickLimit { get; init; }
}

/// <summary>
/// 社团域的配置表。**只读一张 <c>ClubLevels.txt</c>**（10 个数据行）——
/// 取证文档 `work/club-domain-forensics.md` §9.1 的结论是「社团域唯一必读的表」，
/// 其余 37 张 <c>Club*</c> 表全是 boss / 周任务专用（§9.2），最小切片一张都不需要。
///
/// <para><b>三条「不存在的表」（不要去找）</b>（取证 §9.2.3）</para>
/// <list type="bullet">
/// <item>社团**职位**表：不存在，职位硬编码 9/8/0（<c>ClubCommon.lua:4..6</c>）。</item>
/// <item>社团**捐献**表：不存在 —— 本作没有捐献玩法，社团贡献（货币 11）来自社团任务与 boss 奖励。</item>
/// <item>社团**每日奖励**表：不存在，寄生在本表的 <see cref="ClubLevelRow.DailyAwards"/> 列。</item>
/// </list>
///
/// <para><b>编码</b></para>
/// ★ <c>extracted/setting/</c> 全是 **UTF-8 + CRLF**，不是 GBK（取证 §9.0：
/// <c>file extracted/setting/ClubLevels.txt</c> → <c>UTF-8 text, with CRLF</c>，
/// 而 <c>iconv -f GBK</c> 反而把第 2 行中文弄成乱码）。
/// <see cref="SettingTable"/> 已经按 UTF-8 打开，直接用它即可。
///
/// <para><b>缺文件时的降级</b></para>
/// 与其它配置表一致：<see cref="Empty"/> + 一条 WARN，**绝不抛**。
/// 空表下 <see cref="Level"/> 会回落到 <see cref="FallbackLevel1"/>
/// （= 现网第 1 级的逐字副本），保证「没有配置表也能创建社团」。
/// </summary>
public sealed class ClubTable
{
    /// <summary>配置表文件名。</summary>
    public const string FileName = "ClubLevels.txt";

    /// <summary>社团最高等级（现网 <c>ClubLevels.txt</c> 恰好 10 行）。空表时也用它当上界。</summary>
    public const int MaxLevelFallback = 10;

    /// <summary>
    /// 缺配置表时的第 1 级兜底值 —— **现网 <c>ClubLevels.txt</c> 第 4 行的逐字副本**
    /// （取证 §9.1「第 1 级完整行」）：
    /// <c>Level=1 LvUpExp=0 CountLmt=30 CreateFee=03|250 DismissTime=7 MaxShopCloumnNum=17
    /// DailyAwards=[15-14-1-1-5,15-1-1-1-2000] Boss=30 DailyKickLimit=3</c>。
    ///
    /// 存在的理由：<c>primary.level</c> 一旦下发成客户端 <c>ClubLevelConfig</c> 里查不到的值，
    /// <c>UI_Club.lua:206</c> 的 <c>levelCfg.CountLmt</c> 就会抛 —— 所以服务端宁可用兜底值，
    /// 也不能让「社团等级」变成 0 或越界值。
    /// </summary>
    public static readonly ClubLevelRow FallbackLevel1 = new()
    {
        Level = 1,
        LvUpExp = 0,
        CountLmt = 30,
        CreateFeeType = MoneyType.FreeDiamond,
        CreateFee = 250,
        DismissTime = 7,
        MaxShopColumnNum = 17,
        DailyAwards = new[] { new[] { 15, 14, 1, 1, 5 }, new[] { 15, 1, 1, 1, 2000 } },
        Boss = 30,
        DailyKickLimit = 3,
    };

    /// <summary>空表（缺文件时的降级值，也是单测基线）。</summary>
    public static ClubTable Empty { get; } = new();

    private readonly Dictionary<int, ClubLevelRow> _levels = new();

    private ClubTable() { }

    /// <summary>全部等级行（按 <c>Level</c> 升序）。</summary>
    public IReadOnlyList<ClubLevelRow> Levels
        => _levels.Values.OrderBy(r => r.Level).ToList();

    /// <summary>表里出现过的最高等级；空表返回 <see cref="MaxLevelFallback"/>。</summary>
    public int MaxLevel => _levels.Count == 0 ? MaxLevelFallback : _levels.Keys.Max();

    /// <summary>
    /// 取某一级的配置。**永不返回 null** —— 越界/空表时回落到
    /// <see cref="FallbackLevel1"/>（理由见它的注释）。
    /// </summary>
    public ClubLevelRow Level(int level)
        => _levels.TryGetValue(level, out var row) ? row : FallbackLevel1;

    /// <summary>该等级是否真的在表里（下发 <c>primary.level</c> 之前的合法性判据）。</summary>
    public bool HasLevel(int level) => _levels.ContainsKey(level);

    /// <summary>
    /// 由累计 exp 反算社团等级 = 满足 <c>exp &gt;= LvUpExp</c> 的最高一级。
    ///
    /// 取证 <see cref="ClubLevelRow.LvUpExp"/>：<c>LvUpExp</c> 是累计门槛且单调递增，
    /// 首行为 0 ⇒ exp=0 时结果恒为 1。空表时恒返回 1（配合 <see cref="FallbackLevel1"/>）。
    /// </summary>
    public int LevelForExp(int exp)
    {
        int best = 1;
        foreach (var row in _levels.Values)
        {
            if (exp >= row.LvUpExp && row.Level > best)
                best = row.Level;
        }
        return best;
    }

    public static ClubTable Load(string settingDir)
    {
        var table = new ClubTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] 找不到 {FileName}，社团等级表降级为空 —— " +
                     "人数上限/创建费用/每日福利一律用第 1 级兜底值（ClubTable.FallbackLevel1）");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int level = raw.Int(row, "Level");
            if (level <= 0)
                continue;

            // CreateFee 列的写法是 "03|250"（货币 id | 数量），与 ClubCommon.lua:66..70 的
            // Split(tbRow["CreateFee"], "|") 同口径；拆不出两段就退回兜底值。
            int feeType = FallbackLevel1.CreateFeeType;
            int fee = FallbackLevel1.CreateFee;
            var feeParts = raw.Str(row, "CreateFee").Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (feeParts.Length == 2
                && int.TryParse(feeParts[0].Trim(), out int parsedType)
                && int.TryParse(feeParts[1].Trim(), out int parsedFee))
            {
                feeType = parsedType;
                fee = parsedFee;
            }

            table._levels[level] = new ClubLevelRow
            {
                Level = level,
                LvUpExp = raw.Int(row, "LvUpExp"),
                CountLmt = raw.Int(row, "CountLmt", FallbackLevel1.CountLmt),
                CreateFeeType = feeType,
                CreateFee = fee,
                DismissTime = raw.Int(row, "DismissTime"),
                MaxShopColumnNum = raw.Int(row, "MaxShopCloumnNum"),
                DailyAwards = ParseAwardList(raw.Str(row, "DailyAwards")),
                Boss = raw.Int(row, "Boss"),
                DailyKickLimit = raw.Int(row, "DailyKickLimit"),
            };
        }

        var lv1 = table.Level(1);
        Log.Info($"[setting] 社团等级表：{table._levels.Count} 级（最高 {table.MaxLevel}）；" +
                 $"1 级 人数上限 {lv1.CountLmt}、创建费用 货币{lv1.CreateFeeType}×{lv1.CreateFee}、" +
                 $"每日福利 {lv1.DailyAwards.Count} 项");
        return table;
    }

    /// <summary>
    /// 解析 <c>DailyAwards</c> 这种「一个中括号里逗号分隔多个五元组」的写法：
    /// <c>[15-14-1-1-5,15-1-1-1-2000]</c> → <c>{{15,14,1,1,5},{15,1,1,1,2000}}</c>。
    ///
    /// ★ **不能用 <see cref="ChapterTable.ParseTuples"/>** —— 那个按「每个中括号一个元组」切，
    /// 会把 <c>5,15</c> 当成一段而 <c>int.TryParse</c> 失败，整行被丢掉。
    /// 这里照客户端 <c>Parse2ItemList(szContent, sep1=",", sep2="-")</c>
    /// （<c>Lib.lua:282..290</c>，先整串去掉 <c>[ ] "</c> 与空格，再按 <c>,</c> 拆、每段按 <c>-</c> 拆）。
    /// </summary>
    internal static List<int[]> ParseAwardList(string text)
    {
        var result = new List<int[]>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        // string.gsub(szContent, '[%[" %]]', "") —— 去掉 [ ] " 与空格
        var cleaned = text.Replace("[", "").Replace("]", "").Replace("\"", "").Replace(" ", "");
        foreach (var seg in cleaned.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = seg.Split('-', StringSplitOptions.RemoveEmptyEntries);
            var nums = new List<int>(parts.Length);
            bool ok = parts.Length >= 4;
            foreach (var p in parts)
            {
                if (!int.TryParse(p.Trim(), out int n))
                {
                    ok = false;
                    break;
                }
                nums.Add(n);
            }
            if (ok)
                result.Add(nums.ToArray());
        }
        return result;
    }
}
