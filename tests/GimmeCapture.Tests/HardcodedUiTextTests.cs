using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Xunit;

namespace GimmeCapture.Tests;

/// <summary>
/// The locale-parity check only compares JSON keys, so text written straight into a view or a dialog call slipped
/// past it: the folder picker and two hotkey dialogs were hardcoded in Chinese for every language, and the pinned
/// video's seek buttons, the pin dialogs' YES/NO and several window titles in English. These tests catch that at the
/// source. Text that is deliberately literal is listed here, with the reason.
/// </summary>
public partial class HardcodedUiTextTests
{
    // Attributes whose value a user reads, or a screen reader announces.
    private static readonly HashSet<string> TextAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "Title", "ToolTip.Tip", "Watermark", "PlaceholderText", "AutomationProperties.Name"
    };

    private static readonly HashSet<string> AllowedXamlLiterals = new(StringComparer.Ordinal)
    {
        // The product name and its slogan.
        "GimmeCapture!!", "Gimme Update!!", "GIMME UPDATE!!", "The One Tool to Snip & Record",
        // A file-name template shown as the example of the template syntax.
        "GimmeCapture_{date}_{time}",
        // Glyph labels (bold, italic, mosaic cell size) whose tooltip carries the words.
        "B", "I", "S", "M", "L",
    };

    // CJK in these files is data, not UI: text the translation model emits that the app strips or recognises.
    private static readonly HashSet<string> CjkDataFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Services/Translation/TranslationService.TextSanitization.cs",
    };

    private static readonly HashSet<string> AllowedCjkLiterals = new(StringComparer.Ordinal)
    {
        // Language names written in their own script, as language pickers conventionally show them.
        "繁體中文 (台灣)", "日本語 (日本)",
        // Prompt echoes the translation model sometimes prefixes its answer with; recognised and stripped.
        "翻譯してください", "翻訳してください", "請翻譯", "翻譯:", "翻訳:", "譯文:",
    };

    [Fact]
    public void XamlShowsNoHardcodedText()
    {
        var hardcoded = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.axaml"))
        {
            var document = XDocument.Load(file, LoadOptions.SetLineInfo);
            foreach (var element in document.Descendants())
            {
                foreach (var attribute in element.Attributes().Where(a => TextAttributes.Contains(a.Name.LocalName)))
                {
                    string value = attribute.Value.Trim();
                    if (value.StartsWith('{') || !value.Any(char.IsLetter) || AllowedXamlLiterals.Contains(value))
                    {
                        continue;
                    }

                    hardcoded.Add($"{RepositoryFiles.Relative(file)}:{((IXmlLineInfo)attribute).LineNumber} {attribute.Name.LocalName}=\"{value}\"");
                }
            }
        }

        Assert.True(hardcoded.Count == 0,
            "User-visible text written into XAML instead of the locale files (bind a LocalizationService key, or add it to "
            + "AllowedXamlLiterals with the reason):" + Environment.NewLine + string.Join(Environment.NewLine, hardcoded));
    }

    [Fact]
    public void CodeHasNoHardcodedCjkText()
    {
        var hardcoded = new List<string>();
        foreach (var file in RepositoryFiles.AppFiles("*.cs"))
        {
            if (CjkDataFiles.Contains(RepositoryFiles.Relative(file)))
            {
                continue;
            }

            int lineNumber = 0;
            foreach (string line in File.ReadLines(file))
            {
                lineNumber++;
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal) || trimmed.StartsWith('*'))
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match literal in StringLiteral().Matches(CodeBeforeComment(line)))
                {
                    string value = literal.Groups["value"].Value;
                    if (Cjk().IsMatch(value) && !AllowedCjkLiterals.Contains(value))
                    {
                        hardcoded.Add($"{RepositoryFiles.Relative(file)}:{lineNumber} {literal.Value}");
                    }
                }
            }
        }

        Assert.True(hardcoded.Count == 0,
            "Chinese/Japanese text hardcoded in C# — every other language sees it untranslated (use a LocalizationService "
            + "key, or allow-list it with the reason):" + Environment.NewLine + string.Join(Environment.NewLine, hardcoded));
    }

    // Drops a trailing // comment that is not inside a string literal. A heuristic, but enough for this codebase.
    private static string CodeBeforeComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '/' && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    [GeneratedRegex(@"\$?@?""(?<value>(?:[^""\\]|\\.)*)""")]
    private static partial Regex StringLiteral();

    [GeneratedRegex(@"[぀-ヿ一-鿿＀-￯]")]
    private static partial Regex Cjk();
}
