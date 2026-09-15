// SysDeck — «Загрузки», видео: разбор MPEG-TS (PAT/PMT, PES, отметки времени, H.264/HEVC, AAC, MP3).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Media Foundation читает TS, но отдаёт типы, которые приёмник MP4 не берёт (H264_ES, AAC с заголовками ADTS), — поэтому
// TS разбираем сами и отдаём склейке готовые кадры: H.264/HEVC Annex B (заголовок последовательности из SPS/PPS/VPS,
// ключевой — IDR/IRAP), AAC без заголовков ADTS с AudioSpecificConfig, MP3 как есть.
// Отметки 33-битные: переполнение разворачивается по ближайшему к предыдущему значению; разрыв (шаг назад или вперёд
// больше 10 с) сдвигается так, чтобы время шло дальше без провала. Сдвиг общий для дорожек, у которых разрыв пришёлся
// на одно и то же место исходной шкалы, — синхрон звука и видео сохраняется.
// Враждебный вход: пакет строго 188 байт, PES не больше 64 МиБ, секции PSI не больше 4 КиБ, CRC секций проверяется.
using System;
using System.Collections.Generic;
using System.IO;

namespace SysDeck.Downloads
{
    internal enum MdCodec { None, H264, Hevc, Aac, Mp3 }

    // Кадр дорожки: отметки в 90 кГц, развёрнутые и без разрывов; Data — готовая к записи полезная нагрузка.
    internal sealed class MdAu
    {
        public int Track;
        public byte[] Data;
        public long Pts, Dts, Dur;
        public bool Key;
    }

    // Элементарная дорожка разборщика (TS, ADTS, MP3): кодек и то, что нужно для типа приёмника.
    internal sealed class MdEsTrack
    {
        public int Index;
        public MdCodec Codec;
        public bool Video { get { return Codec == MdCodec.H264 || Codec == MdCodec.Hevc; } }
        public byte[] SeqHeader;            // Annex B: [VPS] SPS PPS с 4-байтными стартовыми кодами
        public int Width, Height;
        public int SampleRate, Channels;
        public byte[] Asc;                  // AudioSpecificConfig (AAC)
        public int Mp3Bitrate, Mp3FrameBytes, Mp3Layer;     // MPEG audio: кбит/с, байт в кадре, слой (MP4 берёт только слой III)
        public long FrameDur;               // оценка длительности кадра, 90 кГц
        public long Frames, Bytes;          // выдано кадров и байт полезной нагрузки
        public bool Configured
        {
            get
            {
                if (Video) return SeqHeader != null && Width > 0 && Height > 0;
                if (Codec == MdCodec.Aac) return Asc != null && SampleRate > 0;
                if (Codec == MdCodec.Mp3) return SampleRate > 0 && Mp3Bitrate > 0;
                return false;
            }
        }
    }

    internal interface IMdAuReader : IDisposable
    {
        List<MdEsTrack> Tracks { get; }
        bool Next(out MdAu au);             // false — конец данных или ошибка (тогда Error не пуст)
        string Error { get; }
        long Position { get; }
        long Length { get; }
        MdTimeline Timeline { get; }
    }

    // ------------------------------------------------------------------ //
    //  Шкала времени 90 кГц: разворот 33 бит и разрывы
    // ------------------------------------------------------------------ //
    internal sealed class MdTimeline
    {
        public const long Wrap = 1L << 33;
        public const long JumpLimit = 10 * 90000L;
        private const long Jitter = 9000;   // шаг назад до 100 мс — неточность упаковщика, не разрыв

        private sealed class Shift { public long OldOffset, RawNew, NewOffset; }
        private sealed class State { public bool HasLast; public bool HasRaw; public long LastRaw, LastDts, LastDur, Offset; }

        private readonly List<Shift> _shifts = new List<Shift>();
        private readonly Dictionary<int, State> _state = new Dictionary<int, State>();
        private bool _hasRef;
        private long _ref;

