using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 社团日志的事件类型（<c>actionloglist[].type</c>）。
///
/// 取证 <c>extracted/lua/UI_ClubBook.lua:9..19</c>（常量声明）与 <c>:135..175</c>（渲染分支）。
/// ★ 常量表只声明到 10，但 <c>:135</c>/<c>:138</c> 的分支**已经处理 11 和 12**。
/// ★★ <c>strlist</c> 的**长度必须与 type 匹配**，否则该行被渲染成空串（不崩，但看不见）：
/// type 1/2/4/10/11/12 → 1 个；3/5/7/8/9 → 2 个；6 → **4 个**（name, name, srcpos, dstpos）。
/// </summary>
internal static class ClubLogType
{
    /// <summary><c>%s创建了社团</c>（strlist 长度 1）。</summary>
    public const int Create = 1;

    /// <summary><c>%s自动加入了社团</c>（长度 1）。</summary>
    public const int AutoJoin = 2;

    /// <summary><c>%s被%s批准加入了社团</c>（长度 2）。</summary>
    public const int RatifyJoin = 3;

    /// <summary><c>%s退出了社团</c>（长度 1）。</summary>
    public const int Quit = 4;

    /// <summary><c>%s被%s请离出了社团</c>（长度 2）。</summary>
    public const int Kick = 5;

    /// <summary><c>%s将%s从%s调整为%s</c>（长度 **4**，后两位是职位数字，客户端自己翻译）。</summary>
    public const int ChangePos = 6;

    /// <summary><c>%s将社长转让%s</c>（长度 2）。</summary>
    public const int ChangeHeader = 7;

    /// <summary><c>%s修改社团通知为：%s</c>（长度 2，第 2 位过 <c>TextFilter</c>）。</summary>
    public const int ModifyNotice = 8;

    /// <summary><c>%s申请解散社团</c>（长度 1）。</summary>
    public const int ReqDissolve = 11;

    /// <summary><c>%s取消解散社团</c>（长度 1）。</summary>
    public const int CancelDissolve = 12;
}

/// <summary>
/// 社团成员（<c>memberlist</c> / <c>applylist</c> 的元素，两者同构）。
///
/// <para><b>★★ 键名是 <c>pos</c>，不是 <c>post</c></b></para>
/// <c>LuaCall.lua:2130</c> 的原服注释块写的是 <c>post = 1</c>，那是**旧版**：
/// 运行时全部读 <c>member.pos</c>（<c>UI_Club.lua:196</c>/<c>:307</c>、
/// <c>UI_ClubMember.lua:82</c>/<c>:150</c>/<c>:243..244</c>/<c>:562..579</c>、<c>ClubCommon.lua:366</c>），
/// 值域是 <c>9=社长 / 8=副社长 / 0=普通</c>（<c>ClubCommon.lua:4..6</c>）。
/// 按注释块照抄 = 社长图标不亮、三个管理按钮权限全错。
///
/// <para><b>五个「漏了就抛 Lua 错误」的字段</b></para>
/// <c>level</c>（<c>UI_ClubMember.lua:250</c> <c>"LV." .. data.level</c>）、
/// <c>pos</c> / <c>state</c> / <c>last7_contribute</c>（<c>:147..167</c> 的 <c>table.sort</c> 比较 nil）、
/// <c>off_times</c>（<c>:308</c> <c>GetTime() - off_times</c>）。
/// 另外 <c>headframeid</c> 必须能映射到 <c>CardCommon:GetAllHeadConfig()</c> 里的项 ——
/// <c>:227..235</c> 刚做过 nil 判断，<c>:237</c> 却无条件 <c>Sprite(headCfg.Icon)</c>；
/// 安全值是 <b>0</b>（<c>CardCommon:GetBorderID(0)</c> 返回 1，而 1 一定在 <c>HeadFrame.txt</c> 里，
/// <c>CardCommon.lua:862..867</c>）。
/// </summary>
internal sealed class ClubMemberState
{
    /// <summary>玩家 id（<c>roleid</c>）。NPC 成员用生成器分配的假 id。</summary>
    public ulong Roleid { get; set; }

    /// <summary>
    /// 玩家名（<c>name</c>）。★ <c>"Not Active"</c> 是**保留值**：
    /// <c>UI_ClubMember.lua:689</c> 判 <c>name == "Not Active" and level == 1</c> 时
    /// 显示成「未激活」并隐藏等级，别当普通名字用。
    /// </summary>
    public string Name { get; set; } = "";

