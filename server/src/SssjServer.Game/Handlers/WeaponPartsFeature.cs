using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// 武器零件（genre 4 / `OriginalItem`）的装载与方案改名 —— LuaCall **350** / **348**。
/// 取证 `work/weapon-genre-forensics.md` §C.6 / §D.3、台账 `docs/05` §30。
///
/// <para><b>0. ★★★ 为什么先做零件、不做符文（30 `Rune_Equip`）</b></para>
/// 「装备关系」这件事在两者上**下发通道的有无完全相反**：
/// <list type="table">
///   <item><term>零件（本域）</term><description>✅ 有**双向**通道 ——
///     武器侧 <c>WeaponItem.{Barrel,Ammo,Sight,Grip}Guid</c>（走 <c>ItemInfo.weaponinlay</c>）
///     + 零件侧 <c>OriginalItem.ownguid</c>（走 <c>ItemInfo.ownguid</c>）。</description></item>
///   <item><term>符文（genre 3）</term><description>❌ **没有通道** ——
///     关系在 <c>RuneItem.weaponGuid</c>(+0x48)，而 <c>AddItem</c>（`0x1D9A82C`）
///     **既不清也不写它**（§C.4）⇒ 服务端下发的符文永远是「散装」的。
///     ⇒ 30 `Rune_Equip` 现阶段做不出可见效果，见 <see cref="OnRuneEquipNote"/> 的论证。</description></item>
/// </list>
///
/// <para><b>1. ★★★ 一条命名误解（照抄取证文档时最容易错的）</b></para>
/// <c>gsproto.WeaponRune</c> 这个消息名 + 字段名 <c>weaponinlay</c>（「武器镶嵌」）
/// 会让人以为装的是 4 个**符文**。**不是，装的是 4 个零件**（§C.6 三条独立证据）：
/// 客户端 `UI_WeaponStrong.lua:2241..2244` 拿这 4 个 guid 调的是
/// <c>GetOriginal</c>（零件）而不是 <c>GetRune</c>；
/// `PartsOverwrite.txt` 表头写明「零件类型 1-4 对应枪管 弹匣 瞄准仪 握把」，
/// 正好对上 <c>barrel</c>/<c>ammo</c>/<c>sight</c>/<c>grip</c>。
///
/// <para><b>2. ★★ 槽位由零件的 <c>Detail</c> 决定，不是玩家选的</b></para>
/// <c>WeaponParts.txt</c> 表头第 2 行原文：<c>细分D(零件位 1枪管2弹匣3瞄准仪4握把)</c>
/// ⇒ 一个零件只能装进它自己那个槽。上行**没有槽位字段**，服务端必须自己按 D 推。
///
/// <para><b>3. ★★★ 必须两边同时写、保持一致</b></para>
/// <c>weaponinlay</c>（武器记「装了哪 4 个」）与 <c>ownguid</c>（零件记「装在哪把上」）
/// 是**双向冗余**。只写一边 ⇒ 界面一边显示装着、一边显示空闲
/// （取证 `UI_WeaponSkillSelected.lua:50` 用零件的 <c>OwnGuid</c> 反查武器、
/// `UI_WeaponStrong.lua:2095` 拿它与武器 <c>Guid</c> 直接比较）。
/// ★ 空槽哨兵是 **0**，不是 -1（`OriginalLogic.lua:114/137` 都是 <c>&gt; 0</c> 判有效）。
///
/// <para><b>4. ★★ 回包只回显、数值靠 pf 1031 先到</b></para>
/// 350 的客户端消费点 `UI_WeaponStrong.lua:2355` <c>RefreshRefigPnl(tbParam)</c>
/// **只用 <c>tbParam.nPartsGuid</c>**，其余全部 <c>self.tbSelectItem:RefreshLevelInfo()</c>
/// 从本地缓存重读 ⇒ 与角色卡/武器同一个模式：**先推 pf 1031、再回包**。
/// 原服是 <c>LuaCall:CallClient(sCmd, tbParam)</c> **原样回显**（`LuaCall.lua:3596`）。
///
/// <para><b>5. ★ 348 改名的回包是「整张字典」不是单条</b></para>
/// `OriginalLogic.lua:127..130` <c>self.tbPlanName = tbParam</c> —— **全量替换**
/// ⇒ 必须回**所有**已命名方案，只回本次改的那条会把其余名字清空。
/// 方案名存 <see cref="Player.Extra"/> 的 <c>partsPlanName</c> 键。
/// </summary>
public sealed class WeaponPartsFeature : IGameFeature
{
    /// <summary>LuaCall 348 零件方案改名（上行 <c>{id, name}</c>）。</summary>
    public const int CmdChangePlanName = 348;

