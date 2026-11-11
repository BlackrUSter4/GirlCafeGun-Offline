using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 聊天域：**pf 1010 / 1011 / 1012** 三条专用 protobuf 通道 + **LuaCall 140 / 141** 切世界频道。
///
/// 完整取证在 `work/chat-domain-forensics.md`（1297 行），本注释只记「写代码时必须知道的」。
/// 它取代了 `docs/05-feature-domains.md` §17 里那句「聊天正文疑似不走 LuaCall，通道未取证」。
///
/// <para><b>① 通道全景（取证 `docs/pf-table.txt:10..12`）</b></para>
/// <code>
/// C2S pf=1010  ChattingReq        Game.Player.SendChatMessage        RVA 0x1856BD0
/// S2C pf=1011  ChattingRsp        Game.Player.OnReciveChatMessageRsp RVA 0x1855E80（★空方法）
/// S2C pf=1012  NoticeChattingMsg  Game.Player.OnReciveChatMessage    RVA 0x1855EF0
///              └ Game.Lua.Call("PlayerLogic.AddNewChatMessage", object[10])   PlayerLogic.lua:179
/// C2S LuaCall 140 Chat_ChangeChannel        tbParam = [nNum]（★裸数组）  UI_Chat.lua:1534
/// C2S LuaCall 141 Chat_RandomChangeChannel  tbParam = <不发>（★null）    UI_Chat.lua:1450
/// </code>
/// 聊天正文**完全不走 LuaCall**；只有「切世界频道」走通用 RPC。
///
/// <para><b>② 频道枚举（唯一权威定义在 Lua，proto 里只是 uint32）</b></para>
/// <c>PlayerLogic.lua:26..30</c>：1 世界 / 2 系统 / 3 私聊 / 4 社团 / 5 队伍。
/// 界面五个页签「世界|系统|社团|队伍|私聊」逐一对应（`UI_Chat.lua:69..73`）。
///
/// <para><b>③ ★ pf 1012 的 field 1..8 是 required，少一个整包被丢</b></para>
/// <c>NoticeChattingMsg.IsInitialized</c>（RVA `0x337CB6C`）是 <c>mov w9,#0xff; bics wzr,w9,w8</c>
/// ⇒ 要求 bit0..bit7 全置。ProtoGen 生成的 setter 才会置 has 位
/// （`gsproto_NoticeChattingMsg.cs:35` <c>set { _ReceiverName = value; _has0 |= 0x10u; }</c>），
/// 所以**非私聊场景也必须显式写** <c>ReceiverId = 0</c> / <c>ReceiverName = ""</c>，
/// 不赋值 = 不写字段 = 客户端 <c>Failed to process message. PF : 1012</c>。
/// <c>girlid</c>(9)/<c>headframe</c>(10)/<c>channelid</c>(11) 可省。
///
/// <para><b>④ ★★ `channelid` 两头都没用</b></para>
/// 上行 <c>ChattingReq.channelid</c>：<c>set_channelid</c>(`0x31E5354`) 全 so **0 caller**
/// ⇒ 客户端从不填 ⇒ **世界频道号只能由服务端自己维护**（存 <see cref="Player.ChatChannelId"/>，
/// 登录随 pf 1005 <c>PlayerNtf.chatchannelid</c> 下发）。
/// 下行 <c>NoticeChattingMsg.channelid</c>：<c>get_channelid</c>(`0x337C080`) 也是 **0 caller**，
/// 且不在传给 Lua 的 10 个实参里 ⇒ 填不填一样，本实现填上只为语义完整。
///
/// <para><b>⑤ ★★★ 已知偏差：自己的消息会被渲染成「别人的」（陷阱 T1）</b></para>
/// <c>NoticeChattingMsg.sender_id</c> 是 **uint32**，而客户端 <c>me.id</c> 是
/// <c>PlayerNtf.roleid</c>（**uint64**）。本服 <c>World.DeriveRoleId</c> 产出 ~5.2e15，
/// 截断进 uint32 之后 <c>me.id == sender_id</c> 恒假，后果三处：
/// <c>UI_Chat.lua:581/589/595</c> 走 <c>ShowLeft</c>（消息显示在左边）、
/// <c>PlayerLogic.lua:283</c> 每条自己的消息都在主城弹飘字、
/// <c>UI_Chat.lua:701/789</c> 点自己头像会去请求名片。
/// **这是纯表现问题，不影响消息收发。**根治要把 roleid 限制到 ≤ 0xFFFFFFFF
/// （见 <see cref="SenderIdOf"/> 与 `work/chat-domain-forensics.md` §9.1 第 1 条）。
///
/// <para><b>⑥ 遮罩与错误码：这一族最宽容</b></para>
/// * 正文走 <c>Net.Send</c>，不经 <c>LuaCall:CallServer</c>；140/141 也**不传 blockTime**
///   （`LuaCall.lua:506..508` 才会 <c>Blocking.Show</c>）⇒ **不回包不会卡遮罩**。
/// * 封包头 <c>[14:16] code</c> 必须为 0：<c>0x1855F84 cbnz w8 → ret</c> 是**静默**早退，
///   连 <c>ui.Error.*</c> 都不弹（`PacketCodec.Encode` 默认 0，天然满足）。
/// * 140/141 属 **LuaCall 数字族**：成功回**裸数组** <c>[新频道号]</c>（天然不含 <c>nError</c> 键）；
///   失败才带非 0 <c>nError</c>。本实现**永不回失败**（非法频道号钳到合法区间），
///   理由：<c>ui.Error.&lt;code&gt;</c> 的本地化键表未取证，玩家会看到字面量 <c>ui.Error.1</c>。
/// </summary>
public sealed class ChatFeature : IGameFeature
{
    // ---------------------------------------------------------------- 频道类型
    // 取证：extracted/lua/PlayerLogic.lua:26..30（唯一权威定义，proto 里只是 uint32）

