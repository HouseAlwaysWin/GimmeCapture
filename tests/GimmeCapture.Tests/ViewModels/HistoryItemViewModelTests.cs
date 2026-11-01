using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// A history entry decodes its thumbnail only while a card shows it (SettingsHistoryTab), and lets it go when the last
// card stops: the panel used to decode every entry's when it first opened, 300 with the history full, and keep them.
public sealed class HistoryItemViewModelTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Shown_DecodesOnce_AndTheLastCardToGo_LetsItGo()
    {
        var image = new FakeImage();
        int decodes = 0;
        var item = Item(_ =>
        {
            Interlocked.Increment(ref decodes);
            return image;
        });

        item.ShowThumbnail();
        await Until(() => item.HasThumbnail);
        item.ShowThumbnail();

        item.HideThumbnail();
        Assert.Same(image, item.Thumbnail);
        Assert.False(image.Disposed);

        item.HideThumbnail();
        Assert.Null(item.Thumbnail);
        Assert.True(image.Disposed);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public async Task HiddenWhileItDecodes_ThrowsTheResultAway()
    {
        var image = new FakeImage();
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var item = Item(_ =>
        {
            started.SetResult();
            release.Task.Wait(Wait);
            return image;
        });

        item.ShowThumbnail();
        await started.Task.WaitAsync(Wait);
        item.HideThumbnail();
        release.SetResult();

        await image.WhenDisposed.WaitAsync(Wait);
        Assert.Null(item.Thumbnail);
    }

    [Fact]
    public async Task ShownAgainWhileItDecodes_KeepsThatOneDecode()
    {
        var image = new FakeImage();
        int decodes = 0;
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var item = Item(_ =>
        {
            Interlocked.Increment(ref decodes);
            started.TrySetResult();
            release.Task.Wait(Wait);
            return image;
        });

        item.ShowThumbnail();
        await started.Task.WaitAsync(Wait);
        item.HideThumbnail();
        item.ShowThumbnail();
        release.SetResult();

        await Until(() => item.HasThumbnail);
        Assert.Same(image, item.Thumbnail);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public async Task Disposed_LetsItGo_AndDecodesNoMore()
    {
        var image = new FakeImage();
        int decodes = 0;
        var item = Item(_ =>
        {
            Interlocked.Increment(ref decodes);
            return image;
        });
        item.ShowThumbnail();
        await Until(() => item.HasThumbnail);

        item.Dispose();
        Assert.Null(item.Thumbnail);
        Assert.True(image.Disposed);

        // The card still on screen until its row is rebuilt.
        item.HideThumbnail();
        item.ShowThumbnail();
        await Task.Delay(100);
        Assert.Null(item.Thumbnail);
        Assert.Equal(1, decodes);
    }

    [Fact]
    public void Hidden_WithoutBeingShown_ChangesNothing()
    {
        int decodes = 0;
        var item = Item(_ =>
        {
            Interlocked.Increment(ref decodes);
            return new FakeImage();
        });

        item.HideThumbnail();
        item.HideThumbnail();

        Assert.Null(item.Thumbnail);
        Assert.Equal(0, decodes);
    }

    private static HistoryItemViewModel Item(Func<string, IImage?> decode)
    {
        var none = new NoCommand();
        return new HistoryItemViewModel(
            new CaptureHistoryItem
            {
                Id = "entry",
                FilePath = "capture.png",
                ThumbnailPath = "thumb.png",
                Kind = CaptureHistoryKind.Image,
                CapturedAtUtc = DateTime.UtcNow,
            },
            none,
            none,
            none,
            none,
            decode);
    }

    private static async Task Until(Func<bool> condition)
    {
        var giveUp = DateTime.UtcNow + Wait;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < giveUp, "Timed out waiting for the thumbnail.");
            await Task.Delay(5);
        }
    }

    private sealed class FakeImage : IImage, IDisposable
    {
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed => _disposed.Task.IsCompleted;

        public Task WhenDisposed => _disposed.Task;

        public Size Size => new(320, 180);

        public void Draw(DrawingContext context, Rect sourceRect, Rect destRect)
        {
        }

        public void Dispose() => _disposed.TrySetResult();
    }

    private sealed class NoCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter)
        {
        }
    }
}
