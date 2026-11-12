using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Etw.Shared;
using Etw.Shared.Claude;
using Microsoft.Extensions.AI;

namespace Etw.Orchestrator.Claude;

/// <summary>Where an agent's live output goes (the run's event log). Tool use is reported here instead of as chat content.</summary>
public interface IAgentReporter
{
    void Text(string text);
    void Activity(string description);
}

public sealed record ClaudeAgentOptions(
    string CliPath,
    string WorkingDirectory,
    string? Model,
    string? Effort,
    string PermissionMode,
    IReadOnlyList<string> AllowedTools,
    bool DisableTools,
    TimeSpan Timeout);

/// <summary>
/// An <see cref="IChatClient"/> whose "model" is the Claude Code CLI, signed in with the user's own Claude account.
/// Each call runs <c>claude -p</c> in the agent's working directory, so the agent can read, edit and build files there with
/// Claude Code's own tools. Microsoft Agent Framework wraps this in a <c>ChatClientAgent</c> and orchestrates it.
/// </summary>
public sealed class ClaudeCodeChatClient(ClaudeAgentOptions options, IAgentReporter reporter, ILogger log) : IChatClient
{
    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, CancellationToken cancellationToken = default)
        => await GetStreamingResponseAsync(messages, chatOptions, cancellationToken).ToChatResponseAsync(cancellationToken);

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? chatOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(messages);
        var systemPromptFile = Path.Combine(EtwPaths.Temp, $"agent-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(systemPromptFile, chatOptions?.Instructions ?? "", new UTF8Encoding(false), cancellationToken);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);

        using var process = new Process { StartInfo = ClaudeCli.CreateStartInfo(options.CliPath, BuildArguments(systemPromptFile), options.WorkingDirectory) };
        var parser = new StreamParser(options.WorkingDirectory, reporter);
        string stderr = "";
        try
        {
            process.Start();
            using var killOnCancel = timeout.Token.Register(() => ClaudeCli.KillTree(process));

            // The prompt goes through stdin: no command-line length or quoting limits.
            await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                await stdin.WriteAsync(prompt);
            }
            var stderrTask = process.StandardError.ReadToEndAsync();

            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                foreach (var update in parser.Handle(line)) yield return update;
            }

            await process.WaitForExitAsync(CancellationToken.None);
            stderr = (await stderrTask).Trim();
        }
        finally
        {
            ClaudeCli.KillTree(process);
            try { File.Delete(systemPromptFile); } catch (IOException) { /* temp file */ }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (timeout.IsCancellationRequested) throw new TimeoutException($"The agent did not finish within {options.Timeout.TotalMinutes:0} minutes.");
        if (parser.ResultError is not null) throw new InvalidOperationException(Describe(parser.ResultError));
        if (!parser.GotResult && process.ExitCode != 0)
        {
            throw new InvalidOperationException(Describe(stderr.Length > 0 ? stderr : $"Claude Code exited with code {process.ExitCode}."));
        }

        if (parser.Denials.Count > 0) reporter.Activity("Not permitted: " + string.Join("; ", parser.Denials));
        log.LogInformation("Agent finished: {Turns} turns, {Duration} ms, cost ${Cost:0.####}", parser.Turns, parser.DurationMs, parser.CostUsd);
    }

    private List<string> BuildArguments(string systemPromptFile)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--include-partial-messages",
            "--no-session-persistence",     // one-shot agents: do not litter the user's Claude session history
            "--strict-mcp-config",          // ignore MCP servers configured for the user's interactive sessions
            "--append-system-prompt-file", systemPromptFile,
            "--permission-mode", options.PermissionMode,
        };

        if (options.DisableTools)
        {
            args.Add("--tools");
            args.Add("");
        }
        else if (options.AllowedTools.Count > 0)
        {
            args.Add("--allowedTools");
            args.AddRange(options.AllowedTools);
        }

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            args.Add("--model");
            args.Add(options.Model.Trim());
        }
        if (!string.IsNullOrWhiteSpace(options.Effort))
        {
            args.Add("--effort");
            args.Add(options.Effort.Trim().ToLowerInvariant());
        }
        return args;
    }

    private static string BuildPrompt(IEnumerable<ChatMessage> messages)
    {
        var list = messages.Where(m => !string.IsNullOrWhiteSpace(m.Text)).ToList();
        if (list.Count == 1) return list[0].Text;
        return string.Join("\n\n", list.Select(m => $"[{m.Role.Value}]\n{m.Text}"));
    }

    private static string Describe(string error)
    {
        var lower = error.ToLowerInvariant();
        return lower.Contains("login") || lower.Contains("authenticat") || lower.Contains("oauth") || lower.Contains("api key")
            ? error + " — sign in to Claude again (the backend services must run as the Windows account that is signed in)."
            : error;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    /// <summary>Turns stream-json lines into text updates and reports tool use. Text arrives twice (deltas and the complete message); only one copy is used.</summary>
    private sealed class StreamParser(string workingDirectory, IAgentReporter reporter)
    {
        private bool _streamedThisMessage;
        private bool _wroteAnything;

        public bool GotResult { get; private set; }
        public string? ResultError { get; private set; }
        public int Turns { get; private set; }
        public long DurationMs { get; private set; }
        public double CostUsd { get; private set; }
        public List<string> Denials { get; } = [];

        public IEnumerable<ChatResponseUpdate> Handle(string line)
        {
            JsonNode? message;
            try { message = JsonNode.Parse(line); }
            catch (JsonException) { yield break; } // not a stream-json line

            switch (message?["type"]?.GetValue<string>())
            {
                case "stream_event":
                    var ev = message["event"];
                    if (ev?["type"]?.GetValue<string>() == "content_block_delta" && ev["delta"]?["type"]?.GetValue<string>() == "text_delta")
                    {
                        var text = ev["delta"]?["text"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(text))
                        {
                            _streamedThisMessage = true;
                            yield return Emit(text);
                        }
                    }
                    break;

                case "assistant":
                    foreach (var block in message["message"]?["content"] as JsonArray ?? [])
                    {
                        switch (block?["type"]?.GetValue<string>())
                        {
                            case "text" when !_streamedThisMessage:
                                var full = block["text"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(full)) yield return Emit(full);
                                break;
                            case "tool_use":
                                reporter.Activity(DescribeTool(block["name"]?.GetValue<string>(), block["input"] as JsonObject));
                                break;
                        }
                    }
                    if (_streamedThisMessage || _wroteAnything) _separate = true;
                    _streamedThisMessage = false;
                    break;

                case "result":
                    GotResult = true;
                    Turns = message["num_turns"]?.GetValue<int>() ?? 0;
                    DurationMs = message["duration_ms"]?.GetValue<long>() ?? 0;
                    CostUsd = message["total_cost_usd"]?.GetValue<double>() ?? 0;
                    foreach (var denial in message["permission_denials"] as JsonArray ?? [])
                    {
                        Denials.Add(DescribeTool(denial?["tool_name"]?.GetValue<string>(), denial?["tool_input"] as JsonObject));
                    }
                    if (message["is_error"]?.GetValue<bool>() == true)
                    {
                        ResultError = message["result"]?.GetValue<string>() ?? message["subtype"]?.GetValue<string>() ?? "Claude Code reported an error.";
                    }
                    break;
            }
        }

        private bool _separate;

        private ChatResponseUpdate Emit(string text)
        {
            if (_separate && _wroteAnything) text = "\n\n" + text;
            _separate = false;
            _wroteAnything = true;
            reporter.Text(text);
            return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        private string DescribeTool(string? name, JsonObject? input)
        {
            name ??= "tool";
            var detail = input?["file_path"]?.ToString() ?? input?["path"]?.ToString() ?? input?["pattern"]?.ToString()
                         ?? input?["command"]?.ToString() ?? input?["url"]?.ToString() ?? input?["description"]?.ToString();
            if (string.IsNullOrWhiteSpace(detail)) return name;

            if (detail.StartsWith(workingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                detail = detail[workingDirectory.Length..].TrimStart('\\', '/');
            }
            detail = detail.Trim().Replace("\r", " ").Replace("\n", " ");
            return $"{name}: {(detail.Length > 110 ? detail[..110] + "…" : detail)}";
        }
    }
}
