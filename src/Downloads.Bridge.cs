// Windows Process Cleaner — «Загрузки»: мост к браузерам — режим native messaging host того же exe.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Браузер сам запускает exe: Chromium — «exe chrome-extension://<id>/ --parent-window=0» через cmd.exe с каналами вместо
// stdin/stdout, Firefox — «exe <манифест> <id дополнения>». Хост проверяет id расширения по своему списку (кроме
// allowed_origins в манифесте), решает по правилам и пробному запросу, забирать ли загрузку, и передаёт её фоновому процессу
// загрузок по каналу. Решение — без человека и за ≤5 с: браузер держит загрузку не дольше ~15 с. Забираем двухфазно:
// «accept» → расширение отменило загрузку в браузере → «commit» → только теперь элемент появляется в списке. Не удалось
// добавить после отмены — отказ на commit, и загрузку браузеру возвращает само расширение. Cookies живут только в памяти.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    // ------------------------------------------------------------------ //
    //  Имя хоста, разрешённые расширения, распознавание запуска браузером
    // ------------------------------------------------------------------ //
    internal static class DlBridge
    {
        public const string HostName = "org.wpc.downloads";
        public const int ProtocolVersion = 1;
        public const int MaxToBrowser = 1024 * 1024;          // предел Chrome для сообщения хоста
        public const int MaxFromBrowser = 4 * 1024 * 1024;
        public const int HoldMs = 5000;                       // столько расширение держит загрузку, ожидая ответа
        public const int ProbeMs = 3000;

        // Распакованная сборка с полем key в манифесте; id из магазинов добавляются после публикации.
        public static readonly string[] ChromiumIds = { "kfbocmoigekddjcfodbhbiahndahdmal" };
        public static readonly string[] GeckoIds = { "wpc-downloads@windows-process-cleaner" };
        public static readonly string[] Browsers = { "chrome", "edge", "yandex", "firefox", "chromium" };

        // family: "chromium" | "firefox"; id — id расширения. false — это не запуск браузером (обычное окно, ключи /tray и т.п.).
        public static bool TryParse(string[] args, out string family, out string id)
        {
            family = null;
            id = null;
            if (args == null || args.Length == 0) return false;
            foreach (string a in args)
            {
                if (a == null || !a.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase)) continue;
                family = "chromium";
                id = a.Substring("chrome-extension://".Length).TrimEnd('/');
                return true;
            }
            if (args.Length >= 2 && args[0] != null && args[1] != null
                && args[0].EndsWith(".json", StringComparison.OrdinalIgnoreCase) && args[1].IndexOf('@') > 0 && args[1].IndexOfAny(new[] { ' ', '/', '\\' }) < 0)
            {
                family = "firefox";
                id = args[1];
                return true;
            }
            return false;
        }

        public static bool IsAllowed(string family, string id)
        {
            if (string.IsNullOrEmpty(id)) return false;
            string[] list = family == "chromium" ? ChromiumIds : family == "firefox" ? GeckoIds : null;
            if (list == null) return false;
            foreach (string known in list) if (string.Equals(known, id, StringComparison.Ordinal)) return true;
            return false;
        }

        public static bool IsBrowserSource(string source)
        {
            return Array.IndexOf(Browsers, source ?? "") >= 0;
        }

        // Проверяется в Main раньше мьютекса окна. Чужое расширение — выход без чтения stdin: ему ничего не отвечаем.
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            string family, id;
            if (!TryParse(args, out family, out id)) return false;
            if (!IsAllowed(family, id))
            {
                DlLog.Write("native host: extension refused: " + family + " " + id);
                exitCode = 2;
                return true;
            }
            try { Tr.En = new Engine().Config.Language == "en"; }
            catch (Exception ex) { DlLog.Report(ex); }
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { DlLog.Report(e.ExceptionObject as Exception); };
            using (Stream input = Console.OpenStandardInput())
            using (Stream output = Console.OpenStandardOutput())
            {
                DlNativeHost host = new DlNativeHost(input, output, family, new DlBridgeBackend());
                exitCode = host.Run();
            }
            return true;
        }
    }

    // ------------------------------------------------------------------ //
    //  Предложение загрузки от расширения и правила перехвата
    // ------------------------------------------------------------------ //
    internal sealed class DlOffer
    {
        public string Url = "";
        public string FinalUrl = "";
        public string Referrer = "";
        public string PageUrl = "";
        public string FileName = "";
        public string Mime = "";
        public long Total = -1;
        public string Cookies = "";
        public string UserAgent = "";
        public bool Incognito;
        public bool ByExtension;

        public string EffectiveUrl { get { return DlHttp.IsAllowedScheme(FinalUrl) ? FinalUrl : Url; } }

        public static DlOffer FromJson(JVal o)
        {
            DlOffer x = new DlOffer();
            x.Url = DlJson.Str(o, "url", "").Trim();
            x.FinalUrl = DlJson.Str(o, "finalUrl", "").Trim();
            x.Referrer = DlJson.Str(o, "referrer", "").Trim();
            x.PageUrl = DlJson.Str(o, "pageUrl", "").Trim();
            x.FileName = DlJson.Str(o, "filename", "");
            x.Mime = DlJson.Str(o, "mime", "").Trim().ToLowerInvariant();
            x.Total = DlJson.Long(o, "totalBytes", -1);
            x.Cookies = DlJson.Str(o, "cookies", "");
            x.UserAgent = DlJson.Str(o, "ua", "");
            x.Incognito = DlJson.Bool(o, "incognito", false);
            x.ByExtension = DlJson.Bool(o, "byExtension", false);
            return x;
        }

        // Имя, которое выбрал браузер (из Content-Disposition или адреса): у Chromium это полный путь — берём последнюю часть.
        public string BaseName
        {
            get
            {
                string n = (FileName ?? "").Replace('/', '\\');
                int slash = n.LastIndexOf('\\');
                return slash >= 0 ? n.Substring(slash + 1) : n;
            }
        }
    }

    internal sealed class DlProbeResult
    {
        public int Status;               // 0 — ответа нет
        public long Total = -1;
        public string ContentType = "";
        public string Failure = "";      // сеть, таймаут, политика редиректа
    }

    internal static class DlBridgeRules
    {
        public static JVal ToJson(DlSettings s)
        {
            JVal o = JVal.NewObj();
            o.Set("enabled", DlJson.B(s.BrowserIntercept));
            o.Set("incognito", DlJson.B(s.BrowserIncognito));
            o.Set("skipHosts", DlJson.Strings(s.BrowserSkipHosts));
            o.Set("skipExt", DlJson.Strings(s.BrowserSkipExt));
            o.Set("catchAll", DlJson.B(true));
            o.Set("holdMs", DlJson.N(DlBridge.HoldMs));
            return o;
        }

        // null — забирать; иначе код причины (в журнал и расширению). Размер не проверяется намеренно.
        public static string Refusal(DlOffer o, DlSettings s, bool altHeld)
        {
            if (!s.BrowserIntercept) return "disabled";
            if (o.ByExtension) return "byExtension";
            if (!DlHttp.IsAllowedScheme(o.Url) || (o.FinalUrl.Length > 0 && !DlHttp.IsAllowedScheme(o.FinalUrl))) return "scheme";
            if (o.Incognito && !s.BrowserIncognito) return "incognito";
            if (altHeld) return "alt";
            string host = HostOf(o.EffectiveUrl);
            foreach (string pattern in s.BrowserSkipHosts)
                if (DlFiles.HostMatches(host, pattern)) return "host";
            string ext = ExtensionOf(o);
            if (ext.Length > 0 && s.BrowserSkipExt.Contains(ext)) return "ext";
            return null;
        }

        public static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host.ToLowerInvariant() : "";
        }

        public static string ExtensionOf(DlOffer o)
        {
            string name = o.BaseName;
            if (name.Length == 0) name = DlFiles.NameFromUrl(o.EffectiveUrl) ?? "";
            int dot = name.LastIndexOf('.');
            return dot < 0 || dot == name.Length - 1 ? "" : name.Substring(dot + 1).ToLowerInvariant();
        }

        public static bool IsTorrent(DlOffer o)
        {
            return o.Mime == "application/x-bittorrent" || ExtensionOf(o) == "torrent";
        }

        // Сервер отдаёт нашей программе тот же файл, что и браузеру. Иначе загрузку оставляем браузеру — у него она уже идёт.
        public static bool ProbeAcceptable(DlOffer o, DlProbeResult p, out string why)
        {
            why = null;
            if (p.Status != 200 && p.Status != 206)
            {
                why = p.Status == 0 ? "probe: " + (p.Failure.Length > 0 ? p.Failure : "no answer") : "probe: http " + p.Status;
                return false;
            }
            if (o.Total > 0 && p.Total > 0 && o.Total != p.Total)
            {
                why = "probe: size " + p.Total + " != browser " + o.Total;
                return false;
            }
            string ext = ExtensionOf(o);
            bool browserHtml = o.Mime.StartsWith("text/html") || ext == "htm" || ext == "html";
            if (!browserHtml && p.ContentType.Trim().ToLowerInvariant().StartsWith("text/html"))
            {
                why = "probe: html page instead of the file";
                return false;
            }
            return true;
        }
    }

    // ------------------------------------------------------------------ //
    //  Пробный запрос: тот же GET, что у движка (cookies, редиректы, проверки), но только заголовки и байт 0
    // ------------------------------------------------------------------ //
    internal static class DlProbe
    {
        public static DlProbeResult Run(DlOffer o, DlSettings settings, int timeoutMs)
        {
            DlProbeResult result = new DlProbeResult();
            DlItem it = new DlItem();
            it.Url = o.EffectiveUrl;
            it.Referrer = o.Referrer;
            it.UserAgent = o.UserAgent;
            if (o.Cookies.Length > 0)
            {
                it.Cookies = o.Cookies;
                it.CookieHost = DlBridgeRules.HostOf(it.Url);
            }
            DlTransfer transfer = new DlTransfer(it, settings, new DlTokenBucket(0), new DlSystemEnvironment(), null);
            Thread t = new Thread(delegate()
            {
                HttpWebResponse resp = null;
                try
                {
                    string finalUrl;
                    DlFailure f;
                    resp = transfer.Open(it.Url, 0, 0, true, false, null, out finalUrl, out f);
                    if (f != null) { lock (result) result.Failure = f.Message; return; }
                    long total = resp.ContentLength;
                    long s, e, t2;
                    if ((int)resp.StatusCode == 206 && DlHttp.TryParseContentRange(resp.Headers["Content-Range"], out s, out e, out t2)) total = t2;
                    lock (result)
                    {
                        result.Status = (int)resp.StatusCode;
                        result.Total = total;
                        result.ContentType = resp.ContentType ?? "";
                    }
                }
                catch (Exception ex) { lock (result) result.Failure = ex.Message; }
                finally { transfer.Release(resp); }
            });
            t.IsBackground = true;
            t.Name = "wpc-dl-probe";
            t.Start();
            if (!t.Join(timeoutMs))
            {
                transfer.RequestStop();
                t.Join(1000);
                lock (result)
                {
                    DlProbeResult late = new DlProbeResult();
                    late.Failure = "timeout";
                    return late;
                }
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ //
    //  Всё, что хост берёт извне: настройки, клавиатура, сеть, процесс загрузок. Тесты подменяют только это.
    // ------------------------------------------------------------------ //
    internal interface IDlBridgeBackend
    {
        DlSettings LoadSettings();
        DateTime SettingsStamp();
        void SaveSettings(DlSettings s);
        bool AltHeld();
        DlProbeResult Probe(DlOffer offer, DlSettings s);
        bool AgentRunning();
        string EnsureAgent();                         // null — процесс загрузок отвечает
        JVal Agent(JVal request);                     // null — не ответил
        void OpenApp();
        void RecordHello(string browser, string extVersion);
    }

    internal sealed class DlBridgeBackend : IDlBridgeBackend
    {
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        private const int VK_MENU = 0x12;

        public DlSettings LoadSettings() { return DlSettings.Load(); }

        public DateTime SettingsStamp()
        {
            try { return File.GetLastWriteTimeUtc(DlPaths.SettingsFile); }
            catch { return DateTime.MinValue; }
        }

        public void SaveSettings(DlSettings s) { s.Save(); }

        public bool AltHeld() { return (GetAsyncKeyState(VK_MENU) & 0x8000) != 0; }

        public DlProbeResult Probe(DlOffer offer, DlSettings s) { return DlProbe.Run(offer, s, DlBridge.ProbeMs); }

        public bool AgentRunning() { return DlIpc.IsRunning(); }

        // Не дольше 9 с и 4 с на сам запрос: вместе меньше 15 с, которые расширение ждёт ответа на commit.
        public string EnsureAgent()
        {
            string why = DlLauncher.StartAgent();
            if (why != null) return why;
            if (!DlLauncher.WaitRunning(6000)) return "agent did not start";
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 3000)
            {
                if (DlClient.Call(DlClient.Command("hello"), 1000) != null) return null;
                Thread.Sleep(100);
            }
            return "agent does not answer";
        }

        public JVal Agent(JVal request) { return DlClient.Call(request, 4000); }

        public void OpenApp() { DlLauncher.OpenDownloadsPage(); }

        public void RecordHello(string browser, string extVersion) { DlBridgeSeen.Record(browser, extVersion); }
    }

    // Когда расширение браузера последний раз выходило на связь — для строки состояния в настройках. Файл на браузер:
    // хосты разных браузеров работают одновременно и не пишут один файл.
    internal static class DlBridgeSeen
    {
        public static string FileFor(string browser) { return Path.Combine(DlPaths.DataDir, "bridge-" + browser + ".json"); }

        public static void Record(string browser, string extVersion)
        {
            if (!DlBridge.IsBrowserSource(browser)) return;
            JVal o = JVal.NewObj();
            o.Set("lastSeenUtc", DlJson.D(DateTime.UtcNow));
            o.Set("extVersion", DlJson.S(extVersion));
            try { DlPaths.WriteAtomic(FileFor(browser), Jsn.Write(o)); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        public static DateTime LastSeen(string browser, out string extVersion)
        {
            JVal o = DlPaths.ReadJson(FileFor(browser));
            extVersion = DlJson.Str(o, "extVersion", "");
            return DlJson.Date(o, "lastSeenUtc");
        }
    }

    // ------------------------------------------------------------------ //
    //  Поручения хостам браузеров в процессе загрузок. Хост забирает свои опросом раз в 2 с; невостребованное живёт 5 минут.
    // ------------------------------------------------------------------ //
    internal sealed class DlBridgeTasks
    {
        private const int KeepSeconds = 300;

        private sealed class Entry
        {
            public string Browser;
            public DateTime Utc;
            public JVal Body;
        }

        private readonly List<Entry> _entries = new List<Entry>();
        private readonly HashSet<string> _askedCookies = new HashSet<string>(StringComparer.Ordinal);

        public void GiveBack(string browser, string url, string referrer, string filename)
        {
            JVal o = JVal.NewObj();
            o.Set("kind", DlJson.S("giveBack"));
            o.Set("url", DlJson.S(url));
            o.Set("referrer", DlJson.S(referrer));
            o.Set("filename", DlJson.S(filename));
            Enqueue(browser, o);
        }

        // Один раз на загрузку за жизнь процесса: не помогли свежие cookies — дальше решает человек («обновить ссылку»).
        public void AskCookies(string browser, DlItem it)
        {
            if (it == null) return;
            lock (_entries) if (!_askedCookies.Add(it.Id)) return;
            JVal o = JVal.NewObj();
            o.Set("kind", DlJson.S("needCookies"));
            o.Set("id", DlJson.S(it.Id));
            o.Set("url", DlJson.S(it.Url));
            Enqueue(browser, o);
        }

        private void Enqueue(string browser, JVal body)
        {
            Entry e = new Entry();
            e.Browser = browser ?? "";
            e.Utc = DateTime.UtcNow;
            e.Body = body;
            lock (_entries) _entries.Add(e);
        }

        public JVal Take(string browser)
        {
            JVal arr = JVal.NewArr();
            lock (_entries)
            {
                _entries.RemoveAll(delegate(Entry e) { return (DateTime.UtcNow - e.Utc).TotalSeconds > KeepSeconds; });
                for (int i = 0; i < _entries.Count; i++)
                {
                    if (!string.Equals(_entries[i].Browser, browser, StringComparison.Ordinal)) continue;
                    arr.V.Add(_entries[i].Body);
                    _entries.RemoveAt(i--);
                }
            }
            return arr;
        }

        public int Count { get { lock (_entries) return _entries.Count; } }
    }

    // ------------------------------------------------------------------ //
    //  Сам хост: кадры stdin/stdout, запросы расширения, толчки от программы
    // ------------------------------------------------------------------ //
    internal sealed class DlNativeHost
    {
        private const int PollMs = 2000;
        private const int PendingSeconds = 60;

        private sealed class Pending
        {
            public DlOffer Offer;
            public DateTime Utc;
        }

        private readonly Stream _in, _out;
        private readonly string _family;
        private readonly IDlBridgeBackend _backend;
        private readonly object _writeGate = new object();
        private readonly object _gate = new object();
        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly Dictionary<string, string> _cookieAsks = new Dictionary<string, string>();   // pushId → id загрузки
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private DlSettings _settings;
        private DateTime _settingsStamp;
        private string _browser = "";
        private int _pushSeq;
        private int _workers;

        public DlNativeHost(Stream input, Stream output, string family, IDlBridgeBackend backend)
        {
            _in = input;
            _out = output;
            _family = family;
            _backend = backend;
            _browser = family == "firefox" ? "firefox" : "chromium";
            _settingsStamp = backend.SettingsStamp();
            _settings = backend.LoadSettings() ?? new DlSettings();
        }

        public string Browser { get { lock (_gate) return _browser; } }

        // Код выхода: 0 — браузер закрыл stdin, 1 — поток сломан (кадр не разобрался).
        public int Run()
        {
            Thread poll = new Thread(PollLoop);
            poll.IsBackground = true;
            poll.Name = "wpc-dl-host-poll";
            poll.Start();
            int code = 0;
            try
            {
                while (true)
                {
                    JVal msg = DlIpc.ReadFrame(_in, DlBridge.MaxFromBrowser);
                    if (msg == null) break;
                    Dispatch(msg);
                }
            }
            catch (Exception ex)
            {
                DlLog.Write("native host: stream closed: " + ex.Message);
                code = 1;
            }
            _stop.Set();
            // Начатые offer/commit дописывают ответ (или понимают, что писать некуда) — ждём их недолго.
            Stopwatch clock = Stopwatch.StartNew();
            while (Thread.VolatileRead(ref _workers) > 0 && clock.ElapsedMilliseconds < 8000) Thread.Sleep(50);
            poll.Join(3000);
            return code;
        }

        private void Dispatch(JVal msg)
        {
            string type = DlJson.Str(msg, "type", "");
            string reqId = DlJson.Str(msg, "reqId", "");
            switch (type)
            {
                case "offer":
                case "commit":
                case "add":
                case "addMedia":
                case "skipHost":
                case "setEnabled":
                    // Пробный запрос и запуск процесса загрузок идут секунды — не задерживаем остальные сообщения.
                    Interlocked.Increment(ref _workers);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { Send(Handle(type, reqId, msg)); }
                        catch (Exception ex) { DlLog.Report(ex); Send(Fail(reqId, "internal", ex.Message)); }
                        finally { Interlocked.Decrement(ref _workers); }
                    });
                    return;
                case "needCookiesResult":
                    OnCookies(msg);
                    return;
                case "giveBackResult":
                    if (!DlJson.Bool(msg, "ok", false)) DlLog.Write("native host: browser did not take the download back: " + DlJson.Str(msg, "error", ""));
                    return;
            }
            JVal reply;
            try { reply = Handle(type, reqId, msg); }
            catch (Exception ex) { DlLog.Report(ex); reply = Fail(reqId, "internal", ex.Message); }
            Send(reply);
        }

        internal JVal Handle(string type, string reqId, JVal msg)
        {
            if (reqId.Length == 0) return Fail("", "bad_request", "reqId is required");
            switch (type)
            {
                case "hello": return Hello(reqId, msg);
                case "offer": return Offer(reqId, msg);
                case "commit": return Commit(reqId);
                case "abandon":
                    lock (_gate) _pending.Remove(reqId);
                    return Ok(reqId);
                case "add": return Add(reqId, msg);
                case "addMedia": return AddMedia(reqId, msg);
                case "status": return Status(reqId);
                case "openApp":
                    _backend.OpenApp();
                    return Ok(reqId);
                case "skipHost": return ChangeRules(reqId, msg, true);
                case "setEnabled": return ChangeRules(reqId, msg, false);
                case "browserFile":
                {
                    // Торрент-файл, скачанный браузером (сам хост его взять не смог): добавляется торрентом. Копию браузера убирает
                    // расширение — только если торрент в списке и так велит настройка; файл торрента уже лежит в папке данных.
                    DlLog.Write("native host: browser file ready: " + DlLog.Redact(DlJson.Str(msg, "url", "")));
                    JVal o = Ok(reqId);
                    o.Set("removeBrowserCopy", DlJson.B(BrowserTorrent(DlJson.Str(msg, "path", ""))));
                    return o;
                }
                default:
                {
                    JVal o = Fail(reqId, "unknown_type", type);
                    return o;
                }
            }
        }

        private JVal Hello(string reqId, JVal msg)
        {
            string browser = DlJson.Str(msg, "browser", "");
            if (!DlBridge.IsBrowserSource(browser) || (browser == "firefox") != (_family == "firefox")) browser = _family == "firefox" ? "firefox" : "chromium";
            lock (_gate) _browser = browser;
            _backend.RecordHello(browser, DlJson.Str(msg, "extVersion", ""));
            JVal o = Ok(reqId);
            o.Set("v", DlJson.N(DlBridge.ProtocolVersion));
            o.Set("appVersion", DlJson.S(typeof(DlBridge).Assembly.GetName().Version.ToString()));
            o.Set("rules", DlBridgeRules.ToJson(Settings()));
            return o;
        }

        private JVal Offer(string reqId, JVal msg)
        {
            DlOffer offer = DlOffer.FromJson(msg);
            DlSettings s = Settings();
            string refusal = DlBridgeRules.Refusal(offer, s, _backend.AltHeld());
            if (refusal != null) return Decision(reqId, "decline", refusal, offer);

            // Процесс загрузок поднимается, пока идёт пробный запрос: к commit он уже отвечает.
            ThreadPool.QueueUserWorkItem(delegate { _backend.EnsureAgent(); });
            DlProbeResult probe = _backend.Probe(offer, s);
            string why;
            if (!DlBridgeRules.ProbeAcceptable(offer, probe, out why))
                return Decision(reqId, DlBridgeRules.IsTorrent(offer) ? "watch" : "decline", why, offer);
            Pending p = new Pending();
            p.Offer = offer;
            p.Utc = DateTime.UtcNow;
            lock (_gate) _pending[reqId] = p;
            return Decision(reqId, "accept", null, offer);
        }

        private JVal Decision(string reqId, string action, string reason, DlOffer offer)
        {
            DlLog.Write("native host: " + action + (reason != null ? " (" + reason + ")" : "") + ": " + DlLog.Redact(offer.EffectiveUrl));
            JVal o = Ok(reqId);
            o.Set("action", DlJson.S(action));
            if (reason != null) o.Set("reason", DlJson.S(reason));
            return o;
        }

        private JVal Commit(string reqId)
        {
            Pending p;
            lock (_gate)
            {
                if (!_pending.TryGetValue(reqId, out p)) return Fail(reqId, "bad_request", "no such offer");
                _pending.Remove(reqId);
            }
            DlOffer o = p.Offer;
            JVal req = DlClient.Command("add");
            req.Set("url", DlJson.S(o.EffectiveUrl));
            if (o.EffectiveUrl != o.Url && DlHttp.IsAllowedScheme(o.Url)) req.Set("mirrors", DlJson.Strings(new[] { o.Url }));
            if (o.BaseName.Length > 0) req.Set("name", DlJson.S(o.BaseName));
            req.Set("referrer", DlJson.S(o.Referrer));
            req.Set("pageUrl", DlJson.S(o.PageUrl));
            req.Set("userAgent", DlJson.S(o.UserAgent));
            req.Set("cookies", DlJson.S(o.Cookies));
            req.Set("source", DlJson.S(Browser));
            req.Set("allowDuplicate", DlJson.B(true));     // человек сам нажал «скачать» ещё раз
            req.Set("intercepted", DlJson.B(true));
            // Отказ возвращает загрузку браузеру само расширение (по ответу, а если порт оборвался — по обрыву), поэтому
            // хост giveBack здесь не толкает: иначе файл скачался бы в браузере дважды. Ответ укладывается в 15 с ожидания
            // commit у расширения — позже оно уже вернуло загрузку браузеру, и добавлять её в список нельзя.
            string why = _backend.EnsureAgent();
            JVal resp = why == null ? _backend.Agent(req) : null;
            if (resp == null || !DlJson.Bool(resp, "ok", false))
            {
                string error = why ?? (resp == null ? "agent does not answer" : DlJson.Str(resp, "error", "add failed"));
                DlLog.Write("native host: commit failed, the extension gives the download back: " + error);
                return Fail(reqId, "agent_unavailable", error);
            }
            JVal ok = Ok(reqId);
            ok.Set("id", DlJson.S(DlJson.Str(resp, "id", "")));
            return ok;
        }

        // true — торрент в списке (добавлен сейчас или был раньше) и копию браузера можно убрать.
        private bool BrowserTorrent(string path)
        {
            // Путь пришёл от расширения: только существующий .torrent по абсолютному пути, не ссылка.
            string file = BtAssoc.ValidateOpenArgument(path);
            if (file == null || file.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                DlLog.Write("native host: browser file refused: not a .torrent file");
                return false;
            }
            string why = _backend.EnsureAgent();
            JVal req = DlClient.Command("addTorrent");
            req.Set("file", DlJson.S(file));
            req.Set("source", DlJson.S(Browser));
            JVal resp = why == null ? _backend.Agent(req) : null;
            bool listed = resp != null && (DlJson.Bool(resp, "ok", false) || DlJson.Str(resp, "duplicateOf", "").Length > 0);
            if (!listed)
            {
                DlLog.Write("native host: browser torrent not added: " + (why ?? (resp == null ? "agent does not answer" : DlJson.Str(resp, "error", "add failed"))));
                return false;
            }
            return _backend.LoadSettings().BtRecycleTorrentFile;
        }

        private JVal Add(string reqId, JVal msg)
        {
            List<string> urls = DlJson.StrList(msg, "urls");
            JVal cookies = msg.Get("cookiesByUrl");
            if (urls.Count > 500) urls.RemoveRange(500, urls.Count - 500);
            string why = _backend.EnsureAgent();
            if (why != null) return Fail(reqId, "agent_unavailable", why);
            int added = 0, skipped = 0;
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string url in urls)
            {
                if (!DlHttp.IsAllowedScheme(url) || !seen.Add(url)) { skipped++; continue; }
                JVal req = DlClient.Command("add");
                req.Set("url", DlJson.S(url));
                req.Set("referrer", DlJson.S(DlJson.Str(msg, "referrer", "")));
                req.Set("pageUrl", DlJson.S(DlJson.Str(msg, "pageUrl", "")));
                req.Set("userAgent", DlJson.S(DlJson.Str(msg, "ua", "")));
                req.Set("cookies", DlJson.S(cookies != null && cookies.Kind == JKind.Obj ? DlJson.Str(cookies, url, "") : ""));
                req.Set("source", DlJson.S(Browser));
                JVal resp = _backend.Agent(req);
                if (resp != null && DlJson.Bool(resp, "ok", false)) added++;
                else skipped++;
            }
            JVal o = Ok(reqId);
            o.Set("added", DlJson.N(added));
            o.Set("skipped", DlJson.N(skipped));
            return o;
        }

        // Видео со страницы: расширение нашло поток (HLS/DASH), файл или предлагает отдать саму страницу yt-dlp.
        // Cookies берём только те, что прислало расширение для ЭТОГО адреса: у потока обычно чужой хост (CDN).
        private JVal AddMedia(string reqId, JVal msg)
        {
            string url = DlJson.Str(msg, "url", "");
            if (!DlHttp.IsAllowedScheme(url)) return Fail(reqId, "bad_request", "unsupported scheme");
            string why = _backend.EnsureAgent();
            if (why != null) return Fail(reqId, "agent_unavailable", why);
            JVal req = DlClient.Command("addMedia");
            req.Set("url", DlJson.S(url));
            req.Set("kind", DlJson.S(DlJson.Str(msg, "kind", "")));
            req.Set("title", DlJson.S(DlJson.Str(msg, "title", "")));
            req.Set("referrer", DlJson.S(DlJson.Str(msg, "referrer", "")));
            req.Set("pageUrl", DlJson.S(DlJson.Str(msg, "pageUrl", "")));
            req.Set("userAgent", DlJson.S(DlJson.Str(msg, "ua", "")));
            req.Set("cookies", DlJson.S(DlJson.Str(msg, "cookies", "")));
            req.Set("source", DlJson.S(Browser));
            JVal resp = _backend.Agent(req);
            if (resp == null) return Fail(reqId, "agent_unavailable", "no answer from the app");
            if (!DlJson.Bool(resp, "ok", false)) return Fail(reqId, "rejected", DlJson.Str(resp, "error", ""));
            JVal o = Ok(reqId);
            o.Set("id", DlJson.S(DlJson.Str(resp, "id", "")));
            return o;
        }

        private JVal Status(string reqId)
        {
            JVal o = Ok(reqId);
            int active = 0, queued = 0;
            long speed = 0;
            bool running = _backend.AgentRunning();
            JVal list = running ? _backend.Agent(DlClient.Command("list")) : null;
            JVal items = list == null ? null : list.Get("items");
            if (items != null && items.Kind == JKind.Arr)
                foreach (JVal it in items.V)
                {
                    string state = DlJson.Str(it, "State", "");
                    if (state == "Active") { active++; speed += DlJson.Long(it, "SpeedBps", 0); }
                    else if (state == "Queued" || state == "Waiting" || state == "Scheduled") queued++;
                }
            o.Set("active", DlJson.N(active));
            o.Set("queued", DlJson.N(queued));
            o.Set("speedBps", DlJson.N(speed));
            o.Set("agentRunning", DlJson.B(running));
            return o;
        }

        // Переключатель во всплывающем окне и «не перехватывать на этом сайте». Процесс загрузок работает — через него
        // (он сохранит и применит), нет — прямо в файл настроек, как это делает окно программы.
        private JVal ChangeRules(string reqId, JVal msg, bool host)
        {
            DlSettings s = (_backend.LoadSettings() ?? new DlSettings()).Clone();
            bool on = DlJson.Bool(msg, "on", false);
            if (host)
            {
                string h = DlJson.Str(msg, "host", "").Trim().ToLowerInvariant();
                if (h.Length == 0 || h.Length > 253 || h.IndexOfAny(new[] { '/', '\\', ' ', ':' }) >= 0) return Fail(reqId, "bad_request", "bad host");
                if (on && !s.BrowserSkipHosts.Contains(h)) s.BrowserSkipHosts.Add(h);
                if (!on) s.BrowserSkipHosts.Remove(h);
            }
            else s.BrowserIntercept = on;
            bool viaAgent = false;
            if (_backend.AgentRunning())
            {
                JVal req = DlClient.Command("setSettings");
                req.Set("settings", s.ToJson());
                JVal resp = _backend.Agent(req);
                viaAgent = resp != null && DlJson.Bool(resp, "ok", false);
            }
            if (!viaAgent) _backend.SaveSettings(s);
            lock (_gate)
            {
                _settings = s;
                _settingsStamp = _backend.SettingsStamp();
            }
            JVal o = Ok(reqId);
            o.Set("rules", DlBridgeRules.ToJson(s));
            return o;
        }

        private DlSettings Settings()
        {
            DateTime stamp = _backend.SettingsStamp();
            lock (_gate)
            {
                if (stamp != _settingsStamp)
                {
                    _settingsStamp = stamp;
                    _settings = _backend.LoadSettings() ?? new DlSettings();
                }
                return _settings;
            }
        }

        // ---------- толчки: правила изменились, программа просит вернуть загрузку браузеру или свежие cookies ----------
        private void PollLoop()
        {
            DateTime stamp;
            lock (_gate) stamp = _settingsStamp;
            while (!_stop.WaitOne(PollMs))
            {
                try
                {
                    DateTime before = stamp;
                    DlSettings s = Settings();
                    lock (_gate) stamp = _settingsStamp;
                    if (stamp != before)
                    {
                        JVal push = PushOf("rules");
                        push.Set("rules", DlBridgeRules.ToJson(s));
                        Push(push);
                    }
                    ExpirePending();
                    if (!_backend.AgentRunning()) continue;
                    JVal req = DlClient.Command("bridgePending");
                    req.Set("browser", DlJson.S(Browser));
                    JVal resp = _backend.Agent(req);
                    JVal tasks = resp == null ? null : resp.Get("tasks");
                    if (tasks == null || tasks.Kind != JKind.Arr) continue;
                    foreach (JVal t in tasks.V)
                    {
                        string kind = DlJson.Str(t, "kind", "");
                        if (kind == "giveBack")
                            Push(GiveBack(DlJson.Str(t, "url", ""), DlJson.Str(t, "referrer", ""), DlJson.Str(t, "filename", "")));
                        else if (kind == "needCookies")
                        {
                            JVal push = PushOf("needCookies");
                            push.Set("url", DlJson.S(DlJson.Str(t, "url", "")));
                            lock (_gate) _cookieAsks[DlJson.Str(push, "pushId", "")] = DlJson.Str(t, "id", "") + "\n" + DlJson.Str(t, "url", "");
                            Push(push);
                        }
                    }
                }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private void ExpirePending()
        {
            lock (_gate)
            {
                List<string> old = new List<string>();
                foreach (KeyValuePair<string, Pending> kv in _pending)
                    if ((DateTime.UtcNow - kv.Value.Utc).TotalSeconds > PendingSeconds) old.Add(kv.Key);
                foreach (string k in old) _pending.Remove(k);
            }
        }

        private void OnCookies(JVal msg)
        {
            string ask;
            lock (_gate)
            {
                string pushId = DlJson.Str(msg, "pushId", "");
                if (!_cookieAsks.TryGetValue(pushId, out ask)) return;
                _cookieAsks.Remove(pushId);
            }
            string cookies = DlJson.Str(msg, "cookies", "");
            int nl = ask.IndexOf('\n');
            if (cookies.Length == 0 || nl <= 0) return;
            JVal req = DlClient.Command("refreshLink");
            req.Set("id", DlJson.S(ask.Substring(0, nl)));
            req.Set("url", DlJson.S(ask.Substring(nl + 1)));
            req.Set("cookies", DlJson.S(cookies));
            ThreadPool.QueueUserWorkItem(delegate { _backend.Agent(req); });
        }

        private JVal GiveBack(string url, string referrer, string filename)
        {
            JVal push = PushOf("giveBack");
            push.Set("url", DlJson.S(url));
            push.Set("referrer", DlJson.S(referrer));
            push.Set("filename", DlJson.S(filename));
            return push;
        }

        private JVal PushOf(string type)
        {
            JVal o = JVal.NewObj();
            o.Set("type", DlJson.S(type));
            o.Set("pushId", DlJson.S("p" + Interlocked.Increment(ref _pushSeq).ToString(CultureInfo.InvariantCulture)));
            return o;
        }

        private void Push(JVal push) { Send(push); }

        // ---------- кадры ----------
        private static JVal Ok(string reqId)
        {
            JVal o = JVal.NewObj();
            o.Set("reqId", DlJson.S(reqId));
            o.Set("ok", DlJson.B(true));
            return o;
        }

        private static JVal Fail(string reqId, string code, string text)
        {
            JVal o = JVal.NewObj();
            o.Set("reqId", DlJson.S(reqId));
            o.Set("ok", DlJson.B(false));
            JVal e = JVal.NewObj();
            e.Set("code", DlJson.S(code));
            e.Set("msg", DlJson.S(text ?? ""));
            o.Set("error", e);
            return o;
        }

        // Больше 1 МБ браузер не примет и разорвёт порт — такое сообщение не отправляется вовсе.
        internal void Send(JVal message)
        {
            if (message == null) return;
            byte[] body = new UTF8Encoding(false).GetBytes(Jsn.Write(message));
            if (body.Length > DlBridge.MaxToBrowser)
            {
                DlLog.Write("native host: message over 1 MB dropped: " + DlJson.Str(message, "type", DlJson.Str(message, "reqId", "")));
                return;
            }
            byte[] head = BitConverter.GetBytes(body.Length);   // порядок байт платформы — так требует native messaging
            lock (_writeGate)
            {
                try
                {
                    _out.Write(head, 0, 4);
                    _out.Write(body, 0, body.Length);
                    _out.Flush();
                }
                catch (Exception ex) { DlLog.Write("native host: write failed: " + ex.Message); }
            }
        }
    }
}