        public int Discontinuities;

        // Опора разворота для первой отметки: первая отметка другого входа той же трансляции.
        public void Seed(long unwrapped)
        {
            if (!_hasRef) { _hasRef = true; _ref = unwrapped; }
        }

        public bool HasReference { get { return _hasRef; } }
        public long Reference { get { return _ref; } }

        public static long Unwrap(long raw, long reference)
        {
            raw &= Wrap - 1;
            long x = reference - raw + Wrap / 2;
            long k = x >= 0 ? x / Wrap : -((-x + Wrap - 1) / Wrap);
            return raw + k * Wrap;
        }

        private State Get(int track)
        {
            State s;
            if (!_state.TryGetValue(track, out s)) { s = new State(); _state[track] = s; }
            return s;
        }

        // Сырые 33-битные PTS/DTS → итоговые. dur — длительность кадра, если известна (для продолжения после разрыва).
        public void Map(int track, long rawPts, long rawDts, long dur, out long pts, out long dts)
        {
            State s = Get(track);
            long refv = s.HasRaw ? s.LastRaw : (_hasRef ? _ref : (rawDts & (Wrap - 1)));
            long du = Unwrap(rawDts, refv);
            long pu = Unwrap(rawPts, du);
            s.HasRaw = true;
            s.LastRaw = du;
            _hasRef = true;
            _ref = du;
            dts = du + s.Offset;
            if (s.HasLast && (dts < s.LastDts - Jitter || dts > s.LastDts + JumpLimit))
            {
                long expected = s.LastDts + Math.Max(1, s.LastDur);
                Shift hit = null;
                foreach (Shift x in _shifts)
                    if (x.OldOffset == s.Offset && Math.Abs(x.RawNew - du) <= JumpLimit) { hit = x; break; }
                if (hit == null)
                {
                    hit = new Shift();
                    hit.OldOffset = s.Offset;
                    hit.RawNew = du;
                    hit.NewOffset = expected - du;
                    _shifts.Add(hit);
                    Discontinuities++;
                }
                s.Offset = hit.NewOffset;
                dts = du + s.Offset;
            }
            pts = pu + s.Offset;
            Advance(track, dts, dur);
        }

        // Кадр выдан: следующее сравнение идёт с ним.
        public void Advance(int track, long dts, long dur)
        {
            State s = Get(track);
            if (s.HasLast && dur <= 0)
            {
                long d = dts - s.LastDts;
                if (d > 0 && d <= JumpLimit) dur = d;
            }
            s.HasLast = true;
            s.LastDts = dts;
            if (dur > 0) s.LastDur = dur;
        }
    }

    // ------------------------------------------------------------------ //
    //  Растущий буфер без лишних копий
    // ------------------------------------------------------------------ //
    internal sealed class MdBuf
    {
        public byte[] A = new byte[256];
        public int N;

        public void Add(byte[] src, int off, int count)
        {
            if (count <= 0) return;
            if (N + count > A.Length)
            {
                int cap = A.Length;
                while (cap < N + count) cap = cap < (1 << 30) ? cap * 2 : N + count;
                Array.Resize(ref A, cap);
            }
            Buffer.BlockCopy(src, off, A, N, count);
            N += count;
        }

        public void Consume(int count)
        {
            if (count >= N) { N = 0; return; }
            Buffer.BlockCopy(A, count, A, 0, N - count);
            N -= count;
        }

        public void Clear() { N = 0; }
    }

    // ------------------------------------------------------------------ //
    //  Разборщик MPEG-TS
    // ------------------------------------------------------------------ //
    internal sealed class MdTsReader : IMdAuReader
    {
        public const int PacketSize = 188;
        public const int MaxPes = 64 << 20;
        private const int MaxSection = 4096;
        private const int SyncSearchLimit = 1 << 20;

