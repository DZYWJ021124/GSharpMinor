using System;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.MediaFoundation;
using NAudio.Utils;

namespace Pa1.App
{
    /// <summary>带样本时间戳的 Media Foundation 解码读取器（PCM 输出）。
    ///
    /// NAudio 的 MediaFoundationReader 丢弃 ReadSample 返回的 timestamp，SetCurrentPosition
    /// 后把 Position 直接记成请求值——MF 对无 SEEKTABLE 的 VBR FLAC 会按压缩字节比例估算
    /// 落点，落偏可达十几秒（实测 221s 请求落 208s），造成「曲终时钟 ≠ 总时长」与
    /// 「跳转后听到的不是目标位置」。本读取器：
    /// ① Position 取样本真实 timestamp + 缓冲内偏移（真值）；
    /// ② Seek 后校准落点：偏早→丢弃前缀精确到目标；偏晚→回撤重找（≤3 次）后接受真实落点；
    /// ③ Seek 延迟到 Read 线程执行（沿用 NAudio RepositionInRead 语义：MF reader 跨线程
    ///    SetCurrentPosition 不安全，UI 线程只登记目标）；
    /// ④ EOF 处钳制 Position：seek 后 MF 可能超发尾巴样本（时间戳平滑延续但已无真实
    ///    音频），Position 会超计——在 getter 处 clamp 到真值，音频不截断。</summary>
    sealed class MfPcmReader : WaveStream
    {
        readonly string _file;
        readonly WaveFormat _fmt;
        readonly long _lenBytes;
        readonly object _lock = new object();
        IMFSourceReader _reader;

        byte[] _decBuf;              /* 当前解码样本块 */
        int _decOff, _decCount;

        long _position;              /* 字节（输出格式口径），真值 */
        long _repositionTo = -1;     /* UI 线程登记的 seek 目标（字节），Read 线程消费 */
        double _seekTarget = -1;     /* 待校准落点的目标（秒）；-1=无 */
        int _seekAttempts;
        double _trueLenSec = -1;     /* 精确总时长（秒，后台探测到 EOF 的真值）；-1=未知 */

        public MfPcmReader(string file)
        {
            MediaFoundationApi.Startup();
            _file = file;
            _reader = CreateReader(file);
            _fmt = ReadWaveFormat(_reader);
            _reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            _lenBytes = GetLengthBytes(_reader, _fmt);
            /* 与 NAudio 同款线程策略：init 用的 reader 释放，首个 Read 在读线程上重建 */
            Marshal.ReleaseComObject(_reader);
            _reader = null;
        }

