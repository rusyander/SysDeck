// SysDeck — вкладка «Видеопамять»: что занято на видеокарте и кем.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Разложение устроено так же, как у оперативной памяти, и обещает то же: сумма блоков схемы
// равна объёму памяти видеокарты. Две схемы на каждую карту:
//
//   Выделенная (VRAM, объём по DXGI)          Общая (часть ОЗУ, которую Windows отдаёт видеокарте)
//    ├─ Процессы  = Σ min(Dedicated, Local)    ├─ Процессы = Σ Shared Usage
//    ├─ Система и драйвер = занято − процессы  ├─ Система и драйвер
//    └─ Свободно                               └─ Свободно
//
// Почему min(Dedicated, Local), а не просто Dedicated Usage процесса: этот счётчик завышен.
// Процесс, открывший чужую общую поверхность, получает её объём себе целиком, поэтому у dwm.exe
// «Dedicated» 1,9 ГБ при 1,35 ГБ занятых на всей карте, а у NVIDIA Overlay и вовсе 172 ГБ на
// карте в 24 ГБ (замерено 2026-09-13). «Local Usage» — то, что процесс держит резидентно в
// локальной памяти карты. На дискретной карте local ≤ dedicated и сумма local по процессам
// сходится с занятым на карте (1266 из 1352 МБ, остаток — драйвер); на встроенной локальная
// память включает и общую, и там меньше как раз dedicated (dwm: 187 из 693 МБ при 172 на карте).
// Минимум из двух верен в обоих случаях, без угадывания, встроенная карта или нет.
//
// Сбросы — только те, что не вредят работе:
//  * перезапуск GPU-процесса Chromium/Electron (Chrome, Edge, Яндекс, VS Code, Figma, Discord…):
//    приложение само поднимает его заново, вкладки и окна остаются. Не чаще раза в три минуты
//    на приложение: после нескольких падений GPU-процесса подряд Chromium выключает аппаратное
//    ускорение до перезапуска браузера — и это было бы хуже, чем занятая память;
//  * завершение выбранного процесса — тем же путём, что на вкладке «Память»; системные процессы
//    (dwm.exe, csrss.exe…) не завершаются вовсе;
//  * перезапуск видеодрайвера — Win+Ctrl+Shift+B, как нажал бы человек. Прав не требует,
//    экран гаснет на секунду; 3D-приложения при этом могут закрыться, поэтому окно их называет.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace SysDeck
{
    // Виды блоков схемы видеопамяти. Номера не пересекаются с RamKind: блоки обеих вкладок —
    // один и тот же RamSlice, и цвет выбирается по виду.
    public static class GpuKind
    {
        public const int Group = 100;
        public const int Process = 101;
        public const int Helper = 102;        // GPU-процесс Chromium/Electron
        public const int System = 103;
        public const int Free = 104;
    }

    public class GpuProc
    {
        public int Pid;
        public int ParentPid;
        public string Name;
        public string Luid;
        public long Dedicated;        // min(Dedicated Usage, Local Usage) — см. шапку файла
        public long DedicatedRaw;     // как отдаёт счётчик
        public long Local;
        public long Shared;
        public long Committed;
        public double Load;           // %, по самому загруженному типу движка
        public string EngineType;     // 3D, VideoDecode, Compute…
        public bool Helper;           // GPU-процесс Chromium/Electron — можно перезапустить
        public bool Protected;        // системный — завершать нельзя
        public bool Inflated;         // Dedicated Usage больше всей памяти карты
    }

    public class GpuAdapter
    {
        public string Luid;
        public string Name;
        public int VendorId;
        public bool Known;            // DXGI знает карту, и объём памяти настоящий
        public long DedicatedTotal, SharedTotal;
        public long DedicatedUsed, SharedUsed, Committed;
        public long ProcDedicated, ProcShared;
        public double Load;
        public string LoadEngine;
        public int ProcCount;
        public bool Scaled;           // сумма по процессам превысила объём и была ужата
        public List<RamSlice> DedicatedSlices = new List<RamSlice>();
        public List<RamSlice> SharedSlices = new List<RamSlice>();

        public string Vendor
        {
            get
            {
                switch (VendorId)
                {
                    case 0x10DE: return "NVIDIA";
                    case 0x1002: case 0x1022: return "AMD";
                    case 0x8086: case 0x8087: return "Intel";
                    case 0x5143: return "Qualcomm";
                    case 0x1414: return "Microsoft";
                    case 0x15AD: return "VMware";
                    case 0x80EE: return "VirtualBox";
                }
                return VendorId == 0 ? "" : "0x" + VendorId.ToString("X4");
            }
        }
    }

    public class GpuSnapshot
    {
        public DateTime At;
        public bool Ok;
        public string Error;
        public List<GpuAdapter> Adapters = new List<GpuAdapter>();
        public List<GpuProc> Procs = new List<GpuProc>();

        public GpuAdapter Find(string luid)
        {
            foreach (GpuAdapter a in Adapters)
                if (string.Equals(a.Luid, luid, StringComparison.OrdinalIgnoreCase)) return a;
            return null;
        }

        // Карта по умолчанию — с наибольшей выделенной памятью, то есть дискретная (RTX 4090 рядом со встроенной
        // AMD), а не первая в списке DXGI. При равенстве — первая.
        public GpuAdapter Main()
        {
            GpuAdapter best = null;
            foreach (GpuAdapter a in Adapters)
                if (best == null || a.DedicatedTotal > best.DedicatedTotal) best = a;
            return best;
        }
    }

    public class GpuAction
    {
        public bool Ok;
        public bool Refused;          // отказано до всякого действия (охрана, пауза между сбросами)
        public int NewPid;
        public string Message;
    }

    public partial class Engine
    {
        public const int GpuHelperCooldownSeconds = 180;
        public const int GpuDriverCooldownSeconds = 60;

        private readonly object _gpuLock = new object();
        private IntPtr _gpuQuery = IntPtr.Zero;
        private IntPtr _gpuPDed, _gpuPLocal, _gpuPShared, _gpuPCommit, _gpuADed, _gpuAShared, _gpuACommit, _gpuEngine;
        private string _gpuQueryError;
        private List<DxgiAdapter> _gpuDxgi;
        private DateTime _gpuDxgiAt = DateTime.MinValue;
        // pid → «имя|признак». Командная строка процесса за его жизнь не меняется, а читать её
        // раз в секунду у каждого процесса с видеопамятью незачем. Имя в ключе ловит pid, который
        // успел достаться другому процессу.
        private readonly Dictionary<int, string> _gpuHelperCache = new Dictionary<int, string>();
        private readonly Dictionary<string, DateTime> _gpuHelperRestartAt = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private DateTime _gpuDriverResetAt = DateTime.MinValue;

        // ------------------------------------------------------------------ //
        //  Замер
        // ------------------------------------------------------------------ //

        public GpuSnapshot GpuSample()
        {
            GpuSnapshot s = new GpuSnapshot();
            s.At = DateTime.UtcNow;
            lock (_gpuLock)
            {
                if (!GpuEnsureQuery())
                {
                    s.Error = _gpuQueryError;
                    return s;
                }
                int st = Native.PdhCollectQueryData(_gpuQuery);
                if (st != 0)
                {
                    s.Error = "PDH 0x" + st.ToString("X8", CultureInfo.InvariantCulture);
                    return s;
                }
                GpuRead(s);
            }
            s.Ok = true;
            return s;
        }

        private bool GpuEnsureQuery()
        {
            if (_gpuQuery != IntPtr.Zero) return true;
            if (_gpuQueryError != null) return false;           // один раз не вышло — не долбим каждую секунду

            IntPtr q;
            int st = Native.PdhOpenQueryW(null, IntPtr.Zero, out q);
            if (st != 0 || q == IntPtr.Zero)
            {
                _gpuQueryError = Tr.S("Не удалось открыть счётчики производительности: ", "Could not open the performance counters: ")
                               + "0x" + st.ToString("X8", CultureInfo.InvariantCulture);
                return false;
            }
            // Без памяти процессов вкладке показывать нечего — эти четыре обязательны. Загрузка
            // движков — желательна: её нет на некоторых виртуальных адаптерах.
            bool ok = GpuAdd(q, @"\GPU Process Memory(*)\Dedicated Usage", out _gpuPDed)
                    & GpuAdd(q, @"\GPU Process Memory(*)\Local Usage", out _gpuPLocal)
                    & GpuAdd(q, @"\GPU Process Memory(*)\Shared Usage", out _gpuPShared)
                    & GpuAdd(q, @"\GPU Adapter Memory(*)\Dedicated Usage", out _gpuADed)
                    & GpuAdd(q, @"\GPU Adapter Memory(*)\Shared Usage", out _gpuAShared);
            GpuAdd(q, @"\GPU Process Memory(*)\Total Committed", out _gpuPCommit);
            GpuAdd(q, @"\GPU Adapter Memory(*)\Total Committed", out _gpuACommit);
            GpuAdd(q, @"\GPU Engine(*)\Utilization Percentage", out _gpuEngine);
            if (!ok)
            {
                Native.PdhCloseQuery(q);
                _gpuQueryError = Tr.S("Windows не отдаёт счётчики видеопамяти (нужна Windows 10 1709 или новее и драйвер WDDM 2.0+).",
                                      "Windows does not provide the video memory counters (Windows 10 1709 or newer with a WDDM 2.0+ driver is required).");
                return false;
            }
            _gpuQuery = q;
            // Загрузка — счётчик скорости: первое чтение пустое, значение появляется со второго.
            Native.PdhCollectQueryData(q);
            return true;
        }

        private static bool GpuAdd(IntPtr query, string path, out IntPtr counter)
        {
            return Native.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter) == 0 && counter != IntPtr.Zero;
        }

        // Список карт перечитывается раз в 30 секунд: подключение внешней видеокарты или смена
        // драйвера за это время заметны, а DXGI-фабрика раз в секунду — пустая трата.
        private List<DxgiAdapter> GpuDxgiCached()
        {
            DateTime now = DateTime.UtcNow;
            if (_gpuDxgi == null || (now - _gpuDxgiAt).TotalSeconds > 30)
            {
                try { _gpuDxgi = Native.DxgiAdapters(); }
                catch { if (_gpuDxgi == null) _gpuDxgi = new List<DxgiAdapter>(); }
                _gpuDxgiAt = now;
            }
            return _gpuDxgi;
        }

        private void GpuRead(GpuSnapshot s)
        {
            Dictionary<string, double> pDed = Native.PdhReadAll(_gpuPDed, false);
            Dictionary<string, double> pLocal = Native.PdhReadAll(_gpuPLocal, false);
            Dictionary<string, double> pShared = Native.PdhReadAll(_gpuPShared, false);
            Dictionary<string, double> pCommit = Native.PdhReadAll(_gpuPCommit, false);
            Dictionary<string, double> aDed = Native.PdhReadAll(_gpuADed, false);
            Dictionary<string, double> aShared = Native.PdhReadAll(_gpuAShared, false);
            Dictionary<string, double> aCommit = Native.PdhReadAll(_gpuACommit, false);
            Dictionary<string, double> eng = Native.PdhReadAll(_gpuEngine, true);

            // --- карты ---
            Dictionary<string, GpuAdapter> adapters = new Dictionary<string, GpuAdapter>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> software = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DxgiAdapter d in GpuDxgiCached())
            {
                // Microsoft Basic Render Driver — программный рендер на процессоре, своей памяти
                // у него нет, и в списке «видеокарт» он только путает.
                if (d.Software) { software.Add(d.Luid); continue; }
                if (adapters.ContainsKey(d.Luid)) continue;
                GpuAdapter a = new GpuAdapter();
                a.Luid = d.Luid;
                a.Name = d.Name;
                a.VendorId = d.VendorId;
                a.Known = true;
                a.DedicatedTotal = d.Dedicated;
                a.SharedTotal = d.Shared;
                adapters[d.Luid] = a;
            }
            GpuSumAdapter(aDed, adapters, software, 0);
            GpuSumAdapter(aShared, adapters, software, 1);
            GpuSumAdapter(aCommit, adapters, software, 2);

            // --- процессы ---
            Dictionary<string, GpuProc> procs = new Dictionary<string, GpuProc>(StringComparer.OrdinalIgnoreCase);
            GpuSumProc(pDed, procs, 0);
            GpuSumProc(pLocal, procs, 1);
            GpuSumProc(pShared, procs, 2);
            GpuSumProc(pCommit, procs, 3);

            // --- загрузка: сумма по экземплярам одного типа движка, затем максимум по типам.
            // Так считает и Диспетчер задач: у видеокарты несколько «3D»-узлов, и 40 % на одном
            // плюс 40 % на другом — это 80 % работы 3D, а не два разных числа.
            Dictionary<string, double> procType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> adapterType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, double> kv in eng)
            {
                int pid; string luid, type;
                if (!GpuParseInstance(kv.Key, out pid, out luid, out type) || type == null) continue;
                double v = Math.Max(0, Math.Min(100, kv.Value));
                if (v <= 0) continue;
                GpuAddTo(adapterType, luid + "|" + type, v);
                if (pid > 0) GpuAddTo(procType, pid + "|" + luid + "|" + type, v);
            }
            foreach (KeyValuePair<string, double> kv in adapterType)
            {
                string[] parts = kv.Key.Split('|');
                GpuAdapter a;
                if (!adapters.TryGetValue(parts[0], out a)) continue;
                double v = Math.Min(100, kv.Value);
                if (v > a.Load) { a.Load = v; a.LoadEngine = parts[1]; }
            }
            foreach (KeyValuePair<string, double> kv in procType)
            {
                string[] parts = kv.Key.Split('|');
                GpuProc p;
                if (!procs.TryGetValue(parts[0] + "|" + parts[1], out p)) continue;
                double v = Math.Min(100, kv.Value);
                if (v > p.Load) { p.Load = v; p.EngineType = parts[2]; }
            }

            // Есть ли у карты Local Usage вообще. На старых сборках счётчик может молчать —
            // тогда min() обнулил бы всех, и схема показала бы пустую карту.
            HashSet<string> hasLocal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuProc p in procs.Values) if (p.Local > 0) hasLocal.Add(p.Luid);

            Dictionary<int, RawProc> names = new Dictionary<int, RawProc>();
            try { foreach (RawProc r in Snapshot()) names[r.Pid] = r; }
            catch { }

            HashSet<int> alive = new HashSet<int>();
            foreach (GpuProc p in procs.Values)
            {
                GpuAdapter a;
                if (software.Contains(p.Luid) || !adapters.TryGetValue(p.Luid, out a)) continue;
                p.Dedicated = GpuAttribute(p.DedicatedRaw, p.Local, hasLocal.Contains(p.Luid));
                p.Inflated = a.Known && a.DedicatedTotal > 0 && p.DedicatedRaw > a.DedicatedTotal;
                if (p.Dedicated <= 0 && p.Shared <= 0 && p.Load < 0.1) continue;

                RawProc rp, parent = null;
                if (names.TryGetValue(p.Pid, out rp))
                {
                    p.Name = rp.Name;
                    p.ParentPid = rp.Ppid;
                    names.TryGetValue(rp.Ppid, out parent);
                }
                else p.Name = "pid " + p.Pid.ToString(CultureInfo.InvariantCulture);
                p.Protected = p.Pid <= 4 || p.Pid == _selfPid || GpuIsProtectedName(p.Name);
                p.Helper = GpuIsHelper(p.Pid, rp, parent);
                alive.Add(p.Pid);
                s.Procs.Add(p);
            }

            // Кэш командных строк — только за живыми процессами с видеопамятью.
            List<int> stale = new List<int>();
            foreach (int pid in _gpuHelperCache.Keys) if (!alive.Contains(pid)) stale.Add(pid);
            foreach (int pid in stale) _gpuHelperCache.Remove(pid);

            foreach (GpuAdapter a in adapters.Values)
            {
                List<GpuProc> mine = new List<GpuProc>();
                foreach (GpuProc p in s.Procs)
                    if (string.Equals(p.Luid, a.Luid, StringComparison.OrdinalIgnoreCase)) mine.Add(p);
                GpuCompose(a, mine);
                s.Adapters.Add(a);
            }
            // Дискретная карта с большой памятью — первой: на неё смотрят в первую очередь.
            s.Adapters.Sort(delegate(GpuAdapter x, GpuAdapter y)
            {
                int r = y.DedicatedTotal.CompareTo(x.DedicatedTotal);
                return r != 0 ? r : string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
            });
        }

        private static void GpuAddTo(Dictionary<string, double> map, string key, double v)
        {
            double prev;
            map[key] = map.TryGetValue(key, out prev) ? prev + v : v;
        }

        private static void GpuSumAdapter(Dictionary<string, double> values, Dictionary<string, GpuAdapter> adapters,
                                          HashSet<string> software, int field)
        {
            foreach (KeyValuePair<string, double> kv in values)
            {
                int pid; string luid, type;
                if (!GpuParseInstance(kv.Key, out pid, out luid, out type) || pid != 0 || software.Contains(luid)) continue;
                GpuAdapter a;
                if (!adapters.TryGetValue(luid, out a))
                {
                    // Карта, которой нет в DXGI (вычислительный ускоритель, удалённый адаптер):
                    // объём неизвестен, схема строится по занятому.
                    a = new GpuAdapter();
                    a.Luid = luid;
                    a.Name = Tr.S("Видеоадаптер ", "Graphics adapter ") + luid;
                    adapters[luid] = a;
                }
                long v = (long)kv.Value;
                if (field == 0) a.DedicatedUsed += v;
                else if (field == 1) a.SharedUsed += v;
                else a.Committed += v;
            }
        }

        private static void GpuSumProc(Dictionary<string, double> values, Dictionary<string, GpuProc> procs, int field)
        {
            foreach (KeyValuePair<string, double> kv in values)
            {
                int pid; string luid, type;
                if (!GpuParseInstance(kv.Key, out pid, out luid, out type) || pid <= 0) continue;
                string key = pid + "|" + luid;
                GpuProc p;
                if (!procs.TryGetValue(key, out p))
                {
                    p = new GpuProc();
                    p.Pid = pid;
                    p.Luid = luid;
                    procs[key] = p;
                }
                long v = (long)kv.Value;
                switch (field)
                {
                    case 0: p.DedicatedRaw += v; break;
                    case 1: p.Local += v; break;
                    case 2: p.Shared += v; break;
                    default: p.Committed += v; break;
                }
            }
        }

        // «pid_2444_luid_0x00000000_0x00014E6A_phys_0», «luid_0x00000000_0x00014E6A_phys_0» и
        // «pid_1956_luid_0x00000000_0x00014E6A_phys_0_eng_13_engtype_Copy». pid = 0 — экземпляр
        // карты целиком. LUID приводится к виду Native.LuidKey: PDH пишет его заглавными, а
        // регистр в ключах словарей не должен решать, сойдутся ли карта и её процессы.
        internal static bool GpuParseInstance(string name, out int pid, out string luid, out string engineType)
        {
            pid = 0; luid = null; engineType = null;
            if (string.IsNullOrEmpty(name)) return false;
            int p = 0;
            if (name.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            {
                int end = name.IndexOf('_', 4);
                if (end < 0 || !int.TryParse(name.Substring(4, end - 4), NumberStyles.None, CultureInfo.InvariantCulture, out pid)) return false;
                p = end + 1;
            }
            if (string.Compare(name, p, "luid_0x", 0, 7, StringComparison.OrdinalIgnoreCase) != 0) { pid = 0; return false; }
            p += 7;
            int sep = name.IndexOf("_0x", p, StringComparison.OrdinalIgnoreCase);
            if (sep < 0) { pid = 0; return false; }
            uint hi, lo;
            if (!uint.TryParse(name.Substring(p, sep - p), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out hi)) { pid = 0; return false; }
            p = sep + 3;
            int end2 = name.IndexOf('_', p);
            string loText = end2 < 0 ? name.Substring(p) : name.Substring(p, end2 - p);
            if (!uint.TryParse(loText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out lo)) { pid = 0; return false; }
            luid = Native.LuidKey((int)hi, lo);
            int engAt = name.IndexOf("_engtype_", p, StringComparison.OrdinalIgnoreCase);
            if (engAt >= 0) engineType = name.Substring(engAt + 9);
            return true;
        }

        // Сколько выделенной памяти карты действительно на процессе. Почему минимум — в шапке файла.
        internal static long GpuAttribute(long dedicatedRaw, long local, bool adapterReportsLocal)
        {
            if (dedicatedRaw < 0) dedicatedRaw = 0;
            if (!adapterReportsLocal) return dedicatedRaw;
            return Math.Min(dedicatedRaw, Math.Max(0, local));
        }

        // Признак GPU-процесса Chromium: дочерний процесс ТОГО ЖЕ образа с --type=gpu-process.
        // Одной командной строки мало — такую строку может написать кто угодно, и «перезапуск»
        // превратился бы в завершение произвольного процесса без подтверждения.
        internal static bool GpuIsHelperCommandLine(string commandLine)
        {
            return commandLine != null && commandLine.IndexOf("--type=gpu-process", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool GpuIsHelper(int pid, RawProc self, RawProc parent)
        {
            if (self == null || parent == null || string.IsNullOrEmpty(self.Name)
                || !string.Equals(self.Name, parent.Name, StringComparison.OrdinalIgnoreCase)) return false;
            string cached;
            if (_gpuHelperCache.TryGetValue(pid, out cached))
            {
                int bar = cached.LastIndexOf('|');
                if (bar > 0 && string.Equals(cached.Substring(0, bar), self.Name, StringComparison.OrdinalIgnoreCase))
                    return cached.Substring(bar + 1) == "1";
            }
            bool helper = GpuIsHelperCommandLine(Native.CommandLineOf(pid));
            _gpuHelperCache[pid] = self.Name + "|" + (helper ? "1" : "0");
            return helper;
        }

        // ------------------------------------------------------------------ //
        //  Разложение
        // ------------------------------------------------------------------ //

        internal static void GpuCompose(GpuAdapter a, List<GpuProc> mine)
        {
            a.ProcCount = mine.Count;
            a.Scaled = false;
            a.DedicatedSlices = GpuComposeOne(a, mine, true);
            a.SharedSlices = GpuComposeOne(a, mine, false);
        }

        private static List<RamSlice> GpuComposeOne(GpuAdapter a, List<GpuProc> mine, bool dedicated)
        {
            string pre = dedicated ? "gd:" : "gs:";
            long total = dedicated ? a.DedicatedTotal : a.SharedTotal;
            long used = Math.Max(0, dedicated ? a.DedicatedUsed : a.SharedUsed);

            long sum = 0;
            foreach (GpuProc p in mine) sum += Math.Max(0, dedicated ? p.Dedicated : p.Shared);
            if (dedicated) a.ProcDedicated = sum; else a.ProcShared = sum;
            if (total <= 0) total = Math.Max(used, sum);

            // Процессы вместе не могут занимать больше, чем есть на карте. Если счётчики всё же
            // так сказали (разные моменты чтения, общие поверхности), доли ужимаются
            // пропорционально, вниз — схема не имеет права показать больше, чем физически есть.
            double k = 1.0;
            if (sum > total && sum > 0)
            {
                k = (double)total / sum;
                a.Scaled = true;
            }

            List<string> order = new List<string>();
            Dictionary<string, List<RamSlice>> groups = new Dictionary<string, List<RamSlice>>(StringComparer.OrdinalIgnoreCase);
            foreach (GpuProc p in mine)
            {
                long v = (long)Math.Floor(Math.Max(0, dedicated ? p.Dedicated : p.Shared) * k);
                if (v <= 0) continue;
                string name = string.IsNullOrEmpty(p.Name) ? "?" : p.Name;
                RamSlice c = new RamSlice(pre + "pid:" + p.Pid.ToString(CultureInfo.InvariantCulture),
                                          p.Helper ? name + " · GPU" : name, v, p.Helper ? GpuKind.Helper : GpuKind.Process);
                c.Pid = p.Pid;
                c.Pids = new List<int>();
                c.Pids.Add(p.Pid);
                c.Hint = GpuProcHint(p);
                List<RamSlice> g;
                if (!groups.TryGetValue(name, out g)) { g = new List<RamSlice>(); groups[name] = g; order.Add(name); }
                g.Add(c);
            }

            List<RamSlice> slices = new List<RamSlice>();
            long placed = 0;
            foreach (string name in order)
            {
                List<RamSlice> g = groups[name];
                long bytes = 0;
                foreach (RamSlice c in g) bytes += c.Bytes;
                placed += bytes;
                if (g.Count == 1) { slices.Add(g[0]); continue; }
                g.Sort(RamBySize);
                RamSlice grp = new RamSlice(pre + "name:" + name.ToLowerInvariant(), name + " × " + g.Count, bytes, GpuKind.Group);
                grp.Children = g;
                grp.Pids = new List<int>();
                foreach (RamSlice c in g) grp.Pids.Add(c.Pid);
                grp.Hint = Tr.S("процессов: ", "processes: ") + g.Count;
                slices.Add(grp);
            }
            slices.Sort(RamBySize);

            // Счётчик карты бывает меньше суммы по процессам (встроенная графика AMD: 158 МБ
            // против 215 МБ по процессам). «Занято» не может быть меньше того, что уже разложено по
            // процессам, иначе шапка и схема называют разные числа.
            if (placed > used)
            {
                used = placed;
                if (dedicated) a.DedicatedUsed = placed; else a.SharedUsed = placed;
            }

            long system =Math.Max(0, Math.Min(used - placed, total - placed));
            long free = Math.Max(0, total - placed - system);
            RamSlice sys = new RamSlice(pre + "system", Tr.S("Система и драйвер", "System and driver"), system, GpuKind.System);
            sys.Hint = dedicated
                ? Tr.S("занято на карте, но ни одному процессу не отнесено: драйвер, поверхности рабочего стола, резерв ядра",
                       "in use on the card but attributed to no process: the driver, desktop surfaces, kernel reserve")
                : Tr.S("общая память, отданная видеокарте, но не отнесённая ни одному процессу",
                       "shared memory given to the card but attributed to no process");
            slices.Add(sys);
            RamSlice fr = new RamSlice(pre + "free", Tr.S("Свободно", "Free"), free, GpuKind.Free);
            fr.Hint = dedicated
                ? Tr.S("свободная память видеокарты", "free memory on the card")
                : Tr.S("сколько ещё ОЗУ Windows готова отдать видеокарте — это не занятая оперативная память",
                       "how much more RAM Windows is ready to lend the card — this RAM is not in use");
            slices.Add(fr);
            return slices;
        }

        private static string GpuProcHint(GpuProc p)
        {
            string h = "PID " + p.Pid.ToString(CultureInfo.InvariantCulture)
                     + Tr.S(" · выделенная ", " · dedicated ") + FormatBytes(p.Dedicated)
                     + Tr.S(" · общая ", " · shared ") + FormatBytes(p.Shared);
            if (p.Load >= 0.5)
                h += Tr.S(" · загрузка ", " · load ") + p.Load.ToString("0", CultureInfo.InvariantCulture) + " %"
                   + (string.IsNullOrEmpty(p.EngineType) ? "" : " (" + p.EngineType + ")");
            if (p.Helper) h += Tr.S(" · GPU-процесс: можно перезапустить, приложение поднимет его само",
                                    " · GPU process: can be restarted, the app brings it back itself");
            if (p.Protected) h += Tr.S(" · системный процесс", " · system process");
            if (p.Inflated) h += Tr.S(" · счётчик Windows завышен: ", " · the Windows counter is inflated: ") + FormatBytes(p.DedicatedRaw);
            return h;
        }

        // ------------------------------------------------------------------ //
        //  Сбросы
        // ------------------------------------------------------------------ //

        // Сколько секунд ещё ждать. 0 — можно.
        internal static int GpuCooldownLeft(DateTime last, DateTime now, int seconds)
        {
            if (last == DateTime.MinValue) return 0;
            double left = seconds - (now - last).TotalSeconds;
            return left <= 0 ? 0 : (int)Math.Ceiling(left);
        }

        // Что вкладка не завершает никогда. Намеренно уже, чем _critical: тот список защищает от
        // АВТОМАТИЧЕСКОГО завершения при сканировании и включает мессенджеры и вендорские утилиты,
        // а здесь человек сам выбрал процесс и подтвердил. Не завершаются только те, без которых
        // сеанс Windows падает или гаснет экран.
        private static readonly HashSet<string> _gpuNeverKill = new HashSet<string>(new string[]
        {
            "system", "registry", "idle", "secure system", "memcompression", "smss.exe", "csrss.exe", "wininit.exe",
            "winlogon.exe", "services.exe", "lsass.exe", "lsaiso.exe", "fontdrvhost.exe", "dwm.exe", "svchost.exe",
            "sysdeck.exe", "windowsprocesscleaner.exe"
        }, StringComparer.OrdinalIgnoreCase);

        internal static bool GpuIsProtectedName(string name)
        {
            return !string.IsNullOrEmpty(name) && _gpuNeverKill.Contains(name);
        }

        // Перезапуск GPU-процесса Chromium/Electron. Всё проверяется заново в момент нажатия:
        // между замером и щелчком pid мог достаться другому процессу.
        public GpuAction GpuRestartHelper(int pid)
        {
            GpuAction r = new GpuAction();
            RawProc self = null, parent = null;
            List<RawProc> snap = Snapshot();
            foreach (RawProc p in snap) if (p.Pid == pid) { self = p; break; }
            if (self == null)
            {
                r.Refused = true;
                r.Message = Tr.S("Процесса уже нет.", "The process is already gone.");
                return r;
            }
            foreach (RawProc p in snap) if (p.Pid == self.Ppid) { parent = p; break; }
            if (pid <= 4 || pid == _selfPid || parent == null
                || !string.Equals(self.Name, parent.Name, StringComparison.OrdinalIgnoreCase)
                || !GpuIsHelperCommandLine(Native.CommandLineOf(pid)))
            {
                r.Refused = true;
                r.Message = Tr.S("Это не GPU-процесс Chromium/Electron — перезапускать нечего.",
                                 "This is not a Chromium/Electron GPU process — nothing to restart.");
                return r;
            }

            string app = self.Name;
            lock (_gpuLock)
            {
                DateTime last;
                if (!_gpuHelperRestartAt.TryGetValue(app, out last)) last = DateTime.MinValue;
                int wait = GpuCooldownLeft(last, DateTime.UtcNow, GpuHelperCooldownSeconds);
                if (wait > 0)
                {
                    r.Refused = true;
                    r.Message = Tr.S("GPU-процесс ", "The GPU process of ") + app
                              + Tr.S(" уже перезапускался. Следующий раз — через ", " was restarted recently. Next time in ")
                              + wait + Tr.S(" с: после нескольких падений подряд Chromium выключает аппаратное ускорение.",
                                            " s: after several crashes in a row Chromium turns hardware acceleration off.");
                    return r;
                }
            }

            IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE | Native.SYNCHRONIZE, false, pid);
            if (h == IntPtr.Zero)
            {
                r.Message = Tr.S("Нет доступа к процессу (код ", "No access to the process (code ")
                          + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").";
                return r;
            }
            try
            {
                // Код выхода 0 — «завершился сам», а не «упал»: так браузер не засчитывает
                // перезапуск в счётчик падений GPU-процесса.
                if (!Native.TerminateProcess(h, 0))
                {
                    r.Message = Tr.S("Система отказала в завершении (код ", "The system refused to terminate it (code ")
                              + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").";
                    return r;
                }
                Native.WaitForSingleObject(h, 3000);
            }
            finally { Native.CloseHandle(h); }

            lock (_gpuLock) { _gpuHelperRestartAt[app] = DateTime.UtcNow; }
            lock (_gpuLock) { _gpuHelperCache.Remove(pid); }

            // Приложение поднимает GPU-процесс заново само; ждём его до восьми секунд, чтобы
            // сказать человеку не «убито», а «перезапущено» — или честно, что не поднялся.
            DateTime until = DateTime.UtcNow.AddSeconds(8);
            while (DateTime.UtcNow < until && r.NewPid == 0)
            {
                Thread.Sleep(250);
                foreach (RawProc p in Snapshot())
                    if (p.Ppid == parent.Pid && p.Pid != pid
                        && string.Equals(p.Name, self.Name, StringComparison.OrdinalIgnoreCase)
                        && GpuIsHelperCommandLine(Native.CommandLineOf(p.Pid)))
                    {
                        r.NewPid = p.Pid;
                        break;
                    }
            }
            r.Ok = true;
            r.Message = r.NewPid != 0
                ? Tr.S("GPU-процесс ", "The GPU process of ") + app + Tr.S(" перезапущен, новый pid ", " restarted, new pid ") + r.NewPid
                : Tr.S("GPU-процесс ", "The GPU process of ") + app
                  + Tr.S(" завершён; новый ещё не поднялся — приложение запустит его, когда понадобится.",
                         " terminated; a new one has not started yet — the app starts it when it needs one.");
            return r;
        }

        public GpuAction GpuResetDriver()
        {
            GpuAction r = new GpuAction();
            lock (_gpuLock)
            {
                int wait = GpuCooldownLeft(_gpuDriverResetAt, DateTime.UtcNow, GpuDriverCooldownSeconds);
                if (wait > 0)
                {
                    r.Refused = true;
                    r.Message = Tr.S("Видеодрайвер только что перезапускался. Повторить можно через ",
                                     "The graphics driver was just restarted. It can be repeated in ") + wait + Tr.S(" с.", " s.");
                    return r;
                }
                _gpuDriverResetAt = DateTime.UtcNow;
            }
            uint sent = Native.SendGraphicsResetHotkey();
            r.Ok = sent == 8;
            r.Message = r.Ok
                ? Tr.S("Команда перезапуска видеодрайвера отправлена.", "The graphics driver restart was sent.")
                : Tr.S("Система не приняла сочетание клавиш (код ", "The system did not accept the key combination (code ")
                  + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ").";
            if (!r.Ok) lock (_gpuLock) { _gpuDriverResetAt = DateTime.MinValue; }
            return r;
        }
    }
}
