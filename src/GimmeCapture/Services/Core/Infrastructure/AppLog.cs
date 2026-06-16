using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class AppLog
{
    private const long LogFileSizeLimitBytes = 20L * 1024 * 1024;
    private static readonly ConcurrentQueue<string> RecentErrors = new();
    private static int _initialized;

    public static string LogDirectory => Path.Combine(AppStoragePaths.GetRootDirectory(), "logs");

    public static void Initialize()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        Directory.CreateDirectory(LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Avalonia", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(LogDirectory, "gimmecapture-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(7),
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}")
            .CreateLogger();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Error("AppDomain.UnhandledException", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Error("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Information("Application.Started");
    }

    public static void Information(string eventName)
    {
        Log.Information("{EventName}", SanitizeLabel(eventName));
    }

    public static void Warning(string operation, Exception exception)
    {
        Record(LogEventLevel.Warning, operation, exception);
    }

    public static void Error(string operation, Exception exception)
    {
        Record(LogEventLevel.Error, operation, exception);
    }

    public static IReadOnlyList<string> GetAnonymousErrorSummary(int maximumCount = 10)
    {
        if (maximumCount <= 0)
        {
            return [];
        }

        return RecentErrors
            .Reverse()
            .Take(maximumCount)
            .Reverse()
            .ToArray();
    }

    public static void Shutdown()
    {
        if (System.Threading.Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return;
        }

        Information("Application.Stopped");
        Log.CloseAndFlush();
    }

    private static void Record(LogEventLevel level, string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string safeOperation = SanitizeLabel(operation);
        string exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        string summary = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {safeOperation} | {exceptionType}";

        RecentErrors.Enqueue(summary);
        while (RecentErrors.Count > 20)
        {
            RecentErrors.TryDequeue(out _);
        }

        Log.Write(
            level,
            "{Operation} failed; exceptionType={ExceptionType}; hresult={HResult}; stack={StackTrace}",
            safeOperation,
            exceptionType,
            exception.HResult,
            exception.StackTrace ?? "(none)");
    }

    private static string SanitizeLabel(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "Unknown";
        }

        var safe = value.Trim();
        return safe.Length <= 120 ? safe : safe[..120];
    }
}
