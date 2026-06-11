using ReactiveUI;
using System;
using System.Reactive;

namespace GimmeCapture.ViewModels.Floating;

public partial class FloatingVideoViewModel
{
    // ── 裁切模式狀態 ──
    private bool _isTrimmingMode;
    public bool IsTrimmingMode
    {
        get => _isTrimmingMode;
        set
        {
            this.RaiseAndSetIfChanged(ref _isTrimmingMode, value);
            this.RaisePropertyChanged(nameof(WindowPadding));
        }
    }

    // ── 裁切秒數（主要資料來源，Slider 綁定用） ──
    private double _trimStartSeconds;
    public double TrimStartSeconds
    {
        get => _trimStartSeconds;
        set
        {
            var clamped = Math.Max(0, Math.Min(value, _trimEndSeconds - 0.1));
            if (Math.Abs(_trimStartSeconds - clamped) < 0.001) return;
            this.RaiseAndSetIfChanged(ref _trimStartSeconds, clamped);
            // 同步分:秒屬性
            this.RaisePropertyChanged(nameof(TrimStartMinutes));
            this.RaisePropertyChanged(nameof(TrimStartSecs));
        }
    }

    private double _trimEndSeconds;
    public double TrimEndSeconds
    {
        get => _trimEndSeconds;
        set
        {
            var maxVal = _totalDuration.TotalSeconds > 0 ? _totalDuration.TotalSeconds : double.MaxValue;
            var clamped = Math.Max(_trimStartSeconds + 0.1, Math.Min(value, maxVal));
            if (Math.Abs(_trimEndSeconds - clamped) < 0.001) return;
            this.RaiseAndSetIfChanged(ref _trimEndSeconds, clamped);
            // 同步分:秒屬性
            this.RaisePropertyChanged(nameof(TrimEndMinutes));
            this.RaisePropertyChanged(nameof(TrimEndSecs));
        }
    }

    // ── 分:秒 拆分屬性（CompactNumericStep 綁定用） ──
    public double TrimStartMinutes
    {
        get => Math.Floor(_trimStartSeconds / 60.0);
        set => TrimStartSeconds = (value * 60.0) + TrimStartSecs;
    }

    public double TrimStartSecs
    {
        get => Math.Round(_trimStartSeconds % 60.0, 3);
        set => TrimStartSeconds = (TrimStartMinutes * 60.0) + value;
    }

    public double TrimEndMinutes
    {
        get => Math.Floor(_trimEndSeconds / 60.0);
        set => TrimEndSeconds = (value * 60.0) + TrimEndSecs;
    }

    public double TrimEndSecs
    {
        get => Math.Round(_trimEndSeconds % 60.0, 3);
        set => TrimEndSeconds = (TrimEndMinutes * 60.0) + value;
    }

    // ── 命令 ──
    public ReactiveCommand<Unit, Unit> ToggleTrimCommand { get; private set; } = null!;

    // CompactNumericStep 微調命令
    public ReactiveCommand<Unit, Unit> IncTrimStartMinCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecTrimStartMinCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> IncTrimStartSecCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecTrimStartSecCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> IncTrimEndMinCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecTrimEndMinCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> IncTrimEndSecCmd { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DecTrimEndSecCmd { get; private set; } = null!;

    private void InitializeTrimCommands()
    {
        ToggleTrimCommand = ReactiveCommand.Create(() =>
        {
            var activateTrim = !IsTrimmingMode;
            if (activateTrim)
            {
                CurrentTool = FloatingTool.None;
                CurrentAnnotationTool = Models.AnnotationType.None;
            }

            IsTrimmingMode = activateTrim;

            // 第一次開啟時，將終點設為影片總長
            if (IsTrimmingMode && Math.Abs(_trimEndSeconds) < 0.001 && _totalDuration.TotalSeconds > 0)
            {
                _trimEndSeconds = _totalDuration.TotalSeconds;
                this.RaisePropertyChanged(nameof(TrimEndSeconds));
                this.RaisePropertyChanged(nameof(TrimEndMinutes));
                this.RaisePropertyChanged(nameof(TrimEndSecs));
            }
        });

        // 起始時間微調
        IncTrimStartMinCmd = ReactiveCommand.Create(() => { TrimStartMinutes += 1; });
        DecTrimStartMinCmd = ReactiveCommand.Create(() => { TrimStartMinutes = Math.Max(0, TrimStartMinutes - 1); });
        IncTrimStartSecCmd = ReactiveCommand.Create(() => { TrimStartSecs = Math.Round(TrimStartSecs + 0.1, 3); });
        DecTrimStartSecCmd = ReactiveCommand.Create(() => { TrimStartSecs = Math.Max(0, Math.Round(TrimStartSecs - 0.1, 3)); });

        // 終點時間微調
        IncTrimEndMinCmd = ReactiveCommand.Create(() => { TrimEndMinutes += 1; });
        DecTrimEndMinCmd = ReactiveCommand.Create(() => { TrimEndMinutes = Math.Max(0, TrimEndMinutes - 1); });
        IncTrimEndSecCmd = ReactiveCommand.Create(() => { TrimEndSecs = Math.Round(TrimEndSecs + 0.1, 3); });
        DecTrimEndSecCmd = ReactiveCommand.Create(() => { TrimEndSecs = Math.Max(0, Math.Round(TrimEndSecs - 0.1, 3)); });
    }
}
