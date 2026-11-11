namespace SssjServer.Core;

/// <summary>
/// 封包编解码器（还原自客户端 UNet.Net 的 doSend/解析循环，AGENTS.md §2.3）。
/// 头 16 字节小端：[0:4] pf，[4:8] 16+payloadLen，[8:12] serial，
/// [12] 压缩标志，[13] 0x88 魔数，[14:16] 保留 0。
/// </summary>
public static class PacketCodec
{
    /// <summary>
    /// 尝试从缓冲解码一个封包。
    /// 返回 false = 数据不足（<16 字节，或不足声明总长），此时 <paramref name="consumed"/> 为 0，
    /// 调用方应继续等更多字节。
    /// 魔数错误或总长 &lt; 16 抛 <see cref="InvalidDataException"/>。
    /// </summary>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out Packet packet, out int consumed)
    {
        if (buffer.Length < Packet.HeaderSize)
        {
            packet = null!;
            consumed = 0;
            return false;
        }

        int pf = ReadInt32(buffer, 0);
        int total = ReadInt32(buffer, 4);
        uint serial = (uint)ReadInt32(buffer, 8);
        bool compressed = buffer[12] != 0;
        byte magic = buffer[13];

        if (magic != Packet.Magic)
            throw new InvalidDataException(
                $"封包魔数错误：期望 0x{Packet.Magic:X2}，实际 0x{magic:X2}（pf={pf}）");

        if (total < Packet.HeaderSize)
            throw new InvalidDataException($"封包总长非法：{total} < {Packet.HeaderSize}");

        if (buffer.Length < total)
        {
            packet = null!;
            consumed = 0;
            return false; // 声明总长尚未收齐
        }

        int payloadLen = total - Packet.HeaderSize;
        var payload = new byte[payloadLen];
        buffer.Slice(Packet.HeaderSize, payloadLen).CopyTo(payload);

        packet = new Packet
        {
            Pf = pf,
            Serial = serial,
            Compressed = compressed,
            Payload = payload,
        };
        consumed = total;
        return true;
    }

    /// <summary>
    /// 编码一个封包。压缩标志按 <paramref name="compressed"/>；outbound 恒为不压缩。
    /// </summary>
    /// <param name="code">
    /// ★ 头部 <c>[14:16]</c> 的 <b>ushort code（错误码）</b>，**不是**「保留 0」。
    ///
    /// 取证 <c>UNet.Net.Dispatch</c>（RVA <c>0x1B4DAF8</c>，dump.cs:439195）的读头顺序：
    /// <code>
    /// 0x1B4DC2C ReadInt32  → pf
    /// 0x1B4DC5C ReadInt32  → 总长
    /// 0x1B4DC8C ReadInt32  → serial
    /// 0x1B4DCB8 ReadByte   → 压缩标志
    /// 0x1B4DCE4 ReadByte   → 0x88 MAGIC（丢弃）
    /// 0x1B4DD10 ReadUInt16 → w24   ★ code
    /// 0x1B4DE88 mov w1, w24 ; 0x1B4DE90 blr x9   → handler.Invoke(code, stream)
    /// 0x1B4DF48 mov w1, w24                      → serial 回调同样收到它
    /// </code>
    /// 也就是说 <c>Net.Register&lt;T&gt;</c> 的签名 <c>Action&lt;ushort, T&gt;</c>（dump.cs:439075）
    /// 里那个 <c>ushort</c> 就是它。**多数 handler 第一句就是 <c>if (code != 0) 弹 ui.Error.&lt;code&gt; 并 return</c>**，
    /// 例如邮件的 5 个 handler（<c>LoadMailInfo 0x1877600</c> / <c>AddMailInfo 0x1877988</c> /
    /// <c>GetMailItem 0x1877CC4</c> / <c>SetMailRead 0x18786D4</c> / <c>DelMail 0x1878BF4</c>）、
    /// 背包的 <c>OnUpdateItem 0x1D9D7B8</c> 与 <c>ItemNtfMsg 0x1C8CC30</c>。
    ///
    /// 所以 <b>0 = 成功</b>（缺省值，正常业务包一律用它），
    /// 想让客户端直接弹错误提示时把错误码写进来即可 —— 这是「不改协议体也能报错」的通道。
    /// </param>
    public static byte[] Encode(int pf, uint serial, ReadOnlySpan<byte> payload,
                                bool compressed = false, ushort code = 0)
    {
        var buf = new byte[Packet.HeaderSize + payload.Length];
        WriteInt32(buf, 0, pf);
        WriteInt32(buf, 4, Packet.HeaderSize + payload.Length);
        WriteInt32(buf, 8, (int)serial);
        buf[12] = compressed ? (byte)1 : (byte)0;
        buf[13] = Packet.Magic;
        buf[14] = (byte)(code & 0xFF);
        buf[15] = (byte)(code >> 8);
        payload.CopyTo(buf.AsSpan(Packet.HeaderSize));
        return buf;
    }

    private static int ReadInt32(ReadOnlySpan<byte> buf, int off)
        => buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);

    private static void WriteInt32(byte[] buf, int off, int v)
    {
        buf[off] = (byte)v;
        buf[off + 1] = (byte)(v >> 8);
        buf[off + 2] = (byte)(v >> 16);
        buf[off + 3] = (byte)(v >> 24);
    }
}
