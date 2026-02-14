using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Platform;
using ReactiveUI;

namespace GimmeCapture.Services.Core.Infrastructure;
    public enum Language
    {
        English,
        Chinese, // Traditional
        Japanese
    }

    public class LocalizationService : ReactiveObject
    {
        private static LocalizationService? _instance;
        public static LocalizationService Instance => _instance ??= new LocalizationService();

        private Language _currentLanguage;
        public Language CurrentLanguage
        {
            get => _currentLanguage;
            set
            {
                if (_currentLanguage != value)
                {
                    this.RaiseAndSetIfChanged(ref _currentLanguage, value);
                    UpdateFont(value);

                    // Notify indexer changes for binding updates
                    this.RaisePropertyChanged("Item");
                    this.RaisePropertyChanged("Item[]");
                }
            }
        }

        private FontFamily _currentFontFamily;
        public FontFamily CurrentFontFamily
        {
            get => _currentFontFamily;
            private set => this.RaiseAndSetIfChanged(ref _currentFontFamily, value);
        }

        // Font Paths
        private readonly FontFamily _fontEnglish = new FontFamily("avares://GimmeCapture/Assets/Fonts/Cinzel/Cinzel-VariableFont_wght.ttf#Cinzel");
        private readonly FontFamily _fontChinese = new FontFamily("avares://GimmeCapture/Assets/Fonts/Noto_Serif_TC/NotoSerifTC-VariableFont_wght.ttf#Noto Serif TC");
        private readonly FontFamily _fontJapanese = new FontFamily("avares://GimmeCapture/Assets/Fonts/Noto_Serif_JP/NotoSerifJP-VariableFont_wght.ttf#Noto Serif JP");

        private readonly Dictionary<Language, Dictionary<string, string>> _translations = new();

        private LocalizationService()
        {
            _currentFontFamily = _fontEnglish;
            LoadAllTranslations();
            UpdateFont(Language.English);
        }

        private void LoadAllTranslations()
        {
            LoadTranslation(Language.English, "en-US");
            LoadTranslation(Language.Chinese, "zh-TW");
            LoadTranslation(Language.Japanese, "ja-JP");
        }

        private void LoadTranslation(Language lang, string fileName)
        {
            try
            {
                var uri = new Uri($"avares://GimmeCapture/Assets/Localization/{fileName}.json");
                using var stream = AssetLoader.Open(uri);
                using var reader = new StreamReader(stream);
                var json = reader.ReadToEnd();
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                {
                    _translations[lang] = dict;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[LocalizationService] Failed to load translation for {lang}: {ex.Message}");
                _translations[lang] = new Dictionary<string, string>();
            }
        }

        private void UpdateFont(Language lang)
        {
            switch (lang)
            {
                case Language.English:
                    CurrentFontFamily = _fontEnglish;
                    break;
                case Language.Chinese:
                    CurrentFontFamily = _fontChinese;
                    break;
                case Language.Japanese:
                    CurrentFontFamily = _fontJapanese;
                    break;
            }
        }

        public string this[string key]
        {
            get
            {
                if (_translations.TryGetValue(_currentLanguage, out var dict) && dict.TryGetValue(key, out var val))
                {
                    return val;
                }
                return key; // Fallback
            }
        }

        public Dictionary<string, string> CurrentTranslations => _translations[_currentLanguage];

        public void CycleLanguage()
        {
            var next = (int)CurrentLanguage + 1;
            if (next > (int)Language.Japanese) next = 0;
            CurrentLanguage = (Language)next;
        }
    }

