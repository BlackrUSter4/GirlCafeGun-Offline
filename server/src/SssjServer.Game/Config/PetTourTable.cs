using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一个远征栏位（<c>TourArea.txt</c> 的一行）。
///
/// 客户端加载点 <c>NCafePetTour.lua</c>（<c>LoadTabFile(..., 3)</c> ⇒ **三行表头**，
/// 对应 <see cref="SettingTable.Load"/> 的 <c>headerRow = 2</c>）。
/// 实测 5 行 = 5 个栏位。
///
/// ★ 本表**没有任何时间列** —— 远征是常驻玩法，不像佣兵/社团 boss 那样有档期，
/// 所以不需要 <c>BeginTime/ClearTime</c> 那套判定（`docs/05` §40.5 的坑在这里不适用）。
/// </summary>
public sealed class TourAreaRow
{
    /// <summary>栏位 id（<c>Index</c>），1..5。同时是任务值偏移的下标。</summary>
    public int Index { get; init; }

    /// <summary>该栏位产出的远征品阶（<c>Rare</c>）：1 C / 2 B / 3 A。</summary>
    public int Rare { get; init; }

    /// <summary>背景图（<c>bg</c>），服务端不用，仅作取证留档。</summary>
    public string Bg { get; init; } = "";

    /// <summary>栏位名的多语言 key（<c>Name</c>）。</summary>
    public string Name { get; init; } = "";

    /// <summary>远征时长**小时**（<c>Hour</c>）。实测 4 / 6 / 8。</summary>
    public int Hour { get; init; }

    /// <summary>每只宠物出征需要的精力（<c>EnergyCost</c>）。实测 20 / 50 / 80。</summary>
    public int EnergyCost { get; init; }

    /// <summary>
    /// 「宠物指数」满额线（<c>ExNum</c>，实测 100/200/300）。
    /// 客户端 <c>UI_PetTour.lua:609</c> 用 <c>min(实际指数 / ExNum, 1)</c> 折算指数奖励档位。
    /// </summary>
    public int ExNum { get; init; }

    /// <summary>
    /// 解锁条件 1（<c>Unlock1</c>）= <c>[品阶, 次数]</c>，要求「完成 N 次该品阶远征」。
    /// 空 = 无此条件（栏位 1/2 就是空）。
    /// </summary>
    public IReadOnlyList<int> Unlock1 { get; init; } = Array.Empty<int>();

    /// <summary>解锁条件 2（<c>Unlock2</c>）= 需要拥有的宠物只数。0 = 无此条件。</summary>
    public int Unlock2 { get; init; }

    /// <summary>
    /// 解锁条件 3（<c>Unlock3</c>）= <c>[货币id, 数量]</c>，点解锁时**扣除**。
    /// 空 = 免费。实测栏位 3/4/5 分别是 <c>[2,20000]</c> / <c>[2,30000]</c> / <c>[2,50000]</c>。
    /// </summary>
    public IReadOnlyList<int> Unlock3 { get; init; } = Array.Empty<int>();
}

/// <summary>
/// 一种远征任务（<c>TourInfo.txt</c> 的一行）。栏位解锁后会从本表里按品阶抽一条挂上去。
/// </summary>
public sealed class TourInfoRow
{
    /// <summary>远征 id（<c>Index</c>）。</summary>
    public int Index { get; init; }

    /// <summary>品阶（<c>Rare</c>）：1 C / 2 B / 3 A / 4 S。</summary>
    public int Rare { get; init; }

    /// <summary>时长小时（<c>Hour</c>）。★ 与 <see cref="TourAreaRow.Hour"/> 并存，
    /// 客户端倒计时读的是**栏位**的 Hour（<c>UI_PetTour.lua:626</c> <c>data.Hour</c>），本字段仅留档。</summary>
    public int Hour { get; init; }

    /// <summary>每只宠物的精力消耗（<c>EnergyCost</c>）。同上，实际扣除以栏位的为准。</summary>
    public int EnergyCost { get; init; }

    /// <summary>普通奖励 id 列表（<c>AwardNormal</c>，短横分隔，如 <c>7-1-4-2-5</c>）。</summary>
    public IReadOnlyList<int> AwardNormal { get; init; } = Array.Empty<int>();

