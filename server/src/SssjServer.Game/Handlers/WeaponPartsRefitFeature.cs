using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 零件词条覆写（LuaCall <b>351</b> <c>WeaponParts_Overwrite</c> /
/// <b>352</b> <c>WeaponParts_Save</c>）—— 武器零件的「消耗素材零件换随机词条」玩法。
///
/// <para><b>0. 入口链（★ 第十五轮曾误判为「零入口」）</b></para>
/// <code>
/// UI_WeaponStrong（武器强化，常驻界面）
///   └ 零件改造页「覆写」按钮  UI_WeaponStrong.lua:370..379
///        ├ SpAffixType 高位 >0        → UI.OpenAsChild('UI_WeaponSkillSelected')  → 352 nType=2
///        ├ 有新词条 / IsRewrite       → UI.CloseAndOpen('UI_WeaponRefitChoose')   → 352 nType=1/3
///        └ 否则                        → UI.CloseAndOpen('UI_WeaponOverWriteStore')→ 351
/// </code>
/// ★ 这三处用的是 <b><c>UI.CloseAndOpen</c></b> 而不是 <c>UI.Open</c> ——
/// 只 grep 后者会把「入口活着」的域误判成死内容（AGENTS.md §5 已记）。
///
/// <para><b>1. ★★★ 回包必须带 <c>hasSp</c>，否则界面永久卡遮罩</b></para>
/// <c>LuaCall.lua:3600..3616</c> 的客户端分支**第一句**就是：
/// <code>
/// if tbParam.hasSp == nil then return end
/// if tbParam.hasSp then UI.OpenAsChild('UI_WeaponSkillSelected', partGuid, id)
/// else UI.Pop(); UI.CloseAndOpen('UI_WeaponRefitChoose', partGuid, id, true) end
/// </code>
/// 而上行是带 <b>300ms Blocking</b> 发的（<c>UI_WeaponOverWriteStore.lua:448/452</c>）
/// ⇒ 不回包 / 漏 <c>hasSp</c> = **遮罩永远不消失**。
/// ⇒ 回包 = **原样回显上行** + 追加 <c>hasSp</c>(bool) 与 <c>id</c>(新词条 id)。
///
/// <para><b>2. 351 的四种 nType（<c>UI_WeaponOverWriteStore.lua:17..45</c> 四个页签）</b></para>
/// <list type="bullet">
///   <item><b>1 覆写词条</b>：消耗 <c>CurrencyNum</c> + <c>ItemNum</c> 点数；</item>
///   <item><b>2 激活</b>：消耗 <c>ActiveCost</c> + <c>ActiveItem</c> 组套道具；</item>
///   <item><b>3 充能</b>：消耗 <c>ChargeCost</c> + <c>ChargeItem</c>；</item>
///   <item><b>4 属性改写</b>：消耗 <c>RewriteCost</c> + <c>RewriteItem</c> 点数。</item>
/// </list>
/// 消耗判据取证 <c>EnoughMoney</c>（<c>:346..357</c>）与 <c>CheckItem</c>（<c>:397..405</c>）。
///
/// <para><b>3. 352 只是「二选一确认」，原样回显即可</b></para>
/// <c>LuaCall.lua:3619..3629</c>：按 <c>nType</c> 分流到
/// <c>UI_WeaponRefitChoose:Response(bNew)</c>（1/3）或 <c>UI_WeaponSkillSelected:Response()</c>（2）。
/// 客户端只用 <c>nType</c> 分流、用 <c>bNew</c> 选文案 ⇒ **服务端原样回显 + 落库即可**。
///
/// <para><b>4. ★ 两条命令都没有错误通道</b></para>
/// 两个 else 分支都不判任何错误键 ⇒ 失败只能「不回包」，而那样就是卡遮罩。
/// ⇒ **策略：前置校验不通过时也回包**（<c>hasSp=false</c> + 保持原状态），
/// 让玩家看到「没有变化」而不是卡死。只有 <c>partGuid</c> 完全非法时才拒绝。
///
/// <para><b>5. 服务端逻辑不在包内</b></para>
/// 随包 <c>OriginalLogic.lua</c>（148 行）只有客户端侧函数，
/// **没有 <c>StartOverwrite</c> / <c>Save</c>**（正是 <c>MODULE_SERVER</c> 分支调的那两个）
/// ⇒ 与 <c>ClubLogic</c>/<c>MercenaryLogic</c> 同型，服务端逻辑自己补。
/// 完整取证见 <c>work/parts-refit-forensics.md</c>。
/// </summary>
public sealed class WeaponPartsRefitFeature : IGameFeature
{
    /// <summary>351 <c>WeaponParts_Overwrite</c>：覆写 / 激活 / 充能 / 属性改写。</summary>
    public const int CmdOverwrite = 351;

