// SysDeck — «Размеры папок»: пути, журнал, настройки, автозапуск, связь процессов, P/Invoke.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Перенос отдельной программы FolderSizePanel внутрь этого exe. Работает отдельным фоновым
// процессом того же файла — ключ --foldersize, свой значок в трее, — а настройки и автозапуск видны
// ещё и на странице «Размеры папок» основного окна. Два процесса говорят друг с другом тремя
// именованными событиями и общими файлами в папке данных, больше ничем.
using System;
using System.Collections.Generic;
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

namespace SysDeck.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Пути
    // ------------------------------------------------------------------ //
    internal static class FsPaths
    {
        // Внутри папки данных приложения: SYSDECK_DATA_DIR уводит туда же и тесты, и фоновый режим.
        public static string DataDir { get { return Path.Combine(Engine.DefaultDataDir(), "foldersize"); } }
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string SizesFile { get { return Path.Combine(DataDir, "sizes.json"); } }
        public static string StatusFile { get { return Path.Combine(DataDir, "status.json"); } }
        public static string CrashLog { get { return Path.Combine(DataDir, "crash.log"); } }
        public static string TraceLog { get { return Path.Combine(DataDir, "trace.log"); } }
        public static string ExecutablePath { get { return Application.ExecutablePath; } }

        // Отдельная FolderSizePanel хранила всё в %APPDATA%\FolderSizePanel.
        public static string LegacyDataDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderSizePanel"); }
        }

        // Первый запуск фонового режима забирает настройки и запомненные размеры отдельной программы:
        // формат файлов тот же, и переход не должен начинаться с полного пересчёта диска.
        public static void ImportLegacy()
        {
            foreach (string name in new string[] { "settings.json", "sizes.json" })
            {
                try
                {
                    string ours = Path.Combine(DataDir, name);
                    string theirs = Path.Combine(LegacyDataDir, name);
                    if (File.Exists(ours) || !File.Exists(theirs)) continue;
                    Directory.CreateDirectory(DataDir);
                    File.Copy(theirs, ours, false);
                }
                catch (Exception ex) { FsLog.Report(ex); }
            }
        }

        // Запись рядом и перенос на место: оборванная запись не должна оставить половину файла,
        // которая читается как целый.
        public static void WriteAtomic(string file, string text)
        {
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, text, new UTF8Encoding(false));
            if (File.Exists(file)) File.Replace(tmp, file, null);
            else File.Move(tmp, file);
        }
    }

    // ------------------------------------------------------------------ //
    //  Журнал: фоновый режим не должен умирать молча и не должен сыпать окнами с текстом
    // ------------------------------------------------------------------ //
    internal static class FsLog
    {
        private static readonly object Gate = new object();
        private static string _lastKey;

        public static void Report(Exception ex)
        {
            if (ex == null) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(FsPaths.DataDir);
                    File.AppendAllText(FsPaths.CrashLog,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + ex + "\r\n\r\n",
                        Encoding.UTF8);
                }
            }
            catch { }
        }

        // То, что повторяется несколько раз в секунду, пишется один раз подряд: иначе журнал растёт без предела.
        public static void ReportOnce(Exception ex)
        {
            if (ex == null) return;
            string key = ex.GetType().FullName + "|" + ex.Message;
            if (key == _lastKey) return;
            _lastKey = key;
            Report(ex);
        }

        public static void Swallow(Action action)
        {
            try { action(); }
            catch (Exception ex) { Report(ex); }
        }

        // FOLDERSIZE_TRACE=1 — подробный след: что трекер думает о Проводнике, что прочитано, что нарисовано.
        public static readonly bool TraceEnabled =
            string.Equals(Environment.GetEnvironmentVariable("FOLDERSIZE_TRACE"), "1", StringComparison.Ordinal);

        public static void Trace(string text)
        {
            if (!TraceEnabled) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(FsPaths.DataDir);
                    File.AppendAllText(FsPaths.TraceLog,
                        "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " + text + "\r\n");
                }
            }
            catch { }
        }
    }

    internal static class FsMath
    {
        public static int Clamp(int value, int min, int max) { return value < min ? min : value > max ? max : value; }
        public static long Clamp(long value, long min, long max) { return value < min ? min : value > max ? max : value; }

        // Тики без переполнения через 49 дней: Environment.TickCount64 в .NET Framework нет.
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static long NowMs { get { return Clock.ElapsedMilliseconds; } }

        public static int Mix(int hash, int value) { unchecked { return hash * 31 + value; } }
        public static int Mix(int hash, long value) { unchecked { return Mix(Mix(hash, (int)value), (int)(value >> 32)); } }
        public static int Mix(int hash, Rectangle r) { return Mix(Mix(Mix(Mix(hash, r.X), r.Y), r.Width), r.Height); }
        public static int Mix(int hash, string s) { return Mix(hash, s == null ? 0 : StringComparer.Ordinal.GetHashCode(s)); }
    }

    // ------------------------------------------------------------------ //
    //  Настройки — тот же JSON, что у отдельной FolderSizePanel (имена и значения совпадают)
    // ------------------------------------------------------------------ //
    internal enum SizeSortMode { SizeDescending, NameAscending, ItemsDescending }

    internal sealed class FsSettings
    {
        public const int MinPanelWidth = 240, MaxPanelWidth = 1200;

        public bool InlineOverlay = true;              // размеры прямо в колонке «Размер» Проводника
        public bool OverlayFileSizes;                  // перекрывать и размеры файлов (у Проводника они и так точные)
        public bool ShowSidePanel = true;              // боковая панель рядом с Проводником
        // Держать цифры и панель, пока Проводник открыт, а не только пока он активен. Это верхний слой —
        // другого у фонового процесса нет, — но рисуется только на пикселях, которые всё ещё Проводника.
        public bool ShowWhenExplorerUnfocused = true;
        public bool ShowScanBadge = true;              // «идёт подсчёт» в углу окна Проводника
        public int PanelWidth = 360;
        public bool DockRight = true;
        public bool MakeRoomForPanel;                  // сужать окно Проводника, чтобы панель его не закрывала
        public bool ShowHidden = true;
        public bool UseFastNtfsIndex = true;           // чтение $MFT, когда процесс с правами администратора
        public SizeSortMode Sort = SizeSortMode.SizeDescending;
        public bool FollowSystemTheme = true;
        public bool DarkTheme = true;
        public bool PanelHotkey = true;                // Ctrl+Alt+S показывает и прячет панель из любого места
        public bool ShowFreeSpace = true;              // свободное место тома в подвале панели
        public bool PanelMenuExtras = true;            // «Открыть в карте диска» и «Копировать список» в меню панели

        public bool IsDark { get { return FollowSystemTheme ? SystemThemeProbe.IsDarkAppMode() : DarkTheme; } }

        public static FsSettings Load() { return Load(FsPaths.SettingsFile); }

        public static FsSettings Load(string file)
        {
            try
            {
                if (File.Exists(file))
                {
                    FsSettings s = FromJson(File.ReadAllText(file));
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { FsLog.Report(ex); }
            return new FsSettings();
        }

        public void Save() { Save(FsPaths.SettingsFile); }

        public void Save(string file)
        {
            try { FsPaths.WriteAtomic(file, ToJson()); }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        internal static FsSettings FromJson(string text)
        {
            JVal root;
            try { root = Jsn.Parse(text ?? ""); }
            catch { return null; }
            if (root == null || root.Kind != JKind.Obj) return null;
            FsSettings s = new FsSettings();
            s.InlineOverlay = Bool(root, "InlineOverlay", s.InlineOverlay);
            s.OverlayFileSizes = Bool(root, "OverlayFileSizes", s.OverlayFileSizes);
            s.ShowSidePanel = Bool(root, "ShowSidePanel", s.ShowSidePanel);
            s.ShowWhenExplorerUnfocused = Bool(root, "ShowWhenExplorerUnfocused", s.ShowWhenExplorerUnfocused);
            s.ShowScanBadge = Bool(root, "ShowScanBadge", s.ShowScanBadge);
            s.DockRight = Bool(root, "DockRight", s.DockRight);
            s.MakeRoomForPanel = Bool(root, "MakeRoomForPanel", s.MakeRoomForPanel);
            s.ShowHidden = Bool(root, "ShowHidden", s.ShowHidden);
            s.UseFastNtfsIndex = Bool(root, "UseFastNtfsIndex", s.UseFastNtfsIndex);
            s.FollowSystemTheme = Bool(root, "FollowSystemTheme", s.FollowSystemTheme);
            s.DarkTheme = Bool(root, "DarkTheme", s.DarkTheme);
            s.PanelHotkey = Bool(root, "PanelHotkey", s.PanelHotkey);
            s.ShowFreeSpace = Bool(root, "ShowFreeSpace", s.ShowFreeSpace);
            s.PanelMenuExtras = Bool(root, "PanelMenuExtras", s.PanelMenuExtras);
            int width;
            string w = root.GetStr("PanelWidth");
            if (w != null && int.TryParse(w, NumberStyles.Integer, CultureInfo.InvariantCulture, out width)) s.PanelWidth = width;
            s.PanelWidth = FsMath.Clamp(s.PanelWidth, MinPanelWidth, MaxPanelWidth);
            // Отдельная программа писала перечисление строкой; число тоже принимаем.
            JVal sort = root.Get("Sort");
            if (sort != null)
            {
                SizeSortMode mode;
                if (sort.Kind == JKind.Str && Enum.TryParse(sort.Raw, true, out mode) && Enum.IsDefined(typeof(SizeSortMode), mode)) s.Sort = mode;
                else if (sort.Kind == JKind.Num)
                {
                    int n;
                    if (int.TryParse(sort.Raw, out n) && n >= 0 && n <= 2) s.Sort = (SizeSortMode)n;
                }
            }
            return s;
        }

        internal string ToJson()
        {
            JVal o = JVal.NewObj();
            o.Set("InlineOverlay", B(InlineOverlay));
            o.Set("OverlayFileSizes", B(OverlayFileSizes));
            o.Set("ShowSidePanel", B(ShowSidePanel));
            o.Set("ShowWhenExplorerUnfocused", B(ShowWhenExplorerUnfocused));
            o.Set("ShowScanBadge", B(ShowScanBadge));
            o.Set("PanelWidth", JVal.NewNum(FsMath.Clamp(PanelWidth, MinPanelWidth, MaxPanelWidth).ToString(CultureInfo.InvariantCulture)));
            o.Set("DockRight", B(DockRight));
            o.Set("MakeRoomForPanel", B(MakeRoomForPanel));
            o.Set("ShowHidden", B(ShowHidden));
            o.Set("UseFastNtfsIndex", B(UseFastNtfsIndex));
            o.Set("Sort", JVal.NewStr(Sort.ToString()));
            o.Set("FollowSystemTheme", B(FollowSystemTheme));
            o.Set("DarkTheme", B(DarkTheme));
            o.Set("PanelHotkey", B(PanelHotkey));
            o.Set("ShowFreeSpace", B(ShowFreeSpace));
            o.Set("PanelMenuExtras", B(PanelMenuExtras));
            return Jsn.Write(o);
        }

        internal void CopyFrom(FsSettings other)
        {
            InlineOverlay = other.InlineOverlay;
            OverlayFileSizes = other.OverlayFileSizes;
            ShowSidePanel = other.ShowSidePanel;
            ShowWhenExplorerUnfocused = other.ShowWhenExplorerUnfocused;
            ShowScanBadge = other.ShowScanBadge;
            PanelWidth = other.PanelWidth;
            DockRight = other.DockRight;
            MakeRoomForPanel = other.MakeRoomForPanel;
            ShowHidden = other.ShowHidden;
            UseFastNtfsIndex = other.UseFastNtfsIndex;
            Sort = other.Sort;
            FollowSystemTheme = other.FollowSystemTheme;
            DarkTheme = other.DarkTheme;
            PanelHotkey = other.PanelHotkey;
            ShowFreeSpace = other.ShowFreeSpace;
            PanelMenuExtras = other.PanelMenuExtras;
        }

        private static JVal B(bool value)
        {
            JVal j = new JVal();
            j.Kind = JKind.Bool;
            j.B = value;
            return j;
        }

        private static bool Bool(JVal root, string name, bool fallback)
        {
            JVal v = root.Get(name);
            return v != null && v.Kind == JKind.Bool ? v.B : fallback;
        }
    }

    internal static class SystemThemeProbe
    {
        public static bool IsDarkAppMode()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = key == null ? null : key.GetValue("AppsUseLightTheme");
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Автозапуск, два способа — как у отдельной программы.
    //  Ключ Run прост, но процесс из него никогда не бывает с правами администратора — и быстрый режим
    //  молча пропадал бы после каждой перезагрузки. Поэтому с правами создаётся задача Планировщика
    //  «с наивысшими правами»: она стартует при входе без окна UAC. Ключ реестра — запасной путь.
    // ------------------------------------------------------------------ //
    internal static class FsAutoStart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = "SysDeck.FolderSize";
        public const string TaskName = "SysDeck FolderSize";
        public const string LegacyName = "FolderSizePanel";

        public static bool IsEnabled() { return HasRunEntry() || HasScheduledTask(); }
        public static bool IsElevatedAtLogon() { return HasScheduledTask(); }

        public static bool Enable()
        {
            // Задача с правами при входе — то, что сохраняет быстрый режим после перезагрузки.
            if (Elevation.IsElevated && CreateScheduledTask() == null)
            {
                RemoveRunEntry();
                return true;
            }
            return CreateRunEntry();
        }

        public static void Disable()
        {
            RemoveRunEntry();
            RemoveScheduledTask();
        }

        public static bool HasRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    string value = key == null ? null : key.GetValue(ValueName) as string;
                    return value != null && value.IndexOf(FsMode.Switch, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { return false; }
        }

        public static bool CreateRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (key == null) return false;
                    key.SetValue(ValueName, "\"" + FsPaths.ExecutablePath + "\" " + FsMode.Switch);
                }
                return true;
            }
            catch (Exception ex) { FsLog.Report(ex); return false; }
        }

        public static void RemoveRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null) key.DeleteValue(ValueName, false);
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        public static bool HasScheduledTask() { return RunSchTasks("/Query /TN \"" + TaskName + "\"") == 0; }

        // null — успех, иначе причина. Задача с наивысшими правами создаётся только из процесса с правами.
        public static string CreateScheduledTask()
        {
            string xmlPath = Path.Combine(Path.GetTempPath(), "wpc-foldersize-" + Process.GetCurrentProcess().Id + ".xml");
            try
            {
                // schtasks читает файл по объявлению внутри — UTF-16 с BOM.
                File.WriteAllText(xmlPath, TaskXml(FsPaths.ExecutablePath), new UnicodeEncoding(false, true));
                int code = RunSchTasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F");
                return code == 0 ? null : "schtasks → " + code;
            }
            catch (Exception ex) { FsLog.Report(ex); return ex.Message; }
            finally { try { File.Delete(xmlPath); } catch { } }
        }

        public static bool RemoveScheduledTask()
        {
            int code = RunSchTasks("/Delete /TN \"" + TaskName + "\" /F");
            return code == 0 || code == 1;
        }

        // Запуск уже созданной задачи: с правами администратора и без окна UAC — ради этого она и есть.
        public static bool RunScheduledTask() { return RunSchTasks("/Run /TN \"" + TaskName + "\"") == 0; }

        // ---- отдельная FolderSizePanel ----
        public static bool LegacyRunEntryPresent()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey))
                    return key != null && key.GetValue(LegacyName) != null;
            }
            catch { return false; }
        }

        public static bool LegacyTaskPresent() { return RunSchTasks("/Query /TN \"" + LegacyName + "\"") == 0; }

        public static void RemoveLegacyRunEntry()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, true))
                    if (key != null) key.DeleteValue(LegacyName, false);
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        private static int RunSchTasks(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return -1;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { FsLog.Report(ex); return -1; }
        }

        internal static string TaskXml(string exe)
        {
            string sid = null;
            try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value; }
            catch { }
            string account = Environment.UserDomainName + "\\" + Environment.UserName;
            string principal = string.IsNullOrEmpty(sid) ? account : sid;
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            sb.Append("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            sb.Append("  <RegistrationInfo><Description>SysDeck FolderSize</Description></RegistrationInfo>\r\n");
            sb.Append("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>").Append(Escape(account)).Append("</UserId></LogonTrigger></Triggers>\r\n");
            sb.Append("  <Principals><Principal id=\"Author\"><UserId>").Append(Escape(principal)).Append("</UserId>");
            sb.Append("<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <AllowHardTerminate>true</AllowHardTerminate>\r\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n");
            sb.Append("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>true</Enabled>\r\n");
            sb.Append("    <Hidden>false</Hidden>\r\n");
            sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
            sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
            sb.Append("    <Priority>7</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(Escape(exe)).Append("</Command><Arguments>")
              .Append(FsMode.Switch).Append("</Arguments></Exec></Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }

        private static string Escape(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? "");
        }
    }

    // ------------------------------------------------------------------ //
    //  Связь основного окна с фоновым процессом.
    //  Фоновый процесс бывает с правами администратора, окно — почти всегда без. Сообщение окну из
    //  процесса ниже уровнем UIPI отбрасывает молча, а объекту ядра, созданному с правами, по умолчанию
    //  достаётся DACL только для администраторов. Поэтому события и мьютекс создаются с явным доступом
    //  для текущего пользователя — и обычное окно может и увидеть, и остановить, и позвать фоновый режим.
    // ------------------------------------------------------------------ //
    internal static class FsIpc
    {
        public const string MutexName = @"Local\SysDeck.FolderSize";
        public const string ShutdownName = @"Local\SysDeck.FolderSize.Shutdown";
        public const string ReloadName = @"Local\SysDeck.FolderSize.Reload";
        public const string ShowName = @"Local\SysDeck.FolderSize.Show";
        public const string LegacyShutdownName = @"Local\FolderSizePanel.Shutdown";
        public const string LegacyMutexName = @"Local\FolderSizePanel.SingleInstance";

        private static SecurityIdentifier User()
        {
            using (WindowsIdentity id = WindowsIdentity.GetCurrent()) return id.User;
        }

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
                // Мьютекс есть, но открыть его нам не дали — значит, первый экземпляр уже работает.
                first = false;
                return null;
            }
        }

        public static EventWaitHandle CreateEvent(string name)
        {
            bool created;
            EventWaitHandleSecurity sec = new EventWaitHandleSecurity();
            sec.AddAccessRule(new EventWaitHandleAccessRule(User(),
                EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify, AccessControlType.Allow));
            return new EventWaitHandle(false, EventResetMode.AutoReset, name, out created, sec);
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

        public static bool IsLegacyRunning()
        {
            Mutex m;
            try
            {
                if (Mutex.TryOpenExisting(LegacyMutexName, MutexRights.Synchronize, out m)) { m.Dispose(); return true; }
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { }
            try
            {
                Process[] ps = Process.GetProcessesByName(FsAutoStart.LegacyName);
                bool any = ps.Length > 0;
                foreach (Process p in ps) p.Dispose();
                return any;
            }
            catch { return false; }
        }

        public static bool Signal(string name)
        {
            EventWaitHandle h;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch (Exception) { return false; }
        }

        // Ждёт, пока фоновый процесс действительно отпустит мьютекс: новый экземпляр, запущенный раньше,
        // увидел бы старый и тихо вышел.
        public static bool WaitStopped(int timeoutMs)
        {
            long until = FsMath.NowMs + timeoutMs;
            while (IsRunning())
            {
                if (FsMath.NowMs > until) return false;
                Thread.Sleep(100);
            }
            return true;
        }
    }

    // Что основное окно знает о фоновом процессе: его пишет сам фоновый процесс при старте.
    internal sealed class FsStatus
    {
        public int Pid;
        public bool Elevated;
        public DateTime StartedUtc;

        public static void Write(bool elevated)
        {
            try
            {
                JVal o = JVal.NewObj();
                o.Set("pid", JVal.NewNum(Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture)));
                JVal e = new JVal(); e.Kind = JKind.Bool; e.B = elevated;
                o.Set("elevated", e);
                o.Set("started", JVal.NewStr(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                FsPaths.WriteAtomic(FsPaths.StatusFile, Jsn.Write(o));
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        public static void Clear()
        {
            try { File.Delete(FsPaths.StatusFile); } catch { }
        }

        // null — фоновый процесс не запущен (файл не найден или процесс с этим pid уже не наш).
        public static FsStatus Read()
        {
            if (!FsIpc.IsRunning()) return null;
            FsStatus s = new FsStatus();
            try
            {
                if (!File.Exists(FsPaths.StatusFile)) return s;
                JVal o = Jsn.Parse(File.ReadAllText(FsPaths.StatusFile));
                if (o == null || o.Kind != JKind.Obj) return s;
                int.TryParse(o.GetStr("pid"), out s.Pid);
                JVal e = o.Get("elevated");
                s.Elevated = e != null && e.Kind == JKind.Bool && e.B;
                DateTime.TryParse(o.GetStr("started"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out s.StartedUtc);
            }
            catch { }
            return s;
        }
    }

    // ------------------------------------------------------------------ //
    //  P/Invoke — вся поверхность в одном месте, чтобы её можно было проверить глазами
    // ------------------------------------------------------------------ //
    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
        public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SIZE
    {
        public int Cx, Cy;
        public SIZE(int cx, int cy) { Cx = cx; Cy = cy; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    internal static class Win32
    {
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMAXIMIZED = 3;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
        public const uint EVENT_OBJECT_NAMECHANGE = 0x800C;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int OBJID_WINDOW = 0;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new IntPtr(-2);

        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_LAYERED = 0x00080000;
        public const int WS_EX_TRANSPARENT = 0x00000020;

        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const uint GA_ROOT = 2;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate void WinEventProc(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
            int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
            WinEventProc lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        private static extern uint GetDpiForWindowNative(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "SetThreadDpiAwarenessContext")]
        private static extern IntPtr SetThreadDpiAwarenessContextNative(IntPtr context);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AttachConsole(int dwProcessId);

        // ---- слой с попиксельной альфой ----
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
            IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        public static extern int GetPixel(IntPtr hdc, int x, int y);

        public static string GetClassNameOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            return GetClassNameW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        public static string GetTitleOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(512);
            return GetWindowTextW(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
        }

        // Масштаб монитора, на котором окно; до Windows 10 1607 функции нет — тогда системный.
        public static int GetDpiForWindow(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return 0;
            try { return (int)GetDpiForWindowNative(hWnd); }
            catch (EntryPointNotFoundException) { return 0; }
        }

        // Координаты Проводника, UI Automation и наших окон должны быть в одних и тех же физических
        // пикселях, на любом из мониторов. Процесс по манифесту знает только системный DPI; поток
        // фонового режима переключается на DPI каждого монитора до создания первого окна.
        public static void UsePerMonitorDpiOnThisThread()
        {
            try { SetThreadDpiAwarenessContextNative(new IntPtr(-4)); }   // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            catch (EntryPointNotFoundException) { }
        }

        // Видимые границы: GetWindowRect включает невидимую рамку изменения размера Windows 10/11.
        public static Rectangle GetVisualBounds(IntPtr hWnd)
        {
            RECT frame;
            try
            {
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out frame, Marshal.SizeOf(typeof(RECT))) == 0
                    && frame.Width > 0 && frame.Height > 0)
                    return frame.ToRectangle();
            }
            catch (DllNotFoundException) { }
            RECT raw;
            return GetWindowRect(hWnd, out raw) ? raw.ToRectangle() : Rectangle.Empty;
        }

        // Показывает ли этот пиксель экрана именно это окно прямо сейчас? Наши поверхности — верхний
        // слой, поэтому единственное честное правило: рисовать только там, где всё ещё виден Проводник.
        // Прозрачные для мыши окна (и наши тоже) WindowFromPoint не видит — себя он не прочтёт.
        public static bool IsShowingAt(IntPtr root, Point screenPoint)
        {
            if (root == IntPtr.Zero) return false;
            IntPtr hit = WindowFromPoint(new POINT(screenPoint.X, screenPoint.Y));
            if (hit == IntPtr.Zero) return false;
            return hit == root || GetAncestor(hit, GA_ROOT) == root;
        }

        private static readonly double[,] Probes = { { 0.5, 0.5 }, { 0.15, 0.12 }, { 0.85, 0.12 }, { 0.15, 0.88 }, { 0.85, 0.88 } };

        // Виден ли хоть кусок окна. Пять проб: окно под развёрнутой программой провалит все, выглядывающее — хоть одну.
        public static bool IsAnyPartShowing(IntPtr hWnd, Rectangle bounds)
        {
            if (hWnd == IntPtr.Zero || bounds.Width <= 0 || bounds.Height <= 0) return false;
            for (int i = 0; i < Probes.GetLength(0); i++)
            {
                Point p = new Point(bounds.Left + (int)(bounds.Width * Probes[i, 0]), bounds.Top + (int)(bounds.Height * Probes[i, 1]));
                if (IsShowingAt(hWnd, p)) return true;
            }
            return false;
        }

        public static bool IsMaximized(IntPtr hWnd)
        {
            WINDOWPLACEMENT placement = new WINDOWPLACEMENT();
            placement.length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            return GetWindowPlacement(hWnd, ref placement) && placement.showCmd == SW_SHOWMAXIMIZED;
        }
    }
}
