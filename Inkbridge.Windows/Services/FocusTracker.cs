using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inkbridge.Windows.Services;

public class FocusTracker : BackgroundService
{
    private readonly ILogger<FocusTracker> _logger;
    private readonly NetworkService _network;
    private IntPtr _hook;
    private WinEventDelegate _hookDelegate;

    private const uint EVENT_OBJECT_FOCUS = 0x8005;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    public FocusTracker(ILogger<FocusTracker> logger, NetworkService network)
    {
        _logger = logger;
        _network = network;
        _hookDelegate = new WinEventDelegate(WinEventProc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // To receive events, we must have a message loop or run on a thread that has one.
        // For WPF, the BackgroundService runs independently, but we can set the hook here 
        // as long as we pump messages, or we just rely on the main WPF dispatcher.
        // It's safer to post it to the WPF main thread so hook notifications fire correctly on a UI thread.

        var tcs = new TaskCompletionSource();
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _hook = SetWinEventHook(EVENT_OBJECT_FOCUS, EVENT_OBJECT_FOCUS, IntPtr.Zero, _hookDelegate, 0, 0, WINEVENT_OUTOFCONTEXT);
            _logger.LogInformation("Focus hook installed.");
        });

        using (stoppingToken.Register(() => {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                if (_hook != IntPtr.Zero)
                {
                    UnhookWinEvent(_hook);
                    _hook = IntPtr.Zero;
                    _logger.LogInformation("Focus hook removed.");
                }
            });
            tcs.TrySetResult();
        }))
        {
            await tcs.Task;
        }
    }

    private void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (eventType == EVENT_OBJECT_FOCUS)
        {
            _ = Task.Run(() => AnalyzeFocus(hwnd));
        }
    }

    private async Task AnalyzeFocus(IntPtr hwnd)
    {
        try
        {
            AutomationElement element = AutomationElement.FocusedElement;
            if (element == null) return;

            string processName = "Unknown";
            string windowTitle = "Unknown";
            try
            {
                var process = Process.GetProcessById(element.Current.ProcessId);
                processName = process.ProcessName;
                windowTitle = process.MainWindowTitle;
            }
            catch { }

            string controlType = element.Current.ControlType.ProgrammaticName.Replace("ControlType.", "");
            string method = "sendInput"; // Default fallback

            // Layered strategy estimation
            if ((bool)element.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty))
            {
                method = "valuePattern";
            }
            else if ((bool)element.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty))
            {
                method = "textPattern";
            }
            else
            {
                // Some elements are document type with no patterns (e.g. contenteditable in browsers)
                if (controlType.Contains("Document") || controlType.Contains("Edit"))
                {
                    method = "clipboard"; 
                }
            }

            var evt = new
            {
                type = "focus",
                app = processName,
                window = windowTitle,
                control = controlType,
                method = method
            };

            var json = JsonSerializer.Serialize(evt);
            _logger.LogInformation($"Focus changed: {json}");
            await _network.BroadcastJsonAsync(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to analyze focus: {ex.Message}");
        }
    }

    delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    static extern bool UnhookWinEvent(IntPtr hWinEventHook);
}
