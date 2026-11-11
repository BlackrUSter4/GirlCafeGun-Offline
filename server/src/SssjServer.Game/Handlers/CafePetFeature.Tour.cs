using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// <see cref="CafePetFeature"/> 的**宠物远征**子系统（<c>extracted/lua/NCafePetTour.lua</c>）。
/// 台账 `docs/05` §55，配置表见 <see cref="PetTourTable"/>。
///
/// <para><b>0. 玩法</b></para>
/// 咖啡馆 → 宠物 → 远征页：每个栏位挂一条远征任务，派 <b>3 只</b>宠物出发，
/// 等若干小时后回来，按「是否大成功」结算奖励。
/// 栏位 1/2 免费，3/4/5 要满足「完成 N 次某品阶远征 + 拥有 N 只宠物 + 花钱」才能解锁。
///
/// <para><b>1. ★★★ 入口链：只有一条是活的</b></para>
/// <c>UI_NCafe</c>（常驻咖啡馆）→ <c>BtnPet</c>（<b>没有</b> <c>SetActive(false)</c>，
/// 已按第十五轮的教训核过）→ <c>UI_PetMain</c> → <c>btnTour</c> → <c>UI_PetTour</c>。
/// ★ <c>UI_NCafe.lua:251..263</c> 里通往 <c>UI_PetAssign</c> 的那段被 <c>--[[ ]]</c>
/// **整段注释**，所以 <c>UI_PetAssign.lua</c> 里那批同名调用是**死代码**
/// ⇒ 取证一律以 <c>UI_PetTour.lua</c> 为准。
///
/// <para><b>2. ★★★ 只有 4 条上行，但玩法有 5 个动作 —— <c>SetTourPets</c> 身兼二职</b></para>
/// 「派宠物」与「出发」**是同一条命令**：<c>UI_PetTour.lua:431..435</c> 的
/// 「出发」按钮直接调 <c>SetTourPets(栏位, 编队)</c>，没有单独的 <c>StartTour</c>。
/// ⇒ 服务端收到它就要**同时**写编队、扣精力、置状态 2、记出发时刻。
/// 我一开始按「先派后发」找第 5 条命令，全仓 <c>grep sCmd=</c> 只有 4 条才确认。
///
/// <para><b>3. ★★★ 客户端会每秒轮询 <c>CheckTourIsSuccess</c> —— 不推进状态就死循环</b></para>
/// <c>UI_PetTour.lua:620..651</c>：倒计时到 0 时调一次，并且 <c>Game.Timer.Add(0,1,...)</c>
/// 每秒再判一次，<c>needTime &lt;= 0</c> 就再调。
/// ⇒ 服务端必须在这条里**真的把状态从 2 推到 3/4**，否则客户端每秒发一个包、永不停止。
/// ★ 这与第十七轮 353 「不回包 ⇒ 客户端反复重发」是同一类坑，但这次是
/// **回了包但没推进状态**也会重发 —— 判据在状态机而不在回包。
///
/// <para><b>4. 四条回包的共同硬约束：必须回显 <c>nIndex</c></b></para>
/// 四个 <c>Rsp</c> 的第一句全是
/// <c>if not tbParam or not tbParam.nIndex then return end</c>
/// （<c>:64</c> / <c>:129</c> / <c>:419</c> / <c>:433</c>）⇒ 漏了这个字段 = 界面不刷新。
/// ★ 与第十六轮 351 的 <c>hasSp</c> 同型，但**这次四条是一致的**，
/// 不像第十七轮那三条两两不同 —— 仍然是逐条核过才敢这么写。
///
/// <para><b>5. 状态与奖励都存任务值 group 49</b>（偏移见 <see cref="PetTourTable"/>）</para>
/// 客户端**完全不信服务端下发的数据**：<c>UI_PetTour</c> 的每个字段都是自己
/// <c>me:GetTaskValue(49, ...)</c> 现读的。⇒ 回包只是「请刷新」的信号，
/// **真正的数据同步靠 pf 1030 推任务值**，两者都不能少。
///
/// <para><b>6. ★★ 大成功概率必须与客户端同算法</b></para>
/// 客户端 <c>GetPetsTourSuccessPro</c> 只用来**显示**百分比，掷骰在服务端。
/// 两边算法不一致 ⇒ 玩家看到 100% 却失败。
/// 所以 <see cref="PetTourTable.SuccessPercent"/> 与 <c>:222..248</c> 逐行同构，并有单测锁住。
/// </summary>
public sealed partial class CafePetFeature
{
    // =================================================================
    //  sCmd 常量
    // =================================================================

