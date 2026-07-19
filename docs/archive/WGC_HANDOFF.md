# Handoff: True per-window recording via Windows Graphics Capture (WGC)

> **已終結（archived）**：2026-06-27 使用者決定停止追蹤 dual-monitor WGC 問題；WGC 逐視窗錄影維持 Windows 專屬、Linux 隱藏。本文件僅供歷史參考。

> Read this first if you are a fresh Claude Code session picking up the
> `claude/record-window-wgc` branch on a local Windows machine.

---

## ⚠️ ACTIVE ISSUE (2026-06): dual-monitor WGC produces no frames → hang

> This is the current task for branch `claude/record-wgc-multimonitor`. Do this on a
> **local Windows machine with the multi-monitor setup that reproduces it** — it
> cannot be reproduced or verified on Linux/CI.

### ✅ RESOLVED FROM THE ACTUAL RUNTIME LOG (read this first)
The repro machine's AppLog (`%LOCALAPPDATA%\GimmeCapture\logs\gimmecapture-*.log`) settled it:
- **The hang/10s freeze is fixed** (Fix A.1–A.3). New build self-identifies via `Wgc.Build …`.
  Old separate runs were ~12 s/window serial; the new runs bring up all windows in parallel and
  fail over in ~1.7 s total. No more unclosable hang.
- **WGC delivers ZERO frames for every window — even one on the PRIMARY monitor** (`Wgc.FirstFrame.Timeout`
  on every hwnd). The box has a **single GPU** (`adapter='NVIDIA GeForce RTX 4070 SUPER'` for both
  monitors; `Get-CimInstance Win32_VideoController` shows one controller). So:
