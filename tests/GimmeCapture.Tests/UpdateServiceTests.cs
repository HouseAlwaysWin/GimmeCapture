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
            @"C:\Temp\update\config.appdata.backup.json");

        Assert.Contains(@"if exist ""C:\Apps\GimmeCapture\config.json"" copy /y ""C:\Apps\GimmeCapture\config.json"" ""C:\Temp\update\config.local.backup.json"" > nul", script);
        Assert.Contains(@"if exist ""C:\Users\Test\AppData\Local\GimmeCapture\config.json"" copy /y ""C:\Users\Test\AppData\Local\GimmeCapture\config.json"" ""C:\Temp\update\config.appdata.backup.json"" > nul", script);
        Assert.Contains(@"if exist ""C:\Temp\update\config.local.backup.json"" copy /y ""C:\Temp\update\config.local.backup.json"" ""C:\Apps\GimmeCapture\config.json"" > nul", script);
        Assert.Contains(@"copy /y ""C:\Temp\update\config.appdata.backup.json"" ""C:\Users\Test\AppData\Local\GimmeCapture\config.json"" > nul", script);
    }
}
