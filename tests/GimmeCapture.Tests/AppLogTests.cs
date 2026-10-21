using System;
using GimmeCapture.Services.Core.Infrastructure;
using Xunit;

namespace GimmeCapture.Tests;

// A bug report carries the recent-error summary. Failures logged at Debug are expected ones (no CUDA, a video with no
// audio, cleanup a later sweep retries) and must not crowd real problems out of it.
public class AppLogTests
{
    [Fact]
    public void DebugFailures_StayOutOfTheErrorSummary()
    {
        string operation = "AppLogTests.Debug." + Guid.NewGuid().ToString("N");

        AppLog.Debug(operation, new InvalidOperationException("expected"));

        Assert.DoesNotContain(AppLog.GetAnonymousErrorSummary(20), line => line.Contains(operation));
    }

    [Fact]
    public void Warnings_GoIntoTheErrorSummary()
    {
        string operation = "AppLogTests.Warning." + Guid.NewGuid().ToString("N");

        AppLog.Warning(operation, new InvalidOperationException("unexpected"));

        Assert.Contains(AppLog.GetAnonymousErrorSummary(20), line => line.Contains(operation));
    }

    // 144 Debug.WriteLine traces became AppLog.Debug, some on pointer-move paths. Debug.WriteLine vanished from release
    // builds, arguments and all; with debug logging off, an interpolated message must cost as little.
    [Fact]
    public void AnInterpolatedDebugMessage_IsBuiltOnlyWhileDebugLoggingIsOn()
    {
        bool debugOn = Serilog.Log.IsEnabled(Serilog.Events.LogEventLevel.Debug);
        int evaluated = 0;
        string Touch() => (++evaluated).ToString();

        AppLog.Debug("AppLogTests.Lazy", $"value {Touch()}");

        Assert.Equal(debugOn ? 1 : 0, evaluated);
    }
}
