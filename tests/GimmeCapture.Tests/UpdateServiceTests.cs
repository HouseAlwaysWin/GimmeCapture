using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void BuildUpdateScript_BacksUpAndRestores_VersionedUserConfigFiles()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\extract",
            @"C:\Apps\GimmeCapture",
            @"C:\Temp\update",
            @"C:\Apps\GimmeCapture\GimmeCapture.exe",
            12345,
            @"C:\Users\Test\AppData\Local\GimmeCapture\config.json",
            @"C:\Users\Test\AppData\Local\GimmeCapture\instances\abcd1234\versions\0.29.0\config.json",
            @"C:\Temp\update\config.appdata.backup.json",
            @"C:\Temp\update\config.appdata.exists.marker");

        Assert.Contains(@"tasklist /FI ""PID eq 12345"" | find ""12345"" > nul", script);
        Assert.Contains(@":copy_retry", script);
        Assert.Contains(@"robocopy ""C:\Temp\extract"" ""C:\Apps\GimmeCapture"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul", script);
        Assert.Contains(@"type nul > ""C:\Temp\update\config.appdata.exists.marker""", script);
        Assert.Contains(@"copy /y ""C:\Temp\update\config.appdata.backup.json"" ""C:\Users\Test\AppData\Local\GimmeCapture\instances\abcd1234\versions\0.29.0\config.json"" > nul", script);
        Assert.Contains(@"if not exist ""C:\Apps\GimmeCapture\GimmeCapture.exe"" exit /b 1", script);
        Assert.Contains(@"start """" ""C:\Apps\GimmeCapture\GimmeCapture.exe""", script);
    }

    [Fact]
    public void BuildUpdateScript_DoesNotTouch_LocalConfigDuringRestore()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\extract",
            @"C:\Apps\GimmeCapture",
            @"C:\Temp\update",
            @"C:\Apps\GimmeCapture\GimmeCapture.exe",
            12345,
            @"C:\Users\Test\AppData\Local\GimmeCapture\config.json",
            @"C:\Users\Test\AppData\Local\GimmeCapture\instances\abcd1234\versions\0.29.0\config.json",
            @"C:\Temp\update\config.appdata.backup.json",
            @"C:\Temp\update\config.appdata.exists.marker");

        Assert.DoesNotContain(@"config.local.backup.json", script);
        Assert.DoesNotContain(@"config.local.exists.marker", script);
        Assert.DoesNotContain(@"C:\Apps\GimmeCapture\config.json", script);
    }

    [Fact]
    public void FilterAndSortReleases_Excludes_DraftsPrereleases_And_SortsByVersion()
    {
        var releases = new[]
        {
            CreateRelease("v0.25.0", hasZip: true),
            CreateRelease("v0.26.0", hasZip: true),
            CreateRelease("v0.24.0", hasZip: true, prerelease: true),
            CreateRelease("v0.27.0", hasZip: false),
            CreateRelease("v0.23.0", hasZip: true, draft: true)
        };

        var filtered = UpdateService.FilterAndSortReleases(releases);

        Assert.Collection(
            filtered,
            release => Assert.Equal("v0.26.0", release.TagName),
            release => Assert.Equal("v0.25.0", release.TagName));
    }

    private static ReleaseInfo CreateRelease(string tagName, bool hasZip, bool prerelease = false, bool draft = false)
    {
        return new ReleaseInfo
        {
            TagName = tagName,
            Prerelease = prerelease,
            Draft = draft,
            Assets = hasZip
                ? new List<ReleaseAsset> { new() { Name = "GimmeCapture_win-x64.zip", DownloadUrl = "https://example.com/test.zip" } }
                : new List<ReleaseAsset>()
        };
    }
}
