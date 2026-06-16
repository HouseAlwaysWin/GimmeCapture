using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class MainWindowSettingsPersistenceServiceTests
{
    [Fact]
    public async Task SaveAsync_Writes_Snapshot_State_To_Settings_And_Disk()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var settingsService = new AppSettingsService(tempDir);
        var persistenceService = new MainWindowSettingsPersistenceService();
        var snapshot = new MainWindowSettingsSnapshot
        {
            Language = Language.Japanese,
            RunOnStartup = true,
            AutoCheckUpdates = false,
            BorderThickness = 4,
            BorderColor = Color.Parse("#123456"),
            ThemeColor = Color.Parse("#654321"),
            WingScale = 1.7,
            HideSnipPinDecoration = true,
            HideSnipPinBorder = true,
            HideSnipSelectionDecoration = true,
            HideSnipSelectionBorder = false,
            AutoPinScreenshotSelection = true,
            HideRecordPinDecoration = true,
            HideRecordPinBorder = false,
            HideRecordSelectionDecoration = true,
            HideRecordSelectionBorder = true,
            DefaultHideSnipToolbar = true,
            DefaultHideRecordToolbar = false,
            AutoSave = true,
            SaveDirectory = @"D:\captures",
            ShowSnipCursor = true,
            ShowRecordCursor = false,
            RecordSystemAudio = false,
            VideoSaveDirectory = @"D:\captures\video",
            RecordFormat = "webm",
            VideoCodec = VideoCodec.H265,
            VideoQuality = VideoQuality.High,
            RecordFps = 48,
            MaxRecordingSizeMb = 128.5,
            PlaybackUiFps = 45,
            PlaybackTimelineFps = 18,
            UseFixedRecordPath = true,
            TempDirectory = @"D:\captures\tmp",
            SnipHotkey = "Shift+F7",
            RecordHotkey = "Shift+F8",
            TranslateHotkey = "Shift+F9",
            AIResourcesDirectory = @"D:\captures\ai",
            EnableAI = false,
            ShowAIScanBox = false,
            EnableAIScan = false,
            SourceLanguage = OCRLanguage.Japanese,
            TargetLanguage = TranslationLanguage.English,
            SelectedTranslationEngine = TranslationEngine.LlamaSharp,
            LlamaModelId = "custom-model",
            LlamaCustomModelPath = @"D:\models\custom.gguf",
            LlamaContextSize = 4096,
            LlamaGpuLayers = 22
        };

        await persistenceService.SaveAsync(settingsService, snapshot);

        var persisted = MainWindowSettingsSnapshot.FromAppSettings(settingsService.Settings);
        var savedJson = await File.ReadAllTextAsync(Path.Combine(tempDir, "config.json"));

        Assert.Equal(snapshot.Language, persisted.Language);
        Assert.Equal(snapshot.RunOnStartup, persisted.RunOnStartup);
        Assert.Equal(snapshot.BorderColor, persisted.BorderColor);
        Assert.Equal(snapshot.ThemeColor, persisted.ThemeColor);
        Assert.Equal(snapshot.RecordFormat, persisted.RecordFormat);
        Assert.Equal(snapshot.RecordHotkey, persisted.RecordHotkey);
        Assert.Equal(snapshot.AutoPinScreenshotSelection, persisted.AutoPinScreenshotSelection);
        Assert.Equal(snapshot.EnableAIScan, persisted.EnableAIScan);
        Assert.Equal(snapshot.AIResourcesDirectory, persisted.AIResourcesDirectory);
        Assert.Equal(snapshot.LlamaModelId, persisted.LlamaModelId);
        Assert.Equal(snapshot.LlamaGpuLayers, persisted.LlamaGpuLayers);
        Assert.Contains("\"Language\": \"Japanese\"", savedJson);
        Assert.Contains($"\"ConfigVersion\": {AppSettingsService.CurrentConfigVersion}", savedJson);
        Assert.Contains("\"RecordHotkey\": \"Shift\\u002BF8\"", savedJson);
        Assert.Contains("\"AutoPinScreenshotSelection\": true", savedJson);
        Assert.Contains("\"AIResourcesDirectory\": \"D:\\\\captures\\\\ai\"", savedJson);
        Assert.DoesNotContain("AIScanEngine", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("SAM2GridDensity", savedJson, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadSync_Migrates_Legacy_ActionHotkeys_Only_When_Still_On_Old_Defaults()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath,
            """
            {
              "Snip": { "Pin": "Shift+Enter" },
              "Record": { "Action": "Shift+Enter" },
              "Translate": { "Action": "F3", "Pin": "Shift+Enter" }
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal("F6", settingsService.Settings.Snip.Pin);
        Assert.Equal("F6", settingsService.Settings.Record.Action);
        Assert.Equal("F8", settingsService.Settings.Translate.Action);
        Assert.Equal("F6", settingsService.Settings.Translate.Pin);
    }

    [Fact]
    public void LoadSync_Preserves_Custom_ActionHotkeys()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath,
            """
            {
              "Snip": { "Pin": "Ctrl+F6" },
              "Record": { "Action": "Ctrl+F7" },
              "Translate": { "Action": "Ctrl+F8", "Pin": "Ctrl+F9" }
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal("Ctrl+F6", settingsService.Settings.Snip.Pin);
        Assert.Equal("Ctrl+F7", settingsService.Settings.Record.Action);
        Assert.Equal("Ctrl+F8", settingsService.Settings.Translate.Action);
        Assert.Equal("Ctrl+F9", settingsService.Settings.Translate.Pin);
    }

    [Fact]
    public void LoadSync_Preserves_Legacy_FunctionKey_GlobalLaunchHotkeys()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath,
            """
            {
              "ConfigVersion": 3,
              "SnipHotkey": "Shift+F1",
              "RecordHotkey": "Shift+F2",
              "TranslateHotkey": "Shift+F3"
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal(AppSettingsService.CurrentConfigVersion, settingsService.Settings.ConfigVersion);
        Assert.Equal("Shift+F1", settingsService.Settings.SnipHotkey);
        Assert.Equal("Shift+F2", settingsService.Settings.RecordHotkey);
        Assert.Equal("Shift+F3", settingsService.Settings.TranslateHotkey);
    }

    [Fact]
    public void LoadSync_Preserves_Custom_GlobalLaunchHotkeys()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath,
            """
            {
              "ConfigVersion": 4,
              "SnipHotkey": "Ctrl+Shift+F10",
              "RecordHotkey": "Ctrl+Shift+F11",
              "TranslateHotkey": "Ctrl+Shift+F12"
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal(AppSettingsService.CurrentConfigVersion, settingsService.Settings.ConfigVersion);
        Assert.Equal("Ctrl+Shift+F10", settingsService.Settings.SnipHotkey);
        Assert.Equal("Ctrl+Shift+F11", settingsService.Settings.RecordHotkey);
        Assert.Equal("Ctrl+Shift+F12", settingsService.Settings.TranslateHotkey);
    }

    [Fact]
    public void LoadSync_DoesNotRemapVersionedConfigThatUsesOlderLookingValues()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(MainWindowSettingsPersistenceServiceTests),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        var configPath = Path.Combine(tempDir, "config.json");
        File.WriteAllText(configPath,
            """
            {
              "ConfigVersion": 3,
              "Snip": { "Pin": "F6" },
              "Record": { "Action": "F7" },
              "Translate": { "Action": "F3", "Pin": "F9" }
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal(AppSettingsService.CurrentConfigVersion, settingsService.Settings.ConfigVersion);
        Assert.Equal("F6", settingsService.Settings.Snip.Pin);
        Assert.Equal("F7", settingsService.Settings.Record.Action);
        Assert.Equal("F3", settingsService.Settings.Translate.Action);
        Assert.Equal("F9", settingsService.Settings.Translate.Pin);
    }

    [Fact]
    public void LoadSync_IgnoresRemovedMaskOpacitySetting()
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "GimmeCapture.Tests",
            nameof(LoadSync_IgnoresRemovedMaskOpacitySetting),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(
            Path.Combine(tempDir, "config.json"),
            $$"""
            {
              "ConfigVersion": {{AppSettingsService.CurrentConfigVersion}},
              "MaskOpacity": 0.75,
              "CornerIconScale": 0.8,
              "BorderThickness": 3
            }
            """);

        var settingsService = new AppSettingsService(tempDir);

        settingsService.LoadSync();

        Assert.Equal(3, settingsService.Settings.BorderThickness);
        Assert.Equal(AppSettingsService.CurrentConfigVersion, settingsService.Settings.ConfigVersion);
    }
}
