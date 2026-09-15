// SysDeck — область «hud»: вид и расчёты оверлея уровня RTSS/FPS Monitor. Пороги и формат строк,
// переход старого сочетания на Alt+R, наборы строк, своё место на экране, мин./сред./макс., отрисовка со стилем,
// расчётные строки (узкое место, кадры на ватт, режим вывода), запись лагов и её отчёт, живое чтение предела кадров
// NVIDIA (только чтение — настройки драйвера тест не меняет).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using SysDeck.Capture;

namespace SysDeck.Tests
{
    internal static class HudEtalonTests
    {
        internal static void Run()
        {
            ItemFormat();
            Alarms();
            SettingsMigration();
            Placement();
            Stats();
            StyledRendering();
            Derived();
            PresentRows();
            LagRecorder();
            FrameLimiter();
            CpuLoadCapped();
        }

        private static void ItemFormat()
        {
            List<HudItem> old = HudItem.ParseList("cpu.load:3:1000:FF00FF00");
            T.Eq("hud items: an old 4-field entry is written back byte for byte", "cpu.load:3:1000:FF00FF00", HudItem.FormatList(old));
            HudItem it = new HudItem("gpu.temp") { Stats = true, NoAlarm = true, Warn = 70.5, Crit = 90, LabelColor = unchecked((int)0xFF5AB4FF) };
            string text = HudItem.FormatList(new[] { it });
            HudItem back = HudItem.ParseList(text)[0];
            T.Check("hud items: stats, no-alarm, thresholds and label colour survive a round trip",
                    back.Stats && back.NoAlarm && back.Warn == 70.5 && back.Crit == 90 && back.LabelColor == it.LabelColor && back.Text, text);
            HudItem half = HudItem.ParseList("fps:1:0:0::25:0")[0];
            T.Check("hud items: an empty warn field stays default while crit is custom", double.IsNaN(half.Warn) && half.Crit == 25);
        }

        private static void Alarms()
        {
            HudItem fps = new HudItem("fps");
            T.Eq("hud alarm: 100 FPS is normal", 0, HudAlarm.Level(fps, HudKind.Fps, new HudValue("fps", HudKind.Fps, 100)));
            T.Eq("hud alarm: 50 FPS is high (low is bad)", 1, HudAlarm.Level(fps, HudKind.Fps, new HudValue("fps", HudKind.Fps, 50)));
            T.Eq("hud alarm: 20 FPS is critical", 2, HudAlarm.Level(fps, HudKind.Fps, new HudValue("fps", HudKind.Fps, 20)));
            HudItem gpu = new HudItem("gpu.load");
            T.Eq("hud alarm: GPU load 99 % in a game is not an alarm", 0, HudAlarm.Level(gpu, HudKind.Percent, new HudValue("gpu.load", HudKind.Percent, 99)));
            HudItem temp = new HudItem("cpu.temp") { Crit = 60 };
            T.Eq("hud alarm: a custom critical threshold wins over the default", 2, HudAlarm.Level(temp, HudKind.Temp, new HudValue("cpu.temp", HudKind.Temp, 65)));
            temp.NoAlarm = true;
            T.Eq("hud alarm: «no alarm» turns highlighting off", 0, HudAlarm.Level(temp, HudKind.Temp, new HudValue("cpu.temp", HudKind.Temp, 99)));
            HudValue ram = new HudValue("ram", HudKind.Memory, 15.0 * 1073741824.0);
            ram.Total = 16.0 * 1073741824.0;
            T.Eq("hud alarm: memory thresholds are percent of the total", 2, HudAlarm.Level(new HudItem("ram"), HudKind.Memory, ram));
            HudRow row = HudFormat.Row(new HudItem("fps") { Color = unchecked((int)0xFF00FF00) }, new HudValue("fps", HudKind.Fps, 20));
            T.Eq("hud alarm: the row carries the level even with a custom colour", 2, row.Level);
        }

