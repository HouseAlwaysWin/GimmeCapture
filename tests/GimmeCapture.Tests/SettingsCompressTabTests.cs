using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GimmeCapture.Converters;
using GimmeCapture.Models;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Views.Main.Tabs;
using Xunit.Abstractions;

namespace GimmeCapture.Tests;

/// <summary>
/// The Compress tab's queue. Its rows each hold two dropdowns bound both ways to the file's preset and output category,
/// and eight buttons. The headless test builds the tab with the user's 79-file queue; it is opt-in
/// (COMPRESS_TAB_PROBE=1) because it sets up an Avalonia application, which a process can do only once, and it prints
/// what each build took.
/// </summary>
public sealed class SettingsCompressTabTests
{
    private const int QueueLength = 79;
    private readonly ITestOutputHelper _output;

    public SettingsCompressTabTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ADropdownLosingItsOptions_WritesNothingBack()
    {
        // Every option is a real choice, so a null coming back is the dropdown being torn down.
        var converter = IgnoreNullWriteBackConverter.Instance;
        var choice = new CompressPreset { Name = "A" };

        Assert.Same(choice, converter.Convert(choice, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(choice, converter.ConvertBack(choice, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Same(Avalonia.Data.BindingOperations.DoNothing, converter.ConvertBack(null, typeof(object), null, System.Globalization.CultureInfo.InvariantCulture));
    }

    [OptInFact("COMPRESS_TAB_PROBE=1")]
    public async Task TheQueue_BuildsOnlyTheRowsInView_AndLeavesEachFilesChoicesAlone()
    {
        AppBuilder.Configure<GimmeCapture.App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        var vm = new MainWindowViewModel();
        await vm.InitialSettingsLoadTask;
        vm.BatchPresetChoices.Clear();
        vm.BatchPresetChoices.Add(vm._currentSettingsChoice);
        vm.BatchPresetChoices.Add(new CompressPreset { Name = "A" });
        vm.BatchPresetChoices.Add(new CompressPreset { Name = "B" });
        vm.CompressCategoryChoices.Clear();
        vm.CompressCategoryChoices.Add(string.Empty);
        vm.CompressCategoryChoices.Add(@"C:\area\one");
        vm.CompressCategoryChoices.Add(@"C:\area\two");
        for (int i = 0; i < QueueLength; i++)
        {
            vm.CompressQueue.Add(new CompressQueueItem($@"C:\nowhere\a clip with a fairly long name {i:00}.mp4")
            {
                SelectedPreset = PresetOf(vm, i),
                SelectedOutputDir = CategoryOf(vm, i),
            });
        }

        // A first build pays for loading the tab itself; the second is what a warm app pays. The queue used to build
        // every row the first time the tab opened, though its box shows four or five: 9,300 controls, 150 MB
        // allocated and a 0.77 s freeze in the user's log.
        Build(vm, "cold").Close();
        var window = Build(vm, "warm");
        int rows = window.GetVisualDescendants().OfType<Control>()
            .Select(c => c.DataContext).OfType<CompressQueueItem>().Distinct().Count();
        Assert.InRange(rows, 1, 10);

        // The panel hands a row scrolled out of view to the next file scrolled in; its dropdowns must not write one
        // file's choice into another.
        int writes = 0;
        foreach (var item in vm.CompressQueue)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(CompressQueueItem.SelectedPreset) or nameof(CompressQueueItem.SelectedOutputDir))
                {
                    writes++;
                }
            };
        }

        var scroller = window.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.MaxHeight == 240);
        double extent = scroller.Extent.Height;
        for (double y = 0; y <= extent; y += 37)
        {
            scroller.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
        }

        for (double y = extent; y >= 0; y -= 53)
        {
            scroller.Offset = new Vector(0, y);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.Equal(0, writes);

        // Closing the window, as the app does at exit, used to null the choices of every row built.
        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, writes);
        for (int i = 0; i < QueueLength; i++)
        {
            Assert.Same(PresetOf(vm, i), vm.CompressQueue[i].SelectedPreset);
            Assert.Equal(CategoryOf(vm, i), vm.CompressQueue[i].SelectedOutputDir);
        }
    }

    private static CompressPreset PresetOf(MainWindowViewModel vm, int file) => vm.BatchPresetChoices[file % 3];

    private static string CategoryOf(MainWindowViewModel vm, int file) => vm.CompressCategoryChoices[(file / 3) % 3];

    private Window Build(MainWindowViewModel vm, string label)
    {
        Dispatcher.UIThread.RunJobs();
        int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        var window = new Window { Content = new SettingsCompressTab { DataContext = vm }, Width = 1100, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        _output.WriteLine(
            $"{label}: {stopwatch.ElapsedMilliseconds} ms, {window.GetVisualDescendants().Count()} visuals, " +
            $"{(GC.GetAllocatedBytesForCurrentThread() - allocated) / (1024 * 1024)} MB allocated, " +
            $"collections +{GC.CollectionCount(0) - gen0}/+{GC.CollectionCount(1) - gen1}/+{GC.CollectionCount(2) - gen2}");
        return window;
    }
}
