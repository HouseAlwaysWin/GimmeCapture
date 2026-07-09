# Release Log / 更新日誌 / リリースノート

## Licensing — 2026-07-03

- **Relicensed from MIT to GPLv3.** GimmeCapture bundles and links against FFmpeg built with
  `--enable-gpl` — including the GPL-licensed **x264**/**x265** encoders used for H.264/H.265 —
  so the application as distributed is a combined work that must be under the GPL. See
  [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Unreleased

### ✂️ Video editing

- **還原預設 (Reset all)** — a new button in the advanced video editor reverts every edit — trim segments, per-piece
  speed, crop, rotation, annotations, and redaction — back to defaults in one click (with a confirm prompt).
- **Double-click to keep/drop a segment** — on the editor and pin timeline strips, toggling a piece's keep/drop
  state now takes a **double-click**; a single click only scrubs, so seeking no longer flips segments by accident.

### 🐛 Fixed

- **Shift+F4 OCR copy no longer freezes the app on long text.** The recognized text was written to the clipboard
  synchronously on the UI thread; a large payload racing the Windows clipboard-history listeners could wedge that
  OLE call and hang the whole app. The write now runs on a dedicated STA thread bounded by a timeout, so the
  clipboard can never block the UI thread — and a copy that still fails reports a distinct status instead of a
  false "copied".

## v0.62.0 - 2026-07-08

### 🎬 Compress

- **Video filters** — HandBrake-style **denoise** (hqdn3d / NLMeans), **sharpen** (unsharp), **deblock**, and
  **grayscale**, on the Output-settings tab. Applied in-process via a libavfilter graph and folded into every encode
  path, the size estimate, and the quality compare; denoise also helps the output compress smaller.
- **Output categories** — create category subfolders inside the active working directory (**新增分類**), then sort
  each queued file's output into one via a per-row dropdown (defaults to the global output folder / next to source).
- **Open output folder** — a per-row button (shown once a file finishes) reveals the produced file in Explorer.
- **GIF and WebM output** — the Video-Editing (Compress) tab can now output **GIF** and **WebM** in addition to
  MP4/MKV/MOV, matching Pin mode. All edits (trim/crop/rotate/filters/annotations) are baked into an intermediate
  render first, then transcoded, so the GIF/WebM reflects the full edit.

### 📌 Pin

- **Pick the export format from the pin toolbar** — image pins output **PNG / JPG / WebP** and video pins output
  **MP4 / MKV / MOV / GIF / WebM**, chosen from a dropdown on the pin itself. A video pin's format **defaults to
  the main-menu recording format** (so it stays in sync) and can be changed per-pin. The choice applies to **both
  Save and Copy**. Video container changes stream-copy losslessly where possible (falling back to a re-encode when
  the codecs can't be remuxed), and saving a transparent pin as JPG composites it over white.

### ⏺️ Recording

- **GIF recordings keep their audio for pinning** — recording with the format set to GIF now still captures audio.
  A pinned GIF recording plays sound, and re-exporting it to a non-GIF format keeps the audio (only the saved
  `.gif` itself is silent, since GIF has no audio track).

## v0.60.0 - 2026-07-07

### 🗜️ Smaller video compression

- **AV1 (SVT-AV1) codec in the compress pipeline** — the most efficient codec, for the smallest
  files at the same perceived quality (in testing ~65% smaller than H.264 and noticeably smaller
  than H.265 at matching quality). Encoded as **10-bit** (smaller and banding-free even from 8-bit
  sources) using the AV1 encoder already bundled in the app's FFmpeg build — no new dependencies.
  A new **「最小體積 (AV1)」** quick preset applies it at a slow preset / full resolution. AV1 is
  offline-compress-only (not offered for realtime recording, where it would be impractically slow).
- **B-frames enabled** on the offline compress/export path (they were previously disabled) — this
  shrinks output at the same quality for **H.264 and H.265 too**, not just AV1.
- One CRF slider still drives every codec: AV1's different CRF scale is handled transparently, so a
  given CRF means comparable quality across H.264 / H.265 / AV1.

### ✂️ Snip overlay

- **Toolbar position setting** — the region-selection toolbar can now be pinned **top-left / top-center /
  top-right** (Settings → Snip) instead of always top-center. It positions on the active monitor and persists.
- **Two-stage Esc in box-select** — after you've drawn a selection box, the first Esc now **clears the box and
  returns to the ready-to-select state** (auto-detect, hover-to-select, and a fresh OCR scan) rather than
  closing; a second Esc, with no box, closes the overlay.
- **OCR auto-scan reliability** — the text-detection scan now fires correctly on entry and after an Esc-clear
  (it was being skipped while a previous scan's CPU-bound OCR was still finishing).

### 🌐 Translation

- **Pick the custom GGUF model with a button** — the local-model area gained a picker/browse button so you can
  select a downloaded model (or a `.gguf` file directly) instead of typing the path.

### 🧠 Memory

- **Idle-unload of heavy AI resources** — the translation LLM (multi-GB), background-removal (U2Net), and OCR
  models now release themselves when unused instead of staying resident for the whole session; the next use
  transparently reloads them. One-shot resources (OCR / background removal) reclaim within seconds.
- **Broader working-set trimming** — memory is reclaimed after a capture / recording / compress / edit and once
  the app settles at startup, not only when minimizing to tray.
- **Bitmap-leak fixes** — the quality-compare window's decoded frames, compress-queue thumbnails, and the
  smart-selection mask are now released promptly, and a dispose-while-rendered editor crash was fixed.

## v0.52.0 - 2026-07-05

Documentation update — no application changes since v0.51.0 (the shipped binaries are identical).

### 📝 Docs
- **Refreshed the Traditional-Chinese and Japanese READMEs** to match the English one. They were
  stale (Windows-only) and now cover Linux, scrolling capture, compress, AI modules,
  webcam/keystroke recording, and in-app auto-update.
- **Documented Linux support**: developed and tested on **Linux Mint** (Ubuntu-based); needs an
  x86-64 glibc distro with an X11 session and PulseAudio (e.g. Ubuntu 22.04+ / Debian 12+; XWayland
  on Wayland desktops; not musl/Alpine or ARM).

## v0.51.0 - 2026-07-05

**GimmeCapture now runs on Linux (X11)** — the whole capture/record/translate/pin/compress
feature set is ported; only per-window GPU capture (WGC) stays Windows-exclusive.

### 🐧 Linux support (X11)
- **Snip / static capture** (libX11 `XGetImage`), **global hotkeys** (`XGrabKey`), and the snip
  overlay's **click-through** + action-key priority (X Shape input regions).
- **Recording**: screen via `x11grab`, **system + microphone audio via PulseAudio**, and
  **webcam picture-in-picture via V4L2** (`/dev/video*`).
- **Pin / preview audio playback** through PulseAudio (`pa_simple`) — fixes silent pinned videos.
- **Scrolling (long) capture** — fixed X11 seam lines and no-click-through.
- Windows-exclusive **per-window (WGC) recording** is hidden on Linux (no equivalent); the
  capture-scope picker keeps monitor selection.
- Bundled native **FFmpeg `.so`** (x11grab / pulse / v4l2 / libx264) resolved next to the app.

### 🔄 Auto-update
- The in-app updater is now **cross-platform**: Windows downloads/swaps the `.zip`; Linux
  downloads/swaps the self-contained single-file `.tar.gz` and relaunches (with backup + rollback).

### 📦 Packaging
- Releases now include a **Linux x64** self-contained single-file tarball
  (`GimmeCapture_linux-x64.tar.gz`) alongside the Windows portable zip + Inno installer, all under
  one combined `SHA256SUMS.txt`.


## v0.50.0 - 2026-07-04

First release since v0.48.0, collecting the compress/editor and recording work built over the
past week (166 commits).

### 🎬 Compress (new tab)
- Import any video and re-encode it smaller entirely in-process (libav): **H.264 / H.265**,
  **compress-to-target-size** (true two-pass for H.264), resolution downscale, **frame-rate cap**,
  encoder preset, CRF, **audio bitrate + mono/stereo mixdown** or drop-audio, custom output path.
- **Batch queue** with parallel encoding, **per-item** output settings + presets, live output-size
  estimate, persistent working directories, and per-row **rotation**.
- **Advanced video editor**: trim / speed / crop / rotate plus Pin-parity annotations, redaction and
  freeze-frame; **inline side-by-side quality compare** with a playhead-following window.

### ⏺️ Recording
- **True per-window capture** via Windows Graphics Capture (WGC), **multi-window** composite and
  separate-file capture, and a **monitor / window capture-scope picker**.
- **Keystroke overlay**, **high-quality GIF** (palettegen/paletteuse) with a **disk-space guard**,
  deterministic audio mixing, webcam PiP (size/shape) with buffer pooling, global **stop hotkey** +
  live **mute**, and an optional pipelined encode path.

### ▶️ Playback
- **GPU hardware-accelerated decode** + frame-drop for smooth large-file preview.

### 🗂️ History
- Compress-style category **tab strip** (Output-copy / Plain-copy).

### 📦 Packaging & licensing
- Optional **Inno Setup installer** (choose install drive) alongside the portable zip.
- **Relicensed MIT → GPLv3** (bundles GPL FFmpeg incl. x264/x265).

### 🧹 Internal
- **Linux port groundwork**: platform-selection seams + stub services (build-shape unchanged,
  Windows-only for now). Tier-2/3/4 refactors (dead-code cleanup, transcoder characterization tests,
  settings split) and a CI Linux compile gate + caching.


## [v0.8.0] - 2026-02-04

### 🎸 Skeletal Theme Enhancement
- **High-Detail Wings**: Integrated skeletal wing assets with direct image rendering for maximum visual fidelity.
- **Corner Aesthetics**: Replaced standard handles with Heart and Skull icons for a distinct "Metal" look.

### 👻 Ghost Mode Implementation
- **Seamless Interaction**: The selection area is now completely transparent and click-through, allowing direct interaction with background applications during capture.

### 📐 Precision Scaling
- **Wing Scale (0.5x - 3.0x)**: Dedicated controls to resize side decorations.
- **Icon Scale (0.4x - 1.0x)**: Independent scaling for corner heart/skull icons to ensure perfect UI balance.

### 🎨 UI & UX Refinement
- **Interactive Numeric Controls**: Added manual text entry support and theme-aware styling to all numeric inputs.
- **Improved Settings Preview**: High-resolution preview window with expanded area to accommodate all scaling options.

### 🌐 Full Trilingual Support
- Updated localization for English, Traditional Chinese, and Japanese across all new customization features.

---

## [v0.7.0] - 2026-02-04 (Trilingual)

### 🎸 New Style: Skeletal & Heavy Metal / 骨格主題 / スケルトンテーマ
- **High-Detail Wings**: Added beautiful skeletal wings in the middle-left and middle-right of the selection. 
  - *ZH*: 導入高細節骨格翅膀，配置於選取區左右兩側。
  - *JA*: 高精細なスカルウィングを導入し、選択範囲の左右に配置しました。
- **Corner Icons**: Replaced generic handles with Hearts and Skulls.
  - *ZH*: 角落手把更換為「愛心」與「骷髏」圖標。
  - *JA*: コーナーハンドルを「ハート」と「スカル」のアイコンに刷新しました。

### 👻 Ghost Mode / 幽靈模式 / ゴーストモード
- **Live Interaction**: The selection center is now truly transparent and click-through, allowing you to interact with background apps (like video players) while snipping.
  - *ZH*: 選取中心區域改為完全可穿透點擊，讓你在擷圖/錄影時仍能操作底層視窗。
  - *JA*: 選択範囲の中央をクリック透過させ、背後のアプリ（動画プレイヤーなど）を直接操作できるようになりました。

### 📐 Scaling System / 比例自訂 / スケーリング
- **Wing Scale (0.5x - 3.0x)**: Customize the size of side wings.
  - *ZH*: 翅膀大小現在可自由調整。
  - *JA*: ウィングのサイズを自由に変更可能になりました。
- **Icon Scale (0.4x - 1.0x)**: Adjust the size of corner Heart/Skull icons.
  - *ZH*: 角落圖標比例現在可獨立調整。
  - *JA*: コーナーアイコンのサイズを調整できるようになりました。

### 🎨 UI Refinements / 介面優化 / インターフェースの改善
- **Enhanced Numeric Input**: New `CompactNumericStep` supports manual typing and matches your Theme Color.
  - *ZH*: 數值輸入框支援手動輸入，並自動套用主題配色。
  - *JA*: 数値入力ボックスが直接入力に対応し、テーマカラーが適用されるようになりました。
- **Settings Preview**: Enlarged style preview (550px) to prevent clipping at high scales.
  - *ZH*: 加大設定預覽視窗至 550px，確保大比例下也不會裁切。
  - *JA*: スケール拡大時の表示切れを防ぐため、プレビュー画面を 550px に拡大しました。

### 🌐 Localization / 語言支援 / ローカライズ
- Full trilingual support for English, Traditional Chinese, and Japanese across all new features.
  - *ZH*: 所有新功能皆完整支援中、英、日三種語言。
  - *JA*: 全ての新機能において、英語、繁体字中国語、日本語のフルローカライズを完了しました。
