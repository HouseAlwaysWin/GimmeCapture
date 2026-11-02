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
- Not released yet: `## Unreleased` holds 57 entries. Cut a release with `release.ps1` after adding the
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
  measurement (AI_MODELS_DIR).
- Done since batch 1: **background removal and smart selection on the CPU**, which leaves nothing on DirectML. Measured
  on a Ryzen 7 5700X with an RTX 4070 SUPER (adapter 0), the installed U2Net and SAM2 Tiny, raw sessions in fresh
  Release test processes, each variant twice, at 3968x1160. U2Net on DirectML: 1.09 GB with the model loaded, 476-483 MB
  with the session freed and the device held, as `BackgroundRemovalService` held it for five minutes, then 252-260 for
  good; a build of 1.0-1.2 s and runs of 8-38 ms. On the CPU: a build of 0.45-0.5 s, runs of 0.37-0.41 s, and 1.24 GB
  after two runs with ONNX Runtime's arena, 206-216 MB with the shrink option, 174-185 with the arena off, nothing once
  freed. SAM2 Tiny on DirectML: 2.9 GB once the encoder had run, 370-380 MB for good after the unload; a load of
  3.3-4.2 s, the encoder 0.16-0.18 s, the decoder 15-49 ms. On the CPU: 2.4 GB with the arena, 250 with the shrink
  option, 210-226 with it off; the encoder 1.5-1.7 s, 1.36-1.42 with `ORT_ENABLE_ALL` instead of BASIC; the decoder
  40-60 ms; the warm-up's blank encoder run 1.7-2.0 s. Now both build CPU sessions with the arena off, SAM2 with every
  graph optimization and no warm-up, and `GpuDeviceHold` and the GPU fallback are gone. Through the services
  (`BackgroundRemovalServiceTests` and `SAM2RuntimeServiceTests`, opt-in, AI_MODELS_DIR): a removal 0.9-1.1 s with the
  load, then 0.38-0.40, 190 MB loaded and 18-20 once unloaded; smart selection a load of 2.7-2.8 s, the first prepare
  1.7 s, a click 120-130 ms with the mask's resize, 212 MB in use and 38 once unloaded. So a first removal is done
  sooner, smart selection's first mask comes about as soon as on DirectML (no warm-up), and only each further picture
  prepares slower, 1.4 s instead of 0.17.
- Done since batch 1: **the DirectML package swapped for ONNX Runtime's CPU package**, which nothing missed once every
  model ran on the CPU. The Windows publish lost `DirectML.dll`: 333 files and 589.0 MB before, 332 and 568.3 after; a
  zip of it 228.1 MB before, 218.3 after. The two packages' `onnxruntime.dll` alternated three times each under the
  SAM2 measurement ran the same (prepare 1.78-1.92 s with the CPU build, 1.81-1.84 with DirectML's, 212-214 MB in use
  with either), and DirectML's build had never loaded `DirectML.dll` for a CPU session. The updater copies a release
  over the install and removes nothing, so `InstanceStorageCleaner` now deletes the `DirectML.dll` it leaves. The
  ceiling DirectML's package put on ONNX Runtime (1.24.4) is gone; a newer one is a change of its own.
