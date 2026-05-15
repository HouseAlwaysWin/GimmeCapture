using System.Reflection;

namespace GimmeCapture.Composition;

public static class AppVersionInfo
{
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
}
