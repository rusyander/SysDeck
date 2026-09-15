// SysDeck — источники показателей оверлея и их сборщик. Каждый источник заполняет свой кадр со своим
// периодом; сборщик сводит кадры по старшинству: HWiNFO → NVML → Afterburner → PDH → Windows → сведения. Первый,
// кто дал годное значение, побеждает: температура процессора с датчика ядра (HWiNFO) важнее датчика платы (ACPI).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck.Capture
{
    internal abstract class HudSource : IDisposable
    {
        public int PeriodMs = 1000;
        internal long NextDueTicks;
        public HudFrame Last = new HudFrame();
        public abstract string Name { get; }
        public abstract void Collect(HudFrame f);
        public virtual void Dispose() { }
    }

    // Главная видеокарта — аппаратная с наибольшей выделенной памятью: на ноутбуке это дискретная, а не встроенная.
    // Список карт перечитывается раз в 30 секунд, общий для всех источников.
    internal static class HudAdapters
    {
        private static readonly object Gate = new object();
        private static List<DxgiAdapter> _list;
        private static DateTime _at = DateTime.MinValue;

        public static DxgiAdapter Primary()
        {
            lock (Gate)
            {
                DateTime now = DateTime.UtcNow;
                if (_list == null || (now - _at).TotalSeconds > 30)
                {
                    try { _list = Native.DxgiAdapters(); }
                    catch { if (_list == null) _list = new List<DxgiAdapter>(); }
                    _at = now;
                }
                DxgiAdapter best = null;
                foreach (DxgiAdapter a in _list)
                    if (!a.Software && (best == null || a.Dedicated > best.Dedicated)) best = a;
                return best;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  PDH: процессор (всего и по ядрам, частота), ACPI-температура, видеокарта, диск, сеть
    // ------------------------------------------------------------------ //
    internal sealed class HudPdhSource : HudSource
    {
        private IntPtr _query = IntPtr.Zero;
        private IntPtr _cpu, _coreUtil, _corePerf, _coreFreq, _thermal, _gpuEngine, _gpuMem, _gpuProcDed, _gpuProcShared, _diskRead, _diskWrite, _netDown, _netUp, _perfLimit, _pagesIn;

        public override string Name { get { return "PDH"; } }

        public HudPdhSource()
        {
            IntPtr q;
            if (Native.PdhOpenQueryW(null, IntPtr.Zero, out q) != 0 || q == IntPtr.Zero) return;
            _query = q;
            if (!Add(@"\Processor Information(_Total)\% Processor Utility", out _cpu))
                Add(@"\Processor(_Total)\% Processor Time", out _cpu);
            if (!Add(@"\Processor Information(*)\% Processor Utility", out _coreUtil))
                Add(@"\Processor Information(*)\% Processor Time", out _coreUtil);
            Add(@"\Processor Information(*)\% Processor Performance", out _corePerf);
            Add(@"\Processor Information(*)\Processor Frequency", out _coreFreq);
            Add(@"\Thermal Zone Information(*)\Temperature", out _thermal);
            Add(@"\GPU Engine(*)\Utilization Percentage", out _gpuEngine);
            Add(@"\GPU Adapter Memory(*)\Dedicated Usage", out _gpuMem);
            Add(@"\GPU Process Memory(*)\Dedicated Usage", out _gpuProcDed);
            Add(@"\GPU Process Memory(*)\Shared Usage", out _gpuProcShared);
            Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec", out _diskRead);
            Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec", out _diskWrite);
            Add(@"\Network Interface(*)\Bytes Received/sec", out _netDown);
            Add(@"\Network Interface(*)\Bytes Sent/sec", out _netUp);
            // Меньше 100 — Windows или прошивка сбросили частоту (перегрев, питание, план энергопотребления).
            if (!Add(@"\Processor Information(_Total)\% Performance Limit", out _perfLimit)) _perfLimit = IntPtr.Zero;
            // Страниц в секунду, прочитанных с диска по жёстким ошибкам: в игре это фризы из-за нехватки памяти.
            Add(@"\Memory\Pages Input/sec", out _pagesIn);
            // Счётчики скорости и загрузки пусты до второго чтения.
            Native.PdhCollectQueryData(_query);
        }

        private bool Add(string path, out IntPtr counter)
        {
            return Native.PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out counter) == 0 && counter != IntPtr.Zero;
        }

        public override void Collect(HudFrame f)
        {
            DxgiAdapter gpu = HudAdapters.Primary();
            if (_query == IntPtr.Zero || Native.PdhCollectQueryData(_query) != 0) return;
            // «% Processor Utility» на турбочастоте уходит выше 100 % — Диспетчер задач режет до 100, здесь так же
            // (иначе мин./сред./макс. и порог подсветки ловят 168 %).
            double load = Single(_cpu);
            f.Put("cpu.load", HudKind.Percent, double.IsNaN(load) ? load : Math.Max(0, Math.Min(100, load)));

            Dictionary<string, double> util = Native.PdhReadAll(_coreUtil, true);
            Dictionary<string, double> perf = Native.PdhReadAll(_corePerf, true);
            Dictionary<string, double> freq = Native.PdhReadAll(_coreFreq, true);
            List<string> cores = OrderCores(util.Keys);
            double coreMax = double.NaN;
            for (int i = 0; i < cores.Count; i++)
            {
                double v;
                if (util.TryGetValue(cores[i], out v))
                {
                    v = Math.Max(0, Math.Min(100, v));
                    f.Put(HudCatalog.CoreId(i, "load"), HudKind.Percent, v);
                    if (double.IsNaN(coreMax) || v > coreMax) coreMax = v;
                }
                double mhz = Mhz(perf, freq, cores[i]);
                if (HudFormat.Valid(mhz)) f.Put(HudCatalog.CoreId(i, "mhz"), HudKind.Mhz, mhz);
            }
            double total = Mhz(perf, freq, "_Total");
            if (!HudFormat.Valid(total)) total = Mhz(perf, freq, "0,_Total");
            if (HudFormat.Valid(total)) f.Put("cpu.mhz", HudKind.Mhz, total);
            // Одно ядро на 100 % при общей загрузке 30 % — игра упёрлась в поток, а не в процессор целиком.
            f.Put("cpu.coremax", HudKind.Percent, coreMax);
            if (_perfLimit != IntPtr.Zero) f.Put("cpu.perflimit", HudKind.Percent, Single(_perfLimit));
            HudValue faults = new HudValue("ram.hardfaults", HudKind.Number, Single(_pagesIn));
            faults.Unit = Tr.S("стр/с", "pg/s");
            f.Put(faults);

            double hottest = double.NaN;
            foreach (double k in Native.PdhReadAll(_thermal, true).Values)
            {
                double c = HudFormat.KelvinToCelsius(k);
                if (!double.IsNaN(c) && (double.IsNaN(hottest) || c > hottest)) hottest = c;
            }
            if (HudFormat.Valid(hottest))
            {
                HudValue t = new HudValue("cpu.temp", HudKind.Temp, hottest);
                t.Note = Tr.S("датчик платы (ACPI)", "board sensor (ACPI)");
                f.Put(t);
            }
            if (gpu != null)
            {
                Dictionary<string, double> engines = Native.PdhReadAll(_gpuEngine, true);
                f.Put("gpu.load", HudKind.Percent, GpuLoad(engines, gpu.Luid));
                long used = GpuMemory(Native.PdhReadAll(_gpuMem, false), gpu.Luid);
                if (used >= 0 && gpu.Dedicated > 0)
                {
                    HudValue v = new HudValue("vram", HudKind.Memory, used);
                    v.Total = gpu.Dedicated;
                    f.Put(v);
                }
                // Игра (активное окно с потомками): её доля видеокарты и видеопамяти.
                HudFamily fam = HudProcTree.Current();
                if (fam != null)
                {
                    f.Put("app.gpu", HudKind.Percent, GpuLoad(engines, gpu.Luid, fam.Pids));
                    long ded = ProcessGpuMemory(Native.PdhReadAll(_gpuProcDed, false), gpu.Luid, fam.Pids);
                    if (ded >= 0) f.Put("app.vram", HudKind.Bytes, ded);
                    long shared = ProcessGpuMemory(Native.PdhReadAll(_gpuProcShared, false), gpu.Luid, fam.Pids);
                    if (shared >= 0) f.Put("app.vramshared", HudKind.Bytes, shared);
                }
            }
            f.Put("disk.read", HudKind.Rate, Single(_diskRead));
            f.Put("disk.write", HudKind.Rate, Single(_diskWrite));
            f.Put("net.down", HudKind.Rate, Sum(_netDown));
            f.Put("net.up", HudKind.Rate, Sum(_netUp));
        }

        // «0,0», «0,1» … «1,0» — группа и номер; «_Total» и «0,_Total» — итоги. Порядок = номер логического процессора.
        internal static List<string> OrderCores(IEnumerable<string> names)
        {
            List<KeyValuePair<long, string>> list = new List<KeyValuePair<long, string>>();
            foreach (string n in names)
            {
                int comma = n.IndexOf(',');
                int g, c;
                if (comma <= 0 || !int.TryParse(n.Substring(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out g)
                    || !int.TryParse(n.Substring(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out c)) continue;
                list.Add(new KeyValuePair<long, string>(((long)g << 20) | (uint)c, n));
            }
            list.Sort(delegate(KeyValuePair<long, string> a, KeyValuePair<long, string> b) { return a.Key.CompareTo(b.Key); });
            List<string> result = new List<string>();
            foreach (KeyValuePair<long, string> kv in list) result.Add(kv.Value);
            return result;
        }

        // «% Processor Performance» — доля от базовой частоты «Processor Frequency», с турбо больше 100.
        internal static double Mhz(Dictionary<string, double> perf, Dictionary<string, double> freq, string instance)
        {
            double p, fq;
            if (!perf.TryGetValue(instance, out p) || !freq.TryGetValue(instance, out fq) || fq <= 0 || p <= 0) return double.NaN;
            return fq * p / 100.0;
        }

        private static double Single(IntPtr counter)
        {
            foreach (double v in Native.PdhReadAll(counter, true).Values) return v;
            return double.NaN;
        }

        private static double Sum(IntPtr counter)
        {
            Dictionary<string, double> all = Native.PdhReadAll(counter, true);
            if (all.Count == 0) return double.NaN;
            double total = 0;
            foreach (double v in all.Values) total += Math.Max(0, v);
            return total;
        }

        internal static double GpuLoad(Dictionary<string, double> engines, string luid)
        {
            return GpuLoad(engines, luid, null);
        }

        // pids — только эти процессы (null — все). Загрузка — самый занятый тип движка (3D, видео…), как у диспетчера задач.
        internal static double GpuLoad(Dictionary<string, double> engines, string luid, ICollection<int> pids)
        {
            Dictionary<string, double> byType = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            bool seen = false;
            foreach (KeyValuePair<string, double> kv in engines)
            {
                int pid; string instLuid, type;
                if (!Engine.GpuParseInstance(kv.Key, out pid, out instLuid, out type) || type == null) continue;
                if (!string.Equals(instLuid, luid, StringComparison.OrdinalIgnoreCase)) continue;
                if (pids != null && !pids.Contains(pid)) continue;
                seen = true;
                double v = Math.Max(0, Math.Min(100, kv.Value)), prev;
                byType[type] = byType.TryGetValue(type, out prev) ? prev + v : v;
            }
            if (!seen) return double.NaN;
            double max = 0;
            foreach (double v in byType.Values) max = Math.Max(max, Math.Min(100, v));
            return max;
        }

        internal static long GpuMemory(Dictionary<string, double> adapters, string luid)
        {
            long total = -1;
            foreach (KeyValuePair<string, double> kv in adapters)
            {
                int pid; string instLuid, type;
                if (!Engine.GpuParseInstance(kv.Key, out pid, out instLuid, out type) || pid != 0) continue;
                if (!string.Equals(instLuid, luid, StringComparison.OrdinalIgnoreCase)) continue;
                total = Math.Max(0, total) + (long)Math.Max(0, kv.Value);
            }
            return total;
        }

        // «GPU Process Memory»: pid_<N>_luid_<hi>_<lo>_phys_<k> — сумма по процессам семейства на этой карте; -1 — ни одного.
        internal static long ProcessGpuMemory(Dictionary<string, double> procs, string luid, ICollection<int> pids)
        {
            long total = -1;
            foreach (KeyValuePair<string, double> kv in procs)
            {
                int pid; string instLuid, type;
                if (!Engine.GpuParseInstance(kv.Key, out pid, out instLuid, out type) || pid <= 0 || !pids.Contains(pid)) continue;
                if (!string.Equals(instLuid, luid, StringComparison.OrdinalIgnoreCase)) continue;
                total = Math.Max(0, total) + (long)Math.Max(0, kv.Value);
            }
            return total;
        }

        public override void Dispose()
        {
            if (_query != IntPtr.Zero) { Native.PdhCloseQuery(_query); _query = IntPtr.Zero; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Windows: оперативная и выделенная память, время работы, часы
    // ------------------------------------------------------------------ //
    internal sealed class HudWindowsSource : HudSource
    {
        [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();

        public override string Name { get { return "Windows"; } }

        public HudWindowsSource() { PeriodMs = 250; }

        public override void Collect(HudFrame f)
        {
            Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
            m.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
            if (Native.GlobalMemoryStatusEx(ref m))
            {
                HudValue ram = new HudValue("ram", HudKind.Memory, (double)(m.ullTotalPhys - m.ullAvailPhys));
                ram.Total = m.ullTotalPhys;
                f.Put(ram);
                f.Put("ram.load", HudKind.Percent, m.ullTotalPhys == 0 ? double.NaN : 100.0 * (m.ullTotalPhys - m.ullAvailPhys) / m.ullTotalPhys);
                HudValue commit = new HudValue("commit", HudKind.Memory, (double)(m.ullTotalPageFile - m.ullAvailPageFile));
                commit.Total = m.ullTotalPageFile;
                f.Put(commit);
            }
            f.PutText("sys.uptime", HudFormat.Uptime(TimeSpan.FromMilliseconds(GetTickCount64())));
            f.PutText("clock", DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
            double hz = RefreshRate(CapNative.GetForegroundWindow());
            if (hz > 0)
            {
                HudValue v = new HudValue("display.hz", HudKind.Number, hz);
                v.Unit = Tr.S("Гц", "Hz");
                f.Put(v);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettingsW(string device, int mode, ref DEVMODE dm);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        private const int EnumCurrentSettings = -1;
        private const uint MonitorDefaultToPrimary = 1;

        // Частота монитора, на котором активное окно (игра); 0 — не узнать. Меньше 2 Гц Windows отдаёт как «по умолчанию».
        internal static double RefreshRate(IntPtr hwnd)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToPrimary);
            CapNative.MONITORINFOEX mi = new CapNative.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(CapNative.MONITORINFOEX));
            if (mon == IntPtr.Zero || !CapNative.GetMonitorInfo(mon, ref mi)) return 0;
            DEVMODE dm = new DEVMODE();
            dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            if (!EnumDisplaySettingsW(mi.szDevice, EnumCurrentSettings, ref dm)) return 0;
            return dm.dmDisplayFrequency > 1 ? dm.dmDisplayFrequency : 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Сведения о системе: редко, текстом
    // ------------------------------------------------------------------ //
    internal sealed class HudSysSource : HudSource
    {
        public override string Name { get { return "SMBIOS"; } }

        public HudSysSource() { PeriodMs = 60000; }

        public override void Collect(HudFrame f)
        {
            HudSysInfo info = HudSysInfo.Collect();
            f.PutText("sys.cpu", info.CpuText);
            f.PutText("sys.board", info.BoardText);
            f.PutText("sys.mem", info.MemoryText);
            f.PutText("sys.gpu", info.GpuText);
        }
    }

    internal sealed class HudMahmSource : HudSource
    {
        public override string Name { get { return "MSI Afterburner"; } }

        public override void Collect(HudFrame f)
        {
            byte[] data = HudBytes.Snapshot(HudMahm.MapName, null, 4 * 1024 * 1024);
            List<HudMahm.Entry> entries = new List<HudMahm.Entry>();
            List<HudMahm.Gpu> gpus = new List<HudMahm.Gpu>();
            if (!HudMahm.Parse(data, entries, gpus)) return;
            DxgiAdapter main = HudAdapters.Primary();
            HudMahm.Publish(entries, gpus, f, main == null ? 0 : main.VendorId, main == null ? 0 : main.DeviceId);
        }
    }

    internal sealed class HudHwinfoSource : HudSource
    {
        public override string Name { get { return "HWiNFO"; } }

        // Когда HWiNFO последний раз отдал живые данные (UTC); MinValue — ни разу за сеанс.
        public DateTime LastLiveUtc = DateTime.MinValue;

        public override void Collect(HudFrame f)
        {
            byte[] data = HudBytes.Snapshot(HudHwinfo.MapName, HudHwinfo.MutexName, 16 * 1024 * 1024);
            List<HudHwinfo.Sensor> sensors = new List<HudHwinfo.Sensor>();
            List<HudHwinfo.Reading> readings = new List<HudHwinfo.Reading>();
            long poll;
            if (!HudHwinfo.Parse(data, sensors, readings, out poll)) return;
            // Закрытый HWiNFO оставляет карту с последними числами у того, кто её ещё держит; старше 15 секунд — не данные.
            DateTime polled = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(poll);
            if (poll > 0 && Math.Abs((DateTime.UtcNow - polled).TotalSeconds) > 15) return;
            LastLiveUtc = DateTime.UtcNow;
            DxgiAdapter main = HudAdapters.Primary();
            HudHwinfo.Publish(sensors, readings, main == null ? null : main.Name, f);
        }
    }

    // ------------------------------------------------------------------ //
    //  NVML — библиотека драйвера NVIDIA (System32\nvml.dll). Нет её или отказала — молча без этих строк.
    // ------------------------------------------------------------------ //
    internal sealed class HudNvml : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct NvUtil { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)] private struct NvMemory { public ulong Total, Free, Used; }

        [DllImport("nvml.dll")] private static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] private static extern int nvmlShutdown();
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCount_v2(out uint count);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetName(IntPtr device, byte[] name, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint celsius);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEnforcedPowerLimit(IntPtr device, out uint milliwatts);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetFanSpeed(IntPtr device, out uint percent);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetClockInfo(IntPtr device, int type, out uint mhz);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvUtil util);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetEncoderUtilization(IntPtr device, out uint util, out uint periodUs);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetDecoderUtilization(IntPtr device, out uint util, out uint periodUs);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetPerformanceState(IntPtr device, out int state);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrentClocksThrottleReasons(IntPtr device, out ulong reasons);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetCurrPcieLinkWidth(IntPtr device, out uint width);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkGeneration(IntPtr device, out uint gen);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMaxPcieLinkWidth(IntPtr device, out uint width);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvMemory memory);
        [DllImport("nvml.dll")] private static extern int nvmlDeviceGetVbiosVersion(IntPtr device, byte[] version, uint length);
        [DllImport("nvml.dll")] private static extern int nvmlSystemGetDriverVersion(byte[] version, uint length);

        public static readonly HudNvml Shared = new HudNvml();

        private readonly object _gate = new object();
        private int _state;            // 0 — не пробовали, 1 — работает, -1 — недоступна
        private IntPtr _device = IntPtr.Zero;
        private string _deviceFor;
        private string _deviceName;

        private bool Ready(string adapterName)
        {
            if (_state < 0) return false;
            if (_state == 0)
            {
                try
                {
                    if (nvmlInit_v2() != 0) { _state = -1; return false; }
                }
                catch (Exception ex)
                {
                    // DllNotFoundException: драйвер без NVML (или не NVIDIA) — больше не пробовать.
                    if (!(ex is DllNotFoundException)) CapLog.Report(ex);
                    _state = -1;
                    return false;
                }
                _state = 1;
            }
            if (_device == IntPtr.Zero || _deviceFor != adapterName) { _device = Find(adapterName, out _deviceName); _deviceFor = adapterName; }
            return _device != IntPtr.Zero;
        }

        // Одна функция не нашлась в старом драйвере — это не повод отказываться от остальных.
        private delegate int Call();

        private static bool Try(Call call)
        {
            try { return call() == 0; }
            catch (EntryPointNotFoundException) { return false; }
        }

        public void Collect(DxgiAdapter main, HudFrame f)
        {
            if (main == null || main.VendorId != 0x10DE) return;
            lock (_gate)
            {
                try
                {
                    if (!Ready(main.Name)) return;
                    IntPtr d = _device;
                    uint u = 0, u2 = 0; int st = 0; ulong reasons = 0;
                    NvUtil util = new NvUtil();
                    if (Try(delegate { return nvmlDeviceGetTemperature(d, 0, out u); })) f.Put("gpu.temp", HudKind.Temp, u);
                    if (Try(delegate { return nvmlDeviceGetPowerUsage(d, out u); })) f.Put("gpu.power", HudKind.Watts, u / 1000.0);
                    if (Try(delegate { return nvmlDeviceGetEnforcedPowerLimit(d, out u); })) f.Put("gpu.powerlimit", HudKind.Watts, u / 1000.0);
                    if (Try(delegate { return nvmlDeviceGetFanSpeed(d, out u); })) f.Put("gpu.fan", HudKind.Percent, u);
                    if (Try(delegate { return nvmlDeviceGetClockInfo(d, 0, out u); })) f.Put("gpu.clock", HudKind.Mhz, u);
                    if (Try(delegate { return nvmlDeviceGetClockInfo(d, 1, out u); })) f.Put("gpu.memclock", HudKind.Mhz, u);
                    if (Try(delegate { return nvmlDeviceGetUtilizationRates(d, out util); })) f.Put("gpu.memload", HudKind.Percent, util.Memory);
                    if (Try(delegate { return nvmlDeviceGetEncoderUtilization(d, out u, out u2); })) f.Put("gpu.enc", HudKind.Percent, u);
                    if (Try(delegate { return nvmlDeviceGetDecoderUtilization(d, out u, out u2); })) f.Put("gpu.dec", HudKind.Percent, u);
                    if (Try(delegate { return nvmlDeviceGetPerformanceState(d, out st); }) && st >= 0 && st < 32)
                        f.PutText("gpu.pstate", "P" + st.ToString(CultureInfo.InvariantCulture));
                    if (Try(delegate { return nvmlDeviceGetCurrentClocksThrottleReasons(d, out reasons); }))
                        f.PutText("gpu.throttle", ThrottleText(reasons));
                    if (Try(delegate { return nvmlDeviceGetCurrPcieLinkGeneration(d, out u); }) && Try(delegate { return nvmlDeviceGetCurrPcieLinkWidth(d, out u2); }))
                        f.PutText("gpu.pcie", "Gen" + u.ToString(CultureInfo.InvariantCulture) + " x" + u2.ToString(CultureInfo.InvariantCulture));
                }
                catch (Exception ex)
                {
                    CapLog.Report(ex);
                    _state = -1;
                }
            }
        }

        // Для «Сведений о системе»: драйвер, BIOS карты, шина, память.
        public List<KeyValuePair<string, string>> StaticInfo()
        {
            List<KeyValuePair<string, string>> list = new List<KeyValuePair<string, string>>();
            DxgiAdapter main = HudAdapters.Primary();
            if (main == null || main.VendorId != 0x10DE) return list;
            lock (_gate)
            {
                try
                {
                    if (!Ready(main.Name)) return list;
                    IntPtr d = _device;
                    byte[] buf = new byte[96];
                    uint g = 0, w = 0, mg = 0, mw = 0; NvMemory mem = new NvMemory();
                    if (Try(delegate { return nvmlSystemGetDriverVersion(buf, (uint)buf.Length); })) list.Add(new KeyValuePair<string, string>("NVIDIA driver", Z(buf)));
                    buf = new byte[96];
                    if (Try(delegate { return nvmlDeviceGetVbiosVersion(d, buf, (uint)buf.Length); })) list.Add(new KeyValuePair<string, string>("VBIOS", Z(buf)));
                    if (Try(delegate { return nvmlDeviceGetMaxPcieLinkGeneration(d, out mg); }) && Try(delegate { return nvmlDeviceGetMaxPcieLinkWidth(d, out mw); }))
                    {
                        string now = Try(delegate { return nvmlDeviceGetCurrPcieLinkGeneration(d, out g); }) && Try(delegate { return nvmlDeviceGetCurrPcieLinkWidth(d, out w); })
                            ? Tr.S(", сейчас Gen", ", now Gen") + g.ToString(CultureInfo.InvariantCulture) + " x" + w.ToString(CultureInfo.InvariantCulture) : "";
                        list.Add(new KeyValuePair<string, string>("PCIe", "Gen" + mg.ToString(CultureInfo.InvariantCulture) + " x" + mw.ToString(CultureInfo.InvariantCulture) + now));
                    }
                    if (Try(delegate { return nvmlDeviceGetMemoryInfo(d, out mem); }) && mem.Total > 0)
                        list.Add(new KeyValuePair<string, string>(Tr.S("Видеопамять (NVML)", "Video memory (NVML)"),
                            (mem.Total / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + Tr.S(" ГБ", " GB")));
                    uint lim = 0;
                    if (Try(delegate { return nvmlDeviceGetEnforcedPowerLimit(d, out lim); }))
                        list.Add(new KeyValuePair<string, string>(Tr.S("Предел мощности", "Power limit"), (lim / 1000).ToString(CultureInfo.InvariantCulture) + Tr.S(" Вт", " W")));
                }
                catch (Exception ex) { CapLog.Report(ex); }
            }
            return list;
        }

        private static string Z(byte[] buf) { return Encoding.ASCII.GetString(buf).TrimEnd('\0').Trim(); }

        // nvmlClocksThrottleReason*: 0x1 простой, 0x2 частота задана программой, 0x4 лимит мощности, 0x8 аппаратное
        // замедление, 0x10 SyncBoost, 0x20 программный перегрев, 0x40 аппаратный перегрев, 0x80 внешний тормоз питания,
        // 0x100 настройка дисплея.
        internal static string ThrottleText(ulong r)
        {
            List<string> parts = new List<string>();
            if ((r & 0x4) != 0) parts.Add(Tr.S("лимит мощности", "power cap"));
            if ((r & 0x60) != 0) parts.Add(Tr.S("перегрев", "thermal"));
            if ((r & 0x88) != 0) parts.Add(Tr.S("аппаратный", "hardware"));
            if ((r & 0x2) != 0) parts.Add(Tr.S("задано программой", "app setting"));
            if ((r & 0x10) != 0) parts.Add("SyncBoost");
            if ((r & 0x100) != 0) parts.Add(Tr.S("дисплей", "display"));
            if (parts.Count > 0) return string.Join(", ", parts.ToArray());
            return (r & 0x1) != 0 ? Tr.S("простой", "idle") : Tr.S("нет", "none");
        }

        // Карта NVML с тем же названием, что у DXGI; не нашлась по имени — первая.
        private static IntPtr Find(string adapterName, out string name)
        {
            name = null;
            uint count;
            if (nvmlDeviceGetCount_v2(out count) != 0 || count == 0) return IntPtr.Zero;
            IntPtr first = IntPtr.Zero;
            for (uint i = 0; i < count; i++)
            {
                IntPtr d;
                if (nvmlDeviceGetHandleByIndex_v2(i, out d) != 0) continue;
                if (first == IntPtr.Zero) first = d;
                byte[] buf = new byte[96];
                if (nvmlDeviceGetName(d, buf, (uint)buf.Length) != 0) continue;
                string n = Z(buf);
                if (!string.IsNullOrEmpty(adapterName) && adapterName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) { name = n; return d; }
            }
            return first;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_state == 1) { try { nvmlShutdown(); } catch { } }
                _state = 0;
                _device = IntPtr.Zero;
            }
        }
    }

    internal sealed class HudNvmlSource : HudSource
    {
        public override string Name { get { return "NVIDIA NVML"; } }

        public override void Collect(HudFrame f)
        {
            HudNvml.Shared.Collect(HudAdapters.Primary(), f);
        }
    }

    // ------------------------------------------------------------------ //
    //  Сборщик
    // ------------------------------------------------------------------ //
    internal sealed class HudCollector : IDisposable
    {
        private readonly List<HudSource> _sources = new List<HudSource>();
        public readonly HudHwinfoSource Hwinfo;
        public readonly HudFpsSource Fps;

        public HudCollector()
        {
            Hwinfo = new HudHwinfoSource();
            _sources.Add(Hwinfo);
            _sources.Add(new HudNvmlSource());
            // Свои кадры (ETW) старше чисел RTSS из Afterburner: у своих есть время каждого кадра для «пилы».
            Fps = new HudFpsSource();
            _sources.Add(Fps);
            _sources.Add(new HudMahmSource());
            _sources.Add(new HudPdhSource());
            _sources.Add(new HudWindowsSource());
            _sources.Add(new HudAppSource());
            _sources.Add(new HudSysSource());
        }

        // Для тестов и FPS: источник со старшинством ниже всех остальных, если позиция не указана.
        public void Insert(int index, HudSource source)
        {
            _sources.Insert(Math.Max(0, Math.Min(_sources.Count, index)), source);
        }

        public IList<HudSource> Sources { get { return _sources; } }

        public HudFrame Tick()
        {
            long now = DateTime.UtcNow.Ticks;
            foreach (HudSource s in _sources)
            {
                if (now < s.NextDueTicks) continue;
                s.NextDueTicks = now + TimeSpan.TicksPerMillisecond * Math.Max(100, s.PeriodMs);
                HudFrame f = new HudFrame();
                try { s.Collect(f); }
                catch (Exception ex) { CapLog.Report(ex); }
                s.Last = f;
            }
            return Merge(_sources);
        }

        public static HudFrame Merge(IList<HudSource> ordered)
        {
            HudFrame merged = new HudFrame();
            foreach (HudSource s in ordered)
                foreach (KeyValuePair<string, HudValue> kv in s.Last.Values)
                    if (!merged.Has(kv.Key) && (kv.Value.Kind == HudKind.Text ? !string.IsNullOrEmpty(kv.Value.Text) : HudFormat.Valid(kv.Value.Value)))
                        merged.Values[kv.Key] = kv.Value;
            Hottest(merged);
            Derived(merged);
            return merged;
        }

        // Расчётные строки: кадров на ватт и что ограничивает кадры.
        internal static void Derived(HudFrame f)
        {
            HudValue fps = f.Get("fps"), power = f.Get("gpu.power");
            if (fps != null && power != null && HudFormat.Valid(fps.Value) && HudFormat.Valid(power.Value) && power.Value >= 5)
                f.Put("fps.perwatt", HudKind.Number, fps.Value / power.Value);
            string limit = Bottleneck(f);
            if (limit != null) f.PutText("fps.bottleneck", limit);
        }

        // Видеокарта за 95 % — упор в неё; иначе одно ядро за 90 % при кадрах ниже частоты монитора — в процессор;
        // кадры держатся у частоты монитора — в синхронизацию или ограничитель. null — кадров нет, судить не о чем.
        internal static string Bottleneck(HudFrame f)
        {
            HudValue fps = f.Get("fps");
            if (fps == null || !HudFormat.Valid(fps.Value) || fps.Value <= 0) return null;
            double gpu = Num(f, "gpu.load"), core = Num(f, "cpu.coremax"), hz = Num(f, "display.hz"), limit = Num(f, "cpu.perflimit");
            if (HudFormat.Valid(gpu) && gpu >= 95) return Tr.S("видеокарта", "GPU");
            if (HudFormat.Valid(hz) && hz > 0 && Math.Abs(fps.Value - hz) <= Math.Max(2, hz * 0.03))
                return Tr.S("частота монитора / V-Sync", "refresh rate / V-Sync");
            if (HudFormat.Valid(core) && core >= 90)
                return HudFormat.Valid(limit) && limit < 90 ? Tr.S("процессор (частота сброшена)", "CPU (throttled)") : Tr.S("процессор", "CPU");
            if (HudFormat.Valid(gpu) && gpu < 80) return Tr.S("ограничитель или игра", "limiter or the game");
            return null;
        }

        private static double Num(HudFrame f, string id)
        {
            HudValue v = f.Get(id);
            return v == null ? double.NaN : v.Value;
        }

        // «Самая горячая точка»: максимум по всем настоящим температурам кадра, с именем датчика в пояснении.
        internal static void Hottest(HudFrame f)
        {
            HudValue best = null;
            foreach (HudValue v in f.Values.Values)
            {
                if (v.Kind != HudKind.Temp || v.Id == "hot.max" || !HudFormat.Valid(v.Value) || v.Value <= 0 || v.Value > 125) continue;
                if (v.NotPlace || !HudHwinfo.IsRealTemperature(v.Label)) continue;
                if (best == null || v.Value > best.Value) best = v;
            }
            if (best == null) return;
            HudValue hot = new HudValue("hot.max", HudKind.Temp, best.Value);
            HudDef def = HudCatalog.Find(best.Id);
            string what = !string.IsNullOrEmpty(best.Label) ? best.Label : def != null ? def.Title : best.Id;
            hot.Note = string.IsNullOrEmpty(best.Note) || best.Note == what ? what : what + " · " + best.Note;
            f.Values["hot.max"] = hot;
        }

        public void Dispose()
        {
            foreach (HudSource s in _sources) { try { s.Dispose(); } catch { } }
        }
    }
}
