using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Etw.Hosting;
using Etw.Shared.Auth;
using Etw.Shared.Models;

namespace Etw.Azure.Services;

public sealed class AzureSecrets
{
    public string TenantId { get; set; } = "organizations";
    public string ClientId { get; set; } = EntraOAuth.DefaultClientId;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresOn { get; set; }
    public string? Account { get; set; }
}

/// <summary>Holds the Entra sign-in (handed over by the desktop app) and talks to Azure Resource Manager with it.</summary>
public sealed class ArmClient(IHttpClientFactory httpFactory, ILogger<ArmClient> log)
{
    private const string Arm = "https://management.azure.com";
    private readonly SecretStore<AzureSecrets> _store = new("azure");
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    private HttpClient Http => httpFactory.CreateClient("arm");

    // ---- Connection ---------------------------------------------------------------------------------------------

    public ConnectionStatusDto Status()
    {
        var s = _store.Load() ?? new AzureSecrets();
        return new ConnectionStatusDto
        {
            Connected = !string.IsNullOrEmpty(s.RefreshToken) || (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn > DateTimeOffset.UtcNow),
            Account = s.Account,
            TenantId = s.TenantId,
            ClientId = s.ClientId,
        };
    }

    public void SaveConfig(OAuthConfigDto config)
    {
        var s = _store.Load() ?? new AzureSecrets();
        s.TenantId = string.IsNullOrWhiteSpace(config.TenantId) ? "organizations" : config.TenantId.Trim();
        s.ClientId = EntraOAuth.ClientOrDefault(config.ClientId);
        _store.Save(s);
    }

    public void Connect(OAuthConnectRequest request)
    {
        var s = _store.Load() ?? new AzureSecrets();
        s.TenantId = string.IsNullOrWhiteSpace(request.TenantId) ? s.TenantId : request.TenantId.Trim();
        s.ClientId = EntraOAuth.ClientOrDefault(string.IsNullOrWhiteSpace(request.ClientId) ? s.ClientId : request.ClientId);
        s.AccessToken = request.AccessToken;
        s.RefreshToken = request.RefreshToken;
        s.ExpiresOn = request.ExpiresOn;
        s.Account = request.Account ?? EntraOAuth.AccountFromToken(request.AccessToken);
        _store.Save(s);
    }

    public void Disconnect()
    {
        var s = _store.Load() ?? new AzureSecrets();
        s.AccessToken = null;
        s.RefreshToken = null;
        s.ExpiresOn = default;
        s.Account = null;
        _store.Save(s);
    }

    public async Task<string> TokenAsync(CancellationToken ct)
    {
        var s = _store.Load() ?? new AzureSecrets();
        if (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn - TimeSpan.FromMinutes(2) > DateTimeOffset.UtcNow) return s.AccessToken;

        await _refreshGate.WaitAsync(ct);
        try
        {
            s = _store.Load() ?? new AzureSecrets();
            if (!string.IsNullOrEmpty(s.AccessToken) && s.ExpiresOn - TimeSpan.FromMinutes(2) > DateTimeOffset.UtcNow) return s.AccessToken;
            if (string.IsNullOrEmpty(s.RefreshToken))
            {
                throw new InvalidOperationException("Not connected to Azure. Sign in on the Integrations tab.");
            }

            var tokens = await EntraOAuth.RefreshAsync(Http, s.TenantId, EntraOAuth.ClientOrDefault(s.ClientId), s.RefreshToken, EntraOAuth.AzureArmScope, ct);
            s.AccessToken = tokens.AccessToken;
            s.RefreshToken = tokens.RefreshToken ?? s.RefreshToken;
            s.ExpiresOn = tokens.ExpiresOn;
            _store.Save(s);
            return tokens.AccessToken;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Azure token refresh failed: {Message}", ex.Message);
            Disconnect();
            throw new InvalidOperationException("The Azure sign-in expired. Sign in again on the Integrations tab.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    // ---- Queries ------------------------------------------------------------------------------------------------

    public async Task<List<AzureSubscriptionDto>> SubscriptionsAsync(CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Arm}/subscriptions?api-version=2022-12-01", null, ct);
        return (json?["value"] as JsonArray ?? [])
            .Select(x => new AzureSubscriptionDto
            {
                Id = x?["subscriptionId"]?.GetValue<string>() ?? "",
                Name = x?["displayName"]?.GetValue<string>() ?? "",
                State = x?["state"]?.GetValue<string>() ?? "",
            })
            .Where(x => x.State == "Enabled")
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<AzureResourceGroupDto>> ResourceGroupsAsync(string subscriptionId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Arm}/subscriptions/{Guid.Parse(subscriptionId)}/resourcegroups?api-version=2021-04-01", null, ct);
        return (json?["value"] as JsonArray ?? [])
            .Select(x => new AzureResourceGroupDto { Name = x?["name"]?.GetValue<string>() ?? "", Location = x?["location"]?.GetValue<string>() ?? "" })
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<List<AzureLocationDto>> LocationsAsync(string subscriptionId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"{Arm}/subscriptions/{Guid.Parse(subscriptionId)}/locations?api-version=2022-12-01", null, ct);
        return (json?["value"] as JsonArray ?? [])
            .Where(x => x?["metadata"]?["regionType"]?.GetValue<string>() == "Physical")
            .Select(x => new AzureLocationDto { Name = x?["name"]?.GetValue<string>() ?? "", DisplayName = x?["displayName"]?.GetValue<string>() ?? "" })
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<AzureResourceGroupDto> EnsureResourceGroupAsync(string subscriptionId, string name, string location, CancellationToken ct)
    {
        var url = $"{Arm}/subscriptions/{Guid.Parse(subscriptionId)}/resourcegroups/{Uri.EscapeDataString(name)}?api-version=2021-04-01";
        var existing = await SendAsync(HttpMethod.Get, url, null, ct, allowNotFound: true);
        if (existing is not null)
        {
            return new AzureResourceGroupDto { Name = name, Location = existing["location"]?.GetValue<string>() ?? location };
        }
        var created = await SendAsync(HttpMethod.Put, url, new JsonObject { ["location"] = location }, ct);
        return new AzureResourceGroupDto { Name = name, Location = created?["location"]?.GetValue<string>() ?? location };
    }

    // ---- HTTP ---------------------------------------------------------------------------------------------------

    /// <summary>Sends an ARM request with the user's token. Throws with Azure's own error message on failure.</summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct, bool allowNotFound = false)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(ct));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound && allowNotFound) return null;
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(text, response.StatusCode));
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private static string ErrorMessage(string text, HttpStatusCode status)
    {
        try
        {
            var error = JsonNode.Parse(text)?["error"];
            var message = error?["message"]?.GetValue<string>();
            var code = error?["code"]?.GetValue<string>();
            if (message is not null) return code is null ? message : $"{message} ({code})";
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            // Not an ARM error body.
        }
        return $"Azure returned HTTP {(int)status}.";
    }
}
