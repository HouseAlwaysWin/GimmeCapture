# GimmeCapture Release Catalog

This catalog summarizes released versions from `v0.1.0` onward based on repository tags and commit history.

## v0.72.1 - 2026-11-04

- **Pinned videos** — a pinned recording loops without stopping at the loop, its sound looping with it in step with
  the picture instead of breaking off and coming back a different amount off it every time, and a 30 fps recording
  plays in its pin at its full 30 frames a second instead of about half that.

## v0.72.0 - 2026-11-04

- **Translation** — the local model translates in about half the time, as it keeps its instructions read between
  translations, and asked for Traditional Chinese it now writes Traditional Chinese instead of Simplified.
- **Recording** — stopping no longer waits for the sound to be encoded, so a ten-minute recording is saved in a
  fraction of a second instead of about ten; every stop is a little quicker, and a paused recording's video is
  written once less.
- **Compress and export** — a rotated video compresses in a quarter of the time or less; a long compress keeps its
  picture in step with its sound and writes its video twice fewer times; a long export no longer takes a gigabyte or
  more of memory for its sound, nor sits at 100% while it builds it.
- **Videos and pins** — editing or pinning a 4K video takes about 0.75 GB less memory, and removing the background
  on another pin, or on a plain background, no longer loads the model again.
- **Capture overlay** — it no longer stutters as its text scan comes in, and with "Use less memory" on it opens
  without a white flash and takes a fraction of the processor while it is open.
- **Start-up and updates** — starting with Windows from a hard disk spends less time before the app even runs, and a
  launch to the tray says when there is an update, with a notification that opens the update when clicked.

## v0.71.0 - 2026-11-03

- **Hotkeys over administrator windows** — a new "Run as administrator" setting lets the hotkeys work while Task
  Manager or another elevated window is in front; handing over to another copy at start no longer shows a start-up
  error, and a launch to the tray shows its toasts, the capture countdown and the update offer.
- **Far less memory** — OCR, background removal and smart selection no longer leave hundreds of MB to gigabytes
  behind, the settings window gives its memory back five minutes after going to the tray, History keeps only the
  thumbnails on screen, and a new "Use less memory (draw with the CPU)" setting trades processor time for memory.
- **Quicker captures** — frozen screenshots, copying and pinning encode and hold the picture once; the first capture
  after an idle spell no longer freezes the app; a capture hotkey no longer waits for a settings save, and a run's
  first Record overlay no longer waits for FFmpeg; reading a capture's text takes about a quarter less time.
- **Recording** — recorded sound starts with the picture and stays with it through pauses and quiet stretches, WebM
  recordings keep their full length, and the first recording of a run starts about as quickly as the rest.
- **Compress and export** — long GIF exports no longer fill memory, uncompressed PCM sound is kept, cancel and pause
  work all the way through GIF and WebM, 29.97 fps and variable-frame-rate sources stay in step with their sound,
  H.265 and WebM use every core, and the editor steps through frames at once.
- **Idle and start-up** — the app wakes about a third as often while idle, a start from a hard disk reads its files
  ahead instead of a few kilobytes at a time, and the log's start-up line says what a slow start waited on.
- **Pins, mosaic and blur** — save works on every pinned image, mosaic and blur preview the frozen picture without
  scrambled pixels, and resizing a pinned image keeps up with the mouse. The Windows download is about 10 MB smaller.

## v0.70.0 - 2026-10-29

- **Faster all round** — the app opens about a third of a second sooner and its hotkeys work about half a second
  sooner; the Windows download is a folder, 75 files and 49 MB smaller, that no longer unpacks itself after an
  update; the History and Compress tabs, copying, saving and pinning no longer freeze the app.
- **Typing elsewhere is never held up** — the hotkeys' keyboard hook and the capture overlay's run off the UI
  thread, so a busy app no longer slows keys typed in other programs, and its hotkeys keep working.
- **Fewer crashes** — a failing action, a smart-selection confirm, switching the translation model mid-translation,
  a pinned video's save dialog or a model choice on the Modules tab can no longer close the app.
- **Smart selection and background removal** — background removal runs on the GPU; smart selection works where the
  graphics card cannot run it, takes about 2 ms of the UI thread a click on a 4K pin, and shows what it removes as
  transparent.
- **Recording** — recording a single window takes about half the CPU, starting and stopping a long recording no
  longer freeze the app, a recording that fails to save is kept, and recordings never saved no longer pile up.
- **Pinned videos** — seeking, dragging the seek bar, muting and editing the timeline no longer stall or stop the
  clip, and a clip without sound no longer fills the log with warnings.
