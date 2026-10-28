using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.Infrastructure;

public class AppSettingsService : IAppSettingsService
{
    // Config migration history:
    // v0: Legacy configs without ConfigVersion. May still use HoldSingle/HoldMulti and pre-function-key action defaults.
    // v1: SelectionHoldModifier introduced for translation selection behavior.
    // v2: Action hotkeys moved to F6/F7/F8/F9 defaults.
    // v3: Pin-like actions unified so Snip.Pin / Record.Action / Translate.Pin share F6.
    // v4: Snip AI detecting moved to OCR-only; legacy AIScanEngine/SAM2 scan tuning settings are ignored.
    // v5: Translation default Llama preset switches to curated Qwen3 list; deprecated ids fall back safely.
    // v6: Translation model list narrows to TranslateGemma + Gemma 3 4B + Gemma 4 E4B.
    // v7: Translation model list narrows to TranslateGemma 4B + Gemma 3 4B + TranslateGemma 12B.
    // v8: TranslateGemma 12B removed from visible/default path; falls back to TranslateGemma 4B.
    // v9: TranslateGemma 12B restored as an optional supported preset.
    // v10: Llama preset support and migration metadata moved into AIModelCatalog.
    // v11: Legacy translation engines migrate to LlamaSharp; delayed capture and quick OCR settings added.
    public const int CurrentConfigVersion = 11;
    private readonly string _appVersion;
    // Set only by the constructor that takes a directory (tests, the designer preview): see FindConfigSource.
    private readonly bool _hasExplicitDataDirectory;

    public string BaseDataDirectory { get; private set; } = RuntimePathProvider.GetExecutableDirectory();
    private string ConfigPath => Path.Combine(BaseDataDirectory, "config.json");
    private string RuntimeExecutableDirectory => RuntimePathProvider.GetExecutableDirectory();
    private string RuntimeLocalConfigPath => Path.Combine(RuntimeExecutableDirectory, "config.json");
    private string RuntimeVersionedAppDataPath => AppStoragePaths.GetVersionedDataDirectory(RuntimeExecutableDirectory, _appVersion);
    private string RuntimeVersionedAppDataConfigPath => AppStoragePaths.GetVersionedConfigPath(RuntimeExecutableDirectory, _appVersion);
    private static string LegacyAppDataConfigPath => AppStoragePaths.GetLegacyConfigPath();
    
    public AppSettings Settings { get; protected set; } = new();

    public AppSettingsService()
    {
        _appVersion = AppVersionInfo.CurrentVersion;
    }

    public AppSettingsService(string baseDataDirectory, string? appVersion = null)
    {
        BaseDataDirectory = baseDataDirectory;
        _appVersion = string.IsNullOrWhiteSpace(appVersion) ? AppVersionInfo.CurrentVersion : appVersion;
        _hasExplicitDataDirectory = true;
    }
    
    public void DebugLog(string message)
    {
        AppLog.Information($"Settings.{message}");
    }

    private bool IsRuntimeDefaultStorage =>
        string.Equals(
            Path.GetFullPath(BaseDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(RuntimeExecutableDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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
        catch (Exception ex)
        {
            AppLog.Warning("AppSettings.MigrateLegacySelectionModifier", ex);
        }
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

    private static string NormalizeLlamaModelId(string? modelId)
    {
        return AIModelCatalog.NormalizeLlamaModelId(modelId);
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
        catch (JsonException)
        {
            // Not JSON: version 0, and the load that follows reports the unreadable file.
        }

        return 0;
    }

    private static string NormalizeLegacyTranslationEngineJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root == null)
            {
                return json;
            }

            foreach (string propertyName in new[] { "SelectedTranslationEngine", "selectedTranslationEngine" })
            {
                if (root.ContainsKey(propertyName))
                {
                    root[propertyName] = nameof(TranslationEngine.LlamaSharp);
                }
            }

            return root.ToJsonString();
        }
        catch
        {
            return json;
        }
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

        if (sourceVersion < 10)
        {
            settings.LlamaModelId = NormalizeLlamaModelId(settings.LlamaModelId);
        }

        if (sourceVersion < 11)
        {
            settings.SelectedTranslationEngine = TranslationEngine.LlamaSharp;
        }

        settings.ConfigVersion = CurrentConfigVersion;
    }

