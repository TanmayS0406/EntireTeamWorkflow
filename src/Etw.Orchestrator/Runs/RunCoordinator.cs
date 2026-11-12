using System.Collections.Concurrent;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Orchestrator.Runs;

/// <summary>Owns the runs that are executing in this service. Runs are in memory; the backend is the system of record.</summary>
public sealed class RunCoordinator
{
    private readonly ConcurrentDictionary<Guid, RunSession> _runs = new();
    private readonly IHttpClientFactory _http;
    private readonly PipelineRunner _pipeline;
    private readonly ILogger<RunCoordinator> _log;

    public RunCoordinator(IHttpClientFactory http, PipelineRunner pipeline, ILogger<RunCoordinator> log, IHostApplicationLifetime lifetime)
    {
        _http = http;
        _pipeline = pipeline;
        _log = log;
        lifetime.ApplicationStopping.Register(() =>
        {
            foreach (var run in _runs.Values.Where(r => !r.IsFinished)) Cancel(run.Request.RunId, "The orchestrator service is stopping.");
        });
    }

    public (bool Ok, string? Error) Start(RunRequest request)
    {
        var folderError = FolderRules.Validate(request.TargetFolder);
        if (folderError is not null) return (false, folderError);
        if (request.Stories.Count == 0) return (false, "A run needs at least one user story.");
        if (request.Workflow.Chain().Count == 0) return (false, "The workflow has no steps.");

        SweepFinished();
        var session = new RunSession(request, new RunEventSink(request.RunId, _http, _log));
        if (!_runs.TryAdd(request.RunId, session)) return (false, "This run is already executing.");

        _log.LogInformation("Starting run {Run} for project {Project} ({Stories} stories, simulate={Simulate})", request.RunId, request.ProjectName, request.Stories.Count, session.Simulate);
        _ = Task.Run(() => _pipeline.RunAsync(session));
        return (true, null);
    }

    public bool Exists(Guid runId) => _runs.ContainsKey(runId);

    public bool Answer(Guid runId, IEnumerable<ClarifyingQuestion> answers) => _runs.TryGetValue(runId, out var run) && run.Answer(answers);

    public bool Cancel(Guid runId, string? reason = null)
    {
        if (!_runs.TryGetValue(runId, out var run) || run.IsFinished) return false;
        run.CancelReason = reason ?? "Cancelled by the user.";
        run.Cts.Cancel();
        return true;
    }

    /// <summary>Forgets runs that finished a while ago so the dictionary does not grow forever.</summary>
    private void SweepFinished()
    {
        foreach (var (id, run) in _runs)
        {
            if (run.IsFinished && DateTime.UtcNow - run.FinishedUtc > TimeSpan.FromMinutes(15)) _runs.TryRemove(id, out _);
        }
    }
}
