using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ComfyModelManager
{
    /// <summary>
    /// Minimal ini-läsare/skrivare. Bevarar ordningen på sektioner och nycklar.
    /// </summary>
    public class IniFile
    {
        private readonly string _path;
        private readonly List<string> _sectionOrder = new List<string>();
        private readonly Dictionary<string, List<string>> _keyOrder =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<string, string>> _data =
            new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        public IniFile(string path)
        {
            _path = path;
            Load();
        }

        public string Path_ { get { return _path; } }

        private void Load()
        {
            if (!File.Exists(_path))
            {
                EnsureSection("Folders");
                return;
            }

            string current = "";
            string[] lines;
            try
            {
                lines = File.ReadAllLines(_path, Encoding.UTF8);
            }
            catch
            {
                EnsureSection("Folders");
                return;
            }

            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith(";") || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    EnsureSection(current);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                if (current.Length == 0)
                {
                    current = "Folders";
                    EnsureSection(current);
                }

                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                Set(current, key, value);
            }

            EnsureSection("Folders");
        }

        private void EnsureSection(string name)
        {
            if (_data.ContainsKey(name)) return;
            _data[name] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _keyOrder[name] = new List<string>();
            _sectionOrder.Add(name);
        }

        public string Get(string section, string key, string defaultValue)
        {
            Dictionary<string, string> values;
            if (_data.TryGetValue(section, out values))
            {
                string value;
                if (values.TryGetValue(key, out value)) return value;
            }
            return defaultValue;
        }

        public void Set(string section, string key, string value)
        {
            EnsureSection(section);
            Dictionary<string, string> values = _data[section];
            values[key] = value ?? "";
            List<string> keys = _keyOrder[section];
            if (keys.FindIndex(delegate(string k) { return string.Equals(k, key, StringComparison.OrdinalIgnoreCase); }) < 0)
                keys.Add(key);
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string section in _sectionOrder)
            {
                sb.Append('[').Append(section).Append("]\r\n");
                foreach (string key in _keyOrder[section])
                {
                    sb.Append(key).Append('=');
                    sb.Append(_data[section][key]);
                    sb.Append("\r\n");
                }
                sb.Append("\r\n");
            }

            try
            {
                string dir = System.IO.Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(_path, sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Kan inte skriva (t.ex. skrivskyddad mapp) - inställningarna fungerar ändå i minnet.
            }
        }
    }
}
