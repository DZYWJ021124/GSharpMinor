using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pa1.Core;

namespace Pa1.App
{
    /// <summary>
    /// 输出设备服务：MMDevice 枚举 + 自动优选（Moonriver/Moondrop）+ WASAPI 独占。
    /// </summary>
    public sealed class DeviceService : IDisposable
    {
        readonly MMDeviceEnumerator _enum = new MMDeviceEnumerator();
        public sealed class Dev
        {
            public string Id;
            public string Label;
            public override string ToString() { return Label; }
        }

        public List<Dev> Devices { get; } = new List<Dev>();
        public Dev Selected;
        public bool ExclusiveOn;

        public event Action Changed;

        public void Refresh(bool preferMoonriver = true)
        {
            string prevId = Selected != null ? Selected.Id : null;
            Devices.Clear();
            foreach (var d in _enum.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                Devices.Add(new Dev { Id = d.ID, Label = Pretty(d.FriendlyName) });
                d.Dispose();
            }
            if (Devices.Count == 0)
            {
                Devices.Add(new Dev { Id = "", Label = "System Default" });
            }
            /* 自动优选：Moonriver / Moondrop */
            Selected = Devices.FirstOrDefault(d => d.Label.IndexOf("moonriver", StringComparison.OrdinalIgnoreCase) >= 0
                                                || d.Label.IndexOf("moondrop", StringComparison.OrdinalIgnoreCase) >= 0)
                    ?? Devices.FirstOrDefault(d => d.Id == prevId)
                    ?? Devices[0];
            Changed?.Invoke();
        }

        static string Pretty(string l)
        {
            if (string.IsNullOrEmpty(l)) return "OUT DEV";
            /* 去 USB VID:PID 后缀："耳机 (MOONRIVER 3) (35d8:43da)" → "MOONRIVER 3" */
            var s = System.Text.RegularExpressions.Regex.Replace(l, @"\s*\(([0-9a-f]{4}:[0-9a-f]{4})\)\s*$", "");
            var m = System.Text.RegularExpressions.Regex.Match(s, @"^(.*?)\s*\((.*)\)\s*$");
            if (m.Success)
            {
                var pre = m.Groups[1].Value.Trim();
                var paren = m.Groups[2].Value.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(paren, "usb audio|high definition|realtek|nvidia|audio device", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return pre.Length > 0 ? pre : paren;
                if (System.Text.RegularExpressions.Regex.IsMatch(paren, "moonriver|moondrop|usb dac", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return paren;
            }
            return s.Trim();
        }

        public MMDevice GetDevice()
        {
            if (Selected == null || string.IsNullOrEmpty(Selected.Id)) return null;
            try { return _enum.GetDevice(Selected.Id); } catch { return null; }
        }

        int _mixRate = 48000, _mixBits = 16;
        string _mixDevId;

        /// <summary>缓存的设备 MixFormat（每帧调用安全；换设备时自动刷新）。</summary>
        public void GetMixFormat(out int rate, out int bits)
        {
            string id = Selected != null ? Selected.Id : "";
            if (id != _mixDevId)
            {
                _mixDevId = id;
                _mixRate = 48000; _mixBits = 16;
                var d = GetDevice();
                if (d != null)
                {
                    try
                    {
                        using (var ac = d.AudioClient)
                        {
                            _mixRate = ac.MixFormat.SampleRate;
                            _mixBits = ac.MixFormat.BitsPerSample;
                        }
                    }
                    catch { }
                }
            }
            rate = _mixRate; bits = _mixBits;
        }

        public void Dispose()
        {
            try { _enum.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// 播放引擎：NAudio WasapiOut（共享/独占）+ 多格式解码（DSF/DFF 走 Pa1.Core DsdDecoder，
    /// 其余走 MediaFoundation/AudioFileReader）+ FFT 分析采样。
    /// </summary>
    public sealed class AudioEngine : IDisposable
    {
        public readonly DeviceService DevSvc = new DeviceService();

        WasapiOut _out;
        IWaveProvider _src;
        ISampleProvider _tap;
        GainProvider _gain;
        float _volume = 0.8f;
        bool _paused;               /* 暂停=停 client：恢复时重建输出链续播 */
        volatile bool _stopByUser = true;   /* 停止由 Pause/Stop/重建引发：PlaybackStopped 不算自然播完 */

        /* 分析 */
        public readonly int FftSize = AnalyzerMath.FftSize;
        public float[] FreqDb;
        readonly double[] _re, _im, _win;
        readonly float[] _ring;
        readonly float[] _snap;      /* 预分配 FFT 输入，避免每帧 64KB 分配 */
        int _ringPos;
        readonly object _ringLock = new object();

        /* 样本节拍器：WasapiOut 一次拉 150~250ms 音频（事件驱动按整缓冲填充），
           突发入环会让 FFT 窗口阶梯跳变 → 频谱「一顿一顿」。突发样本先进待发队列，
           AnalyzeLoop 按墙钟速率匀速放入环缓 → 频谱逐帧连续推进。 */
        float[] _pace;             /* 待发环（突发样本暂存，满则丢最旧） */
        int _paceHead, _paceLen;

        Thread _anThread;
        volatile bool _anStop;
        public float Rms;
        public bool Clipped;
        public double CalOffset = AnalyzerMath.DbWindowComp;

        /* 状态 */
        public string FilePath;
        public TrackMeta Meta;
        public OutputPlan Plan;
        public string Error;
        /// <summary>DSD 输出模式："dop"（默认，安全载波内 DoP 直通）| "native"（Native DSD 位流）| "pcm"（软件解码）。</summary>
        public string DsdMode = "dop";
        public event Action StateChanged;
        /// <summary>曲目自然播完（源 EOF 且输出排空）；暂停/手动停止/重载不触发。</summary>
        public event Action TrackEnded;

        public bool IsPlaying { get { return _out != null && _out.PlaybackState == PlaybackState.Playing; } }
        public bool IsLoaded { get { return _src != null; } }

        /* 定位源：_src 是完整播放链（SampleToWaveProvider 包装后无法回转到源流），
           故单独持有可定位包装（PCM 的 PcmSeekWrap / DSD 系流）。 */
        PcmSeekWrap _posStream;
        DsdStreamWaveStream _posDsd;
        DopStream _posDop;

        /* 长度校正：容器头部/估算时长与真实音频长度常不符（错误头、截断数据等）。
           _lenSeen = 播放中见过的最大位置（实际超出估算时 Length 随实际增长）；
           _lenTrue = 后台解码到 EOF 的真值或自然播完的定格值（>0 时优先）。 */
        double _lenSeen, _lenTrue;

        /// <summary>FFT 输入采样率（DoP 直通时显示支路是 44.1k 解码，不等于载波率）。</summary>
        public int FftRate;

        public double Position
        {
            get
            {
                try
                {
                    double v = 0;
                    if (_posDop != null) v = _posDop.CurrentTime;
                    else if (_posDsd != null) v = _posDsd.CurrentTime;
                    else if (_posStream != null) v = _posStream.CurrentTime.TotalSeconds;
                    if (v > _lenSeen) _lenSeen = v;
                    return v;
                }
                catch { }
                return 0;
            }
            set
            {
                try
                {
                    double v = Math.Max(0, value);
                    ResetPace();
                    if (_posDop != null) { _posDop.CurrentTime = v; return; }
                    if (_posDsd != null) { _posDsd.CurrentTime = v; return; }
                    if (_posStream != null) _posStream.CurrentTime = TimeSpan.FromSeconds(v);
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "pa1-native-crash.log"), DateTime.Now + " [seek] " + v.ToString("0.00") + Environment.NewLine); } catch { }
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "pa1-native-crash.log"), DateTime.Now + " [seek-fail] " + ex.Message + Environment.NewLine); } catch { }
                }
            }
        }
        public double Length
        {
            get
            {
                double est = 0;
                try
                {
                    if (_posDop != null) est = _posDop.TotalTime;
                    else if (_posDsd != null) est = _posDsd.TotalTime;
                    else if (_posStream != null) est = _posStream.TotalTime.TotalSeconds;
                }
                catch { }
                /* 真值/播完定格优先；播放中实际位置超过估算（元数据偏短）时随实际增长 */
                if (_lenTrue > 0) return _lenTrue;
                return est > _lenSeen ? est : _lenSeen;
            }
        }

