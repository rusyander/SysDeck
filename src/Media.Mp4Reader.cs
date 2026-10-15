// SysDeck — «Загрузки», видео: чтение MP4/fMP4 для склейки (IMFSourceReader) и обход списков правки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Media Foundation молча отдаёт ноль кадров у фрагментированного MP4 со списком правки (edts/elst), где начало правки
// позже первого кадра, — так лежат некоторые форматы YouTube (133 «240p»: elst media_time 1024 при первом tfdt 992).
// Тогда читается копия файла рядом с ним, где у дорожек «edts» переименован во «free»: кадры те же, сдвиг начала на
// десятки миллисекунд склейка всё равно убирает, приводя время к нулю. Вход не меняется; копия удаляется в Dispose.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using SysDeck.Capture;

namespace SysDeck.Downloads
{
    // MP4/fMP4 через IMFSourceReader: сэмплы идут в приёмник без изменений (кроме сдвига времени к нулю).
    internal sealed class MdReaderSource : MdStreamSource
    {
        private const int PrimeSamples = 2000;

        private sealed class Pending { public IntPtr Sample; public int Stream; public long Dts; }

        private readonly string _path;
        private readonly bool _video, _audio;
        private IntPtr _bs, _reader;
        private readonly List<IntPtr> _types = new List<IntPtr>();
        private readonly List<bool> _isVideo = new List<bool>();
        private readonly Dictionary<uint, int> _map = new Dictionary<uint, int>();
        private readonly Queue<Pending> _queue = new Queue<Pending>();
        private Pending _head;
        private bool[] _ended;
        private int _endedCount;
        private long _first = long.MaxValue;
        private long _position;
        private readonly long _length;
        private int _unseen;                     // дорожки, от которых при разгоне не пришло ни одного кадра
        private string _copy;                    // копия без списков правки; null — читается сам файл

        public MdReaderSource(string path, bool video, bool audio)
        {
            _path = path;
            _video = video;
            _audio = audio;
            try { _length = new FileInfo(path).Length; }
            catch (IOException) { _length = 0; }
        }

        public override bool Prime(out string error)
        {
            if (!PrimeFrom(_path, out error)) return false;
            if (_unseen == 0) return true;
            string copy = MdEditList.CopyWithout(_path);
            if (copy == null) return true;
            DlLog.Write("mux: no frames from " + Path.GetFileName(_path) + ", reading a copy without edit lists");
            Release();
            _copy = copy;
            return PrimeFrom(copy, out error);
        }

        private bool PrimeFrom(string path, out string error)
        {
            error = null;
            _bs = MdMf.OpenRead(path, "video/mp4");
            int hr = MdMf.MFCreateSourceReaderFromByteStream(_bs, IntPtr.Zero, out _reader);
            if (hr < 0) { error = Tr.S("Media Foundation не открыла MP4: ", "Media Foundation could not open the MP4: ") + Path.GetFileName(_path) + " (" + VidCom.Hex(hr) + ")"; return false; }
            for (uint i = 0; i < 32; i++)
            {
                IntPtr type;
                if (VidCom.Fn<VidCom.DUIntUIntOutPtr>(_reader, 5)(_reader, i, 0, out type) < 0) break;
                Guid major;
                MfA.GetGuid(type, MfA.MajorType, out major);
                bool isVideo = major == MfA.MediaVideo, isAudio = major == MfA.MediaAudio;
                bool want = (isVideo && _video) || (isAudio && _audio);
                VidCom.Fn<VidCom.DUIntInt>(_reader, 4)(_reader, i, want ? 1 : 0);
                if (!want) { VidCom.Rel(ref type); continue; }
                _map[i] = _types.Count;
                _types.Add(type);
                _isVideo.Add(isVideo);
            }
            if (_types.Count == 0) { error = Tr.S("В файле нет дорожек нужного вида: ", "No tracks of the requested kind in: ") + Path.GetFileName(_path); return false; }
            _ended = new bool[_types.Count];
            bool[] seen = new bool[_types.Count];
            int seenCount = 0;
            while (seenCount < _types.Count && _queue.Count < PrimeSamples)
            {
                Pending p = Read();
                if (p == null) break;
                _queue.Enqueue(p);
                if (!seen[p.Stream]) { seen[p.Stream] = true; seenCount++; }
            }
            if (Error.Length > 0) { error = Error; return false; }
            for (int i = 0; i < _types.Count; i++) if (_ended[i] && !seen[i]) _unseen++;   // дошла до конца пустой
            foreach (Pending p in _queue) if (p.Dts < _first) _first = p.Dts;
            if (_first == long.MaxValue) _first = 0;
            Advance();
            return true;
        }

