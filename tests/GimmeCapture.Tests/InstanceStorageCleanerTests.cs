using System;
using System.IO;

namespace GimmeCapture.Tests;

// What the start-up sweep may delete, and what it must leave. On the owner's machine the data folder held 203 empty
// instance folders, a 671 MB downloaded ONNX Runtime per AI folder, the MarianMT and Vosk models of features long gone,
// and a 3.4 GB unused copy of every model beside the AI folder the settings use.
public sealed class InstanceStorageCleanerTests : IDisposable
{
    private const string CurrentKey = "0123456789abcdef";
    private const string OtherKey = "fedcba9876543210";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N"));

    private string Instances => Path.Combine(_root, "instances");
    private string SharedAI => Path.Combine(Instances, CurrentKey, "shared", "AI");
    private string AppAI => Path.Combine(_root, "app", "AI");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private string Write(string path, int length = 4)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[length]);
        return path;
    }

    private InstanceStorageCleaner.SweepResult Sweep(string aiDirectoryInUse, TimeSpan later) =>
        InstanceStorageCleaner.Sweep(
            new InstanceStorageCleaner.Layout(Instances, CurrentKey, SharedAI, AppAI, aiDirectoryInUse),
            DateTime.UtcNow + later);

    [Fact]
    public void AnotherInstanceHoldingNoFiles_IsRemovedOnceItIsAnHourOld()
    {
        Directory.CreateDirectory(Path.Combine(Instances, OtherKey, "versions", "0.68.1"));

        Assert.Equal(0, Sweep(SharedAI, TimeSpan.Zero).EmptyInstancesRemoved);
        Assert.True(Directory.Exists(Path.Combine(Instances, OtherKey)));

        Assert.Equal(1, Sweep(SharedAI, TimeSpan.FromHours(2)).EmptyInstancesRemoved);
        Assert.False(Directory.Exists(Path.Combine(Instances, OtherKey)));
    }

    [Fact]
    public void ThisInstance_AnInstanceWithFiles_AndFoldersThatAreNotInstances_AreKept()
    {
        Directory.CreateDirectory(Path.Combine(Instances, CurrentKey));
        string config = Write(Path.Combine(Instances, OtherKey, "versions", "0.68.1", "config.json"));
        Directory.CreateDirectory(Path.Combine(Instances, "notes"));

        Assert.Equal(0, Sweep(SharedAI, TimeSpan.FromDays(30)).EmptyInstancesRemoved);

        Assert.True(Directory.Exists(Path.Combine(Instances, CurrentKey)));
        Assert.True(File.Exists(config));
        Assert.True(Directory.Exists(Path.Combine(Instances, "notes")));
    }

    [Fact]
    public void FilesOfFeaturesThatAreGone_AreRemoved_AndTheModelsInUseKept()
    {
        Write(Path.Combine(SharedAI, "runtime", "onnxruntime.dll"), 10);
        Write(Path.Combine(SharedAI, "runtime", "onnxruntime_providers_cuda.dll"), 20);
        Write(Path.Combine(SharedAI, "runtime", "temp_ai.zip"));
        Write(Path.Combine(SharedAI, "runtime", "temp_ai.zip.tmp"));
        Write(Path.Combine(SharedAI, "nmt", "encoder_model.onnx"));
        Write(Path.Combine(SharedAI, "nmt", "tokenizer.json"));
        Write(Path.Combine(SharedAI, "stt", "vosk-model-small-en-us-0.15", "am", "final.mdl"));
        Write(Path.Combine(SharedAI, "models", "mobile_sam_image_encoder.onnx"));
        string u2net = Write(Path.Combine(SharedAI, "models", "u2net.onnx"));

        var result = Sweep(SharedAI, TimeSpan.Zero);

        Assert.False(Directory.Exists(Path.Combine(SharedAI, "runtime")));
        Assert.False(Directory.Exists(Path.Combine(SharedAI, "nmt")));
        Assert.False(Directory.Exists(Path.Combine(SharedAI, "stt")));
        Assert.False(File.Exists(Path.Combine(SharedAI, "models", "mobile_sam_image_encoder.onnx")));
        Assert.True(File.Exists(u2net));
        Assert.Equal(8, result.FilesRemoved);
        Assert.Equal(10 + 20 + (6 * 4), result.BytesFreed);
    }

    // The AI folder in use can be one the user picked, where a folder named runtime could hold anything.
    [Fact]
    public void AFolderOfAnObsoleteFeaturesName_IsKept_WhenItHoldsAnythingTheAppDidNotPutThere()
    {
        string picked = Path.Combine(_root, "picked");
        string dll = Write(Path.Combine(picked, "runtime", "onnxruntime.dll"));
        string notes = Write(Path.Combine(picked, "runtime", "notes.txt"));
        string mine = Write(Path.Combine(picked, "nmt", "mine.bin"));
        string speech = Write(Path.Combine(picked, "stt", "my-recordings", "a.wav"));

        Sweep(picked, TimeSpan.Zero);

        Assert.True(File.Exists(dll));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(mine));
        Assert.True(File.Exists(speech));
    }

    [Fact]
    public void ADownloadAKilledProcessLeftHalfDone_IsRemoved_ButNotOneStillRunning()
    {
        string model = Write(Path.Combine(SharedAI, "models", "u2net.onnx.part"));
        string recognizer = Write(Path.Combine(SharedAI, "ocr", "ocr_rec_ch.onnx.part"));
        string dictionary = Write(Path.Combine(SharedAI, "ocr", "ocr_dict_ch.txt.part"));
        string llm = Write(Path.Combine(SharedAI, "llm", "models", "translategemma-4b_Q4_K_M.gguf.part"));
        string oldDownloader = Write(Path.Combine(SharedAI, "models", "sam2_hiera_tiny_encoder.onnx.tmp"));
        string unknown = Write(Path.Combine(SharedAI, "models", "something.part"));

        Sweep(SharedAI, TimeSpan.FromHours(1));
        Assert.True(File.Exists(model));
        Assert.True(File.Exists(llm));

        Sweep(SharedAI, TimeSpan.FromHours(13));
        Assert.False(File.Exists(model));
        Assert.False(File.Exists(recognizer));
        Assert.False(File.Exists(dictionary));
        Assert.False(File.Exists(llm));
        Assert.False(File.Exists(oldDownloader));
        Assert.True(File.Exists(unknown));
    }

    // The installed copy's settings named its own AI folder, and older versions copied that folder into the shared one
    // on every start. Nothing ever read the copy.
    [Fact]
    public void TheSharedFolderCopiesOfTheFolderInUse_AreRemoved_AndWhatOnlyTheSharedFolderHasIsKept()
    {
        Write(Path.Combine(AppAI, "models", "u2net.onnx"), 10);
        Write(Path.Combine(AppAI, "llm", "models", "translategemma-4b_Q4_K_M.gguf"), 30);
        Write(Path.Combine(AppAI, "ocr", "ocr_det.onnx"), 6);
        string copy = Write(Path.Combine(SharedAI, "models", "u2net.onnx"), 10);
        string llmCopy = Write(Path.Combine(SharedAI, "llm", "models", "translategemma-4b_Q4_K_M.gguf"), 30);
        string different = Write(Path.Combine(SharedAI, "ocr", "ocr_det.onnx"), 5);
        string onlyHere = Write(Path.Combine(SharedAI, "models", "sam2_hiera_tiny_encoder.onnx"), 7);

        var result = Sweep(AppAI, TimeSpan.Zero);

        Assert.False(File.Exists(copy));
        Assert.False(File.Exists(llmCopy));
        Assert.False(Directory.Exists(Path.Combine(SharedAI, "llm")));
        Assert.True(File.Exists(different));
        Assert.True(File.Exists(onlyHere));
        Assert.True(File.Exists(Path.Combine(AppAI, "models", "u2net.onnx")));
        Assert.Equal(40, result.BytesFreed);
    }

    [Fact]
    public void TheSharedFolder_IsLeftAlone_WhenTheFolderInUseIsMissing()
    {
        string model = Write(Path.Combine(SharedAI, "models", "u2net.onnx"));

        Sweep(Path.Combine(_root, "gone"), TimeSpan.Zero);

        Assert.True(File.Exists(model));
    }

    [Fact]
    public void TheSharedFolder_IsLeftAlone_WhenItIsTheFolderInUse()
    {
        string model = Write(Path.Combine(SharedAI, "models", "u2net.onnx"));
        Write(Path.Combine(AppAI, "models", "u2net.onnx"));

        Sweep(SharedAI, TimeSpan.Zero);

        Assert.True(File.Exists(model));
    }

    // The record is how a later sweep, or the Modules tab, tells an install whose folder is gone from one still there.
    [Fact]
    public void ASweep_RecordsTheFolderThisInstallRunsFrom()
    {
        InstanceStorageCleaner.Sweep(Path.Combine(_root, "AI"));

        string appDirectory = RuntimePathProvider.GetExecutableDirectory();
        var record = InstallInstanceRecord.Read(AppStoragePaths.GetInstallInstanceDirectory(appDirectory));
        Assert.NotNull(record);
        Assert.Equal(appDirectory, record.AppDirectory);
        Assert.Equal(AppVersionInfo.CurrentVersion, record.Version);
    }
}
