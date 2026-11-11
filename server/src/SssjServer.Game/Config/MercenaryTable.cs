using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 佣兵赌注（新爬塔）活动的配置。取证 `extracted/lua/MercenaryLogic.lua`、台账 `docs/05` §31。
///
/// <para><b>0. ★★★ 档期是<u>算</u>出来的，不能靠本地快照的表行数判断</b></para>
/// `MercenaryTower.txt` 的每一行只有 <c>WeekId</c>，**没有时间列**；
/// 时间由 `MercenaryLogic.lua:95..99` 现算：
/// <code>
/// ClearTime = FirstClearTime + (WeekId - 1) * 一周
/// BeginTime = max(ClearTime - 一周, MercenaryTotal.BeginTime)
/// </code>
/// 而 `GetCurTowerConfig`（`:284..292`）遍历全表找
/// <c>BeginTime &lt;= now &lt; ClearTime</c> 的那一周。
///
/// <para><b>★★★ 本地快照只有 WeekId 1..102，设备上有 891 个 —— 血泪教训</b></para>
/// 本轮先按本地快照的 102 行算出「档期在 2022-04 全部结束 ⇒ 佣兵是死内容」，
/// 而真机实测 <c>towerCount=891</c>、<c>lastWeek ClearTime</c> 落在 **2037-05**、
/// <c>GetCurTowerConfig</c> **非 nil**、服务端**确实收到了 320**。
/// ⇒ **`extracted/setting/` 的快照可能比设备上的表少行**，
/// 「按本地表行数推断档期」会得出完全相反的结论。
/// ⇒ 因此本实现**不依赖表行数**：直接用公式从当前时间**反解** WeekId
/// （<see cref="CurrentWeekId"/>），这样无论表有几行都对。
///
/// <para><b>1. 表头行数（全部来自客户端自己的 <c>LoadTabFile</c> 第 2 实参）</b></para>
/// <c>MercenaryTotal.txt</c> / <c>MercenaryTower.txt</c> / <c>MercenaryBuff.txt</c> /
/// <c>MercenaryBuffIcon.txt</c> / <c>MercenaryFate.txt</c> / <c>MercenaryLevel.txt</c> 都是 **3**
/// （`MercenaryLogic.lua:59/86/111/129/143/190`）；
/// ★ 只有 <c>CardOffset.txt</c> 是 **2**（`:160`）—— 本域暂不用它。
/// </summary>
public sealed class MercenaryTable
{
    /// <summary>佣兵活动的任务值 group（`MercenaryLogic.lua:4` <c>Group = 64</c>）。</summary>
    public const int Group = 64;

    // ===== 任务值偏移（`MercenaryLogic.lua:5..36` 逐条抄，注释保留原文语义）=====

    /// <summary>0 清除数据时间。</summary>
    public const int TaskClearDataTime = 0;

    /// <summary>1 当前这轮的 WeekId。</summary>
    public const int TaskCurWeekId = 1;

    /// <summary>5 缓存上周的最高分。</summary>
    public const int TaskCacheLastWeekMoney = 5;

    /// <summary>6 当前可领取的代币数量（每周清空）。★ 同时是「每周清空」区段的起点。</summary>
    public const int TaskCanGetMoney = 6;

    /// <summary>10 本周获得的代币数量（每周清空）—— 塔界面「最高分」显示它。</summary>
    public const int TaskWeekMoney = 10;

    /// <summary>11 当前步骤：0 开始 / 1 等待选 buff / 2 等待编队 / 3 闯关 / 4 结算。</summary>
    public const int TaskStep = 11;

    /// <summary>12 剩余次数 —— 塔界面「次数 x/y」的分子。</summary>
    public const int TaskWeekCount = 12;

    /// <summary>「每周清空」区段起点（`MercenaryLogic.lua:31` <c>Task_WeekClear = 6</c>）。</summary>
    public const int TaskWeekClear = 6;

    /// <summary>「结算清空」区段起点（`:32` <c>Task_SettlementClear = 61</c>）。</summary>
    public const int TaskSettlementClear = 61;

    /// <summary>任务值区段上界（`:33` <c>Task_Max = 180</c>）。</summary>
    public const int TaskMax = 180;

    /// <summary>步骤：从头开始。</summary>
    public const int StepStart = 0;

    /// <summary>步骤：等待选 buff。</summary>
    public const int StepSelectBuff = 1;

    /// <summary>步骤：等待编队。</summary>
    public const int StepFormation = 2;

    /// <summary>步骤：闯关中。</summary>
    public const int StepFighting = 3;

    /// <summary>步骤：结算。</summary>
    public const int StepSettlement = 4;

    private const long OneWeek = 7L * 24 * 3600;

    private MercenaryTable() { }