- Done since batch 1: **resizing a pin stalled the UI thread** (the lead under Measured 2026-10-30, and Pins' "Resizing
  rebuilds the window region 4-6 times per pointer move"). `PinResizeProbeTests` (PIN_RESIZE_PROBE=1, Release) drags a
  corner of a real 1280x720 pin for 5 s, a mouse move every 2 ms posted only once the last was taken, as Windows
  coalesces them, and counts frames, UI-thread CPU, region rebuilds and how late a Background-priority tick gets (what
  `UiStall` measures). Before, twice each: an opaque pin 60 frames/s with the UI thread 96-98% busy, about 32 region
  rebuilds a frame (61-62% of the time) and the tick up to 2.7-3.1 s late; a cut-out pin (an ellipse, about 700
  rects) 34-41 frames/s, 93-100% busy, 4 rebuilds a frame at 5.7-6.6 ms each. Every move applied the resize (window
  position, size, the view model's DisplayWidth and DisplayHeight, each of which queued its own rebuild before the layout
  had caught up), and every bounds change after the layout rebuilt again, ORing the cut-out's rects one `CombineRgn` at
  a time. Now `ResizeToward` keeps the newest pointer and applies it in a `RequestAnimationFrame` callback, the region
  rebuild is posted once at Loaded priority, after the frame's layout, and the image's opaque part is a `ScalableRegion`:
  its rects ORed half at a time once per bitmap, kept as `GetRegionData`, placed for each rebuild by one
  `ExtCreateRegion` with an XFORM, and ORed into the other rects last. After, twice each: 60 frames/s for both, 17-20%
  and 21-27% busy, 1.7 rebuilds a frame (the second one because the pin ran past the bottom of the screen, which moves
  the toolbar each frame) at 0.7 and 1.6 ms, the tick at most 17-45 ms late. `ScalableRegionTests` holds the placed
  region to the rects and to a one-by-one union.
- Done since batch 1, the user's call: **software rendering as a setting** (option A under Measured 2026-10-30), "Use
  less memory (draw with the CPU)" on the General tab, off by default (`AppSettings.SoftwareRendering`). Avalonia picks
  its renderer once, as its Win32 platform starts: `Program.BuildAvaloniaApp` hands it `StartupRenderer.Win32Options`
  through `With<Win32PlatformOptions>(Func)`, which it calls only then, and which returns its defaults or
  `RenderingMode = [Software]`. The setting comes from the read Main began when that has finished (0.1 ms), else from
  the one property peeked from the config (0.6-0.7 ms in the published build), and `Startup.Renderer` logs which. A
  change offers a restart when it differs from what the process started with (`AppRestart`, along the elevated
  restart's path). Measured on the desktop (Ryzen 7 5700X, RTX 4070 SUPER, 60 Hz at 100%) with a published build in
  the 2026-10-30 probe instance, launched to the tray, then the main window shown by a second launch and closed again,
  GPU and software alternated twice: GPU 94-95 MB in the tray, 188-196 with the window, 186-191 closed; software
  30-32, 96-97 and 94-96, with ANGLE never loaded. A dedicated GPU saves as much as the laptop's shared one did.
  `PinResizeProbeTests` with PIN_RESIZE_RENDERER=software, twice each: the GPU 55-61 frames/s, the tick at most
  19-36 ms late; software 19-20 frames/s with 43-44 gen2 GCs in the 5 s drag (0-1 on the GPU), the tick at most
  55-63 ms late. Linux keeps the GPU: the setting is hidden off Windows, as nothing measured X11's software path.
  Found by the user's first look in software: every pin drew a black band round its frame, where the glow and the
  resize handles are. On the GPU (WinUI or DirectComposition) Avalonia makes every window transparent by default; on the
  CPU only a window that asks (`TransparencyLevelHint="Transparent"`, granted through DWM's blur-behind on Windows 8 and
  later), and the image, video and translation pins never asked. They do now; the probe asserts
  `ActualTransparencyLevel` Transparent on both renderers, and with a lime window behind, the band showed lime in
  software (R2 G253 B0). `TransparentWindowMarkupTests` fails any .axaml window with a transparent background that
  does not ask; windows built in code (toasts, the countdown, the scrolling-capture windows) already did.
- Done since batch 1, the user's call: **a pin resized in software at 20 frames a second**. A sampling profile of the
  probe's test host (a scratch console app on `Microsoft.Diagnostics.NETCore.Client` 0.2.510501 and `TraceEvent`
  3.1.21 from the local NuGet cache: EventPipe's sample profiler and the runtime's GC events) showed the render thread
  busy 68% of the drag, about 65% of that in `DrawingContextImpl.DrawRectangle`: the pin's glow, a `BoxShadow` that
  Avalonia blurs with an image filter over a layer as large as the pin every frame, and the transparency checkerboard
  under its image, a `VisualBrush` it records and fills again every frame. The image's own scaling was 12%. Taking
  either out alone gave 22-23 frames/s, both 46-53; the checkerboard as a bitmap tile cost no more than none. The GCs
  are `InducedNotForced` background gen2s, Avalonia's `UnmanagedBlob` adding memory pressure for each new framebuffer
  size, 46 ms of pauses in all: not the cause. Now `GlowDecorator` draws the glow (pins, video pins, the capture frame,
  the style preview) with Skia's mask blur through a custom draw operation, Border's own Render being sealed, and
  `TransparencyCheckerboard` repeats a 10x10 tile with a Skia shader (an `ImageBrush` tile is drawn unscaled past 100%
  by Avalonia 12). `GlowDecoratorRenderTests` (GLOW_PROBE=1) renders old and new with Avalonia's Skia renderer: the
  glow within 3 levels at 100%, 5 at 125%, 7 at 150%; the checkerboard identical at 100% and 200%, its square edges
  smoothed rather than antialiased at 125% and 150% (26 levels at most). After, software 29-46 frames/s over six
  drags (mostly 30-36; a frame takes about 16 ms, so the 60 Hz ticks split them into 60 and 30), the GPU 60 as before;
  the glow is under 1.5% of the render thread, the image's scaling now the largest part.
- Done since batch 1, the user's pick: **CJK fonts copied whole into native memory** (from Idle, start-up and memory).
  Decompiled (ICSharpCode.Decompiler 7.1 from the local NuGet cache): avares:// fonts reach Avalonia.Skia's
  `TryCreateGlyphTypeface(Stream)`, `SKTypeface.FromStream`, and SkiaSharp turns the managed stream into an
  `SKMemoryStream`, a copy of the whole file. Each font has one face, ExtraLight, so the SemiBold and Bold the UI uses are
  synthesized, and `FontCollectionBase.TryCreateSyntheticGlyphTypeface` makes the bold face from `TryGetStream` (the
  whole font into a `byte[]`) through the same copying call. Headless with Skia, Noto Serif TC: +16.9-17.2 MB of
  private bytes loaded, +36 MB once regular and bold text was drawn; Microsoft JhengHei, a system font DirectWrite maps,
  about 0. Now the TC and JP fonts are manifest resources, not AvaloniaResource, and `MappedFontCollection`
  (`fonts:GimmeCapture`, registered in `App.Initialize`) wraps each resource's pointer in the mapped assembly image in
  an `SKData` without a copy, makes the face with Avalonia.Skia's internal `SkiaTypeface` by reflection, and overrides
  the synthetic face to make another typeface over the same bytes. `MappedFontCollectionTests` (FONT_PROBE=1): ~0 MB
  loaded, +0.5-1.8 MB once drawn in Normal, SemiBold and Bold, and the render identical to the font read through a
  stream, 0 of 512,000 bytes different; two guards run in CI, the resources being in memory and the reflected
  constructor existing (an Avalonia update that changes it falls back to the copy). Linux's single file is compressed,
  so the assembly is already a private copy there; it still saves SkiaSharp's two.
- Done since batch 1, the user's pick: **the Record-mode warm-up kept NVENC's 190 MB** (from Idle, start-up and
  memory). Entering Record mode opened NVENC on frames in memory, and the CUDA context that made left the driver holding
  190 MB of the process until exit, recorded or not. A temporary probe (fresh Release test processes, five per variant
  in alternating order, NVENC at 1280x720) timed a recording's first open after each warm-up: none 144-200 ms; loading
  `nvcuda.dll` and `nvEncodeAPI64.dll` 137-149 (nothing kept); `cuInit` alone 122-130 (7.6-7.8 MB kept, 21-22 ms);
  the old NVENC open 89-95 (190 MB, 164-174 ms); every later open 86-108. One run each: `cuInit` plus a context made
  and destroyed 96 (175 MB), so the context is what the driver keeps; NVENC on Direct3D 11 frames 135 (35 MB, 422 ms).
  `WarmUpEncoder` now only starts the CUDA driver (`CudaDriver.TryInitialize`: `nvcuda.dll` from System32,
  `libcuda.so.1` on Linux) where the ladder starts with NVENC, and opens the encoder as before elsewhere or without the
  driver; `Recording.WarmUp` notes `encoderWarmUp=cuInit`. Through `RecordingService` (warm-up, then a 1280x720
  recording with system audio): the warm-up kept 35-36 MB instead of 220, FFmpeg included, and took 125-132 ms instead
  of 271-273; the first start took 209-244 ms (`encoderOpen` 123-145, six runs) instead of 190-191 (92-95, three); after
  the recording the process was 240-244 MB above where it started instead of 270-286. `LibavRecordingEncoderTests` has
  the opt-in check (NVENC_PROBE=1: `cuInit`, under 40 MB kept). Linux not measured.
- Done since batch 1, the user's pick: **each mosaic or blur preview kept a copy of the whole picture** (from Pins).
  `AnnotationControl` converted the annotation's snapshot into an `SKBitmap` at its first preview and kept it until
  detached, one per redaction on screen, to cut each drag tick's region out of; and its preview followed the control's
  bounds, which a pin's resize changes for every annotation on it. `RedactionPreviewMemoryTests` (REDACTION_PROBE=1,
  headless with Skia: six mosaics and blurs on a 3840x2160 picture drawn at half size, then the controls resized over
  30 frames), twice each: before +214 MB shown, 179-180 redraws, the resize phase 2.15-2.29 s; after +24-25 MB, 0
  redraws, 1.25-1.38 s (most of it the probe's own pumping); nothing grew over three more open-and-close cycles.
  `AnnotationRenderService.RenderAnnotationPreview` now copies only the padded region out of the Avalonia bitmap
  (decompiled: `Bitmap.CopyPixels` with a source rect locks Skia's pixels in place and copies just those rows), and the
  caching overload is gone; `AnnotationControl` redraws only when what the preview is drawn from changes
  (`PreviewReferenceSize`: the size the annotation was drawn in, the control's only for one without). In CI the preview
  matches the whole-picture path pixel for pixel, mosaic and blur, both alpha formats, and copies one region. A video
  pin and the video editor still give each redaction a frame of its own (`GetContentSnapshot`, `CopyFrame`), which it
  needs as the video moves on; only the Skia copy beside it is gone.
- Done since batch 1, the user's pick: **a pin closed by Alt+F4 kept what it held** (from Pins). Only the close
  command disposed an image pin's view model; a video pin disposes its own in `OnClosing`. `BitmapHistoryAction` keeps
  every picture of the undo history in a static table until the history is disposed, so those stayed for the rest of
  the run, and so did a smart-selection lease. `PinCloseMemoryTests` (PIN_CLOSE_PROBE=command|window, headless with
  Skia: a 3840x2160 pin rotated three times, a SAM2 lease taken, then closed), twice each way. Before: the close
  button +38-40 MB left, Alt+F4 +165-167 MB, the lease held and 4 pictures in the table; the view model itself was
  collected both ways. After: both +38-39 MB, no lease, none in the table. `FloatingImageWindow.OnClosed` disposes the
  view model (a no-op after the command), whose `Dispose` asks for an idle trim (`pin-closed`): the picture on screen
  is left to the collector, since a save or copy may still be encoding it, and in an idle app only the trim collects.
  33 MB of what stayed either way was a whole-picture array in the shared pool (next entry: now 6-7 MB). Not done:
  `NotifyActivity` from pins, which calls a waiting trim off without asking for another, so it would keep a closed
  pin's picture longer; and a cap on the undo history by bytes (Pins, the user's call).
- Done since batch 1, the user's pick: **the pin's opaque-pixel scan** (from Pins). At each new picture the window
  copied the whole bitmap into an array from the shared pool, which then kept it (33 MB on the .NET heap at 4K), and
  read its alpha a byte at a time on the UI thread. `OpaqueRegionScanner.Scan(Bitmap)` now reads it a megabyte of rows
  at a time (`Bitmap.CopyPixels` with a source rect) into native memory freed at the end, and finds each row's runs
  eight pixels at a time (`Find`: a pixel read as a word is see-through when it is at most `0x08FFFFFF`). `Find` is its
  own `Vector256` loop compiled optimized from the first call: through the span's `IndexOfAnyInRange`, .NET ran its
  generic search unoptimized until recompiling it, and an opaque 4K scan stayed at 9.5-10.4 ms for the first 24-30
  scans of a process. `OpaqueRegionScanProbeTests` (OPAQUE_SCAN_PROBE=1, 3840x2160, the two scans in turn after five
  warm-ups each, three processes): opaque 2.6-2.7 ms against 7.5-7.7, cut out (an ellipse with a ragged edge, 14,814
  rects) 4.3-4.7 against 8.0-8.8, the first scan of a process 8.4-8.8 against 25.3-25.7, nothing left behind against
  32 MB, and the same rects. `OpaqueRegionScannerTests` checks a bitmap read in three bands against its pixels. It
  stays on the UI thread at these times. `PinCloseMemoryTests` still left 33 MB on the .NET heap after closing: the
  pool's array came from `FloatingBitmapConversionHelper.TryCopyToSkBitmap` too, which every rotation, save, copy,
  flatten and background removal goes through, and the video pin and editor's frames. It now copies straight into the
  `SKBitmap` it returns, and converts for background removal from native memory it frees: a 3840x2160 pin rotated three
  times held +155 MB open instead of +188, and left +6-7 MB closed (+1 on the .NET heap) instead of +38-40. Checked
  while here: reading a WriteableBitmap through `CopyPixels` marks it
  changed (`Lock` bumps its version), so since the mosaic change a preview redraw makes the card take the pin's picture
  again; `RedactionDragProbeTests` (REDACTION_DRAG_PROBE=displayed|copy, a real window on the graphics card, a mosaic
  dragged over a 4K pin) found nothing measurable on the RTX 4070 SUPER: 34 frames/s against 30 when it reads a copy,
  the process at 110-121% of a core against 114-116%. The drag's 30-34 frames/s is the same either way.
- Done since batch 1, the user's pick: **saving an annotated pin swapped its `Image` for the whole dialog** (from
  Pins). The save command flattened the annotations into a new bitmap before the dialog, cancelled or not, set it as
  the pin's `Image` until the dialog returned, and never disposed it (the `Dispose` was commented out). Meanwhile the
  pin drew the annotations twice (the flattened picture under the live annotation layer), the `Image` change reset
  smart selection (`HandleImageChangedForSam2`: its prepared picture and points), and the window rescanned for
  see-through pixels at each swap. The flattened copy took three full copies: the picture into Skia, back into a
  WriteableBitmap, and into Skia again to encode. Now `SaveImageToPickedFileAsync` asks `EncodeForSaveAsync` once a
  file is picked: one Skia copy, the annotations drawn into it, encoded (`TryEncodeSkBitmap`, split out of
  `TryEncodeBitmap`) and disposed. `FloatingImageViewModelTests`: a cancelled save changes `Image` 0 times (it was 2), and
  the saved PNG has a filled annotation's red where the pin's own picture stays white. Not timed: a 4K PNG encode at
  full compression (over 2 s) dwarfs the copies.
- Done since batch 1, the user's pick: **a pin's copy encoded the PNG twice** (from Pins). The view model encoded the
  file in the toolbar's format, then `ClipboardService.CopyFileAndImageAsync` encoded a PNG again for the picture beside
  it (and decoded that through GDI+ for CF_BITMAP, which stays); an annotated pin made a flattened bitmap and a
  selection a cropped one first, neither disposed. Now `EncodeForCopy` makes one Skia copy (of the selection alone when
  nothing is drawn on it), draws the annotations in, cuts the selection out with `ExtractSubset` (no copy), encodes the
  fast PNG once and the file again only for JPG or WebP, and `IClipboardService.CopyPngAsync` takes those bytes: on
  Windows as before from them; elsewhere, and when the STA write fails with no file, Avalonia's clipboard gets a bitmap
  decoded for it, since it hands pictures out when they are pasted. Neither path holds a bitmap of the caller's, and the
  STA thread, which can outlive its timeout, holds only the bytes. A 3840x2160 picture of 8x8 colour blocks (a 1,070 KB
  PNG), seven runs twice: 137-139 ms of encoding before, 68-69 after; the code's own note had 0.33 s for one fast PNG
  of a real 4K screenshot. `SelectionPixelRect` now does the crop's maths for both the copy and `GetSelectedBitmapFromAsync`.
  A rotation also disposes the flattened copy it draws from. `FloatingImageViewModelTests`: the file holds the
  clipboard's PNG bytes; an annotated pin's selection comes out the selection's size with the annotation in it.
- Done since batch 1, the user's pick: **the frozen screenshot's grab** (from Snip overlay). Screenshots freeze by
  default (`FreezeScreenOnScreenshot`). The user's log, mode=Normal: warm opens `freezeGrab` 47-88 ms of a 89-140 ms
  total, a run's first 85-446 of 539-1014 (`window` 191-215 and `viewModel` 119-150 before it), once 2,033 of 3,000+.
  The grab waited for the window, services and view model, then went through GDI+ `CopyFromScreen` (a temporary DIB
  copied back into the Bitmap), `LockBits` and a row copy into Skia. Now `SnipWindowFactory.Open` starts it first
  (`StartDesktopGrab`: the area worked out as `ConfigureWindowBounds` does, from the screens the main window or the
  tray host already knows) and, at the old point, takes it when the window's own area is the same and grabs that area
  as before when not (`earlyGrab=used|unused|none` in `SnipOpen.Timings`); and `WindowsScreenCaptureService` grabs
  with `BitBlt` into a DIB section and one vectorized copy that makes every pixel opaque (GDI does not promise the
  fourth byte), the cursor drawn with `DrawIconEx` as GDI+'s `DrawIcon` did. `FreezeGrabProbeTests` (FREEZE_GRAB_PROBE=1,
  the real 3968x1160 desktop, the two in turn, five processes): medians 41-58 ms against 56-73; a still window of the
  probe's own read the same by both, 60,000 of 60,000 pixels (the busy rest of the screen differed as much between two
  grabs of one kind as between kinds). Not measured: a whole open; the log will show it.
