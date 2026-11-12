using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Etw.Shared.Auth;
using Etw.Shared.Models;

namespace Etw.Integrations.Services;

/// <summary>
/// Posts run notifications to Slack and Microsoft Teams as the user who signed in with OAuth. Nobody pastes a URL:
/// after signing in the user picks a channel from the ones their account can see.
/// </summary>
public sealed partial class NotificationService(IntegrationVault vault, IHttpClientFactory httpFactory, ILogger<NotificationService> log)
{
    private const string Graph = "https://graph.microsoft.com/v1.0";
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _slackRefresh = new(1, 1);
    private readonly SemaphoreSlim _teamsRefresh = new(1, 1);

    private HttpClient Http => httpFactory.CreateClient("notify");

    // ---- Status -------------------------------------------------------------------------------------------------

    public NotificationStatusDto Status()
    {
        var s = vault.Read();
        return new NotificationStatusDto
        {
            Slack = SlackStatus(s),
            Teams = new TeamsStatusDto
            {
                Connection = new ConnectionStatusDto
                {
                    Connected = TeamsConnected(s),
                    Account = s.TeamsAccount,
                    TenantId = s.TeamsTenantId,
                    ClientId = s.TeamsClientId,
                },
                TeamId = s.TeamsTeamId,
                TeamName = s.TeamsTeamName,
                ChannelId = s.TeamsChannelId,
                ChannelName = s.TeamsChannelName,
            },
        };
    }

    private static bool TeamsConnected(IntegrationSecrets s) =>
        !string.IsNullOrEmpty(s.TeamsRefreshToken) || (!string.IsNullOrEmpty(s.TeamsAccessToken) && s.TeamsExpiresOn > DateTimeOffset.UtcNow);

    public void Disconnect(NotifyChannel channel)
    {
        if (channel == NotifyChannel.Slack)
        {
            DisconnectSlack(null);
            return;
        }

        vault.Update(s =>
        {
            s.TeamsAccessToken = null;
            s.TeamsRefreshToken = null;
            s.TeamsExpiresOn = default;
            s.TeamsAccount = null;
            s.TeamsTeamId = "";
            s.TeamsTeamName = "";
            s.TeamsChannelId = "";
            s.TeamsChannelName = "";
        });
    }

    // ---- Slack: sign-in, workspaces, channels -------------------------------------------------------------------

    private static SlackConnection? ActiveSlack(IntegrationSecrets s) =>
        s.SlackConnections.FirstOrDefault(c => c.TeamId == s.SlackActiveTeamId) ?? s.SlackConnections.FirstOrDefault();

