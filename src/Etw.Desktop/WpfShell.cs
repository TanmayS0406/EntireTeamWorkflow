using System.Diagnostics;
using System.IO;
using Etw.Desktop.UI.Services;
using Microsoft.Win32;

namespace Etw.Desktop;

/// <summary>The window-bound services the Blazor UI cannot do for itself.</summary>
public sealed class WpfShell : IShell
{
    public Task<string?> PickFolderAsync(string? initialFolder)
    {
        var dialog = new OpenFolderDialog { Title = "Choose the project folder" };
        if (!string.IsNullOrWhiteSpace(initialFolder) && Directory.Exists(initialFolder)) dialog.InitialDirectory = initialFolder;
        return Task.FromResult(dialog.ShowDialog() == true ? dialog.FolderName : null);
    }

    public void OpenUrl(string url)
    {
        // Only web links: this is also used for the OAuth sign-in page, and must never start arbitrary programs.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    public void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }
}
