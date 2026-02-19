using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using GimmeCapture.ViewModels.Floating;
using System;

namespace GimmeCapture.Views.Floating;

public partial class FloatingTranslationWindow : FloatingWindowBase
{
    public FloatingTranslationWindow()
    {
        InitializeComponent();

        // 同步視窗位置到 ViewModel
        PositionChanged += (s, e) =>
        {
            if (DataContext is FloatingTranslationViewModel vm)
            {
                vm.ScreenPosition = Position;
            }
        };

        // 同步 Toolbar 尺寸到 ViewModel
        var toolbar = this.FindControl<Border>("ToolbarBorder");
        if (toolbar != null)
        {
            toolbar.GetObservable(Visual.BoundsProperty).Subscribe(bounds =>
            {
                if (DataContext is FloatingTranslationViewModel vm)
                {
                    vm.ToolbarWidth = bounds.Width;
                    vm.ToolbarHeight = bounds.Height;
                }
            });
        }
    }

    protected override Control? GetContentControl() => this.FindControl<Image>("PinnedImage");

    protected override Bitmap? GetContentSnapshot() => (DataContext as FloatingTranslationViewModel)?.Image;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is FloatingTranslationViewModel vm)
        {
            // 監聽 Image 變化同步視窗大小
            vm.PropertyChanged += (s, ev) =>
            {
                if (ev.PropertyName == nameof(FloatingTranslationViewModel.Image))
                {
                    SyncWindowSizeToContent();
                }
            };
        }
    }
}