        private sealed class PidState
        {
            public int Pid;
            public int Cc = -1;
            public bool IsPat, IsPmt;
            public int Track = -1;          // индекс дорожки или -1
            public readonly MdBuf Buf = new MdBuf();
            public bool Started, Damaged;
            public byte[] LastPmt;
        }

        // Состояние дорожки, которое наружу не нужно.
        private sealed class EsState
        {
            public int Pid = -1;
            public bool Bound;
            // видео: кадр ждёт следующего, чтобы узнать длительность
            public MdAu Pending;
            public MdBuf PendingRaw;
            public bool SeenKey;
            public byte[] Vps, Sps, Pps;
            public long LastDts = long.MinValue;
            // звук
            public readonly MdBuf Audio = new MdBuf();
            public long PesTime;
            public long PesFrames;
            public long NextTime = long.MinValue;
        }

        private readonly FileStream _fs;
        private readonly bool _allowTruncated;
        private readonly byte[] _pkt = new byte[PacketSize];
        private readonly Dictionary<int, PidState> _pids = new Dictionary<int, PidState>();
        private readonly List<MdEsTrack> _tracks = new List<MdEsTrack>();
        private readonly List<EsState> _es = new List<EsState>();
        private readonly Queue<MdAu> _out = new Queue<MdAu>();
        private readonly MdTimeline _timeline = new MdTimeline();
        private string _error = "";
        private bool _eof, _flushed, _synced, _tailPartial, _frozen;
        private int _pmtPid = -1;
        private long _pos;

        public int ContinuityErrors, DroppedPes, LostSyncBytes;

        public MdTsReader(string path, bool allowTruncated)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _allowTruncated = allowTruncated;
            PidState pat = new PidState();
            pat.Pid = 0;
            pat.IsPat = true;
            _pids[0] = pat;
        }

        public List<MdEsTrack> Tracks { get { return _tracks; } }
        public string Error { get { return _error; } }
        public long Position { get { return _pos; } }
        public long Length { get { try { return _fs.Length; } catch (IOException) { return 0; } } }
        public MdTimeline Timeline { get { return _timeline; } }
        public int Discontinuities { get { return _timeline.Discontinuities; } }

        // После начала записи новые дорожки не заводятся (приёмнику их уже не добавить).
        public void FreezeTracks() { _frozen = true; }

        public void Dispose() { _fs.Dispose(); }

        public bool Next(out MdAu au)
        {
            au = null;
            while (_out.Count == 0)
            {
                if (_error.Length > 0) return false;
                if (_eof)
                {
                    if (_flushed) return false;
                    _flushed = true;
                    FlushAll();
                    continue;
                }
                if (!Pump()) _eof = true;
            }
            if (_error.Length > 0) return false;
            au = _out.Dequeue();
            return true;
        }

        // ---------- пакеты ----------
        private bool Pump()
        {
            if (!_synced)
            {
                long at = FindSync(0, SyncSearchLimit);
                if (at < 0) { _error = Tr.S("Файл не похож на MPEG-TS: нет синхробайта 0x47 через каждые 188 байт", "The file does not look like MPEG-TS: no 0x47 sync byte every 188 bytes"); return false; }
                _fs.Position = at;
                _pos = at;
                _synced = true;
            }
            int got = ReadFull(_pkt, PacketSize);
            if (got == 0) return false;
            if (got < PacketSize) { _tailPartial = true; _pos += got; return false; }
            if (_pkt[0] != 0x47)
            {
                long at = FindSync(_pos + 1, long.MaxValue);
                if (at < 0) { LostSyncBytes += (int)Math.Min(int.MaxValue, Length - _pos); _tailPartial = true; _pos = Length; return false; }
                LostSyncBytes += (int)Math.Min(int.MaxValue, at - _pos);
                foreach (PidState p in _pids.Values) if (p.Started) p.Damaged = true;
                _fs.Position = at;
                _pos = at;
                return true;
            }
            _pos += PacketSize;
            Packet(_pkt);
            return true;
        }

