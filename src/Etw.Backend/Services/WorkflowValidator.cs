using Etw.Shared.Models;

namespace Etw.Backend.Services;

public static class WorkflowValidator
{
    private static readonly HashSet<NodeKind> AttachmentKinds =
        [NodeKind.Model, NodeKind.BackendEngineer, NodeKind.FrontendEngineer, NodeKind.DatabaseEngineer, NodeKind.QaEngineer];

    /// <summary>Returns an error message, or null when the workflow can be saved and executed.</summary>
    public static string? Validate(WorkflowDefinition wf)
    {
        if (wf.Nodes.Count == 0 || wf.Nodes.Count > 80) return "A workflow needs between 1 and 80 nodes.";
        if (wf.Nodes.Select(n => n.Id).Distinct().Count() != wf.Nodes.Count || wf.Nodes.Any(n => string.IsNullOrWhiteSpace(n.Id))) return "Node ids must be unique.";
        if (wf.Nodes.Count(n => n.Kind == NodeKind.Stories) != 1) return "A workflow needs exactly one User stories node.";

        foreach (var node in wf.Nodes)
        {
            if (node.Title.Length > 120) return "A node title is too long.";
            if (node.Config.Any(kv => kv.Key.Length > 60 || kv.Value.Length > 8000)) return $"The settings of '{node.Title}' are too large.";

            var attachment = AttachmentKinds.Contains(node.Kind);
            if (attachment != node.IsAttachment) return $"'{node.Title}' is attached incorrectly.";
            if (attachment)
            {
                var parent = wf.Find(node.ParentId!);
                if (parent is null || !WorkflowGeometry.CanAttachTo(parent.Kind, node.Kind)) return $"'{node.Title}' cannot be attached to that step.";
            }
        }

        var main = wf.Nodes.Where(n => !n.IsAttachment).Select(n => n.Id).ToHashSet();
        foreach (var edge in wf.Edges)
        {
            if (!main.Contains(edge.From) || !main.Contains(edge.To)) return "A connection points at a missing step.";
        }
        if (wf.Edges.GroupBy(e => e.From).Any(g => g.Count() > 1) || wf.Edges.GroupBy(e => e.To).Any(g => g.Count() > 1))
        {
            return "Each step can have only one step before it and one after it.";
        }
        if (wf.Chain().Count != main.Count) return "All steps must be connected in one chain that starts at User stories.";
        return null;
    }
}
