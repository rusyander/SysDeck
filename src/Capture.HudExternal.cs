// SysDeck — показатели из чужих программ: общая память MSI Afterburner («MAHMSharedMemory») и
// HWiNFO («Global\HWiNFO_SENS_SM2»). Своего драйвера ядра у приложения нет и не будет, а температуры ядер
// процессора, горячая точка видеокарты, напряжения и тайминги памяти без драйвера не читаются — их уже читают эти
// программы, и обе публикуют числа для других. Нет программы или она не публикует — строки просто без данных.
//
// Разбор — из массива байт, чтобы формат проверялся тестами без запущенных программ.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;

namespace SysDeck.Capture
{
    internal static class HudBytes
    {
        public static uint U32(byte[] b, int at) { return at < 0 || at + 4 > b.Length ? 0 : BitConverter.ToUInt32(b, at); }
        public static long I64(byte[] b, int at) { return at < 0 || at + 8 > b.Length ? 0 : BitConverter.ToInt64(b, at); }
        public static float F32(byte[] b, int at) { return at < 0 || at + 4 > b.Length ? float.NaN : BitConverter.ToSingle(b, at); }
        public static double F64(byte[] b, int at) { return at < 0 || at + 8 > b.Length ? double.NaN : BitConverter.ToDouble(b, at); }

        public static string Str(byte[] b, int at, int max, Encoding enc)
        {
            if (at < 0 || at >= b.Length) return "";
            int end = at, limit = Math.Min(b.Length, at + max);
            while (end < limit && b[end] != 0) end++;
            return enc.GetString(b, at, end - at).Trim();
        }

