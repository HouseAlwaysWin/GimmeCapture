using Avalonia.Controls;
using System;
using GimmeCapture.Models;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class SnipWindowFactory : ISnipWindowFactory
{
    private readonly IWindowManager _windowManager;
    private readonly IScreenLayoutService _screenLayoutService;
    private readonly IWindowLayerService _windowLayerService;
    private readonly IScreenCaptureService _screenCaptureService;

    public SnipWindowFactory(
        IWindowManager windowManager,
        IScreenLayoutService screenLayoutService,
        IWindowLayerService windowLayerService,
        IScreenCaptureService screenCaptureService)
    {
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));
        _screenLayoutService = screenLayoutService ?? throw new ArgumentNullException(nameof(screenLayoutService));
        _windowLayerService = windowLayerService ?? throw new ArgumentNullException(nameof(windowLayerService));
        _screenCaptureService = screenCaptureService ?? throw new ArgumentNullException(nameof(screenCaptureService));
    }

    public void Open(MainWindowViewModel mainViewModel, CaptureMode mode)
    {
        ArgumentNullException.ThrowIfNull(mainViewModel);

        var existing = _windowManager.FindWindowOfType<SnipWindow>();
        if (existing != null)
        {
            if (existing.DataContext is SnipWindowViewModel existingVm)
            {
                existingVm.HandleCaptureModeRequest(mode);
            }

            existing.Activate();
            return;
        }

        var snip = new SnipWindow(_screenLayoutService, _windowLayerService);
        ConfigureWindowBounds(snip);

        var snipVm = new SnipWindowViewModel(
            mainViewModel.BorderColor,
            mainViewModel.BorderThickness,
            mainViewModel.MaskOpacity,
            _screenCaptureService,
            mainViewModel.RecordingService,
            mainViewModel);

        snipVm.AutoActionMode = (int)mode;
        if (mode == CaptureMode.Record)
        {
            snipVm.CurrentMode = SnipMode.Recording;
        }
        else if (mode == CaptureMode.Translate)
        {
            snipVm.CurrentMode = SnipMode.Translation;
            snipVm.InitializeTranslationToolbarPosition();
        }

        snip.DataContext = snipVm;
        snip.Show();
    }

    public SnipWindowViewModel? GetActiveViewModel()
    {
        return _windowManager.FindWindowOfType<SnipWindow>()?.DataContext as SnipWindowViewModel;
    }

    private void ConfigureWindowBounds(Window snip)
    {
        var allScreens = snip.Screens.All;
        if (allScreens.Count == 0)
        {
            return;
        }

        var screenBounds = allScreens.AsValueEnumerable().Select(s => s.Bounds).ToList();
        var primaryScreen = snip.Screens.Primary ?? allScreens.AsValueEnumerable().First();
        double unifiedScaling = primaryScreen.Scaling;

        if (_screenLayoutService.TryGetUnifiedDesktopPlacement(screenBounds, unifiedScaling, out var position, out var size))
        {
            snip.WindowStartupLocation = WindowStartupLocation.Manual;
            snip.Position = position;
            snip.Width = size.Width;
            snip.Height = size.Height;
        }
    }
}
