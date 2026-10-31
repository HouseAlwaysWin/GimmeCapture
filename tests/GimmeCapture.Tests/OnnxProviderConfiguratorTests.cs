using Microsoft.ML.OnnxRuntime;

namespace GimmeCapture.Tests;

// The sessions are stood in for: the factories hand back a fake that remembers its options, failing on demand.
public class OnnxProviderConfiguratorTests
{
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
        // Left alive, an encoder whose decoder failed would keep its model, about 100 MB for SAM2 Tiny, with nothing
        // left to free it.
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

    /// <summary>Stands in for an <see cref="InferenceSession"/>: remembers the options it was built from.</summary>
    private sealed class FakeSession(SessionOptions options) : IDisposable
    {
        public SessionOptions Options { get; } = options;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}
