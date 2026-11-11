using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Boss rush SP stage (510-513), girl trial (203-205), and the commands that only need an honest answer here.
///
/// Those last ones are multiplayer events or real-world bindings with nothing to do on a one-player server. Each reply
/// is the shape its client handler expects, so the screen settles instead of waiting on a reply that never comes.
/// </summary>
public sealed class EventMiscFeature : IGameFeature
{
    // SixteenthTowerLogic.lua
    private const int RushTower = 16, RushActivityType = 26;
    private const int RushGroup = 64, RushSaved = 60003, RushHp = 60010, RushShield = 60020, RushSpLevel = 60200, RushSpTime = 60300, RushTeams = 5;

    // GirlTestLogic.lua. Its "strings" are digits packed two bits each, so '1' at position k is 1 << 2(k-1).
    private const int TestGroup = 33, TestRewardsTask = 2, TestPassTask = 3, TestStarsBase = 10, TestActivity = 1, TestStarsPerLevel = 6, TestPassSuccess = 1;

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, List<int[]>> _testFirstAwards = new();
    private readonly Dictionary<int, (int Goal, List<int[]> Rewards)> _testRewards = new();

    public string Name => "Boss rush SP, girl trial and one-player acknowledgements";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "GirlTestLevel.txt")) is { } levels)
            foreach (var row in levels.Rows)
                if (levels.Int(row, "ActivityId") == TestActivity)
                    _testFirstAwards[levels.Int(row, "LevelId")] = ChapterTable.ParseTuples(levels.Str(row, "FirstAward"));
        if (SettingTable.Load(Path.Combine(directory, "GirlTestReward.txt")) is { } rewards)
            foreach (var row in rewards.Rows)
                if (rewards.Int(row, "ActivityId") == TestActivity)
                    _testRewards[rewards.Int(row, "RewardId")] = (rewards.Int(row, "StarGoal"), ChapterTable.ParseTuples(rewards.Str(row, "Rewards")));

        ctx.MapLuaCall(510, OnRushSelect);
        ctx.MapLuaCall(511, OnRushFormation);
        ctx.MapLuaCall(512, OnRushReset);
        ctx.MapLuaCall(513, OnRushResult);
        ctx.MapLuaCall(203, OnTestLevelPass);
        ctx.MapLuaCall(204, OnTestRewardOne);
        ctx.MapLuaCall(205, OnTestRewardSome);

        void Answer(int cmd, Func<JsonNode> reply) => ctx.MapLuaCall(cmd, (s, c, p) => EventKit.Reply(_ctx, s, c, reply()));
        Answer(7, () => new JsonObject());                                // Force_Fight: the client ignores the reply
        Answer(150, () => new JsonObject { ["nError"] = -1 });            // ActCode_Use: "invalid code", none exist
        Answer(210, () => new JsonArray());                               // Online_GetFriendList: nobody to invite
        Answer(220, () => new JsonObject());                              // LvFirstPassReq: no other players' records
        Answer(251, () => new JsonObject { ["nError"] = 0 });             // Charge_SetAge: no purchase limits here
        Answer(260, () => new JsonObject());                              // ApplyCompleteCoopEvent
        Answer(261, () => new JsonObject());                              // GetCoopEventReward: "nothing to collect"
        Answer(270, () => new JsonObject());                              // GetChickenReward: "nothing to collect"
        Answer(314, () => new JsonObject());                              // GetJPBindAward: JP account binding
        Answer(446, () => new JsonObject { ["nError"] = 1 });             // Player_Bind: invite codes need other players
        Answer(456, () => new JsonObject());                              // Battle_ApplyReward: team battle rewards
        Answer(520, () => new JsonObject { ["nType"] = 1, ["tbScore"] = new JsonArray() }); // Online_RescueOperation
        ctx.MapLuaCall(211, (s, c, p) => Task.CompletedTask);             // Online_InviteFriend: no reply on the client
    }

    // ================================================================ boss rush SP

    private Task OnRushSelect(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, RushGroup, RushSpLevel + EventKit.NumberAt(param, 0), EventKit.NumberAt(param, 1));
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    /// <summary>Saving the teams starts the gauntlet, so every team begins at full health and shield.</summary>
    private Task OnRushFormation(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, RushGroup, RushSaved, 1);
            for (int i = 1; i <= RushTeams; i++)
            {
                EventKit.Write(player, changes, RushGroup, RushHp + i, 100);
                EventKit.Write(player, changes, RushGroup, RushShield + i, 100);
            }
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    private Task OnRushReset(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            EventKit.Write(player, changes, RushGroup, RushSaved, 0);
            for (int i = 1; i <= RushTeams; i++)
            {
                EventKit.Write(player, changes, RushGroup, RushHp + i, 100);
                EventKit.Write(player, changes, RushGroup, RushShield + i, 100);
            }
            for (int level = 1; level < 100; level++)
            {
                EventKit.Write(player, changes, RushGroup, RushSpLevel + level, 0);
                EventKit.Write(player, changes, RushGroup, RushSpTime + level, 0);
            }
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    /// <summary>SaveSpTeamBlood: health carries to the next boss only if the player keeps this result.</summary>
    private Task OnRushResult(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        var request = param as JsonObject;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            bool save = request?["bSave"] is JsonValue flag && flag.TryGetValue<bool>(out bool keep) && keep;
            if (save && request?["tbResult"] is JsonObject result)
            {
                int shield = EventKit.Number(result, "shield");
                if (result["blood"] is JsonArray blood)
                    for (int i = 0; i < blood.Count && i < RushTeams; i++)
                    {
                        EventKit.Write(player, changes, RushGroup, RushHp + i + 1, EventKit.NumberAt(blood, i));
                        EventKit.Write(player, changes, RushGroup, RushShield + i + 1, shield);
                    }
                // Condition 231 names the boss picked for the level, and 232/233 the clear time.
                int rushLevel = EventKit.Number(result, "LevelId");
                EventMissionProgress.Bump(player, $"231:{player.GetTask(RushGroup, RushSpLevel + rushLevel)}:{rushLevel}");
                EventMissionProgress.RecordTimedClear(player, RushTower, RushActivityType, rushLevel, EventKit.Number(result, "passTime"));
                EventKit.Write(player, changes, RushGroup, RushSpTime + EventKit.Number(result, "LevelId"), EventKit.Number(result, "passTime"));
            }
            EventKit.Commit(_ctx, session, player, cmd, null, changes, new JsonObject());
        }
        return Task.CompletedTask;
    }

    // ================================================================ girl trial

    private static int Digit(int value, int position) => value >> 2 * (position - 1) & 3;

    private static int WithDigit(int value, int position, int digit)
    {
        int shift = 2 * (position - 1);
        return value & ~(3 << shift) | digit << shift;
    }

    private static int StarCount(Player player)
    {
        int stars = 0;
        for (int level = 1; level <= 5; level++)
            for (int k = 1; k <= TestStarsPerLevel; k++)
                if (Digit(player.GetTask(TestGroup, TestStarsBase + level), k) is 1 or 2)
                    stars++;
        return stars;
    }

    /// <summary>passType 1 is a clear, with info holding the goal digits as a string. The other types are exits.</summary>
    private Task OnTestLevelPass(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int level = EventKit.Number(param, "levelID");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            if (EventKit.Number(param, "passType") == TestPassSuccess && level is >= 1 and <= 5)
            {
                if (Digit(player.GetTask(TestGroup, TestPassTask), level) == 0)
                {
                    _ctx.Rewards.Grant(player, _testFirstAwards.GetValueOrDefault(level), RewardSource.FirstAward, rewards);
                    EventKit.Write(player, changes, TestGroup, TestPassTask, WithDigit(player.GetTask(TestGroup, TestPassTask), level, 1));
                }
                if ((param as JsonObject)?["info"] is JsonValue info && info.TryGetValue<string>(out string? digits))
                {
                    int value = player.GetTask(TestGroup, TestStarsBase + level);
                    for (int k = 1; k <= digits.Length && k <= TestStarsPerLevel; k++)
                        if (char.IsAsciiDigit(digits[k - 1]))
                            value = WithDigit(value, k, digits[k - 1] - '0');
                    EventKit.Write(player, changes, TestGroup, TestStarsBase + level, value);
                }
            }
            var reply = new JsonObject();
            if (rewards.Rewards.Count > 0) reply["Award"] = EventKit.Tuples(rewards);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, reply);
        }
        return Task.CompletedTask;
    }

    private bool TestClaim(Player player, int id, RewardGrantResult rewards, List<KeyValuePair<uint, int>> changes)
    {
        if (!_testRewards.TryGetValue(id, out var reward) || StarCount(player) < reward.Goal
            || Digit(player.GetTask(TestGroup, TestRewardsTask), id) != 0) return false;
        _ctx.Rewards.Grant(player, reward.Rewards, RewardSource.Extra, rewards);
        EventKit.Write(player, changes, TestGroup, TestRewardsTask, WithDigit(player.GetTask(TestGroup, TestRewardsTask), id, 1));
        return true;
    }

    private Task OnTestRewardOne(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int id = EventKit.Number(param, "rewardID");
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var rewards = new RewardGrantResult();
            TestClaim(player, id, rewards, changes);
            EventKit.Commit(_ctx, session, player, cmd, rewards, changes, new JsonObject { ["ID"] = id, ["Award"] = EventKit.Tuples(rewards) });
        }
        return Task.CompletedTask;
    }

    private Task OnTestRewardSome(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        lock (player)
        {
            var changes = new List<KeyValuePair<uint, int>>();
            var all = new RewardGrantResult();
            var result = new JsonObject();
            foreach (int id in ChallengeTowerFeature.ReadPositional((param as JsonObject)?["rewardIDS"]))
            {
                int before = all.Rewards.Count;
                if (!TestClaim(player, id, all, changes)) continue;
                // The screen merges every entry into one popup, so each id lists only what it added.
                var added = new JsonArray();
                foreach (var granted in all.Rewards.Skip(before))
                    added.Add(new JsonArray(granted.ToTuple().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
                result[id.ToString()] = added;
            }
            EventKit.Commit(_ctx, session, player, cmd, all, changes, new JsonObject { ["AwardResult"] = result });
        }
        return Task.CompletedTask;
    }
}
