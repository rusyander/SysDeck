// SysDeck — ядро: состояние, кэши процессов, конфиг и история, форматирование
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
        public AppConfig Config;
        private readonly string _dir;
        private readonly string _configPath;
        private readonly string _historyPath;

        // Мониторинг CPU/простоя между тиками
        private class CpuSample { public TimeSpan Cpu; public DateTime At; }
        private readonly Dictionary<int, CpuSample> _lastCpu = new Dictionary<int, CpuSample>();
        private readonly Dictionary<int, double> _cpuPercent = new Dictionary<int, double>();
        private readonly Dictionary<int, DateTime> _idleSince = new Dictionary<int, DateTime>();

        // MonitorTick и Scan работают в фоновых потоках и оба трогают словари ниже —
        // весь доступ только под _sync. Взаимная сериализация здесь и есть желаемое
        // поведение: два тяжёлых обхода процессов одновременно всё равно не нужны.
        private readonly object _sync = new object();

        // Путь к exe и SID владельца не меняются за жизнь процесса — кэшируем по PID,
        // сверяя время старта (PID переиспользуются). Оба запроса дорогие.
        private readonly Dictionary<int, string> _pathCache = new Dictionary<int, string>();
        private readonly Dictionary<int, string> _sidCache = new Dictionary<int, string>();
        private readonly Dictionary<int, DateTime> _startCache = new Dictionary<int, DateTime>();
        // Процессы, которые не дают PROCESS_VM_READ: не тратим на них OpenProcess дважды за тик.
        private readonly HashSet<int> _noVmRead = new HashSet<int>();

        private struct ProcStat
        {
            public bool Ok;
            public TimeSpan Cpu;
            public DateTime Start;
            public long WorkingSet;
            public string Path;
            public string Sid;
        }

        // Один OpenProcess — все нужные данные. Вызывать только под _sync.
        private ProcStat QueryStat(int pid, bool wantPath, bool wantSid)
        {
            ProcStat st = new ProcStat();
            uint baseAccess = Native.PROCESS_QUERY_LIMITED_INFORMATION;
            bool tryVm = !_noVmRead.Contains(pid);
            IntPtr h = Native.OpenProcess(tryVm ? baseAccess | Native.PROCESS_VM_READ : baseAccess, false, pid);
            if (h == IntPtr.Zero && tryVm)
            {
                _noVmRead.Add(pid);
                h = Native.OpenProcess(baseAccess, false, pid);
            }
            if (h == IntPtr.Zero) return st;
            try
            {
                TimeSpan cpu; DateTime start;
                if (Native.QueryTimes(h, out cpu, out start))
                {
                    st.Cpu = cpu; st.Start = start; st.Ok = true;

                    // PID переиспользован — старые путь/SID относятся к другому процессу
                    DateTime knownStart;
                    if (_startCache.TryGetValue(pid, out knownStart) && knownStart != start)
                    {
                        _pathCache.Remove(pid); _sidCache.Remove(pid); _noVmRead.Remove(pid);
                    }
                    _startCache[pid] = start;
                }
                st.WorkingSet = Native.QueryWorkingSet(h);

                if (wantPath)
                {
                    string cached;
                    if (_pathCache.TryGetValue(pid, out cached)) st.Path = cached;
                    else { st.Path = Native.QueryImagePath(h) ?? ""; _pathCache[pid] = st.Path; }
                }
                if (wantSid)
                {
                    string cached;
                    if (_sidCache.TryGetValue(pid, out cached)) st.Sid = cached;
                    else { st.Sid = Native.GetProcessUserSid(h); _sidCache[pid] = st.Sid; }
                }
            }
            finally { Native.CloseHandle(h); }
            return st;
        }

        // Вызывать только под _sync.
        private void ForgetDead(HashSet<int> alive)
        {
            List<int> dead = new List<int>();
            foreach (int k in _lastCpu.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (int k in dead) { _lastCpu.Remove(k); _cpuPercent.Remove(k); _idleSince.Remove(k); }
            dead.Clear();
            foreach (int k in _startCache.Keys) if (!alive.Contains(k)) dead.Add(k);
            foreach (int k in dead)
            {
                _startCache.Remove(k); _pathCache.Remove(k); _sidCache.Remove(k); _noVmRead.Remove(k);
            }
        }

        private string _currentUserSid;
        private string _winDir;
        private string _programFiles;
        private string _programFilesX86;
        private int _selfPid;

        // Никогда не завершать в глобальном режиме (сверх белого списка):
        // критичные системные/оболочечные процессы + типовые фоновые утилиты,
        // которые обычно должны работать постоянно (облака, драйверы, мессенджеры).
        private static readonly HashSet<string> _critical = new HashSet<string>(
            new string[] {
                // --- ядро Windows и оболочка ---
                "system","registry","idle","smss.exe","csrss.exe","wininit.exe","winlogon.exe",
                "services.exe","lsass.exe","lsaiso.exe","fontdrvhost.exe","dwm.exe","explorer.exe",
                "taskhostw.exe","taskhost.exe","sihost.exe","ctfmon.exe","runtimebroker.exe","conhost.exe",
                "dllhost.exe","searchhost.exe","searchapp.exe","startmenuexperiencehost.exe","shellexperiencehost.exe",
                "textinputhost.exe","applicationframehost.exe","searchindexer.exe","lockapp.exe",
                "wudfhost.exe","spoolsv.exe","audiodg.exe","memcompression","sechealthsystray.exe",
                "securityhealthservice.exe","msmpeng.exe","nissrv.exe","widgets.exe","widgetservice.exe",
                "sysdeck.exe","windowsprocesscleaner.exe","dax3api.exe","phoneexperiencehost.exe",
                // --- облака / синхронизация ---
                "onedrive.exe","dropbox.exe","dropboxupdate.exe","googledrivefs.exe","googledrivesync.exe",
                "yandexdisk.exe","yandexdisk2.exe","megasync.exe","nextcloud.exe",
                // --- мессенджеры / медиа (обычно свёрнуты в трей без окна) ---
                "telegram.exe","discord.exe","slack.exe","teams.exe","ms-teams.exe","whatsapp.exe",
                "spotify.exe","zoom.exe","viber.exe","skype.exe",
                // --- драйверы / вендорские службы ---
                "nvcontainer.exe","nvsphelper64.exe","nvidia web helper.exe","nvdisplay.container.exe",
                "rtkauduservice64.exe","ravbg64.exe","igfxem.exe","igfxext.exe","igfxtray.exe",
                "lghub.exe","lghub_agent.exe","logioptionsplus_agent.exe","logi_lamparray_service.exe",
                "razer synapse service.exe","razercentralservice.exe","steelseriesgg.exe",
                "armourycrate.service.exe","icue.exe","corsair.service.exe",
                // --- прочее ПО, которому нужен фон ---
                "steam.exe","steamwebhelper.exe","epicgameslauncher.exe","msedgewebview2.exe",
                "adobeupdateservice.exe","creative cloud.exe","ccxprocess.exe","acrotray.exe",
                "1password.exe","bitwarden.exe","keepass.exe","keepassxc.exe"
            }, StringComparer.OrdinalIgnoreCase);

        // Семейства вендорских служб с меняющимися именами (AsusCertService, ArmouryCrate.UserSessionHelper,
        // RazerCentralService…): по префиксу, иначе каждое имя пришлось бы перечислять.
        private static readonly string[] _criticalPrefixes = new string[] { "asus", "armourycrate", "razer", "corsair", "nvidia" };

        private static bool IsCriticalName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (_critical.Contains(name)) return true;
            foreach (string p in _criticalPrefixes)
                if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // Папка данных — одна для окна, headless-режимов и crash.log (Program.ReportCrash).
        //
        // Три источника, в порядке убывания приоритета:
        //  1. переменная SYSDECK_DATA_DIR — так тесты работают, не трогая настоящие данные
        //     пользователя (иначе первый же прогон переписал бы его конфиг и историю);
        //  2. файл-метка portable.marker рядом с exe — портативная сборка держит всё в своей
        //     папке и не оставляет следов в профиле: флешку вынули, и на компьютере пусто;
        //  3. обычное место — %APPDATA%\SysDeck (установленная сборка); пока переезд со старого имени не удался —
        //     %APPDATA%\WindowsProcessCleaner (Rebrand).
        public const string PortableMarker = "portable.marker";

        public static string DefaultDataDir()
        {
            string over = null;
            try { over = Environment.GetEnvironmentVariable("SYSDECK_DATA_DIR"); }
            catch { }
            if (!string.IsNullOrEmpty(over)) return over.Trim().TrimEnd('\\');

            string portable = PortableDataDir();
            if (portable != null) return portable;

            return Rebrand.ResolveStandardDataDir();
        }

        // Портативный режим: метка рядом с exe И право писать в эту папку. Портативную
        // сборку вполне могут положить в Program Files, где обычному пользователю писать
        // нельзя, — тогда молчаливое падение при каждом сохранении было бы хуже отката
        // в профиль, поэтому право на запись проверяется по-настоящему.
        public static string PortableDataDir()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(baseDir)) return null;
                if (!File.Exists(Path.Combine(baseDir, PortableMarker))) return null;
                string dir = Path.Combine(baseDir.TrimEnd('\\'), "Data");
                Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "write.test");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return dir;
            }
            catch { return null; }
        }

        public static bool IsPortable { get { return PortableDataDir() != null; } }

        // Папка рядом с exe. У портативной сборки в ней лежат данные, у обычной — сама
        // программа; ни ту, ни другую очистка трогать не должна.
        internal static string ExeDir()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly() != null
                    ? Assembly.GetEntryAssembly().Location
                    : Assembly.GetExecutingAssembly().Location;
                return string.IsNullOrEmpty(exe) ? null : Path.GetDirectoryName(exe);
            }
            catch { return null; }
        }

        public Engine()
        {
            _dir = DefaultDataDir();
            Directory.CreateDirectory(_dir);
            // Свой каталог данных и своя папка (у портативной сборки) — под запретом на
            // всю глубину обхода, а не только как корень цели.
            GuardOwnFolder(_dir);
            GuardOwnFolder(ExeDir());
            _configPath = Path.Combine(_dir, "config.json");
            _historyPath = Path.Combine(_dir, "history.json");
            LoadConfig();

            try { _currentUserSid = WindowsIdentity.GetCurrent().User.Value; } catch { _currentUserSid = null; }
            try { _winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows); } catch { _winDir = @"C:\Windows"; }
            try { _programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles); } catch { _programFiles = @"C:\Program Files"; }
            try { _programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86); } catch { _programFilesX86 = @"C:\Program Files (x86)"; }
            try { _selfPid = Process.GetCurrentProcess().Id; } catch { _selfPid = 0; }
        }

        public string DataDir { get { return _dir; } }

        // ---------- Конфиг ----------
        public void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    using (FileStream fs = File.OpenRead(_configPath))
                    {
                        DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(AppConfig));
                        Config = (AppConfig)s.ReadObject(fs);
                    }
                }
            }
            catch
            {
                // битый файл откладываем рядом, а не затираем настройками по умолчанию
                Config = null;
                try { File.Copy(_configPath, _configPath + ".corrupt", true); } catch { }
            }
            if (Config == null) Config = AppConfig.Default();
            Config.Normalize();
        }

        // Один замок на config.json и history.json: историю пишут рабочие потоки (завершение
        // процессов, автоочистка по расписанию), и два одновременных AppendHistory теряли запись
        // или падали на занятом .tmp — исключение глоталось, запись просто исчезала.
        private static readonly object _fileLock = new object();

        public void SaveConfig()
        {
            lock (_fileLock)
            {
                Config.Normalize();
                WriteJsonAtomic(_configPath, typeof(AppConfig), Config);
            }
        }

        // Запись через временный файл и Replace: крах или обрыв питания посреди записи
        // раньше оставлял пустой config.json, и все настройки сбрасывались.
        private static void WriteJsonAtomic(string path, Type type, object value)
        {
            string tmp = path + ".tmp";
            using (FileStream fs = File.Create(tmp))
            {
                DataContractJsonSerializer s = new DataContractJsonSerializer(type);
                s.WriteObject(fs, value);
                fs.Flush(true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        // ---------- История ----------
        public HistoryFile LoadHistory()
        {
            try
            {
                if (File.Exists(_historyPath))
                {
                    using (FileStream fs = File.OpenRead(_historyPath))
                    {
                        DataContractJsonSerializer s = new DataContractJsonSerializer(typeof(HistoryFile));
                        HistoryFile h = (HistoryFile)s.ReadObject(fs);
                        if (h.Entries == null) h.Entries = new List<HistoryEntry>();
                        return h;
                    }
                }
            }
            catch { }
            HistoryFile empty = new HistoryFile();
            empty.Entries = new List<HistoryEntry>();
            return empty;
        }

        public void AppendHistory(HistoryEntry e)
        {
            lock (_fileLock)
            {
                HistoryFile h = LoadHistory();
                h.Entries.Insert(0, e);
                if (h.Entries.Count > 500) h.Entries = h.Entries.Take(500).ToList();
                WriteJsonAtomic(_historyPath, typeof(HistoryFile), h);
            }
        }

        // ---------- Категории для отображения ----------
        public static string Categorize(string exe)
        {
            string n = exe.ToLowerInvariant();
            if (n == "node.exe" || n == "next.exe") return "Node.js";
            if (n == "npm.exe") return "npm";
            if (n == "pnpm.exe") return "pnpm";
            if (n == "yarn.exe") return "yarn";
            if (n == "bun.exe") return "Bun";
            if (n == "python.exe" || n == "pythonw.exe") return "Python";
            if (n == "java.exe" || n == "gradle.exe") return "Java";
            if (n == "vite.exe") return "Vite";
            if (n == "webpack.exe") return "Webpack";
            if (n == "cargo.exe") return "Cargo";
            if (n == "go.exe") return "Go";
            if (n == "deno.exe") return "Deno";
            if (n == "ruby.exe") return "Ruby";
            if (n == "php.exe") return "PHP";
            return exe;
        }

        public static string FormatBytes(long b)
        {
            if (b == 0) return Tr.En ? "0 B" : "0 Б";
            double v = b;
            string[] u = Tr.En ? new string[] { "B", "KB", "MB", "GB", "TB" }
                                : new string[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString("0.0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }
}
