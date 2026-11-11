using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 助战（LuaCall 250 <c>AssistList</c>）—— 出击前的「选一个助战好友」列表。
///
/// <para><b>0. 为什么这条优先级最高（第十三轮档期筛查的结论）</b></para>
/// 它是**常驻系统**、不是限时活动，且上行发送点有 8 处在常驻战斗流程界面上：
/// <code>
/// UI_Formation.lua:951, 2861          编队页「确认出击前检查」
/// UI_FormationDr.lua:382, 1520        另一套编队页
/// UI_MissionSuccess.lua:1695, 2321, 2369   结算页
/// UI_MissionFailed.lua:540            失败页
/// （UI_Roguelike.lua:1050 / UI_EighthTower.lua:1047 属已过期塔活动，不影响前 8 处）
/// </code>
/// 判据：<c>ls extracted/setting/ | grep -i assist</c> **零命中**（无档期表）；
/// 上行构造点 <c>UI_Formation.lua:941..956 CheckPower</c> 的前置只有
/// <c>self:CanAssist() and not self.toggleSupportCheck.isOn</c>，**没有任何时间判断**。
/// 取证详见 `work/round13-schedule-audit.md` §3.8。
///
/// <para><b>1. ★★★ 不回包 = 玩家永远点不动「出击」</b></para>
/// 这是本域最要命的一条。<c>CheckPower</c> 把「真正开始战斗」的回调
/// **存进 <c>self.pAssistEndCall</c> 然后就发包等回来**：
/// <code>
/// UI_Formation.lua:949  self.pAssistEndCall = callBack;
/// UI_Formation.lua:951  LuaCall:CallServer(LuaCall.AssistList, tbParam);
/// UI_Formation.lua:1004 function view:ResponseAssist(tbFriend)
/// UI_Formation.lua:1006     if not self.pAssistEndCall then return end
/// UI_Formation.lua:1007     UI.Open("UI_Support", tbFriend, {endCall = self.pAssistEndCall});
/// </code>
/// ⇒ 服务端不回包，那个 <c>callBack</c> 永远不会被调用 ⇒ **点了出击没反应**。
/// 所以本域**任何情况下都必须回包**（哪怕是空列表）。
///
/// <para><b>2. ★★★ 回包是「数组」，每项的 <c>Card</c> 必须非空、且每张卡的 <c>Skill</c> 必须非空</b></para>
/// <c>UI_Support.lua:29</c> <c>for k,v in pairs(self.tbFriend) do</c> ⇒ tbParam 是数组；
/// 每项被 <c>AddOneFriend(NewItem, Info)</c> 消费（<c>:85</c>），其中：
/// <code>
/// :135  local MainCardInfo = Info.Card[1]          -- ★ 无 nil 保护
/// :137  self:ShowCardInfo(..., MainCardInfo, ...)  -- :298 直接读 Card.G/.D/.P/.L
/// :174  cardGroup.MainCharacterCard.WeaponType     -- ★ Weapon 缺失时走这里，
///                                                    MainCharacterCard 为 nil 就抛
/// :230  MainCardInfo.Skill[1].id                   -- ★★★ 直接下标！空数组必抛
/// :327  for _,v in pairs(tbSkill) do               -- 这一处空数组是安全的
/// </code>
/// ⇒ <b><c>Card</c> 至少要有 1 张、且每张的 <c>Skill</c> 至少要有 1 个元素</b>。
///
/// ★★★ <b>第十四轮真机验收更正</b>：本注释原先只列了 <c>:327</c>，据此认为
/// 「<c>Skill</c> 可以是空数组」—— **错的**。<c>:230</c> 是更强的消费点，
/// 空数组会抛并**吞掉后面整段渲染**（技能名/战力/攻防血盾），
/// 而界面仍显示预制体默认值（技能名占位符、四维 9999）⇒ 看截图完全发现不了。
/// 详见 <see cref="BuildSkills"/>。
///
/// <para><b>3. <c>type</c> 字段：0 陌生人 / 1 好友 / 2 社团</b></para>
/// <c>UI_Support.lua:30</c> <c>if v.type == 1 and not FriendLogic:IsFriend(v.id) then v.type = 0</c>
/// ⇒ ★ 客户端会**自己校正**：声明为好友但本地好友表里没有的，降级成陌生人。
/// 所以 <c>type=1</c> 必须与好友域（<see cref="FriendFeature"/>）的实际好友一致，
/// 否则玩家会看到「好友」标签闪一下变成「陌生人」。
/// 本实现直接复用好友域的机器人池与好友状态，天然一致。
///
/// <para><b>4. 上行参数（不参与筛选，只记日志）</b></para>
/// <c>UI_Formation.lua:950</c> <c>{ChapterType = self.tbRecommendAttr, Power = self.powerRequire}</c>
/// —— 原服大概率用它按「推荐属性/战力」排序助战候选。
/// 本服**不做筛选**：机器人池只有 16 人，筛完可能一个不剩，而空列表会让
/// <c>UI_Support</c> 显示「无可用助战」（<c>:78</c> <c>self.noPlayer</c>），体验更差。
///
/// <para><b>5. 错误约定：LuaCall 数字族，但本域没有失败通道</b></para>
/// <c>ResponseAssist</c> 通篇不判 <c>nError</c> ⇒ 回 <c>{nError=N}</c> 会被当成
/// 「一个含 nError 键的数组」，<c>pairs</c> 遍历时把它当助战项处理 ⇒ <c>v.type</c> 为 nil ⇒ 抛。
/// ⇒ <b>失败也只能回空数组 <c>[]</c></b>，绝不能回对象。
/// </summary>
public sealed class AssistFeature : IGameFeature
{
    /// <summary>LuaCall.AssistList = 250（<c>LuaCall.lua:245</c>「助战」）。</summary>
    public const int AssistListCmd = 250;

