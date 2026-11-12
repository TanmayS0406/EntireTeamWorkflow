using Etw.Shared.Models;

namespace Etw.Orchestrator.Runs;

public enum StageOutcome
{
    Succeeded,
    Failed,
    Skipped,
}

public sealed record StageResult(string NodeId, string Title, StageOutcome Outcome, string Detail);

/// <summary>State of one run while the orchestrator executes it.</summary>
public sealed class RunSession : IAsyncDisposable
{
    private TaskCompletionSource<List<ClarifyingQuestion>>? _answers;
    private List<ClarifyingQuestion> _pending = [];

    public RunSession(RunRequest request, RunEventSink events)
    {
        Request = request;
        Events = events;
        Limiter = new SemaphoreSlim(Math.Clamp(request.Settings.MaxParallelAgents, 1, 8));
    }

    public RunRequest Request { get; }
    public RunEventSink Events { get; }
    public SemaphoreSlim Limiter { get; }
    public CancellationTokenSource Cts { get; } = new();
    public List<StageResult> Results { get; } = [];

    /// <summary>For an existing-repositories run: where each repository (by id) was cloned and which branch the run works on.</summary>
    public Dictionary<string, GitPrepareResult> Prepared { get; } = [];
    public DateTime FinishedUtc { get; set; }
    public bool IsFinished => FinishedUtc != default;

    /// <summary>Why the run was cancelled (shown in the run log). Set before <see cref="Cts"/> is cancelled.</summary>
    public string? CancelReason { get; set; }

    /// <summary>Path of the Claude CLI used for this run (null when simulating).</summary>
    public string? CliPath { get; set; }
    public bool Simulate => Request.Settings.SimulateAgents;

    /// <summary>Pauses the run until the user answered. This is the only point where a run waits for a person.</summary>
    public async Task<List<ClarifyingQuestion>> AskAsync(List<ClarifyingQuestion> questions, CancellationToken ct)
    {
        lock (this)
        {
            _pending = questions;
            _answers = new TaskCompletionSource<List<ClarifyingQuestion>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        Events.Status(RunStatus.AwaitingInput, null, questions);
        using var registration = ct.Register(() => _answers?.TrySetCanceled(ct));
        return await _answers.Task;
    }

    /// <summary>Delivers the user's answers. Returns false when the run is not waiting.</summary>
    public bool Answer(IEnumerable<ClarifyingQuestion> answers)
    {
        TaskCompletionSource<List<ClarifyingQuestion>>? tcs;
        List<ClarifyingQuestion> merged;
        lock (this)
        {
            tcs = _answers;
            if (tcs is null || tcs.Task.IsCompleted) return false;
            var byId = answers.GroupBy(a => a.Id).ToDictionary(g => g.Key, g => g.Last().Answer ?? "");
            merged = _pending.Select(q => new ClarifyingQuestion { Id = q.Id, Text = q.Text, Answer = byId.GetValueOrDefault(q.Id, "").Trim() }).ToList();
            _answers = null;
        }
        return tcs.TrySetResult(merged);
    }

    public ValueTask DisposeAsync()
    {
        Cts.Dispose();
        return Events.DisposeAsync();
    }
}
