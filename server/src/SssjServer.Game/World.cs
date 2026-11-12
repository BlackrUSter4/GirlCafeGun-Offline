using System.Text;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game;

/// <summary>
/// 世界：所有在线会话与玩家的注册表。
///
/// 职责：
///  * 维护 会话(id) → 玩家 与 账号 → 会话 的映射；
///  * <see cref="GetOrCreatePlayer"/>：按账号取得玩家，不存在则建新号（确定性 roleid）；
///  * 登录/登出时更新映射并触发持久化。
///
/// 线程安全：所有可变状态都用 <see cref="object"/> 锁保护。gateway 为每个连接一个任务，
/// 同一会话的封包串行处理，但不同会话并发，跨会话共享的字典需要锁。
/// </summary>
public sealed class World
{
    private readonly PlayerStore _store;
    private readonly bool _skipTutorial;
    private readonly bool _grantStarter;
    private readonly bool _refillVigourOnLogin;
    private readonly int _startGold;
    private readonly int _startDiamond;
    private readonly PlayerExpTable _expTable;
    private readonly object _sync = new();

    /// <summary>session.Id → 玩家。</summary>
    private readonly Dictionary<int, Player> _bySession = new();

    /// <summary>session.Id → 会话（GM 控制台等需要主动推送的场景用）。</summary>
    private readonly Dictionary<int, IGameSession> _sessions = new();

    /// <summary>账号 → 会话 id（同一账号重连时淘汰旧会话）。</summary>
    private readonly Dictionary<string, int> _accountSession = new(StringComparer.Ordinal);

    /// <summary>
    /// 便捷构造（单测用）：只关心「跳教程 / 送起始卡」两个开关，
    /// 其余取 <see cref="ServerOptions"/> 默认值，等级表为空表（体力上限回退 28）。
    /// </summary>
    public World(PlayerStore store, bool skipTutorial = true, bool grantStarter = true)
        : this(store,
               new ServerOptions { SkipTutorial = skipTutorial, GrantStarter = grantStarter },
               PlayerExpTable.Empty)
    {
    }

    public World(PlayerStore store, ServerOptions options, PlayerExpTable expTable)
    {
        _store = store;
        _skipTutorial = options.SkipTutorial;
        _grantStarter = options.GrantStarter;
        _refillVigourOnLogin = options.RefillVigourOnLogin;
        _startGold = options.StartGold;
        _startDiamond = options.StartDiamond;
        _expTable = expTable;
    }

    /// <summary>该玩家当前等级的体力上限（PlayerExp.txt 的 Spirit 列）。</summary>
    public int MaxVigour(Player player) => _expTable.MaxVigour(player.Level);

    /// <summary>
    /// 按账号取玩家：先在存活会话里找，否则从存档加载，再否则新建。
    /// 新建号会同步写一份存档（保证掉线后仍可恢复）。
    /// </summary>
    public Player GetOrCreatePlayer(string account)
    {
        lock (_sync)
        {
            if (_accountSession.TryGetValue(account, out var sid) &&
                _bySession.TryGetValue(sid, out var online))
            {
                return online;
            }
        }

        var player = _store.Load(account) ?? NewPlayer(account);
        if (player.Name.Length == 0)
            player.Name = NormalizeName(account);
        MigrateRoleId(player);
        _store.Save(player); // 确保新号 / 已有号都落盘
        return player;
    }

