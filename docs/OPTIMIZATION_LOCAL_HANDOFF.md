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

## Status (local Windows session)

| Item | State | Commit | Verified |
| --- | --- | --- | --- |
| **G3** — unify the two WebM+Opus routes | ✅ done | `refactor(webm): …[G3]` | build + 706 unit tests green; **runtime WebM (recording + pin) pending on hardware** |
| **F2** — remove dead `Cli.Wrap` ffmpeg fallbacks | ✅ done | `cleanup: …[F2]` | build + 24 adjacent tests; **runtime pin Save/Crop/burn-in/GIF+WebM + recording finalize pending on hardware** |
| **I2** — `.Forget()` dispatcher hygiene | ✅ done | `hygiene: …[I2]` | build; observability-only, no behaviour change |
| **Tier 3** — transcoder characterization tests | ✅ done | `test: …[Tier 3]` | 5/5 pass with `COMPRESS_IT_OUTDIR` set (real aac/opus encode), 5/5 no-op without it |
| **Tier 4** — split `MainWindowViewModel.Settings.cs` | ⏳ remaining | — | see refined plan below |

Runtime verification (F2 + G3) still needs the dual-monitor hardware; unit tests can't prove the
export/record paths play with audio.

## Remaining items (do each as its own commit; verify after each)

### F2 — remove the unreachable `Cli.Wrap` ffmpeg fallbacks  ⚠️ compress-adjacent  ✅ DONE
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

### I2 — route the 18 raw `Dispatcher.UIThread.Post(async …)` lambdas through `.Forget()`  ✅ DONE
> Only 6 of the 18 were genuine fire-and-forget (converted to `Post(() => X().Forget("…"))`); 3 already
> caught internally (upgraded `Debug.WriteLine` → `AppLog.Error`); the other 9 are `await …InvokeAsync`
> (already observed) and were left alone. `SnipWindow.OnOpened`'s 140-line lambda was wrapped in a logging
> try/catch (too large to extract).

There are 18 `Dispatcher.UIThread.Post(async …)` / `InvokeAsync(async …)` fire-and-forget lambdas across
the VMs/views (FloatingVideoViewModel.Actions, FloatingImageViewModel.AI, MainWindowViewModel.Commands/
About, SnipWindowViewModel.ModeRouting/Toolbar, SnipWindow/MainWindow code-behind, WindowsScreenCapture…).
An unobserved `async void`-style lambda swallows exceptions. Wrap the inner task with the existing
`.Forget()` helper (`Services/Core/Infrastructure/TaskObservationExtensions.cs`) so faults are logged.
**Verify:** compile + a normal run; no behaviour change intended, just observed faults.

### G3 — unify the two WebM+Opus routes  ⚠️ medium behavioural risk  ✅ DONE
> Added `LibavWebmTranscoder.MuxWebmWithOpus(videoOnlyWebm, wav, out, quality)` (returns `MuxStats`,
> throws on failure, self-cleans its temp `.ogg`). Both call sites keep their own audio acquisition +
> fallback; only the WAV→Opus→mux tail is shared. `LibavOpusTranscoder`/`LibavMuxer` no longer referenced
> directly at either site.

`RecordingService.Finalize.cs:518 FinalizeAsWebmAsync` and
`FloatingVideoViewModel.Actions.cs:460 ExportGifWebmInProcessAsync` both build a VP9/WebM + Opus output via
libav. Factor the shared muxing into one helper (a static on the transcoder side is cleanest). This changes
a real encode path — **verify** a recording→WebM and a pin→WebM export both play with audio before pushing.

### Tier 3 — characterization tests for the untested transcoders  ✅ DONE
> Added `WavTestAudio` (standalone PCM-s16le WAV writer, no NAudio) + `LibavAudioTranscoderTests`
> (WAV→AAC exists/decodes/bitrate-honored/mono; WAV→Opus exists/decodes; round-trip → 48k/16-bit/stereo
> PCM). Gated on `COMPRESS_IT_OUTDIR` (the WAV is synthesized, so no source clip needed); `test-compress.ps1`
> runs them in step [5/5]. The muxer mp4-vs-mkv seam test was skipped — it needs a synthetic *video* input
> with no cheap in-process source; `CompressIntegrationTests` already covers MKV-has-audio.

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

