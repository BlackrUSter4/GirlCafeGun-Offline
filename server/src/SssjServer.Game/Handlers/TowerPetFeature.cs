using System.Text.Json;
using System.Text.Json.Nodes;
using SssjServer.Core;
using SssjServer.Game.Config;
using SssjServer.Protocol.GsProto;

namespace SssjServer.Game.Handlers;

/// <summary>
/// The partner in the node towers (190-193), with rules ported from PetLogic.lua, which still ships its server half.
/// Tower.txt names which tower has which pet, and the pet's guid goes in task (30, tower), where the screens read it.
/// Pets reach the client in pf 1035 at login and pf 1063 on change.
///
/// With every event open a tower's pet exists from login. On the original server it arrived through the event's
/// progress, which the client does not show.
/// </summary>
public sealed class TowerPetFeature : IGameFeature
{
    private const int TaskGroup = 30;
    private const string StateKey = "towerPets";
    // AddExp and UpgradeSkill reply codes
    private const int Ok = 1, WeaponLocked = 2, NoPoints = 3, SkillMax = 4, LevelMax = 5, NoItems = 6;

    private sealed record PetConfig(int Id, int[][] Weapons, int[] Skills, int[][] ExpItems);

    private sealed class PetRecord
    {
        public uint Guid { get; set; }
        public int Template { get; set; }
        public int Level { get; set; } = 1;
        public int Exp { get; set; }
        public int Weapon { get; set; } = 1;
        public int Talent { get; set; }
        public Dictionary<int, int> Skills { get; set; } = new();
    }

    private GameFeatureContext _ctx = null!;
    private readonly Dictionary<int, PetConfig> _pets = new();
    private readonly Dictionary<(int Pet, int Level), (int Exp, int Points)> _levels = new();
    private readonly Dictionary<(int Skill, int Level), int> _skillCosts = new();
    private readonly Dictionary<int, int> _towerPets = new();

    public string Name => "Node tower partner (190-193)";

    public void Install(GameFeatureContext ctx)
    {
        _ctx = ctx;
        string directory = Path.GetFullPath(ctx.Options.SettingDirectory);
        if (SettingTable.Load(Path.Combine(directory, "PetConfig.txt")) is { } pets)
            foreach (var row in pets.Rows)
            {
                int id = pets.Int(row, "Id");
                if (id <= 0) continue;
                _pets[id] = new(id, ClubTable.ParseAwardList(pets.Str(row, "WeaponList")).ToArray(), TwentyFirstTowerTable.Numbers(pets.Str(row, "SkillList")),
                    TwentyFirstTowerTable.Numbers(pets.Str(row, "UpgradeItem")).Chunk(4).Where(c => c.Length == 4).ToArray());
            }
        if (SettingTable.Load(Path.Combine(directory, "PetUpgradeConfig.txt")) is { } levels)
            foreach (var row in levels.Rows)
                _levels[(levels.Int(row, "Id"), levels.Int(row, "Level"))] = (levels.Int(row, "UpgradeExp"), levels.Int(row, "Points"));
        if (SettingTable.Load(Path.Combine(directory, "PetSkillUpgradeConfig.txt")) is { } skills)
            foreach (var row in skills.Rows)
                _skillCosts[(skills.Int(row, "Id"), skills.Int(row, "level"))] = skills.Int(row, "UpgradeCost");
        if (SettingTable.Load(Path.Combine(directory, "Tower.txt")) is { } towers)
            foreach (var row in towers.Rows)
                if (towers.Int(row, "PetId") > 0)
                    _towerPets[towers.Int(row, "Id")] = towers.Int(row, "PetId");

        ctx.MapLuaCall(190, OnAdd);
        ctx.MapLuaCall(191, OnAddExp);
        ctx.MapLuaCall(192, OnChangeWeapon);
        ctx.MapLuaCall(193, OnUpgradeSkill);
        if (ctx.Options.AllEventsOpen) ctx.OnPlayerPrepare((session, player) => SeedTowerPets(player));
    }

