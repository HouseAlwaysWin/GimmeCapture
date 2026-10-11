using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// Keyboard and screen-reader guards, checked on the .axaml sources rather than by starting Avalonia, so they run on
/// every head without a render platform. What they guard was verified once at runtime (headless, against the real
/// App): an icon-only button used to be announced by its icon's type name, and no custom button showed focus.
/// </summary>
public class AccessibilityMarkupTests
{
    private static readonly HashSet<string> ButtonTypes = new(StringComparer.Ordinal)
    {
        "Button", "ToggleButton", "RepeatButton", "RadioButton", "DropDownButton", "SplitButton", "ToggleSplitButton"
    };

    [Fact]
    public void EveryCustomButtonThemeShowsKeyboardFocus()
    {
        // A button ControlTheme replaces Fluent's wholesale, and with it the only focus visual Fluent draws. Without a
        // FocusAdorner, Tab moves through these buttons without showing where focus is.
        var themes = ButtonThemes();
        Assert.NotEmpty(themes);

        var missing = themes.Keys.Where(key => !HasFocusAdorner(key, themes, depth: 0)).ToList();

        Assert.True(missing.Count == 0, "Button themes without a keyboard focus visual: " + string.Join(", ", missing));
    }

    [Fact]
    public void ButtonsAreNamedAfterTheirTooltip()
    {
        var theme = XDocument.Load(System.IO.Path.Combine(RepositoryFiles.AppSource, "Styles", "GimmeTheme.axaml"));

        bool present = theme.Descendants()
            .Where(e => e.Name.LocalName == "Style" && (string?)e.Attribute("Selector") == ":is(Button)")
            .SelectMany(style => style.Elements().Where(e => e.Name.LocalName == "Setter"))
            .Any(setter => (string?)setter.Attribute("Property") == "AutomationProperties.Name"
                && ((string?)setter.Attribute("Value") ?? "").Contains("ToolTip.Tip", StringComparison.Ordinal));

        Assert.True(present, "GimmeTheme.axaml must name every button after its tooltip (Style Selector=\":is(Button)\").");
    }

    [Fact]
    public void EveryButtonHasSomethingToBeNamedAfter()
    {
        // A screen reader names a button after its content, and for an icon that is the icon's type name
        // ("Avalonia.Controls.Shapes.Path, button"). A button needs text content, a tooltip — the :is(Button) style
        // turns it into the name — or an explicit AutomationProperties.Name.
        var unnamed = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.axaml"))
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var button in document.Descendants().Where(e => ButtonTypes.Contains(e.Name.LocalName)))
            {
                if (!HasAccessibleName(button))
                {
                    unnamed.Add($"{RepositoryFiles.Relative(file)}:{((IXmlLineInfo)button).LineNumber}");
                }
            }
        }

        Assert.True(unnamed.Count == 0,
            "Buttons with no text, tooltip or AutomationProperties.Name:" + Environment.NewLine + string.Join(Environment.NewLine, unnamed));
    }

    private static Dictionary<string, XElement> ButtonThemes()
    {
        var theme = XDocument.Load(System.IO.Path.Combine(RepositoryFiles.AppSource, "Styles", "GimmeTheme.axaml"));
        return theme.Descendants()
            .Where(e => e.Name.LocalName == "ControlTheme" && ButtonTypes.Contains((string?)e.Attribute("TargetType") ?? ""))
            .ToDictionary(e => (string)e.Attributes().First(a => a.Name.LocalName == "Key"), StringComparer.Ordinal);
    }

    private static bool HasFocusAdorner(string key, Dictionary<string, XElement> themes, int depth)
    {
        if (depth > 10 || !themes.TryGetValue(key, out var theme))
        {
            return false;
        }

        if (theme.Elements().Any(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FocusAdorner"))
        {
            return true;
        }

        // BasedOn="{StaticResource Other}" inherits Other's setters.
        string basedOn = (string?)theme.Attribute("BasedOn") ?? "";
        const string prefix = "{StaticResource ";
        return basedOn.StartsWith(prefix, StringComparison.Ordinal)
            && HasFocusAdorner(basedOn[prefix.Length..].TrimEnd('}').Trim(), themes, depth + 1);
    }

    private static bool HasAccessibleName(XElement button)
    {
        if (button.Attributes().Any(a => a.Name.LocalName is "Content" or "ToolTip.Tip" or "AutomationProperties.Name"))
        {
            return true;
        }

        if (button.Elements().Any(e => e.Name.LocalName is "ToolTip.Tip" or "AutomationProperties.Name"))
        {
            return true;
        }

        return ContentContainsText(button);
    }

    // Walks the button's content only: property elements (Button.Flyout, Button.Styles, ...) are not its content.
    private static bool ContentContainsText(XElement element)
    {
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName.Contains('.'))
            {
                continue;
            }

            if (child.Name.LocalName is "TextBlock" or "SelectableTextBlock" or "Run" || ContentContainsText(child))
            {
                return true;
            }
        }

        return false;
    }
}
