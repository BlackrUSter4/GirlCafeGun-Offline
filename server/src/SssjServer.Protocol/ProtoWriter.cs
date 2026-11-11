namespace SssjServer.Protocol;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// protobuf2 编码器。写入目标 Span，Position 记录写入位置。
/// varint 为 little-endian base-128；int32/int64 负数按 10 字节补码形式输出（与 protobuf 一致）。
/// </summary>
public ref struct ProtoWriter
{
    private readonly Span<byte> _destination;
    private int _position;

    public ProtoWriter(Span<byte> destination)
    {
        _destination = destination;
        _position = 0;
    }

    /// <summary>当前写入位置（已写入字节数）。</summary>
    public int Position => _position;

    /// <summary>写入 tag：(fieldNumber &lt;&lt; 3) | wireType，按 varint。</summary>
    public void WriteTag(int fieldNumber, WireType type)
        => WriteUInt64((ulong)((fieldNumber << 3) | (int)type));

    public void WriteInt32(int value)
        => WriteUInt64(unchecked((ulong)value));

    public void WriteUInt32(uint value)
        => WriteUInt64(value);

    public void WriteInt64(long value)
        => WriteUInt64(unchecked((ulong)value));

    public void WriteUInt64(ulong value)
    {
        while (value >= 0x80)
        {
            _destination[_position++] = (byte)(value | 0x80);
            value >>= 7;
        }
        _destination[_position++] = (byte)value;
    }

    public void WriteSInt32(int value)
        => WriteUInt32(ZigZag32(value));

    public void WriteSInt64(long value)
        => WriteUInt64(ZigZag64(value));

    private static uint ZigZag32(int n) => unchecked((uint)((n << 1) ^ (n >> 31)));
    private static ulong ZigZag64(long n) => unchecked((ulong)((n << 1) ^ (n >> 63)));

    public void WriteBool(bool value)
        => WriteUInt64(value ? 1ul : 0ul);

    public void WriteFixed32(uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(_destination.Slice(_position, 4), value);
        _position += 4;
    }

    public void WriteSFixed32(int value)
        => WriteFixed32(unchecked((uint)value));

    public void WriteFixed64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_destination.Slice(_position, 8), value);
        _position += 8;
    }

    public void WriteSFixed64(long value)
        => WriteFixed64(unchecked((ulong)value));

    public void WriteFloat(float value)
        => WriteFixed32(BitConverter.SingleToUInt32Bits(value));

    public void WriteDouble(double value)
        => WriteFixed64(BitConverter.DoubleToUInt64Bits(value));

    public void WriteString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        WriteUInt64((ulong)utf8.Length);
        utf8.CopyTo(_destination.Slice(_position, utf8.Length));
        _position += utf8.Length;
    }

    public void WriteBytes(ReadOnlySpan<byte> value)
    {
        WriteUInt64((ulong)value.Length);
        value.CopyTo(_destination.Slice(_position, value.Length));
        _position += value.Length;
    }

    /// <summary>长度前缀 + 消息体。value 为 null 时写长度 0（空消息体）。</summary>
    public void WriteMessage(ProtoMessage value)
    {
        if (value is null)
        {
            WriteUInt64(0);
            return;
        }
        int size = value.ComputeSize();
        WriteUInt64((ulong)size);
        value.WriteTo(ref this);
    }
}
