// SysDeck — страница «Скрипты»: установка, сохранение параметров, включение и удаление.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Install одновременно и «Установить», и «Сохранить»: файлы из exe копируются заново (кроме настроек, которые правит
// пользователь), файлы-настройки собираются из параметров, задачи перерегистрируются, лишние по параметрам — удаляются.
// Пункты с Admin (задачи SYSTEM, HKLM) окно само не выполняет: их делает помощник с правами, задание «toolkit».
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace SysDeck.Toolkit
{
    internal static partial class TkEngine
    {
        public static readonly string[] WslOrder = { "memory", "swap", "autoMemoryReclaim", "sparseVhd" };

        // null — успех, иначе причины через «; ». log — что сделано, для окна.
        public static string Install(TkItem item, IDictionary<string, string> raw, TkEnv env, List<string> log)
        {
            Dictionary<string, string> v = item.Clean(raw);
            List<string> errors = new List<string>();
            string dir = DeployDir(item, env);
            try
            {
                // Хук без Claude Code: отказ до копирования, иначе копирование само создало бы %USERPROFILE%\.claude.
                if (item.Place == TkPlace.ClaudeHooks && !Directory.Exists(Path.GetDirectoryName(ClaudeSettingsPath(env))))
                    throw new IOException(Tr.S("Claude Code не установлен: нет папки ", "Claude Code is not installed: no folder ") + Path.GetDirectoryName(ClaudeSettingsPath(env)));
                // Без образца выключатель совпал бы с любым дисплеем: «Выключить ТВ» погасил бы первый попавшийся монитор.
                if (item.Id == "tv-switch" && CmdSafe(v["name"]).Length == 0 && CmdSafe(v["hwid"]).Length == 0)
                    throw new IOException(Tr.S("Укажите имя телевизора или код оборудования", "Enter the TV name or hardware id"));
                if (dir != null) DeployFiles(item, dir, env, v, log);
                switch (item.Id)
                {
                    case "audio-fix": AudioExtras(env, dir, v, log, errors); break;
                    case "mpo-fix": SetMpo(env, true); log.Add("OverlayTestMode = 5"); break;
                    case "wslconfig": WriteWsl(env, v, log); break;
                    case "claude-hook": RegisterHook(env, true, log); break;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
                return string.Join("; ", errors.ToArray());
            }

            List<string> wanted = new List<string>();
            foreach (TkTaskSpec spec in Specs(item, v, env))
            {
                wanted.Add(spec.Name);
                // Выключенная пользователем задача остаётся выключенной: «Сохранить» меняет параметры, а не решение.
                TkTaskState prev = env.Scheduler.Query(spec.Name);
                if (prev.Exists && !prev.Denied) spec.Enabled = prev.Enabled;
                string err = env.Scheduler.Register(spec);
                if (err != null) errors.Add(err);
                else log.Add(Tr.S("задача ", "task ") + spec.Name + (spec.Enabled ? "" : Tr.S(" (выключена)", " (disabled)")));
            }
            foreach (string name in item.Tasks)
                if (!wanted.Contains(name))
                {
                    TkTaskState t = env.Scheduler.Query(name);
                    if (!t.Exists) continue;
                    string err = env.Scheduler.Delete(name);
                    if (err != null) errors.Add(err);
                    else log.Add(Tr.S("задача удалена: ", "task removed: ") + name);
                }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        public static string Remove(TkItem item, TkEnv env, List<string> log)
        {
            List<string> errors = new List<string>();
            foreach (string name in item.Tasks)
            {
                TkTaskState t = env.Scheduler.Query(name);
                if (!t.Exists) continue;
                string err = env.Scheduler.Delete(name);
                if (err != null) errors.Add(err);
                else log.Add(Tr.S("задача удалена: ", "task removed: ") + name);
            }
            try
            {
                switch (item.Id)
                {
                    case "audio-fix": if (env.Live) RemoveAudioShortcuts(env, null, log); break;
                    case "mpo-fix": SetMpo(env, false); log.Add(Tr.S("MPO возвращён как было", "MPO restored")); break;
                    case "wslconfig": WriteWsl(env, null, log); break;
                    case "claude-hook": RegisterHook(env, false, log); break;
                }
                string dir = DeployDir(item, env);
                if (dir != null && Directory.Exists(dir))
                {
                    int removed = 0;
                    foreach (KeyValuePair<string, string> f in TkPayload.Deployed(item))
                    {
                        // Настройки, которые пользователь правил сам, остаются — вдруг скрипт вернётся.
                        if (Array.IndexOf(item.KeepIfPresent, f.Key) >= 0) continue;
                        string path = Path.Combine(dir, f.Key);
                        if (File.Exists(path)) { File.Delete(path); removed++; }
                    }
                    if (item.Place != TkPlace.ClaudeHooks && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                    if (removed > 0) log.Add(Tr.S("удалено файлов: ", "files removed: ") + removed.ToString(CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) { errors.Add(ex.Message); }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        public static string SetEnabled(TkItem item, IDictionary<string, string> raw, TkEnv env, bool on, List<string> log)
        {
            List<string> errors = new List<string>();
            foreach (TkTaskSpec spec in Specs(item, item.Clean(raw), env))
            {
                TkTaskState t = env.Scheduler.Query(spec.Name);
                if (!t.Exists) continue;
                string err = env.Scheduler.SetEnabled(spec.Name, on);
                if (err != null) errors.Add(err);
                else log.Add(spec.Name + (on ? Tr.S(" включена", " enabled") : Tr.S(" выключена", " disabled")));
            }
            return errors.Count == 0 ? null : string.Join("; ", errors.ToArray());
        }

        // ---------- файлы ----------

        private static void DeployFiles(TkItem item, string dir, TkEnv env, Dictionary<string, string> v, List<string> log)
        {
            bool protect = item.Place == TkPlace.Protected;
            if (protect) PrepareProtected(env, dir);
            else Directory.CreateDirectory(dir);
            int written = 0;
            foreach (KeyValuePair<string, string> f in TkPayload.Deployed(item))
            {
                string path = Path.Combine(dir, f.Key);
                if (Array.IndexOf(item.KeepIfPresent, f.Key) >= 0 && File.Exists(path)) continue;
                byte[] data = GeneratedBytes(item, f.Key, v, path) ?? TkPayload.Bytes(f.Value);
                // В защищённой папке файл пересоздаётся: у нового — только унаследованные права папки.
                if (protect && File.Exists(path)) File.Delete(path);
                File.WriteAllBytes(path, data);
                written++;
            }
            log.Add(Tr.S("файлы: ", "files: ") + written.ToString(CultureInfo.InvariantCulture) + " → " + dir);
        }

        private static byte[] GeneratedBytes(TkItem item, string rel, Dictionary<string, string> v, string path)
        {
            if (item.Id == "audio-fix" && rel == "audio-fix.config.json")
            {
                // Существующий файл — основа: неизвестные странице ключи (logMaxKB и будущие) сохраняются.
                string text = File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : TkPayload.Text(item, rel);
                return new UTF8Encoding(false).GetBytes(AudioConfig(text, v));
            }
            if (item.Id == "tv-switch" && rel.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                return Encoding.ASCII.GetBytes(TvCmd(rel, v["name"], v["hwid"]));
            return null;
        }

        internal static string AudioConfig(string existing, IDictionary<string, string> v)
        {
            JVal root;
            try { root = Jsn.Parse((existing ?? "").TrimStart('\uFEFF')); }
            catch (Exception) { root = null; }
            if (root == null || root.Kind != JKind.Obj) root = JVal.NewObj();
            root.Set("preferredEndpoint", JVal.NewStr(v["preferredEndpoint"]));
            root.Set("restartServices", Bool(v["restartServices"] == "true"));
            root.Set("portReset", Bool(v["portReset"] == "true"));
            root.Set("allowDriverReinstall", Bool(v["allowDriverReinstall"] == "true"));
            root.Set("shortcutName", JVal.NewStr(v["shortcutName"]));
            root.Set("wakeDelaySec", JVal.NewNum(v["wakeDelaySec"]));
            root.Set("wakeCooldownMin", JVal.NewNum(v["wakeCooldownMin"]));
            return Jsn.WriteNodeStyle(root);
        }

        private static JVal Bool(bool b) { JVal j = new JVal(); j.Kind = JKind.Bool; j.B = b; return j; }

        // Имя и код в bat-файле: кавычки и символы, которые cmd понимает как команды, выбрасываются.
        internal static string TvCmd(string rel, string name, string hwid)
        {
            string sw = rel.IndexOf("off", StringComparison.OrdinalIgnoreCase) >= 0 ? "-Off"
                      : rel.IndexOf("status", StringComparison.OrdinalIgnoreCase) >= 0 ? "-Status" : "-On";
            StringBuilder sb = new StringBuilder();
            sb.Append("@echo off\r\n");
            sb.Append("powershell -NoProfile -ExecutionPolicy Bypass -File \"%~dp0tv.ps1\" ").Append(sw);
            sb.Append(" -Name \"").Append(CmdSafe(name)).Append("\"");
            if (!string.IsNullOrEmpty(CmdSafe(hwid))) sb.Append(" -HwId \"").Append(CmdSafe(hwid)).Append("\"");
            sb.Append("\r\n");
            sb.Append(sw == "-Status" ? "pause\r\n" : "timeout /t 4 >nul\r\n");
            return sb.ToString();
        }

        private static string CmdSafe(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "") if (c < 128 && (char.IsLetterOrDigit(c) || c == ' ' || c == '-' || c == '_' || c == '.')) sb.Append(c);
            return sb.ToString().Trim();
        }

        // %ProgramData%\SysDeck\toolkit: владелец — администраторы, запись — только SYSTEM и администраторы. Подставленная
        // заранее точка соединения на этом пути — отказ: писать скрипт для SYSTEM туда, куда она ведёт, нельзя.
        private static void PrepareProtected(TkEnv env, string dir)
        {
            string root = Path.Combine(env.ProgramData, @"SysDeck\toolkit");
            foreach (string p in new[] { Path.Combine(env.ProgramData, "SysDeck"), root, dir })
            {
                DirectoryInfo di = new DirectoryInfo(p);
                if (di.Exists && (di.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException(Tr.S("путь подменён точкой соединения: ", "the path is a reparse point: ") + p);
            }
            Directory.CreateDirectory(root);
            if (env.Live) LockDown(root, false);
            Directory.CreateDirectory(dir);
        }

        internal static void LockDown(string dir, bool usersModify)
        {
            InheritanceFlags inh = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            DirectorySecurity ds = new DirectorySecurity();
            ds.SetAccessRuleProtection(true, false);
            SecurityIdentifier admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            ds.SetOwner(admins);
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, inh, PropagationFlags.None, AccessControlType.Allow));
            ds.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                usersModify ? FileSystemRights.Modify : FileSystemRights.ReadAndExecute, inh, PropagationFlags.None, AccessControlType.Allow));
            Directory.SetAccessControl(dir, ds);
        }

        // ---------- звук ----------

        private static void AudioExtras(TkEnv env, string dir, Dictionary<string, string> v, List<string> log, List<string> errors)
        {
            // Журнал пишут и задачи SYSTEM, и ярлык от имени пользователя — пользователям разрешено изменять.
            string state = Path.Combine(env.ProgramData, "audio-fix");
            Directory.CreateDirectory(state);
            if (env.Live)
                try { LockDown(state, true); }
                catch (Exception ex) { errors.Add(state + ": " + ex.Message); }
            if (!env.Live) return;
            string keep = v["shortcut"] == "true" ? Path.Combine(env.Desktop, TkParamSafeName(v["shortcutName"]) + ".lnk") : null;
            try
            {
                RemoveAudioShortcuts(env, keep, log);
                if (keep != null) { AudioShortcut(env, Path.Combine(dir, "audio-fix.ps1"), dir, keep); log.Add(Tr.S("ярлык: ", "shortcut: ") + keep); }
            }
            catch (Exception ex) { errors.Add(Tr.S("ярлык: ", "shortcut: ") + ex.Message); }
            if (File.Exists(Path.Combine(LegacyAudioDir(env), "audio-fix.ps1")))
                log.Add(Tr.S("старая копия осталась в ", "the old copy stays in ") + LegacyAudioDir(env) + Tr.S(" — задачи на неё больше не указывают", " — no task points to it any more"));
        }

        private static object Com(object o, string name, BindingFlags how, params object[] args)
        {
            try { return o.GetType().InvokeMember(name, how, null, o, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static void AudioShortcut(TkEnv env, string script, string dir, string lnk)
        {
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            object sc = Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, lnk);
            Com(sc, "TargetPath", BindingFlags.SetProperty, Path.Combine(env.SystemDir, @"WindowsPowerShell\v1.0\powershell.exe"));
            Com(sc, "Arguments", BindingFlags.SetProperty, "-NoProfile -ExecutionPolicy Bypass -NoExit -File \"" + script + "\"");
            Com(sc, "IconLocation", BindingFlags.SetProperty, Path.Combine(env.SystemDir, "mmres.dll") + ",0");
            Com(sc, "WorkingDirectory", BindingFlags.SetProperty, dir);
            Com(sc, "Save", BindingFlags.InvokeMethod);
            // Байт 0x15, флаг 0x20 — «запуск от имени администратора»: скрипту не нужно перезапускать себя, окно с отчётом остаётся.
            byte[] b = File.ReadAllBytes(lnk);
            if (b.Length > 0x15) { b[0x15] = (byte)(b[0x15] | 0x20); File.WriteAllBytes(lnk, b); }
        }

        // Ярлыки на рабочем столе, запускающие audio-fix.ps1 (любой копии), кроме keep. Узнаются по цели, а не по имени.
        private static void RemoveAudioShortcuts(TkEnv env, string keep, List<string> log)
        {
            if (!Directory.Exists(env.Desktop)) return;
            object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
            foreach (string lnk in Directory.GetFiles(env.Desktop, "*.lnk"))
            {
                if (keep != null && string.Equals(lnk, keep, StringComparison.OrdinalIgnoreCase)) continue;
                string args;
                try { args = Com(Com(shell, "CreateShortcut", BindingFlags.InvokeMethod, lnk), "Arguments", BindingFlags.GetProperty) as string; }
                catch (Exception) { continue; }
                if (args != null && args.IndexOf("audio-fix.ps1", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    File.Delete(lnk);
                    log.Add(Tr.S("старый ярлык удалён: ", "old shortcut removed: ") + Path.GetFileName(lnk));
                }
            }
        }

        // ---------- MPO ----------

        internal static void SetMpo(TkEnv env, bool on)
        {
            using (RegistryKey k = env.MpoHive.CreateSubKey(env.MpoKey))
            {
                if (on) k.SetValue("OverlayTestMode", 5, RegistryValueKind.DWord);
                else k.DeleteValue("OverlayTestMode", false);
            }
        }

        // ---------- WSL ----------

        // v == null — убрать ключи, которыми управляет страница. Копия исходного файла — один раз, .wslconfig.sysdeck.bak.
        private static void WriteWsl(TkEnv env, Dictionary<string, string> v, List<string> log)
        {
            string path = WslConfigPath(env);
            string text = File.Exists(path) ? File.ReadAllText(path) : "";
            string bak = path + ".sysdeck.bak";
            if (text.Length > 0 && !File.Exists(bak)) File.Copy(path, bak);
            string next = WslEdit(text, v);
            if (next == text) return;
            File.WriteAllText(path, next, new UTF8Encoding(false));
            log.Add(path);
        }

        internal static string WslValue(string key, IDictionary<string, string> v)
        {
            string val = v[key];
            return key == "memory" || key == "swap" ? val + "GB" : val;
        }

        internal static string WslEdit(string text, IDictionary<string, string> v)
        {
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            List<string> lines = new List<string>(text.Length == 0 ? new string[0] : text.Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            int header = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (!t.StartsWith("[")) continue;
                if (header >= 0) { end = i; break; }
                if (t.Equals("[wsl2]", StringComparison.OrdinalIgnoreCase)) header = i;
            }
            if (header < 0)
            {
                if (v == null) return text;
                if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add("");
                header = lines.Count;
                lines.Add("[wsl2]");
                end = lines.Count;
            }
            List<string> missing = new List<string>(WslOrder);
            int lastKey = header;
            for (int i = end - 1; i > header; i--)
            {
                string t = lines[i].Trim();
                int eq = t.IndexOf('=');
                if (t.Length == 0 || t[0] == '#' || t[0] == ';' || eq <= 0) continue;
                string key = t.Substring(0, eq).Trim();
                string ours = null;
                foreach (string k in WslOrder) if (k.Equals(key, StringComparison.OrdinalIgnoreCase)) ours = k;
                if (lastKey == header) lastKey = i;
                if (ours == null) continue;
                if (v == null) { lines.RemoveAt(i); if (lastKey >= i) lastKey--; continue; }
                lines[i] = ours + "=" + WslValue(ours, v);
                missing.Remove(ours);
            }
            if (v != null && missing.Count > 0)
            {
                int at = lastKey + 1;
                foreach (string k in missing) lines.Insert(at++, k + "=" + WslValue(k, v));
            }
            return string.Join(nl, lines.ToArray()) + nl;
        }

        // ---------- хук Claude Code ----------

        private static void RegisterHook(TkEnv env, bool add, List<string> log)
        {
            string path = ClaudeSettingsPath(env);
            string text;
            if (File.Exists(path)) text = File.ReadAllText(path, Encoding.UTF8);
            else if (!add) return;
            else if (Directory.Exists(Path.GetDirectoryName(path))) text = "{}";
            else throw new IOException(Tr.S("Claude Code не установлен: нет папки ", "Claude Code is not installed: no folder ") + Path.GetDirectoryName(path));
            bool changed;
            string next = HookEdit(text, HookCommand(env), add, out changed);
            if (!changed) return;
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.WriteAllText(path, next, new UTF8Encoding(false));
            log.Add(path + (add ? Tr.S(": хук SessionEnd добавлен", ": SessionEnd hook added") : Tr.S(": хук SessionEnd убран", ": SessionEnd hook removed")));
        }

        internal static string HookEdit(string json, string command, bool add, out bool changed)
        {
            changed = false;
            JVal root = Jsn.Parse(json.TrimStart('\uFEFF'));
            if (root == null || root.Kind != JKind.Obj) throw new FormatException("settings.json");
            JVal existing = FindHook(root);
            if (add && existing != null) return json;
            if (!add && existing == null) return json;
            JVal hooks = root.Get("hooks");
            if (hooks == null) { hooks = JVal.NewObj(); root.Set("hooks", hooks); }
            JVal groups = hooks.Get("SessionEnd");
            if (groups == null) { groups = JVal.NewArr(); hooks.Set("SessionEnd", groups); }
            if (add)
            {
                JVal entry = JVal.NewObj();
                entry.Set("type", JVal.NewStr("command"));
                entry.Set("command", JVal.NewStr(command));
                JVal slot = null;
                foreach (JVal g in groups.V) if (g.Kind == JKind.Obj && g.Get("matcher") == null && g.Get("hooks") != null) { slot = g; break; }
                if (slot != null) slot.Get("hooks").V.Add(entry);
                else
                {
                    JVal g = JVal.NewObj();
                    JVal list = JVal.NewArr();
                    list.V.Add(entry);
                    g.Set("hooks", list);
                    groups.V.Add(g);
                }
            }
            else
            {
                for (int gi = groups.V.Count - 1; gi >= 0; gi--)
                {
                    JVal list = groups.V[gi].Kind == JKind.Obj ? groups.V[gi].Get("hooks") : null;
                    if (list == null) continue;
                    list.V.Remove(existing);
                    if (list.V.Count == 0) groups.V.RemoveAt(gi);
                }
                if (groups.V.Count == 0) hooks.Remove("SessionEnd");
            }
            changed = true;
            return Jsn.WriteNodeStyle(root);
        }

        // ---------- задание помощнику с правами ----------

        // Параметры в файле задания — «ключ=значение» построчно, уже нормализованные; помощник нормализует их ещё раз.
        public static string Pack(TkItem item, IDictionary<string, string> raw)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in item.Clean(raw)) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            return sb.ToString();
        }

        public static Dictionary<string, string> Unpack(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (string line in (text ?? "").Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return d;
        }
    }
}
