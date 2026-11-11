using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 任务 / 成就的「一条配置」。八张表（日常任务 / 成就 / 其它成就 / 指引任务 / 社团任务）
/// 的列名高度重合，所以统一成这一个结构，缺列的字段留空。
///
/// 列名与中文含义全部取自表文件自身的前三行表头（<c>LoadTabFile(path, 3)</c> 口径，
/// 见 <see cref="SettingTable"/>），解析规则则抄 <c>extracted/lua/MissionManager.lua</c>：
/// <code>
/// DailyMission.txt   MissionManager:LoadMissionConfig        (MissionManager.lua:46..108)
/// Achievement.txt    MissionManager:LoadAchievementConfig    (MissionManager.lua:433..502)
/// AchievementOther   MissionManager:LoadAchievementOtherConfig(MissionManager.lua:504..547)
/// GuideMission.txt   MissionManager:LoadGuideMissionConfig   (MissionManager.lua:168..197)
/// ClubMission.txt    MissionManager:LoadClubMissionConfig    (MissionManager.lua:229..277)
/// </code>
/// </summary>
public sealed class MissionEntry
{
    /// <summary>配置表主键 <c>missionId</c>。</summary>
    public int Id { get; init; }

    /// <summary>
    /// 任务值所在 group。日常任务 / 指引任务 / 社团任务都是 5
    /// （<c>MissionManager.Mission_Group = 5</c>，MissionManager.lua:6；三张表的表头第 2 行
    /// 都写着「任务ID(groupid 5,...)」）；成就是 6（<c>Normal_Group</c>，MissionManager.lua:15），
    /// 其它成就是 18（<c>Other_Group</c>，MissionManager.lua:16）。
    /// </summary>
    public int Group { get; init; }

    /// <summary>前置任务 id（<c>preMissionId</c>，0 = 无）。</summary>
    public int PreId { get; init; }

    /// <summary><c>missionType</c>：-1 废弃 / 0 一般 / 1 终结任务 / 2 少女事件（DailyMission 表头第 2 行）。</summary>
    public int Type { get; init; }

    /// <summary><c>missionSubType</c>：少女事件的子类型（0 默认 / 1 出战 / 2 好感度 / 3 看板娘 / 4 服务生）。</summary>
    public int SubType { get; init; }

    /// <summary><c>achieveType</c>（成就表专用，成就的追踪规则编号，见 AchieveType.txt）。</summary>
    public int AchieveType { get; init; }

    /// <summary>玩家等级门槛（<c>level</c>）。</summary>
    public int Level { get; init; }

    /// <summary>
    /// <c>missionMax</c>「执行次数」，形如 <c>[5]</c> 或 <c>[4,12,1,1]</c>。
    /// 判定用的那一项由 <see cref="Assist"/>[0] 决定（成就）或固定第 1 项（任务/指引/社团），
    /// 见 <c>MissionManager:CheckInfoState</c>（MissionManager.lua:927..954）。
    /// </summary>
    public List<int> Counts { get; init; } = new();

    /// <summary>
    /// <c>assistparam</c>「辅助变量」，成就表用。<c>CheckInfoState</c> 的
    /// <c>nKey = tonumber(tbInfo.tbAssist[1])</c>（MissionManager.lua:936）指出
    /// 第一项就是「拿 missionMax 的第几项做完成阈值」。实测取值只有 1/2/3/4。
    /// </summary>
    public List<int> Assist { get; init; } = new();

    /// <summary>
    /// <c>awardsList</c>「奖励道具ID」，每项是 (G,D,P,L,N) 五元组
    /// （MissionManager.lua:66..71 把 <c>[a-b-c-d-e,...]</c> 拆成 <c>tbReward</c>）。
    /// 客户端领奖弹窗直接读自己这份配置（MissionManager.lua:746..783），
    /// 所以服务端**不需要**下发奖励内容。
    /// </summary>
    public List<int[]> Rewards { get; init; } = new();

    /// <summary><c>ChapterLimit</c>「关卡解锁」，形如 <c>[2-4-1]</c> → (chapter,index,diff)。</summary>
    public List<int> ChapterLimit { get; init; } = new();

