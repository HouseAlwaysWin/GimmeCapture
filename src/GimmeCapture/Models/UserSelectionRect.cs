using Avalonia;
using ReactiveUI;

namespace GimmeCapture.Models;

/// <summary>
/// 翻譯模式下的操作工具類型
/// </summary>
public enum TranslationTool
{
    Select, // 手動選取模式
    Edit    // 編輯與功能選單模式
}

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

    private bool _isAutoDetectEnabled;
    public bool IsAutoDetectEnabled
    {
        get => _isAutoDetectEnabled;
        set => this.RaiseAndSetIfChanged(ref _isAutoDetectEnabled, value);
    }

    private double _estimatedTextHeight;
    public double EstimatedTextHeight
    {
        get => _estimatedTextHeight;
        set => this.RaiseAndSetIfChanged(ref _estimatedTextHeight, value);
    }

    private string _lastOcrText = string.Empty;
    public string LastOcrText
    {
        get => _lastOcrText;
        set => this.RaiseAndSetIfChanged(ref _lastOcrText, value);
    }
}
