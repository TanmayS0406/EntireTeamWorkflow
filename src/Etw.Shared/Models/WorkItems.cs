namespace Etw.Shared.Models;

public enum WorkItemSource
{
    Local,
    AzureDevOps,
}

/// <summary>A user story / backlog item. Used on the wire and as the persisted row in the backend database.</summary>
public sealed class WorkItemDto
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public WorkItemSource Source { get; set; } = WorkItemSource.Local;

    /// <summary>Azure DevOps work item id (as text) when <see cref="Source"/> is AzureDevOps.</summary>
    public string? ExternalId { get; set; }
    public string? ExternalUrl { get; set; }
    public string? ParentExternalId { get; set; }

    public string Type { get; set; } = "User Story";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public string State { get; set; } = "New";
    public string? IterationPath { get; set; }
    public string? AreaPath { get; set; }
    public int? Priority { get; set; }
    public double? StoryPoints { get; set; }

    /// <summary>Whether the story is part of the next run.</summary>
    public bool Selected { get; set; } = true;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class CreateWorkItemRequest
{
    public string Type { get; set; } = "User Story";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string AcceptanceCriteria { get; set; } = "";
    public int? Priority { get; set; }
    public double? StoryPoints { get; set; }

    /// <summary>Also create the item in Azure DevOps (needs a connection and a project on the Azure DevOps tab).</summary>
    public bool PushToAzureDevOps { get; set; }
}

public sealed class ImportWorkItemsRequest
{
    public List<WorkItemDto> Items { get; set; } = [];
}
