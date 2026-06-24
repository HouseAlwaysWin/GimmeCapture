using GimmeCapture.Services.Translation;

namespace GimmeCapture.Tests;

public sealed class TranslationExecutionHelperTests
{
    private static readonly TimeSpan LongTimeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task ExecuteAsync_ReturnsActionResult_OnSuccess()
    {
        string result = await TranslationExecutionHelper.ExecuteAsync(
            _ => Task.FromResult("ok"),
            CancellationToken.None,
            LongTimeout,
            () => "fallback",
            scope: "test");

        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task ExecuteAsync_PassesLinkedTokenToAction()
    {
        bool tokenCanCancel = false;

        await TranslationExecutionHelper.ExecuteAsync(
            token =>
            {
                tokenCanCancel = token.CanBeCanceled;
                return Task.FromResult("ok");
            },
            CancellationToken.None,
            LongTimeout,
            () => "fallback",
            scope: "test");

        // CancelAfter on the linked source makes the token cancelable.
        Assert.True(tokenCanCancel);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFallback_WhenTimeoutElapses()
    {
        string result = await TranslationExecutionHelper.ExecuteAsync(
            async token =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return "should-not-reach";
            },
            CancellationToken.None,
            TimeSpan.FromMilliseconds(20),
            () => "fallback",
            scope: "timeout-test");

        Assert.Equal("fallback", result);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFallback_WhenActionThrows()
    {
        string result = await TranslationExecutionHelper.ExecuteAsync<string>(
            _ => throw new InvalidOperationException("boom"),
            CancellationToken.None,
            LongTimeout,
            () => "fallback",
            scope: "error-test");

        Assert.Equal("fallback", result);
    }

    [Fact]
    public async Task ExecuteAsync_PropagatesCancellation_WhenExternalTokenCancelled()
    {
        using var externalCts = new CancellationTokenSource();
        externalCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TranslationExecutionHelper.ExecuteAsync(
                async token =>
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return "should-not-reach";
                },
                externalCts.Token,
                LongTimeout,
                () => "fallback",
                scope: "external-cancel-test"));
    }

    [Fact]
    public async Task ExecuteAsync_FallbackFactory_IsNotInvokedOnSuccess()
    {
        int fallbackCalls = 0;

        string result = await TranslationExecutionHelper.ExecuteAsync(
            _ => Task.FromResult("ok"),
            CancellationToken.None,
            LongTimeout,
            () =>
            {
                fallbackCalls++;
                return "fallback";
            },
            scope: "test");

        Assert.Equal("ok", result);
        Assert.Equal(0, fallbackCalls);
    }
}
