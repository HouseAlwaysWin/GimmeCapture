using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using GimmeCapture.Composition;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// The Windows build picks its renderer as Avalonia's Win32 platform starts, before the app exists, from the saved setting:
// from the read Main began when that has finished, otherwise from that one property of the config.
public sealed class StartupRendererTests : IDisposable
{
    private static readonly TimeSpan ReadWait = TimeSpan.FromSeconds(30);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public StartupRendererTests() => Directory.CreateDirectory(_dir);

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

    private static readonly string SoftwareConfig =
        $$"""{ "ConfigVersion": {{AppSettingsService.CurrentConfigVersion}}, "SoftwareRendering": true }""";

    [Fact]
    public void FromTheBackgroundRead_OnceItHasFinished()
    {
        WriteConfig(SoftwareConfig);
        var service = new AppSettingsService(_dir);
        service.StartReadingInBackground();
        Assert.True(service.TryGetReadSoftwareRendering(ReadWait, out _));

        Assert.True(StartupRenderer.ReadSetting(service));
        Assert.False(service.Settings.SoftwareRendering);
    }

    [Fact]
    public void FromTheConfig_WhenNoReadHasFinished()
    {
        WriteConfig(SoftwareConfig);
        var service = new AppSettingsService(_dir);
        Assert.False(service.TryGetReadSoftwareRendering(TimeSpan.Zero, out _));

        Assert.True(StartupRenderer.ReadSetting(service));
    }

    [Theory]
    [InlineData("""{ "SoftwareRendering": false }""")]
    [InlineData("{}")]
    [InlineData("""{ "SoftwareRendering": "true" }""")]
    [InlineData("not json")]
    public void TheGpu_UnlessTheConfigSaysTrue(string json)
    {
        WriteConfig(json);

        Assert.False(StartupRenderer.ReadSetting(new AppSettingsService(_dir)));
    }

    [Fact]
    public void TheGpu_OnAFirstRun()
    {
        Assert.False(StartupRenderer.ReadSetting(new AppSettingsService(_dir)));

        var service = new AppSettingsService(_dir);
        service.StartReadingInBackground();
        Assert.True(service.TryGetReadSoftwareRendering(ReadWait, out _));
        Assert.False(StartupRenderer.ReadSetting(service));
    }

    [Fact]
    public async Task ReadsWhatASaveWrote()
    {
        var writer = new AppSettingsService(_dir);
        writer.Settings.SoftwareRendering = true;
        await writer.SaveAsync();

        Assert.True(StartupRenderer.ReadSetting(new AppSettingsService(_dir)));
    }

    [Fact]
    public void Software_IsTheCpuAlone_AndTheGpu_IsAvaloniasDefault()
    {
        Assert.Equal([Win32RenderingMode.Software], StartupRenderer.Win32Options(software: true).RenderingMode);

        var gpu = StartupRenderer.Win32Options(software: false);
        var defaults = new Win32PlatformOptions();
        Assert.Equal(Win32RenderingMode.AngleEgl, gpu.RenderingMode[0]);
        Assert.Equal(defaults.RenderingMode, gpu.RenderingMode);
        Assert.Equal(defaults.CompositionMode, gpu.CompositionMode);
    }
}
