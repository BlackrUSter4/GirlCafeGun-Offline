using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game;

/// <summary>
/// 玩家成长（指挥官经验 / 升级）与 pf 1029 <c>PlayerDataNtf</c> 的构造。
///
/// ★ <c>PlayerDataNtf{repeated int32 updatetype, repeated int64 updatevalue}</c> 的
/// <c>updatetype</c> 语义是反汇编 <c>Game.Player.OnPlayerDataChange</c>（RVA <c>0x1855148</c>）
/// 的 switch 跳转表得到的（表基址 <c>0x3D2D1FC</c>，6 个 int32 相对偏移）：
///
/// | updatetype | 跳转目标 | 写入 Game.Player 字段 | 含义 |
/// | --- | --- | --- | --- |
/// | 1 | 0x18552E0 | `me+0x2C` | **level** —— 且会比较新旧值，新值更大时走 `Game.Login.OnPlayerLevelUp`（升级动画） |
/// | 2 | 0x18554C8 | `me+0x30` | **exp** |
/// | 3 / 4 | 0x185551C | —— | 落到 default 分支，只 `Game.Logger.Error` |
/// | 5 | 0x18555EC | `me+0x40` | **headframe**（头像框） |
/// | 6 | 0x1855640 | `me+0x20` / `me+0x58` | 名字 / 社团 id（分支较复杂，未细分） |
///
/// 字段偏移取证 `dump/dump.cs:558092`（`level` 0x2C）、`558094`（`exp` 0x30）、
/// `558100`（`headframe` 0x40）、`558088`（`name` 0x20）、`558106`（`clubid` 0x58）。
///
/// 顺序要求：**先 level 后 exp**（level 那一条才会触发升级表现，exp 只是刷数字）。
/// </summary>
public static class PlayerProgress
{
    /// <summary>updatetype = 1 → 指挥官等级。</summary>
    public const int UpdateTypeLevel = 1;

    /// <summary>updatetype = 2 → 指挥官经验。</summary>
    public const int UpdateTypeExp = 2;

    /// <summary>updatetype = 5 → 头像框。</summary>
    public const int UpdateTypeHeadFrame = 5;

    /// <summary>
    /// 加指挥官经验并按 <c>PlayerExp.txt</c> 连续升级。
    /// 规则：<c>Exp</c> 列是「升到下一级需要的经验」，够了就减掉并 +1 级；
    /// 表里 <c>Exp == 0</c> 或超出最高级视为满级，经验就地累积不再升。
    /// 升级时体力上限提高，这里**不自动回满**（回满只在登录时按 RefillVigourOnLogin 处理）。
    /// </summary>
    /// <returns>(升了几级, 新等级, 新经验)</returns>
    public static (int LevelsGained, int Level, int Exp) AddMasterExp(Player player, PlayerExpTable table, int exp)
    {
        if (exp <= 0)
            return (0, player.Level, player.Exp);

        player.Exp += exp;
        int gained = 0;

        while (true)
        {
            int need = table.ExpToNext(player.Level);
            if (need <= 0)
                break;                       // 满级 / 无配置：经验只累积
            if (player.Exp < need)
                break;
            player.Exp -= need;
            player.Level++;
            gained++;
            if (table.MaxLevel > 0 && player.Level >= table.MaxLevel)
                break;
        }

        if (gained > 0)
        {
            Log.Info($"[progress] 升级 account={player.Account} +{gained} 级 → LV.{player.Level} " +
                     $"exp={player.Exp} 体力上限={table.MaxVigour(player.Level)}");
        }
        return (gained, player.Level, player.Exp);
    }

    /// <summary>
    /// 构造 pf 1029 <c>PlayerDataNtf</c>：把等级与经验一起推给客户端
    /// （先 level 再 exp，理由见类注释）。
    /// </summary>
    public static PlayerDataNtf BuildLevelExpNtf(Player player)
    {
        var ntf = new PlayerDataNtf();
        ntf.Updatetype.Add(UpdateTypeLevel);
        ntf.Updatevalue.Add(player.Level);
        ntf.Updatetype.Add(UpdateTypeExp);
        ntf.Updatevalue.Add(player.Exp);
        return ntf;
    }
}
