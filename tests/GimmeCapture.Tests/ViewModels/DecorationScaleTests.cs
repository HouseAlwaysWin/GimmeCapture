using GimmeCapture.ViewModels.Floating;
using GimmeCapture.ViewModels.Main;
using Avalonia;
using Xunit;
using Moq;
using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core;
using GimmeCapture.Models;
using System.Collections.Generic;

namespace GimmeCapture.Tests.ViewModels;

public class DecorationScaleTests
{
    // A concrete implementation of FloatingWindowViewModelBase for testing
    private class TestFloatingViewModel : FloatingWindowViewModelBase
    {
        public TestFloatingViewModel() : base() { }

        public override Avalonia.Thickness WindowPadding => new(20);
    }

    [Fact]
    public void FloatingWindowViewModelBase_ShouldCalculateCorrectWingDimensions()
    {
        // Arrange
        var vm = new TestFloatingViewModel();
        
        // Act
        vm.WingScale = 2.0;

        // Assert
        Assert.Equal(200.0, vm.WingWidth);  // 100 * 2.0
        Assert.Equal(120.0, vm.WingHeight); // 60 * 2.0
        Assert.Equal(new Thickness(-200.0, 0, 0, 0), vm.LeftWingMargin);
        Assert.Equal(new Thickness(0, 0, -200.0, 0), vm.RightWingMargin);
    }

    [Fact]
    public void FloatingWindowViewModelBase_ShouldUseFixedIconSize()
    {
        // Arrange
        var vm = new TestFloatingViewModel();
        
        // Assert
        Assert.Equal(8.0, vm.SelectionIconSize);
    }

    [Fact]
    public void FloatingWindowViewModelBase_ShouldTriggerPropertyNotifications()
    {
        // Arrange
        var vm = new TestFloatingViewModel();
        var changedProperties = new List<string>();
        vm.PropertyChanged += (s, e) => { if (e.PropertyName != null) changedProperties.Add(e.PropertyName); };

        // Act
        vm.WingScale = 1.5;

        // Assert
        Assert.Contains(nameof(vm.WingScale), changedProperties);
        Assert.Contains(nameof(vm.WingWidth), changedProperties);
        Assert.Contains(nameof(vm.WingHeight), changedProperties);
        Assert.Contains(nameof(vm.WindowPadding), changedProperties);

    }

    [Fact]
    public void FloatingWindowViewModelBase_ShouldCalculateCorrectWingDimensions_HighScale()
    {
        // Arrange
        var vm = new TestFloatingViewModel();
        
        // Act
        vm.WingScale = 3.0;

        // Assert
        Assert.Equal(300.0, vm.WingWidth);
        Assert.Equal(180.0, vm.WingHeight);
    }

    [Fact]
    public void FloatingWindowViewModelBase_ShouldCalculateCorrectWingDimensions_LowScale()
    {
        // Arrange
        var vm = new TestFloatingViewModel();
        
        // Act
        vm.WingScale = 0.5;

        // Assert
        Assert.Equal(50.0, vm.WingWidth);
        Assert.Equal(30.0, vm.WingHeight);
    }
}
