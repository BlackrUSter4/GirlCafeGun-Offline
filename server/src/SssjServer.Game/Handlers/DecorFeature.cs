using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 个性化装扮六条命令：头像框 / 名片皮肤 / 列表标识 / 聊天气泡 / 主城主题装饰 / 主城背景。
///
/// <para><b>0. 命令面</b></para>
/// <list type="table">
///   <item><term>70 <c>Player_SetHeadFrame</c></term><description>设置头像边框（<c>LuaCall.lua:1142</c>）</description></item>
///   <item><term>91 <c>Player_SetVisitingCard</c></term><description>设置名片皮肤（<c>:1154</c>）</description></item>
///   <item><term>92 <c>Player_SetPlayerListSkin</c></term><description>设置玩家列表个性装扮皮肤（<c>:1166</c>）</description></item>
///   <item><term>93 <c>Player_SetChatBubble</c></term><description>设置聊天气泡（<c>:1178</c>）</description></item>
///   <item><term>95 <c>ChangeDecorate</c></term><description>修改主城主题场景 + 装饰开关（<c>:1234</c>）</description></item>
///   <item><term>96 <c>ChangeBackground</c></term><description>修改主城主题背景（<c>:1244</c>）</description></item>
/// </list>
/// 配套的 94 <c>Player_GetVisitingCardData</c> 早已实现（<see cref="MainCityFeature"/>），
/// 本域只补 setter —— 所以「存哪里」必须与 94 的读取口径**完全一致**，否则改了读不出来。
///
/// <para><b>1. ★★★ 上行是「裸标量」，不是对象 —— 本域最容易写错的地方</b></para>
/// 70/91/92/93 四条的上行参数**直接就是一个数字**，没有外层字段名：
/// <code>
/// UI_SkinSelected.lua:164  LuaCall:CallServer(LuaCall.Player_SetVisitingCard,   self.selectSkinId)
/// UI_SkinSelected.lua:168  LuaCall:CallServer(LuaCall.Player_SetPlayerListSkin, self.selectlistSkinId)
/// UI_SkinSelected.lua:172  LuaCall:CallServer(LuaCall.Player_SetChatBubble,     self.selectchatBubbleId)
/// UI_HeadPortrait.lua:310  LuaCall:CallServer(LuaCall.Player_SetHeadFrame,      self.CurSelectedHeadFrame)
/// CardCommon.lua:1020/1054/1060/1066                （客户端自查过期后反手脱下：实参 1 / 0 / 0 / 0）
/// </code>
/// ⇒ 解析必须走 <see cref="ReadScalarId"/>（认 <c>JsonValue</c>），
/// 写成 <c>param?["id"]</c> 会**恒得 null ⇒ 恒当非法参数拒绝**。
/// ★ 而 95/96 相反，它们的上行**是对象/数组**（见 §5）—— 同一批命令里两种形状，别一刀切。
///
/// <para><b>2. 错误约定：LuaCall 数字族（<c>nError</c> 真值判断）</b></para>
/// 四条 setter 的消费点都是同一形状（<c>UI_User.lua:1262/1296/1317/1328</c>）：
/// <code>
/// if tbParam and tbParam.nError then Game.MessageBox.Simple(Text('ui.Error.' .. tbParam.nError))
/// </code>
/// ⇒ 成功**绝不能带 <c>nError</c>**（Lua 里 <c>0</c> 也是真值，带 0 会弹错误框）。
/// 见 AGENTS.md §5 那张七种约定的表。
///
/// <para><b>3. ★★ 70 的成功回包是「裸数组」<c>[headFrameId]</c></b></para>
/// <c>UI_User.lua:1265</c> <c>elseif tbParam and #tbParam &gt; 0 then ... tbParam[1] &gt; 0</c>
/// ⇒ 回对象 <c>{id=N}</c> 会让 <c>#tbParam == 0</c>、整个分支跳过（**静默不刷新**）。
/// 91/92/93 的消费点则是空实现（<c>:1321</c>/<c>:1332</c> 的 <c>elseif tbParam then</c> 里什么都不做），
/// 回什么都行 —— 但为了让 <c>UI_User:OnChangeSkin</c>（<c>:1306</c> 读
/// <c>ChatBubbleID</c>/<c>PlayerListSkinID</c>）也能用，统一回 94 那份三件套对象。
///
/// <para><b>4. ★★★ 任务值里存的是「获得时间戳」，不是「有没有」</b></para>
/// 这是本域第二个陷阱，取证与后果见 <see cref="DecorRow.Type"/>。
/// 拥有记录 group：头像框 <b>11</b> / 名片 <b>76</b> / 标识 <b>77</b> / 气泡 <b>78</b>。
/// 本域**只校验拥有、不发放**（发放由 GM 全解锁与礼包链负责，见 <see cref="GmUnlock"/>）。
///
/// <para><b>5. 95/96 主城主题：全部存任务值 group 87，偏移是「魔数」</b></para>
/// 取证 <c>MainOrnament.lua</c>（<c>MainOrnament.TaskGroup = 87</c>，<c>:4</c>）：
/// <code>
/// :112  当前主题   = me:GetTaskValue(87, 0)        -- 0 时客户端自己当 1
/// :115  显示装饰   = me:GetTaskValue(87, 1000) &gt; 0
/// :175  当前背景   = me:GetTaskValue(87, 2000)     -- 0 时客户端自己当 1
/// :121  永久拥有   = me:GetTaskValue(87, id + 20000) &gt;= 0
/// :128  获得时间   = me:GetTaskValue(87, id)       -- &lt;= 0 视为未拥有
/// </code>
/// 上行形状（与 §1 的裸标量不同）：
/// <code>
/// UI_MainThemeTip.lua:206  ChangeDecorate,   {{type = 1, id = 主题id}, {type = 2, id = 装饰id或0}}
/// UI_MainThemeTip.lua:216  ChangeBackground, {id = 0}
/// UI_SpineShow.lua:50      ChangeBackground, {id = BGId}
/// </code>
/// ★ <c>type=2</c> 的 <c>id</c> 是**开关语义**：客户端传「要显示就填主题 id、不显示填 0」
/// （<c>:202..205</c>），服务端只需把它按 <c>&gt; 0</c> 存成偏移 1000 的布尔。
///
/// <para><b>6. ★★ 95/96 的回包客户端「不读任何字段」</b></para>
/// <c>LuaCall.lua:1239/1249</c> 的客户端分支是 <c>MainOrnament:UpdateOrnament()</c> /
/// <c>UpdateSkinBg()</c>，**都不传参**（<c>:247</c>/<c>:198</c> 的形参因此为 nil）
/// ⇒ 它们转头去读 <b>任务值</b>（<c>:112</c>/<c>:175</c>）。
/// ⇒ **必须先推 pf 1026 再回包**，否则界面读到的是旧值（与 <c>docs/05</c> §22.2
/// 「回包 handler 不收参数 ⇒ 数值必须靠推送先到」完全同型）。
///
/// <para><b>7. ★ 头像框当前值存 protobuf、本次会话内不回推</b></para>
/// <c>me.headframe</c> 是 <c>Game.Player</c> 的属性，<c>set_headframe</c> 是 <b>private</b>
/// 且 <b>0 个调用者</b>（<c>dump.cs:558077</c> 区段，RVA <c>0x1853600</c>）；
/// 真正的赋值在 <c>Game.Player.Login</c> 内联直写 <c>+0x40</c>
/// （<c>0x0185433C has_headframe</c> → <c>0x01854358 get_headframe</c> → <c>0x01854378 str w23, [x22, #0x40]</c>）
/// ⇒ <b>它只在登录时从 <c>PlayerNtf.headframe</c> 更新</b>。
/// 所以服务端持久化到 <see cref="Player.HeadFrame"/> 即可（下次登录生效），
/// 本次会话内 <c>UI_User</c> 靠 70 的回包自己重绘（§3）。**这就是原厂行为，不是缺陷。**
/// </summary>
public sealed class DecorFeature : IGameFeature
{
    /// <summary>LuaCall.Player_SetHeadFrame = 70（<c>LuaCall.lua:68</c>「设置头像边框」）。</summary>
    public const int SetHeadFrameCmd = 70;

