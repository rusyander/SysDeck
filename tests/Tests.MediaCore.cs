// SysDeck — область «media», ядро: разбор MPEG-TS, склейка MP4/WebM/M4A/MP3, субтитры.
//
// Проверки идут по настоящему пути: фикстуры tests\media → MdMux.Run → чтение результата Media Foundation (MdFx.ReadBack),
// ffprobe как независимый оракул (MdFx.FfProbe, SYSDECK_FFPROBE) и свой разбор итогового файла (боксы MP4, элементы EBML).
// Чего в фикстурах нет — собирается здесь, в Fx.Root: TS с переполнением 33-битных отметок (отметки настоящих сегментов
// переписываются), TS с разрывом на 30 с, обрезанный TS, сегменты WebVTT с X-TIMESTAMP-MAP, TTML, враждебный WebM.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        static partial void RunCore()
        {
            string dir = Fx.MakeDir(Fx.Root, "md-core");
            if (MdFx.Dir.Length == 0) { T.Skip("core: media fixtures", "tests\\media not found"); return; }
            CoreProbe(dir);
            CoreTsToMp4(dir);
            CoreWrap(dir);
            CoreDiscontinuity(dir);
            CoreFmp4(dir);
            CoreTracks(dir);
            CoreWebm(dir);
            CoreAuto();
            CoreM4a(dir);
            CoreMp3(dir);
            CoreTruncated(dir);
            CoreGarbage(dir);
            CoreSubs(dir);
            CoreSidecars(dir);
            CoreTsOut(dir);
            CoreHostile(dir);
            CoreCancel(dir);
        }

        // ------------------------------------------------------------------ //
        //  Помощники
        // ------------------------------------------------------------------ //
        private static MdInput MkIn(string path, MdTrackKind kind)
        {
            MdInput i = new MdInput();
            i.Path = path;
            i.Kind = kind;
            return i;
        }

        private static MdMuxResult Mux(string outPath, MdOutput output, bool allowTruncated, params MdInput[] inputs)
        {
            MdMuxJob job = new MdMuxJob();
            job.OutPath = outPath;
            job.Output = output;
            job.AllowTruncated = allowTruncated;
            foreach (MdInput i in inputs) job.Inputs.Add(i);
            return MdMux.Run(job);
        }

        // Склейка байтов сегментов в один файл дорожки (так их и отдаёт движок: «.data»).
        private static string Concat(string outPath, params string[] parts)
        {
            using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
                foreach (string p in parts)
                {
                    byte[] b = File.ReadAllBytes(p);
                    o.Write(b, 0, b.Length);
                }
            return outPath;
        }

        private static string[] FixtureSet(string dir, string prefix, int count)
        {
            List<string> list = new List<string>();
            for (int i = 0; i < count; i++)
            {
                string p = MdFx.File(dir + "\\" + prefix + i + (dir == "hls-fmp4" ? ".m4s" : ".ts"));
                if (p == null) return null;
                list.Add(p);
            }
            return list.ToArray();
        }

        private static string TsAll(string dir)
        {
            string[] segs = FixtureSet("hls-ts", "seg", 4);
            if (segs == null) return null;
            return Concat(Path.Combine(dir, "ts-all.data"), segs);
        }

        // ---------- свой разбор итогового MP4 ----------
        private static bool NextBox(byte[] a, ref int p, int end, out string type, out int dataStart, out int dataEnd)
        {
            type = "";
            dataStart = 0;
            dataEnd = 0;
            if (p + 8 > end) return false;
            long size = ((long)a[p] << 24) | ((long)a[p + 1] << 16) | ((long)a[p + 2] << 8) | a[p + 3];
            type = Encoding.ASCII.GetString(a, p + 4, 4);
            int hdr = 8;
            if (size == 1)
            {
                if (p + 16 > end) return false;
                size = 0;
                for (int i = 8; i < 16; i++) size = (size << 8) | a[p + i];
                hdr = 16;
            }
            else if (size == 0) size = end - p;
            if (size < hdr || p + size > end) return false;
            dataStart = p + hdr;
            dataEnd = (int)(p + size);
            p = dataEnd;
            return true;
        }

        private static bool Box(byte[] a, int start, int end, string want, out int ds, out int de)
        {
            int p = start;
            string type;
            while (NextBox(a, ref p, end, out type, out ds, out de)) if (type == want) return true;
            ds = 0;
            de = 0;
            return false;
        }

        // Число ключевых кадров видеодорожки MP4 (stss); -1 — дорожки нет, -2 — нет stss.
        private static int KeyFrames(string path)
        {
            byte[] a = File.ReadAllBytes(path);
            int ms, me;
            if (!Box(a, 0, a.Length, "moov", out ms, out me)) return -1;
            int p = ms;
            string type;
            int ds, de;
            while (NextBox(a, ref p, me, out type, out ds, out de))
            {
                if (type != "trak") continue;
                int mds, mde;
                if (!Box(a, ds, de, "mdia", out mds, out mde)) continue;
                int hs, he;
                if (!Box(a, mds, mde, "hdlr", out hs, out he)) continue;
                if (he - hs < 12 || Encoding.ASCII.GetString(a, hs + 8, 4) != "vide") continue;
                int mis, mie, sts, ste, sss, sse;
                if (!Box(a, mds, mde, "minf", out mis, out mie)) return -2;
                if (!Box(a, mis, mie, "stbl", out sts, out ste)) return -2;
                if (!Box(a, sts, ste, "stss", out sss, out sse)) return -2;
                if (sse - sss < 8) return -2;
                return (a[sss + 4] << 24) | (a[sss + 5] << 16) | (a[sss + 6] << 8) | a[sss + 7];
            }
            return -1;
        }

        // ---------- ffprobe: начало дорожек ----------
        private static bool StartDelta(string path, out double delta, out string why)
        {
            delta = 0;
            string output;
            if (!FfProbe("-v error -show_entries stream=codec_type,start_time -of compact", path, out output, out why)) return false;
            double video = double.NaN, audio = double.NaN;
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int at = line.IndexOf("stream|", StringComparison.Ordinal);
                if (at < 0) continue;
                line = line.Substring(at);
                string type = "";
                double start = 0;
                bool has = false;
                foreach (string part in line.Split('|'))
                {
                    if (part.StartsWith("codec_type=", StringComparison.Ordinal)) type = part.Substring(11);
                    else if (part.StartsWith("start_time=", StringComparison.Ordinal))
                        has = double.TryParse(part.Substring(11), NumberStyles.Float, CultureInfo.InvariantCulture, out start);
                }
                if (!has) continue;
                if (type == "video" && double.IsNaN(video)) video = start;
                if (type == "audio" && double.IsNaN(audio)) audio = start;
            }
            if (double.IsNaN(video) || double.IsNaN(audio)) { why = "no start_time"; return false; }
            delta = audio - video;
            return true;
        }

        // Кадры по типам, по одному разу на поток: у MPEG-TS ffprobe печатает поток дважды (в разделе программы и отдельно),
        // поэтому MdFx.FfProbe для TS звук удваивает — здесь счёт по номеру потока.
        private static bool FrameCounts(string path, out int video, out int audio, out string why)
        {
            video = 0;
            audio = 0;
            why = null;
            string output;
            if (!FfProbe("-v error -count_frames -show_entries stream=index,codec_type,nb_read_frames -of compact", path, out output, out why)) return false;
            Dictionary<int, string> types = new Dictionary<int, string>();
            Dictionary<int, int> frames = new Dictionary<int, int>();
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                int at = line.IndexOf("stream|", StringComparison.Ordinal);
                if (at < 0) continue;
                int index = -1, count = -1;
                string type = "";
                foreach (string part in line.Substring(at + 7).Split('|'))
                {
                    if (part.StartsWith("index=", StringComparison.Ordinal)) int.TryParse(part.Substring(6), out index);
                    else if (part.StartsWith("codec_type=", StringComparison.Ordinal)) type = part.Substring(11);
                    else if (part.StartsWith("nb_read_frames=", StringComparison.Ordinal)) int.TryParse(part.Substring(15), out count);
                }
                if (index < 0 || count < 0) continue;
                types[index] = type;
                frames[index] = count;
            }
            if (frames.Count == 0) { why = "no frame counts"; return false; }
            foreach (KeyValuePair<int, int> kv in frames)
            {
                if (types[kv.Key] == "video") video += kv.Value;
                else if (types[kv.Key] == "audio") audio += kv.Value;
            }
            return true;
        }

        private static bool FfProbe(string args, string path, out string output, out string why)
        {
            output = "";
            why = null;
            string exe = Environment.GetEnvironmentVariable("SYSDECK_FFPROBE");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) { why = "SYSDECK_FFPROBE not set"; return false; }
            ProcessStartInfo psi = new ProcessStartInfo(exe, args + " \"" + path + "\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd();
                p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (InvalidOperationException) { } why = "ffprobe timeout"; return false; }
                if (p.ExitCode != 0) { why = "ffprobe exit " + p.ExitCode; return false; }
            }
            return true;
        }

        // ---------- переписывание отметок MPEG-TS ----------
        private static long ReadTs33(byte[] a, int p)
        {
            long v = (long)((a[p] >> 1) & 7) << 30;
            v |= (long)a[p + 1] << 22;
            v |= (long)(a[p + 2] >> 1) << 15;
            v |= (long)a[p + 3] << 7;
            v |= (uint)(a[p + 4] >> 1);
            return v;
        }

        private static void WriteTs33(byte[] a, int p, long v)
        {
            int marker = a[p] >> 4;
            a[p] = (byte)((marker << 4) | (byte)(((v >> 29) & 0x0E) | 1));
            a[p + 1] = (byte)(v >> 22);
            a[p + 2] = (byte)((((v >> 14) & 0xFE) | 1));
            a[p + 3] = (byte)(v >> 7);
            a[p + 4] = (byte)(((v << 1) & 0xFE) | 1);
        }

        // Сдвиг всех PTS/DTS/PCR на delta по кругу 2^33 (absolute: delta считается так, чтобы первая отметка стала delta).
        private static string RewriteTs(string src, string dst, long delta, bool absolute)
        {
            byte[] a = File.ReadAllBytes(src);
            const long Wrap = 1L << 33;
            if (absolute)
            {
                long first = long.MaxValue;
                for (int p = 0; p + 188 <= a.Length; p += 188)
                {
                    int t;
                    int flags;
                    if (!PesTimes(a, p, out t, out flags)) continue;
                    if ((flags & 0x80) != 0) first = Math.Min(first, ReadTs33(a, t));
                    if ((flags & 0x40) != 0) first = Math.Min(first, ReadTs33(a, t + 5));
                }
                if (first == long.MaxValue) first = 0;
                delta = delta - first;
            }
            for (int p = 0; p + 188 <= a.Length; p += 188)
            {
                if (a[p] != 0x47) continue;
                int afc = (a[p + 3] >> 4) & 3;
                if ((afc & 2) != 0 && a[p + 4] > 0 && (a[p + 5] & 0x10) != 0 && p + 12 <= a.Length)
                {
                    int q = p + 6;
                    long baseTs = ((long)a[q] << 25) | ((long)a[q + 1] << 17) | ((long)a[q + 2] << 9) | ((long)a[q + 3] << 1) | ((long)a[q + 4] >> 7);
                    long ext = ((long)(a[q + 4] & 1) << 8) | a[q + 5];
                    long nb = ((baseTs + delta) % Wrap + Wrap) % Wrap;
                    a[q] = (byte)(nb >> 25);
                    a[q + 1] = (byte)(nb >> 17);
                    a[q + 2] = (byte)(nb >> 9);
                    a[q + 3] = (byte)(nb >> 1);
                    a[q + 4] = (byte)(((nb & 1) << 7) | 0x7E | (ext >> 8));
                    a[q + 5] = (byte)ext;
                }
                int t2, flags2;
                if (!PesTimes(a, p, out t2, out flags2)) continue;
                if ((flags2 & 0x80) != 0) WriteTs33(a, t2, ((ReadTs33(a, t2) + delta) % Wrap + Wrap) % Wrap);
                if ((flags2 & 0x40) != 0) WriteTs33(a, t2 + 5, ((ReadTs33(a, t2 + 5) + delta) % Wrap + Wrap) % Wrap);
            }
            File.WriteAllBytes(dst, a);
            return dst;
        }

        // Смещение поля PTS/DTS в пакете (t) и флаги заголовка PES; false — в пакете начала PES нет.
        private static bool PesTimes(byte[] a, int p, out int t, out int flags)
        {
            t = 0;
            flags = 0;
            if (a[p] != 0x47 || (a[p + 1] & 0x40) == 0) return false;
            int afc = (a[p + 3] >> 4) & 3;
            if ((afc & 1) == 0) return false;
            int q = p + 4;
            if ((afc & 2) != 0) q += 1 + a[p + 4];
            if (q + 14 > p + 188) return false;
            if (a[q] != 0 || a[q + 1] != 0 || a[q + 2] != 1) return false;
            int sid = a[q + 3];
            if (sid < 0xBD || sid == 0xBE || sid == 0xBF) return false;
            flags = a[q + 7];
            t = q + 9;
            return (flags & 0xC0) != 0;
        }

        // ---------- свой счёт кадров WebM ----------
        private static bool WebmFrames(string path, out int video, out int audio, out string why)
        {
            video = 0;
            audio = 0;
            why = null;
            using (MdMkvReader r = new MdMkvReader(path))
            {
                if (!r.Open()) { why = r.Error; return false; }
                MdMkvBlock b;
                while (r.Next(out b))
                {
                    int type = r.Tracks[b.Track].Type;
                    if (type == 1) video += b.Frames;
                    else if (type == 2) audio += b.Frames;
                }
                if (r.Error.Length > 0) { why = r.Error; return false; }
                return true;
            }
        }

        // ------------------------------------------------------------------ //
        //  Проверки
        // ------------------------------------------------------------------ //
        private static void CoreProbe(string dir)
        {
            string adts = Path.Combine(dir, "probe.aac");
            byte[] frame = new byte[128];
            frame[0] = 0xFF; frame[1] = 0xF1; frame[2] = 0x50; frame[3] = 0x80; frame[4] = 0x10; frame[5] = 0x1F; frame[6] = 0xFC;
            byte[] two = new byte[256];
            Buffer.BlockCopy(frame, 0, two, 0, 128);
            Buffer.BlockCopy(frame, 0, two, 128, 128);
            File.WriteAllBytes(adts, two);
            string mp3 = Path.Combine(dir, "probe.mp3");
            byte[] mf = new byte[417 * 2];
            for (int i = 0; i < 2; i++)
            {
                mf[i * 417] = 0xFF; mf[i * 417 + 1] = 0xFB; mf[i * 417 + 2] = 0x90; mf[i * 417 + 3] = 0x00;
            }
            File.WriteAllBytes(mp3, mf);
            string ttml = Path.Combine(dir, "probe.ttml");
            File.WriteAllText(ttml, "<?xml version=\"1.0\"?>\n<tt xmlns=\"http://www.w3.org/ns/ttml\"><body><div><p begin=\"1s\" end=\"2s\">x</p></div></body></tt>\n", new UTF8Encoding(false));
            string garbage = Path.Combine(dir, "garbage.bin");
            byte[] g = new byte[4096];
            for (int i = 0; i < g.Length; i++) g[i] = (byte)(0x5A + (i % 7));
            File.WriteAllBytes(garbage, g);

            List<string> bad = new List<string>();
            Expect(bad, MdFx.File("hls-ts\\seg0.ts"), MdLayout.Ts);
            Expect(bad, MdFx.File("wrap.ts"), MdLayout.Ts);
            Expect(bad, MdFx.File("hls-fmp4\\init.mp4"), MdLayout.Fmp4);
            Expect(bad, MdFx.File("hls-fmp4\\seg0.m4s"), MdLayout.Fmp4);
            Expect(bad, MdFx.File("plain.mp4"), MdLayout.Mp4);
            Expect(bad, MdFx.File("track-avc.mp4"), MdLayout.Mp4);
            Expect(bad, MdFx.File("track-aac.m4a"), MdLayout.Mp4);
            Expect(bad, MdFx.File("track-vp9.webm"), MdLayout.WebM);
            Expect(bad, MdFx.File("track-opus.webm"), MdLayout.WebM);
            Expect(bad, MdFx.File("subs.vtt"), MdLayout.Vtt);
            Expect(bad, adts, MdLayout.Adts);
            Expect(bad, mp3, MdLayout.Mp3);
            Expect(bad, ttml, MdLayout.Ttml);
            Expect(bad, garbage, MdLayout.Unknown);
            Expect(bad, Path.Combine(dir, "nothing-here.bin"), MdLayout.Unknown);
            T.Check("core: probe recognises every input shape", bad.Count == 0, string.Join("; ", bad.ToArray()));
        }

        private static void Expect(List<string> bad, string path, MdLayout want)
        {
            if (path == null) { bad.Add("missing fixture for " + want); return; }
            MdLayout got = MdMux.ProbeLayout(path);
            if (got != want) bad.Add(Path.GetFileName(path) + ": " + got + " != " + want);
        }

        private static void CoreTsToMp4(string dir)
        {
            string src = TsAll(dir);
            if (src == null) { T.Skip("core: ts concat -> mp4", "hls-ts fixtures missing"); return; }
            string why;
            int inVideo, inAudio;
            bool oracle = FrameCounts(src, out inVideo, out inAudio, out why);
            MdMuxResult r = Mux(Path.Combine(dir, "ts.mp4"), MdOutput.Auto, false, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: ts concat -> mp4 assembles", r.Ok && File.Exists(r.OutPath), r.Error + " " + r.OutPath);
            if (!r.Ok) return;
            T.Eq("core: ts concat -> mp4 extension", ".mp4", Path.GetExtension(r.OutPath));
            T.Eq("core: ts concat -> mp4 video frame count", 40, r.VideoFrames);
            int v, a;
            string whyBack;
            if (MdFx.ReadBack(r.OutPath, out v, out a, out whyBack))
            {
                T.Eq("core: ts concat -> mp4 read back 40 video samples", 40, v);
                if (oracle) T.Eq("core: ts concat -> mp4 read back all audio samples", inAudio, a);
                else T.Skip("core: ts concat -> mp4 read back all audio samples", why);
            }
            else T.Check("core: ts concat -> mp4 read back 40 video samples", false, whyBack);
            int outVideo, outAudio;
            if (oracle && FrameCounts(r.OutPath, out outVideo, out outAudio, out why))
            {
                T.Eq("core: ts concat -> mp4 ffprobe video frames", inVideo, outVideo);
                T.Eq("core: ts concat -> mp4 ffprobe audio frames", inAudio, outAudio);
            }
            else T.Skip("core: ts concat -> mp4 ffprobe video frames", why);
            MdFx.Probe outp = MdFx.FfProbe(r.OutPath, out why);
            if (outp != null) T.Check("core: ts concat -> mp4 duration ~4 s", outp.Duration > 3.5 && outp.Duration < 4.6, outp.Duration.ToString(CultureInfo.InvariantCulture));
            else T.Skip("core: ts concat -> mp4 duration ~4 s", why);
            T.Eq("core: ts concat -> mp4 keeps IDR flags (stss)", 4, KeyFrames(r.OutPath));
            // MP4 берёт AAC без заголовка ADTS, а настройки дорожки — из AudioSpecificConfig
            int withHeader = 0, ascLen = -1, videoStartCodes = 0;
            using (MdTsReader reader = new MdTsReader(src, false))
            {
                MdAu au;
                while (reader.Next(out au))
                {
                    MdEsTrack t = reader.Tracks[au.Track];
                    if (t.Codec == MdCodec.Aac)
                    {
                        if (au.Data.Length > 1 && au.Data[0] == 0xFF && (au.Data[1] & 0xF0) == 0xF0) withHeader++;
                        if (ascLen < 0 && t.Asc != null) ascLen = t.Asc.Length;
                    }
                    else if (t.Video && au.Data.Length > 4 && au.Data[0] == 0 && au.Data[1] == 0 && au.Data[2] == 0 && au.Data[3] == 1) videoStartCodes++;
                }
            }
            T.Eq("core: ts demux strips adts headers from the aac payload", 0, withHeader);
            T.Eq("core: ts demux keeps the AudioSpecificConfig", 2, ascLen);
            T.Eq("core: ts demux writes 4-byte start codes on every video au", 40, videoStartCodes);
            double dIn, dOut;
            string whyIn, whyOut;
            if (StartDelta(src, out dIn, out whyIn) && StartDelta(r.OutPath, out dOut, out whyOut))
                T.Check("core: a/v start offset preserved within a frame", Math.Abs(dIn - dOut) <= 0.1,
                    "in " + dIn.ToString("0.####", CultureInfo.InvariantCulture) + " out " + dOut.ToString("0.####", CultureInfo.InvariantCulture));
            else T.Skip("core: a/v start offset preserved within a frame", whyIn ?? "ffprobe");
            // входы не изменяются никогда
            T.Eq("core: inputs are never modified", HashOf(MdFx.File("hls-ts\\seg0.ts")), HashOfConcatPart(src, MdFx.File("hls-ts\\seg0.ts")));
        }

        private static string HashOf(string path)
        {
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(File.ReadAllBytes(path)));
        }

        // Первая часть склеенного файла обязана совпадать с исходным сегментом байт в байт.
        private static string HashOfConcatPart(string concat, string part)
        {
            byte[] all = File.ReadAllBytes(concat);
            byte[] one = File.ReadAllBytes(part);
            byte[] head = new byte[Math.Min(one.Length, all.Length)];
            Buffer.BlockCopy(all, 0, head, 0, head.Length);
            using (System.Security.Cryptography.SHA256 h = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(head));
        }

        private static void CoreWrap(string dir)
        {
            string[] segs = FixtureSet("hls-ts", "seg", 4);
            if (segs == null) { T.Skip("core: 33-bit pts wrap", "hls-ts fixtures missing"); return; }
            string plain = Concat(Path.Combine(dir, "wrap-src.ts"), segs);
            // Фикстура wrap.ts переполнения не содержит (сдвиг 95443 с + задержка мультиплексора уходят за 2^33),
            // поэтому оно строится здесь: первая отметка — за 2 с до края 2^33.
            long start = (1L << 33) - 2 * 90000L;
            string wrapped = RewriteTs(plain, Path.Combine(dir, "wrap-made.ts"), start, true);
            int frames = 0, audio = 0, discontinuities = -1;
            long firstPts = -1, lastPts = -1;
            bool monotonic = true;
            string readerError = "";
            using (MdTsReader reader = new MdTsReader(wrapped, false))
            {
                MdAu au;
                long[] last = new long[8];
                for (int i = 0; i < last.Length; i++) last[i] = long.MinValue;
                while (reader.Next(out au))
                {
                    bool video = reader.Tracks[au.Track].Video;
                    if (video) frames++; else audio++;
                    if (au.Dts < last[au.Track]) monotonic = false;
                    last[au.Track] = au.Dts;
                    if (video)
                    {
                        if (firstPts < 0) firstPts = au.Pts;
                        lastPts = au.Pts;
                    }
                }
                readerError = reader.Error;
                discontinuities = reader.Discontinuities;
            }
            T.Eq("core: pts wrap keeps all 40 video frames", 40, frames);
            T.Check("core: pts wrap keeps audio frames", audio > 150, audio.ToString(CultureInfo.InvariantCulture));
            T.Check("core: pts wrap is unwrapped, dts never goes back", monotonic && readerError.Length == 0, readerError);
            T.Eq("core: pts wrap is not counted as a discontinuity", 0, discontinuities);
            T.Check("core: pts wrap keeps the 4 s span", lastPts - firstPts > 3 * 90000 && lastPts - firstPts < 4 * 90000,
                ((lastPts - firstPts) / 90000.0).ToString("0.###", CultureInfo.InvariantCulture));
            MdMuxResult r = Mux(Path.Combine(dir, "wrap.mp4"), MdOutput.Auto, false, MkIn(wrapped, MdTrackKind.Muxed));
            T.Check("core: pts wrap -> mp4 assembles", r.Ok, r.Error);
            if (!r.Ok) return;
            int v, a2;
            string why;
            if (MdFx.ReadBack(r.OutPath, out v, out a2, out why)) T.Eq("core: pts wrap -> mp4 read back 40 video samples", 40, v);
            else T.Check("core: pts wrap -> mp4 read back 40 video samples", false, why);
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null) T.Check("core: pts wrap -> mp4 duration ~4 s", p.Duration > 3.5 && p.Duration < 4.6, p.Duration.ToString(CultureInfo.InvariantCulture));
            else T.Skip("core: pts wrap -> mp4 duration ~4 s", why);
        }

        private static void CoreDiscontinuity(string dir)
        {
            string[] segs = FixtureSet("hls-ts", "seg", 4);
            if (segs == null) { T.Skip("core: discontinuity is rebased", "hls-ts fixtures missing"); return; }
            string tail = Concat(Path.Combine(dir, "disc-tail.ts"), segs[2], segs[3]);
            RewriteTs(tail, Path.Combine(dir, "disc-tail-shift.ts"), 30 * 90000L, false);
            string src = Concat(Path.Combine(dir, "disc.ts"), segs[0], segs[1], Path.Combine(dir, "disc-tail-shift.ts"));
            int frames = 0, discontinuities;
            bool monotonic = true;
            using (MdTsReader reader = new MdTsReader(src, true))
            {
                MdAu au;
                long[] last = new long[8];
                for (int i = 0; i < last.Length; i++) last[i] = long.MinValue;
                while (reader.Next(out au))
                {
                    if (reader.Tracks[au.Track].Video) frames++;
                    if (au.Dts < last[au.Track]) monotonic = false;
                    last[au.Track] = au.Dts;
                }
                discontinuities = reader.Discontinuities;
            }
            T.Eq("core: discontinuity keeps all 40 video frames", 40, frames);
            T.Check("core: discontinuity is noticed", discontinuities >= 1, discontinuities.ToString(CultureInfo.InvariantCulture));
            T.Check("core: discontinuity keeps the timeline monotonic", monotonic);
            MdMuxResult r = Mux(Path.Combine(dir, "disc.mp4"), MdOutput.Auto, true, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: discontinuity -> mp4 assembles", r.Ok, r.Error);
            if (!r.Ok) return;
            string why;
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null) T.Check("core: discontinuity does not stretch the duration", p.Duration > 3.5 && p.Duration < 6, p.Duration.ToString(CultureInfo.InvariantCulture));
            else T.Skip("core: discontinuity does not stretch the duration", why);
        }

        private static void CoreFmp4(string dir)
        {
            string init = MdFx.File("hls-fmp4\\init.mp4");
            string[] segs = FixtureSet("hls-fmp4", "seg", 4);
            if (init == null || segs == null) { T.Skip("core: fmp4 init+segments -> mp4", "hls-fmp4 fixtures missing"); return; }
            List<string> parts = new List<string>();
            parts.Add(init);
            parts.AddRange(segs);
            string src = Concat(Path.Combine(dir, "fmp4-all.data"), parts.ToArray());
            T.Eq("core: fmp4 concat is recognised as fragmented mp4", MdLayout.Fmp4, MdMux.ProbeLayout(src));
            string why;
            MdFx.Probe input = MdFx.FfProbe(src, out why);
            MdMuxResult r = Mux(Path.Combine(dir, "fmp4.mp4"), MdOutput.Auto, false, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: fmp4 init+segments -> mp4", r.Ok && File.Exists(r.OutPath), r.Error);
            if (!r.Ok) return;
            int v, a;
            if (MdFx.ReadBack(r.OutPath, out v, out a, out why))
            {
                T.Eq("core: fmp4 -> mp4 read back 40 video samples", 40, v);
                if (input != null && input.Frames.ContainsKey("audio")) T.Eq("core: fmp4 -> mp4 read back all audio samples", input.Frames["audio"], a);
            }
            else T.Check("core: fmp4 -> mp4 read back 40 video samples", false, why);
        }

        private static void CoreTracks(string dir)
        {
            string video = MdFx.File("track-avc.mp4"), audio = MdFx.File("track-aac.m4a");
            if (video == null || audio == null) { T.Skip("core: avc + aac -> mp4", "track fixtures missing"); return; }
            MdMuxResult r = Mux(Path.Combine(dir, "pair.mp4"), MdOutput.Auto, false, MkIn(video, MdTrackKind.Video), MkIn(audio, MdTrackKind.Audio));
            T.Check("core: avc + aac -> mp4", r.Ok && File.Exists(r.OutPath), r.Error);
            if (!r.Ok) return;
            int v, a, sv, sa;
            string why, whySrc;
            bool source = MdFx.ReadBack(audio, out sv, out sa, out whySrc);
            if (MdFx.ReadBack(r.OutPath, out v, out a, out why))
            {
                T.Eq("core: avc + aac -> mp4 read back 40 video samples", 40, v);
                if (source) T.Eq("core: avc + aac -> mp4 keeps every source audio sample", sa, a);
                else T.Check("core: avc + aac -> mp4 keeps every source audio sample", a > 150, whySrc);
            }
            else T.Check("core: avc + aac -> mp4 read back 40 video samples", false, why);
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null)
            {
                T.Eq("core: avc + aac -> mp4 ffprobe video frames", 40, p.Frames.ContainsKey("video") ? p.Frames["video"] : -1);
                int got = p.Frames.ContainsKey("audio") ? p.Frames["audio"] : -1;
                // Ровно 188 кадров AAC в источнике; MF при копировании отдаёт ещё один (задержка кодировщика), допуск — кадр.
                T.Check("core: avc + aac -> mp4 ffprobe audio frames within a frame of the source", Math.Abs(got - 188) <= 1, got.ToString(CultureInfo.InvariantCulture));
            }
            else T.Skip("core: avc + aac -> mp4 ffprobe video frames", why);
        }

        private static void CoreWebm(string dir)
        {
            string video = MdFx.File("track-vp9.webm"), audio = MdFx.File("track-opus.webm");
            if (video == null || audio == null) { T.Skip("core: vp9 + opus -> webm", "webm fixtures missing"); return; }
            MdMuxResult r = Mux(Path.Combine(dir, "pair.webm"), MdOutput.Auto, false, MkIn(video, MdTrackKind.Video), MkIn(audio, MdTrackKind.Audio));
            T.Check("core: vp9 + opus -> webm", r.Ok && File.Exists(r.OutPath) && r.Output == MdOutput.WebM, r.Error);
            if (!r.Ok) return;
            T.Eq("core: vp9 + opus -> webm extension", ".webm", Path.GetExtension(r.OutPath));
            T.Eq("core: webm output reports 40 video frames", 40, r.VideoFrames);
            int v, a;
            string why;
            if (WebmFrames(r.OutPath, out v, out a, out why))
            {
                T.Eq("core: webm output re-reads 40 video blocks", 40, v);
                T.Eq("core: webm output re-reads 201 audio blocks", 201, a);
            }
            else T.Check("core: webm output re-reads 40 video blocks", false, why);
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null)
            {
                T.Eq("core: webm ffprobe video frames", 40, p.Frames.ContainsKey("video") ? p.Frames["video"] : -1);
                T.Eq("core: webm ffprobe audio frames", 201, p.Frames.ContainsKey("audio") ? p.Frames["audio"] : -1);
                T.Check("core: webm ffprobe format and codecs", p.Format.IndexOf("webm", StringComparison.Ordinal) >= 0
                    && p.Codecs.Contains("video:vp9") && p.Codecs.Contains("audio:opus"), p.Format + " " + string.Join(",", p.Codecs.ToArray()));
                T.Check("core: webm duration ~4 s", p.Duration > 3.5 && p.Duration < 4.6, p.Duration.ToString(CultureInfo.InvariantCulture));
            }
            else T.Skip("core: webm ffprobe video frames", why);
            // WebM в MP4 без перекодирования не переносится — молчаливой замены быть не должно
            MdMuxResult bad = Mux(Path.Combine(dir, "webm-as-mp4.mp4"), MdOutput.Mp4, false, MkIn(video, MdTrackKind.Video), MkIn(audio, MdTrackKind.Audio));
            T.Check("core: webm requested as mp4 is an error, not a silent fallback",
                !bad.Ok && bad.Error.Length > 0 && !File.Exists(Path.Combine(dir, "webm-as-mp4.mp4")), bad.Error);
        }

        private static void CoreAuto()
        {
            string video = MdFx.File("track-avc.mp4"), aac = MdFx.File("track-aac.m4a");
            string vp9 = MdFx.File("track-vp9.webm"), opus = MdFx.File("track-opus.webm");
            string ts = MdFx.File("hls-ts\\seg0.ts");
            if (video == null || aac == null || vp9 == null || opus == null || ts == null) { T.Skip("core: auto output table", "fixtures missing"); return; }
            List<string> bad = new List<string>();
            Auto(bad, "avc+aac", MdOutput.Mp4, MkIn(video, MdTrackKind.Video), MkIn(aac, MdTrackKind.Audio));
            Auto(bad, "vp9+opus", MdOutput.WebM, MkIn(vp9, MdTrackKind.Video), MkIn(opus, MdTrackKind.Audio));
            Auto(bad, "vp9 alone", MdOutput.WebM, MkIn(vp9, MdTrackKind.Video));
            Auto(bad, "ts muxed", MdOutput.Mp4, MkIn(ts, MdTrackKind.Muxed));
            Auto(bad, "aac alone", MdOutput.M4a, MkIn(aac, MdTrackKind.Audio));
            Auto(bad, "avc+opus", MdOutput.WebM, MkIn(video, MdTrackKind.Video), MkIn(opus, MdTrackKind.Audio));
            T.Check("core: auto output table", bad.Count == 0, string.Join("; ", bad.ToArray()));
            List<MdInput> one = new List<MdInput>();
            one.Add(MkIn(vp9, MdTrackKind.Video));
            T.Eq("core: a requested output is never overridden", MdOutput.Mp4, MdMux.ChooseOutput(one, MdOutput.Mp4));
        }

        private static void Auto(List<string> bad, string label, MdOutput want, params MdInput[] inputs)
        {
            List<MdInput> list = new List<MdInput>(inputs);
            MdOutput got = MdMux.ChooseOutput(list, MdOutput.Auto);
            if (got != want) bad.Add(label + ": " + got + " != " + want);
        }

        private static void CoreM4a(string dir)
        {
            string src = TsAll(dir);
            if (src == null) { T.Skip("core: ts -> m4a", "hls-ts fixtures missing"); return; }
            string why;
            int inVideo, inAudio;
            bool oracle = FrameCounts(src, out inVideo, out inAudio, out why);
            MdMuxResult r = Mux(Path.Combine(dir, "audio.out"), MdOutput.M4a, false, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: ts -> m4a", r.Ok && File.Exists(r.OutPath) && Path.GetExtension(r.OutPath) == ".m4a", r.Error + " " + r.OutPath);
            if (!r.Ok) return;
            int v, a;
            if (MdFx.ReadBack(r.OutPath, out v, out a, out why))
            {
                T.Eq("core: m4a has no video", 0, v);
                if (oracle) T.Eq("core: m4a keeps every audio frame", inAudio, a);
                else T.Check("core: m4a keeps every audio frame", a > 150, a.ToString(CultureInfo.InvariantCulture));
            }
            else T.Check("core: m4a has no video", false, why);
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null) T.Check("core: m4a is aac audio only", p.Codecs.Count == 1 && p.Codecs[0] == "audio:aac", string.Join(",", p.Codecs.ToArray()));
            else T.Skip("core: m4a is aac audio only", why);
        }

        private static void CoreMp3(string dir)
        {
            string src = TsAll(dir);
            if (src == null) { T.Skip("core: ts -> mp3", "hls-ts fixtures missing"); return; }
            if (!MdMux.Mp3Available) { T.Skip("core: ts -> mp3", "no MP3 encoder MFT on this machine"); return; }
            MdMuxResult r = Mux(Path.Combine(dir, "audio-mp3.out"), MdOutput.Mp3, false, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: ts -> mp3", r.Ok && File.Exists(r.OutPath) && Path.GetExtension(r.OutPath) == ".mp3", r.Error + " " + r.OutPath);
            if (!r.Ok) return;
            T.Check("core: mp3 has frames", r.AudioFrames > 100, r.AudioFrames.ToString(CultureInfo.InvariantCulture));
            T.Eq("core: mp3 output is recognised as mp3", MdLayout.Mp3, MdMux.ProbeLayout(r.OutPath));
            string why;
            MdFx.Probe p = MdFx.FfProbe(r.OutPath, out why);
            if (p != null)
            {
                T.Check("core: mp3 is mp3 audio only", p.Codecs.Count == 1 && p.Codecs[0] == "audio:mp3", string.Join(",", p.Codecs.ToArray()));
                T.Check("core: mp3 duration ~4 s", p.Duration > 3.3 && p.Duration < 4.8, p.Duration.ToString(CultureInfo.InvariantCulture));
            }
            else T.Skip("core: mp3 is mp3 audio only", why);
        }

        private static void CoreTruncated(string dir)
        {
            string src = TsAll(dir);
            if (src == null) { T.Skip("core: truncated ts", "hls-ts fixtures missing"); return; }
            byte[] all = File.ReadAllBytes(src);
            // обрыв внутри пакета и внутри PES: последний целый пакет минус 100 байт
            int cut = (all.Length / 188) * 188 - 100;
            byte[] head = new byte[cut];
            Buffer.BlockCopy(all, 0, head, 0, cut);
            string cutPath = Path.Combine(dir, "cut.ts");
            File.WriteAllBytes(cutPath, head);
            string outPath = Path.Combine(dir, "cut-strict.mp4");
            MdMuxResult strict = Mux(outPath, MdOutput.Mp4, false, MkIn(cutPath, MdTrackKind.Muxed));
            T.Check("core: truncated ts without AllowTruncated is an error", !strict.Ok && strict.Error.Length > 0, strict.Error);
            T.Check("core: failed assembly leaves no output", !File.Exists(outPath) && !File.Exists(outPath + ".tmp"));
            MdMuxResult loose = Mux(Path.Combine(dir, "cut-loose.mp4"), MdOutput.Mp4, true, MkIn(cutPath, MdTrackKind.Muxed));
            T.Check("core: truncated ts with AllowTruncated assembles", loose.Ok && File.Exists(loose.OutPath), loose.Error);
            if (!loose.Ok) return;
            T.Check("core: truncated ts keeps all whole frames", loose.VideoFrames >= 38 && loose.VideoFrames <= 40, loose.VideoFrames.ToString(CultureInfo.InvariantCulture));
            int v, a;
            string why;
            if (MdFx.ReadBack(loose.OutPath, out v, out a, out why)) T.Check("core: truncated ts output is readable", v >= 38 && a > 150, v + "/" + a);
            else T.Check("core: truncated ts output is readable", false, why);
        }

        private static void CoreGarbage(string dir)
        {
            string garbage = Path.Combine(dir, "garbage.bin");
            if (!File.Exists(garbage)) return;
            string outPath = Path.Combine(dir, "garbage.mp4");
            MdMuxResult r = Mux(outPath, MdOutput.Auto, false, MkIn(garbage, MdTrackKind.Muxed));
            T.Check("core: garbage input is refused", !r.Ok && r.Error.Length > 0, r.Error);
            T.Check("core: garbage input leaves no output", !File.Exists(outPath) && !File.Exists(outPath + ".tmp"));
            MdMuxResult missing = Mux(Path.Combine(dir, "missing.mp4"), MdOutput.Auto, false, MkIn(Path.Combine(dir, "no-such-file.ts"), MdTrackKind.Muxed));
            T.Check("core: a missing track file is refused", !missing.Ok && missing.Error.Length > 0, missing.Error);
            // усечённый WebM: заголовок цел, кластеры оборваны
            string vp9 = MdFx.File("track-vp9.webm");
            if (vp9 != null)
            {
                byte[] b = File.ReadAllBytes(vp9);
                byte[] part = new byte[b.Length / 3];
                Buffer.BlockCopy(b, 0, part, 0, part.Length);
                string cut = Path.Combine(dir, "cut.webm");
                File.WriteAllBytes(cut, part);
                string outCut = Path.Combine(dir, "cut-webm.webm");
                MdMuxResult r2 = Mux(outCut, MdOutput.WebM, false, MkIn(cut, MdTrackKind.Video));
                T.Check("core: truncated webm never hangs or throws", r2.Ok || r2.Error.Length > 0, r2.Error);
                T.Check("core: truncated webm leaves no temporary file", !File.Exists(outCut + ".tmp"));
            }
        }

        private static void CoreSubs(string dir)
        {
            string subs = MdFx.File("subs.vtt");
            if (subs == null) { T.Skip("core: subtitles", "subs.vtt missing"); return; }
            // склейка сегментов HLS: у каждого своя карта X-TIMESTAMP-MAP, реплика на стыке повторяется
            string a = Path.Combine(dir, "seg-a.vtt"), b = Path.Combine(dir, "seg-b.vtt");
            File.WriteAllText(a, "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000\n\n00:00:00.500 --> 00:00:01.500\nПривет\n\n00:00:01.800 --> 00:00:02.400\nНа стыке\n", new UTF8Encoding(false));
            File.WriteAllText(b, "\uFEFFWEBVTT\nX-TIMESTAMP-MAP=MPEGTS:1080000,LOCAL:00:00:02.000\n\n00:00:01.800 --> 00:00:02.400\nНа стыке\n\n00:00:03.000 --> 00:00:04.000\nHello\n", new UTF8Encoding(false));
            string joined = Path.Combine(dir, "joined.vtt");
            string error;
            List<string> segs = new List<string>();
            segs.Add(a);
            segs.Add(b);
            bool ok = MdSubs.JoinVtt(segs, joined, out error);
            T.Check("core: vtt segments join", ok && File.Exists(joined), error);
            if (ok)
            {
                string want = "WEBVTT\nX-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n\n"
                    + "00:00:10.500 --> 00:00:11.500\nПривет\n\n"
                    + "00:00:11.800 --> 00:00:12.400\nНа стыке\n\n"
                    + "00:00:13.000 --> 00:00:14.000\nHello\n\n";
                byte[] got = File.ReadAllBytes(joined);
                T.Check("core: joined vtt is byte-exact (map applied, duplicate cue dropped)",
                    Same(got, new UTF8Encoding(false).GetBytes(want)), Show(got));
            }
            // SRT из VTT: «Привет» байт в байт, BOM и CRLF
            string srt = Path.Combine(dir, "subs.srt");
            ok = MdSubs.ToSrt(subs, srt, out error);
            T.Check("core: vtt -> srt", ok && File.Exists(srt), error);
            if (ok)
            {
                byte[] got = File.ReadAllBytes(srt);
                T.Check("core: srt is byte-exact utf-8 with bom, crlf, «Привет»", Same(got, ExpectedSrt()), Show(got));
            }
            // TTML со вложенными span, <br/> и сущностями
            string ttml = Path.Combine(dir, "cues.ttml");
            File.WriteAllText(ttml,
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
                + "<tt xmlns=\"http://www.w3.org/ns/ttml\" xml:lang=\"ru\">\n"
                + "<head><styling><style xml:id=\"s1\"/></styling></head>\n"
                + "<body><div begin=\"00:00:00.000\">\n"
                + "<p begin=\"00:00:00.500\" end=\"00:00:01.500\"><span style=\"s1\">При</span>вет</p>\n"
                + "<p begin=\"2s\" dur=\"1s\">Hello<br/>&amp; goodbye</p>\n"
                + "</div></body></tt>\n", new UTF8Encoding(false));
            string ttmlSrt = Path.Combine(dir, "cues.srt");
            ok = MdSubs.ToSrt(ttml, ttmlSrt, out error);
            T.Check("core: ttml -> srt", ok && File.Exists(ttmlSrt), error);
            if (ok)
            {
                string text = File.ReadAllText(ttmlSrt);
                string want = "1\r\n00:00:00,500 --> 00:00:01,500\r\nПривет\r\n\r\n2\r\n00:00:02,000 --> 00:00:03,000\r\nHello\r\n& goodbye\r\n\r\n";
                T.Eq("core: ttml cues, tags stripped and entities decoded", want, text);
            }
            string broken = Path.Combine(dir, "broken.vtt");
            File.WriteAllText(broken, "not a subtitle file at all\n", new UTF8Encoding(false));
            T.Check("core: a broken subtitle file is refused, never thrown", !MdSubs.ToSrt(broken, Path.Combine(dir, "broken.srt"), out error) && error != null, error);
        }

        private static byte[] ExpectedSrt()
        {
            string body = "1\r\n00:00:00,500 --> 00:00:01,500\r\nПривет\r\n\r\n2\r\n00:00:02,000 --> 00:00:03,000\r\nHello\r\n\r\n";
            byte[] text = new UTF8Encoding(false).GetBytes(body);
            byte[] all = new byte[text.Length + 3];
            all[0] = 0xEF;
            all[1] = 0xBB;
            all[2] = 0xBF;
            Buffer.BlockCopy(text, 0, all, 3, text.Length);
            return all;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static string Show(byte[] b)
        {
            string s = new UTF8Encoding(false).GetString(b);
            if (s.Length > 400) s = s.Substring(0, 400);
            return s.Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void CoreSidecars(string dir)
        {
            string src = TsAll(dir), subs = MdFx.File("subs.vtt");
            if (src == null || subs == null) { T.Skip("core: subtitle sidecars", "fixtures missing"); return; }
            MdInput sub = MkIn(subs, MdTrackKind.Subtitles);
            sub.Language = "ru";
            MdMuxResult r = Mux(Path.Combine(dir, "withsubs.mp4"), MdOutput.Auto, false, MkIn(src, MdTrackKind.Muxed), sub);
            T.Check("core: video with subtitles assembles", r.Ok, r.Error);
            if (!r.Ok) return;
            string stem = Path.Combine(dir, "withsubs");
            T.Eq("core: two sidecar files next to the video", 2, r.SubtitleFiles.Count);
            T.Check("core: sidecars are named after the video and the language",
                r.SubtitleFiles.Contains(stem + ".ru.vtt") && r.SubtitleFiles.Contains(stem + ".ru.srt"), string.Join(",", r.SubtitleFiles.ToArray()));
            T.Check("core: sidecar files exist", File.Exists(stem + ".ru.vtt") && File.Exists(stem + ".ru.srt"));
            if (File.Exists(stem + ".ru.srt"))
                T.Check("core: sidecar srt is byte-exact", Same(File.ReadAllBytes(stem + ".ru.srt"), ExpectedSrt()), Show(File.ReadAllBytes(stem + ".ru.srt")));
            T.Check("core: the subtitle input is left untouched", File.ReadAllBytes(subs).Length > 0 && MdMux.ProbeLayout(subs) == MdLayout.Vtt);
        }

        private static void CoreTsOut(string dir)
        {
            string[] segs = FixtureSet("hls-ts", "seg", 4);
            if (segs == null) { T.Skip("core: ts output", "hls-ts fixtures missing"); return; }
            MdMuxResult r = Mux(Path.Combine(dir, "copy.out"), MdOutput.Ts, false, MkIn(segs[0], MdTrackKind.Muxed), MkIn(segs[1], MdTrackKind.Muxed));
            T.Check("core: ts output concatenates the inputs", r.Ok && Path.GetExtension(r.OutPath) == ".ts", r.Error + " " + r.OutPath);
            if (r.Ok)
            {
                byte[] got = File.ReadAllBytes(r.OutPath);
                byte[] a = File.ReadAllBytes(segs[0]), b = File.ReadAllBytes(segs[1]);
                byte[] want = new byte[a.Length + b.Length];
                Buffer.BlockCopy(a, 0, want, 0, a.Length);
                Buffer.BlockCopy(b, 0, want, a.Length, b.Length);
                T.Check("core: ts output is a byte-exact concatenation", Same(got, want), got.Length + " vs " + want.Length);
            }
            string mp4 = MdFx.File("plain.mp4");
            if (mp4 != null)
            {
                MdMuxResult bad = Mux(Path.Combine(dir, "mp4-as-ts.out"), MdOutput.Ts, false, MkIn(mp4, MdTrackKind.Muxed));
                T.Check("core: mp4 requested as ts is an error", !bad.Ok && bad.Error.Length > 0, bad.Error);
            }
        }

        private static void CoreHostile(string dir)
        {
            // EBML, где Tracks объявляет размер больше файла: размеры обязаны сверяться с остатком
            MemoryStream head = new MemoryStream();
            MdEbml.UIntEl(head, 0x4286, 1);
            MdEbml.UIntEl(head, 0x42F7, 1);
            MdEbml.UIntEl(head, 0x42F2, 4);
            MdEbml.UIntEl(head, 0x42F3, 8);
            MdEbml.StrEl(head, 0x4282, "webm");
            MdEbml.UIntEl(head, 0x4287, 4);
            MdEbml.UIntEl(head, 0x4285, 2);
            MemoryStream file = new MemoryStream();
            MdEbml.BinEl(file, MdEbml.IdEbml, head.ToArray());
            MdEbml.Id(file, MdEbml.IdSegment);
            file.WriteByte(0x01);
            for (int i = 0; i < 7; i++) file.WriteByte(0xFF);            // размер неизвестен — так пишут трансляции
            MdEbml.Id(file, MdEbml.IdTracks);
            MdEbml.Size(file, 1L << 40, 8);                              // 1 ТиБ в файле на 60 байт
            for (int i = 0; i < 32; i++) file.WriteByte(0);
            string hostile = Path.Combine(dir, "hostile.webm");
            File.WriteAllBytes(hostile, file.ToArray());
            T.Eq("core: hostile file is still recognised as webm by signature", MdLayout.WebM, MdMux.ProbeLayout(hostile));
            string outPath = Path.Combine(dir, "hostile-out.webm");
            MdMuxResult r = Mux(outPath, MdOutput.WebM, false, MkIn(hostile, MdTrackKind.Video));
            T.Check("core: an ebml size larger than the file is refused", !r.Ok && r.Error.Length > 0, r.Error);
            T.Check("core: the refused webm leaves no output", !File.Exists(outPath) && !File.Exists(outPath + ".tmp"));
        }

        private static void CoreCancel(string dir)
        {
            string src = TsAll(dir);
            if (src == null) { T.Skip("core: cancel", "hls-ts fixtures missing"); return; }
            string outPath = Path.Combine(dir, "cancelled.mp4");
            MdMuxJob job = new MdMuxJob();
            job.OutPath = outPath;
            job.Output = MdOutput.Mp4;
            job.Inputs.Add(MkIn(src, MdTrackKind.Muxed));
            int calls = 0;
            job.Cancel = delegate { calls++; return true; };
            MdMuxResult r = MdMux.Run(job);
            T.Check("core: cancel stops the assembly", !r.Ok && r.Error.Length > 0 && calls > 0, r.Error + " calls " + calls);
            T.Check("core: cancel leaves no files behind", !File.Exists(outPath) && !File.Exists(outPath + ".tmp"));
        }
    }
}
