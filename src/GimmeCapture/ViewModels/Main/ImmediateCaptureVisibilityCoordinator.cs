using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.ViewModels.Main;

internal sealed class ImmediateCaptureVisibilityCoordinator : ICaptureVisibilityCoordinator
{
    public Task HideAndWaitForCaptureAsync(Action hideAction, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        hideAction();
        return Task.CompletedTask;
    }
}
