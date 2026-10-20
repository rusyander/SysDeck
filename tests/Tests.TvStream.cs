// SysDeck — тесты «ТВ-трансляции»: границы папок, Range, разбор запроса, «только домашняя сеть», JSON, настройки,
// разбор ffprobe и аргументы ffmpeg; затем настоящий TvServer на loopback — сырые HTTP-запросы через сокет.
// Сборка: tests\run-tests.bat (область tvstream).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SysDeck.TvStream;

namespace SysDeck.Tests
{
    internal static class TvStreamTests
    {
        public static void Run()
        {
            bool wasEn = Tr.En;
            Tr.En = false;
            string root = Path.Combine(Path.GetTempPath(), "sysdeck-tv-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
                Bounds(root);
                Ranges();
                Requests();
                Local();
                JsonAndMsx();
                Settings(root);
                Probe();
                Server(root);
                TvSetupTests.Run(root);
            }
            finally
            {
                Tr.En = wasEn;
                try { Directory.Delete(root, true); } catch (Exception) { }
            }
        }

        private static void Bounds(string tmp)
        {
            string videos = Path.Combine(tmp, "Videos");
            string evil = Path.Combine(tmp, "VideosEvil");
            Directory.CreateDirectory(Path.Combine(videos, "Сериал"));
            Directory.CreateDirectory(evil);
            List<TvFolder> roots = new List<TvFolder> { new TvFolder(videos, "Видео") };

            T.Eq("tv bounds: root itself", videos, TvLibrary.Resolve(roots, 0, ""));
            T.Eq("tv bounds: nested with forward slashes", Path.Combine(videos, @"Сериал\s01.mkv"), TvLibrary.Resolve(roots, 0, "Сериал/s01.mkv"));
            T.Eq("tv bounds: leading slash is still inside", Path.Combine(videos, "a.mp4"), TvLibrary.Resolve(roots, 0, "/a.mp4"));
            T.Eq("tv bounds: parent escape refused", null, TvLibrary.Resolve(roots, 0, "../../Windows/win.ini"));
            T.Eq("tv bounds: escape after descent refused", null, TvLibrary.Resolve(roots, 0, "Сериал/../../x.mp4"));
            T.Eq("tv bounds: sibling with the same prefix refused", null, TvLibrary.Resolve(roots, 0, "../VideosEvil/a.mp4"));
            T.Eq("tv bounds: absolute drive path refused", null, TvLibrary.Resolve(roots, 0, @"C:\Windows\win.ini"));
            T.Eq("tv bounds: alternate data stream refused", null, TvLibrary.Resolve(roots, 0, "a.mp4:secret"));
            T.Eq("tv bounds: UNC-looking path stays inside", Path.Combine(videos, @"server\share\a.mp4"), TvLibrary.Resolve(roots, 0, @"\\server\share\a.mp4"));
            T.Eq("tv bounds: invalid characters refused", null, TvLibrary.Resolve(roots, 0, "a<b>.mp4"));
            T.Eq("tv bounds: unknown root", null, TvLibrary.Resolve(roots, 1, "a.mp4"));
            T.Eq("tv bounds: negative root", null, TvLibrary.Resolve(roots, -1, "a.mp4"));

            File.WriteAllText(Path.Combine(videos, "10 серия.mp4"), "x");
            File.WriteAllText(Path.Combine(videos, "2 серия.mkv"), "x");
            File.WriteAllText(Path.Combine(videos, "notes.txt"), "x");
            string hidden = Path.Combine(videos, "hidden.mp4");
            File.WriteAllText(hidden, "x");
            File.SetAttributes(hidden, FileAttributes.Hidden);
            List<string> dirs = new List<string>(), files = new List<string>();
            TvLibrary.List(videos, dirs, files);
            T.Eq("tv list: folders", "Сериал", string.Join("|", dirs));
            T.Eq("tv list: videos only, hidden skipped, natural order (2 before 10)", "2 серия.mkv|10 серия.mp4", string.Join("|", files));
            T.Eq("tv list: count", 2, TvLibrary.CountVideos(videos, 100));
            T.Eq("tv list: count stops at cap", 1, TvLibrary.CountVideos(videos, 1));

            T.Eq("tv url: segments escaped, slash kept", "%D0%A1/a%20b%23.mp4", TvLibrary.EncodeRel("С/a b#.mp4"));
            T.Eq("tv url: direct file → /media", "http://h/media/0/a.mp4", TvLibrary.PlayUrl("http://h", 0, "a.mp4"));
            T.Eq("tv url: mkv → /tx (ffmpeg)", "http://h/tx/1/d/a.mkv", TvLibrary.PlayUrl("http://h", 1, "d/a.mkv"));
            T.Eq("tv rel: parent of nested", "a/b", TvLibrary.ParentRel("a/b/c"));
            T.Eq("tv rel: parent of top", "", TvLibrary.ParentRel("a"));
        }

