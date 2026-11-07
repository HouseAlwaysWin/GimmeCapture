using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
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

    // While the logger is being built: what is logged meanwhile (PendingLogEvents). Null before Initialize and after.
    private static PendingLogEvents? _pending;
    private static ManualResetEventSlim? _built;
    private static long _buildMilliseconds = -1;

    // Waited for before the process ends, so what it logged last is in the file.
    private static readonly TimeSpan BuildWait = TimeSpan.FromSeconds(10);

    public static string LogDirectory => Path.Combine(AppStoragePaths.GetRootDirectory(), "logs");

    /// <summary>
    /// Starts the log. The logger is built on a thread of its own: built in Main, first thing, it held up everything
    /// after it, 61-96 ms of the installed copy's recent starts (Startup.Timings' <c>log</c> stage), 302-596 ms at
    /// earlier sign-ins. In a ReadyToRun probe it took 42 ms, 16 of them because the daily file sink compiles a
    /// regular expression to IL. What is logged meanwhile waits (<see cref="PendingLogEvents"/>) and is written with
    /// the time it was logged at, in order.
    /// </summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0)
        {
            return;
        }

        var pending = new PendingLogEvents(Environment.GetEnvironmentVariable(DebugLogVariable) == "1");
        var built = new ManualResetEventSlim();
        _built = built;
        Volatile.Write(ref _pending, pending);
        new Thread(() => Build(pending, built)) { IsBackground = true, Name = "AppLog.Build" }.Start();

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception exception)
            {
                Error("AppDomain.UnhandledException", exception);
                // The process ends after this: the logger it may still be waiting for writes this too.
                WaitForLogger();
            }
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Error("TaskScheduler.UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        Information("Application.Started");
    }

    /// <summary>How long the logger took to build, for Startup.Timings: " logger=42ms" or " logger=building".</summary>
    internal static string DescribeBuild()
    {
        long ms = Interlocked.Read(ref _buildMilliseconds);
        return ms >= 0 ? $" logger={ms}ms" : _built == null ? string.Empty : " logger=building";
    }

    private static void Build(PendingLogEvents pending, ManualResetEventSlim built)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        ILogger logger;
        try
        {
            Directory.CreateDirectory(LogDirectory);
            logger = new LoggerConfiguration()
                .MinimumLevel.Is(pending.DebugEnabled ? LogEventLevel.Debug : LogEventLevel.Information)
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
        }
        catch (Exception)
        {
            // No log rather than no app: an exception here ends the process, as one in Main ended the start before.
            logger = Serilog.Core.Logger.None;
        }

        pending.Release(logger, () => Log.Logger = logger);
        Volatile.Write(ref _pending, null);
        long took = (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Interlocked.Exchange(ref _buildMilliseconds, took);
        built.Set();
    }

    private static void WaitForLogger() => _built?.Wait(BuildWait);

    // Keeps the event while the logger is built; false once it is, and the caller writes it.
    private static bool Deferred(LogEventLevel level, string template, object?[] values) =>
        Volatile.Read(ref _pending) is { } pending && pending.TryDefer(level, template, values);

    public static void Information(string eventName)
    {
        string label = SanitizeLabel(eventName);
        if (_pending != null && Deferred(LogEventLevel.Information, "{EventName}", [label]))
        {
            return;
        }

        Log.Information("{EventName}", label);
    }

    /// <summary>
    /// Log an event that carries a message, such as a line of timings. Unlike <see cref="Information(string)"/>, whose
    /// event name is cut at 120 characters, the message is written whole.
    /// </summary>
    public static void Information(string operation, string message)
    {
        string label = SanitizeLabel(operation);
        if (_pending != null
            && Deferred(LogEventLevel.Information, "{Operation}: {Message}", [label, message ?? "(none)"]))
        {
            return;
        }

        Log.Information("{Operation}: {Message}", label, message ?? "(none)");
    }

    /// <summary>
    /// Detail for chasing a problem, written only when <see cref="DebugLogVariable"/> is 1: an expected failure (no CUDA,
    /// a video with no audio) or cleanup that a later sweep retries. Debug.WriteLine, used for these before, does not
    /// exist in a release build at all.
    /// </summary>
    public static void Debug(string operation, string message)
    {
        string label = SanitizeLabel(operation);
        if (_pending != null && Deferred(LogEventLevel.Debug, "{Operation}: {Message}", [label, message ?? "(none)"]))
        {
            return;
        }

        Log.Debug("{Operation}: {Message}", label, message ?? "(none)");
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
            // While the logger is built, the setting it will be built with.
            enabled = Volatile.Read(ref _pending) is { } pending
                ? pending.DebugEnabled
                : Log.IsEnabled(LogEventLevel.Debug);
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
        string label = SanitizeLabel(operation);
        if (_pending != null && Deferred(LogEventLevel.Warning, "{Operation}: {Message}", [label, message ?? "(none)"]))
        {
            return;
        }

        Log.Write(LogEventLevel.Warning, "{Operation}: {Message}", label, message ?? "(none)");
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
        if (Interlocked.Exchange(ref _initialized, 0) == 0)
        {
            return;
        }

        WaitForLogger();
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

        const string template =
            "{Operation} failed; exceptionType={ExceptionType}; hresult={HResult}; stack={StackTrace}";
        string stack = exception.StackTrace ?? "(none)";
        if (_pending != null && Deferred(level, template, [safeOperation, exceptionType, exception.HResult, stack]))
        {
            return;
        }

        Log.Write(level, template, safeOperation, exceptionType, exception.HResult, stack);
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