**Refined plan (confirmed by reading the code — the file is 959 lines, not 1,078):**
- **Save-trigger model:** pure-data props persist via a *global* `this.PropertyChanged → QueueSettingsSave`
  self-subscription (`MainWindowViewModel.cs` ~408-417). `RecordingSettings` adds its own
  `RecordingSettings.PropertyChanged += … QueueSettingsSave` (`~419`). So a moved pure-data prop needs the
  **same per-sub-VM subscription**; the inline `Settings.X = value` writes (only `ScrollingCaptureDirection`
  has one) are redundant with the snapshot flow and can be dropped.
- **Persistence:** update `CreateSettingsSnapshot` / `ApplySettingsSnapshot` (`Settings.Persistence.cs`) to
  read/write `General.X` / `Snip.X` / `Translation.X`. `MainWindowSettingsSnapshot` field names stay — it's
  the single persistence sink and is covered by `MainWindowSettingsPersistenceServiceTests` (the VM↔snapshot
  mapping is **not** unit-tested → needs a runtime settings round-trip).
- **Gotcha — localized option lists:** `AvailableCaptureDelays` / `AvailableOcrTextLayouts` /
  `AvailableScrollDirections` (Snip) and the translation engine/language lists are expression-bodied,
  localization-dependent. `RaiseSettingsBackedPropertyNotifications` re-raises them on language change; if
  they move, the sub-VM must expose a re-raise method called from that flow.
- **Coupling — prefer thin forwarders** on `MainWindowViewModel` for every side-effect-bearing or cross-VM
  member so external callers + `MainWindowViewModel`-typed XAML need no edits: `BorderColor`, `ThemeColor`,
  `ThemeDeepColor`, `BorderThickness`, `WingScale` (+ `Preview*`), `RunOnStartup`, `AutoCheckUpdates`,
  `SelectedLanguageOption`, `SourceLanguage`, `TargetLanguage`, `SelectedTranslationEngine`, `LlamaModelId`,
  `IsLlamaModelPickerOpen`, `ScrollingCaptureDirection`. Cross-VM readers to keep working:
  `SnipWindowFactory` (`BorderColor`/`BorderThickness`), `SnipWindowViewModel[.Toolbar]`
  (`ThemeColor`/`WingScale`/`SourceLanguage`/`TargetLanguage`/`ScrollingCaptureDirection`),
  `AppBootstrapper` (`RunOnStartup`/`AutoCheckUpdates` → TrayController). Full move+rebind only the
  pure-data, single-consumer **Snip** props (`AutoSave`/`EnableHistory`/`RevealAfterSave`/`SaveDirectory`/
  `HideSnip*`/`AutoPin*`/`DefaultHideSnipToolbar`/`ShowSnipCursor`/`CaptureDelay`/`OcrTextLayout`).
- **Snip props are interleaved** in `Settings.cs` with out-of-scope Record-mode props
  (`DefaultHideRecordToolbar`/`HideRecord*`/`TempDirectory`) — extract selectively, leave those.
- **Per-group blast radius:** General = cross-VM (`SnipWindowFactory`/`SnipWindowViewModel`/`AppBootstrapper`
  /tests) unless forwarders kept; Snip = UI across 3 tabs (Snip/History/Record) + `SnipWindowViewModel`;
  Translation = UI + `Modules.cs` + `SettingsTranslationTab.axaml.cs` + `SnipWindowViewModel`.
- **XAML false positives:** `BorderColor`/`ThemeColor`/`BorderThickness`/`WingScale`/`ScrollingCaptureDirection`
  are ALSO property names on `SnipWindowViewModel` / Floating* VMs — only rewrite XAML whose `x:DataType` is
  `MainWindowViewModel`. Compiled bindings turn any missed rewrite into a build failure (good safety net).

This is pure code-organization (no functional change) but has real persistence/side-effect risk and needs a
runtime settings round-trip to prove — recommend doing it after the F2/G3 runtime checks land, one sub-VM
per commit, forwarders first.

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