    /// <summary>世界频道（<c>PlayerLogic.ChatChannel_World = 1</c>）。<c>SendChatMessage</c> 的 nType 默认值。</summary>
    public const uint ChannelWorld = 1;

    /// <summary>系统频道（<c>= 2</c>）。★ 客户端**没有**发言入口（`UI_Chat.lua:455` 隐藏输入框），只收服务端推送。</summary>
    public const uint ChannelSystem = 2;

    /// <summary>私聊频道（<c>= 3</c>）。</summary>
    public const uint ChannelWhisper = 3;

    /// <summary>社团频道（<c>= 4</c>）。</summary>
    public const uint ChannelClub = 4;

    /// <summary>队伍频道（<c>= 5</c>）。</summary>
    public const uint ChannelTeam = 5;

    // ---------------------------------------------------------------- LuaCall id
    // 取证：extracted/lua/LuaCall.lua:142/143

    /// <summary>LuaCall.Chat_ChangeChannel = 140「切换世界聊天频道」（<c>LuaCall.lua:142</c>）。</summary>
    public const int ChangeChannelCmd = 140;

    /// <summary>LuaCall.Chat_RandomChangeChannel = 141「随机切换世界聊天频道」（<c>LuaCall.lua:143</c>）。</summary>
    public const int RandomChangeChannelCmd = 141;

    // ---------------------------------------------------------------- 频道号值域

    /// <summary>世界频道号下界（含）。取证 <c>UI_Chat.lua:1525</c> <c>if not nNum or nNum &lt; 1 or nNum &gt; 999</c>。</summary>
    public const uint MinChannel = 1;

    /// <summary>
    /// 世界频道号上界（含）。**硬编码在客户端**，没有配置表
    /// （<c>ls extracted/setting/ | grep -i channel</c> 为空）。
    /// </summary>
    public const uint MaxChannel = 999;

    // ---------------------------------------------------------------- 服务端自定的限额

    /// <summary>
    /// 服务端侧正文长度上限（字符）。客户端自己也有长度限制，但**具体值未取证**，
    /// 且 GM 通道能绕过全部客户端校验 ⇒ 服务端必须自己兜一道，避免超长正文冲垮
    /// 客户端每桶 500 条的历史表（<c>PlayerLogic.lua:4 MAX_CHAT = 500</c>）。
    /// </summary>
    public const int MaxMessageLength = 200;

    /// <summary>
    /// 服务端侧发言 CD（秒）。客户端有 5 秒重复过滤（<c>UI_Chat.lua:952..969 FilterMsg4Time</c>），
    /// 但那是**本地**限制、GM 通道绕得过，所以服务端自己也限一道。
    /// </summary>
    public const int SpeakCooldownSeconds = 1;

    /// <summary>
    /// 服务端为每个世界频道保留的历史条数上限。切频道时用它补推历史
    /// （客户端切到新频道后那个桶是空的，见类注释 §6.3 的取证）。
    /// 取小值：客户端上限是 500，这里只做「让新频道不是全空」。
    /// </summary>
    public const int HistoryPerChannel = 20;

    /// <summary><see cref="Player.Extra"/> 里本模块的 key。</summary>
    internal const string ExtraKey = "chat";

    /// <summary>Extra["chat"]["lastspeak"]：上次发言的 unix 秒（服务端 CD 用）。</summary>
    internal const string LastSpeakKey = "lastspeak";

    /// <summary>
    /// 系统频道公告的发言者 id / 名字。取证 <c>BannerLogic.lua:615</c>
    /// （原服的另一条系统频道通道就是 <c>AddNewChatMessage(2, 1, "system", ...)</c>）。
    /// </summary>
    internal const uint SystemSenderId = 1;

    /// <summary>同上。</summary>
    internal const string SystemSenderName = "system";

    private GameFeatureContext _ctx = null!;
    private ChatTable _table = ChatTable.Empty;

    /// <summary>
    /// 世界频道历史：频道号 → 最近若干条消息（进程内，不落盘）。
    ///
    /// 为什么要服务端存：客户端的 <c>PlayerLogic.tbChatHistory</c>（`PlayerLogic.lua:3`）是
    /// **纯内存表**，重登即空、切频道后新桶也是空的。想让玩家切回来还看得到内容，
    /// 只有服务端补推（每条 1012 都是独立 append，客户端没有「全量/批次」概念）。
    /// 不落盘是刻意的：单机本地服重启即清，避免存档里堆无用文本。
    /// </summary>
    private readonly Dictionary<uint, List<NoticeChattingMsg>> _worldHistory = new();

