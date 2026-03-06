using Avalonia;
using ReactiveUI;

namespace GimmeCapture.Models;

/// <summary>
/// 翻譯模式下的操作工具類型
/// </summary>
public enum TranslationTool
{
    Cursor, // 一般模式 (編輯與功能選單模式)
    Single, // 單選模式 (截圖模式一致)
    Multi,  // 多選模式
    Audio   // 音訊模式（固定翻譯框）
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

    private bool _isAudioPanel;
    public bool IsAudioPanel
    {
        get => _isAudioPanel;
        set => this.RaiseAndSetIfChanged(ref _isAudioPanel, value);
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
    
    private string _originalText = string.Empty;
    public string OriginalText
    {
        get => _originalText;
        set => this.RaiseAndSetIfChanged(ref _originalText, value);
    }
    
    private double _inferredFontSize = 12.0;
    public double InferredFontSize
    {
        get => _inferredFontSize;
        set => this.RaiseAndSetIfChanged(ref _inferredFontSize, value);
    }
    
    private byte[]? _lastPixels;
    public byte[]? LastPixels
    {
        get => _lastPixels;
        set => this.RaiseAndSetIfChanged(ref _lastPixels, value);
    }

    private int _lastPixelWidth;
    public int LastPixelWidth
    {
        get => _lastPixelWidth;
        set => this.RaiseAndSetIfChanged(ref _lastPixelWidth, value);
    }

    private int _lastPixelHeight;
    public int LastPixelHeight
    {
        get => _lastPixelHeight;
        set => this.RaiseAndSetIfChanged(ref _lastPixelHeight, value);
    }
}
