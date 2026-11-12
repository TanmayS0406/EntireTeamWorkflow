using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Etw.Shared;
using Etw.Shared.Models;

namespace Etw.Integrations.Services;

/// <summary>
/// Runs git for the repositories an "existing repositories" project works in: clone or refresh, start a branch for the
/// run, and later commit, push and open a pull request. It authenticates with the signed-in user's Azure DevOps token,
/// handed to git through environment variables (never a command line), so the user needs no PAT or credential manager.
/// </summary>
public sealed partial class GitRepoService(AzureDevOpsService ado, ILogger<GitRepoService> log)
{
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(20);

    public async Task<GitPrepareResult> PrepareAsync(GitPrepareRequest request, CancellationToken ct)
    {
        var folder = ResolveFolder(request.Workspace, request.Folder);
        var branch = RequireBranch(request.Branch, requireRunPrefix: true);
        var url = RemoteUrl(request.Organization, request.Project, request.RepoName);
        var token = await ado.AccessTokenAsync(ct);
        var cloned = false;

        if (!Directory.Exists(Path.Combine(folder, ".git")))
        {
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
            {
                throw new InvalidOperationException($"{folder} already exists, is not empty and is not a git repository. Choose another workspace folder or remove it.");
            }

            var parent = Path.GetDirectoryName(folder)!;
            Directory.CreateDirectory(parent);
            log.LogInformation("Cloning {Repo} into {Folder}", request.RepoName, folder);
            await GitAsync(parent, token, ["-c", "core.longpaths=true", "clone", "--origin", "origin", url, folder], ct);
            cloned = true;
        }
        else
        {
            // The URL as configured, not as a url.<base>.insteadOf rewrite would turn it.
            var origin = (await GitAsync(folder, token, ["config", "--get", "remote.origin.url"], ct)).Trim();
            if (!SameRemote(origin, url)) throw new InvalidOperationException($"{folder} is a clone of a different repository ({origin}). Choose another workspace folder.");

            // Never destroy the user's own work: a repository with local changes is left alone.
            if ((await GitAsync(folder, token, ["status", "--porcelain"], ct)).Trim().Length > 0)
            {
                throw new InvalidOperationException($"{request.RepoName} has uncommitted changes in {folder}. Commit or discard them first, then run again.");
            }
            await GitAsync(folder, token, ["fetch", "origin", "--prune"], ct);
        }

        var baseBranch = request.BaseBranch.Length > 0 ? RequireBranch(request.BaseBranch) : await DefaultBranchAsync(folder, token, ct);
        if (!await RefExistsAsync(folder, token, $"refs/remotes/origin/{baseBranch}", ct))
        {
            throw new InvalidOperationException($"{request.RepoName} has no branch '{baseBranch}' (an empty repository?). Push an initial commit first.");
        }

        // Every run works on its own branch, started from the latest state of the base branch.
        await GitAsync(folder, token, ["checkout", "--no-track", "-B", branch, $"origin/{baseBranch}"], ct);
        return new GitPrepareResult { Folder = folder, BaseBranch = baseBranch, Branch = branch, Cloned = cloned };
    }

