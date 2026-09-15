﻿// SysDeck — область «torrent», часть C: DHT, PEX, шифрование MSE, проброс порта.
// Сборка и запуск: tests\run-tests.bat torrent.
//
// Сеть — только петля 127.0.0.1: узлы DHT на настоящих BtUdp, MSE через настоящую пару TCP-сокетов, роутер — поддельный
// IGD на HttpListener и поддельный NAT-PMP на UDP-сокете. Начальные узлы DHT продукта подменены пустым списком: ни одного
// пакета в интернет или в локальную сеть. Сверка с независимой реализацией — libtorrent 2.x (SYSDECK_LT_PYTHON, иначе SKIP).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
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
    }
}
