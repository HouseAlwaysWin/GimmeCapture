using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Platforms.Desktop;

public sealed class AvaloniaCaptureVisibilityCoordinator : ICaptureVisibilityCoordinator
{
    public async Task HideAndWaitForCaptureAsync(
        Action hideAction,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hideAction);
        cancellationToken.ThrowIfCancellationRequested();

        await Dispatcher.UIThread.InvokeAsync(hideAction, DispatcherPriority.Send, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Render, cancellationToken);
        await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background, cancellationToken);
    }
}
