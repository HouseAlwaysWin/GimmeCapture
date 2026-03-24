using GimmeCapture.ViewModels;
using GimmeCapture.ViewModels.Main;
using GimmeCapture.Models;
using Avalonia; // For Rect
using System;

namespace GimmeCapture.Tests.ViewModels;

public class SnipWindowViewModelTests
{
    [Fact]
    public void HandleRightClick_WhenSelecting_ResetsToDetectingAndEmptyRect()
    {
        // Arrange
        var vm = new SnipWindowViewModel(); // RecService is null -> RecState is Idle
        vm.CurrentState = SnipState.Selecting;
        vm.SelectionRect = new Rect(10, 10, 100, 100);
        bool closed = false;
        vm.CloseAction = () => closed = true;

        // Act
        vm.HandleRightClick();

        // Assert
        Assert.Equal(SnipState.Detecting, vm.CurrentState);
        Assert.Equal(new Rect(0, 0, 0, 0), vm.SelectionRect);
        Assert.False(closed, "Window should not close when resetting selection.");
    }

    [Fact]
    public void HandleRightClick_WhenSelected_ResetsToDetectingAndEmptyRect()
    {
        // Arrange
        var vm = new SnipWindowViewModel();
        vm.CurrentState = SnipState.Selected;
        vm.SelectionRect = new Rect(10, 10, 100, 100);
        bool closed = false;
        vm.CloseAction = () => closed = true;

        // Act
        vm.HandleRightClick();

        // Assert
        Assert.Equal(SnipState.Detecting, vm.CurrentState);
        Assert.Equal(new Rect(0, 0, 0, 0), vm.SelectionRect);
        Assert.False(closed, "Window should not close when resetting selection.");
    }

    [Fact]
    public void HandleRightClick_WhenDetecting_ClosesWindow()
    {
        // Arrange
        var vm = new SnipWindowViewModel();
        vm.CurrentState = SnipState.Detecting;
        bool closed = false;
        vm.CloseAction = () => closed = true;

        // Act
        vm.HandleRightClick();

        // Assert
        Assert.True(closed, "Window should close when right-clicking in Detecting state.");
        // State remains Detecting or doesn't matter as window closes
    }
}
