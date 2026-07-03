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

            var videoTypes = GimmeCapture.Views.Shared.VideoFilePickerTypes.OpenVideos;

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

        // Advanced video editing: open a standalone editor window for the prepared view model.
        vm.OpenEditorAction = editVm =>
        {
            var loc = GimmeCapture.Services.Core.Infrastructure.LocalizationService.Instance;

            // Changing crop/rotation clears surface-space annotations/redaction — confirm via the main dialog.
            editVm.ConfirmTransformClearsEditsAction = () =>
                vm.ConfirmYesNoAction?.Invoke(loc["CompressEditWindowTitle"], loc["CompressEditTransformClearsEdits"])
                ?? System.Threading.Tasks.Task.FromResult(true);

            // Freeze-frame → a plain floating image pin (no AI): just the current frame pinned out.
            // The user can enter point-removal / background-removal from the pinned image's own toolbar.
            editVm.FreezeFrameToImagePinAction = bitmap => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var pinVm = new GimmeCapture.ViewModels.Floating.FloatingImageViewModel(
                    bitmap, bitmap.Size.Width, bitmap.Size.Height,
                    Avalonia.Media.Colors.Red, 2, false, false,
                    new GimmeCapture.Services.Core.Infrastructure.ClipboardService(),
                    vm.AIResourceService, vm.SAM2RuntimeService, vm.AppSettingsService,
                    vm.AIPathService, vm.ResourceQueue, null, 0.0);
                var pinWin = new GimmeCapture.Views.Floating.FloatingImageWindow
                {
                    DataContext = pinVm,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen,
                };
                pinWin.Show();
            });

            var window = new VideoEditWindow { DataContext = editVm };
            window.Show();
        };
    }
}
