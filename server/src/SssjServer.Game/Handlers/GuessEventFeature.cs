using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Rock-paper-scissors events: the anniversary tournament (430-433) and the camp battle (521-526). Each opponent is a
/// best-of-three where draws do not count, and both use the same bit layout for the current bout.
///
/// INFERRED: the client holds no rules for how many opponents a round has or how the camp battle scores, so a round is
/// six opponents in the tournament and three in the camp battle (the size of its task layout), and net score is wins
/// minus losses. Opponent throws follow RoundList and RoundRate as the client's own hint logic reads them.
/// </summary>
public sealed class GuessEventFeature : IGameFeature
{
    private const int Group = 84;
    private const int NotEnoughTries = 20045;
    private const int Rock = 1, Paper = 3;

    /// <summary>Task layout of one game. The two share a group, so the camp battle's tasks start at 301.</summary>
    private sealed record Game(string Folder, int TurnTask, int DailyTask, int JoinTask, int CurrentTask, int AwardTask)
    {
        public int DailyFree { get; set; }
        public int UseCost { get; set; } = 1;
        public int GoldMoney { get; set; }
        public int TicketMoney { get; set; }
        public List<(int Girl, int Camp)> Girls { get; } = new();
        public Dictionary<(int Round, int Girl), int> ForcedThrows { get; } = new();
        public Dictionary<int, (int Type, int Rate)> ThrowRules { get; } = new();
        public List<(int Type, int Index, int[] Limit, List<int[]> Items)> Awards { get; } = new();
    }

    private readonly Game Tournament = new("anniversaryguess", 2, 3, 4, 7, 31);
    private readonly Game CampBattle = new("guess", 302, 303, 304, 307, 331);

    // AnniversaryGuess.lua
    private const int TourGirlBase = 10, TourRoundRate = 9, TourRoundSize = 6;
    // Guess.lua
    private const int CampMine = 305, CampSide = 306, CampResults = 308, CampAllTurns = 309, CampMyGirlWins = 310,
        CampChoice = 334, CampDictatorData = 335, CampAntiData = 336, CampNetScore = 337, CampBestScore = 338, CampRoundSize = 3;

    private const string StateKey = "guessEvent";

    private GameFeatureContext _ctx = null!;

