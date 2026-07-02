# CLAUDE.md

Guidance for AI assistants working in this repository.

## Project Overview

**GimmeCapture!!** is a Windows desktop screen-capture tool built with **Avalonia
12** on **.NET 10** (`net10.0-windows`). It combines four core modes:

- **Snip** — capture a screen region, then copy / save / annotate / pin it.
- **Record** — screen recording with system audio, live toolbar, multiple export
  formats (MP4, MKV, GIF, WebM, MOV).
- **Translate** — OCR-assisted region selection with local translation overlays.
- **Pin** — floating always-on-top windows for captured images and video clips.

The app runs **Windows-only** (Win32 APIs, WinForms tray, gdigrab capture). Local AI
features (OCR, background removal, smart selection, translation) are powered by
downloadable model modules.

Current version: see `<Version>` in `src/GimmeCapture/GimmeCapture.csproj` and
`CHANGELOG.md`.

## Tech Stack

- **UI**: Avalonia 12, ReactiveUI.Avalonia, Fluent theme, compiled bindings (`AvaloniaUseCompiledBindingsByDefault=true`)
- **Pattern**: MVVM with ReactiveUI (`ReactiveObject`, `ReactiveCommand`)
- **Media**: FFmpeg.AutoGen 8 (native libav), CliWrap, NAudio
- **AI**: Microsoft.ML.OnnxRuntime.DirectML (U2Net, SAM2, PaddleOCR), LLamaSharp + LLamaSharp.Backend.Cpu (GGUF translation), Microsoft.ML.Tokenizers
- **Imaging**: SkiaSharp
- **Logging**: Serilog (file sink) via `AppLog`
- **Tests**: xUnit, Moq, coverlet; Benchmarks via BenchmarkDotNet
- **Interop**: WinForms enabled (`UseWindowsForms=true`) for tray + native dialogs

## Repository Layout

```
GimmeCapture.slnx              # solution (XML slnx format)
Directory.Build.props          # shared MSBuild props (lock files, RID win-x64)
config.json                    # sample/dev runtime config (NOT copied to output)
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
    Main/Tabs/                 # Settings tabs (General/Snip/Record/AI/Modules/…)
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
    Platforms/Windows/         # Win32 capture, hotkeys, window detection
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
scripts/                       # verify.ps1, check-localization.ps1, ensure-ffmpeg-libs.ps1
.github/workflows/             # ci.yml, release.yml
release.ps1 / release.bat      # release automation (main branch only)
```

## Build, Test & Verify

> Running, testing and publishing require a **Windows** environment with the **.NET 10 SDK**;
> CI runs on `windows-latest`. However, the solution **does compile on Linux/macOS** with
> `dotnet build -p:EnableWindowsTargeting=true` (proven by the `linux-compile-check.yml`
> workflow, which type-checks every `claude/**` push in ~80 s) — so cross-platform sessions
> can get compiler feedback even though they cannot run the app or the tests.

**Before building Release/Publish**, native FFmpeg DLLs must exist under
`src/GimmeCapture/ffmpeg-lib/` or the build fails (guardrail target
`FailIfBundledFfmpegMissing`):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ensure-ffmpeg-libs.ps1
```

Common commands:

```powershell
# Restore (locked mode is enforced in CI — keep packages.lock.json in sync)
dotnet restore GimmeCapture.slnx --runtime win-x64

# Build
dotnet build GimmeCapture.slnx -c Debug

# Run unit tests
dotnet test tests/GimmeCapture.Tests/GimmeCapture.Tests.csproj -c Release

# Full local verification (mirrors CI): localization parity + restore + build +
# test w/ coverage gate (>=25%) + single-file publish smoke test
powershell -ExecutionPolicy Bypass -File scripts/verify.ps1
```

`scripts/verify.ps1` is the canonical "is my change good" check — run it (or at least
build + test) before pushing. Use `-SkipPublish` to skip the publish smoke step.

CI (`.github/workflows/ci.yml`) on every PR/`main` push:
ensure FFmpeg → `check-localization.ps1` → `verify.ps1` → upload coverage.

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
- Avalonia/UI-thread concerns live in `Services/Platforms/Avalonia/`.
- **Do not reference `Application.Current` from ViewModels.** Route UI/window/theme
  operations through `IWindowManager`, `IThemeResourceService`, `IScreenLayoutService`,
  `IWindowLayerService`, etc. `Application.Current` is only acceptable inside the
  Avalonia platform service implementations.

### Settings & storage
- `AppSettings` (model) is persisted as JSON by `AppSettingsService`.
- Storage lives under `%LOCALAPPDATA%\GimmeCapture` with per-install-instance,
  per-version config directories (`AppStoragePaths`). The repo-root `config.json`
  is a dev/sample file and is **not** copied to build output.

### Hotkeys
- Global hotkeys go through `WindowsGlobalHotkeyService` and the routing layer:
  `HotkeyIds`, `HotkeyRouterService`, `HotkeyMappingService`, `HotkeyTagNames`.
  Mapping is auto-generated via expression trees (no reflection). Register new
  hotkeys through these helpers, not ad hoc Win32 calls.

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

### Logging
- Use `AppLog` (Serilog wrapper). `AppLog.Initialize()`/`Shutdown()` bracket the
  app lifetime in `Program.cs`. Log with stable category strings, e.g.
  `AppLog.Error("Program.Startup", ex)`.

## Localization (IMPORTANT)

Three locales are kept in **strict key parity**:
`src/GimmeCapture/Assets/Localization/{en-US,zh-TW,ja-JP}.json`.

- `en-US.json` is the reference key set. `check-localization.ps1` (run in CI and by
  `verify.ps1`) **fails the build** if any locale is missing keys or has extra keys.
- When you add or remove a UI string, update **all three** JSON files with the same
  keys. There is also a `LocalizationParityTests` unit test.
- Strings are consumed via `LocalizationService` (a singleton `ReactiveObject` with
  an indexer for binding) and `EnumToLocalizedConverter`.
- Fonts switch per language (Cinzel / Noto Serif TC / Noto Serif JP).

## Testing Conventions

- xUnit + Moq; one test class per unit, named `<Subject>Tests.cs` in
  `tests/GimmeCapture.Tests/` (ViewModel tests under `ViewModels/`).
- Coverage gate is **25% line coverage** (enforced by `verify.ps1`). Add tests for
  new service/VM logic; favor testing services and orchestration that don't require
  a live UI thread. `ReactiveUITestInitializer` sets up ReactiveUI scheduling.
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
- **Don't commit native FFmpeg DLLs** — they are produced by script and gitignored.
- **Can't run/test on non-Windows, but CAN compile**: on non-Windows environments use
  `dotnet build GimmeCapture.slnx -p:EnableWindowsTargeting=true` (or rely on the
  `Linux Compile Check` workflow that runs on every `claude/**` push) for compiler
  feedback. Tests, `verify.ps1`, and running the app still require Windows; state that
  limitation rather than claiming a green build from a compile alone.
- **Git workflow**: develop on the assigned feature branch, commit with clear
  messages, push with `git push -u origin <branch>`. Do **not** open a PR unless
  explicitly asked.
- Useful background reading: `docs/ARCHITECTURE_REFACTOR_ROADMAP.md` (hotspots and
  target architecture) and `docs/REFACTOR_PLAN.md` (completed/ongoing refactors).