    /// <summary>大成功奖励 id 列表（<c>AwardSpecial</c>）。</summary>
    public IReadOnlyList<int> AwardSpecial { get; init; } = Array.Empty<int>();

    /// <summary>
    /// 大成功概率的两个插值点（<c>SpWinNum</c>，形如 <c>{{10,20},{60,100}}</c>）=
    /// <c>[(最低总等级, 最低概率%), (最高总等级, 最高概率%)]</c>。
    /// 算法见 <see cref="PetTourTable.SuccessPercent"/>（与客户端 <c>GetPetsTourSuccessPro</c> 同构）。
    /// </summary>
    public IReadOnlyList<(int Lv, int Pro)> SpWinNum { get; init; } = Array.Empty<(int, int)>();

    /// <summary>
    /// 出征条件（<c>Condition</c>，形如 <c>{{2,1,2},{3,3,2}}</c>）= 每条 <c>[类型, 参数, 数量]</c>：
    /// 类型 1 = 宠物只数 / 2 = 限定物种（参数 = 物种 Type）/ 3 = 限定性格（参数 = nature）。
    /// 取证 <c>NCafePetTour.lua:188..208</c>（<c>IsPetsCanGoTour</c>）。
    /// </summary>
    public IReadOnlyList<(int Type, int Param, int Count)> Condition { get; init; }
        = Array.Empty<(int, int, int)>();

    /// <summary>随机权重（<c>RandomValue</c>），栏位挂任务时按品阶内权重抽。</summary>
    public int RandomValue { get; init; }
}

/// <summary>
/// 一条远征奖励（<c>TourReward.txt</c> 的一行）。实测 49 行。
/// </summary>
public sealed class TourRewardRow
{
    /// <summary>奖励 id（<c>AwardID</c>）。★ 存进任务值时**只有 15 位**，见
    /// <see cref="PetTourTable.MaxRewardId"/>。</summary>
    public int AwardId { get; init; }

    /// <summary>对应品阶（<c>Rare</c>）。</summary>
    public int Rare { get; init; }

    /// <summary>
    /// 奖励归属（<c>AwardNormal</c>）：1 基础奖励 / 2 宠物指数奖励 / 3 条件1奖励 / 4 条件2奖励。
    /// 中文列名写的是「奖励关系（基础奖励-1 指数奖励-2 条件1奖励-3 条件2奖励-4）」。
    /// </summary>
    public int Kind { get; init; }

    /// <summary>奖励物品（<c>Award</c>，短横五元组 <c>G-D-P-L-N</c>）。</summary>
    public IReadOnlyList<int> Award { get; init; } = Array.Empty<int>();

    /// <summary>抽取权重（<c>Pro</c>）。</summary>
    public int Pro { get; init; }
}

/// <summary>
/// 咖啡馆宠物**远征**子系统的配置（<c>TourArea</c> / <c>TourInfo</c> / <c>TourReward</c>）。
/// 取证 `extracted/lua/NCafePetTour.lua` + `UI_PetTour.lua`，台账 `docs/05` §55。
///
/// <para><b>★★★ 与「小七养成」(LuaCall 190-193) 不是一回事</b></para>
/// 那套走 <c>PetLogic</c>、入口 <c>UI_Pet</c> 绑已过期的节点活动，第十五轮已判死
/// （`docs/05` §52.3）。本域走 <c>NCafePetLogic</c>，入口是**常驻**的咖啡馆
/// <c>UI_NCafe</c> → <c>BtnPet</c> → <c>UI_PetMain</c> → <c>UI_PetTour</c>。
///
/// <para><b>★ 入口链里有一条是死的</b></para>
/// <c>UI_NCafe.lua:251..263</c> 里通往 <c>UI_PetAssign</c> 的那段被 <c>--[[ ]]</c> 整段注释，
/// 所以 <c>UI_PetAssign.lua</c> 里那批同名调用是**死代码**；取证一律以 <c>UI_PetTour.lua</c> 为准。
///
/// <para><b>1. 状态全部存任务值 group 49</b>（偏移取证 <c>NCafePetTour.lua:11..19</c>）</para>
/// <list type="bullet">
///   <item><c>510 + nIndex</c> —— 该栏位当前挂的远征 id</item>
///   <item><c>550 + (nIndex-1)*3 + i</c> —— 第 i 只出征宠物的 uid（i = 1..3）</item>
///   <item><c>700 + nIndex</c> —— 状态机，见 <see cref="StateLocked"/> 一组常量</item>
///   <item><c>750 + nIndex</c> —— 出发时刻（unix 秒，0 = 没开始）</item>
///   <item><c>800 + Rare</c> —— 该**品阶**累计完成次数（栏位解锁条件 1 读它）</item>
///   <item><c>850 + (nIndex-1)*2 + 1</c> —— 低 15 位 = 基础奖励 id，高 15 位 = 指数奖励 id</item>
///   <item><c>850 + nIndex*2</c> —— 低 15 位 = 条件1奖励 id，高 15 位 = 条件2奖励 id</item>
/// </list>
///
/// <para><b>★★ 奖励 id 的位段是 1-based 的 <c>LoadBits(v,1,15)</c></b></para>
/// 客户端 <c>LoadBits(value, 1, 15)</c> / <c>LoadBits(value, 16, 30)</c>
/// （<c>:97..101</c>）取的是**第 1..15 位**与**第 16..30 位**，
/// 换算成 C# 的移位就是 <c>(v &gt;&gt; 0) &amp; 0x7FFF</c> 与 <c>(v &gt;&gt; 15) &amp; 0x7FFF</c>。
/// ⇒ 每段只有 15 位、**最大 32767**，且第 31 位空出来（不会碰到符号位）——
/// 这与 §39 剧情位域「填 int.MaxValue 而不是 -1」是同一类约束。
/// </summary>
public sealed class PetTourTable
{
    /// <summary>远征与咖啡馆宠物共用的任务值 group（<c>NCafePetTour.lua:11</c>）。</summary>
    public const int Group = 49;

