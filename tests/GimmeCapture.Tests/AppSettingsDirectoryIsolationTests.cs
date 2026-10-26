using System;
using System.IO;
using System.Threading.Tasks;

namespace GimmeCapture.Tests;

// The constructor that takes a directory is how tests and the designer preview get a settings service of their own;
// the app itself only ever uses the parameterless one. With that directory still empty, a load used to fall through
// FindConfigSource's chain to machine-wide locations and end on the developer's real
// %LOCALAPPDATA%\GimmeCapture\config.json. That leaked both ways. The load read the developer's own settings, so a
// "first run" came back in Chinese on a machine set to Chinese. And because that legacy source redirects saves to the
// running executable's versioned AppData folder, a test's next save wrote a config into the developer's real AppData:
// three instance folders there turned out to belong to the test runner. These pin the given directory as the whole
// answer. They can only show the bug on a machine that has a legacy config, since the data root is not redirectable
// and writing a fake legacy config into the real AppData would be the same damage on purpose.
public sealed class AppSettingsDirectoryIsolationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    public AppSettingsDirectoryIsolationTests() => Directory.CreateDirectory(_dir);

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

    // Checked before anything saves: a service that has been pointed elsewhere writes into the developer's real AppData
    // the moment it saves, which is the damage these tests exist to prevent, so they have to stop here first.
    private void AssertStillInTheGivenDirectoryWithDefaults(AppSettingsService service)
    {
        Assert.Equal(_dir, service.BaseDataDirectory);
        Assert.Equal(Language.English, service.Settings.Language);
    }

    [Fact]
    public async Task LoadAsync_OnAFirstRun_StaysInTheGivenDirectory_AndSavesThere()
    {
        var service = new AppSettingsService(_dir);

        await service.LoadAsync();
        AssertStillInTheGivenDirectoryWithDefaults(service);

        await service.SaveAsync();
        Assert.True(File.Exists(Path.Combine(_dir, "config.json")));
    }

    [Fact]
    public void LoadSync_OnAFirstRun_StaysInTheGivenDirectory()
    {
        var service = new AppSettingsService(_dir);

        service.LoadSync();

        AssertStillInTheGivenDirectoryWithDefaults(service);
    }
}
