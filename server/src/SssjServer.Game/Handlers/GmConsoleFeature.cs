using System.Text;
using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// GM 控制台：把一段 Lua 源码推给客户端执行，并把客户端的 print 输出捞回服务端日志。
///
/// 取证链：
///  * <c>LuaCall.DoLuaString = 999999</c>「服务器指定客户端执行Lua脚本」（LuaCall.lua:489）；
///    客户端分支（LuaCall.lua:3520-3526）：
///    <code>else GM.DoCommand("CL", tbParam.luaStr) end</code>
///  * <c>GM.DoCommand("CL", szCmd)</c>（GM.lua:81-106）：临时替换 <c>_G.print</c> 收集输出 →
///    <c>xpcall(function() assert(load(szCmd))() end, ...)</c> 执行 → 还原 print →
///    <c>DebugEditor.SendResult(table.concat(tbOutput, '\n'))</c>。
///    注意它同时调用了原始 print，所以**输出一定会进 logcat**（`adb logcat | grep Unity`）。
///  * <c>Game.DebugEditor</c>（dump.cs:581373）是客户端**自带的 TCP 调试服务器**
///    （<c>const int Port = 12346</c>），<c>SendResult</c> 写回最近连上的调试客户端 socket，
///    不经过游戏服务器；没有调试客户端连时就是空操作。
///    因此本控制台的「回显」通道是 logcat，不是网络。
///  * 另一条方向：服务端可以主动推 <c>S2C.DebugEditor{szRet=...}</c>（S2CCall.lua:74），
///    客户端会把 szRet 转给 <c>DebugEditor.SendResult</c>。这里也注册了同名 sFunction 的上行处理，
///    以防客户端（`GM.DoCommand` 的 "AU"/"GS" 分支，GM.lua:107）把 GM 命令发到服务端来。
///
/// 用法：往 <c>ServerOptions.GmFile</c>（默认 <c>work/gm.lua</c>）写 Lua 源码，服务端检测到文件
/// 变化就推给所有在线会话执行。例如：
/// <code>
/// echo 'print("vigour=", me.vigour)' > work/gm.lua
/// </code>
/// 这是本项目最强的「客户端内省」手段：能直接读客户端状态、驱动 UI、复现结算流程，
/// 不必靠 adb 点屏幕。
/// </summary>
public sealed class GmConsoleFeature : IGameFeature
{
    /// <summary>LuaCall.DoLuaString = 999999（LuaCall.lua:489）。</summary>
    public const int DoLuaStringCmd = 999999;