- **Storage, accessibility and languages** — the data folder no longer only grows, AI Core is a 176 MB download
  instead of 514 MB, screen readers can tell the buttons apart, keyboard focus is visible, and no text is left in
  only one language.

## v0.68.1 - 2026-09-16

- **Run on startup works in released builds again** — they no longer ask for administrator rights, which since
  v0.42.0 made Windows silently skip the startup entry. Launching no longer shows a UAC prompt either.
- **Administrator rights only when something needs them** — the "restart as administrator" prompt now appears
  only when a hotkey really is blocked over an administrator window, names that hotkey, and the restart no
  longer exits instead of coming back.
- Updating an install under Program Files asks for elevation instead of silently rolling back, and a second
  startup registration no longer pops the main window open at sign-in.

## v0.68.0 - 2026-09-02

- **Capture menus and dropdowns** — the overlay no longer steals focus, and new **freeze-frame** screenshots
  snapshot the desktop before the overlay opens, the only reliable way to catch tray flyouts and the Start menu.
- **Scrolling capture**: live thumbnail of the growing strip, a live warning when stitching loses track, and
  fixes for long screenshots coming out short or displaying sideways.
- **OCR `Auto` actually detects the language** (it silently always used Chinese), plus three separate crash
  causes fixed in back-to-back captures and AI scan.
- **AV1 recording** where the hardware can genuinely encode it, and **auto-stop at a configured length** that
  pins or saves the clip.
- **Clipboard honesty** — a failed copy no longer reports success, and the confirmation shows a thumbnail of
  what actually landed on the clipboard.
- **Single running instance**; a startup entry deleted from outside is repaired, and a development build no
  longer steals the registration.
- **Removed**: Imgur upload — blocked in Taiwan and the only upload target.
- Consolidates the work tagged `v0.67.0` – `v0.67.2`, which shipped without a version bump or a log entry.

## v0.66.0 - 2026-07-22

- **A/V sync fixed** — video is paced by wall clock rather than a frame counter, so recordings no longer drift
  short whenever capture falls behind the target fps.
- **File-name templates** drive all six places the app names a file; **audio-only extract** to WAV/MP3/M4A/Opus.
- **History** tabs grouped by capture kind (screenshots / recordings), with a new date/name sort selector.
- **Run-on-startup reports when Windows has disabled the entry** in Task Manager → Startup apps, instead of
  showing a switch the OS silently overrides.
- One-click **Imgur upload** (removed again in v0.68.0) and optional quick-OCR `.txt` output alongside captures.

## v0.65.0 - 2026-07-17

- **Translation overlay overhaul** — hidden-toolbar no longer grays Chromium browsers or blocks clicks;
  Ctrl-drag selection and Esc-close work even while another app holds focus; F4 show/hide is global.
- **OCR on Windows 10** — DirectML failures fall back to CPU; inconsistent saved AI settings self-repair.
- **Scrollable long-screenshot pin** (pins at selection size, scrolls the tall image inside) and faster
  stitching / OCR decode / auto-translate.

## v0.64.0 - 2026-07-15

- **Temp-file cleanup**: pin sidecars/copies deleted on close + startup sweep of crash leftovers (no more
  unbounded %TEMP% growth); ~37 Debug-only error sites now reach the AppLog file sink.
- **Performance**: background-removal engine stays warm between uses; mosaic/blur drag no longer re-converts
  the whole source image per frame.

## v0.63.0 - 2026-07-09

- Advanced video editor: **Reset-all** button; timeline keep/drop now takes a **double-click** (single click
  only scrubs).
- **Clipboard freezes fixed** — OCR text, large images, and file copies moved to a timeout-bounded background
  STA thread; quick-OCR shows a standalone spinner instead of appearing hung.

## v0.62.0 - 2026-07-08

- **Compress video filters** (denoise/sharpen/deblock/grayscale via libavfilter), **output categories**,
  per-row open-output-folder, and **GIF/WebM output** with all edits baked in.
- **Pin export-format picker** (image: PNG/JPG/WebP; video: MP4/MKV/MOV/GIF/WebM) applying to Save and Copy;
  GIF recordings keep their audio for pinning.

## v0.60.0 - 2026-07-07

- **AV1 (SVT-AV1) video compression** — the most efficient codec for the smallest files at the same perceived
  quality (10-bit, offline-compress only); B-frames enabled on the export path shrink H.264/H.265 output too.
- **Snip overlay**: configurable toolbar position (top-left / center / right); two-stage Esc (first clears the
  box back to the ready state, second closes); OCR auto-scan reliability fixes.
