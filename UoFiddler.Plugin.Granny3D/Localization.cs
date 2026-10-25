using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace UoFiddler.Plugin.Granny3D
{
    public static class Localization
    {
        private static readonly Dictionary<string, string> Strings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public static string CurrentLanguage { get; private set; } = "fr";

        public static event Action? LanguageChanged;

        public static void Initialize(string? initialLang = null)
        {
            string lang = !string.IsNullOrEmpty(initialLang) ? initialLang : "fr";
            SetLanguage(lang);
        }

        public static void SetLanguage(string lang)
        {
            if (string.IsNullOrWhiteSpace(lang)) lang = "fr";
            lang = lang.Trim().ToLowerInvariant();
            if (lang != "en" && lang != "fr") lang = "fr";

            CurrentLanguage = lang;
            Strings.Clear();

            string jsonPath = FindLanguageFile(lang);
            if (File.Exists(jsonPath))
            {
                try
                {
                    string json = File.ReadAllText(jsonPath);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            Strings[kvp.Key] = kvp.Value;
                        }
                    }
                }
                catch { }
            }

            LanguageChanged?.Invoke();
        }

        public static string Tr(string key, string? defaultText = null)
        {
            if (string.IsNullOrEmpty(key)) return defaultText ?? string.Empty;
            if (Strings.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                return val;
            return defaultText ?? key;
        }

        public static string TrFmt(string key, string defaultFormat, params object[] args)
        {
            string fmt = Tr(key, defaultFormat);
            try
            {
                return string.Format(fmt, args);
            }
            catch
            {
                return fmt;
            }
        }

        private static string FindLanguageFile(string lang)
        {
            string fileName = $"{lang}.json";

            // 1. Languages folder alongside this assembly
            string? asmDir = Path.GetDirectoryName(typeof(Localization).Assembly.Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                string p = Path.Combine(asmDir, "Languages", fileName);
                if (File.Exists(p)) return p;
            }

            // 2. Base directory + plugins/Languages
            string p2 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins", "Languages", fileName);
            if (File.Exists(p2)) return p2;

            // 3. Base directory + Languages
            string p3 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages", fileName);
            if (File.Exists(p3)) return p3;

            // 4. Fallback path to write to if not found
            if (!string.IsNullOrEmpty(asmDir))
                return Path.Combine(asmDir, "Languages", fileName);

            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Languages", fileName);
        }
    }
}
