namespace SssjServer.Core;

/// <summary>
/// 服务端运行配置。缺省值与旧 bootstrap_server.py / work/server 对齐。
/// </summary>
public sealed class ServerOptions
{
    /// <summary>下发给客户端的 TCP 网关地址（设备侧即宿主地址，WSL2 mirrored 下为 10.0.2.2）。</summary>
    public string GateHost { get; set; } = "10.0.2.2";

    public int GatePort { get; set; } = 6600;

    public int HttpPort { get; set; } = 8080;

    /// <summary>HTTP 引导服务的监听地址。</summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    public string ServerName { get; set; } = "Local";

    public int ServerId { get; set; } = 1;

    public int Aid { get; set; } = 1;

    public int Sid { get; set; } = 1;

    public string DataDirectory { get; set; } = "data";

    /// <summary>
    /// 建号时是否跳过新手教程：为 true 时给新号预置任务值 group=1 / task=15 → value=6，
    /// 使客户端 PlayerLogic.IsFirstLevelComplete() 返回 true，从而进入主城
    /// （Map.LoadById(2) → UI.Open('UI_Main')）而非教程分支。对已存盘玩家不生效（存档值优先）。
    /// </summary>
    public bool SkipTutorial { get; set; } = true;

    /// <summary>
    /// 建号时是否发放起始角色卡 + 编队（见 Game.StarterLoadout）。
    /// 为 true 时给新号预置一小队角色卡（Item 与对应 Girl）并建 1 号编队；
    /// 对已存盘玩家不生效（存档值优先）。默认 true。
    /// </summary>
    public bool GrantStarter { get; set; } = true;

    /// <summary>
    /// 客户端配置表目录（<c>extracted/setting</c>）。服务端与客户端读同一份表，
    /// 关卡体力消耗 / 通关经验 / 等级体力上限都来自这里（Chapter.txt、PlayerExp.txt）。
    /// 相对路径按进程工作目录解析；目录缺失时相关逻辑降级（体力不扣、经验为 0）。
    /// </summary>
    public string SettingDirectory { get; set; } = "extracted/setting";

    /// <summary>
    /// 进关是否真的扣体力（按 Chapter.txt 的 PreCost+Vigour）。
    /// 关掉可无限刷关，方便压测；默认 true（与原服一致）。
    /// </summary>
    public bool SpendVigour { get; set; } = true;

    /// <summary>
    /// 登录时把体力补满到该等级上限（PlayerExp.txt 的 Spirit 列）。
    /// 原服只按 6 分钟/点自然恢复；私服默认 true 便于连续验收。
    /// </summary>
    public bool RefillVigourOnLogin { get; set; } = true;

    /// <summary>
    /// 是否生成**机器人好友与假社团**（默认 <c>true</c> = 本地开发用）。
    ///
    /// <para><b>★ 为什么需要这个开关</b></para>
    /// 单机本地服没有第二个真玩家，好友的四个页签、社团的发现列表都会是空的
    /// ⇒ 既看不出功能是否实现，也没法做 e2e 验收。所以本服会按账号**确定性**地
    /// 造一批假数据（`FriendFeature.Bots` 16 个机器人、`ClubFeature.DiscoverClubs` 8 个假社团）。
    ///
    /// <para><b>★★ 但对外分发时必须关掉</b></para>
    /// 多个真实玩家同服时，假数据会造成明显的违和感：
    /// 好友列表里有「不存在的人」、给他们送电力没有回应、点进假社团永远没有成员活动。
    /// ⇒ 生产环境用 <c>--bots false</c> 关闭，好友/社团只保留**真实玩家**之间的互动。
    ///
    /// 关掉之后的表现（有意为之，不是 bug）：
    /// 新号好友列表为空、社团发现列表为空 —— 与原厂「服务器刚开、还没人」时一致。
    /// </summary>
    public bool EnableBots { get; set; } = true;