    /// <summary>LuaCall.Player_SetVisitingCard = 91（<c>:91</c>「设置名片皮肤」）。</summary>
    public const int SetVisitingCardCmd = 91;

    /// <summary>LuaCall.Player_SetPlayerListSkin = 92（<c>:92</c>「设置玩家列表个性装扮皮肤」）。</summary>
    public const int SetPlayerListSkinCmd = 92;

    /// <summary>LuaCall.Player_SetChatBubble = 93（<c>:93</c>「设置聊天气泡」）。</summary>
    public const int SetChatBubbleCmd = 93;

    /// <summary>LuaCall.ChangeDecorate = 95（<c>:96</c>「修改主题场景、装饰」）。</summary>
    public const int ChangeDecorateCmd = 95;

    /// <summary>LuaCall.ChangeBackground = 96（<c>:97</c>「修改主题背景」）。</summary>
    public const int ChangeBackgroundCmd = 96;

    /// <summary>主城主题装饰的任务值 group（<c>MainOrnament.TaskGroup</c>，<c>MainOrnament.lua:4</c>）。</summary>
    public const int OrnamentGroup = 87;

    /// <summary>偏移 0 = 当前主题 id（<c>MainOrnament.lua:112</c>）。</summary>
    public const int OrnamentCurrentTheme = 0;

