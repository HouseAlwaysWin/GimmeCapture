using Avalonia.Media.Imaging;
using ReactiveUI;
using System.Reactive;
using System.Threading.Tasks;

namespace GimmeCapture.ViewModels;

public class FloatingImageViewModel : ViewModelBase
{
    private Bitmap? _image;
    public Bitmap? Image
    {
        get => _image;
        set => this.RaiseAndSetIfChanged(ref _image, value);
    }
    
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    
    public System.Action? CloseAction { get; set; }
    // Actions for UI interactions (passed from View)
    public System.Func<Task>? CopyAction { get; set; }
    public System.Func<Task>? SaveAction { get; set; }

    public FloatingImageViewModel(Bitmap image)
    {
        Image = image;
        CloseCommand = ReactiveCommand.Create(() => CloseAction?.Invoke());
        
        CopyCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (CopyAction != null) await CopyAction();
        });
        
        SaveCommand = ReactiveCommand.CreateFromTask(async () => 
        {
             if (SaveAction != null) await SaveAction();
        });
    }
}
