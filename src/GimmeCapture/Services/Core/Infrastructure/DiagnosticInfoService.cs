using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed record ModuleDiagnostic(string Name, string Status);

public static class DiagnosticInfoService
{
    public static string Build(
        string version,
        IEnumerable<ModuleDiagnostic> modules,
        string renderer = "Avalonia/Skia (backend auto)")
    {
        var builder = new StringBuilder();
        builder.AppendLine("GimmeCapture diagnostics");
        builder.AppendLine($"Version: {version}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine($"Renderer: {renderer}");
        builder.AppendLine("Modules:");

        foreach (var module in modules.OrderBy(static item => item.Name, StringComparer.Ordinal))
        {
            builder.AppendLine($"- {module.Name}: {module.Status}");
        }

        builder.AppendLine("Recent anonymous errors:");
        var errors = AppLog.GetAnonymousErrorSummary();
        if (errors.Count == 0)
        {
            builder.AppendLine("- None");
        }
        else
        {
            foreach (var error in errors)
            {
                builder.AppendLine($"- {error}");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
