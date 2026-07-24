using GimmeCapture.Services.Core.Media;

namespace GimmeCapture.Tests;

// The gate exists to reject "excellent-looking" strip matches at physically impossible positions —
// repeated page content (comment boxes, banners) can align a frame far from where the view actually is,
// which prepended duplicated blocks into real captures. Its contract: veto only when motion tracking is
// intact AND the placement contradicts the accumulated frame-to-frame shift beyond the tolerance.
public class ManualScrollMotionGateTests
{
    private const int FrameHeight = 493; // the real capture this was diagnosed from

    [Fact]
    public void Veto_WhenPlacementContradictsObservedMotion()
    {
        // The observed bug: last accepted at 159, user scrolled DOWN (+500 measured), but the matcher
        // proposed -347 (a huge upward prepend). implied = -506, expected = +500 → veto.
        Assert.True(ManualScrollMotionGate.ShouldVeto(
            motionValid: true,
            accumulatedShiftRows: 500,
            lastAcceptedOffset: 159,
            candidateOffset: -347,
            frameHeight: FrameHeight));
    }

    [Fact]
    public void Accept_WhenPlacementAgreesWithObservedMotion()
    {
        // Ordinary scroll: accumulated +120 since the last accept at 159 → candidate ≈ 279.
        Assert.False(ManualScrollMotionGate.ShouldVeto(
            motionValid: true,
            accumulatedShiftRows: 120,
            lastAcceptedOffset: 159,
            candidateOffset: 279,
            frameHeight: FrameHeight));
    }

    [Fact]
    public void Accept_WithinTolerance_DespiteSmallDisagreement()
    {
        // Per-frame measurement error over a miss-run: disagreement below frameHeight/3 passes.
        int tolerance = ManualScrollMotionGate.Tolerance(FrameHeight);
        Assert.False(ManualScrollMotionGate.ShouldVeto(
            motionValid: true,
            accumulatedShiftRows: 100,
            lastAcceptedOffset: 0,
            candidateOffset: 100 + tolerance, // exactly at the edge — not beyond it
            frameHeight: FrameHeight));
    }

    [Fact]
    public void NeverVetoes_WhenMotionTrackingIsBroken()
    {
        // An unmeasurable frame shift invalidates the prediction; the matcher then decides alone,
        // exactly as before the gate existed. Even a wild candidate passes.
        Assert.False(ManualScrollMotionGate.ShouldVeto(
            motionValid: false,
            accumulatedShiftRows: 500,
            lastAcceptedOffset: 159,
            candidateOffset: -347,
            frameHeight: FrameHeight));
    }

    [Fact]
    public void Tolerance_ScalesWithFrameButHasAFloor()
    {
        Assert.Equal(164, ManualScrollMotionGate.Tolerance(493)); // frame/3 wins
        Assert.Equal(120, ManualScrollMotionGate.Tolerance(224)); // floor wins for small captures
    }
}