    /// <summary>
    /// <c>targetChapter</c>「完成条件(通关关卡)」，仅 GuideMission.txt 有，形如 <c>[1-1-1]</c>。
    /// 这是服务端唯一能自行判定「指引任务是否完成」的字段（见 <c>MissionFeature</c>）。
    /// </summary>
    public List<int> TargetChapter { get; init; } = new();

    /// <summary><c>preGuideId</c>「前置引导ID」（GuideMission.txt，空串 = 无）。</summary>
    public string PreGuideId { get; init; } = "";

    /// <summary><c>missionTitle</c> 原始值（客户端会加 <c>mission.</c> 前缀后查本地化）。</summary>
    public string Title { get; init; } = "";

    /// <summary><c>missionDesc</c> 原始值（同上）。</summary>
    public string Desc { get; init; } = "";

    /// <summary><c>target</c>「任务未完成时跳转的界面或场景」。</summary>
    public string Target { get; init; } = "";

    /// <summary><c>launcherIcon</c>「发件人图标路径」（MissionNotify 弹窗用）。</summary>
    public string LauncherIcon { get; init; } = "";

    /// <summary><c>launcherName</c>「发件人名」（本地化 key，MissionNotify 弹窗用）。</summary>
    public string LauncherName { get; init; } = "";

    /// <summary>
    /// <c>index</c>「是否推送实时消息 (值越大优先推) 不填不推」——
    /// 即 LuaCall 200 <c>MissionNotify</c> 的推送开关/优先级（DailyMission.txt / Achievement.txt 表头第 2 行）。
    /// 0 = 不推。
    /// </summary>
    public int NotifyIndex { get; init; }

    /// <summary><c>weekMission</c>（1 每周刷新 / 0 每日刷新）。</summary>
    public int WeekClear { get; init; }

    /// <summary>
    /// <c>AddGirlLove</c>「完成后加的少女好感度经验」（DailyMission.txt 第 8 列）。
    ///
    /// 取证 <c>MissionManager:LoadMissionConfig</c>（MissionManager.lua:75）：
    /// <c>tb.addgirllove = tonumber(tbRow["AddGirlLove"]) or 0</c>。
    /// ★ 只有 <see cref="Type"/> == 2（少女事件）的行有值，实测 6 行全是 **300**。
    /// 由 <see cref="Handlers.GirlLoveFeature"/> 的 LuaCall 174 领奖时发放。
    /// </summary>
    public int AddGirlLove { get; init; }

    /// <summary>
    /// 完成阈值：成就取 <c>Counts[Assist[0]-1]</c>，其余取 <c>Counts[0]</c>。
    /// 与 <c>MissionManager:CheckInfoState</c>（MissionManager.lua:931..951）一致。
    /// 配置缺失时返回 <see cref="int.MaxValue"/>（= 永远不可领，宁可不发也不错发）。
    /// </summary>
    public int RequiredCount(bool achieveKey)
    {
        int key = 1;
        if (achieveKey && Assist.Count > 0 && Assist[0] >= 1)
            key = Assist[0];
        if (Counts.Count < key)
            return int.MaxValue;
        return Counts[key - 1];
    }

    public override string ToString()
        => $"#{Id}(group{Group}) type={Type} counts=[{string.Join(",", Counts)}] rewards={Rewards.Count}";
}

/// <summary>
/// 「进度奖励」的一档（活跃点数 / 指引任务完成数 / 社团周任务值）。
/// 三张表结构一致：<c>Id</c>（档位序号，同时是任务值里的 bit 下标）+ <c>Point</c>（阈值）+ 物品列。
/// 取证：<c>MissionManager:LoadActivePointAward</c>（MissionManager.lua:134..165）、
/// <c>LoadGuideMissionAward</c>（:200..226）、<c>LoadClubMissionAward</c>（:280..306）。
/// </summary>
public sealed class MissionPointAward
{
    /// <summary>档位 id（1 起）。同时是位标记任务值里的 bit 下标（<c>LoadBits(nFlag, i, i)</c>）。</summary>
    public int Id { get; init; }

    /// <summary>达成阈值（活跃点数 / 已完成指引任务数 / 社团周任务值）。</summary>
    public int Point { get; init; }

    /// <summary>奖励物品 (G,D,P,L,N) 列表。</summary>
    public List<int[]> Items { get; init; } = new();

    public override string ToString() => $"档{Id} point={Point} items={Items.Count}";
}

