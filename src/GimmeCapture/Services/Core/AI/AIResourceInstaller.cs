using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Models;

namespace GimmeCapture.Services.Core.AI;

internal sealed class AIResourceInstaller
{
    private readonly AppSettingsService _settingsService;
    private readonly AIPathService _pathService;
    private readonly AIModelDownloader _downloader;
    private readonly AIModelCatalog _modelCatalog;
    private readonly AIResourceInstallerCallbacks _callbacks;
    private readonly SemaphoreSlim _downloadLock = new(1, 1);

    public AIResourceInstaller(
        AppSettingsService settingsService,
        AIPathService pathService,
        AIModelDownloader downloader,
        AIModelCatalog modelCatalog,
        AIResourceInstallerCallbacks callbacks)
    {
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _modelCatalog = modelCatalog ?? throw new ArgumentNullException(nameof(modelCatalog));
        _callbacks = callbacks ?? throw new ArgumentNullException(nameof(callbacks));
    }

    public async Task<bool> EnsureLlamaModelAsync(string modelId, CancellationToken ct = default)
    {
        if (!_modelCatalog.TryGetLlamaModelPreset(modelId, out var preset) ||
            !preset.IsDownloadable ||
            preset.Artifact is null)
        {
            _callbacks.SetLastErrorMessage("Selected Llama model is not downloadable. Please place a GGUF file in the custom model path.");
            return false;
        }

        string modelDir = _callbacks.GetLlmModelsDir();
        Directory.CreateDirectory(modelDir);
        string modelPath = Path.Combine(modelDir, preset.FileName);
        if (File.Exists(modelPath))
        {
            return true;
        }

        await _downloadLock.WaitAsync(ct);
        try
        {
            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = $"LLM Model ({preset.DisplayName})";
            _downloader.DownloadProgress = 0;
            await _downloader.DownloadFileAsync(preset.Artifact, modelPath, 0, 100, ct);
            _settingsService.Settings.LlamaModelId = modelId;
            return File.Exists(modelPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _callbacks.SetLastErrorMessage(ex.Message);
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public bool RemoveLlamaModelPreset(string modelId)
    {
        try
        {
            string path = _callbacks.GetLlamaModelPathById(modelId);
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch (Exception ex)
        {
            _callbacks.SetLastErrorMessage($"LLM model removal failed: {ex.Message}");
            return false;
        }
    }

    public bool RemoveAICoreResources()
    {
        try
        {
            _callbacks.RequestGlobalUnload();
            _callbacks.UnloadAllSessions();

            var runtimeDir = _pathService.GetRuntimeDir();
            var u2net = _pathService.GetAICoreModelPath();

            if (File.Exists(u2net)) File.Delete(u2net);
            if (Directory.Exists(runtimeDir)) Directory.Delete(runtimeDir, true);

            RaiseReadinessProperties(nameof(AIResourceService.IsAICoreReady), nameof(AIResourceService.AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            string message = $"AI Core Removal Failed: {ex.Message}";
            _callbacks.SetLastErrorMessage(message);
            Debug.WriteLine(message);
            return false;
        }
    }

    public bool RemoveSAM2Resources(SAM2Variant variant)
    {
        try
        {
            _callbacks.RequestGlobalUnload();
            _callbacks.UnloadSam2Runtime();

            var paths = _pathService.GetSAM2Paths(variant);
            if (File.Exists(paths.Encoder)) File.Delete(paths.Encoder);
            if (File.Exists(paths.Decoder)) File.Delete(paths.Decoder);

            RaiseReadinessProperties(nameof(AIResourceService.IsSAM2Ready), nameof(AIResourceService.AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            string message = $"SAM2 Removal Failed: {ex.Message}";
            _callbacks.SetLastErrorMessage(message);
            Debug.WriteLine(message);
            return false;
        }
    }

    public bool RemoveOCRResources()
    {
        try
        {
            _callbacks.RequestGlobalUnload();

            var ocrDir = Path.Combine(_pathService.GetAIResourcesPath(), "ocr");
            if (Directory.Exists(ocrDir)) Directory.Delete(ocrDir, true);

            RaiseReadinessProperties(nameof(AIResourceService.IsOCRReady), nameof(AIResourceService.AreResourcesReady));
            return true;
        }
        catch (Exception ex)
        {
            string message = $"OCR Removal Failed: {ex.Message}";
            _callbacks.SetLastErrorMessage(message);
            Debug.WriteLine(message);
            return false;
        }
    }

    public async Task<bool> EnsureAICoreAsync(CancellationToken ct = default)
    {
        if (_callbacks.IsAICoreReady()) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_callbacks.IsAICoreReady()) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = "AI Core";
            _downloader.DownloadProgress = 0;

            Directory.CreateDirectory(_pathService.GetRuntimeDir());
            Directory.CreateDirectory(_pathService.GetAIModelsDir());

            var package = _modelCatalog.GetAICorePackage();

            // The AI-Core runtime zip is the Windows-only onnxruntime.dll build; on Linux/macOS the native
            // runtime ships with the app (libonnxruntime.so/.dylib), so skip it and only fetch the model.
            if (OperatingSystem.IsWindows())
            {
                var onnxDll = _pathService.GetOnnxDllPath();
                if (!File.Exists(onnxDll))
                {
                    await _downloader.DownloadAndExtractZipAsync(package.OnnxRuntimeZip, _pathService.GetRuntimeDir(), 0, 60, ct);
                }
                else
                {
                    _downloader.DownloadProgress = 60;
                }
            }
            else
            {
                _downloader.DownloadProgress = 60;
            }

            var modelPath = _pathService.GetAICoreModelPath();
            if (!File.Exists(modelPath))
            {
                await _downloader.DownloadFileAsync(package.U2NetModel, modelPath, 60, 40, ct);
            }
            else
            {
                _downloader.DownloadProgress = 100;
            }

            return _callbacks.IsAICoreReady();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _callbacks.SetLastErrorMessage(ex.Message);
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public async Task<bool> EnsureSAM2Async(SAM2Variant variant, CancellationToken ct = default)
    {
        if (_callbacks.IsSAM2Ready(variant)) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_callbacks.IsSAM2Ready(variant)) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = $"SAM2 Model ({variant})";
            _downloader.DownloadProgress = 0;

            Directory.CreateDirectory(_pathService.GetAIModelsDir());

            var paths = _pathService.GetSAM2Paths(variant);
            var package = _modelCatalog.GetSam2Package(variant);

            if (!File.Exists(paths.Encoder))
            {
                await _downloader.DownloadFileAsync(package.Encoder, paths.Encoder, 0, 90, ct);
            }
            else
            {
                _downloader.DownloadProgress = 90;
            }

            if (!File.Exists(paths.Decoder))
            {
                await _downloader.DownloadFileAsync(package.Decoder, paths.Decoder, 90, 10, ct);
            }
            else
            {
                _downloader.DownloadProgress = 100;
            }

            return _callbacks.IsSAM2Ready(variant);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _callbacks.SetLastErrorMessage(ex.Message);
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    public async Task<bool> EnsureOCRAsync(OCRLanguage language, CancellationToken ct = default)
    {
        if (_callbacks.IsOCRReady(language)) return true;

        await _downloadLock.WaitAsync(ct);
        try
        {
            if (_callbacks.IsOCRReady(language)) return true;

            _downloader.IsDownloading = true;
            _downloader.CurrentDownloadName = "OCR Models (PaddleOCR v5)";
            _downloader.DownloadProgress = 0;

            var ocrDir = Path.Combine(_pathService.GetAIResourcesPath(), "ocr");
            Directory.CreateDirectory(ocrDir);

            var paths = _pathService.GetOCRPaths(language);
            var package = _modelCatalog.GetOcrPackage(language);

            if (!File.Exists(paths.Det))
                await _downloader.DownloadFileAsync(package.Detection, paths.Det, 0, 40, ct);
            else
                _downloader.DownloadProgress = 40;

            if (!File.Exists(paths.Rec))
                await _downloader.DownloadFileAsync(package.Recognition, paths.Rec, 40, 50, ct);
            else
                _downloader.DownloadProgress = 90;

            if (!File.Exists(paths.Dict))
                await _downloader.DownloadFileAsync(package.Dictionary, paths.Dict, 90, 10, ct);
            else
                _downloader.DownloadProgress = 100;

            return _callbacks.IsOCRReady(language);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            _callbacks.SetLastErrorMessage(ex.Message);
            return false;
        }
        finally
        {
            _downloader.IsDownloading = false;
            _downloadLock.Release();
        }
    }

    private void RaiseReadinessProperties(params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            _callbacks.RaisePropertyChanged(propertyName);
        }
    }
}

internal sealed record AIResourceInstallerCallbacks(
    Action<string> SetLastErrorMessage,
    Action<string> RaisePropertyChanged,
    Action RequestGlobalUnload,
    Action UnloadAllSessions,
    Action UnloadSam2Runtime,
    Func<bool> IsAICoreReady,
    Func<SAM2Variant, bool> IsSAM2Ready,
    Func<OCRLanguage, bool> IsOCRReady,
    Func<string> GetLlmModelsDir,
    Func<string, string> GetLlamaModelPathById);
