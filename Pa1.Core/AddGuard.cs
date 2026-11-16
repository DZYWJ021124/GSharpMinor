using System;
using System.Collections.Generic;
using System.IO;

namespace Pa1.Core
{
    /// <summary>
    /// 添加曲目去重校验：路径规范化键 + 批内/库内重复过滤。
    /// 同一文件的大小写差异、"/"与"\"、"."/".." 段、末尾分隔符视为同一曲目
    /// （Windows 文件系统大小写不敏感）；键比较一律 OrdinalIgnoreCase。
    /// </summary>
    public static class AddGuard
    {
        /// <summary>规范化路径键：绝对化 + 去尾分隔符；空输入返回空串，非法路径原样返回（不抛）。</summary>
        public static string Key(string path)
        {
            if (path == null) return "";
            string p = path.Trim();
            if (p.Length == 0) return "";
            try { p = Path.GetFullPath(p); }
            catch { return p; }
            return p.TrimEnd('\\', '/');
        }

        /// <summary>
        /// 过滤出真正的新曲目：与已有库（existingPaths）重复的计入 skippedExisting，
        /// 批内互相重复的计入 skippedDuplicate，其余按原序返回。
        /// </summary>
        public static List<string> FilterNew(IEnumerable<string> files, IEnumerable<string> existingPaths,
            out int skippedExisting, out int skippedDuplicate)
        {
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (existingPaths != null)
                foreach (var e in existingPaths)
                {
                    string k = Key(e);
                    if (k.Length > 0) existing.Add(k);
                }

            var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
            var fresh = new List<string>();
            skippedExisting = 0;
            skippedDuplicate = 0;

            if (files == null) return fresh;
            foreach (var f in files)
            {
                string k = Key(f);
                if (k.Length == 0) continue;
                if (existing.Contains(k)) { skippedExisting++; continue; }
                if (!seen.Add(k)) { skippedDuplicate++; continue; }
                fresh.Add(f);
            }
            return fresh;
        }
    }
}
