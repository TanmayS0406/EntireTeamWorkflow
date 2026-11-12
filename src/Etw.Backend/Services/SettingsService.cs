using System.Text.Json;
using System.Text.RegularExpressions;
using Etw.Backend.Data;
using Etw.Shared;
using Etw.Shared.Models;
using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Services;

public sealed partial class SettingsService(IDbContextFactory<EtwDb> dbFactory)
{
    private const string Key = "app";

    public async Task<AppSettingsDto> GetAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var row = await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == Key, ct);
        return row is null ? new AppSettingsDto() : JsonSerializer.Deserialize<AppSettingsDto>(row.Json, EtwJson.Options) ?? new AppSettingsDto();
    }

    public async Task<(AppSettingsDto? Settings, string? Error)> SaveAsync(AppSettingsDto settings, CancellationToken ct = default)
    {
        settings.ClaudePath = (settings.ClaudePath ?? "").Trim().Trim('"');
        settings.Model = (settings.Model ?? "").Trim();
        settings.Effort = (settings.Effort ?? "").Trim().ToLowerInvariant();
        settings.DefaultWorkspaceRoot = (settings.DefaultWorkspaceRoot ?? "").Trim().Trim('"');

        if (settings.ClaudePath.Length > 0 && !File.Exists(settings.ClaudePath)) return (null, "The Claude CLI path does not exist.");
        if (!SafeToken().IsMatch(settings.Model)) return (null, "The model name contains characters that are not allowed.");
        if (settings.Effort is not ("" or "low" or "medium" or "high" or "xhigh" or "max")) return (null, "Effort must be low, medium, high, xhigh or max.");
        if (settings.PermissionMode is not ("acceptEdits" or "auto")) return (null, "Permission mode must be acceptEdits or auto.");

        settings.MaxParallelAgents = Math.Clamp(settings.MaxParallelAgents, 1, 8);
        settings.AgentTimeoutMinutes = Math.Clamp(settings.AgentTimeoutMinutes, 5, 240);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var json = JsonSerializer.Serialize(settings, EtwJson.Options);
        var row = await db.Settings.FirstOrDefaultAsync(s => s.Key == Key, ct);
        if (row is null) db.Settings.Add(new SettingRow { Key = Key, Json = json });
        else row.Json = json;
        await db.SaveChangesAsync(ct);
        return (settings, null);
    }

    [GeneratedRegex(@"^[A-Za-z0-9._\-\[\]]*$")]
    private static partial Regex SafeToken();
}
