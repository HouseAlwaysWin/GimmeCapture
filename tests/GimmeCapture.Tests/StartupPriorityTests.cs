using System.Diagnostics;
using GimmeCapture.Launcher;

namespace GimmeCapture.Tests;

// At sign-in the runtime's start waited 1.4-3.2 s for a processor behind the sign-in's other programs: the launcher
// starts the app above normal priority, and the app goes back to normal once its start is done.
public sealed class StartupPriorityTests
{
    [WindowsFact]
    public void Lower_PutsTheLaunchersPriorityBack_AndLeavesAnyOtherAlone()
    {
        using var self = Process.GetCurrentProcess();
        var before = self.PriorityClass;
        try
        {
            self.PriorityClass = ReadAhead.StartPriority;
            StartupPriority.Lower(self);
            self.Refresh();
            Assert.Equal(ProcessPriorityClass.Normal, self.PriorityClass);

            // One the user set in the meantime keeps theirs.
            self.PriorityClass = ProcessPriorityClass.BelowNormal;
            StartupPriority.Lower(self);
            self.Refresh();
            Assert.Equal(ProcessPriorityClass.BelowNormal, self.PriorityClass);
        }
        finally
        {
            self.PriorityClass = before;
        }
    }

    [Fact]
    public void TheLauncher_RaisesThePriority_ButNotToHigh()
    {
        // High would take processors from the sign-in's own work, and from Explorer.
        Assert.Equal(ProcessPriorityClass.AboveNormal, ReadAhead.StartPriority);
    }
}
