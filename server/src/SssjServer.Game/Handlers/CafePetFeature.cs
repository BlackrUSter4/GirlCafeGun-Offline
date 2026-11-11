using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 咖啡馆「宠物 / 食盆 / 咖啡馆食物点数」子域（<c>sFunction = "NCafePetLogic"</c> 一族）。
///
/// <para><b>0. 这个域为什么是一个 sFunction 装 18 个 sCmd</b></para>
/// 上行只有一个入口：<c>NCafePetLogic:CallServer(tbParam)</c> →
/// <c>SendCmd('NCafePetLogic', tbParam)</c>（<c>NCafePetLogic.lua:1031..1033</c>），
/// 真正的命令在 <c>tbParam.sCmd</c> 里。下行入口对称：
/// <c>S2C.NCafePetLogic(tbParam)</c>（<c>S2CCall.lua:532..536</c>）
/// → <c>NCafePetLogic:Dispatcher(tbParam.sCmd, tbParam)</c>。
///
/// <para><b>1. ★★ 最大的坑：下行 <c>sCmd</c> 不是上行 <c>sCmd</c>，而且没有 nil 保护</b></para>
/// <code>
/// -- NCafePetLogic.lua:1036..1042
/// function tbClass:Dispatcher(sCmd,tbParam)
///     if not sCmd or type(sCmd) ~= "string" then return end
///     local cmd = sCmd.."Rsp"
///     self[cmd](self,tbParam);          -- ← 没有 if self[cmd] then！
/// end
/// </code>
/// 也就是：<b>下行 <c>sCmd</c> 必须能拼出一个真实存在的 <c>&lt;sCmd&gt;Rsp</c> 函数</b>，
/// 否则 <c>attempt to call a nil value</c> 直接抛在客户端。
/// 而这一族里有 5 个上行命令**根本没有同名 Rsp**：
///
/// | 上行 sCmd | 有 <c>&lt;sCmd&gt;Rsp</c>？ | 服务端必须回的 sCmd |
/// | --- | --- | --- |
/// | <c>OnFoodBoxOpen</c> | ✗ | <c>UpdateFoodBoxs</c> |
/// | <c>OnBoxFoodDone</c> | ✗ | <c>SetFood</c> |
/// | <c>OnGetGiveupItem</c> | ✗ | <c>UpdateFoodBoxs</c> |
/// | <c>OnNewPet</c> | ✗ | <c>AdoptPet</c>（<c>AdoptPetRsp</c>，<c>:718</c>） |
/// | <c>OnGiveupPet</c> | ✗ | <c>GiveUpPet</c>（<c>GiveUpPetRsp</c>，<c>:793</c>） |
/// | <c>DelPet</c> | ✗ | <c>GiveUpPetOwned</c>（<c>GiveUpPetOwnedRsp</c>，<c>:962</c>） |
/// | <c>BuyCafeFood</c> | ✓ 但 <c>BuyCafeFoodRsp</c> 是**空函数** | <c>GetCafeFoodNum</c> |
///
/// ⇒ 本实现绝不「原样回显 sCmd」，一律走 <see cref="ReplySCmd"/> 的白名单映射。
///
/// <para><b>2. <c>nError</c></b></para>
/// 本族是字符串命令族，按 AGENTS.md §5 / docs/05 §0.1 成功回包**带 <c>nError = 0</c>**。
/// 需要说明的是：<c>S2C.NCafePetLogic</c> 自己**不判** <c>nError</c>
/// （不像 <c>S2CCall.lua:154/227/247</c> 那三个 <c>if tbParam.nError &gt; 0</c>），
/// 所以这里带 0 是「守约定 + 未来兼容」，且已逐个确认 14 个 <c>*Rsp</c> 都不会被它影响。
/// 本族真正的失败开关是另一个键：<c>SetFoodRsp</c> 的
/// <c>if tbParam.Error then return end</c>（<c>NCafePetLogic.lua:229</c>，**大写 E、真值判断**，
/// 所以 <c>Error = 0</c> 也会被当失败）——见 <see cref="BuildErrorPayload"/>。
///
/// <para><b>3. 食盆三件套的字段与状态机</b></para>
/// 客户端把一个食盆压成一个整数 <c>foodBox{i} = state + 10 * type</c>
/// （<c>NCafePetLogic.lua:86</c> / <c>:117</c> / <c>:248</c>），state 由 <c>type</c>/<c>pet</c> 推出：
/// <list type="table">
///   <item><term>0 未解锁</term><description><b>元素里没有 <c>type</c> 字段</b>（或整个元素不存在）——
///     四个 <c>elseif</c> 全部以 <c>if v.type and ...</c> 开头，nil 就落到初值 0</description></item>
///   <item><term>1 已解锁无食物</term><description><c>type == 0</c> 且没有 <c>pet</c></description></item>
///   <item><term>2 有食物倒计时</term><description><c>type ~= 0</c> 且 <c>type ~= 9999</c>（type 就是罐头 id）</description></item>
///   <item><term>3 有宠物可领养</term><description><c>type == 0</c> 且有 <c>pet</c></description></item>
///   <item><term>4 有放生礼物</term><description><c>type == 9999</c> 且没有 <c>pet</c></description></item>
/// </list>
///
/// <para><b>4. ★ <c>boxstatelist</c> 按 uid 取、<c>param</c> 按数组下标取 —— 两条路语义不同</b></para>
/// <list type="bullet">
///   <item>进店的 <c>boxstatelist</c>（LuaCall 112）：<c>InitFoodBoxs</c> 用
///     <c>for k,v in pairs(tbParam) do if v.uid == i then</c>（<c>:69..73</c>）——
///     <b>按 <c>uid</c> 匹配</b>，可以只发已解锁的、可以乱序、可以空。</item>
///   <item><c>UpdateFoodBoxsRsp</c>/<c>SetFoodRsp</c> 的 <c>param</c>：
///     <c>for i,v in pairs(tbParam.param) do self['foodBox'..i] = ...</c>（<c>:106</c> / <c>:237</c>）——
///     <b>用的是数组下标</b>，<c>uid</c> 被完全忽略。</item>
/// </list>
/// ⇒ <c>param</c> **必须是长度恰好 3、按 uid 升序的稠密数组**，否则食盆 2 的数据会写到食盆 1 上。
/// 而且 <c>UpdateFoodBoxsRsp</c> 在 <c>:131..136</c> 无条件跑一遍
/// <c>self['foodBox'..i] % 10</c>（i=1..3），少发一格 = <c>nil % 10</c> 直接抛；
/// 更糟的是 <c>UpdateFoodBoxsTime</c>（<c>:189..213</c>）把
/// <c>self['foodBox'..i..'Time'] - GetTime()</c> 放进了**每秒一次的定时器**里，
/// 一旦某格是 nil 就变成「每秒抛一次异常」。见 <see cref="BuildBoxParam"/>。
///
/// <para><b>5. 咖啡馆食物点数存在任务值 group 49（不是 Extra）</b></para>
/// 客户端直接 <c>me:GetTaskValue</c> 自己算，服务端只能写同一批坐标：
/// <list type="bullet">
///   <item><c>(49, 200)</c> = <c>lastCalTime</c>，上次结算时刻（<c>:618</c> / <c>:645</c>）</item>
///   <item><c>(49, 101 + (i-1)*2)</c> = 第 i 格的食物 id，<c>(49, 102 + (i-1)*2)</c> = 剩余秒数，
///     i = 1..20（<c>:623..632</c> / <c>:648..655</c>）</item>
/// </list>
/// group 49 的取证：<c>NCafePetLogic.lua:28</c> 先写 <c>self.TaskGroup = 95</c>，
/// 但紧接着 <c>:29..30</c> <c>loadlua 'Cafe/NCafePetTour.lua'</c> 里
/// <c>NCafePetLogic.TaskGroup = 49</c>（<c>NCafePetTour.lua:11</c>）**覆盖**了它 ——
/// 实际生效的是 <b>49</b>，而 <c>:617</c> / <c>:644</c> 里也硬写成 49，互相印证。
///
/// <para><b>6. 本域不挂 <c>Game.Blocking</c> 遮罩</b></para>
/// <c>grep Blocking UI_Pet*.lua NCafePet*.lua UI_NCafe*.lua</c> 零命中 ⇒ 按 docs/05 §0.4，
/// 未实现的命令只是「点了没反应」，不会卡界面。这也是本轮敢先只做「有取证的那批」的依据。
/// </summary>
public sealed partial class CafePetFeature : IGameFeature
{
    // =================================================================
    //  常量
    // =================================================================

    /// <summary>上下行共用的 sFunction（<c>NCafePetLogic.lua:1032</c> / <c>S2CCall.lua:532</c>）。</summary>
    public const string Function = "NCafePetLogic";

    /// <summary><see cref="Player.Extra"/> 里本子域的键。
    /// 不能塞进 <c>CafeFeature.ExtraKey</c>（"cafe"）—— <c>CafeFeature.WriteState</c>
    /// 每次进店都会**整体覆写**那个对象（CafeFeature.cs 的 WriteState），塞进去会被抹掉。</summary>
    public const string ExtraKey = "cafepet";

    /// <summary>食盆数量 = 3（<c>NCafePetLogic.lua:10</c> <c>tbClass.FoodBoxCount = 3</c>）。</summary>
    public const int FoodBoxCount = 3;

    /// <summary>宠物域任务值 group（见类注释 §5）。</summary>
    public const int TaskGroup = 49;

    /// <summary>第 1 格食物 id 的任务 id（<c>NCafePetLogic.lua:624</c> <c>101 + (i-1)*2</c>）。</summary>
    public const int TaskFoodSlotBegin = 101;

    /// <summary>食物槽位数 = 20（<c>NCafePetLogic.lua:623</c> <c>for i=1,20</c>）。</summary>
    public const int TaskFoodSlotCount = 20;

    /// <summary>上次结算时刻的任务 id（<c>NCafePetLogic.lua:618</c> <c>GetTaskValue(taskGroup, 200)</c>）。</summary>
    public const int TaskLastCalTime = 200;

    /// <summary>宠物图鉴计数起始（<c>NCafePetTour.lua:25</c> <c>PetRikiOffId = 1000</c>）。</summary>
    public const int TaskRikiBegin = 1000;

    /// <summary>食物点数上限（<c>NCafePetLogic.lua:11</c> <c>MaxCafeFoodPoint = 10000</c>）。</summary>
    public const int MaxCafeFoodPoint = 10000;

    /// <summary>
    /// 1 点食物点数 = 12 秒。取证 <c>UI_PetAddBuff.lua:97</c>
    /// <c>local foodPoint = math.ceil((leaveTime)/12)</c>，而 <c>:34</c> 又把
    /// <c>self.FoodPointNum + self.SelectedData.AddPoint</c> 直接和 10000 比 ——
    /// 两个量同单位，所以「点」与「秒」的换算系数就是 12。
    /// ⇒ 满点 10000 点 = 120000 秒 ≈ 33.3 小时。
    /// </summary>
    public const int SecondsPerFoodPoint = 12;

    /// <summary>
    /// 投放罐头后多久吸引到宠物 = 8 小时。
    ///
    /// 配置表里**没有**这个数（<c>PetFoodInfo.txt</c> 只有价格与稀有度权重），
    /// 唯一取证是客户端自己的钳位：<c>SetFoodRsp</c> 里
    /// <c>self['foodBox'..i..'Time'] = math.min(v.time, time + 8*3600)</c>
    /// （<c>NCafePetLogic.lua:250</c>）—— 原服下发的时间必然 ≤ now+8h，否则这个 min 毫无意义。
    /// ★ 用**正好** 8h 还能规避一处不一致：<c>UpdateFoodBoxsRsp</c>（<c>:118..123</c>）**不钳位**，
    /// 若服务端发了 &gt; 8h，同一个食盆在两条回包里会显示两个不同的倒计时。
    /// </summary>
    public const int FoodAttractSeconds = 8 * 3600;

    /// <summary>「放生礼物待领取」的伪 type（<c>NCafePetLogic.lua:82/114/245</c> <c>v.type == 9999</c>）。</summary>
    public const int GiveUpGiftType = 9999;

    /// <summary>宠物礼物的 genre = 22（<c>UI_PetList.lua:630/645</c> <c>GetItemListByGenreDetail(list, 22, 1|2)</c>）。</summary>
    public const int GiftGenre = 22;

