// Windows Process Cleaner — «Windows: лишнее»: механика выключения / удаления / возврата
// (реестр, службы, задачи планировщика, Appx, PowerToys, компоненты DISM, OneDrive) и снимок
// исходного состояния. Каталог элементов — Engine.DebloatCatalog.cs.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    // Одно действие внутри элемента каталога. Kind: reg, svc, task, appx, pt, feature, cap, onedrive.
    public class DebloatOp
    {
        public string Kind;
        public string Root;              // reg: "HKLM" | "HKCU"
        public string Key;               // reg: путь раздела
        public string Name;              // reg: имя значения; svc: имя службы; task: полный путь задачи;
                                         // appx: Name пакета; pt: ключ модуля; feature/cap: имя компонента
        public object Value;             // reg: int (DWORD) или string — что записать при выключении
        public bool OnDisable = true;    // appx/cap: действует и при «Выключить» (снятие у текущего пользователя)
        public bool RemovePayload;       // feature: /Remove вместе с выключением (Recall)

        // runtime
        public int State;                // DebloatState
        public string Found;             // что нашли (полное имя пакета, тип запуска службы…)
    }

    public static class DebloatState
    {
        public const int Unknown = 0, On = 1, Off = 2, Removed = 3, Absent = 4, Partial = 5, NeedsAdmin = 6;
    }

    public class DebloatItem
    {
        public string Id, Category, Title, What, Pro, Con;
        public int Recommend;            // 0 по желанию, 1 выключить, 2 удалить
        public bool DefaultChecked;      // отмечен при первом показе — только универсальный мусор
        public bool Serious;             // предупреждение крупным: Game Bar, OneDrive, поиск Windows…
        public List<DebloatOp> Ops = new List<DebloatOp>();
        public int State;
        public string StateText, Detail;
        public bool HasSnapshot;

        public bool CanDisable
        {
            get { foreach (DebloatOp o in Ops) if (o.Kind != "appx" && o.Kind != "cap" || o.OnDisable) return true; return false; }
        }
        public bool CanRemove
        {
            get { foreach (DebloatOp o in Ops) if (o.Kind == "appx" || o.Kind == "cap" || o.Kind == "onedrive") return true; return false; }
        }
        public bool HasKind(string k) { foreach (DebloatOp o in Ops) if (o.Kind == k) return true; return false; }

        // Для построения каталога — цепочка вызовов
        public DebloatItem Reg(string root, string key, string name, object value)
        { DebloatOp o = new DebloatOp(); o.Kind = "reg"; o.Root = root; o.Key = key; o.Name = name; o.Value = value; Ops.Add(o); return this; }
        public DebloatItem Svc(string name) { DebloatOp o = new DebloatOp(); o.Kind = "svc"; o.Name = name; Ops.Add(o); return this; }
        public DebloatItem Task(string path) { DebloatOp o = new DebloatOp(); o.Kind = "task"; o.Name = path; Ops.Add(o); return this; }
        public DebloatItem Appx(string name) { return Appx(name, true); }
        public DebloatItem Appx(string name, bool onDisable)
        { DebloatOp o = new DebloatOp(); o.Kind = "appx"; o.Name = name; o.OnDisable = onDisable; Ops.Add(o); return this; }
        public DebloatItem Pt(string module) { DebloatOp o = new DebloatOp(); o.Kind = "pt"; o.Name = module; Ops.Add(o); return this; }
        public DebloatItem Feature(string name, bool removePayload)
        { DebloatOp o = new DebloatOp(); o.Kind = "feature"; o.Name = name; o.RemovePayload = removePayload; Ops.Add(o); return this; }
        public DebloatItem Cap(string prefix) { DebloatOp o = new DebloatOp(); o.Kind = "cap"; o.Name = prefix; Ops.Add(o); return this; }
        public DebloatItem OneDrive() { DebloatOp o = new DebloatOp(); o.Kind = "onedrive"; Ops.Add(o); return this; }
    }

    [DataContract]
    public class DebloatSnap
    {
        [DataMember] public string Id;
        [DataMember] public string When;
        [DataMember] public List<string> Lines;   // по одной на действие: "reg|HKCU|key|name|dword:1" / "…|absent", "svc|name|start=2|delayed=0", …
    }

    [DataContract]
    public class DebloatSnapshotFile
    {
        [DataMember] public List<DebloatSnap> Items;
    }

    public partial class Engine
    {
        public const int DebloatDisable = 1, DebloatRemove = 2, DebloatRestore = 3;

        // ---------- что нашли в системе за один проход ----------
        private class DebloatScan
        {
            public bool Admin;
            public Dictionary<string, List<string[]>> Appx = new Dictionary<string, List<string[]>>(StringComparer.OrdinalIgnoreCase); // Name -> [FullName, InstallLocation, NonRemovable]
            public Dictionary<string, string> Provisioned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);       // DisplayName -> PackageName
            public bool AppxOk, ProvOk;
            public Dictionary<string, string> Tasks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);             // path -> State
            public bool TasksOk;
            public Dictionary<string, string> Features = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);          // name -> state
            public bool FeaturesOk;
            public Dictionary<string, string[]> Caps = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);          // prefix -> [identity, state]
            public bool CapsOk;
            public Dictionary<string, bool> Pt;                                                                                     // module -> enabled; null = PowerToys нет
            public string PtPath;
            public string Log = "";
        }

        private DebloatScan _debloatScan;

        public string DebloatSnapshotPath { get { return Path.Combine(_dir, "debloat-snapshot.json"); } }

        private static bool IsAdmin()
        {
            try
            {
                using (System.Security.Principal.WindowsIdentity id = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // Отмена и живой статус вкладки «Windows: лишнее». Раньше ни одна операция здесь
        // не умела останавливаться: PS() всегда передавал cancel = null, а DISM с таймаутом
        // в 30 минут на пункт мог держать всю вкладку часами при единственной надписи на экране.
        private volatile bool _cancelDebloat;
        public void CancelDebloatWork() { _cancelDebloat = true; }
        public void ResetDebloatCancel() { _cancelDebloat = false; _debloatStatus = null; }
        public bool DebloatCancelled { get { return _cancelDebloat; } }

        private volatile string _debloatStatus;
        public string DebloatStatus { get { return _debloatStatus; } }

        private Func<bool> DebloatCancelPoll() { return delegate { return _cancelDebloat; }; }

        private Action<string, long> DebloatProgressPoll(string what)
        {
            return delegate(string line, long ms)
            {
                string t = (line ?? "").Trim();
                if (t.Length > 80) t = t.Substring(0, 80) + "…";
                _debloatStatus = what + (t.Length > 0 ? "  ·  " + t : "");
            };
        }

        // DISM на этой вкладке — с теми же тремя мерами от зависания, что и на вкладке очистки:
        // опрос отмены, живой вывод и выход сразу после итоговой строки.
        private bool DebloatDism(string args, int timeoutMs, out string so, out int code)
        {
            bool ran = RunCapture(DismPath(), args, timeoutMs, out so, out code, null,
                                  DebloatCancelPoll(), DebloatProgressPoll("DISM"), DismFinishedLine);
            _debloatStatus = null;
            return ran;
        }

        // PowerShell без profile, скрипт через -EncodedCommand: никаких кавычек в командной строке,
        // вывод в UTF-8. Возвращает false, если не запустился или не уложился в timeout.
        private static bool PS(string script, int timeoutMs, out string stdout, out int code)
        {
            return PS(script, timeoutMs, out stdout, out code, null, null);
        }

        private static bool PS(string script, int timeoutMs, out string stdout, out int code,
                               Func<bool> cancel, Action<string, long> progress)
        {
            string full = "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ErrorActionPreference='Continue'; " + script;
            string enc = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            return RunCapture(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell\\v1.0\\powershell.exe"),
                              "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + enc, timeoutMs,
                              out stdout, out code, Encoding.UTF8, cancel, progress, null);
        }

        private static string PsQuote(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        // ================= ОБНАРУЖЕНИЕ =================
        // progress получает короткие строки «что сейчас ищем»; каталог заполняется по мере готовности.
        public void DebloatDetect(List<DebloatItem> items, Action<string> progress)
        {
            DebloatScan s = new DebloatScan();
            s.Admin = IsAdmin();
            StringBuilder log = new StringBuilder();

            // 1. реестр, службы, PowerToys, OneDrive — мгновенно
            if (progress != null) progress(Tr.S("реестр и службы…", "registry and services…"));
            s.Pt = ReadPowerToys(out s.PtPath);
            EvaluateItems(items, s);

            // 2. Appx пакеты (пользователь + образ)
            if (progress != null) progress(Tr.S("приложения Microsoft Store…", "Microsoft Store apps…"));
            string so; int code;
            if (PS("Get-AppxPackage | ForEach-Object { 'A|' + $_.Name + '|' + $_.PackageFullName + '|' + $_.InstallLocation + '|' + $_.NonRemovable }; "
                   + "try { Get-AppxProvisionedPackage -Online -ErrorAction Stop | ForEach-Object { 'P|' + $_.DisplayName + '|' + $_.PackageName } } catch { 'PERR|' + $_.Exception.Message }",
                   180000, out so, out code, DebloatCancelPoll(), null))
            {
                s.AppxOk = true; s.ProvOk = true;
                foreach (string raw in so.Split('\n'))
                {
                    string line = raw.TrimEnd('\r');
                    string[] f = line.Split('|');
                    if (f.Length >= 5 && f[0] == "A")
                    {
                        List<string[]> l;
                        if (!s.Appx.TryGetValue(f[1], out l)) { l = new List<string[]>(); s.Appx[f[1]] = l; }
                        l.Add(new string[] { f[2], f[3], f[4] });
                    }
                    else if (f.Length >= 3 && f[0] == "P") s.Provisioned[f[1]] = f[2];
                    else if (f[0] == "PERR") { s.ProvOk = false; log.AppendLine("provisioned: " + line); }
                }
            }
            else log.AppendLine("Get-AppxPackage: " + (code == RunTimeout ? "timeout" : "exit " + code));
            EvaluateItems(items, s);

            // 3. задачи планировщика — только те, что есть в каталоге
            if (progress != null) progress(Tr.S("задачи планировщика…", "scheduled tasks…"));
            HashSet<string> taskNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DebloatItem it in items) foreach (DebloatOp o in it.Ops) if (o.Kind == "task") taskNames.Add(o.Name);
            if (taskNames.Count > 0)
            {
                StringBuilder arr = new StringBuilder();
                foreach (string t in taskNames) { if (arr.Length > 0) arr.Append(','); arr.Append(PsQuote(t)); }
                if (PS("$w = @(" + arr + "); Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object { $p = $_.TaskPath + $_.TaskName; if ($w -contains $p) { $p + '|' + $_.State } }",
                       120000, out so, out code, DebloatCancelPoll(), null))
                {
                    s.TasksOk = true;
                    foreach (string raw in so.Split('\n'))
                    {
                        string line = raw.TrimEnd('\r');
                        int bar = line.LastIndexOf('|');
                        if (bar > 0) s.Tasks[line.Substring(0, bar)] = line.Substring(bar + 1);
                    }
                }
                else log.AppendLine("Get-ScheduledTask: " + (code == RunTimeout ? "timeout" : "exit " + code));
            }
            EvaluateItems(items, s);

            // 4. компоненты и возможности Windows — DISM, только с правами администратора
            bool needFeatures = false, needCaps = false;
            foreach (DebloatItem it in items) { if (it.HasKind("feature")) needFeatures = true; if (it.HasKind("cap")) needCaps = true; }
            if (s.Admin && needFeatures)
            {
                if (progress != null) progress(Tr.S("компоненты Windows (DISM)…", "Windows features (DISM)…"));
                if (DebloatDism("/Online /Get-Features /Format:Table /English", 120000, out so, out code) && code == 0)
                {
                    s.FeaturesOk = true;
                    ParseDismTable(so, s.Features);
                }
                else log.AppendLine("DISM /Get-Features: " + (code == RunTimeout ? "timeout" : "exit " + code));
            }
            if (s.Admin && needCaps)
            {
                if (progress != null) progress(Tr.S("возможности Windows (DISM)…", "Windows capabilities (DISM)…"));
                if (DebloatDism("/Online /Get-Capabilities /Format:Table /English", 120000, out so, out code) && code == 0)
                {
                    s.CapsOk = true;
                    Dictionary<string, string> raw = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    ParseDismTable(so, raw);
                    foreach (KeyValuePair<string, string> kv in raw)
                    {
                        int tilde = kv.Key.IndexOf('~');
                        string prefix = tilde > 0 ? kv.Key.Substring(0, tilde) : kv.Key;
                        string[] prev;
                        // из нескольких версий одной возможности берём установленную
                        if (!s.Caps.TryGetValue(prefix, out prev) || kv.Value.StartsWith("Installed", StringComparison.OrdinalIgnoreCase))
                            s.Caps[prefix] = new string[] { kv.Key, kv.Value };
                    }
                }
                else log.AppendLine("DISM /Get-Capabilities: " + (code == RunTimeout ? "timeout" : "exit " + code));
            }
            s.Log = log.ToString();
            _debloatScan = s;
            EvaluateItems(items, s);
            MarkSnapshots(items);
        }

        public string DebloatScanLog { get { return _debloatScan == null ? "" : _debloatScan.Log; } }
        public bool DebloatIsAdmin { get { return _debloatScan != null && _debloatScan.Admin; } }

        // "Feature Name | State" — строки вида "Name | Enabled"; заголовок и разделители мимо.
        private static void ParseDismTable(string text, Dictionary<string, string> into)
        {
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                int bar = line.IndexOf(" | ");
                if (bar <= 0) continue;
                string name = line.Substring(0, bar).Trim(), state = line.Substring(bar + 3).Trim();
                if (name.Length == 0 || name.StartsWith("-") || name.Equals("Feature Name", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Capability Identity", StringComparison.OrdinalIgnoreCase)) continue;
                into[name] = state;
            }
        }

        private static string PowerToysSettingsPath()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(lad, "Microsoft\\PowerToys\\settings.json");
        }

        // enabled-карта PowerToys; null = PowerToys не установлен (файла нет).
        public static Dictionary<string, bool> ReadPowerToys(out string path)
        {
            path = PowerToysSettingsPath();
            try
            {
                if (!File.Exists(path)) return null;
                JVal root = Jsn.Parse(File.ReadAllText(path, Encoding.UTF8));
                JVal en = root.Get("enabled");
                if (en == null || en.Kind != JKind.Obj) return new Dictionary<string, bool>();
                Dictionary<string, bool> map = new Dictionary<string, bool>();
                for (int i = 0; i < en.K.Count; i++) map[en.K[i]] = en.V[i].Kind == JKind.Bool && en.V[i].B;
                return map;
            }
            catch { return null; }
        }

        private static bool WritePowerToys(string module, bool enabled)
        {
            string path = PowerToysSettingsPath();
            if (!File.Exists(path)) return false;
            JVal root = Jsn.Parse(File.ReadAllText(path, Encoding.UTF8));
            JVal en = root.Get("enabled");
            if (en == null || en.Kind != JKind.Obj) return false;
            JVal b = new JVal(); b.Kind = JKind.Bool; b.B = enabled;
            en.Set(module, b);
            // PowerToys следит за файлом и применяет изменение сам; пишем как штатно — UTF-8 без BOM
            File.WriteAllText(path, Jsn.Write(root), new UTF8Encoding(false));
            return true;
        }

        private static RegistryKey RegRootOf(string root)
        {
            return root == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        }

        private static object RegRead(string root, string key, string name)
        {
            try { using (RegistryKey k = RegRootOf(root).OpenSubKey(key)) return k == null ? null : k.GetValue(name); }
            catch { return null; }
        }

        private static void RegWrite(string root, string key, string name, object value)
        {
            using (RegistryKey k = RegRootOf(root).CreateSubKey(key))
            {
                if (k == null) throw new IOException(root + "\\" + key);
                if (value is int) k.SetValue(name, (int)value, RegistryValueKind.DWord);
                else k.SetValue(name, value.ToString(), RegistryValueKind.String);
            }
        }

        private static void RegDelete(string root, string key, string name)
        {
            try { using (RegistryKey k = RegRootOf(root).OpenSubKey(key, true)) if (k != null) k.DeleteValue(name, false); } catch { }
        }

        private static bool RegEquals(object have, object want)
        {
            if (have == null || want == null) return false;
            if (want is int) return have is int && (int)have == (int)want;
            return string.Equals(have.ToString(), want.ToString(), StringComparison.OrdinalIgnoreCase);
        }

        private static string OneDriveExe()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] c = {
                Path.Combine(lad, "Microsoft\\OneDrive\\OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive\\OneDrive.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft OneDrive\\OneDrive.exe") };
            foreach (string p in c) { try { if (File.Exists(p)) return p; } catch { } }
            return null;
        }

        private static string OneDriveSetup()
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            List<string> c = new List<string>();
            c.Add(Path.Combine(win, "SysWOW64\\OneDriveSetup.exe"));
            c.Add(Path.Combine(win, "System32\\OneDriveSetup.exe"));
            try
            {
                foreach (string dir in new string[] { Path.Combine(lad, "Microsoft\\OneDrive"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft OneDrive") })
                    if (Directory.Exists(dir))
                        foreach (string sub in Directory.GetDirectories(dir)) c.Add(Path.Combine(sub, "OneDriveSetup.exe"));
            }
            catch { }
            foreach (string p in c) { try { if (File.Exists(p)) return p; } catch { } }
            return null;
        }

        private const string OneDrivePolicyKey = "SOFTWARE\\Policies\\Microsoft\\Windows\\OneDrive";
        private const string ApprovedRunKey = "Software\\Microsoft\\Windows\\CurrentVersion\\Explorer\\StartupApproved\\Run";

        // Состояние каждого действия и итог элемента.
        private void EvaluateItems(List<DebloatItem> items, DebloatScan s)
        {
            foreach (DebloatItem it in items)
            {
                StringBuilder detail = new StringBuilder();
                foreach (DebloatOp o in it.Ops)
                {
                    o.Found = null;
                    switch (o.Kind)
                    {
                        case "reg":
                        {
                            object have = RegRead(o.Root, o.Key, o.Name);
                            o.State = RegEquals(have, o.Value) ? DebloatState.Off : DebloatState.On;
                            break;
                        }
                        case "svc":
                        {
                            object start = RegRead("HKLM", "SYSTEM\\CurrentControlSet\\Services\\" + o.Name, "Start");
                            if (start == null) o.State = DebloatState.Absent;
                            else
                            {
                                int st = start is int ? (int)start : -1;
                                o.State = st == 4 ? DebloatState.Off : DebloatState.On;
                                o.Found = st == 2 ? Tr.S("автоматически", "automatic") : st == 3 ? Tr.S("вручную", "manual") : st == 4 ? Tr.S("отключена", "disabled") : "start=" + st;
                                detail.Append(o.Name).Append(": ").Append(o.Found).Append("; ");
                            }
                            break;
                        }
                        case "task":
                        {
                            string state;
                            if (!s.TasksOk) o.State = DebloatState.Unknown;
                            else if (!s.Tasks.TryGetValue(o.Name, out state)) o.State = DebloatState.Absent;
                            else o.State = state.Equals("Disabled", StringComparison.OrdinalIgnoreCase) ? DebloatState.Off : DebloatState.On;
                            break;
                        }
                        case "appx":
                        {
                            List<string[]> l;
                            if (!s.AppxOk) o.State = DebloatState.Unknown;
                            else if (s.Appx.TryGetValue(o.Name, out l))
                            {
                                o.State = DebloatState.On;
                                o.Found = l[0][0];
                                detail.Append(l[0][0]).Append("; ");
                            }
                            else if (s.Provisioned.ContainsKey(o.Name)) { o.State = DebloatState.Off; detail.Append(o.Name).Append(Tr.S(" (снято у пользователя, в образе есть); ", " (removed for the user, still in the image); ")); }
                            else o.State = DebloatState.Absent;
                            break;
                        }
                        case "pt":
                        {
                            bool en;
                            if (s.Pt == null) o.State = DebloatState.Absent;
                            else if (!s.Pt.TryGetValue(o.Name, out en)) o.State = DebloatState.Absent;
                            else o.State = en ? DebloatState.On : DebloatState.Off;
                            break;
                        }
                        case "feature":
                        {
                            string st;
                            if (!s.Admin) o.State = DebloatState.NeedsAdmin;
                            else if (!s.FeaturesOk) o.State = DebloatState.Unknown;
                            else if (!s.Features.TryGetValue(o.Name, out st)) o.State = DebloatState.Absent;
                            else { o.State = st.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase) ? DebloatState.On : DebloatState.Off; o.Found = st; }
                            break;
                        }
                        case "cap":
                        {
                            string[] c;
                            if (!s.Admin) o.State = DebloatState.NeedsAdmin;
                            else if (!s.CapsOk) o.State = DebloatState.Unknown;
                            else if (!s.Caps.TryGetValue(o.Name, out c)) o.State = DebloatState.Absent;
                            else { o.State = c[1].StartsWith("Installed", StringComparison.OrdinalIgnoreCase) ? DebloatState.On : DebloatState.Removed; o.Found = c[0]; }
                            break;
                        }
                        case "onedrive":
                        {
                            string exe = OneDriveExe();
                            object pol = RegRead("HKLM", OneDrivePolicyKey, "DisableFileSyncNGSC");
                            if (exe == null) o.State = DebloatState.Removed;
                            else { o.State = (pol is int && (int)pol == 1) ? DebloatState.Off : DebloatState.On; o.Found = exe; detail.Append(exe).Append("; "); }
                            break;
                        }
                    }
                }
                Combine(it);
                it.Detail = detail.ToString().TrimEnd(' ', ';');
            }
        }

        private static void Combine(DebloatItem it)
        {
            int on = 0, off = 0, removed = 0, absent = 0, unknown = 0, admin = 0;
            foreach (DebloatOp o in it.Ops)
            {
                switch (o.State)
                {
                    case DebloatState.On: on++; break;
                    case DebloatState.Off: off++; break;
                    case DebloatState.Removed: removed++; break;
                    case DebloatState.Absent: absent++; break;
                    case DebloatState.NeedsAdmin: admin++; break;
                    default: unknown++; break;
                }
            }
            int known = on + off + removed;
            bool pkgOnly = true;                       // только пакеты (appx/cap): «установлено» вместо «включено»
            foreach (DebloatOp o in it.Ops) if (o.Kind != "appx" && o.Kind != "cap") pkgOnly = false;
            if (known == 0)
            {
                if (admin > 0) { it.State = DebloatState.NeedsAdmin; it.StateText = Tr.S("нужны права администратора", "needs administrator rights"); }
                else if (unknown > 0) { it.State = DebloatState.Unknown; it.StateText = Tr.S("не определено", "unknown"); }
                else { it.State = DebloatState.Absent; it.StateText = pkgOnly ? Tr.S("не установлено", "not installed") : Tr.S("нет в этой системе", "not present on this system"); }
                return;
            }
            if (on == known) { it.State = DebloatState.On; it.StateText = pkgOnly ? Tr.S("установлено", "installed") : Tr.S("включено", "on"); }
            else if (on == 0 && removed > 0 && off == 0) { it.State = DebloatState.Removed; it.StateText = Tr.S("удалено", "removed"); }
            else if (on == 0) { it.State = DebloatState.Off; it.StateText = removed > 0 ? Tr.S("выключено и удалено", "off and removed") : Tr.S("выключено", "off"); }
            else { it.State = DebloatState.Partial; it.StateText = Tr.S("частично (", "partial (") + on + "/" + known + Tr.S(" активно)", " active)"); }
        }

        // ================= СНИМОК =================
        private DebloatSnapshotFile LoadDebloatSnapshot()
        {
            try
            {
                if (File.Exists(DebloatSnapshotPath))
                    using (FileStream fs = File.OpenRead(DebloatSnapshotPath))
                    {
                        DebloatSnapshotFile f = (DebloatSnapshotFile)new DataContractJsonSerializer(typeof(DebloatSnapshotFile)).ReadObject(fs);
                        if (f != null && f.Items != null) return f;
                    }
            }
            catch { }
            DebloatSnapshotFile n = new DebloatSnapshotFile(); n.Items = new List<DebloatSnap>();
            return n;
        }

        private void MarkSnapshots(List<DebloatItem> items)
        {
            DebloatSnapshotFile f = LoadDebloatSnapshot();
            HashSet<string> ids = new HashSet<string>();
            foreach (DebloatSnap sn in f.Items) ids.Add(sn.Id);
            foreach (DebloatItem it in items) it.HasSnapshot = ids.Contains(it.Id);
        }

        private DebloatSnap FindSnap(string id)
        {
            foreach (DebloatSnap sn in LoadDebloatSnapshot().Items) if (sn.Id == id) return sn;
            return null;
        }

        // Снимок берётся ОДИН раз — перед первым действием, чтобы «Вернуть» возвращало к состоянию
        // до нас, а не к промежуточному.
        private void EnsureSnapshot(DebloatItem it)
        {
            DebloatSnapshotFile f = LoadDebloatSnapshot();
            foreach (DebloatSnap sn in f.Items) if (sn.Id == it.Id) { it.HasSnapshot = true; return; }
            DebloatSnap snap = new DebloatSnap();
            snap.Id = it.Id;
            snap.When = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            snap.Lines = new List<string>();
            DebloatScan s = _debloatScan;
            foreach (DebloatOp o in it.Ops)
            {
                switch (o.Kind)
                {
                    case "reg":
                    {
                        object have = RegRead(o.Root, o.Key, o.Name);
                        string v = have == null ? "absent" : have is int ? "dword:" + (int)have : "str:" + have;
                        snap.Lines.Add("reg|" + o.Root + "|" + o.Key + "|" + o.Name + "|" + v);
                        break;
                    }
                    case "svc":
                    {
                        object start = RegRead("HKLM", "SYSTEM\\CurrentControlSet\\Services\\" + o.Name, "Start");
                        object delayed = RegRead("HKLM", "SYSTEM\\CurrentControlSet\\Services\\" + o.Name, "DelayedAutostart");
                        snap.Lines.Add("svc|" + o.Name + "|start=" + (start is int ? (int)start : -1) + "|delayed=" + (delayed is int ? (int)delayed : 0));
                        break;
                    }
                    case "task":
                        snap.Lines.Add("task|" + o.Name + "|" + (o.State == DebloatState.Off ? "disabled" : o.State == DebloatState.Absent ? "absent" : "enabled"));
                        break;
                    case "appx":
                    {
                        List<string[]> l;
                        string full = "", loc = "", prov = "";
                        if (s != null && s.Appx.TryGetValue(o.Name, out l)) { full = l[0][0]; loc = l[0][1]; }
                        if (s != null) s.Provisioned.TryGetValue(o.Name, out prov);
                        snap.Lines.Add("appx|" + o.Name + "|" + full + "|" + loc + "|" + (prov ?? ""));
                        break;
                    }
                    case "pt":
                        snap.Lines.Add("pt|" + o.Name + "|" + (o.State == DebloatState.On ? "true" : o.State == DebloatState.Off ? "false" : "absent"));
                        break;
                    case "feature":
                        snap.Lines.Add("feature|" + o.Name + "|" + (o.Found ?? (o.State == DebloatState.Absent ? "absent" : "unknown")));
                        break;
                    case "cap":
                        snap.Lines.Add("cap|" + o.Name + "|" + (o.Found ?? "") + "|" + (o.State == DebloatState.On ? "Installed" : o.State == DebloatState.Removed ? "Not Present" : "absent"));
                        break;
                    case "onedrive":
                    {
                        object pol = RegRead("HKLM", OneDrivePolicyKey, "DisableFileSyncNGSC");
                        byte[] appr = RegRead("HKCU", ApprovedRunKey, "OneDrive") as byte[];
                        snap.Lines.Add("onedrive|" + (OneDriveExe() ?? "") + "|policy=" + (pol is int ? (int)pol : -1) + "|approved=" + (appr == null ? "" : BitConverter.ToString(appr).Replace("-", "")));
                        break;
                    }
                }
            }
            f.Items.Add(snap);
            lock (_fileLock) WriteJsonAtomic(DebloatSnapshotPath, typeof(DebloatSnapshotFile), f);
            it.HasSnapshot = true;
        }

        // ================= ДЕЙСТВИЯ =================
        // Возвращает null при успехе, иначе текст ошибки; log получает построчный отчёт.
        public string DebloatApply(DebloatItem it, int action, StringBuilder log)
        {
            List<string> errors = new List<string>();
            if (action != DebloatRestore)
            {
                try { EnsureSnapshot(it); }
                catch (Exception ex) { return Tr.S("не удалось сохранить снимок: ", "could not save the snapshot: ") + ex.Message; }
            }
            DebloatSnap snap = action == DebloatRestore ? FindSnap(it.Id) : null;
            foreach (DebloatOp o in it.Ops)
            {
                try
                {
                    string line = null;
                    if (action == DebloatRestore) line = RestoreOp(it, o, snap);
                    else line = ApplyOp(it, o, action);
                    if (line != null) log.AppendLine("  " + line);
                }
                catch (Exception ex)
                {
                    errors.Add(o.Kind + " " + o.Name + ": " + ex.Message);
                    log.AppendLine("  ERR " + o.Kind + " " + o.Name + ": " + ex.Message);
                }
            }
            if (action == DebloatRestore && snap != null && errors.Count == 0)
            {
                // снимок отработал — снимаем его, чтобы следующее выключение сняло свежий
                DebloatSnapshotFile f = LoadDebloatSnapshot();
                f.Items.RemoveAll(delegate(DebloatSnap x) { return x.Id == it.Id; });
                lock (_fileLock) WriteJsonAtomic(DebloatSnapshotPath, typeof(DebloatSnapshotFile), f);
                it.HasSnapshot = false;
            }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        private static string Sc(string args)
        {
            string so; int code;
            bool ran = RunCapture(Path.Combine(Environment.SystemDirectory, "sc.exe"), args, 60000, out so, out code, null, null);
            // раньше результат запуска отбрасывался, и «не запустился» показывалось как «sc … → -1»
            return code == 0 ? null : "sc " + args + " → " + RunFailText(ran, code);
        }

        private static string Schtasks(string task, bool enable)
        {
            string so; int code;
            bool ran = RunCapture(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), "/Change /TN \"" + task + "\" " + (enable ? "/Enable" : "/Disable"), 60000, out so, out code, null, null);
            return code == 0 ? null : "schtasks " + task + " → " + RunFailText(ran, code);
        }

        private string ApplyOp(DebloatItem it, DebloatOp o, int action)
        {
            string so; int code;
            switch (o.Kind)
            {
                case "reg":
                    RegWrite(o.Root, o.Key, o.Name, o.Value);
                    return o.Root + "\\" + o.Key + "\\" + o.Name + " = " + o.Value;
                case "svc":
                {
                    if (o.State == DebloatState.Absent) return null;
                    string e = Sc("config " + o.Name + " start= disabled");
                    if (e != null) throw new IOException(e);
                    Sc("stop " + o.Name);   // 1062 = не была запущена, это нормально
                    return Tr.S("служба ", "service ") + o.Name + Tr.S(" отключена и остановлена", " disabled and stopped");
                }
                case "task":
                {
                    if (o.State == DebloatState.Absent) return null;
                    string e = Schtasks(o.Name, false);
                    if (e != null) throw new IOException(e);
                    return Tr.S("задача ", "task ") + o.Name + Tr.S(" отключена", " disabled");
                }
                case "appx":
                {
                    if (action == DebloatDisable && !o.OnDisable) return null;
                    if (o.State == DebloatState.Absent || (o.State == DebloatState.Off && action == DebloatDisable)) return null;
                    string script = "$n = " + PsQuote(o.Name) + "; "
                        + "Get-AppxPackage -Name $n | Remove-AppxPackage -ErrorAction SilentlyContinue; ";
                    if (action == DebloatRemove)
                        script += "try { Get-AppxPackage -AllUsers -Name $n | Remove-AppxPackage -AllUsers -ErrorAction SilentlyContinue } catch {}; "
                                + "try { Get-AppxProvisionedPackage -Online -ErrorAction Stop | Where-Object { $_.DisplayName -eq $n } | Remove-AppxProvisionedPackage -Online -ErrorAction SilentlyContinue | Out-Null } catch {}; ";
                    script += "if (Get-AppxPackage -Name $n) { 'STILL' } else { 'GONE' }";
                    if (!PS(script, 300000, out so, out code, DebloatCancelPoll(), DebloatProgressPoll("PowerShell"))) throw new IOException("PowerShell: " + RunFailText(false, code));
                    if (so.IndexOf("GONE") < 0) throw new IOException(Tr.S("пакет остался: ", "package still present: ") + LastMeaningfulLine(so));
                    return (action == DebloatRemove ? Tr.S("пакет удалён у пользователя и из образа: ", "package removed for the user and deprovisioned: ")
                                                    : Tr.S("пакет снят у текущего пользователя: ", "package removed for the current user: ")) + o.Name;
                }
                case "pt":
                {
                    if (o.State == DebloatState.Absent) return null;
                    if (!WritePowerToys(o.Name, false)) throw new IOException(Tr.S("settings.json PowerToys не найден", "PowerToys settings.json not found"));
                    return Tr.S("модуль PowerToys выключен: ", "PowerToys module disabled: ") + o.Name;
                }
                case "feature":
                {
                    if (o.State == DebloatState.Absent || o.State == DebloatState.Off) return null;
                    string args = "/Online /Disable-Feature /FeatureName:" + o.Name + " /NoRestart /English" + (o.RemovePayload ? " /Remove" : "");
                    bool dr = DebloatDism(args, 1800000, out so, out code);
                    if (!DismOk(dr, code, so)) throw new IOException("DISM " + RunFailText(dr, code) + ": " + LastMeaningfulLine(so));
                    return Tr.S("компонент выключен: ", "feature disabled: ") + o.Name + (code == 3010 ? Tr.S(" (нужна перезагрузка)", " (reboot required)") : "");
                }
                case "cap":
                {
                    if (o.State != DebloatState.On || o.Found == null) return null;
                    bool dr = DebloatDism("/Online /Remove-Capability /CapabilityName:" + o.Found + " /NoRestart /English", 1800000, out so, out code);
                    if (!DismOk(dr, code, so)) throw new IOException("DISM " + RunFailText(dr, code) + ": " + LastMeaningfulLine(so));
                    return Tr.S("возможность удалена: ", "capability removed: ") + o.Found;
                }
                case "onedrive":
                {
                    RegWrite("HKLM", OneDrivePolicyKey, "DisableFileSyncNGSC", 1);
                    RunCapture(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), "/IM OneDrive.exe /F", 30000, out so, out code, null, DebloatCancelPoll());
                    if (RegRead("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "OneDrive") != null)
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ApprovedRunKey))
                            if (k != null) k.SetValue("OneDrive", ApprovedValue(false, DateTime.UtcNow), RegistryValueKind.Binary);
                    }
                    if (action == DebloatRemove)
                    {
                        string setup = OneDriveSetup();
                        if (setup == null) throw new IOException(Tr.S("OneDriveSetup.exe не найден — удалите через «Программы»", "OneDriveSetup.exe not found — uninstall via “Programs”"));
                        bool osr = RunCapture(setup, "/uninstall", 600000, out so, out code, null, DebloatCancelPoll());
                        if (!osr || !IsInstallOk(code)) throw new IOException("OneDriveSetup: " + RunFailText(osr, code));
                        return Tr.S("OneDrive удалён (", "OneDrive uninstalled (") + setup + ")";
                    }
                    return Tr.S("OneDrive остановлен, синхронизация запрещена политикой, автозапуск отключён", "OneDrive stopped, sync blocked by policy, autostart disabled");
                }
            }
            return null;
        }

        private static string[] SnapLine(DebloatSnap snap, string kind, string name)
        {
            if (snap == null || snap.Lines == null) return null;
            foreach (string l in snap.Lines)
            {
                string[] f = l.Split('|');
                if (f.Length < 2 || f[0] != kind) continue;
                if (kind == "reg") { if (f.Length >= 5 && f[2] + "|" + f[3] == name) return f; }
                else if (kind == "onedrive") return f;
                else if (f[1] == name) return f;
            }
            return null;
        }

        // Возврат: по снимку, а без него — к умолчаниям Windows (значение политики удаляется,
        // служба «вручную», задача включена, модуль PowerToys включён, пакет — из образа или Store).
        private string RestoreOp(DebloatItem it, DebloatOp o, DebloatSnap snap)
        {
            string so; int code;
            switch (o.Kind)
            {
                case "reg":
                {
                    string[] f = SnapLine(snap, "reg", o.Key + "|" + o.Name);
                    string v = f == null ? "absent" : f[4];
                    if (v == "absent") { RegDelete(o.Root, o.Key, o.Name); return o.Root + "\\" + o.Key + "\\" + o.Name + Tr.S(" удалено (умолчание Windows)", " removed (Windows default)"); }
                    if (v.StartsWith("dword:")) RegWrite(o.Root, o.Key, o.Name, int.Parse(v.Substring(6)));
                    else RegWrite(o.Root, o.Key, o.Name, v.StartsWith("str:") ? v.Substring(4) : v);
                    return o.Root + "\\" + o.Key + "\\" + o.Name + " = " + v;
                }
                case "svc":
                {
                    if (o.State == DebloatState.Absent) return null;
                    string[] f = SnapLine(snap, "svc", o.Name);
                    int start = 3; bool delayed = false;
                    if (f != null && f.Length >= 4) { int.TryParse(f[2].Replace("start=", ""), out start); delayed = f[3] == "delayed=1"; }
                    if (start == 4) return Tr.S("служба была отключена и до нас: ", "service was already disabled before: ") + o.Name;
                    if (start < 0) start = 3;
                    string mode = start == 2 ? (delayed ? "delayed-auto" : "auto") : start == 3 ? "demand" : start == 1 ? "system" : "boot";
                    string e = Sc("config " + o.Name + " start= " + mode);
                    if (e != null) throw new IOException(e);
                    if (start == 2) Sc("start " + o.Name);
                    return Tr.S("служба ", "service ") + o.Name + ": " + mode;
                }
                case "task":
                {
                    if (o.State == DebloatState.Absent) return null;
                    string[] f = SnapLine(snap, "task", o.Name);
                    if (f != null && f.Length >= 3 && f[2] == "disabled") return Tr.S("задача была отключена и до нас: ", "task was already disabled before: ") + o.Name;
                    string e = Schtasks(o.Name, true);
                    if (e != null) throw new IOException(e);
                    return Tr.S("задача включена: ", "task enabled: ") + o.Name;
                }
                case "appx":
                {
                    if (o.State == DebloatState.On) return null;
                    string[] f = SnapLine(snap, "appx", o.Name);
                    string loc = f != null && f.Length >= 4 ? f[3] : "";
                    string full = f != null && f.Length >= 3 ? f[2] : "";
                    // Имя семейства = Name_PublisherId: регистрация по нему поднимает пакет из образа
                    // (staged/provisioned) и без прав администратора, когда -AllUsers недоступен.
                    string fam = "";
                    int us = full.LastIndexOf("__");
                    if (us > 0) fam = o.Name + "_" + full.Substring(us + 2);
                    string script = "$n = " + PsQuote(o.Name) + "; $p = $null; try { $p = Get-AppxPackage -AllUsers -Name $n -ErrorAction Stop | Select-Object -First 1 } catch {}; "
                        + "if ($p -and (Test-Path ($p.InstallLocation + '\\AppxManifest.xml'))) { Add-AppxPackage -DisableDevelopmentMode -Register ($p.InstallLocation + '\\AppxManifest.xml'); 'REGISTERED' } "
                        + "elseif (" + PsQuote(loc) + " -ne '' -and (Test-Path (" + PsQuote(loc) + " + '\\AppxManifest.xml'))) { Add-AppxPackage -DisableDevelopmentMode -Register (" + PsQuote(loc) + " + '\\AppxManifest.xml'); 'REGISTERED' } "
                        + "elseif (" + PsQuote(fam) + " -ne '') { try { Add-AppxPackage -RegisterByFamilyName -MainPackage " + PsQuote(fam) + " -ErrorAction Stop; 'REGISTERED' } catch { 'MISSING' } } "
                        + "else { 'MISSING' }";
                    if (!PS(script, 300000, out so, out code, DebloatCancelPoll(), DebloatProgressPoll("PowerShell"))) throw new IOException("PowerShell: " + RunFailText(false, code));
                    if (so.IndexOf("REGISTERED") >= 0) return Tr.S("пакет заново зарегистрирован: ", "package re-registered: ") + o.Name;
                    // файлов уже нет — только Microsoft Store; открываем поиск, ставить будет пользователь
                    try { using (Process.Start("ms-windows-store://search/?query=" + Uri.EscapeDataString(it.Title))) { } } catch { }
                    return Tr.S("файлов пакета нет — открыт Microsoft Store для переустановки: ", "package files are gone — Microsoft Store opened for reinstall: ") + it.Title;
                }
                case "pt":
                {
                    if (o.State == DebloatState.Absent) return null;
                    string[] f = SnapLine(snap, "pt", o.Name);
                    bool want = f == null || f.Length < 3 || f[2] != "false";
                    if (!WritePowerToys(o.Name, want)) throw new IOException(Tr.S("settings.json PowerToys не найден", "PowerToys settings.json not found"));
                    return Tr.S("модуль PowerToys: ", "PowerToys module: ") + o.Name + " = " + (want ? Tr.S("включён", "on") : Tr.S("выключен (как до нас)", "off (as before)"));
                }
                case "feature":
                {
                    if (o.State == DebloatState.Absent || o.State == DebloatState.On) return null;
                    string[] f = SnapLine(snap, "feature", o.Name);
                    if (f != null && f.Length >= 3 && f[2].StartsWith("Disabled", StringComparison.OrdinalIgnoreCase)) return Tr.S("компонент был выключен и до нас: ", "feature was already disabled before: ") + o.Name;
                    bool dr = DebloatDism("/Online /Enable-Feature /FeatureName:" + o.Name + " /All /NoRestart /English", 1800000, out so, out code);
                    if (!DismOk(dr, code, so)) throw new IOException("DISM " + RunFailText(dr, code) + ": " + LastMeaningfulLine(so));
                    return Tr.S("компонент включён: ", "feature enabled: ") + o.Name;
                }
                case "cap":
                {
                    if (o.State == DebloatState.On || o.State == DebloatState.Absent) return null;
                    string[] f = SnapLine(snap, "cap", o.Name);
                    if (f != null && f.Length >= 4 && f[3] != "Installed") return Tr.S("возможности не было и до нас: ", "capability was absent before: ") + o.Name;
                    string id = o.Found ?? (f != null && f.Length >= 3 && f[2].Length > 0 ? f[2] : null);
                    if (id == null) throw new IOException(Tr.S("имя возможности неизвестно", "capability identity unknown"));
                    bool dr = DebloatDism("/Online /Add-Capability /CapabilityName:" + id + " /NoRestart /English", 1800000, out so, out code);
                    if (!DismOk(dr, code, so)) throw new IOException("DISM " + RunFailText(dr, code) + ": " + LastMeaningfulLine(so));
                    return Tr.S("возможность установлена: ", "capability installed: ") + id;
                }
                case "onedrive":
                {
                    string[] f = SnapLine(snap, "onedrive", null);
                    int pol = -1;
                    if (f != null && f.Length >= 3) int.TryParse(f[2].Replace("policy=", ""), out pol);
                    if (pol == 1) RegWrite("HKLM", OneDrivePolicyKey, "DisableFileSyncNGSC", 1); else RegDelete("HKLM", OneDrivePolicyKey, "DisableFileSyncNGSC");
                    if (RegRead("HKCU", "Software\\Microsoft\\Windows\\CurrentVersion\\Run", "OneDrive") != null)
                    {
                        using (RegistryKey k = Registry.CurrentUser.CreateSubKey(ApprovedRunKey))
                            if (k != null) k.SetValue("OneDrive", ApprovedValue(true, DateTime.UtcNow), RegistryValueKind.Binary);
                    }
                    string exe = OneDriveExe();
                    if (exe == null)
                    {
                        string setup = OneDriveSetup();
                        if (setup == null)
                        {
                            try { using (Process.Start("ms-windows-store://search/?query=OneDrive")) { } } catch { }
                            return Tr.S("установщик OneDrive не найден — открыт Microsoft Store", "OneDrive installer not found — Microsoft Store opened");
                        }
                        bool osr = RunCapture(setup, "/silent", 900000, out so, out code, null, DebloatCancelPoll());
                        if (!osr || !IsInstallOk(code)) throw new IOException("OneDriveSetup: " + RunFailText(osr, code));
                        return Tr.S("OneDrive установлен заново (", "OneDrive reinstalled (") + setup + ")";
                    }
                    try { using (Process.Start(exe, "/background")) { } } catch { }
                    return Tr.S("политика снята, автозапуск включён, OneDrive запущен", "policy removed, autostart enabled, OneDrive started");
                }
            }
            return null;
        }
    }
}
