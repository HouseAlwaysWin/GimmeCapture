using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

/// <summary>
/// The Compress tab's first open removes the resume chunks of compresses abandoned long ago
/// (<see cref="CompressSegmentStore.PruneOrphans"/>), never the dir of one under way: its state is written with an
/// atomic replace, so a fresh dir can be without state.json for a moment, and removing it then failed the compress's
/// first chunk. The dirs live under the test run's own data folder (TestStorageRoot), with fresh input names.
/// </summary>
public class CompressSegmentStoreTests
{
    private static string NewDir(bool withState, TimeSpan age)
    {
        string input = Path.Combine(Path.GetTempPath(), "GimmeCapture.Tests", Guid.NewGuid().ToString("N") + ".mp4");
        string dir = CompressSegmentStore.DirForInput(input);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "chunk_0.mp4"), [0]);
        DateTime then = DateTime.UtcNow - age;
        if (withState)
        {
            string state = Path.Combine(dir, "state.json");
            File.WriteAllText(state, "{}");
            File.SetLastWriteTimeUtc(state, then);
        }

        Directory.SetLastWriteTimeUtc(dir, then);
        return dir;
    }

    [Fact]
    public void ADirBeingSetUp_WithNoStateYet_IsKept()
    {
        string dir = NewDir(withState: false, TimeSpan.Zero);

        CompressSegmentStore.PruneOrphans();

        Assert.True(Directory.Exists(dir));
        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public void ADirWithNoState_UntouchedForTheGrace_IsRemoved()
    {
        string dir = NewDir(withState: false, CompressSegmentStore.StatelessGrace + TimeSpan.FromMinutes(5));

        CompressSegmentStore.PruneOrphans();

        Assert.False(Directory.Exists(dir));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(8, false)]
    public void ADirWithState_IsKeptForAWeek(int daysOld, bool kept)
    {
        string dir = NewDir(withState: true, TimeSpan.FromDays(daysOld));

        CompressSegmentStore.PruneOrphans();

        Assert.Equal(kept, Directory.Exists(dir));
        if (kept)
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
