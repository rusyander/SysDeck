// SysDeck — область «torrent», часть B: передача, лимиты, злонамеренный пир, шифрование, сверка.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class TorrentTests
    {
        private static void WireUploadLimit()
        {
            const long Limit = 256000;
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(1500000, 111)) };
            BtMeta meta = WireMeta(files, 65536, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-uplimit");
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s = WireSession(BtEncryption.Off), l = WireSession(BtEncryption.Off);
                sessions.AddRange(new[] { s, l });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                ts.UpLimit = Limit;
                BtTorrent tl = WireAdd(l, meta, Fx.MakeDir(root, "leech"), null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && tl.State == BtTorrentState.Downloading; }, 5000);
                tl.AddPeer(WireEp(s));
                bool started = WireWaitFor(delegate { return ts.Uploaded > 0; }, 5000);
                Thread.Sleep(500);
                long u0 = ts.Uploaded;
                Stopwatch sw = Stopwatch.StartNew();
                Thread.Sleep(2000);
                long u1 = ts.Uploaded;
                double rate = (u1 - u0) * 1000.0 / sw.ElapsedMilliseconds;
                T.Check("session: seed UpLimit 256 KB/s holds on the wire (measured 2 s window within 50..130 %)",
                        started && rate >= Limit * 0.5 && rate <= Limit * 1.3, ((long)rate) + " B/s");
            }
            finally { WireDispose(sessions); }
        }

        private static void WireResumeMidway()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(600000, 121), "x.bin"), new BtFxFile(BtFx.Data(450000, 122), "y.bin") };
            BtMeta meta = WireMeta(files, 32768, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-resume");
            string dl = Fx.MakeDir(root, "leech");
            List<BtSession> sessions = new List<BtSession>();
            BtSession first = null;
            try
            {
                BtSession s = WireSession(BtEncryption.Off);
                sessions.Add(s);
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                ts.UpLimit = 400000;
                first = WireSession(BtEncryption.Off);
                BtTorrent t1 = WireAdd(first, meta, dl, null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && t1.State == BtTorrentState.Downloading; }, 5000);
                t1.AddPeer(WireEp(s));
                bool some = WireWaitFor(delegate { BtBitfield h = t1.Have; return h != null && h.SetCount >= 8; }, 10000);
                BtResume r = t1.CaptureResume();
                first.Dispose();
                first = null;
                BtBitfield saved = r == null ? null : BtBitfield.FromBytes(r.Have, r.PieceCount);
                int savedCount = saved == null ? 0 : saved.SetCount;
                T.Check("session: mid-way resume snapshot holds verified pieces but not all", some && savedCount >= 8 && savedCount < meta.PieceCount,
                        savedCount + "/" + meta.PieceCount);

                ts.UpLimit = 0;
                BtSession second = WireSession(BtEncryption.Off);
                sessions.Add(second);
                BtTorrent t2 = WireAdd(second, meta, dl, r);
                WireWaitFor(delegate { return t2.State == BtTorrentState.Downloading || t2.State == BtTorrentState.Seeding; }, 5000);
                int restored = t2.Have == null ? 0 : t2.Have.SetCount;
                t2.AddPeer(WireEp(s));
                bool done = WireWaitFor(delegate { return t2.State == BtTorrentState.Seeding; }, 15000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                long allowance = (meta.PieceCount - savedCount) * meta.PieceLength;
                T.Check("session: new session with CaptureResume completes without re-downloading verified pieces",
                        done && same && restored >= savedCount && t2.Downloaded <= allowance && t2.Downloaded > 0,
                        WireState(t2) + " restored " + restored + " downloaded " + t2.Downloaded + " allowance " + allowance + " " + info);
                T.Check("session: counters continue from the resume data", t2.Stats().Downloaded >= r.Downloaded + t2.Downloaded, t2.Stats().Downloaded + " vs " + r.Downloaded);
            }
            finally
            {
                if (first != null) first.Dispose();
                WireDispose(sessions);
            }
        }

        // «Злой» пир: отдаёт мусор на каждый запрос. Одиночный автор испорченного куска — блокировка, дальше честный сид.
        private static void WireMaliciousPeer()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(400000, 131)) };
            BtMeta meta = WireMeta(files, 65536, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-evil");
            string dl = Fx.MakeDir(root, "leech");
            List<BtSession> sessions = new List<BtSession>();
            TcpListener evil = new TcpListener(IPAddress.Parse("127.0.0.2"), 0);
            int served = 0;
            bool closedByUs = false;
            Thread worker = null;
            try
            {
                evil.Start();
                int evilPort = ((IPEndPoint)evil.LocalEndpoint).Port;
                worker = new Thread(delegate()
                {
                    try
                    {
                        using (Socket c = evil.AcceptSocket())
                        {
                            c.ReceiveTimeout = 15000;
                            byte[] hs = WireReadExact(c, 68);
                            if (hs == null) return;
                            byte[] mine = BtWire.Handshake(meta.SwarmHash, Encoding.ASCII.GetBytes("-EV0001-evilevilevil"), false);
                            mine[25] = 0;
                            mine[27] = 0;
                            c.Send(mine);
                            BtBitfield all = new BtBitfield(meta.PieceCount);
                            for (int i = 0; i < meta.PieceCount; i++) all[i] = true;
                            c.Send(BtWire.BitfieldMsg(all.ToBytes()));
                            c.Send(BtWire.Simple(BtWire.Unchoke));
                            while (true)
                            {
                                byte[] len = WireReadExact(c, 4);
                                if (len == null) { closedByUs = true; return; }
                                int n = BtWire.ReadInt(len, 0);
                                if (n < 0 || n > 1 << 20) return;
                                byte[] body = n == 0 ? new byte[0] : WireReadExact(c, n);
                                if (body == null) { closedByUs = true; return; }
                                if (n == 13 && body[0] == BtWire.Request)
                                {
                                    int length = BtWire.ReadInt(body, 9);
                                    c.Send(BtWire.PieceMsg(BtWire.ReadInt(body, 1), BtWire.ReadInt(body, 5), BtFx.Data(length, 999), 0, length));
                                    Interlocked.Increment(ref served);
                                }
                            }
                        }
                    }
                    catch (SocketException) { closedByUs = true; }
                    catch (ObjectDisposedException) { }
                });
                worker.IsBackground = true;
                worker.Start();

                List<string> journal = new List<string>();
                BtSession s = WireSession(BtEncryption.Off), l = WireSession(BtEncryption.Off, journal);
                sessions.AddRange(new[] { s, l });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                BtTorrent tl = WireAdd(l, meta, dl, null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && tl.State == BtTorrentState.Downloading; }, 5000);
                IPAddress evilIp = IPAddress.Parse("127.0.0.2");
                tl.AddPeer(new BtEndpoint(evilIp, evilPort));
                bool banned = WireWaitFor(delegate { return tl.IsBanned(evilIp); }, 8000);
                bool dropped = worker.Join(5000) && closedByUs;
                T.Check("peer: sole contributor of a corrupt piece is banned by IP and disconnected", banned && dropped && served > 0,
                        "served " + served + ", " + WireState(tl));
                T.Check("peer: the corrupt piece is not counted — nothing verified, bytes wasted", tl.Have != null && tl.Have.SetCount == 0 && tl.Stats().Wasted > 0,
                        WireState(tl) + " wasted " + tl.Stats().Wasted);

                tl.AddPeer(new BtEndpoint(evilIp, evilPort));
                tl.AddPeer(WireEp(s));
                bool done = WireWaitFor(delegate { return tl.State == BtTorrentState.Seeding; }, 15000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                T.Check("peer: after the ban the torrent completes from the honest seed with correct data", done && same, WireState(tl) + " " + info);
                string lines;
                int failsBeforeBan = 0, banAt = -1;
                string failText = Tr.S(" не прошёл проверку хеша", " failed the hash check"), banText = Tr.S(" заблокирован: ", " is banned: ");
                lock (journal)
                {
                    lines = string.Join(" | ", journal.ToArray());
                    for (int i = 0; i < journal.Count && banAt < 0; i++)
                    {
                        if (journal[i].Contains(banText) && journal[i].Contains("127.0.0.2")) banAt = i;
                        else if (journal[i].Contains(failText)) failsBeforeBan++;
                    }
                }
                T.Check("peer: the sole contributor is banned on its first corrupt piece, no strikes needed", banAt >= 0 && failsBeforeBan == 1, lines);
            }
            finally
            {
                try { evil.Stop(); } catch (SocketException) { }
                WireDispose(sessions);
                if (worker != null) worker.Join(2000);
            }
        }

        private static byte[] WireReadExact(Socket c, int n)
        {
            byte[] b = new byte[n];
            int got = 0;
            while (got < n)
            {
                int r = c.Receive(b, got, n - got, SocketFlags.None);
                if (r <= 0) return null;
                got += r;
            }
            return b;
        }

        private sealed class WireFakeExt : IBtExtension
        {
            public const string ExtName = "wpc_probe";
            public int HandshakeId = -1, Field = -1, Closed;
            public bool Supported;
            public string Got;
            public string Name { get { return ExtName; } }
            public void FillHandshake(BVal handshake) { handshake.Set("wpc_probe_field", BVal.Int(42)); }

            public void OnHandshake(IBtPeerLink peer, BVal handshake)
            {
                BVal m = handshake.Get("m", BKind.Dict);
                lock (this)
                {
                    HandshakeId = m == null ? -1 : (int)m.GetInt(ExtName, -1);
                    Field = (int)handshake.GetInt("wpc_probe_field", -1);
                    Supported = peer.Supports(ExtName);
                }
                peer.SendExtended(ExtName, Encoding.ASCII.GetBytes("ping"));
            }

            public void OnMessage(IBtPeerLink peer, byte[] payload, int offset, int count)
            {
                lock (this) Got = Encoding.ASCII.GetString(payload, offset, count);
            }

            public void OnClosed(IBtPeerLink peer) { Interlocked.Increment(ref Closed); }
            public void Tick(DateTime utcNow) { }
        }

        private static void WireExtensionDispatch()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(50000, 141)) };
            BtMeta meta = WireMeta(files, 32768, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-ext");
            List<BtSession> sessions = new List<BtSession>();
            List<WireFakeExt> exts = new List<WireFakeExt>();
            int start = BtFactories.Extensions.Count;
            BtFactories.Extensions.Add(null);
            BtFactories.Extensions.Add(delegate(IBtSwarm swarm, BtContext ctx)
            {
                WireFakeExt e = new WireFakeExt();
                lock (exts) exts.Add(e);
                return e;
            });
            try
            {
                BtSession s = WireSession(BtEncryption.Off), l = WireSession(BtEncryption.Off);
                sessions.AddRange(new[] { s, l });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                BtTorrent tl = WireAdd(l, meta, Fx.MakeDir(root, "leech"), null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && tl.State == BtTorrentState.Downloading; }, 5000);
                tl.AddPeer(WireEp(s));
                bool both = WireWaitFor(delegate
                {
                    lock (exts)
                    {
                        if (exts.Count != 2) return false;
                        foreach (WireFakeExt e in exts) lock (e) if (e.Got != "ping") return false;
                        return true;
                    }
                }, 8000);
                string detail = "";
                bool fields = exts.Count == 2;
                foreach (WireFakeExt e in exts)
                    lock (e)
                    {
                        detail += "[id " + e.HandshakeId + " field " + e.Field + " sup " + e.Supported + " got " + e.Got + "] ";
                        if (e.HandshakeId != start + 2 || e.Field != 42 || !e.Supported) fields = false;
                    }
                T.Check("peer: registered extension gets the peer handshake (its id, its field) and the message routed by id", both && fields, detail);
            }
            finally
            {
                WireDispose(sessions);
                BtFactories.Extensions.RemoveRange(start, 2);
            }
        }

        private static void WireRatioLimit()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(300000, 151)) };
            BtMeta meta = WireMeta(files, 32768, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-ratio");
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s = WireSession(BtEncryption.Off), l = WireSession(BtEncryption.Off);
                sessions.AddRange(new[] { s, l });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                ts.RatioLimit = 1.0;
                string dl = Fx.MakeDir(root, "leech");
                BtTorrent tl = WireAdd(l, meta, dl, null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && tl.State == BtTorrentState.Downloading; }, 5000);
                tl.AddPeer(WireEp(s));
                bool finished = WireWaitFor(delegate { return ts.State == BtTorrentState.Finished; }, 15000);
                T.Check("session: ratio limit 1.0 reached ⇒ seed goes Finished", finished && ts.Stats().Uploaded >= meta.TotalSize,
                        WireState(ts) + " up " + ts.Stats().Uploaded);
                string info;
                bool done = WireWaitFor(delegate { return tl.State == BtTorrentState.Seeding; }, 5000) && WireSameFiles(meta, files, dl, out info);
                T.Check("session: the last blocks are flushed before the finished seed disconnects", done, WireState(tl));
                T.Check("session: a finished seed makes no new connections", WireWaitFor(delegate { return ts.Stats().Peers == 0; }, 7000), WireState(ts));
            }
            finally { WireDispose(sessions); }
        }

        // Шифрование «обязательно»: входящий открытый текст отвергается, исходящий открытый текст не уходит.
        private static void WireRequireEncryption()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(40000, 161)) };
            BtMeta meta = WireMeta(files, 32768, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-require");
            List<BtSession> sessions = new List<BtSession>();
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                BtSession req = WireSession(BtEncryption.Require), pre = WireSession(BtEncryption.Prefer);
                sessions.AddRange(new[] { req, pre });
                BtTorrent tr = WireAddSeed(req, meta, files, Fx.MakeDir(root, "require"));
                BtTorrent tp = WireAddSeed(pre, meta, files, Fx.MakeDir(root, "prefer"));
                WireWaitFor(delegate { return tr.State == BtTorrentState.Seeding && tp.State == BtTorrentState.Seeding; }, 5000);
                int gotReq = WirePlainProbe(req.Context.Port, meta.SwarmHash), gotPre = WirePlainProbe(pre.Context.Port, meta.SwarmHash);
                T.Check("peer: Require refuses an incoming plaintext handshake (closed, no reply); Prefer answers it",
                        gotReq == 0 && gotPre >= 68, "require " + gotReq + ", prefer " + gotPre);

                probe.Start();
                tr.AddPeer(new BtEndpoint(IPAddress.Loopback, ((IPEndPoint)probe.LocalEndpoint).Port));
                bool plain = false;
                if (WireWaitFor(delegate { return probe.Pending(); }, 1500))
                    using (Socket c = probe.AcceptSocket())
                    {
                        c.ReceiveTimeout = 1000;
                        byte[] b = new byte[20];
                        int n = 0;
                        try { n = c.Receive(b); }
                        catch (SocketException) { }
                        plain = n > 0 && BtWire.LooksPlain(b, 0, n);
                    }
                T.Check("peer: Require never sends a plaintext handshake out", !plain);
            }
            finally
            {
                try { probe.Stop(); } catch (SocketException) { }
                WireDispose(sessions);
            }
        }

        // Байт ответа на открытое рукопожатие; -1 — соединение не закрылось и ответа нет.
        private static int WirePlainProbe(int port, byte[] hash)
        {
            using (Socket c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                c.Connect(new IPEndPoint(IPAddress.Loopback, port));
                c.ReceiveTimeout = 3000;
                c.Send(BtWire.Handshake(hash, Encoding.ASCII.GetBytes("-PR0001-probeprobepr"), false));
                byte[] buf = new byte[256];
                int total = 0;
                try
                {
                    while (total < 68)
                    {
                        int n = c.Receive(buf, total, buf.Length - total, SocketFlags.None);
                        if (n <= 0) break;
                        total += n;
                    }
                }
                catch (SocketException ex)
                {
                    if (ex.SocketErrorCode == SocketError.TimedOut) return -1;
                }
                return total >= 20 && !BtWire.LooksPlain(buf, 0, 20) ? -2 : total;
            }
        }

        private static void WireMagnetMetadata()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(90000, 171), "m1.bin"), new BtFxFile(BtFx.Data(30000, 172), "m2.bin") };
            BtMeta v2 = WireMeta(files, 32768, 2);
            BtMeta hybrid = WireMeta(files, 32768, 3);
            BtMeta other = WireMeta(new List<BtFxFile> { new BtFxFile(BtFx.Data(30000, 173), "o.bin") }, 32768, 3);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-magnet");
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s = WireSession(BtEncryption.Off);
                sessions.Add(s);
                string err;
                BtAddParams p = new BtAddParams();
                p.Magnet = BtMagnet.Parse("magnet:?xt=urn:btmh:1220" + Bencode.Hex(v2.InfoHashV2), out err);
                p.Folder = Fx.MakeDir(root, "v2");
                BtTorrent t = s.Add(p, out err);
                int metaRaised = 0;
                if (t != null) t.MetadataReceived = delegate { Interlocked.Increment(ref metaRaised); };
                bool fetching = t != null && WireWaitFor(delegate { return t.State == BtTorrentState.FetchingMetadata; }, 3000);
                bool wrong = t != null && t.OnMetadata(other.InfoBytes);
                bool accepted = t != null && t.OnMetadata(v2.InfoBytes);
                bool error = t != null && WireWaitFor(delegate { return t.State == BtTorrentState.Error; }, 3000);
                T.Check("session: pure v2 magnet — metadata of another torrent refused, own accepted once, then Error (no piece layers)",
                        fetching && !wrong && accepted && error && t.Error == Tr.S("нужны слои хешей v2, пока не поддерживается", "v2 piece layers are needed, not supported yet")
                        && t.Meta == null && metaRaised == 0 && !t.OnMetadata(v2.InfoBytes),
                        t == null ? err : WireState(t));

                BtAddParams ph = new BtAddParams();
                ph.Magnet = BtMagnet.Parse("magnet:?xt=urn:btih:" + Bencode.Hex(hybrid.InfoHash), out err);
                ph.Folder = Fx.MakeDir(root, "hybrid");
                ph.RootName = "wire";
                BtTorrent th = s.Add(ph, out err);
                bool ok = th != null && th.OnMetadata(hybrid.InfoBytes) && WireWaitFor(delegate { return th.State == BtTorrentState.Downloading; }, 3000);
                T.Check("session: hybrid magnet — metadata accepted, v1 pieces used, Downloading", ok && th.Storage != null && th.Have != null, th == null ? err : WireState(th));
                string dupErr;
                BtAddParams again = new BtAddParams();
                again.Meta = hybrid;
                again.Folder = ph.Folder;
                T.Check("session: the same swarm cannot be added twice", s.Add(again, out dupErr) == null && !string.IsNullOrEmpty(dupErr));
                s.Remove(th);
                T.Check("session: Remove drops the torrent from the session", s.Find(hybrid.SwarmHash) == null && s.Torrents().Count == 1);
            }
            finally { WireDispose(sessions); }
        }

        // ================================================================== //
        //  libtorrent — независимая реализация
        // ================================================================== //
        private sealed class WireOracle : IDisposable
        {
            private Process _p;
            private readonly List<string> _lines = new List<string>();

            public static WireOracle Start(string args, out string why)
            {
                why = null;
                string py = Environment.GetEnvironmentVariable("SYSDECK_LT_PYTHON");
                if (string.IsNullOrEmpty(py) || !File.Exists(py)) { why = "SYSDECK_LT_PYTHON not set"; return null; }
                string script = null;
                for (string dir = Path.GetDirectoryName(py); dir != null && script == null; dir = Path.GetDirectoryName(dir))
                {
                    string cand = Path.Combine(Path.Combine(Path.Combine(dir, "tests"), "bt-oracle"), "wire.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null)
                {
                    string cand = Path.Combine(Environment.CurrentDirectory, @"tests\bt-oracle\wire.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null) { why = "FAIL: tests\\bt-oracle\\wire.py not found"; return null; }
                ProcessStartInfo psi = new ProcessStartInfo(py, "\"" + script + "\" " + args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                WireOracle o = new WireOracle();
                o._p = new Process();
                o._p.StartInfo = psi;
                o._p.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (o._lines) o._lines.Add(e.Data); };
                o._p.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) lock (o._lines) o._lines.Add("ERR " + e.Data); };
                o._p.Start();
                o._p.BeginOutputReadLine();
                o._p.BeginErrorReadLine();
                return o;
            }

            // Первая строка с префиксом; строка FAIL тоже возвращается (и выход процесса — как "EXIT").
            public string WaitLine(string prefix, int ms)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (true)
                {
                    lock (_lines)
                        foreach (string l in _lines)
                            if (l.StartsWith(prefix, StringComparison.Ordinal) || l.StartsWith("FAIL", StringComparison.Ordinal)) return l;
                    if (_p.HasExited && sw.ElapsedMilliseconds > 300) return "EXIT " + Output();
                    if (sw.ElapsedMilliseconds > ms) return null;
                    Thread.Sleep(30);
                }
            }

            public string Output() { lock (_lines) return string.Join(" | ", _lines.ToArray()); }

            public bool WaitExit(int ms)
            {
                try { return _p.WaitForExit(ms); }
                catch (InvalidOperationException) { return true; }
            }

            public void CloseInput()
            {
                try { _p.StandardInput.Close(); }
                catch (IOException) { }
                catch (InvalidOperationException) { }
            }

            public void Dispose()
            {
                CloseInput();
                try
                {
                    if (!_p.WaitForExit(5000)) _p.Kill();
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                _p.Dispose();
            }
        }

        private static bool WireOracleSkip(string name, string why)
        {
            if (why != null && why.StartsWith("FAIL", StringComparison.Ordinal)) { T.Check(name, false, why); return true; }
            T.Skip(name, why);
            return true;
        }

        private static void WireOracleLtSeeds()
        {
            const string Name = "peer: libtorrent seeds v1 multi-file ⇒ we download, SHA-256 equal";
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(300000, 181), "p.bin"), new BtFxFile(BtFx.Data(123457, 182), "q", "r.bin") };
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-lt-seed");
            string ltDir = Fx.MakeDir(root, "lt");
            string file = Path.Combine(root, "wire.torrent");
            File.WriteAllBytes(file, torrent);
            WritePlaced(meta, files, ltDir, "wire");
            string why;
            List<BtSession> sessions = new List<BtSession>();
            using (WireOracle lt = WireOracle.Start("seed \"" + file + "\" \"" + ltDir + "\"", out why))
            {
                if (lt == null) { WireOracleSkip(Name, why); return; }
                try
                {
                    string portLine = lt.WaitLine("PORT ", 20000);
                    string seeding = lt.WaitLine("SEEDING", 5000);
                    int port;
                    if (portLine == null || !portLine.StartsWith("PORT ") || !int.TryParse(portLine.Substring(5), out port) || seeding != "SEEDING")
                    {
                        T.Check(Name, false, "oracle did not start: " + lt.Output());
                        return;
                    }
                    BtSession l = WireSession(BtEncryption.Off);
                    sessions.Add(l);
                    string dl = Fx.MakeDir(root, "we");
                    BtTorrent t = WireAdd(l, meta, dl, null);
                    WireWaitFor(delegate { return t.State == BtTorrentState.Downloading; }, 5000);
                    t.AddPeer(new BtEndpoint(IPAddress.Loopback, port));
                    bool done = WireWaitFor(delegate { return t.State == BtTorrentState.Seeding; }, 20000);
                    string info;
                    bool same = WireSameFiles(meta, files, dl, out info);
                    lt.CloseInput();
                    string up = lt.WaitLine("UPLOADED ", 5000);
                    long uploaded;
                    bool counted = up != null && up.StartsWith("UPLOADED ") && long.TryParse(up.Substring(9), out uploaded) && uploaded >= meta.TotalSize;
                    T.Check(Name, done && same && counted, WireState(t) + " " + info + " lt: " + lt.Output());
                }
                finally { WireDispose(sessions); }
            }
        }

        private static void WireOracleLtLeeches()
        {
            const string Name = "peer: we seed a hybrid multi-file ⇒ libtorrent downloads, SHA-256 equal";
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(200001, 191), "u.bin"), new BtFxFile(BtFx.Data(90000, 192), "v", "w.bin") };
            byte[] torrent = BtFx.Build("wire", files, 32768, 3, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-lt-leech");
            string ltDir = Fx.MakeDir(root, "lt");
            string file = Path.Combine(root, "wire.torrent");
            File.WriteAllBytes(file, torrent);
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SYSDECK_LT_PYTHON"))) { T.Skip(Name, "SYSDECK_LT_PYTHON not set"); return; }
                BtSession s = WireSession(BtEncryption.Off);
                sessions.Add(s);
                BtTorrent t = WireAddSeed(s, meta, files, Fx.MakeDir(root, "we"));
                if (!WireWaitFor(delegate { return t.State == BtTorrentState.Seeding; }, 5000)) { T.Check(Name, false, WireState(t)); return; }
                string why;
                using (WireOracle lt = WireOracle.Start("leech \"" + file + "\" \"" + ltDir + "\" " + s.Context.Port, out why))
                {
                    if (lt == null) { WireOracleSkip(Name, why); return; }
                    string line = lt.WaitLine("DONE ", 45000);
                    bool exited = lt.WaitExit(10000);             // libtorrent дописывает файлы при закрытии сессии
                    string info = "";
                    bool same = line != null && line.StartsWith("DONE ") && exited && WireSameFiles(meta, files, ltDir, out info);
                    T.Check(Name, same && t.Stats().Uploaded >= meta.TotalSize, "lt: " + lt.Output() + " | " + info + " | " + WireState(t) + " up " + t.Stats().Uploaded);
                }
            }
            finally { WireDispose(sessions); }
        }
    }
}
