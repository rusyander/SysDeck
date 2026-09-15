// SysDeck — «Сведения о системе»: процессор, плата, BIOS, модули памяти, видеокарты. Источник —
// сырая таблица SMBIOS (GetSystemFirmwareTable 'RSMB'), без WMI и без прав администратора; имя процессора — из
// реестра (в SMBIOS у многих плат там сокращение); видеокарты — DXGI и NVML. Тайминги памяти в SMBIOS нет — их
// знает только тот, кто читает SPD модулей драйвером (HWiNFO), поэтому здесь только частота, напряжение и маркировка;
// паспортные частота и CL угадываются по партномеру у производителей с понятной схемой.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SysDeck.Capture
{
    internal sealed class SmbiosMemory
    {
        public string Locator, Bank, Manufacturer, PartNumber, Serial;
        public long SizeMb;
        public int Type;              // 0x1A DDR4, 0x22 DDR5 …
        public int SpeedMts, ConfiguredMts, MinMv, MaxMv, ConfiguredMv, Rank;

        public string TypeName
        {
            get
            {
                switch (Type)
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
                    default: return "";
                }
            }
        }
    }

    internal sealed class SmbiosInfo
    {
        public string BiosVendor, BiosVersion, BiosDate, SystemMaker, SystemProduct, BoardMaker, BoardProduct, BoardVersion;
        public string CpuName, CpuSocket;
        public int CpuCores, CpuThreads, CpuMaxMhz;
        public string Version;
        public readonly List<SmbiosMemory> Memory = new List<SmbiosMemory>();
    }

    internal static class Smbios
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[] buffer, uint size);

        public static SmbiosInfo Read()
        {
            try
            {
                const uint RSMB = 0x52534D42;
                uint size = GetSystemFirmwareTable(RSMB, 0, null, 0);
                if (size == 0 || size > 16 * 1024 * 1024) return null;
                byte[] buf = new byte[size];
                if (GetSystemFirmwareTable(RSMB, 0, buf, size) == 0) return null;
                return Parse(buf);
            }
            catch (Exception ex) { CapLog.Report(ex); return null; }
        }

        // RawSMBIOSData: 4 байта версии, DWORD длины таблицы, затем структуры: тип, длина, дескриптор, поля, строки.
        public static SmbiosInfo Parse(byte[] raw)
        {
            if (raw == null || raw.Length < 8) return null;
            SmbiosInfo info = new SmbiosInfo();
            info.Version = raw[1].ToString(CultureInfo.InvariantCulture) + "." + raw[2].ToString(CultureInfo.InvariantCulture);
            int length = (int)Math.Min(BitConverter.ToUInt32(raw, 4), (uint)(raw.Length - 8));
            int p = 8, end = 8 + length;
            while (p + 4 <= end)
            {
                int type = raw[p], len = raw[p + 1];
                if (len < 4 || p + len > end) break;
                List<string> strings = new List<string>();
                int s = p + len;
                // Строки до двойного нуля. Структура без строк — сразу два нуля.
                if (s + 1 < end && raw[s] == 0 && raw[s + 1] == 0) s += 2;
                else
                {
                    while (s < end)
                    {
                        int z = s;
                        while (z < end && raw[z] != 0) z++;
                        strings.Add(Encoding.ASCII.GetString(raw, s, z - s).Trim());
                        s = z + 1;
                        if (s < end && raw[s] == 0) { s++; break; }
                    }
                }
                Structure(info, type, raw, p, len, strings);
                if (type == 127) break;
                p = s;
            }
            return info;
        }

        private static string Str(List<string> strings, byte[] raw, int at, int p, int len)
        {
            if (at >= len) return "";
            int index = raw[p + at];
            return index >= 1 && index <= strings.Count ? strings[index - 1] : "";
        }

        private static int Byte(byte[] raw, int p, int len, int at) { return at < len ? raw[p + at] : 0; }
        private static int Word(byte[] raw, int p, int len, int at) { return at + 1 < len ? BitConverter.ToUInt16(raw, p + at) : 0; }
        private static long Dword(byte[] raw, int p, int len, int at) { return at + 3 < len ? BitConverter.ToUInt32(raw, p + at) : 0; }

        private static void Structure(SmbiosInfo info, int type, byte[] raw, int p, int len, List<string> st)
        {
            switch (type)
            {
                case 0:
                    info.BiosVendor = Str(st, raw, 4, p, len);
                    info.BiosVersion = Str(st, raw, 5, p, len);
                    info.BiosDate = Str(st, raw, 8, p, len);
                    break;
                case 1:
                    info.SystemMaker = Str(st, raw, 4, p, len);
                    info.SystemProduct = Str(st, raw, 5, p, len);
                    break;
                case 2:
                    if (info.BoardProduct != null) break;
                    info.BoardMaker = Str(st, raw, 4, p, len);
                    info.BoardProduct = Str(st, raw, 5, p, len);
                    info.BoardVersion = Str(st, raw, 6, p, len);
                    break;
                case 4:
                {
                    if (info.CpuName != null) break;
                    info.CpuSocket = Str(st, raw, 4, p, len);
                    info.CpuName = Str(st, raw, 0x10, p, len);
                    info.CpuMaxMhz = Word(raw, p, len, 0x14);
                    int cores = Byte(raw, p, len, 0x23), threads = Byte(raw, p, len, 0x25);
                    if (cores == 0xFF) cores = Word(raw, p, len, 0x2A);
                    if (threads == 0xFF) threads = Word(raw, p, len, 0x2E);
                    info.CpuCores = cores;
                    info.CpuThreads = threads;
                    break;
                }
                case 17:
                {
                    int size = Word(raw, p, len, 0x0C);
                    if (size == 0 || size == 0xFFFF) break;       // пустой слот / неизвестно
                    SmbiosMemory m = new SmbiosMemory();
                    if (size == 0x7FFF) m.SizeMb = Dword(raw, p, len, 0x1C) & 0x7FFFFFFF;
                    else m.SizeMb = (size & 0x8000) != 0 ? (size & 0x7FFF) / 1024 : size;
                    m.Locator = Str(st, raw, 0x10, p, len);
                    m.Bank = Str(st, raw, 0x11, p, len);
                    m.Type = Byte(raw, p, len, 0x12);
                    m.SpeedMts = Word(raw, p, len, 0x15);
                    if (m.SpeedMts == 0xFFFF) m.SpeedMts = (int)Dword(raw, p, len, 0x54);
                    m.Manufacturer = Str(st, raw, 0x17, p, len);
                    m.Serial = Str(st, raw, 0x18, p, len);
                    m.PartNumber = Str(st, raw, 0x1A, p, len);
                    m.Rank = Byte(raw, p, len, 0x1B) & 0x0F;
                    m.ConfiguredMts = Word(raw, p, len, 0x20);
                    if (m.ConfiguredMts == 0xFFFF) m.ConfiguredMts = (int)Dword(raw, p, len, 0x58);
                    m.MinMv = Word(raw, p, len, 0x22);
                    m.MaxMv = Word(raw, p, len, 0x24);
                    m.ConfiguredMv = Word(raw, p, len, 0x26);
                    info.Memory.Add(m);
                    break;
                }
            }
        }

        // Паспорт комплекта по партномеру: частота и CL. Только понятные схемы; остальное — null, не угадываем.
        //  Corsair  CMH96GX5M2B6600C32  → 6600, CL32
        //  G.Skill  F5-6000J3038F16GX2  → 6000, CL30
        //  Kingston KF556C36BBEK2-32    → 5600, CL36 (KF + поколение + сотни МГц)
        public static bool RatedFromPart(string part, out int mts, out int cl)
        {
            mts = 0; cl = 0;
            if (string.IsNullOrEmpty(part)) return false;
            string p = part.Trim().ToUpperInvariant();
            Match m = Regex.Match(p, @"^CM[A-Z0-9]*?(\d{4})C(\d{2})");
            if (m.Success) return Take(m, 1, 2, 1, out mts, out cl);
            m = Regex.Match(p, @"^F\d-(\d{4})[A-Z](\d{2})");
            if (m.Success) return Take(m, 1, 2, 1, out mts, out cl);
            m = Regex.Match(p, @"^KF\d(\d{2})C(\d{2})");
            if (m.Success) return Take(m, 1, 2, 100, out mts, out cl);
            return false;
        }

        private static bool Take(Match m, int speedGroup, int clGroup, int speedScale, out int mts, out int cl)
        {
            mts = int.Parse(m.Groups[speedGroup].Value, CultureInfo.InvariantCulture) * speedScale;
            cl = int.Parse(m.Groups[clGroup].Value, CultureInfo.InvariantCulture);
            return mts >= 800 && mts <= 20000 && cl >= 4 && cl <= 99;
        }
    }

    // Готовые строки для страницы и для оверлея. Собирается редко (раз в минуту и по открытию страницы).
    internal sealed class HudSysInfo
    {
        public sealed class Line
        {
            public string Section, Key, Value;
            public Line(string section, string key, string value) { Section = section; Key = key; Value = value; }
        }

        public readonly List<Line> Lines = new List<Line>();
        public string CpuText, BoardText, MemoryText, GpuText;

        private void Add(string section, string key, string value)
        {
            if (!string.IsNullOrEmpty(value)) Lines.Add(new Line(section, key, value));
        }

        private static string I(long v) { return v.ToString(CultureInfo.InvariantCulture); }

        private static string Volts(int mv) { return (mv / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + Tr.S(" В", " V"); }

        public static string RegistryCpuName()
        {
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0"))
                {
                    string name = k == null ? null : k.GetValue("ProcessorNameString") as string;
                    return name == null ? null : Regex.Replace(name.Trim(), @"\s+", " ");
                }
            }
            catch { return null; }
        }

        public static HudSysInfo Collect()
        {
            return Build(Smbios.Read(), RegistryCpuName(), Environment.ProcessorCount, SafeAdapters(), HudNvml.Shared.StaticInfo());
        }

        private static List<DxgiAdapter> SafeAdapters()
        {
            try { return Native.DxgiAdapters(); }
            catch { return new List<DxgiAdapter>(); }
        }

        public static HudSysInfo Build(SmbiosInfo sm, string cpuName, int logical, List<DxgiAdapter> adapters, List<KeyValuePair<string, string>> nvml)
        {
            HudSysInfo r = new HudSysInfo();
            string cpu = Tr.S("Процессор", "CPU"), board = Tr.S("Плата и BIOS", "Board and BIOS"),
                   mem = Tr.S("Память", "Memory"), gpu = Tr.S("Видеокарты", "GPUs");

            string name = !string.IsNullOrEmpty(cpuName) ? cpuName : sm != null ? sm.CpuName : null;
            r.Add(cpu, Tr.S("Модель", "Model"), name);
            if (sm != null)
            {
                if (sm.CpuCores > 0) r.Add(cpu, Tr.S("Ядра / потоки", "Cores / threads"), I(sm.CpuCores) + " / " + I(sm.CpuThreads > 0 ? sm.CpuThreads : logical));
                r.Add(cpu, Tr.S("Сокет", "Socket"), sm.CpuSocket);
            }
            else r.Add(cpu, Tr.S("Логических процессоров", "Logical processors"), I(logical));
            r.CpuText = name == null ? null : name + (sm != null && sm.CpuCores > 0 ? " · " + I(sm.CpuCores) + "/" + I(sm.CpuThreads) : "");

            if (sm != null)
            {
                string boardName = Join(" ", sm.BoardMaker, sm.BoardProduct);
                r.Add(board, Tr.S("Плата", "Board"), boardName + (string.IsNullOrEmpty(sm.BoardVersion) || sm.BoardVersion == "Default string" ? "" : " (" + sm.BoardVersion + ")"));
                r.Add(board, Tr.S("Система", "System"), Join(" ", sm.SystemMaker, sm.SystemProduct));
                r.Add(board, "BIOS", Join(" · ", sm.BiosVendor, sm.BiosVersion, sm.BiosDate));
                r.Add(board, "SMBIOS", sm.Version);
                r.BoardText = Join(" · ", boardName, string.IsNullOrEmpty(sm.BiosVersion) ? null : "BIOS " + sm.BiosVersion);

                long totalMb = 0;
                foreach (SmbiosMemory m in sm.Memory) totalMb += m.SizeMb;
                if (sm.Memory.Count > 0)
                {
                    SmbiosMemory first = sm.Memory[0];
                    bool same = true;
                    foreach (SmbiosMemory m in sm.Memory)
                        if (m.SizeMb != first.SizeMb || m.PartNumber != first.PartNumber || m.ConfiguredMts != first.ConfiguredMts) same = false;
                    string speed = first.ConfiguredMts > 0 ? I(first.ConfiguredMts) + Tr.S(" МТ/с", " MT/s") : "";
                    r.Add(mem, Tr.S("Всего", "Total"), Gb(totalMb) + " · " + I(sm.Memory.Count) + Tr.S(" модул.", " modules"));
                    foreach (SmbiosMemory m in sm.Memory)
                    {
                        StringBuilder sb = new StringBuilder();
                        sb.Append(Gb(m.SizeMb));
                        if (m.TypeName.Length > 0) sb.Append(' ').Append(m.TypeName);
                        if (m.ConfiguredMts > 0) sb.Append(" · ").Append(I(m.ConfiguredMts)).Append(Tr.S(" МТ/с", " MT/s"));
                        if (m.SpeedMts > 0 && m.SpeedMts != m.ConfiguredMts) sb.Append(Tr.S(" (базовая JEDEC ", " (JEDEC base ")).Append(I(m.SpeedMts)).Append(')');
                        if (m.ConfiguredMv > 0) sb.Append(" · ").Append(Volts(m.ConfiguredMv));
                        if (m.Rank > 0) sb.Append(" · ").Append(m.Rank == 1 ? Tr.S("одноранговый", "single rank") : I(m.Rank) + Tr.S(" ранга", " ranks"));
                        sb.Append(" · ").Append(Join(" ", m.Manufacturer, m.PartNumber));
                        int rated, cl;
                        if (Smbios.RatedFromPart(m.PartNumber, out rated, out cl))
                            sb.Append(Tr.S(" · по маркировке ", " · rated ")).Append(I(rated)).Append(" CL").Append(I(cl));
                        r.Add(mem, Join(" / ", m.Bank, m.Locator), sb.ToString());
                    }
                    string kit = same ? I(sm.Memory.Count) + " × " + Gb(first.SizeMb) : Gb(totalMb);
                    r.MemoryText = Join(" ", kit, first.TypeName) + (speed.Length > 0 ? " · " + speed : "")
                                 + (first.ConfiguredMv > 0 ? " · " + Volts(first.ConfiguredMv) : "")
                                 + (same && !string.IsNullOrEmpty(first.PartNumber) ? " · " + first.PartNumber : "");
                }
            }

            List<string> gpuNames = new List<string>();
            if (adapters != null)
                foreach (DxgiAdapter a in adapters)
                {
                    if (a.Software) continue;
                    string vram = a.Dedicated > 0 ? " · " + (a.Dedicated / 1073741824.0).ToString("0.#", CultureInfo.InvariantCulture) + Tr.S(" ГБ", " GB") : "";
                    r.Add(gpu, a.Name, Tr.S("видеопамять", "video memory") + vram.Replace(" · ", " ") + " · VEN_" + a.VendorId.ToString("X4") + "&DEV_" + a.DeviceId.ToString("X4"));
                    gpuNames.Add(a.Name + vram);
                }
            string driver = null;
            if (nvml != null)
                foreach (KeyValuePair<string, string> kv in nvml)
                {
                    r.Add(gpu, kv.Key, kv.Value);
                    if (kv.Key == "NVIDIA driver") driver = kv.Value;
                }
            r.GpuText = gpuNames.Count == 0 ? null : string.Join(" + ", gpuNames.ToArray()) + (driver != null ? Tr.S(" · драйвер ", " · driver ") + driver : "");
            return r;
        }

        private static string Gb(long mb)
        {
            double gb = mb / 1024.0;
            return gb.ToString(gb == Math.Floor(gb) ? "0" : "0.#", CultureInfo.InvariantCulture) + Tr.S(" ГБ", " GB");
        }

        private static string Join(string sep, params string[] parts)
        {
            List<string> keep = new List<string>();
            foreach (string p in parts) if (!string.IsNullOrEmpty(p) && p != "Default string" && p != "To Be Filled By O.E.M.") keep.Add(p);
            return string.Join(sep, keep.ToArray());
        }
    }
}
