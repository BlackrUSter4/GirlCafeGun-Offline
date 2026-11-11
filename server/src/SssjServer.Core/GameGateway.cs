using System.Net;
using System.Net.Sockets;

namespace SssjServer.Core;

/// <summary>
/// TCP 游戏网关：监听 BindAddress:GatePort，接收连接，
/// 为每个连接建 <see cref="ClientSession"/>，读循环把解码后的封包交给 <see cref="PfRouter"/> 分发。
/// </summary>
public sealed class GameGateway
{
    private readonly ServerOptions _options;
    private readonly PfRouter _router;
    private int _nextSessionId;

    public GameGateway(ServerOptions options, PfRouter router)
    {
        _options = options;
        _router = router;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var bind = _options.BindAddress == "0.0.0.0" || string.IsNullOrEmpty(_options.BindAddress)
            ? IPAddress.Any
            : IPAddress.Parse(_options.BindAddress);

        var listener = new TcpListener(bind, _options.GatePort);
        listener.Start();
        Log.Info($"[gateway] 监听 {_options.BindAddress}:{_options.GatePort}（下发客户地址 {_options.GateHost}:{_options.GatePort}）");

        var sessionTasks = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient tcp;
                try
                {
                    tcp = await listener.AcceptTcpClientAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (SocketException)
                {
                    continue;
                }

                var session = new ClientSession(++_nextSessionId, tcp);
                Log.Info($"[gateway] 新连接 id={session.Id} {session.RemoteEndPoint}");
                sessionTasks.Add(Task.Run(
                    () => RunSessionAsync(session, ct),
                    CancellationToken.None));
            }
        }
        finally
        {
            listener.Stop();
        }

        // 等所有在途会话收尾（关闭 socket）。
        try
        {
            await Task.WhenAll(sessionTasks);
        }
        catch
        {
            // 会话任务自身兜底，不往上抛
        }
    }

    private async Task RunSessionAsync(ClientSession session, CancellationToken ct)
    {
        try
        {
            await session.RunAsync(_router.DispatchAsync, ct);
        }
        finally
        {
            session.Close("session-end");
            Log.Info($"[gateway] 连接断开 id={session.Id} {session.RemoteEndPoint}");
        }
    }
}
