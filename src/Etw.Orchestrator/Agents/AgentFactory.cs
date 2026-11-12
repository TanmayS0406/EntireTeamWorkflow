using Etw.Orchestrator.Claude;
using Etw.Orchestrator.Runs;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Etw.Orchestrator.Agents;

/// <summary>Builds Microsoft Agent Framework agents whose brain is the Claude Code CLI (or the simulator).</summary>
public sealed class AgentFactory(ILoggerFactory loggers)
{
    public ChatClientAgent Create(RunSession session, AgentSpec spec, out AgentReporter reporter)
    {
        var rep = new AgentReporter(session, spec);

        IChatClient inner = session.Simulate
            ? new SimulatedChatClient(spec.Role, rep)
            : new ClaudeCodeChatClient(
                new ClaudeAgentOptions(
                    CliPath: session.CliPath ?? throw new InvalidOperationException("The Claude Code CLI path is not set."),
                    WorkingDirectory: session.Request.TargetFolder,
                    Model: spec.Model,
                    Effort: spec.Effort,
                    PermissionMode: session.Request.Settings.PermissionMode,
                    AllowedTools: spec.AllowedTools,
                    DisableTools: spec.NoTools,
                    Timeout: TimeSpan.FromMinutes(session.Request.Settings.AgentTimeoutMinutes)),
                rep,
                loggers.CreateLogger("Agent." + spec.Name));

        var client = new ReportingChatClient(inner, rep, session.Limiter);
        reporter = rep;

        return new ChatClientAgent(client, new ChatClientAgentOptions
        {
            Id = spec.NodeId,
            Name = spec.NodeId, // executor ids are built from this; the readable name lives in the reporter
            Description = spec.Name,
            UseProvidedChatClientAsIs = true,
            ChatOptions = new ChatOptions { Instructions = spec.Instructions },
        });
    }
}
