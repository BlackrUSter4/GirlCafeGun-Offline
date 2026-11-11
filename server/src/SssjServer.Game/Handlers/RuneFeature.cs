using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 符文（基因模块）养成：LuaCall 31 强化 / 35 突破。
///
/// <para><b>0. 命令面与本轮范围</b></para>
/// <list type="table">
///   <item><term>31 <c>Rune_Enhance</c></term><description>强化（表里叫「融合」）—— 本轮实现</description></item>
///   <item><term>35 <c>Rune_Break</c></term><description>突破 —— 本轮实现</description></item>
///   <item><term>33/34 重铸 + 用新技能</term><description>★ 池表已找到（见 §5），但属性阶梯语义未取证，后置</description></item>
///   <item><term>30 <c>Rune_Equip</c></term><description>★ 硬阻塞：<c>RuneItem.weaponGuid</c> 无下发通道（<c>docs/05</c> §30.1）</description></item>
///   <item><term>32 <c>Rune_Sell</c></term><description>已由背包分解域实现</description></item>
/// </list>
///
/// <para><b>1. ★★★ 31 的「材料」疑点已解决：只花金币，不吃材料</b></para>
/// 命令注释写「强化符文（融合）」容易让人以为要吃另一枚符文当素材，
/// 但**上行只有一个 <c>nGuid</c>**，两个发送点的前置检查也只看钱：
/// <code>
/// UI_ModuleMain.lua:466  self.nCost = ItemLogic:GetItemExp(self.pRune.EnhanceLevel, 5, self.pRune.Rarity)
/// UI_ModuleMain.lua:753  if not MoneyLogic:CheckMoney(MoneyLogic.eMoneyType_Gold, self.nCost) then 弹 20012
/// UI_ModuleMain.lua:759  if self.pRune.Level >= self.maxLevel then 弹「已满级」
/// UI_ModuleMain.lua:764  LuaCall:CallServer(LuaCall.Rune_Enhance, {nGuid = self.uRuneId})
/// </code>
/// ⇒ 一次强化 = **扣一次金币、等级 +1**。
///
/// <para><b>2. ★★★ 强化费用按星级分档：<c>NeedExp5_&lt;rarity&gt;</c></b></para>
/// <c>ItemLogic:GetItemExp(level, type, rarity)</c>（<c>ItemLogic.lua:103..110</c>）在
/// <c>rarity</c> 非 nil 时把列名拼成 <c>"NeedExp" .. type .. "_" .. rarity</c>。
/// 符文的 type = **5**（<c>WeaponCommon.lua:59</c> <c>ITEMEXP_Moudle = 5</c>）。
/// 实测 <c>ItemExp.txt</c> 里 1★→500 / 2★→750 / 3★→1250 / 4★→2500（Lv1→2）。
/// ⇒ 用 <c>NeedExp5</c>（无后缀）会让 **2/3/4★ 符文都按 1★ 收费**，且不抛不报错
/// —— 与「武器用 <c>NeedExp3</c> 而非 <c>NeedExp2</c>」是同一类坑（AGENTS.md §5）。
///
/// <para><b>3. ★★★ 等级上限先看关卡进度，突破配置只在第三档才接管</b></para>
/// <c>RuneLogic:GetMaxLevel</c>（<c>Rune.lua:176..195</c>）：
/// <code>
/// maxLevel = 5;  if 通关5-12 then 10 end;  if 通关6-12 then 15 end
/// if maxLevel == 15 and 有突破配置 then maxLevel = MaxLevel&lt;BreakNum&gt; end
/// </code>
/// ⇒ **没通关 6-12 的号，符文上限恒 ≤ 15，突破的 MaxLevel 压根不生效**。
/// 服务端按同一公式复核（<see cref="RuneTable.MaxLevelFor"/>），否则会「允许升到 30 级
/// 但客户端界面按 15 显示满级」。
///
/// <para><b>4. ★★ 35 的材料按 guid 指定（与角色卡按模板不同）</b></para>
/// <code>
/// UI_ModuleMain.lua:805..816
///     data = { nGuid = self.uRuneId, tbGuid = {} }
///     for k,v in pairs(self.BreakitemInfo) do
///         local tbInfo = Game.EquipManager.GetItemByGDPL(v[1],v[2],v[3],v[4])
///         table.insert(data.tbGuid, {tbInfo.Guid, v[5]})     -- ★ {guid, 数量}
///     end
/// </code>
/// ⇒ 服务端要按 **guid** 找材料并复核「它确实是配置要求的那个模板」——
/// 只信 guid 不校验模板的话，客户端（或改包）可以拿廉价道具顶替。
/// ★ 本实现**两者都做**：既按 guid 扣，也复核模板与数量满足 <c>BreakItem&lt;n&gt;</c>。
///
/// <para><b>5. ★★ <c>CurrencyCount&lt;n&gt;</c> 列名骗人：实际是 <c>[货币id-数量]</c></b></para>
/// 实测 <c>RuneBreak.txt</c> 的 <c>CurrencyCount1 = [2-20000]</c>（金币 20000）。
/// 按「纯数量」解析会得到 **2** ⇒ 突破几乎免费。解析见 <c>RuneTable.ParsePair</c>。
///
/// <para><b>6. 错误约定：LuaCall 数字族（两条都是）</b></para>
/// <code>
/// UI_ModuleMain.lua:769  function tbClass:OnEnhanceRune(tbParam)  if tbParam.nError then 弹提示
/// UI_ModuleMain.lua:820  function tbClass:OnReceiveBreak(tbParam) if tbParam and tbParam.nError then 弹提示
/// </code>
/// ⇒ 成功**绝不能带 <c>nError</c>**（真值判断，0 也算真）。
/// ★ 失败要**带 <c>nError</c>**（与角色卡突破「失败不回包」不同）——
/// 因为这两个消费点都有明确的错误分支，回了能给玩家提示。
///
/// <para><b>7. ★★ 成功后客户端 <c>self:Update()</c> 重读本地缓存 ⇒ 先推 pf 1031 再回包</b></para>
/// <c>UI_ModuleMain.lua:773</c> 成功分支第一句就是 <c>self:Update()</c>，
/// 它重新从 <c>Game.EquipManager</c> 读符文数据 —— 与 <c>docs/05</c> §22.2 同型。
/// </summary>
public sealed class RuneFeature : IGameFeature
{
    /// <summary>LuaCall.Rune_Enhance = 31（<c>LuaCall.lua:35</c>「请求强化符文（融合）」）。</summary>
    public const int EnhanceCmd = 31;

