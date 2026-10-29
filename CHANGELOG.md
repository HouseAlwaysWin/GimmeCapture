# Release Log / 更新日誌 / リリースノート

## Licensing — 2026-07-03

- **Relicensed from MIT to GPLv3.** GimmeCapture bundles and links against FFmpeg built with
  `--enable-gpl` — including the GPL-licensed **x264**/**x265** encoders used for H.264/H.265 —
  so the application as distributed is a combined work that must be under the GPL. See
  [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Unreleased

### 🛡️ Reliability

- **Recordings keep their system audio in step after a quiet stretch.** While nothing played, Windows handed the
  recording no system audio at all, not even silence: that stretch went missing from the sound track, every sound
  after it played early against the picture, and a recording made while nothing played had no system audio at all.
  The app now plays silence of its own to the same device while it records, which keeps the sound coming without
  being heard.
- **Exporting a long clip as a GIF no longer fills up the computer's memory.** The export kept every frame of the clip
  in memory until it reached the last one, over 1 GB a minute at Medium quality and about 12 GB a minute of 1080p at
  High, whether recording as GIF, saving a pin as GIF or compressing to GIF. It now reads the clip twice, once to pick
  the colours and once to write the frames as it goes, so it stays under about 250 MB however long the clip is, and the
  GIF comes out exactly as before.
- **Videos with uncompressed PCM sound keep it when exported or pinned.** Audio that names no speaker layout, such as
  a plain WAV, was refused frame by frame and came out silent. It now decodes.
- **Exporting a long video no longer hangs while building its sound.** Past about 93 minutes of audio the buffer that
  holds it stopped growing and the export spun forever, ignoring Cancel. Each kept part of a trim now also decodes just
  its own sound, rather than everything from its start to the end of the file.
- **Recordings saved as WebM keep their full length.** The conversion closed up gaps where the capture missed frames,
  so the picture ran short of the sound. Each frame now keeps the time it was captured.
- **Compressed and trimmed videos stay in step with their sound on 29.97 fps and variable-frame-rate sources.** The
  export rounded the frame rate and counted frames, so a 29.97 fps video came out 0.1% fast (3.6 s an hour) and a
  variable-rate recording drifted. Every frame is now placed by its own time at the source's exact rate.

### ⚡ Performance

- **Saving a recording to another drive no longer freezes the app.** After the save dialog, or when a recording
  stopped by itself, the file was moved out of the temp folder on the UI thread. To another drive that is a full copy,
  about 58 MB for each minute of 1080p at 30 fps, and the app and its hotkeys stopped responding until it was done. The
  copy now runs in the background, and the overlay closes and the folder opens once the file is in place, as before.
- **Compressing to H.265 and saving as WebM use every core.** The H.265 encoder worked on one frame at a time and the
  WebM encoder on a single core. A 1080p H.265 compress now runs about 1.5× as fast and a WebM about twice as fast;
  H.264 and AV1 already used every core.

---

## v0.70.0 - 2026-10-29

### 🛡️ Reliability

- **Confirming a smart selection can no longer close the app.** The confirm applies the selection in the background.
  Pressing Esc, picking another tool or undoing on the pin while it ran, or a click's selection arriving just then,
  freed the selection under it: a native crash that closed the app with no error. The confirm now holds on to the
  selection until it is done. Esc or another tool still leaves the tool at once, and the confirm still lands (Ctrl+Z
  takes it back); undoing on the pin meanwhile, or closing it, drops the confirm instead. Esc right after confirming a
  Shift-click selection no longer turns it into a removal of what was selected.
- **A smart selection still on its way when you leave the tool no longer turns up on the pin.** A click's selection
  takes a fraction of a second to arrive. One that arrived after Esc, another tool, a confirm or closing the pin was
  drawn over the pin anyway and stayed there; after Esc and straight back in, it showed up in the new selection. One
  that arrived after undoing that click replaced the undo's selection. A selection that arrives after you have moved on
  is now dropped, and a click still waiting for the pin to be got ready when you leave the tool no longer runs the
  model.
- **Smart selection no longer stops selecting anything after the pin changes while it is starting.** Smart selection
  spends up to a second getting the pin ready. Undoing and redoing on the pin in that second, or switching the
  smart-selection model, left it marked ready with an image size of zero, and every click then selected nothing until
  the pin changed again. A pin that changes while it is being got ready is now got ready again at the next click.
- **The tray menu's *Run on startup* and *Auto check updates* show their saved state from the start.** The menu was
  built before the settings had loaded, so both read unchecked, and choosing one switched it the other way from what the
  menu showed.
- **Dragging a playing video pin's seek bar stops the clip at once.** The drag paused the clip in name only: picture and
  sound played on under it for about 0.3 s, until the seek caught up. Playback now stops the moment the drag starts, as
  the pause button does, and the seek still waits for the drag to settle.
- **Editing a playing video pin's timeline can no longer stop the clip.** The playback, on a thread of its own, read the
  timeline's pieces straight from the list the timeline strip shows, at the start of every piece and loop. An edit
  landing at that moment could stop playback with an error, or play one pass with the edit half applied. It now plays a
  copy the UI thread hands over once each edit is complete. As before, the piece already playing finishes as it was, and
  the next one plays the edit.
- **A playing video pin's time, seek bar and redaction boxes now update on the UI thread.** The decoder moved the
  playhead from its own thread on every frame, and with it rebuilt the redaction preview boxes off the UI thread, which
  Avalonia controls don't allow. The playhead now moves on the UI thread, 15 times a second by default. Pausing,
  reaching the end and every seek while paused now show the frame they stopped on and its time; the last frame or two
  could be skipped before. Dragging the seek bar while the clip plays no longer makes it jump back.
- **`UiStall` log lines say what the UI thread was doing.** A freeze's length alone could not tell a UI thread that was
  working from one that was waiting, or one held by a garbage collection. Each line now gives the UI thread's CPU time
  (Windows) and the GC's pauses and collections during the freeze, and calls it busy, blocked or GC. A freeze still
  going after 2 s is logged at once, so one the app does not survive still leaves a line.
- **A Llama model picked on the Modules tab now stays picked.** Saving the choice wrote the model still selected on the
  Translation tab back over it, so the next start showed the old model again. The choice now updates both.
- **Choosing a SAM2 or Llama model on the Modules tab can no longer close the app.** Each choice saved the settings with
  nothing to catch an error, and a choice made while the previous one was still saving saved alongside it, so the
  earlier choice could be the one left on disk. Choices now save one at a time, in the order made, and a failure goes to
  the log.
- **Text translated again after its cached translation expired stays cached.** Caching it again gave the cache a second
  record of that text. The older record then threw the new translation out as if it were the oldest, so the next request
  ran the model again, and those extra records piled up in memory while the app ran.
- **A save dialog that fails no longer closes the app from a pinned video.** The pin opened its save dialog outside the
  Save command, where nothing caught an error: one from the dialog ended the process, and the command counted as done
  before anything was saved. Such a failure now shows "Save Failed" on the pin, as a failed save already did, and goes
  to the log.
- **Turning AI off no longer freezes the app.** Switching off *Enable AI Features* frees the smart-selection model, and
  that waited for anything still running on it, up to 30 s, with the whole app stopped. It is freed in the background
  now. Smart selection started right after waits for that to finish instead of picking up a model on its way out.
  Before, leaving smart selection and going straight back in could do that, and then every click failed until the tool
  was switched again.
- **Pinning a recording no longer leaves its "Processing recording…" bar on screen.** Since the ReactiveUI 26 update,
  the bar a recording shows while it is finished stayed up after a pinned recording, one more with every recording,
  until the app quit. ReactiveUI 26 queues work the app hands it on the UI thread instead of running it at once, and
  drops what is still queued when the window it was meant for has closed: the capture overlay closed right after the
  recording was finished, before "finished" reached it. Work handed over on the UI thread runs at once again, as
  before the update, and the overlay closes its progress windows when it closes.
- **Failures that went nowhere now reach the log.** About 40 error paths reported only through `Debug.WriteLine`, which
  a release build leaves out. Among them were a recording's audio merge (which then keeps only the first
  segment's audio), FFmpeg failing to start, recording start and stop, device lists, a pin's crop and playback.
  Also logged now: which capture method a recording fell back to, which encoder it chose, and its worker's
  errors. Expected failures, such as no CUDA or a video with no audio, are logged only when
  `GIMMECAPTURE_DEBUG_LOG=1` is set.
- **A busy app no longer slows down typing everywhere else, and its hotkeys keep working.** The keyboard hook that
  serves the hotkeys ran on the app's UI thread, and Windows holds every key typed anywhere until that hook returns.
  So whenever the UI was busy, typing in every program stalled: a key was held up about 0.6 s, and other programs'
  keyboard hooks missed it. Windows also quietly removes a hook that keeps it waiting, which switched off the
  hotkeys only the hook can serve (a PrintScreen owned by the Snipping Tool, a combo another app holds) until the
  next launch. The hook now has its own thread, and with the UI blocked a key is held up 1–2 ms.
- **An open capture overlay no longer slows typing in other programs either.** The overlay, which stays open through
  a whole recording, had a keyboard hook of its own on the UI thread, so every key typed anywhere waited for the app,
  and a long stall could get that hook removed, switching off the overlay's shortcuts until it was reopened. The hook
  now runs on its own thread and lets every key the overlay cannot use straight through. The few it can use (Esc,
  its F-keys, pause and stop, Copy and Save) wait for the app 0.15 s at most, then go to the program in front.
- **An emoji typed into a text annotation no longer turns into '?'.** The capture overlay and pinned images let clicks
  through parts of themselves with a hook in their window procedure, and that hook was an ANSI one. Windows converted
  every typed character to the system's code page and back on its way through, so an emoji, or any character that
  code page lacks (Thai on a Chinese system, say), arrived as '?'.
- **A window that closes just before its recording starts no longer switches window recording off.** Starting to
  record a window that was already gone only failed once window capture had begun starting up, and on a busy or
  freshly started machine that could take long enough to look like stuck capture, which turns window capture off
  until the app restarts (recordings then fall back to the screen region). It now fails at once.
- **Recording a new hotkey no longer triggers the old ones.** While the hotkeys page waits for a new combo, an
  existing hotkey pressed there fired its action and swallowed the key, so it could not be recorded.
- **Settings you change now survive a restart.** 24 settings and hotkeys were written to the config correctly but
  never read back, so every launch quietly reset them to their defaults — and the next save wrote the defaults
  over your choice. Affected, among others: *Freeze screen on screenshot*, *Don't steal focus when capturing*,
  hardware video decoding, video quality / custom CRF / bitrate, the maximum recording size, webcam size and
  circle, the keystroke overlay, and the in-overlay hotkeys for remove background, magic wand, full-screen
  select, mode switching and stopping a recording. A test now fails the build if a new setting is added without
  being loaded.
- **A recording that fails to save is no longer deleted.** When finishing a recording failed, the error was shown
  and then the raw recording was deleted along with the temporary files, so the capture was simply gone. The raw
  recording is now kept, the dialog says where it is, and the reason is written to the log (it used to go
  nowhere in release builds). Recording diagnostics — mux fallbacks, dropped audio — now reach the log too.
- **A failing action no longer closes the app.** Any shortcut or button whose action threw — for example Ctrl+S
  while the auto-save folder is read-only — ended the whole app. The action now fails on its own: it is logged
  and a short message says it did not work.
- **Closing the translate overlay stops its auto-detect.** With auto-detect on, closing the overlay left it
  capturing the screen, running OCR and translating every 1.5 seconds in the background for the rest of the
  session.
- **Smart selection no longer takes the app down.** Pressing Esc or switching tools while smart selection was still
  analysing a pinned image could free the AI model underneath it — a native crash that closed the whole app with no
  error. A model is now released only once nothing is using it. Switching the smart-selection model variant also
  no longer leaves the previous one (hundreds of MB to about 1 GB) stranded in memory.
- **Switching or unloading the translation model mid-translation no longer crashes:** the model is freed only after
  the translation using it has finished.
- **Smart selection works where the graphics card cannot run it.** On some Windows 10 PCs DirectML starts but then
  fails to build the smart-selection model on the graphics card (an older DirectX 12 driver), and smart selection did
  not work there at all. It now falls back to the CPU, as OCR already did: slower, but it works.
  `Sam2Runtime.Loaded` and `OcrRuntime.SessionCreated` in the log say what each runs on and how long loading took.

### ⚡ Performance

- **The app opens a third of a second sooner, and its hotkeys work half a second sooner.** From launch to the main
  window's first frame took 1.35 s and now takes 1.01 s, and the app stops responding for 0.54 s at start-up instead of
  0.88 s (a published build, 10 launches each). The global hotkeys work 0.65 s after launch instead of 1.12 s; one
  pressed before then was lost, not just late. The saved settings are read in the background while the app starts and
  applied before the main window is built, instead of arriving just before its first frame and laying parts of it out
  again; the microphone list is read when the Record tab first opens; the hidden window the hotkeys waited for is built
  only for a launch to the tray; and the AV1 encoder check waits for the first frame. Launched to the tray, the settings
  are applied at 0.46 s instead of 0.77 s. `Startup.Timings` also logs `hotkeysAt`.
- **Launching the app or opening a capture no longer rewrites the settings file.** Every launch saved the settings
  although nothing had changed: the AV1 encoder check reporting whether GIF recording works counted as a change. Every
  capture saved them too, before its overlay opened. A save now leaves the file alone when it already holds the same
  settings.
- **Smart selection on a large pin no longer holds up the app at every click.** Each click and each undo copied the
  whole pinned image on the UI thread, only for the model to find it unchanged, then coloured in the selection and
  handed it to the screen there too: about 30 ms of every click at 3840×2400, with a 37 MB copy. The image is now copied
  only when it has changed since the model last read it, and that copy and the colouring run in the background. A click
  now holds the UI thread about 2 ms (under 1 ms at 1920×1200, down from 9) and shows the same selection.
- **Recording a single window takes about half the CPU and no longer keeps the garbage collector busy.** Each frame
  Windows delivered was read back into a new bitmap and a new array of the frame's size, 2 MB for a 1000×550 window, and
  the bitmaps' memory accounting set off a full garbage collection about every three frames of a window that kept
  changing. The recorder then copied every frame twice more and converted it again, even when the window had not
  changed. Frames are now read back through one reused GPU texture into reused buffers, and converted only when they are
  new. Recording a window that changed 60 times a second for 4 s allocated 416 MB, with 56 full collections and 1.5–1.9
  s of CPU; now it allocates 0.7 MB, with no collections and 0.5–0.8 s of CPU.
- **Opening a file in the Compress editor no longer decodes a picture nothing shows.** Each time the editor opened on
  another file, the app also opened that file again, brought up a GPU decoder and decoded a 360-pixel frame for a
  preview the Compress tab stopped showing when its Edit tab went away, and applying an edit then rotated that hidden
  picture on the UI thread. That work is gone, so the editor's own first frame no longer competes with it.
- **Seeking, changing speed or muting a playing video pin no longer stalls the app.** Each of these opened the clip's
  audio and the audio device again on the UI thread, and closing the previous device waited there too. That now happens
  in the background. A seek made while the audio is still opening waits its turn, and audio that a newer seek replaced
  is never heard.
- **A playing video pin does less work per frame, and never shows a frame that is half one and half the next.** Each
  frame was copied three times on its way to the screen, one copy up to twice the frame's size, and the screen read it
  while the next frame was being written into it. Each frame is now copied twice, at its own size, and the two copies
  never overlap.
- **Editing a file in the Compress queue no longer writes to disk on the UI thread.** Every change to a queue item
  rewrote the whole saved-state file there, up to seven times in a row when the editor's edits were applied. Changes are
  now saved in the background, a burst of them in one write, and anything still waiting is written when the app exits.
- **The Compress tab's first open no longer waits on the disk.** It read the saved queue, settings and per-file edits,
  listed the working folder and swept old resume files on the UI thread; one log showed the app stop responding for 0.45
  s there. Those reads now run in the background, and the tab appears once the saved queue is in, so it never shows an
  empty queue to click on meanwhile.
- **Adding videos to the Compress queue opens each file once, and no longer all at once.** Reading a file's length and
  size and decoding its thumbnail opened it twice, the second time bringing up a GPU decoder for a 96-pixel picture, and
  every file in a folder started that at the same moment. Each file is now opened once and its first frame decoded in
  software: here about 65 ms a file instead of 180 ms for a 1080p H.264 clip, and 30 ms instead of 150 ms at 720p. Two
  still run at a time, and clearing or switching the queue stops the ones still waiting.
- **Playing a video in the Compress editor or its quality comparison no longer piles up memory.** Every frame shown
  became a new bitmap, about 1.2 MB at the preview's 720-pixel width, and the one it replaced was never released. A 30
  fps clip left roughly 35 MB a second for the garbage collector, twice that in the comparison. Frames are now drawn
  into one bitmap per preview, and when the app falls behind it skips to the newest frame instead of queueing them all.
- **Smart selection reads its result about 45 ms faster a click.** It read the model's three 1024×1024 masks about four
  million times through ONNX Runtime's tensor indexer, which allocated a small array on every read: 45–56 ms and 163 MB
  of memory to clean up after each click. It now reads them in place: about 3 ms, nothing to clean up, and the same
  selection.
- **The Windows download is a folder, so the first launch after an update no longer unpacks the app.** The single
  file unpacked all 478 MB of itself into `%TEMP%\.net\GimmeCapture` the first time each version ran: 2.7 s here
  before anything started, so the first frame came 6.1 s after launch instead of 2.3 s. Every earlier version's
  copy stayed there.
  - The `.zip` and the installer now hold the app's folder. Keep its files together: `GimmeCapture.exe`
    no longer runs on its own. The in-app updater moves an existing install over by itself.
  - The copies earlier versions unpacked are deleted a few seconds after start-up, except one that another running
    copy still uses. The Linux build is still a single file, and it now removes its own earlier copies the same way.
- **The Windows app is 75 files and 49 MB smaller.** Rx, through ReactiveUI, brought along all of WPF, which
  nothing in the app uses: 33 assemblies plus their Japanese and Chinese resources. Rx 7 no longer does. Two unused
  libraries are gone too: CliWrap, and Microsoft.ML.Tokenizers with Google.Protobuf.
- **The app opens faster.** From launch to the main window's first frame took 2.4–2.6 s, 2.1–2.2 s of it with the
  UI thread held; it now takes 1.9–2.0 s, with 1.6 s held (3 launches each in English and Chinese).
  - The window is built in the saved language from the start. It used to be laid out in English and then again,
    CJK font included, once the settings loaded.
  - The Snip, Compress, Hotkeys and History tabs are built the first time they are opened, and the Compress tab's
    queue and settings are restored then too.
  - Start-up no longer searches the whole data folder for a pending update.
  - The 1.7 MB window icon is no longer decoded four times; smaller copies are used.
  - Each launch now logs `Startup.Timings`, so a slow start shows where the time went.
- **On Windows the main window appears 0.85 s sooner.** The app and its libraries are compiled ahead of time
  (ReadyToRun) instead of when first used. From launch to the main window's first frame took 2.1–2.4 s and now
  takes 1.3–1.5 s; the app stops responding for 1.1 s at start-up instead of 1.8 s. The capture overlay loads its
  code about 60 ms faster as well. The download is about 23 MB (11%) larger.
- **The History tab opens without a freeze.** With 78 captures, opening it held the UI thread for 0.8–1.4 s, and
  would have held it about 3 s at the 300 captures kept. Each new capture while it was open froze it again for about
  0.4 s, while every card and thumbnail was rebuilt. Now thumbnails load in the background, a refresh keeps the
  cards it already has, and only the rows on screen are built. The tab opens in 0.16–0.19 s.
- **Copying, saving, rotating and removing the background of a pinned image no longer freeze the app.** On a 4K
  pin, the UI thread was held while each did its pixel work:
  - Copy or Save as PNG: about 2.3 s
  - Rotate or flip: about 0.13 s
  - Background removal: 2.3 s before the AI even started

  That work now runs in the background. The PNG that only travels to the clipboard or into background removal uses
  faster settings: about 0.33 s instead of 2.3 s, and about 18% larger. A copy encodes twice, so a copied 4K pin
  reaches the clipboard seconds sooner. Saved files keep full compression.
- **A playing video pin no longer keeps a CPU core busy.** Its frame pacing busy-waited through the last 20 ms
  before every frame, which at 60 fps was the whole time: about half a core per playing pin. It now sleeps on a
  high-resolution timer, with the same frame timing.
- **Opening a capture no longer waits for a settings write**, memory is only handed back to Windows after five
  minutes of inactivity instead of seconds after every capture (the next capture had to page it all back in),
  and OCR models stay loaded for two minutes between captures instead of five seconds.
- **Slow capture opens are now diagnosable:** each capture logs `SnipOpen.Timings` (time waiting for the UI
  thread, then each stage up to the overlay being shown), and a `UiStall` warning is logged whenever the UI
  thread stops responding for more than 250 ms.
- **Stopping a long recording no longer freezes the app.** Finishing an MP4, MKV or MOV recording rewrote the whole
  file — and copied it, when saving to another drive — on the UI thread, so the app stopped responding until that
  was done. It now happens in the background.
- **A capture no longer freezes while OCR reloads.** Once the OCR model had been idle-unloaded, the overlay's text
  scan rebuilt it on the UI thread — about 0.4 s in which the overlay ignored everything (it showed up in the log as
  a `UiStall`). It now loads in the background.
- **Removing an AI module no longer freezes the app.** Removal waited — up to 30 seconds — for an inference still
  running on that model, then deleted hundreds of MB, all on the UI thread. A removal that fails now says why,
  instead of leaving the module installed without a word.
- **Copying or saving no longer stalls on the history thumbnail.** With history on, every copy and save decoded the
  full-size capture, scaled it and encoded a thumbnail on the UI thread (about 30 ms for a 4K capture); clearing
  the history deleted its files there too.
- **Resizing a pin is smoother.** Every step of a resize or zoom re-read each pixel of the pinned image to work out
  its see-through areas (about 9 ms per step at 4K). The image is now scanned once, and a resize only rescales the
  result.
- **Starting a recording no longer freezes the overlay.** Everything a start does before the recording is running —
  opening the system-audio and microphone endpoints, checking for window-capture support, loading FFmpeg if nothing
  had yet — ran on the UI thread: 0.1–0.2 s on every start, and up to 1.8 s measured. It now runs in the background,
  and so does resuming after a pause.
- **Launching the app no longer holds up its UI while it checks for an AV1 encoder.** Reading the saved recording
  codec built the codec list, and the list loaded FFmpeg and test-opened the hardware AV1 encoders right there on the
  UI thread — 1.75 s on a fresh start, at every launch, tray starts included. The check runs in the background now and
  the list updates when it answers; a saved AV1 choice is kept until then rather than reset. The record hotkey's own
  FFmpeg check, which could load it on the UI thread too, moved off it as well.
- **The first capture after launching opens twice as fast.** The first overlay of a run loaded its own code on the UI
  thread — 0.45–0.65 s before it appeared, against 0.09 s for every later one. The app now builds an overlay window
  and view model once while idle, ten seconds after launch, unseen; the first overlay then opens in about 0.22 s.
- **`UiStall` log lines give a freeze's real length.** The watchdog ticked once a second and logged how late the tick
  was, which understated a freeze by up to a second — the recording-start freeze above was logged as 698 ms — and
  could miss one under 1.25 s entirely. It now ticks every 100 ms.
- **Background removal runs on the GPU.** It asked ONNX Runtime only for CUDA, which the runtime the app ships does not
  have, so it ran on the CPU on every PC. It now uses DirectML, as OCR and smart selection do, and falls back to the
  CPU when the GPU session cannot be built. On a laptop's Radeon graphics the model runs in about 0.09 s instead of
  0.3–0.5 s and leaves the CPU alone, so trying again on the same pin takes 0.38 s instead of 0.82 s at 1920×1200
  (1.0 s instead of 1.45 s at 3840×2400). The first removal after a pause still loads the model, which takes longer
  on the GPU (about 0.8 s against 0.45 s), so it is about as fast as before at 1920×1200 (1.5–1.6 s) and 0.3 s
  faster at 3840×2400. `BackgroundRemoval.SessionCreated` in the log says what each load runs on.
- **Background removal no longer spends most of its time after the model.** Once the model ran on the GPU, the pixel
  work around it was most of a removal: about 0.8 s of 0.95 s at 3840×2400. Resizing the mask to the image and
  applying it asked Skia for the image's size and format again at every pixel, and a selection was put back into
  the picture by drawing it. That work now takes about 20 ms there, spread over the CPU's cores, and gives the same
  bytes as before. Trying again on the same pin takes 0.15 s instead of 0.95 s at 3840×2400 and 0.15 s instead of
  0.35 s at 1920×1200; inside a selection, 0.15 s instead of 0.57 s and 0.10 s instead of 0.25 s. The first removal
  after a pause, which also loads the model, saves the same work. On a pin with a one-colour background, which skips
  the model, the removal itself takes 4 ms instead of 50 ms at 3840×2400.
- **The first background removal after a pause starts about 0.2 s sooner.** The model is unloaded 10 s after a
  removal, and with it went the last thing keeping the graphics device open, so each removal after a pause opened the
  device again. A pin now keeps it open until five minutes after its last removal, or until the pin is closed, which
  holds about 45 MB. Loading the model after a pause takes about 0.5 s instead of 0.7 s on a laptop's Radeon graphics.
- **Smart selection keeps up on a large pin.** Its pixel work asked Skia for the image's size at every pixel too. On
  a 3840×2400 pin, each click spent about 0.18 s preparing the mask and then 0.2 s colouring in the selection, which
  held the UI thread; confirming spent 0.36 s applying it. Each of these now takes 5–9 ms and gives the same pixels.
  A click shows its selection in about 0.3 s instead of 0.7 s (0.14 s instead of 0.22 s at 1920×1200); most of what
  remains is resizing the mask to the pin. OCR's passes over each line of text did the same; fixing them saves about
  40 ms on a page of text, a small part of reading it.

### 💾 Storage

- **The data folder no longer only grows.** Every folder GimmeCapture runs from keeps its own settings and AI modules
  under `%LOCALAPPDATA%\GimmeCapture\instances`, and nothing was ever removed from there: 6.5 GB on one machine.
  A few seconds after start-up the app now deletes:
  - instance folders with nothing in them. Reading the update state created one for every folder the app or its
    tests ever ran from (203 on that machine). It no longer does, and test runs keep their data in a temporary folder.
  - what AI features that are gone left behind: the downloaded ONNX Runtime (671 MB per AI folder), the MarianMT
    translator, the Vosk speech models and MobileSAM, plus downloads a closed app left half done.
  - the second copy of every model in the instance folder, when your settings use another AI folder. Each start
    used to copy the app folder's `AI` folder there, although nothing read the copy: 3.4 GB on that machine. When
    the instance folder is the one in use, the app folder's `AI` folder is moved into it once, not copied.
- **Recordings you never saved no longer pile up.** A pinned recording waits, unsaved, in the app's `Temp` folder, and
  closing the pin left it there for good, as did saving from it, which makes a copy: about 100 MB on one machine.
  Every update also left the previous version's whole `Temp` folder behind. The pin now deletes its recording once
  it is closed and any save or copy it started is done. A recording copied to the clipboard as a file stays, and one
  saved from its pin keeps its History entry, which now points at the saved file; one closed unsaved leaves History
  too. A few seconds after start-up the app also removes what earlier runs left in this install's `Temp` folders,
  with their History entries: recordings a day old or more, which includes every recording pinned and never saved
  before this version, and the raw segments a failed save keeps for recovery once they are a week old. In a temp
  folder you picked, it touches only its own recordings (`GimmeCapture_<id>.mp4` and the like). A recording still
  going when the capture overlay is closed goes to your video folder, as one stopped by the length limit does, and a
  recording that fails to start no longer leaves an empty folder behind.
- **AI Core is a 176 MB download instead of 514 MB.** It also fetched a separate ONNX Runtime with CUDA and
  TensorRT providers, which needed CUDA 12 and cuDNN 9 to use the GPU at all, and which replaced the app's own
  runtime for the rest of the session once background removal or smart selection had loaded it. The app ships its
  own ONNX Runtime, so AI Core is now the U2Net model alone, as on Linux. Smart selection no longer asks for AI Core
  first.
- **The Modules tab shows what other copies of GimmeCapture keep here.** A copy that was moved, reinstalled
  elsewhere or deleted leaves its settings and AI modules behind. The tab lists each other copy: where it runs (or
  that it is no longer there), its size and when it was last used. It removes one you pick after asking, but not a
  copy that is running.

### 🖼️ Interface

- **A pinned image's questions and messages use the app's own dialog.** Asking whether to download AI modules
  borrowed the update dialog, so it was titled "GIMME UPDATE!!" with an "update now" button; background removal
  also asked interactive selection's question (about 100 MB) instead of its own (about 300 MB). Messages such as
  "AI is disabled" showed Yes and No buttons whose answer nothing read; they now have one OK button. Recording and
  Quick OCR progress use the same winged bar as downloads.
- **Exporting from a pinned video shows the same winged progress bar as downloads.** Copying, saving or cropping
  an edited clip used to dim the whole video behind a plain bar that never moved off 0.0%. The winged bar now
  appears in its own small window over the video, so a small pin can't clip it, and it doesn't take focus. An
  export that finishes within 0.3 s doesn't show it at all. It also stays up for the whole export: a GIF or WebM
  export with a cut used to lose it halfway, as soon as the trim before the transcode was done.
- **What smart selection removes from a pinned image is transparent.** The removed part showed as a white wash over
  the pin's transparency checkerboard instead of the checkerboard: its pixels kept their colour at full
  transparency, and drawing the pin added that colour to what lay behind instead of hiding it. Smart selection's
  preview also drew the red marks of points that exclude an area in blue, and its tint brighter than intended.
- **Removing the background again no longer darkens a pin's soft edges.** A second removal on the same pin, inside a
  selection say, made the soft edges the first one left about a fifth darker, and darker again with each removal
  after that.

### ♿ Accessibility

- **Screen readers can tell the buttons apart.** An icon-only button — most of every toolbar — was announced by its
  icon's type name ("Avalonia.Controls.Shapes.Path, button"). Every button is now named after its localized
  tooltip, and the icon buttons that had none (window controls, play/pause, colour swatches, bold/italic, mosaic
  sizes) have one or an explicit name.
- **Keyboard focus is visible.** The app's own button styles replaced the only focus indicator the UI framework
  draws, so Tab moved through them without showing where it was. They now show a focus ring — for the keyboard
  only, never after a click.

### 🌐 Localization

- **A pinned video's failed save is reported in the app's language.** "Save Failed:" before the reason was English in
  every language.
- **Chinese labels on the small gothic buttons use the Chinese font.** Their font chain put the Japanese font ahead of
  the Chinese one for every language, so Chinese text took Japanese glyph forms, and a Chinese UI loaded both CJK
  fonts (12.9 MB and 16.8 MB). The chain now follows the app language.
- **No more Chinese-only or English-only text.** The folder picker (titled "select the recording folder" in Chinese
  for all four folder settings), the hotkey-registration failure and the Enter-hotkey warning were Chinese in every
  language; the pin dialogs' YES/NO, the pinned video's seek tooltips, the update-failure message, the blur
  strengths, the translation tab's Refresh button and several window titles were English in every language. All
  now follow the app language, and a test fails the build when UI text is hardcoded again.

### 📝 Docs

- The README, in all three languages, caught up with v0.60–v0.68: capturing menus, freeze-frame, Quick OCR, AV1,
  auto-stop, GIF/WebM output and history — and no longer advertises the removed mask-opacity setting.

### 🔒 Security & supply chain

- **"Copy recording" starts PowerShell by its full Windows path.** It used to be found through `PATH`, whose first
  entry is a folder any program running as you can write to (the AI runtime folder), so a `powershell.exe` planted
  there would have run instead. File names containing `[` or `]` are copied too now; they were read as wildcards
  and silently skipped.
- **That folder is no longer on `PATH` at all.** It was put there for the ONNX Runtime AI Core downloaded, which the
  app no longer downloads or loads.
- **Every build bundles exactly the same FFmpeg.** Builds used to download whatever BtbN's rolling "latest" FFmpeg
  was that day, unchecked. They now fetch one exact build — FFmpeg n8.1.3 (2026-09-22), mirrored in this
  repository's releases — and refuse anything whose SHA-256 differs. THIRD-PARTY-NOTICES.md names that exact
  version, so the GPL corresponding-source notice points at the exact source.

### 🧹 Maintenance

- **A window recording's log no longer gets a line every 300 frames.** After the first three, the "frame arrived" line
  is written only with `GIMMECAPTURE_DEBUG_LOG=1`. The line a window recording ends with also says how many frames were
  read back and how many were converted.
- **A pinned recording without sound no longer logs a warning at every play and seek.** Starting its preview audio
  failed in Media Foundation, which was logged as a warning, and so went into a bug report's error summary. It then
  failed again in FFmpeg; one user's log had 16 of these in a day. The first failure now checks whether the clip has an
  audio stream at all. A clip without one plays silent from then on and is noted only with `GIMMECAPTURE_DEBUG_LOG=1`. A
  clip with sound plays it as before, and a real failure is still logged.
- **A playing pin no longer adds log lines at every seek and loop.** Which decoder the player used, and a file that
  opens slowly, were logged each time the clip was opened, and a pin does that at every seek, segment and loop pass.
  Each is now logged once, and the decoder again when it changes. Repeats appear only with `GIMMECAPTURE_DEBUG_LOG=1`.
- **The log no longer gains two lines for every translation.** Each uncached translation, OCR run and SAM2 click wrote
  two `MemoryTrim.Activity` lines. A line is now written only when that activity calls off a waiting memory trim; every
  one is written with `GIMMECAPTURE_DEBUG_LOG=1`.
- Avalonia 12.1.4, up from 12.0.4, brings its fixes for the tray icon, focus left on removed controls, popup
  placement and font fallback. Serilog moves to 4.4 and ZLinq to 1.5.6, and the test tooling to the xunit runner
  4.0, test SDK 18.11, coverlet 10.1 and Moq 4.21. ReactiveUI.Avalonia stays at 12.0.3, since every newer release
  also moves ReactiveUI to a new major. FFmpeg.AutoGen 9, SkiaSharp 4, NAudio 3, LLamaSharp 0.30 and ONNX Runtime
  wait for upgrades of their own.
- The AAC, MP3 and Opus encoders share one encode routine instead of two copies, and every file they write is
  what it was before, byte for byte (Ogg's random stream serial aside). A test now also pins Opus's own bitrates
  for High and Low.
- The three recorders (a screen region, a window, several windows) share one encoder-and-file writer instead of a
  copy each, and tests now record with every one of them: the region ones in every test run, the window ones on
  request (`RECORDING_PROBE=1`, since they put windows on screen).
- They also share their start, stop and startup check. A recorder that fails as it starts now always reports a
  failed start; it used to throw its error instead about half the time, which the app caught and treated the same.
- The region recorder's two loops, the usual one and *Pipelined encoding*, share how a captured frame is converted
  for the encoder (overlays drawn on the way) and how it is timestamped, instead of a copy each. A test now records
  with *Pipelined encoding* as the app runs it: wall-clock timestamps, overlays on.
- Each Windows function and struct that more than one part of the app calls is declared once (`POINT` had six
  definitions, `RECT` four), and the capture overlay and pinned images share one click-through hook, now tested on a
  real window. The three X11 calls the Linux capture, hotkey and window-shape code share are declared once too.
- The capture overlay no longer depends on the main window's view model at all. It reports statuses, toasts and
  confirmations through a small interface, is handed the services it and the images it pins use (hotkeys, capture
  history, AI resources and the rest), and reads its settings through an interface of its own, its hotkeys straight
  from the settings they are saved in. Its tests can stand in for each of these instead of building a whole main
  window, and a test keeps the main view model from coming back.
- View models read their UI strings through the localization they are given (the app's own unless a test hands them
  a stand-in) instead of the static instance, and the localization check counts those lookups too.
- The two Linux workflows are one, in one job: it compiles both heads, then runs the Linux test suite. It runs on
  `main` too, so a `main` that no longer builds on Linux shows up at once. Its NuGet cache lives on `main` and
  branches reuse it; before, each branch saved its own 1.7 GB copy, twice. A `claude/**` branch with an open PR
  no longer runs it twice.
- The app's 144 diagnostic traces (hotkey registration, translation, recording and capture steps) used
  `Debug.WriteLine`, which a release build leaves out. They go through `AppLog.Debug` now, so a release build
  writes them when `GIMMECAPTURE_DEBUG_LOG=1` is set; otherwise they are not even formatted.
- A release builds Linux and Windows side by side instead of one after the other. Its Windows verify skips the
  publish smokes the build then repeats, and Inno Setup is pinned (6.7.1) instead of whatever is newest that day.
- The repository drops 135 MB of static font files the build never used (only the variable fonts ship) and an
  outdated sample `config.json`. `REFACTOR_PLAN.md` records this pass and what is next.
- `release.ps1` refuses to run unless the local `main` is exactly `origin/main`. The Windows test run stops a test
  stuck for 10 minutes and names the test a crashed test host was running. Three benchmarks of code that is
  gone are removed.
- Dead code is gone: the unused download-progress window and AI settings tab, the throwing URL-download
  overloads, `RemoveResources`, the unimplemented annotation `Feather` setting, and two helpers only their own
  tests still called (`TranslationExecutionHelper`, `VideoAnnotationFilterBuilder`).
- The three locale files drop 64 keys nothing used any more, many of them left by removed features such as Ollama,
  MarianMT, Vosk and the AI scan. The localization check now fails on an unused key instead of only warning.
- Every publish uses one of two profiles, `Properties/PublishProfiles/win-x64.pubxml` and `linux-x64.pubxml`,
  instead of five copies of the same arguments. `scripts/build-installer.ps1` works again: it never named a target
  framework, which the project has needed since it gained the Linux head. A publish for one platform no longer picks
  up the other platform's FFmpeg libraries, and a Linux publish on Linux fails if they are missing, like the
  Windows one.
- CI uploads the coverage report again. Since `verify.ps1` began deleting a passing run's artifacts, it deleted the
  report before the upload step ran, so green runs uploaded nothing.
- Tests that need a local video clip or a render-probe switch are reported as **skipped** while those are not set,
  instead of counting as passes for work they never did, and `scripts/test-compress.ps1` now also runs the
  crop+rotate export checks it said it ran. The empty placeholder test is gone.
- Coverage no longer counts the code the XAML compiler generates from `.axaml` files, which no unit test reaches,
  and its gate rises from 25% to 30%. The six audio-transcoder tests run on every CI job: they never needed a
  video clip, only a folder to write to. The Linux key map, the X11 capture's clamp to the screen and the autostart
  command line are tested. Coverage reads 32%, up from 28%.
- Workflows cancel superseded PR runs, time out instead of hanging for six hours, and ask only for the token
  permissions they use. The release job's one third-party action is pinned to a commit SHA, Dependabot keeps the
  workflow actions current, and CodeQL scans the C# code and the workflows.

---

## v0.68.1 - 2026-09-16

### ⚙️ General

- **Run on startup actually launches the app at sign-in — and the app no longer asks for administrator rights.**
  Every released build since v0.42.0 asked Windows to run it as administrator, and Windows **silently skips
  elevated programs listed under `HKCU\...\Run`** at sign-in — no app, no error, nothing in the event log, and
  nothing in our own log because the app is never started. The registry entry looked perfect the whole time, so
  every check reported "registered" while auto-start had in fact never run. Released builds now start as a normal
  program, the way development builds always have: the startup entry works again, and launching GimmeCapture no
  longer shows a UAC prompt.
- **Administrator rights are requested only when something actually needs them.** Hotkeys keep working while an
  administrator window (Task Manager, an elevated terminal) is in front. The exception is a hotkey another
  program already owns — Print Screen claimed by the Snipping Tool, say — which GimmeCapture can only catch with
  a keyboard hook, and Windows blocks that hook over administrator windows. Only then does it offer to restart as
  administrator, naming the hotkey in question, instead of interrupting at every administrator window the way it
  used to. That restart also works again: it could previously shut down without coming back.
- **Cancel, Copy / Save and the recording keys keep working over administrator windows** while the capture
  overlay deliberately leaves focus where it is ("Don't steal focus when capturing").
- **Updating an install under `Program Files` asks for elevation instead of quietly changing nothing.** A normal
  program cannot replace files there, so the update rolled itself back and relaunched the old version with no
  explanation. It now prompts once and applies. Per-user installs — the installer's default — are unchanged.
- **Auto-start no longer pops the main window open at sign-in** when more than one startup registration exists
  (a Run entry plus a Startup-folder shortcut, for instance); the duplicate launch just exits.

---

## v0.68.0 - 2026-09-02

> Consolidates everything since v0.66.0, including the work tagged as **v0.67.0 – v0.67.2**. Those three tags
> were published without a version bump or a release-log entry — their builds report themselves as `0.66.0` —
> so their changes are recorded here rather than left undocumented.

### ✂️ Snip & capture

- **Menus and dropdowns can be captured now.** The overlay activated itself on open, which stole foreground
  focus and closed exactly the focus-sensitive UI you were trying to capture — dropdown lists, right-click
  context menus. It now opens without taking focus (**Capture without stealing focus**, on by default), so the
  menu stays open until you commit the selection.
- **Freeze-frame screenshots** (**Freeze screen on screenshot**, on by default) snapshot the desktop *before*
  the overlay appears and let you select on that still. This is the only reliable way to capture Windows shell
  "light dismiss" popups — the tray flyout, the Start menu, most left-click dropdowns — which close the instant
  any full-screen overlay covers them, focus or no focus. Trade-off: the overlay shows a still instead of the
  live see-through, and clicking the live app inside the selection is disabled; toggle it off to restore the
  previous behaviour. Screenshot captures only — recording, translation and scrolling stay live.
- **The toolbar freeze button acts on the capture you are looking at.** It used to flip the setting for the
  *next* capture, so pressing it appeared to do nothing. The setting keeps its own job: it decides the initial
  state, and it stays the only way to catch shell popups, which need the still grabbed before the overlay opens.

### 📜 Scrolling capture

- **Live preview of the strip as it grows.** A small window docked beside the captured region shows the stitched
  result in real time, so a misplacement or a stall is visible while you can still do something about it,
  instead of only in the finished pin.
- **A live warning when stitching loses track.** The aligner can only extend the strip while the view still
  overlaps it; scroll too fast past its edge and every later frame fails to align, the capture silently stops
  growing, and you discover the stub image only at the end. The hint window now says so after about 1.2 s of
  misses — scrolling back a little re-overlaps the strip and stitching resumes on its own.
- **Fixed long screenshots coming out short.** The offset search only scored rows near each edge of the strip.
  Once the strip grew past roughly twice the frame height those two windows separated, every offset between
  them went unscored, and stitching stopped finding matches.
- **Fixed long screenshots displaying sideways.** The scrolling pin chose its reading axis from image aspect, so
  a vertical capture of a wide region was displayed as horizontal content until it grew taller than it was
  wide. The axis now follows the capture instead. Repeated page content could also produce a confident but
  backwards match; those are now vetoed against the scroll direction.

### 🔤 OCR

- **`Auto` language detection actually detects.** It never did — the path had no `Auto` arm and fell through to
  the simplified-Chinese recogniser, so Japanese came back as Chinese or as nothing. Detection now recognises a
  sample with each installed recogniser and compares: kana and hangul are emitted by exactly one model each, so
  they decide outright. Quick OCR reports which language it picked. The Modules button installs all five
  languages (~68 MB), because a tick earned by having only Chinese installed was a lie about what `Auto` could
  read.
- **Fixed crashes during AI scan and back-to-back captures.** Three separate causes: ONNX sessions disposed out
  from under a running inference, sessions torn down between consecutive captures, and concurrent `Run` calls on
  a single session — inference is now serialised. The AI scan also gets its own copy of the frozen frame.
- Quick OCR shows **one** spinner instead of two.

### 🎬 Recording

- **AV1 recording, where the hardware can genuinely do it.** The option appears only after test-opening an
  encoder, because every vendor ships an AV1 encoder that exists but fails to open on hardware that cannot
  encode it (NVIDIA before Ada, AMD before RDNA 3, Intel before Arc) — gating on the name alone would have put
  an option in the UI that silently degrades to H.265 on most machines. Software AV1 is deliberately not a
  fallback: it cannot keep up with a live capture, and trading dropped frames for smaller files is the wrong
  failure. This also fixes a bogus warning that reported a fallback for every hardware H.265 recording.
- **Auto-stop at a configured length**, then pin the clip or save it to the video folder. New Record-tab
  settings beside the size limit; the duration counts recorded time only, matching the on-screen timer. An
  unattended stop never pops the save dialog — the clip is filed straight away.

### 📋 Clipboard

- **"Copied" no longer lies.** Pasting sometimes produced the *previous* screenshot. Three stacked causes, all
  silent: the Windows copy had no retry, so another process briefly holding the clipboard open (clipboard
  history, Office, RDP tools) made it fail; the fallback did nothing when it could not resolve a window; and the
  caller reported success either way. The copy now reports success end to end, retries the write, and shows an
  error toast instead of a false "Copied", so you re-copy rather than pasting something stale.
- **The copy confirmation shows a thumbnail of what actually landed on the clipboard.** "Copied to clipboard!"
  never said *which* image, so the only way to find out was to paste — the exact uncertainty the fixes above
  were meant to remove. Only a successful copy gets a thumbnail.
- A standalone **spinner during slow clipboard writes**, so a large image no longer looks like a hang.

### ⚙️ Startup & running instances

- **Only one copy of the app runs at a time.** Double-clicking the app, or autostart racing a manual launch,
  opened a second full instance. A duplicate launch now brings the running app's window forward instead. The
  guard is scoped to the install directory, so two different installs still run side by side, and any failure
  fails open so it can never block startup.
- **A startup entry deleted from outside is noticed and repaired.** Security tooling and "startup cleaner"
  utilities remove it, and the app only re-asserted the entry while launching — which is precisely what a
  missing entry prevents. So the switch read "on", nothing was logged, and the only symptom was the app not
  being there after several reboots. Opening the main window now re-checks and restores it, logs each
  disappearance, and warns in Settings → General if the repair does not stick.
- **A development build no longer steals the startup registration.** The re-assert on every launch handed the
  entry to whichever copy ran last, so a `bin\Debug` build could quietly become the thing Windows launches at
  login — until the next clean build deleted that path and auto-start broke with nothing to show for it. A
  valid entry pointing at another install that still exists is now left alone; a stale one, the reinstall case
  the re-assert exists for, is still taken over.

### 🖼️ Interface

- **The main window has an outline.** It is borderless with a near-black background, so against a dark desktop
  or another dark window its edges simply vanished. The outline follows the theme accent rather than adding a
  fourth hard-coded colour.

### 🗑️ Removed

- **Imgur upload is gone.** Imgur is blocked in Taiwan, where this app is used, and the feature saw little use
  even where it worked. It was the only upload target, so the upload capability is removed entirely rather than
  leaving an empty abstraction behind for a second provider that does not exist. Existing configs need no
  migration — the stored client ID is simply dropped on the next save.

### 🧹 Maintenance

- `verify.ps1` deletes a passing run's artifacts instead of hoarding them, and now smoke-tests the Linux release
  publish locally, so a release cannot fail after the tag has been pushed.
- Added a native-crash diagnosis skill for the assistant workflow.

## v0.66.0 - 2026-07-22

### 🎬 Recording

- **Fixed audio/video drifting out of sync.** Video frames were timestamped from a frame counter, which assumes
  every frame lands exactly 1/fps apart, while the recorded audio is always real time. Whenever capture fell
  behind the target fps — large region, busy scene, loaded CPU — the video timeline came out shorter than
  reality and the gap widened as the recording ran (which is why it only happened *sometimes*). Video is now
  paced by wall clock, so the output duration matches real time and the two tracks stay aligned. This also
  works together with the experimental pipelined-encoding option, which previously excluded it.

### 📤 Sharing

- **One-click Imgur upload** for screenshots — share a capture and get a link without leaving the app.

### 🏷️ File naming

- **File-name templates.** Define how captures are named (date/time and other tokens) from the Snip settings
  tab; the template drives all six places the app names a file, so saved screenshots, recordings and exports
  all follow it consistently.

### 🎧 Audio & compression

- **Audio-only extract.** Pull the audio out of any video to **WAV / MP3 / M4A / OGG-Opus**, and those formats
  are now selectable in the compress output-format picker.

### 🔤 OCR

- **Optionally save quick-OCR text to a `.txt` file** alongside the capture, instead of only copying it.

### 🖼️ History

- **Tabs are grouped by capture kind — 截圖 (screenshots) / 錄影 (recordings)** — instead of by how the entry was
  produced, so a copied screenshot no longer hides in a separate tab from a saved one.
- **New sort selector**: date (newest/oldest) and name (A→Z / Z→A), composing with the search box and the tabs.

### ⚙️ Startup

- **Run-on-startup now tells you when Windows has disabled it.** Windows records a Task Manager → "Startup apps"
  disable separately and then ignores the registered entry, so the app could keep re-registering a valid entry,
  report success, and still never launch at login. That state is now detected and surfaced in Settings → General
  with instructions, instead of showing a switch the OS silently overrides. (It is only reported, never flipped
  back — that stays your choice.)

### 🧹 Maintenance

- **Memory**: freed memory is now returned to the OS on Linux, and the annotation undo history is capped so long
  editing sessions can't grow unbounded.
- **Linux**: the test suite is multi-targeted with a Linux CI job, so regressions on the Linux head get caught.
- Build/CI: FFmpeg + NuGet caching, correct `linux-x64` RID scoping, and a compiler-warning cleanup.

## v0.65.0 - 2026-07-17

### 🌐 Translation overlay

- **The browser no longer greys out when the toolbar is hidden.** Hiding the translation toolbar over a
  Chromium-based browser (Chrome/Edge) used to make the page repaint solid grey when switching tabs/pages; the
  overlay is now marked non-occluding so the browser keeps compositing normally.
- **Ctrl-drag to select while the toolbar is hidden.** Hold the selection modifier (default Ctrl) and drag to
  translate a new region even with the toolbar hidden — the screen dims like a normal selection and returns to
  click-through on release.
- **Esc closes the overlay from anywhere.** Esc now dismisses/closes the translation overlay even when another
  app (browser/editor) holds focus, not only when the capture app is focused.
- **The show/hide-toolbar hotkey (F4) works globally** during translation, including while the target app is
  focused — so you can always bring the toolbar back.
- **A hidden toolbar no longer blocks clicks.** The spot where the toolbar used to sit — and the desktop under a
  hidden toolbar — is fully click-through again.
- **Fixed a crash** when selecting/copying text in a pinned translation (or pinned image caption) that contained
  a blank line.

### 🔤 OCR

- **Windows 10 compatibility.** When the DirectML OCR session can't initialize (seen on some Windows 10 /
  GPU-driver setups), OCR now falls back to CPU instead of failing, and logs the outcome. Inconsistent saved AI
  settings (e.g. OCR detection on while the master AI switch is off) are also repaired on load.

### 🖼️ Long screenshot

- **Scrollable long-screenshot pin.** A long scrolling capture now pins at the size of your original selection
  and scrolls the tall image inside it (fit-to-width), and the pin can be resized freely like any image pin.

### ⚡ Performance

- Faster scrolling-capture stitching, OCR tensor fills / CTC decoding, and auto-translate — the language-detection
  probe's result is reused instead of running detection twice.

## v0.64.0 - 2026-07-15

### 🧹 Maintenance / reliability

- **Temp-file cleanup.** GIF-recording audio sidecars and cropped-video-pin copies (under
  `%TEMP%/GimmeCapture_Pins`) are now deleted when their pin closes, and a best-effort startup sweep removes any
  `GimmeCapture_*` temp workspaces a previous crash left behind (age-gated so a running second instance is never
  touched) — no more unbounded `%TEMP%` growth.
- **Better diagnostics.** ~37 error sites that were logged only to `Debug.WriteLine` (invisible in Release builds)
  now go to the `AppLog` file sink, so OCR / translation / recording / pin failures are diagnosable from the log.
- Fixed a small `CancellationTokenSource` leak on rapid re-scans, and added unit tests for the OCR text formatter
  and hotkey mapping.

### ⚡ Performance

- **Faster repeated background removal.** A pinned image now reuses one background-removal engine instead of
  reloading the (hundreds-of-MB) U2Net model from disk on every removal — the model loads once and stays warm
  between removals, so refining a cut-out is much snappier.
- **Smoother mosaic/blur drag.** The live redaction preview no longer re-converts the whole source image on every
  drag frame (a ~8&#160;MB copy up to 60×/s on the UI thread); the conversion is cached for the drag, cutting jank
  on large monitors.

## v0.63.0 - 2026-07-09

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
- **Quick-OCR (Shift+F4) no longer looks frozen while recognizing** — the snip overlay is hidden to grab a clean
  capture, which also hid the in-overlay "recognizing…" indicator, so multi-second recognition looked like the app
  had hung. A standalone always-on-top spinner now shows during recognition (recognition already runs off the UI
  thread), so there is clear feedback until the text lands on the clipboard.
- **Copying a large image or file no longer freezes the app** — image copies (Snip "copy", pin image copy) and
  file copies (pin/video-clip copy) now run the synchronous Windows clipboard write on a dedicated background STA
  thread bounded by a timeout, matching the earlier OCR-text-copy fix. A large payload racing the Windows
  clipboard-history listeners can no longer wedge the UI thread; a write that overruns the timeout is abandoned
  harmlessly instead.

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
