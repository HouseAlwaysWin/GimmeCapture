# GimmeCapture Refactor Plan

## Goal
Improve maintainability and reduce regressions by splitting large responsibilities, centralizing routing/state logic, and removing brittle duplication.

## Priority Roadmap

### Phase 1 - Foundation (low risk, high leverage) ✅
1. ✅ Centralize hotkey IDs and routing (`HotkeyIds`, `HotkeyRouterService`, `HotkeyMappingService`, `HotkeyTagNames`)
2. ✅ Replace reflection-based hotkey mapping with expression-tree auto-mapping
3. ✅ Consolidate settings mapping flow + startup tag validation

### Phase 2 - Service orchestration ✅
1. ✅ Split Translation/OCR orchestration from API client and cache (`IOllamaApiClient`, `ITranslationCache`, `InMemoryTranslationCache`)
2. ✅ Centralize cancellation/timeout policy (`ITranslationExecutionPolicy`, `TranslationExecutionPolicy`)
3. ✅ Standardize command execution/error handling helpers (`TranslationExecutionHelper`)

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

## Remaining `Application.Current` Usage
In sanctioned locations (platform impls / composition root / view code-behind):
- `Services/Platforms/Avalonia/AvaloniaWindowManager.cs`
- `Services/Platforms/Avalonia/AvaloniaWindowLayerService.cs`
- `Services/Platforms/Avalonia/AvaloniaThemeResourceService.cs`
- `Services/Core/Infrastructure/ClipboardService.cs`
- `Composition/AppBootstrapper.cs` (composition root — acceptable)
- `Views/Main/SnipWindow.ViewModelWiring.cs` (view code-behind — acceptable)

⚠️ Outside the sanctioned boundary (should be routed through a platform service):
- `Services/Core/Infrastructure/TranslationResultLayerManager.cs` — a Core/Infrastructure
  service, not an Avalonia platform impl; migrate its `Application.Current` access behind
  `IWindowManager`/`IWindowLayerService`.

## Risk Controls
- Keep behavior identical per step
- Compile after each change
- Add debug logging at routing boundaries while migrating
- Do not combine pointer/selection/translation refactors in one PR
