// SysDeck — «Загрузки», торренты: метаданные (.torrent v1, v2, гибрид — BEP 3/12/27/47/52), magnet-ссылка
// (BEP 9) и дерево хешей v2.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Раскладка кусков одна на все версии: файлы лежат в «виртуальном потоке» со смещениями. В v1 и гибриде поток сплошной,
// файлы-заполнители (BEP 47) — настоящие нули внутри куска. В чистом v2 каждый файл начинается с границы куска, а
// промежуток между файлами — не данные: кусок состоит только из байт одного файла.
// Пути из торрента — чужой ввод: каждый элемент проходит DlFiles.SanitizeName, разделители внутри элемента заменяются,
// совпавшие после очистки имена разводятся « (1)». Выйти из папки загрузки путь не может по построению, а хранилище
// (Torrent.Storage.cs) проверяет это ещё раз уже на полном пути.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SysDeck.Downloads
{
    internal sealed class BtFile
    {
        public string RelPath = "";          // относительно корня торрента, через '\'
        public long Length;
        public long Offset;                  // начало в виртуальном потоке
        public bool Pad;                     // BEP 47 'p': не пишется, читается нулями
        public bool Symlink;                 // BEP 47 'l': не создаётся никогда
        public byte[] Root;                  // v2 pieces root (32 байта) или null
        public int FirstPiece;
        public int LastPiece = -1;           // -1 — файл без кусков (пустой или заполнитель вне раскладки)

        public long End { get { return Offset + Length; } }
    }

    internal sealed class BtMeta
    {
        public const int BlockSize = 16 * 1024;
        public const long MaxPieceLength = 256L * 1024 * 1024;
        public const int MaxFiles = 1000000;
        public const int MaxPathDepth = 64;

        public byte[] InfoHash;              // SHA-1 словаря info (v1 и гибрид); null — чистый v2
        public byte[] InfoHashV2;            // SHA-256 словаря info (v2 и гибрид); null — чистый v1
        public byte[] InfoBytes;             // словарь info байт в байт — отдаётся пирам по ut_metadata и сохраняется
        public byte[] TorrentBytes;          // исходный .torrent, если метаданные пришли файлом
        public string Name = "";
        public string RootDir = "";          // папка торрента (очищенное имя); пусто — однофайловый
        public long PieceLength;
        public int PieceCount;
        public byte[] PieceHashes;           // v1: 20 байт на кусок
        public readonly List<BtFile> Files = new List<BtFile>();
        public long StreamLength;            // конец последнего файла в виртуальном потоке
        public long TotalSize;               // сумма длин настоящих файлов (без заполнителей)
        public bool Private;
        public int Version;                  // 1, 2 или 3 (гибрид)
        public readonly List<List<string>> Trackers = new List<List<string>>();
        public readonly List<string> WebSeeds = new List<string>();
        public string Comment = "";
        public string PublisherUrl = "";     // rutracker и др.: ссылка на тему раздачи
        public string CreatedBy = "";
        public string Source = "";
        public DateTime CreatedUtc = DateTime.MinValue;
        public readonly Dictionary<string, byte[]> PieceLayers = new Dictionary<string, byte[]>(StringComparer.Ordinal);  // hex root → слой

        public bool IsV1 { get { return InfoHash != null; } }
        public bool IsPureV2 { get { return InfoHash == null; } }

        // 20 байт для рукопожатия, трекеров и DHT: SHA-1 у v1 и гибрида, усечённый SHA-256 у чистого v2.
        public byte[] SwarmHash
        {
            get
            {
                if (InfoHash != null) return InfoHash;
                byte[] h = new byte[20];
                Buffer.BlockCopy(InfoHashV2, 0, h, 0, 20);
                return h;
            }
        }

        public string HexHash { get { return Bencode.Hex(SwarmHash); } }

        public long PieceStart(int piece) { return (long)piece * PieceLength; }

        // Сколько байт данных в куске: пересечение с настоящими файлами и заполнителями.
        public int PieceSize(int piece)
        {
            if (piece < 0 || piece >= PieceCount) return 0;
            long start = PieceStart(piece), end = start + PieceLength;
            if (!IsPureV2) return (int)(Math.Min(end, StreamLength) - start);
            BtFile f = FileOfPiece(piece);
            return f == null ? 0 : (int)(Math.Min(end, f.End) - start);
        }

        // Файл, в котором начинается кусок (для v2 — единственный файл куска).
        public BtFile FileOfPiece(int piece)
        {
            long start = PieceStart(piece);
            int lo = 0, hi = Files.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (Files[mid].Offset <= start) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            for (int i = found; i >= 0 && i < Files.Count; i++)
            {
                BtFile f = Files[i];
                if (f.Length == 0) continue;
                if (f.Offset > start) return f;
                if (f.End > start) return f;
            }
            return null;
        }

        // Индексы файлов, пересекающих кусок (включая заполнители).
        public List<int> FilesOfPiece(int piece)
        {
            List<int> list = new List<int>();
            long start = PieceStart(piece), end = start + PieceSize(piece);
            for (int i = 0; i < Files.Count; i++)
            {
                BtFile f = Files[i];
                if (f.Length == 0 || f.LastPiece < 0) continue;
                if (f.Offset < end && f.End > start) list.Add(i);
            }
            return list;
        }

        public byte[] PieceHashV1(int piece)
        {
            byte[] h = new byte[20];
            Buffer.BlockCopy(PieceHashes, piece * 20, h, 0, 20);
            return h;
        }

        // ---------- разбор .torrent ----------
        public static BtMeta Parse(byte[] torrent, out string error)
        {
            BVal root = Bencode.Decode(torrent, out error);
            if (root == null) { error = "bencode: " + error; return null; }
            if (root.Kind != BKind.Dict) { error = "the root is not a dictionary"; return null; }
            BVal info = root.Get("info", BKind.Dict);
            if (info == null) { error = "no info dictionary"; return null; }
            byte[] infoBytes = new byte[info.End - info.Start];
            Buffer.BlockCopy(torrent, info.Start, infoBytes, 0, infoBytes.Length);
            BtMeta m = FromInfoValue(info, infoBytes, root.Get("piece layers", BKind.Dict), out error);
            if (m == null) return null;
            m.TorrentBytes = torrent;
            m.ReadTrackers(root);
            BVal urls = root.Get("url-list");
            if (urls != null && urls.Kind == BKind.Bytes) AddWebSeed(m, urls.Text);
            else if (urls != null && urls.Kind == BKind.List)
                foreach (BVal u in urls.L) if (u.Kind == BKind.Bytes) AddWebSeed(m, u.Text);
            m.Comment = Utf8Field(root, "comment");
            m.PublisherUrl = Utf8Field(root, "publisher-url");
            m.CreatedBy = Utf8Field(root, "created by");
            long created = root.GetInt("creation date", 0);
            if (created > 0 && created < 253402300799L) m.CreatedUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(created);
            return m;
        }

        // Словарь info, полученный от пиров по magnet (ut_metadata). expected — хеш из ссылки; не совпал — отказ.
        public static BtMeta FromInfo(byte[] infoBytes, byte[] expectedSwarmHash, out string error)
        {
            BVal info = Bencode.Decode(infoBytes, out error);
            if (info == null) { error = "bencode: " + error; return null; }
            if (info.Kind != BKind.Dict) { error = "info is not a dictionary"; return null; }
            BtMeta m = FromInfoValue(info, infoBytes, null, out error);
            if (m == null) return null;
            if (expectedSwarmHash != null && !Bencode.SameBytes(m.SwarmHash, expectedSwarmHash)
                && !(m.InfoHashV2 != null && expectedSwarmHash.Length == 32 && Bencode.SameBytes(m.InfoHashV2, expectedSwarmHash)))
            {
                error = "info-hash mismatch";
                return null;
            }
            return m;
        }

        private static BtMeta FromInfoValue(BVal info, byte[] infoBytes, BVal pieceLayers, out string error)
        {
            error = null;
            BtMeta m = new BtMeta();
            m.InfoBytes = infoBytes;
            long version = info.GetInt("meta version", 1);
            BVal tree = info.Get("file tree", BKind.Dict);
            byte[] pieces = info.GetBytes("pieces");
            bool hasV2 = version == 2 && tree != null;
            bool hasV1 = pieces != null;
            if (version != 1 && version != 2) { error = "unsupported meta version " + version.ToString(CultureInfo.InvariantCulture); return null; }
            if (!hasV1 && !hasV2) { error = "neither v1 pieces nor a v2 file tree"; return null; }
            m.Version = hasV1 && hasV2 ? 3 : hasV2 ? 2 : 1;
            using (SHA1 sha1 = SHA1.Create()) if (hasV1) m.InfoHash = sha1.ComputeHash(infoBytes);
            using (SHA256 sha256 = SHA256.Create()) if (hasV2) m.InfoHashV2 = sha256.ComputeHash(infoBytes);

            m.PieceLength = info.GetInt("piece length", 0);
            if (m.PieceLength < 1024 || m.PieceLength > MaxPieceLength) { error = "bad piece length"; return null; }
            if (hasV2 && (m.PieceLength < BlockSize || (m.PieceLength & (m.PieceLength - 1)) != 0))
            {
                error = "v2 piece length must be a power of two of at least 16 KiB";
                return null;
            }
            string name = Utf8Field(info, "name.utf-8");
            if (name.Length == 0) name = Utf8Field(info, "name");
            m.Name = name;
            m.Private = info.GetInt("private", 0) == 1;
            m.Source = Utf8Field(info, "source");

            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (hasV1)
            {
                if (!m.ReadV1Files(info, taken, out error)) return null;
                if (hasV2 && !m.AttachV2Roots(tree, out error)) return null;
            }
            else if (!m.ReadV2Files(tree, taken, out error)) return null;

            if (hasV1)
            {
                if (pieces.Length % 20 != 0) { error = "pieces length is not a multiple of 20"; return null; }
                m.PieceHashes = pieces;
                long count = (m.StreamLength + m.PieceLength - 1) / m.PieceLength;
                if (count != pieces.Length / 20) { error = "piece count does not match the total length"; return null; }
                m.PieceCount = (int)count;
            }
            m.LayOutPieces();
            if (m.IsPureV2 && !m.ReadPieceLayers(pieceLayers, out error)) return null;
            return m;
        }

        private bool ReadV1Files(BVal info, HashSet<string> taken, out string error)
        {
            error = null;
            BVal files = info.Get("files", BKind.List);
            long length = info.GetInt("length", -1);
            if (files == null)
            {
                if (length < 0) { error = "no length and no files"; return false; }
                BtFile f = new BtFile();
                f.RelPath = CleanElement(Name.Length > 0 ? Name : HexHash);
                f.Length = length;
                Files.Add(f);
                taken.Add(f.RelPath);
            }
            else
            {
                RootDir = CleanElement(Name.Length > 0 ? Name : HexHash);
                if (files.L.Count == 0 || files.L.Count > MaxFiles) { error = "bad file count"; return false; }
                long offset = 0;
                foreach (BVal fe in files.L)
                {
                    if (fe.Kind != BKind.Dict) { error = "file entry is not a dictionary"; return false; }
                    BVal path = fe.Get("path.utf-8", BKind.List) ?? fe.Get("path", BKind.List);
                    long len = fe.GetInt("length", -1);
                    if (path == null || path.L.Count == 0 || path.L.Count > MaxPathDepth || len < 0) { error = "bad file entry"; return false; }
                    string attr = Utf8Field(fe, "attr");
                    BtFile f = new BtFile();
                    f.Length = len;
                    f.Offset = offset;
                    f.Pad = attr.IndexOf('p') >= 0;
                    f.Symlink = attr.IndexOf('l') >= 0;
                    List<string> parts = new List<string>();
                    foreach (BVal p in path.L)
                    {
                        if (p.Kind != BKind.Bytes) { error = "path element is not a string"; return false; }
                        parts.Add(p.Text);
                    }
                    f.RelPath = f.Pad ? "" : Unique(CleanPath(parts), taken);
                    Files.Add(f);
                    if (len > long.MaxValue / 4 - offset) { error = "total length overflow"; return false; }
                    offset += len;
                }
            }
            long end = 0;
            foreach (BtFile f in Files)
            {
                f.Offset = end;
                end += f.Length;
                if (!f.Pad) TotalSize += f.Length;
            }
            StreamLength = end;
            return true;
        }

        // Гибрид: корни v2 прикрепляются к файлам v1 по пути. Пути дерева v2, как и пути v1, — относительно корня торрента,
        // заполнителей в дереве нет. Очистка пути одинакова для обеих версий — сверяется её результат.
        private bool AttachV2Roots(BVal tree, out string error)
        {
            error = null;
            List<KeyValuePair<List<string>, BVal>> leaves = new List<KeyValuePair<List<string>, BVal>>();
            if (!WalkTree(tree, new List<string>(), leaves, 0, out error)) return false;
            Dictionary<string, BtFile> v1 = new Dictionary<string, BtFile>(StringComparer.OrdinalIgnoreCase);
            foreach (BtFile f in Files) if (!f.Pad) v1[f.RelPath] = f;
            foreach (KeyValuePair<List<string>, BVal> kv in leaves)
            {
                BtFile f;
                if (!v1.TryGetValue(CleanPath(kv.Key), out f)) continue;
                long len = kv.Value.GetInt("length", -1);
                if (len != f.Length) { error = "hybrid torrent: v1 and v2 file lengths differ"; return false; }
                f.Root = kv.Value.GetBytes("pieces root");
            }
            return true;
        }

        private bool ReadV2Files(BVal tree, HashSet<string> taken, out string error)
        {
            List<KeyValuePair<List<string>, BVal>> leaves = new List<KeyValuePair<List<string>, BVal>>();
            if (!WalkTree(tree, new List<string>(), leaves, 0, out error)) return false;
            if (leaves.Count == 0 || leaves.Count > MaxFiles) { error = "bad file count"; return false; }
            bool single = leaves.Count == 1 && leaves[0].Key.Count == 1;
            if (!single) RootDir = CleanElement(Name.Length > 0 ? Name : Bencode.Hex(InfoHashV2).Substring(0, 40));
            long offset = 0;
            foreach (KeyValuePair<List<string>, BVal> kv in leaves)
            {
                long len = kv.Value.GetInt("length", -1);
                if (len < 0) { error = "v2 file without length"; return false; }
                BtFile f = new BtFile();
                f.Length = len;
                f.Root = kv.Value.GetBytes("pieces root");
                if (len > 0 && (f.Root == null || f.Root.Length != 32)) { error = "v2 file without a 32-byte pieces root"; return false; }
                f.RelPath = Unique(CleanPath(kv.Key), taken);
                f.Offset = offset;
                Files.Add(f);
                if (len > 0)
                {
                    long pieces = (len + PieceLength - 1) / PieceLength;
                    if (pieces > int.MaxValue / 2 - offset / PieceLength) { error = "total length overflow"; return false; }
                    offset += pieces * PieceLength;
                }
                TotalSize += len;
            }
            StreamLength = Files.Count == 0 ? 0 : Files[Files.Count - 1].End;
            long total = 0;
            foreach (BtFile f in Files) if (f.Length > 0) total = f.Offset + ((f.Length + PieceLength - 1) / PieceLength) * PieceLength;
            PieceCount = (int)(total / PieceLength);
            return true;
        }

        // Обход дерева файлов v2: ключ "" у словаря — это файл. Порядок — порядок ключей в словаре (канонический).
        private static bool WalkTree(BVal node, List<string> path, List<KeyValuePair<List<string>, BVal>> leaves, int depth, out string error)
        {
            error = null;
            if (depth > MaxPathDepth) { error = "file tree too deep"; return false; }
            foreach (KeyValuePair<byte[], BVal> kv in node.D)
            {
                if (kv.Value.Kind != BKind.Dict) { error = "file tree node is not a dictionary"; return false; }
                if (kv.Key.Length == 0)
                {
                    if (path.Count == 0) { error = "file tree leaf at the root"; return false; }
                    leaves.Add(new KeyValuePair<List<string>, BVal>(new List<string>(path), kv.Value));
                    if (leaves.Count > MaxFiles) { error = "too many files"; return false; }
                    continue;
                }
                path.Add(Encoding.UTF8.GetString(kv.Key));
                bool ok = WalkTree(kv.Value, path, leaves, depth + 1, out error);
                path.RemoveAt(path.Count - 1);
                if (!ok) return false;
            }
            return true;
        }

        private void LayOutPieces()
        {
            foreach (BtFile f in Files)
            {
                if (f.Length == 0 || PieceCount == 0) { f.FirstPiece = 0; f.LastPiece = -1; continue; }
                f.FirstPiece = (int)(f.Offset / PieceLength);
                f.LastPiece = (int)((f.End - 1) / PieceLength);
            }
        }

        // Слои хешей чистого v2: для файла больше куска — обязателен и обязан сворачиваться в корень файла.
        private bool ReadPieceLayers(BVal layers, out string error)
        {
            error = null;
            foreach (BtFile f in Files)
            {
                if (f.Length <= PieceLength) continue;
                byte[] layer = null;
                if (layers != null)
                    foreach (KeyValuePair<byte[], BVal> kv in layers.D)
                        if (Bencode.SameBytes(kv.Key, f.Root) && kv.Value.Kind == BKind.Bytes) { layer = kv.Value.B; break; }
                int pieces = f.LastPiece - f.FirstPiece + 1;
                if (layer == null) { error = "v2 torrent without piece layers (a magnet-only v2 swarm is not supported)"; return false; }
                if (layer.Length != pieces * 32) { error = "piece layer length does not match the file"; return false; }
                if (!Bencode.SameBytes(BtMerkle.RootFromLayer(layer, pieces, PieceLength), f.Root)) { error = "piece layer does not match the file root"; return false; }
                PieceLayers[Bencode.Hex(f.Root)] = layer;
            }
            return true;
        }

        // Хеш куска v2: из слоя (файл больше куска) или корень файла (файл в один кусок).
        public bool CheckPieceV2(int piece, byte[] data, int count)
        {
            BtFile f = FileOfPiece(piece);
            if (f == null || f.Root == null) return false;
            if (f.Length <= PieceLength)
                return Bencode.SameBytes(BtMerkle.Root(data, 0, count, BtMerkle.NextPow2((count + BlockSize - 1) / BlockSize)), f.Root);
            byte[] layer;
            if (!PieceLayers.TryGetValue(Bencode.Hex(f.Root), out layer)) return false;
            byte[] expected = new byte[32];
            Buffer.BlockCopy(layer, (piece - f.FirstPiece) * 32, expected, 0, 32);
            return Bencode.SameBytes(BtMerkle.Root(data, 0, count, (int)(PieceLength / BlockSize)), expected);
        }

        private void ReadTrackers(BVal root)
        {
            BVal list = root.Get("announce-list", BKind.List);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (list != null)
                foreach (BVal tier in list.L)
                {
                    if (tier.Kind != BKind.List) continue;
                    List<string> t = new List<string>();
                    foreach (BVal u in tier.L)
                        if (u.Kind == BKind.Bytes && IsTrackerUrl(u.Text) && seen.Add(u.Text)) t.Add(u.Text);
                    if (t.Count > 0) Trackers.Add(t);
                }
            if (Trackers.Count == 0)
            {
                string a = Utf8Field(root, "announce");
                if (IsTrackerUrl(a)) Trackers.Add(new List<string> { a });
            }
        }

        public static bool IsTrackerUrl(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || url.Length > 2048 || !Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            return (u.Scheme == "http" || u.Scheme == "https" || u.Scheme == "udp") && u.Host.Length > 0;
        }

        private static void AddWebSeed(BtMeta m, string url)
        {
            Uri u;
            if (m.WebSeeds.Count < 64 && Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https"))
                m.WebSeeds.Add(url);
        }

        private static string Utf8Field(BVal dict, string key)
        {
            string s = dict.GetStr(key);
            return s ?? "";
        }

        // ---------- пути ----------
        public static string CleanElement(string element)
        {
            string e = (element ?? "").Replace('/', '_').Replace('\\', '_');
            return DlFiles.SanitizeName(e);
        }

        private static string CleanPath(List<string> parts)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string p in parts)
            {
                if (sb.Length > 0) sb.Append('\\');
                sb.Append(CleanElement(p));
            }
            return sb.ToString();
        }

        private static string Unique(string rel, HashSet<string> taken)
        {
            if (taken.Add(rel)) return rel;
            int slash = rel.LastIndexOf('\\');
            string dir = slash >= 0 ? rel.Substring(0, slash + 1) : "";
            string file = rel.Substring(slash + 1);
            int dot = file.LastIndexOf('.');
            string stem = dot > 0 ? file.Substring(0, dot) : file;
            string ext = dot > 0 ? file.Substring(dot) : "";
            for (int n = 1; ; n++)
            {
                string candidate = dir + stem + " (" + n.ToString(CultureInfo.InvariantCulture) + ")" + ext;
                if (taken.Add(candidate)) return candidate;
            }
        }

        // .torrent из метаданных, полученных по magnet: словарь info байт в байт + трекеры.
        public byte[] BuildTorrentFile()
        {
            if (TorrentBytes != null) return TorrentBytes;
            BVal root = BVal.NewDict();
            if (Trackers.Count > 0)
            {
                root.Set("announce", BVal.Str(Trackers[0][0]));
                BVal tiers = BVal.NewList();
                foreach (List<string> tier in Trackers)
                {
                    BVal t = BVal.NewList();
                    foreach (string u in tier) t.Add(BVal.Str(u));
                    tiers.Add(t);
                }
                root.Set("announce-list", tiers);
            }
            if (Comment.Length > 0) root.Set("comment", BVal.Str(Comment));
            // info вставляется сырыми байтами: перекодирование могло бы переставить ключи и сменить info-hash. Ключ «info» по
            // алфавиту после «announce*» и «comment» — место в конце словаря и есть канонический порядок.
            byte[] head = Bencode.Encode(root);
            byte[] key = Encoding.ASCII.GetBytes("4:info");
            List<byte> outBytes = new List<byte>(head.Length + key.Length + InfoBytes.Length);
            for (int i = 0; i < head.Length - 1; i++) outBytes.Add(head[i]);
            outBytes.AddRange(key);
            outBytes.AddRange(InfoBytes);
            outBytes.Add((byte)'e');
            return outBytes.ToArray();
        }
    }

    // ------------------------------------------------------------------ //
    //  magnet:?xt=urn:btih:…&dn=…&tr=…
    // ------------------------------------------------------------------ //
    internal sealed class BtMagnet
    {
        public byte[] InfoHash;              // 20 байт (btih) или null
        public byte[] InfoHashV2;            // 32 байта (btmh sha2-256) или null
        public string Name = "";
        public readonly List<string> Trackers = new List<string>();
        public readonly List<string> WebSeeds = new List<string>();
        public readonly List<string> Peers = new List<string>();   // x.pe: host:port

        public byte[] SwarmHash
        {
            get
            {
                if (InfoHash != null) return InfoHash;
                if (InfoHashV2 == null) return null;
                byte[] h = new byte[20];
                Buffer.BlockCopy(InfoHashV2, 0, h, 0, 20);
                return h;
            }
        }

        public static BtMagnet Parse(string link, out string error)
        {
            error = null;
            string s = (link ?? "").Trim();
            if (s.Length > 16 * 1024 || !s.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) { error = "not a magnet link"; return null; }
            BtMagnet m = new BtMagnet();
            foreach (string pair in s.Substring(8).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                string key = pair.Substring(0, eq).ToLowerInvariant();
                string raw = pair.Substring(eq + 1);
                if (key == "dn") raw = raw.Replace('+', ' ');
                string value;
                try { value = Uri.UnescapeDataString(raw); }
                catch { continue; }
                // tr.1=…, xt.1=… — нумерованные повторы ключа.
                int dot = key.IndexOf('.');
                if (dot > 0 && key != "x.pe") key = key.Substring(0, dot);
                switch (key)
                {
                    case "xt":
                        if (value.StartsWith("urn:btih:", StringComparison.OrdinalIgnoreCase))
                        {
                            string h = value.Substring(9);
                            byte[] b = h.Length == 40 ? Bencode.FromHex(h) : h.Length == 32 ? Base32(h) : null;
                            if (b == null) { error = "bad btih hash"; return null; }
                            m.InfoHash = b;
                        }
                        else if (value.StartsWith("urn:btmh:", StringComparison.OrdinalIgnoreCase))
                        {
                            byte[] mh = Bencode.FromHex(value.Substring(9));
                            if (mh == null || mh.Length != 34 || mh[0] != 0x12 || mh[1] != 0x20) { error = "bad btmh hash"; return null; }
                            m.InfoHashV2 = new byte[32];
                            Buffer.BlockCopy(mh, 2, m.InfoHashV2, 0, 32);
                        }
                        break;
                    case "dn":
                        m.Name = value.Length > 512 ? value.Substring(0, 512) : value;
                        break;
                    case "tr":
                        if (m.Trackers.Count < 100 && BtMeta.IsTrackerUrl(value) && !m.Trackers.Contains(value)) m.Trackers.Add(value);
                        break;
                    case "ws":
                        Uri u;
                        if (m.WebSeeds.Count < 64 && Uri.TryCreate(value, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) m.WebSeeds.Add(value);
                        break;
                    case "x.pe":
                        if (m.Peers.Count < 200 && value.Length < 300) m.Peers.Add(value);
                        break;
                }
            }
            if (m.InfoHash == null && m.InfoHashV2 == null) { error = "no info-hash in the magnet link"; return null; }
            return m;
        }

        public string ToLink()
        {
            StringBuilder sb = new StringBuilder("magnet:?");
            bool first = true;
            if (InfoHash != null) { sb.Append("xt=urn:btih:").Append(Bencode.Hex(InfoHash)); first = false; }
            if (InfoHashV2 != null) { sb.Append(first ? "" : "&").Append("xt=urn:btmh:1220").Append(Bencode.Hex(InfoHashV2)); first = false; }
            if (Name.Length > 0) sb.Append("&dn=").Append(Uri.EscapeDataString(Name));
            foreach (string t in Trackers) sb.Append("&tr=").Append(Uri.EscapeDataString(t));
            return sb.ToString();
        }

        private static byte[] Base32(string s)
        {
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            byte[] result = new byte[20];
            int buffer = 0, bits = 0, at = 0;
            foreach (char ch in s.ToUpperInvariant())
            {
                int v = alphabet.IndexOf(ch);
                if (v < 0) return null;
                buffer = (buffer << 5) | v;
                bits += 5;
                if (bits >= 8)
                {
                    bits -= 8;
                    if (at >= 20) return null;
                    result[at++] = (byte)(buffer >> bits);
                    buffer &= (1 << bits) - 1;
                }
            }
            return at == 20 ? result : null;
        }
    }

    // ------------------------------------------------------------------ //
    //  Дерево хешей BEP 52: листья — SHA-256 блоков по 16 КиБ, недостающие листья — нули
    // ------------------------------------------------------------------ //
    internal static class BtMerkle
    {
        public static int NextPow2(int n)
        {
            int p = 1;
            while (p < n) p <<= 1;
            return p;
        }

        // Корень над данными: leafCount (степень двойки) листьев, блоки за концом данных — нулевые хеши.
        public static byte[] Root(byte[] data, int offset, int count, int leafCount)
        {
            List<byte[]> layer = new List<byte[]>(leafCount);
            using (SHA256 sha = SHA256.Create())
            {
                for (int pos = 0; pos < count; pos += BtMeta.BlockSize)
                    layer.Add(sha.ComputeHash(data, offset + pos, Math.Min(BtMeta.BlockSize, count - pos)));
                while (layer.Count < leafCount) layer.Add(new byte[32]);
                return Fold(layer, sha);
            }
        }

        // Корень файла из слоя кусков: недостающие куски — корень поддерева из нулевых листьев размера куска.
        public static byte[] RootFromLayer(byte[] layer, int pieces, long pieceLength)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] pad = new byte[32];
                for (long n = BtMeta.BlockSize; n < pieceLength; n *= 2) pad = Pair(sha, pad, pad);
                List<byte[]> nodes = new List<byte[]>();
                for (int i = 0; i < pieces; i++)
                {
                    byte[] h = new byte[32];
                    Buffer.BlockCopy(layer, i * 32, h, 0, 32);
                    nodes.Add(h);
                }
                int target = NextPow2(pieces);
                while (nodes.Count < target) nodes.Add(pad);
                return Fold(nodes, sha);
            }
        }

        private static byte[] Fold(List<byte[]> layer, SHA256 sha)
        {
            while (layer.Count > 1)
            {
                List<byte[]> up = new List<byte[]>(layer.Count / 2);
                for (int i = 0; i + 1 < layer.Count; i += 2) up.Add(Pair(sha, layer[i], layer[i + 1]));
                layer = up;
            }
            return layer.Count == 1 ? layer[0] : new byte[32];
        }

        private static byte[] Pair(SHA256 sha, byte[] a, byte[] b)
        {
            byte[] buf = new byte[64];
            Buffer.BlockCopy(a, 0, buf, 0, 32);
            Buffer.BlockCopy(b, 0, buf, 32, 32);
            return sha.ComputeHash(buf);
        }
    }
}