    /// <summary>上行：解锁一个远征栏位（<c>NCafePetTour.lua:56..61</c>）。</summary>
    public const string CmdUnLockTourArea = "UnLockTourArea";

    /// <summary>上行：派宠物 + **出发**（<c>:115..126</c>，见类注释 §2）。</summary>
    public const string CmdSetTourPets = "SetTourPets";

    /// <summary>上行：倒计时到点后查询结算（<c>:410..415</c>，见类注释 §3）。</summary>
    public const string CmdCheckTourIsSuccess = "CheckTourIsSuccess";

    /// <summary>上行：领奖收工（<c>:429..434</c>）。</summary>
    public const string CmdCompleteTour = "CompleteTour";

    /// <summary>下行：<c>UnLockTourAreaRsp</c>（<c>:63</c>）。</summary>
    public const string RspUnLockTourArea = "UnLockTourArea";

    /// <summary>下行：<c>SetTourPetsRsp</c>（<c>:128</c>）。</summary>
    public const string RspSetTourPets = "SetTourPets";

    /// <summary>下行：<c>CheckTourIsSuccessRsp</c>（<c>:418</c>）。</summary>
    public const string RspCheckTourIsSuccess = "CheckTourIsSuccess";

    /// <summary>下行：<c>CompleteTourRsp</c>（<c>:436</c>）。</summary>
    public const string RspCompleteTour = "CompleteTour";

    /// <summary>本功能加载到的远征配置（测试与日志用）。</summary>
    internal PetTourTable TourTable => _tourTable;

    // =================================================================
    //  UnLockTourArea —— 解锁栏位
    // =================================================================

    /// <summary>
    /// 解锁一个远征栏位。上行 <c>{sCmd="UnLockTourArea", nIndex=栏位id}</c>
    /// （<c>NCafePetTour.lua:56..61</c>，来自 <c>UI_PetTour.lua:303</c>）。
    ///
    /// <para><b>三个解锁条件（<c>UI_PetTour.lua:246..300</c>，客户端也判一遍）</b></para>
    /// <list type="number">
    ///   <item><c>Unlock1 = [品阶, 次数]</c> —— 完成 N 次该品阶远征（读任务值 800+品阶）</item>
    ///   <item><c>Unlock2 = N</c> —— 拥有 N 只宠物</item>
    ///   <item><c>Unlock3 = [货币id, 数量]</c> —— **点解锁时扣除**</item>
    /// </list>
    /// ★ 客户端 <c>btn.interactable</c> 已经判过一遍，服务端**仍然要判**
    /// —— 客户端判据是给玩家看的，不是权威（本仓一贯做法）。
    ///
    /// <para><b>回包（<c>:63..73</c>）</b></para>
    /// 只读 <c>nIndex</c>，然后 <c>UpdateTourUI(nIndex)</c> + 弹「解锁成功」提示。
    /// ★ <b>没有错误通道</b> —— 回包必然弹「解锁成功」，所以**校验不过时不能回包**，
    /// 否则玩家看到「解锁成功」但栏位还是锁的。这与第十六轮 351「校验不过也回包」
    /// 相反，因为那条的失败态有 <c>hasSp=false</c> 可表达，这条没有。
    /// </summary>
    private void OnUnLockTourArea(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int index = ReadInt(req["nIndex"], 0);
        var area = _tourTable.Area(index);
        if (area is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 不在 TourArea.txt 里，不回包");
            return;
        }

        int cur = player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index);
        if (cur != PetTourTable.StateLocked)
        {
            Log.Warn($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 状态已是 {cur}（非 0 未解锁），不回包");
            return;
        }

        // ---- 条件 1：完成 N 次某品阶远征 ----
        if (area.Unlock1.Count >= 2)
        {
            int rare = area.Unlock1[0], need = area.Unlock1[1];
            int done = player.GetTask(PetTourTable.Group, PetTourTable.TaskCompleteNumOff + rare);
            if (done < need)
            {
                Log.Warn($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 条件1不满足：" +
                         $"品阶 {rare} 完成 {done}/{need} 次，不回包");
                return;
            }
        }

        // ---- 条件 2：拥有 N 只宠物 ----
        if (area.Unlock2 > 0 && state.Pets.Count < area.Unlock2)
        {
            Log.Warn($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 条件2不满足：" +
                     $"拥有宠物 {state.Pets.Count}/{area.Unlock2} 只，不回包");
            return;
        }

        // ---- 条件 3：花钱 ----
        int currencyId = 0, cost = 0;
        if (area.Unlock3.Count >= 2)
        {
            currencyId = area.Unlock3[0];
            cost = area.Unlock3[1];
            if (player.GetMoney(currencyId) < cost)
            {
                Log.Warn($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 条件3不满足：" +
                         $"需要 货币{currencyId}×{cost}、持有 {player.GetMoney(currencyId)}，不回包");
                return;
            }
        }

        // ---- 结算 ----
        if (cost > 0)
            player.AddMoney(currencyId, -cost);

        player.SetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index, PetTourTable.StateIdle);

