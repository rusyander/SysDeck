// Windows Process Cleaner — область «torrent», часть трекеров: HTTP- и UDP-трекеры, ut_metadata (BEP 9), LSD (BEP 14).
//
// Сеть — только петля 127.0.0.1: трекер HTTP — TcpListener (сырой запрос, чтобы видеть байты info_hash как они ушли),
// трекер UDP — свой сокет против настоящего BtUdp, пиры ut_metadata — пары соединений в памяти с настоящим bencode на
// проводе, LSD — два экземпляра на петле без группы. Рой — подделка: он только записывает, что ему передали, а метаданные
// сверяет настоящим BtMeta.FromInfo. Статические крючки времени восстанавливаются в finally.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunTrackers()
        {
            TrkHttpCases();
            TrkTierCases();
            TrkUdpCases();
            TrkMetadataCases();
            TrkLsdCases();
        }

        // ------------------------------------------------------------------ //
        //  Подделки: рой, контекст, ожидание
        // ------------------------------------------------------------------ //
        private sealed class TrkSwarm : IBtSwarm
        {
            public byte[] Hash;
            public bool Private;
            public BtMeta MetaValue;
            public bool IsActive = true;
            public readonly List<KeyValuePair<BtEndpoint, BtPeerOrigin>> Peers = new List<KeyValuePair<BtEndpoint, BtPeerOrigin>>();
            public readonly List<string> Journals = new List<string>();
            public int MetadataCalls, MetadataRejected;
            public byte[] Accepted;

            public byte[] InfoHash { get { return Hash; } }
            public bool IsPrivate { get { return Private; } }
            public BtMeta Meta { get { return MetaValue; } }
            public long Uploaded { get { return 1000; } }
            public long Downloaded { get { return 2000; } }
            public long Left { get { return MetaValue == null ? 0 : 3000; } }   // до метаданных — 0, как в контракте
            public bool IsSeed { get { return false; } }
            public int NumWant { get { return 50; } }
            public bool Active { get { return IsActive; } }

            public void AddPeers(IList<BtEndpoint> peers, BtPeerOrigin origin)
            {
                lock (Peers) foreach (BtEndpoint p in peers) Peers.Add(new KeyValuePair<BtEndpoint, BtPeerOrigin>(p, origin));
            }

            public List<BtPeerInfo> ConnectedPeers() { return new List<BtPeerInfo>(); }

            public bool OnMetadata(byte[] infoBytes)
            {
                lock (Peers)
                {
                    MetadataCalls++;
                    if (MetaValue != null) return false;
                    string err;
                    BtMeta m = BtMeta.FromInfo(infoBytes, Hash, out err);
                    if (m == null) { MetadataRejected++; return false; }
                    MetaValue = m;
                    Accepted = infoBytes;
                    return true;
                }
            }

            public void Journal(string text) { lock (Journals) Journals.Add(text); }

            public int PeerCount { get { lock (Peers) return Peers.Count; } }

            public bool HasPeer(string endpoint, BtPeerOrigin origin)
            {
                lock (Peers)
                    foreach (KeyValuePair<BtEndpoint, BtPeerOrigin> kv in Peers)
                        if (kv.Key.ToString() == endpoint && kv.Value == origin) return true;
                return false;
            }

            public string AllJournal { get { lock (Journals) return string.Join("\n", Journals.ToArray()); } }
        }

        private static byte[] TrkHash(int seed)
        {
            // Байты, которые проверяют кодирование: пробел, %, &, +, =, ~, буквы, 0x00 и 0xFF.
            byte[] h = new byte[] { 0x00, 0x20, 0x25, 0x26, 0x2B, 0x3D, 0x41, 0x7E, 0xFF, 0x2F, 0x3F, 0x23, 0x80, 0x61, 0x2E, 0x5F, 0x2D, 0x0A, 0x01, 0x00 };
            h[19] = (byte)seed;
            return h;
        }

        private static BtMeta TrkSmallMeta()
        {
            string err;
            return BtMeta.Parse(BtFx.Build("small.bin", new List<BtFxFile> { new BtFxFile(BtFx.Data(3000, 7)) }, 16384, 1, false, null), out err);
        }

        private static BtContext TrkCtx(List<string> log)
        {
            BtContext ctx = new BtContext();
            ctx.PeerId = BtContext.NewPeerId();
            ctx.Port = 6881;
            ctx.AnnounceKey = 0xDEADBEEF;
            ctx.Log = delegate(string s) { lock (log) log.Add(s); };
            return ctx;
        }

        private static bool TrkWaitFor(Func<bool> cond, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                Thread.Sleep(20);
            }
            return cond();
        }

        // Ждать, раз в 50 мс вызывая Tick (UDP-повторы и переход по уровням живут в нём).
        private static bool TrkTickUntil(IBtTrackers t, Func<bool> cond, int ms)
        {
            DateTime end = DateTime.UtcNow.AddMilliseconds(ms);
            while (DateTime.UtcNow < end)
            {
                if (cond()) return true;
                t.Tick(DateTime.UtcNow);
                Thread.Sleep(50);
            }
            return cond();
        }

        private static List<List<string>> TrkTiers(params string[][] tiers)
        {
            List<List<string>> l = new List<List<string>>();
            foreach (string[] t in tiers) l.Add(new List<string>(t));
            return l;
        }

        // ------------------------------------------------------------------ //
        //  HTTP-трекер на TcpListener
        // ------------------------------------------------------------------ //
        private sealed class TrkHttpReply
        {
            public int Code = 200;
            public byte[] Body = new byte[0];
            public string Location;
            public bool Gzip;
        }

        private sealed class TrkHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Thread _thread;
            private volatile bool _stop;
            public readonly int Port;
            public readonly List<KeyValuePair<string, string>> Hits = new List<KeyValuePair<string, string>>();   // путь → сырой query
            public Func<string, string, TrkHttpReply> Handler;

            public TrkHttpServer()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                _thread = new Thread(Accept);
                _thread.IsBackground = true;
                _thread.Start();
            }

            public string Url(string pathAndQuery) { return "http://127.0.0.1:" + Port + pathAndQuery; }

            public int Count(string path)
            {
                int n = 0;
                lock (Hits) foreach (KeyValuePair<string, string> h in Hits) if (h.Key == path) n++;
                return n;
            }

            public int IndexOf(string path)
            {
                lock (Hits) for (int i = 0; i < Hits.Count; i++) if (Hits[i].Key == path) return i;
                return -1;
            }

            public string LastQuery(string path)
            {
                lock (Hits) for (int i = Hits.Count - 1; i >= 0; i--) if (Hits[i].Key == path) return Hits[i].Value;
                return null;
            }

            private void Accept()
            {
                while (!_stop)
                {
                    TcpClient c;
                    try { c = _listener.AcceptTcpClient(); }
                    catch (SocketException) { return; }
                    catch (ObjectDisposedException) { return; }
                    try { Serve(c); }
                    catch (IOException) { }
                    catch (SocketException) { }
                    finally { c.Close(); }
                }
            }

            private void Serve(TcpClient c)
            {
                NetworkStream s = c.GetStream();
                s.ReadTimeout = 5000;
                MemoryStream head = new MemoryStream();
                byte[] one = new byte[1];
                while (head.Length < 16384)
                {
                    int n = s.Read(one, 0, 1);
                    if (n <= 0) return;
                    head.WriteByte(one[0]);
                    byte[] h = head.GetBuffer();
                    long l = head.Length;
                    if (l >= 4 && h[l - 4] == 13 && h[l - 3] == 10 && h[l - 2] == 13 && h[l - 1] == 10) break;
                }
                string first = Encoding.ASCII.GetString(head.ToArray()).Split('\r')[0];
                string[] parts = first.Split(' ');
                if (parts.Length < 2) return;
                string target = parts[1];
                int q = target.IndexOf('?');
                string path = q < 0 ? target : target.Substring(0, q);
                string query = q < 0 ? "" : target.Substring(q + 1);
                lock (Hits) Hits.Add(new KeyValuePair<string, string>(path, query));
                TrkHttpReply r = Handler == null ? null : Handler(path, query);
                if (r == null) { r = new TrkHttpReply(); r.Code = 404; }
                byte[] body = r.Body;
                if (r.Gzip)
                {
                    MemoryStream z = new MemoryStream();
                    using (GZipStream g = new GZipStream(z, CompressionMode.Compress, true)) g.Write(body, 0, body.Length);
                    body = z.ToArray();
                }
                StringBuilder sb = new StringBuilder();
                sb.Append("HTTP/1.1 ").Append(r.Code).Append(r.Code == 200 ? " OK" : r.Code == 302 ? " Found" : " Not Found").Append("\r\n");
                sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
                sb.Append("Content-Type: text/plain\r\nConnection: close\r\n");
                if (r.Gzip) sb.Append("Content-Encoding: gzip\r\n");
                if (r.Location != null) sb.Append("Location: ").Append(r.Location).Append("\r\n");
                sb.Append("\r\n");
                byte[] hb = Encoding.ASCII.GetBytes(sb.ToString());
                s.Write(hb, 0, hb.Length);
                s.Write(body, 0, body.Length);
                s.Flush();
            }

            public void Dispose()
            {
                _stop = true;
                try { _listener.Stop(); } catch { }
                _thread.Join(3000);
            }
        }

        // Разбор query на стороне сервера: %XX → байт, как это делает трекер.
        private static Dictionary<string, byte[]> TrkQuery(string query)
        {
            Dictionary<string, byte[]> d = new Dictionary<string, byte[]>();
            if (query == null) return d;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string v = pair.Substring(eq + 1);
                MemoryStream ms = new MemoryStream();
                for (int i = 0; i < v.Length; i++)
                {
                    if (v[i] == '%' && i + 2 < v.Length)
                    {
                        ms.WriteByte(Convert.ToByte(v.Substring(i + 1, 2), 16));
                        i += 2;
                    }
                    else ms.WriteByte((byte)v[i]);
                }
                d[pair.Substring(0, eq)] = ms.ToArray();
            }
            return d;
        }

        private static string TrkQs(Dictionary<string, byte[]> d, string key)
        {
            byte[] b;
            return d.TryGetValue(key, out b) ? Encoding.ASCII.GetString(b) : null;
        }

        private static TrkHttpReply TrkBody(BVal dict)
        {
            TrkHttpReply r = new TrkHttpReply();
            r.Body = Bencode.Encode(dict);
            return r;
        }

        private static byte[] TrkCompact(params string[] endpoints)
        {
            MemoryStream ms = new MemoryStream();
            foreach (string e in endpoints)
            {
                byte[] b = BtEndpoint.TryParse(e).ToCompact();
                ms.Write(b, 0, b.Length);
            }
            return ms.ToArray();
        }

        private static string TrkSnapshotText(IBtTrackers t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (BtTrackerInfo i in t.Snapshot()) sb.Append(i.Tier).Append(' ').Append(i.Url).Append(" | ").Append(i.Status).Append(" | ").Append(i.Message).Append('\n');
            return sb.ToString();
        }

        private static void TrkHttpCases()
        {
            bool shuffle = BtTrackers.ShuffleTiers;
            BtTrackers.ShuffleTiers = false;
            try
            {
                using (TrkHttpServer srv = new TrkHttpServer())
                {
                    srv.Handler = delegate(string path, string query)
                    {
                        if (path == "/announce")
                            return TrkBody(BVal.NewDict()
                                .Set("interval", BVal.Int(1800)).Set("min interval", BVal.Int(60))
                                .Set("complete", BVal.Int(5)).Set("incomplete", BVal.Int(7))
                                .Set("peers", BVal.Bytes(TrkCompact("10.0.0.1:6881", "10.0.0.2:51413")))
                                .Set("peers6", BVal.Bytes(TrkCompact("[2001:db8::1]:6882")))
                                .Set("warning message", BVal.Str("be nice"))
                                .Set("tracker id", BVal.Str("tid-42")));
                        if (path == "/moved")
                        {
                            TrkHttpReply mv = new TrkHttpReply();
                            mv.Code = 302;
                            mv.Location = "http://127.0.0.1:" + srv.Port + "/dict?" + query;
                            return mv;
                        }
                        if (path == "/dict")
                        {
                            BVal list = BVal.NewList()
                                .Add(BVal.NewDict().Set("ip", BVal.Str("192.168.1.5")).Set("port", BVal.Int(7000)))
                                .Add(BVal.NewDict().Set("ip", BVal.Str("peer.example.com")).Set("port", BVal.Int(7001)))
                                .Add(BVal.NewDict().Set("ip", BVal.Str("192.168.1.6")).Set("port", BVal.Int(0)));
                            TrkHttpReply z = TrkBody(BVal.NewDict().Set("interval", BVal.Int(900)).Set("peers", list));
                            z.Gzip = true;
                            return z;
                        }
                        if (path == "/fail") return TrkBody(BVal.NewDict().Set("failure reason", BVal.Str("unregistered torrent")));
                        if (path == "/loop")
                        {
                            TrkHttpReply lp = new TrkHttpReply();
                            lp.Code = 302;
                            lp.Location = "/loop?" + query;
                            return lp;
                        }
                        if (path == "/huge")
                        {
                            TrkHttpReply hg = new TrkHttpReply();
                            hg.Body = new byte[2 * 1024 * 1024 + 4096];
                            return hg;
                        }
                        if (path == "/0123456789abcdef0123456789abcdef/announce")
                            return TrkBody(BVal.NewDict().Set("failure reason", BVal.Str("passkey denied")));
                        if (path == "/fedcba9876543210fedcba9876543210/announce")
                            return TrkBody(BVal.NewDict().Set("interval", BVal.Int(1800)).Set("peers", BVal.Bytes(TrkCompact("10.9.9.9:1000"))));
                        return null;
                    };

                    // ---------- объявление: параметры глазами сервера, три вида пиров ----------
                    List<string> log = new List<string>();
                    BtContext ctx = TrkCtx(log);
                    TrkSwarm swarm = new TrkSwarm();
                    swarm.Hash = TrkHash(1);
                    swarm.MetaValue = TrkSmallMeta();
                    using (BtTrackers tr = new BtTrackers(swarm, ctx, TrkTiers(new[] { srv.Url("/announce") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkWaitFor(delegate { return swarm.PeerCount >= 3; }, 8000);
                        Dictionary<string, byte[]> q = TrkQuery(srv.LastQuery("/announce"));
                        byte[] ih, pid;
                        q.TryGetValue("info_hash", out ih);
                        q.TryGetValue("peer_id", out pid);
                        T.Check("trackers: http announce reaches the tracker and its compact peers reach the swarm", got, srv.Count("/announce") + " hits, " + swarm.PeerCount + " peers");
                        T.Check("trackers: http info_hash and peer_id arrive as the exact 20 bytes (url-encoded)",
                                Bencode.SameBytes(ih, swarm.Hash) && Bencode.SameBytes(pid, ctx.PeerId), Bencode.Hex(ih));
                        T.Eq("trackers: http port/uploaded/downloaded/left/compact/numwant/key/event decoded server-side",
                             "6881|1000|2000|3000|1|50|deadbeef|started",
                             TrkQs(q, "port") + "|" + TrkQs(q, "uploaded") + "|" + TrkQs(q, "downloaded") + "|" + TrkQs(q, "left") + "|"
                             + TrkQs(q, "compact") + "|" + TrkQs(q, "numwant") + "|" + TrkQs(q, "key") + "|" + TrkQs(q, "event"));
                        T.Check("trackers: compact v4 peers and peers6 reach the swarm with origin Tracker",
                                swarm.HasPeer("10.0.0.1:6881", BtPeerOrigin.Tracker) && swarm.HasPeer("10.0.0.2:51413", BtPeerOrigin.Tracker)
                                && swarm.HasPeer("[2001:db8::1]:6882", BtPeerOrigin.Tracker), swarm.PeerCount.ToString());
                        BtTrackerInfo info = tr.Snapshot()[0];
                        T.Check("trackers: snapshot shows working status, seeders/leechers, warning message and next announce",
                                info.Status == Tr.S("работает", "working") && info.Seeders == 5 && info.Leechers == 7 && info.Message == "be nice"
                                && info.PeersReceived == 3 && info.NextAnnounceUtc > DateTime.UtcNow.AddMinutes(25), TrkSnapshotText(tr));

                        tr.Announce(BtAnnounceEvent.Completed);
                        TrkWaitFor(delegate { return srv.Count("/announce") >= 2; }, 8000);
                        Dictionary<string, byte[]> q2 = TrkQuery(srv.LastQuery("/announce"));
                        T.Eq("trackers: completed event carries the tracker id from the previous reply", "completed|tid-42", TrkQs(q2, "event") + "|" + TrkQs(q2, "trackerid"));

                        // min interval 60 с — ручное обновление сразу после ответа не уходит.
                        tr.Reannounce();
                        Thread.Sleep(300);
                        T.Eq("trackers: Reannounce inside min interval sends nothing", 2, srv.Count("/announce"));

                        // Stopped — без ожидания, и Dispose сразу за ним его не отменяет.
                        tr.Announce(BtAnnounceEvent.Stopped);
                    }
                    bool stopped = TrkWaitFor(delegate { string lq = srv.LastQuery("/announce"); return lq != null && lq.Contains("event=stopped"); }, 8000);
                    T.Check("trackers: Stopped announce is sent best-effort and survives an immediate Dispose", stopped, srv.Count("/announce") + " hits");

                    // ---------- перенаправление, gzip, словарные пиры ----------
                    TrkSwarm s2 = new TrkSwarm();
                    s2.Hash = TrkHash(2);
                    using (BtTrackers tr = new BtTrackers(s2, TrkCtx(new List<string>()), TrkTiers(new[] { srv.Url("/moved") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkTickUntil(tr, delegate { return tr.Snapshot()[0].Status == Tr.S("работает", "working"); }, 8000);
                        Dictionary<string, byte[]> q = TrkQuery(srv.LastQuery("/dict"));
                        byte[] ih;
                        q.TryGetValue("info_hash", out ih);
                        T.Eq("trackers: magnet without metadata announces left=16384, not 0 (would look like a seed)", "16384", TrkQs(q, "left"));
                        T.Check("trackers: redirect followed, gzip reply decoded, dict peers parsed (host names and port 0 dropped)",
                                got && Bencode.SameBytes(ih, s2.Hash) && s2.PeerCount == 1 && s2.HasPeer("192.168.1.5:7000", BtPeerOrigin.Tracker),
                                TrkSnapshotText(tr) + " peers " + s2.PeerCount);
                    }

                    // ---------- failure reason ----------
                    TrkSwarm s3 = new TrkSwarm();
                    s3.Hash = TrkHash(3);
                    using (BtTrackers tr = new BtTrackers(s3, TrkCtx(new List<string>()), TrkTiers(new[] { srv.Url("/fail") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        string want = Tr.S("ошибка: ", "error: ") + "unregistered torrent";
                        bool got = TrkTickUntil(tr, delegate { return tr.Snapshot()[0].Status == want; }, 8000);
                        T.Check("trackers: failure reason surfaces in the snapshot status, message and the journal",
                                got && tr.Snapshot()[0].Message == "unregistered torrent" && s3.AllJournal.Contains("unregistered torrent") && s3.PeerCount == 0,
                                TrkSnapshotText(tr) + " / " + s3.AllJournal);
                    }

                    // ---------- кольцо перенаправлений и огромный ответ ----------
                    TrkSwarm s4 = new TrkSwarm();
                    s4.Hash = TrkHash(4);
                    using (BtTrackers tr = new BtTrackers(s4, TrkCtx(new List<string>()), TrkTiers(new[] { srv.Url("/loop") }, new[] { srv.Url("/huge") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool done = TrkTickUntil(tr, delegate { return srv.Count("/huge") >= 1 && tr.Snapshot()[1].Status.StartsWith(Tr.S("ошибка", "error")); }, 10000);
                        List<BtTrackerInfo> snap = tr.Snapshot();
                        T.Check("trackers: redirect loop — initial request + exactly 3 redirects followed, then an error",
                                done && srv.Count("/loop") == BtTrackers.MaxRedirects + 1
                                && snap[0].Status == Tr.S("ошибка: ", "error: ") + Tr.S("слишком много перенаправлений", "too many redirects"),
                                srv.Count("/loop") + " loop hits; " + TrkSnapshotText(tr));
                        T.Check("trackers: reply over 2 MiB refused", done && snap[1].Status.Contains(Tr.S("больше 2 МБ", "larger than 2 MB")), TrkSnapshotText(tr));
                    }

                    // ---------- passkey нигде не виден ----------
                    List<string> plog = new List<string>();
                    TrkSwarm s5 = new TrkSwarm();
                    s5.Hash = TrkHash(5);
                    string keyA = "0123456789abcdef0123456789abcdef", keyB = "fedcba9876543210fedcba9876543210";
                    using (BtTrackers tr = new BtTrackers(s5, TrkCtx(plog), TrkTiers(
                        new[] { srv.Url("/" + keyA + "/announce?passkey=SECRETKEY42") },
                        new[] { srv.Url("/" + keyB + "/announce?uk=SECRETUK77") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkWaitFor(delegate { return s5.PeerCount >= 1; }, 8000);
                        string reachedQuery = srv.LastQuery("/" + keyA + "/announce") ?? "";
                        string all = TrkSnapshotText(tr) + "\n" + s5.AllJournal + "\n";
                        lock (plog) all += string.Join("\n", plog.ToArray());
                        bool leak = all.Contains(keyA) || all.Contains(keyB) || all.Contains("SECRETKEY42") || all.Contains("SECRETUK77");
                        T.Check("trackers: passkey in path and query reaches the tracker but never Snapshot, log or journal",
                                got && reachedQuery.Contains("passkey=SECRETKEY42") && !leak && all.Contains("***") && plog.Count > 0 && s5.Journals.Count > 0, all);
                    }
                }
            }
            finally
            {
                BtTrackers.ShuffleTiers = shuffle;
            }
        }

        // ------------------------------------------------------------------ //
        //  Уровни BEP 12
        // ------------------------------------------------------------------ //
        private static void TrkTierCases()
        {
            bool shuffle = BtTrackers.ShuffleTiers;
            BtTrackers.ShuffleTiers = false;
            try
            {
                using (TrkHttpServer srv = new TrkHttpServer())
                {
                    srv.Handler = delegate(string path, string query)
                    {
                        if (path.StartsWith("/ok"))
                            return TrkBody(BVal.NewDict().Set("interval", BVal.Int(1800)).Set("peers", BVal.Bytes(TrkCompact("10.5.5." + path.Substring(3) + ":1000"))));
                        if (path.StartsWith("/fail")) return TrkBody(BVal.NewDict().Set("failure reason", BVal.Str("nope")));
                        return null;   // 404
                    };

                    TrkSwarm s1 = new TrkSwarm();
                    s1.Hash = TrkHash(11);
                    using (BtTrackers tr = new BtTrackers(s1, TrkCtx(new List<string>()), TrkTiers(new[] { srv.Url("/fail1"), srv.Url("/ok1") }, new[] { srv.Url("/ok2") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkTickUntil(tr, delegate { return srv.Count("/ok1") >= 1 && s1.PeerCount >= 1; }, 8000);
                        for (int i = 0; i < 6; i++) { tr.Tick(DateTime.UtcNow); Thread.Sleep(50); }
                        List<BtTrackerInfo> snap = tr.Snapshot();
                        T.Check("trackers: tier fallback — second tracker of tier 0 asked after the first failed; tier 1 untouched",
                                got && srv.Count("/fail1") == 1 && srv.Count("/ok2") == 0, "fail1 " + srv.Count("/fail1") + ", ok2 " + srv.Count("/ok2"));
                        T.Check("trackers: tier fallback — the working tracker moved to the front of its tier",
                                snap.Count == 3 && snap[0].Tier == 0 && snap[0].Url.EndsWith("/ok1") && snap[1].Url.EndsWith("/fail1") && snap[2].Tier == 1,
                                TrkSnapshotText(tr));
                    }

                    TrkSwarm s2 = new TrkSwarm();
                    s2.Hash = TrkHash(12);
                    using (BtTrackers tr = new BtTrackers(s2, TrkCtx(new List<string>()), TrkTiers(new[] { srv.Url("/fail2"), srv.Url("/missing") }, new[] { srv.Url("/ok3") })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkTickUntil(tr, delegate { return s2.HasPeer("10.5.5.3:1000", BtPeerOrigin.Tracker); }, 8000);
                        T.Check("trackers: tier fallback — next tier asked only after every tracker of the tier failed",
                                got && srv.IndexOf("/fail2") >= 0 && srv.IndexOf("/missing") > srv.IndexOf("/fail2") && srv.IndexOf("/ok3") > srv.IndexOf("/missing"),
                                TrkSnapshotText(tr));
                        List<BtTrackerInfo> snap = tr.Snapshot();
                        T.Check("trackers: HTTP 404 reported as an error status", snap[1].Status == Tr.S("ошибка: ", "error: ") + "HTTP 404", TrkSnapshotText(tr));
                    }
                }
            }
            finally
            {
                BtTrackers.ShuffleTiers = shuffle;
            }
        }

        // ------------------------------------------------------------------ //
        //  UDP-трекер (BEP 15) против настоящего BtUdp
        // ------------------------------------------------------------------ //
        private static int TrkBe32(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        private static long TrkBe64(byte[] b, int o) { return ((long)(uint)TrkBe32(b, o) << 32) | (uint)TrkBe32(b, o + 4); }

        private static void TrkPut32(MemoryStream ms, int v) { ms.WriteByte((byte)(v >> 24)); ms.WriteByte((byte)(v >> 16)); ms.WriteByte((byte)(v >> 8)); ms.WriteByte((byte)v); }

        private sealed class TrkUdpServer : IDisposable
        {
            public const long ConnId = 0x0102030405060708L;
            private readonly Socket _s;
            private readonly Thread _thread;
            private volatile bool _stop;
            public readonly int Port;
            public volatile int DropConnects;
            public volatile string ErrorText;
            public byte[] Peers = new byte[0];
            public int Connects, Announces;
            public readonly List<byte[]> AnnouncePackets = new List<byte[]>();

            public TrkUdpServer()
            {
                _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                _s.IOControl(-1744830452, new byte[4], null);
                _s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                Port = ((IPEndPoint)_s.LocalEndPoint).Port;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Start();
            }

            private void Loop()
            {
                byte[] buf = new byte[4096];
                while (!_stop)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = _s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { if (_stop) return; continue; }
                    catch (ObjectDisposedException) { return; }
                    MemoryStream reply = new MemoryStream();
                    if (n >= 16 && TrkBe64(buf, 0) == 0x41727101980L && TrkBe32(buf, 8) == 0)
                    {
                        int c = Interlocked.Increment(ref Connects);
                        if (c <= DropConnects) continue;
                        TrkPut32(reply, 0);
                        TrkPut32(reply, TrkBe32(buf, 12));
                        TrkPut32(reply, (int)(ConnId >> 32));
                        TrkPut32(reply, unchecked((int)ConnId));
                    }
                    else if (n >= 98 && TrkBe64(buf, 0) == ConnId && TrkBe32(buf, 8) == 1)
                    {
                        byte[] copy = new byte[n];
                        Buffer.BlockCopy(buf, 0, copy, 0, n);
                        lock (AnnouncePackets) AnnouncePackets.Add(copy);
                        Interlocked.Increment(ref Announces);
                        string err = ErrorText;
                        if (err != null)
                        {
                            TrkPut32(reply, 3);
                            TrkPut32(reply, TrkBe32(buf, 12));
                            byte[] t = Encoding.UTF8.GetBytes(err);
                            reply.Write(t, 0, t.Length);
                        }
                        else
                        {
                            TrkPut32(reply, 1);
                            TrkPut32(reply, TrkBe32(buf, 12));
                            TrkPut32(reply, 1800);
                            TrkPut32(reply, 4);
                            TrkPut32(reply, 9);
                            reply.Write(Peers, 0, Peers.Length);
                        }
                    }
                    else continue;
                    try { _s.SendTo(reply.ToArray(), from); } catch (SocketException) { }
                }
            }

            public byte[] LastAnnounce { get { lock (AnnouncePackets) return AnnouncePackets.Count == 0 ? null : AnnouncePackets[AnnouncePackets.Count - 1]; } }

            public void Dispose()
            {
                _stop = true;
                try { _s.Close(); } catch { }
                _thread.Join(3000);
            }
        }

        private static void TrkUdpCases()
        {
            double baseSec = BtTrackers.UdpRetransmitBaseSeconds, fallback = BtTrackers.FallbackAfterSeconds;
            bool shuffle = BtTrackers.ShuffleTiers;
            BtTrackers.UdpRetransmitBaseSeconds = 0.3;
            BtTrackers.ShuffleTiers = false;
            try
            {
                using (BtUdp udp = new BtUdp(IPAddress.Loopback, 0))
                using (TrkUdpServer srv = new TrkUdpServer())
                {
                    srv.DropConnects = 1;
                    srv.Peers = TrkCompact("10.1.2.3:4000", "10.1.2.4:4001");
                    List<string> log = new List<string>();
                    BtContext ctx = TrkCtx(log);
                    ctx.Udp = udp;
                    TrkSwarm swarm = new TrkSwarm();
                    swarm.Hash = TrkHash(21);
                    swarm.MetaValue = TrkSmallMeta();
                    string url = "udp://127.0.0.1:" + srv.Port + "/announce?passkey=UDPSECRET";
                    using (BtTrackers tr = new BtTrackers(swarm, ctx, TrkTiers(new[] { url })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        bool got = TrkTickUntil(tr, delegate { return swarm.PeerCount >= 2; }, 8000);
                        T.Check("trackers: udp connect reply lost → retransmitted → announce → peers reach the swarm over real BtUdp",
                                got && srv.Connects == 2 && srv.Announces == 1 && swarm.HasPeer("10.1.2.3:4000", BtPeerOrigin.Tracker) && swarm.HasPeer("10.1.2.4:4001", BtPeerOrigin.Tracker),
                                "connects " + srv.Connects + ", announces " + srv.Announces + ", peers " + swarm.PeerCount);
                        byte[] p = srv.LastAnnounce ?? new byte[98];
                        byte[] ih = new byte[20], pid = new byte[20];
                        Buffer.BlockCopy(p, 16, ih, 0, 20);
                        Buffer.BlockCopy(p, 36, pid, 0, 20);
                        T.Check("trackers: udp announce info_hash and peer_id bytes decoded server-side", Bencode.SameBytes(ih, swarm.Hash) && Bencode.SameBytes(pid, ctx.PeerId));
                        T.Eq("trackers: udp announce downloaded/left/uploaded/event/key/num_want/port decoded server-side",
                             "2000|3000|1000|2|deadbeef|50|6881",
                             TrkBe64(p, 56) + "|" + TrkBe64(p, 64) + "|" + TrkBe64(p, 72) + "|" + TrkBe32(p, 80) + "|"
                             + ((uint)TrkBe32(p, 88)).ToString("x8") + "|" + TrkBe32(p, 92) + "|" + ((p[96] << 8) | p[97]));
                        // BEP 41: опции после 98 байт — 0x02 len data … 0x00.
                        StringBuilder urlData = new StringBuilder();
                        int pos = 98;
                        bool ended = false;
                        while (pos < p.Length)
                        {
                            if (p[pos] == 0) { ended = true; break; }
                            if (p[pos] != 2 || pos + 1 >= p.Length) break;
                            int len = p[pos + 1];
                            urlData.Append(Encoding.ASCII.GetString(p, pos + 2, Math.Min(len, p.Length - pos - 2)));
                            pos += 2 + len;
                        }
                        T.Eq("trackers: udp BEP 41 URL data carries path and query", "/announce?passkey=UDPSECRET|True", urlData + "|" + ended);
                        BtTrackerInfo info = tr.Snapshot()[0];
                        string logText;
                        lock (log) logText = string.Join("\n", log.ToArray());
                        T.Check("trackers: udp snapshot — working, seeders 9, leechers 4, passkey hidden in snapshot and log",
                                info.Status == Tr.S("работает", "working") && info.Seeders == 9 && info.Leechers == 4 && !info.Url.Contains("UDPSECRET")
                                && !logText.Contains("UDPSECRET") && !swarm.AllJournal.Contains("UDPSECRET"), TrkSnapshotText(tr));

                        tr.Announce(BtAnnounceEvent.Completed);
                        bool second = TrkTickUntil(tr, delegate { return srv.Announces >= 2; }, 5000);
                        byte[] p2 = srv.LastAnnounce;
                        T.Check("trackers: udp connection id reused within 60 s (no second connect), event completed = 1",
                                second && srv.Connects == 2 && p2 != null && TrkBe32(p2, 80) == 1, "connects " + srv.Connects + ", announces " + srv.Announces);

                        byte[] krpc = Encoding.ASCII.GetBytes("d1:ad2:id20:abcdefghij0123456789e1:q4:ping1:t2:aa1:y1:qe");
                        T.Check("trackers: udp handler does not claim a DHT datagram", !tr.HandleDatagram(new BtEndpoint(IPAddress.Loopback, srv.Port), krpc, krpc.Length));
                    }

                    // action 3 — текст ошибки трекера.
                    srv.ErrorText = "torrent not registered";
                    TrkSwarm s2 = new TrkSwarm();
                    s2.Hash = TrkHash(22);
                    using (BtTrackers tr = new BtTrackers(s2, ctx, TrkTiers(new[] { "udp://127.0.0.1:" + srv.Port })))
                    {
                        tr.Announce(BtAnnounceEvent.Started);
                        string want = Tr.S("ошибка: ", "error: ") + "torrent not registered";
                        bool got = TrkTickUntil(tr, delegate { return tr.Snapshot()[0].Status == want; }, 5000);
                        T.Check("trackers: udp error action surfaces in the snapshot", got, TrkSnapshotText(tr));
                    }
                    srv.ErrorText = null;

                    // Молчащий UDP-трекер не держит уровень: следующий спрашивается по FallbackAfterSeconds.
                    BtTrackers.FallbackAfterSeconds = 0.8;
                    using (TrkUdpServer silent = new TrkUdpServer())
                    using (TrkHttpServer http = new TrkHttpServer())
                    {
                        silent.DropConnects = int.MaxValue;
                        http.Handler = delegate(string path, string query) { return TrkBody(BVal.NewDict().Set("interval", BVal.Int(1800)).Set("peers", BVal.Bytes(TrkCompact("10.7.7.7:7777")))); };
                        TrkSwarm s3 = new TrkSwarm();
                        s3.Hash = TrkHash(23);
                        using (BtTrackers tr = new BtTrackers(s3, ctx, TrkTiers(new[] { "udp://127.0.0.1:" + silent.Port + "/announce" }, new[] { http.Url("/announce") })))
                        {
                            DateTime start = DateTime.UtcNow;
                            tr.Announce(BtAnnounceEvent.Started);
                            bool early = TrkTickUntil(tr, delegate { return http.Count("/announce") > 0; }, 500);
                            bool got = TrkTickUntil(tr, delegate { return s3.PeerCount >= 1; }, 6000);
                            List<BtTrackerInfo> snap = tr.Snapshot();
                            T.Check("trackers: silent udp tracker retransmits, and after the fallback delay the next tier answers",
                                    !early && got && silent.Connects >= 2 && snap[1].Status == Tr.S("работает", "working") && snap[0].Status == Tr.S("не связывался", "not contacted"),
                                    "connects " + silent.Connects + " after " + (DateTime.UtcNow - start).TotalMilliseconds + " ms; " + TrkSnapshotText(tr));
                        }
                    }
                }
            }
            finally
            {
                BtTrackers.UdpRetransmitBaseSeconds = baseSec;
                BtTrackers.FallbackAfterSeconds = fallback;
                BtTrackers.ShuffleTiers = shuffle;
            }
        }

        // ------------------------------------------------------------------ //
        //  ut_metadata: пары соединений в памяти
        // ------------------------------------------------------------------ //
        private sealed class TrkWire
        {
            public readonly Queue<Action> Queue = new Queue<Action>();

            public void Pump()
            {
                for (int guard = 0; guard < 10000; guard++)
                {
                    Action a;
                    lock (Queue)
                    {
                        if (Queue.Count == 0) return;
                        a = Queue.Dequeue();
                    }
                    a();
                }
            }
        }

        private sealed class TrkLink : IBtPeerLink
        {
            public TrkWire Wire;
            public BtEndpoint Ep;
            public IBtExtension Remote;       // расширение на том конце
            public TrkLink Back;              // это же соединение глазами того конца
            public bool Mute, Corrupt;
            public int Requests, Data, Rejects;
            public readonly List<byte[]> Sent = new List<byte[]>();

            public BtEndpoint Endpoint { get { return Ep; } }
            public int ListenPort { get { return 0; } }
            public bool Outgoing { get { return true; } }
            public bool Encrypted { get { return false; } }
            public bool IsSeed { get { return false; } }
            public string Client { get { return "test"; } }
            public bool Supports(string extension) { return extension == "ut_metadata"; }
            public void Close(string reason) { }

            public void SendExtended(string extension, byte[] payload)
            {
                byte[] copy = (byte[])payload.Clone();
                int consumed;
                string err;
                BVal d = Bencode.DecodePrefix(copy, 0, copy.Length, out consumed, out err);
                long type = d == null ? -1 : d.GetInt("msg_type", -1);
                lock (Sent)
                {
                    Sent.Add(copy);
                    if (type == 0) Requests++;
                    else if (type == 1) Data++;
                    else if (type == 2) Rejects++;
                }
                if (Mute || Remote == null) return;
                if (Corrupt && type == 1) copy[copy.Length - 1] ^= 0x5A;
                IBtExtension remote = Remote;
                TrkLink back = Back;
                lock (Wire.Queue) Wire.Queue.Enqueue(delegate { remote.OnMessage(back, copy, 0, copy.Length); });
            }
        }

        // Соединить два расширения: рукопожатия проходят настоящий bencode. Возвращает соединение глазами a.
        private static TrkLink TrkConnect(TrkWire wire, IBtExtension a, string ipA, IBtExtension b, string ipB)
        {
            TrkLink ab = new TrkLink();
            TrkLink ba = new TrkLink();
            ab.Wire = ba.Wire = wire;
            ab.Ep = new BtEndpoint(IPAddress.Parse(ipB), 6881);
            ba.Ep = new BtEndpoint(IPAddress.Parse(ipA), 6881);
            ab.Remote = b;
            ab.Back = ba;
            ba.Remote = a;
            ba.Back = ab;
            BVal ha = BVal.NewDict(), hb = BVal.NewDict();
            a.FillHandshake(ha);
            b.FillHandshake(hb);
            string err;
            BVal wireA = Bencode.Decode(Bencode.Encode(ha), out err);
            BVal wireB = Bencode.Decode(Bencode.Encode(hb), out err);
            b.OnHandshake(ba, wireA);
            a.OnHandshake(ab, wireB);
            return ab;
        }

        private static BtMeta TrkBigMeta(out byte[] torrent)
        {
            List<BtFxFile> files = new List<BtFxFile>();
            for (int i = 0; i < 300; i++)
                files.Add(new BtFxFile(BtFx.Data(3 + i % 5, i), "folder-with-a-reasonably-long-name", "file-number-" + i.ToString("D4") + "-padding-padding-padding-padding.bin"));
            torrent = BtFx.Build("meta-fixture", files, 16384, 1, false, null);
            string err;
            return BtMeta.Parse(torrent, out err);
        }

        private static void TrkMetadataCases()
        {
            byte[] torrent;
            BtMeta meta = TrkBigMeta(out torrent);
            if (meta == null || meta.InfoBytes.Length <= 2 * BtMetadataExt.PieceSize)
            {
                T.Check("metadata: fixture info dictionary spans 3+ pieces", false, meta == null ? "parse failed" : meta.InfoBytes.Length.ToString());
                return;
            }
            BtContext ctx = TrkCtx(new List<string>());
            int timeout = BtMetadataExt.RequestTimeoutSeconds;
            try
            {
                // ---------- раздача ----------
                TrkSwarm seed = new TrkSwarm();
                seed.Hash = meta.InfoHash;
                seed.MetaValue = meta;
                BtMetadataExt seedExt = new BtMetadataExt(seed, ctx);
                BVal hs = BVal.NewDict();
                seedExt.FillHandshake(hs);
                T.Eq("metadata: handshake carries metadata_size when metadata is known", (long)meta.InfoBytes.Length, hs.GetInt("metadata_size", -1));

                TrkLink capture = new TrkLink();
                capture.Wire = new TrkWire();
                capture.Ep = new BtEndpoint(IPAddress.Parse("127.0.0.9"), 1);
                byte[] req1 = Bencode.Encode(BVal.NewDict().Set("msg_type", BVal.Int(0)).Set("piece", BVal.Int(1)));
                seedExt.OnMessage(capture, req1, 0, req1.Length);
                byte[] reply = capture.Sent.Count == 1 ? capture.Sent[0] : new byte[] { (byte)'e' };
                int consumed;
                string err;
                BVal head = Bencode.DecodePrefix(reply, 0, reply.Length, out consumed, out err);
                bool sameBytes = head != null && reply.Length - consumed == BtMetadataExt.PieceSize;
                for (int i = 0; sameBytes && i < BtMetadataExt.PieceSize; i++) sameBytes = reply[consumed + i] == meta.InfoBytes[BtMetadataExt.PieceSize + i];
                T.Check("metadata: request for piece 1 answered with msg_type 1, total_size and the exact 16 KiB of info bytes",
                        head != null && head.GetInt("msg_type", -1) == 1 && head.GetInt("piece", -1) == 1 && head.GetInt("total_size", -1) == meta.InfoBytes.Length && sameBytes, err);

                TrkSwarm empty = new TrkSwarm();
                empty.Hash = meta.InfoHash;
                BtMetadataExt emptyExt = new BtMetadataExt(empty, ctx);
                BVal hsEmpty = BVal.NewDict();
                emptyExt.FillHandshake(hsEmpty);
                TrkLink capture2 = new TrkLink();
                capture2.Wire = new TrkWire();
                capture2.Ep = capture.Ep;
                emptyExt.OnMessage(capture2, req1, 0, req1.Length);
                byte[] tooFar = Bencode.Encode(BVal.NewDict().Set("msg_type", BVal.Int(0)).Set("piece", BVal.Int(900)));
                seedExt.OnMessage(capture2, tooFar, 0, tooFar.Length);
                T.Check("metadata: no metadata_size and a reject without metadata; out-of-range piece rejected",
                        hsEmpty.Get("metadata_size") == null && capture2.Rejects == 2 && capture2.Data == 0, capture2.Rejects + " rejects");

                // ---------- получение: один пир портит куски ----------
                TrkWire wire = new TrkWire();
                TrkSwarm leech = new TrkSwarm();
                leech.Hash = meta.InfoHash;
                BtMetadataExt leechExt = new BtMetadataExt(leech, ctx);
                BtMetadataExt evilExt = new BtMetadataExt(seed, ctx);
                BtMetadataExt honestExt = new BtMetadataExt(seed, ctx);
                DateTime t0 = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
                leechExt.Tick(t0);
                TrkLink toEvil = TrkConnect(wire, leechExt, "127.0.0.10", evilExt, "127.0.0.2");
                toEvil.Back.Corrupt = true;
                TrkLink toHonest = TrkConnect(wire, leechExt, "127.0.0.10", honestExt, "127.0.0.3");
                for (int i = 0; i < 200 && leech.Accepted == null; i++)
                {
                    wire.Pump();
                    leechExt.Tick(t0.AddSeconds(i));
                }
                T.Check("metadata: corrupted pieces from one peer rejected by the hash check, the peer excluded, the honest peer completes",
                        leech.Accepted != null && Bencode.SameBytes(leech.Accepted, meta.InfoBytes) && leech.MetadataRejected >= 1 && leechExt.BannedCount == 1
                        && leech.AllJournal.Length > 0,
                        "rejected " + leech.MetadataRejected + ", banned " + leechExt.BannedCount + ", requests evil " + toEvil.Requests + " honest " + toHonest.Requests);

                // Только портящий пир — после исключения ему больше не шлют запросов, и метаданные не принимаются.
                TrkWire wire2 = new TrkWire();
                TrkSwarm leech2 = new TrkSwarm();
                leech2.Hash = meta.InfoHash;
                BtMetadataExt leechExt2 = new BtMetadataExt(leech2, ctx);
                leechExt2.Tick(t0);
                TrkLink onlyEvil = TrkConnect(wire2, leechExt2, "127.0.0.10", new BtMetadataExt(seed, ctx), "127.0.0.4");
                onlyEvil.Back.Corrupt = true;
                for (int i = 0; i < 100; i++)
                {
                    wire2.Pump();
                    leechExt2.Tick(t0.AddSeconds(i));
                }
                int requestsAfterBan = onlyEvil.Requests;
                for (int i = 100; i < 200; i++)
                {
                    wire2.Pump();
                    leechExt2.Tick(t0.AddSeconds(i));
                }
                T.Check("metadata: a sole corrupting source is banned and never asked again",
                        leech2.Accepted == null && leech2.MetadataRejected == 1 && leechExt2.BannedCount == 1 && onlyEvil.Requests == requestsAfterBan,
                        "rejected " + leech2.MetadataRejected + ", requests " + requestsAfterBan + " → " + onlyEvil.Requests);

                // ---------- молчащий пир: по таймауту кусок уходит другому ----------
                BtMetadataExt.RequestTimeoutSeconds = 15;
                TrkWire wire3 = new TrkWire();
                TrkSwarm leech3 = new TrkSwarm();
                leech3.Hash = meta.InfoHash;
                BtMetadataExt leechExt3 = new BtMetadataExt(leech3, ctx);
                leechExt3.Tick(t0);
                TrkLink toSilent = TrkConnect(wire3, leechExt3, "127.0.0.10", new BtMetadataExt(seed, ctx), "127.0.0.5");
                toSilent.Back.Mute = true;
                TrkConnect(wire3, leechExt3, "127.0.0.10", new BtMetadataExt(seed, ctx), "127.0.0.6");
                for (int i = 0; i < 5; i++)
                {
                    wire3.Pump();
                    leechExt3.Tick(t0.AddSeconds(i));
                }
                bool stalled = leech3.Accepted == null;
                for (int i = 16; i < 30 && leech3.Accepted == null; i++)
                {
                    leechExt3.Tick(t0.AddSeconds(i));
                    wire3.Pump();
                }
                T.Check("metadata: a silent peer holds its piece only until the request timeout, then another peer completes",
                        stalled && toSilent.Requests >= 1 && leech3.Accepted != null && leech3.MetadataRejected == 0,
                        "stalled " + stalled + ", silent requests " + toSilent.Requests);

                // ---------- размер: больше 16 МБ и меньшинство не спрашиваются ----------
                TrkSwarm leech4 = new TrkSwarm();
                leech4.Hash = meta.InfoHash;
                BtMetadataExt leechExt4 = new BtMetadataExt(leech4, ctx);
                leechExt4.Tick(t0);
                TrkWire wire4 = new TrkWire();
                TrkLink huge = TrkTrickLink(wire4, leechExt4, "127.0.0.20", 17 * 1024 * 1024);
                T.Eq("metadata: metadata_size over 16 MiB never requested", 0, huge.Requests);
                TrkLink major1 = TrkTrickLink(wire4, leechExt4, "127.0.0.22", meta.InfoBytes.Length);
                TrkLink major2 = TrkTrickLink(wire4, leechExt4, "127.0.0.23", meta.InfoBytes.Length);
                TrkLink minority = TrkTrickLink(wire4, leechExt4, "127.0.0.21", meta.InfoBytes.Length + 1);
                leechExt4.Tick(t0.AddSeconds(1));
                leechExt4.Tick(t0.AddSeconds(2));
                T.Check("metadata: majority metadata_size wins — minority-size and oversized peers get no requests",
                        major1.Requests + major2.Requests == 2 && minority.Requests == 0 && huge.Requests == 0,
                        "minority " + minority.Requests + ", majority " + major1.Requests + "+" + major2.Requests);
            }
            finally
            {
                BtMetadataExt.RequestTimeoutSeconds = timeout;
            }
        }

        // Пир без расширения на том конце: объявляет заданный metadata_size и только записывает запросы.
        private static TrkLink TrkTrickLink(TrkWire wire, IBtExtension leech, string ip, long size)
        {
            TrkLink l = new TrkLink();
            l.Wire = wire;
            l.Ep = new BtEndpoint(IPAddress.Parse(ip), 6881);
            leech.OnHandshake(l, BVal.NewDict().Set("metadata_size", BVal.Int(size)));
            return l;
        }

        // ------------------------------------------------------------------ //
        //  LSD: два экземпляра на петле
        // ------------------------------------------------------------------ //
        private static void TrkLsdCases()
        {
            byte[] hash = TrkHash(31), privHash = TrkHash(32);
            TrkSwarm swarmA = new TrkSwarm(), swarmB = new TrkSwarm(), privA = new TrkSwarm();
            swarmA.Hash = hash;
            swarmB.Hash = hash;
            privA.Hash = privHash;
            privA.Private = true;
            BtContext ctxA = TrkCtx(new List<string>()), ctxB = TrkCtx(new List<string>());
            ctxA.Port = 40001;
            ctxB.Port = 40002;
            ctxA.FindSwarm = delegate(byte[] h) { return Bencode.SameBytes(h, hash) ? swarmA : Bencode.SameBytes(h, privHash) ? privA : null; };
            ctxB.FindSwarm = delegate(byte[] h) { return Bencode.SameBytes(h, hash) ? swarmB : null; };
            using (BtLsd a = new BtLsd(ctxA, IPAddress.Loopback, 0, false))
            using (BtLsd b = new BtLsd(ctxB, IPAddress.Loopback, 0, false))
            using (Socket raw = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                a.Start();
                b.Start();
                IPEndPoint toA = new IPEndPoint(IPAddress.Loopback, a.LocalPort), toB = new IPEndPoint(IPAddress.Loopback, b.LocalPort);
                a.AddTarget(toB);
                a.AddTarget(toA);    // своё объявление возвращается себе же — как петлёй группы
                b.AddTarget(toA);

                a.Announce(swarmA);
                bool got = TrkWaitFor(delegate { return swarmB.PeerCount >= 1 && a.Received >= 1; }, 5000);
                T.Check("lsd: announce from one instance reaches the other's swarm with the announced port, origin Lsd",
                        got && swarmB.HasPeer("127.0.0.1:40001", BtPeerOrigin.Lsd), "peers " + swarmB.PeerCount + ", received " + b.Received);
                T.Check("lsd: own announce looped back is ignored by cookie", a.Received >= 1 && swarmA.PeerCount == 0 && a.Accepted == 0, "A peers " + swarmA.PeerCount);

                long sent = a.Sent;
                a.Announce(swarmA);
                a.Announce(privA);
                T.Check("lsd: second announce within 5 minutes suppressed; private torrent never announced", a.Sent == sent, sent + " → " + a.Sent);

                // Чужой пакет на A: частный торрент пира не получает, обычный — получает (путь приёма работает).
                raw.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                string priv = "BT-SEARCH * HTTP/1.1\r\nHost: 239.192.152.143:6771\r\nPort: 50000\r\nInfohash: " + Bencode.Hex(privHash) + "\r\ncookie: other\r\n\r\n\r\n";
                string pub = "BT-SEARCH * HTTP/1.1\r\nHost: 239.192.152.143:6771\r\nPort: 50001\r\nInfohash: " + Bencode.Hex(hash).ToUpperInvariant() + "\r\n\r\n\r\n";
                raw.SendTo(Encoding.ASCII.GetBytes("garbage\r\n\r\n"), toA);
                raw.SendTo(Encoding.ASCII.GetBytes(priv), toA);
                raw.SendTo(Encoding.ASCII.GetBytes(pub), toA);
                bool pubGot = TrkWaitFor(delegate { return swarmA.PeerCount >= 1; }, 5000);
                Thread.Sleep(100);
                T.Check("lsd: incoming announce for a private torrent ignored, garbage ignored, a foreign public announce accepted",
                        pubGot && privA.PeerCount == 0 && swarmA.PeerCount == 1 && swarmA.HasPeer("127.0.0.1:50001", BtPeerOrigin.Lsd), "priv " + privA.PeerCount + ", pub " + swarmA.PeerCount);
            }
            T.Check("lsd: only local source addresses accepted",
                    BtLsd.IsLocal(IPAddress.Parse("192.168.0.7")) && BtLsd.IsLocal(IPAddress.Parse("10.2.3.4")) && !BtLsd.IsLocal(IPAddress.Parse("8.8.8.8")) && !BtLsd.IsLocal(IPAddress.Parse("172.32.0.1")));
        }
    }
}
