using System;

namespace GimmeCapture.ViewModels.Main;

internal sealed class SnipSelectionStateController
{
    private readonly Func<bool> _shouldTriggerAutoScan;
    private readonly Action _triggerAutoScan;
    private readonly Action _cancelScan;
    private readonly Action<bool> _dismissHoverPreview;
    private readonly Action _triggerAutoAction;
    private readonly Action _clearTranslatedBlocks;
    private readonly Action _resetParkedToolbar;
    private readonly Action _updateMask;
    private readonly Action _hideScanLoadingBar;

    public SnipSelectionStateController(
        Func<bool> shouldTriggerAutoScan,
        Action triggerAutoScan,
        Action cancelScan,
        Action<bool> dismissHoverPreview,
        Action triggerAutoAction,
        Action clearTranslatedBlocks,
        Action resetParkedToolbar,
        Action updateMask,
        Action hideScanLoadingBar)
    {
        _shouldTriggerAutoScan = shouldTriggerAutoScan;
        _triggerAutoScan = triggerAutoScan;
        _cancelScan = cancelScan;
        _dismissHoverPreview = dismissHoverPreview;
        _triggerAutoAction = triggerAutoAction;
        _clearTranslatedBlocks = clearTranslatedBlocks;
        _resetParkedToolbar = resetParkedToolbar;
        _updateMask = updateMask;
        _hideScanLoadingBar = hideScanLoadingBar;
    }

    public void HandleTransition(SnipState previousState, SnipState nextState)
    {
        if (nextState != SnipState.Detecting)
        {
            _cancelScan();
            _hideScanLoadingBar();
            _dismissHoverPreview(previousState == SnipState.Detecting && nextState == SnipState.Selecting);
        }
        else if (_shouldTriggerAutoScan())
        {
            _triggerAutoScan();
        }

        if (nextState == SnipState.Selected)
        {
            _triggerAutoAction();
            _clearTranslatedBlocks();
        }

        if (nextState != SnipState.Selected)
        {
            _resetParkedToolbar();
        }

        _updateMask();
    }
}