    /// <summary>LuaCall.Rune_Break = 35（<c>LuaCall.lua:39</c>「请求突破符文」）。</summary>
    public const int BreakCmd = 35;

    /// <summary>符文的 genre。</summary>
    public const uint RuneGenre = RuneTable.RuneGenre;

    /// <summary>金币货币 id（强化固定用它：<c>MoneyLogic.eMoneyType_Gold</c>）。</summary>
    public const int GoldMoneyId = 2;

    /// <summary>解锁符文 10 级所需通关的关卡（<c>Rune.lua:179</c> 注释「通关5-12后，解锁」）。</summary>
    public const int UnlockLevel10Chapter = 5;

    /// <summary>解锁符文 15 级所需通关的关卡（<c>Rune.lua:180</c>「通关6-12后」）。</summary>
    public const int UnlockLevel15Chapter = 6;

    /// <summary>上面两条对应的关卡内序号（都是 12）。</summary>
    public const int UnlockChapterStage = 12;

    /// <summary>
    /// 通用失败码。★ 客户端把它拼成 <c>ui.Error.&lt;码&gt;</c> 查本地化。
    /// <b>20012</b> 是客户端自己用的「金币不足」（<c>UI_ModuleMain.lua:754</c>）⇒ 复用它最贴合。
    /// </summary>
    public const int ErrNotEnoughGold = 20012;

    /// <summary>其它拒绝原因的通用码（未配置文案时客户端显示原始 key，但至少有反馈）。</summary>
    public const int ErrGeneric = 1;

    /// <summary>LuaCall.Rune_Recast = 33「请求重铸符文」（第十三轮补）。</summary>
    public const int RecastCmd = 33;

    /// <summary>LuaCall.Rune_UseRecastSkill = 34「使用洗练后的技能」（第十三轮补）。</summary>
    public const int UseRecastCmd = 34;

    /// <summary>
    /// 符文子技能的 <c>skilltype</c>。★ 客户端 <c>AddItem</c> 对符文只认 <b>2</b> 与 <b>6</b>
    /// （都 <c>List.Add</c> 进 <c>SubSkillID</c>/<c>SubSkillLevel</c>），
    /// 且 <c>skilltype == 2</c> 会额外调 <c>RuneItem.UseRuneSkill</c>(RVA <c>0x1BA4050</c>)
    /// ⇒ 用 <b>2</b>（「生效中的词条」语义）。
    /// </summary>
    public const int RuneSkillType = 2;

