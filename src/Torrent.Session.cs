// Windows Process Cleaner — «Загрузки», торренты: сессия — порт, реактор, диск, торренты; точка входа для движка.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Один порт на TCP и UDP. Слушатель TCP открывается только при InboundOpen: без правила брандмауэра Windows покажет
// диалог сама, а приложение по своей инициативе окон не открывает. DHT, LSD, трекеры, MSE и расширения подключаются
// через BtFactories: пустая фабрика — части нет, торрент работает без неё (AddPeer и открытый текст).
// Остановка: реактор (закрывает соединения) → трекеры (Stopped без ожидания) → диск (дописывает очередь) → UDP.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WindowsProcessCleaner.Downloads
{
    internal enum BtTorrentState { FetchingMetadata, Checking, Downloading, Seeding, Paused, Finished, Error }

    // Поля заполняет движок (Torrent.Wiring.cs); в срезе без него присваиваний нет.
#pragma warning disable 649
    internal sealed class BtSessionOptions
    {
        public IPAddress Bind = IPAddress.Any;
        public int Port;                         // 0 — любой свободный
        public string TorrentsDir = "";
        public IDlEnvironment Env;
        public DlTokenBucket DownGlobal, UpGlobal;
        public BtEncryption Encryption = BtEncryption.Prefer;
        public bool InboundOpen;
        public bool EnableDht = true, EnableLsd = true, EnablePex = true;
        public int MaxConnections = 200, MaxConnectionsPerTorrent = 50, MaxHalfOpen = 20, UploadSlots = 4;
        public Action<string> Log;               // журнал сессии без секретов; null — молча
    }

    internal sealed class BtAddParams
    {
        public BtMeta Meta;
        public BtMagnet Magnet;                  // ровно одно из двух
        public string Folder = "";
        public string RootName;                  // null — ChooseRootName(meta) при получении метаданных
        public Func<BtMeta, string> ChooseRootName;
        public BtResume Resume;
        public int[] Priorities;
        public bool Paused, Sequential;
        public bool MetadataOnly;                // только получить метаданные: после них пауза, файлы не открываются
        public List<List<string>> ExtraTrackers;
        public List<BtEndpoint> Peers;
    }

#pragma warning restore 649

    internal sealed class BtTorrentStats
    {
        public long DownBps, UpBps, Downloaded, Uploaded, Wasted, Wanted, Done;
        public double Progress, Availability, CheckProgress;
        public int Peers, Seeds, PiecesHave, PieceCount;
        public long SeedSeconds, ActiveSeconds, EtaSeconds;   // Eta -1 — неизвестно
    }

    internal sealed class BtSession : IDisposable
    {
        private readonly BtSessionOptions _o;
        private readonly BtContext _ctx;
        private readonly object _gate = new object();
        private readonly Dictionary<string, BtTorrent> _torrents = new Dictionary<string, BtTorrent>(StringComparer.OrdinalIgnoreCase);
        private readonly List<BtPeer> _orphans = new List<BtPeer>();
        private BtUdp _udp;
        private IBtDht _dht;
        private IBtLsd _lsd;
        private volatile bool _started, _disposing;

        internal readonly BtReactor Reactor;
        internal BtDisk Disk;
        internal int HalfOpen;                   // поток реактора

        public BtSession(BtSessionOptions o)
        {
            _o = o ?? new BtSessionOptions();
            _ctx = new BtContext();
            _ctx.PeerId = BtContext.NewPeerId();
            _ctx.Encryption = _o.Encryption;
            _ctx.Env = _o.Env;
            _ctx.DownGlobal = _o.DownGlobal;
            _ctx.UpGlobal = _o.UpGlobal;
            _ctx.TorrentsDir = _o.TorrentsDir ?? "";
            byte[] key = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(key);
            _ctx.AnnounceKey = BitConverter.ToUInt32(key, 0);
            _ctx.FindSwarm = delegate(byte[] hash) { return Find(hash); };
            if (_o.Log != null) _ctx.Log = _o.Log;
            Reactor = new BtReactor("wpc-bt-net");
            Reactor.Accepted = OnAccepted;
            Reactor.Tick = OnTick;
        }

        public BtContext Context { get { return _ctx; } }
        internal BtSessionOptions Options { get { return _o; } }
        internal bool Disposing { get { return _disposing; } }

        public void Start()
        {
            if (_started || _disposing) return;
            Disk = new BtDisk("wpc-bt-disk");
            // Сначала UDP (при порте 0 система выбирает порт, свободный для UDP), затем TCP на том же номере. Номер бывает
            // занят или исключён (диапазоны Hyper-V) только для одного из протоколов — тогда другой номер, случайный из
            // динамического диапазона: номер 0 система выдаёт подряд, и сотня исключённых TCP-номеров съела бы все попытки.
            Socket listener = null;
            int port = _o.Port;
            Random pick = null;
            const int Attempts = 20;
            for (int attempt = 0; attempt < Attempts && _udp == null; attempt++)
            {
                BtUdp udp = null;
                try
                {
                    udp = new BtUdp(_o.Bind, port);
                    if (_o.InboundOpen) listener = OpenListener(_o.Bind, udp.Port);
                    _udp = udp;
                }
                catch (SocketException ex)
                {
                    if (udp != null) udp.Dispose();
                    listener = null;
                    if (attempt == Attempts - 1)
                    {
                        Disk.Dispose();
                        Disk = null;
                        throw;
                    }
                    if (attempt == 0 && port != 0) _ctx.Log(Tr.S("порт занят, выбран другой: ", "the port is busy, another one is used: ") + ex.SocketErrorCode);
                    if (pick == null) pick = new Random();
                    port = 49152 + pick.Next(16384);
                }
            }
            _ctx.Udp = _udp;
            _ctx.Port = _udp.Port;
            if (listener != null)
            {
                Reactor.SetListener(listener);
                _ctx.InboundOpen = true;
            }
            if (_o.EnableDht && BtFactories.Dht != null)
            {
                try
                {
                    _dht = BtFactories.Dht(_ctx);
                    if (_dht != null)
                    {
                        _ctx.Dht = _dht;
                        _udp.AddHandler(_dht);
                        _dht.Start();
                    }
                }
                catch (Exception ex) { DlLog.Report(ex); }
            }
            if (_o.EnableLsd && BtFactories.Lsd != null)
            {
                try
                {
                    _lsd = BtFactories.Lsd(_ctx);
                    if (_lsd != null)
                    {
                        _ctx.Lsd = _lsd;
                        _lsd.Start();
                    }
                }
                catch (Exception ex) { DlLog.Report(ex); }
            }
            _started = true;
            Reactor.Start();
            foreach (BtTorrent t in Torrents()) Reactor.Post(t.Start);
        }

        private static Socket OpenListener(IPAddress bind, int port)
        {
            Socket s = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.ExclusiveAddressUse = true;
                s.Bind(new IPEndPoint(bind, port));
                s.Listen(64);
                s.Blocking = false;
                return s;
            }
            catch
            {
                s.Close();
                throw;
            }
        }

        // Слушатель на порту сессии (том же, что UDP). Не открылся (порт занят по TCP) — остаётся закрытым, строка в журнал.
        public void SetInboundOpen(bool open)
        {
            if (!_started)
            {
                _o.InboundOpen = open;
                return;
            }
            Reactor.Post(delegate
            {
                if (open && !Reactor.HasListener)
                {
                    try
                    {
                        Reactor.SetListener(OpenListener(_o.Bind, _ctx.Port));
                        _ctx.InboundOpen = true;
                    }
                    catch (SocketException ex)
                    {
                        _ctx.Log(Tr.S("не удалось открыть входящие соединения: ", "could not open incoming connections: ") + ex.SocketErrorCode);
                    }
                }
                else if (!open && Reactor.HasListener)
                {
                    Reactor.SetListener(null);
                    _ctx.InboundOpen = false;
                }
            });
        }

        public BtTorrent Add(BtAddParams p, out string error)
        {
            error = null;
            if (_disposing) { error = Tr.S("сессия закрыта", "the session is closed"); return null; }
            if (p == null || (p.Meta == null) == (p.Magnet == null))
            {
                error = Tr.S("нужен либо .torrent, либо magnet-ссылка", "either a .torrent or a magnet link is required");
                return null;
            }
            byte[] hash = p.Meta != null ? p.Meta.SwarmHash : p.Magnet.SwarmHash;
            if (hash == null || hash.Length != 20) { error = Tr.S("нет info-hash", "no info-hash"); return null; }
            if (string.IsNullOrEmpty(p.Folder)) { error = Tr.S("не указана папка загрузки", "no download folder"); return null; }
            BtTorrent t;
            lock (_gate)
            {
                string key = Bencode.Hex(hash);
                if (_torrents.ContainsKey(key)) { error = Tr.S("этот торрент уже добавлен", "this torrent is already added"); return null; }
                t = new BtTorrent(this, p, hash);
                _torrents[key] = t;
            }
            if (_started) Reactor.Post(t.Start);
            return t;
        }

        public void Remove(BtTorrent t)
        {
            if (t == null) return;
            lock (_gate)
            {
                string key = Bencode.Hex(t.InfoHash);
                BtTorrent cur;
                if (!_torrents.TryGetValue(key, out cur) || cur != t) return;
                _torrents.Remove(key);
            }
            if (_started && !_disposing) Reactor.Post(t.StopForRemove);
            else t.ShutdownParts();
        }

        public List<BtTorrent> Torrents()
        {
            lock (_gate) return new List<BtTorrent>(_torrents.Values);
        }

        public BtTorrent Find(byte[] swarmHash20)
        {
            if (swarmHash20 == null || swarmHash20.Length != 20) return null;
            BtTorrent t;
            lock (_gate) return _torrents.TryGetValue(Bencode.Hex(swarmHash20), out t) ? t : null;
        }

        // ---------- для пиров (поток реактора) ----------
        internal bool CanOpen()
        {
            return !_disposing && HalfOpen < _o.MaxHalfOpen && Reactor.Count < _o.MaxConnections;
        }

        // Входящее рукопожатие: торрент ищется через ctx.FindSwarm (движок может подменить поиск), чужие реализации — мимо.
        internal BtTorrent FindForIncoming(byte[] infoHash)
        {
            IBtSwarm swarm = null;
            try { swarm = _ctx.FindSwarm(infoHash); }
            catch (Exception ex) { DlLog.Report(ex); }
            BtTorrent t = swarm as BtTorrent;
            return t != null && t.Session == this ? t : null;
        }

        // MSE: торрент по HASH('req2', SKEY) — перебор торрентов сессии.
        internal byte[] Req2Lookup(byte[] req2)
        {
            foreach (BtTorrent t in Torrents())
                if (Bencode.SameBytes(t.Req2Hash, req2)) return t.InfoHash;
            return null;
        }

        private void OnAccepted(Socket s)
        {
            if (_disposing || Reactor.Count >= _o.MaxConnections)
            {
                try { s.Close(); } catch { }
                return;
            }
            IPEndPoint remote;
            try { remote = (IPEndPoint)s.RemoteEndPoint; }
            catch (SocketException)
            {
                try { s.Close(); } catch { }
                return;
            }
            BtPeer p = new BtPeer(this, s, new BtEndpoint(remote.Address, remote.Port), false, null, BtPeerOrigin.Incoming, false);
            _orphans.Add(p);
            Reactor.Add(p);
        }

        internal void Adopt(BtPeer p) { _orphans.Remove(p); }
        internal void Orphaned(BtPeer p) { _orphans.Remove(p); }

        private void OnTick(long now)
        {
            for (int i = _orphans.Count - 1; i >= 0; i--)
                if (i < _orphans.Count) _orphans[i].Tick(now);
            foreach (BtTorrent t in Torrents()) t.Tick(now);
        }

        public void Dispose()
        {
            if (_disposing) return;
            _disposing = true;
            Reactor.Dispose();
            List<BtTorrent> all = Torrents();
            foreach (BtTorrent t in all) t.ShutdownParts();
            if (Disk != null) Disk.Dispose();
            foreach (BtTorrent t in all)
            {
                BtStorage st = t.Storage;
                if (st != null) st.Dispose();
            }
            if (_lsd != null) try { _lsd.Dispose(); } catch (Exception ex) { DlLog.Report(ex); }
            if (_dht != null)
            {
                if (_udp != null) _udp.RemoveHandler(_dht);
                try { _dht.Dispose(); } catch (Exception ex) { DlLog.Report(ex); }
            }
            if (_udp != null) _udp.Dispose();
        }
    }
}
