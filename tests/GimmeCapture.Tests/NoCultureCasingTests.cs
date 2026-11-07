using System.Text.RegularExpressions;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

/// <summary>
/// A culture's ToUpper() or ToLower(), the ones that take no argument, sets up ICU's casing for the UI language the
/// first time a run uses it: 162 ms for zh-TW in a fresh process, against 0.8 ms through Windows' own NLS, even for
/// "ABC". The capture overlay's keyboard hook did it for the first letter key of a run, on the thread that holds every
/// key typed anywhere until it returns. The app's casing is of names, keys and extensions: invariant or ordinal.
/// </summary>
public class NoCultureCasingTests
{
    private static readonly Regex CultureCasing = new(@"\.To(Upper|Lower)\(\s*\)", RegexOptions.Compiled);

    [Fact]
    public void TheAppNeverCasesTextInTheUserCulture()
    {
        var offenders = RepositoryFiles.AppFiles("*.cs")
            .SelectMany(path => File.ReadLines(path).Select((line, index) => (path, line, index)))
            .Where(entry => CultureCasing.IsMatch(entry.line))
            .Select(entry => $"{RepositoryFiles.Relative(entry.path)}:{entry.index + 1}: {entry.line.Trim()}")
            .ToList();

        Assert.True(offenders.Count == 0, "Use ToUpperInvariant/ToLowerInvariant:\n" + string.Join("\n", offenders));
    }

    [Theory]
    [InlineData(0x41, "A")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x30, "0")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x1B, "Escape")]
    [InlineData(0xA2, "Ctrl")]
    [InlineData(0xE8, "Unknown")]
    public void TheOverlaysKeyNames_StayAsTheyWere(int virtualKey, string name)
    {
        Assert.Equal(name, SnipWindow.GetKeyStringFromVirtualKey(virtualKey));
    }
}
