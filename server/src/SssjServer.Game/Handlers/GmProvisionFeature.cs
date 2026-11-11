using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// GM 测试账号的自动供给（provisioning）：登录时把 <see cref="ServerOptions.GmAccounts"/>
/// 里的账号一键补成「全道具 / 全角色卡 / 全关卡三星 / 全任务完成」。
///
/// <para><b>为什么做成登录钩子而不是只留一条 gmsrv 命令</b></para>
/// 验收要反复重启客户端（<c>tools/e2e.sh</c> 每次都杀进程重登），
/// 如果全解锁只能手动触发，每轮验收都要多一步「写 gmsrv 命令 + 等 500ms 轮询」；
/// 更糟的是**新建号**那一刻还没有会话，命令根本作用不上（<c>ServerConsoleFeature</c>
/// 只对已登录会话生效）。挂在登录上就没有这些问题。
///
/// <para><b>零额外封包</b></para>
/// 用的是 <c>ctx.OnPlayerPrepare</c>（pf 1005 之前），所以全部数据随
/// 1005 <c>PlayerNtf</c>（货币 / 关卡 / 少女）、1104 <c>ItemNtf</c>（背包全量）、
/// 1026 <c>TaskValueSyncRsp</c>（任务值全量）下发 —— 这三条包本来就要发。
///
/// <para><b>只在 revision 落后时才真跑</b></para>
/// <see cref="GmUnlock.Revision"/> 记在 <c>Player.Extra["gmunlock"]</c>；
/// 已是最新就直接跳过（一次字典查表），所以老账号的日常登录零开销。
/// 往全解锁里加内容时把 Revision 加一，老存档下次登录自动补齐。
/// </summary>
public sealed class GmProvisionFeature : IGameFeature
{
    private GameFeatureContext _ctx = null!;

    public string Name => "GM 测试账号全解锁（登录自动供给）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;

        if (!ctx.Options.GmFullUnlock)
        {
            Log.Info("[gmunlock] ServerOptions.GmFullUnlock=false，登录自动全解锁已关闭" +
                     "（仍可用 gmsrv 的 godmode/allitem/allgirl/alllevel/allmission 手动触发）");
            return;
        }

        var accounts = ctx.Options.GmAccounts ?? Array.Empty<string>();
        Log.Info($"[gmunlock] 登录自动全解锁已开启，GM 账号 = " +
                 $"{(accounts.Length == 0 ? "（空，等于关闭）" : string.Join(", ", accounts.Select(Show)))}" +
                 $"，内容版本 rev={GmUnlock.Revision}");

        ctx.OnPlayerPrepare(OnPrepare);
    }

    /// <summary>账号名的日志显示（空串是 selftest 的真实账号名，要显式标出来）。</summary>
    private static string Show(string? account)
        => account switch
        {
            null => "<null>",
            "" => "<空账号=selftest 缺省>",
            "*" => "*（所有账号）",
            _ => $"'{account}'",
        };

    private void OnPrepare(ClientSession session, Player player)
    {
        var gm = _ctx.GmUnlock;
        if (!gm.IsGmAccount(player.Account))
            return;

        int had = GmUnlock.RevisionOf(player);
        if (had >= GmUnlock.Revision)
        {
            Log.Debug($"[session {session.Id}] GM 账号 {Show(player.Account)} 已是全解锁 rev={had}，跳过");
            return;
        }

        var report = gm.ApplyAll(player, force: true);
        _ctx.Store.Save(player);

        Log.Info($"[session {session.Id}] ★ GM 账号 {Show(player.Account)} 全解锁完成 " +
                 $"rev {had} → {GmUnlock.Revision}：{report.Describe()}");
        Log.Info($"[session {session.Id}] 全解锁后存量：背包 {player.Items.Count} 项 / " +
                 $"少女 {player.Girls.Count} 位 / 关卡 {player.Levels.Count} 关 / " +
                 $"任务值 {player.Tasks.Count} 条 / 货币 {player.Money.Count} 种" +
                 $"（金币 {player.GetMoney(MoneyType.Gold)}、钻 {player.GetMoney(MoneyType.FreeDiamond)}）");
    }
}
