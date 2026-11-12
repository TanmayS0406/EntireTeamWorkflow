using System.Net.Http.Json;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Orchestrator.Runs;

/// <summary>Sends notifications through the integrations service. Never throws: a failed notification must not fail a run.</summary>
public sealed class Notifier(IHttpClientFactory http, ILogger<Notifier> log)
{
    public async Task<NotifyResultDto> SendAsync(NotifyChannel channel, Guid runId, string title, string message, NotifyLevel level, CancellationToken ct)
    {
        try
        {
            var request = new NotifyRequest { Channels = [channel], Title = title, Message = message, Level = level, RunId = runId };
            using var response = await http.CreateClient("integrations").PostAsJsonAsync("/notify", request, EtwJson.Options, ct);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadFromJsonAsync<NotifyResultDto>(EtwJson.Options, ct) ?? new NotifyResultDto();
            }
            return new NotifyResultDto { Failed = [$"{channel}: integrations service returned {(int)response.StatusCode}"] };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Notification failed: {Message}", ex.Message);
            return new NotifyResultDto { Failed = [$"{channel}: {ex.Message}"] };
        }
    }
}

/// <summary>
/// Git for the repositories of an existing-repositories project, through the integrations service (which holds the
/// Azure DevOps sign-in): clone and branch before the run, commit, push and open a pull request after it.
/// </summary>
public sealed class RepoClient(IHttpClientFactory http)
{
    public Task<GitPrepareResult> PrepareAsync(GitPrepareRequest request, CancellationToken ct) => PostAsync<GitPrepareRequest, GitPrepareResult>("/azuredevops/git/prepare", request, ct);

    public Task<GitPublishResult> PublishAsync(GitPublishRequest request, CancellationToken ct) => PostAsync<GitPublishRequest, GitPublishResult>("/azuredevops/git/publish", request, ct);

    private async Task<TResult> PostAsync<TRequest, TResult>(string path, TRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient("integrations-git").PostAsJsonAsync(path, request, EtwJson.Options, ct);
            if (!response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                var message = text;
                try { message = System.Text.Json.JsonDocument.Parse(text).RootElement.GetProperty("error").GetString() ?? text; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException) { /* not an error body */ }
                throw new StageFailedException(message.Length > 0 ? message : $"The integrations service answered HTTP {(int)response.StatusCode}.");
            }
            return await response.Content.ReadFromJsonAsync<TResult>(EtwJson.Options, ct) ?? throw new StageFailedException("The integrations service sent an empty answer.");
        }
        catch (HttpRequestException ex)
        {
            throw new StageFailedException("The integrations service is not reachable (" + ex.Message + "). Start the EtwIntegrations service.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new StageFailedException("The integrations service took too long to answer (a very large repository?).");
        }
    }
}

/// <summary>Asks the Azure service to deploy the release artifacts. Deployments can take several minutes.</summary>
public sealed class AzureDeployClient(IHttpClientFactory http)
{
    public async Task<DeployResultDto> DeployAsync(DeployRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient("azure").PostAsJsonAsync("/deploy", request, EtwJson.Options, ct);
            var result = await response.Content.ReadFromJsonAsync<DeployResultDto>(EtwJson.Options, ct);
            return result ?? new DeployResultDto { Error = $"The Azure service returned {(int)response.StatusCode}." };
        }
        catch (HttpRequestException ex)
        {
            return new DeployResultDto { Error = "The Azure service is not reachable (" + ex.Message + "). Start the EtwAzure service." };
        }
    }
}
