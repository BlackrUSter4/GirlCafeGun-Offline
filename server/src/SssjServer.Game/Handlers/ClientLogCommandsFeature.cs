using System.Text.Json.Nodes;
using SssjServer.Core;

namespace SssjServer.Game.Handlers;

/// <summary>
/// Telemetry the client sends and never waits on: it passes noReply to CallServer and registers no
/// reply handler for these. They carry nothing a private server needs to act on, so they are accepted
/// quietly. Otherwise each one logs "unhandled" and hides the commands that really are missing.
/// </summary>
public sealed class ClientLogCommandsFeature : IGameFeature
{
    private static readonly int[] Commands =
    {
        100,  // WriteLevelCompleteLog
        103,  // WriteCQuitLog
        105,  // WriteSkillDamageLog
        151,  // ClientError
        152,  // PayFail
        223,  // WriteReportLog
        280,  // EvaluateGuideLog
        282,  // RikiBGMLog
        300,  // WriteTssLog
        391,  // MusicRelayLog
    };

    public string Name => "客户端日志类命令（只收不回）";

    public void Install(GameFeatureContext ctx)
    {
        foreach (int command in Commands)
            ctx.MapLuaCall(command, Accept);
    }

    private static Task Accept(ClientSession session, int cmd, JsonNode? param)
    {
        Log.Debug($"[session {session.Id}] 客户端日志 cmd={cmd} {param?.ToJsonString()}");
        return Task.CompletedTask;
    }
}
