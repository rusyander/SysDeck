// SysDeck — «Загрузки», торренты: выбор кусков и блоков для запросов.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Порядок: сначала дозапрашиваются начатые куски (недокачанный кусок бесполезен), затем новые — по приоритету файлов,
// первый и последний кусок нужного файла раньше остальных (по ним плеер и архиватор узнают файл), затем по порядку
// (последовательная загрузка) или самые редкие. Порядок пересчитывается не чаще раза в секунду: сортировка всех кусков
// на каждый блок стоила бы дороже самой загрузки. Эндшпиль: когда свободных блоков не осталось, недостающий блок
// просится ещё у одного пира, а пришедший первым отменяет остальные запросы. Работает только в потоке реактора.
using System;
using System.Collections.Generic;

namespace SysDeck.Downloads
{
    internal sealed class BtBlockReq
    {
        public int Piece, Begin, Length;
        public long SentMs;
        public BtBlockReq(int piece, int begin, int length) { Piece = piece; Begin = begin; Length = length; }
        public bool Same(int piece, int begin, int length) { return Piece == piece && Begin == begin && Length == length; }
    }

    internal interface IBtPickPeer
    {
        bool HasPiece(int piece);
        bool CanRequest(int piece);          // есть у пира и разрешено сейчас (не задушены или кусок из allowed fast)
        string BanKey { get; }               // адрес пира: по нему начисляются штрафы за испорченные куски
    }

    internal enum BtBlockResult { Accepted, PieceComplete, Duplicate, Invalid }

    internal sealed class BtPicker
    {
        public const int EndgameRequesters = 2;
        public const int OrderRefreshMs = 1000;

        private sealed class Partial
        {
            public int Piece;
            public int Blocks;
            public byte[] State;                         // 0 — свободен, 1 — запрошен, 2 — получен
            public List<IBtPickPeer>[] Requesters;
            public string[] From;                        // кто прислал блок
        }

        private readonly BtMeta _meta;
        private readonly BtBitfield _have;
        private readonly int[] _avail;
        private readonly byte[] _prio;
        private readonly bool[] _edge;
        private readonly int[] _tie;
        private readonly Dictionary<int, Partial> _partial = new Dictionary<int, Partial>();
        private readonly HashSet<int> _checking = new HashSet<int>();
        private int _seeds;
        private int[] _order;
        private bool _orderDirty = true;
        private long _orderBuilt = long.MinValue;
        private int _wantedMissing;
        private bool _sequential;

        public BtPicker(BtMeta meta, BtBitfield have)
        {
            _meta = meta;
            _have = have;
            _avail = new int[meta.PieceCount];
            _prio = new byte[meta.PieceCount];
            _edge = new bool[meta.PieceCount];
            _tie = new int[meta.PieceCount];
            Random rng = new Random();
            for (int i = 0; i < _tie.Length; i++) _tie[i] = rng.Next();
            SetPriorities(null);
        }

        public int PieceCount { get { return _avail.Length; } }
        public int WantedMissing { get { return _wantedMissing; } }
        public int Seeds { get { return _seeds; } }
        public int PartialCount { get { return _partial.Count; } }

        public bool Sequential
        {
            get { return _sequential; }
            set { if (_sequential != value) { _sequential = value; _orderDirty = true; _orderBuilt = long.MinValue; } }
        }

        public int Priority(int piece) { return piece >= 0 && piece < _prio.Length ? _prio[piece] : 0; }
        public int Availability(int piece) { return piece >= 0 && piece < _avail.Length ? _avail[piece] + _seeds : 0; }