/// <summary>
/// 任务 / 成就的全部配置表。与客户端读同一批文件，因此「哪条任务要几次」这种判定天然一致。
///
/// <para><b>八张表与服务端要用的列</b></para>
/// <list type="table">
/// <item><term>DailyMission.txt (17 行)</term><description>日常任务，group 5，键 <c>missionId</c>(1..110)；
///   服务端要 <c>missionMax</c>(阈值) / <c>missionType</c>(-1 废弃、2 少女事件不进任务列表) /
///   <c>awardsList</c>(奖励，仅日志) / <c>index</c>(是否推 MissionNotify)。</description></item>
/// <item><term>Achievement.txt (1435 行)</term><description>成就，group 6，键 <c>missionId</c>；
///   服务端要 <c>missionMax</c> + <c>assistparam</c>(取第几项做阈值) / <c>preMissionId</c>(成就链)。</description></item>
/// <item><term>AchievementOther.txt (1857 行)</term><description>活动成就，group 18，同上；
///   本实现只装载不使用（客户端 <c>GetOtherAchieve</c> 走 group 18，领奖入口不在 MissionMgrMsg 族）。</description></item>
/// <item><term>GuideMission.txt (15 行)</term><description>新手指引任务链，group 5，键 <c>missionId</c>(40001..40027)；
///   服务端要 <c>missionMax</c> / <c>preMissionId</c> / <c>targetChapter</c>(通关哪一关算完成)。</description></item>
/// <item><term>MissionAward.txt (5 行)</term><description>活跃点数进度奖励，键 <c>Id</c>(1..5)，阈值列 <c>Point</c>；
///   ★ 物品列名是 <c>Rewards</c>，而客户端 <c>LoadActivePointAward</c> 读的是 <c>Item1..Item5</c>
///   （MissionManager.lua:150），列名对不上 → 客户端这一档的 <c>tbAward</c> 恒为空。
///   这正是 <c>Dispatcher</c> 里 nMission==2 要看 <c>tbParam.tbItems</c> 的原因（MissionManager.lua:785）。</description></item>
/// <item><term>GuideMissionAward.txt (5 行)</term><description>指引任务完成数进度奖励，键 <c>Id</c>(1..5)，
///   阈值 <c>Point</c>=3/6/9/12/15，物品列 <c>Item1..Item5</c>。</description></item>
/// <item><term>ClubMission.txt (24 行)</term><description>社团任务，group 5，键 <c>missionId</c>(50001..50024)。</description></item>
/// <item><term>ClubMissionAward.txt (5 行)</term><description>社团周任务值进度奖励，键 <c>Id</c>(1..5)，
///   阈值 <c>Point</c>=500/1200/2000/3000/4000。</description></item>
/// </list>
/// 三段 id 区间互不重叠（1..110 / 40001..40027 / 50001..50024），所以同在 group 5 也不会撞键。
/// </summary>
public sealed class MissionTable
{
    /// <summary>日常任务 group（<c>MissionManager.Mission_Group = 5</c>，MissionManager.lua:6）。</summary>
    public const int MissionGroup = 5;

    /// <summary>成就 group（<c>MissionManager.Normal_Group = 6</c>，MissionManager.lua:15）。</summary>
    public const int NormalGroup = 6;

    /// <summary>其它（活动）成就 group（<c>MissionManager.Other_Group = 18</c>，MissionManager.lua:16）。</summary>
    public const int OtherGroup = 18;

    private readonly Dictionary<int, MissionEntry> _daily = new();
    private readonly Dictionary<int, MissionEntry> _achieve = new();
    private readonly Dictionary<int, MissionEntry> _achieveOther = new();
    private readonly Dictionary<int, MissionEntry> _guide = new();
    private readonly Dictionary<int, MissionEntry> _club = new();
    private readonly Dictionary<int, MissionPointAward> _activePoint = new();
    private readonly Dictionary<int, MissionPointAward> _guideProgress = new();
    private readonly Dictionary<int, MissionPointAward> _clubProgress = new();

    private MissionTable() { }

    /// <summary>空表（配置目录缺失时的降级，以及单测的起点）。</summary>
    public static MissionTable Empty { get; } = new();