    /// <summary>genre 22 里 detail 1 = 玩具（加好感经验），<c>UI_PetList.lua:630</c> + <c>:527</c>（ExtParam1 显示在 AddLove）。</summary>
    public const int GiftDetailLove = 1;

    /// <summary>genre 22 里 detail 2 = 清洁用品（加精力），<c>UI_PetList.lua:645</c> + <c>:528</c>（AddEnergy）。</summary>
    public const int GiftDetailHot = 2;

    // ---- 上行 sCmd ----
    public const string CmdUpdateFoodBoxs = "UpdateFoodBoxs";
    public const string CmdGetCafeFoodNum = "GetCafeFoodNum";
    public const string CmdOnFoodBoxOpen = "OnFoodBoxOpen";
    public const string CmdSetFood = "SetFood";
    public const string CmdOnBoxFoodDone = "OnBoxFoodDone";
    public const string CmdOnGetGiveupItem = "OnGetGiveupItem";
    public const string CmdOnNewPet = "OnNewPet";
    public const string CmdOnGiveupPet = "OnGiveupPet";
    public const string CmdChangePetCafeState = "ChangePetCafeState";
    public const string CmdPetGift = "PetGift";
    public const string CmdPetRename = "PetRename";
    public const string CmdDelPet = "DelPet";
    public const string CmdBuyCafeFood = "BuyCafeFood";
    public const string CmdGetPetMissionAward = "GetPetMissionAward";

    // ---- 下行 sCmd（必须存在同名 <sCmd>Rsp，见类注释 §1）----
    public const string RspUpdateFoodBoxs = "UpdateFoodBoxs";
    public const string RspSetFood = "SetFood";
    public const string RspGetCafeFoodNum = "GetCafeFoodNum";
    public const string RspAdoptPet = "AdoptPet";
    public const string RspGiveUpPet = "GiveUpPet";
    public const string RspGiveUpPetOwned = "GiveUpPetOwned";
    public const string RspChangePetCafeState = "ChangePetCafeState";
    public const string RspPetGift = "PetGift";
    public const string RspPetRename = "PetRename";

    /// <summary>纯下行推送：把宠物属性同步给客户端缓存（<c>UpdatePetRsp</c>，<c>NCafePetLogic.lua:761</c>）。</summary>
    public const string RspUpdatePet = "UpdatePet";

