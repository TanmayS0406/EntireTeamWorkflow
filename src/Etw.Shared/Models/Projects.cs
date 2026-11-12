namespace Etw.Shared.Models;

public enum ProjectMode
{
    /// <summary>A new application is designed and built from the stories in an empty folder.</summary>
    NewApp,

    /// <summary>The stories are implemented inside existing Azure DevOps repositories and delivered as pull requests.</summary>
    ExistingRepos,
}

/// <summary>An existing Azure DevOps Git repository that a project works in.</summary>
public sealed class RepoRefDto
{
    /// <summary>Azure DevOps repository id (a GUID).</summary>
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Branch the work starts from and the pull request targets (without refs/heads/).</summary>
    public string DefaultBranch { get; set; } = "main";

    /// <summary>Sub-folder of the project's workspace folder that the repository is cloned into.</summary>
    public string Folder { get; set; } = "";
}

public sealed class ProjectDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>
    /// <see cref="ProjectMode.NewApp"/>: the folder the agents build the application in.
    /// <see cref="ProjectMode.ExistingRepos"/>: the workspace folder the repositories are cloned into (one sub-folder each).
    /// </summary>
    public string TargetFolder { get; set; } = "";

    /// <summary>Id from <see cref="LanguageCatalog.Backends"/>.</summary>
    public string BackendLanguage { get; set; } = "csharp-aspnet";

    /// <summary>Id from <see cref="LanguageCatalog.UiFrameworks"/>.</summary>
    public string UiFramework { get; set; } = "blazor";

    public ProjectMode Mode { get; set; } = ProjectMode.NewApp;

    /// <summary>The Azure DevOps organization and project this project's backlog and repositories belong to. Empty = use the Connect tab's default.</summary>
    public string AzureDevOpsOrganization { get; set; } = "";
    public string AzureDevOpsProject { get; set; } = "";

    /// <summary>The repositories of an <see cref="ProjectMode.ExistingRepos"/> project.</summary>
    public List<RepoRefDto> Repos { get; set; } = [];

    public WorkflowDefinition Workflow { get; set; } = WorkflowDefinition.Default();
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SaveProjectRequest
{
    public string Name { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public string BackendLanguage { get; set; } = "csharp-aspnet";
    public string UiFramework { get; set; } = "blazor";
    public ProjectMode Mode { get; set; } = ProjectMode.NewApp;
    public string AzureDevOpsOrganization { get; set; } = "";
    public string AzureDevOpsProject { get; set; } = "";
    public List<RepoRefDto> Repos { get; set; } = [];
}

/// <summary>Machine-wide settings, edited on the Settings tab.</summary>
public sealed class AppSettingsDto
{
    /// <summary>Full path of claude.exe / claude.cmd. Empty = search PATH and the usual install folders.</summary>
    public string ClaudePath { get; set; } = "";

    /// <summary>Claude model alias or id (opus, sonnet, haiku, fable, claude-...). Empty = Claude Code's default.</summary>
    public string Model { get; set; } = "";

    /// <summary>low, medium, high, xhigh, max. Empty = the model's default.</summary>
    public string Effort { get; set; } = "";

    /// <summary>acceptEdits (edit files, run the allow-listed build tools) or auto (Claude Code's classifier decides).</summary>
    public string PermissionMode { get; set; } = "acceptEdits";

    /// <summary>How many Claude agents may run at the same moment (plan limits make this worth capping).</summary>
    public int MaxParallelAgents { get; set; } = 3;

    public int AgentTimeoutMinutes { get; set; } = 45;

    /// <summary>After the stories are loaded, let the analyst ask its open questions once, then run without interruption.</summary>
    public bool AskClarifyingQuestions { get; set; } = true;

    /// <summary>Run the pipeline with simulated agents (no Claude calls, no files written). For demos and UI work.</summary>
    public bool SimulateAgents { get; set; }

    /// <summary>Folder offered by default when creating a project.</summary>
    public string DefaultWorkspaceRoot { get; set; } = "";
}

public sealed class ClaudeStatusDto
{
    public bool CliFound { get; set; }
    public bool LoggedIn { get; set; }
    public string? Account { get; set; }
    public string? CliPath { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class ServiceHealthDto
{
    public string Name { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Healthy { get; set; }
    public string? Detail { get; set; }
}
