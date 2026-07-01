using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

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

        // Batch queue: output-folder picker (empty = save each output next to its source).
        vm.PickCompressOutputFolderAction = async () =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return null;
            }

            var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = LocalizationService.Instance["CompressOutputFolderChoose"],
                AllowMultiple = false
            });

            return folders.Count > 0 ? folders[0].Path.LocalPath : null;
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

        // Quality compare: open a standalone side-by-side window for the prepared view model.
        vm.OpenCompareAction = compareVm =>
        {
            var window = new CompareWindow { DataContext = compareVm };
            window.Show();
        };

        // Advanced video editing: open a standalone editor window for the prepared view model.
        vm.OpenEditorAction = editVm =>
        {
            var window = new VideoEditWindow { DataContext = editVm };
            window.Show();
        };
    }
}