    /// <summary>空表（配置缺失时降级：活动视为未开放）。</summary>
    public static MercenaryTable Empty { get; } = new();

    /// <summary>活动总开始时间（unix 秒）。0 = 配置缺失。</summary>
    public long BeginTime { get; private set; }

    /// <summary>第一周的结算时间（unix 秒）。0 = 配置缺失。</summary>
    public long FirstClearTime { get; private set; }

    /// <summary>每周可玩次数（<c>MercenaryTotal.WeekCount</c>，实测 3）。</summary>
    public int WeekCount { get; private set; }

    /// <summary>周奖励（<c>MercenaryTotal.Rewards</c>，实测 <c>[15-44-1-1-1]</c> 佣兵代币 ×1）。</summary>
    public IReadOnlyList<int[]> WeekRewards { get; private set; } = Array.Empty<int[]>();

    /// <summary>佣兵商店 id（<c>ShopId</c>，实测 20009）。</summary>
    public int ShopId { get; private set; }

    /// <summary>本地快照里有配置的 WeekId 集合（★ 只用于日志/测试，**判定档期不靠它**）。</summary>
    public IReadOnlyCollection<int> KnownWeekIds => _weekIds;

    private readonly HashSet<int> _weekIds = new();

    /// <summary>载入两张表；缺表只降级、不抛。</summary>
    public static MercenaryTable Load(string settingDir)
    {
        var t = new MercenaryTable();
        t.LoadTotal(Path.Combine(settingDir, "MercenaryTotal.txt"));
        t.LoadTower(Path.Combine(settingDir, "MercenaryTower.txt"));
        t.LoadLevels(Path.Combine(settingDir, "MercenaryLevel.txt"));

        int cur = t.CurrentWeekId(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Log.Info($"[setting] 佣兵赌注配置：开始 {Fmt(t.BeginTime)}、首周结算 {Fmt(t.FirstClearTime)}、" +
                 $"每周 {t.WeekCount} 次、商店 {t.ShopId}、周奖励 {t.WeekRewards.Count} 项、" +
                 $"本地快照 {t._weekIds.Count} 个 WeekId（★ 设备上可能更多，判档期用公式不用表）、" +
                 $"当前第 {cur} 周、关卡 {t._levels.Count} 关");
        return t;
    }

    private static string Fmt(long unix)
        => unix <= 0 ? "-" : DateTimeOffset.FromUnixTimeSeconds(unix).ToString("yyyy-MM-dd");

    private readonly Dictionary<int, MercenaryLevelRow> _levels = new();
    private readonly Dictionary<int, List<int>> _weekLevels = new();   // WeekId → 该周关卡清单（有序）

    /// <summary>佣兵关卡数（探针用）。</summary>
    public int LevelCount => _levels.Count;

    /// <summary>取一关的配置；不存在返回 null。</summary>
    public MercenaryLevelRow? LevelOf(int levelId)
        => _levels.TryGetValue(levelId, out var row) ? row : null;

    /// <summary>
    /// 载入 <c>MercenaryLevel.txt</c>（288 关，表头 3 行 ——
    /// <c>MercenaryLogic.lua:190</c> <c>LoadTabFile('Activities/Mercenary/MercenaryLevel.txt', 3)</c>）。
    ///
    /// ★★ 列名与主线 <c>Chapter.txt</c> **不完全同名**：这里是 <c>FixAward</c>
    /// （主线是 <c>FixedAward</c>）—— 抄主线的解析会让固定奖励**整列读空、静默不发奖**。
    /// </summary>
    private void LoadLevels(string path)
    {
        var table = SettingTable.LoadAuto(path, "LevelId");
        if (table is null)
            return;

        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "LevelId", 0);
            if (id <= 0)
                continue;

            _levels[id] = new MercenaryLevelRow(
                LevelId: id,
                LevelType: table.Int(row, "LevelType", 0),
                FateId: table.Int(row, "FateId", 0),
                MapId: table.Int(row, "MapId", 0),
                PreCost: table.Int(row, "PreCost", 0),
                // ★ 用本文件的 ParseRewards（只取 5 位）—— 实测佣兵这三列都是**纯 5 位**
                //   （样例 `[7-8-1-4-1,15-20-1-1-10]`），没有主线那种第 6 位（等级/概率/权重）。
                FirstAward: ParseRewards(table.Str(row, "FirstAward")),
                FixAward: ParseRewards(table.Str(row, "FixAward")),
                RandomAward: ParseRewards(table.Str(row, "RandomAward")));
        }
    }

    /// <summary>
    /// 载入 <c>MercenaryTotal.txt</c>。
    /// <c>FirstClearTime</c> 的算法 1:1 照 `MercenaryLogic.lua:57..80`：
    /// 取 <c>BeginTime</c> 那天的星期，推到 <c>WeekClearTime</c>（形如 <c>1,4:00:00</c>
    /// = 周一 04:00）对应的那天，再加一周的天数差。
    /// </summary>
    private void LoadTotal(string path)
    {
        var table = SettingTable.LoadAuto(path, "BeginTime");
        var row = table?.Rows.FirstOrDefault();
        if (table is null || row is null)
            return;

        BeginTime = ParseDate(table.Str(row, "BeginTime"));
        WeekCount = table.Int(row, "WeekCount");
        ShopId = table.Int(row, "ShopId");
        WeekRewards = ParseRewards(table.Str(row, "Rewards"));

        // WeekClearTime = "<星期>,<HH:MM:SS>"，星期 1 = 周一（Lua 里 wday 周日=1，所以 +1）
        var parts = table.Str(row, "WeekClearTime").Split(',', StringSplitOptions.TrimEntries);
        int clearWeekday = parts.Length > 0 && int.TryParse(parts[0], out var w) ? w : 1;
        var hms = (parts.Length > 1 ? parts[1] : "4:00:00").Split(':');
        int hour = hms.Length > 0 && int.TryParse(hms[0], out var hh) ? hh : 4;
        int min = hms.Length > 1 && int.TryParse(hms[1], out var mm) ? mm : 0;

        if (BeginTime <= 0)
            return;

        // ★ 与 Lua 同源：begintime.wday（周日=1） → clearweek = 配置值 + 1
        var begin = DateTimeOffset.FromUnixTimeSeconds(BeginTime).UtcDateTime;
        int beginWeek = (int)begin.DayOfWeek + 1;      // Sunday=0 → 1
        int clearWeek = clearWeekday + 1;
        int spanDay = clearWeek - beginWeek;
        if (spanDay <= 0)
            spanDay += 7;

        var atClock = new DateTime(begin.Year, begin.Month, begin.Day, hour, min, 0, DateTimeKind.Utc);
        FirstClearTime = new DateTimeOffset(atClock).ToUnixTimeSeconds() + spanDay * 24L * 3600;
    }

    private void LoadTower(string path)
    {
        var table = SettingTable.LoadAuto(path, "WeekId");
        if (table is null)
            return;
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "WeekId", -1);
            if (id <= 0)
                continue;
            _weekIds.Add(id);

            // ★ LevelIds 是**逗号**分隔的关卡 id 清单（客户端 `MercenaryLogic.lua:92`
            //   `Parse2Item(tbRow["LevelIds"] or "", ",")` —— 第 2 个实参就是分隔符）。
            //   本仓踩过「同表两个 INTARR 列分隔符不同」的坑 ⇒ 这里按客户端实参定案。
            var ids = new List<int>();
            foreach (var seg in table.Str(row, "LevelIds")
                                     .Replace("[", "").Replace("]", "")
                                     .Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(seg.Trim(), out int lv) && lv > 0)
                    ids.Add(lv);
            }
            if (ids.Count > 0)
                _weekLevels[id] = ids;
        }
    }

    /// <summary>
    /// 某关在**某一周清单**里的 1-based 序号；不在该周返回 0。
    /// 对应客户端 <c>CheckSkipLevel</c> 里那个 <c>for idx, nLevel in ipairs(tbLevelIds)</c>。
    /// </summary>
    public int LevelIndexInWeek(int levelId, int weekId)
    {
        if (!_weekLevels.TryGetValue(weekId, out var ids))
            return 0;
        int at = ids.IndexOf(levelId);
        return at < 0 ? 0 : at + 1;
    }

    /// <summary>
    /// 某关在**当前周**清单里的序号（按公式反解当前周）。
    /// ★ 本地 <c>MercenaryTower.txt</c> 的 WeekId 可能不覆盖当前周
    /// （表快照比设备旧，AGENTS.md 记过这条）⇒ 找不到时退化成
    /// 「在所有周清单里的首个命中序号」，避免「表旧 ⇒ 功能全不可用」。
    /// </summary>
    public int LevelIndexInWeek(int levelId)
    {
        int cur = CurrentWeekId(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        int idx = LevelIndexInWeek(levelId, cur);
        if (idx > 0)
            return idx;

        foreach (var kv in _weekLevels)
        {
            int at = kv.Value.IndexOf(levelId);
            if (at >= 0)
                return at + 1;
        }
        return 0;
    }

    /// <summary>该周的结算时间（`MercenaryLogic.lua:95`）。</summary>
    public long ClearTimeOf(int weekId)
        => FirstClearTime <= 0 ? 0 : FirstClearTime + (weekId - 1) * OneWeek;

    /// <summary>该周的开始时间（`:96..99`，不早于活动总开始时间）。</summary>
    public long BeginTimeOf(int weekId)
    {
        long t = ClearTimeOf(weekId) - OneWeek;
        return t < BeginTime ? BeginTime : t;
    }

    /// <summary>
    /// 当前时间落在第几周（<c>0</c> = 活动未开放 / 配置缺失）。
    ///
    /// ★★ **用公式反解，不遍历表** —— 理由见类注释 §0：本地快照可能比设备少行，
    /// 遍历本地表会把「其实开着的周」判成 nil。
    /// 反解式：<c>weekId = floor((now - FirstClearTime) / 一周) + 2</c>
    /// （因为第 1 周的区间是 <c>[BeginTime, FirstClearTime)</c>）。
    /// </summary>
    public int CurrentWeekId(long nowUnix)
    {
        if (FirstClearTime <= 0 || nowUnix < BeginTime)
            return 0;
        if (nowUnix < FirstClearTime)
            return 1;
        long delta = nowUnix - FirstClearTime;
        return (int)(delta / OneWeek) + 2;
    }

    /// <summary>活动此刻是否开放（当前周 &gt; 0）。</summary>
    public bool IsOpen(long nowUnix) => CurrentWeekId(nowUnix) > 0;

    private static long ParseDate(string text)
    {
        // 形如 20200427
        if (text.Length == 8 && int.TryParse(text, out _)
            && DateTime.TryParseExact(text, "yyyyMMdd", null,
                   System.Globalization.DateTimeStyles.AssumeUniversal
                   | System.Globalization.DateTimeStyles.AdjustToUniversal, out var d))
        {
            return new DateTimeOffset(d, TimeSpan.Zero).ToUnixTimeSeconds();
        }
        return 0;
    }

    /// <summary>解析 <c>[15-44-1-1-1]</c> 形式的奖励列表（**短横**分隔，逗号分隔多组）。</summary>
    internal static List<int[]> ParseRewards(string text)
    {
        var result = new List<int[]>();
        if (string.IsNullOrWhiteSpace(text))
            return result;
        foreach (var seg in text.Replace("[", "").Replace("]", "")
                                .Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = seg.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 5)
                continue;
            var one = new int[5];
            bool ok = true;
            for (int i = 0; i < 5; i++)
            {
                if (!int.TryParse(f[i], out one[i]))
                {
                    ok = false;
                    break;
                }
            }
            if (ok)
                result.Add(one);
        }
        return result;
    }
}

