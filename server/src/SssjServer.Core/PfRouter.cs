namespace SssjServer.Core;

/// <summary>pf 处理委托。</summary>
public delegate Task PacketHandler(ClientSession session, Packet packet);

/// <summary>
/// 按 pf 分发的路由表。未注册的 pf 交给 MapDefault 注册的兜底（打日志）。
/// </summary>
public sealed class PfRouter
{
    private readonly Dictionary<int, PacketHandler> _map = new();
    private PacketHandler? _default;

    public void Map(int pf, PacketHandler handler)
    {
        _map[pf] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void MapDefault(PacketHandler handler)
    {
        _default = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public Task DispatchAsync(ClientSession session, Packet packet)
    {
        if (_map.TryGetValue(packet.Pf, out var handler))
            return handler(session, packet);

        if (_default != null)
            return _default(session, packet);

        LogUnhandled(session, packet);
        return Task.CompletedTask;
    }

    /// <summary>默认兜底行为：日志未处理 pf + payload 前 64 字节 hex。</summary>
    internal static void LogUnhandled(ClientSession session, Packet packet)
    {
        var hex = Log.Hex(packet.Payload, 64);
        Log.Warn($"[session {session.Id}] unhandled pf={packet.Pf} len={packet.Payload.Length} hex={hex}");
    }
}
