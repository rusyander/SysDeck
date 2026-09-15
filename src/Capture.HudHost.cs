// SysDeck — отдельный процесс оверлея (ключ --hud) и HWiNFO в фоне.
//
// Почему не внутри «Захвата»: агент захвата сознательно работает без прав (повышенный процесс не отдал бы снимок
// перетаскиванием в обычную программу), а оверлею права нужны — HWiNFO (manifest requireAdministrator) поднимается
// без окна UAC только из повышенного процесса, трассировка кадров ETW тоже просит права. Поэтому оверлей — свой
// резидент со своим мьютексом и событиями, а «Захват» лишь передаёт ему нажатие клавиши.
//
// Права: галочка «с правами администратора» создаёт (одно окно UAC по нажатию) задачу Планировщика с наивысшими
// правами и без триггеров; дальше программа запускает оверлей этой задачей, окна UAC больше нет. Что повышенному
// процессу доверено из файлов пользователя: только вид строк. Путь HWiNFO берётся не из настроек, а из Program Files
// (туда пишет только администратор) — иначе настройки в %APPDATA% запускали бы от администратора что угодно.
//
// Жизнь процесса: пока работает окно программы или агент «Захвата». Оба закрыты — оверлей закрывает свой HWiNFO и
// выходит сам. HWiNFO, запущенный самим человеком, не трогается никогда: из него только читаются числа.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SysDeck.Capture
{
    internal static class HudMode
    {
        public const string Switch = "--hud";                 // резидент оверлея; «--hud show» — сразу показать
        public const string ToggleSwitch = "--hud-toggle";    // показать / скрыть (запустит резидент, если нужно)
        public const string WaitSwitch = "--hud-wait";        // вместе с --hud: дождаться выхода прежнего оверлея (новая сборка)

        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            if (string.Equals(args[0], ToggleSwitch, StringComparison.OrdinalIgnoreCase))
            {
                exitCode = HudLauncher.Toggle() == null ? 0 : 1;
                return true;
            }
            if (!string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase)) return false;
            bool show = false, wait = false;
            for (int i = 1; i < args.Length; i++)
            {
                if (string.Equals(args[i], "show", StringComparison.OrdinalIgnoreCase)) show = true;
                else if (string.Equals(args[i], WaitSwitch, StringComparison.OrdinalIgnoreCase)) wait = true;
            }
            exitCode = RunHost(show, wait);
            return true;
        }

        // Этот процесс — резидент оверлея (имя сессии ETW не должно совпасть с сессией окна программы).
        public static bool IsHudProcess;

        private static int RunHost(bool show, bool waitPrevious)
        {
            IsHudProcess = true;
            try { Tr.En = new Engine().Config.Language == "en"; }
            catch (Exception ex) { CapLog.Report(ex); }

            bool first;
            SafeWaitHandle instance = HudIpc.CreateInstanceMutex(out first);
            // Мьютекс оверлея — признак существования, не владения: ждать можно только его исчезновения.
            for (int waited = 0; !first && waitPrevious && waited < 15000; waited += 200)
            {
                if (instance != null) instance.Dispose();
                Thread.Sleep(200);
                instance = HudIpc.CreateInstanceMutex(out first);
            }
            if (!first)
            {
                if (instance != null) instance.Dispose();
                if (show) HudIpc.Signal("Show");
                return 0;
            }
            List<EventWaitHandle> events = new List<EventWaitHandle>();
            List<RegisteredWaitHandle> waits = new List<RegisteredWaitHandle>();
            HudApp app = null;
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { CapLog.Report(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { CapLog.Report(e.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                CapNative.UsePerMonitorDpiOnThisThread();
                app = new HudApp();
                HudApp target = app;
                foreach (string command in HudIpc.Commands)
                {
                    EventWaitHandle h = HudIpc.CreateEvent(command);
                    events.Add(h);
                    string cmd = command;
                    waits.Add(ThreadPool.RegisterWaitForSingleObject(h, delegate { target.Post(delegate { target.Command(cmd); }); },
                                                                     null, Timeout.Infinite, false));
                }
                app.Start(show);
                Application.Run();
            }
            catch (Exception ex) { CapLog.Report(ex); return 1; }
            finally
            {
                foreach (RegisteredWaitHandle w in waits) w.Unregister(null);
                if (app != null) app.Dispose();
                HudStatus.Clear();
                foreach (EventWaitHandle h in events) h.Dispose();
                instance.Dispose();
            }
            return 0;
        }
    }

    internal static class HudIpc
    {
        public const string MutexName = @"Local\SysDeck.Hud";
        private const string Prefix = @"Local\SysDeck.Hud.";
        public const string AppMutexName = @"Local\SysDeck.singleinstance";

        // Ни одна команда не несёт пути или аргумента: повышенный процесс не берёт от обычных ничего, кроме «покажись»
        // и номера слота Afterburner в самом имени события (путь к Afterburner оверлей находит сам, в Program Files).
        public static readonly string[] Commands = { "Show", "Hide", "Toggle", "Reload", "Shutdown", "ResetStats", "NextScene", "Move", "LagToggle",
                                                     "Afterburner1", "Afterburner2", "Afterburner3", "Afterburner4", "Afterburner5" };

        // Доступ — только своему пользователю; метка целостности «средняя»: объект повышенного процесса по умолчанию
        // получает высокую, и окно программы без прав не смогло бы подать сигнал (no-write-up).
        // Метка ставится через CreateMutex/CreateEvent напрямую: MutexSecurity с разделом S: требует SeSecurityPrivilege
        // (ошибка 1314), а дескриптор при создании объекта метку не выше своей принимает без привилегий.
        internal static string Sddl(int rights)
        {
            string sid;
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User.Value;
            return "D:(A;;0x" + rights.ToString("x", CultureInfo.InvariantCulture) + ";;;" + sid + ")S:(ML;;NW;;;ME)";
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SecurityAttributes
        {
            public int Length;
            public IntPtr Descriptor;
            public bool Inherit;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out IntPtr descriptor, IntPtr size);
        // Варианты Ex — чтобы запросить ровно те права, что даёт DACL. CreateMutexW/CreateEventW просят полный доступ,
        // и если объект уже существует (его ещё держит открытым окно программы, подававшее сигнал), перезапущенный
        // оверлей получал «Отказано в доступе» и падал.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateMutexExW(ref SecurityAttributes sa, string name, uint flags, uint desiredAccess);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeWaitHandle CreateEventExW(ref SecurityAttributes sa, string name, uint flags, uint desiredAccess);
        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr mem);

        private delegate SafeWaitHandle Creator(ref SecurityAttributes sa);

        private static SafeWaitHandle Create(int rights, Creator create, out int error)
        {
            IntPtr sd;
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(Sddl(rights), 1, out sd, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                SecurityAttributes sa = new SecurityAttributes();
                sa.Length = Marshal.SizeOf(typeof(SecurityAttributes));
                sa.Descriptor = sd;
                SafeWaitHandle h = create(ref sa);
                error = Marshal.GetLastWin32Error();
                return h;
            }
            finally { LocalFree(sd); }
        }

        // Признак «оверлей запущен» — само существование объекта, владение не нужно. null — уже есть чужой (например,
        // повышенный) экземпляр; first=false — есть свой.
        public static SafeWaitHandle CreateInstanceMutex(out bool first)
        {
            int error;
            SafeWaitHandle h = Create(0x100001, delegate(ref SecurityAttributes sa) { return CreateMutexExW(ref sa, MutexName, 0, 0x100001); }, out error);
            first = !h.IsInvalid && error != 183;                          // ERROR_ALREADY_EXISTS
            if (!h.IsInvalid) return h;
            h.Dispose();
            return null;
        }

        public static EventWaitHandle CreateEvent(string command)
        {
            if (Array.IndexOf(Commands, command) < 0) throw new ArgumentException("unknown hud command: " + command);
            return CreateNamedEvent(Prefix + command);
        }

        internal static EventWaitHandle CreateNamedEvent(string name)
        {
            int error;
            SafeWaitHandle h = Create(0x100002, delegate(ref SecurityAttributes sa) { return CreateEventExW(ref sa, name, 0, 0x100002); }, out error);
            if (h.IsInvalid) { h.Dispose(); throw new Win32Exception(error); }
            EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset);
            SafeWaitHandle unnamed = ev.SafeWaitHandle;
            ev.SafeWaitHandle = h;
            unnamed.Dispose();
            return ev;
        }

        public static bool IsRunning() { return MutexExists(MutexName); }

        public static bool MutexExists(string name)
        {
            Mutex m;
            try
            {
                if (Mutex.TryOpenExisting(name, MutexRights.Synchronize, out m)) { m.Dispose(); return true; }
                return false;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        public static bool Signal(string command)
        {
            if (Array.IndexOf(Commands, command) < 0) return false;
            EventWaitHandle h;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(Prefix + command, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch { return false; }
        }

        public static bool WaitRunning(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (!IsRunning())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }

        public static bool WaitStopped(int timeoutMs)
        {
            Stopwatch clock = Stopwatch.StartNew();
            while (IsRunning())
            {
                if (clock.ElapsedMilliseconds > timeoutMs) return false;
                Thread.Sleep(100);
            }
            return true;
        }
    }

    // Что окно программы знает об оверлее: пишет сам процесс оверлея.
    internal sealed class HudStatus
    {
        public int Pid;
        public bool Elevated, Shown;
        public string Hwinfo = "";       // состояние HwinfoHost.State
        public string Fps = "";          // состояние сессии кадров HudEtwSession.State
        public string Afterburner = "";  // «номер|слот|ok» или «номер|слот|текст ошибки» — итог последней команды AfterburnerN
        public string Lag = "";          // «rec|папка», «done|путь к report.md», «error|причина»; пусто — записи не было
        public string Note = "";

        public static string FilePath { get { return Path.Combine(CapPaths.DataDir, "hud-status.txt"); } }

        public static void Write(HudStatus s)
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("pid=").Append(s.Pid.ToString(CultureInfo.InvariantCulture)).Append('\n');
                sb.Append("elevated=").Append(s.Elevated ? "1" : "0").Append('\n');
                sb.Append("shown=").Append(s.Shown ? "1" : "0").Append('\n');
                sb.Append("hwinfo=").Append(s.Hwinfo).Append('\n');
                sb.Append("fps=").Append(s.Fps).Append('\n');
                sb.Append("afterburner=").Append((s.Afterburner ?? "").Replace('\n', ' ')).Append('\n');
                sb.Append("lag=").Append((s.Lag ?? "").Replace('\n', ' ')).Append('\n');
                sb.Append("note=").Append((s.Note ?? "").Replace('\n', ' ')).Append('\n');
                CapPaths.WriteAtomic(FilePath, sb.ToString());
            }
            // Файл занят читателем дольше всех попыток: статус пишется заново через несколько секунд — не сбой.
            catch (IOException) { }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static HudStatus Read()
        {
            if (!HudIpc.IsRunning()) return null;
            try
            {
                HudStatus s = new HudStatus();
                foreach (string line in CapPaths.ReadShared(FilePath).Split('\n'))
                {
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "pid") int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out s.Pid);
                    else if (k == "elevated") s.Elevated = v == "1";
                    else if (k == "shown") s.Shown = v == "1";
                    else if (k == "hwinfo") s.Hwinfo = v;
                    else if (k == "fps") s.Fps = v;
                    else if (k == "afterburner") s.Afterburner = v;
                    else if (k == "lag") s.Lag = v;
                    else if (k == "note") s.Note = v;
                }
                return s;
            }
            catch { return null; }
        }

        public static void Clear()
        {
            try { File.Delete(FilePath); } catch { }
        }
    }

    // ------------------------------------------------------------------ //
    //  Запуск оверлея: задачей с правами, если так выбрано, иначе обычным процессом
    // ------------------------------------------------------------------ //
    internal static class HudLauncher
    {
        public const string TaskName = "SysDeck HUD";

        // Показать / скрыть. null — сделано, иначе причина.
        public static string Toggle()
        {
            if (HudIpc.IsRunning()) return HudIpc.Signal("Toggle") ? null : "no answer from the overlay process";
            return Start(true);
        }

        // Команда резиденту; startIfNeeded — поднять оверлей (скрытым), если он не запущен: запись лагов и смена набора
        // осмысленны и без показанного столбика.
        public static void CommandAsync(string command, bool startIfNeeded)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!HudIpc.IsRunning())
                    {
                        if (!startIfNeeded) return;
                        string why = Start(false);
                        if (why != null || !HudIpc.WaitRunning(10000)) { CapLog.Write("hud " + command + ": " + (why ?? "overlay did not start")); return; }
                        Thread.Sleep(300);   // события команд создаются сразу после мьютекса
                    }
                    if (!HudIpc.Signal(command)) CapLog.Write("hud " + command + ": no answer from the overlay process");
                }
                catch (Exception ex) { CapLog.Report(ex); }
            });
        }

        public static void ToggleAsync()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string why = Toggle();
                if (why != null) CapLog.Write("hud toggle failed: " + why);
            });
        }

        // Вместе с программой: оверлей нужен сразу, если он был показан или выбран запуск с правами (HWiNFO уже
        // готов к первому нажатию клавиши). Фоновый поток: schtasks.exe отвечает до секунды.
        public static void StartIfWanted()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (HudIpc.IsRunning()) return;
                    CapSettings s = CapSettings.Load();
                    if (!s.HudShown && !s.HudElevated) return;
                    string why = Start(false);
                    if (why != null) CapLog.Write("hud start with the app failed: " + why);
                }
                catch (Exception ex) { CapLog.Report(ex); }
            });
        }

        public static string Start(bool show)
        {
            if (HudIpc.IsRunning()) { if (show) HudIpc.Signal("Show"); return null; }
            CapSettings s = CapSettings.Load();
            string exe = CapPaths.ExecutablePath;
            if (s.HudElevated && !Elevation.IsElevated)
            {
                string why = TaskPointsHere(exe) ? null : "the elevated task is missing or points to another exe";
                if (why == null && RunSchTasks("/Run /TN \"" + TaskName + "\"") != 0) why = "schtasks /Run failed";
                if (why == null && !HudIpc.WaitRunning(10000)) why = "the elevated task did not start the overlay";
                if (why == null)
                {
                    if (show) HudIpc.Signal("Show");
                    return null;
                }
                // Без прав оверлей всё равно работает (без HWiNFO в фоне); причина видна на странице.
                CapLog.Write("hud elevated start: " + why + "; starting without rights");
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, HudMode.Switch + (show ? " show" : ""));
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) { }
                return null;
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                return ex.Message;
            }
        }

        // Перезапуск после смены галочки: работающий оверлей закрывается и поднимается уже с новыми правами.
        public static void Restart()
        {
            bool wasShown = false;
            if (HudIpc.IsRunning())
            {
                HudStatus st = HudStatus.Read();
                wasShown = st != null && st.Shown;
                HudIpc.Signal("Shutdown");
                HudIpc.WaitStopped(8000);
            }
            CapSettings s = CapSettings.Load();
            if (wasShown || s.HudShown || s.HudElevated) Start(wasShown);
        }

        // ---- задача Планировщика ----
        public static bool TaskExists() { return RunSchTasks("/Query /TN \"" + TaskName + "\"") == 0; }

        // Задача запускает ровно этот exe: после переноса программы старая задача не должна поднимать чужой файл.
        public static bool TaskPointsHere(string exe)
        {
            string output;
            if (RunSchTasks("/Query /XML /TN \"" + TaskName + "\"", out output) != 0 || output == null) return false;
            return output.IndexOf("<Command>" + System.Security.SecurityElement.Escape(exe) + "</Command>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // Только из повышенного процесса (--elevated-job «hudtask»). null — успех.
        public static string CreateTask()
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "wpc-hud-" + Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + ".xml");
            try
            {
                File.WriteAllText(xmlPath, TaskXml(CapPaths.ExecutablePath), new UnicodeEncoding(false, true));
                int code = RunSchTasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F");
                return code == 0 ? null : "schtasks → " + code.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) { CapLog.Report(ex); return ex.Message; }
            finally { try { File.Delete(xmlPath); } catch { } }
        }

        public static bool RemoveTask()
        {
            int code = RunSchTasks("/Delete /TN \"" + TaskName + "\" /F");
            return code == 0 || !TaskExists();
        }

        // Без триггеров: задача ничего не запускает сама, её запускает программа. Приоритет 5 — обычный (7 у
        // Планировщика по умолчанию — ниже обычного, оверлей в игре подтормаживал бы).
        internal static string TaskXml(string exe)
        {
            string sid = null;
            try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value; }
            catch { }
            string principal = string.IsNullOrEmpty(sid) ? Environment.UserDomainName + "\\" + Environment.UserName : sid;
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            sb.Append("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            sb.Append("  <RegistrationInfo><Description>SysDeck overlay</Description></RegistrationInfo>\r\n");
            sb.Append("  <Principals><Principal id=\"Author\"><UserId>").Append(System.Security.SecurityElement.Escape(principal)).Append("</UserId>");
            sb.Append("<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
            sb.Append("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>true</Enabled>\r\n");
            sb.Append("    <Hidden>false</Hidden>\r\n");
            sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
            sb.Append("    <Priority>5</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(System.Security.SecurityElement.Escape(exe)).Append("</Command><Arguments>")
              .Append(HudMode.Switch).Append("</Arguments></Exec></Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }

        private static int RunSchTasks(string arguments)
        {
            string ignored;
            return RunSchTasks(arguments, out ignored);
        }

        private static int RunSchTasks(string arguments, out string output)
        {
            output = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { CapLog.Report(ex); return -1; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Резидент оверлея
    // ------------------------------------------------------------------ //
    internal sealed class HudApp : IDisposable
    {
        private readonly Control _invoker;
        private readonly HudController _hud;
        private readonly HwinfoHost _hwinfo = new HwinfoHost();
        private readonly System.Windows.Forms.Timer _watch = new System.Windows.Forms.Timer();
        private CapSettings _settings;
        private int _orphanChecks;
        private DateTime _buildStamp = AgentBuild.Stamp(CapPaths.ExecutablePath);
        private int _hostBusy;
        private int _abBusy, _abSeq;
        private string _abResult = "";

        public HudApp()
        {
            _invoker = new Control();
            _invoker.CreateControl();
            IntPtr force = _invoker.Handle;
            _settings = CapSettings.Load();
            _hud = new HudController(Post);
            _hud.Moved += OnMoved;
            _watch.Interval = 5000;
            _watch.Tick += delegate { Watch(); };
        }

        public void Post(Action action)
        {
            try { if (!_invoker.IsDisposed) _invoker.BeginInvoke(action); }
            catch (InvalidOperationException) { }
        }

        public void Start(bool show)
        {
            HwinfoIni.RecoverAfterCrash();
            _hud.Apply(_settings);
            if (show || _settings.HudShown) ShowHud();
            _watch.Start();
            Watch();
        }

        public void Command(string command)
        {
            switch (command)
            {
                case "Show": ShowHud(); break;
                case "Hide": HideHud(); break;
                case "Toggle": if (_hud.Visible) HideHud(); else ShowHud(); break;
                case "Reload":
                    _settings = CapSettings.Load();
                    _hud.Apply(_settings);
                    Watch();
                    break;
                case "Shutdown": Application.ExitThread(); break;
                case "ResetStats": _hud.ResetStats(); break;
                case "NextScene": NextScene(); break;
                case "Move": if (_hud.MoveMode) _hud.EndMove(); else _hud.BeginMove(); break;
                case "LagToggle": ToggleLag(); break;
                default:
                    if (command.StartsWith("Afterburner", StringComparison.Ordinal))
                        ApplyAfterburner(int.Parse(command.Substring("Afterburner".Length), CultureInfo.InvariantCulture));
                    break;
            }
        }

        // Переключение профиля занимает до 15 с (ждём второй экземпляр Afterburner) — не на потоке окна оверлея.
        // Повторное нажатие, пока первое не закончилось, отвечает ошибкой, а не встаёт в очередь.
        private void ApplyAfterburner(int slot)
        {
            int seq = ++_abSeq;
            string prefix = seq.ToString(CultureInfo.InvariantCulture) + "|" + slot.ToString(CultureInfo.InvariantCulture) + "|";
            if (Interlocked.CompareExchange(ref _abBusy, 1, 0) != 0)
            {
                _abResult = prefix + Tr.S("предыдущее переключение ещё идёт", "the previous switch is still running");
                WriteStatus();
                return;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error;
                try { error = SysDeck.Afterburner.Apply(slot); }
                catch (Exception ex) { error = ex.Message; }
                finally { Interlocked.Exchange(ref _abBusy, 0); }
                string result = prefix + (error ?? "ok");
                Post(delegate { _abResult = result; WriteStatus(); });
            });
        }

        // Следующий непустой набор строк; второй и третий пустые — клавиша честно говорит, что набор один.
        private void NextScene()
        {
            CapSettings fresh = CapSettings.Load();
            int scene = fresh.HudScene;
            for (int i = 1; i <= 3; i++)
            {
                int candidate = (fresh.HudScene + i) % 3;
                if (candidate == 0 || !string.IsNullOrEmpty(fresh.SceneItems(candidate))) { scene = candidate; break; }
            }
            if (scene == fresh.HudScene)
            {
                _hud.Flash(Tr.S("Набор строк один — второй задаётся на странице «Оверлей»", "Only one row set — add another on the Overlay page"));
                if (!_hud.Visible) ShowHud();
                return;
            }
            fresh.HudScene = scene;
            fresh.Save();
            _settings = fresh;
            _hud.Apply(_settings);
            if (!_hud.Visible) ShowHud();
            _hud.Flash(Tr.S("Набор ", "Set ") + (scene + 1).ToString(CultureInfo.InvariantCulture));
        }

        // Перетащили столбик: место запоминается долей поля монитора, на котором оказалась его середина.
        private void OnMoved(Rectangle bounds)
        {
            Post(delegate
            {
                CapSettings fresh = CapSettings.Load();
                Point center = new Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                int monitor = HudLayout.MonitorIndexAt(center);
                int x, y;
                HudLayout.ToPermille(HudLayout.MonitorArea(monitor), bounds, out x, out y);
                fresh.HudMonitor = monitor;
                fresh.HudCorner = HudCorner.Custom;
                fresh.HudX = x;
                fresh.HudY = y;
                fresh.Save();
                _settings = fresh;
                _hud.Apply(_settings);
            });
        }

        private string _lagStatus = "";

        private void ToggleLag()
        {
            HudLagRecorder running = _hud.StopRecording();
            if (running != null)
            {
                string folder = running.Folder;
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string report = null;
                    try { report = running.Stop(); }
                    catch (Exception ex) { CapLog.Report(ex); }
                    Post(delegate
                    {
                        _lagStatus = report != null ? "done|" + report : "error|" + Tr.S("отчёт не записан, данные в ", "the report was not written, data is in ") + folder;
                        _hud.Flash(report != null ? Tr.S("Отчёт о лагах сохранён", "Lag report saved") : Tr.S("Отчёт не записан", "Report not written"));
                        WriteStatus();
                        OpenFolder(folder);
                    });
                });
                return;
            }
            HudFamily fam = HudProcTree.Current();
            string error;
            HudLagRecorder rec = HudLagRecorder.Start(fam != null && fam.Name != null ? fam.Name : "desktop", out error);
            if (rec == null)
            {
                _lagStatus = "error|" + error;
                _hud.Flash(Tr.S("Запись лагов не началась: ", "Lag recording did not start: ") + error);
                if (!_hud.Visible) ShowHud();
            }
            else
            {
                _lagStatus = "rec|" + rec.Folder;
                _hud.StartRecording(rec);
            }
            WriteStatus();
        }

        // Папку открывает Проводник с обычными правами, даже если оверлей повышенный.
        private static void OpenFolder(string folder)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    string args = "\"" + folder + "\"";
                    string why = Elevation.IsElevated ? CapLauncher.ShellExecuteUnelevated("explorer.exe", args) : null;
                    if (!Elevation.IsElevated || why != null)
                    {
                        if (why != null) CapLog.Write("lag report folder: " + why);
                        if (!Elevation.IsElevated) using (Process p = Process.Start("explorer.exe", args)) { }
                    }
                }
                catch (Exception ex) { CapLog.Report(ex); }
            });
        }

        private void ShowHud()
        {
            _settings = CapSettings.Load();
            _hud.Apply(_settings);
            _hud.Show();
            RememberShown(true);
        }

        private void HideHud()
        {
            _hud.Hide();
            RememberShown(false);
        }

        // Настройки перечитываются перед записью, чтобы не затереть то, что окно программы успело сохранить.
        private void RememberShown(bool shown)
        {
            CapSettings fresh = CapSettings.Load();
            if (fresh.HudShown != shown) { fresh.HudShown = shown; fresh.Save(); }
            _settings.HudShown = shown;
            WriteStatus();
        }

        private void Watch()
        {
            // Окна программы и «Захвата» нет два замера подряд — оверлей больше никому не нужен.
            bool owner = HudIpc.MutexExists(HudIpc.AppMutexName) || CapIpc.IsRunning();
            _orphanChecks = owner ? 0 : _orphanChecks + 1;
            if (_orphanChecks >= 2) { Application.ExitThread(); return; }
            if (RestartIfRebuilt()) return;
            if (Interlocked.CompareExchange(ref _hostBusy, 1, 0) != 0) return;
            bool wanted = _settings.HudHwinfo;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try { _hwinfo.Tick(wanted); }
                catch (Exception ex) { CapLog.Report(ex); }
                finally { Interlocked.Exchange(ref _hostBusy, 0); }
                Post(WriteStatus);
            });
        }

        // exe пересобрали или обновили, а оверлей работает со старым кодом. Новый процесс наследует права этого
        // (задачу Планировщика заново не зовём) и ждёт, пока этот отпустит мьютекс. Не во время записи лагов,
        // переключения Afterburner и перетаскивания.
        private bool RestartIfRebuilt()
        {
            bool busy = _hud.Recorder != null || _hud.MoveMode || Interlocked.CompareExchange(ref _abBusy, 0, 0) != 0;
            string exe = CapPaths.ExecutablePath;
            if (!AgentBuild.ShouldRestart(_buildStamp, AgentBuild.Stamp(exe), DateTime.UtcNow, busy)) return false;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, HudMode.Switch + (_hud.Visible ? " show " : " ") + HudMode.WaitSwitch);
                psi.UseShellExecute = false;
                using (Process p = Process.Start(psi)) { }
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                _buildStamp = DateTime.MinValue;       // новый exe не стартует — больше не пробуем, работаем дальше
                return false;
            }
            CapLog.Write("new build of the exe, overlay restarts");
            Application.ExitThread();
            return true;
        }

        private void WriteStatus()
        {
            HudStatus s = new HudStatus();
            s.Pid = Process.GetCurrentProcess().Id;
            s.Elevated = Elevation.IsElevated;
            s.Shown = _hud.Visible;
            s.Hwinfo = _hwinfo.State;
            s.Fps = _hud.Visible ? HudFpsSource.LastState : HudEtwSession.StateOff;
            s.Note = _hwinfo.Note;
            s.Afterburner = _abResult;
            s.Lag = _lagStatus;
            HudStatus.Write(s);
        }

        public void Dispose()
        {
            _watch.Stop();
            HudLagRecorder rec = _hud.StopRecording();
            if (rec != null) { try { rec.Stop(); } catch (Exception ex) { CapLog.Report(ex); } }
            _hud.Dispose();
            try { _hwinfo.Dispose(); } catch (Exception ex) { CapLog.Report(ex); }
            _invoker.Dispose();
        }
    }
}