/// <summary>
/// 佣兵赌注的一关（<c>MercenaryLevel.txt</c> 的一行）。
/// </summary>
/// <param name="LevelId">关卡 id（<c>LevelId</c> 列）。</param>
/// <param name="LevelType">
/// 关卡类型。★ 客户端对 <b>2 = 剧情关</b> 有专门分支（<c>MercenaryLogic.lua:592</c>
/// 直接播 CG、不进战斗；通关回包走 <c>:658</c> 只开 <c>UI_RewardNormal</c>）。
///
/// ★★ **但现网这张表里没有 2**：实测 288 关的 <c>LevelType</c> 值域是
/// <c>{0: 80, 1: 20, 3: 11, 4: 167}</c> ⇒ **剧情关分支走不到**。
/// 仍保留 <see cref="MercenaryLevelRow.IsStory"/> 与相应处理，
/// 因为配置随时可能更新，且那条分支的回包形状与普通关不同（<c>#tbData</c> 取长度）。
/// </param>
/// <param name="FateId">命运（fate）id，用于 328 选 fate（本轮未实现）。</param>
/// <param name="MapId">
/// 场景地图 id。客户端 <c>MercenaryLogic.lua:600</c> <c>Map.LoadById(MapId)</c>
/// —— 服务端不需要下发它（客户端自己从配置读），但用于日志与合法性校验。
/// </param>
/// <param name="PreCost">进关消耗（<c>PreCost</c> 列）。</param>
/// <param name="FirstAward">首通奖励（<c>G-D-P-L-N-Level</c>，第 6 位是角色卡等级）。</param>
/// <param name="FixAward">
/// 固定奖励。★★ 列名是 <c>FixAward</c> **不是** <c>FixedAward</c>（主线那张表才是后者）。
/// </param>
/// <param name="RandomAward">随机奖励（第 6 位是权重）。</param>
public sealed record MercenaryLevelRow(
    int LevelId,
    int LevelType,
    int FateId,
    int MapId,
    int PreCost,
    List<int[]> FirstAward,
    List<int[]> FixAward,
    List<int[]> RandomAward)
{
    /// <summary>是否剧情关（<c>LevelType == 2</c>，见 <see cref="LevelType"/> 的取证）。</summary>
    public bool IsStory => LevelType == StoryLevelType;

    /// <summary>剧情关的 <c>LevelType</c> 值。</summary>
    public const int StoryLevelType = 2;
}