    public async Task<GitPublishResult> PublishAsync(GitPublishRequest request, CancellationToken ct)
    {
        var folder = ResolveFolder(request.Workspace, request.Folder);
        if (!Directory.Exists(Path.Combine(folder, ".git"))) throw new InvalidOperationException($"{request.RepoName} is not cloned in {folder}.");

        var branch = RequireBranch(request.Branch, requireRunPrefix: true);
        var baseBranch = RequireBranch(request.BaseBranch);
        var token = await ado.AccessTokenAsync(ct);

        var current = (await GitAsync(folder, token, ["rev-parse", "--abbrev-ref", "HEAD"], ct)).Trim();
        if (current != branch) throw new InvalidOperationException($"{request.RepoName} is on branch '{current}' instead of the run's branch '{branch}', so nothing was published.");

        var files = (await GitAsync(folder, token, ["status", "--porcelain"], ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
        if (files > 0)
        {
            await GitAsync(folder, token, ["add", "-A"], ct);
            await GitAsync(folder, token, ["commit", "-m", Clip(request.CommitMessage.Length > 0 ? request.CommitMessage : request.PullRequestTitle, 4000)], ct);
        }

        var ahead = int.Parse((await GitAsync(folder, token, ["rev-list", "--count", $"origin/{baseBranch}..HEAD"], ct)).Trim());
        if (ahead == 0) return new GitPublishResult { Changed = false };

        await GitAsync(folder, token, ["push", "origin", $"HEAD:refs/heads/{branch}"], ct);
        try
        {
            var (id, url) = await ado.CreatePullRequestAsync(request.Organization, request.Project, request.RepoId, request.RepoName,
                branch, baseBranch, request.PullRequestTitle, request.PullRequestDescription, request.WorkItemIds, ct);
            return new GitPublishResult { Changed = true, FilesChanged = files, PullRequestId = id, PullRequestUrl = url };
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException)
        {
            throw new InvalidOperationException($"The branch '{branch}' was pushed to {request.RepoName}, but the pull request could not be created: {ex.Message}");
        }
    }

    // ---- git ----------------------------------------------------------------------------------------------------

    private async Task<string> DefaultBranchAsync(string folder, string token, CancellationToken ct)
    {
        try
        {
            var head = (await GitAsync(folder, token, ["symbolic-ref", "--short", "refs/remotes/origin/HEAD"], ct)).Trim();
            if (head.StartsWith("origin/", StringComparison.Ordinal)) return RequireBranch(head["origin/".Length..]);
        }
        catch (InvalidOperationException)
        {
            // origin/HEAD is not set: try the usual names.
        }
        foreach (var name in new[] { "main", "master" })
        {
            if (await RefExistsAsync(folder, token, $"refs/remotes/origin/{name}", ct)) return name;
        }
        throw new InvalidOperationException("Could not tell the repository's default branch.");
    }

    private async Task<bool> RefExistsAsync(string folder, string token, string reference, CancellationToken ct)
    {
        try
        {
            await GitAsync(folder, token, ["rev-parse", "--verify", "--quiet", reference], ct);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private async Task<string> GitAsync(string workingDirectory, string token, IEnumerable<string> args, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        // The token travels in the environment: it must not show up in any process list.
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.Environment["GCM_INTERACTIVE"] = "never";
        info.Environment["GIT_CONFIG_COUNT"] = "3";
        info.Environment["GIT_CONFIG_KEY_0"] = "http.extraHeader";
        info.Environment["GIT_CONFIG_VALUE_0"] = "Authorization: Bearer " + token;
        info.Environment["GIT_CONFIG_KEY_1"] = "credential.helper";
        info.Environment["GIT_CONFIG_VALUE_1"] = "";
        info.Environment["GIT_CONFIG_KEY_2"] = "commit.gpgsign";
        info.Environment["GIT_CONFIG_VALUE_2"] = "false";

        var (name, email) = Author();
        info.Environment["GIT_AUTHOR_NAME"] = name;
        info.Environment["GIT_AUTHOR_EMAIL"] = email;
        info.Environment["GIT_COMMITTER_NAME"] = name;
        info.Environment["GIT_COMMITTER_EMAIL"] = email;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(GitTimeout);

        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("Could not start git.");
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("Git was not found. Install Git for Windows (https://git-scm.com/download/win) on this computer so the services can use it.");
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
                if (ct.IsCancellationRequested) throw;
                throw new InvalidOperationException($"git {Verb(args)} took longer than {GitTimeout.TotalMinutes:0} minutes and was stopped.");
            }

            var output = await stdout;
            if (process.ExitCode != 0)
            {
                var error = Redact((await stderr).Trim(), token);
                throw new InvalidOperationException($"git {Verb(args)} failed: {(error.Length > 0 ? Clip(error, 600) : "exit code " + process.ExitCode)}");
            }
            return output;
        }
    }

    /// <summary>The git command (clone, push, ...) for messages: the first argument that is not an option or an option's value.</summary>
    private static string Verb(IEnumerable<string> args) => args.FirstOrDefault(a => !a.StartsWith('-') && a != "core.longpaths=true") ?? "command";

    private (string Name, string Email) Author()
    {
        var account = ado.Account;
        if (string.IsNullOrWhiteSpace(account) || !account.Contains('@')) return ("Entire Team Workflow", "etw@localhost");
        return (account[..account.IndexOf('@')], account);
    }

    // ---- Validation ---------------------------------------------------------------------------------------------

    private static string ResolveFolder(string workspace, string folder)
    {
        var error = FolderRules.Validate(workspace);
        if (error is not null) throw new ArgumentException(error);
        if (!FolderName().IsMatch(folder)) throw new ArgumentException("Invalid repository folder name.");

        var root = Path.GetFullPath(workspace).TrimEnd('\\', '/');
        var full = Path.GetFullPath(Path.Combine(root, folder));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("The repository folder must be inside the workspace folder.");
        return full;
    }

    private static string RequireBranch(string branch, bool requireRunPrefix = false)
    {
        if (!BranchName().IsMatch(branch) || branch.Contains("..") || branch.Contains("@{") || branch.EndsWith('/') || branch.EndsWith(".lock", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{branch}' is not a valid branch name.");
        }
        if (requireRunPrefix && !branch.StartsWith("etw/", StringComparison.Ordinal)) throw new ArgumentException("Run branches must start with etw/.");
        return branch;
    }

    private static string RemoteUrl(string organization, string project, string repoName)
    {
        if (!OrgName().IsMatch(organization)) throw new ArgumentException("Invalid Azure DevOps organization name.");
        if (string.IsNullOrWhiteSpace(project) || project.Length > 100 || project.Any(char.IsControl)) throw new ArgumentException("Invalid Azure DevOps project name.");
        if (string.IsNullOrWhiteSpace(repoName) || repoName.Length > 100 || repoName.Any(char.IsControl)) throw new ArgumentException("Invalid repository name.");
        return $"https://dev.azure.com/{organization}/{Uri.EscapeDataString(project.Trim())}/_git/{Uri.EscapeDataString(repoName.Trim())}";
    }

    /// <summary>Same repository, ignoring a <c>user@</c> part, a trailing .git or slash, and escaping.</summary>
    private static bool SameRemote(string a, string b)
    {
        static string Normalize(string url)
        {
            url = Uri.UnescapeDataString(url.Trim()).TrimEnd('/');
            if (url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) url = url[..^4];
            var at = url.IndexOf('@');
            if (at > 0 && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) url = "https://" + url[(at + 1)..];
            return url.ToLowerInvariant();
        }
        return Normalize(a) == Normalize(b);
    }

    private static string Redact(string text, string secret) => secret.Length > 0 ? text.Replace(secret, "***") : text;

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")]
    private static partial Regex FolderName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,99}$")]
    private static partial Regex BranchName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,63}$")]
    private static partial Regex OrgName();
}
