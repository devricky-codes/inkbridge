# 🌉 Inkbridge

**Transform your Android Tablet into a high-performance PC drawing & note-taking powerhouse!**

Inkbridge connects your Android tablet seamlessly to your Windows PC over local WebSockets, letting you sketch out brilliant ideas, create infinite brain maps, and annotate exactly what's currently on your PC screen in real-time. Forget expensive dedicated drawing tablets or disjointed note-taking platforms—Inkbridge instantly unifies the touch capabilities of your tablet with the processing flexibility of your PC workspace!

## ✨ Why Inkbridge?
Whether you're brainstorming complex systems, taking rapid class notes, or highlighting critical document structures, Inkbridge provides the ultimate interactive canvas:
* **Infinite Generative Brain Maps:** Unrestricted panning constraints, smooth multi-touch zooming, and borderless canvas areas allow your mind to expand without hitting the edge of a page.
* **Zero-Latency Network Workflow:** Through local port connections, your brilliant hand gestures transfer natively, without lag or stutter, visualizing instantly on your primary Windows monitor.
* **Streamlined Productivity:** Instead of awkwardly importing/exporting images, drop references directly onto the PC canvas so your tablet can sketch directly over them simultaneously! 

## 🚀 Key Features

* 📝 **Cross-Platform Whiteboard:** Draw, manipulate lines, and erase continuously. A robust WebSocket core handles flawless 1-to-1 sync tracing.
* 🖥️ **Interactive Overlay Mode:** Seamlessly stream live captures of your Windows screen straight into the background of your tablet! Draw overlays, draft ideas across live application UI elements, and automatically export composites straight into your Windows Clipboard.
* 📄 **Fast Document Mode:** A dedicated UI that enables swift multi-page serialization! Auto-save sequential canvases as natively-associated `.inkboard` files directly into targeted Windows directories.
* 🖼️ **Rich Media Handling:** Easily drag-and-drop Desktop files, URLs, or execute `Ctrl+V` to immediately inject PC-side context into your tablet stream. 
* 🎨 **Clean, Extensible UI:** Packed inside a polished, minimalist Windows package with rounded buttons, hovering aesthetics, and dynamic Hex Background Color-Pickers!

## ⚙️ Getting Started
1. **Launch on PC:** Run the `Inkbridge.Windows.exe` application. It operates silently in your Windows System Tray. (Right-click the icon to manually configure modes).
2. **Launch on Android:** Open the `Inkbridge` application. Enter your PC's Local IP Address (`192.168.x.x`) configured against port `8765`. 
3. Select either **Whiteboard Tab** to begin drafting, or **Overlay Tab** to interact dynamically atop your PC's operating system!

## 🖱️ PC Shortcuts & Controls
* Double-clicking *any* `.inkboard` file will automatically boot the application and process the whiteboard!
* **Ctrl + Scroll:** Scale & Zoom the Whiteboard seamlessly.
* **Alt + Scroll:** Lock-on Scale for exclusively transforming active shapes or embedded images.
* **Drag-and-Drop:** Drag images seamlessly across the application.
* **Ctrl + V:** Paste Clipboard content.

---
*Built with .NET 8 WPF, Android Jetpack Compose, & WebSocket Protocol Architecture.*