    /// <summary>
    /// 「已取证、但本轮不实现」的 sCmd。收到只打一条**可读**的 WARN，不回包
    /// （回包会撞上类注释 §1 的 nil 调用，或者假装成功让界面显示错的数据）。
    ///
    /// <list type="bullet">
    ///   <item><c>OnBuyBoxFood</c> —— **死代码**：唯一调用点 <c>UI_PetFood.lua:21</c>
    ///     整行被注释掉了，改成弹「去商店 20010 买」。</item>
    ///   <item><c>GetPetMissionAward</c> —— 需要任务/成就域的 <c>BattlePass:GetWeekMissionConfig</c>，
    ///     跨域；而且它的 Rsp 本身有 bug（见 docs 小结）。</item>
    ///   <item><c>GetPawAward</c>（<c>NCafePetTour.lua:462</c>）—— ★ 属于**宠物图鉴/爪印**子系统
    ///     （消费界面 <c>UI_PetRikiAward</c>、状态存 1450 + awardId），**不是远征**。
    ///     而且它在全仓**没有任何 <c>GetPawAwardRsp</c> 函数** ⇒ 回包会撞 nil 调用，
    ///     只能不回包。留待图鉴子系统整体实现时一起做。</item>
    /// </list>
    ///
    /// ★ 宠物远征 4 条（<c>UnLockTourArea</c> / <c>SetTourPets</c> / <c>CheckTourIsSuccess</c> /
    /// <c>CompleteTour</c>）**已在第十八轮实现**，见 <c>CafePetFeature.Tour.cs</c>。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownUnimplemented =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OnBuyBoxFood"] = "死代码：唯一调用点 UI_PetFood.lua:21 已被注释，改走商店 20010",
            ["GetPawAward"] = "宠物图鉴爪印奖励（NCafePetTour.lua:462），全仓无 GetPawAwardRsp ⇒ 只能不回包",
        };

    private GameFeatureContext _ctx = null!;
    private CafeTable _table = CafeTable.Empty;
    private PetTourTable _tourTable = PetTourTable.Empty;
    private readonly Random _rng = new();

    /// <summary>GameModule 反射装配用的公开无参构造（<c>GameFeatureLoader.Discover</c> 要求它存在）。</summary>
    public CafePetFeature() { }

    /// <summary>
    /// 单测用：直接注入配置表。
    /// <see cref="SettleFoodPoints"/> / <see cref="AddLoveExp"/> / <see cref="RollAttractedPet"/>
    /// 只依赖配置表，不碰网络与存档，用这个构造就能单独验算法，
    /// 不必去凑一个 <see cref="GameFeatureContext"/>（它的构造函数要 14 个依赖，且是 internal）。
    /// </summary>
    internal CafePetFeature(CafeTable table) => _table = table;

    public string Name => "咖啡馆宠物/食盆/食物点数(sFunction NCafePetLogic)";

    /// <summary>本功能加载到的配置（测试与日志用）。</summary>
    internal CafeTable Table => _table;

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = CafeTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        _tourTable = PetTourTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapFunction(Function, OnCommand);
    }

    // =================================================================
    //  分派
    // =================================================================

    private Task OnCommand(ClientSession session, string function, JsonNode? param)
    {
        if (session.Tag is not Player player)
        {
            Log.Warn($"[session {session.Id}] {Function} 到达但会话未登录（Tag 非 Player），忽略");
            return Task.CompletedTask;
        }

        // ★ 必须先判类型再取键：JsonNode 的索引器对非对象节点是**抛异常**而不是返回 null
        // （JsonNode.AsObject → InvalidOperationException），所以 `param?["sCmd"]` 不能提前写。
        // 真机上 SendCmd 传的一定是 Lua table，但空表会被 Json.Encode 编成 `[]`（JsonArray），
        // 这条路径真实存在（LuaCall 112 的上行就是 `[]`）。
        if (param is not JsonObject obj)
        {
            Log.Warn($"[session {session.Id}] {Function} 上行不是 JSON 对象，忽略：{param?.ToJsonString() ?? "null"}");
            return Task.CompletedTask;
        }

        string sCmd = obj["sCmd"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? obj["sCmd"]!.GetValue<string>()
            : "";
        if (sCmd.Length == 0)
        {
            Log.Warn($"[session {session.Id}] {Function} 上行缺 sCmd（或不是字符串），忽略：{obj.ToJsonString()}");
            return Task.CompletedTask;
        }

        var state = CafePetState.Read(player);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        switch (sCmd)
        {
            case CmdGetCafeFoodNum: OnGetCafeFoodNum(session, player, state, now); break;
            case CmdUpdateFoodBoxs: OnUpdateFoodBoxs(session, player, state, now); break;
            case CmdOnFoodBoxOpen: OnFoodBoxOpen(session, player, state, obj); break;
            case CmdSetFood: OnSetFood(session, player, state, obj, now); break;
            case CmdOnBoxFoodDone: OnBoxFoodDone(session, player, state, obj, now); break;
            case CmdOnGiveupPet: OnGiveupPet(session, player, state, obj); break;
            case CmdOnGetGiveupItem: OnGetGiveupItem(session, player, state, obj); break;
            case CmdOnNewPet: OnNewPet(session, player, state, obj, now); break;
            case CmdChangePetCafeState: OnChangePetCafeState(session, player, state, obj, now); break;
            case CmdPetGift: OnPetGift(session, player, state, obj); break;
            case CmdPetRename: OnPetRename(session, player, state, obj); break;
            case CmdDelPet: OnDelPet(session, player, state, obj, now); break;
            case CmdBuyCafeFood: OnBuyCafeFood(session, player, state, obj, now); break;
            case CmdGetPetMissionAward: OnGetPetMissionAward(session, player, obj); break;

            // ---- 远征子系统（CafePetFeature.Tour.cs）----
            case CmdUnLockTourArea: OnUnLockTourArea(session, player, state, obj); break;
            case CmdSetTourPets: OnSetTourPets(session, player, state, obj, now); break;
            case CmdCheckTourIsSuccess: OnCheckTourIsSuccess(session, player, state, obj, now); break;
            case CmdCompleteTour: OnCompleteTour(session, player, state, obj); break;

            default:
                if (KnownUnimplemented.TryGetValue(sCmd, out var why))
                    Log.Warn($"[session {session.Id}] {Function} sCmd={sCmd} 已取证但未实现（{why}），不回包");
                else
                    Log.Warn($"[session {session.Id}] {Function} sCmd={sCmd} 未知，不回包：{obj.ToJsonString()}");
                break;
        }

        return Task.CompletedTask;
    }

    // =================================================================
    //  GetCafeFoodNum —— 真机日志里那条 unhandled
    // =================================================================

    /// <summary>
    /// 咖啡馆食物点数。上行 <c>{sCmd="GetCafeFoodNum"}</c>（<c>NCafePetLogic.lua:421..425</c>），
    /// 由 <c>NCafePetLogic.OnEnterCafe()</c>（<c>:1060..1063</c>）在**进店时自动发**，
    /// 而 <c>OnEnterCafe</c> 是 C# <c>Game.NCafePetController.OnEnter</c>（RVA <c>0x1A3435C</c>）调的 ——
    /// 所以这条**每次进咖啡馆必现**，正是 AGENTS.md 里记的那条 unhandled。
    ///
    /// <para><b>回包字段：只需要 <c>sCmd</c>。</b></para>
    /// <c>GetCafeFoodNumRsp</c>（<c>:432..496</c>）除了 <c>if not tbParam then return end</c>
    /// 之外**一个字段都不读**（原本要读的 <c>ChangePetList</c> 在 <c>:437..444</c> 被整块注释掉了），
    /// 它做的全是「拿任务值重算 + 重建定时器 + 刷 3 个界面」：
    /// <c>UpdateFoodTime()</c> → <c>GetFoodBuffTime()</c>/<c>GetCurBuffState()</c>
    /// 只读 <c>me:GetTaskValue(49, ...)</c>。
    /// ⇒ <b>真正的「数据」是靠 pf 1026 任务值同步过去的，回包只是一个「重算信号」</b>，
    /// 这就是本实现先推 1026 再回包的原因（顺序反了客户端会用旧任务值重算）。
    ///
    /// 顺带确认过 <c>GetCafeFoodNumRsp</c> 全链路的 nil 安全：
    /// <c>GetTaskValue</c> 在 C# 侧返回 int（缺省 0，不会是 nil），所以
    /// <c>if thisTime &gt; 0</c>（<c>:652</c>）、<c>math.fmod(allTime,3600)</c>（<c>:658</c>）都安全；
    /// <c>self.CafeFoodCfg[thisId]</c>（<c>:627</c>）里 <c>CafeFoodCfg</c> 由客户端自己
    /// 从 <c>BuffFood.txt</c> 加载，与服务端无关。
    /// </summary>
    private void OnGetCafeFoodNum(ClientSession session, Player player, CafePetState state, long now)
    {
        var changed = SettleFoodPoints(player, state, now);
        _ctx.Store.Save(player);

        PushFoodPointTasks(session, player, changed);
        Reply(session, RspGetCafeFoodNum, new JsonObject());

        Log.Info($"[session {session.Id}] {Function}/GetCafeFoodNum 食物点数结算：" +
                 $"剩余 {TotalFoodSeconds(player) / SecondsPerFoodPoint}/{MaxCafeFoodPoint} 点，" +
                 $"店内宠物 {state.PetsInCafeCount} 只，(49,{TaskLastCalTime})={player.GetTask(TaskGroup, TaskLastCalTime)}");
    }

    // =================================================================
    //  UpdateFoodBoxs —— 第二条「进店必发」
    // =================================================================

    /// <summary>
    /// 刷新三个食盆。上行 <c>{sCmd="UpdateFoodBoxs"}</c>（<c>NCafePetLogic.lua:52..56</c>），
    /// 两个自动触发点：
    /// <list type="number">
    ///   <item><c>NCafePetLogic.OnCafeItemLoaded()</c>（<c>:1049..1050</c>）—— C#
    ///     <c>Game.NCafeCharacterController.&lt;InitCharacters&gt;d__2.MoveNext</c>（RVA <c>0x1D60A60</c>）
    ///     在咖啡馆模型加载完时调，**进店必发**；</item>
    ///   <item><c>UpdateFoodBoxsTime</c> 的每秒定时器（<c>:189..213</c>）在某格倒计时 &lt; 1 秒时补发 ——
    ///     这就是「食物到点后自动结算成宠物」的驱动力，服务端因此可以**懒结算**（见
    ///     <see cref="ResolveExpiredBoxes"/>）。</item>
    /// </list>
    /// 回包 <c>{sCmd, nError, param:[3 格]}</c>，<c>param</c> 的稠密性要求见类注释 §4。
    /// </summary>
    private void OnUpdateFoodBoxs(ClientSession session, Player player, CafePetState state, long now)
    {
        int resolved = ResolveExpiredBoxes(state, now);
        if (resolved > 0)
        {
            // 只在真的结算出宠物时落盘：这条命令还会被客户端「每秒定时器」在倒计时归零附近
            // 反复触发（NCafePetLogic.lua:210..212），无条件写存档等于凭空的磁盘抖动。
            state.Write(player);
            _ctx.Store.Save(player);
        }

        Reply(session, RspUpdateFoodBoxs, new JsonObject { ["param"] = BuildBoxParam(state) });

        Log.Info($"[session {session.Id}] {Function}/UpdateFoodBoxs 下发 3 格食盆" +
                 (resolved > 0 ? $"（本次结算出 {resolved} 只待领养宠物）" : "") +
                 $"：{state.DescribeBoxes(now)}");
    }

    // =================================================================
    //  OnFoodBoxOpen —— 解锁食盆
    // =================================================================

    /// <summary>
    /// 解锁一个食盆。上行 <c>{sCmd="OnFoodBoxOpen", nBoxId=posId}</c>
    /// （<c>NCafePetLogic.lua:306</c>，在 <c>ClickFood</c> 的 <c>foodState == 0</c> 分支里）。
    ///
    /// 价格来自 <c>FoodBoxs.txt</c>，索引是**已解锁数量 + 1**（客户端
    /// <c>tbPetFoodPrice[UnClockNum + 1]</c>，<c>:294</c>）：现网 <c>[3,0] / [3,100] / [3,200]</c>，
    /// 货币 3 = 免费钻 ⇒ 第一个食盆免费。
    ///
    /// ★ 客户端这里有个**写死的 bug**：确认按钮前的余额检查是
    /// <c>MoneyLogic:CheckMoney(3, costNum)</c>（<c>:301</c>）—— 货币 id **硬编码 3**，
    /// 而价格里的 <c>costType</c> 是配置值。现网两者恰好都是 3 所以看不出来，
    /// 但服务端必须按 <c>costType</c> 扣，否则将来改表就会「检查一种货币、扣另一种」。
    ///
    /// 回包用 <b><c>UpdateFoodBoxs</c></b>：<c>OnFoodBoxOpenRsp</c> 不存在（类注释 §1），
    /// 而 <c>UpdateFoodBoxsRsp</c> 正好会 <c>ShowFoods()</c> + 重开 <c>UI_PetHUD</c>（<c>:138..143</c>），
    /// 是「解锁后立刻看到空食盆」最贴切的那条。
    /// </summary>
    private void OnFoodBoxOpen(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int boxId = ReadInt(req["nBoxId"], 0);
        var box = state.Box(boxId);
        if (box is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnFoodBoxOpen nBoxId={boxId} 越界（合法 1..{FoodBoxCount}），忽略");
            return;
        }
        if (box.Unlocked)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnFoodBoxOpen 食盆 {boxId} 已解锁，重发一遍当前状态");
            Reply(session, RspUpdateFoodBoxs, new JsonObject { ["param"] = BuildBoxParam(state) });
            return;
        }

        int unlocked = state.UnlockedCount;
        var price = _table.FoodBoxPriceFor(unlocked);
        if (price is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnFoodBoxOpen 已解锁 {unlocked} 个，" +
                     $"{CafeTable.FoodBoxFileName} 里没有第 {unlocked + 1} 档价格，拒绝");
            return;
        }
        if (price.CostNum > 0 && player.GetMoney(price.CostType) < price.CostNum)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnFoodBoxOpen 货币 {price.CostType}" +
                     $"({MoneyType.NameOf(price.CostType)}) 不足（有 {player.GetMoney(price.CostType)}，需 {price.CostNum}），拒绝");
            return;
        }

        if (price.CostNum > 0)
        {
            player.AddMoney(price.CostType, -price.CostNum);
            GameNotify.PushMoney(session, player, price.CostType);
        }

        box.Unlocked = true;
        box.Type = 0;
        box.Time = 0;
        box.Pet = null;
        state.Write(player);
        _ctx.Store.Save(player);

        Reply(session, RspUpdateFoodBoxs, new JsonObject { ["param"] = BuildBoxParam(state) });
        Log.Info($"[session {session.Id}] {Function}/OnFoodBoxOpen 食盆 {boxId} 解锁成功" +
                 $"（第 {unlocked + 1} 档，扣 {MoneyType.NameOf(price.CostType)}×{price.CostNum}）");
    }

    // =================================================================
    //  SetFood —— 投放罐头
    // =================================================================

    /// <summary>
    /// 投放罐头。上行 <c>{sCmd="SetFood", nBoxId=posId, nId=foodId}</c>
    /// （<c>NCafePetLogic.lua:217..223</c>，来自 <c>UI_PetFood.lua:29</c>）。
    /// <c>nId</c> 是 <c>PetFoodInfo.txt</c> 的 <c>Id</c>（不是道具四元组）——
    /// 客户端列表是「genre 21 模板 → <c>ExtParam1</c> → 本表」拼出来的（<c>:354..372</c>）。
    ///
    /// 服务端要做三件事：扣 1 个对应道具（<c>PetFoodInfo.GDPL</c>）、把食盆置成
    /// <c>type = nId</c> / <c>time = now + 8h</c>（见 <see cref="FoodAttractSeconds"/>）、回 <c>SetFood</c>。
    ///
    /// 失败时回 <c>{Error = 1}</c>：<c>SetFoodRsp</c> 第二句就是
    /// <c>if tbParam.Error then return end</c>（<c>:229</c>）—— 这是本族唯一的失败通道，
    /// 且是**真值判断**，所以成功回包绝不能带 <c>Error</c>（哪怕 0）。
    /// </summary>
    private void OnSetFood(ClientSession session, Player player, CafePetState state, JsonObject req, long now)
    {
        int boxId = ReadInt(req["nBoxId"], 0);
        int foodId = ReadInt(req["nId"], 0);
        var box = state.Box(boxId);

        if (box is null || !box.Unlocked || box.Type != 0 || box.Pet is not null)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetFood nBoxId={boxId} 状态不允许投食" +
                     $"（unlocked={box?.Unlocked} type={box?.Type} pet={(box?.Pet is not null)}），回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }

        if (!_table.PetFoods.TryGetValue(foodId, out var food) || food.Gdpl.Length < 4)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetFood nId={foodId} 不在 " +
                     $"{CafeTable.PetFoodFileName} 里（现网只有 {string.Join(',', _table.PetFoods.Keys)}），回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }

        var stack = player.Items.FirstOrDefault(i => i.Genre == (uint)food.Gdpl[0] && i.Detail == (uint)food.Gdpl[1]
                                                  && i.Particular == (uint)food.Gdpl[2] && i.Level == (uint)food.Gdpl[3]
                                                  && i.Count > 0);
        if (stack is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetFood 罐头 " +
                     $"{_ctx.ItemTemplates.NameOf((uint)food.Gdpl[0], (uint)food.Gdpl[1], (uint)food.Gdpl[2], (uint)food.Gdpl[3])} " +
                     $"数量为 0，回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }

        stack.Count -= 1;
        if (stack.Count == 0)
        {
            player.Items.Remove(stack);
            GameNotify.PushItemsRemoved(session, new[] { stack });
        }
        else
        {
            GameNotify.PushItem(session, stack);
        }

        box.Type = foodId;
        box.Time = now + FoodAttractSeconds;
        box.Pet = null;
        state.Write(player);
        _ctx.Store.Save(player);

        Reply(session, RspSetFood, new JsonObject { ["param"] = BuildBoxParam(state) });
        Log.Info($"[session {session.Id}] {Function}/SetFood 食盆 {boxId} 投放罐头 {foodId}，" +
                 $"{FoodAttractSeconds / 3600} 小时后（{box.Time}）吸引宠物");
    }

    // =================================================================
    //  OnBoxFoodDone —— 立刻完成倒计时
    // =================================================================

    /// <summary>
    /// 花钱立刻完成食盆倒计时。上行 <c>{sCmd="OnBoxFoodDone", nBoxId=boxId}</c>
    /// （<c>NCafePetLogic.lua:389..394</c>，来自 <c>UI_PetFinishFast.lua:17</c>）。
    ///
    /// ★ 价格公式必须**逐字照抄**客户端展示式（<c>UI_PetFinishFast.lua:61</c>）：
    /// <code>
    /// needTime = max(0, boxTime - now)
    /// hour = floor(needTime/3600);  min = floor((needTime - hour*3600)/60)
    /// cost = speedNumPer * (hour*6 + floor(min/10) + 1)
    /// </code>
    /// <c>speedType</c>/<c>speedNumPer</c> 来自 <c>PetFoodInfo.PricePer</c>（现网 <c>[3,1]</c>，
    /// 即免费钻 1 个/10 分钟）。服务端另算一套 = 界面显示 X 实扣 Y。
    ///
    /// 完成后直接结算出宠物（<see cref="RollAttractedPet"/>），回 <b><c>SetFood</c></b> ——
    /// <c>OnBoxFoodDoneRsp</c> 不存在，而 <c>SetFoodRsp</c> 会顺手 <c>UI.Close('UI_PetFinishFast')</c>
    /// （<c>:272..274</c>），正是这里需要的收尾。
    /// </summary>
    private void OnBoxFoodDone(ClientSession session, Player player, CafePetState state, JsonObject req, long now)
    {
        int boxId = ReadInt(req["nBoxId"], 0);
        var box = state.Box(boxId);
        if (box is null || !box.Unlocked || box.Type == 0 || box.Type == GiveUpGiftType)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnBoxFoodDone nBoxId={boxId} 不是「有食物倒计时」状态" +
                     $"（type={box?.Type}），回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }

        long remain = Math.Max(0, box.Time - now);
        if (!_table.PetFoods.TryGetValue(box.Type, out var food))
        {
            Log.Warn($"[session {session.Id}] {Function}/OnBoxFoodDone 食盆 {boxId} 上的罐头 {box.Type} 已不在配置表，回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }

        int cost = SpeedUpCost(remain, food.SpeedNumPer);
        if (cost > 0 && player.GetMoney(food.SpeedType) < cost)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnBoxFoodDone 加速需 " +
                     $"{MoneyType.NameOf(food.SpeedType)}×{cost}（剩余 {remain}s），余额 " +
                     $"{player.GetMoney(food.SpeedType)}，回 Error");
            Reply(session, RspSetFood, BuildErrorPayload());
            return;
        }
        if (cost > 0)
        {
            player.AddMoney(food.SpeedType, -cost);
            GameNotify.PushMoney(session, player, food.SpeedType);
        }

        box.Time = now;
        ResolveExpiredBoxes(state, now);
        state.Write(player);
        _ctx.Store.Save(player);

        Reply(session, RspSetFood, new JsonObject { ["param"] = BuildBoxParam(state) });
        Log.Info($"[session {session.Id}] {Function}/OnBoxFoodDone 食盆 {boxId} 加速完成" +
                 $"（扣 {MoneyType.NameOf(food.SpeedType)}×{cost}）→ " +
                 $"吸引到 templateid={box.Pet?.TemplateId}");
    }

    /// <summary>
    /// 加速价 = <c>speedNumPer * (hour*6 + floor(min/10) + 1)</c>。
    /// 与客户端展示式逐字一致（<c>UI_PetFinishFast.lua:61</c>）；剩余 0 秒时仍收 1 份
    /// （式子末尾那个 <c>+1</c> 是客户端写死的，不是舍入）。
    /// </summary>
    internal static int SpeedUpCost(long remainSeconds, int speedNumPer)
    {
        long need = Math.Max(0, remainSeconds);
        long hour = need / 3600;
        long min = (need - hour * 3600) / 60;
        return (int)(speedNumPer * (hour * 6 + min / 10 + 1));
    }

    // =================================================================
    //  OnGiveupPet / OnGetGiveupItem —— 放生「吸引来的」宠物
    // =================================================================

    /// <summary>
    /// 放生食盆里吸引来、还没领养的宠物。上行 <c>{sCmd="OnGiveupPet", boxId=boxId}</c>
    /// （<c>NCafePetLogic.lua:786..791</c>，来自 <c>UI_PetMeet.lua:32</c>）。
    ///
    /// ★ 注意上行键名是 <c>boxId</c>（<b>不带 n 前缀</b>），而 <c>SetFood</c>/<c>OnFoodBoxOpen</c>/
    /// <c>OnBoxFoodDone</c>/<c>OnNewPet</c> 用的是 <c>nBoxId</c> —— 同一个域里两种命名，
    /// 本实现两个键都读（<see cref="ReadBoxId"/>）。
    ///
    /// ★★ 回包**必须带 <c>boxId</c>**：<c>GiveUpPetRsp</c> 最后一句
    /// <c>PetController:GiveUpPet(-1 * tbParam.boxId)</c>（<c>:803</c>）是**无条件**算术，
    /// 缺字段 = <c>attempt to perform arithmetic on a nil value</c> 直接抛。
    /// 而且它在 <c>:802</c> 会转调 <c>SetFoodRsp(tbParam)</c>，所以 <c>param</c> 也要带。
    ///
    /// 放生后食盆变成「有放生礼物」（<c>type = 9999</c>），等玩家再点一次触发
    /// <see cref="OnGetGiveupItem"/> 领取 —— 状态机见类注释 §3。
    /// </summary>
    private void OnGiveupPet(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int boxId = ReadBoxId(req);
        var box = state.Box(boxId);
        if (box is null || box.Pet is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnGiveupPet boxId={boxId} 上没有待领养宠物，回 Error");
            Reply(session, RspGiveUpPet, BuildErrorPayload());
            return;
        }

        uint tmp = box.Pet.TemplateId;
        box.Pet = null;
        box.Type = GiveUpGiftType;
        box.Time = 0;
        state.Write(player);
        _ctx.Store.Save(player);

        Reply(session, RspGiveUpPet, new JsonObject
        {
            ["boxId"] = boxId,                       // ★ :803 无条件算术，必填
            ["param"] = BuildBoxParam(state),        // ★ :802 转调 SetFoodRsp，必填
        });
        Log.Info($"[session {session.Id}] {Function}/OnGiveupPet 放生食盆 {boxId} 的 templateid={tmp}，" +
                 $"食盆转为放生礼物（type={GiveUpGiftType}）");
    }

    /// <summary>
    /// 领取放生礼物。上行 <c>{sCmd="OnGetGiveupItem", boxId=boxId}</c>
    /// （<c>NCafePetLogic.lua:396..401</c>，来自 <c>ClickFood</c> 的 <c>foodState == 4</c> 分支 <c>:328</c>）。
    ///
    /// 返还物品照客户端 <c>GetPetGiveUpGift(level)</c>（<c>:998..1009</c>）查 <c>GiveBack.txt</c>；
    /// 这只宠物从未领养、没有好感等级，所以按 1 级取（现网 1 级档 = <c>[22-1-1-1-1]</c>，
    /// 也就是 1 个「宠物玩具Ⅰ」）。
    ///
    /// 回包用 <b><c>UpdateFoodBoxs</c></b>（<c>OnGetGiveupItemRsp</c> 不存在）。
    /// </summary>
    private void OnGetGiveupItem(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int boxId = ReadBoxId(req);
        var box = state.Box(boxId);
        if (box is null || box.Type != GiveUpGiftType)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnGetGiveupItem boxId={boxId} 上没有放生礼物" +
                     $"（type={box?.Type}），忽略");
            return;
        }

        var gift = _table.GiveBackItemFor(1);
        var granted = new RewardGrantResult();
        if (gift is not null)
            _ctx.Rewards.Grant(player, new[] { gift }, RewardSource.Extra, granted);

        box.Type = 0;
        box.Time = 0;
        box.Pet = null;
        state.Write(player);
        _ctx.Store.Save(player);

        GameNotify.PushRewards(session, player, granted);
        if (!granted.IsEmpty)
            GameNotify.ShowRewardBox(_ctx.LuaCalls, session, granted);
        Reply(session, RspUpdateFoodBoxs, new JsonObject { ["param"] = BuildBoxParam(state) });

        Log.Info($"[session {session.Id}] {Function}/OnGetGiveupItem 食盆 {boxId} 领取放生礼物：" +
                 $"{(granted.IsEmpty ? "无（GiveBack.txt 缺失）" : granted.Describe())}");
    }

    // =================================================================
    //  OnNewPet —— 领养
    // =================================================================

    /// <summary>
    /// 领养食盆里的宠物。上行 <c>{sCmd="OnNewPet", nBoxId=petBoxId, name=name}</c>
    /// （<c>NCafePetLogic.lua:707..716</c>，来自 <c>UI_PetMeet.lua:43/55</c> 与 <c>UI_PetName.lua:15</c>）。
    ///
    /// <para><b>回包字段（<c>AdoptPetRsp</c>，<c>:718..759</c>）</b></para>
    /// <list type="bullet">
    ///   <item><c>param</c> —— 第一句就 <c>self:SetFoodRsp(tbParam)</c>（<c>:719</c>）刷食盆，必填。</item>
    ///   <item><c>petInfo</c> —— 12 个字段**全部必填**：<c>:723..734</c> 把它们逐个写进 C#
    ///     <c>NCafePetController.NPetInfo</c>（<c>dump.cs:532995..533008</c>），
    ///     除 <c>customName</c> 是 <c>string</c> 外**全是 <c>uint</c> 公开字段**，
    ///     xLua 的字段 setter 吃到 nil 会直接报错。字段名与 proto
    ///     <c>gsproto.CafePet</c>（docs/proto-schema.json：uid/templateid/exp/hot/name/
    ///     birthday/meetday/hobby/iq/nature/gender/lv）一一对应，交叉验证通过。</item>
    ///   <item><c>boxId</c> —— <c>:741</c> <c>PetController:DestroyPet(-1 * tbParam.boxId)</c>
    ///     是**无条件**算术，缺了直接抛。★ 上行是 <c>nBoxId</c>、下行是 <c>boxId</c>，名字不一样。</item>
    /// </list>
    ///
    /// <para><b>两个连带动作</b></para>
    /// <list type="number">
    ///   <item><c>:751</c> <c>if self:GetTmpNowPawCount(newInfo.tmpId) == 1 then UI.Open('UI_PetNew', ...)</c>
    ///     —— 图鉴计数读的是任务值 <c>(49, 1000 + floor(tmpId/100)*20 + tmpId%10)</c> 的低 16 位
    ///     （<c>NCafePetTour.lua:452..458</c> + <c>LoadBits(v,0,15)</c>，<c>Lib.lua:519</c>）。
    ///     服务端必须**先推 pf 1026 再回包**，否则「第一次遇到该品种」的展示弹窗不会出现。</item>
    ///   <item><c>:744..749</c> 客户端自己会挑第一个空指派位再发一条
    ///     <c>ChangePetCafeState</c> —— 所以服务端**不要**抢先指派，等那条上行。</item>
    /// </list>
    /// </summary>
    private void OnNewPet(ClientSession session, Player player, CafePetState state, JsonObject req, long now)
    {
        int boxId = ReadBoxId(req);
        var box = state.Box(boxId);
        if (box is null || box.Pet is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/OnNewPet nBoxId={boxId} 上没有待领养宠物，回 Error");
            Reply(session, RspAdoptPet, BuildErrorPayload());
            return;
        }

        var pet = box.Pet;
        pet.Uid = (uint)state.NextUid;           // ★ 领养这一刻才定 uid（吸引时那个只是占位）
        pet.Name = SanitizeName(req["name"], pet.Uid);
        pet.MeetDay = (uint)now;
        pet.Lv = 1;
        pet.Exp = 0;
        pet.Hot = 0;
        state.NextUid++;
        state.Pets.Add(pet);

        box.Pet = null;
        box.Type = 0;
        box.Time = 0;

        // 图鉴计数 +1（低 16 位），并把变更先推给客户端
        int rikiTaskId = RikiTaskId((int)pet.TemplateId);
        int rikiValue = player.GetTask(TaskGroup, rikiTaskId);
        int owned = (rikiValue & 0xFFFF) + 1;
        int newRiki = (rikiValue & ~0xFFFF) | (owned & 0xFFFF);
        player.SetTask(TaskGroup, rikiTaskId, newRiki);

        state.Write(player);
        _ctx.Store.Save(player);

        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, rikiTaskId), newRiki),
        });

        Reply(session, RspAdoptPet, new JsonObject
        {
            ["boxId"] = boxId,
            ["param"] = BuildBoxParam(state),
            ["petInfo"] = pet.ToJson(),
        });

        Log.Info($"[session {session.Id}] {Function}/OnNewPet 领养成功 uid={pet.Uid} " +
                 $"templateid={pet.TemplateId} name=\"{pet.Name}\"，图鉴 (49,{rikiTaskId}) 拥有数 → {owned}，" +
                 $"nextpetid → {state.NextUid}");
    }

    // =================================================================
    //  ChangePetCafeState —— 指派到咖啡馆
    // =================================================================

    /// <summary>
    /// 把宠物指派到咖啡馆的某个位置（<c>uid = 0</c> 表示撤下）。
    /// 上行 <c>{sCmd="ChangePetCafeState", nIndex=posId, uid=petId}</c>
    /// （<c>NCafePetLogic.lua:819..825</c>，来自 <c>UI_PetMain.lua:258/261/304</c> 与
    /// <c>UI_PetAssign.lua:232/235/307</c>）。
    ///
    /// <para><b>回包字段（<c>ChangePetCafeStateRsp</c>，<c>:827..860</c>）</b></para>
    /// <list type="bullet">
    ///   <item><c>nIndex</c> + <c>uid</c> **都必填** —— <c>:828</c>
    ///     <c>if not tbParam or not tbParam.nIndex or not tbParam.uid then return end</c>；
    ///     Lua 里 0 为真，所以「撤下」用 <c>uid = 0</c> 能过闸门。</item>
    ///   <item><c>nCleanIndex</c> **条件必填** —— 同一只宠物本来在别的位置时必须带上，
    ///     <c>:843..849</c> 会 <c>PetsInCafe_Sort[nCleanIndex] = 0</c> 把旧位置清掉；
    ///     不带 = 客户端里同一只宠物同时占两个位置（服务端只有一份数据，于是两边不一致）。</item>
    /// </list>
    ///
    /// ★ 指派变更会改变「店内宠物数量」，而食物点数**只在店内有宠物时才消耗**
    /// （<c>GetFoodBuffTime</c> 的三个分支，<c>:659..665</c>）⇒ 必须**先按旧数量结算**、
    /// 再改指派，否则会多扣或少扣一段时长。
    /// </summary>
    private void OnChangePetCafeState(ClientSession session, Player player, CafePetState state,
                                      JsonObject req, long now)
    {
        int index = ReadInt(req["nIndex"], 0);
        int uid = ReadInt(req["uid"], 0);
        int maxArea = _table.MaxPetAreaCount;

        if (index < 1 || index > maxArea)
        {
            Log.Warn($"[session {session.Id}] {Function}/ChangePetCafeState nIndex={index} 越界" +
                     $"（合法 1..{maxArea}，来自 {CafeTable.AreaUnlockFileName} 行数），忽略");
            return;
        }
        if (uid != 0 && state.Pet(uid) is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/ChangePetCafeState uid={uid} 不是本玩家的宠物，忽略");
            return;
        }

        // ① 先用旧的店内宠物数量结算食物点数
        var changedTasks = SettleFoodPoints(player, state, now);

        // ② 换位：同一只宠物若已在别处，必须把旧位置一起清掉并告知客户端
        state.EnsureSlots(maxArea);
        int? cleanIndex = null;
        if (uid != 0)
        {
            int old = state.InCafe.FindIndex(v => v == uid);
            if (old >= 0 && old != index - 1)
                cleanIndex = old + 1;
        }
        if (cleanIndex is int ci)
            state.InCafe[ci - 1] = 0;
        state.InCafe[index - 1] = uid;

        state.Write(player);
        _ctx.Store.Save(player);

        PushFoodPointTasks(session, player, changedTasks);

        var reply = new JsonObject { ["nIndex"] = index, ["uid"] = uid };
        if (cleanIndex is int ci2)
            reply["nCleanIndex"] = ci2;
        Reply(session, RspChangePetCafeState, reply);

        Log.Info($"[session {session.Id}] {Function}/ChangePetCafeState 位置 {index} ← uid={uid}" +
                 (cleanIndex is null ? "" : $"（顺带清空旧位置 {cleanIndex}）") +
                 $"，店内宠物 {state.PetsInCafeCount} 只");
    }

    // =================================================================
    //  PetGift —— 送礼
    // =================================================================

    /// <summary>
    /// 给宠物送礼。上行 <c>{sCmd="PetGift", uid=petId, tbItem={G,D,P,L,N}}</c>
    /// （<c>NCafePetLogic.lua:908..917</c>，来自 <c>UI_PetList</c>/<c>UI_PetInfo</c>）。
    ///
    /// 礼物一律 genre <b>22</b>（<c>UI_PetList.lua:630/645</c>
    /// <c>GetItemListByGenreDetail(self.GiftList, 22, 1|2)</c>）：
    /// <list type="bullet">
    ///   <item><c>detail == 1</c> 玩具 → 加**好感经验**（<c>UI_PetList.lua:527</c> 把
    ///     <c>ExtParam1</c> 显示在 <c>AddLove/Num</c>）；现网 <c>ExtParam1</c> = 30/60/90。</item>
    ///   <item><c>detail == 2</c> 清洁 → 加**精力 hot**（<c>:528</c> <c>AddEnergy/Num</c>）；
    ///     现网 20/50/80，上限 100（<see cref="CafeTable.MaxPetHot"/>）。</item>
    /// </list>
    ///
    /// ★★ **必须先推 <c>UpdatePet</c> 再回 <c>PetGift</c>**：<c>PetGiftRsp</c>（<c>:920..929</c>）
    /// 只是转手调 <c>UI_PetList:OnPetGift(tbParam.tbItem)</c>，而那个函数
    /// （<c>UI_PetList.lua:706..746</c>）读的是**客户端本地缓存**
    /// <c>NCafePetLogic:GetPetInfoById(petId)</c> 的 <c>lv</c>/<c>loveExp</c> 来刷新进度条。
    /// 顺序反了 = 送了礼但界面显示旧数值。
    ///
    /// ★ 顺带一个 nil 陷阱：<c>OnPetGift</c> 里 <c>modelInfo.Rare</c>（<c>:729</c>）没有 nil 保护 ⇒
    /// 服务端下发的 <c>templateid</c> 必须在 <c>PetInfo.txt</c> 里（见 <see cref="PetTemplateRow"/>）。
    /// </summary>
    private void OnPetGift(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int uid = ReadInt(req["uid"], 0);
        var pet = state.Pet(uid);
        if (pet is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetGift uid={uid} 不是本玩家的宠物，忽略");
            return;
        }

        if (req["tbItem"] is not JsonArray tuple || tuple.Count < 5)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetGift tbItem 不是 5 元组：" +
                     $"{req["tbItem"]?.ToJsonString() ?? "null"}，忽略");
            return;
        }

        int g = ReadInt(tuple[0], 0), d = ReadInt(tuple[1], 0);
        int p = ReadInt(tuple[2], 0), l = ReadInt(tuple[3], 0);
        int n = Math.Max(1, ReadInt(tuple[4], 1));

        if (g != GiftGenre)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetGift 礼物 genre={g}，只接受 {GiftGenre}，忽略");
            return;
        }

        var stack = player.Items.FirstOrDefault(i => i.Genre == (uint)g && i.Detail == (uint)d
                                                  && i.Particular == (uint)p && i.Level == (uint)l);
        if (stack is null || stack.Count < (uint)n)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetGift 礼物 ({g},{d},{p},{l}) 数量不足" +
                     $"（有 {stack?.Count ?? 0}，需 {n}），忽略");
            return;
        }

        var tpl = _ctx.ItemTemplates.Get((uint)g, (uint)d, (uint)p, (uint)l);
        if (tpl is null || !int.TryParse(tpl.ExtParam1, out int per) || per <= 0)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetGift 礼物 ({g},{d},{p},{l}) 的 ExtParam1=" +
                     $"\"{tpl?.ExtParam1}\" 不是正整数，忽略");
            return;
        }

        int amount = per * n;
        string what;
        if (d == GiftDetailHot)
        {
            int before = (int)pet.Hot;
            pet.Hot = (uint)Math.Min(CafeTable.MaxPetHot, before + amount);
            what = $"精力 {before} → {pet.Hot}（上限 {CafeTable.MaxPetHot}）";
        }
        else
        {
            var (lv, exp) = AddLoveExp(pet, amount);
            what = $"好感 lv{pet.Lv}/{pet.Exp} → lv{lv}/{exp}";
            pet.Lv = (uint)lv;
            pet.Exp = (uint)exp;
        }

        stack.Count -= (uint)n;
        if (stack.Count == 0)
        {
            player.Items.Remove(stack);
            GameNotify.PushItemsRemoved(session, new[] { stack });
        }
        else
        {
            GameNotify.PushItem(session, stack);
        }

        state.Write(player);
        _ctx.Store.Save(player);

        PushUpdatePet(_ctx.LuaCalls, session, new[] { pet });   // ★ 必须先于回包
        Reply(session, RspPetGift, new JsonObject
        {
            ["uid"] = uid,
            ["tbItem"] = new JsonArray { g, d, p, l, n },
        });

        Log.Info($"[session {session.Id}] {Function}/PetGift uid={uid} 收到 " +
                 $"{_ctx.ItemTemplates.NameOf((uint)g, (uint)d, (uint)p, (uint)l)}×{n}（每个 {per}）：{what}");
    }

    /// <summary>
    /// 加好感经验并连升级。
    ///
    /// 口径来自客户端的**显示方式**：<c>UI_PetInfo.lua:261</c> 直接
    /// <c>self.ExpText.text = nowExp..'/'..needExp</c>，其中 <c>nowExp = info.loveExp</c>、
    /// <c>needExp = GetLoveLevelNeedExp(level) = PetLoveLevel[level]</c>
    /// ⇒ <b><c>exp</c> 是「当前等级内」的经验，不是累计经验</b>。
    /// 等级上限按品种稀有度 30/40/50（<c>UI_PetList.lua:728..733</c> 硬编码）。
    /// </summary>
    internal (int Lv, int Exp) AddLoveExp(CafePet pet, int add)
    {
        int rare = _table.PetTemplates.TryGetValue((int)pet.TemplateId, out var t) ? t.Rare : 1;
        int maxLv = CafeTable.MaxLoveLevelForRare(rare);

        int lv = Math.Max(1, (int)pet.Lv);
        int exp = (int)pet.Exp + Math.Max(0, add);
        while (lv < maxLv)
        {
            int need = _table.LoveLevelNeedExp(lv);
            if (exp < need)
                break;
            exp -= need;
            lv++;
        }
        if (lv >= maxLv)
            exp = Math.Min(exp, _table.LoveLevelNeedExp(maxLv));   // 满级后不再累加，界面显示 Max
        return (lv, exp);
    }

    // =================================================================
    //  PetRename / DelPet
    // =================================================================

    /// <summary>
    /// 宠物改名。上行 <c>{sCmd="PetRename", uid=petId, name=name}</c>
    /// （<c>NCafePetLogic.lua:932..941</c>，来自 <c>UI_PetChangeName.lua:15</c>）。
    ///
    /// <c>PetRenameRsp</c>（<c>:943..953</c>）只判 <c>if not tbParam then return end</c>，
    /// 随后 <c>UI.Close('UI_PetChangeName')</c> + 重开 <c>UI_PetList</c>（读**本地缓存**）
    /// ⇒ 与送礼同理，<c>UpdatePet</c> 必须先推，否则列表里还是旧名字。
    /// </summary>
    private void OnPetRename(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int uid = ReadInt(req["uid"], 0);
        var pet = state.Pet(uid);
        if (pet is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/PetRename uid={uid} 不是本玩家的宠物，忽略");
            return;
        }

        string old = pet.Name;
        pet.Name = SanitizeName(req["name"], pet.Uid);
        state.Write(player);
        _ctx.Store.Save(player);

        PushUpdatePet(_ctx.LuaCalls, session, new[] { pet });
        Reply(session, RspPetRename, new JsonObject { ["uid"] = uid, ["name"] = pet.Name });

        Log.Info($"[session {session.Id}] {Function}/PetRename uid={uid} \"{old}\" → \"{pet.Name}\"");
    }

    /// <summary>
    /// 放生**已领养**的宠物。上行 <c>{sCmd="DelPet", uid=petId}</c>
    /// （<c>NCafePetLogic.lua:955..960</c>，来自 <c>UI_PetList.lua:185</c> / <c>UI_PetInfo.lua:16</c>）。
    ///
    /// 回包 sCmd 是 <b><c>GiveUpPetOwned</c></b>（<c>GiveUpPetOwnedRsp</c>，<c>:962</c>）：
    /// <list type="bullet">
    ///   <item><c>uid</c> **必填** —— <c>:978..981</c> <c>PetController:GiveUpPetOwned/DestroyPet(tbParam.uid)</c>。</item>
    ///   <item><c>nIndex</c> **条件必填** —— 该宠物正被指派在咖啡馆时必须带，
    ///     <c>:966..968</c> 靠它把 <c>PetsInCafe_Sort[nIndex]</c> 清 0。</item>
    /// </list>
    /// 返还物品按它的好感等级查 <c>GiveBack.txt</c>（<c>GetPetGiveUpGift</c>，<c>:998</c>）。
    /// </summary>
    private void OnDelPet(ClientSession session, Player player, CafePetState state, JsonObject req, long now)
    {
        int uid = ReadInt(req["uid"], 0);
        var pet = state.Pet(uid);
        if (pet is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/DelPet uid={uid} 不是本玩家的宠物，忽略");
            return;
        }

        // 先按旧的店内宠物数量结算食物点数（放生可能让店内宠物变 0）
        var changedTasks = SettleFoodPoints(player, state, now);

        int? slot = null;
        int at = state.InCafe.FindIndex(v => v == uid);
        if (at >= 0)
        {
            slot = at + 1;
            state.InCafe[at] = 0;
        }
        state.Pets.Remove(pet);

        var gift = _table.GiveBackItemFor((int)pet.Lv);
        var granted = new RewardGrantResult();
        if (gift is not null)
            _ctx.Rewards.Grant(player, new[] { gift }, RewardSource.Extra, granted);

        state.Write(player);
        _ctx.Store.Save(player);

        GameNotify.PushRewards(session, player, granted);
        PushFoodPointTasks(session, player, changedTasks);

        var reply = new JsonObject { ["uid"] = uid };
        if (slot is int s)
            reply["nIndex"] = s;
        Reply(session, RspGiveUpPetOwned, reply);

        Log.Info($"[session {session.Id}] {Function}/DelPet 放生 uid={uid} templateid={pet.TemplateId} " +
                 $"lv={pet.Lv}" + (slot is null ? "" : $"（原指派位 {slot}）") +
                 $"，返还 {(granted.IsEmpty ? "无" : granted.Describe())}");
    }

    // =================================================================
    //  BuyCafeFood —— 买咖啡馆食物（加点数）
    // =================================================================

    /// <summary>
    /// 买咖啡馆食物、给「食物点数」加时长。
    /// 上行 <c>{sCmd="BuyCafeFood", nFoodId=foodId, nNum=1}</c>
    /// （<c>NCafePetLogic.lua:547..554</c>，来自 <c>UI_PetAddBuff.lua:40/43/52/55</c>）。
    ///
    /// ★ 回包**不能**用 <c>BuyCafeFood</c>：<c>BuyCafeFoodRsp</c>（<c>:556..561</c>）虽然存在，
    /// 但函数体是 <c>if not tbParam or tbParam.Num then return end</c> + 一句注释，
    /// **什么都不做**。真正会刷新界面的是 <c>GetCafeFoodNumRsp</c>（它在 <c>:483..495</c>
    /// 重开 <c>UI_PetAddBuff</c>、刷 <c>UI_PetMain</c>、刷 <c>UI_NCafe</c> 红点）
    /// ⇒ 本实现回 <b><c>GetCafeFoodNum</c></b>，与「点数变了」的语义也更贴。
    ///
    /// 数值：<c>BuffFood.txt</c> 的 <c>AddPoint</c> 点 × 12 秒/点
    /// （<see cref="SecondsPerFoodPoint"/>），总量封顶 10000 点 = 120000 秒。
    /// 存放槽位取「食物 id 本身」（id 1..4，远小于 20 格），这样同种食物重复购买是累加，
    /// 而客户端 <c>GetCurBuffState</c>（<c>:623..632</c>）按 <c>Rarity</c> 最大者报「当前 buff」
    /// 也就自然成立。
    /// </summary>
    /// <summary>
    /// GetPetMissionAward: the pet-mission screen (UI_PetMission) claims a battle-pass week mission. These are the same
    /// group-18 missions as the battle-pass screen (LuaCall 343), so the claim is shared via
    /// <see cref="SeasonEventsFeature.TryClaimPassMission"/> — the claimed bit prevents a second grant from either entry.
    /// The client's <c>GetPetMissionAwardRsp</c> shows the reward popup from its own local <c>GetWeekMissionConfig</c>, so
    /// the reply only echoes <c>nId</c>; the server pushes the granted items and task values silently and shows no extra
    /// box. An invalid claim (missing / already claimed / in progress) is not answered, as the rest of this domain does.
    /// </summary>
    private void OnGetPetMissionAward(ClientSession session, Player player, JsonObject obj)
    {
        int id = EventKit.Number(obj, "nId");
        var changes = new List<KeyValuePair<uint, int>>();
        var rewards = new RewardGrantResult();
        if (!SeasonEventsFeature.TryClaimPassMission(_ctx, player, id, changes, rewards))
        {
            Log.Warn($"[session {session.Id}] {Function}/GetPetMissionAward 任务 {id} 不可领取（缺失/已领/未完成），不回包");
            return;
        }
        _ctx.Store.Save(player);
        GameNotify.PushRewards(session, player, rewards);
        GameNotify.PushTaskValues(session, changes);
        Reply(session, CmdGetPetMissionAward, new JsonObject { ["nId"] = id });
        Log.Info($"[session {session.Id}] {Function}/GetPetMissionAward 任务 {id} 领取：发奖 {rewards.Rewards.Count} 项");
    }

    private void OnBuyCafeFood(ClientSession session, Player player, CafePetState state,
                               JsonObject req, long now)
    {
        int foodId = ReadInt(req["nFoodId"], 0);
        int num = Math.Max(1, ReadInt(req["nNum"], 1));

        if (!_table.BuffFoods.TryGetValue(foodId, out var food))
        {
            Log.Warn($"[session {session.Id}] {Function}/BuyCafeFood nFoodId={foodId} 不在 " +
                     $"{CafeTable.BuffFoodFileName}（现网 {string.Join(',', _table.BuffFoods.Keys)}），忽略");
            return;
        }
        if (foodId > TaskFoodSlotCount)
        {
            Log.Warn($"[session {session.Id}] {Function}/BuyCafeFood nFoodId={foodId} 超出 " +
                     $"{TaskFoodSlotCount} 个任务槽位，忽略");
            return;
        }

        int cost = food.PriceNum * num;
        if (cost > 0 && player.GetMoney(food.PriceType) < cost)
        {
            Log.Warn($"[session {session.Id}] {Function}/BuyCafeFood 需 " +
                     $"{MoneyType.NameOf(food.PriceType)}×{cost}，余额 {player.GetMoney(food.PriceType)}，忽略");
            return;
        }

        // ① 先结算旧时长（lastCalTime 会被推进到 now）
        var changed = new SortedSet<int>(SettleFoodPoints(player, state, now));

        // ② 加时长，总量封顶
        int slot = foodId;
        int add = food.AddPoint * num * SecondsPerFoodPoint;
        int total = TotalFoodSeconds(player);
        int room = MaxCafeFoodPoint * SecondsPerFoodPoint - total;
        int real = Math.Max(0, Math.Min(add, room));
        if (real <= 0)
        {
            Log.Warn($"[session {session.Id}] {Function}/BuyCafeFood 食物点数已满" +
                     $"（{total / SecondsPerFoodPoint}/{MaxCafeFoodPoint}），不扣钱不加点");
            PushFoodPointTasks(session, player, changed);
            Reply(session, RspGetCafeFoodNum, new JsonObject());
            return;
        }

        if (cost > 0)
        {
            player.AddMoney(food.PriceType, -cost);
            GameNotify.PushMoney(session, player, food.PriceType);
        }

        player.SetTask(TaskGroup, FoodSlotIdTask(slot), foodId);
        player.SetTask(TaskGroup, FoodSlotTimeTask(slot),
                       player.GetTask(TaskGroup, FoodSlotTimeTask(slot)) + real);
        changed.Add(FoodSlotIdTask(slot));
        changed.Add(FoodSlotTimeTask(slot));

        _ctx.Store.Save(player);
        PushFoodPointTasks(session, player, changed);
        Reply(session, RspGetCafeFoodNum, new JsonObject());

        Log.Info($"[session {session.Id}] {Function}/BuyCafeFood 食物 {foodId}×{num}" +
                 $"（扣 {MoneyType.NameOf(food.PriceType)}×{cost}）+{real / SecondsPerFoodPoint} 点 → " +
                 $"合计 {TotalFoodSeconds(player) / SecondsPerFoodPoint}/{MaxCafeFoodPoint} 点");
    }

    // =================================================================
    //  食物点数（任务值 group 49）
    // =================================================================

    /// <summary>第 <paramref name="slot"/>（1..20）格的「食物 id」任务 id。</summary>
    internal static int FoodSlotIdTask(int slot) => TaskFoodSlotBegin + (slot - 1) * 2;

    /// <summary>第 <paramref name="slot"/>（1..20）格的「剩余秒数」任务 id。</summary>
    internal static int FoodSlotTimeTask(int slot) => TaskFoodSlotBegin + (slot - 1) * 2 + 1;

    /// <summary>
    /// 宠物图鉴计数的任务 id。
    /// 移植 <c>GetTmpNowPawCount</c>（<c>NCafePetTour.lua:452..458</c>）：
    /// <c>RikiId = floor(tmpId/100)*20 + tmpId%10</c>，任务 id = <c>1000 + RikiId</c>。
    /// 例：101 → 1*20+1 = 21 → <c>(49, 1021)</c>。
    /// </summary>
    internal static int RikiTaskId(int templateId) => TaskRikiBegin + templateId / 100 * 20 + templateId % 10;

    /// <summary>全部 20 格剩余秒数之和（= 客户端 <c>GetFoodBuffTime</c> 里的 <c>nTime</c>，<c>:648..655</c>）。</summary>
    internal static int TotalFoodSeconds(Player player)
    {
        int sum = 0;
        for (int i = 1; i <= TaskFoodSlotCount; i++)
        {
            int t = player.GetTask(TaskGroup, FoodSlotTimeTask(i));
            if (t > 0)
                sum += t;
        }
        return sum;
    }

    /// <summary>
    /// 结算食物点数：**只在店内有宠物时**才按 <c>now - lastCalTime</c> 扣时长，
    /// 之后把 <c>(49,200)</c> 推进到 <paramref name="now"/>。
    ///
    /// 依据是客户端 <c>GetFoodBuffTime</c> 的三个分支（<c>:659..665</c>）：
    /// <list type="bullet">
    ///   <item>有食物 + <c>lastCalTime &gt; 0</c> + 店内有宠物 → <c>CafeFoodTime = nTime + lastCalTime</c>
    ///     （所以服务端每次结算完把 lastCalTime 设为 now，客户端算出的到期时刻正好是 <c>now + 剩余</c>）；</item>
    ///   <item>店内**没有**宠物 → <c>nTime + nowTime</c>（不消耗，服务端也就不能扣）；</item>
    ///   <item>没有食物 → <c>lastCalTime, 3600</c>（<c>CafeFoodTime = now</c> ⇒ 0 点，无定时器）。</item>
    /// </list>
    /// 扣的顺序按 <c>Rarity</c> 从高到低，和 <c>GetCurBuffState</c>（<c>:623..632</c>）
    /// 「取 Rarity 最大者当当前 buff」保持一致：界面上正在生效的那一格，就是正在被消耗的那一格。
    /// </summary>
    /// <returns>被改动的任务 id 集合（推 pf 1026 用）。</returns>
    internal IReadOnlyCollection<int> SettleFoodPoints(Player player, CafePetState state, long now)
    {
        var changed = new SortedSet<int> { TaskLastCalTime };
        int last = player.GetTask(TaskGroup, TaskLastCalTime);
        long elapsed = last > 0 ? now - last : 0;

        if (elapsed > 0 && state.PetsInCafeCount > 0)
        {
            // 按稀有度从高到低（同稀有度按 id 升序，保证可复现）
            var order = Enumerable.Range(1, TaskFoodSlotCount)
                .Select(slot => (slot, id: player.GetTask(TaskGroup, FoodSlotIdTask(slot))))
                .Where(x => player.GetTask(TaskGroup, FoodSlotTimeTask(x.slot)) > 0)
                .OrderByDescending(x => _table.BuffFoods.TryGetValue(x.id, out var f) ? f.Rarity : 0)
                .ThenBy(x => x.id)
                .ToList();

            foreach (var (slot, _) in order)
            {
                if (elapsed <= 0)
                    break;
                int have = player.GetTask(TaskGroup, FoodSlotTimeTask(slot));
                int take = (int)Math.Min(have, elapsed);
                player.SetTask(TaskGroup, FoodSlotTimeTask(slot), have - take);
                elapsed -= take;
                changed.Add(FoodSlotTimeTask(slot));
                if (have - take == 0)
                {
                    player.SetTask(TaskGroup, FoodSlotIdTask(slot), 0);
                    changed.Add(FoodSlotIdTask(slot));
                }
            }
        }

        player.SetTask(TaskGroup, TaskLastCalTime, (int)Math.Min(now, int.MaxValue));
        return changed;
    }

    /// <summary>把 group 49 的若干任务值推给客户端（pf 1026，逐条 merge 不清表）。</summary>
    private static void PushFoodPointTasks(ClientSession session, Player player, IReadOnlyCollection<int> taskIds)
    {
        if (taskIds.Count == 0)
            return;
        GameNotify.PushTaskValues(session, taskIds.Select(id =>
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, id), player.GetTask(TaskGroup, id))));
    }

    // =================================================================
    //  食盆结算
    // =================================================================

    /// <summary>
    /// 把所有「倒计时已到」的食盆结算成「有宠物待领养」（<c>type = 0</c> + <c>pet</c>）。
    ///
    /// 懒结算是安全的：客户端 <c>UpdateFoodBoxsTime</c>（<c>:189..213</c>）在倒计时归零那一秒
    /// 会主动补发一条 <c>UpdateFoodBoxs</c>，进店时 <c>OnCafeItemLoaded</c> 也会发一条，
    /// 所以服务端不需要任何定时器。
    /// </summary>
    /// <returns>本次结算出的宠物数量。</returns>
    internal int ResolveExpiredBoxes(CafePetState state, long now)
    {
        int n = 0;
        foreach (var box in state.Boxes)
        {
            if (!box.Unlocked || box.Pet is not null)
                continue;
            if (box.Type == 0 || box.Type == GiveUpGiftType)
                continue;
            if (box.Time > now)
                continue;

            var pet = RollAttractedPet(box.Type, state.NextUid, now);
            if (pet is null)
            {
                Log.Warn($"[cafepet] 食盆 {box.Uid} 的罐头 {box.Type} 结算失败" +
                         $"（{CafeTable.PetInfoFileName} 或 {CafeTable.PetFoodFileName} 缺失），保持倒计时状态");
                continue;
            }
            box.Pet = pet;
            box.Type = 0;
            box.Time = 0;
            n++;
        }
        return n;
    }

    /// <summary>
    /// 按罐头的 <c>GetPetRare</c> 权重抽稀有度、再在该稀有度的模板里等概率抽一只。
    ///
    /// 权重取证 <c>PetFoodInfo.txt</c> 的 <c>GetPetRare</c> 列（罐头 1 = <c>{{1,90},{2,10}}</c>、
    /// 罐头 2 = <c>{{2,90},{3,10}}</c>），而客户端把同一份权重按合计归一化后**当公示概率展示**
    /// （<c>UI_PetFood.lua:216..224</c>）⇒ 服务端用同一份权重，公示概率才是真的。
    ///
    /// <paramref name="provisionalUid"/> 只是给客户端 <c>ClickFood</c> 的 <c>newInfo.Id</c> 占位
    /// （<c>NCafePetLogic.lua:316</c>，C# 侧真正的 key 是 <c>-boxId</c>）；
    /// 正式 uid 在 <see cref="OnNewPet"/> 里重新盖章。
    /// </summary>
    internal CafePet? RollAttractedPet(int foodId, int provisionalUid, long now)
    {
        if (_table.PetTemplates.Count == 0)
            return null;

        IReadOnlyList<PetTemplateRow> candidates = Array.Empty<PetTemplateRow>();
        if (_table.PetFoods.TryGetValue(foodId, out var food) && food.RareWeights.Count > 0)
        {
            int sum = food.RareWeights.Sum(w => Math.Max(0, w.Weight));
            if (sum > 0)
            {
                int roll = _rng.Next(sum), acc = 0;
                foreach (var (rare, weight) in food.RareWeights)
                {
                    acc += Math.Max(0, weight);
                    if (roll < acc)
                    {
                        candidates = _table.TemplatesOfRare(rare);
                        break;
                    }
                }
            }
        }
        if (candidates.Count == 0)
            candidates = _table.PetTemplates.Values.OrderBy(t => t.Id).ToList();
        if (candidates.Count == 0)
            return null;

        var tpl = candidates[_rng.Next(candidates.Count)];
        return new CafePet
        {
            Uid = (uint)Math.Max(1, provisionalUid),
            TemplateId = (uint)tpl.Id,
            Exp = 0,
            Hot = 0,
            Name = "",
            BirthDay = (uint)RandomBirthday(now),
            MeetDay = (uint)now,
            Hobby = (uint)_rng.Next(1, CafeTable.NatureCount + 1),
            Iq = (uint)_rng.Next(1, 251),
            Nature = (uint)_rng.Next(1, CafeTable.NatureCount + 1),
            Gender = (uint)_rng.Next(1, 3),
            Lv = 1,
        };
    }

    /// <summary>
    /// 随机生日：本年内的某一天正午（UTC）。
    /// 客户端只取 month/day 展示（<c>os.date("*t", info.birthday)</c> →
    /// <c>UI_PetMeet.lua:107..109</c> / <c>UI_PetInfo.lua:277..279</c>），
    /// 用正午可避免时区把日期挪到前一天。
    /// </summary>
    private long RandomBirthday(long now)
    {
        var year = DateTimeOffset.FromUnixTimeSeconds(now).UtcDateTime.Year;
        var start = new DateTimeOffset(year, 1, 1, 12, 0, 0, TimeSpan.Zero);
        return start.AddDays(_rng.Next(0, 365)).ToUnixTimeSeconds();
    }

    // =================================================================
    //  回包构造
    // =================================================================

    /// <summary>
    /// 组 <c>UpdateFoodBoxsRsp</c>/<c>SetFoodRsp</c> 的 <c>param</c>：
    /// **长度恰好 3、按 uid 升序的稠密数组**（理由见类注释 §4）。
    ///
    /// 未解锁的那一格**不带 <c>type</c> 字段**（也就不带 <c>time</c>），这是唯一能表达
    /// 「state = 0 未解锁」的写法 —— 四个 <c>elseif</c> 都以 <c>if v.type and ...</c> 开头
    /// （<c>NCafePetLogic.lua:108..116</c>），<c>type</c> 为 nil 时 state 停在初值 0，
    /// 而 <c>10 * (v and v.type or 0)</c> 也正好是 0。
    /// 反过来说：给未解锁的格子填 <c>type = 0</c> 会让它变成「已解锁无食物」，
    /// 玩家不花钱就白拿一个食盆。
    /// </summary>
    internal static JsonArray BuildBoxParam(CafePetState state)
    {
        var arr = new JsonArray();
        for (int uid = 1; uid <= FoodBoxCount; uid++)
        {
            var box = state.Box(uid);
            var obj = new JsonObject { ["uid"] = uid };
            if (box is not null && box.Unlocked)
            {
                obj["type"] = box.Type;
                obj["time"] = box.Time;
                if (box.Pet is not null)
                    obj["pet"] = box.Pet.ToJson();
            }
            arr.Add(obj);
        }
        return arr;
    }

    /// <summary>
    /// 组 LuaCall 112 的 <c>boxstatelist</c>：**只发已解锁的格子**。
    ///
    /// 与 <see cref="BuildBoxParam"/> 不同，这条走 <c>InitFoodBoxs</c> 的
    /// <c>if v.uid == i</c> 匹配（<c>NCafePetLogic.lua:69..73</c>），可以稀疏；
    /// 而它对 1..3 每一格都会写 <c>foodBox{i}</c>/<c>foodBox{i}Time</c>（<c>:86..92</c>），
    /// 所以「不发」= 未解锁，且不会留下 nil。
    /// 新号一格都没解锁 ⇒ 空数组，与 <c>CafeFeature</c> 原先的行为完全一致。
    /// </summary>
    internal static JsonArray BuildBoxStateList(CafePetState state)
    {
        var arr = new JsonArray();
        foreach (var box in state.Boxes.Where(b => b.Unlocked).OrderBy(b => b.Uid))
        {
            var obj = new JsonObject
            {
                ["uid"] = box.Uid,
                ["type"] = box.Type,
                ["time"] = box.Time,
            };
            if (box.Pet is not null)
                obj["pet"] = box.Pet.ToJson();
            arr.Add(obj);
        }
        return arr;
    }

    /// <summary>
    /// 组 LuaCall 112 的 <c>petstatelist</c>：**按指派位下标的稠密数组**，空位填 <c>uid = 0</c>。
    ///
    /// 取证 <c>InitPetsInCafe</c>（<c>NCafePetLogic.lua:862..869</c>）：
    /// <c>for k,v in ipairs(tbPetIds) do self.PetsInCafe_Sort[k] = v.uid end</c>
    /// —— <b>k 是数组下标（=指派位），元素里的 <c>uid</c> 才是宠物</b>。
    /// 所以「位置 2 有宠物、位置 1 空」必须发 <c>[{uid:0},{uid:5}]</c>；
    /// 只发一个 <c>{uid:5}</c> 会让它出现在位置 1。
    /// 一只都没指派时发空数组（<c>PetsInCafe_Sort = {}</c>），
    /// 客户端 <c>AdoptPetRsp</c> 的自动指派（<c>:744..749</c>）靠 <c>not ...[i]</c> 找空位，空表刚好。
    /// </summary>
    internal static JsonArray BuildPetStateList(CafePetState state)
    {
        var arr = new JsonArray();
        if (state.InCafe.All(v => v == 0))
            return arr;
        foreach (int uid in state.InCafe)
            arr.Add(new JsonObject { ["uid"] = uid });
        return arr;
    }

    /// <summary>
    /// 组失败回包。用 <c>Error</c>（大写、真值判断）而不是 <c>nError</c> ——
    /// <c>SetFoodRsp</c> 的 <c>if tbParam.Error then return end</c>（<c>NCafePetLogic.lua:229</c>）
    /// 是本族**唯一**的失败闸门，且它是真值判断（<c>Error = 0</c> 也算失败），
    /// 所以成功回包一律不带这个键。
    /// 同时按 docs/05 §0.1 带上 <c>nError = 1</c>，让未来接这条协议的界面也能看出是失败。
    /// </summary>
    internal static JsonObject BuildErrorPayload() => new()
    {
        ["Error"] = 1,
        ["nError"] = 1,
    };

    /// <summary>
    /// 推送宠物属性到客户端缓存：<c>{sCmd="UpdatePet", nError=0, tbPetInfo=[...]}</c>。
    ///
    /// 这是一条**纯下行**通道：全仓没有任何地方发 <c>sCmd = "UpdatePet"</c> 的上行
    /// （<c>grep UpdatePet</c> 命中的 <c>me:SendUpdatePet(pet)</c>，<c>LuaCall.lua:2676</c> /
    /// <c>PetLogic.lua:284</c>，是**战斗宠物**那套 <c>gsproto.PetUpdateNtf</c>，与咖啡馆宠物无关）。
    /// 客户端 <c>UpdatePetRsp</c>（<c>NCafePetLogic.lua:761..783</c>）把每项写进
    /// C# <c>NPetInfo</c> 再 <c>OnUpdatePetInfo</c>。12 个字段全必填，理由同
    /// <see cref="OnNewPet"/>（<c>NPetInfo</c> 的 uint 公开字段吃不了 nil）。
    ///
    /// ★ 进店时必须推：<c>petstatelist</c> 只带 uid，宠物的名字/等级/立绘全靠这条；
    /// 而 <c>NCafePetLogic.OnCafeItemLoaded</c>（<c>:1049..1058</c>）会拿
    /// <c>PetsInCafe_Sort</c> 里的 uid 去 <c>PetController:InitPet(v)</c>，
    /// 缓存里没有对应 <c>NPetInfo</c> 就摆不出宠物模型。
    /// </summary>
    internal static void PushUpdatePet(LuaCallRouter luaCalls, ClientSession session, IReadOnlyList<CafePet> pets)
    {
        if (pets.Count == 0)
            return;
        var arr = new JsonArray();
        foreach (var p in pets)
            arr.Add(p.ToJson());
        Send(luaCalls, session, RspUpdatePet, new JsonObject { ["tbPetInfo"] = arr });
    }

    /// <summary>
    /// 发一条本族下行：<c>sFunction = "NCafePetLogic"</c>，
    /// <c>tbParam = {sCmd = &lt;下行 sCmd&gt;, nError = 0, ...}</c>。
    ///
    /// <c>sCmd</c> 必填 —— <c>S2C.NCafePetLogic</c> 的唯一闸门是
    /// <c>if tbParam.sCmd then</c>（<c>S2CCall.lua:533</c>）；缺了整条包被静默丢弃。
    /// </summary>
    internal static void Send(LuaCallRouter luaCalls, ClientSession session, string sCmd, JsonObject payload)
        => luaCalls.CallClientFunction(session, Function, ReplySCmd(sCmd, payload));

    private void Reply(ClientSession session, string sCmd, JsonObject payload)
        => Send(_ctx.LuaCalls, session, sCmd, payload);

    /// <summary>
    /// 把 <c>sCmd</c> 与 <c>nError = 0</c> 补到 payload 前面（字段顺序稳定，便于单测锁 wire）。
    /// 只允许类注释 §1 表格里列出的那 10 个下行 sCmd —— 传别的会抛，
    /// 因为客户端 <c>Dispatcher</c> 会 <c>self[sCmd.."Rsp"](self, ...)</c> 而不判 nil。
    /// </summary>
    internal static JsonObject ReplySCmd(string sCmd, JsonObject payload)
    {
        if (!AllowedReplyCmds.Contains(sCmd))
            throw new ArgumentException($"下行 sCmd=\"{sCmd}\" 没有对应的 {sCmd}Rsp 函数，" +
                                       "客户端 NCafePetLogic:Dispatcher 会 nil 调用", nameof(sCmd));

        var obj = new JsonObject
        {
            ["sCmd"] = sCmd,
        };
        if (payload["nError"] is null)
            obj["nError"] = 0;      // 字符串命令族的成功回包必须带（docs/05 §0.1）
        foreach (var kv in payload.ToList())
        {
            payload.Remove(kv.Key);          // JsonNode 只能有一个父节点，先摘下来
            obj[kv.Key] = kv.Value;
        }
        return obj;
    }

    /// <summary>
    /// 允许出现在下行 <c>sCmd</c> 里的值 —— 每一个都在 <c>NCafePetLogic.lua</c> 里
    /// 有真实的 <c>&lt;sCmd&gt;Rsp</c> 函数（行号见类注释 §1 与各 handler）。
    /// </summary>
    internal static readonly IReadOnlySet<string> AllowedReplyCmds = new HashSet<string>(StringComparer.Ordinal)
    {
        RspUpdateFoodBoxs,        // :100
        RspSetFood,               // :225
        RspGetCafeFoodNum,        // :432
        RspAdoptPet,              // :718
        RspGiveUpPet,             // :793
        RspGiveUpPetOwned,        // :962
        RspChangePetCafeState,    // :827
        RspPetGift,               // :920
        RspPetRename,             // :943
        RspUpdatePet,             // :761

        // ---- 远征子系统（NCafePetTour.lua，均有真实 <sCmd>Rsp）----
        RspUnLockTourArea,        // :63
        RspSetTourPets,           // :128
        RspCheckTourIsSuccess,    // :418
        RspCompleteTour,          // :436

        CmdGetPetMissionAward,    // NCafePetLogic.lua:1036 GetPetMissionAwardRsp (reply sCmd == uplink sCmd)
    };

    // =================================================================
    //  小工具
    // =================================================================

    /// <summary>
    /// 读食盆 id。同一个域里两种键名：<c>SetFood</c>/<c>OnFoodBoxOpen</c>/<c>OnBoxFoodDone</c>/
    /// <c>OnNewPet</c> 用 <c>nBoxId</c>（<c>NCafePetLogic.lua:220/306/392/713</c>），
    /// <c>OnGiveupPet</c>/<c>OnGetGiveupItem</c> 用 <c>boxId</c>（<c>:789/399</c>）。
    /// 两个都读，避免被命名不一致坑到。
    /// </summary>
    internal static int ReadBoxId(JsonObject req)
    {
        int v = ReadInt(req["nBoxId"], 0);
        return v != 0 ? v : ReadInt(req["boxId"], 0);
    }

    /// <summary>
    /// 宠物名字：必须是**非空字符串**（客户端 <c>self.Name.text = info.customName</c>，
    /// <c>UI_PetInfo.lua:270</c>；xLua 的 string 字段 setter 吃 nil 会报错）。
    /// 超长按 24 个字符截断（服务端自保，客户端输入框本身也有长度限制）。
    /// </summary>
    internal static string SanitizeName(JsonNode? node, uint uid)
    {
        string raw = node is null ? "" : (node.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? node.GetValue<string>()
            : node.ToJsonString());
        raw = raw.Trim();
        if (raw.Length == 0)
            return $"pet{uid}";
        return raw.Length <= 24 ? raw : raw[..24];
    }

    /// <summary>读整数；Lua/JSON 侧可能给 float 或字符串，兜一层。</summary>
    internal static int ReadInt(JsonNode? node, int fallback)
    {
        if (node is null)
            return fallback;
        try
        {
            return node.GetValue<int>();
        }
        catch
        {
            return double.TryParse(node.ToString(), out var d) ? (int)d : fallback;
        }
    }
}

