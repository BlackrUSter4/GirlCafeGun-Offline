using System.Net.Sockets;
using SssjServer.Protocol;

namespace SssjServer.Core;

/// <summary>
/// 客户端 TCP 会话：收包 → 解密/解压 → 分发；发包 → 加密 → 写出。
///
/// 加密时序（AGENTS.md §2.3）：连接时 Crypto == null，pf=1102 Verify 一来一回为明文；
/// 客户端收到 VerifyRsp.client_key 后启用 3DES。服务端必须在 VerifyRsp 发出之后
/// 才调用 <see cref="EnableCrypto"/>，此后所有进出包体都 3DES；入库如有压缩标志则先解密再 zlib 解压，
/// 出库恒不压缩（压缩标志保持 0）。
///
/// 缓冲用一块可增长的 byte[]（_buf + _len）：读循环把收到的字节累积进来，
/// 排空时直接在这块缓冲区上遍历解出尽量多个完整封包，最后一次性 compact，
/// 避免按包反复 ToArray/RemoveRange 的 O(n²)。
///
/// 读循环由调用方（GameGateway）驱动 <see cref="RunAsync"/>。
/// </summary>
public sealed class ClientSession : IDisposable
{
    private const int InitialBufferSize = 0x10000;

    private readonly TcpClient _tcp;
    private readonly NetworkStream _stream;
    private byte[] _buf = new byte[InitialBufferSize];
    private int _len;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _closed;

    public ClientSession(int id, TcpClient tcp)
    {
        Id = id;
        _tcp = tcp;
        _stream = tcp.GetStream();
        RemoteEndPoint = tcp.Client.RemoteEndPoint?.ToString() ?? "?";
    }

    public int Id { get; }
    public string RemoteEndPoint { get; }
    public SessionCrypto? Crypto { get; private set; }

    /// <summary>Game 层挂业务对象（如 Player）。</summary>
    public object? Tag { get; set; }

    /// <summary>会话是否已关闭（测试断言用）。</summary>
    internal bool IsClosed => _closed != 0;

    /// <summary>Verify 响应（VerifyRsp.client_key）发出之后才能调用。</summary>
    public void EnableCrypto(string clientKey)
    {
        Crypto = new SessionCrypto(clientKey);
        Log.Info($"[session {Id}] 启用 3DES（client_key={clientKey}）");
    }