    private static SlackStatusDto SlackStatus(IntegrationSecrets s)
    {
        var active = ActiveSlack(s);
        return new SlackStatusDto
        {
            AppClientId = s.SlackClientId,
            Connected = active is not null,
            Account = active?.Account,
            Workspace = active?.Workspace,
            ChannelId = active?.ChannelId ?? "",
            ChannelName = active?.ChannelName ?? "",
            ActiveWorkspaceId = active?.TeamId ?? "",
            Workspaces = s.SlackConnections.Select(c => new SlackWorkspaceDto { Id = c.TeamId, Name = c.Workspace, Account = c.Account }).OrderBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    /// <summary>Signs out of one workspace (the active one by default). The next workspace, if any, becomes the active one.</summary>
    public void DisconnectSlack(string? teamId) => vault.Update(s =>
    {
        var id = string.IsNullOrEmpty(teamId) ? ActiveSlack(s)?.TeamId : teamId;
        s.SlackConnections.RemoveAll(c => c.TeamId == id);
        s.SlackActiveTeamId = s.SlackConnections.FirstOrDefault()?.TeamId ?? "";
    });

    /// <summary>Remembers the Client ID of the Slack app after the one-time setup. A different app invalidates every existing sign-in.</summary>
    public void SaveSlackApp(SlackAppRequest request)
    {
        var clientId = request.ClientId.Trim();
        if (!SlackOAuth.IsValidClientId(clientId)) throw new ArgumentException("That does not look like a Slack Client ID. It is two numbers joined by a dot, like 1234567890.1234567890123.");

        vault.Update(s =>
        {
            if (s.SlackClientId != clientId)
            {
                s.SlackConnections.Clear();
                s.SlackActiveTeamId = "";
            }
            s.SlackClientId = clientId;
        });
    }

    /// <summary>Adds (or refreshes) the sign-in to the workspace the user just chose in the browser, and makes it the active one.</summary>
    public async Task ConnectSlackAsync(SlackConnectRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.AccessToken)) throw new ArgumentException("No Slack token was provided.");

        // auth.test says who signed in and to which workspace.
        var who = await SlackFormAsync(request.AccessToken, "auth.test", new Dictionary<string, string>(), null, ct);
        var teamId = (string?)who["team_id"] ?? throw new InvalidOperationException("Slack did not say which workspace this is.");
        vault.Update(s =>
        {
            var connection = s.SlackConnections.FirstOrDefault(c => c.TeamId == teamId);
            if (connection is null)
            {
                connection = new SlackConnection { TeamId = teamId };
                s.SlackConnections.Add(connection);
            }
            connection.Workspace = (string?)who["team"] ?? teamId;
            connection.Account = (string?)who["user"] ?? "";
            connection.AccessToken = request.AccessToken;
            connection.RefreshToken = request.RefreshToken;
            connection.ExpiresOn = request.ExpiresOn;
            s.SlackClientId = request.ClientId.Trim();
            s.SlackActiveTeamId = teamId;
        });
    }

    public void SelectSlackWorkspace(SlackWorkspaceRequest request) => vault.Update(s =>
    {
        if (!s.SlackConnections.Any(c => c.TeamId == request.TeamId)) throw new ArgumentException("You are not signed in to that Slack workspace.");
        s.SlackActiveTeamId = request.TeamId;
    });

    public async Task<List<NotifyChoiceDto>> SlackChannelsAsync(CancellationToken ct)
    {
        var teamId = ActiveSlack(vault.Read())?.TeamId ?? throw new InvalidOperationException("Not connected to Slack. Sign in on the Connect tab.");
        var token = await SlackTokenAsync(teamId, ct);
        var channels = new List<NotifyChoiceDto>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var form = new Dictionary<string, string>
            {
                ["types"] = "public_channel,private_channel",
                ["exclude_archived"] = "true",
                ["limit"] = "200",
            };
            if (!string.IsNullOrEmpty(cursor)) form["cursor"] = cursor;

            var json = await SlackFormAsync(token, "users.conversations", form, teamId, ct);
            foreach (var channel in json["channels"] as JsonArray ?? [])
            {
                var id = (string?)channel?["id"];
                var name = (string?)channel?["name"];
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) continue;
                channels.Add(new NotifyChoiceDto { Id = id, Name = channel?["is_private"]?.GetValue<bool>() == true ? $"#{name} (private)" : $"#{name}" });
            }