        private static void Ranges()
        {
            long s, e;
            T.Eq("tv range: none", TvHttp.RangeNone, TvHttp.ParseRange(null, 100, out s, out e));
            T.Eq("tv range: a-b", TvHttp.RangeOk + ":10-19", TvHttp.ParseRange("bytes=10-19", 100, out s, out e) + ":" + s + "-" + e);
            T.Eq("tv range: open end", TvHttp.RangeOk + ":90-99", TvHttp.ParseRange("bytes=90-", 100, out s, out e) + ":" + s + "-" + e);
            T.Eq("tv range: suffix", TvHttp.RangeOk + ":75-99", TvHttp.ParseRange("bytes=-25", 100, out s, out e) + ":" + s + "-" + e);
            T.Eq("tv range: suffix longer than file", TvHttp.RangeOk + ":0-99", TvHttp.ParseRange("bytes=-500", 100, out s, out e) + ":" + s + "-" + e);
            T.Eq("tv range: end clamped to size", TvHttp.RangeOk + ":50-99", TvHttp.ParseRange("bytes=50-1000", 100, out s, out e) + ":" + s + "-" + e);
            T.Eq("tv range: start past end", TvHttp.RangeBad, TvHttp.ParseRange("bytes=100-", 100, out s, out e));
            T.Eq("tv range: reversed", TvHttp.RangeBad, TvHttp.ParseRange("bytes=20-10", 100, out s, out e));
            T.Eq("tv range: garbage", TvHttp.RangeBad, TvHttp.ParseRange("bytes=abc", 100, out s, out e));
            T.Eq("tv range: negative number", TvHttp.RangeBad, TvHttp.ParseRange("bytes=-0", 100, out s, out e));
            T.Eq("tv range: multi-range → whole file", TvHttp.RangeNone, TvHttp.ParseRange("bytes=0-1,5-6", 100, out s, out e));
            T.Eq("tv range: other unit → whole file", TvHttp.RangeNone, TvHttp.ParseRange("items=0-1", 100, out s, out e));
        }

        private static void Requests()
        {
            TvRequest r = TvHttp.Parse("GET /media/0/%D0%A1%D0%B5%D1%80%D0%B8%D0%B0%D0%BB/a%20b.mp4?t=30&path=x%2Fy HTTP/1.1\r\nHost: 192.168.0.5:8090\r\nRange: bytes=0-\r\n\r\n");
            T.Eq("tv request: path decoded", "/media/0/Сериал/a b.mp4", r.Path);
            T.Eq("tv request: query", "30|x/y", r.Q("t") + "|" + r.Q("path"));
            T.Eq("tv request: headers case-insensitive", "bytes=0-", r.Header("range"));
            T.Eq("tv request: 1.1 keep-alive by default", true, r.KeepAlive);
            T.Eq("tv request: Connection close", false, TvHttp.Parse("GET / HTTP/1.1\r\nConnection: close\r\n\r\n").KeepAlive);
            T.Eq("tv request: 1.0 closes by default", false, TvHttp.Parse("GET / HTTP/1.0\r\n\r\n").KeepAlive);
            T.Eq("tv request: absolute-form target refused", null, TvHttp.Parse("GET http://evil/ HTTP/1.1\r\n\r\n"));
            T.Eq("tv request: broken line refused", null, TvHttp.Parse("GARBAGE\r\n\r\n"));
        }

