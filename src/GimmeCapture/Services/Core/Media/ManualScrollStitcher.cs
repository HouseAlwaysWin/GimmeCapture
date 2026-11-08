using System;
using SkiaSharp;

namespace GimmeCapture.Services.Core.Media;

/// <summary>
/// What <see cref="ManualScrollStitcher.Add"/> did with a frame: where it was placed against the strip (strip rows,
/// as <see cref="ScrollStitcher.FrameAlignment"/>), how many rows it added above or below, and why, for the trace.
/// </summary>
/// <param name="Placed">The frame was placed (or, before the fixed bars are known, still shows the first frame).</param>
/// <param name="Guided">The placement was looked for only where the motion measured frame to frame puts the frame.</param>
/// <param name="Predicted">Where that motion put it (strip rows), when <paramref name="Guided"/>.</param>
/// <param name="BestScore">The best alignment score looked at (<c>double.MaxValue</c>: none).</param>
/// <param name="AmbiguityGap">For an unguided search, how much worse the best rival scored (<c>double.MaxValue</c>: none).</param>
/// <param name="TopBand">The rows at the top of this frame kept out of the strip (fixed bar and edge guard).</param>
/// <param name="BottomBand">The rows at the bottom of this frame kept out of the strip.</param>
/// <param name="TrimmedTop">
/// Rows taken back off the strip's top before this frame was placed: the previous frame had added them from rows a bar
/// that has just appeared covers. They are shown again as part of the top band (of the previous frame).
/// </param>
/// <param name="TrimmedBottom">The same at the strip's bottom.</param>
/// <param name="Tentative">
/// The frame was found by searching the whole strip and nothing yet vouches for the place: it is not counted as placed,
/// and adds nothing, until the next frame is placed where the motion from it says.
/// </param>
/// <param name="Shift">The shift measured from the frame before (rows), or null when it could not be measured.</param>
internal readonly record struct ManualScrollStep(
    bool Placed,
    int Offset,
    int Prepended,
    int Appended,
    bool Guided,
    int Predicted,
    double BestScore,
    double AmbiguityGap,
    int TopBand = 0,
    int BottomBand = 0,
    int TrimmedTop = 0,
    int TrimmedBottom = 0,
    bool Tentative = false,
    int? Shift = null)
{
    public bool Grew => Prepended > 0 || Appended > 0;
}

/// <summary>
/// How a capture's stitching went, for its closing log line (<see cref="ManualScrollStitcher.Describe"/>). Every frame
/// is counted once: <c>Frames = Placed + Waited + Tentative + MissedNear + MissedBeyond + MissedLost</c>.
/// </summary>
/// <param name="Frames">Frames handed to the stitcher.</param>
/// <param name="Placed">Frames placed on the strip.</param>
/// <param name="Waited">Frames before the strip started that had not moved far enough from the first to start it.</param>
/// <param name="Unguided">Placements found by searching the whole strip, tentative ones included.</param>
/// <param name="Tentative">Frames found by searching the whole strip that waited for the next frame to vouch for them.</param>
/// <param name="MissedNear">
/// Frames not placed though the strip was where the motion put them: they did not match there (an animation, content
/// that changed, a shift measured wrong).
/// </param>
/// <param name="MissedBeyond">Frames the motion put past the strip's edge: the view had scrolled on past what was kept.</param>
/// <param name="MissedLost">
/// Frames not placed with nothing to say where they were, after a shift that could not be measured, and the whole strip
/// held no clear match for them; also a frame of another size.
/// </param>
/// <param name="ShiftsLost">Frames whose shift from the frame before could not be measured (too far to overlap, or changed).</param>
/// <param name="LargestShift">The largest shift measured between two frames, in rows.</param>
/// <param name="LongestMissRun">The most frames in a row not placed (the capture warns the user after 30).</param>
/// <param name="WindowRows">
/// The rows of each frame matched against the strip, between the bands (0 before the strip starts). A frame that moved
/// further than this from the one before cannot meet the strip where the motion puts it.
/// </param>
/// <param name="Rescued">
/// Placed frames whose shift could not be measured, or was too far for the frame to meet the strip, found on the strip
/// around where the motion had them: the shift was not believed.
/// </param>
internal readonly record struct ManualScrollStats(
    int Frames,
    int Placed,
    int Waited,
    int Unguided,
    int Tentative,
    int MissedNear,
    int MissedBeyond,
    int MissedLost,
    int ShiftsLost,
    int LargestShift,
    int LongestMissRun,
    int WindowRows,
    int Rescued);

