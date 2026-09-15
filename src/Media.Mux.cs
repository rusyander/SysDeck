// SysDeck — «Загрузки», видео: склейка дорожек в один файл (MP4/M4A через Media Foundation, WebM, MP3, TS).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Без перекодирования: MP4/M4A пишет IMFSinkWriter, которому кадры дают либо свой разборщик (TS, ADTS, MP3 — Media.Ts.cs,
// Media.Audio.cs), либо IMFSourceReader (MP4/fMP4 — сэмплы копируются как есть). WebM собирает свой писатель Matroska
// (Media.Mkv.cs): MF на чистой Windows WebM не читает, а VP9/Opus из WebM в MP4 без CodecPrivate не переносятся.
// MP3 — единственный случай с перекодированием (декодер MF → кодировщик MP3 MF).
// Файлы открываются через IMFByteStream с явным типом содержимого: у данных движка расширение «.data», по нему MF
// формат не угадает. Результат пишется во «<OutPath>.tmp» и переименовывается только после успешного Finalize;
// при любой ошибке временный файл удаляется, входы не трогаются никогда.
// Время на выходе начинается с нуля — вычитается самая ранняя отметка всех дорожек, относительные сдвиги сохраняются.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using SysDeck.Capture;

namespace SysDeck.Downloads
{
    internal static class MdMux
    {
        public static MdMuxResult Run(MdMuxJob job)
        {
            MdMuxResult result = null;
            Exception error = null;
            // MF и COM — в своём потоке MTA, как починка fMP4 в «Захвате»: вызывающий поток может быть STA.
            Thread t = new Thread(() =>
            {
                try { result = RunCore(job); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(ApartmentState.MTA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (result != null) return result;
            MdMuxResult r = new MdMuxResult();
            r.Error = Tr.S("Склейка не удалась: ", "Assembly failed: ") + (error != null ? error.Message : "?");
            return r;
        }

        public static MdLayout ProbeLayout(string path)
        {
            byte[] b = new byte[64 << 10];
            int n = 0;
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return MdLayout.Unknown;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    int got;
                    while (n < b.Length && (got = fs.Read(b, n, b.Length - n)) > 0) n += got;
                }
            }
            catch (IOException) { return MdLayout.Unknown; }
            catch (UnauthorizedAccessException) { return MdLayout.Unknown; }
            catch (ArgumentException) { return MdLayout.Unknown; }
            catch (NotSupportedException) { return MdLayout.Unknown; }
            return ProbeBytes(b, n);
        }

        internal static MdLayout ProbeBytes(byte[] b, int n)
        {
            if (b == null || n < 4) return MdLayout.Unknown;
            if (n >= MdTsReader.PacketSize && b[0] == 0x47 && (n <= 188 || b[188] == 0x47) && (n <= 376 || b[376] == 0x47)) return MdLayout.Ts;
            if (b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3) return MdLayout.WebM;
            if (n >= 8)
            {
                string type = Encoding.ASCII.GetString(b, 4, 4);
                if (type == "ftyp" || type == "styp" || type == "moof" || type == "sidx" || type == "moov" || type == "free" || type == "skip" || type == "emsg" || type == "prft")
                    return ScanMp4(b, n);
            }
            int s = n >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            if (n - s >= 6 && Encoding.ASCII.GetString(b, s, 6) == "WEBVTT") return MdLayout.Vtt;
            string head = Encoding.UTF8.GetString(b, s, Math.Min(n - s, 4096)).TrimStart();
            if (head.StartsWith("<", StringComparison.Ordinal))
            {
                int tt = head.IndexOf("<tt", StringComparison.Ordinal);
                if (tt < 0)
                {
                    int colon = head.IndexOf(":tt", StringComparison.Ordinal);
                    if (colon > 0 && head.LastIndexOf('<', colon) >= 0) tt = colon;
                }
                if (tt >= 0) return MdLayout.Ttml;
            }
            int p = 0;
            if (b[0] == 'I' && b[1] == 'D' && b[2] == '3' && n >= 10)
            {
                p = 10 + MdEsFileReader.SyncSafe(b, 6) + ((b[5] & 0x10) != 0 ? 10 : 0);
                if (p >= n) return MdLayout.Mp3;
                // HLS «packed audio» ставит несколько ID3 подряд
                while (p + 10 <= n && b[p] == 'I' && b[p + 1] == 'D' && b[p + 2] == '3') p += 10 + MdEsFileReader.SyncSafe(b, p + 6);
                if (p >= n) return MdLayout.Mp3;
            }
            int hl, fl, rate, ch, samples;
            if (MdAdts.Parse(b, p, n - p, out hl, out fl, out rate, out ch, out samples) == 1)
            {
                int next = p + fl;
                if (next >= n || MdAdts.Parse(b, next, n - next, out hl, out fl, out rate, out ch, out samples) != 0) return MdLayout.Adts;
            }
            if (MdMpa.Parse(b, p, n - p, out hl, out fl, out rate, out ch, out samples) == 1)
            {
                int next = p + fl;
                if (next >= n || MdMpa.Parse(b, next, n - next, out hl, out fl, out rate, out ch, out samples) != 0) return MdLayout.Mp3;
            }
            return MdLayout.Unknown;
        }

        private static MdLayout ScanMp4(byte[] b, int n)
        {
            long p = 0;
            bool sawFtyp = false;
            while (p + 8 <= n)
            {
                long size = ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
                string type = Encoding.ASCII.GetString(b, (int)p, 8).Substring(4);
                int header = 8;
                if (size == 1)
                {
                    if (p + 16 > n) break;
                    size = 0;
                    for (int i = 8; i < 16; i++) size = (size << 8) | b[p + i];
                    header = 16;
                }
                else if (size == 0) size = long.MaxValue / 2;
                if (size < header) return sawFtyp ? MdLayout.Mp4 : MdLayout.Unknown;
                if (type == "ftyp") sawFtyp = true;
                if (type == "moof" || type == "styp" || type == "sidx") return MdLayout.Fmp4;
                if (type == "moov")
                {
                    long end = Math.Min(n, p + size);
                    for (long q = p + header; q + 8 <= end; q++)
                        if (b[q + 4] == 'm' && b[q + 5] == 'v' && b[q + 6] == 'e' && b[q + 7] == 'x') return MdLayout.Fmp4;
                    return MdLayout.Mp4;
                }
                if (type == "mdat") return MdLayout.Mp4;
                p += size;
            }
            return sawFtyp ? MdLayout.Mp4 : MdLayout.Unknown;
        }

        public static MdOutput ChooseOutput(IList<MdInput> inputs, MdOutput requested)
        {
            if (requested != MdOutput.Auto) return requested;
            if (inputs == null) return MdOutput.Mp4;
            int count = 0;
            bool webm = false, video = false, allMp3 = true;
            foreach (MdInput input in inputs)
            {
                if (input == null) continue;
                MdLayout layout = input.Layout == MdLayout.Unknown ? ProbeLayout(input.Path) : input.Layout;
                if (IsSubtitle(input, layout)) continue;
                count++;
                if (layout == MdLayout.WebM) webm = true;
                bool audioOnly = input.Kind == MdTrackKind.Audio || layout == MdLayout.Adts || layout == MdLayout.Mp3;
                if (!audioOnly) video = true;
                if (layout != MdLayout.Mp3) allMp3 = false;
            }
            if (count == 0) return MdOutput.Mp4;
            if (webm) return MdOutput.WebM;
            if (!video) return allMp3 ? MdOutput.Mp3 : MdOutput.M4a;
            return MdOutput.Mp4;
        }

        public static bool Mp3Available { get { return MdMp3.EncoderPresent(); } }

        internal static bool IsSubtitle(MdInput input, MdLayout layout)
        {
            return layout == MdLayout.Vtt || layout == MdLayout.Ttml || input.Kind == MdTrackKind.Subtitles;
        }

        internal static string Extension(MdOutput output)
        {
            switch (output)
            {
                case MdOutput.WebM: return ".webm";
                case MdOutput.M4a: return ".m4a";
                case MdOutput.Mp3: return ".mp3";
                case MdOutput.Ts: return ".ts";
                default: return ".mp4";
            }
        }

        // ------------------------------------------------------------------ //
        private static MdMuxResult Fail(MdMuxResult r, string error)
        {
            r.Ok = false;
            r.Error = error;
            return r;
        }

        internal static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        // Удалить только свой временный файл по точному имени.
        internal static void DeleteOwn(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static MdMuxResult RunCore(MdMuxJob job)
        {
            MdMuxResult res = new MdMuxResult();
            if (job == null || job.Inputs.Count == 0) return Fail(res, Tr.S("Нет дорожек для склейки", "Nothing to assemble"));
            string outPath;
            try { outPath = Path.GetFullPath(job.OutPath); }
            catch (ArgumentException) { return Fail(res, Tr.S("Неверный путь результата", "Invalid output path")); }
            catch (NotSupportedException) { return Fail(res, Tr.S("Неверный путь результата", "Invalid output path")); }
            List<MdInput> av = new List<MdInput>();
            List<MdInput> subs = new List<MdInput>();
            foreach (MdInput src in job.Inputs)
            {
                if (src == null || string.IsNullOrEmpty(src.Path) || !File.Exists(src.Path))
                    return Fail(res, Tr.S("Нет файла дорожки: ", "Track file is missing: ") + (src == null ? "" : Path.GetFileName(src.Path ?? "")));
                if (DlFiles.IsReparse(src.Path)) return Fail(res, Tr.S("Файл дорожки — ссылка (junction/symlink), не открываю: ", "The track file is a link (junction/symlink), refusing: ") + Path.GetFileName(src.Path));
                MdInput input = new MdInput();
                input.Path = Path.GetFullPath(src.Path);
                input.Kind = src.Kind;
                input.Codec = src.Codec ?? "";
                input.Language = src.Language ?? "";
                input.Name = src.Name ?? "";
                input.Layout = src.Layout == MdLayout.Unknown ? ProbeLayout(src.Path) : src.Layout;
                if (input.Layout == MdLayout.Unknown) return Fail(res, Tr.S("Не удалось распознать формат файла: ", "Unrecognised file format: ") + Path.GetFileName(src.Path));
                if (IsSubtitle(input, input.Layout)) subs.Add(input); else av.Add(input);
            }
            if (av.Count == 0) return Fail(res, Tr.S("Нет дорожек видео или звука", "No video or audio tracks"));
            MdOutput output = ChooseOutput(av, job.Output);
            string final = Path.ChangeExtension(outPath, Extension(output));
            string tmp = outPath + ".tmp";
            foreach (MdInput input in job.Inputs)
                if (input != null && (SamePath(final, input.Path) || SamePath(tmp, input.Path)))
                    return Fail(res, Tr.S("Итоговый файл совпадает с одной из дорожек", "The output file is one of the inputs"));
            DeleteOwn(tmp);
            MdProgress progress = new MdProgress(job.Progress, av);
            string error = null;
            bool ok = false;
            long base100 = 0;
            try
            {
                switch (output)
                {
                    case MdOutput.Mp4:
                    case MdOutput.M4a:
                        ok = MdMp4Writer.Write(av, output == MdOutput.M4a, tmp, job, progress, res, out base100, out error);
                        break;
                    case MdOutput.WebM:
                        ok = MdMkv.Merge(av, tmp, job, progress, res, out base100, out error);
                        break;
                    case MdOutput.Mp3:
                        ok = MdMp3.Write(av, tmp, job, progress, res, out base100, out error);
                        break;
                    case MdOutput.Ts:
                        ok = ConcatTs(av, tmp, job, progress, res, out base100, out error);
                        break;
                }
            }
            catch (Exception ex)
            {
                ok = false;
                error = Tr.S("Склейка не удалась: ", "Assembly failed: ") + ex.Message;
            }
            if (!ok)
            {
                DeleteOwn(tmp);
                return Fail(res, string.IsNullOrEmpty(error) ? Tr.S("Склейка не удалась", "Assembly failed") : error);
            }
            try
            {
                if (File.Exists(final)) File.Delete(final);
                File.Move(tmp, final);
            }
            catch (IOException ex)
            {
                DeleteOwn(tmp);
                return Fail(res, Tr.S("Не удалось переименовать результат: ", "Could not rename the result: ") + ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                DeleteOwn(tmp);
                return Fail(res, Tr.S("Не удалось переименовать результат: ", "Could not rename the result: ") + ex.Message);
            }
            res.Ok = true;
            res.Error = "";
            res.OutPath = final;
            res.Output = output;
            MdSubs.WriteSidecars(subs, final, base100, res.SubtitleFiles);
            progress.Done();
            return res;
        }

        // TS как есть: байты входов подряд (HLS-сегменты и так склеиваются этим способом).
        private static bool ConcatTs(List<MdInput> av, string tmp, MdMuxJob job, MdProgress progress, MdMuxResult res, out long base100, out string error)
        {
            base100 = 0;
            error = null;
            foreach (MdInput input in av)
                if (input.Layout != MdLayout.Ts) { error = Tr.S("В TS собираются только дорожки MPEG-TS", "Only MPEG-TS tracks can be assembled into TS"); return false; }
            // Время для субтитров: первая отметка первого входа, как у проигрывателей.
            using (MdAuSource first = new MdAuSource(new MdTsReader(av[0].Path, true), true, true))
            {
                string e;
                if (first.Prime(out e)) base100 = first.FirstTime;
            }
            byte[] buf = new byte[1 << 16];
            long done = 0;
            using (FileStream o = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                foreach (MdInput input in av)
                    using (FileStream i = new FileStream(input.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    {
                        int n;
                        while ((n = i.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (job.Cancel != null && job.Cancel()) { error = MdProgress.Cancelled; return false; }
                            o.Write(buf, 0, n);
                            done += n;
                            progress.Report(done);
                        }
                    }
            return true;
        }
    }

    // ------------------------------------------------------------------ //
    //  Прогресс не чаще раза в 200 мс
    // ------------------------------------------------------------------ //
    internal sealed class MdProgress
    {
        public static string Cancelled { get { return Tr.S("Склейка отменена", "Assembly cancelled"); } }

        private readonly Action<double> _cb;
        private readonly long _total;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private long _last = -1000;

        public MdProgress(Action<double> cb, List<MdInput> inputs)
        {
            _cb = cb;
            foreach (MdInput i in inputs)
                try { _total += new FileInfo(i.Path).Length; }
                catch (IOException) { }
        }

        public void Report(long done)
        {
            if (_cb == null || _total <= 0) return;
            long now = _clock.ElapsedMilliseconds;
            if (now - _last < 200) return;
            _last = now;
            _cb(Math.Max(0, Math.Min(1, (double)done / _total)));
        }

        public void Done() { if (_cb != null) _cb(1); }
    }

    // ------------------------------------------------------------------ //
    //  Media Foundation: то, чего нет в Capture.Video
    // ------------------------------------------------------------------ //
    internal static class MdMf
    {
        [DllImport("mfplat.dll", CharSet = CharSet.Unicode)] public static extern int MFCreateFile(int access, int openMode, int flags, string url, out IntPtr byteStream);
        [DllImport("mfreadwrite.dll")] public static extern int MFCreateSourceReaderFromByteStream(IntPtr byteStream, IntPtr attrs, out IntPtr reader);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int DSetBlob(IntPtr s, ref Guid k, [In] byte[] buf, uint size);
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)] public delegate int DSetString(IntPtr s, ref Guid k, [MarshalAs(UnmanagedType.LPWStr)] string v);

        public static readonly Guid IidAttributes = new Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3");
        public static readonly Guid ByteStreamContentType = new Guid("fc358289-3cb6-460c-a424-b6681260375a");
        public static readonly Guid SeqHeader = new Guid("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
        public static readonly Guid UserData = new Guid("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        public static readonly Guid AacPayload = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        public static readonly Guid AacProfile = new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456");
        public static readonly Guid DecodeTimestamp = new Guid("73a954d4-09e2-4861-befc-94bd97c08e6e");
        public static readonly Guid FmtMp3 = new Guid("00000055-0000-0010-8000-00AA00389B71");
        public static readonly Guid ContainerMp3 = new Guid("e438b912-83f1-4de6-9e3a-9ffbc6dd24d1");
        public static readonly Guid CatAudioEncoder = new Guid("91c64bd0-f91e-4d8c-9276-db248279d975");

        public const uint AnyStream = 0xFFFFFFFE;
        public const uint AllStreams = 0xFFFFFFFE;

        public static void Blob(IntPtr a, Guid k, byte[] v)
        {
            VidCom.Fn<DSetBlob>(a, 26)(a, ref k, v, (uint)v.Length);
        }

        public static IntPtr OpenRead(string path, string contentType)
        {
            IntPtr bs;
            VidCom.Check(MFCreateFile(1, 0, 0, path, out bs), "MFCreateFile(read)");
            IntPtr attrs;
            if (VidCom.QI(bs, IidAttributes, out attrs) >= 0)
            {
                Guid k = ByteStreamContentType;
                VidCom.Fn<DSetString>(attrs, 25)(attrs, ref k, contentType);
                VidCom.Rel(ref attrs);
            }
            return bs;
        }

        public static IntPtr OpenWrite(string path)
        {
            IntPtr bs;
            VidCom.Check(MFCreateFile(3, 4, 0, path, out bs), "MFCreateFile(write)");
            return bs;
        }

        public static void CloseStream(ref IntPtr bs)
        {
            if (bs == IntPtr.Zero) return;
            VidCom.Fn<VidCom.DHr>(bs, 17)(bs);
            VidCom.Rel(ref bs);
        }

        public static IntPtr NewType(Guid major, Guid sub)
        {
            IntPtr t;
            VidCom.Check(VidNative.MFCreateMediaType(out t), "MFCreateMediaType");
            MfA.G(t, MfA.MajorType, major);
            MfA.G(t, MfA.Subtype, sub);
            return t;
        }

        public static IntPtr Sample(byte[] data, long time, long dur)
        {
            IntPtr buf = IntPtr.Zero, sample = IntPtr.Zero;
            try
            {
                VidCom.Check(VidNative.MFCreateMemoryBuffer((uint)Math.Max(1, data.Length), out buf), "MFCreateMemoryBuffer");
                IntPtr p;
                uint max, cur;
                VidCom.Check(VidCom.Fn<VidCom.DLock>(buf, 3)(buf, out p, out max, out cur), "IMFMediaBuffer.Lock");
                Marshal.Copy(data, 0, p, data.Length);
                VidCom.Fn<VidCom.DHr>(buf, 4)(buf);
                VidCom.Fn<VidCom.DUInt>(buf, 6)(buf, (uint)data.Length);
                VidCom.Check(VidNative.MFCreateSample(out sample), "MFCreateSample");
                VidCom.Check(VidCom.Fn<VidCom.DPtr>(sample, 42)(sample, buf), "IMFSample.AddBuffer");
                VidCom.Fn<VidCom.DLong>(sample, 36)(sample, time);
                VidCom.Fn<VidCom.DLong>(sample, 38)(sample, Math.Max(1, dur));
                IntPtr r = sample;
                sample = IntPtr.Zero;
                return r;
            }
            finally
            {
                VidCom.Rel(ref buf);
                VidCom.Rel(ref sample);
            }
        }

        // 90 кГц → 100 нс (с округлением вниз и для отрицательных).
        public static long Hns(long t90)
        {
            return t90 >= 0 ? t90 * 1000 / 9 : -((-t90 * 1000 + 8) / 9);
        }
    }

    // ------------------------------------------------------------------ //
    //  Источники сэмплов для приёмника MP4
    // ------------------------------------------------------------------ //
    internal abstract class MdStreamSource : IDisposable
    {
        public string Error = "";
        public abstract bool Prime(out string error);   // начало потока: дорожки, конфигурация, первая отметка
        public abstract int Count { get; }
        public abstract bool IsVideo(int stream);
        public abstract IntPtr CreateType(int stream);  // новый IMFMediaType, освобождает вызывающий
        public abstract long FirstTime { get; }         // 100 нс, самая ранняя отметка (DTS)
        public abstract bool HasHead { get; }           // false — конец (или Error)
        public abstract long HeadTime { get; }          // 100 нс, DTS головы
        public abstract int HeadStream { get; }
        public abstract IntPtr TakeHead(long base100, out long end100);   // IMFSample, освобождает вызывающий
        public abstract long Position { get; }
        public abstract void Dispose();
    }

    // Кадры своего разборщика → сэмплы MF.
    internal sealed class MdAuSource : MdStreamSource
    {
        private const long PrimeBytes = 32L << 20;

        private readonly IMdAuReader _reader;
        private readonly bool _video, _audio;
        private readonly Queue<MdAu> _queue = new Queue<MdAu>();
        private readonly List<MdEsTrack> _selected = new List<MdEsTrack>();
        private int[] _map = new int[0];                // индекс дорожки → поток источника
        private MdAu _head;
        private bool _end;
        private long _first = long.MaxValue;
        public bool RequireMp4Codecs = true;

        public MdAuSource(IMdAuReader reader, bool video, bool audio)
        {
            _reader = reader;
            _video = video;
            _audio = audio;
        }

        public IMdAuReader Reader { get { return _reader; } }
        public List<MdEsTrack> Selected { get { return _selected; } }

        private bool Wanted(MdEsTrack t) { return t.Video ? _video : _audio; }

        public override bool Prime(out string error)
        {
            error = null;
            while (true)
            {
                bool ready = _reader.Tracks.Count > 0;
                foreach (MdEsTrack t in _reader.Tracks)
                    if (Wanted(t) && (!t.Configured || t.Frames < (t.Video ? 2 : 1))) ready = false;
                if (ready || _reader.Position >= PrimeBytes) break;
                MdAu au;
                if (!_reader.Next(out au)) { _end = true; break; }
                _queue.Enqueue(au);
            }
            if (_reader.Error.Length > 0) { error = _reader.Error; return false; }
            MdTsReader ts = _reader as MdTsReader;
            if (ts != null) ts.FreezeTracks();
            _map = new int[_reader.Tracks.Count];
            bool unsupported = false;
            foreach (MdEsTrack t in _reader.Tracks)
            {
                _map[t.Index] = -1;
                if (!Wanted(t) || !t.Configured || t.Frames == 0) continue;
                if (RequireMp4Codecs && t.Codec == MdCodec.Mp3 && t.Mp3Layer != 3) { unsupported = true; continue; }
                _map[t.Index] = _selected.Count;
                _selected.Add(t);
            }
            if (_selected.Count == 0)
            {
                error = unsupported
                    ? Tr.S("Звук MPEG Layer I/II в MP4 без перекодирования не собирается", "MPEG Layer I/II audio cannot go into MP4 without transcoding")
                    : Tr.S("В файле нет подходящих дорожек (H.264, HEVC, AAC, MP3): ", "No supported tracks (H.264, HEVC, AAC, MP3) in: ") + "TS/ES";
                return false;
            }
            foreach (MdAu au in _queue)
                if (_map[au.Track] >= 0 && au.Dts < _first) _first = au.Dts;
            Advance();
            if (_head != null && _head.Dts < _first) _first = _head.Dts;
            if (_first == long.MaxValue) _first = 0;
            return Error.Length == 0;
        }

        private void Advance()
        {
            _head = null;
            while (true)
            {
                MdAu au = null;
                if (_queue.Count > 0) au = _queue.Dequeue();
                else if (!_end && !_reader.Next(out au)) { _end = true; au = null; }
                if (au == null)
                {
                    if (_reader.Error.Length > 0) Error = _reader.Error;
                    return;
                }
                if (au.Track < _map.Length && _map[au.Track] >= 0) { _head = au; return; }
            }
        }

        public override int Count { get { return _selected.Count; } }
        public override bool IsVideo(int stream) { return _selected[stream].Video; }
        public override long FirstTime { get { return MdMf.Hns(_first); } }
        public override bool HasHead { get { return _head != null; } }
        public override long HeadTime { get { return _head == null ? long.MaxValue : MdMf.Hns(_head.Dts); } }
        public override int HeadStream { get { return _head == null ? -1 : _map[_head.Track]; } }
        public override long Position { get { return _reader.Position; } }

        public override IntPtr TakeHead(long base100, out long end100)
        {
            MdAu au = _head;
            MdEsTrack t = _reader.Tracks[au.Track];
            long pts = MdMf.Hns(au.Pts) - base100;
            long dur = Math.Max(1, MdMf.Hns(au.Dur));
            IntPtr sample = MdMf.Sample(au.Data, pts, dur);
            MfA.U32(sample, MfA.CleanPoint, au.Key ? 1u : 0u);
            if (t.Video) MfA.U64(sample, MdMf.DecodeTimestamp, (ulong)Math.Max(0, MdMf.Hns(au.Dts) - base100));
            end100 = pts + dur;
            Advance();
            return sample;
        }

        public override IntPtr CreateType(int stream)
        {
            MdEsTrack t = _selected[stream];
            IntPtr type = IntPtr.Zero;
            try
            {
                switch (t.Codec)
                {
                    case MdCodec.H264:
                    case MdCodec.Hevc:
                        {
                            type = MdMf.NewType(MfA.MediaVideo, t.Codec == MdCodec.H264 ? MfA.FmtH264 : MfA.FmtHevc);
                            long dur = t.FrameDur > 0 ? t.FrameDur : 3600;
                            long g = Gcd(90000, dur);
                            MfA.Pair(type, MfA.FrameSize, (uint)t.Width, (uint)t.Height);
                            MfA.Pair(type, MfA.FrameRate, (uint)(90000 / g), (uint)(dur / g));
                            MfA.Pair(type, MfA.PixelAspect, 1, 1);
                            MfA.U32(type, MfA.InterlaceMode, 2);
                            long seconds90 = Math.Max(1, t.Frames * dur);
                            MfA.U32(type, MfA.AvgBitrate, (uint)Math.Min(uint.MaxValue, Math.Max(1000, t.Bytes * 8 * 90000 / seconds90)));
                            MdMf.Blob(type, MdMf.SeqHeader, t.SeqHeader);
                            break;
                        }
                    case MdCodec.Aac:
                        {
                            type = MdMf.NewType(MfA.MediaAudio, MfA.FmtAac);
                            MfA.U32(type, MfA.AudioSampleRate, (uint)t.SampleRate);
                            MfA.U32(type, MfA.AudioChannels, (uint)t.Channels);
                            MfA.U32(type, MfA.AudioBits, 16);
                            MfA.U32(type, MfA.AudioBlockAlign, 1);
                            MfA.U32(type, MdMf.AacPayload, 0);
                            MfA.U32(type, MdMf.AacProfile, 0x29);
                            long seconds = Math.Max(1, t.Frames * 1024 / Math.Max(1, t.SampleRate));
                            MfA.U32(type, MfA.AudioAvgBytes, (uint)Math.Max(1000, t.Bytes / seconds));
                            // HEAACWAVEINFO без WAVEFORMATEX: payload 0 (сырой AAC), профиль не указан, затем AudioSpecificConfig
                            byte[] ud = new byte[12 + t.Asc.Length];
                            ud[2] = 0xFE;
                            Buffer.BlockCopy(t.Asc, 0, ud, 12, t.Asc.Length);
                            MdMf.Blob(type, MdMf.UserData, ud);
                            break;
                        }
                    case MdCodec.Mp3:
                        {
                            type = MdMf.NewType(MfA.MediaAudio, MdMf.FmtMp3);
                            MfA.U32(type, MfA.AudioSampleRate, (uint)t.SampleRate);
                            MfA.U32(type, MfA.AudioChannels, (uint)t.Channels);
                            MfA.U32(type, MfA.AudioAvgBytes, (uint)(t.Mp3Bitrate * 125));
                            MfA.U32(type, MfA.AudioBlockAlign, 1);
                            // MPEGLAYER3WAVEFORMAT без WAVEFORMATEX: wID=1, флаги, размер блока, кадров в блоке, задержка кодека
                            byte[] ud = new byte[12];
                            ud[0] = 1;
                            ud[6] = (byte)t.Mp3FrameBytes;
                            ud[7] = (byte)(t.Mp3FrameBytes >> 8);
                            ud[8] = 1;
                            ud[10] = 0x71;
                            ud[11] = 0x05;
                            MdMf.Blob(type, MdMf.UserData, ud);
                            break;
                        }
                }
                IntPtr r = type;
                type = IntPtr.Zero;
                return r;
            }
            finally { VidCom.Rel(ref type); }
        }

        private static long Gcd(long a, long b)
        {
            while (b != 0) { long t = a % b; a = b; b = t; }
            return Math.Max(1, a);
        }

        public override void Dispose() { _reader.Dispose(); }
    }

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
            error = null;
            _bs = MdMf.OpenRead(_path, "video/mp4");
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
            if (_head != null) VidCom.Rel(ref _head.Sample);
            while (_queue.Count > 0) { Pending p = _queue.Dequeue(); VidCom.Rel(ref p.Sample); }
            for (int i = 0; i < _types.Count; i++) { IntPtr t = _types[i]; VidCom.Rel(ref t); }
            _types.Clear();
            VidCom.Rel(ref _reader);
            MdMf.CloseStream(ref _bs);
        }
    }

    // ------------------------------------------------------------------ //
    //  Запись MP4/M4A: источники по порядку отметок → IMFSinkWriter
    // ------------------------------------------------------------------ //
    internal static class MdMp4Writer
    {
        internal static List<MdStreamSource> OpenSources(List<MdInput> av, bool audioOnly, bool allowTruncated, out string error)
        {
            error = null;
            List<MdStreamSource> list = new List<MdStreamSource>();
            bool done = false;
            try
            {
                List<MdStreamSource> r = OpenSourcesCore(av, audioOnly, allowTruncated, list, out error);
                done = r != null;
                return r;
            }
            finally
            {
                if (!done) foreach (MdStreamSource s in list) s.Dispose();
            }
        }

        private static List<MdStreamSource> OpenSourcesCore(List<MdInput> av, bool audioOnly, bool allowTruncated, List<MdStreamSource> list, out string error)
        {
            error = null;
            foreach (MdInput input in av)
            {
                bool video = !audioOnly && input.Kind != MdTrackKind.Audio;
                bool audio = input.Kind != MdTrackKind.Video;
                switch (input.Layout)
                {
                    case MdLayout.Ts:
                        list.Add(new MdAuSource(new MdTsReader(input.Path, allowTruncated), video, audio));
                        break;
                    case MdLayout.Adts:
                        list.Add(new MdAuSource(new MdEsFileReader(input.Path, MdCodec.Aac, allowTruncated), false, audio));
                        break;
                    case MdLayout.Mp3:
                        list.Add(new MdAuSource(new MdEsFileReader(input.Path, MdCodec.Mp3, allowTruncated), false, audio));
                        break;
                    case MdLayout.Mp4:
                    case MdLayout.Fmp4:
                        list.Add(new MdReaderSource(input.Path, video, audio));
                        break;
                    default:
                        error = input.Layout == MdLayout.WebM
                            ? (audioOnly ? Tr.S("Звук из WebM (Opus/Vorbis) в M4A без перекодирования не собирается — выберите WebM", "Audio from WebM (Opus/Vorbis) cannot go into M4A without transcoding — choose WebM")
                                         : Tr.S("Дорожки WebM (VP9/Opus) в MP4 без перекодирования не собираются — выберите WebM или «Авто»", "WebM tracks (VP9/Opus) cannot go into MP4 without transcoding — choose WebM or Auto"))
                            : Tr.S("Этот формат дорожки в MP4 не собирается: ", "This track format cannot go into MP4: ") + input.Layout;
                        return null;
                }
            }
            return list;
        }

        // Первая отметка каждого входа TS/ES разворачивается относительно первого такого входа (одна трансляция).
        internal static bool PrimeAll(List<MdStreamSource> sources, out long base100, out string error)
        {
            base100 = long.MaxValue;
            error = null;
            MdTimeline reference = null;
            foreach (MdStreamSource s in sources)
            {
                MdAuSource au = s as MdAuSource;
                if (au != null && reference != null && reference.HasReference) au.Reader.Timeline.Seed(reference.Reference);
                if (!s.Prime(out error)) return false;
                if (au != null && reference == null) reference = au.Reader.Timeline;
                if (s.FirstTime < base100) base100 = s.FirstTime;
            }
            if (base100 == long.MaxValue) base100 = 0;
            return true;
        }

        public static bool Write(List<MdInput> av, bool audioOnly, string tmp, MdMuxJob job, MdProgress progress, MdMuxResult res, out long base100, out string error)
        {
            base100 = 0;
            List<MdStreamSource> sources = OpenSources(av, audioOnly, job.AllowTruncated, out error);
            if (sources == null) return false;
            IntPtr attrs = IntPtr.Zero, writer = IntPtr.Zero, bs = IntPtr.Zero;
            VidCom.Check(VidNative.MFStartup(0x20070, 0), "MFStartup");
            try
            {
                if (!PrimeAll(sources, out base100, out error)) return false;
                int streams = 0;
                foreach (MdStreamSource s in sources) streams += s.Count;
                if (streams == 0) { error = Tr.S("Нет дорожек для записи", "No tracks to write"); return false; }
                VidCom.Check(VidNative.MFCreateAttributes(out attrs, 2), "MFCreateAttributes");
                MfA.G(attrs, MfA.ContainerType, MfA.ContainerMp4);
                MfA.U32(attrs, MfA.DisableThrottling, 1);
                bs = MdMf.OpenWrite(tmp);
                int hr = VidNative.MFCreateSinkWriterFromURL(null, bs, attrs, out writer);
                if (hr < 0) { error = Tr.S("Media Foundation не создала файл MP4 (", "Media Foundation could not create the MP4 (") + VidCom.Hex(hr) + ")"; return false; }
                List<uint[]> maps = new List<uint[]>();
                foreach (MdStreamSource s in sources)
                {
                    uint[] m = new uint[s.Count];
                    for (int i = 0; i < s.Count; i++)
                    {
                        IntPtr type = s.CreateType(i);
                        try
                        {
                            uint idx;
                            hr = VidCom.Fn<VidCom.DPtrOutUInt>(writer, 3)(writer, type, out idx);
                            if (hr >= 0) hr = VidCom.Fn<VidCom.DUIntPtrPtr>(writer, 4)(writer, idx, type, IntPtr.Zero);
                            if (hr < 0) { error = Tr.S("Дорожку нельзя записать в MP4 без перекодирования (", "This track cannot be written to MP4 without transcoding (") + VidCom.Hex(hr) + ")"; return false; }
                            m[i] = idx;
                        }
                        finally { VidCom.Rel(ref type); }
                    }
                    maps.Add(m);
                }
                hr = VidCom.Fn<VidCom.DHr>(writer, 5)(writer);
                if (hr < 0) { error = "BeginWriting " + VidCom.Hex(hr); return false; }
                VidCom.DUIntPtr write = VidCom.Fn<VidCom.DUIntPtr>(writer, 6);
                long end = 0;
                int count = 0;
                while (true)
                {
                    int pick = -1;
                    long best = long.MaxValue;
                    for (int i = 0; i < sources.Count; i++)
                        if (sources[i].HasHead && sources[i].HeadTime < best) { best = sources[i].HeadTime; pick = i; }
                    if (pick < 0) break;
                    MdStreamSource src = sources[pick];
                    int stream = src.HeadStream;
                    bool video = src.IsVideo(stream);
                    long sampleEnd;
                    IntPtr sample = src.TakeHead(base100, out sampleEnd);
                    try
                    {
                        hr = write(writer, maps[pick][stream], sample);
                        if (hr < 0) { error = Tr.S("Media Foundation не приняла кадр (", "Media Foundation rejected a frame (") + VidCom.Hex(hr) + ")"; return false; }
                    }
                    finally { VidCom.Rel(ref sample); }
                    if (video) res.VideoFrames++; else res.AudioFrames++;
                    if (sampleEnd > end) end = sampleEnd;
                    if ((++count & 63) == 0)
                    {
                        if (job.Cancel != null && job.Cancel()) { error = MdProgress.Cancelled; return false; }
                        long pos = 0;
                        foreach (MdStreamSource s in sources) pos += s.Position;
                        progress.Report(pos);
                    }
                }
                foreach (MdStreamSource s in sources)
                    if (s.Error.Length > 0) { error = s.Error; return false; }
                if (res.VideoFrames + res.AudioFrames == 0) { error = Tr.S("Нет ни одного целого кадра", "Not a single complete frame"); return false; }
                hr = VidCom.Fn<VidCom.DHr>(writer, 11)(writer);
                if (hr < 0) { error = Tr.S("Media Foundation не завершила файл (", "Media Foundation could not finalize the file (") + VidCom.Hex(hr) + ")"; return false; }
                res.DurationMs = end / 10000;
                return true;
            }
            finally
            {
                VidCom.Rel(ref writer);
                MdMf.CloseStream(ref bs);
                VidCom.Rel(ref attrs);
                foreach (MdStreamSource s in sources) s.Dispose();
                VidNative.MFShutdown();
            }
        }
    }
}
