using System.ComponentModel;
using GimmeCapture.Services.Core.Infrastructure;

namespace GimmeCapture.Services.Abstractions;

/// <summary>
/// The UI strings in the current language. View models read them through <c>ViewModelBase.Localization</c> rather than
/// the static <see cref="LocalizationService.Instance"/>, so a test can hand one a stand-in. Raises PropertyChanged when
/// the language changes.
/// </summary>
public interface ILocalizationService : INotifyPropertyChanged
{
    /// <summary>The string for <paramref name="key"/> in the current language, or the key itself when there is none.</summary>
    string this[string key] { get; }

    Language CurrentLanguage { get; set; }

    /// <summary>Switches to the next UI language.</summary>
    void CycleLanguage();
}