        public AudioEngine()
        {
            FreqDb = new float[FftSize / 2];
            for (int i = 0; i < FreqDb.Length; i++) FreqDb[i] = -200;
            _re = new double[FftSize]; _im = new double[FftSize];
            _win = AnalyzerMath.BlackmanWindow(FftSize);
            _ring = new float[FftSize * 2];
            _snap = new float[FftSize];
            DevSvc.Refresh();
            /* 分析走后台线程：FFT+dB 不占 UI 线程（UI 只读 FreqDb/Rms/Clipped），帧率与分析解耦 */
            _anThread = new Thread(AnalyzeLoop) { IsBackground = true, Name = "pa1-analyze" };
            _anThread.Start();
        }

        void AnalyzeLoop()
        {
            long last = DateTime.UtcNow.Ticks;
            while (!_anStop)
            {
                try
                {
                    long now = DateTime.UtcNow.Ticks;
                    double dt = (now - last) / 1e7;
                    last = now;
                    if (dt > 0.25) dt = 0.25;      /* 挂起/断点恢复不追赶巨量 */
                    DrainPace(dt);                 /* 按墙钟速率把突发样本匀速放进环缓 */
                    Analyze();
                }
                catch { }
                Thread.Sleep(20);   /* 50Hz 分析；节拍器吸收 WASAPI 的 150ms 突发 */
            }
        }

        /// <summary>切曲/Seek/停止：丢弃待发样本（避免旧音频尾巴混进新位置频谱）。</summary>
        void ResetPace()
        {
            lock (_ringLock) { _paceHead = 0; _paceLen = 0; }
        }

        /// <summary>按 dt 秒 × 采样率从待发环取样本入 FFT 环（速率=真实播放速率）。</summary>
        void DrainPace(double dt)
        {
            int rate = FftRate > 0 ? FftRate : 48000;
            int want = (int)(dt * rate + 0.5);
            if (want <= 0) return;
            lock (_ringLock)
            {
                if (_paceLen == 0 || _pace == null) return;
                if (want > _paceLen) want = _paceLen;
                for (int i = 0; i < want; i++)
                {
                    _ring[_ringPos] = _pace[_paceHead];
                    _ringPos = (_ringPos + 1) % _ring.Length;
                    _paceHead = (_paceHead + 1) % _pace.Length;
                }
                _paceLen -= want;
            }
        }

        public bool LoadFile(string path)
        {
            Stop();
            Error = null;
            DisposeSources();
            ResetPace();
            _lenSeen = 0; _lenTrue = 0;
            try
            {
                var meta = TagReader.Read(path);
                if (meta.Spec == null) { Error = "UNSUPPORTED FORMAT"; StateChanged?.Invoke(); return false; }
                if (meta.Spec.Compressed) { Error = "DST COMPRESSED DSD NOT SUPPORTED"; StateChanged?.Invoke(); return false; }

                int devRate, devBits;
                DevSvc.GetMixFormat(out devRate, out devBits);
                /* 独占模式绕开共享混音器 → 不按混音率降档（hwRate=0 = 不限，OutputPlanner 语义） */
                Plan = OutputPlanner.Plan(meta.Spec, DevSvc.ExclusiveOn ? 0 : devRate, devBits);

                if (meta.Spec.Dsd)
                {
                    var dsf = new DsdDecoder.DsdMeta
                    {
                        Container = meta.Spec.Fmt,
                        Rate = meta.Spec.Rate,
                        Channels = meta.Spec.Channels,
                        SampleCount = meta.Spec.SampleCount,
                        BlockSize = meta.Spec.BlockSize,
                        DataOffset = meta.Spec.DataOffset,
                        DataBytes = meta.Spec.DataBytes,
                        Id3Offset = meta.Spec.Id3Offset,
                        LsbFirst = meta.Spec.Fmt == "DSF",
                    };

                    /* ① Native DSD 位流直通（显式选中且独占）：原始位流按 DSD_U32_LE/U16_LE
                       直排进 DAC 硬解（无 DoP 标记、不转 PCM）。Windows 没有 Microsoft 定义的
                       Native DSD 通道（厂商 Thesycon/XMOS 系驱动走 ASIO 私有协议），此处按厂商
                       DSD 子格式约定探测；设备/驱动不接受则明示原因并回退，绝不硬发未知格式
                       （位流被当 PCM 解读会出满幅刺耳噪声）。 */
                    if (_src == null && DevSvc.ExclusiveOn && DsdMode == "native")
                    {
                        int nRate = DsdNativePacker.CarrierRateU32(meta.Spec.Rate), nBits = 32;
                        var nfmt = new DsdWaveFormat(nRate, 32, meta.Spec.Channels, GuidDsdU32Le);
                        if (!NativeFormatSupported(nfmt))
                        {
                            nRate = DsdNativePacker.CarrierRateU16(meta.Spec.Rate); nBits = 16;
                            nfmt = new DsdWaveFormat(nRate, 16, meta.Spec.Channels, GuidDsdU16Le);
                        }
                        if (NativeFormatSupported(nfmt))
                        {
                            Plan = OutputPlanner.PlanDsdNative(meta.Spec.Rate, nBits);
                            _posDop = new DopStream(path, dsf, nRate, this,
                                nBits == 32 ? WireMode.Native32 : WireMode.Native16);
                            _src = _posDop;
                            FftRate = 44100;    /* 显示支路 44.1k 解码（位流不进 FFT） */
                        }
                        else
                        {
                            Error = "NATIVE DSD UNSUPPORTED · 需厂商 ASIO 驱动";
                        }
                    }

                    /* ② DoP 直通（独占）：位流原样进 DAC 硬解、不降采样；混音器会破坏标记，故共享模式不启用。
                       不降档、全速率直发（DSD256 → 705.6 kHz 载波）：界面声称支持高载波却把 DoP 当 PCM
                       解读时会出噪声，须固件/驱动侧解决——是否真锁定由实听确认（DAC 指示灯）。 */
                    if (_src == null && DevSvc.ExclusiveOn && DsdMode != "pcm")
                    {
                        int dopRate = DopPacker.CarrierRate(meta.Spec.Rate);
                        if (DopSupported(dopRate, meta.Spec.Channels))
                        {
                            Plan = OutputPlanner.PlanDop(meta.Spec.Rate);
                            _posDop = new DopStream(path, dsf, dopRate, this);
                            _src = _posDop;
                            FftRate = 44100;    /* 显示支路 44.1k 解码（载波不进 FFT） */
                        }
                    }

                    /* ③ 回退：DSD 流式解码 → PCM（共享模式 / 位流路不被设备支持） */
                    if (_src == null)
                    {
                        /* 显式选了 Native 却不可用时把原因亮给读数（已有更具体的 UNSUPPORTED 文案时不覆盖） */
                        if (Error == null && DsdMode == "native" && !DevSvc.ExclusiveOn)
                            Error = "NATIVE DSD 需独占模式 · 已回退 PCM";
                        _posDsd = new DsdStreamWaveStream(path, dsf, Plan.Rate, Plan.Bits, this);
                        _src = _posDsd;
                        FftRate = Plan.Rate;
                    }
                }
                else
                {
                    PcmSeekWrap chain;
                    _src = OpenPcm(path, Plan.Rate, meta.Spec.Channels, out chain);
                    _posStream = chain;
                    FftRate = Plan.Rate;
                }

                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "pa1-native-crash.log"),
                        DateTime.Now + " [plan] " + Plan.Label
                        + (Error != null ? "  err=" + Error : "")
                        + "  " + Path.GetFileName(path) + Environment.NewLine);
                }
                catch { }

