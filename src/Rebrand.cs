// SysDeck — переезд со старого имени «Windows Process Cleaner» (сборки до 15.09.2026).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Что переносится и когда:
//  - папка данных %APPDATA%\WindowsProcessCleaner → %APPDATA%\SysDeck — при первом запуске любого режима, кроме
//    повышенного задания и хоста расширения. Старые фоновые процессы сперва получают свой «Shutdown» (никого не убиваем).
//    На старом месте остаётся точка соединения на новую папку: Chrome держит путь распакованного расширения, старое
//    окно, если оно ещё открыто, пишет туда же. Не вышло переименовать (файл занят) — все процессы работают со старой
//    папкой (Engine.DefaultDataDir), попытка повторится при следующем запуске;
//  - ключи Run текущего пользователя и привязки .torrent / magnet — там же, без прав;
//  - задачи Планировщика и правило брандмауэра — только кнопкой в окне, одним заданием с правами ("rebrand").
// Всё идемпотентно: когда переносить нечего, запуск стоит несколько обращений к реестру и файловой системе.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SysDeck
{
    internal static class Rebrand
    {
        public const string Brand = "SysDeck";
        public const string LegacyName = "WindowsProcessCleaner";
        public const string LegacyFirewallRule = "Windows Process Cleaner (BitTorrent)";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        // Фоновые процессы с мьютексом «Local\<имя>.<процесс>» и событием «…<процесс>.Shutdown».
        private static readonly string[] Agents = { "Capture", "Hud", "FolderSize", "Downloads" };
        private static readonly string[] RunSuffixes = { ".Capture", ".Downloads", ".FolderSize" };
        public static readonly string[] LegacyTasks = { LegacyName, LegacyName + " FolderSize", LegacyName + " HUD" };

        public static string StandardDataDir() { return Path.Combine(AppData(), Brand); }
        public static string LegacyDataDir() { return Path.Combine(AppData(), LegacyName); }

        private static string AppData() { return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }

        // Папка данных установленной сборки: новая, а пока переезд не удался — старая.
        public static string ResolveStandardDataDir()
        {
            return Resolve(LegacyDataDir(), StandardDataDir());
        }

        internal static string Resolve(string legacy, string now)
        {
            return !Directory.Exists(now) && IsRealDirectory(legacy) ? legacy : now;
        }

        // ---------- запуск ----------
        public static void MigrateOnStart()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("SYSDECK_DATA_DIR") != null || Engine.IsPortable) return;
                using (Mutex gate = new Mutex(false, @"Local\" + Brand + ".Migrate"))
                {
                    bool got;
                    try { got = gate.WaitOne(20000); }
                    catch (AbandonedMutexException) { got = true; }
                    if (!got) return;
                    try
                    {
                        string exe = Process.GetCurrentProcess().MainModule.FileName;
                        if (MigrateDataDir(LegacyDataDir(), StandardDataDir(), true))
                            Log("data folder moved from " + LegacyDataDir());
                        MigrateDocuments();
                        using (RegistryKey run = Registry.CurrentUser.OpenSubKey(RunKey, true))
                            if (run != null) foreach (string line in MigrateRunValues(run, exe)) Log(line);
                        using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software", true))
                            if (software != null && Downloads.BtAssoc.MigrateLegacy(software, exe)) Log("torrent associations moved");
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            catch (Exception ex) { Log("migration failed: " + ex.Message); }
        }

        // live — настоящий запуск: сначала остановить старые фоновые процессы и не трогать папку, пока работают новые
        // (они уже пишут в старую). Тесты зовут без live на временных папках.
        internal static bool MigrateDataDir(string legacy, string now, bool live)
        {
            if (Directory.Exists(now) || !IsRealDirectory(legacy)) return false;
            if (live)
            {
                if (AnyAgentRunning(Brand, true)) return false;
                StopLegacyAgents();
            }
            try { Directory.Move(legacy, now); }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            if (!MakeJunction(legacy, now)) Log("junction " + legacy + " was not created");
            return true;
        }

        internal static bool IsRealDirectory(string path)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(path);
                return d.Exists && (d.Attributes & FileAttributes.ReparsePoint) == 0;
            }
            catch { return false; }
        }

        // Точка соединения, а не символьная ссылка: прав администратора не требует.
        internal static bool MakeJunction(string link, string target)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/d /c mklink /J \"" + link + "\" \"" + target + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(10000)) return false;
                }
                return Directory.Exists(link) && !IsRealDirectory(link);
            }
            catch { return false; }
        }

        private static void StopLegacyAgents()
        {
            bool any = false;
            foreach (string a in Agents)
                if (SignalEvent(@"Local\" + LegacyName + "." + a + ".Shutdown")) any = true;
            if (!any) return;
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 8000 && AnyAgentRunning(LegacyName, false)) Thread.Sleep(100);
        }

        internal static bool AnyAgentRunning(string name, bool withWindow)
        {
            foreach (string a in Agents)
                if (MutexExists(@"Local\" + name + "." + a)) return true;
            return withWindow && MutexExists(@"Local\" + name + ".singleinstance");
        }

        private static bool MutexExists(string name)
        {
            Mutex m;
            try
            {
                if (!Mutex.TryOpenExisting(name, MutexRights.Synchronize, out m)) return false;
                m.Dispose();
                return true;
            }
            catch (UnauthorizedAccessException) { return true; }
            catch { return false; }
        }

        private static bool SignalEvent(string name)
        {
            EventWaitHandle h;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(name, EventWaitHandleRights.Modify | EventWaitHandleRights.Synchronize, out h)) return false;
                using (h) h.Set();
                return true;
            }
            catch { return false; }
        }

        // «Документы\WindowsProcessCleaner\Lag reports» — отчёты записи лагов. Открытых файлов там не бывает.
        private static void MigrateDocuments()
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (string.IsNullOrEmpty(docs)) return;
            string legacy = Path.Combine(docs, LegacyName), now = Path.Combine(docs, Brand);
            if (Directory.Exists(now) || !IsRealDirectory(legacy)) return;
            try { Directory.Move(legacy, now); Log("documents folder moved from " + legacy); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // ---------- ключи Run ----------
        // Значение переносится, только если оно запускало эту же копию: старый exe лежал рядом с новым или его уже нет.
        internal static List<string> MigrateRunValues(RegistryKey run, string exe)
        {
            List<string> done = new List<string>();
            foreach (string suffix in RunSuffixes)
            {
                string legacyValue = LegacyName + suffix;
                string command = run.GetValue(legacyValue) as string;
                if (command == null) continue;
                string args;
                if (!IsLegacyCopyOf(SplitCommand(command, out args), exe)) continue;
                if (run.GetValue(Brand + suffix) == null) run.SetValue(Brand + suffix, "\"" + exe + "\"" + args);
                run.DeleteValue(legacyValue, false);
                done.Add("run value " + legacyValue + " → " + Brand + suffix);
            }
            return done;
        }

        // "C:\dir\app.exe" --capture → C:\dir\app.exe и « --capture»; без кавычек — до первого пробела.
        internal static string SplitCommand(string command, out string args)
        {
            args = "";
            string c = (command ?? "").Trim();
            if (c.Length == 0) return null;
            if (c[0] == '"')
            {
                int close = c.IndexOf('"', 1);
                if (close < 0) return null;
                args = c.Substring(close + 1);
                return c.Substring(1, close - 1);
            }
            int space = c.IndexOf(' ');
            if (space < 0) return c;
            args = c.Substring(space);
            return c.Substring(0, space);
        }

        internal static bool IsLegacyCopyOf(string legacyExe, string exe)
        {
            if (string.IsNullOrEmpty(legacyExe) || string.IsNullOrEmpty(exe)) return false;
            try
            {
                if (!string.Equals(Path.GetFileName(legacyExe), LegacyName + ".exe", StringComparison.OrdinalIgnoreCase)) return false;
                if (!File.Exists(legacyExe)) return true;
                return string.Equals(Path.GetDirectoryName(Path.GetFullPath(legacyExe)), Path.GetDirectoryName(Path.GetFullPath(exe)),
                                     StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
            catch (NotSupportedException) { return false; }
        }

        // ---------- задачи Планировщика и брандмауэр (с правами) ----------
        // Окно спрашивает без прав: запрос задачи Планировщика права не требует.
        public static List<string> LegacyTasksPresent(string exe)
        {
            List<string> found = new List<string>();
            foreach (string task in LegacyTasks)
            {
                string xml;
                if (Run("schtasks.exe", "/Query /XML /TN \"" + task + "\"", out xml) != 0 || xml == null) continue;
                if (IsLegacyCopyOf(TaskCommand(xml), exe)) found.Add(task);
            }
            return found;
        }

        internal static string TaskCommand(string xml)
        {
            int start = xml.IndexOf("<Command>", StringComparison.OrdinalIgnoreCase);
            if (start < 0) return null;
            start += "<Command>".Length;
            int end = xml.IndexOf("</Command>", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            return System.Net.WebUtility.HtmlDecode(xml.Substring(start, end - start)).Trim().Trim('"');
        }

        public static bool LegacyFirewallRulePresent()
        {
            string ignored;
            return Run("netsh.exe", "advfirewall firewall show rule name=\"" + LegacyFirewallRule + "\"", out ignored) == 0;
        }

        // Задание "rebrand": из файла задания не берётся ничего. Каждая старая задача заменяется такой же под новым именем
        // на этот exe; старая удаляется, только когда новая создана. null — успех, иначе причины через «; ».
        public static string MigrateElevated(Engine engine)
        {
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            List<string> errors = new List<string>();
            foreach (string task in LegacyTasksPresent(exe))
            {
                string err;
                if (task == LegacyName) err = engine.ApplyAutostart(true);
                else if (task == LegacyName + " FolderSize") err = FolderSize.FsAutoStart.CreateScheduledTask();
                else err = Capture.HudLauncher.CreateTask();
                string ignored;
                if (err == null && Run("schtasks.exe", "/Delete /TN \"" + task + "\" /F", out ignored) != 0) err = "schtasks /Delete";
                if (err != null) errors.Add(task + ": " + err);
            }
            if (LegacyFirewallRulePresent())
            {
                string ignored;
                string err = Engine.FirewallApply(true, exe);
                if (err == null && Run("netsh.exe", "advfirewall firewall delete rule name=\"" + LegacyFirewallRule + "\"", out ignored) != 0)
                    err = "netsh delete";
                if (err != null) errors.Add(LegacyFirewallRule + ": " + err);
            }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        private static int Run(string tool, string arguments, out string output)
        {
            output = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, tool), arguments);
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
            catch { return -1; }
        }

        private static void Log(string line)
        {
            try
            {
                string dir = ResolveStandardDataDir();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "rebrand.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + "  " + line + "\r\n",
                    Encoding.UTF8);
            }
            catch { }
        }
    }
}