    /// <summary>玩家等级（<c>level</c>）。</summary>
    public int Level { get; set; } = 1;

    /// <summary>职位（<c>pos</c>）：9 社长 / 8 副社长 / 0 普通。</summary>
    public int Pos { get; set; }

    /// <summary>在线状态（<c>state</c>）：1 在线 / 0 离线。</summary>
    public int State { get; set; }

    /// <summary>头像用的角色卡 id（<c>girliconid</c>）→ <c>Npc/icon_n_girl%d.png</c>（<c>CardCommon.lua:547</c>）。</summary>
    public uint GirlIconId { get; set; } = 1;

    /// <summary>头像框 id（<c>headframeid</c>）。安全值 0，见类注释。</summary>
    public int HeadFrameId { get; set; }

    /// <summary>最后离线时间（<c>off_times</c>，unix 秒）。<b>0 = 显示「2个月前」</b>（<c>UI_ClubMember.lua:309</c>）。</summary>
    public long OffTimes { get; set; }

    /// <summary>近 7 日贡献（<c>last7_contribute</c>），成员列表的「周活跃」列 + 排序键。</summary>
    public int Last7Contribute { get; set; }

    /// <summary>当日贡献（<c>daily_contribute</c>）。本包 UI 零读取，填 0。</summary>
    public int DailyContribute { get; set; }

    /// <summary>
    /// 列表皮肤 id（<c>listskinid</c>）。★ 必须是 <b>0</b> 或 <c>CardCommon.tbBoardConfig</c> 里的合法 id：
    /// <c>UI_ClubMember.lua:214..224</c> 有 <c>if id &gt; 0</c> 守卫，但守卫内部
    /// <c>CardCommon.tbBoardConfig[id].bg</c> 无 nil 保护。
    /// </summary>
    public int ListSkinId { get; set; }

    /// <summary>入团时间（<c>join_date</c>，unix 秒）。UI 零读取，服务端用它算入团时长限制。</summary>
    public long JoinDate { get; set; }

    public JsonObject ToStorage() => new()
    {
        ["roleid"] = Roleid,
        ["name"] = Name,
        ["level"] = Level,
        ["pos"] = Pos,
        ["state"] = State,
        ["girliconid"] = GirlIconId,
        ["headframeid"] = HeadFrameId,
        ["offtimes"] = OffTimes,
        ["last7"] = Last7Contribute,
        ["daily"] = DailyContribute,
        ["listskinid"] = ListSkinId,
        ["joindate"] = JoinDate,
    };

    public static ClubMemberState FromStorage(JsonObject o) => new()
    {
        Roleid = ClubJson.ReadULong(o["roleid"]),
        Name = ClubJson.ReadString(o["name"]),
        Level = ClubJson.ReadInt(o["level"], 1),
        Pos = ClubJson.ReadInt(o["pos"], 0),
        State = ClubJson.ReadInt(o["state"], 0),
        GirlIconId = (uint)Math.Max(0, ClubJson.ReadInt(o["girliconid"], 1)),
        HeadFrameId = ClubJson.ReadInt(o["headframeid"], 0),
        OffTimes = ClubJson.ReadLong(o["offtimes"], 0),
        Last7Contribute = ClubJson.ReadInt(o["last7"], 0),
        DailyContribute = ClubJson.ReadInt(o["daily"], 0),
        ListSkinId = ClubJson.ReadInt(o["listskinid"], 0),
        JoinDate = ClubJson.ReadLong(o["joindate"], 0),
    };
}

/// <summary>社团日志的一条（<c>{type, strlist, time}</c>，取证 <c>UI_ClubBook.lua:128..181</c>）。</summary>
/// <param name="Type">事件类型，见 <see cref="ClubLogType"/>。</param>
/// <param name="Strings">格式化参数（**长度必须与 Type 匹配**，见 <see cref="ClubLogType"/>）。</param>
/// <param name="Time">发生时间（unix 秒），客户端按 <c>yyyy-MM-dd</c> 分组、<c>HH:mm</c> 显示。</param>
internal sealed record ClubLogEntry(int Type, IReadOnlyList<string> Strings, long Time)
{
    public JsonObject ToStorage()
    {
        var arr = new JsonArray();
        foreach (var s in Strings)
            arr.Add(s);
        return new JsonObject { ["type"] = Type, ["str"] = arr, ["time"] = Time };
    }

