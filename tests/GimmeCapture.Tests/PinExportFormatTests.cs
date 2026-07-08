using GimmeCapture.Views.Shared;
using Xunit;

namespace GimmeCapture.Tests;

// The pin toolbar's export-format dropdown feeds its selection into these save-choice builders (the dialog then
// defaults to that format, first in the type list). Pure string logic — no Avalonia platform needed.
public class PinExportFormatTests
{
    [Theory]
    [InlineData("PNG", "png", "PNG Image")]
    [InlineData("JPG", "jpg", "JPEG Image")]
    [InlineData("jpeg", "jpg", "JPEG Image")] // jpeg normalized to jpg
    [InlineData("WebP", "webp", "WebP Image")]
    [InlineData("", "png", "PNG Image")]       // empty -> png
    [InlineData("bmp", "png", "PNG Image")]    // unknown -> png
    public void SaveImageChoices_DefaultsAndOrdersByPreferred(string preferred, string expectedExt, string firstChoiceName)
    {
        var (choices, defaultExt) = VideoFilePickerTypes.SaveImageChoicesPreferring(preferred);
        Assert.Equal(expectedExt, defaultExt);
        Assert.Equal(firstChoiceName, choices[0].Name);   // preferred format first
        Assert.Equal("All Files", choices[^1].Name);       // All Files last
        Assert.Equal(4, choices.Count);                    // png + jpg + webp + All Files
    }

    [Theory]
    [InlineData("MP4", "mp4", "MP4 Video")]
    [InlineData("MKV", "mkv", "MKV Video")]
    [InlineData("WebM", "webm", "WebM Video")]
    [InlineData("GIF", "gif", "GIF")]
    [InlineData("avi", "mp4", "MP4 Video")]    // unknown -> mp4
    public void SaveVideoChoices_DefaultsAndOrdersByPreferred(string preferred, string expectedExt, string firstChoiceName)
    {
        var (choices, defaultExt) = VideoFilePickerTypes.SaveChoicesPreferring(preferred);
        Assert.Equal(expectedExt, defaultExt);
        Assert.Equal(firstChoiceName, choices[0].Name);
    }
}
