using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Season-long events: battle pass (340-344), fan support vote (354-357) and concert collection exchange (380-384).
///
/// Several tables here share file names with other events, so they are read from extracted/setting/_scoped.
/// There are no other players, so the vote ranks only this player's own votes, the exchange board stays empty, and
/// the paid pass unlocks without a charge.
/// </summary>
public sealed class SeasonEventsFeature : IGameFeature
{
    // BattlePass.lua
    private const int PassGroup = 66, PassOwnedTask = 1, PassExpTask = 2, PassRefreshTask = 3, PassWeekExpTask = 4,
        PassNormalBase = 100, PassAdvanceBase = 200, PassBitsPerTask = 30;
    private const int PassMissionGroup = MissionTable.OtherGroup;
    private const int IbGroup = 42;
    /// <summary>Battle pass mission rewards pay exp as this item: 310 of it for a 310 exp mission.</summary>
    private static readonly int[] PassExpItem = { 15, 52, 1, 1 };

    // GirlSupport.lua
    private const int FanGroup = 69, FanIdTask = 1, FanAwardTask = 2, FanPointTask = 3, FanGirlBase = 10;
    private const int FanCoinMoney = 36; // MoneyLogic.eMoneyType_FanEventCoin

    // ConcertExchange.lua
    private const int ConcertGroup = 74, ConcertIdTask = 1, ConcertAwardsTask = 2, ConcertListingFlags = 3, ConcertListingCount = 4;

    private const int ErrGeneric = 20045; // "not in the event window", which every screen here knows how to show

    private sealed record PassSeason(int Id, int ExpPerLevel, int ExpPerDiamond);
    private sealed record PassLevel(List<int[]> Normal, List<int[]> Advance);
    private sealed record PassGift(int Id, List<int[]> Items);
    private sealed record FanSeason(int Id, int[] Girls);
    private sealed record ConcertSeason(int Id, int[][] Collections, List<int[]> Award);

    private GameFeatureContext _ctx = null!;
    private PassSeason? _season;
    private readonly Dictionary<int, PassLevel> _passLevels = new();
    private readonly Dictionary<int, PassGift> _passGifts = new();
    private readonly Dictionary<int, FanSeason> _fanSeasons = new();
    private readonly Dictionary<(int Season, int Index), (int Points, List<int[]> Rewards)> _fanAwards = new();
    private readonly Dictionary<int, ConcertSeason> _concerts = new();
    private readonly Dictionary<(int Season, int Step), List<int[]>> _concertRewards = new();