            cursor = (string?)json["response_metadata"]?["next_cursor"];
            if (string.IsNullOrEmpty(cursor)) break;
        }
        return channels.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void SelectSlackChannel(SelectChannelRequest selection)
    {
        if (selection.ChannelId.Length > 0 && !SlackId().IsMatch(selection.ChannelId)) throw new ArgumentException("Invalid Slack channel.");
        vault.Update(s =>
        {
            var active = ActiveSlack(s) ?? throw new InvalidOperationException("Not connected to Slack. Sign in on the Connect tab.");
            active.ChannelId = selection.ChannelId;
            active.ChannelName = selection.ChannelName;
        });
    }

    private async Task<string> SlackTokenAsync(string teamId, CancellationToken ct)
    {
        const string notConnected = "Not connected to Slack. Sign in on the Connect tab.";
        var connection = vault.Read().SlackConnections.FirstOrDefault(c => c.TeamId == teamId) ?? throw new InvalidOperationException(notConnected);
        if (!SlackExpired(connection)) return connection.AccessToken;

        await _slackRefresh.WaitAsync(ct);
        try
        {
            var s = vault.Read();
            connection = s.SlackConnections.FirstOrDefault(c => c.TeamId == teamId) ?? throw new InvalidOperationException(notConnected);
            if (!SlackExpired(connection)) return connection.AccessToken;

            try
            {
                if (string.IsNullOrEmpty(connection.RefreshToken) || string.IsNullOrEmpty(s.SlackClientId)) throw new InvalidOperationException("No refresh token.");
                var tokens = await SlackOAuth.RefreshAsync(Http, s.SlackClientId, connection.RefreshToken, ct);
                vault.Update(v =>
                {
                    var c = v.SlackConnections.FirstOrDefault(x => x.TeamId == teamId);
                    if (c is null) return;
                    c.AccessToken = tokens.AccessToken;
                    c.RefreshToken = tokens.RefreshToken;
                    c.ExpiresOn = tokens.ExpiresOn;
                });
                return tokens.AccessToken;
            }
            catch (InvalidOperationException ex)
            {
                log.LogWarning("Slack token refresh failed for {Workspace}: {Message}", connection.Workspace, ex.Message);
                DisconnectSlack(teamId);
                throw new InvalidOperationException(SlackExpiredMessage);
            }
        }
        finally
        {
            _slackRefresh.Release();
        }
    }

    private static bool SlackExpired(SlackConnection c) => c.ExpiresOn is { } expires && expires - Skew <= DateTimeOffset.UtcNow;

    private const string SlackExpiredMessage = "The Slack sign-in expired. Sign in to Slack again.";

    private Task<JsonObject> SlackFormAsync(string token, string method, Dictionary<string, string> form, string? teamId, CancellationToken ct) =>
        SlackSendAsync(token, method, new FormUrlEncodedContent(form), teamId, ct);

    private Task<JsonObject> SlackJsonAsync(string token, string method, JsonObject body, string? teamId, CancellationToken ct) =>
        SlackSendAsync(token, method, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), teamId, ct);

    /// <summary>Calls the Slack Web API. When the token is rejected the workspace's sign-in (<paramref name="teamId"/>) is removed.</summary>
    private async Task<JsonObject> SlackSendAsync(string token, string method, HttpContent content, string? teamId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://slack.com/api/" + method) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        var json = TryParse(text) ?? throw new InvalidOperationException($"Slack returned an unreadable answer (HTTP {(int)response.StatusCode}).");
        if (json["ok"]?.GetValue<bool>() == true) return json;

        var error = (string?)json["error"] ?? "unknown_error";
        if (error is "invalid_auth" or "token_revoked" or "token_expired" or "account_inactive" or "not_authed")
        {
            if (teamId is not null) DisconnectSlack(teamId);
            throw new InvalidOperationException(SlackExpiredMessage);
        }
        throw new InvalidOperationException(error switch
        {
            "channel_not_found" => "Slack could not find that channel. Choose the channel again.",
            "not_in_channel" => "You are not a member of that Slack channel. Join it in Slack or choose another channel.",
            "is_archived" => "That Slack channel is archived. Choose another channel.",
            "missing_scope" => "Slack did not grant a needed permission. Sign in to Slack again.",
            "restricted_action" => "Your Slack workspace does not allow you to post there.",
            _ => "Slack said: " + error + ".",
        });
    }

    // ---- Teams: sign-in, teams and channels ---------------------------------------------------------------------

    public void SaveTeamsConfig(OAuthConfigDto config) => vault.Update(s =>
    {
        s.TeamsTenantId = string.IsNullOrWhiteSpace(config.TenantId) ? "organizations" : config.TenantId.Trim();
        s.TeamsClientId = EntraOAuth.ClientOrDefault(config.ClientId, EntraOAuth.GraphClientId);
    });

    public void ConnectTeams(OAuthConnectRequest request) => vault.Update(s =>
    {
        s.TeamsTenantId = string.IsNullOrWhiteSpace(request.TenantId) ? s.TeamsTenantId : request.TenantId.Trim();
        s.TeamsClientId = EntraOAuth.ClientOrDefault(string.IsNullOrWhiteSpace(request.ClientId) ? s.TeamsClientId : request.ClientId, EntraOAuth.GraphClientId);
        s.TeamsAccessToken = request.AccessToken;
        s.TeamsRefreshToken = request.RefreshToken;
        s.TeamsExpiresOn = request.ExpiresOn;
        s.TeamsAccount = request.Account ?? EntraOAuth.AccountFromToken(request.AccessToken);
    });

    public async Task<List<NotifyChoiceDto>> TeamsAsync(CancellationToken ct)
    {
        var json = await GraphAsync(HttpMethod.Get, Graph + "/me/joinedTeams", null, ct);
        return Choices(json);
    }

    public async Task<List<NotifyChoiceDto>> TeamsChannelsAsync(string teamId, CancellationToken ct)
    {
        var json = await GraphAsync(HttpMethod.Get, $"{Graph}/teams/{Guid.Parse(teamId)}/channels", null, ct);
        return Choices(json);
    }

    public void SelectTeamsChannel(SelectChannelRequest selection)
    {
        if (selection.TeamId.Length > 0 && !Guid.TryParse(selection.TeamId, out _)) throw new ArgumentException("Invalid team.");
        if (selection.ChannelId.Length > 0 && !TeamsChannelId().IsMatch(selection.ChannelId)) throw new ArgumentException("Invalid Teams channel.");
        vault.Update(s =>
        {
            s.TeamsTeamId = selection.TeamId;
            s.TeamsTeamName = selection.TeamName;
            s.TeamsChannelId = selection.ChannelId;
            s.TeamsChannelName = selection.ChannelName;
        });
    }

    private static List<NotifyChoiceDto> Choices(JsonNode? json) =>
        (json?["value"] as JsonArray ?? [])
        .Select(x => new NotifyChoiceDto { Id = (string?)x?["id"] ?? "", Name = (string?)x?["displayName"] ?? "" })
        .Where(x => x.Id.Length > 0 && x.Name.Length > 0)
        .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    private async Task<string> TeamsTokenAsync(CancellationToken ct)
    {
        var s = vault.Read();
        if (!string.IsNullOrEmpty(s.TeamsAccessToken) && s.TeamsExpiresOn - Skew > DateTimeOffset.UtcNow) return s.TeamsAccessToken;

        await _teamsRefresh.WaitAsync(ct);
        try
        {
            s = vault.Read();
            if (!string.IsNullOrEmpty(s.TeamsAccessToken) && s.TeamsExpiresOn - Skew > DateTimeOffset.UtcNow) return s.TeamsAccessToken;
            if (string.IsNullOrEmpty(s.TeamsRefreshToken)) throw new InvalidOperationException("Not connected to Microsoft Teams. Sign in on the Connect tab.");

            var tokens = await EntraOAuth.RefreshAsync(Http, s.TeamsTenantId, EntraOAuth.ClientOrDefault(s.TeamsClientId, EntraOAuth.GraphClientId), s.TeamsRefreshToken, EntraOAuth.TeamsScope, ct);
            vault.Update(v =>
            {
                v.TeamsAccessToken = tokens.AccessToken;
                v.TeamsRefreshToken = tokens.RefreshToken ?? v.TeamsRefreshToken;
                v.TeamsExpiresOn = tokens.ExpiresOn;
            });
            return tokens.AccessToken;
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("AADSTS", StringComparison.OrdinalIgnoreCase))
        {
            log.LogWarning("Teams token refresh failed: {Message}", ex.Message);
            Disconnect(NotifyChannel.Teams);
            throw new InvalidOperationException("The Microsoft Teams sign-in expired. Sign in again on the Connect tab.");
        }
        finally
        {
            _teamsRefresh.Release();
        }
    }

    private async Task<JsonNode?> GraphAsync(HttpMethod method, string url, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TeamsTokenAsync(ct));
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            var message = TryParse(text)?["error"]?["message"]?.GetValue<string>();
            throw new InvalidOperationException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? "Microsoft Teams refused the request" + (message is null ? "." : ": " + message) + " Sign in again, and check that your account can use Teams and post in that channel."
                : message ?? $"Microsoft Teams returned HTTP {(int)response.StatusCode}.");
        }
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    // ---- Sending ------------------------------------------------------------------------------------------------

    public async Task<NotifyResultDto> SendAsync(NotifyRequest request, CancellationToken ct)
    {
        var result = new NotifyResultDto();
        var tasks = request.Channels.Distinct().Select(async channel =>
        {
            try
            {
                if (channel == NotifyChannel.Slack) await PostSlackAsync(request, ct);
                else await PostTeamsAsync(request, ct);
                lock (result) result.Delivered.Add(channel.ToString());
            }
            catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
            {
                log.LogWarning("{Channel} notification failed: {Message}", channel, ex.Message);
                lock (result) result.Failed.Add($"{channel}: {ex.Message}");
            }
        });
        await Task.WhenAll(tasks);
        return result;
    }

    private async Task PostSlackAsync(NotifyRequest request, CancellationToken ct)
    {
        var active = ActiveSlack(vault.Read()) ?? throw new InvalidOperationException("not connected. Sign in to Slack on the Connect tab.");
        if (string.IsNullOrEmpty(active.ChannelId)) throw new InvalidOperationException("no Slack channel chosen yet. Pick a channel on the Connect tab.");

        var payload = SlackPayload(request);
        payload["channel"] = active.ChannelId;
        await SlackJsonAsync(await SlackTokenAsync(active.TeamId, ct), "chat.postMessage", payload, active.TeamId, ct);
    }

    private async Task PostTeamsAsync(NotifyRequest request, CancellationToken ct)
    {
        var s = vault.Read();
        if (string.IsNullOrEmpty(s.TeamsTeamId) || string.IsNullOrEmpty(s.TeamsChannelId))
        {
            throw new InvalidOperationException("no Teams channel chosen yet. Sign in and pick a channel on the Connect tab.");
        }

        var url = $"{Graph}/teams/{Guid.Parse(s.TeamsTeamId)}/channels/{Uri.EscapeDataString(s.TeamsChannelId)}/messages";
        await GraphAsync(HttpMethod.Post, url, TeamsPayload(request), ct);
    }

    // ---- Payloads -----------------------------------------------------------------------------------------------

    private static JsonObject SlackPayload(NotifyRequest r)
    {
        var icon = r.Level switch { NotifyLevel.Success => ":white_check_mark:", NotifyLevel.Warning => ":warning:", NotifyLevel.Error => ":x:", _ => ":information_source:" };
        var text = Truncate(r.Message, 2800);
        return new JsonObject
        {
            ["text"] = $"{r.Title}: {Truncate(r.Message, 200)}",
            ["blocks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "section",
                    ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = $"{icon} *{Escape(r.Title)}*\n{Escape(text)}" },
                },
                new JsonObject
                {
                    ["type"] = "context",
                    ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = "Entire Team Workflow" + (r.RunId is { } id ? $" · run `{id.ToString()[..8]}`" : "") } },
                },
            },
        };
    }

    private static JsonObject TeamsPayload(NotifyRequest r)
    {
        var icon = r.Level switch { NotifyLevel.Success => "✅", NotifyLevel.Warning => "⚠️", NotifyLevel.Error => "❌", _ => "ℹ️" };
        var message = WebUtility.HtmlEncode(Truncate(r.Message, 3000)).ReplaceLineEndings("<br/>");
        var footer = "Entire Team Workflow" + (r.RunId is { } id ? $" · run {id.ToString()[..8]}" : "");
        var html = $"<b>{icon} {WebUtility.HtmlEncode(r.Title)}</b><br/>{message}<br/><i>{footer}</i>";
        return new JsonObject { ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = html } };
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    private static JsonObject? TryParse(string text)
    {
        try { return JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"^[CGD][A-Z0-9]{6,20}$")]
    private static partial Regex SlackId();

    [GeneratedRegex(@"^19:[A-Za-z0-9_\-.=]+@thread\.[A-Za-z0-9]+$")]
    private static partial Regex TeamsChannelId();
}
