namespace SssjServer.Protocol;

/// <summary>
/// protobuf2 各字段编码长度的静态计算，供生成代码的 ComputeSize 使用。
/// </summary>
public static class SizeOf
{
    /// <summary>tag 的 varint 长度：(fieldNumber &lt;&lt; 3) | wireType 的 varint 长度。</summary>
    public static int Tag(int fieldNumber)
        => VarintLen((ulong)((fieldNumber << 3) | 0)); // wire type irrelevant for tag length (0..7 fits)

    private static int VarintLen(ulong v)
    {
        int n = 1;
        while (v >= 0x80)
        {
            v >>= 7;
            n++;
        }
        return n;
    }

    /// <summary>varint int32：负数恒为 10 字节（补码最高位 1 → 变长）。</summary>
    public static int Int32(int v)
        => VarintLen(unchecked((ulong)v));

    public static int UInt32(uint v)
        => VarintLen(v);

    /// <summary>varint int64：负数恒为 10 字节。</summary>
    public static int Int64(long v)
        => VarintLen(unchecked((ulong)v));

    public static int UInt64(ulong v)
        => VarintLen(v);

    /// <summary>zigzag 后的 varint 长度（sint32 用 32 位 zigzag → 恒大 ≤5 字节）。</summary>
    public static int SInt32(int v)
        => VarintLen(unchecked((ulong)(uint)((v << 1) ^ (v >> 31))));

    public static int SInt64(long v)
        => VarintLen(unchecked((ulong)((v << 1) ^ (v >> 63))));

    public static int String(string v)
    {
        int n = System.Text.Encoding.UTF8.GetByteCount(v);
        return VarintLen((ulong)n) + n;
    }

    public static int Bytes(byte[] v)
    {
        int n = v?.Length ?? 0;
        return VarintLen((ulong)n) + n;
    }

    /// <summary>长度前缀 + 消息体总长。</summary>
    public static int Message(ProtoMessage v)
    {
        int body = v?.ComputeSize() ?? 0;
        return VarintLen((ulong)body) + body;
    }

    public const int Fixed32 = 4;
    public const int Fixed64 = 8;
}
