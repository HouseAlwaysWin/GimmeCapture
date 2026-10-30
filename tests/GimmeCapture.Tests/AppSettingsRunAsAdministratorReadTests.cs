using System;
using System.IO;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// Start-up decides on this one setting before any window exists, to hand over to an elevated copy (ElevatedLaunch): from
// the read Main began, or from a read of the one value when that has not finished in time.
public sealed class AppSettingsRunAsAdministratorReadTests : IDisposable
{
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsRunAsAdministratorReadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void WriteConfig(string json) => File.WriteAllText(Path.Combine(_dir, "config.json"), json);

    private bool Peek(string json)
    {
        WriteConfig(json);
        return new AppSettingsService(_dir).PeekSavedRunAsAdministrator();
    }

    [Fact]
    public void TheBackgroundRead_HasIt_BeforeTheSettingsLoad()
    {
        WriteConfig($$"""{ "ConfigVersion": {{AppSettingsService.CurrentConfigVersion}}, "RunAsAdministrator": true }""");
        var service = new AppSettingsService(_dir);
        service.StartReadingInBackground();

        Assert.True(service.TryGetReadRunAsAdministrator(ReadWait, out bool runAsAdministrator));
        Assert.True(runAsAdministrator);
        Assert.False(service.Settings.RunAsAdministrator);
    }

    [Fact]
    public void WithoutABackgroundRead_TheOneValueIsReadInstead()
    {
        WriteConfig($$"""{ "ConfigVersion": {{AppSettingsService.CurrentConfigVersion}}, "RunAsAdministrator": true }""");
        var service = new AppSettingsService(_dir);

        Assert.False(service.TryGetReadRunAsAdministrator(TimeSpan.Zero, out _));
        Assert.True(service.PeekSavedRunAsAdministrator());
    }

    [Fact]
    public void ReadsTheSavedSetting()
    {
        Assert.True(Peek("""{ "RunAsAdministrator": true, "SnipHotkey": "Shift+F1" }"""));
    }

    [Theory]
    [InlineData("""{ "RunAsAdministrator": false }""")]
    [InlineData("{}")]
    [InlineData("""{ "RunAsAdministrator": "true" }""")]
    [InlineData("not json")]
    public void IsFalse_UnlessTheConfigSaysTrue(string json)
    {
        Assert.False(Peek(json));
    }

    [Fact]
    public void IsFalse_OnAFirstRun()
    {
        Assert.False(new AppSettingsService(_dir).PeekSavedRunAsAdministrator());
        var service = new AppSettingsService(_dir);
        service.StartReadingInBackground();
        Assert.True(service.TryGetReadRunAsAdministrator(ReadWait, out bool runAsAdministrator));
        Assert.False(runAsAdministrator);
    }

    [Fact]
    public async Task ReadsWhatASaveWrote()
    {
        var writer = new AppSettingsService(_dir);
        writer.Settings.RunAsAdministrator = true;
        await writer.SaveAsync();

        Assert.True(new AppSettingsService(_dir).PeekSavedRunAsAdministrator());
    }
}
