using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 音游（咖啡馆小游戏 / 音乐会）：LuaCall 388 上报成绩 / 389 取排行 / 390 取好友排行。
///
/// <para><b>0. ★★★ 为什么这块是「活着」的（第十三轮档期筛查的关键发现）</b></para>
/// 音游归属「音乐会活动」，那个活动的档期**早已过期**，所以它一度被判为死内容。
/// 但客户端咖啡馆里的音游按钮有**两条**准入判据（<c>UI_NCafe.lua:556</c>）：
/// <code>
/// if MusicActivityLogic:IsMusicGameOpen() or MusicActivityLogic:HasByMusicScene() then
///     self.objMusicGame:SetActive(self.IsShowOptions)
/// else
///     self.objMusicGame:SetActive(false)
/// end
/// MusicActivityLogic.lua:703..712  IsMusicGameOpen()  —— 走 tbMusicTowerTotalConfig 时间窗（★ 已过期）
/// MusicActivityLogic.lua:714..716  HasByMusicScene()  —— 只看
///     Game.NCafeManager.Data:GetItemByTemplate(1400).Count &gt; 0
/// </code>
/// ⇒ **第二条与档期无关**：只要玩家持有咖啡馆家具 **1400「音乐节外景」**，按钮就常驻可见。
/// 服务端在 <see cref="HouseFeature.GrantMusicSceneForGm"/> 给 GM 账号补这件家具 ⇒ 玩法可达。
///
/// ★★ **通用教训**：判一个玩法死活时，**要把入口的所有准入分支都列出来** ——
/// 「档期过期」只否掉了其中一条。本轮档期筛查最初把 388/389/390 判成死内容，
/// 正是因为只追了「音乐会肝塔」那一条入口链。
///
/// <para><b>1. 命令面</b></para>
/// <list type="table">
///   <item><term>388 <c>MusicGameComplete</c></term><description>一局结束上报成绩。★ **不回包**（见 §3）</description></item>
///   <item><term>389 <c>GetMusicGameRank</c></term><description>取排行（三种上行形态，见 §4）</description></item>
///   <item><term>390 <c>GetMusicGameFriedsRank</c></term><description>取好友排行（★ 原厂拼写 <c>Frieds</c>，少个 n）</description></item>
/// </list>
///
/// <para><b>2. ★★ 成绩存任务值 group 1 偏移 <c>3100 + gameId</c></b></para>
/// 两处独立取证完全一致：
/// <code>
/// UI_GMiniMusic.lua:371   local oldScore = me:GetTaskValue(1, 3100 + self.tbConfig.Id)
/// UI_MusicSelect.lua:13   local nTaskGroup = 1
/// UI_MusicSelect.lua:14   local nTaskScoreStart = 3100
/// UI_MusicSelect.lua:601  local myScore = me:GetTaskValue(nTaskGroup, nTaskScoreStart + tb.musicId)
/// </code>
/// ⇒ 服务端只需「取最大值」（新分数更高才覆盖）并推 pf 1026。
///
/// <para><b>3. ★★★ 388 故意不回包 —— 原厂就把 <c>CallClient</c> 注释掉了</b></para>
/// <code>
/// RegisterCMD(LuaCall.MusicGameComplete, function(sCmd, tbParam)
///     if MODULE_SERVER then
///         MusicGame:OnGameComplete(tbParam);
///     --	LuaCall:CallClient(sCmd, tbRet)      ← ★ 注释掉的
///     else
///                                              ← ★ 客户端分支是空的
///     end
/// end)
/// </code>
/// ⇒ 客户端分支**整个是空的**，回包也没人处理。
/// 而结算界面的名次是靠**紧接着发的 389**（<c>UI_GMiniMusic.lua:391</c>，延迟 3 秒）拿到的。
/// ⇒ 本实现 388 只存分数 + 推 pf 1026，**不回业务包**。
///
/// <para><b>4. ★★ 389 有三种上行形态，回包是「以 gameId 为键的字典」</b></para>
/// <code>
/// UI_GMiniMusic.lua:391   CallServer(GetMusicGameRank, {gameId = self.tbConfig.Id})   -- 单曲
/// UI_GMiniMusic.lua:395   CallServer(GetMusicGameRank)                                 -- ★ 无参
/// UI_MusicSelect.lua:174  CallServer(GetMusicGameRank, {isAll = true})                 -- 全部
/// </code>
/// 消费点（<c>UI_GMiniMusic.lua:533..545</c>）：
/// <code>
/// function tbClass:OnRankDataResponse(tb)
///     self.tbServerScore = tb
///     local tbScore = tb[self.tbConfig.Id] or {}          -- ★ 用 gameId 当键
///     if not tbScore.pos or tbScore.pos == 0 or tbScore.pos &gt; 50 then 显示「未上榜」
///     else 显示 tbScore.pos end
/// </code>
/// ⇒ 回 <c>{ "&lt;gameId&gt;": { pos = 名次, score = 分数 } }</c>。
/// ★ 三种形态**都回同一份全量字典**最省事且安全：<c>tb[id] or {}</c> 有兜底，多给不会错。
///
/// <para><b>5. 单机私服没有真实排行榜 ⇒ <c>pos = 0</c> 是正解</b></para>
/// <c>pos == 0</c> 客户端显示「未上榜」（<c>objRankNone</c>），这是**原厂认可的合法状态**，
/// 比编一个假名次好（假名次会让玩家发现分数与名次对不上）。
/// ★ 玩家自己的**最高分**仍然真实存在（任务值），结算页的连击/分数都正常显示。
///
/// <para><b>6. ★★ 390 的回包客户端会「自己把玩家插进列表」</b></para>
/// <code>
/// UI_MusicSelect.lua:600..615
/// function view:OnFriendRankDataResponse(tb)
///     local myScore = me:GetTaskValue(1, 3100 + tb.musicId)
///     if myScore &gt; 0 then table.insert(tb.tbList, { roleId, score, rolename, level, girliconid, headframeid }) end
///     table.sort(tb.tbList, function(a,b) return a.score &gt; b.score end)
/// </code>
/// ⇒ 服务端**不必**把玩家自己放进 <c>tbList</c>（会重复）；
/// 且 <c>tbList</c> 可以是**空数组**（单机无好友成绩），客户端排序与渲染都安全。
/// ★ 必须给 <c>musicId</c>（客户端拿它查任务值与缓存键），漏了会读到 <c>nil</c>。
/// </summary>
public sealed class MusicGameFeature : IGameFeature
{
    /// <summary>LuaCall.MusicGameComplete = 388「音游结束」。</summary>
    public const int CompleteCmd = 388;