        private int ReadFull(byte[] buf, int count)
        {
            int total = 0;
            while (total < count)
            {
                int n = _fs.Read(buf, total, count - total);
                if (n <= 0) break;
                total += n;
            }
            return total;
        }

        // Синхронизация: 0x47 на k, k+188, k+376 (сколько пакетов есть в файле).
        private long FindSync(long from, long limit)
        {
            long len = Length;
            byte[] win = new byte[1 << 16];
            long start = from;
            while (start < len && start - from < limit)
            {
                _fs.Position = start;
                int n = ReadFull(win, win.Length);
                if (n <= 0) break;
                for (int i = 0; i < n; i++)
                {
                    if (win[i] != 0x47) continue;
                    long abs = start + i;
                    int ok = 0, need = 0;
                    for (int k = 1; k <= 2; k++)
                    {
                        long at = abs + k * PacketSize;
                        if (at >= len) break;
                        need++;
                        if (ByteAt(win, start, n, at) == 0x47) ok++;
                    }
                    if (ok == need && (need > 0 || len - abs <= PacketSize)) { return abs; }
                }
                if (n < win.Length) break;
                start += n - 2 * PacketSize;
            }
            return -1;
        }

        private int ByteAt(byte[] win, long winStart, int winLen, long at)
        {
            long rel = at - winStart;
            if (rel >= 0 && rel < winLen) return win[rel];
            long keep = _fs.Position;
            _fs.Position = at;
            int b = _fs.ReadByte();
            _fs.Position = keep;
            return b;
        }

        private void Packet(byte[] p)
        {
            int pid = ((p[1] & 0x1F) << 8) | p[2];
            if (pid == 0x1FFF) return;
            PidState st;
            if (!_pids.TryGetValue(pid, out st)) return;
            bool transportError = (p[1] & 0x80) != 0;
            bool pusi = (p[1] & 0x40) != 0;
            int afc = (p[3] >> 4) & 3;
            int cc = p[3] & 0x0F;
            int off = 4;
            bool discontinuity = false;
            if ((afc & 2) != 0)
            {
                int al = p[4];
                if (al > 183) { if (st.Started) st.Damaged = true; return; }
                if (al > 0) discontinuity = (p[5] & 0x80) != 0;
                off = 5 + al;
            }
            bool hasPayload = (afc & 1) != 0 && off < PacketSize;
            if ((afc & 1) != 0)
            {
                if (st.Cc >= 0 && !discontinuity)
                {
                    if (cc == st.Cc) return;                       // повтор пакета
                    if (cc != ((st.Cc + 1) & 0x0F)) { ContinuityErrors++; if (st.Started) st.Damaged = true; }
                }
                st.Cc = cc;
            }
            if (transportError) { if (st.Started) st.Damaged = true; return; }
            if (!hasPayload) return;
            if (st.IsPat || st.IsPmt) { Section(st, p, off, pusi); return; }
            if (pusi)
            {
                FinishPes(st, false);
                st.Started = true;
                st.Damaged = false;
                st.Buf.Clear();
            }
            if (!st.Started) return;
            if (st.Buf.N + (PacketSize - off) > MaxPes) { st.Started = false; DroppedPes++; st.Buf.Clear(); return; }
            st.Buf.Add(p, off, PacketSize - off);
            if (st.Buf.N >= 6)
            {
                int pesLen = (st.Buf.A[4] << 8) | st.Buf.A[5];
                if (pesLen != 0 && st.Buf.N >= 6 + pesLen) FinishPes(st, false);
            }
        }

