using System.Reflection;

namespace GimmeCapture.Tests;

public sealed class PlatformErrorDialogTests
{
    [Fact]
    public void ShowError_IsNeverInlined()
    {
        // Inlined into App.OnFrameworkInitializationCompleted, its WinForms MessageBox made the ReadyToRun code of every
        // start load System.Windows.Forms.dll (13 MB), which sign-in reads ahead from the disk, though no start shows it.
        // Found with a probe that prepared the published build's start-up methods in a fresh process.
        MethodImplAttributes flags = typeof(PlatformErrorDialog)
            .GetMethod(nameof(PlatformErrorDialog.ShowError))!
            .MethodImplementationFlags;

        Assert.True(flags.HasFlag(MethodImplAttributes.NoInlining));
    }
}
