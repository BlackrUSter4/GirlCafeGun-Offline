using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Richman board game (500-504). The player rolls dice, walks a 71-grid loop and triggers whatever each grid holds.
///
/// INFERRED, all of the board: the original server held each grid's function and reward, and the client only ships
/// the function list (GridFunc.txt), the girls and their questions. The layout and reward amounts below are mine,
/// chosen to use every function. Protocol shape and the question data are the client's.
/// </summary>
public sealed class MonopolyFeature : IGameFeature
{
    private const int GridCount = 71;
    private const int DailyFreeRolls = 3; // MonoPoly.JoinActRsp fixes FreeNumCount at 3
    private const int DiceCount = 2;
    private const int FlipCards = 12;
    private const string StateKey = "monopoly";
    private const int ErrNotEnough = 1;

    // GridFunc.txt
    private const int FnQuestion = 1, FnReward = 2, FnFlip = 3, FnNextGrid = 4, FnBuff = 5, FnNeighbor = 6;
    // gridState: 0 untouched, 1 triggered and unfinished, 2 done.
    private const int Untouched = 0, Triggered = 1, Done = 2;

    private static readonly int[][] SmallRewards =
    {
        new[] { 15, 1, 1, 1, 5000 }, new[] { 15, 2, 1, 1, 20 }, new[] { 7, 1, 1, 3, 1 }, new[] { 7, 9, 1, 1, 2 },
    };

    private sealed class Board
    {
        public string Day { get; set; } = "";
        public int FreeRolls { get; set; }
        public int Loop { get; set; }
        public int Pos { get; set; }
        public List<int> Dice { get; set; } = new();
        public int[] Funcs { get; set; } = Array.Empty<int>();
        public int[] States { get; set; } = Array.Empty<int>();
        public int[] Rewards { get; set; } = Array.Empty<int>();
        public Dictionary<int, int> GirlTriggers { get; set; } = new();
        public Dictionary<int, int> GirlPassed { get; set; } = new();
        public int[][] FlipRewards { get; set; } = Array.Empty<int[]>();
    }

    private GameFeatureContext _ctx = null!;
    private readonly List<(int Girl, int[] Questions)> _girls = new();
    private readonly Dictionary<int, int> _answers = new();