    /// <summary>LuaCall 350 零件装载/卸载（上行 <c>{nWeaponGuid, nPartsGuid, bLoad}</c>）。</summary>
    public const int CmdLoad = 350;

    /// <summary>零件 genre。</summary>
    public const uint PartsGenre = 4;

    /// <summary>武器 genre。</summary>
    public const uint WeaponGenre = WeaponTable.WeaponGenre;

    /// <summary>零件位：枪管（<c>Detail == 1</c>）。</summary>
    public const uint SlotBarrel = 1;

    /// <summary>零件位：弹匣。</summary>
    public const uint SlotAmmo = 2;

    /// <summary>零件位：瞄准仪。</summary>
    public const uint SlotSight = 3;

    /// <summary>零件位：握把。</summary>
    public const uint SlotGrip = 4;

    /// <summary>方案名在 <see cref="Player.Extra"/> 里的键。</summary>
    public const string PlanNameKey = "partsPlanName";

    /// <summary>方案数上限（4 页 × 4 个，`UI_WeaponStrong.lua:2456` 的 <c>(idx-1)*4 + sel</c>）。</summary>
    public const int MaxPlanId = 16;

    /// <summary>方案名最大长度（防客户端塞超长串进存档）。</summary>
    public const int MaxPlanNameLength = 32;

    private GameFeatureContext _ctx = null!;