    /// <summary>读取循环：读到数据就累积并排空封包，每个封包加密/解密后交给 dispatch。</summary>
    public async Task RunAsync(Func<ClientSession, Packet, Task> dispatch, CancellationToken ct)
    {
        var recvBuf = new byte[InitialBufferSize];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await _stream.ReadAsync(recvBuf, ct);
                if (n <= 0)
                    break;
                Feed(recvBuf.AsSpan(0, n));
                await DrainAsync(dispatch);
            }
        }
        catch (IOException)
        {
            // 对端断开 / 连接重置
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Close("read-loop end");
        }
    }

    /// <summary>
    /// 测试/内部用：直接喂入一批字节并排空（不经网络读）。dispatch 与 RunAsync 一致。
    /// 不会抛出协议解析异常 —— 失步会让会话关闭（见 DrainAsync）。
    /// 注意：本方法非 async（span 不能跨 await），只在内部同步拷贝后返回 DrainAsync 的 Task。
    /// </summary>
    internal Task FeedAsync(Func<ClientSession, Packet, Task> dispatch, ReadOnlySpan<byte> data)
    {
        Feed(data);
        return DrainAsync(dispatch);
    }

    /// <summary>把新收到的字节追加进缓冲。</summary>
    private void Feed(ReadOnlySpan<byte> data)
    {
        EnsureCapacity(_len + data.Length);
        data.CopyTo(_buf.AsSpan(_len));
        _len += data.Length;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buf.Length)
            return;
        int newCap = _buf.Length;
        while (newCap < needed)
            newCap *= 2;
        if (newCap < InitialBufferSize)
            newCap = InitialBufferSize;
        Array.Resize(ref _buf, newCap);
    }

    /// <summary>
    /// 排空当前已完整缓冲的封包：直接在 _buf 上按偏移遍历，逐个解密→(zlib)解压→分发，
    /// 结束后一次性 compact 剩余字节。
    /// TryDecode 返回 false = 数据不足，等更多字节；TryDecode 抛 InvalidDataException =
    /// 协议失步（魔数/总长错误），不可恢复 —— 记日志并关闭会话，不尝试重新同步。
    /// </summary>
    private async Task DrainAsync(Func<ClientSession, Packet, Task> dispatch)
    {
        int offset = 0;
        while (true)
        {
            var span = _buf.AsSpan(offset, _len - offset);
            bool ok;
            int consumed;
            Packet? packet;
            try
            {
                ok = PacketCodec.TryDecode(span, out packet, out consumed);
            }
            catch (InvalidDataException e)
            {
                var head = Log.Hex(span[..Math.Min(32, span.Length)]);
                Log.Error($"[session {Id}] 协议失步：{e.Message} buf={head}");
                Close("protocol desync");
                return;
            }

            if (!ok)
                break; // 数据不足，等更多字节

            offset += consumed;

            // packet.Payload 是 TryDecode 内的独立副本，后续 compact 不会影响它。
            ProcessInbound(packet!);

            try
            {
                await dispatch(this, packet!);
            }
            catch (Exception e)
            {
                Log.Error($"[session {Id}] pf={packet!.Pf} 处理异常: {e.Message}");
            }
        }

        if (offset > 0)
        {
            int remaining = _len - offset;
            if (remaining > 0)
                Buffer.BlockCopy(_buf, offset, _buf, 0, remaining);
            _len = remaining;
        }
    }

    /// <summary>入库包体：启用加密后先 3DES 解密；压缩标志置位则再 zlib 解压。</summary>
    private void ProcessInbound(Packet packet)
    {
        var crypto = Crypto;
        bool encrypted = crypto != null && packet.Payload.Length > 0;
        bool compressed = packet.Compressed;

        // 诊断开关 SSSJ_WIRE_HEX=1：打印每个入库包的原始密文，
        // 便于离线复算加密参数（例如判断客户端是否跨包续用 CBC 链）。
        bool wireHex = Environment.GetEnvironmentVariable("SSSJ_WIRE_HEX") == "1";
        if (encrypted && wireHex)
        {
            Log.Info($"[session {Id}] RAW pf={packet.Pf} serial={packet.Serial} " +
                     $"cipher={Log.Hex(packet.Payload, 512)}");
        }

        if (encrypted)
        {
            try
            {
                packet.Payload = crypto!.Decrypt(packet.Payload);
                if (packet.Compressed)
                    packet.Payload = SessionCrypto.Inflate(packet.Payload);
            }
            catch (Exception e)
            {
                Log.Error($"[session {Id}] pf={packet.Pf} 解密失败: {e.Message} " +
                          $"cipherLen={packet.Payload.Length} cipher={Log.Hex(packet.Payload, 512)}");
                packet.Payload = [];
            }
        }
        else if (wireHex && packet.Payload.Length > 0)
        {
            Log.Info($"[session {Id}] RAW pf={packet.Pf} serial={packet.Serial} " +
                     $"plain={Log.Hex(packet.Payload, 512)}");
        }

        Log.Info($"[session {Id}] <= pf={packet.Pf} serial={packet.Serial} " +
                 $"len={packet.Payload.Length} enc={encrypted} comp={compressed}");
    }

    /// <summary>发送 protobuf 消息（pf + serial 回包，按序列回调匹配）。</summary>
    public void Send(int pf, uint serial, ProtoMessage message)
    {
        SendRaw(pf, serial, message.ToByteArray());
    }

    /// <summary>服务端主动推送（serial = 0）。</summary>
    public void Push(int pf, ProtoMessage message)
    {
        PushRaw(pf, message.ToByteArray());
    }

    /// <summary>发送原始 payload（自动加密 + 封包化）。LuaCall/透传用。</summary>
    public void SendRaw(int pf, uint serial, ReadOnlySpan<byte> payload)
    {
        WriteOutbound(pf, serial, payload);
    }

    /// <summary>推送原始 payload（serial = 0）。LuaCall/透传用。</summary>
    public void PushRaw(int pf, ReadOnlySpan<byte> payload)
    {
        WriteOutbound(pf, serial: 0, payload);
    }

    private void WriteOutbound(int pf, uint serial, ReadOnlySpan<byte> payload)
    {
        var crypto = Crypto;
        bool encrypted = crypto != null && payload.Length > 0;

        byte[] body = encrypted ? crypto!.Encrypt(payload) : payload.ToArray();
        var packet = PacketCodec.Encode(pf, serial, body, compressed: false);

        Log.Info($"[session {Id}] => pf={pf} serial={serial} " +
                 $"len={payload.Length} enc={encrypted} comp=false");

        try
        {
            _writeLock.Wait();
            try
            {
                _stream.Write(packet, 0, packet.Length);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch (IOException)
        {
            Log.Warn($"[session {Id}] 发送失败（连接已断开）pf={pf}");
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>关闭连接。</summary>
    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
            return;
        Log.Info($"[session {Id}] 关闭：{reason}");
        _writeLock.Dispose();
        try { _tcp.Dispose(); } catch { /* ignore */ }
    }

    public void Dispose() => Close("dispose");
}