    private readonly object _historySync = new();

    /// <summary>随机切频道用（<c>141</c> 的随机在**服务端**，客户端连 tbParam 都不发）。</summary>
    private readonly Random _rng = new();

    public string Name => "聊天（pf 1010/1011/1012 + LuaCall 140 切频道 / 141 随机切频道）";

    public ChatFeature() { }

    /// <summary>单测用：直接注入装扮白名单，跳过 <see cref="Install"/>（同 <c>HouseFeature</c> 的做法）。</summary>
    internal ChatFeature(ChatTable table) => _table = table;

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = ChatTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapPf(GamePf.Chatting, (s, p) => HandleChatting(s, p));
        ctx.MapLuaCall(ChangeChannelCmd, OnChangeChannel);
        ctx.MapLuaCall(RandomChangeChannelCmd, OnRandomChangeChannel);

        // ★ 必须用 OnPlayerPrepare（pf 1005 之前）：频道号要随 PlayerNtf.chatchannelid 一起下发，
        //   否则客户端 me.chatchannel 会是 0（本服默认值），而客户端合法区间是 1..999。
        //   在这里改 = 零额外封包（GameFeature.cs:135..151 的设计意图）。
        ctx.OnPlayerPrepare(EnsureChannel);

        // ★ 系统欢迎语必须用 OnPlayerLogin（1005 之后）：PlayerLogic.AddNewChatMessage
        //   第一行就用 me（PlayerLogic.lua:188），而 me 是 pf 1005 建立的。
        ctx.OnPlayerLogin(OnLogin);
    }

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[]
    {
        ChangeChannelCmd, RandomChangeChannelCmd,
    };

    // =================================================================
    //  登录钩子
    // =================================================================

    /// <summary>
    /// 给新号分配一个合法的世界频道号（pf 1005 之前，随 <c>PlayerNtf.chatchannelid</c> 下发）。
    ///
    /// 为什么必须做：<see cref="Player.ChatChannelId"/> 默认 0（`Player.cs:51`），
    /// 而客户端允许的输入区间是 **1..999**（`UI_Chat.lua:1525`）。
    /// 0 在 Lua 里是 truthy，所以界面会把「频道」显示成 <c>0</c>、消息也会进
    /// <c>tbChatHistory[1][0]</c> 这个桶 —— 不崩，但值域不一致，切一次频道就再也回不去 0。
    /// </summary>
    internal void EnsureChannel(ClientSession session, Player player)
    {
        if (player.ChatChannelId >= MinChannel && player.ChatChannelId <= MaxChannel)
            return;

        player.ChatChannelId = MinChannel;
        _ctx?.Store.Save(player);
        Log.Info($"[session {session.Id}] 聊天频道未初始化（0），分配世界频道 {MinChannel}"
                 + "（客户端合法区间 1..999，随 pf 1005 chatchannelid 下发）");
    }

    /// <summary>
    /// 登录后往**系统频道**推一条欢迎语，并补推当前世界频道的历史。
    ///
    /// 系统频道的渲染分支 <c>ShowSystem</c>（`UI_Chat.lua:808..820`）只用 <c>info</c> + <c>timeString</c>，
    /// 不读头像/等级/名字 —— 但 field 1..8 仍然全是 required（见类注释 ③）。
    /// </summary>
    internal void OnLogin(ClientSession session, Player player)
    {
        session.Push(GamePf.NoticeChattingMsg,
                     BuildSystemNotice($"欢迎回来，{Display(player.Name)}。当前世界频道 {player.ChatChannelId}。"));

        int replayed = ReplayWorldHistory(session, player.ChatChannelId);
        Log.Info($"[session {session.Id}] 聊天登录推送：系统欢迎语 1 条（pf 1012 type=2）"
                 + $"、世界频道 {player.ChatChannelId} 历史 {replayed} 条");
    }

    // =================================================================
    //  pf 1010 ChattingReq → pf 1011 ChattingRsp + pf 1012 NoticeChattingMsg
    // =================================================================

    /// <summary>
    /// 聊天正文上行。
    ///
    /// 上行字段（<c>ChattingReq.IsInitialized</c> RVA `0x31E59D8` 掩码 <c>0x0F</c>）：
    /// <c>type</c> / <c>target_id</c> / <c>target_name</c> / <c>message</c> 四个 **required**，
    /// <c>channelid</c> 客户端从不填（见类注释 ④）。
    /// 客户端调用点 <c>UI_Chat.lua:1054..1060</c>：世界频道用默认 <c>nType=1</c>、
    /// 私聊额外带 <c>WhisperChatID/Name</c>、社团与队伍只带 nType。
    ///
    /// 本服的处理：
    /// <list type="number">
    /// <item>回 pf 1011（空体，见 <see cref="ReplyAck"/> 的取证）；</item>
    /// <item>世界 / 社团 / 队伍：把消息**回显**给自己（单机本地服没有别的玩家），
    ///   同时广播给同频道的其它在线会话（多开时有用）；</item>
    /// <item>私聊：目标不在线就退化成一条系统频道提示（原服会离线投递，本服不做）。</item>
    /// </list>
    /// </summary>
    internal Task HandleChatting(ClientSession session, Packet packet)
    {
        var req = TryParse(session, packet);
        if (req is null)
            return Task.CompletedTask;

        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] ChattingReq(1010) 到达但会话未登录（Tag 非 Player），丢弃");
            return Task.CompletedTask;
        }

        // pf 1011 先回：客户端 handler 与 serial 回调都是空方法，顺序其实无所谓（见 ReplyAck）
        ReplyAck(session, packet.Serial);

        uint type = req.HasType ? req.Type : ChannelWorld;
        string message = req.Message ?? "";
        string reason = Validate(player, message);
        if (reason.Length > 0)
        {
            Log.Warn($"[session {session.Id}] 聊天上行被拒：{reason}"
                     + $"（type={type} len={message.Length} 正文前 32 字={Preview(message)}）");
            return Task.CompletedTask;
        }

        StampSpeakTime(player);

        switch (type)
        {
            case ChannelWorld:
            case ChannelClub:
            case ChannelTeam:
            {
                var ntf = BuildNotice(player, type, message, receiverId: 0, receiverName: "");
                int fanout = Broadcast(session, player, type, ntf);
                Log.Info($"[session {session.Id}] 聊天 {ChannelName(type)} roleid={player.Roleid}"
                         + $" 频道={player.ChatChannelId} 正文={Preview(message)}"
                         + $" → pf 1012 推给 {fanout} 个会话");
                break;
            }

            case ChannelWhisper:
            {
                var ntf = BuildNotice(player, type, message,
                                      receiverId: Truncate(req.HasTargetId ? req.TargetId : 0),
                                      receiverName: req.TargetName ?? "");
                // 私聊：先回显给自己（ShowRight/ShowLeft 都要这条），再尝试投给目标
                session.Push(GamePf.NoticeChattingMsg, ntf);
                int delivered = DeliverWhisper(session, req.HasTargetId ? req.TargetId : 0, ntf);
                Log.Info($"[session {session.Id}] 聊天 私聊 → target_id={req.TargetId}"
                         + $"（name={Display(req.TargetName)}）正文={Preview(message)}"
                         + $" → 自己 1 条；对方{(delivered > 0 ? $"在线 {delivered} 条" : "不在线，未投递（本服不做离线投递）")}");
                if (delivered == 0)
                {
                    session.Push(GamePf.NoticeChattingMsg,
                                 BuildSystemNotice($"「{Display(req.TargetName)}」当前不在线，消息未送达。"));
                }
                break;
            }

            default:
                Log.Warn($"[session {session.Id}] 聊天上行的 type={type} 不在 1..5（PlayerLogic.lua:26..30）内，丢弃");
                break;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 回 pf 1011 <c>ChattingRsp</c>（**零字段空消息**）。
    ///
    /// 为什么「回了也没用、但还是回」：
    /// <list type="bullet">
    /// <item>客户端 pf 处理函数 <c>OnReciveChatMessageRsp</c>（RVA `0x1855E80`）只有 IFix 跳板 + <c>ret</c>；
    ///   <c>Net.Send</c> 注册的 serial 回调 <c>&lt;SendChatMessage&gt;b__126_0</c>（RVA `0x18579FC`）
    ///   **整个函数就一条 <c>ret</c>** ⇒ 客户端拿它什么都不做。</item>
    /// <item>但**不回**会让客户端 <c>UNet.Net.callbacks</c>（<c>SimpleDictionary&lt;uint, IDispatcher&gt;</c>，
    ///   `dump.cs:439070`）里那条 serial 记账永久悬挂 —— 只有收到同 serial 的回包才会
    ///   <c>Remove</c>（`0x1B4DF70`）。每发一条消息漏一条，所以照原服回一个。</item>
    /// <item>安全性：<c>UNet.Dispatcher&lt;T&gt;.Process</c>（`0x30967D4`）对 serial 回调用的是
    ///   **发送时那个泛型 T**（这里是 <c>ChattingReq</c>）去 <c>Parse</c>（vtable +0x168 = slot 4）。
    ///   空体解出来 <c>IsInitialized</c> 为假、<c>Parse</c> 返回 false，但 `0x3096860` **不看返回值**、
    ///   直接调那个空 processor ⇒ 不抛异常、不打日志。且 <c>Game.Crypto.Decode</c>（`0x156221C`）
    ///   对长度 0 的 payload 原样返回（`0x156228C cbz x8`）⇒ 不推进 3DES 的 CBC 链。</item>
    /// </list>
    /// </summary>
    private static void ReplyAck(ClientSession session, uint serial)
        => session.Send(GamePf.ChattingRsp, serial, new ChattingRsp());

    /// <summary>
    /// 服务端侧发言校验。返回空串 = 放行，否则是中文拒绝原因（只打日志，不回错误给客户端 ——
    /// 聊天域没有「弹错误框」的通道，见类注释 ⑥）。
    /// </summary>
    internal string Validate(Player player, string message)
    {
        if (message.Length == 0)
            return "正文为空（客户端 0x1856C8C 也会拦，这里兜底）";
        if (message.Length > MaxMessageLength)
            return $"正文超过 {MaxMessageLength} 字（服务端自定上限）";
        if (!IsLinkPrefixSafe(message))
            return "正文含链接键但缺少合法的 3 位长度前缀（会让客户端 Int32.Parse 抛，整条消息丢）";

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long last = ReadLastSpeak(player);
        if (now - last < SpeakCooldownSeconds)
            return $"发言 CD 未到（{SpeakCooldownSeconds}s，上次 {last}）";

        return "";
    }

    /// <summary>
    /// 陷阱 T5：正文里出现下列链接键时，客户端 <c>Game.Utils.GetByteArrayFromChatMsg</c>
    /// （RVA `0x15413D0`）会取键前面 3 个字符做 <c>Int32.Parse</c>（`0x1541544`），
    /// 不是数字就抛 <c>FormatException</c> ⇒ 异常被 <c>Net.Dispatch</c> 捕获 ⇒
    /// logcat 打 <c>Failed to process message. PF : 1012</c> ⇒ **整条消息不显示**。
    ///
    /// <c>idx &lt; 3</c> 或找不到时函数会安全 <c>return false</c>（`0x15414CC`），所以只需拦
    /// 「键出现在第 3 个字符之后、但前 3 个字符不是纯数字」这种情况。
    /// </summary>
    internal static bool IsLinkPrefixSafe(string message)
    {
        foreach (var key in LinkKeys)
        {
            int idx = message.IndexOf(key, StringComparison.Ordinal);
            if (idx < 3)
                continue;                       // -1（没有）或 <3（客户端自己会安全 return）
            var prefix = message.AsSpan(idx - 3, 3);
            foreach (var c in prefix)
            {
                if (c is < '0' or > '9')
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 客户端会在聊天正文里找的「内嵌结构」键。取证 <c>Game.Utils.GetByteArrayFromChatMsg</c>
    /// （RVA `0x15413D0`）里逐个 <c>String.IndexOf</c> 的字面量，以及
    /// <c>UI_Chat.lua:1440</c> 的 <c>"ByteArray=BoundInfo=%s;%s"</c>、
    /// <c>PlayerLogic.lua:196</c> 的 <c>"PvpInfo="</c>。
    ///
    /// 服务端只做「前缀合法性」校验，**不改正文**（链接本体 <c>midString</c> 客户端刻意不过滤，
    /// `0x1856030..0x18560C4`，改了会破坏 byteArray）。
    /// </summary>
    internal static readonly string[] LinkKeys =
    {
        "ByteArray=", "PvpInfo=", "CardInfo=", "BoundInfo=",
    };

    // =================================================================
    //  推送与广播
    // =================================================================

    /// <summary>
    /// 把一条消息推给「该收到的人」，返回实际推送的会话数。
    ///
    /// * 世界频道：同一个 <see cref="Player.ChatChannelId"/> 的全部在线会话（含自己）。
    /// * 社团频道：同一个 <see cref="Player.ClubId"/>（且非 0）的全部在线会话；无社团则只回自己。
    /// * 队伍频道：本服没有组队，只回自己。
    ///
    /// ★ 自己**一定要收到**：客户端不做「本地回显」，界面上那条消息完全靠服务端推回来
    /// （渲染分支 <c>UI_Chat.lua:561..603</c> 只从 <c>tbChatHistory</c> 取数据）。
    /// </summary>
    private int Broadcast(ClientSession session, Player player, uint type, NoticeChattingMsg ntf)
    {
        if (type == ChannelWorld)
            RememberWorld(player.ChatChannelId, ntf);

        int n = 0;
        foreach (var other in _ctx.World.OnlineSessions())
        {
            if (other is not SessionAdapter adapter)
                continue;
            if (adapter.Tag is not Player p)
                continue;

            bool send = adapter.Inner.Id == session.Id || type switch
            {
                ChannelWorld => p.ChatChannelId == player.ChatChannelId,
                ChannelClub => player.ClubId != 0 && p.ClubId == player.ClubId,
                _ => false,
            };
            if (!send)
                continue;

            adapter.Inner.Push(GamePf.NoticeChattingMsg, ntf);
            n++;
        }

        // OnlineSessions 里找不到自己（例如单测里没走 World.Login）时也要回显
        if (n == 0)
        {
            session.Push(GamePf.NoticeChattingMsg, ntf);
            n = 1;
        }
        return n;
    }

    /// <summary>把私聊投给目标 roleid 的在线会话，返回投出的条数（0 = 对方不在线）。</summary>
    private int DeliverWhisper(ClientSession session, ulong targetRoleid, NoticeChattingMsg ntf)
    {
        if (targetRoleid == 0)
            return 0;

        int n = 0;
        foreach (var other in _ctx.World.OnlineSessions())
        {
            if (other is not SessionAdapter adapter || adapter.Inner.Id == session.Id)
                continue;
            if (adapter.Tag is Player p && p.Roleid == targetRoleid)
            {
                adapter.Inner.Push(GamePf.NoticeChattingMsg, ntf);
                n++;
            }
        }
        return n;
    }

    /// <summary>记一条世界频道历史（每频道保留最近 <see cref="HistoryPerChannel"/> 条）。</summary>
    private void RememberWorld(uint channel, NoticeChattingMsg ntf)
    {
        lock (_historySync)
        {
            if (!_worldHistory.TryGetValue(channel, out var list))
            {
                list = new List<NoticeChattingMsg>();
                _worldHistory[channel] = list;
            }
            list.Add(ntf);
            if (list.Count > HistoryPerChannel)
                list.RemoveRange(0, list.Count - HistoryPerChannel);
        }
    }

    /// <summary>
    /// 按时间正序补推某个世界频道的历史，返回推送条数。
    ///
    /// 为什么需要：客户端世界频道按 <c>tbChatHistory[1][me.chatchannel]</c> 分桶
    /// （<c>PlayerLogic.lua:218..221</c>），切到新频道号那个桶是空的，界面会显示「暂无消息」
    /// （<c>UI_Chat.lua:546</c>）；而 <c>OnSuccessChangeChannel</c> 里清历史的那行是
    /// **被注释掉的**（<c>UI_Chat.lua:1543</c>）⇒ 旧频道的消息还留着，只有新频道空。
    /// </summary>
    private int ReplayWorldHistory(ClientSession session, uint channel)
    {
        List<NoticeChattingMsg> snapshot;
        lock (_historySync)
        {
            if (!_worldHistory.TryGetValue(channel, out var list) || list.Count == 0)
                return 0;
            snapshot = new List<NoticeChattingMsg>(list);
        }
        foreach (var ntf in snapshot)
            session.Push(GamePf.NoticeChattingMsg, ntf);
        return snapshot.Count;
    }

    // =================================================================
    //  LuaCall 140 / 141 切世界频道
    // =================================================================

    /// <summary>
    /// cmd 140 切换世界频道。
    ///
    /// 上行（<c>UI_Chat.lua:1521..1535</c>）：**裸数组** <c>[nNum]</c>，客户端已把范围限在 1..999，
    /// 且 <c>nNum == me.chatchannel</c> 时不发。
    /// ★★ 服务端拿到的是 <c>JsonArray</c> 而不是 <c>JsonObject</c> ——
    /// <c>JsonNode["key"]</c> 对 <c>JsonArray</c> **会抛**（AGENTS.md 记录过的真机崩溃），
    /// 所以必须先判类型。
    /// </summary>
    internal Task OnChangeChannel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Chat_ChangeChannel(140) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        uint want = player.ChatChannelId;
        if (TryReadFirstNumber(param, out uint parsed))
        {
            want = parsed;
        }
        else
        {
            Log.Warn($"[session {session.Id}] Chat_ChangeChannel(140) 的 tbParam 不是「首元素为数字的数组」"
                     + $"（param={param?.ToJsonString() ?? "nil"}），保持当前频道 {want}");
        }

        return ApplyChannel(session, player, cmd, Clamp(want), "指定");
    }

    /// <summary>
    /// cmd 141 随机切换世界频道。
    ///
    /// ★ 上行**根本没有 tbParam 键**（<c>UI_Chat.lua:1450</c> 调
    /// <c>LuaCall:CallServer(LuaCall.Chat_RandomChangeChannel)</c> 第二实参为 nil，
    /// <c>Json.Encode</c> 丢掉 nil 值）⇒ 服务端侧 <c>param == null</c>。
    /// ★ 随机由**服务端**决定（原服函数名就叫 <c>Player:RandomChangeChatChannel</c>，
    /// <c>LuaCall.lua:2030</c>）。
    /// </summary>
    internal Task OnRandomChangeChannel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] Chat_RandomChangeChannel(141) 到达但会话未登录，忽略");
            return Task.CompletedTask;
        }

        uint next = RandomChannel(player.ChatChannelId);
        return ApplyChannel(session, player, cmd, next, "随机");
    }

    /// <summary>随机一个与当前不同的合法频道号。</summary>
    internal uint RandomChannel(uint current)
    {
        for (int i = 0; i < 8; i++)
        {
            uint n = (uint)_rng.Next((int)MinChannel, (int)MaxChannel + 1);
            if (n != current)
                return n;
        }
        // 极小概率连续撞上当前频道：确定性地退到相邻频道，保证「切了」这个语义成立
        return current >= MaxChannel ? MinChannel : current + 1;
    }

    /// <summary>
    /// 落盘新频道号 + 回裸数组 + 补推该频道历史。
    ///
    /// ★ 回包必须是**长度 ≥ 1 且首元素是数字**的数组：
    /// <c>UI_Chat.lua:1538..1544 OnSuccessChangeChannel</c> 判
    /// <c>if tbParam and #tbParam &gt; 0 and type(tbParam[1]) == 'number'</c>，
    /// 不满足就什么都不做（空数组 <c>[]</c> 会被静默忽略）。
    /// ★ <c>sCmd</c> 原样回（140 回 140、141 回 141）：客户端按 id 查 <c>LuaCall.tbCMD</c> 分发。
    /// ★ 成功回包**不带 <c>nError</c>**：<c>LuaCall.lua:2017/2033</c> 是 <c>if tbParam.nError then</c>
    /// 真值判断，回 0 也会被当成出错。裸数组天然没有这个键。
    /// ★ 界面关着时客户端会丢弃回包（<c>LuaCall.lua:2020/2036</c> 有 <c>if UI.IsOpen("UI_Chat")</c>），
    /// 所以服务端必须自己落盘 —— 下次登录靠 pf 1005 的 <c>chatchannelid</c> 同步。
    /// </summary>
    private Task ApplyChannel(ClientSession session, Player player, int cmd, uint channel, string how)
    {
        uint old = player.ChatChannelId;
        player.ChatChannelId = channel;
        _ctx.Store.Save(player);

        _ctx.LuaCalls.CallClient(session, cmd, BuildChannelPayload(channel));

        int replayed = ReplayWorldHistory(session, channel);
        if (replayed == 0)
        {
            session.Push(GamePf.NoticeChattingMsg,
                         BuildSystemNotice($"已进入 {channel} 号频道。"));
        }

        Log.Info($"[session {session.Id}] Chat_{(cmd == RandomChangeChannelCmd ? "RandomChangeChannel(141)" : "ChangeChannel(140)")}"
                 + $" {how} 切频道 {old} → {channel}（回裸数组 [{channel}]，"
                 + $"{(replayed > 0 ? $"补推历史 {replayed} 条" : "该频道无历史，推 1 条系统提示")}）");
        return Task.CompletedTask;
    }

    /// <summary>把频道号钳到客户端允许的 1..999（本服**永不回失败**，见类注释 ⑥）。</summary>
    internal static uint Clamp(uint channel)
        => channel < MinChannel ? MinChannel : (channel > MaxChannel ? MaxChannel : channel);

    /// <summary>140/141 的回包：**裸数组** <c>[新频道号]</c>（<c>UI_Chat.lua:1539</c>）。纯函数，便于单测。</summary>
    internal static JsonArray BuildChannelPayload(uint channel) => new(channel);

    // =================================================================
    //  NoticeChattingMsg 构造
    // =================================================================

    /// <summary>
    /// 构造一条玩家消息。**field 1..8 全部显式赋值**（required 掩码 <c>0xFF</c>，见类注释 ③）。
    /// </summary>
    internal NoticeChattingMsg BuildNotice(Player player, uint type, string message,
                                           uint receiverId, string receiverName)
        => new()
        {
            Type = type,                                   // 1 required
            SenderId = SenderIdOf(player),                 // 2 required（★ 见 SenderIdOf 的 uint32 偏差）
            ReceiverId = receiverId,                       // 3 required（非私聊必须显式写 0）
            SenderName = Display(player.Name),             // 4 required
            ReceiverName = receiverName ?? "",             // 5 required（非私聊必须显式写 ""）
            Message = message,                             // 6 required
            Time = NowSeconds(),                           // 7 required
            SenderLevel = (uint)Math.Max(0, player.Level),  // 8 required
            Girlid = GirlIdOf(player),                     // 9  省了头像变问号
            Headframe = HeadframeOf(player),               // 10 ★ 组合值，命不中白名单会让客户端抛
            Channelid = player.ChatChannelId,              // 11 客户端从不读，填上只为语义完整
        };

    /// <summary>
    /// 构造一条**系统频道**公告（type=2）。
    ///
    /// <c>ShowSystem</c>（<c>UI_Chat.lua:808..820</c>）只用 <c>info</c> + <c>timeString</c>，
    /// 所以 <c>girlid</c>/<c>headframe</c> 不填；但 field 1..8 仍然必须齐全。
    /// <c>sender_id=1</c> / <c>sender_name="system"</c> 与原服的另一条系统频道通道口径一致
    /// （<c>BannerLogic.lua:615</c> <c>AddNewChatMessage(2, 1, "system", ...)</c>）。
    /// </summary>
    internal static NoticeChattingMsg BuildSystemNotice(string text) => new()
    {
        Type = ChannelSystem,
        SenderId = SystemSenderId,
        ReceiverId = 0,
        SenderName = SystemSenderName,
        ReceiverName = "",
        Message = text,
        Time = NowSeconds(),
        SenderLevel = 0,
    };

    /// <summary>
    /// 发言者 id。
    ///
    /// ★★★ 协议约束：<c>NoticeChattingMsg.sender_id</c> 是 **uint32**（全 gsproto 里只有它和
    /// <c>receiver_id</c> 这两个角色 id 是 32 位；<c>PlayerNtf.roleid</c> 是 uint64），
    /// 而客户端 <c>me.id</c> 拿的就是 uint64 的 <c>roleid</c>，并**直接与本字段比较**
    /// 来判断「这条消息是不是我自己发的」。
    ///
    /// 所以 <c>roleid</c> **必须** ≤ <c>0xFFFFFFFF</c>，否则截断后 <c>me.id == sender_id</c> 恒假 ⇒
    /// 自己的消息走 <c>ShowLeft</c>（显示在左侧、带头像等级名字）、且每条都在主城弹飘字
    /// （<c>PlayerLogic.lua:283</c>）。
    ///
    /// 本轮已把 <c>World.DeriveRoleId</c> 的值域收进 uint32、并加了旧存档迁移
    /// （<c>World.MigrateRoleId</c>）⇒ 正常路径上这里**不会**发生截断。
    /// 约束由 <c>WorldRoleIdTests</c> 与本域的 <c>发言者id_新口径下与roleid完全相等</c> 盯着。
    /// 取证见 `docs/05` §18.5。
    /// </summary>
    internal static uint SenderIdOf(Player player) => Truncate(player.Roleid);

    /// <summary>uint64 roleid → uint32（协议限制，见 <see cref="SenderIdOf"/>）。</summary>
    internal static uint Truncate(ulong roleid) => unchecked((uint)roleid);

    /// <summary>
    /// 头像用的少女 id（<c>Npc/icon_n_girl%d.png</c>，<c>UI_Chat.lua:640/727</c>）。
    ///
    /// 优先用「看板娘」任务值 <c>(2,2)</c>（<c>GirlCommon:MainGirl</c>，`GmUnlock.cs:233..239`），
    /// 其次用第一张持有的角色卡，最后兜底 1。
    /// 填 0 不会崩，但 <c>Sprite</c> 加载失败会回落成问号图 <c>Num/what.png</c>（<c>Lib.lua:471..476</c>）。
    /// </summary>
    internal static uint GirlIdOf(Player player)
    {
        int main = player.GetTask(GmUnlock.MainGirlGroup, GmUnlock.MainGirlTaskId);
        if (main > 0)
            return (uint)main;
        if (player.Girls.Count > 0)
            return player.Girls[0].Girlid;
        return 1;
    }

    /// <summary>
    /// 组合出 pf 1012 的 <c>headframe</c>（<c>头像框 id * 1000 + 气泡 id</c>，<c>PlayerLogic.lua:183..184</c>）。
    ///
    /// 头像框取 <see cref="Player.HeadFrame"/>；气泡取名片装扮里的
    /// <c>ChatBubbleID</c>（<see cref="MainCityFeature.ReadCardState"/>，来源 <c>LuaCall.lua:1189</c> 注释）。
    /// 两者都要过 <see cref="ChatTable"/> 白名单，理由见 <see cref="ChatTable"/> 的类注释（客户端无 nil 保护）。
    /// </summary>
    internal uint HeadframeOf(Player player)
    {
        uint frame = (uint)Math.Max(0, player.HeadFrame);
        uint bubble = (uint)Math.Max(0, MainCityFeature.ReadCardState(player).ChatBubbleId);
        if (!_table.IsBabbleKnown(bubble))
            bubble = 0;
        return _table.ComposeHeadframe(frame, bubble);
    }

    // =================================================================
    //  小工具
    // =================================================================

    /// <summary>频道类型的中文名（仅日志用）。取证 <c>PlayerLogic.lua:26..30</c>。</summary>
    internal static string ChannelName(uint type) => type switch
    {
        ChannelWorld => "世界",
        ChannelSystem => "系统",
        ChannelWhisper => "私聊",
        ChannelClub => "社团",
        ChannelTeam => "队伍",
        _ => $"未知({type})",
    };

    private static uint NowSeconds() => (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    /// <summary>空名字会被渲染成空白，给一个可见的兜底（<c>sender_name</c> 是 required，不能是 null）。</summary>
    private static string Display(string? name) => string.IsNullOrEmpty(name) ? "无名" : name;

    /// <summary>日志用的正文预览（截断，避免长文本刷屏）。</summary>
    private static string Preview(string message)
        => message.Length <= 32 ? $"「{message}」" : $"「{message[..32]}…」({message.Length} 字)";

    /// <summary>
    /// 从 tbParam 里取「首元素数字」。<paramref name="param"/> 可能是
    /// <c>JsonArray</c>（140 的 <c>[nNum]</c>）、<c>null</c>（141）、
    /// 或 <c>JsonObject</c>（防御性：万一别的入口换了形状）。
    /// </summary>
    internal static bool TryReadFirstNumber(JsonNode? param, out uint value)
    {
        value = 0;
        JsonNode? first = param switch
        {
            JsonArray arr when arr.Count > 0 => arr[0],
            JsonObject obj => obj["nNum"] ?? obj["channel"],
            _ => null,
        };
        if (first is null)
            return false;

        try
        {
            long v = first.GetValue<long>();
            if (v < 0)
                return false;
            value = (uint)v;
            return true;
        }
        catch
        {
            // Lua 侧可能给字符串/浮点
            return double.TryParse(first.ToString(), out var d) && d >= 0 && Set(out value, (uint)d);
        }
    }

    private static bool Set(out uint slot, uint v)
    {
        slot = v;
        return true;
    }

    private static ChattingReq? TryParse(ClientSession session, Packet packet)
    {
        try
        {
            return SssjServer.Protocol.ProtoMessage.Parse<ChattingReq>(packet.Payload);
        }
        catch (Exception e)
        {
            Log.Warn($"[session {session.Id}] 解析 ChattingReq(1010) 失败：{e.Message}");
            return null;
        }
    }

    private static long ReadLastSpeak(Player player)
    {
        if (player.Extra.TryGetValue(ExtraKey, out var node) && node is JsonObject obj
            && obj[LastSpeakKey] is JsonNode v)
        {
            try
            {
                return v.GetValue<long>();
            }
            catch
            {
                return 0;
            }
        }
        return 0;
    }

    private void StampSpeakTime(Player player)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (player.Extra.TryGetValue(ExtraKey, out var node) && node is JsonObject obj)
            obj[LastSpeakKey] = now;
        else
            player.Extra[ExtraKey] = new JsonObject { [LastSpeakKey] = now };
        _ctx?.Store.Save(player);
    }
}
