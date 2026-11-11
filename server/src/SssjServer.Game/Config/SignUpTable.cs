using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// <c>NormalActivity/SignUpActivity.txt</c>（本仓 <c>extracted/setting/SignUpActivity.txt</c>）：
/// 每月签到奖励表。
///
/// 取证 <c>extracted/lua/NormalActivityConfig.lua:517..523</c>：
/// <code>
/// local tbFile = LoadTabFile( "NormalActivity/SignUpActivity.txt", 3 );
/// local nMonth = tonumber(tbInfo.Month);
/// local nDay   = tonumber(tbInfo.SignUpCount);
/// self.tbSignUpActivityConfig[nMonth][nDay] = { tbAward = Parse2Item( tbInfo["Award"] ) };
/// </code>
/// 表头（第 3 行）：<c>Month | SignUpCount | Award</c>，Award 形如 <c>[15-1-1-1-5000]</c>。
///
/// ★ 列名是 <c>SignUpCount</c>（**本月第几次签到**），不是日期 —— 与客户端
/// <c>UI_SignActivityPop:Refresh</c> 读的 <c>me:GetTaskValue(20, 11002)</c>「本月总共签了几次」
/// 是同一个口径。所以第 N 次签到发的是 <c>SignUpCount == N</c> 那一行。
/// </summary>
public sealed class SignUpTable
{
    /// <summary>配置文件名（相对 setting 目录）。</summary>
    public const string FileName = "SignUpActivity.txt";

    private readonly Dictionary<(int Month, int Count), List<int[]>> _awards = new();
    private readonly Dictionary<int, int> _maxCount = new();

    private SignUpTable() { }

    /// <summary>空表（配置缺失时的降级值：签到仍成功，只是没有奖励）。</summary>
    public static SignUpTable Empty { get; } = new();

    /// <summary>条目数。</summary>
    public int Count => _awards.Count;

    /// <summary>取某月第 <paramref name="count"/> 次签到的奖励元组；无配置返回空表。</summary>
    public List<int[]> Award(int month, int count)
        => _awards.TryGetValue((month, count), out var v) ? v : new List<int[]>();

    /// <summary>某月配置的最大签到次数（通常 = 该月天数）。</summary>
    public int MaxCount(int month) => _maxCount.TryGetValue(month, out var v) ? v : 0;

    public static SignUpTable Load(string settingDir)
    {
        var table = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (table is null)
            return Empty;

        var result = new SignUpTable();
        foreach (var row in table.Rows)
        {
            int month = table.Int(row, "Month", -1);
            int count = table.Int(row, "SignUpCount", -1);
            if (month <= 0 || count <= 0)
                continue;
            result._awards[(month, count)] = ChapterTable.ParseTuples(table.Str(row, "Award"));
            if (count > result.MaxCount(month))
                result._maxCount[month] = count;
        }

        Log.Info($"[setting] {FileName} 载入 {result.Count} 条签到奖励" +
                 $"（覆盖 {result._maxCount.Count} 个月，最长 {(result._maxCount.Count > 0 ? result._maxCount.Values.Max() : 0)} 天）");
        return result;
    }
}
