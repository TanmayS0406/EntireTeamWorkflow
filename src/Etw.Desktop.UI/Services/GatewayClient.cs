using System.Net.Http.Json;
using System.Text.Json;
using Etw.Shared;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Etw.Desktop.UI.Services;

/// <summary>An error from the backend with a message that is safe and useful to show to the user.</summary>
public sealed class GatewayException(string message, int statusCode = 0) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>The desktop app's only connection to the system: the REST API and the SignalR hub of the backend service.</summary>
public sealed class GatewayClient : IAsyncDisposable
{
    private readonly HttpClient _http = new() { BaseAddress = new Uri(EtwConstants.BackendUrl), Timeout = TimeSpan.FromMinutes(3) };
    private string? _key;
    private HubConnection? _hub;

    public event Action<RunEventDto>? RunEventReceived;
    public event Action<RunDto>? RunStatusReceived;
    public event Action<bool>? ConnectionChanged;

    public HttpClient Http => _http;

    // ---- Plumbing -----------------------------------------------------------------------------------------------

    /// <summary>Reads the API key the services created. Throws a readable error when the backend has never started.</summary>
    private string Key()
    {
        if (_key is not null) return _key;
        try
        {
            _key = File.ReadAllText(EtwPaths.ApiKeyFile).Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new GatewayException("The Entire Team Workflow backend is not running yet. Start the EtwBackend service (or scripts\\Start-Dev.ps1).");
        }
        catch (UnauthorizedAccessException)
        {
            throw new GatewayException($"Access to {EtwPaths.ApiKeyFile} was denied. Re-run scripts\\Install-Services.ps1 so it grants your account read access.");
        }
        return _key;
    }

    private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(EtwConstants.ApiKeyHeader, Key());
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: EtwJson.Options);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException)
        {
            throw new GatewayException("Cannot reach the backend service on " + EtwConstants.BackendUrl + ". Is the EtwBackend service running?");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GatewayException("The backend took too long to answer.");
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            _key = null; // the key file may have been regenerated
            response.Dispose();
            throw new GatewayException("The backend rejected the API key. Restart the app; if it persists, reinstall the services.", 401);
        }
        if (!response.IsSuccessStatusCode)
        {
            var message = await ReadErrorAsync(response, ct);
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new GatewayException(message, status);
        }
        return response;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? text;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(text) ? $"The backend answered HTTP {(int)response.StatusCode}." : text;
    }

    public async Task<T> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await RequestAsync(HttpMethod.Get, path, null, ct);
        return (await response.Content.ReadFromJsonAsync<T>(EtwJson.Options, ct))!;
    }

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await RequestAsync(method, path, body, ct);
        return (await response.Content.ReadFromJsonAsync<T>(EtwJson.Options, ct))!;
    }

    public async Task SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var response = await RequestAsync(method, path, body, ct);
    }

    /// <summary>
    /// The app is often opened at the same moment as the services, and the backend needs a few seconds (database, hub).
    /// Waits until it answers instead of failing on the first try.
    /// </summary>
    public async Task WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await _http.GetAsync("/health", ct);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException)
            {
                // Not listening yet.
            }

            if (DateTime.UtcNow >= deadline) throw new GatewayException("Cannot reach the backend service on " + EtwConstants.BackendUrl + ". Is the EtwBackend service running?");
            await Task.Delay(500, ct);
        }
    }

    // ---- Live events --------------------------------------------------------------------------------------------

    public async Task StartHubAsync()
    {
        if (_hub is not null) return;
        _hub = new HubConnectionBuilder()
            .WithUrl(EtwConstants.BackendUrl + "/hubs/runs", options => options.Headers.Add(EtwConstants.ApiKeyHeader, Key()))
            .AddJsonProtocol(o => o.PayloadSerializerOptions = EtwJson.Create())
            .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)])
            .Build();

        _hub.On<RunEventDto>("runEvent", e => RunEventReceived?.Invoke(e));
        _hub.On<RunDto>("runStatus", r => RunStatusReceived?.Invoke(r));
        _hub.Reconnected += _ => { ConnectionChanged?.Invoke(true); return Task.CompletedTask; };
        _hub.Closed += _ => { ConnectionChanged?.Invoke(false); return Task.CompletedTask; };

        try
        {
            await _hub.StartAsync();
            ConnectionChanged?.Invoke(true);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            await _hub.DisposeAsync();
            _hub = null;
            throw new GatewayException("Could not open the live connection to the backend.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub is not null) await _hub.DisposeAsync();
        _http.Dispose();
    }
}


