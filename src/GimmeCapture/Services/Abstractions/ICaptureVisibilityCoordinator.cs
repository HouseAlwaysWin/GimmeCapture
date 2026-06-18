using System;
using System.Threading;
using System.Threading.Tasks;

namespace GimmeCapture.Services.Abstractions;

public interface ICaptureVisibilityCoordinator
{
    Task HideAndWaitForCaptureAsync(Action hideAction, CancellationToken cancellationToken = default);
}
