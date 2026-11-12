using System.Text.Json;
using Etw.Shared;
using Etw.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Data;

public sealed class SettingRow
{
    public string Key { get; set; } = "";
    public string Json { get; set; } = "";
}

/// <summary>SQLite store. The wire DTOs double as the persisted rows, so there is no mapping layer to keep in sync.</summary>
public sealed class EtwDb(DbContextOptions<EtwDb> options) : DbContext(options)
{
    public DbSet<ProjectDto> Projects => Set<ProjectDto>();
    public DbSet<WorkItemDto> WorkItems => Set<WorkItemDto>();
    public DbSet<RunDto> Runs => Set<RunDto>();
    public DbSet<RunEventDto> RunEvents => Set<RunEventDto>();
    public DbSet<SettingRow> Settings => Set<SettingRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ProjectDto>(e =>
        {
            e.ToTable("Projects");
            e.HasKey(p => p.Id);
            e.Property(p => p.Workflow).HasConversion(
                v => JsonSerializer.Serialize(v, EtwJson.Options),
                v => JsonSerializer.Deserialize<WorkflowDefinition>(v, EtwJson.Options) ?? new WorkflowDefinition());
            e.Property(p => p.Repos).HasConversion(
                v => JsonSerializer.Serialize(v, EtwJson.Options),
                v => JsonSerializer.Deserialize<List<RepoRefDto>>(v, EtwJson.Options) ?? new List<RepoRefDto>());
        });

        b.Entity<WorkItemDto>(e =>
        {
            e.ToTable("WorkItems");
            e.HasKey(w => w.Id);
            e.Property(w => w.Source).HasConversion<string>();
            e.HasIndex(w => w.ProjectId);
            e.HasIndex(w => new { w.ProjectId, w.ExternalId });
        });

        b.Entity<RunDto>(e =>
        {
            e.ToTable("Runs");
            e.HasKey(r => r.Id);
            e.Property(r => r.Status).HasConversion<string>();
            e.Property(r => r.PendingQuestions).HasConversion(
                v => JsonSerializer.Serialize(v, EtwJson.Options),
                v => JsonSerializer.Deserialize<List<ClarifyingQuestion>>(v, EtwJson.Options) ?? new List<ClarifyingQuestion>());
            e.HasIndex(r => r.ProjectId);
        });

        b.Entity<RunEventDto>(e =>
        {
            e.ToTable("RunEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Kind).HasConversion<string>();
            e.HasIndex(x => new { x.RunId, x.Id });
        });

        b.Entity<SettingRow>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
        });
    }
}