    public string Name => "Guess events (430-433, 521-526)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        Load(directory, Tournament);
        Load(directory, CampBattle);
        ctx.MapLuaCall(430, OnTournamentRefresh);
        ctx.MapLuaCall(431, (s, c, p) => OnFist(s, c, p, Tournament));
        ctx.MapLuaCall(432, (s, c, p) => OnAward(s, c, p, Tournament));
        ctx.MapLuaCall(433, OnTournamentLog);
        ctx.MapLuaCall(521, OnSelectCamp);
        ctx.MapLuaCall(522, OnChangeMembers);
        ctx.MapLuaCall(523, OnCampRefresh);
        ctx.MapLuaCall(524, (s, c, p) => OnFist(s, c, p, CampBattle));
        ctx.MapLuaCall(525, (s, c, p) => OnAward(s, c, p, CampBattle));
        ctx.MapLuaCall(526, OnCampLog);
    }

    private static void Load(string directory, Game game)
    {
        SettingTable? Table(string name) => SettingTable.Load(Path.Combine(directory, "_scoped", "activities", game.Folder, name + ".txt"));
        if (Table("timelist") is { } time && time.Rows.Count > 0)
        {
            var row = time.Rows[0];
            game.DailyFree = time.Int(row, "DailyNum");
            game.UseCost = Math.Max(1, time.Int(row, "UseNum"));
            game.GoldMoney = time.Int(row, "GoldID");
            game.TicketMoney = time.Int(row, "MoneyID");
        }
        if (Table("girlconfig") is { } girls)
            foreach (var row in girls.Rows)
                if (girls.Int(row, "girlId") > 0)
                    game.Girls.Add((girls.Int(row, "girlId"), girls.Int(row, "camp")));
        if (Table("roundlist") is { } rounds)
            foreach (var row in rounds.Rows)
                foreach (int girl in TwentyFirstTowerTable.Numbers(rounds.Str(row, "Girls")))
                    game.ForcedThrows[(rounds.Int(row, "Round"), girl)] = rounds.Int(row, "Fist");
        if (Table("roundrate") is { } rates)
            foreach (var row in rates.Rows)
                game.ThrowRules[rates.Int(row, "GirlId")] = (rates.Int(row, "Type"), rates.Int(row, "Rate"));
        if (Table("awardlist") is { } awards)
            foreach (var row in awards.Rows)
                game.Awards.Add((awards.Int(row, "Type"), awards.Int(row, "Index"),
                    TwentyFirstTowerTable.Numbers(awards.Str(row, "LimitNum")), ChapterTable.ParseTuples(awards.Str(row, "Item"))));
    }

    // ================================================================ bout state

    private static int Encode(int girl, int pos, int count, int[] results)
        => girl << 20 | pos << 16 | count << 6 | results[2] << 4 | results[1] << 2 | results[0];

    private static (int Girl, int Pos, int Count, int[] Results) Decode(int value)
        => (value >> 20 & 0xFFF, value >> 16 & 0xF, value >> 6 & 0x3FF, new[] { value & 3, value >> 2 & 3, value >> 4 & 3 });

    /// <summary>Per-player state with no task of its own: the round's opponents and the record screens' history.</summary>
    private static JsonObject State(Player player, Game game)
    {
        if (player.Extra.GetValueOrDefault(StateKey + game.Folder) is not JsonObject state)
            player.Extra[StateKey + game.Folder] = state = new JsonObject { ["girls"] = new JsonArray(), ["log"] = new JsonArray() };
        return state;
    }

    /// <summary>The free tries reset every day at 4 AM, as on every other daily counter in this game.</summary>
    private void ResetDaily(Player player, Game game, List<KeyValuePair<uint, int>> changes)
    {
        string day = DateTimeOffset.Now.AddHours(-4).ToString("yyyyMMdd");
        var state = State(player, game);
        if (state["day"]?.GetValue<string>() == day) return;
        state["day"] = day;
        EventKit.Write(player, changes, Group, game.DailyTask, 0);
    }

    private bool SpendTry(ClientSession session, Player player, Game game, List<KeyValuePair<uint, int>> changes)
    {
        int used = player.GetTask(Group, game.DailyTask);
        if (used + game.UseCost <= game.DailyFree)
        {
            EventKit.Write(player, changes, Group, game.DailyTask, used + game.UseCost);
            return true;
        }
        if (player.GetMoney(game.TicketMoney) < game.UseCost) return false;
        player.AddMoney(game.TicketMoney, -game.UseCost);
        GameNotify.PushMoney(session, player, game.TicketMoney);
        return true;
    }

    /// <summary>
    /// GetRandomFist: a RoundList row fixes the throw. RoundRate type 2 throws what the hint said, type 3 anything but
    /// that, and type 4 refuses to throw at all, which the client shows as the player winning by timeout.
    /// </summary>
    private static int OpponentThrow(Game game, int round, int girl, int hint, out bool refuses)
    {
        refuses = false;
        if (game.ForcedThrows.TryGetValue((round, girl), out int forced)) return forced;
        int random = Random.Shared.Next(Rock, Paper + 1);
        if (!game.ThrowRules.TryGetValue(girl, out var rule)) return random;
        if (rule.Type == 2 && hint is >= Rock and <= Paper) return hint;
        if (rule.Type == 3 && hint is >= Rock and <= Paper) return hint % 3 + 1;
        if (rule.Type == 4 && Random.Shared.Next(100) < rule.Rate) refuses = true;
        return random;
    }

    /// <summary>0 draw, 1 the player lost, 2 the player won. Same comparison as UI_GuessFight.OnReceiveUpdate.</summary>
    private static int Outcome(int player, int server)
        => player == server % 3 + 1 ? 1 : player == (server + 1) % 3 + 1 ? 2 : 0;

    private Task OnFist(ClientSession session, int cmd, JsonNode? param, Game game)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var request = param as JsonObject;
        int type = EventKit.Number(param, "nType");
        bool gold = request?["bGold"] is JsonValue g && g.TryGetValue<bool>(out bool usedGold) && usedGold;
        bool timedOut = request?["bTimeOut"] is JsonValue t && t.TryGetValue<bool>(out bool expired) && expired;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            ResetDaily(player, game, changes);
            var (girl, pos, count, results) = Decode(player.GetTask(Group, game.CurrentTask));
            if (girl <= 0 || !SpendTry(session, player, game, changes))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = NotEnoughTries });
            if (gold)
            {
                if (player.GetMoney(game.GoldMoney) < 1) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = NotEnoughTries });
                player.AddMoney(game.GoldMoney, -1);
                GameNotify.PushMoney(session, player, game.GoldMoney);
            }
            var reply = new JsonObject { ["Error"] = 0 };
            int outcome;
            if (timedOut && type == 0)
            {
                outcome = 1;
                reply["bTimeOut"] = true;
            }
            else
            {
                // A throw that loses to the player's: rock(1) beats scissors(2) beats paper(3) beats rock.
                int server = gold ? type % 3 + 1 : OpponentThrow(game, count + 1, girl, EventKit.Number(param, "nFist"), out bool refuses) is var thrown && refuses ? 0 : thrown;
                if (server == 0)
                {
                    outcome = 2;
                    reply["bTimeOut"] = true;
                    reply["nType"] = type;
                }
                else
                {
                    outcome = Outcome(type, server);
                    reply["nType"] = type;
                    reply["ServerFist"] = server;
                    if (gold) reply["bGold"] = true;
                }
            }
            RecordBout(player, game, girl, pos, count, results, outcome, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, reply);
        }
        return Task.CompletedTask;
    }

    private void RecordBout(Player player, Game game, int girl, int pos, int count, int[] results, int outcome, List<KeyValuePair<uint, int>> changes)
    {
        count++;
        if (outcome != 0)
        {
            int slot = Array.IndexOf(results, 0);
            if (slot >= 0) results[slot] = outcome;
        }
        int wins = results.Count(r => r == 2), losses = results.Count(r => r == 1);
        bool finished = wins >= 2 || losses >= 2 || count >= 3 + results.Count(r => r == 0) * 10;
        if (!finished)
        {
            EventKit.Write(player, changes, Group, game.CurrentTask, Encode(girl, pos, count, results));
            return;
        }
        bool won = wins >= 2;
        EventKit.Write(player, changes, Group, game.JoinTask, player.GetTask(Group, game.JoinTask) + 1);
        if (ReferenceEquals(game, Tournament)) FinishTournamentGirl(player, girl, pos, results, won, changes);
        else FinishCampGirl(player, girl, pos, results, won, changes);
    }

    // ================================================================ anniversary tournament

    private int GirlSlot(Game game, int girl) => game.Girls.FindIndex(g => g.Girl == girl) + 1;

    private void StartTournamentRound(Player player, List<KeyValuePair<uint, int>> changes)
    {
        var picks = Tournament.Girls.Select(g => g.Girl).OrderBy(_ => Random.Shared.Next()).Take(TourRoundSize).ToArray();
        var state = State(player, Tournament);
        state["girls"] = new JsonArray(picks.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray());
        EventKit.Write(player, changes, Group, Tournament.TurnTask, player.GetTask(Group, Tournament.TurnTask) + 1);
        EventKit.Write(player, changes, Group, Tournament.CurrentTask, Encode(picks.FirstOrDefault(), 1, 0, new int[3]));
        foreach (int girl in picks)
        {
            int slot = TourGirlBase + GirlSlot(Tournament, girl);
            EventKit.Write(player, changes, Group, slot, player.GetTask(Group, slot) + (1 << 16));
        }
        var log = (JsonArray)state["log"]!;
        log.Add(new JsonObject { ["nRound"] = player.GetTask(Group, Tournament.TurnTask), ["tbGirls"] = new JsonArray() });
    }

    private void FinishTournamentGirl(Player player, int girl, int pos, int[] results, bool won, List<KeyValuePair<uint, int>> changes)
    {
        var state = State(player, Tournament);
        var log = (JsonArray)state["log"]!;
        if (log.LastOrDefault() is JsonObject entry)
            ((JsonArray)entry["tbGirls"]!).Add(new JsonObject { ["Girlid"] = girl, ["tbResult"] = new JsonArray(results[0], results[1], results[2]) });
        int slot = TourGirlBase + GirlSlot(Tournament, girl);
        if (won) EventKit.Write(player, changes, Group, slot, player.GetTask(Group, slot) + 1);
        var girls = ChallengeTowerFeature.ReadPositional(state["girls"]);
        if (pos < girls.Count)
        {
            EventKit.Write(player, changes, Group, Tournament.CurrentTask, Encode(girls[pos], pos + 1, 0, new int[3]));
            return;
        }
        // AllRoundRate sums each finished round's bout win rate as a percentage.
        var finishedGirls = (log.LastOrDefault() as JsonObject)?["tbGirls"] as JsonArray ?? new JsonArray();
        var outcomes = finishedGirls.SelectMany(g => ChallengeTowerFeature.ReadPositional(g?["tbResult"])).ToArray();
        int bouts = outcomes.Count(r => r != 0);
        if (bouts > 0) EventKit.Write(player, changes, Group, TourRoundRate, player.GetTask(Group, TourRoundRate) + outcomes.Count(r => r == 2) * 100 / bouts);
        StartTournamentRound(player, changes);
    }

    private Task OnTournamentRefresh(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            ResetDaily(player, Tournament, changes);
            if (player.GetTask(Group, Tournament.TurnTask) == 0) StartTournamentRound(player, changes);
            var list = new JsonArray();
            foreach (int girl in ChallengeTowerFeature.ReadPositional(State(player, Tournament)["girls"]))
                list.Add(new JsonArray(girl));
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0, ["tbGirl"] = list });
        }
        return Task.CompletedTask;
    }

    private Task OnTournamentLog(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
            return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = 0, ["tbLog"] = State(player, Tournament)["log"]!.DeepClone() });
    }

    // ================================================================ camp battle

    private int[] CampGirls(int camp) => CampBattle.Girls.Where(g => g.Camp == camp).Select(g => g.Girl).ToArray();

    /// <summary>GetCurMember: three girls per task, each at bits i*10+2..i*10+9, with her result in the two bits below.</summary>
    private static int PackMembers(IReadOnlyList<int> girls)
    {
        int value = 0;
        for (int i = 0; i < girls.Count && i < CampRoundSize; i++) value |= girls[i] << (i * 10 + 2);
        return value;
    }

    private static int[] UnpackMembers(int value)
        => Enumerable.Range(0, CampRoundSize).Select(i => value >> (i * 10 + 2) & 0xFF).ToArray();

    private void StartCampRound(Player player, List<KeyValuePair<uint, int>> changes)
    {
        int camp = player.GetTask(Group, CampChoice);
        var mine = UnpackMembers(player.GetTask(Group, CampMine));
        if (mine.All(g => g == 0)) mine = CampGirls(camp).OrderBy(_ => Random.Shared.Next()).Take(CampRoundSize).ToArray();
        var side = CampGirls(camp == 1 ? 2 : 1).OrderBy(_ => Random.Shared.Next()).Take(CampRoundSize).ToArray();
        EventKit.Write(player, changes, Group, CampMine, PackMembers(mine));
        EventKit.Write(player, changes, Group, CampSide, PackMembers(side));
        EventKit.Write(player, changes, Group, CampResults, 0);
        EventKit.Write(player, changes, Group, CampBattle.TurnTask, player.GetTask(Group, CampBattle.TurnTask) + 1);
        EventKit.Write(player, changes, Group, CampBattle.CurrentTask, Encode(side.FirstOrDefault(), 1, 0, new int[3]));
        var log = (JsonArray)State(player, CampBattle)["log"]!;
        log.Add(new JsonObject { ["nTurn"] = player.GetTask(Group, CampBattle.TurnTask), ["Score"] = 0, ["RoundData"] = new JsonArray() });
    }

    private void FinishCampGirl(Player player, int girl, int pos, int[] results, bool won, List<KeyValuePair<uint, int>> changes)
    {
        int[] mine = UnpackMembers(player.GetTask(Group, CampMine)), side = UnpackMembers(player.GetTask(Group, CampSide));
        int shift = (pos - 1) * 10;
        int mark = won ? 2 : 1;
        EventKit.Write(player, changes, Group, CampMine, player.GetTask(Group, CampMine) | mark << shift);
        EventKit.Write(player, changes, Group, CampSide, player.GetTask(Group, CampSide) | (won ? 1 : 2) << shift);
        // Wins sit in the high half of the round result, losses in the low half.
        EventKit.Write(player, changes, Group, CampResults, player.GetTask(Group, CampResults) + (won ? 1 << 16 : 1));
        int myGirl = pos - 1 < mine.Length ? mine[pos - 1] : 0;
        int mySlot = GirlSlot(CampBattle, myGirl);
        if (won && mySlot > 0) EventKit.Write(player, changes, Group, CampMyGirlWins + mySlot, player.GetTask(Group, CampMyGirlWins + mySlot) + 1);

        var log = State(player, CampBattle)["log"] as JsonArray;
        if (log?.LastOrDefault() is JsonObject entry)
        {
            ((JsonArray)entry["RoundData"]!).Add(new JsonObject
            {
                ["nPos"] = pos, ["nMyGirlId"] = myGirl, ["nSideGirlId"] = girl,
                ["nCurValue"] = results[2] << 4 | results[1] << 2 | results[0], ["tbGold"] = new JsonObject(),
            });
            entry["Score"] = (entry["Score"]?.GetValue<int>() ?? 0) + (won ? 1 : -1);
        }
        if (pos < CampRoundSize)
        {
            EventKit.Write(player, changes, Group, CampBattle.CurrentTask, Encode(side[pos], pos + 1, 0, new int[3]));
            return;
        }
        int resultValue = player.GetTask(Group, CampResults);
        int roundWins = resultValue >> 16, roundLosses = resultValue & 0xFFFF;
        bool roundWon = roundWins > roundLosses;
        EventKit.Write(player, changes, Group, CampAllTurns, player.GetTask(Group, CampAllTurns) + (roundWon ? 1 << 16 : 1));
        int net = player.GetTask(Group, CampNetScore) + roundWins - roundLosses;
        EventKit.Write(player, changes, Group, CampNetScore, net);
        if (net > player.GetTask(Group, CampBestScore)) EventKit.Write(player, changes, Group, CampBestScore, net);
        // tbCamp: join count in bits 0-10, wins in bits 11-21.
        int dataTask = player.GetTask(Group, CampChoice) == 1 ? CampDictatorData : CampAntiData;
        EventKit.Write(player, changes, Group, dataTask, player.GetTask(Group, dataTask) + 1 + (roundWon ? 1 << 11 : 0));
        EventKit.Write(player, changes, Group, CampMine, PackMembers(mine));
        StartCampRound(player, changes);
    }

    private Task OnSelectCamp(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int camp = EventKit.Number(param, "nCamp");
        lock (player)
        {
            if (camp is not (1 or 2)) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = NotEnoughTries });
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, Group, CampChoice, camp);
            // Changing sides starts the team over from the new camp's girls.
            EventKit.Write(player, changes, Group, CampMine, 0);
            StartCampRound(player, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    private Task OnChangeMembers(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var wanted = ChallengeTowerFeature.ReadPositional(param);
            if (wanted.Count != CampRoundSize || wanted.Any(g => g <= 0))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = NotEnoughTries });
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, Group, CampMine, PackMembers(wanted));
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    private Task OnCampRefresh(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            ResetDaily(player, CampBattle, changes);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>GetRecordDataRsp iterates the reply itself as the list of rounds.</summary>
    private Task OnCampLog(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
            return EventKit.Reply(_ctx, session, cmd, State(player, CampBattle)["log"]!.DeepClone());
    }

    // ================================================================ rewards

    /// <summary>Type 1 needs bouts played, type 2 wins with one girl, type 3 wins with every girl (CheckCanAward).</summary>
    private Task OnAward(ClientSession session, int cmd, JsonNode? param, Game game)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "nType"), index = EventKit.Number(param, "nIndex");
        lock (player)
        {
            var award = game.Awards.FirstOrDefault(a => a.Type == type && a.Index == index);
            int task = game.AwardTask + type - 1;
            if (award.Items is null || award.Limit.Length == 0 || EventKit.HasBit(player, Group, task, index) || !CanClaim(player, game, award.Type, award.Limit))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = NotEnoughTries });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, award.Items, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, Group, task, index);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Error"] = 0, ["Items"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private bool CanClaim(Player player, Game game, int type, int[] limit)
    {
        int Wins(int girl)
        {
            int slot = GirlSlot(game, girl);
            if (slot <= 0) return 0;
            return ReferenceEquals(game, Tournament)
                ? player.GetTask(Group, TourGirlBase + slot) & 0xFFFF
                : player.GetTask(Group, CampMyGirlWins + slot);
        }
        return type switch
        {
            1 => player.GetTask(Group, game.JoinTask) >= limit[0],
            2 => limit.Length >= 2 && Wins(limit[0]) >= limit[1],
            3 => game.Girls.Where(g => g.Camp > 0 || ReferenceEquals(game, Tournament)).All(g => Wins(g.Girl) >= limit[0]),
            _ => false,
        };
    }
}
