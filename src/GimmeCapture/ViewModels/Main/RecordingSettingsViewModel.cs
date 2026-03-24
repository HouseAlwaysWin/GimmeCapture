using ReactiveUI;
using GimmeCapture.Models;
using GimmeCapture.Services.Core;

namespace GimmeCapture.ViewModels.Main;

public class RecordingSettingsViewModel : ViewModelBase
{
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

    public VideoCodecOption[] VideoCodecOptions { get; } = {
        new VideoCodecOption { Value = VideoCodec.H264 },
        new VideoCodecOption { Value = VideoCodec.H265 }
    };

    public VideoQualityOption[] VideoQualityOptions { get; } = {
        new VideoQualityOption { Value = VideoQuality.Low },
        new VideoQualityOption { Value = VideoQuality.Medium },
        new VideoQualityOption { Value = VideoQuality.High }
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

    private bool _useFixedRecordPath;
    public bool UseFixedRecordPath
    {
        get => _useFixedRecordPath;
        set => this.RaiseAndSetIfChanged(ref _useFixedRecordPath, value);
    }
}
