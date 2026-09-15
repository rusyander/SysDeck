// SysDeck — тесты страницы «Скрипты» (Toolkit.*.cs).
//
// Установка и удаление идут по настоящему коду в окружении-фикстуре: корни папок — во временном дереве тестов, Планировщик —
// поддельный (хранит описания задач и отдаёт их XML), раздел MPO — под HKCU\Software\WPC-Tests\<pid>. Ярлыки, права на
// папки и звуковые устройства выключены (Live = false). Отдельно — живой Планировщик: одна выключенная задача
// SysDeck-Test-<pid> регистрируется, читается и удаляется; задачи пользователя только читаются.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using SysDeck.Toolkit;

namespace SysDeck.Tests
{
    internal static class ToolkitTests
    {
        // Поддельный Планировщик: задача — её описание; XML строит тот же TkXml.Build, что уходит в Windows.
        private sealed class FakeScheduler : ITkScheduler
        {
            public readonly Dictionary<string, TkTaskSpec> Tasks = new Dictionary<string, TkTaskSpec>();
            public readonly List<string> Denied = new List<string>();
            public int Registered;

            public TkTaskState Query(string name)
            {
                TkTaskState s = new TkTaskState();
                s.Name = name;
                if (Denied.Contains(name)) { s.Exists = true; s.Denied = true; return s; }
                TkTaskSpec t;
                if (!Tasks.TryGetValue(name, out t)) return s;
                s.Exists = true;
                s.Enabled = t.Enabled;
                s.State = t.Enabled ? 3 : 1;
                s.Xml = TkXml.Build(t, DateTime.Today);
                return s;
            }

            public string Register(TkTaskSpec spec)
            {
                if (Denied.Contains(spec.Name)) return spec.Name + ": denied";
                Tasks[spec.Name] = spec;
                Registered++;
                return null;
            }

            public string Delete(string name) { Tasks.Remove(name); return null; }

            public string SetEnabled(string name, bool on)
            {
                TkTaskSpec t;
                if (!Tasks.TryGetValue(name, out t)) return name + ": not found";
                t.Enabled = on;
                return null;
            }

            public string Run(string name) { return Tasks.ContainsKey(name) ? null : name + ": not found"; }
        }

        internal static void Run()
        {
            CatalogCases();
            NormalizeCases();
            SpecCases();
            WslCases();
            HookCases();
            GeneratedCases();
            string sub = @"Software\WPC-Tests\" + Process.GetCurrentProcess().Id + "-toolkit";
            try
            {
                using (RegistryKey hive = Registry.CurrentUser.CreateSubKey(sub))
                {
                    FixtureCases(hive);
                }
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software\WPC-Tests", true))
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0) { parent.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\WPC-Tests", false); }
            }
            LiveSchedulerCases();
            T.Eq("toolkit: the elevated job has a human title", false, Elevation.JobTitle("toolkit") == "toolkit");
        }

        private static TkEnv Fixture(string name, RegistryKey hive, out FakeScheduler sch)
        {
            string root = Fx.MakeDir(Fx.Root, "toolkit-" + name);
            TkEnv env = new TkEnv();
            env.UserProfile = Fx.MakeDir(root, "profile");
            env.ProgramData = Fx.MakeDir(root, "programdata");
            env.LocalAppData = Fx.MakeDir(root, "local");
            env.SystemDir = @"C:\Windows\System32";
            env.Desktop = Fx.MakeDir(root, "desktop");
            env.Documents = Fx.MakeDir(root, "documents");
            env.ProgramFiles = Fx.MakeDir(root, "programfiles");
            sch = new FakeScheduler();
            env.Scheduler = sch;
            env.MpoHive = hive;
            env.MpoKey = "Dwm";
            env.Live = false;
            return env;
        }

        private static Dictionary<string, string> With(TkItem item, params string[] kv)
        {
            Dictionary<string, string> d = item.Defaults();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[kv[i]] = kv[i + 1];
            return d;
        }

        // ---------- каталог ----------

