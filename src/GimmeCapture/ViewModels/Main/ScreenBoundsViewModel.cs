using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

public class ScreenBoundsViewModel : ReactiveObject
{
    private double _x;
    public double X { get => _x; set => this.RaiseAndSetIfChanged(ref _x, value); }
    
    private double _y;
    public double Y { get => _y; set => this.RaiseAndSetIfChanged(ref _y, value); }
    
    private double _w;
    public double W { get => _w; set => this.RaiseAndSetIfChanged(ref _w, value); }
    
    private double _h;
    public double H { get => _h; set => this.RaiseAndSetIfChanged(ref _h, value); }
}
