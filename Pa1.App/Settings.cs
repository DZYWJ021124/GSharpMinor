using System;
using System.Collections.Generic;
using System.IO;

namespace Pa1.App
{
    /// <summary>用户设置记忆：主题/音量/独占/设备/播放模式/页签/窗口位置。
    /// 存于 %APPDATA%\GSharpMinor\settings.txt（key=value），对应 HTML 版 localStorage。</summary>
    public static class Settings
    {
        static readonly string Dir = DataDirs.Roaming;
        static readonly string FilePath = Path.Combine(Dir, "settings.txt");
        static readonly Dictionary<string, string> _kv =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>自动化测试时置 true，不落盘，防污染用户设置。</summary>
        public static bool NoSave;

        public static void Load()
        {
            _kv.Clear();
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var ln in File.ReadAllLines(FilePath))
                {
                    int i = ln.IndexOf('=');
                    if (i <= 0) continue;
                    _kv[ln.Substring(0, i).Trim()] = ln.Substring(i + 1).Trim();
                }
            }
            catch { /* 设置损坏不致命 */ }
        }

        public static string Get(string key, string def)
        {
            string v;
            return _kv.TryGetValue(key, out v) ? v : def;
        }

        public static int GetInt(string key, int def)
        {
            int n;
            return int.TryParse(Get(key, null), out n) ? n : def;
        }

        public static double GetDouble(string key, double def)
        {
            double n;
            return double.TryParse(Get(key, null), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out n) ? n : def;
        }

        public static void Set(string key, string val) { _kv[key] = val ?? ""; }

        public static void Save()
        {
            if (NoSave) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var sw = new StringWriter();
                foreach (var kv in _kv) sw.WriteLine(kv.Key + "=" + kv.Value);
                File.WriteAllText(FilePath, sw.ToString());
            }
            catch { /* 只读盘等不致命 */ }
        }
    }
}