        // filePriorities[i]: 0 — не качать, 1 — обычный, 2 — высокий; null или короче — обычный.
        public void SetPriorities(int[] filePriorities)
        {
            Array.Clear(_prio, 0, _prio.Length);
            Array.Clear(_edge, 0, _edge.Length);
            for (int i = 0; i < _meta.Files.Count; i++)
            {
                BtFile f = _meta.Files[i];
                if (f.Pad || f.Symlink || f.Length == 0 || f.LastPiece < 0) continue;
                int fp = filePriorities == null || i >= filePriorities.Length ? 1 : Math.Max(0, Math.Min(2, filePriorities[i]));
                if (fp == 0) continue;
                for (int p = Math.Max(0, f.FirstPiece); p <= f.LastPiece && p < _prio.Length; p++)
                    if (_prio[p] < fp) _prio[p] = (byte)fp;
                if (f.FirstPiece >= 0 && f.FirstPiece < _edge.Length) _edge[f.FirstPiece] = true;
                if (f.LastPiece < _edge.Length) _edge[f.LastPiece] = true;
            }
            RecountWanted();
            _orderDirty = true;
            _orderBuilt = long.MinValue;
        }

        public void RecountWanted()
        {
            int missing = 0;
            for (int p = 0; p < _prio.Length; p++) if (_prio[p] > 0 && !_have[p]) missing++;
            _wantedMissing = missing;
        }

        // ---------- наличие у пиров ----------
        public void AddPeerBits(BtBitfield bits, bool seed)
        {
            if (seed) { _seeds++; _orderDirty = true; return; }
            if (bits == null) return;
            for (int p = 0; p < _avail.Length; p++) if (bits[p]) _avail[p]++;
            _orderDirty = true;
        }

        public void RemovePeerBits(BtBitfield bits, bool seed)
        {
            if (seed) { _seeds = Math.Max(0, _seeds - 1); _orderDirty = true; return; }
            if (bits == null) return;
            for (int p = 0; p < _avail.Length; p++) if (bits[p] && _avail[p] > 0) _avail[p]--;
            _orderDirty = true;
        }

        public void PeerHave(int piece)
        {
            if (piece < 0 || piece >= _avail.Length) return;
            _avail[piece]++;
            _orderDirty = true;
        }

        // Пир с частичным набором стал сидом: его куски переходят в счётчик сидов.
        public void PeerBecameSeed(BtBitfield bits)
        {
            RemovePeerBits(bits, false);
            AddPeerBits(null, true);
        }

        // Кусок нужен: высокий приоритет и не скачан. Для решения «интересен ли пир».
        public bool Wants(int piece) { return piece >= 0 && piece < _prio.Length && _prio[piece] > 0 && !_have[piece]; }

        // ---------- порядок ----------
        private int Compare(int a, int b)
        {
            if (_prio[a] != _prio[b]) return _prio[b].CompareTo(_prio[a]);
            if (_edge[a] != _edge[b]) return _edge[a] ? -1 : 1;
            if (_sequential) return a.CompareTo(b);
            if (_avail[a] != _avail[b]) return _avail[a].CompareTo(_avail[b]);
            if (_tie[a] != _tie[b]) return _tie[a].CompareTo(_tie[b]);
            return a.CompareTo(b);
        }

        public void BuildOrder(long now)
        {
            List<int> list = new List<int>();
            for (int p = 0; p < _prio.Length; p++) if (_prio[p] > 0 && !_have[p]) list.Add(p);
            list.Sort(Compare);
            _order = list.ToArray();
            _orderDirty = false;
            _orderBuilt = now;
        }

        // Первые n кусков в текущем порядке (для тестов и карточки).
        public List<int> OrderPreview(int n)
        {
            List<int> list = new List<int>();
            if (_order == null) return list;
            foreach (int p in _order)
            {
                if (list.Count >= n) break;
                if (!_have[p]) list.Add(p);
            }
            return list;
        }

