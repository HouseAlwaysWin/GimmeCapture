using System.Collections.Generic;
using Avalonia;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Floating;

namespace GimmeCapture.Tests.ViewModels;

// A long pin's scroll bar runs along the content's edge, under the resize handle of that edge, which took the pointer:
// the bar never widened under it and could not be dragged. On the edge that carries the bar the handle moves out past
// the content; every other edge keeps its handle where it was.
public class LongPinResizeHandleTests
{
    private static readonly Thickness RightInside = new(0, 15, 0, 15);
    private static readonly Thickness RightOutside = new(0, 15, -15, 15);
    private static readonly Thickness BottomInside = new(15, 0, 15, 0);
    private static readonly Thickness BottomOutside = new(15, 0, 15, -15);

    [Fact]
    public void APin_KeepsItsEdgeHandlesOnTheEdge()
    {
        FloatingImageViewModel pin = NewPin();

        Assert.Equal(RightInside, pin.RightResizeHandleMargin);
        Assert.Equal(BottomInside, pin.BottomResizeHandleMargin);
    }

    [Fact]
    public void APinThatScrollsDown_MovesItsRightHandleOffTheScrollBar()
    {
        FloatingImageViewModel pin = NewPin();
        var changed = new List<string?>();
        pin.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pin.ConfigureScrollableContent(imageNativeWidth: 807, imageNativeHeight: 5200, viewportWidth: 538, viewportHeight: 307);

        Assert.Equal(RightOutside, pin.RightResizeHandleMargin);
        Assert.Equal(BottomInside, pin.BottomResizeHandleMargin);
        Assert.Contains(nameof(FloatingImageViewModel.RightResizeHandleMargin), changed);
    }

    [Fact]
    public void APinThatScrollsSideways_MovesItsBottomHandleOffTheScrollBar()
    {
        FloatingImageViewModel pin = NewPin();
        var changed = new List<string?>();
        pin.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        pin.ConfigureScrollableContent(imageNativeWidth: 6000, imageNativeHeight: 545, viewportWidth: 600, viewportHeight: 363);

        Assert.Equal(BottomOutside, pin.BottomResizeHandleMargin);
        Assert.Equal(RightInside, pin.RightResizeHandleMargin);
        Assert.Contains(nameof(FloatingImageViewModel.BottomResizeHandleMargin), changed);
    }

    private static FloatingImageViewModel NewPin()
    {
        var settings = new Mock<IAppSettingsService>();
        settings.Setup(s => s.Settings).Returns(new AppSettings());
        var aiPath = new Mock<AIPathService>(settings.Object);
        var ai = new Mock<AIResourceService>(settings.Object, aiPath.Object, new Mock<AIModelDownloader>().Object);
        return new FloatingImageViewModel(
            null!, 0, 0, Avalonia.Media.Colors.Red, 2.0, false, false, Mock.Of<IClipboardService>(), ai.Object,
            new SAM2RuntimeService(aiPath.Object), settings.Object, Mock.Of<IBackgroundRemovalService>(), new ResourceQueueService());
    }
}
