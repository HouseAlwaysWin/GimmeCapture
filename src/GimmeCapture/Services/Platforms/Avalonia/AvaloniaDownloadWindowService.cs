using System;
using Avalonia.Controls;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using GimmeCapture.Views.Shared;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class AvaloniaDownloadWindowService : IDownloadWindowService
{
    private ProcessingWindow? _downloadWindow;

    public void Update(Window owner, object? dataContext, bool isProcessing)
    {
        bool isBackground = owner.WindowState == WindowState.Minimized || !owner.IsVisible;

        if (isProcessing && isBackground)
        {
            if (_downloadWindow == null)
            {
                try
                {
                    _downloadWindow = new ProcessingWindow
                    {
                        DataContext = dataContext,
                        Title = LocalizationService.Instance["WindowTitleDownloadingResources"]
                    };

                    if (owner.IsVisible)
                    {
                        _downloadWindow.Show(owner);
                    }
                    else
                    {
                        // Hidden owner cannot be used as window owner in Avalonia.
                        _downloadWindow.Show();
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Error("DownloadWindow.Show", ex);
                }
            }
            else
            {
                _downloadWindow.DataContext = dataContext;
                _downloadWindow.Show();
                _downloadWindow.WindowState = WindowState.Normal;
                _downloadWindow.Activate();
            }

            return;
        }

        if (!isProcessing)
        {
            Close();
            return;
        }

        _downloadWindow?.Hide();
    }

    public void Close()
    {
        if (_downloadWindow == null)
        {
            return;
        }

        _downloadWindow.Close();
        _downloadWindow = null;
    }
}
