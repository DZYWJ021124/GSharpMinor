using System;
using System.IO;

namespace Pa1.App
{
    /// <summary>应用数据目录：%APPDATA%\GSharpMinor（设置/曲库/响度缓存）与 %LOCALAPPDATA%\GSharpMinor（Skia 抽出库）。
    /// 首次启动自动把旧 PHOSPHOR-PA1 目录整体迁移过来（Directory.Move = 同卷改名，原子），
    /// 用户设置与曲库一条不丢。MigrateLegacy 必须先于任何类型首次读取 Roaming/Local（App.OnStartup 首行）。</summary>
    public static class DataDirs
    {
        public static readonly string Roaming = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GSharpMinor");
        public static readonly string Local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GSharpMinor");

        public static void MigrateLegacy()
        {
            MoveDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PHOSPHOR-PA1"), Roaming);
            MoveDir(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PHOSPHOR-PA1"), Local);
        }

        /// <summary>新目录不存在 → 整目录改名迁移；两个都在 → 补拷新目录缺失的顶层文件（防数据滞留旧目录）。</summary>
        static void MoveDir(string oldDir, string newDir)
        {
            try
            {
                if (!Directory.Exists(oldDir)) return;
                if (!Directory.Exists(newDir)) { Directory.Move(oldDir, newDir); return; }
                foreach (var f in Directory.GetFiles(oldDir))
                {
                    string dst = Path.Combine(newDir, Path.GetFileName(f));
                    if (!File.Exists(dst)) File.Copy(f, dst);
                }
            }
            catch { /* 迁移失败不致命：新目录从空白开始，不覆盖用户任何现有文件 */ }
        }
    }
}
