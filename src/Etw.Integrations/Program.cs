using Etw.Hosting;
using Etw.Integrations.Services;
using Etw.Shared;
using Etw.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddEtwService(EtwConstants.IntegrationsService, EtwConstants.IntegrationsPort);

builder.Services.AddHttpClient("ado");
builder.Services.AddHttpClient("notify", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<IntegrationVault>();
builder.Services.AddSingleton<AzureDevOpsService>();
builder.Services.AddSingleton<GitRepoService>();
builder.Services.AddSingleton<NotificationService>();

var app = builder.Build();
app.UseEtwApiKey();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EtwConstants.IntegrationsService }));

// ---- Combined status for the Integrations tab ---------------------------------------------------------------------
app.MapGet("/status", (AzureDevOpsService ado, NotificationService notifications) => new
{
    azureDevOps = ado.Status(),
    notifications = notifications.Status(),
});

// ---- Azure DevOps -------------------------------------------------------------------------------------------------
var devops = app.MapGroup("/azuredevops");

devops.MapGet("/status", (AzureDevOpsService ado) => ado.Status());
devops.MapPut("/config", (OAuthConfigDto config, AzureDevOpsService ado) => { ado.SaveConfig(config); return Results.NoContent(); });
devops.MapPost("/connect", (OAuthConnectRequest request, AzureDevOpsService ado) => { ado.Connect(request); return Results.Ok(ado.Status()); });
devops.MapPost("/disconnect", (AzureDevOpsService ado) => { ado.Disconnect(); return Results.NoContent(); });
devops.MapPut("/selection", (AzureDevOpsSelection selection, AzureDevOpsService ado) =>
{
    try { ado.Select(selection); return Results.Ok(ado.Status()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

devops.MapGet("/organizations", (AzureDevOpsService ado, CancellationToken ct) => Guard(() => ado.OrganizationsAsync(ct)));
devops.MapGet("/projects", (string organization, AzureDevOpsService ado, CancellationToken ct) => Guard(() => ado.ProjectsAsync(organization, ct)));
devops.MapGet("/workitems", (string? organization, string? project, bool? includeClosed, AzureDevOpsService ado, CancellationToken ct) =>
    Guard(() => ado.BacklogAsync(organization, project, includeClosed ?? false, ct)));
devops.MapGet("/repos", (string organization, string project, AzureDevOpsService ado, CancellationToken ct) => Guard(() => ado.ReposAsync(organization, project, ct)));

// Used by the orchestrator for "existing repositories" projects: clone, branch, then commit, push and open a pull request.
var git = devops.MapGroup("/git");
git.MapPost("/prepare", (GitPrepareRequest request, GitRepoService g, CancellationToken ct) => Guard(() => g.PrepareAsync(request, ct)));
git.MapPost("/publish", (GitPublishRequest request, GitRepoService g, CancellationToken ct) => Guard(() => g.PublishAsync(request, ct)));
devops.MapPost("/workitems", (AdoCreateWorkItemRequest request, AzureDevOpsService ado, CancellationToken ct) => Guard(() => ado.CreateAsync(request, ct)));

// ---- Slack / Teams ------------------------------------------------------------------------------------------------
var notify = app.MapGroup("/notifications");

notify.MapGet("/status", (NotificationService n) => n.Status());

// Slack: the desktop app signs in (OAuth + PKCE) and hands the token over; the user then picks a channel.
var slack = notify.MapGroup("/slack");
slack.MapPut("/app", (SlackAppRequest request, NotificationService n) =>
{
    try { n.SaveSlackApp(request); return Results.Ok(n.Status()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
slack.MapPost("/connect", (SlackConnectRequest request, NotificationService n, CancellationToken ct) => Guard(async () => { await n.ConnectSlackAsync(request, ct); return n.Status(); }));
slack.MapPost("/disconnect", (NotificationService n) => { n.DisconnectSlack(null); return Results.Ok(n.Status()); });
slack.MapPut("/workspace", (SlackWorkspaceRequest request, NotificationService n) =>
{
    try { n.SelectSlackWorkspace(request); return Results.Ok(n.Status()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});
slack.MapGet("/channels", (NotificationService n, CancellationToken ct) => Guard(() => n.SlackChannelsAsync(ct)));
slack.MapPut("/selection", (SelectChannelRequest selection, NotificationService n) =>
{
    try { n.SelectSlackChannel(selection); return Results.Ok(n.Status()); }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { return Results.BadRequest(new { error = ex.Message }); }
});

// Teams: Microsoft Entra sign-in (Microsoft Graph), then the user picks a team and a channel.
var teams = notify.MapGroup("/teams");
teams.MapPut("/config", (OAuthConfigDto config, NotificationService n) => { n.SaveTeamsConfig(config); return Results.NoContent(); });
teams.MapPost("/connect", (OAuthConnectRequest request, NotificationService n) => { n.ConnectTeams(request); return Results.Ok(n.Status()); });
teams.MapPost("/disconnect", (NotificationService n) => { n.Disconnect(NotifyChannel.Teams); return Results.NoContent(); });
teams.MapGet("/list", (NotificationService n, CancellationToken ct) => Guard(() => n.TeamsAsync(ct)));
teams.MapGet("/{teamId}/channels", (string teamId, NotificationService n, CancellationToken ct) => Guard(() => n.TeamsChannelsAsync(teamId, ct)));
teams.MapPut("/selection", (SelectChannelRequest selection, NotificationService n) =>
{
    try { n.SelectTeamsChannel(selection); return Results.Ok(n.Status()); }
    catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
});

notify.MapPost("/test/{channel}", async (NotifyChannel channel, NotificationService n, CancellationToken ct) =>
{
    var result = await n.SendAsync(new NotifyRequest
    {
        Channels = [channel],
        Title = "Entire Team Workflow",
        Message = "Test notification. Your workflow can now reach this channel.",
        Level = NotifyLevel.Success,
    }, ct);
    return result.Failed.Count == 0 ? Results.Ok(result) : Results.Json(new { error = string.Join("; ", result.Failed) }, statusCode: 502);
});

// Used by the orchestrator while a run is in progress.
app.MapPost("/notify", async (NotifyRequest request, NotificationService n, CancellationToken ct) => await n.SendAsync(request, ct));

app.Run();

/// <summary>Turns service exceptions into readable JSON errors instead of 500s.</summary>
static async Task<IResult> Guard<T>(Func<Task<T>> action)
{
    try
    {
        return Results.Ok(await action());
    }
    catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or HttpRequestException)
    {
        return Results.Json(new { error = ex.Message }, statusCode: ex is ArgumentException or FormatException ? 400 : 502);
    }
}