        private static void Local()
        {
            T.Eq("tv lan: 192.168/16", true, TvNet.IsLocal(IPAddress.Parse("192.168.0.20")));
            T.Eq("tv lan: 10/8", true, TvNet.IsLocal(IPAddress.Parse("10.1.2.3")));
            T.Eq("tv lan: 172.16/12 inside", true, TvNet.IsLocal(IPAddress.Parse("172.31.255.1")));
            T.Eq("tv lan: 172.32 outside", false, TvNet.IsLocal(IPAddress.Parse("172.32.0.1")));
            T.Eq("tv lan: CGNAT (Tailscale)", true, TvNet.IsLocal(IPAddress.Parse("100.106.92.19")));
            T.Eq("tv lan: 100.128 public", false, TvNet.IsLocal(IPAddress.Parse("100.128.0.1")));
            T.Eq("tv lan: public v4", false, TvNet.IsLocal(IPAddress.Parse("8.8.8.8")));
            T.Eq("tv lan: loopback v6", true, TvNet.IsLocal(IPAddress.IPv6Loopback));
            T.Eq("tv lan: link-local v6", true, TvNet.IsLocal(IPAddress.Parse("fe80::1")));
            T.Eq("tv lan: ULA v6", true, TvNet.IsLocal(IPAddress.Parse("fd12::1")));
            T.Eq("tv lan: public v6", false, TvNet.IsLocal(IPAddress.Parse("2001:4860::8888")));
            T.Eq("tv lan: mapped public v4", false, TvNet.IsLocal(IPAddress.Parse("::ffff:8.8.8.8")));
            T.Eq("tv lan: mapped private v4", true, TvNet.IsLocal(IPAddress.Parse("::ffff:192.168.1.1")));
            T.Eq("tv device: Hisense", "Hisense (VIDAA)", TvNet.DeviceName("Mozilla/5.0 (Linux; VIDAA/6.0(Hisense;SmartTV;55A7)) Chrome/88"));
            T.Eq("tv device: Windows", "Windows", TvNet.DeviceName("Mozilla/5.0 (Windows NT 10.0; Win64; x64)"));
            T.Eq("tv device: empty", "?", TvNet.DeviceName(null));
        }

