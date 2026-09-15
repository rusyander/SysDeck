// SysDeck — показатели «игры» для оверлея: процесс активного окна вместе с его потомками. Браузеры,
// Electron и часть лаунчеров рисуют кадры и держат видеопамять в дочернем GPU-процессе, поэтому числа — по семейству
// процессов, а не по одному pid. Без прав администратора: хватает PROCESS_QUERY_LIMITED_INFORMATION.
//
// Видеопамять и загрузка видеокарты игры считаются в HudPdhSource (там уже читаются счётчики GPU Engine), процесс
// берётся отсюда же — HudProcTree.Current().
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace SysDeck.Capture
{
    // Активное окно и дерево процессов — общие для кадров (ETW) и показателей игры.
    internal static class HudProcTree
    {
        private static readonly object Gate = new object();
        private static Dictionary<int, Entry> _snap = new Dictionary<int, Entry>();
        private static int _snapAt;
        private static HudFamily _family;
        private static int _familyAt;

        internal struct Entry { public int Parent; public int Threads; public string Exe; }

        public static int ForegroundPid()
        {
            IntPtr fg = CapNative.GetForegroundWindow();
            if (fg == IntPtr.Zero) return 0;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            return (int)pid;
        }

        // Снимок процессов не чаще раза в 3 с (родитель, потоки, имя exe).
        public static Dictionary<int, Entry> Snapshot()
        {
            lock (Gate)
            {
                if (_snap.Count > 0 && unchecked(Environment.TickCount - _snapAt) < 3000) return _snap;
                _snapAt = Environment.TickCount;
                Dictionary<int, Entry> map = new Dictionary<int, Entry>();
                IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
                if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return _snap;
                try
                {
                    PROCESSENTRY32 e = new PROCESSENTRY32();
                    e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                    for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                    {
                        Entry x = new Entry();
                        x.Parent = (int)e.th32ParentProcessID;
                        x.Threads = (int)e.cntThreads;
                        x.Exe = e.szExeFile;
                        map[(int)e.th32ProcessID] = x;
                    }
                }
                finally { CloseHandle(snap); }
                _snap = map;
                return map;
            }
        }

        public static Dictionary<int, int> Parents()
        {
            Dictionary<int, int> map = new Dictionary<int, int>();
            foreach (KeyValuePair<int, Entry> kv in Snapshot()) map[kv.Key] = kv.Value.Parent;
            return map;
        }

        // Процесс активного окна и его потомки; пересчитывается не чаще раза в 500 мс.
        public static HudFamily Current()
        {
            lock (Gate)
            {
                if (_family != null && unchecked(Environment.TickCount - _familyAt) < 500) return _family;
            }
            int pid = ForegroundPid();
            HudFamily f = pid <= 0 ? null : Family(pid, Snapshot());
            lock (Gate)
            {
                _family = f;
                _familyAt = Environment.TickCount;
            }
            return f;
        }

        internal static HudFamily Family(int root, Dictionary<int, Entry> snap)
        {
            Dictionary<int, int> parents = new Dictionary<int, int>();
            foreach (KeyValuePair<int, Entry> kv in snap) parents[kv.Key] = kv.Value.Parent;
            HudFamily f = new HudFamily();
            f.Root = root;
            f.Pids.Add(root);
            foreach (int pid in snap.Keys)
                if (pid != root && HudFpsSource.IsDescendant(pid, root, parents)) f.Pids.Add(pid);
            Entry e;
            if (snap.TryGetValue(root, out e) && !string.IsNullOrEmpty(e.Exe)) f.Name = Path.GetFileNameWithoutExtension(e.Exe);
            foreach (int pid in f.Pids)
                if (snap.TryGetValue(pid, out e)) f.Threads += e.Threads;
            return f;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    }

    internal sealed class HudFamily
    {
        public int Root;
        public string Name;
        public int Threads;
        public readonly HashSet<int> Pids = new HashSet<int>();
    }

    // ------------------------------------------------------------------ //
    //  Источник строк «Игра»
    // ------------------------------------------------------------------ //
    internal sealed class HudAppSource : HudSource
    {
        private struct Sample { public long Cpu, Read, Write; }

        private int _root;
        private long _at;
        private Dictionary<int, Sample> _prev = new Dictionary<int, Sample>();

        public HudAppSource() { PeriodMs = 1000; }

        public override string Name { get { return "Windows"; } }

        public override void Collect(HudFrame f)
        {
            HudFamily fam = HudProcTree.Current();
            if (fam != null) Collect(f, fam);
        }

        // Отдельно от активного окна — тесты меряют собственный процесс теми же вызовами.
        internal void Collect(HudFrame f, HudFamily fam)
        {
            long ws = 0, priv = 0, handles = 0;
            DateTime started = DateTime.MinValue;
            Dictionary<int, Sample> now = new Dictionary<int, Sample>();
            bool any = false;
            foreach (int pid in fam.Pids)
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    Native.PROCESS_MEMORY_COUNTERS m;
                    if (!Native.GetProcessMemoryInfo(h, out m, (uint)Marshal.SizeOf(typeof(Native.PROCESS_MEMORY_COUNTERS)))) continue;
                    any = true;
                    ws += m.WorkingSetSize.ToInt64();
                    // PagefileUsage у процесса — это его private commit (то же, что «Выделено» в диспетчере задач).
                    priv += m.PagefileUsage.ToInt64();
                    uint hc;
                    if (GetProcessHandleCount(h, out hc)) handles += hc;
                    Sample s = new Sample();
                    TimeSpan cpu; DateTime start;
                    if (Native.QueryTimes(h, out cpu, out start))
                    {
                        s.Cpu = cpu.Ticks;
                        if (pid == fam.Root) started = start;
                    }
                    IO_COUNTERS io;
                    if (GetProcessIoCounters(h, out io)) { s.Read = (long)io.ReadTransferCount; s.Write = (long)io.WriteTransferCount; }
                    now[pid] = s;
                }
                finally { Native.CloseHandle(h); }
            }
            if (!any) return;

            f.PutText("app.name", fam.Pids.Count > 1
                ? (fam.Name ?? fam.Root.ToString()) + " +" + (fam.Pids.Count - 1)
                : fam.Name ?? fam.Root.ToString());
            f.Put("app.ram", HudKind.Bytes, ws);
            f.Put("app.private", HudKind.Bytes, priv);
            HudValue th = new HudValue("app.threads", HudKind.Number, fam.Threads);
            f.Put(th);
            f.Put("app.handles", HudKind.Number, handles);
            if (started != DateTime.MinValue && started <= DateTime.Now)
                f.PutText("app.uptime", HudFormat.Uptime(DateTime.Now - started));

            long stamp = Stopwatch.GetTimestamp();
            if (fam.Root == _root && _at > 0)
            {
                double sec = (double)(stamp - _at) / Stopwatch.Frequency;
                double cpuPct, read, write;
                if (Rates(_prev, now, sec, Environment.ProcessorCount, out cpuPct, out read, out write))
                {
                    f.Put("app.cpu", HudKind.Percent, cpuPct);
                    f.Put("app.io.read", HudKind.Rate, read);
                    f.Put("app.io.write", HudKind.Rate, write);
                }
            }
            _root = fam.Root;
            _at = stamp;
            _prev = now;
        }

        // Скорости по процессам, которые были и в прошлом замере: закрывшийся дочерний процесс не даёт минуса, новый —
        // скачка на всё время своей жизни. Процессор — доля от всех логических ядер, как в диспетчере задач.
        private static bool Rates(Dictionary<int, Sample> prev, Dictionary<int, Sample> now, double seconds, int cores,
                                   out double cpuPercent, out double readPerSec, out double writePerSec)
        {
            cpuPercent = readPerSec = writePerSec = double.NaN;
            if (seconds <= 0.05 || cores <= 0) return false;
            long cpu = 0, read = 0, write = 0;
            bool any = false;
            foreach (KeyValuePair<int, Sample> kv in now)
            {
                Sample p;
                if (!prev.TryGetValue(kv.Key, out p)) continue;
                any = true;
                cpu += Math.Max(0, kv.Value.Cpu - p.Cpu);
                read += Math.Max(0, kv.Value.Read - p.Read);
                write += Math.Max(0, kv.Value.Write - p.Write);
            }
            if (!any) return false;
            cpuPercent = Math.Min(100, cpu / (double)TimeSpan.TicksPerSecond / seconds / cores * 100.0);
            readPerSec = read / seconds;
            writePerSec = write / seconds;
            return true;
        }

        // Для тестов: те же скорости по готовым числам.
        internal static bool Rates(long[] prevCpuReadWrite, long[] nowCpuReadWrite, double seconds, int cores,
                                   out double cpuPercent, out double readPerSec, out double writePerSec)
        {
            Dictionary<int, Sample> a = new Dictionary<int, Sample>(), b = new Dictionary<int, Sample>();
            for (int i = 0; i + 3 < prevCpuReadWrite.Length; i += 4)
                a[(int)prevCpuReadWrite[i]] = new Sample { Cpu = prevCpuReadWrite[i + 1], Read = prevCpuReadWrite[i + 2], Write = prevCpuReadWrite[i + 3] };
            for (int i = 0; i + 3 < nowCpuReadWrite.Length; i += 4)
                b[(int)nowCpuReadWrite[i]] = new Sample { Cpu = nowCpuReadWrite[i + 1], Read = nowCpuReadWrite[i + 2], Write = nowCpuReadWrite[i + 3] };
            return Rates(a, b, seconds, cores, out cpuPercent, out readPerSec, out writePerSec);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [DllImport("kernel32.dll")] private static extern bool GetProcessIoCounters(IntPtr h, out IO_COUNTERS c);
        [DllImport("kernel32.dll")] private static extern bool GetProcessHandleCount(IntPtr h, out uint count);
    }
}
