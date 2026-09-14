// Windows Process Cleaner — область «torrent», часть соединений: кодек протокола пиров, выбор кусков, раздача слотов,
// рой и сессия.
//
// Настоящий путь: несколько BtSession на 127.0.0.1 передают торренты друг другу через сокеты, реактор, диск и проверку
// хешей; «злой» пир — сырой сокет на 127.0.0.2. Независимая реализация — libtorrent (tests\bt-oracle\wire.py), если
// сборочный скрипт полосы нашёл python с ним (WPC_LT_PYTHON); нет — пропуск. Интернет не трогается, всё — в фикстуре.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunWire()
        {
            WireRun("codec", WireCodecCases);
            WireRun("ext handshake", WireExtHandshakeCases);
            WireRun("picker", WirePickerCases);
            WireRun("choker", WireChokerCases);
            WireRun("v1 three sessions", WireTransferV1ThreeSessions);
            WireRun("hybrid", delegate { WireTransferV2(3, "hybrid"); });
            WireRun("pure v2", delegate { WireTransferV2(2, "pure v2"); });
            WireRun("upload limit", WireUploadLimit);
            WireRun("resume", WireResumeMidway);
            WireRun("malicious peer", WireMaliciousPeer);
            WireRun("extension dispatch", WireExtensionDispatch);
            WireRun("ratio limit", WireRatioLimit);
            WireRun("require encryption", WireRequireEncryption);
            WireRun("magnet metadata", WireMagnetMetadata);
            WireRun("libtorrent seeds", WireOracleLtSeeds);
            WireRun("libtorrent leeches", WireOracleLtLeeches);
        }

        // Исключение в одном сценарии не отменяет остальные.
        private static void WireRun(string name, Action body)
        {
            try { body(); }
            catch (Exception ex) { T.Check("wire: scenario '" + name + "' runs without an exception", false, ex.ToString().Replace(Environment.NewLine, " ")); }
        }

        // ================================================================== //
        //  Помощники
        // ================================================================== //
        private static bool WireWaitFor(Func<bool> cond, int ms)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(20);
            }
            return cond();
        }

        private static BtSession WireSession(BtEncryption enc) { return WireSession(enc, null); }

        // log — строки журнала сессии в порядке записи (журнал торрента идёт через пул потоков, порядок там не гарантирован).
        private static BtSession WireSession(BtEncryption enc, List<string> log)
        {
            BtSessionOptions o = new BtSessionOptions();
            o.Bind = IPAddress.Loopback;
            o.InboundOpen = true;
            o.EnableDht = false;
            o.EnableLsd = false;
            o.EnablePex = false;
            o.Encryption = enc;
            o.TorrentsDir = Fx.MakeDir(Fx.Root, "bt-wire-state");
            if (log != null) o.Log = delegate(string line) { lock (log) log.Add(line); };
            BtSession s = new BtSession(o);
            s.Start();
            return s;
        }

        private static BtMeta WireMeta(IList<BtFxFile> files, int pieceLength, int mode)
        {
            string err;
            BtMeta m = BtMeta.Parse(BtFx.Build("wire", files, pieceLength, mode, false, null), out err);
            if (m == null) throw new InvalidOperationException("fixture torrent: " + err);
            return m;
        }

        // Исходные данные файла торрента (заполнители — нули, их нет в списке фикстуры).
        private static byte[] WireSource(BtMeta meta, IList<BtFxFile> files, int index)
        {
            BtFile f = meta.Files[index];
            if (files.Count == 1 && files[0].Path == null) return files[0].Data;
            foreach (BtFxFile x in files)
                if (string.Join("\\", x.Path) == f.RelPath) return x.Data;
            return null;
        }

        private static void WritePlaced(BtMeta meta, IList<BtFxFile> files, string dir, string root)
        {
            using (BtStorage layout = new BtStorage(meta, dir, root))
                for (int i = 0; i < meta.Files.Count; i++)
                {
                    if (meta.Files[i].Pad) continue;
                    string path = layout.FinalPath(i);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllBytes(path, WireSource(meta, files, i));
                }
        }

        private static BtTorrent WireAdd(BtSession s, BtMeta meta, string dir, BtResume resume)
        {
            BtAddParams p = new BtAddParams();
            p.Meta = meta;
            p.Folder = dir;
            p.RootName = "wire";
            p.Resume = resume;
            string error;
            BtTorrent t = s.Add(p, out error);
            if (t == null) throw new InvalidOperationException("add: " + error);
            return t;
        }

        private static BtTorrent WireAddSeed(BtSession s, BtMeta meta, IList<BtFxFile> files, string dir)
        {
            WritePlaced(meta, files, dir, "wire");
            return WireAdd(s, meta, dir, null);
        }

        private static BtEndpoint WireEp(BtSession s) { return new BtEndpoint(IPAddress.Loopback, s.Context.Port); }

        // Итоговые файлы совпали с исходными (SHA-256), ни одного .wpcpart.
        private static bool WireSameFiles(BtMeta meta, IList<BtFxFile> files, string dir, out string info)
        {
            info = "";
            using (SHA256 sha = SHA256.Create())
            using (BtStorage layout = new BtStorage(meta, dir, "wire"))
            {
                for (int i = 0; i < meta.Files.Count; i++)
                {
                    if (meta.Files[i].Pad) continue;
                    string path = layout.FinalPath(i);
                    if (!File.Exists(path)) { info = "missing " + meta.Files[i].RelPath; return false; }
                    byte[] onDisk;
                    try
                    {
                        // Файл ещё открыт раздачей (общий доступ на запись) — читать с тем же разрешением.
                        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            onDisk = sha.ComputeHash(fs);
                    }
                    catch (IOException ex) { info = ex.Message; return false; }
                    if (!Bencode.SameBytes(onDisk, sha.ComputeHash(WireSource(meta, files, i))))
                    {
                        info = "content differs: " + meta.Files[i].RelPath;
                        return false;
                    }
                }
            }
            string[] parts = Directory.GetFiles(dir, "*" + DlPaths.PartSuffix, SearchOption.AllDirectories);
            if (parts.Length > 0) { info = parts.Length + " .wpcpart left"; return false; }
            return true;
        }

        private static void WireDispose(List<BtSession> sessions)
        {
            foreach (BtSession s in sessions)
                try { s.Dispose(); }
                catch (Exception ex) { T.Check("session: Dispose does not throw", false, ex.Message); }
        }

        private static string WireState(BtTorrent t)
        {
            BtTorrentStats s = t.Stats();
            return t.State + " " + s.PiecesHave + "/" + s.PieceCount + " peers " + s.Peers + (t.Error.Length > 0 ? " err " + t.Error : "");
        }

        // ================================================================== //
        //  Кодек
        // ================================================================== //
        private static void WireCodecCases()
        {
            byte[] hash = new byte[20], id = new byte[20];
            for (int i = 0; i < 20; i++) { hash[i] = (byte)i; id[i] = (byte)(100 + i); }
            byte[] hs = BtWire.Handshake(hash, id, true);
            byte[] reserved, h2, id2;
            bool parsed = BtWire.ParseHandshake(hs, 0, out reserved, out h2, out id2);
            T.Check("wire: handshake round-trips with ext, fast and DHT bits", parsed && hs.Length == 68 && Bencode.SameBytes(h2, hash)
                    && Bencode.SameBytes(id2, id) && BtWire.SupportsExtended(reserved) && BtWire.SupportsFast(reserved) && BtWire.SupportsDht(reserved));
            T.Check("wire: DHT bit is off without a DHT", !BtWire.SupportsDht(WireReserved(BtWire.Handshake(hash, id, false))));
            byte[] bad = (byte[])hs.Clone();
            bad[5] = (byte)'X';
            T.Check("wire: a foreign protocol string is not a handshake", !BtWire.ParseHandshake(bad, 0, out reserved, out h2, out id2) && !BtWire.LooksPlain(bad, 0, 20));

            int need;
            T.Check("wire: fewer than 4 bytes — frame incomplete", BtWire.FrameSize(new byte[3], 0, 3, out need) == 0 && need == 4);
            byte[] req = BtWire.Block(BtWire.Request, 7, 16384, 16384);
            T.Check("wire: request frame is 17 bytes and parses whole", req.Length == 17 && BtWire.FrameSize(req, 0, req.Length, out need) == 17
                    && BtWire.ReadInt(req, 5) == 7 && BtWire.ReadInt(req, 9) == 16384 && req[4] == BtWire.Request);
            byte[] bigPiece = new byte[5];
            BtWire.WriteInt(bigPiece, 0, 9 + BtMeta.BlockSize + 1);
            bigPiece[4] = BtWire.Piece;
            T.Check("wire: a piece message longer than one 16 KiB block is refused before allocation", BtWire.FrameSize(bigPiece, 0, 5, out need) == -1);
            byte[] bigBits = new byte[5];
            BtWire.WriteInt(bigBits, 0, 1 << 20);
            bigBits[4] = BtWire.Bitfield;
            T.Check("wire: a 1 MiB bitfield is allowed (waits for the rest)", BtWire.FrameSize(bigBits, 0, 5, out need) == 0 && need == 4 + (1 << 20));
            BtWire.WriteInt(bigBits, 0, (1 << 20) + 1);
            T.Check("wire: anything over 1 MiB is refused", BtWire.FrameSize(bigBits, 0, 5, out need) == -1);
            T.Check("wire: a negative length is refused", BtWire.FrameSize(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0 }, 0, 5, out need) == -1);
            byte[] pm = BtWire.PieceMsg(3, 32768, new byte[] { 9, 8, 7, 6 }, 1, 2);
            T.Check("wire: piece message layout", pm.Length == 15 && BtWire.ReadInt(pm, 0) == 11 && pm[4] == BtWire.Piece && BtWire.ReadInt(pm, 9) == 32768 && pm[13] == 8 && pm[14] == 7);
            byte[] port = BtWire.PortMsg(51413);
            T.Check("wire: PORT message is big-endian 16 bit", port.Length == 7 && port[4] == BtWire.Port && ((port[5] << 8) | port[6]) == 51413);

            // BEP 6, пример из спецификации.
            byte[] aa = new byte[20];
            for (int i = 0; i < 20; i++) aa[i] = 0xAA;
            List<int> seven = BtWire.AllowedFastSet(IPAddress.Parse("80.4.4.200"), aa, 1313, 7);
            T.Eq("wire: BEP 6 allowed fast vector, k = 7", "1059,431,808,1217,287,376,1188", WireJoin(seven));
            List<int> nine = BtWire.AllowedFastSet(IPAddress.Parse("80.4.4.200"), aa, 1313, 9);
            T.Eq("wire: BEP 6 allowed fast vector, k = 9", "1059,431,808,1217,287,376,1188,353,508", WireJoin(nine));
            T.Eq("wire: allowed fast needs IPv4", 0, BtWire.AllowedFastSet(IPAddress.IPv6Loopback, aa, 1313, 7).Count);
        }

        private static byte[] WireReserved(byte[] handshake)
        {
            byte[] r = new byte[8];
            Buffer.BlockCopy(handshake, 20, r, 0, 8);
            return r;
        }

        private static string WireJoin(List<int> list)
        {
            StringBuilder sb = new StringBuilder();
            foreach (int x in list) { if (sb.Length > 0) sb.Append(','); sb.Append(x); }
            return sb.ToString();
        }

        private static void WireExtHandshakeCases()
        {
            byte[] raw = BtExtHandshake.Build(new List<string> { null, "ut_metadata", "wpc_x" }, 6881, IPAddress.Parse("10.1.2.3"), null);
            BtExtHandshake h = BtExtHandshake.Parse(raw, 0, raw.Length);
            T.Check("wire: ext handshake ids = factory index + 1, a null slot keeps its number",
                    h != null && !h.M.ContainsKey("") && h.M.Count == 2 && h.M["ut_metadata"] == 2 && h.M["wpc_x"] == 3);
            T.Check("wire: ext handshake carries p, reqq 250, v and yourip",
                    h != null && h.ListenPort == 6881 && h.ReqQ == 250 && h.Client == BtContext.ClientName && IPAddress.Parse("10.1.2.3").Equals(h.YourIp));

            BVal root = BVal.NewDict();
            BVal m = BVal.NewDict();
            m.Set("ut_pex", BVal.Int(0));
            m.Set("huge", BVal.Int(100000));
            root.Set("m", m);
            root.Set("reqq", BVal.Int(1000000));
            root.Set("v", BVal.Str("evil\r\nclient" + new string('x', 200)));
            root.Set("p", BVal.Int(70000));
            byte[] hostile = Bencode.Encode(root);
            BtExtHandshake e = BtExtHandshake.Parse(hostile, 0, hostile.Length);
            T.Check("wire: hostile ext handshake — id 0 disables, big ids and ports dropped, reqq capped, client name cleaned",
                    e != null && e.Disabled.Contains("ut_pex") && !e.M.ContainsKey("huge") && e.ListenPort == 0 && e.ReqQ == 2000
                    && e.Client.Length <= 64 && e.Client.IndexOf('\n') < 0);
            T.Check("wire: ext handshake that is not a dictionary is refused", BtExtHandshake.Parse(new byte[] { (byte)'i', (byte)'1', (byte)'e' }, 0, 3) == null);
            T.Check("wire: ext handshake over 64 KiB is refused", BtExtHandshake.Parse(new byte[70000], 0, 70000) == null);
        }

        // ================================================================== //
        //  Выбор кусков
        // ================================================================== //
        private sealed class WirePickPeer : IBtPickPeer
        {
            public BtBitfield Bits;
            public bool Seed, Choked;
            public readonly HashSet<int> Allowed = new HashSet<int>();
            private readonly string _key;
            public WirePickPeer(string key, BtBitfield bits, bool seed) { _key = key; Bits = bits; Seed = seed; }
            public bool HasPiece(int piece) { return Seed || (Bits != null && Bits[piece]); }
            public bool CanRequest(int piece) { return HasPiece(piece) && (!Choked || Allowed.Contains(piece)); }
            public string BanKey { get { return _key; } }
        }

        private static BtBitfield WireBits(int count, params int[] set)
        {
            BtBitfield b = new BtBitfield(count);
            foreach (int i in set) b[i] = true;
            return b;
        }

        private static void WirePickerCases()
        {
            const int B = BtMeta.BlockSize;
            BtMeta one = WireMeta(new List<BtFxFile> { new BtFxFile(BtFx.Data(8 * B, 1)) }, B, 1);

            // Редкие первыми, но первый и последний кусок файла — раньше всех.
            BtPicker pk = new BtPicker(one, new BtBitfield(8));
            pk.AddPeerBits(WireBits(8, 0, 1, 2, 3, 4, 5, 6, 7), false);
            pk.AddPeerBits(WireBits(8, 1, 2, 4, 5, 6), false);
            pk.AddPeerBits(WireBits(8, 5), false);
            pk.BuildOrder(0);
            List<int> order = pk.OrderPreview(8);
            bool edgesFirst = order.Count == 8 && ((order[0] == 0 && order[1] == 7) || (order[0] == 7 && order[1] == 0));
            T.Check("picker: file edges first, then rarest (3), most common (5) last", edgesFirst && order[2] == 3 && order[7] == 5, WireJoin(order));

            pk.Sequential = true;
            pk.BuildOrder(0);
            T.Eq("picker: sequential keeps edges first, then index order", "0,7,1,2,3,4,5,6", WireJoin(pk.OrderPreview(8)));

            // Приоритеты: файл 0 — не качать, 2 — высокий.
            BtMeta three = WireMeta(new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(2 * B, 2), "a.bin"), new BtFxFile(BtFx.Data(2 * B, 3), "b.bin"), new BtFxFile(BtFx.Data(2 * B, 4), "c.bin")
            }, B, 1);
            BtPicker pp = new BtPicker(three, new BtBitfield(6));
            pp.SetPriorities(new[] { 0, 1, 2 });
            pp.BuildOrder(0);
            List<int> po = pp.OrderPreview(10);
            int fa = three.Files[0].FirstPiece, fc = three.Files[2].FirstPiece;
            T.Check("picker: priority 2 before 1, priority 0 never ordered",
                    po.Count == 4 && po[0] >= fc && po[1] >= fc && po[2] < fc && po[3] < fc && !po.Contains(fa) && !pp.Wants(fa) && pp.WantedMissing == 4, WireJoin(po));
            List<BtBlockReq> got = new List<BtBlockReq>();
            pp.Pick(new WirePickPeer("s", null, true), 50, 0, got);
            bool noSkipped = got.Count == 4;
            foreach (BtBlockReq r in got) if (r.Piece < three.Files[1].FirstPiece) noSkipped = false;
            T.Check("picker: a seed gets requests only for wanted pieces", noSkipped, got.Count + " requests");

            // Файл выбрали, когда порядок уже построен: следующий выбор его куски видит (порядок перестраивается сразу).
            pp.SetPriorities(new[] { 1, 1, 2 });
            got.Clear();
            pp.Pick(new WirePickPeer("s2", null, true), 50, 5000, got);
            bool newFile = false;
            foreach (BtBlockReq r in got) if (r.Piece >= fa && r.Piece < three.Files[1].FirstPiece) newFile = true;
            T.Check("picker: a file selected after the order was built is requested on the next pick", newFile && pp.WantedMissing == 6, got.Count + " requests");

            // Задушены: только allowed fast.
            BtPicker pf = new BtPicker(one, new BtBitfield(8));
            WirePickPeer choked = new WirePickPeer("c", null, true);
            choked.Choked = true;
            choked.Allowed.Add(5);
            pf.AddPeerBits(null, true);
            got.Clear();
            pf.Pick(choked, 10, 0, got);
            T.Check("picker: choked peer is asked only for its allowed fast pieces", got.Count == 1 && got[0].Piece == 5 && got[0].Length == B);

            // Эндшпиль: второй запрос того же блока и отмена у другого пира.
            BtMeta two = WireMeta(new List<BtFxFile> { new BtFxFile(BtFx.Data(2 * B, 5)) }, B, 1);
            BtPicker pe = new BtPicker(two, new BtBitfield(2));
            WirePickPeer a = new WirePickPeer("10.0.0.1", null, true), b = new WirePickPeer("10.0.0.2", null, true), c = new WirePickPeer("10.0.0.3", null, true);
            List<BtBlockReq> ra = new List<BtBlockReq>(), rb = new List<BtBlockReq>(), rc = new List<BtBlockReq>(), again = new List<BtBlockReq>();
            pe.Pick(a, 10, 0, ra);
            bool endgame = pe.IsEndgame();
            pe.Pick(b, 10, 0, rb);
            pe.Pick(a, 10, 0, again);
            pe.Pick(c, 10, 0, rc);
            T.Check("picker: endgame duplicates each block once for another peer, never for the same one or a third",
                    ra.Count == 2 && endgame && rb.Count == 2 && again.Count == 0 && rc.Count == 0, ra.Count + "/" + rb.Count + "/" + again.Count + "/" + rc.Count);
            List<KeyValuePair<IBtPickPeer, BtBlockReq>> cancels = new List<KeyValuePair<IBtPickPeer, BtBlockReq>>();
            BtBlockResult first = pe.OnBlock(a, 0, 0, B, cancels);
            BtBlockResult dup = pe.OnBlock(b, 0, 0, B, null);
            T.Check("picker: the first copy completes the piece and cancels the other requester, the second copy is a duplicate",
                    first == BtBlockResult.PieceComplete && cancels.Count == 1 && cancels[0].Key == b && dup == BtBlockResult.Duplicate);
            T.Check("picker: a block off the 16 KiB grid or of wrong length is invalid",
                    pe.OnBlock(a, 1, 1, B, null) == BtBlockResult.Invalid && pe.OnBlock(a, 1, 0, B - 1, null) == BtBlockResult.Invalid && pe.OnBlock(a, 9, 0, B, null) == BtBlockResult.Invalid);

            // Испорченный кусок: вернуть авторов, кусок снова в выборе.
            BtMeta big = WireMeta(new List<BtFxFile> { new BtFxFile(BtFx.Data(2 * B, 6)) }, 2 * B, 1);
            BtPicker ph = new BtPicker(big, new BtBitfield(1));
            WirePickPeer x = new WirePickPeer("10.0.0.7", null, true), y = new WirePickPeer("10.0.0.8", null, true);
            ph.OnBlock(x, 0, 0, B, null);
            BtBlockResult last = ph.OnBlock(y, 0, B, B, null);
            List<string> from = ph.PieceChecked(0, false);
            got.Clear();
            ph.Pick(x, 10, 0, got);
            T.Check("picker: failed piece reports every contributor and becomes requestable again",
                    last == BtBlockResult.PieceComplete && from.Count == 2 && from.Contains("10.0.0.7") && from.Contains("10.0.0.8") && got.Count == 2 && ph.WantedMissing == 1);
        }

        // ================================================================== //
        //  Слоты
        // ================================================================== //
        private static BtChokeInfo WireCi(string key, bool interested, long down, long up)
        {
            BtChokeInfo i = new BtChokeInfo();
            i.Key = key;
            i.Interested = interested;
            i.DownBps = down;
            i.UpBps = up;
            return i;
        }

        private static string WireUnchoked(List<BtChokeInfo> peers, bool optimistic)
        {
            List<string> keys = new List<string>();
            foreach (BtChokeInfo p in peers) if (p.Unchoke && p.OptimisticNext == optimistic) keys.Add((string)p.Key);
            keys.Sort(StringComparer.Ordinal);
            return string.Join(",", keys.ToArray());
        }

        private static void WireChokerCases()
        {
            const long Now = 1000000;
            List<BtChokeInfo> leech = new List<BtChokeInfo>();
            for (int i = 0; i < 6; i++) leech.Add(WireCi("p" + i, true, 100 * (i + 1), 0));
            BtChokeInfo snub = WireCi("snub", true, 9000, 0);
            snub.Snubbed = true;
            leech.Add(snub);
            leech.Add(WireCi("idle", false, 50000, 0));
            BtChokeInfo seed = WireCi("seed", true, 0, 0);
            seed.IsSeed = true;
            leech.Add(seed);
            BtChoker.Decide(leech, 4, false, Now, true, new Random(1));
            string opt = WireUnchoked(leech, true);
            T.Eq("choker: leeching — 4 fastest uploaders to us, snubbed, uninterested and seeds get no regular slot", "p2,p3,p4,p5", WireUnchoked(leech, false));
            T.Check("choker: leeching — exactly one optimistic slot among the waiting", opt == "p0" || opt == "p1" || opt == "snub", opt);

            foreach (BtChokeInfo p in leech)
            {
                p.Unchoked = p.Unchoke;
                p.Optimistic = p.OptimisticNext;
                if (p.Unchoke) p.UnchokedAtMs = Now;
            }
            BtChoker.Decide(leech, 4, false, Now + 10000, false, new Random(2));
            T.Eq("choker: optimistic slot holds until rotation", opt, WireUnchoked(leech, true));

            List<BtChokeInfo> seeding = new List<BtChokeInfo>();
            BtChokeInfo fresh = WireCi("fresh", true, 0, 10), expired = WireCi("expired", true, 0, 5000), never = WireCi("never", true, 0, 0), waited = WireCi("waited", true, 0, 0);
            fresh.Unchoked = true;
            fresh.UnchokedAtMs = Now - 5000;
            expired.Unchoked = true;
            expired.UnchokedAtMs = Now - 40000;
            waited.UnchokedAtMs = Now - 20000;
            seeding.Add(fresh);
            seeding.Add(expired);
            seeding.Add(never);
            seeding.Add(waited);
            BtChoker.Decide(seeding, 2, true, Now, true, new Random(3));
            T.Check("choker: seeding round-robin — a fresh slot stays, the longest waiter enters, the expired slot yields",
                    WireUnchoked(seeding, false) == "fresh,never" && WireUnchoked(seeding, true) == "waited" && !expired.Unchoke,
                    WireUnchoked(seeding, false) + " / " + WireUnchoked(seeding, true));
        }

        // ================================================================== //
        //  Настоящий путь: сессии на петле
        // ================================================================== //
        private static void WireTransferV1ThreeSessions()
        {
            List<BtFxFile> files = new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(120000, 71), "dir", "one.bin"),
                new BtFxFile(BtFx.Data(70001, 72), "two.bin"),
                new BtFxFile(BtFx.Data(250000, 73), "dir", "sub", "three.bin")
            };
            BtMeta meta = WireMeta(files, 32768, 1);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-v1");
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s = WireSession(BtEncryption.Off), l1 = WireSession(BtEncryption.Off), l2 = WireSession(BtEncryption.Off);
                sessions.AddRange(new[] { s, l1, l2 });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                string d1 = Fx.MakeDir(root, "l1"), d2 = Fx.MakeDir(root, "l2");
                BtTorrent t1 = WireAdd(l1, meta, d1, null), t2 = WireAdd(l2, meta, d2, null);
                int completed = 0;
                t1.Completed = delegate { Interlocked.Increment(ref completed); };
                bool seeding = WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding; }, 5000);
                T.Check("session: a seed with all files on disk reaches Seeding after the check", seeding, WireState(ts));
                t1.AddPeer(WireEp(s));
                t2.AddPeer(WireEp(l1));                // второй качальщик знает только первого
                bool done = WireWaitFor(delegate { return t1.State == BtTorrentState.Seeding && t2.State == BtTorrentState.Seeding; }, 20000);
                string info1, info2;
                bool same1 = WireSameFiles(meta, files, d1, out info1), same2 = WireSameFiles(meta, files, d2, out info2);
                T.Check("session: v1 multi-file — both leechers complete, SHA-256 equal, no .wpcpart", done && same1 && same2,
                        WireState(t1) + " | " + WireState(t2) + " | " + info1 + " " + info2);
                T.Check("session: the middle leecher uploaded verified pieces to the third session", t1.Stats().Uploaded > 0 && t2.Stats().Downloaded >= meta.TotalSize,
                        t1.Stats().Uploaded + " up, " + t2.Stats().Downloaded + " down");
                T.Check("session: Completed raised once on the thread pool", WireWaitFor(delegate { return completed == 1; }, 2000), completed.ToString());
            }
            finally { WireDispose(sessions); }
        }

        private static void WireTransferV2(int mode, string label)
        {
            List<BtFxFile> files = new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(100000, 81 + mode), "a.bin"),
                new BtFxFile(BtFx.Data(40000, 91 + mode), "b", "c.bin"),
                new BtFxFile(BtFx.Data(70000, 97 + mode), "d.bin")
            };
            BtMeta meta = WireMeta(files, 32768, mode);
            string root = Fx.MakeDir(Fx.Root, "bt-wire-v" + mode);
            List<BtSession> sessions = new List<BtSession>();
            try
            {
                BtSession s = WireSession(BtEncryption.Off), l = WireSession(BtEncryption.Off);
                sessions.AddRange(new[] { s, l });
                BtTorrent ts = WireAddSeed(s, meta, files, Fx.MakeDir(root, "seed"));
                string dl = Fx.MakeDir(root, "leech");
                BtTorrent tl = WireAdd(l, meta, dl, null);
                WireWaitFor(delegate { return ts.State == BtTorrentState.Seeding && tl.State == BtTorrentState.Downloading; }, 5000);
                tl.AddPeer(WireEp(s));
                bool done = WireWaitFor(delegate { return tl.State == BtTorrentState.Seeding; }, 15000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                T.Check("session: " + label + " transfer — complete, SHA-256 equal, no .wpcpart", done && same, WireState(ts) + " | " + WireState(tl) + " | " + info);
            }
            finally { WireDispose(sessions); }
        }

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
                string py = Environment.GetEnvironmentVariable("WPC_LT_PYTHON");
                if (string.IsNullOrEmpty(py) || !File.Exists(py)) { why = "WPC_LT_PYTHON not set"; return null; }
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
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPC_LT_PYTHON"))) { T.Skip(Name, "WPC_LT_PYTHON not set"); return; }
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