        private static void JsonAndMsx()
        {
            T.Eq("tv json: escapes", "\"a\\\"b\\\\c\\n\\u003c/script>\\u0001\"", TvJson.Str("a\"b\\c\n</script>\u0001"));
            T.Eq("tv json: null", "null", TvJson.Str(null));
            T.Eq("tv json: number invariant", "462.5", TvJson.Num(462.5));
            T.Eq("tv json: NaN → 0", "0", TvJson.Num(double.NaN));

            List<TvFolder> roots = new List<TvFolder> { new TvFolder(@"C:\V", "Кино") };
            T.Eq("tv msx: start points at menu", "{\"name\":\"PC Stream\",\"version\":\"1.0.0\",\"parameter\":\"menu:http://h:1/msx/menu.json\"}", TvMsx.Start("http://h:1"));
            T.Eq("tv msx: menu", "{\"headline\":\"PC Stream\",\"menu\":[{\"icon\":\"folder\",\"label\":\"Кино\",\"data\":\"http://h/msx/list.json?root=0&path=\"}]}", TvMsx.Menu("http://h", roots));
            string list = TvMsx.List("http://h", 0, "d", "d", new List<string> { "s 1" }, new List<string> { "a b.mp4", "c.mkv" });
            T.Check("tv msx: folder opens nested list", list.Contains("\"action\":\"content:http://h/msx/list.json?root=0&path=d%2Fs%201\""), list);
            T.Check("tv msx: mp4 plays directly", list.Contains("\"action\":\"video:http://h/media/0/d/a%20b.mp4\""), list);
            T.Check("tv msx: mkv plays through ffmpeg", list.Contains("\"action\":\"video:http://h/tx/0/d/c.mkv\""), list);
            T.Check("tv msx: title without extension", list.Contains("\"title\":\"a b\""), list);
            T.Check("tv msx: empty folder still has an item", TvMsx.List("http://h", 0, "", "x", new List<string>(), new List<string>()).Contains("\"enable\":false"));

            string api = TvApi.List(0, "", new List<string>(), new List<string> { "a.mkv" }, delegate(string rel) { return rel == "a.mkv" ? 61.25 : -1; });
            T.Eq("tv api: list", "{\"dirs\":[],\"files\":[{\"name\":\"a.mkv\",\"url\":\"/tx/0/a.mkv\",\"thumb\":\"/thumb/0/a.mkv\",\"transcode\":true,\"duration\":61.25}]}", api);
            T.Eq("tv api: info", "{\"ip\":\"1.2.3.4\",\"port\":8090,\"transcode\":false,\"roots\":[{\"id\":0,\"name\":\"Кино\"}]}", TvApi.Info("1.2.3.4", 8090, roots, false));
        }