    /// <summary>偏移 1000 = 是否显示 UI 装饰（<c>:115</c>，<c>&gt; 0</c> 为真）。</summary>
    public const int OrnamentShowDecorate = 1000;

    /// <summary>偏移 2000 = 当前背景 id（<c>:175</c>）。</summary>
    public const int OrnamentCurrentBg = 2000;

    /// <summary>偏移基址 20000：<c>id + 20000</c> = 该主题是否永久拥有（<c>:121</c>）。</summary>
    public const int OrnamentPermanentBase = 20000;

    /// <summary>
    /// 通用失败码。★ 客户端把它拼成 <c>ui.Error.&lt;码&gt;</c> 查本地化文案，
    /// 未配置的码会显示成原始 key —— 但**有回包**比「静默无反应」好得多，
    /// 且本域四条 setter 的失败路径玩家极难触发（界面自己先过一遍 <c>IsUnlock</c>）。
    /// </summary>
    public const int ErrGeneric = 1;

    private GameFeatureContext _ctx = null!;
    private DecorTable _decor = new();
    private int[] _themeIds = Array.Empty<int>(), _backgroundIds = Array.Empty<int>();

    /// <summary>Offset of background ownership in group 87: <c>GetTaskValue(87, id + 2000) &gt; 0</c> (MainOrnament.lua:154).</summary>
    public const int OrnamentBackgroundBase = 2000;

    public string Name => "个性化装扮（LuaCall 70 头像框 / 91 名片 / 92 标识 / 93 气泡 / 95 主题 / 96 背景）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _decor = DecorTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(SetHeadFrameCmd, OnSetHeadFrame);
        ctx.MapLuaCall(SetVisitingCardCmd, OnSetVisitingCard);
        ctx.MapLuaCall(SetPlayerListSkinCmd, OnSetPlayerListSkin);
        ctx.MapLuaCall(SetChatBubbleCmd, OnSetChatBubble);
        ctx.MapLuaCall(ChangeDecorateCmd, OnChangeDecorate);
        ctx.MapLuaCall(ChangeBackgroundCmd, OnChangeBackground);

