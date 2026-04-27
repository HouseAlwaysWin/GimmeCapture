using Avalonia;
using System;
using Avalonia.ReactiveUI;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture;

class Program
{
    /// <summary>Command-line arguments passed to <see cref="Main"/> (e.g. <c>--startup</c> from Windows Run).</summary>
    public static string[] CommandLineArgs { get; private set; } = [];

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        CommandLineArgs = args ?? [];
        try
        {
            // Ensure Working Directory is correct (Fix for Auto-Start)
            var exeDir = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrEmpty(exeDir))
                System.IO.Directory.SetCurrentDirectory(exeDir);

            _ = FFmpegRuntime.TryInitialize(out var ffErr);
            if (!string.IsNullOrEmpty(ffErr))
            {
                System.Diagnostics.Debug.WriteLine($"[FFmpeg] {ffErr}");
            }

            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args ?? []);
        }
        catch (Exception ex)
        {
            // Startup failure can happen before Avalonia UI is fully available,
            // so keep the fatal prompt on a platform-native dialog.
            var message = $"Application Startup Failed:\n{ex.Message}\n\nStack:\n{ex.StackTrace}";
            System.Windows.Forms.MessageBox.Show(message, "GimmeCapture Fatal Error");
            System.Diagnostics.Debug.WriteLine($"FATAL: {ex}");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
