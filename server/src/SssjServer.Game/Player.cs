namespace SssjServer.Game;

/// <summary>
/// 玩家业务对象。一份实例对应用户会话（挂在 <c>ClientSession.Tag</c> 上），
/// 持久化字段与 <c>docs/03-login-flow.md</c> 中客户端 <c>PlayerNtf</c> 的存量对齐。
/// 仅存放玩家基础数据；NPC/关卡/编队等后续按 LuaCall 逐个补。
/// </summary>
public sealed class Player
{
    public Player(string account, ulong roleid)
    {
        Account = account;
        Roleid = roleid;
        RegisterTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>账号（本地 selftest 即客户端 PlayerPrefs 的 account）。</summary>
    public string Account { get; set; } = "";

    /// <summary>由账号确定的角色 id（见 <see cref="World.DeriveRoleId"/>）。</summary>
    public ulong Roleid { get; set; }

    /// <summary>角色名（未建号时默认与账号一致，可后续用 1006 改名）。</summary>
    public string Name { get; set; } = "";

    public int Level { get; set; } = 1;

    public int Exp { get; set; } = 0;

    public int Fightpower { get; set; } = 0;

    /// <summary>注册时间（unix 秒）。</summary>
    public ulong RegisterTime { get; set; }

    /// <summary>本次进入游戏的时间（unix 秒），即 PlayerNtf.starttime。</summary>
    public ulong StartTime { get; set; }

    /// <summary>服务器区（PlayerNtf.serverzone / TimeRsp.Serverzone）。</summary>
    public int ServerZone { get; set; } = 1;

    /// <summary>
    /// 体力自然恢复的计时基准（unix 秒）。每 6 分钟 1 点（<see cref="Config.PlayerExpTable.VigourRecoverSeconds"/>，
    /// 取证 Notification.lua:164），由 <c>World.RecoverVigour</c> 维护。
    /// </summary>
    public ulong VigourTime { get; set; }

    /// <summary>头像框 id（PlayerNtf.headframe）。</summary>
    public int HeadFrame { get; set; } = 0;

    /// <summary>聊天频道 id（PlayerNtf.chatchannelid）。</summary>
    public uint ChatChannelId { get; set; } = 0;

    /// <summary>社团 id（PlayerNtf.clubid），0 = 未加入。</summary>
    public ulong ClubId { get; set; } = 0;

    /// <summary>货币表：货币 id → 数量（PlayerNtf.moneyinfo，repeated MoneyInfo{id,curnum}）。</summary>
    public Dictionary<int, int> Money { get; } = new();

    /// <summary>
    /// 关卡进度：mapId（<see cref="MapList.MapId"/> 口径）→ star 位域
    /// （bit0..2 三星、bit3..31 通关次数，见 <see cref="MapList"/>）。
    /// 通道：登录随 <c>PlayerNtf.levellist</c> 全量下发；变更用 pf 1034 <c>ChapterUpdateNtf</c> 增量推。
    /// </summary>
    public Dictionary<uint, int> Levels { get; } = new();

    /// <summary>
    /// 功能模块自有状态（key 建议用功能名，如 "cafe" / "friend"，value 任意 JSON）。
    ///
    /// 存在的理由：每个 <see cref="IGameFeature"/> 都可能要存一点自己的数据，
    /// 若都往 Player/PlayerDto 里加字段，多人并行开发必然改到同一文件。
    /// 这个袋子随存档一起 round-trip（System.Text.Json 原生支持 JsonNode），
    /// 功能模块自己负责其中的结构与版本。
    /// </summary>
    public Dictionary<string, System.Text.Json.Nodes.JsonNode?> Extra { get; } = new(StringComparer.Ordinal);

    /// <summary>已获得角色卡（PlayerNtf.girllist）。</summary>
    public List<Girl> Girls { get; } = new();

    /// <summary>
    /// 背包/物品清单（PlayerNtf 由卡片 girl 映射 + pf=1104 ItemNtf 全量下发）。
    /// 卡片本身是 inventory item：genre=1（Card），detail=少女 id，particular=变体，level=阶。
    /// 每项的 <see cref="Item.Guid"/> 是服务端分配的实例 id（单调递增，从 1 起）。
    /// </summary>
    public List<Item> Items { get; } = new();

    /// <summary>下一个可用的物品实例 guid（单调递增，不重用）。新建号从 1 开始。</summary>
    public uint NextGuid { get; set; } = 1;

    /// <summary>
    /// 编队（PlayerNtf.formationlist → FormationInfo）。每项 id 不同（本实现固定 1），
    /// fight-card 槽的 maincardguid/usedcardguid 引用 <see cref="Item.Guid"/>（genre==1 的卡片实例）。
    /// </summary>
    public List<Formation> Formations { get; } = new();

    /// <summary>分配的下一实例 guid（当前 <see cref="NextGuid"/>，然后自增），保证唯一且单调。</summary>
    public uint AllocateGuid() => NextGuid++;

    /// <summary>取背包中指定 guid 的物品；不存在返回 null。</summary>
    public Item? GetItem(uint guid) => Items.FirstOrDefault(i => i.Guid == guid);

    /// <summary>新增一件物品并自动分配实例 guid、记录创建时间（unix 秒）。</summary>
    public Item AddItem(uint genre, uint detail, uint particular, uint level, uint count)
    {
        var item = new Item
        {
            Guid = AllocateGuid(),
            Genre = genre,
            Detail = detail,
            Particular = particular,
            Level = level,
            Count = count,
            CreateTime = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };
        Items.Add(item);
        return item;
    }

    /// <summary>
    /// 任务值表：组合 id = (group &lt;&lt; 16) | taskId → 值。
    /// 组合方式来自 Game.Player.GetTask（RVA 0x18448F0）。这是客户端判定
    /// 「新手关是否完成 / 能否进主城」的唯一依据（见 PlayerLogic.IsFirstLevelComplete）。
    /// </summary>
    public Dictionary<uint, int> Tasks { get; } = new();

    public Girl? GetGirl(uint girlId) => Girls.FirstOrDefault(g => g.Girlid == girlId);

    public void AddMoney(int id, int amount)
    {
        Money.TryGetValue(id, out var cur);
        // gcg2: clamp to 0..int.MaxValue; a plain int add wrapped to a negative balance
        Money[id] = (int)Math.Clamp((long)cur + amount, 0L, int.MaxValue);
        Handlers.EventMissionProgress.MoneyChanged(this, id, amount);
    }

    /// <summary>读某种货币的当前值；未持有返回 0。</summary>
    public int GetMoney(int id) => Money.TryGetValue(id, out var v) ? v : 0;

    /// <summary>写某种货币（覆盖，负值夹到 0）。</summary>
    public void SetMoney(int id, int value)
    {
        int next = value < 0 ? 0 : value;
        Handlers.EventMissionProgress.MoneyChanged(this, id, next - GetMoney(id));
        Money[id] = next;
    }

    /// <summary>体力（<c>MoneyLogic.eMoneyType_Vigour == 1</c>）。</summary>
    public int Vigour => GetMoney(MoneyType.Vigour);

    /// <summary>
    /// 扣体力。不足时返回 false 且不改动（服务端拒绝进关）。
    /// </summary>
    public bool TrySpendVigour(int amount)
    {
        if (amount <= 0)
            return true;
        int cur = GetMoney(MoneyType.Vigour);
        if (cur < amount)
            return false;
        SetMoney(MoneyType.Vigour, cur - amount);
        // Event missions ("spend N stamina") read this. Refunds leave it alone: the stamina was spent at the time.
        Extra[Handlers.EventMissionProgress.VigourSpentKey] = (Extra.GetValueOrDefault(Handlers.EventMissionProgress.VigourSpentKey)?.GetValue<int>() ?? 0) + amount;
        return true;
    }

    /// <summary>读关卡 star 位域；没打过返回 0（客户端 GetLevel 缺失时返回 -1，语义等价于未通关）。</summary>
    public int GetLevelStar(uint mapId) => Levels.TryGetValue(mapId, out var v) ? v : 0;

    /// <summary>
    /// 记一次通关：三星按位或累积（历史最好成绩），通关次数 +1，返回新的 star 位域。
    /// 语义依据 <c>LevelManager.lua:278 GetLevelState</c> 的位域拆解与
    /// <c>Game.MapList.ReflashLevelList</c> 的整值覆盖。
    /// </summary>
    public int RecordLevelPass(uint mapId, int starMask)
    {
        int old = GetLevelStar(mapId);
        int stars = MapList.Stars(old) | MapList.Stars(starMask);
        int passCount = MapList.PassCount(old) + 1;
        int composed = MapList.Compose(stars, passCount);
        Levels[mapId] = composed;
        return composed;
    }

    /// <summary>把 (group, taskId) 组合成存储键：(group &lt;&lt; 16) | taskId。</summary>
    public static uint ComposeTaskId(int group, int id) => unchecked(((uint)group << 16) | (uint)(id & 0xFFFF));

    /// <summary>把存储键拆回高位组的组号。</summary>
    public static int TaskGroupOf(uint taskId) => (int)(taskId >> 16);

    /// <summary>把存储键拆回低 16 位的任务 id。</summary>
    public static int TaskIdOf(uint taskId) => (int)(taskId & 0xFFFF);

    /// <summary>读取任务值；未设置返回 0。</summary>
    public int GetTask(int group, int id)
        => Tasks.TryGetValue(ComposeTaskId(group, id), out var v) ? v : 0;

    /// <summary>写入任务值（覆盖）。</summary>
    public void SetTask(int group, int id, int value)
        => Tasks[ComposeTaskId(group, id)] = value;
}

/// <summary>
/// 一张角色卡（映射 <c>GirlInfo{girlid,level,exp,modelid,moodvalue,vigor,flag}</c>）。
/// </summary>
public sealed class Girl
{
    public uint Girlid { get; set; }
    public uint Level { get; set; } = 1;
    public uint Exp { get; set; } = 0;
    public uint Modelid { get; set; } = 0;
    public uint Moodvalue { get; set; } = 100;
    public uint Vigor { get; set; } = 0;
    public uint Flag { get; set; } = 0;
}

/// <summary>
/// 背包/物品实例（映射 <c>ItemInfo</c>，字段号见 docs/proto-schema.json）。
/// 服务端只填充有证据的字段：<see cref="Guid"/>/<see cref="Genre"/>/<see cref="Detail"/>/
/// <see cref="Particular"/>/<see cref="Level"/>/<see cref="Count"/>/<see cref="CreateTime"/>；
/// 其余（enhancelevel/enhanceexp/breaklevel/…）语义未取证，一律不设。
/// </summary>
public sealed class Item
{
    /// <summary>服务端分配的实例 id（<c>ItemInfo.guid</c>），也作为编队槽的引用。</summary>
    public uint Guid { get; set; }

