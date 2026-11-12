using System.Text.RegularExpressions;
using Etw.Backend.Data;
using Etw.Backend.Services;
using Etw.Shared;
using Etw.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Endpoints;

public static partial class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/languages", () => new { backends = LanguageCatalog.Backends, uiFrameworks = LanguageCatalog.UiFrameworks });

        // ---- Projects -----------------------------------------------------------------------------------------
        api.MapGet("/projects", async (IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.Projects.AsNoTracking().ToListAsync(ct)).OrderByDescending(p => p.UpdatedUtc).ToList();
        });

        api.MapPost("/projects", async (SaveProjectRequest request, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            var error = ValidateProject(request);
            if (error is not null) return Results.BadRequest(new { error });

            var project = new ProjectDto
            {
                Id = Guid.NewGuid(),
                Name = request.Name.Trim(),
                TargetFolder = Path.GetFullPath(request.TargetFolder.Trim().Trim('"')),
                BackendLanguage = request.BackendLanguage,
                UiFramework = request.UiFramework,
                Mode = request.Mode,
                AzureDevOpsOrganization = request.AzureDevOpsOrganization.Trim(),
                AzureDevOpsProject = request.AzureDevOpsProject.Trim(),
                Repos = NormalizedRepos(request),
                Workflow = WorkflowDefinition.Default(),
            };
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Projects.Add(project);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/projects/{project.Id}", project);
        });

        api.MapGet("/projects/{id:guid}", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return project is null ? Results.NotFound() : Results.Ok(project);
        });

        api.MapPut("/projects/{id:guid}", async (Guid id, SaveProjectRequest request, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            var error = ValidateProject(request);
            if (error is not null) return Results.BadRequest(new { error });

            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null) return Results.NotFound();

            project.Name = request.Name.Trim();
            project.TargetFolder = Path.GetFullPath(request.TargetFolder.Trim().Trim('"'));
            project.BackendLanguage = request.BackendLanguage;
            project.UiFramework = request.UiFramework;
            project.Mode = request.Mode;
            project.AzureDevOpsOrganization = request.AzureDevOpsOrganization.Trim();
            project.AzureDevOpsProject = request.AzureDevOpsProject.Trim();
            project.Repos = NormalizedRepos(request); // a new list instance, so the change is detected
            project.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(project);
        });

        api.MapDelete("/projects/{id:guid}", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null) return Results.NotFound();

            var runIds = await db.Runs.Where(r => r.ProjectId == id).Select(r => r.Id).ToListAsync(ct);
            await db.RunEvents.Where(e => runIds.Contains(e.RunId)).ExecuteDeleteAsync(ct);
            await db.Runs.Where(r => r.ProjectId == id).ExecuteDeleteAsync(ct);
            await db.WorkItems.Where(w => w.ProjectId == id).ExecuteDeleteAsync(ct);
            db.Projects.Remove(project);
            await db.SaveChangesAsync(ct);
            return Results.NoContent(); // The generated code in the target folder is never touched.
        });

        // ---- Workflow -----------------------------------------------------------------------------------------
        api.MapGet("/projects/{id:guid}/workflow", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            return project is null ? Results.NotFound() : Results.Ok(project.Workflow);
        });

        api.MapPut("/projects/{id:guid}/workflow", async (Guid id, WorkflowDefinition workflow, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            var error = WorkflowValidator.Validate(workflow);
            if (error is not null) return Results.BadRequest(new { error });

            await using var db = await dbf.CreateDbContextAsync(ct);
            var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct);
            if (project is null) return Results.NotFound();
            project.Workflow = workflow;
            project.UpdatedUtc = DateTime.UtcNow;
            db.Entry(project).Property(p => p.Workflow).IsModified = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(project.Workflow);
        });

        // ---- Work items ---------------------------------------------------------------------------------------
        api.MapGet("/projects/{id:guid}/workitems", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return await db.WorkItems.AsNoTracking().Where(w => w.ProjectId == id).OrderBy(w => w.Priority ?? 99).ThenBy(w => w.UpdatedUtc).ToListAsync(ct);
        });

        api.MapPost("/projects/{id:guid}/workitems", async (Guid id, CreateWorkItemRequest request, IDbContextFactory<EtwDb> dbf, IntegrationsClient integrations, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title)) return Results.BadRequest(new { error = "A story needs a title." });
            await using var db = await dbf.CreateDbContextAsync(ct);
            var owner = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (owner is null) return Results.NotFound();

            var item = new WorkItemDto
            {
                Id = Guid.NewGuid(),
                ProjectId = id,
                Source = WorkItemSource.Local,
                Type = string.IsNullOrWhiteSpace(request.Type) ? "User Story" : request.Type.Trim(),
                Title = request.Title.Trim(),
                Description = request.Description?.Trim() ?? "",
                AcceptanceCriteria = request.AcceptanceCriteria?.Trim() ?? "",
                Priority = request.Priority,
                StoryPoints = request.StoryPoints,
                State = "New",
                UpdatedUtc = DateTime.UtcNow,
            };

            string? warning = null;
            if (request.PushToAzureDevOps)
            {
                var (created, error) = await integrations.CreateWorkItemAsync(new AdoCreateWorkItemRequest
                {
                    Organization = owner.AzureDevOpsOrganization,
                    Project = owner.AzureDevOpsProject,
                    Type = item.Type,
                    Title = item.Title,
                    Description = item.Description,
                    AcceptanceCriteria = item.AcceptanceCriteria,
                    Priority = item.Priority,
                    StoryPoints = item.StoryPoints,
                }, ct);
                if (created is null) return Results.Json(new { error = "Azure DevOps: " + error }, statusCode: 502);

                item.Source = WorkItemSource.AzureDevOps;
                item.ExternalId = created.ExternalId;
                item.ExternalUrl = created.ExternalUrl;
                item.State = created.State;
                item.IterationPath = created.IterationPath;
                item.AreaPath = created.AreaPath;
            }

            db.WorkItems.Add(item);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/workitems/{item.Id}", new { item, warning });
        });

        api.MapPut("/workitems/{id:guid}", async (Guid id, WorkItemDto update, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(update.Title)) return Results.BadRequest(new { error = "A story needs a title." });
            await using var db = await dbf.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
            if (item is null) return Results.NotFound();

            item.Title = update.Title.Trim();
            item.Description = update.Description ?? "";
            item.AcceptanceCriteria = update.AcceptanceCriteria ?? "";
            item.Type = string.IsNullOrWhiteSpace(update.Type) ? item.Type : update.Type;
            item.Priority = update.Priority;
            item.StoryPoints = update.StoryPoints;
            item.Selected = update.Selected;
            item.UpdatedUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(item);
        });

        api.MapDelete("/workitems/{id:guid}", async (Guid id, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var removed = await db.WorkItems.Where(w => w.Id == id).ExecuteDeleteAsync(ct);
            return removed == 0 ? Results.NotFound() : Results.NoContent();
        });

        api.MapPost("/projects/{id:guid}/workitems/select", async (Guid id, bool value, IDbContextFactory<EtwDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            await db.WorkItems.Where(w => w.ProjectId == id).ExecuteUpdateAsync(s => s.SetProperty(w => w.Selected, value), ct);
            return Results.NoContent();
        });

        api.MapPost("/projects/{id:guid}/workitems/import-azuredevops", async (Guid id, bool? includeClosed, IDbContextFactory<EtwDb> dbf, IntegrationsClient integrations, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var owner = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
            if (owner is null) return Results.NotFound();

            // A project linked to its own Azure DevOps project imports from that one; otherwise the Connect tab's default is used.
            var (fetched, error) = await integrations.FetchBacklogAsync(owner.AzureDevOpsOrganization, owner.AzureDevOpsProject, includeClosed ?? false, ct);
            if (fetched is null) return Results.Json(new { error }, statusCode: 502);

            var existing = await db.WorkItems.Where(w => w.ProjectId == id && w.Source == WorkItemSource.AzureDevOps).ToListAsync(ct);
            var byExternal = existing.Where(e => e.ExternalId is not null).ToDictionary(e => e.ExternalId!);
            var seen = new HashSet<string>();
            int added = 0, updated = 0;

            foreach (var incoming in fetched.Where(f => f.ExternalId is not null))
            {
                seen.Add(incoming.ExternalId!);
                if (byExternal.TryGetValue(incoming.ExternalId!, out var row))
                {
                    var selected = row.Selected; // keep the user's choice across syncs
                    CopyFields(incoming, row);
                    row.Selected = selected;
                    updated++;
                }
                else
                {
                    incoming.Id = Guid.NewGuid();
                    incoming.ProjectId = id;
                    incoming.Source = WorkItemSource.AzureDevOps;
                    incoming.Selected = IsStory(incoming.Type);
                    db.WorkItems.Add(incoming);
                    added++;
                }
            }

            var removed = existing.Where(e => e.ExternalId is null || !seen.Contains(e.ExternalId)).ToList();
            db.WorkItems.RemoveRange(removed);
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { added, updated, removed = removed.Count });
        });
    }

    private static bool IsStory(string type) => type is "User Story" or "Product Backlog Item" or "Requirement";

    private static void CopyFields(WorkItemDto from, WorkItemDto to)
    {
        to.ExternalUrl = from.ExternalUrl;
        to.ParentExternalId = from.ParentExternalId;
        to.Type = from.Type;
        to.Title = from.Title;
        to.Description = from.Description;
        to.AcceptanceCriteria = from.AcceptanceCriteria;
        to.State = from.State;
        to.IterationPath = from.IterationPath;
        to.AreaPath = from.AreaPath;
        to.Priority = from.Priority;
        to.StoryPoints = from.StoryPoints;
        to.UpdatedUtc = DateTime.UtcNow;
    }

    private static string? ValidateProject(SaveProjectRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 80) return "Enter a project name (up to 80 characters).";
        var folderError = FolderRules.Validate(request.TargetFolder?.Trim().Trim('"'));
        if (folderError is not null) return folderError;
        if (LanguageCatalog.Backends.All(b => b.Id != request.BackendLanguage)) return "Unknown backend language.";
        if (LanguageCatalog.UiFrameworks.All(u => u.Id != request.UiFramework)) return "Unknown UI framework.";
        return request.Mode == ProjectMode.ExistingRepos ? ValidateRepos(request) : null;
    }

    private static string? ValidateRepos(SaveProjectRequest request)
    {
        if (!OrgName().IsMatch(request.AzureDevOpsOrganization.Trim())) return "Choose the Azure DevOps organization the repositories are in.";
        var project = request.AzureDevOpsProject.Trim();
        if (project.Length is 0 or > 100) return "Choose the Azure DevOps project the repositories are in.";
        if (request.Repos.Count is 0 or > 20) return "Choose between 1 and 20 repositories.";

        var repos = NormalizedRepos(request);
        foreach (var repo in repos)
        {
            if (!Guid.TryParse(repo.Id, out _) || repo.Name.Length is 0 or > 100) return "A repository entry is invalid. Pick the repositories again.";
            if (!FolderName().IsMatch(repo.Folder)) return $"'{repo.Folder}' cannot be used as a folder name for {repo.Name}.";
            if (!BranchName().IsMatch(repo.DefaultBranch) || repo.DefaultBranch.Contains("..")) return $"'{repo.DefaultBranch}' is not a valid branch name for {repo.Name}.";
        }
        if (repos.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != repos.Count) return "A repository was chosen twice.";
        if (repos.Select(r => r.Folder).Distinct(StringComparer.OrdinalIgnoreCase).Count() != repos.Count) return "Two repositories would be cloned into the same folder.";
        return null;
    }

    /// <summary>The repositories as they are stored: trimmed, with a folder name (the repository name, made safe) and a default branch.</summary>
    private static List<RepoRefDto> NormalizedRepos(SaveProjectRequest request) =>
        request.Mode != ProjectMode.ExistingRepos
            ? []
            : request.Repos.Select(r => new RepoRefDto
            {
                Id = r.Id.Trim(),
                Name = r.Name.Trim(),
                DefaultBranch = string.IsNullOrWhiteSpace(r.DefaultBranch) ? "main" : r.DefaultBranch.Trim(),
                Folder = string.IsNullOrWhiteSpace(r.Folder) ? SafeFolder(r.Name) : r.Folder.Trim(),
            }).ToList();

    private static string SafeFolder(string name)
    {
        var slug = new string(name.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-').ToArray()).Trim('-', '.');
        return slug.Length == 0 ? "repo" : slug.Length > 80 ? slug[..80] : slug;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9\-_.]{0,63}$")]
    private static partial Regex OrgName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$")]
    private static partial Regex FolderName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/\-]{0,99}$")]
    private static partial Regex BranchName();
}
