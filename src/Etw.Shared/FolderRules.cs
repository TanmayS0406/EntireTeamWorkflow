namespace Etw.Shared;

public static class FolderRules
{
    /// <summary>Agents get write access to the target folder, so keep it away from system locations and drive roots.</summary>
    public static string? Validate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a target folder for the project first.";
        if (!Path.IsPathFullyQualified(path)) return "The target folder must be a full path (for example C:\\Projects\\MyApp).";

        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (full.Length <= 3) return "Do not use a drive root as the target folder.";

        var blocked = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var root in blocked.Where(b => b.Length > 0))
        {
            var trimmed = root.TrimEnd('\\');
            if (full.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return $"Use a sub-folder, not {root} itself.";
            }
            // The user's profile root is only blocked as a whole: sub-folders (Documents, Desktop\Projects) are fine.
            if (!root.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase)
                && full.StartsWith(trimmed + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return $"The target folder must not be inside {root}.";
            }
        }
        return null;
    }
}
