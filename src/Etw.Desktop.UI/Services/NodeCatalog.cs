using Etw.Shared.Models;

namespace Etw.Desktop.UI.Services;

public sealed record NodeInfo(NodeKind Kind, string Title, string Description);

/// <summary>Names and descriptions of the step types shown in the "+" menus and the inspector.</summary>
public static class NodeCatalog
{
    private static readonly Dictionary<NodeKind, NodeInfo> All = new[]
    {
        new NodeInfo(NodeKind.Stories, "User stories", "The backlog the team builds from: imported from Azure DevOps or written here."),
        new NodeInfo(NodeKind.Architect, "Plan the architecture", "An analyst may ask its open questions once; then the architect writes the design, the API contract and the task plan."),
        new NodeInfo(NodeKind.ParallelDev, "Build in parallel", "Backend, UI and QA engineers work at the same time against the architect's contract."),
        new NodeInfo(NodeKind.Integrate, "Integrate and test", "Builds both halves, runs every test suite, fixes what fails and writes the README."),
        new NodeInfo(NodeKind.CustomAgent, "Custom agent", "A Claude agent that does whatever you describe, working in the project folder."),
        new NodeInfo(NodeKind.NotifySlack, "Notify on Slack", "Posts a progress summary to a Slack channel."),
        new NodeInfo(NodeKind.NotifyTeams, "Notify on Teams", "Posts a progress summary to a Microsoft Teams channel."),
        new NodeInfo(NodeKind.DeployAzure, "Deploy to Azure", "Prepares release builds and deploys them to Azure App Service."),
        new NodeInfo(NodeKind.Model, "Claude", "Which Claude model and effort this step uses."),
        new NodeInfo(NodeKind.BackendEngineer, "Backend engineer", "Builds the API from the contract in the chosen backend language."),
        new NodeInfo(NodeKind.FrontendEngineer, "UI engineer", "Builds the user interface in the chosen UI framework, with a mock API until the backend exists."),
        new NodeInfo(NodeKind.DatabaseEngineer, "Database engineer", "Designs the schema, migrations and seed data."),
        new NodeInfo(NodeKind.QaEngineer, "QA engineer", "Writes black-box API tests from the acceptance criteria."),
    }.ToDictionary(n => n.Kind);

    public static NodeInfo Get(NodeKind kind) => All[kind];

    /// <summary>Steps that can be appended to the main chain from the "+" button.</summary>
    public static IReadOnlyList<NodeKind> AddableSteps { get; } =
        [NodeKind.NotifySlack, NodeKind.NotifyTeams, NodeKind.DeployAzure, NodeKind.CustomAgent, NodeKind.Integrate];

    public static IReadOnlyList<NodeKind> AttachableTo(NodeKind parent) => parent switch
    {
        NodeKind.Architect => [NodeKind.Model],
        NodeKind.ParallelDev => [NodeKind.Model, NodeKind.BackendEngineer, NodeKind.FrontendEngineer, NodeKind.DatabaseEngineer, NodeKind.QaEngineer],
        _ => [],
    };
}