    public static ClubLogEntry FromStorage(JsonObject o)
    {
        var strings = new List<string>();
        if (o["str"] is JsonArray arr)
        {
            foreach (var n in arr)
                strings.Add(ClubJson.ReadString(n));
        }
        return new ClubLogEntry(ClubJson.ReadInt(o["type"], 1), strings, ClubJson.ReadLong(o["time"], 0));
    }
}

/// <summary>
/// 一个社团的服务端权威状态。落盘在 <c>Player.Extra["club"]["club"]</c>
/// （<see cref="ClubBag"/>），下行形状见 <see cref="ClubFeature.BuildClubPayload"/>。
///
/// <para><b>为什么存在玩家存档里而不是全局 <c>ClubStore</c></b></para>
/// 取证文档 §7.3：<c>PlayerStore</c> 是「每账号一个 JSON 文件」（<c>PlayerStore.cs:94..98</c>），
/// <c>World</c> 三张表全是在线会话映射、没有全局状态口子（<c>World.cs:18..60</c>），
/// 而且**没有 roleid → account 的反查表**（<c>World.DeriveRoleId</c> 是单向哈希，<c>World.cs:159</c>）
/// ⇒ 跨玩家共享社团需要新基础设施。单人本地服里「社团」就是这个玩家的私有对象，
/// 存 <c>Extra["club"]</c> 零新增基础设施（<c>Extra</c> 已随存档 round-trip，
/// <c>PlayerStore.cs:149..150</c>/<c>:173</c>/<c>:210..212</c>）。
/// 迁移面刻意收在 <see cref="ClubBag.Read"/> / <see cref="ClubBag.Write"/> 两处。
///
/// ★ 即便如此，成员快照（name/level/girliconid/headframeid）仍然**存在社团对象里**，
/// 而不是「按需去读那个玩家的存档」—— 这样将来换成 <c>ClubStore</c> 时 API 不用改。
/// </summary>
internal sealed class ClubState
{
    /// <summary>社团 id（<c>primary.id</c>）。分配规则见 <see cref="ClubFeature.OwnClubIdOf"/>。</summary>
    public ulong Id { get; set; }

    /// <summary>社团名（<c>primary.name</c>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>社团等级（<c>primary.level</c>，1..10）。由 <see cref="Exp"/> 反算，见 <c>ClubTable.LevelForExp</c>。</summary>
    public int Level { get; set; } = 1;

    /// <summary>
    /// 社团经验 / 发展指数（<c>primary.exp</c>）。
    /// ★ 唯一来源是道具 <c>15-13</c>「团队指数」（<c>ClassName = ClubExpAuto</c>），
    /// 见 <see cref="ClubFeature.SplitIndexAwards"/>。
    /// </summary>
    public int Exp { get; set; }

    /// <summary>
    /// 社团活跃点。来源是道具 <c>15-12</c>「活跃指数」（<c>ClassName = ClubActivePointAuto</c>），
    /// 累到 <c>ClubMissionAward.Point</c> 档位（500/1200/2000/3000/4000）可再领奖。
    /// ★ 客户端**不读**这个字段（周任务进度用的是 <see cref="WeekTaskValue"/>），
    /// 服务端存着是为了将来接 <c>ClubProgressGetAward</c>。
    /// </summary>
    public int ActivePoint { get; set; }

    /// <summary>允许申请的最低玩家等级（<c>primary.applylevel</c>，1..999）。</summary>
    public int ApplyLevel { get; set; } = 1;

    /// <summary>自动准入等级（<c>primary.autojoinlevel</c>，1..999，≥ <see cref="ApplyLevel"/>）。</summary>
    public int AutoJoinLevel { get; set; } = 1;

    /// <summary>
    /// 社团公告（<c>primary.notice</c>，<c>GetStringLen</c> ≤ 140）。
    /// ★ **不能是 null**：<c>UI_Club.lua:411</c> 的 <c>string.len(tbClubInfo.notice)</c> 无 nil 保护
    /// （空串是安全的）。
    /// </summary>
    public string Notice { get; set; } = "";

    /// <summary>
    /// 申请解散的 unix 时间戳（<c>primary.reqdissolvetime</c>），**0 = 未申请**。
    /// ★★★ 必填字段：<c>UI_ClubMember.lua:104</c> 的 <c>reqdissolvetime &gt; 0</c> 对 nil 直接抛。
    /// </summary>
    public long ReqDissolveTime { get; set; }