        // Every home-screen theme and background is unlocked. Nothing in this server sells or awards them, so without
        // this they all show a lock, the default theme included (MainOrnament:IsUseTheme needs (87, id) > 0).
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        _themeIds = OrnamentIds(Path.Combine(directory, "MainOrnament.txt"), 1);
        _backgroundIds = OrnamentIds(Path.Combine(directory, "MainSkinBg.txt"), 2);
        ctx.OnPlayerPrepare((session, player) => UnlockOrnaments(player, _themeIds, _backgroundIds));
        // ★ 不在这里打「装配」日志 —— GameModule 反射装配后会统一打一次（GameModule.cs:235）
    }

    /// <summary>Ids with a name in MainOrnament.txt / MainSkinBg.txt. Row 0 of MainOrnament holds prefab paths, not a theme.</summary>
    private static int[] OrnamentIds(string path, int minimum)
    {
        if (SettingTable.LoadAuto(path, "namekey") is not { } table) return Array.Empty<int>();
        return table.Rows.Where(r => table.Str(r, "namekey").Length > 0).Select(r => table.Int(r, "id"))
            .Where(id => id >= minimum).Distinct().Order().ToArray();
    }

    /// <summary>
    /// Writes an acquisition time for each theme (87, id) and background (87, 2000 + id) the player lacks. Theme ids stop
    /// well below 1000, so neither range touches the current-theme (0), decoration (1000) or current-background (2000) slots.
    /// Permanent ownership (87, id + 20000) is read as <c>&gt;= 0</c>, so the default 0 already counts.
    /// </summary>
    internal static int UnlockOrnaments(Player player, IEnumerable<int> themeIds, IEnumerable<int> backgroundIds)
    {
        int now = (int)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), added = 0;
        foreach (int id in themeIds)
            if (id > 0 && id < OrnamentShowDecorate && player.GetTask(OrnamentGroup, id) <= 0)
            {
                player.SetTask(OrnamentGroup, id, now);
                added++;
            }
        foreach (int id in backgroundIds)
            if (id > 1 && id < OrnamentBackgroundBase && player.GetTask(OrnamentGroup, OrnamentBackgroundBase + id) <= 0)
            {
                player.SetTask(OrnamentGroup, OrnamentBackgroundBase + id, now);
                added++;
            }
        return added;
    }

    // ================================================================ 70 头像框

    /// <summary>
    /// cmd 70 设置头像框。存 <see cref="Player.HeadFrame"/>（protobuf，下次登录随
    /// <c>PlayerNtf.headframe</c> 下发，见类注释 §7），回**裸数组** <c>[id]</c>（§3）。
    /// </summary>
    internal Task OnSetHeadFrame(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;

        int id = ReadScalarId(param);
        if (id < 0)
        {
            Log.Warn($"[session {session.Id}] Player_SetHeadFrame(70) 参数非法：{param?.ToJsonString()}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        // ★ 头像框的「脱下」值是 1 不是 0（CardCommon.lua:1020），0 也放行（等价默认）。
        if (id is not (0 or DecorTable.DefaultHeadFrame)
            && !CheckOwned(player, DecorKind.HeadFrame, id, out string reason))
        {
            Log.Warn($"[session {session.Id}] Player_SetHeadFrame(70) 拒绝 id={id}：{reason}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        int before = player.HeadFrame;
        player.HeadFrame = id;
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] Player_SetHeadFrame(70) 头像框 {before} → {id}");

        // §3：成功回裸数组 [id]（客户端 #tbParam > 0 且读 tbParam[1]）
        Reply(session, cmd, new JsonArray { id });
        return Task.CompletedTask;
    }

    // ================================================================ 91/92/93 名片三件套

    /// <summary>cmd 91 设置名片皮肤（存 <c>Extra["maincity"]["card"]</c> 的 <c>VisitingCardID</c>）。</summary>
    internal Task OnSetVisitingCard(ClientSession session, int cmd, JsonNode? param)
        => SetCardPart(session, cmd, param, DecorKind.Skin, "Player_SetVisitingCard(91)");

    /// <summary>cmd 92 设置玩家列表标识皮肤（<c>PlayerListSkinID</c>）。</summary>
    internal Task OnSetPlayerListSkin(ClientSession session, int cmd, JsonNode? param)
        => SetCardPart(session, cmd, param, DecorKind.Board, "Player_SetPlayerListSkin(92)");

    /// <summary>cmd 93 设置聊天气泡（<c>ChatBubbleID</c>）。</summary>
    internal Task OnSetChatBubble(ClientSession session, int cmd, JsonNode? param)
        => SetCardPart(session, cmd, param, DecorKind.Babble, "Player_SetChatBubble(93)");

    /// <summary>
    /// 91/92/93 共用实现：三条只差「改三件套里的哪一个字段」。
    /// 回包统一给 94 那份三件套对象（§3），这样 <c>UI_User:OnChangeSkin</c>
    /// （<c>UI_User.lua:1306</c> 要 <c>ChatBubbleID</c> + <c>PlayerListSkinID</c>）也能直接用。
    /// </summary>
    private Task SetCardPart(
        ClientSession session, int cmd, JsonNode? param, DecorKind kind, string tag)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;

        int id = ReadScalarId(param);
        if (id < 0)
        {
            Log.Warn($"[session {session.Id}] {tag} 参数非法：{param?.ToJsonString()}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        // 0 = 脱下，一律放行（客户端自查过期后就是发 0，CardCommon.lua:1054/1060/1066）
        if (id != 0 && !CheckOwned(player, kind, id, out string reason))
        {
            Log.Warn($"[session {session.Id}] {tag} 拒绝 id={id}：{reason}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        var before = MainCityFeature.ReadCardState(player);
        var after = kind switch
        {
            DecorKind.Skin => before with { VisitingCardId = id },
            DecorKind.Board => before with { PlayerListSkinId = id },
            DecorKind.Babble => before with { ChatBubbleId = id },
            _ => before,
        };
        MainCityFeature.WriteCardState(player, after);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] {tag} → 名片={after.VisitingCardId} "
                 + $"标识={after.PlayerListSkinId} 气泡={after.ChatBubbleId}");

        Reply(session, cmd, MainCityFeature.BuildVisitingCardPayload(after));
        return Task.CompletedTask;
    }

    // ================================================================ 95 主城主题 + 装饰开关

    /// <summary>
    /// cmd 95 修改主城主题场景 / 装饰开关。
    ///
    /// 上行是**数组** <c>[{type=1,id=主题}, {type=2,id=装饰或0}]</c>（类注释 §5）。
    /// ★ 必须**先推 pf 1026 再回包**：客户端 <c>MainOrnament:UpdateOrnament()</c> 不收参数、
    /// 转头读任务值（§6）。
    /// </summary>
    internal Task OnChangeDecorate(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;

        // ★★ JsonNode["key"] 对 JsonArray 会抛（AGENTS.md 的通用坑）⇒ 逐项判类型
        var items = new List<(int Type, int Id)>();
        if (param is JsonArray arr)
        {
            foreach (var node in arr)
            {
                if (node is not JsonObject o) continue;
                items.Add((ReadInt(o["type"]), ReadInt(o["id"])));
            }
        }
        else if (param is JsonObject single)
        {
            items.Add((ReadInt(single["type"]), ReadInt(single["id"])));
        }

        if (items.Count == 0)
        {
            Log.Warn($"[session {session.Id}] ChangeDecorate(95) 参数非法：{param?.ToJsonString()}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        var changed = new List<KeyValuePair<uint, int>>();
        var parts = new List<string>();

        foreach (var (type, id) in items)
        {
            switch (type)
            {
                // type 1 = 换主题：写偏移 0（MainOrnament.lua:112）
                case 1:
                    if (id > 0 && !OwnsTheme(player, id))
                    {
                        Log.Warn($"[session {session.Id}] ChangeDecorate(95) 拒绝主题 {id}：未拥有");
                        Reply(session, cmd, Fail());
                        return Task.CompletedTask;
                    }
                    player.SetTask(OrnamentGroup, OrnamentCurrentTheme, id);
                    changed.Add(new(Player.ComposeTaskId(OrnamentGroup, OrnamentCurrentTheme), id));
                    parts.Add($"主题={id}");
                    break;

                // type 2 = 装饰开关：客户端传「显示就填主题 id、隐藏填 0」（UI_MainThemeTip.lua:202..205）
                case 2:
                    int flag = id > 0 ? 1 : 0;
                    player.SetTask(OrnamentGroup, OrnamentShowDecorate, flag);
                    changed.Add(new(Player.ComposeTaskId(OrnamentGroup, OrnamentShowDecorate), flag));
                    parts.Add($"装饰={(flag > 0 ? "显示" : "隐藏")}");
                    break;

                default:
                    Log.Warn($"[session {session.Id}] ChangeDecorate(95) 忽略未知 type={type} id={id}");
                    break;
            }
        }

        _ctx.Store.Save(player);
        Log.Info($"[session {session.Id}] ChangeDecorate(95) {string.Join(" ", parts)}");

        // §6：先推任务值，再回包
        GameNotify.PushTaskValues(session, changed);
        Reply(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 96 主城背景

    /// <summary>
    /// cmd 96 修改主城主题背景。上行是对象 <c>{id = N}</c>（类注释 §5）；
    /// 存任务值偏移 2000（<c>MainOrnament.lua:175</c>）。同样**先推后回**（§6）。
    /// </summary>
    internal Task OnChangeBackground(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;

        // 上行是 {id=N}；也容忍裸标量（防御性，与 §1 那批保持一致的宽容度）
        int id = param is JsonObject o ? ReadInt(o["id"], -1) : ReadScalarId(param);
        if (id < 0)
        {
            Log.Warn($"[session {session.Id}] ChangeBackground(96) 参数非法：{param?.ToJsonString()}");
            Reply(session, cmd, Fail());
            return Task.CompletedTask;
        }

        int before = player.GetTask(OrnamentGroup, OrnamentCurrentBg);
        player.SetTask(OrnamentGroup, OrnamentCurrentBg, id);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] ChangeBackground(96) 背景 {before} → {id}");

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(OrnamentGroup, OrnamentCurrentBg), id),
        });
        Reply(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 公共

    /// <summary>
    /// 主城主题是否拥有。取证 <c>MainOrnament:IsUseTheme</c>（<c>MainOrnament.lua:125..137</c>）：
    /// <c>GetTaskValue(87, id) &lt;= 0</c> 直接判未拥有；否则永久（<c>id+20000</c>）或未过期即可用。
    /// ★ 这里只做「有没有」的粗判（拿到时间 &gt; 0），到期判定交给客户端 —— 因为
    /// <c>MainOrnamentTimeConfig.txt</c> 的档期语义未逐列取证，宁可宽松也不要误拒。
    /// </summary>
    private static bool OwnsTheme(Player player, int themeId)
        => player.GetTask(OrnamentGroup, themeId) > 0
           || player.GetTask(OrnamentGroup, themeId + OrnamentPermanentBase) > 0;

    /// <summary>
    /// 校验玩家是否拥有某装扮（含限时到期），失败时给出可写进日志的原因。
    /// </summary>
    private bool CheckOwned(Player player, DecorKind kind, int id, out string reason)
    {
        if (!_decor.Exists(kind, id))
        {
            // ★ 配置表里没有的 id 必须拒绝：客户端查不到配置会反手发 Set…(0) 脱下来
            reason = "配置表里没有这个 id";
            return false;
        }

        var (_, group) = _decor.For(kind);
        int stamp = player.GetTask(group, id);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (!_decor.IsOwned(kind, id, stamp, now))
        {
            reason = stamp == 0 ? "未获得（任务值为 0）" : $"已过期（获得于 {stamp}）";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>
    /// 读「裸标量」形式的 id（类注释 §1）。非法返回 <c>-1</c>。
    /// ★ 同时容忍 <c>{id=N}</c> 形式，避免以后客户端某个发送点改了形状就整条命令失效。
    /// </summary>
    internal static int ReadScalarId(JsonNode? param)
    {
        switch (param)
        {
            case JsonValue v:
                if (v.TryGetValue<int>(out int i)) return i < 0 ? -1 : i;
                if (v.TryGetValue<double>(out double d)) return d < 0 ? -1 : (int)d;
                if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int p)) return p < 0 ? -1 : p;
                return -1;

            case JsonObject o:
                return ReadInt(o["id"], -1);

            // ★ 单元素数组（防御性：客户端有些发送点习惯包一层）
            case JsonArray a when a.Count == 1:
                return ReadScalarId(a[0]);

            default:
                return -1;
        }
    }

    private static int ReadInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out int i)) return i;
        if (v.TryGetValue<double>(out double d)) return (int)d;
        if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int p)) return p;
        return fallback;
    }

    /// <summary>
    /// 失败回包：<c>{nError = 码}</c>。★ 成功路径**绝不能**走这里（§2：真值判断）。
    /// </summary>
    private static JsonObject Fail(int code = ErrGeneric) => new() { ["nError"] = code };

    private void Reply(ClientSession session, int cmd, JsonNode payload)
        => _ctx.LuaCalls.CallClient(session, cmd, payload);
}
