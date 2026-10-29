# CLAUDE.md

Guidance for AI assistants working in this repository.

## Project Overview

**GimmeCapture!!** is a desktop screen-capture tool built with **Avalonia 12** on
**.NET 10**, multi-targeting `net10.0-windows10.0.19041.0` (full feature set) and
`net10.0` (Linux/X11 subset). It combines five core modes:

- **Snip** — capture a screen region, then copy / save / annotate / pin it.
- **Record** — screen recording with system audio, live toolbar, multiple export
  formats (MP4, MKV, GIF, WebM, MOV).
- **Translate** — OCR-assisted region selection with local translation overlays.
- **Pin** — floating always-on-top windows for captured images and video clips.
- **Compress** — HandBrake-lite video re-encode: batch queue, H.264/H.265/AV1,
  two-pass target size, and an editor (trim/speed/crop/rotate/annotations).

**Windows is the primary platform** (Win32 APIs, WGC per-window recording, WASAPI
audio). Since v0.51 a **Linux/X11 build ships from the `net10.0` head** with the
whole capture/record/translate/pin/compress feature set ported — libX11 capture,
`XGrabKey` hotkeys, `x11grab` recording, PulseAudio system/mic audio, V4L2 webcam.
Only **per-window (WGC) recording stays Windows-exclusive**. See
`docs/LINUX_PORT_FEASIBILITY.md` for the port history. Local AI features (OCR,
background removal, smart selection, translation) are powered by downloadable model
modules.

Current version: see `<Version>` in `src/GimmeCapture/GimmeCapture.csproj` and
`CHANGELOG.md`.

## Tech Stack

- **UI**: Avalonia 12, ReactiveUI.Avalonia, Fluent theme, compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`)
- **Pattern**: MVVM with ReactiveUI (`ReactiveObject`, `ReactiveCommand`)
- **Media**: FFmpeg.AutoGen 8 (native libav), NAudio
- **AI**: Microsoft.ML.OnnxRuntime.DirectML (U2Net and SAM2 on DirectML, PaddleOCR on its CPU provider), LLamaSharp + LLamaSharp.Backend.Cpu (GGUF translation)
- **Imaging**: SkiaSharp
- **Logging**: Serilog (file sink) via `AppLog`
- **Tests**: xUnit, Moq, coverlet; Benchmarks via BenchmarkDotNet
- **Interop**: WinForms enabled (`UseWindowsForms=true`) for tray + native dialogs

## Repository Layout

```
GimmeCapture.slnx              # solution (XML slnx format)
Directory.Build.props          # shared MSBuild props (lock files, RID win-x64)
src/GimmeCapture/
  Program.cs                   # entry point; AppLog init, Avalonia bootstrap
  App.axaml(.cs)               # Avalonia Application, lifecycle
  Composition/                 # composition root — service wiring & factories
    AppBootstrapper.cs         # owns shared services, lazy graph construction
    MainWindowViewModelDependencies*.cs
    RuntimeServiceFactory.cs
  Models/                      # POCOs, enums, settings (AppSettings, CaptureMode…)
  ViewModels/
    Main/                      # MainWindowViewModel.*, SnipWindowViewModel.* (partials)
    Floating/                  # pin window VMs (image/video/translation)
    Shared/                    # cross-cutting VM helpers
  Views/
    Main/                      # MainWindow, SnipWindow (partial code-behind)
    Main/Tabs/                 # Settings tabs (General/Snip/Record/Translation/Modules/…)
    Floating/                  # floating image/video/translation windows
  Services/
    Abstractions/              # I*-prefixed service interfaces
    Core/
      AI/                      # OCR/SAM2/background-removal/model catalog & install
      Infrastructure/          # settings, hotkeys, storage paths, logging, update
      Media/                   # RecordingService.* + NativeFFmpeg/ libav wrappers
      Rendering/               # annotation rendering
      Interaction/             # pointer/hit-test helpers
      Interfaces/              # IOCREngine, ITranslationEngine
    Translation/               # TranslationService.*, LlamaSharp engine, cache
    OCR/                       # PaddleOCR engine + factory
    Platforms/Windows/         # Win32 capture, hotkeys, window detection (Windows TFM only)
    Platforms/Linux/           # X11 capture, XGrabKey hotkeys, window shape (net10.0 head)
    Platforms/Avalonia/        # Avalonia-backed UI services (window mgr, theme…)
    Interop/                   # Win32Helpers
  Converters/                  # Avalonia value converters
  Styles/                      # GimmeTheme.axaml (red/black BABYMETAL aesthetic)
  Assets/                      # fonts, icons, Localization/ JSON
  ffmpeg-lib/                  # native FFmpeg DLLs (populated by script, gitignored)
