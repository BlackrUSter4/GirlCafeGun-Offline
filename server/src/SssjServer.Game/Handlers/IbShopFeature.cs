using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 内购商店购买（<c>LuaCall.DoRecharge = 10000</c>）——
/// 一条命令同时支撑商店界面里的三个入口：
/// <b>特殊补给</b> / <b>地球币矿池</b> / <b>十面妆造（妆扮商店）</b>。
///
/// <para><b>1. 为什么三个入口只需要一条命令</b></para>
/// <list type="bullet">
///   <item>特殊补给 <c>UI_ShopEntry.lua:12</c> → <c>IBLogic:ClickGemShop(1)</c>；
///     地球币矿池 <c>:36</c> → <c>ClickGemShop(2)</c>。两者都落到
///     <c>UI.CloseAndOpen('UI_Charge', nType, nClickType)</c>（<c>IBLogic.lua:1028</c>）
///     ⇒ <c>nType</c> 只是 <c>UI_Charge</c> 的**页签**
///     （<c>UI_Charge.lua:8/9</c>：<c>UI_SHOP1=1</c> 礼包 / <c>UI_SHOP2=2</c> 充值道具）。</item>
///   <item>十面妆造 <c>:49</c> → <c>UI.CloseAndOpen('UI_ShopNew', nil, 101)</c>，
///     数据来自 <c>IBLogic:GetAllIBShop(101)</c>。</item>
///   <item>三者的**商品列表、排序、限购置灰、红点全在客户端本地算**
///     （<c>IBLogic:GetAllIBShop</c> 只读本地 <c>tbGoods</c> + <c>me:GetTaskValue</c>，不发包）
///     ⇒ 服务端缺的只有「购买」这一条。</item>
/// </list>
///
/// <para><b>2. 上行（真机取证）</b></para>
/// `work/server.log` 里两次不同会话都留下了这条：
/// <code>
/// [WRN] unhandled LuaCall cmd=10000 (DoRecharge) param={"tbParam":{"Type":1,"Id":1027},"sCmd":10000}
/// </code>
/// 构造点两处：
/// <code>
/// IBLogic.lua:995  tbParam = { Type = 1, Id = proId, bExChange = …, VoucherGuid = … }  -- 礼包(TYPE_IBSHOP)
/// IBLogic.lua:784  tbParam = { Type = 2, Id = proId }                                  -- 充值道具(TYPE_IBITEM)
/// </code>
///
/// <para><b>★★★ 3. 为什么私服能绕开支付 SDK（本域成立的关键）</b></para>
/// <c>IBLogic:ResponseTradeNo</c>（<c>IBLogic.lua:790</c>）的 <c>Type == 1</c> 分支：
/// <code>
/// if tbIBItem.Currency == -1 then
///     … Game.Login.XGCharge(…)          -- 真钱：走 bilibili/XGSDK，platform=selftest 下压根没初始化
/// else
///     UI.OpenAsChild("UI_RewardNormal", tbParam.tbItem)   -- ★ 直接弹奖励框 = 发货成功
///     ui:RefreshShop(tbParam.Id)
/// end
/// </code>
/// ⇒ <b><c>Currency == -1</c> 是「真钱」哨兵</b>；其余货币（钻石 -2 / 代币 &gt;0）
/// **完全不碰 SDK**，服务端回 <c>tbItem</c> 就发货成功。
/// 真机自报货币分布：<c>-1</c> 299 件（做不了）/ <c>62</c> 20 / <c>63</c> 19 / <c>-2</c> 3
/// ⇒ 可做 **42 件**，其中十面妆造在售 8 件全是 <c>62</c>（造型U型夹）。
/// ⇒ 本域对 <c>Currency == -1</c> 一律回 <c>20073</c>（明确拒绝），不假装成功。
///
/// <para><b>★★ 4. 第七种错误约定：大写 <c>Error</c> + 数值比较</b></para>
/// <c>IBLogic.lua:798</c> 第一句就是 <c>if tbParam.Error &gt; 0 then</c>
/// ⇒ <b><c>Error</c> 必须存在且是数字</b>（缺了 Lua 抛「attempt to compare nil with number」），
/// 成功必须显式给 <c>Error = 0</c>。
/// 这与本项目已知的六种都不同：不是 <c>nError</c>（大小写不同）、
/// 也不是咖啡馆宠物族的大写 <c>Error</c>（那族是**真值**判断、成功不带）。
/// ⇒ 至此本项目共 **七种**错误/路由约定。
///
/// <para><b>5. 回包字段</b></para>
/// <list type="number">
///   <item><c>Error</c> —— 见上，必填数值。</item>
///   <item><c>Type</c> —— 回显（<c>:813</c> <c>if tbParam.Type == 1 then</c> 分流）。</item>
///   <item><c>Id</c> —— 回显（<c>:814 GetIBShopInfo(tbParam.Id)</c>，
///     取不到就弹 <c>20074</c>；<c>:872 ui:RefreshShop(tbParam.Id)</c> 刷新那一格）。</item>
///   <item><c>tbItem</c> —— **发出去的物品列表**，形状是「四/五元组的数组」
///     （<c>:868 UI.OpenAsChild("UI_RewardNormal", tbParam.tbItem)</c>，
///     并在 <c>:882..889</c> 被 <c>for i,v in ipairs(tbParam.tbItem)</c> +
///     <c>v[1]/v[2]</c> 当数组消费）。
///     ★ 必须给（哪怕空数组）：<c>:882</c> 那个 <c>ipairs(tbParam.tbItem)</c> 对 nil 会抛。</item>
/// </list>
/// ★ <c>TradeNo</c>/<c>CallBackUrl</c>/<c>Unit</c> **只有 <c>Currency == -1</c> 的分支才读**
/// ⇒ 本域（只做非真钱）不需要造订单号。
///
/// <para><b>6. 校验与扣费（客户端不查货币，服务端必须查）</b></para>
/// 客户端 <c>DoBuyBox</c>（<c>IBLogic.lua:945..999</c>）只查了商品存在、等级、时间窗、限购，
/// **全程没有 <c>CheckMoney</c>**（grep <c>IBLogic.lua</c> 无命中）
/// ⇒ 货币是否够**只能由服务端把关**，否则玩家可以负数购买。
/// 各校验的失败码逐条照抄客户端：
/// <code>
/// 20074 商品不存在   (:950)      20079 等级不足 (:956)
/// 20078 不在上架期   (:965/:972)  20075 超出限购 (:981)
/// 20073 本服不支持（真钱商品）—— ★ 本域自定，客户端无此分支
/// </code>
///
/// <para><b>7. 购买记录（任务值，必须推 pf 1026）</b></para>
/// <c>IBLogic:GetIBTask</c>（<c>IBLogic.lua:356</c>）：
/// <c>TYPE_IBSHOP → (42, id + 10000)</c>、<c>TYPE_IBITEM → (42, id)</c>
/// （分段规则见 <c>:5..7</c> 注释）。客户端 <c>GetAllIBShop</c>（<c>:255</c>）
/// 就是读它来把已售罄的商品挪进「售罄」区 ⇒ 不推 pf 1026 界面不会置灰。
/// </summary>
public sealed class IbShopFeature : IGameFeature
{
    /// <summary><c>LuaCall.DoRecharge</c>（<c>LuaCall.lua:452</c>）。</summary>
    public const int CmdDoRecharge = 10000;