- **Translation**: a picker/browse button for the custom local GGUF model.
- **Memory**: idle-unload of heavy AI models (translation LLM / background removal / OCR), broader working-set
  trimming (after captures/exports and at startup, not just tray), and several bitmap-leak/crash fixes.

## v0.52.0 - 2026-07-05

- Documentation-only release: refreshed zh-TW/ja READMEs (Linux, scrolling capture, compress, AI modules,
  auto-update) and documented Linux support requirements (x86-64 glibc, X11 session, PulseAudio).

## v0.51.0 - 2026-07-05

- **GimmeCapture now runs on Linux (X11)** — full capture/record/translate/pin/compress feature set ported:
  libX11 snip, XGrabKey global hotkeys, x11grab recording, PulseAudio system+mic audio, V4L2 webcam PiP;
  only WGC per-window recording stays Windows-exclusive.
- **Cross-platform auto-update** (Linux tar.gz swap with backup+rollback) and releases now ship a Linux x64
  self-contained tarball beside the Windows zip + installer under one SHA256SUMS.txt.

## v0.50.0 - 2026-07-04

- New **Compress** tab: in-process re-encode (H.264/H.265, target-size two-pass, resolution/fps-cap/
  preset/CRF, audio bitrate + mixdown), batch queue with per-item settings/presets and live estimate,
  and an advanced editor (trim/speed/crop/rotate + annotations/redaction/freeze) with inline quality compare.
- **Recording**: true per-window capture (WGC), multi-window composite/separate capture, capture-scope
  picker, keystroke overlay, high-quality GIF + disk guard, deterministic audio mixing, webcam PiP.
- **Playback**: GPU hardware-accelerated decode for large files.
- **History** category tab strip; optional **Inno Setup installer**; relicensed **MIT → GPLv3**.
- Internal: Linux port groundwork (platform seams) and Tier-2/3/4 refactors + CI Linux compile gate.


## v0.25.0 - 2026-05-26

- Unified the main action-style hotkeys around the new `F6` and `F8` layout for better consistency.
- Added explicit config versioning and migration support to make future settings upgrades safer.

## v0.24.0 - 2026-05-15

- Refactored service composition and dependency structure to support a cleaner architecture.
- Added persistent settings snapshots, stronger global hotkey coordination, and improved settings management.
- Expanded AI and translation infrastructure with `AIModelCatalog`, `AIResourceInstaller`, `SAM2RuntimeService`, OCR integration, and translation session services.
- Added architecture roadmap documentation and improved FFmpeg/audio handling.

## v0.23.1 - 2026-04-30

- Polished the main window layout and resize-grip behavior.
- Renamed theme-facing styling from `ThemeColor` to `BorderColor` for clearer UI semantics.

## v0.23.0 - 2026-04-29

- Simplified the release workflow and zip packaging process.

## v0.22.0 - 2026-04-29

- Stabilization release for the new publish and packaging flow.

## v0.21.0 - 2026-04-29

- Refined `SnipToolbar` and `MainWindow` interaction and layout behavior.

## v0.20.0 - 2026-04-28

- Packaging and release progression release following the FFmpeg and translation stack refresh.

## v0.19.0 - 2026-04-28

- Interim release continuing the native media and translation engine rollout.

## v0.18.0 - 2026-04-28

- Switched translation to a `LlamaSharp`-based engine and refreshed model-related localization and tests.
- Moved recording and playback further onto native FFmpeg-based components, including decoded-audio fallback and GIF export support.
- Improved translation-mode input handling, command focus behavior, and floating-video playback infrastructure.
- Updated Avalonia/window decoration plumbing and cleaned up deprecated FFmpeg download logic.

## v0.17.0 - 2026-04-10

- Added configurable video recording settings and broader localization coverage.
- Introduced the benchmark suite and performance-focused profiling work.
- Improved translation-mode routing, toolbar positioning, screen-capture affinity, and tray-only startup handling.
- Added the hotkey settings tab and more Win32-specific snip window integration.

## v0.16.2 - 2026-03-24

- Added low-level Windows keyboard hook and click-through integration for `SnipWindow`.
- Introduced floating video trimming controls and a compact numeric step control.
- Expanded `FloatingVideoViewModel` playback and FFmpeg integration.

## v0.16.1 - 2026-03-18

- Added recording finalization for merged segments and multi-format FFmpeg export.

## v0.16.0 - 2026-03-17