- Done since batch 1, the user's pick: **a frozen overlay held the desktop twice** (from Snip overlay).
  `OverlayBackdrop` kept the grab (`Pixels`, for crops, pins and OCR) beside the WriteableBitmap it shows (`Image`,
  `DefaultToDisplay`), and `FreezeFromPreOverlayGrab` made that copy on the UI thread (6 ms at 3968x1160, 16 the first
  time). Every reader of `Pixels` only reads (crops draw it into a new bitmap; OCR takes a `Copy()`), so `TryCreate` now
  makes `Pixels` a view of the shown bitmap's own pixels (`InstallPixels` at the address its `Lock` gives once; it stays
  put while the bitmap lives, and reading the view takes no lock, which would mark it changed) and disposes the grab;
  `Dispose` disposes the view first. `SnipWindowFactory`'s grab task makes the whole backdrop on the pool, and the
  surface adopts it (`FreezeFromPreOverlayGrab(OverlayBackdrop)`). `FreezeGrabProbeTests` on the real 3968x1160 desktop,
  twice: a frozen backdrop holds 18.9 MB (one picture is 17.6) against 35.2 for the grab beside its copy.
  `OverlayBackdropTests` checks the view is the shown bitmap's memory and the grab is gone; `OverlaySurfaceTests`
  that a made backdrop is adopted, or disposed outside a screenshot. Reading the picture at every redraw needs it
  alive, and the old copy had hidden that it often was not: the capture overlay disposes its drawing snapshot on
  leaving drawing mode (Esc, or the tool clicked off) and grabs another on coming back, while the redactions drawn
  keep pointing at the old one; a pin's oldest image leaves its undo history disposed. Now the overlay points its
  mosaics and blurs at each new snapshot and at none while it has none (`AdoptDrawingModeSnapshot`,
  `DrawingModeSnapshotTests`), and the control keeps its preview as drawn on `ObjectDisposedException` (the probe drags
  one whose picture was disposed). By reading, the old code threw from `OnDataContextChanged` for a control made over
  a disposed picture, as undoing a redaction's deletion after leaving drawing mode would; that one now has no preview.
