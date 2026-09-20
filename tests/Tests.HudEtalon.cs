// SysDeck — область «hud»: вид и расчёты оверлея уровня RTSS/FPS Monitor. Пороги и формат строк,
// переход старого сочетания на Alt+R, наборы строк, своё место на экране, мин./сред./макс., отрисовка со стилем,
// расчётные строки (узкое место, кадры на ватт, режим вывода), запись лагов и её отчёт, живое чтение предела кадров
// NVIDIA (только чтение — настройки драйвера тест не меняет).
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            ScreenFrames();
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
            // Ширина графиков: полоса идёт во всю ширину столбика, поэтому проценты должны двигать саму ширину
            // столбика, а высоту — нет; строка без графика от настройки не зависит.
            HudRow graphed = HudFormat.Row(new HudItem("fps") { Graph = true }, new HudValue("fps", HudKind.Fps, 120));
            graphed.Graph = true;
            List<HudRow> graphRows = new List<HudRow> { graphed };
            int base100 = 0, high200 = 0, high400 = 0, baseH = 0;
            using (Bitmap w100 = HudRender.Draw(graphRows, 1f, new HudStyle())) { base100 = w100.Width; baseH = w100.Height; }
            using (Bitmap w200 = HudRender.Draw(graphRows, 1f, new HudStyle { GraphWidth = 200 }))
            {
                high200 = w200.Width;
                T.Check("hud style: 200 % graph width roughly doubles the column, height untouched",
                        high200 > base100 * 1.7 && high200 < base100 * 2.3 && w200.Height == baseH,
                        base100 + "x" + baseH + " → " + w200.Size);
            }
            using (Bitmap w400 = HudRender.Draw(graphRows, 1f, new HudStyle { GraphWidth = 400 }))
            {
                high400 = w400.Width;
                T.Check("hud style: 400 % goes wider still", high400 > high200 * 1.7, high200 + " → " + high400);
            }
            using (Bitmap t100 = HudRender.Draw(new List<HudRow> { crit }, 1f, new HudStyle()))
            using (Bitmap t400 = HudRender.Draw(new List<HudRow> { crit }, 1f, new HudStyle { GraphWidth = 400 }))
                T.Check("hud style: a row without a graph ignores the graph width", t400.Size == t100.Size, t100.Size + " / " + t400.Size);
            using (Bitmap lo = HudRender.Draw(graphRows, 1f, new HudStyle { GraphWidth = 10 }))
            using (Bitmap hi = HudRender.Draw(graphRows, 1f, new HudStyle { GraphWidth = 5000 }))
                T.Check("hud style: out-of-range graph widths are clamped, never narrower than normal or endless",
                        lo.Width == base100 && hi.Width == high400, lo.Width + " / " + hi.Width + " / " + high400);

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
            // Кадр собирает DWM — разрешённые игрой разрывы на экран не попадают, и строка не должна их обещать.
            T.Eq("hud present: the tearing flag on a composed frame does not promise tearing",
                 Tr.S("выкл, но кадр собирает DWM — разрывов нет", "off, but DWM composes — no tearing"), f.Get("fps.vsync").Text);
            T.Eq("hud present: flip model through DWM", Tr.S("через DWM, обмен", "via DWM, flip"), f.Get("fps.presentmode").Text);

            // Та же игра, но кадр идёт на экран напрямую: вот тут разрывы настоящие.
            HudPresentTracker direct = new HudPresentTracker();
            direct.NoteApi(10, HudPresentInfo.ApiDxgi, 0, true, 5000);
            direct.NoteModel(99, 9, 5000);                // ядро сообщает модели, но не для этой игры
            HudFrame d = new HudFrame();
            HudFpsSource.PutPresent(d, direct.Info(10), 5500, freq);
            T.Eq("hud present: sync 0 with the tearing flag and a direct frame", Tr.S("выкл, с разрывами", "off, tearing"), d.Get("fps.vsync").Text);

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

        // Кадры на экране: очередь вывода -> вертикальное гашение. Номер отправки в FlipFenceId лежит в одной из
        // половин, какой именно — Windows не обещает, поэтому обе проверяются на живых событиях.
        // TRACE_EVENT_INFO, как его отдаёт TDH: шапка, за ней массив EVENT_PROPERTY_INFO по 24 байта, имена — в хвосте.
        // Смещения полей SysDeck считает сам, и ошибка здесь означала бы чтение чужих байтов под видом номера кадра.
        private static IntPtr TraceEventInfo(string[] names, int[] inTypes, int[] counts)
        {
            const int header = 112, propSize = 24;
            int names0 = header + names.Length * propSize;
            int size = names0;
            foreach (string n in names) size += (n.Length + 1) * 2;
            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) System.Runtime.InteropServices.Marshal.WriteByte(buf, i, 0);
            System.Runtime.InteropServices.Marshal.WriteInt32(buf, 100, names.Length);       // PropertyCount
            int nameAt = names0;
            for (int i = 0; i < names.Length; i++)
            {
                int at = header + i * propSize;
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, at, 0);               // Flags
                System.Runtime.InteropServices.Marshal.WriteInt32(buf, at + 4, nameAt);      // NameOffset
                System.Runtime.InteropServices.Marshal.WriteInt16(buf, at + 8, (short)inTypes[i]);
                System.Runtime.InteropServices.Marshal.WriteInt16(buf, at + 16, (short)counts[i]);
                foreach (char ch in names[i]) { System.Runtime.InteropServices.Marshal.WriteInt16(buf, nameAt, (short)ch); nameAt += 2; }
                System.Runtime.InteropServices.Marshal.WriteInt16(buf, nameAt, 0);
                nameAt += 2;
            }
            return buf;
        }

        private static void EventFieldOffsets()
        {
            const int ptr = 16, u32 = 8, u64 = 10, i32 = 7, i64 = 9, str = 1;
            // Настоящий шаблон MMIOFlip (116) с этой машины: pDxgAdapter, VidPnSourceId, FlipSubmitSequence, …
            IntPtr mmio = TraceEventInfo(new[] { "pDxgAdapter", "VidPnSourceId", "FlipSubmitSequence", "FlipToDriverAllocation" },
                                         new[] { ptr, u32, u32, ptr }, new[] { 1, 1, 1, 1 });
            try
            {
                HudEventFields.Layout l = HudEventFields.Walk(mmio, 4096, false);
                T.Check("hud fields: MMIOFlip field offsets match the provider template",
                        l.Ok && l.Fields["VidPnSourceId"].Offset == 8 && l.Fields["FlipSubmitSequence"].Offset == 12
                        && l.Fields["FlipSubmitSequence"].Size == 4,
                        l.Ok ? l.Fields["VidPnSourceId"].Offset + "/" + l.Fields["FlipSubmitSequence"].Offset : "not read");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mmio); }

            // Настоящий шаблон VSyncDPC (17): FlipFenceId стоит девятым, за полями разной ширины.
            IntPtr vsync = TraceEventInfo(
                new[] { "pDxgAdapter", "VidPnTargetId", "ScannedPhysicalAddress", "VidPnSourceId", "FrameNumber", "FrameQPCTime", "hFlipDevice", "FlipType", "FlipFenceId" },
                new[] { ptr, u32, u64, u32, u32, i64, ptr, u32, u64 }, new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1 });
            try
            {
                HudEventFields.Layout l = HudEventFields.Walk(vsync, 4096, false);
                T.Check("hud fields: VSyncDPC FlipFenceId is found at 48, VidPnSourceId at 20",
                        l.Ok && l.Fields["FlipFenceId"].Offset == 48 && l.Fields["FlipFenceId"].Size == 8 && l.Fields["VidPnSourceId"].Offset == 20,
                        l.Ok ? l.Fields["FlipFenceId"].Offset + "/" + l.Fields["VidPnSourceId"].Offset : "not read");
                ulong v;
                T.Check("hud fields: a field past the end of the record is refused, not read as garbage",
                        !HudEventFields.Value(l, "FlipFenceId", vsync, 20, out v));
                T.Check("hud fields: a field the template does not have is refused",
                        !HudEventFields.Value(l, "FlipSubmitSequence", vsync, 4096, out v));
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(vsync); }

            // Настоящий шаблон PresentHistoryDetailed (215): Token — второй, сразу за hAdapter, и дальше по записи
            // идут массивы переменной длины. Токен обязан читаться до того, как разбор на них оборвётся.
            IntPtr history = TraceEventInfo(
                new[] { "hAdapter", "Token", "Model", "TokenSize", "TokenData", "Flags", "CustomDuration" },
                new[] { ptr, ptr, u32, u32, u64, u32, u32 }, new[] { 1, 1, 1, 1, 1, 1, 1 });
            try
            {
                HudEventFields.Layout l = HudEventFields.Walk(history, 4096, false);
                ulong v;
                T.Check("hud fields: PresentHistory Token sits right after hAdapter",
                        l.Ok && l.Fields["Token"].Offset == 8 && l.Fields["Token"].Size == 8
                        && HudEventFields.Value(l, "Token", history, 4096, out v),
                        l.Ok && l.Fields.ContainsKey("Token") ? l.Fields["Token"].Offset.ToString() : "not read");
                T.Check("hud fields: a 32-bit process makes the token four bytes wide",
                        HudEventFields.Walk(history, 4096, true).Fields["Token"].Size == 4);
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(history); }

            // Массив и поле переменной длины: до массива смещения считаются, после строки разбор обязан оборваться.
            IntPtr mixed = TraceEventInfo(new[] { "Planes", "Tag", "Name", "AfterName" },
                                          new[] { i32, u32, str, u32 }, new[] { 4, 1, 1, 1 });
            try
            {
                HudEventFields.Layout l = HudEventFields.Walk(mixed, 4096, false);
                T.Check("hud fields: an array counts as its whole length and a string stops the walk",
                        l.Ok && l.Fields["Tag"].Offset == 16 && !l.Fields.ContainsKey("AfterName"),
                        l.Ok ? l.Fields["Tag"].Offset + ", fields " + l.Fields.Count : "not read");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(mixed); }
        }

        // Кадры, собранные DWM: чей кадр, говорит только токен PresentHistory. Событие Flip в этом режиме выдаёт
        // dwm.exe, и без токена все кадры всех программ достались бы ему одному.
        private static void ComposedFrames()
        {
            HudFlipTracker t = new HudFlipTracker();
            for (int i = 1; i <= 30; i++)
            {
                t.Queue(0x1000UL, 700);                 // тот же токен по кругу — так их и выдаёт Windows
                t.Retire(0x1000UL, i * 1000L);
            }
            long[] shown = t.Pick(700);
            T.Check("hud screen: a retired present-history token is a frame on the display", shown != null && shown.Length == 30,
                    shown == null ? "none" : shown.Length.ToString());
            T.Eq("hud screen: a process that queued nothing gets no frames", null, t.Pick(701));

            // Один и тот же адрес токена достаётся разным программам по очереди — кадры не должны перепутаться.
            HudFlipTracker share = new HudFlipTracker();
            for (int i = 1; i <= 10; i++)
            {
                share.Queue(0x2000UL, 800); share.Retire(0x2000UL, i * 2000L);
                share.Queue(0x2000UL, 801); share.Retire(0x2000UL, i * 2000L + 1000L);
            }
            T.Check("hud screen: a recycled token follows whoever queued it last",
                    share.Pick(800) != null && share.Pick(800).Length == 10 && share.Pick(801) != null && share.Pick(801).Length == 10,
                    (share.Pick(800) == null ? "0" : share.Pick(800).Length.ToString()) + "/" +
                    (share.Pick(801) == null ? "0" : share.Pick(801).Length.ToString()));

            // Токен отрабатывает один раз: повторное событие не должно добавить кадр из ниоткуда.
            HudFlipTracker once = new HudFlipTracker();
            once.Queue(0x3000UL, 900); once.Retire(0x3000UL, 1000L);
            once.Retire(0x3000UL, 2000L);
            once.Queue(0x3000UL, 900); once.Retire(0x3000UL, 3000L);
            long[] twice = once.Pick(900);
            T.Check("hud screen: a token retired twice counts one frame", twice != null && twice.Length == 2,
                    twice == null ? "none" : twice.Length.ToString());

            T.Eq("hud screen: a token nobody queued is not attributed to anyone", null, Orphan());

            // Две цепочки у одного процесса не складываются: кадр посчитался бы дважды.
            HudFlipTracker both = new HudFlipTracker();
            both.Owner(0, 950);
            for (int i = 1; i <= 20; i++)
            {
                both.Submit(0, (ulong)i, i * 1000L);
                both.Displayed((ulong)i << 32, i * 1000L);
                both.Queue(0x4000UL, 950);
                both.Retire(0x4000UL, i * 1000L + 500L);
            }
            long[] mixed = both.Pick(950);
            T.Check("hud screen: the two chains are not summed into one stream", mixed != null && mixed.Length == 20,
                    mixed == null ? "none" : mixed.Length.ToString());
        }

        private static long[] Orphan()
        {
            HudFlipTracker t = new HudFlipTracker();
            t.Retire(0x5000UL, 1000L);
            t.Retire(0x5000UL, 2000L);
            return t.Pick(1000);
        }

        private static void ScreenFrames()
        {
            EventFieldOffsets();
            ComposedFrames();
            // Цепочка: Flip говорит, чей это вывод, MMIOFlip даёт номер отправки, VSyncDPC — что номер отработал.
            HudFlipTracker high = new HudFlipTracker();
            high.Owner(0, 500);
            for (int i = 1; i <= 40; i++)
            {
                high.Submit(0, (ulong)i, i * 60000L);
                high.Displayed((ulong)i << 32, i * 60000L + 10000L);      // номер в старшей половине FlipFenceId
            }
            long[] shown = high.Pick(500);
            T.Check("hud screen: a flip matched at the vsync becomes a frame on screen", shown != null && shown.Length == 40,
                    shown == null ? "none" : shown.Length.ToString());
            T.Eq("hud screen: frames of a process nobody flipped are not invented", null, high.Pick(501));

            // Без события Flip владелец вывода неизвестен — приписывать кадры наугад нельзя.
            HudFlipTracker orphan = new HudFlipTracker();
            orphan.Submit(0, 1, 1000);
            orphan.Displayed(1UL << 32, 2000);
            T.Eq("hud screen: flips on a display nobody claimed are not attributed", null, orphan.Pick(500));

            HudFlipTracker low = new HudFlipTracker();
            low.Owner(1, 600);
            for (int i = 1; i <= 40; i++)
            {
                low.Submit(1, (ulong)i, i * 60000L);
                low.Displayed((ulong)i, i * 60000L + 10000L);             // тот же номер, но в младшей половине
            }
            T.Check("hud screen: either half of FlipFenceId matches", low.Pick(600) != null && low.Pick(600).Length == 40);

            HudFlipTracker stray = new HudFlipTracker();
            stray.Owner(0, 700);
            stray.Submit(0, 5, 1000);
            stray.Displayed(0x1234567800000000UL | 0x99UL, 2000);         // ни одна половина не совпадает
            T.Eq("hud screen: a vsync for a flip nobody submitted adds nothing", null, stray.Pick(700));

            // Два разных экрана у двух программ: кадры не должны перемешаться.
            HudFlipTracker two = new HudFlipTracker();
            two.Owner(0, 900); two.Owner(1, 901);
            for (int i = 1; i <= 20; i++)
            {
                two.Submit(0, (ulong)(i * 2), i * 1000L);
                two.Displayed((ulong)(i * 2) << 32, i * 1000L);
                two.Submit(1, (ulong)(i * 2 + 1), i * 1000L);
                two.Displayed((ulong)(i * 2 + 1) << 32, i * 1000L);
            }
            T.Check("hud screen: two displays keep their own frames",
                    two.Pick(900) != null && two.Pick(900).Length == 20 && two.Pick(901) != null && two.Pick(901).Length == 20);

            // Метка вывода не может идти назад: иначе интервал между кадрами получился бы отрицательным.
            HudFlipTracker back = new HudFlipTracker();
            back.Owner(0, 800);
            back.Submit(0, 1, 1000); back.Displayed(1UL << 32, 5000);
            back.Submit(0, 2, 2000); back.Displayed(2UL << 32, 3000);
            back.Submit(0, 3, 3000); back.Displayed(3UL << 32, 9000);
            long[] order = back.Pick(800);
            T.Check("hud screen: screen times never go backwards", order != null && order.Length == 2 && order[0] == 5000 && order[1] == 9000,
                    order == null ? "none" : string.Join(",", Array.ConvertAll(order, delegate(long v) { return v.ToString(); })));

            // Строка появляется только когда есть что показать: без событий вывода её в кадре нет.
            HudFrame empty = new HudFrame();
            HudFpsSource.PutDisplayed(empty, new HudFlipTracker(), 500, 100000, 10000);
            T.Check("hud screen: no flip events means no row at all", empty.Get("fps.screen") == null && empty.Get("fps.screenms") == null);

            HudFrame full = new HudFrame();
            HudFlipTracker even = new HudFlipTracker();
            even.Owner(0, 500);
            for (int i = 1; i <= 100; i++) { even.Submit(0, (ulong)i, i * 1000L); even.Displayed((ulong)i << 32, i * 1000L); }
            HudFpsSource.PutDisplayed(full, even, 500, 100000, 10000);    // 10 000 тактов в секунду, кадр раз в 1000
            T.Check("hud screen: an even 10 fps stream reads as 10 fps and 100 ms",
                    full.Get("fps.screen") != null && Math.Abs(full.Get("fps.screen").Value - 10) < 0.5
                    && Math.Abs(full.Get("fps.screenms").Value - 100) < 1,
                    full.Get("fps.screen") == null ? "none" : full.Get("fps.screen").Value + " / " + full.Get("fps.screenms").Value);
        }

        // Две сессии реального времени на одних поставщиках глушат друг друга: живая остаётся «ok», но событий
        // не получает. Поэтому имя несёт номер процесса, и сессию мёртвого хозяина надо узнавать по имени.
        private static void TraceSessionNames()
        {
            string mine = HudEtwSession.DefaultName();
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            T.Check("hud etw: the session name carries this process id", HudEtwSession.OwnerPid(mine) == self,
                    mine + " -> " + HudEtwSession.OwnerPid(mine));
            T.Check("hud etw: two runs of the same role do not share a session name",
                    mine.IndexOf(' ' + self.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal) > 0, mine);
            T.Eq("hud etw: somebody else's session is left alone", 0, HudEtwSession.OwnerPid("Circular Kernel Context Logger"));
            T.Eq("hud etw: an old name without a process id is not claimed", 0, HudEtwSession.OwnerPid("SysDeck Frames (HUD)"));
            T.Check("hud etw: a session of a dead process is abandoned", !HudEtwSession.OwnerAlive(0x7FFFFFF0));
            T.Check("hud etw: our own live session is kept", HudEtwSession.OwnerAlive(self) == IsSysDeckProcess(self));
        }

        private static bool IsSysDeckProcess(int pid)
        {
            try { return string.Equals(System.Diagnostics.Process.GetProcessById(pid).ProcessName, "SysDeck", StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void LagRecorder()
        {
            TraceSessionNames();
            // Метка события у сессии реального времени должна остаться QPC: без «сырой» метки ETW отдаёт FILETIME,
            // и запись лагов писала в отчёт время, отсчитанное от 1601 года, вместо секунд от начала записи.
            T.Check("hud lag: the real-time trace keeps raw QPC timestamps", (HudEtwSession.LogfileMode(true) & 0x1000) != 0,
                    "0x" + HudEtwSession.LogfileMode(true).ToString("x"));

            List<float> ms = new List<float>();
            List<double> t = new List<double>();
            double at = 0;
            for (int i = 0; i < 6000; i++)
            {
                float v = i % 1000 == 500 ? 120f : 8.33f;
                at += v / 1000.0;
                ms.Add(v); t.Add(at);
            }
            List<int> pids = new List<int>();
            for (int i = 0; i < ms.Count; i++) pids.Add(7);
            HudLagReport.Summary s = HudLagReport.Analyze(ms, t, pids, 7);
            T.Eq("hud lag: six 120 ms frames among 8 ms ones are six hitches", 6, s.Hitches.Count);
            T.Check("hud lag: average FPS and 1 % low", s.AvgFps > 100 && s.Low1 < s.AvgFps, s.AvgFps + " / " + s.Low1);
            // Шесть длинных кадров из шести тысяч тянут «худший 1 %» вдвое вниз, а кадр на месте 99 % остаётся
            // ровным: в отчёте должны стоять оба числа, иначе сводка читается как просадка на каждой сотне кадров.
            T.Eq("hud lag: the 1 % low averages 60 frames out of 6000", 60, s.Low1Frames);
            T.Eq("hud lag: the 0.1 % low averages 6 frames", 6, s.Low01Frames);
            T.Check("hud lag: six long frames halve the 1 % low but not the 99th percentile",
                    s.Low1 < 60 && s.P99Fps > 110 && s.P999Fps > 110, s.Low1 + " / " + s.P99Fps + " / " + s.P999Fps);

            // Чужой процесс в тех же данных: свёрнутая программа с кадром раз в секунду. Её кадры не должны трогать
            // ни средний FPS, ни «худший 1 %», ни список фризов — иначе отчёт об игре описывает не игру.
            List<float> mixed = new List<float>(ms);
            List<double> mixedT = new List<double>(t);
            List<int> mixedPid = new List<int>(pids);
            for (int i = 0; i < 60; i++) { mixed.Add(1000f); mixedT.Add(i); mixedPid.Add(9); }
            HudLagReport.Summary only = HudLagReport.Analyze(mixed, mixedT, mixedPid, 7);
            T.Eq("hud lag: frames of another process stay out of the summary", s.Frames, only.Frames);
            T.Check("hud lag: another process cannot drag the average and the 1 % low down",
                    Math.Abs(only.AvgFps - s.AvgFps) < 0.01 && Math.Abs(only.Low1 - s.Low1) < 0.01 && only.Hitches.Count == s.Hitches.Count,
                    only.AvgFps + " / " + only.Low1 + " / " + only.Hitches.Count);
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
                // Метки — по тем же часам, что и у записи: источник кадров отдаёт кольцо в QPC.
                long f = Stopwatch.Frequency;
                long[] old = new long[300];
                old[0] = rec.StartQpc - f * 120;                      // кольцо источника: две минуты до нажатия кнопки
                for (int i = 1; i < old.Length; i++) old[i] = old[i - 1] + f / 120;
                rec.AddFrames(old, 42, f);
                T.Eq("hud lag: frames presented before the recording started are not recorded", 0, rec.FrameMs.Count);

                long[] frames = new long[200];
                frames[0] = rec.StartQpc + f / 100;
                for (int i = 1; i < frames.Length; i++) frames[i] = frames[i - 1] + (i == 150 ? f * 120 / 1000 : f * 8333 / 1000000);
                rec.AddFrames(frames, 42, f);
                rec.AddFrames(frames, 42, f);                         // тот же буфер ещё раз — дубли не пишутся
                T.Eq("hud lag: frames already recorded are not added twice", frames.Length - 1, rec.FrameMs.Count);
                // Кадры другого процесса между замерами не должны сбрасывать курсор первого: иначе его кольцо
                // перезапишется целиком и в отчёте будет вдвое больше кадров, чем игра показала.
                long[] other = new long[10];
                other[0] = rec.StartQpc + f / 100;
                for (int i = 1; i < other.Length; i++) other[i] = other[i - 1] + f;
                rec.AddFrames(other, 77, f);
                int before = rec.FrameMs.Count;
                rec.AddFrames(frames, 42, f);
                T.Eq("hud lag: a switch to another process does not re-record the first one", before, rec.FrameMs.Count);
                T.Check("hud lag: t_s is counted from the start of the recording",
                        rec.FrameT.Count > 0 && rec.FrameT[0] >= 0 && rec.FrameT[rec.FrameT.Count - 1] < 300,
                        rec.FrameT.Count > 0 ? rec.FrameT[0] + ".." + rec.FrameT[rec.FrameT.Count - 1] : "none");
                rec.TargetPid = 42;
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
                T.Check("hud lag: the report says the lows are tail averages and gives the percentiles",
                        md.Contains("tail averages, not percentiles") && md.Contains("99th percentile frame") && md.Contains("99.9th percentile"),
                        md.Length.ToString());
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