        private Pending Read()
        {
            VidCom.DReadSample read = VidCom.Fn<VidCom.DReadSample>(_reader, 9);
            while (_endedCount < _types.Count)
            {
                uint actual, flags;
                long ts;
                IntPtr sample;
                int hr = read(_reader, MdMf.AnyStream, 0, out actual, out flags, out ts, out sample);
                if (hr < 0 || (flags & 1) != 0)
                {
                    VidCom.Rel(ref sample);
                    Error = Tr.S("Media Foundation не дочитала файл: ", "Media Foundation failed reading: ") + Path.GetFileName(_path) + " (" + VidCom.Hex(hr) + ")";
                    return null;
                }
                int stream;
                bool known = _map.TryGetValue(actual, out stream);
                if (sample != IntPtr.Zero)
                {
                    if (!known) { VidCom.Rel(ref sample); continue; }
                    Pending p = new Pending();
                    p.Sample = sample;
                    p.Stream = stream;
                    ulong dts;
                    long time;
                    VidCom.Fn<VidCom.DOutLong>(sample, 35)(sample, out time);
                    p.Dts = MfA.GetU64(sample, MdMf.DecodeTimestamp, out dts) ? Math.Min(time, (long)dts) : time;
                    uint total;
                    if (VidCom.Fn<VidCom.DOutUInt>(sample, 45)(sample, out total) >= 0) _position += total;
                    if ((flags & 2) != 0 && known && !_ended[stream]) { _ended[stream] = true; _endedCount++; }
                    return p;
                }
                if ((flags & 2) != 0 && known && !_ended[stream]) { _ended[stream] = true; _endedCount++; }
            }
            return null;
        }

        private void Advance()
        {
            _head = _queue.Count > 0 ? _queue.Dequeue() : Read();
        }

        public override int Count { get { return _types.Count; } }
        public override bool IsVideo(int stream) { return _isVideo[stream]; }
        public override long FirstTime { get { return _first; } }
        public override bool HasHead { get { return _head != null; } }
        public override long HeadTime { get { return _head == null ? long.MaxValue : _head.Dts; } }
        public override int HeadStream { get { return _head == null ? -1 : _head.Stream; } }
        public override long Position { get { return Math.Min(_position, _length); } }

        public override IntPtr CreateType(int stream)
        {
            IntPtr t = _types[stream];
            Marshal.AddRef(t);
            return t;
        }

        public override IntPtr TakeHead(long base100, out long end100)
        {
            Pending p = _head;
            IntPtr sample = p.Sample;
            p.Sample = IntPtr.Zero;
            long time, dur;
            VidCom.Fn<VidCom.DOutLong>(sample, 35)(sample, out time);
            if (VidCom.Fn<VidCom.DOutLong>(sample, 37)(sample, out dur) < 0) dur = 0;
            if (base100 != 0)
            {
                time -= base100;
                VidCom.Fn<VidCom.DLong>(sample, 36)(sample, time);
                ulong dts;
                if (MfA.GetU64(sample, MdMf.DecodeTimestamp, out dts)) MfA.U64(sample, MdMf.DecodeTimestamp, (ulong)Math.Max(0, (long)dts - base100));
            }
            end100 = time + Math.Max(0, dur);
            Advance();
            return sample;
        }

