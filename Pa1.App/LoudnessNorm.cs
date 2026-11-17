using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using NAudio.Wave;
using Pa1.Core;

namespace Pa1.App
{
    /// <summary>
    /// 响度测量持久缓存：%APPDATA%\GSharpMinor\loudness.txt，行格式 path|mtimeTicks|lufs|peakDb。
    /// mtime 校验防文件更换后读旧值。行含 '|' 的路径会失效（罕见）——按文件重写，不做增量追加。
    /// </summary>
    public static class LoudnessCache
    {
        static readonly string FilePath = Path.Combine(DataDirs.Roaming, "loudness.txt");
        static readonly Dictionary<string, string> _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        static readonly object _lk = new object();
        static bool _loaded;

        static void EnsureLoaded()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (var ln in File.ReadAllLines(FilePath))
                {
                    int i = ln.IndexOf('|');
                    if (i <= 0) continue;
                    _map[ln.Substring(0, i)] = ln.Substring(i + 1);
                }
            }
            catch { /* 缓存损坏不致命 */ }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sw = new StringWriter();
                foreach (var kv in _map) sw.WriteLine(kv.Key + "|" + kv.Value);
                File.WriteAllText(FilePath, sw.ToString());
            }
            catch { /* 只读盘等不致命 */ }
        }

        public static bool TryGet(string path, out float lufs, out float peakDb)
        {
            lufs = float.NaN; peakDb = float.NaN;
            lock (_lk)
            {
                EnsureLoaded();
                string v;
                if (!_map.TryGetValue(path, out v)) return false;
                var parts = v.Split('|');
                long mt;
                if (parts.Length < 2 || !long.TryParse(parts[0], out mt)) return false;
                try
                {
                    if (File.GetLastWriteTimeUtc(path).Ticks != mt) return false;
                }
                catch { return false; }
                float l, p = float.NaN;
                if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out l)) return false;
                if (parts.Length > 2) float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out p);
                lufs = l; peakDb = p;
                return true;
            }
        }

        public static void Put(string path, float lufs, float peakDb)
        {
            lock (_lk)
            {
                EnsureLoaded();
                try
                {
                    string mt = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
                    _map[path] = mt + "|" + lufs.ToString(CultureInfo.InvariantCulture)
                               + "|" + peakDb.ToString(CultureInfo.InvariantCulture);
                }
                catch { return; }
                Save();
            }
        }
    }

    /// <summary>
    /// 全文件 R128 响度扫描：PCM 走 NAudio 解码器，DSD 走 DsdDecoder 管线（48k 目标率，流式恒定内存）。
    /// 单专用工作线程串行消费优先队列——绝不用线程池：Task.Run + 阻塞信号量曾钉死池线程，
    /// 曲库恢复批量预热时把 PlayIndex 的加载任务饿死在队列里（播放永远卡 LOADING）。
    /// </summary>
    public static class LoudnessScanner
    {
        static readonly object _qLk = new object();
        static readonly List<KeyValuePair<string, Action>> _q = new List<KeyValuePair<string, Action>>();
        static Thread _worker;

        /// <summary>入队后台扫描（缓存命中则跳过）。priority=true 插到队首（当前播放曲目优先）。</summary>
        public static void Request(string path, Action onDone = null, bool priority = false)
        {
            lock (_qLk)
            {
                if (priority) _q.Insert(0, new KeyValuePair<string, Action>(path, onDone));
                else _q.Add(new KeyValuePair<string, Action>(path, onDone));
                if (_worker == null || !_worker.IsAlive)
                {
                    _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "pa1-loudness-scan" };
                    _worker.Start();
                }
            }
        }

        static void WorkerLoop()
        {
            while (true)
            {
                KeyValuePair<string, Action> job;
                lock (_qLk)
                {
                    while (_q.Count == 0) Monitor.Wait(_qLk);
                    job = _q[0];
                    _q.RemoveAt(0);
                }
                try
                {
                    float l, p;
                    if (!LoudnessCache.TryGet(job.Key, out l, out p) && TryScan(job.Key, out l, out p))
                        LoudnessCache.Put(job.Key, l, p);
                }
                catch { }
                try { if (job.Value != null) job.Value(); } catch { }
            }
        }

        /// <summary>阻塞式全文件扫描。失败返回 false。</summary>
        public static bool TryScan(string path, out float lufs, out float peakDb)
        {
            lufs = float.NaN; peakDb = float.NaN;
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".dsf" || ext == ".dff") return TryScanDsd(path, out lufs, out peakDb);

                ISampleProvider sp;
                AudioFileReader afr = null;
                WaveStream ws = null;
                try
                {
                    if (ext == ".flac") { ws = new NAudio.Flac.FlacReader(path); sp = ws.ToSampleProvider(); }
                    else { afr = new AudioFileReader(path); sp = afr; }
                    int ch = Math.Max(1, sp.WaveFormat.Channels);
                    var meter = new LoudnessMeter(sp.WaveFormat.SampleRate, ch);
                    var buf = new float[8192 * ch];
                    int got;
                    while ((got = sp.Read(buf, 0, buf.Length)) > 0)
                        meter.Process(buf, 0, got / ch);
                    meter.Finish();
                    lufs = (float)meter.IntegratedLufs;
                    peakDb = (float)meter.PeakDb;
                    return true;
                }
                finally
                {
                    if (afr != null) afr.Dispose();
                    if (ws != null) ws.Dispose();
                }
            }
            catch { return false; }
        }

        static bool TryScanDsd(string path, out float lufs, out float peakDb)
        {
            lufs = float.NaN; peakDb = float.NaN;
            var t = TagReader.Read(path);
            if (t.Spec == null || t.Spec.Rate <= 0) return false;
            var meta = new DsdDecoder.DsdMeta
            {
                Ok = true,
                Container = t.Spec.Fmt,
                Rate = t.Spec.Rate,
                Channels = Math.Max(1, t.Spec.Channels),
                SampleCount = t.Spec.SampleCount,
                BlockSize = t.Spec.BlockSize,
                DataOffset = t.Spec.DataOffset,
                DataBytes = t.Spec.DataBytes,
                Id3Offset = t.Spec.Id3Offset,
                LsbFirst = t.Spec.Fmt == "DSF",
            };
            int ch = meta.Channels;
            var meter = new LoudnessMeter(48000, ch);
            var pipe = DsdDecoder.MakePipeline(meta, 48000, false);
            using (var fs = File.OpenRead(path))
            {
                long dataEnd = DsdDecoder.AudioDataEnd(meta, fs.Length);
                long pos = Math.Min(Math.Max(0, meta.DataOffset), fs.Length);
                fs.Position = pos;
                int block = (meta.Container == "DSF" ? meta.BlockSize : 4096) * ch;
                if (block <= 0) block = 4096 * ch;
                var buf = new byte[block];
                var inter = new float[block];
                while (pos < dataEnd)
                {
                    int len = (int)Math.Min(block, dataEnd - pos);
                    int got = 0;
                    while (got < len)
                    {
                        int r = fs.Read(buf, got, len - got);
                        if (r <= 0) break;
                        got += r;
                    }
                    if (got <= 0) break;
                    pos += got;
                    var planes = DsdDecoder.FeedBlock(pipe, buf, got, meta);
                    if (planes == null || planes.Length == 0 || planes[0].Length == 0) continue;
                    int frames = planes[0].Length;
                    for (int f = 0; f < frames; f++)
                        for (int c = 0; c < ch; c++)
                            inter[f * ch + c] = planes[c][f];
                    meter.Process(inter, 0, frames);
                }
            }
            meter.Finish();
            lufs = (float)meter.IntegratedLufs;
            peakDb = (float)meter.PeakDb;
            return true;
        }
    }
}