    // ================================================================
    //  资源下发（方案 A：客户端自动从我们这里下载全部资源）
    //  取证：work/checkupdate-forensics.md + work/checkupdate-mainsession.md
    // ================================================================

    /// <summary>
    /// 资源根目录 —— 里面按 <c>&lt;bundle 名&gt;</c> 平铺放资源（例 <c>map/maplogin_prefab.u</c>）。
    ///
    /// <para><b>★ 客户端拉资源的 URL 形状</b></para>
    /// <c>Game.Dir.update</c>（RVA <c>0x1E8EFE8</c>）= <c>baseUpdateUrl + "Bundles" + "/"</c>
    /// ⇒ 实际请求是 <c>{updateUrl}Bundles/&lt;bundle 名&gt;</c>。
    /// 本服务据此把 <c>/Bundles/xxx</c> 映射到 <c>{AssetDirectory}/xxx</c>。
    /// 空串 = 不提供资源下发（保持「跳过更新」模式）。
    /// </summary>
    public string AssetDirectory { get; set; } = "";

    /// <summary>
    /// 下发给客户端的资源清单版本（<c>checkupdate</c> 回包的 <c>urllist.buildversion</c>）。
    ///
    /// ★★ <b>这个字段才是「要不要更新」的开关</b>（不是 <c>msg</c>）：
    /// <c>0</c> ⇒ 客户端打 <c>"buildversion is 0. dont update"</c>（字面量 <c>0x52C06B8</c> 附近，
    /// 判定在 <c>0x1F548C0/C4</c>）并跳过整个资源更新；非 0 ⇒ 进下载流程。
    /// 走方案 A 时设成清单里的真实版本（备份那份是 <b>1365</b>）。
    ///
    /// <para><b>★★★ 热更新场景请改用 <see cref="AssetBuildVersionAuto"/></b>
    /// （命令行 <c>--asset-build-version auto</c>）：手写死的版本号在
    /// <c>tools/hotfix.py</c> bump 清单之后会**静默失效**（客户端拿到旧号 ⇒ 什么都不下 ⇒
    /// 两端日志全干净），详见 <see cref="AssetManifest"/> 的类注释。</para>
    /// </summary>
    public int AssetBuildVersion { get; set; } = 0;

    /// <summary>
    /// 是否**每次**从 <c>{AssetDirectory}/version.data</c> 现读 <c>buildVersion</c>
    /// （命令行 <c>--asset-build-version auto</c>）。
    ///
    /// <para>为 true 时 <see cref="AssetBuildVersion"/> 被忽略。这是热更新的**推荐姿势**：
    /// 发布新补丁后**不必重启服务端**，客户端下次 <c>checkupdate</c> 就拿到新版本号。</para>
    /// </summary>
    public bool AssetBuildVersionAuto { get; set; }

    /// <summary>新建号赠送的金币（MoneyLogic.eMoneyType_Gold = 2）。</summary>
    public int StartGold { get; set; } = 100000;

    /// <summary>新建号赠送的免费钻石（MoneyLogic.eMoneyType_Free_Diamond = 3）。</summary>
    public int StartDiamond { get; set; } = 3000;

    /// <summary>
    /// GM 控制台的 Lua 投递文件。服务端每 500ms 检查一次，内容变化就把它推给所有在线客户端执行
    /// （LuaCall.DoLuaString=999999 → 客户端 GM.DoCommand("CL", ...)，输出进 logcat）。
    /// 置空字符串可关闭。
    /// </summary>
    public string GmFile { get; set; } = "work/gm.lua";

    /// <summary>
    /// **服务端侧** GM 命令投递文件（与 <see cref="GmFile"/> 相对：那个执行客户端 Lua，
    /// 这个执行服务端命令）。服务端每 500ms 检查一次，内容变化就逐行执行，
    /// 用于验收时快速造数据：解锁功能（记关卡通关）、发货币/物品、改等级。
    /// 命令集见 <c>SssjServer.Game.Handlers.ServerConsoleFeature</c>。置空字符串可关闭。
    /// </summary>
    public string ServerGmFile { get; set; } = "work/gmsrv.txt";

