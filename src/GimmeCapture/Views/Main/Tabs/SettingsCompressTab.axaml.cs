using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Views.Main.Tabs;

public partial class SettingsCompressTab : UserControl
{
    public SettingsCompressTab()
    {
        InitializeComponent();
    }

    // Wire the file pickers to THIS control's TopLevel StorageProvider once the
    // MainWindowViewModel is attached (same pattern as FloatingVideoWindow's save picker).
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not MainWindowViewModel vm)
        {
            return;
        }

        vm.PickCompressInputAction = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return null;
            }

            var videoTypes = new FilePickerFileType("Video Files")
            {
                Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv", "*.flv" }
            };
            var allFiles = new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } };

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["CompressSelectFile"],
                AllowMultiple = false,
                FileTypeFilter = new[] { videoTypes, allFiles }
            });

            return files.Count > 0 ? files[0].Path.LocalPath : null;
        };

        vm.PickCompressOutputAction = async suggested =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return null;
            }

            string suggestedName = string.IsNullOrEmpty(suggested) ? "compressed.mp4" : Path.GetFileName(suggested);
            string suggestedExt = Path.GetExtension(suggestedName).TrimStart('.');

            // Offer a save type matching the suggested container plus the other supported ones.
            var types = new[]
            {
                new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4" } },
                new FilePickerFileType("Matroska Video") { Patterns = new[] { "*.mkv" } },
                new FilePickerFileType("QuickTime Video") { Patterns = new[] { "*.mov" } }
            };

            IStorageFolder? startFolder = null;
            string? suggestedDir = string.IsNullOrEmpty(suggested) ? null : Path.GetDirectoryName(suggested);
            if (!string.IsNullOrEmpty(suggestedDir))
            {
                startFolder = await topLevel.StorageProvider.TryGetFolderFromPathAsync(suggestedDir);
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["CompressChooseOutput"],
                SuggestedFileName = suggestedName,
                DefaultExtension = suggestedExt,
                FileTypeChoices = types,
                SuggestedStartLocation = startFolder
            });

            return file?.Path.LocalPath;
        };

        // Batch queue: multi-file picker.
        vm.PickCompressFilesAction = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return Array.Empty<string>();
            }

            var videoTypes = new FilePickerFileType("Video Files")
            {
                Patterns = new[] { "*.mp4", "*.mkv", "*.mov", "*.avi", "*.webm", "*.m4v", "*.wmv", "*.flv" }
            };

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = LocalizationService.Instance["CompressQueueAddFiles"],
                AllowMultiple = true,
                FileTypeFilter = new[] { videoTypes }
            });

            return files.Select(f => f.Path.LocalPath).ToList();
        };

        // Batch queue: folder picker (its videos are enumerated by the view model).
        vm.PickCompressFolderAction = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return null;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance["CompressQueueAddFolder"],
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
        };
    }
}