    /// <summary>352 <c>WeaponParts_Save</c>：新旧二选一的确认。</summary>
    public const int CmdSave = 352;

    /// <summary>零件的 genre（<c>UI_WeaponOverWriteStore.lua:424</c> 判 <c>v.pPart.G == 4</c>）。</summary>
    public const uint PartsGenre = 4;

    /// <summary>存档键（<c>Player.Extra</c>）：记录「待二选一」的新词条。</summary>
    public const string ExtraKey = "partsrefit";

    private GameFeatureContext _ctx = null!;
    private PartsRefitTable _table = null!;

    public string Name => "零件词条覆写（LuaCall 351 覆写/激活/充能/改写 + 352 新旧二选一）";

    /// <summary>本模块注册的全部 LuaCall id（供测试与文档校对）。</summary>
    internal static IReadOnlyList<int> HandledCmds { get; } = new[] { CmdOverwrite, CmdSave };

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _table = PartsRefitTable.Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(CmdOverwrite, OnOverwrite);
        ctx.MapLuaCall(CmdSave, OnSave);
    }

    // ================================================================ 351

    /// <summary>
    /// 351 覆写。上行
    /// <c>{partGuid, ItemGuids=[素材零件 guid…], nType, remainOrgPlus, srCoinNum, ssrCoinNum}</c>。
    ///
    /// ★★★ 回包必须**原样回显 + `hasSp` + `id`**（类注释 §1）。
    /// </summary>
    internal Task OnOverwrite(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        // ★ JsonNode["k"] 对 JsonArray 会抛（AGENTS.md §5）⇒ 先判类型
        var o = param as JsonObject;
        uint partGuid = ReadUInt(o, "partGuid");
        int opType = ReadInt(o, "nType", PartsRefitTable.OpOverwrite);

        var part = player.Items.FirstOrDefault(i => i.Guid == partGuid && i.Genre == PartsGenre);
        if (part is null)
        {
            // 只有目标零件本身非法时才拒绝（回包也带 hasSp，避免卡遮罩）
            Log.Warn($"[session {session.Id}] WeaponParts_Overwrite(351) partGuid={partGuid} " +
                     $"不是本玩家的零件(genre={PartsGenre})，拒绝但仍回包（避免 300ms Blocking 卡死）");
            ReplyOverwrite(session, cmd, o, hasSp: false, affixId: 0);
            return Task.CompletedTask;
        }

        var materials = ReadGuidList(o, "ItemGuids")
            .Select(g => player.Items.FirstOrDefault(i => i.Guid == g && i.Genre == PartsGenre))
            .Where(i => i is not null)
            .Select(i => i!)
            .ToList();

        var rule = _table.Overwrite(PartTypeOf(part), (int)part.Level);
        int cost = rule?.CostOf(opType) ?? 0;
        int currencyId = rule?.CurrencyId ?? 0;

        // ---- 前置校验：不足时**照样回包**（类注释 §4），只是不产生新词条 ----
        if (rule is null)
        {
            Log.Warn($"[session {session.Id}] WeaponParts_Overwrite(351) 零件 {partGuid} " +
                     $"(type={PartTypeOf(part)} rarity={part.Level}) 没有覆写规则，回空结果");
            ReplyOverwrite(session, cmd, o, hasSp: false, affixId: 0);
            return Task.CompletedTask;
        }
        if (cost > 0 && player.GetMoney(currencyId) < cost)
        {
            Log.Warn($"[session {session.Id}] WeaponParts_Overwrite(351) nType={opType} 货币不足：" +
                     $"需要 {currencyId}×{cost}、持有 {player.GetMoney(currencyId)}，回空结果");
            ReplyOverwrite(session, cmd, o, hasSp: false, affixId: 0);
            return Task.CompletedTask;
        }
        if (materials.Count == 0)
        {
            Log.Warn($"[session {session.Id}] WeaponParts_Overwrite(351) nType={opType} " +
                     "没有有效素材零件，回空结果");
            ReplyOverwrite(session, cmd, o, hasSp: false, affixId: 0);
            return Task.CompletedTask;
        }

        // ---- 结算 ----
        var changed = new List<int>();
        if (cost > 0)
        {
            player.AddMoney(currencyId, -cost);
            changed.Add(currencyId);
        }

        // 素材零件被消耗掉（客户端 Comfirm 里已把它们当"要被吃掉的"收集起来）
        foreach (var m in materials)
            player.Items.Remove(m);

        // ★ 随机出一个新词条（词缀池 22 条）。确定性种子：guid + 已用次数，
        //   这样同一次操作重放结果一致，便于排查。
        var affixes = _table.AllAffixes;
        int affixId = 0;
        if (affixes.Count > 0)
        {
            uint seed = partGuid * 2654435761u + (uint)materials.Count * 40503u + (uint)opType;
            affixId = affixes[(int)(seed % (uint)affixes.Count)].Type;
        }

        // 记录「待二选一」的新词条，等 352 来确认保留新的还是旧的
        var state = LoadState(player);
        state.Pending[partGuid] = new PartsRefitPending
        {
            PartGuid = partGuid,
            NewAffixId = affixId,
            OpType = opType,
        };
        SaveState(player, state);

        if (changed.Count > 0)
            GameNotify.PushMoney(session, player, changed);
        GameNotify.PushItemsRemoved(session, materials);

        // ★ hasSp：是否要走「技能选择」界面。本服按 opType==2(激活) 处理为 true，
        //   其余走「新旧二选一」(UI_WeaponRefitChoose)。
        bool hasSp = opType == PartsRefitTable.OpActive;

        Log.Info($"[session {session.Id}] WeaponParts_Overwrite(351) 零件 {partGuid}" +
                 $"（type={PartTypeOf(part)} rarity={part.Level}）nType={opType}" +
                 $"（{OpName(opType)}）：吃掉素材 {materials.Count} 个" +
                 (cost > 0 ? $"、扣 货币{currencyId}×{cost}" : "") +
                 $" ⇒ 新词条 id={affixId} hasSp={hasSp}；推 pf 1031 + 1032");

        ReplyOverwrite(session, cmd, o, hasSp, affixId);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 351 的回包：**原样回显上行** + <c>hasSp</c> + <c>id</c>。
    /// ★ 原样回显是必须的 —— 客户端要用回包里的 <c>partGuid</c> 打开下一个界面。
    /// </summary>
    private void ReplyOverwrite(ClientSession session, int cmd, JsonObject? upstream,
                                bool hasSp, int affixId)
    {
        var reply = upstream is null ? new JsonObject() : CloneObject(upstream);
        reply["hasSp"] = hasSp;      // ★★★ 漏了它客户端第一句就 return ⇒ 永久卡遮罩
        reply["id"] = affixId;
        _ctx.LuaCalls.CallClient(session, cmd, reply);
    }

    // ================================================================ 352

    /// <summary>
    /// 352 保存二选一结果。上行 <c>{guid, bNew, nType}</c>。
    ///
    /// <c>bNew = true</c> 保留新词条、<c>false</c> 保留旧的。
    /// ★ 客户端只用 <c>nType</c> 分流、用 <c>bNew</c> 选文案 ⇒ **原样回显**即可。
    /// </summary>
    internal Task OnSave(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var o = param as JsonObject;
        uint guid = ReadUInt(o, "guid");
        bool bNew = ReadBool(o, "bNew");
        int nType = ReadInt(o, "nType", 1);

        var state = LoadState(player);
        state.Pending.TryGetValue(guid, out var pending);

        var part = player.Items.FirstOrDefault(i => i.Guid == guid && i.Genre == PartsGenre);
        if (part is not null && bNew && pending is not null && pending.NewAffixId > 0)
        {
            // 保留新词条：写进零件的属性槽（Attrs 恒 6 槽，见 Item.Attrs 的红线注释）
            ApplyAffix(part, pending.NewAffixId);
            GameNotify.PushItems(session, new[] { part });
        }

        if (pending is not null)
        {
            state.Pending.Remove(guid);
            SaveState(player, state);
        }

        Log.Info($"[session {session.Id}] WeaponParts_Save(352) 零件 {guid} nType={nType} " +
                 $"bNew={bNew} ⇒ {(bNew ? $"保留新词条 id={pending?.NewAffixId ?? 0}" : "保留旧词条")}" +
                 (pending is null ? "（★ 没有待确认记录，只回显）" : "") + "；原样回显");

        // ★ 原样回显（客户端 UI_WeaponRefitChoose:Response(bNew) / UI_WeaponSkillSelected:Response()）
        _ctx.LuaCalls.CallClient(session, cmd, o is null ? new JsonObject() : CloneObject(o));
        return Task.CompletedTask;
    }

    /// <summary>
    /// 把词条写进零件。
    ///
    /// ★★ <see cref="ItemAttr"/> 的槽位语义（<c>Player.cs:429</c> 的注释）：
    /// **下标 0..2 是属性类型**（1 攻击 / 2 暴击 / 3 攻速 / 4 破防）、
    /// **下标 3..5 才是新词条槽位**。所以覆写只能写 3..5，写 0..2 会把基础属性冲掉。
    ///
    /// ★ <c>Attrs</c> 下发前会被 <c>NormalizedAttrs()</c> 补齐到 6 个
    ///   （少了客户端抛 <c>ArgumentOutOfRangeException</c> 且该包后续物品全丢，
    ///    AGENTS.md §5 的红线）—— 这里先把长度补到 6 再写。
    /// </summary>
    internal void ApplyAffix(Item part, int affixId)
    {
        var cfg = _table.Affix(affixId);
        // 点数编码：低 16 位原生 / 高 16 位充能（OriginalLogic:GetPointVal）
        int org = cfg is not null && cfg.LevelPoints.Count > 0 && cfg.LevelPoints[0].Count > 0
            ? cfg.LevelPoints[0][0]
            : 1;
        int point = PartsRefitTable.MakePoint(org, 0);

        // 补齐到 6 槽（下标 3..5 是词条槽）
        while (part.Attrs.Count < Item.AttrSlotCount)
            part.Attrs.Add(new ItemAttr());

        // 在词条槽里找空位；都占了就覆盖最后一个
        int idx = -1;
        for (int i = AffixSlotStart; i < Item.AttrSlotCount; i++)
        {
            if (part.Attrs[i].AType == 0) { idx = i; break; }
        }
        if (idx < 0)
            idx = Item.AttrSlotCount - 1;

        part.Attrs[idx].AType = (uint)affixId;
        part.Attrs[idx].AValue = point;
    }

    /// <summary>
    /// 新词条槽的起始下标。<see cref="ItemAttr"/> 下标 0..2 是基础属性、**3..5 才是词条**
    /// （<c>Player.cs:429</c>）。
    /// </summary>
    internal const int AffixSlotStart = 3;

    // ================================================================ 内部

    /// <summary>零件类型（1 枪管 / 2 弹匣 / 3 瞄准仪 / 4 握把）来自 <c>Detail</c>。</summary>
    internal static int PartTypeOf(Item part) => (int)part.Detail;

    private static string OpName(int opType) => opType switch
    {
        PartsRefitTable.OpOverwrite => "覆写词条",
        PartsRefitTable.OpActive => "激活",
        PartsRefitTable.OpCharge => "充能",
        PartsRefitTable.OpRewrite => "属性改写",
        _ => $"未知({opType})",
    };

    private static JsonObject CloneObject(JsonObject src)
    {
        var dst = new JsonObject();
        foreach (var kv in src)
            dst[kv.Key] = kv.Value?.DeepClone();
        return dst;
    }

    private static int ReadInt(JsonObject? o, string key, int fallback = 0)
    {
        if (o?[key] is not JsonValue v)
            return fallback;
        if (v.TryGetValue<int>(out int n))
            return n;
        return v.TryGetValue<string>(out var s) && int.TryParse(s, out int m) ? m : fallback;
    }

    private static uint ReadUInt(JsonObject? o, string key)
    {
        int n = ReadInt(o, key);
        return n > 0 ? (uint)n : 0u;
    }

    private static bool ReadBool(JsonObject? o, string key)
    {
        if (o?[key] is not JsonValue v)
            return false;
        if (v.TryGetValue<bool>(out bool b))
            return b;
        // Lua 侧也可能编成 0/1
        return v.TryGetValue<int>(out int n) && n != 0;
    }

    private static List<uint> ReadGuidList(JsonObject? o, string key)
    {
        var result = new List<uint>();
        if (o?[key] is not JsonArray arr)
            return result;
        foreach (var el in arr)
        {
            if (el is JsonValue v && v.TryGetValue<int>(out int n) && n > 0)
                result.Add((uint)n);
        }
        return result;
    }

    // ---------------- 存档 ----------------

    internal PartsRefitState LoadState(Player player) => LoadStateStatic(player);

    internal static PartsRefitState LoadStateStatic(Player player)
    {
        player.Extra.TryGetValue(ExtraKey, out var node);
        return PartsRefitState.FromJson(node as JsonObject);
    }

    private void SaveState(Player player, PartsRefitState state)
    {
        player.Extra[ExtraKey] = state.ToJson();
        _ctx.Store.Save(player);
    }
}

