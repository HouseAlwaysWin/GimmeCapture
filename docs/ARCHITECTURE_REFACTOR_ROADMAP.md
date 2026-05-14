# GimmeCapture Architecture Refactor Roadmap

Snapshot date: 2026-05-14

## Purpose
This document captures the current architecture refactor recommendations for the repository so the next implementation pass can start from a concrete, code-aware roadmap instead of re-analyzing the codebase.

This roadmap is based on the current state of:

- `src/GimmeCapture/App.axaml.cs`
- `src/GimmeCapture/Views/Main/MainWindow.axaml.cs`
- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel*.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel*.cs`
- `src/GimmeCapture/Services/Core/AI/AIResourceService.cs`
- `src/GimmeCapture/Services/Translation/TranslationService.cs`
- `src/GimmeCapture/Services/Core/Media/ResourceQueueService.cs`
- `src/GimmeCapture/Services/Platforms/Windows/WindowsScreenCaptureService.cs`

## Executive Summary
The main architecture issue is not one isolated large file. The deeper problem is that dependency wiring, UI state, background orchestration, platform concerns, settings persistence, and AI resource lifecycle are still spread across ViewModels, Views, and concrete services without a single stable composition boundary.

The highest-value refactors are:

1. Centralize object creation and lifetime management.
2. Pull Snip and Translation session orchestration out of `SnipWindowViewModel`.
3. Separate settings persistence and hotkey registration from UI-facing settings properties.
4. Split AI resource management into catalog, installer, runtime, and queue concerns.
5. Move tray/window lifecycle orchestration out of `App` and `MainWindow` code-behind into app-shell services.

## Current Hotspots
The following files are the highest-risk maintenance hotspots by responsibility density and size:

- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel.Settings.cs` at about 1011 lines
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.State.cs` at about 992 lines
- `src/GimmeCapture/Services/Core/AI/AIResourceService.cs` at about 855 lines
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.ModeRouting.cs` at about 769 lines
- `src/GimmeCapture/Services/Platforms/Windows/WindowsScreenCaptureService.cs` at about 732 lines
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.cs` at about 637 lines
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.Translation.cs` at about 583 lines
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.AIScan.cs` at about 551 lines

Large files alone are not the problem. These files are risky because they combine multiple architectural layers in one place.

## Refactor Priorities

### Priority 1: Composition Root and Dependency Wiring
#### Problem
Object creation is currently scattered:

- `App` creates `MainWindow` and `MainWindowViewModel` directly.
- `MainWindowViewModel` creates settings, hotkey, update, recording, AI path, AI downloader, and AI resource services directly.
- `MainWindow` creates window services directly.
- `SnipWindowViewModel` creates `WindowsScreenCaptureService` directly.

This causes:

- unclear ownership of service lifetimes
- difficult testing because most collaborators are concrete classes
- accidental duplication of shared service state
- cross-window behavior depending on hidden object graphs

#### Target State
Introduce one application bootstrapper or service registry that owns all shared services and factories.

Suggested extractions:

- `AppBootstrapper`
- `IServiceRegistry` or `ServiceProvider`
- `ISnipWindowFactory`
- `IMainWindowFactory`

#### First Concrete Steps

1. Create a bootstrapper that constructs shared services once.
2. Inject `MainWindowViewModel` dependencies through the constructor.
3. Inject `IScreenCaptureService` into `SnipWindowViewModel`.
4. Replace view-level `new` calls with factory calls.

#### Main Files Involved

- `src/GimmeCapture/App.axaml.cs`
- `src/GimmeCapture/Views/Main/MainWindow.axaml.cs`
- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.cs`
- `src/GimmeCapture/Services/Platforms/Windows/WindowsScreenCaptureService.cs`

### Priority 2: Snip and Translation Session Decomposition
#### Problem
`SnipWindowViewModel` currently owns:

- selection state machine
- mode routing
- translation warmup and cancellation
- OCR auto-detect loop
- AI scan lifecycle
- capture flow
- toolbar placement behavior
- recording UI coordination

