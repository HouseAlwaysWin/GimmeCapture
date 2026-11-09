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
- Released as v0.74.0 on 2026-11-06 (13 entries), after v0.73.0 on 2026-11-05 (17 entries), v0.72.1 on 2026-11-04
  (2 entries: a pinned recording's seamless loop and its frame rate), v0.72.0 the same day (17 entries) and v0.71.0 on
  2026-11-03: `release.ps1` after the `docs/catalog/releases.md` entry went in as its own commit; `## Unreleased`
  starts over, and the CHANGELOG has none until the next change adds it. A tag pushed by hand fails
  `verify-release-metadata`: v0.71.0's first one did, on 2026-11-02, and was deleted, as nothing had been published
  under it.
- Since v0.74.0, `## Unreleased` holds 42 entries (2026-11-09).
- The installed copy on the user's desktop (`D:\GimmeCapture_win-x64`, instance `b0337ff88a000ffc`, the machine this
  backlog's sign-in figures come from) runs `0.74.0+1f7e84fd`, installed by hand on 2026-11-08 in the evening over
  `0.74.0+36c839ad` (by hand, that afternoon), started at sign-in by the launcher its Run value names. It draws on the CPU: its config has
  `SoftwareRendering` true since 2026-11-05 20:16, and every start since logs `Startup.Renderer: software`.
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
  Standby the item guessed. Left then: fewer probes while no window is shown, at the cost of precision when a hotkey
  arrives in the tray; done 2026-11-06 without that cost ("the stall monitor in the tray"). The probe also found that
  `Dispatcher.MainLoop(token)` sleeps through a token cancelled from another thread (10 min and 2 h 44 min, until a UAC
  prompt); the three probes' `PumpFor` cancel from the UI thread now.
- Done since batch 1: **the first Record press of a run waited for FFmpeg**. The user's log, 2026-11-02 21:16, three
  idle hours after the start: `SnipOpen.Timings mode=Record waitedForUiThread=2927 ... show=1428`, no `UiStall` in
  those 2.9 s, then one of 1.9 s blocked at 11% CPU across `show`; the next press, 4 s later, took 44 ms in all.
  `StartCapture` awaited `IsFFmpegAvailable` on the pool before asking for the overlay, and on a run's first Record
  press that loads FFmpeg (~150 MB from the hard disk the app runs from), though the record button checks it again
  anyway. The overlay no longer waits for it, and the Record-mode warm-up, which loads FFmpeg first, is posted at
  Background priority so that it starts once the overlay is on screen (`MainWindowViewModelCaptureSaveTests` covers
  Record now). Not explained: that overlay's `show=1428`. `UiStall` lines now end `page faults +N` (`ProcessPageFaults`)
  to tell waiting on pages from waiting on anything else next time.
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
- Done since batch 1: **every editor or compare step reopened the file** (Compress and the editor).
  `LibavStillFrameDecoder` keeps the file, the decoder and its D3D11VA device open between the paused frames the editor
  and both sides of the compare show: a frame after the decoder's is reached by decoding on unless the file's index puts
  a keyframe between (a seek would start there), the frame last given comes back for the same time without a decode, and
  the file closes after 30 s without a request. `LibavVideoFramePlayer.DecodeFrameAtAsync` is now such a decoder opened
  for one frame. `LibavStillFrameDecoderTests`' opt-in STILL_FRAME_PROBE=1 (STILL_FRAME_PROBE_FILE=<clip>,
  STILL_FRAME_PROBE_SOFTWARE=1) times an editor's clicks both ways in turn. Medians on the laptop, for two of the user's
  recordings (1450x900 and 2432x660, 30 fps) and a generated 1920x1080 60 fps clip with the recorder's 2 s keyframes,
  opened each time against kept: a step forward 147-191 ms against 2-5, a step back 172-194 against 73-96 (it still
  decodes from the keyframe), the same frame again 170-188 against 0, a 5 s skip 138-212 against 20-68, a slider drop
  126-192 against 18-84, the first frame 209-316 against 124-180. With hardware decoding off (1450x900): a step forward
  99 ms against 1.5, a step back 106 against 70. Timed one way after the other, the second ran slower than in turn (a
  warmer processor), which first showed the kept decoder slower at software steps back. Open, the decoder holds 70-90 MB
  of the process, on the CPU or the GPU. Not done: a step back still decodes from the keyframe; keeping the few frames
  before the one shown would make repeated steps back instant. Hand check owed since 2026-11-09: drag the editor's
  seek bar and strip on a 4K video, the picture should follow and the drop show the exact frame.
