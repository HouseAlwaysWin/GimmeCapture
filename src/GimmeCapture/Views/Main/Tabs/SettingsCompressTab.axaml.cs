using System;
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
    }
}
