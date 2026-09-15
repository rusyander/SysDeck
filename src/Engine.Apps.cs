// SysDeck — установленные программы и деинсталляция
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

namespace SysDeck
{
    public partial class Engine
    {
        // ================= ДЕИНСТАЛЛЯЦИЯ ПРОГРАММ =================
        private readonly Dictionary<string, string[]> _installExes =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        public List<InstalledApp> GetInstalledApps() { return GetInstalledApps(null); }

        // stage — какой раздел реестра читается сейчас. Чтение трёх ульев с поиском exe на диске
        // занимает секунды; без этого вкладка «Программы» просто молчала всё это время.
        public List<InstalledApp> GetInstalledApps(Action<string> stage)
        {
            _installExes.Clear();
            Dictionary<string, InstalledApp> map = new Dictionary<string, InstalledApp>(StringComparer.OrdinalIgnoreCase);
            if (stage != null) stage(Tr.S("читаю раздел «Программы» (система)", "reading the system uninstall keys"));
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
            if (stage != null) stage(Tr.S("читаю раздел «Программы» (32-разрядные), найдено: ", "reading the 32-bit uninstall keys, found: ") + map.Count);
            ReadUninstall(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
            if (stage != null) stage(Tr.S("читаю раздел «Программы» (пользователь), найдено: ", "reading the per-user uninstall keys, found: ") + map.Count);
            ReadUninstall(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
            if (stage != null) stage(Tr.S("сортирую список, найдено: ", "sorting the list, found: ") + map.Count);
            List<InstalledApp> list = new List<InstalledApp>(map.Values);
            list.Sort(delegate(InstalledApp a, InstalledApp b)
            { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        private void ReadUninstall(RegistryKey root, string sub, Dictionary<string, InstalledApp> map)
        {
            try
            {
                using (RegistryKey k = root.OpenSubKey(sub))
                {
                    if (k == null) return;
                    foreach (string name in k.GetSubKeyNames())
                    {
                        try
                        {
                            using (RegistryKey s = k.OpenSubKey(name))
                            {
                                if (s == null) continue;
                                object sysComp = s.GetValue("SystemComponent");
                                if (sysComp is int && (int)sysComp == 1) continue;
                                if (s.GetValue("ParentKeyName") != null) continue; // патчи/апдейты
                                if (s.GetValue("ReleaseType") as string == "Security Update") continue;
                                string disp = s.GetValue("DisplayName") as string;
                                string unins = s.GetValue("UninstallString") as string;
                                if (string.IsNullOrEmpty(disp) || string.IsNullOrEmpty(unins)) continue;
                                InstalledApp app = new InstalledApp();
                                app.Name = disp;
                                app.Version = s.GetValue("DisplayVersion") as string;
                                app.Publisher = s.GetValue("Publisher") as string;
                                app.UninstallCmd = unins;
                                app.QuietCmd = s.GetValue("QuietUninstallString") as string;
                                string loc = s.GetValue("InstallLocation") as string;
                                app.InstallLocation = loc;
                                app.ExePath = ResolveAppExe(s.GetValue("DisplayIcon") as string, loc, disp);
                                object es = s.GetValue("EstimatedSize");
                                if (es is int) app.EstimatedSizeBytes = ((long)(int)es) * 1024L;
                                app.RegKey = (root == Registry.CurrentUser ? "HKCU\\" : "HKLM\\") + sub + "\\" + name;
                                // Ключ — имя + версия + команда: одноимённые записи из разных ульев (x86 и x64,
                                // две версии одной программы) схлопывались в одну строку, и вторую было не
                                // удалить; полностью одинаковые дубли по-прежнему показываются один раз.
                                map[disp + "\n" + (app.Version ?? "") + "\n" + unins] = app;
                            }
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }

        // Запись в реестре ещё на месте? Исчезла — деинсталляция завершилась.
        public bool UninstallEntryExists(InstalledApp app)
        {
            if (app == null || string.IsNullOrEmpty(app.RegKey)) return false;
            RegistryKey root = app.RegKey.StartsWith("HKCU\\") ? Registry.CurrentUser : Registry.LocalMachine;
            try
            {
                using (RegistryKey k = root.OpenSubKey(app.RegKey.Substring(5)))
                    return k != null && !string.IsNullOrEmpty(k.GetValue("UninstallString") as string);
            }
            catch { return false; }
        }

        private HashSet<int> _uninstallPreSnapshot;   // процессы, жившие до запуска деинсталлятора

        // Ждёт окончания деинсталляции: пока запись в реестре на месте, а запущенный процесс или
        // его потомки живы. Inno Setup (unins000.exe) и bootstrapper-ы копируют себя во временную
        // папку и выходят сразу — ожидание одного лишь запущенного процесса заканчивалось до начала
        // удаления, и список перечитывался с ещё не удалённой программой. Без дескриптора процесса
        // ждём только реестр, не дольше 30 с. true = запись исчезла.
        public bool WaitUninstall(InstalledApp app, Process started, int maxMs)
        {
            return WaitUninstall(app, started, maxMs, null, null);
        }

        // cancel — «Стоп» на вкладке. Чужой процесс по нему НЕ убивается: прерванный на середине
        // деинсталлятор оставляет программу в полуудалённом виде, а очередь ждала каждую из них до
        // 45 минут, и выйти из этого было нельзя ничем, кроме убийства приложения. По отмене мы
        // просто перестаём ждать. onProgress — чего именно ждём: без этого сорокапятиминутное
        // ожидание молчаливого мастера неотличимо от зависшего приложения.
        public bool WaitUninstall(InstalledApp app, Process started, int maxMs, Func<bool> cancel, Action<string> onProgress)
        {
            DateTime deadline = DateTime.Now.AddMilliseconds(maxMs);
            DateTime softDeadline = DateTime.Now.AddSeconds(30);
            HashSet<int> family = new HashSet<int>();
            if (started != null) { try { family.Add(started.Id); } catch { } }
            HashSet<int> pre = _uninstallPreSnapshot ?? new HashSet<int>();
            while (DateTime.Now < deadline)
            {
                if (cancel != null && cancel()) return !UninstallEntryExists(app);
                if (!UninstallEntryExists(app)) return true;
                bool alive;
                if (family.Count == 0)
                {
                    // Дескриптора процесса нет (ShellExecute отдал работу уже запущенной копии):
                    // остаётся только реестр, и ждать его вечно нельзя — отсюда 30 секунд.
                    alive = DateTime.Now < softDeadline;
                    ReportWait(onProgress, Tr.S("процесс деинсталлятора не виден — жду запись в реестре",
                                                "the uninstaller process is not visible — waiting for the registry entry"));
                }
                else
                {
                    List<RawProc> snap = Snapshot();
                    bool grown = true;
                    while (grown)
                    {
                        grown = false;
                        foreach (RawProc r in snap)
                            if (family.Contains(r.Ppid) && !family.Contains(r.Pid) && !pre.Contains(r.Pid)) { family.Add(r.Pid); grown = true; }
                    }
                    alive = false;
                    int live = 0;
                    foreach (RawProc r in snap) if (family.Contains(r.Pid)) { alive = true; live++; }
                    if (alive)
                        ReportWait(onProgress, Tr.S("деинсталлятор работает, процессов: ",
                                                    "the uninstaller is running, processes: ") + live);
                }
                if (!alive)
                {
                    ReportWait(onProgress, Tr.S("деинсталлятор завершился — проверяю реестр",
                                                "the uninstaller has exited — checking the registry"));
                    Thread.Sleep(1500);   // реестр обновляется последним — короткая пауза после последнего процесса
                    return !UninstallEntryExists(app);
                }
                Thread.Sleep(700);
            }
            return !UninstallEntryExists(app);
        }

        private static void ReportWait(Action<string> onProgress, string text)
        {
            if (onProgress == null) return;
            try { onProgress(text); } catch { }
        }

        // Запуск штатного деинсталлятора. null = запущен, иначе причина (уже локализована) —
        // и тогда ничего не запускалось. Win32Exception запуска — наверх, вызывающему.
        public string RunUninstall(InstalledApp app)
        {
            Process p;
            string err = RunUninstall(app, out p);
            if (p != null) p.Dispose();   // сам объект не нужен — только закрыть его handle
            return err;
        }

        // started = запущенный процесс (null, если ShellExecute не вернул handle) — вызывающий ждёт и закрывает.
        public string RunUninstall(InstalledApp app, out Process started)
        {
            started = null;
            string exe, args;
            if (!SplitUninstallCommand(app == null ? null : app.UninstallCmd, out exe, out args))
                return Tr.S("в реестре нет команды деинсталляции", "the registry has no uninstall command");
            bool rooted = false;
            try { rooted = Path.IsPathRooted(exe); } catch { }
            if (rooted && !File.Exists(exe))
                return Tr.S("деинсталлятор не найден: ", "uninstaller not found: ") + exe;
            // Раньше строка шла через cmd.exe /c: он снимает кавычки с пути, где есть «&», не умеет
            // путь с пробелами без кавычек (Android Studio, Steam, InstallShield) и ломается на
            // четырёх кавычках («RunDll32.EXE» «NVI2.DLL»,… у NVIDIA, «Docker Desktop Installer.exe»
            // «uninstall») — такие деинсталляторы молча не запускались. ShellExecute: exe + аргументы как есть,
            // имя без пути (winget, rundll32) ищется по PATH, рабочая папка = папка деинсталлятора.
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.UseShellExecute = true;
            if (rooted) { try { psi.WorkingDirectory = Path.GetDirectoryName(exe); } catch { } }
            HashSet<int> pre = new HashSet<int>();
            try { foreach (RawProc r in Snapshot()) pre.Add(r.Pid); } catch { }
            _uninstallPreSnapshot = pre;
            started = Process.Start(psi);
            return null;
        }

        // Делит UninstallString на exe и аргументы: первый токен в кавычках; иначе кратчайший префикс
        // до «.exe» на границе слова — без пробелов (MsiExec.exe, C:\x\u.exe) или полный путь с пробелами
        // (C:\Program Files\X\uninstall.exe -u); иначе первое слово (winget …, rundll32 …).
        // MsiExec /I{GUID} — это диалог восстановления, удаление = /X{GUID}; /I <файл.msi> не трогаем.
        internal static bool SplitUninstallCommand(string cmd, out string exe, out string args)
        {
            exe = null; args = "";
            if (string.IsNullOrEmpty(cmd)) return false;
            cmd = Environment.ExpandEnvironmentVariables(cmd).Trim();
            if (cmd.Length == 0) return false;
            char[] ws = new char[] { ' ', '\t' };
            if (cmd[0] == '"')
            {
                int end = cmd.IndexOf('"', 1);
                if (end < 0) exe = cmd.Substring(1);
                else { exe = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1); }
            }
            else
            {
                int cut = -1;
                int at = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                while (at > 0)
                {
                    int e = at + 4;
                    if (e == cmd.Length || cmd[e] == ' ' || cmd[e] == '\t')
                    {
                        string cand = cmd.Substring(0, e);
                        bool rooted = false;
                        try { rooted = Path.IsPathRooted(cand); } catch { }
                        if (cand.IndexOfAny(ws) < 0 || rooted) { cut = e; break; }
                    }
                    at = cmd.IndexOf(".exe", e, StringComparison.OrdinalIgnoreCase);
                }
                if (cut < 0)
                {
                    int sp = cmd.IndexOfAny(ws);
                    cut = sp > 0 ? sp : cmd.Length;
                }
                exe = cmd.Substring(0, cut); args = cmd.Substring(cut);
            }
            exe = exe.Trim(); args = args.Trim();
            if (exe.Length == 0) return false;
            string fn = exe;
            try { fn = Path.GetFileName(exe); } catch { }
            if (fn.Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) || fn.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                args = Regex.Replace(args, @"^[/-][iI](?=\s*\{)", "/X");
            return true;
        }

        // ================= АВТОЗАПУСК =================
        // Установщик, деинсталлятор, апдейтер, репортер — не «главный exe» программы.
        private static bool LooksLikeInstaller(string fn)
        {
            return fn.StartsWith("unins") || fn.StartsWith("setup") || fn.EndsWith("setup") || fn.Contains("install")
                || fn.Contains("update") || fn.Contains("crash") || fn.Contains("report") || fn.Contains("helper");
        }

        private string ResolveAppExe(string displayIcon, string installLoc, string name)
        {
            string iconExe = null;
            // 1) из DisplayIcon ("C:\...\app.exe,0")
            if (!string.IsNullOrEmpty(displayIcon))
            {
                string ip = displayIcon.Trim().Trim('"');
                int comma = ip.LastIndexOf(',');
                if (comma > 0)
                {
                    int idx;
                    if (int.TryParse(ip.Substring(comma + 1).Trim(), out idx)) ip = ip.Substring(0, comma);
                }
                ip = ip.Trim().Trim('"');
                try
                {
                    if (ip.ToLowerInvariant().EndsWith(".exe") && File.Exists(ip))
                    {
                        // DisplayIcon у Docker Desktop и подобных смотрит на установщик («Docker Desktop
                        // Installer.exe»): такой exe не совпадал с записью автозапуска, и программа
                        // горела оранжевым как «чужая». Сначала настоящий exe из папки установки,
                        // установщик — запасной вариант.
                        string ifn = new string(Path.GetFileNameWithoutExtension(ip).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                        if (!LooksLikeInstaller(ifn)) return ip;
                        iconExe = ip;
                    }
                }
                catch { }
            }
            // 2) поиск exe в InstallLocation по имени программы
            try
            {
                if (!string.IsNullOrEmpty(installLoc) && Directory.Exists(installLoc))
                {
                    // Разные записи реестра часто указывают на одну папку (пакеты, языковые
                    // модули): без кэша один и тот же каталог перечисляется десятки раз.
                    string cacheKey = installLoc.TrimEnd('\\').ToLowerInvariant();
                    string[] exes;
                    if (!_installExes.TryGetValue(cacheKey, out exes))
                    {
                        exes = Directory.GetFiles(installLoc, "*.exe", SearchOption.TopDirectoryOnly);
                        _installExes[cacheKey] = exes;
                    }
                    if (exes.Length == 1) return exes[0];
                    if (exes.Length > 1 && !string.IsNullOrEmpty(name))
                    {
                        // Совпадение по «содержит» в обе стороны, но не короче 3 знаков (иначе
                        // «ui.exe» подходил к любому имени), деинсталляторы и апдейтеры мимо,
                        // из нескольких подходящих — самое длинное совпадение.
                        string key = new string(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                        string best = null; int bestLen = 0;
                        foreach (string e in exes)
                        {
                            string fn = new string(Path.GetFileNameWithoutExtension(e).ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                            if (fn.Length < 3 || key.Length < 3) continue;
                            if (LooksLikeInstaller(fn)) continue;
                            if (fn == key) return e;
                            if (key.Contains(fn) || fn.Contains(key))
                            {
                                int l = Math.Min(fn.Length, key.Length);
                                if (l > bestLen) { best = e; bestLen = l; }
                            }
                        }
                        if (best != null) return best;
                    }
                }
            }
            catch { }
            return iconExe;
        }

        private string ParseExeFromCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            cmd = cmd.Trim();
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) return cmd.Substring(1, end - 1);
            }
            // Путь без кавычек, но с пробелами («C:\Program Files\X\x.exe /tray»): как и
            // CreateProcess, берём первый префикс, оканчивающийся на .exe, если он существует.
            int exeAt = cmd.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeAt > 0)
            {
                string cand = cmd.Substring(0, exeAt + 4);
                if (cand.IndexOf(' ') < 0) return cand;
                try { if (File.Exists(Environment.ExpandEnvironmentVariables(cand))) return cand; } catch { }
            }
            int sp = cmd.IndexOf(' ');
            string p = sp > 0 ? cmd.Substring(0, sp) : cmd;
            return p.Trim();
        }

        // COM-объект WScript.Shell создавался заново на КАЖДЫЙ ярлык (это запуск
        // out-of-proc сервера, десятки миллисекунд); держим один на весь процесс.
        private object _wshShell;
        private Type _wshType;
        private bool _wshFailed;

        private string ResolveLnk(string lnkPath)
        {
            if (_wshFailed) return null;
            try
            {
                if (_wshShell == null)
                {
                    _wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (_wshType == null) { _wshFailed = true; return null; }
                    _wshShell = Activator.CreateInstance(_wshType);
                }
                object sc = _wshType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, _wshShell,
                    new object[] { lnkPath });
                return (string)sc.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                    null, sc, null);
            }
            catch { return null; }
        }
    }
}
