using System;
using System.Collections.Generic;
using System.Threading;
using Serilog;
using Serilog.Events;

namespace GimmeCapture.Services.Core.Infrastructure;

/// <summary>
/// What is logged while the logger is still being built (<see cref="AppLog.Initialize"/>), kept with the time it was
/// logged at and written, in order, once the logger is there. After that, nothing is kept: a caller whose event
/// <see cref="TryDefer"/> turns down writes it to the logger itself.
/// </summary>
internal sealed class PendingLogEvents
{
    private readonly object _gate = new();
    private List<Pending>? _events = [];

    internal PendingLogEvents(bool debugEnabled) => DebugEnabled = debugEnabled;

    /// <summary>Whether debug events are kept: the logger to come writes them only then.</summary>
    internal bool DebugEnabled { get; }

    internal bool IsReleased => Volatile.Read(ref _events) == null;

    /// <summary>
    /// Keeps the event until <see cref="Release"/>, or says it has been released: then the caller writes the event.
    /// </summary>
    internal bool TryDefer(LogEventLevel level, string template, object?[] values)
    {
        DateTimeOffset now = DateTimeOffset.Now;
        lock (_gate)
        {
            if (_events == null)
            {
                return false;
            }

            if (level >= LogEventLevel.Information || DebugEnabled)
            {
                _events.Add(new Pending(now, level, template, values));
            }

            return true;
        }
    }

    /// <summary>
    /// Writes what was kept to <paramref name="logger"/> and keeps nothing from then on. <paramref name="install"/>,
    /// which makes the logger the one everything else writes to, runs first under the same lock, so no event logged
    /// meanwhile can be written ahead of those kept.
    /// </summary>
    internal void Release(ILogger logger, Action install)
    {
        lock (_gate)
        {
            install();
            foreach (var pending in _events ?? [])
            {
                if (logger.IsEnabled(pending.Level)
                    && logger.BindMessageTemplate(
                        pending.Template, pending.Values, out var template, out var properties))
                {
                    logger.Write(new LogEvent(pending.Timestamp, pending.Level, null, template, properties));
                }
            }

            Volatile.Write(ref _events, null);
        }
    }

    private readonly record struct Pending(
        DateTimeOffset Timestamp, LogEventLevel Level, string Template, object?[] Values);
}