/// <summary>
/// 一只宠物的服务端状态。字段名与 proto <c>gsproto.CafePet</c>
/// （docs/proto-schema.json，字段 1..12）以及客户端 <c>AdoptPetRsp</c>/<c>UpdatePetRsp</c>
/// 读的 key **完全一致**：uid / templateid / exp / hot / name / birthday / meetday /
/// hobby / iq / nature / gender / lv。
///
/// 落地对象是 C# <c>NCafePetController.NPetInfo</c>（<c>dump.cs:532995</c>）——
/// 除 <c>customName</c> 是 <c>string</c> 外**全是 <c>uint</c> 公开字段**，
/// 所以下发时 12 个字段一个都不能少（xLua 字段 setter 吃 nil 直接报错）。
/// proto 里还有 <c>ext1..ext5</c>，客户端从不读，按「没有证据不编」的规矩不下发。
/// </summary>
internal sealed class CafePet
{
    public uint Uid { get; set; }
    public uint TemplateId { get; set; }

    /// <summary>好感经验 —— **当前等级内**的经验，不是累计（见 <see cref="CafePetFeature.AddLoveExp"/>）。</summary>
    public uint Exp { get; set; }

    /// <summary>精力，上限 100（<see cref="CafeTable.MaxPetHot"/>）。</summary>
    public uint Hot { get; set; }

