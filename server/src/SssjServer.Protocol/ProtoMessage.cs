namespace SssjServer.Protocol;

/// <summary>
/// 协议消息基类（protobuf2 语义：has_ 位 + 默认值不写）。
///
/// 占位实现：本类型的真实实现由 task A / SssjServer.Protocol agent 提供，
/// 这里只声明与设计契约一致的公开签名，便于 Core/App 先编译联调。
/// 方法体抛 <see cref="NotImplementedException"/>。
/// </summary>
public abstract class ProtoMessage
{
    public abstract void WriteTo(ref ProtoWriter w);
    public abstract void MergeFrom(ref ProtoReader r);
    public abstract int ComputeSize();

    /// <summary>ComputeSize + WriteTo，返回完整 protobuf 字节。</summary>
    public byte[] ToByteArray()
    {
        int size = ComputeSize();
        var buffer = new byte[size];
        var w = new ProtoWriter(buffer);
        WriteTo(ref w);
        System.Diagnostics.Debug.Assert(w.Position == size, "ProtoMessage.ToByteArray: 写入长度 != ComputeSize");
        return buffer;
    }

    public static T Parse<T>(ReadOnlySpan<byte> data) where T : ProtoMessage, new()
    {
        var msg = new T();
        var r = new ProtoReader(data);
        msg.MergeFrom(ref r);
        return msg;
    }
}
