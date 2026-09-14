// Windows Process Cleaner — область «torrent», собранный клиент: BtWiring.Install подключает все части, и сессии на
// 127.0.0.1 работают так же, как в приложении — трекер, ut_metadata, MSE, PEX, DHT вместе.
//
// Ненастоящие здесь только окружение сети: HTTP-трекер на петле (TrkHttpServer), узел DHT для первого знакомства
// (DhtFxNode) и libtorrent как чужой клиент. Адреса bootstrap DHT подменяются на петлю — интернет не трогается, LSD
// (многоадресная рассылка в локальную сеть) выключен.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunSession()
        {
            BtWiring.Install();
            BtWiring.Install();
            T.Check("session: Install is idempotent — ut_metadata = 1, ut_pex = 2, all parts present",
                    BtFactories.Extensions.Count == 2 && BtFactories.Trackers != null && BtFactories.Dht != null && BtFactories.Lsd != null
                    && BtFactories.PortMapper != null && BtFactories.MseOutgoing != null && BtFactories.MseIncoming != null,
                    "extensions " + BtFactories.Extensions.Count);
            string[] savedBootstrap = BtDht.DefaultBootstrap;
            BtDht.DefaultBootstrap = new string[0];
            try
            {
                WireRun("port fallback", SesPortFallback);
                WireRun("tracker magnet", SesTrackerMagnet);
                WireRun("require both", SesRequireBoth);
                WireRun("pex third peer", SesPexIntroduces);
                WireRun("dht magnet", SesDhtMagnet);
                WireRun("private torrent", SesPrivateSilent);
                WireRun("libtorrent forced encryption magnet", SesOracleForcedMagnet);
            }
            finally { BtDht.DefaultBootstrap = savedBootstrap; }
        }

        private static int _sesDirs;

        // Сессия как в приложении, только на петле и без LSD. DHT узла — bootstrap из BtDht.DefaultBootstrap.
        private static BtSession SesSession(BtEncryption enc, bool dht, bool pex)
        {
            BtSessionOptions o = new BtSessionOptions();
            o.Bind = IPAddress.Loopback;
            o.InboundOpen = true;
            o.EnableDht = dht;
            o.EnableLsd = false;
            o.EnablePex = pex;
            o.Encryption = enc;
            o.TorrentsDir = Fx.MakeDir(Fx.Root, "bt-ses-state", "s" + Interlocked.Increment(ref _sesDirs));
            BtSession s = new BtSession(o);
            s.Start();
            // Все узлы на одном адресе 127.0.0.1: предел запросов «с одного IP» рассчитан на интернет, не на петлю.
            BtDht d = s.Context.Dht as BtDht;
            if (d != null) d.RateLimitPerSecond = 500;
            return s;
        }

        private static BtTorrent SesAddMagnet(BtSession s, string link, string dir)
        {
            string err;
            BtAddParams p = new BtAddParams();
            p.Magnet = BtMagnet.Parse(link, out err);
            if (p.Magnet == null) throw new InvalidOperationException("magnet: " + err);
            p.Folder = dir;
            p.RootName = "wire";
            BtTorrent t = s.Add(p, out err);
            if (t == null) throw new InvalidOperationException("add magnet: " + err);
            return t;
        }

        // Хоть одно соединение торрента зашифровано — проверяется, пока идёт передача.
        private static bool SesEncrypted(BtTorrent t)
        {
            foreach (BtPeerInfo p in t.ConnectedPeers()) if (p.Encrypted) return true;
            return false;
        }

        private static List<BtFxFile> SesFiles(int seed, int size)
        {
            return new List<BtFxFile> { new BtFxFile(BtFx.Data(size, seed), "a.bin"), new BtFxFile(BtFx.Data(size / 3 + 7, seed + 1), "d", "b.bin") };
        }

        // ================================================================== //
        //  Порт сессии: UDP-номер 0 система выдаёт подряд, а TCP-номера бывают исключены сотнями (Hyper-V)
        // ================================================================== //
        private static void SesPortFallback()
        {
            // Следующие номера, которые система выдала бы UDP, заняты по TCP — как диапазон исключения для одного протокола.
            int next;
            using (Socket probe = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                next = ((IPEndPoint)probe.LocalEndPoint).Port;
            }
            List<Socket> blockers = new List<Socket>();
            BtSession s = null;
            try
            {
                for (int p = next; p < next + 60 && p <= 65535; p++)
                {
                    Socket b = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        b.ExclusiveAddressUse = true;
                        b.Bind(new IPEndPoint(IPAddress.Loopback, p));
                        blockers.Add(b);
                    }
                    catch (SocketException) { b.Close(); }
                }
                BtSessionOptions o = new BtSessionOptions();
                o.Bind = IPAddress.Loopback;
                o.InboundOpen = true;
                o.EnableDht = false;
                o.EnableLsd = false;
                o.TorrentsDir = Fx.MakeDir(Fx.Root, "bt-ses-state", "port");
                s = new BtSession(o);
                string error = null;
                try { s.Start(); }
                catch (SocketException ex) { error = ex.SocketErrorCode.ToString(); }
                bool listening = false;
                if (error == null)
                    using (Socket c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        try { c.Connect(new IPEndPoint(IPAddress.Loopback, s.Context.Port)); listening = true; }
                        catch (SocketException) { }
                    }
                T.Check("session: Start finds a port free for UDP and TCP although the next 60 UDP picks are taken by TCP",
                        error == null && listening && (s.Context.Port < next || s.Context.Port >= next + 60),
                        "next udp " + next + ", blocked " + blockers.Count + ", error " + error + ", port " + (error == null ? s.Context.Port : 0));
            }
            finally
            {
                if (s != null) s.Dispose();
                foreach (Socket b in blockers) b.Close();
            }
        }

        // ================================================================== //
        //  Трекер + ut_metadata: у скачивающего только magnet
        // ================================================================== //
        private static void SesTrackerMagnet()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-ses-tracker");
            List<BtSession> sessions = new List<BtSession>();
            using (TrkHttpServer tracker = new TrkHttpServer())
            {
                // Простой трекер: запоминает порт каждого объявившегося и отдаёт остальных в компактном виде.
                Dictionary<string, List<int>> swarms = new Dictionary<string, List<int>>();
                tracker.Handler = delegate(string path, string query)
                {
                    if (path != "/announce") return null;
                    Dictionary<string, byte[]> q = TrkQuery(query);
                    byte[] hash;
                    int port;
                    if (!q.TryGetValue("info_hash", out hash) || !int.TryParse(TrkQs(q, "port"), out port)) return null;
                    List<string> others = new List<string>();
                    lock (swarms)
                    {
                        List<int> ports;
                        if (!swarms.TryGetValue(Bencode.Hex(hash), out ports)) swarms[Bencode.Hex(hash)] = ports = new List<int>();
                        foreach (int p in ports) if (p != port) others.Add("127.0.0.1:" + p);
                        if (!ports.Contains(port)) ports.Add(port);
                    }
                    BVal d = BVal.NewDict();
                    d.Set("interval", BVal.Int(1800));
                    d.Set("peers", BVal.Bytes(TrkCompact(others.ToArray())));
                    return TrkBody(d);
                };
                string announce = tracker.Url("/announce");
                List<BtFxFile> files = SesFiles(201, 300000);
                string err;
                BtMeta meta = BtMeta.Parse(BtFx.Build("wire", files, 32768, 1, false, new List<IList<string>> { new List<string> { announce } }), out err);
                if (meta == null) throw new InvalidOperationException("fixture: " + err);
                try
                {
                    BtSession seedS = SesSession(BtEncryption.Prefer, false, true), leechS = SesSession(BtEncryption.Prefer, false, true);
                    sessions.AddRange(new[] { seedS, leechS });
                    BtTorrent seed = WireAddSeed(seedS, meta, files, Fx.MakeDir(root, "seed"));
                    bool announced = WireWaitFor(delegate { return seed.State == BtTorrentState.Seeding && tracker.Count("/announce") >= 1; }, 10000);
                    string dl = Fx.MakeDir(root, "leech");
                    BtTorrent leech = SesAddMagnet(leechS, "magnet:?xt=urn:btih:" + Bencode.Hex(meta.InfoHash) + "&tr=" + Uri.EscapeDataString(announce), dl);
                    int metaRaised = 0;
                    leech.MetadataReceived = delegate { Interlocked.Increment(ref metaRaised); };
                    bool encrypted = false;
                    bool done = WireWaitFor(delegate
                    {
                        if (SesEncrypted(leech)) encrypted = true;
                        return leech.State == BtTorrentState.Seeding;
                    }, 30000);
                    string info = "";
                    bool same = done && WireSameFiles(meta, files, dl, out info);
                    T.Check("session: magnet-only leecher finds the seed through the HTTP tracker, gets metadata by ut_metadata, SHA-256 equal",
                            announced && done && same && leech.Meta != null && Bencode.SameBytes(leech.Meta.InfoHash, meta.InfoHash),
                            WireState(leech) + " seed " + WireState(seed) + " hits " + tracker.Count("/announce") + " " + info);
                    T.Check("session: MetadataReceived raised once; Prefer on both sides ⇒ the connection is encrypted", WireWaitFor(delegate { return metaRaised == 1; }, 3000) && encrypted,
                            "raised " + metaRaised + " encrypted " + encrypted);
                    bool trackerSeen = false;
                    foreach (BtTrackerInfo ti in leech.Trackers()) if (ti.Url.StartsWith("http://127.0.0.1:" + tracker.Port, StringComparison.Ordinal)) trackerSeen = true;
                    T.Check("session: the tracker from magnet tr= is listed for the card", trackerSeen);
                }
                finally { WireDispose(sessions); }
            }
        }

        // ================================================================== //
        //  MSE Require с обеих сторон
        // ================================================================== //
        private static void SesRequireBoth()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-ses-require");
            List<BtFxFile> files = SesFiles(211, 400000);
            BtMeta meta = WireMeta(files, 32768, 3);
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession a = SesSession(BtEncryption.Require, false, false), b = SesSession(BtEncryption.Require, false, false);
                sessions.AddRange(new[] { a, b });
                BtTorrent seed = WireAddSeed(a, meta, files, Fx.MakeDir(root, "seed"));
                seed.UpLimit = 256 * 1024;             // ~2 с передачи: соединение успевает попасть в выборку
                string dl = Fx.MakeDir(root, "leech");
                BtTorrent leech = WireAdd(b, meta, dl, null);
                WireWaitFor(delegate { return seed.State == BtTorrentState.Seeding && leech.State == BtTorrentState.Downloading; }, 5000);
                leech.AddPeer(WireEp(a));
                bool encrypted = false, plain = false;
                bool done = WireWaitFor(delegate
                {
                    foreach (BtPeerInfo p in leech.ConnectedPeers()) { if (p.Encrypted) encrypted = true; else plain = true; }
                    return leech.State == BtTorrentState.Seeding;
                }, 20000);
                string info = "";
                bool same = done && WireSameFiles(meta, files, dl, out info);
                T.Check("session: Require on both sides — hybrid transfer completes over MSE, SHA-256 equal, no plaintext connection",
                        done && same && encrypted && !plain, WireState(leech) + " encrypted " + encrypted + " plain " + plain + " " + info);
            }
            finally { WireDispose(sessions); }
        }

        // ================================================================== //
        //  PEX: третий узел знакомится с сидом через второй
        // ================================================================== //
        private static void SesPexIntroduces()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-ses-pex");
            List<BtFxFile> files = SesFiles(221, 1200000);
            BtMeta meta = WireMeta(files, 65536, 1);
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s1 = SesSession(BtEncryption.Prefer, false, true), s2 = SesSession(BtEncryption.Prefer, false, true), s3 = SesSession(BtEncryption.Prefer, false, true);
                sessions.AddRange(new[] { s1, s2, s3 });
                BtTorrent seed = WireAddSeed(s1, meta, files, Fx.MakeDir(root, "seed"));
                seed.UpLimit = 64 * 1024;              // второй не успевает докачать, пока третий подключается
                BtTorrent l2 = WireAdd(s2, meta, Fx.MakeDir(root, "l2"), null);
                BtTorrent l3 = WireAdd(s3, meta, Fx.MakeDir(root, "l3"), null);
                WireWaitFor(delegate { return seed.State == BtTorrentState.Seeding && l2.State == BtTorrentState.Downloading && l3.State == BtTorrentState.Downloading; }, 5000);
                l2.AddPeer(WireEp(s1));
                bool linked = WireWaitFor(delegate { return l2.ConnectedPeers().Count >= 1; }, 5000);
                l3.AddPeer(WireEp(s2));
                BtPeerInfo viaPex = null;
                bool introduced = WireWaitFor(delegate
                {
                    foreach (BtPeerInfo p in l3.ConnectedPeers())
                        if (p.Origin == BtPeerOrigin.Pex && p.Endpoint != null && p.Endpoint.Port == s1.Context.Port) { viaPex = p; return true; }
                    return false;
                }, 10000);
                StringBuilder peers = new StringBuilder();
                foreach (BtPeerInfo p in l3.ConnectedPeers()) peers.Append(p.Endpoint).Append(' ').Append(p.Origin).Append("; ");
                T.Check("session: PEX — the third peer, given only the second, connects to the seed learned from ut_pex",
                        linked && introduced && viaPex.Seed, "l3 peers: " + peers + " seed port " + s1.Context.Port + " l2 " + WireState(l2));
            }
            finally { WireDispose(sessions); }
        }

        // ================================================================== //
        //  DHT: bootstrap-узел и две сессии; у скачивающего только magnet
        // ================================================================== //
        private static void SesDhtMagnet()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-ses-dht");
            List<BtFxFile> files = SesFiles(231, 250000);
            BtMeta meta = WireMeta(files, 32768, 1);
            List<BtSession> sessions = new List<BtSession>();
            DhtFxNode n0 = null;
            try
            {
                n0 = new DhtFxNode(Fx.MakeDir(root, "n0"));
                n0.Dht.RateLimitPerSecond = 500;
                n0.Dht.Start();
                BtDht.DefaultBootstrap = new[] { n0.Addr };
                BtSession s1 = SesSession(BtEncryption.Prefer, true, false), s2 = SesSession(BtEncryption.Prefer, true, false);
                BtDht.DefaultBootstrap = new string[0];
                sessions.AddRange(new[] { s1, s2 });
                BtTorrent seed = WireAddSeed(s1, meta, files, Fx.MakeDir(root, "seed"));
                bool stored = WireWaitFor(delegate { return n0.Dht.StoredPeers(meta.InfoHash) > 0; }, 10000);
                T.Check("session: the seed announces itself into DHT (announce_peer stored on the bootstrap node)", stored,
                        "n0 nodes " + n0.Dht.NodeCount + " s1 dht " + s1.Context.Dht.Status + " " + WireState(seed));
                string dl = Fx.MakeDir(root, "leech");
                BtTorrent leech = SesAddMagnet(s2, "magnet:?xt=urn:btih:" + Bencode.Hex(meta.InfoHash), dl);
                BtPeerOrigin origin = BtPeerOrigin.Manual;
                bool fromDht = false;
                bool done = WireWaitFor(delegate
                {
                    foreach (BtPeerInfo p in leech.ConnectedPeers()) { origin = p.Origin; if (p.Origin == BtPeerOrigin.Dht) fromDht = true; }
                    return leech.State == BtTorrentState.Seeding;
                }, 30000);
                string info = "";
                bool same = done && WireSameFiles(meta, files, dl, out info);
                T.Check("session: magnet without trackers or peers — DHT (3 nodes) finds the seed, metadata by ut_metadata, SHA-256 equal",
                        stored && done && same && fromDht, WireState(leech) + " origin " + origin + " s2 dht " + s2.Context.Dht.Status + " " + info);
                string m;
                bool pex = SesProbeExtensions(s1.Context.Port, meta.InfoHash, out m);
                T.Check("session: EnablePex off — no ut_pex in the extended handshake, ut_metadata keeps number 1", !pex && m.Contains("ut_metadata=1"), m);
            }
            finally
            {
                BtDht.DefaultBootstrap = new string[0];
                WireDispose(sessions);
                if (n0 != null) n0.Dispose();
            }
        }

        // ================================================================== //
        //  Частный торрент (BEP 27): ни DHT, ни PEX; публичный рядом — контроль
        // ================================================================== //
        private sealed class SesUdpSpy : IBtUdpHandler
        {
            private readonly byte[] _a, _b;
            public int HitsA, HitsB;
            public SesUdpSpy(byte[] a, byte[] b) { _a = a; _b = b; }

            public bool HandleDatagram(BtEndpoint from, byte[] data, int count)
            {
                if (Contains(data, count, _a)) Interlocked.Increment(ref HitsA);
                if (Contains(data, count, _b)) Interlocked.Increment(ref HitsB);
                return false;
            }

            private static bool Contains(byte[] data, int count, byte[] what)
            {
                for (int i = 0; i + what.Length <= count; i++)
                {
                    int j = 0;
                    while (j < what.Length && data[i + j] == what[j]) j++;
                    if (j == what.Length) return true;
                }
                return false;
            }
        }

        private static void SesPrivateSilent()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-ses-private");
            List<BtFxFile> pubFiles = SesFiles(241, 150000), privFiles = SesFiles(243, 150000);
            string err;
            BtMeta pub = WireMeta(pubFiles, 32768, 1);
            BtMeta priv = BtMeta.Parse(BtFx.Build("wire", privFiles, 32768, 1, true, null), out err);
            if (priv == null || !priv.Private) throw new InvalidOperationException("private fixture: " + err);
            List<BtSession> sessions = new List<BtSession>();
            DhtFxNode n0 = null;
            try
            {
                n0 = new DhtFxNode(Fx.MakeDir(root, "n0"));
                n0.Dht.RateLimitPerSecond = 500;
                SesUdpSpy spy = new SesUdpSpy(pub.InfoHash, priv.InfoHash);
                n0.Udp.AddHandler(spy);                 // раньше DHT: видит каждую датаграмму и передаёт её дальше
                n0.Dht.Start();
                BtDht.DefaultBootstrap = new[] { n0.Addr };
                BtSession s1 = SesSession(BtEncryption.Prefer, true, true), s2 = SesSession(BtEncryption.Prefer, true, true);
                BtDht.DefaultBootstrap = new string[0];
                sessions.AddRange(new[] { s1, s2 });
                BtTorrent pubSeed = WireAddSeed(s1, pub, pubFiles, Fx.MakeDir(root, "pub-seed"));
                BtTorrent privSeed = WireAddSeed(s1, priv, privFiles, Fx.MakeDir(root, "priv-seed"));
                BtTorrent pubLeech = WireAdd(s2, pub, Fx.MakeDir(root, "pub-leech"), null);
                BtTorrent privLeech = WireAdd(s2, priv, Fx.MakeDir(root, "priv-leech"), null);
                WireWaitFor(delegate { return pubSeed.State == BtTorrentState.Seeding && privSeed.State == BtTorrentState.Seeding; }, 5000);
                pubLeech.AddPeer(WireEp(s1));
                privLeech.AddPeer(WireEp(s1));
                bool done = WireWaitFor(delegate { return pubLeech.State == BtTorrentState.Seeding && privLeech.State == BtTorrentState.Seeding; }, 20000);
                bool control = WireWaitFor(delegate { return spy.HitsA > 0 && n0.Dht.StoredPeers(pub.InfoHash) > 0; }, 10000);
                Thread.Sleep(1500);                     // запоздавший запрос по частному торренту успел бы дойти
                T.Check("session: private torrent — no DHT datagram carries its info-hash; the public one beside it is looked up and announced",
                        done && control && spy.HitsB == 0 && n0.Dht.StoredPeers(priv.InfoHash) == 0,
                        "public hits " + spy.HitsA + " private hits " + spy.HitsB + " | " + WireState(pubLeech) + " | " + WireState(privLeech));

                string pubM, privM;
                bool pubPex = SesProbeExtensions(s1.Context.Port, pub.InfoHash, out pubM);
                bool privPex = SesProbeExtensions(s1.Context.Port, priv.InfoHash, out privM);
                T.Check("session: private torrent — our extended handshake has no ut_pex (public: ut_metadata=1, ut_pex=2)",
                        pubPex && !privPex && pubM.Contains("ut_metadata=1") && pubM.Contains("ut_pex=2") && privM.Contains("ut_metadata=1"),
                        "public m: " + pubM + " private m: " + privM);
            }
            finally
            {
                BtDht.DefaultBootstrap = new string[0];
                WireDispose(sessions);
                if (n0 != null) n0.Dispose();
            }
        }

        // Сырой пир: рукопожатие с битом расширений, ждём расширенное рукопожатие сессии; m — «имя=номер» через запятую.
        private static bool SesProbeExtensions(int port, byte[] hash, out string m)
        {
            m = "";
            using (Socket c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                c.ReceiveTimeout = 5000;
                c.Connect(new IPEndPoint(IPAddress.Loopback, port));
                c.Send(BtWire.Handshake(hash, Encoding.ASCII.GetBytes("-PR0001-probeprobepr"), false));
                if (WireReadExact(c, 68) == null) { m = "no handshake"; return false; }
                for (int i = 0; i < 20; i++)
                {
                    byte[] len = WireReadExact(c, 4);
                    if (len == null) { m = "closed"; return false; }
                    int n = BtWire.ReadInt(len, 0);
                    if (n == 0) continue;
                    if (n < 0 || n > 1 << 20) { m = "bad length"; return false; }
                    byte[] body = WireReadExact(c, n);
                    if (body == null) { m = "closed"; return false; }
                    if (body[0] != BtWire.Extended || n < 2 || body[1] != 0) continue;
                    byte[] payload = new byte[n - 2];
                    Buffer.BlockCopy(body, 2, payload, 0, payload.Length);
                    string err;
                    BVal root = Bencode.Decode(payload, out err);
                    BVal dict = root == null ? null : root.Get("m", BKind.Dict);
                    if (dict == null) { m = "no m: " + err; return false; }
                    bool pex = false;
                    List<string> parts = new List<string>();
                    foreach (KeyValuePair<byte[], BVal> kv in dict.D)
                    {
                        string name = Encoding.ASCII.GetString(kv.Key);
                        parts.Add(name + "=" + kv.Value.I);
                        if (name == "ut_pex") pex = true;
                    }
                    m = string.Join(",", parts.ToArray());
                    return pex;
                }
                m = "no extended handshake";
                return false;
            }
        }

        // ================================================================== //
        //  libtorrent: шифрование обязательно с обеих сторон, у нас только его magnet
        // ================================================================== //
        private static void SesOracleForcedMagnet()
        {
            const string Name = "session: libtorrent seeds with forced RC4, we Require and have only its magnet ⇒ metadata over MSE, SHA-256 equal";
            List<BtFxFile> files = SesFiles(251, 280000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-ses-lt-enc");
            string ltDir = Fx.MakeDir(root, "lt");
            string file = System.IO.Path.Combine(root, "wire.torrent");
            System.IO.File.WriteAllBytes(file, torrent);
            WritePlaced(meta, files, ltDir, "wire");
            string why;
            List<BtSession> sessions = new List<BtSession>();
            using (WireOracle lt = WireOracle.Start("seedenc \"" + file + "\" \"" + ltDir + "\"", out why))
            {
                if (lt == null) { WireOracleSkip(Name, why); return; }
                try
                {
                    string portLine = lt.WaitLine("PORT ", 20000);
                    string magnetLine = lt.WaitLine("MAGNET ", 5000);
                    string seeding = lt.WaitLine("SEEDING", 5000);
                    int port;
                    if (portLine == null || !portLine.StartsWith("PORT ") || !int.TryParse(portLine.Substring(5), out port)
                        || magnetLine == null || !magnetLine.StartsWith("MAGNET ") || seeding != "SEEDING")
                    {
                        T.Check(Name, false, "oracle did not start: " + lt.Output());
                        return;
                    }
                    BtSession s = SesSession(BtEncryption.Require, false, false);
                    sessions.Add(s);
                    string dl = Fx.MakeDir(root, "we");
                    BtTorrent t = SesAddMagnet(s, magnetLine.Substring(7), dl);
                    t.AddPeer(new BtEndpoint(IPAddress.Loopback, port));
                    bool encrypted = false;
                    bool done = WireWaitFor(delegate
                    {
                        if (SesEncrypted(t)) encrypted = true;
                        return t.State == BtTorrentState.Seeding;
                    }, 30000);
                    string info = "";
                    bool same = done && WireSameFiles(meta, files, dl, out info);
                    lt.CloseInput();
                    string up = lt.WaitLine("UPLOADED ", 5000);
                    long uploaded;
                    bool counted = up != null && up.StartsWith("UPLOADED ") && long.TryParse(up.Substring(9), out uploaded) && uploaded >= meta.TotalSize;
                    T.Check(Name, done && same && encrypted && counted && t.Meta != null,
                            WireState(t) + " encrypted " + encrypted + " " + info + " lt: " + lt.Output());
                }
                finally { WireDispose(sessions); }
            }
        }
    }
}