        // ---------- выбор ----------
        // Добавляет в result до max запросов для пира. Блоки, уже запрошенные этим пиром, не повторяются.
        public void Pick(IBtPickPeer peer, int max, long now, List<BtBlockReq> result)
        {
            if (max <= 0) return;
            // long.MinValue — «перестроить сразу»; разность с ним переполнилась бы в минус, и порядок не перестраивался бы никогда.
            if (_order == null || (_orderDirty && (_orderBuilt == long.MinValue || now - _orderBuilt >= OrderRefreshMs))) BuildOrder(now);
            int picked = 0;

            if (_partial.Count > 0)
            {
                List<int> started = new List<int>(_partial.Keys);
                started.Sort(Compare);
                foreach (int piece in started)
                {
                    if (picked >= max) return;
                    if (_prio[piece] == 0 || _have[piece] || _checking.Contains(piece) || !peer.CanRequest(piece)) continue;
                    picked += TakeFree(_partial[piece], peer, max - picked, now, result);
                }
            }

            foreach (int piece in _order)
            {
                if (picked >= max) return;
                if (_have[piece] || _prio[piece] == 0 || _partial.ContainsKey(piece) || _checking.Contains(piece) || !peer.CanRequest(piece)) continue;
                Partial pp = NewPartial(piece);
                _partial[piece] = pp;
                picked += TakeFree(pp, peer, max - picked, now, result);
            }

            if (picked < max && IsEndgame())
            {
                List<int> started = new List<int>(_partial.Keys);
                started.Sort(Compare);
                foreach (int piece in started)
                {
                    if (picked >= max) return;
                    Partial pp = _partial[piece];
                    if (_prio[piece] == 0 || _checking.Contains(piece) || !peer.CanRequest(piece)) continue;
                    for (int b = 0; b < pp.Blocks && picked < max; b++)
                    {
                        if (pp.State[b] != 1) continue;
                        List<IBtPickPeer> who = pp.Requesters[b];
                        if (who != null && (who.Contains(peer) || who.Count >= EndgameRequesters)) continue;
                        AddRequester(pp, b, peer);
                        result.Add(MakeReq(piece, b, now));
                        picked++;
                    }
                }
            }
        }

        // Эндшпиль: все недостающие нужные куски начаты и в них нет свободных блоков.
        public bool IsEndgame()
        {
            if (_order != null)
                foreach (int p in _order)
                    if (!_have[p] && _prio[p] > 0 && !_partial.ContainsKey(p) && !_checking.Contains(p)) return false;
            foreach (Partial pp in _partial.Values)
            {
                if (_prio[pp.Piece] == 0 || _checking.Contains(pp.Piece)) continue;
                for (int b = 0; b < pp.Blocks; b++) if (pp.State[b] == 0) return false;
            }
            return _wantedMissing > 0;
        }

        private Partial NewPartial(int piece)
        {
            Partial pp = new Partial();
            pp.Piece = piece;
            int size = _meta.PieceSize(piece);
            pp.Blocks = Math.Max(1, (size + BtMeta.BlockSize - 1) / BtMeta.BlockSize);
            pp.State = new byte[pp.Blocks];
            pp.Requesters = new List<IBtPickPeer>[pp.Blocks];
            pp.From = new string[pp.Blocks];
            return pp;
        }

        private int TakeFree(Partial pp, IBtPickPeer peer, int max, long now, List<BtBlockReq> result)
        {
            int n = 0;
            for (int b = 0; b < pp.Blocks && n < max; b++)
            {
                if (pp.State[b] != 0) continue;
                pp.State[b] = 1;
                AddRequester(pp, b, peer);
                result.Add(MakeReq(pp.Piece, b, now));
                n++;
            }
            return n;
        }

        private static void AddRequester(Partial pp, int block, IBtPickPeer peer)
        {
            if (pp.Requesters[block] == null) pp.Requesters[block] = new List<IBtPickPeer>(1);
            if (!pp.Requesters[block].Contains(peer)) pp.Requesters[block].Add(peer);
        }

        private BtBlockReq MakeReq(int piece, int block, long now)
        {
            int begin = block * BtMeta.BlockSize;
            BtBlockReq r = new BtBlockReq(piece, begin, Math.Min(BtMeta.BlockSize, _meta.PieceSize(piece) - begin));
            r.SentMs = now;
            return r;
        }