    /// <summary>登录即开自动战斗的客户端 Lua（见 Install 里的说明）。pcall 包一层，免得 SystemUnlock 还没初始化时报错中断。</summary>
    /// <summary>
    /// Client half of --all-events-open. Config loaders cache every date through GetDate2Time, which records
    /// {table, key, date} in Preload's tableTimeKey so login can re-derive them; rewriting both the value and
    /// the recorded date keeps a later refresh from closing the event again. nEnd is left out because only
    /// banners use it, and opening it would list every historical segment of each banner at once.
    /// </summary>
    public const string AllEventsOpenLua =
        "pcall(function() local keys,i=nil,1 while true do local a,b=debug.getupvalue(RegisterTimeKey,i) if not a then break end " +
        "if a=='tableTimeKey' then keys=b end i=i+1 end if not keys then return end " +
        "local ends={endTime=1,nEndTime=1,EndTime=1,nLevelEndTime=1,FirstAwardEndTime=1,ClearTime=1,clearTime=1,nClearTime=1} " +
        "local date={year=2030,month=1,day=1,hour=4,min=0,sec=0} local future=GetSecFromNowData(date) local now=GetTime() " +
        "for _,e in ipairs(keys) do local v=e.table and e.table[e.key] " +
        "if ends[e.key] and type(v)=='number' and v<now then e.table[e.key]=future e.date=date end end end)" +
        // These configs keep raw date strings or numbers and call GetDate2Time on each check, so tableTimeKey never
        // sees them. Some also reset or reload after login, so their loaders are wrapped to re-extend whatever they load.
        "pcall(function() local names={EndTime=1,endTime=1,VoteEnd=1,End=1,BuyEndTime=1,nCloseTime=1} " +
        "local roots={'BattlePass','MonoPoly','RemakeActivity','ConcertExchange','GirlSupport','ShopTabLogicC','PuzzleLogic','MonthMission','MusicMission'} " +
        "local function extend() local seen={} local future=GetSecFromNowData({year=2030,month=1,day=1,hour=4,min=0,sec=0}) local now=GetTime() " +
        // An end date is held three ways: as a date string, as the number 202110240400, or as unix seconds.
        "local function walk(t,d) if type(t)~='table' or d>4 or seen[t] then return end seen[t]=true " +
        "for k,v in pairs(t) do if names[k] and type(v)=='string' then t[k]=string.gsub(v,'%d%d%d%d','2030',1) " +
        "elseif names[k] and type(v)=='number' then " +
        "if v>=190000000000 and v<203000000000 then t[k]=203001010400 elseif v>1000000000 and v<now then t[k]=future end " +
        "elseif type(v)=='table' then walk(v,d+1) end end end " +
        "for _,g in ipairs(roots) do local r=_G[g] if type(r)=='table' then for k,v in pairs(r) do if type(v)=='table' then walk(v,0) end end end end " +
        // The pass shows one season. The server serves the newest, so every older one stays closed here.
        // A past date, not 0: UpdateBPNowInfo passes it to GetDate2Time, which returns nil for 0 and the compare then throws.
        "local b=BattlePass and BattlePass.AllBattlePassConfig if b then local m=0 for k in pairs(b) do if k>m then m=k end end " +
        "for k,v in pairs(b) do if k~=m then v.EndTime=201901010000 end end BattlePass.NowBattlePassConfig=b[m] end " +
        // GetTimeConfig returns the first war in its dates, so only the newest stays open: the one ClubBossHandler serves.
        "local w=ClubCommon and ClubCommon.tbClubBossTimeConfig if w then local m=0 for k in pairs(w) do if k>m then m=k end end " +
        "for k,v in pairs(w) do if k==m then v.ShowTime=now-172800 v.BeginTime=now-86400 v.EndTime=future v.AwardTime=future+86400 v.ClearTime=future+172800 " +
        "else v.ShowTime=0 v.ClearTime=0 end end end end " +
        "for _,h in ipairs({{'GirlSupport','LoadTimeListConfig'},{'ConcertExchange','LoadTimeListConfig'},{'BattlePass','Init'},{'MonoPoly','LoadCfg'},{'RemakeActivity','Init'}}) do " +
        "local r,f=_G[h[1]],h[2] if r and r[f] and not r['__ext'..f] then local o=r[f] r['__ext'..f]=true " +
        "r[f]=function(...) local a,c=o(...) pcall(extend) return a,c end end end " +
        "for _,g in ipairs({'GirlSupport','ConcertExchange'}) do local r=_G[g] if r and not r.timeListConfig then pcall(r.LoadTimeListConfig,r) end end " +
        "extend() end) " +
        // First recharge (23) and the diamond fund (25) ship with Sort 0, which GetEnabledActivity treats as hidden.
        "pcall(function() local l=NormalActivityConfig and NormalActivityConfig.tbActivityListConfig if l then "+
        "if l[23] then l[23].nSort=9000 end if l[25] then l[25].nSort=8999 end end end) " +
        // These three only show the first open entry they find, so opening every entry would hide all but one for good.
        // Instead one entry stays open per week, in id order. The other single-entry families have only one row,
        // and the anniversary ones already list every open entry.
        "pcall(function() local now=GetTime() local week=math.floor(now/604800) " +
        "local function rot(t,b,c,e) if type(t)~='table' then return end local ids={} for k in pairs(t) do ids[#ids+1]=k end " +
        "if #ids<2 then return end table.sort(ids) local pick=ids[week%#ids+1] " +
        "for k,v in pairs(t) do if k==pick then v[b]=now-86400 v[c]=now+31536000 if v[e] then v[e]=now+31536000 end else v[c]=now-86400 end end end " +
        "rot(BuffTowerLogic and BuffTowerLogic.tbChallengeTowerConfig,'BeginTime','ClearTime','EndTime') " +
        "rot(LotteryActivityLogic and LotteryActivityLogic.tbLotteryActivityConf,'beginTime','clearTime','endTime') " +
        "rot(WarChessTower and WarChessTower.tbTotalConfig,'BeginTime','ClearTime','EndTime') end)";

    private static readonly TimeSpan AllEventsRepatchDelay = TimeSpan.FromSeconds(8);

    /// <summary>重连后让客户端 Lua 侧缓存跟 pf 1035 重新对齐（见 Install 里的说明）。</summary>
    public const string ReloginResyncLua =
        "pcall(function() if PhoneMsgLogic then PhoneMsgLogic.bUIInitLetter = false end end)";

    public const string AutoFightFromStartLua =
        "pcall(function() if SystemUnlock and SystemUnlock.tbUnlock then SystemUnlock.tbUnlock['UI_UnlockAutoFight'] = nil end end)";

