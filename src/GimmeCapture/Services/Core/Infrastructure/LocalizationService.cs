using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Avalonia.Media;
using Avalonia.Platform;
using GimmeCapture.Services.Abstractions;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace GimmeCapture.Services.Core.Infrastructure;
    public enum Language
    {
        English,
        Chinese, // Traditional
        Japanese
    }

    public class LocalizationService : ReactiveObject, ILocalizationService
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

        private FontFamily _currentButtonFontFamily;
        /// <summary>
        /// The gothic buttons' face: Cinzel for Latin letters, then the current language's CJK font. A fixed Cinzel,
        /// Japanese, Chinese chain drew Chinese labels in Japanese glyph forms and loaded both CJK fonts.
        /// </summary>
        public FontFamily CurrentButtonFontFamily
        {
            get => _currentButtonFontFamily;
            private set => this.RaiseAndSetIfChanged(ref _currentButtonFontFamily, value);
        }

        // Font Paths. The CJK fonts come from MappedFontCollection, which reads them in place rather than copying them.
        private const string CinzelSource = "avares://GimmeCapture/Assets/Fonts/Cinzel/Cinzel-VariableFont_wght.ttf#Cinzel";
        internal const string ChineseSource = Platforms.Avalonia.MappedFontCollection.KeyText + "#Noto Serif TC";
        internal const string JapaneseSource = Platforms.Avalonia.MappedFontCollection.KeyText + "#Noto Serif JP";
        private readonly FontFamily _fontEnglish = new FontFamily(CinzelSource);
        private readonly FontFamily _fontChinese = new FontFamily(ChineseSource);
        private readonly FontFamily _fontJapanese = new FontFamily(JapaneseSource);

        // Each language's strings, by Language, read the first time one of them is looked up: a run uses one language,
        // and reading all three took about 7 ms of the start's UI thread, most of it System.Text.Json setting itself
        // up for a dictionary (a probe, its serializer warmed on another thread by a settings-like class, as the
        // app's is).
        private static readonly string[] FileNames = ["en-US", "zh-TW", "ja-JP"];
        private readonly Dictionary<string, string>?[] _translations = new Dictionary<string, string>?[3];

        private LocalizationService()
        {
            _currentFontFamily = _fontEnglish;
            _currentButtonFontFamily = FontFamily.Parse(ButtonFontSources(Language.English));
            UpdateFont(Language.English);
        }

        private Dictionary<string, string> TranslationsFor(Language lang)
        {
            int index = (int)lang;
            if (Volatile.Read(ref _translations[index]) is { } loaded)
            {
                return loaded;
            }

            // Strings are looked up from more than one thread; whichever is first reads the file, the rest wait for it.
            lock (_translations)
            {
                return _translations[index] ??= LoadTranslation(FileNames[index]);
            }
        }

        private static Dictionary<string, string> LoadTranslation(string fileName)
        {
            try
            {
                var uri = new Uri($"avares://GimmeCapture/Assets/Localization/{fileName}.json");
                using var stream = AssetLoader.Open(uri);
                byte[] json = new byte[stream.Length];
                stream.ReadExactly(json);
                return ReadTranslations(json);
            }
            catch (Exception ex)
            {
                AppLog.Warning("Localization.Load", ex);
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// A locale file's strings: one JSON object of string values, read with <see cref="Utf8JsonReader"/>. The
        /// serializer's first dictionary of a run cost 5-7 ms against the reader's 1.2-1.4 (a probe, its serializer
        /// already used for a settings-like class); the strings come out the same.
        /// </summary>
        internal static Dictionary<string, string> ReadTranslations(ReadOnlySpan<byte> utf8Json)
        {
            ReadOnlySpan<byte> bom = [0xEF, 0xBB, 0xBF];
            if (utf8Json.StartsWith(bom))
            {
                utf8Json = utf8Json[bom.Length..];
            }

            var reader = new Utf8JsonReader(utf8Json);
            var translations = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A locale file holds one object.");
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString()!;
                if (!reader.Read() || reader.TokenType != JsonTokenType.String)
                {
                    throw new JsonException($"The value of {key} is not a string.");
                }

                translations[key] = reader.GetString()!;
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                throw new JsonException("A locale file holds one object of strings.");
            }

            return translations;
        }

        /// <summary>The font sources behind <see cref="CurrentButtonFontFamily"/>, first choice first.</summary>
        internal static string ButtonFontSources(Language lang) => lang switch
        {
            Language.Chinese => $"{CinzelSource}, {ChineseSource}",
            Language.Japanese => $"{CinzelSource}, {JapaneseSource}",
            _ => $"{CinzelSource}, {JapaneseSource}, {ChineseSource}",
        };

        private void UpdateFont(Language lang)
        {
            CurrentButtonFontFamily = FontFamily.Parse(ButtonFontSources(lang));
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
                if (TranslationsFor(_currentLanguage).TryGetValue(key, out var val))
                {
                    return val;
                }
                return key; // Fallback
            }
        }

        public void CycleLanguage()
        {
            var next = (int)CurrentLanguage + 1;
            if (next > (int)Language.Japanese) next = 0;
            CurrentLanguage = (Language)next;
        }
    }