        // ---------- PSI ----------
        private void Section(PidState st, byte[] p, int off, bool pusi)
        {
            if (pusi)
            {
                int pointer = p[off];
                int start = off + 1 + pointer;
                if (start >= PacketSize) { st.Started = false; return; }
                st.Buf.Clear();
                st.Started = true;
                st.Buf.Add(p, start, PacketSize - start);
            }
            else if (st.Started)
            {
                if (st.Buf.N + PacketSize - off > MaxSection) { st.Started = false; return; }
                st.Buf.Add(p, off, PacketSize - off);
            }
            if (!st.Started || st.Buf.N < 3) return;
            if (st.Buf.A[0] == 0xFF) { st.Started = false; return; }
            int total = 3 + (((st.Buf.A[1] & 0x0F) << 8) | st.Buf.A[2]);
            if (total > MaxSection || total < 12) { st.Started = false; return; }
            if (st.Buf.N < total) return;
            st.Started = false;
            if (Crc32(st.Buf.A, 0, total) != 0) return;
            if (st.IsPat) Pat(st.Buf.A, total);
            else Pmt(st, st.Buf.A, total);
        }

        private void Pat(byte[] s, int total)
        {
            if (s[0] != 0) return;
            for (int i = 8; i + 4 <= total - 4; i += 4)
            {
                int program = (s[i] << 8) | s[i + 1];
                int pid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
                if (program == 0) continue;
                if (pid != _pmtPid)
                {
                    PidState old;
                    if (_pmtPid >= 0 && _pids.TryGetValue(_pmtPid, out old) && old.IsPmt) _pids.Remove(_pmtPid);
                    _pmtPid = pid;
                    PidState pm = new PidState();
                    pm.Pid = pid;
                    pm.IsPmt = true;
                    _pids[pid] = pm;
                }
                return;                                             // берём первую программу
            }
        }

        private void Pmt(PidState st, byte[] s, int total)
        {
            if (s[0] != 2) return;
            byte[] copy = new byte[total];
            Buffer.BlockCopy(s, 0, copy, 0, total);
            if (st.LastPmt != null && Same(st.LastPmt, copy)) return;
            st.LastPmt = copy;
            int infoLen = ((s[10] & 0x0F) << 8) | s[11];
            int i = 12 + infoLen;
            foreach (EsState e in _es) e.Bound = false;
            List<int[]> found = new List<int[]>();
            while (i + 5 <= total - 4)
            {
                int type = s[i];
                int pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
                int esLen = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
                i += 5 + esLen;
                MdCodec codec = CodecOf(type);
                if (codec != MdCodec.None) found.Add(new int[] { pid, (int)codec });
            }
            foreach (int[] f in found)
            {
                MdCodec codec = (MdCodec)f[1];
                int index = -1;
                for (int k = 0; k < _tracks.Count; k++)
                    if (_tracks[k].Codec == codec && !_es[k].Bound) { index = k; break; }
                if (index < 0)
                {
                    if (_frozen) continue;
                    MdEsTrack t = new MdEsTrack();
                    t.Index = _tracks.Count;
                    t.Codec = codec;
                    _tracks.Add(t);
                    _es.Add(new EsState());
                    index = t.Index;
                }
                EsState es = _es[index];
                es.Bound = true;
                if (es.Pid != f[0])
                {
                    PidState old;
                    if (es.Pid >= 0 && _pids.TryGetValue(es.Pid, out old) && old.Track == index)
                    {
                        FinishPes(old, false);
                        _pids.Remove(es.Pid);
                    }
                    es.Pid = f[0];
                    PidState ps;
                    if (!_pids.TryGetValue(f[0], out ps) || ps.IsPat || ps.IsPmt)
                    {
                        if (ps != null && (ps.IsPat || ps.IsPmt)) continue;
                        ps = new PidState();
                        ps.Pid = f[0];
                        _pids[f[0]] = ps;
                    }
                    ps.Track = index;
                }
            }
        }

