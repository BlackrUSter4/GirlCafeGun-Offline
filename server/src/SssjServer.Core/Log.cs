namespace SssjServer.Core;

/// <summary>
/// 极简静态控制台日志：四级（Debug/Info/Warn/Error），带时间戳，无外部依赖。
/// 级别可通过 <see cref="MinLevel"/> 调整（默认 Info）。
/// </summary>
public static class Log
{
    public enum Level
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
    }

    /// <summary>低于该级别的日志不输出。默认 Info。</summary>
    public static Level MinLevel { get; set; } = Level.Info;

    private static readonly object Sync = new();

    private static void Write(Level level, string message)
    {
        if (level < MinLevel)
            return;
        var ts = DateTime.Now.ToString("HH:mm:ss.fff");
        var tag = level switch
        {
            Level.Debug => "DBG",
            Level.Warn => "WRN",
            Level.Error => "ERR",
            _ => "INF",
        };
        lock (Sync)
        {
            Console.WriteLine($"[{ts}] [{tag}] {message}");
        }
    }

    public static void Debug(string message) => Write(Level.Debug, message);
    public static void Info(string message) => Write(Level.Info, message);
    public static void Warn(string message) => Write(Level.Warn, message);
    public static void Error(string message) => Write(Level.Error, message);

    /// <summary>把字节转成十六进制（可选截断，用于日志）。</summary>
    public static string Hex(ReadOnlySpan<byte> data, int max = int.MaxValue)
    {
        if (data.Length == 0)
            return "(empty)";
        var len = Math.Min(data.Length, max);
        return Convert.ToHexString(data[..len]);
    }
}
