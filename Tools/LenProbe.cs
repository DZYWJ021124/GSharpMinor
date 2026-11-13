using System;
using System.IO;
using NAudio.Wave;

/* 时长量化：对真实曲目比较「容器头部估算 TotalTime」vs「读到 EOF 的 CurrentTime」
   vs「解码字节数/字节率」（真值）。列 = ext / 文件 / est / cur / trueBytes / 判定 */
class LenProbe
{
    static void Main(string[] args)
    {
        string[] dirs = { @"E:\Music\流媒体", @"E:\Music\古典", @"E:\Music\CD抓轨" };
        string[] exts = { ".flac", ".wav", ".mp3", ".m4a", ".mp4", ".ogg", ".opus", ".wma", ".aac", ".aiff", ".aif", ".dsf", ".dff", ".ape", ".wv" };
        int bad = 0, all = 0;
        foreach (var dir in dirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(exts, ext) < 0) continue;
                all++;
                try
                {
                    WaveStream ws;
                    try { ws = new MediaFoundationReader(f); }
                    catch { ws = new AudioFileReader(f); }
                    double est = ws.TotalTime.TotalSeconds;
                    var wf = ws.WaveFormat;
                    long bytes = 0;
                    var buf = new byte[1 << 16];
                    int n;
                    while ((n = ws.Read(buf, 0, buf.Length)) > 0) bytes += n;
                    double tCur = ws.CurrentTime.TotalSeconds;
                    double tBytes = bytes / (double)wf.AverageBytesPerSecond;
                    string reader = ws.GetType().Name;
                    ws.Dispose();
                    bool mismatch = Math.Abs(est - tBytes) > 1.0;
                    bool badCur = Math.Abs(tCur - tBytes) > 0.5;
                    if (mismatch || badCur) bad++;
                    Console.WriteLine("{0}|{1}|est={2:0.00}|cur={3:0.00}|true={4:0.00}|{5}|{6}{7}",
                        ext.TrimStart('.'), Path.GetFileName(f), est, tCur, tBytes, reader,
                        mismatch ? "EST_BAD " : "", badCur ? "CUR_BAD" : (mismatch ? "" : "ok"));
                }
                catch (Exception ex)
                {
                    bad++;
                    Console.WriteLine("{0}|{1}|ERR|{2}", ext.TrimStart('.'), Path.GetFileName(f), ex.Message.Replace('\n', ' '));
                }
            }
        }
        Console.WriteLine("TOTAL={0} BAD={1}", all, bad);
    }
}