        private static MdCodec CodecOf(int streamType)
        {
            switch (streamType)
            {
                case 0x1B: return MdCodec.H264;
                case 0x24: return MdCodec.Hevc;
                case 0x0F: return MdCodec.Aac;
                case 0x03:
                case 0x04: return MdCodec.Mp3;
                default: return MdCodec.None;
            }
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static uint[] _crcTable;

        public static uint Crc32(byte[] a, int off, int len)
        {
            if (_crcTable == null)
            {
                uint[] t = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i << 24;
                    for (int k = 0; k < 8; k++) c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
                    t[i] = c;
                }
                _crcTable = t;
            }
            uint crc = 0xFFFFFFFF;
            for (int i = off; i < off + len; i++) crc = (crc << 8) ^ _crcTable[((crc >> 24) ^ a[i]) & 0xFF];
            return crc;
        }

        // ---------- PES ----------
        private void FinishPes(PidState st, bool atEof)
        {
            if (!st.Started) return;
            st.Started = false;
            if (st.Track < 0) return;
            if (st.Damaged) { DroppedPes++; return; }
            byte[] a = st.Buf.A;
            int n = st.Buf.N;
            if (n < 9 || a[0] != 0 || a[1] != 0 || a[2] != 1)
            {
                if (atEof && n > 0) { Truncated(); return; }
                DroppedPes++;
                return;
            }
            int pesLen = (a[4] << 8) | a[5];
            bool incomplete = pesLen != 0 ? n < 6 + pesLen : atEof && _tailPartial;
            if (incomplete)
            {
                if (atEof) Truncated();
                else DroppedPes++;
                return;
            }
            int flags = a[7];
            int payload = 9 + a[8];
            int end = pesLen != 0 ? 6 + pesLen : n;
            if (payload > end) { DroppedPes++; return; }
            bool hasPts = (flags & 0x80) != 0 && a[8] >= 5;
            long pts = 0, dts = 0;
            if (hasPts)
            {
                pts = Stamp(a, 9);
                dts = (flags & 0x40) != 0 && a[8] >= 10 ? Stamp(a, 14) : pts;
            }
            Deliver(st.Track, a, payload, end - payload, hasPts, pts, dts);
        }

        private void Truncated()
        {
            if (_allowTruncated) { DroppedPes++; return; }
            if (_error.Length == 0) _error = Tr.S("Поток оборван: последний кадр неполный", "The stream is cut off: the last frame is incomplete");
        }

        private static long Stamp(byte[] a, int i)
        {
            return ((long)(a[i] & 0x0E) << 29) | ((long)a[i + 1] << 22) | ((long)(a[i + 2] & 0xFE) << 14) | ((long)a[i + 3] << 7) | ((long)a[i + 4] >> 1);
        }

        private void Deliver(int track, byte[] a, int off, int len, bool hasPts, long rawPts, long rawDts)
        {
            MdEsTrack t = _tracks[track];
            EsState es = _es[track];
            if (t.Video) VideoPes(t, es, a, off, len, hasPts, rawPts, rawDts);
            else AudioPes(t, es, a, off, len, hasPts, rawPts);
        }

        private void VideoPes(MdEsTrack t, EsState es, byte[] a, int off, int len, bool hasPts, long rawPts, long rawDts)
        {
            if (!hasPts)
            {
                if (es.PendingRaw != null) es.PendingRaw.Add(a, off, len);  // продолжение кадра без отметки
                return;
            }
            long pts, dts;
            _timeline.Map(t.Index, rawPts, rawDts, t.FrameDur, out pts, out dts);
            FinishVideo(t, es, dts);
            MdAu au = new MdAu();
            au.Track = t.Index;
            au.Pts = pts;
            au.Dts = dts;
            es.Pending = au;
            es.PendingRaw = new MdBuf();
            es.PendingRaw.Add(a, off, len);
        }

