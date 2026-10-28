using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading;
using GimmeCapture.Models;

namespace GimmeCapture.ViewModels.Floating;

/// <summary>
/// The kept pieces of a video pin's timeline, on their way from the UI thread to the pin's playback loop. The loop runs
/// on the thread pool, and it read EditSegments, the collection the timeline strip is bound to, at every pass while the
/// UI thread could be changing it. It reads a copy from here instead. The UI thread takes one when playback starts, and
/// again once each change to the pieces is complete: a split or a merge empties the collection and adds the pieces back
/// one at a time, and the loop must never play a list half rebuilt. A pass already playing keeps the pieces it started
/// with; the next pass plays the edit.
/// </summary>
internal sealed class KeptSegmentsHandoff
{
    private readonly ObservableCollection<VideoEditSegment> _segments;
    private readonly Action<Action> _postToUiThread;
    private VideoEditSegment[] _kept;
    private bool _refreshPosted; // UI thread

    /// <param name="segments">The timeline's pieces, which only the UI thread changes.</param>
    /// <param name="postToUiThread">Runs an action on the UI thread once the work in hand is done, as Dispatcher.Post does.</param>
    public KeptSegmentsHandoff(ObservableCollection<VideoEditSegment> segments, Action<Action> postToUiThread)
    {
        _segments = segments;
        _postToUiThread = postToUiThread;
        _kept = KeptOf(segments);
        segments.CollectionChanged += OnSegmentsChanged;
    }

    /// <summary>Any thread: the kept pieces as of the last complete change.</summary>
    public VideoEditSegment[] Kept => Volatile.Read(ref _kept);

    /// <summary>UI thread: takes the kept pieces as they are now, for a playback that is starting, and returns them.</summary>
    public VideoEditSegment[] Refresh()
    {
        VideoEditSegment[] kept = KeptOf(_segments);
        Volatile.Write(ref _kept, kept);
        return kept;
    }

    // UI thread. A change is complete once the work that made it is done, so the copy is taken then, once per burst.
    private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_refreshPosted)
        {
            return;
        }

        _refreshPosted = true;
        _postToUiThread(() =>
        {
            _refreshPosted = false;
            Refresh();
        });
    }

    private static VideoEditSegment[] KeptOf(ObservableCollection<VideoEditSegment> segments) =>
        segments.Where(s => s.Kept).ToArray();
}
