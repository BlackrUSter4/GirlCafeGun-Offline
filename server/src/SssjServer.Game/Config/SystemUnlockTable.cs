using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// <c>Guide/SystemUnlock.txt</c>（本仓 <c>extracted/setting/SystemUnlock.txt</c>，77 条）：
/// **主城每个功能模块的解锁条件都是「某一关是否通关」，不是玩家等级**。
///
/// 取证：
/// <code>
/// -- extracted/lua/SystemUnlock.lua:73
/// function tbClass:CheckLevel(chapter, index, diff, tip)
///     local bPassed = me:GetPlayerLevelData(chapter, index, diff);
///     if bPassed then return true; end
///     ...
/// -- extracted/lua/SystemUnlock.lua:105  Init()
///     tbFile = LoadTabFile("Guide/SystemUnlock.txt", 3);
///     tbConfig[Key] = { Chapter=…, Index=…, Diff=…, CostomTip=… }
/// </code>
/// 而 <c>me:GetPlayerLevelData</c> 读的就是 <c>Game.Player.levelList</c>
/// （<c>Dictionary&lt;uint,int&gt;</c>，字段偏移 0x78，dump.cs:558118），
/// 也就是服务端 <c>PlayerNtf.levellist</c> / pf 1034 <c>ChapterUpdateNtf</c> 下发的那张表。
///
/// 结论：**要开哪个功能，就把它对应的关卡记成已通关**（见 <see cref="Player.RecordLevelPass"/>）。
/// 表里有几个哨兵行不要当真：<c>None 30 30 1</c>（注释写着「无效配置」）、
/// <c>UI_UnlockChapterChallenge11..20</c> 的 <c>Chapter=1000</c>（占位，现网没有第 1000 章）。
/// </summary>
public sealed class SystemUnlockTable
{
    /// <summary>配置文件名（相对 setting 目录）。</summary>
    public const string FileName = "SystemUnlock.txt";

    /// <summary>「无效配置」哨兵行的 Key（SystemUnlock.txt:45 注释即「无效配置」）。</summary>
    public const string SentinelKey = "None";

    /// <summary>占位章节号：超过这个值的行视为未开放内容（现网最大章节远小于它）。</summary>
    public const int PlaceholderChapter = 100;

    /// <summary>一条解锁配置。</summary>
    public sealed record Entry(string Key, int Chapter, int Index, int Diff, string CustomTip)
    {
        /// <summary>该条对应的 mapId（<see cref="MapList.MapId"/> 口径）。</summary>
        public uint MapId => MapList.MapId(Chapter, Index, Diff);

        /// <summary>是否是哨兵/占位行（不该被当作真实解锁目标）。</summary>
        public bool IsPlaceholder
            => string.Equals(Key, SentinelKey, StringComparison.Ordinal) || Chapter > PlaceholderChapter;
    }

    private readonly List<Entry> _entries = new();
    private readonly Dictionary<string, Entry> _byKey = new(StringComparer.Ordinal);

    private SystemUnlockTable() { }

    /// <summary>空表（配置缺失时的降级值）。</summary>
    public static SystemUnlockTable Empty { get; } = new();

    /// <summary>全部条目（含哨兵行，按文件顺序）。</summary>
    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>按 Key 取；不存在返回 null。</summary>
    public Entry? Get(string key) => _byKey.TryGetValue(key, out var e) ? e : null;

    /// <summary>
    /// 真实解锁目标去重后的 mapId 集合（跳过哨兵/占位行）。
    /// 多个 Key 共用同一关是常态（例如 <c>UI_UnlockGacha</c> 与 <c>UI_UnlockTeam</c> 都是 1-2-1）。
    /// </summary>
    public IReadOnlyList<uint> RealMapIds()
        => _entries.Where(e => !e.IsPlaceholder).Select(e => e.MapId).Distinct().ToList();

    public static SystemUnlockTable Load(string settingDir)
    {
        var table = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (table is null)
            return Empty;

        var result = new SystemUnlockTable();
        foreach (var row in table.Rows)
        {
            string key = table.Str(row, "Key");
            if (key.Length == 0)
                continue;
            var entry = new Entry(
                key,
                table.Int(row, "Chapter"),
                table.Int(row, "Index"),
                table.Int(row, "Diff"),
                table.Str(row, "CostomTip"));   // 原表列名就是拼错的 CostomTip
            result._entries.Add(entry);
            result._byKey[key] = entry;
        }

        Log.Info($"[setting] {FileName} 载入 {result._entries.Count} 条解锁条件" +
                 $"（真实关卡 {result.RealMapIds().Count} 个）");
        return result;
    }
}
