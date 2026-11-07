using System.Text.Json;

namespace GimmeCapture.Tests;

/// <summary>
/// The Windows build takes the culture's rules from Windows' own NLS, not from ICU (GimmeCapture.csproj): with ICU, the
/// first culture-sensitive casing of a run took 162 ms for zh-TW and the first culture search 20 ms, 186-188 ms and
/// 1.5 MB with a sort, against 3.4-3.6 ms with NLS. Windows head only: Linux has no NLS.
/// </summary>
public class GlobalizationModeTests
{
    [Fact]
    public void TheWindowsBuild_UsesNls()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "GimmeCapture.runtimeconfig.json");
        using var config = JsonDocument.Parse(File.ReadAllText(path));

        var properties = config.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.True(properties.GetProperty("System.Globalization.UseNls").GetBoolean());
    }
}