    public string Name => "Richman board game (500-504)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "RichManGirlInfo.txt")) is { } girls)
            foreach (var row in girls.Rows)
                if (girls.Int(row, "GirlId") > 0)
                    _girls.Add((girls.Int(row, "GirlId"), TwentyFirstTowerTable.Numbers(girls.Str(row, "QuestionList"))));
        if (SettingTable.Load(Path.Combine(directory, "QuestionInfo.txt")) is { } questions)
            foreach (var row in questions.Rows)
                _answers[questions.Int(row, "Id")] = questions.Int(row, "RealAnswer");
        ctx.MapLuaCall(500, OnGetData);
        ctx.MapLuaCall(501, OnRoll);
        ctx.MapLuaCall(502, OnChoose);
        ctx.MapLuaCall(503, OnFlip);
        ctx.MapLuaCall(504, OnAnswer);
    }

    private static Board Read(Player player)
    {
        Board? board = null;
        try { board = player.Extra.GetValueOrDefault(StateKey)?.Deserialize<Board>(); }
        catch (JsonException) { }
        board ??= new Board();
        if (board.Funcs.Length != GridCount) NewLoop(board);
        string day = DateTimeOffset.Now.AddHours(-4).ToString("yyyyMMdd");
        if (board.Day != day)
        {
            board.Day = day;
            board.FreeRolls = DailyFreeRolls;
        }
        return board;
    }

    private static void Save(Player player, Board board) => player.Extra[StateKey] = JsonSerializer.SerializeToNode(board);

    /// <summary>Each loop deals a new board: mostly rewards, with the special grids spread at fixed intervals.</summary>
    private static void NewLoop(Board board)
    {
        board.Loop++;
        board.Pos = 0;
        board.Dice.Clear();
        board.Funcs = new int[GridCount];
        board.States = new int[GridCount];
        board.Rewards = new int[GridCount];
        for (int i = 0; i < GridCount; i++)
        {
            board.Funcs[i] = (i + 1) switch
            {
                var n when n % 12 == 0 => FnFlip,
                var n when n % 9 == 0 => FnQuestion,
                var n when n % 17 == 0 => FnNextGrid,
                var n when n % 19 == 0 => FnNeighbor,
                var n when n % 23 == 0 => FnBuff,
                _ => FnReward,
            };
            board.Rewards[i] = Random.Shared.Next(SmallRewards.Length);
        }
    }

    private static JsonArray Items(params int[][] tuples)
        => new(tuples.Select(t => (JsonNode?)new JsonArray(t.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray())).ToArray());

    private JsonObject GridInfo(Board board, int pos)
    {
        int func = board.Funcs[pos];
        var info = new JsonObject
        {
            ["gridType"] = func, ["gridID"] = pos + 1, ["funcType"] = func, ["pos"] = pos + 1, ["gridState"] = board.States[pos],
        };
        info["param"] = func switch
        {
            FnReward => new JsonObject { ["rewards"] = Items(SmallRewards[board.Rewards[pos]]) },
            FnNeighbor => new JsonObject { ["rewards"] = Items(SmallRewards[board.Rewards[pos]]) },
            FnQuestion => QuestionParam(board, pos),
            FnNextGrid => new JsonObject { ["activeGrids"] = new JsonArray(pos + 1, Math.Min(pos + 2, GridCount)) },
            _ => new JsonObject(),
        };
        return info;
    }

    /// <summary>A girl asks the questions in QuestionList in order, so the grid's question is fixed by how often she was met.</summary>
    private JsonObject QuestionParam(Board board, int pos)
    {
        if (_girls.Count == 0) return new JsonObject();
        var (girl, questions) = _girls[pos % _girls.Count];
        int asked = board.GirlTriggers.GetValueOrDefault(girl);
        return new JsonObject { ["girlId"] = girl, ["QId"] = questions.Length == 0 ? 0 : questions[asked % questions.Length] };
    }

    private JsonObject ActInfo(Board board)
    {
        var grids = new JsonArray();
        for (int i = 0; i < GridCount; i++) grids.Add(GridInfo(board, i));
        var dice = new JsonArray();
        for (int i = 0; i < board.Dice.Count; i++) dice.Add(new JsonObject { ["pos"] = i + 1, ["num"] = board.Dice[i] });
        var girls = new JsonArray();
        foreach (var (girl, count) in board.GirlTriggers)
            girls.Add(new JsonObject { ["girlId"] = girl, ["triggerNum"] = count, ["passed"] = board.GirlPassed.GetValueOrDefault(girl) });
        return new JsonObject
        {
            ["tbGridInfoList"] = grids, ["nCurLoop"] = board.Loop, ["nCurPos"] = board.Pos, ["tbDiceList"] = dice,
            ["PlayTimesInfo"] = Times(board), ["girlEvent"] = girls,
        };
    }

    private static JsonObject Times(Board board) => new()
    {
        ["DailyFreeTimes"] = board.FreeRolls, ["TaskGetTimes"] = 0, ["ShopBuyTimes"] = 0, ["VigorConvertTimes"] = 0,
    };

    private Task OnGetData(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var board = Read(player);
            Save(player, board);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["Error"] = 0, ["actInfo"] = ActInfo(board) });
        }
        return Task.CompletedTask;
    }

    private Task OnRoll(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var board = Read(player);
            if (board.FreeRolls <= 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrNotEnough, ["PlayTimesInfo"] = Times(board) });
            board.FreeRolls--;
            board.Dice = Enumerable.Range(0, DiceCount).Select(_ => Random.Shared.Next(1, 7)).ToList();
            Save(player, board);
            _ctx.Store.Save(player);
            var dice = new JsonArray();
            for (int i = 0; i < board.Dice.Count; i++) dice.Add(new JsonObject { ["pos"] = i + 1, ["num"] = board.Dice[i] });
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["Error"] = 0, ["DiceList"] = dice, ["PlayTimesInfo"] = Times(board) });
        }
        return Task.CompletedTask;
    }

    /// <summary>Plain rewards pay on arrival, as the client opens their popup straight from GridInfo.param.</summary>
    private Task OnChoose(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int index = EventKit.Number(param, "idx");
        lock (player)
        {
            var board = Read(player);
            if (index < 1 || index > board.Dice.Count)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrNotEnough });
            int steps = board.Dice[index - 1];
            board.Dice.Clear();
            int target = board.Pos + steps;
            var reply = new JsonObject { ["Error"] = 0 };
            RewardGrantResult? rewards = null;
            if (target >= GridCount)
            {
                // Walking off the end deals the next loop. The client resets the map from actInfo.
                NewLoop(board);
                reply["GridInfo"] = new JsonObject { ["pos"] = 0 };
                reply["CurPos"] = 0;
                reply["actInfo"] = ActInfo(board);
            }
            else
            {
                board.Pos = target;
                int grid = target;
                reply["CurPos"] = target + 1;
                bool first = board.States[grid] == Untouched;
                int func = board.Funcs[grid];
                board.States[grid] = func is FnQuestion or FnFlip ? Triggered : Done;
                if (first && func is FnReward or FnNeighbor)
                {
                    rewards = new RewardGrantResult();
                    _ctx.Rewards.Grant(player, new[] { SmallRewards[board.Rewards[grid]] }, RewardSource.Extra, rewards);
                }
                reply["GridInfo"] = GridInfo(board, grid);
                if (func == FnNextGrid && grid + 1 < GridCount)
                {
                    board.Pos = grid + 1;
                    board.States[grid + 1] = board.Funcs[grid + 1] == FnReward ? Done : board.States[grid + 1];
                }
            }
            Save(player, board);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, reply);
        }
        return Task.CompletedTask;
    }

    /// <summary>UI_RichmanShow fills each card from tbAward by position and shows tbAward[idx] as what was won.</summary>
    private Task OnFlip(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int index = EventKit.Number(param, "idx");
        lock (player)
        {
            var board = Read(player);
            if (board.States[board.Pos] != Triggered || board.Funcs[board.Pos] != FnFlip || index < 1 || index > FlipCards)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrNotEnough });
            var cards = Enumerable.Range(0, FlipCards).Select(_ => SmallRewards[Random.Shared.Next(SmallRewards.Length)]).ToArray();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, new[] { cards[index - 1] }, RewardSource.Extra, rewards);
            board.States[board.Pos] = Done;
            Save(player, board);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, new JsonObject { ["Error"] = 0, ["tbAward"] = Items(cards) });
        }
        return Task.CompletedTask;
    }

    private Task OnAnswer(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int girl = EventKit.Number(param, "girlId"), question = EventKit.Number(param, "QId"), answer = EventKit.Number(param, "Aidx");
        lock (player)
        {
            var board = Read(player);
            board.GirlTriggers[girl] = board.GirlTriggers.GetValueOrDefault(girl) + 1;
            if (board.Funcs[board.Pos] == FnQuestion) board.States[board.Pos] = Done;
            bool correct = _answers.GetValueOrDefault(question) == answer;
            var reply = new JsonObject { ["Error"] = 0, ["AnsResult"] = correct ? 1 : 0 };
            RewardGrantResult? rewards = null;
            if (correct)
            {
                board.GirlPassed[girl] = board.GirlPassed.GetValueOrDefault(girl) + 1;
                rewards = new RewardGrantResult();
                _ctx.Rewards.Grant(player, new[] { SmallRewards[1] }, RewardSource.Extra, rewards);
                reply["tbAward"] = EventKit.Tuples(rewards);
            }
            Save(player, board);
            EventKit.Commit(_ctx, session, player, cmd, rewards, null, reply);
        }
        return Task.CompletedTask;
    }
}