    /// <summary>日常任务：missionId → 配置（DailyMission.txt，已剔除 <c>missionType == -1</c> 的废弃行）。</summary>
    public IReadOnlyDictionary<int, MissionEntry> Daily => _daily;

    /// <summary>成就：missionId → 配置（Achievement.txt，group 6）。</summary>
    public IReadOnlyDictionary<int, MissionEntry> Achieve => _achieve;

    /// <summary>活动成就：missionId → 配置（AchievementOther.txt，group 18）。</summary>
    public IReadOnlyDictionary<int, MissionEntry> AchieveOther => _achieveOther;

    /// <summary>指引任务：missionId → 配置（GuideMission.txt，group 5）。</summary>
    public IReadOnlyDictionary<int, MissionEntry> Guide => _guide;

    /// <summary>社团任务：missionId → 配置（ClubMission.txt，group 5）。</summary>
    public IReadOnlyDictionary<int, MissionEntry> Club => _club;

    /// <summary>活跃点数进度奖励：档位 Id → 配置（MissionAward.txt）。</summary>
    public IReadOnlyDictionary<int, MissionPointAward> ActivePointAwards => _activePoint;

    /// <summary>指引任务完成数进度奖励：档位 Id → 配置（GuideMissionAward.txt）。</summary>
    public IReadOnlyDictionary<int, MissionPointAward> GuideProgressAwards => _guideProgress;

    /// <summary>社团周任务值进度奖励：档位 Id → 配置（ClubMissionAward.txt）。</summary>
    public IReadOnlyDictionary<int, MissionPointAward> ClubProgressAwards => _clubProgress;

    /// <summary>
    /// 活跃点数上限 = <c>MissionAward.txt</c> 里最大的 <c>Point</c>
    /// （<c>MissionManager.ActivePointMax</c>，MissionManager.lua:144；客户端用它判「今日任务已做完」）。
    /// </summary>
    public int ActivePointMax => _activePoint.Count == 0 ? 0 : _activePoint.Values.Max(a => a.Point);

    /// <summary>指引任务总数（客户端进度条分母，UI_Achievement.lua:830 CountTB(GetGuideMissionType())）。</summary>
    public int GuideMissionCount => _guide.Count;

    public MissionEntry? GetDaily(int id) => _daily.TryGetValue(id, out var v) ? v : null;
    public MissionEntry? GetAchieve(int id) => _achieve.TryGetValue(id, out var v) ? v : null;
    public MissionEntry? GetAchieveOther(int id) => _achieveOther.TryGetValue(id, out var v) ? v : null;
    public MissionEntry? GetGuide(int id) => _guide.TryGetValue(id, out var v) ? v : null;
    public MissionEntry? GetClub(int id) => _club.TryGetValue(id, out var v) ? v : null;

    public MissionPointAward? GetActivePointAward(int id)
        => _activePoint.TryGetValue(id, out var v) ? v : null;

    public MissionPointAward? GetGuideProgressAward(int id)
        => _guideProgress.TryGetValue(id, out var v) ? v : null;

    public MissionPointAward? GetClubProgressAward(int id)
        => _clubProgress.TryGetValue(id, out var v) ? v : null;

    /// <summary>
    /// 「少女事件」= <c>DailyMission.txt</c> 里 <c>missionType == 2</c> 的行
    /// （<c>MissionManager.lua:100..102</c>：<c>if tb.type == 2 then table.insert(self.tbGirlEvent, tb)</c>）。
    ///
    /// ★ 实测现网 6 条（missionId <b>1,2,3,4,6,7</b> —— 摸头 / 外出 / 送礼 / 携带战斗 /
    /// 设置看板娘 / 设置服务生），奖励全是 <c>[15-2-1-1-20,9-16-1-1-1]</c>、
    /// <c>AddGirlLove</c> 全是 <b>300</b>。
    ///
    /// ★★ 少女事件 id **不是**少女 id：客户端「今天是哪个事件 / 哪个少女」分别存在任务值
    /// <c>(5, 20001)</c> 与 <c>(5, 20000)</c>（<c>MissionManager.lua:7..8</c>），
    /// LuaCall 174 的上行 <c>nId</c> 就是前者。
    /// </summary>
    public IEnumerable<MissionEntry> GirlEvents
        => _daily.Values.Where(m => m.Type == GirlEventType).OrderBy(m => m.Id);

