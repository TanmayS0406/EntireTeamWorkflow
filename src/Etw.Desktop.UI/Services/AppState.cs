using Etw.Shared.Auth;
using Etw.Shared.Claude;

namespace Etw.Desktop.UI.Services;

public enum Tab
{
    Workflow,
    Backlog,
    Runs,
    Integrations,
    Settings,
}

public enum NodeRunState
{
    Idle,
    Running,
    Succeeded,
    Failed,
    Skipped,
}

public enum EntraTarget
{
    AzureDevOps,
    Azure,
    Teams,
}

public sealed record Toast(Guid Id, string Message, bool IsError);

/// <summary>Everything the screens show, and every action they can take. Components stay thin and call into this.</summary>
public sealed class AppState : IAsyncDisposable
{
    private static readonly HttpClient OAuthHttp = new();
    private readonly List<Toast> _toasts = [];
    private CancellationTokenSource? _saveDebounce;

    public AppState(GatewayClient gateway, ClaudeLoginService claude, IShell shell)
    {
        Gateway = gateway;
        Claude = claude;
        Shell = shell;
        gateway.RunEventReceived += OnRunEvent;
        gateway.RunStatusReceived += OnRunStatus;
    }

    public GatewayClient Gateway { get; }
    public ClaudeLoginService Claude { get; }
    public IShell Shell { get; }

    public event Action? Changed;

    public void Notify() => Changed?.Invoke();

    // ---- Claude sign-in -----------------------------------------------------------------------------------------

    public bool Initialized { get; private set; }
    public bool ClaudeSignedIn { get; private set; }
    public bool ClaudeCliFound { get; private set; } = true;
    public string? ClaudeAccount { get; private set; }
    public string AuthMessage { get; private set; } = "";
    public bool AuthBusy { get; private set; }
    public ClaudeStatusDto? BackendClaude { get; private set; }

    /// <summary>The user is signed in here, but the backend service (which runs the agents) is not: it runs as another Windows account.</summary>
    public bool ClaudeMismatch => ClaudeSignedIn && BackendClaude is { LoggedIn: false };

    public async Task InitializeAsync()
    {
        ApplyClaude(await Claude.GetStatusAsync());
        Initialized = true;
        Notify();
        if (ClaudeSignedIn) await LoadAllAsync();
    }

    public async Task SignInAsync()
    {
        AuthBusy = true;
        AuthMessage = "Opening the Claude sign-in…";
        Notify();
        try
        {
            var status = await Claude.SignInAsync(message => { AuthMessage = message; Notify(); }, CancellationToken.None);
            ApplyClaude(status);
            AuthMessage = status.LoggedIn ? "" : "Sign-in was not completed. Try again.";
            if (status.LoggedIn) await LoadAllAsync();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AuthMessage = ex.Message;
        }
        finally
        {
            AuthBusy = false;
            Notify();
        }
    }

    public async Task SignOutAsync()
    {
        await TryAsync(async () =>
        {
            await Claude.SignOutAsync();
            ApplyClaude(await Claude.GetStatusAsync());
            Run = null;
            Events.Clear();
        });
    }

    private void ApplyClaude(ClaudeAuthStatus status)
    {
        ClaudeCliFound = status.CliFound;
        ClaudeSignedIn = status.LoggedIn;
        ClaudeAccount = status.Account;
        if (!status.CliFound) AuthMessage = "Claude Code CLI was not found. Install it from https://claude.com/claude-code, or set its path below.";
    }

    // ---- Loading ------------------------------------------------------------------------------------------------

    public string? BackendError { get; private set; }
    public bool Loading { get; private set; }
    public bool LiveConnected { get; private set; }
    public AppSettingsDto Settings { get; private set; } = new();
    public List<ProjectDto> Projects { get; private set; } = [];
    public List<ServiceHealthDto> Services { get; private set; } = [];