/// <summary>
/// Stitches a manual scrolling capture once its axis is known (frames in stitch space, so the content scrolls along
/// the rows). Pure — no UI, no threads — so a whole capture can be replayed in a test; the capture's consumer owns it
/// on one thread.
/// <para>
/// Three things keep it from keeping content twice, which is what users saw:
/// </para>
/// <list type="bullet">
/// <item>It follows the motion. Frames come ~40 ms apart and overlap almost entirely, so the shift between two of them
/// is cheap and close to certain; summed since the last placed frame it says where the next frame sits on the strip,
/// and the frame is looked for only near there. Matching against the whole strip let repeated content — the same
/// phrase or sticker, the rows of a list — place a frame somewhere it could not be, and what it then added was already
/// in the strip. The sum keeps following the view past the strip's edge, so after a fast scroll the frame is placed
/// where it is as soon as it meets the strip again.</item>
/// <item>A frame found without the motion — after a jump between two frames too big for them to overlap — adds nothing
/// until the next frame, followed from it, is placed too. Scrolling faster than frames are grabbed, a frame that met
/// nothing in the strip could still match it somewhere and added content out of place.</item>
/// <item>It leaves out what does not scroll. The rows at the top and bottom that stay put while the content moves — a
/// header bar, a message box — are found from the frames that moved (<see cref="ScrollStitcher.FindFixedBands"/>),
/// also when one appears partway through, and kept out of the strip with a guard of rows next to them where apps float
/// things (<see cref="EdgeGuardRows"/>); the result shows those rows once, from the frames at the strip's two ends.
/// Left in, a message box was added again with every frame scrolled down, and a header with every frame scrolled
/// up.</item>
/// </list>
/// </summary>
internal sealed class ManualScrollStitcher : IDisposable
{
    /// <summary>The least motion between two frames that shows which rows stay put (as the axis lock's threshold).</summary>
    internal const int CalibrationShiftRows = 8;

    /// <summary>
    /// Frames in a row that could not be placed where the motion said, though the strip was in reach there, before the
    /// motion itself is taken to be wrong and the whole strip searched again.
    /// </summary>
    internal const int MaxMissesFollowingMotion = 10;

    /// <summary>The worst score a placement where the motion says may have: the motion vouches for where it is.</summary>
    internal const double GuidedMaxScore = 0.5;

    /// <summary>The worst score a placement found by searching the whole strip may have: nothing vouches for it.</summary>
    internal const double UnguidedMaxScore = 0.2;

    private readonly int _width;
    private readonly int _frameHeight;
    private readonly int _minOverlap;
    private readonly int _ignoreRight;
    private readonly double _rowTolerance;
    private readonly int _edgeGuard;

    // The first frame, until a frame has moved from it far enough to find the fixed bars.
    private SKBitmap? _seed;
    private SKBitmap _previous;
    private ManualScrollStrip? _strip;

    // Rows at the top and bottom of every frame kept out of the strip (fixed bars plus the edge guard, `_guard` of
    // them), and those rows of the frames at the strip's top and bottom, which the result shows.
    private int _guard;
    private int _fixedTop;
    private int _fixedBottom;
    private int _topBand;
    private int _bottomBand;
    private SKBitmap? _header;
    private SKBitmap? _footer;

    // Motion followed since the last placed frame: the summed frame-to-frame shift (rows, positive = the content moved
    // up), whether every shift in it was measured, and the strip offset of the last placed frame. A placement found
    // without the motion is tentative until the next frame confirms it.
    private long _motion;
    private bool _motionValid = true;
    private int _lastShift;
    private bool _tentative;
    private int _lastOffset;
    private int _framesSinceAccepted;
    private int _missesFollowingMotion;