    public string Name { get; set; } = "";

    /// <summary>生日 unix 秒，客户端只取 month/day 展示。</summary>
    public uint BirthDay { get; set; }

    /// <summary>相遇日 unix 秒，客户端算「相识 N 天」。</summary>
    public uint MeetDay { get; set; }

    public uint Hobby { get; set; }
    public uint Iq { get; set; }
    public uint Nature { get; set; }

    /// <summary>1 = 雄（<c>Mpath</c>）/ 2 = 雌（<c>Fpath</c>），取证 <c>UI_PetMeet.lua:114</c>。</summary>
    public uint Gender { get; set; }

    /// <summary>好感等级，从 1 起。</summary>
    public uint Lv { get; set; } = 1;

    /// <summary>下发给客户端的 JSON（key 就是 proto <c>CafePet</c> 的字段名）。</summary>
    public JsonObject ToJson() => new()
    {
        ["uid"] = Uid,
        ["templateid"] = TemplateId,
        ["exp"] = Exp,
        ["hot"] = Hot,
        ["name"] = Name,
        ["birthday"] = BirthDay,
        ["meetday"] = MeetDay,
        ["hobby"] = Hobby,
        ["iq"] = Iq,
        ["nature"] = Nature,
        ["gender"] = Gender,
        ["lv"] = Lv,
    };

