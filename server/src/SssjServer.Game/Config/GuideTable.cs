using SssjServer.Core;

namespace SssjServer.Game.Config;

/// <summary>
/// 一条新手指引的配置（<c>extracted/setting/UIGuide.txt</c> 里 <c>ID</c> 非空的那些行）。
///
/// 服务端只需要三列（<c>Index</c> / <c>ID</c> / <c>Type</c>）—— 指引的**内容**全在客户端，
/// 服务端唯一要管的是「这条指引做完了没有」，而那是一个任务值。
/// </summary>
public sealed class GuideEntry
{
    /// <summary>
    /// 任务变量 id（<c>Index</c> 列）。客户端 <c>GuideManager:Init</c>（GuideManager.lua:37）
    /// <c>tmp.nTaskId = tonumber(tbInfo.Index)</c>，并 assert 全表不重复（GuideManager.lua:50）。
    /// </summary>
    public int TaskId { get; init; }

    /// <summary>指引 id（<c>ID</c> 列，字符串，如 <c>Guide_Main</c>）。</summary>
    public string Id { get; init; } = "";

    /// <summary>
    /// 指引类型（<c>Type</c> 列）：<c>Force</c> 由 UI/消息自动触发（强制），
    /// <c>NonForce</c>/<c>Texture</c>/<c>NonForce_Repeat</c> 由外部手动调用
    /// （GuideManager.lua:56..60）。
    /// </summary>
    public string Type { get; init; } = "";

    public override string ToString() => $"#{TaskId} {Id}({Type})";
}

/// <summary>
/// 新手指引表（<c>UIGuide.txt</c>），服务端只用来回答一个问题：
/// **「把全部指引标记成已完成」要写哪些任务值。**
///
/// <para><b>取证</b></para>
/// <code>
/// extracted/lua/GuideManager.lua:11   local nTaskGroup = 10;
/// extracted/lua/GuideManager.lua:23   local tbFile = LoadTabFile("Guide/UIGuide.txt", 3);
/// extracted/lua/GuideManager.lua:37   tmp.nTaskId = tonumber(tbInfo.Index);
/// extracted/lua/GuideManager.lua:124  local function SetTask(nTaskId, nStepId)
///                                         if (GetTask(nTaskId) &lt; nStepId) then
///                                             me:SetTaskValue(nTaskGroup, nTaskId, nStepId);
/// extracted/lua/GuideManager.lua:136  function tbClass:DebugSetGuideComplete()
///                                         for _, info in pairs(self.tbConfig) do
///                                             SetTask(info.nTaskId, 100);          -- ← 100 = 完成
/// </code>
/// 即：**group 10、taskId = <c>Index</c> 列、值 &gt;= 100 视为该条指引已完成**。
///
/// <para><b>★ 为什么必须由服务端在登录前写好（本轮踩的坑）</b></para>
/// 客户端自带 <c>GuideManager:DebugSetGuideComplete()</c> / <c>DebugSetCurrentGuideComplete()</c>，
/// 可以用 <c>tools/gm.sh</c> 事后调用。但那两个方法会：
/// <list type="number">
///   <item><c>UI.Close("UI_Guide")</c> + <c>CloseGuide()</c> —— 如果此刻正在播引导剧情，
///     会把界面栈打空（实测 <c>UI.GetTop()</c> 变成 nil），画面卡在剧情立绘上、
///     所有按钮点不动，**看起来像「账号数据被清空、退回新手教程」**，
///     实际数据完好（背包 2044 / 卡 268 / 少女 16 全在）。
///     恢复要手工 <c>Map.LoadById(2, function() UI.Open("UI_Player"); UI.Open("UI_Background"); UI.Open("UI_Main") end)</c>。</item>
///   <item>把 100 经 pf 1027 一条条回写服务端 —— 114 条指引 = 114 个来回。</item>
/// </list>
/// 服务端在 <c>OnPlayerPrepare</c>（pf 1005 之前）就把这些任务值写好，
/// 客户端 <c>GuideManager</c> 初始化时看到的就是「全部已完成」，
/// **引导从一开始就不会触发**，既不需要事后打断，也没有回写往返。
///
/// <para><b>缺表降级</b></para>
/// 与其它配置表一致：<see cref="Load"/> 绝不抛，缺表返回空表并打 WARN。
/// </summary>
public sealed class GuideTable
{
    /// <summary>表文件名（客户端口径 "Guide/UIGuide.txt"，本目录是扁平化的）。</summary>
    public const string FileName = "UIGuide.txt";

    /// <summary>指引任务值所在 group（<c>GuideManager.lua:11 local nTaskGroup = 10</c>）。</summary>
    public const int TaskGroup = 10;

    /// <summary>
    /// 「该条指引已完成」的任务值（<c>DebugSetGuideComplete</c> 写的就是 100，GuideManager.lua:138）。
    /// 客户端判定是 <c>GetTask(nTaskId) &lt; nStepId</c> 的比较，所以 &gt;= 实际步数即算完成，
    /// 100 是官方 Debug 方法用的「远大于任何步数」的值。
    /// </summary>
    public const int CompleteValue = 100;

    private readonly List<GuideEntry> _entries = new();

    private GuideTable() { }

    /// <summary>永久空表（缺表降级用）。</summary>
    public static GuideTable Empty { get; } = new();

    /// <summary>指引条数（<c>ID</c> 非空的行数）。</summary>
    public int Count => _entries.Count;

    /// <summary>全部指引（顺序即文件顺序）。</summary>
    public IReadOnlyList<GuideEntry> All => _entries;

    /// <summary>从 setting 目录加载 UIGuide.txt。缺表返回空表并打 WARN（不抛）。</summary>
    public static GuideTable Load(string settingDir)
    {
        var table = new GuideTable();
        var raw = SettingTable.Load(Path.Combine(settingDir, FileName));
        if (raw is null)
        {
            Log.Warn($"[setting] {FileName} 缺失：无法把新手指引标记为已完成，" +
                     "GM 账号登录后仍会走引导流程");
            return table;
        }

        var seen = new HashSet<int>();
        foreach (var row in raw.Rows)
        {
            // 一条指引占多行（ID 只在第一行，其余行是 StepId 步骤）—— 只取 ID 非空的行，
            // 与客户端 GuideManager.lua:35 `if szGuideId and szGuideId ~= "" then` 一致。
            string id = raw.Str(row, "ID");
            if (id.Length == 0)
                continue;

            int taskId = raw.Int(row, "Index", -1);
            if (taskId < 0)
            {
                // 客户端在这里是 assert（GuideManager.lua:48），服务端只记一条 WARN 并跳过
                Log.Warn($"[setting] {FileName} 指引 '{id}' 的 Index 列为空/非法，跳过");
                continue;
            }
            if (!seen.Add(taskId))
            {
                Log.Warn($"[setting] {FileName} 指引 '{id}' 的 Index={taskId} 与前面的重复" +
                         "（客户端 GuideManager.lua:50 会 assert），跳过");
                continue;
            }

            table._entries.Add(new GuideEntry
            {
                TaskId = taskId,
                Id = id,
                Type = raw.Str(row, "Type"),
            });
        }

        Log.Info($"[setting] {FileName} 载入 {table._entries.Count} 条新手指引" +
                 $"（任务值 group {TaskGroup}，完成值 {CompleteValue}）");
        return table;
    }
}
