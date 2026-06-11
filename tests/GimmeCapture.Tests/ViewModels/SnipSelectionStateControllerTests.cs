using GimmeCapture.ViewModels.Main;

namespace GimmeCapture.Tests.ViewModels;

public sealed class SnipSelectionStateControllerTests
{
    [Theory]
    [InlineData(SnipMode.Screenshot)]
    [InlineData(SnipMode.Recording)]
    public void HandleModeTransition_ShouldScanWhenLeavingTranslationForDetectionMode(SnipMode nextMode)
    {
        int scanCount = 0;
        var controller = CreateController(
            shouldTriggerAutoScan: () => true,
            triggerAutoScan: () => scanCount++);

        controller.HandleModeTransition(
            SnipMode.Translation,
            nextMode,
            SnipState.Detecting);

        Assert.Equal(1, scanCount);
    }

    [Theory]
    [InlineData(SnipMode.Screenshot, SnipMode.Recording, SnipState.Detecting, true)]
    [InlineData(SnipMode.Translation, SnipMode.Screenshot, SnipState.Selected, true)]
    [InlineData(SnipMode.Translation, SnipMode.Screenshot, SnipState.Detecting, false)]
    public void HandleModeTransition_ShouldNotScanWhenTransitionIsNotEligible(
        SnipMode previousMode,
        SnipMode nextMode,
        SnipState currentState,
        bool scanEnabled)
    {
        int scanCount = 0;
        var controller = CreateController(
            shouldTriggerAutoScan: () => scanEnabled,
            triggerAutoScan: () => scanCount++);

        controller.HandleModeTransition(previousMode, nextMode, currentState);

        Assert.Equal(0, scanCount);
    }

    private static SnipSelectionStateController CreateController(
        Func<bool> shouldTriggerAutoScan,
        Action triggerAutoScan)
    {
        return new SnipSelectionStateController(
            shouldTriggerAutoScan,
            triggerAutoScan,
            cancelScan: () => { },
            dismissHoverPreview: _ => { },
            triggerAutoAction: () => { },
            clearTranslatedBlocks: () => { },
            resetParkedToolbar: () => { },
            refreshInteractionRegion: () => { },
            hideScanLoadingBar: () => { });
    }
}