    /// <summary>从存档 JSON 读回（键名与 <see cref="ToJson"/> 一致，存档与协议同构）。</summary>
    public static CafePet FromJson(JsonObject obj) => new()
    {
        Uid = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["uid"], 0)),
        TemplateId = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["templateid"], 0)),
        Exp = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["exp"], 0)),
        Hot = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["hot"], 0)),
        Name = obj["name"]?.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? obj["name"]!.GetValue<string>() : "",
        BirthDay = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["birthday"], 0)),
        MeetDay = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["meetday"], 0)),
        Hobby = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["hobby"], 0)),
        Iq = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["iq"], 0)),
        Nature = (uint)Math.Max(0, CafePetFeature.ReadInt(obj["nature"], 0)),
        Gender = (uint)Math.Max(1, CafePetFeature.ReadInt(obj["gender"], 1)),
        Lv = (uint)Math.Max(1, CafePetFeature.ReadInt(obj["lv"], 1)),
    };
}

/// <summary>一个食盆的服务端状态（状态机见 <see cref="CafePetFeature"/> 类注释 §3）。</summary>
internal sealed class CafeFoodBox
{
    /// <summary>食盆位置 1..3（客户端叫 posId，元素里叫 uid）。</summary>
    public int Uid { get; init; }

    /// <summary>是否已花钱解锁。未解锁时下发的元素**不带 type 字段**。</summary>
    public bool Unlocked { get; set; }