    /// <summary>
    /// 一枚符文最多几条子技能 = <b>3</b>。
    /// 判据：客户端两处渲染都是 <c>while SubIdx &lt;= 3 and SubIdx &lt;= SubSkillID.Count</c>
    /// （<c>UI_Warehouse.lua:781</c>、<c>UI_GeneChoose.lua:124</c>）。
    /// </summary>
    public const int MaxSubSkills = 3;

    private GameFeatureContext _ctx = null!;
    private RuneTable _runes = RuneTable.Empty;

    /// <summary>
    /// 重铸抽词条的随机源。★ 用实例字段而非 <c>Random.Shared</c>，便于测试注入确定性种子。
    /// </summary>
    private Random _rng = new();

    public string Name => "符文养成（LuaCall 31 强化 / 35 突破）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _runes = RuneTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));

        ctx.MapLuaCall(EnhanceCmd, OnEnhance);
        ctx.MapLuaCall(BreakCmd, OnBreak);
        ctx.MapLuaCall(RecastCmd, OnRecast);
        ctx.MapLuaCall(UseRecastCmd, OnUseRecast);
    }

    /// <summary>测试用：注入确定性随机源。</summary>
    internal void SetRngForTests(Random rng) => _rng = rng;

    // ================================================================ 31 强化

    /// <summary>
    /// 31 强化：扣金币、等级 +1。上行 <c>{nGuid}</c>（类注释 §1）。
    /// </summary>
    internal Task OnEnhance(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        uint guid = ReadUInt((param as JsonObject)?["nGuid"]);
        var (rune, row, err) = Resolve(player, guid);
        if (rune is null || row is null)
        {
            Log.Warn($"[session {session.Id}] Rune_Enhance(31) guid={guid} {err}");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        // 等级上限（★ 先看关卡进度，见类注释 §3）
        int maxLevel = MaxLevelOf(player, rune, row);
        int curLevel = (int)rune.EnhanceLevel;
        if (curLevel >= maxLevel)
        {
            Log.Warn($"[session {session.Id}] Rune_Enhance(31) guid={guid} 已满级"
                   + $"（{curLevel}/{maxLevel}，突破 {rune.BreakLevel} 次），拒绝");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        // ★★ 费用按星级分档：NeedExp5_<rarity>（见类注释 §2）
        int cost = _runes.EnhanceCost(row.Rarity, curLevel);
        if (cost <= 0)
        {
            Log.Warn($"[session {session.Id}] Rune_Enhance(31) guid={guid} 查不到强化费用"
                   + $"（Lv{curLevel} type=5 rarity={row.Rarity}），拒绝");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        if (player.GetMoney(GoldMoneyId) < cost)
        {
            Log.Warn($"[session {session.Id}] Rune_Enhance(31) 金币不足：需要 {cost}、"
                   + $"持有 {player.GetMoney(GoldMoneyId)}，拒绝");
            Fail(session, cmd, ErrNotEnoughGold);
            return Task.CompletedTask;
        }

        player.AddMoney(GoldMoneyId, -cost);
        rune.EnhanceLevel = (uint)(curLevel + 1);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 符文强化 guid={guid}（{row.Rarity}★）："
               + $"Lv{curLevel} → Lv{rune.EnhanceLevel}（上限 {maxLevel}），金币 -{cost}");

        // ★★ 先推 1031 + 货币，再回包（§7）
        GameNotify.PushItem(session, rune);
        GameNotify.PushMoney(session, player, GoldMoneyId);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 35 突破

    /// <summary>
    /// 35 突破：吃材料 + 金币，突破次数 +1（等级上限随之提高）。
    /// 上行 <c>{nGuid, tbGuid = [[guid, 数量], …]}</c>（类注释 §4）。
    /// </summary>
    internal Task OnBreak(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        uint guid = ReadUInt(body?["nGuid"]);
        var (rune, row, err) = Resolve(player, guid);
        if (rune is null || row is null)
        {
            Log.Warn($"[session {session.Id}] Rune_Break(35) guid={guid} {err}");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        var brk = _runes.BreakOf(row.BreakConfigId);
        int next = (int)rune.BreakLevel + 1;
        if (brk is null || next > brk.MaxBreak)
        {
            Log.Warn($"[session {session.Id}] Rune_Break(35) guid={guid} 不能再突破"
                   + $"（当前 {rune.BreakLevel} 次，"
                   + $"{(brk is null ? $"无突破配置 breakId={row.BreakConfigId}" : $"上限 {brk.MaxBreak}")}），拒绝");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        var step = brk.Steps[next - 1];

        // ---- 校验货币（★ CurrencyCount<n> 带货币 id，见 §5）----
        int curId = step.CurrencyId > 0 ? step.CurrencyId : GoldMoneyId;
        if (step.CurrencyCount > 0 && player.GetMoney(curId) < step.CurrencyCount)
        {
            Log.Warn($"[session {session.Id}] Rune_Break(35) 货币不足：需要 "
                   + $"{curId}×{step.CurrencyCount}、持有 {player.GetMoney(curId)}，拒绝");
            Fail(session, cmd, ErrNotEnoughGold);
            return Task.CompletedTask;
        }

        // ---- 校验材料 ----
        // ★★ 双重校验（§4）：既按客户端给的 guid 找，也复核「模板与数量满足配置」，
        //    否则改包可以拿廉价道具顶替。
        foreach (var need in step.Items)
        {
            int have = player.Items
                .Where(i => i.Genre == (uint)need.G && i.Detail == (uint)need.D
                         && i.Particular == (uint)need.P && i.Level == (uint)need.L)
                .Sum(i => (int)i.Count);
            if (have < need.N)
            {
                Log.Warn($"[session {session.Id}] Rune_Break(35) 材料不足："
                       + $"({need.G},{need.D},{need.P},{need.L}) 需要 {need.N}、持有 {have}，拒绝");
                Fail(session, cmd, ErrGeneric);
                return Task.CompletedTask;
            }
        }

        // ---- 扣材料（按配置扣，不按客户端给的 guid 列表扣）----
        var changed = new List<Item>();
        var removed = new List<Item>();
        foreach (var need in step.Items)
            Consume(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L,
                    need.N, changed, removed);

        if (step.CurrencyCount > 0)
            player.AddMoney(curId, -step.CurrencyCount);

        rune.BreakLevel = (uint)next;
        if (!changed.Contains(rune))
            changed.Add(rune);

        _ctx.Store.Save(player);

        int newMax = MaxLevelOf(player, rune, row);
        Log.Info($"[session {session.Id}] 符文突破 guid={guid}（{row.Rarity}★）：第 {next} 次"
               + $"（上限 {brk.MaxBreak}）→ 等级上限 {newMax}；消耗 货币{curId}×{step.CurrencyCount}"
               + $" + 材料 {string.Join(',', step.Items.Select(i => $"({i.G},{i.D},{i.P},{i.L})×{i.N}"))}");

        // ★ 先推 1031 + 货币，再回包
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);
        if (step.CurrencyCount > 0)
            GameNotify.PushMoney(session, player, curId);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    // ================================================================ 33 重铸 / 34 用新技能

    /// <summary>
    /// 33 重铸（洗练）符文：花消耗重抽子技能词条。上行 <c>{rune = guid}</c>
    /// （<c>UI_ModuleMain.lua:844</c> / <c>UI_GeneUpdate.lua:494</c>，第 3 实参 <c>true</c>）。
    ///
    /// <para><b>1. ★★★ 服务端只下发 skillId，不下发数值</b></para>
    /// 这是本域最初被判「做不了」的那个卡点，本轮解除：
    /// <code>
    /// UI_GeneChoose.lua:61  local runeMagic = self.pRune:GetRuneSkillInfo(skillID)
    /// dump.cs:542520        RuneItem.GetRuneSkillInfo(int skillId)
    ///   → 0x01BA46AC get_breakLevel → 0x01BA46E4 ItemMgr.GetRuneSkillInfo(id, breakNum)
    /// il2cpp_xref "Item/RuneSkillList.txt" → 0x15B20B0 Game.ItemMgr.RuneSkillInfoInit
    /// </code>
    /// ⇒ **客户端在 C# 侧自己加载 <c>RuneSkillList.txt</c>**，按 <c>(skillId, 突破等级)</c>
    /// 反查那 6 个属性的阶梯值。这就是 Lua 侧 grep 不到那张表的原因。
    /// ⇒ 服务端**只需抽出 skillId 列表**，属性数值完全由客户端算 ——
    /// 「属性阶梯语义未取证」不再是阻塞。
    ///
    /// <para><b>2. 抽取规则</b></para>
    /// <c>RuneSkillList.txt</c> 24 条，按 <c>RuneRarity</c> 1/2/3/4 **各 6 条**、
    /// 每条只有一个属性列非空（= 一种词条），<c>Rate</c> 是权重。
    /// ⇒ 先按符文星级过滤，再按 <c>Rate</c> 权重**不重复**抽 <see cref="MaxSubSkills"/> 条。
    ///
    /// <para><b>3. 存储：直接替换 <see cref="Item.Skills"/>（skilltype 2）</b></para>
    /// 客户端 <c>AddItem</c> 对符文把 <c>skillinfo</c> 里 <c>skilltype ∈ {2,6}</c> 的条目
    /// <c>List.Add</c> 进 <c>SubSkillID</c>（有 Count 保护、空数组安全）。
    /// ★ 而 <c>skillinfo</c> 是「无条件先 Clear」的四字段之一（§0.3 红线）
    /// ⇒ 重推时必须把**当前全部**子技能一起发（本实现每次都发完整列表）。
    ///
    /// <para><b>4. 错误约定：LuaCall 数字族（真值判断）</b></para>
    /// <c>UI_ModuleMain.lua:850</c> <c>if tbParam.nError then</c> ⇒ 成功**绝不能带**。
    /// ★ 成功后客户端 <c>:855</c> <c>UI.Open("UI_GeneChoose", …)</c> 打开对比界面，
    /// 那个界面从 <c>GetRune(guid)</c> **重读本地缓存** ⇒ **先推 pf 1031 再回包**。
    /// </summary>
    internal Task OnRecast(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // 上行字段名是 rune（不是 nGuid —— 与 31/35 不同，别抄错）
        uint guid = ReadUInt((param as JsonObject)?["rune"]);
        var (rune, row, err) = Resolve(player, guid);
        if (rune is null || row is null)
        {
            Log.Warn($"[session {session.Id}] Rune_Recast(33) guid={guid} {err}");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        // ---- 校验消耗（Rune.txt 的 RecastGDPLN + RecastMoney）----
        if (row.RecastMoneyCount > 0
            && player.GetMoney(row.RecastMoneyId) < row.RecastMoneyCount)
        {
            Log.Warn($"[session {session.Id}] Rune_Recast(33) 货币不足：需要 "
                   + $"{row.RecastMoneyId}×{row.RecastMoneyCount}、持有 "
                   + $"{player.GetMoney(row.RecastMoneyId)}，拒绝");
            Fail(session, cmd, ErrNotEnoughGold);
            return Task.CompletedTask;
        }
        foreach (var need in row.RecastItems)
        {
            int have = player.Items
                .Where(i => i.Genre == (uint)need.G && i.Detail == (uint)need.D
                         && i.Particular == (uint)need.P && i.Level == (uint)need.L)
                .Sum(i => (int)i.Count);
            if (have < need.N)
            {
                Log.Warn($"[session {session.Id}] Rune_Recast(33) 材料不足："
                       + $"({need.G},{need.D},{need.P},{need.L}) 需要 {need.N}、持有 {have}，拒绝");
                Fail(session, cmd, ErrGeneric);
                return Task.CompletedTask;
            }
        }

        // ---- 抽新词条 ----
        var picked = RollSkills(row.Rarity);
        if (picked.Count == 0)
        {
            Log.Warn($"[session {session.Id}] Rune_Recast(33) 星级 {row.Rarity} 在 "
                   + $"RuneSkillList.txt 里没有候选词条（共 {_runes.Skills.Count} 条），拒绝");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        // ---- 扣消耗 ----
        var changed = new List<Item>();
        var removed = new List<Item>();
        foreach (var need in row.RecastItems)
            Consume(player, (uint)need.G, (uint)need.D, (uint)need.P, (uint)need.L,
                    need.N, changed, removed);
        if (row.RecastMoneyCount > 0)
            player.AddMoney(row.RecastMoneyId, -row.RecastMoneyCount);

        // ---- 替换子技能（skilltype 2）----
        var before = rune.Skills.Select(k => k.SkillId).ToList();
        rune.Skills = picked
            .Select(id => new ItemSkill { SkillId = (uint)id, SkillLevel = 1, SkillType = RuneSkillType })
            .ToList();
        if (!changed.Contains(rune))
            changed.Add(rune);

        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] 符文重铸 guid={guid}（{row.Rarity}★）："
               + $"[{string.Join(',', before)}] → [{string.Join(',', picked)}]；"
               + $"消耗 货币{row.RecastMoneyId}×{row.RecastMoneyCount}"
               + (row.RecastItems.Count > 0
                   ? " + 材料 " + string.Join(',', row.RecastItems.Select(i => $"({i.G},{i.D},{i.P},{i.L})×{i.N}"))
                   : ""));

        // ★ 先推 1031（客户端 UI_GeneChoose 从本地缓存重读），再回包
        if (removed.Count > 0)
            GameNotify.PushItemsRemoved(session, removed);
        GameNotify.PushItems(session, changed);
        if (row.RecastMoneyCount > 0)
            GameNotify.PushMoney(session, player, row.RecastMoneyId);
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 34 使用洗练后的技能（在「旧/新」对比界面点确认）。
    /// 上行 <c>{rune = guid, usenew = true|false}</c>（<c>UI_GeneChoose.lua:168/172</c>）。
    ///
    /// <para><b>★★ 本服的语义与原厂略有不同（已论证）</b></para>
    /// 原厂大概率把「重铸结果」暂存在服务端、由 34 决定是否落地。
    /// 但本服 33 **已经直接落地**（因为客户端 <c>UI_GeneChoose</c> 从
    /// <c>GetRune(guid)</c> 重读**本地缓存**渲染「新」栏 —— 不落地就没得看）。
    /// ⇒ 34 在这里是**确认/放弃的记账**：
    /// <list type="bullet">
    ///   <item><c>usenew = true</c> —— 保留（33 已落地，无需再动）；</item>
    ///   <item><c>usenew = false</c> —— 玩家想回退旧词条。★ **本服做不到**：
    ///     33 落地时旧词条已被覆盖，而原厂的暂存通道（<c>ItemInfo</c> 里符文没有
    ///     <c>recastskill</c> 那样的备份字段 —— 那两个字段属 <c>WeaponItem</c>）未取证。
    ///     ⇒ 仍然**回成功包**（不回会卡界面），但日志记明「无法回退」。</item>
    /// </list>
    /// ★ 这是本域唯一的行为妥协，写在这里以免后人误以为是 bug。
    ///
    /// ★ 错误约定：LuaCall 数字族（<c>UI_GeneChoose.lua:178</c> <c>if tbParam.nError then</c>）
    /// ⇒ 成功绝不能带。
    /// ★★ 原厂服务端分支是 <c>if tbRet then CallClient(...) end</c>（可以不回包），
    /// 但 <c>UI_GeneChoose.lua:182</c> 成功分支要把 <c>objUp_HaveClick</c> 复位
    /// ⇒ **不回包会让按钮永久锁死**，所以本实现一律回包。
    /// </summary>
    internal Task OnUseRecast(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var body = param as JsonObject;
        uint guid = ReadUInt(body?["rune"]);
        bool useNew = body?["usenew"] is JsonValue v
                      && (v.TryGetValue<bool>(out bool b) ? b
                          : v.TryGetValue<int>(out int i) && i != 0);

        var (rune, row, err) = Resolve(player, guid);
        if (rune is null || row is null)
        {
            Log.Warn($"[session {session.Id}] Rune_UseRecastSkill(34) guid={guid} {err}");
            Fail(session, cmd, ErrGeneric);
            return Task.CompletedTask;
        }

        if (useNew)
        {
            Log.Info($"[session {session.Id}] Rune_UseRecastSkill(34) guid={guid} 保留新词条 "
                   + $"[{string.Join(',', rune.Skills.Select(k => k.SkillId))}]");
        }
        else
        {
            // ★ 见方法注释：33 已落地、旧词条无备份通道 ⇒ 只能记日志
            Log.Warn($"[session {session.Id}] Rune_UseRecastSkill(34) guid={guid} 请求放弃新词条，"
                   + "但本服 33 已直接落地、旧词条无备份通道（原厂暂存字段未取证）⇒ 保持现状");
        }

        // ★ 一律回包：不回会让 objUp_HaveClick 永久锁死（见方法注释）
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject());
        return Task.CompletedTask;
    }

    /// <summary>
    /// 按符文星级从 <c>RuneSkillList.txt</c> 权重抽 <see cref="MaxSubSkills"/> 条**不重复**词条。
    /// </summary>
    internal List<int> RollSkills(int rarity)
    {
        var pool = _runes.Skills.Where(s => s.RuneRarity == rarity).ToList();
        var result = new List<int>();

        while (result.Count < MaxSubSkills && pool.Count > 0)
        {
            int total = pool.Sum(s => Math.Max(0, s.Rate));
            RuneSkillRow? hit = null;

            if (total <= 0)
            {
                // 权重全 0 ⇒ 均匀抽（避免整组永远抽不出来）
                hit = pool[_rng.Next(pool.Count)];
            }
            else
            {
                int roll = _rng.Next(total);
                foreach (var s in pool)
                {
                    roll -= Math.Max(0, s.Rate);
                    if (roll < 0)
                    {
                        hit = s;
                        break;
                    }
                }
                hit ??= pool[^1];
            }

            result.Add(hit.Id);
            pool.Remove(hit);        // ★ 不重复
        }

        return result;
    }

    // ================================================================ 公共

    /// <summary>找符文实例 + 它的模板配置。</summary>
    private (Item? Rune, RuneRow? Row, string Err) Resolve(Player player, uint guid)
    {
        if (guid == 0)
            return (null, null, "guid 为 0（参数非法）");

        var item = player.Items.FirstOrDefault(i => i.Guid == guid);
        if (item is null)
            return (null, null, "背包里没有这个 guid");
        if (item.Genre != RuneGenre)
            return (null, null, $"guid={guid} 的 genre 是 {item.Genre}，不是符文(3)");

        var row = _runes.RuneOf((int)item.Genre, (int)item.Detail,
                                (int)item.Particular, (int)item.Level);
        if (row is null)
            return (null, null, $"({item.Genre},{item.Detail},{item.Particular},{item.Level}) 不在 Rune.txt 里");

        return (item, row, "");
    }

    /// <summary>
    /// 符文等级上限。★ 先看玩家关卡进度（类注释 §3），到 15 档后突破配置才接管。
    /// </summary>
    internal int MaxLevelOf(Player player, Item rune, RuneRow row)
    {
        bool p512 = HasCleared(player, UnlockLevel10Chapter, UnlockChapterStage);
        bool p612 = HasCleared(player, UnlockLevel15Chapter, UnlockChapterStage);
        return _runes.MaxLevelFor(row.BreakConfigId, (int)rune.BreakLevel, p512, p612);
    }

    /// <summary>
    /// 是否通关某一关。客户端判据 <c>LevelManager:CheckLevelState(5, 12, 1) == 2</c>
    /// （<c>Rune.lua:179</c>），本服的等价口径是「关卡记录里该关星级 &gt; 0」。
    /// </summary>
    private static bool HasCleared(Player player, int chapter, int stage)
    {
        // 关卡 id 的编码与 ChapterHandler 一致：章 * 100 + 关
        uint mapId = (uint)(chapter * 100 + stage);
        return player.Levels.TryGetValue(mapId, out int star) && star > 0;
    }

    /// <summary>按模板扣道具（够不够由调用方先校验）。</summary>
    private static void Consume(Player player, uint g, uint d, uint p, uint l, int want,
                               List<Item> changed, List<Item> removed)
    {
        int left = want;
        foreach (var it in player.Items
                     .Where(i => i.Genre == g && i.Detail == d && i.Particular == p && i.Level == l)
                     .ToList())
        {
            if (left <= 0) break;
            uint take = (uint)Math.Min(left, (int)it.Count);
            it.Count -= take;
            left -= (int)take;
            if (it.Count == 0)
            {
                player.Items.Remove(it);
                removed.Add(it);
            }
            else if (!changed.Contains(it))
            {
                changed.Add(it);
            }
        }
    }

    private static uint ReadUInt(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<uint>(out uint u)) return u;
        if (v.TryGetValue<long>(out long l)) return l > 0 ? (uint)l : 0;
        if (v.TryGetValue<double>(out double d)) return d > 0 ? (uint)d : 0;
        if (v.TryGetValue<string>(out string? s) && uint.TryParse(s, out uint p)) return p;
        return 0;
    }

    /// <summary>
    /// 失败回包：<c>{nError = 码}</c>。
    /// ★ 本域**失败要回包**（两个消费点都有 <c>nError</c> 分支，见类注释 §6），
    /// 与角色卡突破「失败不回包」不同。
    /// </summary>
    private void Fail(ClientSession session, int cmd, int code)
        => _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = code });
}
