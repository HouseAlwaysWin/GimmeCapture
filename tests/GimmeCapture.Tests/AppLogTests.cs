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
}
