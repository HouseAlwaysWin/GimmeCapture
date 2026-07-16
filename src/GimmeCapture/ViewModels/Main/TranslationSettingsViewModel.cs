using System;
using System.Collections.Generic;
using GimmeCapture.Models;
using ReactiveUI;

namespace GimmeCapture.ViewModels.Main;

// Translation settings section (OCR source language, target language, engine). A plain data holder: the
// immediate mirror into the live settings model + the debounced save are wired by MainWindowViewModel (which
// keeps forwarder properties for these so the many Snip / toolbar consumers of SourceLanguage/TargetLanguage
// stay untouched). This is the seed the Llama model catalog will later fold into.
public class TranslationSettingsViewModel : ViewModelBase
{
    public List<TranslationLanguage> AvailableTranslationLanguages =>
        Enum.GetValues<TranslationLanguage>().AsValueEnumerable().ToList();

    public List<OCRLanguage> AvailableOCRLanguages =>
        Enum.GetValues<OCRLanguage>().AsValueEnumerable().ToList();

    public List<TranslationEngine> AvailableTranslationEngines { get; } = new() { TranslationEngine.LlamaSharp };

    private OCRLanguage _sourceLanguage;
    public OCRLanguage SourceLanguage
    {
        get => _sourceLanguage;
        set => this.RaiseAndSetIfChanged(ref _sourceLanguage, value);
    }

    private TranslationLanguage _targetLanguage;
    public TranslationLanguage TargetLanguage
    {
        get => _targetLanguage;
        set => this.RaiseAndSetIfChanged(ref _targetLanguage, value);
    }

    private TranslationEngine _selectedTranslationEngine;
    public TranslationEngine SelectedTranslationEngine
    {
        get => _selectedTranslationEngine;
        set
        {
            if (_selectedTranslationEngine != value)
            {
                this.RaiseAndSetIfChanged(ref _selectedTranslationEngine, value);
                this.RaisePropertyChanged(nameof(IsLlamaVisible));

                // Notify language lists changed
                this.RaisePropertyChanged(nameof(AvailableOCRLanguages));
                this.RaisePropertyChanged(nameof(AvailableTranslationLanguages));
            }
        }
    }

    public bool IsLlamaVisible => SelectedTranslationEngine == TranslationEngine.LlamaSharp;
}
