# GimmeCapture Refactor Plan

## Goal
Improve maintainability and reduce regressions by splitting large responsibilities, centralizing routing/state logic, and removing brittle duplication.

## Priority Roadmap

### Phase 1 - Foundation (low risk, high leverage)
1. Centralize hotkey IDs and routing
2. Replace reflection-based hotkey mapping with explicit mapping
3. Consolidate settings mapping flow

### Phase 2 - Service orchestration
1. Split Translation/OCR orchestration from API client and cache
2. Centralize cancellation/timeout policy
3. Standardize command execution/error handling helpers

### Phase 3 - Snip module decomposition
1. Split `SnipWindowViewModel.Actions` by mode (Screenshot/Recording/Translation)
2. Split `SnipWindowViewModel.Selection` into state manager + selection service
3. Extract pointer interaction handlers from `SnipWindow.Pointer`

### Phase 4 - UI decoupling
1. Introduce `IWindowManager` and move `Application.Current` access out of ViewModels
2. Move screen/layout calculations from view code-behind into services

## Top Hotspots
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Actions.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.cs`
- `src/GimmeCapture/Views/Main/SnipWindow.Pointer.cs`
- `src/GimmeCapture/Views/Main/SnipWindow.axaml.cs`
- `src/GimmeCapture/Services/Core/TranslationService.cs`

## First Increment (started)
- Add shared hotkey constants and central router service
- Stop using mixed ID spaces for global routing
- Route existing Snip window requests through mode-based handler instead of abusing global ID handler

## Risk Controls
- Keep behavior identical per step
- Compile after each change
- Add debug logging at routing boundaries while migrating
- Do not combine pointer/selection/translation refactors in one PR
