# Optimization work — local (Windows) handoff

Hands the **remaining Tier 2–4 optimization items** from the cloud/Linux session (compile-only, cannot
run the Windows app) to a **local Windows session** that can build, run, and verify behaviour.

Context: Tier 1 (process/CI pack) and the first Tier-2 cleanups shipped in **PR #62 (merged into `main`)** —
Linux compile gate, FFmpeg+NuGet CI caching, PR skip-publish, localization key-usage check, release
CHANGELOG gate, −281-line dead CLI cluster, interface-first hygiene, `VideoFilePickerTypes`, GIF
`QualityLadder`. The items below were deliberately left for local because they are **compress/recording-
adjacent** or need **runtime verification** that Linux can't do.

## Branch
Work on **`claude/claude-md-docs-d2lq7f`** (already restarted from the post-#62 `main` — it contains only
this handoff doc on top of `main`). Pull it before starting:

```powershell
git fetch origin claude/claude-md-docs-d2lq7f
git checkout claude/claude-md-docs-d2lq7f
git pull origin claude/claude-md-docs-d2lq7f
```

Every push gets a **Linux Compile Check** (~80 s) — but that only proves it compiles. **You** run
`scripts/verify.ps1` and the gated compress matrix to prove behaviour.

## Remaining items (do each as its own commit; verify after each)

### F2 — remove the unreachable `Cli.Wrap` ffmpeg fallbacks  ⚠️ compress-adjacent
`FfmpegDownloaderService.FfmpegExecutablePath` is now a DLL-runtime stub returning `string.Empty`, so every
`File.Exists(ffmpegPath)` guard is permanently false and these CLI branches are dead:
- `FloatingVideoViewModel.cs:48` `_ffmpegPath`, `:51` `FFmpegPath`, `:402` assignment.
- `FloatingVideoViewModel.Actions.cs:661` `ExportBurntInVideoAsync` — the `FFmpegPath` branch at `:710`.
- `FloatingVideoViewModel.Actions.cs:808` `ExportComposedAsync` — the `FFmpegPath` branch at `:868`.

The in-process libav paths are dispatched first; when they fail the methods already return `null`/failed.
Delete the exe branches and the `_ffmpegPath`/`FFmpegPath` members, then check whether
`FfmpegDownloaderService` can shed its now-unused exe-path API.
**Verify:** pinned-video Save / Crop / annotation burn-in / GIF+WebM export still work; recording still
finalizes. This is the highest-collision file — rebase carefully if local compress work advanced `main`.

### I2 — route the 18 raw `Dispatcher.UIThread.Post(async …)` lambdas through `.Forget()`
There are 18 `Dispatcher.UIThread.Post(async …)` / `InvokeAsync(async …)` fire-and-forget lambdas across
the VMs/views (FloatingVideoViewModel.Actions, FloatingImageViewModel.AI, MainWindowViewModel.Commands/
About, SnipWindowViewModel.ModeRouting/Toolbar, SnipWindow/MainWindow code-behind, WindowsScreenCapture…).
An unobserved `async void`-style lambda swallows exceptions. Wrap the inner task with the existing
`.Forget()` helper (`Services/Core/Infrastructure/TaskObservationExtensions.cs`) so faults are logged.
**Verify:** compile + a normal run; no behaviour change intended, just observed faults.

### G3 — unify the two WebM+Opus routes  ⚠️ medium behavioural risk
`RecordingService.Finalize.cs:518 FinalizeAsWebmAsync` and
`FloatingVideoViewModel.Actions.cs:460 ExportGifWebmInProcessAsync` both build a VP9/WebM + Opus output via
libav. Factor the shared muxing into one helper (a static on the transcoder side is cleanest). This changes
a real encode path — **verify** a recording→WebM and a pin→WebM export both play with audio before pushing.

### Tier 3 — characterization tests for the untested transcoders
`LibavAacTranscoder`, `LibavOpusTranscoder`, `LibavPinAudioPcmDecoder` (all under
`Services/Core/Media/NativeFFmpeg/`) have **zero direct tests**. Add gated facts (same env gate as
`CompressIntegrationTests`):
- A tiny synthetic **WAV writer** test helper (none exists — `WavMixerTests` is buffer-only).
- WAV→AAC: assert `.m4a` exists, has an audio stream, bitrate honored.
- WAV→Opus: assert output exists with an audio stream.
- Round-trip: encode → `LibavPinAudioPcmDecoder` decodes back to PCM.
- Muxer mp4-vs-mkv container cases at the SegmentResume seam.
Extend the `scripts/test-compress.ps1` docs line if you add a new gate.

### Tier 4 — split the 1,078-line `MainWindowViewModel.Settings.cs`
Follow the proven `RecordingSettingsViewModel` template (sub-VM `{ get; } = new()` on
`MainWindowViewModel` + its fields added to `CreateSettingsSnapshot`/`ApplySettingsSnapshot` in
`Settings.Persistence.cs`, saved via the existing coordinator). Extract, **one per commit**, XAML bindings
updated (`{Binding General.X}` etc.), compile-checked per push:
- `GeneralSettingsViewModel` (language / startup / theme / border)
- `SnipSettingsViewModel`
- `TranslationSettingsViewModel`

**Do NOT touch** `MainWindowViewModel.VideoCompress.cs` / `VideoCompressBatch.cs` — the active local
compress/editor hot zone. Note them only as future work.

## House rules (unchanged)
- **Localization parity** is enforced (`scripts/check-localization.ps1`, run by `verify.ps1`) — touch one
  locale JSON, touch all three. The new key-usage phase also fails on referenced-but-missing keys.
- Run `scripts/verify.ps1` before pushing (build + tests + 25% coverage gate + publish smoke; `-SkipPublish`
  to skip the last step).
- Commit footers required on this repo:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>` and the `Claude-Session:` line.
- Push with `git push -u origin claude/claude-md-docs-d2lq7f`. Open a PR only when the user asks.
- Each commit message should state its blast radius (compress-adjacent / recording / tests-only) so
  parallel sessions can rebase around it.
