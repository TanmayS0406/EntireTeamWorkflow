using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Etw.Shared.Auth;

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresOn, string Scope)
{
    public bool IsExpired(TimeSpan? skew = null) => DateTimeOffset.UtcNow >= ExpiresOn - (skew ?? TimeSpan.FromMinutes(2));
}

/// <summary>
/// Microsoft Entra ID OAuth 2.0 authorization-code flow with PKCE for a public (no secret) client with a
/// loopback redirect. Used for Azure DevOps, Azure Resource Manager and Microsoft Teams (Microsoft Graph).
/// </summary>
public static class EntraOAuth
{
    /// <summary>Azure DevOps resource id (fixed by Microsoft). Delegated permission: user_impersonation.</summary>
    public const string AzureDevOpsScope = "499b84ac-1321-427f-aa17-267ca6975798/.default offline_access";

    public const string AzureArmScope = "https://management.azure.com/.default offline_access";

    /// <summary>Post to a Teams channel as the signed-in user, and list the teams and channels to choose from. None of these need admin consent.</summary>
    public const string TeamsScope =
        "https://graph.microsoft.com/ChannelMessage.Send https://graph.microsoft.com/Team.ReadBasic.All https://graph.microsoft.com/Channel.ReadBasic.All offline_access";

    /// <summary>
    /// Microsoft's public "Azure developer sign-on" client, the default of Azure.Identity's interactive browser sign-in.
    /// It is pre-authorized for Azure Resource Manager and Azure DevOps and accepts a http://localhost redirect, so the
    /// user needs no app registration. A custom client id can still be set under Advanced.
    /// </summary>
    public const string DefaultClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>
    /// Microsoft's public "Microsoft Graph Command Line Tools" client: a public client with a http://localhost redirect
    /// whose delegated permissions the user consents to when signing in. Used for Teams.
    /// </summary>
    public const string GraphClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public static string ClientOrDefault(string? clientId, string fallback = DefaultClientId) =>
        string.IsNullOrWhiteSpace(clientId) ? fallback : clientId.Trim();

    public static string Authority(string tenant) => $"https://login.microsoftonline.com/{(string.IsNullOrWhiteSpace(tenant) ? "organizations" : tenant.Trim())}";

    public static (string Verifier, string Challenge) NewPkce()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(16));

    public static string BuildAuthorizeUrl(string tenant, string clientId, string redirectUri, string scope, string state, string challenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = scope,
            ["state"] = state,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["prompt"] = "select_account",
        };
        return Authority(tenant) + "/oauth2/v2.0/authorize?" + string.Join('&', query.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
    }

    public static Task<OAuthTokens> ExchangeCodeAsync(HttpClient http, string tenant, string clientId, string redirectUri,
        string code, string verifier, string scope, CancellationToken ct) =>
        PostTokenAsync(http, tenant, scope, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = scope,
        }, ct);

    public static Task<OAuthTokens> RefreshAsync(HttpClient http, string tenant, string clientId, string refreshToken,
        string scope, CancellationToken ct) =>
        PostTokenAsync(http, tenant, scope, new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = scope,
        }, ct);

    private static async Task<OAuthTokens> PostTokenAsync(HttpClient http, string tenant, string scope,
        Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(Authority(tenant) + "/oauth2/v2.0/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var body = await response.Content.ReadFromJsonAsync<TokenResponse>(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || body?.AccessToken is null)
        {
            throw new InvalidOperationException(body?.ErrorDescription ?? body?.Error ?? $"Token endpoint returned {(int)response.StatusCode}.");
        }
        return new OAuthTokens(body.AccessToken, body.RefreshToken, DateTimeOffset.UtcNow.AddSeconds(body.ExpiresIn), body.Scope ?? scope);
    }

    /// <summary>Reads the signed-in user's name from an access/id token without validating it (display only).</summary>
    public static string? AccountFromToken(string jwt)
    {
        try
        {
            var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "upn", "preferred_username", "unique_name", "email", "name" })
            {
                if (doc.RootElement.TryGetProperty(claim, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch
        {
            // Not a JWT we can read: no display name.
        }
        return null;
    }

    internal static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; set; }
        [JsonPropertyName("scope")] public string? Scope { get; set; }
        [JsonPropertyName("error")] public string? Error { get; set; }
        [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    }
}

/// <summary>
/// Receives the redirect of a browser sign-in on a loopback port. Runs in the desktop app (the user's session),
/// because a Windows service cannot show a browser.
/// </summary>
public static class LoopbackRedirect
{
    /// <summary>
    /// Starts listening on the first of <paramref name="ports"/> that is free (any free port when none are given).
    /// Some providers only accept redirect URLs that were registered with their exact port.
    /// </summary>
    public static (HttpListener Listener, string RedirectUri) Listen(IReadOnlyList<int>? ports = null)
    {
        foreach (var port in ports is { Count: > 0 } ? ports : [FreePort()])
        {
            var redirectUri = $"http://localhost:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(redirectUri);
            try
            {
                listener.Start();
                return (listener, redirectUri);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("Could not start the local sign-in listener: its port is in use. Close the program using it and try again.");
    }

    /// <summary>Waits for the browser to come back, answers it with a small page and returns the authorization code.</summary>
    public static async Task<string> WaitForCodeAsync(HttpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var _ = timeout.Token.Register(() => { try { listener.Stop(); } catch { /* disposed */ } });

        HttpListenerContext context;
        try
        {
            context = await listener.GetContextAsync().ConfigureAwait(false);
        }
        catch (Exception) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Sign-in was not completed in the browser within 5 minutes.");
        }

        var query = context.Request.QueryString;
        var error = query["error_description"] ?? query["error"];
        var ok = error is null && query["state"] == state && !string.IsNullOrEmpty(query["code"]);
        await RespondAsync(context, ok, error).ConfigureAwait(false);

        if (!ok) throw new InvalidOperationException(error ?? "Sign-in was rejected (state mismatch).");
        return query["code"]!;
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private static async Task RespondAsync(HttpListenerContext context, bool ok, string? error)
    {
        var html = ok
            ? "<html><body style='font-family:Segoe UI,sans-serif;text-align:center;margin-top:20vh'><h2>Signed in</h2><p>You can close this tab and return to Entire Team Workflow.</p></body></html>"
            : $"<html><body style='font-family:Segoe UI,sans-serif;text-align:center;margin-top:20vh'><h2>Sign-in failed</h2><p>{WebUtility.HtmlEncode(error ?? "Unknown error")}</p></body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        context.Response.Close();
    }
}

/// <summary>Interactive Microsoft sign-in: opens the system browser and exchanges the code that comes back.</summary>
public static class EntraInteractiveSignIn
{
    public static async Task<OAuthTokens> SignInAsync(HttpClient http, string tenant, string clientId, string scope,
        Action<string> openBrowser, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);

        var (listener, redirectUri) = LoopbackRedirect.Listen();
        using (listener)
        {
            var (verifier, challenge) = EntraOAuth.NewPkce();
            var state = EntraOAuth.NewState();
            openBrowser(EntraOAuth.BuildAuthorizeUrl(tenant, clientId, redirectUri, scope, state, challenge));

            var code = await LoopbackRedirect.WaitForCodeAsync(listener, state, ct).ConfigureAwait(false);
            return await EntraOAuth.ExchangeCodeAsync(http, tenant, clientId, redirectUri, code, verifier, scope, ct).ConfigureAwait(false);
        }
    }
}
