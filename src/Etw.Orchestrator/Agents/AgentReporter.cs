using System.Diagnostics;
using System.Text;
using Etw.Orchestrator.Claude;
using Etw.Orchestrator.Runs;
using Etw.Shared.Models;
using Microsoft.Extensions.AI;

namespace Etw.Orchestrator.Agents;

public enum AgentOutcome
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>Turns one agent's output into run events: complete text lines, tool activity and start/finish markers.</summary>
public sealed class AgentReporter(RunSession session, AgentSpec spec) : IAgentReporter
{
    private readonly StringBuilder _line = new();
    private readonly object _gate = new();

    public AgentOutcome Outcome { get; private set; } = AgentOutcome.Pending;
    public string? Error { get; private set; }
    public string Name => spec.Name;
    public string NodeId => spec.NodeId;

    public void Started() => session.Events.Emit(RunEventKind.AgentStarted, "Started", spec.NodeId, spec.Name);

    public void Text(string text)
    {
        lock (_gate)
        {
            foreach (var ch in text)
            {
                if (ch == '\n') FlushLine();
                else _line.Append(ch);
            }
        }
    }

    public void Activity(string description)
    {
        lock (_gate) FlushLine(); // keep the order: text said before a tool call is shown before it
        session.Events.Emit(RunEventKind.AgentActivity, description, spec.NodeId, spec.Name);
    }

    public void Completed(TimeSpan elapsed)
    {
        lock (_gate) FlushLine();
        Outcome = AgentOutcome.Succeeded;
        session.Events.Emit(RunEventKind.AgentCompleted, $"Done in {Format(elapsed)}", spec.NodeId, spec.Name);
    }

    public void Failed(string error, TimeSpan elapsed)
    {
        lock (_gate) FlushLine();
        Outcome = AgentOutcome.Failed;
        Error = error;
        session.Events.Emit(RunEventKind.AgentFailed, $"Failed after {Format(elapsed)}: {error}", spec.NodeId, spec.Name);
    }

    public void Cancelled() => Outcome = AgentOutcome.Cancelled;

    private void FlushLine()
    {
        var text = _line.ToString().TrimEnd();
        _line.Clear();
        if (text.Length > 0) session.Events.Emit(RunEventKind.AgentText, text, spec.NodeId, spec.Name);
    }

    private static string Format(TimeSpan t) => t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds}s" : $"{t.TotalSeconds:0}s";
}

/// <summary>
/// Wraps the real chat client with the things every agent needs regardless of backend: waiting for a free parallel slot,
/// start/finish reporting, and failure isolation — one engineer failing must not take the other engineers down with it.
/// </summary>
public sealed class ReportingChatClient(IChatClient inner, AgentReporter reporter, SemaphoreSlim limiter) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, options, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (limiter.CurrentCount == 0) reporter.Activity("Waiting for a free agent slot…");
        await limiter.WaitAsync(cancellationToken);

        var clock = Stopwatch.StartNew();
        var enumerator = inner.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        Exception? failure = null;
        try
        {
            reporter.Started();
            while (true)
            {
                ChatResponseUpdate? current = null;
                try
                {
                    if (!await enumerator.MoveNextAsync()) break;
                    current = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    reporter.Cancelled();
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (failure is not null) break;
                yield return current!;
            }

            if (failure is null)
            {
                reporter.Completed(clock.Elapsed);
            }
            else
            {
                reporter.Failed(failure.Message, clock.Elapsed);
                yield return new ChatResponseUpdate(ChatRole.Assistant, $"[{reporter.Name} failed: {failure.Message}]");
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
            limiter.Release();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => inner.GetService(serviceType, serviceKey);

    public void Dispose() => inner.Dispose();
}
