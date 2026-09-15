// SysDeck — «Главная»: снимок состояния системы (карточки) и проверка здоровья
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace SysDeck
{
    // Строка проверки состояния: уровень, что проверяли, подробности, что с этим делать.
    public class HealthItem
    {
        public string Id;
        public int Level;            // HealthLevel.*
        public string Title;
        public string Detail;
        public string Action;        // подпись действия; null = действия нет
        public string ActionKind;    // "boost" | "page:<Имя>" | "disk:<путь>" | "tool:<id>" | "open:<что запустить>"
        public long Bytes;           // объём, если уместен (мусор, Загрузки, файл гибернации)
    }

    public static class HealthLevel { public const int Ok = 0, Info = 1, Warn = 2; }

    // Быстрый снимок для карточек: память, диски, аптайм. Считается за миллисекунды, без обхода диска.
    public class SystemSnapshot
    {
        public int MemoryLoad;
        public long MemTotal, MemAvail;
        public List<DriveRow> Drives = new List<DriveRow>();
        public DriveRow SystemDrive;
        public TimeSpan Uptime;
        public bool Admin;
    }

    public partial class Engine
    {
        // withDrives=false — только память и аптайм: карточки обновляются каждые пару секунд, а опрос
        // DriveInfo будит спящие диски и стоит десятки миллисекунд.
        public static SystemSnapshot Snapshot(bool withDrives)
        {
            SystemSnapshot s = new SystemSnapshot();
            try
            {
                Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    s.MemoryLoad = (int)m.dwMemoryLoad;
                    s.MemTotal = (long)m.ullTotalPhys;
                    s.MemAvail = (long)m.ullAvailPhys;
                }
            }
            catch { }
            if (withDrives)
            {
                s.Drives = Drives();
                string sys = SystemDriveRoot();
                foreach (DriveRow d in s.Drives)
                    if (string.Equals(d.Name, sys, StringComparison.OrdinalIgnoreCase)) { s.SystemDrive = d; break; }
                if (s.SystemDrive == null && s.Drives.Count > 0) s.SystemDrive = s.Drives[0];
            }
            try { s.Uptime = TimeSpan.FromMilliseconds(Native.GetTickCount64()); } catch { }
            s.Admin = IsAdmin();
            return s;
        }

        public static string SystemDriveRoot()
        {
            try { return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
            catch { return @"C:\"; }
        }

        public static string FormatUptime(TimeSpan t)
        {
            int d = (int)t.TotalDays;
            if (d >= 1) return d + Tr.S(" дн ", " d ") + t.Hours + Tr.S(" ч", " h");
            if (t.TotalHours >= 1) return t.Hours + Tr.S(" ч ", " h ") + t.Minutes + Tr.S(" мин", " min");
            return t.Minutes + Tr.S(" мин", " min");
        }

        private static string DaysAgo(int days)
        {
            if (days <= 0) return Tr.S("сегодня", "today");
            if (days == 1) return Tr.S("вчера", "yesterday");
            return Tr.N(days, "день", "дня", "дней", "day", "days") + Tr.S(" назад", " ago");
        }

        // ================= ПРОВЕРКА СОСТОЯНИЯ =================
        // Пункты отдаются по мере готовности: сначала мгновенные (память, диск, аптайм), потом обход
        // автозапуска и процессов, анализ мусора, Загрузки, и в конце один вызов PowerShell (Защитник,
        // история Windows Update, точки восстановления — по 1–3 с каждый, поэтому одним процессом).
        public void HealthCheck(Action<HealthItem> onItem, Func<bool> cancel)
        {
            SystemSnapshot s = Snapshot(true);
            onItem(HealthRam(s));
            onItem(HealthDisk(s));
            onItem(HealthUptime(s));
            onItem(HealthHibernation());
            if (cancel()) return;
            onItem(HealthStartup());
            if (cancel()) return;
            onItem(HealthOrphans());
            if (cancel()) return;
            onItem(HealthJunk());
            if (cancel()) return;
            onItem(HealthDownloads(cancel));
            if (cancel()) return;
            Dictionary<string, string> ps = HealthQueryPs();
            onItem(HealthDefender(ps));
            onItem(HealthWindowsUpdate(ps));
            onItem(HealthRestorePoint(ps, s.Admin));
        }

        private static HealthItem Item(string id, int level, string title, string detail, string action, string kind)
        {
            HealthItem h = new HealthItem();
            h.Id = id; h.Level = level; h.Title = title; h.Detail = detail; h.Action = action; h.ActionKind = kind;
            return h;
        }

        private static HealthItem HealthRam(SystemSnapshot s)
        {
            long used = s.MemTotal - s.MemAvail;
            int level = s.MemoryLoad >= 85 ? HealthLevel.Warn : s.MemoryLoad >= 70 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = Tr.S("занято ", "used ") + s.MemoryLoad + " % (" + FormatBytes(used) + Tr.S(" из ", " of ") + FormatBytes(s.MemTotal) + ")";
            if (level == HealthLevel.Ok) detail += Tr.S(" — в норме", " — fine");
            return Item("ram", level, Tr.S("Оперативная память", "Memory"), detail,
                        level == HealthLevel.Ok ? null : Tr.S("Ускорить", "Boost"), level == HealthLevel.Ok ? null : "boost");
        }

        private static HealthItem HealthDisk(SystemSnapshot s)
        {
            DriveRow d = s.SystemDrive;
            if (d == null)
                return Item("disk", HealthLevel.Info, Tr.S("Системный диск", "System drive"),
                            Tr.S("не удалось прочитать сведения о дисках", "could not read drive information"), null, null);
            double freeFrac = d.Total > 0 ? (double)d.Free / d.Total : 0;
            int level = (freeFrac < 0.10 || d.Free < 15L << 30) ? HealthLevel.Warn : freeFrac < 0.20 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = d.Name.TrimEnd('\\') + Tr.S(" свободно ", " free ") + FormatBytes(d.Free) + Tr.S(" из ", " of ") + FormatBytes(d.Total)
                          + " (" + (int)Math.Round(freeFrac * 100) + " %)";
            if (level == HealthLevel.Warn) detail += Tr.S(" — мало места, Windows и обновления начнут тормозить", " — low, Windows and updates will slow down");
            return Item("disk", level, Tr.S("Системный диск", "System drive"), detail,
                        Tr.S("Открыть «Диск»", "Open “Disk”"), "page:Disk");
        }

        private static HealthItem HealthUptime(SystemSnapshot s)
        {
            double days = s.Uptime.TotalDays;
            int level = days > 30 ? HealthLevel.Warn : days > 7 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = Tr.S("без перезагрузки ", "without a reboot for ") + FormatUptime(s.Uptime);
            if (level != HealthLevel.Ok)
                detail += Tr.S(" — перезагрузка освободит память, зависшие службы и доустановит обновления",
                               " — a reboot frees memory, stuck services and finishes pending updates");
            return Item("uptime", level, Tr.S("Время работы", "Uptime"), detail, null, null);
        }

        // Гибернация: HKLM\SYSTEM\CurrentControlSet\Control\Power\HibernateEnabled (0 = powercfg /h off) плюс
        // сам файл: если hiberfil.sys есть и не пуст — место занято, что бы ни говорил реестр.
        public static bool HibernationEnabled(out long fileSize)
        {
            fileSize = 0;
            bool enabled = true;
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Power"))
                {
                    object v = k != null ? k.GetValue("HibernateEnabled") : null;
                    if (v is int) enabled = (int)v != 0;
                }
            }
            catch { }
            try
            {
                FileInfo fi = new FileInfo(Path.Combine(SystemDriveRoot(), "hiberfil.sys"));
                if (fi.Exists) fileSize = fi.Length;
            }
            catch { }
            if (fileSize > 0) enabled = true;
            return enabled;
        }

        private static HealthItem HealthHibernation()
        {
            long size;
            bool on = HibernationEnabled(out size);
            if (!on || size == 0)
                return Item("hiber", HealthLevel.Ok, Tr.S("Файл гибернации", "Hibernation file"),
                            Tr.S("гибернация выключена, hiberfil.sys места не занимает", "hibernation is off, hiberfil.sys takes no space"), null, null);
            HealthItem h = Item("hiber", HealthLevel.Info, Tr.S("Файл гибернации", "Hibernation file"),
                "hiberfil.sys " + Tr.S("занимает ", "takes ") + FormatBytes(size)
                + Tr.S(" на системном диске. Нужен для гибернации и быстрого запуска; если ими не пользуетесь — можно отключить",
                       " on the system drive. Needed for hibernation and fast startup; if you use neither, it can be turned off"),
                Tr.S("Отключить гибернацию…", "Turn hibernation off…"), "tool:hiber");
            h.Bytes = size;
            return h;
        }

        private HealthItem HealthStartup()
        {
            int on = 0, total = 0;
            try
            {
                foreach (AutostartEntry e in GetAutostartEntries()) { total++; if (e.Enabled) on++; }
            }
            catch
            {
                return Item("startup", HealthLevel.Info, Tr.S("Автозапуск", "Startup"),
                            Tr.S("не удалось прочитать записи автозапуска", "could not read startup entries"), Tr.S("Открыть «Автозапуск»", "Open “Startup”"), "page:Startup");
            }
            int level = on > 12 ? HealthLevel.Warn : on > 6 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = Tr.S("при входе запускается ", "at sign-in Windows starts ") + Tr.N(on, "программа", "программы", "программ", "program", "programs")
                          + Tr.S(" (записей всего: ", " (entries total: ") + total + ")";
            if (level != HealthLevel.Ok) detail += Tr.S(" — каждая замедляет вход и живёт в памяти", " — each one slows sign-in and stays in memory");
            return Item("startup", level, Tr.S("Автозапуск", "Startup"), detail, Tr.S("Открыть «Автозапуск»", "Open “Startup”"), "page:Startup");
        }

        private HealthItem HealthOrphans()
        {
            int n = 0; long ram = 0;
            try
            {
                foreach (ProcInfo p in Scan(Config.GlobalScan)) if (p.IsCandidate) { n++; ram += p.RamBytes; }
            }
            catch { }
            if (n == 0)
                return Item("orphans", HealthLevel.Ok, Tr.S("Заброшенные процессы", "Abandoned processes"),
                            Tr.S("не найдены", "none found") + (Config.GlobalScan ? "" : Tr.S(" (dev-режим: только отслеживаемые процессы)", " (dev mode: watched processes only)")), null, null);
            return Item("orphans", HealthLevel.Warn, Tr.S("Заброшенные процессы", "Abandoned processes"),
                        Tr.N(n, "процесс", "процесса", "процессов", "process", "processes") + Tr.S(" без родителя простаивают и держат ~", " idle with a dead parent, holding ~") + FormatBytes(ram),
                        Tr.S("Ускорить", "Boost"), "boost");
        }

        // Мусор считается той же категорией «Системный мусор», что и на вкладке очистки: тот же обход,
        // те же предохранители и исключения пользователя, — цифра совпадёт с тем, что он увидит там.
        private HealthItem HealthJunk()
        {
            CleanCategory sys = null;
            try
            {
                foreach (CleanCategory c in BuildCleanCategories()) if (c.Id == "sys") { sys = c; break; }
                // флаг отмены общий с вкладкой очистки: снимаем его, только если она сейчас ничего не делает
                if (sys != null) { TryResetDiskCancel(); AnalyzeCategory(sys); }
            }
            catch { sys = null; }
            if (sys == null)
                return Item("junk", HealthLevel.Info, Tr.S("Временные файлы", "Temporary files"),
                            Tr.S("не удалось посчитать", "could not be measured"), Tr.S("Открыть «Очистка диска»", "Open “Disk Cleanup”"), "page:Clean");
            long size = sys.Size;
            int level = size > 1L << 30 ? HealthLevel.Warn : size > 200L << 20 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = Tr.S("временные файлы, Корзина, кэш обновлений и дампы: ", "temp files, Recycle Bin, update cache and dumps: ") + FormatBytes(size)
                          + " (" + Tr.N(sys.FileCount, "файл", "файла", "файлов", "file", "files") + ")";
            if (!sys.Analyzed) detail += Tr.S(" — подсчёт прерван", " — count interrupted");
            HealthItem h = Item("junk", level, Tr.S("Временные файлы", "Temporary files"), detail,
                                Tr.S("Открыть «Очистка диска»", "Open “Disk Cleanup”"), "page:Clean");
            h.Bytes = size;
            return h;
        }

        // Папка «Загрузки» из реестра (пользователь мог её перенести), обход ограничен по времени:
        // это оценка «есть ли что разгребать», а не точная карта — точная на вкладке «Диск».
        public static string DownloadsFolder()
        {
            try
            {
                using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    object v = k != null ? k.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") : null;
                    string p = v as string;
                    if (!string.IsNullOrEmpty(p)) return Environment.ExpandEnvironmentVariables(p);
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }

        private static HealthItem HealthDownloads(Func<bool> cancel)
        {
            string dir = DownloadsFolder();
            long size = 0; int files = 0; bool partial = false;
            DateTime deadline = DateTime.UtcNow.AddSeconds(6);
            // глубина идёт вместе с путём: раньше счётчик сбрасывался на каждой итерации и лимит не работал
            Stack<KeyValuePair<string, int>> stack = new Stack<KeyValuePair<string, int>>();
            if (Directory.Exists(dir)) stack.Push(new KeyValuePair<string, int>(dir, 0));
            while (stack.Count > 0)
            {
                if (DateTime.UtcNow > deadline || cancel()) { partial = true; break; }
                KeyValuePair<string, int> cur = stack.Pop();
                string d = cur.Key;
                int depth = cur.Value;
                try
                {
                    foreach (FileSystemInfo fi in new DirectoryInfo(d).EnumerateFileSystemInfos())
                    {
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if ((fi.Attributes & FileAttributes.Directory) != 0)
                        {
                            if (depth < 24) stack.Push(new KeyValuePair<string, int>(fi.FullName, depth + 1));
                            else partial = true;
                        }
                        else { size += ((FileInfo)fi).Length; files++; }
                    }
                }
                catch { }
            }
            int level = size > 20L << 30 ? HealthLevel.Warn : size > 5L << 30 ? HealthLevel.Info : HealthLevel.Ok;
            string detail = Tr.S("папка «Загрузки»: ", "the Downloads folder: ") + FormatBytes(size) + ", " + Tr.N(files, "файл", "файла", "файлов", "file", "files")
                          + (partial ? Tr.S(" (не меньше — обход остановлен по времени)", " (at least — the walk hit its time limit)") : "");
            if (level != HealthLevel.Ok) detail += Tr.S(" — установщики и архивы после установки обычно не нужны", " — installers and archives are usually not needed after installing");
            HealthItem h = Item("downloads", level, Tr.S("Загрузки", "Downloads"), detail,
                                Tr.S("Показать в «Диск»", "Show in “Disk”"), "disk:" + dir);
            h.Bytes = size;
            return h;
        }

        // Один PowerShell на три вопроса. Каждый блок в своём try: отсутствие Защитника (сервер, сторонний
        // антивирус), пустая история обновлений или выключенное восстановление — не ошибка всего опроса.
        private static Dictionary<string, string> HealthQueryPs()
        {
            Dictionary<string, string> r = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string script =
                "$o=@{};" +
                "try{$s=Get-MpComputerStatus -ErrorAction Stop;$o.rtp=$s.RealTimeProtectionEnabled;$o.av=$s.AntivirusEnabled;$o.sig=$s.AntivirusSignatureAge;$o.quick=$s.QuickScanAge;$o.full=$s.FullScanAge;$o.mode=$s.AMRunningMode;$o.sigdate=$s.AntivirusSignatureLastUpdated.ToString('yyyy-MM-dd')}catch{$o.mperr=$_.Exception.Message};" +
                "try{$o.avp=((Get-CimInstance -Namespace root/SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop)|%{$_.displayName+'='+$_.productState}) -join ';'}catch{};" +
                "try{$h=(New-Object -ComObject Microsoft.Update.Session).CreateUpdateSearcher().QueryHistory(0,25)|?{$_.ResultCode -eq 2}|select -First 1;if($h){$o.wu=$h.Date.ToString('yyyy-MM-dd');$o.wutitle=$h.Title}else{$o.wu=''}}catch{$o.wuerr=$_.Exception.Message};" +
                "try{$rp=Get-CimInstance -Namespace root/default -ClassName SystemRestore -ErrorAction Stop|sort CreationTime|select -Last 1;if($rp){$o.rp=$rp.CreationTime.Substring(0,8);$o.rpdesc=$rp.Description}else{$o.rp=''}}catch{$o.rperr=$_.Exception.Message};" +
                "$o.GetEnumerator()|%{\"$($_.Key)=$($_.Value)\"}";
            string so; int code;
            if (!PS(script, 120000, out so, out code)) return r;
            foreach (string raw in so.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                r[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return r;
        }

        private static bool PsBool(Dictionary<string, string> d, string key, bool dflt)
        {
            string v;
            if (!d.TryGetValue(key, out v)) return dflt;
            return string.Equals(v, "True", StringComparison.OrdinalIgnoreCase);
        }

        private static long PsLong(Dictionary<string, string> d, string key, long dflt)
        {
            string v; long n;
            if (!d.TryGetValue(key, out v)) return dflt;
            return long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : dflt;
        }

        private static string PsStr(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : null;
        }

        // productState из SecurityCenter2: бит 0x1000 = продукт включён. «Windows Defender» в списке есть
        // всегда — интересуют сторонние.
        private static string ThirdPartyAv(Dictionary<string, string> d)
        {
            string avp = PsStr(d, "avp");
            if (string.IsNullOrEmpty(avp)) return null;
            List<string> names = new List<string>();
            foreach (string part in avp.Split(';'))
            {
                int eq = part.LastIndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim();
                long state;
                if (!long.TryParse(part.Substring(eq + 1).Trim(), out state)) continue;
                if ((state & 0x1000) == 0) continue;
                if (name.IndexOf("Defender", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                names.Add(name);
            }
            return names.Count == 0 ? null : string.Join(", ", names.ToArray());
        }

        private static HealthItem HealthDefender(Dictionary<string, string> d)
        {
            string title = Tr.S("Защита от вирусов", "Antivirus protection");
            string third = ThirdPartyAv(d);
            if (!d.ContainsKey("rtp"))
            {
                string err = PsStr(d, "mperr");
                string detail = third != null
                    ? Tr.S("сторонний антивирус: ", "third-party antivirus: ") + third
                    : Tr.S("не удалось опросить Защитник Windows", "could not query Windows Defender") + (err != null ? " (" + err + ")" : "");
                return Item("defender", HealthLevel.Info, title, detail, Tr.S("Открыть «Безопасность Windows»", "Open “Windows Security”"), "open:windowsdefender:");
            }
            bool rtp = PsBool(d, "rtp", false);
            string mode = PsStr(d, "mode") ?? "";
            long sig = PsLong(d, "sig", -1), quick = PsLong(d, "quick", -1);
            string sigDate = PsStr(d, "sigdate");
            if (third != null && (!rtp || mode.IndexOf("Passive", StringComparison.OrdinalIgnoreCase) >= 0))
                return Item("defender", HealthLevel.Info, title,
                            Tr.S("защиту обеспечивает сторонний антивирус: ", "protection is provided by a third-party antivirus: ") + third
                            + Tr.S("; Защитник Windows в пассивном режиме", "; Windows Defender is in passive mode"),
                            Tr.S("Открыть «Безопасность Windows»", "Open “Windows Security”"), "open:windowsdefender:");
            if (!rtp)
                return Item("defender", HealthLevel.Warn, title,
                            Tr.S("защита в реальном времени ВЫКЛЮЧЕНА — новые файлы не проверяются", "real-time protection is OFF — new files are not scanned"),
                            Tr.S("Открыть «Безопасность Windows»", "Open “Windows Security”"), "open:windowsdefender:");
            if (sig > 7)
                return Item("defender", HealthLevel.Warn, title,
                            Tr.S("антивирусные базы не обновлялись ", "antivirus definitions not updated for ") + Tr.N(sig, "день", "дня", "дней", "day", "days")
                            + (sigDate != null ? Tr.S(" (последнее: ", " (last: ") + sigDate + ")" : ""),
                            Tr.S("Обновить базы", "Update definitions"), "tool:defsig");
            bool never = quick < 0 || quick > 100000;
            if (never || quick >= 14)
                return Item("defender", HealthLevel.Info, title,
                            Tr.S("защита включена, базы свежие; быстрая проверка ", "protection is on, definitions are fresh; quick scan ")
                            + (never ? Tr.S("не выполнялась", "never ran") : Tr.S("была ", "was ") + Tr.N(quick, "день", "дня", "дней", "day", "days") + Tr.S(" назад", " ago")),
                            Tr.S("Быстрая проверка", "Quick scan"), "tool:defquick");
            return Item("defender", HealthLevel.Ok, title,
                        Tr.S("защита включена, базы обновлены ", "protection is on, definitions updated ") + DaysAgo((int)sig)
                        + Tr.S(", быстрая проверка ", ", quick scan ") + DaysAgo((int)quick), null, null);
        }

        // Ожидающая перезагрузка: два ключа, которые ставят Windows Update и обслуживание компонентов.
        public static bool RebootPending()
        {
            string[] keys = {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired",
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending" };
            foreach (string k in keys)
            {
                try { using (RegistryKey r = Registry.LocalMachine.OpenSubKey(k)) if (r != null) return true; }
                catch { }
            }
            return false;
        }

        private static HealthItem HealthWindowsUpdate(Dictionary<string, string> d)
        {
            string title = Tr.S("Обновления Windows", "Windows Update");
            string act = Tr.S("Открыть Центр обновления", "Open Windows Update");
            if (RebootPending())
                return Item("wu", HealthLevel.Warn, title,
                            Tr.S("обновления установлены, но ждут перезагрузки — до неё система работает на старых компонентах",
                                 "updates are installed but waiting for a reboot — until then the old components keep running"),
                            act, "open:ms-settings:windowsupdate");
            string wu = PsStr(d, "wu");
            if (string.IsNullOrEmpty(wu))
                return Item("wu", HealthLevel.Info, title,
                            Tr.S("история обновлений недоступна или пуста", "update history is unavailable or empty"), act, "open:ms-settings:windowsupdate");
            DateTime at;
            if (!DateTime.TryParseExact(wu, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                return Item("wu", HealthLevel.Info, title, Tr.S("история обновлений: ", "update history: ") + wu, act, "open:ms-settings:windowsupdate");
            int days = (int)(DateTime.Today - at.Date).TotalDays;
            string what = PsStr(d, "wutitle");
            string detail = Tr.S("последнее успешное обновление ", "last successful update ") + DaysAgo(days) + (what != null ? ": " + what : "");
            if (days > 45)
                return Item("wu", HealthLevel.Warn, title, detail + Tr.S(" — давно; проверьте, не остановлены ли обновления", " — long ago; check that updates are not paused"),
                            act, "open:ms-settings:windowsupdate");
            return Item("wu", HealthLevel.Ok, title, detail, null, null);
        }

        // Защита системы для системного диска: RPSessionInterval=0 или политика DisableSR=1 = выключена.
        public static bool SystemRestoreEnabled()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore"))
                {
                    if (k == null) return false;
                    object dis = k.GetValue("DisableSR");
                    if (dis is int && (int)dis != 0) return false;
                    object iv = k.GetValue("RPSessionInterval");
                    if (iv is int) return (int)iv != 0;
                }
            }
            catch { }
            return false;
        }

        private static HealthItem HealthRestorePoint(Dictionary<string, string> d, bool admin)
        {
            string title = Tr.S("Точка восстановления", "Restore point");
            string act = Tr.S("Создать точку восстановления", "Create a restore point");
            if (!SystemRestoreEnabled())
                return Item("restore", HealthLevel.Warn, title,
                            Tr.S("защита системы выключена — откатить неудачное обновление или драйвер будет нечем",
                                 "System Protection is off — there is nothing to roll back a bad update or driver to"), act, "tool:restore");
            if (!admin)
                return Item("restore", HealthLevel.Info, title,
                            Tr.S("список точек виден только с правами администратора", "the list of points is visible only with administrator rights"), act, "tool:restore");
            string err = PsStr(d, "rperr");
            if (err != null)
                return Item("restore", HealthLevel.Info, title, Tr.S("не удалось прочитать список точек: ", "could not read the list of points: ") + err, act, "tool:restore");
            string rp = PsStr(d, "rp");
            if (string.IsNullOrEmpty(rp))
                return Item("restore", HealthLevel.Warn, title, Tr.S("ни одной точки восстановления нет", "there are no restore points at all"), act, "tool:restore");
            DateTime at;
            if (!DateTime.TryParseExact(rp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out at))
                return Item("restore", HealthLevel.Info, title, Tr.S("последняя точка: ", "last point: ") + rp, act, "tool:restore");
            int days = (int)(DateTime.Today - at.Date).TotalDays;
            string desc = PsStr(d, "rpdesc");
            string detail = Tr.S("последняя точка создана ", "last point created ") + DaysAgo(days) + (desc != null ? " (" + desc + ")" : "");
            if (days > 30) return Item("restore", HealthLevel.Info, title, detail + Tr.S(" — перед удалением программ и компонентов стоит создать свежую", " — create a fresh one before removing programs and components"), act, "tool:restore");
            return Item("restore", HealthLevel.Ok, title, detail, null, null);
        }
    }
}
