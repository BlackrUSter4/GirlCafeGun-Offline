namespace SssjServer.Game;

/// <summary>
/// Game 层实际处理的 pf 常量（仅列出本层用到的）。
///
/// 说明：完整的 pf 表在 <c>SssjServer.Protocol.Pf</c>（由另一 agent 从
/// docs/pf-table.json 生成）。此处不引用它，避免在 ProtoGen 未就绪时出现编译依赖；
/// 与生成表 reconcile 时，数值必须一致 —— 语义以 docs/03-login-flow.md 为准。
/// </summary>
public static class GamePf
{
    /// <summary>pf 1001 — C2S LoginReq。</summary>
    public const int Login = 1001;

    /// <summary>pf 1002 — S2C LoginRsp（客户端 Game.Login.PlayerLogineRsp）。</summary>
    public const int LoginRsp = 1002;

    /// <summary>pf 1005 — S2C PlayerNtf（客户端 Game.Login.PlayerLogined，真正进游戏）。</summary>
    public const int PlayerNtf = 1005;

    /// <summary>pf 1006 — C2S RenameReq / S2C RenameRsp（空消息；客户端 Game.Player.SendRename 只按 serial 回调）。</summary>
    public const int Rename = 1006;

    /// <summary>pf 1006 — S2C RenameRsp（与请求同 pf，空 body，仅触发 serial 回调）。</summary>
    public const int RenameRsp = 1006;

    /// <summary>pf 1008 — C2S 心跳。</summary>
    public const int Alive = 1008;

    /// <summary>pf 1009 — S2C KeepAlive（客户端 UNet.ReConnection.PingRsp）。</summary>
    public const int KeepAlive = 1009;

    /// <summary>
    /// pf 1010 — C2S <c>ChattingReq</c>（聊天正文上行）。
    /// 取证 docs/pf-table.txt:10 <c>Send&lt;gsproto.ChattingReq&gt;</c> ←
    /// <c>Game.Player.SendChatMessage</c>。★ 聊天正文**不走 LuaCall**，只有「切频道」
    /// 走 LuaCall 140/141。
    /// </summary>
    public const int Chatting = 1010;

    /// <summary>
    /// pf 1011 — S2C <c>ChattingRsp</c>（1010 的应答，**空消息体**）。
    /// 取证 docs/pf-table.txt:11 <c>Register&lt;gsproto.ChattingRsp&gt;</c> ←
    /// <c>Game.Player.OnReciveChatMessageRsp</c>。
    /// </summary>
    public const int ChattingRsp = 1011;

    /// <summary>
    /// pf 1012 — S2C <c>NoticeChattingMsg</c>（聊天消息推送，服务端主动发）。
    /// 取证 docs/pf-table.txt:12 <c>Register&lt;gsproto.NoticeChattingMsg&gt;</c> ←
    /// <c>Game.Player.OnReciveChatMessage</c>。
    /// </summary>
    public const int NoticeChattingMsg = 1012;

    /// <summary>pf 1022 — C2S 通用 RPC（C2SCallReq，sFunction=="LuaCall"）。</summary>
    public const int C2SCall = 1022;

    /// <summary>pf 1023 — S2C C2SCallRsp（1022 的 ack）。</summary>
    public const int C2SCallRsp = 1023;

    /// <summary>pf 1024 — S2C NtfS2CCall（服务端主动 LuaCall 推送）。</summary>
    public const int NtfS2CCall = 1024;

    /// <summary>pf 1026 — S2C TaskValueSyncRsp（服务端推送全部任务值，serial=0）。</summary>
    public const int TaskValueSyncRsp = 1026;

    /// <summary>pf 1027 — C2S TaskValueChangeReq（客户端提交任务值变化）。</summary>
    public const int TaskValueChangeReq = 1027;

    /// <summary>pf 1028 — S2C TaskValueChangeRsp（1027 的应答，echo 已存值）。</summary>
    public const int TaskValueChangeRsp = 1028;

    /// <summary>pf 1029 — S2C PlayerDataNtf（客户端 Game.Player.OnPlayerDataChange，updatetype/updatevalue 对）。</summary>
    public const int PlayerDataNtf = 1029;

    /// <summary>pf 1030 — S2C GirlDataNtf（客户端 Game.Player.OnGirlUpdate）。</summary>
    public const int GirlDataNtf = 1030;

    /// <summary>pf 1031 — S2C ItemUpdateNtf（客户端 Game.EquipManager.OnUpdateItem，增量背包）。</summary>
    public const int ItemUpdateNtf = 1031;

    /// <summary>pf 1032 — S2C MoneyUpdateNtf（客户端 Game.Player.OnMoneyChange，单条货币）。</summary>
    public const int MoneyUpdateNtf = 1032;

    /// <summary>pf 1033 — S2C FormationInfoUpdateNtf（客户端 Game.EquipManager.UpdateFormationNtf）。</summary>
    public const int FormationInfoUpdateNtf = 1033;

    /// <summary>pf 1034 — S2C ChapterUpdateNtf（客户端 Game.Player.OnChapterChange → MapList.ReflashLevelList）。</summary>
    public const int ChapterUpdateNtf = 1034;

