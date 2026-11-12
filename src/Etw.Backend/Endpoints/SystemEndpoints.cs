using Etw.Backend.Services;
using Etw.Shared;
using Etw.Shared.Claude;
using Etw.Shared.Models;

namespace Etw.Backend.Endpoints;

public static class SystemEndpoints
{
    public static void MapSystemEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = EtwConstants.BackendService }));

        var api = app.MapGroup("/api");

        // Sign-in status as the backend sees it. Agents run in this account, so this is the one that matters.
        api.MapGet("/claude/status", async (SettingsService settingsService, CancellationToken ct) =>
        {
            var settings = await settingsService.GetAsync(ct);
            var cli = ClaudeCli.Locate(settings.ClaudePath);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var status = await ClaudeCli.GetAuthStatusAsync(cli, timeout.Token);
            return new ClaudeStatusDto
            {
                CliFound = status.CliFound,
                LoggedIn = status.LoggedIn,
                Account = status.Account,
                CliPath = cli,
                Detail = status.Detail,
            };
        });

        api.MapGet("/settings", async (SettingsService settings, CancellationToken ct) => await settings.GetAsync(ct));

        api.MapPut("/settings", async (AppSettingsDto request, SettingsService settings, CancellationToken ct) =>
        {
            var (saved, error) = await settings.SaveAsync(request, ct);
            return saved is null ? Results.BadRequest(new { error }) : Results.Ok(saved);
        });

        api.MapGet("/services", async (IHttpClientFactory factory, CancellationToken ct) =>
        {
            var targets = new (string Name, string Url)[]
            {
                (EtwConstants.BackendService, EtwConstants.BackendUrl),
                (EtwConstants.IntegrationsService, EtwConstants.IntegrationsUrl),
                (EtwConstants.OrchestratorService, EtwConstants.OrchestratorUrl),
                (EtwConstants.AzureService, EtwConstants.AzureUrl),
            };
            var http = factory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(2);

            var checks = targets.Select(async t =>
            {
                try
                {
                    using var response = await http.GetAsync(t.Url + "/health", ct);
                    return new ServiceHealthDto { Name = t.Name, Url = t.Url, Healthy = response.IsSuccessStatusCode };
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    return new ServiceHealthDto { Name = t.Name, Url = t.Url, Healthy = false, Detail = "Not running" };
                }
            });
            return await Task.WhenAll(checks);
        });
    }
}