    /// <summary>大类（<c>ItemInfo.genre</c>）；卡片为 1（emGenreType.Card）。</summary>
    public uint Genre { get; set; }

    /// <summary>少女 id（<c>ItemInfo.detail</c>）。</summary>
    public uint Detail { get; set; }

    /// <summary>变体（<c>ItemInfo.particular</c>）。</summary>
    public uint Particular { get; set; }

    /// <summary>阶/稀有度（<c>ItemInfo.level</c>）。</summary>
    public uint Level { get; set; }

    /// <summary>数量（<c>ItemInfo.count</c>）。</summary>
    public uint Count { get; set; }

    /// <summary>创建时间（unix 秒，<c>ItemInfo.createtime</c>）。</summary>
    public uint CreateTime { get; set; }

    /// <summary>
    /// 强化等级（<c>ItemInfo.enhancelevel</c>，字段 8）—— **这才是界面上那个 "LV.x"**。
    /// 取证：<c>FormationCommon.lua:621</c> <c>tbInfo.Level = tbItemInfo:GetEnhanceLevel()</c>，
    /// 以及 <c>UI_ChoosePaster.lua:642</c> / <c>UI_DecomposeTip.lua:470</c> 的
    /// <c>"LV." .. tbInfo.EnhanceLevel</c>。新卡必须给 1，给 0 会显示 LV.0。
    /// </summary>
    public uint EnhanceLevel { get; set; } = 1;

