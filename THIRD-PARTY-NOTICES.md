# Third-Party Notices

GimmeCapture (the "Program") is licensed under the **GNU General Public License, version 3**
(see [LICENSE](LICENSE)). The Program combines and distributes the third-party components
listed below. The GPL applies to the Program as a whole; each component remains under its own
license, all of which are compatible with GPLv3.

## Why GimmeCapture is GPL

GimmeCapture bundles and links against **FFmpeg** compiled with `--enable-gpl`, which includes
the GPL-licensed **x264** and **x265** encoders used for H.264/H.265 recording and compression.
Because those components are under the GPL, the combined work — GimmeCapture as distributed —
must be, and is, released under the GPL.

## Native media components (copyleft — the reason for the GPL)

| Component | License | Source |
|---|---|---|
| **FFmpeg** (libavcodec/format/util/…), built with `--enable-gpl` | GPL-2.0-or-later (the GPL build; FFmpeg's own code is LGPL-2.1-or-later) | https://ffmpeg.org/ · build: https://github.com/BtbN/FFmpeg-Builds |
| **x264** (libx264) | GPL-2.0-or-later (or commercial) | https://www.videolan.org/developers/x264.html |
| **x265** (libx265) | GPL-2.0-or-later (or commercial) | https://www.videolan.org/developers/x265.html |

The exact FFmpeg build is BtbN's `ffmpeg-n8.1-latest-win64-gpl-shared-8.1` (avcodec-62 /
avformat-62 / avutil-60 ABI). It is **not** committed to this repository — it is fetched at build
time by [`scripts/ensure-ffmpeg-libs.ps1`](scripts/ensure-ffmpeg-libs.ps1) and placed in
`src/GimmeCapture/ffmpeg-lib/`.

### Corresponding source (GPLv3 §6 / GPLv2 §3)

The complete corresponding source for FFmpeg, x264, and x265 is freely available from the
upstream projects linked above and from the exact build tree published at
https://github.com/BtbN/FFmpeg-Builds. No GPL component is modified by this project; the
unmodified shared libraries are redistributed as built by BtbN.

## Managed FFmpeg bindings

| Component | License | Source |
|---|---|---|
| **FFmpeg.AutoGen** (P/Invoke bindings) | LGPL-3.0 | https://github.com/Ruslan-B/FFmpeg.AutoGen |

## Permissively licensed dependencies (GPLv3-compatible)

These NuGet packages are under permissive licenses (MIT / Apache-2.0), which are compatible with
GPLv3:

| Package | License |
|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Controls.ColorPicker, Avalonia.Fonts.Inter | MIT |
| ReactiveUI.Avalonia | MIT |
| CliWrap | MIT |
| NAudio | MIT |
| SkiaSharp | MIT |
| LLamaSharp, LLamaSharp.Backend.Cpu | MIT |
| Microsoft.ML.Tokenizers | MIT |
| Microsoft.ML.OnnxRuntime.DirectML | MIT |
| ZLinq | MIT |
| Serilog, Serilog.Sinks.File | Apache-2.0 |

## Bundled fonts

| Font | License |
|---|---|
| Cinzel | SIL Open Font License 1.1 |
| Noto Serif TC | SIL Open Font License 1.1 |
| Noto Serif JP | SIL Open Font License 1.1 |
| Inter | SIL Open Font License 1.1 |

## AI models

Local AI features (OCR, background removal, smart selection, translation) use models that are
**downloaded on demand** from the Modules tab and are **not** bundled with the Program. Each model
is subject to its own license as presented at download time.
