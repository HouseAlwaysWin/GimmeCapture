using System;
using System.Collections.Generic;
using System.IO;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Pure parts of the OCR text export (name rendering + collision resolution over a fake
// exists-probe) — run on both the Windows and net10.0 (Linux CI) heads.
public class OcrTextExportServiceTests
{
    private static readonly DateTime Stamp = new(2026, 6, 29, 14, 30, 52);

    [Fact]
    public void BuildExportFileName_UsesTemplate_AndTxtExtension()
    {
        Assert.Equal("ocr_20260629.txt",
            OcrTextExportService.BuildExportFileName("ocr_{date}", Stamp));
    }

    [Fact]
    public void BuildExportFileName_BlankTemplate_FallsBackToDefault()
    {
        Assert.Equal("GimmeCapture_20260629_143052.txt",
            OcrTextExportService.BuildExportFileName(null, Stamp));
    }

    [Fact]
    public void ResolveCollisionFreePath_NoCollision_ReturnsPlainPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gimme_ocr");
        string p = OcrTextExportService.ResolveCollisionFreePath(dir, "a.txt", _ => false);
        Assert.Equal(Path.Combine(dir, "a.txt"), p);
    }

    [Fact]
    public void ResolveCollisionFreePath_AppendsCounter_UntilFree()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gimme_ocr");
        var taken = new HashSet<string>
        {
            Path.Combine(dir, "a.txt"),
            Path.Combine(dir, "a (1).txt"),
        };
        string p = OcrTextExportService.ResolveCollisionFreePath(dir, "a.txt", taken.Contains);
        Assert.Equal(Path.Combine(dir, "a (2).txt"), p);
    }
}
