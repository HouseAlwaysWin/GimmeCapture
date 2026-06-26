# Handoff: True per-window recording via Windows Graphics Capture (WGC)

> Read this first if you are a fresh Claude Code session picking up the
> `claude/record-window-wgc` branch on a local Windows machine.

## Goal
Add **true "record a specific window"** to GimmeCapture's recording mode: the
output follows the window as it moves, works for GPU/DWM-composited windows
(Chrome, UWP, hardware-accelerated apps), and captures the window even when it's
occluded. This must be done with the **Windows Graphics Capture (WGC)** API —
the same tech Win10/11's built-in recorder and OBS "Window Capture" use.

## Why WGC (what already failed)
- **Drawn region / monitor capture** (shipped on branch `claude/record-capture-scope`)
  works fine — that's just gdigrab desktop offset+size.
- **gdigrab `title=`** was tried and **reverted**: it uses BitBlt from the window
  DC, which returns an **all-black frame** for modern DWM/GPU-composited windows.
  Dead end. (See the reverted commit on this branch's history.)
- So real window capture **requires WGC** (DirectX/WinRT), which is why the work
  is on this dedicated branch.

## Current state of THIS branch (`claude/record-window-wgc`)
**Steps 1 and 2 of 3 are done and committed.**

**Step 1 (TFM bump + lock files) — DONE.** Windows target platform bumped to
`net10.0-windows10.0.19041.0` for all three projects so WinRT projections are
available. The three `packages.lock.json` files were regenerated on Windows
(`dotnet restore ... --runtime win-x64`) and committed. The test
`FakeWindowDetectionService` got the missing `GetRecordableWindows` member so the
test project compiles. Verified: build green, 457 tests pass, app launches cleanly.

**Step 2 (WGC probe) — DONE and proven on a real machine.** See
`Services/Platforms/Windows/WgcWindowCaptureProbe.cs` (+ `IWgcWindowCaptureProbe`).
It captures one frame of an HWND via WGC and saves a PNG. Picking a **window** in
the record capture-scope picker fires the probe (temporary trigger) and writes
`wgc-probe.png` next to the app data dir, toasting the path. Confirmed: capturing a
Brave (Chromium/GPU-composited) window produced a fully-rendered, non-black image —
the exact case gdigrab returned black for. **The BGRA readback path
(`SoftwareBitmap.CreateCopyFromSurfaceAsync` → `IMemoryBufferByteAccess` → tightly
strided BGRA) is the one Step 3 should reuse to feed the encoder.**

> Reusable interop bits already written in `WgcWindowCaptureProbe.cs`: D3D11 device
> creation + WinRT `IDirect3DDevice` wrapping (`CreateDirect3DDevice`),
> `IGraphicsCaptureItemInterop.CreateForWindow` (`CreateCaptureItemForWindow`),
> free-threaded frame pool. Note: `IsBorderRequired` is NOT in the 19041 SDK
> projection, so it is not referenced; revisit if the border needs hiding.

## Status: ALL THREE STEPS DONE ✅ + multi-window (composite & separate)
Single-window WGC recording (steps 1–3) shipped, AND multi-window recording was added
on top: pick several windows in the picker and either **composite** them into one tiled
video or record each to a **separate** file (toggle in the picker). See
`CompositeGridLayout`, `LibavWgcCompositeMkvSession`, the `VideoTrack`/separate path in
`RecordingService.*`, and `MultiWindowMode`. Both modes verified end-to-end (composite →
one 2560×1440 grid; separate → N native-resolution files, shared synced audio).

Step 3 (single window) pieces:
- `Services/Platforms/Windows/WgcInterop.cs` — shared WGC/D3D interop.
- `Services/Platforms/Windows/WgcWindowCaptureSource.cs` — continuous BGRA source
  (latest-frame-under-lock; recreates pool on resize).
- `Services/Platforms/Windows/WgcInterop.cs` — shared WGC/D3D interop.
- `Services/Platforms/Windows/WgcWindowCaptureSource.cs` — continuous BGRA source
  (latest-frame-under-lock; recreates pool on resize).
- `Services/Core/Media/NativeFFmpeg/LibavWgcMkvSession.cs` — stopwatch-paced encode
  loop (BGRA → encoder pixfmt → Matroska), reuses `LibavRecordingEncoder`.
- `Services/Core/Media/NativeFFmpeg/LibavRecordingEncoder.cs` — encoder ladder +
  packet drain shared with the gdigrab session.
- `RecordingService.StartAsync(... IntPtr windowHandle)` + `StartFfmpegSegmentAsync`
  routes window targets to WGC (gdigrab-region fallback w/ warning if WGC fails).
- `SnipWindowViewModel` stores the picked HWND (`_recordWindowHandle`, cleared by the
  `SelectionRect` setter) and passes it to `StartAsync`.
- The temporary Step 2 probe was removed (superseded by the source).

Verified end-to-end via the public `RecordingService` path: recorded a Brave window
→ valid 1920×1020 H.264 Matroska (hardware `h264_amf`); a decoded frame shows the
real window content. Build green, 457 tests pass.

### Known v1 limitations / follow-ups
- Cursor/click highlight overlays and webcam PiP are **not** composited in WGC window
  mode yet (gdigrab region mode still has them). WGC frames are already BGRA, so it's
  the natural place to add them — but the cursor/click overlay needs the cursor mapped
  into window-local coords. The real OS cursor is included via
  `GraphicsCaptureSession.IsCursorCaptureEnabled` (set from `drawMouse`).
- On window resize the output is scaled to the **initial** window size (fixed encoder
  dimensions); content isn't re-cropped, just stretched. Fine for v1.
- Audio mux wasn't exercised in the automated test (recorded video-only), but the
  audio path in `RecordingService.*` is unchanged — WGC only swaps the video session.

---

## Plan — original steps (for reference)

### Step 2 — WGC probe (de-risk the interop FIRST) — ✅ DONE (probe later removed)
Write a minimal `WgcWindowCaptureSource` (or `WgcProbe`) that captures **one
frame** of a given top-level window (HWND) and saves it as a PNG, to prove WGC
works on the user's machine and produces a non-black image. Suggested flow:
1. Create a D3D11 device (`D3D11CreateDevice`) and wrap it as a WinRT
   `IDirect3DDevice` via `CreateDirect3D11DeviceFromDXGIDevice` (small P/Invoke to
   `d3d11.dll`).
2. `GraphicsCaptureItem` for the HWND via the `IGraphicsCaptureItemInterop`
   activation-factory interop (`CreateForWindow`).
3. `Direct3D11CaptureFramePool.Create(device, B8G8R8A8UIntNormalized, 1, item.Size)`,
   `CreateCaptureSession(item)`, `StartCapture()`, await one `FrameArrived`.
4. GPU→CPU readback: `SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)`
   (avoids hand-written D3D staging/map). Copy its BGRA bytes.
5. Save via **SkiaSharp** (already a dependency): `SKBitmap` (BGRA8888) → PNG.
6. Gate everything on `GraphicsCaptureSession.IsSupported()`.

Wire a trigger so the user can run it: e.g. when a **window** is picked in the
record capture-scope picker, save `wgc-probe.png` next to the recording output and
show a status message. **Build it yourself, run it, open the PNG — confirm it's
the window, not black.** This is the key checkpoint; do not proceed until it's good.

### Step 3 — feed WGC frames into the encoder (real recording)
Turn the probe into a continuous source and encode to MKV reusing the existing
encoder/muxer. Two viable shapes:
- A `WgcWindowCaptureSource` background source exposing latest BGRA frame (mirror
  `Services/Core/Media/NativeFFmpeg/WebcamCaptureSource.cs`, which already does
  exactly this pattern for the webcam), **and**
- A `LibavWgcMkvSession` (or a refactor of `LibavGdigrabMkvSession`) whose input
  loop pulls BGRA frames from the WGC source instead of `av_read_frame` from
  gdigrab, then runs the same encode path (`OpenRecordingEncoderContext`, muxer).
- Keep pause/resume + audio working (those live in `RecordingService.*`, unchanged).

## Integration seams (already mapped — reuse these)
- **Where the recorder is built:** `RecordingService.LiveSession.cs`
  `StartFfmpegSegmentAsync` constructs `LibavGdigrabMkvSession` and computes
  `x/y/w/h`. Route window targets to the WGC session here.
- **Service entry:** `RecordingService.StartAsync(...)` stores capture params; add
  a window HWND/target param (the reverted title= attempt added a similar string
  param — mirror it with an HWND instead).
- **The picker already has the HWND:** `IWindowDetectionService.GetRecordableWindows()`
  returns `RecordableWindow(Title, Bounds, Hwnd)`
  (`Services/Platforms/Windows/WindowDetectionService.cs`). The VM picker is
  `ViewModels/Main/SnipWindowViewModel.CaptureScope.cs` (`CaptureTargetItem`,
  `SelectCaptureTarget`). Carry `Hwnd` on `CaptureTargetItem` and through to
  `StartAsync`.
- **Encoder/muxer to reuse:** `LibavGdigrabMkvSession.cs`
  (`OpenRecordingEncoderContext`, the DecodeEncodeLoop/composite, `LibavMuxer`).
- **External-BGRA precedent:** `WebcamCaptureSource.cs` already pushes BGRA frames
  from a background thread with a latest-frame lock — copy that shape.

## Other recording branches (context; not part of WGC)
- `claude/record-controls` — MERGED (PR #42): stop hotkey + mute.
- `claude/record-webcam-pool` — PR #43 open: webcam buffer pooling.
- `claude/record-audio-mix-fix` — pushed, awaiting Windows verification.
- `claude/record-capture-scope` — pushed: monitor + drawn-region picker (window
  entries currently just snap a rect; WGC here will supersede that). Awaiting
  verification.
- Remaining planned: `record-webcam-audio-polish`, `record-keystroke-overlay`,
  `record-gif-disk`, `record-encode-pipeline`. Full plan in the session's plan file.

## Workflow notes for the local session
- You can now **build and run** (`dotnet build`, `scripts/verify.ps1`, launch the
  app) — use that to iterate on the WGC interop directly instead of guessing.
- Project conventions are in `CLAUDE.md` (MVVM/ReactiveUI, partial classes,
  interface-first services, **localization parity across en-US/zh-TW/ja-JP**,
  keep `packages.lock.json` in sync, don't commit ffmpeg DLLs).
- Develop on `claude/record-window-wgc`; commit with clear messages; PR only when
  asked.
