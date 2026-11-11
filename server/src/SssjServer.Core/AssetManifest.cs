using System.Buffers.Binary;

namespace SssjServer.Core;

/// <summary>
/// 读 <c>{AssetDirectory}/version.data</c> 顶部的 <c>buildVersion</c>，让服务端下发的
/// <c>checkupdate.urllist.buildversion</c> **永远跟着清单走**。
///
/// <para><b>★★★ 为什么必须有这个东西（热更新的第一号陷阱）</b></para>
/// 热更新的机制是「<c>buildVersion</c> 变大 + 某个包的 <c>hash</c> 变了 ⇒ 客户端重下那个包」。
/// 而在这之前，服务端下发的版本号是**命令行手写死的**（<c>--asset-build-version 1366</c>）：
/// 用 <c>tools/hotfix.py</c> 把清单 bump 到 1367 之后如果忘了同步这个参数，
/// 客户端拿到的还是 1366 ⇒ <b>它认为没有更新、什么都不下</b>，而且
/// <b>两端日志都完全干净</b>（服务端正常回包、客户端正常进游戏）——
/// 属于最难查的一类「静默失效」。
///
/// <para>⇒ 用 <c>--asset-build-version auto</c> 时改由本类每次现读清单，
/// 二者**在结构上不可能不一致**，且发布新补丁**不需要重启服务端**。</para>
///
/// <para><b>格式</b>（<c>tools/verdata.py</c> 已 round-trip 字节级验证，
/// 客户端入口 <c>Game.VersionControl.InitFrom(Stream)</c> RVA <c>0x19071E8</c>）：</para>
/// <code>
/// offset 0x00  int32  version        客户端 versionCode（10136）
/// offset 0x04  int32  buildVersion   ← 本类只读这 4 个字节
/// </code>
/// 无 magic / 无压缩 / 无加密 ⇒ 读前 8 字节即可，不必解析全部 4650 条 abinfo。
///
/// <para>★ 带 500ms 缓存：<c>checkupdate</c> 每次启动只来一次，但
/// <c>/Bundles/version.data</c> 与它相邻，避免同一次更新里反复 IO。
/// 缓存以文件的「大小 + 最后写入时间」为键 ⇒ <c>hotfix.py</c> 一改清单立刻生效。</para>
/// </summary>
public sealed class AssetManifest
{
    private readonly string _assetDirectory;
    private readonly object _gate = new();
    private long _cachedLength = -1;
    private DateTime _cachedWriteUtc;
    private int _cachedBuildVersion;

    public AssetManifest(string assetDirectory)
    {
        _assetDirectory = assetDirectory;
    }

    /// <summary>清单文件路径（可能不存在）。</summary>
    public string Path => System.IO.Path.Combine(_assetDirectory, "version.data");

    /// <summary>
    /// 读清单里的 <c>buildVersion</c>；读不到（文件缺失/太短/IO 失败）返回 0。
    /// 返回 0 会让 <see cref="BootstrapHttpServer"/> 走「跳过资源更新」分支
    /// —— 与「没配资源目录」等价，是安全的降级。
    /// </summary>
    public int ReadBuildVersion()
    {
        try
        {
            var fi = new FileInfo(Path);
            if (!fi.Exists || fi.Length < 8)
            {
                Log.Warn($"[asset] 清单不可用：{Path}（存在={fi.Exists} 长度={(fi.Exists ? fi.Length : 0)}）"
                         + " ⇒ 本次不下发资源更新");
                return 0;
            }

            lock (_gate)
            {
                if (fi.Length == _cachedLength && fi.LastWriteTimeUtc == _cachedWriteUtc)
                    return _cachedBuildVersion;

                Span<byte> head = stackalloc byte[8];
                using (var fs = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int read = 0;
                    while (read < 8)
                    {
                        int n = fs.Read(head[read..]);
                        if (n <= 0)
                            break;
                        read += n;
                    }
                    if (read < 8)
                        return 0;
                }

                int version = BinaryPrimitives.ReadInt32LittleEndian(head[..4]);
                int build = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);

                if (build != _cachedBuildVersion)
                    Log.Info($"[asset] 清单 buildVersion = {build}（version={version}，{Path}）");

                _cachedLength = fi.Length;
                _cachedWriteUtc = fi.LastWriteTimeUtc;
                _cachedBuildVersion = build;
                return build;
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[asset] 读清单失败 {Path}：{e.Message} ⇒ 本次不下发资源更新");
            return 0;
        }
    }
}
