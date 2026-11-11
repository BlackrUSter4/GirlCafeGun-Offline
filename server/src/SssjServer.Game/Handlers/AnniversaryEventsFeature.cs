using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The anniversary hub's side activities: thank-you board (424/425), capsule game (438), girl love stories (437),
/// celebration barrage (528) and the discovery map (418-420, 429, 480). Battles on the map go through
/// NamedTowerFeature's discovery family.
/// </summary>
public sealed class AnniversaryEventsFeature : IGameFeature
{
    // AnniversaryBoard.lua
    private const int BoardGroup = 83, BoardUnlocked = 2, BoardClaimed = 3, BoardLastDay = 4, BoardPhotos = 14;
    /// <summary>INFERRED: the board's final reward is not in any client table, so this is a modest stand-in.</summary>
    private static readonly int[] BoardAward = { 15, 2, 1, 1, 200 };

    // AnniversaryMainLogic.lua
    private const int MainGroup = 81, PondIdTask = 1901, PondCountBase = 1902, GirlLoveBase = 2001, GirlStoryBase = 2501, MaxLove = 3;
    private static readonly int[] LoveItem = { 15, 88, 1, 1 };

    // GridActLogic.lua
    private const int GridVisitGroup = 80, GridMapStride = 200, AttrBase = 4000, AttrExpAdd = 50, ProcessBase = 19000, ProcessMapStride = 3;
    private const int PowerMoney = 58, PointMoney = 59; // MoneyLogic.eMoneyType_Anniversary_Power / _Point
    private const int MoveNoPower = 1;

    private const int ErrGeneric = 1;

    private sealed record Pool(int Limit, int Limitless, int[] Cost);
    private sealed record PoolItem(int Pond, bool Limitless, List<int[]> Item, int Weight, int Count);
    private sealed record GridEvent(int Type, List<int[]> FirstAward, int Degree);
    private sealed record MapConfig(int[] ProcessPoints, List<int[]>[] Rewards);

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, Pool> _pools = new();
    private readonly Dictionary<int, List<PoolItem>> _poolItems = new();
    private readonly Dictionary<(int Girl, int Index), List<int[]>> _stories = new();
    private readonly Dictionary<int, GridEvent> _events = new();
    private readonly Dictionary<int, MapConfig> _maps = new();
    private readonly Dictionary<(int Map, int Grid), int> _gridEvents = new();