    /// <summary>强化经验（<c>ItemInfo.enhanceexp</c>，字段 9）。</summary>
    public uint EnhanceExp { get; set; }

    /// <summary>突破等级（<c>ItemInfo.breaklevel</c>，字段 10）。</summary>
    public uint BreakLevel { get; set; }

    /// <summary>
    /// 同步率（<c>ItemInfo.syncrate</c>，字段 18）—— 角色卡「改造」体系的数值。
    ///
    /// <para><b>★★ 必须每条 pf 1031/1104 都带上，哪怕是 0</b></para>
    /// 客户端 <c>EquipManager.AddCharacterCard</c>（RVA <c>0x1D95368</c>）在
    /// <c>0x1D957BC get_syncrate</c> → <c>0x1D957DC set_SyncRate</c> 是**无条件**读写
    /// （没有 <c>has_</c> 判断）⇒ 漏了等于**每次推背包都把同步率清零**。
    /// 与 <c>docs/05</c> §0.3 那条「无条件先 Clear」红线同族，只不过那张表列的是
    /// 武器/符文(genre 2/3/4) 的字段，这两个是**角色卡(genre 1)** 的。
    /// </summary>
    public uint SyncRate { get; set; }

    /// <summary>
    /// 改造序号（<c>ItemInfo.refitnum</c>，字段 19）—— 对应上行
    /// <c>Card_Refit{guid, refitNum}</c> 里那个值。
    /// ★ 同 <see cref="SyncRate"/>：<c>AddCharacterCard</c> 在 <c>0x1D957F8 get_refitnum</c>
    /// 处**无条件**读，漏了会被清零。
    /// 改造功能本身（LuaCall 8）尚未实现，但字段必须先能**保真往返**，
    /// 否则将来 GM 给卡塞了同步率也会被任意一次背包推送抹掉。
    /// </summary>
    public uint RefitNum { get; set; }

