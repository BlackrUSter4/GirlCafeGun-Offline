namespace SssjServer.Game;

/// <summary>
/// LuaCall 命令名表：int → (name, 中文注释)。数据在 <c>LuaCallNames.g.cs</c>（由
/// Tools/gen_luacall_names.py 从 docs/luacall-table.json 生成）。作用在于把未处理命令
/// 的日志从“裸 cmd 数字”升级成带名称与注释的可读行，方便决定下一步实现什么。
/// </summary>
internal static partial class LuaCallNames
{
    /// <summary>未处理 LuaCall 的命令日志行：`cmd=11 (Shop_ReqGoodsList 请求商品列表) param={...}`。</summary>
    public static string FormatLog(int cmd, string? paramJson)
    {
        var p = paramJson ?? "null";
        if (_map.TryGetValue(cmd, out var entry))
            return $"cmd={cmd} ({entry.Name} {entry.Comment}) param={p}";
        return $"cmd={cmd} (UnNamed) param={p}";
    }

    /// <summary>解析某 id 的 (名称, 中文注释)；未知返回 (null, null)。</summary>
    public static (string? Name, string? Comment) Resolve(int cmd)
        => _map.TryGetValue(cmd, out var entry) ? (entry.Name, entry.Comment) : (null, null);
}
