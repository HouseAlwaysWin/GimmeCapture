using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// Removing another copy's data deletes settings and modules that copy may still need, so it only ever happens after
// the user says yes, and never to a copy that is running.
public sealed class OtherInstallsViewModelTests : IDisposable
{
    private const string CurrentKey = "0123456789abcdef";
    private const string OldKey = "aaaaaaaaaaaaaaaa";
    private const string RunningKey = "bbbbbbbbbbbbbbbb";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));
    private readonly string _goneExe;
    private string? _running = RunningKey;
    private bool _answer;
    private string? _notified;

    public OtherInstallsViewModelTests()
    {
        _goneExe = Path.Combine(_root, "gone", "GimmeCapture.exe");
        string old = Path.Combine(Instances, OldKey);
        Write(Path.Combine(old, "shared", "AI", "models", "u2net.onnx"), 30);
        InstallInstanceRecord.Write(old, new InstallInstanceRecord(_goneExe, "0.69.0", new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc)));
        Write(Path.Combine(Instances, RunningKey, "versions", "0.68.1", "config.json"), 10);
    }

    private string Instances => Path.Combine(_root, "instances");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static void Write(string path, int length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
    }

    private OtherInstallsViewModel Create()
    {
        var localization = new Mock<ILocalizationService>();
        localization.Setup(l => l[It.IsAny<string>()]).Returns((string key) => key switch
        {
            "OtherInstallsSummary" => "{0} copies, {1}",
            "OtherInstallGoneLocation" => "{0} (gone)",
            "OtherInstallLastUsed" => "used {0}",
            "OtherInstallVersion" => "v{0}",
            "ConfirmRemoveOtherInstall" => "remove {0}, {1}?",
            "OtherInstallRemoveFailed" => "failed: {0}",
            _ => $"[{key}]",
        });

        return new OtherInstallsViewModel(
            Instances,
            CurrentKey,
            key => key == _running,
            (_, _) => Task.FromResult(_answer),
            (_, message) =>
            {
                _notified = message;
                return Task.CompletedTask;
            },
            localization.Object);
    }

    [Fact]
    public async Task Refresh_ListsTheOtherCopies_AndOffersRemovalOnlyForThoseNotRunning()
    {
        var vm = Create();

        await vm.RefreshAsync();

        // The model plus the record the copy wrote of itself.
        long oldSize = new DirectoryInfo(Path.Combine(Instances, OldKey))
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);
        Assert.True(vm.HasAny);
        Assert.Equal($"2 copies, {FileSizeFormatter.Format(oldSize + 10)}", vm.Summary);
        var old = vm.Items[0];
        Assert.Equal(Path.GetDirectoryName(_goneExe) + " (gone)", old.Location);
        Assert.Equal(
            $"{FileSizeFormatter.Format(oldSize)} · used {new DateTime(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc).ToLocalTime():yyyy-MM-dd} · v0.69.0",
            old.Details);
        Assert.True(old.CanRemove);
        var running = vm.Items[1];
        Assert.Equal("[OtherInstallUnknownLocation]", running.Location);
        Assert.EndsWith("· v0.68.1 · [OtherInstallRunning]", running.Details);
        Assert.False(running.CanRemove);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Removing_HappensOnlyOnceTheUserSaysYes(bool answer)
    {
        var vm = Create();
        await vm.RefreshAsync();
        _answer = answer;

        vm.Items[0].RemoveCommand.Execute(null);
        await WaitUntil(() => vm.Items.Count == (answer ? 1 : 2) && Directory.Exists(Path.Combine(Instances, OldKey)) != answer);

        Assert.Equal(!answer, Directory.Exists(Path.Combine(Instances, OldKey)));
        Assert.Null(_notified);
    }

    [Fact]
    public async Task ACopyThatStartedRunningSinceTheListWasDrawn_IsKept_AndTheUserIsTold()
    {
        _running = null;
        var vm = Create();
        await vm.RefreshAsync();
        Assert.True(vm.Items[1].CanRemove);
        _running = RunningKey;
        _answer = true;

        vm.Items[1].RemoveCommand.Execute(null);
        await WaitUntil(() => _notified != null);

        Assert.True(Directory.Exists(Path.Combine(Instances, RunningKey)));
        Assert.Equal("failed: [OtherInstallRunning]", _notified);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }
}
