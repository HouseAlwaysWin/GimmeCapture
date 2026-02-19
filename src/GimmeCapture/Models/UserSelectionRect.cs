using Avalonia;
using ReactiveUI;

namespace GimmeCapture.Models;

/// <summary>
/// 翻譯模式下使用者圈選的矩形區域
/// </summary>
public class UserSelectionRect : ReactiveObject
{
    private Rect _bounds;
    public Rect Bounds
    {
        get => _bounds;
        set => this.RaiseAndSetIfChanged(ref _bounds, value);
    }

    private bool _isTranslated;
    public bool IsTranslated
    {
        get => _isTranslated;
        set => this.RaiseAndSetIfChanged(ref _isTranslated, value);
    }

    private string _translatedText = string.Empty;
    public string TranslatedText
    {
        get => _translatedText;
        set => this.RaiseAndSetIfChanged(ref _translatedText, value);
    }
}