- Done since batch 1, the user's pick: **history thumbnails decoded for every entry and never released** (from Idle,
  start-up and memory). `LoadHistoryAsync` started a decode for every new entry, both kinds, and the view models
  outlive the window. `SettingsHistoryTabTests` (HISTORY_TAB_PROBE=1, headless with Skia, 300 entries with 320x180
  thumbnails, a 1100x900 window), twice each: before, 300 decoded with the tab open and 300 still after leaving it,
  private bytes 41 MB before opening, 128-131 MB open and after leaving. Now `HistoryItemViewModel.ShowThumbnail` and
  `HideThumbnail` count the cards showing an entry, the first decodes (on the pool, two at a time, nothing if the card
  scrolled away before its turn) and the last disposes; `SettingsHistoryTab` calls them from the rows host's
  `ContainerPrepared` (the row taken by index: the panel registers the container only after the event) and
  `ContainerClearing`, and for every row built as the tab enters or leaves the window. After: 12 decoded open, at most
  16 while scrolling to the end, 0 after leaving, 12 back on it, 0 once the window closed; 60-61 MB with the tab open.
  The thumbnails are still decoded at their file's size (at most 320 wide): at 12-16 that is about 3 MB, not worth the
  card-width decode the item suggested. A hidden main window keeps its tab, so the cards on screen keep theirs until
  the window is let go of five minutes later.
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
- Done since batch 1: **the UiStall monitor's idle wakeups**. The item guessed 14 a second; the user's app on the old
  monitor, settings window open, woke 84 times a second, 77 on the UI thread and 7 on the watchdog, and no other
  thread woke at all: the 100 ms `DispatcherTimer` cost 8-11 UI-thread wakeups a tick. The watchdog thread now posts
  one probe at a time every 100 ms (`UiStallProbe`), and how long it waits is the stall, as the tick's lateness was.
  `UiStallMonitorIdleProbeTests` (STALL_MONITOR_PROBE=1: real Win32 Avalonia, each thread's context switches and
  cycles after a 30 s warm-up), no window: UI thread + watchdog 84-113 + 5-6 wakeups a second before, 19-21 + 16-18
  after; about 40% fewer cycles, from about 1 ms of CPU a second. Any post from another thread costs the UI thread about
  two wakeups here, even a bare `PostThreadMessage`, and no poster priority changed that; at Background priority, as
  the timer ran, about 2.6, so probes go at Default, the hotkey press's own. Six 600 ms holds read 531-615 ms before and
  507-599 after. Stop/`Pause` ends a probe generation, so a watchdog caught mid-beat posts nothing that would wait out
  the UAC prompt. The user's logs since 2026-10-26 hold no `UiStall` over 10 s: no sign of the false stall across Modern
  Standby the item guessed. Left: fewer probes while no window is shown, at the cost of precision when a hotkey arrives
  in the tray. The probe also found that `Dispatcher.MainLoop(token)` sleeps through a token cancelled from another
  thread (10 min and 2 h 44 min, until a UAC prompt); the three probes' `PumpFor` cancel from the UI thread now.