    /// <summary>Pets for the pf 1035 login packet. Called by PhoneMsgFeature, which builds that message.</summary>
    internal static void Fill(PhoneMsgNtf message, Player player)
    {
        foreach (var pet in Read(player)) message.Petlist.Add(ToProto(pet));
    }

    /// <summary>The pet's level for AchieveType 111, or zero when the player has none of that template.</summary>
    internal static int LevelOf(Player player, int template)
        => Read(player).FirstOrDefault(p => p.Template == template)?.Level ?? 0;

    private static List<PetRecord> Read(Player player)
    {
        try { return player.Extra.GetValueOrDefault(StateKey)?.Deserialize<List<PetRecord>>() ?? new(); }
        catch (JsonException) { return new(); }
    }

    private static void Save(Player player, List<PetRecord> pets) => player.Extra[StateKey] = JsonSerializer.SerializeToNode(pets);

    private static PetInfo ToProto(PetRecord pet)
    {
        var info = new PetInfo
        {
            Petunique = pet.Guid, Templateid = (uint)pet.Template, Level = (uint)pet.Level, Exp = (uint)pet.Exp,
            Weaponstatus = pet.Weapon, Talentpoint = (uint)pet.Talent,
        };
        foreach (var (skill, level) in pet.Skills)
            info.Skilllist.Add(new MiniSkill { Skillid = (uint)skill, Skilllevel = (uint)level });
        return info;
    }

    private void Push(ClientSession session, PetRecord pet)
    {
        var message = new PetUpdateNtf();
        message.Pets.Add(ToProto(pet));
        session.Push(GamePf.PetUpdateNtf, message);
    }

    private PetRecord Create(Player player, int template)
    {
        var pet = new PetRecord { Guid = player.AllocateGuid(), Template = template };
        if (_pets.TryGetValue(template, out var config))
            foreach (int skill in config.Skills) pet.Skills[skill] = 0;
        UnlockFreeSkills(pet);
        return pet;
    }

    private void SeedTowerPets(Player player)
    {
        var pets = Read(player);
        foreach (var (tower, template) in _towerPets)
        {
            if (!_pets.ContainsKey(template)) continue;
            var pet = pets.FirstOrDefault(p => p.Template == template);
            if (pet is null) pets.Add(pet = Create(player, template));
            player.SetTask(TaskGroup, tower, (int)pet.Guid);
        }
        Save(player, pets);
    }

    /// <summary>UnLockSkill: a skill whose first level costs nothing starts at level 1.</summary>
    private void UnlockFreeSkills(PetRecord pet)
    {
        foreach (int skill in pet.Skills.Keys.ToArray())
            if (pet.Skills[skill] == 0 && _skillCosts.GetValueOrDefault((skill, 0), -1) == 0)
                pet.Skills[skill] = 1;
    }

    private Task OnAdd(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        int template = EventKit.Number(param, "id");
        lock (player)
        {
            var pets = Read(player);
            if (!_pets.ContainsKey(template) || pets.Any(p => p.Template == template)) return Task.CompletedTask;
            var pet = Create(player, template);
            pets.Add(pet);
            Save(player, pets);
            int task = EventKit.Number(param, "TaskId");
            var changes = new List<KeyValuePair<uint, int>>();
            if (task > 0) EventKit.Write(player, changes, TaskGroup, task, (int)pet.Guid);
            _ctx.Store.Save(player);
            GameNotify.PushTaskValues(session, changes);
            Push(session, pet);
        }
        return Task.CompletedTask;
    }

    private int MaxLevel(int template) => _levels.Keys.Where(k => k.Pet == template).Select(k => k.Level).DefaultIfEmpty(1).Max();

