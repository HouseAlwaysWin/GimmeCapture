using System;
using System.IO;
using System.Runtime.InteropServices;

namespace GimmeCapture.Services.Core.AI;

public class NativeResolverService
{
    private readonly AIPathService _pathService;
    private static bool _isResolverSet = false;

    public NativeResolverService(AIPathService pathService)
    {
        _pathService = pathService;
    }

    public virtual void SetupNativeResolvers()
    {
        if (_isResolverSet) return;
        _isResolverSet = true;

        try
        {
            // Use .NET's modern DllImportResolver to point directly to the AI/runtime folder.
            var onnxAssembly = typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly;
            
            NativeLibrary.SetDllImportResolver(onnxAssembly, (libraryName, assembly, searchPath) =>
            {
                if (libraryName == "onnxruntime")
                {
                    var dllPath = _pathService.GetOnnxDllPath();
                    
                    if (File.Exists(dllPath))
                    {
                        System.Diagnostics.Debug.WriteLine($"[AI] Custom Resolver loading: {dllPath}");
                        return NativeLibrary.Load(dllPath);
                    }
                }
                return IntPtr.Zero;
            });

            // Also add to PATH as fallback for dependencies of onnxruntime.dll
            var runtimeDirFallback = _pathService.GetRuntimeDir();
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!path.Contains(runtimeDirFallback))
            {
                Environment.SetEnvironmentVariable("PATH", runtimeDirFallback + Path.PathSeparator + path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AI] Failed to setup native resolvers: {ex.Message}");
        }
    }
}
