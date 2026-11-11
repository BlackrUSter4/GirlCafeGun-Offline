using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 「本地资源快照里缺失的道具图标」清单（<c>extracted/setting/MissingIcon.txt</c>）。
///
/// <para><b>0. 这解决的是什么现象</b></para>
/// 仓库/奖励弹窗里**部分道具图标显示成灰底问号**（少数是空白）。
/// 这不是服务端 bug —— 问号就是客户端自己的兜底图：
/// <code>
/// -- extracted/lua/Lib.lua:470..476
/// function Sprite(sPath)
///     local img = Game.LuaUtils.LoadSprite(sPath);
///     -- 如果加载失败就使用默认图片
///     return img or Game.LuaUtils.LoadSprite('Num/what.png');   -- ← 这张就是问号
/// end
/// </code>
/// 即 <c>LoadSprite(IconId) == nil</c> ⇒ 画 <c>Num/what.png</c>。
///
/// <para><b>1. 根因：配置表版本 &gt; 本地资源快照版本</b></para>
/// <list type="bullet">
///   <item><c>ItemList.txt</c> 来自 **APK 的 setting.u**（客户端 10136，配置是最新的，
///     含全部后期活动道具）；</item>
///   <item>图集（<c>SpriteAtlas</c>）资源来自**本地备份的 CDN 快照**（资源 buildVersion 1365）；</item>
///   <item>CDN（<c>l{1,2,3}-prod-patch-sssj.bilibiligame.net</c>）已随游戏停服下线，差集补不回来。</item>
/// </list>
/// 客户端图集名的推导是 <c>Game.SpriteAtlasMgr.GetAtlasName</c>（RVA <c>0x1A1AEEC</c>）：
/// 按 <c>/</c> Split 取**首段**，所以 <c>Item/icon_xxx.png</c> → 图集 <c>Item</c>。
/// 于是「同一个图集里有的图标正常、有的问号」是正常现象 —— 图集本身加载成功，只是缺那几张 sprite。
///
/// <para><b>2. 实测规模（<c>tools/probe_icons.sh</c> 产出）</b></para>
/// <c>ItemList.txt</c> 2120 条 / 去重 IconId 1349 个 / 空 IconId 1 条 /
/// **缺失 125 个图标，影响 136 条道具（6.4%）**。
/// 缺失内容全部是后期版本：塔活动 <c>tower11..20</c>(23) / 少女皮肤箱 <c>girlskinbox01..22</c>(22) /
/// 外景 DIY(21) / 宠物 <c>pet_*</c>(8) / 礼包(7) / 武器零件(6) / 周年(1) / 其它(37)。
///
/// <para><b>3. 判据不是猜的</b></para>
/// <c>tools/probe_icons.sh</c> 让**客户端自己回答**：遍历
/// <c>Game.FileItemBaseInfoReader.LoadFile()</c> 的每个 <c>IconId</c> 调
/// <c>Game.LuaUtils.LoadSprite</c>，返回 nil 的才记进表 —— 与游戏实际渲染同一条代码路径。
/// 换客户端版本或重灌资源（<c>tools/push_assets.sh</c>）后需重跑该脚本。
///
/// <para><b>4. 服务端拿它做什么</b></para>
/// <see cref="GmUnlock.ApplyItems"/> 在 <see cref="ServerOptions.GmSkipMissingIcon"/> 打开时
/// **跳过**这些道具，于是 GM 全解锁后的仓库里不会出现问号格子。
/// 这些道具对应的功能（塔活动/宠物/DIY/皮肤）本来也都没实装，不发不影响任何链路。
/// 关掉该开关即可拿回「真·全道具」（代价就是那 136 个问号格子）。
/// </summary>
public sealed class MissingIconTable
{
    /// <summary>表文件名（本仓库自造，客户端无此文件）。</summary>
    public const string FileName = "MissingIcon.txt";

    private readonly HashSet<string> _icons = new(StringComparer.Ordinal);

    private MissingIconTable() { }

    /// <summary>永久空表（缺表降级：不跳过任何道具）。</summary>
    public static MissingIconTable Empty { get; } = new();

    /// <summary>缺失图标个数。</summary>
    public int Count => _icons.Count;

    /// <summary>全部缺失图标路径。</summary>
    public IEnumerable<string> All => _icons;

    /// <summary>该图标路径是否在缺失清单里（大小写敏感，与配置表原文一致）。</summary>
    public bool IsMissing(string? iconId)
        => !string.IsNullOrEmpty(iconId) && _icons.Contains(iconId);

    /// <summary>从 setting 目录加载。缺表返回空表并打 INFO（这张表是可选的，缺了只是不跳过）。</summary>
    public static MissingIconTable Load(string settingDir)
    {
        var table = new MissingIconTable();
        string path = Path.Combine(settingDir, FileName);
        if (!File.Exists(path))
        {
            Log.Info($"[setting] {FileName} 不存在（可选表，用 tools/probe_icons.sh 生成）：" +
                     "GM 全解锁不会跳过图标缺失的道具，仓库里会出现问号格子");
            return table;
        }

        var raw = SettingTable.Load(path);
        if (raw is null)
            return table;

        foreach (var row in raw.Rows)
        {
            var icon = raw.Str(row, "IconId");
            if (icon.Length > 0)
                table._icons.Add(icon);
        }

        Log.Info($"[setting] {FileName} 载入 {table._icons.Count} 个「本地资源快照缺失」的图标" +
                 "（引用它们的道具在 GM 全解锁时会被跳过，避免仓库出现问号）");
        return table;
    }
}
