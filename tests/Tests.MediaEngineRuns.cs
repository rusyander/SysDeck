// SysDeck — тесты медиадвижка: прогоны HLS/DASH, субтитры, DRM, возобновление, эфир.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        // ---------- один файл, сегменты диапазонами ----------
        private static void ByteRangeRun(DlTestServer srv)
        {
            string url = MdFx.Serve(srv, "hls-byterange", "br", "index.m3u8");
            byte[] whole = FixBytes("hls-byterange\\index.ts");
            string folder = MdDir("br");
            using (DlEngine e = StartEngine(MdDir("br-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, url, new DlMedia(), folder, "b.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: byterange segments of one file are downloaded by ranges", done, MdInfo(e, id));
                T.Eq("engine: the ranges rebuild the whole file", Sha(whole), ShaFile(e.Find(id).TargetPath));
            }
        }

        // ---------- master: выбор варианта, звук отдельной дорожкой, субтитры рядом ----------
        private static void MasterAndSubtitles(DlTestServer srv)
        {
            string masterUrl = MdFx.Serve(srv, "hls-master", "m", "master.m3u8");
            MdFx.Serve(srv, "hls-subs", "subs", "subs.m3u8");
            string err;
            MdManifest parsed = MdHls.ParseMaster(FixText("hls-master\\master.m3u8"), masterUrl, out err);
            string best = parsed.Variants.Count > 0 ? parsed.Variants[0].Id : "";
            byte[] video = Cat(FixBytes("hls-master\\v1_seg0.ts"), FixBytes("hls-master\\v1_seg1.ts"), FixBytes("hls-master\\v1_seg2.ts"), FixBytes("hls-master\\v1_seg3.ts"));
            byte[] audio = Cat(FixBytes("hls-master\\vEnglish_seg0.ts"), FixBytes("hls-master\\vEnglish_seg1.ts"), FixBytes("hls-master\\vEnglish_seg2.ts"),
                               FixBytes("hls-master\\vEnglish_seg3.ts"), FixBytes("hls-master\\vEnglish_seg4.ts"));
            string folder = MdDir("master");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Hls;
            md.VariantId = best;
            using (DlEngine e = StartEngine(MdDir("master-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, masterUrl, md, folder, "m.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: the chosen variant brings its audio group along", done, MdInfo(e, id));
                string inputs;
                lock (MdGate) inputs = string.Join(" | ", _muxInputs.ToArray());
                T.Check("engine: assembly gets a video track and an audio track", inputs.Contains("Video:Ts:") && inputs.Contains("Audio:Ts:"), inputs);
                T.Eq("engine: both tracks are complete", Sha(Cat(video, audio)), ShaFile(e.Find(id).TargetPath));
                T.Eq("engine: all segments of both tracks are counted", 9, e.Find(id).Media.SegmentsDone);
            }

            // Запасной вариант: выбранного качества больше нет — движок берёт не выше по высоте.
            string missingFolder = MdDir("fallback");
            DlMedia gone = new DlMedia();
            gone.Source = MdSource.Hls;
            gone.VariantId = "v2160p-99999k";
            using (DlEngine e = StartEngine(MdDir("fallback-store"), MdSettings(missingFolder)))
            {
                string id = AddMedia(e, masterUrl, gone, missingFolder, "fb.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: a missing variant falls back and is journaled", done && EventsHave(e.Find(id), "недоступен"), MdInfo(e, id));
            }

            // Субтитры: master, собранный тестом, ссылается на фикстуру субтитров.
            string subMaster = "#EXTM3U\n#EXT-X-VERSION:3\n"
                             + "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"sub\",NAME=\"ru\",DEFAULT=YES,LANGUAGE=\"ru\",URI=\"../subs/subs.m3u8\"\n"
                             + "#EXT-X-STREAM-INF:BANDWIDTH=84224,RESOLUTION=160x90,CODECS=\"avc1.640009\",SUBTITLES=\"sub\"\nv0.m3u8\n";
            Txt(srv, "m/master-subs.m3u8", subMaster, "application/vnd.apple.mpegurl");
            MdManifest subParsed = MdHls.ParseMaster(subMaster, srv.Url("m/master-subs.m3u8"), out err);
            string subId = subParsed.Subtitles.Count > 0 ? subParsed.Subtitles[0].Id : "";
            byte[] v0 = Cat(FixBytes("hls-master\\v0_seg0.ts"), FixBytes("hls-master\\v0_seg1.ts"), FixBytes("hls-master\\v0_seg2.ts"), FixBytes("hls-master\\v0_seg3.ts"));
            string subFolder = MdDir("subs");
            DlMedia smd = new DlMedia();
            smd.Source = MdSource.Hls;
            smd.SubtitleIds.Add(subId);
            using (DlEngine e = StartEngine(MdDir("subs-store"), MdSettings(subFolder)))
            {
                string id = AddMedia(e, srv.Url("m/master-subs.m3u8"), smd, subFolder, "s.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                string side = Path.Combine(subFolder, "s.ru.vtt");
                T.Check("engine: a subtitle track lands next to the video", done && File.Exists(side), MdInfo(e, id) + " id=" + subId);
                T.Eq("engine: the subtitle file is the served VTT", Sha(FixBytes("hls-subs\\sub0.vtt")), ShaFile(side));
                T.Eq("engine: the video itself is untouched by the subtitles", Sha(v0), ShaFile(e.Find(id).TargetPath));
            }
        }

        // ---------- DASH ----------
        private static void DashRuns(DlTestServer srv)
        {
            string url = MdFx.Serve(srv, "dash-tl", "dtl", "manifest.mpd");
            byte[] video = Cat(FixBytes("dash-tl\\init0.m4s"), FixBytes("dash-tl\\seg0-1.m4s"), FixBytes("dash-tl\\seg0-2.m4s"),
                               FixBytes("dash-tl\\seg0-3.m4s"), FixBytes("dash-tl\\seg0-4.m4s"));
            byte[] audio = Cat(FixBytes("dash-tl\\init1.m4s"), FixBytes("dash-tl\\seg1-1.m4s"), FixBytes("dash-tl\\seg1-2.m4s"),
                               FixBytes("dash-tl\\seg1-3.m4s"), FixBytes("dash-tl\\seg1-4.m4s"), FixBytes("dash-tl\\seg1-5.m4s"));
            string folder = MdDir("dtl");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Dash;
            md.ManifestUrl = url;
            using (DlEngine e = StartEngine(MdDir("dtl-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, url, md, folder, "d.mp4");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("dash: a timeline manifest downloads both representations", done, MdInfo(e, id));
                T.Eq("dash: each track is its init plus its segments", Sha(Cat(video, audio)), ShaFile(e.Find(id).TargetPath));
                T.Eq("dash: nine segments in total", 9, e.Find(id).Media.SegmentsDone);
            }

            string one = MdFx.Serve(srv, "dash-single", "dsg", "manifest.mpd");
            byte[] rep0 = FixBytes("dash-single\\rep0.mp4");
            string oneFolder = MdDir("dsg");
            DlMedia omd = new DlMedia();
            omd.Source = MdSource.Dash;
            omd.Output = MdOutput.Mp4;
            using (DlEngine e = StartEngine(MdDir("dsg-store"), MdSettings(oneFolder)))
            {
                string id = AddMedia(e, one, omd, oneFolder, "one.mp4");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                DlItem it = e.Find(id);
                byte[] got = File.Exists(it.TargetPath) ? File.ReadAllBytes(it.TargetPath) : new byte[0];
                // Первая дорожка файла — rep0.mp4 целиком (init 0-831 плюс все диапазоны подряд).
                bool head = got.Length > rep0.Length;
                for (int i = 0; i < rep0.Length && head; i++) if (got[i] != rep0[i]) head = false;
                T.Check("dash: SegmentList ranges rebuild the single-file representation", done && head, MdInfo(e, id) + " len=" + got.Length);
            }
        }

        // ---------- SegmentBase + sidx: файл собран в тесте ----------
        private static void SidxRun(DlTestServer srv)
        {
            byte[][] segs = new byte[3][];
            for (int i = 0; i < 3; i++)
            {
                segs[i] = new byte[400 + i * 100];
                for (int j = 0; j < segs[i].Length; j++) segs[i][j] = (byte)((i + 1) * 37 + j);
            }
            byte[] init = new byte[64];
            for (int i = 0; i < init.Length; i++) init[i] = (byte)i;
            byte[] sidx = Sidx(segs, 1000);
            byte[] file = Cat(init, sidx, segs[0], segs[1], segs[2]);
            Bin(srv, "sx/one.mp4", file, "video/mp4");
            string mpd = "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT3S\">"
                       + "<Period id=\"0\" start=\"PT0S\"><AdaptationSet contentType=\"video\">"
                       + "<Representation id=\"0\" mimeType=\"video/mp4\" bandwidth=\"1000\" width=\"64\" height=\"36\"><BaseURL>one.mp4</BaseURL>"
                       + "<SegmentBase indexRange=\"" + init.Length + "-" + (init.Length + sidx.Length - 1) + "\">"
                       + "<Initialization range=\"0-" + (init.Length - 1) + "\"/></SegmentBase>"
                       + "</Representation></AdaptationSet></Period></MPD>";
            Txt(srv, "sx/manifest.mpd", mpd, "application/dash+xml");
            string folder = MdDir("sidx");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Dash;
            md.Output = MdOutput.Mp4;
            using (DlEngine e = StartEngine(MdDir("sidx-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, srv.Url("sx/manifest.mpd"), md, folder, "sx.mp4");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("dash: a sidx index turns SegmentBase into segments", done && e.Find(id).Media.SegmentsDone == 3, MdInfo(e, id));
                T.Eq("dash: the sidx ranges rebuild init plus all three segments",
                     Sha(Cat(init, segs[0], segs[1], segs[2])), ShaFile(e.Find(id).TargetPath));
            }
        }

        // sidx версии 0 (ISO/IEC 14496-12 §8.16.3): по одной ссылке на сегмент, тип 0 (медиа).
        private static byte[] Sidx(byte[][] segs, long timescale)
        {
            int size = 12 + 20 + segs.Length * 12;
            byte[] b = new byte[size];
            int at = 0;
            PutU32(b, ref at, (uint)size);
            b[at++] = (byte)'s'; b[at++] = (byte)'i'; b[at++] = (byte)'d'; b[at++] = (byte)'x';
            PutU32(b, ref at, 0);                       // version 0 + flags
            PutU32(b, ref at, 1);                       // reference_ID
            PutU32(b, ref at, (uint)timescale);
            PutU32(b, ref at, 0);                       // earliest_presentation_time
            PutU32(b, ref at, 0);                       // first_offset
            PutU32(b, ref at, (uint)segs.Length);       // reserved(16) + reference_count(16)
            foreach (byte[] s in segs)
            {
                PutU32(b, ref at, (uint)s.Length);      // reference_type = 0 в старшем бите
                PutU32(b, ref at, (uint)timescale);     // subsegment_duration = 1 с
                PutU32(b, ref at, 0x90000000);          // starts_with_SAP
            }
            return b;
        }

        private static void PutU32(byte[] b, ref int at, uint v)
        {
            b[at++] = (byte)(v >> 24);
            b[at++] = (byte)(v >> 16);
            b[at++] = (byte)(v >> 8);
            b[at++] = (byte)v;
        }

        // ---------- прямой файл и страница ----------
        private static void DirectAndPage(DlTestServer srv)
        {
            byte[] plain = FixBytes("plain.mp4");
            Bin(srv, "direct/plain.mp4", plain, "video/mp4");
            string folder = MdDir("direct");
            using (DlEngine e = StartEngine(MdDir("direct-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, srv.Url("direct/plain.mp4"), new DlMedia(), folder, "p.mp4");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: a plain video file goes through the same path", done, MdInfo(e, id));
                T.Eq("engine: the direct file arrives byte for byte", Sha(plain), ShaFile(e.Find(id).TargetPath));
            }

            Txt(srv, "page/index.html", "<!DOCTYPE html><html><body>video here</body></html>", "text/html; charset=utf-8");
            string pageFolder = MdDir("page");
            using (DlEngine e = StartEngine(MdDir("page-store"), MdSettings(pageFolder)))
            {
                string id = AddMedia(e, srv.Url("page/index.html"), new DlMedia(), pageFolder, "page.mp4");
                bool stop = MdWait(delegate { return Stopped(e, id); }, 20000);
                DlItem it = e.Find(id);
                T.Check("engine: a web page is refused with the yt-dlp hint", stop && it.ErrorKind == DlErrorKind.Client
                        && it.Error.IndexOf("yt-dlp", StringComparison.Ordinal) >= 0, MdInfo(e, id));
            }
        }

        // ---------- DRM: ни одного запроса за сегментами ----------
        private static void RefusedDrm(DlTestServer srv)
        {
            DlTestServer.Res seg = Bin(srv, "drm/seg0.ts", new byte[188], "video/mp2t");
            Txt(srv, "drm/index.m3u8", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-PLAYLIST-TYPE:VOD\n"
                + "#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://key\",KEYFORMAT=\"com.apple.streamingkeydelivery\"\n#EXTINF:1.0,\nseg0.ts\n#EXT-X-ENDLIST\n",
                "application/vnd.apple.mpegurl");
            string folder = MdDir("drm");
            using (DlEngine e = StartEngine(MdDir("drm-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, srv.Url("drm/index.m3u8"), new DlMedia(), folder, "drm.ts");
                bool stop = MdWait(delegate { return Stopped(e, id); }, 20000);
                Thread.Sleep(300);
                int requests;
                lock (srv.Gate) requests = seg.Requests;
                DlItem it = e.Find(id);
                T.Check("engine: SAMPLE-AES is refused before any segment is asked for", stop && it.ErrorKind == DlErrorKind.Policy && requests == 0,
                        MdInfo(e, id) + " requests=" + requests);
                T.Check("engine: the refusal is in the item journal", EventsHave(it, "отказ"), MdInfo(e, id));
            }

            DlTestServer.Res dseg = Bin(srv, "drmd/s-1.m4s", new byte[100], "video/iso.segment");
            Txt(srv, "drmd/manifest.mpd", Mpd("static", "PT2S",
                "<ContentProtection schemeIdUri=\"urn:uuid:9a04f079-9840-4286-ab92-e65be0885f95\"/>"
                + "<SegmentTemplate timescale=\"1\" duration=\"1\" startNumber=\"1\" initialization=\"i.mp4\" media=\"s-$Number$.m4s\"/>"), "application/dash+xml");
            string dfolder = MdDir("drmd");
            using (DlEngine e = StartEngine(MdDir("drmd-store"), MdSettings(dfolder)))
            {
                DlMedia md = new DlMedia();
                md.Source = MdSource.Dash;
                string id = AddMedia(e, srv.Url("drmd/manifest.mpd"), md, dfolder, "drm.mp4");
                bool stop = MdWait(delegate { return Stopped(e, id); }, 20000);
                Thread.Sleep(300);
                int requests;
                lock (srv.Gate) requests = dseg.Requests;
                T.Check("dash: a protected manifest is refused before any segment is asked for",
                        stop && e.Find(id).ErrorKind == DlErrorKind.Policy && requests == 0, MdInfo(e, id) + " requests=" + requests);
            }
        }

        // ---------- cookie только своему хосту, чужая схема — отказ ----------
        private static void CookiesAndSchemes(DlTestServer srv)
        {
            byte[] a = new byte[2048], b = new byte[2048];
            for (int i = 0; i < a.Length; i++) { a[i] = (byte)(i & 0xFF); b[i] = (byte)(255 - (i & 0xFF)); }
            DlTestServer.Res elsewhere = Bin(srv, "ck/far.ts", b, "video/mp2t");
            DlTestServer.Res near = Bin(srv, "ck/seg0.ts", a, "video/mp2t");
            near.RequireCookie = "sid=SECRET";
            DlTestServer.Res hop = srv.Add("ck/seg1.ts", new DlTestServer.Res());
            hop.RedirectTo = srv.UrlLocalhost("ck/far.ts");
            DlTestServer.Res list = Txt(srv, "ck/index.m3u8", "#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-PLAYLIST-TYPE:VOD\n"
                + "#EXTINF:1.0,\nseg0.ts\n#EXTINF:1.0,\nseg1.ts\n#EXT-X-ENDLIST\n", "application/vnd.apple.mpegurl");
            list.RequireCookie = "sid=SECRET";
            string folder = MdDir("cookies");
            using (DlEngine e = StartEngine(MdDir("cookies-store"), MdSettings(folder)))
            {
                DlAddRequest r = new DlAddRequest();
                r.Url = srv.Url("ck/index.m3u8");
                r.Media = new DlMedia();
                r.Folder = folder;
                r.FileName = "c.ts";
                r.Cookies = "sid=SECRET";
                r.AllowDuplicate = true;
                string dup, err;
                string id = e.Add(r, out dup, out err);
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                bool toOrigin = false, toOther = false;
                lock (srv.Gate)
                {
                    foreach (string c in near.Cookies) if (c.IndexOf("SECRET", StringComparison.Ordinal) >= 0) toOrigin = true;
                    foreach (string c in elsewhere.Cookies) if (c.IndexOf("SECRET", StringComparison.Ordinal) >= 0) toOther = true;
                }
                T.Check("engine: the download completes across a redirect to another host", done, MdInfo(e, id) + " " + err);
                T.Eq("engine: the redirected segment came from the other host", Sha(Cat(a, b)), ShaFile(e.Find(id).TargetPath));
                T.Check("engine: cookies go to the host they were given for", toOrigin);
                T.Check("engine: cookies are never forwarded to another host", !toOther);
            }

            DlTestServer.Res bad = srv.Add("scheme/seg0.ts", new DlTestServer.Res());
            bad.RedirectTo = "ftp://127.0.0.1/seg0.ts";
            Txt(srv, "scheme/index.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:1.0,\nseg0.ts\n#EXT-X-ENDLIST\n",
                "application/vnd.apple.mpegurl");
            string sfolder = MdDir("scheme");
            using (DlEngine e = StartEngine(MdDir("scheme-store"), MdSettings(sfolder)))
            {
                string id = AddMedia(e, srv.Url("scheme/index.m3u8"), new DlMedia(), sfolder, "sc.ts");
                bool stop = MdWait(delegate { return Stopped(e, id); }, 20000);
                T.Check("engine: a segment redirect to a foreign scheme fails the download",
                        stop && e.Find(id).ErrorKind == DlErrorKind.Policy, MdInfo(e, id));
            }
        }

        // ---------- поток для проверок продолжения: медленный, из нескольких сегментов ----------
        private static byte[][] SlowStream(DlTestServer srv, string prefix, int count, int size, int rateBps, List<DlTestServer.Res> segs)
        {
            byte[][] bodies = new byte[count][];
            StringBuilder pl = new StringBuilder();
            pl.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
            for (int i = 0; i < count; i++)
            {
                bodies[i] = new byte[size];
                for (int j = 0; j < size; j++) bodies[i][j] = (byte)((i * 131 + j * 7) & 0xFF);
                DlTestServer.Res r = Bin(srv, prefix + "/seg" + i + ".ts", bodies[i], "video/mp2t");
                r.RateBps = rateBps;
                if (segs != null) segs.Add(r);
                pl.Append("#EXTINF:1.0,\nseg").Append(i.ToString(CultureInfo.InvariantCulture)).Append(".ts\n");
            }
            pl.Append("#EXT-X-ENDLIST\n");
            Txt(srv, prefix + "/index.m3u8", pl.ToString(), "application/vnd.apple.mpegurl");
            return bodies;
        }

        // ---------- пауза, новый движок, продолжение ----------
        private static void ResumeAfterPause(DlTestServer srv)
        {
            const int count = 6, size = 96 * 1024;
            List<DlTestServer.Res> segs = new List<DlTestServer.Res>();
            byte[][] bodies = SlowStream(srv, "resume", count, size, 200 * 1024, segs);
            byte[] expected = Cat(bodies);

            string folder = MdDir("resume");
            string store = MdDir("resume-store");
            DlSettings s = MdSettings(folder);
            s.Segments = 2;
            string id;
            string partsDir;
            using (DlEngine e = StartEngine(store, s))
            {
                id = AddMedia(e, srv.Url("resume/index.m3u8"), new DlMedia(), folder, "r.ts");
                bool started = MdWait(delegate { DlItem x = e.Find(id); return x != null && x.Media.SegmentsDone >= 2; }, 30000);
                partsDir = e.Find(id).Media.PartsDir;
                e.Pause(id);
                MdWait(delegate { return e.Find(id).State == DlState.Paused; }, 10000);
                T.Check("engine: a pause mid-stream keeps the journal and the data", started && File.Exists(Path.Combine(partsDir, DlMediaRun.JournalName)),
                        MdInfo(e, id) + " " + partsDir);
            }
            using (DlEngine e2 = StartEngine(store, s))
            {
                e2.Resume(id);
                bool done = MdWait(delegate { return Done(e2, id); }, 40000);
                T.Check("engine: a new engine over the same store finishes the download", done, MdInfo(e2, id));
                T.Eq("engine: the resumed file is identical to an uninterrupted one", Sha(expected), ShaFile(e2.Find(id).TargetPath));
                int once = 0, total = 0;
                lock (srv.Gate)
                    foreach (DlTestServer.Res r in segs)
                    {
                        total += r.Requests;
                        if (r.Requests == 1) once++;
                    }
                T.Check("engine: segments already in the file are not downloaded again", once >= 2 && total < count * 2,
                        "once=" + once + " total=" + total);
            }
        }

        // ---------- процесс убит: журнал отстаёт, данные на месте ----------
        private static void ResumeAfterKill(DlTestServer srv)
        {
            const int count = 6, size = 96 * 1024;
            byte[][] bodies = SlowStream(srv, "kill", count, size, 200 * 1024, null);
            byte[] expected = Cat(bodies);
            string folder = MdDir("kill");
            string store = MdDir("kill-store");
            DlSettings s = MdSettings(folder);
            s.Segments = 2;
            string id;
            string partsDir = "";
            DlEngine dead = StartEngine(store, s);
            try
            {
                id = AddMedia(dead, srv.Url("kill/index.m3u8"), new DlMedia(), folder, "k.ts");
                bool started = MdWait(delegate { DlItem x = dead.Find(id); return x != null && x.Media.SegmentsDone >= 2; }, 30000);
                partsDir = dead.Find(id).Media.PartsDir;
                DlMediaRun run;
                lock (MdGate) run = _lastRun;
                // Планировщик останавливается без сохранения, запуск — без журнала: как будто процесс сняли.
                typeof(DlEngine).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(dead, true);
                run.KillForTest();
                run.Join(10000);
                T.Check("engine: the killed run leaves its journal and parts behind",
                        started && Directory.Exists(partsDir) && File.Exists(Path.Combine(partsDir, DlMediaRun.JournalName)), partsDir);
            }
            finally { dead.Dispose(); }

            using (DlEngine e = StartEngine(store, s))
            {
                bool done = MdWait(delegate { return Done(e, id); }, 40000);
                T.Check("engine: after a kill the next start resumes from the journal", done, MdInfo(e, id));
                T.Eq("engine: the file after a kill-restart is byte-identical", Sha(expected), ShaFile(e.Find(id).TargetPath));
                T.Check("engine: the parts folder is cleaned up in the end", !Directory.Exists(partsDir));
            }
        }

        // ---------- yt-dlp: 403 на сегменте — одно повторное извлечение ----------
        private static void YtdlpRefresh(DlTestServer srv)
        {
            byte[] body = new byte[200 * 1024];
            for (int i = 0; i < body.Length; i++) body[i] = (byte)((i * 13) & 0xFF);
            DlTestServer.Res old = srv.Add("yt/old.bin", new DlTestServer.Res());
            old.Status = 403;
            Bin(srv, "yt/new.bin", body, "video/mp4");
            string page = srv.Url("yt/watch");

            string folder = MdDir("ytdlp");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Ytdlp;
            md.VariantId = "v-137";
            md.Output = MdOutput.Mp4;
            md.FormatIds = "137";
            md.ExtractedUtc = DateTime.UtcNow;
            MdHooks.ExtractFn saved = MdHooks.Extract;
            string id = null;
            using (DlEngine e = StartEngine(MdDir("ytdlp-store"), MdSettings(folder)))
            {
                try
                {
                    lock (MdGate)
                    {
                        _extractCalls = 0;
                        _extractPage = "";
                        _extractResult = YtManifest(srv.Url("yt/new.bin"), body.Length);
                    }
                    MdHooks.Extract = FakeExtract;
                    DlAddRequest r = new DlAddRequest();
                    r.Url = srv.Url("yt/old.bin");
                    r.PageUrl = page;
                    r.Media = md;
                    r.Folder = folder;
                    r.FileName = "y.mp4";
                    r.StartPaused = true;
                    r.AllowDuplicate = true;
                    string dup, err;
                    id = e.Add(r, out dup, out err);
                    DlMediaRun.RememberManifest(id, YtManifest(srv.Url("yt/old.bin"), body.Length));
                    e.Resume(id);
                    bool done = MdWait(delegate { return Done(e, id); }, 40000);
                    int calls;
                    string askedFor;
                    lock (MdGate) { calls = _extractCalls; askedFor = _extractPage; }
                    T.Check("engine: a 403 on a yt-dlp item re-extracts the links once", done && calls == 1, MdInfo(e, id) + " calls=" + calls);
                    T.Eq("engine: the re-extraction is asked for the page url", page, askedFor);
                    T.Eq("engine: the refreshed links finish the same file", Sha(body), ShaFile(e.Find(id).TargetPath));
                    T.Check("engine: the extraction cache spared the first run", EventsHave(e.Find(id), "ссылки обновлены"), MdInfo(e, id));
                }
                finally
                {
                    MdHooks.Extract = saved;
                    if (id != null) DlMediaRun.RememberManifest(id, null);
                    lock (MdGate) _extractResult = null;
                }
            }
        }

        private static MdManifest YtManifest(string url, long size)
        {
            MdManifest m = new MdManifest();
            m.Source = MdSource.Ytdlp;
            m.Title = "yt item";
            m.ExpiresUtc = DateTime.UtcNow.AddHours(1);
            MdTrack t = new MdTrack();
            t.Id = "v-137";
            t.FormatId = "137";
            t.Kind = MdTrackKind.Muxed;
            t.Layout = MdLayout.Mp4;
            t.Url = url;
            t.SizeHint = size;
            t.ChunkBytes = 64 * 1024;
            t.Width = 640;
            t.Height = 360;
            MdVariant v = new MdVariant();
            v.Id = t.Id;
            v.Label = "360p";
            v.Height = 360;
            v.Main = t;
            m.Variants.Add(v);
            return m;
        }

        // ---------- склейки нет: части сохранены ----------
        private static void NoMuxHook(DlTestServer srv)
        {
            string url = MdFx.Serve(srv, "hls-ts", "nomux", "index.m3u8");
            string folder = MdDir("nomux");
            Func<MdMuxJob, MdMuxResult> saved = MdHooks.Mux;
            using (DlEngine e = StartEngine(MdDir("nomux-store"), MdSettings(folder)))
            {
                try
                {
                    MdHooks.Mux = null;
                    string id = AddMedia(e, url, new DlMedia(), folder, "nm.ts");
                    bool stop = MdWait(delegate { return Stopped(e, id); }, 30000);
                    DlItem it = e.Find(id);
                    string parts = it.Media.PartsDir;
                    T.Check("engine: without an assembly hook the download fails as policy",
                            stop && it.ErrorKind == DlErrorKind.Policy && it.Error.IndexOf("склей", StringComparison.Ordinal) >= 0, MdInfo(e, id));
                    T.Check("engine: the downloaded parts are kept for a retry",
                            Directory.Exists(parts) && File.Exists(Path.Combine(parts, DlMediaRun.JournalName)), parts);
                }
                finally { MdHooks.Mux = saved; }
            }
        }

        // ---------- просмотр во время загрузки ----------
        private static void WatchPreview(DlTestServer srv)
        {
            const int count = 5, size = 96 * 1024;
            byte[][] bodies = SlowStream(srv, "watch", count, size, 200 * 1024, null);
            string folder = MdDir("watch");
            DlMedia md = new DlMedia();
            md.Watch = true;
            using (DlEngine e = StartEngine(MdDir("watch-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, srv.Url("watch/index.m3u8"), md, folder, "w.ts");
                string preview = Path.Combine(folder, "w.ts" + DlMediaRun.PartsSuffix + "\\preview.ts");
                bool grew = MdWait(delegate
                {
                    DlItem x = e.Find(id);
                    return x != null && x.Media.PreviewPath.Length > 0 && File.Exists(preview) && new FileInfo(preview).Length >= size;
                }, 30000);
                T.Check("engine: watching keeps a growing preview file in the parts folder", grew,
                        MdInfo(e, id) + " " + (e.Find(id) == null ? "" : e.Find(id).Media.PreviewPath));
                bool done = MdWait(delegate { return Done(e, id); }, 40000);
                T.Check("engine: the preview is gone once the file is ready", done && e.Find(id).Media.PreviewPath.Length == 0 && !File.Exists(preview), MdInfo(e, id));
                T.Eq("engine: watching does not change the result", Sha(Cat(bodies)), ShaFile(e.Find(id).TargetPath));
            }
        }

        // ---------- трансляция: окно едет, пропуск, остановка пользователем ----------
        private static void LiveWindow(DlTestServer srv)
        {
            byte[][] bodies = new byte[12][];
            for (int i = 0; i < bodies.Length; i++)
            {
                bodies[i] = Encoding.ASCII.GetBytes("s" + i.ToString("000", CultureInfo.InvariantCulture) + "............");
                Bin(srv, "live/s" + i + ".ts", bodies[i], "video/mp2t");
            }
            PutWindow(srv, 0, 3, false);
            string folder = MdDir("live");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Hls;
            using (DlEngine e = StartEngine(MdDir("live-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, srv.Url("live/index.m3u8"), md, folder, "l.ts");
                bool first = MdWait(delegate { DlItem x = e.Find(id); return x != null && x.Media.SegmentsDone >= 3; }, 20000);
                T.Check("live: recording starts at the live edge of the window", first, MdInfo(e, id));
                T.Eq("live: the total is unknown while recording", -1, e.Find(id).Media.SegmentsTotal);
                PutWindow(srv, 1, 3, false);
                bool second = MdWait(delegate { return e.Find(id).Media.SegmentsDone >= 4; }, 20000);
                PutWindow(srv, 2, 3, false);
                bool third = MdWait(delegate { return e.Find(id).Media.SegmentsDone >= 5; }, 20000);
                T.Check("live: each reload appends only the new segments", second && third, MdInfo(e, id));
                PutWindow(srv, 6, 3, false);          // 5 не попал ни в одно окно — разрыв
                bool jumped = MdWait(delegate { return e.Find(id).Media.SegmentsDone >= 8; }, 20000);
                T.Check("live: a segment that fell out of the window is journaled as a gap",
                        jumped && EventsHave(e.Find(id), "пропуск"), MdInfo(e, id));
                e.Find(id).Media.StopLive = true;
                bool done = MdWait(delegate { return Done(e, id); }, 40000);
                T.Check("live: stop-recording assembles what has been recorded", done, MdInfo(e, id));
                bool truncated;
                lock (MdGate) truncated = _muxTruncated;
                T.Check("live: the assembly is told the stream may be cut short", truncated);
                byte[] got = File.Exists(e.Find(id).TargetPath) ? File.ReadAllBytes(e.Find(id).TargetPath) : new byte[0];
                T.Eq("live: every recorded segment is in the file once, in order", "0,1,2,3,4,6,7,8", Recorded(got));
            }
        }

        // Плейлист трансляции: окно из count сегментов, начиная с first.
        private static void PutWindow(DlTestServer srv, int first, int count, bool end)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:").Append(first.ToString(CultureInfo.InvariantCulture)).Append('\n');
            for (int i = first; i < first + count; i++)
                sb.Append("#EXTINF:1.0,\ns").Append(i.ToString(CultureInfo.InvariantCulture)).Append(".ts\n");
            if (end) sb.Append("#EXT-X-ENDLIST\n");
            Txt(srv, "live/index.m3u8", sb.ToString(), "application/vnd.apple.mpegurl");
        }

        // Номера сегментов, как они лежат в собранном файле (каждый сегмент — 16 байт «sNNN............»).
        private static string Recorded(byte[] data)
        {
            StringBuilder sb = new StringBuilder();
            for (int at = 0; at + 16 <= data.Length; at += 16)
            {
                string chunk = Encoding.ASCII.GetString(data, at, 16);
                if (chunk.Length < 4 || chunk[0] != 's') return "broken at " + at;
                int n;
                if (!int.TryParse(chunk.Substring(1, 3), NumberStyles.None, CultureInfo.InvariantCulture, out n)) return "broken at " + at;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(n.ToString(CultureInfo.InvariantCulture));
            }
            if (data.Length % 16 != 0) sb.Append("+tail").Append((data.Length % 16).ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }
}
