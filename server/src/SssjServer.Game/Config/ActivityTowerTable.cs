namespace SssjServer.Game.Config;

/// <summary>How a tower class records that a level was cleared. The client reads each differently.</summary>
internal enum TowerProgress
{
    /// <summary>Best score. GetLevelScore must be nonzero to unlock the next level.</summary>
    Score,
    /// <summary>Clear count. GetLevelPassCount / IsLevelPass.</summary>
    PassCount,
    /// <summary>Star bits, with the node family's ACT score and SP records beside them.</summary>
    Stars,
}

/// <summary>Storage layout of one tower class, copied from the constants at the top of its *Logic.lua.</summary>
internal sealed record TowerClass(string LevelFile, int Group, int LevelBase, int GoalBase, TowerProgress Progress,
    int Offset = 0, string PointFile = "", string PondFile = "", string TokenFile = "", int ScoreTask = 0);

internal sealed record ActivityTowerLevel(int Tower, int Id, int Type, int Node, int Cost, int MasterExp,
    List<int[]> First, List<int[]> Fixed);

internal sealed record TowerPointReward(int Id, int Goal, List<int[]> Rewards);
internal sealed record TowerPondEntry(List<int[]> Item, bool Key, int Weight, int Count);
internal sealed record TowerPondToken(int Times, int Cost, int Money);
internal sealed record TowerScoreAward(int Order, int Score, List<int[]> Rewards);

/// <summary>Every tower that goes through the shared Activities_* commands (54/55/56), keyed by Tower.txt id.</summary>
internal sealed class ActivityTowerTable
{
    /// <summary>ClassName column of Tower.txt to storage. Animalize and EighthTower keep their own card state.</summary>
    private static readonly Dictionary<string, TowerClass> Classes = new()
    {
        ["SmallTower"] = new("SmallTowerLevel", 9, 1100, 10000, TowerProgress.Score),
        ["ChessTower"] = new("ChessTowerLevel", 9, 1500, 20000, TowerProgress.Score),
        ["NinthTower"] = new("NinthTowerLevel", 9, 2500, 60000, TowerProgress.Score),
        ["TwelfthTower"] = new("TwelfthTowerLevel", 64, 8201, 30000, TowerProgress.Score),
        ["ThirteenthTower"] = new("ThirteenthTowerLevel", 64, 9100, 40000, TowerProgress.Score),
        ["FourteenthTower"] = new("FourteenthTowerLevel", 96, 101, 10000, TowerProgress.Score),
        ["SeventeenthTower"] = new("SeventeenthTowerLevel", 96, 401, 20000, TowerProgress.Score),
        ["EighteenthTower"] = new("EighteenthTowerLevel", 96, 701, 30000, TowerProgress.Score),
        ["NineteenthTower"] = new("NineteenthTowerLevel", 96, 1001, 40000, TowerProgress.Score),
        ["TwentiethTower"] = new("TwentiethTowerLevel", 96, 1301, 50000, TowerProgress.Score, 1201, "",
            // Linkage has tables of the same name, so the flat setting folder can only hold one of the pair.
            "_scoped/activities/smalltower/twentiethtower/towerpond", "_scoped/activities/smalltower/twentiethtower/towerpondtoken"),
        ["TenthTower"] = new("TenthTowerLevel", 64, 2100, 10000, TowerProgress.PassCount),
        ["FifteenthTower"] = new("FifteenthTowerLevel", 64, 3701, 50000, TowerProgress.PassCount),
        ["SixteenthTower"] = new("SixteenthTowerLevel", 64, 60100, 0, TowerProgress.PassCount),
        ["Swimwear"] = new("SwimwearLevel", 34, 0, 0, TowerProgress.PassCount, 0, ScoreTask: 102),
        ["AbsorbPeak"] = new("AbsorbPeakLevel", 34, 2000, 20000, TowerProgress.PassCount, 2000, ScoreTask: 2102),
        ["VariegatedTower"] = new("VariegatedTowerLevel", 64, 2600, 0, TowerProgress.PassCount, 2600, ScoreTask: 2804),
        ["NodeTower"] = new("NodeTowerLevel", 9, 3100, 30000, TowerProgress.Stars, 3000, "NodePointReward", "NodeTowerPond", "NodeTowerPondToken"),
        ["SeventhTower"] = new("SeventhTowerLevel", 9, 3600, 50000, TowerProgress.Stars, 3500, "SeventhPointReward", "SeventhTowerPond", "SeventhTowerPondToken"),
        ["EleventhTower"] = new("EleventhTowerLevel", 64, 7100, 20000, TowerProgress.Stars, 7000, "EleventhPointReward", "EleventhTowerPond", "EleventhTowerPondToken"),
    };

