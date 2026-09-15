// SysDeck — область «hud»: оверлей показателей. Настройка строк и перевод старого набора, текст
// значений, разбор общей памяти Afterburner и HWiNFO (синтетические буферы того же формата + живой Afterburner, если
// он запущен), SMBIOS этой машины, счётчики PDH по ядрам, интервалы строк и графики, отрисовка и настоящее окно.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck.Tests
{
    internal static partial class HudTests
    {
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();

        // Карта NVIDIA есть, а служба драйвера остановлена — NVML отвечает «драйвер не загружен» (9). Температура и
        // частота тогда приходят из HWiNFO, P-state — только из NVML: это состояние машины, а не ошибка сборщика.
        private static bool NvmlWorks()
        {
            try
            {
                if (nvmlInit_v2() != 0) return false;
                nvmlShutdown();
                return true;
            }
            catch (DllNotFoundException) { return false; }
        }

        internal static void Run()
        {
            Items();
            SettingsRoundTrip();
            Formatting();
            Mahm();
            Hwinfo();
            SmbiosParsing();
            PdhHelpers();
            BoardIntervals();
            Rendering();
            LiveCollector();
            LiveWindow();
            HostIni();
            HostTask();
            HostIpc();
            AfterburnerCfg();
            FrameMath();
            FrameTracker();
            FrameEvents();
            FrameSourceRows();
            FrameLiveSession();
            GameMetrics();
            HelpTexts();
            HudEtalonTests.Run();
        }

        // Группа «Игра»: процесс активного окна с потомками. Разбор и скорости — на готовых числах, сбор — живой, по
        // собственному процессу тестов теми же вызовами ОС.
        private static void GameMetrics()
        {
            int level;
            T.Check("hud game: bytes under a gigabyte in MB", Text(HudKind.Bytes, 512L * 1048576L, double.NaN, out level).StartsWith("512 "));
            T.Check("hud game: bytes over a gigabyte in GB with two decimals", Text(HudKind.Bytes, 3.5 * 1073741824.0, double.NaN, out level).StartsWith("3.50 "));

            Dictionary<int, HudProcTree.Entry> snap = new Dictionary<int, HudProcTree.Entry>();
            snap[10] = new HudProcTree.Entry { Parent = 1, Threads = 30, Exe = "Game.exe" };
            snap[11] = new HudProcTree.Entry { Parent = 10, Threads = 5, Exe = "CrashHandler.exe" };
            snap[13] = new HudProcTree.Entry { Parent = 11, Threads = 2, Exe = "Helper.exe" };
            snap[12] = new HudProcTree.Entry { Parent = 1, Threads = 99, Exe = "Other.exe" };
            HudFamily fam = HudProcTree.Family(10, snap);
            T.Check("hud game: family = window process and its descendants only",
                    fam.Pids.Count == 3 && fam.Pids.Contains(10) && fam.Pids.Contains(11) && fam.Pids.Contains(13) && !fam.Pids.Contains(12));
            T.Eq("hud game: family name from the root exe", "Game", fam.Name);
            T.Eq("hud game: threads summed over the family", 37, fam.Threads);

            double cpu, read, write;
            // pid, cpu ticks, read, write. 4 ядра, 1 с: 1 с процессорного времени = 25 %.
            bool ok = HudAppSource.Rates(new long[] { 10, 0, 0, 0, 11, 5000000, 100, 0 },
                                         new long[] { 10, TimeSpan.TicksPerSecond, 1048576, 2048, 20, 999999999, 999999, 999999 },
                                         1.0, 4, out cpu, out read, out write);
            T.Check("hud game: CPU share of all cores", ok && Math.Abs(cpu - 25) < 0.01, cpu.ToString());
            T.Check("hud game: read/write per second; a new child adds no jump and an exited one no minus",
                    Math.Abs(read - 1048576) < 1 && Math.Abs(write - 2048) < 1, read + " / " + write);
            T.Check("hud game: no common process — no rate", !HudAppSource.Rates(new long[] { 1, 0, 0, 0 }, new long[] { 2, 5, 5, 5 }, 1.0, 4, out cpu, out read, out write));

            string luid = Native.LuidKey(0, 0xC5C2);
            HashSet<int> pids = new HashSet<int> { 10, 11 };
            Dictionary<string, double> mem = new Dictionary<string, double>();
            mem["pid_10_luid_0x00000000_0x0000C5C2_phys_0"] = 100;
            mem["pid_11_luid_0x00000000_0x0000C5C2_phys_0"] = 50;
            mem["pid_12_luid_0x00000000_0x0000C5C2_phys_0"] = 999;
            mem["pid_10_luid_0x00000000_0x0000AAAA_phys_0"] = 777;
            T.Eq("hud game: video memory of the family on the main card", 150L, HudPdhSource.ProcessGpuMemory(mem, luid, pids));
            T.Eq("hud game: no family process on the card — no data", -1L, HudPdhSource.ProcessGpuMemory(mem, luid, new HashSet<int> { 99 }));
            Dictionary<string, double> eng = new Dictionary<string, double>();
            eng["pid_10_luid_0x00000000_0x0000C5C2_phys_0_eng_0_engtype_3D"] = 30;
            eng["pid_11_luid_0x00000000_0x0000C5C2_phys_0_eng_0_engtype_3D"] = 20;
            eng["pid_12_luid_0x00000000_0x0000C5C2_phys_0_eng_0_engtype_3D"] = 40;
            eng["pid_10_luid_0x00000000_0x0000C5C2_phys_0_eng_1_engtype_VideoDecode"] = 10;
            T.Check("hud game: GPU load of the family = busiest engine type of its processes", Math.Abs(HudPdhSource.GpuLoad(eng, luid, pids) - 50) < 0.01);
            T.Check("hud game: whole-card load still counts everyone", Math.Abs(HudPdhSource.GpuLoad(eng, luid) - 90) < 0.01);

            // Живой сбор по процессу тестов.
            int self = System.Diagnostics.Process.GetCurrentProcess().Id;
            HudFamily me = HudProcTree.Family(self, HudProcTree.Snapshot());
            T.Check("hud game live: own process found in the snapshot", me.Pids.Contains(self) && !string.IsNullOrEmpty(me.Name) && me.Threads > 0, me.Name);
            HudAppSource src = new HudAppSource();
            HudFrame f1 = new HudFrame();
            src.Collect(f1, me);
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            double burn = 0;
            while (sw.ElapsedMilliseconds < 400) burn += Math.Sqrt(sw.ElapsedTicks);
            HudFrame f2 = new HudFrame();
            src.Collect(f2, me);
            HudValue ram = f2.Get("app.ram"), priv = f2.Get("app.private"), cpuV = f2.Get("app.cpu"), handles = f2.Get("app.handles");
            T.Check("hud game live: working set of this process", ram != null && ram.Value > 10 * 1048576.0, ram == null ? "null" : ram.Value.ToString());
            T.Check("hud game live: private bytes of this process", priv != null && priv.Value > 1048576.0, priv == null ? "null" : priv.Value.ToString());
            T.Check("hud game live: CPU after a busy loop is above zero", cpuV != null && cpuV.Value > 0 && cpuV.Value <= 100, cpuV == null ? "null" : cpuV.Value + " (" + burn + ")");
            T.Check("hud game live: handles counted", handles != null && handles.Value > 10);
            T.Check("hud game live: process name and uptime rows", f2.Get("app.name") != null && f2.Get("app.uptime") != null);
            T.Check("hud game: no rate on the first sample", f1.Get("app.cpu") == null);
        }

        private static void HelpTexts()
        {
            List<string> missing = new List<string>();
            foreach (HudDef d in HudCatalog.BuiltIn)
                if (string.IsNullOrEmpty(HudHelp.ForId(d.Id))) missing.Add(d.Id);
            T.Check("hud help: every built-in metric has an explanation", missing.Count == 0, string.Join(", ", missing.ToArray()));
            T.Check("hud help: per-core rows explained", HudHelp.ForId(HudCatalog.CoreId(3, "mhz")).Length > 0 && HudHelp.ForId(HudCatalog.CoreId(0, "load")).Length > 0);
            List<string> groups = new List<string>();
            foreach (string g in HudGroups.Order)
                if (g != HudGroups.Other && HudHelp.ForGroup(g) == HudHelp.ForGroup("unknown-group")) groups.Add(g);
            T.Check("hud help: every group has its own explanation", groups.Count == 0, string.Join(", ", groups.ToArray()));
            T.Check("hud default: the basic set parses into known metrics", AllKnown(HudItem.ParseList(HudItem.Default)));
        }

        private static bool AllKnown(List<HudItem> items)
        {
            if (items.Count < 5) return false;
            foreach (HudItem it in items) if (HudCatalog.Find(it.Id) == null) return false;
            return true;
        }

        private static long[] Frames(double fps, double seconds, long freq, long start)
        {
            int n = (int)(fps * seconds);
            long[] ts = new long[n];
            for (int i = 0; i < n; i++) ts[i] = start + (long)(i * freq / fps);
            return ts;
        }

        // FPS, время кадра, худшие кадры и фризы по меткам — те же формулы, что у столбика.
        private static void FrameMath()
        {
            long freq = 1000000;
            long[] ts = Frames(100, 20, freq, 5 * freq);
            long now = ts[ts.Length - 1];
            HudFpsResult r = HudFrameMath.Compute(ts, ts.Length, now, freq);
            T.Check("hud fps: steady 100 fps counts 100", Math.Abs(r.Fps - 100) <= 1, r.Fps.ToString());
            T.Check("hud fps: frame time is 10 ms", Math.Abs(r.FrameMs - 10) < 0.2, r.FrameMs.ToString());
            T.Check("hud fps: 1% low of a steady run equals the average", Math.Abs(r.Low1 - 100) < 1, r.Low1.ToString());
            T.Check("hud fps: no stutters in a steady run", r.StuttersPerMin == 0, r.StuttersPerMin.ToString());
            T.Check("hud fps: series is capped and in ms", r.Series.Length == HudFrameMath.SeriesMax && Math.Abs(r.Series[0] - 10) < 0.2);

            // Три фриза по 100 мс внутри 60 fps: 1 % худших падает, фризы видны, 0,1 % — самый долгий кадр.
            List<long> list = new List<long>();
            long t = 0;
            for (int i = 0; i < 1200; i++)
            {
                t += (i == 300 || i == 600 || i == 900) ? freq / 10 : freq / 60;
                list.Add(t);
            }
            long[] st = list.ToArray();
            HudFpsResult s = HudFrameMath.Compute(st, st.Length, t, freq);
            T.Check("hud fps: three 100 ms hitches are three stutters", s.StuttersPerMin > 2.9, s.StuttersPerMin.ToString());
            T.Check("hud fps: 0.1% low reflects the worst frame (10 fps)", Math.Abs(s.Low01 - 10) < 0.5, s.Low01.ToString());
            T.Check("hud fps: 1% low is below the average", s.Low1 < 50, s.Low1.ToString());
            T.Check("hud fps: series shows the spike", Array.Exists(s.Series, delegate(double x) { return x > 90; }) || st.Length - 900 > HudFrameMath.SeriesMax);

            HudFpsResult idle = HudFrameMath.Compute(ts, ts.Length, now + 5 * freq, freq);
            T.Check("hud fps: presents stopped 5 s ago = 0 fps, not the old value", idle.Fps == 0, idle.Fps.ToString());
            HudFpsResult gone = HudFrameMath.Compute(ts, ts.Length, now + 40 * freq, freq);
            T.Check("hud fps: silent for 40 s = no value", double.IsNaN(gone.Fps));
            long[] fresh = Frames(50, 0.5, freq, 0);
            HudFpsResult part = HudFrameMath.Compute(fresh, fresh.Length, fresh[fresh.Length - 1], freq);
            T.Check("hud fps: half a second of 50 fps is 50, not 25", Math.Abs(part.Fps - 50) < 1, part.Fps.ToString());
            T.Check("hud fps: empty input gives no value", double.IsNaN(HudFrameMath.Compute(new long[0], 0, 0, freq).Fps));
        }

        // Цепочки кадров: самая частая цепочка DXGI, ядро — только когда DXGI молчит, чужой процесс не смешивается.
        private static void FrameTracker()
        {
            long freq = 1000;
            HudPresentTracker tr = new HudPresentTracker();
            for (long ms = 0; ms < 2000; ms += 10) tr.Present(10, HudPresentTracker.UserLayer, 0xA, ms);     // 100 fps
            for (long ms = 0; ms < 2000; ms += 50) tr.Present(10, HudPresentTracker.UserLayer, 0xB, ms);     // 20 fps
            for (long ms = 0; ms < 2000; ms += 5) tr.Present(10, HudPresentTracker.KernelLayer, 184, ms);    // ядро 200
            for (long ms = 0; ms < 2000; ms += 20) tr.Present(11, HudPresentTracker.KernelLayer, 184, ms);
            long[] a = tr.Pick(10, 1990, freq, 0);
            T.Check("hud fps tracker: busiest DXGI chain wins over kernel events", a != null && a.Length == 200, a == null ? "null" : a.Length.ToString());
            long[] b = tr.Pick(11, 1990, freq, 0);
            T.Check("hud fps tracker: kernel chain used when a process has no DXGI", b != null && b.Length == 100);
            T.Check("hud fps tracker: unknown process has no frames", tr.Pick(12, 1990, freq, 0) == null);
            tr.Present(10, HudPresentTracker.UserLayer, 0xA, 1985);
            long[] c = tr.Pick(10, 1990, freq, 0);
            T.Check("hud fps tracker: a late out-of-order stamp keeps the chain ascending", c[c.Length - 1] >= c[c.Length - 2]);
            tr.Pick(10, 100000, freq, 50000);
            T.Check("hud fps tracker: silent chains are dropped", tr.SeriesCount == 0, tr.SeriesCount.ToString());
        }

        private static IntPtr Record(Guid provider, ushort id, int pid, long ts, byte[] payload, byte flags)
        {
            IntPtr rec = Marshal.AllocHGlobal(HudPresentEvents.RecordSize + 64);
            Marshal.Copy(new byte[HudPresentEvents.RecordSize + 64], 0, rec, HudPresentEvents.RecordSize + 64);
            Marshal.WriteByte(rec, HudPresentEvents.OffFlags, flags);
            Marshal.WriteInt32(rec, HudPresentEvents.OffPid, pid);
            Marshal.WriteInt64(rec, HudPresentEvents.OffTime, ts);
            Marshal.Copy(provider.ToByteArray(), 0, new IntPtr(rec.ToInt64() + HudPresentEvents.OffProvider), 16);
            Marshal.WriteInt16(rec, HudPresentEvents.OffId, unchecked((short)id));
            Marshal.WriteInt16(rec, HudPresentEvents.OffUserDataLength, (short)payload.Length);
            IntPtr data = new IntPtr(rec.ToInt64() + HudPresentEvents.RecordSize);
            Marshal.Copy(payload, 0, data, payload.Length);
            Marshal.WriteIntPtr(rec, HudPresentEvents.OffUserData, data);
            return rec;
        }

        private static byte[] DxgiPayload(ulong swap, uint flags, bool is32)
        {
            List<byte> b = new List<byte>();
            b.AddRange(is32 ? BitConverter.GetBytes((uint)swap) : BitConverter.GetBytes(swap));
            b.AddRange(BitConverter.GetBytes(flags));
            b.AddRange(BitConverter.GetBytes(1));
            return b.ToArray();
        }

        // Разбор записи ETW — на буфере той же раскладки, что даёт ProcessTrace в 64-битном процессе.
        private static void FrameEvents()
        {
            if (IntPtr.Size != 8) { T.Skip("hud fps events", "32-bit test process"); return; }
            int pid, layer; ulong key; long qpc;
            IntPtr r1 = Record(HudPresentEvents.Dxgi, 42, 4321, 777, DxgiPayload(0x1122334455667788UL, 0, false), 0x40);
            bool ok = HudPresentEvents.Decode(r1, out pid, out layer, out key, out qpc);
            T.Check("hud fps events: DXGI Present_Start is a frame of its swap chain",
                    ok && pid == 4321 && layer == HudPresentTracker.UserLayer && key == 0x1122334455667788UL && qpc == 777);
            IntPtr r2 = Record(HudPresentEvents.Dxgi, 42, 1, 1, DxgiPayload(5, 1, false), 0x40);
            T.Check("hud fps events: DXGI_PRESENT_TEST is not a frame", !HudPresentEvents.Decode(r2, out pid, out layer, out key, out qpc));
            IntPtr r3 = Record(HudPresentEvents.Dxgi, 42, 1, 1, DxgiPayload(0xABCD, 0, true), 0x20);
            T.Check("hud fps events: 32-bit game (4-byte pointer) decodes", HudPresentEvents.Decode(r3, out pid, out layer, out key, out qpc) && key == 0xABCD);
            IntPtr r4 = Record(HudPresentEvents.D3d9, 1, 9, 2, BitConverter.GetBytes(0x99UL), 0x40);
            T.Check("hud fps events: D3D9 Present_Start decodes", HudPresentEvents.Decode(r4, out pid, out layer, out key, out qpc) && key == 0x99 && pid == 9);
            IntPtr r5 = Record(HudPresentEvents.DxgKrnl, 215, 9, 2, new byte[0], 0x40);
            T.Check("hud fps events: DxgKrnl PresentHistoryDetailed is a kernel chain",
                    HudPresentEvents.Decode(r5, out pid, out layer, out key, out qpc) && layer == HudPresentTracker.KernelLayer && key == 215);
            IntPtr r6 = Record(HudPresentEvents.Dxgi, 43, 9, 2, DxgiPayload(1, 0, false), 0x40);
            T.Check("hud fps events: Present_Stop is not counted", !HudPresentEvents.Decode(r6, out pid, out layer, out key, out qpc));
            IntPtr r7 = Record(HudPresentEvents.D3d9, 42, 9, 2, DxgiPayload(1, 0, false), 0x40);
            T.Check("hud fps events: id 42 of another provider is not DXGI", !HudPresentEvents.Decode(r7, out pid, out layer, out key, out qpc));
            IntPtr r8 = Record(HudPresentEvents.Dxgi, 42, 9, 2, new byte[3], 0x40);
            T.Check("hud fps events: truncated payload is ignored", !HudPresentEvents.Decode(r8, out pid, out layer, out key, out qpc));
            foreach (IntPtr p in new[] { r1, r2, r3, r4, r5, r6, r7, r8 }) Marshal.FreeHGlobal(p);
        }

        // Строки «Кадры» из источника и «пила» в графике столбика.
        private static void FrameSourceRows()
        {
            long freq = 1000000;
            HudPresentTracker tr = new HudPresentTracker();
            foreach (long t in Frames(144, 5, freq, freq)) tr.Present(77, HudPresentTracker.UserLayer, 1, t);
            HudFrame f = new HudFrame();
            using (HudFpsSource src = new HudFpsSource())
                src.Put(f, tr, 77, tr.Latest + freq / 2, freq);
            T.Check("hud fps source: fps / frame time / lows / stutters / app are all put",
                    f.Has("fps") && f.Has("fps.frametime") && f.Has("fps.low1") && f.Has("fps.low01") && f.Has("fps.stutter") && f.Has("fps.app"));
            T.Check("hud fps source: fps uses the newest event as now (ETW delivers late)", Math.Abs(f.Get("fps").Value - 144) <= 2, f.Get("fps").Value.ToString());
            HudFrame none = new HudFrame();
            using (HudFpsSource src = new HudFpsSource()) src.Put(none, tr, 78, tr.Latest, freq);
            T.Check("hud fps source: foreground without presents puts nothing", !none.Has("fps"));

            // Окно браузера (pid 500) кадров не выводит, их выводит GPU-процесс 501 (потомок); чужой 900 не в счёт.
            HudPresentTracker tb = new HudPresentTracker();
            foreach (long t in Frames(60, 3, freq, freq)) tb.Present(501, HudPresentTracker.UserLayer, 1, t);
            foreach (long t in Frames(240, 3, freq, freq)) tb.Present(900, HudPresentTracker.UserLayer, 1, t);
            Dictionary<int, int> tree = new Dictionary<int, int>();
            tree[501] = 500; tree[500] = 4; tree[900] = 4;
            HudFrame fb = new HudFrame();
            using (HudFpsSource src = new HudFpsSource()) src.Put(fb, tb, 500, tb.Latest, freq, tree);
            T.Check("hud fps source: a window without own presents counts its GPU child", fb.Has("fps") && Math.Abs(fb.Get("fps").Value - 60) <= 2,
                    fb.Has("fps") ? fb.Get("fps").Value.ToString() : "none");
            HudFrame fo = new HudFrame();
            using (HudFpsSource src = new HudFpsSource()) src.Put(fo, tb, 4, tb.Latest, freq);
            T.Check("hud fps source: no process tree means no borrowed frames", !fo.Has("fps"));
            Dictionary<int, int> chain = new Dictionary<int, int>();
            chain[3] = 2; chain[2] = 1; chain[7] = 7;
            T.Check("hud fps source: grandchild within four generations", HudFpsSource.IsDescendant(3, 1, chain));
            T.Check("hud fps source: a self-parented pid does not loop", !HudFpsSource.IsDescendant(7, 1, chain));

            HudBoard board = new HudBoard();
            List<HudItem> items = HudItem.ParseList("fps.frametime:3:0:0");
            board.Advance(f, items, DateTime.Now);
            List<HudRow> rows = board.Rows(items, 60);
            T.Check("hud fps board: frame time graph uses the per-frame series",
                    rows.Count == 1 && rows[0].Points != null && rows[0].Points.Length == HudFrameMath.SeriesMax && rows[0].Slots == HudFrameMath.SeriesMax);
            using (Bitmap bmp = HudRender.Draw(rows, 1f, 80))
                T.Check("hud fps board: frame time row renders", bmp.Width > 100 && bmp.Height > 20);
        }

        // Настоящая сессия ETW: с правами — идёт и закрывается; без прав (как в тестах) — понятное состояние, без исключений.
        private static void FrameLiveSession()
        {
            using (HudEtwSession s = new HudEtwSession("SysDeck Frames (tests)"))
            {
                bool started = s.Start();
                T.Check("hud fps live: state is known, not an exception",
                        started ? s.State == HudEtwSession.StateOk
                                : s.State == HudEtwSession.StateNoRights || s.State == HudEtwSession.StateRelogon,
                        s.State + " err " + s.Error);
                if (!started) T.Skip("hud fps live events", "no rights for a real-time ETW session (" + s.State + ")");
                else
                {
                    System.Threading.Thread.Sleep(2500);
                    T.Check("hud fps live: kernel present events arrive (DWM composes every refresh)", s.EventsSeen > 0, s.EventsSeen.ToString());
                }
            }
            // Читатель ETW на настоящих записях: журнал запуска Проводника есть в каждом профиле и читается без прав.
            // Заголовок записи = 80 байт, значит Size = 80 + UserDataLength — это ловит сдвиг смещений EVENT_RECORD.
            string etl = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                                @"Microsoft\Windows\Explorer\ExplorerStartupLog_RunOnce.etl");
            if (IntPtr.Size != 8 || !System.IO.File.Exists(etl)) T.Skip("hud fps etl", "no Explorer startup log");
            else
            {
                string copy = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wpc-tests-explorer-" + Guid.NewGuid().ToString("N") + ".etl");
                System.IO.File.Copy(etl, copy);
                int records = 0, sized = 0, mismatched = 0, withData = 0;
                string first = null;
                Guid header = new Guid("68fdd900-4a3e-11d1-84f4-0000f80464e3");
                int rc = HudEtwSession.ReadEtl(copy, delegate(IntPtr rec)
                {
                    records++;
                    byte[] g = new byte[16];
                    Marshal.Copy(new IntPtr(rec.ToInt64() + HudPresentEvents.OffProvider), g, 0, 16);
                    if (new Guid(g) == header) return;
                    int size = (ushort)Marshal.ReadInt16(rec, 0), len = (ushort)Marshal.ReadInt16(rec, HudPresentEvents.OffUserDataLength);
                    sized++;
                    if (len > 0 && Marshal.ReadIntPtr(rec, HudPresentEvents.OffUserData) != IntPtr.Zero) withData++;
                    if (size != 80 + len) { mismatched++; if (first == null) first = size + " vs 80+" + len; }
                });
                try { System.IO.File.Delete(copy); } catch { }
                T.Check("hud fps etl: real records are delivered to the callback", rc == 0 && records > 0 && sized > 0, "rc " + rc + ", " + records);
                T.Check("hud fps etl: user data length sits where the header size says", mismatched == 0 && withData > 0,
                        mismatched + " of " + sized + " mismatched, first " + first + ", with data " + withData);
            }
            T.Check("hud fps live: token check does not throw", HudPerfLog.InToken() || !HudPerfLog.InToken());
            string group = null;
            try { group = HudPerfLog.GroupName(); } catch (Exception ex) { group = null; T.Check("hud fps live: group name resolves", false, ex.Message); }
            T.Check("hud fps live: group S-1-5-32-559 has a local name", !string.IsNullOrEmpty(group), group);
        }

        // Настройки HWiNFO на время фона и возврат прежних: чужие ключи и секции не трогаются, отсутствовавшие ключи удаляются.
        private static void HostIni()
        {
            string ini = "[Settings]\r\nSensorsOnly=0\r\nTheme=2\r\nAutoUpdate=1\r\n[LogfileSettings]\r\nSensorsSM=0\r\n";
            Dictionary<string, string> orig = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string patched = HwinfoIni.Patch(ini, orig);
            T.Check("hud host: patch sets the wanted keys inside [Settings]",
                    patched.Contains("SensorsOnly=1") && patched.Contains("AutoUpdate=0") && patched.IndexOf("SensorsSM=1") < patched.IndexOf("[LogfileSettings]"), patched);
            T.Check("hud host: patch keeps foreign keys and sections", patched.Contains("Theme=2") && patched.Contains("[LogfileSettings]\r\nSensorsSM=0"), patched);
            string back = HwinfoIni.Restore(patched, HwinfoIni.Deserialize(HwinfoIni.Serialize(orig)));
            T.Check("hud host: restore gives the original file back", back == ini, back);
            Dictionary<string, string> none = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string created = HwinfoIni.Patch("", none);
            T.Check("hud host: missing INI gets a [Settings] section", created.StartsWith("[Settings]\r\n") && created.Contains("OpenSensors=1"), created);
            T.Check("hud host: keys that did not exist are removed on restore", HwinfoIni.Restore(created, none).Trim() == "[Settings]");

            string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            T.Check("hud host: Program Files path is trusted", HwinfoPaths.IsTrusted(System.IO.Path.Combine(pf, @"HWiNFO64\HWiNFO64.EXE")));
            T.Check("hud host: a path beside Program Files is not", !HwinfoPaths.IsTrusted(pf + @" Evil\HWiNFO64.EXE"));
            T.Check("hud host: traversal out of Program Files is not", !HwinfoPaths.IsTrusted(System.IO.Path.Combine(pf, @"..\Users\x\HWiNFO64.EXE")));
            T.Check("hud host: user profile is not", !HwinfoPaths.IsTrusted(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"HWiNFO64\HWiNFO64.EXE")));
        }

        private static void AfterburnerCfg()
        {
            string cfg = "[Startup]\r\nFormat=2\r\nPowerLimit=\r\nCoreClkBoost=\r\n[Defaults]\r\nFormat=2\r\nPowerLimit=100\r\n"
                       + "[Profile1]\r\nFormat=2\r\nPowerLimit=85\r\nCoreClkBoost=-502000\r\nMemClkBoost=800000\r\n"
                       + "[Profile2]\r\nFormat=2\r\nMemClkBoost=811000\r\nPowerLimit=85\r\nCoreClkBoost=-502000\r\n"
                       + "[Profile3]\r\nFormat=2\r\nCoreClkBoost=-502000\r\nPowerLimit=85\r\nMemClkBoost=811000\r\n[Profile5]\r\nFormat=2\r\nPowerLimit=\r\n";
            T.Check("afterburner: empty [Startup] reads as empty", Afterburner.IsEmpty(Afterburner.Section(cfg, "Startup")));
            T.Check("afterburner: a missing section is null", Afterburner.Section(cfg, "Profile4") == null);
            T.Check("afterburner: a slot with only Format and blanks is empty", Afterburner.IsEmpty(Afterburner.Section(cfg, "Profile5")));
            T.Check("afterburner: key order does not matter", Afterburner.SameValues(Afterburner.Section(cfg, "Profile2"), Afterburner.Section(cfg, "Profile3")));
            T.Check("afterburner: different memory offset differs", !Afterburner.SameValues(Afterburner.Section(cfg, "Profile1"), Afterburner.Section(cfg, "Profile2")));

            string next = Afterburner.CopyToStartup(cfg, 2);
            T.Check("afterburner: [Startup] takes the profile lines", next != null && next.StartsWith("[Startup]\r\nFormat=2\r\nMemClkBoost=811000\r\nPowerLimit=85\r\nCoreClkBoost=-502000\r\n[Defaults]", StringComparison.Ordinal), next);
            T.Check("afterburner: the rest of the file is untouched", next != null && next.Substring(next.IndexOf("[Defaults]", StringComparison.Ordinal)) == cfg.Substring(cfg.IndexOf("[Defaults]", StringComparison.Ordinal)));
            T.Check("afterburner: a missing slot changes nothing", Afterburner.CopyToStartup(cfg, 4) == null);
            string noStartup = "[Profile1]\nPowerLimit=90\n";
            T.Check("afterburner: a file without [Startup] gets one", Afterburner.CopyToStartup(noStartup, 1) == "[Startup]\nPowerLimit=90\n[Profile1]\nPowerLimit=90\n", Afterburner.CopyToStartup(noStartup, 1));

            AbGpu g = new AbGpu();
            for (int i = 1; i <= Afterburner.MaxSlot; i++) g.Slots[i] = Afterburner.Section(next, "Profile" + i);
            g.Startup = Afterburner.Section(next, "Startup");
            List<AbGpu> gpus = new List<AbGpu>(new[] { g });
            T.Eq("afterburner: equal slots 2 and 3 resolve to 2", 2, Afterburner.StartupSlotOf(gpus, new List<int>(new[] { 1, 2, 3 })));
            g.Startup = Afterburner.Section(cfg, "Startup");
            T.Eq("afterburner: empty [Startup] = no slot", 0, Afterburner.StartupSlotOf(gpus, new List<int>(new[] { 1, 2, 3 })));
            g.Startup = Afterburner.Section(cfg, "Defaults");
            T.Eq("afterburner: foreign [Startup] = -1", -1, Afterburner.StartupSlotOf(gpus, new List<int>(new[] { 1, 2, 3 })));
            T.Eq("afterburner: kHz offsets shown in MHz with sign", "\u2212" + "502", Afterburner.Signed("-502000", 1000));
            T.Eq("afterburner: positive offset gets a plus", "+800", Afterburner.Signed("800000", 1000));
            T.Eq("afterburner: the adapter name is VEN + DEV", "VEN_10DE DEV_2684", Afterburner.GpuName("VEN_10DE&DEV_2684&SUBSYS_40C01458&REV_A1&BUS_2&DEV_0&FN_0"));
            T.Check("afterburner: hud commands carry only slots 1..5", Array.IndexOf(HudIpc.Commands, "Afterburner5") >= 0 && Array.IndexOf(HudIpc.Commands, "Afterburner6") < 0);

            // Живая машина: только чтение.
            AbState live = Afterburner.Load();
            if (live.Exe == null) T.Skip("afterburner: live profiles", "MSI Afterburner is not installed");
            else T.Check("afterburner: live profiles read without an error", live.Error == null, live.Error);
        }

        private static void HostTask()
        {
            string exe = @"C:\Program Files\SysDeck\SysDeck.exe";
            System.Xml.XmlDocument doc = new System.Xml.XmlDocument();
            bool parsed = true;
            try { doc.LoadXml(HudLauncher.TaskXml(exe).Substring(HudLauncher.TaskXml(exe).IndexOf("<Task", StringComparison.Ordinal))); }
            catch (Exception ex) { parsed = false; T.Check("hud host: task xml parses", false, ex.Message); }
            if (!parsed) return;
            System.Xml.XmlNamespaceManager ns = new System.Xml.XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            Func<string, string> at = delegate(string xp) { System.Xml.XmlNode n = doc.SelectSingleNode(xp, ns); return n == null ? null : n.InnerText; };
            T.Check("hud host: task runs this exe with --hud", at("//t:Exec/t:Command") == exe && at("//t:Exec/t:Arguments") == HudMode.Switch);
            T.Check("hud host: task is highest-available, interactive, without time limit",
                    at("//t:RunLevel") == "HighestAvailable" && at("//t:LogonType") == "InteractiveToken" && at("//t:ExecutionTimeLimit") == "PT0S");
            T.Check("hud host: task has no triggers (the app starts it)", doc.SelectSingleNode("//t:Triggers", ns) == null);
        }

        // Настоящие именованные объекты ядра: событие, созданное с меткой «средняя», открывается и срабатывает по имени.
        private static void HostIpc()
        {
            // Событие ещё открыто у того, кто подаёт сигнал (окно, агент захвата), а оверлей перезапускается: повторное
            // создание должно открыть тот же объект, а не упасть с «Отказано в доступе» (живой случай 15.09).
            string name = @"Local\SysDeck.HudTest." + Guid.NewGuid().ToString("N");
            System.Threading.EventWaitHandle held;
            string reopen = null;
            using (System.Threading.EventWaitHandle ev1 = HudIpc.CreateNamedEvent(name))
                System.Threading.EventWaitHandle.TryOpenExisting(name, System.Security.AccessControl.EventWaitHandleRights.Modify
                    | System.Security.AccessControl.EventWaitHandleRights.Synchronize, out held);
            try
            {
                using (System.Threading.EventWaitHandle ev2 = HudIpc.CreateNamedEvent(name))
                {
                    held.Set();
                    if (!ev2.WaitOne(1000)) reopen = "no signal through the reopened event";
                }
            }
            catch (Exception ex) { reopen = ex.Message; }
            finally { if (held != null) held.Dispose(); }
            T.Check("hud host: a restarted overlay reopens an event still held by a signaller", reopen == null, reopen);

            if (HudIpc.IsRunning()) { T.Skip("hud host: ipc round trip", "an overlay process is running on this machine"); return; }
            bool first;
            using (Microsoft.Win32.SafeHandles.SafeWaitHandle m = HudIpc.CreateInstanceMutex(out first))
            using (System.Threading.EventWaitHandle ev = HudIpc.CreateEvent("Reload"))
            {
                T.Check("hud host: instance mutex is created and seen", m != null && first && HudIpc.IsRunning());
                bool again;
                using (Microsoft.Win32.SafeHandles.SafeWaitHandle second = HudIpc.CreateInstanceMutex(out again))
                    T.Check("hud host: second instance is not first", !again);
                T.Check("hud host: signal reaches the event", HudIpc.Signal("Reload") && ev.WaitOne(1000));
                T.Check("hud host: unknown command is refused", !HudIpc.Signal("Kill"));
            }
            T.Check("hud host: mutex gone after release", !HudIpc.IsRunning());
        }

        private static void Items()
        {
            List<HudItem> parsed = HudItem.ParseList(" cpu.load:3:2000:FF40C0FF ; bad id!:1:0:0; ram ; cpu.load:1:0:0; gpu.temp:0:10:0");
            T.Eq("hud: parse keeps valid ids once, in the given order", "cpu.load,ram,gpu.temp",
                 string.Join(",", parsed.ConvertAll(delegate(HudItem i) { return i.Id; }).ToArray()));
            T.Check("hud: flags 3 = text and graph", parsed[0].Text && parsed[0].Graph);
            T.Eq("hud: interval survives", 2000, parsed[0].IntervalMs);
            T.Eq("hud: colour is ARGB hex", unchecked((int)0xFF40C0FF), parsed[0].Color);
            T.Check("hud: bare id = text only, default interval", parsed[1].Text && !parsed[1].Graph && parsed[1].IntervalMs == 0);
            T.Check("hud: flags 0 falls back to text (a row that shows nothing is not a choice)", parsed[2].Text);
            T.Eq("hud: interval below 250 ms is raised", 250, parsed[2].IntervalMs);
            T.Eq("hud: format round-trips", "cpu.load:3:2000:FF40C0FF;ram:1:0:0;gpu.temp:1:250:0", HudItem.FormatList(parsed));
            T.Eq("hud: an empty list stays empty (all boxes unchecked)", 0, HudItem.ParseList("").Count);
            T.Eq("hud: legacy disk and net split into two rows each", "cpu.load,disk.read,disk.write,net.down,net.up,clock",
                 string.Join(",", HudCatalog.FromLegacy("Cpu, Disk,Net,bogus,Clock,Cpu").ToArray()));

            int core; string metric;
            T.Check("hud: core id parses", HudCatalog.TryCore("cpu.core.15.mhz", out core, out metric) && core == 15 && metric == "mhz");
            T.Check("hud: foreign core metric is rejected", !HudCatalog.TryCore("cpu.core.1.volts", out core, out metric));
            HudDef d = HudCatalog.Find("cpu.core.3.load");
            T.Check("hud: core rows are numbered from 1 and grouped", d != null && d.Group == HudGroups.Cores && d.Label.EndsWith("4"), d == null ? "null" : d.Label);
            HashSet<string> ids = new HashSet<string>();
            bool unique = true;
            foreach (HudDef x in HudCatalog.BuiltIn) if (!ids.Add(x.Id) || !HudItem.ValidId(x.Id)) unique = false;
            T.Check("hud: catalog ids are unique and valid", unique);
        }

        private static void SettingsRoundTrip()
        {
            CapSettings fresh = CapSettings.FromJson("{\"Version\":2}");
            T.Eq("hud: settings without hud fields get the default rows", HudItem.Default, fresh.HudItems);
            T.Eq("hud: default hotkey is Alt+R", 0x52u, fresh.Hotkey(CapAction.Hud).Vk);
            T.Check("hud: hidden, not elevated, HWiNFO helper on by default", !fresh.HudShown && !fresh.HudElevated && fresh.HudHwinfo);
            T.Eq("hud: default position is top right", HudCorner.TopRight, fresh.HudCorner);
            T.Eq("hud: a saved centre position survives", HudCorner.MiddleLeft, CapSettings.FromJson("{\"HudCorner\":\"MiddleLeft\"}").HudCorner);

            Rectangle area = new Rectangle(100, 50, 1000, 800);
            Size col = new Size(200, 100);
            T.Eq("hud place: top right", new Point(890, 60), HudLayout.Place(area, col, HudCorner.TopRight, 10));
            T.Eq("hud place: bottom left", new Point(110, 740), HudLayout.Place(area, col, HudCorner.BottomLeft, 10));
            T.Eq("hud place: middle left", new Point(110, 400), HudLayout.Place(area, col, HudCorner.MiddleLeft, 10));
            T.Eq("hud place: middle right", new Point(890, 400), HudLayout.Place(area, col, HudCorner.MiddleRight, 10));
            T.Eq("hud place: top center", new Point(500, 60), HudLayout.Place(area, col, HudCorner.TopCenter, 10));
            T.Eq("hud place: bottom center", new Point(500, 740), HudLayout.Place(area, col, HudCorner.BottomCenter, 10));
            T.Eq("hud place: a column taller than the area stays inside its top", 50, HudLayout.Place(area, new Size(200, 900), HudCorner.MiddleRight, 10).Y);

            T.Eq("cpu power: 99 is reduced", CpuPowerMode.Reduced, CpuPower.ModeOf(99));
            T.Eq("cpu power: 100 is max", CpuPowerMode.Max, CpuPower.ModeOf(100));
            T.Eq("cpu power: 98 matches no button", CpuPowerMode.None, CpuPower.ModeOf(98));
            T.Eq("cpu power: reduced writes 99", 99, CpuPower.PercentOf(CpuPowerMode.Reduced));
            CpuPowerState live = CpuPower.Read();   // только чтение — схему питания машины тесты не меняют
            T.Check("cpu power: the active scheme is readable without admin (" + live.Error + ")", live.Ok && live.Ac >= 0 && live.Ac <= 100);

            CapSettings old = CapSettings.FromJson("{\"HudMetrics\":\"Net,Cpu,GpuPower\"}");
            T.Eq("hud: old metric set is migrated to rows", "net.down:1:0:0;net.up:1:0:0;cpu.load:1:0:0;gpu.power:1:0:0", old.HudItems);
            T.Eq("hud: an old explicitly empty set stays empty", "", CapSettings.FromJson("{\"HudMetrics\":\"\"}").HudItems);

            CapSettings s = new CapSettings();
            s.HudItems = "cpu.core.0.mhz:2:1000:0;hw.f000.0.1000000:3:500:FFFF0000";
            s.HudCorner = HudCorner.BottomRight;
            s.HudMonitor = 2;
            s.HudOpacity = 5;
            s.HudGraphSeconds = 5000;
            s.HudScale = 150;
            s.HudElevated = true;
            s.HudHwinfo = false;
            s.HudInCaptures = true;
            s.HudShown = true;
            CapSettings back = CapSettings.FromJson(s.ToJson());
            T.Eq("hud: rows survive a save", s.HudItems, back.HudItems);
            T.Eq("hud: corner survives a save", HudCorner.BottomRight, back.HudCorner);
            T.Eq("hud: opacity is clamped to 20 %", 20, back.HudOpacity);
            T.Eq("hud: graph window is clamped to 600 s", 600, back.HudGraphSeconds);
            T.Eq("hud: scale survives", 150, back.HudScale);
            T.Check("hud: elevated / HWiNFO / in-captures / shown survive a save",
                    back.HudElevated && !back.HudHwinfo && back.HudInCaptures && back.HudShown);
            T.Eq("hud: new rows win over a stale legacy field", "ram:1:0:0",
                 CapSettings.FromJson("{\"HudMetrics\":\"Cpu\",\"HudItems\":\"ram:1:0:0\"}").HudItems);
        }

        private static string Text(HudKind kind, double value, double total, out int level)
        {
            HudValue v = new HudValue("x", kind, value);
            v.Total = total;
            return HudFormat.Text(v, out level);
        }

        private static void Formatting()
        {
            int level;
            T.Eq("hud: percent rounds", "92%", Text(HudKind.Percent, 92.4, 0, out level));
            T.Eq("hud: 92 % is critical", 2, level);
            T.Eq("hud: processor utility over 100 % is shown as 100 %", "100%", Text(HudKind.Percent, 131, 0, out level));
            T.Eq("hud: temperature", "65°C", Text(HudKind.Temp, 64.6, 0, out level));
            T.Eq("hud: 80 °C is high", 1, Text(HudKind.Temp, 80, 0, out level) == "80°C" ? level : -1);
            string mem = Text(HudKind.Memory, 30L * 1073741824L, 32L * 1073741824L, out level);
            T.Check("hud: memory used / total in GB, critical at 94 %", mem.StartsWith("30.0 / 32.0") && level == 2, mem);
            T.Eq("hud: memory without a total is no data", HudFormat.NoData, Text(HudKind.Memory, 5, double.NaN, out level));
            T.Check("hud: MHz", Text(HudKind.Mhz, 4425.4, 0, out level).StartsWith("4425 "));
            T.Check("hud: volts with three decimals", Text(HudKind.Volts, 0.895, 0, out level).StartsWith("0.895 "));
            T.Check("hud: small watts keep a decimal", Text(HudKind.Watts, 7.25, 0, out level).StartsWith("7.3 ") || Text(HudKind.Watts, 7.25, 0, out level).StartsWith("7.2 "));
            T.Check("hud: frame time in ms", Text(HudKind.Ms, 6.94, 0, out level).StartsWith("6.9 "));
            HudValue generic = new HudValue("hw.1.0.2", HudKind.Number, 1.5);
            generic.Unit = "A";
            T.Eq("hud: generic number carries the source unit", "1.50 A", HudFormat.Text(generic, out level));
            T.Eq("hud: NaN is no data", HudFormat.NoData, Text(HudKind.Rpm, double.NaN, 0, out level));
            T.Check("hud: 26.85 °C from 300 K", Math.Abs(HudFormat.KelvinToCelsius(300) - 26.85) < 0.001);
            T.Check("hud: rate in gigabytes", HudFormat.Rate(3 * 1073741824.0).StartsWith("3.0 "));
            T.Eq("hud: uptime with days", Tr.S("2 д 03:04:05", "2 d 03:04:05"), HudFormat.Uptime(new TimeSpan(2, 3, 4, 5)));
            HudRow row = HudFormat.Row(new HudItem("cpu.load") { Graph = true, Text = false }, new HudValue("cpu.load", HudKind.Percent, 50));
            T.Check("hud: graph-only row hides the number and scales 0..100", row.Graph && !row.TextShown && row.Max == 100);
            HudRow textGraph = HudFormat.Row(new HudItem("clock") { Graph = true }, null);
            T.Check("hud: text rows never get a graph", !textGraph.Graph && textGraph.TextShown);
        }

        // ---- SMBIOS ----
        private static void SmbiosParsing()
        {
            // Тип 17 со строками: 32 ГБ DDR5 6000 (базовая 4800), 1,35 В, «Corsair», «CMH96GX5M2B6600C32»; затем тип 127.
            byte[] t17 = new byte[0x5C];
            t17[0] = 17; t17[1] = 0x5C;
            Array.Copy(BitConverter.GetBytes((ushort)0x7FFF), 0, t17, 0x0C, 2);
            Array.Copy(BitConverter.GetBytes(48u * 1024u), 0, t17, 0x1C, 4);
            t17[0x10] = 1; t17[0x11] = 2; t17[0x12] = 0x22;
            Array.Copy(BitConverter.GetBytes((ushort)4800), 0, t17, 0x15, 2);
            t17[0x17] = 3; t17[0x1A] = 4; t17[0x1B] = 2;
            Array.Copy(BitConverter.GetBytes((ushort)6000), 0, t17, 0x20, 2);
            Array.Copy(BitConverter.GetBytes((ushort)1350), 0, t17, 0x26, 2);
            byte[] strings = Encoding.ASCII.GetBytes("DIMM 1\0P0 CHANNEL A\0Corsair\0CMH96GX5M2B6600C32\0\0");
            byte[] end = { 127, 4, 0, 0, 0, 0 };
            byte[] table = new byte[t17.Length + strings.Length + end.Length];
            Array.Copy(t17, table, t17.Length);
            Array.Copy(strings, 0, table, t17.Length, strings.Length);
            Array.Copy(end, 0, table, t17.Length + strings.Length, end.Length);
            byte[] raw = new byte[8 + table.Length];
            raw[1] = 3; raw[2] = 6;
            Array.Copy(BitConverter.GetBytes((uint)table.Length), 0, raw, 4, 4);
            Array.Copy(table, 0, raw, 8, table.Length);
            SmbiosInfo info = Smbios.Parse(raw);
            SmbiosMemory m = info != null && info.Memory.Count == 1 ? info.Memory[0] : null;
            T.Check("hud smbios: extended size, DDR5, speeds, voltage, strings", m != null && m.SizeMb == 48 * 1024 && m.TypeName == "DDR5"
                    && m.SpeedMts == 4800 && m.ConfiguredMts == 6000 && m.ConfiguredMv == 1350 && m.Locator == "DIMM 1"
                    && m.Bank == "P0 CHANNEL A" && m.PartNumber == "CMH96GX5M2B6600C32" && m.Rank == 2, m == null ? "no module" : m.PartNumber);

            int mts, cl;
            T.Check("hud smbios: Corsair part → 6600 CL32", Smbios.RatedFromPart("CMH96GX5M2B6600C32", out mts, out cl) && mts == 6600 && cl == 32);
            T.Check("hud smbios: G.Skill part → 6000 CL30", Smbios.RatedFromPart("F5-6000J3038F16GX2-TZ5RK", out mts, out cl) && mts == 6000 && cl == 30);
            T.Check("hud smbios: Kingston Fury part → 5600 CL36", Smbios.RatedFromPart("KF556C36BBEK2-32", out mts, out cl) && mts == 5600 && cl == 36, mts + " " + cl);
            T.Check("hud smbios: unknown scheme is not guessed", !Smbios.RatedFromPart("M425R1GB4BB0-CQK", out mts, out cl));

            HudSysInfo sys = HudSysInfo.Build(info, "AMD Ryzen 9 7950X3D 16-Core Processor", 32, new List<DxgiAdapter>(), null);
            T.Check("hud smbios: memory line names kit, speed, voltage and part", sys.MemoryText != null && sys.MemoryText.Contains("DDR5")
                    && sys.MemoryText.Contains("6000") && sys.MemoryText.Contains("1.350") && sys.MemoryText.Contains("CMH96GX5M2B6600C32"), sys.MemoryText);

            SmbiosInfo live = Smbios.Read();
            T.Check("hud smbios live: this machine's table lists installed memory modules", live != null && live.Memory.Count > 0,
                    live == null ? "no table" : live.Memory.Count + " modules");
            if (live != null && live.Memory.Count > 0)
            {
                long totalMb = 0;
                foreach (SmbiosMemory x in live.Memory) totalMb += x.SizeMb;
                Native.MEMORYSTATUSEX ms = new Native.MEMORYSTATUSEX();
                ms.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                Native.GlobalMemoryStatusEx(ref ms);
                double visible = ms.ullTotalPhys / 1048576.0;
                T.Check("hud smbios live: module sizes add up to what Windows sees (within 10 %)", visible <= totalMb && visible >= totalMb * 0.9,
                        totalMb + " MB vs " + Math.Round(visible) + " MB");
            }
            HudSysInfo liveInfo = HudSysInfo.Collect();
            T.Check("hud sysinfo live: cpu model comes from the registry", !string.IsNullOrEmpty(liveInfo.CpuText), liveInfo.CpuText);
        }

        private static void PdhHelpers()
        {
            T.Eq("hud pdh: cores ordered by group and number, totals dropped", "0,0|0,1|0,2|0,10|1,0",
                 string.Join("|", HudPdhSource.OrderCores(new[] { "0,10", "_Total", "1,0", "0,_Total", "0,2", "0,0", "0,1" }).ToArray()));
            Dictionary<string, double> perf = new Dictionary<string, double> { { "0,3", 105.0 } };
            Dictionary<string, double> freq = new Dictionary<string, double> { { "0,3", 4200.0 } };
            T.Eq("hud pdh: MHz = base frequency × performance %", 4410.0, HudPdhSource.Mhz(perf, freq, "0,3"));
            T.Check("hud pdh: missing instance is no data", double.IsNaN(HudPdhSource.Mhz(perf, freq, "0,4")));

            string luid = Native.LuidKey(0, 0x14E6A);
            Dictionary<string, double> engines = new Dictionary<string, double>();
            engines["pid_100_luid_0x00000000_0x00014E6A_phys_0_eng_0_engtype_3D"] = 40;
            engines["pid_200_luid_0x00000000_0x00014E6A_phys_0_eng_1_engtype_3D"] = 40;
            engines["pid_100_luid_0x00000000_0x00014E6A_phys_0_eng_5_engtype_VideoDecode"] = 10;
            engines["pid_300_luid_0x00000000_0x0000AAAA_phys_0_eng_0_engtype_3D"] = 99;
            T.Eq("hud: gpu load = sum within an engine type, max across types, this card only", 80.0, HudPdhSource.GpuLoad(engines, luid));
            Dictionary<string, double> memory = new Dictionary<string, double>();
            memory["luid_0x00000000_0x00014E6A_phys_0"] = 1073741824.0;
            memory["pid_100_luid_0x00000000_0x00014E6A_phys_0"] = 5e9;
            T.Eq("hud: vram used comes from the adapter instance, not processes", 1073741824L, HudPdhSource.GpuMemory(memory, luid));
            T.Eq("hud: nvml throttle reasons", Tr.S("лимит мощности, перегрев", "power cap, thermal"), HudNvml.ThrottleText(0x4 | 0x40 | 0x1));
            T.Eq("hud: nvml idle only", Tr.S("простой", "idle"), HudNvml.ThrottleText(0x1));
        }

        private static void BoardIntervals()
        {
            HudBoard board = new HudBoard();
            List<HudItem> items = HudItem.ParseList("cpu.load:3:1000:0;ram:1:2000:0");
            DateTime t0 = new DateTime(2026, 9, 15, 12, 0, 0);
            HudFrame f = new HudFrame();
            f.Put("cpu.load", HudKind.Percent, 10);
            HudValue ram = new HudValue("ram", HudKind.Memory, 1e9); ram.Total = 2e9; f.Put(ram);
            T.Check("hud board: first tick shows everything", board.Advance(f, items, t0));
            f.Put("cpu.load", HudKind.Percent, 20);
            HudValue ram2 = new HudValue("ram", HudKind.Memory, 1.5e9); ram2.Total = 2e9; f.Put(ram2);
            board.Advance(f, items, t0.AddMilliseconds(1000));
            List<HudRow> rows = board.Rows(items, 60);
            T.Eq("hud board: 1 s row refreshed after 1 s", "20%", rows[0].Value);
            T.Check("hud board: 2 s row keeps its old value after 1 s", rows[1].Value.StartsWith("0.9 "), rows[1].Value);
            T.Check("hud board: nothing is due 250 ms later", !board.Advance(f, items, t0.AddMilliseconds(1250)));
            board.Advance(f, items, t0.AddMilliseconds(2000));
            rows = board.Rows(items, 60);
            T.Check("hud board: 2 s row refreshed after 2 s", rows[1].Value.StartsWith("1.4 "), rows[1].Value);
            T.Check("hud board: graph carries history oldest→newest, 60 slots for 60 s at 1 s",
                    rows[0].Points != null && rows[0].Points.Length == 3 && rows[0].Points[0] == 10 && rows[0].Points[2] == 20 && rows[0].Slots == 60,
                    rows[0].Points == null ? "null" : rows[0].Points.Length.ToString());

            HudRing ring = new HudRing(3);
            for (int i = 1; i <= 5; i++) ring.Push(i, i);
            T.Check("hud ring: keeps the newest values in order", ring.Count == 3 && ring.Value(0) == 3 && ring.Value(2) == 5);
        }

        private static void Rendering()
        {
            HudRow text = HudFormat.Row(new HudItem("cpu.load"), new HudValue("cpu.load", HudKind.Percent, 40));
            HudRow graph = HudFormat.Row(new HudItem("cpu.load") { Graph = true }, new HudValue("cpu.load", HudKind.Percent, 40));
            graph.Points = new double[] { 10, 50, double.NaN, 90, 40 };
            graph.Slots = 60;
            using (Bitmap a = HudRender.Draw(new List<HudRow> { text }, 1f, 75))
            using (Bitmap b = HudRender.Draw(new List<HudRow> { graph }, 1f, 75))
            using (Bitmap c = HudRender.Draw(new List<HudRow> { graph }, 2f, 75))
            {
                T.Check("hud render: a graph adds width", b.Width > a.Width, a.Width + " → " + b.Width);
                T.Check("hud render: scale doubles the size", c.Height >= b.Height * 2 - 2, b.Height + " → " + c.Height);
                T.Check("hud render: a graph row is twice as tall as a text row", b.Height - 16 >= (a.Height - 16) * 2 - 1, a.Height + " / " + b.Height);
                // Правый край графика — последняя точка (40 %), под ней должна быть закрашенная область.
                int x = b.Width - 8 - 2, bottom = b.Height - 8 - 4;
                Color px = b.GetPixel(x, bottom);
                T.Check("hud render: the graph is actually drawn (fill under the newest point)", px.B > 60, px.ToString());
                // График — полосой под строкой во всю ширину: у левого края внизу уже полоса графика, а не пустой фон под подписью.
                Color band = b.GetPixel(8 + 2, b.Height - 8 - 3);
                T.Check("hud render: the graph strip lies under the text row from the left edge", band.R > 30, band.ToString());
                Color top = b.GetPixel(b.Width - 8 - 2, 8 + 1);
                T.Check("hud render: the text row above the graph has no graph fill", top.B < 60 || (top.R > 150 && top.G > 150), top.ToString());
            }
        }

        private static void LiveCollector()
        {
            using (HudCollector c = new HudCollector())
            {
                c.Tick();
                System.Threading.Thread.Sleep(1100);
                HudFrame f = c.Tick();
                T.Check("hud live: cpu load on the second read", f.Has("cpu.load"), f.Get("cpu.load") == null ? "null" : f.Get("cpu.load").Value.ToString());
                T.Check("hud live: per-core load for logical processor 1", f.Has("cpu.core.0.load"));
                int cores = 0;
                while (f.Has(HudCatalog.CoreId(cores, "load"))) cores++;
                T.Eq("hud live: one load row per logical processor", Environment.ProcessorCount, cores);
                if (f.Has("cpu.core.0.mhz"))
                    T.Check("hud live: per-core frequency is plausible", f.Get("cpu.core.0.mhz").Value > 100 && f.Get("cpu.core.0.mhz").Value < 10000, f.Get("cpu.core.0.mhz").Value.ToString());
                else T.Skip("hud live: per-core frequency is plausible", "no «% Processor Performance» on this machine");
                T.Check("hud live: ram, commit, clock, uptime", f.Has("ram") && f.Has("commit") && f.Has("clock") && f.Has("sys.uptime"));
                T.Check("hud live: disk and network rates", f.Has("disk.read") && f.Has("net.down"));
                T.Check("hud live: memory modules line from SMBIOS", f.Has("sys.mem"), f.Get("sys.mem") == null ? "null" : f.Get("sys.mem").Text);
                DxgiAdapter main = HudAdapters.Primary();
                if (main != null && main.VendorId == 0x10DE && !NvmlWorks())
                    T.Skip("hud live: NVIDIA card reports temperature, clock and P-state", "NVML does not start - NVIDIA driver service stopped");
                else if (main != null && main.VendorId == 0x10DE)
                    T.Check("hud live: NVIDIA card reports temperature, clock and P-state", f.Has("gpu.temp") && f.Has("gpu.clock") && f.Has("gpu.pstate"),
                        "temp=" + f.Has("gpu.temp") + " clock=" + f.Has("gpu.clock") + " pstate=" + f.Has("gpu.pstate") + " power=" + f.Has("gpu.power"));
                else T.Skip("hud live: NVIDIA card reports temperature, clock and P-state", "main card is not NVIDIA");
            }
        }

        private static void LiveWindow()
        {
            using (HudWindow w = new HudWindow())
            {
                w.Show();
                w.Render(new List<HudRow> { HudFormat.Row(new HudItem("cpu.load") { Graph = true }, new HudValue("cpu.load", HudKind.Percent, 12)) },
                         new CapSettings { HudCorner = HudCorner.TopLeft }, new HudStyle());
                Application.DoEvents();
                int ex = GetWindowLong(w.Handle, -20);
                T.Check("hud live: the window is visible", IsWindowVisible(w.Handle));
                T.Check("hud live: clicks pass through (layered + transparent)", (ex & 0x80020) == 0x80020, ex.ToString("X"));
                T.Check("hud live: never takes focus and stays on top", (ex & 0x8000008) == 0x8000008, ex.ToString("X"));
                T.Check("hud live: showing it did not steal the foreground", Form.ActiveForm != w);
                w.SetMoveMode(true);
                int moving = GetWindowLong(w.Handle, -20);
                w.SetMoveMode(false);
                int back = GetWindowLong(w.Handle, -20);
                T.Check("hud live: drag mode catches the mouse, leaving it passes clicks through again",
                        (moving & 0x20) == 0 && (moving & 0x80000) != 0 && (back & 0x20) != 0, moving.ToString("X") + " " + back.ToString("X"));
                w.Close();
            }
        }
    }
}