        // Кадр, ждавший следующего: длительность, NAL, ключевой, заголовок последовательности.
        private void FinishVideo(MdEsTrack t, EsState es, long nextDts)
        {
            MdAu au = es.Pending;
            MdBuf raw = es.PendingRaw;
            es.Pending = null;
            es.PendingRaw = null;
            if (au == null) return;
            if (es.LastDts != long.MinValue && au.Dts <= es.LastDts)
            {
                long shift = es.LastDts + 1 - au.Dts;
                au.Dts += shift;
                if (au.Pts < au.Dts) au.Pts = au.Dts;
            }
            long d = nextDts == long.MinValue ? 0 : nextDts - au.Dts;
            if (d > 0 && d <= MdTimeline.JumpLimit)
            {
                au.Dur = d;
                if (d >= 450 && (t.FrameDur <= 0 || d < t.FrameDur)) t.FrameDur = d;   // не чаще 200 к/с
            }
            else au.Dur = t.FrameDur > 0 ? t.FrameDur : 3000;
            bool hevc = t.Codec == MdCodec.Hevc;
            List<int[]> nals = MdNal.Split(raw.A, 0, raw.N);
            int size = 0;
            foreach (int[] s in nals) size += 4 + s[1];
            byte[] data = new byte[size];
            int w = 0;
            bool key = false;
            foreach (int[] s in nals)
            {
                int type = hevc ? (raw.A[s[0]] >> 1) & 0x3F : raw.A[s[0]] & 0x1F;
                if (hevc)
                {
                    if (type == 35 || type == 38) continue;            // AUD, заполнитель
                    if (type == 32) es.Vps = Copy(raw.A, s);
                    else if (type == 33) es.Sps = Copy(raw.A, s);
                    else if (type == 34) es.Pps = Copy(raw.A, s);
                    else if (type >= 16 && type <= 21) key = true;
                }
                else
                {
                    if (type == 9 || type == 12) continue;
                    if (type == 7) es.Sps = Copy(raw.A, s);
                    else if (type == 8) es.Pps = Copy(raw.A, s);
                    else if (type == 5) key = true;
                }
                data[w] = 0; data[w + 1] = 0; data[w + 2] = 0; data[w + 3] = 1;
                Buffer.BlockCopy(raw.A, s[0], data, w + 4, s[1]);
                w += 4 + s[1];
            }
            if (w == 0) return;
            if (w < data.Length) Array.Resize(ref data, w);
            if (t.SeqHeader == null && es.Sps != null && es.Pps != null && (!hevc || es.Vps != null))
            {
                int width, height;
                bool sized = hevc ? MdNal.HevcSize(es.Sps, 0, es.Sps.Length, out width, out height) : MdNal.H264Size(es.Sps, 0, es.Sps.Length, out width, out height);
                if (sized)
                {
                    t.Width = width;
                    t.Height = height;
                    t.SeqHeader = hevc ? Join(es.Vps, es.Sps, es.Pps) : Join(es.Sps, es.Pps);
                }
            }
            // До первого ключевого кадра с известным заголовком декодер ничего не покажет — такие кадры не пишем.
            if (!es.SeenKey)
            {
                if (!key || t.SeqHeader == null) return;
                es.SeenKey = true;
            }
            au.Key = key;
            au.Data = data;
            es.LastDts = au.Dts;
            t.Frames++;
            t.Bytes += data.Length;
            _out.Enqueue(au);
        }

        private static byte[] Copy(byte[] a, int[] s)
        {
            byte[] r = new byte[s[1]];
            Buffer.BlockCopy(a, s[0], r, 0, s[1]);
            return r;
        }

        private static byte[] Join(params byte[][] nals)
        {
            int size = 0;
            foreach (byte[] n in nals) size += 4 + n.Length;
            byte[] r = new byte[size];
            int w = 0;
            foreach (byte[] n in nals)
            {
                r[w + 3] = 1;
                Buffer.BlockCopy(n, 0, r, w + 4, n.Length);
                w += 4 + n.Length;
            }
            return r;
        }

