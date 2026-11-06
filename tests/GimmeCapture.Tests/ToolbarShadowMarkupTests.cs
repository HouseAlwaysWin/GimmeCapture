using System.Linq;
using System.Xml.Linq;

namespace GimmeCapture.Tests;

/// <summary>
/// A playing video pin redraws its bottom toolbar for every move of the playhead, 15 times a second. Drawn as the
/// toolbar Border's own box shadow, its drop shadow was blurred as a whole layer with an image filter at every redraw:
/// on the GPU that cost Avalonia's render thread 150 M CPU cycles a second of the pin's 990, at 1450x900
/// (PinPlaybackProbeTests). The shadow is a GlowDecorator's, whose mask blur Skia works out along the edges, rounded
/// corners and all. Checked on the .axaml source, so it runs on every head without a render platform.
/// </summary>
public class ToolbarShadowMarkupTests
{
    [Fact]
    public void TheVideoPinsToolbarShadow_IsTheGlowDecorators()
    {
        string file = RepositoryFiles.AppFiles("FloatingVideoWindow.axaml").Single();
        var toolbar = XDocument.Load(file).Descendants()
            .Single(e => (string?)e.Attributes().FirstOrDefault(a => a.Name.LocalName == "Name")?.Value == "ToolbarBorder");

        Assert.Null(toolbar.Attribute("BoxShadow"));
        var decorator = toolbar.Parent;
        Assert.NotNull(decorator);
        Assert.Equal("GlowDecorator", decorator.Name.LocalName);
        Assert.Equal("0 4 15 0 #D0000000", (string?)decorator.Attribute("Glow"));
        // It shows and hides, and sits, with the toolbar.
        Assert.Equal("{Binding ShowToolbar}", (string?)decorator.Attribute("IsVisible"));
        Assert.Null(toolbar.Attribute("IsVisible"));
    }
}
