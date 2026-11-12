namespace Etw.Desktop.UI.Services;

/// <summary>What the UI needs from the window that hosts it. Implemented by the WPF app.</summary>
public interface IShell
{
    /// <summary>Shows the folder picker. Returns null when cancelled.</summary>
    Task<string?> PickFolderAsync(string? initialFolder);

    void OpenUrl(string url);

    void OpenFolder(string path);
}
