namespace SssjServer.Protocol;

using System.Buffers.Binary;
using System.Text;

/// <summary>
/// protobuf2 解码器。读取只读源 Span，按读取顺序推进内部位置。
/// </summary>
public ref struct ProtoReader
{
    private readonly ReadOnlySpan<byte> _source;
    private int _position;

    public ProtoReader(ReadOnlySpan<byte> source)
    {
        _source = source;
        _position = 0;
    }

    /// <summary>是否已到达输入末尾。</summary>
    public bool End => _position >= _source.Length;

    /// <summary>
    /// 尝试读取一个字段 tag。到输入末尾返回 false。tag 为 (fieldNumber &lt;&lt; 3) | wireType 的 varint。
    /// </summary>
    public bool ReadTag(out int fieldNumber, out WireType type)
    {
        if (End)
        {
            fieldNumber = 0;
            type = WireType.Varint;
            return false;
        }
        ulong tag = ReadUInt64();
        fieldNumber = (int)(tag >> 3);
        type = (WireType)(tag & 0x7);
        return true;
    }

    public int ReadInt32()
        => unchecked((int)ReadUInt64());

    public uint ReadUInt32()
        => unchecked((uint)ReadUInt64());

    public long ReadInt64()
        => unchecked((long)ReadUInt64());

    public ulong ReadUInt64()
    {
        ulong result = 0;
        int shift = 0;
        while (true)
        {
            if (_position >= _source.Length)
                throw new System.IO.EndOfStreamException("proto varint 到达输入末尾");
            byte b = _source[_position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
            if (shift >= 64)
                throw new System.IO.InvalidDataException("proto varint 过长");
        }
        return result;
    }

    public int ReadSInt32()
        => UnZigZag32(ReadUInt32());

    public long ReadSInt64()
        => UnZigZag64(ReadUInt64());

    private static int UnZigZag32(uint n) => unchecked((int)(n >> 1) ^ -(int)(n & 1));
    private static long UnZigZag64(ulong n) => unchecked((long)(n >> 1) ^ -(long)(n & 1));

    public bool ReadBool()
        => ReadUInt64() != 0;

    public uint ReadFixed32()
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_source.Slice(_position, 4));
        _position += 4;
        return v;
    }

    public int ReadSFixed32()
        => unchecked((int)ReadFixed32());

    public ulong ReadFixed64()
    {
        ulong v = BinaryPrimitives.ReadUInt64LittleEndian(_source.Slice(_position, 8));
        _position += 8;
        return v;
    }

    public long ReadSFixed64()
        => unchecked((long)ReadFixed64());

    public float ReadFloat()
        => BitConverter.UInt32BitsToSingle(ReadFixed32());

    public double ReadDouble()
        => BitConverter.UInt64BitsToDouble(ReadFixed64());

    public string ReadString()
    {
        int len = checked((int)ReadUInt64());
        var slice = _source.Slice(_position, len);
        _position += len;
        return Encoding.UTF8.GetString(slice);
    }

    public byte[] ReadBytes()
    {
        int len = checked((int)ReadUInt64());
        var slice = _source.Slice(_position, len);
        _position += len;
        return slice.ToArray();
    }

    /// <summary>读取长度前缀，再把恰好该长度的字节 merge 进 target。</summary>
    public void ReadMessage(ProtoMessage target)
    {
        int len = checked((int)ReadUInt64());
        var sub = new ProtoReader(_source.Slice(_position, len));
        _position += len;
        target.MergeFrom(ref sub);
    }

    /// <summary>跳过未知字段。四种 wire type 都必须能正确跳过。</summary>
    public void SkipField(WireType type)
    {
        switch (type)
        {
            case WireType.Varint:
                ReadUInt64();
                break;
            case WireType.Fixed64:
                _position += 8;
                break;
            case WireType.Length:
            {
                int len = checked((int)ReadUInt64());
                _position += len;
                break;
            }
            case WireType.Fixed32:
                _position += 4;
                break;
            default:
                throw new System.IO.InvalidDataException($"未知 wire type {(int)type}");
        }
        if (_position > _source.Length)
            throw new System.IO.InvalidDataException("proto 读取越过输入末尾");
    }
}
