// SysDeck — область «torrent», часть соединений: кодек протокола пиров, выбор кусков, раздача слотов,
// рой и сессия.
//
// Настоящий путь: несколько BtSession на 127.0.0.1 передают торренты друг другу через сокеты, реактор, диск и проверку
// хешей; «злой» пир — сырой сокет на 127.0.0.2. Независимая реализация — libtorrent (tests\bt-oracle\wire.py), если
// сборочный скрипт полосы нашёл python с ним (SYSDECK_LT_PYTHON); нет — пропуск. Интернет не трогается, всё — в фикстуре.

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
    }
}
