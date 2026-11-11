using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// <c>EnergyActivity.txt</c>：「限时领电力」（活动 8 <c>activity.power</c>）的时段表。
///
/// <para>表结构（表头 <b>3</b> 行；现网只有 <b>2</b> 行数据）：</para>
/// <code>
/// Id | Des                  | BeginTime | EndTime | Energy
/// 1  | activity.event9.tip  | 1100      | 1400    | 30
/// 2  | activity.event9.tip  | 1800      | 2000    | 30
/// </code>
///
/// <para><b>★ 时间格式是 <c>HHMM</c> 的整数</b></para>
/// <c>1100</c> = 11:00、<c>1400</c> = 14:00。**不是**秒、不是时间戳。
/// 客户端 <c>UI_EnergyGet.lua</c> 的倒计时也是按这个算的。
///
/// <para><b>★★ 判定用「本地时间」的时分，不是 UTC</b></para>
/// 权威取证 <c>UI_EnergyGet.lua:39..43</c> —— 原厂把 <c>GetTime()</c> 那版**注释掉了**，
/// 改用 <c>Game.Utils.DateTime('HHmm')</c>：
/// <code>
/// -- local tbTime = os.date( "*t", GetTime() );          ← 被注释掉的旧版
/// local nNowTime = tonumber(Game.Utils.DateTime('HHmm')) ← 实际在用
/// </code>
/// 而 <c>Game.Utils.DateTime</c>（RVA <c>0x1537064</c>）内部调
/// <c>Game.ServerStatus$$get_LocalTime</c>（<c>0x17C1858</c>）= 服务器时间戳 + 本地时区偏移
/// ⇒ 玩家看到的是**本地时分**。
///
/// ★ 所以服务端也必须用本地时分判定。用 UTC 判会与客户端错开时区差：
/// 实测开发环境设备是 CST(UTC+8)、宿主 JST(UTC+9)、UTC 相差 8~9 小时
/// ⇒ 客户端显示「可领」而服务端拒绝，玩家点了没反应。
///
/// <para><b>★ 判定左闭右开，且支持跨午夜时段</b></para>
/// 左闭右开与抽卡池一致（<c>docs/05</c> §11.4）、与商店的两端闭**相反**：
/// 14:00:00 整已经不能领了。
/// 跨午夜（<c>EndTime &lt; BeginTime</c>，如 23:00-02:00）走客户端
/// <c>UI_EnergyGet.lua:56</c> 那条 <c>elseif</c> 的同一套判据 ——
/// 现网两行数据都不跨午夜，但配置改了也不会错。
/// </summary>
public sealed class EnergyActivityTable
{
    /// <summary>配置文件名（相对 setting 目录）。</summary>
    public const string FileName = "EnergyActivity.txt";

    private readonly List<EnergySlot> _slots = new();

    private EnergyActivityTable() { }

    /// <summary>
    /// 空表（配置缺失时的降级值）。★ 空表意味着**任何时刻都不在时段内** ⇒ 领取一律静默拒绝，
    /// 这比「乱发电力」安全。
    /// </summary>
    public static EnergyActivityTable Empty { get; } = new();

    /// <summary>全部时段（按 <c>BeginTime</c> 升序）。</summary>
    public IReadOnlyList<EnergySlot> Slots => _slots;

    /// <summary>时段数。</summary>
    public int Count => _slots.Count;

    /// <summary>
    /// 当前时刻落在哪个时段里；不在任何时段内返回 <c>null</c>。
    /// ★ 用 <paramref name="now"/> 的**本地时分**（见类注释：客户端口径是本地时间）。
    /// </summary>
    public EnergySlot? SlotAt(DateTimeOffset now)
    {
        int hhmm = now.Hour * 100 + now.Minute;
        foreach (var s in _slots)
        {
            if (s.Contains(hhmm))
                return s;
        }
        return null;
    }

    /// <summary>按 Id 取时段（GM/单测用）。</summary>
    public EnergySlot? SlotById(int id) => _slots.FirstOrDefault(s => s.Id == id);

    /// <summary>供日志用的一行摘要。</summary>
    public string Describe()
        => _slots.Count == 0 ? "（空表）" : string.Join("、", _slots.Select(s => s.Describe()));

    public static EnergyActivityTable Load(string settingDir)
    {
        // 表头 3 行（默认约定：第 2 行列名、数据从第 3 行起）。
        // ★ 用 LoadAuto + 必有列名做探测，防「表头行数不对 ⇒ 整表静默返回 0」那类最难查的 bug。
        var table = SettingTable.LoadAuto(Path.Combine(settingDir, FileName), "BeginTime");
        if (table is null)
            return Empty;

        var result = new EnergyActivityTable();
        foreach (var row in table.Rows)
        {
            int id = table.Int(row, "Id", -1);
            if (id <= 0)
                continue;
            int begin = table.Int(row, "BeginTime", -1);
            int end = table.Int(row, "EndTime", -1);
            int energy = table.Int(row, "Energy", 0);
            if (begin < 0 || end < 0 || energy <= 0)
                continue;
            result._slots.Add(new EnergySlot(id, begin, end, energy));
        }
        result._slots.Sort((a, b) => a.BeginTime.CompareTo(b.BeginTime));
        return result;
    }
}

/// <summary>一个领电力时段（<c>EnergyActivity.txt</c> 的一行）。</summary>
/// <param name="Id">时段编号 —— ★ 同时决定任务值 taskId（<c>10000 + Id</c>）。</param>
/// <param name="BeginTime">开始时间，<c>HHMM</c> 整数（含）。</param>
/// <param name="EndTime">结束时间，<c>HHMM</c> 整数（不含）。</param>
/// <param name="Energy">领取的电力数量。</param>
public sealed record EnergySlot(int Id, int BeginTime, int EndTime, int Energy)
{
    /// <summary>是不是跨午夜时段（如 23:00-02:00）。</summary>
    public bool WrapsMidnight => EndTime < BeginTime;

    /// <summary>
    /// 给定 <c>HHMM</c> 是否落在本时段内（**左闭右开**）。
    /// 跨午夜时用「≥ 开始 或 &lt; 结束」，与客户端 <c>UI_EnergyGet.lua:56</c> 的
    /// <c>elseif nEndTime &lt; nBeginTime and (nNowTime &gt;= nBeginTime or nNowTime &lt; nEndTime)</c> 同构。
    /// </summary>
    public bool Contains(int hhmm)
        => WrapsMidnight ? (hhmm >= BeginTime || hhmm < EndTime)
                         : (hhmm >= BeginTime && hhmm < EndTime);

    /// <summary>供日志用（形如 <c>#1 11:00-14:00 +30</c>）。</summary>
    public string Describe()
        => $"#{Id} {BeginTime / 100:00}:{BeginTime % 100:00}-{EndTime / 100:00}:{EndTime % 100:00} +{Energy}";
}
