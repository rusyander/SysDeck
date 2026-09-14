// Windows Process Cleaner — «Загрузки», торренты: рой одного торрента — состояние, пиры, проверка кусков, раздача.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Изменяется в потоке реактора; публичные методы из других потоков передают работу туда через Post. Диск (открытие
// хранилища, выбор имени, проверка, возобновление) — только в потоке BtDisk. Обратные вызовы для движка — в пуле потоков.
// Кусок считается скачанным только после проверки хеша на диске: до неё пирам не объявляется и не раздаётся.
// Испорченный кусок: каждый приславший его блоки получает штраф; единственный автор куска или три штрафа — адрес
// блокируется до конца сессии (по IP: сменой порта обойти нельзя).
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class BtCandidate
    {
        public BtEndpoint Endpoint;
        public BtPeerOrigin Origin;
        public int Fails;
        public long NextTryMs;
        public BtPeer Peer;
        public bool PlainOnly;
    }

    internal sealed class BtTorrent : IBtSwarm
    {
        public const int MaxCandidates = 2000;
        public const int MaxStrikes = 3;
        public const int FinishFlushMs = 5000;

        private readonly BtSession _s;
        private readonly BtContext _ctx;
        private readonly byte[] _hash;
        private readonly byte[] _req2;
        private readonly byte[] _expectedHash;
        private readonly object _gate = new object();
        private volatile BtMeta _meta;
        private bool _metaPending, _v2Only;
        private readonly string _folder;
        private string _rootName;
        private readonly Func<BtMeta, string> _chooseRoot;
        private volatile BtStorage _storage;
        private volatile BtBitfield _have;
        private volatile BtPicker _picker;
        private int[] _filePrio;
        private BtResume _pendingResume;
        private volatile int _state;
        private volatile string _error = "";
        private volatile bool _paused, _removed, _sequential;
        private readonly bool _metadataOnly;
        private int _checkGen;
        private volatile int _checkedPieces;

        private readonly List<BtPeer> _peers = new List<BtPeer>();
        private readonly Dictionary<BtEndpoint, BtCandidate> _cands = new Dictionary<BtEndpoint, BtCandidate>();
        private readonly HashSet<string> _banned = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _strikes = new Dictionary<string, int>(StringComparer.Ordinal);
        private readonly IList<List<string>> _trackerTiers;
        private IBtTrackers _trackers;
        private readonly List<IBtExtension> _exts = new List<IBtExtension>();
        private readonly List<string> _extNames = new List<string>();

        internal readonly DlTokenBucket DownBucket = new DlTokenBucket(0), UpBucket = new DlTokenBucket(0);
        private long _down, _up, _wasted;
        private readonly long _baseDown, _baseUp, _baseWasted, _baseSeedSec, _baseActiveSec;
        private long _seedMs, _activeMs;
        private long _doneBytes, _wantedBytes;
        private bool _completedRaised;
        private bool[] _fileRaised;
        private volatile int _peerCount, _seedCount;
        private long _downBps, _upBps;
        private double _availability;
        private long _lastSecond, _lastSample, _lastChoke, _lastOptimistic, _lastInterest, _lastDht = long.MinValue, _lastLsd = long.MinValue;
        private bool _rechokeSoon, _interestDirty;
        private readonly Random _rng = new Random();

        // Настройки и обработчики задаёт движок (Torrent.Wiring.cs); в срезе без него присваиваний нет.
#pragma warning disable 649
        public long DownLimit, UpLimit;          // байт/с; 0 — без ограничения
        public double RatioLimit;                // 0 — без предела
        public long SeedTimeLimitSeconds;        // 0 — без предела
        public Action<BtTorrent> MetadataReceived, Completed, StateChanged;
        public Action<BtTorrent, int> FileCompleted;
        public Action<BtTorrent, string> Journaled;
#pragma warning restore 649

        internal BtTorrent(BtSession session, BtAddParams p, byte[] swarmHash)
        {
            _s = session;
            _ctx = session.Context;
            _hash = swarmHash;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] pre = Encoding.ASCII.GetBytes("req2");
                byte[] x = new byte[pre.Length + 20];
                Buffer.BlockCopy(pre, 0, x, 0, pre.Length);
                Buffer.BlockCopy(swarmHash, 0, x, pre.Length, 20);
                _req2 = sha.ComputeHash(x);
            }
            _meta = p.Meta;
            _folder = p.Folder ?? "";
            _rootName = p.RootName;
            if (string.IsNullOrEmpty(_rootName) && p.Resume != null && !string.IsNullOrEmpty(p.Resume.RootName)) _rootName = p.Resume.RootName;
            _chooseRoot = p.ChooseRootName;
            _pendingResume = p.Resume;
            _paused = p.Paused;
            _sequential = p.Sequential;
            _metadataOnly = p.MetadataOnly;
            _filePrio = p.Priorities != null ? (int[])p.Priorities.Clone()
                      : p.Resume != null && p.Resume.Priorities != null && p.Resume.Priorities.Length > 0 ? (int[])p.Resume.Priorities.Clone() : null;
            if (p.Resume != null)
            {
                _baseDown = p.Resume.Downloaded;
                _baseUp = p.Resume.Uploaded;
                _baseWasted = p.Resume.Wasted;
                _baseSeedSec = p.Resume.SeedSeconds;
                _baseActiveSec = p.Resume.ActiveSeconds;
            }
            if (p.Magnet != null) _expectedHash = p.Magnet.InfoHash ?? p.Magnet.InfoHashV2;
            _state = (int)(p.Paused ? BtTorrentState.Paused : _meta == null ? BtTorrentState.FetchingMetadata : BtTorrentState.Checking);

            List<List<string>> tiers = new List<List<string>>();
            if (p.Meta != null) foreach (List<string> t in p.Meta.Trackers) tiers.Add(new List<string>(t));
            if (p.Magnet != null) foreach (string u in p.Magnet.Trackers) if (BtMeta.IsTrackerUrl(u)) tiers.Add(new List<string> { u });
            if (p.ExtraTrackers != null) foreach (List<string> t in p.ExtraTrackers) if (t != null && t.Count > 0) tiers.Add(new List<string>(t));
            _trackerTiers = tiers;

            foreach (Func<IBtSwarm, BtContext, IBtExtension> f in BtFactories.Extensions)
            {
                IBtExtension e = null;
                try { e = f == null ? null : f(this, _ctx); }
                catch (Exception ex) { DlLog.Report(ex); }
                _exts.Add(e);
                _extNames.Add(e == null ? null : e.Name);
            }
            if (p.Peers != null) AddPeers(p.Peers, BtPeerOrigin.Manual);
            if (p.Magnet != null)
            {
                List<BtEndpoint> pe = new List<BtEndpoint>();
                foreach (string s in p.Magnet.Peers)
                {
                    BtEndpoint ep = BtEndpoint.TryParse(s);
                    if (ep != null) pe.Add(ep);
                }
                AddPeers(pe, BtPeerOrigin.Manual);
            }
        }

        // ---------- IBtSwarm ----------
        public byte[] InfoHash { get { return _hash; } }
        public bool IsPrivate { get { BtMeta m = _meta; return m != null && m.Private; } }
        public BtMeta Meta { get { return _meta; } }
        public long Uploaded { get { return Interlocked.Read(ref _up); } }
        public long Downloaded { get { return Interlocked.Read(ref _down); } }

        public long Left
        {
            get
            {
                BtMeta m = _meta;
                if (m == null) return 0;
                return Math.Max(0, m.TotalSize - Interlocked.Read(ref _doneBytes));
            }
        }

        public bool IsSeed { get { BtBitfield h = _have; return h != null && h.All; } }

        public int NumWant
        {
            get
            {
                int cap = _s.Options.MaxConnectionsPerTorrent;
                lock (_gate)
                {
                    int idle = _cands.Count - _peers.Count;
                    return idle >= 2 * cap ? 0 : Math.Min(50, cap);
                }
            }
        }

        public bool Active
        {
            get
            {
                BtTorrentState st = State;
                return st == BtTorrentState.FetchingMetadata || st == BtTorrentState.Downloading || st == BtTorrentState.Seeding;
            }
        }

        public void AddPeers(IList<BtEndpoint> peers, BtPeerOrigin origin)
        {
            if (peers == null) return;
            lock (_gate)
                foreach (BtEndpoint ep in peers)
                {
                    if (ep == null || !ep.IsUsable || _banned.Contains(ep.Address.ToString())) continue;
                    if (ep.Port == _ctx.Port && (IPAddress.IsLoopback(ep.Address) || ep.Address.Equals(_ctx.ExternalAddress))) continue;
                    BtCandidate c;
                    if (_cands.TryGetValue(ep, out c))
                    {
                        if (origin == BtPeerOrigin.Manual) { c.NextTryMs = 0; c.Fails = 0; }
                        continue;
                    }
                    if (_cands.Count >= MaxCandidates && !EvictOne()) break;
                    c = new BtCandidate();
                    c.Endpoint = ep;
                    c.Origin = origin;
                    _cands[ep] = c;
                }
        }

        private bool EvictOne()
        {
            BtEndpoint worst = null;
            int fails = 0;
            foreach (KeyValuePair<BtEndpoint, BtCandidate> kv in _cands)
                if (kv.Value.Peer == null && kv.Value.Fails >= fails) { worst = kv.Key; fails = kv.Value.Fails; }
            if (worst == null) return false;
            _cands.Remove(worst);
            return true;
        }

        public List<BtPeerInfo> ConnectedPeers()
        {
            List<BtPeerInfo> list = new List<BtPeerInfo>();
            lock (_gate)
                foreach (BtPeer p in _peers)
                    if (!p.Dead && p.Phase == BtPeerPhase.Active) list.Add(p.Info());
            return list;
        }

        public bool OnMetadata(byte[] infoBytes)
        {
            if (infoBytes == null) return false;
            lock (_gate) if (_meta != null || _metaPending || _removed) return false;
            string error;
            BtMeta m = BtMeta.FromInfo(infoBytes, _expectedHash ?? _hash, out error);
            if (m == null)
            {
                if (IsAuthenticV2Only(infoBytes))
                {
                    lock (_gate)
                    {
                        if (_meta != null || _metaPending || _removed) return false;
                        _metaPending = true;             // больше не запрашивать: без слоёв хешей торрент не скачать
                        _v2Only = true;
                    }
                    _s.Reactor.Post(delegate { if (!_removed) Fail(V2Unsupported); });
                    return true;
                }
                Journal(Tr.S("метаданные от пиров не подошли: ", "metadata from peers was rejected: ") + error);
                return false;
            }
            lock (_gate)
            {
                if (_meta != null || _metaPending) return false;
                _metaPending = true;
            }
            _s.Reactor.Post(delegate { OnMetadataReady(m); });
            return true;
        }

        private static string V2Unsupported { get { return Tr.S("нужны слои хешей v2, пока не поддерживается", "v2 piece layers are needed, not supported yet"); } }

        // Словарь info чистого v2 из magnet: подлинный (SHA-256 сошёлся с хешем ссылки), но v1-хешей кусков в нём нет,
        // а слои хешей v2 по ut_metadata не приходят.
        private bool IsAuthenticV2Only(byte[] infoBytes)
        {
            byte[] h;
            using (SHA256 sha = SHA256.Create()) h = sha.ComputeHash(infoBytes);
            bool match;
            if (_expectedHash != null && _expectedHash.Length == 32) match = Bencode.SameBytes(h, _expectedHash);
            else
            {
                byte[] h20 = new byte[20];
                Buffer.BlockCopy(h, 0, h20, 0, 20);
                match = Bencode.SameBytes(h20, _hash);
            }
            if (!match) return false;
            string error;
            BVal info = Bencode.Decode(infoBytes, out error);
            return info != null && info.Kind == BKind.Dict && info.Get("pieces") == null && info.GetInt("meta version", 1) == 2;
        }

        public void Journal(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            try { _ctx.Log(text); }
            catch (Exception ex) { DlLog.Report(ex); }
            Action<BtTorrent, string> cb = Journaled;
            if (cb != null) ThreadPool.QueueUserWorkItem(delegate { try { cb(this, text); } catch (Exception ex) { DlLog.Report(ex); } });
        }

        // ---------- состояние для движка ----------
        public BtTorrentState State { get { return (BtTorrentState)_state; } }
        public string Error { get { return _error; } }
        public BtStorage Storage { get { return _storage; } }
        public BtBitfield Have { get { return _have; } }
        internal BtPicker Picker { get { return _picker; } }
        internal BtSession Session { get { return _s; } }
        internal byte[] Req2Hash { get { return _req2; } }
        internal bool IsDownloading { get { return State == BtTorrentState.Downloading; } }
        internal bool CanUpload { get { BtTorrentState st = State; return st == BtTorrentState.Downloading || st == BtTorrentState.Seeding; } }

        public bool Sequential
        {
            get { return _sequential; }
            set
            {
                _sequential = value;
                _s.Reactor.Post(delegate { BtPicker pk = _picker; if (pk != null) pk.Sequential = value; });
            }
        }

        internal bool IsBanned(IPAddress address)
        {
            lock (_gate) return address != null && _banned.Contains(address.ToString());
        }

        public BtTorrentStats Stats()
        {
            BtTorrentStats s = new BtTorrentStats();
            BtMeta meta = _meta;
            BtBitfield have = _have;
            BtPicker picker = _picker;
            s.DownBps = Interlocked.Read(ref _downBps);
            s.UpBps = Interlocked.Read(ref _upBps);
            s.Downloaded = _baseDown + Downloaded;
            s.Uploaded = _baseUp + Uploaded;
            s.Wasted = _baseWasted + Interlocked.Read(ref _wasted);
            s.Wanted = Interlocked.Read(ref _wantedBytes);
            s.Done = Interlocked.Read(ref _wantedDone);
            s.Progress = s.Wanted > 0 ? Math.Min(1.0, (double)s.Done / s.Wanted) : picker != null && picker.WantedMissing == 0 ? 1 : 0;
            s.Availability = _availability;
            s.CheckProgress = State == BtTorrentState.Checking && meta != null && meta.PieceCount > 0 ? (double)_checkedPieces / meta.PieceCount : 0;
            s.Peers = _peerCount;
            s.Seeds = _seedCount;
            s.PiecesHave = have == null ? 0 : have.SetCount;
            s.PieceCount = meta == null ? 0 : meta.PieceCount;
            s.SeedSeconds = _baseSeedSec + Interlocked.Read(ref _seedMs) / 1000;
            s.ActiveSeconds = _baseActiveSec + Interlocked.Read(ref _activeMs) / 1000;
            long left = s.Wanted - s.Done;
            s.EtaSeconds = left <= 0 && picker != null ? 0 : s.DownBps > 0 ? left / s.DownBps : -1;
            return s;
        }

        private long _wantedDone;

        public List<BtTrackerInfo> Trackers()
        {
            IBtTrackers t = _trackers;
            if (t == null) return new List<BtTrackerInfo>();
            try { return t.Snapshot(); }
            catch (Exception ex) { DlLog.Report(ex); return new List<BtTrackerInfo>(); }
        }

        // Снимок для возобновления: сначала битовое поле, потом сброс файлов — в снимке нет кусков, чьи данные не на диске.
        public BtResume CaptureResume()
        {
            BtStorage st = _storage;
            BtBitfield have = _have;
            if (st == null || have == null) return _pendingResume;
            BtBitfield snap = have.Clone();
            st.FlushAll();
            BtResume r = BtResume.Capture(st, snap, _filePrio);
            r.Downloaded = _baseDown + Downloaded;
            r.Uploaded = _baseUp + Uploaded;
            r.Wasted = _baseWasted + Interlocked.Read(ref _wasted);
            r.SeedSeconds = _baseSeedSec + Interlocked.Read(ref _seedMs) / 1000;
            r.ActiveSeconds = _baseActiveSec + Interlocked.Read(ref _activeMs) / 1000;
            return r;
        }

        // ---------- команды (любой поток) ----------
        public void Pause() { _paused = true; _s.Reactor.Post(DoPause); }
        public void Resume() { _paused = false; _s.Reactor.Post(DoResume); }
        public void Recheck() { _s.Reactor.Post(delegate { if (!_removed && _meta != null) BeginCheck(true, true); }); }

        public void Reannounce()
        {
            _s.Reactor.Post(delegate
            {
                if (_trackers != null && Active) Safe(delegate { _trackers.Reannounce(); });
                _lastDht = long.MinValue;
            });
        }

        public void AddPeer(BtEndpoint ep)
        {
            if (ep == null) return;
            AddPeers(new List<BtEndpoint> { ep }, BtPeerOrigin.Manual);
            _s.Reactor.Post(delegate { ConnectCandidates(BtNetClock.Ms); });
        }

        public void SetPriorities(int[] p)
        {
            int[] copy = p == null ? null : (int[])p.Clone();
            _s.Reactor.Post(delegate
            {
                _filePrio = copy;
                BtPicker pk = _picker;
                if (pk == null) return;
                pk.SetPriorities(copy);
                RecomputeDone();
                if (State == BtTorrentState.Downloading && pk.WantedMissing == 0) OnWantedComplete();
                else if (State == BtTorrentState.Seeding && pk.WantedMissing > 0) SetState(BtTorrentState.Downloading);
                _interestDirty = true;
            });
        }

        // ---------- жизненный цикл (поток реактора) ----------
        internal void Start()
        {
            if (_removed) return;
            if (_paused) { SetState(BtTorrentState.Paused); return; }
            if (_meta == null)
            {
                SetState(BtTorrentState.FetchingMetadata);
                StartTrackers();
                return;
            }
            BeginCheck(false, false);
        }

        private void DoPause()
        {
            if (_removed || !_paused) return;
            BtTorrentState st = State;
            if (st == BtTorrentState.Checking) _checkGen++;
            ClosePeers(Tr.S("пауза", "paused"));
            if (st != BtTorrentState.Error) SetState(BtTorrentState.Paused);
            if (_trackers != null) Safe(delegate { _trackers.Announce(BtAnnounceEvent.Stopped); });
        }

        private void DoResume()
        {
            if (_removed || _paused) return;
            BtTorrentState st = State;
            if (st != BtTorrentState.Paused && st != BtTorrentState.Finished && st != BtTorrentState.Error) return;
            if (_v2Only) Fail(V2Unsupported);
            else if (_meta == null)
            {
                SetState(BtTorrentState.FetchingMetadata);
                StartTrackers();
            }
            else if (_have == null || _picker == null || st == BtTorrentState.Error) BeginCheck(st == BtTorrentState.Error, false);
            else
            {
                SetState(_picker.WantedMissing == 0 ? BtTorrentState.Seeding : BtTorrentState.Downloading);
                StartTrackers();
            }
        }

        internal void StopForRemove()
        {
            if (_removed) return;
            _removed = true;
            _checkGen++;
            ClosePeers(Tr.S("торрент удалён", "the torrent is removed"));
            ShutdownParts();
            BtStorage st = _storage;
            if (st != null) _s.Disk.Run(delegate { st.Close(); });
        }

        // Сессия закрывается: реактор уже остановлен, объявление Stopped — без ожидания ответа.
        internal void ShutdownParts()
        {
            _removed = true;
            IBtTrackers t = _trackers;
            _trackers = null;
            if (t != null)
            {
                Safe(delegate { t.Announce(BtAnnounceEvent.Stopped); });
                Safe(delegate { t.Dispose(); });
            }
        }

        private void StartTrackers()
        {
            if (_trackers == null && BtFactories.Trackers != null && _trackerTiers.Count > 0)
                Safe(delegate { _trackers = BtFactories.Trackers(this, _ctx, _trackerTiers); });
            if (_trackers != null) Safe(delegate { _trackers.Announce(BtAnnounceEvent.Started); });
            _lastDht = long.MinValue;
            _lastLsd = long.MinValue;
        }

        private void Fail(string why)
        {
            _error = why ?? "";
            _checkGen++;
            ClosePeers(why);
            Journal(Tr.S("ошибка: ", "error: ") + why);
            SetState(BtTorrentState.Error);
        }

        private void SetState(BtTorrentState st)
        {
            if (_state == (int)st) return;
            _state = (int)st;
            if (st != BtTorrentState.Error) _error = "";
            Raise(StateChanged);
        }

        private void Raise(Action<BtTorrent> cb)
        {
            if (cb == null) return;
            ThreadPool.QueueUserWorkItem(delegate { try { cb(this); } catch (Exception ex) { DlLog.Report(ex); } });
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private void OnMetadataReady(BtMeta m)
        {
            lock (_gate)
            {
                _metaPending = false;
                if (_meta != null || _removed) return;
                _meta = m;
            }
            Journal(Tr.S("метаданные получены: ", "metadata received: ") + m.Name);
            foreach (BtPeer p in PeersCopy()) if (!p.Dead) p.ApplyPendingBits(m);
            Raise(MetadataReceived);
            if (_metadataOnly)
            {
                // Проба новой версии раздачи: метаданные у владельца, пиры закрываются, на диск ничего.
                _paused = true;
                DoPause();
                return;
            }
            if (m.IsPureV2)
            {
                Fail(V2Unsupported);
                return;
            }
            if (_paused) { SetState(BtTorrentState.Paused); return; }
            BeginCheck(false, false);
        }

        // Проверка данных на диске (с быстрым возобновлением — только подозрительные куски). Всё дисковое — в потоке BtDisk.
        private void BeginCheck(bool full, bool closePeers)
        {
            if (closePeers) ClosePeers(Tr.S("проверка данных", "checking data"));
            BtPicker old = _picker;
            if (old != null) old.Reset();
            _picker = null;
            SetState(BtTorrentState.Checking);
            int gen = ++_checkGen;
            _checkedPieces = 0;
            BtMeta meta = _meta;
            BtStorage st = _storage;
            BtResume resume = full ? null : _pendingResume;
            string folder = _folder, root = _rootName;
            Func<BtMeta, string> choose = _chooseRoot;
            _s.Disk.Run(delegate
            {
                BtBitfield have = null;
                string err = null;
                List<string> notes = new List<string>();
                try
                {
                    if (st == null)
                    {
                        if (string.IsNullOrEmpty(root) && choose != null) root = choose(meta);
                        if (string.IsNullOrEmpty(root)) root = DefaultRoot(meta);
                        BtStorage opened = new BtStorage(meta, folder, root);
                        err = opened.Validate();
                        if (err == null) st = opened;
                    }
                    if (err == null)
                    {
                        if (resume != null && resume.PieceCount == meta.PieceCount && string.Equals(resume.Hash, meta.HexHash, StringComparison.OrdinalIgnoreCase))
                        {
                            List<int> recheck = new List<int>();
                            have = resume.Restore(st, recheck);
                            byte[] buf = new byte[meta.PieceLength];
                            foreach (int p in recheck)
                            {
                                if (gen != _checkGen || _s.Disposing) break;
                                if (st.CheckPiece(p, buf)) have[p] = true;
                            }
                        }
                        else if (full || AnyFileOnDisk(st))
                            have = st.Recheck(delegate { return gen != _checkGen || _s.Disposing; }, delegate(int n) { _checkedPieces = n; });
                        else
                        {
                            st.ProbeDone();
                            have = new BtBitfield(meta.PieceCount);
                        }
                        notes = FinishFiles(st, have);
                    }
                }
                catch (Exception ex)
                {
                    DlLog.Report(ex);
                    err = ex.Message;
                }
                BtStorage result = st;
                string rootUsed = root;
                _s.Reactor.Post(delegate { OnCheckDone(gen, result, rootUsed, have, err, notes); });
            });
        }

        private static string DefaultRoot(BtMeta meta)
        {
            string name = DlFiles.SanitizeName(meta.RootDir.Length > 0 ? meta.RootDir : meta.Name);
            return string.IsNullOrEmpty(name) ? meta.HexHash : name;
        }

        private static bool AnyFileOnDisk(BtStorage st)
        {
            for (int i = 0; i < st.Meta.Files.Count; i++)
                if (st.IsStored(i) && (DlFiles.Exists(st.FinalPath(i)) || DlFiles.Exists(st.PartPath(i)))) return true;
            return false;
        }

        // Файлы, все куски которых уже есть, получают настоящее имя (скачаны до перезапуска или найдены проверкой).
        private static List<string> FinishFiles(BtStorage st, BtBitfield have)
        {
            List<string> errors = new List<string>();
            for (int i = 0; i < st.Meta.Files.Count; i++)
            {
                BtFile f = st.Meta.Files[i];
                if (!st.IsStored(i) || st.IsFileDone(i) || f.LastPiece < 0) continue;
                bool all = true;
                for (int p = f.FirstPiece; p <= f.LastPiece && all; p++) all = have[p];
                if (all) errors.AddRange(st.PieceVerified(f.LastPiece, have));
            }
            return errors;
        }

        private void OnCheckDone(int gen, BtStorage st, string root, BtBitfield have, string err, List<string> notes)
        {
            if (gen != _checkGen || _removed) return;
            if (err != null || have == null || st == null) { Fail(err ?? Tr.S("проверка не удалась", "the check failed")); return; }
            if (notes != null && notes.Count > 0) { Fail(notes[0]); return; }
            _storage = st;
            _rootName = root;
            _pendingResume = null;
            _have = have;
            BtPicker picker = new BtPicker(_meta, have);
            picker.SetPriorities(_filePrio);
            picker.Sequential = _sequential;
            _picker = picker;
            _fileRaised = new bool[_meta.Files.Count];
            for (int i = 0; i < _fileRaised.Length; i++) _fileRaised[i] = st.IsFileDone(i);
            _completedRaised = picker.WantedMissing == 0;
            RecomputeDone();
            foreach (BtPeer p in PeersCopy())
            {
                if (p.Dead || p.Phase != BtPeerPhase.Active) continue;
                p.RegisterBits(picker);
                for (int i = 0; i < have.Count; i++) if (have[i]) p.SendHave(i);
                p.UpdateInterest();
            }
            if (_paused) { SetState(BtTorrentState.Paused); return; }
            SetState(picker.WantedMissing == 0 ? BtTorrentState.Seeding : BtTorrentState.Downloading);
            StartTrackers();
            ConnectCandidates(BtNetClock.Ms);
        }

        private void RecomputeDone()
        {
            BtMeta meta = _meta;
            BtBitfield have = _have;
            BtStorage st = _storage;
            if (meta == null || have == null || st == null) return;
            long done = 0, wantedDone = 0;
            for (int p = 0; p < meta.PieceCount; p++)
                if (have[p]) { long all, wanted; PieceBytes(p, out all, out wanted); done += all; wantedDone += wanted; }
            Interlocked.Exchange(ref _doneBytes, done);
            Interlocked.Exchange(ref _wantedDone, wantedDone);
            Interlocked.Exchange(ref _wantedBytes, st.WantedBytes(_filePrio));
        }

        // Байты настоящих файлов в куске: все и только нужные по приоритетам.
        private void PieceBytes(int piece, out long all, out long wanted)
        {
            all = wanted = 0;
            BtMeta meta = _meta;
            long start = meta.PieceStart(piece), end = start + meta.PieceSize(piece);
            int lo = 0, hi = meta.Files.Count - 1, first = meta.Files.Count;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (meta.Files[mid].End > start) { first = mid; hi = mid - 1; }
                else lo = mid + 1;
            }
            for (int i = first; i < meta.Files.Count && meta.Files[i].Offset < end; i++)
            {
                BtFile f = meta.Files[i];
                if (f.Pad || f.Symlink || f.Length == 0) continue;
                long n = Math.Min(end, f.End) - Math.Max(start, f.Offset);
                if (n <= 0) continue;
                all += n;
                if (_filePrio == null || i >= _filePrio.Length || _filePrio[i] > 0) wanted += n;
            }
        }

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