        private void AudioPes(MdEsTrack t, EsState es, byte[] a, int off, int len, bool hasPts, long rawPts)
        {
            int leftover = es.Audio.N;                              // байты кадра, начавшегося в прошлом PES
            long pesTime = 0;
            if (hasPts)
            {
                long dts;
                _timeline.Map(t.Index, rawPts, rawPts, t.FrameDur, out pesTime, out dts);
            }
            if (es.Audio.N + len > (1 << 20)) { es.Audio.Clear(); leftover = 0; DroppedPes++; }
            es.Audio.Add(a, off, len);
            byte[] buf = es.Audio.A;
            int n = es.Audio.N;
            int pos = 0;
            bool anchored = false;
            while (pos < n)
            {
                int headerLen, frameLen, rate, channels, samples;
                int r = t.Codec == MdCodec.Aac
                    ? MdAdts.Parse(buf, pos, n - pos, out headerLen, out frameLen, out rate, out channels, out samples)
                    : MdMpa.Parse(buf, pos, n - pos, out headerLen, out frameLen, out rate, out channels, out samples);
                if (r < 0) break;
                if (r == 0) { pos++; continue; }
                if (pos + frameLen > n) break;
                if (t.SampleRate == 0)
                {
                    t.SampleRate = rate;
                    t.Channels = channels;
                    if (t.Codec == MdCodec.Aac) t.Asc = MdAdts.AudioSpecificConfig(buf, pos);
                    else MdMpa.Describe(buf, pos, t);
                }
                long dur = samples * 90000L / rate;
                t.FrameDur = dur;
                long time;
                // Отметка PES относится к первому кадру, который в этом PES начался.
                if (hasPts && !anchored && pos >= leftover) { es.PesTime = pesTime; es.PesFrames = 0; anchored = true; }
                if (anchored)
                {
                    time = es.PesTime + (es.PesFrames * 90000L + rate / 2) / rate;
                    es.PesFrames += samples;
                }
                else if (es.NextTime != long.MinValue) time = es.NextTime;
                else { pos += frameLen; continue; }                // кадры до первой отметки времени не пишем
                if (es.LastDts != long.MinValue && time <= es.LastDts) time = es.LastDts + 1;
                MdAu au = new MdAu();
                au.Track = t.Index;
                au.Pts = time;
                au.Dts = time;
                au.Dur = dur;
                au.Key = true;
                int payloadOff = t.Codec == MdCodec.Aac ? headerLen : 0;
                au.Data = new byte[frameLen - payloadOff];
                Buffer.BlockCopy(buf, pos + payloadOff, au.Data, 0, au.Data.Length);
                es.LastDts = time;
                es.NextTime = time + dur;
                _timeline.Advance(t.Index, time, dur);
                t.Frames++;
                t.Bytes += au.Data.Length;
                _out.Enqueue(au);
                pos += frameLen;
            }
            es.Audio.Consume(pos);
        }

        private void FlushAll()
        {
            foreach (PidState st in new List<PidState>(_pids.Values))
                if (!st.IsPat && !st.IsPmt) FinishPes(st, true);
            for (int i = 0; i < _tracks.Count; i++)
            {
                EsState es = _es[i];
                if (_tracks[i].Video) FinishVideo(_tracks[i], es, long.MinValue);
                else if (es.Audio.N > 0)
                {
                    // хвост звука, не дотянувший до целого кадра: начало кадра есть — поток оборван, иначе мусор
                    int headerLen, frameLen, rate, channels, samples;
                    int r = _tracks[i].Codec == MdCodec.Aac
                        ? MdAdts.Parse(es.Audio.A, 0, es.Audio.N, out headerLen, out frameLen, out rate, out channels, out samples)
                        : MdMpa.Parse(es.Audio.A, 0, es.Audio.N, out headerLen, out frameLen, out rate, out channels, out samples);
                    if (r != 0) Truncated();
                    es.Audio.Clear();
                }
            }
        }
    }
}
