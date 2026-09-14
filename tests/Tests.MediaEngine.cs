// Windows Process Cleaner — часть «media»: движок видео-загрузок (HLS, DASH, сегменты, ключи, продолжение, трансляции).
//
// Разборщики проверяются на настоящих файлах из tests\media (их сделал ffmpeg заранее), движок — целиком: настоящий
// DlEngine создаёт настоящий DlMediaRun, тот ходит по HTTP на 127.0.0.1 (DlTestServer) и складывает файл частями.
// Ненастоящие только две границы: склейка (MdHooks.Mux здесь просто сцепляет файлы дорожек — ядро в эту часть сборки
// не входит) и yt-dlp (MdHooks.Extract отдаёт заранее подготовленный манифест). Ожидания считаются независимо:
// содержимое итогового файла сверяется с байтами, которые отдавал сервер, а не с тем, что насчитал движок.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class MediaTests
    {
        private static int _mdN;
        private static readonly object MdGate = new object();
        private static DlMediaRun _lastRun;
        private static int _muxCalls;
        private static bool _muxTruncated;
        private static readonly List<string> _muxInputs = new List<string>();
        private static int _extractCalls;
        private static string _extractPage = "";
        private static MdManifest _extractResult;

        static partial void RunEngine()
        {
            if (MdFx.Dir.Length == 0)
            {
                T.Skip("engine: media fixtures", "tests\\media is missing");
                return;
            }
            HlsTables();
            DashTables();
            FetchUnits();

            Func<DlItem, DlSettings, DlTokenBucket, IDlEnvironment, IDlTransferHost, IDlRun> savedCreate = MdHooks.CreateRun;
            Func<MdMuxJob, MdMuxResult> savedMux = MdHooks.Mux;
            MdHooks.ExtractFn savedExtract = MdHooks.Extract;
            Func<DateTime> savedNow = MdDash.NowUtc;
            int savedIdle = DlMediaRun.LiveIdleExtraSeconds;
            try
            {
                MdHooks.CreateRun = NewRun;
                MdHooks.Mux = TestMux;
                MdHooks.Extract = null;
                DlMediaRun.LiveIdleExtraSeconds = 2;
                using (DlTestServer srv = new DlTestServer())
                {
                    VodTs(srv);
                    Fmp4AndKeys(srv);
                    ByteRangeRun(srv);
                    MasterAndSubtitles(srv);
                    DashRuns(srv);
                    SidxRun(srv);
                    DirectAndPage(srv);
                    RefusedDrm(srv);
                    CookiesAndSchemes(srv);
                    ResumeAfterPause(srv);
                    ResumeAfterKill(srv);
                    YtdlpRefresh(srv);
                    NoMuxHook(srv);
                    WatchPreview(srv);
                    LiveWindow(srv);
                }
            }
            finally
            {
                MdHooks.CreateRun = savedCreate;
                MdHooks.Mux = savedMux;
                MdHooks.Extract = savedExtract;
                MdDash.NowUtc = savedNow;
                DlMediaRun.LiveIdleExtraSeconds = savedIdle;
            }
        }

        // ------------------------------------------------------------------ //
        //  Помощники
        // ------------------------------------------------------------------ //
        private static IDlRun NewRun(DlItem item, DlSettings s, DlTokenBucket global, IDlEnvironment env, IDlTransferHost host)
        {
            DlMediaRun r = new DlMediaRun(item, s, global, env, host);
            lock (MdGate) _lastRun = r;
            return r;
        }

        // Вместо ядра: дорожки сцепляются подряд, субтитры кладутся рядом файлом.
        private static MdMuxResult TestMux(MdMuxJob job)
        {
            MdMuxResult r = new MdMuxResult();
            List<MdInput> media = new List<MdInput>(), subs = new List<MdInput>();
            foreach (MdInput i in job.Inputs)
            {
                if (i.Kind == MdTrackKind.Subtitles) subs.Add(i);
                else media.Add(i);
            }
            lock (MdGate)
            {
                _muxCalls++;
                _muxTruncated = job.AllowTruncated;
                _muxInputs.Clear();
                foreach (MdInput i in job.Inputs) _muxInputs.Add(i.Kind + ":" + i.Layout + ":" + Path.GetFileName(i.Path));
            }
            try
            {
                if (media.Count == 0) { r.Error = "no media inputs"; return r; }
                string outPath = Path.ChangeExtension(job.OutPath, MuxExt(media[0].Layout));
                using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write))
                    foreach (MdInput i in media)
                        using (FileStream s = new FileStream(i.Path, FileMode.Open, FileAccess.Read)) s.CopyTo(o);
                int n = 0;
                foreach (MdInput i in subs)
                {
                    n++;
                    string tag = i.Language.Length > 0 ? i.Language : "s" + n.ToString(CultureInfo.InvariantCulture);
                    string side = Path.Combine(Path.GetDirectoryName(outPath), Path.GetFileNameWithoutExtension(outPath) + "." + tag + ".vtt");
                    File.Copy(i.Path, side, true);
                    r.SubtitleFiles.Add(side);
                }
                r.Ok = true;
                r.OutPath = outPath;
                r.Output = job.Output;
            }
            catch (Exception ex) { r.Ok = false; r.Error = ex.Message; }
            return r;
        }

        private static string MuxExt(MdLayout l)
        {
            switch (l)
            {
                case MdLayout.Ts: return ".ts";
                case MdLayout.WebM: return ".webm";
                case MdLayout.Vtt: return ".vtt";
                default: return ".mp4";
            }
        }

        private static MdManifest FakeExtract(string pageUrl, Func<bool> cancel, out string error)
        {
            error = null;
            lock (MdGate)
            {
                _extractCalls++;
                _extractPage = pageUrl ?? "";
                if (_extractResult == null) error = "no formats";
                return _extractResult;
            }
        }

        private static string MdDir(string name) { return Fx.MakeDir(Fx.Root, "md-" + name + "-" + (++_mdN).ToString(CultureInfo.InvariantCulture)); }

        private static DlSettings MdSettings(string folder)
        {
            DlSettings s = new DlSettings();
            s.Folder = folder;
            s.MarkOfTheWeb = false;
            s.PreventSleep = false;
            s.SmallFileMB = 0;
            s.Segments = 4;
            return s;
        }

        private static DlEngine StartEngine(string store, DlSettings s)
        {
            DlEngine e = new DlEngine(new DlStore(store), s, new FakeDlEnv());
            e.Start();
            return e;
        }

        private static string AddMedia(DlEngine e, string url, DlMedia md, string folder, string fileName)
        {
            DlAddRequest r = new DlAddRequest();
            r.Url = url;
            r.Media = md;
            r.Folder = folder;
            r.FileName = fileName;
            r.AllowDuplicate = true;
            string dup, err;
            string id = e.Add(r, out dup, out err);
            if (id == null) T.Check("engine: item added", false, err);
            return id;
        }

        private static bool MdWait(Func<bool> cond, int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(40);
            }
            return cond();
        }

        private static bool Done(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            return it != null && it.State == DlState.Completed;
        }

        private static bool Stopped(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            return it != null && (it.State == DlState.Failed || it.State == DlState.NeedsLink);
        }

        private static string MdInfo(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            if (it == null) return "no item";
            string ev = "";
            lock (it.Events) if (it.Events.Count > 0) ev = it.Events[it.Events.Count - 1].Text;
            return it.State + "/" + it.ErrorKind + "/" + it.Error + "/" + it.Media.Phase + "/" + it.Media.SegmentsDone + " · " + ev;
        }

        private static bool EventsHave(DlItem it, string part)
        {
            lock (it.Events)
                foreach (DlEvent e in it.Events) if ((e.Text ?? "").IndexOf(part, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static string Sha(byte[] data)
        {
            using (SHA256 h = SHA256.Create()) return DlTestServer.Hex(h.ComputeHash(data));
        }

        private static string ShaFile(string path)
        {
            return File.Exists(path) ? Sha(File.ReadAllBytes(path)) : "<missing:" + Path.GetFileName(path) + ">";
        }

        private static byte[] Cat(params byte[][] parts)
        {
            int n = 0;
            foreach (byte[] p in parts) n += p.Length;
            byte[] all = new byte[n];
            int at = 0;
            foreach (byte[] p in parts)
            {
                Buffer.BlockCopy(p, 0, all, at, p.Length);
                at += p.Length;
            }
            return all;
        }

        private static byte[] FixBytes(string relative)
        {
            byte[] b = MdFx.Bytes(relative);
            return b ?? new byte[0];
        }

        private static string FixText(string relative)
        {
            string p = MdFx.File(relative);
            return p == null ? "" : File.ReadAllText(p);
        }

        private static DlTestServer.Res Bin(DlTestServer srv, string key, byte[] body, string contentType)
        {
            DlTestServer.Res r = new DlTestServer.Res();
            r.Body = body;
            r.Size = body.Length;
            r.ContentType = contentType;
            return srv.Add(key, r);
        }

        private static DlTestServer.Res Txt(DlTestServer srv, string key, string text, string contentType)
        {
            return Bin(srv, key, Encoding.UTF8.GetBytes(text), contentType);
        }

        private static string Seqs(MdTrack t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (MdSegment s in t.Segments)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(s.Sequence.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Ranges(MdTrack t)
        {
            StringBuilder sb = new StringBuilder();
            foreach (MdSegment s in t.Segments)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(s.Offset.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s.Length.ToString(CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static string Tail(string url)
        {
            int i = (url ?? "").LastIndexOf('/');
            return i < 0 ? url ?? "" : url.Substring(i + 1);
        }

        private static double SumDur(MdTrack t)
        {
            double d = 0;
            foreach (MdSegment s in t.Segments) d += s.Duration;
            return d;
        }

        // ------------------------------------------------------------------ //
        //  Разбор HLS
        // ------------------------------------------------------------------ //
        private static void HlsTables()
        {
            const string baseUrl = "http://127.0.0.1:1/x/index.m3u8";
            string err;
            bool refused;

            MdTrack ts = new MdTrack();
            bool ok = MdHls.ParseMedia(FixText("hls-ts\\index.m3u8"), baseUrl, ts, out err, out refused);
            T.Check("hls: VOD playlist gives 4 segments of one second", ok && ts.Segments.Count == 4 && Math.Abs(SumDur(ts) - 4.0) < 0.01 && !ts.Live, err + " " + Seqs(ts));
            T.Eq("hls: segment url is resolved against the playlist", "http://127.0.0.1:1/x/seg0.ts", ts.Segments.Count > 0 ? ts.Segments[0].Url : "");
            T.Eq("hls: media sequence numbers the segments", "0,1,2,3", Seqs(ts));
            T.Eq("hls: target duration is kept for live reloads", 1.0, ts.TargetDuration);
            T.Eq("hls: a .ts playlist lays out as MPEG-TS", MdLayout.Ts, ts.Layout);

            MdTrack aes = new MdTrack();
            ok = MdHls.ParseMedia(FixText("hls-aes\\index.m3u8"), baseUrl, aes, out err, out refused);
            bool keyed = ok && aes.Segments.Count == 4;
            foreach (MdSegment s in aes.Segments) if (s.Key == null || s.Key.Method != "AES-128") keyed = false;
            T.Check("hls: AES-128 key covers every segment", keyed, err);
            T.Eq("hls: key uri is absolute", "http://127.0.0.1:1/x/key.bin", keyed ? aes.Segments[0].Key.Uri : "");
            T.Eq("hls: key IV comes from the playlist", "0f0e0d0c0b0a09080706050403020100", keyed ? DlTestServer.Hex(aes.Segments[0].Key.Iv) : "");

            MdTrack fmp4 = new MdTrack();
            ok = MdHls.ParseMedia(FixText("hls-fmp4\\index.m3u8"), baseUrl, fmp4, out err, out refused);
            bool mapped = ok && fmp4.Segments.Count == 4;
            foreach (MdSegment s in fmp4.Segments) if (s.InitUrl != "http://127.0.0.1:1/x/init.mp4") mapped = false;
            T.Check("hls: EXT-X-MAP init is attached to every segment", mapped && fmp4.Layout == MdLayout.Fmp4, err + " " + fmp4.Layout);

            MdTrack br = new MdTrack();
            ok = MdHls.ParseMedia(FixText("hls-byterange\\index.m3u8"), baseUrl, br, out err, out refused);
            T.Eq("hls: byterange offsets and lengths", "0:15040,15040:15604,30644:17296,47940:18236", ok ? Ranges(br) : err);
            bool oneFile = ok && br.Segments.Count == 4;
            foreach (MdSegment s in br.Segments) if (Tail(s.Url) != "index.ts") oneFile = false;
            T.Check("hls: all byterange segments point at one file", oneFile);

            MdTrack cont = new MdTrack();
            ok = MdHls.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n#EXT-X-BYTERANGE:100@10\na.ts\n#EXTINF:1,\n"
                                  + "#EXT-X-BYTERANGE:50\na.ts\n#EXT-X-ENDLIST\n", baseUrl, cont, out err, out refused);
            T.Eq("hls: byterange without an offset continues the previous one", "10:100,110:50", ok ? Ranges(cont) : err);
            MdTrack jump = new MdTrack();
            ok = MdHls.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n#EXT-X-BYTERANGE:100@10\na.ts\n#EXTINF:1,\n"
                                  + "#EXT-X-BYTERANGE:50\nb.ts\n#EXT-X-ENDLIST\n", baseUrl, jump, out err, out refused);
            T.Check("hls: byterange without an offset after another file is refused", !ok && jump.Segments.Count == 0, err);

            MdManifest master = MdHls.ParseMaster(FixText("hls-master\\master.m3u8"), "http://127.0.0.1:1/m/master.m3u8", out err);
            T.Eq("hls: master variants are sorted from the best", "v180p-154k,v90p-84k",
                 master.Variants.Count == 2 ? master.Variants[0].Id + "," + master.Variants[1].Id : "count=" + master.Variants.Count + " " + err);
            T.Check("hls: variant keeps resolution, bandwidth and its playlist",
                    master.Variants.Count == 2 && master.Variants[0].Height == 180 && master.Variants[0].Bandwidth == 154912
                    && Tail(master.Variants[0].Main.Url) == "v1.m3u8" && master.Variants[0].Main.Kind == MdTrackKind.Video);
            T.Check("hls: audio group is a separate default track",
                    master.Audio.Count == 1 && master.Audio[0].GroupId == "group_aud" && master.Audio[0].Language == "en"
                    && master.Audio[0].Default && Tail(master.Audio[0].Url) == "vEnglish.m3u8"
                    && master.Variants[0].AudioGroup == "group_aud",
                    master.Audio.Count == 1 ? master.Audio[0].Id : "count=" + master.Audio.Count);

            MdManifest asMaster = MdHls.ParseMaster(FixText("hls-ts\\index.m3u8"), baseUrl, out err);
            T.Check("hls: a media playlist given as master becomes one variant with segments",
                    asMaster.Variants.Count == 1 && asMaster.Variants[0].Main.Segments.Count == 4 && Math.Abs(asMaster.DurationSec - 4.0) < 0.01, err);

            MdTrack live = new MdTrack();
            MdHls.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:7\n#EXTINF:2,\ns7.ts\n#EXTINF:2,\ns8.ts\n", baseUrl, live, out err, out refused);
            T.Check("hls: no ENDLIST means live, sequence continues from MEDIA-SEQUENCE", live.Live && Seqs(live) == "7,8" && live.TargetDuration == 2, Seqs(live));

            MdTrack sample = new MdTrack();
            ok = MdHls.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://x\"\n#EXTINF:1,\ns0.ts\n#EXT-X-ENDLIST\n",
                                  baseUrl, sample, out err, out refused);
            T.Check("hls: SAMPLE-AES is refused and leaves no segments", !ok && refused && sample.Segments.Count == 0 && err.Length > 0, err);

            MdTrack wv = new MdTrack();
            ok = MdHls.ParseMedia("#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXT-X-KEY:METHOD=AES-128,URI=\"k\",KEYFORMAT=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"\n"
                                  + "#EXTINF:1,\ns0.ts\n#EXT-X-ENDLIST\n", baseUrl, wv, out err, out refused);
            T.Check("hls: a widevine KEYFORMAT is refused", !ok && refused, err);

            MdManifest sk = MdHls.ParseMaster("#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=SAMPLE-AES,KEYFORMAT=\"com.apple.streamingkeydelivery\",URI=\"skd://x\"\n"
                                              + "#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=64x36\nv.m3u8\n", "http://127.0.0.1:1/m/m.m3u8", out err);
            T.Check("hls: SESSION-KEY with DRM refuses the whole master", sk.Refused.Length > 0, sk.Refused);
        }

        // ------------------------------------------------------------------ //
        //  Разбор DASH
        // ------------------------------------------------------------------ //
        private static void DashTables()
        {
            string err;
            MdManifest tl = MdDash.Parse(FixText("dash-tl\\manifest.mpd"), "http://127.0.0.1:1/d/manifest.mpd", out err);
            MdTrack v = tl.Variants.Count > 0 ? tl.Variants[0].Main : null;
            MdTrack a = tl.Audio.Count > 0 ? tl.Audio[0] : null;
            T.Check("dash: timeline r=3 expands to four video segments", v != null && v.Segments.Count == 4 && Seqs(v) == "1,2,3,4", err + " " + (v == null ? "no video" : Seqs(v)));
            T.Check("dash: timeline S with repeats and a tail gives five audio segments", a != null && a.Segments.Count == 5 && Seqs(a) == "1,2,3,4,5",
                    a == null ? "no audio" : Seqs(a));
            T.Eq("dash: $RepresentationID$ and $Number$ expand in the media template", "http://127.0.0.1:1/d/seg0-1.m4s", v == null ? "" : v.Segments[0].Url);
            T.Eq("dash: initialization template expands as well", "http://127.0.0.1:1/d/init0.m4s", v == null ? "" : v.Segments[0].InitUrl);
            T.Check("dash: segment durations come from d/timescale", v != null && Math.Abs(v.Segments[0].Duration - 1.0) < 0.001
                    && a != null && Math.Abs(a.Segments[0].Duration - 38912.0 / 48000.0) < 0.001);
            T.Eq("dash: representation ids become track ids", "v-0/a-1", (v == null ? "?" : v.Id) + "/" + (a == null ? "?" : a.Id));
            T.Check("dash: a static manifest is not live", !tl.Live && Math.Abs(tl.DurationSec - 4.0) < 0.01, tl.DurationSec.ToString(CultureInfo.InvariantCulture));

            MdManifest num = MdDash.Parse(FixText("dash-num\\manifest.mpd"), "http://127.0.0.1:1/n/manifest.mpd", out err);
            MdTrack nv = num.Variants.Count > 0 ? num.Variants[0].Main : null;
            T.Check("dash: $Number$ with duration covers the whole presentation", nv != null && nv.Segments.Count == 4 && Seqs(nv) == "1,2,3,4",
                    err + " " + (nv == null ? "no video" : Seqs(nv)));

            MdManifest one = MdDash.Parse(FixText("dash-single\\manifest.mpd"), "http://127.0.0.1:1/s/manifest.mpd", out err);
            MdTrack ov = one.Variants.Count > 0 ? one.Variants[0].Main : null;
            T.Eq("dash: SegmentList mediaRange gives offsets inside one file", "832:7718,8550:7074,15624:8700,24324:8280", ov == null ? err : Ranges(ov));
            T.Check("dash: Initialization range is kept", ov != null && ov.Segments[0].InitOffset == 0 && ov.Segments[0].InitLength == 832
                    && Tail(ov.Segments[0].Url) == "rep0.mp4");

            MdTrack bt = null;
            MdManifest time = MdDash.Parse(Mpd("static", "PT4S",
                "<SegmentTemplate timescale=\"1000\" initialization=\"i.mp4\" media=\"s-$Time$.m4s\"><SegmentTimeline><S t=\"0\" d=\"2000\" r=\"1\"/></SegmentTimeline></SegmentTemplate>"),
                "http://127.0.0.1:1/t/m.mpd", out err);
            if (time.Variants.Count > 0) bt = time.Variants[0].Main;
            T.Eq("dash: a $Time$ template numbers segments by time", "0,2000", bt == null ? err : Seqs(bt));
            T.Eq("dash: $Time$ is substituted into the url", "http://127.0.0.1:1/t/s-2000.m4s", bt == null || bt.Segments.Count < 2 ? "" : bt.Segments[1].Url);

            MdManifest rep = MdDash.Parse(Mpd("static", "PT10S",
                "<SegmentTemplate timescale=\"1\" initialization=\"i.mp4\" media=\"s-$Number$.m4s\" startNumber=\"1\"><SegmentTimeline><S t=\"0\" d=\"2\" r=\"-1\"/></SegmentTimeline></SegmentTemplate>"),
                "http://127.0.0.1:1/t/m.mpd", out err);
            MdTrack rt = rep.Variants.Count > 0 ? rep.Variants[0].Main : null;
            T.Eq("dash: r=-1 repeats to the end of the period", "1,2,3,4,5", rt == null ? err : Seqs(rt));

            DateTime fixedNow = new DateTime(2026, 1, 1, 12, 0, 10, DateTimeKind.Utc);
            MdDash.NowUtc = delegate { return fixedNow; };
            try
            {
                MdManifest dyn = MdDash.Parse(
                    "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"dynamic\" availabilityStartTime=\"2026-01-01T12:00:00Z\""
                    + " timeShiftBufferDepth=\"PT6S\" minimumUpdatePeriod=\"PT2S\"><Period id=\"0\" start=\"PT0S\"><AdaptationSet contentType=\"video\">"
                    + "<Representation id=\"0\" mimeType=\"video/mp4\" bandwidth=\"1000\" width=\"64\" height=\"36\">"
                    + "<SegmentTemplate timescale=\"1\" duration=\"2\" startNumber=\"1\" initialization=\"i.mp4\" media=\"s-$Number$.m4s\"/>"
                    + "</Representation></AdaptationSet></Period></MPD>", "http://127.0.0.1:1/t/m.mpd", out err);
                MdTrack dv = dyn.Variants.Count > 0 ? dyn.Variants[0].Main : null;
                T.Check("dash: a dynamic manifest is live and holds only the time-shift window",
                        dyn.Live && dv != null && dv.Live && dv.Segments.Count == 3 && Seqs(dv) == "3,4,5", err + " " + (dv == null ? "no video" : Seqs(dv)));
            }
            finally { MdDash.NowUtc = delegate { return DateTime.UtcNow; }; }

            MdManifest drm = MdDash.Parse(Mpd("static", "PT4S",
                "<ContentProtection schemeIdUri=\"urn:uuid:edef8ba9-79d6-4ace-a3c8-27dcd51d21ed\"/>"
                + "<SegmentTemplate timescale=\"1\" duration=\"1\" initialization=\"i.mp4\" media=\"s-$Number$.m4s\"/>"),
                "http://127.0.0.1:1/t/m.mpd", out err);
            T.Check("dash: ContentProtection refuses the manifest", drm.Refused.Length > 0, drm.Refused);

            MdManifest sb = MdDash.Parse(Mpd("static", "PT4S", "<BaseURL>one.mp4</BaseURL><SegmentBase indexRange=\"0-99\"><Initialization range=\"0-99\"/></SegmentBase>"),
                                         "http://127.0.0.1:1/t/m.mpd", out err);
            MdTrack sbt = sb.Variants.Count > 0 ? sb.Variants[0].Main : null;
            MdDash.SidxRef sidx = sbt == null ? null : MdDash.SidxOf(sbt);
            T.Check("dash: SegmentBase indexRange marks the track for a sidx fetch",
                    sidx != null && sbt.Segments.Count == 0, err + " " + (sidx == null ? "no sidx" : sbt.Segments.Count.ToString(CultureInfo.InvariantCulture)));
        }

        private static string Mpd(string type, string duration, string inner)
        {
            return "<?xml version=\"1.0\"?><MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"" + type + "\" mediaPresentationDuration=\"" + duration + "\">"
                   + "<Period id=\"0\" start=\"PT0S\"><AdaptationSet contentType=\"video\">"
                   + "<Representation id=\"0\" mimeType=\"video/mp4\" bandwidth=\"1000\" width=\"64\" height=\"36\">" + inner
                   + "</Representation></AdaptationSet></Period></MPD>";
        }

        // ------------------------------------------------------------------ //
        //  Сетевые мелочи без сервера
        // ------------------------------------------------------------------ //
        private static void FetchUnits()
        {
            string next;
            DlFailure f = MdFetcher.NextHop("https://a.example/x.m3u8", "http://a.example/seg0.ts", false, out next);
            T.Check("engine: a https to http redirect is refused", f != null && f.Kind == DlErrorKind.Policy && next == null, f == null ? next : f.Message);
            f = MdFetcher.NextHop("https://a.example/x.m3u8", "http://a.example/seg0.ts", true, out next);
            T.Eq("engine: the downgrade is allowed only when the item allows it", "http://a.example/seg0.ts", f == null ? next : "refused");
            f = MdFetcher.NextHop("http://a.example/x.m3u8", "seg0.ts", false, out next);
            T.Eq("engine: a relative Location resolves against the current url", "http://a.example/seg0.ts", f == null ? next : "refused: " + f.Message);
            f = MdFetcher.NextHop("http://a.example/x.m3u8", "ftp://a.example/seg0.ts", false, out next);
            T.Check("engine: a redirect to another scheme is refused", f != null && next == null, f == null ? next : f.Message);

            MdSegment s = new MdSegment();
            s.Sequence = 0x0102;
            T.Eq("engine: without a playlist IV the sequence is the IV, big-endian", "00000000000000000000000000000102", DlTestServer.Hex(DlMediaRun.IvFor(s)));
            s.Key = new MdKey();
            s.Key.Iv = new byte[16];
            s.Key.Iv[15] = 9;
            T.Eq("engine: the playlist IV wins over the sequence", "00000000000000000000000000000009", DlTestServer.Hex(DlMediaRun.IvFor(s)));
        }

        // ------------------------------------------------------------------ //
        //  Загрузка: HLS TS
        // ------------------------------------------------------------------ //
        private static void VodTs(DlTestServer srv)
        {
            string url = MdFx.Serve(srv, "hls-ts", "ts", "index.m3u8");
            byte[] expected = Cat(FixBytes("hls-ts\\seg0.ts"), FixBytes("hls-ts\\seg1.ts"), FixBytes("hls-ts\\seg2.ts"), FixBytes("hls-ts\\seg3.ts"));
            string folder = MdDir("ts");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Hls;
            md.ManifestUrl = url;
            using (DlEngine e = StartEngine(MdDir("ts-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, url, md, folder, "movie.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                DlItem it = e.Find(id);
                T.Check("engine: a VOD HLS stream downloads and assembles", done, MdInfo(e, id));
                T.Eq("engine: the file is the segments one after another", Sha(expected), ShaFile(it.TargetPath));
                T.Eq("engine: the counters end at all segments", "4/4", it.Media.SegmentsDone + "/" + it.Media.SegmentsTotal);
                T.Check("engine: the parts folder is gone after success", !Directory.Exists(folder + "\\movie.ts" + DlMediaRun.PartsSuffix)
                        && it.Media.PartsDir.Length > 0, it.Media.PartsDir);
                T.Check("engine: the item ends with the real size and no partial file", it.Total == expected.Length && !File.Exists(it.PartPath), it.Total.ToString(CultureInfo.InvariantCulture));
            }
        }

        // ---------- fMP4 с init и AES-128 (IV из плейлиста и из номера) ----------
        private static void Fmp4AndKeys(DlTestServer srv)
        {
            string url = MdFx.Serve(srv, "hls-fmp4", "fmp4", "index.m3u8");
            byte[] expected = Cat(FixBytes("hls-fmp4\\init.mp4"), FixBytes("hls-fmp4\\seg0.m4s"), FixBytes("hls-fmp4\\seg1.m4s"),
                                  FixBytes("hls-fmp4\\seg2.m4s"), FixBytes("hls-fmp4\\seg3.m4s"));
            string folder = MdDir("fmp4");
            DlMedia md = new DlMedia();
            md.Source = MdSource.Hls;
            using (DlEngine e = StartEngine(MdDir("fmp4-store"), MdSettings(folder)))
            {
                string id = AddMedia(e, url, md, folder, "f.mp4");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: fMP4 starts the file with the init segment", done, MdInfo(e, id));
                T.Eq("engine: init plus segments in order", Sha(expected), ShaFile(e.Find(id).TargetPath));
            }

            // Ключ из фикстуры, IV из плейлиста: расшифрованное должно совпасть с открытым потоком.
            string aesUrl = MdFx.Serve(srv, "hls-aes", "aes", "index.m3u8");
            byte[] plain = Cat(FixBytes("hls-ts\\seg0.ts"), FixBytes("hls-ts\\seg1.ts"), FixBytes("hls-ts\\seg2.ts"), FixBytes("hls-ts\\seg3.ts"));
            string aesFolder = MdDir("aes");
            using (DlEngine e = StartEngine(MdDir("aes-store"), MdSettings(aesFolder)))
            {
                string id = AddMedia(e, aesUrl, new DlMedia(), aesFolder, "a.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                byte[] got = File.Exists(e.Find(id).TargetPath) ? File.ReadAllBytes(e.Find(id).TargetPath) : new byte[0];
                bool sync = got.Length > 0 && got.Length % 188 == 0;
                for (int i = 0; i < got.Length && sync; i += 188) if (got[i] != 0x47) sync = false;
                T.Check("engine: AES-128 segments are decrypted into a valid TS stream", done && sync, MdInfo(e, id) + " len=" + got.Length);
                T.Eq("engine: the decrypted stream equals the plain fixture", Sha(plain), Sha(got));
            }

            // Тот же ключ, но IV не задан: он берётся из номера сегмента.
            byte[] key = FixBytes("hls-aes\\key.bin");
            byte[][] parts = new byte[][] { FixBytes("hls-ts\\seg0.ts"), FixBytes("hls-ts\\seg1.ts"), FixBytes("hls-ts\\seg2.ts") };
            StringBuilder pl = new StringBuilder();
            pl.Append("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:1\n#EXT-X-MEDIA-SEQUENCE:0\n#EXT-X-PLAYLIST-TYPE:VOD\n");
            pl.Append("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin\"\n");
            Bin(srv, "ivseq/key.bin", key, "application/octet-stream");
            for (int i = 0; i < parts.Length; i++)
            {
                byte[] iv = new byte[16];
                iv[15] = (byte)i;                       // номер сегмента big-endian, посчитан здесь независимо
                Bin(srv, "ivseq/seg" + i + ".ts", Aes128(parts[i], key, iv, true), "video/mp2t");
                pl.Append("#EXTINF:1.0,\nseg").Append(i.ToString(CultureInfo.InvariantCulture)).Append(".ts\n");
            }
            pl.Append("#EXT-X-ENDLIST\n");
            Txt(srv, "ivseq/index.m3u8", pl.ToString(), "application/vnd.apple.mpegurl");
            string ivFolder = MdDir("ivseq");
            using (DlEngine e = StartEngine(MdDir("ivseq-store"), MdSettings(ivFolder)))
            {
                string id = AddMedia(e, srv.Url("ivseq/index.m3u8"), new DlMedia(), ivFolder, "iv.ts");
                bool done = MdWait(delegate { return Done(e, id); }, 30000);
                T.Check("engine: a key without IV decrypts with the sequence number as IV", done, MdInfo(e, id));
                T.Eq("engine: the sequence-IV stream matches the plain segments", Sha(Cat(parts[0], parts[1], parts[2])), ShaFile(e.Find(id).TargetPath));
            }
        }

        private static byte[] Aes128(byte[] data, byte[] key, byte[] iv, bool encrypt)
        {
            using (Aes a = Aes.Create())
            {
                a.Mode = CipherMode.CBC;
                a.Padding = PaddingMode.PKCS7;
                a.Key = key;
                a.IV = iv;
                using (ICryptoTransform t = encrypt ? a.CreateEncryptor() : a.CreateDecryptor())
                    return t.TransformFinalBlock(data, 0, data.Length);
            }
        }

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
