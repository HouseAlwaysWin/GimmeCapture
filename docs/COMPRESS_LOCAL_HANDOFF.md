# Compress feature — local (Windows) handoff

This hands the **Compress Video** work from the cloud/Linux session (which cannot build or run the
Windows-only app) to a **local Windows session** that can build, test, and actually encode.

## Branch
All work is on **`claude/claude-md-docs-d2lq7f`**. Pull it before starting:

```powershell
git fetch origin claude/claude-md-docs-d2lq7f
git checkout claude/claude-md-docs-d2lq7f
git pull origin claude/claude-md-docs-d2lq7f
```

## What exists now (cloud session, UNVERIFIED on Windows)
The **Compress** tab (new top-level tab, after Record) imports any video and re-encodes it smaller via
the in-process `LibavClipExporter`. Commits on the branch:

1. Compress tab: import file → quality/format → auto-derive output next to source.
2. H.265 codec + compress-to-target-size (single-pass ABR).
3. Target-size adaptive corrective pass (re-encode once if the first overshoots).
4. Auto-save output (no second file dialog).
5. **Stage 1 controls** — `LibavExportOptions` threaded through `LibavClipExporter.TryExport`
   (null = original behaviour, so the floating-video callers are untouched):
   - Resolution downscale (`ScaleToMaxHeight`, even-dim safe) — Original/1080p/720p/480p
   - Encoder preset (ultrafast..slow), replacing hardcoded `veryfast`
   - CRF slider (14–40, default 23) replacing the 3-step quality ladder
   - Remove-audio toggle

Also on this branch (separate, but recording-related): a fix so **recording honours MP4** instead of
force-converting to MKV for hardware encoders, and so **MKV output carries AAC audio**
(`RecordingService.Finalize.cs`). Worth a manual recording check too.

## First thing to do: verify Stage 1 builds + encodes correctly

```powershell
powershell -ExecutionPolicy Bypass -File scripts/test-compress.ps1 -Source "D:\path\to\your4k.mp4"
```

(Drop `-Source` to auto-generate a short 4K clip if `ffmpeg.exe` is on PATH.)

The script: ensures FFmpeg libs → builds Debug → runs unit tests → runs the **gated real-encode matrix**
(`tests/GimmeCapture.Tests/CompressIntegrationTests.cs`). That test self-verifies (via the project's own
libav probes — no ffprobe needed):

- baseline keeps source resolution + audio
- `scale720`/`scale1080` produce exactly 720/1080 height, even width
- `crf30` file < `crf18` file
- `h265_720` encodes at 720
- `noaudio` has no audio track; `with_audio.mkv` **does** (AAC-in-Matroska)
- `target5mb` lands ≤ 5 MB + 20 %

If it builds green and the matrix passes, Stage 1 is real. If the build fails, fix the compile errors
(most likely in `LibavClipExporter.cs`, `LibavExportOptions.cs`, `LibavVideoFramePlayer.ProbeVideoSizeAsync`,
or `MainWindowViewModel.VideoCompress.cs`) — these were written without a compiler.

## Then: manual GUI smoke
Run the app, open the **Compress** tab, and confirm with the 4K clip:
- 720p output is actually 720p and smaller; CRF 18 vs 30 differs clearly; preset slow ≈ smaller/slower;
  remove-audio yields no audio; H.265 + 720p + CRF 25 works; target-size lands near the requested MB.
- Regression: pinned-video **Save / Crop / annotation burn-in** still work (they share `TryExport`).
- Recording: set MP4, record (hardware encoder), confirm the file is `.mp4` **with audio**; set MKV,
  confirm `.mkv` **with audio**.

## Remaining roadmap (the user opted into all of these — HandBrake-lite)
Stage 1 (above) shipped the low-risk engine knobs. Still to do:

- **Stage 2 (engine):** FPS cap (needs frame decimation in `EncodeVideoRanges`), audio bitrate / channel
  (mixdown) selection, **true 2-pass** ABR (replace the adaptive single-pass approximation).
- **Stage 3 (UI/VM):** live output-size estimate before encoding, save/load compression presets, batch
  queue (drop multiple files / a folder).

Implement each as its own commit, build + run `scripts/test-compress.ps1` after each, extend the matrix in
`CompressIntegrationTests.cs` with the new knob's assertion.

## Notes
- Localization parity is enforced (`scripts/check-localization.ps1`); touch one locale JSON, touch all three.
- Commit footers required on this repo:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` and the `Claude-Session:` line.
- Push with `git push -u origin claude/claude-md-docs-d2lq7f`. PR only when the user asks.
