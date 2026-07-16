using GimmeCapture.Converters;

namespace GimmeCapture.Tests;

// The converter exists solely to stop Avalonia's SelectableTextBlock from crashing when a selection spans a
// zero-length text line. The load-bearing invariant is therefore: the output never contains an empty line.
public class SelectableTextSanitizeConverterTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Hello", "Hello")]
    [InlineData("Hello\n", "Hello")]              // trailing newline stripped
    [InlineData("Hello\n\n", "Hello")]            // trailing blank lines stripped
    [InlineData("Hello\r\nWorld", "Hello\nWorld")] // CRLF normalized
    [InlineData("Hello\rWorld", "Hello\nWorld")]   // lone CR normalized
    [InlineData("Hello\n\nWorld", "Hello\n \nWorld")] // internal blank line -> single space (kept, not empty)
    [InlineData("a\n\n\nb", "a\n \n \nb")]         // consecutive blank lines each -> space
    public void Sanitize_ProducesExpected(string? input, string expected)
    {
        Assert.Equal(expected, SelectableTextSanitizeConverter.Sanitize(input));
    }

    [Theory]
    [InlineData("Line1\n\nLine2\n\n\n")]
    [InlineData("\n\n text \n\n more \n")]
    [InlineData("trailing spaces   \n\n")]
    [InlineData("僅一行")]
    [InlineData("多行\n翻譯\n\n結果\n")]
    public void Sanitize_NeverLeavesAZeroLengthLine(string input)
    {
        string result = SelectableTextSanitizeConverter.Sanitize(input);
        foreach (string line in result.Split('\n'))
        {
            Assert.NotEqual(0, line.Length); // an empty line is exactly what crashes the selection hit-test
        }
    }
}
