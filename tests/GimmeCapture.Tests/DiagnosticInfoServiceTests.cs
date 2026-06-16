using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public sealed class DiagnosticInfoServiceTests
{
    [Fact]
    public void Build_IncludesRuntimeAndModuleState_WithoutExceptionMessageContent()
    {
        AppLog.Error("Translation.Test", new InvalidOperationException("sensitive OCR content"));

        string diagnostics = DiagnosticInfoService.Build(
            "v0.41.0",
            [new ModuleDiagnostic("OCR", "Installed")],
            "Avalonia/Skia (test)");

        Assert.Contains("Version: v0.41.0", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Renderer: Avalonia/Skia (test)", diagnostics, StringComparison.Ordinal);
        Assert.Contains("- OCR: Installed", diagnostics, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive OCR content", diagnostics, StringComparison.Ordinal);
    }
}
