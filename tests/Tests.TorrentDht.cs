﻿// Windows Process Cleaner — область «torrent», часть C: DHT, PEX, шифрование MSE, проброс порта.
// Сборка и запуск: tests\run-tests.bat torrent (или .agent/tmp/bt-lane-build.sh C test).
//
// Сеть — только петля 127.0.0.1: узлы DHT на настоящих BtUdp, MSE через настоящую пару TCP-сокетов, роутер — поддельный
// IGD на HttpListener и поддельный NAT-PMP на UDP-сокете. Начальные узлы DHT продукта подменены пустым списком: ни одного
// пакета в интернет или в локальную сеть. Сверка с независимой реализацией — libtorrent 2.x (WPC_LT_PYTHON, иначе SKIP).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunDht()
        {
            string[] savedBootstrap = BtDht.DefaultBootstrap;
            BtDht.DefaultBootstrap = new string[0];
            try
            {
                MseKnownAnswers();
                MseLoopback();
                PexCases();
                DhtCodecCases();
                DhtNetworkCases();
                PortMapCases();
                DhtOracleMse();
                DhtOracleDht();
            }
            finally
            {
                BtDht.DefaultBootstrap = savedBootstrap;
            }
        }

        private static bool DhtWaitFor(Func<bool> cond, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(25);
            }
            return cond();
        }

        private static byte[] DhtRandom(int n)
        {
            byte[] b = new byte[n];
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        // ================================================================== //
        //  MSE
        // ================================================================== //
        private static void MseKnownAnswers()
        {
            // Классический вектор RC4 (без отброса) и отброс 1024 байт — значения посчитаны отдельной реализацией на Python.
            byte[] plain = A("Plaintext");
            new BtRc4(A("Key"), 0).Apply(plain, 0, plain.Length);
            T.Eq("mse: RC4 known answer Key/Plaintext", "bbf316e8d940af0ad3", Bencode.Hex(plain));
            byte[] ks = new byte[16];
            new BtRc4(A("WPC-test-key"), 1024).Apply(ks, 0, 16);
            T.Eq("mse: RC4 drop-1024 keystream known answer", "b2b5f30338b4e685892ffb7aaa427a0b", Bencode.Hex(ks));
            byte[] chunked = new byte[16];
            BtRc4 c = new BtRc4(A("WPC-test-key"), 1024);
            c.Apply(chunked, 0, 5);
            c.Apply(chunked, 5, 11);
            T.Check("mse: RC4 keystream continues across Apply calls", Bencode.SameBytes(chunked, ks));

            byte[] xa = new byte[20], xb = new byte[20];
            for (int i = 0; i < 20; i++) { xa[i] = (byte)(i + 1); xb[i] = (byte)(i + 101); }
            byte[] ya = BtMse.DhPublic(xa);
            T.Eq("mse: DH public key 2^x mod P known answer (96 bytes, big-endian)",
                 "96e112dab29e8c5272accb9b17b26887ce54a144a4e3b697c7d159b7a817e556b0918db2b4c658e02a87f7e5fb14b18a553e084cbf3dad2d30f16596ccb982d406258c61b30c5c1dae2ddc60bdbd48d79896312aad63238c39e1a633821eb693",
                 Bencode.Hex(ya));
            byte[] s1 = BtMse.DhSecret(xa, BtMse.DhPublic(xb), 0);
            byte[] s2 = BtMse.DhSecret(xb, ya, 0);
            T.Check("mse: DH shared secret known answer and symmetric",
                    s1 != null && Bencode.Hex(BtMse.Sha1(s1)) == "8a347c13acc42e0841bfae5ef1b2eb4d0a484769" && Bencode.SameBytes(s1, s2));
            byte[] one = new byte[96];
            one[95] = 1;
            T.Check("mse: degenerate DH public key 1 refused", BtMse.DhSecret(xa, one, 0) == null);
        }

        private sealed class MseResult
        {
            public BtHandshakeState Out = BtHandshakeState.NeedMore, In = BtHandshakeState.NeedMore;
            public string OutError, InError;
            public bool OutRc4, InRc4;
            public byte[] InGot, OutGot;
            public byte[] InHash;
        }

        private static readonly byte[] MseHash = Encoding.ASCII.GetBytes("0123456789abcdefghij");

        private static byte[] MseLookup(byte[] req2)
        {
            return Bencode.SameBytes(req2, BtMse.Req2(MseHash)) ? MseHash : null;
        }

        // Одна сторона: рукопожатие, затем свои данные и чтение expect байт данных пира (Remaining + дальнейшее чтение).
        private static BtHandshakeState MsePump(Socket s, IBtStreamHandshake hs, bool begin, byte[] payload, int expect, out byte[] got)
        {
            got = null;
            List<byte[]> send = new List<byte[]>();
            BtHandshakeState st = BtHandshakeState.NeedMore;
            try
            {
                if (begin) hs.Begin(send);
                foreach (byte[] b in send) s.Send(b);
                send.Clear();
                byte[] buf = new byte[4096];
                while (st == BtHandshakeState.NeedMore)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    st = hs.Feed(buf, 0, n, send);
                    foreach (byte[] b in send) s.Send(b);
                    send.Clear();
                }
                if (st != BtHandshakeState.Done) return st;
                byte[] p = (byte[])payload.Clone();
                if (hs.Encryptor != null) hs.Encryptor.Apply(p, 0, p.Length);
                s.Send(p);
                MemoryStream ms = new MemoryStream();
                ms.Write(hs.Remaining, 0, hs.Remaining.Length);
                while (ms.Length < expect)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    if (hs.Decryptor != null) hs.Decryptor.Apply(buf, 0, n);
                    ms.Write(buf, 0, n);
                }
                got = ms.ToArray();
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            return st;
        }

        private static MseResult MsePair(BtEncryption outMode, BtEncryption inMode, int padAB, int padCD, byte[] ia)
        {
            MseResult r = new MseResult();
            byte[] clientData = A("CLIENT-DATA-after-the-handshake");
            byte[] serverData = A("SERVER-DATA-after-the-handshake!");
            byte[] iaBytes = ia ?? new byte[0];
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Socket client = null, server = null;
            Thread t = null;
            try
            {
                client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                client.ReceiveTimeout = 5000;
                client.Connect(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port));
                server = listener.AcceptSocket();
                server.ReceiveTimeout = 5000;
                Socket srv = server;
                t = new Thread(delegate()
                {
                    IBtStreamHandshake inc = BtMse.CreateIncoming(MseLookup, inMode, padAB, padCD);
                    byte[] got;
                    r.In = MsePump(srv, inc, false, serverData, iaBytes.Length + clientData.Length, out got);
                    r.InGot = got;
                    r.InError = inc.Error;
                    r.InRc4 = inc.Encryptor != null && inc.Decryptor != null;
                    r.InHash = inc.InfoHash;
                    if (r.In != BtHandshakeState.Done) try { srv.Close(); } catch { }
                });
                t.IsBackground = true;
                t.Start();
                IBtStreamHandshake outg = BtMse.CreateOutgoing(MseHash, outMode, ia, padAB, padCD);
                byte[] og;
                r.Out = MsePump(client, outg, true, clientData, serverData.Length, out og);
                r.OutGot = og;
                r.OutError = outg.Error;
                r.OutRc4 = outg.Encryptor != null && outg.Decryptor != null;
                if (r.Out != BtHandshakeState.Done) try { client.Close(); } catch { }
                t.Join(6000);
                byte[] wantIn = new byte[iaBytes.Length + clientData.Length];
                Buffer.BlockCopy(iaBytes, 0, wantIn, 0, iaBytes.Length);
                Buffer.BlockCopy(clientData, 0, wantIn, iaBytes.Length, clientData.Length);
                bool dataOk = Bencode.SameBytes(r.InGot, wantIn) && Bencode.SameBytes(r.OutGot, serverData);
                if (!dataOk) { r.InGot = null; r.OutGot = null; }
            }
            finally
            {
                if (client != null) client.Close();
                if (server != null) server.Close();
                listener.Stop();
                if (t != null) t.Join(2000);
            }
            return r;
        }

        private static void MseLoopback()
        {
            BtEncryption P = BtEncryption.Prefer, R = BtEncryption.Require, O = BtEncryption.Off;
            object[][] matrix =
            {
                new object[] { P, P, true }, new object[] { R, P, true }, new object[] { P, R, true }, new object[] { R, R, true },
                new object[] { P, O, false }, new object[] { O, P, false }
            };
            foreach (object[] m in matrix)
            {
                BtEncryption om = (BtEncryption)m[0], im = (BtEncryption)m[1];
                bool rc4 = (bool)m[2];
                MseResult r = MsePair(om, im, -1, 0, null);
                T.Check("mse: loopback TCP " + om + "->" + im + " completes " + (rc4 ? "with RC4 both ways" : "and selects plaintext") + ", data crosses intact",
                        r.Out == BtHandshakeState.Done && r.In == BtHandshakeState.Done && r.OutRc4 == rc4 && r.InRc4 == rc4 && r.InGot != null
                        && Bencode.SameBytes(r.InHash, MseHash),
                        r.Out + "/" + r.In + " " + r.OutError + " " + r.InError);
            }
            MseResult ro = MsePair(R, O, -1, 0, null);
            T.Check("mse: Require->Off — receiver refuses (no common method), nothing completes",
                    ro.In == BtHandshakeState.Failed && ro.Out != BtHandshakeState.Done && ro.InError != null, ro.Out + "/" + ro.In + " " + ro.InError);
            MseResult ro2 = MsePair(O, R, -1, 0, null);
            T.Check("mse: Off->Require — plaintext-only offer refused by a Require receiver",
                    ro2.In == BtHandshakeState.Failed && ro2.Out != BtHandshakeState.Done, ro2.Out + "/" + ro2.In + " " + ro2.InError);

            // Заполнители на пределе (PadA/PadB 512, PadC/PadD 512) и IA с рукопожатием BitTorrent внутри шага 3.
            byte[] ia = new byte[68];
            ia[0] = 19;
            Buffer.BlockCopy(A("BitTorrent protocol"), 0, ia, 1, 19);
            Buffer.BlockCopy(MseHash, 0, ia, 28, 20);
            MseResult rp = MsePair(P, P, 512, 512, ia);
            T.Check("mse: max pads (512) + IA — sync found at the window edge, IA comes out first in Remaining",
                    rp.Out == BtHandshakeState.Done && rp.In == BtHandshakeState.Done && rp.InGot != null && rp.InRc4, rp.OutError + " " + rp.InError);

            // Неизвестный торрент.
            List<byte[]> send = new List<byte[]>();
            IBtStreamHandshake a = BtMse.CreateOutgoing(MseHash, P, null, 0, 0);
            IBtStreamHandshake b = BtMse.CreateIncoming(delegate(byte[] h) { return null; }, P, 0, 0);
            a.Begin(send);
            BtHandshakeState sb = BtHandshakeState.NeedMore;
            for (int round = 0; round < 4 && sb == BtHandshakeState.NeedMore; round++)
            {
                List<byte[]> toB = new List<byte[]>(send);
                send.Clear();
                List<byte[]> toA = new List<byte[]>();
                foreach (byte[] x in toB) sb = b.Feed(x, 0, x.Length, toA);
                foreach (byte[] x in toA) a.Feed(x, 0, x.Length, send);
            }
            T.Check("mse: incoming handshake for an unknown info-hash fails", sb == BtHandshakeState.Failed && b.Error != null, sb + " " + b.Error);

            // Мусор вместо MSE: провал не позже окна синхронизации, байт за байтом.
            byte[] garbage = DhtRandom(4000);
            garbage[0] = 0x7F;
            IBtStreamHandshake gi = BtMse.Incoming(MseLookup, P);
            int failedAt = -1;
            for (int i = 0; i < garbage.Length; i++)
                if (gi.Feed(garbage, i, 1, send) == BtHandshakeState.Failed) { failedAt = i + 1; break; }
            T.Check("mse: garbage stream fails within the 628-byte sync window (incoming)", failedAt > 0 && failedAt <= 628, "failed at " + failedAt);
            IBtStreamHandshake go = BtMse.Outgoing(MseHash, P);
            send.Clear();
            go.Begin(send);
            failedAt = -1;
            for (int i = 0; i < garbage.Length; i++)
                if (go.Feed(garbage, i, 1, send) == BtHandshakeState.Failed) { failedAt = i + 1; break; }
            T.Check("mse: garbage reply fails within the 616-byte VC window (outgoing)", failedAt > 0 && failedAt <= 616, "failed at " + failedAt);
        }

        // ================================================================== //
        //  PEX
        // ================================================================== //
        private sealed class DhtFxSwarm : IBtSwarm
        {
            public byte[] Hash = new byte[20];
            public bool Private, Seed;
            public List<BtPeerInfo> Connected = new List<BtPeerInfo>();
            public readonly List<BtEndpoint> Added = new List<BtEndpoint>();
            public readonly List<BtPeerOrigin> Origins = new List<BtPeerOrigin>();
            public readonly List<string> Lines = new List<string>();
            public byte[] InfoHash { get { return Hash; } }
            public bool IsPrivate { get { return Private; } }
            public BtMeta Meta { get { return null; } }
            public long Uploaded { get { return 0; } }
            public long Downloaded { get { return 0; } }
            public long Left { get { return 0; } }
            public bool IsSeed { get { return Seed; } }
            public int NumWant { get { return 50; } }
            public bool Active { get { return true; } }
            public void AddPeers(IList<BtEndpoint> peers, BtPeerOrigin origin)
            {
                lock (Added)
                    foreach (BtEndpoint p in peers) { Added.Add(p); Origins.Add(origin); }
            }
            public bool Has(BtEndpoint ep) { lock (Added) return Added.Contains(ep); }
            public int Count { get { lock (Added) return Added.Count; } }
            public List<BtPeerInfo> ConnectedPeers() { return new List<BtPeerInfo>(Connected); }
            public bool OnMetadata(byte[] infoBytes) { return false; }
            public void Journal(string text) { lock (Lines) Lines.Add(text); }
        }

        // Пир в памяти: SendExtended отдаёт байты сообщения расширению на другом конце.
        private sealed class DhtFxLink : IBtPeerLink
        {
            public BtEndpoint Ep;
            public int Listen;
            public IBtExtension Remote;
            public DhtFxLink RemoteLink;
            public bool Pex = true;
            public readonly List<byte[]> Sent = new List<byte[]>();
            public BtEndpoint Endpoint { get { return Ep; } }
            public int ListenPort { get { return Listen; } }
            public bool Outgoing { get { return true; } }
            public bool Encrypted { get { return false; } }
            public bool IsSeed { get { return false; } }
            public string Client { get { return "fx"; } }
            public bool Supports(string extension) { return Pex && extension == "ut_pex"; }
            public void SendExtended(string extension, byte[] payload)
            {
                Sent.Add((byte[])payload.Clone());
                if (Remote == null) return;
                // Со смещением в буфере — как из разборщика сообщений провода.
                byte[] framed = new byte[payload.Length + 6];
                Buffer.BlockCopy(payload, 0, framed, 6, payload.Length);
                Remote.OnMessage(RemoteLink, framed, 6, payload.Length);
            }
            public void Close(string reason) { }
        }

        private static BtPeerInfo PexPeer(string ep, bool seed, bool enc, bool outgoing)
        {
            BtPeerInfo p = new BtPeerInfo();
            p.Endpoint = BtEndpoint.TryParse(ep);
            p.Seed = seed;
            p.Encrypted = enc;
            p.Outgoing = outgoing;
            return p;
        }

        private static void PexCases()
        {
            BtContext ctx = new BtContext();
            DhtFxSwarm priv = new DhtFxSwarm();
            priv.Private = true;
            DhtFxSwarm pub = new DhtFxSwarm();
            IBtExtension made = BtPexExt.Create(pub, ctx);
            T.Check("pex: factory returns null for a private swarm (BEP 27), ut_pex for a public one",
                    BtPexExt.Create(priv, ctx) == null && made != null && made.Name == "ut_pex");

            DhtFxSwarm sa = new DhtFxSwarm(), sb = new DhtFxSwarm();
            sa.Connected.Add(PexPeer("10.0.0.1:6881", true, true, false));
            sa.Connected.Add(PexPeer("10.0.0.2:51413", false, false, true));
            sa.Connected.Add(PexPeer("[2001:db8::1]:6881", false, false, false));
            sa.Connected.Add(PexPeer("127.0.0.1:7000", false, false, true));   // сам получатель — ему о нём не сообщают
            BtPexExt ea = new BtPexExt(sa, ctx), eb = new BtPexExt(sb, ctx);
            DhtFxLink ab = new DhtFxLink(), ba = new DhtFxLink();
            ab.Ep = BtEndpoint.TryParse("127.0.0.1:7000"); ab.Listen = 7000; ab.Remote = eb; ab.RemoteLink = ba;
            ba.Ep = BtEndpoint.TryParse("127.0.0.1:50123"); ba.Listen = 7001; ba.Remote = ea; ba.RemoteLink = ab;
            BVal hs = BVal.NewDict();
            ea.OnHandshake(ab, hs);
            eb.OnHandshake(ba, hs);
            DateTime t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            eb.Tick(t0);
            ea.Tick(t0);
            T.Check("pex: first message delivers v4 + v6 peers to the other swarm as Pex, never the recipient itself",
                    sb.Count == 3 && sb.Has(BtEndpoint.TryParse("10.0.0.1:6881")) && sb.Has(BtEndpoint.TryParse("10.0.0.2:51413"))
                    && sb.Has(BtEndpoint.TryParse("[2001:db8::1]:6881")) && !sb.Has(BtEndpoint.TryParse("127.0.0.1:7000"))
                    && sb.Origins.TrueForAll(delegate(BtPeerOrigin o) { return o == BtPeerOrigin.Pex; }), "added " + sb.Count);
            string err;
            BVal m1 = ab.Sent.Count == 1 ? Bencode.Decode(ab.Sent[0], out err) : null;
            bool flagsOk = false;
            if (m1 != null)
            {
                byte[] added = m1.GetBytes("added"), f = m1.GetBytes("added.f");
                flagsOk = added != null && f != null && added.Length == 12 && f.Length == 2 && m1.GetBytes("added6").Length == 18;
                for (int i = 0; flagsOk && i < 2; i++)
                    flagsOk = added[i * 6 + 3] == 1 ? f[i] == 0x03 : f[i] == 0x10;   // 10.0.0.1 — сид с шифрованием, 10.0.0.2 — достижим
            }
            T.Check("pex: wire payload — compact added (6 bytes each), added.f flags seed|encryption / reachable, added6 18 bytes", flagsOk);

            sa.Connected.RemoveAt(1);
            sa.Connected.Add(PexPeer("10.0.0.3:6882", false, false, false));
            ea.Tick(t0.AddSeconds(30));
            T.Eq("pex: no second message inside 60 s", 1, ab.Sent.Count);
            eb.Tick(t0.AddSeconds(61));
            ea.Tick(t0.AddSeconds(61));
            BVal m2 = ab.Sent.Count == 2 ? Bencode.Decode(ab.Sent[1], out err) : null;
            T.Check("pex: after 60 s only the difference — added 10.0.0.3, dropped 10.0.0.2",
                    m2 != null && Bencode.Hex(m2.GetBytes("added")) == Bencode.Hex(BtEndpoint.TryParse("10.0.0.3:6882").ToCompact())
                    && Bencode.Hex(m2.GetBytes("dropped")) == Bencode.Hex(BtEndpoint.TryParse("10.0.0.2:51413").ToCompact())
                    && sb.Has(BtEndpoint.TryParse("10.0.0.3:6882")), m2 == null ? "sent " + ab.Sent.Count : "");
            int before = sb.Count;
            byte[] replay = Bencode.Encode(BVal.NewDict().Set("added", BVal.Bytes(BtEndpoint.TryParse("10.0.0.99:99").ToCompact())));
            eb.OnMessage(ba, replay, 0, replay.Length);
            T.Eq("pex: a message sooner than 45 s after the previous one is ignored", before, sb.Count);

            // Пределы: 120 соединённых — в сообщении 50; входящее с 200 адресами — в рой 50.
            DhtFxSwarm big = new DhtFxSwarm();
            for (int i = 0; i < 120; i++) big.Connected.Add(PexPeer("10.1." + (i / 250) + "." + (i % 250 + 1) + ":6881", false, false, false));
            BtPexExt ebig = new BtPexExt(big, ctx);
            DhtFxLink lb = new DhtFxLink();
            lb.Ep = BtEndpoint.TryParse("127.0.0.1:9000");
            ebig.OnHandshake(lb, hs);
            ebig.Tick(t0);
            BVal mb = lb.Sent.Count == 1 ? Bencode.Decode(lb.Sent[0], out err) : null;
            T.Check("pex: at most 50 added per message", mb != null && mb.GetBytes("added").Length == 50 * 6, lb.Sent.Count.ToString());
            byte[] many = new byte[200 * 6];
            for (int i = 0; i < 200; i++) { many[i * 6] = 10; many[i * 6 + 1] = 2; many[i * 6 + 2] = (byte)(i / 200); many[i * 6 + 3] = (byte)(i + 1); many[i * 6 + 5] = 80; }
            DhtFxSwarm sink = new DhtFxSwarm();
            BtPexExt esink = new BtPexExt(sink, ctx);
            byte[] manyMsg = Bencode.Encode(BVal.NewDict().Set("added", BVal.Bytes(many)).Set("added.f", BVal.Bytes(new byte[200])));
            esink.OnMessage(lb, manyMsg, 0, manyMsg.Length);
            T.Eq("pex: at most 50 received peers reach the swarm per message", 50, sink.Count);

            DhtFxSwarm seeder = new DhtFxSwarm();
            seeder.Seed = true;
            BtPexExt eseed = new BtPexExt(seeder, ctx);
            byte[] two = new byte[12];
            Buffer.BlockCopy(BtEndpoint.TryParse("10.3.0.1:1000").ToCompact(), 0, two, 0, 6);
            Buffer.BlockCopy(BtEndpoint.TryParse("10.3.0.2:1000").ToCompact(), 0, two, 6, 6);
            byte[] seedMsg = Bencode.Encode(BVal.NewDict().Set("added", BVal.Bytes(two)).Set("added.f", BVal.Bytes(new byte[] { 0x02, 0x00 })));
            eseed.OnMessage(lb, seedMsg, 0, seedMsg.Length);
            T.Check("pex: a seeding swarm skips peers flagged as seeds", seeder.Count == 1 && seeder.Has(BtEndpoint.TryParse("10.3.0.2:1000")));

            // Созданный в обход фабрики экземпляр частного роя всё равно молчит.
            DhtFxSwarm pr = new DhtFxSwarm();
            pr.Private = true;
            pr.Connected.Add(PexPeer("10.0.0.1:6881", false, false, false));
            BtPexExt epr = new BtPexExt(pr, ctx);
            DhtFxLink lp = new DhtFxLink();
            lp.Ep = BtEndpoint.TryParse("127.0.0.1:9001");
            epr.OnHandshake(lp, hs);
            epr.Tick(t0);
            epr.OnMessage(lp, manyMsg, 0, manyMsg.Length);
            T.Check("pex: private swarm — nothing sent, nothing accepted", lp.Sent.Count == 0 && pr.Count == 0);

            DhtFxSwarm junk = new DhtFxSwarm();
            BtPexExt ejunk = new BtPexExt(junk, ctx);
            bool threw = false;
            try
            {
                foreach (string bad in new[] { "d5:addedi5ee", "l1:ae", "d5:added7:1234567e", "d5:added", "xyz" })
                {
                    byte[] bb = A(bad);
                    ejunk.OnMessage(new DhtFxLink(), bb, 0, bb.Length);
                }
            }
            catch (Exception) { threw = true; }
            T.Check("pex: hostile payloads — no exception, only whole 6-byte entries", !threw && junk.Count == 1 && junk.Added[0].Port == 13622, junk.Count.ToString());
        }

        // ================================================================== //
        //  DHT
        // ================================================================== //
        private sealed class DhtFxNode : IDisposable
        {
            public readonly BtUdp Udp;
            public readonly BtContext Ctx;
            public BtDht Dht;

            public DhtFxNode(string dir, params string[] bootstrap)
            {
                Udp = new BtUdp(IPAddress.Loopback, 0);
                Ctx = new BtContext();
                Ctx.Udp = Udp;
                Ctx.Port = Udp.Port;
                Ctx.TorrentsDir = dir;
                Ctx.PeerId = BtContext.NewPeerId();
                Dht = new BtDht(Ctx);
                Dht.Bootstrap.Clear();
                Dht.Bootstrap.AddRange(bootstrap);
                Dht.QueryTimeoutMs = 700;
                Dht.RateLimitPerSecond = 500;
                Ctx.Dht = Dht;
            }

            public BtEndpoint Ep { get { return new BtEndpoint(IPAddress.Loopback, Udp.Port); } }
            public string Addr { get { return "127.0.0.1:" + Udp.Port; } }

            public void Dispose()
            {
                if (Dht != null) Dht.Dispose();
                Udp.Dispose();
            }
        }

        // Сырой клиент KRPC: свой сокет, ответы складываются по transaction id.
        private sealed class DhtFxClient : IDisposable
        {
            public readonly BtUdp Udp = new BtUdp(IPAddress.Loopback, 0);
            private readonly List<BVal> _got = new List<BVal>();
            public readonly byte[] Id = DhtRandom(20);
            private int _tid;

            public DhtFxClient()
            {
                Udp.AddHandler(new FxUdpHandler(delegate(BtEndpoint from, byte[] d, int n)
                {
                    string err;
                    BVal v = Bencode.Decode(d, out err);
                    if (v != null) lock (_got) _got.Add(v);
                    return true;
                }));
            }

            public int Count { get { lock (_got) return _got.Count; } }

            public bool Any(Predicate<BVal> p) { lock (_got) return _got.Exists(p); }

            public void Fire(BtEndpoint to, string q, BVal args, byte[] t)
            {
                BVal m = BVal.NewDict().Set("t", BVal.Bytes(t)).Set("y", BVal.Str("q")).Set("q", BVal.Str(q));
                if (args != null) m.Set("a", args);
                byte[] data = Bencode.Encode(m);
                Udp.Send(to, data, data.Length);
            }

            public BVal Ask(BtEndpoint to, string q, BVal args)
            {
                _tid++;
                byte[] t = { (byte)'x', (byte)_tid };
                Fire(to, q, args, t);
                BVal found = null;
                DhtWaitFor(delegate
                {
                    lock (_got)
                        foreach (BVal v in _got)
                            if (Bencode.SameBytes(v.GetBytes("t"), t)) { found = v; return true; }
                    return false;
                }, 2000);
                return found;
            }

            public BVal Args() { return BVal.NewDict().Set("id", BVal.Bytes(Id)); }

            public void Dispose() { Udp.Dispose(); }
        }

        private static byte[] DhtIdWithPrefix(byte[] own, int cpl, int salt)
        {
            byte[] id = DhtRandom(20);
            id[19] ^= (byte)salt;
            for (int bit = 0; bit < cpl; bit++)
            {
                int mask = 0x80 >> (bit % 8);
                id[bit / 8] = (byte)((id[bit / 8] & ~mask) | (own[bit / 8] & mask));
            }
            int m2 = 0x80 >> (cpl % 8);
            id[cpl / 8] = (byte)((id[cpl / 8] & ~m2) | (~own[cpl / 8] & m2));
            return id;
        }

        private static void DhtCodecCases()
        {
            BtContext ctx = new BtContext();
            BtDht d = new BtDht(ctx);
            try
            {
                byte[] bep15 = { 0, 0, 0, 0, 0x12, 0x34, 0x56, 0x78, 1, 2, 3, 4, 5, 6, 7, 8 };
                byte[] noY = A("d1:ai1ee");
                byte[] broken = A("d1:y1:qe-trailing");
                byte[] krpc = A("d1:t2:aa1:y1:re");
                T.Check("dht: HandleDatagram — BEP 15 reply, dict without y, trailing bytes are not ours; KRPC is",
                        !d.HandleDatagram(new BtEndpoint(IPAddress.Loopback, 1), bep15, bep15.Length)
                        && !d.HandleDatagram(new BtEndpoint(IPAddress.Loopback, 1), noY, noY.Length)
                        && !d.HandleDatagram(new BtEndpoint(IPAddress.Loopback, 1), broken, broken.Length)
                        && d.HandleDatagram(new BtEndpoint(IPAddress.Loopback, 1), krpc, krpc.Length));

                // Таблица: 12 далёких (cpl 0) — в таблице 8; 12 близких (cpl 5) — корзина со своим id делится, ещё 8.
                byte[] own = d.NodeId;
                int far = 0, near = 0;
                for (int i = 0; i < 12; i++)
                    if (d.InsertNodeForTest(DhtIdWithPrefix(own, 0, i), new BtEndpoint(IPAddress.Parse("10.9.0." + (i + 1)), 6881))) far++;
                for (int i = 0; i < 12; i++)
                    if (d.InsertNodeForTest(DhtIdWithPrefix(own, 5, i), new BtEndpoint(IPAddress.Parse("10.9.1." + (i + 1)), 6881))) near++;
                bool lateFar = d.InsertNodeForTest(DhtIdWithPrefix(own, 0, 99), new BtEndpoint(IPAddress.Parse("10.9.2.1"), 6881));
                T.Check("dht: routing table K=8 — far bucket never splits, own-id range splits",
                        far == 8 && near == 8 && !lateFar && d.NodeCount == 16 && d.BucketCount >= 6,
                        "far " + far + " near " + near + " late " + lateFar + " nodes " + d.NodeCount + " buckets " + d.BucketCount);
            }
            finally
            {
                d.Dispose();
            }
        }

        private static void DhtNetworkCases()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-dht");
            List<DhtFxNode> nodes = new List<DhtFxNode>();
            DhtFxClient client = null;
            try
            {
                DhtFxNode n0 = new DhtFxNode(Fx.MakeDir(root, "n0"));
                nodes.Add(n0);
                for (int i = 1; i < 5; i++) nodes.Add(new DhtFxNode(Fx.MakeDir(root, "n" + i), n0.Addr));
                foreach (DhtFxNode n in nodes) n.Dht.Start();
                bool meshed = DhtWaitFor(delegate { return n0.Dht.NodeCount >= 4 && nodes.TrueForAll(delegate(DhtFxNode n) { return n.Dht.NodeCount >= 1; }); }, 8000);
                string counts = string.Join(",", nodes.ConvertAll(delegate(DhtFxNode n) { return n.Dht.NodeCount.ToString(); }).ToArray());
                T.Check("dht: 5 nodes on real BtUdp bootstrap from one and fill their tables", meshed, counts);

                byte[] hash = DhtRandom(20);
                DhtFxSwarm announcer = new DhtFxSwarm();
                announcer.Hash = hash;
                nodes[2].Ctx.InboundOpen = true;
                nodes[2].Dht.GetPeers(announcer, true);
                bool stored = DhtWaitFor(delegate { int s = 0; foreach (DhtFxNode n in nodes) s += n.Dht.StoredPeers(hash); return s > 0; }, 5000);
                T.Check("dht: announce_peer (inbound open) stores the peer on the closest nodes", stored);

                DhtFxSwarm seeker = new DhtFxSwarm();
                seeker.Hash = hash;
                BtEndpoint expected = new BtEndpoint(IPAddress.Loopback, nodes[2].Ctx.Port);
                bool found = DhtWaitFor(delegate
                {
                    if (seeker.Has(expected)) return true;
                    if (nodes[4].Dht.LookupCount == 0) nodes[4].Dht.GetPeers(seeker, false);
                    return false;
                }, 6000);
                T.Check("dht: get_peers on another node finds the announced peer -> swarm.AddPeers(…, Dht)",
                        found && seeker.Origins.TrueForAll(delegate(BtPeerOrigin o) { return o == BtPeerOrigin.Dht; }));

                byte[] closedHash = DhtRandom(20);
                DhtFxSwarm closed = new DhtFxSwarm();
                closed.Hash = closedHash;
                nodes[3].Ctx.InboundOpen = false;
                nodes[3].Dht.GetPeers(closed, true);
                DhtWaitFor(delegate { return nodes[3].Dht.LookupCount == 0; }, 5000);
                Thread.Sleep(300);
                int closedStored = 0;
                foreach (DhtFxNode n in nodes) closedStored += n.Dht.StoredPeers(closedHash);
                T.Check("dht: inbound closed — lookup runs but nothing is announced", nodes[3].Dht.LookupCount == 0 && closedStored == 0, "stored " + closedStored);

                // Частный рой: узел, чья таблица знает только наш сырой сокет. Открытый рой — запрос приходит (контроль канала),
                // частный — ни одного пакета с его info-hash.
                using (DhtFxClient watcher = new DhtFxClient())
                using (DhtFxNode lone = new DhtFxNode(Fx.MakeDir(root, "lone")))
                {
                    lone.Dht.Start();
                    DhtWaitFor(delegate { return lone.Dht.Ready; }, 3000);
                    lone.Dht.InsertNodeForTest(DhtRandom(20), watcher.Udp.Port > 0 ? new BtEndpoint(IPAddress.Loopback, watcher.Udp.Port) : null);
                    lone.Ctx.InboundOpen = true;
                    DhtFxSwarm open = new DhtFxSwarm();
                    open.Hash = DhtRandom(20);
                    DhtFxSwarm priv = new DhtFxSwarm();
                    priv.Private = true;
                    priv.Hash = DhtRandom(20);
                    lone.Dht.GetPeers(priv, true);
                    lone.Dht.GetPeers(open, true);
                    bool control = DhtWaitFor(delegate { return watcher.Any(delegate(BVal v) { return v.GetStr("q") == "get_peers" && Bencode.SameBytes(v.Get("a", BKind.Dict).GetBytes("info_hash"), open.Hash); }); }, 2000);
                    Thread.Sleep(200);
                    bool leaked = watcher.Any(delegate(BVal v) { BVal a = v.Get("a", BKind.Dict); return a != null && Bencode.SameBytes(a.GetBytes("info_hash"), priv.Hash); });
                    T.Check("dht: private swarm — no get_peers/announce leaves the node (public swarm control query arrives)", control && !leaked && priv.Count == 0,
                            "control " + control + " leaked " + leaked);
                }

                // Токены: поддельный отклоняется, настоящий принимается, после одной смены секрета ещё годен, после двух — нет.
                client = new DhtFxClient();
                byte[] th = DhtRandom(20);
                BVal gp = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                BVal gr = gp == null ? null : gp.Get("r", BKind.Dict);
                byte[] token = gr == null ? null : gr.GetBytes("token");
                T.Check("dht: get_peers reply carries a token and compact nodes", token != null && gr.GetBytes("nodes") != null && gr.GetBytes("nodes").Length % 26 == 0);
                if (token != null)
                {
                    byte[] forged = (byte[])token.Clone();
                    forged[0] ^= 0x5A;
                    BVal bad = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7001)).Set("token", BVal.Bytes(forged)));
                    BVal badE = bad == null ? null : bad.Get("e", BKind.List);
                    T.Check("dht: forged token refused (error 203), nothing stored",
                            badE != null && badE.L.Count >= 1 && badE.L[0].I == 203 && n0.Dht.StoredPeers(th) == 0, bad == null ? "no reply" : bad.GetStr("y"));
                    BVal good = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7001)).Set("token", BVal.Bytes(token)));
                    BVal again = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    BVal values = again == null || again.Get("r", BKind.Dict) == null ? null : again.Get("r", BKind.Dict).Get("values", BKind.List);
                    T.Check("dht: valid token accepted — the peer is served back in values",
                            good != null && good.GetStr("y") == "r" && values != null && values.L.Count == 1
                            && Bencode.Hex(values.L[0].B) == Bencode.Hex(new BtEndpoint(IPAddress.Loopback, 7001).ToCompact()));
                    n0.Dht.RotateSecret();
                    BVal prev = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7002)).Set("token", BVal.Bytes(token)));
                    n0.Dht.RotateSecret();
                    BVal stale = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7003)).Set("token", BVal.Bytes(token)));
                    T.Check("dht: token from the previous secret still accepted, two rotations old refused",
                            prev != null && prev.GetStr("y") == "r" && stale != null && stale.GetStr("y") == "e" && n0.Dht.StoredPeers(th) == 2);
                    BVal fresh = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    byte[] freshToken = fresh == null || fresh.Get("r", BKind.Dict) == null ? new byte[0] : fresh.Get("r", BKind.Dict).GetBytes("token");
                    BVal implied = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(1))
                        .Set("implied_port", BVal.Int(1)).Set("token", BVal.Bytes(freshToken)));
                    BVal withImplied = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    BVal vi = withImplied == null || withImplied.Get("r", BKind.Dict) == null ? null : withImplied.Get("r", BKind.Dict).Get("values", BKind.List);
                    string wantImplied = Bencode.Hex(new BtEndpoint(IPAddress.Loopback, client.Udp.Port).ToCompact());
                    T.Check("dht: implied_port stores the UDP source port, not the port argument",
                            implied != null && implied.GetStr("y") == "r" && vi != null && vi.L.Exists(delegate(BVal v) { return Bencode.Hex(v.B) == wantImplied; }));
                }
                BVal noId = client.Ask(n0.Ep, "ping", BVal.NewDict());
                BVal unknown = client.Ask(n0.Ep, "vote", client.Args());
                T.Check("dht: malformed query -> 203, unknown method -> 204",
                        noId != null && noId.Get("e", BKind.List) != null && noId.Get("e", BKind.List).L[0].I == 203
                        && unknown != null && unknown.Get("e", BKind.List) != null && unknown.Get("e", BKind.List).L[0].I == 204);

                n0.Dht.RateLimitPerSecond = 5;
                Thread.Sleep(1100);
                int base0 = client.Count;
                for (int i = 0; i < 40; i++) client.Fire(n0.Ep, "ping", client.Args(), new byte[] { (byte)'r', (byte)i });
                Thread.Sleep(400);
                int replies = client.Count - base0;
                n0.Dht.RateLimitPerSecond = 500;
                T.Check("dht: per-IP rate limit — a burst of 40 queries gets at most a few replies", replies >= 1 && replies <= 10, replies + " replies");

                // dht.json: id и живые узлы переживают перезапуск.
                string dir1 = nodes[1].Ctx.TorrentsDir;
                byte[] id1 = nodes[1].Dht.NodeId;
                nodes[1].Dht.Dispose();
                JVal saved = DlPaths.ReadJson(Path.Combine(dir1, "dht.json"));
                JVal savedNodes = saved == null ? null : saved.Get("nodes");
                T.Check("dht: dht.json written atomically on Dispose with id and good nodes",
                        saved != null && saved.GetStr("id") == Bencode.Hex(id1) && savedNodes != null && savedNodes.V.Count >= 1
                        && !File.Exists(Path.Combine(dir1, "dht.json.tmp")));
                nodes[1].Dht = new BtDht(nodes[1].Ctx);
                nodes[1].Dht.Bootstrap.Clear();
                nodes[1].Dht.QueryTimeoutMs = 700;
                nodes[1].Ctx.Dht = nodes[1].Dht;
                nodes[1].Dht.Start();
                bool back = DhtWaitFor(delegate { return nodes[1].Dht.Ready && nodes[1].Dht.NodeCount >= 1; }, 4000);
                T.Check("dht: restart from dht.json — same node id, saved nodes re-joined without bootstrap",
                        back && Bencode.SameBytes(nodes[1].Dht.NodeId, id1), "nodes " + nodes[1].Dht.NodeCount);
                T.Check("dht: status text reports the table", nodes[1].Dht.Status.Contains("DHT"), nodes[1].Dht.Status);
            }
            finally
            {
                if (client != null) client.Dispose();
                foreach (DhtFxNode n in nodes) n.Dispose();
            }
        }

        // ================================================================== //
        //  Проброс порта
        // ================================================================== //
        private sealed class PortMapFxIgd : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            public readonly int Port;
            public readonly List<string> Calls = new List<string>();     // «Action proto port client desc lease»
            public int Gets;
            public int FailAddCode;
            public int DescriptionBytes;

            public PortMapFxIgd()
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                _listener.Prefixes.Add("http://localhost:" + Port + "/");
                _listener.Start();
                Thread t = new Thread(Accept);
                t.IsBackground = true;
                t.Start();
            }

            public string DescUrl { get { return "http://127.0.0.1:" + Port + "/desc.xml"; } }

            public List<string> Snapshot() { lock (Calls) return new List<string>(Calls); }

            private void Accept()
            {
                while (true)
                {
                    HttpListenerContext c;
                    try { c = _listener.GetContext(); }
                    catch { return; }
                    HttpListenerContext cc = c;
                    ThreadPool.QueueUserWorkItem(delegate { Handle(cc); });
                }
            }

            private static string Tag(string xml, string name)
            {
                int a = xml.IndexOf("<" + name + ">", StringComparison.Ordinal);
                int b = xml.IndexOf("</" + name + ">", StringComparison.Ordinal);
                return a < 0 || b < a ? "" : xml.Substring(a + name.Length + 2, b - a - name.Length - 2);
            }

            private void Handle(HttpListenerContext c)
            {
                string body;
                int status = 200;
                try
                {
                    string path = c.Request.Url.AbsolutePath;
                    if (c.Request.HttpMethod == "GET" && path == "/desc.xml")
                    {
                        lock (Calls) Gets++;
                        body = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>" +
                               "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType><deviceList><device>" +
                               "<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>" +
                               "<controlURL>/ctl/IPConn</controlURL></service></serviceList></device></deviceList></device></deviceList></device></root>";
                        if (DescriptionBytes > 0) body = body + "<!--" + new string('x', DescriptionBytes) + "-->";
                    }
                    else if (c.Request.HttpMethod == "POST" && path == "/ctl/IPConn")
                    {
                        string action = c.Request.Headers["SOAPAction"] ?? "";
                        string xml;
                        using (StreamReader r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) xml = r.ReadToEnd();
                        string name = action.Trim('"');
                        name = name.Substring(name.IndexOf('#') + 1);
                        bool typeOk = action.Contains("urn:schemas-upnp-org:service:WANIPConnection:1#");
                        lock (Calls)
                            Calls.Add(name + " " + Tag(xml, "NewProtocol") + " " + Tag(xml, "NewExternalPort") + " " + Tag(xml, "NewInternalClient") + " " +
                                      Tag(xml, "NewPortMappingDescription").Replace(' ', '_') + " " + Tag(xml, "NewLeaseDuration") + (typeOk ? "" : " BADTYPE"));
                        if (name == "AddPortMapping" && FailAddCode > 0)
                        {
                            status = 500;
                            body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>" +
                                   "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + FailAddCode +
                                   "</errorCode><errorDescription>Conflict</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
                        }
                        else
                            body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:" + name + "Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                                   (name == "GetExternalIPAddress" ? "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>" : "") +
                                   "</u:" + name + "Response></s:Body></s:Envelope>";
                    }
                    else
                    {
                        status = 404;
                        body = "";
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    c.Response.StatusCode = status;
                    c.Response.ContentType = "text/xml";
                    c.Response.ContentLength64 = bytes.Length;
                    c.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    c.Response.Close();
                }
                catch
                {
                    try { c.Response.Abort(); } catch { }
                }
            }

            public void Dispose()
            {
                try { _listener.Stop(); _listener.Close(); } catch { }
            }
        }

        // UDP-ответчик на петле: SSDP (LOCATION) или NAT-PMP (RFC 6886).
        private sealed class PortMapFxUdp : IDisposable
        {
            private readonly Socket _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly Thread _t;
            private volatile bool _stop;
            public readonly List<string> Seen = new List<string>();
            public readonly string Location;      // null — NAT-PMP

            public PortMapFxUdp(string ssdpLocation)
            {
                Location = ssdpLocation;
                _s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _t = new Thread(Loop);
                _t.IsBackground = true;
                _t.Start();
            }

            public BtEndpoint Ep { get { return new BtEndpoint(IPAddress.Loopback, ((IPEndPoint)_s.LocalEndPoint).Port); } }
            public List<string> Snapshot() { lock (Seen) return new List<string>(Seen); }

            private void Loop()
            {
                byte[] buf = new byte[2048];
                while (!_stop)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try
                    {
                        if (!_s.Poll(100000, SelectMode.SelectRead)) continue;
                        n = _s.ReceiveFrom(buf, ref from);
                    }
                    catch { if (_stop) return; continue; }
                    byte[] reply;
                    if (Location != null)
                    {
                        lock (Seen) Seen.Add(Encoding.ASCII.GetString(buf, 0, n));
                        reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nLOCATION: " + Location + "\r\n\r\n");
                    }
                    else if (n == 2 && buf[0] == 0 && buf[1] == 0)
                    {
                        lock (Seen) Seen.Add("ext");
                        reply = new byte[] { 0, 128, 0, 0, 0, 0, 0, 9, 198, 51, 100, 9 };
                    }
                    else if (n == 12 && buf[0] == 0 && (buf[1] == 1 || buf[1] == 2))
                    {
                        int life = (buf[8] << 24) | (buf[9] << 16) | (buf[10] << 8) | buf[11];
                        lock (Seen) Seen.Add((buf[1] == 2 ? "TCP " : "UDP ") + ((buf[4] << 8) | buf[5]) + " " + life);
                        reply = new byte[16];
                        reply[1] = (byte)(128 + buf[1]);
                        reply[7] = 9;
                        Buffer.BlockCopy(buf, 4, reply, 8, 4);
                        Buffer.BlockCopy(buf, 8, reply, 12, 4);
                    }
                    else continue;
                    try { _s.SendTo(reply, from); } catch { }
                }
            }

            public void Dispose()
            {
                _stop = true;
                _t.Join(1000);
                _s.Close();
            }
        }

        private static int PortMapClosedPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static void PortMapCases()
        {
            using (PortMapFxIgd igd = new PortMapFxIgd())
            {
                BtPortMapper m = new BtPortMapper(null, igd.DescUrl, null);
                m.LeaseSeconds = 2;
                try
                {
                    m.Start(40111, 40112);
                    bool mapped = DhtWaitFor(delegate { return m.Mapped; }, 5000);
                    List<string> calls = igd.Snapshot();
                    T.Check("portmap: UPnP IGD — AddPortMapping TCP and UDP with our description, internal client and lease",
                            mapped && calls.Contains("AddPortMapping TCP 40111 127.0.0.1 Windows_Process_Cleaner 2")
                            && calls.Contains("AddPortMapping UDP 40112 127.0.0.1 Windows_Process_Cleaner 2"), string.Join(" | ", calls.ToArray()));
                    T.Check("portmap: GetExternalIPAddress -> ExternalAddress, status names the port",
                            IPAddress.Parse("203.0.113.7").Equals(m.ExternalAddress) && m.Status.Contains("40111"), m.Status);
                    bool renewed = DhtWaitFor(delegate { return igd.Snapshot().FindAll(delegate(string s) { return s.StartsWith("AddPortMapping TCP", StringComparison.Ordinal); }).Count >= 2; }, 4000);
                    T.Check("portmap: lease renewed at half its duration", renewed);
                }
                finally
                {
                    m.Dispose();
                }
                List<string> after = igd.Snapshot();
                T.Check("portmap: mapping deleted on Dispose (TCP and UDP)",
                        after.Contains("DeletePortMapping TCP 40111   ") && after.Contains("DeletePortMapping UDP 40112   ") && !m.Mapped,
                        string.Join(" | ", after.ToArray()));

                // SSDP на петлевой адрес: M-SEARCH -> LOCATION -> тот же IGD.
                using (PortMapFxUdp ssdp = new PortMapFxUdp(igd.DescUrl))
                {
                    BtPortMapper ms = new BtPortMapper(ssdp.Ep, null, null);
                    ms.SsdpWaitMs = 1500;
                    try
                    {
                        ms.Start(40121, 40121);
                        bool ok = DhtWaitFor(delegate { return ms.Mapped; }, 6000);
                        List<string> seen = ssdp.Snapshot();
                        T.Check("portmap: SSDP M-SEARCH (ssdp:discover, IGD) -> LOCATION -> mapped",
                                ok && seen.Count >= 1 && seen[0].StartsWith("M-SEARCH * HTTP/1.1", StringComparison.Ordinal) && seen[0].Contains("\"ssdp:discover\"")
                                && seen.Exists(delegate(string s) { return s.Contains("InternetGatewayDevice"); }), ok + " " + seen.Count);
                    }
                    finally
                    {
                        ms.Dispose();
                    }
                }
                int getsBefore = igd.Gets;
                using (PortMapFxUdp foreign = new PortMapFxUdp("http://localhost:" + igd.Port + "/desc.xml"))
                {
                    BtPortMapper mf = new BtPortMapper(foreign.Ep, null, null);
                    mf.SsdpWaitMs = 600;
                    try
                    {
                        mf.Start(40131, 40131);
                        DhtWaitFor(delegate { return foreign.Snapshot().Count > 0; }, 3000);
                        Thread.Sleep(1200);
                        T.Check("portmap: SSDP LOCATION on a host other than the responder is not fetched", !mf.Mapped && igd.Gets == getsBefore, igd.Gets + " gets");
                    }
                    finally
                    {
                        mf.Dispose();
                    }
                }

                igd.FailAddCode = 718;
                BtPortMapper mc = new BtPortMapper(null, igd.DescUrl, null);
                try
                {
                    mc.Start(40141, 40141);
                    bool reported = DhtWaitFor(delegate { return mc.Status.Contains("40141"); }, 5000);
                    T.Check("portmap: SOAP error 718 (conflict) — not mapped, status explains", reported && !mc.Mapped, mc.Status);
                }
                finally
                {
                    mc.Dispose();
                    igd.FailAddCode = 0;
                }

                igd.DescriptionBytes = 300 * 1024;
                int soapBefore = igd.Snapshot().Count;
                getsBefore = igd.Gets;
                BtPortMapper mbig = new BtPortMapper(null, igd.DescUrl, null);
                try
                {
                    mbig.Start(40151, 40151);
                    // Ждём именно отказа после скачивания описания, а не начальный статус.
                    DhtWaitFor(delegate { return igd.Gets > getsBefore && mbig.Status.StartsWith("UPnP", StringComparison.Ordinal); }, 5000);
                    T.Check("portmap: device description over 256 KiB refused — fetched once, no SOAP call", !mbig.Mapped && igd.Gets > getsBefore && igd.Snapshot().Count == soapBefore, mbig.Status);
                }
                finally
                {
                    mbig.Dispose();
                    igd.DescriptionBytes = 0;
                }
            }

            using (PortMapFxUdp pmp = new PortMapFxUdp(null))
            {
                BtPortMapper mp = new BtPortMapper(null, "http://127.0.0.1:" + PortMapClosedPort() + "/desc.xml", pmp.Ep);
                mp.HttpTimeoutMs = 1500;
                try
                {
                    mp.Start(40161, 40162);
                    bool ok = DhtWaitFor(delegate { return mp.Mapped; }, 6000);
                    List<string> seen = pmp.Snapshot();
                    T.Check("portmap: no UPnP -> NAT-PMP fallback maps TCP and UDP, external address from the gateway",
                            ok && seen.Contains("ext") && seen.Contains("TCP 40161 3600") && seen.Contains("UDP 40162 3600")
                            && IPAddress.Parse("198.51.100.9").Equals(mp.ExternalAddress) && mp.Status.Contains("NAT-PMP"),
                            mp.Status + " | " + string.Join(",", seen.ToArray()));
                }
                finally
                {
                    mp.Dispose();
                }
                List<string> after = pmp.Snapshot();
                T.Check("portmap: NAT-PMP mappings deleted on Dispose (lifetime 0)", after.Contains("TCP 40161 0") && after.Contains("UDP 40162 0"), string.Join(",", after.ToArray()));
            }
        }

        // ================================================================== //
        //  libtorrent — независимая реализация
        // ================================================================== //
        private sealed class DhtOracle : IDisposable
        {
            private Process _p;
            private readonly List<string> _lines = new List<string>();

            public static DhtOracle Start(string mode, string work, out string why)
            {
                why = null;
                string py = Environment.GetEnvironmentVariable("WPC_LT_PYTHON");
                if (string.IsNullOrEmpty(py) || !File.Exists(py)) { why = "WPC_LT_PYTHON not set"; return null; }
                string script = null;
                for (string dir = Path.GetDirectoryName(py); dir != null && script == null; dir = Path.GetDirectoryName(dir))
                {
                    string cand = Path.Combine(Path.Combine(Path.Combine(dir, "tests"), "bt-oracle"), "dht_mse.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null)
                {
                    string cand = Path.Combine(Environment.CurrentDirectory, @"tests\bt-oracle\dht_mse.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null) { why = "tests\\bt-oracle\\dht_mse.py not found"; return null; }
                ProcessStartInfo psi = new ProcessStartInfo(py, "\"" + script + "\" " + mode + " \"" + work + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                DhtOracle o = new DhtOracle();
                o._p = Process.Start(psi);
                o._p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (o._lines) o._lines.Add(e.Data); };
                o._p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                o._p.BeginOutputReadLine();
                o._p.BeginErrorReadLine();
                if (o.Read("READY", 20000) == null)
                {
                    why = "oracle did not start: " + string.Join(" / ", o._lines.ToArray());
                    o.Dispose();
                    return null;
                }
                return o;
            }

            // Первая строка с таким началом (из уже прочитанных тоже); null — не дождались.
            public string Read(string prefix, int ms)
            {
                string found = null;
                DhtWaitFor(delegate
                {
                    lock (_lines)
                        for (int i = 0; i < _lines.Count; i++)
                            if (_lines[i].StartsWith(prefix, StringComparison.Ordinal)) { found = _lines[i]; _lines.RemoveAt(i); return true; }
                    return false;
                }, ms);
                return found;
            }

            public void Send(string command)
            {
                _p.StandardInput.WriteLine(command);
                _p.StandardInput.Flush();
            }

            public void Dispose()
            {
                if (_p == null) return;
                try { Send("quit"); _p.StandardInput.Close(); } catch { }
                try { if (!_p.WaitForExit(5000)) _p.Kill(); } catch { }
                _p.Dispose();
                _p = null;
            }
        }

        private static byte[] BtHandshakeBytes(byte[] hash)
        {
            byte[] h = new byte[68];
            h[0] = 19;
            Buffer.BlockCopy(A("BitTorrent protocol"), 0, h, 1, 19);
            Buffer.BlockCopy(hash, 0, h, 28, 20);
            Buffer.BlockCopy(BtContext.NewPeerId(), 0, h, 48, 20);
            return h;
        }

        // После рукопожатия: BT-рукопожатие пира (68 байт) и следующее сообщение целиком — всё через Decryptor.
        private static byte[] OracleReadStream(Socket s, IBtStreamHandshake hs, int want)
        {
            MemoryStream ms = new MemoryStream();
            ms.Write(hs.Remaining, 0, hs.Remaining.Length);
            byte[] buf = new byte[4096];
            try
            {
                while (ms.Length < want)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    if (hs.Decryptor != null) hs.Decryptor.Apply(buf, 0, n);
                    ms.Write(buf, 0, n);
                }
            }
            catch (SocketException) { }
            return ms.ToArray();
        }

        private static BtHandshakeState OracleHandshake(Socket s, IBtStreamHandshake hs, bool begin)
        {
            List<byte[]> send = new List<byte[]>();
            BtHandshakeState st = BtHandshakeState.NeedMore;
            try
            {
                if (begin) hs.Begin(send);
                byte[] buf = new byte[4096];
                while (true)
                {
                    foreach (byte[] b in send) s.Send(b);
                    send.Clear();
                    if (st != BtHandshakeState.NeedMore) break;
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    st = hs.Feed(buf, 0, n, send);
                }
            }
            catch (SocketException) { }
            return st;
        }

        private static bool OracleBtReply(byte[] got, byte[] hash, out string info)
        {
            info = "got " + (got == null ? 0 : got.Length) + " bytes";
            if (got == null || got.Length < 68 + 5) return false;
            bool hsOk = got[0] == 19 && Encoding.ASCII.GetString(got, 1, 19) == "BitTorrent protocol" && Bencode.Hex(Sub(got, 28, 20)) == Bencode.Hex(hash);
            int len = (got[68] << 24) | (got[69] << 16) | (got[70] << 8) | got[71];
            info += ", next message len " + len + " id " + got[72];
            return hsOk && len >= 1 && len < 1024 && (got[72] == 5 || got[72] == 14 || got[72] == 20);
        }

        private static byte[] Sub(byte[] b, int at, int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, at, r, 0, n);
            return r;
        }

        private static void DhtOracleMse()
        {
            string why;
            string work = Fx.MakeDir(Fx.Root, "bt-oracle-mse");
            DhtOracle lt = DhtOracle.Start("mse", work, out why);
            if (lt == null)
            {
                T.Skip("mse: libtorrent (encryption forced) accepts our MSE Outgoing and answers inside RC4", why);
                T.Skip("mse: libtorrent dials us encrypted — our MSE Incoming completes and decrypts its handshake", why);
                return;
            }
            try
            {
                string hashLine = lt.Read("HASH ", 1000), portLine = lt.Read("PORT ", 1000);
                byte[] hash = hashLine == null ? null : Bencode.FromHex(hashLine.Substring(5).Trim());
                int port = portLine == null ? 0 : int.Parse(portLine.Substring(5).Trim());
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.ReceiveTimeout = 8000;
                    s.Connect(new IPEndPoint(IPAddress.Loopback, port));
                    IBtStreamHandshake hs = BtMse.Outgoing(hash, BtEncryption.Require);
                    BtHandshakeState st = OracleHandshake(s, hs, true);
                    string info = st + " " + hs.Error;
                    bool ok = false;
                    if (st == BtHandshakeState.Done && hs.Encryptor != null)
                    {
                        byte[] ours = BtHandshakeBytes(hash);
                        hs.Encryptor.Apply(ours, 0, ours.Length);
                        s.Send(ours);
                        ok = OracleBtReply(OracleReadStream(s, hs, 68 + 5), hash, out info);
                    }
                    T.Check("mse: libtorrent (encryption forced) accepts our MSE Outgoing and answers inside RC4", ok, info);
                }

                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    lt.Send("connect " + ((IPEndPoint)listener.LocalEndpoint).Port);
                    bool dialed = DhtWaitFor(delegate { return listener.Pending(); }, 10000);
                    string info = "not dialed";
                    bool ok = false;
                    if (dialed)
                        using (Socket s = listener.AcceptSocket())
                        {
                            s.ReceiveTimeout = 8000;
                            IBtStreamHandshake hs = BtMse.Incoming(delegate(byte[] req2) { return Bencode.SameBytes(req2, BtMse.Req2(hash)) ? hash : null; }, BtEncryption.Require);
                            BtHandshakeState st = OracleHandshake(s, hs, false);
                            info = st + " " + hs.Error;
                            if (st == BtHandshakeState.Done && hs.Decryptor != null && Bencode.SameBytes(hs.InfoHash, hash))
                            {
                                byte[] theirs = OracleReadStream(s, hs, 68);
                                bool hsOk = theirs.Length >= 68 && theirs[0] == 19 && Bencode.Hex(Sub(theirs, 28, 20)) == Bencode.Hex(hash);
                                byte[] ours = BtHandshakeBytes(hash);
                                hs.Encryptor.Apply(ours, 0, ours.Length);
                                s.Send(ours);
                                byte[] extra = new byte[0];
                                if (theirs.Length > 68) extra = Sub(theirs, 68, theirs.Length - 68);
                                // Следующее сообщение после рукопожатия — тем же расшифровщиком.
                                byte[] next = new byte[0];
                                MemoryStream rest = new MemoryStream();
                                rest.Write(extra, 0, extra.Length);
                                byte[] buf = new byte[4096];
                                try
                                {
                                    while (rest.Length < 5)
                                    {
                                        int n = s.Receive(buf);
                                        if (n <= 0) break;
                                        hs.Decryptor.Apply(buf, 0, n);
                                        rest.Write(buf, 0, n);
                                    }
                                }
                                catch (SocketException) { }
                                next = rest.ToArray();
                                byte[] whole = new byte[68 + next.Length];
                                Buffer.BlockCopy(theirs, 0, whole, 0, 68);
                                Buffer.BlockCopy(next, 0, whole, 68, next.Length);
                                ok = hsOk && OracleBtReply(whole, hash, out info);
                            }
                        }
                    T.Check("mse: libtorrent dials us encrypted — our MSE Incoming completes and decrypts its handshake", ok, info);
                }
                finally
                {
                    listener.Stop();
                }
            }
            finally
            {
                lt.Dispose();
            }
        }

        private static void DhtOracleDht()
        {
            string why;
            string work = Fx.MakeDir(Fx.Root, "bt-oracle-dht");
            DhtOracle lt = DhtOracle.Start("dht", work, out why);
            string[] names =
            {
                "dht: libtorrent node answers our find_node — it enters our routing table",
                "dht: our announce_peer accepted by libtorrent; get_peers from another of our nodes returns the peer",
                "dht: libtorrent get_peers + announce through our node — our replies and token accepted"
            };
            if (lt == null)
            {
                foreach (string n in names) T.Skip(n, why);
                return;
            }
            DhtFxNode a = null, b = null;
            try
            {
                string portLine = lt.Read("PORT ", 1000);
                string ltAddr = "127.0.0.1:" + (portLine == null ? "0" : portLine.Substring(5).Trim());
                a = new DhtFxNode(Fx.MakeDir(work, "a"), ltAddr);
                b = new DhtFxNode(Fx.MakeDir(work, "b"), ltAddr);
                a.Dht.QueryTimeoutMs = 1500;
                b.Dht.QueryTimeoutMs = 1500;
                a.Dht.Start();
                DhtFxNode an = a;
                T.Check(names[0], DhtWaitFor(delegate { return an.Dht.NodeCount >= 1; }, 6000), "nodes " + a.Dht.NodeCount);

                byte[] hash = DhtRandom(20);
                DhtFxSwarm announcer = new DhtFxSwarm();
                announcer.Hash = hash;
                a.Ctx.InboundOpen = true;
                a.Dht.GetPeers(announcer, true);
                DhtWaitFor(delegate { return an.Dht.LookupCount == 0; }, 6000);
                Thread.Sleep(300);
                b.Dht.Start();
                DhtFxSwarm seeker = new DhtFxSwarm();
                seeker.Hash = hash;
                BtEndpoint expected = new BtEndpoint(IPAddress.Loopback, a.Ctx.Port);
                DhtFxNode bn = b;
                bool found = DhtWaitFor(delegate
                {
                    if (seeker.Has(expected)) return true;
                    if (bn.Dht.Ready && bn.Dht.LookupCount == 0) bn.Dht.GetPeers(seeker, false);
                    return false;
                }, 10000);
                T.Check(names[1], found, "seeker got " + seeker.Count);

                byte[] hash2 = DhtRandom(20);
                lt.Send("addnode " + b.Udp.Port);
                lt.Read("OK addnode", 3000);
                Thread.Sleep(1000);
                lt.Send("announce " + Bencode.Hex(hash2) + " 46002");
                lt.Read("OK announce", 3000);
                bool stored = DhtWaitFor(delegate { return bn.Dht.StoredPeers(hash2) + an.Dht.StoredPeers(hash2) >= 1; }, 10000);
                T.Check(names[2], stored, "stored " + b.Dht.StoredPeers(hash2) + "+" + a.Dht.StoredPeers(hash2));
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
                lt.Dispose();
            }
        }
    }
}