    /// <summary>礼包商店（<c>IBLogic.TYPE_IBSHOP</c>，<c>IBLogic.lua:32</c>）。</summary>
    public const int TypeIbShop = 1;

    /// <summary>充值道具商店（<c>IBLogic.TYPE_IBITEM</c>，<c>IBLogic.lua:33</c>）。</summary>
    public const int TypeIbItem = 2;

    /// <summary>购买记录任务值组（<c>IBLogic.GROUP</c>，<c>IBLogic.lua:3</c>）。</summary>
    public const int TaskGroup = 42;

    /// <summary>ibshop 记录的 id 偏移（<c>IBLogic.IBSHOP_TASKID</c>，<c>IBLogic.lua:7</c>）。</summary>
    public const int IbShopTaskOffset = 10000;

    /// <summary>成功（<c>Error</c> 是数值比较 ⇒ 必须显式给 0）。</summary>
    public const int ErrOk = 0;

    /// <summary>商品不存在（<c>IBLogic.lua:950/816</c>）。</summary>
    public const int ErrNoGoods = 20074;

    /// <summary>超出限购（<c>IBLogic.lua:981</c>）。</summary>
    public const int ErrLimitReached = 20075;

    /// <summary>不在上架期（<c>IBLogic.lua:965/972</c>）。</summary>
    public const int ErrOutOfTime = 20078;

    /// <summary>等级不足（<c>IBLogic.lua:956</c>）。</summary>
    public const int ErrLevel = 20079;

