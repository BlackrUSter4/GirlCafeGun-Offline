namespace SssjServer.Core;

/// <summary>
/// 一个游戏封包（16 字节头 + payload）。
/// 头格式（小端）：[0:4] pf，[4:8] 16+payloadLen，[8:12] serial，
/// [12] 压缩标志（!=0 = payload 为 zlib 压缩流），[13] 0x88 魔数，[14:16] 保留 0。
/// </summary>
public sealed class Packet
{
    public const int HeaderSize = 16;
    public const byte Magic = 0x88;

    public int Pf;
    public uint Serial;
    public bool Compressed;
    public byte[] Payload = [];

    public int TotalLength => HeaderSize + Payload.Length;
}