        private static void CatalogCases()
        {
            List<string> ids = new List<string>();
            bool unique = true, payload = true, defaultsStable = true, tasksNamed = true;
            string info = null;
            foreach (TkItem it in TkCatalog.All)
            {
                if (ids.Contains(it.Id)) unique = false;
                ids.Add(it.Id);
                if (TkPayload.Files(it).Count == 0) { payload = false; info = it.Id; }
                foreach (TkParam p in it.Params)
                    if (p.Normalize(p.Default) != p.Default) { defaultsStable = false; info = it.Id + "." + p.Key; }
                if (it.Tasks.Length > 0 && TkEngine.DeployDir(it, TkEnv.Real()) == null) tasksNamed = false;
            }
            T.Check("toolkit: item ids are unique", unique);
            T.Check("toolkit: every item has its scripts embedded in the exe", payload, info);
            T.Check("toolkit: every default survives its own normalisation", defaultsStable, info);
            T.Check("toolkit: an item with tasks has a folder for their scripts", tasksNamed);
            T.Check("toolkit: personal VS Code settings are not in the catalog", TkCatalog.Find("vscode-settings") == null);
            T.Check("toolkit: only SYSTEM items need administrator rights",
                    TkCatalog.Find("audio-fix").Admin && TkCatalog.Find("mpo-fix").Admin && !TkCatalog.Find("proc-reaper").Admin && !TkCatalog.Find("wslconfig").Admin);
            T.Check("toolkit: SYSTEM scripts go to the protected folder",
                    TkCatalog.Find("audio-fix").Place == TkPlace.Protected && TkCatalog.Find("mpo-fix").Place == TkPlace.Protected);
            int wsl = TkCatalog.DefaultWslMemoryGb();
            T.Check("toolkit: the default WSL limit is 1..24 GB", wsl >= 1 && wsl <= 24, wsl.ToString());
        }

        private static void NormalizeCases()
        {
            TkItem reaper = TkCatalog.Find("proc-reaper");
            TkParam minutes = reaper.Param("minutes"), start = reaper.Param("start");
            T.Eq("toolkit: minutes below the minimum are raised to it", "15", minutes.Normalize("5"));
            T.Eq("toolkit: minutes above the maximum are cut to it", "1440", minutes.Normalize("99999"));
            T.Eq("toolkit: garbage minutes become the default", "240", minutes.Normalize("четыре"));
            T.Eq("toolkit: time is padded to HH:mm", "04:05", start.Normalize(" 4:05 "));
            T.Eq("toolkit: 24:00 is not a time", "03:50", start.Normalize("24:00"));
            T.Eq("toolkit: minutes past 59 are not a time", "03:50", start.Normalize("03:60"));
            TkItem audio = TkCatalog.Find("audio-fix");
            T.Eq("toolkit: booleans are case-insensitive", "false", audio.Param("portReset").Normalize("FALSE"));
            T.Eq("toolkit: a non-boolean becomes the default", "true", audio.Param("portReset").Normalize("yes"));
            T.Eq("toolkit: control characters are stripped from text", "Realtek  Out", audio.Param("preferredEndpoint").Normalize("Realtek \r\n Out"));
            T.Eq("toolkit: optional text may be empty", "", audio.Param("preferredEndpoint").Normalize("   "));
            T.Eq("toolkit: required text never ends up empty", "Починить звук", audio.Param("shortcutName").Normalize(""));
            T.Eq("toolkit: text is cut to 80 characters", 80, audio.Param("shortcutName").Normalize(new string('x', 200)).Length);
            TkParam reclaim = TkCatalog.Find("wslconfig").Param("autoMemoryReclaim");
            T.Eq("toolkit: a choice matches case-insensitively and keeps its canonical form", "gradual", reclaim.Normalize("GRADUAL"));
            T.Eq("toolkit: an unknown choice becomes the default", "dropcache", reclaim.Normalize("sometimes"));

            Dictionary<string, string> raw = new Dictionary<string, string>();
            raw["minutes"] = "60"; raw["evil"] = "x";
            Dictionary<string, string> clean = reaper.Clean(raw);
            T.Check("toolkit: Clean keeps only known keys and fills the rest with defaults",
                    clean.Count == 2 && clean["minutes"] == "60" && clean["start"] == "03:50");

            // Файл задания помощнику: значения с «=» доходят целыми, перевод строки не может добавить ключ.
            Dictionary<string, string> v = With(audio, "preferredEndpoint", "a=b", "shortcutName", "Звук\nportReset=false");
            Dictionary<string, string> back = audio.Clean(TkEngine.Unpack(TkEngine.Pack(audio, v)));
            T.Check("toolkit: Pack/Unpack keeps a value with '='", back["preferredEndpoint"] == "a=b");
            T.Check("toolkit: a newline inside a value cannot inject another key", back["portReset"] == "true" && back["shortcutName"] == "ЗвукportReset=false",
                    back["portReset"] + " / " + back["shortcutName"]);
        }

        // ---------- задачи: параметры → XML → параметры ----------