    public string Name => "Season events (battle pass, fan support, concert exchange)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        LoadPass(directory);
        LoadFan(directory);
        LoadConcert(directory);
        ctx.MapLuaCall(340, OnPassBuy);
        ctx.MapLuaCall(341, OnPassBuyLevel);
        ctx.MapLuaCall(342, OnPassAward);
        ctx.MapLuaCall(343, OnPassMission);
        ctx.MapLuaCall(344, OnPassRefresh);
        ctx.MapLuaCall(354, OnFanList);
        ctx.MapLuaCall(355, OnFanSupport);
        ctx.MapLuaCall(356, OnFanReward);
        ctx.MapLuaCall(357, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonObject { ["tbList"] = new JsonArray() }));
        ctx.MapLuaCall(380, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonObject { ["tbData"] = new JsonArray() }));
        ctx.MapLuaCall(381, OnConcertListing);
        ctx.MapLuaCall(382, OnConcertAward);
        ctx.MapLuaCall(383, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonObject { ["tbList"] = new JsonArray() }));
        ctx.MapLuaCall(384, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonObject()));
        if (ctx.Options.AllEventsOpen) ctx.OnPlayerPrepare((session, player) => SeedSeasons(player));
    }

    /// <summary>Both screens look up their season by a task the server writes. One season runs per calendar week.</summary>
    private void SeedSeasons(Player player)
    {
        long week = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / (7 * 86400);
        if (_fanSeasons.Count > 0) player.SetTask(FanGroup, FanIdTask, _fanSeasons.Keys.OrderBy(i => i).ElementAt((int)(week % _fanSeasons.Count)));
        if (_concerts.Count > 0) player.SetTask(ConcertGroup, ConcertIdTask, _concerts.Keys.OrderBy(i => i).ElementAt((int)(week % _concerts.Count)));
    }

    // ================================================================ battle pass

    private void LoadPass(string directory)
    {
        // AllEventsOpenLua leaves only the newest season open on the client, so that is the one served here too.
        var seasons = SettingTable.Load(Path.Combine(directory, "_scoped", "battlepass", "timelist.txt"));
        if (seasons is null || seasons.Rows.Count == 0) return;
        var newest = seasons.Rows.Where(r => seasons.Int(r, "ID") > 0).MaxBy(r => seasons.Int(r, "ID"));
        if (newest is null) return;
        _season = new(seasons.Int(newest, "ID"), Math.Max(1, seasons.Int(newest, "ExpStep", 100)), Math.Max(1, seasons.Int(newest, "ExPerMoney", 5)));
        if (SettingTable.Load(Path.Combine(directory, "_scoped", "battlepass", "levellist.txt")) is { } levels)
            foreach (var row in levels.Rows)
                if (levels.Int(row, "ID") == _season.Id)
                    _passLevels[levels.Int(row, "Level")] = new(ChapterTable.ParseTuples(levels.Str(row, "NormalAward")),
                        ChapterTable.ParseTuples(levels.Str(row, "AdvanceAward")));
        if (SettingTable.Load(Path.Combine(directory, "BattlePassIB.txt")) is { } gifts)
            foreach (var row in gifts.Rows)
                if (gifts.Int(row, "BPID") == _season.Id)
                    _passGifts[gifts.Int(row, "BpType")] = new(gifts.Int(row, "Id"), ToTuples(gifts.Str(row, "ServerItem1")));
    }

    /// <summary>ServerItem columns use commas inside one tuple, unlike the usual dashes.</summary>
    private static List<int[]> ToTuples(string cell)
    {
        var numbers = TwentyFirstTowerTable.Numbers(cell);
        return numbers.Length >= 5 ? new() { numbers } : new();
    }

    /// <summary>Type 0 is the basic pass, 1 the advanced and 2 a gift. Owned means Id_Task equals the running season.</summary>
    private Task OnPassBuy(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player || _season is null) return Task.CompletedTask;
        int type = EventKit.Number(param, "Id");
        lock (player)
        {
            if (!_passGifts.TryGetValue(type, out var gift)) return PassReply(session, cmd, ErrGeneric);
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, gift.Items, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, IbGroup, gift.Id, player.GetTask(IbGroup, gift.Id) + 1);
            EventKit.Write(player, changes, PassGroup, PassOwnedTask, _season.Id);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>UI_BattlePassBuyLevel: exp to the next level plus whole levels, at ExPerMoney exp per diamond.</summary>
    private Task OnPassBuyLevel(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player || _season is null) return Task.CompletedTask;
        int levels = EventKit.Number(param, "Level");
        lock (player)
        {
            int exp = player.GetTask(PassGroup, PassExpTask);
            int toNext = _season.ExpPerLevel - exp % _season.ExpPerLevel;
            int gain = toNext + (levels - 1) * _season.ExpPerLevel;
            int cost = (int)Math.Ceiling(gain / (double)_season.ExpPerDiamond);
            if (levels < 1 || player.GetMoney(MoneyType.FreeDiamond) < cost) return PassReply(session, cmd, ErrGeneric);
            player.AddMoney(MoneyType.FreeDiamond, -cost);
            GameNotify.PushMoney(session, player, MoneyType.FreeDiamond);
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, PassGroup, PassExpTask, exp + gain);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    private Task OnPassAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player || _season is null) return Task.CompletedTask;
        lock (player)
        {
            int level = player.GetTask(PassGroup, PassExpTask) / _season.ExpPerLevel;
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            void Claim(string field, int taskBase, Func<PassLevel, List<int[]>> pick)
            {
                if ((param as JsonObject)?[field] is not JsonArray wanted) return;
                foreach (int id in ChallengeTowerFeature.ReadPositional(wanted))
                {
                    int task = taskBase + id / PassBitsPerTask, bit = id % PassBitsPerTask;
                    if (id > level || !_passLevels.TryGetValue(id, out var row) || EventKit.HasBit(player, PassGroup, task, bit)) continue;
                    _ctx.Rewards.Grant(player, pick(row), RewardSource.Extra, rewards);
                    EventKit.SetBit(player, changes, PassGroup, task, bit);
                }
            }
            Claim("tbBaseLevel", PassNormalBase, r => r.Normal);
            if (player.GetTask(PassGroup, PassOwnedTask) == _season.Id) Claim("tbAdvLevel", PassAdvanceBase, r => r.Advance);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Error"] = 0, ["Items"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    /// <summary>A weekly mission pays pass exp as an item, so the item's count also moves the exp task.</summary>
    private Task OnPassMission(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            if (!TryClaimPassMission(_ctx, player, id, changes, rewards))
                return PassReply(session, cmd, ErrGeneric);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Claim one battle-pass week mission (achieve-other, group 18): checks it is complete and unclaimed, grants its
    /// rewards into <paramref name="rewards"/>, sets the claimed bit, and converts any pass-exp item in the reward into
    /// pass exp. Returns false (nothing mutated) when the mission is missing, already claimed, or still in progress.
    /// Shared so the pet-mission screen (NCafePetLogic/GetPetMissionAward) claims the very same mission as the battle-pass
    /// screen (LuaCall 343): the claimed bit is shared, so a mission can be claimed from either entry exactly once.
    /// </summary>
    internal static bool TryClaimPassMission(GameFeatureContext ctx, Player player, int id,
        List<KeyValuePair<uint, int>> changes, RewardGrantResult rewards)
    {
        var mission = ctx.Missions.GetAchieveOther(id);
        if (mission is null) return false;
        int value = player.GetTask(PassMissionGroup, id);
        if (MissionFeature.ClaimedOf(value) == 1 || MissionFeature.ProgressOf(value) < mission.RequiredCount(true))
            return false;
        ctx.Rewards.Grant(player, mission.Rewards, RewardSource.Extra, rewards);
        EventKit.SetBit(player, changes, PassMissionGroup, id, 0);
        int exp = mission.Rewards.Where(r => r.Length >= 5 && r.Take(4).SequenceEqual(PassExpItem)).Sum(r => r[4]);
        if (exp > 0)
        {
            EventKit.Write(player, changes, PassGroup, PassExpTask, player.GetTask(PassGroup, PassExpTask) + exp);
            EventKit.Write(player, changes, PassGroup, PassWeekExpTask, player.GetTask(PassGroup, PassWeekExpTask) + exp);
        }
        return true;
    }

    private Task OnPassRefresh(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player || _season is null) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            if (player.GetTask(PassGroup, PassRefreshTask) != _season.Id)
            {
                EventKit.Write(player, changes, PassGroup, PassRefreshTask, _season.Id);
                EventKit.Write(player, changes, PassGroup, PassWeekExpTask, 0);
            }
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["Error"] = 0 });
        }
        return Task.CompletedTask;
    }

    private Task PassReply(ClientSession session, int cmd, int error)
        => EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = error });

    // ================================================================ fan support

    private void LoadFan(string directory)
    {
        if (SettingTable.Load(Path.Combine(directory, "_scoped", "fanevent", "timelist.txt")) is { } seasons)
            foreach (var row in seasons.Rows)
                if (seasons.Int(row, "ID") > 0)
                    _fanSeasons[seasons.Int(row, "ID")] = new(seasons.Int(row, "ID"), TwentyFirstTowerTable.Numbers(seasons.Str(row, "GirlList")));
        if (SettingTable.Load(Path.Combine(directory, "FanEventAward.txt")) is { } awards)
            foreach (var row in awards.Rows)
            {
                var items = new List<int[]>();
                for (int i = 1; i <= 5; i++) items.AddRange(ChapterTable.ParseTuples(awards.Str(row, "Item" + i)));
                _fanAwards[(awards.Int(row, "Id"), awards.Int(row, "Index"))] = (awards.Int(row, "Point"), items);
            }
    }

    /// <summary>tbNewGirlList is [[girlId, votes], ...] best first. The screen ranks by that order.</summary>
    private Task OnFanList(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var list = new JsonArray();
        if (_fanSeasons.TryGetValue(player.GetTask(FanGroup, FanIdTask), out var season))
            foreach (var (girl, votes) in season.Girls.Select((g, i) => (g, player.GetTask(FanGroup, FanGirlBase + i + 1))).OrderByDescending(x => x.Item2))
                list.Add(new JsonArray(girl, votes));
        return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["tbNewGirlList"] = list });
    }

    /// <summary>GetGirlPoint reads task 10 + the girl's position in GirlList, which is 1-based on the client.</summary>
    private Task OnFanSupport(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int girl = EventKit.Number(param, "GirlId"), count = EventKit.Number(param, "Num");
        lock (player)
        {
            if (!_fanSeasons.TryGetValue(player.GetTask(FanGroup, FanIdTask), out var season) || count <= 0
                || player.GetMoney(FanCoinMoney) < count || Array.IndexOf(season.Girls, girl) is var index && index < 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            player.AddMoney(FanCoinMoney, -count);
            GameNotify.PushMoney(session, player, FanCoinMoney);
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, FanGroup, FanGirlBase + index + 1, player.GetTask(FanGroup, FanGirlBase + index + 1) + count);
            EventKit.Write(player, changes, FanGroup, FanPointTask, player.GetTask(FanGroup, FanPointTask) + count);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    private Task OnFanReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int index = EventKit.Number(param, "nId");
        lock (player)
        {
            if (!_fanAwards.TryGetValue((player.GetTask(FanGroup, FanIdTask), index), out var award)
                || EventKit.HasBit(player, FanGroup, FanAwardTask, index) || player.GetTask(FanGroup, FanPointTask) < award.Points)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, award.Rewards, RewardSource.Extra, rewards);
            EventKit.SetBit(player, changes, FanGroup, FanAwardTask, index);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Items"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ concert exchange

    private void LoadConcert(string directory)
    {
        if (SettingTable.Load(Path.Combine(directory, "_scoped", "concertexchange", "timelist.txt")) is { } seasons)
            foreach (var row in seasons.Rows)
            {
                int id = seasons.Int(row, "ID");
                if (id <= 0) continue;
                var collections = Enumerable.Range(1, 4).Select(i => TwentyFirstTowerTable.Numbers(seasons.Str(row, "Collection" + i))).ToArray();
                _concerts[id] = new(id, collections, ChapterTable.ParseTuples(seasons.Str(row, "Award")));
            }
        if (SettingTable.Load(Path.Combine(directory, "rewardlist.txt")) is { } rewards)
            foreach (var row in rewards.Rows)
                _concertRewards[(rewards.Int(row, "ID"), rewards.Int(row, "TaskId"))] = ChapterTable.ParseTuples(rewards.Str(row, "NormalAward"));
    }

    /// <summary>Each listing marks a collection piece the player wants. With no other players nobody can answer it.</summary>
    private Task OnConcertListing(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "TypeId");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            if (type > 0)
            {
                EventKit.SetBit(player, changes, ConcertGroup, ConcertListingFlags, type);
                EventKit.Write(player, changes, ConcertGroup, ConcertListingCount, player.GetTask(ConcertGroup, ConcertListingCount) + 1);
            }
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["TypeId"] = type });
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// One full set of the four collection currencies (money ids 37-40) buys the season's award and the next step's
    /// reward. INFERRED: the set cost and step order come from the table's shape, as the client never computes them.
    /// </summary>
    private Task OnConcertAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (!_concerts.TryGetValue(player.GetTask(ConcertGroup, ConcertIdTask), out var season)
                || season.Collections.Any(c => c.Length < 2 || player.GetMoney(c[0]) < c[1]))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["Error"] = ErrGeneric });
            foreach (var collection in season.Collections)
            {
                player.AddMoney(collection[0], -collection[1]);
                GameNotify.PushMoney(session, player, collection[0]);
            }
            int step = player.GetTask(ConcertGroup, ConcertAwardsTask) + 1;
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, season.Award, RewardSource.Extra, rewards);
            _ctx.Rewards.Grant(player, _concertRewards.GetValueOrDefault((season.Id, step)), RewardSource.Extra, rewards);
            EventKit.Write(player, changes, ConcertGroup, ConcertAwardsTask, step);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Items"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }
}