    /// <summary>Tower id column differs per file: Id for most, TowerId for the variegated towers.</summary>
    private static readonly string[] TowerColumns = { "Id", "TowerId", "ActivityId" };

    internal Dictionary<int, TowerClass> Towers { get; } = new();
    internal Dictionary<int, int[]> Money1 { get; } = new();
    internal Dictionary<(int Tower, int Level), ActivityTowerLevel> Levels { get; } = new();
    internal Dictionary<(int Tower, int Level), List<RestoredTowerGoal>> Goals { get; } = new();
    internal Dictionary<(int Tower, int Level), List<RestoredTowerGoalReward>> GoalRewards { get; } = new();
    internal Dictionary<(int Tower, int Level), int> VariegatedIndex { get; } = new();
    internal Dictionary<int, List<TowerPointReward>> PointRewards { get; } = new();
    internal Dictionary<(int Tower, int Pond), List<TowerPondEntry>> Ponds { get; } = new();
    internal Dictionary<(int Tower, int Type), TowerPondToken> Tokens { get; } = new();
    internal Dictionary<int, List<TowerScoreAward>> ScoreAwards { get; } = new();
    internal Dictionary<int, List<TowerScoreAward>> ServerScoreAwards { get; } = new();

    internal static ActivityTowerTable Load(string directory)
    {
        var result = new ActivityTowerTable();
        var towers = SettingTable.Load(Path.Combine(directory, "Tower.txt"));
        if (towers is null) return result;
        foreach (var row in towers.Rows)
        {
            int id = towers.Int(row, "Id");
            if (id <= 0 || !Classes.TryGetValue(towers.Str(row, "ClassName"), out var cls)) continue;
            result.Towers[id] = cls;
            var money = TwentyFirstTowerTable.Numbers(towers.Str(row, "Money1GDPL"));
            if (money.Length == 4) result.Money1[id] = money;
        }
        foreach (var file in result.Towers.Values.Select(c => c.LevelFile).Distinct())
            result.LoadLevels(directory, file);
        result.LoadGoals(directory);
        result.LoadVariegated(directory);
        foreach (var (id, cls) in result.Towers)
            result.LoadPointsAndPond(directory, id, cls);
        result.LoadScoreAwards(directory, "SwimwearScoreAward", result.ScoreAwards);
        result.LoadScoreAwards(directory, "AbsorbPeakScoreAward", result.ScoreAwards);
        result.LoadScoreAwards(directory, "SwimwearAllServiceScoreAward", result.ServerScoreAwards);
        result.LoadScoreAwards(directory, "AbsorbPeakAllServiceScoreAward", result.ServerScoreAwards);
        return result;
    }

    private static int TowerOf(SettingTable table, string[] row)
    {
        foreach (var column in TowerColumns)
            if (table.Columns.ContainsKey(column))
                return table.Int(row, column);
        return 0;
    }

