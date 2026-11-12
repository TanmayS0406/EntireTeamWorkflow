using System.Net.Http.Json;
using System.Threading.Channels;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Orchestrator.Runs;

/// <summary>
/// Sends a run's events and status changes to the backend in order, in small batches, from a background pump so that
/// agents never wait on the network. Disposing flushes everything that is queued.
/// </summary>
public sealed class RunEventSink : IAsyncDisposable
{
    private readonly Guid _runId;
    private readonly IHttpClientFactory _http;
    private readonly ILogger _log;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    public RunEventSink(Guid runId, IHttpClientFactory http, ILogger log)
    {
        _runId = runId;
        _http = http;
        _log = log;
        _pump = Task.Run(PumpAsync);
    }

    public void Emit(RunEventKind kind, string message, string? nodeId = null, string? agent = null)
        => _queue.Writer.TryWrite(new RunEventDto { RunId = _runId, TimeUtc = DateTime.UtcNow, Kind = kind, Message = message, NodeId = nodeId, Agent = agent });

    public void Status(RunStatus status, string? summary = null, List<ClarifyingQuestion>? questions = null)
        => _queue.Writer.TryWrite(new RunStatusUpdate { Status = status, Summary = summary, PendingQuestions = questions });

    private async Task PumpAsync()
    {
        var reader = _queue.Reader;
        var events = new List<RunEventDto>();
        while (await reader.WaitToReadAsync())
        {
            var count = 0;
            while (count++ < 200 && reader.TryRead(out var item))
            {
                if (item is RunEventDto e)
                {
                    events.Add(e);
                    continue;
                }
                await FlushAsync(events); // keep events before the status change
                await PostAsync($"/internal/runs/{_runId}/status", item);
            }
            await FlushAsync(events);
        }
    }

    private async Task FlushAsync(List<RunEventDto> events)
    {
        if (events.Count == 0) return;
        await PostAsync($"/internal/runs/{_runId}/events", events.ToList());
        events.Clear();
    }

    private async Task PostAsync(string path, object body)
    {
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var response = await _http.CreateClient("backend").PostAsJsonAsync(path, body, EtwJson.Options);
                if (response.IsSuccessStatusCode) return;
                _log.LogWarning("Backend rejected {Path}: {Status}", path, (int)response.StatusCode);
                if ((int)response.StatusCode is >= 400 and < 500) return; // retrying will not help
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning("Backend not reachable for {Path} (attempt {Attempt}): {Message}", path, attempt, ex.Message);
            }
            await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt));
        }
        _log.LogError("Dropped a message for {Path} after repeated failures", path);
    }

    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete();
        await _pump;
    }
}
