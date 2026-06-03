using Avalonia;
using Avalonia.Media;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Floating;

internal sealed class FloatingTranslationLayerViewModel : ReactiveObject
{
    private PixelPoint _screenOffset;
    private Size _viewportSize;
    private double _visualScaling = 1.0;
    private Color _borderColor = Color.Parse("#00E5FF");

    public ObservableCollection<TranslationResultItem> Items { get; } = new();

    public PixelPoint ScreenOffset
    {
        get => _screenOffset;
        set => this.RaiseAndSetIfChanged(ref _screenOffset, value);
    }

    public Size ViewportSize
    {
        get => _viewportSize;
        set => this.RaiseAndSetIfChanged(ref _viewportSize, value);
    }

    public double VisualScaling
    {
        get => _visualScaling;
        set => this.RaiseAndSetIfChanged(ref _visualScaling, value);
    }

    public Color BorderColor
    {
        get => _borderColor;
        set => this.RaiseAndSetIfChanged(ref _borderColor, value);
    }

    public ReactiveCommand<TranslationResultItem, Unit> CloseItemCommand { get; }
    public ReactiveCommand<object?, Unit> CopyItemTextCommand { get; }
    public ReactiveCommand<TranslationResultItem, Unit> PinItemCommand { get; }

    public string MenuCopyTranslation => LocalizationService.Instance["CopyTranslation"] ?? "Copy";
    public string MenuPinTranslation => LocalizationService.Instance["MenuPinTranslation"] ?? "Pin";
    public string MenuClose => LocalizationService.Instance["MenuClose"] ?? "Close";
    public string InputLabel => LocalizationService.Instance["Input"] ?? "Input";
    public string OutputLabel => LocalizationService.Instance["Output"] ?? "Output";

    public Func<object?, Task>? CopyAction { get; set; }
    public Func<TranslationResultItem, Task>? PinAction { get; set; }

    public FloatingTranslationLayerViewModel()
    {
        CloseItemCommand = ReactiveCommand.Create<TranslationResultItem>(RemoveItem);
        CopyItemTextCommand = ReactiveCommand.CreateFromTask<object?>(ExecuteCopyAsync);
        PinItemCommand = ReactiveCommand.CreateFromTask<TranslationResultItem>(ExecutePinAsync);
    }

    public void AddItems(System.Collections.Generic.IEnumerable<TranslationResultItem> items)
    {
        foreach (var item in items)
        {
            Items.Add(item);
        }
    }

    public void ClearAll()
    {
        Items.Clear();
    }

    private void RemoveItem(TranslationResultItem? item)
    {
        if (item == null)
        {
            return;
        }

        Items.Remove(item);
    }

    private async Task ExecuteCopyAsync(object? item)
    {
        if (CopyAction != null)
        {
            await CopyAction(item);
        }
    }

    private async Task ExecutePinAsync(TranslationResultItem? item)
    {
        if (item == null || PinAction == null)
        {
            return;
        }

        await PinAction(item);
    }
}