    /// <summary>
    /// 锁定标记（<c>ItemInfo.lockon</c>，字段 13 → 客户端 <c>BaseItem.set_LockOn</c> RVA 0x1D9AAC8）。
    /// &gt;0 表示玩家手动锁定，分解列表拒选（<c>UI_Warehouse.lua:1293</c>，客户端错误码 20015）。
    /// 上行通道是字符串命令 <c>LockItem{nGuid,nLockOn}</c>（无回包，见 <see cref="Handlers.BagFeature"/>）。
    /// </summary>
    public uint LockOn { get; set; }

    /// <summary>
    /// 位标记（<c>ItemInfo.flag</c>，字段 14 → <c>BaseItem.set_Flag</c> RVA 0x1D9ABB0）：
    /// <list type="bullet">
    /// <item>bit0 = <c>bNew</c> 背包红点（<c>get_bNew</c> RVA 0x174ED1C：<c>Flag &amp; 1</c>）</item>
    /// <item>bit1 = <c>bStoryNew</c> 的**反码**（<c>get_bStoryNew</c> RVA 0x174EEB0：
    ///   <c>LoadBits(Flag,1,1) == 0</c> → 即 bit1=1 表示「剧情已读」）</item>
    /// </list>
    /// 所以 <c>Flag == 0</c> 的角色卡会显示「剧情未读」红点，客户端点开剧情后会发
    /// <c>ItemFlushFlag{nCmd=3}</c> 让服务端置上 bit1。
    /// </summary>
    public uint Flag { get; set; }

    /// <summary>bit0：背包「新道具」红点。</summary>
    public const uint FlagNewBit = 1 << 0;

    /// <summary>bit1：剧情已读（注意是「已读」为 1，客户端取反后当 bStoryNew 用）。</summary>
    public const uint FlagStoryReadBit = 1 << 1;

    // ================= 装备类（genre 2 武器 / 3 符文 / 4 零件）专属字段 =================
    // ★★ 这一组就是 docs/05 §0.3 那条「无条件先 Clear」红线所指的字段。
    //    完整取证见 work/weapon-genre-forensics.md 与 work/weapon-mainsession.md。
    //    核心机制（主会话已独立反汇编复核）：客户端 AddWeapon(0x1D96FEC) /
    //    AddOriginal(0x1D99094) / AddItem(0x1D9A82C) 在填回之前**无条件**清空目标数组，
    //    然后只从 ItemInfo 的这些字段填回 ⇒ 服务端每次下发都必须带**全量真实状态**。
    //    ⇒ 安全性判据不是「必须发」，而是「**发的必须与当前状态一致**」：
    //       新造的、无符文无技能的装备发空值与不发等价（AddWeapon 的
    //       has_weaponinlay 为假分支自己就把四槽写 0，见 0x1D979F8）。

