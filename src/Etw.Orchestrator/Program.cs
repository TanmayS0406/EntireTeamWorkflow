using Etw.Hosting;
using Etw.Orchestrator.Agents;
using Etw.Orchestrator.Runs;
using Etw.Shared;
using Etw.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddEtwService(EtwConstants.OrchestratorService, EtwConstants.OrchestratorPort);

builder.Services.AddEtwClient("backend", EtwConstants.BackendUrl);
builder.Services.AddEtwClient("integrations", EtwConstants.IntegrationsUrl);
builder.Services.AddEtwClient("azure", EtwConstants.AzureUrl, TimeSpan.FromMinutes(45));
builder.Services.AddEtwClient("integrations-git", EtwConstants.IntegrationsUrl, TimeSpan.FromMinutes(25)); // cloning a large repository takes a while
builder.Services.AddSingleton<AgentFactory>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<RepoClient>();
builder.Services.AddSingleton<AzureDeployClient>();
builder.Services.AddSingleton<PipelineRunner>();
builder.Services.AddSingleton<RunCoordinator>();

var app = builder.Build();
app.UseEtwApiKey();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EtwConstants.OrchestratorService }));

app.MapPost("/runs", (RunRequest request, RunCoordinator coordinator) =>
{
    var (ok, error) = coordinator.Start(request);
    return ok ? Results.Accepted($"/runs/{request.RunId}") : Results.BadRequest(new { error });
});

app.MapGet("/runs/{id:guid}", (Guid id, RunCoordinator coordinator) => coordinator.Exists(id) ? Results.Ok() : Results.NotFound());

app.MapPost("/runs/{id:guid}/answers", (Guid id, AnswerQuestionsRequest request, RunCoordinator coordinator) =>
    coordinator.Answer(id, request.Answers) ? Results.NoContent() : Results.Conflict(new { error = "The run is not waiting for answers." }));

app.MapPost("/runs/{id:guid}/cancel", (Guid id, RunCoordinator coordinator) =>
    coordinator.Cancel(id) ? Results.NoContent() : Results.NotFound());

app.Run();