    // What the previous frame added to the strip, taken back if a bar that has just appeared covers where it came from.
    private int _previousPrepended;
    private int _previousAppended;

    // For the capture's closing log line (ManualScrollStats says what each counts).
    private int _frames;
    private int _placed;
    private int _waited;
    private int _unguided;
    private int _tentatives;
    private int _rescued;
    private int _missedNear;
    private int _missedBeyond;
    private int _missedLost;
    private int _shiftsLost;
    private int _largestShift;
    private int _missRun;
    private int _longestMissRun;
    private int _bandChanges;

    /// <summary>How many frames <see cref="DescribeRecent"/> goes back.</summary>
    internal const int RecentFrames = 64;

    private readonly (int? Shift, bool Placed)[] _recent = new (int?, bool)[RecentFrames];
    private int _recentCount;

    /// <param name="seed">The first frame, copied.</param>
    /// <param name="minOverlapRows">The least overlap of a frame with the strip that places it.</param>
    /// <param name="ignoreRightColumns">Columns at the right left out of matching (a scrollbar, a hover toolbar).</param>
    /// <param name="rowMismatchTolerance">The share of overlapping rows that may differ (re-rendered rows).</param>
    /// <param name="edgeGuardRows">Rows next to each edge (or fixed bar) kept out of the strip (see <see cref="EdgeGuardRows"/>).</param>
    public ManualScrollStitcher(
        SKBitmap seed, int minOverlapRows, int ignoreRightColumns, double rowMismatchTolerance, int edgeGuardRows)
    {
        ArgumentNullException.ThrowIfNull(seed);

        _width = seed.Width;
        _frameHeight = seed.Height;
        _minOverlap = Math.Max(1, minOverlapRows);
        _ignoreRight = Math.Max(0, ignoreRightColumns);
        _rowTolerance = rowMismatchTolerance;
        _edgeGuard = Math.Max(0, edgeGuardRows);
        _seed = seed.Copy();
        _previous = seed.Copy();
    }

    public int Width => _width;

    /// <summary>The height of the result: the top band, the strip and the bottom band.</summary>
    public int Height => _strip == null
        ? _frameHeight
        : (_header?.Height ?? 0) + _strip.Height + (_footer?.Height ?? 0);

    /// <summary>Whether the strip has started: a frame moved far enough from the first to find the fixed bars.</summary>
    public bool Started => _strip != null;

    /// <summary>The rows at the top of each frame kept out of the strip (0 until the strip starts).</summary>
    public int TopBand => _topBand;

    /// <summary>The rows at the bottom of each frame kept out of the strip.</summary>
    public int BottomBand => _bottomBand;

    /// <summary>
    /// Places the next frame and grows the strip by the rows it adds. Takes <paramref name="frame"/> over: it is kept as
    /// the previous frame and disposed later.
    /// </summary>
    public ManualScrollStep Add(SKBitmap frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frames++;
        ManualScrollStep step;
        if (frame.Width != _width || frame.Height != _frameHeight)
        {
            frame.Dispose();
            _missedLost++;
            step = Missed(guided: false);
        }
        else
        {
            step = _strip == null ? Calibrate(frame) : Place(frame);
        }

        _missRun = step.Placed ? 0 : _missRun + 1;
        _longestMissRun = Math.Max(_longestMissRun, _missRun);
        _recent[_recentCount++ % RecentFrames] = (step.Shift, step.Placed);
        return step;
    }

    /// <summary>
    /// The shifts of the last <see cref="RecentFrames"/> frames, oldest first, for the log line of a stall warning: rows
    /// (<c>x</c> when it could not be measured), with <c>!</c> on a frame not placed. It shows what the closing line's totals
    /// cannot: whether a single wild jump lost the capture, and how the scrolling went around it.
    /// </summary>
    internal string DescribeRecent()
    {
        int count = Math.Min(_recentCount, RecentFrames);
        var text = new System.Text.StringBuilder(count * 5);
        for (int i = _recentCount - count; i < _recentCount; i++)
        {
            (int? shift, bool placed) = _recent[i % RecentFrames];
            if (text.Length > 0)
            {
                text.Append(',');
            }

            text.Append(shift is int rows ? rows.ToString(System.Globalization.CultureInfo.InvariantCulture) : "x");
            if (!placed)
            {
                text.Append('!');
            }
        }

        return text.ToString();
    }

