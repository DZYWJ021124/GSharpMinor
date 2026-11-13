using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Pa1.Core
{
    /// <summary>
    /// DSD 流式解码源（带预解码 look-ahead）：
    ///   后台线程把后续数据块解成 PCM 预存入有界队列（默认约 3 秒），播放线程只做取块/拷贝，
    ///   不再在音频线程上现场解码（大 DSD256 全量/按块现场解码都曾卡顿）。
    ///   构造即开始预取 → 加载完成后到按下播放之间，队列已被填满（预加载）。
    ///   lookaheadSeconds=0 时退化为纯同步按需解码（供测试对照，输出逐样本一致）。
    ///   Seek = 块边界粗定位 + 管线重建 + 清空预取队列（系数走 MakePipeline 设计缓存，重建近零成本）。
    /// 输出：交错 float 帧（ch0,f0, ch1,f0, ch0,f1 …）；位级与 DecodeFile 批式一致。
    /// </summary>
    public sealed class DsdStreamSource : IDisposable
    {
        readonly DsdDecoder.DsdMeta _meta;
        readonly int _rate;              /* 目标 PCM 采样率 */
        readonly int _channels;
        readonly int _block;             /* 每次文件读取字节数（全声道） */
        readonly long _dataEnd;
        readonly long _totalBits;
        readonly long _totalFrames;
        readonly int _maxQueueFrames;    /* 预取队列上限（帧） */
        readonly int _chunkFrames;       /* 每个预取块目标帧数 */
        readonly object _lock = new object();

        Stream _fs;
        byte[] _ibuf;
        DsdDecoder.Pipeline _pipe;
        readonly Queue<float[]> _queue = new Queue<float[]>();   /* 交错 float 块 */
        readonly Queue<float[]> _free = new Queue<float[]>();    /* 消费毕的块回收池（灭 LOH churn） */
        int _queuedFrames;

        /* 消费游标（当前块内偏移） */
        float[] _pending;
        int _pendFrames, _pendPos;
        long _consumedFrames;
        bool _eos;

        Thread _worker;
        volatile bool _stop;

        public DsdStreamSource(string path, DsdDecoder.DsdMeta meta, int targetRate, double lookaheadSeconds = 5.0)
        {
            _meta = meta;
            _rate = targetRate;
            _channels = Math.Max(1, meta.Channels);
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            _block = (meta.Container == "DSF" ? meta.BlockSize : 4096) * meta.Channels;
            _ibuf = new byte[_block];
            _dataEnd = DsdDecoder.AudioDataEnd(meta, _fs.Length);
            /* 从数据区起点解码（DSF 头 92B 不能当音频喂） */
            _fs.Position = Math.Min(Math.Max(0, meta.DataOffset), _fs.Length);
            _totalBits = meta.SampleCount > 0
                ? meta.SampleCount
                : Math.Max(1, (_dataEnd - meta.DataOffset) * 8 / Math.Max(1, meta.Channels));
            _pipe = DsdDecoder.MakePipeline(meta, targetRate, false);   /* 不分配整曲缓冲 */
            _totalFrames = Math.Max(1, _totalBits / Math.Max(1, _pipe.TotalDec));

            _chunkFrames = Math.Max(256, targetRate / 25);              /* ≈40ms 一块，摊薄队列开销 */
            _maxQueueFrames = lookaheadSeconds > 0
                ? Math.Max(_chunkFrames * 2, (int)(lookaheadSeconds * targetRate))
                : 0;

            if (lookaheadSeconds > 0)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "pa1-dsd-prefetch" };
                _worker.Start();
            }
        }

        public int Channels { get { return _channels; } }
        public int SampleRate { get { return _rate; } }
        public double TotalTime { get { return _totalFrames / (double)_rate; } }

        /// <summary>真流结束（已解到文件尾且队列/残留块耗尽）。预取瞬时未就绪时为 false，
        /// 播放侧据此区分「补零续播」与「曲目播完」。</summary>
        public bool EndOfStream
        {
            get
            {
                lock (_lock)
                {
                    return _consumedFrames >= _totalFrames
                        || (_eos && _queue.Count == 0 && (_pending == null || _pendPos >= _pendFrames));
                }
            }
        }

        /// <summary>已消费（已播放）位置。预取超前不影响该读数。</summary>
        public double CurrentTime
        {
            get { lock (_lock) return _consumedFrames / (double)_rate; }
            set { lock (_lock) SeekLocked(Math.Max(0, value)); }
        }

        /// <summary>读取至多 maxFrames 个交错帧到 buffer[0..]；返回实际帧数（EOF 时不足）。</summary>
        public int Read(float[] buffer, int maxFrames)
        {
            if (maxFrames <= 0) return 0;
            lock (_lock)
            {
                /* DSF 最后一块按 BlockSize 填充，填充字节不属于 SampleCount；
                   到声明样本数立即 EOF，不能把块填充/尾随数据播放成直流或噪声。 */
                long remaining = _totalFrames - _consumedFrames;
                if (remaining <= 0) return 0;
                if (maxFrames > remaining) maxFrames = (int)Math.Min(int.MaxValue, remaining);
                int done = 0;
                while (done < maxFrames)
                {
                    if (_pending == null || _pendPos >= _pendFrames)
                    {
                        if (_queue.Count > 0)
                        {
                            if (_pending != null) ReturnBuf(_pending);   /* 旧块消费毕 → 回池 */
                            _pending = _queue.Dequeue();
                            _pendFrames = _pending.Length / _channels;
                            _pendPos = 0;
                            _queuedFrames -= _pendFrames;
                            Monitor.PulseAll(_lock);      /* 唤醒可能在等容量的预取线程 */
                        }
                        else if (_eos) break;
                        else if (_worker == null || !_worker.IsAlive)
                        {
                            /* 同步模式（或预取线程已退出）：现场解一块 */
                            if (_pending != null) ReturnBuf(_pending);
                            _pending = DecodeChunkLocked();
                            if (_pending == null) { _eos = true; break; }
                            _pendFrames = _pending.Length / _channels;
                            _pendPos = 0;
                        }
                        else
                        {
                            /* 预取线程尚未就绪 → 最多等 5ms 一块；仍无货则返回已有部分
                               （调用方淡出补零凑满），绝不在音频线程上长阻塞（曾是爆音来源） */
                            Monitor.Wait(_lock, 5);
                            if (_queue.Count == 0 && (_pending == null || _pendPos >= _pendFrames)) break;
                            continue;
                        }
                    }
                    int take = Math.Min(maxFrames - done, _pendFrames - _pendPos);
                    Array.Copy(_pending, _pendPos * _channels, buffer, done * _channels, take * _channels);
                    done += take;
                    _pendPos += take;
                }
                _consumedFrames += done;
                return done;
            }
        }

        void WorkerLoop()
        {
            try
            {
                while (!_stop)
                {
                    lock (_lock)
                    {
                        while (!_stop && _queuedFrames >= _maxQueueFrames)
                            Monitor.Wait(_lock);
                        if (_stop) break;

                        var chunk = DecodeChunkLocked();
                        if (chunk == null)
                        {
                            _eos = true;
                            Monitor.PulseAll(_lock);
                            /* 等 Seek（清 _eos）或停止 */
                            while (!_stop && _eos) Monitor.Wait(_lock, 200);
                            continue;
                        }
                        _queue.Enqueue(chunk);
                        _queuedFrames += chunk.Length / _channels;
                        Monitor.PulseAll(_lock);
                    }
                }
            }
            catch { /* 流已释放等异常一律吞掉，播放侧以 EOF 收场 */ }
        }

        /// <summary>解出下一个 ~40ms 交错块；文件结束返回 null。须持 _lock。
        /// 合并缓冲从回收池取（352.8k 一块 ≈113KB 即 LOH——每 40ms new 一个会把 Gen2 压垮）。</summary>
        float[] DecodeChunkLocked()
        {
            int cap = _chunkFrames * _channels;
            float[] merged = TakeBuf(cap);
            int frames = 0;
            while (frames < _chunkFrames)
            {
                if (_fs == null || _fs.Position >= _dataEnd) break;
                int len = (int)Math.Min(_block, _dataEnd - _fs.Position);
                int got = 0;
                while (got < len)
                {
                    int r = _fs.Read(_ibuf, got, len - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got <= 0) break;
                var planes = DsdDecoder.FeedBlock(_pipe, _ibuf, got, _meta);
                if (planes == null || planes.Length == 0 || planes[0].Length == 0) continue;
                int n = planes[0].Length, ch = _channels;
                int need = (frames + n) * ch;
                if (merged.Length < need)                     /* 尾块跨界：罕见扩容路径 */
                {
                    var bigger = new float[need];
                    Array.Copy(merged, 0, bigger, 0, frames * ch);
                    ReturnBuf(merged);
                    merged = bigger;
                }
                /* 直接交错进合并缓冲（省掉旧 List<float[]> + 二次拼接拷贝） */
                for (int f = 0; f < n; f++)
                {
                    int o = (frames + f) * ch;
                    for (int c = 0; c < ch; c++) merged[o + c] = planes[c][f];
                }
                frames += n;
            }
            if (frames == 0) { ReturnBuf(merged); return null; }
            int exact = frames * _channels;
            if (merged.Length != exact)                        /* 收尾裁到精确长度（队列按 Length 推帧数） */
            {
                var t = TakeBuf(exact);
                Array.Copy(merged, 0, t, 0, exact);
                ReturnBuf(merged);
                merged = t;
            }
            return merged;
        }

        /// <summary>取精确 n 个采样的缓冲（回收池精确命中优先，否则新建）。须持 _lock。</summary>
        float[] TakeBuf(int n)
        {
            int cnt = _free.Count;
            for (int i = 0; i < cnt; i++)
            {
                var b = _free.Dequeue();
                if (b.Length == n) return b;
                _free.Enqueue(b);
            }
            return new float[n];
        }

        /// <summary>归还消费毕的缓冲（池上限 8 个防驻留过多）。须持 _lock。</summary>
        void ReturnBuf(float[] b)
        {
            if (b != null && _free.Count < 8) _free.Enqueue(b);
        }

        /// <summary>粗定位：按比例落到文件块边界 + 重建管线（DSD 位流无索引，接点处可有轻微不连续）。</summary>
        void SeekLocked(double sec)
        {
            double ratio = Math.Max(0, Math.Min(1, sec * _rate / (double)_totalFrames));
            long bytePos = _meta.DataOffset + (long)(ratio * (_dataEnd - _meta.DataOffset));
            if (_meta.Container == "DSF")
            {
                long bs = (long)_meta.BlockSize * _meta.Channels;
                bytePos = _meta.DataOffset + (bytePos - _meta.DataOffset) / bs * bs;
            }
            if (_fs != null) _fs.Position = bytePos;
            _consumedFrames = (long)(ratio * _totalFrames);
            _pipe = DsdDecoder.MakePipeline(_meta, _rate, false);
            while (_queue.Count > 0) ReturnBuf(_queue.Dequeue());   /* 旧队列块回池 */
            if (_pending != null) { ReturnBuf(_pending); _pending = null; }
            _queuedFrames = 0;
            _pendFrames = 0; _pendPos = 0;
            _eos = false;
            Monitor.PulseAll(_lock);
        }

        public void Dispose()
        {
            _stop = true;
            try { lock (_lock) Monitor.PulseAll(_lock); } catch { }
            try { if (_worker != null) _worker.Join(500); } catch { }
            try { if (_fs != null) _fs.Dispose(); } catch { }
            _fs = null;
        }
    }
}
