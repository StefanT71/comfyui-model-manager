using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace ComfyModelManager
{
    public class LanguageInfo
    {
        public string Code;
        public string Name;
        public string CultureName;
        public string FilePath;
        public bool IsBuiltIn;

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// Språkhantering. Engelska är inbyggt (inbäddad resurs från lang\_template.ini);
    /// övriga språk läses från lang\&lt;kod&gt;.ini. Saknade nycklar faller tillbaka till engelska.
    /// </summary>
    public static class Lang
    {
        private class ParsedLanguage
        {
            public string Name;
            public string Culture;
            public readonly Dictionary<string, string> Values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        private const string EnglishResource = "ComfyUI.ModelManager.English.ini";

        private static readonly Dictionary<string, string> English;
        private static Dictionary<string, string> _values;
        private static string _code = "en";
        private static string _name = "English";
        private static CultureInfo _culture = CultureInfo.GetCultureInfo("en-US");

        static Lang()
        {
            English = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            LoadEnglish();
            _values = new Dictionary<string, string>(English, StringComparer.OrdinalIgnoreCase);
            _name = GetMeta(English, "name", "English");
            _culture = ResolveCulture(GetMeta(English, "culture", "en-US"), CultureInfo.GetCultureInfo("en-US"));
        }

        public static string CurrentCode { get { return _code; } }
        public static string CurrentName { get { return _name; } }
        public static CultureInfo Culture { get { return _culture; } }

        public static string Folder
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang"); }
        }

        public static void Load(string code)
        {
            _values = new Dictionary<string, string>(English, StringComparer.OrdinalIgnoreCase);
            _code = "en";
            _name = GetMeta(English, "name", "English");
            _culture = ResolveCulture(GetMeta(English, "culture", "en-US"), CultureInfo.GetCultureInfo("en-US"));

            if (string.IsNullOrEmpty(code) || string.Equals(code, "en", StringComparison.OrdinalIgnoreCase))
                return;

            string path = FindLanguageFile(code);
            if (path == null) return;

            try
            {
                ParsedLanguage parsed = ParseText(ReadTextSmart(path));
                foreach (KeyValuePair<string, string> pair in parsed.Values)
                    _values[pair.Key] = pair.Value;

                _code = Path.GetFileNameWithoutExtension(path);
                _name = string.IsNullOrEmpty(parsed.Name) ? _code : parsed.Name;
                _culture = ResolveCulture(parsed.Culture, CultureInfo.CurrentCulture);
            }
            catch
            {
                // Ogiltig språkfil: behåll engelska.
            }
        }

        public static bool LanguageExists(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) return true;
            return FindLanguageFile(code) != null;
        }

        public static List<LanguageInfo> AvailableLanguages()
        {
            List<LanguageInfo> result = new List<LanguageInfo>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            LanguageInfo english = new LanguageInfo();
            english.Code = "en";
            english.Name = GetMeta(English, "name", "English");
            english.CultureName = GetMeta(English, "culture", "en-US");
            english.IsBuiltIn = true;
            result.Add(english);
            seen.Add("en");

            List<LanguageInfo> external = new List<LanguageInfo>();
            try
            {
                if (Directory.Exists(Folder))
                {
                    string[] files = Directory.GetFiles(Folder, "*.ini");
                    for (int i = 0; i < files.Length; i++)
                    {
                        string path = files[i];
                        string code = Path.GetFileNameWithoutExtension(path);
                        if (string.IsNullOrEmpty(code)) continue;
                        if (code.StartsWith("_", StringComparison.Ordinal)) continue;
                        if (string.Equals(code, "en", StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Contains(code)) continue;

                        try
                        {
                            ParsedLanguage parsed = ParseText(ReadTextSmart(path));
                            LanguageInfo info = new LanguageInfo();
                            info.Code = code;
                            info.Name = string.IsNullOrEmpty(parsed.Name) ? code : parsed.Name;
                            info.CultureName = parsed.Culture;
                            info.FilePath = path;
                            external.Add(info);
                            seen.Add(code);
                        }
                        catch
                        {
                            // Hoppa över oläsbar/ogiltig fil.
                        }
                    }
                }
            }
            catch
            {
                // Om lang-mappen inte kan läsas visas bara engelska.
            }

            external.Sort(delegate(LanguageInfo a, LanguageInfo b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            result.AddRange(external);
            return result;
        }

        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            string value;
            if (_values.TryGetValue(key, out value)) return value;
            if (English.TryGetValue(key, out value)) return value;
            return key;
        }

        public static string F(string key, params object[] args)
        {
            string format = T(key);
            try
            {
                return string.Format(_culture, format, args);
            }
            catch (FormatException)
            {
                string fallback;
                if (English.TryGetValue(key, out fallback) &&
                    !string.Equals(fallback, format, StringComparison.Ordinal))
                {
                    try { return string.Format(CultureInfo.InvariantCulture, fallback, args); }
                    catch { }
                }
                return format;
            }
        }

        public static string Plural(string key, int count)
        {
            return T(key + (count == 1 ? ".one" : ".other"));
        }

        public static void OpenLanguageFolder()
        {
            Directory.CreateDirectory(Folder);
            Process.Start("explorer.exe", "\"" + Folder + "\"");
        }

        private static void LoadEnglish()
        {
            try
            {
                Assembly assembly = typeof(Lang).Assembly;
                using (Stream stream = assembly.GetManifestResourceStream(EnglishResource))
                {
                    if (stream != null)
                    {
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                            CopyParsed(ParseText(reader.ReadToEnd()), English);
                        return;
                    }
                }
            }
            catch
            {
                // faller tillbaka på extern mall nedan
            }

            try
            {
                string path = Path.Combine(Folder, "_template.ini");
                if (File.Exists(path))
                    CopyParsed(ParseText(ReadTextSmart(path)), English);
            }
            catch
            {
                // utan engelsk text visas nyckelnamnen
            }
        }

        private static void CopyParsed(ParsedLanguage parsed, Dictionary<string, string> target)
        {
            foreach (KeyValuePair<string, string> pair in parsed.Values)
                target[pair.Key] = pair.Value;
        }

        private static string FindLanguageFile(string code)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;

                string[] files = Directory.GetFiles(Folder, "*.ini");
                for (int i = 0; i < files.Length; i++)
                {
                    string baseName = Path.GetFileNameWithoutExtension(files[i]);
                    if (string.Equals(baseName, code, StringComparison.OrdinalIgnoreCase))
                        return files[i];
                }
            }
            catch
            {
                // ignorera
            }
            return null;
        }

        private static ParsedLanguage ParseText(string text)
        {
            ParsedLanguage result = new ParsedLanguage();
            using (StringReader reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0) continue;
                    if (trimmed.StartsWith(";") || trimmed.StartsWith("#")) continue;
                    if (trimmed.StartsWith("[")) continue;

                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0) continue;

                    string key = trimmed.Substring(0, equals).Trim();
                    string value = trimmed.Substring(equals + 1).Trim();
                    if (key.Length == 0) continue;

                    value = value.Replace("\\n", Environment.NewLine);
                    result.Values[key] = value;

                    if (string.Equals(key, "name", StringComparison.OrdinalIgnoreCase))
                        result.Name = value;
                    else if (string.Equals(key, "culture", StringComparison.OrdinalIgnoreCase))
                        result.Culture = value;
                }
            }
            return result;
        }

        private static string ReadTextSmart(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);

            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Default.GetString(bytes);
            }
        }

        private static string GetMeta(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            if (values.TryGetValue(key, out value) && !string.IsNullOrEmpty(value)) return value;
            return fallback;
        }

        private static CultureInfo ResolveCulture(string name, CultureInfo fallback)
        {
            if (!string.IsNullOrEmpty(name))
            {
                try { return CultureInfo.GetCultureInfo(name); }
                catch { }
            }
            return fallback ?? CultureInfo.CurrentCulture;
        }
    }
}