        public override void Dispose()
        {
            Release();
            if (_copy == null) return;
            try { File.Delete(_copy); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            _copy = null;
        }

        // Всё, что держит открытый файл: сэмплы, типы, чтец, поток. Состояние разгона — к началу.
        private void Release()
        {
            if (_head != null) { VidCom.Rel(ref _head.Sample); _head = null; }
            while (_queue.Count > 0) { Pending p = _queue.Dequeue(); VidCom.Rel(ref p.Sample); }
            for (int i = 0; i < _types.Count; i++) { IntPtr t = _types[i]; VidCom.Rel(ref t); }
            _types.Clear();
            _isVideo.Clear();
            _map.Clear();
            _ended = null;
            _endedCount = 0;
            _first = long.MaxValue;
            _position = 0;
            _unseen = 0;
            Error = "";
            VidCom.Rel(ref _reader);
            MdMf.CloseStream(ref _bs);
        }
    }

    // Списки правки (moov/trak/edts) в MP4: найти и выключить в копии файла.
    internal static class MdEditList
    {
        private const long MaxMoov = 64L << 20;

        // Смещения четырёх байт типа «edts» у дорожек внутри moov (от начала файла); пусто — правок нет.
        internal static List<long> Find(Stream fs)
        {
            List<long> found = new List<long>();
            long pos = 0, len = fs.Length;
            byte[] h = new byte[8];
            for (int guard = 0; guard < 100000 && pos + 8 <= len; guard++)
            {
                long size, header;
                string type;
                if (!Header(fs, pos, len, h, out size, out header, out type)) break;
                if (type == "moov")
                {
                    if (size > MaxMoov) break;
                    byte[] moov = new byte[size];
                    fs.Position = pos;
                    if (ReadFull(fs, moov, moov.Length) != moov.Length) break;
                    foreach (long t in Children(moov, header, moov.Length, "trak"))
                        foreach (long e in Children(moov, t + Head(moov, t), t + Size(moov, t), "edts")) found.Add(pos + e + 4);
                    break;
                }
                pos += size;
            }
            return found;
        }

        // Копия рядом с файлом («<файл>.noedit»), где «edts» у дорожек стал «free». null — правок нет или копия не вышла.
        internal static string CopyWithout(string path)
        {
            try
            {
                List<long> at;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    at = Find(fs);
                if (at.Count == 0) return null;
                string copy = path + ".noedit";
                File.Copy(path, copy, true);
                byte[] free = { (byte)'f', (byte)'r', (byte)'e', (byte)'e' };
                using (FileStream fs = new FileStream(copy, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    foreach (long o in at) { fs.Position = o; fs.Write(free, 0, 4); }
                return copy;
            }
            catch (IOException ex) { DlLog.Write("mux: edit-list copy failed: " + ex.Message); return null; }
            catch (UnauthorizedAccessException ex) { DlLog.Write("mux: edit-list copy failed: " + ex.Message); return null; }
        }

        private static bool Header(Stream fs, long pos, long len, byte[] h, out long size, out long header, out string type)
        {
            size = 0;
            header = 8;
            type = "";
            fs.Position = pos;
            if (ReadFull(fs, h, 8) != 8) return false;
            size = BE32(h, 0);
            type = Type(h, 4);
            if (size == 1)
            {
                if (ReadFull(fs, h, 8) != 8) return false;
                size = (long)((ulong)BE32(h, 0) << 32 | BE32(h, 4));
                header = 16;
            }
            else if (size == 0) size = len - pos;
            return size >= header && pos + size <= len;
        }

        private static IEnumerable<long> Children(byte[] b, long from, long to, string want)
        {
            long p = from;
            while (p + 8 <= to)
            {
                long size = Size(b, p);
                if (size < 8 || p + size > to) yield break;
                if (Type(b, (int)p + 4) == want) yield return p;
                p += size;
            }
        }

        private static long Size(byte[] b, long p)
        {
            long size = BE32(b, (int)p);
            if (size == 1) return p + 16 <= b.Length ? (long)((ulong)BE32(b, (int)p + 8) << 32 | BE32(b, (int)p + 12)) : 0;
            return size == 0 ? b.Length - p : size;
        }

        private static long Head(byte[] b, long p) { return BE32(b, (int)p) == 1 ? 16 : 8; }

        private static uint BE32(byte[] b, int o) { return (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]); }

        private static string Type(byte[] b, int o) { return new string(new[] { (char)b[o], (char)b[o + 1], (char)b[o + 2], (char)b[o + 3] }); }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) break;
                got += n;
            }
            return got;
        }
    }
}
