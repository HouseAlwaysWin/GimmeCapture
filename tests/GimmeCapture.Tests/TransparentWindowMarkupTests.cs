using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// A window drawn see-through has to ask for it. On the GPU (WinUI or DirectComposition) Avalonia makes every window
/// transparent anyway, so a window with a transparent background looked right without asking. Drawn on the CPU ("Use
/// less memory", StartupRenderer) a window is opaque unless it asks for TransparencyLevelHint Transparent, which Avalonia
/// then grants through DWM's blur-behind: every pin showed a black band where its glow and resize handles are. Checked
/// on the .axaml sources, so it runs on every head without a render platform.
/// </summary>
public class TransparentWindowMarkupTests
{
    [Fact]
    public void EveryWindowWithATransparentBackground_AsksToBeTransparent()
    {
        var checkedWindows = new List<string>();
        var missing = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.axaml"))
        {
            var root = XDocument.Load(file).Root;
            if (root is null || root.Name.LocalName != "Window" || (string?)root.Attribute("Background") != "Transparent")
            {
                continue;
            }

            checkedWindows.Add(RepositoryFiles.Relative(file));
            if (!((string?)root.Attribute("TransparencyLevelHint") ?? string.Empty).Contains("Transparent", StringComparison.Ordinal))
            {
                missing.Add(RepositoryFiles.Relative(file));
            }
        }

        // The capture overlay and the three kinds of pin, at least.
        Assert.True(checkedWindows.Count >= 4, "Windows with a transparent background: " + string.Join(", ", checkedWindows));
        Assert.True(missing.Count == 0, "Transparent windows that do not ask to be: " + string.Join(", ", missing));
    }
}
