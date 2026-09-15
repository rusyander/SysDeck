// SysDeck — вкладка «Память»: что физически занято и кем.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Задача файла — свести всю физическую память к одному разложению, которое СХОДИТСЯ:
// сумма всех строк равна установленному объёму планок, байт в байт. Пока разложение не
// сходится, любая отдельная цифра на экране ничего не стоит — именно поэтому здесь есть
// строка «не отнесено», а не молчаливое округление остатка в чью-то пользу.
//
// Как считается (все величины — в страницах, умножаются на PageSize):
//
//   Установлено (SMBIOS)
//    ├─ Аппаратно зарезервировано  = Установлено − TotalPhys
//    └─ TotalPhys
//        ├─ Активная  = TotalPhys − Standby − Modified − Free − Zeroed − Bad
//        │   ├─ Процессы          = Σ частных рабочих наборов (без «Сжатой памяти»)
//        │   ├─ Сжатая память     = рабочий набор процесса Memory Compression
//        │   ├─ Невыгружаемый пул = NonPagedPoolPages (резидентен по определению)
//        │   ├─ Выгружаемый пул   = ResidentPagedPoolPage (именно резидентная часть)
//        │   ├─ Код ядра/драйверов= ResidentSystemCodePage + ResidentSystemDriverPage
//        │   ├─ Системный кэш     = ResidentSystemCachePage
//        │   └─ Не отнесено       = остаток
//        ├─ Ожидание (standby) по восьми приоритетам
//        ├─ Изменённые (+ «без записи»)
//        ├─ Свободно (свободные + обнулённые)
//        └─ Плохие блоки
//
// Про «не отнесено» честно: туда попадают страницы, которые Windows не отдаёт ни одним
// документированным вызовом — заблокированные драйверами и виртуальными машинами (WSL,
// Hyper-V, Docker), таблицы страниц, разделяемые DLL и отображённые файлы, AWE. RAMMap
// разбирает их по PFN, но это чтение всей таблицы физических страниц (на 96 ГБ — восемь
// миллионов записей) со структурами, которые меняются от сборки к сборке Windows: в
// реальном времени такое не показывают, а ошибка в раскладке структуры стоит падения.
// Верхняя граница разделяемых страниц считается честно — Σ(рабочий набор − частный), —
// и по ней видно, чего в остатке ТОЧНО нет.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysDeck
{
    // ------------------------------------------------------------------ //
    //  Модель
    // ------------------------------------------------------------------ //

    // Из чего состоит строка разложения. Числами, а не строками: по виду выбирается цвет
    // на схеме, и подпись «Свободно» в другой локали не должна менять цвет блока.
    public static class RamKind
    {
        public const int Group = 0;
        public const int Process = 1;
        public const int Compressed = 2;
        public const int NonPagedPool = 3;
        public const int PagedPool = 4;
        public const int KernelCode = 5;
        public const int SystemCache = 6;
        public const int Unattributed = 7;
        public const int Standby = 8;
        public const int Modified = 9;
        public const int Free = 10;
        public const int Reserved = 11;
        public const int Bad = 12;
    }

    public class RamProc
    {
        public int Pid;
        public int ParentPid;
        public int SessionId;
        public int Threads;
        public int Handles;
        public string Name;
        public long WorkingSet;
        public long PrivateWorkingSet;
        public long Commit;
        public long PagedPool;
        public long NonPagedPool;
        public long Faults;
        public long HardFaults;
        public long Delta;            // насколько вырос частный набор с прошлого замера
        public double HardFaultRate;  // жёстких промахов в секунду с прошлого замера
    }

    // Узел схемы. Children — вложенные блоки, Pids — процессы, которые этот блок покрывает
    // (по ним работают «Завершить» и «Сбросить рабочий набор» на любом уровне вложенности).
    public class RamSlice
    {
        public string Key;
        public string Title;
        public string Hint;
        public long Bytes;
        public int Kind;
        public int Pid;                       // != 0 — блок ровно одного процесса
        public List<RamSlice> Children;
        public List<int> Pids;

        public RamSlice(string key, string title, long bytes, int kind)
        {
            Key = key; Title = title; Bytes = bytes; Kind = kind;
        }

        public bool Killable { get { return Pids != null && Pids.Count > 0; } }
    }

    public class RamRange
    {
        public long Start;
        public long Length;
    }

    public class RamModule
    {
        public string Slot;
        public string Bank;
        public string Kind;
        public string Maker;
        public string Part;
        public long Bytes;
        public int Speed;
        public int ConfiguredSpeed;
    }

    public class RamPool
    {
        public string Tag;
        public long Paged;
        public long NonPaged;
        public long PagedAllocs;
        public long NonPagedAllocs;
        public long Total { get { return Paged + NonPaged; } }
    }

    public class RamSnapshot
    {
        public DateTime At;
        public long PageSize = 4096;

        public long Installed;            // сумма планок по SMBIOS; без SMBIOS — TotalPhys
        public long TotalPhys;
        public long AvailPhys;
        public long Used;                 // TotalPhys − AvailPhys
        public long HardwareReserved;

        public long CommitTotal, CommitLimit, CommitPeak;
        public long KernelPaged, KernelNonPaged, KernelTotal, SystemCacheRough;
        public int Handles, ProcessCount, ThreadCount;

        public bool ListsOk;              // ответил ли SystemMemoryListInformation
        public long Zeroed, FreePages, Modified, ModifiedNoWrite, Bad;
        public long[] StandbyByPriority = new long[8];
        public long Standby;
        public long Active;

        public bool CacheOk;
        public long CacheCurrent, CachePeak, CacheWithTransition;

        public bool PerfOk;
        public long PagedPoolTotal, NonPagedPoolTotal;
        public long ResidentPagedPool, ResidentDriver, ResidentKernelCode, ResidentCache;
        public long PageFaults, PageReads, HardPageReads;
        public double FaultsPerSec, HardReadsPerSec;

        public long ProcPrivate;          // Σ частных рабочих наборов, без сжатой памяти
        public long ProcWorkingSet;       // Σ рабочих наборов (разделяемое посчитано не раз)
        public long Compressed;
        public long ShareableMax;         // верхняя граница разделяемых резидентных страниц
        public long Unattributed;

        public List<RamProc> Procs = new List<RamProc>();
        public List<RamSlice> Slices = new List<RamSlice>();

        public double UsedPercent
        {
            get { return TotalPhys > 0 ? 100.0 * Used / TotalPhys : 0; }
        }
    }

    // Результат сброса: сколько освободилось и что сказать человеку.
    public class RamAction
    {
        public bool Ok;
        public long Freed;
        public int Count;
        public int Denied;
        public string Message;
    }

    public partial class Engine
    {
        // Команды сброса. 2..5 — команды ядра для SystemMemoryListInformation (те же, что у
        // RAMMap в меню Empty), 6 и 7 — наши составные.
        public const int RamEmptyWorkingSets = 2;
        public const int RamFlushModified = 3;
        public const int RamPurgeStandby = 4;
        public const int RamPurgeLowStandby = 5;
        public const int RamEmptySystemWorkingSet = 6;
        public const int RamEmptyEverything = 7;

        public static bool RamCommandKnown(int what)
        {
            return what >= RamEmptyWorkingSets && what <= RamEmptyEverything;
        }

        public static string RamCommandTitle(int what)
        {
            switch (what)
            {
                case RamEmptyWorkingSets: return Tr.S("рабочие наборы всех процессов", "the working sets of all processes");
                case RamFlushModified: return Tr.S("список изменённых страниц", "the modified page list");
                case RamPurgeStandby: return Tr.S("список ожидания (standby)", "the standby list");
                case RamPurgeLowStandby: return Tr.S("ожидание нулевого приоритета", "the priority 0 standby list");
                case RamEmptySystemWorkingSet: return Tr.S("системный рабочий набор", "the system working set");
                case RamEmptyEverything: return Tr.S("всё сразу", "everything");
            }
            return "?";
        }

        // Буфер снимка процессов переиспользуется: на 600 процессах это около 1,7 МБ, и
        // выделять их заново каждую секунду — единственное, что в этом цикле дорого.
        private readonly object _ramLock = new object();
        private IntPtr _ramProcBuf = IntPtr.Zero;
        private int _ramProcBufSize;
        private List<RamModule> _ramModules;      // железо за время работы не меняется
        private List<RamRange> _ramRanges;

        // ------------------------------------------------------------------ //
        //  Замер
        // ------------------------------------------------------------------ //

        // prev нужен только ради «дельт»: насколько вырос каждый процесс и сколько промахов
        // в секунду. Без него замер полон, просто без скоростей.
        public RamSnapshot RamSample(RamSnapshot prev)
        {
            RamSnapshot s = new RamSnapshot();
            s.At = DateTime.UtcNow;

            lock (_ramLock)
            {
                RamReadTotals(s);
                RamReadLists(s);
                RamReadFileCache(s);
                RamReadPerf(s);
                RamReadProcesses(s, prev);
            }

            RamComposeRates(s, prev);
            RamComposeSlices(s);
            return s;
        }

        private void RamReadTotals(RamSnapshot s)
        {
            Native.PERFORMANCE_INFORMATION pi = new Native.PERFORMANCE_INFORMATION();
            pi.cb = (uint)Marshal.SizeOf(typeof(Native.PERFORMANCE_INFORMATION));
            if (Native.GetPerformanceInfo(ref pi, (int)pi.cb))
            {
                long page = pi.PageSize.ToInt64();
                if (page > 0) s.PageSize = page;
                s.CommitTotal = pi.CommitTotal.ToInt64() * s.PageSize;
                s.CommitLimit = pi.CommitLimit.ToInt64() * s.PageSize;
                s.CommitPeak = pi.CommitPeak.ToInt64() * s.PageSize;
                s.TotalPhys = pi.PhysicalTotal.ToInt64() * s.PageSize;
                s.AvailPhys = pi.PhysicalAvailable.ToInt64() * s.PageSize;
                s.SystemCacheRough = pi.SystemCache.ToInt64() * s.PageSize;
                s.KernelTotal = pi.KernelTotal.ToInt64() * s.PageSize;
                s.KernelPaged = pi.KernelPaged.ToInt64() * s.PageSize;
                s.KernelNonPaged = pi.KernelNonpaged.ToInt64() * s.PageSize;
                s.Handles = (int)pi.HandleCount;
                s.ProcessCount = (int)pi.ProcessCount;
                s.ThreadCount = (int)pi.ThreadCount;
            }

            // Подстраховка: без GetPerformanceInfo общий объём всё равно известен.
            if (s.TotalPhys == 0)
            {
                Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
                m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (Native.GlobalMemoryStatusEx(ref m))
                {
                    s.TotalPhys = (long)m.ullTotalPhys;
                    s.AvailPhys = (long)m.ullAvailPhys;
                }
            }
            s.Used = s.TotalPhys - s.AvailPhys;

            long installed = RamInstalledBytes();
            s.Installed = installed > s.TotalPhys ? installed : s.TotalPhys;
            s.HardwareReserved = s.Installed - s.TotalPhys;
        }

        // Списки страниц — сердце вкладки: ровно они делят физическую память на «занято»,
        // «ожидание», «изменено» и «свободно».
        private void RamReadLists(RamSnapshot s)
        {
            IntPtr buf = Marshal.AllocHGlobal(Native.MemoryListSize);
            try
            {
                int got;
                int rc = Native.NtQuerySystemInformation(Native.SystemMemoryListInformation,
                                                         buf, Native.MemoryListSize, out got);
                if (rc != 0) return;
                int ps = IntPtr.Size;
                long page = s.PageSize;
                s.Zeroed = Native.ReadPtr(buf, 0) * page;
                s.FreePages = Native.ReadPtr(buf, ps) * page;
                s.Modified = Native.ReadPtr(buf, 2 * ps) * page;
                s.ModifiedNoWrite = Native.ReadPtr(buf, 3 * ps) * page;
                s.Bad = Native.ReadPtr(buf, 4 * ps) * page;
                long standby = 0;
                for (int i = 0; i < 8; i++)
                {
                    long v = Native.ReadPtr(buf, (5 + i) * ps) * page;
                    s.StandbyByPriority[i] = v;
                    standby += v;
                }
                s.Standby = standby;
                s.ListsOk = true;

                // Активная память — не отдельный счётчик, а остаток. Проверка сходимости:
                // Standby + Free + Zeroed должно совпасть с «доступно» по GlobalMemoryStatusEx.
                long rest = s.Standby + s.Modified + s.ModifiedNoWrite + s.FreePages + s.Zeroed + s.Bad;
                s.Active = s.TotalPhys - rest;
                if (s.Active < 0) s.Active = 0;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private void RamReadFileCache(RamSnapshot s)
        {
            int size = Native.FileCacheSize;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                int got;
                if (Native.NtQuerySystemInformation(Native.SystemFileCacheInformation, buf, size, out got) != 0) return;
                s.CacheCurrent = Native.ReadPtr(buf, 0);
                s.CachePeak = Native.ReadPtr(buf, IntPtr.Size);
                s.CacheWithTransition = Native.ReadPtr(buf, Native.FileCacheTransitionOffset) * s.PageSize;
                s.CacheOk = true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Разбор ядра. Здесь важно различие, которого нет в Диспетчере задач: выгружаемый
        // пул бывает большим, но лежать в памяти может лишь часть его — на схему идёт
        // именно резидентная часть, иначе сумма разложения перестанет сходиться.
        private void RamReadPerf(RamSnapshot s)
        {
            IntPtr buf = Marshal.AllocHGlobal(1024);
            try
            {
                int got;
                if (Native.NtQuerySystemInformation(Native.SystemPerformanceInformation, buf, 1024, out got) != 0) return;
                if (got < Native.PerfMinSize) return;
                long page = s.PageSize;
                s.PagedPoolTotal = Native.ReadU32(buf, Native.PerfPagedPoolPages) * page;
                s.NonPagedPoolTotal = Native.ReadU32(buf, Native.PerfNonPagedPoolPages) * page;
                s.ResidentPagedPool = Native.ReadU32(buf, Native.PerfResidentPagedPoolPage) * page;
                s.ResidentDriver = Native.ReadU32(buf, Native.PerfResidentSystemDriverPage) * page;
                s.ResidentKernelCode = Native.ReadU32(buf, Native.PerfResidentSystemCodePage) * page;
                s.ResidentCache = Native.ReadU32(buf, Native.PerfResidentSystemCachePage) * page;
                s.PageFaults = Native.ReadU32(buf, Native.PerfPageFaultCount);
                s.PageReads = Native.ReadU32(buf, Native.PerfPageReadCount);
                s.HardPageReads = Native.ReadU32(buf, Native.PerfPageReadIoCount);
                s.PerfOk = true;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        // Снимок всех процессов одним вызовом. Process.GetProcesses() делает ровно это же,
        // но потом создаёт объект на каждый процесс и лезет за именем в модули — на шестистах
        // процессах и опросе раз в секунду разница между «5 мс» и «несколько секунд».
        private void RamReadProcesses(RamSnapshot s, RamSnapshot prev)
        {
            if (_ramProcBufSize == 0) { _ramProcBufSize = 1 << 20; _ramProcBuf = Marshal.AllocHGlobal(_ramProcBufSize); }

            int got = 0;
            int rc = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                rc = Native.NtQuerySystemInformation(Native.SystemProcessInformation,
                                                     _ramProcBuf, _ramProcBufSize, out got);
                if ((uint)rc != Native.STATUS_INFO_LENGTH_MISMATCH) break;
                int want = got > 0 ? got + (1 << 18) : _ramProcBufSize * 2;
                Marshal.FreeHGlobal(_ramProcBuf);
                _ramProcBufSize = want;
                _ramProcBuf = Marshal.AllocHGlobal(_ramProcBufSize);
            }
            if (rc != 0) return;

            Dictionary<int, RamProc> before = null;
            double seconds = 0;
            if (prev != null && prev.Procs != null && prev.Procs.Count > 0)
            {
                before = new Dictionary<int, RamProc>(prev.Procs.Count);
                foreach (RamProc p in prev.Procs) before[p.Pid] = p;
                seconds = (s.At - prev.At).TotalSeconds;
                if (seconds <= 0) seconds = 0;
            }

            Native.ProcInfoLayout L = Native.ProcLayout;
            long baseAddr = _ramProcBuf.ToInt64();
            int off = 0;
            long sumPrivate = 0, sumWs = 0, compressed = 0;
            List<RamProc> list = new List<RamProc>(600);

            while (true)
            {
                if (off < 0 || off + 256 > _ramProcBufSize) break;
                IntPtr e = new IntPtr(baseAddr + off);
                int next = Marshal.ReadInt32(e, L.NextEntry);

                RamProc p = new RamProc();
                p.Pid = (int)Native.ReadPtr(e, L.UniqueProcessId);
                p.ParentPid = (int)Native.ReadPtr(e, L.InheritedFromPid);
                p.Threads = Marshal.ReadInt32(e, L.ThreadCount);
                p.Handles = Marshal.ReadInt32(e, L.HandleCount);
                p.SessionId = Marshal.ReadInt32(e, L.SessionId);
                p.Name = Native.ReadImageName(e);
                if (string.IsNullOrEmpty(p.Name))
                    p.Name = p.Pid == 0 ? Tr.S("Простой системы", "System Idle") : "pid " + p.Pid;
                p.PrivateWorkingSet = Marshal.ReadInt64(e, L.WorkingSetPrivate);
                p.WorkingSet = Native.ReadPtr(e, L.WorkingSetSize);
                p.Commit = Native.ReadPtr(e, L.PrivatePageCount);
                p.PagedPool = Native.ReadPtr(e, L.QuotaPagedPoolUsage);
                p.NonPagedPool = Native.ReadPtr(e, L.QuotaNonPagedPoolUsage);
                p.Faults = Native.ReadU32(e, L.PageFaultCount);
                p.HardFaults = Native.ReadU32(e, L.HardFaultCount);
                if (p.PrivateWorkingSet < 0) p.PrivateWorkingSet = 0;

                if (before != null)
                {
                    RamProc old;
                    if (before.TryGetValue(p.Pid, out old) && old.Name == p.Name)
                    {
                        p.Delta = p.PrivateWorkingSet - old.PrivateWorkingSet;
                        if (seconds > 0 && p.HardFaults >= old.HardFaults)
                            p.HardFaultRate = (p.HardFaults - old.HardFaults) / seconds;
                    }
                }

                // Сжатая память — не процесс в обычном смысле: это хранилище, куда ядро
                // складывает сжатые страницы чужих процессов. В «Процессы» её класть нельзя,
                // иначе получится, что 5 ГБ занял безымянный системный процесс.
                if (p.Pid != 0)
                {
                    if (RamIsCompressionStore(p.Name)) compressed += p.WorkingSet;
                    else { sumPrivate += p.PrivateWorkingSet; list.Add(p); }
                    sumWs += p.WorkingSet;
                }

                if (next == 0) break;
                off += next;
            }

            list.Sort(delegate(RamProc a, RamProc b)
            {
                int c = b.PrivateWorkingSet.CompareTo(a.PrivateWorkingSet);
                return c != 0 ? c : a.Pid.CompareTo(b.Pid);
            });

            s.Procs = list;
            s.ProcPrivate = sumPrivate;
            s.ProcWorkingSet = sumWs;
            s.Compressed = compressed;
            long shareable = sumWs - sumPrivate - compressed;
            s.ShareableMax = shareable > 0 ? shareable : 0;
        }

        private static bool RamIsCompressionStore(string name)
        {
            return name != null
                && (name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase)
                 || name.Equals("MemCompression", StringComparison.OrdinalIgnoreCase));
        }

        private static void RamComposeRates(RamSnapshot s, RamSnapshot prev)
        {
            if (prev == null || !prev.PerfOk || !s.PerfOk) return;
            double sec = (s.At - prev.At).TotalSeconds;
            if (sec <= 0.05) return;
            // Счётчики 32-битные и переполняются: отрицательная разница — это переполнение,
            // а не «минус миллион промахов в секунду».
            if (s.PageFaults >= prev.PageFaults) s.FaultsPerSec = (s.PageFaults - prev.PageFaults) / sec;
            if (s.HardPageReads >= prev.HardPageReads) s.HardReadsPerSec = (s.HardPageReads - prev.HardPageReads) / sec;
        }

        // ------------------------------------------------------------------ //
        //  Разложение в схему
        // ------------------------------------------------------------------ //

        private static void RamComposeSlices(RamSnapshot s)
        {
            List<RamSlice> top = new List<RamSlice>();

            // 1. Процессы — с группировкой по имени образа. Двадцать четыре chrome.exe по
            // отдельности не отвечают на вопрос «кто съел память», а одна строка «chrome.exe
            // × 24 — 6,2 ГБ» отвечает; отдельные процессы остаются внутри, на второй уровень.
            RamSlice procs = new RamSlice("procs", Tr.S("Процессы", "Processes"), s.ProcPrivate, RamKind.Process);
            procs.Hint = Tr.S("частные рабочие наборы: память, которую процесс не делит ни с кем",
                              "private working sets: memory a process shares with nobody");
            procs.Children = new List<RamSlice>();
            procs.Pids = new List<int>();

            Dictionary<string, RamSlice> byName = new Dictionary<string, RamSlice>(StringComparer.OrdinalIgnoreCase);
            foreach (RamProc p in s.Procs)
            {
                RamSlice g;
                if (!byName.TryGetValue(p.Name, out g))
                {
                    g = new RamSlice("proc:" + p.Name, p.Name, 0, RamKind.Process);
                    g.Children = new List<RamSlice>();
                    g.Pids = new List<int>();
                    byName[p.Name] = g;
                    procs.Children.Add(g);
                }
                g.Bytes += p.PrivateWorkingSet;
                g.Pids.Add(p.Pid);
                procs.Pids.Add(p.Pid);

                RamSlice leaf = new RamSlice("pid:" + p.Pid, p.Name + "  (pid " + p.Pid + ")",
                                             p.PrivateWorkingSet, RamKind.Process);
                leaf.Pid = p.Pid;
                leaf.Pids = new List<int>();
                leaf.Pids.Add(p.Pid);
                leaf.Hint = Tr.S("рабочий набор ", "working set ") + FormatBytes(p.WorkingSet)
                          + Tr.S(", выделено ", ", committed ") + FormatBytes(p.Commit);
                g.Children.Add(leaf);
            }
            foreach (RamSlice g in procs.Children)
            {
                g.Children.Sort(RamBySize);
                if (g.Children.Count > 1)
                    g.Title = g.Title + "  × " + g.Children.Count;
                g.Hint = Tr.S("процессов: ", "processes: ") + g.Children.Count;
            }
            procs.Children.Sort(RamBySize);
            top.Add(procs);

            RamAdd(top, "compressed", Tr.S("Сжатая память", "Compressed memory"), s.Compressed, RamKind.Compressed,
                   Tr.S("страницы других процессов, сжатые ядром вместо выгрузки на диск",
                        "pages of other processes the kernel compressed instead of paging out"));

            RamAdd(top, "nonpaged", Tr.S("Невыгружаемый пул", "Non-paged pool"), s.NonPagedPoolTotal, RamKind.NonPagedPool,
                   Tr.S("память драйверов, которую нельзя выгрузить на диск никогда",
                        "driver memory that can never be paged out"));

            RamAdd(top, "pagedres", Tr.S("Выгружаемый пул (в памяти)", "Paged pool (resident)"), s.ResidentPagedPool, RamKind.PagedPool,
                   Tr.S("резидентная часть выгружаемого пула; всего пула — ", "the resident part of the paged pool; total pool — ")
                   + FormatBytes(s.PagedPoolTotal));

            RamAdd(top, "kcode", Tr.S("Код ядра и драйверов", "Kernel and driver code"),
                   s.ResidentKernelCode + s.ResidentDriver, RamKind.KernelCode,
                   Tr.S("исполняемый код самой Windows и загруженных драйверов",
                        "the executable code of Windows itself and of the loaded drivers"));

            RamAdd(top, "syscache", Tr.S("Системный кэш (в памяти)", "System cache (resident)"), s.ResidentCache, RamKind.SystemCache,
                   Tr.S("рабочий набор файлового кэша; вместе с ожиданием — ", "the file cache working set; together with standby — ")
                   + FormatBytes(s.CacheWithTransition));

            // Остаток активной памяти. Он бывает большим, и это не ошибка счёта — см. шапку файла.
            long known = s.ProcPrivate + s.Compressed + s.NonPagedPoolTotal + s.ResidentPagedPool
                       + s.ResidentKernelCode + s.ResidentDriver + s.ResidentCache;
            long rest = s.Active - known;
            s.Unattributed = rest > 0 ? rest : 0;
            RamAdd(top, "other", Tr.S("Не отнесено: драйверы, ВМ, общие страницы", "Unattributed: drivers, VMs, shared pages"),
                   s.Unattributed, RamKind.Unattributed,
                   Tr.S("страницы, заблокированные драйверами и виртуальными машинами (WSL, Hyper-V, Docker), таблицы страниц, общие DLL и отображённые файлы. Общих здесь не больше ",
                        "pages locked by drivers and virtual machines (WSL, Hyper-V, Docker), page tables, shared DLLs and mapped files. Of these, shared pages are at most ")
                   + FormatBytes(s.ShareableMax));

            // Ожидание — по восьми приоритетам: ядро отдаёт нулевой приоритет первым, а
            // седьмой держит до последнего, поэтому «сколько там всего» — не весь ответ.
            RamSlice standby = new RamSlice("standby", Tr.S("Ожидание (кэш)", "Standby (cache)"), s.Standby, RamKind.Standby);
            standby.Hint = Tr.S("данные с диска, оставленные про запас; освобождаются мгновенно, но перечитывать придётся заново",
                                "data kept from disk just in case; released instantly, but has to be re-read afterwards");
            standby.Children = new List<RamSlice>();
            for (int i = 0; i < 8; i++)
            {
                if (s.StandbyByPriority[i] <= 0) continue;
                RamSlice pr = new RamSlice("standby:" + i,
                    Tr.S("Приоритет ", "Priority ") + i, s.StandbyByPriority[i], RamKind.Standby);
                pr.Hint = i == 0 ? Tr.S("отдаётся первым — самые бесполезные страницы", "released first — the least useful pages")
                                 : Tr.S("чем выше приоритет, тем дольше страница живёт в кэше", "the higher the priority, the longer the page stays cached");
                standby.Children.Add(pr);
            }
            standby.Children.Sort(RamBySize);
            if (standby.Bytes > 0) top.Add(standby);

            RamAdd(top, "modified", Tr.S("Изменённые", "Modified"), s.Modified + s.ModifiedNoWrite, RamKind.Modified,
                   Tr.S("изменённые страницы, ещё не записанные на диск; освобождаются только после записи",
                        "changed pages not yet written to disk; released only after they are written"));

            RamAdd(top, "free", Tr.S("Свободно", "Free"), s.FreePages + s.Zeroed, RamKind.Free,
                   Tr.S("свободные и обнулённые страницы", "free and zeroed pages"));

            RamAdd(top, "reserved", Tr.S("Аппаратно зарезервировано", "Hardware reserved"), s.HardwareReserved, RamKind.Reserved,
                   Tr.S("забрало железо и прошивка; операционной системе эта память не видна вовсе",
                        "taken by hardware and firmware; the operating system never sees this memory"));

            RamAdd(top, "bad", Tr.S("Плохие блоки", "Bad pages"), s.Bad, RamKind.Bad,
                   Tr.S("страницы, помеченные как сбойные", "pages marked as faulty"));

            s.Slices = top;
        }

        private static int RamBySize(RamSlice a, RamSlice b)
        {
            int c = b.Bytes.CompareTo(a.Bytes);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        }

        private static void RamAdd(List<RamSlice> to, string key, string title, long bytes, int kind, string hint)
        {
            if (bytes <= 0) return;
            RamSlice s = new RamSlice(key, title, bytes, kind);
            s.Hint = hint;
            to.Add(s);
        }

        // ------------------------------------------------------------------ //
        //  Железо: планки и физические диапазоны
        // ------------------------------------------------------------------ //

        public List<RamModule> RamModules()
        {
            if (_ramModules != null) return _ramModules;
            List<RamModule> list = new List<RamModule>();
            try
            {
                byte[] b = Native.ReadSmbios();
                if (b != null && b.Length > 8)
                {
                    int len = BitConverter.ToInt32(b, 4);
                    int end = Math.Min(b.Length, 8 + Math.Max(0, len));
                    int p = 8;
                    while (p + 4 <= end)
                    {
                        byte type = b[p];
                        int hdr = b[p + 1];
                        if (hdr < 4 || p + hdr > end) break;
                        int strings = p + hdr;
                        if (type == 17) RamReadModule(b, p, hdr, strings, list);
                        // тело кончилось — дальше строки до двух нулей подряд
                        int q = strings;
                        while (q + 1 < end && !(b[q] == 0 && b[q + 1] == 0)) q++;
                        p = q + 2;
                        if (type == 127) break;                     // End-of-table
                    }
                }
            }
            catch { }
            _ramModules = list;
            return list;
        }

        private static void RamReadModule(byte[] b, int p, int hdr, int strings, List<RamModule> list)
        {
            if (p + 0x0E > b.Length) return;
            long bytes;
            int size = BitConverter.ToUInt16(b, p + 0x0C);
            if (size == 0) return;                                   // слот пуст
            if (size == 0x7FFF)
            {
                if (p + 0x20 > b.Length || hdr < 0x20) return;
                bytes = (long)BitConverter.ToUInt32(b, p + 0x1C) * 1024L * 1024L;
            }
            else if ((size & 0x8000) != 0) bytes = (long)(size & 0x7FFF) * 1024L;   // бит 15 — величина в КБ
            else bytes = (long)size * 1024L * 1024L;
            if (bytes <= 0) return;

            RamModule m = new RamModule();
            m.Bytes = bytes;
            if (hdr > 0x10) m.Slot = Native.SmbiosString(b, strings, b[p + 0x10]);
            if (hdr > 0x11) m.Bank = Native.SmbiosString(b, strings, b[p + 0x11]);
            if (hdr > 0x12) m.Kind = RamMemoryTypeName(b[p + 0x12]);
            if (hdr > 0x16) m.Speed = BitConverter.ToUInt16(b, p + 0x15);
            if (hdr > 0x17) m.Maker = Native.SmbiosString(b, strings, b[p + 0x17]);
            if (hdr > 0x1A) m.Part = Native.SmbiosString(b, strings, b[p + 0x1A]);
            if (hdr > 0x21) m.ConfiguredSpeed = BitConverter.ToUInt16(b, p + 0x20);
            list.Add(m);
        }

        private static string RamMemoryTypeName(byte t)
        {
            switch (t)
            {
                case 0x12: return "DDR";
                case 0x13: return "DDR2";
                case 0x18: return "DDR3";
                case 0x1A: return "DDR4";
                case 0x1B: return "LPDDR";
                case 0x1C: return "LPDDR2";
                case 0x1D: return "LPDDR3";
                case 0x1E: return "LPDDR4";
                case 0x22: return "DDR5";
                case 0x23: return "LPDDR5";
            }
            return Tr.S("тип ", "type ") + t;
        }

        public long RamInstalledBytes()
        {
            long total = 0;
            foreach (RamModule m in RamModules()) total += m.Bytes;
            return total;
        }

        // Физические диапазоны: что именно из адресного пространства отдано под ОЗУ.
        // Их сумма совпадает с TotalPhys, а разница с суммой планок — это и есть то,
        // что забрало железо.
        public List<RamRange> RamRanges()
        {
            if (_ramRanges != null) return _ramRanges;
            List<RamRange> list = new List<RamRange>();
            try
            {
                byte[] b = Native.ReadRawRegistryValue(
                    @"HARDWARE\RESOURCEMAP\System Resources\Physical Memory", ".Translated");
                if (b != null && b.Length >= 4) RamParseResourceList(b, list);
            }
            catch { }
            list.Sort(delegate(RamRange x, RamRange y) { return x.Start.CompareTo(y.Start); });
            _ramRanges = list;
            return list;
        }

        internal static void RamParseResourceList(byte[] b, List<RamRange> list)
        {
            int p = 0;
            int full = BitConverter.ToInt32(b, p); p += 4;
            if (full < 0 || full > 64) return;
            for (int i = 0; i < full; i++)
            {
                if (p + 16 > b.Length) return;
                p += 8;                                  // InterfaceType + BusNumber
                p += 4;                                  // Version + Revision
                int count = BitConverter.ToInt32(b, p); p += 4;
                if (count < 0 || count > 4096) return;
                for (int j = 0; j < count; j++)
                {
                    if (p + Native.ResourceDescriptorStride > b.Length) return;
                    byte type = b[p];
                    ushort flags = BitConverter.ToUInt16(b, p + 2);
                    long start = BitConverter.ToInt64(b, p + 4);
                    long len = (long)BitConverter.ToUInt32(b, p + 12);
                    if (type == Native.CmResourceTypeMemoryLarge)
                    {
                        if ((flags & Native.CM_RESOURCE_MEMORY_LARGE_40) != 0) len <<= 8;
                        else if ((flags & Native.CM_RESOURCE_MEMORY_LARGE_48) != 0) len <<= 16;
                        else if ((flags & Native.CM_RESOURCE_MEMORY_LARGE_64) != 0) len <<= 32;
                    }
                    if ((type == Native.CmResourceTypeMemory || type == Native.CmResourceTypeMemoryLarge)
                        && len > 0 && start >= 0)
                    {
                        RamRange r = new RamRange();
                        r.Start = start;
                        r.Length = len;
                        list.Add(r);
                    }
                    p += Native.ResourceDescriptorStride;
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Теги пула: кто из драйверов держит невыгружаемую память
        // ------------------------------------------------------------------ //

        public List<RamPool> RamPools()
        {
            List<RamPool> list = new List<RamPool>();
            int size = 1 << 20;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int got;
                    int rc = Native.NtQuerySystemInformation(Native.SystemPoolTagInformation, buf, size, out got);
                    if ((uint)rc == Native.STATUS_INFO_LENGTH_MISMATCH)
                    {
                        size = got > 0 ? got + (1 << 16) : size * 2;
                        continue;
                    }
                    if (rc != 0) return list;

                    int count = Marshal.ReadInt32(buf, 0);
                    int head = Native.PoolTagHead, entry = Native.PoolTagEntry;
                    if (count < 0 || head + (long)count * entry > size) return list;
                    byte[] tag = new byte[4];
                    for (int i = 0; i < count; i++)
                    {
                        int o = head + i * entry;
                        Marshal.Copy(new IntPtr(buf.ToInt64() + o), tag, 0, 4);
                        RamPool pl = new RamPool();
                        pl.Tag = RamTagText(tag);
                        pl.PagedAllocs = Native.ReadU32(buf, o + 4) - Native.ReadU32(buf, o + 8);
                        pl.Paged = Native.ReadPtr(buf, o + Native.PoolTagPagedUsed);
                        int na = Native.PoolTagPagedUsed + IntPtr.Size;
                        pl.NonPagedAllocs = Native.ReadU32(buf, o + na) - Native.ReadU32(buf, o + na + 4);
                        pl.NonPaged = Native.ReadPtr(buf, o + Native.PoolTagNonPagedUsed);
                        if (pl.Paged < 0) pl.Paged = 0;
                        if (pl.NonPaged < 0) pl.NonPaged = 0;
                        if (pl.Total > 0) list.Add(pl);
                    }
                    list.Sort(delegate(RamPool a, RamPool b) { return b.Total.CompareTo(a.Total); });
                    return list;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return list;
        }

        private static string RamTagText(byte[] t)
        {
            char[] c = new char[4];
            for (int i = 0; i < 4; i++) c[i] = (t[i] >= 32 && t[i] < 127) ? (char)t[i] : ' ';
            string s = new string(c).TrimEnd();
            return s.Length == 0 ? "?" : s;
        }

        // ------------------------------------------------------------------ //
        //  Сбросы
        // ------------------------------------------------------------------ //

        // Одна команда ядра. Права нужны все три: без них вызов вернёт 0xC0000061, и это
        // единственный честный ответ — «не сделано», а не «сделано, но ничего не изменилось».
        public RamAction RamRunEmpty(int what)
        {
            RamAction r = new RamAction();
            if (!RamCommandKnown(what))
            {
                r.Message = Tr.S("неизвестная команда сброса", "unknown reset command");
                return r;
            }

            long before = RamAvailNow();
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");

            int rc = 0;
            if (what == RamEmptyEverything)
            {
                // Порядок важен: сначала выдавить рабочие наборы (страницы уйдут в standby и
                // modified), затем записать изменённые, и только потом чистить standby —
                // иначе только что выдавленное осядет в кэше и «освободилось» будет меньше.
                rc = RamSetMemoryList(Native.MemoryEmptyWorkingSets);
                if (rc == 0) RamEmptySystemCache();
                if (rc == 0) rc = RamSetMemoryList(Native.MemoryFlushModifiedList);
                Thread.Sleep(400);
                if (rc == 0) rc = RamSetMemoryList(Native.MemoryPurgeStandbyList);
            }
            else if (what == RamEmptySystemWorkingSet) rc = RamEmptySystemCache();
            else rc = RamSetMemoryList(what);

            // Списки чистятся асинхронно: замер «сразу после» показывает меньше, чем есть.
            Thread.Sleep(what == RamFlushModified || what == RamEmptyEverything ? 700 : 300);
            long after = RamAvailNow();
            long freed = after - before;
            r.Freed = freed > 0 ? freed : 0;

            if (rc == 0)
            {
                r.Ok = true;
                r.Count = 1;
                r.Message = Tr.S("готово: ", "done: ") + RamCommandTitle(what);
            }
            else if ((uint)rc == Native.STATUS_PRIVILEGE_NOT_HELD || (uint)rc == Native.STATUS_ACCESS_DENIED)
                r.Message = Tr.S("нужны права администратора", "administrator rights are required");
            else
                r.Message = "NtSetSystemInformation 0x" + ((uint)rc).ToString("X8", CultureInfo.InvariantCulture);
            return r;
        }

        private int RamSetMemoryList(int command)
        {
            IntPtr p = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(p, command);
                return Native.NtSetSystemInformation(Native.SystemMemoryListInformation, p, sizeof(int));
            }
            finally { Marshal.FreeHGlobal(p); }
        }

        // «Сбросить системный рабочий набор» = записать в SystemFileCacheInformation минимум
        // и максимум, равные −1. Структура сначала читается и меняется на месте: писать в
        // ядро наполовину заполненную структуру нельзя, остальные поля должны быть свои.
        private int RamEmptySystemCache()
        {
            int size = Native.FileCacheSize;
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                int got;
                Native.NtQuerySystemInformation(Native.SystemFileCacheInformation, buf, size, out got);
                int min = Native.FileCacheMinOffset;
                if (IntPtr.Size == 8)
                {
                    Marshal.WriteInt64(buf, min, -1L);
                    Marshal.WriteInt64(buf, min + 8, -1L);
                }
                else
                {
                    Marshal.WriteInt32(buf, min, -1);
                    Marshal.WriteInt32(buf, min + 4, -1);
                }
                return Native.NtSetSystemInformation(Native.SystemFileCacheInformation, buf, size);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        private static long RamAvailNow()
        {
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            return Native.GlobalMemoryStatusEx(ref m) ? (long)m.ullAvailPhys : 0;
        }

        // Сброс рабочих наборов ПЕРЕЧИСЛЕННЫХ процессов. Для процессов того же пользователя
        // прав не нужно вовсе; на процессы сессии 0 система отвечает отказом — их считаем
        // отдельно, чтобы окно могло предложить помощника, а не соврать «готово».
        public RamAction RamTrimProcesses(IList<int> pids)
        {
            RamAction r = new RamAction();
            if (pids == null || pids.Count == 0)
            {
                r.Message = Tr.S("не выбрано ни одного процесса", "no processes selected");
                return r;
            }

            long before = RamAvailNow();
            int self = 0;
            try { self = System.Diagnostics.Process.GetCurrentProcess().Id; } catch { }

            foreach (int pid in pids)
            {
                if (pid <= 0 || pid == self) continue;
                IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
                if (h == IntPtr.Zero)
                    h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION | Native.PROCESS_SET_QUOTA, false, pid);
                if (h == IntPtr.Zero) { r.Denied++; continue; }
                try { if (Native.EmptyWorkingSet(h)) r.Count++; else r.Denied++; }
                finally { Native.CloseHandle(h); }
            }

            Thread.Sleep(250);
            // Прирост «доступно» за четверть секунды — величина приблизительная: за это же
            // время память отдают и берут все остальные процессы. Поэтому она называется
            // числом только тогда, когда мы действительно что-то сбросили: сказать
            // «освобождено 39 МБ» после нуля успешных сбросов — это соврать чужим шумом.
            long freed = RamAvailNow() - before;
            r.Freed = r.Count > 0 && freed > 0 ? freed : 0;
            r.Ok = r.Count > 0;
            r.Message = r.Count > 0
                ? Tr.S("сброшено рабочих наборов: ", "working sets emptied: ") + r.Count
                  + (r.Denied > 0 ? Tr.S(", отказано: ", ", denied: ") + r.Denied : "")
                : Tr.S("ни один рабочий набор сбросить не удалось", "not a single working set could be emptied");
            return r;
        }
    }
}
