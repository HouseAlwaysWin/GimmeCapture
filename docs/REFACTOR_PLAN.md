# GimmeCapture Refactor Plan

## Goal
Improve maintainability and reduce regressions by splitting large responsibilities, centralizing routing/state logic, and removing brittle duplication.

## Priority Roadmap

### Phase 1 - Foundation (low risk, high leverage) ✅
1. ✅ Centralize hotkey IDs and routing (`HotkeyIds`, `HotkeyRouterService`, `HotkeyMappingService`, `HotkeyTagNames`)
2. ✅ Replace reflection-based hotkey mapping with expression-tree auto-mapping
3. ✅ Consolidate settings mapping flow + startup tag validation

### Phase 2 - Service orchestration ✅
1. ✅ Split Translation/OCR orchestration from API client and cache (`ITranslationCache`, `InMemoryTranslationCache`).
   The Ollama client went with the Ollama engine; translation is LlamaSharp only.
2. ✅ Centralize cancellation/timeout policy. `ITranslationExecutionPolicy` / `TranslationExecutionPolicy` no longer
   exist.
3. ✅ Standardize command execution/error handling helpers. `TranslationExecutionHelper` ended up called only by its
   own tests and was removed (#179).

### Phase 3 - Snip module decomposition ✅
1. ✅ Split `SnipWindowViewModel.Actions` by mode → `ModeRouting`, `Recording`, `Capture`
2. ✅ Split `SnipWindowViewModel.Selection` → `Selection.State`, `Selection.Translation`, `Selection.AIScan`
3. ✅ Extract pointer interaction handlers → `SnipWindow.Pointer.Translation`, `SnipWindow.Pointer.Annotation`

### Phase 4 - UI decoupling ✅
1. ✅ `IWindowManager` / `AvaloniaWindowManager` — removed `Application.Current` from ViewModels
2. ✅ `IThemeResourceService` / `AvaloniaThemeResourceService` — theme color updates
3. ✅ `IScreenLayoutService` / `AvaloniaScreenLayoutService` — screen layout calculations
4. ✅ `IWindowLayerService` / `AvaloniaWindowLayerService` — window topmost management
5. ✅ `IDownloadWindowService` / `AvaloniaDownloadWindowService` — download window lifecycle

### Phase 5 - October 2026 optimisation pass ✅
1. ✅ UI stalls and hotkeys: the keyboard hook on its own thread (#172), start-up and the History tab (#173), pin
   pixel work off the UI thread (#174), the snip overlay's keyboard hook on a thread of its own (A2)
2. ✅ Start-up and size: Windows ships as a folder (#175), WPF and unused packages dropped (#176), CJK font chain per
   language (#177), ReadyToRun (#178, #180)
3. ✅ Cleanup: dead code (#179), one confirmation dialog and progress bar (#181), failures and traces reaching the
   log (#182, #183), repository hygiene (#184), unused locale keys failing the build (#185), the overlay's 713-line
   `OnDataContextChanged` split into seven wiring methods (#186), one encode core (`LibavAudioEncoder`) behind the
   AAC, MP3 and Opus transcoders (#195), one encoder-and-file writer (`RecordingSegmentWriter`) behind the gdigrab,
   WGC and WGC-composite recording sessions, under tests that record with each (#197, #198), one start, stop and
   startup-gate shell for them (`LibavRecordingSession`, #199), one frame conversion and timestamp clock for
   gdigrab's single-threaded and pipelined paths (#200), and one home for every P/Invoke declaration used in more
   than one place (`User32`, `Kernel32`, `Gdi32`, `LibX11`) with one click-through subclass for the overlay and pins
   (`Win32HitTestPassThrough`)
4. ✅ CI and tests: one Linux workflow (#187), Linux and Windows release builds side by side (#188), release and
   hung-test guards (#189), coverage without `.axaml` at a 30% gate and the audio and Linux tests in CI (#190), NuGet
   updates by hand after Dependabot proved unable to handle the multi-target projects (#191, #193, #194)

### Next
- The snip view model's `_mainVm` service locator (212 uses of 110 members). Done: its calls back into the main
  window go through `ISnipWindowHost` and its services come in as `SnipWindowServices` (#202); it reads its own 59
  hotkeys from the settings model (#203), and its other settings, six of them written back, through
  `ISnipOverlaySettings`, with the main window's progress on the host. Left: the overlay view's own reach through
  `MainVm`, for settings and to build pinned images; then dropping the `MainWindowViewModel` parameter
- `ILocalizationService` for the ~300 static `LocalizationService.Instance` uses

## Remaining `Application.Current` Usage
In sanctioned locations (platform impls / composition root / view code-behind):
- `Services/Platforms/Avalonia/AvaloniaWindowManager.cs`
- `Services/Platforms/Avalonia/AvaloniaWindowLayerService.cs`
- `Services/Platforms/Avalonia/AvaloniaThemeResourceService.cs`
- `Services/Core/Infrastructure/ClipboardService.cs`
- `Composition/AppBootstrapper.cs` (composition root — acceptable)
- `Views/Main/SnipWindow.ViewModelWiring.cs` (view code-behind — acceptable)

- `Services/Platforms/Avalonia/TranslationResultLayerManager.cs` — relocated here from
  Core/Infrastructure (it creates/queries Avalonia windows, so the platform layer is its honest
  home). Follow-up: convert the static class to an injected service behind an abstraction so
  ViewModels stop calling it directly.

## Risk Controls
- Keep behavior identical per step
- Compile after each change
- Add debug logging at routing boundaries while migrating
- Do not combine pointer/selection/translation refactors in one PR
