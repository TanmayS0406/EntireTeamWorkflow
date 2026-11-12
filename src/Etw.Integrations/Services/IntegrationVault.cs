using Etw.Hosting;
using Etw.Shared.Auth;

namespace Etw.Integrations.Services;

/// <summary>Everything this service must remember between restarts. Encrypted at rest by <see cref="SecretStore{T}"/>.</summary>
public sealed class IntegrationSecrets
{
    public string TenantId { get; set; } = "organizations";
    public string ClientId { get; set; } = EntraOAuth.DefaultClientId;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresOn { get; set; }
    public string? Account { get; set; }
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";

    // Slack: the Client ID of the Slack app, and one OAuth sign-in per workspace (a person can belong to several).
    public string SlackClientId { get; set; } = "";
    public List<SlackConnection> SlackConnections { get; set; } = [];

    /// <summary>The workspace messages are posted to.</summary>
    public string SlackActiveTeamId { get; set; } = "";

    // Teams: a Microsoft Graph token from the Entra sign-in, and the team and channel to post to.
    public string TeamsTenantId { get; set; } = "organizations";
    public string TeamsClientId { get; set; } = EntraOAuth.GraphClientId;
    public string? TeamsAccessToken { get; set; }
    public string? TeamsRefreshToken { get; set; }
    public DateTimeOffset TeamsExpiresOn { get; set; }
    public string? TeamsAccount { get; set; }
    public string TeamsTeamId { get; set; } = "";
    public string TeamsTeamName { get; set; } = "";
    public string TeamsChannelId { get; set; } = "";
    public string TeamsChannelName { get; set; } = "";
}

/// <summary>A user's sign-in to one Slack workspace and the channel of that workspace to post to.</summary>
public sealed class SlackConnection
{
    public string TeamId { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string Account { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
}

/// <summary>Thread-safe read/modify/write access to the secret document.</summary>
public sealed class IntegrationVault
{
    private readonly SecretStore<IntegrationSecrets> _store = new("integrations");
    private readonly object _gate = new();

    public IntegrationSecrets Read()
    {
        lock (_gate) return _store.Load() ?? new IntegrationSecrets();
    }

    public void Update(Action<IntegrationSecrets> change)
    {
        lock (_gate)
        {
            var current = _store.Load() ?? new IntegrationSecrets();
            change(current);
            _store.Save(current);
        }
    }
}
