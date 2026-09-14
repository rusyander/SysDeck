// Windows Process Cleaner — «Загрузки», видео: звук — заголовки ADTS и MPEG audio, файлы .aac/.mp3, выход MP3.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// ADTS: в MP4 кладётся сырой AAC (заголовок 7/9 байт снимается), а конфигурация уходит в AudioSpecificConfig типа.
// «Packed audio» HLS — ADTS/MP3 с ID3-тегами между кадрами; метка com.apple.streaming.transportStreamTimestamp даёт
// отметку 90 кГц той же шкалы, что у видео в TS.
// MP3 на выходе — единственное перекодирование: декодер MF → PCM 16 бит → кодировщик MP3 MF (IMFSinkWriter, контейнер
// MP3). TS/ADTS сначала собираются во временный M4A рядом с результатом (он удаляется по точному имени).
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using WindowsProcessCleaner.Capture;

namespace WindowsProcessCleaner.Downloads
{
    internal static class MdAdts
    {
        private static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

        // 1 — заголовок кадра; 0 — здесь не кадр; -1 — похоже на начало кадра, но байтов мало.
        public static int Parse(byte[] b, int pos, int avail, out int headerLen, out int frameLen, out int rate, out int channels, out int samples)
        {
            headerLen = frameLen = rate = channels = samples = 0;
            if (avail < 1) return -1;
            if (b[pos] != 0xFF) return 0;
            if (avail < 2) return -1;
            if ((b[pos + 1] & 0xF6) != 0xF0) return 0;
            if (avail < 7) return -1;
            int freq = (b[pos + 2] >> 2) & 0x0F;
            if (freq >= Rates.Length) return 0;
            int ch = ((b[pos + 2] & 1) << 2) | (b[pos + 3] >> 6);
            if (ch == 0) return 0;                                       // конфигурация в PCE — не поддерживаем
            headerLen = (b[pos + 1] & 1) != 0 ? 7 : 9;
            frameLen = ((b[pos + 3] & 3) << 11) | (b[pos + 4] << 3) | (b[pos + 5] >> 5);
            if (frameLen <= headerLen) return 0;
            rate = Rates[freq];
            channels = ch == 7 ? 8 : ch;
            samples = 1024 * ((b[pos + 6] & 3) + 1);
            return 1;
        }

        public static byte[] AudioSpecificConfig(byte[] b, int pos)
        {
            int objectType = ((b[pos + 2] >> 6) & 3) + 1;
            int freq = (b[pos + 2] >> 2) & 0x0F;
            int ch = ((b[pos + 2] & 1) << 2) | (b[pos + 3] >> 6);
            return new byte[] { (byte)((objectType << 3) | (freq >> 1)), (byte)(((freq & 1) << 7) | (ch << 3)) };
        }
    }

    internal static class MdMpa
    {
        private static readonly int[,] Bitrates =
        {
            { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 },   // MPEG-1 слой I
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 },      // MPEG-1 слой II
            { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 },       // MPEG-1 слой III
            { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 },      // MPEG-2/2.5 слой I
            { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 },           // MPEG-2/2.5 слои II и III
        };

        private static int Header(byte[] b, int pos, out int version, out int layer, out int kbps, out int rate, out int pad, out int channels)
        {
            version = layer = kbps = rate = pad = channels = 0;
            int v = ((b[pos + 1] >> 3) & 3), l = ((b[pos + 1] >> 1) & 3);
            int br = b[pos + 2] >> 4, sr = (b[pos + 2] >> 2) & 3;
            if (v == 1 || l == 0 || br == 0 || br == 15 || sr == 3) return 0;
            version = v == 3 ? 1 : v == 2 ? 2 : 25;
            layer = 4 - l;
            int row = version == 1 ? layer - 1 : (layer == 1 ? 3 : 4);
            kbps = Bitrates[row, br];
            rate = new int[] { 44100, 48000, 32000 }[sr] >> (version == 1 ? 0 : version == 2 ? 1 : 2);
            pad = (b[pos + 2] >> 1) & 1;
            channels = ((b[pos + 3] >> 6) & 3) == 3 ? 1 : 2;
            return 1;
        }

