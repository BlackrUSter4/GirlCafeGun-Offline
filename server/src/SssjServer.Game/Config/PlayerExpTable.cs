using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>一行玩家等级配置（<c>extracted/setting/PlayerExp.txt</c>）。</summary>
public sealed class PlayerLevelRow
{
    /// <summary>指挥官等级。</summary>
    public int Level { get; init; }

    /// <summary>升到下一级需要的经验（表头「升到下一级需要经验」）。0 表示满级。</summary>
    public int Exp { get; init; }

    /// <summary>该等级的体力上限（表头「体力上限」，客户端 <c>me.LevelMaxSpirit</c>）。</summary>
    public int Spirit { get; init; }

    /// <summary>体力恢复（表头「体力多久恢复」）。</summary>
    public int SpiritRecover { get; init; }

    /// <summary>该等级咖啡馆总等级上限。</summary>
    public int Coffee { get; init; }
}

/// <summary>
/// <c>PlayerExp.txt</c>：等级 → 升级经验 / 体力上限 / 体力恢复。
///
/// 取证：客户端 <c>PlayerLogic.lua:21</c> <c>PLAYER_EXP_PATH="Player/PlayerExp.txt"</c>
/// 与 <c>PlayerLogic:LoadPlayerExpConfig()</c>（lua:109）读的是同一张表，列名相同。
/// 体力恢复速率取证：<c>Notification.lua:164</c>
/// <code>nMin = nMin + 6 * (me.LevelMaxSpirit - me.vigour - 1)</code>
/// —— 客户端按 **每 6 分钟 1 点体力** 估算「回满还要多久」，故服务端用同一速率。
/// </summary>
public sealed class PlayerExpTable
{
    /// <summary>体力自然恢复间隔（秒）：6 分钟 1 点，见 Notification.lua:164。</summary>
    public const int VigourRecoverSeconds = 6 * 60;

    private readonly Dictionary<int, PlayerLevelRow> _byLevel = new();

    private PlayerExpTable() { }

    /// <summary>空表（配置缺失/单测用）：体力上限回退 28，不做升级判定。</summary>
    public static PlayerExpTable Empty { get; } = new();

    public int Count => _byLevel.Count;

    /// <summary>最高配置等级（超过它视为满级）。</summary>
    public int MaxLevel { get; private set; }

    public PlayerLevelRow? Get(int level) => _byLevel.TryGetValue(level, out var r) ? r : null;

    /// <summary>该等级体力上限；查不到时回退 <paramref name="fallback"/>。</summary>
    public int MaxVigour(int level, int fallback = 28)
    {
        var row = Get(level);
        if (row is not null && row.Spirit > 0)
            return row.Spirit;
        return fallback;
    }

    /// <summary>升到下一级所需经验；0 表示已满级（不再升）。</summary>
    public int ExpToNext(int level) => Get(level)?.Exp ?? 0;

    public static PlayerExpTable Load(string settingDir)
    {
        var table = new PlayerExpTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, "PlayerExp.txt"));
        if (raw is null)
        {
            Log.Warn("[setting] PlayerExp.txt 缺失：体力上限按 28、不做升级判定");
            return table;
        }

        foreach (var row in raw.Rows)
        {
            int level = raw.Int(row, "Level", -1);
            if (level < 0)
                continue;
            table._byLevel[level] = new PlayerLevelRow
            {
                Level = level,
                Exp = raw.Int(row, "Exp"),
                Spirit = raw.Int(row, "Spirit"),
                SpiritRecover = raw.Int(row, "SpiritRecover"),
                Coffee = raw.Int(row, "Coffee"),
            };
            if (level > table.MaxLevel)
                table.MaxLevel = level;
        }
        Log.Info($"[setting] PlayerExp.txt 载入 {table._byLevel.Count} 级（最高 {table.MaxLevel} 级）");
        return table;
    }
}
