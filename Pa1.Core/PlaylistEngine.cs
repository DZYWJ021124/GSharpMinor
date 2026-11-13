using System;
using System.Collections.Generic;
using System.Linq;

namespace Pa1.Core
{
    /// <summary>
    /// 播放模式：order 顺序播放到列表尾停 · shuffle 全列表随机循环 ·
    ///            album-order 专辑内按曲序播完停 · album-shuffle 专辑内随机循环 ·
    ///            one 单曲循环。
    /// </summary>
    public sealed class PlayMode
    {
        public string Id;
        public string Label;
        public string Title;

        public static readonly PlayMode[] All =
        {
            new PlayMode { Id = "order",         Label = "Order",     Title = "顺序播放：按列表顺序播到结尾停止" },
            new PlayMode { Id = "shuffle",       Label = "Shuffle",   Title = "随机播放：全列表随机，播完洗牌继续" },
            new PlayMode { Id = "album-order",   Label = "Alb Order", Title = "专辑内顺序：同一专辑按曲序播完停止" },
            new PlayMode { Id = "album-shuffle", Label = "Alb Shuf",  Title = "专辑内随机：同一专辑内随机循环" },
            new PlayMode { Id = "one",           Label = "Repeat 1",  Title = "单曲循环：一直重复当前曲目" },
        };
    }

    /// <summary>播放顺序推进引擎（与 phosphor-pa1.html pickNext/orderedSeq 逐行对齐）。</summary>
    public sealed class PlaylistEngine
    {
        readonly List<TrackMeta> _tracks = new List<TrackMeta>();
        readonly Random _rng;
        List<int> _shuffleOrder = new List<int>();
        string _shuffleSig = "";

        public int ModeIdx;
        public int CurIdx = -1;

        public PlaylistEngine(int? seed = null)
        {
            _rng = seed.HasValue ? new Random(seed.Value) : new Random();
        }

        public IList<TrackMeta> Tracks { get { return _tracks; } }
        public PlayMode Mode { get { return PlayMode.All[ModeIdx]; } }

        public void CycleMode()
        {
            ModeIdx = (ModeIdx + 1) % PlayMode.All.Length;
        }

        public void InvalidateShuffle()
        {
            _shuffleSig = "";
        }

        List<int> PoolAlbum()
        {
            if (CurIdx < 0 || CurIdx >= _tracks.Count) return Enumerable.Range(0, _tracks.Count).ToList();
            string alb = _tracks[CurIdx].Album ?? "";
            return _tracks.Select((t, i) => i).Where(i => (_tracks[i].Album ?? "") == alb).ToList();
        }

        /// <summary>按模式给出完整播放序列。</summary>
        public List<int> OrderedSeq(string mode)
        {
            var all = Enumerable.Range(0, _tracks.Count).ToList();
            if (mode == "order") return all;
            if (mode == "album-order")
            {
                return PoolAlbum().OrderBy(i => _tracks[i].TrackNo).ThenBy(i => i).ToList();
            }
            var pool = mode == "album-shuffle" ? PoolAlbum() : all;
            string sig = mode + ":" + string.Join(",", pool);
            if (sig != _shuffleSig || _shuffleOrder.Count != pool.Count)
            {
                _shuffleSig = sig;
                _shuffleOrder = pool.ToList();
                for (int i = _shuffleOrder.Count - 1; i > 0; i--)
                {
                    int j = _rng.Next(i + 1);
                    int tmp = _shuffleOrder[i]; _shuffleOrder[i] = _shuffleOrder[j]; _shuffleOrder[j] = tmp;
                }
            }
            return _shuffleOrder;
        }

        /// <summary>
        /// dir=+1 下一首 / -1 上一首；auto=true 表示曲目自然播完推进。
        /// 返回 -1 表示停止。手动切换循环绕回；顺序类播完停止；随机类洗牌继续。
        /// </summary>
        public int PickNext(int dir, bool auto)
        {
            int n = _tracks.Count;
            if (n == 0) return -1;
            string mode = Mode.Id;
            if (CurIdx < 0 || CurIdx >= n) return 0;
            if (mode == "one")
            {
                if (auto) return CurIdx;
                return ((CurIdx + dir) % n + n) % n;
            }
            var seq = OrderedSeq(mode);
            int pos = seq.IndexOf(CurIdx);
            if (pos == -1) return seq[0];
            if (auto)
            {
                int nx = pos + 1;
                if (nx >= seq.Count)
                {
                    if (mode == "shuffle" || mode == "album-shuffle")
                    {
                        _shuffleSig = "";                 // 播完一轮 -> 洗牌继续
                        var fresh = OrderedSeq(mode);
                        /* 洗牌后避免立刻重播刚播完的这首 */
                        if (fresh.Count > 1 && fresh[0] == CurIdx)
                        {
                            int j = 1 + _rng.Next(fresh.Count - 1);
                            int tmp = fresh[0]; fresh[0] = fresh[j]; fresh[j] = tmp;
                        }
                        return fresh[0];
                    }
                    return -1;                        // 顺序类：列表/专辑播完停止
                }
                return seq[nx];
            }
            return seq[((pos + dir) % seq.Count + seq.Count) % seq.Count];
        }

        /// <summary>排序：专辑 -> 曲序 -> 标题。</summary>
        public void Sort()
        {
            var sorted = _tracks
                .OrderBy(t => string.IsNullOrEmpty(t.Album) ? "\uffff" : t.Album, StringComparer.Ordinal)
                .ThenBy(t => t.TrackNo)
                .ThenBy(t => t.Title ?? "", StringComparer.Ordinal)
                .ToList();
            _tracks.Clear();
            _tracks.AddRange(sorted);
            _shuffleSig = "";
        }
    }
}
