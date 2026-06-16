using System;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.Services.Abstractions;

namespace GimmeCapture.Services.Core.Infrastructure;

public sealed class DebouncedSettingsSaveCoordinatorFactory : ISettingsSaveCoordinatorFactory
{
    private readonly TimeSpan _delay;

    public DebouncedSettingsSaveCoordinatorFactory(TimeSpan? delay = null)
    {
        _delay = delay ?? TimeSpan.FromMilliseconds(150);
    }

    public ISettingsSaveCoordinator Create(Func<Task<bool>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);
        return new DebouncedSettingsSaveCoordinator(saveAsync, _delay);
    }

    private sealed class DebouncedSettingsSaveCoordinator : ISettingsSaveCoordinator
    {
        private readonly Func<Task<bool>> _saveAsync;
        private readonly TimeSpan _delay;
        private readonly object _gate = new();
        private CancellationTokenSource? _pendingCts;
        private int _version;

        public DebouncedSettingsSaveCoordinator(Func<Task<bool>> saveAsync, TimeSpan delay)
        {
            _saveAsync = saveAsync;
            _delay = delay;
        }

        public void RequestSave()
        {
            CancellationTokenSource cts;
            int version;

            lock (_gate)
            {
                _pendingCts?.Cancel();
                _pendingCts?.Dispose();
                _pendingCts = new CancellationTokenSource();
                cts = _pendingCts;
                version = ++_version;
            }

            RunAsync(version, cts.Token).Forget("Settings.DebouncedSave");
        }

        private async Task RunAsync(int version, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(_delay, cancellationToken);
                lock (_gate)
                {
                    if (version != _version)
                    {
                        return;
                    }
                }

                await _saveAsync();
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
