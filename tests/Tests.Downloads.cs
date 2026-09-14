// Windows Process Cleaner — область «downloads»: разбор заголовков, имена и пути, настройки и хранилище, настоящий движок
// против локального HTTP-сервера (сегменты, докачка, редиректы, повторы, лимиты, условия запуска), метка «из интернета»,
// канал процесса загрузок.
//
// Ненастоящая здесь только граница: сеть — HttpListener на 127.0.0.1 (localhost — «другой хост»), без прав
// администратора; часы, простой ПК, лимитная сеть и батарея — подменное окружение; Корзина — подменный делегат (прогон
// не кладёт в Корзину пользователя ничего). Качается только в папки фикстуры. Фоновый процесс не запускается, автозапуск
// не пишется.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    // Время идёт по-настоящему (повторы ждут секунды), но его можно сдвинуть вперёд.
    internal sealed class FakeDlEnv : IDlEnvironment
    {
        private long _offsetTicks;
        public volatile bool Metered, Battery, Fullscreen;
        public volatile int IdleSeconds = 100000, Cpu;

        public DateTime UtcNow { get { return DateTime.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks)); } }
        public DateTime LocalNow { get { return UtcNow.ToLocalTime(); } }
        public void Advance(TimeSpan d) { Interlocked.Add(ref _offsetTicks, d.Ticks); }
        public bool IsMetered() { return Metered; }
        public bool OnBattery() { return Battery; }
        public int InputIdleSeconds() { return IdleSeconds; }
        public int CpuPercent() { return Cpu; }
        public bool FullscreenBusy() { return Fullscreen; }
    }

    // ------------------------------------------------------------------ //
    //  Локальный HTTP-сервер со сценариями
    // ------------------------------------------------------------------ //
    internal sealed class DlTestServer : IDisposable
    {
        internal sealed class Res
        {
            public int Size;
            public int Seed;
            public bool Ranges = true;
            public string ETag = "\"v1\"";
            public bool NoLength;
            public int RateBps;
            public string Disposition;
            public int FailFirst;
            public int FailCode;
            public string RetryAfter;
            public int Status;
            public string RedirectTo;
            public bool ForbidResume;
            public string ContentType = "application/octet-stream";
            public string RequireCookie;          // без этой пары в Cookie — 403 (сессия на сайте истекла)
            public byte[] Body;                   // готовое содержимое вместо узора (Size = Body.Length)

            public int Requests, Concurrent, MaxConcurrent;
            public readonly List<string> RangesSeen = new List<string>();
            public readonly List<string> IfRanges = new List<string>();
            public readonly List<string> Cookies = new List<string>();
        }

        private readonly HttpListener _listener = new HttpListener();
        private readonly Dictionary<string, Res> _res = new Dictionary<string, Res>();
        private readonly Dictionary<string, int> _hostNow = new Dictionary<string, int>();
        private volatile bool _stop;
        public readonly object Gate = new object();
        public readonly int Port;
        public int ActiveKeys, MaxActiveKeys;
        public readonly Dictionary<string, int> HostMax = new Dictionary<string, int>();

        public DlTestServer()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Prefixes.Add("http://localhost:" + Port + "/");
            _listener.Start();
            Thread t = new Thread(Accept);
            t.IsBackground = true;
            t.Start();
        }

        public Res Add(string key, Res r) { lock (Gate) _res[key] = r; return r; }
        public string Url(string key) { return "http://127.0.0.1:" + Port + "/" + key; }
        public string UrlLocalhost(string key) { return "http://localhost:" + Port + "/" + key; }

        public static byte Pattern(int seed, long i) { return (byte)((i * 31 + seed * 7 + (i >> 11)) & 0xFF); }

        public static string Sha256Of(int seed, int size)
        {
            byte[] data = new byte[size];
            for (int i = 0; i < size; i++) data[i] = Pattern(seed, i);
            using (SHA256 h = SHA256.Create()) return Hex(h.ComputeHash(data));
        }

        public static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        private void Accept()
        {
            while (!_stop)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }
                HttpListenerContext c = ctx;
                Thread t = new Thread(delegate() { Handle(c); });
                t.IsBackground = true;
                t.Start();
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            HttpListenerResponse resp = ctx.Response;
            string key = ctx.Request.Url.AbsolutePath.TrimStart('/');
            string host = ctx.Request.Url.Host.ToLowerInvariant();
            Res r;
            lock (Gate) _res.TryGetValue(key, out r);
            if (r == null)
            {
                resp.StatusCode = 404;
                try { resp.Close(); } catch { }
                return;
            }
            lock (Gate)
            {
                r.Requests++;
                r.Concurrent++;
                if (r.Concurrent > r.MaxConcurrent) r.MaxConcurrent = r.Concurrent;
                if (r.Concurrent == 1) { ActiveKeys++; if (ActiveKeys > MaxActiveKeys) MaxActiveKeys = ActiveKeys; }
                r.RangesSeen.Add(ctx.Request.Headers["Range"] ?? "");
                r.IfRanges.Add(ctx.Request.Headers["If-Range"] ?? "");
                r.Cookies.Add(ctx.Request.Headers["Cookie"] ?? "");
                int n;
                _hostNow.TryGetValue(host, out n);
                _hostNow[host] = n + 1;
                int m;
                HostMax.TryGetValue(host, out m);
                if (n + 1 > m) HostMax[host] = n + 1;
            }
            try
            {
                if (r.RedirectTo != null)
                {
                    resp.StatusCode = 302;
                    resp.AddHeader("Location", r.RedirectTo);
                    return;
                }
                if (r.Status != 0) { resp.StatusCode = r.Status; return; }
                if (r.RequireCookie != null && (ctx.Request.Headers["Cookie"] ?? "").IndexOf(r.RequireCookie, StringComparison.Ordinal) < 0)
                {
                    resp.StatusCode = 403;
                    return;
                }
                if (r.FailFirst > 0)
                {
                    lock (Gate) r.FailFirst--;
                    resp.StatusCode = r.FailCode;
                    if (r.RetryAfter != null) resp.AddHeader("Retry-After", r.RetryAfter);
                    return;
                }
                long from = 0, to = r.Size - 1;
                bool partial = false;
                string range = ctx.Request.Headers["Range"];
                if (r.Ranges && !string.IsNullOrEmpty(range) && range.StartsWith("bytes="))
                {
                    string spec = range.Substring(6);
                    int dash = spec.IndexOf('-');
                    long a = long.Parse(spec.Substring(0, dash));
                    long b = dash + 1 < spec.Length ? long.Parse(spec.Substring(dash + 1)) : r.Size - 1;
                    if (r.ForbidResume && a > 0) { resp.StatusCode = 403; return; }
                    string ifRange = ctx.Request.Headers["If-Range"];
                    if (ifRange == null || ifRange == r.ETag)
                    {
                        partial = true;
                        from = a;
                        to = Math.Min(b, r.Size - 1);
                    }
                }
                resp.AddHeader("ETag", r.ETag);
                if (r.Ranges) resp.AddHeader("Accept-Ranges", "bytes");
                if (r.Disposition != null) resp.AddHeader("Content-Disposition", r.Disposition);
                resp.ContentType = r.ContentType;
                resp.StatusCode = partial ? 206 : 200;
                if (partial) resp.AddHeader("Content-Range", "bytes " + from + "-" + to + "/" + r.Size);
                long len = to - from + 1;
                if (r.NoLength) resp.SendChunked = true;
                else resp.ContentLength64 = len;
                Stream o = resp.OutputStream;
                byte[] buf = new byte[16 * 1024];
                Stopwatch w = Stopwatch.StartNew();
                long sent = 0;
                while (sent < len && !_stop)
                {
                    int chunk = (int)Math.Min(buf.Length, len - sent);
                    if (r.Body != null) Array.Copy(r.Body, from + sent, buf, 0, chunk);
                    else for (int i = 0; i < chunk; i++) buf[i] = Pattern(r.Seed, from + sent + i);
                    o.Write(buf, 0, chunk);
                    sent += chunk;
                    if (r.RateBps > 0)
                    {
                        long due = sent * 1000 / r.RateBps;
                        long wait = due - w.ElapsedMilliseconds;
                        if (wait > 0) Thread.Sleep((int)wait);
                    }
                }
                o.Close();
            }
            catch { }
            finally
            {
                lock (Gate)
                {
                    r.Concurrent--;
                    if (r.Concurrent == 0) ActiveKeys--;
                    _hostNow[host] = _hostNow[host] - 1;
                }
                try { resp.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
        }
    }

    // ------------------------------------------------------------------ //
    //  Проверки
    // ------------------------------------------------------------------ //
    internal static partial class DownloadsTests
    {
        private static int _n;

        internal static void Run()
        {
            Func<string, string> savedRecycler = DlFiles.Recycler;
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); return null; };
            try
            {
                Parsers();
                Names();
                Folders();
                SettingsAndItems();
                Store();
                Motw();
                PageView();
                using (DlTestServer srv = new DlTestServer())
                {
                    Segmented(srv);
                    SingleStream(srv);
                    UnknownLength(srv);
                    ResumeAfterRestart(srv);
                    ChangedOnServer(srv);
                    PauseResume(srv);
                    Redirects(srv);
                    RetryAndNotRetry(srv);
                    LinkExpiredAndRefresh(srv);
                    MirrorAndHash(srv);
                    Collision(srv);
                    Throttle(srv);
                    Concurrency(srv);
                    Gates(srv);
                    RemoveAndDuplicates(srv, recycled);
                    PageStage(srv, recycled);
                    Pipe(srv);
                    Bridge(srv, recycled);
                }
                BrowsersRegistry();
                ExtensionUnpack();
            }
            finally
            {
                DlFiles.Recycler = savedRecycler;
            }
        }

        // ---------- помощники ----------
        private static bool WaitFor(Func<bool> cond, int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (cond()) return true;
                Thread.Sleep(40);
            }
            return cond();
        }

        private static DlSettings NewSettings(string folder)
        {
            DlSettings s = new DlSettings();
            s.Folder = folder;
            s.MarkOfTheWeb = false;
            s.PreventSleep = false;
            s.SmallFileMB = 0;
            s.PauseOnMetered = true;
            s.PauseOnBattery = true;
            return s;
        }

        private static DlEngine NewEngine(string storeDir, DlSettings s, FakeDlEnv env)
        {
            DlEngine e = new DlEngine(new DlStore(storeDir), s, env);
            e.Start();
            return e;
        }

        private static string Dir(string name) { return Fx.MakeDir(Fx.Root, "dl-" + name + "-" + (++_n)); }

        private static string Add(DlEngine e, string url)
        {
            DlAddRequest r = new DlAddRequest();
            r.Url = url;
            string dup, err;
            string id = e.Add(r, out dup, out err);
            if (id == null) T.Check("add " + url, false, err);
            return id;
        }

        private static DlState StateOf(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            return it == null ? (DlState)(-1) : it.State;
        }

        private static string FileHash(string path)
        {
            try
            {
                using (SHA256 h = SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                    return DlTestServer.Hex(h.ComputeHash(fs));
            }
            catch { return "<unreadable>"; }
        }

        private static string Info(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            return it == null ? "no item" : it.State + " " + it.ErrorKind + " " + it.Error + " done=" + it.DoneBytes + "/" + it.Total + " wait=" + it.WaitReason;
        }

        // ---------- разбор заголовков ----------
        private static void Parsers()
        {
            T.Eq("RFC 5987 filename* wins over filename", "отчёт.pdf",
                 DlHttp.ContentDispositionName("attachment; filename*=UTF-8''%D0%BE%D1%82%D1%87%D1%91%D1%82.pdf; filename=\"fallback.pdf\""));
            T.Eq("quoted filename with escaped quotes", "with \"quote\".txt", DlHttp.ContentDispositionName("attachment; filename=\"with \\\"quote\\\".txt\""));
            T.Eq("bare filename token", "bare.zip", DlHttp.ContentDispositionName("attachment; filename=bare.zip"));
            byte[] utf8 = Encoding.UTF8.GetBytes("файл.zip");
            StringBuilder mangled = new StringBuilder();
            foreach (byte b in utf8) mangled.Append((char)b);
            T.Eq("raw UTF-8 read as Latin-1 is re-decoded", "файл.zip", DlHttp.ContentDispositionName("attachment; filename=\"" + mangled + "\""));
            T.Eq("percent-encoded plain filename is decoded", "ф.txt", DlHttp.ContentDispositionName("inline; filename=\"%D1%84.txt\""));
            T.Eq("filename* in ISO-8859-1", "£ rates.txt", DlHttp.ContentDispositionName("attachment; filename*=iso-8859-1'en'%A3%20rates.txt"));
            T.Eq("no filename at all", "", DlHttp.ContentDispositionName("attachment"));

            long s, e, t;
            T.Check("Content-Range bytes 0-99/1234", DlHttp.TryParseContentRange("bytes 0-99/1234", out s, out e, out t) && s == 0 && e == 99 && t == 1234);
            T.Check("Content-Range with unknown total", DlHttp.TryParseContentRange("bytes 0-9/*", out s, out e, out t) && t == -1);
            T.Check("Content-Range end before start is rejected", !DlHttp.TryParseContentRange("bytes 5-4/10", out s, out e, out t));
            T.Check("unsatisfied Content-Range is rejected", !DlHttp.TryParseContentRange("bytes */1234", out s, out e, out t));
            T.Check("Content-Range end past total is rejected", !DlHttp.TryParseContentRange("bytes 0-10/10", out s, out e, out t));

            DateTime now = DateTime.UtcNow;
            T.Eq("Retry-After in seconds", 7, DlHttp.RetryAfterSeconds("7", now));
            int dateWait = DlHttp.RetryAfterSeconds(now.AddSeconds(30).ToString("R"), now);
            T.Check("Retry-After as an HTTP date", dateWait >= 28 && dateWait <= 30, dateWait.ToString());
            T.Eq("unreadable Retry-After", 0, DlHttp.RetryAfterSeconds("soon", now));

            T.Check("503 is retried after the server pause", DlHttp.Classify(503, false, "5", now).Kind == DlErrorKind.RateLimited && DlHttp.Classify(503, false, "5", now).RetryAfterSeconds == 5);
            T.Check("500 is a retryable server error", DlHttp.Classify(500, false, null, now).Retryable);
            T.Check("404 on a fresh download is final", DlHttp.Classify(404, false, null, now).Kind == DlErrorKind.Client && !DlHttp.Classify(404, false, null, now).Retryable);
            T.Eq("404 while resuming means the link expired", DlErrorKind.LinkExpired, DlHttp.Classify(404, true, null, now).Kind);
            T.Eq("403 while resuming means the link expired", DlErrorKind.LinkExpired, DlHttp.Classify(403, true, null, now).Kind);
            T.Eq("416 while resuming means the file changed", DlErrorKind.Changed, DlHttp.Classify(416, true, null, now).Kind);
            T.Eq("408 is a network error", DlErrorKind.Network, DlHttp.Classify(408, false, null, now).Kind);

            DlItem it = new DlItem();
            it.ETag = "W/\"weak\"";
            it.LastModified = "Sun, 13 Sep 2026 10:00:00 GMT";
            T.Eq("a weak ETag is not used for If-Range", it.LastModified, DlHttp.IfRangeValue(it));
            it.ETag = "\"strong\"";
            T.Eq("a strong ETag is used for If-Range", "\"strong\"", DlHttp.IfRangeValue(it));

            T.Check("https → http redirect needs consent", DlHttp.CheckRedirect("https://a.example/x", "http://b.example/y", false) != null
                    && DlHttp.CheckRedirect("https://a.example/x", "http://b.example/y", false).Kind == DlErrorKind.Policy);
            T.Check("https → http redirect with consent", DlHttp.CheckRedirect("https://a.example/x", "http://b.example/y", true) == null);
            T.Check("http → https redirect is fine", DlHttp.CheckRedirect("http://a.example/x", "https://a.example/y", false) == null);
            T.Check("redirect to file: is refused", DlHttp.CheckRedirect("http://a.example/x", "file:///C:/Windows/win.ini", true) != null);
            T.Check("redirect to ftp: is refused", DlHttp.CheckRedirect("http://a.example/x", "ftp://a.example/y", true) != null);

            foreach (string bad in new[] { "file:///C:/x.txt", "ftp://example.com/x", "javascript:alert(1)", "data:text/plain,x", "\\\\server\\share\\x", "chrome://settings", "" })
                T.Check("scheme refused: «" + bad + "»", !DlHttp.IsAllowedScheme(bad));
            T.Check("https link is allowed", DlHttp.IsAllowedScheme("https://example.com/a.zip"));

            string algo, hex;
            T.Check("expected sha256 with prefix", DlFinish.ParseExpected("SHA-256:" + new string('A', 64), out algo, out hex) && algo == "sha256");
            T.Check("bare 40-hex is sha1", DlFinish.ParseExpected(new string('0', 40), out algo, out hex) && algo == "sha1");
            T.Check("short hex is refused", !DlFinish.ParseExpected("sha256:abc", out algo, out hex));
            T.Check("non-hex is refused", !DlFinish.ParseExpected("md5:" + new string('z', 32), out algo, out hex));

            T.Eq("log redaction drops the query", "https://a.example/b?…", DlLog.Redact("https://a.example/b?token=secret"));
            T.Eq("url key ignores the fragment and host case", DlEngine.UrlKey("http://a.example/x?q=1"), DlEngine.UrlKey("HTTP://A.EXAMPLE/x?q=1#frag"));
            T.Eq("MOTW source without the query", "https://a.example/f.zip", DlMotw.StripQuery("https://a.example/f.zip?sig=1#x"));
            T.Check("Defender scans exactly the one file", DlMotw.DefenderArguments(@"C:\d\a b.zip") == "-Scan -ScanType 3 -File \"C:\\d\\a b.zip\"");
            T.Eq("autostart command line", "\"C:\\x\\app.exe\" --downloads", DlLauncher.RunCommand(@"C:\x\app.exe"));
            int code;
            T.Check("no downloads switch — the window starts as usual", !DlMode.TryRun(new[] { "/tray" }, out code));
        }

        // ---------- имена ----------
        private static void Names()
        {
            T.Eq("a path in the name keeps only the last element", "evil.exe", DlFiles.SanitizeName("..\\..\\evil.exe"));
            T.Eq("forward-slash path too", "x.zip", DlFiles.SanitizeName("a/b/../x.zip"));
            T.Eq("reserved CON.txt is prefixed", "_CON.txt", DlFiles.SanitizeName("CON.txt"));
            T.Eq("reserved lpt1.tar.gz is prefixed", "_lpt1.tar.gz", DlFiles.SanitizeName("lpt1.tar.gz"));
            T.Eq("colon (ADS) is replaced", "a_b.txt", DlFiles.SanitizeName("a:b.txt"));
            string spoof = DlFiles.SanitizeName("photo\u202Egpj.exe");
            T.Eq("bidi override is removed", "photogpj.exe", spoof);
            T.Check("the unmasked name is dangerous", DlFiles.IsDangerous(spoof));
            T.Eq("trailing dots and spaces are cut", "report.pdf", DlFiles.SanitizeName("report.pdf. . ."));
            T.Eq("empty name becomes «download»", "download", DlFiles.SanitizeName(""));
            T.Eq("dots only become «download»", "download", DlFiles.SanitizeName("..."));
            T.Eq("control characters are removed", "ab.txt", DlFiles.SanitizeName("a\tb\u0001.txt"));
            string longName = DlFiles.SanitizeName(new string('я', 300) + ".zip");
            T.Check("a long name is cut to 200 and keeps the extension", longName.Length <= DlFiles.MaxNameLength && longName.EndsWith(".zip"), longName.Length.ToString());
            T.Check("an archive is not dangerous", !DlFiles.IsDangerous("a.zip"));
            T.Check("a shortcut is dangerous", DlFiles.IsDangerous("a.LNK"));
            T.Eq("name from the URL is decoded", "мой файл.iso", DlFiles.NameFromUrl("https://a.example/dl/%D0%BC%D0%BE%D0%B9%20%D1%84%D0%B0%D0%B9%D0%BB.iso?x=1"));
            T.Eq("extension from Content-Type", ".zip", DlFiles.ExtensionForMime("application/zip; charset=binary"));

            string dir = Dir("names");
            T.Check("a clean name stays inside the folder", (DlFiles.PathInside(dir, "ok.bin") ?? "").StartsWith(dir.TrimEnd('\\') + "\\"));
            T.Check("«..» is not a file name inside the folder", DlFiles.PathInside(dir, "..") == null);
            T.Check("an unsanitised name is refused", DlFiles.PathInside(dir, "..\\x.bin") == null);

            Fx.MakeFile(Path.Combine(dir, "same.bin"), 10);
            Fx.MakeFile(Path.Combine(dir, "same (1).bin" + DlPaths.PartSuffix), 10);
            T.Eq("existing file and partial file are skipped", "same (2).bin", DlFiles.UniqueName(dir, "same.bin", null));
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.Combine(dir, "same (2).bin").ToLowerInvariant() };
            T.Eq("a name taken by another download is skipped", "same (3).bin", DlFiles.UniqueName(dir, "same.bin", taken));
            Fx.MakeFile(Path.Combine(dir, "a.tar.gz"), 1);
            T.Eq("double extension keeps together", "a (1).tar.gz", DlFiles.UniqueName(dir, "a.tar.gz", null));
        }

        // ---------- папки ----------
        private static void Folders()
        {
            string why;
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            T.Check("the Windows folder is refused", DlFiles.CheckFolder(Path.Combine(win, "Temp"), out why) == null, why);
            T.Check("Program Files is refused", DlFiles.CheckFolder(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), out why) == null);
            T.Check("a relative folder is refused", DlFiles.CheckFolder("relative\\dir", out why) == null);
            T.Check("a drive-relative folder is refused", DlFiles.CheckFolder("C:dir", out why) == null);
            T.Check("a device path is refused", DlFiles.CheckFolder("\\\\?\\C:\\x", out why) == null);
            T.Check("the app data folder is refused", DlFiles.CheckFolder(Path.Combine(Engine.DefaultDataDir(), "sub"), out why) == null);
            string ok = Dir("folder");
            T.Check("a folder in the fixture is accepted", DlFiles.CheckFolder(ok, out why) != null, why);

            string link = Path.Combine(Fx.Root, "dl-junction-win");
            if (Fx.Junction(link, win))
                T.Check("a junction into Windows is refused (target is checked, not the name)", DlFiles.CheckFolder(link, out why) == null, why);
            else
                T.Skip("a junction into Windows is refused", "mklink /J failed");
            string real = Dir("junction-target");
            string link2 = Path.Combine(Fx.Root, "dl-junction-ok");
            if (Fx.Junction(link2, real))
                T.Eq("a junction to a normal folder resolves to its target", Native.CanonicalPath(real), DlFiles.CheckFolder(link2, out why));
            else
                T.Skip("a junction to a normal folder resolves", "mklink /J failed");

            T.Check("host pattern *.example.com covers subdomains", DlFiles.HostMatches("cdn.example.com", "*.example.com") && DlFiles.HostMatches("example.com", "*.example.com"));
            T.Check("plain host pattern is exact", !DlFiles.HostMatches("cdn.example.com", "example.com"));
            DlSettings s = new DlSettings();
            s.Folder = ok;
            DlFolderRule site = new DlFolderRule(); site.Kind = "site"; site.Pattern = "*.github.com"; site.Folder = @"D:\gh"; s.Rules.Add(site);
            DlFolderRule ext = new DlFolderRule(); ext.Kind = "ext"; ext.Pattern = "iso, img"; ext.Folder = @"D:\images"; s.Rules.Add(ext);
            T.Eq("site rule wins over type rule", @"D:\gh", DlFiles.FolderFor(s, "https://objects.github.com/x.iso", "x.iso"));
            T.Eq("type rule by extension", @"D:\images", DlFiles.FolderFor(s, "https://a.example/x.IMG", "x.IMG"));
            T.Eq("no rule — the default folder", ok, DlFiles.FolderFor(s, "https://a.example/x.zip", "x.zip"));
        }

        // ---------- настройки и модель ----------
        private static void SettingsAndItems()
        {
            T.Check("window across midnight contains 00:30", DlSettings.InWindow(30, 23 * 60, 60));
            T.Check("window across midnight excludes 10:00", !DlSettings.InWindow(600, 23 * 60, 60));
            T.Check("from == to is the whole day", DlSettings.InWindow(777, 100, 100));
            DlSettings s = new DlSettings();
            T.Eq("default segments", 4, s.Segments);
            T.Eq("default max active", 3, s.MaxActive);
            T.Eq("default per host", 2, s.MaxPerHost);
            T.Eq("default idle minutes", 5, s.IdleMinutes);
            T.Eq("default idle CPU", 30, s.IdleCpuPercent);
            JVal j = s.ToJson();
            j.Set("Segments", JVal.NewNum("99"));
            j.Set("MaxActive", JVal.NewNum("-5"));
            DlSettings c = DlSettings.FromJson(j);
            T.Eq("segments clamp to 16", 16, c.Segments);
            T.Eq("max active clamps to 1", 1, c.MaxActive);
            s.Mode = DlSpeedMode.Quiet;
            s.QuietKBps = 100;
            T.Eq("quiet mode limit", 100L * 1024, s.EffectiveLimitBytes(new DateTime(2026, 9, 13, 12, 0, 0)));
            s.Mode = DlSpeedMode.Normal;
            s.LimitKBps = 500;
            s.NightEnabled = true; s.NightFrom = 60; s.NightTo = 7 * 60; s.NightKBps = 0;
            T.Eq("night has its own limit", 0L, s.EffectiveLimitBytes(new DateTime(2026, 9, 13, 3, 0, 0)));
            T.Eq("day uses the normal limit", 500L * 1024, s.EffectiveLimitBytes(new DateTime(2026, 9, 13, 12, 0, 0)));
            s.ScheduleEnabled = true; s.ScheduleDays = 1 << 6; s.ScheduleFrom = 0; s.ScheduleTo = 0;
            T.Check("schedule: Sunday only allows Sunday", s.ScheduleAllows(new DateTime(2026, 9, 13, 12, 0, 0)) && !s.ScheduleAllows(new DateTime(2026, 9, 14, 12, 0, 0)));

            DlItem it = new DlItem();
            it.Id = DlItem.NewId();
            it.Url = "https://a.example/f.bin";
            it.Cookies = "sid=TOPSECRET";
            DlSegment seg = new DlSegment();
            seg.Start = 0; seg.End = 9999; seg.Done = 5000; seg.Durable = 4096;
            it.Segments.Add(seg);
            string disk = Jsn.Write(it.ToJson(true));
            T.Check("cookies never reach the stored JSON", disk.IndexOf("TOPSECRET", StringComparison.Ordinal) < 0);
            DlItem back = DlItem.FromJson(Jsn.Parse(disk));
            T.Check("the stored offset is the durable one, not the live one", back != null && back.Segments.Count == 1 && back.Segments[0].Done == 4096);
            T.Check("id with a path is invalid", !DlItem.IsValidId("..\\x") && !DlItem.IsValidId("a b") && DlItem.IsValidId(it.Id));
        }

        // ---------- хранилище ----------
        private static void Store()
        {
            string dir = Dir("store");
            DlStore store = new DlStore(dir);
            DlItem a = new DlItem();
            a.Id = "a-1";
            a.Url = "https://a.example/1";
            a.State = DlState.Paused;
            store.Save(a);
            a.State = DlState.Queued;
            store.Save(a);
            store.SaveOrder(new[] { "a-1" });
            File.WriteAllText(Path.Combine(Path.Combine(dir, "items"), "a-1.json"), "{\"Id\":\"a-1\",\"Url\":");
            File.WriteAllText(Path.Combine(Path.Combine(dir, "items"), "a-1.json.tmp"), "{\"Id\":\"a-1\",\"Url\":\"https://x\",\"St");
            DlItem b = new DlItem();
            b.Id = "b-2";
            b.Url = "https://a.example/2";
            store.Save(b);
            File.WriteAllText(Path.Combine(Path.Combine(dir, "items"), "bad id.json"), "{\"Id\":\"bad id\"}");
            List<DlItem> loaded = new DlStore(dir).LoadAll();
            DlItem la = loaded.Find(delegate(DlItem x) { return x.Id == "a-1"; });
            T.Check("a torn main file falls back to .bak", la != null && la.Url == "https://a.example/1" && la.State == DlState.Paused, la == null ? "missing" : la.State.ToString());
            T.Check("an item missing from the index is still loaded", loaded.Exists(delegate(DlItem x) { return x.Id == "b-2"; }));
            T.Eq("files with invalid ids are ignored", 2, loaded.Count);
            T.Eq("index order comes first", "a-1", loaded.Count > 0 ? loaded[0].Id : "");
        }

        // ---------- метка «из интернета» ----------
        private static void Motw()
        {
            string dir = Dir("motw");
            string file = Fx.MakeFile(Path.Combine(dir, "sample.zip"), 1024);
            string why = DlMotw.Apply(file, "https://example.com/dl/sample.zip?token=secret123", "https://example.com/page?session=abc");
            string zone = DlMotw.ReadZoneStream(file) ?? "";
            T.Check("IAttachmentExecute marks the file as Internet zone", why == null && zone.Contains("ZoneId=3"), (why ?? "") + " | " + zone.Replace("\r\n", " "));
            T.Check("no query-string secrets in Zone.Identifier", zone.IndexOf("secret123", StringComparison.Ordinal) < 0 && zone.IndexOf("session=abc", StringComparison.Ordinal) < 0, zone);
            string file2 = Fx.MakeFile(Path.Combine(dir, "fallback.bin"), 10);
            T.Check("manual Zone.Identifier fallback writes zone 3",
                    DlMotw.WriteZoneStream(file2, "https://e.example/f?sig=1", "") == null && (DlMotw.ReadZoneStream(file2) ?? "").Contains("ZoneId=3")
                    && !(DlMotw.ReadZoneStream(file2) ?? "").Contains("sig=1"));
        }

        // ---------- движок против сервера ----------
        private static void Segmented(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("seg", new DlTestServer.Res());
            r.Size = 5 * 1024 * 1024 + 123;
            r.Seed = 1;
            r.Disposition = "attachment; filename*=UTF-8''%D0%BE%D1%82%D1%87%D1%91%D1%82.bin";
            string folder = Dir("seg");
            FakeDlEnv env = new FakeDlEnv();
            using (DlEngine e = NewEngine(Dir("seg-store"), NewSettings(folder), env))
            {
                string id = Add(e, srv.Url("seg"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 20000);
                DlItem it = e.Find(id);
                T.Check("206 download with 4 segments completes", done, Info(e, id));
                T.Eq("name from filename*", "отчёт.bin", it.FileName);
                T.Eq("assembled file matches the source SHA-256", DlTestServer.Sha256Of(1, r.Size), FileHash(Path.Combine(folder, "отчёт.bin")));
                T.Eq("engine SHA-256 equals the independent one", DlTestServer.Sha256Of(1, r.Size), it.Sha256);
                T.Eq("four segments were planned", 4, it.Segments.Count);
                T.Check("the server saw parallel connections", r.MaxConcurrent >= 2, r.MaxConcurrent.ToString());
                T.Check("no partial file is left", !File.Exists(Path.Combine(folder, "отчёт.bin" + DlPaths.PartSuffix)));
                T.Check("first request asked for bytes=0-", r.RangesSeen.Count > 0 && r.RangesSeen[0] == "bytes=0-");
            }
        }

        private static void SingleStream(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("single", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 2;
            r.Ranges = false;
            string folder = Dir("single");
            using (DlEngine e = NewEngine(Dir("single-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("single"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                DlItem it = e.Find(id);
                T.Check("200 without ranges falls back to one stream", done && !it.AcceptRanges && it.Segments.Count == 1, Info(e, id));
                T.Eq("single stream: one connection only", 1, r.MaxConcurrent);
                T.Eq("single stream: content is intact", DlTestServer.Sha256Of(2, r.Size), FileHash(it.TargetPath));
                T.Eq("name from the URL path", "single", it.FileName);
            }
        }

        private static void UnknownLength(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("chunked.dat", new DlTestServer.Res());
            r.Size = 700 * 1024 + 5;
            r.Seed = 3;
            r.Ranges = false;
            r.NoLength = true;
            string folder = Dir("chunked");
            using (DlEngine e = NewEngine(Dir("chunked-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("chunked.dat"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                DlItem it = e.Find(id);
                T.Check("chunked response of unknown size completes", done, Info(e, id));
                T.Eq("size is learned from the end of the stream", (long)r.Size, it.Total);
                T.Eq("chunked: content is intact", DlTestServer.Sha256Of(3, r.Size), FileHash(it.TargetPath));
            }
        }

        private static void ResumeAfterRestart(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("resume.bin", new DlTestServer.Res());
            r.Size = 3 * 1024 * 1024;
            r.Seed = 4;
            r.RateBps = 700 * 1024;
            string folder = Dir("resume");
            string store = Dir("resume-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 2;
            string id;
            long doneBefore;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("resume.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 600 * 1024; }, 10000);
            }
            DlItem saved = new DlStore(store).LoadAll().Find(delegate(DlItem x) { return x.Id == id; });
            doneBefore = saved == null ? -1 : saved.DoneBytes;
            T.Check("stopping the engine keeps the partial download", saved != null && doneBefore > 0 && doneBefore < r.Size && saved.State == DlState.Queued,
                    saved == null ? "no item" : saved.State + " " + doneBefore);
            T.Check("the partial file is on disk", File.Exists(Path.Combine(folder, "resume.bin" + DlPaths.PartSuffix)));
            int requestsBefore = r.Requests;
            r.RateBps = 0;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                T.Check("a new engine resumes the download by itself", done, Info(e, id));
                T.Eq("resumed file is intact", DlTestServer.Sha256Of(4, r.Size), FileHash(Path.Combine(folder, "resume.bin")));
            }
            bool rangedResume = false, ifRangeSent = false;
            lock (srv.Gate)
                for (int i = requestsBefore; i < r.RangesSeen.Count; i++)
                {
                    if (r.RangesSeen[i].StartsWith("bytes=") && !r.RangesSeen[i].StartsWith("bytes=0-")) rangedResume = true;
                    if (r.IfRanges[i] == "\"v1\"") ifRangeSent = true;
                }
            T.Check("resume asked the server for the missing ranges only", rangedResume);
            T.Check("resume sent If-Range with the ETag", ifRangeSent);
        }

        private static void ChangedOnServer(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("changing.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 5;
            r.RateBps = 600 * 1024;
            string folder = Dir("changed");
            string store = Dir("changed-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 1;
            string id;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("changing.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 300 * 1024; }, 10000);
            }
            lock (srv.Gate) { r.ETag = "\"v2\""; r.Seed = 55; r.RateBps = 0; }
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool failed = WaitFor(delegate { return StateOf(e, id) == DlState.Failed; }, 10000);
                DlItem it = e.Find(id);
                T.Check("a file changed on the server stops the resume (If-Range → 200)", failed && it.ErrorKind == DlErrorKind.Changed, Info(e, id));
                T.Check("the partial data is kept for the user to decide", File.Exists(Path.Combine(folder, "changing.bin" + DlPaths.PartSuffix)));
                string why;
                T.Check("restart from zero is accepted", e.Restart(id, out why), why);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("restart downloads the new version", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(55, r.Size), Info(e, id));
            }
        }

        private static void PauseResume(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("pause.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 6;
            r.RateBps = 800 * 1024;
            string folder = Dir("pause");
            using (DlEngine e = NewEngine(Dir("pause-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("pause.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 200 * 1024; }, 10000);
                e.Pause(id);
                bool paused = WaitFor(delegate { return StateOf(e, id) == DlState.Paused; }, 5000);
                long frozen = e.Find(id).DoneBytes;
                Thread.Sleep(700);
                T.Check("pause stops the transfer", paused && e.Find(id).DoneBytes == frozen && frozen < r.Size, Info(e, id));
                r.RateBps = 0;
                e.Resume(id);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("resume after pause completes intact", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(6, r.Size), Info(e, id));
            }
        }

        private static void Redirects(DlTestServer srv)
        {
            DlTestServer.Res target = srv.Add("target.bin", new DlTestServer.Res());
            target.Size = 300 * 1024;
            target.Seed = 7;
            DlTestServer.Res hop = srv.Add("hop", new DlTestServer.Res());
            hop.RedirectTo = srv.UrlLocalhost("target.bin");
            DlTestServer.Res loop = srv.Add("loop", new DlTestServer.Res());
            loop.RedirectTo = srv.Url("loop");
            string folder = Dir("redirect");
            using (DlEngine e = NewEngine(Dir("redirect-store"), NewSettings(folder), new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("hop");
                req.Cookies = "sid=TOPSECRET";
                string dup, err;
                string id = e.Add(req, out dup, out err);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                DlItem it = e.Find(id);
                T.Check("redirect to another host completes", done && it.FinalUrl.StartsWith("http://localhost:"), Info(e, id));
                T.Check("the redirect chain is recorded", it.Redirects.Count == 1);
                bool cookieToOrigin, cookieElsewhere = false;
                lock (srv.Gate)
                {
                    cookieToOrigin = hop.Cookies.Count > 0 && hop.Cookies[0] == "sid=TOPSECRET";
                    foreach (string c in target.Cookies) if (c.Length > 0) cookieElsewhere = true;
                }
                T.Check("cookies go to the host they were given for", cookieToOrigin);
                T.Check("cookies are not forwarded to another host", !cookieElsewhere);

                string loopId = Add(e, srv.Url("loop"));
                bool failed = WaitFor(delegate { return StateOf(e, loopId) == DlState.Failed; }, 10000);
                T.Check("a redirect loop stops at the limit", failed && e.Find(loopId).ErrorKind == DlErrorKind.Policy && loop.Requests == DlHttp.MaxRedirects + 1,
                        Info(e, loopId) + " requests=" + loop.Requests);
            }
        }

        private static void RetryAndNotRetry(DlTestServer srv)
        {
            DlTestServer.Res busy = srv.Add("busy.bin", new DlTestServer.Res());
            busy.Size = 200 * 1024;
            busy.Seed = 8;
            busy.FailFirst = 2;
            busy.FailCode = 503;
            busy.RetryAfter = "1";
            DlTestServer.Res gone = srv.Add("gone.bin", new DlTestServer.Res());
            gone.Status = 404;
            string folder = Dir("retry");
            using (DlEngine e = NewEngine(Dir("retry-store"), NewSettings(folder), new FakeDlEnv()))
            {
                Stopwatch w = Stopwatch.StartNew();
                string id = Add(e, srv.Url("busy.bin"));
                string goneId = Add(e, srv.Url("gone.bin"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                T.Check("503 with Retry-After is retried until success", done && busy.Requests == 3, Info(e, id) + " requests=" + busy.Requests);
                T.Check("the server pause was honoured (≥ 2 s for two 1 s pauses)", w.ElapsedMilliseconds >= 1900, w.ElapsedMilliseconds.ToString());
                T.Eq("attempts reset after success", 0, e.Find(id).Attempts);
                bool failed = WaitFor(delegate { return StateOf(e, goneId) == DlState.Failed; }, 5000);
                Thread.Sleep(1500);
                T.Check("404 is not retried", failed && gone.Requests == 1 && e.Find(goneId).ErrorKind == DlErrorKind.Client, Info(e, goneId) + " requests=" + gone.Requests);
            }
        }

        private static void LinkExpiredAndRefresh(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("expiring.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 9;
            r.RateBps = 600 * 1024;
            DlTestServer.Res fresh = srv.Add("fresh-link.bin", new DlTestServer.Res());
            fresh.Size = r.Size;
            fresh.Seed = 9;
            fresh.ETag = "\"other-node\"";
            string folder = Dir("expired");
            string store = Dir("expired-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 1;
            string id;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("expiring.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 300 * 1024; }, 10000);
            }
            lock (srv.Gate) r.ForbidResume = true;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool needs = WaitFor(delegate { return StateOf(e, id) == DlState.NeedsLink; }, 10000);
                long kept = e.Find(id).DoneBytes;
                T.Check("403 while resuming asks for a fresh link and keeps the data", needs && kept > 0, Info(e, id));
                string why;
                T.Check("refresh link is accepted", e.RefreshLink(id, srv.Url("fresh-link.bin"), null, out why), why);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("the fresh link finishes the same file", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(9, r.Size), Info(e, id));
                bool fromOffset = false;
                lock (srv.Gate) foreach (string range in fresh.RangesSeen) if (range.StartsWith("bytes=") && !range.StartsWith("bytes=0-")) fromOffset = true;
                T.Check("the fresh link continued from the kept bytes", fromOffset);
            }
        }

        private static void MirrorAndHash(DlTestServer srv)
        {
            DlTestServer.Res primary = srv.Add("primary-missing.bin", new DlTestServer.Res());
            primary.Status = 404;
            DlTestServer.Res mirror = srv.Add("mirror.bin", new DlTestServer.Res());
            mirror.Size = 400 * 1024;
            mirror.Seed = 10;
            DlTestServer.Res bad = srv.Add("badhash.bin", new DlTestServer.Res());
            bad.Size = 100 * 1024;
            bad.Seed = 11;
            string folder = Dir("mirror");
            using (DlEngine e = NewEngine(Dir("mirror-store"), NewSettings(folder), new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("primary-missing.bin");
                req.Mirrors.Add(srv.Url("mirror.bin"));
                req.ExpectedHash = "sha256:" + DlTestServer.Sha256Of(10, mirror.Size);
                string dup, err;
                string id = e.Add(req, out dup, out err);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("404 on the primary switches to the mirror, the expected hash matches", done, Info(e, id));

                DlAddRequest wrong = new DlAddRequest();
                wrong.Url = srv.Url("badhash.bin");
                wrong.ExpectedHash = "sha256:" + new string('0', 64);
                string wid = e.Add(wrong, out dup, out err);
                bool failed = WaitFor(delegate { return StateOf(e, wid) == DlState.Failed; }, 10000);
                DlItem it = e.Find(wid);
                T.Check("a hash mismatch fails the download", failed && it.ErrorKind == DlErrorKind.HashMismatch, Info(e, wid));
                T.Check("a mismatched file stays partial and never gets its real name",
                        File.Exists(Path.Combine(folder, "badhash.bin" + DlPaths.PartSuffix)) && !File.Exists(Path.Combine(folder, "badhash.bin")));
            }
        }

        private static void Collision(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("collide", new DlTestServer.Res());
            r.Size = 50 * 1024;
            r.Seed = 12;
            r.Disposition = "attachment; filename=\"same.bin\"";
            string folder = Dir("collide");
            string existing = Fx.MakeFile(Path.Combine(folder, "same.bin"), 333);
            string before = FileHash(existing);
            using (DlEngine e = NewEngine(Dir("collide-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("collide"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("an existing file gets a numbered sibling", done && e.Find(id).FileName == "same (1).bin", Info(e, id));
                T.Eq("the existing file is untouched", before, FileHash(existing));
            }
        }

        private static void Throttle(DlTestServer srv)
        {
            DlTestServer.Res one = srv.Add("limit-item.bin", new DlTestServer.Res());
            one.Size = 1536 * 1024;
            one.Seed = 13;
            string folder = Dir("throttle");
            DlSettings s = NewSettings(folder);
            s.Segments = 2;
            using (DlEngine e = NewEngine(Dir("throttle-store"), s, new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("limit-item.bin");
                req.LimitKBps = 512;
                string dup, err;
                string id = e.Add(req, out dup, out err);
                double rate = MeasureRate(e, new[] { id }, 20000);
                T.Check("per-download limit 512 KB/s holds within ±15 %", rate > 512 * 1024 * 0.85 && rate < 512 * 1024 * 1.15, (rate / 1024).ToString("0") + " KB/s");
            }

            DlTestServer.Res a = srv.Add("limit-a.bin", new DlTestServer.Res());
            a.Size = 768 * 1024; a.Seed = 14;
            DlTestServer.Res b = srv.Add("limit-b.bin", new DlTestServer.Res());
            b.Size = 768 * 1024; b.Seed = 15;
            DlSettings g = NewSettings(Dir("throttle-global"));
            g.LimitKBps = 512;
            using (DlEngine e = NewEngine(Dir("throttle-global-store"), g, new FakeDlEnv()))
            {
                string ia = Add(e, srv.Url("limit-a.bin"));
                string ib = Add(e, srv.Url("limit-b.bin"));
                double rate = MeasureRate(e, new[] { ia, ib }, 20000);
                T.Check("global limit 512 KB/s holds for two downloads together within ±15 %", rate > 512 * 1024 * 0.85 && rate < 512 * 1024 * 1.15, (rate / 1024).ToString("0") + " KB/s");
            }
        }

        // Байты в секунду между первым замером с данными и завершением всех загрузок.
        private static double MeasureRate(DlEngine e, string[] ids, int timeoutMs)
        {
            Func<long> done = delegate
            {
                long sum = 0;
                foreach (string id in ids) { DlItem it = e.Find(id); if (it != null) sum += it.State == DlState.Completed ? Math.Max(0, it.Total) : it.DoneBytes; }
                return sum;
            };
            Func<bool> all = delegate
            {
                foreach (string id in ids) if (StateOf(e, id) != DlState.Completed) return false;
                return true;
            };
            if (!WaitFor(delegate { return done() > 64 * 1024; }, timeoutMs)) return -1;
            Stopwatch w = Stopwatch.StartNew();
            long start = done();
            if (!WaitFor(all, timeoutMs)) return -2;
            return (done() - start) / w.Elapsed.TotalSeconds;
        }

        private static void Concurrency(DlTestServer srv)
        {
            string[] keys = { "q1.bin", "q2.bin", "q3.bin", "q4.bin" };
            foreach (string k in keys)
            {
                DlTestServer.Res r = srv.Add(k, new DlTestServer.Res());
                r.Size = 256 * 1024;
                r.Seed = 20;
                r.RateBps = 256 * 1024;
            }
            lock (srv.Gate) srv.MaxActiveKeys = 0;
            DlSettings s = NewSettings(Dir("queue"));
            s.Segments = 1;
            s.MaxActive = 2;
            s.MaxPerHost = 10;
            using (DlEngine e = NewEngine(Dir("queue-store"), s, new FakeDlEnv()))
            {
                List<string> ids = new List<string>();
                foreach (string k in keys) ids.Add(Add(e, srv.Url(k)));
                bool done = WaitFor(delegate { foreach (string id in ids) if (StateOf(e, id) != DlState.Completed) return false; return true; }, 20000);
                T.Check("max active 2: all four complete", done);
                T.Check("max active 2: never more than two at once, and two did run together", srv.MaxActiveKeys == 2, srv.MaxActiveKeys.ToString());
            }

            string[] hostKeys = { "h1.bin", "h2.bin", "h3.bin" };
            foreach (string k in hostKeys)
            {
                DlTestServer.Res r = srv.Add(k, new DlTestServer.Res());
                r.Size = 256 * 1024;
                r.Seed = 21;
                r.RateBps = 256 * 1024;
            }
            lock (srv.Gate) { srv.MaxActiveKeys = 0; srv.HostMax.Clear(); }
            DlSettings h = NewSettings(Dir("perhost"));
            h.Segments = 1;
            h.MaxActive = 10;
            h.MaxPerHost = 1;
            using (DlEngine e = NewEngine(Dir("perhost-store"), h, new FakeDlEnv()))
            {
                string a = Add(e, srv.Url("h1.bin"));
                string b = Add(e, srv.Url("h2.bin"));
                string c = Add(e, srv.UrlLocalhost("h3.bin"));
                bool done = WaitFor(delegate { return StateOf(e, a) == DlState.Completed && StateOf(e, b) == DlState.Completed && StateOf(e, c) == DlState.Completed; }, 20000);
                int loopbackMax;
                lock (srv.Gate) srv.HostMax.TryGetValue("127.0.0.1", out loopbackMax);
                T.Check("per host 1: all complete", done);
                T.Check("per host 1: one at a time from 127.0.0.1", loopbackMax == 1, loopbackMax.ToString());
                T.Check("per host 1: another host runs in parallel", srv.MaxActiveKeys >= 2, srv.MaxActiveKeys.ToString());
            }
        }

        private static void Gates(DlTestServer srv)
        {
            DlTestServer.Res later = srv.Add("later.bin", new DlTestServer.Res());
            later.Size = 100 * 1024; later.Seed = 30;
            DlTestServer.Res sched = srv.Add("sched.bin", new DlTestServer.Res());
            sched.Size = 100 * 1024; sched.Seed = 31;
            DlTestServer.Res idle = srv.Add("idle.bin", new DlTestServer.Res());
            idle.Size = 2 * 1024 * 1024; idle.Seed = 32; idle.RateBps = 300 * 1024;
            DlTestServer.Res net = srv.Add("metered.bin", new DlTestServer.Res());
            net.Size = 100 * 1024; net.Seed = 33;

            // Отложенный старт
            FakeDlEnv env = new FakeDlEnv();
            using (DlEngine e = NewEngine(Dir("later-store"), NewSettings(Dir("later")), env))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("later.bin");
                req.StartAtUtc = env.UtcNow.AddMinutes(10);
                string dup, err;
                string id = e.Add(req, out dup, out err);
                Thread.Sleep(800);
                T.Check("a postponed download waits for its time", StateOf(e, id) == DlState.Scheduled && later.Requests == 0, Info(e, id));
                env.Advance(TimeSpan.FromMinutes(11));
                T.Check("the postponed download starts when its time comes", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }

            // Расписание
            env = new FakeDlEnv();
            DlSettings s = NewSettings(Dir("sched"));
            int nowMinute = DlSettings.MinuteOfDay(env.LocalNow);
            s.ScheduleEnabled = true;
            s.ScheduleFrom = (nowMinute + 120) % 1440;
            s.ScheduleTo = (nowMinute + 240) % 1440;
            using (DlEngine e = NewEngine(Dir("sched-store"), s, env))
            {
                string id = Add(e, srv.Url("sched.bin"));
                Thread.Sleep(800);
                T.Check("outside the schedule the download waits", StateOf(e, id) == DlState.Waiting && sched.Requests == 0, Info(e, id));
                env.Advance(TimeSpan.FromMinutes(150));
                T.Check("inside the schedule it runs", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }

            // Простой ПК
            env = new FakeDlEnv();
            env.IdleSeconds = 0;
            DlSettings si = NewSettings(Dir("idle"));
            si.Segments = 1;
            using (DlEngine e = NewEngine(Dir("idle-store"), si, env))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("idle.bin");
                req.WhenIdle = true;
                string dup, err;
                string id = e.Add(req, out dup, out err);
                Thread.Sleep(800);
                T.Check("«when idle» waits while the user is active", StateOf(e, id) == DlState.Waiting && idle.Requests == 0, Info(e, id));
                env.IdleSeconds = 100000;
                env.Cpu = 90;
                Thread.Sleep(800);
                T.Check("«when idle» still waits while the CPU is busy", StateOf(e, id) == DlState.Waiting, Info(e, id));
                env.Cpu = 5;
                bool started = WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 200 * 1024; }, 8000);
                T.Check("«when idle» starts once the PC is idle", started, Info(e, id));
                env.Cpu = 90;
                Thread.Sleep(600);
                T.Check("our own load does not stop an idle download", StateOf(e, id) == DlState.Active, Info(e, id));
                env.Fullscreen = true;
                T.Check("a fullscreen app puts it on hold", WaitFor(delegate { return StateOf(e, id) == DlState.Waiting; }, 5000), Info(e, id));
                env.Fullscreen = false;
                env.Cpu = 5;
                T.Check("the fullscreen app gone — it runs again", WaitFor(delegate { return StateOf(e, id) == DlState.Active; }, 5000), Info(e, id));
                env.IdleSeconds = 3;
                bool held = WaitFor(delegate { return StateOf(e, id) == DlState.Waiting; }, 5000);
                long kept = e.Find(id).DoneBytes;
                T.Check("the user coming back puts it on hold, data kept", held && kept > 0 && kept < idle.Size, Info(e, id));
                env.IdleSeconds = 100000;
                env.Cpu = 5;
                idle.RateBps = 0;
                T.Check("idle again — it finishes intact",
                        WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000) && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(32, idle.Size), Info(e, id));
            }

            // Лимитная сеть и батарея
            env = new FakeDlEnv();
            env.Metered = true;
            using (DlEngine e = NewEngine(Dir("metered-store"), NewSettings(Dir("metered")), env))
            {
                string id = Add(e, srv.Url("metered.bin"));
                Thread.Sleep(800);
                T.Check("a metered connection holds the queue", StateOf(e, id) == DlState.Waiting && net.Requests == 0 && e.GlobalGate.Length > 0, Info(e, id));
                env.Metered = false;
                env.Battery = true;
                Thread.Sleep(800);
                T.Check("battery power holds the queue", StateOf(e, id) == DlState.Waiting && net.Requests == 0, Info(e, id));
                env.Battery = false;
                T.Check("back on AC and unmetered — it runs", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }
        }

        private static void RemoveAndDuplicates(DlTestServer srv, List<string> recycled)
        {
            DlTestServer.Res r = srv.Add("keep.bin", new DlTestServer.Res());
            r.Size = 60 * 1024; r.Seed = 40;
            DlTestServer.Res r2 = srv.Add("recycle.bin", new DlTestServer.Res());
            r2.Size = 60 * 1024; r2.Seed = 41;
            string folder = Dir("remove");
            using (DlEngine e = NewEngine(Dir("remove-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string keep = Add(e, srv.Url("keep.bin"));
                string gone = Add(e, srv.Url("recycle.bin"));
                WaitFor(delegate { return StateOf(e, keep) == DlState.Completed && StateOf(e, gone) == DlState.Completed; }, 10000);

                DlAddRequest again = new DlAddRequest();
                again.Url = srv.Url("keep.bin") + "#fragment";
                string dup, err;
                T.Check("the same link is refused as a duplicate", e.Add(again, out dup, out err) == null && dup == keep, err);
                again.AllowDuplicate = true;
                T.Check("a duplicate is accepted when the user insists", e.Add(again, out dup, out err) != null && dup == keep);

                DlAddRequest badScheme = new DlAddRequest();
                badScheme.Url = "file:///C:/Windows/win.ini";
                T.Check("add refuses file: links", e.Add(badScheme, out dup, out err) == null && err != null);
                DlAddRequest badFolder = new DlAddRequest();
                badFolder.Url = srv.Url("keep.bin");
                badFolder.AllowDuplicate = true;
                badFolder.Folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                T.Check("add refuses a system folder", e.Add(badFolder, out dup, out err) == null && err != null);

                string why;
                string keepPath = e.Find(keep).TargetPath;
                T.Check("removing the record only", e.Remove(keep, false, out why) && e.Find(keep) == null, why);
                T.Check("the file stays when only the record is removed", File.Exists(keepPath));

                string gonePath = e.Find(gone).TargetPath;
                lock (recycled) recycled.Clear();
                T.Check("remove with files goes through the Recycle Bin", e.Remove(gone, true, out why), why);
                lock (recycled) T.Check("exactly the downloaded file was handed to the Recycle Bin", recycled.Count == 1 && recycled[0] == gonePath, string.Join("; ", recycled.ToArray()));
                T.Check("the engine never deletes the file itself", File.Exists(gonePath));

                string third = Add(e, srv.Url("recycle.bin"));
                WaitFor(delegate { return StateOf(e, third) == DlState.Completed; }, 10000);
                Func<string, string> ok = DlFiles.Recycler;
                DlFiles.Recycler = delegate { return "no Recycle Bin on this volume"; };
                try
                {
                    T.Check("no Recycle Bin — nothing removed, the record stays", !e.Remove(third, true, out why) && e.Find(third) != null && File.Exists(e.Find(third).TargetPath), why);
                }
                finally { DlFiles.Recycler = ok; }
            }
        }

        // ---------- канал ----------
        private static void Pipe(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("pipe.bin", new DlTestServer.Res());
            r.Size = 80 * 1024; r.Seed = 50;
            string name = "WindowsProcessCleaner.dl.test-" + Process.GetCurrentProcess().Id;
            using (DlEngine e = NewEngine(Dir("pipe-store"), NewSettings(Dir("pipe")), new FakeDlEnv()))
            {
                bool shutdown = false;
                DlCommands commands = new DlCommands(e, delegate { shutdown = true; }, null);
                using (DlPipeServer server = new DlPipeServer(name, commands.Handle, DlIpc.IsOwnImage))
                {
                    server.Start();
                    JVal hello = DlClient.Call(name, DlClient.Command("hello"), 3000);
                    T.Check("pipe: hello from our own exe is answered", hello != null && DlJson.Bool(hello, "ok", false) && DlJson.Int(hello, "version", 0) == 1);

                    JVal add = DlClient.Command("add");
                    add.Set("url", JVal.NewStr(srv.Url("pipe.bin")));
                    JVal added = DlClient.Call(name, add, 3000);
                    string id = DlJson.Str(added, "id", "");
                    T.Check("pipe: add returns an id", DlItem.IsValidId(id), added == null ? "null" : Jsn.Write(added));
                    WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                    JVal list = DlClient.Call(name, DlClient.Command("list"), 3000);
                    bool listed = false;
                    JVal items = list == null ? null : list.Get("items");
                    if (items != null) foreach (JVal it in items.V) if (DlJson.Str(it, "Id", "") == id && DlJson.Str(it, "State", "") == "Completed") listed = true;
                    T.Check("pipe: list shows the completed download", listed);
                    T.Check("pipe: list carries no journals unless asked", items != null && items.V.Count > 0 && items.V[0].Get("Events") == null);

                    JVal bad = DlClient.Command("remove");
                    bad.Set("id", JVal.NewStr("..\\..\\Windows"));
                    JVal badResp = DlClient.Call(name, bad, 3000);
                    T.Check("pipe: an id that looks like a path is refused", badResp != null && !DlJson.Bool(badResp, "ok", true) && DlJson.Str(badResp, "error", "") == "bad id");
                    JVal unknown = DlClient.Call(name, DlClient.Command("runFile"), 3000);
                    T.Check("pipe: unknown command is refused", unknown != null && !DlJson.Bool(unknown, "ok", true));
                    JVal sysFolder = DlClient.Command("setSettings");
                    DlSettings evil = NewSettings(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    sysFolder.Set("settings", evil.ToJson());
                    JVal sysResp = DlClient.Call(name, sysFolder, 3000);
                    T.Check("pipe: settings with a system folder are refused", sysResp != null && !DlJson.Bool(sysResp, "ok", true));

                    // Кадр длиной 2 ГБ: сервер рвёт соединение, а не выделяет память.
                    bool closed = false;
                    try
                    {
                        using (NamedPipeClientStream raw = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                        {
                            raw.Connect(3000);
                            raw.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, 0, 4);
                            raw.Flush();
                            closed = raw.ReadByte() == -1;
                        }
                    }
                    catch (IOException) { closed = true; }
                    T.Check("pipe: an oversized frame closes the connection", closed);
                    JVal after = DlClient.Call(name, DlClient.Command("hello"), 3000);
                    T.Check("pipe: the server keeps serving after a bad client", after != null);

                    PipeSecurity sec = DlIpc.CreatePipeSecurity();
                    AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                    bool onlyUser = rules.Count == 1 && ((PipeAccessRule)rules[0]).IdentityReference.Value == DlIpc.User().Value
                                    && ((PipeAccessRule)rules[0]).AccessControlType == AccessControlType.Allow;
                    T.Check("pipe: the ACL grants only the current user, inheritance cut", onlyUser && sec.AreAccessRulesProtected, rules.Count.ToString());

                    string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                    if (File.Exists(ps))
                    {
                        int refusedBefore = server.Refused;
                        string script = "$r=-1; try { $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + name + "', 'InOut'); $p.Connect(5000); "
                                        + "$b=[byte[]](12,0,0,0,123,34,99,109,100,34,58,34,104,105,34,125); $p.Write($b,0,16); $p.Flush(); $r = $p.ReadByte() } catch { $r = -1 }; exit ($r + 10)";
                        ProcessStartInfo psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -Command \"" + script + "\"");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        int exit = -100;
                        using (Process p = Process.Start(psi))
                        {
                            if (p.WaitForExit(30000)) exit = p.ExitCode;
                            else { try { p.Kill(); } catch { } }
                        }
                        T.Check("pipe: another program (PowerShell) is refused without an answer", exit == 9 && server.Refused > refusedBefore,
                                "exit " + exit + ", refused " + server.Refused);
                    }
                    else T.Skip("pipe: another program is refused", "powershell.exe not found");

                    JVal stop = DlClient.Call(name, DlClient.Command("shutdown"), 3000);
                    T.Check("pipe: shutdown reaches the agent", stop != null && shutdown);
                }
            }
        }
    }
}