    // ===== 任务值偏移（`NCafePetTour.lua:12..19` 逐条抄）=====

    /// <summary>501 能否刷新远征任务（<c>CanUpdateTaskId</c>）。本实现暂不用。</summary>
    public const int TaskCanUpdate = 501;

    /// <summary>510 + 栏位 id ⇒ 该栏位当前挂的远征 id（<c>ToursIdOff</c>）。</summary>
    public const int TaskTourIdOff = 510;

    /// <summary>550 + (栏位-1)*3 + i ⇒ 出征宠物 uid（<c>ToursFormationIdOff</c>）。</summary>
    public const int TaskFormationOff = 550;

    /// <summary>700 + 栏位 id ⇒ 状态（<c>ToursStateIdOff</c>）。</summary>
    public const int TaskStateOff = 700;

    /// <summary>750 + 栏位 id ⇒ 出发时刻（<c>ToursTimeIdOff</c>）。</summary>
    public const int TaskTimeOff = 750;

    /// <summary>800 + 品阶 ⇒ 该品阶累计完成次数（<c>ToursCompleteNumIdOff</c>）。</summary>
    public const int TaskCompleteNumOff = 800;

    /// <summary>850 起 ⇒ 本轮抽中的四个奖励 id（<c>ToursAwardIdOff</c>，两个 int、各打包两段）。</summary>
    public const int TaskAwardOff = 850;

    // ===== 状态机（`NCafePetTour.lua:15` 的原文注释）=====

    /// <summary>0 未解锁。</summary>
    public const int StateLocked = 0;

    /// <summary>1 已解锁、未出发。</summary>
    public const int StateIdle = 1;

    /// <summary>2 远征进行中。</summary>
    public const int StateRunning = 2;

    /// <summary>3 已完成、但没有大成功。</summary>
    public const int StateDone = 3;

    /// <summary>4 已完成且大成功。</summary>
    public const int StateGreatSuccess = 4;

    /// <summary>
    /// 5 已领奖。★ <b>这个值 <c>NCafePetTour.lua:15</c> 的注释里没写</b>（它只列了 0..4），
    /// 是从消费点 <c>UI_PetTour.lua:241</c>（<c>RewardGet</c> 面板）与 <c>:661</c> 反推出来的。
    /// ⇒ 又一次「注释与消费点矛盾时以消费点为准」（同 §52.1 誓约 232 那条）。
    /// </summary>
    public const int StateRewarded = 5;

    /// <summary>出征固定要 3 只宠物（<c>NCafePetTour.lua:215</c> <c>petCount &lt; 3 ⇒ res = false</c>）。</summary>
    public const int PetsPerTour = 3;

