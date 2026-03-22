using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using Microsoft.Extensions.Logging;

namespace Inkbridge.Windows.Services;

public class TextInjector
{
    private readonly ILogger<TextInjector> _logger;

    public TextInjector(ILogger<TextInjector> logger)
    {
        _logger = logger;
    }

    public async Task<string> InjectTextAsync(string text)
    {
        return await Task.Run(() =>
        {
            try
            {
                var element = AutomationElement.FocusedElement;
                if (element != null)
                {
                    // 1. ValuePattern
                    if ((bool)element.GetCurrentPropertyValue(AutomationElement.IsValuePatternAvailableProperty))
                    {
                        var valuePattern = element.GetCurrentPattern(ValuePattern.Pattern) as ValuePattern;
                        if (valuePattern != null && !valuePattern.Current.IsReadOnly)
                        {
                            valuePattern.SetValue(text);
                            _logger.LogInformation("Injected text using ValuePattern.");
                            return "valuePattern";
                        }
                    }

                    // 2. TextPattern
                    if ((bool)element.GetCurrentPropertyValue(AutomationElement.IsTextPatternAvailableProperty))
                    {
                        var textPattern = element.GetCurrentPattern(TextPattern.Pattern) as TextPattern;
                        if (textPattern != null && textPattern.SupportedTextSelection != SupportedTextSelection.None)
                        {
                            InjectViaClipboard(text);
                            return "textPattern";
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Automation injection failed: {ex.Message}");
            }

            // 3/4. Fallback SendInput vs Clipboard
            if (text.Length > 250)
            {
                InjectViaClipboard(text);
                return "clipboard";
            }
            else
            {
                InjectViaSendInput(text);
                return "sendInput";
            }
        });
    }

    private void InjectViaSendInput(string text)
    {
        var inputs = new INPUT[text.Length * 2];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            
            inputs[i * 2].type = INPUT_KEYBOARD;
            inputs[i * 2].u.ki.wVk = 0;
            inputs[i * 2].u.ki.wScan = (ushort)c;
            inputs[i * 2].u.ki.dwFlags = KEYEVENTF_UNICODE;
            inputs[i * 2].u.ki.time = 0;
            inputs[i * 2].u.ki.dwExtraInfo = IntPtr.Zero;

            inputs[i * 2 + 1].type = INPUT_KEYBOARD;
            inputs[i * 2 + 1].u.ki.wVk = 0;
            inputs[i * 2 + 1].u.ki.wScan = (ushort)c;
            inputs[i * 2 + 1].u.ki.dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP;
            inputs[i * 2 + 1].u.ki.time = 0;
            inputs[i * 2 + 1].u.ki.dwExtraInfo = IntPtr.Zero;
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    }

    private void InjectViaClipboard(string text)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            string oldText = null;
            try { if (System.Windows.Clipboard.ContainsText()) oldText = System.Windows.Clipboard.GetText(); } catch { }

            System.Windows.Clipboard.SetText(text);

            PressKey(VK_CONTROL, false);
            PressKey(VK_V, false);
            PressKey(VK_V, true);
            PressKey(VK_CONTROL, true);

            Task.Delay(50).ContinueWith(_ =>
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    if (oldText != null)
                        System.Windows.Clipboard.SetText(oldText);
                    else
                        System.Windows.Clipboard.Clear();
                });
            });
        });
    }

    private void PressKey(ushort vk, bool keyUp)
    {
        var input = new INPUT();
        input.type = INPUT_KEYBOARD;
        input.u.ki.wVk = vk;
        input.u.ki.wScan = 0;
        input.u.ki.dwFlags = keyUp ? KEYEVENTF_KEYUP : 0;
        input.u.ki.time = 0;
        input.u.ki.dwExtraInfo = IntPtr.Zero;

        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
    }

    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_Z = 0x5A;

    public void Undo()
    {
        PressKey(VK_CONTROL, false);
        PressKey(VK_Z, false);
        PressKey(VK_Z, true);
        PressKey(VK_CONTROL, true);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public int type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)]
        public KEYBDINPUT ki;
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