    /// <summary>How the capture's stitching has gone so far (see <see cref="ManualScrollStats"/>).</summary>
    internal ManualScrollStats Stats => new(
        _frames, _placed, _waited, _unguided, _tentatives, _missedNear, _missedBeyond, _missedLost,
        _shiftsLost, _largestShift, _longestMissRun, _strip == null ? 0 : _frameHeight - _topBand - _bottomBand, _rescued);

    /// <summary>
    /// How the capture went, for its closing log line: frames seen and placed, placements found by searching the whole
    /// strip (and how many of those waited for the next frame), the frames not placed by why (missed where the motion
    /// put them, past the strip's edge, or with the motion lost), shifts that could not be measured, the largest that
    /// was, the longest run of frames not placed, the rows matched per frame, and the bands kept out of the strip.
    /// </summary>
    internal string Describe()
    {
        ManualScrollStats s = Stats;
        return $"frames={s.Frames} placed={s.Placed} waited={s.Waited} unguided={s.Unguided} tentative={s.Tentative} " +
            $"rescued={s.Rescued} missed=near:{s.MissedNear},beyond:{s.MissedBeyond},lost:{s.MissedLost} shiftLost={s.ShiftsLost} " +
            $"maxShift={s.LargestShift} longestMiss={s.LongestMissRun} window={s.WindowRows} " +
            $"bands={_topBand}/{_bottomBand} bandChanges={_bandChanges} started={Started}";
    }

    /// <summary>Whether <paramref name="frame"/> is one the stitcher keeps (the caller must not dispose it).</summary>
    internal bool Holds(SKBitmap frame) => ReferenceEquals(frame, _previous) || ReferenceEquals(frame, _seed);

