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
**Step 1 of 3 is done and committed:** the Windows target platform version was
bumped so WinRT projections are available:
- `src/GimmeCapture/GimmeCapture.csproj`, `tests/GimmeCapture.Tests/*.csproj`,
  `tests/GimmeCapture.Benchmarks/*.csproj`: `net10.0-windows` →
  `net10.0-windows10.0.19041.0`.
- **`packages.lock.json` for all three projects must be regenerated** on Windows:
  `dotnet restore GimmeCapture.slnx --runtime win-x64`, then commit the updated
  lock files. (The cloud Linux session could not regenerate them.)

**First thing to do locally:** restore + build + run the app and confirm the TFM
bump didn't break anything (no WGC code yet). Commit the regenerated lock files.

## Plan — remaining steps
### Step 2 — WGC probe (de-risk the interop FIRST)
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
