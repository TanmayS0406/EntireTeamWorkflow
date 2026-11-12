using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etw.Shared;
using Etw.Shared.Auth;
using Etw.Shared.Models;

namespace Etw.Integrations.Services;

public sealed partial class AzureDevOpsService(IntegrationVault vault, IHttpClientFactory httpFactory, ILogger<AzureDevOpsService> log)
{
    private const string Api = "api-version=7.1";
    private static readonly string[] BacklogTypes = ["Epic", "Feature", "User Story", "Product Backlog Item", "Requirement", "Bug"];
    private static readonly string[] Fields =
    [
        "System.Id", "System.WorkItemType", "System.Title", "System.Description", "System.State", "System.IterationPath",
        "System.AreaPath", "System.Parent", "Microsoft.VSTS.Common.AcceptanceCriteria", "Microsoft.VSTS.Common.Priority",
        "Microsoft.VSTS.Scheduling.StoryPoints", "Microsoft.VSTS.Scheduling.Effort", "Microsoft.VSTS.Common.BacklogPriority",
    ];

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private HttpClient Http => httpFactory.CreateClient("ado");

    // ---- Connection ---------------------------------------------------------------------------------------------

    public AzureDevOpsStatusDto Status()
    {
        var s = vault.Read();
        return new AzureDevOpsStatusDto
        {
            Organization = s.Organization,
            Project = s.Project,
            Connection = new ConnectionStatusDto
            {
                Connected = !string.IsNullOrEmpty(s.RefreshToken) || (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn > DateTimeOffset.UtcNow),
                Account = s.Account,
                TenantId = s.TenantId,
                ClientId = s.ClientId,
            },
        };
    }

    public void SaveConfig(OAuthConfigDto config) => vault.Update(s =>
    {
        s.TenantId = string.IsNullOrWhiteSpace(config.TenantId) ? "organizations" : config.TenantId.Trim();
        s.ClientId = EntraOAuth.ClientOrDefault(config.ClientId);
    });

    public void Connect(OAuthConnectRequest request) => vault.Update(s =>
    {
        s.TenantId = string.IsNullOrWhiteSpace(request.TenantId) ? s.TenantId : request.TenantId.Trim();
        s.ClientId = EntraOAuth.ClientOrDefault(string.IsNullOrWhiteSpace(request.ClientId) ? s.ClientId : request.ClientId);
        s.AccessToken = request.AccessToken;
        s.RefreshToken = request.RefreshToken;
        s.ExpiresOn = request.ExpiresOn;
        s.Account = request.Account ?? EntraOAuth.AccountFromToken(request.AccessToken);
    });

    public void Disconnect() => vault.Update(s =>
    {
        s.AccessToken = null;
        s.RefreshToken = null;
        s.ExpiresOn = default;
        s.Account = null;
    });

    public void Select(AzureDevOpsSelection selection)
    {
        if (selection.Organization.Length > 0 && !OrgName().IsMatch(selection.Organization)) throw new ArgumentException("Invalid organization name.");
        vault.Update(s =>
        {
            s.Organization = selection.Organization.Trim();
            s.Project = selection.Project.Trim();
        });
    }

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        var s = vault.Read();
        if (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn - TimeSpan.FromMinutes(2) > DateTimeOffset.UtcNow) return s.AccessToken;

