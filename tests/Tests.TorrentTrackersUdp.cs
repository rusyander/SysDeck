// SysDeck — область «torrent», часть A: UDP-трекеры, метаданные, LSD.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class TorrentTests
    {
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
