using GimmeCapture.Services.Abstractions;
using GimmeCapture.Services.Core.Infrastructure;
using ReactiveUI;

namespace GimmeCapture.ViewModels;

public class ViewModelBase : ReactiveObject
{
    public ViewModelBase()
        : this(null)
    {
    }

    /// <param name="localization">The UI strings; the app's own (<see cref="LocalizationService.Instance"/>) when null.</param>
    protected ViewModelBase(ILocalizationService? localization)
    {
        Localization = localization ?? LocalizationService.Instance;
    }

    /// <summary>The UI strings in the current language. Read them here, not from the static instance.</summary>
    protected ILocalizationService Localization { get; }
}
