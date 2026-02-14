using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public class AppSettingsService
{
    private static string LocalConfigPath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config.json");
    private static string AppDataPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimmeCapture");
    private static string AppDataConfigPath => Path.Combine(AppDataPath, "config.json");

    public string BaseDataDirectory { get; private set; } = AppDomain.CurrentDomain.BaseDirectory;
    private string ConfigPath => Path.Combine(BaseDataDirectory, "config.json");
    
    public virtual AppSettings Settings { get; protected set; } = new();

    public AppSettingsService() { }

    public AppSettingsService(string baseDataDirectory)
    {
        BaseDataDirectory = baseDataDirectory;
    }
    
    public void DebugLog(string message)
    {
        try
        {
            var logPath = Path.Combine(BaseDataDirectory, "settings_debug.log");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(logPath, $"[{timestamp}] {message}{Environment.NewLine}");
            System.Diagnostics.Debug.WriteLine($"[Settings] {message}");
        }
        catch { }
    }

    private JsonSerializerOptions GetJsonOptions() => new JsonSerializerOptions 
    { 
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public async Task LoadAsync()
    {
        string? targetPath = null;
        DateTime localTime = DateTime.MinValue;
        DateTime appDataTime = DateTime.MinValue;

        bool localExists = File.Exists(LocalConfigPath);
        bool appDataExists = File.Exists(AppDataConfigPath);

        if (localExists)
        {
            localTime = File.GetLastWriteTime(LocalConfigPath);
            targetPath = LocalConfigPath;
            BaseDataDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        if (appDataExists)
        {
            appDataTime = File.GetLastWriteTime(AppDataConfigPath);
            if (!localExists || appDataTime > localTime)
            {
                targetPath = AppDataConfigPath;
                BaseDataDirectory = AppDataPath;
            }
        }

        DebugLog($"Loading phase. Local exists: {localExists} ({localTime}), AppData exists: {appDataExists} ({appDataTime}). Choosing: {targetPath ?? "DEFAULT"}");

        if (targetPath != null)
        {
            try
            {
                var json = await File.ReadAllTextAsync(targetPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, GetJsonOptions());
                if (settings != null)
                {
                    UpdateSettings(settings);
                    DebugLog($"Successfully loaded settings from {targetPath}. Language value: {Settings.Language}");
                }
            }
            catch (Exception ex)
            {
                DebugLog($"ERROR loading from {targetPath}: {ex.Message}");
            }
        }
    }

    private void UpdateSettings(AppSettings source)
    {
        var dest = Settings;
        dest.Language = source.Language;
        dest.RunOnStartup = source.RunOnStartup;
        dest.AutoCheckUpdates = source.AutoCheckUpdates;
        dest.BorderThickness = source.BorderThickness;
        dest.MaskOpacity = source.MaskOpacity;
        dest.BorderColorHex = source.BorderColorHex;
        dest.ThemeColorHex = source.ThemeColorHex;
        dest.WingScale = source.WingScale;
        dest.CornerIconScale = source.CornerIconScale;
        dest.HideSnipPinDecoration = source.HideSnipPinDecoration;
        dest.HideSnipPinBorder = source.HideSnipPinBorder;
        dest.HideSnipSelectionDecoration = source.HideSnipSelectionDecoration;
        dest.HideSnipSelectionBorder = source.HideSnipSelectionBorder;
        dest.HideRecordPinDecoration = source.HideRecordPinDecoration;
        dest.HideRecordPinBorder = source.HideRecordPinBorder;
        dest.HideRecordSelectionDecoration = source.HideRecordSelectionDecoration;
        dest.HideRecordSelectionBorder = source.HideRecordSelectionBorder;
        dest.AutoSave = source.AutoSave;
        dest.SaveDirectory = source.SaveDirectory;
        dest.ShowSnipCursor = source.ShowSnipCursor;
        dest.ShowRecordCursor = source.ShowRecordCursor;
        dest.VideoSaveDirectory = source.VideoSaveDirectory;
        dest.RecordFormat = source.RecordFormat;
        dest.VideoCodec = source.VideoCodec;
        dest.RecordFPS = source.RecordFPS;
        dest.UseFixedRecordPath = source.UseFixedRecordPath;
        dest.TempDirectory = source.TempDirectory;
        dest.SnipHotkey = source.SnipHotkey;
        dest.RecordHotkey = source.RecordHotkey;
        dest.PinHotkey = source.PinHotkey;
        dest.CopyHotkey = source.CopyHotkey;
        dest.RectangleHotkey = source.RectangleHotkey;
        dest.EllipseHotkey = source.EllipseHotkey;
        dest.ArrowHotkey = source.ArrowHotkey;
        dest.LineHotkey = source.LineHotkey;
        dest.PenHotkey = source.PenHotkey;
        dest.TextHotkey = source.TextHotkey;
        dest.MosaicHotkey = source.MosaicHotkey;
        dest.BlurHotkey = source.BlurHotkey;
        dest.UndoHotkey = source.UndoHotkey;
        dest.RedoHotkey = source.RedoHotkey;
        dest.ClearHotkey = source.ClearHotkey;
        dest.SaveHotkey = source.SaveHotkey;
        dest.CloseHotkey = source.CloseHotkey;
        dest.TogglePlaybackHotkey = source.TogglePlaybackHotkey;
        dest.ToggleToolbarHotkey = source.ToggleToolbarHotkey;
        dest.SelectionModeHotkey = source.SelectionModeHotkey;
        dest.CropModeHotkey = source.CropModeHotkey;
        dest.AIResourcesDirectory = source.AIResourcesDirectory;
        dest.EnableAI = source.EnableAI;
        dest.SelectedSAM2Variant = source.SelectedSAM2Variant;
        dest.ShowAIScanBox = source.ShowAIScanBox;
        dest.EnableAIScan = source.EnableAIScan;
        dest.SAM2GridDensity = source.SAM2GridDensity;
        dest.SAM2MaxObjects = source.SAM2MaxObjects;
        dest.SAM2MinObjectSize = source.SAM2MinObjectSize;
        dest.SourceLanguage = source.SourceLanguage;
        dest.TargetLanguage = source.TargetLanguage;
        dest.SelectedTranslationEngine = source.SelectedTranslationEngine;
        dest.OllamaModel = source.OllamaModel;
        dest.OllamaApiUrl = source.OllamaApiUrl;
        DebugLog($"UpdateSettings: OllamaModel: '{dest.OllamaModel}', Engine: {dest.SelectedTranslationEngine}");
    }


    public void LoadSync()
    {
        string? targetPath = null;
        DateTime localTime = DateTime.MinValue;
        DateTime appDataTime = DateTime.MinValue;

        if (File.Exists(LocalConfigPath))
        {
            localTime = File.GetLastWriteTime(LocalConfigPath);
            targetPath = LocalConfigPath;
            BaseDataDirectory = AppDomain.CurrentDomain.BaseDirectory;
        }

        if (File.Exists(AppDataConfigPath))
        {
            appDataTime = File.GetLastWriteTime(AppDataConfigPath);
            if (appDataTime > localTime)
            {
                targetPath = AppDataConfigPath;
                BaseDataDirectory = AppDataPath;
            }
        }

        if (targetPath != null)
        {
            try
            {
                var json = File.ReadAllText(targetPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json, GetJsonOptions());
                if (settings != null) UpdateSettings(settings);
            }
            catch { }
        }
    }


    public async Task SaveAsync()
    {
        try
        {
            var options = GetJsonOptions();
            var json = JsonSerializer.Serialize(Settings, options);

            DebugLog($"Saving settings to {ConfigPath}. Language: {Settings.Language}");

            // Attempt to save to current directory first if that's where we are
            if (BaseDataDirectory == AppDomain.CurrentDomain.BaseDirectory)
            {
                try
                {
                    await File.WriteAllTextAsync(ConfigPath, json);
                    DebugLog("Saved to local directory successfully.");
                    return;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException || ex is System.Security.SecurityException)
                {
                    DebugLog($"Local directory not writable, switching to AppData. Error: {ex.GetType().Name}");
                    BaseDataDirectory = AppDataPath;
                }
            }

            if (!Directory.Exists(BaseDataDirectory))
            {
                Directory.CreateDirectory(BaseDataDirectory);
            }
            await File.WriteAllTextAsync(ConfigPath, json);
            DebugLog($"Saved to {ConfigPath} successfully.");
        }
        catch (Exception ex)
        {
            DebugLog($"CRITICAL SAVE ERROR: {ex.Message}");
        }
    }
}