    /// <summary>今日已发社团邮件次数（<c>primary.send_mail_times</c>），上限 10（<c>ClubCommon.lua:27</c>）。</summary>
    public int SendMailTimes { get; set; }

    /// <summary><see cref="SendMailTimes"/> 计在哪一天（<c>CafeFeature.CafeDayIndex</c> 的 4 点日序）。</summary>
    public int SendMailDay { get; set; } = int.MinValue;

    /// <summary>
    /// 本周社团任务积分（<c>primary.weektaskvalue</c>）。
    /// ★ <c>UI_ClubTask.lua:229/230/242/276/291</c> **全部无 nil 保护**，必须下发。
    /// </summary>
    public int WeekTaskValue { get; set; }

    /// <summary>社团创建时间（unix 秒），用于第一条日志与「成立多久」。</summary>
    public long CreateTime { get; set; }

    /// <summary>上一次**申请解散**的时间（unix 秒）：<c>ui.Club.DissolveLimit</c>「24小时内只能申请解散社团一次」。</summary>
    public long LastDissolveReqTime { get; set; }

    /// <summary>社长 roleid（<c>primary.headerid</c>）。UI 未直读（改判 <c>member.pos == 9</c>），但建议填。</summary>
    public ulong HeaderId { get; set; }

    /// <summary>社长名字（<c>primary.headername</c>）。★ 原服注释块里没有它，但 <c>UI_Club.lua:202</c> 会显示。</summary>
    public string HeaderName { get; set; } = "";

    /// <summary>The club war's saved state, owned by ClubBossHandler. Null until the first war action.</summary>
    public JsonObject? BossData { get; set; }

    /// <summary>成员列表（<c>memberlist</c>）。★ 多处 <c>pairs()</c> 无 nil 保护，至少要有自己。</summary>
    public List<ClubMemberState> Members { get; } = new();

    /// <summary>社团日志（<c>actionloglist</c>），**服务端裁到 50 条**（<c>ui.Club.BookTip</c>「保留最新的50条」）。</summary>
    public List<ClubLogEntry> Logs { get; } = new();

    /// <summary>日志条数上限（<c>ui.Club.BookTip</c>）。</summary>
    public const int MaxLogs = 50;

    /// <summary>取某个 roleid 的成员；不存在返回 null。</summary>
    public ClubMemberState? Member(ulong roleid) => Members.FirstOrDefault(m => m.Roleid == roleid);

    /// <summary>追加一条日志并裁到 <see cref="MaxLogs"/> 条（保留最新的）。</summary>
    public void AddLog(int type, long time, params string[] strings)
    {
        Logs.Add(new ClubLogEntry(type, strings, time));
        if (Logs.Count > MaxLogs)
            Logs.RemoveRange(0, Logs.Count - MaxLogs);
    }

    public JsonObject ToStorage()
    {
        var members = new JsonArray();
        foreach (var m in Members)
            members.Add(m.ToStorage());
        var logs = new JsonArray();
        foreach (var l in Logs)
            logs.Add(l.ToStorage());

        return new JsonObject
        {
            ["id"] = Id,
            ["name"] = Name,
            ["level"] = Level,
            ["exp"] = Exp,
            ["activepoint"] = ActivePoint,
            ["applylevel"] = ApplyLevel,
            ["autojoinlevel"] = AutoJoinLevel,
            ["notice"] = Notice,
            ["reqdissolvetime"] = ReqDissolveTime,
            ["sendmailtimes"] = SendMailTimes,
            ["sendmailday"] = SendMailDay,
            ["weektaskvalue"] = WeekTaskValue,
            ["createtime"] = CreateTime,
            ["lastdissolvereq"] = LastDissolveReqTime,
            ["headerid"] = HeaderId,
            ["headername"] = HeaderName,
            ["members"] = members,
            ["logs"] = logs,
            ["boss"] = BossData?.DeepClone(),
        };
    }