    /// <summary>
    /// 技能列表（<c>ItemInfo.skillinfo</c>，字段 11，repeated <c>MiniSkill{skillid,skilllevel,skilltype}</c>）。
    ///
    /// <para><b>★ 同一个字段在三种 genre 上语义完全不同</b>（`skilltype` 的取值集合不同）：</para>
    /// <list type="bullet">
    /// <item><b>武器(2)</b>：<c>1</c> = 主技能（只用 <c>skilllevel</c> → <c>set_MainSkillLevel</c> 0x191109C）、
    ///   <c>2</c> = 被动技能（写 <c>PassiveSkill[]</c>/<c>PassiveSkillLevel[]</c>）、
    ///   <c>4</c> = 校准技能（写 <c>RecastSkill[]</c>/<c>RecastSkillLevel[]</c>）。
    ///   ★★ 武器用**数组下标**赋值且有越界硬检查（<c>0x1D976D0</c>/<c>0x1D977D4</c>）⇒
    ///   同类型技能条数不能超过客户端分配的数组容量，否则抛 <c>IndexOutOfRangeException</c>。</item>
    /// <item><b>符文(3)</b>：认 <c>2</c> 与 <c>6</c>，都 <c>List.Add</c> 进 <c>SubSkillID</c>/<c>SubSkillLevel</c>
    ///   （有 Count 保护、自动扩容，空数组安全）；额外地 <c>skilltype == 2</c> 会调
    ///   <c>RuneItem.UseRuneSkill</c>（0x1BA4050）。</item>
    /// <item><b>零件(4)</b>：认 <c>7</c>/<c>8</c>/<c>9</c>。</item>
    /// </list>
    /// 空列表 = 「没有技能」，对武器等价于 Clear 之后不填回（全 0）。
    /// </summary>
    public List<ItemSkill> Skills { get; set; } = new();

    /// <summary>
    /// 四个符文槽（<c>ItemInfo.weaponinlay</c>，字段 15）—— 仅 genre 2 武器有意义。
    ///
    /// <para><b>★★ 它不是 repeated</b></para>
    /// proto 里是**单个** <c>WeaponRune</c> 子消息，内含 4 个具名 uint32
    /// （<c>barrel</c>/<c>ammo</c>/<c>sight</c>/<c>grip</c>），不是「4 元素数组」。
    /// 这纠正了 <c>docs/05</c> §0.3 早期「四个符文槽被写 0」的措辞歧义。
    ///
    /// <para><b>0 = 空槽（合法值）</b></para>
    /// 取证 <c>AddWeapon 0x1D979F8</c>：<c>has_weaponinlay</c> 为假时客户端**自己**把四槽
    /// 全部 <c>set_*Guid(0)</c>（<c>mov w1, wzr</c>）⇒ 「无符文的武器」发不发这条子消息等价。
    /// 真正的红线是「**已镶符文的武器重推时漏发 ⇒ 符文全掉**」。
    /// </summary>
    public WeaponInlay? Inlay { get; set; }

    /// <summary>
    /// 属性列表（<c>ItemInfo.attrlist</c>，字段 16，repeated <c>BaseAttribute{atype,avalue}</c>）——
    /// 仅 genre 4 零件有意义。
    ///
    /// <para><b>★★★ 必须恰好 6 个元素，否则客户端抛异常</b></para>
    /// <c>AddOriginal</c> 有两个**固定次数、无 Count 检查**的取值循环
    /// （主会话独立复核过边界指令）：
    /// <list type="bullet">
    /// <item>循环①下标 0,1,2：<c>0x1D99AB8 cmp w23,#3</c>；语义 = 四维属性，
    ///   <c>atype</c> 1=攻击/2=暴击/3=攻速/4=破防，写对应标量 + <c>SchoolInt[]</c> 缓存槽。</item>
    /// <item>循环②下标 3,4,5：<c>0x1D99D3C mov w21,#3</c> → <c>0x1D99E7C cmp w21,#6</c>；
    ///   语义 = 新词条等级，此处 <c>atype</c> 是**槽位号**而非属性类型。</item>
    /// </list>
    /// 全函数**零次** <c>List&lt;BaseAttribute&gt;.get_Count</c> 调用、且 <c>attrlist</c>
    /// 取出后**没有 <c>has_</c> 保护</c> ⇒ 少于 6 个元素 = <c>ArgumentOutOfRangeException</c>
    /// ⇒ 整条 pf 1031/1104 处理中断（该包后续物品全部丢失）。
    /// 多于 6 个只读前 6 个，无害。
    /// ⇒ 用 <see cref="NormalizedAttrs"/> 下发，它保证补齐到 6。
    /// </summary>
    public List<ItemAttr> Attrs { get; set; } = new();

    /// <summary>
    /// 宿主武器 guid（<c>ItemInfo.ownguid</c>，字段 17）—— 仅 genre 4 零件有意义：
    /// 这个零件当前被装在哪把武器上，<c>0</c> = 未装备。
    /// 漏发等于「零件与宿主武器解绑」（<c>docs/05</c> §0.3）。
    /// </summary>
    public uint OwnGuid { get; set; }

