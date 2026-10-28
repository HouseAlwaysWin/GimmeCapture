using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

// The microphone list used to be built in a field initializer, so building the main view model at start-up enumerated the
// audio devices (COM) on the UI thread at every launch, for a list only the Record tab shows. The tab now has it listed
// on the thread pool the first time it is shown, and is built once the list is in.
public sealed class RecordingSettingsMicDevicesTests
{
    private static readonly AudioInputDevice[] Devices = [new(string.Empty, "Default"), new("{mic-1}", "USB Microphone")];

    [Fact]
    public void BuildingTheMainViewModel_ListsNoMicrophones()
    {
        var viewModel = new MainWindowViewModel(MainWindowViewModel.CreateDesignDependencies());

        Assert.Empty(viewModel.RecordingSettings.MicInputDevices);
    }

    [Fact]
    public async Task TheList_IsReadOffTheCallingThread()
    {
        using var release = new ManualResetEventSlim();
        var viewModel = new RecordingSettingsViewModel(() => null, () =>
        {
            release.Wait(TimeSpan.FromSeconds(30));
            return Devices;
        });

        // Back while the enumeration still waits, so it is not running on this thread.
        Task load = viewModel.LoadMicInputDevicesAsync();
        Assert.False(load.IsCompleted);
        Assert.Empty(viewModel.MicInputDevices);

        release.Set();
        await load;

        Assert.Equal(Devices, viewModel.MicInputDevices);
    }

    [Fact]
    public async Task TheList_IsReadOnce_AndAnnounced()
    {
        int enumerations = 0;
        var viewModel = new RecordingSettingsViewModel(() => null, () =>
        {
            Interlocked.Increment(ref enumerations);
            return Devices;
        });
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Task first = viewModel.LoadMicInputDevicesAsync();
        Task second = viewModel.LoadMicInputDevicesAsync();
        await first;

        Assert.Same(first, second);
        Assert.Equal(1, Volatile.Read(ref enumerations));
        Assert.Contains(nameof(RecordingSettingsViewModel.MicInputDevices), raised);
    }
}