        static IMFSourceReader CreateReader(string file)
        {
            IMFSourceReader reader;
            MediaFoundationInterop.MFCreateSourceReaderFromURL(file, null, out reader);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_ALL_STREAMS, false);
            reader.SetStreamSelection(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, true);
            var partial = new MediaType();
            partial.MajorType = MediaTypes.MFMediaType_Audio;
            partial.SubType = AudioSubtypes.MFAudioFormat_PCM;
            var current = new MediaType(GetCurrentMediaType(reader));
            partial.ChannelCount = current.ChannelCount;
            partial.SampleRate = current.SampleRate;
            try
            {
                reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                    IntPtr.Zero, partial.MediaFoundationObject);
            }
            catch (COMException ex)
            {
                /* HE-AAC 单声道在 Win10 上采样率折半的既有怪癖（NAudio 同款补丁） */
                if (ex.GetHResult() == MediaFoundationErrors.MF_E_INVALIDMEDIATYPE &&
                    current.SubType == AudioSubtypes.MFAudioFormat_AAC && current.ChannelCount == 1)
                {
                    partial.SampleRate = current.SampleRate *= 2;
                    partial.ChannelCount = current.ChannelCount *= 2;
                    reader.SetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM,
                        IntPtr.Zero, partial.MediaFoundationObject);
                }
                else throw;
            }
            Marshal.ReleaseComObject(current.MediaFoundationObject);
            return reader;
        }

        static IMFMediaType GetCurrentMediaType(IMFSourceReader reader)
        {
            reader.GetCurrentMediaType(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, out IMFMediaType mt);
            return mt;
        }

        static WaveFormat ReadWaveFormat(IMFSourceReader reader)
        {
            var umt = new MediaType(GetCurrentMediaType(reader));
            int ch = umt.ChannelCount, bits = umt.BitsPerSample, rate = umt.SampleRate;
            Guid sub = umt.SubType;
            Marshal.ReleaseComObject(umt.MediaFoundationObject);
            if (sub == AudioSubtypes.MFAudioFormat_PCM) return new WaveFormat(rate, bits, ch);
            if (sub == AudioSubtypes.MFAudioFormat_Float) return WaveFormat.CreateIeeeFloatWaveFormat(rate, ch);
            throw new InvalidDataException("UNSUPPORTED MF SUBTYPE");
        }

        static long GetLengthBytes(IMFSourceReader reader, WaveFormat fmt)
        {
            var variantPtr = Marshal.AllocHGlobal(64);
            try
            {
                int hr = reader.GetPresentationAttribute(MediaFoundationInterop.MF_SOURCE_READER_MEDIASOURCE,
                    MediaFoundationAttributes.MF_PD_DURATION, variantPtr);
                if (hr == MediaFoundationErrors.MF_E_ATTRIBUTENOTFOUND) return 0;
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                /* PROPVARIANT 值联合体在偏移 8（VT_I8 时长，单位 100ns） */
                long ns = Marshal.ReadInt64(variantPtr, 8);
                return (ns * fmt.AverageBytesPerSecond) / 10000000L;
            }
            finally
            {
                PropVariantClear(variantPtr);
                Marshal.FreeHGlobal(variantPtr);
            }
        }

        public override WaveFormat WaveFormat { get { return _fmt; } }
        public override long Length { get { return _lenBytes; } }

        public override long Position
        {
            get
            {
                lock (_lock)
                {
                    /* seek 后 MF 超发尾巴样本会让 _position 超计——clamp 到真值，
                       音频不截断，只修正曲终时钟显示。真值未到时用 est+1s 兜底。 */
                    double limit = _trueLenSec > 0 ? _trueLenSec
                        : (_lenBytes > 0 ? _lenBytes / (double)_fmt.AverageBytesPerSecond + 1.0 : -1);
                    if (limit > 0)
                    {
                        long cap = BytesOf(limit);
                        if (_position > cap) return cap;
                    }
                    return _position;
                }
            }
            set
            {
                if (value < 0) value = 0;
                lock (_lock) { _repositionTo = value; _position = value; }
            }
        }

        /// <summary>后台精确时长探测完成后设置真值，用于 EOF 处钳制 Position 显示。</summary>
        public void SetTrueLength(double sec)
        {
            lock (_lock) { if (sec > 0 && (_trueLenSec <= 0 || sec < _trueLenSec)) _trueLenSec = sec; }
        }

        void DoSetPosition(double sec)
        {
            long ns = (long)(sec * 10000000.0);
            /* 手造 PROPVARIANT { VT_I8=20 @0, 值 @8 }（免依赖 NAudio.PropVariant 的命名空间） */
            var ptr = Marshal.AllocHGlobal(16);
            try
            {
                for (int i = 0; i < 16; i++) Marshal.WriteByte(ptr, i, 0);
                Marshal.WriteInt16(ptr, 0, 20);          /* VT_I8 */
                Marshal.WriteInt64(ptr, 8, ns);
                _reader.SetCurrentPosition(Guid.Empty, ptr);
            }
            finally { PropVariantClear(ptr); Marshal.FreeHGlobal(ptr); }
            _decOff = 0; _decCount = 0;
        }

        [DllImport("ole32.dll")]
        static extern int PropVariantClear(IntPtr pvar);

        void StartSeek(long bytes)
        {
            _seekTarget = bytes / (double)_fmt.AverageBytesPerSecond;
            _seekAttempts = 0;
            DoSetPosition(_seekTarget);
            _repositionTo = -1;
            try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                DateTime.Now + " [mfs-seek] " + _seekTarget.ToString("0.00") + Environment.NewLine); } catch { }
        }

        long BytesOf(double sec)
        {
            long b = (long)(sec * _fmt.AverageBytesPerSecond);
            b -= b % _fmt.BlockAlign;
            return b;
        }

        /// <summary>拉一个解码样本入缓冲；false=真 EOF。Seek 落点校准在此完成。</summary>
        bool PullSample()
        {
            while (true)
            {
                _reader.ReadSample(MediaFoundationInterop.MF_SOURCE_READER_FIRST_AUDIO_STREAM, 0,
                    out int idx, out MF_SOURCE_READER_FLAG flags, out ulong ts, out IMFSample sample);
                if ((flags & MF_SOURCE_READER_FLAG.MF_SOURCE_READERF_ENDOFSTREAM) != 0 || sample == null)
                {
                    _seekTarget = -1;
                    try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "gsharpminor-crash.log"),
                        DateTime.Now + " [mfs-eof] pos=" + (_position / (double)_fmt.AverageBytesPerSecond).ToString("0.00") + Environment.NewLine); } catch { }
                    return false;
                }
                sample.ConvertToContiguousBuffer(out IMFMediaBuffer buf);
                buf.Lock(out IntPtr data, out int maxLen, out int curLen);
                if (_decBuf == null || _decBuf.Length < curLen) _decBuf = new byte[curLen];
                Marshal.Copy(data, _decBuf, 0, curLen);
                buf.Unlock();
                Marshal.ReleaseComObject(buf);
                Marshal.ReleaseComObject(sample);
                _decOff = 0; _decCount = curLen;

                if (_seekTarget < 0)
                {
                    return true;
                }

                double sStart = ts / 1e7;
                double sEnd = sStart + curLen / (double)_fmt.AverageBytesPerSecond;

                if (sStart > _seekTarget + 0.03)
                {
                    /* 落点偏晚：按超调量回撤重找（≤3 次），仍晚则接受真实落点（位置取真值） */
                    _seekAttempts++;
                    if (_seekAttempts <= 3)
                    {
                        DoSetPosition(Math.Max(0, _seekTarget - (sStart - _seekTarget) - 0.25));
                        continue;
                    }
                    _position = BytesOf(sStart);
                    _seekTarget = -1;
                    return true;
                }
                if (sEnd <= _seekTarget + 0.001)
                {
                    /* 落点偏早且本块未到目标：丢弃整块继续向前 */
                    _decCount = 0;
                    continue;
                }
                /* 本块跨过目标：裁掉前缀，精确落点 */
                int skip = (int)((_seekTarget - sStart) * _fmt.AverageBytesPerSecond);
                skip -= skip % _fmt.BlockAlign;
                if (skip > 0 && skip < curLen) { _decOff = skip; _decCount = curLen - skip; }
                _position = BytesOf(_seekTarget);
                _seekTarget = -1;
                return true;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (_lock)
            {
                if (_reader == null) _reader = CreateReader(_file);
                if (_repositionTo != -1) StartSeek(_repositionTo);
                int written = 0;
                while (written < count)
                {
                    if (_decCount <= 0)
                    {
                        if (!PullSample()) break;
                        if (_decCount <= 0) continue;
                    }
                    int n = Math.Min(count - written, _decCount);
                    Array.Copy(_decBuf, _decOff, buffer, offset + written, n);
                    _decOff += n; _decCount -= n;
                    written += n;
                }
                _position += written;
                return written;
            }
        }

        protected override void Dispose(bool disposing)
        {
            lock (_lock)
            {
                if (_reader != null) { Marshal.ReleaseComObject(_reader); _reader = null; }
            }
            base.Dispose(disposing);
        }
    }
}
