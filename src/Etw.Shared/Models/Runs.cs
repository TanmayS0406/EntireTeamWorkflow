namespace Etw.Shared.Models;

public enum RunStatus
{
    Pending,
    Running,
    AwaitingInput,
    Succeeded,
    Failed,
    Cancelled,
}

public enum RunEventKind
{
    Info,
    RunStarted,
    NodeStarted,
    NodeCompleted,
    NodeFailed,
    NodeSkipped,
    AgentStarted,
    AgentText,
    AgentActivity,
    AgentCompleted,
    AgentFailed,
    Questions,
    Answered,
    Notification,
    Deployment,
    RunCompleted,
    RunFailed,
    RunCancelled,
}

public sealed class ClarifyingQuestion
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public string Answer { get; set; } = "";
}

public sealed class RunDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public RunStatus Status { get; set; } = RunStatus.Pending;
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? EndedUtc { get; set; }
    public string? Summary { get; set; }
    public int StoryCount { get; set; }
    public List<ClarifyingQuestion> PendingQuestions { get; set; } = [];
}

public sealed class RunEventDto
{
    public long Id { get; set; }
    public Guid RunId { get; set; }
    public DateTime TimeUtc { get; set; } = DateTime.UtcNow;
    public RunEventKind Kind { get; set; }

    /// <summary>Workflow node this event belongs to (an agent node id for agent events).</summary>
    public string? NodeId { get; set; }

    /// <summary>Display name of the agent, e.g. "Backend engineer".</summary>
    public string? Agent { get; set; }
    public string Message { get; set; } = "";
}

public sealed class StartRunRequest
{
    /// <summary>Stories to build. Empty = every story currently ticked in the backlog.</summary>
    public List<Guid> WorkItemIds { get; set; } = [];
}

public sealed class AnswerQuestionsRequest
{
    public List<ClarifyingQuestion> Answers { get; set; } = [];
}

/// <summary>Everything the orchestrator needs to execute one run. Built by the backend.</summary>
public sealed class RunRequest
{
    public Guid RunId { get; set; }
    public Guid ProjectId { get; set; }
    public string ProjectName { get; set; } = "";
    public string TargetFolder { get; set; } = "";
    public string BackendLanguage { get; set; } = "";
    public string UiFramework { get; set; } = "";
    public ProjectMode Mode { get; set; } = ProjectMode.NewApp;
    public string AzureDevOpsOrganization { get; set; } = "";
    public string AzureDevOpsProject { get; set; } = "";
    public List<RepoRefDto> Repos { get; set; } = [];
    public List<WorkItemDto> Stories { get; set; } = [];
    public WorkflowDefinition Workflow { get; set; } = new();
    public AppSettingsDto Settings { get; set; } = new();
}

/// <summary>Orchestrator → backend: status change of a run.</summary>
public sealed class RunStatusUpdate
{
    public RunStatus Status { get; set; }
    public string? Summary { get; set; }
    public List<ClarifyingQuestion>? PendingQuestions { get; set; }
}