    /// <summary>
    /// ★ 本域自定：真钱商品本服不支持。
    /// 20073 在客户端没有专门分支 ⇒ 会走 <c>:809</c> 的通用路径弹
    /// <c>ui.Error.20073</c>（查不到文案就显示原始 key，属可接受降级），
    /// 关键是**明确拒绝而不是静默**。
    /// </summary>
    public const int ErrRealMoneyUnsupported = 20073;

    private GameFeatureContext _ctx = null!;
    private IbShopTable _table = IbShopTable.Empty;

    public string Name => "内购商店购买（LuaCall 10000：特殊补给 / 地球币矿池 / 十面妆造）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = IbShopTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(CmdDoRecharge, OnDoRecharge);
    }

    private Task OnDoRecharge(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ 真机上行可能是数组形态（本项目踩过 JsonNode["key"] 对 JsonArray 抛）
        var obj = param as JsonObject;
        int type = ReadInt(obj?["Type"], -1);
        int id = ReadInt(obj?["Id"], -1);

        // 充值道具（Type=2）是**真钱**通道：PurchaseIB.txt 的商品一律经 XGCharge
        // （IBLogic.lua:897..929 的 Type==2 分支无条件读 TradeNo 并调 XGCharge）
        // ⇒ 本服不支持，明确拒绝。
        if (type == TypeIbItem)
        {
            Log.Warn($"[session {session.Id}] DoRecharge Type=2(充值道具 id={id}) 属真钱通道，" +
                     $"本服不支持 → Error={ErrRealMoneyUnsupported}");
            Reply(session, type, id, ErrRealMoneyUnsupported);
            return Task.CompletedTask;
        }

        if (type != TypeIbShop)
        {
            Log.Warn($"[session {session.Id}] DoRecharge 未知 Type={type} id={id} " +
                     $"param={param?.ToJsonString()} → Error={ErrNoGoods}");
            Reply(session, type, id, ErrNoGoods);
            return Task.CompletedTask;
        }

        var goods = _table.Get(id);
        if (goods is null)
        {
            // ★ 本地表比真机少 55 行（真机 341 / 本地 286）⇒ 这条分支真机上会命中
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 不在 " +
                     $"{IbShopTable.FileName}（本地 {_table.Count} 件；★ 本地表可能比设备少行，" +
                     $"见 AGENTS.md §5）→ Error={ErrNoGoods}");
            Reply(session, type, id, ErrNoGoods);
            return Task.CompletedTask;
        }

        // ---- 真钱商品：明确拒绝 ----
        if (goods.IsRealMoney)
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 是真钱商品" +
                     $"（Currency=-1，需 XGSDK）→ Error={ErrRealMoneyUnsupported}");
            Reply(session, type, id, ErrRealMoneyUnsupported);
            return Task.CompletedTask;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // ---- 等级（IBLogic.lua:955）----
        if (goods.Level > 0 && player.Level < goods.Level)
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 等级不足" +
                     $"（需 {goods.Level}，当前 {player.Level}）→ Error={ErrLevel}");
            Reply(session, type, id, ErrLevel);
            return Task.CompletedTask;
        }

        // ---- 上架期（IBLogic:CheckTime，两端闭、0 = 不限）----
        if (!goods.IsOnSaleAt(now))
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 不在上架期" +
                     $"（{goods.StartTime}~{goods.EndTime}，now={now}）→ Error={ErrOutOfTime}");
            Reply(session, type, id, ErrOutOfTime);
            return Task.CompletedTask;
        }

        // ---- 限购（IBLogic.lua:978..983）----
        int taskId = IbShopTaskOffset + id;
        int bought = player.GetTask(TaskGroup, taskId);
        if (goods.LimitType > 0 && bought >= goods.LimitTimes)
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 超出限购" +
                     $"（已买 {bought}/{goods.LimitTimes}，LimitType={goods.LimitType}）" +
                     $" → Error={ErrLimitReached}");
            Reply(session, type, id, ErrLimitReached);
            return Task.CompletedTask;
        }

        // ---- 扣费（★ 客户端不查货币，只有服务端把关）----
        int price = goods.PriceAt(now);
        int moneyId = ResolveMoneyId(goods.Currency);
        int have = player.GetMoney(moneyId);
        if (price > 0 && have < price)
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 货币不足：" +
                     $"{MoneyType.NameOf(moneyId)}(id={moneyId}) 有 {have}，需 {price}" +
                     $" → Error={ErrNoGoods}");
            // 客户端没有「货币不足」的专用码（它本来指望 SDK 拦），用 20074 让它弹提示
            Reply(session, type, id, ErrNoGoods);
            return Task.CompletedTask;
        }

        if (price > 0)
            player.SetMoney(moneyId, have - price);

        // ---- 发货 ----
        var granted = new RewardGrantResult();
        if (goods.Gdpl is not null && goods.Gdpl.Length >= 4)
        {
            int count = goods.Gdpl.Length >= 5 && goods.Gdpl[4] > 0 ? goods.Gdpl[4] : 1;
            _ctx.Rewards.Grant(player,
                new[] { new[] { goods.Gdpl[0], goods.Gdpl[1], goods.Gdpl[2], goods.Gdpl[3], count } },
                RewardSource.Extra, granted);
        }
        else
        {
            Log.Warn($"[session {session.Id}] DoRecharge 礼包 id={id} 的 GDPL 列为空/非法，" +
                     "本次不发物品（仍按成功回包，避免界面卡住）");
        }

        // ---- 购买记录 +1 ----
        player.SetTask(TaskGroup, taskId, bought + 1);
        _ctx.Store.Save(player);

        // ---- 推送（先货币/背包，再任务值；回包最后）----
        if (price > 0)
            GameNotify.PushMoney(session, player, moneyId);
        GameNotify.PushRewards(session, player, granted);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(TaskGroup, taskId), bought + 1),
        });

        Reply(session, type, id, ErrOk, BuildItemList(granted));

        Log.Info($"[session {session.Id}] 内购购买成功 id={id}（label={goods.LabelIdx}" +
                 $"{(goods.LabelIdx == IbShopTable.LabelDress ? " 十面妆造" : "")}）" +
                 $" 花费 {MoneyType.NameOf(moneyId)}×{price}" +
                 $"（原价 {goods.Cost}{(price != goods.Cost ? "，折后" : "")}）" +
                 $" 得到 {granted.Describe()}；购买记录 ({TaskGroup},{taskId})={bought + 1}" +
                 $"/{(goods.LimitType > 0 ? goods.LimitTimes.ToString() : "∞")}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>Currency</c> 列 → 服务端货币 id。
    ///
    /// 表头原文「货币类型,-1货币-2钻石-3金币 &gt;0对应代币表」：
    /// <list type="bullet">
    ///   <item><c>-2</c> → 钻石。★ 用**免费钻石**（<see cref="MoneyType.FreeDiamond"/>）：
    ///     付费钻石在私服里无从获得，用它会让所有钻石商品永远买不起。</item>
    ///   <item><c>-3</c> → 金币。</item>
    ///   <item><c>&gt; 0</c> → 代币 id 就是它本身（与抽卡的 <c>CashType</c> 同构，
    ///     例 62 = 造型U型夹 <c>MoneyLogic.eMoneyType_Hairpin</c>、
    ///     63 = U型夹 <c>eMoneyType_FreeHairpin</c>，<c>MoneyLogic.lua:57/58</c>）。</item>
    /// </list>
    /// </summary>
    internal static int ResolveMoneyId(int currency) => currency switch
    {
        IbShopGoods.CurrencyDiamond => MoneyType.FreeDiamond,
        IbShopGoods.CurrencyGold => MoneyType.Gold,
        _ => currency,
    };

    /// <summary>
    /// 组 <c>tbItem</c>：客户端按**数组**消费（<c>v[1]</c>/<c>v[2]</c>，<c>IBLogic.lua:884</c>），
    /// 且 <c>ipairs</c> 对 nil 会抛 ⇒ 没有物品也要给空数组。
    /// </summary>
    internal static JsonArray BuildItemList(RewardGrantResult granted)
    {
        var arr = new JsonArray();
        foreach (var r in granted.Rewards)
        {
            arr.Add(new JsonArray
            {
                (int)r.Genre, (int)r.Detail, (int)r.Particular, (int)r.Level, (int)r.Count,
            });
        }
        return arr;
    }

    /// <summary>
    /// 回包。★ <c>Error</c> 是**大写 + 数值比较**（<c>IBLogic.lua:798</c>）——
    /// 成功必须显式给 0，且 <c>tbItem</c> 一律给（空数组也行）。
    /// </summary>
    private void Reply(ClientSession session, int type, int id, int error, JsonArray? items = null)
        => _ctx.LuaCalls.CallClient(session, CmdDoRecharge, new JsonObject
        {
            ["Error"] = error,
            ["Type"] = type,
            ["Id"] = id,
            ["tbItem"] = items ?? new JsonArray(),
        });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;
}