    // ================= GM 测试账号（全解锁） =================

    /// <summary>
    /// 被视为「GM 测试账号」的账号名清单。
    ///
    /// <para>为什么缺省是一个**空串**：<c>package.txt</c> 里 <c>platform="selftest"</c> 时
    /// <c>GameManager:UseSDK</c> 返回 false，账号取本地 <c>PlayerPrefs['account']</c>，
    /// 而它从未被写过 ⇒ 客户端在 pf 1102/1001 里上报的 <c>account</c> 就是**空字符串**
    /// （实测存档落在 <c>data/_.player.json</c>，其 <c>"Account": ""</c>）。
    /// 也就是说「我们测试用的那个账号」在本地私服里就是这个空账号。</para>
    ///
    /// 特殊值 <c>"*"</c> = 所有账号都当 GM（本地压测时省事）。
    /// 匹配区分大小写（与 <c>World</c> 的账号字典同口径，<see cref="StringComparer.Ordinal"/>）。
    /// </summary>
    public string[] GmAccounts { get; set; } = { "" };

    /// <summary>
    /// GM 账号登录时自动「全解锁」（全道具 / 全角色卡 / 全关卡三星 / 全任务完成）。
    ///
    /// 实现是**幂等**的，并且发生在 pf 1005 <c>PlayerNtf</c> 之前（<c>OnPlayerPrepare</c> 钩子），
    /// 所以全部数据随登录本来就要发的那几条全量包下发，**不产生额外封包**。
    /// 只在存档里记的 revision 落后于 <c>GmUnlock.Revision</c> 时才真正重放一遍
    /// （见 <c>SssjServer.Game.GmUnlock</c>），因此日常登录几乎零开销。
    /// </summary>
    public bool GmFullUnlock { get; set; } = true;

    /// <summary>Treat every expired event window as still running (see SettingTable.ExtendEndDates).</summary>
    public bool AllEventsOpen { get; set; }

    /// <summary>全解锁时每种**非货币**道具给的数量（<c>ItemList.txt</c> 逐条）。</summary>
    public int GmItemCount { get; set; } = 999;

    /// <summary>
    /// 全解锁时每种货币设成的绝对值。
    /// 用「设成绝对值」而不是「累加」是为了幂等（重复执行不会翻倍、也不会溢出 int32）。
    /// </summary>
    public int GmMoneyAmount { get; set; } = 100_000_000;

    /// <summary>
    /// 全解锁时把任务/成就标记成「已领奖」（<c>true</c>，缺省）还是「可领取」（<c>false</c>）。
    ///
    /// 缺省 true 的理由：3300+ 条任务同时变成「可领取」会让主城任务面板与红点全部亮起，
    /// 且客户端「一键领取」会一次性向服务端提交上千条领奖 —— 对验收有害无益。
    /// 需要验「领奖链路」时把它设成 false，再配合 <c>gmsrv.sh 'allmission open'</c>。
    /// </summary>
    public bool GmMissionClaimed { get; set; } = true;

    /// <summary>
    /// 全解锁「全道具」时跳过**图标在本地资源快照里缺失**的道具
    /// （清单 <c>extracted/setting/MissingIcon.txt</c>，用 <c>tools/probe_icons.sh</c> 生成）。
    ///
    /// 为什么默认 true：这些道具在仓库里会画成灰底问号
    /// （客户端 <c>Lib.lua:475</c> 的兜底图 <c>Num/what.png</c>），
    /// 而它们对应的功能（塔活动 / 宠物 / 外景 DIY / 少女皮肤）本来也没实装 ——
    /// 发了只添 136 个问号格子，没有任何验收价值。
    /// 想拿回「真·全道具」就设成 false。
    /// 详见 <c>SssjServer.Game.Config.MissingIconTable</c>。
    /// </summary>
    public bool GmSkipMissingIcon { get; set; } = true;
}
