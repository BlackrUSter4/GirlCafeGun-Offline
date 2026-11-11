using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Anniversary puzzle (440/441) and event puzzle (442-445). A board has 18 pieces. Each flip costs one of the board's
/// money, and each piece and claimed award is one bit in the board's task value (group 86), as PuzzleLogic reads them.
/// Replies echo the request with nRet set, because the server side of these commands is `tbRet = tbParam`.
/// </summary>
public sealed class PuzzleEventFeature : IGameFeature
{
    private const int Group = 86;
    private const int PieceCount = 18;
    private const int AwardBit = 21;
    private const int DailyBoardBase = 10;   // tbTaskPuzzle = {11..15}
    private const int DailyMissionBase = 15; // tbTaskMission = {16..20}
    private const int DailyVersionTask = 21;

    // The client maps -N to the text key Puzzle.ErrorN.
    private const int Refused = -1, NoMoney = -5;

    private sealed record Award(int Count, List<int[]> Rewards);
    private sealed record Board(int Money, List<Award> Awards);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, Board> _boards = new();
    private readonly Dictionary<(int Version, int Board), Board> _dailyBoards = new();

    public string Name => "Puzzle events (440-445)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "PuzzleCfg.txt")) is { } main)
            foreach (var row in main.Rows)
                _boards[main.Int(row, "ID")] = new(main.Int(row, "MONEY"), Enumerable.Range(1, 4)
                    .Select(i => new Award(main.Int(row, "COUNT" + i), ChapterTable.ParseTuples(main.Str(row, "AWARD" + i)))).ToList());
        if (SettingTable.Load(Path.Combine(directory, "DailyPuzzleCfg.txt")) is { } daily)
            foreach (var row in daily.Rows)
                _dailyBoards[(daily.Int(row, "VER"), daily.Int(row, "ID"))] = new(daily.Int(row, "MONEY"),
                    new() { new(daily.Int(row, "COUNT"), ChapterTable.ParseTuples(daily.Str(row, "AWARD"))) });

        ctx.MapLuaCall(440, OnOpen);
        ctx.MapLuaCall(441, OnBox);
        ctx.MapLuaCall(442, OnDailyOpen);
        ctx.MapLuaCall(443, OnDailyBox);
        ctx.MapLuaCall(444, OnDailyMission);
        ctx.MapLuaCall(445, OnDailyReset);
    }

    private static JsonObject Echo(JsonNode? param, int result)
    {
        var reply = param is JsonObject request ? (JsonObject)request.DeepClone() : new JsonObject();
        reply["nRet"] = result;
        return reply;
    }

    private Task OnOpen(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nGroup");
        lock (player)
        {
            if (!_boards.TryGetValue(id, out var board)) return EventKit.Reply(_ctx, session, cmd, Echo(param, Refused));
            int result = Flip(session, player, board, id, EventKit.Number(param, "nIndex"), out var changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, Echo(param, result));
        }
        return Task.CompletedTask;
    }

    private Task OnBox(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nGroup"), index = EventKit.Number(param, "nIndex");
        lock (player)
        {
            if (!_boards.TryGetValue(id, out var board) || index < 1 || index > board.Awards.Count
                || !Claim(player, id, board, index, out var rewards, out var changes))
                return EventKit.Reply(_ctx, session, cmd, Echo(param, Refused));
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, Echo(param, 0));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Finishing the board pays its award in the same reply: the event screen opens the reward popup from tbAward
    /// here (UI_EventPuzzle.OnPuzzleAward), and marking it claimed retires the claim button.
    /// </summary>
    private Task OnDailyOpen(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int group = EventKit.Number(param, "nGroup");
        lock (player)
        {
            if (!_dailyBoards.TryGetValue((EventKit.Number(param, "nVer"), group), out var board))
                return EventKit.Reply(_ctx, session, cmd, Echo(param, Refused));
            int task = DailyBoardBase + group;
            int result = Flip(session, player, board, task, EventKit.Number(param, "nIndex"), out var changes);
            var reply = Echo(param, result);
            RewardGrantResult? rewards = null;
            if (result == 0 && Claim(player, task, board, 1, out var granted, out var claimChanges))
            {
                rewards = granted;
                changes.AddRange(claimChanges);
                reply["tbAward"] = EventKit.Tuples(granted);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, reply);
        }
        return Task.CompletedTask;
    }

    private Task OnDailyBox(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int group = EventKit.Number(param, "nGroup");
        lock (player)
        {
            if (!_dailyBoards.TryGetValue((EventKit.Number(param, "nVer"), group), out var board)
                || !Claim(player, DailyBoardBase + group, board, 1, out var rewards, out var changes))
                return EventKit.Reply(_ctx, session, cmd, Echo(param, Refused));
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, Echo(param, 0));
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Pays the board's assigned mission once its achievement is complete. The panel only shows when a mission id
    /// is set in the board's task, and nothing in the client says how the original server assigned those.
    /// </summary>
    private Task OnDailyMission(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int group = EventKit.Number(param, "nGroup"), id = EventKit.Number(param, "nTaskId");
        lock (player)
        {
            if (id <= 0 || player.GetTask(Group, DailyMissionBase + group) != id
                || !_ctx.Missions.AchieveOther.TryGetValue(id, out var mission)
                || EventKit.HasBit(player, mission.Group, mission.Id, 0))
                return EventKit.Reply(_ctx, session, cmd, Echo(param, Refused));
            var rewards = new RewardGrantResult();
            var changes = new List<KeyValuePair<uint, int>>();
            _ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, mission.Group, mission.Id, 0);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, Echo(param, 0));
        }
        return Task.CompletedTask;
    }

    /// <summary>A new event version starts with clean boards, which CheckActData requests when the versions differ.</summary>
    private Task OnDailyReset(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            for (int i = 1; i <= 5; i++)
            {
                EventKit.Write(player, changes, Group, DailyBoardBase + i, 0);
                EventKit.Write(player, changes, Group, DailyMissionBase + i, 0);
            }
            EventKit.Write(player, changes, Group, DailyVersionTask, EventKit.Number(param, "nVer"));
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["nRet"] = 0 });
        }
        return Task.CompletedTask;
    }

    private int Flip(ClientSession session, Player player, Board board, int task, int piece, out List<KeyValuePair<uint, int>> changes)
    {
        changes = new();
        if (piece < 1 || piece > PieceCount || EventKit.HasBit(player, Group, task, piece)) return Refused;
        if (player.GetMoney(board.Money) <= 0) return NoMoney;
        player.AddMoney(board.Money, -1);
        EventKit.SetBit(player, changes, Group, task, piece);
        GameNotify.PushMoney(session, player, board.Money);
        return 0;
    }

    private bool Claim(Player player, int task, Board board, int index, out RewardGrantResult rewards, out List<KeyValuePair<uint, int>> changes)
    {
        rewards = new();
        changes = new();
        var award = board.Awards[index - 1];
        int bit = AwardBit + index - 1;
        int flipped = Enumerable.Range(1, PieceCount).Count(i => EventKit.HasBit(player, Group, task, i));
        if (EventKit.HasBit(player, Group, task, bit) || flipped < award.Count) return false;
        _ctx.Rewards.Grant(player, award.Rewards, RewardSource.Extra, rewards);
        EventKit.SetBit(player, changes, Group, task, bit);
        return true;
    }
}
