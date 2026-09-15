// SysDeck — область «media»: видео-загрузки этапа 6 (HLS, DASH, склейка, yt-dlp, расширение).
//
// Части подключают свои проверки partial-методами: файл части есть в сборке — его тесты идут, нет — вызов исчезает при
// компиляции. Потоки для проверок — настоящие файлы, заранее сделанные ffmpeg (tests\media\make-fixtures.bat); сам ffmpeg
// тесты не запускают. Независимая сверка результата — ffprobe из SYSDECK_FFPROBE (нет переменной — SKIP), обязательная —
// чтение итогового файла Media Foundation (MdFx.ReadBack). Сеть — только петля 127.0.0.1 (DlTestServer).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using SysDeck.Capture;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        internal static void Run()
        {
            ContractCases();
            RunCore();
            RunEngine();
            RunTools();
            RunIntegration();
        }

        static partial void RunCore();         // Tests.MediaCore.cs — MPEG-TS, склейка MP4/WebM, звук, субтитры
        static partial void RunEngine();       // Tests.MediaEngine.cs — HLS, DASH, сегменты, трансляции, продолжение
        static partial void RunTools();        // Tests.MediaTools.cs — yt-dlp и Deno: установка, извлечение, обновление
        static partial void RunIntegration();  // Tests.MediaIntegration.cs — собранная цепочка: движок + ядро + окно

        // ---------- основа: запись DlItem с видео переживает сохранение, секреты не пишутся ----------
        private static void ContractCases()
        {
            DlItem it = new DlItem();
            it.Id = "m1";
            it.Kind = DlItem.KindMedia;
            it.Url = "http://127.0.0.1/x/index.m3u8";
            it.Media = new DlMedia();
            it.Media.Source = MdSource.Hls;
            it.Media.VariantId = "v1";
            it.Media.SubtitleIds.Add("s-ru");
            it.Media.Output = MdOutput.Mp4;
            it.Media.Live = true;
            it.Media.SegmentsTotal = 12;
            it.Media.SegmentsDone = 5;
            it.Media.SecondsDone = 10.5;
            it.Media.Headers["Referer"] = "http://127.0.0.1/page";
            it.Media.Headers["Cookie"] = "sid=secret";
            it.Media.Headers["Authorization"] = "Bearer secret";
            string text = Jsn.Write(it.ToJson(true));
            T.Check("media: json of a media item never contains cookies or authorization", text.IndexOf("secret", StringComparison.Ordinal) < 0, text);
            DlItem back = DlItem.FromJson(Jsn.Parse(text));
            T.Check("media: item round-trips as media", back != null && back.IsMedia && back.Media != null);
            if (back != null && back.Media != null)
            {
                T.Eq("media: variant id round-trips", "v1", back.Media.VariantId);
                T.Eq("media: subtitle ids round-trip", "s-ru", back.Media.SubtitleIds.Count == 1 ? back.Media.SubtitleIds[0] : "");
                T.Eq("media: output round-trips", MdOutput.Mp4, back.Media.Output);
                T.Eq("media: segment counters round-trip", "12/5/10.5", back.Media.SegmentsTotal + "/" + back.Media.SegmentsDone + "/" + back.Media.SecondsDone.ToString(CultureInfo.InvariantCulture));
                T.Check("media: safe header kept, sensitive dropped", back.Media.Headers.ContainsKey("Referer") && !back.Media.Headers.ContainsKey("Cookie") && !back.Media.Headers.ContainsKey("Authorization"));
            }
            DlItem http = DlItem.FromJson(Jsn.Parse("{\"Id\":\"h1\",\"Url\":\"http://127.0.0.1/a\",\"Kind\":\"weird\"}"));
            T.Check("media: unknown kind falls back to http, no media state", http != null && !http.IsMedia && http.Media == null);
        }
    }

    // ------------------------------------------------------------------ //
    //  Общие помощники частей
    // ------------------------------------------------------------------ //
    internal static class MdFx
    {
        private static string _dir;

        // tests\media в репозитории: SYSDECK_REPO, иначе папка SYSDECK_TEST_APP, иначе вверх от текущей папки.
        internal static string Dir
        {
            get
            {
                if (_dir != null) return _dir;
                List<string> starts = new List<string>();
                string repo = Environment.GetEnvironmentVariable("SYSDECK_REPO");
                if (!string.IsNullOrEmpty(repo)) starts.Add(repo);
                string app = Environment.GetEnvironmentVariable("SYSDECK_TEST_APP");
                if (!string.IsNullOrEmpty(app)) { try { starts.Add(Path.GetDirectoryName(Path.GetFullPath(app))); } catch (ArgumentException) { } }
                starts.Add(Environment.CurrentDirectory);
                foreach (string s in starts)
                    for (string d = s; !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
                    {
                        string cand = Path.Combine(Path.Combine(d, "tests"), "media");
                        if (System.IO.File.Exists(Path.Combine(cand, "make-fixtures.bat"))) { _dir = cand; return _dir; }
                    }
                _dir = "";
                return _dir;
            }
        }

        // Полный путь фикстуры («hls-ts\index.m3u8»); null — фикстур нет (тогда проверка — SKIP с причиной).
        internal static string File(string relative)
        {
            if (Dir.Length == 0) return null;
            string p = Path.Combine(Dir, relative);
            return System.IO.File.Exists(p) ? p : null;
        }

        internal static byte[] Bytes(string relative)
        {
            string p = File(relative);
            return p == null ? null : System.IO.File.ReadAllBytes(p);
        }

        // Раздать папку фикстуры с тестового сервера: ключ «<prefix>/<относительный путь с />». Возвращает URL файла entry.
        internal static string Serve(DlTestServer server, string fixtureDir, string prefix, string entry)
        {
            string root = Path.Combine(Dir, fixtureDir);
            foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(root.Length).TrimStart('\\').Replace('\\', '/');
                DlTestServer.Res r = new DlTestServer.Res();
                r.Body = System.IO.File.ReadAllBytes(f);
                r.Size = r.Body.Length;
                r.ContentType = ContentTypeOf(rel);
                server.Add(prefix + "/" + rel, r);
            }
            return server.Url(prefix + "/" + entry);
        }

        internal static string ContentTypeOf(string name)
        {
            string ext = Path.GetExtension(name).ToLowerInvariant();
            switch (ext)
            {
                case ".m3u8": return "application/vnd.apple.mpegurl";
                case ".mpd": return "application/dash+xml";
                case ".ts": return "video/mp2t";
                case ".m4s": return "video/iso.segment";
                case ".mp4": return "video/mp4";
                case ".m4a": return "audio/mp4";
                case ".webm": return "video/webm";
                case ".vtt": return "text/vtt";
                default: return "application/octet-stream";
            }
        }

        // Независимая сверка: ffprobe -count_frames. null — SYSDECK_FFPROBE не задан (why заполнен) или ffprobe не отработал.
        internal sealed class Probe
        {
            public string Format = "";
            public double Duration;
            public readonly List<string> Codecs = new List<string>();      // «video:h264», «audio:aac»
            public readonly Dictionary<string, int> Frames = new Dictionary<string, int>();   // по типу: video/audio/subtitle
        }

        internal static Probe FfProbe(string path, out string why)
        {
            why = null;
            string exe = Environment.GetEnvironmentVariable("SYSDECK_FFPROBE");
            if (string.IsNullOrEmpty(exe) || !System.IO.File.Exists(exe)) { why = "SYSDECK_FFPROBE not set"; return null; }
            // index обязателен: для MPEG-TS ffprobe печатает каждую дорожку дважды (внутри программы и отдельно),
            // без него кадры считались бы по два раза.
            ProcessStartInfo psi = new ProcessStartInfo(exe, "-v error -count_frames -show_entries format=format_name,duration:stream=index,codec_type,codec_name,nb_read_frames -of compact \"" + path + "\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            string output, error;
            using (Process p = Process.Start(psi))
            {
                output = p.StandardOutput.ReadToEnd();
                error = p.StandardError.ReadToEnd();
                if (!p.WaitForExit(60000)) { try { p.Kill(); } catch (InvalidOperationException) { } why = "ffprobe timeout"; return null; }
                if (p.ExitCode != 0) { why = "ffprobe exit " + p.ExitCode + ": " + error.Trim(); return null; }
            }
            Probe r = new Probe();
            HashSet<string> seenStreams = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in output.Split('\n'))
            {
                string line = raw.Trim();
                Dictionary<string, string> kv = new Dictionary<string, string>();
                foreach (string part in line.Split('|'))
                {
                    int eq = part.IndexOf('=');
                    if (eq > 0) kv[part.Substring(0, eq)] = part.Substring(eq + 1);
                }
                if (line.StartsWith("stream|", StringComparison.Ordinal))
                {
                    if (!seenStreams.Add(kv.ContainsKey("index") ? kv["index"] : Guid.NewGuid().ToString())) continue;
                    string type = kv.ContainsKey("codec_type") ? kv["codec_type"] : "";
                    r.Codecs.Add(type + ":" + (kv.ContainsKey("codec_name") ? kv["codec_name"] : ""));
                    int n;
                    if (kv.ContainsKey("nb_read_frames") && int.TryParse(kv["nb_read_frames"], out n))
                    {
                        int prev;
                        r.Frames.TryGetValue(type, out prev);
                        r.Frames[type] = prev + n;
                    }
                }
                else if (line.StartsWith("format|", StringComparison.Ordinal))
                {
                    r.Format = kv.ContainsKey("format_name") ? kv["format_name"] : "";
                    double d;
                    if (kv.ContainsKey("duration") && double.TryParse(kv["duration"], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) r.Duration = d;
                }
            }
            return r;
        }

        // Обязательная сверка без внешних программ: Media Foundation читает итог и считает сэмплы по типам потоков.
        // Для WebM (MF его на чистой Windows может не читать) — false с причиной, проверка части тогда ffprobe или своя.
        internal static bool ReadBack(string path, out int videoSamples, out int audioSamples, out string why)
        {
            videoSamples = 0;
            audioSamples = 0;
            why = null;
            IntPtr reader = IntPtr.Zero;
            try
            {
                VidCom.Check(VidNative.MFStartup(0x20070, 0), "MFStartup");
                int hr = VidNative.MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader);
                if (hr < 0) { why = "MFCreateSourceReaderFromURL 0x" + hr.ToString("X8"); return false; }
                List<bool> isVideo = new List<bool>();
                for (uint i = 0; i < 16; i++)
                {
                    IntPtr type;
                    if (VidCom.Fn<VidCom.DUIntUIntOutPtr>(reader, 5)(reader, i, 0, out type) < 0) break;
                    Guid key = MfA.MajorType, major;
                    VidCom.Fn<VidCom.DGetGuid>(type, 10)(type, ref key, out major);
                    VidCom.Rel(ref type);
                    isVideo.Add(major == MfA.MediaVideo);
                    VidCom.Fn<VidCom.DUIntInt>(reader, 4)(reader, i, 1);
                }
                if (isVideo.Count == 0) { why = "no streams"; return false; }
                int ended = 0;
                bool[] done = new bool[isVideo.Count];
                VidCom.DReadSample read = VidCom.Fn<VidCom.DReadSample>(reader, 9);
                while (ended < isVideo.Count)
                {
                    uint actual, flags;
                    long ts;
                    IntPtr sample;
                    hr = read(reader, 0xFFFFFFFE, 0, out actual, out flags, out ts, out sample);
                    if (hr < 0) { why = "ReadSample 0x" + hr.ToString("X8"); VidCom.Rel(ref sample); return false; }
                    if (sample != IntPtr.Zero)
                    {
                        if (actual < isVideo.Count) { if (isVideo[(int)actual]) videoSamples++; else audioSamples++; }
                        VidCom.Rel(ref sample);
                    }
                    if ((flags & 1) != 0) break;
                    if ((flags & 2) != 0 && actual < isVideo.Count && !done[actual]) { done[actual] = true; ended++; }
                }
                return true;
            }
            catch (Exception ex) { why = ex.Message; return false; }
            finally { VidCom.Rel(ref reader); }
        }
    }
}
