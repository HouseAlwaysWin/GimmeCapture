using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Serilog;
using Serilog.Events;

namespace GimmeCapture.Services.Core.Infrastructure;

public static class AppLog
{
    private const long LogFileSizeLimitBytes = 20L * 1024 * 1024;

    /// <summary>Set to 1 before start-up to also write <see cref="Debug(string, string)"/> detail.</summary>
    public const string DebugLogVariable = "GIMMECAPTURE_DEBUG_LOG";
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
            .MinimumLevel.Is(Environment.GetEnvironmentVariable(DebugLogVariable) == "1" ? LogEventLevel.Debug : LogEventLevel.Information)
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

    /// <summary>
    /// Log an event that carries a message, such as a line of timings. Unlike <see cref="Information(string)"/>, whose
    /// event name is cut at 120 characters, the message is written whole.
    /// </summary>
    public static void Information(string operation, string message)
    {
        Log.Information("{Operation}: {Message}", SanitizeLabel(operation), message ?? "(none)");
    }

    /// <summary>
    /// Detail for chasing a problem, written only when <see cref="DebugLogVariable"/> is 1: an expected failure (no CUDA,
    /// a video with no audio) or cleanup that a later sweep retries. Debug.WriteLine, used for these before, does not
    /// exist in a release build at all.
    /// </summary>
    public static void Debug(string operation, string message)
    {
        Log.Debug("{Operation}: {Message}", SanitizeLabel(operation), message ?? "(none)");
    }

    /// <summary>
    /// <see cref="Debug(string, string)"/> for an interpolated message: with debug logging off, nothing in the message is
    /// evaluated or formatted, as with the Debug.WriteLine it replaces in a release build, so it is cheap on hot paths.
    /// </summary>
    public static void Debug(string operation, ref DebugMessageHandler message)
    {
        if (message.Enabled)
        {
            Debug(operation, message.ToStringAndClear());
        }
    }

    /// <inheritdoc cref="Debug(string, string)"/>
    public static void Debug(string operation, Exception exception)
    {
        Record(LogEventLevel.Debug, operation, exception);
    }

    /// <summary>Builds a debug message only when debug logging is on; see <see cref="Debug(string, ref DebugMessageHandler)"/>.</summary>
    [InterpolatedStringHandler]
    public ref struct DebugMessageHandler
    {
        private DefaultInterpolatedStringHandler _builder;

        public DebugMessageHandler(int literalLength, int formattedCount, out bool enabled)
        {
            enabled = Log.IsEnabled(LogEventLevel.Debug);
            Enabled = enabled;
            _builder = enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
        }

        internal bool Enabled { get; }

        public void AppendLiteral(string value) => _builder.AppendLiteral(value);

        public void AppendFormatted<T>(T value) => _builder.AppendFormatted(value);

        public void AppendFormatted<T>(T value, string? format) => _builder.AppendFormatted(value, format);

        public void AppendFormatted<T>(T value, int alignment) => _builder.AppendFormatted(value, alignment);

        public void AppendFormatted<T>(T value, int alignment, string? format) => _builder.AppendFormatted(value, alignment, format);

        public void AppendFormatted(string? value) => _builder.AppendFormatted(value);

        internal string ToStringAndClear() => _builder.ToStringAndClear();
    }

    public static void Warning(string operation, Exception exception)
    {
        Record(LogEventLevel.Warning, operation, exception);
    }

    /// <summary>
    /// Log a warning that carries a diagnostic MESSAGE (not an exception) — for failure sites that have a reason
    /// string but no exception object. Unlike the exception overload, the message text IS written to the sink
    /// (the exception overload records only the type/HResult/stack, never Exception.Message).
    /// </summary>
    public static void Warning(string operation, string message)
    {
        Log.Write(LogEventLevel.Warning, "{Operation}: {Message}", SanitizeLabel(operation), message ?? "(none)");
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

        // Only problems go into the summary a bug report carries; expected failures logged at Debug do not.
        if (level >= LogEventLevel.Warning)
        {
            RecentErrors.Enqueue($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} | {safeOperation} | {exceptionType}");
            while (RecentErrors.Count > 20)
            {
                RecentErrors.TryDequeue(out _);
            }
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
