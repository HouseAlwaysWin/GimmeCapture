# GimmeCapture!!

The one tool to snip, record, translate, and pin your screen with style.

---

## Languages

- [Traditional Chinese](README.zh-TW.md)
- [Japanese](README.ja.md)

---

## Overview

**GimmeCapture!!** is a Windows desktop capture tool built with **Avalonia**. It combines fast snipping, screen recording, OCR-assisted translation, floating pin windows, and lightweight annotation tools in one app.

The project name is a tribute to **BABYMETAL** and the song **["Gimme chocolate!!"](https://www.youtube.com/watch?v=WIKqgE4BwAY)**. The visual style follows that same bold, high-contrast energy.

## Highlights

- **Snip mode**: Capture a region quickly, then copy, save, annotate, or pin it.
- **Record mode**: Record your screen with system audio and live toolbar controls.
- **Translate mode**: Use OCR-assisted region selection and local translation overlays.
- **Pin windows**: Keep images and videos on top as floating reference windows.
- **Annotation tools**: Draw boxes, arrows, lines, and text on captures.
- **Custom hotkeys**: Configure shortcuts from the Control tab.
- **Visual customization**: Adjust theme colors, border thickness, mask opacity, and decoration visibility.

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
- Pin video clips with playback controls
- Keep windows on top while preserving lightweight interaction

## Recording

Recording mode supports:

- Screen recording with desktop/system audio
- Multiple export formats such as MP4, MKV, GIF, WebM, and MOV
- Live recording toolbar controls
- Audio level feedback during recording

## Notes

- This project is actively iterated, so translation and AI workflows may evolve across releases.
- The Modules tab is the expected place to install or update optional AI resources.

## Third-Party Components

- **FFmpeg / FFmpeg.AutoGen**: media recording, playback, probing, and processing
- **NAudio**: system audio capture and monitoring
- **ONNX Runtime**: local AI inference runtime
- **PaddleOCR**: OCR pipeline
- **LlamaSharp**: local GGUF inference for translation

## Requirements

- Windows 10 or Windows 11

## License

Released under the [MIT License](LICENSE).

