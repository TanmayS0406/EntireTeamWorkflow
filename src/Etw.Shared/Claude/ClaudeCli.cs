using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace Etw.Shared.Claude;

/// <summary>Result of <c>claude auth status</c>.</summary>
public sealed record ClaudeAuthStatus(bool CliFound, bool LoggedIn, string? Account, string Detail);

/// <summary>
/// Thin wrapper around the Claude Code CLI. This solution never handles Claude credentials itself:
/// sign-in (OAuth in the browser), token storage and refresh all happen inside the official CLI.
/// </summary>
public static class ClaudeCli
{
    /// <summary>Resolves the CLI from the setting, PATH, or the default install folders. Null when not found.</summary>
    public static string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var configured = configuredPath.Trim().Trim('"');
            return File.Exists(configured) ? configured : null;
        }

        var candidates = new List<string>();
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, "claude.exe"));
            candidates.Add(Path.Combine(dir, "claude.cmd"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe")); // native installer
        candidates.Add(Path.Combine(appData, "npm", "claude.cmd"));        // npm -g install

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry.
            }
        }
        return null;
    }

    /// <summary>Runs <c>claude auth status</c>.</summary>
    public static async Task<ClaudeAuthStatus> GetAuthStatusAsync(string? cliPath, CancellationToken cancellationToken)
    {
        if (cliPath is null) return new ClaudeAuthStatus(false, false, null, "Claude Code CLI not found.");

        string text;
        int exitCode;
        try
        {
            (exitCode, text) = await RunAsync(cliPath, ["auth", "status"], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ClaudeAuthStatus(true, false, null, ex.Message);
        }

        text = text.Trim();
        try
        {
            var json = JsonNode.Parse(text)!;
            var loggedIn = json["loggedIn"]?.GetValue<bool>() ?? exitCode == 0;
            var account = json["email"]?.GetValue<string>() ?? json["authMethod"]?.GetValue<string>();
            return new ClaudeAuthStatus(true, loggedIn, loggedIn ? account : null, loggedIn ? "Signed in." : "Not signed in.");
        }
        catch
        {
            // Plain text output (older CLI): rely on the exit code.
            return new ClaudeAuthStatus(true, exitCode == 0, null, exitCode == 0 ? "Signed in." : "Not signed in.");
        }
    }

    /// <summary>
    /// Opens a console window running <c>claude auth login</c>, which takes the user through the browser sign-in.
    /// Must be called from the interactive user session (the desktop app), never from a Windows service.
    /// </summary>
    public static Process? StartLogin(string cliPath)
    {
        var info = new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " auth login & echo. & pause\"")
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        return Process.Start(info);
    }

    public static async Task<int> LogoutAsync(string cliPath, CancellationToken cancellationToken)
        => (await RunAsync(cliPath, ["auth", "logout"], cancellationToken).ConfigureAwait(false)).ExitCode;

    /// <summary>
    /// Builds a hidden, redirected process for the CLI. <c>.cmd</c> shims (npm installs) go through cmd.exe, so arguments
    /// must not contain cmd metacharacters such as &amp; | &lt; &gt; ^.
    /// </summary>
    public static ProcessStartInfo CreateStartInfo(string cliPath, IEnumerable<string> arguments, string workingDirectory)
    {
        var args = string.Join(' ', arguments.Select(QuoteArgument));
        var isShim = cliPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                     || cliPath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

        var info = isShim
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + QuoteArgument(cliPath) + " " + args + "\"")
            : new ProcessStartInfo(cliPath, args);

        info.UseShellExecute = false;
        info.CreateNoWindow = true;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = new UTF8Encoding(false);
        info.StandardErrorEncoding = new UTF8Encoding(false);
        info.WorkingDirectory = workingDirectory;
        return info;
    }

    /// <summary>Kills the process and its children (the CLI spawns node / shell processes).</summary>
    public static void KillTree(Process process)
    {
        try
        {
            if (process.HasExited) return;
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Already gone.
        }
    }

    /// <summary>Quotes one argument following the CommandLineToArgvW rules.</summary>
    public static string QuoteArgument(string arg)
    {
        if (string.IsNullOrEmpty(arg)) return "\"\"";
        if (arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0) return arg;

        var sb = new StringBuilder("\"");
        for (var i = 0; i < arg.Length; i++)
        {
            var backslashes = 0;
            while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

            if (i == arg.Length)
            {
                sb.Append('\\', backslashes * 2);
                break;
            }
            if (arg[i] == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(arg[i]);
            }
        }
        return sb.Append('"').ToString();
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string cliPath, IEnumerable<string> arguments, CancellationToken cancellationToken)
    {
        var info = CreateStartInfo(cliPath, arguments, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        using var process = new Process { StartInfo = info };
        process.Start();
        process.StandardInput.Close();

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);

        using (cancellationToken.Register(() => KillTree(process)))
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        return (process.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false));
    }
}