- Expanded the app into screenshot, recording, and translation mode routing with dynamic tooltips and commands.
- Added crop, cut, copy, save, and pin flows for floating images with annotation flattening.
- Improved bitmap-processing performance and recording state management.
- Expanded AI-powered interactive removal and segmentation workflows in floating images.

## v0.15.2 - 2026-03-12

- Added floating video playback, trimming, export, and drawing tools.

## v0.15.1 - 2026-03-11

- Expanded recording with pause/resume, finalization, and optional system audio handling.
- Added file reveal helpers, fullscreen selection hotkeys, and more translation/OCR automation.
- Introduced Vosk speech-to-text integration and improved toolbar/recording reliability.

## v0.15.0 - 2026-03-04

- Added system-audio-aware recording infrastructure and localized audio settings.
- Introduced a stronger floating video player foundation with playback controls and duration detection.
- Refined toolbar coverage and documented the new live translation and recording capabilities.

## v0.14.0 - 2026-03-02

- Introduced broader application settings management, including hotkeys and AI configuration.

## v0.13.2 - 2026-02-26

- Added AI-powered object and window detection to `SnipWindowViewModel`.

## v0.13.1 - 2026-02-26

- Added AI scan engine options and localization support for OCR and SAM2 settings.

## v0.13.0 - 2026-02-26

- Expanded translation mode with selected-region translation and full-screen text scanning.
- Strengthened `SnipWindowViewModel` hotkeys, tooltips, processing overlays, and translation readiness handling.
- Added more Win32-specific global hotkey and click-through support.

## v0.12.1 - 2026-02-25

- Added the Windows global hotkey service and its initial unit tests.
- Refreshed the main window presentation with the Gothic metal theme direction.

## v0.12.0 - 2026-02-25

- Introduced OCR plus multi-engine translation support, including Ollama-based LLM translation.
- Expanded `SnipWindow` into screenshot, recording, and translation modes with a dedicated toolbar.
- Added update-service-driven commands and broader settings coverage for language, appearance, hotkeys, and output.

## v0.11.0 - 2026-02-23

- Expanded multi-monitor snipping, hotkey customization, and floating-image annotation support.
- Added OCR/translation services, AI resource management, and more settings surfaces.
- Introduced partialized floating image/video view models and more robust snip window state handling.

## v0.10.0 - 2026-02-11

- Matured floating image and floating video windows into full annotation-capable editing surfaces.
- Added the drawing toolbar, annotation control, blur, mosaic, and richer pointer interactions.
- Expanded snipping, recording, AI detection, pinning, clipboard, and localization support across the app.

## v0.9.1 - 2026-02-09

- Added more complete floating image/video window UI and release workflow integration.
- Expanded localization and AI-assisted snip/recording flows.

## v0.9.0 - 2026-02-09

- Introduced floating image and floating video windows as a core workflow.
- Added clipboard support, global hotkeys, Windows-specific capture services, and early AI background removal.
- Brought in processing overlays, undo/redo, object removal, segmentation, resource downloads, and window detection foundations.

## v0.8.0 - 2026-02-04

- Enhanced the visual theme with skeletal wing assets and heart/skull corner styling.
- Added Ghost Mode for transparent, click-through interaction with background apps during capture.
- Added independent scaling controls for wings and corner icons.

## v0.7.1 - 2026-02-04

- Expanded the snip UI with drawing, text, pinning, decorative assets, and compact numeric controls.
- Improved localization coverage and documented style scaling behavior.

## v0.7.0 - 2026-02-04

- Established the early main window, startup flow, localization service, and multi-monitor capture behavior.
- Introduced the interactive snip window with Win32 region handling, transparent selection, and pass-through interaction.
- Added the first end-to-end capture, recording, and annotation experience.

## v0.6.0 - 2026-02-02

- Added the first README, MIT license, and Traditional Chinese / Japanese documentation.
- Introduced `SnipWindow` for interactive capture, annotation, and recording.

## v0.5.0 - 2026-02-02

- Added `MainWindowViewModel` application logic and `StartupService` for run-on-startup support.

## v0.4.0 - 2026-02-02

- Added application settings, hotkey management, update dialog, update service, and custom theme foundations.
- Introduced the first `SnipToolbar`, initial configuration, Windows capture service, and early recording service.

## v0.3.0 - 2026-02-02

- Added `release.bat` to standardize release script invocation.

## v0.2.0 - 2026-02-02

- Added the first main window UI with general/snip settings and localization service support.
- Established the initial GitHub Actions release workflow.

## v0.1.0 - 2026-02-02

- Initial tagged release baseline for the project.
