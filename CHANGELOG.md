# Release Log / 更新日誌 / リリースノート

## Licensing — 2026-07-03

- **Relicensed from MIT to GPLv3.** GimmeCapture bundles and links against FFmpeg built with
  `--enable-gpl` — including the GPL-licensed **x264**/**x265** encoders used for H.264/H.265 —
  so the application as distributed is a combined work that must be under the GPL. See
  [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

---

## Unreleased

### ⚡ Performance

- **Starting with Windows from a hard disk is quick again, and quicker than before.** v0.74.0 read only the parts of
  the app's files its last start had used, which halved what it read but made it slower: at sign-in other programs
  read the same disk, each read waits behind theirs however small it is, and many small reads waited far longer than a
  few whole files. At the first sign-in with it the launcher took 42.6 s for 87 MB, where whole files had taken 22.6 s
  for 141 MB the day before. The launcher, like the app's own read-ahead, reads whole files again, now four at a time,
  so the disk always has some of the app's reads to choose from. In tests on that disk with other reads going on, the
  app's 133 MB took 4.1-4.8 s instead of 10.2-12.0 one file at a time, and 2 s instead of 3.5-4.0 with the disk to
  itself. At the first sign-in with it the launcher read the 136 MB in 4.0 s, and the hotkeys worked about 8.4 s after
  it started, against 25.9 s the day before.
- **Signing in also reads ahead what the app loads in its first idle seconds.** Ten seconds after it starts, the app
  builds a capture overlay and a pin out of sight, so that the first real ones open sooner. At that sign-in this held
  the interface up for 0.64 s while it read files from the hard disk that nothing had read ahead, and a capture hotkey
  pressed then would have waited. A start to the tray now adds the files that warm-up loads (about 2.4 MB) to its
  start-up list, which the next sign-in reads with the rest. Whatever loaded more meanwhile, such as a capture's text
  recognition or a video's decoder, is left out, so as not to slow every later sign-in.
- **At sign-in the app no longer waits its turn for a processor.** While Windows signs in, its other programs keep the
  processors busy, and the app's start, which needs well under a second of processor time, waited for one: the .NET
  runtime's own start took 1.4 to 3.2 s at the last four sign-ins, the longer the busier the machine, against 0.05 to
  0.2 s at other times. The launcher now starts the app above normal priority, and the app goes back to normal as soon
  as its hotkeys work. In a test with every processor kept busy, a .NET program reached its code in 0.38 s instead of
  0.82, and did its work at almost the speed of an idle machine.
- **Saving a capture closes the overlay at once.** A frozen capture's overlay stayed on screen while the picture was
  encoded, a PNG at full compression that takes about 0.35 s for a 1920x1080 screen. It now closes as soon as the file
  has its name, and the file is written after it, still at full compression.
- **Starting with Windows reads 14 MB less of the app.** Every start loaded WinForms' 13 MB library, which only an error
  message no start shows uses (the message had been compiled into the start-up code), and FFmpeg's bindings (1.1 MB)
  as the video-decoding setting was applied. The start-up files sign-in reads ahead from the disk come to about 121 MB
  instead of 136.
- **Signing in reads the fonts once the hotkeys work.** The Chinese and Japanese fonts the app draws with, 28 MB, were
  part of its main file, which the sign-in read whole before starting the app, though no start draws a character before
  its hotkeys work. They are a file of their own now, which the sign-in reads in the background once the hotkeys work,
  well before the first window or capture needs it: what it reads first comes to about 93 MB instead of 121. Each font
  also loads only when something first draws with it, which takes 23-34 ms off a start to the tray, as at sign-in.
- **A start sets up only what it uses.** The log was set up first thing, before anything else could start: 61-96 ms
  of each recent start, most of it Serilog compiling the pattern of its daily file names. It is now set up alongside
  the rest of the start, and what is logged meanwhile is written once it is ready, in order and with the time it was
  logged at. Avalonia's own font, Inter, read in all six weights at every start (13 ms and 3 MB), now loads only if
  something draws with it, which only a few small windows do. The interface text is read for the language in use
  only, without the general JSON reader's set-up: 1.2 ms instead of about 7.
- **Signing in reads about 7 MB less of the app.** The update check ran within a second of every start, and with it
  came the web, TLS and encryption libraries, which the start-up list then had the sign-in read ahead from the disk.
  It now runs 30 seconds after the start, the update service is built only when something uses it, and the install's
  own key, worked out at every start, no longer needs the encryption library. An update found at sign-in is announced
  half a minute later than before.

### 🔤 OCR

- **With two screens, the capture overlay finds the text on each of them.** The overlay's scan read both screens as
  one picture, which text detection shrinks to 1280 pixels across, so each screen was read at a third of its pixels:
  on two screens of window text at 11 to 24 pixels, 4% of the lines got a box of their own and 86% were covered by one.
  Each screen is now read on its own: 62% and 100%. The boxes take a little longer to appear, detection taking about
  0.3 s instead of 0.07 in tests. With one screen nothing changes.
- **The Modules tab says how many of OCR's languages are installed.** The OCR module installs five languages
  (Traditional and Simplified Chinese, Japanese, Korean and English), and counts as installed only with all of them,
  since detecting the language can only tell apart the ones it has. With some of them its card said "Not Installed",
  which read as broken while OCR was reading those: it now says "Installed 2/5", and Install fetches only the rest.

### 🛡️ Reliability

- **A capture saved over a larger file no longer keeps that file's end.** Saving onto an existing file wrote the new
  PNG over its start and left the rest in place, so the file kept its old size and the old data after the picture.

### 💾 Storage

- **Recordings that early versions left behind are cleared away.** Versions from before the per-install folders
  recorded into `%LOCALAPPDATA%\GimmeCapture\Temp`, and the sweep at start-up that removes leftover recordings
  only looked in the folders recordings go to now: on one machine 628 MB stayed there, the raw pieces of 32
  recordings and 3 recordings, from February to May. That folder is now swept by the same rules as the others: a
  recording goes once it is a day old, the pieces of one that failed to save a week after their last write, and
  nothing else in it is touched.

## v0.74.0 - 2026-11-06

### ⚡ Performance

- **Starting with Windows reads less of the app from a hard disk.** At the last sign-in the launcher read the app's
  start-up files whole before starting it, 141 MB in 22.6 s from a disk the rest of the sign-in was reading too, and
  the app then started in 3.3 s. A start uses much less of those files than they hold: the app's own file is 40 MB, 30
  of them fonts a start to the tray never draws. Each start now writes down which parts of each file it used, and the
  launcher, like the app's own read-ahead, reads only those.
- **The first pin after the app starts opens sooner.** A frozen capture's overlay stays up until the pin it opens has
  drawn, and the first pin of a run, which loads a pin's code and draws one for the first time, kept it up close to a
  second longer than later pins in tests. Ten seconds after it starts, the app now builds a pin out of sight and
  throws it away, and when it draws on the CPU ("Use less memory") draws it too: the first pin then took 0.13 s instead
  of 0.9. Drawing on the GPU it only builds it, which saves about half; drawing it there too would keep 27 MB more for
  good.
- **Record mode's audio meter no longer keeps the interface busy.** The meter that shows the recording's sound levels
  read them on the interface's own thread every 120 ms, all through a recording, and redrew them whether they had
  changed or not: in silence that thread woke 63 times a second, and as Record mode opened it set up the audio devices
  there, for up to 58 ms. The meter now reads on a thread of its own, tells the interface only when a level changes,
  and leaves the microphone alone when the recording does not take it.
- **Recording a region takes 60-65% less processor.** Every frame was grabbed through GDI, which has Windows read the
  screen back for it: recording 1450x900 at 30 fps on NVENC took 640-650 M cycles a second, and 1920x1080 took 890. A
  region on one display is now grabbed through Desktop Duplication, which copies it out of the screen on the graphics
  card and hands over only that: 245-265 and 315 M cycles a second, for the same frames, the same pixels and the same
  colours, with the capture overlay left out as before. A region across two displays, on a turned display, or where
  Desktop Duplication does not open is grabbed as before, and when Windows takes a duplication away mid-recording (a
  display change, a UAC prompt) the recording carries on through a fresh one, or as before. On a laptop with an NVIDIA
  card beside the chip the display hangs off, the app now asks Windows about its displays before it starts the NVIDIA
  driver: started first, the driver had Windows put the whole app on the NVIDIA card, from which Desktop Duplication
  cannot reach the display.
- **A recording then takes about a fifth less processor again.** Desktop Duplication and GDI both hand over each frame
  in the colour format NVENC and AMD's encoder take, yet every frame was still copied into one of the encoder's own
  before it was encoded. A frame already in the encoder's format and size, with nothing drawn on it (a cursor highlight,
  clicks, keystrokes or the webcam draw on it), now goes to the encoder as it is: recording 1450x900 at 30 fps on NVENC
  took 210-214 M cycles a second instead of 263-265, and 601-604 instead of 621-632 where GDI grabs it. The file is the
  same: a keyframe every two seconds, the same colours and the same length.
- **A pinned recording takes less processor while it plays.** A video decoded on the graphics card comes back in a
  colour format (NV12) that the pin converted to its own by a slow general route: on a 1450x900 recording that
  conversion was most of the work of the pin's decoding thread. It now takes a fast route, the same picture to the eye:
  that thread took 188-212 M cycles a second instead of 286, and the whole app 975-1005 instead of 1055 while the pin
  played, drawing on the graphics card.
- **A playing pin with its toolbar showing takes about a sixth less processor again.** The toolbar is redrawn for
  every move of the playhead, 15 times a second, and its soft shadow was blurred all over again each time: drawing on
  the graphics card, that was about 170 M cycles a second of a 1450x900 recording's pin. The shadow is now drawn the
  way the pin's coloured frame already is, the same to the eye, and the pin took 814-823 M cycles a second instead of
  about 990.
- **A playing pin leaves the interface's thread freer.** Each frame was copied into the pin's picture on the thread that
  runs the interface, from a copy the decoding thread had made of it: while a 1450x900 recording played, that copy was
  most of that thread's work. The decoding thread now writes each frame into the picture itself, and the interface's
  thread took 40-43 M cycles a second instead of about 77.
- **The app wakes the processor about a third as often while it waits in the tray.** The watchdog that notices when
  the interface stops responding (the log's `UiStall` lines) checked on it ten times a second whatever the app showed,
  and in the tray that was most of the app's wakeups: a test build there woke 55-69 times a second, for 6.5-8 M cycles.
  It now checks once a second while none of the app's windows is open, which took that to 21-24 times a second and
  4.2-4.3 M cycles, and a hotkey press or a click on the tray icon has it check at once, so the wait a press meets is
  still measured, now from the press itself.
- **Text boxes come up sooner on a capture after a pause.** OCR's models leave memory two minutes after their last
  use, and most captures come later than that, so each loaded them again, and ONNX Runtime optimized their graphs
  again every time. The app now keeps a copy of each model as ONNX Runtime optimized it, saved while the app is idle
  after the first load, beside the model (it goes with the OCR module if you remove it), and loads that: the detector
  in about 73 ms instead of 106 in tests, the detector and the recognizer for copying text in 116 instead of 207, with
  the same boxes found.
- **The first capture after the app starts opens sooner.** The capture overlay's first opening of a run ran code
  nothing had run yet: in a published build it took 218-266 ms to come up, against 74-86 for later ones. Ten seconds
  after it starts, the app already built an overlay out of sight to load its code; it now also connects that overlay
  to its settings and lays it out, as an opening does, in steps of under 70 ms. The first overlay then came up in
  123-180 ms, and later ones no slower than before.
- **GIFs are made about a third faster.** A GIF is made in two passes over the video, one that picks its 256 colours
  and one that maps every frame to them, and in each pass the next frame was decoded only once the frame before had
  been dealt with. Each pass now decodes a few frames ahead on a core of its own, so the GIF comes out the same, byte
  for byte, sooner: a 21-second cut of a 1450x900 recording took 5.1 s to turn into a GIF instead of 7.9, and the whole
  save from its pin 7.9 s instead of 10.7. GIF recordings, a pinned recording's GIF and the Compress tab's GIFs all
  gain. A pinned recording cut before it is saved as a GIF also no longer makes its sound for the in-between copy the
  GIF is made from, which leaves the sound out anyway.

### 🛡️ Reliability

- **A mosaic or blur shows what will be saved after the capture overlay is frozen or unfrozen while you draw.** Drawing
  on a live overlay takes a picture of the selection for mosaics and blurs to sample. Freezing the overlay from its
  toolbar then saves from the frozen picture, and going back to live from the screen, but what was being drawn kept
  sampling the picture taken before, so the preview could show other pixels than the saved capture. It now takes the
  selection again from what will be saved.

## v0.73.0 - 2026-11-05

### ⚡ Performance

- **Starting with Windows from a hard disk spends less time waiting on the disk.** The launcher that Windows starts
  at sign-in in the app's place read .NET's own files ahead, which cut the wait before the app's code ran from 27 s to
  6 s at the last sign-in. The app then still waited 10 to 15 s on the disk for the rest of its files before its
  hotkeys worked: its own read-ahead of them ran alongside the start, which read the same disk elsewhere. The launcher
  now reads those files as well before it starts the app, each in one pass, from the list the app writes down after
  each start: 73 files, 76 MB for an installed copy.
- **A pin that has removed a background no longer keeps the app drawing.** The bar a pin shows while it removes a
  background or makes a smart selection kept animating out of sight after it hid: Avalonia pauses a hidden control's
  animation but keeps redrawing every 16 ms for it. From a pin's first such job until the pin was closed, the app's
  interface thread woke about 330 times a second instead of 13, at about eight times its idle CPU. The bar now runs
  only while it shows. The video editor's loading bar did the same for as long as the editor stayed open.
- **A pin keeps at most 256 MB of pictures for undo.** Each crop, rotation, background removal or smart selection on a
  pin kept the whole picture before it, for up to 30 steps: a 4K pin rotated 30 times held about 1 GB until it was
  closed. Once the pictures kept pass 256 MB the oldest such steps now go, the newest always kept: 8 steps of a 4K
  picture, 18 of 2560x1440, still all 30 of 1080p. Steps that keep no picture, such as strokes and shapes, are as
  before.
- **Copying a pinned recording as a GIF takes about half as long.** A GIF has 256 colours, and every frame is mapped
  onto them, each pixel's error spread onto its neighbours: that ran on one core and took about three quarters of the
  time. Frames are now mapped on several cores at once and written in order, so the GIF is the same, byte for byte: at
  the Medium quality, 10.7 s instead of 24 for a 34-second 1450x900 recording, 1.7 s instead of 3.2 for a 6-second
  1134x624 one. GIF exports from the Compress tab and GIF recordings are made the same way, and the Compress tab's bar
  for one now fills evenly.
- **A GIF copied from a pinned recording or exported from the Compress tab is dithered as a GIF recording is, so at
  Medium and Low it comes out smaller.** A GIF's 256 colours are dithered to look like more. The copy and the Compress
  tab spread each pixel's error onto its neighbours at every quality, the finest way and the largest file, where a GIF
  recording does that only at High, uses an ordered pattern at Medium and none at Low. Both now do as the recording
  does; the Compress tab's quality follows its CRF slider as before (20 or less High, 30 or more Low, Medium between,
  where its default of 22 is). At Medium, a 34-second 1450x900 recording's GIF is 3.5 MB instead of 6.5 and takes a
  third less time, two shorter ones' are about a fifth smaller; at Low, 1.3 MB instead of 2.6. Zoomed in, the ordered
  pattern shows as a fine regular grain on smooth gradients. At High nothing changes.
- **Recording on an NVIDIA graphics card takes about a third less of the processor, and other players show its
  colours right.** Every frame was converted from the screen's colours to the video's on the processor, the largest
  part of a recording's work. The card's encoder (NVENC) now takes the screen's colours as they are and converts them
  itself: a 1450x900 region at 30 frames a second takes about 30% less of the processor, and recording the whole
  1920x1080 screen no longer drops frames (it dropped 3 to 7 in 10 seconds). The colours are the same and the picture is
  a little closer to the screen. The file also says now which colour conversion it holds; without that, players such as
  a browser took the usual one for HD video and showed reds a little orange and greens darker. Other encoders are as
  before.
- **The capture overlay no longer redraws 60 times a second while it waits.** The bar along its top, which shows while
  a scan or a translation runs, kept its animation going hidden from the moment the overlay opened: the overlay's
  interface thread woke about 580 times a second instead of 19, at ten times the processor, for as long as it was open.
  It now animates only while it shows.
- **The capture overlay's text scan no longer sets aside 11 MB each time.** The picture handed to the text detector,
  10.8 MB for a 1920x1080 screen, was made anew for every scan and thrown away after it, and a full clean-up of the
  app's memory followed about every second scan. It is now kept while the detector stays loaded: scans after the first
  set aside nothing, and the boxes found are the same.
- **Reopening the capture overlay at once shows its text boxes sooner.** Text scans run one at a time, and a scan
  cancelled while it waited behind another, as when the overlay is closed and opened again quickly, still ran once its
  turn came, about 0.16 s of a 1920x1080 screen, ahead of the new overlay's own. It now stops before it starts.
- **A paused pinned recording shows the frame it is moved to three to ten times sooner.** Each move while paused, from
  the seek bar, the 5-second buttons or the timeline, opened the file again, found its streams and set up the decoder:
  130 to 195 ms on the user's recordings before the frame showed. The pin now keeps that decoder open between such
  moves, 20 to 55 ms for a jump and 10 to 20 ms for a frame a little further on, and lets it go when it plays or after
  30 seconds unused.
- **Dragging a pinned recording's seek bar shows the picture as it moves.** It showed nothing until the drag had
  rested for 0.3 seconds, as each frame then took a new open of the file. With the decoder kept open, a frame is shown
  every 50 ms as the bar moves, and the drag's last spot once it rests, as before.

### 🖼️ Interface

- **A pinned recording shows at its full resolution, pixel for pixel.** At 125% the pin of a recording was decoded at
  its region's size in DIPs, 80% of its pixels (907x499 of a 1134x624 recording), stretched back up to the screen, and
  shrunk 4 pixels more by the border round it, so the picture came out soft. It now decodes the recording at its own
  pixels and puts each on one of the screen's, with the border drawn round the outside as the capture overlay draws it
  round the selection, and copies the frames as they are instead of smoothing every one: drawn with the processor
  ("Use less memory"), a playing pin of that recording takes 23% of a core instead of 27%, of a 1450x900 one 31 to 33%
  instead of 37 to 38%. A pin resized, or on a screen at another scaling, is smoothed as before. At scalings other
  than 100%, cropping such a pin also cut out a part of the frame other than the one selected (at 125%, 80% as far in
  and as large): it now cuts out the part selected.
- **Videos show their own colours.** A video says which of two colour formulas its picture is stored with, and most
  HD video, a phone's, a camera's or another recorder's, says the newer one. The app read every video with the older
  one, so in a pin, the video editor, a GIF made from it and a thumbnail, reds came out darker (pure red as 232,0,1)
  and greens yellower. Each video is now read as it says. A video that says nothing, as the app's own recordings did,
  is read as before.
- **Recordings say how their colours are stored, so other players show them right.** Without it, a browser or a
  phone took a recording 720 lines or taller for the newer formula and showed its reds a little orange and its greens
  darker; the app read it right. On AMD graphics it was the other way round: the card's encoder declares the newer
  formula whatever it is told, while the picture handed to it held the older one. Recordings now say the formula they
  hold, and on AMD graphics the card takes the screen's colours as they are and converts them with the formula it
  declares, which also takes that conversion off the processor.

### 🛡️ Reliability

- **Started at sign-in, the app says when another app holds one of its hotkeys.** Started to the tray, the app had
  not built its main window, whose message it was, so a hotkey that another app had taken simply did nothing. A toast
  now says which, and opening it opens the main window to change it. The same went for the capture overlay's button
  that opens the Modules tab to get a missing model, which did nothing from the tray: it now opens the main window on
  that tab.
- **A manual scrolling capture sets aside far less memory as it runs.** Matching each new picture against what was
  captured so far sampled both into new arrays, two to four times a picture, 115 KB each for a region 900 pixels
  tall, and the app's memory clean-up ran again and again while scrolling: 315 KB set aside a picture before, under
  64 KB now.
- **The check for a graphics card that can record AV1 runs once at a time.** The Record tab, Record mode's warm-up
  and a recording's start could each test the card's AV1 encoders at the same moment, the first time a run needed the
  answer; the others now wait for the first's answer.

## v0.72.1 - 2026-11-04

### 🖼️ Interface

- **A pinned recording loops without a stop, its sound in step with its picture.** At every loop the pin opened the
  file, the decoder and the sound again: the picture stood still 70 to 120 ms, the sound broke off for 60 to 80 ms,
  and it came back a different amount off the picture each time, from 110 ms early to 110 ms late on a test clip. The
  pin now plays its loop in one go, jumping back to the start with nothing reopened, its sound looping alongside and
  the picture keeping to the sound's own clock: on the same clip the sound loops without a gap, and stays 12 to 53 ms
  behind the picture pass after pass (21 ms of that is the clip's own). The loop ends where the picture does, as
  before. This covers a pin played at 1× as recorded or trimmed to one stretch; a timeline with cuts between pieces or
  pieces at other speeds plays a pass per piece, as before. Unmuting, and turning the loop off, start the playback
  again from where it stands, as a seek does.
- **A pinned 30 fps recording plays at 30 frames a second.** A pin shows at most 30 frames a second by default, and
  judged each frame by a clock that moves in 15.6 ms steps: two frames 33 ms apart mostly measured 31 ms, too close, so
  a 30 fps recording played in its pin at about 16 frames a second (194 of 367 frames shown). Every frame of it now
  shows (780 of 787), and a faster clip shows an even share, 30 a second. Drawn with the processor ("Use less memory"),
  showing them all takes 2 to 3% more of one core.

---

## v0.72.0 - 2026-11-04

### 🛡️ Reliability

- **Translating into Traditional Chinese with the local model writes Traditional Chinese.** The model was asked for
  "Chinese (zh-TW)", as its own instructions name it, and answered 13 of 16 English and Japanese test sentences in
  Simplified Chinese (设置 for 設定). It is now asked for Traditional Chinese by name, and answered all 16 in
  Traditional, in the words used in Taiwan (檔案, 儲存, 網路). Asked for Simplified Chinese the same way, it no longer
  leaves Traditional Chinese text as it was.
- **A long compress keeps its picture in step with its sound.** A compress at a quality setting, the Compress tab's
  default, encodes the video in 30-second pieces and joins them. Since v0.67.0 each piece after the first was joined two
  frames late, so the picture fell further behind the sound every 30 seconds: 0.17 s by the end of two minutes, 3 to 4 s
  by the end of half an hour. The pieces now join end to end.

### ⚡ Performance

- **With "Use less memory" on, an open capture overlay takes a fraction of the processor it took.** Drawn with the
  processor, every frame of the overlay is copied whole to the screen however little changed, and the dashed outline
  round the window under the pointer flowed at about 43 frames a second: a fifth to a third of a core for as long as the
  overlay stayed open. Drawn that way, the dashes now step about 21 times a second while the pointer moves and stand
  still a second after it stops, the outline still shown; a move to another window glides as before. The open overlay
  takes about 1% of a core with the pointer at rest and 13 to 18% while it moves. Drawn on the graphics card, nothing
  changes.
- **Editing or pinning a 4K video takes about 0.75 GB less memory.** The Compress editor's frames and a pinned video's
  playback are decoded on the graphics card, and the decoder was also given a thread for every processor core. FFmpeg
  sets aside a 4K picture on the card for each of those threads, though the card does the decoding itself: on a 4K
  phone video the editor held about 1.25 GB while open, and a playing pin as much. On one thread they hold about
  0.47 GB, the pin still plays at full speed, and the editor finds a far frame a little sooner, while a step of one
  frame takes 5 to 10 ms longer.
- **Compressing a rotated video takes a quarter of the time or less.** A video turned in the Compress editor had every
  frame converted to a picture, turned, and converted back. On a 4K phone video that took 45 ms a frame, so turned, ten
  seconds of one took 17 s to compress at 1080p instead of 3 to 8 s. The frames are now turned as they are decoded:
  2.5 s at the default speed and 4 s at slow, and the result is a little closer to the original. A turned video with
  something drawn on it is made as before.
- **The capture overlay no longer stutters as its text scan comes in.** Each box the scan found was a control of its
  own, with its own bindings, layer and style, and putting a scan's hundred boxes and the windows on screen held the
  interface's thread for 35 to 49 ms, often just as a selection was being dragged. One control now draws them all, in
  under a millisecond of that thread, and they look exactly as before. Translate mode's text search boxes are drawn the
  same way.
- **Translating with the local model takes about half as long.** Each translation made the model read its
  instructions again before the text, about a second of the 1.3 to 2.4 s a sentence took on the processor, and set up
  the model's working memory afresh. While the model stays loaded it now keeps both from one translation to the next,
  and reads only what changed: sixteen sentences took 0.4 to 1.6 s each instead of 1.3 to 2.4 s, 14.3 s in all instead
  of 27.5 s. The first translation after the model loads reads the instructions as before.
- **Stopping a recording no longer waits for its sound to be encoded.** The sound was encoded after the stop, about
  20 ms for every second of it: 2.2 s after two minutes, 9.7 s after ten, half a minute or more after half an hour. It
  is now encoded while the recording goes, on a thread that gives way to the capture and needs about one core in forty,
  so the stop only finishes it: 0.06 to 0.09 s after two minutes of sound, 0.14 to 0.19 s after ten. A recording of
  several windows into a file each encoded the sound again for every file, and does it once now. GIF and WebM
  recordings, made from the recorded sound as it is, are saved as before.
- **Stopping a recording saves it about 0.1 s sooner, and a paused one writes its video once less.** Every stop first
  waited 100 ms for files that were closed already. A recording paused and resumed had its stretches joined into a file
  of their own before the sound went in, the whole video written once more; they are now joined with the sound in one
  go. Ten seconds at 1280x720 was saved in 0.24-0.36 s instead of 0.36-0.46 s recorded in one stretch, and in
  0.40-0.41 s instead of 0.54-0.58 s in three.
- **Compressing or exporting a long video no longer takes a gigabyte or more of memory for its sound.** An export
  decoded its sound into memory whole and copied it twice more before writing it out for the encoder: for a 30-minute
  recording, 1 to 2 GB taken and given back, with up to three copies of its 330 MB of sound held at once and about a
  gigabyte kept for a while after. A compress at a quality setting, a trimmed or edited export, a pinned video's export
  and a GIF or WebM made through an edited copy all built their sound that way. It is now written as it is decoded,
  taking about 7 MB in all, and no slower. An audio-only export (WAV, MP3, M4A or OGG) held the whole track once and no
  longer does.
- **A compress or export no longer sits at 100% while it builds the sound.** The sound was decoded and encoded only once
  the video was done, with the bar full: about 0.9 s for each minute of sound, half a minute for a 30-minute recording.
  It is now built while the video encodes, so a video that takes longer to encode than its sound, as most do, ends a
  moment after its bar fills: 0.4 s for a two-minute 960x540 clip, whose sound took 1.9 to 2.8 s to build. Pause and
  Cancel reach the sound too, and its working file goes as soon as it is encoded instead of staying until the end,
  330 MB for half an hour.
- **Starting with Windows from a hard disk spends less time before the app even runs.** At sign-in, .NET took 18 to
  37 s to load its own files from the hard disk the app was installed on, before any of the app's code ran, a few
  kilobytes at a time while other programs read the same disk. Windows now starts a small launcher instead, which
  runs on the .NET Framework that Windows keeps on its own drive: it reads those files through in one pass each and
  then starts the app, which finds them in memory. "Run on startup" switches to it the next time the app runs, and
  the log's `Startup.Timings` line says what it read and how long it took (`launcher=`).
- **Removing the background on a second pin no longer loads the model again.** Each pin loaded the background-removal
  model for itself and kept it 10 s after its last removal, so a second pin's removal in that time loaded a copy of its
  own: 0.9 s instead of 0.4 for a 1600x900 picture, and 180 MB more held while both were kept, 370 MB in all. Every pin
  now shares one, and the second pin's removal runs on the model the first one loaded. Closing a pin no longer unloads
  it at once; it goes 10 s after the last removal, as it did with the pin open.
- **Removing a plain background no longer loads the model.** A picture on one plain colour has its background cut out
  by that colour, in 2 ms at 1920x1080, but its removal loaded the background-removal model all the same: half a second
  first, and 190 MB held for the 10 s after. Only a picture without a plain background loads it now, so a plain one is
  done in under 20 ms and takes no memory.

### 💾 Storage

- **A compress at a quality setting writes its video twice fewer times.** Each 30-second piece was encoded into the
  temporary folder and copied to where the pieces are kept, and the pieces were joined into one more file before the
  output was written from it. They are now encoded where they are kept and joined, with the sound, straight into the
  output: at the end of two minutes of 720p, 99 MB written instead of 197, in half the time.

### 🖼️ Interface

- **Started with Windows, the app now says when there is an update.** Started in the tray, as it is at sign-in, the
  app found a new version and kept it for the main window, which a tray user may not open for days; before v0.71.0 it
  dropped it altogether. A notification now says the new version is available, once a run, and clicking it opens the
  main window, which asks about the download as before.
- **With "Use less memory" on, a capture opens without a white flash.** As the capture overlay was set up, still
  hidden, it told Windows that screen recordings may show it, which every window allows anyway. For a window drawn with
  the processor, that put the overlay on screen as a white rectangle at the size it started with (1440x740 on a
  1920x1080 screen) until its first picture replaced it: a frame or two each time, and about 0.4 s the first time
  after the app starts. A setting that would not change is now left alone. Drawn on the graphics card, the
  overlay never flashed.

---

## v0.71.0 - 2026-11-03

### 🛡️ Reliability

- **The hotkeys can work over Task Manager again: turn on "Run as administrator".** Since v0.68.1 the app has run
  without administrator rights, on the belief that its hotkeys still reach it while a window run as administrator, Task
  Manager say, is in front. They do not: Windows withholds every key from an app that is not elevated then, so pressing
  a hotkey there did nothing. The new setting on the General tab starts the app with administrator rights, with Windows
  asking for permission at every start, sign-in included. The first administrator window explains this once and offers
  it; the notice used to appear only for a hotkey another program had taken, and said the others still worked.
- **No more "Application Startup Failed" when the app hands over to another copy as it starts.** A launch that
  handed over to a copy run as administrator, after the UAC prompt, or to an updated copy installed elsewhere, or that
  stopped because an update could not be verified, ended its own start-up in a way Avalonia 12 no longer accepts. It
  showed a fatal start-up error, though the other copy started fine. It now ends quietly.
- **Mosaic and blur on a frozen screenshot preview the frozen picture.** They previewed the live screen while the
  frozen picture is what gets saved, so the preview could show what had changed since the freeze.
- **A mosaic or blur preview can no longer show scrambled pixels or close the app.** It read the picture from a buffer
  the idle memory clean-up could move mid-read. The picture now goes straight to where the preview reads it, which also
  saves a full-size copy, 33 MB at 4K.
- **Save works on every pinned image.** A pin opened from part of another pin, or from a frame frozen in the Compress
  editor, had a Save button and shortcut that did nothing. Every image pin now saves the same way.
- **Launched to the tray, the app now shows its toasts, the capture-delay countdown and the update offer.** All three
  were set up by the main window, which a launch to the tray, as at login, builds only once you open it. Until then a
  copy showed no confirmation, errors showed nothing, a delayed capture counted down with nothing on screen, and an
  update the start-up check found was dropped. Toasts and the countdown now work without the main window, and an update
  found while it is closed is offered the next time you open it.
- **Recordings keep their system audio in step after a quiet stretch.** While nothing played, Windows handed the
  recording no system audio at all, not even silence: that stretch went missing from the sound track, every sound
  after it played early against the picture, and a recording made while nothing played had no system audio at all.
  The app now plays silence of its own to the same device while it records, which keeps the sound coming without
  being heard.
- **Queued files keep their output category after the app closes.** Closing the app, once the Compress tab had been
  opened, cleared the output category chosen for each file in its queue and saved that, so the files came back with
  the default folder at the next start.
- **Recorded sound starts with the picture and stays with it after a pause.** A recording takes its sound in 50-70 ms
  pieces and kept or dropped each piece whole, so the sound could start up to 68 ms (38 on average) before the first
  frame and played that much behind the picture. Pieces are now cut where the picture starts, which puts the two
  within about 10 ms. Each part of a paused recording also had 6-120 ms less sound than picture: the piece holding the
  pause was dropped, and the picture takes a frame or two more after it. The parts are joined end to end, so everything
  after a pause played that much early, more with each pause. Each part's sound now runs to the pause, then holds
  silence until its picture ends.
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
- **Cancelling a compress to GIF or WebM stops it, and its progress bar runs to the end.** Such a file is made in two
  steps, the edited video and then the GIF or WebM made from it, and only the first moved the bar or heard Cancel. The
  bar sat at 100% through the second, the longer one (59-88% of the time on two screen recordings), and a cancel there
  let it finish and marked the file done. The two steps now share the bar, and a cancel stops either of them.
- **A compress to MP4 of a video with no sound makes an MP4.** A compress at a quality setting, the Compress tab's
  default, of a video with no sound, or with its sound dropped, wrote a Matroska file under the .mp4 name unless the
  video was cut into pieces, sped up, cropped or drawn on. Most players opened it anyway, but apps and sites that go by
  what a file really is could refuse it. It is now a real MP4, with the same frames.
- **Pause holds a compress to GIF or WebM all the way through.** It held only the first step, the H.264 copy with the
  edits applied; making the GIF or WebM from that copy, the longer step, ran on to the end. Both steps now wait.
- **Compressed and trimmed videos stay in step with their sound on 29.97 fps and variable-frame-rate sources.** The
  export rounded the frame rate and counted frames, so a 29.97 fps video came out 0.1% fast (3.6 s an hour) and a
  variable-rate recording drifted. Every frame is now placed by its own time at the source's exact rate.

### ⚡ Performance

- **Opening the Record tab no longer loads FFmpeg in every run.** To list the codecs, the tab tested the graphics card's
  AV1 encoders the first time it opened in each run, which loaded all of FFmpeg and kept it for the rest of the run
  even when nothing was recorded. The answer is now kept beside the settings for the graphics adapters and drivers it
  was found on, so later runs list the codecs without loading FFmpeg: about 18 MB less from then on, and about 47 MB
  less just after the tab opens. A new graphics driver, a new GPU or an update of the app tests again.
- **A rejected translation falls back to translating line by line a whole generation sooner.** The rejected text was
  first sent to the model again with the same languages, and the model answers the same text the same way every time,
  so the wait bought the same answer. The line-by-line fallback now starts at once.
- **A second translation within two minutes no longer loads the translation model again.** Each Translate press freed
  the 2.5 GB model when it finished, and each capture overlay started with a translation engine and cache of its own,
  so every press loaded the model again and no overlay reused another's translations. The model and the translations
  already made now last the whole run: the model is freed once it has gone unused for two minutes, when AI is turned
  off, before a module is removed and at exit. It also starts loading as a press begins, alongside OCR, instead of
  once OCR has read the text.
- **Translating no longer takes a snapshot of every running process a dozen times per press.** A debug log line read
  the app's memory use, which on Windows means a snapshot of every process, several times on the UI thread, and was
  then thrown away unless debug logging was on. The memory use is now read only when debug logging is on.
- **Text detection no longer throws away tens of megabytes per scan.** Reading the detection model's result made a
  small array for every pixel, about 38 MB of garbage for a 1280×736 scan. It is now read straight from the model's
  output.
- **Manual scrolling capture no longer writes a log line for every frame.** About 25 lines a second went to the log
  file, each waiting for the file and flushing it; the line now needs debug logging on.
- **Closing the capture overlay no longer makes every window on screen repaint.** Each close repainted the whole
  desktop as a safety net for a leftover recording frame, which only an overlay that recorded or was kept out of screen
  captures can leave; only those do it now. The overlay and pins also stop redrawing when their shape is set again
  unchanged.
- **Saving or cropping a video pin, and finishing a compress, no longer freeze the app while the file is copied.** The
  finished file, for an unedited pin the whole recording, was copied on the UI thread; it now copies in the background
  while the progress shows. A crop moves its clip instead, and a save or crop no longer leaves its export in the temp
  folder.
- **The app no longer loads FFmpeg and tests the graphics card's AV1 encoders at every launch.** The check that tells
  the Record tab whether AV1 and GIF recording are available ran at every start-up, a launch to the tray at login
  included. It now runs when the Record tab is first opened, or as an AV1 recording starts, and FFmpeg loads the first
  time a recording, a video pin or the Record tab needs it.
- **Reading a capture's text takes about a quarter less time.** Each line was read again with its contrast stretched
  whenever its box was small, as most screen text is, and up to four times on a dark background, and the best reading
  kept, even when the first was already sure of every character. A line now stops at a reading that is sure throughout:
  on 50 real captures (610 lines) that left out a third of the readings and changed one character in 6626. Quick OCR,
  translation and the automatic choice of OCR language all read this way.
- **Translation mode's text search no longer holds up the overlay while it copies the screen.** Finding the text on the
  whole screen began with a smaller copy of it, made on the app's main thread each time the search ran: about 33 ms at
  1920x1080 or 4K. A large selection was brought down to size there too before it was translated. Both copies are now
  made in the background, with the reading.
- **A capture's text boxes come up sooner when OCR has been idle.** The capture overlay looked for text only once it
  was on screen, and OCR, which unloads after two minutes unused, then loaded both its models, though the overlay only
  finds text and never reads it, and ran its first detection slowly while the graphics card set itself up: 0.6-0.8 s
  before the first box. The overlay now loads text detection alone, starting as it opens, which with its first
  detection takes about 0.3 s, and 0.5 s on the first capture after launch. Changing the OCR language also reloads only
  that language's reader.
- **OCR no longer leaves hundreds of MB behind.** OCR ran on the graphics card through DirectML, which does not give
  back all it allocates: the first capture that looked for text left the app about 270 MB larger until it closed, and
  each one held 500-550 MB for the next five minutes. OCR now runs on the CPU, and text detection hands its working
  memory back after each run: 40-50 MB while OCR is loaded, and about 10 MB once it unloads two minutes after the last
  use. It is no slower where it counts. Reading 30 lines of text takes 0.5 s instead of 1.9 s, as the graphics card set
  itself up again whenever the width of the text changed, and the first detection after OCR has been idle about 0.1 s
  instead of 0.3-0.4 s; only a detection right after another is slower, about 70 ms instead of 16. On Linux, where OCR
  already ran on the CPU, it now holds 40-50 MB while loaded instead of about 340.
- **Background removal and smart selection no longer take gigabytes and leave hundreds of MB behind.** Both ran on the
  graphics card through DirectML, as OCR did. A background removal held about 1.1 GB, then 480 MB for the next five
  minutes and 255 MB until the app closed; smart selection took the app up by 2.9 GB while in use and left 370-380 MB
  until it closed. Both now run on the CPU: about 190 MB while background removal is loaded, which it stays for ten
  seconds after a removal, and about 215 MB while smart selection is in use, with 20-40 MB left after either. A removal
  after the model has unloaded, the usual case, is done sooner, as the model loads in half the time: about 1 s for a
  picture the size of two screens, and 0.4 s for one right after it. Smart selection's first mask comes about as soon as
  before, as the model loads faster and no longer runs once on a blank picture first; each further picture then takes
  1.4 s to get ready instead of 0.2, and a click about as long as before. On Linux, where both already ran on the CPU,
  they now hold about 190 and 215 MB instead of up to 1.2 and 2.4 GB.
- **Opening the Record tab no longer leaves 190 MB behind on an NVIDIA graphics card.** The tab checks whether the
  card can record AV1 by opening its encoder, which loaded CUDA, and CUDA's driver kept 190 MB of the app until it
  closed, though nothing was recorded. The check now opens the encoder through Direct3D 11, which leaves about 35 MB.
  Recording itself is unchanged: once the NVIDIA encoder has recorded, its driver keeps 200-260 MB of the app either
  way.
- **The settings window gives its memory back five minutes after it is closed to the tray.** Closing it only hid it,
  with every tab it had built, for as long as the app ran. Five minutes after it is closed to the tray it is now closed
  for good, and the next open builds it again, on its first tab. With every tab opened, the app's commit size fell
  14-28 MB. Most of what opening the window cost still stays: the graphics card's share, the fonts and the libraries it
  loaded.
- **The History tab keeps only the thumbnails on screen.** Opening it decoded the thumbnail of every entry, 300 with
  the history full, and kept them all for as long as the app ran: about 90 MB, which leaving the tab never gave back.
  It now decodes the thumbnails of the cards in view as they scroll in, 12-16 at a time in a window of 1100x900, and
  lets them go as they scroll out or the tab is left. With the history full, the app held about 60 MB with the tab open
  instead of 130.
- **The Chinese and Japanese interface no longer copies its font into memory, twice.** The fonts for the Chinese and
  Japanese interface (16.8 and 12.9 MB) were read from inside the app the way any font file is read, which copied the
  whole file into memory, and the bold the interface uses, which these fonts have no face for, copied it once more:
  with the Chinese interface, about 35 MB. The fonts are now read where they already are, inside the app's own file,
  and the bold is drawn from there too: about 1 MB. Text looks exactly as before, to the pixel.
- **Mosaic and blur previews no longer copy the whole picture.** Each mosaic or blur on a pin or in the capture
  overlay made its own copy of the whole picture to draw its preview from, and kept it for as long as it was on screen:
  about 32 MB each at 4K. A preview now reads only the part of the picture under it: with six on a 4K picture, the app
  holds about 190 MB less. Resizing a pin also no longer redraws every mosaic and blur on it at each step of the drag,
  as their look does not change with it.
- **A frozen screenshot opens sooner.** A screenshot freezes the screen by default, and its overlay waits for that grab
  of the whole desktop. The grab began only once the overlay's window and the rest were built, and copied the screen
  twice more through GDI+ than it needed. It now starts as the capture does and runs while they are built (13-34 ms of
  a capture, about 300 of the first after launch), and reads the screen with one copy: on two screens of 3968x1160 in
  all, 41-58 ms instead of 56-73. The log's `SnipOpen.Timings` line says whether that early grab was used.
- **A frozen screenshot holds the desktop once.** While a frozen screenshot is open, the app kept the grab of the whole
  desktop and a copy of it to show: 35 MB on two screens of 3968x1160 in all (a copy is 4 bytes a pixel, 66 MB for two
  4K screens side by side). What is shown and what is cropped, pinned and read for text now share one copy, 19 MB
  there, and it is made before the overlay shows instead of on the overlay's thread as it opens.
- **Copying a pin encodes its picture once.** Each Ctrl+C on a pin encoded the picture for the file it puts on the
  clipboard, then again for the picture beside it: on a 4K picture, about 140 ms of work where 70 now do, and more for
  a photo. An annotated pin, or a selection, also made a whole new picture for each copy, and a rotation one for its
  annotations, which none of them let go of. The annotations are now drawn into the one copy that is encoded, and the
  selection cut out of it in place.
- **Copying a screenshot is done sooner, and encodes the picture once.** A copy encoded the picture as a PNG for the
  clipboard and again for its copy in the history, both at full compression, and a frozen screenshot's overlay stayed
  on screen until both were done: 0.26-0.72 s for a whole 1920x1080 screen, seconds at 4K. One PNG with the faster
  settings now goes to both, in 0.03-0.09 s for the same screen, and the history's copy is written after the overlay
  has closed. Those history copies take 17-30% more disk space for it.
- **Exporting a pinned video as WebM no longer works on the app's main thread, and leaves no working files behind.** The
  export decoded the clip's whole sound into memory and wrote it out on that thread, 20-30 ms for half a minute of sound
  and about 0.1 s the first time in a run, longer for a longer clip, and allocated up to four times the sound's size for
  it (about 11 MB a minute), much of which stayed held afterwards. A cut clip's trimmed copy, and a failed export's
  folder, stayed in the temp folder. All of it now runs in the background, the sound is written as it is decoded, in
  0.1 MB, and only the exported file stays. The Compress tab writes a WebM's sound the same way.
- **A recording saved as WebM whose sound cannot be added is saved in about half the time.** The video is encoded
  first and the sound added after it; when adding the sound failed, the save encoded the whole video again, the
  slowest part of it, only to get the file it already had. It now keeps that file.
- **Stepping through a video in the Compress editor answers at once.** Every frame the paused editor or its quality
  comparison showed, a step, a skip, a drop on the slider, or the same frame drawn again after a rotation or a crop,
  opened the file afresh, read its streams and set up a decoder and a graphics device: 0.13-0.2 s a click on a laptop,
  twice in the comparison. The file now stays open while you work on it: a step forward takes a few milliseconds, the
  same frame none, a step back, a skip or a drop on the slider 0.02-0.1 s. It closes again after half a minute without a
  click.
- **A compress to GIF or WebM of a clip left unedited takes up to a third less time.** Every GIF or WebM was made from a
  copy of the clip encoded first as H.264 with the edits applied, edits or none. With nothing to apply, it now comes
  from the file itself, which also spares it a generation of loss: on three screen recordings of 4 to 35 seconds, a GIF
  took 18-37% less time and a WebM 14-26% less, and the GIFs came out up to half the size.
- **Saving a pin with annotations leaves the pin alone while its dialog is open.** The save drew the annotations into a
  copy of the picture and put that copy in the pin for as long as the dialog was open, cancelled or not: the pin drew
  its annotations twice meanwhile, so a highlighter looked darker, a smart selection lost its points, and the copy (33
  MB at 4K) stayed until the next clean-up. The annotations are now drawn only once a file is picked, into the copy that
  is written, which goes as soon as it is; a save also copies the picture once instead of three times, and a cancelled
  one not at all.
- **A 4K pin no longer leaves 32 MB behind after a change, and checks its see-through parts in a third of the time.**
  Rotating, saving, copying or removing the background of a pin each copied the whole picture first, into memory the
  app then kept, and so did the check a pin runs whenever its picture changes, which works out its see-through parts
  so that clicks pass through them: 32 MB for each size of picture, from the first such change on. A 4K pin turned
  three times now holds 33 MB less while open, and leaves 6 MB instead of 39 once closed. The check reads the picture
  a strip at a time and eight pixels at once: about 3 ms instead of 8 for a 4K picture, and 9 ms instead of 25 the
  first time after launch.
- **A pin closed with Alt+F4 gives back its memory.** Only a pin's close button, Ctrl+W and its menu's Close let go of
  what it held. Closed with Alt+F4, a pin kept every picture in its undo history until the app closed, about 125 MB
  for a 4K pin turned three times, and with smart selection picked it kept that model loaded as well. However a pin
  closes, both are now let go of at once, and its picture goes at the next idle clean-up.
- **The idle clean-up hands back the memory .NET keeps free for later.** It compacted the heap but kept the space it
  freed: after the settings window had been let go of, the heap held 35 MB and kept 51. The clean-up now returns the
  difference, which took the commit size down 14 MB more there.
- **Opening the Compress tab with a long queue no longer freezes the app.** The queue built a row for every file at
  once, each with two dropdowns and eight buttons, though its box shows four or five: a 79-file queue came to 9,300
  controls and froze the app for 0.77 s the first time the tab opened. Only the rows in view are built now, and the
  rest as they scroll in.
- **The first capture after the computer has sat idle no longer freezes the app for seconds.** Five minutes after its
  last use the app emptied its working set, which moved all of its memory out of RAM without freeing any of it. Hours
  later Windows had given that RAM to other programs, so the next capture hotkey first had to read the app back from
  the page file: one press measured the capture overlay taking 2.3 s to open and the app not responding for 4.1 s. The
  app no longer does this, and Windows still takes an idle app's memory back by itself when another program needs it.
  Task Manager's Memory column now shows a few hundred MB while the app sits idle, where it showed a few MB; what the
  app holds, its commit size, is the same as before.
- **Clearing a selection with Esc no longer looks for text in the capture twice.** Esc takes the capture overlay back
  to choosing a region, which starts a text scan, and a leftover second start ran the same detection again right
  behind the first. On a frozen screen, where the picture cannot change, the text boxes already found now come back at
  once with no scan at all; each return used to copy the whole picture on the UI thread and find the same boxes again.
- **The first recording after launch starts about as quickly as the rest.** Nothing is recorded until a start
  finishes, and the first one also loaded FFmpeg and opened the video encoder for the first time: 0.35-0.39 s on the
  machine measured, against 0.17-0.21 s for every later recording. Entering Record mode now gets both ready in the
  background while you pick what to record, which brings the first start to 0.21-0.24 s. On an NVIDIA graphics card it
  starts only the encoder's driver and does not open the encoder: an opened encoder made the driver keep about 190 MB
  of the app until it closed, recorded with or not, and the driver alone keeps about 8 MB. Every region recording also
  starts about 17 ms sooner: each start asked Windows whether window capture works, which only a recording of picked
  windows needs, and a window recording now asks once a run instead of twice a recording.
- **A capture hotkey opens its overlay without waiting for the settings to be saved.** Every capture saves the
  settings, and its overlay waited while they were turned into text, a step that redid its preparation from scratch
  each time, and while the saved file was read back to see whether anything had changed. The overlay now opens first,
  and the save follows once it is up, reading and writing the file in the background.
- **Saving a recording to another drive no longer freezes the app.** After the save dialog, or when a recording
  stopped by itself, the file was moved out of the temp folder on the UI thread. To another drive that is a full copy,
  about 58 MB for each minute of 1080p at 30 fps, and the app and its hotkeys stopped responding until it was done. The
  copy now runs in the background, and the overlay closes and the folder opens once the file is in place, as before.
- **Compressing to H.265 and saving as WebM use every core.** The H.265 encoder worked on one frame at a time and the
  WebM encoder on a single core. A 1080p H.265 compress now runs about 1.5× as fast and a WebM about twice as fast;
  H.264 and AV1 already used every core.
- **Resizing a pinned image keeps up with the mouse.** Every mouse move resized the pin and rebuilt the shape that
  lets clicks through its see-through parts, several times over: about 32 rebuilds each frame, which kept the app busy
  all through a drag and left everything else it had to do waiting, for up to 3 s at a time. A pin whose background had
  been removed took 6-7 ms a rebuild and was redrawn only 34-41 times a second. A pin now takes the newest mouse
  position once a frame and rebuilds its shape once or twice after it, and a removed background's shape is worked out
  once and then scaled: dragging a corner keeps the app 18-27% busy at 60 frames a second, and nothing waits more than
  about 45 ms. Video pins take the mouse once a frame too.
- **A new setting trades processor time for memory: "Use less memory (draw with the CPU)".** It is on the General tab
  and off unless you turn it on. Drawing with the graphics card makes Direct3D and the graphics driver hold memory
  inside the app, the largest single part of what it holds while idle; drawing every window with the processor keeps
  that out. With an RTX 4070 SUPER, the commit size Task Manager shows went from 94-95 MB to 30-32 MB with the app in
  the tray, and from 188-196 MB to 96-97 MB with the settings window open; a laptop whose graphics share its memory
  saved about as much, and 170 MB after a capture. In exchange the processor does all the drawing: on that laptop a
  full-screen capture and a playing video pin took about twice the processor time, and resizing a pin draws 29-46
  frames a second instead of 60. That was 19-20 until the glow around pins and the capture frame, and the checkerboard
  behind a pin's image, were drawn in ways that cost the processor almost nothing; they look the same as before. The
  app picks how it draws as it starts, so changing the setting offers to restart it. Windows only.
- **The start-up line in the log now says whether a slow start was working or waiting.** Started at sign-in from a
  hard disk, the app took 67-77 s before its hotkeys worked, against a few seconds at most otherwise, and
  `Startup.Timings` could not say why. Each stage now comes with the processor time it used, Avalonia's start-up is
  split into its parts, and on Windows the line adds how busy the whole machine was, how many of the app's pages had to
  be read from disk, whether the app's drive is a hard disk, and a priority Windows lowered.
- **The app wakes up about a third as often while it sits idle.** The check that notices a frozen app, which writes
  the `UiStall` lines in the log, ran a 100 ms timer on the app's main thread, and each tick woke it eight to eleven
  times: 80-110 wakeups a second, in the tray too, nearly all the app did while it waited. A thread of its own now asks
  the main thread to answer every 100 ms, posted the way a hotkey press is: about 35 wakeups a second in all, and about
  40% less processor time, which was never more than a millisecond a second. A freeze is still measured to within
  0.1 s, now by how long a hotkey press would have waited.
- **Starting from a hard disk no longer reads the app a few kilobytes at a time.** Started at sign-in from a hard
  disk, the app took 46 s before its hotkeys worked, with barely a second of processor time: it read its own files
  in thousands of small pieces, in no order, while the other apps starting with Windows read the same disk. Each start
  now writes down which of the app's files it used, and the next start reads those through in one pass each, from its
  first moment, so they are in memory by the time they are needed. On that disk, what the start then reads of them
  came from memory in 12 ms instead of 1.5 s. The log's `Startup.Timings` line says what was read ahead
  (`prefetch=`). The part before the app's own code runs is out of its reach: moving the app to an SSD helps there.
- **The first Record overlay of a run no longer waits for FFmpeg.** The Record hotkey loaded FFmpeg's libraries,
  about 150 MB, before the overlay could appear: 2.9 s from a hard disk at the first press after three idle hours,
  with nothing on screen meanwhile. The overlay now opens at once, and FFmpeg loads once it is on screen, while you
  choose what to record; the record button still checks it before it starts.
- **A `UiStall` line in the log now counts the page faults during the freeze.** Thousands of them at a few percent
  processor time mean the app was waiting for its own memory to come back from disk, as after a long idle spell,
  rather than for something else.

### 💾 Storage

- **A two-pass compress no longer writes a second copy of the video to the temporary folder.** Its first pass, run only
  for the encoder's statistics, also wrote the whole video there, about as large as the finished file, and nothing read
  it. It now writes no video, and the finished file is the same.
- **The Windows download is about 10 MB smaller.** With background removal and smart selection on the CPU like OCR,
  nothing used the graphics-card build of ONNX Runtime any more. The app now ships its CPU build, without DirectML:
  the app's folder is 21 MB smaller and the zip about 10 MB. An update copies the new version over the old one and
  removes nothing, so an updated install would keep the 18 MB `DirectML.dll` it no longer uses; the app deletes it
  a few seconds after start-up.

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
