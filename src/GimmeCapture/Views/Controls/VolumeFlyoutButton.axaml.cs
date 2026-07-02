using System;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Threading;

namespace GimmeCapture.Views.Controls;

/// <summary>
/// A compact volume control shared by the video editors (the Pin floating video window and the compress
/// 進階影片編輯 editor): a speaker button whose click toggles mute, and hovering it reveals a floating
/// <em>vertical</em> volume slider (media-player style). Replaces the older inline horizontal slider so the
/// transport bar stays compact. Bindable: <see cref="Volume"/> (0–1, two-way), <see cref="IsMuted"/>,
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

    private readonly DispatcherTimer _closeTimer;
    private readonly Popup? _popup;
    private readonly Button? _speaker;
    private readonly Border? _popupBorder;
    private readonly EventHandler<PointerEventArgs> _onSpeakerEntered;
    private readonly EventHandler<PointerEventArgs> _onSpeakerExited;
    private readonly EventHandler<PointerEventArgs> _onPopupEntered;
    private readonly EventHandler<PointerEventArgs> _onPopupExited;
    private bool _overButton;
    private bool _overPopup;

    public VolumeFlyoutButton()
    {
        InitializeComponent();

        _popup = this.FindControl<Popup>("VolumePopup");
        _speaker = this.FindControl<Button>("SpeakerButton");
        _popupBorder = this.FindControl<Border>("PopupBorder");

        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _closeTimer.Tick += OnCloseTick;

        _onSpeakerEntered = (_, _) => { _overButton = true; OpenPopup(); };
        _onSpeakerExited = (_, _) => { _overButton = false; ScheduleClose(); };
        _onPopupEntered = (_, _) => { _overPopup = true; _closeTimer.Stop(); };
        _onPopupExited = (_, _) => { _overPopup = false; ScheduleClose(); };
    }

    // Subscribe on attach / unsubscribe on detach (symmetric, so it survives a detach+reattach and, more
    // importantly, does NOT keep the control alive via the running DispatcherTimer once its window closes).
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_speaker != null)
        {
            _speaker.PointerEntered += _onSpeakerEntered;
            _speaker.PointerExited += _onSpeakerExited;
        }
        if (_popupBorder != null)
        {
            _popupBorder.PointerEntered += _onPopupEntered;
            _popupBorder.PointerExited += _onPopupExited;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _closeTimer.Stop();
        if (_speaker != null)
        {
            _speaker.PointerEntered -= _onSpeakerEntered;
            _speaker.PointerExited -= _onSpeakerExited;
        }
        if (_popupBorder != null)
        {
            _popupBorder.PointerEntered -= _onPopupEntered;
            _popupBorder.PointerExited -= _onPopupExited;
        }
        _overButton = false;
        _overPopup = false;
        if (_popup != null)
        {
            _popup.IsOpen = false;
        }
    }

    private void OnCloseTick(object? sender, EventArgs e)
    {
        _closeTimer.Stop();
        if (_popup == null || !_popup.IsOpen)
        {
            return;
        }

        // Close only when the pointer is genuinely off both the speaker and the popup. The IsPointerOver
        // re-check guards against a missed PointerExited (e.g. while dragging the thumb) closing the popup
        // out from under the user.
        bool overButton = _overButton || (_speaker?.IsPointerOver ?? false);
        bool overPopup = _overPopup || (_popupBorder?.IsPointerOver ?? false);
        if (!overButton && !overPopup)
        {
            _popup.IsOpen = false;
        }
    }

    private void OpenPopup()
    {
        _closeTimer.Stop();
        if (_popup != null)
        {
            _popup.IsOpen = true;
        }
    }

    private void ScheduleClose()
    {
        _closeTimer.Stop();
        _closeTimer.Start();
    }
}
