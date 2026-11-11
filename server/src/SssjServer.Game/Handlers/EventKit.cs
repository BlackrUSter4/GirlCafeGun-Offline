using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>Small helpers every event handler needs: reading requests, writing tasks, shaping reward lists.</summary>
internal static class EventKit
{
    internal static int Number(JsonNode? param, string field)
        => (param as JsonObject)?[field] is JsonValue value && value.TryGetValue<double>(out double n) ? (int)n : 0;

    internal static int NumberAt(JsonNode? param, int index)
        => param is JsonArray list && index < list.Count && list[index] is JsonValue value && value.TryGetValue<double>(out double n) ? (int)n : 0;

    /// <summary>Records the change for the pf 1026 push. An unchanged value stays out of the push.</summary>
    internal static void Write(Player player, List<KeyValuePair<uint, int>> changes, int group, int key, int value)
    {
        if (player.GetTask(group, key) == value) return;
        player.SetTask(group, key, value);
        changes.Add(new(Player.ComposeTaskId(group, key), value));
    }

    internal static void SetBit(Player player, List<KeyValuePair<uint, int>> changes, int group, int key, int bit)
        => Write(player, changes, group, key, player.GetTask(group, key) | 1 << bit);

    internal static bool HasBit(Player player, int group, int key, int bit) => (player.GetTask(group, key) >> bit & 1) == 1;

    /// <summary>
    /// Takes a cost given as G-D-P-L from either a currency or item stacks, and pushes the change.
    /// Returns false, changing nothing, when the player cannot afford all of it.
    /// </summary>
    internal static bool Spend(GameFeatureContext ctx, ClientSession session, Player player, int[] gdpl, int count)
    {
        if (count <= 0) return true;
        if (gdpl.Length < 4) return false;
        if (ctx.ItemTemplates.TryGetMoneyId((uint)gdpl[0], (uint)gdpl[1], (uint)gdpl[2], (uint)gdpl[3], out int money))
        {
            if (player.GetMoney(money) < count) return false;
            player.AddMoney(money, -count);
            GameNotify.PushMoney(session, player, money);
            return true;
        }
        var stacks = player.Items.Where(i => i.Genre == gdpl[0] && i.Detail == gdpl[1] && i.Particular == gdpl[2]
            && i.Level == gdpl[3] && i.Count > 0).ToArray();
        if (stacks.Sum(i => (long)i.Count) < count) return false;
        uint left = (uint)count;
        var changed = new List<Item>();
        var removed = new List<Item>();
        foreach (var stack in stacks)
        {
            uint used = Math.Min(stack.Count, left);
            stack.Count -= used;
            left -= used;
            if (stack.Count == 0) { player.Items.Remove(stack); removed.Add(stack); }
            else changed.Add(stack);
            if (left == 0) break;
        }
        if (removed.Count > 0) GameNotify.PushItemsRemoved(session, removed);
        if (changed.Count > 0) GameNotify.PushItems(session, changed);
        return true;
    }

    /// <summary>Whole-weight draw over rows with stock left. Returns -1 when none has any.</summary>
    internal static int WeightedPick(IReadOnlyList<int> weights, Func<int, bool> available)
    {
        int total = 0;
        for (int i = 0; i < weights.Count; i++)
            if (available(i)) total += Math.Max(1, weights[i]);
        if (total == 0) return -1;
        int roll = Random.Shared.Next(total);
        for (int i = 0; i < weights.Count; i++)
        {
            if (!available(i)) continue;
            roll -= Math.Max(1, weights[i]);
            if (roll < 0) return i;
        }
        return -1;
    }

    internal static JsonArray Tuples(RewardGrantResult rewards)
    {
        var data = new JsonArray();
        foreach (var reward in rewards.ToAwardTuples())
            data.Add(new JsonArray(reward.Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
        return data;
    }

    /// <summary>Saves, then pushes state before the reply: client reply handlers re-read tasks and money right away.</summary>
    internal static void Commit(GameFeatureContext ctx, ClientSession session, Player player, int cmd,
        RewardGrantResult? rewards, List<KeyValuePair<uint, int>>? changes, JsonNode reply)
    {
        ctx.Store.Save(player);
        if (rewards is not null) GameNotify.PushRewards(session, player, rewards);
        if (changes is { Count: > 0 }) GameNotify.PushTaskValues(session, changes);
        ctx.LuaCalls.CallClient(session, cmd, reply);
    }

    internal static Task Reply(GameFeatureContext ctx, ClientSession session, int cmd, JsonNode reply)
    {
        ctx.LuaCalls.CallClient(session, cmd, reply);
        return Task.CompletedTask;
    }
}