    /// <summary>
    /// 把「存档里超出 uint32 的旧 roleid」重算成新口径（<see cref="DeriveRoleId"/>）。
    ///
    /// 为什么必须做：<c>gsproto.NoticeChattingMsg.sender_id</c> / <c>receiver_id</c> 是
    /// **uint32**（全 gsproto 里只有这两个角色 id 是 32 位；<c>PlayerNtf.roleid</c> 是 uint64），
    /// 而客户端 <c>me.id</c> 拿的就是 <c>roleid</c>
    /// （<c>Game.Player$$Login</c> RVA <c>0x1854130 str x20, [x22, #0x10]</c>）。
    /// roleid 一旦超过 <c>0xFFFFFFFF</c>，聊天里 <c>me.id == sender_id</c> 恒假 ⇒
    /// 自己发的消息被渲染成「别人的」（<c>UI_Chat.lua:581/589/595</c> 走 <c>ShowLeft</c>）、
    /// 且每条都在主城弹飘字（<c>PlayerLogic.lua:283</c>）。取证见 `docs/05` §18.5。
    ///
    /// 迁移是安全的：存档文件按**账号**命名（<c>PlayerStore</c> 的
    /// <c>SanitizeFileName(account) + ".player.json"</c>），道具 / 关卡 / 任务值都不引用 roleid，
    /// 所以只是换一个对外 id。唯一副作用是客户端以 <c>me.id</c> 拼 key 的**本地**设置会重置
    /// （例 <c>"WareHouseSortCfg_"..me.id</c>，<c>PlayerLogic.lua:60</c> —— 仓库排序偏好）。
    ///
    /// 幂等：新口径算出来的值本身 ≤ <c>uint.MaxValue</c>，所以第二次加载不会再动。
    /// </summary>
    internal static void MigrateRoleId(Player player)
    {
        if (player.Roleid <= uint.MaxValue)
            return;

        ulong old = player.Roleid;
        player.Roleid = DeriveRoleId(player.Account);
        Log.Warn($"[world] 账号 {(player.Account.Length == 0 ? "<空>" : player.Account)} 的存档 roleid " +
                 $"{old} 超出 uint32，已按新口径重算为 {player.Roleid} —— " +
                 "聊天协议的 sender_id 只有 32 位（docs/05 §18.5），不重算会让玩家自己的消息显示成别人的。" +
                 "存档数据不受影响（按账号命名），仅客户端以 me.id 拼 key 的本地偏好会重置。");
    }

    /// <summary>登录：把玩家挂到会话上，记入在线表，刷新本次进入时间与持久化。</summary>
    public Player Login(IGameSession session, string account)
    {
        var player = GetOrCreatePlayer(account);
        player.StartTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        player.ServerZone = _store.ServerZone;
        RecoverVigour(player);

        lock (_sync)
        {
            // 同一账号的新连接顶掉旧连接
            //
            // ★ 必须**同时**从 _sessions 里摘掉旧会话（本轮修）：先前只 Remove 了 _bySession，
            //   于是旧会话仍然留在 OnlineSessions() 的快照里。后果是所有「按在线会话广播」的
            //   功能都会往一个已经断开的 socket 上写 —— 真机实测：客户端重启后重新登录成为
            //   session 2，聊天广播却打出 `session 1 => pf=1012` 与 `session 2 => pf=1012` 两条
            //   （`work/server.log`，好友/社团/聊天三域联调时发现）。
            //   正常断线会走 Logout 清理，但「客户端进程被杀 → 服务端还没察觉 → 新连接先到」
            //   这个顺序下 Logout 晚于 Login，泄漏就固化了。
            if (_accountSession.TryGetValue(account, out var oldSid) &&
                oldSid != session.Id)
            {
                _bySession.Remove(oldSid);
                _sessions.Remove(oldSid);
            }
            _bySession[session.Id] = player;
            _sessions[session.Id] = session;
            _accountSession[account] = session.Id;
        }

        session.Tag = player;
        _store.Save(player);
        return player;
    }

    /// <summary>登出：从在线表摘下，落盘。account 未知时按角色 id 兜底查找。</summary>
    public void Logout(IGameSession session)
    {
        Player? player;
        lock (_sync)
        {
            _bySession.TryGetValue(session.Id, out player);
            _bySession.Remove(session.Id);
            _sessions.Remove(session.Id);
            if (player is not null && _accountSession.TryGetValue(player.Account, out var sid) && sid == session.Id)
            {
                _accountSession.Remove(player.Account);
            }
        }
        if (player is not null && session.Tag is not null)
            _store.Save(player);
    }

    /// <summary>按账号查找在线玩家（作业查找失败时返回 null）。</summary>
    public Player? FindOnlineByAccount(string account)    {
        lock (_sync)
        {
            if (_accountSession.TryGetValue(account, out var sid) &&
                _bySession.TryGetValue(sid, out var p))
            {
                return p;
            }
        }
        return null;
    }

    /// <summary>
    /// 当前在线会话快照（GM 控制台向客户端推 Lua、活动广播等用）。
    /// 返回拷贝，遍历期间不会被并发修改影响。
    /// </summary>
    public List<IGameSession> OnlineSessions()
    {
        lock (_sync)
        {
            return _sessions.Values.ToList();
        }
    }

