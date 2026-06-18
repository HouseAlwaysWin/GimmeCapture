using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GimmeCapture.Tests;

public class LocalizationParityTests
{
    [Fact]
    public void AllSupportedLanguagesHaveIdenticalKeys()
    {
        string root = FindRepositoryRoot();
        string localizationDir = Path.Combine(root, "src", "GimmeCapture", "Assets", "Localization");
        string[] files = ["en-US.json", "zh-TW.json", "ja-JP.json"];
        var keySets = files.ToDictionary(
            file => file,
            file =>
            {
                using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(localizationDir, file)));
                return document.RootElement.EnumerateObject()
                    .Select(property => property.Name)
                    .ToHashSet(System.StringComparer.Ordinal);
            });
        var expected = keySets[files[0]];

        foreach (string file in files.Skip(1))
        {
            Assert.True(
                expected.SetEquals(keySets[file]),
                $"{file} localization keys differ from {files[0]}.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "GimmeCapture.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
