using Etw.Shared.Claude;

namespace Etw.Desktop.UI.Services;

/// <summary>
/// "Sign in with Claude", the same way the SSMS reference extension does it: the official Claude Code CLI performs the
/// OAuth sign-in in the browser and keeps the tokens. This app never sees or stores Claude credentials. It runs here,
/// in the desktop app, because a browser can only be shown in the user's interactive session — never from a Windows service.
/// </summary>
public sealed class ClaudeLoginService
{
    private static readonly TimeSpan LoginTimeout = TimeSpan.FromMinutes(10);

    public string? ConfiguredPath { get; set; }

    public string? CliPath => ClaudeCli.Locate(ConfiguredPath);

    public async Task<ClaudeAuthStatus> GetStatusAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await ClaudeCli.GetAuthStatusAsync(CliPath, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            return new ClaudeAuthStatus(CliPath is not null, false, null, "Timed out checking the sign-in.");
        }
    }

    /// <summary>Opens the CLI's browser sign-in and waits until it reports a signed-in account (or the window is closed).</summary>
    public async Task<ClaudeAuthStatus> SignInAsync(Action<string> progress, CancellationToken ct)
    {
        var cli = CliPath ?? throw new InvalidOperationException("Claude Code CLI not found. Install it from https://claude.com/claude-code, or set its path.");

        progress("Finish signing in in the browser and the console window that just opened…");
        using var console = ClaudeCli.StartLogin(cli);

        var started = DateTime.UtcNow;
        ClaudeAuthStatus status = new(true, false, null, "");
        while (DateTime.UtcNow - started < LoginTimeout)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            status = await GetStatusAsync();
            if (status.LoggedIn) return status;
            if (console is null || console.HasExited) break;
            progress("Waiting for the Claude sign-in to finish…");
        }
        return await GetStatusAsync();
    }

    public async Task SignOutAsync()
    {
        var cli = CliPath;
        if (cli is null) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ClaudeCli.LogoutAsync(cli, timeout.Token);
    }
}
