// Windows Process Cleaner — область «downloads»: мост к браузерам. Этот же тестовый exe запускается так, как его запускает
// браузер: Chromium — «cmd.exe /d /s /c ""exe" chrome-extension://id/ --parent-window=0" < канал > канал», Firefox —
// «exe <манифест> <id дополнения>» с перенаправленными stdin/stdout; путь к exe берётся из манифеста, на который указывает
// раздел реестра, записанный DlBrowsers.Register. Тест играет роль расширения: пишет и читает кадры сам, своим кодом.
//
// Ненастоящее здесь: сеть — локальный HTTP-сервер; «процесс загрузок» — движок и DlPipeServer внутри тестового процесса
// (под тем же мьютексом и именем канала, что увидит хост); реестр — раздел HKCU\Software\WPC-Tests\<pid>, настоящие ключи
// NativeMessagingHosts не пишутся (проверяется).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class DownloadsTests
    {
        private const string ChromiumId = "kfbocmoigekddjcfodbhbiahndahdmal";
        private const string GeckoId = "wpc-downloads@windows-process-cleaner";
        private static readonly string[] RealHostKeys =
        {
            @"Software\Google\Chrome\NativeMessagingHosts\org.wpc.downloads",
            @"Software\Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads",
            @"Software\Mozilla\NativeMessagingHosts\org.wpc.downloads",
        };

        // Сторона расширения: кадры native messaging (4 байта длины в порядке x86 + UTF-8 JSON), написанные здесь заново.
        private sealed class BrowserPort
        {
            private readonly Stream _toHost, _fromHost;
            private readonly List<JVal> _got = new List<JVal>();
            private bool _eof;
            public int BytesRead;

            public BrowserPort(Stream toHost, Stream fromHost)
            {
                _toHost = toHost;
                _fromHost = fromHost;
                Thread t = new Thread(Reader);
                t.IsBackground = true;
                t.Start();
            }

            private void Reader()
            {
                try
                {
                    while (true)
                    {
                        byte[] head = ReadAll(4);
                        if (head == null) break;
                        int len = head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24;
                        if (len <= 0 || len > 1024 * 1024) break;
                        byte[] body = ReadAll(len);
                        if (body == null) break;
                        JVal v = Jsn.Parse(Encoding.UTF8.GetString(body));
                        lock (_got)
                        {
                            _got.Add(v);
                            Monitor.PulseAll(_got);
                        }
                    }
                }
                catch { }
                lock (_got)
                {
                    _eof = true;
                    Monitor.PulseAll(_got);
                }
            }

            private byte[] ReadAll(int count)
            {
                byte[] buf = new byte[count];
                int got = 0;
                while (got < count)
                {
                    int n = _fromHost.Read(buf, got, count - got);
                    if (n <= 0) return null;
                    got += n;
                    Interlocked.Add(ref BytesRead, n);
                }
                return buf;
            }

            public void Send(JVal msg)
            {
                byte[] body = Encoding.UTF8.GetBytes(Jsn.Write(msg));
                byte[] frame = new byte[4 + body.Length];
                frame[0] = (byte)body.Length;
                frame[1] = (byte)(body.Length >> 8);
                frame[2] = (byte)(body.Length >> 16);
                frame[3] = (byte)(body.Length >> 24);
                Buffer.BlockCopy(body, 0, frame, 4, body.Length);
                _toHost.Write(frame, 0, frame.Length);
                _toHost.Flush();
            }

            // Первый пришедший кадр, подходящий под условие (забирается); null — не пришёл за ms или хост закрыл поток.
            public JVal Wait(Func<JVal, bool> match, int ms)
            {
                Stopwatch clock = Stopwatch.StartNew();
                lock (_got)
                {
                    while (true)
                    {
                        for (int i = 0; i < _got.Count; i++)
                            if (_got[i] != null && match(_got[i]))
                            {
                                JVal v = _got[i];
                                _got.RemoveAt(i);
                                return v;
                            }
                        long left = ms - clock.ElapsedMilliseconds;
                        if (_eof || left <= 0) return null;
                        Monitor.Wait(_got, (int)left);
                    }
                }
            }

            public JVal Push(string type, int ms)
            {
                return Wait(delegate(JVal m) { return DlJson.Str(m, "type", "") == type; }, ms);
            }

            public JVal Request(JVal msg, int ms)
            {
                string reqId = DlJson.Str(msg, "reqId", "");
                Send(msg);
                return Wait(delegate(JVal m) { return DlJson.Str(m, "reqId", "") == reqId && DlJson.Str(m, "type", "") == ""; }, ms);
            }

            public bool Eof(int ms)
            {
                Stopwatch clock = Stopwatch.StartNew();
                lock (_got)
                {
                    while (!_eof && clock.ElapsedMilliseconds < ms) Monitor.Wait(_got, 100);
                    return _eof;
                }
            }

            public void CloseInput()
            {
                try { _toHost.Dispose(); } catch { }
            }
        }

        private sealed class LaunchedHost
        {
            public Process Process;
            public BrowserPort Port;
            public NamedPipeServerStream In, Out;

            public int ExitCode(int ms)
            {
                if (Process == null) return -100;
                if (!Process.WaitForExit(ms))
                {
                    try { Process.Kill(); } catch { }
                    return -101;
                }
                return Process.ExitCode;
            }

            public void Dispose()
            {
                if (Port != null) Port.CloseInput();
                if (Process != null)
                {
                    try { if (!Process.WaitForExit(5000)) Process.Kill(); } catch { }
                    Process.Dispose();
                }
                try { if (In != null) In.Dispose(); } catch { }
                try { if (Out != null) Out.Dispose(); } catch { }
            }
        }

        private static int _reqSeq;

        private static JVal Msg(string type)
        {
            JVal o = JVal.NewObj();
            o.Set("type", DlJson.S(type));
            o.Set("reqId", DlJson.S("r" + Interlocked.Increment(ref _reqSeq)));
            return o;
        }

        private static JVal OfferMsg(string url, string filename, string mime, long total, string cookies)
        {
            JVal o = Msg("offer");
            o.Set("url", DlJson.S(url));
            o.Set("finalUrl", DlJson.S(url));
            o.Set("referrer", DlJson.S("http://127.0.0.1/page.html"));
            o.Set("pageUrl", DlJson.S("http://127.0.0.1/page.html"));
            o.Set("filename", DlJson.S(filename));
            o.Set("mime", DlJson.S(mime));
            o.Set("totalBytes", DlJson.N(total));
            o.Set("cookies", DlJson.S(cookies));
            o.Set("ua", DlJson.S("Mozilla/5.0 test"));
            o.Set("incognito", DlJson.B(false));
            o.Set("byExtension", DlJson.B(false));
            return o;
        }

        private static string ActionOf(JVal reply) { return reply == null ? "<no reply>" : DlJson.Str(reply, "action", "<" + Jsn.Write(reply) + ">"); }

        // Как Chromium (native_process_launcher_win.cc): два канала chrome.nativeMessaging.{in,out}.<tag> и cmd.exe с
        // перенаправлением; exe и аргументы — внутри внешних кавычек, перенаправление — за ними.
        private static LaunchedHost LaunchLikeChromium(string exe, string origin)
        {
            LaunchedHost h = new LaunchedHost();
            string tag = Guid.NewGuid().ToString("N").Substring(0, 16);
            string inName = "chrome.nativeMessaging.in." + tag, outName = "chrome.nativeMessaging.out." + tag;
            h.In = new NamedPipeServerStream(inName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            h.Out = new NamedPipeServerStream(outName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            IAsyncResult a = h.In.BeginWaitForConnection(null, null);
            IAsyncResult b = h.Out.BeginWaitForConnection(null, null);
            string comspec = Environment.GetEnvironmentVariable("ComSpec");
            if (string.IsNullOrEmpty(comspec)) comspec = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            string line = "/d /s /c \"\"" + exe + "\" " + origin + " --parent-window=0\" < \\\\.\\pipe\\" + inName + " > \\\\.\\pipe\\" + outName;
            ProcessStartInfo psi = new ProcessStartInfo(comspec, line);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            h.Process = Process.Start(psi);
            if (!a.AsyncWaitHandle.WaitOne(15000) || !b.AsyncWaitHandle.WaitOne(15000)) return h;
            h.In.EndWaitForConnection(a);
            h.Out.EndWaitForConnection(b);
            h.Port = new BrowserPort(h.In, h.Out);
            return h;
        }

        private static LaunchedHost LaunchLikeFirefox(string exe, string manifest, string addonId)
        {
            LaunchedHost h = new LaunchedHost();
            ProcessStartInfo psi = new ProcessStartInfo(exe, "\"" + manifest + "\" " + addonId);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            h.Process = Process.Start(psi);
            h.Port = new BrowserPort(h.Process.StandardInput.BaseStream, h.Process.StandardOutput.BaseStream);
            return h;
        }

        private static string TestSoftwareKey() { return @"Software\WPC-Tests\" + Process.GetCurrentProcess().Id + "-" + (++_n); }

        private static void DeleteTestKey(string sub)
        {
            try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
            try
            {
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software\WPC-Tests", true))
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0) { parent.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\WPC-Tests", false); }
            }
            catch { }
        }

        private static string RealHostKeysSnapshot()
        {
            StringBuilder sb = new StringBuilder();
            foreach (string k in RealHostKeys)
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(k))
                    sb.Append(k).Append('=').Append(key == null ? "<none>" : (key.GetValue("") as string ?? "<empty>")).Append('\n');
            return sb.ToString();
        }

        // Как браузер находит хост: раздел → файл манифеста → «path» и список разрешённых расширений.
        private static string ResolveLikeBrowser(RegistryKey software, string subKey, string allowField, string allowValue, out string manifest)
        {
            manifest = null;
            using (RegistryKey k = software.OpenSubKey(subKey))
            {
                manifest = k == null ? null : k.GetValue("") as string;
                if (string.IsNullOrEmpty(manifest) || !File.Exists(manifest)) return null;
            }
            JVal m = Jsn.Parse(File.ReadAllText(manifest, Encoding.UTF8));
            if (DlJson.Str(m, "name", "") != "org.wpc.downloads" || DlJson.Str(m, "type", "") != "stdio") return null;
            if (!DlJson.StrList(m, allowField).Contains(allowValue)) return null;
            string path = DlJson.Str(m, "path", "");
            return Path.IsPathRooted(path) && File.Exists(path) ? path : null;
        }

        private static bool TreeContains(string dir, string secret, out string where)
        {
            where = null;
            if (!Directory.Exists(dir)) return false;
            byte[] needle = Encoding.UTF8.GetBytes(secret);
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                byte[] bytes;
                try { bytes = File.ReadAllBytes(f); }
                catch { continue; }
                for (int i = 0; i + needle.Length <= bytes.Length; i++)
                {
                    int j = 0;
                    while (j < needle.Length && bytes[i + j] == needle[j]) j++;
                    if (j == needle.Length) { where = f; return true; }
                }
            }
            return false;
        }

        // ------------------------------------------------------------------ //
        //  Хост, запущенный как браузер, против настоящего движка и сервера
        // ------------------------------------------------------------------ //
        private static void Bridge(DlTestServer srv, List<string> recycled)
        {
            // Разбор запуска: браузер или обычный запуск программы.
            string fam, id;
            T.Check("bridge: Chromium launch args are recognised", DlBridge.TryParse(new[] { "chrome-extension://" + ChromiumId + "/", "--parent-window=0" }, out fam, out id) && fam == "chromium" && id == ChromiumId, fam + " " + id);
            T.Check("bridge: Firefox launch args are recognised", DlBridge.TryParse(new[] { @"C:\x\org.wpc.downloads.firefox.json", GeckoId }, out fam, out id) && fam == "firefox" && id == GeckoId, fam + " " + id);
            T.Check("bridge: ordinary app args are not a host launch", !DlBridge.TryParse(new[] { "/tray" }, out fam, out id) && !DlBridge.TryParse(new string[0], out fam, out id)
                    && !DlBridge.TryParse(new[] { @"C:\x\a.json" }, out fam, out id) && !DlBridge.TryParse(new[] { @"C:\x\a.json", "not an id@x y" }, out fam, out id));
            T.Check("bridge: only our own extension ids are allowed", DlBridge.IsAllowed("chromium", ChromiumId) && DlBridge.IsAllowed("firefox", GeckoId)
                    && !DlBridge.IsAllowed("chromium", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa") && !DlBridge.IsAllowed("firefox", ChromiumId) && !DlBridge.IsAllowed("chromium", GeckoId));

            string exeSelf = Process.GetCurrentProcess().MainModule.FileName;
            string sub = TestSoftwareKey();
            string manifestDir = Dir("bridge-nmh");
            string realBefore = RealHostKeysSnapshot();
            string folder = Dir("bridge");
            string store = Dir("bridge-store");
            DlSettings settings = NewSettings(folder);
            settings.Segments = 1;
            settings.Save();
            foreach (string b in DlBridge.Browsers) try { File.Delete(DlBridgeSeen.FileFor(b)); } catch { }

            bool first;
            Mutex instance = DlIpc.CreateInstanceMutex(out first);
            DlPipeServer server = null;
            try
            {
                T.Check("bridge: the test owns the download-process mutex of its channel", first && instance != null);
                string edgeManifest, ffManifest, chromiumExe, firefoxExe;
                using (RegistryKey software = Registry.CurrentUser.CreateSubKey(sub))
                {
                    string reg = DlBrowsers.Register(software, manifestDir, exeSelf);
                    T.Check("bridge: host registered under the test key", reg == null, reg);
                    chromiumExe = ResolveLikeBrowser(software, @"Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads", "allowed_origins", "chrome-extension://" + ChromiumId + "/", out edgeManifest);
                    firefoxExe = ResolveLikeBrowser(software, @"Mozilla\NativeMessagingHosts\org.wpc.downloads", "allowed_extensions", GeckoId, out ffManifest);
                }
                T.Check("bridge: Edge finds the host exe through registry and manifest", chromiumExe != null && string.Equals(chromiumExe, exeSelf, StringComparison.OrdinalIgnoreCase), edgeManifest);
                T.Check("bridge: Firefox finds the host exe through registry and manifest", firefoxExe != null && string.Equals(firefoxExe, exeSelf, StringComparison.OrdinalIgnoreCase), ffManifest);
                if (chromiumExe == null || firefoxExe == null || !first) return;

                using (DlEngine e = NewEngine(store, settings, new FakeDlEnv()))
                {
                    DlCommands commands = new DlCommands(e, delegate { }, null);
                    e.Notice = commands.OnNotice;
                    List<DlNotice> intercepted = new List<DlNotice>();
                    commands.Intercepted = delegate(DlNotice n) { lock (intercepted) intercepted.Add(n); };
                    server = new DlPipeServer(DlIpc.PipeName, commands.Handle, DlIpc.IsOwnImage);
                    server.Start();
                    BridgeChromium(srv, e, commands, intercepted, recycled, ref server, chromiumExe, folder, store);
                }

                // Чужое расширение: хост выходит с кодом 2 и не пишет ни байта.
                LaunchedHost foreign = LaunchLikeChromium(chromiumExe, "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/");
                try
                {
                    int code = foreign.ExitCode(15000);
                    bool silent = foreign.Port != null && foreign.Port.Eof(3000) && foreign.Port.BytesRead == 0;
                    T.Check("bridge: a foreign extension id gets exit code 2 and no bytes", code == 2 && silent, "exit " + code);
                }
                finally { foreign.Dispose(); }

                // Firefox: exe запускается напрямую, браузер указан в hello неверно — хост знает семейство сам.
                LaunchedHost ff = LaunchLikeFirefox(firefoxExe, ffManifest, GeckoId);
                try
                {
                    JVal hello = Msg("hello");
                    hello.Set("browser", DlJson.S("chrome"));
                    hello.Set("extVersion", DlJson.S("1.2.3"));
                    JVal r = ff.Port.Request(hello, 15000);
                    T.Check("bridge: Firefox host answers hello", r != null && DlJson.Bool(r, "ok", false) && DlJson.Int(r, "v", 0) == 1, r == null ? "null" : Jsn.Write(r));
                    string ver;
                    DateTime seen = DlBridgeSeen.LastSeen("firefox", out ver);
                    T.Check("bridge: a Firefox host records itself as firefox whatever hello claims", seen != DateTime.MinValue && ver == "1.2.3" && !File.Exists(DlBridgeSeen.FileFor("chrome")), ver);
                    ff.Port.CloseInput();
                    T.Check("bridge: Firefox host exits 0 when the browser closes the port", ff.ExitCode(10000) == 0);
                }
                finally { ff.Dispose(); }
            }
            finally
            {
                if (server != null) server.Dispose();
                if (instance != null)
                {
                    try { instance.ReleaseMutex(); } catch { }
                    instance.Dispose();
                }
                DeleteTestKey(sub);
                T.Check("bridge: the real NativeMessagingHosts keys were not touched", RealHostKeysSnapshot() == realBefore, realBefore);
            }
        }

        private static void BridgeChromium(DlTestServer srv, DlEngine e, DlCommands commands, List<DlNotice> intercepted, List<string> recycled,
                                           ref DlPipeServer server, string exe, string folder, string store)
        {
            LaunchedHost host = LaunchLikeChromium(exe, "chrome-extension://" + ChromiumId + "/");
            try
            {
                T.Check("bridge: Chromium-style launch through cmd.exe connects both pipes", host.Port != null);
                if (host.Port == null) return;
                BrowserPort port = host.Port;

                JVal hello = Msg("hello");
                hello.Set("browser", DlJson.S("edge"));
                hello.Set("extVersion", DlJson.S("1.0.0"));
                JVal hr = port.Request(hello, 15000);
                JVal rules = hr == null ? null : hr.Get("rules");
                T.Check("bridge: hello answers protocol 1 with the rules", hr != null && DlJson.Bool(hr, "ok", false) && DlJson.Int(hr, "v", 0) == 1 && rules != null
                        && DlJson.Bool(rules, "enabled", false) && DlJson.Bool(rules, "catchAll", false) && DlJson.Int(rules, "holdMs", 0) == 5000, hr == null ? "null" : Jsn.Write(hr));
                string ver;
                T.Check("bridge: the browser from hello is recorded for the settings page", DlBridgeSeen.LastSeen("edge", out ver) != DateTime.MinValue && ver == "1.0.0", ver);

                JVal noReq = JVal.NewObj();
                noReq.Set("type", DlJson.S("status"));
                port.Send(noReq);
                JVal bad = port.Wait(delegate(JVal m) { return DlJson.Str(m, "reqId", "x") == "" && !DlJson.Bool(m, "ok", true); }, 5000);
                T.Check("bridge: a request without reqId is refused", bad != null && DlJson.Str(bad.Get("error"), "code", "") == "bad_request");
                JVal unknown = port.Request(Msg("runFile"), 5000);
                T.Check("bridge: an unknown message type is refused", unknown != null && !DlJson.Bool(unknown, "ok", true) && DlJson.Str(unknown.Get("error"), "code", "") == "unknown_type");
                JVal status = port.Request(Msg("status"), 8000);
                T.Check("bridge: status sees the running download process", status != null && DlJson.Bool(status, "agentRunning", false), status == null ? "null" : Jsn.Write(status));

                // Правила из файла настроек: изменились — хост сам присылает новые.
                DlSettings off = NewSettings(folder);
                off.Segments = 1;
                off.BrowserIntercept = false;
                Thread.Sleep(30);
                off.Save();
                JVal push = port.Push("rules", 8000);
                T.Check("bridge: turning interception off in settings pushes new rules", push != null && push.Get("rules") != null && !DlJson.Bool(push.Get("rules"), "enabled", true));
                DlTestServer.Res any = srv.Add("bridge-any.bin", new DlTestServer.Res());
                any.Size = 1000; any.Seed = 61;
                JVal declined = port.Request(OfferMsg(srv.Url("bridge-any.bin"), "bridge-any.bin", "application/octet-stream", 1000, ""), 8000);
                T.Check("bridge: with interception off the offer is declined", ActionOf(declined) == "decline" && DlJson.Str(declined, "reason", "") == "disabled", ActionOf(declined));
                off.BrowserIntercept = true;
                Thread.Sleep(30);
                off.Save();
                push = port.Push("rules", 8000);
                T.Check("bridge: turning it back on pushes enabled rules", push != null && DlJson.Bool(push.Get("rules"), "enabled", false));

                JVal blob = OfferMsg("blob:http://127.0.0.1/1234", "x.bin", "", -1, "");
                T.Eq("bridge: a blob: download is left to the browser", "decline", ActionOf(port.Request(blob, 8000)));
                JVal own = OfferMsg(srv.Url("bridge-any.bin"), "bridge-any.bin", "", 1000, "");
                own.Set("byExtension", DlJson.B(true));
                T.Eq("bridge: a download the extension itself started is never taken", "decline", ActionOf(port.Request(own, 8000)));
                JVal incognito = OfferMsg(srv.Url("bridge-any.bin"), "bridge-any.bin", "", 1000, "");
                incognito.Set("incognito", DlJson.B(true));
                JVal ir = port.Request(incognito, 8000);
                T.Check("bridge: incognito is not taken by default", ActionOf(ir) == "decline" && DlJson.Str(ir, "reason", "") == "incognito", ActionOf(ir));
                T.Eq("bridge: declines above never reached the server", 0, any.Requests);

                // Пробный запрос: сервер отдаёт страницу вместо файла, другой размер, торрент по битой ссылке.
                DlTestServer.Res html = srv.Add("bridge-login", new DlTestServer.Res());
                html.Size = 3000; html.Seed = 62; html.ContentType = "text/html; charset=utf-8";
                JVal hrr = port.Request(OfferMsg(srv.Url("bridge-login"), @"C:\Users\u\Downloads\setup.exe", "application/octet-stream", 3000, ""), 10000);
                T.Check("bridge: a login page instead of the file is declined after one probe request", ActionOf(hrr) == "decline" && DlJson.Str(hrr, "reason", "").StartsWith("probe: html")
                        && html.Requests == 1 && html.RangesSeen[0] == "bytes=0-0", ActionOf(hrr) + " " + DlJson.Str(hrr, "reason", "") + " requests " + html.Requests);
                JVal sizeR = port.Request(OfferMsg(srv.Url("bridge-any.bin"), "bridge-any.bin", "application/octet-stream", 999, ""), 10000);
                T.Check("bridge: a size different from the browser's is declined", ActionOf(sizeR) == "decline" && DlJson.Str(sizeR, "reason", "").StartsWith("probe: size"), DlJson.Str(sizeR, "reason", ""));
                JVal torrent = port.Request(OfferMsg(srv.Url("gone.torrent"), "gone.torrent", "application/x-bittorrent", 500, ""), 10000);
                T.Eq("bridge: a .torrent the probe cannot fetch is watched in the browser", "watch", ActionOf(torrent));
                T.Eq("bridge: an ordinary file the probe cannot fetch is declined", "decline", ActionOf(port.Request(OfferMsg(srv.Url("gone.zip"), "gone.zip", "", 500, ""), 10000)));

                // Забрать: cookies из браузера доходят до пробы и до самой загрузки, но не до диска.
                const string Secret = "sid=SECRET-7f3a9c";
                DlTestServer.Res auth = srv.Add("bridge-auth.bin", new DlTestServer.Res());
                auth.Size = 300 * 1024; auth.Seed = 63; auth.RequireCookie = Secret;
                JVal noCookies = port.Request(OfferMsg(srv.Url("bridge-auth.bin"), "bridge-auth.bin", "", auth.Size, ""), 10000);
                T.Eq("bridge: without the browser's cookies the probe is refused and the download left", "decline", ActionOf(noCookies));
                JVal offer = OfferMsg(srv.Url("bridge-auth.bin"), @"C:\Users\u\Downloads\bridge-auth.bin", "application/octet-stream", auth.Size, Secret + "; theme=dark");
                JVal accepted = port.Request(offer, 10000);
                T.Eq("bridge: with the cookies the offer is accepted", "accept", ActionOf(accepted));
                JVal commit = Msg("commit");
                commit.Set("reqId", offer.Get("reqId"));
                int before = e.Ids().Count;
                JVal committed = port.Request(commit, 15000);
                string newId = DlJson.Str(committed, "id", "");
                T.Check("bridge: commit adds the download and returns its id", committed != null && DlJson.Bool(committed, "ok", false) && DlItem.IsValidId(newId) && e.Ids().Count == before + 1,
                        committed == null ? "null" : Jsn.Write(committed));
                JVal again = port.Request(commit, 8000);
                T.Check("bridge: the same offer cannot be committed twice", again != null && !DlJson.Bool(again, "ok", true) && e.Ids().Count == before + 1);
                bool done = WaitFor(delegate { return StateOf(e, newId) == DlState.Completed; }, 15000);
                DlItem got = e.Find(newId);
                T.Check("bridge: the taken download completes with the right bytes", done && got != null && FileHash(got.TargetPath) == DlTestServer.Sha256Of(63, auth.Size), Info(e, newId));
                T.Check("bridge: it keeps the browser's file name and source", got != null && got.FileName == "bridge-auth.bin" && got.Source == "edge" && got.Referrer == "http://127.0.0.1/page.html",
                        got == null ? "null" : got.FileName + " " + got.Source);
                DlNotice note = null;
                lock (intercepted) if (intercepted.Count > 0) note = intercepted[intercepted.Count - 1];
                T.Check("bridge: the app announces the interception with a give-back action", note != null && note.Id == newId && note.Kind == DlNoticeKind.Intercepted && note.GiveBack != null && note.Source == "edge");
                string where;
                bool leakStore = TreeContains(store, "SECRET-7f3a9c", out where);
                bool leakData = !leakStore && TreeContains(DlPaths.DataDir, "SECRET-7f3a9c", out where);
                T.Check("bridge: cookies never reach the store, settings or logs on disk", !leakStore && !leakData, where);

                // «Не перехватывать на этом сайте» из расширения — через процесс загрузок.
                JVal skip = Msg("skipHost");
                skip.Set("host", DlJson.S("127.0.0.1"));
                skip.Set("on", DlJson.B(true));
                JVal sk = port.Request(skip, 10000);
                T.Check("bridge: skipHost goes through the running download process", sk != null && DlJson.Bool(sk, "ok", false) && e.Settings.BrowserSkipHosts.Contains("127.0.0.1")
                        && DlJson.StrList(sk.Get("rules"), "skipHosts").Contains("127.0.0.1"));
                JVal skipped = port.Request(OfferMsg(srv.Url("bridge-any.bin"), "bridge-any.bin", "", 1000, ""), 10000);
                T.Check("bridge: a skipped site is left to the browser", ActionOf(skipped) == "decline" && DlJson.Str(skipped, "reason", "") == "host", ActionOf(skipped));
                skip.Set("reqId", DlJson.S("r" + Interlocked.Increment(ref _reqSeq)));
                skip.Set("on", DlJson.B(false));
                port.Request(skip, 10000);
                JVal evil = Msg("skipHost");
                evil.Set("host", DlJson.S("a/../b"));
                evil.Set("on", DlJson.B(true));
                JVal er = port.Request(evil, 10000);
                T.Check("bridge: a host with path characters is refused", er != null && !DlJson.Bool(er, "ok", true));

                BridgeBrowserTorrent(e, port, off);

                // «Отдать браузеру» из уведомления: загрузка уходит из списка, хост толкает giveBack.
                DlTestServer.Res slow = srv.Add("bridge-slow.bin", new DlTestServer.Res());
                slow.Size = 4 * 1024 * 1024; slow.Seed = 64; slow.RateBps = 200 * 1024;
                JVal slowOffer = OfferMsg(srv.Url("bridge-slow.bin"), "bridge-slow.bin", "", slow.Size, "");
                T.Eq("bridge: a slow file is accepted", "accept", ActionOf(port.Request(slowOffer, 10000)));
                JVal slowCommit = Msg("commit");
                slowCommit.Set("reqId", slowOffer.Get("reqId"));
                string slowId = DlJson.Str(port.Request(slowCommit, 15000), "id", "");
                WaitFor(delegate { DlItem x = e.Find(slowId); return x != null && x.DoneBytes > 50 * 1024; }, 10000);
                string why;
                int recycledBefore;
                lock (recycled) recycledBefore = recycled.Count;
                bool gave = commands.GiveBack(slowId, out why);
                JVal gb = port.Push("giveBack", 10000);
                T.Check("bridge: give-back removes the download and the host hands it to the browser", gave && e.Find(slowId) == null && gb != null
                        && DlJson.Str(gb, "url", "") == srv.Url("bridge-slow.bin") && DlJson.Str(gb, "filename", "") == "bridge-slow.bin", why + " " + (gb == null ? "no push" : Jsn.Write(gb)));
                lock (recycled) T.Check("bridge: the partial file of a given-back download goes to the Recycle Bin", recycled.Count > recycledBefore);
                string whyDone;
                T.Check("bridge: a completed download cannot be given back", !commands.GiveBack(newId, out whyDone) && e.Find(newId) != null);

                // Сессия на сайте истекла посреди загрузки: хост просит у расширения свежие cookies и обновляет ссылку.
                DlTestServer.Res session = srv.Add("bridge-session.bin", new DlTestServer.Res());
                session.Size = 1536 * 1024; session.Seed = 65; session.RateBps = 300 * 1024; session.RequireCookie = "sid=old";
                JVal sOffer = OfferMsg(srv.Url("bridge-session.bin"), "bridge-session.bin", "", session.Size, "sid=old");
                T.Eq("bridge: a session file is accepted", "accept", ActionOf(port.Request(sOffer, 10000)));
                JVal sCommit = Msg("commit");
                sCommit.Set("reqId", sOffer.Get("reqId"));
                string sId = DlJson.Str(port.Request(sCommit, 15000), "id", "");
                WaitFor(delegate { DlItem x = e.Find(sId); return x != null && x.DoneBytes > 100 * 1024; }, 10000);
                e.Pause(sId);
                WaitFor(delegate { return StateOf(e, sId) == DlState.Paused; }, 5000);
                lock (srv.Gate) session.RequireCookie = "sid=new";
                e.Resume(sId);
                JVal ask = port.Push("needCookies", 15000);
                T.Check("bridge: an expired session makes the host ask the extension for cookies", ask != null && DlJson.Str(ask, "url", "") == srv.Url("bridge-session.bin")
                        && StateOf(e, sId) == DlState.NeedsLink, ask == null ? Info(e, sId) : Jsn.Write(ask));
                if (ask != null)
                {
                    JVal answer = JVal.NewObj();
                    answer.Set("type", DlJson.S("needCookiesResult"));
                    answer.Set("pushId", ask.Get("pushId"));
                    answer.Set("cookies", DlJson.S("sid=new"));
                    port.Send(answer);
                    bool sDone = WaitFor(delegate { return StateOf(e, sId) == DlState.Completed; }, 20000);
                    T.Check("bridge: fresh cookies from the browser finish the download", sDone && FileHash(e.Find(sId).TargetPath) == DlTestServer.Sha256Of(65, session.Size), Info(e, sId));
                }

                // Процесс загрузок перестал отвечать между accept и commit: загрузка возвращается браузеру.
                DlTestServer.Res lost = srv.Add("bridge-lost.bin", new DlTestServer.Res());
                lost.Size = 2000; lost.Seed = 66;
                JVal lOffer = OfferMsg(srv.Url("bridge-lost.bin"), "bridge-lost.bin", "", lost.Size, "");
                T.Eq("bridge: an offer is accepted before the process goes away", "accept", ActionOf(port.Request(lOffer, 10000)));
                server.Dispose();
                server = null;
                int count = e.Ids().Count;
                JVal lCommit = Msg("commit");
                lCommit.Set("reqId", lOffer.Get("reqId"));
                Stopwatch lClock = Stopwatch.StartNew();
                JVal lr = port.Request(lCommit, 20000);
                long lMs = lClock.ElapsedMilliseconds;
                // Загрузку браузеру возвращает расширение по этому отказу; толчок giveBack от хоста скачал бы файл второй раз.
                JVal lgb = port.Push("giveBack", 3000);
                T.Check("bridge: a commit the download process cannot take fails within the extension's 15 s, no second give-back", lr != null && !DlJson.Bool(lr, "ok", true)
                        && DlJson.Str(lr.Get("error"), "code", "") == "agent_unavailable" && lMs < 15000 && lgb == null && e.Ids().Count == count,
                        (lr == null ? "no reply" : Jsn.Write(lr)) + " in " + lMs + " ms" + (lgb != null ? ", pushed giveBack" : ""));

                port.CloseInput();
                T.Eq("bridge: the host exits 0 when the browser closes the port", 0, host.ExitCode(15000));
            }
            finally { host.Dispose(); }
        }

        // .torrent, который скачал сам браузер: хост отдаёт путь процессу загрузок, тот добавляет торрент без диалога. Сессия
        // торрентов — только на петле, без DHT, LSD, PEX и проброса порта: за пределы машины ничего не уходит.
        private static void BridgeBrowserTorrent(DlEngine e, BrowserPort port, DlSettings fileSettings)
        {
            e.BtBind = IPAddress.Loopback;
            e.BtInboundReady = delegate { return true; };
            DlSettings bt = e.Settings.Clone();
            bt.BtDht = false;
            bt.BtLsd = false;
            bt.BtPex = false;
            bt.BtPortMapping = false;
            e.UpdateSettings(bt);
            fileSettings.BtRecycleTorrentFile = false;   // по умолчанию копию .torrent убирают; сперва проверяется обратный случай
            Thread.Sleep(30);
            fileSettings.Save();

            string dir = Dir("bridge-browser");
            byte[] torrent = BtFx.Build("browser-wire", new List<BtFxFile> { new BtFxFile(BtFx.Data(40000, 91), "a.bin") }, 16384, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string file = Path.Combine(dir, "browser-wire.torrent");
            File.WriteAllBytes(file, torrent);
            int before = e.Ids().Count;

            JVal msg = Msg("browserFile");
            msg.Set("url", DlJson.S("http://127.0.0.1/browser-wire.torrent"));
            msg.Set("path", DlJson.S(file));
            JVal r = port.Request(msg, 15000);
            DlItem added = null;
            foreach (string id in e.Ids()) { DlItem x = e.Find(id); if (x != null && x.IsTorrent && x.InfoHash == meta.HexHash) added = x; }
            T.Check("bridge: a .torrent downloaded by the browser becomes a torrent of that browser, the copy stays with the setting off",
                    r != null && DlJson.Bool(r, "ok", false) && !DlJson.Bool(r, "removeBrowserCopy", true) && added != null && added.Source == "edge"
                    && e.Ids().Count == before + 1, (r == null ? "no reply" : Jsn.Write(r)) + " items " + e.Ids().Count);

            fileSettings.BtRecycleTorrentFile = true;
            Thread.Sleep(30);
            fileSettings.Save();
            msg.Set("reqId", DlJson.S("r" + Interlocked.Increment(ref _reqSeq)));
            JVal again = port.Request(msg, 15000);
            T.Check("bridge: the same torrent again is not added twice; with «remove .torrent» on the extension may delete the copy",
                    again != null && DlJson.Bool(again, "removeBrowserCopy", false) && e.Ids().Count == before + 1, again == null ? "no reply" : Jsn.Write(again));

            string page = Path.Combine(dir, "login.torrent");
            File.WriteAllText(page, "<html><body>sign in</body></html>");
            JVal notTorrent = Msg("browserFile");
            notTorrent.Set("path", DlJson.S(page));
            JVal nr = port.Request(notTorrent, 15000);
            JVal relative = Msg("browserFile");
            relative.Set("path", DlJson.S("browser-wire.torrent"));
            JVal rr = port.Request(relative, 15000);
            JVal exe = Msg("browserFile");
            exe.Set("path", DlJson.S(Process.GetCurrentProcess().MainModule.FileName));
            JVal xr = port.Request(exe, 15000);
            T.Check("bridge: a page saved as .torrent, a relative path and a non-.torrent file add nothing and keep the browser's copy",
                    nr != null && !DlJson.Bool(nr, "removeBrowserCopy", true) && rr != null && !DlJson.Bool(rr, "removeBrowserCopy", true)
                    && xr != null && !DlJson.Bool(xr, "removeBrowserCopy", true) && e.Ids().Count == before + 1,
                    (nr == null ? "null" : Jsn.Write(nr)) + " " + (rr == null ? "null" : Jsn.Write(rr)) + " " + (xr == null ? "null" : Jsn.Write(xr)));

            fileSettings.BtRecycleTorrentFile = false;
            Thread.Sleep(30);
            fileSettings.Save();
            string why;
            if (added != null) e.Remove(added.Id, false, out why);
        }

        // ------------------------------------------------------------------ //
        //  Кадры хоста без процесса: пределы в обе стороны
        // ------------------------------------------------------------------ //
        private sealed class NullBridgeBackend : IDlBridgeBackend
        {
            public DlSettings LoadSettings() { return new DlSettings(); }
            public DateTime SettingsStamp() { return DateTime.MinValue; }
            public void SaveSettings(DlSettings s) { }
            public bool AltHeld() { return false; }
            public DlProbeResult Probe(DlOffer offer, DlSettings s) { return new DlProbeResult(); }
            public bool AgentRunning() { return false; }
            public string EnsureAgent() { return "no agent in this test"; }
            public JVal Agent(JVal request) { return null; }
            public void OpenApp() { }
            public void RecordHello(string browser, string extVersion) { }
        }

        private static void BridgeFrames()
        {
            MemoryStream hugeIn = new MemoryStream(new byte[] { 0x01, 0x00, 0x40, 0x00 });   // 4 МБ + 1 байт
            MemoryStream sink = new MemoryStream();
            DlNativeHost h = new DlNativeHost(hugeIn, sink, "chromium", new NullBridgeBackend());
            T.Eq("bridge: a frame over 4 MB from the browser ends the host with code 1", 1, h.Run());

            MemoryStream outS = new MemoryStream();
            DlNativeHost h2 = new DlNativeHost(new MemoryStream(), outS, "chromium", new NullBridgeBackend());
            JVal big = JVal.NewObj();
            big.Set("type", DlJson.S("rules"));
            big.Set("pad", DlJson.S(new string('x', 1024 * 1024)));
            h2.Send(big);
            T.Eq("bridge: a message over 1 MB is never written to the browser", 0L, outS.Length);
            JVal small = JVal.NewObj();
            small.Set("type", DlJson.S("rules"));
            h2.Send(small);
            byte[] b = outS.ToArray();
            int len = b.Length >= 4 ? b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24 : -1;
            JVal back = len == b.Length - 4 ? Jsn.Parse(Encoding.UTF8.GetString(b, 4, b.Length - 4)) : null;
            T.Check("bridge: a normal message is length-prefixed little-endian UTF-8 JSON", back != null && DlJson.Str(back, "type", "") == "rules", len + " / " + b.Length);
        }

        // ------------------------------------------------------------------ //
        //  Реестр: только наши разделы, только под переданным корнем
        // ------------------------------------------------------------------ //
        private static void BrowsersRegistry()
        {
            BridgeFrames();
            string realBefore = RealHostKeysSnapshot();
            string sub = TestSoftwareKey();
            string dir = Dir("nmh");
            try
            {
                using (RegistryKey software = Registry.CurrentUser.CreateSubKey(sub))
                {
                    using (RegistryKey foreign = software.CreateSubKey(@"Google\Chrome\NativeMessagingHosts\com.other.host"))
                        foreign.SetValue("", @"C:\other\host.json");
                    const string exeA = @"C:\Apps\WPC\WindowsProcessCleaner.exe";
                    T.Check("registry: register writes our three keys", DlBrowsers.Register(software, dir, exeA) == null);
                    string chromium = Path.Combine(dir, "org.wpc.downloads.chromium.json");
                    string firefox = Path.Combine(dir, "org.wpc.downloads.firefox.json");
                    T.Check("registry: Chrome, Edge and Mozilla keys point at the family manifests",
                            (software.OpenSubKey(@"Google\Chrome\NativeMessagingHosts\org.wpc.downloads").GetValue("") as string) == chromium
                            && (software.OpenSubKey(@"Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads").GetValue("") as string) == chromium
                            && (software.OpenSubKey(@"Mozilla\NativeMessagingHosts\org.wpc.downloads").GetValue("") as string) == firefox);
                    JVal mc = Jsn.Parse(File.ReadAllText(chromium, Encoding.UTF8));
                    JVal mf = Jsn.Parse(File.ReadAllText(firefox, Encoding.UTF8));
                    List<string> origins = DlJson.StrList(mc, "allowed_origins");
                    List<string> exts = DlJson.StrList(mf, "allowed_extensions");
                    T.Check("registry: the Chromium manifest allows exactly our extension origin", origins.Count == 1 && origins[0] == "chrome-extension://" + ChromiumId + "/"
                            && DlJson.Str(mc, "path", "") == exeA && mc.Get("allowed_extensions") == null, Jsn.Write(mc));
                    T.Check("registry: the Firefox manifest allows exactly our add-on id", exts.Count == 1 && exts[0] == GeckoId && DlJson.Str(mf, "path", "") == exeA && mf.Get("allowed_origins") == null, Jsn.Write(mf));
                    T.Check("registry: IsRegistered sees this exe", DlBrowsers.IsRegistered(software, dir, exeA, "chromium", @"Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads"));
                    const string exeB = @"D:\Moved\WindowsProcessCleaner.exe";
                    T.Check("registry: after the exe moved it is not registered", !DlBrowsers.IsRegistered(software, dir, exeB, "firefox", @"Mozilla\NativeMessagingHosts\org.wpc.downloads"));
                    DlBrowsers.Register(software, dir, exeB);
                    T.Check("registry: registering again rewrites the manifest path", DlBrowsers.IsRegistered(software, dir, exeB, "firefox", @"Mozilla\NativeMessagingHosts\org.wpc.downloads"));

                    T.Check("registry: unregister succeeds", DlBrowsers.Unregister(software, dir) == null);
                    bool ours = false;
                    foreach (string k in new[] { @"Google\Chrome\NativeMessagingHosts\org.wpc.downloads", @"Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads", @"Mozilla\NativeMessagingHosts\org.wpc.downloads" })
                        using (RegistryKey x = software.OpenSubKey(k)) if (x != null) ours = true;
                    T.Check("registry: unregister removes our keys and manifests", !ours && !File.Exists(chromium) && !File.Exists(firefox));
                    using (RegistryKey foreign = software.OpenSubKey(@"Google\Chrome\NativeMessagingHosts\com.other.host"))
                        T.Check("registry: another program's host key is left alone", foreign != null && (foreign.GetValue("") as string) == @"C:\other\host.json");
                }

                DlSettings on = new DlSettings();
                on.BrowserIntegration = true;
                T.Check("registry: a copy with its own data folder refuses to write the real keys", !DlBrowsers.IsDefaultCopy && DlBrowsers.EnsureDefault(on) != null);
            }
            finally
            {
                DeleteTestKey(sub);
            }
            T.Check("registry: the real NativeMessagingHosts keys are unchanged", RealHostKeysSnapshot() == realBefore, realBefore);
            using (RegistryKey gone = Registry.CurrentUser.OpenSubKey(sub))
                T.Check("registry: the test key is removed", gone == null);
        }

        // ------------------------------------------------------------------ //
        //  Расширение, вшитое в exe
        // ------------------------------------------------------------------ //
        // id Chromium из поля key: SHA-256 открытого ключа, первые 16 байт, каждая тетрада 0..15 → буква a..p.
        private static string ChromiumIdOfKey(string base64)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create()) hash = sha.ComputeHash(Convert.FromBase64String(base64));
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 16; i++)
            {
                sb.Append((char)('a' + (hash[i] >> 4)));
                sb.Append((char)('a' + (hash[i] & 15)));
            }
            return sb.ToString();
        }

        private static byte[] ResourceBytes(string name)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        private static void ExtensionUnpack()
        {
            List<string> names = DlBrowsers.ResourceNames();
            bool noTests = true;
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.IndexOf("extension/test/", StringComparison.Ordinal) >= 0) noTests = false;
            T.Check("extension: the build embeds both manifests and the background script", names.Contains("extension/manifest.chromium.json")
                    && names.Contains("extension/manifest.firefox.json") && names.Contains("extension/src/background.js"), string.Join(", ", names.ToArray()));
            T.Check("extension: the extension's own tests are not embedded", noTests);

            foreach (string family in new[] { "chromium", "firefox" })
            {
                string dir = Dir("ext-" + family);
                int files;
                string why = DlBrowsers.Unpack(family, dir, out files);
                T.Check("extension: " + family + " unpacks", why == null && files > 1, why);
                int expected = 0;
                bool same = true;
                string diff = null;
                foreach (string n in names)
                {
                    string rel = n == "extension/manifest." + family + ".json" ? "manifest.json"
                               : n.StartsWith("extension/src/", StringComparison.Ordinal) ? n.Substring("extension/src/".Length).Replace('/', '\\') : null;
                    if (rel == null) continue;
                    expected++;
                    string path = Path.Combine(dir, rel);
                    byte[] want = ResourceBytes(n);
                    byte[] have = File.Exists(path) ? File.ReadAllBytes(path) : null;
                    if (have == null || want == null || have.Length != want.Length) { same = false; diff = rel; continue; }
                    for (int i = 0; i < have.Length; i++) if (have[i] != want[i]) { same = false; diff = rel; break; }
                }
                T.Check("extension: every " + family + " file on disk equals the embedded bytes", same && files == expected
                        && Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length == expected, diff + " files " + files + "/" + expected);
                JVal manifest = Jsn.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json"), Encoding.UTF8));
                if (family == "chromium")
                    T.Eq("extension: the Chromium key derives exactly the id the host allows", ChromiumId, ChromiumIdOfKey(DlJson.Str(manifest, "key", "")));
                else
                {
                    JVal bss = manifest == null ? null : manifest.Get("browser_specific_settings");
                    T.Eq("extension: the Firefox add-on id is the one the host allows", GeckoId, DlJson.Str(bss == null ? null : bss.Get("gecko"), "id", ""));
                }

                string bg = Path.Combine(dir, "background.js");
                DateTime stamp = File.GetLastWriteTimeUtc(bg);
                File.WriteAllText(Path.Combine(dir, "manifest.json"), "{}");
                Thread.Sleep(30);
                DlBrowsers.Unpack(family, dir, out files);
                T.Check("extension: re-unpacking rewrites only changed files", File.GetLastWriteTimeUtc(bg) == stamp
                        && File.ReadAllText(Path.Combine(dir, "manifest.json")) != "{}");
            }
            string root = Dir("ext-nested");
            T.Check("extension: nested paths stay inside the folder", DlBrowsers.NestedInside(root, @"_locales\ru\messages.json") == Path.Combine(root, @"_locales\ru\messages.json")
                    && DlBrowsers.NestedInside(root, @"..\evil.js") == null && DlBrowsers.NestedInside(root, @"a\..\..\evil.js") == null
                    && DlBrowsers.NestedInside(root, @"C:\evil.js") == null && DlBrowsers.NestedInside(root, "a.js:stream") == null && DlBrowsers.NestedInside(root, @"a\\b.js") == null);
            T.Check("extension: manifests of the other family and non-src files are skipped", DlBrowsers.TargetOf("extension/manifest.firefox.json", "chromium") == null
                    && DlBrowsers.TargetOf("extension/README.md", "chromium") == null && DlBrowsers.TargetOf("extension/src/../x.js", "chromium") == null);
        }
    }
}
