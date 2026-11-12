using System.Net.Http.Json;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Backend.Services;

/// <summary>Calls the orchestrator microservice.</summary>
public sealed class OrchestratorClient(IHttpClientFactory factory)
{
    private HttpClient Http => factory.CreateClient("orchestrator");

    public async Task<(bool Ok, string? Error)> StartAsync(RunRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync("/runs", request, EtwJson.Options, ct);
            return response.IsSuccessStatusCode ? (true, null) : (false, await ReadError(response, ct));
        }
        catch (HttpRequestException ex)
        {
            return (false, "The orchestrator service is not reachable (" + ex.Message + "). Start the EtwOrchestrator service.");
        }
    }

    public async Task<(bool Ok, string? Error)> AnswerAsync(Guid runId, AnswerQuestionsRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync($"/runs/{runId}/answers", request, EtwJson.Options, ct);
            return response.IsSuccessStatusCode ? (true, null) : (false, await ReadError(response, ct));
        }
        catch (HttpRequestException ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<bool> CancelAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsync($"/runs/{runId}/cancel", null, ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>True/false when the orchestrator answered, null when it could not be reached.</summary>
    public async Task<bool?> KnowsRunAsync(Guid runId, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync($"/runs/{runId}", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task<string> ReadError(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? $"HTTP {(int)response.StatusCode}" : text;
    }
}

/// <summary>Calls the integrations microservice (Azure DevOps, notifications).</summary>
public sealed class IntegrationsClient(IHttpClientFactory factory)
{
    private HttpClient Http => factory.CreateClient("integrations");

    /// <summary>Loads a backlog. Empty organization/project mean the default chosen on the Connect tab.</summary>
    public async Task<(List<WorkItemDto>? Items, string? Error)> FetchBacklogAsync(string organization, string project, bool includeClosed, CancellationToken ct)
    {
        try
        {
            var url = $"/azuredevops/workitems?includeClosed={includeClosed.ToString().ToLowerInvariant()}";
            if (organization.Length > 0 && project.Length > 0) url += $"&organization={Uri.EscapeDataString(organization)}&project={Uri.EscapeDataString(project)}";
            using var response = await Http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return (null, await response.Content.ReadAsStringAsync(ct));
            return (await response.Content.ReadFromJsonAsync<List<WorkItemDto>>(EtwJson.Options, ct) ?? [], null);
        }
        catch (HttpRequestException ex)
        {
            return (null, "The integrations service is not reachable (" + ex.Message + ").");
        }
    }

    public async Task<(WorkItemDto? Item, string? Error)> CreateWorkItemAsync(AdoCreateWorkItemRequest request, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync("/azuredevops/workitems", request, EtwJson.Options, ct);
            if (!response.IsSuccessStatusCode) return (null, await response.Content.ReadAsStringAsync(ct));
            return (await response.Content.ReadFromJsonAsync<WorkItemDto>(EtwJson.Options, ct), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, "The integrations service is not reachable (" + ex.Message + ").");
        }
    }
}
