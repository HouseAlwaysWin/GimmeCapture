# Optimisation Backlog

What is left of the 2026-10-29 whole-app analysis (six read-only audits: snip overlay, recording, OCR/AI/translation,
pins, compress, start-up and idle, checked against the app's logs). Batch 1 of it landed as 21 commits,
`665c8b94..85411bed` (see `## Unreleased` in CHANGELOG.md). Everything below is still open.

Line numbers are from `665c8b94`, before batch 1; batch 1 moved some of them, so search for the symbol named.
Sizes: S = a few lines to a small class, M = a day-sized change with tests, L = a redesign.

The last section is not an optimisation but a feature the user wants built later: the video editor as a standalone
tool.

## Status

- Batch 1 is on `main` with CI, Linux and CodeQL green. `verify.ps1`: 1600 passed, 0 failed, 22 skipped, coverage 44%.
- Not released yet: `## Unreleased` holds 34 entries. Cut a release with `release.ps1` after adding the
  `docs/catalog/releases.md` entry as its own commit (a hand-pushed tag fails `verify-release-metadata`).
- The user's installed copy is 0.70.0 since 2026-10-29.
- Done since batch 1: **first recording of a run starts slowly**. Every start now logs `Recording.StartTimings`, which
  showed where the time went (`RecordingAvSyncTests` probe, three fresh processes each): FFmpeg 46-49 ms and an NVENC
  open of 135-260 ms instead of 80-100 ms on a run's first start, and `GraphicsCaptureSession.IsSupported()` 16 ms on
  every start (45 ms on the first) although regions never use WGC. Regions no longer ask, a yes is cached, and entering
  Record mode runs `RecordingService.WarmUpAsync` once a run: the first start went from 346-392 ms to 200-215 ms, later
  ones from 172-210 ms to 162-173 ms. Opening audio and video in parallel was left out on purpose: it would save the
  ~55 ms the audio takes, but only by risking the first frame arriving before the audio captures, which puts the head
  out of step.
- Done since batch 1: **the audio gate's precision**. Timestamps on every loopback burst (the same probe, three processes
  of three recordings) showed the first burst written holding 3-68 ms (38 on average) from before the first frame, so
  system audio started that much behind the picture. Against the audio handed over, burst arrivals spread by at most
  10 ms, so a burst can be placed by when it arrived. `RecordingAudioGate` now cuts each burst at the first frame and
  at the stop, a pause or stop waits (0-13 ms measured) until the capture has handed over its audio up to the stop, and
  each segment's audio ends in silence where its video ends: the video runs on 45-105 ms past the stop, and segment
  audio came out 6-120 ms (53 on average) short of its video, so every part after a pause played that much early.
  After: the muxed tracks start together and differ in length only by AAC framing (+27 or +36 ms), and the audio
  written matches the gate's open span within 7 ms. Each pause or stop logs `Recording.AudioGate`.
