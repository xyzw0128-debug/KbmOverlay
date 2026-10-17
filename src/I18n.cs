using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace KbmOverlay
{
    // Translations for the console and the pages, from pages/i18n/<code>.json.
    // The console (here) and the pages (overlay.js) read the same files. A file dropped into the
    // "pages/i18n" folder next to the exe adds a language without rebuilding.
    //
    // Language: --lang, else the one picked on the settings page, else the Windows display language
    // (full name first, e.g. "zh-TW", then two letters, e.g. "zh"), else English.
    // Keys missing from a translation fall back to English.
    internal static class I18n
    {
        private static readonly Regex CodePattern = new Regex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$");

        // Order of the settings page's language menu; other languages follow alphabetically.
        private static readonly string[] MenuOrder = { "en", "ko", "ja", "zh" };
        private static readonly Regex Placeholder = new Regex(@"\{(\w+)\}");

        private static PageStore pages;
        private static Dictionary<string, object> english = new Dictionary<string, object>();
        private static Dictionary<string, object> current = english;

        public static string Lang { get; private set; } = "en";

        public static void Init(PageStore store, string preferred)
        {
            pages = store;
            english = Load("en") ?? new Dictionary<string, object>();
            Use(preferred);
        }

        // Switches language; null or "auto" means the Windows display language.
        public static void Use(string preferred)
        {
            Lang = Pick(preferred);
            current = Lang == "en" ? english : Load(Lang) ?? english;
        }

        // The language "automatic" resolves to on this PC.
        public static string SystemLang => Pick(null);

        public static List<KeyValuePair<string, string>> Available()
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (string path in pages.List("i18n/"))
            {
                if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                string code = path.Substring(5, path.Length - 10);
                if (!CodePattern.IsMatch(code)) continue;
                result.Add(new KeyValuePair<string, string>(code, NameOf(code)));
            }
            result.Sort((a, b) =>
            {
                int byMenu = MenuRank(a.Key).CompareTo(MenuRank(b.Key));
                return byMenu != 0 ? byMenu : string.CompareOrdinal(a.Key, b.Key);
            });
            return result;
        }

        // "zh-TW" ranks with "zh" (and after it, by the name comparison).
        private static int MenuRank(string code)
        {
            int dash = code.IndexOf('-');
            int index = Array.IndexOf(MenuOrder, dash < 0 ? code : code.Substring(0, dash));
            return index < 0 ? MenuOrder.Length : index;
        }

        public static string NameOf(string code)
        {
            Dictionary<string, object> map = code == "en" ? english : Load(code);
            return Json.Str(map, "_name") ?? code;
        }

        public static bool Has(string code) => code != null && CodePattern.IsMatch(code) && pages.Exists("i18n/" + code + ".json");

        // T("receive.request1", ("name", name), ("address", address))
        public static string T(string key, params (string Name, object Value)[] args)
        {
            string text = Json.Str(current, key) ?? Json.Str(english, key) ?? key;
            if (args.Length == 0) return text;
            return Placeholder.Replace(text, m =>
            {
                foreach ((string Name, object Value) arg in args)
                    if (arg.Name == m.Groups[1].Value) return Convert.ToString(arg.Value, CultureInfo.InvariantCulture);
                return m.Value;
            });
        }

        private static string Pick(string preferred)
        {
            if (!string.IsNullOrEmpty(preferred) && preferred != "auto" && Has(preferred)) return preferred;
            CultureInfo culture = CultureInfo.CurrentUICulture;
            if (Has(culture.Name)) return culture.Name;
            // Chinese: Taiwan, Hong Kong and Macau use Traditional characters, if such a file exists.
            if (culture.TwoLetterISOLanguageName == "zh" && (culture.Name.EndsWith("TW") || culture.Name.EndsWith("HK") || culture.Name.EndsWith("MO") || culture.Name.Contains("Hant")) && Has("zh-TW"))
                return "zh-TW";
            if (Has(culture.TwoLetterISOLanguageName)) return culture.TwoLetterISOLanguageName;
            return "en";
        }

        private static Dictionary<string, object> Load(string code)
        {
            if (pages == null || !pages.TryGet("i18n/" + code + ".json", out byte[] data)) return null;
            string text = Encoding.UTF8.GetString(data);
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            return Json.Parse(text);
        }
    }
}