tests/
  GimmeCapture.Tests/          # xUnit unit tests
  GimmeCapture.Benchmarks/     # BenchmarkDotNet
docs/                          # architecture roadmap, refactor plan, release catalog
scripts/                       # verify.ps1, check-localization.ps1, ensure-ffmpeg-libs.ps1,
                               # ensure-ffmpeg-libs-linux.sh, test-compress.ps1, build-installer.ps1
.github/workflows/             # ci.yml, linux.yml, release.yml, codeql.yml
.github/dependabot.yml         # weekly GitHub Actions version bumps (not NuGet: see the file)
release.ps1 / release.bat      # release automation (main branch only)
```

## Build, Test & Verify

> **`verify.ps1`, the Windows-only tests and the full feature set still require Windows**;
> CI runs them on `windows-latest`. The Tests project multi-targets
> `net10.0-windows10.0.19041.0;net10.0`, with the Windows-only test files fenced out of the
> `net10.0` head in its csproj. The solution **compiles on Linux/macOS** with
> `dotnet build -p:EnableWindowsTargeting=true` — enforced by `linux.yml` on every push to
> `main` or `claude/**` and every other PR, building BOTH TFMs and then **executing the
> cross-platform suite against the `net10.0` head on ubuntu**. The `net10.0` head also *runs* on a Linux X11 desktop (full feature set minus
> WGC per-window recording).

**Before building Release/Publish**, native FFmpeg DLLs must exist under
`src/GimmeCapture/ffmpeg-lib/` or the build fails (guardrail target
`FailIfBundledFfmpegMissing`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
```

That script and its Linux twin `ensure-ffmpeg-libs-linux.sh` pin **one exact FFmpeg build**
(n8.1.3, 2026-09-22) by URL and SHA-256, downloading it from this repository's
`deps-ffmpeg-n8.1-20260922` prerelease — a byte-identical mirror of BtbN's build, because
BtbN's own `latest` asset keeps changing and its dated builds get deleted. To move FFmpeg,
mirror the new build as another prerelease the same way, then update the URL and hash in
**both** scripts together with the exact version in `THIRD-PARTY-NOTICES.md`.

Common commands:

```powershell
# Restore (locked mode is enforced in CI — keep packages.lock.json in sync).
# No --runtime flag: it overrides the projects' RuntimeIdentifiers (win-x64, plus
# linux-x64 on the net10.0 head) and desyncs the lock files.
dotnet restore GimmeCapture.slnx

# Build
dotnet build GimmeCapture.slnx -c Debug

# Run unit tests
dotnet test tests/GimmeCapture.Tests/GimmeCapture.Tests.csproj -c Release

# Full local verification (mirrors CI): localization parity + restore + build +
# test w/ coverage gate (>=30%) + publish smoke test of both release shapes
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1

# Publish a release shape (Properties/PublishProfiles): win-x64 is a self-contained folder,
# linux-x64 a self-contained single file (the Linux updater swaps that one file)
dotnet publish src/GimmeCapture/GimmeCapture.csproj -c Release -f net10.0-windows10.0.19041.0 -p:PublishProfile=win-x64 -o publish
```

`scripts/verify.ps1` is the canonical "is my change good" check — run it (or at least
build + test) before pushing. Use `-SkipPublish` to skip the publish smoke step.

CI on every PR/`main` push: `ci.yml` (windows-latest) runs ensure-FFmpeg → `verify.ps1`
(which includes the localization check) → upload coverage (`verify.ps1` leaves the report at
`artifacts/coverage/`). PRs run `verify.ps1 -SkipPublish`. `codeql.yml` analyses the C# code
and the workflow files (build-mode none) on PRs, `main` pushes and weekly.

`linux.yml` (ubuntu-latest) runs on every push to `main` or `claude/**` and on PRs from other
branches (a `claude/**` PR shows its push run instead of running twice). In one job it type-checks
both TFMs and runs the suite against the `net10.0` head: it provisions the FFmpeg `.so` bundle and
restores unlocked with `--force-evaluate`. On `claude/**` pushes only, it commits refreshed
`packages.lock.json` back to the branch — so a `claude/**` branch can gain a
`chore(ci): refresh packages.lock.json` commit, and you should pull before pushing again.

## Architecture & Conventions

### MVVM + ReactiveUI
- ViewModels derive from `ViewModelBase` / `ReactiveObject`; use
  `RaiseAndSetIfChanged` for properties and `ReactiveCommand` for actions.
- Compiled bindings are on by default — bind against typed `DataContext`s.
- Large ViewModels are split into **partial classes by concern**, e.g.
  `SnipWindowViewModel.Capture.cs`, `.Recording.cs`, `.Selection.State.cs`,
  `.ModeRouting.cs`. Add new behavior to the matching partial (or a new partial)
  rather than growing one file.

### Composition root
- `Composition/AppBootstrapper.cs` is the single ownership point for shared
  services; it builds the object graph lazily (`Lazy<T>`) and injects dependencies
  via constructors. There is **no DI container** — wiring is manual through
  factories (`RuntimeServiceFactory`, `MainWindowViewModelDependenciesFactory`,
  `SnipWindowFactory`).
- Prefer injecting an interface from `Services/Abstractions/` over `new`-ing a
  concrete service inside a ViewModel/View. This is an ongoing refactor goal —
  see `docs/REFACTOR_PLAN.md` and `docs/ARCHITECTURE_REFACTOR_ROADMAP.md`.

### Platform isolation
- Win32-specific code lives in `Services/Platforms/Windows/` and `Services/Interop/`.
- A P/Invoke function, struct or callback type used in more than one place is declared once: `User32`, `Kernel32`
  and `Gdi32` in `Services/Interop/`, `LibX11` in `Services/Platforms/Linux/`. One used in a single place stays
  next to it. Bind the Unicode (`W`) entry points: an ANSI window subclass turned typed emoji into '?'. To let clicks
  through parts of a window, use `Win32HitTestPassThrough`.
- Avalonia/UI-thread concerns live in `Services/Platforms/Avalonia/`.
- **Do not reference `Application.Current` from ViewModels.** Route UI/window/theme
  operations through `IWindowManager`, `IThemeResourceService`, `IScreenLayoutService`,
  `IWindowLayerService`, etc. `Application.Current` is only acceptable inside the
  Avalonia platform service implementations.

### Settings & storage
- `AppSettings` (model) is persisted as JSON by `AppSettingsService`.
- Storage lives under `%LOCALAPPDATA%\GimmeCapture` with per-install-instance,
  per-version config directories (`AppStoragePaths`). A first run writes a fresh
  config there; the repository carries no sample.

### Hotkeys
- Global hotkeys go through `WindowsGlobalHotkeyService` and the routing layer:
  `HotkeyIds`, `HotkeyRouterService`, `HotkeyMappingService`, `HotkeyTagNames`.
  Mapping is auto-generated via expression trees (no reflection). Register new
  hotkeys through these helpers, not ad hoc Win32 calls.
- `WindowsGlobalHotkeyService` keeps its keyboard hook, message window and raw-input sink on its own
  message-loop thread (`Win32HotkeyThread`) and posts presses to the UI thread. Windows holds every key typed
  anywhere until that hook returns, and silently removes a hook that is too slow, so nothing on that thread may
  wait for the UI thread. The match logic is the pure `LowLevelHotkeyMatcher`.
- The snip overlay has a low-level hook of its own (`SnipWindow.Win32.cs`) on a second `Win32HotkeyThread`. Its
  routing (`HandleGlobalKeyboardEvent`) reads the view model, so it runs on the UI thread. `SnipOverlayKeyFilter`
  lets every key that routing cannot act on straight through, and the rest wait for the UI thread through
  `TimedUiRoute` (100 ms, 50 ms more once it has started). Teach the routing a new key and the filter must list it,
  or the key stops working while the overlay is open; `SnipOverlayKeyFilterTests` pin the defaults.

### Media / FFmpeg
- Recording is split across `RecordingService.*` partials and `NativeFFmpeg/`
  libav wrappers (`LibavMuxer`, `Libav*Transcoder`, `LibavGdigrabMkvSession`…).
- Native FFmpeg is resolved/loaded via `FFmpegRuntime` from the bundled
  `ffmpeg-lib/` folder next to the executable.

### AI modules
- AI models are **downloaded on demand** from the Modules tab (not bundled).
- Catalog/installer/runtime/queue concerns are separated:
  `AIModelCatalog`, `AIResourceInstaller`/`ModuleInstallCoordinator`,
  `*RuntimeService`, `ResourceQueueService`. ONNX provider config is centralized
  in `OnnxProviderConfigurator`.

### UI thread
- An `async` method runs on its caller's thread until its first real yield, and `ConfigureAwait(false)` does not
  change that when the awaited task has already completed. A service method the UI awaits must leave the caller's
  thread before blocking work — native/COM initialization, model loads, large file I/O — with `Task.Run` or
  `ConfigureAwaitOptions.ForceYielding`. Missing this caused the OCR-session, history-thumbnail and recording-start
  freezes. `UiStall` warnings in the log (any freeze over ~0.35 s, with its length) are where these show up.
- Every settings tab except General is built the first time it is shown (`EnsureLazyTabContent` in
  `MainWindow.Tabs.cs`), and set-up only one tab needs belongs there too (`EnsureCompressInitializedAsync`).
  `Startup.Timings` in the log breaks a launch down by stage.

### Accessibility
- A screen reader names a button after its content, which for an icon is the icon's type name.
  Give every button text, a `ToolTip.Tip` (a global `:is(Button)` style in `GimmeTheme.axaml`
  turns it into `AutomationProperties.Name`) or an explicit `AutomationProperties.Name`.
- A custom button `ControlTheme` replaces Fluent's focus visual: give it
  `<Setter Property="FocusAdorner" Value="{StaticResource GimmeFocusAdorner}"/>`.
  `AccessibilityMarkupTests` enforce both rules on the `.axaml` sources.

### Logging
- Use `AppLog` (Serilog wrapper). `AppLog.Initialize()`/`Shutdown()` bracket the
  app lifetime in `Program.cs`. Log with stable category strings, e.g.
  `AppLog.Error("Program.Startup", ex)`.
- `Debug.WriteLine` does not exist in a release build, so a failure reported only there is invisible.
  Use `AppLog.Warning(operation, ex)` for a failure the user would notice, and `AppLog.Debug` for an
  expected one (no CUDA, cleanup a later sweep retries), written only when `GIMMECAPTURE_DEBUG_LOG=1`.
  The exception overloads record type, HResult and stack, never the message.
- Traces go through `AppLog.Debug(operation, $"...")` too, not `Debug.WriteLine`. With debug
  logging off, an interpolated message is neither evaluated nor formatted, so it is fine on hot paths.

## Localization (IMPORTANT)

Three locales are kept in **strict key parity**:
`src/GimmeCapture/Assets/Localization/{en-US,zh-TW,ja-JP}.json`.

- `en-US.json` is the reference key set. `check-localization.ps1` (run in CI and by
  `verify.ps1`) **fails the build** if any locale is missing keys or has extra keys, or if a key
  is defined that nothing uses. A key built at run time must use `Instance[$"Prefix{...}"]` (or
  `Localization[$"Prefix{...}"]` in a view model) or an `EnumToLocalizedConverter` `ConverterParameter`,
  which the check treats as prefixes.
- When you add or remove a UI string, update **all three** JSON files with the same
  keys. There is also a `LocalizationParityTests` unit test.
- Parity only compares keys, so `HardcodedUiTextTests` also fails the build on user-visible
  literals: text attributes in `.axaml` (`Text`, `Content`, `Title`, `ToolTip.Tip`, …) and
  Chinese/Japanese string literals in C#. Bind a key instead; the few deliberate literals
  (product name, glyph labels, model-output patterns) are allow-listed there with the reason.
- Strings are consumed via `LocalizationService` (a singleton `ReactiveObject` with
  an indexer for binding) and `EnumToLocalizedConverter`. View models read them through
  `ViewModelBase.Localization` (an `ILocalizationService`, the singleton unless a test hands them
  a stand-in), not `LocalizationService.Instance`; only nested option types and views use the singleton.
- Fonts switch per language (Cinzel / Noto Serif TC / Noto Serif JP).

## Testing Conventions

- xUnit + Moq; one test class per unit, named `<Subject>Tests.cs` in
  `tests/GimmeCapture.Tests/` (ViewModel tests under `ViewModels/`).
- Coverage gate is **30% line coverage** (enforced by `verify.ps1`). It leaves out the code
  the XAML compiler generates from `.axaml` files (`tests/GimmeCapture.Tests/coverage.runsettings`),
  which no unit test reaches. Add tests for
  new service/VM logic; favor testing services and orchestration that don't require
  a live UI thread. `ReactiveUITestInitializer` sets up ReactiveUI scheduling.
- A test that cannot run everywhere says so in its attribute, so it is reported as **skipped**:
  `[WindowsFact]` for Windows-only behaviour, `[OptInFact("ENV_VAR", "OTHER=1")]` for tests that
  need a local clip or a probe switch. A test that only needs somewhere to write uses its own
  temporary folder and runs everywhere — for settings that means `new AppSettingsService(tempDir)`,
  which then reads and writes only there; the parameterless constructor is the app's real storage
  under `%LOCALAPPDATA%\GimmeCapture`. Never gate with an early `return` — that
  counts as a pass for something that was never checked.
- Benchmarks live in `tests/GimmeCapture.Benchmarks/` (run manually, not in CI).

## Releases

- `release.ps1` (wrapped by `release.bat`) automates tagging/release and **must be
  run from `main` with a clean working tree**; version must be `vMAJOR.MINOR.PATCH`.
- Keep `<Version>` in the csproj, `CHANGELOG.md`, and `docs/catalog/releases.md`
  consistent when cutting a release.
- Do **not** run the release flow from feature branches.

## Working Agreements for AI Assistants

- **Match existing style**: partial-class-by-concern, interface-first services,
  no `Application.Current` in VMs, ReactiveUI patterns.
- **Localization parity is non-negotiable** — touch one locale JSON, touch all three.
- **Keep package lock files in sync** (`packages.lock.json`); CI restores in locked
  mode. If you change `PackageReference`s, restore so the lock file updates.
  NuGet updates are manual: Dependabot cannot update these multi-target projects, and the
  lock files it wrote lost the `net10.0` head (`.github/dependabot.yml` says why).
- **Don't commit native FFmpeg DLLs** — they are produced by script and gitignored.
- **Non-Windows environments: compile and the cross-platform tests yes, `verify.ps1` no.**
  Use `dotnet build GimmeCapture.slnx -p:EnableWindowsTargeting=true` for compiler feedback
  on both TFMs, and
  `dotnet test tests/GimmeCapture.Tests/GimmeCapture.Tests.csproj -f net10.0 -p:EnableWindowsTargeting=true`
  for the tests that are not Windows-fenced — or rely on the `Linux` workflow, which runs on
  every push to `main`, though that only reports after the code has landed. `verify.ps1`
  (localization parity + coverage gate + publish smoke) and the Windows-only tests still
  require Windows; the app itself runs on a Linux X11 desktop from the `net10.0` head
  (everything except WGC per-window recording). Say which of these actually ran rather than
  claiming a green build from a compile alone.
- **Git workflow — commit straight to `main`.** No feature branch, no PR: only the owner and
  AI assistants touch this repository, so a PR is a record with no reviewer. Commit on `main`
  with a clear message and `git push`. Do **not** create a `claude/**` branch, open a PR or
  add branch protection unless explicitly asked (`main` is deliberately unprotected and
  `allow_auto_merge` is off).
- **Because of that, `verify.ps1` before you push is not optional.** `ci.yml` runs on the
  `main` push, which is *after* the code is on the branch everything builds from — CI is an
  alarm, not a gate, and a break lands and has to be reverted instead of being blocked. So on
  Windows run `scripts/verify.ps1` (or at least `dotnet build` plus
  `dotnet test tests/GimmeCapture.Tests/GimmeCapture.Tests.csproj -f net10.0-windows10.0.19041.0`
  — the `net10.0` head skips the Windows-fenced tests) and push only once it is clean. A test
  that times a call which blocks on purpose runs it with `OwnThread.Run` and times its waits with
  `OwnThread.Delay`, not `Task.Run`/`Task.Delay`: the rest of the suite keeps the thread pool
  busy, which is how `ResourceUseGateTests` came to time out under load. Don't "fix" such a test
  by loosening an assertion.
- Useful background reading: `docs/ARCHITECTURE_REFACTOR_ROADMAP.md` (hotspots and
  target architecture) and `docs/REFACTOR_PLAN.md` (completed/ongoing refactors).
