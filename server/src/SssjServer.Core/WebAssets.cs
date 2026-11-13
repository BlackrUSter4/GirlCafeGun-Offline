using System.Reflection;

namespace SssjServer.Core;

/// <summary>
/// Static web pages that the client used to load from the publisher's web servers (now dead), served by
/// <see cref="BootstrapHttpServer"/> under <c>/web/</c>.
///
/// The files live in <c>SssjServer.Core/Web/</c> and are compiled into this assembly as embedded
/// resources (see the csproj), so the phone build needs no extra packaging step: they travel inside
/// the server DLL in <c>libsssjdata.so</c>.
///
/// Who asks for them: the client Lua, after <c>tools/patch_lua_offline_web.py</c> rewrote the page
/// URLs (function <c>OfflineWebUrl</c> in <c>Lib.lua</c>). Full list: <c>docs/07-ONLINE-RESOURCES.md</c>.
/// </summary>
public static class WebAssets
{
    private const string ResourcePrefix = "web/";

    /// <summary>URL path below <c>/web/</c> (forward slashes, lower case) → resource name.</summary>
    private static readonly Dictionary<string, string> Files = Index();

    private static Dictionary<string, string> Index()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in typeof(WebAssets).Assembly.GetManifestResourceNames())
        {
            // MSBuild keeps the build machine's separator in %(RecursiveDir): '\' on Windows, '/' elsewhere.
            string key = name.Replace('\\', '/');
            if (key.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
                map[key.Substring(ResourcePrefix.Length).ToLowerInvariant()] = name;
        }
        return map;
    }

    /// <summary>Paths below <c>/web/</c> that exist, for logs and tests.</summary>
    public static IReadOnlyCollection<string> Paths => Files.Keys;

    /// <summary>
    /// Resolves a request path such as <c>/web/notice/index.html</c> or <c>/web/notice/</c>.
    /// Returns false when the path is not under <c>/web/</c> or no such file is embedded.
    /// </summary>
    public static bool TryGet(string requestPath, out byte[] body, out string contentType)
    {
        body = [];
        contentType = "application/octet-stream";

        const string urlPrefix = "/web/";
        if (!requestPath.StartsWith(urlPrefix, StringComparison.Ordinal))
            return false;

        string rel = requestPath.Substring(urlPrefix.Length).ToLowerInvariant();
        if (rel.Length == 0 || rel.EndsWith('/'))
            rel += "index.html";
        if (!Files.TryGetValue(rel, out string? resource) &&
            !Files.TryGetValue(rel + "/index.html", out resource))
            return false;

        using Stream? s = typeof(WebAssets).Assembly.GetManifestResourceStream(resource);
        if (s is null)
            return false;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        body = ms.ToArray();
        contentType = ContentTypeOf(resource);
        return true;
    }

    private static string ContentTypeOf(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "application/javascript; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".txt" => "text/plain; charset=utf-8",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        _ => "application/octet-stream",
    };
}
