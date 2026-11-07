using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

/// <summary>
/// Making the update service builds the shared HTTP client, which loads the web libraries, and no start reads those
/// ahead any more: made on the UI thread 30 s after a sign-in, the automatic check held that thread 1.6 s while they
/// came off a busy hard disk. The check makes it, and sends its request, on the thread pool.
/// </summary>
public sealed class MainWindowViewModelUpdateCheckThreadTests
{
    private sealed class NoReleases : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            });
        }
    }

    [Fact]
    public async Task TheUpdateService_IsMadeOffTheThreadThatAsksForTheCheck()
    {
        int madeOn = -1;
        var handler = new NoReleases();
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies() with
        {
            UpdateService = new Lazy<UpdateService>(() =>
            {
                madeOn = Environment.CurrentManagedThreadId;
                return new UpdateService("0.74.0", new HttpClient(handler));
            }),
        });
        await viewModel.InitialSettingsLoadTask;

        // The automatic check, asked from a thread of its own as from the UI thread: whatever runs before its first
        // await runs on that thread. (A manual check that finds nothing says so through the UI thread's dispatcher.)
        Task? check = null;
        var caller = new Thread(() => check = viewModel.CheckForUpdatesQuietly());
        caller.Start();
        caller.Join();
        await check!;

        Assert.NotEqual(-1, madeOn);
        Assert.NotEqual(caller.ManagedThreadId, madeOn);
        Assert.Equal(1, handler.Requests);
    }
}
