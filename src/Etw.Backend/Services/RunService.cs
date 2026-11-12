using Etw.Backend.Data;
using Etw.Backend.Hubs;
using Etw.Shared;
using Etw.Shared.Claude;
using Etw.Shared.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Services;

public sealed class RunService(
    IDbContextFactory<EtwDb> dbFactory,
    IHubContext<RunsHub> hub,
    OrchestratorClient orchestrator,
    SettingsService settingsService,
    ILogger<RunService> log)
{
    public static bool IsTerminal(RunStatus status) => status is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled;

    public async Task<(RunDto? Run, int Status, string? Error)> StartAsync(Guid projectId, StartRunRequest request, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, ct);
        if (project is null) return (null, 404, "Project not found.");

        var stories = request.WorkItemIds.Count > 0
            ? await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == projectId && request.WorkItemIds.Contains(w.Id)).ToListAsync(ct)
            : await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == projectId && w.Selected).ToListAsync(ct);
        if (stories.Count == 0) return (null, 400, "Select at least one user story first (Backlog tab).");

        var ruleError = FolderRules.Validate(project.TargetFolder);
        if (ruleError is not null) return (null, 400, ruleError);

        if (project.Mode == ProjectMode.ExistingRepos && (project.Repos.Count == 0 || project.AzureDevOpsOrganization.Length == 0 || project.AzureDevOpsProject.Length == 0))
        {
            return (null, 400, "This project has no repositories yet. Choose its Azure DevOps project and repositories first.");
        }

        var projectRuns = await db.Runs.AsNoTracking().Where(r => r.ProjectId == projectId).ToListAsync(ct);
        foreach (var active in projectRuns.Where(r => !IsTerminal(r.Status)))
        {
            var reconciled = await ReconcileAsync(active, ct);
            if (!IsTerminal(reconciled.Status)) return (null, 409, "This project already has a run in progress.");
        }

        var settings = await settingsService.GetAsync(ct);
        if (!settings.SimulateAgents)
        {
            var cli = ClaudeCli.Locate(settings.ClaudePath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var status = await ClaudeCli.GetAuthStatusAsync(cli, timeout.Token);
            if (!status.CliFound) return (null, 400, "Claude Code CLI not found. Install it or set its path in Settings.");
            if (!status.LoggedIn) return (null, 400, "The backend service is not signed in to Claude. If it runs as a different Windows account than you, reinstall it with -ServiceUser (see README).");
        }

        try
        {
            Directory.CreateDirectory(project.TargetFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 400, $"Cannot use the target folder: {ex.Message}");
        }

        var run = new RunDto
        {
            Id = Guid.NewGuid(),
            ProjectId = projectId,
            Status = RunStatus.Running,
            StartedUtc = DateTime.UtcNow,
            StoryCount = stories.Count,
        };
        db.Runs.Add(run);
        await db.SaveChangesAsync(ct);

        var runRequest = new RunRequest
        {
            RunId = run.Id,
            ProjectId = projectId,
            ProjectName = project.Name,
            TargetFolder = project.TargetFolder,
            BackendLanguage = project.BackendLanguage,
            UiFramework = project.UiFramework,
            Mode = project.Mode,
            AzureDevOpsOrganization = project.AzureDevOpsOrganization,
            AzureDevOpsProject = project.AzureDevOpsProject,
            Repos = project.Repos,
            Stories = stories,
            Workflow = project.Workflow,
            Settings = settings,
        };

        var (ok, error) = await orchestrator.StartAsync(runRequest, ct);
        if (!ok)
        {
            await UpdateStatusAsync(run.Id, new RunStatusUpdate { Status = RunStatus.Failed, Summary = error }, ct);
            await IngestEventsAsync(run.Id, [new RunEventDto { Kind = RunEventKind.RunFailed, Message = error ?? "The orchestrator rejected the run." }], ct);
            return (null, 503, error);
        }

        await hub.Clients.All.SendAsync("runStatus", run, ct);
        return (run, 202, null);
    }

    public async Task<RunDto?> GetAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return run is null ? null : await ReconcileAsync(run, ct);
    }

    /// <summary>A run that is not finished but that the orchestrator no longer knows (it was restarted) can never finish: fail it.</summary>
    private async Task<RunDto> ReconcileAsync(RunDto run, CancellationToken ct)
    {
        if (IsTerminal(run.Status) || DateTime.UtcNow - run.StartedUtc < TimeSpan.FromSeconds(10)) return run;
        if (await orchestrator.KnowsRunAsync(run.Id, ct) != false) return run;

        const string reason = "The orchestrator service was restarted while this run was in progress.";
        await IngestEventsAsync(run.Id, [new RunEventDto { Kind = RunEventKind.RunFailed, Message = reason }], ct);
        return await UpdateStatusAsync(run.Id, new RunStatusUpdate { Status = RunStatus.Failed, Summary = reason }, ct) ?? run;
    }

    public async Task<List<RunEventDto>> EventsAsync(Guid runId, long after, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.RunEvents.AsNoTracking().Where(e => e.RunId == runId && e.Id > after).OrderBy(e => e.Id).Take(2000).ToListAsync(ct);
    }

    public async Task IngestEventsAsync(Guid runId, IReadOnlyList<RunEventDto> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        foreach (var e in events)
        {
            e.Id = 0;
            e.RunId = runId;
            if (e.TimeUtc == default) e.TimeUtc = DateTime.UtcNow;
            db.RunEvents.Add(e);
        }
        await db.SaveChangesAsync(ct);
        foreach (var e in events) await hub.Clients.All.SendAsync("runEvent", e, ct);
    }

    public async Task<RunDto?> UpdateStatusAsync(Guid runId, RunStatusUpdate update, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var run = await db.Runs.FirstOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null) return null;
        if (IsTerminal(run.Status) && !IsTerminal(update.Status)) return run; // a late update must not revive a finished run

        run.Status = update.Status;
        if (update.Summary is not null) run.Summary = update.Summary;
        run.PendingQuestions = update.Status == RunStatus.AwaitingInput ? update.PendingQuestions ?? [] : [];
        if (IsTerminal(update.Status)) run.EndedUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        log.LogInformation("Run {Run} is now {Status}", runId, run.Status);
        await hub.Clients.All.SendAsync("runStatus", run, ct);
        return run;
    }

    public async Task<(bool Ok, string? Error)> AnswerAsync(Guid runId, AnswerQuestionsRequest request, CancellationToken ct)
    {
        var run = await GetAsync(runId, ct);
        if (run is null) return (false, "Run not found.");
        if (run.Status != RunStatus.AwaitingInput) return (false, "This run is not waiting for answers.");
        return await orchestrator.AnswerAsync(runId, request, ct);
    }

    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
    {
        var run = await GetAsync(runId, ct);
        if (run is null || IsTerminal(run.Status)) return false;
        if (!await orchestrator.CancelAsync(runId, ct))
        {
            // The orchestrator is gone: close the run ourselves.
            await UpdateStatusAsync(runId, new RunStatusUpdate { Status = RunStatus.Cancelled, Summary = "Cancelled." }, ct);
        }
        return true;
    }
}