    /// <summary>助战项 <c>type</c>：陌生人。</summary>
    public const int TypeStranger = 0;

    /// <summary>助战项 <c>type</c>：好友。</summary>
    public const int TypeFriend = 1;

    /// <summary>助战项 <c>type</c>：同社团。</summary>
    public const int TypeClub = 2;

    /// <summary>
    /// 一次最多返回多少个助战候选。
    /// <c>UI_Support</c> 是滚动列表（<c>:80</c> <c>Scroll.totalCount</c>），没有硬上限；
    /// 取 10 是为了让「好友优先」的排序有意义又不至于列表过长。
    /// </summary>
    public const int MaxCandidates = 10;

    private GameFeatureContext _ctx = null!;
    private CharacterCardTable _cards = null!;

    public string Name => "助战（LuaCall 250 AssistList）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _cards = ctx.CharacterCards;
        ctx.MapLuaCall(AssistListCmd, OnAssistList);
    }

    /// <summary>
    /// cmd 250 请求助战列表。
    ///
    /// ★ 必须回包（类注释 §1），且必须是数组（§2）。
    /// </summary>
    internal Task OnAssistList(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // 上行只用于日志（§4）。★ 注意 param 可能是 JsonArray，直读 ["Power"] 会抛
        string reqInfo = "";
        if (param is JsonObject o)
        {
            var power = o["Power"];
            var chapterType = o["ChapterType"];
            reqInfo = $" 请求(Power={power?.ToJsonString() ?? "-"}"
                    + $" ChapterType={chapterType?.ToJsonString() ?? "-"})";
        }

        var candidates = BuildCandidates(player);

        var arr = new JsonArray();
        foreach (var c in candidates)
            arr.Add(c);

        Log.Info($"[session {session.Id}] AssistList(250) → {candidates.Count} 个助战候选"
               + $"（好友 {candidates.Count(HasFriendType)} / 社团 0 / 陌生 "
               + $"{candidates.Count - candidates.Count(HasFriendType)}）{reqInfo}");

        _ctx.LuaCalls.CallClient(session, cmd, arr);
        return Task.CompletedTask;
    }

    private static bool HasFriendType(JsonObject o)
        => o["type"] is JsonValue v && v.TryGetValue<int>(out int t) && t == TypeFriend;

    /// <summary>
    /// 造助战候选列表：复用好友域的机器人池，**好友排前面**
    /// （<c>UI_Support.lua:41..56</c> 客户端也按 好友 → 社团 → 陌生人 排序）。
    /// </summary>
    internal List<JsonObject> BuildCandidates(Player player)
    {
        var result = new List<JsonObject>();

        var friendState = FriendFeature.ReadState(player);
        uint seed = friendState.Seed != 0 ? friendState.Seed : FriendFeature.SeedOf(player.Account);

        // ★ 与好友域同一口径：--bots false 时不造机器人（生产环境只留真实玩家）。
        //   此时助战列表为空 ⇒ UI_Support 显示「无可用助战」，但**仍然回包** ⇒ 出击不会卡（§1）。
        if (!_ctx.Options.EnableBots)
            return result;

        // ★ 复用好友域的**确定性**机器人池（同账号每次都是同一批人），
        //   这样助战里标 type=1 的那些一定在好友表里，不会被客户端降级成陌生人（§3）。
        var bots = FriendFeature.BuildBots(seed, _cards);
        if (bots.Count == 0)
            return result;

        // 好友域里「已是好友」的那批 roleid（FriendState.Hy = 好友列表）
        var friendIds = friendState.Hy.ToHashSet();

        // 好友优先，其余当陌生人；各自保持池内顺序（确定性）
        foreach (var bot in bots.Where(b => friendIds.Contains(b.Roleid)))
        {
            result.Add(BuildAssistInfo(bot, TypeFriend));
            if (result.Count >= MaxCandidates) return result;
        }
        foreach (var bot in bots.Where(b => !friendIds.Contains(b.Roleid)))
        {
            result.Add(BuildAssistInfo(bot, TypeStranger));
            if (result.Count >= MaxCandidates) return result;
        }

        return result;
    }

    /// <summary>
    /// 造一个助战项。字段取证见类注释 §2/§3。
    ///
    /// ★ <c>Card</c> 至少 1 张、每张必带 <c>Skill</c>（可空数组）——
    /// 这两条是「弹出助战框瞬间抛 Lua 错」的直接原因。
    /// </summary>
    internal JsonObject BuildAssistInfo(FriendFeature.FriendBot bot, int type)
    {
        var cards = new JsonArray();
        foreach (var c in bot.Cards.Take(4))     // 主卡 1 + 副卡 3（UI_Support.lua:139 的 for i=1,3）
            cards.Add(BuildCard(c));

        // ★ 兜底：池里若某个机器人一张卡都没有，也要塞一张，否则 Info.Card[1] 为 nil ⇒ 抛
        if (cards.Count == 0)
            cards.Add(BuildFallbackCard());

        return new JsonObject
        {
            ["id"] = bot.Roleid,
            ["type"] = type,
            ["name"] = bot.RoleName,
            ["level"] = bot.Level,
            ["Card"] = cards,
            // ★ Weapon 故意不发：UI_Support.lua:174 会用
            //   Game.EquipManager.GetDefaultWeapon(MainCharacterCard.WeaponType) 兜底，
            //   而我们下发的角色卡一定在 CharacterCard.txt 里 ⇒ WeaponType 一定有效。
            //   发一把「玩家/机器人并不真正持有」的武器反而要凑 Enhance/Break 等字段。
        };
    }

    /// <summary>一张助战卡（<c>UI_Support.lua:297..323</c> 的 <c>Card</c>）。</summary>
    private JsonObject BuildCard(FriendFeature.BotCard c) => new()
    {
        ["G"] = c.G,
        ["D"] = c.D,
        ["P"] = c.P,
        ["L"] = c.L,
        // :300/:318 card.Level = Card.Enhance，且 :318 直接拼进 'Lv.'..Card.Enhance
        ["Enhance"] = c.EnhanceLevel,
        ["Break"] = c.BreakLevel,
        // :302/:303 无 nil 保护地赋给 card.RefitNum / card.SyncRate
        ["RefitNum"] = 0,
        ["SyncRate"] = 0,
        // ★★★ 必须至少 1 个元素，理由见 BuildSkills 的注释
        ["Skill"] = BuildSkills(c.G, c.D, c.P, c.L),
    };

    /// <summary>
    /// 造一张卡的 <c>Skill</c> 数组。
    ///
    /// <para><b>★★★ 为什么不能是空数组（第十四轮真机验收暴露的缺陷）</b></para>
    /// 本域原实现给每张卡发 <c>Skill = []</c>，依据是类注释 §2 引的
    /// <c>UI_Support.lua:327 for _,v in pairs(tbSkill)</c>（空数组安全）。
    /// **那次取证漏了同一文件更早的一处强消费点**：
    /// <code>
    /// UI_Support.lua:230  local id, level = MainCardInfo.Skill[1].id, MainCardInfo.Skill[1].level
    /// </code>
    /// 这是**直接下标、无 nil 保护** ⇒ 空数组必抛
    /// <c>attempt to index a nil value (field '?')</c>，且抛点在
    /// 「技能显示逻辑」段的**开头**，把后面整段渲染全部吞掉：
    /// 技能图标/名称、<c>ComputePowerValue</c> 战力、攻/防/血/盾四维都不再执行
    /// ⇒ 界面停在**预制体默认值**（技能名占位符「技能名称」、四维恒 9999）。
    ///
    /// ★★ 这就是「界面看起来渲染成功、其实报了错」的典型：截图上有卡片、有名字、
    /// 有等级、有出击按钮，唯独技能名与四维是假的。
    /// <b>判据只能是 logcat 的 <c>LuaException</c> 计数，不能靠看截图。</b>
    ///
    /// <para><b>★ 通用教训：「非 nil」≠「非空」</b></para>
    /// 同一个字段在同一个文件里可能有**强度不同**的多个消费点：
    /// <c>pairs(t)</c> 容忍空表，<c>t[1].x</c> 不容忍。
    /// 取证时必须把**全部**消费点找齐，按最强的那个定契约。
    ///
    /// <para><b>发什么</b></para>
    /// <c>SetSkillLevel</c>（<c>:325..338</c>）把本数组当作**等级覆盖表**：
    /// <c>tbSkillLevel[v.id] = v.level</c>，随后只在 id 命中该卡自身的
    /// <c>TacticalSkill / TacticalSkill2 / PassiveSkill1 / PassiveSkill2</c> 时才生效。
    /// ⇒ 必须发**这张卡自己的技能 id**：发别的 id 不仅被 <c>SetSkillLevel</c> 忽略，
    /// 还会被 <c>:230</c> 拿去 <c>SkillDescription.GetName(id)</c> 显示成**错误的技能名**。
    ///
    /// 顺序：战术技能放 <b>第一位</b> —— <c>:230</c> 取的就是 <c>[1]</c>，
    /// 而 <c>UI_Support</c> 的技能栏（<c>Card2/Item1</c>）语义上展示的是主动技能。
    /// </summary>
    internal JsonArray BuildSkills(uint g, uint d, uint p, uint l)
    {
        var arr = new JsonArray();
        var tpl = _cards.Get(g, d, p, l);

        if (tpl is not null)
        {
            // ★ 战术技能必须排第一（:230 取 [1]）。实测 269/269 张卡该列都有值。
            if (tpl.TacticalSkill > 0) arr.Add(SkillEntry(tpl.TacticalSkill));
            if (tpl.PassiveSkill1 > 0) arr.Add(SkillEntry(tpl.PassiveSkill1));
            if (tpl.PassiveSkill2 > 0) arr.Add(SkillEntry(tpl.PassiveSkill2));
        }

        // ★ 最后一道防线：模板缺失（换 dump / 缺表降级）时也**绝不能回空数组**，
        //   否则又变回「:230 抛异常、界面显示假数值」。
        //   FallbackSkillId 只是让下标存在；客户端查不到该技能只显示空名字，不抛。
        if (arr.Count == 0)
            arr.Add(SkillEntry(FallbackSkillId));

        return arr;
    }

    /// <summary>
    /// 模板缺失时占位的技能 id。
    /// <c>CS.SkillDescription.GetName(id)</c> / <c>SkillData.GetSkillIcon(id)</c>
    /// 对未知 id 只返回空/默认，不抛（与 <c>Sprite(nil)</c> 同为软失败）。
    /// </summary>
    internal const int FallbackSkillId = 0;

    /// <summary>一个技能项：<c>{id, level}</c>（<c>UI_Support.lua:230/:329</c> 只读这两个键）。</summary>
    private static JsonObject SkillEntry(int id) => new()
    {
        ["id"] = id,
        // :237 string.format("Lv.%d", level) ⇒ level 必须是数字，nil 会抛
        ["level"] = 1,
    };

    /// <summary>
    /// 兜底卡：取角色卡表里的第一张。
    /// 只在「机器人一张卡都没有」时用到（正常不会发生），是防抛的最后一道。
    /// </summary>
    private JsonObject BuildFallbackCard()
    {
        var first = _cards.All.FirstOrDefault();
        uint d = first?.D ?? 1u, p = first?.P ?? 1u, l = first?.L ?? 1u;
        return new JsonObject
        {
            ["G"] = 1u,
            ["D"] = d,
            ["P"] = p,
            ["L"] = l,
            ["Enhance"] = 1,
            ["Break"] = 0,
            ["RefitNum"] = 0,
            ["SyncRate"] = 0,
            // ★ 这里同样不能是空数组（:230）—— BuildSkills 自带兜底
            ["Skill"] = BuildSkills(1u, d, p, l),
        };
    }
}
