namespace SssjServer.Game;

/// <summary>
/// 新建角色（首次建号）赠送的起始角色卡小队 + 默认编队。
///
/// 卡片是 inventory item：genre=1（emGenreType.Card），detail=少女 id，particular=变体，level=阶。
/// 这里只放“赠几张、哪张”这一组常量，方便一处改；<see cref="Grant"/> 负责把卡落成
/// Player.Items（Item 实例）+ Player.Girls（GirlInfo）+ Player.Formations（1 号编队）。
/// 仅在首次建号时调用（World.NewPlayer 在 ServerOptions.GrantStarter 开启时调用）；
/// 从磁盘加载的玩家不经过这里，其存盘数据优先。
/// </summary>
public static class StarterLoadout
{
    /// <summary>赠卡数量。当前 4 张（与 <see cref="Cards"/> 一一对应）。</summary>
    public const int SquadSize = 1;

    /// <summary>
    /// 建号附带的角色单抽券 9-12-1-1 ×1，供引导 104 (gacha) 使用。同样是推断：
    /// 1-3 开放之前没有任何关卡/指引任务奖励发这张券，而 UI_Gacha.lua:476 在
    /// <c>GetItemCount(9,12,1,1) &lt; 1</c> 时直接把引导标记完成 —— 没有券，整段抽卡教程会被静默跳过。
    /// </summary>
    public const int StarterTicketCount = 1;

    /// <summary>
    /// ★★★ 一个编队最多能有几个 <c>FightCard</c> 槽：**3**（客户端硬上限，不是我们的选择）。
    ///
    /// <para>取证（本轮真机 NRE 反查）：<c>Game.EquipSolution$$.ctor</c>（RVA <c>0x1D92498</c>）
    /// 的最后一句是 <c>mov w1, #3</c> + <c>b Game.EquipSolution$$SetGroupSize</c>
    /// ⇒ 新建的 <c>EquipSolution</c> 恒有 **3** 个 <c>CardGroup</c>。
    /// 而 <c>Game.EquipSolution$$GetGroup</c>（RVA <c>0x1D92534</c>）是
    /// <c>cmp Count, groupnum; b.le → mov x0, xzr</c>
    /// —— <c>groupnum >= Count</c> 时**返回 null 而不抛**；
    /// 回到 <c>Game.EquipManager$$AddEquipSolution</c>（RVA <c>0x1D91FF8</c>）
    /// 的 <c>0x1D92270 cbnz x22</c> 处才对这个 null **抛 NullReferenceException**。</para>
    ///
    /// <para>★ 后果比看起来严重：<c>AddEquipSolution</c> 是 <c>Game.EquipManager.InitItem</c>
    /// （pf <b>1104</b> 背包**全量**）的一环 ⇒ 发第 4 个槽会让**整条 1104 中断**，
    /// 客户端打 <c>Failed to process message. PF : 1104</c>。</para>
    ///
    /// <para>★★ 这个 bug 只有**新账号首登**才会触发（老存档的编队是客户端经
    /// <c>ParseSlots</c> 回写的 3 槽），所以本地长期 e2e 一直没暴露 ——
    /// 而「每个朋友都是新账号」正好全中。</para>
    /// </summary>
    public const int MaxFormationSlots = 3;

    /// <summary>
    /// 起始卡片（Detail=少女 id, Particular=变体, Level=阶）：只有 1-7-1-1 艾琳。
    ///
    /// 这是从新手引导反推的，setting 里没有直接写明，证据：
    /// <list type="bullet">
    ///   <item>UIGuide.txt 行 105 (team) 第 2 步 <c>{7,"CheckTeamGroup",{2}}</c>：2 号栏位已有卡就跳过，
    ///     否则让玩家把抽到的 1-9-1-3 放进去 —— 引导预期建号时 2 号栏位是空的；</item>
    ///   <item>SystemUnlock.txt：3 号栏位 (UI_UnlockTeamPos3) 要通关 1-5 才开放；</item>
    ///   <item>Chapter.txt 首通奖励才发其他少女：1-5 高奈利亚、1-6 伊珂丝、2-4 6 号/朱诺、2-6 无瑕、3-2 洛可可；</item>
    ///   <item>除抽卡池外没有任何表发 1-7-1-1，而引导 115/116 在 1-6 前后就要用她。</item>
    /// </list>
    /// </summary>
    public static readonly (uint Detail, uint Particular, uint Level)[] Cards =
    {
        (7, 1, 1),
    };

    /// <summary>
    /// 把起始卡落成 Player 的 Items + Girls，并建 1 号编队。
    /// 第 1 张卡是队长（maincardguid == usedcardguid），其余卡作为额外 FightCard 槽。
    /// </summary>
    public static void Grant(Player player)
    {
        if (player.Items.Count > 0)
            return; // 已有背包时不再重复赠送（防御）

        for (int i = 0; i < Cards.Length; i++)
        {
            var (detail, particular, level) = Cards[i];
            var item = player.AddItem(genre: 1, detail, particular, level, count: 1);
            // 对应 GirlInfo：level=1（角色等级），modelid=少女 id，mood/vigor 取正常非零值，flag=1
            player.Girls.Add(new Girl
            {
                Girlid = detail,
                Level = 1,
                Modelid = detail,
                Moodvalue = 100,
                Vigor = 100,
                Flag = 1,
            });
        }

        // 1 号编队：队长 = 第 1 张卡实例 guid，其余卡各占一个 FightCard 槽
        // ★ 最多只放 MaxFormationSlots(=3) 个槽 —— 客户端 EquipSolution 恒有 3 个 CardGroup，
        //   多发一个就会让 pf 1104 整条中断（取证见 MaxFormationSlots 注释）。
        //   赠卡是 4 张，第 4 张留在背包里当备用卡，不上阵。
        var formation = new Formation { Id = 1, Title = "" };
        foreach (var item in player.Items)
        {
            if (formation.Slots.Count >= MaxFormationSlots)
                break;
            formation.Slots.Add(new FormationCardSlot
            {
                MainCardGuid = item.Guid,
                UsedCardGuid = item.Guid,
            });
        }
        player.Formations.Add(formation);

        // 放在编队之后：编队循环按背包顺序取前 3 个实例，券不能混进去
        player.AddItem(genre: 9, detail: 12, particular: 1, level: 1, count: StarterTicketCount);
    }
}
