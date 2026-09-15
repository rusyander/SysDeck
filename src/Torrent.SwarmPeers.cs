// SysDeck — область «torrent»: рой — пиры, блоки, проверка частей, такт и choke.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed partial class BtTorrent : IBtSwarm
    {
        // ---------- пиры ----------
        private List<BtPeer> PeersCopy()
        {
            lock (_gate) return new List<BtPeer>(_peers);
        }

        private void ClosePeers(string reason)
        {
            foreach (BtPeer p in PeersCopy()) p.Close(reason);
        }

        internal string RefuseIncoming(BtPeer p)
        {
            if (!Active) return Tr.S("торрент не активен", "the torrent is not active");
            lock (_gate)
            {
                if (_banned.Contains(p.BanKey)) return Tr.S("адрес заблокирован", "the address is banned");
                if (_peers.Count >= _s.Options.MaxConnectionsPerTorrent) return Tr.S("предел соединений торрента", "the torrent connection limit");
            }
            return null;
        }

        internal void Attach(BtPeer p)
        {
            lock (_gate) _peers.Add(p);
        }

        internal bool HasPeerId(byte[] peerId, BtPeer except)
        {
            lock (_gate)
                foreach (BtPeer p in _peers)
                    if (p != except && !p.Dead && p.PeerId != null && Bencode.SameBytes(p.PeerId, peerId)) return true;
            return false;
        }

        internal byte[] BuildExtHandshake(BtPeer p)
        {
            List<IBtExtension> live = new List<IBtExtension>();
            foreach (IBtExtension e in _exts) if (e != null) live.Add(e);
            return BtExtHandshake.Build(_extNames, _ctx.InboundOpen ? _ctx.Port : 0, p.Remote.Address, live);
        }

        internal IBtExtension ExtensionById(int id)
        {
            return id >= 1 && id <= _exts.Count ? _exts[id - 1] : null;
        }

        internal void OnPeerActive(BtPeer p)
        {
            BtPicker picker = _picker;
            if (picker != null) p.RegisterBits(picker);
            if (p.IsOutgoing && p.Candidate != null) p.Candidate.Fails = 0;
        }

        internal void OnExtHandshake(BtPeer p, BtExtHandshake h)
        {
            if (!p.IsOutgoing && h.ListenPort > 0 && p.Candidate == null)
            {
                BtEndpoint ep = new BtEndpoint(p.Remote.Address, h.ListenPort);
                lock (_gate)
                {
                    BtCandidate c;
                    if (!_cands.TryGetValue(ep, out c) && ep.IsUsable && _cands.Count < MaxCandidates)
                    {
                        c = new BtCandidate();
                        c.Endpoint = ep;
                        c.Origin = BtPeerOrigin.Incoming;
                        _cands[ep] = c;
                    }
                    if (c != null && c.Peer == null)
                    {
                        c.Peer = p;
                        p.Candidate = c;
                    }
                }
            }
            foreach (IBtExtension e in _exts)
                if (e != null)
                {
                    IBtExtension ext = e;
                    Safe(delegate { ext.OnHandshake(p, h.Root); });
                }
        }

        internal void OnPeerInterest(BtPeer p, long now)
        {
            if (!CanUpload) return;
            if (p.PeerInterested && p.AmChoking)
            {
                int open = 0;
                foreach (BtPeer q in PeersCopy()) if (!q.AmChoking && !q.Dead) open++;
                if (open < _s.Options.UploadSlots + 1) p.SetChoke(false, now);
            }
            else if (!p.PeerInterested && !p.AmChoking)
            {
                p.SetChoke(true, now);
                _rechokeSoon = true;
            }
        }

        internal void OnPeerBecameSeed(BtPeer p)
        {
            BtBitfield have = _have;
            if (have != null && have.All)
            {
                p.BothSeeds = true;
                p.Close(Tr.S("оба — сиды", "both are seeds"));
            }
        }

        internal void OnPeerClosed(BtPeer p, string reason)
        {
            lock (_gate)
            {
                _peers.Remove(p);
                BtCandidate c = p.Candidate;
                if (c != null && c.Peer == p)
                {
                    c.Peer = null;
                    long now = BtNetClock.Ms;
                    if (p.CryptoFailed && _ctx.Encryption == BtEncryption.Prefer && !p.PlainOnly)
                    {
                        c.PlainOnly = true;
                        c.NextTryMs = now;
                    }
                    else if (p.PeerId != null)
                        c.NextTryMs = now + (p.BothSeeds ? 600000 : 30000);
                    else
                    {
                        c.Fails++;
                        c.NextTryMs = now + Math.Min(300000L, 5000L << Math.Min(c.Fails, 6));
                        if (c.Fails > 8 && c.Origin != BtPeerOrigin.Manual) _cands.Remove(c.Endpoint);
                    }
                }
            }
            if (p.PeerId != null)
                foreach (IBtExtension e in _exts)
                    if (e != null)
                    {
                        IBtExtension ext = e;
                        Safe(delegate { ext.OnClosed(p); });
                    }
            if (!p.AmChoking) _rechokeSoon = true;
        }

        private void ConnectCandidates(long now)
        {
            BtTorrentState st = State;
            if (_removed || (st != BtTorrentState.Downloading && st != BtTorrentState.Seeding && st != BtTorrentState.FetchingMetadata)) return;
            if (_ctx.Encryption == BtEncryption.Require && BtFactories.MseOutgoing == null) return;
            List<BtCandidate> ready = new List<BtCandidate>();
            lock (_gate)
            {
                int room = _s.Options.MaxConnectionsPerTorrent - _peers.Count;
                foreach (BtCandidate c in _cands.Values)
                {
                    if (ready.Count >= room) break;
                    if (c.Peer != null || c.NextTryMs > now || _banned.Contains(c.Endpoint.Address.ToString())) continue;
                    ready.Add(c);
                }
            }
            foreach (BtCandidate c in ready)
            {
                if (!_s.CanOpen()) break;
                bool connected;
                string error;
                System.Net.Sockets.Socket sock = BtReactor.BeginConnect(c.Endpoint, out connected, out error);
                lock (_gate)
                {
                    if (sock == null)
                    {
                        c.Fails++;
                        c.NextTryMs = now + Math.Min(300000L, 5000L << Math.Min(c.Fails, 6));
                        continue;
                    }
                    BtPeer p = new BtPeer(_s, sock, c.Endpoint, true, this, c.Origin, c.PlainOnly || _ctx.Encryption == BtEncryption.Off);
                    p.Candidate = c;
                    c.Peer = p;
                    _peers.Add(p);
                    _s.Reactor.Add(p);
                }
            }
        }

        // ---------- блоки и куски ----------
        internal void OnBlock(BtPeer p, int piece, int begin, byte[] buf, int at, int count)
        {
            Interlocked.Add(ref _down, count);
            BtPicker picker = _picker;
            BtStorage st = _storage;
            if (picker == null || st == null || !CanUpload)
            {
                Interlocked.Add(ref _wasted, count);
                return;
            }
            List<KeyValuePair<IBtPickPeer, BtBlockReq>> cancels = new List<KeyValuePair<IBtPickPeer, BtBlockReq>>();
            BtBlockResult res = picker.OnBlock(p, piece, begin, count, cancels);
            foreach (KeyValuePair<IBtPickPeer, BtBlockReq> kv in cancels)
            {
                BtPeer other = kv.Key as BtPeer;
                if (other != null) other.CancelRequest(kv.Value);
            }
            if (res == BtBlockResult.Duplicate || res == BtBlockResult.Invalid)
            {
                Interlocked.Add(ref _wasted, count);
                return;
            }
            byte[] copy = new byte[count];
            Buffer.BlockCopy(buf, at, copy, 0, count);
            int gen = _checkGen;
            _s.Disk.Write(st, piece, begin, copy, 0, count, delegate(string why)
            {
                if (why != null) _s.Reactor.Post(delegate { if (gen == _checkGen && !_removed) Fail(why); });
            });
            if (res != BtBlockResult.PieceComplete) return;
            BtBitfield have = _have;
            _s.Disk.Check(st, piece, have, delegate(bool ok, List<string> errors)
            {
                _s.Reactor.Post(delegate { OnPieceChecked(gen, piece, ok, errors); });
            });
        }

        private void OnPieceChecked(int gen, int piece, bool ok, List<string> errors)
        {
            BtPicker picker = _picker;
            if (gen != _checkGen || picker == null || _removed) return;
            if (!ok)
            {
                List<string> from = picker.PieceChecked(piece, false);
                Interlocked.Add(ref _wasted, _meta.PieceSize(piece));
                Journal(Tr.S("кусок ", "piece ") + piece + Tr.S(" не прошёл проверку хеша", " failed the hash check"));
                OnHashFail(from);
                return;
            }
            picker.PieceChecked(piece, true);
            long all, wanted;
            PieceBytes(piece, out all, out wanted);
            Interlocked.Add(ref _doneBytes, all);
            Interlocked.Add(ref _wantedDone, wanted);
            byte[] haveMsg = BtWire.WithIndex(BtWire.Have, piece);
            foreach (BtPeer p in PeersCopy())
                if (!p.Dead && p.Phase == BtPeerPhase.Active && !p.HasPiece(piece)) p.SendFrame(haveMsg);
            if (errors != null && errors.Count > 0) { Fail(errors[0]); return; }
            BtStorage st = _storage;
            foreach (int fi in _meta.FilesOfPiece(piece))
                if (!_fileRaised[fi] && st.IsFileDone(fi))
                {
                    _fileRaised[fi] = true;
                    Action<BtTorrent, int> cb = FileCompleted;
                    int file = fi;
                    if (cb != null) ThreadPool.QueueUserWorkItem(delegate { try { cb(this, file); } catch (Exception ex) { DlLog.Report(ex); } });
                }
            _interestDirty = true;
            if (picker.WantedMissing == 0) OnWantedComplete();
        }

        private void OnHashFail(List<string> from)
        {
            HashSet<string> who = new HashSet<string>(from, StringComparer.Ordinal);
            List<string> ban = new List<string>();
            lock (_gate)
            {
                foreach (string key in who)
                {
                    if (_banned.Contains(key)) continue;         // блоки, пришедшие до блокировки, — не повод штрафовать снова
                    int n;
                    _strikes.TryGetValue(key, out n);
                    _strikes[key] = ++n;
                    if (who.Count == 1 || n >= MaxStrikes) ban.Add(key);
                }
                foreach (string key in ban)
                {
                    _banned.Add(key);
                    List<BtEndpoint> drop = new List<BtEndpoint>();
                    foreach (BtEndpoint ep in _cands.Keys) if (ep.Address.ToString() == key) drop.Add(ep);
                    foreach (BtEndpoint ep in drop) _cands.Remove(ep);
                }
            }
            foreach (string key in ban)
            {
                Journal(Tr.S("пир ", "peer ") + key + Tr.S(" заблокирован: присылал испорченные данные", " is banned: it sent corrupt data"));
                foreach (BtPeer p in PeersCopy()) if (p.BanKey == key) p.Close(Tr.S("заблокирован за испорченные данные", "banned for corrupt data"));
            }
        }

        private void OnWantedComplete()
        {
            BtBitfield have = _have;
            if (!_completedRaised)
            {
                _completedRaised = true;
                Journal(Tr.S("загрузка завершена", "download complete"));
                Raise(Completed);
                if (_trackers != null && have != null && have.All) Safe(delegate { _trackers.Announce(BtAnnounceEvent.Completed); });
            }
            SetState(BtTorrentState.Seeding);
            foreach (BtPeer p in PeersCopy())
            {
                if (p.Dead || p.Phase != BtPeerPhase.Active) continue;
                p.SetInterested(false);
                if (p.SeedFlag && have != null && have.All)
                {
                    p.BothSeeds = true;
                    p.Close(Tr.S("оба — сиды", "both are seeds"));
                }
            }
        }

        internal void OnReadError(string why)
        {
            Fail(Tr.S("не удалось прочитать данные для раздачи: ", "could not read data to upload: ") + why);
        }

        internal void AddUploaded(int n) { Interlocked.Add(ref _up, n); }

        // ---------- таймер (поток реактора, ~10 раз в секунду) ----------
        internal void Tick(long now)
        {
            if (_removed) return;
            if (DownBucket.Rate != DownLimit) DownBucket.Rate = DownLimit;
            if (UpBucket.Rate != UpLimit) UpBucket.Rate = UpLimit;
            List<BtPeer> peers = PeersCopy();
            if (now - _lastSample >= 500)
            {
                _lastSample = now;
                long down = 0, up = 0;
                int active = 0, seeds = 0;
                foreach (BtPeer p in peers)
                {
                    p.SampleRates();
                    down += p.DownBps;
                    up += p.UpBps;
                    if (p.Phase == BtPeerPhase.Active && !p.Dead) { active++; if (p.SeedFlag) seeds++; }
                }
                Interlocked.Exchange(ref _downBps, down);
                Interlocked.Exchange(ref _upBps, up);
                _peerCount = active;
                _seedCount = seeds;
            }
            if (now - _lastSecond < 1000) return;
            long delta = _lastSecond == 0 ? 1000 : Math.Min(5000, now - _lastSecond);
            _lastSecond = now;
            BtTorrentState st = State;
            if (st == BtTorrentState.Downloading || st == BtTorrentState.Seeding || st == BtTorrentState.FetchingMetadata) Interlocked.Add(ref _activeMs, delta);
            if (st == BtTorrentState.Seeding) Interlocked.Add(ref _seedMs, delta);
            BtPicker picker = _picker;
            if (picker != null) _availability = picker.DistributedCopies();

            bool refreshInterest = _interestDirty || now - _lastInterest >= 5000;
            if (refreshInterest) { _interestDirty = false; _lastInterest = now; }
            foreach (BtPeer p in peers)
            {
                p.Tick(now);
                if (p.Dead || p.Phase != BtPeerPhase.Active) continue;
                if (refreshInterest) p.UpdateInterest();
                p.FillRequests(now);
                p.ServeUploads();
            }
            if (CanUpload && (_rechokeSoon || now - _lastChoke >= BtChoker.RechokeMs)) Rechoke(now);
            ConnectCandidates(now);
            CheckLimits();
            DateTime utc = _ctx.Env != null ? _ctx.Env.UtcNow : DateTime.UtcNow;
            IBtTrackers trackers = _trackers;
            if (trackers != null && Active) Safe(delegate { trackers.Tick(utc); });
            foreach (IBtExtension e in _exts)
                if (e != null)
                {
                    IBtExtension ext = e;
                    Safe(delegate { ext.Tick(utc); });
                }
            if (Active && !IsPrivate)
            {
                if (_ctx.Dht != null && (_lastDht == long.MinValue || now - _lastDht >= 15 * 60000) && NumWant > 0)
                {
                    _lastDht = now;
                    Safe(delegate { _ctx.Dht.GetPeers(this, _ctx.InboundOpen); });
                }
                if (_ctx.Lsd != null && (_lastLsd == long.MinValue || now - _lastLsd >= 5 * 60000))
                {
                    _lastLsd = now;
                    Safe(delegate { _ctx.Lsd.Announce(this); });
                }
            }
        }

        private void Rechoke(long now)
        {
            _rechokeSoon = false;
            _lastChoke = now;
            bool rotate = now - _lastOptimistic >= BtChoker.OptimisticMs;
            if (rotate) _lastOptimistic = now;
            List<BtChokeInfo> infos = new List<BtChokeInfo>();
            foreach (BtPeer p in PeersCopy())
            {
                if (p.Dead || p.Phase != BtPeerPhase.Active) continue;
                BtChokeInfo i = new BtChokeInfo();
                i.Key = p;
                i.Interested = p.PeerInterested;
                i.IsSeed = p.SeedFlag;
                i.Snubbed = p.Snubbed;
                i.Unchoked = !p.AmChoking;
                i.Optimistic = p.Optimistic;
                i.DownBps = p.DownBps;
                i.UpBps = p.UpBps;
                i.UnchokedAtMs = p.UnchokedAtMs;
                infos.Add(i);
            }
            BtChoker.Decide(infos, _s.Options.UploadSlots, State == BtTorrentState.Seeding, now, rotate, _rng);
            foreach (BtChokeInfo i in infos)
            {
                BtPeer p = (BtPeer)i.Key;
                p.SetChoke(!i.Unchoke, now);
                p.Optimistic = i.OptimisticNext;
            }
        }

        // Предел раздачи: рейтинг от скачанного (или от готового объёма, если раздаём своё) и время раздачи.
        private void CheckLimits()
        {
            if (State != BtTorrentState.Seeding) return;
            BtTorrentStats s = Stats();
            bool ratio = RatioLimit > 0 && s.Uploaded >= RatioLimit * Math.Max(1, Math.Max(s.Downloaded, Interlocked.Read(ref _doneBytes)));
            bool time = SeedTimeLimitSeconds > 0 && s.SeedSeconds >= SeedTimeLimitSeconds;
            if (!ratio && !time) return;
            Journal(ratio ? Tr.S("раздача остановлена: достигнут рейтинг", "seeding stopped: the ratio limit is reached")
                          : Tr.S("раздача остановлена: истекло время раздачи", "seeding stopped: the seeding time limit is reached"));
            SetState(BtTorrentState.Finished);
            foreach (BtPeer p in PeersCopy()) p.CloseAfterFlush(Tr.S("раздача завершена", "seeding finished"), FinishFlushMs);
            if (_trackers != null) Safe(delegate { _trackers.Announce(BtAnnounceEvent.Stopped); });
        }
    }
}