    /// <summary>0 = 空 / 罐头 id = 倒计时中 / 9999 = 放生礼物待领。</summary>
    public int Type { get; set; }

    /// <summary>倒计时到期的 unix 秒（绝对值，客户端直接拿去减 <c>GetTime()</c>）。</summary>
    public long Time { get; set; }

    /// <summary>吸引到、还没领养的宠物（<c>type == 0</c> 且非 null ⇒ state 3）。</summary>
    public CafePet? Pet { get; set; }
}

/// <summary>
/// 咖啡馆宠物子域的存档，落在 <c>Player.Extra["cafepet"]</c>。
///
/// 为什么不复用 <c>Player.Extra["cafe"]</c>：<c>CafeFeature.WriteState</c> 每次进店都
/// **整体覆写**那个 JsonObject（只写 level/hot/comfort），共用一个键会把宠物数据抹掉。
/// </summary>
internal sealed class CafePetState
{
    /// <summary>三个食盆，恒定 3 个、按 uid 1..3 升序。</summary>
    public List<CafeFoodBox> Boxes { get; } = new();

    /// <summary>已领养的宠物。</summary>
    public List<CafePet> Pets { get; } = new();

    /// <summary>
    /// 下一只宠物的 uid，也就是 LuaCall 112 的 <c>nextpetid</c>。
    /// 客户端 <c>UpdatePetNum(maxNum)</c> → <c>petMaxNum = (maxNum or 2) - 1</c>
    /// （<c>NCafePetLogic.lua:872</c>），语义是「拥有过的最大宠物 uid」⇒ 服务端存「下一个」。
    /// </summary>
    public int NextUid { get; set; } = 1;

