using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Tests;

// The GPU is stood in for: the provider step reports "DirectML" (or nothing) without touching a device, and the
// session factory hands back a string, failing on demand the way a DirectML session fails to build on Windows 10.
public class OnnxProviderConfiguratorTests
{
    [Fact]
    public void CreateWithCpuFallback_KeepsTheGpuSession_WhenItBuilds()
    {
        var attempts = new List<SessionOptions>();

        string session = OnnxProviderConfigurator.CreateWithCpuFallback(
            () => new SessionOptions(),
            options =>
            {
                attempts.Add(options);
                return "gpu session";
            },
            "Test",
            _ => "DirectML",
            out string provider);

        Assert.Equal("gpu session", session);
        Assert.Equal("DirectML", provider);
        // Released once the session is built: DirectML's options keep the GPU device open.
        Assert.True(Assert.Single(attempts).IsClosed);
    }

    [Fact]
    public void CreateWithCpuFallback_BuildsAgainOnTheCpuAlone_WhenTheGpuSessionFails()
    {
        var attempts = new List<SessionOptions>();
        var givenGpuProviders = new List<SessionOptions>();

        string session = OnnxProviderConfigurator.CreateWithCpuFallback(
            () => new SessionOptions(),
            options =>
            {
                attempts.Add(options);
                return attempts.Count == 1 ? throw new InvalidOperationException("DirectML session failed") : "cpu session";
            },
            "Test",
            options =>
            {
                givenGpuProviders.Add(options);
                return "DirectML";
            },
            out string provider);

        Assert.Equal("cpu session", session);
        Assert.Equal(OnnxProviderConfigurator.CpuProvider, provider);
        Assert.Equal(2, attempts.Count);
        // The retry gets fresh options that never had a GPU provider appended, and both sets are released.
        Assert.NotSame(attempts[0], attempts[1]);
        Assert.Same(attempts[0], Assert.Single(givenGpuProviders));
        Assert.All(attempts, options => Assert.True(options.IsClosed));
    }

    [Fact]
    public void CreateWithCpuFallback_ReportsTheCpu_WhenNoGpuProviderAppends()
    {
        int attempts = 0;

        string session = OnnxProviderConfigurator.CreateWithCpuFallback(
            () => new SessionOptions(),
            _ =>
            {
                attempts++;
                return "cpu session";
            },
            "Test",
            _ => null,
            out string provider);

        Assert.Equal("cpu session", session);
        Assert.Equal(OnnxProviderConfigurator.CpuProvider, provider);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void CreateWithCpuFallback_DoesNotRetry_WhenTheFailedSessionHadNoGpuProvider()
    {
        // Already a CPU session, so a second CPU attempt could only fail the same way (a corrupt model, say).
        int attempts = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => OnnxProviderConfigurator.CreateWithCpuFallback<string>(
            () => new SessionOptions(),
            _ =>
            {
                attempts++;
                throw new InvalidOperationException("model is corrupt");
            },
            "Test",
            _ => null,
            out _));

        Assert.Equal("model is corrupt", ex.Message);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void CreateWithCpuFallback_ThrowsTheCpuFailure_WhenBothAttemptsFail()
    {
        int attempts = 0;

        var ex = Assert.Throws<InvalidOperationException>(() => OnnxProviderConfigurator.CreateWithCpuFallback<string>(
            () => new SessionOptions(),
            _ => throw new InvalidOperationException(++attempts == 1 ? "gpu failure" : "cpu failure"),
            "Test",
            _ => "DirectML",
            out _));

        Assert.Equal("cpu failure", ex.Message);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public void CreateSessionPair_BuildsBothFromTheSameOptions()
    {
        using var options = new SessionOptions();

        var (first, second) = OnnxProviderConfigurator.CreateSessionPair(
            options,
            pairOptions => new FakeSession(pairOptions),
            pairOptions => new FakeSession(pairOptions));

        Assert.Same(options, first.Options);
        Assert.Same(options, second.Options);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    [Fact]
    public void CreateSessionPair_DisposesTheFirst_WhenTheSecondFails()
    {
        // Left alive, a GPU encoder whose decoder failed would hold the device while the CPU attempt builds both again.
        FakeSession? first = null;
        using var options = new SessionOptions();

        var ex = Assert.Throws<InvalidOperationException>(() => OnnxProviderConfigurator.CreateSessionPair<FakeSession>(
            options,
            pairOptions => first = new FakeSession(pairOptions),
            _ => throw new InvalidOperationException("decoder failed")));

        Assert.Equal("decoder failed", ex.Message);
        Assert.NotNull(first);
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public void CreateWithCpuFallback_BuildsBothSessionsAgainOnTheCpu_WhenOnlyTheSecondGpuSessionFails()
    {
        // The way OCR and SAM2 build their pairs. The GPU's first session must not be kept beside a CPU second one:
        // the pair always runs on one provider.
        var firstSessions = new List<FakeSession>();
        int secondAttempts = 0;

        var (first, second) = OnnxProviderConfigurator.CreateWithCpuFallback(
            () => new SessionOptions(),
            options => OnnxProviderConfigurator.CreateSessionPair(
                options,
                pairOptions =>
                {
                    var session = new FakeSession(pairOptions);
                    firstSessions.Add(session);
                    return session;
                },
                pairOptions => ++secondAttempts == 1
                    ? throw new InvalidOperationException("DirectML session failed")
                    : new FakeSession(pairOptions)),
            "Test",
            _ => "DirectML",
            out string provider);

        Assert.Equal(OnnxProviderConfigurator.CpuProvider, provider);
        Assert.Equal(2, firstSessions.Count);
        Assert.True(firstSessions[0].IsDisposed);
        Assert.Same(firstSessions[1], first);
        Assert.False(first.IsDisposed);
        Assert.False(second.IsDisposed);
        Assert.Same(first.Options, second.Options);
        Assert.NotSame(firstSessions[0].Options, first.Options);
    }

    /// <summary>Stands in for an <see cref="InferenceSession"/>: remembers the options it was built from.</summary>
    private sealed class FakeSession(SessionOptions options) : IDisposable
    {
        public SessionOptions Options { get; } = options;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