    /// <summary>LuaCall.GetMusicGameRank = 389「得到音游排名」。</summary>
    public const int RankCmd = 389;

    /// <summary>
    /// LuaCall.GetMusicGameFriedsRank = 390「得到音游好友排名」。
    /// ★ 原厂拼写就是 <c>Frieds</c>（少个 n）。
    /// </summary>
    public const int FriendRankCmd = 390;

    /// <summary>成绩所在任务值 group = <b>1</b>（<c>UI_MusicSelect.lua:13</c>）。</summary>
    public const int ScoreTaskGroup = 1;

    /// <summary>
    /// 成绩偏移基址 = <b>3100</b>（<c>UI_MusicSelect.lua:14</c> <c>nTaskScoreStart</c>，
    /// 与 <c>UI_GMiniMusic.lua:371</c> 的 <c>3100 + Id</c> 相互印证）。
    /// 实际偏移 = <c>3100 + gameId</c>。
    /// </summary>
    public const int ScoreTaskBase = 3100;

    /// <summary>
    /// 曲目 id 的合理上界（防止越界写任务值污染别的 group）。
    /// 现网曲目表 <c>music01..05.txt</c> ⇒ id 是个位数；留 100 的余量。
    /// ★ 任务值键是 <c>(group &lt;&lt; 16) | (id &amp; 0xFFFF)</c>，
    /// 偏移超过 65535 会**溢出到别的 group** —— 这条上界是必须的。
    /// </summary>
    public const int MaxGameId = 100;

    /// <summary>
    /// 「未上榜」的名次值。<c>pos == 0</c> 客户端显示 <c>objRankNone</c>
    /// （<c>UI_GMiniMusic.lua:537</c>），是原厂认可的合法状态（见类注释 §5）。
    /// </summary>
    public const int RankNone = 0;

    private GameFeatureContext _ctx = null!;

