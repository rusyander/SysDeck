// Windows Process Cleaner — «Загрузки», видео: свой разбор и запись Matroska/WebM (VP9, AV1, Opus, Vorbis без перекодирования).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// MF на чистой Windows WebM не читает (нужны кодеки из Store), а VP9/Opus из WebM в MP4 не переносятся — поэтому раздельные
// дорожки WebM (так их отдают сайты) склеиваются здесь: блоки копируются байт в байт, меняются только номер дорожки и
// отметка относительно нового кластера. Выход: заголовок EBML, Segment (SeekHead, Info с длительностью, Tracks,
// кластеры не длиннее 5 с, Cues по ключевым кадрам).
// Враждебный вход: размер каждого элемента сверяется с остатком родителя и файла, «неизвестный» размер допустим только у
// Segment и Cluster, TrackEntry не больше 16 МиБ, блок не больше 64 МиБ, дорожек не больше 64. Шифрование и сжатие блоков
// (ContentEncodings) — отказ.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class MdMkvTrack
    {
        public long Number;
        public int Type;                    // 1 видео, 2 звук, 17 субтитры
        public string CodecId = "";
        public long DefaultDurationNs;
        public bool Encoded;                // ContentEncodings: шифрование или сжатие блоков
        public byte[] Raw;                  // содержимое TrackEntry
    }

    internal sealed class MdMkvBlock
    {
        public int Track;                   // индекс в MdMkvReader.Tracks
        public long TimeNs;
        public bool Key;
        public int Frames;
        public bool Group;
        public byte[] Body;                 // после номера дорожки: отметка (2), флаги (1), данные
        public long DurationNs = -1;
        public long ReferenceNs;            // ReferenceBlock, если есть
        public bool HasReference;
        public byte[] Extras;               // прочие дети BlockGroup как есть (DiscardPadding и т. п.)
    }

    internal static class MdEbml
    {
        public const long IdEbml = 0x1A45DFA3, IdDocType = 0x4282, IdDocTypeReadVersion = 0x4285, IdEbmlReadVersion = 0x42F7;
        public const long IdSegment = 0x18538067, IdSeekHead = 0x114D9B74, IdSeek = 0x4DBB, IdSeekId = 0x53AB, IdSeekPosition = 0x53AC;
        public const long IdInfo = 0x1549A966, IdTimecodeScale = 0x2AD7B1, IdDuration = 0x4489, IdMuxingApp = 0x4D80, IdWritingApp = 0x5741;
        public const long IdTracks = 0x1654AE6B, IdTrackEntry = 0xAE, IdTrackNumber = 0xD7, IdTrackUid = 0x73C5, IdTrackType = 0x83;
        public const long IdCodecId = 0x86, IdDefaultDuration = 0x23E383, IdContentEncodings = 0x6D80;
        public const long IdCluster = 0x1F43B675, IdTimecode = 0xE7, IdSimpleBlock = 0xA3, IdBlockGroup = 0xA0, IdBlock = 0xA1;
        public const long IdBlockDuration = 0x9B, IdReferenceBlock = 0xFB;
        public const long IdCues = 0x1C53BB6B, IdCuePoint = 0xBB, IdCueTime = 0xB3, IdCueTrackPositions = 0xB7, IdCueTrack = 0xF7, IdCueClusterPosition = 0xF1;
        public const long IdVoid = 0xEC, IdTags = 0x1254C367, IdChapters = 0x1043A770, IdAttachments = 0x1941A469;

        // Длина vint по первому байту: число ведущих нулей + 1; 9 — недопустимо.
        public static int VintLength(int first)
        {
            for (int i = 0; i < 8; i++) if ((first & (0x80 >> i)) != 0) return i + 1;
            return 9;
        }

        // Ребёнок элемента в массиве: false — конец или порча (bad = true).
        public static bool Child(byte[] a, ref int p, int end, out long id, out long size, out int data, out bool bad)
        {
            id = 0;
            size = 0;
            data = 0;
            bad = false;
            if (p >= end) return false;
            int il = VintLength(a[p]);
            if (il > 4 || p + il >= end) { bad = true; return false; }
            for (int i = 0; i < il; i++) id = (id << 8) | a[p + i];
            int q = p + il;
            int sl = VintLength(a[q]);
            if (sl > 8 || q + sl > end) { bad = true; return false; }
            size = a[q] & (0xFF >> sl);
            for (int i = 1; i < sl; i++) size = (size << 8) | a[q + i];
            data = q + sl;
            if (size > end - data) { bad = true; return false; }
            p = data + (int)size;
            return true;
        }

        public static long UInt(byte[] a, int p, long size)
        {
            long v = 0;
            for (int i = 0; i < size && i < 8; i++) v = (v << 8) | a[p + i];
            return v;
        }

        public static long SInt(byte[] a, int p, long size)
        {
            if (size <= 0) return 0;
            long v = (sbyte)a[p];
            for (int i = 1; i < size && i < 8; i++) v = (v << 8) | a[p + i];
            return v;
        }

        // ---------- запись ----------
        public static void Id(Stream s, long id)
        {
            if (id > 0xFFFFFF) s.WriteByte((byte)(id >> 24));
            if (id > 0xFFFF) s.WriteByte((byte)(id >> 16));
            if (id > 0xFF) s.WriteByte((byte)(id >> 8));
            s.WriteByte((byte)id);
        }

        public static void Size(Stream s, long size)
        {
            int len = 1;
            while (len < 8 && size >= (1L << (7 * len)) - 1) len++;
            Size(s, size, len);
        }

        public static void Size(Stream s, long size, int len)
        {
            long v = size | (1L << (7 * len));
            for (int i = len - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }

        public static void UIntEl(Stream s, long id, long v)
        {
            int n = 1;
            while (n < 8 && (v >> (8 * n)) != 0) n++;
            Id(s, id);
            Size(s, n);
            for (int i = n - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }

        public static void SIntEl(Stream s, long id, long v)
        {
            int n = 1;
            while (n < 8 && (v < -(1L << (8 * n - 1)) || v >= (1L << (8 * n - 1)))) n++;
            Id(s, id);
            Size(s, n);
            for (int i = n - 1; i >= 0; i--) s.WriteByte((byte)(v >> (8 * i)));
        }

        public static void StrEl(Stream s, long id, string v)
        {
            byte[] b = Encoding.UTF8.GetBytes(v);
            BinEl(s, id, b);
        }

        public static void BinEl(Stream s, long id, byte[] b)
        {
            Id(s, id);
            Size(s, b.Length);
            s.Write(b, 0, b.Length);
        }

        public static void Float8(Stream s, double v)
        {
            long bits = BitConverter.DoubleToInt64Bits(v);
            for (int i = 7; i >= 0; i--) s.WriteByte((byte)(bits >> (8 * i)));
        }
    }

    // ------------------------------------------------------------------ //
    //  Чтение
    // ------------------------------------------------------------------ //
    internal sealed class MdMkvReader : IDisposable
    {
        private const long MaxTrackEntry = 16L << 20;
        private const long MaxBlock = 64L << 20;
        private const long MaxInfo = 1L << 20;

        private readonly FileStream _fs;
        private readonly string _name;
        private long _len, _segEnd, _clusterEnd, _clusterTc;
        private bool _inCluster, _opened;
        public readonly List<MdMkvTrack> Tracks = new List<MdMkvTrack>();
        public long TimecodeScale = 1000000;
        public string Error = "";

        public MdMkvReader(string path)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _name = Path.GetFileName(path);
        }

        public long Position { get { return _fs.Position; } }
        public void Dispose() { _fs.Dispose(); }

        private bool Bad(string why)
        {
            if (Error.Length == 0) Error = Tr.S("Повреждённый WebM (", "Damaged WebM (") + _name + "): " + why;
            return false;
        }

        // Заголовок элемента из файла: false — конец (Error пуст) или порча (Error заполнен).
        private bool Header(long limit, out long id, out long size, out long data)
        {
            id = 0;
            size = 0;
            data = 0;
            long start = _fs.Position;
            if (limit - start < 2) return false;
            int b = _fs.ReadByte();
            if (b < 0) return false;
            int il = MdEbml.VintLength(b);
            if (il > 4) return Bad("id @" + start);
            id = b;
            for (int i = 1; i < il; i++)
            {
                int n = _fs.ReadByte();
                if (n < 0) return Bad("id @" + start);
                id = (id << 8) | (uint)n;
            }
            int s = _fs.ReadByte();
            if (s < 0) return Bad("size @" + start);
            int sl = MdEbml.VintLength(s);
            if (sl > 8) return Bad("size @" + start);
            long mask = 0xFF >> sl;
            size = s & mask;
            bool unknown = size == mask;
            for (int i = 1; i < sl; i++)
            {
                int n = _fs.ReadByte();
                if (n < 0) return Bad("size @" + start);
                size = (size << 8) | (uint)n;
                unknown &= n == 0xFF;
            }
            data = _fs.Position;
            if (unknown) { size = -1; return true; }
            if (size > limit - data) return Bad("element 0x" + id.ToString("X") + " size " + size + " > remaining " + (limit - data));
            return true;
        }

        private byte[] Bytes(long size, long cap)
        {
            if (size < 0 || size > cap) { Bad("element too large: " + size); return null; }
            byte[] b = new byte[size];
            int got = 0;
            while (got < size)
            {
                int n = _fs.Read(b, got, (int)size - got);
                if (n <= 0) { Bad("unexpected end of file"); return null; }
                got += n;
            }
            return b;
        }

        private void Skip(long size) { _fs.Position += size; }

        public bool Open()
        {
            _opened = true;
            _len = _fs.Length;
            long id, size, data;
            if (!Header(_len, out id, out size, out data) || id != MdEbml.IdEbml || size < 0 || size > 4096)
                return Bad(Tr.S("нет заголовка EBML", "no EBML header"));
            byte[] h = Bytes(size, 4096);
            if (h == null) return false;
            string docType = "";
            int p = 0;
            long cid, csize;
            int cdata;
            bool bad;
            while (MdEbml.Child(h, ref p, h.Length, out cid, out csize, out cdata, out bad))
            {
                if (cid == MdEbml.IdDocType) docType = Encoding.ASCII.GetString(h, cdata, (int)csize).TrimEnd('\0');
                else if (cid == MdEbml.IdDocTypeReadVersion && MdEbml.UInt(h, cdata, csize) > 4) return Bad("DocTypeReadVersion");
                else if (cid == MdEbml.IdEbmlReadVersion && MdEbml.UInt(h, cdata, csize) > 1) return Bad("EBMLReadVersion");
            }
            if (bad) return Bad("EBML header");
            if (docType != "webm" && docType != "matroska") return Bad("DocType " + docType);
            while (true)
            {
                if (!Header(_len, out id, out size, out data)) return Error.Length > 0 ? false : Bad("no Segment");
                if (id == MdEbml.IdSegment) break;
                if (size < 0) return Bad("unknown size");
                Skip(size);
            }
            _segEnd = size < 0 ? _len : data + size;
            while (true)
            {
                long at = _fs.Position;
                if (!Header(_segEnd, out id, out size, out data))
                {
                    if (Error.Length > 0) return false;
                    break;
                }
                if (id == MdEbml.IdCluster) { _fs.Position = at; break; }
                if (size < 0) return Bad("unknown size of 0x" + id.ToString("X"));
                if (id == MdEbml.IdInfo)
                {
                    byte[] info = Bytes(size, MaxInfo);
                    if (info == null) return false;
                    p = 0;
                    while (MdEbml.Child(info, ref p, info.Length, out cid, out csize, out cdata, out bad))
                        if (cid == MdEbml.IdTimecodeScale) TimecodeScale = MdEbml.UInt(info, cdata, csize);
                    if (bad || TimecodeScale <= 0 || TimecodeScale > 1000000000L) return Bad("Info");
                }
                else if (id == MdEbml.IdTracks)
                {
                    byte[] tracks = Bytes(size, MaxTrackEntry * 4);
                    if (tracks == null) return false;
                    p = 0;
                    while (MdEbml.Child(tracks, ref p, tracks.Length, out cid, out csize, out cdata, out bad))
                    {
                        if (cid != MdEbml.IdTrackEntry) continue;
                        if (csize > MaxTrackEntry || Tracks.Count >= 64) return Bad("TrackEntry");
                        MdMkvTrack t = new MdMkvTrack();
                        t.Raw = new byte[csize];
                        Buffer.BlockCopy(tracks, cdata, t.Raw, 0, (int)csize);
                        int q = 0;
                        long tid, tsize;
                        int tdata;
                        bool tbad;
                        while (MdEbml.Child(t.Raw, ref q, t.Raw.Length, out tid, out tsize, out tdata, out tbad))
                        {
                            if (tid == MdEbml.IdTrackNumber) t.Number = MdEbml.UInt(t.Raw, tdata, tsize);
                            else if (tid == MdEbml.IdTrackType) t.Type = (int)MdEbml.UInt(t.Raw, tdata, tsize);
                            else if (tid == MdEbml.IdCodecId) t.CodecId = Encoding.ASCII.GetString(t.Raw, tdata, (int)tsize).TrimEnd('\0');
                            else if (tid == MdEbml.IdDefaultDuration) t.DefaultDurationNs = MdEbml.UInt(t.Raw, tdata, tsize);
                            else if (tid == MdEbml.IdContentEncodings) t.Encoded = true;
                        }
                        if (tbad || t.Number <= 0) return Bad("TrackEntry");
                        Tracks.Add(t);
                    }
                    if (bad) return Bad("Tracks");
                }
                else Skip(size);
            }
            if (Tracks.Count == 0) return Bad(Tr.S("нет дорожек", "no tracks"));
            return true;
        }

        private static bool TopLevel(long id)
        {
            return id == MdEbml.IdCluster || id == MdEbml.IdCues || id == MdEbml.IdTags || id == MdEbml.IdChapters
                || id == MdEbml.IdAttachments || id == MdEbml.IdSeekHead || id == MdEbml.IdInfo || id == MdEbml.IdTracks;
        }

        public bool Next(out MdMkvBlock block)
        {
            block = null;
            if (!_opened || Error.Length > 0) return false;
            while (true)
            {
                long id, size, data;
                if (_inCluster)
                {
                    long limit = _clusterEnd < 0 ? _segEnd : _clusterEnd;
                    long at = _fs.Position;
                    if (at >= limit || !Header(limit, out id, out size, out data))
                    {
                        if (Error.Length > 0) return false;
                        _inCluster = false;
                        _fs.Position = Math.Min(Math.Max(at, limit), _segEnd);
                        continue;
                    }
                    if (_clusterEnd < 0 && TopLevel(id)) { _fs.Position = at; _inCluster = false; continue; }
                    if (size < 0) return Bad("unknown size in Cluster");
                    if (id == MdEbml.IdTimecode)
                    {
                        byte[] v = Bytes(size, 8);
                        if (v == null) return false;
                        _clusterTc = MdEbml.UInt(v, 0, size);
                    }
                    else if (id == MdEbml.IdSimpleBlock)
                    {
                        byte[] body = Bytes(size, MaxBlock);
                        if (body == null) return false;
                        block = ParseBlock(body, 0, body.Length, true, false, null);
                        if (Error.Length > 0) return false;
                        if (block != null) return true;
                    }
                    else if (id == MdEbml.IdBlockGroup)
                    {
                        byte[] g = Bytes(size, MaxBlock + 1024);
                        if (g == null) return false;
                        int p = 0, blockAt = -1, blockLen = 0;
                        long cid, csize;
                        int cdata;
                        bool bad, hasRef = false;
                        long dur = -1, refTc = 0;
                        MemoryStream extras = new MemoryStream();
                        while (true)
                        {
                            int start = p;
                            if (!MdEbml.Child(g, ref p, g.Length, out cid, out csize, out cdata, out bad)) break;
                            if (cid == MdEbml.IdBlock) { blockAt = cdata; blockLen = (int)csize; }
                            else if (cid == MdEbml.IdBlockDuration) dur = MdEbml.UInt(g, cdata, csize);
                            else if (cid == MdEbml.IdReferenceBlock) { hasRef = true; refTc = MdEbml.SInt(g, cdata, csize); }
                            else extras.Write(g, start, p - start);
                        }
                        if (bad || blockAt < 0) return Bad("BlockGroup");
                        block = ParseBlock(g, blockAt, blockLen, false, hasRef, extras.ToArray());
                        if (Error.Length > 0) return false;
                        if (block != null)
                        {
                            if (dur >= 0) block.DurationNs = dur * TimecodeScale;
                            block.HasReference = hasRef;
                            block.ReferenceNs = refTc * TimecodeScale;
                            return true;
                        }
                    }
                    else Skip(size);
                }
                else
                {
                    if (_fs.Position >= _segEnd) return false;
                    if (!Header(_segEnd, out id, out size, out data)) return false;
                    if (id == MdEbml.IdCluster)
                    {
                        _inCluster = true;
                        _clusterEnd = size < 0 ? -1 : data + size;
                        _clusterTc = 0;
                        continue;
                    }
                    if (size < 0) return Bad("unknown size of 0x" + id.ToString("X"));
                    Skip(size);
                }
            }
        }

        private MdMkvBlock ParseBlock(byte[] a, int off, int len, bool simple, bool hasRef, byte[] extras)
        {
            if (len < 4) { Bad("block"); return null; }
            int vl = MdEbml.VintLength(a[off]);
            if (vl > 8 || vl + 3 > len) { Bad("block"); return null; }
            long number = a[off] & (0xFF >> vl);
            for (int i = 1; i < vl; i++) number = (number << 8) | a[off + i];
            int track = -1;
            for (int i = 0; i < Tracks.Count; i++) if (Tracks[i].Number == number) { track = i; break; }
            if (track < 0) return null;
            short rel = (short)((a[off + vl] << 8) | a[off + vl + 1]);
            int flags = a[off + vl + 2];
            int lacing = (flags >> 1) & 3;
            int frames = 1;
            if (lacing != 0)
            {
                if (vl + 3 >= len) { Bad("lacing"); return null; }
                frames = a[off + vl + 3] + 1;
            }
            MdMkvBlock b = new MdMkvBlock();
            b.Track = track;
            b.TimeNs = (_clusterTc + rel) * TimecodeScale;
            b.Key = simple ? (flags & 0x80) != 0 : !hasRef;
            b.Frames = frames;
            b.Group = !simple;
            b.Body = new byte[len - vl];
            Buffer.BlockCopy(a, off + vl, b.Body, 0, b.Body.Length);
            b.Extras = extras;
            return b;
        }
    }

    // ------------------------------------------------------------------ //
    //  Запись
    // ------------------------------------------------------------------ //
    internal sealed class MdMkvWriter : IDisposable
    {
        private const int SeekHeadReserve = 100;
        private const long ClusterMaxMs = 5000;
        private const long ClusterMaxBytes = 5L << 20;

        private readonly FileStream _fs;
        private long _segSizeAt, _segData, _seekAt, _durationAt, _infoPos, _tracksPos;
        private readonly MemoryStream _cluster = new MemoryStream();
        private long _clusterTc = -1;
        private readonly List<long[]> _cues = new List<long[]>();   // время мс, дорожка, позиция кластера
        private bool _hasVideo;
        private long _maxEndMs;

        public MdMkvWriter(string path)
        {
            _fs = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16);
        }

        public void Dispose() { _fs.Dispose(); }

        public void Begin(List<byte[]> trackEntries, bool hasVideo)
        {
            _hasVideo = hasVideo;
            MemoryStream h = new MemoryStream();
            MdEbml.UIntEl(h, 0x4286, 1);
            MdEbml.UIntEl(h, MdEbml.IdEbmlReadVersion, 1);
            MdEbml.UIntEl(h, 0x42F2, 4);
            MdEbml.UIntEl(h, 0x42F3, 8);
            MdEbml.StrEl(h, MdEbml.IdDocType, "webm");
            MdEbml.UIntEl(h, 0x4287, 4);
            MdEbml.UIntEl(h, MdEbml.IdDocTypeReadVersion, 2);
            MdEbml.BinEl(_fs, MdEbml.IdEbml, h.ToArray());
            MdEbml.Id(_fs, MdEbml.IdSegment);
            _segSizeAt = _fs.Position;
            MdEbml.Size(_fs, 0, 8);
            _segData = _fs.Position;
            _seekAt = _fs.Position;
            MdEbml.Id(_fs, MdEbml.IdVoid);
            MdEbml.Size(_fs, SeekHeadReserve - 2, 1);
            _fs.Write(new byte[SeekHeadReserve - 2], 0, SeekHeadReserve - 2);
            _infoPos = _fs.Position - _segData;
            MemoryStream info = new MemoryStream();
            MdEbml.UIntEl(info, MdEbml.IdTimecodeScale, 1000000);
            MdEbml.StrEl(info, MdEbml.IdMuxingApp, "WindowsProcessCleaner");
            MdEbml.StrEl(info, MdEbml.IdWritingApp, "WindowsProcessCleaner");
            MdEbml.Id(info, MdEbml.IdDuration);
            MdEbml.Size(info, 8);
            long durationInInfo = info.Position;
            MdEbml.Float8(info, 0);
            MdEbml.Id(_fs, MdEbml.IdInfo);
            MdEbml.Size(_fs, info.Length);
            _durationAt = _fs.Position + durationInInfo;
            info.WriteTo(_fs);
            _tracksPos = _fs.Position - _segData;
            MemoryStream tracks = new MemoryStream();
            foreach (byte[] e in trackEntries) MdEbml.BinEl(tracks, MdEbml.IdTrackEntry, e);
            MdEbml.BinEl(_fs, MdEbml.IdTracks, tracks.ToArray());
        }

        // Номер выходной дорожки < 127 (один байт vint). timeMs — от начала файла.
        public void Add(int track, bool video, MdMkvBlock b, long timeMs, long durationMs, long referenceMs)
        {
            bool fresh = _clusterTc < 0 || timeMs - _clusterTc > 30000 || timeMs < _clusterTc
                || _cluster.Length >= ClusterMaxBytes
                || timeMs - _clusterTc >= ClusterMaxMs
                || (video && b.Key && timeMs - _clusterTc >= 1000);
            if (fresh)
            {
                FlushCluster();
                _clusterTc = timeMs;
                if (!_hasVideo || (video && b.Key)) _cues.Add(new long[] { timeMs, track, _fs.Position - _segData });
            }
            short rel = (short)(timeMs - _clusterTc);
            byte[] body = b.Body;
            body[0] = (byte)(rel >> 8);
            body[1] = (byte)rel;
            if (!b.Group)
            {
                MdEbml.Id(_cluster, MdEbml.IdSimpleBlock);
                MdEbml.Size(_cluster, 1 + body.Length);
                _cluster.WriteByte((byte)(0x80 | track));
                _cluster.Write(body, 0, body.Length);
            }
            else
            {
                MemoryStream g = new MemoryStream();
                MdEbml.Id(g, MdEbml.IdBlock);
                MdEbml.Size(g, 1 + body.Length);
                g.WriteByte((byte)(0x80 | track));
                g.Write(body, 0, body.Length);
                if (b.DurationNs >= 0) MdEbml.UIntEl(g, MdEbml.IdBlockDuration, durationMs);
                if (b.HasReference) MdEbml.SIntEl(g, MdEbml.IdReferenceBlock, referenceMs);
                if (b.Extras != null) g.Write(b.Extras, 0, b.Extras.Length);
                MdEbml.BinEl(_cluster, MdEbml.IdBlockGroup, g.ToArray());
            }
            if (timeMs + durationMs > _maxEndMs) _maxEndMs = timeMs + durationMs;
        }

        public void NoteEnd(long endMs) { if (endMs > _maxEndMs) _maxEndMs = endMs; }
        public long DurationMs { get { return _maxEndMs; } }

        private void FlushCluster()
        {
            if (_clusterTc < 0 || _cluster.Length == 0) return;
            MemoryStream c = new MemoryStream();
            MdEbml.UIntEl(c, MdEbml.IdTimecode, _clusterTc);
            _cluster.WriteTo(c);
            MdEbml.Id(_fs, MdEbml.IdCluster);
            MdEbml.Size(_fs, c.Length);
            c.WriteTo(_fs);
            _cluster.SetLength(0);
        }

        public void Finish()
        {
            FlushCluster();
            long cuesPos = _fs.Position - _segData;
            MemoryStream cues = new MemoryStream();
            foreach (long[] cue in _cues)
            {
                MemoryStream pos = new MemoryStream();
                MdEbml.UIntEl(pos, MdEbml.IdCueTrack, cue[1]);
                MdEbml.UIntEl(pos, MdEbml.IdCueClusterPosition, cue[2]);
                MemoryStream point = new MemoryStream();
                MdEbml.UIntEl(point, MdEbml.IdCueTime, cue[0]);
                MdEbml.BinEl(point, MdEbml.IdCueTrackPositions, pos.ToArray());
                MdEbml.BinEl(cues, MdEbml.IdCuePoint, point.ToArray());
            }
            if (_cues.Count > 0) MdEbml.BinEl(_fs, MdEbml.IdCues, cues.ToArray());
            long end = _fs.Position;
            // SeekHead в зарезервированное место, остаток — Void
            MemoryStream seek = new MemoryStream();
            long[][] entries = _cues.Count > 0
                ? new long[][] { new long[] { MdEbml.IdInfo, _infoPos }, new long[] { MdEbml.IdTracks, _tracksPos }, new long[] { MdEbml.IdCues, cuesPos } }
                : new long[][] { new long[] { MdEbml.IdInfo, _infoPos }, new long[] { MdEbml.IdTracks, _tracksPos } };
            foreach (long[] e in entries)
            {
                MemoryStream one = new MemoryStream();
                MemoryStream idBytes = new MemoryStream();
                MdEbml.Id(idBytes, e[0]);
                MdEbml.BinEl(one, MdEbml.IdSeekId, idBytes.ToArray());
                MdEbml.Id(one, MdEbml.IdSeekPosition);
                MdEbml.Size(one, 8);
                for (int i = 7; i >= 0; i--) one.WriteByte((byte)(e[1] >> (8 * i)));
                MdEbml.BinEl(seek, MdEbml.IdSeek, one.ToArray());
            }
            MemoryStream head = new MemoryStream();
            MdEbml.BinEl(head, MdEbml.IdSeekHead, seek.ToArray());
            int rest = SeekHeadReserve - (int)head.Length;
            if (rest >= 2)
            {
                MdEbml.Id(head, MdEbml.IdVoid);
                MdEbml.Size(head, rest - 2, 1);
                head.Write(new byte[rest - 2], 0, rest - 2);
                _fs.Position = _seekAt;
                head.WriteTo(_fs);
            }
            _fs.Position = _durationAt;
            MdEbml.Float8(_fs, _maxEndMs);
            _fs.Position = _segSizeAt;
            MdEbml.Size(_fs, end - _segData, 8);
            _fs.Position = end;
            _fs.Flush();
        }
    }

    // ------------------------------------------------------------------ //
    //  Склейка входов WebM
    // ------------------------------------------------------------------ //
    internal static class MdMkv
    {
        private sealed class Lane
        {
            public MdMkvReader Reader;
            public int[] OutTrack;          // индекс дорожки входа → номер на выходе (0 — не берём)
            public MdMkvBlock Head;
        }

        public static bool Merge(List<MdInput> av, string tmp, MdMuxJob job, MdProgress progress, MdMuxResult res, out long base100, out string error)
        {
            base100 = 0;
            error = null;
            List<Lane> lanes = new List<Lane>();
            try
            {
                foreach (MdInput input in av)
                {
                    if (input.Layout != MdLayout.WebM) { error = Tr.S("В WebM собираются только дорожки WebM — выберите MP4 для этой пары", "Only WebM tracks can be assembled into WebM — choose MP4 for this pair"); return false; }
                    Lane lane = new Lane();
                    lane.Reader = new MdMkvReader(input.Path);
                    lanes.Add(lane);
                    if (!lane.Reader.Open()) { error = lane.Reader.Error; return false; }
                }
                // Порядок дорожек на выходе: сначала видео, затем звук — в порядке входов.
                List<byte[]> entries = new List<byte[]>();
                List<bool> isVideo = new List<bool>();
                List<long> defaultNs = new List<long>();
                for (int pass = 1; pass <= 2; pass++)
                    for (int li = 0; li < lanes.Count; li++)
                    {
                        Lane lane = lanes[li];
                        if (lane.OutTrack == null) lane.OutTrack = new int[lane.Reader.Tracks.Count];
                        MdTrackKind kind = av[li].Kind;
                        for (int ti = 0; ti < lane.Reader.Tracks.Count; ti++)
                        {
                            MdMkvTrack t = lane.Reader.Tracks[ti];
                            if (t.Type != pass) continue;
                            if ((pass == 1 && kind == MdTrackKind.Audio) || (pass == 2 && kind == MdTrackKind.Video)) continue;
                            if (t.Encoded) { error = Tr.S("Дорожка WebM зашифрована или сжата — не собирается", "The WebM track is encrypted or compressed — cannot assemble"); return false; }
                            if (entries.Count >= 126) break;
                            entries.Add(RewriteEntry(t.Raw, entries.Count + 1));
                            isVideo.Add(pass == 1);
                            defaultNs.Add(t.DefaultDurationNs);
                            lane.OutTrack[ti] = entries.Count;
                        }
                    }
                if (entries.Count == 0) { error = Tr.S("Во входах WebM нет дорожек видео или звука", "No video or audio tracks in the WebM inputs"); return false; }
                long baseNs = long.MaxValue;
                foreach (Lane lane in lanes)
                {
                    Advance(lane);
                    if (lane.Reader.Error.Length > 0) { error = lane.Reader.Error; return false; }
                    if (lane.Head != null && lane.Head.TimeNs < baseNs) baseNs = lane.Head.TimeNs;
                }
                if (baseNs == long.MaxValue) { error = Tr.S("Во входах WebM нет ни одного кадра", "No frames in the WebM inputs"); return false; }
                base100 = baseNs / 100;
                bool hasVideo = isVideo.Contains(true);
                long[] lastMs = new long[entries.Count + 1], lastDelta = new long[entries.Count + 1];
                for (int i = 0; i < lastMs.Length; i++) lastMs[i] = -1;
                using (MdMkvWriter w = new MdMkvWriter(tmp))
                {
                    w.Begin(entries, hasVideo);
                    int count = 0;
                    while (true)
                    {
                        Lane pick = null;
                        foreach (Lane lane in lanes)
                            if (lane.Head != null && (pick == null || lane.Head.TimeNs < pick.Head.TimeNs)) pick = lane;
                        if (pick == null) break;
                        MdMkvBlock b = pick.Head;
                        int track = pick.OutTrack[b.Track];
                        bool video = isVideo[track - 1];
                        long timeMs = Math.Max(0, (b.TimeNs - baseNs + 500000) / 1000000);
                        long durMs = b.DurationNs >= 0 ? (b.DurationNs + 500000) / 1000000 : 0;
                        long refMs = b.HasReference ? (b.ReferenceNs + (b.ReferenceNs >= 0 ? 500000 : -500000)) / 1000000 : 0;
                        if (lastMs[track] >= 0 && timeMs > lastMs[track]) lastDelta[track] = timeMs - lastMs[track];
                        lastMs[track] = timeMs;
                        w.Add(track, video, b, timeMs, durMs, refMs);
                        long frameMs = defaultNs[track - 1] > 0 ? (defaultNs[track - 1] * b.Frames + 500000) / 1000000 : lastDelta[track];
                        w.NoteEnd(timeMs + Math.Max(durMs, frameMs));
                        if (video) res.VideoFrames += b.Frames; else res.AudioFrames += b.Frames;
                        Advance(pick);
                        if (pick.Reader.Error.Length > 0) { error = pick.Reader.Error; return false; }
                        if ((++count & 63) == 0)
                        {
                            if (job.Cancel != null && job.Cancel()) { error = MdProgress.Cancelled; return false; }
                            long pos = 0;
                            foreach (Lane lane in lanes) pos += lane.Reader.Position;
                            progress.Report(pos);
                        }
                    }
                    w.Finish();
                    res.DurationMs = w.DurationMs;
                }
                return true;
            }
            finally
            {
                foreach (Lane lane in lanes) lane.Reader.Dispose();
            }
        }

        private static void Advance(Lane lane)
        {
            lane.Head = null;
            MdMkvBlock b;
            while (lane.Reader.Next(out b))
                if (lane.OutTrack[b.Track] > 0) { lane.Head = b; return; }
        }

        // TrackEntry с новым номером и UID; остальные дети — как были.
        private static byte[] RewriteEntry(byte[] raw, int number)
        {
            MemoryStream m = new MemoryStream();
            MdEbml.UIntEl(m, MdEbml.IdTrackNumber, number);
            MdEbml.UIntEl(m, MdEbml.IdTrackUid, number);
            int p = 0;
            long id, size;
            int data;
            bool bad;
            while (true)
            {
                int start = p;
                if (!MdEbml.Child(raw, ref p, raw.Length, out id, out size, out data, out bad)) break;
                if (id == MdEbml.IdTrackNumber || id == MdEbml.IdTrackUid) continue;
                m.Write(raw, start, p - start);
            }
            return m.ToArray();
        }
    }
}
