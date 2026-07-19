using System;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Pure template-renderer tests (fixed clock, exact assertions) — run on both the Windows and the
// net10.0 (Linux CI) heads, mirroring the CompressOutputPathTests style.
public class CaptureFileNameServiceTests
{
    private static readonly DateTime Stamp = new(2026, 6, 29, 14, 30, 52);

    [Fact]
    public void DefaultTemplate_ReproducesHistoricalName()
    {
        Assert.Equal("GimmeCapture_20260629_143052",
            CaptureFileNameService.RenderTemplate(CaptureFileNameService.DefaultTemplate, Stamp));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTemplate_FallsBackToDefault(string? template)
    {
        Assert.Equal("GimmeCapture_20260629_143052",
            CaptureFileNameService.RenderTemplate(template, Stamp));
    }

    [Fact]
    public void DatetimeToken_ExpandsAsOneStamp()
    {
        Assert.Equal("cap_20260629_143052",
            CaptureFileNameService.RenderTemplate("cap_{datetime}", Stamp));
    }

    [Fact]
    public void IndividualParts_Expand()
    {
        Assert.Equal("2026-06-29 14.30.52",
            CaptureFileNameService.RenderTemplate("{yyyy}-{MM}-{dd} {HH}.{mm}.{ss}", Stamp));
    }

    [Fact]
    public void UnknownTokens_StayLiteral()
    {
        Assert.Equal("{app}_20260629",
            CaptureFileNameService.RenderTemplate("{app}_{date}", Stamp));
    }

    [Fact]
    public void PathInvalidCharacters_AreSanitized()
    {
        // '/' is invalid in a file NAME on every platform; the sanitized result must not contain it.
        string result = CaptureFileNameService.RenderTemplate("a/b_{date}", Stamp);
        Assert.DoesNotContain("/", result);
        Assert.EndsWith("20260629", result);
    }

    [Fact]
    public void DegenerateTemplate_NeverReturnsEmpty()
    {
        // "..." trims to empty after sanitization (trailing dots are stripped) → falls back to default.
        Assert.Equal("GimmeCapture_20260629_143052",
            CaptureFileNameService.RenderTemplate("...", Stamp));
    }

    [Fact]
    public void BuildFileName_AppendsExtension_WithTemplate()
    {
        // BuildFileName uses the real clock, so only assert the shape, not the timestamp digits.
        string name = CaptureFileNameService.BuildFileName("png", "shot_{date}");
        Assert.StartsWith("shot_", name);
        Assert.EndsWith(".png", name);
    }
}
