using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Core.AI;

/// <summary>
/// Keeps open the GPU device a service's sessions run on, between one session and the next, until it has gone unused
/// for a while. DirectML closes its device with the last session or options using it, so a service that frees its
/// session when idle reopened the device with every rebuild, about 0.2 s each time. Holding it keeps about 45 MB.
/// </summary>
internal sealed class GpuDeviceHold
{
    private readonly Func<IDisposable> _holdDevice;
    private readonly IdleReleaseScheduler _idleRelease;
    private readonly object _lock = new();
    private IDisposable? _device;

    /// <param name="idleRelease">How long the device stays held after the last <see cref="NotifyUse"/>.</param>
    public GpuDeviceHold(TimeSpan idleRelease)
        : this(OnnxProviderConfigurator.HoldGpuDevice, idleRelease)
    {
    }

    /// <summary><paramref name="holdDevice"/> stands in for opening the device and <paramref name="delayAsync"/> for
    /// the wait, so tests need neither a GPU nor real time.</summary>
    internal GpuDeviceHold(
        Func<IDisposable> holdDevice,
        TimeSpan idleRelease,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        _holdDevice = holdDevice ?? throw new ArgumentNullException(nameof(holdDevice));
        _idleRelease = new IdleReleaseScheduler(idleRelease, Release, delayAsync);
    }

    /// <summary>True while a device is held.</summary>
    public bool IsHolding
    {
        get
        {
            lock (_lock)
            {
                return _device != null;
            }
        }
    }

    /// <summary>
    /// Call with the provider a session was just built on. On a GPU, holds its device: the session has it open
    /// already, so that takes a few ms. On the CPU, lets go of any device held, so one that just failed to build a
    /// session (after a driver reset, say) is opened afresh next time, and a PC without a usable GPU holds nothing.
    /// </summary>
    public void SessionBuilt(string provider)
    {
        if (provider == OnnxProviderConfigurator.CpuProvider)
        {
            Release();
            return;
        }

        lock (_lock)
        {
            _device ??= _holdDevice();
        }

        _idleRelease.NotifyUse();
    }

    /// <summary>Call after each use of the sessions: the device is let go once none has followed for the idle delay.</summary>
    public void NotifyUse() => _idleRelease.NotifyUse();

    /// <summary>Lets the device close once no session uses it. A later GPU session holds it again.</summary>
    public void Release()
    {
        _idleRelease.Cancel();
        IDisposable? device;
        lock (_lock)
        {
            device = _device;
            _device = null;
        }

        device?.Dispose();
    }
}
