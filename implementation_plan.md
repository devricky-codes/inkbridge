# Goal Description

Build "Inkbridge", a two-part application system comprising:
1. An **Android Jetpack Compose Tablet App** handling text input (via ML Kit Ink Recognition) and canvas mirror/drawing overlay.
2. A **Windows C# .NET 8 Background Service** that discovers clients, relays keyboard input via various text-injection tiers, and mirrors bounded screen captures.

## User Review Required

- For the **Windows Service**, we'll create a generic .NET 8 Host application (similar to a Worker Service) but run it as a standard Windows exectuable with a WinForms message loop behind the scenes solely for `Hardcodet.NotifyIcon` and `SetWinEventHook` thread-affinity requirements.
- For **mDNS** on Windows, `Makaretu.Dns` is standard but mostly unmaintained, so leveraging an updated equivalent (e.g., `Tmds.MDns`) or basic UDP Multicast depends on platform reliability. I plan to use `Tmds.MDns` or `Makaretu.Dns` based on compatibility.
- Ensure the requested path `f:\Projects\TabletEasyWrite` has the expected permissions for initializing both `Inkbridge.Windows` and `Inkbridge.Android` subprojects side-by-side.

Please approve this plan and these specific library considerations before I generate the code.

## Proposed Changes

### Windows Background Service (C# .NET 8)
- `Program.cs`: Setup Generic Host with a UI thread context for the NotifyIcon.
- `WebSocketServer.cs`: Implements JSON WebSocket handling and binary payload streaming for drawing inputs/canvas frames.
- `Services/FocusTracker.cs`: Uses `SetWinEventHook` (`EVENT_OBJECT_FOCUS`, `EVENT_SYSTEM_FOREGROUND`) to subscribe to active window controls.
- `Services/TextInjector.cs`: Layered priority: `IUIAutomation`, `TextPattern`, `SendInput`, then fallback Clipboard integration. 
- `Services/ScreenCapturer.cs`: Ties into SharpDX / Desktop Duplication API to capture the active window bounds and compress frames to 70% JPEG at up to 60fps.
- `Services/PointerInjector.cs`: Implements Windows Pointer API `InjectStylusInput` to replay X,Y coordinates back onto the Windows surface.

### Android Jetpack Compose App (Kotlin)
- `MainActivity.kt`: Setup UI content + minimal tabs.
- `NetworkManager.kt`: Uses `NsdManager` to discover `_inkbridge._tcp` port 8765.
- `WebSocketClient.kt`: Connects OkHttp WebSocket passing binary and text frames.
- `ui/theme/Theme.kt`: CRED-like aesthetic (Black #0A0A0A, DM Sans typography, no chrome).
- `ui/TextInjectMode.kt`: Full-height TextField with "Start writing", capturing ML Kit Ink inputs if stylus used.
- `ui/CanvasMirrorMode.kt`: Overlay `SurfaceView` rendering JPEGs on the background, tracking `MotionEvent` pointers with `TOOL_TYPE_STYLUS` to reconstruct `x`, `y`, `pressure`, `phase` and dispatch packets 21 bytes each.

## Verification Plan
### Automated Tests
- Integration tests simulating the injection endpoints.
### Manual Verification
- Testing mDNS auto-discovery between Android and PC over LAN.
- Text Input: Verify injection into target apps (Visual Studio Code, Chrome, etc).
- Pen Input: Verify streaming JPEG canvas from Windows accurately reflects on Android, and Android stylus events synthesize Windows pointer inputs correctly.
