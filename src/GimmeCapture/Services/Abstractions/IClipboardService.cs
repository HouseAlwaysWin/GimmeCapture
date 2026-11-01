using Avalonia.Media.Imaging;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// Clipboard writes for pinned images/videos and history entries.
///
/// <para>Every method returns whether the payload actually reached the clipboard. A write can lose the race for
/// the clipboard (a clipboard manager, Win+V history, RDP/VM clipboard sync or an Office add-in holding it open),
/// and a failed write leaves the PREVIOUS clipboard content in place — so a caller that reports success anyway
/// makes the next paste silently yield the previous image. Never ignore a <c>false</c>.</para>
/// </summary>
public interface IClipboardService
{
    Task<bool> CopyImageAsync(Bitmap bitmap);
    Task<bool> CopyTextAsync(string text);
    Task<bool> CopyFileAsync(string filePath);
    Task<bool> CopyFileAndImageAsync(string filePath, Bitmap bitmap);

    /// <summary>
    /// A picture already encoded as <paramref name="png"/>, with the file at <paramref name="filePath"/> beside it when
    /// there is one, for pasting into a folder. The other two encode the PNG themselves, again for a caller that has
    /// just encoded one. Nothing here holds on to a bitmap of the caller's, which it may then dispose.
    /// </summary>
    Task<bool> CopyPngAsync(byte[] png, string? filePath = null);
}
