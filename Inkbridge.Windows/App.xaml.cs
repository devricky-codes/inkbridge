using System.Windows;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Controls;
using System;
using System.IO;

namespace Inkbridge.Windows;

public partial class App : System.Windows.Application
{
    private IHost _host;
    private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon _notifyIcon;
    private WhiteboardWindow? _whiteboardWindow;

    public App()
    {
        // Log unhandled exceptions to file so crashes are visible
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            File.AppendAllText("inkbridge-crash.log", $"[{DateTime.Now}] {e.ExceptionObject}\n");
        DispatcherUnhandledException += (s, e) =>
        {
            File.AppendAllText("inkbridge-crash.log", $"[{DateTime.Now}] {e.Exception}\n");
            e.Handled = true;
        };

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices((context, services) =>
            {
                // Register NetworkService as a singleton so it can be injected by concrete type
                // by FocusTracker, then wire it as a hosted service too.
                services.AddSingleton<Inkbridge.Windows.Services.NetworkService>();
                services.AddHostedService(sp => sp.GetRequiredService<Inkbridge.Windows.Services.NetworkService>());

                services.AddSingleton<Inkbridge.Windows.Services.TextInjector>();
                services.AddSingleton<Inkbridge.Windows.Services.PointerInjector>();

                services.AddHostedService<Inkbridge.Windows.Services.FocusTracker>();
            })
            .Build();
    }

    public static string[] StartupArgs { get; private set; }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var processes = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName);
            foreach (var process in processes)
            {
                if (process.Id != currentProcess.Id)
                {
                    try { process.Kill(); } catch { }
                }
            }

            StartupArgs = e.Args;

            var exePath = currentProcess.MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
            {
                using var extKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\.inkboard");
                extKey.SetValue("", "Inkbridge.Whiteboard");

                using var progKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Inkbridge.Whiteboard");
                progKey.SetValue("", "Inkbridge Whiteboard File");

                using var cmdKey = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\Inkbridge.Whiteboard\shell\open\command");
                cmdKey.SetValue("", $"\"{exePath}\" \"%1\"");
            }

            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            await _host.StartAsync();

            _notifyIcon = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon();
            var icoPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            _notifyIcon.Icon = System.IO.File.Exists(icoPath)
                ? new System.Drawing.Icon(icoPath)
                : System.Drawing.SystemIcons.Application;
            _notifyIcon.ToolTipText = "Inkbridge — running on port 8765";

            var contextMenu = new ContextMenu();

            var whiteboardMenuItem = new MenuItem { Header = "Open Whiteboard" };
            whiteboardMenuItem.Click += (s, args) =>
            {
                OpenWhiteboard();
            };
            contextMenu.Items.Add(whiteboardMenuItem);

            var overlayMenuItem = new MenuItem { Header = "Open Overlay" };
            overlayMenuItem.Click += (s, args) =>
            {
                var networkService = _host.Services.GetRequiredService<Inkbridge.Windows.Services.NetworkService>();
                var overlayWindow = new OverlayWindow(networkService);
                overlayWindow.Show();
                overlayWindow.Activate();
            };
            contextMenu.Items.Add(overlayMenuItem);

            var exitMenuItem = new MenuItem { Header = "Exit Inkbridge" };
            exitMenuItem.Click += async (s, args) =>
            {
                await _host.StopAsync();
                _notifyIcon.Dispose();
                Current.Shutdown();
            };
            contextMenu.Items.Add(exitMenuItem);
            _notifyIcon.ContextMenu = contextMenu;

            if (StartupArgs.Length > 0 && File.Exists(StartupArgs[0]))
            {
                OpenWhiteboard(StartupArgs[0]);
            }
        }
        catch (Exception ex)
        {
            File.AppendAllText("inkbridge-crash.log", $"[{DateTime.Now}] OnStartup: {ex}\n");
            System.Windows.MessageBox.Show($"Inkbridge failed to start:\n\n{ex.Message}", "Inkbridge Error",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        using (_host)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
        }
        _notifyIcon?.Dispose();
        base.OnExit(e);
    }

    private void OpenWhiteboard(string? fileToLoad = null)
    {
        if (_whiteboardWindow == null || !_whiteboardWindow.IsLoaded)
        {
            var networkService = _host.Services.GetRequiredService<Inkbridge.Windows.Services.NetworkService>();
            _whiteboardWindow = new WhiteboardWindow(networkService);
            Action<string> wbHandler = msg =>
            {
                _whiteboardWindow.Dispatcher.BeginInvoke(() => _whiteboardWindow.HandleWhiteboardMessage(msg));
            };
            networkService.OnWhiteboardMessage += wbHandler;
            _whiteboardWindow.Closed += (s2, e2) =>
            {
                networkService.OnWhiteboardMessage -= wbHandler;
                _whiteboardWindow = null;
            };
        }
        _whiteboardWindow.Show();
        _whiteboardWindow.Activate();

        if (!string.IsNullOrEmpty(fileToLoad))
        {
            _ = _whiteboardWindow.LoadWhiteboardFromFileAsync(fileToLoad);
        }
    }
}