    /// <summary>
    /// pf 1035 — S2C PhoneMsgNtf（客户端 <c>Game.Login.PlayerPhoneMsg</c>，RVA <c>0x1C8CC98</c>）。
    ///
    /// ★★★ 终端「私信 + 朋友圈」的**唯一数据源**：客户端列表读
    /// <c>Game.PhoneMsgMgr</c> 的缓存，而那份缓存只由本 pf 填充
    /// （→ <c>Game.PhoneMsgMgr.PhoneMsgData</c> `0x1844BE0`）
    /// ⇒ 不推它两个页签永远空白（LuaCall 侧 12 个 nCmd 做得再全也看不见）。
    ///
    /// ★★ **全量覆盖语义**：<c>PhoneMsgData</c> 对私信/帖子两个字典都是
    /// 先 <c>SimpleDictionary.Clear()</c>（`0x1844C78` / `0x1844CF0`）再逐条 Add
    /// ⇒ 每次推都要带完整状态。实现见 <c>Handlers.PhoneMsgFeature.PushAll</c>。
    /// </summary>
    public const int PhoneMsgNtf = 1035;

    /// <summary>pf 1063: the node tower partner changed (gsproto.PetUpdateNtf, read by PetLogic.lua).</summary>
    public const int PetUpdateNtf = 1063;

    /// <summary>pf 1104 — S2C ItemNtf（服务端推送全量背包，客户端 Game.Login.ItemNtfMsg）。</summary>
    public const int ItemNtf = 1104;

    // ================================================================
    //  邮件（终端界面 UI_Phone 的 TYPE_EMAIL = 2 页签）
    //  取证 docs/pf-table.txt:13..21，客户端侧全在 Game.EmailManager。
    //  ★ 1014 是**服务端主动推**：客户端没有任何地方发 GetMailListReq
    //    （实测打开邮件页签服务端收到零请求），见 Handlers/MailFeature.cs。
    // ================================================================

    /// <summary>
    /// pf 1014 — S2C <c>GetMailListRsp</c>（邮件列表**全量**）。
    /// 客户端 <c>Game.EmailManager.LoadMailInfo</c>（RVA <c>0x1877600</c>）会先
    /// <c>_maillist.Clear()</c>（<c>0x1877730</c>）再填 ⇒ **全量替换**语义。
    /// </summary>
    public const int GetMailListRsp = 1014;

    /// <summary>pf 1015 — S2C <c>AddMailNtf</c>（新邮件单封追加，<c>AddMailInfo 0x1877988</c>）。</summary>
    public const int AddMailNtf = 1015;

    /// <summary>pf 1016 — C2S <c>GetMailItemsReq</c>（领附件，<c>ClickGetMailItem</c>）。</summary>
    public const int GetMailItems = 1016;

    /// <summary>pf 1017 — S2C <c>GetMailItemsRsp</c>（<c>GetMailItem 0x1877CC4</c>）。</summary>
    public const int GetMailItemsRsp = 1017;

    /// <summary>pf 1018 — C2S <c>SetReadFlagReq</c>（标已读，<c>ClickSetMailRead</c>）。</summary>
    public const int SetReadFlag = 1018;

    /// <summary>pf 1019 — S2C <c>SetReadFlagRsp</c>（<c>SetMailRead 0x18786D4</c>）。</summary>
    public const int SetReadFlagRsp = 1019;

    /// <summary>pf 1020 — C2S <c>DelMailReq</c>（删邮件，<c>ClickDeleteMail</c>）。</summary>
    public const int DelMail = 1020;

    /// <summary>pf 1021 — S2C <c>DelMailRsp</c>（<c>DelMail 0x1878BF4</c>）。</summary>
    public const int DelMailRsp = 1021;

    /// <summary>pf 1048 — C2S GetHouseInfoReq（进咖啡厅自动请求）。</summary>
    public const int GetHouseInfo = 1048;

    /// <summary>pf 1049 — S2C GetHouseInfoRsp。</summary>
    public const int GetHouseInfoRsp = 1049;

    /// <summary>pf 1102 — C2S VerifyReq（明文，VerifyRsp 后在启用加密）。</summary>
    public const int Verify = 1102;

    /// <summary>pf 1105 — C2S TimeReq（时间同步）。</summary>
    public const int Time = 1105;

    /// <summary>pf 1106 — S2C TimeRsp。</summary>
    public const int TimeRsp = 1106;

    /// <summary>
    /// pf 1107 — C2S <c>OtherHouseInfoReq</c>（访问别人的咖啡馆）。
    /// 取证 <c>docs/pf-table.txt:68</c> ← <c>Game.NCafeNetController$$ReqOtherHouseInfo</c>
    /// （RVA <c>0x1D3354C</c>，<c>mov w0,#0x453</c>）。见 <see cref="Handlers.VisitCafeFeature"/>。
    /// </summary>
    public const int OtherHouseInfo = 1107;

    /// <summary>
    /// pf 1108 — S2C <c>OtherHouseInfoRsp</c>。客户端 <c>OnOtherHouseInfoRsp</c>
    /// （RVA <c>0x1D31F14</c>）收到后**立刻 <c>Map.Load(6)</c>** 切进访客模式咖啡馆。
    /// </summary>
    public const int OtherHouseInfoRsp = 1108;

    /// <summary>pf 1109 — C2S HouseRandomListReq（进咖啡厅自动请求）。</summary>
    public const int HouseRandomList = 1109;

    /// <summary>pf 1110 — S2C HouseRandomListRsp。</summary>
    public const int HouseRandomListRsp = 1110;
}