    /// <summary>按 id 取少女事件（<c>missionType == 2</c> 的日常任务）；不是少女事件返回 null。</summary>
    public MissionEntry? GetGirlEvent(int id)
        => _daily.TryGetValue(id, out var v) && v.Type == GirlEventType ? v : null;

    /// <summary><c>missionType == 2</c> = 少女事件（DailyMission 表头第 2 行注释）。</summary>
    public const int GirlEventType = 2;

    /// <summary>
    /// 从 setting 目录加载全部八张表；任一文件缺失只影响它自己那份集合（其余照常）。
    /// 与 <see cref="ChapterTable.Load"/> 一致：绝不抛，缺表就降级成空集合。
    /// </summary>
    public static MissionTable Load(string settingDir)
    {
        var t = new MissionTable();

        LoadMissions(t._daily, Path.Combine(settingDir, "DailyMission.txt"), MissionGroup,
                     skipDiscarded: true, hasTargetChapter: false);
        LoadMissions(t._achieve, Path.Combine(settingDir, "Achievement.txt"), NormalGroup,
                     skipDiscarded: false, hasTargetChapter: false);
        LoadMissions(t._achieveOther, Path.Combine(settingDir, "AchievementOther.txt"), OtherGroup,
                     skipDiscarded: false, hasTargetChapter: false);
        LoadMissions(t._guide, Path.Combine(settingDir, "GuideMission.txt"), MissionGroup,
                     skipDiscarded: false, hasTargetChapter: true);
        LoadMissions(t._club, Path.Combine(settingDir, "ClubMission.txt"), MissionGroup,
                     skipDiscarded: true, hasTargetChapter: false);

        // MissionAward.txt 的物品列叫 Rewards（客户端读 Item1..5，读不到 —— 见类注释）
        // Return events ship a MissionAward.txt too, and that one owns the flat name.
        LoadPointAwards(t._activePoint, File.Exists(Path.Combine(settingDir, "_scoped", "mission", "missionaward.txt")) ? Path.Combine(settingDir, "_scoped", "mission", "missionaward.txt") : Path.Combine(settingDir, "MissionAward.txt"),
                        new[] { "Rewards", "Item1", "Item2", "Item3", "Item4", "Item5" });
        LoadPointAwards(t._guideProgress, Path.Combine(settingDir, "GuideMissionAward.txt"),
                        new[] { "Item1", "Item2", "Item3", "Item4", "Item5" });
        LoadPointAwards(t._clubProgress, Path.Combine(settingDir, "ClubMissionAward.txt"),
                        new[] { "Item1", "Item2", "Item3", "Item4", "Item5" });

        Log.Info($"[setting] 任务/成就表载入：日常{t._daily.Count} 成就{t._achieve.Count} " +
                 $"活动成就{t._achieveOther.Count} 指引{t._guide.Count} 社团{t._club.Count} " +
                 $"活跃档{t._activePoint.Count}(上限{t.ActivePointMax}) " +
                 $"指引档{t._guideProgress.Count} 社团档{t._clubProgress.Count}");
        return t;
    }

    private static void LoadMissions(Dictionary<int, MissionEntry> into, string path, int group,
                                    bool skipDiscarded, bool hasTargetChapter)
    {
        var raw = SettingTable.Load(path);
        if (raw is null)
            return;

        foreach (var row in raw.Rows)
        {
            int id = raw.Int(row, "missionId", -1);
            if (id <= 0)
                continue;

            // MissionManager.lua:57 / :236 —— missionType == -1 是「废弃」，客户端根本不装载
            int type = raw.Int(row, "missionType");
            if (skipDiscarded && type == -1)
                continue;

            var entry = new MissionEntry
            {
                Id = id,
                Group = group,
                PreId = raw.Int(row, "preMissionId"),
                Type = type,
                SubType = raw.Int(row, "missionSubType"),
                AchieveType = raw.Int(row, "achieveType"),
                Level = raw.Int(row, "level"),
                Counts = ParseIntList(raw.Str(row, "missionMax"), ','),
                Assist = ParseIntList(raw.Str(row, "assistparam"), ','),
                Rewards = ParseTuples(raw.Str(row, "awardsList")),
                ChapterLimit = ParseIntList(raw.Str(row, "ChapterLimit"), '-'),
                TargetChapter = hasTargetChapter ? ParseIntList(raw.Str(row, "targetChapter"), '-') : new List<int>(),
                PreGuideId = raw.Str(row, "preGuideId"),
                Title = raw.Str(row, "missionTitle"),
                Desc = raw.Str(row, "missionDesc"),
                Target = raw.Str(row, "target"),
                LauncherIcon = raw.Str(row, "launcherIcon"),
                LauncherName = raw.Str(row, "launcherName"),
                NotifyIndex = raw.Int(row, "index"),
                WeekClear = raw.Int(row, "weekMission"),
                AddGirlLove = raw.Int(row, "AddGirlLove"),
            };
            into[id] = entry;
        }
    }

