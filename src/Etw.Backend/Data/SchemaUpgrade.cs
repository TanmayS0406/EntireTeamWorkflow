using Microsoft.EntityFrameworkCore;

namespace Etw.Backend.Data;

/// <summary>
/// <c>EnsureCreated</c> only creates a database that does not exist yet; it never adds columns to an older one. So that
/// an existing installation keeps its projects when the model grows, the missing columns are added here.
/// </summary>
public static class SchemaUpgrade
{
    private static readonly (string Name, string Definition)[] ProjectColumns =
    [
        ("Mode", "INTEGER NOT NULL DEFAULT 0"),
        ("AzureDevOpsOrganization", "TEXT NOT NULL DEFAULT ''"),
        ("AzureDevOpsProject", "TEXT NOT NULL DEFAULT ''"),
        ("Repos", "TEXT NOT NULL DEFAULT '[]'"),
    ];

    public static async Task RunAsync(EtwDb db)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await db.Database.OpenConnectionAsync();
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA table_info('Projects')";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) existing.Add(reader.GetString(1));
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        foreach (var (name, definition) in ProjectColumns.Where(c => !existing.Contains(c.Name)))
        {
            var sql = "ALTER TABLE Projects ADD COLUMN " + name + " " + definition;
            await db.Database.ExecuteSqlRawAsync(sql);
        }
    }
}
