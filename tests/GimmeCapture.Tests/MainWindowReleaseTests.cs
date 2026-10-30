using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main;

namespace GimmeCapture.Tests;

/// <summary>
/// The main window, hidden in the tray, is let go of (<see cref="MainWindow.Release"/>) and built again on the next open.
/// The view model outlives it, so nothing the window or its tabs wired into the view model may keep it: the Compress tab's
/// file pickers did. And closing it for real must not write into any setting, as the Compress queue's dropdowns once
/// nulled their files' choices that way. Opt-in (MAIN_WINDOW_PROBE=1) because it sets up a headless Avalonia
/// application, which a process can do only once.
/// </summary>
public sealed class MainWindowReleaseTests
{
    [OptInFact("MAIN_WINDOW_PROBE=1")]
    public void AReleasedMainWindow_IsCollected_AndLeavesEverySettingAsItWas()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var vm = new MainWindowViewModel();
        // Applying saved settings goes through the UI thread, which in a headless test is this one: pump it meanwhile,
        // without an await, after which the test could go on on another thread.
        var settingsLoad = vm.InitialSettingsLoadTask;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!settingsLoad.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }

        settingsLoad.GetAwaiter().GetResult();
        string settingsBefore = JsonSerializer.Serialize(vm.CreateSettingsSnapshot());

        var released = OpenEveryTabThenRelease(vm);
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(released.Window.IsAlive, "The released main window is still reachable.");
        Assert.False(released.FirstTab.IsAlive, "A tab of the released main window is still reachable.");
        Assert.Null(vm.PickFolderAction);
        Assert.Null(vm.PickCompressFilesAction);
        Assert.Equal(settingsBefore, JsonSerializer.Serialize(vm.CreateSettingsSnapshot()));

        // The next open builds a window that works as the first did.
        var reopened = new MainWindow(vm, null);
        reopened.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(vm.PickFolderAction);
        reopened.Release();
        Dispatcher.UIThread.RunJobs();
    }

    // In a method of its own, so no local of the test keeps the window.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Window, WeakReference FirstTab) OpenEveryTabThenRelease(MainWindowViewModel vm)
    {
        var window = new MainWindow(vm, null) { Width = 1100, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var tabs = window.FindControl<TabControl>("MainTabControl")!;
        object? firstTab = ((TabItem)tabs.Items[0]!).Content;
        for (int index = 0; index < tabs.ItemCount; index++)
        {
            tabs.SelectedIndex = index;
            Dispatcher.UIThread.RunJobs();
        }

        Assert.NotNull(vm.PickCompressFilesAction);

        // Closed to the tray first, as the user does: the window hides, then sits there until it is let go of.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
        window.Release();
        Dispatcher.UIThread.RunJobs();
        return (new WeakReference(window), new WeakReference(firstTab));
    }
}
