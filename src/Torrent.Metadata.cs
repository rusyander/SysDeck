// Windows Process Cleaner — «Загрузки», торренты: обмен метаданными с пирами (BEP 9, ut_metadata) — magnet без .torrent.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Словарь info приходит кусками по 16 КБ от пиров, объявивших metadata_size. Размер — по большинству голосов и не больше
// 16 МБ, буфер один на торрент. Хеш сверяет рой (OnMetadata). Не сошлось — неизвестно, чей кусок битый: единственный
// источник исключается сразу, при нескольких каждому штраф, и дальше метаданные берутся целиком от одного пира, чтобы
// виновник определился следующей попыткой. Отдаём метаданные, только когда они есть и торрент не частный (как libtorrent).
// Отправка пиру — всегда после снятия своей блокировки: соединение может ответить синхронно.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class BtMetadataExt : IBtExtension
    {
        public const int PieceSize = 16 * 1024;
        public const int MaxMetadataSize = 16 * 1024 * 1024;
        internal static int RequestTimeoutSeconds = 15;
        internal const int PauseSeconds = 60;          // пир отказал или промолчал — к нему позже
        internal const int MaxStrikes = 2;
        internal const int ServeWindowSeconds = 60;

        private sealed class PeerState
        {
            public IBtPeerLink Link;
            public string Key;
            public int Size;                           // объявленный metadata_size; 0 — не объявлял
            public int Pending = -1;                   // запрошенный кусок
            public DateTime RequestedUtc;
            public DateTime PauseUntilUtc = DateTime.MinValue;
            public int Served;
            public DateTime ServeWindowUtc = DateTime.MinValue;
        }

        private readonly IBtSwarm _swarm;
        private readonly BtContext _ctx;
        private readonly object _gate = new object();
        private readonly Dictionary<IBtPeerLink, PeerState> _peers = new Dictionary<IBtPeerLink, PeerState>();
        private readonly Dictionary<string, int> _strikes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly HashSet<string> _banned = new HashSet<string>(StringComparer.Ordinal);
        private int _size;
        private byte[] _buffer;                        // выделяется с первым куском
        private bool[] _have;
        private PeerState[] _owner;
        private string[] _source;
        private bool _singleSource;
        private PeerState _single;
        private bool _verifying, _done;
        private DateTime _lastTick = DateTime.MinValue;

        public BtMetadataExt(IBtSwarm swarm, BtContext ctx)
        {
            _swarm = swarm;
            _ctx = ctx;
        }

        public string Name { get { return "ut_metadata"; } }

        // Для тестов и карточки: сколько адресов исключено за неверные метаданные.
        internal int BannedCount { get { lock (_gate) return _banned.Count; } }

        private static bool CanServe(BtMeta m)
        {
            return m != null && m.InfoBytes != null && m.InfoBytes.Length > 0 && m.InfoBytes.Length <= MaxMetadataSize && !m.Private;
        }

        public void FillHandshake(BVal handshake)
        {
            BtMeta m = _swarm.Meta;
            if (handshake == null || handshake.Kind != BKind.Dict || !CanServe(m)) return;
            handshake.Set("metadata_size", BVal.Int(m.InfoBytes.Length));
        }

        public void OnHandshake(IBtPeerLink peer, BVal handshake)
        {
            if (peer == null || handshake == null || handshake.Kind != BKind.Dict) return;
            long size = handshake.GetInt("metadata_size", 0);
            bool supports = peer.Supports(Name);
            bool fetching = _swarm.Meta == null;
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                PeerState p = GetOrAddLocked(peer);
                p.Size = supports && size > 0 && size <= MaxMetadataSize ? (int)size : 0;
                if (fetching) ScheduleLocked(NowLocked(), outbox);
            }
            Flush(outbox);
        }

        public void OnMessage(IBtPeerLink peer, byte[] payload, int offset, int count)
        {
            if (peer == null || payload == null || offset < 0 || count <= 0 || offset + count > payload.Length) return;
            int consumed;
            string err;
            BVal d = Bencode.DecodePrefix(payload, offset, count, out consumed, out err);
            if (d == null || d.Kind != BKind.Dict) return;
            long type = d.GetInt("msg_type", -1);
            long piece = d.GetInt("piece", -1);
            if (piece < 0 || piece >= (MaxMetadataSize + PieceSize - 1) / PieceSize) return;
            if (type == 0) Serve(peer, (int)piece);
            else if (type == 1) OnData(peer, (int)piece, d.GetInt("total_size", -1), payload, offset + consumed, count - consumed);
            else if (type == 2) OnReject(peer, (int)piece);
        }

        public void OnClosed(IBtPeerLink peer)
        {
            if (peer == null) return;
            bool fetching = _swarm.Meta == null;
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                PeerState p;
                if (!_peers.TryGetValue(peer, out p)) return;
                _peers.Remove(peer);
                ReleaseLocked(p);
                if (fetching) ScheduleLocked(NowLocked(), outbox);
            }
            Flush(outbox);
        }

        public void Tick(DateTime utcNow)
        {
            bool fetching = _swarm.Meta == null;
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                _lastTick = utcNow;
                if (!fetching)
                {
                    // Метаданные уже есть (пришли иначе) — буфер больше не нужен.
                    if (!_done) FreeLocked();
                    return;
                }
                foreach (PeerState p in _peers.Values)
                    if (p.Pending >= 0 && (utcNow - p.RequestedUtc).TotalSeconds >= RequestTimeoutSeconds)
                    {
                        ReleaseLocked(p);
                        p.PauseUntilUtc = utcNow.AddSeconds(PauseSeconds);
                    }
                ScheduleLocked(utcNow, outbox);
            }
            Flush(outbox);
        }

        // ------------------------------------------------------------------ //
        //  Раздача
        // ------------------------------------------------------------------ //
        private void Serve(IBtPeerLink peer, int piece)
        {
            BtMeta m = _swarm.Meta;
            bool ok = CanServe(m) && (long)piece * PieceSize < m.InfoBytes.Length;
            if (ok)
                lock (_gate)
                {
                    PeerState p = GetOrAddLocked(peer);
                    DateTime now = NowLocked();
                    if (p.ServeWindowUtc == DateTime.MinValue || (now - p.ServeWindowUtc).TotalSeconds >= ServeWindowSeconds)
                    {
                        p.ServeWindowUtc = now;
                        p.Served = 0;
                    }
                    // Один и тот же кусок по кругу — не повод отдавать мегабайты: не больше двух полных копий в минуту.
                    int pieces = (m.InfoBytes.Length + PieceSize - 1) / PieceSize;
                    if (p.Served >= 2 * pieces + 4) ok = false;
                    else p.Served++;
                }
            BVal head = BVal.NewDict().Set("msg_type", BVal.Int(ok ? 1 : 2)).Set("piece", BVal.Int(piece));
            byte[] reply;
            if (ok)
            {
                head.Set("total_size", BVal.Int(m.InfoBytes.Length));
                byte[] h = Bencode.Encode(head);
                int n = Math.Min(PieceSize, m.InfoBytes.Length - piece * PieceSize);
                reply = new byte[h.Length + n];
                Buffer.BlockCopy(h, 0, reply, 0, h.Length);
                Buffer.BlockCopy(m.InfoBytes, piece * PieceSize, reply, h.Length, n);
            }
            else reply = Bencode.Encode(head);
            try { peer.SendExtended(Name, reply); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // ------------------------------------------------------------------ //
        //  Получение
        // ------------------------------------------------------------------ //
        private void OnData(IBtPeerLink peer, int piece, long total, byte[] data, int offset, int count)
        {
            if (_swarm.Meta != null) return;
            byte[] assembled = null;
            List<string> contributors = null;
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                PeerState p;
                // Не запрошенное у этого пира или не того размера — не наше, молча мимо.
                if (_done || _verifying || _have == null || !_peers.TryGetValue(peer, out p) || p.Pending != piece || total != _size) return;
                DateTime now = NowLocked();
                int expect = Math.Min(PieceSize, _size - piece * PieceSize);
                ReleaseLocked(p);
                if (count != expect)
                {
                    p.PauseUntilUtc = now.AddSeconds(PauseSeconds);
                    if (_single == p) _single = null;
                }
                else
                {
                    if (_buffer == null) _buffer = new byte[_size];
                    Buffer.BlockCopy(data, offset, _buffer, piece * PieceSize, count);
                    _have[piece] = true;
                    _source[piece] = p.Key;
                    if (Array.IndexOf(_have, false) < 0)
                    {
                        assembled = _buffer;
                        contributors = new List<string>();
                        foreach (string k in _source) if (!contributors.Contains(k)) contributors.Add(k);
                        ResetPiecesLocked();
                        _verifying = true;
                    }
                }
                if (assembled == null) ScheduleLocked(now, outbox);
            }
            Flush(outbox);
            if (assembled != null) Verify(assembled, contributors);
        }

        private void Verify(byte[] info, List<string> contributors)
        {
            bool accepted = false;
            try { accepted = _swarm.OnMetadata(info); }
            catch (Exception ex) { DlLog.Report(ex); }
            bool haveMeta = accepted || _swarm.Meta != null;
            string journal = null;
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                _verifying = false;
                if (haveMeta) FreeLocked();
                else
                {
                    int banned = 0;
                    foreach (string key in contributors)
                    {
                        int s;
                        _strikes.TryGetValue(key, out s);
                        _strikes[key] = ++s;
                        if (contributors.Count == 1 || s >= MaxStrikes)
                        {
                            if (_banned.Add(key)) banned++;
                        }
                    }
                    _singleSource = true;
                    _single = null;
                    journal = banned > 0
                        ? Tr.S("Метаданные от пира не совпали с info-hash — пир больше не спрашивается", "Metadata from a peer did not match the info-hash — the peer is no longer asked")
                        : Tr.S("Метаданные от пиров не совпали с info-hash — повтор от одного пира", "Metadata from peers did not match the info-hash — retrying from a single peer");
                    ScheduleLocked(NowLocked(), outbox);
                }
            }
            if (journal != null)
            {
                _swarm.Journal(journal);
                _ctx.Log("bt ut_metadata: hash mismatch, contributors " + contributors.Count);
            }
            Flush(outbox);
        }

        private void OnReject(IBtPeerLink peer, int piece)
        {
            List<KeyValuePair<IBtPeerLink, byte[]>> outbox = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                PeerState p;
                if (_done || !_peers.TryGetValue(peer, out p) || p.Pending != piece) return;
                DateTime now = NowLocked();
                ReleaseLocked(p);
                p.PauseUntilUtc = now.AddSeconds(PauseSeconds);
                ScheduleLocked(now, outbox);
            }
            Flush(outbox);
        }

        private void ScheduleLocked(DateTime now, List<KeyValuePair<IBtPeerLink, byte[]>> outbox)
        {
            if (_done || _verifying) return;
            Dictionary<int, int> votes = new Dictionary<int, int>();
            foreach (PeerState p in _peers.Values)
                if (p.Size > 0 && !_banned.Contains(p.Key))
                {
                    int v;
                    votes.TryGetValue(p.Size, out v);
                    votes[p.Size] = v + 1;
                }
            int best = 0, bestVotes = 0;
            foreach (KeyValuePair<int, int> kv in votes)
                if (kv.Value > bestVotes || (kv.Value == bestVotes && kv.Key == _size))
                {
                    best = kv.Key;
                    bestVotes = kv.Value;
                }
            if (best == 0) return;
            if (best != _size)
            {
                _size = best;
                ResetPiecesLocked();
            }

            if (_singleSource)
            {
                if (_single != null && (!_peers.ContainsKey(_single.Link) || _banned.Contains(_single.Key) || _single.Size != _size || _single.PauseUntilUtc > now))
                    _single = null;
                if (_single == null)
                {
                    PeerState pick = null;
                    int pickStrikes = int.MaxValue;
                    foreach (PeerState p in _peers.Values)
                    {
                        if (!Eligible(p, now)) continue;
                        int s;
                        _strikes.TryGetValue(p.Key, out s);
                        if (s < pickStrikes)
                        {
                            pick = p;
                            pickStrikes = s;
                        }
                    }
                    if (pick == null) return;
                    // Новый единственный источник — начатое другими выбрасывается, иначе виновника снова не отличить.
                    ResetPiecesLocked();
                    _single = pick;
                }
                if (_single.Pending < 0) RequestNextLocked(_single, now, outbox);
                return;
            }
            foreach (PeerState p in _peers.Values)
                if (Eligible(p, now) && p.Pending < 0 && !RequestNextLocked(p, now, outbox)) break;
        }

        private bool Eligible(PeerState p, DateTime now)
        {
            return p.Size == _size && p.PauseUntilUtc <= now && !_banned.Contains(p.Key);
        }

        private bool RequestNextLocked(PeerState p, DateTime now, List<KeyValuePair<IBtPeerLink, byte[]>> outbox)
        {
            for (int i = 0; i < _have.Length; i++)
            {
                if (_have[i] || _owner[i] != null) continue;
                _owner[i] = p;
                p.Pending = i;
                p.RequestedUtc = now;
                BVal req = BVal.NewDict().Set("msg_type", BVal.Int(0)).Set("piece", BVal.Int(i));
                outbox.Add(new KeyValuePair<IBtPeerLink, byte[]>(p.Link, Bencode.Encode(req)));
                return true;
            }
            return false;
        }

        private void ReleaseLocked(PeerState p)
        {
            if (p.Pending >= 0 && _owner != null && p.Pending < _owner.Length && _owner[p.Pending] == p) _owner[p.Pending] = null;
            p.Pending = -1;
            if (_single == p && !_peers.ContainsKey(p.Link)) _single = null;
        }

        private void ResetPiecesLocked()
        {
            int n = (_size + PieceSize - 1) / PieceSize;
            _buffer = null;
            _have = new bool[n];
            _owner = new PeerState[n];
            _source = new string[n];
            foreach (PeerState p in _peers.Values) p.Pending = -1;
        }

        private void FreeLocked()
        {
            _done = true;
            _buffer = null;
            _have = null;
            _owner = null;
            _source = null;
            _single = null;
            foreach (PeerState p in _peers.Values) p.Pending = -1;
        }

        private PeerState GetOrAddLocked(IBtPeerLink peer)
        {
            PeerState p;
            if (!_peers.TryGetValue(peer, out p))
            {
                p = new PeerState();
                p.Link = peer;
                // Штраф и исключение — по IP: переподключение не обнуляет вину.
                BtEndpoint ep = peer.Endpoint;
                p.Key = ep != null ? ep.Address.ToString() : "link-" + RuntimeHelpers.GetHashCode(peer);
                _peers[peer] = p;
            }
            return p;
        }

        private DateTime NowLocked()
        {
            return _lastTick != DateTime.MinValue ? _lastTick : DateTime.UtcNow;
        }

        private void Flush(List<KeyValuePair<IBtPeerLink, byte[]>> outbox)
        {
            foreach (KeyValuePair<IBtPeerLink, byte[]> kv in outbox)
            {
                try { kv.Key.SendExtended(Name, kv.Value); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }
    }
}