    public async Task LoadAsync()
    {
        ConfigSource? source = FindConfigSource();
        if (source is not { } found)
        {
            DebugLog("Loading phase. No saved config: DEFAULT");
            return;
        }

        DebugLog($"Loading phase. Choosing: {found.Path}");
        UseConfigSource(found);
        await LoadFromPathAsync(found.Path, found.SaveDirectoryOverride);
    }

    /// <summary>
    /// The saved UI language, read without loading or migrating the rest of the settings. The windows are built
    /// before the settings load, and building them in the wrong language costs the main window a second full
    /// layout once the saved one is applied. Null on a first run or when the config cannot be read.
    /// </summary>
    public Language? PeekSavedLanguage()
    {
        if (FindConfigSource() is not { } source)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(source.Path));
            if (!document.RootElement.TryGetProperty(nameof(AppSettings.Language), out JsonElement value))
            {
                return null;
            }

            if (value.ValueKind == JsonValueKind.String && Enum.TryParse(value.GetString(), out Language named))
            {
                return named;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && Enum.IsDefined(typeof(Language), number))
            {
                return (Language)number;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            AppLog.Warning("Settings.PeekLanguage", ex);
        }

        return null;
    }

    // The config file a load reads, in order: one in an explicitly chosen data directory, this version's AppData
    // config, a config next to the executable, then the config from before configs were versioned. Null on a first
    // run. Only reads; UseConfigSource applies the choice.
    private ConfigSource? FindConfigSource()
    {
        // A directory handed to the constructor is the whole answer, unless it is the executable's own, which is the
        // app's default storage and what the rest of this chain is for. The chain finds and migrates an existing
        // user's config, so it probes machine-wide locations: from a test or the designer preview it read the
        // developer's own %LOCALAPPDATA%\GimmeCapture\config.json, and because that legacy source redirects saves
        // into this executable's versioned AppData folder, the next save wrote a config into the developer's real
        // AppData as well. The app only ever uses the parameterless constructor, so none of this reached it.
        if (_hasExplicitDataDirectory && !IsRuntimeDefaultStorage)
        {
            return File.Exists(ConfigPath)
                ? new ConfigSource(ConfigPath, UsesVersionedAppData: false, SaveDirectoryOverride: null)
                : null;
        }

        if (BaseDataDirectory != RuntimeExecutableDirectory && File.Exists(ConfigPath))
        {
            return new ConfigSource(ConfigPath, UsesVersionedAppData: false, SaveDirectoryOverride: null);
        }

        if (File.Exists(RuntimeVersionedAppDataConfigPath))
        {
            return new ConfigSource(RuntimeVersionedAppDataConfigPath, UsesVersionedAppData: true, SaveDirectoryOverride: null);
        }

        if (File.Exists(RuntimeLocalConfigPath))
        {
            return new ConfigSource(RuntimeLocalConfigPath, UsesVersionedAppData: false, RuntimeVersionedAppDataPath);
        }

        if (File.Exists(LegacyAppDataConfigPath))
        {
            return new ConfigSource(LegacyAppDataConfigPath, UsesVersionedAppData: false, RuntimeVersionedAppDataPath);
        }

        return null;
    }

    private void UseConfigSource(ConfigSource source)
    {
        if (source.UsesVersionedAppData)
        {
            BaseDataDirectory = RuntimeVersionedAppDataPath;
        }
    }

    private readonly record struct ConfigSource(string Path, bool UsesVersionedAppData, string? SaveDirectoryOverride);

    /// <summary>
    /// Copies a freshly loaded <see cref="AppSettings"/> into the live instance. EVERY persisted property needs a
    /// line here: one that is missing is not an error anywhere, it just reverts to its default on the next launch
    /// (24 settings and hotkeys once did exactly that). <c>AppSettingsLoadRoundTripTests</c> fails the build for any
    /// persisted property added to the model without its copy.
    /// </summary>
    public void UpdateSettings(AppSettings source)
    {
        var dest = Settings;
        dest.ConfigVersion = source.ConfigVersion;
        dest.Language = source.Language;
        dest.RunOnStartup = source.RunOnStartup;
        dest.AutoCheckUpdates = source.AutoCheckUpdates;
        dest.BorderThickness = source.BorderThickness;
        dest.BorderColorHex = source.BorderColorHex;
        dest.ThemeColorHex = source.ThemeColorHex;
        dest.WingScale = source.WingScale;
        dest.HideSnipPinDecoration = source.HideSnipPinDecoration;
        dest.HideSnipPinBorder = source.HideSnipPinBorder;
        dest.HideSnipSelectionDecoration = source.HideSnipSelectionDecoration;
        dest.CaptureWithoutStealingFocus = source.CaptureWithoutStealingFocus;
        dest.FreezeScreenOnScreenshot = source.FreezeScreenOnScreenshot;
        dest.CaptureDelay = source.CaptureDelay;
        dest.OcrTextLayout = source.OcrTextLayout;
        dest.SaveOcrTextToFile = source.SaveOcrTextToFile;
        dest.ScrollingCaptureDirection = source.ScrollingCaptureDirection;
        dest.SnipToolbarPosition = source.SnipToolbarPosition;
        dest.HideRecordPinDecoration = source.HideRecordPinDecoration;
        dest.HideRecordPinBorder = source.HideRecordPinBorder;
        dest.HideRecordSelectionDecoration = source.HideRecordSelectionDecoration;
        dest.DefaultHideSnipToolbar = source.DefaultHideSnipToolbar;
        dest.DefaultHideRecordToolbar = source.DefaultHideRecordToolbar;
        dest.AutoSave = source.AutoSave;
        dest.EnableHistory = source.EnableHistory;
        dest.RevealAfterSave = source.RevealAfterSave;
        dest.SaveDirectory = source.SaveDirectory;
        dest.FileNameTemplate = source.FileNameTemplate;
        dest.ShowSnipCursor = source.ShowSnipCursor;
        dest.ShowRecordCursor = source.ShowRecordCursor;
        dest.RecordSystemAudio = source.RecordSystemAudio;
        dest.EnableWebcam = source.EnableWebcam;
        dest.WebcamDeviceName = source.WebcamDeviceName;
        dest.WebcamCorner = source.WebcamCorner;
        dest.WebcamSize = source.WebcamSize;
        dest.WebcamCircular = source.WebcamCircular;
        dest.RecordMicrophone = source.RecordMicrophone;
        dest.SelectedMicDeviceId = source.SelectedMicDeviceId;
        dest.MicVolume = source.MicVolume;
        dest.HighlightCursor = source.HighlightCursor;
        dest.HighlightClicks = source.HighlightClicks;
        dest.ShowKeystrokes = source.ShowKeystrokes;
        dest.PipelinedEncoding = source.PipelinedEncoding;
        dest.VideoSaveDirectory = source.VideoSaveDirectory;
        dest.RecordFormat = source.RecordFormat;
        dest.VideoCodec = source.VideoCodec;
        dest.VideoEncoderHint = source.VideoEncoderHint;
        dest.VideoQuality = source.VideoQuality;
        dest.CustomVideoCrf = source.CustomVideoCrf;
        dest.CustomVideoBitrateKbps = source.CustomVideoBitrateKbps;
        dest.RecordFPS = source.RecordFPS;
        dest.MaxRecordingSizeMB = source.MaxRecordingSizeMB;
        dest.MaxRecordingSeconds = source.MaxRecordingSeconds;
        dest.RecordingAutoStopAction = source.RecordingAutoStopAction;
        dest.PlaybackUiFps = source.PlaybackUiFps;
        dest.PlaybackTimelineFps = source.PlaybackTimelineFps;
        dest.HardwareDecodeEnabled = source.HardwareDecodeEnabled;
        dest.UseFixedRecordPath = source.UseFixedRecordPath;
        dest.TempDirectory = source.TempDirectory;
        dest.SnipHotkey = source.SnipHotkey;
        dest.RecordHotkey = source.RecordHotkey;
        dest.TranslateHotkey = source.TranslateHotkey;
        dest.TextCopyHotkey = source.TextCopyHotkey;
        dest.ScrollingCaptureHotkey = source.ScrollingCaptureHotkey;

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
        dest.Snip.RemoveBackground = source.Snip.RemoveBackground;
        dest.Snip.MagicWand = source.Snip.MagicWand;
        dest.Snip.FullscreenSelect = source.Snip.FullscreenSelect;
        dest.Snip.SwitchToTranslate = source.Snip.SwitchToTranslate;
        dest.Snip.SwitchToRecord = source.Snip.SwitchToRecord;

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
        dest.Record.Stop = source.Record.Stop;
        dest.Record.FullscreenSelect = source.Record.FullscreenSelect;
        dest.Record.SwitchToSnip = source.Record.SwitchToSnip;
        dest.Record.SwitchToTranslate = source.Record.SwitchToTranslate;

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
        dest.SourceLanguage = source.SourceLanguage;
        dest.TargetLanguage = source.TargetLanguage;
        dest.SelectedTranslationEngine = TranslationEngine.LlamaSharp;
        dest.LlamaModelId = NormalizeLlamaModelId(source.LlamaModelId);
        dest.LlamaCustomModelPath = source.LlamaCustomModelPath;
        dest.LlamaContextSize = source.LlamaContextSize;
        dest.LlamaGpuLayers = source.LlamaGpuLayers;
        DebugLog($"UpdateSettings: LlamaModelId: '{dest.LlamaModelId}', Engine: {dest.SelectedTranslationEngine}");
    }


    public void LoadSync()
    {
        if (FindConfigSource() is not { } source)
        {
            return;
        }

        UseConfigSource(source);
        LoadFromPathSync(source.Path, source.SaveDirectoryOverride);
    }

    private async Task LoadFromPathAsync(string path, string? saveDirectoryOverride = null)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path);
            ApplyLoadedJson(path, json, saveDirectoryOverride);
        }
        catch (Exception ex)
        {
            // A parse/read failure here reverts to defaults — surface it (was silent DebugLog).
            AppLog.Warning("Settings.Load", ex);
        }
    }

    private void LoadFromPathSync(string path, string? saveDirectoryOverride = null)
    {
        try
        {
            var json = File.ReadAllText(path);
            ApplyLoadedJson(path, json, saveDirectoryOverride);
        }
        catch (Exception ex)
        {
            DebugLog($"ERROR loading from {path}: {ex.Message}");
        }
    }

    private void ApplyLoadedJson(string path, string json, string? saveDirectoryOverride = null)
    {
        int sourceVersion = DetectConfigVersion(json);
        string normalizedJson = NormalizeLegacyTranslationEngineJson(json);
        var settings = JsonSerializer.Deserialize<AppSettings>(normalizedJson, GetJsonOptions());
        if (settings == null)
            return;

        ApplyMigrations(json, settings, sourceVersion);
        NormalizeAIResourcesDirectory(settings);
        UpdateSettings(settings);
        if (!string.IsNullOrWhiteSpace(saveDirectoryOverride))
        {
            BaseDataDirectory = saveDirectoryOverride;
        }
        DebugLog($"Successfully loaded settings from {path}. Version {sourceVersion} -> {Settings.ConfigVersion}. Language value: {Settings.Language}");
    }

    private void NormalizeAIResourcesDirectory(AppSettings settings)
    {
        settings.AIResourcesDirectory = ResolveAIResourcesDirectory(
            settings.AIResourcesDirectory,
            AppStoragePaths.GetSharedAIResourcesDirectory(RuntimeExecutableDirectory),
            Path.Combine(RuntimeExecutableDirectory, "AI"));
    }

    /// <summary>
    /// The AI resources folder a loaded config uses: the install's shared folder when the config names none, the
    /// shared folder too for a version-scoped one (an older layout, whose files move there), otherwise the folder the
    /// config names. Older versions kept AI resources in the executable's own AI folder, which moves into the shared
    /// folder only while that is the folder in use. When the config names a folder of its own (older versions saved
    /// the executable's AI folder there), that folder is the one in use, and copying it into the shared folder on
    /// every load, as this used to, kept an unused second copy of every model there: 3.4 GB on one machine.
    /// </summary>
    internal static string ResolveAIResourcesDirectory(
        string? configuredDirectory, string sharedDirectory, string legacyExecutableDirectory)
    {
        string directory = sharedDirectory;
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            if (AppStoragePaths.TryMapVersionScopedAIResourcesDirectory(configuredDirectory, out string mappedSharedDirectory))
            {
                TryMigrateAIResourceDirectory(configuredDirectory, mappedSharedDirectory);
                directory = mappedSharedDirectory;
            }
            else
            {
                directory = configuredDirectory;
            }
        }

        if (SamePath(directory, sharedDirectory) && !SamePath(legacyExecutableDirectory, sharedDirectory))
        {
            TryMigrateAIResourceDirectory(legacyExecutableDirectory, sharedDirectory);
        }

        return directory;
    }

    private static void TryMigrateAIResourceDirectory(string sourceDirectory, string targetDirectory)
    {
        try
        {
            if (!Directory.Exists(sourceDirectory))
            {
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            MoveDirectoryContents(sourceDirectory, targetDirectory);

            if (!Directory.EnumerateFileSystemEntries(sourceDirectory).Any())
            {
                Directory.Delete(sourceDirectory, false);
            }

            AppLog.Information("Settings.MigrateAIResources", $"moved {sourceDirectory} into {targetDirectory}");
        }
        catch (Exception ex)
        {
            AppLog.Warning("Settings.MigrateAIResources", ex);
        }
    }

    // Moves what the target lacks, and drops what it already has at the same size: the copies earlier versions made
    // instead of moving. A file the target holds at another size stays put, since neither is provably the one to keep.
    private static void MoveDirectoryContents(string sourceDirectory, string targetDirectory)
    {
        foreach (string directory in Directory.GetDirectories(sourceDirectory))
        {
            string targetSubDirectory = Path.Combine(targetDirectory, Path.GetFileName(directory));
            Directory.CreateDirectory(targetSubDirectory);
            MoveDirectoryContents(directory, targetSubDirectory);

            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory, false);
            }
        }

        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            string targetFile = Path.Combine(targetDirectory, Path.GetFileName(file));
            if (!File.Exists(targetFile))
            {
                File.Move(file, targetFile);
            }
            else if (new FileInfo(targetFile).Length == new FileInfo(file).Length)
            {
                File.Delete(file);
            }
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);


    public async Task SaveAsync()
    {
        try
        {
            Settings.ConfigVersion = CurrentConfigVersion;
            var options = GetJsonOptions();
            var json = JsonSerializer.Serialize(Settings, options);

            if (IsRuntimeDefaultStorage)
                BaseDataDirectory = RuntimeVersionedAppDataPath;

            string path = ConfigPath;
            // A save asked for although nothing persisted changed leaves the file alone. Every launch used to rewrite
            // it that way, and so did every capture, which saves before it opens.
            if (await HoldsAsync(path, json))
            {
                AppLog.Debug("Settings.Save", "Unchanged; the config was not rewritten.");
                return;
            }

            DebugLog($"Saving settings to {path}. Language: {Settings.Language}");

            if (!Directory.Exists(BaseDataDirectory))
            {
                Directory.CreateDirectory(BaseDataDirectory);
            }
            await AtomicFile.WriteAllTextAsync(path, json);
            DebugLog($"Saved to {path} successfully.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings.Save", ex);
        }
    }

    // Whether the config at path already holds exactly json.
    private static async Task<bool> HoldsAsync(string path, string json)
    {
        try
        {
            return File.Exists(path) && await File.ReadAllTextAsync(path) == json;
        }
        catch (Exception ex)
        {
            // Never a reason not to save: the save writes it instead.
            AppLog.Debug("Settings.Save", ex);
            return false;
        }
    }
}