    /// <summary>
    /// <see cref="Attrs"/> 补齐/截断到**恰好 6 个**元素后的副本（见 <see cref="Attrs"/> 的红线注释）。
    /// 缺的位置用 <c>atype = 0</c> 占位 —— 两个循环都对 <c>atype == 0</c> 有跳过分支
    /// （循环① <c>0x1D997F8 sub w8,w0,#1</c> + <c>cmp w8,#3</c> + <c>b.hi</c>；
    /// 循环② <c>0x1D99D74 cbz w0</c>），所以占位元素被静默忽略、不会污染数据。
    /// </summary>
    public List<ItemAttr> NormalizedAttrs()
    {
        var list = new List<ItemAttr>(AttrSlotCount);
        for (int i = 0; i < AttrSlotCount; i++)
            list.Add(i < Attrs.Count ? Attrs[i] : new ItemAttr());
        return list;
    }

    /// <summary>
    /// <c>attrlist</c> 的固定长度：客户端两个循环合计读下标 0..5
    /// （<c>0x1D99AB8 cmp w23,#3</c> + <c>0x1D99E7C cmp w21,#6</c>）。
    /// </summary>
    public const int AttrSlotCount = 6;

    /// <summary>这件物品是不是「装备类」（武器/符文/零件）—— 决定要不要带上面那组字段。</summary>
    public bool IsEquipGenre => Genre is 2 or 3 or 4;
}

/// <summary>
/// 一条技能（映射 <c>gsproto.MiniSkill</c>，<c>Serialize</c> RVA <c>0x337A504</c>）。
/// <c>Type</c> 的语义随宿主 genre 而变，见 <see cref="Item.Skills"/>。
/// </summary>
public sealed class ItemSkill
{
    public uint SkillId { get; set; }

    public uint SkillLevel { get; set; }

    /// <summary>分流键（武器 1/2/4、符文 2/6、零件 7/8/9）。</summary>
    public int SkillType { get; set; }
}

/// <summary>
/// 武器的四个符文槽（映射 <c>gsproto.WeaponRune</c>，<c>Serialize</c> RVA <c>0x3598928</c>）。
/// 每个值是**符文实例 guid**，<c>0</c> = 空槽（客户端自己在 <c>0x1D979F8</c> 用 0 表示空）。
/// </summary>
public sealed class WeaponInlay
{
    public uint Barrel { get; set; }

    public uint Ammo { get; set; }

    public uint Sight { get; set; }

    public uint Grip { get; set; }

    public bool IsEmpty => Barrel == 0 && Ammo == 0 && Sight == 0 && Grip == 0;
}

/// <summary>
/// 一条属性（映射 <c>gsproto.BaseAttribute</c>，<c>Serialize</c> RVA <c>0x33D65D8</c>）。
/// <c>AValue</c> 在 proto 里是 <b>int32</b>（可为负）。
/// </summary>
public sealed class ItemAttr
{
    /// <summary>下标 0..2 时是属性类型（1 攻击/2 暴击/3 攻速/4 破防）；下标 3..5 时是新词条槽位号。</summary>
    public uint AType { get; set; }

    public int AValue { get; set; }
}

/// <summary>
/// 一个编队（映射 <c>FormationInfo{id, fightcard[] , title}</c>）。
/// 只保存本实现填写的字段；secondarycard/weapon/rune 槽未取证，保持空集合。
/// </summary>
public sealed class Formation
{
    public uint Id { get; set; }

    public string Title { get; set; } = "";

    public List<FormationCardSlot> Slots { get; } = new();
}

/// <summary>
/// 编队内一个战斗卡槽（映射 <c>FightCard</c>）。
/// <see cref="MainCardGuid"/> 为该槽的主卡实例 guid；<see cref="UsedCardGuid"/> 客户端用于
/// 区分「已上阵的卡」与「候选副卡」。上行来自 <c>LuaCall.Formation_Update</c> 的
/// <c>{MainCard, UsedCard, Secondarycard[], WeaponId, Rune[]}</c>。
/// </summary>
public sealed class FormationCardSlot
{
    public uint MainCardGuid { get; set; }

    public uint UsedCardGuid { get; set; }

    /// <summary>武器实例 guid（<c>FightCard.weaponguid</c>，未装备为 0）。</summary>
    public uint WeaponGuid { get; set; }

    /// <summary>副卡实例 guid 列表（<c>FightCard.secondarycardguid</c>）。</summary>
    public List<uint> SecondaryCardGuids { get; } = new();

    /// <summary>符文实例 guid 列表（<c>FightCard.runeitem</c>，未镶嵌为 0）。</summary>
    public List<uint> RuneItems { get; } = new();
}
