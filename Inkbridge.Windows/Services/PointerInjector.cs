using System;
using System.Runtime.InteropServices;
using Windows.UI.Input.Preview.Injection;
using Microsoft.Extensions.Logging;
using System.Drawing;

namespace Inkbridge.Windows.Services;

public class PointerInjector
{
    private readonly ILogger<PointerInjector> _logger;
    private InputInjector _inputInjector;

    public PointerInjector(ILogger<PointerInjector> logger)
    {
        _logger = logger;
        try
        {
            _inputInjector = InputInjector.TryCreate();
            if (_inputInjector == null)
            {
                _logger.LogWarning("Failed to initialize InputInjector. Pen replay will not work.");
            }
            else
            {
                _inputInjector.InitializePenInjection(InjectedInputVisualizationMode.Default);
                _logger.LogInformation("Pen injection initialized.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PointerInjector init error");
        }
    }

    public void InjectStroke(byte phase, float normX, float normY, float pressure, long timestamp)
    {
        if (_inputInjector == null) return;

        try
        {
            var hwnd = GetForegroundWindow();
            RECT rect = new RECT { Left=0, Top=0, Right=1920, Bottom=1080 }; // Fallback
            if (hwnd != IntPtr.Zero)
            {
                GetWindowRect(hwnd, out rect);
            }

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;

            int screenX = rect.Left + (int)Math.Round(width * normX);
            int screenY = rect.Top + (int)Math.Round(height * normY);

            var penInfo = new InjectedInputPenInfo();
            penInfo.PointerInfo = new InjectedInputPointerInfo
            {
                PointerId = 1,
                PixelLocation = new InjectedInputPoint { PositionX = screenX, PositionY = screenY },
                TimeOffsetInMilliseconds = 0
            };

            penInfo.PenParameters = InjectedInputPenParameters.Pressure;
            penInfo.Pressure = pressure;

            var options = InjectedInputPointerOptions.InRange;

            if (phase == 0) // Down
            {
                options |= InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.PointerDown;
            }
            else if (phase == 1) // Move
            {
                options |= InjectedInputPointerOptions.InContact | InjectedInputPointerOptions.Update;
            }
            else if (phase == 2) // Up
            {
                options |= InjectedInputPointerOptions.PointerUp;
            }

            var info = penInfo.PointerInfo;
            info.PointerOptions = options;
            penInfo.PointerInfo = info;

            _inputInjector.InjectPenInput(penInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to inject pen stroke: {ex.Message}");
        }
    }

    [DllImport("user32.dll")]
    static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
