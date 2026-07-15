using System;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Tests;

public class StartupServiceArgsTests
{
    [Fact]
    public void ShouldLaunchToTrayOnly_True_WhenStartupArgPresent()
    {
        Assert.True(StartupService.ShouldLaunchToTrayOnly(new[] { StartupService.RunArgumentForTrayStartup }));
        Assert.True(StartupService.ShouldLaunchToTrayOnly(new[] { "other", "--STARTUP" })); // case-insensitive, any position
    }

    [Fact]
    public void ShouldLaunchToTrayOnly_False_WhenAbsentOrEmpty()
    {
        Assert.False(StartupService.ShouldLaunchToTrayOnly(null));
        Assert.False(StartupService.ShouldLaunchToTrayOnly(Array.Empty<string>()));
        Assert.False(StartupService.ShouldLaunchToTrayOnly(new[] { "--other", "-s", "startup" }));
    }
}
