# GimmeCapture!!

The one tool to snip, record, translate, and pin your screen with style.

---

## Languages

- [Traditional Chinese](README.zh-TW.md)
- [Japanese](README.ja.md)

---

## Overview

**GimmeCapture!!** is a cross-platform desktop capture tool built with **Avalonia** on **.NET 10**,
running on **Windows** and **Linux (X11)**. It combines fast snipping, screen recording,
OCR-assisted translation, floating pin windows, in-process video compression, and local AI tools
(OCR, background removal, smart selection, translation) in one app.

The project name is a tribute to **BABYMETAL** and the song **["Gimme chocolate!!"](https://www.youtube.com/watch?v=WIKqgE4BwAY)**. The visual style follows that same bold, high-contrast energy.

## Highlights

- **Snip mode**: Capture a region quickly, then copy, save, annotate, or pin it.
- **Record mode**: Record your screen with system + microphone audio, webcam picture-in-picture, a
  keystroke overlay, and live toolbar controls.
- **Scrolling capture**: Stitch a long page or chat into one tall image while you scroll.
- **Translate mode**: OCR-assisted region selection with local translation overlays.
- **Compress tab**: Re-encode any video smaller (H.264/H.265) with a batch queue and an advanced editor.
- **Pin windows**: Keep images and videos on top as floating reference windows.
- **AI tools**: Background removal, smart object/region selection, and OCR — all local, downloaded on demand.
- **Annotation tools**: Boxes, arrows, lines, text, mosaic/blur redaction, highlighter, callouts, and step markers.
- **Custom hotkeys** and **visual customization** (theme colors, border thickness, mask opacity, decorations).
- **In-app auto-update**: Downloads and installs new releases without leaving the app.

## Platform support

GimmeCapture runs on **Windows 10/11** and **Linux (X11)**.

- **Windows** — full feature set, including **per-window GPU capture** (Windows Graphics Capture),
  a native system-tray, and an optional installer.
- **Linux (X11)** — snip/capture, recording (screen via x11grab + **system/mic audio via
  PulseAudio** + **webcam PiP via V4L2**), translation, pin playback, compress, scrolling capture,
  and in-app auto-update all work. **Per-window (WGC) recording has no Linux equivalent and is
  hidden** — the capture-scope picker keeps monitor selection. Global hotkeys use X11 (they work
  under XWayland; native Wayland global hotkeys are not yet supported).

## Recording

Recording mode supports:

- Screen recording with desktop/system audio and optional **microphone** mixing
- **Webcam picture-in-picture** (corner, size, and rectangular/circular shape)
- **Keystroke overlay** and cursor/click visualization
- Multiple export formats: **MP4, MKV, GIF, WebM, MOV** (high-quality palette-based GIF)
- **Capture-scope picker** — record a specific monitor (both platforms) or window (Windows/WGC)
- Live recording toolbar, audio-level feedback, mute toggle, and a global stop hotkey

## Scrolling capture

Capture content taller than the screen — a long web page, document, or chat log:

- Select a region, then scroll the target by hand while frames are captured and stitched
- Automatic vertical/horizontal direction detection (or lock a direction)
- The capture region outline stays out of the stitched image and passes your scrolling through

## Translation Mode

Translation mode is designed for fast OCR-to-translation workflows on desktop UI, documents, comics, and screenshots.

- Select one or more regions manually, or use OCR-assisted quick selection.
- The OCR helper appears while holding the configured selection modifier.
- Translate results stay as movable, resizable overlay boxes.
- OCR/original text can be toggled separately for inspection.
- Translation runs locally through **LlamaSharp (GGUF)**.

### Curated local translation models

The current user-facing local translation model list is:

- **TranslateGemma 4B**: primary recommended model
- **Gemma 3 4B**: stable general fallback

## Compress

Import any video and re-encode it smaller, entirely in-process (libav):

- **H.264 / H.265**, **compress-to-target-size** (true two-pass for H.264), resolution downscale,
  frame-rate cap, encoder preset, CRF, and audio bitrate / mono-stereo mixdown or drop-audio.
- **Batch queue** with parallel encoding, per-item settings + presets, and a live output-size estimate.
- **Advanced video editor**: trim / speed / crop / rotate plus annotations, redaction, and
  freeze-frame, with an inline side-by-side quality compare.

## AI Modules

AI resources are downloaded on demand from the **Modules** tab.

Current module groups include:

- **ONNX Runtime & U2Net**: background removal runtime and model
- **SAM2 Model**: smart object/region selection
- **PaddleOCR v5**: OCR detection and recognition
- **Llama Models**: local GGUF translation models

## Pin Windows

Pinned windows are meant for quick reference while you work.

- Pin captured images as floating windows
- Pin video clips with playback controls (audio included)
- Keep windows on top while preserving lightweight interaction

## Updates

- The app checks for new releases and can **download and install updates in place**, then relaunch.
- On Windows the update is a portable `.zip`; on Linux it is a self-contained single-file `.tar.gz`.
- Downloads are verified against the release `SHA256SUMS.txt` before being applied.

## Notes

- This project is actively iterated, so translation and AI workflows may evolve across releases.
- The Modules tab is the expected place to install or update optional AI resources.

## Third-Party Components

- **FFmpeg / FFmpeg.AutoGen**: media recording, playback, probing, and processing (x11grab / PulseAudio / V4L2 / dshow / gdigrab)
- **NAudio**: system audio capture on Windows (WASAPI)
- **ONNX Runtime**: local AI inference runtime
- **PaddleOCR**: OCR pipeline
- **LlamaSharp**: local GGUF inference for translation
- **SkiaSharp**: imaging, annotation, and compositing

## Requirements

- **Windows 10 / Windows 11**, or
- **Linux with an X11 session** (x86-64)

## License

Released under the [GNU General Public License v3.0](LICENSE).

GimmeCapture links against and bundles **FFmpeg** built with `--enable-gpl` — including the
GPL-licensed **x264** and **x265** encoders it uses for H.264/H.265 — so the application as
distributed is a combined work licensed under the GPL. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party component licenses and
corresponding-source information.
