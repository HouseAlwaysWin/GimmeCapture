using System;
using System.Collections.Generic;
using System.IO;
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

        vm.PickCompressOutputAction = async (suggestedName) =>
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null)
            {
                return null;
            }

            string ext = Path.GetExtension(suggestedName).TrimStart('.').ToLowerInvariant();
            if (string.IsNullOrEmpty(ext))
            {
                ext = "mp4";
            }

            var types = new Dictionary<string, FilePickerFileType>
            {
                ["mp4"] = new FilePickerFileType("MP4 Video") { Patterns = new[] { "*.mp4" } },
                ["mkv"] = new FilePickerFileType("MKV Video") { Patterns = new[] { "*.mkv" } },
                ["mov"] = new FilePickerFileType("MOV Video") { Patterns = new[] { "*.mov" } },
            };

            // Put the chosen format first so it is the dialog's default filter.
            var choices = new List<FilePickerFileType>();
            if (types.TryGetValue(ext, out var preferred))
            {
                choices.Add(preferred);
            }

            foreach (var kv in types)
            {
                if (kv.Key != ext)
                {
                    choices.Add(kv.Value);
                }
            }

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = LocalizationService.Instance["CompressStart"],
                DefaultExtension = ext,
                ShowOverwritePrompt = true,
                SuggestedFileName = suggestedName,
                FileTypeChoices = choices
            });

            return file?.Path.LocalPath;
        };
    }
}
