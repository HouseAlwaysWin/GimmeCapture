using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class UpdateServiceTests
{
    [Fact]
    public void BuildUpdateScript_BacksUpAndRestores_UserConfigFiles()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\extract",
            @"C:\Apps\GimmeCapture",
            @"C:\Temp\update",
            "GimmeCapture.exe",
            @"C:\Apps\GimmeCapture\config.json",
            @"C:\Users\Test\AppData\Local\GimmeCapture\config.json",
            @"C:\Temp\update\config.local.backup.json",
            @"C:\Temp\update\config.appdata.backup.json",
            @"C:\Temp\update\config.local.exists.marker",
            @"C:\Temp\update\config.appdata.exists.marker");

        Assert.Contains(@"type nul > ""C:\Temp\update\config.local.exists.marker""", script);
        Assert.Contains(@"type nul > ""C:\Temp\update\config.appdata.exists.marker""", script);
        Assert.Contains(@"copy /y ""C:\Temp\update\config.local.backup.json"" ""C:\Apps\GimmeCapture\config.json"" > nul", script);
        Assert.Contains(@"copy /y ""C:\Temp\update\config.appdata.backup.json"" ""C:\Users\Test\AppData\Local\GimmeCapture\config.json"" > nul", script);
    }

    [Fact]
    public void BuildUpdateScript_RemovesFreshLocalConfig_WhenOriginalConfigWasOnlyInAppData()
    {
        var script = UpdateService.BuildUpdateScript(
            @"C:\Temp\extract",
            @"C:\Apps\GimmeCapture",
            @"C:\Temp\update",
            "GimmeCapture.exe",
            @"C:\Apps\GimmeCapture\config.json",
            @"C:\Users\Test\AppData\Local\GimmeCapture\config.json",
            @"C:\Temp\update\config.local.backup.json",
            @"C:\Temp\update\config.appdata.backup.json",
            @"C:\Temp\update\config.local.exists.marker",
            @"C:\Temp\update\config.appdata.exists.marker");

        Assert.Contains(@"if exist ""C:\Temp\update\config.local.exists.marker"" (", script);
        Assert.Contains(@") else if exist ""C:\Temp\update\config.appdata.exists.marker"" (", script);
        Assert.Contains(@"if exist ""C:\Apps\GimmeCapture\config.json"" del /f /q ""C:\Apps\GimmeCapture\config.json""", script);
    }
}