    public string Name => "GM 控制台（DoLuaString 999999 下发客户端 Lua）";

    public void Install(GameFeatureContext ctx)
    {
        GmConsole.Attach(ctx.World, ctx.LuaCalls);
        var rotation = Config.GachaRotation.For(ctx.Options.SettingDirectory);
        ctx.OnPlayerLogin((session, player) =>
        {
            if (ctx.Options.AllEventsOpen)
            {
                ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = AllEventsOpenLua });
                // Configs registered with RegisterLoginEvents (BattlePass and others) load after this push and
                // overwrite it, so the patch runs again once the client has finished its login work.
                _ = Task.Delay(AllEventsRepatchDelay).ContinueWith(_ =>
                    ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = AllEventsOpenLua }), TaskScheduler.Default);
            }

            // A persisted disabled plan must also restore dates on reconnect
            // when the client Lua VM survived the server restart.
            if (rotation.HasConfiguration || rotation.Read().Enabled)
                ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = rotation.ClientLua() });

            // GM 模式下从第一关就给自动战斗，但**不**改关卡记录：
            // 客户端 SystemUnlock:GetUnlock(key) 对没有配置行的 key 走 SpecialUnlock → true，
            // 所以把 UI_UnlockAutoFight 这一行从运行期表里拿掉即可（SystemUnlock.lua:16-21）。
            // 以前用 gmsrv `unlock UI_UnlockAutoFight` 是把门槛关 1-6 记成通关 —— 结果客户端
            // 直接起 fight1.6Force 强制指引，把 1-2..1-5 全跳了。Lua 表每次登录重新装，故每次都推。
            if (ctx.Options.GmFullUnlock)
                ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = AutoFightFromStartLua });

            // 断线重连时 Lua VM 还活着：PhoneMsgLogic.bUIInitLetter 一旦为 true，MakeLetterAll 就
            // 不再从 Game.PhoneMsgMgr（pf 1035 刚覆盖过的原生缓存）重建私信列表 —— 离线期间/登录补发
            // 的私信在手机界面里看不见（列表空、新手引导 phone1 第 2 步没目标）。登录后把它清掉。
            ctx.LuaCalls.CallClient(session, DoLuaStringCmd, new { luaStr = ReloginResyncLua });
        });

        // 上行的 sFunction="DebugEditor"（原服 GM 通道，参数是 Lua 源码）由
        // GmDebugEditorFeature 用 MapRawFunction 处理 —— 那条通道的 sJsonParam 不是 JSON，
        // 用 MapFunction 注册的话会在 JsonNode.Parse 处就失败。

        // 上行（正常不会出现）：客户端把 999999 发回来
        ctx.MapLuaCall(DoLuaStringCmd, (session, cmd, param) =>
        {
            Log.Info($"[session {session.Id}] [gm] 收到客户端 DoLuaString 回声：{param?.ToJsonString()}");
            return Task.CompletedTask;
        });
    }
}

/// <summary>
/// GM 控制台的推送端。由 <see cref="GmConsoleFeature"/> 在装配时注入 World / LuaCallRouter，
/// 供 App 层的文件监视循环调用。
/// </summary>
public static class GmConsole
{
    private static World? _world;
    private static LuaCallRouter? _luaCalls;

    internal static void Attach(World world, LuaCallRouter luaCalls)
    {
        _world = world;
        _luaCalls = luaCalls;
    }

    /// <summary>是否已装配（未装配时 <see cref="Broadcast"/> 直接返回 0）。</summary>
    public static bool Ready => _world is not null && _luaCalls is not null;

    /// <summary>
    /// 把一段 Lua 源码推给所有在线会话执行（pf 1024，sCmd=999999，tbParam={luaStr=...}）。
    /// 返回推送到的会话数。客户端的 print 输出去 logcat。
    /// </summary>
    public static int Broadcast(string luaStr)
    {
        var world = _world;
        var luaCalls = _luaCalls;
        if (world is null || luaCalls is null)
        {
            Log.Warn("[gm] 控制台未装配（GmConsoleFeature 未安装），忽略本次命令");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(luaStr))
            return 0;

        int sent = 0;
        foreach (var session in world.OnlineSessions())
        {
            if (session is not SessionAdapter adapter)
                continue;
            luaCalls.CallClient(adapter.Inner, GmConsoleFeature.DoLuaStringCmd, new { luaStr });
            sent++;
        }
        Log.Info($"[gm] 已下发 Lua（{Encoding.UTF8.GetByteCount(luaStr)} 字节）到 {sent} 个会话");
        return sent;
    }
}
