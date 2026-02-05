using System;
using System.Reactive;
using ReactiveUI;
using GimmeCapture.Services.Core;

namespace GimmeCapture.ViewModels.Shared;

public class GothicDialogViewModel : ReactiveObject
{
    private string _title = string.Empty;
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }

    private string _message = string.Empty;
    public string Message
    {
        get => _message;
        set => this.RaiseAndSetIfChanged(ref _message, value);
    }

    public ReactiveCommand<bool, bool> CloseCommand { get; }

    public GothicDialogViewModel()
    {
        CloseCommand = ReactiveCommand.Create<bool, bool>(result => result);
    }
}
