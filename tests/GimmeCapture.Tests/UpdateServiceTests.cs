using System.Net;
using System.Net.Http;
using GimmeCapture.Services.Abstractions;
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
        Assert.Contains(@"app-backup", script);
        Assert.Contains(@"robocopy ""C:\Temp\extract"" ""C:\Apps\GimmeCapture"" /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP > nul", script);
        Assert.Contains(@":rollback", script);
        Assert.Contains(@"/MIR /R:2 /W:1", script);
        Assert.Contains(@"pending-update.json", script);
        Assert.Contains(@"type nul > ""C:\Temp\update\config.appdata.exists.marker""", script);
        Assert.Contains(@"copy /y ""C:\Temp\update\config.appdata.backup.json"" ""C:\Users\Test\AppData\Local\GimmeCapture\instances\abcd1234\versions\0.29.0\config.json"" > nul", script);
        Assert.Contains(@"if not exist ""C:\Apps\GimmeCapture\GimmeCapture.exe"" goto rollback", script);
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
    public void BuildLinuxUpdateScript_WaitsSwapsMigratesRelaunches_AndRollsBack()
    {
        var script = UpdateService.BuildLinuxUpdateScript(
            currentProcessId: 4242,
            newExePath: "/tmp/dl/extract/GimmeCapture",
            currentExePath: "/opt/GimmeCapture/GimmeCapture",
            backupExePath: "/tmp/dl/GimmeCapture.backup",
            parentDir: "/tmp/dl",
            sourceConfigPath: "/home/u/.local/share/GimmeCapture/instances/ab/versions/0.50.0/config.json",
            targetConfigPath: "/home/u/.local/share/GimmeCapture/instances/ab/versions/0.51.0/config.json",
            pendingUpdateStatePath: "/home/u/.local/share/GimmeCapture/instances/ab/pending-update.json");

        // Waits for the old process, swaps the binary, migrates config, relaunches detached.
        Assert.Contains("kill -0 4242", script);
        // Backup is guarded: a failed backup aborts the swap instead of risking an unrecoverable state.
        Assert.Contains("if ! cp -f '/opt/GimmeCapture/GimmeCapture' '/tmp/dl/GimmeCapture.backup'; then", script);
        // Config dir creation is guarded so a mkdir failure doesn't lead to a silent half-migration.
        Assert.Contains("if mkdir -p '/home/u/.local/share/GimmeCapture/instances/ab/versions/0.51.0' 2>/dev/null; then", script);
        Assert.Contains("if cp -f '/tmp/dl/extract/GimmeCapture' '/opt/GimmeCapture/GimmeCapture'; then", script);
        Assert.Contains("mkdir -p '/home/u/.local/share/GimmeCapture/instances/ab/versions/0.51.0'", script);
        Assert.Contains("cp -f '/home/u/.local/share/GimmeCapture/instances/ab/versions/0.50.0/config.json' '/home/u/.local/share/GimmeCapture/instances/ab/versions/0.51.0/config.json'", script);
        Assert.Contains("setsid '/opt/GimmeCapture/GimmeCapture'", script);
        Assert.Contains("rm -rf '/tmp/dl'", script);
        // Rollback branch restores the backup and clears the pending state so startup does not flag a failure.
        Assert.Contains("cp -f '/tmp/dl/GimmeCapture.backup' '/opt/GimmeCapture/GimmeCapture'", script);
        Assert.Contains("rm -f '/home/u/.local/share/GimmeCapture/instances/ab/pending-update.json'", script);
    }

    [Fact]
    public void BuildLinuxUpdateScript_SingleQuotesPathsWithSpaces()
    {
        var script = UpdateService.BuildLinuxUpdateScript(
            1, "/tmp/a b/new/GimmeCapture", "/opt/My Apps/GimmeCapture", "/tmp/a b/bak",
            "/tmp/a b", "/cfg/src.json", "/cfg/dst.json", "/state/pending.json");

        // A space-containing app path stays a single shell token thanks to single-quoting.
        Assert.Contains("'/opt/My Apps/GimmeCapture'", script);
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

    [Fact]
    public void ParseChecksum_ReturnsHashForExactAsset()
    {
        var hash = new string('a', 64);
        var manifest = $"{hash}  GimmeCapture_win-x64.zip{Environment.NewLine}";

        Assert.Equal(hash, UpdateService.ParseChecksum(manifest, "GimmeCapture_win-x64.zip"));
        Assert.Null(UpdateService.ParseChecksum(manifest, "other.zip"));
    }

    [Fact]
    public async Task CancelDownload_CancelsActiveVerifiedArtifactDownload()
    {
        var hash = new string('a', 64);
        // Manifest carries both platform artifacts — GetPreferredZipAsset picks the win zip on the
        // Windows head and the linux tarball on the net10.0 (Linux CI) head.
        using var client = new HttpClient(new StaticResponseHandler(
            $"{hash}  GimmeCapture_win-x64.zip{Environment.NewLine}" +
            $"{hash}  GimmeCapture_linux-x64.tar.gz{Environment.NewLine}"));
        var downloader = new BlockingArtifactDownloader();
        var service = new UpdateService("0.40.0", client, downloader);
        var release = CreateRelease("v0.41.0", hasZip: true);
        release.Assets.Add(new ReleaseAsset
        {
            Name = "SHA256SUMS.txt",
            DownloadUrl = "https://example.com/SHA256SUMS.txt",
            Size = hash.Length
        });

        var downloadTask = service.DownloadUpdateAsync(release);
        await downloader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        service.CancelDownload();
        string? result = await downloadTask;

        Assert.Null(result);
        Assert.Equal(ArtifactDownloadStage.Cancelled, service.DownloadStage);
        Assert.False(service.IsDownloading);
    }

    private static ReleaseInfo CreateRelease(string tagName, bool hasZip, bool prerelease = false, bool draft = false)
    {
        return new ReleaseInfo
        {
            TagName = tagName,
            Prerelease = prerelease,
            Draft = draft,
            // Real releases ship both platform artifacts; including both keeps these tests meaningful
            // on the net10.0 (Linux) head, where GetPreferredZipAsset selects the tarball instead.
            Assets = hasZip
                ? new List<ReleaseAsset>
                {
                    new() { Name = "GimmeCapture_win-x64.zip", DownloadUrl = "https://example.com/test.zip" },
                    new() { Name = "GimmeCapture_linux-x64.tar.gz", DownloadUrl = "https://example.com/test.tar.gz" }
                }
                : new List<ReleaseAsset>()
        };
    }

    private sealed class StaticResponseHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content)
            });
    }

    private sealed class BlockingArtifactDownloader : IArtifactDownloader
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> DownloadAsync(
            ArtifactDescriptor descriptor,
            string destinationDirectory,
            IProgress<ArtifactDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            Started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return string.Empty;
        }
    }
}
