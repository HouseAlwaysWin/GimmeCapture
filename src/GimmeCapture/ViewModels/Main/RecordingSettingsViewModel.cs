using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;
using GimmeCapture.Services.Core.Media.NativeFFmpeg;

namespace GimmeCapture.ViewModels.Main;

public class RecordingSettingsViewModel : ViewModelBase
{
    public RecordingSettingsViewModel()
    {
        RefreshWebcamDevicesCommand = ReactiveCommand.Create(RefreshWebcamDevices);
    }

    private string _videoSaveDirectory = string.Empty;
    public string VideoSaveDirectory
    {
        get => _videoSaveDirectory;
        set => this.RaiseAndSetIfChanged(ref _videoSaveDirectory, value);
    }

    private string _recordFormat = "mp4";
    public string RecordFormat
    {
        get => _recordFormat;
        set => this.RaiseAndSetIfChanged(ref _recordFormat, value);
    }

    private int _recordFPS = 30;
    public int RecordFPS
    {
        get => _recordFPS;
        set => this.RaiseAndSetIfChanged(ref _recordFPS, value);
    }

    private double _maxRecordingSizeMB = 0;
    public double MaxRecordingSizeMB
    {
        get => _maxRecordingSizeMB;
        set => this.RaiseAndSetIfChanged(ref _maxRecordingSizeMB, value);
    }

    private VideoCodec _videoCodec = VideoCodec.H264;
    public VideoCodec VideoCodec
    {
        get => _videoCodec;
        set => this.RaiseAndSetIfChanged(ref _videoCodec, value);
    }

    private VideoQuality _videoQuality = VideoQuality.Medium;
    public VideoQuality VideoQuality
    {
        get => _videoQuality;
        set => this.RaiseAndSetIfChanged(ref _videoQuality, value);
    }

    private VideoEncoderHint _videoEncoderHint = VideoEncoderHint.PreferHardware;
    public VideoEncoderHint VideoEncoderHint
    {
        get => _videoEncoderHint;
        set => this.RaiseAndSetIfChanged(ref _videoEncoderHint, value);
    }

    public class VideoCodecOption
    {
        public VideoCodec Value { get; set; }
        public string Name => LocalizationService.Instance[$"VideoCodec{Value}"];
    }

    public class VideoQualityOption
    {
        public VideoQuality Value { get; set; }
        public string Name => LocalizationService.Instance[$"VideoQuality{Value}"];
    }

    public class VideoEncoderHintOption
    {
        public VideoEncoderHint Value { get; set; }
        public string Name => LocalizationService.Instance[$"VideoEncoderHint{Value}"];
    }

    /// <summary>
    /// AV1 appears only on machines that can actually encode it in hardware. The software AV1 encoders exist in
    /// the bundled build — the Compress pipeline uses them — but they are offline encoders and cannot keep up
    /// with a realtime capture, so offering AV1 without hardware would be offering dropped frames.
    ///
    /// Built lazily: the probe test-opens an encoder, which needs the native libraries loaded, and this view
    /// model is constructed before that is guaranteed.
    /// </summary>
    public VideoCodecOption[] VideoCodecOptions => _videoCodecOptions ??= BuildVideoCodecOptions();

    private VideoCodecOption[]? _videoCodecOptions;

    private static VideoCodecOption[] BuildVideoCodecOptions()
    {
        var options = new List<VideoCodecOption>(3)
        {
            new() { Value = VideoCodec.H264 },
            new() { Value = VideoCodec.H265 },
        };

        if (LibavRecordingEncoder.HasUsableHardwareAv1Encoder())
        {
            options.Add(new VideoCodecOption { Value = VideoCodec.Av1 });
        }

        return [.. options];
    }

    public VideoQualityOption[] VideoQualityOptions { get; } = {
        new VideoQualityOption { Value = VideoQuality.Low },
        new VideoQualityOption { Value = VideoQuality.Medium },
        new VideoQualityOption { Value = VideoQuality.High }
    };

    public VideoEncoderHintOption[] VideoEncoderHintOptions { get; } = {
        new VideoEncoderHintOption { Value = VideoEncoderHint.PreferHardware },
        new VideoEncoderHintOption { Value = VideoEncoderHint.SoftwareOnly }
    };

    private VideoCodecOption? _selectedVideoCodecOption;
    public VideoCodecOption? SelectedVideoCodecOption
    {
        get => _selectedVideoCodecOption;
        set 
        {
            this.RaiseAndSetIfChanged(ref _selectedVideoCodecOption, value);
            if (value != null) VideoCodec = value.Value;
        }
    }

