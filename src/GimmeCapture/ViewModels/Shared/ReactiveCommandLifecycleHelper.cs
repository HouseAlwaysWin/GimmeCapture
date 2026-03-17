using ReactiveUI;
using System;
using System.Diagnostics;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels.Shared;

internal static class ReactiveCommandLifecycleHelper
{
    public static ReactiveCommand<Unit, Unit> CreateCommand(
        Action execute,
        IObservable<bool>? canExecute,
        string commandName,
        CompositeDisposable lifecycleDisposables,
        string scope)
    {
        return CreateCommand<Unit>(
            _ => execute(),
            canExecute,
            commandName,
            lifecycleDisposables,
            scope);
    }

    public static ReactiveCommand<Unit, Unit> CreateAsyncCommand(
        Func<Task> execute,
        IObservable<bool>? canExecute,
        string commandName,
        CompositeDisposable lifecycleDisposables,
        string scope)
    {
        return CreateAsyncCommand<Unit>(
            _ => execute(),
            canExecute,
            commandName,
            lifecycleDisposables,
            scope);
    }

    public static ReactiveCommand<TParam, Unit> CreateCommand<TParam>(
        Action<TParam> execute,
        IObservable<bool>? canExecute,
        string commandName,
        CompositeDisposable lifecycleDisposables,
        string scope)
    {
        var command = canExecute == null
            ? ReactiveCommand.Create<TParam>(execute)
            : ReactiveCommand.Create<TParam>(execute, canExecute);

        AttachCommandErrorLogging(command, commandName, lifecycleDisposables, scope);
        lifecycleDisposables.Add(command);
        return command;
    }

    public static ReactiveCommand<TParam, Unit> CreateAsyncCommand<TParam>(
        Func<TParam, Task> execute,
        IObservable<bool>? canExecute,
        string commandName,
        CompositeDisposable lifecycleDisposables,
        string scope)
    {
        var command = canExecute == null
            ? ReactiveCommand.CreateFromTask<TParam>(execute)
            : ReactiveCommand.CreateFromTask<TParam>(execute, canExecute);

        AttachCommandErrorLogging(command, commandName, lifecycleDisposables, scope);
        lifecycleDisposables.Add(command);
        return command;
    }

    private static void AttachCommandErrorLogging<TParam, TResult>(
        ReactiveCommand<TParam, TResult> command,
        string commandName,
        CompositeDisposable lifecycleDisposables,
        string scope)
    {
        lifecycleDisposables.Add(command.ThrownExceptions.Subscribe(ex =>
            Debug.WriteLine($"[{scope}:{commandName}] {ex}")));
    }
}