    /// <summary>奖励 id 位段宽度 15 位 ⇒ 上限 32767。</summary>
    public const int MaxRewardId = 0x7FFF;

    /// <summary>栏位 id 的合法上界（<c>GetTourAreaState</c> 判 <c>nIndex &lt; 40</c>）。</summary>
    public const int MaxAreaIndex = 39;

    private readonly Dictionary<int, TourAreaRow> _areas = new();
    private readonly Dictionary<int, TourInfoRow> _tours = new();
    private readonly Dictionary<int, TourRewardRow> _rewards = new();

    private PetTourTable() { }

    /// <summary>空表（缺表时的降级对象，所有查询返回空）。</summary>
    public static PetTourTable Empty { get; } = new();

    /// <summary>全部栏位，按 id 升序。</summary>
    public IReadOnlyList<TourAreaRow> Areas => _areas.Values.OrderBy(a => a.Index).ToList();

    /// <summary>全部远征任务。</summary>
    public IReadOnlyList<TourInfoRow> Tours => _tours.Values.OrderBy(t => t.Index).ToList();

    /// <summary>全部奖励行。</summary>
    public IReadOnlyList<TourRewardRow> Rewards => _rewards.Values.OrderBy(r => r.AwardId).ToList();

    /// <summary>取栏位配置；不存在返回 null。</summary>
    public TourAreaRow? Area(int index) => _areas.GetValueOrDefault(index);

    /// <summary>取远征配置；不存在返回 null。</summary>
    public TourInfoRow? Tour(int index) => _tours.GetValueOrDefault(index);

    /// <summary>取奖励配置；不存在返回 null。</summary>
    public TourRewardRow? Reward(int awardId) => _rewards.GetValueOrDefault(awardId);

    /// <summary>
    /// 大成功概率（**百分比**，0..100），与客户端 <c>GetPetsTourSuccessPro</c>
    /// （<c>NCafePetTour.lua:222..248</c>）逐行同构：在两个插值点之间线性插值，
    /// 两端截断；总等级为 0 时恒 0。
    /// ★ 客户端只拿它显示文本，**真正掷骰在服务端**，所以两边必须同算法，
    /// 否则玩家看到 100% 却失败。
    /// </summary>
    public static double SuccessPercent(TourInfoRow tour, int totalLv)
    {
        if (totalLv == 0 || tour.SpWinNum.Count < 2)
            return 0;

        var (minLv, minPro) = tour.SpWinNum[0];
        var (maxLv, maxPro) = tour.SpWinNum[1];

        if (totalLv >= maxLv)
            return maxPro;
        if (totalLv > minLv && maxLv > minLv)
            return minPro + (totalLv - minLv) * (double)(maxPro - minPro) / (maxLv - minLv);
        return minPro;
    }

    /// <summary>
    /// 读三张表。缺表只 WARN 并降级为空（与本仓其它表一致：不抛，让服务端仍能启动）。
    /// </summary>
    /// <param name="settingDir">配置表目录（<c>--setting-dir</c>）。</param>
    public static PetTourTable Load(string settingDir)
    {
        var table = new PetTourTable();
        table.LoadAreas(Path.Combine(settingDir, "TourArea.txt"));
        table.LoadTours(Path.Combine(settingDir, "TourInfo.txt"));
        table.LoadRewards(Path.Combine(settingDir, "TourReward.txt"));

        Log.Info($"[setting] 宠物远征：栏位 {table._areas.Count} 个、远征 {table._tours.Count} 种、" +
                 $"奖励 {table._rewards.Count} 条");
        return table;
    }

    private void LoadAreas(string path)
    {
        var t = SettingTable.Load(path);
        if (t is null)
            return;

        foreach (var row in t.Rows)
        {
            int index = t.Int(row, "Index");
            if (index <= 0)
                continue;

            _areas[index] = new TourAreaRow
            {
                Index = index,
                Rare = t.Int(row, "Rare"),
                Bg = t.Str(row, "bg"),
                Name = t.Str(row, "Name"),
                Hour = t.Int(row, "Hour"),
                EnergyCost = t.Int(row, "EnergyCost"),
                ExNum = t.Int(row, "ExNum"),
                Unlock1 = ParseInts(t.Str(row, "Unlock1")),
                Unlock2 = t.Int(row, "Unlock2"),
                Unlock3 = ParseInts(t.Str(row, "Unlock3")),
            };
        }
    }