        // Снимок общей памяти целиком: чтение идёт по копии, чужая программа в это время пишет свою.
        public static byte[] Snapshot(string mapName, string mutexName, int maxBytes)
        {
            Mutex mutex = null;
            bool held = false;
            try
            {
                using (MemoryMappedFile map = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read))
                using (MemoryMappedViewAccessor view = map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    long cap = Math.Min(view.Capacity, maxBytes);
                    if (cap <= 0) return null;
                    if (mutexName != null && Mutex.TryOpenExisting(mutexName, System.Security.AccessControl.MutexRights.Synchronize, out mutex))
                    {
                        try { held = mutex.WaitOne(100); }
                        catch (AbandonedMutexException) { held = true; }
                    }
                    byte[] data = new byte[cap];
                    view.ReadArray(0, data, 0, data.Length);
                    return data;
                }
            }
            catch (System.IO.FileNotFoundException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (Exception ex) { CapLog.Report(ex); return null; }
            finally
            {
                if (mutex != null)
                {
                    if (held) { try { mutex.ReleaseMutex(); } catch { } }
                    mutex.Dispose();
                }
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  MSI Afterburner
    // ------------------------------------------------------------------ //
    internal static class HudMahm
    {
        public const string MapName = "MAHMSharedMemory";
        public const uint Signature = 0x4D41484D;         // 'MAHM'
        public const int EntryMinSize = 1324, GpuEntryMinSize = 1304;

        // Идентификаторы источников Afterburner, которые переводятся в общие показатели.
        public const uint SrcGpuTemp = 0x00, SrcFan = 0x10, SrcFanRpm = 0x11, SrcCoreClock = 0x20, SrcMemClock = 0x22,
                          SrcGpuUsage = 0x30, SrcVoltage = 0x40, SrcFramerate = 0x50, SrcFrametime = 0x51,
                          SrcFps1Low = 0x55, SrcFps01Low = 0x56, SrcPower = 0x61, SrcCpuTemp = 0x80;

        public sealed class Entry
        {
            public string Name, Units;
            public float Data;
            public uint Gpu, SrcId;
        }

        public sealed class Gpu
        {
            public string Id;          // «VEN_10DE&DEV_2684&SUBSYS_…&BUS_1&DEV_0&FN_0»
            public string Device;
            public uint MemoryMb;
        }

        public static bool Parse(byte[] b, List<Entry> entries, List<Gpu> gpus)
        {
            if (b == null || b.Length < 32 || HudBytes.U32(b, 0) != Signature) return false;
            uint version = HudBytes.U32(b, 4), header = HudBytes.U32(b, 8), count = HudBytes.U32(b, 12), size = HudBytes.U32(b, 16);
            if (version < 0x20000 || header < 32 || size < EntryMinSize || count > 4096) return false;
            Encoding ansi = Encoding.Default;
            for (uint i = 0; i < count; i++)
            {
                long at = header + (long)i * size;
                if (at + EntryMinSize > b.Length) break;
                int o = (int)at;
                Entry e = new Entry();
                e.Name = HudBytes.Str(b, o, 260, ansi);
                e.Units = HudBytes.Str(b, o + 260, 260, ansi);
                e.Data = HudBytes.F32(b, o + 1300);
                e.Gpu = HudBytes.U32(b, o + 1316);
                e.SrcId = HudBytes.U32(b, o + 1320);
                entries.Add(e);
            }
            uint gpuCount = HudBytes.U32(b, 24), gpuSize = HudBytes.U32(b, 28);
            if (gpus != null && gpuSize >= GpuEntryMinSize && gpuCount <= 16)
                for (uint i = 0; i < gpuCount; i++)
                {
                    long at = header + (long)count * size + (long)i * gpuSize;
                    if (at + GpuEntryMinSize > b.Length) break;
                    Gpu g = new Gpu();
                    g.Id = HudBytes.Str(b, (int)at, 260, ansi);
                    g.Device = HudBytes.Str(b, (int)at + 520, 260, ansi);
                    g.MemoryMb = HudBytes.U32(b, (int)at + 1300);
                    gpus.Add(g);
                }
            return true;
        }

        private static HudKind KindOf(Entry e)
        {
            string u = e.Units ?? "";
            if (u == "%") return HudKind.Percent;
            if (u.EndsWith("C", StringComparison.Ordinal) && u.Length <= 2) return HudKind.Temp;
            if (u == "MHz") return HudKind.Mhz;
            if (u == "W") return HudKind.Watts;
            if (u == "V") return HudKind.Volts;
            if (u == "RPM") return HudKind.Rpm;
            if (u == "FPS") return HudKind.Fps;
            if (u == "ms") return HudKind.Ms;
            return HudKind.Number;
        }

        // Все строки Afterburner — как есть (группа «MSI Afterburner»), плюс общие показатели для главной карты.
        public static void Publish(List<Entry> entries, List<Gpu> gpus, HudFrame f)
        {
            Publish(entries, gpus, f, 0, 0);
        }

        // vendorId/deviceId — главная карта по DXGI (0 — неизвестна).
        public static void Publish(List<Entry> entries, List<Gpu> gpus, HudFrame f, int vendorId, int deviceId)
        {
            uint main = MainIndex(gpus, vendorId, deviceId);
            foreach (Entry e in entries)
            {
                if (float.IsNaN(e.Data) || e.Data >= 3.0e38f) continue;
                HudKind kind = KindOf(e);
                string gpuPart = e.Gpu == 0xFFFFFFFF ? "x" : e.Gpu.ToString(CultureInfo.InvariantCulture);
                HudValue raw = new HudValue("ab." + e.SrcId.ToString(CultureInfo.InvariantCulture) + "." + gpuPart, kind, e.Data);
                raw.Label = e.Name;
                raw.Unit = e.Units;
                f.Put(raw);

                bool onMain = e.Gpu == main;
                string common = null;
                switch (e.SrcId)
                {
                    case SrcGpuTemp: if (onMain) common = "gpu.temp"; break;
                    case SrcFan: if (onMain) common = "gpu.fan"; break;
                    case SrcFanRpm: if (onMain) common = "gpu.fanrpm"; break;
                    case SrcCoreClock: if (onMain) common = "gpu.clock"; break;
                    case SrcMemClock: if (onMain) common = "gpu.memclock"; break;
                    case SrcVoltage: if (onMain) common = "gpu.voltage"; break;
                    case SrcPower: if (onMain) common = "gpu.power"; break;
                    case SrcCpuTemp: if (e.Gpu == 0xFFFFFFFF) common = "cpu.temp"; break;
                    case SrcFramerate: common = "fps"; break;
                    case SrcFrametime: common = "fps.frametime"; break;
                    case SrcFps1Low: common = "fps.low1"; break;
                    case SrcFps01Low: common = "fps.low01"; break;
                }
                if (common != null && f.Get(common) == null)
                {
                    HudValue c = new HudValue(common, HudCatalog.Find(common).Kind, e.Data);
                    c.Note = "MSI Afterburner";
                    f.Put(c);
                }
            }
        }

        // Главная карта Afterburner — та же, что главная у DXGI (VEN/DEV в идентификаторе карты), иначе — с наибольшей
        // памятью, иначе первая. Afterburner оставляет объём памяти нулём (RTX 4090 рядом со встроенной AMD: у обеих 0),
        // и без сверки по VEN/DEV главной оказывалась встроенная — напряжения и оборотов 4090 в общих строках не было.
        internal static uint MainIndex(List<Gpu> gpus, int vendorId, int deviceId)
        {
            if (gpus == null || gpus.Count == 0) return 0;
            if (vendorId != 0)
            {
                string want = "VEN_" + vendorId.ToString("X4", CultureInfo.InvariantCulture) + "&DEV_" + deviceId.ToString("X4", CultureInfo.InvariantCulture);
                for (int i = 0; i < gpus.Count; i++)
                    if (gpus[i].Id != null && gpus[i].Id.IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0) return (uint)i;
            }
            uint main = 0, best = 0;
            for (int i = 0; i < gpus.Count; i++)
                if (gpus[i].MemoryMb > best) { best = gpus[i].MemoryMb; main = (uint)i; }
            return main;
        }
    }

    // ------------------------------------------------------------------ //
    //  HWiNFO
    // ------------------------------------------------------------------ //
    internal static class HudHwinfo
    {
        public const string MapName = @"Global\HWiNFO_SENS_SM2", MutexName = @"Global\HWiNFO_SM2_MUTEX";
        public const uint Signature = 0x53695748;          // 'HWiS'
        public const int SensorMinSize = 264, ReadingMinSize = 316;

        public sealed class Sensor
        {
            public uint Id, Instance;
            public string Name;
            public string NameOrig;    // английское имя HWiNFO независимо от языка его интерфейса — по нему и сверяемся
        }

        public sealed class Reading
        {
            public uint Type, SensorIndex, Id;
            public string Label, Unit;
            public string LabelOrig;   // английская подпись; Label — на языке интерфейса HWiNFO («ЦП PPT»)
            public double Value, Min, Max, Avg;
        }

        // Отдаёт ли HWiNFO данные сейчас: читается один заголовок, без датчиков.
        public static bool Live()
        {
            byte[] b = HudBytes.Snapshot(MapName, MutexName, 44);
            if (b == null || b.Length < 44 || HudBytes.U32(b, 0) != Signature) return false;
            long poll = HudBytes.I64(b, 12);
            if (poll <= 0) return true;
            DateTime polled = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(poll);
            return Math.Abs((DateTime.UtcNow - polled).TotalSeconds) <= 15;
        }

        public const uint TypeTemp = 1, TypeVolt = 2, TypeFan = 3, TypeCurrent = 4, TypePower = 5, TypeClock = 6, TypeUsage = 7, TypeOther = 8;

        // pollTimeUnix — когда HWiNFO последний раз обновлял данные; остановленный HWiNFO оставляет старые числа.
        public static bool Parse(byte[] b, List<Sensor> sensors, List<Reading> readings, out long pollTimeUnix)
        {
            pollTimeUnix = 0;
            if (b == null || b.Length < 44 || HudBytes.U32(b, 0) != Signature) return false;
            pollTimeUnix = HudBytes.I64(b, 12);
            uint sOff = HudBytes.U32(b, 20), sSize = HudBytes.U32(b, 24), sCount = HudBytes.U32(b, 28);
            uint rOff = HudBytes.U32(b, 32), rSize = HudBytes.U32(b, 36), rCount = HudBytes.U32(b, 40);
            if (sSize < SensorMinSize || rSize < ReadingMinSize || sCount > 1024 || rCount > 16384) return false;
            Encoding ansi = Encoding.Default, utf = Encoding.UTF8;
            for (uint i = 0; i < sCount; i++)
            {
                long at = sOff + (long)i * sSize;
                if (at + sSize > b.Length) break;
                int o = (int)at;
                Sensor s = new Sensor();
                s.Id = HudBytes.U32(b, o);
                s.Instance = HudBytes.U32(b, o + 4);
                s.Name = sSize >= 392 ? HudBytes.Str(b, o + 264, 128, utf) : "";
                if (s.Name.Length == 0) s.Name = HudBytes.Str(b, o + 136, 128, ansi);
                s.NameOrig = HudBytes.Str(b, o + 8, 128, ansi);
                if (s.Name.Length == 0) s.Name = s.NameOrig;
                if (s.NameOrig.Length == 0) s.NameOrig = s.Name;
                sensors.Add(s);
            }
            for (uint i = 0; i < rCount; i++)
            {
                long at = rOff + (long)i * rSize;
                if (at + rSize > b.Length) break;
                int o = (int)at;
                Reading r = new Reading();
                r.Type = HudBytes.U32(b, o);
                r.SensorIndex = HudBytes.U32(b, o + 4);
                r.Id = HudBytes.U32(b, o + 8);
                r.Label = rSize >= 444 ? HudBytes.Str(b, o + 316, 128, utf) : "";
                if (r.Label.Length == 0) r.Label = HudBytes.Str(b, o + 140, 128, ansi);
                r.LabelOrig = HudBytes.Str(b, o + 12, 128, ansi);
                if (r.Label.Length == 0) r.Label = r.LabelOrig;
                if (r.LabelOrig.Length == 0) r.LabelOrig = r.Label;
                r.Unit = rSize >= 460 ? HudBytes.Str(b, o + 444, 16, utf) : "";
                if (r.Unit.Length == 0) r.Unit = HudBytes.Str(b, o + 268, 16, ansi);
                r.Value = HudBytes.F64(b, o + 284);
                r.Min = HudBytes.F64(b, o + 292);
                r.Max = HudBytes.F64(b, o + 300);
                r.Avg = HudBytes.F64(b, o + 308);
                readings.Add(r);
            }
            return true;
        }

        public static string IdOf(Sensor s, Reading r)
        {
            return "hw." + s.Id.ToString("x", CultureInfo.InvariantCulture) + "." + s.Instance.ToString("x", CultureInfo.InvariantCulture)
                 + "." + r.Id.ToString("x", CultureInfo.InvariantCulture);
        }

        private static HudKind KindOf(Reading r)
        {
            string u = r.Unit ?? "";
            switch (r.Type)
            {
                case TypeTemp: return u.EndsWith("C", StringComparison.Ordinal) ? HudKind.Temp : HudKind.Number;
                case TypeVolt: return u == "V" ? HudKind.Volts : HudKind.Number;
                case TypeFan: return u == "RPM" ? HudKind.Rpm : HudKind.Number;
                case TypePower: return u == "W" ? HudKind.Watts : HudKind.Number;
                case TypeClock: return u == "MHz" ? HudKind.Mhz : HudKind.Number;
                case TypeUsage: return u == "%" ? HudKind.Percent : HudKind.Number;
            }
            if (u == "FPS") return HudKind.Fps;
            if (u == "ms") return HudKind.Ms;
            return HudKind.Number;
        }

        private static bool Has(string text, string part) { return text != null && text.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0; }

        // Датчики, у которых «температура» — не температура места, а предел или запас до него.
        public static bool IsRealTemperature(string label)
        {
            return !(Has(label, "limit") || Has(label, "distance") || Has(label, "tjmax") || Has(label, "throttl") || Has(label, "critical")
                  || Has(label, "margin") || Has(label, "offset"));
        }

        // mainGpuName — имя главной карты (DXGI), чтобы горячая точка бралась с неё, а не со встроенной.
        public static void Publish(List<Sensor> sensors, List<Reading> readings, string mainGpuName, HudFrame f)
        {
            Dictionary<string, Reading> firstByLabel = new Dictionary<string, Reading>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, double> timing = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (Reading r in readings)
            {
                if (r.SensorIndex >= sensors.Count || !HudFormat.Valid(r.Value)) continue;
                Sensor s = sensors[(int)r.SensorIndex];
                HudValue v = new HudValue(IdOf(s, r), KindOf(r), r.Value);
                v.Label = r.Label;
                v.Unit = r.Unit;
                v.Note = s.Name;
                // Предел и запас до него — не температура места; сверка по английской подписи, русская её не выдаст.
                if (r.Type == TypeTemp && !IsRealTemperature(r.LabelOrig)) v.NotPlace = true;
                f.Put(v);

                if (Has(s.NameOrig, "Memory Timings"))
                    timing[r.LabelOrig] = r.Value;
            }

            Pick(f, "cpu.temp", sensors, readings, TypeTemp, IsCpu, "CPU (Tctl/Tdie)", "CPU Package", "Core Max", "CPU Die (average)", "CPU (Tdie)", "CPU");
            Pick(f, "cpu.power", sensors, readings, TypePower, IsCpu, "CPU Package Power", "CPU PPT", "CPU Power");
            Pick(f, "cpu.voltage", sensors, readings, TypeVolt, IsCpu, "CPU Core Voltage (SVI3 VR Out)", "CPU VDDCR_VDD Voltage (SVI3 TFN)", "CPU Core Voltage (SVI2 TFN)", "Vcore", "CPU Core Voltage");
            Predicate<Sensor> gpu = delegate(Sensor s) { return IsGpu(s, mainGpuName); };
            Pick(f, "gpu.hotspot", sensors, readings, TypeTemp, gpu, "GPU Hot Spot Temperature", "GPU Hotspot Temperature", "GPU Hot Spot");
            Pick(f, "gpu.memtemp", sensors, readings, TypeTemp, gpu, "GPU Memory Junction Temperature", "GPU Memory Temperature");
            Pick(f, "gpu.fanrpm", sensors, readings, TypeFan, gpu, "GPU Fan1", "GPU Fan");
            Pick(f, "gpu.voltage", sensors, readings, TypeVolt, gpu, "GPU Core Voltage");

            string t = Timings(timing);
            if (t != null)
            {
                HudValue v = new HudValue("mem.timings", HudKind.Text, double.NaN);
                v.Text = t;
                v.Note = "HWiNFO";
                f.Put(v);
            }
        }

        // Имена сверяются по английскому оригиналу: русский HWiNFO называет датчик «ЦП [#0]», и без этого
        // мощность и напряжение процессора, горячая точка и память видеокарты оставались «нет данных».
        private static bool IsCpu(Sensor s) { return s.NameOrig.StartsWith("CPU", StringComparison.OrdinalIgnoreCase); }

        private static bool IsGpu(Sensor s, string mainGpuName)
        {
            // Новые версии HWiNFO пишут «dGPU [#0]» (дискретная) и «iGPU [#1]» (встроенная), старые — «GPU [#0]».
            string name = s.NameOrig;
            if (name.StartsWith("dGPU", StringComparison.OrdinalIgnoreCase) || name.StartsWith("iGPU", StringComparison.OrdinalIgnoreCase)) name = name.Substring(1);
            if (!name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.IsNullOrEmpty(mainGpuName)) return true;
            // «GPU [#1]: NVIDIA GeForce RTX 4090: …» — имя карты внутри.
            return name.IndexOf(mainGpuName, StringComparison.OrdinalIgnoreCase) >= 0
                || mainGpuName.IndexOf(StripGpuPrefix(name), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string StripGpuPrefix(string name)
        {
            int c = name.IndexOf("]: ", StringComparison.Ordinal);
            string rest = c >= 0 ? name.Substring(c + 3) : name;
            int tail = rest.IndexOf(": ", StringComparison.Ordinal);
            return (tail >= 0 ? rest.Substring(0, tail) : rest).Trim();
        }

        // Первое по списку предпочтений показание нужного типа в подходящем датчике.
        private static void Pick(HudFrame f, string id, List<Sensor> sensors, List<Reading> readings, uint type, Predicate<Sensor> sensorOk, params string[] labels)
        {
            if (f.Get(id) != null) return;
            foreach (string wanted in labels)
                foreach (Reading r in readings)
                {
                    if (r.Type != type || r.SensorIndex >= sensors.Count || !HudFormat.Valid(r.Value)) continue;
                    Sensor s = sensors[(int)r.SensorIndex];
                    if (!sensorOk(s) || !string.Equals(r.LabelOrig, wanted, StringComparison.OrdinalIgnoreCase)) continue;
                    HudValue v = new HudValue(id, HudCatalog.Find(id).Kind, r.Value);
                    v.Note = s.Name + " · " + r.Label;
                    f.Put(v);
                    return;
                }
        }

        // «6000 МГц · CL30-36-36-76 · 1T»; нет основных таймингов — null.
        public static string Timings(Dictionary<string, double> t)
        {
            double cl, rcd, rp, ras, freq, cr;
            if (!Find(t, out cl, "Tcas", "CL", "CAS Latency")) return null;
            StringBuilder sb = new StringBuilder();
            if (Find(t, out freq, "Memory Frequency", "Frequency") && freq > 0)
                sb.Append(Math.Round(freq).ToString("0", CultureInfo.InvariantCulture)).Append(Tr.S(" МГц · ", " MHz · "));
            sb.Append("CL").Append(Num(cl));
            if (Find(t, out rcd, "Trcd", "tRCD")) sb.Append('-').Append(Num(rcd));
            if (Find(t, out rp, "Trp", "tRP")) sb.Append('-').Append(Num(rp));
            if (Find(t, out ras, "Tras", "tRAS")) sb.Append('-').Append(Num(ras));
            if (Find(t, out cr, "Command Rate", "CR")) sb.Append(" · ").Append(Num(cr)).Append('T');
            return sb.ToString();
        }

        private static string Num(double v)
        {
            return v == Math.Floor(v) ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static bool Find(Dictionary<string, double> t, out double value, params string[] names)
        {
            foreach (string n in names)
                if (t.TryGetValue(n, out value) && HudFormat.Valid(value)) return true;
            value = double.NaN;
            return false;
        }
    }
}