- **Fix B (pick the DXGI adapter for the window's monitor) is MOOT here** — there is only one adapter and
  WGC already binds it. WGC FrameArrived simply never fires on this machine's dual-monitor + NVIDIA driver
  (32.0.15.9597) config; that's environment/driver-level, not something the capture code can force.
- **Decision (user, 2026-06-27): make the multi-window fallback actually usable** instead of chasing WGC.
  Implemented **Fix C** (below): separate-files mode now falls back to **per-window gdigrab region capture**,
  one file per window, with correct secondary-monitor (negative-offset) coordinates. Verified on the repro
  machine: gdigrab captures a region at `x=-2000` on DISPLAY2 and produces a valid non-empty MKV.
- A latent bug was also fixed: when WGC failed, separate-files finalize had **no segments** per track and
  emitted **nothing** ("separate recording is broken"). Per-window region capture populates the tracks.

### Fix D — clamp rects, skip dead WGC, never strand the overlay (after 2nd test on hardware)
The AppLog showed Fix C *firing* (`Wgc.SeparateFallback.Region track=0/1`) but separate mode still produced one
merged file and the "multi-window unavailable" message. Three root causes, all found/fixed against the real log
and verified on the machine:
1. **gdigrab rejected the per-window rects.** Maximized windows report `GetWindowRect` a few px *outside* the
   desktop (`track1 rect=…@(-2056,-88)`, `track0` right edge 1928 > desktop 1920). gdigrab returns
   `avformat_open_input: I/O error (-5)` for any rect past the virtual-desktop bounds → both captures failed →
   fell through to the single merged region (issues #1 message + #2). **Fix:** clamp the rect to the virtual
   desktop (`GetSystemMetrics(SM_*VIRTUALSCREEN)`) in `TryGetWindowCaptureRect`. Proven on the repro box: the raw
   `(-2056,-88)` rect fails; the clamped `(-2048,-80,2056,1120)` rect captures 404 KB of real content. Both
   windows now record.
2. **WGC was retried (and its ~1.5 s first-frame timeout paid) on every recording.** `LibavWgc*MkvSession` now
   expose `TimedOutWaitingForFrame`; `RecordingService` sets a process-static `_wgcNoFramesThisSession` the first
   time WGC brings up but delivers no frames, after which `WgcAvailable` is false and recordings skip straight to
   gdigrab — no per-recording timeout, and the "unavailable" warning shows **once**, not every time (issue #1
   "卡" + recurring message). Reset by restarting the app. (Only a genuine no-frames/bring-up *wedge* trips it —
   a fast `Start()`-returns-false / window-gone does not, so healthy machines are unaffected.)
3. **A failed pin left the selection overlay stranded.** When the (broken) single-region fallback produced no
   file, `ExecutePinRecordingAsync` hit an early `return` *before* `CloseAction`, leaving the dimmed selection on
   screen with no way to dismiss it (issue #3). **Fix:** call `CloseAction` before that return.

`StartFfmpegSegmentAsync` was restructured so multi-window routing no longer depends on `WgcAvailable`: it always
reaches the per-window-region path for separate mode (and single region for composite) whether WGC is skipped or
attempted-then-failed.

**Confirmed from the log:** clamped rects now capture (`track1 … rect=2056x1120@(-2048,-80)`), and after the first
no-frames the cache logs `Wgc.Disabled (session)` and subsequent recordings skip straight to
`Wgc.SeparateFallback.Region` (no WGC probe). The ~1.5 s WGC probe is therefore paid only once per app launch.

4. **The yellow selection border stayed drawn over the recorded window after stop/pin** (confirmed by a user
   screenshot — a `SelectionBorderColor` frame around the recorded app). It is the `SnipState.Selected` Win32
   window-region ring (`SnipWindow.Win32.Region.UpdateWindowRegion`, drawn when
   `state == Selected && SelectionRect.Width > 10`). The overlay isn't being torn down promptly after a
   window/separate recording, so the region (and the snap-candidate `WindowRects` outlines) lingered. **Fix:**
   `ClearRecordingSelectionVisuals()` runs on every recording-completion path (stop / pin / copy) right after
   `StopAsync` and **empties `SelectionRect`** (plus `WindowRects`/`AIScanRects`). Emptying `SelectionRect` makes
   the throttled `_selectionRectSubscription` re-run `UpdateWindowRegion`, which drops the border ring — so the
   frame clears whether or not the overlay actually closes (if it closes, `OnClosing → ClearWindowRegion` clears
   it anyway). Safe because pin/save sizing reads `_recordingCaptureLogicalRect` (captured at record start), not
   `SelectionRect`.

### Fix E — per-window region video was sped up (wall-clock PTS)
N large window regions captured concurrently make gdigrab fall behind the target fps, but
`LibavGdigrabMkvSession.DecodeEncodeLoop` stamped output PTS from a frame counter at `1/fps`
spacing → a 5 s capture became a ~1 s (5×) video. **Fix:** new `LibavGdigrabMkvSession.UseWallClockPts`
(off by default — single-region path unchanged) paces output PTS by real elapsed time
(`pts = elapsed_seconds * fps`, started lazily on the first frame, kept strictly monotonic), and
forces the single-threaded encode loop. Set true by `StartSeparateGdigrabSegmentsAsync` **and** by
`StartGdigrabSegmentAsync` whenever windows are involved (`_windowHandles.Count > 0` — the single-window /
composite / separate-last-resort fallbacks, which all capture large regions). Normal drawn-region recording
(no windows) keeps the counter PTS. Result: output duration == real time (lower effective fps under load,
but correct speed). **Verified on the repro box:** a full-1080p region for ~3.2 s real → counter PTS gave a
1.5 s (sped-up) video; wall-clock gave 3.0 s.

> NOTE: composite mode on a machine where WGC fails still falls back to a **single gdigrab region of the
> windows' union bounding box**. For windows on different monitors that box spans both screens (~3968×1160) —
> now correct-speed but large and not a clean tile. Separate-files mode (per-window region) is the clean
> option there; a proper gdigrab composite (per-window region → grid) is not yet implemented.

> ⚠️ BUILD GOTCHA: the running app holds `GimmeCapture.exe`/`.dll`, so `dotnet build` fails the
> output copy (MSB3027/MSB3021) while it's open — the new code never lands in the exe. **Close the
> app before rebuilding.** Several "the fix didn't change anything" reports traced to stale binaries.

### Fix F — the leftover yellow recording border was a DWM ghost of the capture-excluded overlay
Multiple attempts to clear it via the ViewModel (empty `SelectionRect`, `CurrentState=Idle`, clear `WindowRects`,
force the Win32 region to 1×1) all **ran** (proven by the `Recording.ClearVisuals v2`/`ForceClearSelectionRegion`
AppLog lines) yet the frame stayed. The decisive log:
```
SnipWindow.CloseAction → Close() (IsVisible=False)   ← overlay already hidden
SnipWindow.OnClosing (RecState=Idle, Cancel=False)   ← overlay DOES close
```
So the overlay is hidden and closed — the yellow frame is a **DWM ghost** of the `WDA_EXCLUDEFROMCAPTURE` +
`SetWindowRgn` recording overlay: hiding/closing a capture-excluded, regioned window can leave its last-rendered
border on screen (it survives the hide and the close). **Fix:** in `OpenRecordingProgressWindowAction` (the
finalize step that hides the overlay) drop the affinity to `WDA_NONE` and clear the window region **before**
`Hide()` (so it hides cleanly with no ghost), and in `SnipWindow.OnClosed` `RedrawWindow(desktop, RDW_*)` as a
safety net to repaint over any ghost that already formed. Also suppressed the recurring
`[ERR] UnobservedTaskException` (the WGC gate's faulted `firstFrame` task on the fallback path) via
`ObserveFaultedTask`. Instrumentation (`CloseAction`/`OnClosing`/`ClearVisuals`/`ForceClearSelectionRegion` logs)
is left in for now.

### Fix C — per-window region fallback for separate-files mode (implemented + verified)
`RecordingService.LiveSession.StartSeparateGdigrabSegmentsAsync()`: when `StartSeparateSegmentsAsync` (WGC)
returns false, capture each window's `GetWindowRect` physical rectangle via its own `LibavGdigrabMkvSession`
into that track's segment file (sessions start concurrently; even dimensions; webcam PiP skipped — a dshow
device can't be shared across N sessions). `VideoTrack.GdigrabSession` holds it; stop/dispose handle both
WGC and gdigrab sessions; the existing per-track finalize is session-agnostic. Trade-off vs WGC: does not
follow the window if it moves and records whatever is on top — but it is GPU-agnostic and reliable.
If per-window region capture also fails, `_tracks` is cleared and it drops to a single region that finalizes.

### Symptom (reported)
On a **two-monitor** desktop (single GPU), recording a window via WGC:
- recording takes **several seconds to actually start** (it hits the 5 s
  `StartupGateAsync` timeout and reports "started" with no real frame),
- then **hangs on stop / pin** — the app becomes unclosable,
- **no error** is logged anywhere.

On a **single-display laptop (Win11, hybrid GPU)** the *same build* works fine. So this
is **environment-specific, NOT a code regression** — verified by checking out the
known-good pre-merge WGC branch tip and reproducing the hang there too. The WGC code in
`main` is byte-identical to the version that was tested working.

### Root cause (confirmed by code; exact trigger TBD on hardware)
WGC reports success at every step but the frame pool never raises `FrameArrived`, so the
encode worker's prime loop (`LibavWgcMkvSession.RunTranscode`, the
`while (!ct.IsCancellationRequested)` wait for the first `TryCopyLatest`) blocks forever;
`StopAsync` then awaits that worker and the UI thread hangs on the pinning `await`.

The capture device is created on the **default GPU adapter**, ignoring which monitor /
adapter actually renders the target window:
- `WgcInterop.CreateDirect3DDevice()` (`WgcInterop.cs:58`) passes
  `pAdapter = IntPtr.Zero` + `D3D_DRIVER_TYPE_HARDWARE`.
- `WgcWindowCaptureSource.Start()` (`WgcWindowCaptureSource.cs:57`) →
  `Direct3D11CaptureFramePool.CreateFreeThreaded` → `OnFrameArrived` (line 109).

Note: the first multi-GPU theory was **wrong** — the failing machine has a *single* GPU
with two monitors, while the working laptop has *dual* GPUs. So the differentiating
variable is the **display configuration (2 monitors vs 1)** — likely per-monitor DPI/scale,
window-on-secondary-monitor, or the GPU driver's WGC multi-monitor handling. Pin it down
with the diagnostics in Fix A before attempting Fix B.

### Fix A — symptom-agnostic, do this first (low risk)
Whatever the exact trigger, the failure mode is always "WGC started but no first frame".
Make that recoverable instead of a hang:
1. **First-frame timeout → fall back to gdigrab.** In the WGC session
   (`LibavWgcMkvSession` / `LibavWgcCompositeMkvSession`), if no first frame arrives within
   **~1.5–2 s**, treat the start as **failed** (return `false`) so the existing fallback
   chain in `RecordingService.LiveSession.StartFfmpegSegmentAsync()` drops to **gdigrab
   region capture** (GPU-agnostic, already works on this machine). Today the gate reports
   `started=true` on timeout, so the fallback never fires — that's the gap.
2. **Stop timeout** so the app can never hang unclosably: wrap the WGC `StopAsync` awaits in
   `RecordingService.LiveSession.StopCurrentSegmentAsync()` with
   `Task.WhenAny(stopTask, Task.Delay(~6 s))`.
3. **Diagnostics to a file (not `Debug.WriteLine`)** via `AppLog.Information` /
   `AppLog.Error` (Serilog file sink under `%LOCALAPPDATA%\GimmeCapture\...\logs`): log the
   selected adapter description, the window's monitor (`MonitorFromWindow`), whether a first
   frame arrived and how long it took, and the frame count at stop. Run once on the failing
   machine → the log identifies the exact trigger.

### Fix A.2 — the teardown was still blocking (follow-up, committed after first test)
First test on the repro machine showed Fix A's *gate* worked but start/stop still froze
~10 s (single window) and effectively dead-locked in **separate** mode. Root cause: the
WGC **teardown** is what wedges (frame-pool / capture-session native `Dispose`, or a
readback stuck holding `_readbackLock`), and the worker disposed the source via
`using`, so the worker Task never completed — then `StartupGate`'s backstop, the
start-failure cleanup, and `Dispose()`'s `_worker.Wait(8 s)` all **blocked on that wedged
worker** (and stacked **per window** in separate mode → ~24 s, looked like a hang).

Fix: **never wait on a wedged WGC teardown.**
- `WgcWindowCaptureSource.DisposeDetached(source, label)` disposes the source on a
  background (`IsBackground`) thread; the recording worker no longer waits on it, so it
  always completes promptly (gate returns ~1.5 s, stop returns fast, fallback fires fast).
  The detached teardown may leak a thread if the native dispose never returns — preferable
  to a frozen app; it logs `Wgc.Source.DisposeDetached … tookMs=…` when slow.
- `LibavWgcMkvSession.RunTranscode` / `LibavWgcCompositeMkvSession.RunTranscode` now detach
  source disposal in their `finally` instead of `using` / `src.Dispose()`.
- `Dispose()` `_worker.Wait` lowered 8 s → 2 s in both sessions; first-frame timeout 2 s → 1.5 s.

### Fix A.3 — the bring-up (`Start()`) itself was unguarded + serial stacking
Second test: **still ~10 s, no change.** A.2 only helped the no-frames-then-teardown case; it
could not help if **`WgcWindowCaptureSource.Start()` itself blocks** (`D3D11CreateDevice` /
`Direct3D11CaptureFramePool.CreateFreeThreaded` wedging on the multi-monitor box). The
first-frame timeout starts measuring only *after* `Start()` returns, so an unbounded `Start()`
is invisible to it. Plus separate/composite ran each window's `Start()` **sequentially**, so
even bounded costs stacked to ~10 s for N windows. (Could not be distinguished from a stale
build without a runtime log — so both are now covered, and a build marker settles it.)

Fix:
- **Timeout-guard `Start()`** (`BringupTimeoutMs = 1500`): run it on a throwaway `Task`; if it
  doesn't finish in time, log `Wgc.Start.Timeout`, abandon the half-built source via
  `WgcWindowCaptureSource.DisposeDetachedAfter(startTask, source, …)` (waits for the wedged
  `Start()` to settle on a background thread, then disposes — no field-write race), and fail so
  the caller falls back to gdigrab. Now the worker **always self-terminates ≤ ~3 s**.
- **Parallelize multi-window bring-up**: composite brings up all sources concurrently under one
  shared `BringupTimeoutMs` (`Task.WaitAll(tasks, timeout, ct)`); `StartSeparateSegmentsAsync`
  launches every window's `StartAsync` eagerly then awaits them — N windows ≈ one window's cost.
- **Build/version marker** `Wgc.Build sessionType=… bringupTimeoutMs=… firstFrameTimeoutMs=…`
  logged at each WGC `StartAsync`, so the AppLog proves which binary is running.
- `Dispose()` `_worker.Wait` set to 3 s (a cap that lets a finishing worker release its output
  file before the gdigrab fallback opens the same path; normally returns at once).
- Verified: `dotnet build` clean (0 errors); 480/481 unit tests pass (the 1 failure,
  `EnteringSelectedRecordingState_AppliesDefaultHideRecordToolbarSetting`, pre-exists on `main`).

**Next-run triage from the AppLog** (paste it back):
- No `Wgc.Build` line at all → **stale build** (changes not running). Clean-rebuild.
- `Wgc.Start adapter='…'` + `Wgc.FirstFrame.Timeout` → bring-up *succeeded*, **no frames**
  (the documented case). Fix B (correct DXGI adapter) is the cure.
- `Wgc.Start.Timeout … bringupMs=…` (no `Wgc.Start adapter=`) → **bring-up itself wedges**;
  Fix B still applies (device on the right adapter) but confirms the wedge is in `Start()`.

### Fix B — root fix, later (higher risk, needs local DXGI testing)
Create the D3D device on the adapter that drives the window's monitor:
`MonitorFromWindow(hwnd)` → match to the `IDXGIOutput`/`IDXGIAdapter` via
`IDXGIFactory.EnumAdapters` + `adapter.EnumOutputs` → call `D3D11CreateDevice` with that
`IDXGIAdapter` and `D3D_DRIVER_TYPE_UNKNOWN`. This is the "correct" multi-adapter handling
but requires DXGI enumeration interop; build and verify it on the real two-monitor machine.

### Not affected / out of scope here
- The **gdigrab 60 fps "sped up" fix** is independent and lives on branch
  `claude/record-fps-timing` (real wall-clock PTS, gdigrab only — `WallClockPtsClock`).
- Branches `claude/record-audio-mix-fix` and `claude/record-webcam-audio-polish` are still
  awaiting Windows verification.

---

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