        // Запрос снят (отказ, задушили, пир ушёл, пир тормозит): блок снова свободен, если его больше никто не ждёт.
        public void Release(IBtPickPeer peer, int piece, int begin)
        {
            Partial pp;
            if (!_partial.TryGetValue(piece, out pp) || begin < 0 || begin % BtMeta.BlockSize != 0) return;
            int b = begin / BtMeta.BlockSize;
            if (b >= pp.Blocks) return;
            List<IBtPickPeer> who = pp.Requesters[b];
            if (who != null) who.Remove(peer);
            if (pp.State[b] == 1 && (who == null || who.Count == 0)) pp.State[b] = 0;
        }

        // Пришёл блок. Принимается только блок нужного куска на границе 16 КБ точной длины, ещё не полученный.
        // cancels — запросы того же блока у других пиров (эндшпиль): их надо отменить.
        public BtBlockResult OnBlock(IBtPickPeer peer, int piece, int begin, int length, List<KeyValuePair<IBtPickPeer, BtBlockReq>> cancels)
        {
            if (piece < 0 || piece >= _avail.Length || begin < 0 || begin % BtMeta.BlockSize != 0) return BtBlockResult.Invalid;
            int size = _meta.PieceSize(piece);
            if (begin >= size || length != Math.Min(BtMeta.BlockSize, size - begin)) return BtBlockResult.Invalid;
            if (_have[piece] || _checking.Contains(piece)) return BtBlockResult.Duplicate;
            Partial pp;
            if (!_partial.TryGetValue(piece, out pp))
            {
                if (_prio[piece] == 0) return BtBlockResult.Duplicate;
                pp = NewPartial(piece);
                _partial[piece] = pp;
            }
            int b = begin / BtMeta.BlockSize;
            if (pp.State[b] == 2) return BtBlockResult.Duplicate;
            pp.State[b] = 2;
            pp.From[b] = peer.BanKey;
            List<IBtPickPeer> who = pp.Requesters[b];
            if (who != null)
            {
                foreach (IBtPickPeer other in who)
                    if (other != peer && cancels != null) cancels.Add(new KeyValuePair<IBtPickPeer, BtBlockReq>(other, new BtBlockReq(piece, begin, length)));
                pp.Requesters[b] = null;
            }
            for (int i = 0; i < pp.Blocks; i++) if (pp.State[i] != 2) return BtBlockResult.Accepted;
            _checking.Add(piece);
            return BtBlockResult.PieceComplete;
        }

        // Итог проверки хеша. Неудача — кусок заново, возвращаются адреса всех, кто присылал его блоки (с повторами по блокам).
        public List<string> PieceChecked(int piece, bool ok)
        {
            List<string> from = new List<string>();
            bool wasChecking = _checking.Remove(piece);
            Partial pp;
            if (_partial.TryGetValue(piece, out pp))
            {
                if (!ok) foreach (string f in pp.From) if (f != null) from.Add(f);
                _partial.Remove(piece);
            }
            if (ok && wasChecking && _prio[piece] > 0) _wantedMissing = Math.Max(0, _wantedMissing - 1);
            return from;
        }

        // Все блоки, полученные и запрошенные, сбрасываются (перепроверка, смена хранилища).
        public void Reset()
        {
            _partial.Clear();
            _checking.Clear();
            RecountWanted();
            _orderDirty = true;
            _orderBuilt = long.MinValue;
        }

        // Распределённые копии: минимум наличия плюс доля кусков, которых больше минимума.
        public double DistributedCopies()
        {
            if (_avail.Length == 0) return 0;
            int min = int.MaxValue;
            for (int p = 0; p < _avail.Length; p++) if (_avail[p] < min) min = _avail[p];
            int above = 0;
            for (int p = 0; p < _avail.Length; p++) if (_avail[p] > min) above++;
            return min + _seeds + (double)above / _avail.Length;
        }
    }
}