    public string Name => "Anniversary activities (board, capsules, love, barrage, discovery map)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        Load(Path.GetFullPath(ctx.Options.SettingDirectory));
        ctx.MapLuaCall(424, OnBoardOpen);
        ctx.MapLuaCall(425, OnBoardAward);
        ctx.MapLuaCall(437, OnLove);
        ctx.MapLuaCall(438, OnCapsule);
        ctx.MapLuaCall(528, (s, c, p) => EventKit.Reply(_ctx, s, c, new JsonArray()));
        ctx.MapLuaCall(418, OnAddPower);
        ctx.MapLuaCall(419, OnResetMap);
        ctx.MapLuaCall(420, OnMove);
        ctx.MapLuaCall(429, OnStrengthen);
        ctx.MapLuaCall(480, OnProcessReward);
    }

    private void Load(string directory)
    {
        SettingTable? Table(string file) => SettingTable.Load(Path.Combine(directory, file + ".txt"));
        if (Table("GachaGame") is { } pools)
            foreach (var row in pools.Rows)
                if (pools.Int(row, "Id") > 0)
                    _pools[pools.Int(row, "Id")] = new(pools.Int(row, "LimitCount"), pools.Int(row, "LimitlessCount"), TwentyFirstTowerTable.Numbers(pools.Str(row, "CostItem")));
        if (Table("GachaGamePool") is { } items)
            foreach (var row in items.Rows)
            {
                int id = items.Int(row, "Id");
                if (id <= 0) continue;
                if (!_poolItems.TryGetValue(id, out var list)) _poolItems[id] = list = new();
                list.Add(new(items.Int(row, "PondId"), items.Int(row, "Limitless") == 1, ChapterTable.ParseTuples(items.Str(row, "OtherItem")), items.Int(row, "Weight"), items.Int(row, "Count")));
            }
        if (Table("GirlStory") is { } stories)
        {
            var index = new Dictionary<int, int>();
            foreach (var row in stories.Rows)
            {
                int girl = stories.Int(row, "GirlId");
                if (girl <= 0) continue;
                int position = index[girl] = index.GetValueOrDefault(girl) + 1;
                _stories[(girl, position)] = ChapterTable.ParseTuples(stories.Str(row, "Reward"));
            }
        }
        if (Table("MapEvents") is { } events)
            foreach (var row in events.Rows)
                _events[events.Int(row, "ID")] = new(events.Int(row, "EventType"), ChapterTable.ParseTuples(events.Str(row, "FirstAward")), events.Int(row, "ExploratoryDegree", 1));
        if (Table("AnniversaryDiscovery") is { } maps)
            foreach (var row in maps.Rows)
            {
                int id = maps.Int(row, "ID");
                if (id <= 0) continue;
                _maps[id] = new(TwentyFirstTowerTable.Numbers(maps.Str(row, "ProcessPoint")),
                    Enumerable.Range(1, 3).Select(i => ChapterTable.ParseTuples(maps.Str(row, "Reward" + i))).ToArray());
                if (Table("GridMap" + id) is { } grid)
                    foreach (var cell in grid.Rows)
                        _gridEvents[(id, grid.Int(cell, "ID"))] = grid.Int(cell, "Event");
            }
    }

    // ================================================================ thank-you board

    /// <summary>One photo per day: the reply is the running count, and the client unlocks photo count + 1.</summary>
    private Task OnBoardOpen(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            int today = (int)(DateTimeOffset.Now.AddHours(-4).ToUnixTimeSeconds() / 86400);
            int unlocked = player.GetTask(BoardGroup, BoardUnlocked);
            if (unlocked >= BoardPhotos || today <= player.GetTask(BoardGroup, BoardLastDay)) return Task.CompletedTask;
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, BoardGroup, BoardLastDay, today);
            EventKit.Write(player, changes, BoardGroup, BoardUnlocked, unlocked + 1);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonArray(unlocked));
        }
        return Task.CompletedTask;
    }

    private Task OnBoardAward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            if (player.GetTask(BoardGroup, BoardUnlocked) < BoardPhotos || player.GetTask(BoardGroup, BoardClaimed) == 1) return Task.CompletedTask;
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, new[] { BoardAward }, RewardSource.Extra, rewards);
            EventKit.Write(player, changes, BoardGroup, BoardClaimed, 1);
            // GetAwardRsp checks for exactly five entries: one flat g,d,p,l,n tuple.
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes,
                new JsonArray(BoardAward.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
        }
        return Task.CompletedTask;
    }

    // ================================================================ girl love stories

    /// <summary>nType 1 spends one gift item on a love level; nType 2 claims a story's reward once.</summary>
    private Task OnLove(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int type = EventKit.Number(param, "nType"), girl = EventKit.Number(param, "girlId");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            if (type == 1)
            {
                int love = player.GetTask(MainGroup, GirlLoveBase + girl);
                if (love >= MaxLove || !EventKit.Spend(_ctx, session, player, LoveItem, 1))
                    return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 20045 });
                EventKit.Write(player, changes, MainGroup, GirlLoveBase + girl, love + 1);
            }
            else
            {
                int story = EventKit.Number(param, "storyIdx");
                if (EventKit.HasBit(player, MainGroup, GirlStoryBase + girl, story))
                    return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = 20045 });
                _ctx.Rewards.Grant(player, _stories.GetValueOrDefault((girl, story)), RewardSource.Extra, rewards);
                EventKit.SetBit(player, changes, MainGroup, GirlStoryBase + girl, story);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["nType"] = type, ["tbReward"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    // ================================================================ capsule game

    /// <summary>
    /// Each turn draws LimitCount from the current limited pond and LimitlessCount from the endless pond. A pond
    /// empties item by item (task 1902+slot), then the next pond starts. The reply is the bare reward list.
    /// </summary>
    private Task OnCapsule(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "nId");
        lock (player)
        {
            if (!_pools.TryGetValue(id, out var pool) || !_poolItems.TryGetValue(id, out var items)
                || pool.Cost.Length < 5 || !EventKit.Spend(_ctx, session, player, pool.Cost, pool.Cost[4]))
                return EventKit.Reply(_ctx, session, cmd, new JsonArray());
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            for (int i = 0; i < pool.Limit; i++) DrawLimited(player, items, rewards, changes);
            var endless = items.Where(x => x.Limitless).ToList();
            for (int i = 0; i < pool.Limitless && endless.Count > 0; i++)
            {
                int pick = EventKit.WeightedPick(endless.Select(x => x.Weight).ToArray(), _ => true);
                _ctx.Rewards.Grant(player, endless[pick].Item, RewardSource.Extra, rewards);
            }
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, EventKit.Tuples(rewards));
        }
        return Task.CompletedTask;
    }

    private void DrawLimited(Player player, List<PoolItem> items, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changes)
    {
        int pond = Math.Max(1, player.GetTask(MainGroup, PondIdTask));
        var slots = items.Where(x => !x.Limitless && x.Pond == pond).ToList();
        if (slots.Count == 0) return;
        var drawn = slots.Select((_, i) => player.GetTask(MainGroup, PondCountBase + i)).ToArray();
        int pick = EventKit.WeightedPick(slots.Select(s => s.Weight).ToArray(), i => drawn[i] < slots[i].Count);
        if (pick >= 0)
        {
            _ctx.Rewards.Grant(player, slots[pick].Item, RewardSource.Extra, rewards);
            drawn[pick]++;
            EventKit.Write(player, changes, MainGroup, PondCountBase + pick, drawn[pick]);
        }
        if (drawn.Where((d, i) => d >= slots[i].Count).Count() < slots.Count) return;
        for (int i = 0; i < slots.Count; i++) EventKit.Write(player, changes, MainGroup, PondCountBase + i, 0);
        EventKit.Write(player, changes, MainGroup, PondIdTask, pond + 1);
    }

    // ================================================================ discovery map

    private Task OnAddPower(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is Player player)
            lock (player)
            {
                int value = EventKit.Number(param, "Val");
                if (value > 0)
                {
                    player.AddMoney(PowerMoney, value);
                    _ctx.Store.Save(player);
                    GameNotify.PushMoney(session, player, PowerMoney);
                }
            }
        return Task.CompletedTask;
    }

    /// <summary>No reply exists for this command. The client rebuilds the map from the tasks pushed here.</summary>
    private Task OnResetMap(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int map = EventKit.Number(param, "MapID");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            foreach (var (key, _) in _gridEvents.Where(g => g.Key.Map == map))
                EventKit.Write(player, changes, GridVisitGroup, (map - 1) * GridMapStride + key.Grid, 0);
            EventKit.Write(player, changes, MainGroup, map, 0);
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, changes);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Path lists walkable grid ids. A new grid costs one power, and its event's FirstAward pays on first arrival.
    /// The reply's TeamPos is where the walk ended, which is short of the goal if power ran out.
    /// </summary>
    private Task OnMove(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int map = EventKit.Number(param, "MapID");
        var path = ChallengeTowerFeature.ReadPositional((param as JsonObject)?["Path"]);
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            int position = player.GetTask(MainGroup, map);
            int result = 0;
            foreach (int grid in path)
            {
                if (!_gridEvents.TryGetValue((map, grid), out int eventId)) break;
                int visit = (map - 1) * GridMapStride + grid;
                if (player.GetTask(GridVisitGroup, visit) == 0)
                {
                    if (player.GetMoney(PowerMoney) <= 0) { result = MoveNoPower; break; }
                    player.AddMoney(PowerMoney, -1);
                    EventKit.Write(player, changes, GridVisitGroup, visit, 1);
                    if (_events.TryGetValue(eventId, out var gridEvent))
                        _ctx.Rewards.Grant(player, gridEvent.FirstAward, RewardSource.Extra, rewards);
                }
                position = grid;
            }
            EventKit.Write(player, changes, MainGroup, map, position);
            GameNotify.PushMoney(session, player, PowerMoney);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["Err"] = result, ["TeamPos"] = position });
        }
        return Task.CompletedTask;
    }

    /// <summary>One skill point adds ExpAdd exp to an attribute. Level is exp/ExpCost + 1 on the client.</summary>
    private Task OnStrengthen(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int attr = EventKit.Number(param, "AttrIdx");
        lock (player)
        {
            if (attr is < 1 or > 3 || player.GetMoney(PointMoney) <= 0)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            player.AddMoney(PointMoney, -1);
            GameNotify.PushMoney(session, player, PointMoney);
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, MainGroup, AttrBase + attr, player.GetTask(MainGroup, AttrBase + attr) + AttrExpAdd);
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject { ["nError"] = 0 });
        }
        return Task.CompletedTask;
    }

    /// <summary>Exploration milestones: the map's visited grids' degree must reach ProcessPoint[index].</summary>
    private Task OnProcessReward(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int map = EventKit.Number(param, "MapID"), index = EventKit.Number(param, "RewardIdx");
        lock (player)
        {
            int flag = ProcessBase + (map - 1) * ProcessMapStride + index;
            if (!_maps.TryGetValue(map, out var config) || index < 1 || index > config.ProcessPoints.Length || index > config.Rewards.Length
                || player.GetTask(MainGroup, flag) > 0 || Explored(player, map) < config.ProcessPoints[index - 1])
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["nError"] = ErrGeneric });
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            _ctx.Rewards.Grant(player, config.Rewards[index - 1], RewardSource.Extra, rewards);
            EventKit.Write(player, changes, MainGroup, flag, 1);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes,
                new JsonObject { ["nError"] = 0, ["nMapId"] = map, ["nRewardIdx"] = index });
        }
        return Task.CompletedTask;
    }

    private int Explored(Player player, int map)
        => _gridEvents.Where(g => g.Key.Map == map && g.Value != -1 && player.GetTask(GridVisitGroup, (map - 1) * GridMapStride + g.Key.Grid) > 0)
            .Sum(g => _events.TryGetValue(g.Value, out var gridEvent) ? gridEvent.Degree : 0);
}