    /// <summary>
    /// Spends every experience item held, cheapest first, until the items run out or the pet hits max level. Each item's
    /// ExtParam1 is its experience, and the last one used may overshoot, as CSCaculateExp allows.
    /// </summary>
    private Task OnAddExp(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        uint guid = (uint)EventKit.Number(param, "petID");
        lock (player)
        {
            var pets = Read(player);
            var pet = pets.FirstOrDefault(p => p.Guid == guid);
            if (pet is null || !_pets.TryGetValue(pet.Template, out var config))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = NoItems });
            int max = MaxLevel(pet.Template);
            if (pet.Level >= max) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = LevelMax });

            int startLevel = pet.Level;
            bool hadItems = false;
            foreach (var item in config.ExpItems)
            {
                int each = int.TryParse(_ctx.ItemTemplates.Get((uint)item[0], (uint)item[1], (uint)item[2], (uint)item[3])?.ExtParam1, out int value) ? value : 0;
                if (each <= 0) continue;
                long held = player.Items.Where(i => i.Genre == item[0] && i.Detail == item[1] && i.Particular == item[2] && i.Level == item[3]).Sum(i => (long)i.Count);
                if (held <= 0) continue;
                hadItems = true;
                int used = 0;
                while (used < held && pet.Level < max)
                {
                    used++;
                    pet.Exp += each;
                    while (pet.Level < max && _levels.TryGetValue((pet.Template, pet.Level), out var step) && pet.Exp >= step.Exp)
                    {
                        pet.Exp -= step.Exp;
                        pet.Talent += step.Points;
                        pet.Level++;
                    }
                }
                EventKit.Spend(_ctx, session, player, item, used);
                if (pet.Level >= max) break;
            }
            if (!hadItems) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = NoItems });
            if (pet.Level >= max) pet.Exp = 0;
            if (pet.Level > startLevel)
            {
                UnlockFreeSkills(pet);
                Push(session, pet);
            }
            Save(player, pets);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["isok"] = Ok, ["Level"] = pet.Level, ["Exp"] = pet.Exp, ["TalentPoint"] = pet.Talent });
        }
        return Task.CompletedTask;
    }

    private Task OnChangeWeapon(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        uint guid = (uint)EventKit.Number(param, "petID");
        int index = EventKit.Number(param, "index");
        lock (player)
        {
            var pets = Read(player);
            var pet = pets.FirstOrDefault(p => p.Guid == guid);
            if (pet is null || !_pets.TryGetValue(pet.Template, out var config) || index < 1 || index > config.Weapons.Length)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = WeaponLocked, ["unlockLv"] = 0 });
            int unlock = config.Weapons[index - 1].Length >= 5 ? config.Weapons[index - 1][4] : 1;
            if (pet.Level < unlock)
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = WeaponLocked, ["unlockLv"] = unlock });
            pet.Weapon = index;
            Save(player, pets);
            _ctx.Store.Save(player);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["isok"] = Ok, ["unlockLv"] = 0, ["weapon"] = pet.Weapon });
        }
        return Task.CompletedTask;
    }

    private Task OnUpgradeSkill(ClientSession session, int cmd, JsonNode? param)
    {
        if (session.Tag is not Player player) return Task.CompletedTask;
        uint guid = (uint)EventKit.Number(param, "petID");
        int skill = EventKit.Number(param, "skillID");
        lock (player)
        {
            var pets = Read(player);
            var pet = pets.FirstOrDefault(p => p.Guid == guid);
            if (pet is null || !pet.Skills.TryGetValue(skill, out int level))
                return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = SkillMax });
            // Rows start at level 0 and the client compares against #Upgrade, which skips index 0: the top row is the cap.
            int maxLevel = _skillCosts.Keys.Where(k => k.Skill == skill).Select(k => k.Level).DefaultIfEmpty(0).Max();
            if (level >= maxLevel) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = SkillMax });
            int cost = _skillCosts.GetValueOrDefault((skill, level));
            if (cost > pet.Talent) return EventKit.Reply(_ctx, session, cmd, new JsonObject { ["isok"] = NoPoints });
            pet.Skills[skill] = level + 1;
            pet.Talent -= cost;
            Save(player, pets);
            _ctx.Store.Save(player);
            Push(session, pet);
            _ctx.LuaCalls.CallClient(session, cmd, new JsonObject { ["isok"] = Ok, ["skillID"] = skill, ["skilllevel"] = level + 1, ["TalentPoint"] = pet.Talent });
        }
        return Task.CompletedTask;
    }
}