It also creates `TranslationService` internally in multiple paths and manually syncs shared settings before translation work.

This makes the window ViewModel a runtime controller instead of a presentation model.

#### Target State
Keep `SnipWindowViewModel` as UI state and command surface only. Move orchestration into dedicated session services.

Suggested extractions:

- `SnipSessionController`
- `SnipStateMachine`
- `TranslationSessionService`
- `TranslationSelectionMonitor`
- `AIScanSessionService`

#### First Concrete Steps

1. Replace `AutoActionMode` magic integers with a typed enum.
2. Extract translation warmup, engine readiness, and cancellation into a dedicated translation session service.
3. Extract auto-detect OCR loop into its own service.
4. Leave only property updates and command binding in the ViewModel.

#### Main Files Involved

- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.ModeRouting.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.State.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.Translation.cs`
- `src/GimmeCapture/ViewModels/Main/SnipWindowViewModel.Selection.AIScan.cs`

### Priority 3: Settings and Hotkey Architecture
#### Problem
`MainWindowViewModel.Settings.cs` mixes:

- UI-facing properties
- hotkey registration side effects
- persistence side effects
- Windows startup registration side effects
- language updates
- AI settings
- recording settings

`LoadSettingsAsync`, `SaveSettingsAsync`, and `AppSettingsService.UpdateSettings` all manually map the same data in different places, which creates drift risk.

#### Target State
Split settings into independent sections and move side effects behind coordinators.

Suggested extractions:

- `GeneralSettingsViewModel`
- `CaptureSettingsViewModel`
- `RecordingSettingsCoordinator`
- `TranslationSettingsViewModel`
- `HotkeyProfileService`
- `SettingsPersistenceService`
- `StartupRegistrationService`

#### First Concrete Steps

1. Separate persistence from UI setters.
2. Stop calling `SaveSettingsAsync` directly from many property setters.
3. Move hotkey registration into a dedicated hotkey coordinator that reacts to settings changes.
4. Reduce manual copy logic between `MainWindowViewModel` and `AppSettingsService`.

#### Main Files Involved

- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel.Settings.cs`
- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel.cs`
- `src/GimmeCapture/Services/Core/Infrastructure/AppSettingsService.cs`
- `src/GimmeCapture/Services/Core/Infrastructure/HotkeyMappingService.cs`
- `src/GimmeCapture/Services/Core/Infrastructure/HotkeyRouterService.cs`
- `src/GimmeCapture/Services/Core/Infrastructure/StartupService.cs`
- `src/GimmeCapture/Models/AppSettings.cs`

### Priority 4: AI Resource and Download Orchestration
#### Problem
`AIResourceService` currently mixes:

- model URL catalog
- download workflow
- file layout rules
- readiness checks
- resource removal
- ONNX session cache
- warmup logic
- error state and progress forwarding

`ResourceQueueService` is a singleton and combines queue state, task scheduling, cancellation, and observable UI status.

There is already a visible symptom of orchestration drift: the main ViewModel contains queue and service state reconciliation logic instead of trusting one source of truth.

#### Target State
Split installation and runtime responsibilities.

Suggested extractions:

- `AIModelCatalog`
- `AIResourceInstaller`
- `AIResourceStateService`
- `SAM2RuntimeService`
- `IBackgroundTaskQueue`
- `ModuleInstallCoordinator`

#### First Concrete Steps

1. Move model URLs and preset metadata into a catalog object.
2. Move install and remove workflows into an installer service.
3. Keep runtime session caching in a runtime-specific service only.
4. Replace singleton queue access with injected queue dependency.
5. Make queue state authoritative so the UI does not need race-condition workarounds.

#### Main Files Involved

- `src/GimmeCapture/Services/Core/AI/AIResourceService.cs`
- `src/GimmeCapture/Services/Core/AI/AIPathService.cs`
- `src/GimmeCapture/Services/Core/AI/NativeResolverService.cs`
- `src/GimmeCapture/Services/Core/Media/ResourceQueueService.cs`
- `src/GimmeCapture/ViewModels/Main/MainWindowViewModel.Modules.cs`

### Priority 5: App Shell, Tray, and Window Orchestration
#### Problem
Tray behavior, download window behavior, dialogs, and snip window creation are distributed between `App` and `MainWindow` code-behind.

This keeps platform and app-shell concerns close to UI classes and makes lifecycle changes harder than they should be.

#### Target State
Introduce app-shell services for:

- tray menu lifecycle
- snip window creation and reuse
- dialog routing
- download/progress window orchestration

Suggested extractions:

- `TrayController`
- `AppShellService`
- `CaptureWindowCoordinator`
- `DialogService`

#### Main Files Involved

- `src/GimmeCapture/App.axaml.cs`
- `src/GimmeCapture/Views/Main/MainWindow.axaml.cs`
- `src/GimmeCapture/Services/Platforms/Avalonia/*.cs`

## Suggested Phase Plan

### Phase 0: Safety Net
Add characterization tests before structural edits.

Recommended coverage:

- `MainWindowViewModel` settings load/save behavior
- hotkey registration changes when settings change
- `TranslationService` warmup and readiness behavior
- `ResourceQueueService` status transitions and cancellation behavior
- `AIResourceService` model selection rules

### Phase 1: Composition Root
Goal: stop scattered `new` chains.

Deliverables:

- bootstrapper or service registry
- constructor injection for shared services
- factories for windows and session-scoped objects

### Phase 2: Settings and Hotkey Boundary
Goal: stop side effects from leaking through property setters.

Deliverables:

- settings persistence service
- hotkey coordinator
- smaller settings view models or settings sections

### Phase 3: Snip Session Split
Goal: remove orchestration from `SnipWindowViewModel`.

Deliverables:

- typed auto action enum
- translation session service
- AI scan service
- snip state machine abstraction

### Phase 4: AI Module Split
Goal: separate install-time and runtime AI concerns.

Deliverables:

- AI model catalog
- AI installer
- runtime session service
- queue abstraction

### Phase 5: App Shell Cleanup
Goal: reduce code-behind orchestration.

Deliverables:

- tray controller
- capture window coordinator
- dialog routing abstraction

## Recommended PR Slicing
Do not combine these in one PR:

- settings persistence refactor and translation refactor
- queue refactor and AI runtime refactor
- window lifecycle refactor and snip state machine refactor

Good early PR slices:

1. Bootstrapper plus constructor injection only.
2. Settings persistence coordinator plus tests.
3. Snip translation session extraction with no UI behavior changes.
4. AI resource installer split without touching model inference behavior.

## Known Test Gaps
Current tests exist for some focused components, but the most fragile architecture areas still have weak coverage.

Highest-value missing tests:

- `MainWindowViewModel`
- `ResourceQueueService`
- `TranslationService`
- app-shell and tray orchestration

Without those tests, large refactors will be slower and riskier.

## Implementation Rules
Use these rules during refactor work:

- Keep behavior identical per PR.
- Compile after each step.
- Add tests before moving orchestration across boundaries.
- Prefer extracting collaborators before rewriting logic.
- Do not mix state-machine changes with UI restyling or unrelated cleanup.
- Do not keep adding more logic to the current hotspot files while preparing the refactor.

## Proposed Next Starting Point
If only one refactor starts next, start with Composition Root plus Settings boundary work.

Reason:

- it reduces future change cost across the whole app
- it unlocks easier testing for later refactors
- it removes the highest amount of hidden coupling with the lowest behavior risk

Suggested first implementation sequence:

1. Introduce bootstrapper and shared service graph.
2. Inject `MainWindowViewModel` dependencies.
3. Inject `SnipWindowViewModel` dependencies.
4. Extract settings persistence and hotkey coordination.
5. Only then start splitting Snip translation orchestration.