    private void LoadTours(string path)
    {
        var t = SettingTable.Load(path);
        if (t is null)
            return;

        foreach (var row in t.Rows)
        {
            int index = t.Int(row, "Index");
            if (index <= 0)
                continue;

            _tours[index] = new TourInfoRow
            {
                Index = index,
                Rare = t.Int(row, "Rare"),
                Hour = t.Int(row, "Hour"),
                EnergyCost = t.Int(row, "EnergyCost"),
                AwardNormal = ParseInts(t.Str(row, "AwardNormal")),
                AwardSpecial = ParseInts(t.Str(row, "AwardSpecial")),
                SpWinNum = ParseGroups(t.Str(row, "SpWinNum"))
                           .Where(g => g.Count >= 2)
                           .Select(g => (g[0], g[1]))
                           .ToList(),
                Condition = ParseGroups(t.Str(row, "Condition"))
                            .Where(g => g.Count >= 3)
                            .Select(g => (g[0], g[1], g[2]))
                            .ToList(),
                RandomValue = t.Int(row, "RandomValue"),
            };
        }
    }

    private void LoadRewards(string path)
    {
        var t = SettingTable.Load(path);
        if (t is null)
            return;

        foreach (var row in t.Rows)
        {
            int id = t.Int(row, "AwardID");
            if (id <= 0)
                continue;

            if (id > MaxRewardId)
            {
                // 15 位段存不下 ⇒ 客户端 LoadBits 读回来会是截断值，宁可不收
                Log.Warn($"[setting] TourReward 奖励 id {id} 超出 15 位段上限 {MaxRewardId}，跳过");
                continue;
            }

            _rewards[id] = new TourRewardRow
            {
                AwardId = id,
                Rare = t.Int(row, "Rare"),
                Kind = t.Int(row, "AwardNormal"),
                Award = ParseInts(t.Str(row, "Award")),
                Pro = t.Int(row, "Pro"),
            };
        }
    }

    /// <summary>
    /// 把 <c>[7-1-4-2-5]</c> / <c>[1,2]</c> / <c>10</c> 里的整数抽出来（去掉一切括号，
    /// 逗号与短横都当分隔符）。
    /// ★ 本域两种分隔符**混用**（<c>Unlock1</c> 用逗号、<c>Award</c> 用短横），
    /// 所以这里一律按「非数字即分隔」处理，不像 `PartsRefitTable` 那样分两个函数。
    /// </summary>
    internal static List<int> ParseInts(string value)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        int i = 0;
        while (i < value.Length)
        {
            // 负号只有跟在分隔位后才算符号位
            bool neg = value[i] == '-' && i + 1 < value.Length && char.IsDigit(value[i + 1])
                       && (i == 0 || !char.IsDigit(value[i - 1]));
            int start = i;
            if (neg)
                i++;
            if (i < value.Length && char.IsDigit(value[i]))
            {
                while (i < value.Length && char.IsDigit(value[i]))
                    i++;
                if (int.TryParse(value.AsSpan(start, i - start), out int n))
                    result.Add(n);
                continue;
            }
            i++;
        }
        return result;
    }

    /// <summary>
    /// 解析 <c>{{10,20},{60,100}}</c> / <c>{{2,1,2},{3,3,2}}</c> 这类**嵌套组**，
    /// 每个最内层 <c>{}</c> 返回一组整数（元数不限，调用方自己判够不够）。
    /// 与 <c>CafeTable.ParsePairList</c> 同型，但那个写死了二元组，这里要吃三元组。
    /// </summary>
    internal static List<List<int>> ParseGroups(string value)
    {
        var result = new List<List<int>>();
        if (string.IsNullOrWhiteSpace(value))
            return result;

        int i = 0;
        while (i < value.Length)
        {
            int open = value.IndexOf('{', i);
            if (open < 0)
                break;
            int close = value.IndexOf('}', open + 1);
            if (close < 0)
                break;

            var body = value.Substring(open + 1, close - open - 1);
            if (body.Contains('{'))
            {
                // 外层 {{..},{..}}：跳过这一层，让内层各自匹配
                i = open + 1;
                continue;
            }

            var nums = ParseInts(body);
            if (nums.Count > 0)
                result.Add(nums);
            i = close + 1;
        }
        return result;
    }
}
