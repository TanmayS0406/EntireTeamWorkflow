using System.IO;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Etw.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        InitializeComponent();

        WebView.BlazorWebViewInitializing += (_, e) =>
        {
            // WebView2 keeps its profile here instead of next to the exe (which may be read-only, e.g. Program Files).
            e.UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EntireTeamWorkflow", "WebView2");

#if DEBUG
            // Debug builds only: ETW_DEVTOOLS_PORT exposes the page over the DevTools protocol on localhost (UI automation, screenshots).
            if (int.TryParse(Environment.GetEnvironmentVariable("ETW_DEVTOOLS_PORT"), out var port) && port is > 1024 and < 65536)
            {
                e.EnvironmentOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = $"--remote-debugging-port={port}" };
            }
#endif
        };

        // Links to anything outside the app open in the user's browser: that is BlazorWebView's default, so nothing to add here.
        WebView.Services = services;
    }
}