        // 解锁后立刻给这个栏位挂一条远征任务，否则客户端 :308 会 Error('已解锁的栏位没有任务！')
        int tourId = RollTourId(area, player, index);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index, tourId);
        RollAwards(player, index, tourId);

        _ctx.Store.Save(player);

        if (cost > 0)
            GameNotify.PushMoney(session, player, new[] { currencyId });
        PushTourTasks(session, player, index);

        Log.Info($"[session {session.Id}] {Function}/UnLockTourArea 栏位 {index} 解锁成功" +
                 (cost > 0 ? $"（扣 货币{currencyId}×{cost}）" : "（免费）") +
                 $"，挂上远征 {tourId}，状态 → 1");

        Reply(session, RspUnLockTourArea, new JsonObject { ["nIndex"] = index });
    }

    // =================================================================
    //  SetTourPets —— 派宠物 + 出发
    // =================================================================

    /// <summary>
    /// 派宠物出征。上行 <c>{sCmd="SetTourPets", nIndex=栏位id, tbPets=[uid,uid,uid]}</c>
    /// （<c>NCafePetTour.lua:115..126</c>，来自 <c>UI_PetTour.lua:434</c> 的「出发」按钮）。
    ///
    /// ★ 见类注释 §2：这条命令**同时是「出发」**，没有单独的开始命令。
    ///
    /// <para><b>校验（与客户端 <c>IsPetsCanGoTour</c>（<c>:159..220</c>）同构）</b></para>
    /// <list type="bullet">
    ///   <item>必须正好 3 只、且互不相同、且都是自己的宠物（<c>:215</c> <c>petCount &lt; 3 ⇒ false</c>）；</item>
    ///   <item>每只精力 <c>hot</c> 必须 <b>&gt;</b> <c>EnergyCost</c>（<c>:280</c> 是**严格大于**，不是 ≥）；</item>
    ///   <item>远征的 <c>Condition</c> 逐条判：类型 1 只数 / 2 限定物种 / 3 限定性格。</item>
    /// </list>
    /// ★ 同一只宠物不能同时派两个栏位（客户端 <c>GetTbPetsInTour</c> 会把在征的排除掉）。
    ///
    /// <para><b>回包（<c>:128..157</c>）</b></para>
    /// <c>nIndex</c> 必填；可选 <c>statelist</c> = 「要从咖啡馆撤下的指派位」列表 ——
    /// <c>:134..141</c> 会把这些位置的宠物 <c>DestroyPet</c>。
    /// 出征的宠物如果正在店里站着，就必须带上它的指派位，否则客户端里它**既在店里又在远征**。
    /// </summary>
    private void OnSetTourPets(ClientSession session, Player player, CafePetState state,
                               JsonObject req, long now)
    {
        int index = ReadInt(req["nIndex"], 0);
        var area = _tourTable.Area(index);
        if (area is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 不存在，不回包");
            return;
        }

        int cur = player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index);
        if (cur != PetTourTable.StateIdle)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 状态 {cur} 不是 1（未出发），不回包");
            return;
        }

        int tourId = player.GetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index);
        var tour = _tourTable.Tour(tourId);
        if (tour is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 挂的远征 {tourId} 不在表里，不回包");
            return;
        }

        // ---- 收上行编队（客户端保证 3 个元素，缺位补 0）----
        var uids = (req["tbPets"] as JsonArray)?.Select(n => ReadInt(n, 0)).Where(v => v != 0).ToList()
                   ?? new List<int>();
        if (uids.Count != PetTourTable.PetsPerTour || uids.Distinct().Count() != uids.Count)
        {
            Log.Warn($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 编队非法：" +
                     $"[{string.Join(',', uids)}]（需要 {PetTourTable.PetsPerTour} 只互不相同），不回包");
            return;
        }

        var pets = new List<CafePet>();
        foreach (int uid in uids)
        {
            var pet = state.Pet(uid);
            if (pet is null)
            {
                Log.Warn($"[session {session.Id}] {Function}/SetTourPets 宠物 uid={uid} 不是本玩家的，不回包");
                return;
            }
            if (pet.Hot <= area.EnergyCost)
            {
                Log.Warn($"[session {session.Id}] {Function}/SetTourPets 宠物 uid={uid} 精力 {pet.Hot} " +
                         $"不足（需 > {area.EnergyCost}），不回包");
                return;
            }
            pets.Add(pet);
        }

        // ---- 同一只宠物不能同时在两个栏位 ----
        foreach (var other in _tourTable.Areas.Where(a => a.Index != index))
        {
            for (int i = 1; i <= PetTourTable.PetsPerTour; i++)
            {
                int busy = player.GetTask(PetTourTable.Group, FormationTask(other.Index, i));
                if (busy != 0 && uids.Contains(busy) &&
                    player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + other.Index)
                        == PetTourTable.StateRunning)
                {
                    Log.Warn($"[session {session.Id}] {Function}/SetTourPets 宠物 uid={busy} " +
                             $"已在栏位 {other.Index} 远征中，不回包");
                    return;
                }
            }
        }

        // ---- 出征条件（Condition）----
        if (!ConditionsSatisfied(tour, pets, out string why))
        {
            Log.Warn($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 远征 {tourId} 条件不满足：{why}，不回包");
            return;
        }

        // ---- 结算：写编队、扣精力、置状态 2、记出发时刻 ----
        for (int i = 1; i <= PetTourTable.PetsPerTour; i++)
            player.SetTask(PetTourTable.Group, FormationTask(index, i), uids[i - 1]);

        foreach (var pet in pets)
            pet.Hot = (uint)Math.Max(0, (int)pet.Hot - area.EnergyCost);

        player.SetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index, PetTourTable.StateRunning);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + index, (int)now);

        // 出征的宠物要从咖啡馆指派位上撤下来（否则客户端里它同时在两处）
        var cleared = new List<int>();
        for (int slot = 0; slot < state.InCafe.Count; slot++)
        {
            if (state.InCafe[slot] != 0 && uids.Contains(state.InCafe[slot]))
            {
                state.InCafe[slot] = 0;
                cleared.Add(slot + 1);   // 客户端的指派位是 1-based
            }
        }

        state.Write(player);
        _ctx.Store.Save(player);
        PushTourTasks(session, player, index);

        double pct = PetTourTable.SuccessPercent(tour, pets.Sum(p => (int)p.Lv));
        Log.Info($"[session {session.Id}] {Function}/SetTourPets 栏位 {index} 出发：" +
                 $"远征 {tourId}（品阶 {tour.Rare}）、宠物 [{string.Join(',', uids)}]、" +
                 $"每只扣精力 {area.EnergyCost}、时长 {area.Hour}h、大成功率 {pct:F2}%" +
                 (cleared.Count > 0 ? $"；撤下咖啡馆指派位 [{string.Join(',', cleared)}]" : ""));

        var payload = new JsonObject { ["nIndex"] = index };
        if (cleared.Count > 0)
        {
            var arr = new JsonArray();
            foreach (int s in cleared)
                arr.Add(s);
            payload["statelist"] = arr;
        }
        Reply(session, RspSetTourPets, payload);
    }

    // =================================================================
    //  CheckTourIsSuccess —— 到点结算
    // =================================================================

    /// <summary>
    /// 倒计时到点后由客户端发起结算。上行 <c>{sCmd="CheckTourIsSuccess", nIndex=栏位id}</c>
    /// （<c>NCafePetTour.lua:410..415</c>，来自 <c>UI_PetTour.lua:630/645</c>）。
    ///
    /// <para><b>★★★ 必须真的推进状态，否则客户端每秒重发</b>（类注释 §3）</para>
    /// 时间没到就**不推进也不回包** —— 回了包客户端会 <c>UpdateTourUI</c> 重新读状态，
    /// 读到还是 2 就继续倒计时，行为正确；但不回包能少一次无谓的往返。
    /// ★ 时间判定以**服务端**的 <c>now - 出发时刻 &gt;= Hour*3600</c> 为准，
    /// 不信客户端的倒计时（客户端时钟可改）。
    ///
    /// <para><b>大成功掷骰</b></para>
    /// 概率 = <see cref="PetTourTable.SuccessPercent"/>（与客户端显示同算法，类注释 §6）。
    /// ★ 用**确定性种子**（栏位 + 出发时刻 + 远征 id）而不是 <c>Random.Shared</c>：
    /// 这样同一次远征无论被查询几次结果都一样 —— 客户端每秒轮询，
    /// 用真随机会导致「同一次远征这秒大成功、下秒普通成功」。
    /// </summary>
    private void OnCheckTourIsSuccess(ClientSession session, Player player, CafePetState state,
                                      JsonObject req, long now)
    {
        int index = ReadInt(req["nIndex"], 0);
        var area = _tourTable.Area(index);
        if (area is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/CheckTourIsSuccess 栏位 {index} 不存在，不回包");
            return;
        }

        int cur = player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index);
        if (cur == PetTourTable.StateDone || cur == PetTourTable.StateGreatSuccess)
        {
            // 已经结算过（客户端可能重复问），回包让它刷新即可
            Reply(session, RspCheckTourIsSuccess, new JsonObject { ["nIndex"] = index });
            return;
        }
        if (cur != PetTourTable.StateRunning)
        {
            Log.Warn($"[session {session.Id}] {Function}/CheckTourIsSuccess 栏位 {index} 状态 {cur} 不是 2（进行中），不回包");
            return;
        }

        int begin = player.GetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + index);
        long elapsed = now - begin;
        long need = (long)area.Hour * 3600;
        if (begin <= 0 || elapsed < need)
        {
            Log.Warn($"[session {session.Id}] {Function}/CheckTourIsSuccess 栏位 {index} 还没到点：" +
                     $"已过 {elapsed}s / 需要 {need}s，不推进状态、不回包");
            return;
        }

        int tourId = player.GetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index);
        var tour = _tourTable.Tour(tourId);
        if (tour is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/CheckTourIsSuccess 栏位 {index} 远征 {tourId} 不在表里，不回包");
            return;
        }

        // 总等级 = 三只出征宠物的 lv 之和（与客户端 GetPetsTourSuccessPro 一致）
        int totalLv = 0;
        for (int i = 1; i <= PetTourTable.PetsPerTour; i++)
        {
            var pet = state.Pet(player.GetTask(PetTourTable.Group, FormationTask(index, i)));
            if (pet is not null)
                totalLv += (int)pet.Lv;
        }

        double pct = PetTourTable.SuccessPercent(tour, totalLv);
        // 确定性掷骰：同一次远征（栏位 + 出发时刻 + 远征 id）结果恒定
        uint seed = (uint)index * 2654435761u + (uint)begin * 40503u + (uint)tourId;
        bool great = seed % 10000 < (uint)Math.Round(pct * 100);

        int newState = great ? PetTourTable.StateGreatSuccess : PetTourTable.StateDone;
        player.SetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index, newState);
        _ctx.Store.Save(player);
        PushTourTasks(session, player, index);

        Log.Info($"[session {session.Id}] {Function}/CheckTourIsSuccess 栏位 {index} 远征 {tourId} 结算：" +
                 $"总等级 {totalLv} ⇒ 大成功率 {pct:F2}%，掷骰{(great ? "**大成功**" : "普通完成")}，状态 → {newState}");

        Reply(session, RspCheckTourIsSuccess, new JsonObject { ["nIndex"] = index });
    }

    // =================================================================
    //  CompleteTour —— 领奖收工
    // =================================================================

    /// <summary>
    /// 领奖收工。上行 <c>{sCmd="CompleteTour", nIndex=栏位id}</c>
    /// （<c>NCafePetTour.lua:429..434</c>，来自 <c>UI_PetFinishTour.lua:19</c> 的「确定」按钮）。
    ///
    /// <para><b>发什么奖</b></para>
    /// 远征本身的 <c>AwardNormal</c>（普通）或 <c>AwardSpecial</c>（大成功），
    /// 外加 850 段里那四个按栏位抽好的 <c>TourReward</c> 条目。
    /// ★ 客户端**不下发奖励列表也能正常工作**（<c>:436..449</c> 只 <c>UpdateAllTourUI</c>），
    /// 走 <see cref="GameNotify.ShowRewardBox"/> 的通用获得框展示。
    ///
    /// <para><b>收工后把栏位重置成「可再出发」</b></para>
    /// 状态回 1、清编队与出发时刻、重新抽一条远征任务与四个奖励，
    /// 并把该品阶的完成次数 +1（栏位解锁条件 1 读它）。
    /// ★ 客户端 <c>:441</c> 调的是 <c>UpdateAllTourUI()</c>（**全部**栏位），
    /// 所以这里重抽任务不会有「界面没刷到」的问题。
    /// </summary>
    private void OnCompleteTour(ClientSession session, Player player, CafePetState state, JsonObject req)
    {
        int index = ReadInt(req["nIndex"], 0);
        var area = _tourTable.Area(index);
        if (area is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/CompleteTour 栏位 {index} 不存在，不回包");
            return;
        }

        int cur = player.GetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index);
        if (cur != PetTourTable.StateDone && cur != PetTourTable.StateGreatSuccess)
        {
            Log.Warn($"[session {session.Id}] {Function}/CompleteTour 栏位 {index} 状态 {cur} 不是 3/4（已完成），不回包");
            return;
        }

        int tourId = player.GetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index);
        var tour = _tourTable.Tour(tourId);
        if (tour is null)
        {
            Log.Warn($"[session {session.Id}] {Function}/CompleteTour 栏位 {index} 远征 {tourId} 不在表里，不回包");
            return;
        }

        bool great = cur == PetTourTable.StateGreatSuccess;

        // ---- 发奖：远征本体 + 850 段里抽好的四条 ----
        var awards = new List<int[]>();
        var main = great ? tour.AwardSpecial : tour.AwardNormal;
        if (main.Count >= 5)
            awards.Add(main.ToArray());

        foreach (int awardId in ReadAwardIds(player, index))
        {
            var reward = _tourTable.Reward(awardId);
            if (reward is not null && reward.Award.Count >= 5)
                awards.Add(reward.Award.ToArray());
        }

        var granted = _ctx.Rewards.Grant(player, awards, RewardSource.Extra);

        // ---- 该品阶完成次数 +1（栏位解锁条件 1 读它）----
        int doneTask = PetTourTable.TaskCompleteNumOff + area.Rare;
        int done = player.GetTask(PetTourTable.Group, doneTask) + 1;
        player.SetTask(PetTourTable.Group, doneTask, done);

        // ---- 栏位重置为「可再出发」，并重抽任务与奖励 ----
        for (int i = 1; i <= PetTourTable.PetsPerTour; i++)
            player.SetTask(PetTourTable.Group, FormationTask(index, i), 0);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskTimeOff + index, 0);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskStateOff + index, PetTourTable.StateIdle);

        int nextTour = RollTourId(area, player, index);
        player.SetTask(PetTourTable.Group, PetTourTable.TaskTourIdOff + index, nextTour);
        RollAwards(player, index, nextTour);

        state.Write(player);
        _ctx.Store.Save(player);

        GameNotify.PushRewards(session, player, granted);
        PushTourTasks(session, player, index);
        GameNotify.PushTaskValues(session, new[]
        {
            new KeyValuePair<uint, int>(Player.ComposeTaskId(PetTourTable.Group, doneTask), done),
        });

        Log.Info($"[session {session.Id}] {Function}/CompleteTour 栏位 {index} 远征 {tourId} 收工" +
                 $"（{(great ? "大成功" : "普通")}）：发奖 {granted.Rewards.Count} 项、" +
                 $"品阶 {area.Rare} 完成次数 → {done}，重抽远征 {nextTour}，状态 → 1");

        // ★ 顺序与本域 OnGetGiveupItem（CafePetFeature.cs:689..692）一致：
        //   先发通用获得框，再发本域回包 —— 让「请刷新界面」的信号留在最后一条。
        if (!granted.IsEmpty)
            GameNotify.ShowRewardBox(_ctx.LuaCalls, session, granted);
        Reply(session, RspCompleteTour, new JsonObject { ["nIndex"] = index });
    }

    // =================================================================
    //  小工具
    // =================================================================

    /// <summary>第 <paramref name="slot"/>（1..3）只出征宠物的任务 id
    /// （<c>NCafePetTour.lua:14</c> <c>(FomationId - 1) * 3 + 550 + Index</c>）。</summary>
    internal static int FormationTask(int areaIndex, int slot)
        => PetTourTable.TaskFormationOff + (areaIndex - 1) * PetTourTable.PetsPerTour + slot;

    /// <summary>
    /// 判出征条件（<c>TourInfo.Condition</c>），与客户端 <c>IsPetsCanGoTour</c>
    /// （<c>NCafePetTour.lua:186..212</c>）同构：
    /// 类型 1 = 宠物只数 / 2 = 限定物种（比 <c>modelInfo.Type</c>）/ 3 = 限定性格（比 <c>pet.nature</c>）。
    ///
    /// ★ 类型 2 的「物种」是 <c>NCafePets.txt</c> 的 <c>Type</c> 列，要由 templateId 查表得到，
    /// 不是 templateId 本身。
    /// </summary>
    private bool ConditionsSatisfied(TourInfoRow tour, IReadOnlyList<CafePet> pets, out string why)
    {
        why = "";
        foreach (var (type, param, need) in tour.Condition)
        {
            int count = type switch
            {
                1 => pets.Count,
                2 => pets.Count(p => _table.PetTemplates.TryGetValue((int)p.TemplateId, out var m)
                                     && m.Type == param),
                3 => pets.Count(p => p.Nature == param),
                _ => pets.Count,   // 未知类型按「只数」兜底，与客户端 else 分支一致
            };
            if (count < need)
            {
                why = type switch
                {
                    2 => $"限定物种 {param} 只有 {count}/{need} 只",
                    3 => $"限定性格 {param} 只有 {count}/{need} 只",
                    _ => $"宠物只数 {count}/{need}",
                };
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// 给栏位抽一条远征任务：按栏位的 <c>Rare</c> 在 <c>TourInfo</c> 里按 <c>RandomValue</c> 权重抽。
    /// 该品阶没有配置时退回「任意一条」，再没有就返回 0（调用方会记 WARN）。
    ///
    /// ★ 确定性种子（栏位 + 已完成次数 + 玩家 id），理由同 <see cref="OnCheckTourIsSuccess"/>：
    /// 便于排查，且重放一致。
    /// </summary>
    private int RollTourId(TourAreaRow area, Player player, int index)
    {
        var pool = _tourTable.Tours.Where(t => t.Rare == area.Rare && t.RandomValue > 0).ToList();
        if (pool.Count == 0)
            pool = _tourTable.Tours.Where(t => t.RandomValue > 0).ToList();
        if (pool.Count == 0)
            return 0;

        int done = player.GetTask(PetTourTable.Group, PetTourTable.TaskCompleteNumOff + area.Rare);
        uint seed = (uint)index * 2654435761u + (uint)done * 40503u + (uint)player.Roleid;

        int total = pool.Sum(t => t.RandomValue);
        int roll = (int)(seed % (uint)total);
        foreach (var t in pool)
        {
            roll -= t.RandomValue;
            if (roll < 0)
                return t.Index;
        }
        return pool[^1].Index;
    }

    /// <summary>
    /// 给栏位抽四个 <c>TourReward</c> 条目（基础 / 指数 / 条件1 / 条件2 = <c>Kind</c> 1..4），
    /// 按 15 位段打包写进 850 那两个任务值。
    ///
    /// <para><b>★★ 位段是 1-based 的 <c>LoadBits(v,1,15)</c> / <c>LoadBits(v,16,30)</c></b></para>
    /// 换算成 C# 就是低 15 位与次 15 位，第 31 位空着（不碰符号位）——
    /// 见 <see cref="PetTourTable"/> 的类注释。
    /// </summary>
    private void RollAwards(Player player, int index, int tourId)
    {
        var tour = _tourTable.Tour(tourId);
        int rare = tour?.Rare ?? 1;

        int Pick(int kind, int salt)
        {
            var pool = _tourTable.Rewards.Where(r => r.Kind == kind && r.Rare == rare && r.Pro > 0).ToList();
            if (pool.Count == 0)
                pool = _tourTable.Rewards.Where(r => r.Kind == kind && r.Pro > 0).ToList();
            if (pool.Count == 0)
                return 0;

            uint seed = (uint)index * 2654435761u + (uint)tourId * 40503u + (uint)salt * 2246822519u;
            int total = pool.Sum(r => r.Pro);
            int roll = (int)(seed % (uint)total);
            foreach (var r in pool)
            {
                roll -= r.Pro;
                if (roll < 0)
                    return r.AwardId;
            }
            return pool[^1].AwardId;
        }

        int baseId = Pick(1, 1), numId = Pick(2, 2), con1 = Pick(3, 3), con2 = Pick(4, 4);

        player.SetTask(PetTourTable.Group, AwardTaskFirst(index),
                       Pack15(baseId, numId));
        player.SetTask(PetTourTable.Group, AwardTaskSecond(index),
                       Pack15(con1, con2));
    }

    /// <summary>850 段的第一个任务 id（低 15 位 = 基础奖励、次 15 位 = 指数奖励）
    /// —— <c>NCafePetTour.lua:96</c> <c>ToursAwardIdOff + (nIndex-1)*2 + 1</c>。</summary>
    internal static int AwardTaskFirst(int index)
        => PetTourTable.TaskAwardOff + (index - 1) * 2 + 1;

    /// <summary>850 段的第二个任务 id（低 15 位 = 条件1奖励、次 15 位 = 条件2奖励）
    /// —— <c>:99</c> <c>ToursAwardIdOff + nIndex*2</c>。</summary>
    internal static int AwardTaskSecond(int index)
        => PetTourTable.TaskAwardOff + index * 2;

    /// <summary>按 15 位段打包两个奖励 id（对应客户端的 <c>LoadBits(v,1,15)</c> / <c>(v,16,30)</c>）。</summary>
    internal static int Pack15(int low, int high)
        => (low & PetTourTable.MaxRewardId) | ((high & PetTourTable.MaxRewardId) << 15);

    /// <summary>把 850 段里那四个奖励 id 读回来（顺序：基础 / 指数 / 条件1 / 条件2）。</summary>
    internal static IReadOnlyList<int> ReadAwardIds(Player player, int index)
    {
        int first = player.GetTask(PetTourTable.Group, AwardTaskFirst(index));
        int second = player.GetTask(PetTourTable.Group, AwardTaskSecond(index));
        return new[]
        {
            first & PetTourTable.MaxRewardId,
            (first >> 15) & PetTourTable.MaxRewardId,
            second & PetTourTable.MaxRewardId,
            (second >> 15) & PetTourTable.MaxRewardId,
        }.Where(v => v != 0).ToList();
    }

    /// <summary>
    /// 把某个栏位的全部任务值推给客户端（pf 1030）。
    /// ★ 必须推 —— 客户端 <c>UI_PetTour</c> 的每个字段都是自己 <c>GetTaskValue</c> 现读的，
    /// 回包只是「请刷新」的信号（类注释 §5）。
    /// </summary>
    private static void PushTourTasks(ClientSession session, Player player, int index)
    {
        var ids = new List<int>
        {
            PetTourTable.TaskTourIdOff + index,
            PetTourTable.TaskStateOff + index,
            PetTourTable.TaskTimeOff + index,
            AwardTaskFirst(index),
            AwardTaskSecond(index),
        };
        for (int i = 1; i <= PetTourTable.PetsPerTour; i++)
            ids.Add(FormationTask(index, i));

        GameNotify.PushTaskValues(session, ids.Select(id => new KeyValuePair<uint, int>(
            Player.ComposeTaskId(PetTourTable.Group, id),
            player.GetTask(PetTourTable.Group, id))));
    }
}