    /// <summary>指派位 → 宠物 uid（0 = 空）。下标 0 对应位置 1。</summary>
    public List<int> InCafe { get; } = new();

    /// <summary>店内宠物数量（= 客户端 <c>GetNowCafePetNum()</c>，<c>NCafePetLogic.lua:498..506</c>）。</summary>
    public int PetsInCafeCount => InCafe.Count(v => v != 0);

    /// <summary>已解锁的食盆数量（= 客户端 <c>ClickFood</c> 里的 <c>UnClockNum</c>，<c>:287..293</c>）。</summary>
    public int UnlockedCount => Boxes.Count(b => b.Unlocked);

    /// <summary>取某个食盆（1..3）；越界返回 null。</summary>
    public CafeFoodBox? Box(int uid) => Boxes.FirstOrDefault(b => b.Uid == uid);

    /// <summary>取某只已领养的宠物；<c>uid &lt;= 0</c> 或不存在返回 null。</summary>
    public CafePet? Pet(int uid) => uid <= 0 ? null : Pets.FirstOrDefault(p => p.Uid == (uint)uid);

    /// <summary>把指派位补齐到 <paramref name="count"/> 个（<c>AreaUnlock.txt</c> 的行数）。</summary>
    public void EnsureSlots(int count)
    {
        while (InCafe.Count < count)
            InCafe.Add(0);
    }

    /// <summary>日志用的一行摘要。</summary>
    public string DescribeBoxes(long now) => string.Join(" ", Boxes.Select(b =>
        !b.Unlocked ? $"[{b.Uid}]锁"
        : b.Pet is not null ? $"[{b.Uid}]宠物{b.Pet.TemplateId}"
        : b.Type == CafePetFeature.GiveUpGiftType ? $"[{b.Uid}]放生礼物"
        : b.Type == 0 ? $"[{b.Uid}]空"
        : $"[{b.Uid}]罐头{b.Type}剩{Math.Max(0, b.Time - now)}s"));

    /// <summary>从存档读；没有记录时给出「三个食盆全锁、无宠物」的新号状态。</summary>
    public static CafePetState Read(Player player)
    {
        var state = new CafePetState();
        var node = player.Extra.TryGetValue(CafePetFeature.ExtraKey, out var v) ? v as JsonObject : null;

        var boxes = node?["boxes"] as JsonArray;
        for (int uid = 1; uid <= CafePetFeature.FoodBoxCount; uid++)
        {
            var raw = boxes?.OfType<JsonObject>()
                            .FirstOrDefault(o => CafePetFeature.ReadInt(o["uid"], 0) == uid);
            var box = new CafeFoodBox { Uid = uid };
            if (raw is not null)
            {
                box.Unlocked = raw["unlocked"]?.GetValueKind() == System.Text.Json.JsonValueKind.True;
                box.Type = CafePetFeature.ReadInt(raw["type"], 0);
                box.Time = CafePetFeature.ReadInt(raw["time"], 0);
                if (raw["pet"] is JsonObject petObj)
                    box.Pet = CafePet.FromJson(petObj);
            }
            state.Boxes.Add(box);
        }

        if (node?["pets"] is JsonArray pets)
        {
            foreach (var p in pets.OfType<JsonObject>())
            {
                var pet = CafePet.FromJson(p);
                if (pet.Uid > 0)
                    state.Pets.Add(pet);
            }
        }

        state.NextUid = Math.Max(1, CafePetFeature.ReadInt(node?["nextuid"], 1));
        // 存档里 nextuid 落后于实际宠物时自愈（避免 uid 撞车）
        foreach (var pet in state.Pets)
        {
            if (pet.Uid >= state.NextUid)
                state.NextUid = (int)pet.Uid + 1;
        }

        if (node?["incafe"] is JsonArray incafe)
        {
            foreach (var slot in incafe)
                state.InCafe.Add(Math.Max(0, CafePetFeature.ReadInt(slot, 0)));
        }

        return state;
    }

    /// <summary>写回 <c>Player.Extra["cafepet"]</c>（<see cref="Player"/> 的 Extra 袋随存档 round-trip）。</summary>
    public void Write(Player player)
    {
        var boxes = new JsonArray();
        foreach (var b in Boxes.OrderBy(b => b.Uid))
        {
            var obj = new JsonObject
            {
                ["uid"] = b.Uid,
                ["unlocked"] = b.Unlocked,
                ["type"] = b.Type,
                ["time"] = b.Time,
            };
            if (b.Pet is not null)
                obj["pet"] = b.Pet.ToJson();
            boxes.Add(obj);
        }

        var pets = new JsonArray();
        foreach (var p in Pets.OrderBy(p => p.Uid))
            pets.Add(p.ToJson());

        var incafe = new JsonArray();
        foreach (int uid in InCafe)
            incafe.Add(uid);

        player.Extra[CafePetFeature.ExtraKey] = new JsonObject
        {
            ["boxes"] = boxes,
            ["pets"] = pets,
            ["nextuid"] = NextUid,
            ["incafe"] = incafe,
        };
    }
}