    private static void LoadPointAwards(Dictionary<int, MissionPointAward> into, string path,
                                        IReadOnlyList<string> itemColumns)
    {
        var raw = SettingTable.Load(path);
        if (raw is null)
            return;

        foreach (var row in raw.Rows)
        {
            int id = raw.Int(row, "Id", -1);
            if (id <= 0)
                continue;

            var items = new List<int[]>();
            foreach (var col in itemColumns)
            {
                if (!raw.Columns.ContainsKey(col))
                    continue;
                items.AddRange(ParseTuples(raw.Str(row, col)));
            }

            into[id] = new MissionPointAward
            {
                Id = id,
                Point = raw.Int(row, "Point"),
                Items = items,
            };
        }
    }

    /// <summary>
    /// 解析 <c>[a,b,c]</c> / <c>[a-b-c]</c> 这类「带方括号的定长数组」。
    /// 与客户端一致：先 <c>string.sub(s, 2, -2)</c> 去掉方括号，再按分隔符 <c>Split</c>
    /// （MissionManager.lua:58..59 / :63..64）。空串 → 空列表（Lua <c>Split</c> 对空串返回 <c>{}</c>，Lib.lua:253）。
    /// </summary>
    internal static List<int> ParseIntList(string value, char delim)
    {
        var result = new List<int>();
        var body = StripBrackets(value);
        if (body.Length == 0)
            return result;

        foreach (var part in body.Split(delim))
        {
            if (int.TryParse(part.Trim(), out var n))
                result.Add(n);
        }
        return result;
    }

    /// <summary>
    /// 解析 <c>[G-D-P-L-N,G-D-P-L-N]</c> 这类奖励列：先去方括号，按 <c>,</c> 拆项，每项按 <c>-</c> 拆数字
    /// （MissionManager.lua:67..71）。非数字项整条丢弃（不猜、不补默认值）。
    /// </summary>
    internal static List<int[]> ParseTuples(string value)
    {
        var result = new List<int[]>();
        var body = StripBrackets(value);
        if (body.Length == 0)
            return result;

        foreach (var item in body.Split(','))
        {
            var parts = item.Trim().Split('-');
            var nums = new int[parts.Length];
            bool ok = parts.Length > 0;
            for (int i = 0; i < parts.Length && ok; i++)
                ok = int.TryParse(parts[i].Trim(), out nums[i]);
            if (ok)
                result.Add(nums);
        }
        return result;
    }

    private static string StripBrackets(string value)
    {
        var s = value.Trim();
        if (s.Length >= 2 && s[0] == '[' && s[^1] == ']')
            s = s.Substring(1, s.Length - 2);
        return s.Trim();
    }

    // ---- 单测用的构造接缝（生产路径只用 Load）----

    internal MissionTable Put(string kind, MissionEntry entry)
    {
        Bucket(kind)[entry.Id] = entry;
        return this;
    }

    internal MissionTable Put(string kind, MissionPointAward award)
    {
        AwardBucket(kind)[award.Id] = award;
        return this;
    }

    internal static MissionTable NewForTest() => new();

    private Dictionary<int, MissionEntry> Bucket(string kind) => kind switch
    {
        "daily" => _daily,
        "achieve" => _achieve,
        "achieveOther" => _achieveOther,
        "guide" => _guide,
        "club" => _club,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知任务集合"),
    };

    private Dictionary<int, MissionPointAward> AwardBucket(string kind) => kind switch
    {
        "activePoint" => _activePoint,
        "guideProgress" => _guideProgress,
        "clubProgress" => _clubProgress,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "未知进度奖励集合"),
    };
}
