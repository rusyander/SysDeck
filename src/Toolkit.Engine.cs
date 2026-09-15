// SysDeck — страница «Скрипты»: где что лежит, в каком состоянии, и какие задачи из каких параметров получаются.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Всё, что трогает машину, идёт через TkEnv: тесты подменяют корни папок, Планировщик и раздел реестра, поэтому
// настоящие задачи и файлы в профиле пользователя они не видят. Установка и удаление — Toolkit.Apply.cs.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace SysDeck.Toolkit
{
    internal sealed class TkEnv
    {
        public string UserProfile, ProgramData, LocalAppData, SystemDir, Desktop, Documents, ProgramFiles;
        public ITkScheduler Scheduler;
        public RegistryKey MpoHive;             // HKLM; тесты — свой раздел HKCU
        public string MpoKey = @"SOFTWARE\Microsoft\Windows\Dwm";
        public bool Live = true;                // ACL, ярлыки, звуковые устройства — только на настоящей машине

        public static TkEnv Real()
        {
            TkEnv e = new TkEnv();
            e.UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            e.ProgramData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            e.LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            e.SystemDir = Environment.SystemDirectory;
            e.Desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            e.Documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            e.ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            e.Scheduler = new TkComScheduler();
            e.MpoHive = Registry.LocalMachine;
            return e;
        }

        public string Expand(string path)
        {
            if (path == null) return null;
            return path.Replace("%LOCALAPPDATA%", LocalAppData).Replace("%ProgramData%", ProgramData).Replace("%USERPROFILE%", UserProfile);
        }
    }

    internal enum TkState { NotInstalled, Ok, Disabled, Differs, Partial }

    internal sealed class TkStatus
    {
        public TkItem Item;
        public string Dir;
        public int FilesTotal, FilesSame, FilesDiffer, FilesMissing;
        public List<TkTaskState> Tasks = new List<TkTaskState>();
        public List<string> WantedTasks = new List<string>();
        public bool Applied = true;             // для пунктов без задач: значение в реестре, ключи в файле, хук
        public bool AnyPresent;
        public bool Relevant;
        public string Note;                     // что ещё важно знать (старая установка, нет модуля…)
        public Dictionary<string, string> Values = new Dictionary<string, string>();
        public TkState State;

        public bool Denied
        {
            get { foreach (TkTaskState t in Tasks) if (t.Denied) return true; return false; }
        }

        public TkTaskState Task(string name)
        {
            foreach (TkTaskState t in Tasks) if (t.Name == name) return t;
            return null;
        }
    }

    internal static class TkPayload
    {
        public const string Prefix = "toolkit/";

        // Относительный путь внутри папки пункта → имя ресурса.
        public static List<KeyValuePair<string, string>> Files(TkItem item)
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            string p = Prefix + item.Folder + "/";
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    list.Add(new KeyValuePair<string, string>(n.Substring(p.Length).Replace('/', '\\'), n));
            list.Sort(delegate(KeyValuePair<string, string> a, KeyValuePair<string, string> b) { return string.CompareOrdinal(a.Key, b.Key); });
            return list;
        }

        // То, что ложится на диск. Папка хуков Claude общая — туда идёт только сам хук, README остаётся в exe.
        public static List<KeyValuePair<string, string>> Deployed(TkItem item)
        {
            List<KeyValuePair<string, string>> list = Files(item);
            if (item.Place == TkPlace.ClaudeHooks)
                list.RemoveAll(delegate(KeyValuePair<string, string> f) { return !f.Key.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        public static byte[] Bytes(string resource)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            {
                if (s == null) return null;
                MemoryStream ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
        }

        public static string Text(TkItem item, string rel)
        {
            byte[] b = Bytes(Prefix + item.Folder + "/" + rel.Replace('\\', '/'));
            return b == null ? null : new UTF8Encoding(false).GetString(b).TrimStart('\uFEFF');
        }
    }

    internal static partial class TkEngine
    {
        public static string DeployDir(TkItem item, TkEnv env)
        {
            switch (item.Place)
            {
                case TkPlace.UserTools: return Path.Combine(env.UserProfile, @".claude\tools\" + item.Folder);
                case TkPlace.UserHome: return Path.Combine(env.UserProfile, @"Tools\" + item.Folder);
                case TkPlace.Protected: return Path.Combine(env.ProgramData, @"SysDeck\toolkit\" + item.Folder);
                case TkPlace.ClaudeHooks: return Path.Combine(env.UserProfile, @".claude\hooks");
                default: return null;
            }
        }

        // Файлы, содержимое которых собирается из параметров: сравниваются по наличию, а не побайтно.
        internal static bool Generated(TkItem item, string rel)
        {
            if (Array.IndexOf(item.KeepIfPresent, rel) >= 0) return true;
            if (item.Id == "audio-fix") return rel == "audio-fix.config.json";
            if (item.Id == "tv-switch") return rel.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            return false;
        }

        internal static string Wscript(TkEnv env) { return Path.Combine(env.SystemDir, "wscript.exe"); }

        private static string Q(string s) { return "\"" + s + "\""; }

        // Какие задачи должны существовать при этих параметрах. Остальные из item.Tasks при сохранении удаляются.
        public static List<TkTaskSpec> Specs(TkItem item, IDictionary<string, string> raw, TkEnv env)
        {
            Dictionary<string, string> v = item.Clean(raw);
            List<TkTaskSpec> list = new List<TkTaskSpec>();
            string dir = DeployDir(item, env);
            string vbs = dir == null ? null : Path.Combine(dir, "run-hidden.vbs");
            switch (item.Id)
            {
                case "proc-reaper":
                case "vscode-watcher-reaper":
                    {
                        TkTaskSpec t = Hidden(item.Tasks[0], env, vbs, Path.Combine(dir, item.Id == "proc-reaper" ? "reap.ps1" : "watcher-reap.ps1"), "-Force -Quiet");
                        t.Description = item.En;
                        t.RepeatMinutes = int.Parse(v["minutes"], CultureInfo.InvariantCulture);
                        t.StartTime = v["start"];
                        list.Add(t);
                        break;
                    }
                case "agent-governor":
                    {
                        TkTaskSpec t = Hidden("AgentGovernor", env, vbs, Path.Combine(dir, "governor.ps1"), null);
                        t.Description = "Pins Claude agent process trees to BelowNormal priority + upper-half CPU affinity.";
                        t.RepeatMinutes = int.Parse(v["minutes"], CultureInfo.InvariantCulture);
                        t.StartTime = "00:00";
                        t.AtLogon = true;
                        t.TimeLimit = "PT5M";
                        list.Add(t);
                        break;
                    }
                case "freeze-canary":
                    {
                        // Приоритет обычный: «проснулся поздно» должно значить «встала система», а не «задачу обогнали».
                        TkTaskSpec t = Hidden("FreezeCanary", env, vbs, Path.Combine(dir, "canary.ps1"), null);
                        t.Description = @"Records system-wide stalls (>=1s). Log: %LOCALAPPDATA%\freeze-canary\stalls.log";
                        t.AtLogon = true;
                        t.TimeLimit = "PT0S";
                        t.RestartCount = 3;
                        t.Priority = 5;
                        list.Add(t);
                        break;
                    }
                case "port-watch":
                    {
                        TkTaskSpec t = Hidden("PortWatch4231", env, vbs, Path.Combine(dir, "on-4231.ps1"), null);
                        t.Description = "Snapshots per-process TCP port usage on Tcpip event 4231.";
                        t.EventQuery = "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name='Tcpip'] and (EventID=4231)]]</Select></Query></QueryList>";
                        t.TimeLimit = "PT5M";
                        list.Add(t);
                        break;
                    }
                case "audio-fix":
                    {
                        string script = Path.Combine(dir, "audio-fix.ps1");
                        TkTaskSpec boot = Hidden("AudioFix-Boot", env, vbs, script, "-OnBoot");
                        boot.Description = "Re-applies USB power settings for the playback device after boot.";
                        boot.System = true; boot.AtBoot = true; boot.BootDelaySec = 30; boot.TimeLimit = "PT10M";
                        list.Add(boot);
                        TkTaskSpec wake = Hidden("AudioFix-Wake", env, vbs, script, "-OnWake");
                        wake.Description = "Re-initialises the playback device after resume from sleep.";
                        wake.System = true; wake.EventDelaySec = 15; wake.TimeLimit = "PT10M";
                        wake.EventQuery = "<QueryList><Query Id=\"0\" Path=\"System\"><Select Path=\"System\">*[System[Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=107 or EventID=566)]]</Select></Query></QueryList>";
                        list.Add(wake);
                        if (v["watch"] == "true")
                        {
                            TkTaskSpec watch = Hidden("AudioFix-Watch", env, vbs, Path.Combine(dir, "audio-watch.ps1"), "-Loop");
                            watch.Description = "Samples the audio device state every 30 s (read-only).";
                            watch.System = true; watch.AtBoot = true; watch.TimeLimit = "PT0S"; watch.RestartCount = 3;
                            list.Add(watch);
                        }
                        break;
                    }
                case "mpo-fix":
                    if (v["reapply"] == "true")
                    {
                        TkTaskSpec t = Hidden("MpoFix-Logon", env, Path.Combine(dir, "run-hidden.vbs"), Path.Combine(dir, "apply.ps1"), null);
                        t.Description = "Re-applies Dwm OverlayTestMode=5 (MPO off) at sign-in.";
                        t.System = true; t.AtLogon = true; t.TimeLimit = "PT5M";
                        list.Add(t);
                    }
                    break;
                case "docker-maint":
                    if (v["auto"] == "true")
                    {
                        TkTaskSpec t = Hidden("DockerMaint-AutoPrune", env, vbs, Path.Combine(dir, "docker-maint.ps1"), "-Auto");
                        t.Description = "Daily Docker prune of old stopped containers, dangling images and build cache.";
                        t.Daily = true; t.StartTime = v["time"];
                        list.Add(t);
                    }
                    break;
            }
            return list;
        }

        private static TkTaskSpec Hidden(string name, TkEnv env, string vbs, string script, string args)
        {
            TkTaskSpec t = new TkTaskSpec();
            t.Name = name;
            t.Command = Wscript(env);
            t.Arguments = Q(vbs) + " " + Q(script) + (string.IsNullOrEmpty(args) ? "" : " " + args);
            return t;
        }

        // ---------- состояние ----------

        public static TkStatus Probe(TkItem item, TkEnv env)
        {
            TkStatus s = new TkStatus();
            s.Item = item;
            s.Dir = DeployDir(item, env);

            if (s.Dir != null)
                foreach (KeyValuePair<string, string> f in TkPayload.Deployed(item))
                {
                    s.FilesTotal++;
                    string path = Path.Combine(s.Dir, f.Key);
                    if (!File.Exists(path)) { s.FilesMissing++; continue; }
                    // Настройка, оставленная после «Удалить», сама по себе установкой не считается.
                    if (Array.IndexOf(item.KeepIfPresent, f.Key) < 0) s.AnyPresent = true;
                    if (Generated(item, f.Key)) { s.FilesSame++; continue; }
                    if (SameBytes(path, TkPayload.Bytes(f.Value))) s.FilesSame++;
                    else s.FilesDiffer++;
                }

            foreach (string name in item.Tasks)
            {
                TkTaskState t = env.Scheduler.Query(name);
                s.Tasks.Add(t);
                if (t.Exists) s.AnyPresent = true;
            }

            ReadValues(item, env, s);
            foreach (TkTaskSpec spec in Specs(item, s.Values, env)) s.WantedTasks.Add(spec.Name);
            Classify(s);
            s.Relevant = Relevant(item, env, s);
            return s;
        }

        private static bool SameBytes(string path, byte[] want)
        {
            if (want == null) return false;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Length != want.Length) return false;
                byte[] have = File.ReadAllBytes(path);
                for (int i = 0; i < have.Length; i++) if (have[i] != want[i]) return false;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        internal static void Classify(TkStatus s)
        {
            bool tasksOk = true, anyDisabled = false;
            foreach (string name in s.WantedTasks)
            {
                TkTaskState t = s.Task(name);
                if (t == null || !t.Exists) tasksOk = false;
                else if (!t.Denied && !t.Enabled) anyDisabled = true;
            }
            // MPO без переприменения — это только параметр реестра: файлы в защищённой папке ему не нужны.
            bool filesOk = s.FilesMissing == 0 || (s.Item.Id == "mpo-fix" && s.WantedTasks.Count == 0);
            if (!s.AnyPresent && !(s.Item.Place == TkPlace.None && s.Applied) && !(s.Item.Id == "mpo-fix" && s.Applied)) { s.State = TkState.NotInstalled; return; }
            if (!filesOk || !tasksOk || !s.Applied) { s.State = TkState.Partial; return; }
            if (anyDisabled) { s.State = TkState.Disabled; return; }
            s.State = s.FilesDiffer > 0 ? TkState.Differs : TkState.Ok;
        }

        // Параметры, как они есть в Windows сейчас; чего не видно — умолчание.
        private static void ReadValues(TkItem item, TkEnv env, TkStatus s)
        {
            Dictionary<string, string> v = item.Defaults();
            TkTaskState first = s.Tasks.Count > 0 ? s.Tasks[0] : null;
            Dictionary<string, string> x = first == null ? new Dictionary<string, string>() : TkXml.Read(first.Xml);
            switch (item.Id)
            {
                case "proc-reaper":
                case "vscode-watcher-reaper":
                    Take(v, x, "minutes", "minutes");
                    Take(v, x, "start", "start");
                    break;
                case "agent-governor":
                    Take(v, x, "minutes", "minutes");
                    break;
                case "docker-maint":
                    if (first != null && !first.Denied) v["auto"] = first.Exists ? "true" : "false";
                    Take(v, x, "time", "start");
                    break;
                case "mpo-fix":
                    {
                        TkTaskState t = s.Task("MpoFix-Logon");
                        if (t != null) v["reapply"] = t.Exists ? "true" : "false";
                        s.Applied = MpoApplied(env);
                        break;
                    }
                case "audio-fix":
                    ReadAudio(env, s, v);
                    break;
                case "wslconfig":
                    ReadWsl(env, s, v);
                    break;
                case "claude-hook":
                    s.Applied = HookRegistered(env);
                    if (s.Applied) s.AnyPresent = true;
                    break;
                case "tv-switch":
                    ReadTv(env, s, v);
                    break;
            }
            s.Values = item.Clean(v);
        }

        private static void Take(Dictionary<string, string> v, Dictionary<string, string> x, string key, string from)
        {
            string val;
            if (x.TryGetValue(from, out val)) v[key] = val;
        }

        private static bool Relevant(TkItem item, TkEnv env, TkStatus s)
        {
            if (s.State != TkState.NotInstalled) return true;
            switch (item.Id)
            {
                case "proc-reaper":
                case "port-watch":
                    return true;
                case "vscode-watcher-reaper":
                    return File.Exists(Path.Combine(env.LocalAppData, @"Programs\Microsoft VS Code\Code.exe"))
                        || File.Exists(Path.Combine(env.ProgramFiles, @"Microsoft VS Code\Code.exe"));
                case "agent-governor":
                case "claude-hook":
                    return Directory.Exists(Path.Combine(env.UserProfile, ".claude"));
                case "docker-maint":
                    return DockerPresent(env);
                case "wslconfig":
                    return DockerPresent(env) || File.Exists(WslConfigPath(env));
                case "audio-fix":
                    return AudioDevicePresent(env, s.Values["preferredEndpoint"]);
                default:
                    return false;                   // регистратор фризов, MPO, телевизор — по решению пользователя
            }
        }

        private static bool DockerPresent(TkEnv env)
        {
            return File.Exists(Path.Combine(env.ProgramFiles, @"Docker\Docker\Docker Desktop.exe"));
        }

        private static bool AudioDevicePresent(TkEnv env, string fragment)
        {
            if (!env.Live || string.IsNullOrEmpty(fragment)) return false;
            try
            {
                foreach (Capture.AudioDeviceInfo d in Capture.AudioDevices.Outputs())
                    if (d.Name != null && d.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            catch (Exception) { }
            return false;
        }

        // ---------- звук ----------

        internal static string LegacyAudioDir(TkEnv env) { return Path.Combine(env.UserProfile, @".claude\tools\audio-fix"); }

        private static void ReadAudio(TkEnv env, TkStatus s, Dictionary<string, string> v)
        {
            string cfg = Path.Combine(s.Dir, "audio-fix.config.json");
            string legacy = LegacyAudioDir(env);
            bool legacyInstall = File.Exists(Path.Combine(legacy, "audio-fix.ps1"));
            if (!File.Exists(cfg) && legacyInstall) cfg = Path.Combine(legacy, "audio-fix.config.json");
            if (legacyInstall && s.FilesMissing == s.FilesTotal)
                s.Note = Tr.S("Скрипт стоит в старом месте (" + legacy + "), откуда его может подменить любая программа пользователя, а "
                              + "выполняет его SYSTEM. «Установить» перенесёт его в защищённую папку и перенастроит задачи и ярлык.",
                              "The script sits in the old place (" + legacy + "), where any user program can replace it, while SYSTEM runs "
                              + "it. “Install” moves it to a protected folder and re-points the tasks and the shortcut.");
            try
            {
                if (File.Exists(cfg))
                {
                    JVal j = Jsn.Parse(File.ReadAllText(cfg, Encoding.UTF8).TrimStart('\uFEFF'));
                    foreach (string k in new[] { "preferredEndpoint", "shortcutName", "wakeDelaySec", "wakeCooldownMin", "restartServices", "portReset", "allowDriverReinstall" })
                    {
                        JVal val = j.Get(k);
                        if (val == null) continue;
                        v[k] = val.Kind == JKind.Bool ? (val.B ? "true" : "false") : val.Raw;
                    }
                }
            }
            catch (Exception) { }
            TkTaskState watch = s.Task("AudioFix-Watch");
            if (watch != null && (watch.Exists || s.AnyPresent)) v["watch"] = watch.Exists ? "true" : "false";
            if (s.AnyPresent || legacyInstall)
                v["shortcut"] = File.Exists(Path.Combine(env.Desktop, TkParamSafeName(v["shortcutName"]) + ".lnk")) ? "true" : "false";
            // Задачи SYSTEM старой установки существуют — их достаточно, чтобы пункт не выглядел «не установленным».
            if (legacyInstall) s.AnyPresent = true;
        }

        internal static string TkParamSafeName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name ?? "") if (Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0) sb.Append(c);
            string t = sb.ToString().Trim().TrimEnd('.');
            return t.Length == 0 ? "Fix sound" : t;
        }

        // ---------- MPO ----------

        internal static bool MpoApplied(TkEnv env)
        {
            try
            {
                using (RegistryKey k = env.MpoHive.OpenSubKey(env.MpoKey))
                {
                    object o = k == null ? null : k.GetValue("OverlayTestMode");
                    return o is int && (int)o == 5;
                }
            }
            catch (Exception) { return false; }
        }

        // ---------- WSL ----------

        internal static string WslConfigPath(TkEnv env) { return Path.Combine(env.UserProfile, ".wslconfig"); }

        private static void ReadWsl(TkEnv env, TkStatus s, Dictionary<string, string> v)
        {
            string path = WslConfigPath(env);
            Dictionary<string, string> have = WslKeys(File.Exists(path) ? SafeRead(path) : "");
            // Установлено — если есть хоть один ключ страницы; чужие ключи (processors, networkingMode) не в счёт.
            foreach (string k in TkEngine.WslOrder) if (have.ContainsKey(k.ToLowerInvariant())) s.AnyPresent = true;
            string val;
            if (have.TryGetValue("memory", out val)) v["memory"] = WslGb(val);
            if (have.TryGetValue("swap", out val)) v["swap"] = WslGb(val);
            if (have.TryGetValue("automemoryreclaim", out val)) v["autoMemoryReclaim"] = val;
            if (have.TryGetValue("sparsevhd", out val)) v["sparseVhd"] = val.ToLowerInvariant();
            s.Applied = have.ContainsKey("memory") && have.ContainsKey("automemoryreclaim");
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch (Exception) { return ""; }
        }

        // Ключи раздела [wsl2], имена — в нижнем регистре, значения без комментариев.
        internal static Dictionary<string, string> WslKeys(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            bool inside = false;
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { inside = line.Equals("[wsl2]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inside || line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string val = line.Substring(eq + 1);
                int hash = val.IndexOf('#');
                if (hash >= 0) val = val.Substring(0, hash);
                d[line.Substring(0, eq).Trim().ToLowerInvariant()] = val.Trim();
            }
            return d;
        }

        internal static string WslGb(string val)
        {
            string s = (val ?? "").Trim().ToUpperInvariant();
            double mul = 1;
            if (s.EndsWith("GB")) s = s.Substring(0, s.Length - 2);
            else if (s.EndsWith("G")) s = s.Substring(0, s.Length - 1);
            else if (s.EndsWith("MB")) { s = s.Substring(0, s.Length - 2); mul = 1.0 / 1024; }
            else if (s.EndsWith("M")) { s = s.Substring(0, s.Length - 1); mul = 1.0 / 1024; }
            double n;
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return null;
            return ((int)Math.Round(n * mul)).ToString(CultureInfo.InvariantCulture);
        }

        // ---------- хук Claude Code ----------

        internal static string ClaudeSettingsPath(TkEnv env) { return Path.Combine(env.UserProfile, @".claude\settings.json"); }

        internal static string HookCommand(TkEnv env)
        {
            return "node \"" + Path.Combine(DeployDir(TkCatalog.Find("claude-hook"), env), "proc-reap-session.mjs").Replace('\\', '/') + "\"";
        }

        internal static bool HookRegistered(TkEnv env)
        {
            string path = ClaudeSettingsPath(env);
            if (!File.Exists(path)) return false;
            try
            {
                JVal root = Jsn.Parse(File.ReadAllText(path, Encoding.UTF8));
                return FindHook(root) != null;
            }
            catch (Exception) { return false; }
        }

        // Группа и запись SessionEnd, чья команда запускает proc-reap-session.mjs (путь мог быть записан по-разному).
        internal static JVal FindHook(JVal root)
        {
            JVal hooks = root == null ? null : root.Get("hooks");
            JVal groups = hooks == null ? null : hooks.Get("SessionEnd");
            if (groups == null || groups.Kind != JKind.Arr) return null;
            foreach (JVal g in groups.V)
            {
                JVal list = g.Kind == JKind.Obj ? g.Get("hooks") : null;
                if (list == null || list.Kind != JKind.Arr) continue;
                foreach (JVal h in list.V)
                {
                    string cmd = h.Kind == JKind.Obj ? h.GetStr("command") : null;
                    if (cmd != null && cmd.IndexOf("proc-reap-session.mjs", StringComparison.OrdinalIgnoreCase) >= 0) return h;
                }
            }
            return null;
        }

        // ---------- телевизор ----------

        private static void ReadTv(TkEnv env, TkStatus s, Dictionary<string, string> v)
        {
            string on = Path.Combine(s.Dir, "tv-on.cmd");
            if (File.Exists(on))
            {
                string text = SafeRead(on);
                string name = CmdArg(text, "-Name"), hw = CmdArg(text, "-HwId");
                if (name != null) v["name"] = name;
                if (hw != null) v["hwid"] = hw;
            }
            if (s.AnyPresent && !DisplayConfigPresent(env))
                s.Note = Tr.S("Модуль PowerShell DisplayConfig не найден — без него переключатель не работает. Кнопка «Установить модуль» "
                              + "ставит его из PowerShell Gallery для текущего пользователя.",
                              "The DisplayConfig PowerShell module is missing — the switch does not work without it. “Install module” "
                              + "installs it from the PowerShell Gallery for the current user.");
        }

        internal static string CmdArg(string text, string name)
        {
            int i = (text ?? "").IndexOf(name + " \"", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            int start = i + name.Length + 2, end = text.IndexOf('"', start);
            return end < 0 ? null : text.Substring(start, end - start);
        }

        internal static bool DisplayConfigPresent(TkEnv env)
        {
            return Directory.Exists(Path.Combine(env.Documents ?? "", @"WindowsPowerShell\Modules\DisplayConfig"))
                || Directory.Exists(Path.Combine(env.ProgramFiles ?? "", @"WindowsPowerShell\Modules\DisplayConfig"));
        }
    }
}
