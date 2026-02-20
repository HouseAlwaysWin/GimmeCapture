using Avalonia;
using Avalonia.Media.Imaging;
using ReactiveUI;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.ViewModels.Shared;

namespace GimmeCapture.ViewModels.Floating;

/// <summary>
/// 翻譯浮動視窗的 ViewModel
/// 繼承自 FloatingWindowViewModelBase，與截圖/錄影浮動視窗架構一致
/// 支援多選區域翻譯結果顯示
/// </summary>
public partial class FloatingTranslationViewModel : FloatingWindowViewModelBase, IDrawingToolViewModel
{
    public bool ShowIconSettings => false;

    private Bitmap? _image;
    /// <summary>擷取的螢幕截圖（作為翻譯背景）</summary>
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }

    /// <summary>翻譯區塊集合（多選）</summary>
    public ObservableCollection<TranslatedBlock> TranslatedBlocks { get; } = new();

    private bool _isTranslating;
    /// <summary>是否正在翻譯</summary>
    public bool IsTranslating
    {
        get => _isTranslating;
        set => this.RaiseAndSetIfChanged(ref _isTranslating, value);
    }

    // 快捷鍵
    private readonly AppSettingsService _appSettingsService = null!;
    private readonly IClipboardService _clipboardService;

    public override string CopyHotkey => _appSettingsService?.Settings.CopyHotkey ?? base.CopyHotkey;
    public override string SaveHotkey => _appSettingsService?.Settings.SaveHotkey ?? base.SaveHotkey;
    public override string CloseHotkey => _appSettingsService?.Settings.CloseHotkey ?? base.CloseHotkey;
    public override string PinHotkey => _appSettingsService?.Settings.PinHotkey ?? base.PinHotkey;
    public override string UndoHotkey => _appSettingsService?.Settings.UndoHotkey ?? base.UndoHotkey;
    public override string RedoHotkey => _appSettingsService?.Settings.RedoHotkey ?? base.RedoHotkey;
    public override string ClearHotkey => _appSettingsService?.Settings.ClearHotkey ?? base.ClearHotkey;

    public override string RectangleHotkey => _appSettingsService?.Settings.RectangleHotkey ?? base.RectangleHotkey;
    public override string EllipseHotkey => _appSettingsService?.Settings.EllipseHotkey ?? base.EllipseHotkey;
    public override string ArrowHotkey => _appSettingsService?.Settings.ArrowHotkey ?? base.ArrowHotkey;
    public override string LineHotkey => _appSettingsService?.Settings.LineHotkey ?? base.LineHotkey;
    public override string PenHotkey => _appSettingsService?.Settings.PenHotkey ?? base.PenHotkey;
    public override string TextHotkey => _appSettingsService?.Settings.TextHotkey ?? base.TextHotkey;
    public override string MosaicHotkey => _appSettingsService?.Settings.MosaicHotkey ?? base.MosaicHotkey;
    public override string BlurHotkey => _appSettingsService?.Settings.BlurHotkey ?? base.BlurHotkey;

    // Tooltips
    public string CopyTooltip => $"{LocalizationService.Instance["TipCopy"]} ({CopyHotkey})";
    public string SaveTooltip => $"{LocalizationService.Instance["TipSave"]} ({SaveHotkey})";
    public string CloseTooltip => $"{LocalizationService.Instance["ActionClose"]} ({CloseHotkey})";
    public string UndoTooltip => $"{LocalizationService.Instance["Undo"]} ({UndoHotkey})";
    public string RedoTooltip => $"{LocalizationService.Instance["Redo"]} ({RedoHotkey})";
    public string ClearTooltip => $"{LocalizationService.Instance["Clear"]} ({ClearHotkey})";
    public string ToggleToolbarTooltip => $"{LocalizationService.Instance["ActionToolbar"]} ({_appSettingsService?.Settings.ToggleToolbarHotkey ?? "H"})";
    public string RectangleTooltip => $"{LocalizationService.Instance["TipRectangle"]} ({RectangleHotkey})";
    public string EllipseTooltip => $"{LocalizationService.Instance["TipEllipse"]} ({EllipseHotkey})";
    public string ArrowTooltip => $"{LocalizationService.Instance["TipArrow"]} ({ArrowHotkey})";
    public string LineTooltip => $"{LocalizationService.Instance["TipLine"]} ({LineHotkey})";
    public string PenTooltip => $"{LocalizationService.Instance["TipPen"]} ({PenHotkey})";
    public string TextTooltip => $"{LocalizationService.Instance["TipText"]} ({TextHotkey})";
    public string MosaicTooltip => $"{LocalizationService.Instance["TipMosaic"]} ({MosaicHotkey})";
    public string BlurTooltip => $"{LocalizationService.Instance["TipBlur"]} ({BlurHotkey})";

    public IClipboardService ClipboardService => _clipboardService;
    public AppSettingsService AppSettingsService => _appSettingsService;

    // 命令
    public ReactiveCommand<Unit, Unit> CopyCommand { get; private set; } = null!;
    public ReactiveCommand<TranslatedBlock, Unit> CopyTranslationTextCommand { get; private set; } = null!;
    public ReactiveCommand<TranslatedBlock, Unit> RemoveTranslatedBlockCommand { get; private set; } = null!;

    public override Thickness WindowPadding
    {
        get
        {
            // 無裝飾，只需頂部給工具列留空間
            double topPad = ShowToolbar ? 40 : 5;
            return new Thickness(5, topPad, 5, 5);
        }
    }

    public FloatingTranslationViewModel(
        Bitmap image,
        double originalWidth,
        double originalHeight,
        Avalonia.Media.Color borderColor,
        double borderThickness,
        bool hideDecoration,
        bool hideBorder,
        IClipboardService clipboardService,
        AppSettingsService appSettingsService)
    {
        Image = image;
        OriginalWidth = originalWidth;
        OriginalHeight = originalHeight;
        DisplayWidth = originalWidth;
        DisplayHeight = originalHeight;
        BorderColor = borderColor;
        BorderThickness = borderThickness;
        HidePinDecoration = hideDecoration;
        HidePinBorder = hideBorder;
        _clipboardService = clipboardService;
        _appSettingsService = appSettingsService;

        // 預設工具列可見性
        ShowToolbar = !(_appSettingsService.Settings.DefaultHideSnipToolbar);

        InitializeBaseCommands();
        InitializeAnnotationCommands();
        InitializeTranslationCommands();
    }

    private void InitializeTranslationCommands()
    {
        CopyCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (Image != null)
            {
                await _clipboardService.CopyImageAsync(Image);
            }
        });
        CopyCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Translation Copy Error: {ex}"));

        CopyTranslationTextCommand = ReactiveCommand.CreateFromTask<TranslatedBlock>(async (TranslatedBlock block) =>
        {
            if (block != null && !string.IsNullOrEmpty(block.TranslatedText))
            {
                await _clipboardService.CopyTextAsync(block.TranslatedText);
            }
        });
        CopyTranslationTextCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Copy Text Error: {ex}"));

        RemoveTranslatedBlockCommand = ReactiveCommand.Create<TranslatedBlock>(block =>
        {
            if (block != null)
            {
                TranslatedBlocks.Remove(block);
            }
        });
        RemoveTranslatedBlockCommand.ThrownExceptions.Subscribe(ex => System.Diagnostics.Debug.WriteLine($"Remove Block Error: {ex}"));
    }

    /// <summary>
    /// 新增翻譯結果區塊
    /// </summary>
    public void AddTranslatedBlock(Rect bounds, string originalText, string translatedText, double inferredFontSize = 12.0)
    {
        TranslatedBlocks.Add(new TranslatedBlock
        {
            Bounds = bounds,
            OriginalText = originalText,
            TranslatedText = translatedText,
            InferredFontSize = inferredFontSize
        });
    }

    /// <summary>
    /// 清除所有翻譯結果
    /// </summary>
    public void ClearTranslatedBlocks()
    {
        TranslatedBlocks.Clear();
    }

    public override void Dispose()
    {
        // 清理資源
    }
}
