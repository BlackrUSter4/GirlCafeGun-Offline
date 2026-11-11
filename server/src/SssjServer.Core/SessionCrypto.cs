using System.IO.Compression;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;

namespace SssjServer.Core;

/// <summary>
/// 会话加密（还原自客户端 Game.Crypto + Game.Encryptor，AGENTS.md §2.3）。
///
/// 时序：连接时 Crypto == null；客户端收到 VerifyRsp.client_key 后
/// Game.Crypto.Init(clientKey) → Game.Encryptor.Init(UTF8(clientKey))，
/// 此后所有包体 3DES。服务端必须在 VerifyRsp 发出之后才启用（见 ClientSession.EnableCrypto）。
///
/// 3DES 参数：TripleDES / CBC / PKCS7，Key = UTF8(clientKey)[0..24)，
/// 初始 IV = UTF8(clientKey)[24..32)。clientKey 必须 ≥ 32 字节。
///
/// ★ CBC 链跨封包连续（实测确认，2026-08-19）
/// 客户端 Game.Encryptor.Init 只创建 **一个** 加密 transform 和 **一个** 解密 transform，
/// 之后每个包体都调这两个实例的 TransformFinalBlock。Unity 的 Mono 实现不会在
/// TransformFinalBlock 之后把 IV 复位，所以**整条会话在每个方向上是一条连续的 CBC 流**：
///
///     第 1 个包：IV = clientKey[24..32)
///     第 N 个包：IV = 第 N-1 个包密文的最后 8 字节
///
/// 两个方向各自独立计数。空包体客户端会原样返回（不加密），因此**空包不推进链**。
///
/// 取证：某次会话 client_key=A8A0090787E46849B998D16942933ED7，
/// 按「每包都用初始 IV」解密，只有第一个包（pf=1001 LoginReq）正确，后续全是垃圾；
/// 按链式 IV 解密则全部正确：
///     pf=1048 → 0885D388A1C89CA709                  （field1 varint = roleid）
///     pf=1022 → 0A074C756143616C6C120E7B22...       （"LuaCall" + {"sCmd":10002}）
///     pf=1008 → 0A0130                              （field1 string "0"）
///
/// 压缩两端不对称：
///   客户端→服务端：zlib（ZlibStream.CompressBuffer，带 0x78 头）→ 用 <see cref="Inflate"/> 解
///   服务端→客户端：恒不压缩（回包压缩标志保持 0）
/// </summary>
public sealed class SessionCrypto
{
    private const int BlockSize = 8;

    private readonly byte[] _key;
    private readonly byte[] _initialIv;
    private readonly object _sendLock = new();
    private readonly object _recvLock = new();

    /// <summary>出站方向的滚动 IV（上一个出站密文的最后 8 字节）。</summary>
    private byte[] _sendIv;

    /// <summary>入站方向的滚动 IV（上一个入站密文的最后 8 字节）。</summary>
    private byte[] _recvIv;

    public SessionCrypto(string clientKey)
    {
        var raw = Encoding.UTF8.GetBytes(clientKey);
        if (raw.Length < 32)
            throw new ArgumentException(
                $"client_key 至少 32 字节，实际 {raw.Length} 字节", nameof(clientKey));
        _key = raw[..24];
        _initialIv = raw[24..32];
        _sendIv = (byte[])_initialIv.Clone();
        _recvIv = (byte[])_initialIv.Clone();
    }

    /// <summary>当前出站 IV（诊断用）。</summary>
    public byte[] SendIv => (byte[])_sendIv.Clone();

    /// <summary>当前入站 IV（诊断用）。</summary>
    public byte[] RecvIv => (byte[])_recvIv.Clone();

    // 3DES / CBC / PKCS7，托管实现（BouncyCastle）。用 KeyParameter 而非 DesEdeParameters：
    // 后者会做弱密钥校验并对某些 client_key 抛异常，而客户端的 Mono 3DES 不校验 —— 必须与之对齐。
    private byte[] Crypt(bool forEncryption, byte[] iv, byte[] input)
    {
        var cipher = new PaddedBufferedBlockCipher(new CbcBlockCipher(new DesEdeEngine()), new Pkcs7Padding());
        cipher.Init(forEncryption, new ParametersWithIV(new KeyParameter(_key), iv));
        var output = new byte[cipher.GetOutputSize(input.Length)];
        int n = cipher.ProcessBytes(input, 0, input.Length, output, 0);
        n += cipher.DoFinal(output, n);
        return n == output.Length ? output : output[..n];
    }

    /// <summary>加密一个出站包体，并把出站 CBC 链推进到本次密文尾块。</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plain)
    {
        if (plain.Length == 0)
            return [];   // 空包体不加密、不推进链（与客户端 Crypto.Encode 一致）

        var input = plain.ToArray();
        lock (_sendLock)
        {
            var cipher = Crypt(forEncryption: true, _sendIv, input);
            _sendIv = TailBlock(cipher, _sendIv);
            return cipher;
        }
    }

    /// <summary>解密一个入站包体，并把入站 CBC 链推进到本次密文尾块。</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> cipher)
    {
        if (cipher.Length == 0)
            return [];

        var input = cipher.ToArray();
        lock (_recvLock)
        {
            // 先算出新 IV 再解密：解密失败也要推进链，否则整条会话都会错位
            var next = TailBlock(input, _recvIv);
            try
            {
                return Crypt(forEncryption: false, _recvIv, input);
            }
            finally
            {
                _recvIv = next;
            }
        }
    }

    private static byte[] TailBlock(byte[] cipher, byte[] fallback)
    {
        if (cipher.Length < BlockSize)
            return fallback;
        var tail = new byte[BlockSize];
        Array.Copy(cipher, cipher.Length - BlockSize, tail, 0, BlockSize);
        return tail;
    }

    /// <summary>客户端发来的压缩包体是 zlib（带 0x78 头）流。服务端回包一律不压缩。</summary>
    public static byte[] Inflate(ReadOnlySpan<byte> zlib)
    {
        using var input = new MemoryStream(zlib.ToArray());
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        inflate.CopyTo(output);
        return output.ToArray();
    }
}