                Meta = meta;
                FilePath = path;
                /* PCM：后台解码到 EOF 取真实时长（DSD 系头部样本数精确，无需） */
                if (!meta.Spec.Dsd) RefineLengthAsync(path);
                StateChanged?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _src = null;
                _posStream = null;
                _posDsd = null;
                _posDop = null;
                StateChanged?.Invoke();
                return false;
            }
        }

        /// <summary>后台精确时长探测：独立解码实例读到 EOF，以解码器终点为真实长度。
        /// 容器头部时长（错误头/截断数据/估算）与真实音频长度常不符——显示的总时长
        /// 若只信头部，会出现「歌已放完但时间没走完」或「时间走完歌还在响」。</summary>
        void RefineLengthAsync(string path)
        {
            double est = Length;
            System.Threading.Tasks.Task.Run(() =>
            {
                double trueLen = 0;
                try
                {
                    IWaveProvider raw;
                    try { raw = new MediaFoundationReader(path); }
                    catch { raw = new AudioFileReader(path); }
                    var ws = (WaveStream)raw;
                    var buf = new byte[1 << 16];
                    while (ws.Read(buf, 0, buf.Length) > 0) { }
                    trueLen = ws.CurrentTime.TotalSeconds;
                    try { ws.Dispose(); } catch { }
                }
                catch { }
                if (trueLen <= 0) return;
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "pa1-native-crash.log"),
                        DateTime.Now + " [len] " + Path.GetFileName(path) +
                        " est=" + est.ToString("0.00") + " true=" + trueLen.ToString("0.00") + Environment.NewLine);
                }
                catch { }
                if (path == FilePath)
                {
                    _lenTrue = trueLen;
                    /* 同步真值到解码器：拦截 seek 后 MF 超发真实结尾的尾巴样本 */
                    var ps = _posStream;
                    if (ps != null) ps.SetTrueLength(trueLen);
                }
            });
        }

        /// <summary>释放上一曲的解码源（DSD 系流持有文件句柄，PCM 源持 MediaFoundation 句柄）。</summary>
        void DisposeSources()
        {
            try { if (_posDop != null) _posDop.Dispose(); } catch { }
            try { if (_posDsd != null) _posDsd.Dispose(); } catch { }
            try { if (_posStream != null) _posStream.Dispose(); } catch { }
            _src = null;
            _posStream = null;
            _posDsd = null;
            _posDop = null;
            _tap = null;
        }

        IWaveProvider OpenPcm(string path, int targetRate, int channels, out PcmSeekWrap posWrap)
        {
            /* FLAC 使用 NAudio 原生读取器：按 PCM 帧定位，不走 Media Foundation。
               MF 在部分 VBR FLAC 后段 seek 后会超发十几秒尾部样本；仅钳制 Position
               只能遮住显示误差，实际结束时间仍会延后。其它格式保留 MfPcmReader。 */
            IWaveProvider raw;
            try
            {
                raw = string.Equals(Path.GetExtension(path), ".flac", StringComparison.OrdinalIgnoreCase)
                    ? (IWaveProvider)new NAudio.Flac.FlacReader(path)
                    : new MfPcmReader(path);
            }
            catch { raw = new AudioFileReader(path); }

            var sp = raw as ISampleProvider;
            if (sp == null)
            {
                var w = raw as WaveStream;
                if (w != null) sp = w.ToSampleProvider();
            }
            if (sp == null) throw new InvalidOperationException("DECODE FAILED");

            /* 定位包装：Read/Seek 互斥（UI 线程 seek 与 WASAPI 渲染线程读解码器竞态会读错位），
               Seek 时重建重采样器（丢弃旧位置残留样本） */
            posWrap = new PcmSeekWrap((WaveStream)raw, sp, targetRate);
            /* 输出为 32-bit float，WasapiOut 负责最终转换 */
            _tap = new TapProvider(posWrap, this);
            return new SampleToWaveProvider(_tap);
        }

        public void Play()
        {
            if (_src == null) return;
            _paused = false;
            /* 每次播放都重建输出 client：NAudio 的 Pause() 只停喂数据、不停 audioClient，
               WASAPI 会把末缓冲循环重放（暂停漏声），故暂停走真停止、恢复走重建续播 */
            Stop();
            try
            {
                var dev = DevSvc.GetDevice();
                var share = DevSvc.ExclusiveOn ? AudioClientShareMode.Exclusive : AudioClientShareMode.Shared;
                /* 事件驱动（NAudio 默认路径）+ 缓冲加大：sleep 轮询在调度抖动下易欠载爆音；
                   高载波（DoP 705.6k）更吃缓冲——独占 300ms / 共享 400ms */
                int lat = share == AudioClientShareMode.Exclusive ? 300 : 400;
                _out = dev != null
                    ? new WasapiOut(dev, share, true, lat)
                    : new WasapiOut(share, true, lat);
                /* 每次起播都发 DoP 静音前导：新建 output client 后 DAC 需重新锁标记，锁定前的
                   真实位流会被当 PCM 解读 → 切 DSD 爆音；PCM 解码路同样重新淡入 */
                if (_posDop != null) _posDop.ArmLeadIn();
                if (_posDsd != null) _posDsd.ArmFadeIn();
                _out.Init(BuildOutput(share));
                /* 捕获实例比对：重载/暂停/停止后旧 client 的迟到事件不得误判为播完 */
                var o = _out;
                o.PlaybackStopped += (s, e) => OnPlaybackStopped(o);
                _stopByUser = false;
                _out.Play();
                StateChanged?.Invoke();
            }
            catch (Exception ex)
            {
                Error = DevSvc.ExclusiveOn ? "EXCLUSIVE FAILED: " + ex.Message : ex.Message;
                /* 独占失败自动回退共享：重载当前曲目（DoP 源须换成 PCM 解码路——
                   DoP 标记经共享混音器会被破坏），从失败处进度续播 */
                if (DevSvc.ExclusiveOn)
                {
                    DevSvc.ExclusiveOn = false;
                    string path = FilePath;
                    double pos = Position;
                    if (!string.IsNullOrEmpty(path) && LoadFile(path))
                    {
                        Position = Math.Min(pos, Length > 0 ? Length - 0.1 : 0);
                        Play();
                    }
                }
                StateChanged?.Invoke();
            }
        }

        /// <summary>独占模式下设备是否收该载波率的 24-bit PCM（DoP 帧率 = DSD 比特率/16）。</summary>
        bool DopSupported(int rate, int channels)
        {
            var dev = DevSvc.GetDevice();
            if (dev == null) return false;
            try
            {
                using (var ac = dev.AudioClient)
                {
                    return ac.IsFormatSupported(AudioClientShareMode.Exclusive, new WaveFormat(rate, 24, channels));
                }
            }
            catch { return false; }
        }

        /* ── Native DSD 线格式（WAVEFORMATEXTENSIBLE + DSD 子格式） ──
              GUID 采用厂商 DSD 驱动/ksmedia 扩展的 DSD_U32_LE / DSD_U16_LE 约定。
              Windows 没有 Microsoft 定义的 Native DSD 传输标准（Thesycon/XMOS 系厂商驱动
              的 Native DSD 走 ASIO 私有协议），故此处只是「按约定探测」：驱动不认就回退。 */
        static readonly Guid GuidDsdU32Le = new Guid("00000013-0000-0010-8000-00aa00389b71");
        static readonly Guid GuidDsdU16Le = new Guid("00000012-0000-0010-8000-00aa00389b71");

        /// <summary>WAVEFORMATEXTENSIBLE 自定义 SubFormat（NAudio 的 WaveFormatExtensible 不开放写 SubFormat，按同布局自建）。</summary>
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, CharSet = System.Runtime.InteropServices.CharSet.Ansi, Pack = 2)]
        sealed class DsdWaveFormat : WaveFormat
        {
            short wValidBitsPerSample;
            int dwChannelMask;
            Guid subFormat;
            public DsdWaveFormat(int rate, int bits, int channels, Guid subFormat) : base(rate, bits, channels)
            {
                waveFormatTag = WaveFormatEncoding.Extensible;
                extraSize = 22;
                wValidBitsPerSample = (short)bits;
                dwChannelMask = channels >= 2 ? 0x3 : 0x1;
                this.subFormat = subFormat;
            }
        }

        /// <summary>独占模式下设备是否接受该 DSD 子格式（Native DSD 能否直通的唯一判据）。</summary>
        bool NativeFormatSupported(WaveFormat fmt)
        {
            var dev = DevSvc.GetDevice();
            if (dev == null) return false;
            try
            {
                using (var ac = dev.AudioClient)
                {
                    return ac.IsFormatSupported(AudioClientShareMode.Exclusive, fmt);
                }
            }
            catch { return false; }
        }

        /// <summary>按共享/独占组装输出链：
        /// 共享 → 32-bit float（WASAPI AutoConvertPcm 自动转换）；
        /// 独占 → 24-bit PCM（多数 DAC 独占不收 float）。DSD 链已是 PCM，直接透传。</summary>
        IWaveProvider BuildOutput(AudioClientShareMode share)
        {
            if (_tap != null)
            {
                /* 音量做软件增益：NAudio WasapiOut.Volume 写的是系统端点音量（AudioEndpointVolume），
                   ① 会劫持 Windows 系统音量；② 部分 USB DAC 返回 E_NOTIMPL → 「尚未实现」异常 */
                _gain = new GainProvider(_tap) { Gain = _volume };
                return share == AudioClientShareMode.Exclusive
                    ? (IWaveProvider)new SampleToPcm24Provider(_gain)
                    : new SampleToWaveProvider(_gain);
            }
            return _src;   /* DSD 系流自管增益（DoP 直通不加增益——bit-perfect，音量用设备旋钮） */
        }

        public void Pause()
        {
            /* 真静音：停掉 audioClient（NAudio Pause 仅停喂数据 → 末缓冲循环重放有残留声）。
               源位置不动 → 恢复播放时重建输出链即从暂停处续播。 */
            if (_out == null) return;
            _stopByUser = true;
            try { _out.Stop(); } catch { }
            try { _out.Dispose(); } catch { }
            _out = null;
            _paused = true;
            StateChanged?.Invoke();
        }

        public void Toggle() { if (IsPlaying) Pause(); else Play(); }

        public void Stop()
        {
            _paused = false;
            _stopByUser = true;
            if (_out != null)
            {
                try { _out.Stop(); _out.Dispose(); } catch { }
                _out = null;
            }
            ResetPace();
            StateChanged?.Invoke();
        }

        /// <summary>输出 client 停止回调：区分「用户停止/暂停/重载」与「曲目自然播完」。</summary>
        void OnPlaybackStopped(WasapiOut o)
        {
            StateChanged?.Invoke();
            if (_stopByUser || !ReferenceEquals(o, _out)) return;   /* 非自然播完 */
            _stopByUser = true;                                     /* 消费一次，防重复推进 */
            /* 自然播完：解码器终点=真实长度（后台探测未出结果时的兜底定格） */
            try
            {
                double p = Position, l = Length;
                if (_lenTrue <= 0 && p > 0) _lenTrue = p;
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "pa1-native-crash.log"),
                    DateTime.Now + " [end] pos=" + p.ToString("0.00") + " len=" + l.ToString("0.00") + Environment.NewLine);
            }
            catch { }
            var h = TrackEnded;
            if (h != null) h();
        }

        public void SetVolume(float v)
        {
            _volume = Math.Max(0, Math.Min(1, v));
            if (_gain != null) _gain.Gain = _volume;    /* 软件增益，不动系统音量 */
        }

        public void SetExclusive(bool on)
        {
            if (DevSvc.ExclusiveOn == on) { StateChanged?.Invoke(); return; }
            DevSvc.ExclusiveOn = on;
            /* 独占/共享的输出格式与降档策略不同 → 带进度重载当前曲目 */
            string path = FilePath;
            double pos = Position;
            bool wasPlaying = IsPlaying;
            if (!string.IsNullOrEmpty(path))
            {
                if (LoadFile(path))
                {
                    Position = Math.Min(pos, Length > 0 ? Length - 0.1 : 0);
                    if (wasPlaying) Play();
                }
            }
            StateChanged?.Invoke();
        }

        /// <summary>切换 DSD 输出模式（dop/native/pcm）：位流链路与格式完全不同 → 带进度重载当前曲目。</summary>
        public void SetDsdMode(string mode)
        {
            if (mode != "dop" && mode != "native" && mode != "pcm") mode = "dop";
            if (DsdMode == mode) { StateChanged?.Invoke(); return; }
            DsdMode = mode;
            string path = FilePath;
            double pos = Position;
            bool wasPlaying = IsPlaying;
            if (!string.IsNullOrEmpty(path))
            {
                if (LoadFile(path))
                {
                    Position = Math.Min(pos, Length > 0 ? Length - 0.1 : 0);
                    if (wasPlaying) Play();
                }
            }
            StateChanged?.Invoke();
        }

        /// <summary>分析一帧：从环形缓冲取最新 FftSize 样本 → Blackman 窗 → FFT → dB。</summary>
        public void Analyze()
        {
            lock (_ringLock)
            {
                int n = Math.Min(FftSize, _ring.Length);
                int start = (_ringPos - n + _ring.Length) % _ring.Length;
                for (int i = 0; i < n; i++) _snap[i] = _ring[(start + i) % _ring.Length];
            }
            for (int i = 0; i < FftSize; i++)
            {
                _re[i] = _snap[i] * _win[i];
                _im[i] = 0;
            }
            Fft.Transform(_re, _im);
            double inv = 1.0 / FftSize;
            for (int k = 0; k < FreqDb.Length; k++)
            {
                double m = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * inv;
                FreqDb[k] = (float)(20 * Math.Log10(m + 1e-20));
            }
            /* RMS / clip */
            double sum = 0; bool clip = false;
            for (int i = 0; i < FftSize; i += 8)
            {
                double v = _snap[i];
                sum += v * v;
                if (Math.Abs(v) > 0.985) clip = true;
            }
            Rms = (float)Math.Sqrt(sum / (FftSize / 8.0));
            Clipped = clip;
        }

        internal void PushSamples(float[] buf, int count)
        {
            lock (_ringLock)
            {
                /* 突发入待发环（WasapiOut 一次回调可能带150~250ms 样本），由 DrainPace 匀速转出 */
                if (_pace == null) _pace = new float[1 << 18];   /* 256k 样本兜底 */
                int cap = _pace.Length;
                for (int i = 0; i < count; i++)
                {
                    _pace[(_paceHead + _paceLen) % cap] = buf[i];
                    if (_paceLen < cap) _paceLen++;
                    else _paceHead = (_paceHead + 1) % cap;      /* 满则丢最旧 */
                }
                /* 深度封顶 ~330ms：显示支路追进度攒出长队列时丢最旧——频谱始终贴近实时且平滑 */
                int maxPace = FftRate > 0 ? FftRate / 3 : 16000;
                if (maxPace < 4096) maxPace = 4096;
                if (_paceLen > maxPace)
                {
                    _paceHead = (_paceHead + (_paceLen - maxPace)) % cap;
                    _paceLen = maxPace;
                }
            }
        }

        public void Dispose()
        {
            _anStop = true;
            try { if (_anThread != null) _anThread.Join(300); } catch { }
            Stop();
            DisposeSources();
            DevSvc.Dispose();
        }

        /* ── 数值整形：软限幅 + 四舍五入（截位会叠加量化失真，硬削波过冲会出刺耳噪声） ── */

        /// <summary>软限幅：|x|≤0.95 线性通过；过冲区 tanh 软拐，把削波失真压成温和谐波。</summary>
        internal static float SoftClip(float x)
        {
            const float T = 0.95f;
            float a = x < 0 ? -x : x;
            if (a <= T) return x;
            float over = (a - T) / (1f - T);
            float y = T + (1f - T) * (float)Math.Tanh(over);
            return x < 0 ? -y : y;
        }

        internal static int ToI24(float v)
        {
            float s = v * 8388607f;
            int i = (int)(s + (s >= 0 ? 0.5f : -0.5f));
            return i < -8388608 ? -8388608 : (i > 8388607 ? 8388607 : i);
        }

        internal static short ToI16(float v)
        {
            float s = v * 32767f;
            int i = (int)(s + (s >= 0 ? 0.5f : -0.5f));
            return (short)(i < -32768 ? -32768 : (i > 32767 ? 32767 : i));
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        struct FloatBits
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float F;
            [System.Runtime.InteropServices.FieldOffset(0)] public byte B0;
            [System.Runtime.InteropServices.FieldOffset(1)] public byte B1;
            [System.Runtime.InteropServices.FieldOffset(2)] public byte B2;
            [System.Runtime.InteropServices.FieldOffset(3)] public byte B3;
        }

        /* ── DSD 流式解码 → IWaveProvider（预解码 look-ahead：DsdStreamSource 后台填块，此处仅转码+分路 FFT） ── */
        sealed class DsdStreamWaveStream : IWaveProvider, IDisposable
        {
            readonly DsdStreamSource _src;
            readonly WaveFormat _fmt;
            readonly AudioEngine _eng;
            readonly int _channels;
            float[] _fbuf;          /* 交错 float 源块 */
            float[] _tapScratch;
            float[] _lastV;         /* 各声道最后输出值：全欠载时从它衰减补给（不跳变） */
            int _underruns;
            float _env;             /* 输出包络 0..1：起播/Seek/欠载恢复淡入，欠载尾巴淡出 */
            float _gainSm;          /* 平滑音量（瞬时跳变会咔哒） */

            public DsdStreamWaveStream(string path, DsdDecoder.DsdMeta meta, int rate, int bits, AudioEngine eng)
            {
                _eng = eng;
                _channels = Math.Max(1, meta.Channels);
                _fmt = new WaveFormat(rate, bits, _channels);
                /* 构造即预取：LoadFile 到按下播放之间队列已被填满（预加载） */
                _src = new DsdStreamSource(path, meta, rate);
                _fbuf = new float[rate / 25 * _channels + _channels];
                _tapScratch = new float[4096];
                _lastV = new float[_channels];
                _env = 0f;                               /* 起播 20ms 淡入防爆音 */
                _gainSm = eng != null ? eng._volume : 1f;
            }

            /// <summary>重建输出链后重新淡入（暂停恢复/独占切换）。</summary>
            public void ArmFadeIn() { _env = 0f; }

            public WaveFormat WaveFormat { get { return _fmt; } }

            public double CurrentTime
            {
                get { return _src.CurrentTime; }
                set { _src.CurrentTime = value; _env = 0f; }   /* Seek 后淡入：接点不连续防爆音 */
            }
            public double TotalTime { get { return _src.TotalTime; } }

            public int Read(byte[] buffer, int offset, int count)
            {
                int bps = _fmt.BitsPerSample / 8;
                int ch = _channels;
                int want = count / (bps * ch);
                if (want <= 0) return 0;
                if (_tapScratch.Length < want) _tapScratch = new float[want];
                int fneed = want * ch;
                if (_fbuf.Length < fneed) _fbuf = new float[fneed];

                int got = _src.Read(_fbuf, want);
                bool eos = _src.EndOfStream;

                if (got <= 0)
                {
                    /* 真 EOF 交还 NAudio（0 = 流结束）；瞬时欠载从各声道末值衰减补给——
                       音频→硬零的跳变正是「咔哒/爆音」，衰减到 0 后再静音则连续 */
                    if (eos) return 0;
                    NoteUnderrun();
                    FillPcm(buffer, offset, want, Math.Min(want, _fmt.SampleRate / 200));
                    _env = 0f;
                    return count;
                }

                /* 尾部淡出：欠载补零前 / 曲目播完前，把有效尾巴衰减到 0（消除 音频→静音 跳变） */
                int fadeOut = (got < want || eos) ? Math.Min(got, Math.Max(1, _fmt.SampleRate / 200)) : 0;
                if (got < want && !eos) NoteUnderrun();
                int fadeStart = got - fadeOut;

                float envStep = 1f / (0.02f * _fmt.SampleRate);    /* 20ms 淡入 */
                float gainStep = 1f / (0.005f * _fmt.SampleRate);  /* 5ms 音量平滑 */
                float targetG = _eng != null ? _eng._volume : 1f;

                int o = offset;
                FloatBits fb = new FloatBits();
                for (int f = 0; f < got; f++)
                {
                    if (_env < 1f) { _env += envStep; if (_env > 1f) _env = 1f; }
                    float d = targetG - _gainSm;
                    if (d > gainStep) _gainSm += gainStep; else if (d < -gainStep) _gainSm -= gainStep; else _gainSm = targetG;

                    float g = _env * _gainSm;
                    if (f >= fadeStart) g *= (got - f) / (float)(fadeOut + 1);   /* 尾部线性衰减到 0 */

                    float mono = 0;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = SoftClip(_fbuf[f * ch + c] * g);
                        mono += v;
                        _lastV[c] = v;
                        if (bps == 4)   /* float（显式布局重解释，零分配——(BitConverter.GetBytes 曾逐采样分配致 GC 爆音)） */
                        {
                            fb.F = v;
                            buffer[o++] = fb.B0; buffer[o++] = fb.B1; buffer[o++] = fb.B2; buffer[o++] = fb.B3;
                        }
                        else if (bps == 3)
                        {
                            int i24 = ToI24(v);
                            buffer[o++] = (byte)(i24 & 0xff); buffer[o++] = (byte)((i24 >> 8) & 0xff); buffer[o++] = (byte)((i24 >> 16) & 0xff);
                        }
                        else /* 16 */
                        {
                            short i16 = ToI16(v);
                            buffer[o++] = (byte)(i16 & 0xff); buffer[o++] = (byte)((i16 >> 8) & 0xff);
                        }
                    }
                    _tapScratch[f] = mono / ch;
                }
                if (_eng != null) _eng.PushSamples(_tapScratch, got);

                if (got < want)
                {
                    /* 预取欠载：余量从末值衰减后补零凑满整个缓冲再返回 count（短读会让驱动拼旧数据出电流声） */
                    FillPcm(buffer, o, want - got, Math.Min(want - got, _fmt.SampleRate / 200));
                    _env = 0f;
                    return count;
                }
                return o - offset;
            }

            /// <summary>写 frames 帧：前 k 帧从 _lastV 各声道线性衰减到 0，其余填 0（欠载补给，与上一缓冲末尾连续）。</summary>
            void FillPcm(byte[] buffer, int offset, int frames, int k)
            {
                int bps = _fmt.BitsPerSample / 8;
                int ch = _channels;
                if (k < 1) k = 1;
                int o = offset;
                FloatBits fb = new FloatBits();
                for (int f = 0; f < frames; f++)
                {
                    float scale = f < k ? (1f - f / (float)k) : 0f;
                    for (int c = 0; c < ch; c++)
                    {
                        float v = f < k ? _lastV[c] * scale : 0f;
                        if (f < k) _lastV[c] = v;
                        if (bps == 4)
                        {
                            fb.F = v;
                            buffer[o++] = fb.B0; buffer[o++] = fb.B1; buffer[o++] = fb.B2; buffer[o++] = fb.B3;
                        }
                        else if (bps == 3)
                        {
                            int i24 = ToI24(v);
                            buffer[o++] = (byte)(i24 & 0xff); buffer[o++] = (byte)((i24 >> 8) & 0xff); buffer[o++] = (byte)((i24 >> 16) & 0xff);
                        }
                        else
                        {
                            short i16 = ToI16(v);
                            buffer[o++] = (byte)(i16 & 0xff); buffer[o++] = (byte)((i16 >> 8) & 0xff);
                        }
                    }
                }
            }

            /// <summary>欠载计数落日志（限流：前 5 次 + 每 200 次），供真机排查杂音来源。</summary>
            void NoteUnderrun()
            {
                int n = System.Threading.Interlocked.Increment(ref _underruns);
                if (n <= 5 || n % 200 == 0)
                {
                    try
                    {
                        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pa1-native-crash.log"),
                            DateTime.Now + " [underrun] n=" + n + Environment.NewLine);
                    }
                    catch { }
                }
            }

            public void Dispose() { try { _src.Dispose(); } catch { } }
        }

        /// <summary>DSD 位流直通的线上封装：DoP 1.1（24-bit 帧 + 标记）或 Native DSD（U32/U16 裸位流）。</summary>
        enum WireMode { Dop, Native16, Native32 }

        /* ── DSD 位流直通：原始 DSD 位流 → DoP 帧 / Native DSD 帧（bit-perfect，零处理）。
              显示支路在后台线程 44.1k 解码喂 FFT（绝不占音频线程）；定位 = 文件偏移比例。 ── */
        sealed class DopStream : IWaveProvider, IDisposable
        {
            readonly Stream _fs;       /* 仅供预读线程顺序读——音频线程零磁盘 I/O（同步盘读曾是残余爆音来源） */
            readonly DsdDecoder.DsdMeta _meta;
            readonly WaveFormat _fmt;
            readonly AudioEngine _eng;
            readonly WireMode _wire;
            readonly int _slotBytes;   /* 每声道每帧字节数：DoP=3、Native16=2、Native32=4 */
            readonly int _bitsPerFrame;/* 每帧每声道承载的 DSD 位数：DoP/Native16=16、Native32=32 */
            readonly int _channels;
            readonly bool _lsbFirst;
            readonly int _block;
            readonly int _chunk;       /* 预读块：≥64KB 且为 _block 整倍数（保位流拆分对齐） */
            readonly long _dataEnd;
            readonly long _totalBits;
            readonly object _io = new object();
            byte[] _flat, _packed, _pending;
            int _pendPos, _pendBytes;
            long _bitsDone;          /* 已消费 DSD 位数 */
            bool _markerPhase;
            int _leadIn;             /* 起播静音前导帧数：DAC 锁 DoP 标记期间不出声（防切曲爆音） */

            /* 预读队列（SPSC）：预读线程投块、音频线程取块；缓冲池复用防 LOH 分配抖动 */
            sealed class RawBlk { public byte[] Buf; public int Len; }
            readonly object _qLock = new object();
            readonly Queue<RawBlk> _q = new Queue<RawBlk>();
            readonly Stack<byte[]> _pool = new Stack<byte[]>();
            int _qBytes;
            volatile bool _eos;      /* 预读线程已读到数据尾（队列清空后才是真 EOF） */
            long _consumed;          /* 音频线程已消费原始字节（显示目标 = DataOffset + 此值） */
            int _underruns;
            Thread _prefetchThread;
            long _readRestart;
            volatile int _readGen;
            const int MaxQBytes = 16 << 20;   /* ≈4s DoP 载波余量（705.6k×6B/s≈4.2MB/s） */

            /* 显示支路（后台线程）：独立文件句柄 + 独立管线，跟随播放偏移、允许滞后 */
            readonly Stream _dfs;
            byte[] _dibuf, _dflat;
            float[] _tapScratch;
            Thread _dispThread;
            volatile bool _stop;
            long _dispTarget;      /* 播放侧已消费到的文件偏移（Interlocked 读写） */
            long _dispRestart;     /* Seek 后重起点 */
            volatile int _seekGen;

            public DopStream(string path, DsdDecoder.DsdMeta meta, int carrierRate, AudioEngine eng, WireMode wire = WireMode.Dop)
            {
                _meta = meta;
                _eng = eng;
                _wire = wire;
                _slotBytes = wire == WireMode.Native32 ? 4 : wire == WireMode.Native16 ? 2 : 3;
                _bitsPerFrame = wire == WireMode.Native32 ? 32 : 16;
                _channels = Math.Max(1, meta.Channels);
                _lsbFirst = meta.LsbFirst;
                _fmt = wire == WireMode.Dop
                    ? new WaveFormat(carrierRate, 24, _channels)
                    : new DsdWaveFormat(carrierRate, _slotBytes * 8, _channels,
                        wire == WireMode.Native32 ? GuidDsdU32Le : GuidDsdU16Le);
                _block = (meta.Container == "DSF" ? meta.BlockSize : 4096) * _channels;
                _chunk = Math.Max(64 * 1024 / Math.Max(1, _block), 1) * _block;
                _flat = new byte[_chunk];
                int frameCap = _chunk / Math.Max(1, _channels * (_bitsPerFrame / 8));
                _packed = new byte[frameCap * _slotBytes * _channels + _slotBytes * _channels];
                _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                _dataEnd = DsdDecoder.AudioDataEnd(meta, _fs.Length);
                _totalBits = meta.SampleCount > 0
                    ? meta.SampleCount
                    : Math.Max(1, (_dataEnd - meta.DataOffset) * 8 / Math.Max(1, meta.Channels));
                _fs.Position = Math.Min(Math.Max(0, meta.DataOffset), _fs.Length);
                _readRestart = _fs.Position;

                _dfs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
                _dibuf = new byte[_block];
                _dflat = new byte[_block];
                _tapScratch = new float[4096];
                _dispTarget = _readRestart;
                _dispRestart = _readRestart;
                _prefetchThread = new Thread(PrefetchLoop) { IsBackground = true, Name = "pa1-dop-prefetch" };
                _prefetchThread.Start();
                _dispThread = new Thread(DispLoop) { IsBackground = true, Name = "pa1-dop-display" };
                _dispThread.Start();
                _leadIn = carrierRate / 10;    /* 100ms 前导：DAC 锁标记更宽裕（防切换爆音） */
            }

            /// <summary>重新武装起播静音前导（暂停恢复重建输出链后调用——DAC 需重新锁标记）。</summary>
            public void ArmLeadIn() { lock (_io) _leadIn = _fmt.SampleRate / 10; }

            public WaveFormat WaveFormat { get { return _fmt; } }

            public double CurrentTime
            {
                get { lock (_io) return _bitsDone / (double)_meta.Rate; }
                set { lock (_io) Seek(value); }
            }
            public double TotalTime { get { return _totalBits / (double)_meta.Rate; } }

            void Seek(double sec)
            {
                double ratio = Math.Max(0, Math.Min(1, sec * _meta.Rate / _totalBits));
                long bytePos = _meta.DataOffset + (long)(ratio * (_dataEnd - _meta.DataOffset));
                if (_meta.Container == "DSF")
                {
                    long bs = (long)_meta.BlockSize * _channels;
                    bytePos = _meta.DataOffset + (bytePos - _meta.DataOffset) / bs * bs;
                }
                _bitsDone = (long)(ratio * _totalBits);
                _pending = null; _pendPos = 0; _pendBytes = 0;
                _markerPhase = false;
                _consumed = bytePos - _meta.DataOffset;
                /* 清预读队列 + 锁内递增代数（读者入队时校验代数，防旧块混入）+ 重定位预读线程 */
                lock (_qLock)
                {
                    while (_q.Count > 0) _pool.Push(_q.Dequeue().Buf);
                    _qBytes = 0; _eos = false;
                    _readGen++;
                    Interlocked.Exchange(ref _readRestart, bytePos);
                    Monitor.PulseAll(_qLock);
                }
                Interlocked.Exchange(ref _dispRestart, bytePos);
                Interlocked.Exchange(ref _dispTarget, bytePos);
                _seekGen++;          /* 显示线程检测到代数变化 → 重建管线重定位 */
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                int frameBytes = _slotBytes * _channels;
                int want = count / frameBytes;
                if (want <= 0) return 0;
                lock (_io)
                {
                    int done = 0;
                    while (done < want)
                    {
                        /* 起播前导静音：DoP 用 0x69 位型+标记交替（DAC 无声期锁标记防爆音）；
                           Native 用 0x00（误按 PCM 解读时也是真静音） */
                        if (_leadIn > 0)
                        {
                            int lf = Math.Min(want - done, _leadIn);
                            if (_wire == WireMode.Dop)
                                DopPacker.PackSilence(buffer, offset + done * frameBytes, lf, _channels, ref _markerPhase);
                            else
                                DsdNativePacker.PackSilence(buffer, offset + done * frameBytes, lf, _channels, _slotBytes);
                            done += lf;
                            _leadIn -= lf;
                            continue;
                        }
                        /* SampleCount 之外是 DSF 块填充，再往后可能是 ID3 标签；
                           二者均不得进入 DAC。前导静音不计入曲目样本数。 */
                        if (_bitsDone >= _totalBits) break;
                        if (_pending == null || _pendPos >= _pendBytes)
                        {
                            if (!Fill()) break;
                        }
                        long framesLeft = (_totalBits - _bitsDone) / _bitsPerFrame;
                        if (framesLeft <= 0) { _bitsDone = _totalBits; break; }
                        int take = (int)Math.Min(Math.Min(want - done, (_pendBytes - _pendPos) / frameBytes), framesLeft);
                        Array.Copy(_pending, _pendPos, buffer, offset + done * frameBytes, take * frameBytes);
                        _pendPos += take * frameBytes;
                        done += take;
                        _bitsDone += (long)take * _bitsPerFrame;
                    }
                    if (done < want && !_eos && _bitsDone < _totalBits)
                    {
                        /* 瞬时欠载（预读队列暂空、未到 EOF）：补静音帧——真静音且保持 DAC 锁定 */
                        NoteUnderrun();
                        while (done < want)
                        {
                            if (_wire == WireMode.Dop)
                            {
                                byte marker = _markerPhase ? DopPacker.MarkerB : DopPacker.MarkerA;
                                for (int c = 0; c < _channels; c++)
                                {
                                    int o = offset + done * frameBytes + c * 3;
                                    buffer[o] = 0x69; buffer[o + 1] = 0x69; buffer[o + 2] = marker;
                                }
                                _markerPhase = !_markerPhase;
                            }
                            else
                            {
                                DsdNativePacker.PackSilence(buffer, offset + done * frameBytes, 1, _channels, _slotBytes);
                            }
                            _bitsDone += _bitsPerFrame;   /* 静音帧也走表（时间在流逝，进度不冻结） */
                            done++;
                        }
                    }
                    return done * frameBytes;
                }
            }

            /// <summary>取一个预读块 → 打 DoP 帧；推进显示目标偏移。队列暂空返回 false（调用方区分欠载/EOF）。</summary>
            bool Fill()
            {
                RawBlk blk = null;
                lock (_qLock)
                {
                    if (_q.Count > 0) { blk = _q.Dequeue(); _qBytes -= blk.Len; }
                }
                if (blk == null) return false;
                int per = DsdDecoder.SplitRaw(_meta, blk.Buf, blk.Len, _flat);
                int frames = per / (_bitsPerFrame / 8);
                if (frames == 0)
                {
                    lock (_qLock) _pool.Push(blk.Buf);
                    return false;
                }
                if (_wire == WireMode.Dop)
                    DopPacker.Pack(_packed, _flat, per, frames, _channels, _lsbFirst, ref _markerPhase);
                else if (_wire == WireMode.Native32)
                    DsdNativePacker.Pack32(_packed, _flat, per, frames, _channels, _lsbFirst);
                else
                    DsdNativePacker.Pack16(_packed, _flat, per, frames, _channels, _lsbFirst);
                _pending = _packed;
                _pendPos = 0;
                _pendBytes = frames * _slotBytes * _channels;
                _consumed += blk.Len;
                Interlocked.Exchange(ref _dispTarget, _meta.DataOffset + _consumed);
                lock (_qLock) { _pool.Push(blk.Buf); Monitor.PulseAll(_qLock); }
                return true;
            }

            /// <summary>预读线程：顺序读原始位流进有界队列（音频线程只取块打包，零磁盘 I/O）。</summary>
            void PrefetchLoop()
            {
                int genSeen = 0;
                try
                {
                    while (!_stop)
                    {
                        if (genSeen != _readGen)
                        {
                            genSeen = _readGen;
                            _fs.Position = Interlocked.Read(ref _readRestart);
                        }
                        long pos = _fs.Position;
                        if (pos >= _dataEnd) { _eos = true; Thread.Sleep(15); continue; }
                        lock (_qLock)
                        {
                            while (_qBytes >= MaxQBytes && !_stop && genSeen == _readGen)
                                Monitor.Wait(_qLock, 20);
                        }
                        if (_stop || genSeen != _readGen) continue;

                        byte[] buf = null;
                        lock (_qLock) { if (_pool.Count > 0) buf = _pool.Pop(); }
                        if (buf == null) buf = new byte[_chunk];
                        int len = (int)Math.Min(_chunk, _dataEnd - pos);
                        int got = 0;
                        while (got < len)
                        {
                            int r = _fs.Read(buf, got, len - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        if (got <= 0)
                        {
                            lock (_qLock) _pool.Push(buf);
                            _eos = true;
                            continue;
                        }
                        /* 拆分对齐：只投 _block 整倍数（DSF 块交错/ DFF 帧交错都靠它保位序） */
                        int usable = got / _block * _block;
                        if (usable < got && pos + got < _dataEnd)
                        {
                            try { _fs.Position = pos + usable; } catch { }
                        }
                        if (usable <= 0)
                        {
                            if (pos + got >= _dataEnd) _eos = true;
                            lock (_qLock) _pool.Push(buf);
                            continue;
                        }
                        lock (_qLock)
                        {
                            if (genSeen == _readGen)   /* Seek 竞态：代数变了就丢弃旧块 */
                            {
                                _q.Enqueue(new RawBlk { Buf = buf, Len = usable });
                                _qBytes += usable;
                            }
                            else _pool.Push(buf);
                            Monitor.PulseAll(_qLock);
                        }
                    }
                }
                catch { _eos = true; }
            }

            /// <summary>欠载计数落日志（限流：前 5 次 + 每 200 次），供真机排查杂音来源。</summary>
            void NoteUnderrun()
            {
                int n = System.Threading.Interlocked.Increment(ref _underruns);
                if (n <= 5 || n % 200 == 0)
                {
                    try
                    {
                        System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pa1-native-crash.log"),
                            DateTime.Now + " [underrun] dop n=" + n + Environment.NewLine);
                    }
                    catch { }
                }
            }

            /// <summary>显示支路：独立句柄按需追播放偏移，44.1k 解码喂 FFT；慢了只影响刷新，不影响声音。</summary>
            void DispLoop()
            {
                try
                {
                    long pos = _meta.DataOffset;
                    int genSeen = 0;
                    _dfs.Position = pos;
                    var disp = DsdDecoder.MakePipeline(_meta, 44100, false);
                    while (!_stop)
                    {
                        if (genSeen != _seekGen)
                        {
                            genSeen = _seekGen;
                            pos = Interlocked.Read(ref _dispRestart);
                            _dfs.Position = pos;
                            disp = DsdDecoder.MakePipeline(_meta, 44100, false);
                        }
                        long target = Interlocked.Read(ref _dispTarget);
                        if (pos >= target || pos >= _dataEnd) { Thread.Sleep(10); continue; }
                        int len = (int)Math.Min(_block, Math.Min(target, _dataEnd) - pos);
                        int got = 0;
                        while (got < len)
                        {
                            int r = _dfs.Read(_dibuf, got, len - got);
                            if (r <= 0) break;
                            got += r;
                        }
                        if (got <= 0) { Thread.Sleep(10); continue; }
                        int per = DsdDecoder.SplitRaw(_meta, _dibuf, got, _dflat);
                        var planes = disp.Feed(_dflat, per);
                        if (_eng != null && planes != null && planes.Length > 0 && planes[0].Length > 0)
                        {
                            int n = planes[0].Length;
                            if (_tapScratch.Length < n) _tapScratch = new float[n];
                            for (int i = 0; i < n; i++)
                            {
                                float s = 0;
                                for (int c = 0; c < _channels; c++) s += planes[c][i];
                                _tapScratch[i] = s / _channels;
                            }
                            _eng.PushSamples(_tapScratch, n);
                        }
                        pos += got;
                    }
                }
                catch { /* 流已释放等异常：显示支路静默收场 */ }
            }

            public void Dispose()
            {
                _stop = true;
                lock (_qLock) Monitor.PulseAll(_qLock);
                try { if (_prefetchThread != null) _prefetchThread.Join(500); } catch { }
                try { if (_dispThread != null) _dispThread.Join(500); } catch { }
                try { _fs.Dispose(); } catch { }
                try { _dfs.Dispose(); } catch { }
            }
        }

        /// <summary>float 样本 → 24-bit PCM（独占模式输出）。PCM24 是 WASAPI 独占最通用格式。</summary>
        sealed class SampleToPcm24Provider : IWaveProvider
        {
            readonly ISampleProvider _src;
            float[] _tmp;               /* 复用缓冲：音频回调内零分配（曾每回调 new float[] → GC 抖动） */
            public SampleToPcm24Provider(ISampleProvider src)
            {
                _src = src;
                WaveFormat = new WaveFormat(src.WaveFormat.SampleRate, 24, src.WaveFormat.Channels);
            }
            public WaveFormat WaveFormat { get; private set; }

            public int Read(byte[] buffer, int offset, int count)
            {
                int ch = WaveFormat.Channels;
                int bytesPerFrame = 3 * ch;
                int frames = count / bytesPerFrame;
                if (frames <= 0) return 0;
                int need = frames * ch;
                if (_tmp == null || _tmp.Length < need) _tmp = new float[need];
                int got = _src.Read(_tmp, 0, need);
                if (got <= 0) return 0;
                int outFrames = got / ch;
                int o = offset;
                for (int i = 0; i < outFrames; i++)
                {
                    for (int c = 0; c < ch; c++)
                    {
                        int i24 = ToI24(SoftClip(_tmp[i * ch + c]));
                        buffer[o++] = (byte)(i24 & 0xff);
                        buffer[o++] = (byte)((i24 >> 8) & 0xff);
                        buffer[o++] = (byte)((i24 >> 16) & 0xff);
                    }
                }
                return outFrames * bytesPerFrame;
            }
        }

        /// <summary>软件增益（替代 NAudio WasapiOut.Volume——那个写系统端点音量，USB DAC 常报「尚未实现」）。</summary>
        sealed class GainProvider : ISampleProvider
        {
            readonly ISampleProvider _src;
            public volatile float Gain = 1f;
            float _cur = 1f;
            public GainProvider(ISampleProvider src) { _src = src; }
            public WaveFormat WaveFormat { get { return _src.WaveFormat; } }
            public int Read(float[] samples, int offset, int count)
            {
                int got = _src.Read(samples, offset, count);
                float target = Gain;
                float step = 1f / (0.005f * WaveFormat.SampleRate);   /* 5ms 增益平滑：瞬时跳变会咔哒 */
                if (_cur != target || _cur != 1f)
                {
                    for (int i = 0; i < got; i++)
                    {
                        float d = target - _cur;
                        if (d > step) _cur += step; else if (d < -step) _cur -= step; else _cur = target;
                        if (_cur != 1f) samples[offset + i] *= _cur;
                    }
                }
                return got;
            }
        }

        /// <summary>样本分路：一路进环形缓冲供 FFT，一路透传输出。</summary>
        /// <summary>PCM 解码链的定位包装。
        /// ① Read 与 Seek 同锁互斥：UI 线程 seek 与 WASAPI 渲染线程并发读 MF/AFR 解码器是竞态，
        ///    会让跳转后位置/内容错位（概率性）；
        /// ② Seek 后重建重采样器：其内部残留「Seek 前已读未用」的旧位置样本，不重建会先播出
        ///    旧音频尾巴，与进度条目标位置对不上（拖向开头时尤其明显）。</summary>
        sealed class PcmSeekWrap : ISampleProvider, IDisposable
        {
            readonly WaveStream _stream;        /* 底层可定位解码流（MediaFoundation / AudioFileReader） */
            readonly ISampleProvider _rawSp;    /* 解码流的采样视图（未重采样） */
            readonly int _targetRate;
            readonly object _lock = new object();
            ISampleProvider _chain;             /* 重采样器（源率=目标率时即 _rawSp） */

            public PcmSeekWrap(WaveStream stream, ISampleProvider rawSp, int targetRate)
            {
                _stream = stream; _rawSp = rawSp; _targetRate = targetRate;
                _chain = Build();
            }

            ISampleProvider Build()
            {
                return _rawSp.WaveFormat.SampleRate != _targetRate
                    ? (ISampleProvider)new WdlResamplingSampleProvider(_rawSp, _targetRate)
                    : _rawSp;
            }

            public WaveFormat WaveFormat { get { return _chain.WaveFormat; } }

            public int Read(float[] buffer, int offset, int count)
            {
                lock (_lock) return _chain.Read(buffer, offset, count);
            }

            public TimeSpan CurrentTime
            {
                get { lock (_lock) return _stream.CurrentTime; }
                set
                {
                    lock (_lock)
                    {
                        _stream.CurrentTime = value;
                        _chain = Build();   /* 丢弃重采样器旧残留样本 */
                    }
                }
            }

            public TimeSpan TotalTime { get { return _stream.TotalTime; } }

            /// <summary>精确时长真值转发给解码器（拦截 seek 后 MF 超发的尾巴样本）。</summary>
            public void SetTrueLength(double sec)
            {
                var mf = _stream as MfPcmReader;
                if (mf != null) mf.SetTrueLength(sec);
            }

            public void Dispose() { try { _stream.Dispose(); } catch { } }
        }

        sealed class TapProvider : ISampleProvider
        {
            readonly ISampleProvider _src;
            readonly AudioEngine _eng;
            float[] _mono;              /* 复用单声道暂存：逐样本 new[]{s} 曾在音频回调里每秒造 4 万个小数组 */
            public TapProvider(ISampleProvider src, AudioEngine eng)
            {
                _src = src; _eng = eng;
                _mono = new float[4096];
            }
            public WaveFormat WaveFormat { get { return _src.WaveFormat; } }
            public int Read(float[] samples, int offset, int count)
            {
                int got = _src.Read(samples, offset, count);
                if (got > 0)
                {
                    /* 混成单声道进环缓（块拷贝，零分配） */
                    int ch = _src.WaveFormat.Channels;
                    int n = got / ch;
                    if (_mono.Length < n) _mono = new float[n];
                    for (int i = 0; i < n; i++)
                    {
                        float s = 0;
                        for (int c = 0; c < ch; c++) s += samples[offset + i * ch + c];
                        _mono[i] = s / ch;
                    }
                    _eng.PushSamples(_mono, n);
                }
                return got;
            }
        }
    }
}