    private void LoadLevels(string directory, string file)
    {
        var table = SettingTable.Load(Path.Combine(directory, file + ".txt"));
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int tower = TowerOf(table, row);
            int level = table.Int(row, "LevelId");
            if (tower <= 0 || level <= 0) continue;
            Levels[(tower, level)] = new(tower, level, table.Int(row, "LevelType"), table.Int(row, "NodeId"),
                table.Int(row, "PreCost"), table.Int(row, "MasterExp"),
                ChapterTable.ParseTuples(table.Str(row, "FirstAward")),
                ChapterTable.ParseTuples(table.Str(row, "FixAward")));
        }
    }

    private void LoadGoals(string directory)
    {
        var goals = SettingTable.Load(Path.Combine(directory, "TowerGoal.txt"));
        if (goals is not null)
            foreach (var row in goals.Rows)
            {
                var key = (goals.Int(row, "Id"), goals.Int(row, "LevelId"));
                if (!Goals.TryGetValue(key, out var list)) Goals[key] = list = new();
                list.Add(new(goals.Int(row, "GoalID"), goals.Int(row, "GoalPoint")));
            }
        var rewards = SettingTable.Load(Path.Combine(directory, "TowerGoalReward.txt"));
        if (rewards is not null)
            foreach (var row in rewards.Rows)
            {
                var key = (rewards.Int(row, "Id"), rewards.Int(row, "LevelId"));
                if (!GoalRewards.TryGetValue(key, out var list)) GoalRewards[key] = list = new();
                list.Add(new(rewards.Int(row, "GoalPoint"), ChapterTable.ParseTuples(rewards.Str(row, "Reward"))));
            }
    }

    /// <summary>VariegatedTowerLogic.GetLevelIndex: a level's slot is area*20+position, areas counted with blanks.</summary>
    private void LoadVariegated(string directory)
    {
        var table = SettingTable.Load(Path.Combine(directory, "VariegatedTower.txt"));
        if (table is null) return;
        foreach (var row in table.Rows)
        {
            int tower = table.Int(row, "TowerId");
            for (int area = 1; area <= 5; area++)
            {
                var ids = TwentyFirstTowerTable.Numbers(table.Str(row, "LevelArea" + area));
                for (int i = 0; i < ids.Length; i++)
                    VariegatedIndex[(tower, ids[i])] = area * 20 + i + 1;
            }
        }
    }

    private void LoadPointsAndPond(string directory, int tower, TowerClass cls)
    {
        if (cls.PointFile.Length > 0 && SettingTable.Load(Path.Combine(directory, cls.PointFile + ".txt")) is { } points)
        {
            var list = PointRewards[tower] = new();
            foreach (var row in points.Rows)
                if (TowerOf(points, row) == tower)
                    list.Add(new(points.Int(row, "RewardId"), points.Int(row, "Gold"),
                        ChapterTable.ParseTuples(points.Str(row, "Rewards"))));
        }
        if (cls.PondFile.Length == 0) return;
        if (SettingTable.Load(Path.Combine(directory, cls.PondFile + ".txt")) is { } ponds)
            foreach (var row in ponds.Rows)
            {
                // TowerPond.txt belongs to tower 20 alone, so it carries no tower column and renames two others.
                int owner = TowerOf(ponds, row);
                if (owner != 0 && owner != tower) continue;
                var key = (tower, ponds.Int(row, "PondId"));
                if (!Ponds.TryGetValue(key, out var list)) Ponds[key] = list = new();
                string item = ponds.Columns.ContainsKey("OtherItem") ? ponds.Str(row, "OtherItem") : ponds.Str(row, "Item");
                int isKey = ponds.Columns.ContainsKey("Key") ? ponds.Int(row, "Key") : ponds.Int(row, "Sp");
                list.Add(new(ChapterTable.ParseTuples(item), isKey == 1, ponds.Int(row, "Weight", 1), ponds.Int(row, "Count", 1)));
            }
        if (SettingTable.Load(Path.Combine(directory, cls.TokenFile + ".txt")) is { } tokens)
            foreach (var row in tokens.Rows)
            {
                int owner = TowerOf(tokens, row);
                if (owner != 0 && owner != tower) continue;
                int cost = tokens.Columns.ContainsKey("CastCount") ? tokens.Int(row, "CastCount") : tokens.Int(row, "CostCount");
                Tokens[(tower, tokens.Int(row, "Type"))] =
                    new(tokens.Int(row, "LotteryTimes"), cost, tokens.Int(row, "CostType", MoneyType.Lottery));
            }
    }

    private static void LoadAwards(SettingTable table, Dictionary<int, List<TowerScoreAward>> into)
    {
        foreach (var row in table.Rows)
        {
            int tower = table.Int(row, "Id");
            if (!into.TryGetValue(tower, out var list)) into[tower] = list = new();
            list.Add(new(table.Int(row, "OrderId"), table.Int(row, "Score"), ChapterTable.ParseTuples(table.Str(row, "Awards"))));
        }
    }

    private void LoadScoreAwards(string directory, string file, Dictionary<int, List<TowerScoreAward>> into)
    {
        if (SettingTable.Load(Path.Combine(directory, file + ".txt")) is { } table)
            LoadAwards(table, into);
    }
}
