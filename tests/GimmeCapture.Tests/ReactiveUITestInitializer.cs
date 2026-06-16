using System.Reactive.Concurrency;
using System.Runtime.CompilerServices;
using ReactiveUI;
using ReactiveUI.Builder;

namespace GimmeCapture.Tests;

internal static class ReactiveUITestInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        RxAppBuilder.CreateReactiveUIBuilder()
            .WithCoreServices()
            .BuildApp();
        RxSchedulers.MainThreadScheduler = ImmediateScheduler.Instance;
    }
}