    private VideoQualityOption? _selectedVideoQualityOption;
    public VideoQualityOption? SelectedVideoQualityOption
    {
        get => _selectedVideoQualityOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedVideoQualityOption, value);
            if (value != null) VideoQuality = value.Value;
        }
    }

    private VideoEncoderHintOption? _selectedVideoEncoderHintOption;
    public VideoEncoderHintOption? SelectedVideoEncoderHintOption
    {
        get => _selectedVideoEncoderHintOption;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedVideoEncoderHintOption, value);
            if (value != null) VideoEncoderHint = value.Value;
        }
    }

    private bool _useFixedRecordPath;
    public bool UseFixedRecordPath
    {
        get => _useFixedRecordPath;
        set => this.RaiseAndSetIfChanged(ref _useFixedRecordPath, value);
    }

    // --- Webcam picture-in-picture overlay (a recording setting; moved here from MainWindowViewModel) ---

    public ReactiveCommand<Unit, Unit> RefreshWebcamDevicesCommand { get; }

    private bool _enableWebcam = false;
    public bool EnableWebcam
    {
        get => _enableWebcam;
        set
        {
            this.RaiseAndSetIfChanged(ref _enableWebcam, value);
            // Auto-list cameras the moment the feature is switched on, so the dropdown is ready.
            if (value && WebcamDevices.Count == 0)
            {
                RefreshWebcamDevices();
            }
        }
    }

    private string _webcamDeviceName = string.Empty;
    public string WebcamDeviceName
    {
        get => _webcamDeviceName;
        set => this.RaiseAndSetIfChanged(ref _webcamDeviceName, value);
    }

    // Auto-detected webcam names (DirectShow on Windows, V4L2 /dev/video* on Linux). The settings combo is
    // editable, so a hand-typed name (or a /dev/videoN path on Linux) still works if enumeration comes back
    // empty (e.g. a virtual camera not exposed to dshow).
    public ObservableCollection<string> WebcamDevices { get; } = new();

    /// <summary>Re-enumerates connected webcams and, if nothing is selected yet, picks the first one.</summary>
    public void RefreshWebcamDevices()
    {
        try
        {
            var found = Services.Core.Media.VideoInputDevices.Enumerate();
            WebcamDevices.Clear();
            foreach (var device in found)
            {
                WebcamDevices.Add(device.Name);
            }

            if (string.IsNullOrWhiteSpace(WebcamDeviceName) && WebcamDevices.Count > 0)
            {
                WebcamDeviceName = WebcamDevices[0];
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("RecordingSettingsViewModel.RefreshWebcamDevices", ex);
        }
    }

    private int _webcamCorner = 3;
    public int WebcamCorner
    {
        get => _webcamCorner;
        set => this.RaiseAndSetIfChanged(ref _webcamCorner, value);
    }

    // Corner choices for the webcam PiP combo (value = corner index used by the encoder).
    public IReadOnlyList<WebcamCornerOption> WebcamCornerOptions { get; } = new[]
    {
        new WebcamCornerOption(0, "WebcamCornerTopLeft"),
        new WebcamCornerOption(1, "WebcamCornerTopRight"),
        new WebcamCornerOption(2, "WebcamCornerBottomLeft"),
        new WebcamCornerOption(3, "WebcamCornerBottomRight"),
    };

    public sealed record WebcamCornerOption(int Value, string LocalizationKey)
    {
        public string Name => LocalizationService.Instance[LocalizationKey];
    }

    private int _webcamSize = 1;
    public int WebcamSize
    {
        get => _webcamSize;
        set => this.RaiseAndSetIfChanged(ref _webcamSize, value);
    }

    // PiP size choices (value = size index used by the encoder: 0=Small, 1=Medium, 2=Large).
    public IReadOnlyList<WebcamCornerOption> WebcamSizeOptions { get; } = new[]
    {
        new WebcamCornerOption(0, "WebcamSizeSmall"),
        new WebcamCornerOption(1, "WebcamSizeMedium"),
        new WebcamCornerOption(2, "WebcamSizeLarge"),
    };

    private bool _webcamCircular = false;
    public bool WebcamCircular
    {
        get => _webcamCircular;
        set => this.RaiseAndSetIfChanged(ref _webcamCircular, value);
    }

    // --- Microphone input (a recording setting; moved here from MainWindowViewModel) ---

    private bool _recordMicrophone = false;
    public bool RecordMicrophone
    {
        get => _recordMicrophone;
        set => this.RaiseAndSetIfChanged(ref _recordMicrophone, value);
    }

    private string _selectedMicDeviceId = string.Empty;
    public string SelectedMicDeviceId
    {
        get => _selectedMicDeviceId;
        set => this.RaiseAndSetIfChanged(ref _selectedMicDeviceId, value);
    }

    private double _micVolume = 1.0;
    public double MicVolume
    {
        get => _micVolume;
        set => this.RaiseAndSetIfChanged(ref _micVolume, Math.Clamp(value, 0.0, 2.0));
    }

    // Available microphone / capture devices (first entry = system default, empty id).
    public System.Collections.Generic.IReadOnlyList<GimmeCapture.Services.Core.Media.AudioInputDevice> MicInputDevices { get; } =
        GimmeCapture.Services.Core.Media.AudioInputDevices.Enumerate();
}
