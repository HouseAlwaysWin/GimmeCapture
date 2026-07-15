using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.AI;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Services.Core.Media;
using System;

namespace GimmeCapture.ViewModels.Main;

public sealed record MainWindowViewModelDependencies(
    IAppSettingsService SettingsService,
    IWindowManager WindowManager,
    IThemeResourceService ThemeResourceService,
    IGlobalHotkeyService HotkeyService,
    IGlobalHotkeySettingsCoordinator GlobalHotkeySettingsCoordinator,
    IStartupRegistrationService StartupRegistrationService,
    ISettingsSaveCoordinatorFactory SettingsSaveCoordinatorFactory,
    IMainWindowSettingsPersistenceService SettingsPersistenceService,
    HotkeyMappingService HotkeyMappingService,
    HotkeyRouterService HotkeyRouterService,
    Lazy<FFmpegDownloaderService> FfmpegDownloader,
    Lazy<RecordingService> RecordingService,
    Lazy<UpdateService> UpdateService,
    AIModelCatalog AIModelCatalog,
    Lazy<AIResourceService> AIResourceService,
    Lazy<AIResourceOrchestrator> AIResourceOrchestrator,
    Lazy<SAM2RuntimeService> SAM2RuntimeService,
    Lazy<OcrRuntimeService> OcrRuntimeService,
    AIPathService AIPathService,
    IResourceQueueService ResourceQueue,
    ITranslationResultLayerService TranslationResultLayer);
