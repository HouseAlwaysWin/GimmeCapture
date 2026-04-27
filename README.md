# GimmeCapture!! 🦊

The One Tool to Snip & Record. A sleek, high-performance desktop utility built with Avalonia for capturing your screen with style.

---

## 🌐 Other Languages / 其他語言 / 他の言語

- [繁體中文 (Traditional Chinese)](README.zh-TW.md)
- [日本語 (Japanese)](README.ja.md)

---

## 🇺🇸 English

### 🎸 Design Inspiration
The name **GimmeCapture!!** is a tribute to the song **["Gimme chocolate!!" (Official Video)](https://www.youtube.com/watch?v=WIKqgE4BwAY)** by **BABYMETAL**. Just like the song, this tool aims to be fast, energetic, and heavy on style. The UI colors and theme are inspired by the iconic BABYMETAL aesthetic.

### Features
- **Smart Snip**: High-performance screen capture with instant editing tools.
- **Screen Recording + System Audio**: Record screen video with desktop/system audio into MP4, MKV, GIF, and more.
- **Live Translation Mode**: OCR + translation workflow with language selection, drag-region selection, and translation result overlays.
- **Pin to Top**: Pin your snips as floating windows for easy reference.
- **Pinned Video Player**: Play/pause, loop, seek, speed control (0.5x/1.0x/1.5x/2.0x), and audio mute toggle.
- **Editing Tools**: Draw boxes, arrows, lines, and text directly on your capture.
- **Customizable Hotkeys**: All shortcuts are fully customizable in the "Control" tab.
- **Visual Personalization**: Adjustable border thickness, mask opacity, and theme colors (Gold, Silver, Red).
- **Decoration Scaling**: Customize the size of **Side Wings (0.5x - 3.0x)** and **Corner Icons (0.4x - 1.0x)** to fit your style.
- **Auto-start**: Option to launch automatically when Windows starts.
- **Live Audio Metering**: Recording toolbar shows real-time input/output audio levels and dB state.

### How to Use
1. Launch the app and switch between the three modes on the toolbar: **Snip / Record / Translate**.
2. In Snip mode, capture an area, annotate it, then copy, save, or pin it as a floating window.
3. In Record mode, start/pause/stop recording and monitor live input/output audio levels from the toolbar.
4. In Translate mode, choose source/target languages, drag to select regions, then run translate or OCR scan.
5. Use right-click on pinned windows to access context actions; pinned videos support playback, speed, and audio controls.

### Translation Mode Notes
- Switch to **Translation Mode** from the toolbar language/translate icon.
- Select source and target languages, then drag to select one or more text regions.
- The selection hold modifier is configurable in **Settings > Hotkeys** (`Shift` / `Ctrl` / `Alt` / `None`), default is `Ctrl`.
- Use **Translate All** to process all selections, or **Scan All** for OCR-only detection.
- Translation overlays can be toggled on/off from the translation toolbar.

### Recording / Pinned Video Notes
- Enable or disable system audio capture in **Settings > Record**.
- In pinned video mode, audio is **muted by default**.
- In pinned video mode, press **Shift + M** to toggle mute/unmute audio.
- Video speed changes also affect audio playback speed in pinned video mode.

### 📦 Third-party Components
- **FFmpeg**: Used for screen recording and multimedia processing. FFmpeg is licensed under the [GPL/LGPL](https://ffmpeg.org/legal.html). Screen capture uses **libav*** DLLs via [FFmpeg.AutoGen](https://www.nuget.org/packages/FFmpeg.AutoGen); finalize/transcode/preview still invoke the bundled `ffmpeg.exe` / `ffprobe.exe` / `ffplay.exe` shipped next to those DLLs under `ffmpeg-lib/`. Populate that folder before release builds by running `powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1` (downloads the [BtbN FFmpeg-Builds](https://github.com/BtbN/FFmpeg-Builds) **win64-gpl-shared** archive and extracts binaries).
- **NAudio**: Used for system audio loopback capture during recording and real-time audio level monitoring.

---

## 🛠️ Requirements
- Windows 10/11
- [.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## ⚖️ License
Released under the [MIT License](LICENSE). Created by HouseAlwaysWin.