        public static int Parse(byte[] b, int pos, int avail, out int headerLen, out int frameLen, out int rate, out int channels, out int samples)
        {
            headerLen = frameLen = rate = channels = samples = 0;
            if (avail < 1) return -1;
            if (b[pos] != 0xFF) return 0;
            if (avail < 2) return -1;
            if ((b[pos + 1] & 0xE0) != 0xE0) return 0;
            if (avail < 4) return -1;
            int version, layer, kbps, pad;
            if (Header(b, pos, out version, out layer, out kbps, out rate, out pad, out channels) == 0) return 0;
            samples = layer == 1 ? 384 : (layer == 2 || version == 1 ? 1152 : 576);
            frameLen = layer == 1 ? (12 * kbps * 1000 / rate + pad) * 4 : samples / 8 * kbps * 1000 / rate + pad;
            headerLen = 4;
            return frameLen > 4 ? 1 : 0;
        }

        public static void Describe(byte[] b, int pos, MdEsTrack t)
        {
            int version, layer, kbps, rate, pad, channels;
            if (Header(b, pos, out version, out layer, out kbps, out rate, out pad, out channels) == 0) return;
            int hl, fl, r, ch, samples;
            Parse(b, pos, 4, out hl, out fl, out r, out ch, out samples);
            t.Mp3Layer = layer;
            t.Mp3Bitrate = kbps;
            t.Mp3FrameBytes = fl;
        }
    }

    // Файл-поток ADTS или MPEG audio (в том числе склеенные сегменты HLS «packed audio» с ID3).
    internal sealed class MdEsFileReader : IMdAuReader
    {
        private const int MaxId3 = 16 << 20;
        private const string TimestampOwner = "com.apple.streaming.transportStreamTimestamp";

        private readonly FileStream _fs;
        private readonly bool _allowTruncated;
        private readonly MdBuf _buf = new MdBuf();
        private readonly List<MdEsTrack> _tracks = new List<MdEsTrack>();
        private readonly MdTimeline _timeline = new MdTimeline();
        private readonly byte[] _chunk = new byte[1 << 16];
        private int _pos;
        private long _bufStart;
        private bool _eof;
        private string _error = "";
        private bool _stamp;
        private long _stampRaw;
        private long _anchor = long.MinValue, _anchorSamples, _last = long.MinValue;

        public MdEsFileReader(string path, MdCodec codec, bool allowTruncated)
        {
            _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            _allowTruncated = allowTruncated;
            MdEsTrack t = new MdEsTrack();
            t.Codec = codec;
            _tracks.Add(t);
        }

        public List<MdEsTrack> Tracks { get { return _tracks; } }
        public string Error { get { return _error; } }
        public long Position { get { return _bufStart + _pos; } }
        public long Length { get { try { return _fs.Length; } catch (IOException) { return 0; } } }
        public MdTimeline Timeline { get { return _timeline; } }
        public void Dispose() { _fs.Dispose(); }

        internal static int SyncSafe(byte[] b, int i)
        {
            return ((b[i] & 0x7F) << 21) | ((b[i + 1] & 0x7F) << 14) | ((b[i + 2] & 0x7F) << 7) | (b[i + 3] & 0x7F);
        }

        private void Fill()
        {
            if (_pos > 0)
            {
                _buf.Consume(_pos);
                _bufStart += _pos;
                _pos = 0;
            }
            int n = _fs.Read(_chunk, 0, _chunk.Length);
            if (n <= 0) _eof = true;
            else _buf.Add(_chunk, 0, n);
        }

        private void Truncated()
        {
            if (!_allowTruncated) _error = Tr.S("Поток оборван: последний кадр неполный", "The stream is cut off: the last frame is incomplete");
            _pos = _buf.N;
        }

        public bool Next(out MdAu au)
        {
            au = null;
            MdEsTrack t = _tracks[0];
            while (_error.Length == 0)
            {
                int avail = _buf.N - _pos;
                if (avail < 16 && !_eof) { Fill(); continue; }
                if (avail <= 0) return false;
                byte[] a = _buf.A;
                if (avail >= 3 && a[_pos] == 'I' && a[_pos + 1] == 'D' && a[_pos + 2] == '3')
                {
                    if (avail < 10) { _pos = _buf.N; continue; }
                    int size = 10 + SyncSafe(a, _pos + 6) + ((a[_pos + 5] & 0x10) != 0 ? 10 : 0);
                    if (size > MaxId3) { _error = Tr.S("Слишком большой тег ID3", "ID3 tag is too large"); return false; }
                    if (avail < size) { if (_eof) { _pos = _buf.N; continue; } Fill(); continue; }
                    long ts;
                    if (Id3Timestamp(a, _pos, size, out ts)) { _stamp = true; _stampRaw = ts; }
                    _pos += size;
                    continue;
                }
                int headerLen, frameLen, rate, channels, samples;
                int r = t.Codec == MdCodec.Aac
                    ? MdAdts.Parse(a, _pos, avail, out headerLen, out frameLen, out rate, out channels, out samples)
                    : MdMpa.Parse(a, _pos, avail, out headerLen, out frameLen, out rate, out channels, out samples);
                if (r < 0 || (r > 0 && frameLen > avail))
                {
                    if (_eof) { Truncated(); return false; }
                    Fill();
                    continue;
                }
                if (r == 0) { _pos++; continue; }
                if (t.SampleRate == 0)
                {
                    t.SampleRate = rate;
                    t.Channels = channels;
                    if (t.Codec == MdCodec.Aac) t.Asc = MdAdts.AudioSpecificConfig(a, _pos);
                    else MdMpa.Describe(a, _pos, t);
                }
                long dur = samples * 90000L / rate;
                t.FrameDur = dur;
                if (_stamp)
                {
                    long pts, dts;
                    _timeline.Map(0, _stampRaw, _stampRaw, dur, out pts, out dts);
                    _anchor = pts;
                    _anchorSamples = 0;
                    _stamp = false;
                }
                if (_anchor == long.MinValue) { _anchor = 0; _anchorSamples = 0; }
                long time = _anchor + (_anchorSamples * 90000L + rate / 2) / rate;
                _anchorSamples += samples;
                if (_last != long.MinValue && time <= _last) time = _last + 1;
                int skip = t.Codec == MdCodec.Aac ? headerLen : 0;
                au = new MdAu();
                au.Track = 0;
                au.Pts = time;
                au.Dts = time;
                au.Dur = dur;
                au.Key = true;
                au.Data = new byte[frameLen - skip];
                Buffer.BlockCopy(a, _pos + skip, au.Data, 0, au.Data.Length);
                _timeline.Advance(0, time, dur);
                _last = time;
                _pos += frameLen;
                t.Frames++;
                t.Bytes += au.Data.Length;
                return true;
            }
            return false;
        }

        // PRIV com.apple.streaming.transportStreamTimestamp: 8 байт, младшие 33 бита — PTS.
        private static bool Id3Timestamp(byte[] a, int pos, int size, out long ts)
        {
            ts = 0;
            int version = a[pos + 3];
            int p = pos + 10, end = pos + size;
            if ((a[pos + 5] & 0x40) != 0 && p + 4 <= end)
                p += version >= 4 ? SyncSafe(a, p) : 4 + ((a[p] << 24) | (a[p + 1] << 16) | (a[p + 2] << 8) | a[p + 3]);
            while (p + 10 <= end)
            {
                string id = Encoding.ASCII.GetString(a, p, 4);
                int len = version >= 4 ? SyncSafe(a, p + 4) : (a[p + 4] << 24) | (a[p + 5] << 16) | (a[p + 6] << 8) | a[p + 7];
                if (len < 0 || id[0] == 0) break;
                int body = p + 10;
                if (body + len > end) break;
                if (id == "PRIV")
                {
                    int zero = Array.IndexOf(a, (byte)0, body, len);
                    if (zero > 0 && Encoding.ASCII.GetString(a, body, zero - body) == TimestampOwner && zero + 9 <= body + len)
                    {
                        long v = 0;
                        for (int i = 1; i <= 8; i++) v = (v << 8) | a[zero + i];
                        ts = v & (MdTimeline.Wrap - 1);
                        return true;
                    }
                }
                p = body + len;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------ //
    //  Выход MP3
    // ------------------------------------------------------------------ //
    internal static class MdMp3
    {
        private static readonly object Gate = new object();
        private static int _present = -1;

        public static bool EncoderPresent()
        {
            lock (Gate)
            {
                if (_present >= 0) return _present == 1;
                _present = 0;
                if (VidNative.MFStartup(0x20070, 0) < 0) return false;
                try
                {
                    VidNative.TypeInfo ti = new VidNative.TypeInfo();
                    ti.Major = MfA.MediaAudio;
                    ti.Sub = MdMf.FmtMp3;
                    IntPtr arr;
                    uint n;
                    if (VidNative.MFTEnumEx(MdMf.CatAudioEncoder, 0x3F | 0x40, IntPtr.Zero, ref ti, out arr, out n) >= 0)
                    {
                        for (int i = 0; i < n; i++)
                        {
                            IntPtr act = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
                            VidCom.Rel(ref act);
                        }
                        if (arr != IntPtr.Zero) VidNative.CoTaskMemFree(arr);
                        if (n > 0) _present = 1;
                    }
                }
                finally { VidNative.MFShutdown(); }
                return _present == 1;
            }
        }

        public static bool Write(List<MdInput> av, string tmp, MdMuxJob job, MdProgress progress, MdMuxResult res, out long base100, out string error)
        {
            base100 = 0;
            error = null;
            MdInput src = null;
            foreach (MdInput i in av) if (i.Kind != MdTrackKind.Video) { src = i; break; }
            if (src == null) { error = Tr.S("Нет дорожки звука для MP3", "No audio track for MP3"); return false; }
            if (src.Layout == MdLayout.Mp3)
            {
                // MP3 уже MP3: копия без перекодирования, кадры и длительность — по разбору
                if (!Count(src.Path, job.AllowTruncated, res, out error)) return false;
                File.Copy(src.Path, tmp, false);
                return true;
            }
            if (src.Layout == MdLayout.WebM) { error = Tr.S("Звук из WebM в MP3 не перекодируется — выберите WebM", "Audio from WebM cannot be converted to MP3 — choose WebM"); return false; }
            if (!EncoderPresent()) { error = Tr.S("Кодировщик MP3 в Windows не найден", "No MP3 encoder found in Windows"); return false; }
            string mid = null;
            string from = src.Path;
            try
            {
                if (src.Layout == MdLayout.Ts || src.Layout == MdLayout.Adts)
                {
                    mid = tmp + ".src.m4a";
                    MdMux.DeleteOwn(mid);
                    MdInput audio = new MdInput();
                    audio.Path = src.Path;
                    audio.Layout = src.Layout;
                    audio.Kind = MdTrackKind.Audio;
                    MdMuxResult stage = new MdMuxResult();
                    if (!MdMp4Writer.Write(new List<MdInput> { audio }, true, mid, job, progress, stage, out base100, out error)) return false;
                    from = mid;
                }
                if (!Transcode(from, tmp, job, out error)) return false;
                return Count(tmp, true, res, out error);
            }
            finally
            {
                if (mid != null) MdMux.DeleteOwn(mid);
            }
        }

        private static bool Count(string path, bool allowTruncated, MdMuxResult res, out string error)
        {
            error = null;
            long end = 0;
            int frames = 0;
            using (MdEsFileReader r = new MdEsFileReader(path, MdCodec.Mp3, allowTruncated))
            {
                MdAu au;
                while (r.Next(out au)) { frames++; end = au.Pts + au.Dur; }
                if (r.Error.Length > 0) { error = r.Error; return false; }
            }
            if (frames == 0) { error = Tr.S("В MP3 нет ни одного кадра", "The MP3 has no frames"); return false; }
            res.AudioFrames = frames;
            res.DurationMs = end / 90;
            return true;
        }

        private static bool Transcode(string from, string tmp, MdMuxJob job, out string error)
        {
            error = null;
            IntPtr bsIn = IntPtr.Zero, reader = IntPtr.Zero, pcm = IntPtr.Zero, current = IntPtr.Zero;
            IntPtr attrs = IntPtr.Zero, bsOut = IntPtr.Zero, writer = IntPtr.Zero, outType = IntPtr.Zero, inType = IntPtr.Zero;
            VidCom.Check(VidNative.MFStartup(0x20070, 0), "MFStartup");
            try
            {
                bsIn = MdMf.OpenRead(from, "audio/mp4");
                int hr = MdMf.MFCreateSourceReaderFromByteStream(bsIn, IntPtr.Zero, out reader);
                if (hr < 0) { error = Tr.S("Media Foundation не открыла звук (", "Media Foundation could not open the audio (") + VidCom.Hex(hr) + ")"; return false; }
                uint stream = uint.MaxValue;
                for (uint i = 0; i < 32; i++)
                {
                    IntPtr type;
                    if (VidCom.Fn<VidCom.DUIntUIntOutPtr>(reader, 5)(reader, i, 0, out type) < 0) break;
                    Guid major;
                    MfA.GetGuid(type, MfA.MajorType, out major);
                    VidCom.Rel(ref type);
                    bool pick = stream == uint.MaxValue && major == MfA.MediaAudio;
                    if (pick) stream = i;
                    VidCom.Fn<VidCom.DUIntInt>(reader, 4)(reader, i, pick ? 1 : 0);
                }
                if (stream == uint.MaxValue) { error = Tr.S("Нет дорожки звука для MP3", "No audio track for MP3"); return false; }
                pcm = MdMf.NewType(MfA.MediaAudio, MfA.FmtPcm);
                MfA.U32(pcm, MfA.AudioBits, 16);
                hr = VidCom.Fn<VidCom.DUIntPtrPtr>(reader, 7)(reader, stream, IntPtr.Zero, pcm);
                if (hr < 0) { error = Tr.S("Декодер звука Windows не найден (", "No Windows audio decoder (") + VidCom.Hex(hr) + ")"; return false; }
                VidCom.Check(VidCom.Fn<VidCom.DUIntOutPtr>(reader, 6)(reader, stream, out current), "GetCurrentMediaType");
                uint rate, channels;
                MfA.GetU32(current, MfA.AudioSampleRate, out rate);
                MfA.GetU32(current, MfA.AudioChannels, out channels);
                if ((rate != 32000 && rate != 44100 && rate != 48000) || channels < 1 || channels > 2)
                {
                    error = Tr.S("Кодировщик MP3 не принимает такой звук: ", "The MP3 encoder does not accept this audio: ") + rate + " Hz, " + channels + " ch";
                    return false;
                }
                VidCom.Check(VidNative.MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
                MfA.G(attrs, MfA.ContainerType, MdMf.ContainerMp3);
                MfA.U32(attrs, MfA.DisableThrottling, 1);
                bsOut = MdMf.OpenWrite(tmp);
                hr = VidNative.MFCreateSinkWriterFromURL(null, bsOut, attrs, out writer);
                if (hr < 0) { error = Tr.S("Media Foundation не создала файл MP3 (", "Media Foundation could not create the MP3 (") + VidCom.Hex(hr) + ")"; return false; }
                uint idx = 0;
                hr = -1;
                foreach (uint bytes in new uint[] { channels == 1 ? 16000u : 24000u, 20000u, 16000u })
                {
                    VidCom.Rel(ref outType);
                    outType = MdMf.NewType(MfA.MediaAudio, MdMf.FmtMp3);
                    MfA.U32(outType, MfA.AudioSampleRate, rate);
                    MfA.U32(outType, MfA.AudioChannels, channels);
                    MfA.U32(outType, MfA.AudioAvgBytes, bytes);
                    hr = VidCom.Fn<VidCom.DPtrOutUInt>(writer, 3)(writer, outType, out idx);
                    if (hr >= 0) break;
                }
                if (hr < 0) { error = Tr.S("Кодировщик MP3 не принял параметры (", "The MP3 encoder rejected the format (") + VidCom.Hex(hr) + ")"; return false; }
                inType = MdMf.NewType(MfA.MediaAudio, MfA.FmtPcm);
                MfA.U32(inType, MfA.AudioBits, 16);
                MfA.U32(inType, MfA.AudioSampleRate, rate);
                MfA.U32(inType, MfA.AudioChannels, channels);
                MfA.U32(inType, MfA.AudioBlockAlign, channels * 2);
                MfA.U32(inType, MfA.AudioAvgBytes, rate * channels * 2);
                MfA.U32(inType, MfA.AllSamplesIndependent, 1);
                hr = VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, inType, IntPtr.Zero);
                if (hr < 0) { error = Tr.S("Кодировщик MP3 не принял PCM (", "The MP3 encoder rejected PCM (") + VidCom.Hex(hr) + ")"; return false; }
                hr = VidCom.Fn<VidCom.DHr>(writer, 5)(writer);
                if (hr < 0) { error = "BeginWriting " + VidCom.Hex(hr); return false; }
                VidCom.DReadSample read = VidCom.Fn<VidCom.DReadSample>(reader, 9);
                int count = 0;
                while (true)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr sample;
                    hr = read(reader, stream, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0 || (flags & 1) != 0) { VidCom.Rel(ref sample); error = Tr.S("Ошибка декодирования звука (", "Audio decoding failed (") + VidCom.Hex(hr) + ")"; return false; }
                    if (sample != IntPtr.Zero)
                    {
                        hr = VidCom.Fn<VidCom.DUIntPtr>(writer, 6)(writer, idx, sample);
                        VidCom.Rel(ref sample);
                        if (hr < 0) { error = Tr.S("Кодировщик MP3 не принял звук (", "The MP3 encoder rejected audio (") + VidCom.Hex(hr) + ")"; return false; }
                    }
                    if ((flags & 2) != 0) break;
                    if ((++count & 63) == 0 && job.Cancel != null && job.Cancel()) { error = MdProgress.Cancelled; return false; }
                }
                hr = VidCom.Fn<VidCom.DHr>(writer, 11)(writer);
                if (hr < 0) { error = Tr.S("Media Foundation не завершила MP3 (", "Media Foundation could not finalize the MP3 (") + VidCom.Hex(hr) + ")"; return false; }
                return true;
            }
            finally
            {
                VidCom.Rel(ref writer);
                MdMf.CloseStream(ref bsOut);
                VidCom.Rel(ref attrs);
                VidCom.Rel(ref outType);
                VidCom.Rel(ref inType);
                VidCom.Rel(ref current);
                VidCom.Rel(ref pcm);
                VidCom.Rel(ref reader);
                MdMf.CloseStream(ref bsIn);
                VidNative.MFShutdown();
            }
        }
    }
}
