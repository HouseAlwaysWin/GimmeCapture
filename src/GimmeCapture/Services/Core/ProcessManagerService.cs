using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CliWrap;

namespace GimmeCapture.Services.Core;

public class ProcessManagerService
{
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _activeProcesses = new();

    public static async Task StartProcessAsync(string key, string fileName, string arguments)
    {
        // 1. Kill existing if any
        await StopProcessAsync(key);

        var cts = new CancellationTokenSource();
        _activeProcesses[key] = cts;

        try
        {
            await Cli.Wrap(fileName)
                .WithArguments(arguments)
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal exit via cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Process Managed Error ({key}): {ex.Message}");
        }
        finally
        {
            _activeProcesses.TryRemove(key, out _);
        }
    }

    public static async Task StopProcessAsync(string key)
    {
        if (_activeProcesses.TryRemove(key, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch { /* Ignore */ }
        }
        await Task.CompletedTask;
    }
}