    public static ClubState FromStorage(JsonObject o)
    {
        var state = new ClubState
        {
            Id = ClubJson.ReadULong(o["id"]),
            Name = ClubJson.ReadString(o["name"]),
            Level = ClubJson.ReadInt(o["level"], 1),
            Exp = ClubJson.ReadInt(o["exp"], 0),
            ActivePoint = ClubJson.ReadInt(o["activepoint"], 0),
            ApplyLevel = ClubJson.ReadInt(o["applylevel"], 1),
            AutoJoinLevel = ClubJson.ReadInt(o["autojoinlevel"], 1),
            Notice = ClubJson.ReadString(o["notice"]),
            ReqDissolveTime = ClubJson.ReadLong(o["reqdissolvetime"], 0),
            SendMailTimes = ClubJson.ReadInt(o["sendmailtimes"], 0),
            SendMailDay = ClubJson.ReadInt(o["sendmailday"], int.MinValue),
            WeekTaskValue = ClubJson.ReadInt(o["weektaskvalue"], 0),
            CreateTime = ClubJson.ReadLong(o["createtime"], 0),
            LastDissolveReqTime = ClubJson.ReadLong(o["lastdissolvereq"], 0),
            HeaderId = ClubJson.ReadULong(o["headerid"]),
            HeaderName = ClubJson.ReadString(o["headername"]),
            BossData = o["boss"]?.DeepClone() as JsonObject,
        };

        if (o["members"] is JsonArray members)
        {
            foreach (var n in members)
            {
                if (n is JsonObject mo)
                    state.Members.Add(ClubMemberState.FromStorage(mo));
            }
        }
        if (o["logs"] is JsonArray logs)
        {
            foreach (var n in logs)
            {
                if (n is JsonObject lo)
                    state.Logs.Add(ClubLogEntry.FromStorage(lo));
            }
        }
        return state;
    }
}

/// <summary>
/// <c>Player.Extra["club"]</c> 的整体形状。
///
/// <code>
/// { "club": { …ClubState… } | 缺失,     // 缺失/null = 这个玩家没有社团
///   "applied": [ 100001, 100205 ] }     // 申请中（等审批）的社团 id
/// </code>
///
/// ★ <see cref="Applied"/> 必须与 <see cref="Club"/> 分开存：申请发生在「还没社团」的时候，
/// 那时 <c>club</c> 是缺失的。
///
/// ★★ <b>「离团时间」不在这里</b> —— 它存任务值 <c>(1,28)</c>，因为客户端
/// <c>UI_Search.lua:83</c>/<c>:111</c> 直接读 <c>me:GetTaskValue(1, 28)</c> 显示再入团倒计时。
/// </summary>
internal sealed class ClubBag
{
    /// <summary><see cref="Player.Extra"/> 里本模块的 key（取证文档 §7.3 确认该键当前空闲）。</summary>
    internal const string ExtraKey = "club";

    /// <summary>本玩家所在的社团；null = 没有社团。</summary>
    public ClubState? Club { get; set; }

    /// <summary>申请中的社团 id（等审批，永远不会被通过 —— 那些社团是确定性假数据）。</summary>
    public List<ulong> Applied { get; } = new();

    /// <summary>
    /// 从存档读出来。任何异常/畸形结构都降级成「空袋子」而不是抛 ——
    /// 存档是本地文件，手改坏了也不能让登录失败。
    /// </summary>
    public static ClubBag Read(Player player)
    {
        var bag = new ClubBag();
        if (!player.Extra.TryGetValue(ExtraKey, out var node) || node is not JsonObject root)
            return bag;

        try
        {
            if (root["club"] is JsonObject club)
                bag.Club = ClubState.FromStorage(club);
            if (root["applied"] is JsonArray applied)
            {
                foreach (var n in applied)
                {
                    ulong id = ClubJson.ReadULong(n);
                    if (id != 0 && !bag.Applied.Contains(id))
                        bag.Applied.Add(id);
                }
            }
        }
        catch (Exception e)
        {
            Log.Warn($"[club] Extra[\"{ExtraKey}\"] 结构异常，本次按「没有社团」处理：{e.Message}");
            return new ClubBag();
        }
        return bag;
    }