    /// <summary>The result: the top band, the strip, and the bottom band.</summary>
    public SKBitmap ToBitmap()
    {
        if (_strip == null)
        {
            return (_seed ?? _previous).Copy();
        }

        var output = new SKBitmap(new SKImageInfo(Math.Max(1, _width), Math.Max(1, Height), SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        int top = _header?.Height ?? 0;
        if (_header != null)
        {
            canvas.DrawBitmap(_header, 0, 0);
        }

        _strip.DrawInto(canvas, top);
        if (_footer != null)
        {
            canvas.DrawBitmap(_footer, 0, top + _strip.Height);
        }

        return output;
    }

    /// <summary>
    /// The result scaled to fit <paramref name="maxWidth"/> × <paramref name="maxHeight"/> (never enlarged), for the live
    /// preview. The caller disposes it.
    /// </summary>
    public SKBitmap RenderScaledPreview(int maxWidth, int maxHeight)
    {
        if (_strip == null)
        {
            using var only = new ManualScrollStrip(_seed ?? _previous, _ignoreRight);
            return only.RenderScaledPreview(maxWidth, maxHeight);
        }

        if (_header == null && _footer == null)
        {
            return _strip.RenderScaledPreview(maxWidth, maxHeight);
        }

        double scale = Math.Min(
            Math.Min((double)Math.Max(1, maxWidth) / Math.Max(1, _width), (double)Math.Max(1, maxHeight) / Math.Max(1, Height)),
            1.0);
        int width = Math.Max(1, (int)Math.Round(_width * scale));
        int top = (int)Math.Round((_header?.Height ?? 0) * scale);
        int bottom = (int)Math.Round((_footer?.Height ?? 0) * scale);
        using SKBitmap middle = _strip.RenderScaledPreview(width, Math.Max(1, (int)Math.Round(_strip.Height * scale)));

        var output = new SKBitmap(new SKImageInfo(width, top + middle.Height + bottom, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(output);
        if (_header != null && top > 0)
        {
            canvas.DrawBitmap(_header, new SKRect(0, 0, width, top));
        }

        canvas.DrawBitmap(middle, 0, top);
        if (_footer != null && bottom > 0)
        {
            canvas.DrawBitmap(_footer, new SKRect(0, top + middle.Height, width, top + middle.Height + bottom));
        }

        return output;
    }

    public void Dispose()
    {
        _seed?.Dispose();
        _previous.Dispose();
        _strip?.Dispose();
        _header?.Dispose();
        _footer?.Dispose();
    }

    /// <summary>
    /// Rows next to each fixed bar (or frame edge) that are kept out of the strip as well: apps float things there over
    /// the content — LINE's date while it scrolls, Discord's "new messages" and "jump to present" bars, a scroll-down
    /// button — and content passing under them was kept with them, the same pill in the strip again every few rows.
    /// The content is kept once it has scrolled past them, and the result shows these rows of the frames at the
    /// strip's two ends, as the screen did. <paramref name="wanted"/> rows (the capture asks for 48 logical pixels),
    /// fewer when the fixed bars already take most of the frame: the strip is matched on what is left, at least 40%.
    /// </summary>
    internal static int EdgeGuardRows(int frameHeight, int fixedRows, int wanted)
    {
        int room = ((frameHeight * 6 / 10) - fixedRows) / 2;
        return Math.Max(0, Math.Min(wanted, room));
    }

    /// <summary>
    /// How far from where the motion says a frame is looked for: a few rows for a frame right after a placed one, more
    /// the longer frames have gone unplaced (each measured shift may be a row off), at most a quarter of the window.
    /// </summary>
    internal static int NearWindow(int windowHeight, int framesSinceAccepted) =>
        Math.Clamp(8 + (2 * framesSinceAccepted), 8, Math.Max(8, windowHeight / 4));

    // Before the strip starts: wait for a frame that moved from the first one, find the fixed bars from the two, then
    // start the strip from the first frame's rows between them and place this frame on it.
    private ManualScrollStep Calibrate(SKBitmap frame)
    {
        SKBitmap seed = _seed!;
        ScrollStitcher.VerticalShift shift = MeasureCalibrationShift(seed, frame);
        if (!shift.Found || Math.Abs(shift.Rows) < CalibrationShiftRows)
        {
            KeepAsPrevious(frame);
            if (!shift.Found)
            {
                _shiftsLost++;
                _missedLost++;
                return Missed(guided: false);
            }

            _waited++;
            return new ManualScrollStep(true, 0, 0, 0, false, 0, double.MaxValue, double.MaxValue, Shift: shift.Rows);
        }

        (_fixedTop, _fixedBottom) = ScrollStitcher.FindFixedBands(seed, frame, shift.Rows, _ignoreRight, _frameHeight * 3 / 10);
        _guard = EdgeGuardRows(_frameHeight, _fixedTop + _fixedBottom, _edgeGuard);
        _topBand = _fixedTop + _guard;
        _bottomBand = _fixedBottom + _guard;
        _header = CopyRows(seed, 0, _topBand);
        _footer = CopyRows(seed, _frameHeight - _bottomBand, _bottomBand);
        using (SKBitmap seedWindow = Window(seed))
        {
            _strip = new ManualScrollStrip(seedWindow, _ignoreRight);
        }

        // The first frame is where the strip starts, so the shift to this frame is measured from it.
        _previous.Dispose();
        _previous = seed;
        _seed = null;
        _lastOffset = 0;
        _motion = 0;
        _motionValid = true;
        _lastShift = shift.Rows;
        _framesSinceAccepted = 0;
        return Place(frame);
    }

    // The shift between the first frame and this one, over the frames' middle half: a header or a message box at the
    // edges stays put, so it pulls toward no motion at all; the middle is where the content shows. A small frame, or a
    // shift too big for the middle, is measured over the whole frame.
    private ScrollStitcher.VerticalShift MeasureCalibrationShift(SKBitmap seed, SKBitmap frame)
    {
        int quarter = _frameHeight / 4;
        if (_frameHeight >= 64)
        {
            using SKBitmap seedMiddle = Rows(seed, quarter, _frameHeight - (2 * quarter));
            using SKBitmap frameMiddle = Rows(frame, quarter, _frameHeight - (2 * quarter));
            ScrollStitcher.VerticalShift middle = ScrollStitcher.FindVerticalShift(
                seedMiddle, frameMiddle, Math.Min(_minOverlap, seedMiddle.Height), _ignoreRight, _rowTolerance);
            if (middle.Found)
            {
                return middle;
            }
        }

        return ScrollStitcher.FindVerticalShift(seed, frame, _minOverlap, _ignoreRight, _rowTolerance);
    }

    private ManualScrollStep Place(SKBitmap frame)
    {
        ManualScrollStrip strip = _strip!;
        ScrollStitcher.VerticalShift shift;
        // Measured between the fixed bars, the edge guards included: content shows there too, and the taller the
        // rows compared, the faster a scroll that can still be measured.
        using (SKBitmap previousInner = Inner(_previous))
        using (SKBitmap frameInner = Inner(frame))
        {
            shift = ScrollStitcher.FindVerticalShift(
                previousInner, frameInner, Math.Clamp(_minOverlap, 1, frameInner.Height), _ignoreRight, _rowTolerance,
                expectedShift: _lastShift);
        }

        ScrollStitcher.FrameAlignment? rescued = LookWhereTheMotionWas(frame, shift, out double rescueScore);
        int trimmedTop = 0;
        int trimmedBottom = 0;
        if (rescued is { } placedBack)
        {
            // The strip says where the frame is: that is the shift, whatever was measured.
            int actual = (int)(placedBack.Offset - _lastOffset - _motion);
            _motion += actual;
            _lastShift = actual;
            _largestShift = Math.Max(_largestShift, Math.Abs(actual));
            _rescued++;
            _shiftsLost += shift.Found ? 0 : 1;
        }
        else if (shift.Found)
        {
            _motion += shift.Rows;
            _lastShift = shift.Rows;
            _largestShift = Math.Max(_largestShift, Math.Abs(shift.Rows));
            if (Math.Abs(shift.Rows) >= CalibrationShiftRows)
            {
                (trimmedTop, trimmedBottom) = GrowBands(frame, shift.Rows);
            }
        }
        else
        {
            _motionValid = false;
            _shiftsLost++;
        }

        int windowHeight = _frameHeight - _topBand - _bottomBand;
        int minOverlap = Math.Clamp(_minOverlap, 1, windowHeight);
        // Where the motion vouches for the place, a few rows of overlap confirm it: a scroll can then go almost a whole
        // window a frame and still be followed.
        int guidedMinOverlap = Math.Clamp(Math.Max(8, _minOverlap / 3), 1, windowHeight);
        using SKBitmap window = Window(frame);

        _framesSinceAccepted++;
        bool guided = _motionValid;
        int predicted = 0;
        bool inReach = true;
        double bestScore;
        double ambiguityGap = double.MaxValue;
        ScrollStitcher.FrameAlignment align;
        if (rescued is { } found)
        {
            predicted = found.Offset;
            align = found;
            bestScore = rescueScore;
        }
        else if (guided)
        {
            predicted = (int)Math.Clamp(_lastOffset + _motion, int.MinValue / 2, int.MaxValue / 2);
            align = strip.AlignNear(
                window, predicted, NearWindow(windowHeight, _framesSinceAccepted), guidedMinOverlap, _rowTolerance,
                GuidedMaxScore, out bestScore, out inReach);
        }
        else
        {
            // Nothing says where the frame is: it must overlap the strip well and match it clearly.
            align = strip.Align(
                window, Math.Max(minOverlap, windowHeight / 4), _rowTolerance, UnguidedMaxScore, out bestScore, out ambiguityGap);
        }

        int prepended = 0;
        int appended = 0;
        if (align.Found)
        {
            _unguided += guided ? 0 : 1;
        }
        else if (!guided)
        {
            _missedLost++;
        }
        else if (inReach)
        {
            _missedNear++;
        }
        else
        {
            _missedBeyond++;
        }

        bool tentative = align.Found && !guided
            && !(shift.Found && PreviousFrameAgrees(align.Offset - shift.Rows, windowHeight, guidedMinOverlap));
        if (tentative)
        {
            // Found with nothing to vouch for it — the previous frame, the measured shift back, is not where it would
            // have to be: follow the motion from here, and add what this frame shows only once the next frame is placed
            // where that motion says too.
            _tentative = true;
            _tentatives++;
            _lastOffset = align.Offset;
            _motion = 0;
            _motionValid = true;
            _framesSinceAccepted = 0;
        }
        else if (align.Found)
        {
            _placed++;
            int stripHeight = strip.Height;
            if (align.Offset < 0)
            {
                prepended = -align.Offset;
                strip.Prepend(window, prepended);
                ReplaceBand(ref _header, frame, 0, _topBand);
            }
            else if (align.Offset + windowHeight > stripHeight)
            {
                appended = align.Offset + windowHeight - stripHeight;
                strip.Append(window, appended);
                ReplaceBand(ref _footer, frame, _frameHeight - _bottomBand, _bottomBand);
            }

            // A prepend rebases strip coordinates: the frame just placed is then at offset 0.
            _lastOffset = Math.Max(0, align.Offset);
            _motion = 0;
            _motionValid = true;
            _tentative = false;
            _framesSinceAccepted = 0;
            _missesFollowingMotion = 0;
        }
        else if (_tentative && inReach)
        {
            // The frame found without the motion was not where this one says: it matched by chance. (Past the strip's
            // edge nothing can say either way, so the motion keeps being followed from it.)
            _tentative = false;
            _motionValid = false;
        }
        else if (guided && inReach && ++_missesFollowingMotion >= MaxMissesFollowingMotion)
        {
            // The strip was there to meet the frame where the motion said, frame after frame, and it did not: the motion
            // followed has gone wrong (a mis-measured shift), so look for the frame anywhere again.
            _motionValid = false;
        }

        _previousPrepended = prepended;
        _previousAppended = appended;
        KeepAsPrevious(frame);
        return new ManualScrollStep(
            align.Found && !tentative, align.Offset, prepended, appended, guided, predicted, bestScore, ambiguityGap,
            _topBand, _bottomBand, trimmedTop, trimmedBottom, tentative, shift.Found ? shift.Rows : null);
    }

    // A shift that could not be measured, or one too far for the frame to meet the strip, is checked against the strip
    // before it is believed. A stretch with little in it can measure a long way off (456 rows for a scroll of 13 in the
    // simulation, 365 in a user's capture): followed, the motion puts every later frame past the strip's edge, and the
    // capture waits for the view to come back from where it never went. And one frame that cannot be measured cost the
    // motion, leaving only a search of the whole strip, which a stretch like that does not answer clearly. The frame is
    // looked for around where the motion had it, with the stricter score of a search nothing vouches for; a frame that
    // really jumped is not there, and goes on as before.
    private ScrollStitcher.FrameAlignment? LookWhereTheMotionWas(SKBitmap frame, ScrollStitcher.VerticalShift shift, out double score)
    {
        score = double.MaxValue;
        int windowHeight = _frameHeight - _topBand - _bottomBand;
        int guidedMinOverlap = Math.Clamp(Math.Max(8, _minOverlap / 3), 1, windowHeight);
        bool tooFar = shift.Found && Math.Abs(shift.Rows) > windowHeight - guidedMinOverlap;
        if ((shift.Found && !tooFar) || !_motionValid || _tentative)
        {
            return null;
        }

        int predicted = (int)Math.Clamp(_lastOffset + _motion + _lastShift, int.MinValue / 2, int.MaxValue / 2);
        int reach = Math.Min(NearWindow(windowHeight, _framesSinceAccepted + 1) + Math.Max(48, Math.Abs(_lastShift)), windowHeight / 2);
        using SKBitmap window = Window(frame);
        ScrollStitcher.FrameAlignment align = _strip!.AlignNear(
            window, predicted, reach, guidedMinOverlap, _rowTolerance, UnguidedMaxScore, out score, out _, rejectAmbiguous: true);
        return align.Found ? align : null;
    }

    // Whether the previous frame sits on the strip at `offset` (within a few rows): what makes a frame found by searching
    // the whole strip more than a chance match, the two frames agreeing with the shift measured between them.
    private bool PreviousFrameAgrees(int offset, int windowHeight, int minOverlap)
    {
        using SKBitmap previousWindow = Window(_previous);
        return _strip!.AlignNear(
            previousWindow, offset, NearWindow(windowHeight, 1), minOverlap, _rowTolerance, UnguidedMaxScore,
            out _, out _).Found;
    }

    // A bar that has appeared since the strip started (Discord's "jump to present", a page's sticky header coming back)
    // stays put between the previous frame and this one: keep it out of the strip from now on. The previous frame may
    // already have added rows it covers; those come off the strip again and are shown as part of that frame's band.
    private (int TrimmedTop, int TrimmedBottom) GrowBands(SKBitmap frame, int shiftRows)
    {
        (int fixedTop, int fixedBottom) = ScrollStitcher.FindFixedBands(_previous, frame, shiftRows, _ignoreRight, _frameHeight * 3 / 10);
        int top = Math.Max(_topBand, fixedTop + _guard);
        int bottom = Math.Max(_bottomBand, fixedBottom + _guard);
        if ((top == _topBand && bottom == _bottomBand) || top + bottom > _frameHeight * 6 / 10)
        {
            return (0, 0);
        }

        _fixedTop = Math.Max(_fixedTop, fixedTop);
        _fixedBottom = Math.Max(_fixedBottom, fixedBottom);

        ManualScrollStrip strip = _strip!;
        _bandChanges++;
        int trimmedTop = 0;
        int trimmedBottom = 0;
        if (top > _topBand)
        {
            trimmedTop = Math.Min(_previousPrepended, Math.Min(top - _topBand, strip.Height - 1));
            if (trimmedTop > 0)
            {
                strip.TrimTop(trimmedTop);
                ReplaceBand(ref _header, _previous, 0, _topBand + trimmedTop);
            }

            // The previous frame's window now starts lower in it, and the strip lost its first rows.
            _lastOffset += (top - _topBand) - trimmedTop;
            _topBand = top;
        }

        if (bottom > _bottomBand)
        {
            trimmedBottom = Math.Min(_previousAppended, Math.Min(bottom - _bottomBand, strip.Height - 1));
            if (trimmedBottom > 0)
            {
                strip.TrimBottom(trimmedBottom);
                ReplaceBand(ref _footer, _previous, _frameHeight - _bottomBand - trimmedBottom, _bottomBand + trimmedBottom);
            }

            _bottomBand = bottom;
        }

        return (trimmedTop, trimmedBottom);
    }

    private ManualScrollStep Missed(bool guided) =>
        new(false, 0, 0, 0, guided, 0, double.MaxValue, double.MaxValue, _topBand, _bottomBand);

    private void KeepAsPrevious(SKBitmap frame)
    {
        _previous.Dispose();
        _previous = frame;
    }

    // The frame's rows between the bands, sharing its pixels.
    private SKBitmap Window(SKBitmap frame) => Rows(frame, _topBand, _frameHeight - _topBand - _bottomBand);

    // The frame's rows between the fixed bars (the window and its edge guards), sharing its pixels.
    private SKBitmap Inner(SKBitmap frame) => Rows(frame, _fixedTop, _frameHeight - _fixedTop - _fixedBottom);

    private static SKBitmap Rows(SKBitmap bitmap, int first, int count)
    {
        var rows = new SKBitmap();
        if (count <= 0 || !bitmap.ExtractSubset(rows, new SKRectI(0, first, bitmap.Width, first + count)))
        {
            rows.Dispose();
            return CopyRows(bitmap, first, Math.Max(1, count))!;
        }

        return rows;
    }

    private static SKBitmap? CopyRows(SKBitmap bitmap, int first, int count)
    {
        if (count <= 0)
        {
            return null;
        }

        var copy = new SKBitmap(new SKImageInfo(bitmap.Width, count, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(copy);
        canvas.DrawBitmap(bitmap, new SKRect(0, first, bitmap.Width, first + count), new SKRect(0, 0, bitmap.Width, count));
        return copy;
    }

    private static void ReplaceBand(ref SKBitmap? band, SKBitmap frame, int first, int count)
    {
        if (count <= 0)
        {
            return;
        }

        band?.Dispose();
        band = CopyRows(frame, first, count);
    }
}
