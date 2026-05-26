using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public class AppSettingsService
{
    // Config migration history:
    // v0: Legacy configs without ConfigVersion. May still use HoldSingle/HoldMulti and pre-function-key action defaults.
    // v1: SelectionHoldModifier introduced for translation selection behavior.
    // v2: Action hotkeys moved to F6/F7/F8/F9 defaults.
    // v3: Pin-like actions unified so Snip.Pin / Record.Action / Translate.Pin share F6.
    public const int CurrentConfigVersion = 3;
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

    /// <summary>
    /// v0 -> v1: migrate legacy Translate.HoldSingle / HoldMulti into SelectionHoldModifier
    /// when the newer key is absent.
    /// </summary>
    private static void MigrateTranslateSelectionHoldModifier(string json, AppSettings settings)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Translate", out var tr)) return;

            static string? LegacyString(JsonElement parent, string pascalName)
            {
                if (parent.TryGetProperty(pascalName, out var el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
                var camel = char.ToLowerInvariant(pascalName[0]) + pascalName[1..];
                if (parent.TryGetProperty(camel, out el) && el.ValueKind == JsonValueKind.String)
                    return el.GetString();
                return null;
            }

            var sel = LegacyString(tr, "SelectionHoldModifier");
            if (!string.IsNullOrWhiteSpace(sel))
                return;

            var pick = LegacyString(tr, "HoldMulti") ?? LegacyString(tr, "HoldSingle");
            if (!string.IsNullOrEmpty(pick))
                settings.Translate.SelectionHoldModifier = pick;
        }
        catch { /* ignore malformed migration */ }
    }

    private static void MigrateLegacyActionHotkeys(AppSettings settings)
    {
        // v2 -> v3: unify pin-like actions on F6 while preserving clearly custom values.
        if (string.Equals(settings.Snip.Pin, "Shift+Enter", StringComparison.OrdinalIgnoreCase))
            settings.Snip.Pin = "F6";

        if (string.Equals(settings.Record.Action, "Shift+Enter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.Record.Action, "F7", StringComparison.OrdinalIgnoreCase))
            settings.Record.Action = "F6";

        if (string.Equals(settings.Translate.Action, "F3", StringComparison.OrdinalIgnoreCase))
            settings.Translate.Action = "F8";

        if (string.Equals(settings.Translate.Pin, "Shift+Enter", StringComparison.OrdinalIgnoreCase)
            || string.Equals(settings.Translate.Pin, "F9", StringComparison.OrdinalIgnoreCase))
            settings.Translate.Pin = "F6";
    }

    private static int DetectConfigVersion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("ConfigVersion", out var versionEl)
                && versionEl.ValueKind == JsonValueKind.Number
                && versionEl.TryGetInt32(out int version))
            {
                return version;
            }
        }
        catch
        {
        }

        return 0;
    }

    private static void ApplyMigrations(string json, AppSettings settings, int sourceVersion)
    {
        // Migrations are intentionally cumulative and version-gated.
        // Once a config has a newer version number, we stop "guessing" based on value shapes.
        if (sourceVersion < 1)
        {
            MigrateTranslateSelectionHoldModifier(json, settings);
        }

        if (sourceVersion < 2)
        {
            // v1 -> v2: move older action defaults onto function-key based actions.
            if (string.Equals(settings.Snip.Pin, "Shift+Enter", StringComparison.OrdinalIgnoreCase))
                settings.Snip.Pin = "F6";

            if (string.Equals(settings.Record.Action, "Shift+Enter", StringComparison.OrdinalIgnoreCase))
                settings.Record.Action = "F7";

            if (string.Equals(settings.Translate.Action, "F3", StringComparison.OrdinalIgnoreCase))
                settings.Translate.Action = "F8";

            if (string.Equals(settings.Translate.Pin, "Shift+Enter", StringComparison.OrdinalIgnoreCase))
                settings.Translate.Pin = "F9";
        }

        if (sourceVersion < 3)
        {
            MigrateLegacyActionHotkeys(settings);
        }

        settings.ConfigVersion = CurrentConfigVersion;
    }

    public async Task LoadAsync()
    {
        if (BaseDataDirectory != AppDomain.CurrentDomain.BaseDirectory && File.Exists(ConfigPath))
        {
            await LoadFromPathAsync(ConfigPath);
            return;
        }

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
            await LoadFromPathAsync(targetPath);
    }

    public void UpdateSettings(AppSettings source)
    {
        var dest = Settings;
        dest.ConfigVersion = source.ConfigVersion;
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
        dest.RecordSystemAudio = source.RecordSystemAudio;
        dest.VideoSaveDirectory = source.VideoSaveDirectory;
        dest.RecordFormat = source.RecordFormat;
        dest.VideoCodec = source.VideoCodec;
        dest.RecordFPS = source.RecordFPS;
        dest.UseFixedRecordPath = source.UseFixedRecordPath;
        dest.TempDirectory = source.TempDirectory;
        dest.SnipHotkey = source.SnipHotkey;
        dest.RecordHotkey = source.RecordHotkey;
        dest.TranslateHotkey = source.TranslateHotkey;

        // Structured Hotkeys
        dest.Snip.Rectangle = source.Snip.Rectangle;
        dest.Snip.Ellipse = source.Snip.Ellipse;
        dest.Snip.Arrow = source.Snip.Arrow;
        dest.Snip.Line = source.Snip.Line;
        dest.Snip.Pen = source.Snip.Pen;
        dest.Snip.Text = source.Snip.Text;
        dest.Snip.Mosaic = source.Snip.Mosaic;
        dest.Snip.Blur = source.Snip.Blur;
        dest.Snip.Undo = source.Snip.Undo;
        dest.Snip.Redo = source.Snip.Redo;
        dest.Snip.Clear = source.Snip.Clear;
        dest.Snip.Save = source.Snip.Save;
        dest.Snip.Copy = source.Snip.Copy;
        dest.Snip.Pin = source.Snip.Pin;
        dest.Snip.Close = source.Snip.Close;
        dest.Snip.Toolbar = source.Snip.Toolbar;
        dest.Snip.SelectionMode = source.Snip.SelectionMode;
        dest.Snip.CropMode = source.Snip.CropMode;

        dest.Record.Rectangle = source.Record.Rectangle;
        dest.Record.Ellipse = source.Record.Ellipse;
        dest.Record.Arrow = source.Record.Arrow;
        dest.Record.Line = source.Record.Line;
        dest.Record.Pen = source.Record.Pen;
        dest.Record.Text = source.Record.Text;
        dest.Record.Mosaic = source.Record.Mosaic;
        dest.Record.Blur = source.Record.Blur;
        dest.Record.Undo = source.Record.Undo;
        dest.Record.Redo = source.Record.Redo;
        dest.Record.Clear = source.Record.Clear;
        dest.Record.Save = source.Record.Save;
        dest.Record.Copy = source.Record.Copy;
        dest.Record.Close = source.Record.Close;
        dest.Record.Toolbar = source.Record.Toolbar;
        dest.Record.Action = source.Record.Action;
        dest.Record.Playback = source.Record.Playback;

        dest.Translate.Action = source.Translate.Action;
        dest.Translate.Pin = source.Translate.Pin;
        dest.Translate.Toolbar = source.Translate.Toolbar;
        dest.Translate.Close = source.Translate.Close;
        dest.Translate.TranslateAll = source.Translate.TranslateAll;
        dest.Translate.ScanAll = source.Translate.ScanAll;
        dest.Translate.ClearAll = source.Translate.ClearAll;
        dest.Translate.ToggleSelect = source.Translate.ToggleSelect;
        dest.Translate.AutoDetect = source.Translate.AutoDetect;
        dest.Translate.SelectionHoldModifier = source.Translate.SelectionHoldModifier;
        dest.Translate.ModeCursor = source.Translate.ModeCursor;
        dest.Translate.ModeSingle = source.Translate.ModeSingle;
        dest.Translate.ModeMulti = source.Translate.ModeMulti;
        dest.Translate.SwitchToSnip = source.Translate.SwitchToSnip;
        dest.Translate.SwitchToRecord = source.Translate.SwitchToRecord;

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
        dest.SelectedTranslationEngine = TranslationEngine.LlamaSharp;
        dest.LlamaModelId = string.IsNullOrWhiteSpace(source.LlamaModelId) ? "qwen2.5-1.5b-instruct-q4" : source.LlamaModelId;
        dest.LlamaCustomModelPath = source.LlamaCustomModelPath;
        dest.LlamaContextSize = source.LlamaContextSize;
        dest.LlamaGpuLayers = source.LlamaGpuLayers;
        DebugLog($"UpdateSettings: LlamaModelId: '{dest.LlamaModelId}', Engine: {dest.SelectedTranslationEngine}");
    }


    public void LoadSync()
    {
        if (BaseDataDirectory != AppDomain.CurrentDomain.BaseDirectory && File.Exists(ConfigPath))
        {
            LoadFromPathSync(ConfigPath);
            return;
        }

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
            LoadFromPathSync(targetPath);
    }

    private async Task LoadFromPathAsync(string path)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            ApplyLoadedJson(path, json);
        }
        catch (Exception ex)
        {
            DebugLog($"ERROR loading from {path}: {ex.Message}");
        }
    }

    private void LoadFromPathSync(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            ApplyLoadedJson(path, json);
        }
        catch (Exception ex)
        {
            DebugLog($"ERROR loading from {path}: {ex.Message}");
        }
    }

    private void ApplyLoadedJson(string path, string json)
    {
        int sourceVersion = DetectConfigVersion(json);
        var settings = JsonSerializer.Deserialize<AppSettings>(json, GetJsonOptions());
        if (settings == null)
            return;

        ApplyMigrations(json, settings, sourceVersion);
        UpdateSettings(settings);
        DebugLog($"Successfully loaded settings from {path}. Version {sourceVersion} -> {Settings.ConfigVersion}. Language value: {Settings.Language}");
    }


    public async Task SaveAsync()
    {
        try
        {
            Settings.ConfigVersion = CurrentConfigVersion;
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