    public string Name => "音游（LuaCall 388 成绩 / 389 排行 / 390 好友排行）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.MapLuaCall(CompleteCmd, OnComplete);
        ctx.MapLuaCall(RankCmd, OnRank);
        ctx.MapLuaCall(FriendRankCmd, OnFriendRank);
    }

    // ================================================================ 388 上报成绩

    /// <summary>
    /// 388 一局结束上报成绩。上行 <c>{gameId, score, type, nCombo}</c>
    /// （<c>UI_GMiniMusic.lua:372..378</c>）。
    /// ★ **不回业务包**（类注释 §3）—— 只存分数 + 推 pf 1026。
    /// </summary>
    internal Task OnComplete(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        int gameId = ReadInt(body?["gameId"]);
        int score = ReadInt(body?["score"]);
        int combo = ReadInt(body?["nCombo"]);
        int type = ReadInt(body?["type"]);

        if (gameId <= 0 || gameId > MaxGameId)
        {
            Log.Warn($"[session {session.Id}] MusicGameComplete(388) gameId={gameId} 越界"
                   + $"（合法 1..{MaxGameId}），忽略");
            return Task.CompletedTask;
        }
        if (score <= 0)
        {
            // 客户端本来就只在 nScore > 0 时才发（UI_GMiniMusic.lua:372），这里兜一下
            Log.Warn($"[session {session.Id}] MusicGameComplete(388) score={score} 非正，忽略");
            return Task.CompletedTask;
        }

        int offset = ScoreTaskBase + gameId;
        int old = player.GetTask(ScoreTaskGroup, offset);

        if (score <= old)
        {
            Log.Info($"[session {session.Id}] MusicGameComplete(388) 曲目 {gameId} 得分 {score}"
                   + $"（连击 {combo} 评级 {type}）未超过最高分 {old}，不更新");
            return Task.CompletedTask;
        }

        player.SetTask(ScoreTaskGroup, offset, score);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] MusicGameComplete(388) 曲目 {gameId} 新纪录："
               + $"{old} → {score}（连击 {combo} 评级 {type}）");

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(
                Player.ComposeTaskId(ScoreTaskGroup, offset), score),
        });
        // ★ 不回业务包：原厂 CallClient 被注释、客户端分支是空的（类注释 §3）
        return Task.CompletedTask;
    }

    // ================================================================ 389 排行

    /// <summary>
    /// 389 取排行。三种上行形态都回**同一份以 gameId 为键的全量字典**（类注释 §4）。
    /// 单机无真实榜单 ⇒ <c>pos = 0</c>（未上榜），但带上玩家自己的真实最高分。
    /// </summary>
    internal Task OnRank(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var dict = new JsonObject();
        int n = 0;
        for (int gameId = 1; gameId <= MaxGameId; gameId++)
        {
            int score = player.GetTask(ScoreTaskGroup, ScoreTaskBase + gameId);
            if (score <= 0)
                continue;
            dict[gameId.ToString()] = new JsonObject
            {
                ["pos"] = RankNone,          // ★ 未上榜（单机无榜），见类注释 §5
                ["score"] = score,
            };
            n++;
        }

        Log.Info($"[session {session.Id}] GetMusicGameRank(389) 回 {n} 首有成绩的曲目"
               + $"（pos 一律 {RankNone} = 未上榜）");

        _ctx.LuaCalls.CallClient(session, cmd, dict);
        return Task.CompletedTask;
    }

    // ================================================================ 390 好友排行

    /// <summary>
    /// 390 取好友排行。回 <c>{musicId, tbList = []}</c>（类注释 §6）。
    /// ★ <c>musicId</c> 必填；<c>tbList</c> 可以是空数组 —— 客户端会**自己**把玩家
    /// 插进列表再排序（<c>UI_MusicSelect.lua:601..612</c>），服务端放进去反而重复。
    /// </summary>
    internal Task OnFriendRank(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        // 上行形态未逐个取证，容忍 gameId / musicId 两种键名
        int musicId = ReadInt(body?["musicId"]);
        if (musicId <= 0)
            musicId = ReadInt(body?["gameId"]);

        Log.Info($"[session {session.Id}] GetMusicGameFriedsRank(390) musicId={musicId} → 空好友榜"
               + "（单机无其它真实玩家成绩；客户端会自己把本人插进列表）");

        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["musicId"] = musicId,
            ["tbList"] = new JsonArray(),
        });
        return Task.CompletedTask;
    }

    private static int ReadInt(JsonNode? node, int fallback = 0)
    {
        if (node is not JsonValue v) return fallback;
        if (v.TryGetValue<int>(out int i)) return i;
        if (v.TryGetValue<double>(out double d)) return (int)d;
        if (v.TryGetValue<string>(out string? s) && int.TryParse(s, out int p)) return p;
        return fallback;
    }
}
