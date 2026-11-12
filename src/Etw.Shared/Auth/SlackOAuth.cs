using System.Text.Json.Nodes;

namespace Etw.Shared.Auth;

/// <summary>A Slack user token. <see cref="ExpiresOn"/> is null when the workspace does not rotate tokens.</summary>
public sealed record SlackTokens(string AccessToken, string? RefreshToken, DateTimeOffset? ExpiresOn);

/// <summary>
/// Slack OAuth v2 with PKCE for a public client: no client secret, and a http://localhost redirect, which Slack allows
/// for desktop apps. Slack does not let such an app ask for bot scopes, so messages are posted as the signed-in user.
/// </summary>
public static class SlackOAuth
{
    /// <summary>
    /// The Client ID of the Slack app, when it is built in (it is public, not a secret). Whoever ships the product can set it
    /// here, or with the ETW_SLACK_CLIENT_ID environment variable, and nobody is ever asked for it. When empty, the
    /// first sign-in walks the user through creating the app once (<see cref="CreateAppUrl"/>) and the app remembers its Client ID.
    /// </summary>
    public const string DefaultClientId = "";

    public static string ClientId => Environment.GetEnvironmentVariable("ETW_SLACK_CLIENT_ID") is { Length: > 0 } fromEnvironment
        ? fromEnvironment.Trim()
        : DefaultClientId;

    public static bool IsConfigured => ClientId.Length > 0;

    public const string UserScope = "chat:write,channels:read,groups:read";

    /// <summary>A Slack Client ID is two numbers joined by a dot, e.g. 1234567890.1234567890123.</summary>
    public static bool IsValidClientId(string? clientId) =>
        clientId is { Length: >= 10 and <= 60 } && clientId.Count(c => c == '.') == 1 && clientId.All(c => char.IsAsciiDigit(c) || c == '.') && clientId[0] != '.' && clientId[^1] != '.';

    /// <summary>The Slack app definition: user scopes only, PKCE on, and the loopback redirect URLs the sign-in uses.</summary>
    public static string BuildManifestJson() => new JsonObject
    {
        ["display_information"] = new JsonObject
        {
            ["name"] = "Entire Team Workflow",
            ["description"] = "Posts the progress of Entire Team Workflow runs to a Slack channel, as the person who signed in.",
            ["background_color"] = "#1f2a44",
        },
        ["oauth_config"] = new JsonObject
        {
            ["redirect_urls"] = new JsonArray(RedirectPorts.Select(port => (JsonNode)$"http://localhost:{port}/").ToArray()),
            ["scopes"] = new JsonObject { ["user"] = new JsonArray(UserScope.Split(',').Select(scope => (JsonNode)scope).ToArray()) },
            ["pkce_enabled"] = true,
        },
        ["settings"] = new JsonObject
        {
            ["org_deploy_enabled"] = false,
            ["socket_mode_enabled"] = false,
            ["token_rotation_enabled"] = false,
        },
    }.ToJsonString();

    /// <summary>Opens Slack's "create app" page with everything above already filled in: the user only presses Create.</summary>
    public static string CreateAppUrl() => "https://api.slack.com/apps?new_app=1&manifest_json=" + Uri.EscapeDataString(BuildManifestJson());

    /// <summary>
    /// Slack compares the redirect URL's host and port exactly with the ones registered in the app, so sign-in uses these
    /// fixed loopback ports (all listed in scripts\slack-app-manifest.json) and takes the first free one.
    /// </summary>
    public static readonly IReadOnlyList<int> RedirectPorts = [53682, 53683, 53684, 53685];

    public static string BuildAuthorizeUrl(string clientId, string redirectUri, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["user_scope"] = UserScope,
            ["redirect_uri"] = redirectUri,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
        };
        return "https://slack.com/oauth/v2/authorize?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public static async Task<SlackTokens> ExchangeCodeAsync(HttpClient http, string clientId, string redirectUri, string code, string verifier, CancellationToken ct)
    {
        var json = await PostAsync(http, new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
        }, ct).ConfigureAwait(false);

        var user = json["authed_user"] as JsonObject;
        var token = (string?)user?["access_token"] ?? throw new InvalidOperationException("Slack did not return a user token. Check that the Slack app asks for user scopes only.");
        return new SlackTokens(token, (string?)user?["refresh_token"], ExpiryFrom(user?["expires_in"]));
    }

    public static async Task<SlackTokens> RefreshAsync(HttpClient http, string clientId, string refreshToken, CancellationToken ct)
    {
        var json = await PostAsync(http, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        }, ct).ConfigureAwait(false);

        var token = (string?)json["access_token"] ?? throw new InvalidOperationException("Slack did not return a new token.");
        return new SlackTokens(token, (string?)json["refresh_token"] ?? refreshToken, ExpiryFrom(json["expires_in"]));
    }

    private static DateTimeOffset? ExpiryFrom(JsonNode? expiresIn) =>
        expiresIn is not null && int.TryParse(expiresIn.ToString(), out var seconds) && seconds > 0 ? DateTimeOffset.UtcNow.AddSeconds(seconds) : null;

    private static async Task<JsonObject> PostAsync(HttpClient http, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync("https://slack.com/api/oauth.v2.access", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var json = JsonNode.Parse(text) as JsonObject ?? throw new InvalidOperationException($"Slack returned an unreadable answer (HTTP {(int)response.StatusCode}).");
        if (json["ok"]?.GetValue<bool>() != true)
        {
            throw new InvalidOperationException("Slack sign-in failed: " + ((string?)json["error"] ?? "unknown error") + ".");
        }
        return json;
    }
}

/// <summary>Interactive Slack sign-in: opens the browser, takes the redirect on a registered loopback port and exchanges the code.</summary>
public static class SlackInteractiveSignIn
{
    public static async Task<SlackTokens> SignInAsync(HttpClient http, string clientId, Action<string> openBrowser, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var (listener, redirectUri) = LoopbackRedirect.Listen(SlackOAuth.RedirectPorts);
        using (listener)
        {
            var (verifier, challenge) = EntraOAuth.NewPkce();
            var state = EntraOAuth.NewState();
            openBrowser(SlackOAuth.BuildAuthorizeUrl(clientId, redirectUri, state, challenge));

            var code = await LoopbackRedirect.WaitForCodeAsync(listener, state, ct).ConfigureAwait(false);
            return await SlackOAuth.ExchangeCodeAsync(http, clientId, redirectUri, code, verifier, ct).ConfigureAwait(false);
        }
    }
}
