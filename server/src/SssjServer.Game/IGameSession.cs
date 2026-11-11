using SssjServer.Protocol;

namespace SssjServer.Game;

/// <summary>
/// Game 层对“一个客户端会话”的薄抽象。
///
/// Core 的 <see cref="Core.ClientSession"/> 是 sealed 且内建加密/封包/网络，难以在单测里替换，
/// 故 Game 层不直接依赖它。Core 处理函数收到真实 ClientSession 后，由
/// <see cref="SessionAdapter"/> 转成本接口；测试用 <see cref="Testing.FakeSession"/> 替代。
///
/// 语义与 Core.ClientSession 对应：Send = 带 serial 的回包；Push = 服务端主动推送(serial=0)；
/// EnableCrypto 必须在 VerifyRsp 发出之后调用。Tag 挂玩家对象。
/// </summary>
public interface IGameSession
{
    int Id { get; }
    object? Tag { get; set; }

    /// <summary>必须在 VerifyRsp(client_key) 发出之后调用。</summary>
    void EnableCrypto(string clientKey);

    /// <summary>带 serial 的应答（按请求 serial 匹配回调）。</summary>
    void Send(int pf, uint serial, ProtoMessage message);

    /// <summary>服务端主动推送（serial = 0）。</summary>
    void Push(int pf, ProtoMessage message);
}

/// <summary>
/// 把 Core 的 <see cref="Core.ClientSession"/> 适配成 <see cref="IGameSession"/>。
/// 仅做成员转发，不改动 Core。
/// </summary>
public sealed class SessionAdapter : IGameSession
{
    private readonly Core.ClientSession _inner;

    public SessionAdapter(Core.ClientSession inner) => _inner = inner;

    /// <summary>被适配的真实会话（LuaCallRouter.DispatchAsync 需要范化的 ClientSession 时用）。</summary>
    public Core.ClientSession Inner => _inner;

    public int Id => _inner.Id;
    public object? Tag { get => _inner.Tag; set => _inner.Tag = value; }
    public void EnableCrypto(string clientKey) => _inner.EnableCrypto(clientKey);
    public void Send(int pf, uint serial, ProtoMessage message) => _inner.Send(pf, serial, message);
    public void Push(int pf, ProtoMessage message) => _inner.Push(pf, message);
}
