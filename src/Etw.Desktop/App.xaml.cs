using System.IO;
using System.Windows;
using Etw.Desktop.UI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Etw.Desktop;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show(args.Exception.Message, "Entire Team Workflow", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddSingleton<IShell, WpfShell>();
        services.AddSingleton<GatewayClient>();
        services.AddSingleton<ClaudeLoginService>();
        services.AddSingleton<AppState>();
        _services = services.BuildServiceProvider();

        new MainWindow(_services).Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static void Log(Exception ex)
    {
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EntireTeamWorkflow");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "desktop.log"), $"{DateTime.Now:s} {ex}{Environment.NewLine}");
        }
        catch (IOException)
        {
            // Logging must not make a crash worse.
        }
    }
}