/// <summary>一条「待二选一」的新词条记录（351 产生、352 消费）。</summary>
internal sealed class PartsRefitPending
{
    /// <summary>目标零件 guid。</summary>
    public uint PartGuid { get; set; }

    /// <summary>351 随机出的新词条 id（<c>PartsAffix.Type</c>）。</summary>
    public int NewAffixId { get; set; }

    /// <summary>产生它的操作类型（351 的 nType）。</summary>
    public int OpType { get; set; }

    public JsonObject ToJson() => new()
    {
        ["guid"] = PartGuid,
        ["affix"] = NewAffixId,
        ["op"] = OpType,
    };

    public static PartsRefitPending FromJson(JsonObject o) => new()
    {
        PartGuid = (uint)Math.Max(0, ReadInt(o["guid"])),
        NewAffixId = ReadInt(o["affix"]),
        OpType = ReadInt(o["op"]),
    };

    /// <summary>
    /// 读整数。
    /// ★★ 必须同时试 <c>int</c> 与 <c>uint</c>/<c>long</c>：
    /// <see cref="ToJson"/> 里 <c>PartGuid</c> 是 <c>uint</c>，
    /// 而 <c>JsonValue.TryGetValue&lt;int&gt;</c> 对一个以 <c>uint</c> 装箱的值会**返回 false**
    /// ⇒ 只试 int 会让 <c>PartGuid</c> 恒为 0、整条 Pending 记录被丢弃
    /// （症状：351 明明成功了，352 却查不到待确认词条）。
    /// </summary>
    private static int ReadInt(JsonNode? n)
    {
        if (n is not JsonValue v)
            return 0;
        if (v.TryGetValue<int>(out int x))
            return x;
        if (v.TryGetValue<uint>(out uint u))
            return (int)Math.Min(u, int.MaxValue);
        if (v.TryGetValue<long>(out long l))
            return (int)Math.Clamp(l, 0, int.MaxValue);
        return 0;
    }
}

/// <summary>零件覆写域的玩家存档（<c>Player.Extra["partsrefit"]</c>）。</summary>
internal sealed class PartsRefitState
{
    /// <summary>零件 guid → 待确认的新词条。</summary>
    public Dictionary<uint, PartsRefitPending> Pending { get; } = new();

    public JsonObject ToJson()
    {
        var arr = new JsonArray();
        foreach (var p in Pending.Values.OrderBy(p => p.PartGuid))
            arr.Add(p.ToJson());
        return new JsonObject { ["pending"] = arr };
    }

    public static PartsRefitState FromJson(JsonObject? o)
    {
        var s = new PartsRefitState();
        if (o?["pending"] is JsonArray arr)
        {
            foreach (var el in arr)
            {
                if (el is not JsonObject po)
                    continue;
                var p = PartsRefitPending.FromJson(po);
                if (p.PartGuid > 0)
                    s.Pending[p.PartGuid] = p;
            }
        }
        return s;
    }
}
