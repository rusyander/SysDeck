// Windows Process Cleaner — автозапуск: ключи Run, папки Startup, флаги StartupApproved, задача планировщика для самого приложения
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyPathWow = @"Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Run";

        // Включена запись или нет, Windows хранит отдельно от самой записи — в StartupApproved.
        // Именно этот флаг переключают Диспетчер задач и «Параметры → Автозагрузка»; значение
        // Run (с параметрами командной строки) или ярлык при этом остаются на месте, и запись
        // можно включить обратно. Значение — 12 байт: байт 0 = флаги, бит 0 = «отключено»
        // (Диспетчер пишет 02 = вкл., 03 = откл.; встречаются 06/07 и 01 — бит тот же),
        // байты 4..11 = FILETIME момента отключения (у включённых — нули). Подраздел:
        // Run — для HKCU/HKLM\Run, Run32 — для Wow6432Node\Run, StartupFolder — для папок
        // «Автозагрузка» (имя значения = имя файла с расширением).
        private const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

        public List<AutostartEntry> GetAutostartEntries() { return GetAutostartEntries(null); }

        // stage — какой источник читается сейчас: разбор ярлыков в папках «Автозагрузка»
        // ходит на диск, и на холодном кэше это заметные секунды молчания.
        public List<AutostartEntry> GetAutostartEntries(Action<string> stage)
        {
            List<AutostartEntry> list = new List<AutostartEntry>();
            if (stage != null) stage("HKCU\\Run");
            ReadRun(Registry.CurrentUser, RunKeyPath, 0, "HKCU\\Run", list);
            if (stage != null) stage("HKLM\\Run");
            ReadRun(Registry.LocalMachine, RunKeyPath, 1, "HKLM\\Run", list);
            if (stage != null) stage("HKLM\\Run (32-bit)");
            ReadRun(Registry.LocalMachine, RunKeyPathWow, 2, "HKLM\\Run (32-bit)", list);
            if (stage != null) stage(Tr.S("Автозагрузка (пользователь)", "Startup folder (user)"));
            ReadStartupFolder(Environment.SpecialFolder.Startup, 3, Tr.S("Автозагрузка (пользователь)", "Startup folder (user)"), list);
            if (stage != null) stage(Tr.S("Автозагрузка (общая)", "Startup folder (all users)"));
            ReadStartupFolder(Environment.SpecialFolder.CommonStartup, 4, Tr.S("Автозагрузка (общая)", "Startup folder (all users)"), list);
            if (stage != null) stage(Tr.S("состояние записей, найдено: ", "entry states, found: ") + list.Count);
            foreach (AutostartEntry e in list) ReadApproved(e);
            return list;
        }

        private void ReadRun(RegistryKey root, string sub, int kind, string label, List<AutostartEntry> list)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        string cmd = k.GetValue(name) as string;
                        if (string.IsNullOrEmpty(cmd)) continue;
                        AutostartEntry e = new AutostartEntry();
                        e.Name = name; e.Command = cmd; e.ExePath = ParseExeFromCommand(cmd);
                        e.Kind = kind; e.SourceLabel = label; e.RegName = name;
                        list.Add(e);
                    }
                }
            }
            catch { }
        }

        private void ReadStartupFolder(Environment.SpecialFolder folder, int kind, string label, List<AutostartEntry> list)
        {
            try
            {
                string dir = Environment.GetFolderPath(folder);
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
                foreach (string f in Directory.GetFiles(dir))
                {
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".ini") continue;
                    string target = ext == ".lnk" ? ResolveLnk(f) : f;
                    AutostartEntry e = new AutostartEntry();
                    e.Name = Path.GetFileNameWithoutExtension(f);
                    e.Command = target != null ? target : f;
                    e.ExePath = target; e.Kind = kind; e.SourceLabel = label; e.LnkPath = f;
                    list.Add(e);
                }
            }
            catch { }
        }

        // ---------- StartupApproved: где лежит состояние записи ----------
        private static RegistryKey ApprovedRoot(int kind)
        {
            return kind == 0 || kind == 3 ? Registry.CurrentUser : Registry.LocalMachine;
        }

        private static string ApprovedSubKey(int kind)
        {
            switch (kind)
            {
                case 2: return ApprovedKeyPath + "\\Run32";
                case 3: case 4: return ApprovedKeyPath + "\\StartupFolder";
                default: return ApprovedKeyPath + "\\Run";
            }
        }

        private static string ApprovedName(AutostartEntry e)
        {
            return e.LnkPath != null ? Path.GetFileName(e.LnkPath) : e.RegName;
        }

        // Нет записи о состоянии или она пустая — Windows запускает; иначе решает бит 0 байта 0.
        public static bool ApprovedMeansEnabled(byte[] data)
        {
            return data == null || data.Length == 0 || (data[0] & 1) == 0;
        }

        // То же, что пишет Диспетчер задач: 02 + нули — включено, 03 + FILETIME — отключено.
        public static byte[] ApprovedValue(bool enabled, DateTime utcNow)
        {
            byte[] v = new byte[12];
            v[0] = (byte)(enabled ? 2 : 3);
            if (!enabled) Array.Copy(BitConverter.GetBytes(utcNow.ToFileTimeUtc()), 0, v, 4, 8);
            return v;
        }

        private static void ReadApproved(AutostartEntry e)
        {
            e.Enabled = true; e.ApprovedState = -1;
            string name = ApprovedName(e);
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                using (RegistryKey k = ApprovedRoot(e.Kind).OpenSubKey(ApprovedSubKey(e.Kind)))
                {
                    if (k == null) return;
                    byte[] data = k.GetValue(name) as byte[];
                    if (data == null || data.Length == 0) return;
                    e.ApprovedState = data[0];
                    e.Enabled = ApprovedMeansEnabled(data);
                }
            }
            catch { }
        }

        // Включить/отключить запись, не трогая её саму — как Диспетчер задач. Раньше «снять
        // галочку» удаляло значение Run или ярлык, а «поставить» писало новую запись в
        // HKCU\Run без параметров командной строки: «--minimized», «/tray» и прочие ключи
        // терялись безвозвратно, а запись из HKLM переезжала в HKCU.
        public void SetAutostartEnabled(AutostartEntry e, bool enabled)
        {
            string name = ApprovedName(e);
            if (string.IsNullOrEmpty(name))
                throw new InvalidOperationException(Tr.S("у записи автозапуска нет имени", "the startup entry has no name"));
            try
            {
                using (RegistryKey k = ApprovedRoot(e.Kind).CreateSubKey(ApprovedSubKey(e.Kind)))
                {
                    if (k == null) throw new IOException(Tr.S("раздел StartupApproved недоступен", "the StartupApproved key is not accessible"));
                    k.SetValue(name, ApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
                }
            }
            catch (Exception ex)
            {
                // Ошибка не глотается: галочка в списке должна отражать реестр, а не намерение.
                throw new IOException(Tr.S("Не удалось изменить состояние автозапуска «", "Could not change the startup state of “")
                                      + e.Name + Tr.S("» (", "” (") + e.SourceLabel + "): " + ex.Message, ex);
            }
            e.Enabled = enabled;
            e.ApprovedState = enabled ? 2 : 3;
        }

        // public: тот же нормализованный путь нужен и окну (вкладка «Автозапуск» строит по нему
        // указатель exe → записи). Своя копия там разъезжалась бы с этой при первой же правке.
        public static string NormPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            try { return Path.GetFullPath(p).TrimEnd('\\').ToLowerInvariant(); }
            catch { return p.Trim().TrimEnd('\\').ToLowerInvariant(); }
        }

        public List<AutostartEntry> EntriesForExe(string exe, List<AutostartEntry> entries)
        {
            List<AutostartEntry> r = new List<AutostartEntry>();
            string np = NormPath(exe);
            if (np == null || entries == null) return r;
            foreach (AutostartEntry e in entries)
            {
                string ep = NormPath(e.ExePath);
                if (ep != null && ep == np) r.Add(e);
            }
            return r;
        }

        // «В автозапуске» = есть хотя бы одна ВКЛЮЧЁННАЯ запись: отключённая в Диспетчере
        // задач программа при входе не стартует, и галочка у неё стоять не должна.
        public bool IsExeInAutostart(string exe, List<AutostartEntry> entries)
        {
            foreach (AutostartEntry e in EntriesForExe(exe, entries)) if (e.Enabled) return true;
            return false;
        }

        private static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "App";
            string s = name.Replace('\\', ' ').Replace('/', ' ').Trim();
            if (s.Length > 60) s = s.Substring(0, 60);
            return s;
        }

        // Новая запись в HKCU\Run. Под этим именем мог остаться флаг «отключено» от прежней
        // записи — Windows не запустила бы и новую, поэтому состояние выставляется явно.
        public AutostartEntry AddAutostart(string name, string exe)
        {
            if (string.IsNullOrEmpty(exe))
                throw new ArgumentException(Tr.S("не задан exe для автозапуска", "no exe given for startup"));
            string vn = SanitizeName(name);
            string cmd = "\"" + exe + "\"";
            try
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(RunKeyPath))
                {
                    if (k == null) throw new IOException(Tr.S("раздел Run недоступен", "the Run key is not accessible"));
                    k.SetValue(vn, cmd);
                }
            }
            catch (Exception ex)
            {
                throw new IOException(Tr.S("Не удалось добавить в автозапуск: ", "Could not add to startup: ") + ex.Message, ex);
            }
            AutostartEntry e = new AutostartEntry();
            e.Name = vn; e.Command = cmd; e.ExePath = exe; e.Kind = 0; e.SourceLabel = "HKCU\\Run"; e.RegName = vn;
            SetAutostartEnabled(e, true);
            return e;
        }

        // Галочка у установленной программы: включить все её записи, а если записей нет —
        // создать одну в HKCU\Run (новая добавляется в переданный список).
        public void EnableAutostartForExe(string name, string exe, List<AutostartEntry> entries)
        {
            List<AutostartEntry> mine = EntriesForExe(exe, entries);
            if (mine.Count == 0)
            {
                AutostartEntry e = AddAutostart(name, exe);
                if (entries != null) entries.Add(e);
                return;
            }
            foreach (AutostartEntry e in mine) if (!e.Enabled) SetAutostartEnabled(e, true);
        }

        // Снятая галочка у установленной программы: отключить все её записи (во всех ульях и
        // папках) — сами записи остаются.
        public void DisableAutostartForExe(string exe, List<AutostartEntry> entries)
        {
            foreach (AutostartEntry e in EntriesForExe(exe, entries)) if (e.Enabled) SetAutostartEnabled(e, false);
        }

        // ---------- Автозапуск с Windows ----------
        // Приложение запускается от администратора (requireAdministrator), поэтому
        // ключ реестра Run для автозапуска не годится (Windows не запускает из него
        // приложения с повышенными правами). Используем Планировщик задач с
        // наивысшими правами — тогда при входе в систему UAC не появляется.
        private const string TaskName = "WindowsProcessCleaner";
        public static string AutostartTaskName { get { return TaskName; } }

        // Возвращает null при успехе, иначе причину. Раньше результат не проверялся вовсе:
        // schtasks мог отказать, а окно писало «Настройки сохранены» — автозапуска при этом не было.
        public string ApplyAutostart(bool enabled)
        {
            // почистить возможный устаревший Run-ключ
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k != null && k.GetValue(TaskName) != null) k.DeleteValue(TaskName, false);
                }
            }
            catch { }

            try
            {
                if (!enabled) return RunSchtasks("/Delete /TN \"" + TaskName + "\" /F", false);

                string exe = Application.ExecutablePath;
                // Задача описывается XML, а не «/SC ONLOGON /RL HIGHEST»: у задачи из командной
                // строки остаются умолчания планировщика — лимит выполнения 72 часа (через трое
                // суток трей-приложение просто убивали), запрет старта и остановка на батарее.
                string xmlErr = null;
                string xmlPath = null;
                try
                {
                    xmlPath = Path.Combine(Path.GetTempPath(), "wpc-autostart-" + Process.GetCurrentProcess().Id + ".xml");
                    File.WriteAllText(xmlPath, AutostartTaskXml(exe, true), Encoding.Unicode);
                    xmlErr = RunSchtasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", true);
                }
                catch (Exception ex) { xmlErr = ex.Message; }
                finally { if (xmlPath != null) try { File.Delete(xmlPath); } catch { } }
                if (xmlErr == null) return null;

                // запасной путь — прежняя командная строка: задача хуже, но лучше, чем никакой
                string err = RunSchtasks("/Create /TN \"" + TaskName + "\" /TR \"\\\"" + exe +
                                         "\\\" /tray\" /SC ONLOGON /RL HIGHEST /F", true);
                return err == null ? null : xmlErr + "; " + err;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // Возвращает null при успехе, иначе причину (код возврата и первая строка stderr).
        private static string RunSchtasks(string args, bool creating)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args);
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                if (p == null) return Tr.S("не удалось запустить schtasks.exe", "failed to start schtasks.exe");
                // вывод крошечный, поэтому читаем по очереди: труба не переполнится
                string stderr = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                if (!p.WaitForExit(15000))
                {
                    try { p.Kill(); } catch { }
                    return Tr.S("schtasks.exe не ответил за 15 секунд", "schtasks.exe did not answer within 15 seconds");
                }
                // /Delete для несуществующей задачи возвращает 1 — выключать нечего, и это не ошибка
                if (p.ExitCode != 0 && !(!creating && p.ExitCode == 1))
                {
                    string line = (stderr ?? "").Trim();
                    int nl = line.IndexOfAny(new char[] { '\r', '\n' });
                    if (nl > 0) line = line.Substring(0, nl);
                    if (line.Length > 160) line = line.Substring(0, 160) + "…";
                    return "schtasks → " + p.ExitCode + (line.Length > 0 ? ": " + line : "");
                }
            }
            return null;
        }

        // highest = запуск с наивысшими правами (в тестах без повышения задача такой не создаётся).
        internal static string AutostartTaskXml(string exe, bool highest)
        {
            string sid = null;
            try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value; }
            catch { }
            string account = Environment.UserDomainName + "\\" + Environment.UserName;
            string principal = string.IsNullOrEmpty(sid) ? account : sid;
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            sb.Append("<Task version=\"1.4\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n");
            sb.Append("  <RegistrationInfo><Description>Windows Process Cleaner</Description></RegistrationInfo>\r\n");
            sb.Append("  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>").Append(System.Security.SecurityElement.Escape(account)).Append("</UserId></LogonTrigger></Triggers>\r\n");
            sb.Append("  <Principals><Principal id=\"Author\"><UserId>").Append(System.Security.SecurityElement.Escape(principal)).Append("</UserId>");
            sb.Append("<LogonType>InteractiveToken</LogonType><RunLevel>").Append(highest ? "HighestAvailable" : "LeastPrivilege").Append("</RunLevel></Principal></Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <AllowHardTerminate>false</AllowHardTerminate>\r\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            sb.Append("    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n");
            sb.Append("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>true</Enabled>\r\n");
            sb.Append("    <Hidden>false</Hidden>\r\n");
            sb.Append("    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n");
            sb.Append("    <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>\r\n");
            sb.Append("    <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>\r\n");
            sb.Append("    <WakeToRun>false</WakeToRun>\r\n");
            sb.Append("    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\r\n");
            sb.Append("    <Priority>7</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(System.Security.SecurityElement.Escape(exe)).Append("</Command><Arguments>/tray</Arguments></Exec></Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }
    }
}
