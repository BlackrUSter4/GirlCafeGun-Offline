using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一关的配置（<c>extracted/setting/Chapter.txt</c> 的一行）。
/// 列名取自该表第 3 行（英文字段名），中文含义取自第 2 行：
/// <code>
/// Chapter  章节ID          Index    关卡ID          Difficult 难度
/// MapID    对应地图ID      Type     节点类型(1通常/2剧情/3boss)
/// PreCost  体力预扣除      Vigour   体力消耗        LevelLimit 关卡解锁等级(指挥官)
/// Diamond  钻石奖励        RewardGold 金币奖励      MasterExp  店长获得经验  CardExp 卡牌获得经验
/// FirstAward 首通奖励(G-D-P-L-N-Level)   FixedAward 固定奖励(G-D-P-L-N-Rate)
/// StarType 是否有星级目标
/// </code>
/// </summary>
public sealed class ChapterLevel
{
    public int Chapter { get; init; }
    public int Index { get; init; }
    public int Diff { get; init; }
    public string Name { get; init; } = "";

    /// <summary>场景（Unity map）id —— 客户端 <c>Map.LoadById(levelInfo.nMapID)</c> 用。</summary>
    public int SceneMapId { get; init; }

    public int Type { get; init; }

    /// <summary>体力预扣除。</summary>
    public int PreCost { get; init; }

    /// <summary>体力消耗。</summary>
    public int Vigour { get; init; }

    public int LevelLimit { get; init; }
    public int Diamond { get; init; }
    public int RewardGold { get; init; }
    public int MasterExp { get; init; }
    public int CardExp { get; init; }
    public int StarType { get; init; }

    /// <summary>首通奖励，每项是 (G,D,P,L,N[,Level]) 元组。</summary>
    public List<int[]> FirstAward { get; init; } = new();

    /// <summary>固定奖励，每项是 (G,D,P,L,N[,Rate]) 元组。</summary>
    public List<int[]> FixedAward { get; init; } = new();

    /// <summary>客户端 <c>Game.MapList.GetMapId</c> 口径的关卡 key（见 <see cref="MapList"/>）。</summary>
    public uint MapId => MapList.MapId(Chapter, Index, Diff);

    /// <summary>进关总体力消耗（UI 的「消耗」= PreCost + Vigour，与 UI_LevelInfoNew 的 nNeedEnergy 一致）。</summary>
    public int TotalVigour => PreCost + Vigour;

    public override string ToString()
        => $"{Chapter}-{Index}(diff{Diff}) {Name} scene={SceneMapId} vigour={TotalVigour} exp={MasterExp}/{CardExp}";
}

/// <summary>
/// <c>Chapter.txt</c> 全表：主线关卡配置。服务端用它取体力消耗、通关经验、奖励，
/// 与客户端读的是同一份文件，因此行为天然一致（AGENTS.md §4「配置驱动」）。
/// </summary>
public sealed class ChapterTable
{
    private readonly Dictionary<uint, ChapterLevel> _byMapId = new();

    private ChapterTable() { }

    public int Count => _byMapId.Count;

    /// <summary>永久空表（缺表降级与单测用；与 <see cref="ItemBaseTable.Empty"/> 同款）。</summary>
    public static ChapterTable Empty { get; } = new();

    /// <summary>按 (chapter,index,diff) 查一关；不存在返回 null。</summary>
    public ChapterLevel? Get(int chapter, int index, int diff)
        => _byMapId.TryGetValue(MapList.MapId(chapter, index, diff), out var lv) ? lv : null;

    /// <summary>按 <see cref="MapList"/> 口径的 mapId 查一关。</summary>
    public ChapterLevel? GetByMapId(uint mapId)
        => _byMapId.TryGetValue(mapId, out var lv) ? lv : null;

    public IEnumerable<ChapterLevel> All => _byMapId.Values;

    /// <summary>从 setting 目录加载 Chapter.txt；失败时返回空表（服务端降级为「不查配置」）。</summary>
    public static ChapterTable Load(string settingDir)
    {
        var table = new ChapterTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, "Chapter.txt"));
        if (raw is null)
        {
            Log.Warn("[setting] Chapter.txt 缺失：关卡体力/经验/奖励将全部按 0 处理");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int chapter = raw.Int(row, "Chapter", -1);
            int index = raw.Int(row, "Index", -1);
            int diff = raw.Int(row, "Difficult", -1);
            if (chapter < 0 || index < 0 || diff < 0)
                continue;

            var lv = new ChapterLevel
            {
                Chapter = chapter,
                Index = index,
                Diff = diff,
                Name = raw.Str(row, "name"),
                SceneMapId = raw.Int(row, "MapID"),
                Type = raw.Int(row, "Type"),
                PreCost = raw.Int(row, "PreCost"),
                Vigour = raw.Int(row, "Vigour"),
                LevelLimit = raw.Int(row, "LevelLimit"),
                Diamond = raw.Int(row, "Diamond"),
                RewardGold = raw.Int(row, "RewardGold"),
                MasterExp = raw.Int(row, "MasterExp"),
                CardExp = raw.Int(row, "CardExp"),
                StarType = raw.Int(row, "StarType"),
                FirstAward = ParseTuples(raw.Str(row, "FirstAward")),
                FixedAward = ParseTuples(raw.Str(row, "FixedAward")),
            };
            table._byMapId[lv.MapId] = lv;
        }
        Log.Info($"[setting] Chapter.txt 载入 {table._byMapId.Count} 关");
        return table;
    }

    /// <summary>
    /// 解析配置里的元组列表，两种写法都吃：
    /// <c>[[15-2-1-1-20],[7-1-4-1-1]]</c>、<c>[15-1-1-1-1000-10000]</c> 与 <c>[15-1-1-1-500,15-14-1-1-30]</c>。
    /// 返回每个中括号内 '-' 分隔的整数数组；解析不出整数的项忽略。
    /// </summary>
    internal static List<int[]> ParseTuples(string text)
    {
        var result = new List<int[]>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        int i = 0;
        while (i < text.Length)
        {
            int open = text.IndexOf('[', i);
            if (open < 0)
                break;
            int close = text.IndexOf(']', open + 1);
            if (close < 0)
                break;

            var body = text.Substring(open + 1, close - open - 1);
            if (body.Contains('['))
            {
                // 外层 [[..],[..]]：跳过这一层，让内层各自匹配
                i = open + 1;
                continue;
            }

            // Most multi-reward rows are one bracket with commas: [15-1-1-1-500,15-14-1-1-30]. Reading that whole
            // body as one tuple failed on "500,15" and silently lost every reward in the row.
            foreach (var tuple in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = tuple.Split('-', StringSplitOptions.RemoveEmptyEntries);
                var nums = new List<int>(parts.Length);
                // A comma-separated plain number list like [1,2,3] is not a tuple list, so it stays ignored.
                bool ok = parts.Length > 0 && (parts.Length > 1 || !body.Contains(','));
                foreach (var p in parts)
                {
                    if (int.TryParse(p.Trim(), out var n))
                        nums.Add(n);
                    else { ok = false; break; }
                }
                if (ok)
                    result.Add(nums.ToArray());
            }
            i = close + 1;
        }
        return result;
    }
}