        private static void Settings(string tmp)
        {
            TvSettings s = new TvSettings();
            s.Folders = new List<TvFolder> { new TvFolder(tmp + @"\A\", ""), new TvFolder(tmp + @"\a", "Другое"), null, new TvFolder("  ", "x"), new TvFolder("D:", "") };
            s.Port = 80;
            s.Normalize();
            T.Eq("tv settings: duplicates (case, trailing slash) and blanks dropped", 2, s.Folders.Count);
            T.Eq("tv settings: default name = folder name", "A", s.Folders[0].Name);
            T.Eq("tv settings: drive root keeps its slash", @"D:\", s.Folders[1].Path);
            T.Eq("tv settings: privileged port → default", TvSettings.DefaultPort, s.Port);
            T.Eq("tv settings: version 0 turns default-on flags on", "True|True", s.AutoStart + "|" + s.Thumbnails);
            s.AutoStart = false;
            s.Normalize();
            T.Eq("tv settings: user's off survives the next normalize", false, s.AutoStart);

            string file = Path.Combine(tmp, @"cfg\tvstream.json");
            s.Port = 9100;
            s.Save(file);
            TvSettings back = TvSettings.Load(file);
            T.Eq("tv settings: roundtrip", "9100|2|False|A", back.Port + "|" + back.Folders.Count + "|" + back.AutoStart + "|" + back.Folders[0].Name);
            s.Save(file);   // замена существующего файла (File.Replace)
            T.Eq("tv settings: no tmp left", false, File.Exists(file + ".tmp"));
            File.WriteAllText(file, "{broken");
            TvSettings bad = TvSettings.Load(file);
            T.Eq("tv settings: corrupt → defaults", TvSettings.DefaultPort + "|0", bad.Port + "|" + bad.Folders.Count);
            T.Eq("tv settings: corrupt file kept aside", true, File.Exists(file + ".corrupt"));
            T.Eq("tv settings: missing file → defaults", true, TvSettings.Load(Path.Combine(tmp, "none.json")).AutoStart);

            T.Eq("tv settings: guide not seen by default", false, back.GuideSeen);
            back.GuideSeen = true;
            back.Save(file);
            T.Eq("tv settings: guide seen survives a restart", true, TvSettings.Load(file).GuideSeen);

            TvSettings c = back.Clone();
            c.Folders[0].Name = "changed";
            T.Eq("tv settings: clone is deep", "A", back.Folders[0].Name);
        }

        private static void Probe()
        {
            TvProbe p = TvFfmpeg.ParseProbe("codec_name=hevc\r\nduration=1767.050000\r\n");
            T.Eq("tv probe: codec", "hevc", p.VideoCodec);
            T.Eq("tv probe: duration", 1767.05, p.Duration);
            TvProbe na = TvFfmpeg.ParseProbe("codec_name=N/A\nduration=N/A\n");
            T.Eq("tv probe: N/A → unknown", "|0", (na.VideoCodec ?? "") + "|" + na.Duration);
            T.Eq("tv probe: empty", "|0", (TvFfmpeg.ParseProbe(null).VideoCodec ?? "") + "|" + TvFfmpeg.ParseProbe(null).Duration);

            string h264 = TvFfmpeg.TranscodeArgs(@"D:\v\a b.mkv", 95.7, "h264");
            T.Check("tv ffmpeg: H.264 is copied", h264.Contains("-c:v copy") && !h264.Contains("libx264"), h264);
            T.Check("tv ffmpeg: seek before input, whole seconds", h264.Contains("-ss 95 -i \"D:\\v\\a b.mkv\""), h264);
            T.Check("tv ffmpeg: fragmented mp4 to stdout", h264.EndsWith("-movflags frag_keyframe+empty_moov+default_base_moof -f mp4 pipe:1"), h264);
            T.Check("tv ffmpeg: audio optional, AAC stereo", h264.Contains("-map 0:a:0? ") && h264.Contains("-c:a aac -b:a 192k -ac 2"), h264);
            string hevc = TvFfmpeg.TranscodeArgs(@"D:\a.mkv", 0, "hevc");
            T.Check("tv ffmpeg: HEVC re-encoded to 8-bit H.264", hevc.Contains("-c:v libx264") && hevc.Contains("-pix_fmt yuv420p"), hevc);
            T.Check("tv ffmpeg: no -ss at start", !hevc.Contains("-ss"), hevc);
            T.Check("tv ffmpeg: unknown codec re-encoded", TvFfmpeg.TranscodeArgs("a", 0, null).Contains("libx264"));
        }

        // ---------- настоящий сервер ----------
        private static void Server(string tmp)
        {
            string videos = Path.Combine(tmp, "Srv");
            Directory.CreateDirectory(Path.Combine(videos, "Папка"));
            byte[] data = new byte[300000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 + 3);
            File.WriteAllBytes(Path.Combine(videos, @"Папка\фильм 1.mp4"), data);
            File.WriteAllText(Path.Combine(tmp, "secret.mp4"), "SECRET");

            TvSettings set = TvSettings.Defaults();
            set.Folders.Add(new TvFolder(videos, "Видео"));
            set.Thumbnails = false;
            TvServer srv = new TvServer();
            srv.Settings = delegate { return set.Clone(); };
            srv.Ffmpeg = delegate { return new TvFfmpeg(null, null); };   // без ffmpeg: проверяем честный отказ /tx
            srv.ThumbDir = Path.Combine(tmp, "thumbs");
            srv.Resource = delegate(string name) { return name == "tvstream/index.html" ? Encoding.UTF8.GetBytes("<html>ui</html>") : null; };
            int port = FreePort();
            string err;
            T.Eq("tv server: starts", true, srv.Start(port, out err));
            TvServer twin = new TvServer();
            twin.Settings = srv.Settings; twin.Ffmpeg = srv.Ffmpeg; twin.Resource = srv.Resource;
            string twinErr;
            T.Eq("tv server: busy port refused with a message", false, twin.Start(port, out twinErr));
            T.Check("tv server: busy port message names the port", twinErr != null && twinErr.Contains(port.ToString()), twinErr);
            try
            {
                Resp ui = Get(port, "GET / HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                T.Eq("tv server: index served", "200|<html>ui</html>", ui.Code + "|" + ui.Text);

                Resp info = Get(port, "GET /api/info HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                T.Check("tv server: info lists the root", info.Code == 200 && info.Text.Contains("\"name\":\"Видео\"") && info.Text.Contains("\"transcode\":false"), info.Text);

                Resp list = Get(port, "GET /api/list?root=0&path=%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                T.Check("tv server: list of nested folder", list.Code == 200 && list.Text.Contains("\"name\":\"фильм 1.mp4\""), list.Text);

                Resp msx = Get(port, "GET /msx/list.json?root=0&path= HTTP/1.1\r\nHost: 192.168.0.9:" + port + "\r\nConnection: close\r\n\r\n");
                T.Check("tv server: msx urls use the Host the TV asked", msx.Text.Contains("content:http://192.168.0.9:" + port + "/msx/list.json?root=0&path=%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0"), msx.Text);
                T.Check("tv server: CORS for Media Station X", msx.Head.Contains("Access-Control-Allow-Origin: *"), msx.Head);

                string media = "/media/0/%D0%9F%D0%B0%D0%BF%D0%BA%D0%B0/%D1%84%D0%B8%D0%BB%D1%8C%D0%BC%201.mp4";
                Resp part = Get(port, "GET " + media + " HTTP/1.1\r\nHost: x\r\nRange: bytes=1000-1999\r\nConnection: close\r\n\r\n");
                bool same = part.Body.Length == 1000;
                for (int i = 0; same && i < 1000; i++) same = part.Body[i] == data[1000 + i];
                T.Check("tv server: range → 206 with exactly those bytes", part.Code == 206 && same && part.Head.Contains("Content-Range: bytes 1000-1999/300000"), part.Head);

                Resp full = Get(port, "GET " + media + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                T.Check("tv server: whole file", full.Code == 200 && full.Body.Length == data.Length && full.Head.Contains("Accept-Ranges: bytes"), full.Head);

                Resp bad = Get(port, "GET " + media + " HTTP/1.1\r\nHost: x\r\nRange: bytes=400000-\r\nConnection: close\r\n\r\n");
                T.Check("tv server: unsatisfiable range → 416", bad.Code == 416 && bad.Head.Contains("Content-Range: bytes */300000"), bad.Head);

                Resp head = Get(port, "HEAD " + media + " HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                T.Check("tv server: HEAD has length, no body", head.Code == 200 && head.Body.Length == 0 && head.Head.Contains("Content-Length: 300000"), head.Head);

                T.Eq("tv server: escape to a file outside → 404", 404, Get(port, "GET /media/0/..%2Fsecret.mp4 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);
                T.Eq("tv server: raw ../ → 404", 404, Get(port, "GET /media/0/../secret.mp4 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);
                T.Eq("tv server: unknown root → 404", 404, Get(port, "GET /media/7/a.mp4 HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);
                File.WriteAllText(Path.Combine(videos, "x.txt"), "x");
                T.Eq("tv server: non-video file → 404", 404, Get(port, "GET /media/0/x.txt HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);
                T.Eq("tv server: POST → 405", 405, Get(port, "POST / HTTP/1.1\r\nHost: x\r\nContent-Length: 0\r\nConnection: close\r\n\r\n").Code);
                T.Eq("tv server: missing mkv → 404", 404, Get(port, "GET /tx/0/a.mkv HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);
                File.WriteAllText(Path.Combine(videos, "b.mkv"), "x");
                T.Eq("tv server: transcode without ffmpeg → 501 (existing mkv)", 501, Get(port, "GET /tx/0/b.mkv HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n").Code);

                // keep-alive: два запроса по одному соединению
                using (TcpClient c = new TcpClient("127.0.0.1", port))
                {
                    NetworkStream ns = c.GetStream();
                    ns.ReadTimeout = 5000;
                    Resp a = Exchange(ns, "GET /api/info HTTP/1.1\r\nHost: x\r\n\r\n");
                    Resp b = Exchange(ns, "GET /api/info HTTP/1.1\r\nHost: x\r\nConnection: close\r\n\r\n");
                    T.Eq("tv server: keep-alive serves two requests on one socket", "200|200", a.Code + "|" + b.Code);
                }

                T.Eq("tv server: requests from this PC are not «the TV connected»", null, srv.LastVisit());
                string lan = TvNet.LanAddress();
                if (lan == "127.0.0.1") T.Skip("tv server: visit from another device recorded", "no LAN address on this machine");
                else
                {
                    // Адрес домашней сети, а не loopback: для сервера это «другое устройство», как телевизор.
                    using (TcpClient c = new TcpClient(lan, port))
                    {
                        NetworkStream ns = c.GetStream();
                        ns.ReadTimeout = 5000;
                        Resp start = Exchange(ns, "GET /msx/start.json HTTP/1.1\r\nHost: x\r\nUser-Agent: Mozilla/5.0 (VIDAA; Hisense)\r\nConnection: close\r\n\r\n");
                        T.Eq("tv server: msx start answered over the LAN address", 200, start.Code);
                    }
                    TvSession v = srv.LastVisit();
                    T.Check("tv server: Media Station X visit recorded with the device", v != null && v.Mode == "msx" && v.Device == "Hisense (VIDAA)" && v.Remote == lan,
                            v == null ? "null" : v.Mode + " " + v.Device + " " + v.Remote);
                }

                List<TvSession> ses = srv.Sessions();
                T.Check("tv server: viewer recorded", ses.Count == 1 && ses[0].Remote == "127.0.0.1" && ses[0].Mode == "direct" && ses[0].Active == 0,
                        ses.Count + (ses.Count > 0 ? " " + ses[0].Remote + " " + ses[0].Mode + " " + ses[0].Active : ""));
            }
            finally { srv.Stop(); }
            T.Eq("tv server: stopped", false, srv.Running);
            bool refused = false;
            try { using (TcpClient c = new TcpClient("127.0.0.1", port)) { } }
            catch (SocketException) { refused = true; }
            T.Eq("tv server: port released after stop", true, refused);
            T.Eq("tv server: viewers cleared after stop", 0, srv.Sessions().Count);
        }

        private sealed class Resp
        {
            public int Code;
            public string Head = "";
            public byte[] Body = new byte[0];
            public string Text { get { return Encoding.UTF8.GetString(Body); } }
        }

        private static int FreePort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static Resp Get(int port, string request)
        {
            using (TcpClient c = new TcpClient("127.0.0.1", port))
            {
                NetworkStream ns = c.GetStream();
                ns.ReadTimeout = 10000;
                return Exchange(ns, request);
            }
        }

        // Один ответ: заголовок, затем Content-Length байт (или до закрытия, если длины нет).
        private static Resp Exchange(NetworkStream ns, string request)
        {
            byte[] req = Encoding.UTF8.GetBytes(request);
            ns.Write(req, 0, req.Length);
            Resp r = new Resp();
            string head = TvHttp.ReadHead(ns);
            if (head == null) return r;
            r.Head = head;
            string[] first = head.Split(' ');
            int.TryParse(first.Length > 1 ? first[1] : "0", out r.Code);
            long len = -1;
            foreach (string line in head.Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) len = long.Parse(line.Substring(15).Trim());
            if (request.StartsWith("HEAD")) return r;
            MemoryStream ms = new MemoryStream();
            byte[] buf = new byte[65536];
            while (len < 0 || ms.Length < len)
            {
                int n;
                try { n = ns.Read(buf, 0, len < 0 ? buf.Length : (int)Math.Min(buf.Length, len - ms.Length)); }
                catch (IOException) { break; }
                if (n <= 0) break;
                ms.Write(buf, 0, n);
            }
            r.Body = ms.ToArray();
            return r;
        }
    }
}
