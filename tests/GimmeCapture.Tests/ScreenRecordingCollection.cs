namespace GimmeCapture.Tests;

/// <summary>
/// Windows lets a process duplicate a display once at a time: a second <c>DuplicateOutput</c> gets E_INVALIDARG, which
/// FFmpeg's ddagrab reports as an invalid argument. A region recording grabs through Desktop Duplication, so two test
/// classes recording at once pushed one of them back to gdigrab, and the test that checks a recording duplicated its
/// display failed for it. The classes that record the screen take turns here, while the rest of the suite runs
/// alongside.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ScreenRecordingCollection
{
    public const string Name = "Screen recording";
}
