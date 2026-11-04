using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// An indeterminate progress bar animates through a style, and Avalonia only pauses a style's animation while its control
/// is hidden: the animation stays on the UI thread's animation clock, and while anything is on that clock Avalonia renders
/// every 16 ms. So a bar left indeterminate behind an overlay that had shown once kept the app drawing 60 frames a second
/// while it sat hidden: an image pin after its first background removal or smart selection, until the pin closed
/// (PinProcessingIdleProbeTests), and the video editor after its first frame. A bar is indeterminate only while it shows:
/// its IsIndeterminate is bound, never a literal True. Checked on the .axaml sources, so it runs on every head without a
/// render platform.
/// </summary>
public class ProgressBarMarkupTests
{
    [Fact]
    public void NoProgressBar_StaysIndeterminateWhileHidden()
    {
        var bars = new List<string>();
        var forGood = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.axaml"))
        {
            foreach (var bar in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants().Where(e => e.Name.LocalName == "ProgressBar"))
            {
                string where = $"{RepositoryFiles.Relative(file)}:{((IXmlLineInfo)bar).LineNumber}";
                bars.Add(where);
                if (string.Equals((string?)bar.Attribute("IsIndeterminate"), "True", StringComparison.OrdinalIgnoreCase))
                {
                    forGood.Add(where);
                }
            }
        }

        // The processing overlay's (pins, the capture overlay, the processing window) and the video editor's, at least.
        Assert.True(bars.Count >= 2, "Progress bars: " + string.Join(", ", bars));
        Assert.True(forGood.Count == 0, "Progress bars indeterminate for good: " + string.Join(", ", forGood));
    }
}
