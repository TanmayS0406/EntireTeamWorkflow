using Etw.Backend.Data;
using Etw.Backend.Services;
using Etw.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Endpoints;

public static class RunEndpoints
{
    public static void MapRunEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapPost("/projects/{id:guid}/runs", async (Guid id, StartRunRequest? request, RunService runs, CancellationToken ct) =>
        {
            var (run, status, error) = await runs.StartAsync(id, request ?? new StartRunRequest(), ct);
            return run is null ? Results.Json(new { error }, statusCode: status) : Results.Accepted($"/api/runs/{run.Id}", run);
        });

        api.MapGet("/projects/{id:guid}/runs", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.Runs.AsNoTracking().Where(r => r.ProjectId == id).ToListAsync(ct)).OrderByDescending(r => r.StartedUtc).Take(50).ToList();
        });

        api.MapGet("/runs/{id:guid}", async (Guid id, RunService runs, CancellationToken ct) =>
            await runs.GetAsync(id, ct) is { } run ? Results.Ok(run) : Results.NotFound());

        api.MapGet("/runs/{id:guid}/events", async (Guid id, long? after, RunService runs, CancellationToken ct) =>
            await runs.EventsAsync(id, after ?? 0, ct));

        api.MapPost("/runs/{id:guid}/answers", async (Guid id, AnswerQuestionsRequest request, RunService runs, CancellationToken ct) =>
        {
            var (ok, error) = await runs.AnswerAsync(id, request, ct);
            return ok ? Results.NoContent() : Results.BadRequest(new { error });
        });

        api.MapPost("/runs/{id:guid}/cancel", async (Guid id, RunService runs, CancellationToken ct) =>
            await runs.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        // Called by the orchestrator (same API key as everything else).
        var internalApi = app.MapGroup("/internal/runs");
        internalApi.MapPost("/{id:guid}/events", async (Guid id, List<RunEventDto> events, RunService runs, CancellationToken ct) =>
        {
            await runs.IngestEventsAsync(id, events, ct);
            return Results.NoContent();
        });
        internalApi.MapPost("/{id:guid}/status", async (Guid id, RunStatusUpdate update, RunService runs, CancellationToken ct) =>
            await runs.UpdateStatusAsync(id, update, ct) is null ? Results.NotFound() : Results.NoContent());
    }
}
