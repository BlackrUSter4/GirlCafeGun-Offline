using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// LuaCall 313 GetReserveAward: the pre-register reward popup (UI_SubscribeReward).
///
/// The client opens this popup on its own after login once 1-6 is cleared (ReservationKR:CanRewards,
/// EN and KR clients) and stays on it until the reply arrives, so an unanswered 313 blocks the main
/// screen. Eligibility and the claimed flag mirror CanRewards: task (1, 2002) == 0 and 1-6 normal
/// cleared. Rewards come from ReserveAward.txt, the same table the popup draws its icons from.
/// </summary>
public sealed class ReserveAwardFeature : IGameFeature
{
    internal const int Command = 313;
    internal const int ClaimedGroup = 1;
    internal const int ClaimedTask = 2002;
    private const string FileName = "ReserveAward.txt";

    private GameFeatureContext _ctx = null!;
    private List<int[]> _awards = new();

    public string Name => "预约奖励（LuaCall 313 GetReserveAward）";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        _awards = LoadAwards(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(Command, Claim);
    }

    /// <summary>Rows whose EndTime has passed are dropped by the client too (ReservationKR:IsValid).</summary>
    internal static List<int[]> LoadAwards(string settingDir)
    {
        var table = SettingTable.Load(Path.Combine(settingDir, FileName));
        var awards = new List<int[]>();
        if (table is null)
            return awards;
        foreach (var row in table.Rows)
            awards.AddRange(ChapterTable.ParseTuples(table.Str(row, "AwardItem")));
        return awards;
    }

    internal static bool CanClaim(Player player)
        => player.GetTask(ClaimedGroup, ClaimedTask) == 0
           && MapList.PassCount(player.GetLevelStar(MapList.MapId(1, 6, 1))) > 0;

    private Task Claim(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player)
            return Task.CompletedTask;

        lock (player)
        {
            if (_awards.Count == 0 || !CanClaim(player))
            {
                // A nonzero nError makes the popup show the error and close, so the player is never stuck.
                Log.Warn($"[session {session.Id}] GetReserveAward 拒绝：奖励 {_awards.Count} 项，" +
                         $"已领标记 {player.GetTask(ClaimedGroup, ClaimedTask)}");
                _ctx.LuaCalls.CallClient(session, cmd, new { nError = 1, tbAward = Array.Empty<int[]>() });
                return Task.CompletedTask;
            }

            var granted = new RewardGrantResult();
            _ctx.Rewards.Grant(player, _awards, RewardSource.Extra, granted);
            player.SetTask(ClaimedGroup, ClaimedTask, 1);
            _ctx.Store.Save(player);

            GameNotify.PushRewards(session, player, granted);
            // Push the flag before the reply so CanRewards is already false when the popup closes.
            GameNotify.PushTaskValues(session, new[]
            {
                new KeyValuePair<uint, int>(Player.ComposeTaskId(ClaimedGroup, ClaimedTask), 1),
            });
            _ctx.LuaCalls.CallClient(session, cmd, new { nError = 0, tbAward = granted.ToAwardTuples() });
            Log.Info($"[session {session.Id}] GetReserveAward 发放 {granted.Describe()}");
        }
        return Task.CompletedTask;
    }
}
