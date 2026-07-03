using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;

namespace GimmeCapture.Views.Controls;

/// <summary>
/// A compact volume control shared by the video editors (the Pin floating video window and the compress
/// 進階影片編輯 editor): a speaker button that opens a floating <em>vertical</em> volume slider popup on
/// click (with a mute toggle inside). Bindable: <see cref="Volume"/> (0–1, two-way), <see cref="IsMuted"/>,
/// <see cref="ToggleMuteCommand"/>, <see cref="MuteTooltip"/>.
/// </summary>
public partial class VolumeFlyoutButton : UserControl
{
    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<VolumeFlyoutButton, double>(
            nameof(Volume), defaultValue: 1.0, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsMutedProperty =
        AvaloniaProperty.Register<VolumeFlyoutButton, bool>(
            nameof(IsMuted), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<ICommand?> ToggleMuteCommandProperty =
        AvaloniaProperty.Register<VolumeFlyoutButton, ICommand?>(nameof(ToggleMuteCommand));

    public static readonly StyledProperty<string?> MuteTooltipProperty =
        AvaloniaProperty.Register<VolumeFlyoutButton, string?>(nameof(MuteTooltip));

    /// <summary>Optional theme override for the toolbar (speaker) button, so hosts can match their row's
    /// button style (e.g. the editor's gold metal buttons). Unset → the control's default (SnipButton).</summary>
    public static readonly StyledProperty<Avalonia.Styling.ControlTheme?> ButtonThemeProperty =
        AvaloniaProperty.Register<VolumeFlyoutButton, Avalonia.Styling.ControlTheme?>(nameof(ButtonTheme));

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    public bool IsMuted
    {
        get => GetValue(IsMutedProperty);
        set => SetValue(IsMutedProperty, value);
    }

    public ICommand? ToggleMuteCommand
    {
        get => GetValue(ToggleMuteCommandProperty);
        set => SetValue(ToggleMuteCommandProperty, value);
    }

    public string? MuteTooltip
    {
        get => GetValue(MuteTooltipProperty);
        set => SetValue(MuteTooltipProperty, value);
    }

    public Avalonia.Styling.ControlTheme? ButtonTheme
    {
        get => GetValue(ButtonThemeProperty);
        set => SetValue(ButtonThemeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ButtonThemeProperty && _speaker != null
            && change.NewValue is Avalonia.Styling.ControlTheme theme)
        {
            _speaker.Theme = theme;
        }
    }

    private readonly Popup? _popup;
    private readonly Button? _speaker;
    private readonly Slider? _slider;
    private long _lastClosedTick;

    public VolumeFlyoutButton()
    {
        InitializeComponent();
        _popup = this.FindControl<Popup>("VolumePopup");
        _slider = this.FindControl<Slider>("VolumeSlider");
        _speaker = this.FindControl<Button>("SpeakerButton");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_speaker != null)
        {
            _speaker.Click += OnSpeakerClick;
        }
        if (_popup != null)
        {
            _popup.Closed += OnPopupClosed;
        }
        if (_slider != null)
        {
            _slider.PropertyChanged += OnSliderPropertyChanged;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_speaker != null)
        {
            _speaker.Click -= OnSpeakerClick;
        }
        if (_popup != null)
        {
            _popup.Closed -= OnPopupClosed;
            _popup.IsOpen = false;
        }
        if (_slider != null)
        {
            _slider.PropertyChanged -= OnSliderPropertyChanged;
        }
    }

    // Click toggles the popup. When it's open, clicking the speaker light-dismisses it first (Closed fires
    // on the pointer-press); the tick guard then suppresses the immediate reopen so the click reads as a
    // clean toggle. Dragging the slider inside the popup never dismisses it (the press is inside).
    private void OnSpeakerClick(object? sender, RoutedEventArgs e)
    {
        if (_popup == null)
        {
            return;
        }

        if (Environment.TickCount64 - _lastClosedTick > 150)
        {
            _popup.IsOpen = true;
        }
    }

    private void OnPopupClosed(object? sender, EventArgs e) => _lastClosedTick = Environment.TickCount64;

    // Push slider drags onto the Volume StyledProperty via code (SetValue) — this reliably fires the
    // control's outer TwoWay binding to the VM's PreviewVolume, unlike a chained TwoWay slider binding.
    private void OnSliderPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == RangeBase.ValueProperty && _slider != null)
        {
            Volume = _slider.Value;
        }
    }
}