- Done since batch 1: **a copy from the overlay encoded its PNG twice** (from Snip overlay, the copy half of "Frozen
  commits keep the overlay up"). History is on by default, and a copy encoded the picture at full compression on the
  clipboard's STA thread, decoded it through GDI+ for CF_BITMAP, then encoded it again into the history's file, all
  before the capture ritual closed the overlay, which a frozen screenshot (the default) keeps on screen meanwhile.
  `SnipCopyProbeTests` (SNIP_COPY_PROBE=1, the real 1920x1080 desktop of the Ryzen 5 7535HS laptop, eight runs of
  each in turn, medians, two screen contents): before 342 + 28 + 350 ms and 124 + 10 + 125; now one fast PNG (zlib 1,
  Sub, as pins' copies use) 73 + 20 and 25 + 8, the history's file written from the same bytes in 1-2 ms. The PNG is
  17% and 30% larger (1226 to 1438 KB, 585 to 762). `ExecuteCopyCaptureAsync` now encodes on the pool and hands the
  bytes to `IScreenCaptureService.CopyPngToClipboardAsync` (it took an `SKBitmap` and encoded it on the STA thread, or
  on Linux on the UI thread), and `KeepCopyInHistoryAsync` writes them and adds the entry without the overlay waiting.
  `SnipWindowViewModelTests` hold the clipboard's PNG and the history's to the same bytes and the close to not wait;
  SNIP_COPY_PROBE=clipboard writes to the real clipboard and reads back the PNG and the bitmap, and was not run (it
  replaces what the clipboard holds). Save still encodes the user's file at full compression with the overlay up.
- Done since batch 1: **a compress to GIF or WebM ignored a cancel during its transcode** (the progress and cancel half
  of Compress's "GIF/WebM output" item). `EncodeGifWebmAsync` handed the intermediate the whole progress and the
  transcode none, and the transcode only had the token on its `Task.Run`, so a cancel during it ran it to the end and
  returned true. `CompressPipelineGifWebmTests` (opt-in COMPRESS_GIFWEBM_PROBE=<clip>, the laptop, the tab's defaults:
  CRF 22, medium) on two screen recordings: the intermediate took 41% of a 4.2 s clip's GIF (3.5 of 8.6 s) and 31% of a
  30.8 s one's (9.5 of 30.9), and 24% (3.1 of 13.1) and 12% (6.3 of 53.6) of their WebMs. Now the intermediate covers
  `GifIntermediateShare` 0.3 or `WebmIntermediateShare` 0.15 of the bar and the transcode the rest, both through a
  `StageProgress` that reports on the stage's thread, the transcode gets the token, and a cancel after it still fails the
  file. `LibavWebmTranscoder.TranscodeToWebm` reports by source time and checks the token before every packet, as the
  GIF transcoder does, and deletes the WebM it started when it does not finish; `GifWebmVideoExporter` gives the VP9
  video 95% of its part and renames the video-only WebM instead of copying it. The tests run the real pipeline on tiny
  clips and tell the transcode is under way by its file in the pipeline's temporary folder, and all four fail against
  the old pipeline. Not done: the transcode still ignores Pause, the recording's and the pin's GIF and WebM still show no
  progress during it, and an unedited source still gets the intermediate.
- Done since batch 1: **a pinned video's WebM export ran its audio step on the UI thread** (Pins' "WebM export from a
  video pin" item), and **a failed WebM audio mux encoded the recording's video again** (Recording). The pin decoded the
  whole track into one array with `LibavPinAudioPcmDecoder.Decode` and wrote the WAV on the UI thread after its VP9
  `Task.Run`, probed the fps there too, and left the cut's MP4 folder behind, and its own after a failure. It now goes
  through `GifWebmVideoExporter` in one `Task.Run` (`FloatingVideoViewModel.TranscodeGifWebmAsync`), which deletes both
  folders as due; the exporter writes the WAV as it decodes (`WriteAudioAsWav`, through `DecodeStreaming`) and deletes
  the WAV and the video-only WebM after the mux, so a copy's export folder, kept for the clipboard, holds only the file.
  GIFWEBM_AUDIO_PROBE=<clip> on four of the user's recordings (4-33 s of audio, 0.8-6.3 MB of WAV), three runs each in
  one process: the in-memory step allocated 3.5-3.9 times the WAV on its first run (89-108 ms, with the process's first
  decode) and about the WAV's size on later ones (21-31 ms: the array pool had kept its growing buffers); as it decodes,
  0.1 MB and 22-40 ms. `RecordingService.FinalizeAsWebmAsync` now moves the video-only file when the mux fails instead
  of encoding the video again; `RecordingWebmFinalizeTests` pin the file that comes out but cannot tell one encode from
  two. Correction to the "GIF/WebM output" item: the recording's and the pin's GIF and WebM have no progress to show,
  since every progress window's bar is indeterminate (`ProcessingOverlay.axaml`) and nothing reads
  `RecordingService.FinalizationProgress`; a bar that fills changes its look, the user's call.
- Done since batch 1: **OCR read every small line twice, and up to four times on a dark background** (the gating half of
  OCR's "Recognition runs up to four passes per box" item). `PaddleOcrRecognitionProbeTests` (opt-in
  OCR_RECOGNITION_PROBE=<folder of PNGs>, AI_MODELS_DIR, OCR_RECOGNITION_OUT=<file>) writes each box's text and
  confidence and the time recognition took; run on the 50 captures in the user's history (610 boxes, the installed
  Traditional Chinese module, the laptop), with the policies switched in one process: the old passes ran 2048 times (3.4
  a box) in 20.2-22.2 s. Gating the extra passes on the average character probability alone, at 0.9, ran 1142 and halved
  the time, but 18 boxes (21 characters) read differently, more of them worse than better (USB for USD at 0.91-0.93);
  0.95 ran 1416 and changed 6. The decode now also reports the least certain character (`DecodeCTC`), and
  `IsConfidentReading` stops at an average of 0.9 with no character under 0.6, keeping the old order and conditions
  otherwise: 1397 passes, 26% less time (16.3 against 22.1 s; 14.0 against 21.6 s in separate processes), and one box
  changed, `Racing)` read as `Racing>`, which the capture does not settle. A weakest character of 0.5 changed 7 boxes,
  0.4 changed 14. Reading a dark crop inverted first, as the item suggested, ran more passes, not fewer (1433 against
  1142 at 0.9): the model reads light text on dark well. Batching boxes by width is still open.
- Done since batch 1: **the OCR search's whole-screen copy on the UI thread** (OCR's "Full-screen resize" item, the
  off-the-UI-thread half) and **two-pass pass 1's `pass1.mp4`** (from Compress's rotation item). The search and Scan All
  made the 1600 px Mitchell copy of the grab on the UI thread before the detection's `Task.Run`, and a translated
  selection over 1600 px likewise; `DetectWholeScreenTextAsync` and the selection's own `Task.Run` now make it on the
  pool. `SnipWindowViewModelTranslationBitmapTests` (opt-in OCR_SEARCH_RESIZE_PROBE=1, the laptop): 33.4 ms median at
  1920x1080, 34.6 at 2560x1440, 17.6 at 3968x1160, 33.6 at 3840x2160 (the copy's size sets the cost).
  `LibavClipExporter` sends a two-pass encode's first pass to FFmpeg's null muxer, as `ffmpeg -pass 1 -f null` does;
  `LibavClipExporterTwoPassTests` fails against the old exporter, and its opt-in TWOPASS_PROBE=<clip> gave the same
  files before and after on two recordings (2.82 and 2.95 MB for 3 MB targets, 6.2 and 3.2 s). Also struck as stale:
  "`GimmeCapture_*` folders in %TEMP% are never swept after a crash", since `TempWorkspaceCleaner` deletes every
  `GimmeCapture_*` folder older than 12 h at start-up.

## Next session: what the app itself holds (left off 2026-10-30)

The user asked to bring down what the app really holds: Task Manager's commit size (Details tab, Commit size column),
not the Memory column the working-set trim used to empty. Done so far: OCR on the CPU and the Record tab's AV1 check
on Direct3D 11 (Status), then, in a second session on 2026-10-30, the hand checks of both and the breakdown of what the
idle app holds (Measured 2026-10-30, below). Pick up in this order:

1. ~~**Choose with the user what to do about the GPU's share**~~: done 2026-10-31, A as a setting, off by default (the
   user's choice), and B and C are done (B gave back less than hoped, see there); the pin-resize stalls are fixed
   (Status).
2. ~~**DirectML after background removal and smart selection**~~: done 2026-10-31, both on the CPU, and the DirectML
   package swapped for the CPU one (Status).
3. **NVENC** only if the user wants a child process: nothing in-process helped (under Idle, start-up and memory). The
   Record-mode warm-up's 190 MB with nothing recorded is done (Status).

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
  decide. The magnifier was not measured (it follows the real pointer). Done 2026-10-31 as that setting (Status).
- **New lead from the same runs: resizing a pin stalls the UI thread.** Both renderers logged `UiStall` warnings of
  0.3-1.5 s, one after another, while the pin was resized every frame: with the GPU the thread waited (about 20% CPU),
  in software it ran a dozen or more full GCs a second, a new bitmap per size. Check by hand by dragging a pin's
  corner quickly; it fits "Resizing rebuilds the window region 4-6 times per pointer move" under Pins. Fixed
  2026-10-31 (Status).
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
- **First recording of a run**: entering Record mode now warms FFmpeg, the encoder (on NVIDIA only the CUDA driver),
  the audio endpoint and the WGC check up in the background (see Status). After a launch, the log should show one
  `Recording.WarmUp` line (`encoderWarmUp=cuInit` on NVIDIA) and the first `Recording.StartTimings` line (`n=1`) within
  about 50 ms of the later ones; entering Record mode and leaving without recording should leave the commit size about
  35 MB higher, not 220.
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
- **Background removal and smart selection memory** (a published build): with Task Manager's Commit size column
  showing, remove the background of a photo-like pin, and use smart selection on a pin with a few clicks. Each should
  add about 200 MB at most while in use, not 1-3 GB, and the commit size should come back down: within about ten
  seconds of a removal, and as smart selection is left. The log shows `BackgroundRemoval.SessionCreated: CPU` and
  `Sam2Runtime.Loaded: Tiny on CPU`. Smart selection's first mask should come about as soon as before; a second pin
  takes about a second longer to get ready.
- **DirectML.dll after an update**: update an install whose folder has `DirectML.dll` (0.70.0 or older) with the
  in-app updater. A few seconds after the new version starts, the file is gone and the log's `InstanceStorage.Sweep`
  line counts it (17 MB); OCR, background removal and smart selection all still work.
- **Pin resize**: drag a pin's corner quickly, on an ordinary pin and on one whose background was removed, and with
  Shift held. The pin should follow the mouse smoothly, end at the size where the mouse let go, undo in one step, and
  still let clicks through a removed background's transparent parts at its new size. The log should show no `UiStall`
  line during the drag. The probe stood in for the mouse; a real drag was not measured.
- **Use less memory (software rendering)**: turn it on in the General tab and accept the restart. The app should come
  back with the settings window open, and the log show `Startup.Renderer: software`. Then check that everything still
  looks as it does on the GPU, which nobody has looked at in software: the capture overlay (dimming, selection,
  magnifier, toolbar), a frozen capture with text boxes, an ordinary pin and one whose background was removed (its
  transparent parts see-through and clickable), a video pin, the recording frame and toolbar, a translation overlay,
  toasts and the countdown. Dragging a selection, playing a video pin and resizing a pin should stay usable (the probe
  resized a pin at 29-46 frames a second), and Task Manager's commit size should sit lower than with the setting off.
  Turning it off offers the restart again and brings back `Startup.Renderer: GPU`; turning it on and off again without
  restarting asks nothing the second time. Partly checked by the user on 2026-10-31 (desktop, 100%): the restart came
  back in software (log), a pin's black band was found and fixed (`582a7cfb`), and after that no black around pins,
  their wings, the toolbars' corners or the menus, and a background-removed pin is see-through. The rest is still owed.
- **Run as administrator at start**: with the setting on, start the app normally and accept the UAC prompt. No
  "Application Startup Failed" dialog should appear, and the log should show `Elevation.RelaunchAtStart: handed over to
  the elevated copy.` with no `Program.Startup` error after it. `AppShutdownBeforeStartTests` (STARTUP_EXIT_PROBE=direct
  or posted) shows the cause and the fix in a headless app; the update hand-over takes the same path and was not tried.
  Checked 2026-10-31: the user's next start with the setting on logged the hand-over and no error after it.
- **The glow and the checkerboard, on the GPU too**: they are drawn a new way in both renderers (`GlowDecorator`,
  `TransparencyCheckerboard`). The glow around a pin, a video pin, the capture frame and the General tab's style
  preview should look as before, follow a pin's opacity slider, and go with "hide decoration" or "hide border"; a
  background-removed pin still shows the checkerboard behind its soft edges. Compared by a test, not by eye.
- **A frozen screenshot**: take a few screenshots, one with a right-click menu or the tray's flyout open. The frozen
  picture should show them as before, sharp and in place on both screens, and the log's `SnipOpen.Timings` lines
  should say `earlyGrab=used` with a smaller `freezeGrab` than the 47-88 ms before. A selection copied or pinned from
  it, and the text boxes OCR finds on it, should match what the frozen picture shows.
- **Copying a pin**: Ctrl+C on a pin with annotations, with and without a selection, in PNG and in JPG. Paste into
  an image editor and into a folder: both should have the annotations, cut to the selection, and a PNG with a removed
  background should keep its transparency in an editor that takes the PNG format (Chrome, Discord).
- **Saving an annotated pin**: draw a highlighter and pick smart selection's points on a pin, then Save and cancel the
  dialog. While the dialog is open the highlighter should not turn darker, and after it the points should still be
  there. Saved for real, the file should have the annotations.
- **A pin's click-through after a change**: remove a pin's background, then rotate and crop it, undo and redo. Clicks
  should still go through its see-through parts and land on its picture, as before.
- **A pin closed with Alt+F4**: rotate a pin or pick smart selection on it, then close it with Alt+F4. The commit size
  should drop as it does for the close button, and about five idle minutes later the log should show a
  `MemoryTrim.Before.idle:` line naming `pin-closed`.
- **Mosaic and blur**: draw a few on a pin and in the capture overlay, drag and resize them, change their strength,
  then resize the pin. The previews should look and follow as before; on a 4K screen the commit size should no longer
  climb about 32 MB with each one.
- **Esc on a frozen capture**: select a region, press Esc. The text boxes should come back at once, and the log should
  hold one `OcrScan.Detected` per capture, not one more for each Esc.
- **A capture after a long idle**: leave the app alone for an hour or more while the computer is in use, then press a
  capture hotkey. `SnipOpen.Timings` should be close to the usual ~0.1 s, with no multi-second `UiStall`, and no more
  `MemoryTrim.WorkingSet*` lines. Only one such press has been measured, before the change.
- **Compress tab**: open it with the long queue: no `UiStall`, and scrolling the queue shows each file's own preset and
  category. Pick a category for a file, exit from the tray, start again: the category is still there.
- **Copying a screenshot** (history on, as by default): select a large part of the screen on a frozen screenshot and
  press Ctrl+C, then the same on a live one. The overlay should go about as soon as the key is pressed, and the toast
  show the thumbnail. Paste into Paint (it takes the bitmap) and into Chrome or Discord (they take the PNG): both
  should show the capture. The History tab should have the copy, and it should open, copy and pin from there as before.
- **Compressing to GIF or WebM**: queue a recording of half a minute or more with GIF as the output and start it. The
  bar should keep moving to the end instead of sitting at 100% for most of the time. Start it again and cancel past
  the middle: the file should end as cancelled, not done, with no GIF in the output folder. Then the same with WebM.
- **Exporting a pinned video as WebM**: pin a recording with sound, cut a piece out of it, then Save as WebM and Copy as
  WebM. The pin should keep playing smoothly while it exports, both files should have the sound, and the temp folder
  should keep no new `GimmeCapture_Export_*` folder after the save (the copy keeps one, holding just its WebM).
- **Reading text**: Quick OCR and translate a light window and a dark one (a dark-themed editor, say). The text should
  come out as before, a little sooner. In Translate mode, the text search's boxes should land on the text as before,
  on each screen.

## How to run a batch

Six `isolation: "worktree"` agents on disjoint file sets, each told: no commits, no CHANGELOG/csproj/lock files, copy
`src/GimmeCapture/ffmpeg-lib/*.dll` into its worktree, targeted tests with TRX output, report commit messages, hunks
and a CHANGELOG bullet per change. Apply every diff to main's working tree, add follow-ups, run `verify.ps1`, then
split into commits with a plan file and replay script, dry-run on a temporary index first. The method and its traps
are in the assistant's memory note "integrating-parallel-agent-worktrees".

## Backlog

### OCR, AI and translation

- **Recognition runs one box per run** (M batching). The passes are gated now (Status). Callers loop one box at a time
  (`QuickOcrService.cs:66-74`, `TranslationService.cs:153-160`) though the recognizer's batch dimension is dynamic:
  batch boxes by width bucket, padded to the bucket's width, and decode each row of the output.
- **Every LLM request builds a new context and re-reads the whole prompt** (M). `LlamaSharpTranslationEngine` uses
  `StatelessExecutor`, which creates a context (KV cache for 2048 tokens) and prefills from zero per call. Fix: one
  long-lived context, the per-language-pair instruction prefix decoded once and forked per request
  (BatchedExecutor/Conversation), blocks batched into one prompt. Measure first; no load-time log exists yet.
- **Auto re-probes the language in every overlay** (S, the user's call). `OcrScriptDetector.cs:85-112`: keep the Auto
  verdict app-wide. Each probe swaps recognizers, which now costs ~135 ms each (the shared detector stays loaded). Only
  matters with several OCR languages installed, as the user has (Auto, with Simplified and Traditional Chinese). The
  detector keeps its verdict for one overlay on purpose: kept app-wide, a capture in the other script (a Simplified
  Chinese page after Traditional ones) would be read with the wrong model until something re-probes.
- **11 MB input tensor allocated per scan** (S). `PaddleOCREngine.cs:78`. Reuse belongs in `OcrRuntimeService`.
- **Two resizes per OCR search** (S). The search copies the grab to 1600 px (Mitchell, now on the pool: Status), then
  `DetectText` resizes again to 1280. One would do, but it changes what the detector sees: compare the boxes found on
  real captures first.

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

### Compress and the editor

- **Every editor or compare step reopens the file** (M). `LibavVideoFramePlayer.cs:567-582` (`DecodeFrameAtAsync`:
  open, find_stream_info, new D3D11VA device, seek, decode), called from `VideoEditViewModel.cs:227,254,650,686` and
  `CompareViewModel.cs:428,442`. A cold open logged 279-293 ms; compare pays it twice; rotate, crop toggles and
  split/merge re-decode an unchanged frame. Fix: one long-lived decoder per view, and cache the last BGRA frame.
- **GIF/WebM output: an extra encode** (S-M). `CompressPipeline.EncodeGifWebmAsync`: an H.264 CRF 18 intermediate at the
  user's preset even with no edits; transcode the source directly when nothing is edited. Compress's progress and cancel
  are done (Status); still open there: Pause during the transcode. The recording's and the pin's GIF and WebM show the
  indeterminate bar every progress window has (the user's call: a bar that fills changes its look; the transcoders can
  report).
- **A target size at or above the source's size still encodes** (S). `CompressBatchRunner.cs:116-124`: stream-copy
  with `LibavMuxer.RemuxAllStreams` when unedited, otherwise cap at the source bitrate. Audio-only M4A from an AAC
  source can stream-copy too.
- **Segmented CRF path writes the video four times** (M). `CompressPipeline.cs:462-523`, `LibavClipExporter.cs:224-227`:
  each 30 s chunk reopens and probes the input and copies its output; finalize concatenates, builds the audio after the
  bar shows 100%, then remuxes. Write chunks in place, concatenate straight into the final mux, build audio alongside.
- **Rotation round-trips every frame through BGRA** (S-M). `LibavClipExporter.cs` (~338, 913-956): sws to BGRA, Skia
  rotate, sws back. Use transpose/hflip,vflip in the YUV graph or display-matrix rotation; render static annotations
  once.
- **Small ones**: every CRF-slider tick saves `compress-session.json` on the UI thread
  (`MainWindowViewModel.VideoCompress.cs:552-559`; a small file, written without a flush, and a delayed save could lose
  the last change at exit: measure before changing it); preview frames are decoded on the GPU, downloaded at full size
  and scaled on the CPU.

### Pins

- **A video pin reopens file, GPU device, decoder and audio at every loop and seek** (M-L).
  `FloatingVideoViewModel.Media.cs:279-313,371-373` (`loopPlayback = false`, a new `PlayFramesAsync` per pass),
  `LibavVideoFramePlayer.cs:670-718`; `UpdateAudioStateFromPlayback` reopens Media Foundation and WASAPI too. Keep one
  decoder per pin, do loops and seeks as requests to it, seek the audio on loop; 1-2 decode threads under D3D11VA.
- **Each pin builds its own U2Net session** (S-M). `FloatingImageViewModel.AI.cs:684-687`: `new
  BackgroundRemovalService` per pin; sessions take 661-962 ms. One shared service owned by `AppBootstrapper`.
- **Each copy from a pin writes a `GimmeCapture_Copy_*` folder** (S, disk), swept only at start-up after 12 h, each
  holding the copy's file (1 MB for a 4K picture of colour blocks as a fast PNG; not measured for photos). Deleting the
  last one at the next copy would break a paste of the older file from Win+V's history. The rest of that item is done
  (Status).
- **A pin's undo history is capped at 30 steps, not bytes** (S, the user's call): up to ~1 GB at 4K while the pin is
  open (`AnnotationEditorState.cs:150`). A cap by bytes means fewer undo steps on large pictures. What a closed pin
  kept is done (Status).
- **Lower**: video pins decode at full size and copy each frame twice (`LatestFrameHandoff.cs:40`, then
  `FloatingVideoViewModel.Media.cs:564`, 33 MB per frame at 4K) and keep decoding when covered;
  `ProcessingOverlay.axaml:87` keeps `IsIndeterminate="True"` (a 60 Hz animation after the first AI job? measure);
  the translation layer adds results one at a time, rebuilding its region per item (`FloatingTranslationWindow.axaml.cs:
  109-117`).

### Snip overlay

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
- **Frozen commits keep the overlay up while the pin window is built** (M). `Capture.cs:464`: hide the frozen overlay
  right after the crop. (Explains the 307 ms "blocked" stall before close.) The copy's second encode is done (Status);
  a Save still keeps a frozen overlay up through its full-compression encode, 0.35 s for a 1920x1080 screen, which the
  user's own file should keep.
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

- **Cinzel lacks ▶ ◀ → ⚠ ✓** (S), so English falls back through the JP chain for those (found with the CJK font item,
  done: Status). Since the CJK fonts are read in place, that fallback costs no copy either.
- **The raw-input sink duplicates the keyboard hook** (S). `WindowsGlobalHotkeyService.cs:488,537-577`: about four
  hotkey-thread wakeups per keystroke typed elsewhere. Register the sink only when the hook fails or stops counting.
- **A launch at sign-in takes 67-77 s until the hotkeys work** (the user's pick on 2026-11-01; read the next one's log
  before changing anything). The user's log has three, 2026-10-30 and -31, 0.70.0 launched to the tray by the Run key:
  `beforeMain` 30.4-37.5 s and `avalonia` 34.3-41.7 s, against 0.06-0.22 s and 0.29-1.4 s at the other launches since
  2026-10-30; from process start to working hotkeys the others took 0.5-2.1 s, and 5.6-7.2 s at the slowest. The first
  `UiStall` after each sign-in, 2.4-2.5 s from `pendingUpdate` until the tray icon is up (`viewModel` alone 1.6-1.9 s,
  0.08-0.17 s at other tray launches), ran at 9-10% UI-thread CPU: waiting, not working. The app runs from
  `D:\GimmeCapture_win-x64` (332
  files, 511 MB, ReadyToRun), and D: is a hard disk; C: is NVMe. `Startup.Timings` now has every stage as `wall/cpu`,
  Main's own work as `log` and `guard`, `avalonia` split into `platform`, `reactiveUI`, `fonts`, `xaml` and
  `afterSetup`, and on Windows `systemCpu`, `hardFaults`, `disk` and a lowered `priority`/`io` (`StartupWaitCounters`).
  The installed copy logs them only once it runs a build that has them. Then: a long stage with many hard faults and a
  low `systemCpu` waited on the disk (read less at start, or install on the SSD: the install folder names the settings'
  instance, so a copy elsewhere starts with its own settings and AI modules); a high `systemCpu` means a busy machine;
  few hard faults point at something else, such as the font cache service or the graphics driver. On a warm start
  `xaml` holds what `avalonia=` (~375 ms) was blamed on: the whole ColorPicker theme for one `ColorSpectrum`
  (`App.axaml:60-68`) and all three locale files parsed through `x:Static`.
- **Tray sessions still miss** the failed-hotkey-registration dialog and the overlay's request to open the Modules tab;
  an update found there waits for the main window, because `UpdateDialog.ShowDialog` cannot open centred on screen.
- **Two AV1 probes can run at once** (S): `HasUsableHardwareAv1Encoder` does not guard against the Record tab, the
  Record-mode warm-up and a recording start probing together (`LibavRecordingEncoder`).
- **NVENC's driver keeps 200-260 MB once a recording has used it** (L: a child process is the only fix found). Release
  tests at 3968x1160, 120 frames, three encoders a process: 208-266 MB on frames in memory (CUDA), the same on CUDA
  with 4, 8 or 16 `surfaces`, 198-256 on Direct3D 11 frames with a device each, and one Direct3D 11 device kept across
  encoders grew to 456 MB. Only an encoder that encodes nothing gains from Direct3D 11 (the AV1 check, done). Direct3D
  11 frames also cost a recording start the device, 140 ms on the default adapter and 300-330 with `vendor_id`, against
  a CUDA open of 110-180 ms; with a kept device an open took 7-17 ms. The Record-mode warm-up no longer opens NVENC,
  only starts the CUDA driver, so entering Record mode without recording keeps about 8 MB of it, not 190 (Status).

## Suggested batch 2 split

Disjoint file sets for parallel agents:

1. OCR recognition passes, the Auto verdict, per-scan buffers (`OcrRuntimeService`, `PaddleOCREngine`,
   `OcrScriptDetector`, `QuickOcrService`).
2. Recording: composite WGC, WGC readback pacing, BGRA into hardware encoders, finalize, the Linux audio gate.
3. Compress: persistent editor/compare decoder, GIF/WebM intermediate with progress and cancel, stream copy at or above
   source size, rotation in YUV, segmented path.
4. Pins: opaque scan, persistent video decoder, shared background removal, save/copy paths, close and undo memory,
   pin WebM audio.
5. Overlay: frozen open, OCR double scan and cache, candidate render control, hover timer, record meter, scroll buffers.
6. Idle and memory: UiStall pause, history thumbnails, raw input, ColorPicker/locales.

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
