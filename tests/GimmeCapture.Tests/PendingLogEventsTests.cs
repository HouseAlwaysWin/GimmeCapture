using GimmeCapture.Services.Core.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace GimmeCapture.Tests;

/// <summary>
/// The log's first lines wait while the logger is built on a thread of its own (<see cref="AppLog.Initialize"/>): they
/// must come out as if the logger had been there, in order, at the time each was logged.
/// </summary>
public class PendingLogEventsTests
{
    private sealed class Collected : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (Logger Logger, Collected Sink) Logger(LogEventLevel minimum)
    {
        var sink = new Collected();
        return (new LoggerConfiguration().MinimumLevel.Is(minimum).WriteTo.Sink(sink).CreateLogger(), sink);
    }

    [Fact]
    public void WhatWasLoggedMeanwhile_IsWrittenInOrder_AtTheTimeItWasLogged()
    {
        var pending = new PendingLogEvents(debugEnabled: false);
        DateTimeOffset before = DateTimeOffset.Now;
        Assert.True(pending.TryDefer(LogEventLevel.Information, "{EventName}", ["Application.Started"]));
        Thread.Sleep(30);
        Assert.True(pending.TryDefer(LogEventLevel.Warning, "{Operation}: {Message}", ["Fonts.Inter", "none read"]));
        DateTimeOffset after = DateTimeOffset.Now;
        var (logger, sink) = Logger(LogEventLevel.Information);

        pending.Release(logger, () => { });

        Assert.Collection(
            sink.Events,
            first =>
            {
                Assert.Equal(LogEventLevel.Information, first.Level);
                Assert.Equal("\"Application.Started\"", first.RenderMessage());
                Assert.InRange(first.Timestamp, before, after);
            },
            second =>
            {
                Assert.Equal(LogEventLevel.Warning, second.Level);
                Assert.Equal("\"Fonts.Inter\": \"none read\"", second.RenderMessage());
                Assert.True(second.Timestamp >= sink.Events[0].Timestamp.AddMilliseconds(25));
            });
    }

    [Fact]
    public void OnceReleased_NothingIsKept_AndTheCallerWritesIt()
    {
        var pending = new PendingLogEvents(debugEnabled: false);
        var (logger, sink) = Logger(LogEventLevel.Information);
        pending.Release(logger, () => { });

        Assert.True(pending.IsReleased);
        Assert.False(pending.TryDefer(LogEventLevel.Information, "{EventName}", ["Later"]));
        Assert.Empty(sink.Events);
    }

    [Theory]
    [InlineData(false, LogEventLevel.Information, 0)]
    [InlineData(true, LogEventLevel.Debug, 1)]
    [InlineData(true, LogEventLevel.Information, 0)]
    public void DebugEvents_AreKeptOnlyForALoggerThatWritesThem(bool debugEnabled, LogEventLevel minimum, int written)
    {
        var pending = new PendingLogEvents(debugEnabled);
        Assert.True(pending.TryDefer(LogEventLevel.Debug, "{Operation}: {Message}", ["Ocr.Probe", "no CUDA"]));
        var (logger, sink) = Logger(minimum);

        pending.Release(logger, () => { });

        Assert.Equal(written, sink.Events.Count);
    }

    [Fact]
    public void TheLoggerIsInstalledBeforeAnyKeptEventIsWritten()
    {
        // Anything logged once the logger is installed goes to it straight away; were it installed after the kept
        // events, such a line could land ahead of lines logged before it.
        var pending = new PendingLogEvents(debugEnabled: false);
        pending.TryDefer(LogEventLevel.Information, "{EventName}", ["First"]);
        var (logger, sink) = Logger(LogEventLevel.Information);
        int writtenWhenInstalled = -1;

        pending.Release(logger, () => writtenWhenInstalled = sink.Events.Count);

        Assert.Equal(0, writtenWhenInstalled);
        Assert.Single(sink.Events);
    }

    [Fact]
    public void EventsLoggedFromManyThreadsWhileReleasing_AreEachWrittenOnce()
    {
        var pending = new PendingLogEvents(debugEnabled: false);
        var (logger, sink) = Logger(LogEventLevel.Information);
        var direct = new System.Collections.Concurrent.ConcurrentBag<int>();
        using var start = new ManualResetEventSlim();
        var writers = Enumerable.Range(0, 4).Select(t => new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < 500; i++)
            {
                int value = (t * 1000) + i;
                if (!pending.TryDefer(LogEventLevel.Information, "{Value}", [value]))
                {
                    direct.Add(value);
                }
            }
        })).ToArray();
        foreach (var writer in writers)
        {
            writer.Start();
        }

        start.Set();
        Thread.Sleep(5);
        lock (sink)
        {
            pending.Release(logger, () => { });
        }

        foreach (var writer in writers)
        {
            writer.Join();
        }

        var kept = sink.Events.Select(e => (int)((ScalarValue)e.Properties["Value"]).Value!).ToList();
        Assert.Equal(2000, kept.Count + direct.Count);
        Assert.Equal(kept.Count + direct.Count, kept.Concat(direct).Distinct().Count());
    }
}