    /// <summary>
    /// 写回存档（**不落盘**，调用方负责 <c>ctx.Store.Save(player)</c>）。
    ///
    /// ★★★ 同时把 <see cref="Player.ClubId"/> 写成 <c>Club?.Id ?? 0</c> ——
    /// 这是取证文档 §8.5 的**硬约束**，不是保险：<c>GameModule.cs:590</c> 已无条件把
    /// <c>ClubId</c> 塞进 pf 1005 <c>PlayerNtf.clubid</c>，一旦 <c>ClubId != 0</c> 而
    /// 10002 回不出完整社团对象，<c>UI_ClubReward.lua:35</c> 的
    /// <c>next(ClubCommon.MyClubInfo)</c> 就会 <c>next(nil)</c> 抛
    /// （<c>bad argument #1 to 'next' (table expected, got nil)</c>），
    /// <c>DropWayRegister.lua:596</c> 的 <c>me.clubid &gt; 0</c> 分支还会再抛一次。
    /// 把两者收进同一个方法，是让「原子性」在代码层面无法被绕过的唯一办法。
    /// </summary>
    public void Write(Player player)
    {
        var root = new JsonObject();
        if (Club is not null)
            root["club"] = Club.ToStorage();
        var applied = new JsonArray();
        foreach (var id in Applied)
            applied.Add(id);
        root["applied"] = applied;

        player.Extra[ExtraKey] = root;
        player.ClubId = Club?.Id ?? 0;
    }
}

/// <summary>
/// 存档 / 上行 JSON 的宽容读取小工具（两者都不可控，走异常路径会把日志刷满）。
///
/// ★★ 为什么要自己写一套而不是直接 <c>GetValue&lt;int&gt;()</c>：
/// <c>JsonValue.TryGetValue&lt;T&gt;</c> 对**内存里现造的**节点是**按装箱类型严格匹配**的 ——
/// <c>ToStorage()</c> 里写 <c>["girliconid"] = m.GirlIconId</c>（<c>uint</c>）之后，
/// <c>TryGetValue&lt;int&gt;</c>、<c>TryGetValue&lt;long&gt;</c>、<c>TryGetValue&lt;double&gt;</c> **全部返回 false**，
/// 于是读回来是 fallback（实测把 <c>girliconid=7</c> 静默读成 1）。
/// 而经 <c>PlayerStore</c> 落盘再解析出来的节点是 <c>JsonElement</c> 支撑的、数值转换正常
/// ⇒ 这个 bug **只在「同一进程内写完立刻读」时出现**，而那恰好是本域最高频的路径
/// （<c>bag.Write(player)</c> → 下一条命令 <c>ClubBag.Read(player)</c>）。
/// 所以数值一律先归一到 <see cref="Number"/> 再夹取。
/// </summary>
internal static class ClubJson
{
    /// <summary>
    /// 把任意 JSON 标量归一成 <see cref="decimal"/>（整数精度到 ~7.9e28，覆盖 <see cref="ulong"/> 全域）。
    /// 非标量 / 解不出数字返回 null。
    /// </summary>
    private static decimal? Number(JsonNode? node)
    {
        if (node is not JsonValue v)
            return null;
        try
        {
            if (v.TryGetValue(out int i))
                return i;
            if (v.TryGetValue(out long l))
                return l;
            if (v.TryGetValue(out uint ui))
                return ui;
            if (v.TryGetValue(out ulong ul))
                return ul;
            if (v.TryGetValue(out decimal dec))
                return dec;
            if (v.TryGetValue(out double d))
                return (decimal)d;
            if (v.TryGetValue(out bool b))
                return b ? 1 : 0;
            if (v.TryGetValue(out string? s) && decimal.TryParse(s, out var sd))
                return sd;
            return decimal.TryParse(v.ToString(), out var td) ? td : null;
        }
        catch (Exception)
        {
            // OverflowException（超大 double → decimal）等一律当「解不出数字」
            return null;
        }
    }

    public static int ReadInt(JsonNode? node, int fallback)
    {
        var n = Number(node);
        if (n is null)
            return fallback;
        if (n.Value <= int.MinValue)
            return int.MinValue;
        if (n.Value >= int.MaxValue)
            return int.MaxValue;
        return (int)n.Value;
    }

    public static long ReadLong(JsonNode? node, long fallback)
    {
        var n = Number(node);
        if (n is null)
            return fallback;
        if (n.Value <= long.MinValue)
            return long.MinValue;
        if (n.Value >= long.MaxValue)
            return long.MaxValue;
        return (long)n.Value;
    }

    public static ulong ReadULong(JsonNode? node)
    {
        var n = Number(node);
        if (n is null || n.Value <= 0)
            return 0;
        if (n.Value >= ulong.MaxValue)
            return ulong.MaxValue;
        return (ulong)n.Value;
    }

    /// <summary>读字符串；非字符串节点返回其 JSON 文本，null 返回空串（**永不返回 null**）。</summary>
    public static string ReadString(JsonNode? node)
    {
        if (node is null)
            return "";
        return node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString();
    }
}
