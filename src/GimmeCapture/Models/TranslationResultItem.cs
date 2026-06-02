using Avalonia;
using ReactiveUI;

namespace GimmeCapture.Models;

internal sealed class TranslationResultItem : ReactiveObject
{
    private Rect _bounds;
    private bool _isTranslated;
    private bool _isAudioPanel;
    private string _translatedText = string.Empty;
    private string _originalText = string.Empty;
    private double _inferredFontSize = 12.0;
    private double _displayFontSize = 12.0;
    private double _estimatedTextHeight;

    public Rect Bounds
    {
        get => _bounds;
        set => this.RaiseAndSetIfChanged(ref _bounds, value);
    }

    public bool IsTranslated
    {
        get => _isTranslated;
        set => this.RaiseAndSetIfChanged(ref _isTranslated, value);
    }

    public bool IsAudioPanel
    {
        get => _isAudioPanel;
        set => this.RaiseAndSetIfChanged(ref _isAudioPanel, value);
    }

    public string TranslatedText
    {
        get => _translatedText;
        set => this.RaiseAndSetIfChanged(ref _translatedText, value);
    }

    public string OriginalText
    {
        get => _originalText;
        set => this.RaiseAndSetIfChanged(ref _originalText, value);
    }

    public double InferredFontSize
    {
        get => _inferredFontSize;
        set => this.RaiseAndSetIfChanged(ref _inferredFontSize, value);
    }

    public double DisplayFontSize
    {
        get => _displayFontSize;
        set => this.RaiseAndSetIfChanged(ref _displayFontSize, value);
    }

    public double EstimatedTextHeight
    {
        get => _estimatedTextHeight;
        set => this.RaiseAndSetIfChanged(ref _estimatedTextHeight, value);
    }

    public string PrimaryText => !string.IsNullOrWhiteSpace(TranslatedText) ? TranslatedText : OriginalText;

    public static TranslationResultItem FromSelection(UserSelectionRect selection)
    {
        return new TranslationResultItem
        {
            Bounds = selection.Bounds,
            IsTranslated = selection.IsTranslated,
            IsAudioPanel = selection.IsAudioPanel,
            TranslatedText = selection.TranslatedText,
            OriginalText = selection.OriginalText,
            InferredFontSize = selection.InferredFontSize,
            DisplayFontSize = selection.DisplayFontSize,
            EstimatedTextHeight = selection.EstimatedTextHeight
        };
    }
}
