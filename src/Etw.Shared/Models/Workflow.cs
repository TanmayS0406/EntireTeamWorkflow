namespace Etw.Shared.Models;

public enum NodeKind
{
    // Main chain
    Stories,
    Architect,
    ParallelDev,
    Integrate,
    CustomAgent,
    NotifySlack,
    NotifyTeams,
    DeployAzure,

    // Attached to an agent node (shown under it, connected by a dashed line)
    Model,
    BackendEngineer,
    FrontendEngineer,
    DatabaseEngineer,
    QaEngineer,
}

public sealed class WorkflowNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public NodeKind Kind { get; set; }
    public string Title { get; set; } = "";
    public double X { get; set; }
    public double Y { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>Set for nodes attached under an agent node. Their position is derived from the parent.</summary>
    public string? ParentId { get; set; }

    public Dictionary<string, string> Config { get; set; } = [];

    public bool IsAttachment => ParentId is not null;

    public string Get(string key, string fallback = "") => Config.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;
}

public sealed class WorkflowEdge
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
}

/// <summary>The visual workflow: a main chain of steps (edges) plus nodes attached to agent steps (ParentId).</summary>
public sealed class WorkflowDefinition
{
    public List<WorkflowNode> Nodes { get; set; } = [];
    public List<WorkflowEdge> Edges { get; set; } = [];

    /// <summary>Main-chain nodes in execution order, starting at the Stories trigger.</summary>
    public List<WorkflowNode> Chain()
    {
        var chain = new List<WorkflowNode>();
        var start = Nodes.FirstOrDefault(n => n.Kind == NodeKind.Stories && !n.IsAttachment);
        var seen = new HashSet<string>();
        for (var node = start; node is not null && seen.Add(node.Id);)
        {
            chain.Add(node);
            var next = Edges.FirstOrDefault(e => e.From == node.Id)?.To;
            node = next is null ? null : Nodes.FirstOrDefault(n => n.Id == next);
        }
        return chain;
    }

    public List<WorkflowNode> ChildrenOf(string parentId) => Nodes.Where(n => n.ParentId == parentId).ToList();

    public WorkflowNode? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>Appends a main-chain node after the last one (or after <paramref name="afterId"/>).</summary>
    public WorkflowNode InsertAfter(WorkflowNode node, string? afterId = null)
    {
        var chain = Chain();
        var anchor = afterId is null ? chain.LastOrDefault() : chain.FirstOrDefault(n => n.Id == afterId) ?? chain.LastOrDefault();
        if (anchor is not null)
        {
            node.X = anchor.X + WorkflowGeometry.Width(anchor.Kind) + 130;
            node.Y = anchor.Y;
            var existing = Edges.FirstOrDefault(e => e.From == anchor.Id);
            var next = existing?.To;
            if (existing is not null) Edges.Remove(existing);
            Edges.Add(new WorkflowEdge { From = anchor.Id, To = node.Id });
            if (next is not null) Edges.Add(new WorkflowEdge { From = node.Id, To = next });
        }
        Nodes.Add(node);
        return node;
    }

    public WorkflowNode Attach(string parentId, WorkflowNode node)
    {
        node.ParentId = parentId;
        Nodes.Add(node);
        return node;
    }

    public void Remove(string id)
    {
        var node = Find(id);
        if (node is null) return;
        foreach (var child in ChildrenOf(id).ToList()) Remove(child.Id);

        var incoming = Edges.Where(e => e.To == id).ToList();
        var outgoing = Edges.Where(e => e.From == id).ToList();
        Edges.RemoveAll(e => e.To == id || e.From == id);
        // Heal the chain: connect the node before to the node after.
        if (incoming.Count == 1 && outgoing.Count == 1)
        {
            Edges.Add(new WorkflowEdge { From = incoming[0].From, To = outgoing[0].To });
        }
        Nodes.Remove(node);
    }

    /// <summary>The workflow a new project starts with, laid out like the n8n canvas it imitates.</summary>
    public static WorkflowDefinition Default()
    {
        var wf = new WorkflowDefinition();

        var stories = new WorkflowNode { Id = "stories", Kind = NodeKind.Stories, Title = "User stories", X = 60, Y = 170 };
        var architect = new WorkflowNode { Id = "architect", Kind = NodeKind.Architect, Title = "Plan the architecture", X = 240, Y = 170 };
        var dev = new WorkflowNode { Id = "dev", Kind = NodeKind.ParallelDev, Title = "Build in parallel", X = 590, Y = 170 };
        var integrate = new WorkflowNode { Id = "integrate", Kind = NodeKind.Integrate, Title = "Integrate and test", X = 940, Y = 170 };
        var slack = new WorkflowNode { Id = "slack", Kind = NodeKind.NotifySlack, Title = "Notify on Slack", X = 1130, Y = 170, Enabled = false };

        wf.Nodes.AddRange([stories, architect, dev, integrate, slack]);
        wf.Edges.AddRange(
        [
            new WorkflowEdge { From = "stories", To = "architect" },
            new WorkflowEdge { From = "architect", To = "dev" },
            new WorkflowEdge { From = "dev", To = "integrate" },
            new WorkflowEdge { From = "integrate", To = "slack" },
        ]);

        wf.Attach("architect", new WorkflowNode { Id = "architect-model", Kind = NodeKind.Model, Title = "Claude" });
        wf.Attach("dev", new WorkflowNode { Id = "dev-model", Kind = NodeKind.Model, Title = "Claude" });
        wf.Attach("dev", new WorkflowNode { Id = "dev-backend", Kind = NodeKind.BackendEngineer, Title = "Backend engineer" });
        wf.Attach("dev", new WorkflowNode { Id = "dev-frontend", Kind = NodeKind.FrontendEngineer, Title = "UI engineer" });
        wf.Attach("dev", new WorkflowNode { Id = "dev-qa", Kind = NodeKind.QaEngineer, Title = "QA engineer" });
        return wf;
    }
}

/// <summary>Node sizes in canvas pixels. The canvas and the edge routing both use these.</summary>
public static class WorkflowGeometry
{
    public const double IconSize = 96;
    public const double AgentWidth = 252;
    public const double AgentHeight = 96;
    public const double SubSpacing = 150;
    public const double SubDrop = 150;

    public static bool IsAgent(NodeKind kind) => kind is NodeKind.Architect or NodeKind.ParallelDev;

    public static double Width(NodeKind kind) => IsAgent(kind) ? AgentWidth : IconSize;

    public static double Height(NodeKind kind) => IsAgent(kind) ? AgentHeight : IconSize;

    public static bool CanAttachTo(NodeKind parent, NodeKind child) => parent switch
    {
        NodeKind.Architect => child == NodeKind.Model,
        NodeKind.ParallelDev => child is NodeKind.Model or NodeKind.BackendEngineer or NodeKind.FrontendEngineer
            or NodeKind.DatabaseEngineer or NodeKind.QaEngineer,
        _ => false,
    };

    /// <summary>Top-left of an attached node, spread evenly under its parent.</summary>
    public static (double X, double Y) AttachmentPosition(WorkflowNode parent, int index, int count)
    {
        var span = (count - 1) * SubSpacing;
        var centre = parent.X + AgentWidth / 2;
        return (centre - span / 2 + index * SubSpacing - IconSize / 2, parent.Y + AgentHeight + SubDrop);
    }

    /// <summary>X of the diamond socket for attachment <paramref name="index"/> on the parent's bottom edge.</summary>
    public static double SocketX(WorkflowNode parent, int index, int count) => parent.X + AgentWidth * (index + 1) / (count + 1);
}
