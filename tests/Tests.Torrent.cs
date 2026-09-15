// SysDeck — область «torrent»: основа торрент-клиента (bencode, метаданные v1/v2/гибрид, magnet, дерево
// хешей, хранилище на настоящей файловой системе, возобновление, очередь диска, UDP-сокет) и сборщик торрентов для
// тестов всех частей клиента.
//
// Части клиента подключают свои проверки partial-методами ниже: файл части есть в сборке — его тесты идут, нет —
// вызов исчезает при компиляции. Общий реестр областей (Tests.cs) при этом не трогается.
// Ненастоящая здесь только Корзина (подменный делегат) и сеть — петля 127.0.0.1. Все записи — внутри фикстуры.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal sealed class BtFxFile
    {
        public string[] Path;                // null — однофайловый торрент (имя = name)
        public byte[] Data;
        public BtFxFile(byte[] data, params string[] path) { Data = data; Path = path.Length == 0 ? null : path; }
    }

    // ------------------------------------------------------------------ //
    //  Сборщик .torrent: v1 (mode 1), чистый v2 (2), гибрид с заполнителями (3)
    // ------------------------------------------------------------------ //
    internal static class BtFx
    {
        public static byte[] Data(int length, int seed)
        {
            byte[] b = new byte[length];
            uint x = (uint)seed * 2654435761u + 1;
            for (int i = 0; i < length; i++)
            {
                x ^= x << 13; x ^= x >> 17; x ^= x << 5;
                b[i] = (byte)x;
            }
            return b;
        }

        public static byte[] Build(string name, IList<BtFxFile> files, int pieceLength, int mode, bool isPrivate, IList<IList<string>> trackers)
        {
            BVal info = BVal.NewDict();
            info.Set("name", BVal.Str(name));
            info.Set("piece length", BVal.Int(pieceLength));
            if (isPrivate) info.Set("private", BVal.Int(1));
            bool single = files.Count == 1 && files[0].Path == null;
            BVal root = BVal.NewDict();

            if (mode == 1 || mode == 3)
            {
                MemoryStream stream = new MemoryStream();
                BVal list = BVal.NewList();
                for (int i = 0; i < files.Count; i++)
                {
                    BtFxFile f = files[i];
                    stream.Write(f.Data, 0, f.Data.Length);
                    if (!single)
                    {
                        BVal e = BVal.NewDict().Set("length", BVal.Int(f.Data.Length)).Set("path", PathList(f.Path));
                        list.Add(e);
                    }
                    long rem = stream.Length % pieceLength;
                    if (mode == 3 && i < files.Count - 1 && rem != 0)
                    {
                        int pad = (int)(pieceLength - rem);
                        stream.Write(new byte[pad], 0, pad);
                        list.Add(BVal.NewDict().Set("attr", BVal.Str("p")).Set("length", BVal.Int(pad))
                                     .Set("path", PathList(new[] { ".pad", pad.ToString() })));
                    }
                }
                if (single) info.Set("length", BVal.Int(files[0].Data.Length));
                else info.Set("files", list);
                byte[] all = stream.ToArray();
                MemoryStream pieces = new MemoryStream();
                using (SHA1 sha = SHA1.Create())
                    for (int pos = 0; pos < all.Length; pos += pieceLength)
                    {
                        byte[] h = sha.ComputeHash(all, pos, Math.Min(pieceLength, all.Length - pos));
                        pieces.Write(h, 0, 20);
                    }
                info.Set("pieces", BVal.Bytes(pieces.ToArray()));
            }

            if (mode == 2 || mode == 3)
            {
                info.Set("meta version", BVal.Int(2));
                BVal tree = BVal.NewDict();
                BVal layers = BVal.NewDict();
                foreach (BtFxFile f in files)
                {
                    string[] path = f.Path ?? new[] { name };
                    BVal node = tree;
                    foreach (string p in path)
                    {
                        BVal next = node.Get(p);
                        if (next == null) { next = BVal.NewDict(); node.Set(p, next); }
                        node = next;
                    }
                    BVal leaf = BVal.NewDict().Set("length", BVal.Int(f.Data.Length));
                    if (f.Data.Length > 0)
                    {
                        byte[] rootHash;
                        if (f.Data.Length <= pieceLength)
                            rootHash = BtMerkle.Root(f.Data, 0, f.Data.Length, BtMerkle.NextPow2((f.Data.Length + BtMeta.BlockSize - 1) / BtMeta.BlockSize));
                        else
                        {
                            MemoryStream layer = new MemoryStream();
                            int count = 0;
                            for (int pos = 0; pos < f.Data.Length; pos += pieceLength, count++)
                            {
                                byte[] h = BtMerkle.Root(f.Data, pos, Math.Min(pieceLength, f.Data.Length - pos), pieceLength / BtMeta.BlockSize);
                                layer.Write(h, 0, 32);
                            }
                            rootHash = BtMerkle.RootFromLayer(layer.ToArray(), count, pieceLength);
                            layers.Set(rootHash, BVal.Bytes(layer.ToArray()));
                        }
                        leaf.Set("pieces root", BVal.Bytes(rootHash));
                    }
                    node.Set("", leaf);
                }
                info.Set("file tree", tree);
                root.Set("piece layers", layers);
            }

            root.Set("info", info);
            root.Set("comment", BVal.Str("https://example.org/topic?t=1"));
            if (trackers != null && trackers.Count > 0)
            {
                root.Set("announce", BVal.Str(trackers[0][0]));
                BVal tiers = BVal.NewList();
                foreach (IList<string> tier in trackers)
                {
                    BVal t = BVal.NewList();
                    foreach (string u in tier) t.Add(BVal.Str(u));
                    tiers.Add(t);
                }
                root.Set("announce-list", tiers);
            }
            return Bencode.Encode(root);
        }

        private static BVal PathList(string[] path)
        {
            BVal l = BVal.NewList();
            foreach (string p in path) l.Add(BVal.Str(p));
            return l;
        }

        // Все куски торрента из исходных файлов — как их отдал бы сид.
        public static byte[] PieceBytes(BtMeta meta, IList<BtFxFile> files, int piece)
        {
            byte[] buf = new byte[meta.PieceSize(piece)];
            long start = meta.PieceStart(piece);
            int k = 0;
            for (int i = 0; i < meta.Files.Count; i++)
            {
                BtFile f = meta.Files[i];
                byte[] data = f.Pad ? new byte[f.Length] : files[k++].Data;
                long from = Math.Max(start, f.Offset), to = Math.Min(start + buf.Length, f.End);
                if (from < to) Buffer.BlockCopy(data, (int)(from - f.Offset), buf, (int)(from - start), (int)(to - from));
            }
            return buf;
        }
    }

    internal static partial class TorrentTests
    {
        internal static void Run()
        {
            BencodeCases();
            MetaCases();
            MagnetCases();
            MerkleCases();
            StorageCases();
            ResumeAndDiskCases();
            NetBasics();
            RunTrackers();
            RunWire();
            RunDht();
            RunIntegration();
            RunSession();
            RunEngine();
            RunUpdate();
        }

        static partial void RunTrackers();   // Tests.TorrentTrackers.cs — трекеры, ut_metadata, LSD
        static partial void RunWire();       // Tests.TorrentWire.cs — соединения, куски, раздача, сессия
        static partial void RunDht();        // Tests.TorrentDht.cs — DHT, PEX, MSE, проброс порта
        static partial void RunIntegration(); // Tests.TorrentIntegration.cs — брандмауэр, привязка файлов, движок загрузок
        static partial void RunSession();    // Tests.TorrentSession.cs — собранный клиент: трекер, ut_metadata, MSE, PEX, DHT
        static partial void RunEngine();     // Tests.TorrentEngine.cs — движок загрузок: торренты в очереди, пауза, падение, удаление
        static partial void RunUpdate();     // Tests.TorrentUpdate.cs — «Обновить раздачу»: план, шаги на диске, движок

        private static byte[] A(string s) { return Encoding.ASCII.GetBytes(s); }

        // ---------- bencode ----------
        private static void BencodeCases()
        {
            string err;
            BVal v = Bencode.Decode(A("d3:cow3:moo4:spaml1:a1:bee"), out err);
            T.Check("bencode: dictionary with a list decodes", v != null && v.GetStr("cow") == "moo" && v.Get("spam", BKind.List).L.Count == 2, err);
            T.Eq("bencode: canonical re-encode is byte-identical", "d3:cow3:moo4:spaml1:a1:bee", Encoding.ASCII.GetString(Bencode.Encode(v)));
            BVal unsorted = BVal.NewDict().Set("zz", BVal.Int(1)).Set("a", BVal.Int(-7)).Set(new byte[] { 0xFF, 0 }, BVal.Str(""));
            byte[] sortedExpected = Encoding.ASCII.GetBytes("d1:ai-7e2:zzi1e2:XX0:e");
            sortedExpected[17] = 0xFF; sortedExpected[18] = 0;
            T.Check("bencode: encode sorts keys by raw bytes", Bencode.SameBytes(Bencode.Encode(unsorted), sortedExpected));
            foreach (string bad in new[] { "i03e", "i-0e", "ie", "i12", "5:abc", "d1:ai1e", "di1ei2ee", "l", "x", "i1ei2e", "-1:a", "i9223372036854775808e" })
                T.Check("bencode: rejects malformed '" + bad + "'", Bencode.Decode(A(bad), out err) == null);
            T.Check("bencode: 64 nested lists accepted", Bencode.Decode(A(new string('l', 64) + new string('e', 64)), out err) != null, err);
            T.Check("bencode: 66 nested lists refused (stack guard)", Bencode.Decode(A(new string('l', 66) + new string('e', 66)), out err) == null);
            int consumed;
            byte[] prefix = A("d8:msg_typei1e5:piecei0eeRAWDATA");
            BVal p = Bencode.DecodePrefix(prefix, 0, prefix.Length, out consumed, out err);
            T.Check("bencode: prefix decode stops at the dictionary end (ut_metadata layout)", p != null && consumed == prefix.Length - 7, "consumed=" + consumed);
            BVal nested = Bencode.Decode(A("d1:ad1:bi5eee"), out err);
            BVal inner = nested.Get("a");
            T.Check("bencode: element spans point at its own bytes", inner.Start == 4 && inner.End == 12, inner.Start + ".." + inner.End);
            BVal big = Bencode.Decode(A("i-9223372036854775807e"), out err);
            T.Check("bencode: long min+1 parses", big != null && big.I == -9223372036854775807L, err);
        }

        // ---------- метаданные ----------
        private static void MetaCases()
        {
            string err;
            List<BtFxFile> one = new List<BtFxFile> { new BtFxFile(BtFx.Data(100000, 1)) };
            IList<IList<string>> trackers = new List<IList<string>>
            {
                new List<string> { "http://t1.example/announce", "udp://t2.example:80", "ftp://bad.example/x", "http://t1.example/announce" },
                new List<string> { "https://t3.example/ann?pk=secret" }
            };
            byte[] t1 = BtFx.Build("single.bin", one, 32768, 1, true, trackers);
            BtMeta m = BtMeta.Parse(t1, out err);
            T.Check("meta v1 single: parses", m != null, err);
            if (m == null) return;
            T.Eq("meta v1 single: piece count", 4, m.PieceCount);
            T.Eq("meta v1 single: last piece size", 100000 - 3 * 32768, m.PieceSize(3));
            T.Check("meta v1 single: private flag", m.Private);
            T.Eq("meta v1 single: tiers — ftp and duplicates dropped", "2|1", m.Trackers[0].Count + "|" + m.Trackers[1].Count);
            T.Eq("meta v1 single: one file named by name", "single.bin", m.Files[0].RelPath);
            T.Check("meta v1 single: comment kept for «Обновить раздачу»", m.Comment == "https://example.org/topic?t=1");

            // info-hash — по исходным байтам словаря, даже если ключи не по порядку.
            byte[] pieces20 = new byte[20];
            string raw = "d4:infod4:name1:a6:lengthi3e12:piece lengthi16384e6:pieces20:" + new string('\0', 20) + "ee";
            byte[] rawBytes = Encoding.GetEncoding(28591).GetBytes(raw);
            BtMeta nc = BtMeta.Parse(rawBytes, out err);
            byte[] infoSlice = new byte[rawBytes.Length - 8];
            Buffer.BlockCopy(rawBytes, 7, infoSlice, 0, infoSlice.Length);
            byte[] expected;
            using (SHA1 sha = SHA1.Create()) expected = sha.ComputeHash(infoSlice);
            T.Check("meta: info-hash over the original (non-canonical) info bytes", nc != null && Bencode.SameBytes(nc.InfoHash, expected), err);
            T.Check("meta: re-encoded info would hash differently (the case is real)",
                    nc != null && !Bencode.SameBytes(nc.InfoHash, SHA1.Create().ComputeHash(Bencode.Encode(Bencode.Decode(infoSlice, out err)))));

            // Враждебные пути.
            List<BtFxFile> evil = new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(10, 2), "..", "..", "evil.exe"),
                new BtFxFile(BtFx.Data(10, 3), "a:b", "c"),
                new BtFxFile(BtFx.Data(10, 4), "CON"),
                new BtFxFile(BtFx.Data(10, 5), "x/y\\z"),
                new BtFxFile(BtFx.Data(10, 6), "gpj.‮exe"),
                new BtFxFile(BtFx.Data(10, 7), "Same.txt"),
                new BtFxFile(BtFx.Data(10, 8), "same.txt")
            };
            BtMeta me = BtMeta.Parse(BtFx.Build("..", evil, 16384, 1, false, null), out err);
            T.Check("meta hostile paths: parses", me != null, err);
            if (me != null)
            {
                bool clean = true;
                foreach (BtFile f in me.Files)
                    foreach (string part in f.RelPath.Split('\\'))
                        if (part == ".." || part == "." || part.IndexOf(':') >= 0 || part.IndexOf('/') >= 0 || part.IndexOf('‮') >= 0) clean = false;
                T.Check("meta hostile paths: no '..', ':', '/', bidi in any element", clean, string.Join(" | ", Paths(me)));
                T.Check("meta hostile paths: root dir '..' replaced", me.RootDir != ".." && me.RootDir.Length > 0, me.RootDir);
                T.Eq("meta hostile paths: reserved name CON prefixed", "_CON", me.Files[2].RelPath);
                T.Eq("meta hostile paths: case-insensitive duplicate gets (1)", "same (1).txt", me.Files[6].RelPath);
            }

            // Гибрид: заполнители, корни v2 на файлах v1.
            List<BtFxFile> multi = new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(40000, 11), "dir", "a.bin"),
                new BtFxFile(BtFx.Data(70000, 12), "b.bin"),
                new BtFxFile(BtFx.Data(5, 13), "dir", "c.txt")
            };
            BtMeta h = BtMeta.Parse(BtFx.Build("multi", multi, 32768, 3, false, null), out err);
            T.Check("meta hybrid: parses as version 3", h != null && h.Version == 3, err);
            if (h != null)
            {
                int pads = 0, roots = 0;
                foreach (BtFile f in h.Files) { if (f.Pad) pads++; if (f.Root != null) roots++; }
                T.Eq("meta hybrid: pad files between files", 2, pads);
                T.Eq("meta hybrid: every real file got its v2 root", 3, roots);
                T.Eq("meta hybrid: total size excludes pads", 110005L, h.TotalSize);
                T.Check("meta hybrid: files start on piece boundaries", h.Files[2].Offset % 32768 == 0 && h.Files[4].Offset % 32768 == 0);
                T.Check("meta hybrid: SwarmHash is the v1 SHA-1", Bencode.SameBytes(h.SwarmHash, h.InfoHash));
            }

            // Чистый v2.
            BtMeta v2 = BtMeta.Parse(BtFx.Build("pure", multi, 32768, 2, false, null), out err);
            T.Check("meta v2: parses as version 2 with piece layers", v2 != null && v2.Version == 2 && v2.PieceLayers.Count == 2, err);
            if (v2 != null)
            {
                T.Eq("meta v2: pieces = per-file ceil (2 + 3 + 1)", 6, v2.PieceCount);
                T.Eq("meta v2: last piece of the first file (b.bin) is short", 70000 - 2 * 32768, v2.PieceSize(2));
                T.Eq("meta v2: last piece of dir/a.bin is short, no bytes of the next file", 40000 - 32768, v2.PieceSize(4));
                T.Check("meta v2: SwarmHash = truncated SHA-256", v2.InfoHash == null && v2.SwarmHash.Length == 20);
                // v2 порядок файлов — по ключам дерева: "b.bin" < "dir".
                T.Eq("meta v2: file order follows the tree", "b.bin", v2.Files[0].RelPath);
                byte[] good = BtFx.PieceBytes(v2, new List<BtFxFile> { multi[1], multi[0], multi[2] }, 3);
                T.Check("meta v2: correct piece verifies by merkle", v2.CheckPieceV2(3, good, good.Length));
                good[100] ^= 1;
                T.Check("meta v2: flipped byte fails merkle", !v2.CheckPieceV2(3, good, good.Length));
            }
            byte[] v2bytes = BtFx.Build("pure", multi, 32768, 2, false, null);
            BVal broken = Bencode.Decode(v2bytes, out err);
            foreach (KeyValuePair<byte[], BVal> kv in broken.Get("piece layers").D) { kv.Value.B[5] ^= 0x40; break; }
            T.Check("meta v2: piece layer that does not fold into the file root is refused", BtMeta.Parse(Bencode.Encode(broken), out err) == null && err.Contains("root"), err);

            BtMeta fromInfo = BtMeta.FromInfo(m.InfoBytes, m.InfoHash, out err);
            T.Check("meta: FromInfo accepts matching hash", fromInfo != null, err);
            byte[] wrong = (byte[])m.InfoHash.Clone();
            wrong[0] ^= 1;
            T.Check("meta: FromInfo refuses a different info-hash", BtMeta.FromInfo(m.InfoBytes, wrong, out err) == null);
            BtMeta rebuilt = BtMeta.Parse(fromInfo.BuildTorrentFile(), out err);
            T.Check("meta: .torrent rebuilt from magnet metadata keeps the info-hash", rebuilt != null && Bencode.SameBytes(rebuilt.InfoHash, m.InfoHash), err);
            T.Check("meta: refuses piece count mismatch", BtMeta.Parse(Encoding.GetEncoding(28591).GetBytes(
                "d4:infod6:lengthi40000e4:name1:a12:piece lengthi16384e6:pieces20:" + new string('\0', 20) + "ee"), out err) == null);
        }

        private static List<string> Paths(BtMeta m)
        {
            List<string> l = new List<string>();
            foreach (BtFile f in m.Files) l.Add(f.RelPath);
            return l;
        }

        // ---------- magnet ----------
        private static void MagnetCases()
        {
            string err;
            BtMagnet a = BtMagnet.Parse("magnet:?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a&dn=Some+Name&tr=udp%3A%2F%2Ft.example%3A6969&tr.1=http%3A%2F%2Fx.example%2Fa%2Bb&x.pe=1.2.3.4:5", out err);
            T.Check("magnet: hex btih", a != null && Bencode.Hex(a.InfoHash) == "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", err);
            if (a != null)
            {
                T.Eq("magnet: dn '+' is a space", "Some Name", a.Name);
                T.Eq("magnet: tr and tr.1 decoded, '+' in a URL kept", "udp://t.example:6969|http://x.example/a+b", string.Join("|", a.Trackers.ToArray()));
                T.Eq("magnet: x.pe peer", "1.2.3.4:5", a.Peers.Count == 1 ? a.Peers[0] : "");
            }
            BtMagnet b32 = BtMagnet.Parse("magnet:?xt=urn:btih:YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK", out err);
            T.Check("magnet: base32 btih equals the hex form", b32 != null && Bencode.Hex(b32.InfoHash) == "c12fe1c06bba254a9dc9f519b335aa7c1367a88a", err);
            BtMagnet mh = BtMagnet.Parse("magnet:?xt=urn:btmh:1220" + new string('a', 64), out err);
            T.Check("magnet: btmh sha2-256 → v2 hash, swarm hash truncated", mh != null && mh.InfoHashV2.Length == 32 && mh.SwarmHash.Length == 20, err);
            T.Check("magnet: no xt refused", BtMagnet.Parse("magnet:?dn=x", out err) == null);
            T.Check("magnet: bad hash refused", BtMagnet.Parse("magnet:?xt=urn:btih:zz", out err) == null);
            T.Check("magnet: other scheme refused", BtMagnet.Parse("http://x/?xt=urn:btih:c12fe1c06bba254a9dc9f519b335aa7c1367a88a", out err) == null);
            BtMagnet round = a == null ? null : BtMagnet.Parse(a.ToLink(), out err);
            T.Check("magnet: ToLink round-trips hash, name and trackers", round != null && Bencode.SameBytes(round.InfoHash, a.InfoHash)
                    && round.Name == a.Name && round.Trackers.Count == 2, err);
        }

        // ---------- дерево хешей: значения посчитаны по тексту BEP 52 вручную ----------
        private static void MerkleCases()
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] data = BtFx.Data(3 * 16384 - 100, 21);
                byte[] h0 = sha.ComputeHash(data, 0, 16384), h1 = sha.ComputeHash(data, 16384, 16384), h2 = sha.ComputeHash(data, 32768, data.Length - 32768);
                byte[] zero = new byte[32];
                byte[] expected = sha.ComputeHash(Cat(sha.ComputeHash(Cat(h0, h1)), sha.ComputeHash(Cat(h2, zero))));
                T.Check("merkle: 3 blocks → 4 leaves, missing leaf is 32 zero bytes (not a hash of zeros)",
                        Bencode.SameBytes(BtMerkle.Root(data, 0, data.Length, 4), expected));
                byte[] p0 = sha.ComputeHash(new byte[] { 1 }), p1 = sha.ComputeHash(new byte[] { 2 }), p2 = sha.ComputeHash(new byte[] { 3 });
                byte[] padPiece = sha.ComputeHash(Cat(zero, zero));   // куску 32 КиБ = 2 листа
                byte[] layer = Cat(Cat(p0, p1), p2);
                byte[] exp2 = sha.ComputeHash(Cat(sha.ComputeHash(Cat(p0, p1)), sha.ComputeHash(Cat(p2, padPiece))));
                T.Check("merkle: layer padding uses the root of an all-zero piece subtree",
                        Bencode.SameBytes(BtMerkle.RootFromLayer(layer, 3, 32768), exp2));
            }
        }

        private static byte[] Cat(byte[] a, byte[] b)
        {
            byte[] c = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, c, 0, a.Length);
            Buffer.BlockCopy(b, 0, c, a.Length, b.Length);
            return c;
        }

        // ---------- хранилище: настоящая файловая система ----------
        private static void StorageCases()
        {
            string err;
            string dir = Fx.MakeDir(Fx.Root, "bt-storage");
            List<BtFxFile> files = new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(50000, 31), "sub", "one.bin"),
                new BtFxFile(BtFx.Data(20000, 32), "two.bin"),
                new BtFxFile(BtFx.Data(90001, 33), "sub", "deep", "three.bin")
            };
            BtMeta meta = BtMeta.Parse(BtFx.Build("pack", files, 32768, 1, false, null), out err);
            if (meta == null) { T.Check("storage: fixture torrent parses", false, err); return; }
            BtStorage st = new BtStorage(meta, dir, "pack");
            T.Check("storage: validate passes for a clean layout", st.Validate() == null, st.Validate());
            BtBitfield have = new BtBitfield(meta.PieceCount);
            byte[] buf = new byte[meta.PieceLength];
            List<string> renameErrors = new List<string>();
            // Блоки в обратном порядке: запись далеко за концом файла, разреженность.
            for (int p = meta.PieceCount - 1; p >= 0; p--)
            {
                byte[] piece = BtFx.PieceBytes(meta, files, p);
                for (int at = piece.Length; at > 0; )
                {
                    int len = Math.Min(16384, at - (at - 1) / 16384 * 16384);
                    int begin = at - len;
                    string w = st.Write(p, begin, piece, begin, len);
                    if (w != null) renameErrors.Add(w);
                    at = begin;
                }
                if (p == 2) T.Check("storage: partial file keeps the .wpcpart name", File.Exists(st.PartPath(2)) && !File.Exists(st.FinalPath(2)));
                bool ok = st.CheckPiece(p, buf);
                if (!ok) renameErrors.Add("hash " + p);
                have[p] = ok;
            }
            for (int p = 0; p < meta.PieceCount; p++) renameErrors.AddRange(st.PieceVerified(p, have));
            T.Check("storage: all pieces written out of order verify, files renamed", renameErrors.Count == 0, string.Join("; ", renameErrors.ToArray()));
            bool same = true;
            for (int i = 0; i < files.Count; i++)
                if (!File.Exists(st.FinalPath(i)) || !Bencode.SameBytes(File.ReadAllBytes(st.FinalPath(i)), files[i].Data)) same = false;
            T.Check("storage: final files are byte-identical to the source", same);
            T.Check("storage: no .wpcpart left", Directory.GetFiles(Path.Combine(dir, "pack"), "*" + DlPaths.PartSuffix, SearchOption.AllDirectories).Length == 0);
            T.Check("storage: sparse flag cleared on completed files", (File.GetAttributes(st.FinalPath(2)) & FileAttributes.SparseFile) == 0);

            byte[] readBack = new byte[1000];
            string spanErr = st.Read(1, 50000 - 32768 - 500, readBack, 0, 1000);
            T.Check("storage: read spanning the boundary of two files", spanErr == null && readBack[0] == files[0].Data[49500] && readBack[500] == files[1].Data[0]
                    && readBack[999] == files[1].Data[499], spanErr);
            byte[] expect = BtFx.PieceBytes(meta, files, 1);
            byte[] got = new byte[expect.Length];
            T.Check("storage: whole piece read equals the source", st.Read(1, 0, got, 0, got.Length) == null && Bencode.SameBytes(got, expect));
            T.Check("storage: block beyond the piece refused", st.Read(0, 32000, got, 0, 1000) != null);
            st.Close();

            // Повреждение: recheck теряет ровно этот кусок.
            using (FileStream fs = new FileStream(st.FinalPath(2), FileMode.Open, FileAccess.ReadWrite))
            {
                fs.Position = 40000;
                int b = fs.ReadByte();
                fs.Position = 40000;
                fs.WriteByte((byte)(b ^ 0xFF));
            }
            BtStorage st2 = new BtStorage(meta, dir, "pack");
            BtBitfield rc = st2.Recheck(null, null);
            int piecesOf40000 = (int)((meta.Files[2].Offset + 40000) / meta.PieceLength);
            T.Check("storage: recheck misses exactly the corrupted piece", rc.SetCount == meta.PieceCount - 1 && !rc[piecesOf40000], rc.SetCount + "/" + meta.PieceCount);
            st2.Dispose();

            // Существующий файл под настоящим именем не перезаписывается.
            string dir2 = Fx.MakeDir(Fx.Root, "bt-taken");
            BtStorage st3 = new BtStorage(meta, dir2, "pack");
            File.WriteAllText(Fx.MakeFile(Path.Combine(Path.Combine(dir2, "pack"), "two.bin"), 1), "users own file");
            BtBitfield h3 = new BtBitfield(meta.PieceCount);
            for (int p = 0; p < meta.PieceCount; p++)
            {
                byte[] piece = BtFx.PieceBytes(meta, files, p);
                st3.Write(p, 0, piece, 0, piece.Length);
                h3[p] = st3.CheckPiece(p, buf);
            }
            List<string> errs3 = new List<string>();
            for (int p = 0; p < meta.PieceCount; p++) errs3.AddRange(st3.PieceVerified(p, h3));
            T.Check("storage: a taken final name is an error, not an overwrite", errs3.Count >= 1 && File.ReadAllText(st3.FinalPath(1)) == "users own file"
                    && File.Exists(st3.PartPath(1)), string.Join("; ", errs3.ToArray()));
            st3.Dispose();

            // Junction внутри торрента — отказ.
            string dir4 = Fx.MakeDir(Fx.Root, "bt-junction");
            string outside = Fx.MakeDir(Fx.Root, "bt-outside");
            Directory.CreateDirectory(Path.Combine(dir4, "pack"));
            if (Fx.Junction(Path.Combine(Path.Combine(dir4, "pack"), "sub"), outside))
            {
                BtStorage st4 = new BtStorage(meta, dir4, "pack");
                T.Check("storage: junction inside the torrent tree refused", st4.Validate() != null, st4.Validate());
                st4.Dispose();
            }
            else T.Skip("storage: junction inside the torrent tree refused", "mklink /J failed");

            // Длинный путь.
            string longRoot = Fx.MakeDir(Fx.Root, "bt-long");
            string deepName = new string('d', 120);
            List<BtFxFile> longFiles = new List<BtFxFile> { new BtFxFile(BtFx.Data(40000, 41), deepName, deepName, deepName, "f.bin") };
            BtMeta lm = BtMeta.Parse(BtFx.Build("long", longFiles, 16384, 1, false, null), out err);
            BtStorage ls = new BtStorage(lm, longRoot, "long");
            BtBitfield lh = new BtBitfield(lm.PieceCount);
            string lerr = null;
            for (int p = 0; p < lm.PieceCount; p++)
            {
                byte[] piece = BtFx.PieceBytes(lm, longFiles, p);
                lerr = lerr ?? ls.Write(p, 0, piece, 0, piece.Length);
                lh[p] = ls.CheckPiece(p, new byte[16384]);
            }
            List<string> lerrs = new List<string>();
            for (int p = 0; p < lm.PieceCount; p++) lerrs.AddRange(ls.PieceVerified(p, lh));
            T.Check("storage: path over 260 chars written, verified and renamed", lerr == null && lh.All && lerrs.Count == 0 && ls.FinalPath(0).Length > 260
                    && BtFs.Length(ls.FinalPath(0)) == 40000, lerr + " " + string.Join(";", lerrs.ToArray()));
            ls.Dispose();

            // Удаление — только через Корзину; папка целиком, если чужого нет.
            Func<string, string> saved = DlFiles.Recycler;
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { recycled.Add(p); return null; };
            try
            {
                new BtStorage(meta, dir, "pack").RecycleData();
                T.Check("storage: remove recycles the torrent folder as a whole when it holds only our files",
                        recycled.Count == 1 && recycled[0] == Path.Combine(dir, "pack"), string.Join("; ", recycled.ToArray()));
                recycled.Clear();
                File.WriteAllText(Path.Combine(Path.Combine(dir, "pack"), "foreign.txt"), "x");
                new BtStorage(meta, dir, "pack").RecycleData();
                T.Check("storage: a foreign file inside ⇒ only our files recycled, one by one",
                        recycled.Count == 3 && !recycled.Contains(Path.Combine(dir, "pack")), string.Join("; ", recycled.ToArray()));
            }
            finally { DlFiles.Recycler = saved; }
            T.Check("storage: nothing actually deleted by the faked recycle", File.Exists(Path.Combine(Path.Combine(dir, "pack"), "two.bin")));
        }

        // ---------- возобновление, очередь диска, битовое поле ----------
        private static void ResumeAndDiskCases()
        {
            string err;
            T.Check("bitfield: spare bits set ⇒ refused", BtBitfield.FromBytes(new byte[] { 0xFF }, 7) == null);
            T.Check("bitfield: wrong length ⇒ refused", BtBitfield.FromBytes(new byte[2], 7) == null);
            BtBitfield bf = BtBitfield.FromBytes(new byte[] { 0x80, 0x01 }, 16);
            T.Check("bitfield: MSB of byte 0 is piece 0", bf != null && bf[0] && bf[15] && !bf[1] && bf.SetCount == 2);

            string dir = Fx.MakeDir(Fx.Root, "bt-resume");
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(40000, 51), "a.bin"), new BtFxFile(BtFx.Data(40000, 52), "b.bin") };
            BtMeta meta = BtMeta.Parse(BtFx.Build("res", files, 16384, 1, false, null), out err);
            BtStorage st = new BtStorage(meta, dir, "res");
            BtBitfield have = new BtBitfield(meta.PieceCount);
            using (BtDisk disk = new BtDisk("wpc-bt-disk-test"))
            {
                int verified = 0, failed = 0;
                for (int p = 0; p < meta.PieceCount; p++)
                {
                    if (p == 3) continue;                        // один кусок не скачан
                    byte[] piece = BtFx.PieceBytes(meta, files, p);
                    disk.Write(st, p, 0, piece, 0, piece.Length, null);
                    disk.Check(st, p, have, delegate(bool ok, List<string> e) { if (ok) Interlocked.Increment(ref verified); else Interlocked.Increment(ref failed); });
                }
                ManualResetEvent flushed = new ManualResetEvent(false);
                disk.Run(delegate { st.FlushAll(); flushed.Set(); });
                T.Check("disk queue: write-then-check order holds for every piece", flushed.WaitOne(10000) && verified == meta.PieceCount - 1 && failed == 0,
                        verified + " ok, " + failed + " failed");
                T.Eq("disk queue: queued bytes drain to zero", 0L, disk.QueuedBytes);
            }
            BtResume r = BtResume.Capture(st, have, new[] { 1, 2 });
            string file = BtResume.FileFor(dir, meta.HexHash);
            r.Save(file);
            st.Dispose();

            BtStorage st2 = new BtStorage(meta, dir, "res");
            List<int> recheck = new List<int>();
            BtResume loaded = BtResume.Load(file);
            BtBitfield back = loaded == null ? null : loaded.Restore(st2, recheck);
            T.Check("resume: untouched files restore the same bitfield, nothing to recheck",
                    back != null && Bencode.SameBytes(back.ToBytes(), have.ToBytes()) && recheck.Count == 0 && loaded.Priorities[1] == 2);
            T.Check("resume: the finished file is detected by name", st2.IsFileDone(0) && !st2.IsFileDone(1));
            st2.Dispose();

            // Файл изменён после записи состояния — его куски не доверяются.
            string bPart = new BtStorage(meta, dir, "res").PartPath(1);
            File.SetLastWriteTimeUtc(bPart, DateTime.UtcNow.AddMinutes(-5));
            BtStorage st3 = new BtStorage(meta, dir, "res");
            recheck.Clear();
            BtBitfield after = BtResume.Load(file).Restore(st3, recheck);
            BtFile fb = meta.Files[1];
            bool clearedB = true;
            for (int p = fb.FirstPiece; p <= fb.LastPiece; p++) if (after[p]) clearedB = false;
            T.Check("resume: a file changed on disk loses its pieces into the recheck list, the other file keeps them",
                    clearedB && after[0] && recheck.Count > 0 && recheck.Contains(fb.LastPiece), "recheck " + recheck.Count);
            st3.Dispose();
        }

        // ---------- адреса, UDP, ведро без ожидания, скрытие ключей ----------
        private static void NetBasics()
        {
            List<BtEndpoint> eps = BtEndpoint.ParseCompact(new byte[] { 10, 0, 0, 1, 0x1A, 0xE1, 1, 2, 3, 4, 0, 0, 224, 0, 0, 1, 0, 80, 9 }, false);
            T.Eq("endpoint: compact v4 parsed, port 0 and multicast dropped, tail ignored", "10.0.0.1:6881", eps.Count == 1 ? eps[0].ToString() : eps.Count.ToString());
            BtEndpoint e = BtEndpoint.TryParse("[::1]:51413");
            T.Check("endpoint: v6 literal round-trips via compact", e != null && BtEndpoint.ParseCompact(e.ToCompact(), true)[0].Equals(e));
            T.Check("endpoint: host names are not resolved", BtEndpoint.TryParse("tracker.example:80") == null);

            string red = BtRedact.Url("https://bt.t-ru.org/ann?pk=0123456789abcdef0123456789abcdef");
            T.Check("redact: passkey query hidden", !red.Contains("0123456789") && red.Contains("bt.t-ru.org"), red);
            string red2 = BtRedact.Url("http://tracker.example:2710/0123456789abcdef0123456789abcdef/announce");
            T.Check("redact: key in the path hidden, announce kept", !red2.Contains("0123456789") && red2.EndsWith("/announce"), red2);

            DlTokenBucket bucket = new DlTokenBucket(100000);
            int first = bucket.TryTake(50000);
            Thread.Sleep(120);
            int later = bucket.TryTake(50000);
            T.Check("bucket: TryTake never waits, starts empty, refills with time", first < 1000 && later > 5000 && later <= 25000, first + " then " + later);

            using (BtUdp a = new BtUdp(IPAddress.Loopback, 0))
            using (BtUdp b = new BtUdp(IPAddress.Loopback, 0))
            {
                List<string> seen = new List<string>();
                ManualResetEvent got = new ManualResetEvent(false);
                a.AddHandler(new FxUdpHandler(delegate(BtEndpoint from, byte[] d, int n) { lock (seen) seen.Add("first:" + d[0]); return d[0] == 1; }));
                a.AddHandler(new FxUdpHandler(delegate(BtEndpoint from, byte[] d, int n) { lock (seen) seen.Add("second:" + d[0]); if (d[0] == 2) got.Set(); return true; }));
                // Датаграмма в закрытый порт: ICMP «недоступен» не должен уронить поток приёма.
                int closedPort;
                using (BtUdp c = new BtUdp(IPAddress.Loopback, 0)) closedPort = c.Port;
                a.Send(new BtEndpoint(IPAddress.Loopback, closedPort), new byte[] { 9 }, 1);
                Thread.Sleep(100);
                BtEndpoint toA = new BtEndpoint(IPAddress.Loopback, a.Port);
                b.Send(toA, new byte[] { 1 }, 1);
                Thread.Sleep(100);
                b.Send(toA, new byte[] { 2 }, 1);
                bool arrived = got.WaitOne(3000);
                string order;
                lock (seen) order = string.Join(",", seen.ToArray());
                T.Check("udp: handler chain — first true wins, receive thread survives ICMP port-unreachable", arrived && order == "first:1,first:2,second:2", order);
            }
        }

        private sealed class FxUdpHandler : IBtUdpHandler
        {
            private readonly Func<BtEndpoint, byte[], int, bool> _f;
            public FxUdpHandler(Func<BtEndpoint, byte[], int, bool> f) { _f = f; }
            public bool HandleDatagram(BtEndpoint from, byte[] data, int count) { return _f(from, data, count); }
        }
    }
}
