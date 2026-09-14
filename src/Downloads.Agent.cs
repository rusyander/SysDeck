// Windows Process Cleaner — «Загрузки»: фоновый процесс (ключ --downloads), именованный канал, команды, клиент, автозапуск.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Канал \\.\pipe\WindowsProcessCleaner.dl.<SID>: доступ только у текущего пользователя, а подключившийся процесс обязан
// быть этим же exe (окно программы, позже — мост к браузерам). Кадры — 4 байта длины (LE) + JSON в UTF-8, как у native
// messaging. Ни одна команда не принимает путь для удаления или запуска: «удалить в Корзину» работает только с файлом,
// который движок сам скачал, а папка новой загрузки проходит ту же проверку, что и в движке.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WindowsProcessCleaner.Downloads
{
    // ------------------------------------------------------------------ //
    //  Ключи exe. Проверяются в Main раньше мьютекса главного окна.
    // ------------------------------------------------------------------ //
    internal static class DlMode
    {
        public const string Switch = "--downloads";
        public const string ExitSwitch = "--downloads-exit";

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            foreach (string a in args)
            {
                if (string.Equals(a, ExitSwitch, StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = DlIpc.SignalShutdown() ? 0 : 1;
                    return true;
                }
            }
            foreach (string a in args)
            {
                if (string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase))
                {
                    exitCode = RunAgent();
                    return true;
                }
            }
            return false;
        }

        private static int RunAgent()
        {
            try { Tr.En = new Engine().Config.Language == "en"; }
            catch (Exception ex) { DlLog.Report(ex); }

            bool first;
            Mutex instance = DlIpc.CreateInstanceMutex(out first);
            if (!first)
            {
                if (instance != null) instance.Dispose();
                return 0;
            }
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { DlLog.Report(e.ExceptionObject as Exception); };

            DlEngine engine = null;
            DlPipeServer server = null;
            DlNotifier notifier = null;
            EventWaitHandle shutdown = null;
            try
            {
                shutdown = DlIpc.CreateShutdownEvent();
                DlSettings settings = DlSettings.Load();
                MdWiring.Install();          // до первого запуска очереди: иначе записи Kind = "media" остались бы без исполнителя
                engine = new DlEngine(new DlStore(DlPaths.DataDir), settings, new DlSystemEnvironment());
                engine.SettingsPersist = delegate(DlSettings s) { s.Save(); };
                notifier = new DlNotifier();
                EventWaitHandle stop = shutdown;
                DlEngine eng = engine;
                DlNotifier toasts = notifier;
                DlCommands commands = new DlCommands(engine, delegate { stop.Set(); },
                    delegate(DlSettings s) { s.Save(); DlLauncher.SyncAutostart(s, eng); DlBrowsers.EnsureDefault(s); });
                engine.Notice = delegate(DlNotice n)
                {
                    DlSettings s = eng.Settings;
                    commands.OnNotice(n);
                    if (n.Kind == DlNoticeKind.UpdateAvailable) n.OpenApp = DlLauncher.OpenDownloadsPage;
                    if (n.Kind == DlNoticeKind.Completed || n.Kind == DlNoticeKind.UpdateAvailable ? s.NotifyComplete : s.NotifyErrors) toasts.Show(n);
                };
                commands.Intercepted = delegate(DlNotice n)
                {
                    n.OpenApp = DlLauncher.OpenDownloadsPage;
                    toasts.Show(n);
                };
                commands.AskFolder = delegate(string id, string name) { DlFolderAsk.Begin(eng, toasts, id, name); };
                engine.Start();
                server = new DlPipeServer(DlIpc.PipeName, commands.Handle, DlIpc.IsOwnImage);
                server.Start();
                DlLog.Write("agent started, pid " + Process.GetCurrentProcess().Id);
                DlLauncher.SyncAutostart(settings, engine);
                ThreadPool.QueueUserWorkItem(delegate { DlBrowsers.EnsureDefault(settings); });
                DlIdleExit idle = new DlIdleExit(DateTime.UtcNow, DlIdleExit.DefaultSeconds);
                while (!shutdown.WaitOne(1000))
                {
                    idle.Touch(commands.LastCallUtc);
                    if (idle.ShouldExit(DateTime.UtcNow, engine.HasPendingWork || notifier.Busy))
                    {
                        DlLog.Write("nothing to do for " + DlIdleExit.DefaultSeconds + " s, exiting");
                        break;
                    }
                }
            }
            catch (Exception ex) { DlLog.Report(ex); return 1; }
            finally
            {
                if (server != null) server.Dispose();
                if (engine != null)
                {
                    engine.Notice = null;
                    engine.Dispose();
                    DlLauncher.SyncAutostart(engine.Settings, engine);
                }
                if (notifier != null) notifier.Dispose();
                if (shutdown != null) shutdown.Dispose();
                try { instance.ReleaseMutex(); } catch { }
                instance.Dispose();
                DlLog.Write("agent stopped");
            }
            return 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Мьютекс, событие остановки, кадры, проверка клиента
    // ------------------------------------------------------------------ //
    internal static class DlIpc
    {
        private const string BaseName = @"Local\WindowsProcessCleaner.Downloads";
        public const int MaxFrame = 16 * 1024 * 1024;

        public static SecurityIdentifier User()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return id.User;
        }

        // Один процесс загрузок — на одно хранилище. Копия программы со своей папкой данных (портативная, WPC_DATA_DIR
        // тестов и снимков) получает свои имена: иначе её окно управляло бы чужим списком, а её процесс не стартовал бы
        // вовсе — имя уже занято. У хранилища по умолчанию суффикса нет.
        public static string Channel
        {
            get
            {
                string standard;
                try { standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsProcessCleaner", "downloads"); }
                catch { return ""; }
                return ChannelFor(DlPaths.DataDir, standard);
            }
        }

        internal static string ChannelFor(string dataDir, string standardDir)
        {
            string dir, standard;
            try
            {
                dir = Path.GetFullPath(dataDir).TrimEnd('\\');
                standard = Path.GetFullPath(standardDir).TrimEnd('\\');
            }
            catch { return ""; }
            if (string.Equals(dir, standard, StringComparison.OrdinalIgnoreCase)) return "";
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(dir.ToLowerInvariant()));
                StringBuilder sb = new StringBuilder(".");
                for (int i = 0; i < 6; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string MutexName { get { return BaseName + Channel; } }
        public static string ShutdownEventName { get { return BaseName + Channel + ".Shutdown"; } }
        public static string PipeName { get { return "WindowsProcessCleaner.dl." + User().Value + Channel; } }

        public static Mutex CreateInstanceMutex(out bool first)
        {
            try
            {
                MutexSecurity sec = new MutexSecurity();
                sec.AddAccessRule(new MutexAccessRule(User(), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
                return new Mutex(true, MutexName, out first, sec);
            }
            catch (UnauthorizedAccessException)
            {
                first = false;
                return null;
            }
        }

        public static EventWaitHandle CreateShutdownEvent()
        {
            bool created;
            EventWaitHandleSecurity sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(new EventWaitHandleAccessRule(User(), EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName, out created, sec);
        }

        public static bool SignalShutdown()
        {
            EventWaitHandle h;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(ShutdownEventName, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch { return false; }
        }

        public static bool IsRunning()
        {
            Mutex m;
            try
            {
                if (Mutex.TryOpenExisting(MutexName, MutexRights.Synchronize, out m)) { m.Dispose(); return true; }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        // Доступ к каналу — только у SID текущего пользователя (повышенное окно того же пользователя тоже проходит).
        public static PipeSecurity CreatePipeSecurity()
        {
            PipeSecurity sec = new PipeSecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new PipeAccessRule(User(), PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize,
                                                 AccessControlType.Allow));
            return sec;
        }

        public static void WriteFrame(Stream s, JVal message)
        {
            byte[] body = new UTF8Encoding(false).GetBytes(Jsn.Write(message));
            if (body.Length > MaxFrame) throw new InvalidDataException("frame too large");
            byte[] frame = new byte[4 + body.Length];
            frame[0] = (byte)body.Length;
            frame[1] = (byte)(body.Length >> 8);
            frame[2] = (byte)(body.Length >> 16);
            frame[3] = (byte)(body.Length >> 24);
            Buffer.BlockCopy(body, 0, frame, 4, body.Length);
            s.Write(frame, 0, frame.Length);
            s.Flush();
        }

        // null — конец потока. Кадр больше max или не JSON-объект — исключение (соединение закрывается).
        public static JVal ReadFrame(Stream s, int max)
        {
            byte[] head = new byte[4];
            if (!ReadExactly(s, head, 4, true)) return null;
            int len = head[0] | head[1] << 8 | head[2] << 16 | head[3] << 24;
            if (len < 0 || len > max) throw new InvalidDataException("frame length " + len);
            byte[] body = new byte[len];
            if (!ReadExactly(s, body, len, false)) throw new EndOfStreamException();
            JVal v = Jsn.Parse(new UTF8Encoding(false, true).GetString(body));
            if (v == null || v.Kind != JKind.Obj) throw new InvalidDataException("frame is not a JSON object");
            return v;
        }

        private static bool ReadExactly(Stream s, byte[] buf, int count, bool eofAllowed)
        {
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0)
                {
                    if (got == 0 && eofAllowed) return false;
                    throw new EndOfStreamException();
                }
                got += n;
            }
            return true;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        public static uint ClientPid(NamedPipeServerStream pipe)
        {
            uint pid;
            return GetNamedPipeClientProcessId(pipe.SafePipeHandle, out pid) ? pid : 0;
        }

        public static string ImageOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return Native.QueryImagePath(h); }
            finally { CloseHandle(h); }
        }

        private static string _ownImage;

        // Клиент — тот же exe, что и сервер (сравнение канонических путей).
        public static bool IsOwnImage(uint pid)
        {
            if (pid == 0) return false;
            if (_ownImage == null) _ownImage = Canon(ImageOf((uint)Process.GetCurrentProcess().Id));
            string image = Canon(ImageOf(pid));
            return image != null && _ownImage != null && string.Equals(image, _ownImage, StringComparison.OrdinalIgnoreCase);
        }

        private static string Canon(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            try { return Native.CanonicalPath(Path.GetFullPath(path)); }
            catch { return path; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Сервер канала: поток приёма, по потоку на соединение, до 8 соединений сразу
    // ------------------------------------------------------------------ //
    internal sealed class DlPipeServer : IDisposable
    {
        public const int MaxClients = 8;

        private readonly string _name;
        private readonly Func<JVal, JVal> _handler;
        private readonly Func<uint, bool> _allowed;
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private readonly List<NamedPipeServerStream> _open = new List<NamedPipeServerStream>();
        private Thread _accept;
        public int Refused;

        public DlPipeServer(string name, Func<JVal, JVal> handler, Func<uint, bool> allowed)
        {
            _name = name;
            _handler = handler;
            _allowed = allowed;
        }

        public void Start()
        {
            _accept = new Thread(AcceptLoop);
            _accept.IsBackground = true;
            _accept.Name = "wpc-dl-pipe";
            _accept.Start();
        }

        private void AcceptLoop()
        {
            while (!_stop.WaitOne(0))
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, MaxClients, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                                                     64 * 1024, 64 * 1024, DlIpc.CreatePipeSecurity());
                    IAsyncResult ar = pipe.BeginWaitForConnection(null, null);
                    if (WaitHandle.WaitAny(new[] { ar.AsyncWaitHandle, _stop }) == 1)
                    {
                        pipe.Dispose();
                        return;
                    }
                    pipe.EndWaitForConnection(ar);
                    uint pid = DlIpc.ClientPid(pipe);
                    if (_allowed != null && !_allowed(pid))
                    {
                        Interlocked.Increment(ref Refused);
                        DlLog.Write("pipe client refused: pid " + pid + " " + (DlIpc.ImageOf(pid) ?? "?"));
                        pipe.Dispose();
                        continue;
                    }
                    lock (_open) _open.Add(pipe);
                    NamedPipeServerStream conn = pipe;
                    pipe = null;
                    Thread t = new Thread(delegate() { Serve(conn); });
                    t.IsBackground = true;
                    t.Name = "wpc-dl-pipe-client";
                    t.Start();
                }
                catch (Exception ex)
                {
                    if (pipe != null) pipe.Dispose();
                    if (_stop.WaitOne(0)) return;
                    // Все экземпляры заняты или канал ещё не освободился — короткая пауза, не цикл на 100 % процессора.
                    DlLog.Write("pipe accept: " + ex.Message);
                    _stop.WaitOne(200);
                }
            }
        }

        private void Serve(NamedPipeServerStream pipe)
        {
            try
            {
                while (!_stop.WaitOne(0) && pipe.IsConnected)
                {
                    JVal req = DlIpc.ReadFrame(pipe, DlIpc.MaxFrame);
                    if (req == null) break;
                    JVal resp;
                    try { resp = _handler(req); }
                    catch (Exception ex)
                    {
                        DlLog.Report(ex);
                        resp = DlCommands.Error(ex.Message);
                    }
                    DlIpc.WriteFrame(pipe, resp ?? DlCommands.Error("no response"));
                }
            }
            catch (Exception) { }
            finally
            {
                lock (_open) _open.Remove(pipe);
                try { pipe.Dispose(); } catch { }
            }
        }

        public void Dispose()
        {
            _stop.Set();
            if (_accept != null) _accept.Join(3000);
            lock (_open) foreach (NamedPipeServerStream p in _open.ToArray()) { try { p.Dispose(); } catch { } }
        }
    }

    // ------------------------------------------------------------------ //
    //  Команды канала
    // ------------------------------------------------------------------ //
    internal sealed class DlCommands
    {
        public const int ProtocolVersion = 1;

        private readonly DlEngine _engine;
        private readonly Action _shutdown;
        private readonly Action<DlSettings> _settingsChanged;

        // Поручения хостам браузеров (вернуть загрузку, прислать cookies) — хост забирает их опросом bridgePending.
        public readonly DlBridgeTasks Bridge = new DlBridgeTasks();
        // Загрузку забрали у браузера — процесс показывает уведомление с кнопками.
        public Action<DlNotice> Intercepted;

        public DlCommands(DlEngine engine, Action shutdown, Action<DlSettings> settingsChanged)
        {
            _engine = engine;
            _shutdown = shutdown;
            _settingsChanged = settingsChanged;
        }

        public static JVal Error(string message)
        {
            JVal o = JVal.NewObj();
            o.Set("ok", DlJson.B(false));
            o.Set("error", DlJson.S(message));
            return o;
        }

        private static JVal Ok()
        {
            JVal o = JVal.NewObj();
            o.Set("ok", DlJson.B(true));
            return o;
        }

        private static JVal Result(bool ok, string why)
        {
            return ok ? Ok() : Error(why ?? Tr.S("нет такой загрузки или действие к ней не применимо", "no such download or the action does not apply"));
        }

        private long _lastCallTicks;

        // Вопрос «куда скачивать» задаёт процесс-хозяин (у него есть поток с окнами); здесь — только повод его задать.
        public Action<string, string> AskFolder;

        // Запись заводится стоящей: пока человек не ответил, не качается ни байта, и ответ ничего не догоняет.
        private bool HoldForFolder(DlAddRequest r, string name)
        {
            if (AskFolder == null || r.StartPaused || !DlFolderAsk.Needed(_engine.Settings, r.Folder, name)) return false;
            r.StartPaused = true;
            return true;
        }

        private void AskFolderFor(string id, string name)
        {
            if (id == null || AskFolder == null) return;
            _engine.SetWaitReason(id, Tr.S("ждёт выбора папки", "waiting for a folder"));
            AskFolder(id, name);
        }

        // Видео: поток (HLS/DASH), прямой файл или страница для yt-dlp. Вариант качества и контейнер — из настроек,
        // выбор дорожек вручную делает окно добавления, не этот путь: расширение шлёт только адрес и вид.
        private JVal AddMedia(JVal req)
        {
            string url = DlJson.Str(req, "url", "");
            if (!DlHttp.IsAllowedScheme(url)) return Error(Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"));
            string kind = DlJson.Str(req, "kind", "").Trim().ToLowerInvariant();
            DlSettings s = _engine.Settings;
            DlAddRequest r = new DlAddRequest();
            r.Url = url;
            r.Referrer = DlJson.Str(req, "referrer", "");
            r.PageUrl = DlJson.Str(req, "pageUrl", "");
            r.UserAgent = DlJson.Str(req, "userAgent", "");
            r.Cookies = DlJson.Str(req, "cookies", "");
            r.Folder = DlJson.Str(req, "folder", "");
            r.Source = DlJson.Str(req, "source", "manual");
            r.Priority = DlJson.Int(req, "priority", 0);
            r.StartPaused = DlJson.Bool(req, "paused", false);
            DlMedia md = new DlMedia();
            md.Source = kind == "hls" ? MdSource.Hls : kind == "dash" ? MdSource.Dash : kind == "page" ? MdSource.Ytdlp : MdSource.Direct;
            md.ManifestUrl = url;
            md.Title = DlJson.Str(req, "title", "");
            if (md.Title.Length > 300) md.Title = md.Title.Substring(0, 300);
            md.Output = s.MdOutput;
            md.Watch = DlJson.Bool(req, "watch", false);
            r.Media = md;
            if (md.Source == MdSource.Ytdlp && !MdYtdlp.Available)
                return Error(Tr.S("для страниц нужен yt-dlp: поставьте его в настройках загрузок",
                                  "pages need yt-dlp: install it in the download settings"));
            bool ask = HoldForFolder(r, md.Title);
            string duplicateOf, error;
            string newId = _engine.Add(r, out duplicateOf, out error);
            JVal o = newId == null ? Error(error) : Ok();
            if (newId != null) o.Set("id", DlJson.S(newId));
            if (duplicateOf != null) o.Set("duplicateOf", DlJson.S(duplicateOf));
            if (newId != null && !ask && DlJson.Bool(req, "intercepted", false) && DlBridge.IsBrowserSource(r.Source)) RaiseIntercepted(newId);
            if (ask) AskFolderFor(newId, md.Title);
            return o;
        }

        // Торрент: путь к .torrent (окно и процесс — один пользователь), содержимое в base64 или magnet-ссылка.
        private JVal AddTorrent(JVal req)
        {
            DlTorrentRequest r = new DlTorrentRequest();
            string file = DlJson.Str(req, "file", ""), data = DlJson.Str(req, "data", "");
            try
            {
                if (file.Length > 0)
                {
                    FileInfo fi = new FileInfo(file);
                    if (!fi.Exists) return Error(Tr.S("файла .torrent нет", "the .torrent file is missing"));
                    if (fi.Length > DlEngine.BtMaxTorrentBytes) return Error(Tr.S("файл .torrent больше 16 МБ", "the .torrent file is larger than 16 MB"));
                    r.TorrentBytes = File.ReadAllBytes(file);
                }
                else if (data.Length > 0) r.TorrentBytes = Convert.FromBase64String(data);
            }
            catch (Exception ex) { return Error(ex.Message); }
            r.Magnet = DlJson.Str(req, "magnet", "");
            r.Folder = DlJson.Str(req, "folder", "");
            r.RootName = DlJson.Str(req, "rootName", "");
            r.Priorities = Ints(req.Get("priorities"));
            r.Sequential = DlJson.Bool(req, "sequential", false);
            r.StartPaused = DlJson.Bool(req, "paused", false);
            r.UseExisting = DlJson.Bool(req, "useExisting", false);
            r.Source = DlJson.Str(req, "source", "manual");
            r.Priority = DlJson.Int(req, "priority", 0);
            r.OnTopicMatch = DlJson.Str(req, "onTopicMatch", DlEngine.TopicMatchAsk);
            bool ask = AskFolder != null && !r.StartPaused && DlFolderAsk.Needed(_engine.Settings, r.Folder, r.RootName);
            if (ask) r.StartPaused = true;
            string duplicateOf, error;
            string newId = _engine.AddTorrent(r, out duplicateOf, out error);
            // Новая версия раздачи из списка принята обновлением: запись не добавлена, но и ошибки нет.
            JVal o = newId == null && !(r.UpdateOf != null && error == null) ? Error(error) : Ok();
            if (newId != null) o.Set("id", DlJson.S(newId));
            if (duplicateOf != null) o.Set("duplicateOf", DlJson.S(duplicateOf));
            if (r.UpdateOf != null) o.Set("updateOf", DlJson.S(r.UpdateOf));
            if (ask) AskFolderFor(newId, r.RootName);
            return o;
        }

        // «Обновить из файла .torrent…»: путь (окно и процесс — один пользователь) или содержимое в base64.
        private JVal UpdateFromFile(string id, JVal req)
        {
            string file = DlJson.Str(req, "file", ""), data = DlJson.Str(req, "data", "");
            byte[] bytes;
            try
            {
                if (file.Length > 0)
                {
                    FileInfo fi = new FileInfo(file);
                    if (!fi.Exists) return Error(Tr.S("файла .torrent нет", "the .torrent file is missing"));
                    if (fi.Length > DlEngine.BtMaxTorrentBytes) return Error(Tr.S("файл .torrent больше 16 МБ", "the .torrent file is larger than 16 MB"));
                    bytes = File.ReadAllBytes(file);
                }
                else bytes = Convert.FromBase64String(data);
            }
            catch (Exception ex) { return Error(ex.Message); }
            string why = _engine.OfferUpdateFile(id, bytes, false);
            return why == null ? Ok() : Error(why);
        }

        private static int[] Ints(JVal arr)
        {
            if (arr == null || arr.Kind != JKind.Arr) return null;
            int[] result = new int[arr.V.Count];
            for (int i = 0; i < result.Length; i++)
            {
                int v;
                result[i] = int.TryParse(arr.V[i].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 1;
            }
            return result;
        }

        // Когда клиент спрашивал в последний раз: пока страница «Загрузки» открыта и опрашивает, процесс не уходит.
        public DateTime LastCallUtc { get { return new DateTime(Interlocked.Read(ref _lastCallTicks), DateTimeKind.Utc); } }

        public JVal Handle(JVal req)
        {
            Interlocked.Exchange(ref _lastCallTicks, DateTime.UtcNow.Ticks);
            string cmd = DlJson.Str(req, "cmd", "");
            string id = DlJson.Str(req, "id", "");
            bool needsId = cmd != "hello" && cmd != "list" && cmd != "add" && cmd != "pauseAll" && cmd != "resumeAll"
                           && cmd != "settings" && cmd != "setSettings" && cmd != "shutdown" && cmd != "clearHistory" && cmd != "bridgePending"
                           && cmd != "addTorrent" && cmd != "recheckInbound" && cmd != "addMedia";
            if (needsId && !DlItem.IsValidId(id)) return Error("bad id");
            string why;
            switch (cmd)
            {
                case "hello":
                {
                    JVal o = Ok();
                    o.Set("version", DlJson.N(ProtocolVersion));
                    o.Set("pid", DlJson.N(Process.GetCurrentProcess().Id));
                    return o;
                }
                case "list":
                {
                    JVal o = _engine.ListJson(DlJson.Bool(req, "events", false));
                    o.Set("ok", DlJson.B(true));
                    return o;
                }
                case "get":
                {
                    JVal item = _engine.ItemJson(id);
                    if (item == null) return Error("no such download");
                    JVal o = Ok();
                    o.Set("item", item);
                    return o;
                }
                case "add":
                {
                    DlAddRequest r = new DlAddRequest();
                    r.Url = DlJson.Str(req, "url", "");
                    r.Folder = DlJson.Str(req, "folder", "");
                    r.FileName = DlJson.Str(req, "name", "");
                    r.Referrer = DlJson.Str(req, "referrer", "");
                    r.PageUrl = DlJson.Str(req, "pageUrl", "");
                    r.UserAgent = DlJson.Str(req, "userAgent", "");
                    r.Cookies = DlJson.Str(req, "cookies", "");
                    r.Source = DlJson.Str(req, "source", "manual");
                    r.ExpectedHash = DlJson.Str(req, "expectedHash", "");
                    r.Mirrors.AddRange(DlJson.StrList(req, "mirrors"));
                    r.WhenIdle = DlJson.Bool(req, "whenIdle", false);
                    r.LimitKBps = DlJson.Int(req, "limitKBps", 0);
                    r.Priority = DlJson.Int(req, "priority", 0);
                    r.Connections = DlJson.Int(req, "connections", 0);
                    r.AllowDuplicate = DlJson.Bool(req, "allowDuplicate", false);
                    r.StartPaused = DlJson.Bool(req, "paused", false);
                    r.AllowHttpDowngrade = DlJson.Bool(req, "allowHttpDowngrade", false);
                    r.StartAtUtc = StartTime(req);
                    bool ask = HoldForFolder(r, r.FileName);
                    string duplicateOf, error;
                    string newId = _engine.Add(r, out duplicateOf, out error);
                    JVal o = newId == null ? Error(error) : Ok();
                    if (newId != null) o.Set("id", DlJson.S(newId));
                    if (duplicateOf != null) o.Set("duplicateOf", DlJson.S(duplicateOf));
                    if (newId != null && !ask && DlJson.Bool(req, "intercepted", false) && DlBridge.IsBrowserSource(r.Source)) RaiseIntercepted(newId);
                    if (ask) AskFolderFor(newId, r.FileName);
                    return o;
                }
                case "addMedia": return AddMedia(req);
                case "stopLive": return Result(_engine.StopLive(id, out why), why);
                case "addTorrent": return AddTorrent(req);
                case "torrent":
                {
                    JVal t = _engine.TorrentJson(id);
                    if (t == null) return Error("no such torrent");
                    JVal o = Ok();
                    o.Set("torrent", t);
                    return o;
                }
                case "setFilePriorities": return Result(_engine.SetFilePriorities(id, Ints(req.Get("priorities")), out why), why);
                case "updateInfo":
                {
                    JVal u = _engine.UpdateJson(id);
                    if (u == null) return Error("no such torrent");
                    JVal o = Ok();
                    o.Set("update", u);
                    return o;
                }
                case "checkUpdate": return Result(_engine.CheckUpdateNow(id, out why), why);
                case "applyUpdate": return Result(_engine.ApplyUpdate(id, out why), why);
                case "dismissUpdate": return Result(_engine.DismissUpdate(id, out why), why);
                case "updateFromFile": return UpdateFromFile(id, req);
                case "recheck": return Result(_engine.Recheck(id, out why), why);
                case "reannounce": return Result(_engine.Reannounce(id), null);
                case "setSequential": return Result(_engine.SetSequential(id, DlJson.Bool(req, "on", false)), null);
                case "recheckInbound": _engine.RecheckInbound(); return Ok();
                case "giveBack": return Result(GiveBack(id, out why), why);
                case "bridgePending":
                {
                    JVal o = Ok();
                    o.Set("tasks", Bridge.Take(DlJson.Str(req, "browser", "")));
                    return o;
                }
                case "pause": return Result(_engine.Pause(id), null);
                case "resume": return Result(_engine.Resume(id), null);
                case "pauseAll": _engine.PauseAll(); return Ok();
                case "resumeAll": _engine.ResumeAll(); return Ok();
                case "restart": return Result(_engine.Restart(id, out why), why);
                case "remove": return Result(_engine.Remove(id, DlJson.Bool(req, "recycle", false), out why), why);
                case "setLimit": return Result(_engine.SetLimit(id, DlJson.Int(req, "kbps", 0)), null);
                case "setPriority": return Result(_engine.SetPriority(id, DlJson.Int(req, "priority", 0)), null);
                case "whenIdle": return Result(_engine.SetWhenIdle(id, DlJson.Bool(req, "on", false)), null);
                case "postpone": return Result(_engine.Postpone(id, StartTime(req)), null);
                case "refreshLink": return Result(_engine.RefreshLink(id, DlJson.Str(req, "url", ""), DlJson.Str(req, "cookies", ""), out why), why);
                case "addMirror": return Result(_engine.AddMirror(id, DlJson.Str(req, "url", "")), null);
                case "move": return Result(_engine.Relocate(id, DlJson.Str(req, "folder", ""), DlJson.Bool(req, "copy", false), out why), why);
                case "cancelMove": return Result(_engine.CancelRelocate(id), null);
                case "clearHistory":
                {
                    JVal o = Ok();
                    o.Set("removed", DlJson.N(_engine.ClearHistory(DlJson.Clamp(DlJson.Int(req, "days", 0), 0, 36500), DlJson.Bool(req, "missingOnly", false))));
                    return o;
                }
                case "settings":
                {
                    JVal o = Ok();
                    o.Set("settings", _engine.Settings.ToJson());
                    return o;
                }
                case "setSettings":
                {
                    DlSettings s = DlSettings.FromJson(req.Get("settings"));
                    if (s == null) return Error("bad settings");
                    if (!string.IsNullOrEmpty(s.Folder) && DlFiles.CheckFolder(s.Folder, out why) == null) return Error(why);
                    foreach (DlFolderRule rule in s.Rules)
                        if (DlFiles.CheckFolder(rule.Folder, out why) == null) return Error(why);
                    _engine.UpdateSettings(s);
                    if (_settingsChanged != null) _settingsChanged(_engine.Settings);
                    return Ok();
                }
                case "shutdown":
                    if (_shutdown != null) _shutdown();
                    return Ok();
                default:
                    return Error("unknown command: " + cmd);
            }
        }

        // Загрузка из браузера упёрлась в авторизацию (после перезагрузки cookies в памяти уже нет) — хост этого браузера
        // попросит у расширения свежие cookies и обновит ссылку сам.
        public void OnNotice(DlNotice n)
        {
            if (n.Kind == DlNoticeKind.NeedsLink && DlBridge.IsBrowserSource(n.Source)) Bridge.AskCookies(n.Source, _engine.Find(n.Id));
        }

        private void RaiseIntercepted(string id)
        {
            Action<DlNotice> handler = Intercepted;
            DlItem it = _engine.Find(id);
            if (handler == null || it == null) return;
            DlNotice n = new DlNotice();
            n.Kind = DlNoticeKind.Intercepted;
            n.Id = id;
            n.Name = string.IsNullOrEmpty(it.FileName) ? DlLog.Redact(it.Url) : it.FileName;
            n.Source = it.Source;
            n.GiveBack = delegate { string ignored; GiveBack(id, out ignored); };
            try { handler(n); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // «Отдать браузеру»: загрузка убирается из списка (недокачанное — в Корзину), браузер качает её заново сам.
        // Только для загрузки, которую забрали у браузера, и только пока она не завершена.
        public bool GiveBack(string id, out string why)
        {
            why = null;
            DlItem it = _engine.Find(id);
            if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
            if (!DlBridge.IsBrowserSource(it.Source)) { why = Tr.S("загрузка добавлена не из браузера", "the download did not come from a browser"); return false; }
            if (it.State == DlState.Completed) { why = Tr.S("загрузка уже завершена", "the download is already complete"); return false; }
            string url = it.OriginalUrl, referrer = it.Referrer, name = it.FileName, source = it.Source;
            if (!_engine.Remove(id, true, out why)) return false;
            Bridge.GiveBack(source, url, referrer, name);
            return true;
        }

        // «at» — время в ISO 8601; «afterMinutes» — через N минут; ни того, ни другого — сейчас.
        private DateTime StartTime(JVal req)
        {
            DateTime at = DlJson.Date(req, "at");
            if (at != DateTime.MinValue) return at;
            int minutes = DlJson.Int(req, "afterMinutes", 0);
            if (minutes > 0) return DateTime.UtcNow.AddMinutes(Math.Min(minutes, 60 * 24 * 365));
            return DateTime.MinValue;
        }
    }

    // ------------------------------------------------------------------ //
    //  Клиент канала (окно программы, мост к браузерам, пробники)
    // ------------------------------------------------------------------ //
    internal static class DlClient
    {
        public static JVal Call(JVal request, int timeoutMs) { return Call(DlIpc.PipeName, request, timeoutMs); }

        // null — процесс загрузок не отвечает.
        public static JVal Call(string pipeName, JVal request, int timeoutMs)
        {
            try
            {
                using (NamedPipeClientStream pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut))
                {
                    pipe.Connect(timeoutMs);
                    DlIpc.WriteFrame(pipe, request);
                    return DlIpc.ReadFrame(pipe, DlIpc.MaxFrame);
                }
            }
            catch { return null; }
        }

        public static JVal Command(string cmd)
        {
            JVal o = JVal.NewObj();
            o.Set("cmd", DlJson.S(cmd));
            return o;
        }
    }

    // ------------------------------------------------------------------ //
    //  Запуск процесса загрузок и продолжение после входа в систему
    // ------------------------------------------------------------------ //
    internal static class DlLauncher
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public static string RunValueName { get { return "WindowsProcessCleaner.Downloads" + DlIpc.Channel; } }

        // null — запущен (или уже работал), иначе причина. Из повышенного окна — через Проводник, с обычными правами:
        // файлы загрузок не должны принадлежать администратору.
        public static string StartAgent()
        {
            if (DlIpc.IsRunning()) return null;
            string exe = DlPaths.ExecutablePath;
            try
            {
                if (Elevation.IsElevated)
                {
                    string why = Capture.CapLauncher.ShellExecuteUnelevated(exe, DlMode.Switch);
                    if (why == null) return null;
                    DlLog.Write("unelevated start failed (" + why + "), starting directly");
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe, DlMode.Switch);
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
                return null;
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return ex.Message;
            }
        }

        public static bool WaitRunning(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!DlIpc.IsRunning())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        // Окно программы на странице «Загрузки»: уже открытое получит сообщение от второго экземпляра.
        public static void OpenDownloadsPage()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(DlPaths.ExecutablePath, "/downloads");
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        internal static string RunCommand(string exe) { return "\"" + exe + "\" " + DlMode.Switch; }

        // Незавершённые загрузки есть и пользователь не выключил «продолжать после входа» — процесс поднимется при входе;
        // очередь пуста — значение удаляется, чтобы в автозапуске не висело лишнего.
        public static void SyncAutostart(DlSettings settings, DlEngine engine)
        {
            bool pending = false;
            foreach (string id in engine.Ids())
            {
                DlItem it = engine.Find(id);
                if (it != null && IsPending(it)) pending = true;
            }
            SyncAutostart(settings, pending);
        }

        public static bool IsPending(DlItem it)
        {
            return it.State == DlState.Queued || it.State == DlState.Scheduled || it.State == DlState.Waiting || it.State == DlState.Active
                || it.State == DlState.Checking || it.State == DlState.Seeding;
        }

        // Для окна, пока процесса нет: настройки сохранены в обход него, а значение автозапуска всё равно должно им следовать.
        public static void SyncAutostart(DlSettings settings, bool pending)
        {
            bool want = settings != null && settings.ResumeAtLogon && pending;
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return;
                    string current = key.GetValue(RunValueName) as string;
                    string wanted = RunCommand(DlPaths.ExecutablePath);
                    if (want && current != wanted) key.SetValue(RunValueName, wanted);
                    else if (!want && current != null) key.DeleteValue(RunValueName, false);
                }
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }
    }
}
