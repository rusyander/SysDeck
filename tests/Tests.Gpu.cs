// Windows Process Cleaner — область «gpu»: сходится ли схема видеопамяти и от чего отказывается
// перезапуск GPU-процесса.
//
// Прогон НИЧЕГО не сбрасывает: видеодрайвер не перезапускается (это гасит экран), чужие GPU-процессы
// не трогаются. Единственный процесс, на который тест направляет перезапуск, — собственный дочерний
// cmd.exe с подложной строкой «--type=gpu-process», и проверяется, что он остался жив.

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace WindowsProcessCleaner.Tests
{
    internal static class GpuTests
    {
        internal static void Run()
        {
            Parser();
            Attribution();
            ComposeByHand();
            Cooldown();
            Protection();

            Engine e = Fx.NewEngine("gpu");
            Live(e);
            RefuseFake(e);
        }

        // ---------- имена экземпляров PDH ----------
        private static void Parser()
        {
            int pid; string luid, eng;
            T.Check("process instance parses",
                    Engine.GpuParseInstance("pid_2444_luid_0x00000000_0x00014E6A_phys_0", out pid, out luid, out eng)
                    && pid == 2444 && luid == Native.LuidKey(0, 0x00014E6A) && eng == null, pid + " " + luid);
            T.Check("adapter instance parses with pid 0",
                    Engine.GpuParseInstance("luid_0x00000000_0x00014E6A_phys_0", out pid, out luid, out eng)
                    && pid == 0 && luid == Native.LuidKey(0, 0x00014E6A), pid + " " + luid);
            T.Check("engine instance keeps the engine type",
                    Engine.GpuParseInstance("pid_1956_luid_0x00000000_0x00014e6a_phys_0_eng_13_engtype_VideoDecode", out pid, out luid, out eng)
                    && pid == 1956 && eng == "VideoDecode" && luid == Native.LuidKey(0, 0x00014E6A), pid + " " + eng + " " + luid);
            T.Check("a high LUID part keeps its sign bits",
                    Engine.GpuParseInstance("luid_0xFFFFFFFF_0x00000001_phys_0", out pid, out luid, out eng)
                    && luid == Native.LuidKey(-1, 1), luid);
            string[] bad = { null, "", "_Total", "pid_x_luid_0x0_0x1", "pid_12_luid_0xZZ_0x1", "pid_12_something", "luid_0x00000000" };
            foreach (string b in bad)
                T.Check("garbage instance is rejected: " + (b ?? "null"), !Engine.GpuParseInstance(b, out pid, out luid, out eng) && pid == 0);
        }

        // ---------- правило минимума ----------
        // Dedicated Usage у процесса иногда больше всей карты (виртуальные резервы); Local Usage —
        // сколько реально в памяти карты. Берётся меньшее, но только если карта Local вообще отдаёт.
        private static void Attribution()
        {
            const long G = 1024L * 1024 * 1024;
            T.Eq("min of dedicated and local", 2 * G, Engine.GpuAttribute(9 * G, 2 * G, true));
            T.Eq("local larger than dedicated keeps dedicated", 1 * G, Engine.GpuAttribute(1 * G, 3 * G, true));
            T.Eq("adapter without local counter keeps dedicated", 9 * G, Engine.GpuAttribute(9 * G, 0, false));
            T.Eq("negative counters clamp to zero", 0L, Engine.GpuAttribute(-5, -5, true));
        }

        // ---------- разложение на подставленных числах ----------
        private static void ComposeByHand()
        {
            const long M = 1024L * 1024;
            GpuAdapter a = new GpuAdapter();
            a.Luid = "x"; a.Name = "test"; a.Known = true;
            a.DedicatedTotal = 8000 * M; a.DedicatedUsed = 3000 * M;
            a.SharedTotal = 16000 * M; a.SharedUsed = 500 * M;
            List<GpuProc> mine = new List<GpuProc>();
            mine.Add(Proc(10, "chrome.exe", 700 * M, 100 * M, false));
            mine.Add(Proc(11, "chrome.exe", 300 * M, 0, true));
            mine.Add(Proc(12, "game.exe", 1500 * M, 200 * M, false));
            Engine.GpuCompose(a, mine);

            T.Eq("hand: dedicated slices sum to the card", a.DedicatedTotal, Sum(a.DedicatedSlices));
            T.Eq("hand: shared slices sum to the shared total", a.SharedTotal, Sum(a.SharedSlices));
            T.Check("hand: nothing is scaled when processes fit", !a.Scaled);
            RamSlice grp = Find(a.DedicatedSlices, "gd:name:chrome.exe");
            T.Check("hand: two chrome processes form one group of 1000 MB",
                    grp != null && grp.Bytes == 1000 * M && grp.Children != null && grp.Children.Count == 2
                    && grp.Pids.Count == 2, grp == null ? "no group" : grp.Bytes.ToString());
            RamSlice sys = Find(a.DedicatedSlices, "gd:system");
            T.Eq("hand: system is used minus processes", 500 * M, sys == null ? -1 : sys.Bytes);
            RamSlice helper = grp == null ? null : Find(grp.Children, "gd:pid:11");
            T.Check("hand: the GPU helper is its own kind", helper != null && helper.Kind == GpuKind.Helper);

            // Процессы «заняли» больше карты — схема ужимается, но не выходит за объём.
            GpuAdapter over = new GpuAdapter();
            over.Luid = "y"; over.Name = "small"; over.Known = true;
            over.DedicatedTotal = 1000 * M; over.DedicatedUsed = 1000 * M;
            List<GpuProc> big = new List<GpuProc>();
            big.Add(Proc(20, "a.exe", 900 * M, 0, false));
            big.Add(Proc(21, "b.exe", 900 * M, 0, false));
            Engine.GpuCompose(over, big);
            T.Check("overshoot: shares are scaled", over.Scaled);
            T.Eq("overshoot: slices still sum to the card", over.DedicatedTotal, Sum(over.DedicatedSlices));
            foreach (RamSlice s in over.DedicatedSlices)
                T.Check("overshoot: no negative slice " + s.Key, s.Bytes >= 0, s.Bytes.ToString());

            // Счётчик карты отстал от процессов — «занято» подтягивается к их сумме.
            GpuAdapter lag = new GpuAdapter();
            lag.Luid = "z"; lag.Name = "igpu"; lag.Known = true;
            lag.DedicatedTotal = 500 * M; lag.DedicatedUsed = 150 * M;
            List<GpuProc> more = new List<GpuProc>();
            more.Add(Proc(30, "dwm.exe", 170 * M, 0, false));
            more.Add(Proc(31, "csrss.exe", 40 * M, 0, false));
            Engine.GpuCompose(lag, more);
            T.Eq("lagging counter: used is raised to the process sum", 210 * M, lag.DedicatedUsed);
            RamSlice lagSys = Find(lag.DedicatedSlices, "gd:system");
            RamSlice lagFree = Find(lag.DedicatedSlices, "gd:free");
            T.Check("lagging counter: system is zero and free is the rest",
                    lagSys != null && lagSys.Bytes == 0 && lagFree != null && lagFree.Bytes == 290 * M,
                    (lagSys == null ? "?" : lagSys.Bytes.ToString()) + " / " + (lagFree == null ? "?" : lagFree.Bytes.ToString()));
            T.Eq("lagging counter: slices still sum to the card", lag.DedicatedTotal, Sum(lag.DedicatedSlices));
        }

        private static GpuProc Proc(int pid, string name, long dedicated, long shared, bool helper)
        {
            GpuProc p = new GpuProc();
            p.Pid = pid; p.Name = name; p.Luid = "x";
            p.Dedicated = dedicated; p.DedicatedRaw = dedicated; p.Local = dedicated;
            p.Shared = shared; p.Helper = helper;
            return p;
        }

        // ---------- пауза между сбросами ----------
        private static void Cooldown()
        {
            DateTime now = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
            T.Eq("never restarted means no wait", 0, Engine.GpuCooldownLeft(DateTime.MinValue, now, 180));
            T.Eq("just restarted waits the full pause", 180, Engine.GpuCooldownLeft(now, now, 180));
            T.Eq("partial second rounds up", 60, Engine.GpuCooldownLeft(now.AddSeconds(-120.5), now, 180));
            T.Eq("after the pause it is free", 0, Engine.GpuCooldownLeft(now.AddSeconds(-181), now, 180));
        }

        // ---------- что не завершается никогда ----------
        // Список намеренно узкий: мессенджеры и вендорские утилиты человек вправе закрыть сам.
        private static void Protection()
        {
            string[] never = { "dwm.exe", "csrss.exe", "svchost.exe", "LSASS.EXE", "WindowsProcessCleaner.exe" };
            foreach (string n in never) T.Check("protected: " + n, Engine.GpuIsProtectedName(n));
            string[] allowed = { "chrome.exe", "Telegram.exe", "NVIDIA app.exe", "steam.exe", "", null };
            foreach (string n in allowed) T.Check("not protected: " + (n ?? "null"), !Engine.GpuIsProtectedName(n));
        }

        // ---------- настоящий замер ----------
        private static void Live(Engine e)
        {
            Stopwatch sw = Stopwatch.StartNew();
            GpuSnapshot s = e.GpuSample();
            long first = sw.ElapsedMilliseconds;
            if (s == null || !s.Ok) { T.Skip("live GPU sample", s == null ? "null" : s.Error); return; }
            if (s.Adapters.Count == 0) { T.Skip("live GPU sample", "no graphics card with memory on this machine"); return; }
            System.Threading.Thread.Sleep(300);
            sw = Stopwatch.StartNew();
            s = e.GpuSample();
            T.Check("live: a repeated sample is fast (< 1500 ms)", sw.ElapsedMilliseconds < 1500,
                    "first " + first + " ms, second " + sw.ElapsedMilliseconds + " ms");

            foreach (GpuAdapter a in s.Adapters)
            {
                string tag = "live " + a.Name + ": ";
                T.Check(tag + "has a LUID and a name", !string.IsNullOrEmpty(a.Luid) && !string.IsNullOrEmpty(a.Name));
                if (a.DedicatedTotal > 0)
                    T.Eq(tag + "dedicated slices sum to the card", a.DedicatedTotal, Sum(a.DedicatedSlices));
                if (a.SharedTotal > 0)
                    T.Eq(tag + "shared slices sum to the shared total", a.SharedTotal, Sum(a.SharedSlices));
                // Шапка берёт «занято» из счётчика карты, схема — из блоков; числа обязаны совпасть.
                RamSlice free = Find(a.DedicatedSlices, "gd:free");
                if (a.DedicatedTotal > 0 && free != null && a.DedicatedUsed <= a.DedicatedTotal)
                    T.Eq(tag + "header used equals the scheme without the free block",
                         a.DedicatedUsed, a.DedicatedTotal - free.Bytes);
                T.Check(tag + "used memory never exceeds the card when the size is known",
                        !a.Known || a.DedicatedTotal == 0 || Sum(a.DedicatedSlices) == a.DedicatedTotal);
                foreach (RamSlice x in a.DedicatedSlices)
                    if (x.Bytes < 0) T.Check(tag + "no negative slice " + x.Key, false, x.Bytes.ToString());
                T.Check(tag + "load is a percentage", a.Load >= 0 && a.Load <= 100.0001, a.Load.ToString());
            }
            bool minHolds = true;
            string bad = null;
            foreach (GpuProc p in s.Procs)
                if (p.Dedicated > p.DedicatedRaw || p.Dedicated < 0) { minHolds = false; bad = p.Name + " " + p.Pid; break; }
            T.Check("live: attributed dedicated memory never exceeds the raw counter", minHolds, bad);
        }

        // ---------- подложный GPU-процесс ----------
        // Дочерний cmd.exe с «--type=gpu-process» в строке — но его родитель не cmd.exe, а сам тест.
        // Перезапуск обязан отказать и оставить процесс живым: иначе строкой запуска можно было бы
        // заставить кнопку завершить что угодно.
        private static void RefuseFake(Engine e)
        {
            Process child = null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 6 127.0.0.1 >nul & rem --type=gpu-process");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                child = Process.Start(psi);
            }
            catch (Exception ex) { T.Skip("fake GPU process is refused", ex.Message); return; }
            try
            {
                System.Threading.Thread.Sleep(300);
                T.Check("fake: the command line really carries the flag",
                        Engine.GpuIsHelperCommandLine(Native.CommandLineOf(child.Id)), Native.CommandLineOf(child.Id));
                GpuAction r = e.GpuRestartHelper(child.Id);
                T.Check("fake: a child of a different image is refused", r.Refused && !r.Ok, r.Message);
                T.Check("fake: the refused process is still alive", !child.HasExited);

                GpuAction self = e.GpuRestartHelper(Process.GetCurrentProcess().Id);
                T.Check("fake: the test process itself is refused", self.Refused && !self.Ok, self.Message);
                GpuAction gone = e.GpuRestartHelper(0);
                T.Check("fake: pid 0 is refused", gone.Refused && !gone.Ok, gone.Message);
            }
            finally
            {
                try { if (!child.HasExited) child.Kill(); } catch { }
                child.Dispose();
            }
        }

        private static long Sum(List<RamSlice> list)
        {
            long s = 0;
            foreach (RamSlice x in list) s += x.Bytes;
            return s;
        }

        private static RamSlice Find(List<RamSlice> list, string key)
        {
            if (list == null) return null;
            foreach (RamSlice x in list)
            {
                if (x.Key == key) return x;
                RamSlice inner = Find(x.Children, key);
                if (inner != null) return inner;
            }
            return null;
        }
    }
}