        private static void SettingsMigration()
        {
            CapSettings v2 = CapSettings.FromJson("{\"Version\":2,\"Hotkeys\":{\"Hud\":\"Ctrl+Alt+F12\"}}");
            T.Eq("hud hotkey: the saved old default Ctrl+Alt+F12 moves to Alt+R", "Alt+R", v2.Hotkeys[CapAction.Hud]);
            CapSettings own = CapSettings.FromJson("{\"Version\":2,\"Hotkeys\":{\"Hud\":\"Ctrl+Shift+H\"}}");
            T.Eq("hud hotkey: a hotkey the person chose is kept", "Ctrl+Shift+H", own.Hotkeys[CapAction.Hud]);
            T.Check("hud hotkey: reset, next set and lag recording have distinct defaults",
                    CapActions.DefaultHotkey(CapAction.HudReset) != CapActions.DefaultHotkey(CapAction.HudScene)
                    && CapActions.DefaultHotkey(CapAction.HudScene) != CapActions.DefaultHotkey(CapAction.LagRecord));
            CapSettings bare = CapSettings.FromJson("{\"Version\":2,\"Hotkeys\":{\"ShotRegion\":\"F3\",\"Hud\":\"Ctrl+Alt+F12\"}}");
            T.Check("hud hotkey: a settings file from before the overlay keys gets Alt+R and the default reset / set / lag keys",
                    bare.Hotkeys[CapAction.Hud] == "Alt+R" && bare.Hotkeys[CapAction.HudReset] == "Ctrl+Alt+R"
                    && bare.Hotkeys[CapAction.HudScene] == "Ctrl+Alt+N" && bare.Hotkeys[CapAction.LagRecord] == "Ctrl+Alt+L");
            CapSettings v3 = CapSettings.FromJson("{\"Version\":3,\"ToastEnabled\":false,\"Hotkeys\":{\"HudScene\":\"Ctrl+Alt+S\"}}");
            T.Eq("hud hotkey: the old next-set default Ctrl+Alt+S (taken by Folder sizes) moves to Ctrl+Alt+N", "Ctrl+Alt+N", v3.Hotkeys[CapAction.HudScene]);
            T.Check("settings v3 → v4: toasts switched off after version 3 stay off", !v3.ToastEnabled);
            CapSettings v4 = CapSettings.FromJson("{\"Version\":4,\"Hotkeys\":{\"HudScene\":\"Ctrl+Alt+S\"}}");
            T.Eq("hud hotkey: Ctrl+Alt+S chosen after version 4 is kept", "Ctrl+Alt+S", v4.Hotkeys[CapAction.HudScene]);
            T.Eq("hud hotkey: a fresh install toggles the overlay with Alt+R", "Alt+R", new CapSettings().Hotkey(CapAction.Hud).ToString());

            HotkeySpec altR, ctrlAltR, winR;
            HotkeySpec.TryParse("Alt+R", out altR);
            HotkeySpec.TryParse("Ctrl+Alt+R", out ctrlAltR);
            HotkeySpec.TryParse("Win+Alt+R", out winR);
            Dictionary<CapAction, HotkeySpec> held = new Dictionary<CapAction, HotkeySpec>();
            held[CapAction.Hud] = altR;
            held[CapAction.HudReset] = ctrlAltR;
            held[CapAction.LagRecord] = winR;
            held[CapAction.ShotRegion] = altR;
            Dictionary<CapAction, int> errs = new Dictionary<CapAction, int>();
            errs[CapAction.Hud] = HotkeyOwners.ErrorHotkeyAlreadyRegistered;
            errs[CapAction.HudReset] = 0;
            errs[CapAction.LagRecord] = HotkeyOwners.ErrorHotkeyAlreadyRegistered;
            errs[CapAction.ShotRegion] = 5;
            Dictionary<CapAction, HotkeySpec> grab = KeyGrab.Busy(held, errs, true);
            T.Check("key grab: only a busy (1409) non-Win shortcut is taken over — Alt+R of NVIDIA App yes, Win+Alt+R and other errors no",
                    grab.Count == 1 && grab.ContainsKey(CapAction.Hud), string.Join(",", new List<CapAction>(grab.Keys).ConvertAll(a => a.ToString()).ToArray()));
            T.Eq("key grab: the setting off takes nothing", 0, KeyGrab.Busy(held, errs, false).Count);
            CapAction hit;
            T.Check("key grab: Alt+R press matches the overlay toggle", KeyGrab.Match(grab, 0x52, HotkeySpec.MOD_ALT, out hit) && hit == CapAction.Hud);
            T.Check("key grab: Ctrl+Alt+R press does not match Alt+R", !KeyGrab.Match(grab, 0x52, HotkeySpec.MOD_ALT | HotkeySpec.MOD_CONTROL, out hit));
            T.Check("key grab: a settings file without the flag takes busy keys", CapSettings.FromJson("{\"Version\":3}").TakeBusyKeys);
            CapSettings off = new CapSettings();
            off.TakeBusyKeys = false;
            T.Check("key grab: unticked survives a round trip", !CapSettings.FromJson(off.ToJson()).TakeBusyKeys);

            // Агент со старой сборкой держал бы Ctrl+Alt+F12 и после обновления exe — он перезапускается сам.
            DateTime built = new DateTime(2026, 9, 15, 13, 0, 0, DateTimeKind.Utc), rebuilt = built.AddHours(4);
            T.Check("agent build: same exe — no restart", !AgentBuild.ShouldRestart(built, built, rebuilt.AddMinutes(1), false));
            T.Check("agent build: new exe that settled 10 s — restart", AgentBuild.ShouldRestart(built, rebuilt, rebuilt.AddSeconds(10), false));
            T.Check("agent build: exe still being written — wait", !AgentBuild.ShouldRestart(built, rebuilt, rebuilt.AddSeconds(3), false));
            T.Check("agent build: recording / selection / editor open — wait", !AgentBuild.ShouldRestart(built, rebuilt, rebuilt.AddMinutes(1), true));
            T.Check("agent build: exe renamed away and not replaced yet — wait", !AgentBuild.ShouldRestart(built, DateTime.MinValue, rebuilt, false));
            string exeDir = Path.Combine(Path.GetTempPath(), "wpc-agentbuild-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(exeDir);
            try
            {
                string exe = Path.Combine(exeDir, "app.exe");
                T.Eq("agent build: a missing exe has no stamp", DateTime.MinValue, AgentBuild.Stamp(exe));
                File.WriteAllText(exe, "x");
                T.Check("agent build: an existing exe has its write time", AgentBuild.Stamp(exe) == File.GetLastWriteTimeUtc(exe));

                // Страница читает hud-status.txt раз в секунду, HUD в это время его заменяет.
                string status = Path.Combine(exeDir, "hud-status.txt");
                CapPaths.WriteAtomic(status, "pid=1\n");
                bool replaced;
                using (new FileStream(status, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    try { CapPaths.WriteAtomic(status, "pid=2\n"); replaced = true; }
                    catch (IOException) { replaced = false; }
                }
                T.Check("status file: replaced while a reader holds it the way ReadShared opens it", replaced && CapPaths.ReadShared(status) == "pid=2\n");
            }
            finally { try { Directory.Delete(exeDir, true); } catch { } }

            CapSettings s = new CapSettings();
            s.SetSceneItems(1, "fps:1:0:0");
            s.HudScene = 1;
            s.HudFontSize = 99;
            s.HudStatsSeconds = 5000;
            CapSettings r = CapSettings.FromJson(s.ToJson());
            T.Check("hud scenes: the active set is the chosen one after a round trip", r.HudScene == 1 && r.ActiveItems == "fps:1:0:0", r.ActiveItems);
            T.Eq("hud style: font size is clamped", HudStyle.MaxFontSize, HudStyle.From(r).FontSize);
            T.Eq("hud stats: the window is clamped to what the history keeps", 600, r.HudStatsSeconds);
            T.Eq("hud style: an unknown font falls back to the default", HudStyle.DefaultFont, HudStyle.ValidFont("Comic Sans; rm -rf"));
            foreach (KeyValuePair<string, string> p in HudPresets.All())
                T.Check("hud presets: «" + p.Key + "» uses only known metrics", AllKnown(p.Value), p.Value);
        }

        private static bool AllKnown(string items)
        {
            foreach (HudItem it in HudItem.ParseList(items)) if (HudCatalog.Find(it.Id) == null) return false;
            return HudItem.ParseList(items).Count > 0;
        }

        private static void Placement()
        {
            Rectangle area = new Rectangle(0, 0, 1920, 1040);
            Size size = new Size(200, 300);
            Point p = HudLayout.Place(area, size, HudCorner.Custom, 12, 500, 1000);
            T.Check("hud place: custom 50 % × 100 % is centred at the bottom", p.X == 860 && p.Y == 740, p.ToString());
            int x, y;
            HudLayout.ToPermille(area, new Rectangle(new Point(430, 185), size), out x, out y);
            T.Check("hud place: a dragged window converts back to permille", x == 250 && y == 250, x + " " + y);
            Point off = HudLayout.Place(area, size, HudCorner.Custom, 12, 5000, -5);
            T.Check("hud place: out-of-range permille stays on the screen", off.X == 1720 && off.Y == 0, off.ToString());
        }

        private static void Stats()
        {
            HudBoard board = new HudBoard();
            List<HudItem> items = new List<HudItem> { new HudItem("cpu.load") { Stats = true, IntervalMs = 250 } };
            DateTime t0 = DateTime.Now.AddSeconds(-10);
            double[] values = { 10, 20, 90, 40 };
            for (int i = 0; i < values.Length; i++)
            {
                HudFrame f = new HudFrame();
                f.Put("cpu.load", HudKind.Percent, values[i]);
                board.Advance(f, items, t0.AddSeconds(i));
            }
            List<HudRow> rows = board.Rows(items, 60, 60);
            T.Eq("hud stats: min / avg / max over the window", "↓10% ⌀40% ↑90%", rows[0].Stats);
            board.ResetStats();
            T.Check("hud stats: reset clears min / avg / max", board.Rows(items, 60, 60)[0].Stats == null, board.Rows(items, 60, 60)[0].Stats);
            List<HudItem> noStats = new List<HudItem> { new HudItem("cpu.load") };
            T.Check("hud stats: rows without the flag show none", board.Rows(noStats, 60)[0].Stats == null);

            HudBoard ft = new HudBoard();
            HudFrame frame = new HudFrame();
            frame.Put("fps.frametime", HudKind.Ms, 7);
            HudValue d = new HudValue("display.hz", HudKind.Number, 144);
            frame.Put(d);
            List<HudItem> ftItems = new List<HudItem> { new HudItem("fps.frametime") { Graph = true } };
            ft.Advance(frame, ftItems, DateTime.Now);
            T.Check("hud target: frame time graph gets a line at 1000 / refresh rate", Math.Abs(ft.Rows(ftItems, 30)[0].Target - 1000.0 / 144) < 0.01);
        }

        private static void StyledRendering()
        {
            HudRow a = HudFormat.Row(new HudItem("cpu.load") { Stats = true }, new HudValue("cpu.load", HudKind.Percent, 40));
            a.Stats = "↓10% ⌀40% ↑90%";
            HudRow crit = HudFormat.Row(new HudItem("fps"), new HudValue("fps", HudKind.Fps, 12));
            List<HudRow> rows = new List<HudRow> { a, crit };
            HudStyle small = new HudStyle { FontSize = 11 }, big = new HudStyle { FontSize = 22 };
            using (Bitmap s1 = HudRender.Draw(rows, 1f, small))
            using (Bitmap s2 = HudRender.Draw(rows, 1f, big))
            {
                T.Check("hud style: a bigger font makes a bigger overlay", s2.Width > s1.Width && s2.Height > s1.Height, s1.Size + " → " + s2.Size);
            }
            HudStyle line = new HudStyle { RowLayout = true };
            using (Bitmap col = HudRender.Draw(rows, 1f, new HudStyle()))
            using (Bitmap row = HudRender.Draw(rows, 1f, line))
                T.Check("hud style: row layout is wider than tall", row.Width > row.Height * 3 && row.Height < col.Height, col.Size + " / " + row.Size);
            HudStyle headed = new HudStyle { Header = "● REC 00:10" };
            using (Bitmap plain = HudRender.Draw(rows, 1f, new HudStyle()))
            using (Bitmap withHeader = HudRender.Draw(rows, 1f, headed))
                T.Check("hud style: a header adds a line", withHeader.Height > plain.Height);
            using (Bitmap onlyHeader = HudRender.Draw(new List<HudRow>(), 1f, headed))
                T.Check("hud style: recording while hidden still draws the header", onlyHeader.Width > 40);
            using (Bitmap bmp = HudRender.Draw(new List<HudRow> { crit }, 1f, new HudStyle { Opacity = 20 }))
            {
                Color strip = bmp.GetPixel(6, bmp.Height / 2);
                T.Check("hud style: a critical row gets a red strip", strip.R > strip.G + 20, strip.ToString());
            }
            T.Check("hud style: group colours differ for CPU and GPU", HudStyle.GroupColor(HudGroups.Cpu) != HudStyle.GroupColor(HudGroups.Gpu));
        }

        private static void Derived()
        {
            HudFrame f = new HudFrame();
            f.Put("fps", HudKind.Fps, 120);
            f.Put("gpu.power", HudKind.Watts, 200);
            f.Put("gpu.load", HudKind.Percent, 99);
            HudCollector.Derived(f);
            T.Check("hud derived: FPS per watt", Math.Abs(f.Get("fps.perwatt").Value - 0.6) < 1e-9);
            T.Eq("hud derived: GPU at 99 % is the bottleneck", Tr.S("видеокарта", "GPU"), f.Get("fps.bottleneck").Text);

            HudFrame cpu = new HudFrame();
            cpu.Put("fps", HudKind.Fps, 70);
            cpu.Put("gpu.load", HudKind.Percent, 60);
            cpu.Put("cpu.coremax", HudKind.Percent, 98);
            cpu.Put("display.hz", HudKind.Number, 144);
            T.Eq("hud derived: one core at 98 % with an idle GPU is a CPU limit", Tr.S("процессор", "CPU"), HudCollector.Bottleneck(cpu));
            HudFrame vs = new HudFrame();
            vs.Put("fps", HudKind.Fps, 59.8);
            vs.Put("gpu.load", HudKind.Percent, 50);
            vs.Put("display.hz", HudKind.Number, 60);
            T.Eq("hud derived: frames pinned to the refresh rate", Tr.S("частота монитора / V-Sync", "refresh rate / V-Sync"), HudCollector.Bottleneck(vs));
            HudFrame none = new HudFrame();
            none.Put("gpu.load", HudKind.Percent, 99);
            T.Check("hud derived: no frames — no verdict", HudCollector.Bottleneck(none) == null);
            HudFrame idle = new HudFrame();
            idle.Put("fps", HudKind.Fps, 100);
            idle.Put("gpu.power", HudKind.Watts, 2);
            HudCollector.Derived(idle);
            T.Check("hud derived: no FPS per watt from an idle card reading", idle.Get("fps.perwatt") == null);
            T.Check("hud live: refresh rate of the primary monitor is plausible", HudWindowsSource.RefreshRate(IntPtr.Zero) >= 24, HudWindowsSource.RefreshRate(IntPtr.Zero).ToString());
        }

        private static void PresentRows()
        {
            long freq = 1000;
            HudPresentTracker tr = new HudPresentTracker();
            tr.NoteApi(10, HudPresentInfo.ApiDxgi, 0, true, 5000);
            tr.NoteModel(10, 9, 5000);
            HudFrame f = new HudFrame();
            HudFpsSource.PutPresent(f, tr.Info(10), 5500, freq);
            T.Eq("hud present: DXGI api", "DXGI (Direct3D 10–12)", f.Get("fps.api").Text);
            T.Eq("hud present: sync 0 with the tearing flag", Tr.S("выкл, с разрывами", "off, tearing allowed"), f.Get("fps.vsync").Text);
            T.Eq("hud present: flip model through DWM", Tr.S("через DWM, обмен", "via DWM, flip"), f.Get("fps.presentmode").Text);

            tr.NoteModel(99, 3, 9000);                    // ядро сообщает модели, но не для этой игры
            tr.NoteApi(10, HudPresentInfo.ApiDxgi, 1, false, 9000);
            HudFrame g = new HudFrame();
            HudFpsSource.PutPresent(g, tr.Info(10), 9100, freq);
            T.Eq("hud present: V-Sync on", Tr.S("вкл", "on"), g.Get("fps.vsync").Text);
            T.Eq("hud present: no composition events for the game — direct", Tr.S("напрямую, без DWM", "direct, no DWM"), g.Get("fps.presentmode").Text);

            HudPresentTracker silent = new HudPresentTracker();
            silent.NoteApi(10, HudPresentInfo.ApiD3d9, -1, false, 100);
            HudFrame h = new HudFrame();
            HudFpsSource.PutPresent(h, silent.Info(10), 200, freq);
            T.Check("hud present: without kernel model events no present mode is guessed", h.Get("fps.presentmode") == null && h.Get("fps.vsync") == null);
            HudFrame k = new HudFrame();
            HudFpsSource.PutPresent(k, null, 200, freq);
            T.Eq("hud present: frames without DXGI/D3D9 events", Tr.S("OpenGL / Vulkan / другое", "OpenGL / Vulkan / other"), k.Get("fps.api").Text);
        }

        private static void LagRecorder()
        {
            List<float> ms = new List<float>();
            List<double> t = new List<double>();
            double at = 0;
            for (int i = 0; i < 6000; i++)
            {
                float v = i % 1000 == 500 ? 120f : 8.33f;
                at += v / 1000.0;
                ms.Add(v); t.Add(at);
            }
            HudLagReport.Summary s = HudLagReport.Analyze(ms, t);
            T.Eq("hud lag: six 120 ms frames among 8 ms ones are six hitches", 6, s.Hitches.Count);
            T.Check("hud lag: average FPS and 1 % low", s.AvgFps > 100 && s.Low1 < s.AvgFps, s.AvgFps + " / " + s.Low1);
            T.Eq("hud lag: folder name keeps only safe characters", "My_Game_1", HudLagRecorder.SafeName("My Game?1"));
            T.Eq("hud lag: empty game name", "desktop", HudLagRecorder.SafeName(".."));

            // Настоящие «Документы» тест не трогает: корень подменяется временной папкой.
            string docs = Path.Combine(Path.GetTempPath(), "wpc-lag-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(docs);
            HudLagRecorder.DocumentsOverride = docs;
            string error;
            HudLagRecorder rec = HudLagRecorder.Start("wpc-selftest", out error);
            if (rec == null) { HudLagRecorder.DocumentsOverride = null; T.Check("hud lag: a recording writes report.md and CSV files", false, error); return; }
            try
            {
                T.Check("hud lag: the report folder is under Documents", rec.Folder.StartsWith(Path.Combine(docs, "SysDeck"), StringComparison.OrdinalIgnoreCase), rec.Folder);
                long f = 1000000;
                long[] frames = new long[200];
                frames[0] = 10000000;
                for (int i = 1; i < frames.Length; i++) frames[i] = frames[i - 1] + (i == 150 ? 120000 : 8333);   // 8,3 мс и один фриз 120 мс
                rec.AddFrames(frames, 42, f);
                rec.AddFrames(frames, 42, f);                         // тот же буфер ещё раз — дубли не пишутся
                T.Eq("hud lag: frames already recorded are not added twice", frames.Length - 1, rec.FrameMs.Count);
                HudFrame frame = new HudFrame();
                frame.Put("cpu.load", HudKind.Percent, 55);
                frame.Put("cpu.perflimit", HudKind.Percent, 60);
                frame.PutText("app.name", "wpc-selftest");
                rec.AddSystem(frame, 1);
                string report = rec.Stop();
                T.Check("hud lag: report.md written", report != null && File.Exists(report));
                string md = report == null ? "" : File.ReadAllText(report);
                T.Check("hud lag: the report names its files for an AI reader", md.Contains("frames.csv") && md.Contains("system.csv") && md.Contains("## Findings"));
                T.Check("hud lag: a CPU frequency cap becomes an event and a finding", md.Contains("cpu-limit") && md.Contains("capped"), md.Length.ToString());
                string sys = File.ReadAllText(Path.Combine(rec.Folder, "system.csv"));
                T.Check("hud lag: system.csv has a header and a row", sys.StartsWith("t_s,fps,") && sys.Split('\n').Length >= 3);
                T.Check("hud lag: a second report into the same folder is refused, not overwritten", RefusesOverwrite(rec.Folder));
                T.Check("hud lag: the worst hitch is listed", md.Contains("| 120 |"), md);
            }
            finally
            {
                HudLagRecorder.DocumentsOverride = null;
                try { Directory.Delete(docs, true); } catch { }
            }
        }

        private static bool RefusesOverwrite(string folder)
        {
            try
            {
                using (new FileStream(Path.Combine(folder, "report.md"), FileMode.CreateNew)) { }
                return false;
            }
            catch (IOException) { return true; }
        }

        private static void FrameLimiter()
        {
            T.Eq("nv limit: exe name without a path is accepted", "game.exe", NvFrameLimit.ValidExe("Game.EXE"));
            T.Check("nv limit: paths and non-exe names are refused",
                    NvFrameLimit.ValidExe(@"C:\x\game.exe") == null && NvFrameLimit.ValidExe("game.bat") == null && NvFrameLimit.ValidExe("..\\a.exe") == null && NvFrameLimit.ValidExe(".exe") == null);
            if (!NvFrameLimit.Available) { T.Skip("nv limit live: the global limit reads", "no NVIDIA driver"); return; }
            string error;
            int fps = NvFrameLimit.Get(null, out error);
            T.Check("nv limit live: the global limit reads", fps >= 0 && fps <= NvFrameLimit.MaxFps, fps + " " + error);
            int game = NvFrameLimit.Get("wpc-selftest-nonexistent.exe", out error);
            T.Eq("nv limit live: an exe with no profile has no limit", 0, game);
            // Запись в базу профилей драйвера — только по явному SYSDECK_NV_WRITE=1: создаёт и удаляет профиль «WPC <exe>».
            if (Environment.GetEnvironmentVariable("SYSDECK_NV_WRITE") != "1") { T.Skip("nv limit live: write, read back, remove", "SYSDECK_NV_WRITE not set"); return; }
            const string probe = "wpc-selftest-probe.exe";
            string set = NvFrameLimit.Set(probe, 57);
            if (NvFrameLimit.IsPrivilegeError(set)) { T.Skip("nv limit live: write, read back, remove", "driver requires administrator rights: " + set); return; }
            int read = NvFrameLimit.Get(probe, out error);
            string removed = NvFrameLimit.Set(probe, 0);
            int after = NvFrameLimit.Get(probe, out error);
            T.Check("nv limit live: write, read back, remove", set == null && read == 57 && removed == null && after == 0,
                    set + " / " + read + " / " + removed + " / " + after + " " + error);
        }

        // Настоящий счётчик PDH под нагрузкой: «% Processor Utility» на турбочастоте даёт >100 %, в строке — не больше 100.
        private static void CpuLoadCapped()
        {
            bool stop = false;
            List<System.Threading.Thread> spin = new List<System.Threading.Thread>();
            for (int i = 0; i < Environment.ProcessorCount; i++)
            {
                System.Threading.Thread t = new System.Threading.Thread(delegate() { double x = 0; while (!stop) x += Math.Sqrt(x + 1); });
                t.IsBackground = true;
                t.Priority = System.Threading.ThreadPriority.BelowNormal;
                spin.Add(t);
                t.Start();
            }
            double max = double.NaN;
            int seen = 0;
            try
            {
                using (HudPdhSource src = new HudPdhSource())
                {
                    for (int i = 0; i < 8; i++)
                    {
                        System.Threading.Thread.Sleep(250);
                        HudFrame f = new HudFrame();
                        src.Collect(f);
                        HudValue v = f.Get("cpu.load");
                        if (v == null || !HudFormat.Valid(v.Value)) continue;
                        seen++;
                        if (double.IsNaN(max) || v.Value > max) max = v.Value;
                    }
                }
            }
            finally { stop = true; }
            if (seen == 0) { T.Skip("hud pdh: CPU load under full load never exceeds 100 %", "no PDH samples"); return; }
            T.Check("hud pdh: CPU load under full load never exceeds 100 %", max >= 0 && max <= 100, max.ToString("0.0"));
        }
    }
}
