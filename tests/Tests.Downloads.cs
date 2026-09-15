// SysDeck — область «downloads»: разбор заголовков, имена и пути, настройки и хранилище, настоящий движок
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
using SysDeck.Downloads;

namespace SysDeck.Tests
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
            T.Eq("default idle minutes", 2, s.IdleMinutes);
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
    }
}