        await _refreshGate.WaitAsync(ct);
        try
        {
            s = vault.Read();
            if (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn - TimeSpan.FromMinutes(2) > DateTimeOffset.UtcNow) return s.AccessToken;
            if (string.IsNullOrEmpty(s.RefreshToken))
            {
                throw new InvalidOperationException("Not connected to Azure DevOps. Sign in on the Integrations tab.");
            }

            var tokens = await EntraOAuth.RefreshAsync(Http, s.TenantId, EntraOAuth.ClientOrDefault(s.ClientId), s.RefreshToken, EntraOAuth.AzureDevOpsScope, ct);
            vault.Update(v =>
            {
                v.AccessToken = tokens.AccessToken;
                v.RefreshToken = tokens.RefreshToken ?? v.RefreshToken;
                v.ExpiresOn = tokens.ExpiresOn;
            });
            return tokens.AccessToken;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Azure DevOps refresh failed: {Message}", ex.Message);
            Disconnect();
            throw new InvalidOperationException("The Azure DevOps sign-in expired. Sign in again on the Integrations tab.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---- Reading ------------------------------------------------------------------------------------------------

    public async Task<List<string>> OrganizationsAsync(CancellationToken ct)
    {
        var profile = await GetJsonAsync("https://app.vssps.visualstudio.com/_apis/profile/profiles/me?" + Api, ct);
        var id = profile?["id"]?.GetValue<string>() ?? throw new InvalidOperationException("Could not read the Azure DevOps profile.");
        var accounts = await GetJsonAsync($"https://app.vssps.visualstudio.com/_apis/accounts?memberId={Uri.EscapeDataString(id)}&" + Api, ct);
        return (accounts?["value"] as JsonArray ?? [])
            .Select(a => a?["accountName"]?.GetValue<string>())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<AdoProjectDto>> ProjectsAsync(string organization, CancellationToken ct)
    {
        var org = RequireOrg(organization);
        var json = await GetJsonAsync($"https://dev.azure.com/{org}/_apis/projects?{Api}&$top=500", ct);
        return (json?["value"] as JsonArray ?? [])
            .Select(p => new AdoProjectDto
            {
                Id = p?["id"]?.GetValue<string>() ?? "",
                Name = p?["name"]?.GetValue<string>() ?? "",
                Description = p?["description"]?.GetValue<string>(),
            })
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The repositories of an Azure DevOps project, for the project's repository picker.</summary>
    public async Task<List<AdoRepoDto>> ReposAsync(string organization, string project, CancellationToken ct)
    {
        var org = RequireOrg(organization);
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("Choose an Azure DevOps project first.");

        var json = await GetJsonAsync($"https://dev.azure.com/{org}/{Uri.EscapeDataString(project.Trim())}/_apis/git/repositories?{Api}", ct);
        return (json?["value"] as JsonArray ?? [])
            .Select(r => new AdoRepoDto
            {
                Id = r?["id"]?.GetValue<string>() ?? "",
                Name = r?["name"]?.GetValue<string>() ?? "",
                DefaultBranch = (r?["defaultBranch"]?.GetValue<string>() ?? "").Replace("refs/heads/", ""),
                WebUrl = r?["webUrl"]?.GetValue<string>(),
                IsDisabled = r?["isDisabled"]?.GetValue<bool>() == true,
            })
            .Where(r => r.Id.Length > 0 && r.Name.Length > 0 && !r.IsDisabled)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>An access token for git over https (<c>Authorization: Bearer</c>) and the REST API.</summary>
    public Task<string> AccessTokenAsync(CancellationToken ct) => TokenAsync(ct);

    /// <summary>The signed-in user (UPN), used as the author of commits made for them.</summary>
    public string? Account => vault.Read().Account;

    /// <summary>Opens a pull request from <paramref name="sourceBranch"/> into <paramref name="targetBranch"/> and links work items to it.</summary>
    public async Task<(int Id, string Url)> CreatePullRequestAsync(string organization, string project, string repoId, string repoName,
        string sourceBranch, string targetBranch, string title, string description, IEnumerable<int> workItemIds, CancellationToken ct)
    {
        var org = RequireOrg(organization);
        var body = new JsonObject
        {
            ["sourceRefName"] = "refs/heads/" + sourceBranch,
            ["targetRefName"] = "refs/heads/" + targetBranch,
            ["title"] = title.Length > 400 ? title[..400] : title,
            ["description"] = description.Length > 4000 ? description[..4000] : description,
        };
        var refs = workItemIds.Distinct().Take(50).Select(id => (JsonNode)new JsonObject { ["id"] = id.ToString() }).ToArray();
        if (refs.Length > 0) body["workItemRefs"] = new JsonArray(refs);

        var repo = Guid.TryParse(repoId, out var guid) ? guid.ToString() : Uri.EscapeDataString(repoName);
        var created = await SendJsonAsync(HttpMethod.Post, $"https://dev.azure.com/{org}/{Uri.EscapeDataString(project.Trim())}/_apis/git/repositories/{repo}/pullrequests?{Api}", body, null, ct);
        var id = created?["pullRequestId"]?.GetValue<int>() ?? throw new InvalidOperationException("Azure DevOps did not return the pull request.");
        return (id, $"https://dev.azure.com/{org}/{Uri.EscapeDataString(project.Trim())}/_git/{Uri.EscapeDataString(repoName)}/pullrequest/{id}");
    }

    /// <summary>
    /// Loads the epics, features, stories and bugs of a project, in backlog order. Without an explicit organization and
    /// project the Connect tab's default selection is used.
    /// </summary>
    public async Task<List<WorkItemDto>> BacklogAsync(string? organization, string? project, bool includeClosed, CancellationToken ct)
    {
        var s = vault.Read();
        var org = RequireOrg(string.IsNullOrWhiteSpace(organization) ? s.Organization : organization);
        var projectName = string.IsNullOrWhiteSpace(project) ? s.Project : project.Trim();
        if (string.IsNullOrWhiteSpace(projectName)) throw new InvalidOperationException("Choose an Azure DevOps project first.");

        var types = string.Join(", ", BacklogTypes.Select(t => $"'{t}'"));
        var states = includeClosed ? "" : " AND [System.State] NOT IN ('Closed', 'Done', 'Removed', 'Completed', 'Resolved')";
        var wiql = $"SELECT [System.Id] FROM WorkItems WHERE [System.TeamProject] = @project AND [System.WorkItemType] IN ({types}){states} ORDER BY [Microsoft.VSTS.Common.BacklogPriority], [System.Id]";

        var projectPath = Uri.EscapeDataString(projectName);
        var found = await SendJsonAsync(HttpMethod.Post, $"https://dev.azure.com/{org}/{projectPath}/_apis/wit/wiql?{Api}&$top=1000", new JsonObject { ["query"] = wiql }, null, ct);
        var ids = (found?["workItems"] as JsonArray ?? []).Select(w => w?["id"]?.GetValue<int>() ?? 0).Where(i => i > 0).ToList();

        var items = new List<WorkItemDto>(ids.Count);
        foreach (var chunk in ids.Chunk(200))
        {
            var batch = await SendJsonAsync(HttpMethod.Post, $"https://dev.azure.com/{org}/_apis/wit/workitemsbatch?{Api}",
                new JsonObject { ["ids"] = new JsonArray(chunk.Select(i => (JsonNode)i).ToArray()), ["fields"] = new JsonArray(Fields.Select(f => (JsonNode)f).ToArray()) }, null, ct);
            foreach (var node in batch?["value"] as JsonArray ?? []) items.Add(Map(org, projectName, node!));
        }

        // Keep the order WIQL returned (it is the backlog order).
        var order = ids.Select((id, index) => (id, index)).ToDictionary(x => x.id.ToString(), x => x.index);
        return items.OrderBy(i => order.GetValueOrDefault(i.ExternalId ?? "", int.MaxValue)).ToList();
    }

    public async Task<WorkItemDto> CreateAsync(AdoCreateWorkItemRequest request, CancellationToken ct)
    {
        var s = vault.Read();
        var org = RequireOrg(string.IsNullOrWhiteSpace(request.Organization) ? s.Organization : request.Organization);
        var project = string.IsNullOrWhiteSpace(request.Project) ? s.Project : request.Project;
        if (string.IsNullOrWhiteSpace(project)) throw new InvalidOperationException("Choose an Azure DevOps project first.");
        if (string.IsNullOrWhiteSpace(request.Title)) throw new ArgumentException("A story needs a title.");

        var patch = new JsonArray { Op("System.Title", request.Title) };
        if (!string.IsNullOrWhiteSpace(request.Description)) patch.Add(Op("System.Description", WebUtility.HtmlEncode(request.Description).Replace("\n", "<br>")));
        if (!string.IsNullOrWhiteSpace(request.AcceptanceCriteria)) patch.Add(Op("Microsoft.VSTS.Common.AcceptanceCriteria", WebUtility.HtmlEncode(request.AcceptanceCriteria).Replace("\n", "<br>")));
        if (request.Priority is { } priority) patch.Add(Op("Microsoft.VSTS.Common.Priority", priority));
        if (request.StoryPoints is { } points) patch.Add(Op("Microsoft.VSTS.Scheduling.StoryPoints", points));

        var type = Uri.EscapeDataString(string.IsNullOrWhiteSpace(request.Type) ? "User Story" : request.Type.Trim());
        var created = await SendJsonAsync(HttpMethod.Post, $"https://dev.azure.com/{org}/{Uri.EscapeDataString(project)}/_apis/wit/workitems/${type}?{Api}", patch, "application/json-patch+json", ct);
        return Map(org, project, created!);
    }

    // ---- Mapping ------------------------------------------------------------------------------------------------

    private static WorkItemDto Map(string org, string project, JsonNode node)
    {
        var f = node["fields"] as JsonObject ?? [];
        var id = (node["id"]?.GetValue<int>() ?? f["System.Id"]?.GetValue<int>() ?? 0).ToString();
        string Text(string name) => f[name]?.ToString() ?? "";
        double? Number(params string[] names) => names.Select(n => f[n]).FirstOrDefault(v => v is not null)?.GetValue<double>();

        var assigned = Number("Microsoft.VSTS.Common.Priority");
        return new WorkItemDto
        {
            Source = WorkItemSource.AzureDevOps,
            ExternalId = id,
            ExternalUrl = $"https://dev.azure.com/{org}/{Uri.EscapeDataString(project)}/_workitems/edit/{id}",
            ParentExternalId = f["System.Parent"]?.ToString(),
            Type = Text("System.WorkItemType"),
            Title = Text("System.Title"),
            Description = HtmlToText(Text("System.Description")),
            AcceptanceCriteria = HtmlToText(Text("Microsoft.VSTS.Common.AcceptanceCriteria")),
            State = Text("System.State"),
            IterationPath = f["System.IterationPath"]?.ToString(),
            AreaPath = f["System.AreaPath"]?.ToString(),
            Priority = assigned is { } p ? (int)p : null,
            StoryPoints = Number("Microsoft.VSTS.Scheduling.StoryPoints", "Microsoft.VSTS.Scheduling.Effort"),
            UpdatedUtc = DateTime.UtcNow,
        };
    }

    /// <summary>Azure DevOps stores rich text as HTML; agents get plain text.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var text = BlockBreak().Replace(html, "\n");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(" ", " ");
        text = ExtraBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    private static JsonObject Op(string field, JsonNode value) => new() { ["op"] = "add", ["path"] = "/fields/" + field, ["value"] = value };

    private static string RequireOrg(string organization)
    {
        var org = organization.Trim();
        if (!OrgName().IsMatch(org)) throw new InvalidOperationException("Enter a valid Azure DevOps organization name (letters, numbers and hyphens).");
        return org;
    }

    // ---- HTTP ---------------------------------------------------------------------------------------------------

    private Task<JsonNode?> GetJsonAsync(string url, CancellationToken ct) => SendJsonAsync(HttpMethod.Get, url, null, null, ct);

    private async Task<JsonNode?> SendJsonAsync(HttpMethod method, string url, JsonNode? body, string? contentType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, contentType ?? "application/json");
            if (contentType is not null) request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);

        // A rejected token is often answered with an HTML sign-in page, even with a 2xx status.
        var html = text.TrimStart().StartsWith('<');
        if (!response.IsSuccessStatusCode || html)
        {
            var message = TryMessage(text) ?? $"HTTP {(int)response.StatusCode}";
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || html)
            {
                message = "Azure DevOps rejected the request (sign in again, and check that you can access this organization. If you use your own app registration, it needs the Azure DevOps user_impersonation permission).";
            }
            throw new InvalidOperationException(message);
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string? TryMessage(string text)
    {
        try { return JsonNode.Parse(text)?["message"]?.GetValue<string>(); }
        catch (JsonException) { return null; }
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,63}$")]
    private static partial Regex OrgName();

    [GeneratedRegex(@"</(p|div|li|h[1-6]|tr)>|<br\s*/?>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"(\s*\n){3,}")]
    private static partial Regex ExtraBlankLines();
}