    /// <summary>
    /// 从账号确定性派生 roleid（文档见 AGENTS.md/03）：FNV-1a 64 位哈希 UTF-8 账号字节，
    /// 再做一个模运算映射到便捷区间，保证同一账号每次算出同一 roleid。
    ///
    /// ★ 值域是 <c>[1, 0xFFFFFFFF]</c>（**不是** 先前的 <c>[1, 2^53)</c>）。
    /// 原因：<c>gsproto.NoticeChattingMsg.sender_id</c> / <c>receiver_id</c> 是 **uint32**，
    /// 而客户端拿它与 uint64 的 <c>me.id</c>（= <c>PlayerNtf.roleid</c>）直接比较，
    /// 超过 32 位就永远比不相等 —— 后果与取证见 `docs/05` §18.5、
    /// 迁移见 <see cref="MigrateRoleId"/>。
    /// 32 位仍然远小于客户端 Number(double) 的 53 位精度上限，不会丢位段。
    /// </summary>
    public static ulong DeriveRoleId(string account)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong h = offset;
        foreach (byte b in Encoding.UTF8.GetBytes(account))
        {
            h ^= b;
            h *= prime;
        }
        // 映射到 [1, 0xFFFFFFFF]：聊天协议的 sender_id 只有 32 位（见上）
        return (h % 0xFFFFFFFEUL) + 1UL;
    }

    private Player NewPlayer(string account)
    {
        var p = new Player(account, DeriveRoleId(account))
        {
            Name = NormalizeName(account),
            ServerZone = _store.ServerZone,
        };
        // 起始货币。id 取证 extracted/lua/MoneyLogic.lua:10..45（见 MoneyType）：
        // 1=体力（给满该等级上限，PlayerExp.txt 的 Spirit 列，1 级 = 28）、2=金币、3=免费钻石。
        p.SetMoney(MoneyType.Vigour, _expTable.MaxVigour(p.Level));
        p.SetMoney(MoneyType.Gold, _startGold);
        p.SetMoney(MoneyType.FreeDiamond, _startDiamond);
        p.VigourTime = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 跳过新手教程：预置任务值 group=1 / task=15 → value=6。
        // 依据 PlayerLogic.lua:516 IsFirstLevelComplete()：
        //   if GetTaskValue(1,15) >= 6 then return true
        //   return GetTaskValue(1,14) > 0
        // value=6 使第一个分支为真 → OnUpdateEnd 走 Map.LoadById(2) → UI.ClearStack()
        // → UI.Open('UI_Main') 主城分支，而不是教程分支。
        // 只在“首次建号”时播种；从磁盘加载的玩家不经过这里，其存盘值优先。
        if (_skipTutorial)
            p.SetTask(1, 15, 6);

        // 起始角色卡 + 编队（同样只在首次建号时发放；GrantStarter=false 则不发）。
        if (_grantStarter)
            StarterLoadout.Grant(p);
        return p;
    }

    private static string NormalizeName(string account)
        => string.IsNullOrWhiteSpace(account) ? "Player" : account;

    /// <summary>
    /// 体力恢复。两段逻辑：
    ///  ① 自然恢复：距 <see cref="Player.VigourTime"/> 每 <see cref="PlayerExpTable.VigourRecoverSeconds"/>
    ///     （6 分钟，取证 Notification.lua:164）恢复 1 点，不超过该等级上限；
    ///  ② <c>RefillVigourOnLogin</c> 打开时直接补满（私服便利，默认开）。
    /// 已高于上限（GM 加的）不动，只把计时基准推到现在。
    /// </summary>
    internal void RecoverVigour(Player player)
    {
        ulong now = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int max = _expTable.MaxVigour(player.Level);
        int cur = player.Vigour;

        if (cur >= max)
        {
            player.VigourTime = now;
            return;
        }

        if (_refillVigourOnLogin)
        {
            player.SetMoney(MoneyType.Vigour, max);
            player.VigourTime = now;
            Log.Info($"[world] 体力补满 account={player.Account} {cur} → {max}");
            return;
        }

        ulong last = player.VigourTime == 0 ? now : player.VigourTime;
        if (now <= last)
        {
            player.VigourTime = now;
            return;
        }

        int gained = (int)((now - last) / PlayerExpTable.VigourRecoverSeconds);
        if (gained <= 0)
            return;

        int next = Math.Min(max, cur + gained);
        player.SetMoney(MoneyType.Vigour, next);
        // 只推进已经“兑现”的整段时间，余数留到下次
        player.VigourTime = last + (ulong)(gained * PlayerExpTable.VigourRecoverSeconds);
        if (next >= max)
            player.VigourTime = now;
        Log.Info($"[world] 体力自然恢复 account={player.Account} {cur} → {next}（上限 {max}）");
    }
}