        private static void SpecCases()
        {
            FakeScheduler sch;
            TkEnv env = Fixture("specs", null, out sch);
            TkItem reaper = TkCatalog.Find("proc-reaper");
            List<TkTaskSpec> specs = TkEngine.Specs(reaper, With(reaper, "minutes", "180", "start", "2:30"), env);
            T.Eq("toolkit: the reaper has one task", 1, specs.Count);
            string xml = TkXml.Build(specs[0], new DateTime(2026, 9, 15));
            Dictionary<string, string> x = TkXml.Read(xml);
            T.Eq("toolkit: the interval read back from XML", "180", Get(x, "minutes"));
            T.Eq("toolkit: the start time read back from XML", "02:30", Get(x, "start"));
            T.Check("toolkit: the start boundary is today at that time", xml.Contains("<StartBoundary>2026-09-15T02:30:00</StartBoundary>"));
            T.Check("toolkit: the task runs hidden through wscript", Get(x, "command") == @"C:\Windows\System32\wscript.exe"
                    && Get(x, "arguments") == "\"" + Path.Combine(env.UserProfile, @".claude\tools\proc-reaper\run-hidden.vbs") + "\" \""
                       + Path.Combine(env.UserProfile, @".claude\tools\proc-reaper\reap.ps1") + "\" -Force -Quiet", Get(x, "arguments"));
            T.Check("toolkit: a user task never asks for elevation", xml.Contains("<RunLevel>LeastPrivilege</RunLevel>") && !xml.Contains("S-1-5-18"));
            T.Check("toolkit: the XML is valid for the Task Scheduler schema root", xml.Contains("xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\""));

            TkItem docker = TkCatalog.Find("docker-maint");
            List<TkTaskSpec> d = TkEngine.Specs(docker, With(docker, "time", "05:15"), env);
            Dictionary<string, string> dx = TkXml.Read(TkXml.Build(d[0], DateTime.Today));
            T.Check("toolkit: docker cleanup is daily at the chosen time", Get(dx, "daily") == "true" && Get(dx, "start") == "05:15" && Get(dx, "minutes") == null);
            T.Eq("toolkit: docker cleanup off means no task", 0, TkEngine.Specs(docker, With(docker, "auto", "false"), env).Count);

            TkItem audio = TkCatalog.Find("audio-fix");
            List<TkTaskSpec> a = TkEngine.Specs(audio, With(audio, "watch", "false"), env);
            T.Eq("toolkit: audio without the state recorder has two tasks", 2, a.Count);
            T.Eq("toolkit: audio with the state recorder has three tasks", 3, TkEngine.Specs(audio, audio.Defaults(), env).Count);
            string ax = TkXml.Build(a[1], DateTime.Today);
            T.Check("toolkit: audio tasks run as SYSTEM from the protected folder",
                    ax.Contains("<UserId>S-1-5-18</UserId>") && a[1].Arguments.Contains(Path.Combine(env.ProgramData, @"SysDeck\toolkit\audio-fix\audio-fix.ps1")));
            T.Check("toolkit: the wake task fires on Kernel-Power resume events", TkXml.Read(ax).ContainsKey("event") && ax.Contains("EventID=107"));

            TkItem canary = TkCatalog.Find("freeze-canary");
            TkTaskSpec c = TkEngine.Specs(canary, null, env)[0];
            string cx = TkXml.Build(c, DateTime.Today);
            T.Check("toolkit: the freeze recorder starts at sign-in, never times out and restarts after a crash",
                    TkXml.Read(cx).ContainsKey("logon") && cx.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>") && cx.Contains("<Count>3</Count>"));

            TkItem mpo = TkCatalog.Find("mpo-fix");
            T.Eq("toolkit: MPO without re-apply has no task", 0, TkEngine.Specs(mpo, null, env).Count);
            T.Eq("toolkit: MPO re-apply adds the logon task", "MpoFix-Logon", TkEngine.Specs(mpo, With(mpo, "reapply", "true"), env)[0].Name);

            // Описание с символами XML не ломает документ.
            TkTaskSpec odd = TkEngine.Specs(reaper, null, env)[0];
            odd.Description = "a < b & \"c\"";
            T.Check("toolkit: XML special characters are escaped", TkXml.Read(TkXml.Build(odd, DateTime.Today)).ContainsKey("minutes"));
            T.Eq("toolkit: broken XML reads as nothing", 0, TkXml.Read("<Task").Count);
        }

        private static string Get(Dictionary<string, string> d, string k)
        {
            string v;
            return d.TryGetValue(k, out v) ? v : null;
        }

        // ---------- .wslconfig ----------

        private static void WslCases()
        {
            TkItem wsl = TkCatalog.Find("wslconfig");
            Dictionary<string, string> v = With(wsl, "memory", "16", "swap", "4", "autoMemoryReclaim", "gradual", "sparseVhd", "true");
            T.Eq("toolkit: an empty .wslconfig gets a [wsl2] section with the four keys",
                 "[wsl2]\nmemory=16GB\nswap=4GB\nautoMemoryReclaim=gradual\nsparseVhd=true\n", TkEngine.WslEdit("", v));

            string mine = "# my notes\r\n[wsl2]\r\nprocessors=8\r\nMemory = 8GB # old\r\nnetworkingMode=mirrored\r\n\r\n[experimental]\r\nhostAddressLoopback=true\r\n";
            string edited = TkEngine.WslEdit(mine, v);
            T.Eq("toolkit: .wslconfig — only our keys change, comments, other keys and sections stay, CRLF kept",
                 "# my notes\r\n[wsl2]\r\nprocessors=8\r\nmemory=16GB\r\nnetworkingMode=mirrored\r\nswap=4GB\r\nautoMemoryReclaim=gradual\r\nsparseVhd=true\r\n\r\n[experimental]\r\nhostAddressLoopback=true\r\n",
                 edited);
            T.Eq("toolkit: editing .wslconfig twice changes nothing more", edited, TkEngine.WslEdit(edited, v));
            Dictionary<string, string> keys = TkEngine.WslKeys(edited);
            T.Check("toolkit: the keys read back", TkEngine.WslGb(keys["memory"]) == "16" && keys["automemoryreclaim"] == "gradual" && !keys.ContainsKey("hostaddressloopback"));
            T.Eq("toolkit: removal takes out only our keys",
                 "# my notes\r\n[wsl2]\r\nprocessors=8\r\nnetworkingMode=mirrored\r\n\r\n[experimental]\r\nhostAddressLoopback=true\r\n",
                 TkEngine.WslEdit(edited, null));
            T.Eq("toolkit: removal from a file without [wsl2] changes nothing", "[boot]\nsystemd=true\n", TkEngine.WslEdit("[boot]\nsystemd=true\n", null));
            T.Eq("toolkit: 2048MB reads as 2 GB", "2", TkEngine.WslGb("2048MB"));
            T.Eq("toolkit: an unreadable size reads as nothing", null, TkEngine.WslGb("lots"));
        }

        // ---------- settings.json Claude Code ----------

        private static void HookCases()
        {
            const string cmd = "node \"C:/Users/u/.claude/hooks/proc-reap-session.mjs\"";
            bool changed;
            string added = TkEngine.HookEdit("{}", cmd, true, out changed);
            T.Check("toolkit: the hook is added to empty settings", changed && TkEngine.FindHook(Jsn.Parse(added)) != null);
            string again = TkEngine.HookEdit(added, cmd, true, out changed);
            T.Check("toolkit: adding the hook twice changes nothing", !changed && again == added);

            string theirs = "{\n  \"model\": \"opus\",\n  \"statusLine\": \"привет\",\n  \"hooks\": {\n    \"SessionEnd\": [\n      {\n        \"hooks\": [\n"
                          + "          { \"type\": \"command\", \"command\": \"node other.mjs\" }\n        ]\n      }\n    ],\n"
                          + "    \"Stop\": [ { \"hooks\": [ { \"type\": \"command\", \"command\": \"x\" } ] } ]\n  }\n}\n";
            string withOurs = TkEngine.HookEdit(theirs, cmd, true, out changed);
            JVal root = Jsn.Parse(withOurs);
            JVal groups = root.Get("hooks").Get("SessionEnd");
            T.Check("toolkit: our hook joins the existing SessionEnd group instead of a second one",
                    groups.V.Count == 1 && groups.V[0].Get("hooks").V.Count == 2 && groups.V[0].Get("hooks").V[0].GetStr("command") == "node other.mjs");
            T.Check("toolkit: other settings and hooks stay, non-ASCII is kept as is",
                    root.GetStr("model") == "opus" && withOurs.Contains("\"statusLine\": \"привет\"") && root.Get("hooks").Get("Stop") != null);
            T.Check("toolkit: settings are written with two-space indentation", withOurs.StartsWith("{\n  \"model\""), withOurs.Substring(0, Math.Min(20, withOurs.Length)));

            string back = TkEngine.HookEdit(withOurs, cmd, false, out changed);
            JVal br = Jsn.Parse(back);
            T.Check("toolkit: removal takes out only our hook", changed && TkEngine.FindHook(br) == null
                    && br.Get("hooks").Get("SessionEnd").V[0].Get("hooks").V.Count == 1);
            string gone = TkEngine.HookEdit(added, cmd, false, out changed);
            T.Check("toolkit: removing the only SessionEnd hook removes the empty event", changed && Jsn.Parse(gone).Get("hooks").Get("SessionEnd") == null);
            // Хук, записанный другим путём (обратные слэши, другая папка), узнаётся по имени файла.
            string legacy = "{\"hooks\":{\"SessionEnd\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"node C:\\\\x\\\\proc-reap-session.mjs\"}]}]}}";
            TkEngine.HookEdit(legacy, cmd, true, out changed);
            T.Check("toolkit: a hook registered under another path is recognised and not duplicated", !changed);
            bool threw = false;
            try { TkEngine.HookEdit("[1,2]", cmd, true, out changed); }
            catch (FormatException) { threw = true; }
            T.Check("toolkit: settings that are not a JSON object are refused, not overwritten", threw);
        }

        // ---------- файлы, собранные из параметров ----------

        private static void GeneratedCases()
        {
            TkItem audio = TkCatalog.Find("audio-fix");
            string existing = "{\n  \"preferredEndpoint\": \"Old\",\n  \"logMaxKB\": 512\n}\n";
            string cfg = TkEngine.AudioConfig(existing, With(audio, "preferredEndpoint", "Speakers \"USB\"", "portReset", "false", "wakeDelaySec", "45"));
            JVal j = Jsn.Parse(cfg);
            T.Check("toolkit: audio config — values written, unknown keys kept",
                    j.GetStr("preferredEndpoint") == "Speakers \"USB\"" && j.GetStr("logMaxKB") == "512" && j.GetStr("wakeDelaySec") == "45");
            T.Check("toolkit: audio config — booleans are JSON booleans", j.Get("portReset").Kind == JKind.Bool && !j.Get("portReset").B && j.Get("restartServices").B);
            T.Check("toolkit: audio config from garbage starts clean", Jsn.Parse(TkEngine.AudioConfig("not json", audio.Defaults())).GetStr("shortcutName") == "Починить звук");

            string on = TkEngine.TvCmd("tv-on.cmd", "LG \"&del C:\\*", "GSM1234|x");
            T.Check("toolkit: TV command — cmd metacharacters never reach the bat file", !on.Contains("&") && !on.Contains("|") && !on.Contains("\\*"), on);
            T.Eq("toolkit: TV command — the name reads back", "LG del C", TkEngine.CmdArg(on, "-Name"));
            T.Check("toolkit: TV command — the switch follows the file name",
                    TkEngine.TvCmd("tv-off.cmd", "TV", "").Contains(" -Off ") && TkEngine.TvCmd("tv-status.cmd", "TV", "").Contains("pause") && on.Contains(" -On "));
            T.Check("toolkit: TV command — an empty hardware id is omitted", !TkEngine.TvCmd("tv-on.cmd", "TV", "").Contains("-HwId"));
            T.Eq("toolkit: a shortcut name loses characters Windows forbids", "Звук вкл", TkEngine.TkParamSafeName("Звук: вкл?"));
            T.Eq("toolkit: an unusable shortcut name falls back", "Fix sound", TkEngine.TkParamSafeName("..."));
        }

        // ---------- установка, сохранение, удаление в фикстуре ----------

        private static void FixtureCases(RegistryKey hive)
        {
            ReaperFixture(hive);
            AudioFixture(hive);
            OtherFixtures(hive);
        }

        private static void ReaperFixture(RegistryKey hive)
        {
            FakeScheduler sch;
            TkEnv env = Fixture("reaper", hive, out sch);
            TkItem it = TkCatalog.Find("proc-reaper");
            string dir = TkEngine.DeployDir(it, env);
            T.Eq("toolkit: fixture — nothing installed at first", TkState.NotInstalled, TkEngine.Probe(it, env).State);

            List<string> log = new List<string>();
            T.Eq("toolkit: fixture — install succeeds", null, TkEngine.Install(it, With(it, "minutes", "180"), env, log));
            bool same = true;
            foreach (KeyValuePair<string, string> f in TkPayload.Files(it))
            {
                string p = Path.Combine(dir, f.Key);
                if (!File.Exists(p) || Convert.ToBase64String(File.ReadAllBytes(p)) != Convert.ToBase64String(TkPayload.Bytes(f.Value))) same = false;
            }
            T.Check("toolkit: fixture — every script is on disk byte for byte", same && File.Exists(Path.Combine(dir, "reap.ps1")));
            TkStatus s = TkEngine.Probe(it, env);
            T.Check("toolkit: fixture — installed item probes as OK with the saved interval", s.State == TkState.Ok && s.Values["minutes"] == "180",
                    s.State + " " + s.Values["minutes"]);

            T.Eq("toolkit: fixture — disabling succeeds", null, TkEngine.SetEnabled(it, s.Values, env, false, log));
            T.Eq("toolkit: fixture — a disabled task probes as Disabled", TkState.Disabled, TkEngine.Probe(it, env).State);
            File.WriteAllText(Path.Combine(dir, "reap.config.json"), "{\"mine\":true}");
            T.Eq("toolkit: fixture — saving new parameters succeeds", null, TkEngine.Install(it, With(it, "minutes", "360", "start", "01:10"), env, log));
            T.Check("toolkit: fixture — Save keeps a disabled task disabled", !sch.Tasks["ProcReaper"].Enabled);
            s = TkEngine.Probe(it, env);
            T.Check("toolkit: fixture — Save re-registers with the new schedule", s.Values["minutes"] == "360" && s.Values["start"] == "01:10");
            T.Eq("toolkit: fixture — Save never overwrites the user's own reap.config.json", "{\"mine\":true}", File.ReadAllText(Path.Combine(dir, "reap.config.json")));

            TkEngine.SetEnabled(it, s.Values, env, true, log);
            File.AppendAllText(Path.Combine(dir, "reap.ps1"), "# edited\r\n");
            T.Eq("toolkit: fixture — a script edited on disk probes as Differs", TkState.Differs, TkEngine.Probe(it, env).State);
            sch.Tasks.Remove("ProcReaper");
            T.Eq("toolkit: fixture — a task deleted in Windows probes as Partial", TkState.Partial, TkEngine.Probe(it, env).State);

            T.Eq("toolkit: fixture — remove succeeds", null, TkEngine.Remove(it, env, log));
            T.Check("toolkit: fixture — remove deletes scripts but keeps the user's config", !File.Exists(Path.Combine(dir, "reap.ps1")) && File.Exists(Path.Combine(dir, "reap.config.json")));
            T.Eq("toolkit: fixture — a kept config alone does not look installed", TkState.NotInstalled, TkEngine.Probe(it, env).State);

            // Задача, закрытая от пользователя (SYSTEM старой установки): не «не установлено», и Save не выдаёт это за успех.
            FakeScheduler sch2;
            TkEnv env2 = Fixture("docker", hive, out sch2);
            TkItem docker = TkCatalog.Find("docker-maint");
            TkEngine.Install(docker, docker.Defaults(), env2, log);
            T.Check("toolkit: fixture — docker daily task registered", sch2.Tasks.ContainsKey("DockerMaint-AutoPrune") && sch2.Tasks["DockerMaint-AutoPrune"].Daily);
            T.Eq("toolkit: fixture — turning auto-clean off succeeds", null, TkEngine.Install(docker, With(docker, "auto", "false"), env2, log));
            TkStatus ds = TkEngine.Probe(docker, env2);
            T.Check("toolkit: fixture — auto-clean off removes the task and reads back as off", !sch2.Tasks.ContainsKey("DockerMaint-AutoPrune") && ds.Values["auto"] == "false" && ds.State == TkState.Ok,
                    ds.State + " " + ds.Values["auto"]);
            sch2.Denied.Add("DockerMaint-AutoPrune");
            T.Check("toolkit: fixture — a denied task makes Save report an error", TkEngine.Install(docker, docker.Defaults(), env2, log) != null);
            T.Check("toolkit: fixture — a denied task is visible in the status", TkEngine.Probe(docker, env2).Denied);
        }

        private static void AudioFixture(RegistryKey hive)
        {
            FakeScheduler sch;
            TkEnv env = Fixture("audio", hive, out sch);
            TkItem it = TkCatalog.Find("audio-fix");
            string legacy = Fx.MakeDir(env.UserProfile, @".claude\tools\audio-fix");
            File.WriteAllText(Path.Combine(legacy, "audio-fix.ps1"), "# old");
            File.WriteAllText(Path.Combine(legacy, "audio-fix.config.json"), "{\"preferredEndpoint\":\"Old DAC\",\"wakeDelaySec\":120,\"portReset\":false}");
            TkStatus before = TkEngine.Probe(it, env);
            T.Check("toolkit: fixture — an old audio install is noticed and explained", before.State != TkState.NotInstalled && before.Note != null);
            T.Check("toolkit: fixture — the old install's settings are shown", before.Values["preferredEndpoint"] == "Old DAC" && before.Values["wakeDelaySec"] == "120" && before.Values["portReset"] == "false");

            List<string> log = new List<string>();
            T.Eq("toolkit: fixture — audio install succeeds", null, TkEngine.Install(it, before.Values, env, log));
            string dir = TkEngine.DeployDir(it, env);
            T.Check("toolkit: fixture — audio scripts land in ProgramData\\SysDeck\\toolkit", File.Exists(Path.Combine(env.ProgramData, @"SysDeck\toolkit\audio-fix\audio-fix.ps1")));
            JVal cfg = Jsn.Parse(File.ReadAllText(Path.Combine(dir, "audio-fix.config.json"), Encoding.UTF8));
            T.Check("toolkit: fixture — the carried-over settings are written to the new config", cfg.GetStr("preferredEndpoint") == "Old DAC" && !cfg.Get("portReset").B);
            T.Check("toolkit: fixture — three SYSTEM tasks point at the protected copy",
                    sch.Tasks.Count == 3 && sch.Tasks["AudioFix-Boot"].System && sch.Tasks["AudioFix-Wake"].Arguments.Contains(dir));
            TkStatus after = TkEngine.Probe(it, env);
            T.Check("toolkit: fixture — after the move no warning about the old place", after.Note == null && after.State == TkState.Ok, after.State + " " + after.Note);
            T.Eq("toolkit: fixture — the old copy is left in place", true, File.Exists(Path.Combine(legacy, "audio-fix.ps1")));

            TkEngine.Install(it, With(it, "watch", "false"), env, log);
            T.Check("toolkit: fixture — turning the recorder off deletes only its task", sch.Tasks.Count == 2 && !sch.Tasks.ContainsKey("AudioFix-Watch"));
            T.Eq("toolkit: fixture — the recorder reads back as off", "false", TkEngine.Probe(it, env).Values["watch"]);

            // Подменённая заранее папка — отказ, а не запись скрипта для SYSTEM туда, куда она ведёт.
            FakeScheduler sch2;
            TkEnv env2 = Fixture("audio-junction", hive, out sch2);
            string target = Fx.MakeDir(Fx.Root, "toolkit-audio-junction-target");
            string link = Path.Combine(env2.ProgramData, "SysDeck");
            if (Fx.Junction(link, target))
            {
                string err = TkEngine.Install(it, it.Defaults(), env2, log);
                T.Check("toolkit: fixture — a junction in the protected path is refused", err != null && Directory.GetFileSystemEntries(target).Length == 0, err);
                T.Eq("toolkit: fixture — no task is registered after the refusal", 0, sch2.Tasks.Count);
            }
            else T.Skip("toolkit: fixture — a junction in the protected path is refused", "junction could not be created");
        }

        private static void OtherFixtures(RegistryKey hive)
        {
            List<string> log = new List<string>();
            FakeScheduler sch;

            TkEnv env = Fixture("mpo", hive, out sch);
            TkItem mpo = TkCatalog.Find("mpo-fix");
            T.Eq("toolkit: fixture — MPO not applied at first", TkState.NotInstalled, TkEngine.Probe(mpo, env).State);
            T.Eq("toolkit: fixture — MPO install succeeds", null, TkEngine.Install(mpo, null, env, log));
            using (RegistryKey k = hive.OpenSubKey("Dwm")) T.Eq("toolkit: fixture — OverlayTestMode is a DWORD 5", 5, k.GetValue("OverlayTestMode"));
            T.Eq("toolkit: fixture — applied MPO without re-apply probes as OK", TkState.Ok, TkEngine.Probe(mpo, env).State);
            TkEngine.Install(mpo, With(mpo, "reapply", "true"), env, log);
            T.Check("toolkit: fixture — re-apply registers the logon task", sch.Tasks.ContainsKey("MpoFix-Logon") && TkEngine.Probe(mpo, env).Values["reapply"] == "true");
            T.Eq("toolkit: fixture — MPO remove succeeds", null, TkEngine.Remove(mpo, env, log));
            using (RegistryKey k = hive.OpenSubKey("Dwm")) T.Check("toolkit: fixture — remove restores MPO and deletes the task", k.GetValue("OverlayTestMode") == null && sch.Tasks.Count == 0);

            env = Fixture("wsl", hive, out sch);
            TkItem wsl = TkCatalog.Find("wslconfig");
            string path = Path.Combine(env.UserProfile, ".wslconfig");
            File.WriteAllText(path, "[wsl2]\nprocessors=4\n");
            T.Eq("toolkit: fixture — wsl limits install succeeds", null, TkEngine.Install(wsl, With(wsl, "memory", "12"), env, log));
            T.Eq("toolkit: fixture — the original .wslconfig is backed up", "[wsl2]\nprocessors=4\n", File.ReadAllText(path + ".sysdeck.bak"));
            TkStatus ws = TkEngine.Probe(wsl, env);
            T.Check("toolkit: fixture — wsl limits read back", ws.State == TkState.Ok && ws.Values["memory"] == "12" && File.ReadAllText(path).Contains("processors=4"), ws.State + " " + ws.Values["memory"]);
            TkEngine.Install(wsl, With(wsl, "memory", "20"), env, log);
            T.Eq("toolkit: fixture — the backup is made once, not over the first original", "[wsl2]\nprocessors=4\n", File.ReadAllText(path + ".sysdeck.bak"));
            TkEngine.Remove(wsl, env, log);
            T.Check("toolkit: fixture — wsl remove keeps the user's keys", File.ReadAllText(path) == "[wsl2]\nprocessors=4\n" && TkEngine.Probe(wsl, env).State == TkState.NotInstalled);

            env = Fixture("hook", hive, out sch);
            TkItem hook = TkCatalog.Find("claude-hook");
            T.Check("toolkit: fixture — the hook needs Claude Code installed", TkEngine.Install(hook, null, env, log) != null);
            Fx.MakeDir(env.UserProfile, ".claude");
            T.Eq("toolkit: fixture — hook install succeeds", null, TkEngine.Install(hook, null, env, log));
            string settings = Path.Combine(env.UserProfile, @".claude\settings.json");
            T.Check("toolkit: fixture — the hook script and its registration are in place",
                    File.Exists(Path.Combine(env.UserProfile, @".claude\hooks\proc-reap-session.mjs")) && TkEngine.HookRegistered(env)
                    && File.ReadAllText(settings).Contains(Path.Combine(env.UserProfile, @".claude\hooks").Replace('\\', '/')));
            T.Eq("toolkit: fixture — hook probes as OK", TkState.Ok, TkEngine.Probe(hook, env).State);
            File.WriteAllText(Path.Combine(env.UserProfile, @".claude\hooks\other.mjs"), "x");
            TkEngine.Remove(hook, env, log);
            T.Check("toolkit: fixture — hook remove leaves other hooks and the hooks folder",
                    !TkEngine.HookRegistered(env) && File.Exists(Path.Combine(env.UserProfile, @".claude\hooks\other.mjs")) && File.Exists(settings + ".bak"));

            env = Fixture("tv", hive, out sch);
            TkItem tv = TkCatalog.Find("tv-switch");
            T.Check("toolkit: fixture — TV switch without a name or id is refused before any file is written",
                    TkEngine.Install(tv, tv.Defaults(), env, log) != null && !Directory.Exists(Path.Combine(env.UserProfile, @"Tools\tv-switch")));
            T.Eq("toolkit: fixture — TV switch install succeeds", null, TkEngine.Install(tv, With(tv, "name", "LG OLED", "hwid", ""), env, log));
            TkStatus ts = TkEngine.Probe(tv, env);
            T.Check("toolkit: fixture — TV name reads back from the bat file", ts.Values["name"] == "LG OLED" && ts.State == TkState.Ok, ts.State + " " + ts.Values["name"]);
            T.Check("toolkit: fixture — a missing DisplayConfig module is explained", ts.Note != null);
            T.Check("toolkit: fixture — the TV switch goes to ~/Tools", File.Exists(Path.Combine(env.UserProfile, @"Tools\tv-switch\tv.ps1")));
        }

        // ---------- настоящий Планировщик ----------

        private static void LiveSchedulerCases()
        {
            TkComScheduler com = new TkComScheduler();
            string name = "SysDeck-Test-" + Process.GetCurrentProcess().Id;
            TkTaskState none = com.Query(name);
            T.Check("toolkit: live — a missing task is not an error", !none.Exists && none.Error == null && !none.Denied, none.Error);

            TkTaskSpec t = new TkTaskSpec();
            t.Name = name;
            t.Description = "SysDeck test task (safe to delete)";
            t.Command = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            t.Arguments = "/c exit 0";
            t.RepeatMinutes = 60;
            t.StartTime = "03:17";
            t.Enabled = false;          // выключена: за время прогона ничего не запустится
            try
            {
                string err = com.Register(t);
                if (err != null && err.IndexOf("denied", StringComparison.OrdinalIgnoreCase) < 0 && err.IndexOf("доступа", StringComparison.OrdinalIgnoreCase) < 0)
                    T.Check("toolkit: live — a user task registers without administrator rights", false, err);
                else if (err != null) T.Skip("toolkit: live — register a user task", err);
                else
                {
                    TkTaskState q = com.Query(name);
                    Dictionary<string, string> x = TkXml.Read(q.Xml);
                    T.Check("toolkit: live — Windows accepted our XML and returns the same schedule",
                            q.Exists && !q.Enabled && Get(x, "minutes") == "60" && Get(x, "start") == "03:17", Get(x, "minutes") + " " + Get(x, "start") + " " + q.Error);
                    T.Eq("toolkit: live — enabling through COM succeeds", null, com.SetEnabled(name, true));
                    T.Eq("toolkit: live — disabling through COM succeeds", null, com.SetEnabled(name, false));
                    T.Check("toolkit: live — the state follows", !com.Query(name).Enabled);
                    t.RepeatMinutes = 90;
                    T.Eq("toolkit: live — re-registering (Save) succeeds", null, com.Register(t));
                    T.Eq("toolkit: live — the new interval is in Windows", "90", Get(TkXml.Read(com.Query(name).Xml), "minutes"));
                }
            }
            finally
            {
                T.Eq("toolkit: live — delete succeeds", null, com.Delete(name));
            }
            T.Check("toolkit: live — the task is gone", !com.Query(name).Exists);
            T.Eq("toolkit: live — deleting a missing task is not an error", null, com.Delete(name));

            // Задачи этой машины — только чтение.
            TkTaskState reaper = com.Query("ProcReaper");
            if (reaper.Exists && !reaper.Denied)
                T.Check("toolkit: live — the installed reaper's schedule reads back", TkXml.Read(reaper.Xml).ContainsKey("minutes"));
            else T.Skip("toolkit: live — the installed reaper's schedule reads back", "no ProcReaper task here");
            TkTaskState sys = com.Query("AudioFix-Boot");
            if (sys.Exists) T.Check("toolkit: live — a SYSTEM task is reported as existing (denied or readable), not missing", sys.Error == null, sys.Error);
            else T.Skip("toolkit: live — a SYSTEM task hidden from the user", "no AudioFix-Boot task here");
        }
    }
}
