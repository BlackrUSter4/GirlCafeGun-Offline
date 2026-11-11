using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// <c>extracted/setting/*.txt</c> 配置表的通用读取器。
///
/// 这些表是客户端自带的 tab 分隔文本（<c>LoadTabFile(path, 3)</c> —— 第 3 行起是数据），
/// 前三行是表头：
/// <list type="number">
///   <item>第 0 行：列类型（<c>INT/STR/INTARR/INTARR2/MAP/NUL/BOOL/STRARR</c>）</item>
///   <item>第 1 行：中文列名（人读）</item>
///   <item>第 2 行：英文字段名（程序读，本类用它建列索引）</item>
/// </list>
/// 服务端与客户端共用同一份表，是让「服务端行为与原服一致」最省力也最可靠的做法：
/// 体力消耗、通关经验、掉落都直接来自这里，不用猜。
///
/// <para><b>★ 少数表只有 2 行表头（没有「列类型」那一行）</b></para>
/// 实测 <c>ItemExp.txt</c> 就是这样：第 0 行中文名、**第 1 行英文字段名**、第 2 行起是数据
/// （<c>sed -n '2p' ItemExp.txt</c> → <c>Lv NeedExp1 NeedExp2 …</c>）。
/// 按默认的「第 2 行是列名」去读它，会把**第一行数据**当列名 ⇒ 整表列全部找不到、
/// 所有查询静默返回 0 —— 不抛、不报错，是最难查的一类。
/// ⇒ 这种表调用方要显式传 <see cref="Load(string, int)"/> 的 <c>headerRow</c>，
/// 或用 <see cref="LoadAuto"/> 让它按「哪一行含指定列名」自动探测。
/// </summary>
public sealed class SettingTable
{
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);
    private readonly List<string[]> _rows = new();

    private SettingTable() { }

    /// <summary>列名 → 列序号。</summary>
    public IReadOnlyDictionary<string, int> Columns => _columns;

    /// <summary>数据行（不含三行表头）。</summary>
    public IReadOnlyList<string[]> Rows => _rows;

    /// <summary>
    /// 读一张表。文件缺失返回 null（调用方决定降级行为），
    /// 编码按 UTF-8 处理（实测 extracted/setting 全是 UTF-8，行尾可能带 \r）。
    /// </summary>
    /// <param name="path">表文件绝对路径。</param>
    /// <param name="headerRow">
    /// 英文字段名所在的行号（0-based），默认 <b>2</b>（= 三行表头）。
    /// 只有 2 行表头的表（如 <c>ItemExp.txt</c>）要传 <b>1</b>。数据从 <c>headerRow + 1</c> 行开始。
    /// </param>
    public static SettingTable? Load(string path, int headerRow = 2)
    {
        if (!File.Exists(path))
        {
            Log.Warn($"[setting] 配置表不存在：{path}");
            return null;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch (Exception e)
        {
            Log.Warn($"[setting] 读取配置表失败 {path}：{e.Message}");
            return null;
        }

        if (headerRow < 0 || lines.Length <= headerRow)
        {
            Log.Warn($"[setting] 配置表行数不足（需要 > {headerRow} 行表头）：{path}");
            return null;
        }

        var table = new SettingTable();
        var header = lines[headerRow].TrimEnd('\r').Split('\t');
        for (int i = 0; i < header.Length; i++)
        {
            var name = header[i].Trim();
            if (name.Length > 0 && !table._columns.ContainsKey(name))
                table._columns[name] = i;
        }

        for (int i = headerRow + 1; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.Length == 0)
                continue;
            var cells = line.Split('\t');
            if (ExtendEndDates)
                table.ExtendEndCells(cells);
            table._rows.Add(cells);
        }
        return table;
    }

    /// <summary>
    /// --all-events-open: expired event windows load as still running, so the server agrees with the
    /// client's login patch (GmConsoleFeature.AllEventsOpenLua). The files on disk are never changed.
    /// Not listed on purpose: PopEndTime (would stack every old popup on each login) and gacha Time
    /// (banners are scheduled one segment at a time by gacha-rotation.json).
    /// </summary>
    public static bool ExtendEndDates { get; set; }

    private static readonly string[] EndDateColumns =
    {
        "EndTime", "endTime", "End", "LevelEndTime", "FirstAwardEndTime", "RealEndTime", "OpenEndTime", "OffEndTime",
        "ClearTime", "clearTime",   // many event families (towers, linkage, boss) close on this, not EndTime
    };

    private void ExtendEndCells(string[] cells)
    {
        foreach (var column in EndDateColumns)
            if (_columns.TryGetValue(column, out int idx) && idx < cells.Length)
                cells[idx] = ExtendEndDate(cells[idx]);
    }

    /// <summary>Moves a year before 2030 to 2030, keeping the cell's format ({...}, [...], yyyy-MM-dd-HHmm).</summary>
    internal static string ExtendEndDate(string cell)
        => System.Text.RegularExpressions.Regex.Replace(cell, @"(?<!\d)(?:19\d\d|20[012]\d)(?=-\d{2}-\d{2}|\d{4,10}(?!\d))", "2030");

    /// <summary>
    /// 读一张表，并按「哪一行包含 <paramref name="probeColumn"/> 这个列名」**自动探测表头行**。
    ///
    /// 存在的理由：绝大多数表是 3 行表头（列名在第 2 行），但 <c>ItemExp.txt</c> 只有 2 行
    /// （列名在第 1 行）。写死行号会让后者**静默读空**（列全找不到、查询全返回 0、不抛不报错）。
    /// 传一个该表必然存在的列名即可两种都吃。
    ///
    /// 探测范围只有前 3 行（0/1/2）—— 再往下就不是表头了。都不匹配时回退到 <paramref name="fallbackRow"/>。
    /// </summary>
    public static SettingTable? LoadAuto(string path, string probeColumn, int fallbackRow = 2)
    {
        for (int row = 0; row <= 2; row++)
        {
            var t = Load(path, row);
            if (t is null)
                return null;                      // 文件缺失/读失败：不必再试别的行
            if (t._columns.ContainsKey(probeColumn))
                return t;
        }
        Log.Warn($"[setting] 前 3 行都找不到列 \"{probeColumn}\"，按第 {fallbackRow} 行当表头读：{path}");
        return Load(path, fallbackRow);
    }

    /// <summary>取某行某列的原始字符串；列不存在或越界返回空串。</summary>
    public string Str(string[] row, string column)
    {
        if (!_columns.TryGetValue(column, out int idx) || idx >= row.Length)
            return "";
        return row[idx].Trim();
    }

    /// <summary>取某行某列的整数；空/非法按 <paramref name="fallback"/>。</summary>
    public int Int(string[] row, string column, int fallback = 0)
        => int.TryParse(Str(row, column), out var v) ? v : fallback;
}