- Done since batch 1: **GIF/WebM output's extra encode, and its Pause** (Compress and the editor). `EncodeGifWebmAsync`
  transcodes the source itself when `NeedsNoIntermediate` (the whole clip at its own speed, no crop, rotation, burn-in
  or filter, and for a WebM sound not mixed to mono: the transcoders' sound is stereo, as the intermediate's AAC is) and
  `CapsLeaveTheSource` (no size cap below the source's height, and for a WebM no frame-rate cap below its rate, probed
  only when a cap is set) hold. The GIF and WebM transcoders wait on the pause gate before every packet, as the clip
  exporter does. `CompressPipelineGifWebmTests`: an unedited clip makes no intermediate, each kind of edit still does,
  both ways give the same frames, length, size and sound on a constant-rate clip, and a pause during the transcode stops
  it both ways (all four fail with the transcoders' waits taken out); the progress and cancel tests run both ways. Its
  opt-in COMPRESS_GIFWEBM_PROBE=<clip> (COMPRESS_GIFWEBM_PROBE_FORMATS=.gif) runs each way twice, through, from, from,
  through, with the tab's defaults (CRF 22, medium) on the laptop: a 4.1 s 1054x630 H.264 recording's GIF took
  5.14-5.22 s through the intermediate against 4.21-4.30 from the source, a 34.6 s 1450x900 HEVC one's 20.45-20.49
  against 14.81-14.95. Run once each way, through first: the 16.3 s 2432x660 one's GIF 9.13 against 5.78 s, and the
  three WebMs 8.36 against 6.15, 33.89 against 29.28 and 35.47 against 28.55 s. The 4.1 s clip's first run, through
  then from in a fresh process, took 7.04 and 7.40 s, which first looked like no gain. The HEVC recording's GIF
  came out at 6.6 MB against 14.2 (the other two 1-5% smaller), 34.27 s long with 513 frames against 34.67 and 520: its
  video ends at 34.27 s, and the intermediate held the last frame to the end of the 34.6 s sound. Correction to the
  entry on its cancel above: the 41% for a 4.2 s clip's GIF counted the transcode's palette pass as the intermediate's
  (a GIF's file appears only at its second pass); the WebMs' shares were right.
- Found and fixed on the way (reading Compress's "Segmented CRF path" item): **a silent CRF compress to MP4 wrote
  Matroska**. The segmented path joins its chunks into `merged.mkv`, and `LibavClipExporter.TryMuxAudioForRanges`, with
  no sound to add (none in the source, or dropped), copied that file to the output when the output was an MP4, so the
  .mp4 was Matroska inside. It now remuxes into the output's container as it already did for the others.
  `CompressPipelineSegmentedTests` reads the output's first bytes (an `ftyp` box, or the EBML magic) and the sound; the
  two silent-MP4 cases fail against the old exporter, and a silent clip keeps all 60 of its frames either way. The bug
  came with the resumable compress (`65539b5d`, in v0.67.0).
- Done since batch 1: **an export held its whole sound, up to three copies at once** (found reading Compress's
  segmented-path item). `LibavClipExporter.TryBuildAudio`, which every export with sound runs (a CRF compress's
  finalize, trims, speed, crop, burn-in, two-pass, a pin's export, the GIF/WebM intermediate), decoded each range into
  an array through a doubling buffer, wrote it into a `MemoryStream` and copied it out with `ToArray` before writing the
  WAV; `AudioOnlyExporter` decoded the whole track the same way. Both now write through `DecodedWavFile`, which appends
  PCM to the WAV as `LibavPinAudioPcmDecoder.DecodeRangeStreaming` hands it over (only a sped-up range, which the tempo
  filter takes whole, is still decoded into memory), through a 1 MB file buffer: the decoder hands over 4 KB at a time,
  and through the default 4 KB buffer each was a write of its own, which first made the streamed build 0.1-0.4 s slower
  for half an hour. `LibavAudioEncoder` also built six error-context strings a frame, about 400 bytes, 1.1 MB a minute;
  they are joined only on a failure now. `DecodedWavFileTests`: ranges come out as their in-memory decodes, retimed PCM
  appends, two minutes of sound is written allocating under a tenth of its PCM, an export's two minutes too (135.6 MB
  allocated before), and an audio-only WAV export is the track (11.5 MB allocated for 11.5 MB before); those two fail
  against the old exporters. AUDIO_BUILD_PROBE=1 on 30 minutes of AAC (329.6 MiB of PCM), ABBA on the laptop: through
  memory 1.35 and 1.14 s, allocating 2018 MB and then 994 (the array pool had kept about 1 GB of the first run's
  buffers), against 1.15 and 1.07 s and 6.7 MB as it decodes.
- Done since batch 1: **an export built its sound after its video, with the bar at 100%** (the "build audio alongside"
  part of Compress's segmented-path item). `LibavClipExporter.TryExport` now starts `StartBuildingSound` before its
  video encode and takes the AAC file after it, and the segmented compress starts one for its whole span before its
  chunks and muxes it in at the end (`TryMuxWithSound`, which takes over `TryMuxAudioForRanges`). The sound's decode
  waits on the pause gate before every packet and `LibavAudioEncoder.EncodeWav` before every frame, which also checks
  the token now: the AAC encode could not be cancelled at all. `TryBuildAudio` deletes its WAV once encoded, which would
  otherwise stay through the whole compress. `SoundAlongsideVideoTests` pause an export and a segmented compress at
  their first report and find the sound's file in their folder, not growing; both fail with the sound started after the
  video (run alone: other tests make folders of the same kind). The opt-in SEGMENTED_FINALIZE_PROBE=1
  (SEGMENTED_FINALIZE_PROBE_MINUTES, _SIZE, _FILE) times what follows the chunks' last report: a two-minute 960x540 clip
  with a tone, CRF 23 medium, 0.41-0.42 s, its sound taking 1.86-2.79 s on its own, which the old finalize added; ten
  minutes at 64x48, whose chunks take 2 s, still 9.6-11.8 s, as its sound alone takes that long. Still open in that
  item: the chunks reopen and probe the input each, and `merged.mkv` is written whole before the final mux.
- Done since batch 1: **the segmented compress joined its chunks two frames apart, and wrote the video four times**
  (Compress's segmented-path item, found while timing it). Every chunk came out of `LibavClipExporter.TryExport` with
  B-frames (`max_b_frames = 3`), so its first packet decodes two frames before it shows (pts 0, dts -1056 in 1/16000),
  and `LibavMuxer.ConcatVideoSegments` re-bases each segment on that decode time: every chunk after the first started
  two frames after the one before it ended. The merged.mkv in between, which keeps no decode times, lost the first
  chunk's shift when it was muxed again, but not the gaps: a two-minute compress came out with its picture 120.186 s
  long against 120.021 s of sound, 55-66 ms more at every 30 s, 3-4 s over half an hour at 30 fps, since v0.67.0
  (`65539b5d`). `ConcatVideoSegmentsWithAudio` joins the chunks with the sound straight into the output in one pass,
  re-basing each on its stream's `start_time` and keeping the negative decode times before the first frame, as the
  muxers take them (MP4 keeps them; Matroska writes only when frames show): in both, picture and sound start at 0, as
  the old way's did. A soundless MP4 export is encoded straight into its file, deleted if the export does not finish,
  instead of into the export's folder and copied: every chunk was. Tests: `LibavMuxerJoinTests` (the old and new joins
  alike for segments without B-frames; exported chunks laid end to end, which the old join runs long of; no sound; a
  segment without video skipped), `CompressPipelineSegmentedTests.ALongerCompress_KeepsThePictureAsLongAsItsSound`, and
  `ExportTempFileTests` watching the temporary folder for `video.mp4` and `merged.mkv`; the three that watch or measure
  fail against the old code. JOIN_PROBE=1, four 30 s chunks at 1280x720 (97.3 MB) and two minutes of AAC, ABBA: two
  passes 0.20-0.21 s writing 196.5 MB, one pass 0.10 s writing 99.2 MB. The recording's own segment join stays as it
  was: the recorder encodes no B-frames (`max_b_frames = 0`).
- Done since batch 1: **the stop's fixed wait, and a paused recording's video written twice** (two parts of Recording's
  "Stop and finalize run in series"). `FinalizeRecordingAsync` and the separate-files finalize waited `Task.Delay(100)`
  "for file handles to be released", left from the FFmpeg-process days: the stop disposes the video sessions, and the
  audio capture (which joins its thread) and writers, before the finalize starts. A paused recording's segments were
  joined into `merged.mkv`, then muxed with the AAC: MP4, MOV and MKV now join them with the sound in the mux
  (`LibavMuxer.ConcatVideoSegmentsWithAudio`), a recording in one stretch is muxed as before, and GIF and WebM, which
  transcode one file, still join first. Every single-output stop logs `Recording.FinalizeTimings` (the stages, as
  `RecordingStartTimings` writes them: audio, aac, mux or remux, move). `RecordingFinalizeTests` run the finalize on
  synthetic segments through `FinalizeForTestAsync`; the three paused ones fail against the old finalize, which wrote
  merged.mkv. FINALIZE_PROBE=1, ten seconds at 1280x720: one stretch 241 and 361 ms against 364 and 456 before, three
  414 and 404 against 579 and 535 (the two builds in runs one after the other, so beyond the 100 ms wait the gap is
  loose). The AAC encode is most of what is left: aac=195-336 ms for those ten seconds of sound, so half a minute or
  more for half an hour.
- Done since batch 1: **the stop encoded the sound after it** (Recording's "The stop's finalize encodes the AAC after
  it"). The encoder itself is 98% of that: for five minutes of sound, 7 s of which reading the WAV took 0.1 s, at 18-23
  ms a second of sound. `RecordingSoundEncoder` now encodes it while the recording goes. It reads each segment's WAVs
  as the captures write them (opened shared for writing; `LinuxPulseAudioRecorder` now shares its WAV for reading, as
  NAudio's writer does), waits in its reads for the capture, and puts the sound together through the providers
  `BuildFinalAudioAsync` uses: valid segments end to end (`RecordingService.HoldsSound`, the 100 ms rule both share),
  the 48 kHz stereo mix with the microphone's gain, the 16-bit WAV round trip. It runs below normal priority, and at
  normal once the stop waits on it. The first segment starts it, a resume hands it the next WAVs (`EndTrack` for a
  capture that did not start again), and the captures' stop ends each segment. The finalize takes its AAC only when it
  came from the segments the finalize would use, at its bitrate: `Recording.FinalizeTimings` then says `sound=recorded`,
  with a `recordedSound` stage. A format change across a pause, surround system audio with a microphone, the quality
  changed mid-recording or a failed encode have the sound built at the stop as before (`sound=rebuilt`); GIF and WebM
  never start it. The stop's own AAC is now encoded once, where separate-files mode encoded it for every window and the
  MKV fallback once more. `RecordingSoundEncoderTests`: the same decoded samples as the WAV encoded afterwards, the AAC
  written before the stop, the 100 ms rule, a format change, a dispose. `RecordingFinalizeTests`: system audio,
  system and microphone paused twice, the microphone with a gain paused once (MKV), and a microphone that did not
  resume all give the stop path's samples and write no joined or mixed WAV or AAC of the stop's own (failing with the
  encoder never started); a format change and a quality change are rebuilt. `RecordingServiceStartTests` records a
  desktop corner with a stand-in loopback, paused once: `segments=2`, `sound=recorded`, sound and picture within
  0.15 s. FINALIZE_PROBE=1 (`AStopsFinalize_WithTheSoundEncodedWhileRecording_Timed`), system audio and a 320x240
  video, ABBA: two minutes 2.21 and 2.22 s at the stop (aac=2169-2176) against 85 and 61 ms, ten minutes 9.78 and
  9.66 s (aac=9537-9661) against 187 and 144 ms (recordedSound=40-46, mux about 100).
- Done since batch 1: **every LLM request built a new context and read the whole prompt again** (OCR, AI and
  translation). Measured first on the user's TranslateGemma 4B Q4_K_M, CPU, context 2048: load 2.1 s warm (now logged
  as `Translation.ModelLoaded`), a context 42-57 ms to build and 20-35 to free, the prompt read at about 75 tokens/s,
  generation 62-72 ms a token. The instructions are 72 of a short prompt's 85-94 tokens, so about 1 s of every
  request went on reading them again. A translate press sends one merged text, so batching blocks into one prompt was
  not needed. `LlamaKeptContextGenerator` keeps one context while the model is loaded and decodes only what follows
  the tokens a prompt shares with what it holds (`LlamaPromptCache`; `MemorySequenceRemove` drops the rest, and the
  cache is cleared whenever it does not hold exactly positions 0 to kept-1). The generation loop is StatelessExecutor's
  as LLamaSharp 0.30 compiles it (read with ildasm): Tokenize with BOS and special tokens, the chunked DecodeAsync,
  Sample at the batch's last token, end of generation, then the anti-prompts, a token's decode at the next position. A
  request whose prompt and MaxTokens could fill the context, where StatelessExecutor would shift it, goes to
  StatelessExecutor, with the kept context freed first. llama.cpp keeps a full-size cache for the sliding-window
  layers, so no shared position has been dropped; the context holds its 272 MB of KV cache while the model is loaded,
  where each request built and freed one. Each request logs `Translation.Timings` (prompt, kept, context, read,
  generated, generate). `LlamaPromptCacheTests` cover what is kept. `LlamaKeptContextGeneratorTests` (opt-in, the same
  GIMMECAPTURE_LLAMA_MODEL) find StatelessExecutor's text token for token from a fresh context, greedy and seeded, 8 of
  8. The next prompt of a pair kept 72 tokens, read the other 13 in 229 ms against about 1030 for a whole prompt, and
  took 1.06 s against 1.6-1.7. A prompt that could fill a 256-token context is handed back.
  `LlamaSharpTranslationBenchmarkTests`, 16 sentences: median 1781 ms before, 869 after, total 27.5 s against 14.3. 15
  translations came out the same; the 16th turned at a near tie (无法上传 to 無法上傳), from the rounding of a prompt
  read in two parts instead of one.
- Done since batch 1: **TranslateGemma answered a Traditional Chinese target in Simplified Chinese** (found with the
  item above; the user translates to Traditional Chinese with this model). `LlamaSharpTranslationBenchmarkTests` failed
  on it: 13 of 16 English and Japanese sentences came back Simplified (设置 for 設定). The prompt was the model's own: its
  chat template names zh-TW, zh-Hant and zh-CN all "Chinese". On the 16 sentences, by Windows' Simplified-Traditional
  mapping: "Chinese (zh-TW)" 3 Traditional, "Chinese (zh-Hant)" 1, "Traditional Chinese (zh-TW)" 16, in Taiwan's words
  (應用程式, 儲存, 伺服器, 檔案, 網路), "Traditional Chinese (zh-Hant)" 16, "Traditional Chinese (Taiwan)" 16 but looser
  ("電池快沒了"). The other way, "Chinese (zh-CN)" left 4 of 4 Traditional sources in Traditional; "Simplified Chinese
  (zh-CN)" answered all 20 in Simplified (文件夹, 网络). `GetTranslateGemmaTargetName` now names a Chinese target by its
  script; the source keeps the template's name, and the cache key's prompt version went to v9.
  `TranslationServiceSanitizationTests.BuildTranslateGemmaPrompt_NamesAChineseTargetByItsScript` fails against the old
  prompt; the benchmark passes, its median 866 ms.
- Done since batch 1: **OCR candidate rectangles were built one item at a time** (the first half of the Snip overlay
  item; the user keeps `ShowAIScanBox` on, the default). Each box of the scan's text, the windows to snap to and
  Translate mode's search was an ItemsControl item: a content presenter, a Border, four bindings (the brush through
  `ColorToBrushConverter`, a new brush each), an opacity layer, and for the text a pointer-over style that could not
  match, the layer being out of hit testing; each box was added on its own. `CandidateBoxesLayer` draws a list of them
  in one Render with one pen, where layout rounding put each Border (`LayoutHelper.RoundLayoutValue` for the corner,
  `RoundLayoutSizeUp` for the size, `RoundLayoutThickness` for the stroke), and the view model replaces
  `AIScanRects`, `WindowRects` and `TranslationOcrSearchRects` whole (`VisualRect` went). `CandidateBoxesLayerRenderTests`
  (opt-in CANDIDATE_BOXES_PROBE=1, Avalonia's headless Skia renderer) render the old ItemsControl, rebuilt in code
  as SnipWindow.axaml had it, and the new layer: on boxes at fractional positions, for all three layers' thickness and
  opacity, at 100, 125, 150 and 200%, 0 levels apart, with 1134-8712 pixels stroked. Filling a 1920x1080 overlay with
  100 text boxes and 20 windows: the UI thread's part 35-49 ms before, 0.2-0.3 ms after; to the frame that shows them
  37-52 ms against 10-17, most of that now the headless frame itself. `CandidateBoxesLayerTests` pin the rounding,
  `SnipWindowViewModelTests.AScansBoxes_ReachTheOverlayInOneChange` the one change.
- Done since batch 1: **rotation took every frame through BGRA** (Compress and the editor; most of the user's queue
  are 4K phone videos turned 90/180/270). Measured on one of them (`V_20260110_175957_ES0.mp4`, 3840x2160 HEVC), ten
  seconds to 1080p: veryfast 3.3-3.4 s unrotated against 16.7-16.9 rotated, slow 8.1-8.4 against 17.2-17.3, about
  45 ms a frame for the decode-size BGRA conversion, the Skia turn and the conversion back. Without annotations the turn
  is now the filter graph's last step, `transpose=dir=clock`/`cclock` or `hflip`,`vflip`
  (`LibavClipExporter.RotationFilterNodes`), and the final scale reads its source size from the graph's sink. With
  annotations, drawn before the turn and after it, the BGRA path stays. Rotated after: veryfast 2.5-3.0 s, slow
  4.1 s (a rotated 1080 is portrait, 607 wide, so less to encode than the unrotated); the builds ran one after the
  other, the unrotated times the same in both. `LibavClipExporterRotationTests`: a textured clip exported at 90, 180
  and 270 is 1.4-1.5 levels off the source turned by hand and about 64 off the opposite turn; the old path gave the
  same turn at 1.7-2.1 levels, through its two colour conversions. The opt-in `ARotatedExportOfARealVideo_Timed`
  (ROTATE_PROBE_FILE) has the timing.
- Done since batch 1: **a D3D11VA decoder held a surface for every frame thread** (part of Idle, start-up and memory's
  "A Direct3D 11 decode leaves..."). The editor's and compare's `LibavStillFrameDecoder` and a pin's
  `LibavVideoFramePlayer.PlayFramesAsync` set `thread_count = 0` with frame threads also under D3D11VA, where the player's
  comment took them to be ignored; FFmpeg sizes a hardware decoder's surface pool for its frame threads too. Both now
  take `LibavVideoFramePlayer.DecodeThreads`: one thread with a device, libavcodec's count without. Measured with the
  opt-in `HardwareDecodeThreadsTests.OneThreadUnderD3D11VA_Measured` (HW_DECODE_THREADS_PROBE_FILE), ABBA, process
  private bytes, the editor's 720 px frames and ten seconds of a pin at 1280x720. On a 4K HEVC phone video: while
  open, one thread 474-486 MB against 1216-1295 with frame threads and 400-640 on the CPU; a far seek 137-144 ms
  against 180-183 (CPU 281-283), a step 21-25 ms against 14-15 (CPU 10), a step back 214-231 against 179-180 (CPU
  319-326); the pin 30.1 frames a second in every mode, at most 458-467 MB against 1225-1276. On a 1450x900
  recording: 61-112 MB more while open against 164-205, steps 6-8 ms against 4, the pin at full speed. After closing,
  every mode left a few hundred MB, the driver's (the item below). Also measured, and left: decoding an export on the
  GPU, the frames copied back, took 2.4 s for ten seconds of the 4K video against 1.5-2.0 on the CPU and held 440 MB
  more, so exports stay on the CPU.
- Fixed since batch 1 (the user's report, 2026-11-03): **with software rendering the overlay opened behind a white
  rectangle**, 1440x740 at the top left of a 1920x1080 screen, Windows' default size for a new window, which Avalonia
  creates every window at before sizing it. Measured with a temporary App hook (not committed) opening the overlay
  three to five times a run, the screen grabbed every 20-30 ms (BitBlt) and a WinEvent hook logging the overlay's
  show/hide/move/cloak: the white appeared while the window was still hidden (no show event), from the moment it was
  bound, and stayed until the first frame after showing; one grab on a warm open, 0.38 s on a run's first, which
  spends that long binding. Bisected, with a 300 ms pause after binding to widen it: the trigger is
  `ApplyRecordingScreenCaptureAffinity`, `SetWindowDisplayAffinity(WDA_NONE)` on the hidden overlay (rounds with the
  call 13-23 white grabs while hidden, without it 0, ABAB). A new window already has WDA_NONE, so
  `Win32Helpers.SetWindowCaptureVisibility` now leaves an unchanged affinity alone (`GetWindowDisplayAffinity`): five
  opens, the first included, no white grab; the toolbar drawn 45-60 ms after showing (0.18 s on the first).
  The same call on a plain hidden window, opaque or see-through, never showed it, so there is no committed repro:
  `Win32HelpersCaptureVisibilityTests` pins that every real change is still made. Tried first and dropped once the
  trigger was known: cloaking the window (DWMWA_CLOAK) until Avalonia's first frame, which hid it too. The GPU path
  creates windows with WS_EX_NOREDIRECTIONBITMAP and never flashed. The other windows that exclude themselves from
  capture (toasts, the countdown, translations, scrolling capture) do it in `Opened`, after showing.
- Done since batch 1: **each pin built its own U2Net session** (Pins). A pin made a `BackgroundRemovalService` on its
  first removal and disposed it on close, and the service disposed itself on a module removal (`IsDisposed`, for the
  pin to build another). The app now has one (`MainWindowViewModelDependencies.BackgroundRemoval`, created with the first
  overlay, no model until a removal, disposed by `AppBootstrapper` at exit), handed to every pin through
  `SnipWindowServices`, pin-from-pin and the Compress editor's freeze-frame. A module removal unloads its session and
  leaves it listening (`BackgroundRemovalServiceTests.AModuleRemoval_UnloadsTheModel_EachTime_AndLeavesTheService`, on
  `TinyOnnxModel`; the old code fails it at the second removal). Measured with the installed model in a scratch opt-in
  test (Release, each case in two fresh processes): two pins removing one after the other, 1920x1080 then 1600x900
  scenes, the second took 887-901 ms with a service each and 396-402 ms shared, and the process held 368-369 MB over
  its baseline against 189. Closing a pin no longer unloads the model at once: it goes 10 s after the last removal.
- Done since batch 1: **a plain background's removal loaded U2Net** (found while sharing it). `RemoveBackgroundAsync`
  loaded the session before looking for a plain background, which `RemoveSolidBackground` cuts out by colour. It now
  crops and tries that cut-out first, on the pool, and only a picture without a plain background takes the gate and
  loads the model (`RemoveWithModelAsync`); only a model run restarts the idle unload. Measured with the installed model
  in a scratch opt-in test (Release, two fresh processes each), a 1920x1080 white picture with a dark subject: 512-555
  ms and 188-189 MB over the baseline with the model loaded, against 17-18 ms (2 ms the second time) and 0 MB.
  `TheRealModel_KeepsLittleOfTheProcess_LoadedOrNot` after: 1000-1009 ms with the load, 397-425 warm, 191 MB loaded and
  19 unloaded, as before. `BackgroundRemovalServiceTests.OnlyAPictureWithoutAPlainBackground_LoadsTheModel` runs both
  paths on `TinyOnnxModel` (now with an input name, U2Net's `input.1`) and fails on the old code. The pin still asks for
  the AI Core module before any removal (`EnsureAICoreResourcesAsync`), a plain one included.
- Done since batch 1, the user's choice (2026-11-04): **the hover outline's dashes flowed at full rate on the CPU**
  (the Snip overlay's "Hover animation renders at 60 Hz while the overlay sits idle"). The user draws with the CPU ("Use
  less memory"). Measured with a temporary App hook (not committed) on the laptop at 125%, the overlay frozen, its
  hover set through reflection, process CPU from `QueryProcessCycleTime` over 3 s (`TotalProcessorTime` moves in
  15.6 ms steps and read 0-5% here): no outline 1.2-1.3% of a core, a 240x120 outline 20-21%, 1300x650 25-26%, the
  whole screen 27-34%, and the frozen backdrop invalidated every 16 ms 29-38%. The UI thread took 1.1-1.8% throughout,
  and the "16 ms" timer ticked about 43 times a second. A small outline costs nearly as much as the whole screen because
  the CPU renderer presents every frame whole: `FramebufferManager.DrawAndUnlock` calls `DrawToWindow(hwnd, data, 0,
  0, 0, 0, -1, -1)`, a `SetDIBitsToDevice` of all 1920x1080 pixels, about 4.5 ms a frame whatever changed. Ticking at
  33 ms (about 21 a second) halved it (small 10-12%, whole screen 15%). Asked (the backlog had left the look to the
  user), they chose both: under software rendering `HoverRectangleAnimationController.Economical` steps the dashes
  every 33 ms and stops them once the pointer has rested a second (`PointerMoved`, from `UpdateWindowHover`, the
  pointer handler's only call), the outline still shown; a move to a new target keeps 16 ms. `SnipWindowFactory`
  passes `StartupRenderer.IsSoftware`; on the GPU nothing changes. After, the pointer resting: 1.2% (small) and 1.4%
  (whole screen), the timer stopped; moving (woken every 100 ms): 12.7% and 16.9-17.7%. A second round was disturbed
  by a real pointer over the overlay, its clean parts the same. `HoverRectangleAnimationControllerTests` pin the GPU's
  unchanged rate and the economical steps, rest and wake.
- Done since batch 1, the user's choice (2026-11-04): **an update found in a launch to the tray waited for the main
  window** (part of "Tray sessions still miss"). The user starts in the tray at sign-in with automatic checks on, and
  their installed copy was still v0.68.1, which dropped such an update for the session (v0.69 to v0.71.0 were never
  offered); HEAD held it in `_pendingUpdate` until the main window showed, which a tray user may not do for days.
  Asked, they chose a clickable toast: `OfferUpdateAsync` announces a held release once a run per tag
  (`UpdateAvailableToast`), and a click opens the main window through `ShowMainWindowAction` (the tray's
  `ShowMainWindow`), whose showing offers the update as before. `IToastService.Show` took an optional click action;
  such a toast stays up 10 s instead of 2.6, with a hand cursor. `MainWindowViewModelUpdateOfferTests`: announced once,
  a newer tag again, none with the main window up or with no way to open it; the two announcing tests fail without it.
- Checked 2026-11-04, nothing to change: **a closed capture overlay is let go of.** A temporary App hook opened and closed
  the frozen Normal overlay eight times, as the user captures (with its open-time text scan), software rendering on,
  keeping weak references: after each close every window and view model so far had been collected (0 of 8 alive), and
  private bytes went from 37 MB before to 98 MB after the first capture and stayed at 99-109 MB, the managed heap at
  9-20 MB. What the first capture adds is what any capture loads, the OCR model (kept 2 min) the largest part.
- Done since batch 1, the user's choice (2026-11-04): **a video pin reopened file, GPU device, decoder and sound at
  every loop** (most of the Pins item). Measured first with a temporary App hook (not committed; the probe source is
  `AvLoopProbe.cs` in the session's scratchpad): a 6 s 640x360 30 fps clip written for it, a white flash and a 60 ms
  3 kHz beep together at every k+0.5 s, pinned at 920x527 with software rendering, the flashes read by BitBlt of the
  pin's middle and the beeps from a WASAPI loopback (Goertzel), 26 s, fresh processes. Before: sound minus picture
  -60..+109 and -113..+100 ms, a new offset every pass, drifting later; at the loop the sound broke off 63-83 ms and the
  picture's interval read 990-1106 ms. Now one `PlayFramesAsync` plays the loop (`loopRange`): at the loop's end it
  seeks back with the file, decoder and device kept, and `LoopPassTimeline` lays the passes end to end. The sound loops
  in step without reopening (`AudioPreviewPlayer.Start(..., loop)`: `LoopingWaveProvider` over the Media Foundation
  reader, or each pass decoded in turn on the FFmpeg path, padded with silence or cut at the loop's end), and the
  picture keeps to the sound's clock (`ReadClock` per start ticket: WasapiOut/WaveOutEvent `IWavePosition`),
  `PlaybackMediaClock` holding the first frame up to 1 s for the sound, running on from its last reported position up
  to 50 ms, never back, and on a stopwatch while muted or stalled. Lesson from the first try: the loop ended at the
  file's length, 6.038 s, as the clip's AAC track (283 frames, an edit list from 0) runs 38 ms past the picture, and the
  picture held its last frame that long every pass; one of the user's recordings has 0.3 s of sound past its picture.
  The loop now ends where the picture does (`PictureEndSeconds`: the video stream's start + duration), as the old
  restart at EOF did, and the sound starts once the player has laid the loop out (`onLoopKnown`). The FFmpeg path also
  starts its device only once 400 ms are buffered: started on an empty buffer, WasapiOut's first fill of 200 ms was
  silence the device clock counted. After: the beeps 996-1004 ms apart across loops, sound minus picture -25..+48 and
  -13..+48 ms, then 12..53 with the frame-rate fix below (21 ms is the clip's own AAC delay). Muting keeps the picture
  going; unmuting and turning the loop off start the playback again from the playhead (one 60-100 ms reopen), checked
  with the probe. Only a 1× loop of the whole clip or one stretch (`SeamlessLoopFor`) plays this way; a timeline edit
  that changes the loop restarts it (`KeptSegmentsHandoff.Changed`). Tests: `LibavVideoFramePlayerLoopTests` (y4m:
  passes in one call, a stretch, frames never before a half-speed sound, which fails with the sound clock off),
  `PlaybackMediaClockTests`, `LoopingWaveProviderTests`, `PlaybackLoopTests`, `FloatingVideoSeamlessLoopTests`.
- Found measuring that (2026-11-04): **a pin showed about half the frames of a 30 fps recording.** The frame throttle
  compared `Environment.TickCount64` (15-16 ms steps here) with when the UI last drew, against 33 ms at the default
  PlaybackUiFps 30: two frames 33 ms apart mostly read 31. The user records at 30 fps with PlaybackUiFps 30, so every
  pin of theirs showed about 16 fps: 194 of 367 frames in 12 s. `FramePresentationPacer` passes frames on shares that
  fill at the UI's rate (1.5 at most, a tenth early allowed): 780 of 787 shown, a 60 or 120 fps clip thinned to 30.
  CPU with software rendering, the CPU-only probe alternated old/new in six fresh processes (15 s): 19.4/19.6/20.0% of
  a core at 15.9 fps against 22.0/22.9/22.5% at 29.8 fps. `FramePresentationPacerTests`.
- Done since batch 1, the user's choice (2026-11-04): **a pin's processing bar kept the app rendering after it hid**
  (Pins' "Lower"). Fluent's indeterminate `ProgressBar` animates through a style on its template parts, and Avalonia
  12.1.4 only pauses a style's animation while its control is not effectively visible: the paused clock stays
  subscribed to `MediaContext`'s, and while that has any subscriber `MediaContext` renders every 16 ms, changed or not
  (decompiled `AnimationInstance.Subscribed`, `Clock`, `MediaContext.RenderCore`). The bar's template, and so its
  animation, starts when `ProcessingOverlay` first shows, at a pin's first background removal, smart selection or
  refinement, and from then until the pin closed the UI thread kept that pace. `PinProcessingIdleProbeTests` (opt-in,
  the real Win32 platform, a 640x360 pin off screen, GPU rendering, 10 s windows after 30 s of start-up): the UI thread
  woke 12.9 times a second at 2.35 M cycles/s with the pin as shown, and 332.0 at 18.2 M once its overlay had shown
  for a second and hidden; the process 115 and 435 wakeups/s, 5.7 and 22.2 M cycles/s. The bar is now indeterminate
  only while the overlay shows (`IsIndeterminate` bound to its `IsVisible`): after it, 9.1-11.9 wakeups and 1.9-2.3 M
  cycles/s on the UI thread and 104-111 wakeups in the process, three runs, and the probe checks that the bar still
  runs while it shows (371.8 wakeups/s then). The video editor's loading bar did the same after the first frame, for
  as long as the editor stayed open; it follows `IsPreparing` now. `ProgressBarMarkupTests` fails a literal
  `IsIndeterminate="True"` in any `.axaml`. The capture overlay's top loading bar probably does the same (Snip
  overlay).
- Checked 2026-11-04, nothing to change: **the main window's first open** (the user's pick). Window launches since
  2026-11-02 log `mainWindow` 220-284 ms, `show` 233-284 (the tray and hotkeys included) and `firstFrame` 175-209:
  about 0.65 s of the UI thread. `MainWindowOpenProbeTests` (opt-in, the real Win32 platform, off screen, this build
  not ReadyToRun): the first open of a process 1283-1302 ms in three fresh processes (built 550-569, shown 396-400,
  drawn 332-337), every later one 45-123 ms, a window released in the tray included, so only a process's first open
  is slow. An EventPipe trace of the first: control templates built on first use (about 20%), type initializers
  (10%), fonts loaded and shaped the first time (10%), the first frame (25%: the UI thread waiting in
  `MediaContext.SyncCommit` while the render thread drew it, half of that the GPU's first flush) and the
  drawn-decorations layer Avalonia 12 builds for any window whose client area is extended, as the main window's is for
  its own resize edges, even with no part shown (9%). What the app could change, an empty `WindowDecorationsTheme`
  (the edges' roles are hit-tested on the whole tree, not that layer), no inset `BoxShadow` on the colour swatches and
  the 64 px window icon instead of the 256 px one, together changed nothing: 1306-1328 ms. Building the window ahead
  while the tray idles would only move that work into the tray's memory and onto its UI thread.
- Done since batch 1, the user's choice (2026-11-04): **a pin's undo history was capped by steps, not bytes** (Pins).
  Each crop, rotation, background removal or smart selection keeps the whole picture before it (`BitmapHistoryAction`,
  neighbouring steps sharing theirs), for up to 30 steps: `PinUndoMemoryProbeTests` (opt-in, a 3840x2160 pin rotated
  30 times, the test process's private bytes) read 33 MB a step and 984 MB after 30. Asked for a cap, the user chose
  256 MB a pin: `AnnotationEditorState.TrimHistory` keeps, of the newest 30 steps, the newest whose pictures fit
  `HistoryBitmapBudget` (`IKeepsBitmaps`: the pictures a step keeps, the one on screen left out, each counted at its
  newest step), the newest step always. After: 8 steps and 256-260 MB from the 9th rotation on, undone as far back as
  they reach; that is 18 steps of 2560x1440, all 30 of 1080p and 14 of the user's whole two-screen capture (3968x1160).
  Steps that keep no picture count nothing. `FloatingImageViewModelTests` (a budget of three pictures: three steps
  kept, the older pictures disposed, undone to the right one; the newest kept over the budget) and
  `AnnotationEditorStateTests`.
- Done since batch 1, the user's choice (2026-11-05): **a recording's pin was decoded at its region's DIPs and drawn
  inside its border** (from measuring a playing pin's CPU with "Use less memory" on, the user's setting). A temporary
  App hook pinned the user's own recordings (read-only from `D:\影片`) with software rendering and read every thread's
  cycles (`NtQuerySystemInformation` and `QueryThreadCycleTime`, as `ThreadActivity` does) over 12 s after 10 s of
  warm-up, alternating modes in fresh processes. A playing pin of the 1134x624 H.264 recording took 27.2-27.4% of a
  core, 16.9-17.1% of it on Avalonia's render thread (a managed thread started in coreclr). Two causes, both seen in
  the probe's layout readout: `SnipWindowViewModel.Recording` passed the region's size in DIPs as the decode size
  (`pixelWidth = Math.Round(originalWidth)`), so at 125% the pin decoded 907x499 of a 1134x624 recording and stretched
  it back up; and `MainBorder` insets its child by its thickness, so the picture was laid out at 1130x620 for its 1134
  pixels. Every frame was smoothed on its way to the screen (Unspecified sampling costs what LowQuality does; an
  opaque bitmap alone saved nothing; the glow nothing). Now `RecordingFrameSize.Pin` decodes the recording at the
  recorder's own pixels (DIPs x scaling, rounded down to even, `EvenPixels` shared with `RecordingService.LiveSession`)
  and shows them at pixels / scaling DIPs; the border goes round the outside (`Margin = SelectionBorderOuterMargin`,
  as the capture overlay draws its selection border, and `WindowPadding` keeps room for it with the decoration
  hidden); the bitmap is `AlphaFormat.Opaque`; and `FloatingVideoWindow.UpdateVideoSampling` sets
  `BitmapInterpolationMode.None` while the picture lands pixel for pixel (`IsPixelForPixel`: layout x scaling within
  half a pixel of the bitmap), the default otherwise. After: laid out at 1134.00x624.00 px for the 1134x624 bitmap,
  22.9-23.4% of a core (render thread 12.1-12.4%) against 26.9% for the old sizing in the same build; the 1450x900
  HEVC recording 31.4-32.9% against 36.6-37.7% (decoded at 1160x720 before). What is left on the render thread is
  Avalonia's software compositor presenting the whole window every frame. `SnipWindowViewModelRecordingPinTests`
  (the real pin path by reflection at 1.25, 1.0 and 1.5; 1.25 and 1.5 fail on the old code) and
  `RecordingFrameSizeTests`. The same mix-up cropped the wrong part: `TryGetSelectionPixelRect` mapped the selection
  onto `OriginalWidth` (DIPs) as the video's pixels, and the exporter applies the crop in the file's pixels, so at 125%
  the right half of a 1134-pixel recording came out as pixels 454-908; it maps onto the decode size now, the file's own
  (`FloatingVideoCropTests`), and a copied clip goes to History with its pixels too. Not looked at: an image pin with its border shown has the same layout (`FloatingImageWindow`
  draws its picture inside `MainBorder` too); the user hides the snip pins' border, so theirs are drawn at their size.
- Done since batch 1 (2026-11-05, "繼續下一項", chosen by the user's use: they copy pinned recordings as GIF, 18 MB
  ones on 11-03): **a GIF's dithering ran on one core**. A throwaway test (not committed) ran
  `TranscodeGifWebmAsync`/`LibavGifTranscoder.TranscodeToGif` at Medium (15 fps, 720 wide, sierra2_4a) on the user's
  recordings, read-only: 2.4 s for the 6 s 1134x624 one, 15.4 s for the 34 s 1450x900 HEVC one, the palette pass
  3.9 s of that and the map/encode pass 14.5; with `dither=none` the second pass took 4.0 s, so paletteuse's error
  diffusion was ~10.5 s, single-threaded. Without `diff_mode`, paletteuse maps a frame from that frame alone (its colour
  cache changes no result), so `PaletteMapper` maps the second pass's frames on `mapThreads` threads (half the
  processor's, 8 at most), each with a paletteuse graph of its own, the decoding thread handing frames in (two a thread
  on their way at most) and writing them in order; a `diff_mode` forces one thread. ABBA on the laptop, one thread
  against the default (6): 23.8/24.0 s against 10.7/10.8 for the HEVC clip, 3.1/3.3 against 1.7/1.7 for the 1134x624
  one, 4.4/4.6 against 2.0/2.0 for a 432x766 one, the same sizes; the process's cycles rose about 30% (cores sharing
  turbo and SMT), the wall time halved. Each pass now takes about half, so `PalettePassShare` is 0.5. The first version
  deadlocked: the decoding thread waited for room only the writer frees, and it is the writer.
  `LibavGifTranscoderTests.MappedOnSeveralThreads_TheGifIsTheSameByteForByte` (2 and 5 threads, a clip of thousands
  of colours so the dithering works everywhere). Left: both passes decode and scale on one thread (sws is the
  legacy single-threaded call); a short clip could keep pass 1's frames for pass 2.
- Done since batch 1 (2026-11-05, the user's call "改成跟錄影一樣"): **a pin's GIF copy dithers as a GIF recording of
  its quality does**. `LibavGifTranscoder.QualityDither` (High sierra2_4a, Medium bayer:bayer_scale=3, Low none) is
  what `RecordingService.FinalizeAsGifAsync` chose inline, and the copy now dithers by it too; the Compress tab's GIF
  kept the finest until the next entry. The user's quality is Medium. A throwaway
  test (not committed), ABBA with the parallel mapping, on the user's recordings, read-only: at Medium the 34.6 s
  1450x900 HEVC one 9.31/7.40 s and 6.49 MB against 5.73/5.42 s and 3.48 MB, the 6.2 s 1134x624 one 1.13/1.10 against
  0.99/0.95 and 6.88 against 5.56 MB, the 4.9 s 432x766 one 1.42/1.53 against 0.96/0.97 and 7.99 against 6.38 MB; at
  Low 3.56/3.48 against 3.08/3.00 and 2.61 against 1.33 MB, 0.53/0.50 against 0.45/0.45 and 2.51 against 1.92,
  1.30/1.24 against 0.93/0.87 and 5.57 against 3.49. A frame of each, zoomed 4×: bayer's regular grain shows on flat
  watercolour and dark gradients, invisible at 1×, and sierra's stray blue and green dots in a tree are gone.
  `FloatingVideoGifWebmExportTests.AGifCopy_IsDitheredAsAGifRecordingOfItsQuality` (byte for byte against
  `TranscodeToGif` with each quality's dither written out; Medium and Low fail on the old code), and the several-threads
  byte-for-byte test runs bayer too.
- Done since batch 1 (2026-11-05, the user's call "也跟著改"): **the Compress tab's GIF dithers by its quality too**.
  `GifWebmVideoExporter.TranscodeToGif` takes the `VideoQuality` and dithers by `QualityDither`, so every GIF the app
  makes picks its dithering by quality; the tab's quality is its CRF slider's as before (20 or less High, 30 or more
  Low, the default 22 Medium). The user's logs from 10-30 to 11-05 show no Compress export. An unedited compress at the
  defaults runs the same transcode as the pin's copy at Medium: the existing opt-in
  `FromTheSource_AgainstThroughAnIntermediate_Timed` on the user's 34.6 s 1450x900 recording gave 3564 KB from the
  source, the pin copy's size (6.5 MB with the finest), and 5464 KB through an intermediate (its CRF 18 H.264 adds
  noise to dither). `CompressPipelineGifWebmTests.AGif_IsDitheredAsAGifRecordingOfItsQuality` (CRF 18, 22, 32, byte for
  byte against `TranscodeToGif` with each quality's dither written out; 22 and 32 fail on the old code). Not taken:
  `diff_mode=rectangle` (map only what changed) halved sierra's single-thread time on that recording and did nothing for
  a scrolling 432x766 one, and would put the mapping back on one thread (it maps from the frame before).
- Done since batch 1 (2026-11-05, "繼續下一步", chosen by the user's use: they record regions, H.265 on the RTX 3050
  Laptop's hevc_nvenc, cursor on, no overlays): **NVENC is handed the capture's BGRA**. A throwaway test recorded a
  1450x900 region at 30 fps with the user's settings through `LibavGdigrabMkvSession`: 1240-1300 M cycles a second,
  nearly all of it the session's thread, and `sws_scale` BGRA to 4:2:0 alone was 10.8 M cycles a 1456x896 frame (NVENC's
  encode call 2.0). `hevc_nvenc` lists BGRA; fed it, its GPU conversion gave the same BT.601 colours (eight patches,
  YUV within one level; alpha 0, as GDI leaves it, the same) and wrote BT.601 into the stream, where the 4:2:0 converted
  on the CPU left the matrix unset (players take BT.709 for 720 lines or more: red 255,24,0). RGB PSNR against the source
  at the same bitrate, 45 frames of four of the user's recordings: 38.00 against 43.27 dB (a game UI), 42.35/42.88,
  42.64/42.79, 30.41/30.67. `ChooseEncoderPixelFormat` hands NVENC BGRA (tagging the context BT.601, limited) and the
  encoder ladder opens NVENC again on 4:2:0 if its driver refuses BGRA; the other encoders keep what they took (NVENC got
  yuv420p, the first 4:2:0 it lists, not NV12). The AV1 probe stays on 4:2:0. ABBA, process cycles: 1450x900 1257, 1240,
  1230, 1128 old against 894, 700, 893, 892 new, 305 frames either way; 1920x1080 1907/1920 dropping 3-7 frames in 10 s
  against 1294/1415 with none dropped. `AV_SYNC_PROBE` through `RecordingService` (h264_nvenc, three 3 s mp4s): audio
  minus video +24 to +36 ms. `LibavRecordingEncoderTests` (the choice, pure; the bundled `hevc_nvenc` configured, Windows;
  fails on the old code: yuv420p) and opt-in `NVENC_PROBE` `Gdigrab_OnNvenc_KeepsTheColours_AndSaysWhichTheyAre`
  (SteelBlue within 3 levels, BT.601 in the file; the old code left it unset). Left: AMF and QSV (not measured; the
  laptop's Radeon could test AMF), the overlays' BGRA round trip, the webcam's full-size copy (Recording).
- Done since batch 1 (2026-11-05, "把能做的都做完"): **the overlay's top loading bar animated hidden**. Its infinite
  animation was a style on its `Rectangle`, applied as the overlay is built. `TopLoadingBarIdleProbeTests` (opt-in, the
  real Win32 platform, a 1536x864 window off screen, GPU rendering, 8 s windows after 30 s of start-up): the UI thread
  woke 19.2 times a second at 4.4 M cycles/s without the bar, 582.4 at 43.3 M with it hidden since it was built and
  597.4 at 42.4 M hidden after showing. The style now selects `ctl|TopLoadingBar[IsVisible=True] Rectangle#Indicator`:
  18.9 at 4.0 M hidden, 17.6 at 3.9 M after showing, 943 while it shows (the probe checks it still runs).
  `StyleAnimationMarkupTests` fails an infinite style animation whose selector does not say `[IsVisible=True]` (the old
  bar's `Rectangle`); it is the only one in the app's own markup.
- Done since batch 1 (2026-11-05): **detection's input tensor is kept**. A throwaway test ran `DetectText` on 1920x1080
  frames of the user's recordings (read-only, the installed detector from `D:\GimmeCapture_win-x64\AI`): 10.78 MB
  allocated a scan, all of it the 1x3x736x1280 input, a gen2 collection every second or third scan, 154-190 ms. The
  input is now a buffer `OcrRuntimeService` keeps while the detector is loaded and drops with it, handed out through
  `OcrSessionUse.DetectionInput` (one inference at a time holds the scope): 0.00 MB a scan after the first, no gen2,
  152-173 ms, the boxes identical. `OcrRuntimeServiceTests.DetectionsInput_IsOneBufferKeptWhileLoaded_AndGoesWithTheUnload`.
  Measured and left in the same round: a drag-select's pointer move costs the overlay's view model 15-19 us and 1.4 KB
  (five changes a move, with a subscriber reading the 28 hotkeys as the keyboard filter does), about 0.2% of a core at
  125 moves a second, so neither the filter's rebuild nor `ToolbarMaxWidth`'s notification is worth a change.
- Done since batch 1 (2026-11-05): **a cancelled scan's queued detection**. `AIScanSessionService` ran `DetectText` in
  `Task.Run(..., ct)`, which stops it only before the pool picks it up; one cancelled while it waited for the inference
  lock behind another ran anyway (154-190 ms on a 1920x1080 frame, above). `IOCREngine.DetectText(bitmap, ct)` (a
  default member, so the fakes keep theirs) and `PaddleOCREngine` checks the token once it holds the session, before
  any work. `PaddleOCREngineTests.ACancelledScan_DetectsNothing_OnceItHasTheSession`.
- Done since batch 1 (2026-11-05): **a paused pin's seeks keep their decoder**. Each paused seek (the seek bar's
  debounced seek, the 5-second buttons, the timeline) was a one-frame `PlayFramesAsync`: open, find streams, D3D11VA
  device, seek, decode. A throwaway test, 12 random jumps and 12 steps of 0.1 s on the user's recordings, ABBA: 1450x900
  HEVC 143.5/166.7 ms a jump and 158.8/156.6 a step, against 51.3/51.3 and 12.5/12.4 from a kept
  `LibavStillFrameDecoder` (the compress editor's); 1134x624 136.1/135.2 and 133.9/132.7 against 21.9/22.4 and 9.7/9.9;
  1920x1080 HEVC 169.0/165.6 and 194.5/193.1 against 56.1/55.3 and 18.7/19.2. `KeptStillFrames` keeps one for the
  pin's file and size, the loop shows a paused seek's frame from it (the frame's own time on the playhead, as before)
  and releases it when the pin plays (it holds 70-90 MB open; it also closes after 30 s unused); before the duration is
  known a paused seek still takes the old pass, which reports it. `KeptStillFramesTests`. The seek bar still waits
  300 ms after a drag stops before it seeks (`DebounceSeekAsync`), which a kept decoder no longer needs: showing frames
  while dragging is the user's call (Pins).
- Done since batch 1 (2026-11-05): **tray sessions miss the hotkey dialog and the Modules tab**. The main window wired
  `OnHotkeyRegistrationFailed` (its dialog) and `RequestOpenModulesAction` (its tab), so in a launch to the tray, the
  user's at sign-in, a hotkey another app held failed silently and the overlay's request to get a model did nothing.
  `WindowlessMainHandlers` stands in until a main window is up and again once one lets go (`UseWindowlessHandlers`,
  from `ConfigureSharedViewModel` before `InitializeHotkeys`, and `UnwireViewModel`): a clickable toast with the
  dialog's message that opens the main window, and the main window opened on the Modules tab. The hotkey names moved
  there from `MainWindow.ViewModelWiring.cs` for both. `WindowlessMainHandlersTests`.
- Done since batch 1 (2026-11-05): **colours: every picture was read, and every recording written, as BT.601, unsaid**.
  Checking AMF for the NVENC change (a probe encoding eight colour patches, scratchpad `AmfBgraProbe`, on the laptop's
  Radeon 660M) found `hevc_amf` and `h264_amf` write BT.709 into the stream whatever `colorspace` says, NV12 or BGRA,
  and convert BGRA with BT.709 (patches within three levels); so AMD recordings held the CPU's BT.601 and said BT.709.
  `libx264` and `hevc_nvenc` write the `colorspace` they are given, BT.601 included; nothing gave them one. And every
  YUV-to-RGB `sws_scale` (player, thumbnail, `LibavStillFrameDecoder`, `GifFrameSource`) took every picture for
  limited BT.601, so BT.709 video, most HD video, showed red as 232,0,1. `LibavColorMatrix.ReadAs` sets those scalers
  to the frame's matrix and range (the decoded frame's: a GPU download carries none; BT.709, BT.2020, full range; a
  frame that says nothing stays BT.601) and `WriteAs` sets the recording converters (gdigrab, WGC, composite) to the
  encoder's. `ConfigureEncoderContext` declares limited BT.601, AMF BT.709, and AMF is handed BGRA like NVENC (4:2:0,
  if refused, is written as BT.709). `LibavClipExporter`'s BGRA round trip for drawn-on exports reads and writes BT.601
  both ways, so its picture keeps its own; only what is drawn is off on a BT.709 source (left). `LibavColorMatrixTests`
  (BT.709, BT.601 and unsaid clips through the still decoder, playback and a GIF; BT.709 fails on the old code with
  232,0,1), `ARecording_SaysTheColoursTheConverterWrites`, `HevcAmf_IsConfiguredForBgra_AsTheBt709ItWrites`,
  `Gdigrab_SaysWhichColoursItsFileHolds` (unset on the old code). The NVENC opt-in test and `AV_SYNC_PROBE` pass as
  before (+24 to +27 ms). Not checked: QSV and MF, which may write their own matrix as AMF does (no Intel GPU here,
  `hevc_mf` would not open).
- Done since batch 1 (2026-11-05): **a manual scroll's row signatures are pooled**. `ScrollStitcher` sampled the frame
  and the strip into new `byte[height * 32 * 4]` arrays for every alignment (115 KB for 900 rows, on the large object
  heap), two to four a frame. They are rented from the shared pool and handed back (`RentRowSignatures`; a bitmap
  without pixels gets cleared bytes, as a new array had). `ScrollStitcherTests.AligningAFrame_SetsAsideNoRowSignatures`
  (a 1500-row strip and a 900-row frame: 314,896 bytes an alignment on the old code, under 64 KB now). The user's logs
  show no scrolling capture, so its other cost stays: the producer grabs back to back while nothing scrolls (Snip).
- Done since batch 1 (2026-11-05): **two AV1 probes at once**. `HasUsableHardwareAv1Encoder` read and wrote its state
  without a guard, so the Record tab, the Record-mode warm-up and a recording's start could each open the GPU's AV1
  encoders together on a run's first need (the capability cache answers every other run). `ProbedAnswer` runs one
  probe at a time, the others waiting for its answer, keeps no answer a probe could not reach, and takes an earlier
  run's (`Recall`). `ProbedAnswerTests` (a second caller during a probe gets its answer without probing).
- Done since batch 1 (2026-11-05, the user's call "邊拖邊顯示"): **the seek bar shows frames while dragged**. The
  setter of `CurrentTimeSeconds` starts a paused seek at most every `ScrubFrameIntervalMs` (50) once the duration is
  known, each cancelling the last, kept short of the end (a seek there wraps to the start and would pull the thumb
  away); the 300 ms rest still ends the drag and seeks to its exact spot. From the kept decoder a step takes 10-20 ms
  (Status above). `FloatingVideoScrubTests`.
- Done since batch 1, the user's choice (2026-11-05): **the first pin of a run kept a frozen overlay up** (Snip
  overlay's "Frozen commits keep the overlay up while the pin window is built"). Since 288d5aba the overlay closes once
  the pin it opens has drawn (`ExecutePinCaptureAsync` waits for its first frame), so a slow pin keeps it up.
  `PinOpenProbeTests` (opt-in, the real Win32 platform, the pin built off screen as the overlay builds it, the tray's
  host window up): the first pin of a process took 0.93-1.04 s in a test build (not ReadyToRun), later ones 37-73 ms
  at 1280x720 and 3968x1160, on the GPU and the CPU alike; 5 of the user's 29 logged overlay closes followed a busy
  stall of 0.26-0.70 s, all on 2026-10-31. Hiding the overlay first, as the item had it, would leave a gap at every pin
  to speed up only the first. Instead `SnipWindowFactory.ScheduleWarmUp`, ten seconds after start, now also warms a pin
  with the app's services: built and thrown away when drawing on the GPU (the first pin then took 0.56 s, nothing
  kept), and on the CPU also shown off screen until its first frame (0.13 s, 0.5-1.3 MB kept). Shown on the GPU it took
  the first pin to 0.13 s as well but kept 26-28 MB (Skia's GPU cache and shaders), so it is not. Skipped while an
  overlay is open. Found on the way: a pin closed before its queued toolbar placement ran threw from `Screens`
  (`FloatingWindowBase.UpdateToolbarEdgePlacement`), which now returns once the window has no platform left.
- Done since batch 1, the user's choice (2026-11-05): **the Record-mode audio meter** (Snip overlay).
  `SnipWindowViewModel` read both levels on the UI thread from a 120 ms DispatcherTimer for as long as the overlay was
  in Record mode, a recording included, raised all eight of their properties at every beat whether they had changed or
  not, read the microphone though its meter shows only when the recording takes it, and made the audio devices' COM
  objects on the UI thread at the first beat. `RecordMeterIdleProbeTests` (opt-in, the real Win32 platform, an
  overlay's view model in each mode, 10 s windows after 30 s, silence): in Record mode the UI thread woke 63.1 times a
  second at 5.2 M cycles/s and raised 47.4 property changes a second, and the first reading took 58 ms on an STA
  thread (14 ms once the audio DLLs were cached). `AudioLevelMeter` now reads on a thread of its own
  (`IAudioLevelReader`, its COM objects made and let go there), hands a level on only when it changed to the
  hundredth, and reads the microphone only when the recording takes it; the view model posts what changed and raises a
  level's properties only when it changed. After, in the same silence: the UI thread out of the busiest eight, no
  property changes, the meter's thread 16.5-16.9 wakeups at 1.3-1.5 M cycles/s. Off Windows the reader no longer tries
  Core Audio, which threw, and was caught, at every beat. `AudioLevelMeterTests`, `SnipWindowViewModelTests`.
- Done since batch 1 (2026-11-06, "繼續做"): **freezing mid-drawing kept the live snapshot** (Snip overlay). Drawing
  mode takes the selection's pixels for mosaics and blurs (`CaptureDrawingSourceAsync`: the still's crop when frozen,
  a live grab otherwise). The toolbar's freeze (`ToggleFreezeFrameLiveAsync`) and its way back to live left the
  snapshot taken before, so a mosaic sampled a live grab while the export cropped the still, or the still while the
  export grabbed the screen. The toggle now takes it again (`RetakeDrawingSnapshotAsync`, when one is kept; a newer
  capture still wins). `DrawingModeSnapshotTests.FreezingWhileDrawing_TakesTheSelectionFromTheStill_ForWhatIsAlreadyDrawn`
  (the live grab kept on the old code).
- Done since batch 1 (2026-11-06, "Do it"): **gdigrab vs Desktop Duplication** (Recording). Measured on the laptop
  (AMD Radeon chip with the 1920x1080 panel at 125%, RTX 3050 beside it): grabbing 1450x900 at 30 fps alone took 603 M
  cycles/s through gdigrab and 110 through FFmpeg's `ddagrab` + `hwdownload`, both 29.9 fps, the same pixels but for a
  caret (340 of 1.3 M) and the same alpha; a whole recording on `hevc_nvenc` took 640-647 against 245-264 at 1450x900
  (304 frames each), 894 against 314 at 1920x1080, three rounds each. `RegionCaptureSource` takes
  `LibavDesktopDuplicationSource` where one upright display holds the region (`DuplicationOutput.Holding`), else
  `LibavScreenGrabSource` (gdigrab/x11grab, moved out of `LibavGdigrabMkvSession`); a duplication that fails mid-way
  (DXGI_ERROR_ACCESS_LOST and the like come back as AVERROR_EXTERNAL) is opened again if it had given frames, else the
  screen grab takes over, and the converter sets up again should the format or size change. What the measuring found:
  making the Direct3D device took 55 ms every time, so `DxgiOutputs.TryCreateDuplicationDevice` makes it while the
  encoder opens and the display is duplicated after (open alongside NVENC's start, `graph_config` failed every time);
  the device is made on the adapter DXGI lists the display under and handed to FFmpeg (`DuplicationDevice.Adopt`), not
  by number. And a process that starts CUDA before it first asks DXGI anything (`cuInit`, any NVENC open) is put on the
  NVIDIA card for good: DXGI then lists it first with the panel under it, nothing under the AMD chip, and ddagrab says
  "Selected output not supported". The app did that whenever Record mode's warm-up ran first, as it does with software
  rendering on: `DisplayAdapterOrder.SettleBeforeCuda` (a DXGI factory, 36-40 ms cold) now runs before the warm-up's
  `cuInit` and every NVENC open. gdigrab's GDI handles fail on another thread than the one that opened them (EIO), so
  the screen grab opens on the worker. A recording's start: 263-373 ms against gdigrab's 241-272 in fresh processes.
  Windows lets a process duplicate a display once at a time: a second `DuplicateOutput` gets E_INVALIDARG (ddagrab's
  "Invalid argument"; three recordings at once, six rounds: one duplicated each round, two on gdigrab). So of several
  windows recorded to their own files at once (the separate-window fallback) only the first is duplicated, and the test
  classes that record the screen take turns (`ScreenRecordingCollection`): two at once failed the check that a recording
  duplicated its display, once in a `verify.ps1` run.
  The encoder now opens before the capture, so a start whose capture fails deletes the header-only segment it left
  (the finalize took any non-empty file for a segment). `RegionCaptureSourceTests`, `DuplicationOutputTests`,
  `LibavRecordingSessionTests` (gdigrab pinned; `DesktopDuplication_RecordsTheRegion_WhereItCanDuplicateTheDisplay`;
  opt-in: `DesktopDuplication_LeavesOutAWindowExcludedFromCapture`, `DesktopDuplication_OnNvenc_KeepsTheColours_...`),
  `RecordingDesktopDuplicationProbeTests` (opt-in, alone in its process: Record mode's warm-up, `encoderWarmUp=cuInit`,
  then a real `RecordingService` recording logs `Capture source: ddagrab` on the AMD chip; without `DisplayAdapterOrder`
  it logged the NVIDIA card, "did not open" and `Capture source: gdigrab`).
- Done since batch 1 (2026-11-06, "Keep optimizing"): **the converter's copy of every captured frame** (Recording).
  With Desktop Duplication a 1450x900@30 recording on `hevc_nvenc` still took 245-265 M cycles/s. Timed by stage on the
  recording thread (a throwaway probe, the session's own loop): grab 85-86, `CapturedFrameConverter`'s BGRA to BGRA
  `sws_scale` 48-49 (0.23 ms a frame in a cache-warm loop, double that on a frame fresh from `hwdownload`), the encoder
  88; the process 263-265. Where the frame already is in the encoder's pixel format and size and no overlay draws on
  it (`PassesThrough`: BGRA from either grabber on NVENC and AMF), it now goes to the encoder itself
  (`RecordingSegmentWriter.EncodeAsIs`; the pipelined loop queues a clone), cleared first of what it says of itself
  (`ClearSourceProperties`): gdigrab's frames come out of the BMP decoder as intra pictures with the key flag, which an
  encoder takes for an order to make the frame a keyframe (20 keyframes in 20 frames in the writer test without the
  clearing), and ddagrab's carry RGB/sRGB tags and a 1:1 aspect. By stage: 210-214 M cycles/s. Through the real
  session, on and off interleaved: 211, 219, 251 against 245, 297, 275; gdigrab's path 601-604 against 621-632.
  `RecordingSegmentWriterTests.FramesEncodedAsIs_MakeNoKeyframesOfTheirOwn`, `ClearingAFramesSourceProperties_...`;
  the opt-in NVENC session tests now also count keyframes (one in 1.5 s) and check the length and frame rate. Found
  on the way: those opt-in colour tests failed twice reading (31, ...) where the window is SteelBlue; the browser in
  use was on top at the region (the check that now runs first said so), so `TargetWindow` paints and waits for a
  composition before it is used, and `EnsureOnTopAt` raises it again for up to a second or names what covers it.
- Done since batch 1 (2026-11-06, "繼續優化"): **a playing pin's NV12 to BGRA conversion** (Pins). The user's log since
  v0.73.0 shows overlays with OCR and no recordings yet; their recordings end in a pin that loops while it stays open.
  `PinPlaybackProbeTests` (opt-in, the real Win32 platform, a pin of the user's 1450x900 HEVC recording `160036` off
  screen and muted, 10 s after 4): drawing on the GPU, as the user's log says they do again, the process took 1032-1055
  M cycles/s, about a third of a core: Avalonia's render thread (`DwmRenderTimerLoop`, 430-456 wakeups/s) 469-496, the
  decode thread 283-287, an unnamed native thread 94-99, the UI thread 78-92; drawing on the CPU 1147, its render thread
  646. The decode path by stage (a throwaway probe, D3D11VA, 300 frames of that recording): decode 0.54 M cycles a
  frame, the download to NV12 2.01, swscale's NV12 to BGRA 5.68, which has no fast route; NV12 to YUV420P and then
  swscale's fast YUV420P route 2.36 for both, so 247 against 162 M cycles/s at 30 fps. The bundled `scale_d3d11` puts out
  only NV12 and P010, so no conversion on the GPU. The two routes on the same 120 frames: 21% of pixels apart, mostly by
  1, 1.0% by more than 8, at most 62 on coloured edges (the fast route repeats each chroma sample over its 2x2 pixels),
  PSNR 45.7 dB; an 8x crop of the block that differed most looked the same. `LibavBgraConverter` takes the YUV420P step
  for NV12 shown at its own size (`GoesThroughPlanar`; the pin of a recording, not the editor's scaled frames), and the
  player's loop uses it. The probe, interleaved: the decode thread 212, 188 against 286.5, 286.6 M cycles/s, the process
  1005, 975 against 1055, 1055. `LibavBgraConverterTests` (the route; BT.709 and BT.601 tags honoured in the second step;
  PSNR over 40 dB against the direct route on a striped picture; set up again for a frame of another size).
- Done since batch 1 (2026-11-06, "繼續優化"): **a playing pin's toolbar shadow** (Pins). `PinPlaybackProbeTests`'
  variants, drawing on the GPU, interleaved: the toolbar shown 990, 991 M cycles/s (Avalonia's render thread 490, 511,
  an unnamed native thread 94, 108); hidden 744 (render thread 322); shown without its `BoxShadow="0 4 15 0
  #D0000000"` 777 (348); shown with the playhead at 1 fps instead of 15 802, 773 (351, 349). So the playhead redrew the
  toolbar 15 times a second, and every redraw blurred its box shadow again, which Avalonia does to a whole layer with an
  image filter. `GlowDecorator`, whose mask blur the pin frame's glow already used, now draws rounded corners too
  (`SKRoundRect`, clipped out and spread as Avalonia does) and holds the video pin's bottom toolbar: 814, 819, 823 (the
  render thread 368-377). `GlowDecoratorRenderTests`' new toolbar case: 7 levels apart at most at 100%, 8 at 125%, 2 at
  150% (0.02-0.19 on average), the same to the eye. `ToolbarShadowMarkupTests` (fails on the old markup).
- Done since batch 1 (2026-11-06, "繼續優化"): **a playing pin's frames copied on the UI thread** (Pins). The decode
  thread copied each frame into `LatestFrameHandoff` and the UI thread copied it from there into the pin's
  `WriteableBitmap`. Avalonia 12's Skia `WriteableBitmapImpl` (its IL, read through reflection) enters a monitor in
  `Lock` until the framebuffer is disposed, and its `Draw` enters the same one around `SKImage.FromPixels` and
  `DrawImage`, so a frame written under `Lock` on any thread never shows half written. `UpdateBitmap` now writes the
  frame into the bitmap on the decode thread (under `_videoBitmapLock`, skipping a frame of another size or an earlier
  playback) and the UI refresh only redraws; `LatestFrameHandoff` is gone. `PinPlaybackProbeTests`: the UI thread 40-43 M
  cycles/s against about 77, the process 765-834 against 796-823 (within its spread); the decode thread the same, its
  copy now going into the bitmap. `FloatingVideoFrameTests` (the frame in the bitmap at once fails on the old code).
  Found on the way: in a few runs the probe's UI thread woke 1100-1400 times a second; Windows had put its window on
  the desktop at 0,0, whatever position it was given, and it was being moved and resized by hand (its moves and margins,
  logged, said so). The probe now moves the window off every screen once it shows, and checks it stays there.
- Done since batch 1 (2026-11-06, "Keep optimizing"): **the stall monitor in the tray** (Idle, start-up and memory).
  Sampled read-only, the user's v0.72.1 in the tray woke 67 times a second for 6.9 M cycles/s: the `UiStall` watchdog
  24 (2.7 M cycles/s), the UI thread 25.6 (2.1), Avalonia's render timer 12.9 (1.4), the hotkey thread 3 (keys typed).
  Each probe of the watchdog's 100 ms beat cost it about two wakeups and the UI thread two more: about 44 of the 67.
  One sample, at 13:49, found the render thread at 24.8 M cycles/s (19 ticks a second, ~1.3 M cycles each) and 20 s
  later back at 1.4; nothing on screen of the app's then, not seen in the dev build, cause unknown. The monitor now
  beats once a second while none of the app's windows is shown (`UiStallWatchdog.TrayBeat`, told by
  `AppWindowsOnScreen`, which follows every `Window`'s `IsVisible` and close and leaves out the tray's host), and a
  hotkey press (Windows and X11) or the tray's Show posts a probe of its own (`UiThreadStallMonitor.NotifyInput`),
  behind the press; one that finds a probe still waiting has the next posted once that one runs, from the UI thread,
  so behind the press too (`UiStallProbe.TryPostForInput`/`TakeFollowUp`). `UiStallMonitorIdleProbeTests` (opt-in,
  real Win32, the tray host up): 27.6 UI-thread and 22.0 watchdog wakeups a second at 100 ms, 3.1 and 2.6 in the tray
  (the process 82.6 against 37.4). Six 600 ms holds read 0-570 ms in the tray with nothing pressed (a timer's stall is
  now read short by up to a second, or missed below it), 602-614 after a press, 509-555 with a window shown; the
  tracker said false, true, false, true, false through a window's show, hide, show and close. The dev build in the
  tray, 25-60 s after a launch to it, eight runs ABBAABBA: 55-69 wakeups a second at the 100 ms beat (one run 73,
  keys typed), 6.5-8.0 M cycles/s, the watchdog 18-24 and the UI thread 25-30; 21-24 at the tray beat, 4.2-4.3 M
  cycles/s, the watchdog 2.1-2.6 and the UI thread 5.9-6.9 (one run 38 and 6.8, its UI thread at 19 and the render
  timer busier, as in the probe's settling below). `UiStallWatchdogTests` (in 2.5 s, 1-3 beats in the tray and
  10-27 with a window; a window shown starts the short beat at once) and `UiStallProbeTests` (the press's probe and
  its follow-up). Found on the way:
  once a window has been made, Avalonia itself wakes the UI thread 12-19 times a second for some tens of seconds, with
  or without the GPU, hidden or closed, with no message posted or sent (a `WH_GETMESSAGE` and `WH_CALLWNDPROC` count),
  then settles; left alone. The window hit test at the screen's corner, where Windows puts the tray host whatever its
  position, goes to the window above it.
- Done since batch 1 (2026-11-06, "Keep optimizing"): **OCR's models optimized again at every load** (OCR). The user's
  log: most captures come more than the 2-minute keep-alive after the last (3 of 4 that day), so a scan loads detection
  again, `SessionCreated` 138-202 ms (387 the first of the day) before `detect=` 218-281. ONNX Runtime optimizes the
  graph at every build (`ORT_ENABLE_ALL`). Raw sessions on the installed models, ABAB in one process (a throwaway
  probe): the detector 163-178 ms from the file, 113-118 from a copy saved optimized (`OptimizedModelFilePath`) and
  loaded with optimization off, 143 from an ORT-format copy (no better), 78-93 from a copy optimized short of the layout
  passes but 20 ms slower a run; the recognizer 87 against 37; outputs within 1e-5 (the layout reorders sums).
  `OnnxOptimizedModels` keeps the full copy in `optimized\` beside its model, so it goes with the OCR module's folder,
  named by a key over the model's path, size and time, the execution providers, the ONNX Runtime build and the
  processor's instruction sets; a copy that fails to load is deleted and the model loads as before. The app's
  `OcrRuntimeService` (`useOptimizedCopies`) saves the missing copies at its idle unload, on the pool (230-340 ms each,
  once per model file and build), and a module removal's global unload waits for a save under way. Through the runtime
  (`OcrRuntimeServiceTests.TheRealModels_LoadFasterFromTheirOptimizedCopies_AndFindTheSameBoxes`, opt-in, the models
  copied to a folder of its own, 8 rounds ABAB): detection 106 ms (99-127) against 73 (69-75), detection and recognition
  207 (182-258) against 116 (105-120), the same 24 boxes. `OnnxOptimizedModelsTests` (a hand-written y = x + c model: a
  saved copy is what the next session builds from, with the same answer; a bad copy is deleted; a changed model is not
  served the old copy) and the runtime's `TheIdleUnload_SavesOptimizedCopies_…`, which fail on the old code. Left: the
  same for U2Net and SAM2 (smart selection's load was 2.7-2.8 s), off the user's usage so far.
- Done since batch 1 (2026-11-06, the user's yes to overlays popping up while measuring): **the first overlay of a run**
  (Snip overlay). The user's v0.72.1 log: the day's first capture `SnipOpen ... show=229 total=343ms` and a 340 ms
  `UiStall`, later ones 70-85. Measured in win-x64 publishes (ReadyToRun), launched to the tray, the overlay opened at
  25 s and 33 s through a temporary App hook, with temporary marks in `SnipWindowFactory.Open`, `OnDataContextChanged`,
  the window's Measure/Arrange and OnOpened, two `RequestAnimationFrame` callbacks and posts at Default and Background
  (patched in by a script around each publish and restored, so the tree stayed clean). The first open against the
  second, five processes each: the window's wiring to its view model 63 ms against 1, the bindings after it 36 against
  11, the first layout 19 against 3, the UI thread free again (the Background post) 382 against 128. All of it code
  ReadyToRun leaves to the JIT, the generic Rx and ReactiveUI pipelines over the app's types first. The idle warm-up
  (`SnipWindowFactory.RehearseOverlay`) now binds the unseen window to a parameterless view model and lays it out at the
  primary screen's size, then unbinds (which ends its subscriptions) and closes it, a step at a time: in a publish, view
  model 56-58 ms, binding 68-69, layout 16-19, after the window's build of 167-201 as before (`Overlay.WarmUp` logs
  each). The unseen window changes nothing of its native window: capture affinity, region and `WS_EX_NOACTIVATE` skip it
  (`IsWarmUp`), as an affinity set on a hidden overlay once showed it white (fixed in
  `Win32Helpers.SetWindowCaptureVisibility`). ABBAABBAAB: `SnipOpen` total 218 ms (218-266) before, 152 (130-180) after;
  the wiring 5, the bindings 18, the layout 10; free again at 315 (284-344) against 382; the second open 59 (52-79)
  against 81 (74-86). Three later runs of the final, split warm-up: 123-130. `OverlayWarmUpTests` (opt-in, headless):
  bound, laid out, unbound, closed, and window and view model both collected. Left: what the first show itself costs,
  about 115 ms more between `Show` and the UI thread coming free (the window's first composition and frames, OnOpened's
  posted work), which only a shown window rehearses; shown off screen on the GPU, the pin's warm-up kept 26-28 MB, so
  not without the user's say. While the probe's overlays were up the user typed: two `Hotkey.HookLate` lines (156 ms),
  one in each build, and the overlay may have taken some of those keys.
- Done since batch 1 (2026-11-06, "Keep optimizing"): **a GIF's passes decoded in turn** (GIF). The user's log and folder
  that day: short region recordings pinned, cut and saved as GIFs (7-13 MB each), and a cut pin's GIF goes through an
  intermediate MP4 (`ExportTrimmedInProcessAsync`) that kept its sound, which the GIF drops. Timed on their recording
  `20261020_162541` with its middle fifth cut (a temporary stopwatch in `TranscodeToGifWithPalette`, 75 frames at
  720x396): the intermediate 0.42 s, the GIF 1.3 s, of which the first pass decoding 250 ms and palettegen counting
  280, its palette at the end 150-185, the second pass decoding 260 and mapping and writing 270. Made only for the GIF
  (no sound, ultrafast, CRF 16, 15 fps) the intermediate took 0.2 s less, but the GIF came out 13-17% larger (the
  cleaner intermediate compresses worse), so only the sound went: the same GIF, byte for byte. Each pass now decodes
  and scales on a thread of its own up to about 8 MB of frames ahead (`LibavGifTranscoder.FrameFeed`), each frame
  carrying the progress at which it was decoded. `PinGifCutProbeTests` (opt-in, PIN_GIF_CUT_CLIP, the pin's way,
  alternately, four rounds): `20261020_162541` 2.11 s before against 1.56 (the GIF 1.60 against 1.15), `20260803_160036`
  (34.6 s 1450x900 HEVC, a 21 s cut) 10.7 s against 7.9 (the GIF 7.9 against 5.1), the GIFs the same byte for byte.
  `LibavGifTranscoderTests.DecodedAhead_TheGifIsTheSameByteForByte`; the cancellation, progress and fallback tests run
  through the new feed. Left: a cut GIF still goes through the intermediate (2.8 s of the long clip's 7.9); taking the
  cut's ranges straight into the GIF's passes would save that and a generation.
- Done since batch 1, the user's choice (2026-11-06): **the OCR scan on two screens** (Snip overlay). The overlay's
  scan read its whole viewport as one capture, which `PaddleOCREngine.DetectionInputSize` brings down to 1280 across:
  the user's desktop (3968x1160 in every scan of the log, 10-31 to 11-05; its one display on 11-06 was 1920x1080, so
  the other is about 2048x1160) was read at 1280x384, a third of each screen's pixels. `OcrScanScreensProbeTests` (opt-in, the installed OCR module, two screens
  drawn with panels of UI text at 11-24 px, 226 lines): 194 boxes, 4% of the lines with a box of their own (IoU 0.5)
  and 86% at least half inside one, detection 59-69 ms; screen by screen 62% and 100%, 288-320 ms; the cursor's screen
  alone 68% and 100% of its lines in 143-156 ms, the other screen none. The user chose every screen read on its own:
  the request carries the overlay's screens (`AllScreenBounds`), and `OcrScanTiles` reads each screen's part of the one
  capture (`SKBitmap.ExtractSubset`, nothing copied) and moves its boxes back into place, with one screen read whole as
  before; the log line says `2 screens read apart`. The same boxes as two separate screens in the probe.
  `OcrScanTilesTests`, `AIScanSessionServiceTests`. Not checked on the user's screens: the second one was off when it
  was built (one 1920x1080 display reported).
- Done since batch 1 (2026-11-07, "keep optimizing to the end"): **a Save kept a frozen overlay up through its encode**
  (Snip overlay). The overlay's Save wrote its PNG at full compression, about 0.35 s for a 1920x1080 screen
  (`SnipCopyProbeTests`), inside the capture ritual, so a frozen overlay (the user's) and its spinner stayed on screen
  through it, after the save dialog where AutoSave is off (the user's). The overlay now closes once the file has its name
  (`ChooseSavePathAsync`), and `WriteSavedCaptureAsync` writes a copy of the picture after it (the ritual disposes the
  one it hands out; a 1920x1080 copy is an 8 MB memcpy), then sets the status, adds the file to the history and shows
  it in its folder, as before. The file is the same, at full compression. On the way: `SaveToFileAsync` opened the file
  with `File.OpenWrite`, which keeps an existing file's length, so a capture saved over a larger file kept that file's
  tail; both capture services use `File.Create` now. `SnipWindowViewModelTests.SaveCapture_ClosesTheFrozenOverlay...`,
  `WindowsScreenCaptureServiceTests`, `LinuxScreenCaptureServiceTests`.
- Done since batch 1 (2026-11-07, same request): **every start loaded WinForms and FFmpeg's bindings** (Idle, start-up
  and memory). The installed copy's start-up list (a window launch's: 83 files, 135.6 MiB) held
  `System.Windows.Forms.dll` (13.1 MB, 0.2 of it ever in memory) and `FFmpeg.AutoGen.dll` (1.1). A throwaway console
  probe referencing the app (its data folder redirected; nothing shown, no hotkey, tray icon or Run key) logged the
  managed stack of each assembly load while it ran the start's steps in a fresh process: the Avalonia set-up with
  `App.axaml`, the update checks, `AppBootstrapper`'s constructor, the tray host, the platform services and the main
  view model with its settings. On a Debug or Release build nothing loaded WinForms; with the published ReadyToRun
  `GimmeCapture.dll` in place, preparing `App.OnFrameworkInitializationCompleted` without running it did: crossgen had
  inlined `PlatformErrorDialog.ShowError`, whose `MessageBox.Show` put WinForms among the method's fixups. `ShowError`
  is `NoInlining` now (`PlatformErrorDialogTests`). FFmpeg.AutoGen came from applying `HardwareDecodeEnabled`, whose
  setter wrote a static of `LibavVideoFramePlayer` and so ran its static constructor (FFmpeg-typed fields); the flag
  lives in `VideoDecodeSettings` now. The probe on the published build again: neither loads. Left as they are: the HTTP
  stack (`AIModelDownloader` takes the shared client as it is built, but the update check needs it seconds later),
  `System.Security.Cryptography` (the instance key's SHA-256, then TLS) and the ColorPicker theme (the overlay's
  toolbar, which the warm-up builds). Installed as f76f68fe, its first start (a window launch, 2026-11-07 14:21) wrote a
  list of 81 files without either. WinForms now loads at the first clipboard copy, on the clipboard's own STA thread
  (`StaClipboard.RunAsync`), so the UI does not wait for it.
- Done since batch 1 (2026-11-07, same request): **the CJK fonts read before every start's hotkeys** (Idle, start-up and
  memory). Of `GimmeCapture.dll`'s 39.9 MB, 29.7 were Noto Serif TC and JP, manifest resources the launcher read whole
  with the rest before starting the app, and `MappedFontCollection.Register` built both typefaces in `App.Initialize`
  (the `fonts` stage, 23-34 ms), though nothing draws before the hotkeys work: a start to the tray draws nothing at all
  until the overlay's warm-up 10 s in. The fonts are the resources of an assembly of their own now,
  `src/GimmeCapture.Fonts` (no code: `Assembly.Load` by name, so no ReadyToRun fix-up of the app's names it), and the
  collection loads a family the first time Avalonia asks for it (`TryGetGlyphTypeface`, `TryGetFamilyTypefaces`,
  `TryMatchCharacter`; `ResourcesToLoad`: an unknown family or a character from any of them loads every font not yet
  loaded). `ReadAhead.AfterStart` names `GimmeCapture.Fonts.dll`: the launcher's plan leaves it out, the app's own read
  from Main does too at a start through the launcher, and `StartupPrefetch.ReadAfterStart` reads it on a thread of its
  own once `Startup.Timings` is complete, logging `StartupPrefetch.AfterStart: <MB> in <ms>`; `RecordWarmUp` neither adds
  it nor counts it against `WarmUpLimit`, which its size would have broken for every other file. A window launch still
  reads it with the rest, from Main. `FONT_PROBE=1` (headless Skia): the lazy font loads in +0.9-1.5 MB of private
  bytes and draws in +0.8-2.3 MB more, the same pixels as the font read through a stream (0 of 512,000 bytes differ).
  The probe first held the two together to the 4 MB set for the drawing alone, which they reached at random (2.1-4.0
  MB): each step has its own bound now. The installed copy's first start of f76f68fe (a window launch, 2026-11-07
  14:21) logged `fonts=4/0` and wrote a list of 121.4 MiB, 28.3 of it the fonts, so the launcher's read at sign-in comes
  to about 93 MiB (136 before both changes; 91 was an estimate that took the fonts' size in MB from MiB). Check at the
  user's next sign-ins: `launcher=` near 93 MB, an `AfterStart` line, the warm-up without a stall.
- Done since batch 1 (2026-11-07, "optimize until nothing is left"): **what a start set up without using it** (Idle,
  start-up and memory). Measured with throwaway probes, then checked with a console probe that runs the start's steps
  (Avalonia set-up through `App.axaml`, `AppBootstrapper`, the design `MainWindowViewModel`, 8 s of dispatcher) on the
  published ReadyToRun dlls and prints each assembly load with its stack. The log: Serilog's set-up in a ReadyToRun
  probe took 42 ms, 23 to build and 19 for the first line, against 25.5 for a plain file sink, the difference the
  daily sink's `PathRoller` compiling its file-name `Regex` (`RegexOptions.Compiled`: Regex and Reflection.Emit at
  every start); `AppLog.Initialize` now builds the logger on a thread of its own (`AppLog.Build`), what is logged
  meanwhile waits in `PendingLogEvents` and is written with its own time and order, a crash or `Shutdown` waits for
  the build (10 s at most), and `Startup.Timings` ends ` logger=<ms>`. Inter: `WithInterFont` read all six faces
  (1.9 MB) as Avalonia was set up, 82-83 ms of set-up against 63-64.5 with `DeferredInterFontCollection` (76.5-78
  against 89-90 with the font manager's own set-up) and 2.9-3.6 MB of private bytes against none, though only the
  processing, countdown and scroll-hint windows and the translation layer draw in the Fluent theme's font; it loads at
  the first request now, the same faces (`INTER_FONT_PROBE`), and the app names no type of Avalonia.Fonts.Inter. The
  locale files: all three parsed through System.Text.Json, 5-7 ms for the first dictionary even with the serializer
  warmed by a settings-like class on another thread, 1-1.4 for the others; now only the language in use, at its first
  lookup, with `Utf8JsonReader` (1.2-1.4 ms). The web stack: `InitializeAboutCommands` made the update service at
  every start (its cancel command and download-state subscription), and the service the shared `HttpClient`, which
  loaded System.Net.Http, Net.Security, Net.Primitives and the crypto library; the automatic update check then ran
  within a second. Now the update service is made when something uses it (`SubscribeToUpdateDownloadState` follows
  it), `AIModelDownloader` builds its downloader at the first download, the check runs 30 s after the settings load
  (`AutoUpdateCheckDelay`, after the warm-up's list recording), and the instance key's SHA-256 is `Sha256Digest`
  (the same digest, `Sha256DigestTests`), so the probe's start loads none of them, nor Avalonia.Fonts.Inter: about
  7 MB less in the start-up list. Left as they are: the ColorPicker theme (2.3-2.8 ms in the probe, and the
  overlay's warm-up loads it anyway, so the list keeps it), System.ComponentModel.TypeConverter, Avalonia.Dialogs and
  DiagnosticSource (`App.axaml`'s Fluent theme). The installed copy's first start of fb63624a (a window launch,
  2026-11-07 18:23) against f76f68fe's that afternoon: `log=16` (61), `guard=28` (3), `afterSetup=1` (17, Inter's
  set-up was there), `xaml=50` (60), `viewModel=67` (80), total 864 ms (934), `hotkeysAt=555` (623), ` logger=44ms`;
  the list 75 files, 86.2 MiB without the fonts' file (93.1), none of those assemblies in it but
  System.Diagnostics.Tracing (0.02 MB). The guard's 25 ms more, one sample: its own work is the same and the hash
  is precompiled (0.62 ms, nothing jitted, checked on the installed dll), so most likely the logger's build on its
  thread, alongside it; log and guard together 44 ms against 64. Watch it at the next starts.
- Done since batch 1 (2026-11-07, same request): **the data root's own Temp was never swept** (disk). The user's
  `%LOCALAPPDATA%\GimmeCapture\Temp` held 628 MB from February to May: 32 `Recordings_<guid>` session folders (the
  largest 283 MB) and 3 `GimmeCapture_<guid>.mp4`, from versions that recorded there before the per-install folders.
  `RecordingTempCleaner` swept every version's Temp, the current one and the one beside the executable, not that one;
  now it does (`Layout.LegacyTempDirectory`), by the same names and ages. Nothing records there now (the data folder
  is the versioned one, the executable's or one given explicitly). Freed at the installed copy's next start.
- Seen on the way (2026-11-07), not acted on: a few of the Compress queue's probes waited 13-44 s in
  `avformat_open_input` (most took 0.2-1.7 s), two at a time as `_probeSlots` allows, and two batches each ended in an
  `FFmpegNativeException`. The log names only the file, not the drive, and the user's paths were not looked into: a
  sleeping or busy disk is the likely reason, not the app.
- Done since batch 1 (2026-11-07, same request): **every Compress setting change saved the session on the UI thread**
  (Compress, the "small ones"). `CompressSessionService.Save` rewrote `compress-session.json` with `AtomicFile` (a
  temp file and `File.Replace`) at each change, every step of a slider being dragged: 3.8-9.8 ms at the median, p95
  9-20, at most 41 on this desktop's C: drive (200 writes, twice, with a verification run going). The item states'
  background writer became `CoalescingWriter<T>`, and the session goes through one too (300 ms, written off the UI
  thread, flushed at the process exit and before a load). `CompressSessionWriterTests`, `CompressItemStateWriterTests`.
- Done since batch 1 (2026-11-07, same request): **the translation layer rebuilt its region at every change** (Pins'
  "lower"). Each result added, translated or measured, and every pointer move while a result's box was dragged, rebuilt
  the window region from every box at once, before the layout had caught up, as the pins' region did until it was
  queued (the 31 rebuilds a frame of a pin's drag-resize). It is queued the same way now, once a frame at Loaded
  priority (`QueueWindowRegionRefresh`). Not measured: the installed copy has no translation model, and the pins' probe
  showed what the same queueing does.
- Done since batch 1 (2026-11-07, same request): **culture casing on the overlay's keyboard hook** (Snip overlay). The
  running app had loaded Windows' `icu.dll`: .NET uses ICU for the culture's rules. In a fresh process with the user's
  zh-TW culture, the first culture-sensitive `ToUpper()`/`ToLower()` of a run took 162-163 ms, even of "ABC", the first
  culture-sensitive `IndexOf` 19-20 ms, all three with a sort 186-188 ms and +1.5 MB of private bytes (+3.3 MB working
  set); with `DOTNET_SYSTEM_GLOBALIZATION_USENLS=1` (Windows' own NLS) 3.4-3.6 ms and nothing. The app's only such calls
  were `GetKeyStringFromVirtualKey`, on the overlay's hook thread, which every key typed anywhere waits for (`.ToUpper()`
  of the capital letters A-Z, a no-op) and the save dialog's file type name for a recording. Both are culture-free now,
  and `NoCultureCasingTests` fails on a culture `ToUpper()`/`ToLower()` anywhere in the app. Not measured in the app:
  whether something before the first letter key had already set ICU's casing up. Then the user chose the whole app on
  NLS (2026-11-07): `System.Globalization.UseNls` in the Windows head's runtime config (`GimmeCapture.csproj`,
  `GlobalizationModeTests`), which takes ICU out of the Windows build. Checked first that nothing the app shows
  depends on it: dates only through numeric patterns (`yyyy-MM-dd HH:mm`, `yyyyMMdd_HHmmss`), numbers only as plain
  numbers and percentages, and no text sorted or compared by the culture (every sort compares numbers). The Linux
  head keeps ICU. Check in the running app: no `icu.dll` among its modules.
  Installed as 78b42cd0, its first start (a window launch, 20:35) had no `icu*.dll` among its 147 modules, and
  `guard=6`: the 28 ms of fb63624a's first start had been ICU loading at the main thread's first text operation, which
  Serilog's set-up on Main used to do first. Total 831 ms, `hotkeysAt=538`.
- Done since batch 1 (2026-11-07, same request): **what the first sign-in on the new start showed** (Idle, start-up
  and memory). The sign-in at 20:13 ran fb63624a: `launcher=3686ms/86MB` (134-136 MB in 4.0-6.6 s before),
  `beforeMain=1761/94` at `systemCpu=97%`, `xaml=789/172`, total 1284 ms, `hotkeysAt=1380`, ` logger=221ms`. Two stalls
  were the new start's own doing. Right after the timings line, 2.7 s at 10% of a processor (266 ms of it): the
  priority went back to normal at the hotkeys (`StartupPriority.Restore` in `StartupTrace.Mark`), before the tray
  icon, the start's last work on the UI thread, and the fonts' read (`StartupPrefetch.AfterStart: 28.3 MB in 2757 ms`)
  began then too, on the same disk. Both now wait for `StartupSettled`: the timings complete and a Background-priority
  job posted after `TrayController.Install` (`SettleLatch`, `StartupSettledTests`). And 30 s in, 1.6 s at 2%: the
  automatic update check made the update service, so the shared `HttpClient` and the web libraries, on the UI thread,
  reading them off the busy disk since a28e6305 took them out of the start-up list. `GetUpdateServiceAsync` makes it on
  the thread pool, and the check's and the About tab's requests run there too
  (`MainWindowViewModelUpdateCheckThreadTests`; their first request sets up the network on the calling thread). Next
  sign-in: no stall after the timings line, none at 30 s, `beforeMain` still long at a busy `systemCpu`.
- Seen 2026-11-07 18:32, not acted on: an overlay took 1227 ms to open (`services=509 viewModel=493`, the UI thread
  blocked at 5%, 13,174 page faults, a 345 ms gen2 GC), and the keyboard hook had got the key 171 ms late
  (`Hotkey.HookLate`): the whole machine was slow then, not the overlay's own work (61-117 ms at other opens that day).
- Done since batch 1 (2026-11-07, the user's choice): **the overlay's window built ahead of the capture** (Snip
  overlay). In the user's warm opens that day the `window=` stage of `SnipOpen.Timings` (`new SnipWindow` and its
  bounds) took 32-57 ms of 61-117. `OverlayPrepareProbeTests` (OVERLAY_PREPARE_PROBE=1: the real Win32 platform,
  software rendering, nothing shown) built one in 25-33 ms after three warm-ups, and each held 2.9-3.4 MB of private
  bytes, unseen. Making parts of the window's XAML (644 lines, with SnipToolbar's 383 and DrawingToolbar's 279) build
  only when used would save that on every capture without the memory, but touches every mode. The user chose the
  window built ahead for about 3 MB: `PreparedWindow<T>` (`PreparedWindowTests`) builds one 3 s after an overlay
  closes and after the idle warm-up, never while an overlay is up, and `Open` takes it; the line then says
  `window=<ms> (ready)`.
  Installed as 0d51bfe9, the user's next two captures (2026-11-07 23:20) opened with `window=6 (ready)` and
  `window=1 (ready)`, the second in 79 ms; every capture of the night after (dfb0185e) had `window=1-6 (ready)` and
  opened in 91-101 ms, the frozen grab (`freezeGrab=62-71`) now most of it.
- Done since batch 1 (2026-11-07, same request): **smart selection let go of SAM2 the moment the tool was put down**
  (Pins). The same evening the user used it twice 7 s apart: `Sam2Runtime.Loaded: Tiny on CPU in 2931 ms`, the tool
  put down, then `in 3132 ms` again, and the picture's encoding (1.4-1.7 s) redone, since leaving the tool disposed
  the pin's `SAM2Service` and released its lease, and the runtime unloads at its last lease (OCR keeps its model 2
  minutes, `UnloadIdleDelay`). The pin now keeps both for `Sam2KeepAlive` (60 s) after the tool is put down
  (`ReleaseSam2ResourcesSoon`): taken up again meanwhile, the service and its encoding are used as they are; a pin
  that closes lets go at once. SAM2 holds about 174 MB more for that minute. `FloatingImageSmartSelectionTests`.
- Found 2026-11-08 by a verification run, not an optimisation: **`CompressPipelineSegmentedTests` failed at random**
  (twice in two days: `clip_avio_open: No such file or directory`). `CompressSegmentStore.PruneOrphans`, which the
  Compress tab's first open runs, removed any segment dir without `state.json`, and `Save` writes it with an atomic
  replace, so a fresh dir is without one for a moment; tests that open the tab ran alongside. A dir without state is
  now removed only once nothing has been written in it for `StatelessGrace` (an hour). `CompressSegmentStoreTests`.
- Done since batch 1 (2026-11-08): **the first capture after a window launch** (Snip overlay). Each time the user
  started the app by hand they took a capture within seconds, before the overlay's warm-up at 10 s: 8 s in on 11-07
  (304 ms to open: `window=115 viewModel=75 show=111`) and 5 s in on 11-08 (297 ms), against about 95 ms once warm.
  The warm-up's delay is `CaptureOverlayWarmUpDelay`: still 10 s at a start to the tray, which is the sign-in's and
  whose start-up list the warm-up's files join, and 3 s at a window launch, whose start is done at its first frame
  (about 0.9 s); the window built ahead (`PreparedWindow`) follows 3 s after the warm-up. Not yet seen in the log.
- Done since batch 1 (2026-11-08, the user: scrolling captures still often come out with parts repeated): **manual
  scrolling capture's stitching** (Snip overlay). The last week of logs holds no scrolling capture, so a simulation
  stands in for one: `ScrollCaptureSimulation` draws a chat page (messages, repeated replies and stickers, images),
  scrolls a 500-row view over it the way smooth wheel scrolling moves, and draws what apps keep in place over the
  content (a header bar, a message box, one that appears partway, LINE's floating date, a hovered message, re-render
  noise); every row the stitcher keeps is traced back to its page row. `ScrollCaptureSimulationProbeTests` (`SCROLL_SIM=old`
  runs the old stitching beside it): the old one stopped growing under a fixed bar (535 to 1,248 of 4,900 rows),
  repeated a message box (188 rows of bars for 80), stopped at 732 rows scrolling up under the date pill, and kept 22
  rows twice after a flick past the end and back. `ManualScrollStitcher` replaces `ApplyStitchFrame`'s logic and
  `ManualScrollMotionGate`: each frame is looked for only near where the shift measured from the frame before puts it
  (`ScrollStitcher.AlignNear`); the rows that stay put at the top and bottom are found from frames that moved
  (`FindFixedBands`, also mid-capture, taking back what the frame before added under a bar that just appeared) and kept
  out of the strip with 48 logical pixels of edge guard, the result showing them once from the frames at the strip's
  ends; a match found by searching the whole strip is used once the frame before (by the shift back) or after agrees;
  an ambiguous shift between frames is settled by the last one (scrolling keeps its speed for 40 ms). All 25
  scenarios now keep every row once; 440 and 700 rows a frame, past what the window can follow, keep only the first
  frame (the old one kept up with 440, its window the whole frame, and stitched 700 out of place). Each frame scores a
  few dozen placements instead of the whole strip: runs took 17-21% of the old time. A capture now ends with
  `ManualScroll.Finish: ... frames= placed= unguided= tentative= bands=top/bottom bandChanges= started= result=WxH`
  (written as an event name, it lost `started=` and `result=` to the 120-character cut until 2026-11-08). The user's first captures
  with it (LINE, 2026-11-08 10:19-10:21): three captures placed every frame (481, 360 and 379 of them, none unguided,
  bands found top and bottom, 3-5 band changes each); a fourth locked onto the wrong axis before the stitcher started
  (`horiz=True`, 674 frames, none placed; the user cancelled and took it again): the axis detection still matches each
  frame against the first by the old whole-frame search. The long pin's scroll bar lay under the right edge's resize
  handle (`HandleRight`, 15 px inside the content), so it could not be dragged: that handle now moves out past the
  content on the edge the bar runs along (`RightResizeHandleMargin`, `BottomResizeHandleMargin`), and the bar widens to
  20 px after 0.1 s (an opt-in headless check showed Fluent's own 16 px and 0.5 s, and that the pin's ScrollViewer
  resources reach its bar). `LongPinResizeHandleTests`. The Auto direction is now `ManualScrollAxisDetector`: the
  motion since the first frame measured along each axis over the frame's middle half (`FindVerticalShift`, with its
  score), the clearly better axis winning, locked when two frames in a row agree. `AxisReport` (opt-in, `SCROLL_SIM`)
  reproduced the wrong lock: a LINE-like chat (`ScrollCaptureSimulation.Line`) scrolled up under a 170-row title bar and
  the floating date, where the old way found no match down the frame and a sideways one 489 of 545 columns over, and
  locked sideways at the first frame; with a message box too it never locked. The new one locks down at the second
  frame in both, and sideways scrolls still lock sideways. `ManualScrollAxisDetectorTests`. On 4821c550 (2026-11-08
  12:44 and 12:51) both of the user's Auto captures locked down and placed every frame (534 and 256), one a 540x729
  region like the 545x731 one that had locked sideways. Their log also showed the overlay running a snip's text scan
  as it opened (the detector loaded in 157-186 ms, both screens read in 354-384 ms), and a text line under the pointer
  wins the hover over the window around it: a scrolling capture's overlay now offers windows only
  (`OffersTextCandidates`; the user's choice, as it changes what the overlay shows). `SnipWindowViewModelTests`.
  The next two captures left frames out: 14:10 placed 105 of 137 (3 found by searching the whole strip) at 26 frames
  a second, and 15:06 placed 290 of 364 with one "scroll back" warning, 4.6 s in, while the strip had grown only about
  470 px a second (559x536 region, 282 rows matched once the bars and edge guards were out). Why could not be told,
  so the closing line now says (the user's choice): `missed=near:,beyond:,lost:` (did not match where the motion put
  it / past the strip's edge / the motion lost), `shiftLost=`, `maxShift=` against `window=`, `longestMiss=`,
  `stalls=`, `seconds=`, `grabbed=`, `grab=` and `stitch=` (average/longest ms a frame) and `queueFull=` (the
  grabbing waiting for the stitching). `ManualScrollStats`, `DurationTally`. The first capture with it (17:13, scrolled up
  until older messages load) placed 622 of 1035 frames: `missed=near:22,beyond:189,lost:201 shiftLost=3 maxShift=365
  longestMiss=197 window=269 stalls=3`, at 47 frames a second and 18.8 ms a frame. Not slowness and not content changing:
  the motion was lost. `LineReport` (opt-in, `SCROLL_SIM`) ran a LINE-like chat at that size, 72 runs: with the title bar
  not told from the content, a scroll of 13 rows measured as 456 and one of 44 as 445 (the bar's rows stay put, so they
  score the true shift worse than a far one whose small overlap misses them), and the view then waits past the strip's
  edge, as in the capture. A frame whose shift could not be measured, or was too far for it to meet the strip, is now
  looked for around where the motion had it and taken only on a clear match (`LookWhereTheMotionWas`, `rescued=`), and two
  frames with every row the same have not moved. In the simulation that changed little: 9 frames rescued in 72 runs,
  `beyond` 311 to 290, duplicated rows 1470 to 1289, breaks 24 to 23, the 25 standard scenarios as before: the stretches
  that lose track match about equally in many places, which a clear match cannot settle. Tried and dropped: scoring
  shifts without the rows that stayed put (at small shifts most rows look still: wrong shifts up to 102, and duplicated rows
  in scenarios that had none), and the check without the clear-match rule (`beyond` down to 107, but breaks up to 33). Each
  stall warning now lists the last 64 shifts (`shifts=`, `!` on a frame not placed, `x` where none was measured): the next
  real one tells a wild jump from a scroll too fast to follow.
- Done since batch 1 (2026-11-09, "繼續優化,影片編輯部分"): **the editor's picture during a drag** (Compress and the
  editor). The user's queue is 4K HEVC phone videos (`D:\影片2`, 1.7-8.3 GB), of which `compress-item-states.json` shows
  7 of 33 cut, 6 turned, 1 cropped, none drawn on, and earlier measurements of the editor had none of these. On
  `V_20260110_175957_ES0` (3840x2160, 29 fps, 216.8 s, a keyframe a second), kept decoder (`STILL_FRAME_PROBE`): the
  first frame 593 ms, a step forward 29, a step back 79 (42-189), a 5 s skip 180, a slider drop 135. Dragging the seek
  bar or the strip moved only the time until the release. The pin's way, one request every 50 ms each cancelling the one
  before, would show nothing at 115 ms a far seek, so `ScrubFramePump` decodes one at a time, always the newest
  position asked for, and `LibavStillFrameDecoder.DecodeFrameAtAsync(keyframeWillDo)` gives a frame that needs a seek as
  the keyframe it lands on. A drag across the clip, every position a seek: 116 ms a frame exact against 37 keyframes
  (8.6 against 27 a second); a pixel at a time on an 800-wide bar, mostly decoding on and exact: 56-60 either way. The
  release still decodes the exact frame. `VideoEditWindowScrubTests` (opt-in, headless, real pointer events on the
  real window; times out with `ScrubTo` disabled), `ScrubFramePumpTests`, a keyframe case in
  `LibavStillFrameDecoderTests`. Measured and left for now: while playing that video the preview took 1,019 M cycles/s
  (`EditorPlaybackProbeTests`, about a third of a core) and kept up at 28.8 of 29 frames a second, 5.95 ms a frame to
  download the whole 4K frame and 5.88 to scale it to 720 wide. Scaling on the GPU would leave out most of both, but
  the bundled FFmpeg's `scale_d3d11` cannot do it on this laptop: once given a buffer source with the frames context and
  `format=nv12`, its output pool fails with `Could not create the texture (80070057)` (E_INVALIDARG, from the bind flags
  it asks for). A D3D11 video processor written by hand was not worth it: colour handling that varies by vendor, for
  about 600 M cycles/s while playing and about 12 ms a paused frame.
- Done since batch 1 (2026-11-09): **the editor's preview stopped at every cut** (Compress and the editor).
  `VideoEditViewModel.PlayAsync` played each kept run with a `LibavVideoFramePlayer` of its own, so every cut and every
  lap of Loop closed the file, the decoder and its D3D11VA device and opened them again. `PlaybackPieces` and
  `LibavStillFrameDecoder.PlayPiecesAsync` play them in one decode: at a piece's end it seeks to the next with all that
  kept, or decodes on when the index has no keyframe between (`LibavStillFrameDecoder.DecodingOnIsNearer`), and the next
  piece's first frame waits until the last one's time is over. How long the picture stands still at a cut on
  `V_20260110_175957_ES0` (`EditorPlaybackProbeTests`, five cuts, both ways in turn, twice; a frame lasts 34 ms): 10 s
  cut out, 353-435 ms against 119-140; 0.3 s cut out, 372-528 against 127-156, where seeking every time had left
  130-290. Not something the user has met yet: none of the 33 saved items has more than one kept piece and Loop starts
  off, so the choice rests on cuts being the editor's main use. The first piece's sound now starts once the file is
  open rather than before it. `LibavStillFrameDecoderPiecesTests` (fails without the wait for a piece's last frame or
  without each piece's own speed), `PlaybackPiecesTests`, and opt-in EDITOR_CUT_PLAY_PROBE=1 through the real window.
  Measured, nothing done: opening the editor on that video (a headless host with the app's precompiled assemblies)
  holds the UI thread 410-480 ms the first time in a session and 65-90 ms after that, and the picture follows 240-425
  ms later (`Open()` is 83-157 of it, the first seek and decode the rest). Hand check owed: play a 4K video with a piece
  cut out, with its sound on.
- Done since batch 1 (2026-11-09, the user's choice "改成 5 分鐘"): **the editor's decoder waits five minutes before
  closing** (`VideoEditViewModel.StillFramesIdleClose`, `VideoEditViewModelStillFramesTests`). After the 30 s close the
  next frame of `V_20260110_175957_ES0` took 400-593 ms, where a step takes 29-79. Compare and a paused pin keep 30 s,
  and closing the editor still lets go at once. What it holds on longer was read only in a test host (private bytes 69
  → 477 MB open), not in the app, where such figures have come out lower.
- Done since batch 1 (2026-11-09, "繼續下一步"): **pressing Play opened the video again** (Compress and the editor). The
  editor kept one decoder for its paused frames and made another for each playback. `LibavVideoFramePlayer.RunFrames` is
  now the decode loop alone, over an `OpenDecode`, and `LibavStillFrameDecoder.PlayPiecesAsync` runs it with the file and
  decoder it keeps, on from where they stand when that reaches the playhead: decoding on, or the next frame when the
  decoder has just given the one on the screen (or stands up to 0.15 s past it, the frames out of the decoder but not
  drawn when a playback paused). First frame after Play on `V_20260110_175957_ES0`, in turn (`EditorPlaybackProbeTests.
  PressingPlay_Timed`): 221-508 ms with a player of its own against 14-26 from the kept decoder and 25-36 after a pause.
  A playback holds one decoder instead of two. At 1× the picture now keeps to the sound (`PlaybackMediaClock`, the pin's:
  the first frame at once, the next when the sound has started, a second at most), so an instant first frame does not
  run ahead of a sound that takes 30-150 ms to open. Frames asked for during a playback wait for it to end, and a redraw
  for a new crop or rotation while playing is left to the next frame. Also fixed: an old playback's end stopped the sound of
  the new one that a change of speed or an edit had just started (`Stop` only for the playback still current); frames
  left in the decoder at a short cut could make it refuse the next packet (EAGAIN from `avcodec_send_packet`, not seen in
  1,200 short cuts of that video, but the send path allows it), so it now empties them first. The paused frames go through
  `LibavBgraConverter` as the played ones do. Hand check owed, with the one above: Play with sound on a 4K video, that
  picture and sound agree.
- Done since batch 1 (2026-11-09, the user's choice "加上快捷鍵"): **the editor's keys**. Space plays and pauses, the left
  and right arrows step a frame (`VideoEditWindow.OnTransportKeyDown`, in the tunnel phase, because a focused button
  takes Space and the seek bar the arrows). Text boxes, drop-down lists and popups keep them, and the compare view has
  its own transport. A held arrow repeats faster than a step back decodes (79 ms against about 33), and a request
  cancelled at each repeat would show nothing until the key came up, so held steps go through a second
  `ScrubFramePump`, of exact frames. A held Space toggles once. `VideoEditWindowScrubTests` (opt-in EDITOR_KEYS_PROBE=1,
  real key events on the real window; fails with the handler on the bubble phase, without the held-Space guard, and
  without the check for text boxes) and `VideoEditViewModelStillFramesTests`.
- Done since batch 1 (2026-11-09, "繼續優化"): **the editor's first frame starts at the press of Edit**, not once its
  window has opened, so it decodes on the pool while the UI thread builds the window (`StartDecodingFirstFrame`; the
  window's own request then finds it, in `LibavStillFrameDecoder`'s last frame). ABAB in fresh processes on the 4K HEVC
  phone video, a headless host with the app's precompiled assemblies (opt-in EDITOR_OPEN_PROBE=1): the first open of a
  process 660-883 ms to the picture before, 460-523 after, 1-2 ms behind the window itself, which takes as long as it
  did (434-522); later opens 286-524 before, 251-408 after. `VideoEditViewModelStillFramesTests`.

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
  `MemoryTrim.Before: idle:` line naming `pin-closed`.
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
  bar should keep moving to the end instead of sitting at 100% for most of the time, and with nothing edited the GIF
  should come out sooner than in 0.70.0 and play as before. Pause past the middle: the bar should stop until Resume, and
  the file still come out whole; do it once more with the recording trimmed in the editor. Start it again and cancel
  past the middle: the file should end as cancelled, not done, with no GIF in the output folder. Then the same with
  WebM, whose sound should stay with the picture.
- **Exporting a pinned video as WebM**: pin a recording with sound, cut a piece out of it, then Save as WebM and Copy as
  WebM. The pin should keep playing smoothly while it exports, both files should have the sound, and the temp folder
  should keep no new `GimmeCapture_Export_*` folder after the save (the copy keeps one, holding just its WebM).
- **A paused recording**: record half a minute with two pauses, as MP4 and again as MKV. Each file should play through
  all three stretches with the sound in step at each join, and the log's `Recording.FinalizeTimings` line should say
  `segments=3`.
- **A long recording with sound**: record five minutes or more with system audio and the microphone, pausing once. The
  stop should save it within a second or so, with `sound=recorded` and a small `recordedSound` in its
  `Recording.FinalizeTimings`, and the sound in step to the end. The recording should not drop frames or stutter more
  than before, with the encoder beside it. Then one as GIF and one as WebM: saved as before, with no `sound=`.
- **A 4K video in the editor and in a pin**: open a 4K phone video in the Compress editor and scrub, step and jump
  about; Task Manager's commit size for the app (Details tab) should rise about 0.45 GB, not 1.2. Pin a 4K video and let
  it loop: it should play smoothly, its commit size staying about the same.
- **The update toast in a launch to the tray**: with an older version installed, start it from the tray (as at sign-in)
  with automatic update checks on. A toast "GimmeCapture vX is available. Click here to update." should show once, for
  about 10 s, with a hand cursor; clicking it opens the main window, which asks about the download. Left alone it goes,
  and opening the main window later still asks.
- **The hover outline with "Use less memory" on**: with the setting on and the app restarted, open the overlay and
  move over windows. The dashed outline should flow while the mouse moves (a little coarser than on the graphics card),
  stand still about a second after it stops and flow again at the next move; moving to another window glides as
  before. Task Manager should show the app at about 1% with the mouse at rest. With the setting off, nothing changes.
- **Opening a capture with "Use less memory" on**: with the setting on and the app restarted, open the capture overlay a
  few times, the first after the restart included. No white rectangle at the top left as it opens; the overlay and its
  toolbar appear as quickly as before. Then a recording without annotations, which excludes the overlay from capture
  once it starts: the recording should still leave the overlay's frame out of the video.
- **A pinned recording's loop**: pin a recording with sound (yours are 30 fps) and let it loop a few times. The
  picture should not stop at the loop, the sound should not break off, and a click or a key should sound on its frame
  in every pass. It should look smoother than before: every frame shows now. Mute and unmute while it plays (the sound
  comes back in step after a short restart), turn the loop off (it stops at the end), trim it in the timeline (it loops
  the trimmed stretch), play it at 1.5× (pass by pass, as before). A recording whose sound runs past its picture
  should loop where the picture ends, without holding its last frame.
- **Colours**: pin a video from a phone or one downloaded in HD, and compare it with another player: reds and greens
  should look the same (they were darker and yellower in the app). Pin one of your own recordings: as before. Play a
  new recording in a browser: its colours should match the screen (reds were a little orange there).
- **A launch to the tray**: give the capture hotkey a combination another app holds (or start a second app that
  takes it first), sign out and in (or start the app with `--startup`): a toast should name the hotkey, and clicking
  it should open the main window. From the tray, open the overlay and use something that needs a model you have not
  installed: the main window should open on the Modules tab.
- **A paused pinned recording's seeks**: pause a pinned recording, press the 5-second buttons both ways and drag the
  seek bar: each frame should appear about at once (it took a moment before), and the picture should follow the bar
  while it is dragged, the thumb staying under the pointer (at the end too); play on, pause again and repeat.
- **The overlay's loading bar**: open the capture overlay with the AI scan on: the red bar along the top should sweep
  while the scan runs and go once it is done, and do the same during a translation.
- **A recording on the NVIDIA card**: record a region with strong colours (a page with red and green on it) and play
  the file in its pin and in a browser or Windows' player: the colours should match the screen in both (a browser
  showed reds a little orange before). The app should take less of the processor while it records (Task Manager), and
  a recording of the whole screen should play smoothly.
- **A pinned recording copied as a GIF**: copy one of your recordings as GIF from its pin, a short one and a long one.
  It should take half as long as before or less, and at Medium the file should be smaller (a 34-second one about
  half): zoomed in, smooth areas show a fine regular grain where they showed noise, as a GIF recording at Medium does.
  A GIF export from the Compress tab at its defaults: its bar should fill evenly, about half way when the palette is
  done, and the file should be about as small as the pin's copy at Medium, with the same grain.
- **A pinned recording, pixel for pixel**: at 125%, record a region with small text in it and pin it. The pin should
  look as sharp as the screen did (it was soft before), the red border hugging the picture's outside where the
  capture overlay drew it round the selection. Resize the pin: smoothed as before. Move it to a screen at another
  scaling: shown, smoothed. Then the same with the pin's decoration hidden and its border shown: the border still
  shows on the left and right. Crop it: the part selected is the part cut out (at 125% it was not). Draw on it, mask
  a part of it: each lands where it was drawn, and the export is as before.
- **A rotated compress**: compress one of the 4K phone videos turned 90° in the editor, nothing drawn on it. It
  should finish in about a quarter of the time it took before, turned the right way and looking as before. Then one
  with something drawn on it, which takes as long as before, the drawing where it was drawn.
- **A long compress with sound**: compress a recording of several minutes with sound at the tab's defaults. The file
  should be done within a second or so of the bar reaching 100%, and play with its sound in step to the end: in its last
  minute, the sound of a click or a key should land on its frame, as in the recording (it fell 55-66 ms further behind
  every 30 s before). Pause it midway: Task Manager should show the app's processor use drop to about nothing until
  Resume. Cancel another midway: no `GimmeCapture_Compress_*` folder should stay in the temp folder. Then the same with
  a trimmed recording, which takes the single-export path, and an audio-only M4A export of a long recording.
- **Stepping in the Compress editor**: open a recording of a minute or so in the editor, step forward and back, skip 5 s
  both ways, drop the slider, rotate and turn crop on and off. Each should answer at once, a step back a little slower,
  and show the right frame. Open the quality comparison, step there too and close it: the log should have no
  `Compress.Compare` or `Compress.Edit` errors. Leave the editor alone for a minute, then step again: it should still
  work. Close the editor and rename the recording in Explorer: it should not be in use.
- **The overlay's candidate boxes**: open the overlay over a window full of text, on each screen. The scan's boxes and
  the windows' outlines should show as before, in the same colour, width and places, and a selection dragged while
  they come in should not catch. In Translate mode, the text search's boxes too.
- **Reading text**: Quick OCR and translate a light window and a dark one (a dark-themed editor, say). The text should
  come out as before, a little sooner. In Translate mode, the text search's boxes should land on the text as before,
  on each screen.
- **Recording through Desktop Duplication**: record a region of a few seconds in Record mode, with the cursor in it,
  then pause and resume, and stop. The log should show `Desktop Duplication: display 0 of adapter 0 (AMD Radeon(TM)
  Graphics)` and `Capture source: ddagrab` for each part, and Task Manager about a third of the processor use a
  recording took before. The file should look as before: the cursor drawn, the colours as on screen, no trace of the
  overlay's frame or toolbar. A region across two displays, if there are two, should log `no single upright display
  holds the region` and `Capture source: gdigrab`. Lock the computer (Win+L) mid-recording and come back: the log
  should show `Desktop Duplication stopped`; whether the recording then carries on (through a fresh duplication or
  gdigrab) or ends, as a gdigrab one did at a lock, no test has seen.
- **Translating with the local model**: with Traditional Chinese as the target, translate an English window, then a
  Japanese one, within two minutes. Both should come out in Traditional characters (檔案, 設定, not 文件, 设置); the
  second press should take about half as long as the first used to, and its `Translation.Timings` line should say
  `context=kept` with a `kept=` near 72.

## How to run a batch

Six `isolation: "worktree"` agents on disjoint file sets, each told: no commits, no CHANGELOG/csproj/lock files, copy
`src/GimmeCapture/ffmpeg-lib/*.dll` into its worktree, targeted tests with TRX output, report commit messages, hunks
and a CHANGELOG bullet per change. Apply every diff to main's working tree, add follow-ups, run `verify.ps1`, then
split into commits with a plan file and replay script, dry-run on a temporary index first. The method and its traps
are in the assistant's memory note "integrating-parallel-agent-worktrees".

## Backlog

### OCR, AI and translation

- **Recognition runs one box per run** (measured 2026-11-03: not worth it on the CPU). The user's 52 history
  captures, 645 boxes: 23 ms a box, about 2.3 passes of 9 ms of the model each. One run over up to four boxes of the
  same width took 13% less and gave the same output; groups of 8-64 took as long or longer and changed outputs (by up
  to 2e-5). Boxes cannot run side by side: `OcrRuntimeService.BeginSessionUse` takes one inference lock, and running
  them 2-6 at a time gained nothing anyway. ONNX Runtime's own threads are best at its default, one a physical core
  (6): 12 made detection 65% slower and recognition 7%, 4 made recognition 20% slower. The time is the model's.
- **Auto re-probes the language in every overlay** (S, the user's call). `OcrScriptDetector.cs:85-112`: keep the Auto
  verdict app-wide. Each probe swaps recognizers, which now costs ~135 ms each (the shared detector stays loaded). Only
  matters with several OCR languages installed, as the user has (Auto, with Simplified and Traditional Chinese). The
  detector keeps its verdict for one overlay on purpose: kept app-wide, a capture in the other script (a Simplified
  Chinese page after Traditional ones) would be read with the wrong model until something re-probes.
- **The overlay scan's two screens, one after the other** (measured 2026-11-08: left). Both screens resize to
  1280x736; with the installed detector, two runs took 297-307 ms and one batch of the two 360-366 ms (padded to the
  same size, 20% slower), and `DetectText` of both 324 ms: the time is the model's own, about 150 ms a screen, the
  resize, tensor and boxes about 10. Only a smaller input would be quicker, which reads small text worse: the user
  chose each screen read whole (2026-11-06).
- **Two resizes per OCR search** (S). The search copies the grab to 1600 px (Mitchell, now on the pool: Status), then
  `DetectText` resizes again to 1280. One would do, but it changes what the detector sees: compare the boxes found on
  real captures first. Left (2026-11-07): it is the translation's search, and the installed copy has no translation
  model (its `llm\models` folder is empty), so the user does not run it.

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
- **Colour conversion for QSV and MF runs on the CPU** (S-M; NVENC and AMF are done, Status). Frames go through
  single-threaded `sws_scale` to NV12 (`LibavGdigrabMkvSession.cs` `CapturedFrameConverter`, `LibavWgcMkvSession.cs`):
  10.8 M cycles a 1456x896 frame. Check what each writes into the stream first, as AMF writes BT.709 whatever it is
  told (needs an Intel GPU). Overlays add a BGRA copy; the webcam frame is copied full size every output frame
  (`WebcamCaptureSource.cs:157`).
- **The stop moves its file, and stops sources one after another** (S-M). `RecordingService.Finalize.cs` muxes into
  the session folder and moves the file to the output folder, a whole copy when that is on another drive; sources stop
  in series (`LiveSession.cs` `StopCurrentSegmentAsync`). Mux into the destination folder rather than moving there. Only
  a fixed save folder on another drive pays for the copy: with AutoSave off, as the user has it, the file goes to the
  app's own Temp folder, beside the session, and the move is a rename. The fixed wait, the paused recording's second
  video write and the AAC encode are done (Status).
- **The Linux audio still gates whole packets and ends each part short** (S; needs a Linux desktop to check).
  `LinuxPulseAudioRecorder` keeps or drops each packet on `RecordingAudioGate.IsOpen` and writes no silence at the
  end, so after a pause every later part plays early by the video's overrun (45-105 ms on Windows). Give it a `Feed`,
  placing each packet by its arrival as on Windows, and the `PadToVideoLength` step; check a paused recording's file
  with `RecordingAvSyncTests`' file mode.
- **Overlays still convert every frame twice** (S-M). With a cursor highlight, clicks, keystrokes or the webcam on,
  `CapturedFrameConverter` copies each frame to BGRA, draws on it and converts it again; on NVENC and AMF, which take
  BGRA, the overlays could draw on the captured frame itself (after `av_frame_make_writable`) and it could go to the
  encoder as it is, saving both passes (est. twice the 48-49 M cycles/s measured for one at 1450x900@30). The user
  records without overlays.
- **Desktop Duplication straight into the encoder** (L). A region recording reads the duplicated region back to the
  CPU (`hwdownload`) for NVENC to upload again; where the display and the encoder share an adapter (a desktop's own
  card), the D3D11 frame could go to the encoder as it is. On the laptop the panel hangs off the AMD chip and NVENC is
  on the NVIDIA card, so the copy through memory stays there. HDR displays are duplicated as 8-bit BGRA, which Windows
  converts; not checked on one.

### Compress and the editor

- **The recording's and the pin's GIF and WebM show an indeterminate bar** (S, the user's call), as every progress
  window does: a bar that fills changes its look; the transcoders can report. Compress's GIF and WebM (the extra encode,
  progress, cancel and pause) are done (Status).
- **A target size at or above the source's size still encodes** (S). `CompressBatchRunner.cs:116-124`: stream-copy
  with `LibavMuxer.RemuxAllStreams` when unedited, otherwise cap at the source bitrate. Audio-only M4A from an AAC
  source can stream-copy too.
- **Each segmented chunk reopens and probes the input** (estimated 2026-11-04: 1.5-3% at the user's settings, left).
  `CompressPipeline.EncodeOneFileSegmentedAsync` runs `LibavClipExporter.TryExport` for every 30 s chunk, one after
  another, which opens the input, finds its streams, seeks and decodes from the keyframe before the chunk again. On a
  4K HEVC phone video a far seek decodes for about 280 ms on the CPU (the D3D11VA item's measurements), so with the open
  about 0.35 s a chunk, against 12-25 s for a chunk at the user's presets ("slow", 1080p or 480p, four files at once).
  One decode feeding every chunk would have to keep the resume from the first unfinished chunk. The writes, the join and
  the sound are done (Status).
- **Annotations take every frame through BGRA** (S-M). `LibavClipExporter.cs`: an export with something drawn on it
  converts each frame to BGRA at decode size, draws, turns it there if it is turned, and converts back. Render static
  annotations once and blend them in YUV, or draw at the output size. Rotation without annotations is done (Status).
- **Small one**: preview frames are decoded on the GPU, downloaded at full size and scaled on the CPU. The CRF slider's
  save at every tick is done (Status).
- **A target size at or above the source's size**, above, left (2026-11-07): a stream copy would have to match the
  codec, height, frame rate, filters and audio asked for, or it would silently leave them out, and the user compresses
  with CRF presets. The user's logs of the last 7 days hold no compress run at all, only queue probes.

### Pins

- **A video pin still reopens at every seek, and a cut timeline at every piece** (M). The loop of a plain or trimmed
  pin at 1× plays in one decode with its sound in step (Status); a seek, pause/resume, unmute or speed change still
  starts a new `PlayFramesAsync` (68-123 ms to the first frame with D3D11VA on the user's recordings, measured
  2026-11-03, 13-44 on the CPU), and so does every piece of a timeline with cuts or speeds, whose sound restarts per
  piece. Seeking inside the kept decode would need the sound clock's ticket to change mid-run
  (`PlaybackMediaClock` follows one). A paused pin's seeks keep a decoder now (Status).
- **Each copy from a pin writes a `GimmeCapture_Copy_*` folder** (S, disk), swept only at start-up after 12 h, each
  holding the copy's file (1 MB for a 4K picture of colour blocks as a fast PNG; not measured for photos). Deleting the
  last one at the next copy would break a paste of the older file from Win+V's history. The rest of that item is done
  (Status). Left (2026-11-07): the user's Temp folder held none.
- **A playing pin's drawing** (M-L). Drawing on the GPU, Avalonia's render thread takes 368-377 of a playing pin's
  814-823 M cycles/s at 1450x900 with the toolbar shown, 322-334 hidden (`PinPlaybackProbeTests`), uploading the pin's
  `WriteableBitmap` every frame and waking 420-500 times a second; on the CPU it took 646. An RGBA bitmap instead of BGRA
  changed nothing (487-489 against 483-494), and a paused pin's render thread is near idle, so it is the per-frame
  upload. A frame the GPU could show as it decoded it (Avalonia's GPU interop) would leave out the download, the
  conversion, both copies and the upload, at the cost of a second way to draw a pin. The toolbar's shadow is done
  (Status).
- **Lower**: a video pin still copies each frame once on the decode thread, from the player's frame into its bitmap
  (about 50 M cycles/s at 1450x900); converting straight into the locked bitmap would need the player to ask for a
  destination before it converts, and the pin's per-frame checks (the trim end, the sound's start) to run before it.
  Pins keep decoding when covered (Windows tells a window nothing of being covered; left). The conversion to BGRA and
  the UI thread's copy are done (Status). The translation layer's region per item is done (Status).

### Snip overlay

- **A manual scroll keeps grabbing when nothing scrolls** (S). The producer grabs region frames back to back
  (`ScrollingManual.cs` `ManualProducerLoopAsync`) and the consumer aligns every one; the signatures are pooled and a
  frame is now matched only near where the motion puts it (Status). Skipping a frame identical to the last, or pacing
  the grabs while nothing moves, would idle it, at the risk of a fast scroll's first frames. The user does take
  scrolling captures (2026-11-08), though the last week's logs hold none.

### Idle, start-up and memory

- **Cinzel lacks ▶ ◀ → ⚠ ✓** (S), so English falls back through the JP chain for those (found with the CJK font item,
  done: Status). Since the CJK fonts are read in place, that fallback costs no copy either.
- **The raw-input sink duplicates the keyboard hook** (S). `WindowsGlobalHotkeyService.cs:488,537-577`: about four
  hotkey-thread wakeups per keystroke typed elsewhere. Register the sink only when the hook fails or stops counting.
  Left (2026-11-07): Windows drops a slow hook without a sign, and the sink is then what keeps a combo another app
  holds working; it costs a second wakeup on a processor already awake for the key, microseconds. Measuring it would
  mean typing keys into the user's desktop.
- **A launch at sign-in waits on the hard disk** (the user's pick on 2026-11-01; the app stays on D:, the user does not
  want it taking space on C:). Three sign-ins on 0.70.0 (2026-10-30 and -31) took 67-77 s until the hotkeys worked,
  other launches 0.5-7 s. The first sign-in on a build that says why (2026-11-02, 4f0d37fb, `launch=tray`):
  `beforeMain=18210/172 log=8787/172 platform=9205/578 reactiveUI=2644/78 xaml=1700/78 viewModel=2904/141`, total
  27.7 s at 1.2 s of CPU, the hotkeys about 46.5 s after the process started, `systemCpu=13% hardFaults=4188/35944
  disk=hdd`, priority normal: waiting on the disk, not working. The app runs from `D:\GimmeCapture_win-x64` (330 files,
  490 MB), a hard disk that Steam, Everything and Insta360's quick-look also start from at sign-in. The running app had
  92 of those files mapped, all as images, 142 MB, 55 MB of it resident: about 13 KB a hard fault, one random read at a
  time. `StartupPrefetch` now reads the files the last start mapped (`versions\<v>\startup-files.txt`, those loaded
  before Main first) on a thread of its own from the top of Main, and the line ends `prefetch=<ms>/<MB>`.
  `StartupPrefetchProbeTests` (opt-in) shows the effect on that disk: faults on cold copies served from the cache
  after one sequential read. The first sign-in with it (2026-11-03 20:00, v0.71.0): `log=5305 platform=2627
  reactiveUI=1106 xaml=706 viewModel=958`, Main to the hotkeys 10.8 s instead of 28.3 at 1.2 s of CPU, with
  `prefetch=running` (141 MB not read in 10.7 s, so contended is that disk at sign-in). But `beforeMain` took 27.1 s
  (18.2 the time before): 71% of the 37.9 s, before any of the app's code. So the Run key now starts
  `GimmeCapture.Launcher.exe` (`src/GimmeCapture.Launcher`, .NET Framework 4.8, which Windows keeps on C:): it reads
  `ReadAhead.BeforeMain`, the host's and runtime's files (63 MB), through once each, then starts the exe with its
  arguments; "Run on startup" claims this install's own old entry (same folder, same install) on the next run, and the
  line ends `launcher=<ms>/<MB>`. The first sign-in through it (2026-11-04 18:43, 0.71.0+bed41298):
  `launcher=2588ms/62MB beforeMain=5936/78 log=8239/234 platform=3161/1016 xaml=699/156 viewModel=2730/328`, the
  hotkeys at 15.2 s, so 23.7 s from the launcher's start against 37.9: `beforeMain` 27.1 s to 5.9, but `systemCpu=85%`
  (17% the time before) and `prefetch=running` again. After Main is now most of it, and at 17% (2026-11-03) it waited
  9.5 of its 10.7 s on the disk, the app's own read of its list running alongside the start's faults. So the launcher
  also reads that list before it starts the app (`ReadAhead.FromList`, now `Plan`: 73 files, 76 MB after `BeforeMain`'s
  62 for the installed copy), finding it as `AppStoragePaths` does from the app assembly's product version
  (`ReadAheadTests` pins the path to the app's and the installed copy's instance key). The sign-in after it (2026-11-05
  20:08, still 0.72.1+d33e5172): `launcher=22594ms/141MB beforeMain=1402/94 log=518/109 platform=862/484
  viewModel=282/94`, the hotkeys at 1.9 s, so 25.9 s from the launcher's start (23.7 the time before) at
  `systemCpu=29%`. The start found everything in memory, but the launcher read the 141 MB at 6 MB/s from a disk the
  rest of the sign-in read too: the wait only moved. A start uses much less than it maps: the installed app, its window
  opened and a few captures taken, held 56 of the 152 MB its folder's files map, 6 of its own assembly's 39.8 (30 of
  them the CJK fonts), 0.2 of WinForms' 13.1 and 1.8 of Skia's 11.1 (QueryWorkingSetEx over each module, after its idle
  trims). So each start now writes, beside each file it mapped, the stretches of it the start used
  (`ImagePagesInUse`: the module's pages in the working set, through the PE section table to offsets in the file,
  joined where less than 256 KB apart; `name|offset+length,...`, a line without them still read whole), and the
  launcher and the app's own read-ahead read only those (`ReadAhead.Plan`, `ReadStretches`). `StartupPrefetchProbeTests`
  on that disk: faults after reading the stretches served from the cache, 18 ms against 1.3-1.9 s straight away and 12
  ms after the whole files. That shipped in v0.74.0 and made the sign-in slower. Installed by hand as 0.73.0+b0a10013,
  a window launch's list came to 87 MB of the files' 143 (2026-11-05 23:31), and the sign-in that read it (2026-11-06
  18:31): `launcher=42583ms/87MB beforeMain=1995/62 hotkeysAt=1674 systemCpu=60%`, 46 s from the launcher's start
  against 25.9 the day before: 2.0 MB/s against 6.2. `LauncherReadProbeTests` (opt-in) reproduced it on cold copies of
  the installed files on that disk, threads of the probe reading 64 KB at random elsewhere on it: with eight of them
  the stretches (70.1 MB in 134, a start to the tray) took 35.8 s against 10.2-12.0 for the whole files (133.5 MB),
  with four 15.3-15.8 against 7.4-8.2, and with the disk to itself 3.9-4.0 s, no faster than the whole files' 3.5-4.0.
  Read-ahead, one ReadFile a stretch, `PrefetchVirtualMemory` and stretches joined where less than 1 MB apart changed
  nothing much: on a busy disk each read waits behind the others' however small it is, so the count of reads costs,
  not the bytes. What did help was more reads of the launcher's at once: the whole files on four threads took 4.1-4.8
  s with eight threads of load and 2.0-2.2 s with the disk to itself (two threads 5.4-6.3 and 2.5-2.8, eight 2.9-4.6
  and 2.0-2.4 while taking more of the disk from the load). So the stretches went (`ImagePagesInUse`, the list's
  `|offset+length` tails, which are still read past) and the launcher and the app's own read-ahead read whole files,
  four at a time (`ReadAhead.ReadAll`, `ReadThreads`). Installed as 0.74.0+55673b06, the sign-in after it (2026-11-06
  22:02): `launcher=4015ms/136MB beforeMain=3185/78 hotkeysAt=1199 systemCpu=99%`, about 8.4 s from the launcher's
  start to the hotkeys against 25.9 on 11-05 and 46 that evening, `beforeMain` long only because every processor was
  busy. Eleven seconds in, the idle warm-up (`Overlay.WarmUp ... bind=739`) held the UI thread for 637 ms, 2% of it on
  the CPU, 1,343 page faults: it loaded files the list, written at `hotkeysAt`, did not have (HarfBuzz, Avalonia.Markup,
  UI Automation, 2.4 MB of the 19 files and 192 MB the running app later had beyond it; FFmpeg's 190 MB came with a
  video list opened later). So a start to the tray now adds to the list what it loaded by 5 s after the warm-up began
  (`StartupPrefetch.RecordWarmUp`, `WithWarmUp`), unless a real overlay opened meanwhile or the additions come to more
  than 16 MB (`WarmUpLimit`). The sign-in on 2026-11-07 06:18 (same build): `launcher=6582ms/134MB
  beforeMain=2179/78 hotkeysAt=690 systemCpu=69%`, about 9.5 s; the warm-up's bind took 295 ms and logged no stall.
  `beforeMain` at the four sign-ins grew with `systemCpu` (29% 1.4 s, 60% 2.0, 69% 2.2, 99% 3.2) against 0.07-0.2 s at
  other launches, with 62-94 ms of it on the CPU: the start waited for a processor. A throwaway probe kept all 16
  processors busy at normal priority (32 threads) and started a .NET 10 console program at each priority, ten times
  each, alternating: to Main 817 ms (551-862) at normal against 376 (180-420) above normal (54-55 idle), and 300 ms of
  work in 570 (531-687) against 317 (307-326); above normal reached Main later than idle because its parent, starved
  too, raised it only after starting it. So the launcher raises its own priority first, then the app's right after
  starting it (`ReadAhead.StartPriority`, AboveNormal), and the app lowers itself to normal when `Startup.Timings` is
  complete, or a minute after Main (`StartupPriority`), only if it still has the launcher's priority. Checked with the
  built launcher and a stand-in app: it ran AboveNormal with the hand-off. Next: a sign-in on that build (`beforeMain`
  near the other launches' at a busy `systemCpu`, and the running app at normal priority after). Moving the install to
  the SSD would do the rest; the user declined. `hardFaults` stays high, as a fault on a file read through still
  counts though the cache serves it, so read the stage times. On a warm start `xaml` holds what `avalonia=` (~375 ms)
  was blamed on: the whole ColorPicker theme for one `ColorSpectrum` (`App.axaml:60-68`) and all three locale files
  parsed through `x:Static`.
- **A Direct3D 11 decode leaves 150-240 MB in the process after it ends** (measured in the app 2026-11-04: it does
  not, left). A temporary App hook opened a video pin of one of the user's recordings with software rendering on, as
  they run, in fresh processes, GPU and CPU decoding alternated: on a 1134x624 H.264 one (6.2 s, looping), private bytes
  40-41 MB before, 197 while playing on the GPU against 126-139 on the CPU, 110-112 three seconds after closing against
  75-77, and 76-77 against 76-78 ten seconds later; on the 1450x900 HEVC one 195 against 190 playing and 87 against 82
  after. The GPU's extra is given back a few seconds late; the rest is what any pin leaves (codecs, audio). Process CPU
  while playing was the same either way, 20-21% of a core, most of it the CPU renderer presenting the pin's frames.
  What follows is the item as first written, from a test host. In a test host, private bytes went from 40 MB to 189-196 MB after a player's D3D11VA decode of a
  1450x900 recording, three in a row, and from 67 to 303 MB after a 1920x1080 clip's; decoding on the CPU gave all but a
  megabyte or two back. A video pin's playback and the editor's and the compare's frames decode this way by default
  (`LibavVideoFramePlayer.TryInitHardwareDecode`), so a run that has shown a video keeps it. Probably the driver's, like
  NVENC's (below): check the app's commit size in Task Manager before and after a video pin plays, then whether one
  device kept for the run, or the CPU for the editor's 720 px frames, holds less. On a 4K phone video a decoder left
  260-420 MB after it closed. What one held while open, a surface for every frame thread, is done (Status).
- **NVENC's driver keeps 200-260 MB once a recording has used it** (measured in the app 2026-11-04: it does not, left).
  A temporary App hook recorded six seconds of a 975x554 region as the user records it (H.265, 30 fps, system audio),
  software rendering on, in fresh processes, NVENC and the software encoder alternated: private bytes 40-41 MB before,
  143-151 with the Record overlay open, 346 while recording on `hevc_nvenc` against 266-271 on `libx265` (ultrafast),
  and 13 s after closing 163-164 against 153-154. NVENC took 16% of a core while recording, libx265 44%, so the user's
  "prefer hardware" stays the better choice. What follows is the item as first written, from a test host. Release
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
7. ~~Fix "Every editor or compare step reopens the file"~~: done 2026-11-02 (Status). A step back still decodes from
   the keyframe, 0.07-0.1 s.

**Done when** a video opens in the editor from a pin, History, a finished recording and the tray and exports straight
from there; the Compress queue flow still works; new strings are in all three locales; the edit session, its sinks and
the export have tests; `scripts/verify.ps1` passes before each push.

If the Compress tab should be easier to find too, a tray-menu "Video tools" entry opening the main window on that tab
is enough: batch compress is set-and-run, so a tab suits it.