    public async Task LoadAllAsync()
    {
        Loading = true;
        Notify();
        try
        {
            Gateway.ConnectionChanged -= OnConnectionChanged;
            Gateway.ConnectionChanged += OnConnectionChanged;
            await Gateway.WaitUntilReadyAsync(TimeSpan.FromSeconds(45));
            await Gateway.StartHubAsync();

            Settings = await Gateway.GetAsync<AppSettingsDto>("/api/settings");
            Claude.ConfiguredPath = Settings.ClaudePath;
            BackendClaude = await Gateway.GetAsync<ClaudeStatusDto>("/api/claude/status");
            Projects = await Gateway.GetAsync<List<ProjectDto>>("/api/projects");
            BackendError = null;

            if (Projects.Count > 0 && Project is null) await SelectProjectAsync(Projects[0].Id);
            else if (Project is not null) await SelectProjectAsync(Project.Id);
            _ = RefreshServicesAsync();
            _ = RefreshIntegrationsAsync();
        }
        catch (GatewayException ex)
        {
            BackendError = ex.Message;
        }
        finally
        {
            Loading = false;
            Notify();
        }
    }

    private void OnConnectionChanged(bool connected)
    {
        LiveConnected = connected;
        Notify();
    }

    public async Task RefreshServicesAsync()
    {
        try { Services = await Gateway.GetAsync<List<ServiceHealthDto>>("/api/services"); }
        catch (GatewayException) { Services = []; }
        Notify();
    }

    // ---- Navigation / toasts ------------------------------------------------------------------------------------

    public Tab ActiveTab { get; private set; } = Tab.Workflow;

    public void Go(Tab tab)
    {
        ActiveTab = tab;
        if (tab == Tab.Settings) _ = RefreshServicesAsync();
        if (tab == Tab.Integrations) _ = RefreshIntegrationsAsync();
        Notify();
    }

    public IReadOnlyList<Toast> Toasts => _toasts;

    public void ShowToast(string message, bool isError = false)
    {
        var toast = new Toast(Guid.NewGuid(), message, isError);
        _toasts.Add(toast);
        Notify();
        _ = Task.Delay(isError ? 9000 : 4500).ContinueWith(_ =>
        {
            _toasts.Remove(toast);
            Notify();
        });
    }

