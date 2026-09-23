using System;
using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.Tests;

/// <summary>
/// A failing command has to cost the user one action, not the app. Two things used to turn any command failure
/// into a crash: 89 call sites ran <c>Execute().Subscribe()</c> with no error handler (Rx rethrows OnError on the
/// UI thread), and ReactiveUI's default handler for commands nobody observes rethrows as well.
/// </summary>
public class CommandFailureHandlingTests
{
    private static ReactiveCommand<Unit, Unit> FailingCommand() =>
        ReactiveCommand.Create(
            () => throw new InvalidOperationException("the auto-save folder is read-only"),
            outputScheduler: ImmediateScheduler.Instance);

    [Fact]
    public void ABareSubscribeRethrowsTheFailure()
    {
        // The hazard itself, kept as a control: this is what every `Execute().Subscribe()` call site used to do.
        var command = FailingCommand();
        using var observed = command.ThrownExceptions.Subscribe(_ => { });

        Assert.ThrowsAny<Exception>(() => command.Execute().Subscribe());
    }

    [Fact]
    public void SubscribeLoggingErrorsKeepsTheFailureAtTheCallSite()
    {
        var command = FailingCommand();
        var reported = new List<Exception>();
        using var observed = command.ThrownExceptions.Subscribe(reported.Add);

        var failure = Record.Exception(() => command.Execute().SubscribeLoggingErrors());

        Assert.Null(failure);
        // Still reported through the command's own channel — handling it at the call site hides nothing.
        Assert.Single(reported);
    }

    [Fact]
    public void TheReporterLogsEveryUnobservedException()
    {
        var logged = new List<Exception>();
        var reporter = new UnhandledCommandExceptionReporter(logged.Add);

        var failure = new InvalidOperationException("unobserved");
        reporter.OnNext(failure);

        Assert.Same(failure, Assert.Single(logged));
    }

    [Fact]
    public void TheReporterTellsTheUserOnceTheUiIsAttached()
    {
        var notified = new List<Exception>();
        var reporter = new UnhandledCommandExceptionReporter(_ => { });
        reporter.AttachUserNotification(notified.Add);

        reporter.OnNext(new InvalidOperationException("after startup"));

        Assert.Single(notified);
    }

    [Fact]
    public void AFailingNotificationIsLoggedInsteadOfBecomingASecondCrash()
    {
        var logged = new List<Exception>();
        var reporter = new UnhandledCommandExceptionReporter(logged.Add);
        reporter.AttachUserNotification(_ => throw new InvalidOperationException("toast window already closed"));

        var failure = Record.Exception(() => reporter.OnNext(new InvalidOperationException("original")));

        Assert.Null(failure);
        Assert.Equal(2, logged.Count); // the original failure, then the one raised while reporting it
    }

    [Fact]
    public void OnErrorIsReportedLikeOnNext()
    {
        var logged = new List<Exception>();
        var reporter = new UnhandledCommandExceptionReporter(logged.Add);

        reporter.OnError(new InvalidOperationException("terminal"));

        Assert.Single(logged);
    }
}