- Done since batch 1: **the cold OCR scan**. The user's log showed most overlay scans cold (captures more than the
  2-minute keep-alive apart). Measured on the installed models (DirectML, fresh processes; DirectML.dll has to be copied
  beside a Debug build's tests, or the sessions fall back to the CPU): detector alone 445 ms, with the recognizer
  550-660; a cold detector's first run 260-300 ms, then ~10 ms at the same input size and 50-110 at another; a reload
  after the idle unload 340-430 ms plus 260-300, or 170-200 plus 110-140 with the DirectML device still open. Now the
  runtime loads detection and each language's recognizer separately (a language swap keeps the detector), the
  overlay's scan loads detection only, `SnipWindowFactory.Open` starts `AIScanSessionService.PrepareScanAsync` (load,
  then one run on a blank capture of the scan's size), and `GpuDeviceHold` keeps the device for the 5-minute idle
  window. Open-to-boxes with the scan starting 450 ms after the open: first scan of a run 1.39-1.42 s before,
  0.84-0.85 after; after an idle unload 1.18-1.20 before, 0.50 after within 5 minutes and 0.66-0.74 past them. Logs
  `OcrScan.Prepared` and `load=`/`detect=` on `OcrScan.Detected`. The blank-capture run and OCR's `GpuDeviceHold`
  went again with the move to the CPU (next entry).
- Done since batch 1: **OCR on the CPU** (the backlog's "215 MB stays after the first DirectML use"). Measured with the
  installed models on a 3968x1160 capture with 30 lines (Release tests; DirectML.dll copied beside them for the
  DirectML runs): on DirectML the loaded detector kept 500 MB, both models 550, the held device 530 after the idle
  unload and about 270 once it closed; on the CPU with ONNX Runtime's default arena 338, 350 and 52. Raw sessions showed
  why. Detection's arena grew to 285 MB at 1280x384, and to 29-30 MB with `memory.enable_memory_arena_shrinkage` on
  each run (59 ms a run instead of 46; with the arena off, 200 ms runs). DirectML recognition set itself up again
  whenever the width changed: 31 widths twice took 930 ms against the CPU's 310. Now OCR's sessions are CPU-only,
  detection runs with the shrink option, and the warm-up run is gone, since on the CPU it only made the scan wait for
  it (58 ms in the user's log). After: 40, 51 and 8-12 MB; 30 lines read in 0.5 s instead of 1.9; a first detection
  after a load 95-108 ms instead of 316-394, a later one 64-72 instead of 16. `OcrRuntimeServiceTests` has the opt-in
  measurement (OCR_MODELS_DIR).
- Done since batch 1: **the Record tab's AV1 check on Direct3D 11**. Opening `av1_nvenc` with frames in memory makes
  FFmpeg's NVENC create a CUDA context, and the driver kept 190 MB of the process for good (Release tests, two runs).
  `ProbeHardwareAv1` now opens NVENC on Direct3D 11 frames first (a D3D11VA device picked with `vendor_id=0x10de`, so
  an Optimus laptop gets the NVIDIA adapter) and leaves 33-34 MB; an `ENOSYS` answer (the GPU lacks AV1) skips the CUDA
  retry. Recordings were tried the same way and left as they are, see the NVENC item under Idle, start-up and memory.
  `LibavRecordingEncoderTests` has the opt-in measurement (NVENC_PROBE=1).
- Done since batch 1: **OCR scans ran twice**. The user's log had two `OcrScan.Detected` lines 61-69 ms apart after
  most Esc presses: entering Detecting starts the scan (`SnipSelectionStateController`), and `DismissOrClose` started a
  second one, left from 7c54042c, when the first could be skipped. One Esc started 2 scans before and 1 after (a
  view-model test with a counting scan service, run against both). A frozen still also keeps the boxes found on it
  (`_ocrResultsStill`), so a return to Detecting on it shows them again with no scan and no 8 MB copy on the UI thread.
- Done since batch 1, the user's call: **no more EmptyWorkingSet**. Found in the user's 0.70.0 log: the first capture
  three hours after an idle trim (working set 218 MB to 7.6 MB, private bytes 651 MB both before and after) logged
  `SnipOpen.Timings waitedForUiThread=731 window=1252 viewModel=249 freezeGrab=246 show=485` (usually 13-67, 1-74,
  51-73, 14-107) and a 4.1 s `UiStall` at 4% CPU: page-ins from the page file. `ProcessMemoryTrimService` no longer
  empties the working set on Windows; the full trim keeps its compacting GC, and Linux keeps `malloc_trim`
  (`CanReturnFreedMemory`). Task Manager's Memory column now shows a few hundred MB idle instead of a few MB.
- Done since batch 1: **the Compress tab's first open**. The user's log: a 0.77 s `UiStall` (94% CPU, GC +10/+9/+2)
  opening the tab with a 79-file queue. The queue was a plain `ItemsControl`, so every row was built. Headless
  (`SettingsCompressTabTests`, COMPRESS_TAB_PROBE=1, Debug): 79 rows, 9,306 visuals, 144-152 MB, a warm build of
  1.26-1.98 s before; 3 rows, 566 visuals, 8-11 MB, 122-153 ms with a `VirtualizingStackPanel`. The same test found
  that closing the window wrote null into every built row's preset and category (the ComboBoxes lose their
  `$parent` options first), and the category is saved on change, so queued files lost it at exit:
  `IgnoreNullWriteBackConverter` drops those writes. Switching tabs and hiding the window never did it.

## Next session: what the app itself holds (left off 2026-10-30)

The user asked to bring down what the app really holds: Task Manager's commit size (Details tab, Commit size column),
not the Memory column the working-set trim used to empty. Done so far: OCR on the CPU and the Record tab's AV1 check
on Direct3D 11 (Status), then, in a second session on 2026-10-30, the hand checks of both and the breakdown of what the
idle app holds (Measured 2026-10-30, below). Pick up in this order:

1. **Choose with the user what to do about the GPU's share**, the largest one: the options under Measured 2026-10-30.
   A was measured (not a default), and B and C are done (B gave back less than hoped, see there); the pin-resize
   stalls are open.
2. **DirectML after background removal and smart selection** (under OCR, AI and translation): U2Net and SAM2 on the
   CPU against DirectML, measured like OCR's opt-in test (real models, a real size, a fresh process each, twice), and
   `BackgroundRemovalService`'s five-minute device hold.
3. **NVENC** only if the user wants a child process: nothing in-process helped (under Idle, start-up and memory).

### Measured 2026-10-30

A published build of `f01bbcef` with a temporary probe, not committed: an environment-variable hook after
`_trayController.Install()` in `App.axaml.cs` walked a launch to the tray through the main window, each tab, a frozen
capture with text boxes, a full OCR of the screen and OCR's unload. After each step, two `GC.Collect`s, then it logged
private bytes (the commit size), the GC's committed bytes, the Windows heaps (`GetProcessHeaps` + `HeapSummary`), the
rest of the committed private memory (a `VirtualQuery` walk) and the DLLs the step loaded. A switch in `Program.cs`
picked the renderer, the composition mode and Skia's GPU cache. The instance had a folder of its own, settings copied
from the dev instance with the hotkeys on Ctrl+Alt+Shift+F8-F12 and update checks off, the Chinese UI and 45 history
entries. Machine: Ryzen 5 7535HS with Radeon 660M plus RTX 3050 Laptop, 1920x1080 primary at 125%.

Commit size in MB, GPU rendering (the default: ANGLE on Direct3D 11) against software rendering:

| Step | GPU | Software |
|---|---|---|
| Launched to the tray, 25 s on | 92 | 32 |
| Main window shown | 217 | 100 |
| All nine tabs opened, window hidden | 287 | 186 |
| Overlay up with its text boxes | 594 | 274 |
| Overlay closed | 473 | 243 |
| OCR unloaded, 2.5 min on (stays there at 10 min) | 389 | 218 |

- **Record tab AV1 check**: `Recording.HardwareAv1Probe: unavailable` here, the RTX 3050 cannot encode AV1 ("Codec
  not supported" on the Direct3D 11 open, no CUDA retry). The commit size rose 31-51 MB as the tab was built and settled
  2-6 MB above where it was (two runs). The "available" path cannot be checked on this machine.
- **OCR memory**: with software rendering, which takes the GPU out of the numbers, the Windows heaps went from 97 MB to
  170 at the overlay (detector, the frozen still, the GDI+ grab), 168 after recognizing the screen (1092-1382
  characters) and 126 after the unload: tens of MB, and about 28 MB stays.
- **The GPU's share is the largest.** ANGLE and the driver cost 59 MB at a launch to the tray with no window on screen,
  58 more once a window shows, and the overlay adds about 270 MB of private memory outside the heaps, of which about
  60 MB stays after it closes (after OCR unloads: 389 against 218). This laptop's GPU memory is system memory, so all of
  it is commit. No composition mode lowers the idle level (WinUI, DirectComposition, RedirectionSurface and the
  low-latency swap chain all sat at 83-93 MB in the tray, 213-219 with the window, 415-440 ten seconds after the
  overlay), WGL is worse (137 in the tray) and an 8 MB Skia GPU cache instead of the default 28 changed nothing
  (40 s after the overlay: 417/402 default, 423/423 with 8 MB). Only software rendering removes it.
- **The main window is never freed.** Hiding it keeps it and its tabs (about 195 MB with the GPU, 155 in software).
  Closing it for real (`MainWindow.Shutdown()` and dropping the bootstrapper's `_mainWindow`) freed nothing either: a
  `WeakReference` showed it still alive after three collections, so something it wired up keeps it (most likely the
  view model's actions and subscriptions from `MainWindow.ViewModelWiring`).
- **The Record tab loads all of FFmpeg** for its codec list: the seven DLLs plus `vulkan-1.dll`, 18 MB of writable
  image sections and about 8 MB of heap, kept for the rest of the run even if nothing is ever recorded.
- **Smaller ones**: the first window adds about 40 MB of heap even in software (HarfBuzz, the fonts); the Control and
  History tabs about 13 MB of heap each; the GC heap peaked at 74 MB committed; the About tab fetches the release list
  (the whole HTTP and TLS stack loads) and the working set jumps by 277 MB of file-backed pages there, not commit.

Options to put to the user:

- **A. Software rendering** (the most: -60 MB in the tray, -117 with the window, -170 after a capture). It moves all
  drawing to the CPU. Measured the same day (the probe drove each case through the view models for 5-8 s and counted
  `RequestAnimationFrame` callbacks, which run to about 115 a second here; two runs each, CPU in % of one core):

  | Case | GPU | Software |
  |---|---|---|
  | Overlay at 1920x1080, a selection dragged every frame | 47-48%, 70-72 fps | 100-103%, 93-96 fps |
  | Overlay hover animation, the point moving | 27-29% | 23-27% |
  | An image pin (1280x720) resized every frame | 71-76%, 60 fps | 92-99%, 26 fps |
  | The same pin moved every frame | 17-19%, 40 fps | 37%, 28 fps |
  | A 720p video pin playing | 21% | 37-41% |

  Software rendering doubles the CPU of full-screen and video drawing (and a 4K screen has four times the pixels) and
  makes resizing a pin choppy, so it is not a default; at most an opt-in "use less memory" setting, for the user to
  decide. The magnifier was not measured (it follows the real pointer).
- **New lead from the same runs: resizing a pin stalls the UI thread.** Both renderers logged `UiStall` warnings of
  0.3-1.5 s, one after another, while the pin was resized every frame: with the GPU the thread waited (about 20% CPU),
  in software it ran a dozen or more full GCs a second, a new bitmap per size. Check by hand by dragging a pin's
  corner quickly; it fits "Resizing rebuilds the window region 4-6 times per pointer move" under Pins.
- **B. A main window that can be freed** when closed to the tray: done 2026-10-30, and worth less than hoped. What
  kept it was what it and the Compress tab wired into the view model, which outlives them: the window's dialog, picker
  and update actions and its `IsProcessing` subscription, and the Compress tab's three file pickers and editor opener,
  which shared one closure with the tab (found by dumping a headless test process and walking the GC roots with ClrMD,
  `Microsoft.Diagnostics.Runtime` 3.1 from the local NuGet cache). `MainWindow.Release` undoes them and closes the
  window, the Compress tab undoes its own on leaving the logical tree, and `AppBootstrapper` releases the window once it
  has sat hidden for `IdleTrimDelay` (not while a module download or a Compress editor depends on it), the tray host
  standing in as the lifetime's main window. `MainWindowReleaseTests` (MAIN_WINDOW_PROBE=1): collected, every setting
  unchanged, reopened. A published build with a temporary probe (launched to the tray at 93-96 MB, every tab opened,
  closed, released, twice): every tab opened 292-293 MB, hidden 275-293, released and collected 261-265, so 14-28 MB
  back; then an aggressive GC took the heap from 51 MB committed to 35 and the commit to 242-247, which the full trim
  now does (`GCCollectionMode.Aggressive`); `SKGraphics.PurgeAllCaches` gave nothing. About 150 MB above the tray stays:
  native memory the window's first open loaded (the GPU's share, fonts, libraries), not held by the window.
- **C. No FFmpeg for the Record tab's codec list**: done 2026-10-30. `HardwareCapabilityCache` keeps the probe's
  answer (hardware AV1, GIF) in `hardware-capabilities.json` beside the settings, keyed by the probe's version and the
  display adapters' device ids and driver versions from the registry; a match goes to
  `LibavRecordingEncoder.RecallHardwareAv1` without loading FFmpeg. Three fresh runs opening the Record tab: the first
  probed (255 MB two seconds after the tab opened, 220 fifteen seconds on), the next two took the kept answer and loaded
  no FFmpeg DLL (209/207 and 203/200). Linux has no key yet and probes every run, as before.

What made the numbers trustworthy: private bytes after two `GC.Collect`s, one fresh test process per variant, each run
twice, at the real capture size with real work. A 640x480 probe that encoded nothing made Direct3D 11 look as if it
saved 190 MB per recording; a 3968x1160 run with frames showed it saved nothing.

## Hand checks still owed for batch 1

These were covered by unit tests only; check them in the real app before a release.

- **Tray launch** (autostart): a copy shows its toast with the thumbnail, an error shows a toast, a delayed capture
  shows its countdown, and an update the start-up check found is offered when the main window is first opened.
- **Overlay close**: after a recording whose overlay was excluded from capture, no yellow frame ghost stays on screen;
  a plain snip's close no longer repaints every window.
- **Save** on a pin opened from part of another pin, and on a Compress editor freeze-frame pin.
- **First recording of a run**: entering Record mode now warms FFmpeg, the encoder, the audio endpoint and the WGC
  check up in the background (see Status). After a launch, the log should show one `Recording.WarmUp` line and the
  first `Recording.StartTimings` line (`n=1`) close to the later ones.
- **System-audio keep-alive**: while recording, the app shows as a silent session in the Volume Mixer, and each segment
  starts about 65 ms later. Nothing should be audible.
- **Audio gate**: each pause and stop logs a `Recording.AudioGate` line (`head`, `tail`, `pad`, `waited`), with `pad`
  about 45-105 ms and `waited` a few ms. A recording paused once or twice should stay in step after each pause, which
  no test covers: the probe records single segments.
- **Cold OCR scan**: open a capture after OCR has sat unused for more than two minutes. The log should show
  `OcrRuntime.SessionCreated: detection: CPU`, `OcrScan.Prepared` and then `OcrScan.Detected` with a small `load=` and
  a `detect=` around 70-110 ms, and the text boxes should come up about as the overlay appears. Nothing measured the
  real overlay's timing, only a stand-in for it.
- **OCR memory** (a published build, which has DirectML.dll; a Debug build never ran OCR on DirectML): in Task
  Manager's Details tab with the Commit size column, a capture with text boxes, then Translate or copy text, should add
  tens of MB, not hundreds, and the commit size should fall back within two minutes. Translating a screenful of text
  should not be slower than in 0.70.0. Checked 2026-10-30 by a probe for the OCR part (Measured 2026-10-30): tens of
  MB, about 28 MB staying; the translation speed was not checked.
- **Record tab AV1 check** (NVIDIA PC): in a fresh run, open the Record tab. The log should show
  `Recording.HardwareAv1Probe: available` as before, and the commit size should rise by about 35 MB, not 190. An AV1
  recording should still record AV1. Checked 2026-10-30 on an RTX 3050 (no AV1 encoder): `unavailable`, 2-6 MB once
  settled; the "available" path still needs an RTX 40 or newer.
- **Settings window let go of** (a published build): close the settings window to the tray and leave it five minutes or
  more; the log shows `MainWindow.Released`. Opened again from the tray, it comes up on its first tab, and the folder
  and file pickers (Compress included), the dialogs, a hotkey that fails to register and the update offer work as before.
  About ten minutes after closing, the commit size should sit 30-40 MB below where it was with the window hidden. With a
  Compress editor open, or a module downloading, it is not let go of.
- **Esc on a frozen capture**: select a region, press Esc. The text boxes should come back at once, and the log should
  hold one `OcrScan.Detected` per capture, not one more for each Esc.
- **A capture after a long idle**: leave the app alone for an hour or more while the computer is in use, then press a
  capture hotkey. `SnipOpen.Timings` should be close to the usual ~0.1 s, with no multi-second `UiStall`, and no more
  `MemoryTrim.WorkingSet*` lines. Only one such press has been measured, before the change.
- **Compress tab**: open it with the long queue: no `UiStall`, and scrolling the queue shows each file's own preset and
  category. Pick a category for a file, exit from the tray, start again: the category is still there.

## How to run a batch

Six `isolation: "worktree"` agents on disjoint file sets, each told: no commits, no CHANGELOG/csproj/lock files, copy
`src/GimmeCapture/ffmpeg-lib/*.dll` into its worktree, targeted tests with TRX output, report commit messages, hunks
and a CHANGELOG bullet per change. Apply every diff to main's working tree, add follow-ups, run `verify.ps1`, then
split into commits with a plan file and replay script, dry-run on a temporary index first. The method and its traps
are in the assistant's memory note "integrating-parallel-agent-worktrees".

## Backlog

### OCR, AI and translation

- **Recognition runs up to four passes per box, one box per run** (S gating, M batching). `PaddleOCREngine.cs:111`:
  `shouldEnhance = contrast < LowContrastThreshold || box.Height < 28` forces a second pass for most UI text whatever
  the first pass's confidence; dark crops double it again. Callers loop one box at a time (`QuickOcrService.cs:66-74`,
  `TranslationService.cs:153-160`) though the recognizer's batch dimension is dynamic. Fix: extra passes only below
  about 0.9 confidence, inverted pass first on dark crops, batch boxes by width bucket.
- **Every LLM request builds a new context and re-reads the whole prompt** (M). `LlamaSharpTranslationEngine` uses
  `StatelessExecutor`, which creates a context (KV cache for 2048 tokens) and prefills from zero per call. Fix: one
  long-lived context, the per-language-pair instruction prefix decoded once and forked per request
  (BatchedExecutor/Conversation), blocks batched into one prompt. Measure first; no load-time log exists yet.
- **Auto re-probes the language in every overlay** (S). `OcrScriptDetector.cs:85-112`: keep the Auto verdict app-wide.
  Each probe swaps recognizers, which now costs ~135 ms each (the shared detector stays loaded). Only matters with
  several OCR languages installed.
- **11 MB input tensor allocated per scan** (S). `PaddleOCREngine.cs:78`. Reuse belongs in `OcrRuntimeService`.
- **Full-screen resize on the UI thread per OCR search** (S). `SnipWindowViewModel.Selection.AIScan.cs:216` resizes to
  1600 px (Mitchell), then `DetectText` resizes again to 1280. Do one resize, off the UI thread.
- **DirectML still keeps ~270 MB after background removal or smart selection** (measure first, then M-L). OCR moved
  to the CPU (see Status), but U2Net and SAM2 still run on DirectML, and `BackgroundRemovalService` holds the device for
  five minutes after each use: its comment's 45 MB predates sessions having run on it, and the device OCR held kept
  about 260 MB more than a closed one. Measure both on the CPU, and the hold; the alternative is AI work in a child
  process that exits when idle.

### Recording

- **Multi-window (composite) WGC redoes all the work every frame** (M). `LibavWgcCompositeMkvSession.cs:223-249`,
  `WgcFrameExchange.cs:130-161`: clears the canvas, `TryCopyLatest` copies every source under the lock, Skia redraws,
  full `sws_scale`, for every output frame (est. 15-25 ms at 2560x1440). `RecordingFramePacer` stamps one counter pts
  per pass, so a loop slower than 1/fps makes the video shorter than its audio. Fix: lease sources with
  `TryReadFrameAfter(lastSeq)`, redraw only changed cells from the lease, re-send the encoder frame when nothing
  changed or to catch up, log readBack/converted as the single-window path does.
- **WGC reads frames back at the window's update rate** (M). `WgcWindowCaptureSource.cs:169-177`: every FrameArrived
  with no readback in flight does CopyResource, Map, memcpy; the log shows `readBack=254 converted=187`, 26-37% wasted.
  Keep the newest frame and read it back only when due. `MinUpdateInterval` needs build 26100+ and a newer projection.
- **Colour conversion for hardware encoders runs on the CPU** (S-M). NVENC and AMF take BGRA, but frames go through
  single-threaded `sws_scale` to NV12 (`LibavRecordingEncoder.cs:301-303`, `LibavGdigrabMkvSession.cs:536-551`,
  `LibavWgcMkvSession.cs:259`); overlays add a BGRA copy; the webcam frame is copied full size every output frame
  (`WebcamCaptureSource.cs:157`). Est. 3-5 ms per frame at 1080p, 12-25 at 4K.
- **Stop and finalize run in series** (S-M). `RecordingService.Finalize.cs:21,38-42,514`, `LiveSession.cs:555-567,593`:
  a fixed `Task.Delay(100)`, then concat, WAV mix, AAC, mux and move in sequence; sources stop one after another; AAC is
  encoded only after stop (433 ms for 16 s of audio, est. half a minute per 30-minute recording); after a pause the
  video is rewritten twice. Drop the delay, stop everything then await together, encode AAC live or alongside the
  concat, concat and mux in one pass into the destination folder.
- **The Linux audio still gates whole packets and ends each part short** (S; needs a Linux desktop to check).
  `LinuxPulseAudioRecorder` keeps or drops each packet on `RecordingAudioGate.IsOpen` and writes no silence at the
  end, so after a pause every later part plays early by the video's overrun (45-105 ms on Windows). Give it a `Feed`,
  placing each packet by its arrival as on Windows, and the `PadToVideoLength` step; check a paused recording's file
  with `RecordingAvSyncTests`' file mode.
- **gdigrab vs Desktop Duplication** (M; zero-copy L). `LibavGdigrabMkvSession.cs:140`: BitBlt with CAPTUREBLT, two CPU
  copies and a CPU conversion, est. 20-50 ms per frame at 4K, so 4K30 likely drops frames unseen (gdigrab never counts
  drops). FFmpeg's `ddagrab` + `hwdownload,format=bgra` into the existing converter gives monitor choice, crop, a GPU
  cursor and HDR. Keep gdigrab for regions spanning monitors; re-check the overlay stays excluded.
- **A failed WebM audio mux re-encodes the whole video** (S). `RecordingService.Finalize.cs:635`: move the video-only
  file instead.

### Compress and the editor

- **Every editor or compare step reopens the file** (M). `LibavVideoFramePlayer.cs:567-582` (`DecodeFrameAtAsync`:
  open, find_stream_info, new D3D11VA device, seek, decode), called from `VideoEditViewModel.cs:227,254,650,686` and
  `CompareViewModel.cs:428,442`. A cold open logged 279-293 ms; compare pays it twice; rotate, crop toggles and
  split/merge re-decode an unchanged frame. Fix: one long-lived decoder per view, and cache the last BGRA frame.
- **GIF/WebM output: an extra encode, no progress, cancel ignored** (S-M). `CompressPipeline.cs:314-369`: an H.264
  CRF 18 intermediate at the user's preset even with no edits; the transcode then gets no progress or token, so the bar
  sits at 100% through the slowest stage and a cancel finishes as Done. `LibavGifTranscoder.TranscodeToGif` now takes
  progress and a token (batch 1); wire them through `GifWebmVideoExporter.cs`, `CompressPipeline.cs`,
  `RecordingService.Finalize.cs` (FinalizationProgress) and `FloatingVideoViewModel.Actions.cs`, and transcode the
  source directly when nothing is edited.
- **A target size at or above the source's size still encodes** (S). `CompressBatchRunner.cs:116-124`: stream-copy
  with `LibavMuxer.RemuxAllStreams` when unedited, otherwise cap at the source bitrate. Audio-only M4A from an AAC
  source can stream-copy too.
- **Segmented CRF path writes the video four times** (M). `CompressPipeline.cs:462-523`, `LibavClipExporter.cs:224-227`:
  each 30 s chunk reopens and probes the input and copies its output; finalize concatenates, builds the audio after the
  bar shows 100%, then remuxes. Write chunks in place, concatenate straight into the final mux, build audio alongside.
- **Rotation round-trips every frame through BGRA** (S-M). `LibavClipExporter.cs` (~338, 913-956): sws to BGRA, Skia
  rotate, sws back. Use transpose/hflip,vflip in the YUV graph or display-matrix rotation; render static annotations
  once. Two-pass pass 1 also writes a `pass1.mp4` nobody reads.
- **Small ones**: every CRF-slider tick saves `compress-session.json` on the UI thread
  (`MainWindowViewModel.VideoCompress.cs:552-559`); `GimmeCapture_*` folders in %TEMP% are never swept after a crash;
  preview frames are decoded on the GPU, downloaded at full size and scaled on the CPU.

### Pins

- **Resizing rebuilds the window region 4-6 times per pointer move** (M). `FloatingImageWindow.axaml.cs:61-89,166-177,
  218-265`, `Win32Helpers.cs` (~323-353): DisplayWidth and DisplayHeight each rebuild on stale bounds, then the window,
  MainBorder and toolbar Bounds changes rebuild again; each walks toolbar subtrees and ORs rects one `CombineRgn` at a
  time (O(n^2) for a cut-out pin's thousands of rects). Batch 1 made an unchanged region skip `SetWindowRgn`, not the
  rebuild. Fix: a dirty flag rebuilt once per frame at Render priority; build the image part once per bitmap with
  `ExtCreateRegion` and an XFORM to rescale.
- **Opaque-pixel scan on the UI thread at every Image change** (S). `FloatingImageWindow.axaml.cs:315-322,481-510`
  copies into a 32 MiB rented array and scans every pixel, per crop, rotate, AI result, undo and redo (est. 20-30 ms at
  4K). Scan on the pool, cache per bitmap, skip for captures known to be opaque.
- **A video pin reopens file, GPU device, decoder and audio at every loop and seek** (M-L).
  `FloatingVideoViewModel.Media.cs:279-313,371-373` (`loopPlayback = false`, a new `PlayFramesAsync` per pass),
  `LibavVideoFramePlayer.cs:670-718`; `UpdateAudioStateFromPlayback` reopens Media Foundation and WASAPI too. Keep one
  decoder per pin, do loops and seeks as requests to it, seek the audio on loop; 1-2 decode threads under D3D11VA.
- **Each pin builds its own U2Net session** (S-M). `FloatingImageViewModel.AI.cs:684-687`: `new
  BackgroundRemovalService` per pin; sessions take 661-962 ms. One shared service owned by `AppBootstrapper`.
- **Saving an annotated pin swaps `Image` for the whole dialog** (S). `FloatingImageViewModel.Actions.cs:33-58`:
  flattens even on cancel, drops SAM2's prepared image and the selection points, rescans, leaves ~33 MB at 4K to the
  finalizer. Make it `SaveAction(Bitmap)`, flatten after a path is picked, dispose after encoding.
- **Copy encodes the PNG twice** (S-M). `FloatingImageViewModel.Actions.cs:145-196`, `ClipboardService.cs:172-177`:
  the temp file gets one encode, the clipboard path another plus a GDI+ decode (+0.33 s CPU per Ctrl+C at 4K); the
  flattened and cropped bitmaps (and rotate's flatten, :95) are never disposed; each copy makes a `GimmeCapture_Copy_*`
  folder swept only at start-up after 12 h.
- **Each mosaic or blur annotation holds a full-size SKBitmap** (S-M). `AnnotationControl.axaml.cs:39-40,104-106`,
  `AnnotationRenderService.cs:48-52`: ~33 MB per redaction at 4K for the pin's lifetime, built on the UI thread. Share
  one copy per snapshot and drop it when the drag ends; a resize also re-renders previews that do not depend on it.
- **Closing a pin frees little** (S). Image view models are disposed only by CloseCommand, so Alt+F4 skips it and a
  held SAM2 lease keeps SAM2 loaded (`FloatingImageWindow.axaml.cs:191-216`, `SAM2RuntimeService.cs:89-90`); Dispose
  never frees `Image`; undo history is capped at 30 steps, not bytes (up to ~1 GB at 4K,
  `AnnotationEditorState.cs:150`); pins never call `ProcessMemoryTrimService.NotifyActivity`.
- **WebM export from a video pin decodes the whole audio track and writes the WAV on the UI thread**, and leaves its
  trimmed intermediate folder behind (`FloatingVideoViewModel.Actions.cs`; found during batch 1).
- **Lower**: video pins decode at full size and copy each frame twice (`LatestFrameHandoff.cs:40`, then
  `FloatingVideoViewModel.Media.cs:564`, 33 MB per frame at 4K) and keep decoding when covered;
  `ProcessingOverlay.axaml:87` keeps `IsIndeterminate="True"` (a 60 Hz animation after the first AI job? measure);
  the translation layer adds results one at a time, rebuilding its region per item (`FloatingTranslationWindow.axaml.cs:
  109-117`).

### Snip overlay

- **Frozen open grabs and builds the backdrop on the UI thread** (M). `SnipWindowFactory.cs:166-170` blocks in
  `.GetAwaiter().GetResult()` over GDI+ `CopyFromScreen`, a row copy into an SKBitmap and another into the
  WriteableBitmap (`WindowsScreenCaptureService.cs:82-85,126-150`): `freezeGrab=46` + `show=120` ms vs 14-26 ms for a
  live open. BitBlt into a cached DIB section, copy once, build the display bitmap in the same Task.Run, reuse buffers.
- **A cancelled OCR scan still runs a detection it has queued** (S, low value now). The token stops a detection only
  before the pool picks it up (`AIScanSessionService` `Task.Run(..., ct)`); one cancelled while waiting for the
  inference lock behind another still runs. Since the Esc double start went and a frozen still is no longer re-scanned,
  overlapping scans need a live overlay and quick repeated presses. Check the token after taking the lock.
- **OCR candidate rectangles are built one item at a time** (M). `Selection.AIScan.cs:633-647` adds 68-109 rects as
  separate CollectionChanged events, each binding `$parent[Window].DataContext.OcrHighlightColor` through a converter
  that allocates a brush, plus a pointer-over style that never matches (`SnipWindow.axaml:140-153`). One control that
  draws all rects in Render with a shared pen; grab only the active monitor (`AIScanSessionService.cs:77-81`).
- **Hover animation renders at 60 Hz while the overlay sits idle** (S). `Selection.AIScan.cs:26` (16 ms timer, no
  settled exit at :564-570), `HoverRectangleAnimationController.cs:63`; the dirty rect is often the whole screen. Stop
  once the move ends or tick at 30 Hz or less.
- **Frozen commits keep the overlay up while the pin window is built** (M). `Capture.cs:464`; copy with history on
  re-encodes the PNG the clipboard path already encoded (`:342`, `WindowsScreenCaptureService.cs:440-441`). Hide the
  frozen overlay right after the crop, reuse the PNG bytes. (Explains the 307 ms "blocked" stall before close.)
- **Record-mode audio meter** (S). `SnipWindowViewModel.cs:507`: a 120 ms timer in Record mode even when idle, MMDevice
  COM objects created on the UI thread at the first tick (`AudioLevelMonitorService.cs:54-59`), the mic metered when not
  recorded, at least six notifications per tick (`:749-772`). A long-lived meter off the UI thread posting changes only.
- **Manual scrolling allocates per frame** (S). Row signatures built three times a frame into a new
  `byte[height * samples * 4]` (`ScrollStitcher.cs:110-111,351,775`, LOH from ~665 rows); the producer keeps grabbing
  when nothing scrolls (`ScrollingManual.cs:304-309`). Carry signatures in rented buffers, skip unchanged frames.
- **Freezing mid-session after entering drawing mode live keeps the live snapshot** (S). Needs
  `SnipWindowViewModel.Capture.cs` (left from batch 1).
- **Small ones**: `ToolbarMaxWidth` raised on every pointer move (`Selection.AIScan.cs:852`, `State.cs:812`); the key
  filter rebuilds a 28-field record on every view-model PropertyChanged (`SnipWindow.Win32.cs:148,227`).

### Idle, start-up and memory

- **The UiStall monitor never sleeps** (S). `UiThreadStallMonitor.cs:28,33,62,69`: a 100 ms DispatcherTimer and a 250 ms
  watchdog, about 14 wakeups a second in the tray, the app's only scheduled idle work; probably logs a false stall
  across Modern Standby. Pause both while no window is visible and resume on a hotkey or window show, or post one
  probe a second.
- **History thumbnails decoded for every entry and never released** (S-M). `MainWindowViewModel.History.cs:149-156,
  184-187`, `HistoryItemViewModel.cs:126`: about 70 MB at the 300-entry cap for the rest of the session. Decode when a
  row is realized, at card width, dispose when the tab detaches or the window hides.
- **CJK fonts copied whole into native memory** (M). `LocalizationService.cs:63-68,109-114`: 16.8 MB for zh-TW,
  12.9 MB for ja-JP, on the UI thread. Subset the UI-chrome fonts, or load during the idle warm-up. Cinzel lacks
  ▶ ◀ → ⚠ ✓, so English falls back through the JP chain for those.
- **The raw-input sink duplicates the keyboard hook** (S). `WindowsGlobalHotkeyService.cs:488,537-577`: about four
  hotkey-thread wakeups per keystroke typed elsewhere. Register the sink only when the hook fails or stops counting.
- **The `avalonia=` start-up stage (~375 ms) is the largest left** (measure first). It includes the whole ColorPicker
  theme for one `ColorSpectrum` (`App.axaml:60-68`) and all three locale files parsed through `x:Static`. Split the
  mark with `AfterPlatformServicesSetup` and around `AvaloniaXamlLoader.Load` before changing anything.
- **Tray sessions still miss** the failed-hotkey-registration dialog and the overlay's request to open the Modules tab;
  an update found there waits for the main window, because `UpdateDialog.ShowDialog` cannot open centred on screen.
- **Two AV1 probes can run at once** (S): `HasUsableHardwareAv1Encoder` does not guard against the Record tab, the
  Record-mode warm-up and a recording start probing together (`LibavRecordingEncoder`).
- **NVENC's driver keeps 200-260 MB once a recording has used it** (L: a child process is the only fix found). Release
  tests at 3968x1160, 120 frames, three encoders a process: 208-266 MB on frames in memory (CUDA), the same on CUDA
  with 4, 8 or 16 `surfaces`, 198-256 on Direct3D 11 frames with a device each, and one Direct3D 11 device kept across
  encoders grew to 456 MB. Only an encoder that encodes nothing gains from Direct3D 11 (the AV1 check, done). Direct3D
  11 frames also cost a recording start the device, 140 ms on the default adapter and 300-330 with `vendor_id`, against
  a CUDA open of 110-180 ms; with a kept device an open took 7-17 ms. Entering Record mode opens NVENC for the warm-up,
  so the 190 MB is paid there even when nothing is recorded: the price of the faster first start.

## Suggested batch 2 split

Disjoint file sets for parallel agents:

1. OCR recognition passes, the Auto verdict, per-scan buffers (`OcrRuntimeService`, `PaddleOCREngine`,
   `OcrScriptDetector`, `QuickOcrService`).
2. Recording: composite WGC, WGC readback pacing, BGRA into hardware encoders, finalize, the Linux audio gate.
3. Compress: persistent editor/compare decoder, GIF/WebM intermediate with progress and cancel, stream copy at or above
   source size, rotation in YUV, segmented path.
4. Pins: resize region, opaque scan, persistent video decoder, shared background removal, save/copy paths, close and
   undo memory, pin WebM audio.
5. Overlay: frozen open, OCR double scan and cache, candidate render control, hover timer, record meter, scroll buffers.
6. Idle and memory: UiStall pause, history thumbnails, raw input, ColorPicker/locales; DirectML after background
   removal and smart selection.

The LLM context reuse is its own piece of work: measure load and generation times with the real model first.

## Planned feature: the video editor as a standalone tool

Asked for on 2026-10-29 and put off until later (M-L). The editor already has a window of its own; what keeps it
inside the options window is how you reach it and what it can produce.

**Today**

- `Views/Main/VideoEditWindow.axaml(.cs)` (960x760, non-modal) with `VideoEditViewModel.cs` and
  `VideoEditViewModel.Editing.cs`, the compare view (`CompareView.axaml`, `CompareViewModel`) inline.
- The only way in is a Compress queue row's Edit button (`SettingsCompressTab.axaml:229`, then `OpenEditor` in
  `MainWindowViewModel.VideoCompress.cs` around :852-899, then `OpenEditorAction` wired in
  `SettingsCompressTab.axaml.cs:86-120`). Apply only writes the edit back to `CompressQueueItem`: kept runs with their
  speed, crop, rotation, annotations, redaction tracks and the output name. The file comes out only when the user goes
  back to the Compress tab and runs the batch. Record, trim, save therefore takes: open the main window, Compress tab,
  add the file, wait for its probe, Edit, Apply, back, Start.
- The window and the editor's freeze-frame pin are built in the tab's code-behind (`new VideoEditWindow`,
  `new FloatingImageViewModel(... new ClipboardService() ...)`). Its confirm dialogs go through
  `MainWindowViewModel.ConfirmYesNoAction`, which the main window owns; with no main window that falls back to `true`,
  so opened from anywhere else, Reset all would discard every edit without asking.
- A pinned video has an overlapping editor of its own (`FloatingVideoViewModel*.cs`): split and merge segments,
  per-piece speed, crop, redaction keyframes, freeze-frame, save and export through `LibavClipExporter`. Two
  implementations of the same edits, and the pin timeline has needed several fixes of its own (v0.70.0).

**Plan**

1. Propose the design to the user (in Traditional Chinese) and get an OK before refactoring: the entry points, what the
   editor produces (export directly, apply to a queue item, add to the queue), how the pin's timeline and the editor
   share the work.
2. An edit session between `VideoEditViewModel` and its caller, with a result sink: "apply to this queue item" (today's
   behaviour) or "export directly".
3. Export from the editor: MP4, WebM or GIF, by quality or target size, through `CompressPipeline` /
   `LibavClipExporter`, off the UI thread with progress and cancel; optionally "add to the compress queue".
4. Entry points: a pinned video's toolbar ("open in editor"), History video entries, the end of a recording (its
   finished toast or prompt), a tray-menu "Edit video…" with a file picker. Keep the Compress row's Edit.
5. Open the window through a service owned by `AppBootstrapper` (in `Services/Platforms/Avalonia/`), not tab
   code-behind. The editor window owns its confirm dialogs, so they work without the main window. The freeze-frame pin
   gets injected services.
6. Move the pin's heavy edits into the editor step by step; the pin keeps quick trim, loop and playback. Agree the
   split with the user.
7. Fix "Every editor or compare step reopens the file" (Compress and the editor, above) first or alongside: about
   0.3 s cold per click matters more once the editor is a main tool.

**Done when** a video opens in the editor from a pin, History, a finished recording and the tray and exports straight
from there; the Compress queue flow still works; new strings are in all three locales; the edit session, its sinks and
the export have tests; `scripts/verify.ps1` passes before each push.

If the Compress tab should be easier to find too, a tray-menu "Video tools" entry opening the main window on that tab
is enough: batch compress is set-and-run, so a tab suits it.