    public string Name => "武器零件装载/方案改名（LuaCall 350 / 348）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        ctx.MapLuaCall(CmdLoad, OnLoad);
        ctx.MapLuaCall(CmdChangePlanName, OnChangePlanName);
    }

    // ================= 350 装载 / 卸载 =================

    /// <summary>
    /// 装上或卸下一个零件。上行 <c>{nWeaponGuid, nPartsGuid, bLoad}</c>
    /// （`UI_WeaponStrong.lua:2352`；`:2598` 是「一键套用方案」，会连发 4 条）。
    ///
    /// 处理顺序（每一步都有理由，别调换）：
    /// <list type="number">
    ///   <item>校验武器与零件都是玩家的、genre 正确；</item>
    ///   <item>按零件 <c>Detail</c> 推槽位（类注释 §2）；</item>
    ///   <item>装上时**先把该槽原有的零件卸掉**（它的 <c>ownguid</c> 清 0），
    ///     否则会出现「两个零件都说自己装在同一个槽」；</item>
    ///   <item>★ 还要把**这个零件从别的武器上摘下来**（一个零件只能装一处）；</item>
    ///   <item>两边同时写（§3）→ 推 pf 1031（武器 + 涉及的零件）→ 原样回显。</item>
    /// </list>
    /// </summary>
    private Task OnLoad(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var obj = param as JsonObject;
        uint weaponGuid = (uint)ReadInt(obj?["nWeaponGuid"]);
        uint partsGuid = (uint)ReadInt(obj?["nPartsGuid"]);
        bool load = ReadBool(obj?["bLoad"]);

        var weapon = Find(player, weaponGuid, WeaponGenre);
        var parts = Find(player, partsGuid, PartsGenre);
        if (weapon is null || parts is null)
        {
            // ★ LuaCall 数字族：失败带 nError（非 0）。客户端 RefreshRefigPnl 没有 nError 分支
            // ⇒ 表现为「点了没反应」，但仍要回包（原服成功/失败都回）
            Fail(session, cmd);
            Log.Warn($"[session {session.Id}] LuaCall 350 零件装载：" +
                     $"武器 guid={weaponGuid}({(weapon is null ? "无效" : "ok")}) " +
                     $"零件 guid={partsGuid}({(parts is null ? "无效" : "ok")})");
            return Task.CompletedTask;
        }

        uint slot = parts.Detail;
        if (slot is < SlotBarrel or > SlotGrip)
        {
            Fail(session, cmd);
            Log.Warn($"[session {session.Id}] LuaCall 350 零件装载：零件 {partsGuid} 的 Detail={slot} " +
                     $"不是合法零件位（1..4）");
            return Task.CompletedTask;
        }

        weapon.Inlay ??= new WeaponInlay();
        var changed = new List<Item> { weapon, parts };

        if (load)
        {
            // ③ 该槽原有的零件先卸下
            uint old = GetSlot(weapon.Inlay, slot);
            if (old != 0 && old != partsGuid && Find(player, old, PartsGenre) is { } oldParts)
            {
                oldParts.OwnGuid = 0;
                changed.Add(oldParts);
            }

            // ④ 这个零件如果装在别的武器上，从那边摘掉（含 inlay 侧）
            if (parts.OwnGuid != 0 && parts.OwnGuid != weaponGuid
                && Find(player, parts.OwnGuid, WeaponGenre) is { } prevWeapon)
            {
                if (prevWeapon.Inlay is not null && GetSlot(prevWeapon.Inlay, slot) == partsGuid)
                {
                    SetSlot(prevWeapon.Inlay, slot, 0);
                    changed.Add(prevWeapon);
                }
            }

            SetSlot(weapon.Inlay, slot, partsGuid);
            parts.OwnGuid = weaponGuid;
        }
        else
        {
            // 卸下：两边都清。★ 只在「确实装在这把武器的这个槽上」时才动，避免误清别人的
            if (GetSlot(weapon.Inlay, slot) == partsGuid)
                SetSlot(weapon.Inlay, slot, 0);
            if (parts.OwnGuid == weaponGuid)
                parts.OwnGuid = 0;
        }

        _ctx.Store.Save(player);

        // ★ 先推 pf 1031（客户端 RefreshRefigPnl 从本地缓存重读，§4），再回包
        GameNotify.PushItems(session, changed);
        // ★ 原样回显（`LuaCall.lua:3596` CallClient(sCmd, tbParam)）；成功**绝不带 nError**
        _ctx.LuaCalls.CallClient(session, cmd, new JsonObject
        {
            ["nWeaponGuid"] = weaponGuid,
            ["nPartsGuid"] = partsGuid,
            ["bLoad"] = load,
        });

        Log.Info($"[session {session.Id}] LuaCall 350 零件{(load ? "装载" : "卸下")}：" +
                 $"武器 {weaponGuid} 槽{slot}({SlotName(slot)}) ←→ 零件 {partsGuid}" +
                 $"（现槽位 barrel={weapon.Inlay.Barrel} ammo={weapon.Inlay.Ammo} " +
                 $"sight={weapon.Inlay.Sight} grip={weapon.Inlay.Grip}）");
        return Task.CompletedTask;
    }

    // ================= 348 方案改名 =================

    /// <summary>
    /// 零件方案改名。上行 <c>{id, name}</c>，其中 <c>id = (页-1)*4 + 序号</c>
    /// （`UI_WeaponStrong.lua:2456`）。
    ///
    /// ★★ 回包必须是**整张字典**（`OriginalLogic.lua:129` <c>self.tbPlanName = tbParam</c>
    /// 是全量替换）⇒ 只回本次改的那条会把其余名字清空。
    /// ★ 客户端判据是 <c>if tbParam and CountTB(tbParam) ~= 0</c>（`:128`）
    /// ⇒ 空字典会被忽略（保留旧值），所以「删名字」这个操作在协议上做不到，
    /// 只能改成别的名字 —— 与原服行为一致，不是我们的限制。
    /// </summary>
    private Task OnChangePlanName(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        var obj = param as JsonObject;
        int id = ReadInt(obj?["id"]);
        string name = obj?["name"]?.ToString() ?? "";

        if (id < 1 || id > MaxPlanId)
        {
            Fail(session, cmd);
            Log.Warn($"[session {session.Id}] LuaCall 348 方案改名：id={id} 越界（1..{MaxPlanId}）");
            return Task.CompletedTask;
        }
        if (name.Length > MaxPlanNameLength)
            name = name[..MaxPlanNameLength];

        var names = LoadPlanNames(player);
        if (name.Length == 0)
            names.Remove(id.ToString());
        else
            names[id.ToString()] = name;
        SavePlanNames(player, names);
        _ctx.Store.Save(player);

        // ★ 回整张字典（键是方案 id 的字符串形式，与客户端 tbPlanName 的用法一致）
        var reply = new JsonObject();
        foreach (var kv in names.OrderBy(k => int.TryParse(k.Key, out var n) ? n : 0))
            reply[kv.Key] = kv.Value;

        _ctx.LuaCalls.CallClient(session, cmd, reply);
        Log.Info($"[session {session.Id}] LuaCall 348 方案改名：方案 {id} → \"{name}\"" +
                 $"（现共 {names.Count} 个已命名方案）");
        return Task.CompletedTask;
    }

    /// <summary>从 <see cref="Player.Extra"/> 读方案名字典（缺省空）。</summary>
    internal static Dictionary<string, string> LoadPlanNames(Player player)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!player.Extra.TryGetValue(PlanNameKey, out var node) || node is not JsonObject obj)
            return result;
        foreach (var kv in obj)
        {
            if (kv.Value is not null)
                result[kv.Key] = kv.Value.ToString();
        }
        return result;
    }

    private static void SavePlanNames(Player player, Dictionary<string, string> names)
    {
        var obj = new JsonObject();
        foreach (var kv in names)
            obj[kv.Key] = kv.Value;
        player.Extra[PlanNameKey] = obj;
    }

    // ================= 30 Rune_Equip 的论证（有意不实现）=================

    /// <summary>
    /// ★★ <b>符文装备（LuaCall 30 <c>Rune_Equip</c>）为什么现阶段不做</b>（论证，不是遗漏）。
    ///
    /// 「符文装在哪把武器上」这个关系存在 <c>RuneItem.weaponGuid</c>（<c>+0x48</c>），
    /// 而客户端把符文塞进背包的唯一入口 <c>AddItem</c>（RVA <c>0x1D9A82C</c>）的
    /// <c>genre == 3</c> 分支里 —— 取证 `work/weapon-genre-forensics.md` §C.2/§C.4 ——
    /// **既不清空也不写入该字段**。<c>SetEquipRune</c> 的 C# 调用者数为 **0**（只有 XLua 桥）。
    ///
    /// ⇒ 服务端**没有任何协议字段**能把「已装配」这个状态送到客户端：
    /// 即便我们在存档里记下关系、回包也回对了，客户端重登后 <c>weaponGuid</c> 恒为 0
    /// ⇒ 界面永远显示「未装配」。做了等于**只在服务端日志里好看**。
    ///
    /// ⇒ 正确的下一步不是写 handler，而是**先取证**：
    /// 找「符文装配状态」的真实同步通道（可能是某个专用 pf、或某个任务值位域）。
    /// 未取证之前，收到 30 只记日志（由 <c>C2SCall</c> 的统一告警覆盖），不回假成功。
    /// </summary>
    private static void OnRuneEquipNote() { }

    // ================= 工具 =================

    private static Item? Find(Player player, uint guid, uint genre)
    {
        if (guid == 0)
            return null;
        var item = player.Items.FirstOrDefault(i => i.Guid == guid);
        return item is not null && item.Genre == genre ? item : null;
    }

    private static uint GetSlot(WeaponInlay inlay, uint slot) => slot switch
    {
        SlotBarrel => inlay.Barrel,
        SlotAmmo => inlay.Ammo,
        SlotSight => inlay.Sight,
        SlotGrip => inlay.Grip,
        _ => 0,
    };

    private static void SetSlot(WeaponInlay inlay, uint slot, uint guid)
    {
        switch (slot)
        {
            case SlotBarrel: inlay.Barrel = guid; break;
            case SlotAmmo: inlay.Ammo = guid; break;
            case SlotSight: inlay.Sight = guid; break;
            case SlotGrip: inlay.Grip = guid; break;
        }
    }

    internal static string SlotName(uint slot) => slot switch
    {
        SlotBarrel => "枪管",
        SlotAmmo => "弹匣",
        SlotSight => "瞄准仪",
        SlotGrip => "握把",
        _ => "?",
    };

    /// <summary>失败回包：★ LuaCall 数字族 ⇒ 带 <c>nError</c>（非 0）。</summary>
    private void Fail(ClientSession session, int cmd)
        => _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["nError"] = 1 });

    private static int ReadInt(JsonNode? node, int fallback = 0)
        => node is null ? fallback : int.TryParse(node.ToString(), out var v) ? v : fallback;

    /// <summary>读 bool 上行。★ Lua 的 true/false 经 JSON 过来可能是 bool 也可能是 0/1。</summary>
    private static bool ReadBool(JsonNode? node)
    {
        if (node is null)
            return false;
        string s = node.ToString();
        return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
    }
}
