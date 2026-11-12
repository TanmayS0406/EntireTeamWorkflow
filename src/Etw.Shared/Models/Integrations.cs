namespace Etw.Shared.Models;

/// <summary>Tokens obtained by the desktop app's interactive sign-in and handed to a service to keep and refresh.</summary>
public sealed class OAuthConnectRequest
{
    public string TenantId { get; set; } = "organizations";
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset ExpiresOn { get; set; }
    public string? Account { get; set; }
}

public sealed class OAuthConfigDto
{
    public string TenantId { get; set; } = "organizations";
    public string ClientId { get; set; } = "";
}

public sealed class ConnectionStatusDto
{
    public bool Connected { get; set; }
    public string? Account { get; set; }
    public string TenantId { get; set; } = "organizations";
    public string ClientId { get; set; } = "";
    public string? Detail { get; set; }
}

/// <summary>What the integrations service reports for the Integrations tab.</summary>
public sealed class IntegrationsStatusDto
{
    public AzureDevOpsStatusDto AzureDevOps { get; set; } = new();
    public NotificationStatusDto Notifications { get; set; } = new();
}

// ---- Azure DevOps ---------------------------------------------------------------------------------------------

public sealed class AzureDevOpsStatusDto
{
    public ConnectionStatusDto Connection { get; set; } = new();
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
}

public sealed class AzureDevOpsSelection
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
}

public sealed class AdoProjectDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
}

/// <summary>A Git repository of an Azure DevOps project, as offered in the project's repository picker.</summary>
public sealed class AdoRepoDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>Default branch without refs/heads/. Empty when the repository has no commits yet.</summary>
    public string DefaultBranch { get; set; } = "";
    public string? WebUrl { get; set; }
    public bool IsDisabled { get; set; }
}

/// <summary>Clone (or refresh) one repository into the workspace and start a branch for the run.</summary>
public sealed class GitPrepareRequest
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string RepoName { get; set; } = "";

    /// <summary>The project's workspace folder and the sub-folder of this repository inside it.</summary>
    public string Workspace { get; set; } = "";
    public string Folder { get; set; } = "";
    public string BaseBranch { get; set; } = "";

    /// <summary>Name of the branch to create for the run, e.g. etw/1a2b3c4d.</summary>
    public string Branch { get; set; } = "";
}

public sealed class GitPrepareResult
{
    public string Folder { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string Branch { get; set; } = "";
    public bool Cloned { get; set; }
}

/// <summary>Commit what the agents changed, push the run's branch and open a pull request.</summary>
public sealed class GitPublishRequest
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string RepoId { get; set; } = "";
    public string RepoName { get; set; } = "";
    public string Workspace { get; set; } = "";
    public string Folder { get; set; } = "";
    public string BaseBranch { get; set; } = "";
    public string Branch { get; set; } = "";
    public string CommitMessage { get; set; } = "";
    public string PullRequestTitle { get; set; } = "";
    public string PullRequestDescription { get; set; } = "";

    /// <summary>Azure DevOps work item ids to link to the pull request.</summary>
    public List<int> WorkItemIds { get; set; } = [];
}

public sealed class GitPublishResult
{
    /// <summary>False when the agents changed nothing in this repository (no branch or pull request is created).</summary>
    public bool Changed { get; set; }
    public int FilesChanged { get; set; }
    public int? PullRequestId { get; set; }
    public string? PullRequestUrl { get; set; }
}

public sealed class AdoCreateWorkItemRequest
{
    public string Organization { get; set; } = "";
    public string Project { get; set; } = "";
    public string Type { get; set; } = "User Story";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public int? Priority { get; set; }
    public double? StoryPoints { get; set; }
}

// ---- Notifications --------------------------------------------------------------------------------------------

public enum NotifyChannel
{
    Slack,
    Teams,
}

public enum NotifyLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>Slack tokens obtained by the desktop app's sign-in and handed to the service to keep and refresh.</summary>
public sealed class SlackConnectRequest
{
    public string ClientId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string? RefreshToken { get; set; }
    public DateTimeOffset? ExpiresOn { get; set; }
}

/// <summary>A team or channel the signed-in user can choose to post to.</summary>
public sealed class NotifyChoiceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class SelectChannelRequest
{
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
}

/// <summary>The Client ID of the Slack app (public), saved once after the app was created.</summary>
public sealed class SlackAppRequest
{
    public string ClientId { get; set; } = "";
}

/// <summary>A Slack workspace the user signed in to.</summary>
public sealed class SlackWorkspaceDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Account { get; set; }
}

public sealed class SlackWorkspaceRequest
{
    public string TeamId { get; set; } = "";
}

public sealed class SlackStatusDto
{
    /// <summary>The saved Client ID of the Slack app, empty until the one-time setup was done.</summary>
    public string AppClientId { get; set; } = "";

    /// <summary>True when at least one workspace is signed in. <see cref="Account"/>, <see cref="Workspace"/> and the channel describe the active one.</summary>
    public bool Connected { get; set; }
    public string? Account { get; set; }
    public string? Workspace { get; set; }
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";

    public string ActiveWorkspaceId { get; set; } = "";
    public List<SlackWorkspaceDto> Workspaces { get; set; } = [];
}

public sealed class TeamsStatusDto
{
    public ConnectionStatusDto Connection { get; set; } = new();
    public string TeamId { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string ChannelId { get; set; } = "";
    public string ChannelName { get; set; } = "";
}

public sealed class NotificationStatusDto
{
    public SlackStatusDto Slack { get; set; } = new();
    public TeamsStatusDto Teams { get; set; } = new();
}

public sealed class NotifyRequest
{
    public List<NotifyChannel> Channels { get; set; } = [];
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public NotifyLevel Level { get; set; } = NotifyLevel.Info;
    public Guid? RunId { get; set; }
}

public sealed class NotifyResultDto
{
    public List<string> Delivered { get; set; } = [];
    public List<string> Failed { get; set; } = [];
}

// ---- Azure (ARM) -----------------------------------------------------------------------------------------------

public sealed class AzureSubscriptionDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "";
}

public sealed class AzureResourceGroupDto
{
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
}

public sealed class AzureLocationDto
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

/// <summary>One deployable piece produced by the release step (listed in .etw/deploy/manifest.json).</summary>
public sealed class DeployComponent
{
    public string Name { get; set; } = "";

    /// <summary>Folder to deploy (its contents become the web root), relative to the project's target folder. The Azure service zips it.</summary>
    public string Directory { get; set; } = "";

    /// <summary>App Service Linux runtime, e.g. DOTNETCORE|10.0, NODE|22-lts, PYTHON|3.12, JAVA|21-java21, PHP|8.3.</summary>
    public string Runtime { get; set; } = "";
    public string? StartupCommand { get; set; }
}

public sealed class DeployManifest
{
    public List<DeployComponent> Components { get; set; } = [];
}

public sealed class DeployRequest
{
    public Guid RunId { get; set; }
    public string TargetFolder { get; set; } = "";
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "westeurope";

    /// <summary>Prefix of the web app names; the component name is appended ("myapp-backend").</summary>
    public string AppNamePrefix { get; set; } = "";
    public string PlanSku { get; set; } = "B1";
}

public sealed class DeployResultDto
{
    public bool Succeeded { get; set; }
    public List<string> Log { get; set; } = [];
    public Dictionary<string, string> Urls { get; set; } = [];
    public string? Error { get; set; }
}
