using GimmeCapture.Services.Interop;

namespace GimmeCapture.Tests;

public class PowerShellClipboardCommandTests
{
    [Fact]
    public void PlainPath_IsSingleQuoted()
    {
        var args = PowerShellClipboardCommand.BuildSetClipboardArguments(@"C:\videos\clip.mp4");
        Assert.Contains(@"Set-Clipboard -Path 'C:\videos\clip.mp4'", args);
    }

    [Fact]
    public void PathWithApostrophe_IsDoubled_NotBrokenOut()
    {
        // A quote in the filename would otherwise terminate the single-quoted string and inject PowerShell.
        var args = PowerShellClipboardCommand.BuildSetClipboardArguments(@"C:\Bob's clips\a'b.mp4");
        Assert.Contains(@"'C:\Bob''s clips\a''b.mp4'", args);
        // The raw (un-doubled) quote must not appear followed by a string terminator.
        Assert.DoesNotContain("a'b.mp4'\"", args);
    }

    [Fact]
    public void InjectionAttempt_StaysInsideTheQuotedLiteral()
    {
        var args = PowerShellClipboardCommand.BuildSetClipboardArguments("x'; Remove-Item C:\\ -Recurse #");
        // The closing quote of the injection is doubled, so it cannot end the -Path literal.
        Assert.Contains("'x''; Remove-Item C:\\ -Recurse #'", args);
    }

    [Fact]
    public void Null_ProducesEmptyQuotedPath()
    {
        var args = PowerShellClipboardCommand.BuildSetClipboardArguments(null!);
        Assert.Contains("Set-Clipboard -Path ''", args);
    }
}
