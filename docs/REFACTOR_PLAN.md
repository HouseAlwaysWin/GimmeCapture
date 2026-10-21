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
   pixel work off the UI thread (#174)
2. ✅ Start-up and size: Windows ships as a folder (#175), WPF and unused packages dropped (#176), CJK font chain per
   language (#177), ReadyToRun (#178, #180)
3. ✅ Cleanup: dead code (#179), one confirmation dialog and progress bar (#181), failures and traces reaching the
   log (#182, #183), unused locale keys failing the build (#185)

### Next
- `SnipWindow.ViewModelWiring`'s `OnDataContextChanged` (~700 lines of independent assignments) into focused
  methods
- One shared core for the libav audio transcoders (AAC and Opus are ~70% the same) and the three `RunTranscode`s
- The snip view model's `_mainVm` service locator → injected settings snapshot, state interfaces and services;
  `ILocalizationService` for the ~300 static `LocalizationService.Instance` uses; one home for P/Invoke structs
- The snip overlay's own keyboard hook onto the hotkey thread (A2)

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
