using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// An animation that runs for good through a style keeps the app drawing while its control is hidden: Avalonia only
/// pauses a hidden control's style animation, which stays on the UI thread's animation clock, and while anything is on
/// that clock Avalonia renders every 16 ms. The capture overlay's top loading bar did, from the moment the overlay was
/// built until it closed, though it shows only while a scan or a translation runs: its UI thread woke 580 times a second
/// instead of 19 (TopLoadingBarIdleProbeTests). So such a style says when it runs: its selector matches only while the
/// control shows. Progress bars have their own rule (<see cref="ProgressBarMarkupTests"/>). Checked on the .axaml
/// sources, so it runs on every head without a render platform.
/// </summary>
public class StyleAnimationMarkupTests
{
    [Fact]
    public void NoAnimationRunsForGood_ThroughAStyleThatMatchesWhileHidden()
    {
        var forGood = new List<string>();
        var unconditional = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.axaml"))
        {
            foreach (var animation in XDocument.Load(file, LoadOptions.SetLineInfo).Descendants()
                         .Where(e => e.Name.LocalName == "Animation"
                                     && string.Equals((string?)e.Attribute("IterationCount"), "Infinite", StringComparison.OrdinalIgnoreCase)))
            {
                string where = $"{RepositoryFiles.Relative(file)}:{((IXmlLineInfo)animation).LineNumber}";
                forGood.Add(where);
                var style = animation.Ancestors().FirstOrDefault(e => e.Name.LocalName == "Style");
                string selector = (string?)style?.Attribute("Selector") ?? "";
                if (!selector.Contains("[IsVisible=True]", StringComparison.Ordinal))
                {
                    unconditional.Add($"{where} ({selector})");
                }
            }
        }

        // The top loading bar's, at least.
        Assert.NotEmpty(forGood);
        Assert.True(unconditional.Count == 0, "Animations that run for good while hidden: " + string.Join(", ", unconditional));
    }
}
