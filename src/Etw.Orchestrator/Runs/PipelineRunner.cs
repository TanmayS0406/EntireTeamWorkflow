using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Etw.Orchestrator.Agents;
using Etw.Shared;
using Etw.Shared.Claude;
using Etw.Shared.Models;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using RunStatus = Etw.Shared.Models.RunStatus;

namespace Etw.Orchestrator.Runs;

public sealed class StageFailedException(string message) : Exception(message);

/// <summary>
/// Executes a workflow like a real delivery team would: the user is asked at most once (right after the stories), then the
/// architect fixes the contract, the engineers build in parallel against it, and integration, notifications and deployment follow.
/// Single steps are one Microsoft Agent Framework agent; the parallel step is an Agent Framework concurrent workflow.
/// </summary>
public sealed partial class PipelineRunner(AgentFactory factory, Notifier notifier, AzureDeployClient deployer, RepoClient repos, ILogger<PipelineRunner> log)
{
    public async Task RunAsync(RunSession s)
    {
        var request = s.Request;
        var events = s.Events;
        var ct = s.Cts.Token;

        try
        {
            if (!s.Simulate)
            {
                s.CliPath = ClaudeCli.Locate(request.Settings.ClaudePath)
                            ?? throw new StageFailedException("Claude Code CLI not found. Install it or set its path in Settings.");
            }
            var folderError = FolderRules.Validate(request.TargetFolder);
            if (folderError is not null) throw new StageFailedException(folderError);

            var brief = new ProjectBrief(request);
            var stories = $"{request.Stories.Count} user stor{(request.Stories.Count == 1 ? "y" : "ies")}";
            events.Emit(RunEventKind.RunStarted, brief.IsExisting
                ? $"{(s.Simulate ? "[Simulation] " : "")}Implementing {stories} of “{request.ProjectName}” in {request.Repos.Count} existing repositor{(request.Repos.Count == 1 ? "y" : "ies")} ({string.Join(", ", request.Repos.Select(r => r.Name))})"
                : $"{(s.Simulate ? "[Simulation] " : "")}Building “{request.ProjectName}” from {stories} · {brief.Backend.Name} + {brief.Ui.Name}");
            PrepareFolder(s, brief);
            if (brief.IsExisting) await PrepareReposAsync(s, ct);

            var answers = new List<ClarifyingQuestion>();
            var chain = request.Workflow.Chain();

            // Existing repositories: the changes become pull requests right after the last step that can change code.
            var publishAfter = brief.IsExisting
                ? chain.LastOrDefault(n => n.Enabled && n.Kind is NodeKind.ParallelDev or NodeKind.Integrate or NodeKind.CustomAgent)?.Id
                : null;
            if (brief.IsExisting && publishAfter is null) events.Emit(RunEventKind.Info, "No step in this workflow changes code, so no pull requests will be created.");

            foreach (var node in chain)
            {
                ct.ThrowIfCancellationRequested();
                if (!node.Enabled)
                {
                    events.Emit(RunEventKind.NodeSkipped, "Skipped (turned off)", node.Id);
                    s.Results.Add(new StageResult(node.Id, node.Title, StageOutcome.Skipped, "turned off"));
                    continue;
                }

                events.Emit(RunEventKind.NodeStarted, node.Title, node.Id);
                try
                {
                    var detail = await ExecuteAsync(s, node, chain, brief, answers, ct);
                    if (node.Id == publishAfter) detail += " · " + await PublishAsync(s, ct);
                    s.Results.Add(new StageResult(node.Id, node.Title, StageOutcome.Succeeded, detail));
                    events.Emit(RunEventKind.NodeCompleted, detail, node.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var message = ex is StageFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
                    s.Results.Add(new StageResult(node.Id, node.Title, StageOutcome.Failed, message));
                    events.Emit(RunEventKind.NodeFailed, message, node.Id);
                    throw new StageFailedException(message);
                }
            }

            var summary = BuildSummary(s);
            events.Emit(RunEventKind.RunCompleted, "Run finished successfully.");
            events.Status(RunStatus.Succeeded, summary);
        }
        catch (OperationCanceledException) when (s.Cts.IsCancellationRequested)
        {
            var reason = s.CancelReason ?? "Cancelled.";
            events.Emit(RunEventKind.RunCancelled, reason);
            events.Status(RunStatus.Cancelled, reason);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Run {Run} failed", request.RunId);
            var message = ex is StageFailedException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            events.Emit(RunEventKind.RunFailed, message);
            await NotifyFailureAsync(s, message);
            events.Status(RunStatus.Failed, BuildSummary(s) + "\n\n" + message);
        }
        finally
        {
            s.FinishedUtc = DateTime.UtcNow;
            await events.DisposeAsync(); // flushes the queue
        }
    }

    // ---- Steps --------------------------------------------------------------------------------------------------

    private async Task<string> ExecuteAsync(RunSession s, WorkflowNode node, List<WorkflowNode> chain, ProjectBrief brief, List<ClarifyingQuestion> answers, CancellationToken ct)
    {
        switch (node.Kind)
        {
            case NodeKind.Stories:
                return $"{s.Request.Stories.Count} stories loaded ({string.Join(", ", s.Request.Stories.GroupBy(x => x.Type).Select(g => $"{g.Count()} {g.Key}"))})";
            case NodeKind.Architect:
                return await ArchitectAsync(s, node, brief, answers, ct);
            case NodeKind.ParallelDev:
                return await ParallelDevAsync(s, node, brief, ct);
            case NodeKind.Integrate:
                return await IntegrateAsync(s, node, brief, ct);
            case NodeKind.CustomAgent:
                return await CustomAsync(s, node, brief, ct);
            case NodeKind.NotifySlack:
            case NodeKind.NotifyTeams:
                return await NotifyAsync(s, node, chain, ct);
            case NodeKind.DeployAzure:
                return await DeployAsync(s, node, brief, ct);
            default:
                throw new StageFailedException($"'{node.Title}' cannot be used as a workflow step.");
        }
    }

    private async Task<string> ArchitectAsync(RunSession s, WorkflowNode node, ProjectBrief brief, List<ClarifyingQuestion> answers, CancellationToken ct)
    {
        var (model, effort) = ModelFor(s, node);

        // The single moment a person is involved: the analyst reads the stories and may ask what it really needs to know.
        if (s.Request.Settings.AskClarifyingQuestions)
        {
            try
            {
                var reply = await RunAgentAsync(s, AgentPrompts.Analyst(brief, model, effort) with { NodeId = node.Id }, AgentPrompts.AnalystTask(brief), ct);
                var questions = ParseQuestions(reply);
                if (questions.Count > 0)
                {
                    s.Events.Emit(RunEventKind.Questions, $"{questions.Count} open question{(questions.Count == 1 ? "" : "s")} about the stories — waiting for your answers. After this the run continues without interruption.", node.Id);
                    var given = await s.AskAsync(questions, ct);
                    answers.AddRange(given);
                    s.Events.Status(RunStatus.Running);
                    s.Events.Emit(RunEventKind.Answered, "Thanks — continuing without further questions.", node.Id);
                    SaveClarifications(s, given);
                }
            }
            catch (StageFailedException ex)
            {
                // The analyst is optional: a failure here must not block the build.
                s.Events.Emit(RunEventKind.Info, "Skipping clarifying questions: " + ex.Message, node.Id);
            }
        }

        if (brief.IsExisting)
        {
            await RunAgentAsync(s, AgentPrompts.ExistingPlanner(brief, model, effort) with { NodeId = node.Id }, AgentPrompts.ExistingPlannerTask(brief, answers), ct);
            if (!s.Simulate && !File.Exists(Path.Combine(s.Request.TargetFolder, ".etw", "docs", "plan.md")))
            {
                throw new StageFailedException("The architect did not write .etw/docs/plan.md, so the engineers have nothing to work from.");
            }
            return "Impact analysis and per-repository plan are ready";
        }

        await RunAgentAsync(s, AgentPrompts.Architect(brief, model, effort) with { NodeId = node.Id }, AgentPrompts.ArchitectTask(brief, answers), ct);

        if (!s.Simulate)
        {
            var folder = s.Request.TargetFolder;
            var contract = new[] { "openapi.yaml", "openapi.yml", "openapi.json" }.Select(f => Path.Combine(folder, "contracts", f)).FirstOrDefault(File.Exists);
            if (contract is null) throw new StageFailedException("The architect did not produce contracts/openapi.yaml, so the engineers have nothing to build against.");
            if (!File.Exists(Path.Combine(folder, "docs", "architecture.md"))) throw new StageFailedException("The architect did not produce docs/architecture.md.");
        }
        return "Architecture, API contract and task plan are ready";
    }

    /// <summary>All engineers work at the same time against the architect's contract: an Agent Framework concurrent workflow.</summary>
    private async Task<string> ParallelDevAsync(RunSession s, WorkflowNode node, ProjectBrief brief, CancellationToken ct)
    {
        // A new app has one engineer per role (the engineers attached to the step); existing repositories get one engineer per repository.
        var specs = new List<AgentSpec>();
        if (brief.IsExisting)
        {
            var (model, effort) = ModelFor(s, node);
            specs.AddRange(brief.Repos.Select(repo => AgentPrompts.RepoEngineer(brief, repo, "repo-" + repo.Folder, $"Engineer · {repo.Name}", model, effort)));
        }
        else
        {
            var members = s.Request.Workflow.ChildrenOf(node.Id).Where(c => c.Enabled && c.Kind != NodeKind.Model).ToList();
            if (members.Count == 0) throw new StageFailedException("No engineers are attached to this step. Attach at least a backend or UI engineer.");

            foreach (var member in members)
            {
                var (model, effort) = ModelFor(s, node, member);
                specs.Add(member.Kind switch
                {
                    NodeKind.BackendEngineer => AgentPrompts.Backend(brief, member.Title, member.Id, model, effort),
                    NodeKind.FrontendEngineer => AgentPrompts.Frontend(brief, member.Title, member.Id, model, effort),
                    NodeKind.DatabaseEngineer => AgentPrompts.Database(brief, member.Title, member.Id, model, effort),
                    NodeKind.QaEngineer => AgentPrompts.Qa(brief, member.Title, member.Id, model, effort),
                    _ => throw new StageFailedException($"'{member.Title}' cannot work in the parallel step."),
                });
            }
        }

        var agents = new List<AIAgent>();
        var reporters = new List<AgentReporter>();
        foreach (var spec in specs)
        {
            agents.Add(factory.Create(s, spec, out var reporter));
            reporters.Add(reporter);
        }

        s.Events.Emit(RunEventKind.Info,
            $"Team: {string.Join(", ", specs.Select(m => m.Name))} — working in parallel (at most {s.Request.Settings.MaxParallelAgents} at the same time).", node.Id);

        var workflow = AgentWorkflowBuilder.BuildConcurrent(
            "parallel-development",
            agents,
            results => [new ChatMessage(ChatRole.Assistant, string.Join("\n\n", results.Select(r => r.LastOrDefault()?.Text ?? "")))]);

        var task = brief.IsExisting
            ? "Implement your repository's part of the stories now. Inputs: .etw/docs/user-stories.md and .etw/docs/plan.md (your repository's section and the cross-repository agreements). Work independently of the other engineers; the plan is what ties your work together."
            : "Build your part of the application now. Inputs: docs/user-stories.md, docs/architecture.md, contracts/openapi.yaml and docs/tasks.md. Work independently of the other engineers; the contract is what ties your work together.";
        var workflowErrors = new List<string>();

        await using var run = await InProcessExecution.RunStreamingAsync(workflow, new List<ChatMessage> { new(ChatRole.User, task) }, cancellationToken: ct);
        await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
        await foreach (var evt in run.WatchStreamAsync(ct))
        {
            if (evt is WorkflowErrorEvent error) workflowErrors.Add(error.Exception?.Message ?? "unknown workflow error");
        }

        var failed = reporters.Where(r => r.Outcome == AgentOutcome.Failed).ToList();
        var summary = string.Join(" · ", reporters.Select(r => $"{r.Name} {(r.Outcome == AgentOutcome.Succeeded ? "✔" : "✘")}"));
        if (workflowErrors.Count > 0) throw new StageFailedException("Parallel development stopped: " + workflowErrors[0]);
        if (failed.Count > 0) throw new StageFailedException($"{summary}. {failed[0].Name}: {failed[0].Error}");
        return summary;
    }

    private async Task<string> IntegrateAsync(RunSession s, WorkflowNode node, ProjectBrief brief, CancellationToken ct)
    {
        var (model, effort) = ModelFor(s, node);
        if (brief.IsExisting)
        {
            await RunAgentAsync(s, AgentPrompts.ExistingIntegrator(brief, model, effort) with { NodeId = node.Id, Name = node.Title },
                "Verify the changes in every repository now, following your instructions step by step.", ct);
            return "Built and tested (.etw/docs/integration-report.md)";
        }

        await RunAgentAsync(s, AgentPrompts.Integrator(brief, model, effort) with { NodeId = node.Id, Name = node.Title },
            "Integrate and verify the application now, following your instructions step by step.", ct);
        return "Built, tested and documented (docs/integration-report.md)";
    }

    private async Task<string> CustomAsync(RunSession s, WorkflowNode node, ProjectBrief brief, CancellationToken ct)
    {
        var instruction = node.Get("prompt");
        if (instruction.Length == 0) throw new StageFailedException($"'{node.Title}' has no instruction yet. Open the step and describe the task.");
        var (model, effort) = ModelFor(s, node);
        await RunAgentAsync(s, AgentPrompts.Custom(node.Id, node.Title, instruction, brief, model, effort), instruction, ct);
        return "Done";
    }

    private async Task<string> NotifyAsync(RunSession s, WorkflowNode node, List<WorkflowNode> chain, CancellationToken ct)
    {
        var channel = node.Kind == NodeKind.NotifySlack ? NotifyChannel.Slack : NotifyChannel.Teams;
        var isLast = chain.SkipWhile(n => n.Id != node.Id).Skip(1).All(n => !n.Enabled);
        var anyFailed = s.Results.Any(r => r.Outcome == StageOutcome.Failed);

        var title = $"{s.Request.ProjectName}: {(isLast ? "build finished" : "progress update")}";
        var message = BuildSummary(s);

        if (s.Simulate)
        {
            s.Events.Emit(RunEventKind.Notification, $"[Simulation] Would post to {channel}: {title}", node.Id, channel.ToString());
            return $"Simulated {channel} message";
        }

        var result = await notifier.SendAsync(channel, s.Request.RunId, title, message, anyFailed ? NotifyLevel.Warning : NotifyLevel.Success, ct);
        if (result.Delivered.Count > 0)
        {
            s.Events.Emit(RunEventKind.Notification, $"Message posted to {channel}.", node.Id, channel.ToString());
            return $"Posted to {channel}";
        }

        // A notification problem is reported but never fails the run.
        var why = string.Join("; ", result.Failed);
        s.Events.Emit(RunEventKind.Info, $"Could not post to {channel}: {why}", node.Id);
        return $"Not delivered ({why})";
    }

    private async Task<string> DeployAsync(RunSession s, WorkflowNode node, ProjectBrief brief, CancellationToken ct)
    {
        if (brief.IsExisting)
        {
            throw new StageFailedException("Existing repositories are not deployed from here: their own Azure DevOps pipelines build and deploy them once the pull requests are merged. Turn this step off.");
        }

        var prefix = node.Get("appNamePrefix").Trim().ToLowerInvariant();
        var subscription = node.Get("subscriptionId").Trim();
        var resourceGroup = node.Get("resourceGroup").Trim();
        var location = node.Get("location", "westeurope").Trim();

        if (!AppPrefix().IsMatch(prefix)) throw new StageFailedException("Set an app name prefix (3–40 lowercase letters, digits or hyphens) on the Azure deploy step.");
        if (subscription.Length == 0 || resourceGroup.Length == 0) throw new StageFailedException("Choose an Azure subscription and resource group on the Azure deploy step.");

        if (s.Simulate)
        {
            foreach (var line in new[] { $"[Simulation] Resource group {resourceGroup} ready", $"[Simulation] Deployed backend → https://{prefix}-backend.azurewebsites.net", $"[Simulation] Deployed frontend → https://{prefix}-frontend.azurewebsites.net" })
            {
                s.Events.Emit(RunEventKind.Deployment, line, node.Id, "Azure");
            }
            return "Simulated deployment";
        }

        var (model, effort) = ModelFor(s, node);
        await RunAgentAsync(s, AgentPrompts.Release(brief, prefix, model, effort) with { NodeId = node.Id, Name = node.Title },
            "Prepare the deployment artifacts and the manifest now.", ct);

        var result = await deployer.DeployAsync(new DeployRequest
        {
            RunId = s.Request.RunId,
            TargetFolder = s.Request.TargetFolder,
            SubscriptionId = subscription,
            ResourceGroup = resourceGroup,
            Location = location,
            AppNamePrefix = prefix,
            PlanSku = node.Get("planSku", "B1"),
        }, ct);

        foreach (var line in result.Log) s.Events.Emit(RunEventKind.Deployment, line, node.Id, "Azure");
        if (!result.Succeeded) throw new StageFailedException(result.Error ?? "The deployment failed.");
        return "Deployed — " + string.Join(", ", result.Urls.Select(kv => $"{kv.Key}: {kv.Value}"));
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    /// <summary>Runs one agent to completion and returns its text. Throws when the agent failed.</summary>
    private async Task<string> RunAgentAsync(RunSession s, AgentSpec spec, string task, CancellationToken ct)
    {
        var agent = factory.Create(s, spec, out var reporter);
        var response = await agent.RunAsync(task, cancellationToken: ct);
        if (reporter.Outcome == AgentOutcome.Failed) throw new StageFailedException($"{spec.Name} failed: {reporter.Error}");
        return response.Text;
    }

    /// <summary>Model/effort for an agent: its own node, then the agent step's "Claude" node, then the global setting.</summary>
    private static (string? Model, string? Effort) ModelFor(RunSession s, WorkflowNode agentStep, WorkflowNode? member = null)
    {
        var modelNode = s.Request.Workflow.ChildrenOf(agentStep.Id).FirstOrDefault(c => c.Kind == NodeKind.Model && c.Enabled);
        string? Pick(string key, string global) =>
            new[] { member?.Get(key), modelNode?.Get(key), global }.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        return (Pick("model", s.Request.Settings.Model), Pick("effort", s.Request.Settings.Effort));
    }

    private static void PrepareFolder(RunSession s, ProjectBrief brief)
    {
        if (s.Simulate) return;
        var root = s.Request.TargetFolder;
        if (brief.IsExisting)
        {
            // The repositories stay clean: planning documents live next to them, in the workspace's .etw folder.
            Directory.CreateDirectory(Path.Combine(root, ".etw", "docs"));
            File.WriteAllText(Path.Combine(root, ".etw", "docs", "user-stories.md"), brief.StoriesMarkdown(), new UTF8Encoding(false));
            return;
        }

        foreach (var sub in new[] { "docs", "contracts", ".etw" }) Directory.CreateDirectory(Path.Combine(root, sub));
        File.WriteAllText(Path.Combine(root, "docs", "user-stories.md"), brief.StoriesMarkdown(), new UTF8Encoding(false));
    }

    private static void SaveClarifications(RunSession s, List<ClarifyingQuestion> answers)
    {
        if (s.Simulate) return;
        var sb = new StringBuilder("# Clarifications from the product owner\n\n");
        foreach (var a in answers) sb.AppendLine($"- **{a.Text}**\n  {(string.IsNullOrWhiteSpace(a.Answer) ? "_No answer given — use your best judgement._" : a.Answer)}");
        var docs = s.Request.Mode == ProjectMode.ExistingRepos ? Path.Combine(s.Request.TargetFolder, ".etw", "docs") : Path.Combine(s.Request.TargetFolder, "docs");
        File.WriteAllText(Path.Combine(docs, "clarifications.md"), sb.ToString(), new UTF8Encoding(false));
    }

    // ---- Existing repositories ----------------------------------------------------------------------------------

    /// <summary>Clones (or refreshes) every repository of the project and starts this run's branch in each. Runs before any agent.</summary>
    private async Task PrepareReposAsync(RunSession s, CancellationToken ct)
    {
        var request = s.Request;
        var branch = BranchFor(request);

        if (s.Simulate)
        {
            foreach (var repo in request.Repos) s.Events.Emit(RunEventKind.Info, $"[Simulation] {repo.Name}: cloned into {repo.Folder} and branch {branch} created from {repo.DefaultBranch}");
            return;
        }

        s.Events.Emit(RunEventKind.Info, $"Preparing {request.Repos.Count} repositor{(request.Repos.Count == 1 ? "y" : "ies")} (clone or update, then branch {branch})…");
        var prepared = await Task.WhenAll(request.Repos.Select(async repo =>
        {
            var result = await repos.PrepareAsync(new GitPrepareRequest
            {
                Organization = request.AzureDevOpsOrganization,
                Project = request.AzureDevOpsProject,
                RepoName = repo.Name,
                Workspace = request.TargetFolder,
                Folder = repo.Folder,
                BaseBranch = repo.DefaultBranch,
                Branch = branch,
            }, ct);
            return (repo, result);
        }));

        foreach (var (repo, result) in prepared)
        {
            s.Prepared[repo.Id] = result;
            s.Events.Emit(RunEventKind.Info, $"{repo.Name}: {(result.Cloned ? "cloned" : "updated")} · working on branch {result.Branch} from {result.BaseBranch}");
        }
    }

    /// <summary>Commits what the agents changed in each repository, pushes the run's branch and opens a pull request into the repository's default branch.</summary>
    private async Task<string> PublishAsync(RunSession s, CancellationToken ct)
    {
        var request = s.Request;
        if (s.Simulate)
        {
            foreach (var repo in request.Repos) s.Events.Emit(RunEventKind.Info, $"[Simulation] {repo.Name}: would open a pull request into {repo.DefaultBranch}");
            return "simulated pull requests";
        }

        var workItemIds = request.Stories.Select(x => int.TryParse(x.ExternalId, out var id) ? id : 0).Where(id => id > 0).ToList();
        var title = request.Stories.Count == 1 ? request.Stories[0].Title : $"{request.ProjectName}: {request.Stories.Count} user stories";
        var description = PullRequestDescription(s);

        var outcomes = await Task.WhenAll(request.Repos.Select(async repo =>
        {
            try
            {
                var prepared = s.Prepared[repo.Id];
                var result = await repos.PublishAsync(new GitPublishRequest
                {
                    Organization = request.AzureDevOpsOrganization,
                    Project = request.AzureDevOpsProject,
                    RepoId = repo.Id,
                    RepoName = repo.Name,
                    Workspace = request.TargetFolder,
                    Folder = repo.Folder,
                    BaseBranch = prepared.BaseBranch,
                    Branch = prepared.Branch,
                    CommitMessage = title + "\n\n" + description,
                    PullRequestTitle = title,
                    PullRequestDescription = description,
                    WorkItemIds = workItemIds,
                }, ct);
                return (repo, result, error: (string?)null);
            }
            catch (StageFailedException ex)
            {
                return (repo, result: (GitPublishResult?)null, error: ex.Message);
            }
        }));

        var delivered = new List<string>();
        var problems = new List<string>();
        foreach (var (repo, result, error) in outcomes)
        {
            if (error is not null)
            {
                problems.Add($"{repo.Name}: {error}");
                s.Events.Emit(RunEventKind.Info, $"{repo.Name}: could not publish — {error}");
            }
            else if (result is { Changed: true })
            {
                delivered.Add($"{repo.Name} #{result.PullRequestId}");
                s.Events.Emit(RunEventKind.Info, $"{repo.Name}: pull request #{result.PullRequestId} opened ({result.FilesChanged} file{(result.FilesChanged == 1 ? "" : "s")} changed) → {result.PullRequestUrl}");
            }
            else
            {
                s.Events.Emit(RunEventKind.Info, $"{repo.Name}: no changes, so no pull request");
            }
        }

        if (problems.Count > 0) throw new StageFailedException((delivered.Count > 0 ? $"Pull requests opened for {string.Join(", ", delivered)}, but: " : "") + string.Join(" | ", problems));
        return delivered.Count > 0 ? "pull requests: " + string.Join(", ", delivered) : "no repository needed changes";
    }

    /// <summary>A readable, unique branch name: etw/&lt;run&gt;-&lt;first story&gt;.</summary>
    private static string BranchFor(RunRequest request)
    {
        var slug = new string(request.Stories.FirstOrDefault()?.Title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray() ?? []).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 30) slug = slug[..30].Trim('-');
        return $"etw/{request.RunId.ToString("N")[..8]}" + (slug.Length > 0 ? "-" + slug : "");
    }

    private static string PullRequestDescription(RunSession s)
    {
        var sb = new StringBuilder("Implemented by Entire Team Workflow (AI agents). Please review carefully before merging.\n\nUser stories:\n");
        foreach (var story in s.Request.Stories.Take(30)) sb.AppendLine($"- {ProjectBrief.Label(story)} {story.Title}");
        sb.AppendLine().AppendLine($"Run {s.Request.RunId.ToString()[..8]}");
        return sb.ToString();
    }

    private async Task NotifyFailureAsync(RunSession s, string error)
    {
        if (s.Simulate) return;
        foreach (var node in s.Request.Workflow.Chain().Where(n => n.Enabled && n.Kind is NodeKind.NotifySlack or NodeKind.NotifyTeams))
        {
            var channel = node.Kind == NodeKind.NotifySlack ? NotifyChannel.Slack : NotifyChannel.Teams;
            var result = await notifier.SendAsync(channel, s.Request.RunId, $"{s.Request.ProjectName}: build failed", BuildSummary(s) + "\n\n" + error, NotifyLevel.Error, CancellationToken.None);
            if (result.Delivered.Count > 0) s.Events.Emit(RunEventKind.Notification, $"Failure reported to {channel}.", node.Id, channel.ToString());
        }
    }

    public static string BuildSummary(RunSession s)
    {
        var lines = s.Results.Select(r => r.Outcome switch
        {
            StageOutcome.Succeeded => $"✅ {r.Title} — {r.Detail}",
            StageOutcome.Failed => $"❌ {r.Title} — {r.Detail}",
            _ => $"⏭ {r.Title} — {r.Detail}",
        });
        return string.Join("\n", lines);
    }

    /// <summary>Reads the analyst's JSON reply tolerantly (models sometimes wrap it in text or a code fence).</summary>
    public static List<ClarifyingQuestion> ParseQuestions(string reply)
    {
        var start = reply.IndexOf('[');
        var end = reply.LastIndexOf(']');
        if (start < 0 || end <= start) return [];
        try
        {
            var items = JsonSerializer.Deserialize<List<ClarifyingQuestion>>(reply[start..(end + 1)], EtwJson.Options) ?? [];
            return items.Where(q => !string.IsNullOrWhiteSpace(q.Text))
                .Take(5)
                .Select((q, i) => new ClarifyingQuestion { Id = string.IsNullOrWhiteSpace(q.Id) ? $"q{i + 1}" : q.Id, Text = q.Text.Trim() })
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex AppPrefix();
}