    /// <summary>Runs an action and turns backend errors into a toast instead of an unhandled exception.</summary>
    public async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is GatewayException or InvalidOperationException or TimeoutException or HttpRequestException)
        {
            ShowToast(ex.Message, isError: true);
            return false;
        }
        finally
        {
            Notify();
        }
    }

    // ---- Projects -----------------------------------------------------------------------------------------------

    public ProjectDto? Project { get; private set; }
    public List<WorkItemDto> Stories { get; private set; } = [];

    public async Task SelectProjectAsync(Guid id)
    {
        Project = await Gateway.GetAsync<ProjectDto>($"/api/projects/{id}");
        SelectedNodeId = null;
        Stories = await Gateway.GetAsync<List<WorkItemDto>>($"/api/projects/{id}/workitems");
        Run = null;
        Events.Clear();
        NodeStates.Clear();
        await LoadRunHistoryAsync();
        if (RunHistory.Count > 0) await LoadRunAsync(RunHistory[0].Id);
        Notify();
    }

    public Task SelectProjectSafeAsync(Guid id) => TryAsync(() => SelectProjectAsync(id));

    public async Task<bool> CreateProjectAsync(SaveProjectRequest request)
    {
        return await TryAsync(async () =>
        {
            var created = await Gateway.SendAsync<ProjectDto>(HttpMethod.Post, "/api/projects", request);
            Projects = await Gateway.GetAsync<List<ProjectDto>>("/api/projects");
            await SelectProjectAsync(created.Id);
            ShowToast($"Project “{created.Name}” created.");
        });
    }

    public Task<bool> SaveProjectAsync(SaveProjectRequest request) => TryAsync(async () =>
    {
        if (Project is null) return;
        Project = await Gateway.SendAsync<ProjectDto>(HttpMethod.Put, $"/api/projects/{Project.Id}", request);
        Projects = await Gateway.GetAsync<List<ProjectDto>>("/api/projects");
    });

    public Task UpdateProjectSetting(Action<SaveProjectRequest> change)
    {
        if (Project is null) return Task.CompletedTask;
        var request = new SaveProjectRequest
        {
            Name = Project.Name,
            TargetFolder = Project.TargetFolder,
            BackendLanguage = Project.BackendLanguage,
            UiFramework = Project.UiFramework,
            Mode = Project.Mode,
            AzureDevOpsOrganization = Project.AzureDevOpsOrganization,
            AzureDevOpsProject = Project.AzureDevOpsProject,
            Repos = Project.Repos.Select(r => new RepoRefDto { Id = r.Id, Name = r.Name, DefaultBranch = r.DefaultBranch, Folder = r.Folder }).ToList(),
        };
        change(request);
        return SaveProjectAsync(request);
    }

    public Task DeleteProjectAsync() => TryAsync(async () =>
    {
        if (Project is null) return;
        await Gateway.SendAsync(HttpMethod.Delete, $"/api/projects/{Project.Id}");
        Projects = await Gateway.GetAsync<List<ProjectDto>>("/api/projects");
        Project = null;
        Stories = [];
        Run = null;
        if (Projects.Count > 0) await SelectProjectAsync(Projects[0].Id);
        ShowToast("Project removed. The generated files in its folder were left untouched.");
    });

    // ---- Workflow editing ---------------------------------------------------------------------------------------

    public string? SelectedNodeId { get; private set; }
    public WorkflowNode? SelectedNode => Project?.Workflow.Find(SelectedNodeId ?? "");

    public void SelectNode(string? id)
    {
        SelectedNodeId = id;
        Notify();
    }

    public void AddStep(NodeKind kind)
    {
        if (Project is null) return;
        var info = NodeCatalog.Get(kind);
        var node = new WorkflowNode { Kind = kind, Title = info.Title };
        Project.Workflow.InsertAfter(node);
        SelectedNodeId = node.Id;
        QueueWorkflowSave();
    }

    public void AttachNode(string parentId, NodeKind kind)
    {
        if (Project is null) return;
        var node = new WorkflowNode { Kind = kind, Title = NodeCatalog.Get(kind).Title };
        Project.Workflow.Attach(parentId, node);
        SelectedNodeId = node.Id;
        QueueWorkflowSave();
    }

    public void RemoveNode(string id)
    {
        if (Project is null) return;
        Project.Workflow.Remove(id);
        if (SelectedNodeId == id) SelectedNodeId = null;
        QueueWorkflowSave();
    }

    /// <summary>Saves the workflow shortly after the last change, so dragging a node does not send a request per pixel.</summary>
    public void QueueWorkflowSave()
    {
        Notify();
        _saveDebounce?.Cancel();
        var cts = _saveDebounce = new CancellationTokenSource();
        _ = Task.Delay(500, cts.Token).ContinueWith(async t =>
        {
            if (t.IsCanceled || Project is null) return;
            await TryAsync(async () => Project.Workflow = await Gateway.SendAsync<WorkflowDefinition>(HttpMethod.Put, $"/api/projects/{Project.Id}/workflow", Project.Workflow));
        }, TaskScheduler.Default);
    }

    // ---- Settings -----------------------------------------------------------------------------------------------

    public Task<bool> SaveSettingsAsync(AppSettingsDto settings) => TryAsync(async () =>
    {
        Settings = await Gateway.SendAsync<AppSettingsDto>(HttpMethod.Put, "/api/settings", settings);
        Claude.ConfiguredPath = Settings.ClaudePath;
        BackendClaude = await Gateway.GetAsync<ClaudeStatusDto>("/api/claude/status");
        ShowToast("Settings saved.");
    });

    // ---- Stories ------------------------------------------------------------------------------------------------

    public Task<bool> CreateStoryAsync(CreateWorkItemRequest request) => TryAsync(async () =>
    {
        if (Project is null) return;
        await Gateway.SendAsync(HttpMethod.Post, $"/api/projects/{Project.Id}/workitems", request);
        await ReloadStoriesAsync();
    });

    public Task<bool> UpdateStoryAsync(WorkItemDto item) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Put, $"/api/workitems/{item.Id}", item);
        await ReloadStoriesAsync();
    });

    public Task<bool> DeleteStoryAsync(Guid id) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Delete, $"/api/workitems/{id}");
        await ReloadStoriesAsync();
    });

    public Task<bool> SelectAllStoriesAsync(bool value) => TryAsync(async () =>
    {
        if (Project is null) return;
        await Gateway.SendAsync(HttpMethod.Post, $"/api/projects/{Project.Id}/workitems/select?value={value.ToString().ToLowerInvariant()}");
        await ReloadStoriesAsync();
    });

    public Task<bool> ImportFromAzureDevOpsAsync(bool includeClosed) => TryAsync(async () =>
    {
        if (Project is null) return;
        var result = await Gateway.SendAsync<ImportResult>(HttpMethod.Post, $"/api/projects/{Project.Id}/workitems/import-azuredevops?includeClosed={includeClosed.ToString().ToLowerInvariant()}");
        await ReloadStoriesAsync();
        ShowToast($"Azure DevOps backlog loaded: {result.Added} new, {result.Updated} updated, {result.Removed} removed.");
    });

    private async Task ReloadStoriesAsync()
    {
        if (Project is null) return;
        Stories = await Gateway.GetAsync<List<WorkItemDto>>($"/api/projects/{Project.Id}/workitems");
    }

    private sealed record ImportResult(int Added, int Updated, int Removed);

    // ---- Runs ---------------------------------------------------------------------------------------------------

    public RunDto? Run { get; private set; }
    public List<RunDto> RunHistory { get; private set; } = [];
    public List<RunEventDto> Events { get; } = [];
    public Dictionary<string, NodeRunState> NodeStates { get; } = [];

    public bool RunActive => Run is { Status: RunStatus.Running or RunStatus.AwaitingInput or RunStatus.Pending };

    public NodeRunState StateOf(string nodeId) => NodeStates.GetValueOrDefault(nodeId, NodeRunState.Idle);

    public Task<bool> StartRunAsync() => TryAsync(async () =>
    {
        if (Project is null) return;
        Run = await Gateway.SendAsync<RunDto>(HttpMethod.Post, $"/api/projects/{Project.Id}/runs", new StartRunRequest());
        Events.Clear();
        NodeStates.Clear();
        await LoadRunHistoryAsync();
        ShowToast("Run started. After your answers to the analyst's questions (if any) it runs without interruption.");
    });

    public Task CancelRunAsync() => TryAsync(async () =>
    {
        if (Run is null) return;
        await Gateway.SendAsync(HttpMethod.Post, $"/api/runs/{Run.Id}/cancel");
    });

    public Task<bool> AnswerQuestionsAsync(List<ClarifyingQuestion> answers) => TryAsync(async () =>
    {
        if (Run is null) return;
        await Gateway.SendAsync(HttpMethod.Post, $"/api/runs/{Run.Id}/answers", new AnswerQuestionsRequest { Answers = answers });
    });

    public async Task LoadRunHistoryAsync()
    {
        if (Project is null) return;
        RunHistory = await Gateway.GetAsync<List<RunDto>>($"/api/projects/{Project.Id}/runs");
    }

    public async Task LoadRunAsync(Guid runId)
    {
        Run = await Gateway.GetAsync<RunDto>($"/api/runs/{runId}");
        Events.Clear();
        NodeStates.Clear();
        long after = 0;
        while (true)
        {
            var page = await Gateway.GetAsync<List<RunEventDto>>($"/api/runs/{runId}/events?after={after}");
            if (page.Count == 0) break;
            foreach (var e in page) AddEvent(e);
            after = page[^1].Id;
        }
        Notify();
    }

    private void OnRunEvent(RunEventDto e)
    {
        if (Run?.Id != e.RunId) return;
        AddEvent(e);
        Notify();
    }

    private void OnRunStatus(RunDto run)
    {
        if (run.ProjectId != Project?.Id) return;
        if (Run is null || Run.Id == run.Id || run.StartedUtc >= Run.StartedUtc) Run = run;

        var index = RunHistory.FindIndex(r => r.Id == run.Id);
        if (index >= 0) RunHistory[index] = run;
        else RunHistory.Insert(0, run);

        if (run.Status == RunStatus.AwaitingInput) Go(Tab.Runs);
        Notify();
    }

    private void AddEvent(RunEventDto e)
    {
        if (Events.Count >= 4000) Events.RemoveRange(0, 500);
        Events.Add(e);

        var attachment = e.NodeId is not null && Project?.Workflow.Find(e.NodeId)?.IsAttachment == true;
        switch (e.Kind)
        {
            case RunEventKind.NodeStarted when e.NodeId is not null: NodeStates[e.NodeId] = NodeRunState.Running; break;
            case RunEventKind.NodeCompleted when e.NodeId is not null: NodeStates[e.NodeId] = NodeRunState.Succeeded; break;
            case RunEventKind.NodeFailed when e.NodeId is not null: NodeStates[e.NodeId] = NodeRunState.Failed; break;
            case RunEventKind.NodeSkipped when e.NodeId is not null: NodeStates[e.NodeId] = NodeRunState.Skipped; break;
            // Engineers are attached nodes: their own progress shows under the step that runs them.
            case RunEventKind.AgentStarted when attachment: NodeStates[e.NodeId!] = NodeRunState.Running; break;
            case RunEventKind.AgentCompleted when attachment: NodeStates[e.NodeId!] = NodeRunState.Succeeded; break;
            case RunEventKind.AgentFailed when attachment: NodeStates[e.NodeId!] = NodeRunState.Failed; break;
        }
    }

    // ---- Integrations -------------------------------------------------------------------------------------------

    public IntegrationsStatusDto Integrations { get; private set; } = new();
    public ConnectionStatusDto AzureStatus { get; private set; } = new();
    public bool EntraBusy { get; private set; }

    public async Task RefreshIntegrationsAsync()
    {
        try
        {
            Integrations = await Gateway.GetAsync<IntegrationsStatusDto>("/api/integrations/status");
            AzureStatus = await Gateway.GetAsync<ConnectionStatusDto>("/api/azure/status");
        }
        catch (GatewayException)
        {
            // The services may still be starting; the tab shows what it has.
        }
        Notify();
    }

    public Task<bool> SaveEntraConfigAsync(EntraTarget target, string tenantId, string clientId) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Put, EntraPath(target) + "/config", new OAuthConfigDto { TenantId = tenantId, ClientId = clientId });
        await RefreshIntegrationsAsync();
    });

    /// <summary>Browser sign-in with Microsoft Entra ID (OAuth 2.0 + PKCE), done here because a service cannot show a browser. The tokens are then handed to the service.</summary>
    public async Task<bool> ConnectEntraAsync(EntraTarget target, string tenantId, string clientId)
    {
        EntraBusy = true;
        Notify();
        try
        {
            return await TryAsync(async () =>
            {
                clientId = EntraOAuth.ClientOrDefault(clientId, DefaultClientFor(target));
                await Gateway.SendAsync(HttpMethod.Put, EntraPath(target) + "/config", new OAuthConfigDto { TenantId = tenantId, ClientId = clientId });
                var scope = target switch
                {
                    EntraTarget.AzureDevOps => EntraOAuth.AzureDevOpsScope,
                    EntraTarget.Teams => EntraOAuth.TeamsScope,
                    _ => EntraOAuth.AzureArmScope,
                };
                var tokens = await EntraInteractiveSignIn.SignInAsync(OAuthHttp, tenantId, clientId, scope, Shell.OpenUrl, CancellationToken.None);
                await Gateway.SendAsync(HttpMethod.Post, EntraPath(target) + "/connect", new OAuthConnectRequest
                {
                    TenantId = tenantId,
                    ClientId = clientId,
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                    ExpiresOn = tokens.ExpiresOn,
                    Account = EntraOAuth.AccountFromToken(tokens.AccessToken),
                });
                await RefreshIntegrationsAsync();
                ShowToast(target switch
                {
                    EntraTarget.AzureDevOps => "Connected to Azure DevOps.",
                    EntraTarget.Teams => "Connected to Microsoft Teams. Now choose a channel.",
                    _ => "Connected to Azure.",
                });
            });
        }
        finally
        {
            EntraBusy = false;
            Notify();
        }
    }

    public Task<bool> DisconnectEntraAsync(EntraTarget target) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Post, EntraPath(target) + "/disconnect");
        await RefreshIntegrationsAsync();
    });

    private static string EntraPath(EntraTarget target) => target switch
    {
        EntraTarget.AzureDevOps => "/api/integrations/azuredevops",
        EntraTarget.Teams => NotifyPath + "/teams",
        _ => "/api/azure",
    };

    /// <summary>The Microsoft client the sign-in uses when the user has not set their own: Azure CLI's for Azure and Azure DevOps, Microsoft Graph's for Teams.</summary>
    public static string DefaultClientFor(EntraTarget target) => target == EntraTarget.Teams ? EntraOAuth.GraphClientId : EntraOAuth.DefaultClientId;

    // ---- Slack and Teams ----------------------------------------------------------------------------------------

    private const string NotifyPath = "/api/integrations/notifications";

    public bool SlackBusy { get; private set; }

    /// <summary>The Slack app's Client ID: built in when the product ships with one, otherwise the one saved after the one-time setup.</summary>
    public string SlackClientId => SlackOAuth.IsConfigured ? SlackOAuth.ClientId : Integrations.Notifications.Slack.AppClientId;

    /// <summary>False until the Slack app exists and its Client ID is known: the first sign-in then walks through creating it.</summary>
    public bool SlackAvailable => SlackClientId.Length > 0;

    /// <summary>Opens Slack's "create app" page with everything pre-filled; the user only presses Create.</summary>
    public void OpenSlackAppSetup() => Shell.OpenUrl(SlackOAuth.CreateAppUrl());

    /// <summary>Saves the Client ID of the freshly created Slack app (done once, ever).</summary>
    public Task<bool> SaveSlackAppAsync(string clientId) => TryAsync(async () =>
        Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Put, NotifyPath + "/slack/app", new SlackAppRequest { ClientId = clientId }));

    /// <summary>Browser sign-in with Slack (OAuth 2.0 + PKCE, no secret). The token is then handed to the service.</summary>
    public async Task<bool> ConnectSlackAsync()
    {
        SlackBusy = true;
        Notify();
        try
        {
            return await TryAsync(async () =>
            {
                var clientId = SlackClientId;
                var tokens = await SlackInteractiveSignIn.SignInAsync(OAuthHttp, clientId, Shell.OpenUrl, CancellationToken.None);
                Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Post, NotifyPath + "/slack/connect", new SlackConnectRequest
                {
                    ClientId = clientId,
                    AccessToken = tokens.AccessToken,
                    RefreshToken = tokens.RefreshToken,
                    ExpiresOn = tokens.ExpiresOn,
                });
                ShowToast("Connected to Slack. Now choose a channel.");
            });
        }
        finally
        {
            SlackBusy = false;
            Notify();
        }
    }

    /// <summary>Signs out of the active Slack workspace; the next one, if any, becomes active.</summary>
    public Task<bool> DisconnectSlackAsync() => TryAsync(async () =>
        Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Post, NotifyPath + "/slack/disconnect"));

    public Task<bool> SelectSlackWorkspaceAsync(string teamId) => TryAsync(async () =>
        Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Put, NotifyPath + "/slack/workspace", new SlackWorkspaceRequest { TeamId = teamId }));

    public Task<List<NotifyChoiceDto>> SlackChannelsAsync() => Gateway.GetAsync<List<NotifyChoiceDto>>(NotifyPath + "/slack/channels");

    public Task<bool> SelectSlackChannelAsync(NotifyChoiceDto channel) => TryAsync(async () =>
        Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Put, NotifyPath + "/slack/selection",
            new SelectChannelRequest { ChannelId = channel.Id, ChannelName = channel.Name }));

    public Task<List<NotifyChoiceDto>> TeamsListAsync() => Gateway.GetAsync<List<NotifyChoiceDto>>(NotifyPath + "/teams/list");

    public Task<List<NotifyChoiceDto>> TeamsChannelsAsync(string teamId) =>
        Gateway.GetAsync<List<NotifyChoiceDto>>($"{NotifyPath}/teams/{Uri.EscapeDataString(teamId)}/channels");

    public Task<bool> SelectTeamsChannelAsync(NotifyChoiceDto team, NotifyChoiceDto channel) => TryAsync(async () =>
        Integrations.Notifications = await Gateway.SendAsync<NotificationStatusDto>(HttpMethod.Put, NotifyPath + "/teams/selection",
            new SelectChannelRequest { TeamId = team.Id, TeamName = team.Name, ChannelId = channel.Id, ChannelName = channel.Name }));

    public Task<bool> TestNotifyAsync(NotifyChannel channel) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Post, $"{NotifyPath}/test/{channel}");
        ShowToast($"Test message sent to {channel}.");
    });

    public Task<List<string>> AdoOrganizationsAsync() => Gateway.GetAsync<List<string>>("/api/integrations/azuredevops/organizations");

    public Task<List<AdoProjectDto>> AdoProjectsAsync(string organization) =>
        Gateway.GetAsync<List<AdoProjectDto>>($"/api/integrations/azuredevops/projects?organization={Uri.EscapeDataString(organization)}");

    public Task<List<AdoRepoDto>> AdoReposAsync(string organization, string project) =>
        Gateway.GetAsync<List<AdoRepoDto>>($"/api/integrations/azuredevops/repos?organization={Uri.EscapeDataString(organization)}&project={Uri.EscapeDataString(project)}");

    /// <summary>The Azure DevOps organization of the current project: its own link, else the default chosen on the Connect tab.</summary>
    public string ProjectAdoOrganization => Project is { AzureDevOpsOrganization.Length: > 0 } p ? p.AzureDevOpsOrganization : Integrations.AzureDevOps.Organization;

    /// <summary>The Azure DevOps project of the current project: its own link, else the default chosen on the Connect tab.</summary>
    public string ProjectAdoProject => Project is { AzureDevOpsProject.Length: > 0 } p ? p.AzureDevOpsProject : Integrations.AzureDevOps.Project;

    public Task<bool> SelectAdoProjectAsync(string organization, string project) => TryAsync(async () =>
    {
        await Gateway.SendAsync(HttpMethod.Put, "/api/integrations/azuredevops/selection", new AzureDevOpsSelection { Organization = organization, Project = project });
        await RefreshIntegrationsAsync();
    });

    public Task<List<AzureSubscriptionDto>> AzureSubscriptionsAsync() => Gateway.GetAsync<List<AzureSubscriptionDto>>("/api/azure/subscriptions");

    public Task<List<AzureResourceGroupDto>> AzureResourceGroupsAsync(string subscriptionId) =>
        Gateway.GetAsync<List<AzureResourceGroupDto>>($"/api/azure/subscriptions/{subscriptionId}/resourcegroups");

    public Task<List<AzureLocationDto>> AzureLocationsAsync(string subscriptionId) =>
        Gateway.GetAsync<List<AzureLocationDto>>($"/api/azure/subscriptions/{subscriptionId}/locations");

    public async ValueTask DisposeAsync()
    {
        Gateway.RunEventReceived -= OnRunEvent;
        Gateway.RunStatusReceived -= OnRunStatus;
        await Gateway.DisposeAsync();
    }
}
