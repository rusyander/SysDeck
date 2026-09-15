// SysDeck — область «hud», часть 2: разбор общей памяти MSI Afterburner и HWiNFO на синтетических буферах того же
// формата и на живых данных, если программы запущены.
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
        // ---- MSI Afterburner ----
        private static void PutStr(byte[] b, int at, string s)
        {
            byte[] raw = Encoding.Default.GetBytes(s);
            Array.Copy(raw, 0, b, at, raw.Length);
        }

        private static void Put(byte[] b, int at, uint v) { Array.Copy(BitConverter.GetBytes(v), 0, b, at, 4); }

        private static byte[] MahmBuffer(object[][] entries, uint[] gpuMemoryMb)
        {
            return MahmBuffer(entries, gpuMemoryMb, null);
        }

        private static byte[] MahmBuffer(object[][] entries, uint[] gpuMemoryMb, string[] gpuIds)
        {
            const int header = 32, size = 1324, gpuSize = 1304;
            byte[] b = new byte[header + entries.Length * size + gpuMemoryMb.Length * gpuSize];
            Put(b, 0, HudMahm.Signature); Put(b, 4, 0x20000); Put(b, 8, header); Put(b, 12, (uint)entries.Length); Put(b, 16, size);
            Put(b, 24, (uint)gpuMemoryMb.Length); Put(b, 28, gpuSize);
            for (int i = 0; i < entries.Length; i++)
            {
                int o = header + i * size;
                PutStr(b, o, (string)entries[i][0]);
                PutStr(b, o + 260, (string)entries[i][1]);
                Array.Copy(BitConverter.GetBytes((float)entries[i][2]), 0, b, o + 1300, 4);
                Put(b, o + 1316, (uint)entries[i][3]);
                Put(b, o + 1320, (uint)entries[i][4]);
            }
            for (int i = 0; i < gpuMemoryMb.Length; i++)
            {
                Put(b, header + entries.Length * size + i * gpuSize + 1300, gpuMemoryMb[i]);
                if (gpuIds != null) PutStr(b, header + entries.Length * size + i * gpuSize, gpuIds[i]);
            }
            return b;
        }

        private static void Mahm()
        {
            byte[] buf = MahmBuffer(new[]
            {
                new object[] { "GPU1 temperature", "°C", 44f, 0u, HudMahm.SrcGpuTemp },
                new object[] { "GPU2 temperature", "°C", 61f, 1u, HudMahm.SrcGpuTemp },
                new object[] { "GPU2 fan tachometer", "RPM", 1210f, 1u, HudMahm.SrcFanRpm },
                new object[] { "CPU temperature", "°C", 49.75f, 0xFFFFFFFFu, HudMahm.SrcCpuTemp },
                new object[] { "Framerate", "FPS", 3.4e38f, 0xFFFFFFFFu, HudMahm.SrcFramerate },
            }, new uint[] { 512, 24564 });
            List<HudMahm.Entry> entries = new List<HudMahm.Entry>();
            List<HudMahm.Gpu> gpus = new List<HudMahm.Gpu>();
            T.Check("hud mahm: buffer parses", HudMahm.Parse(buf, entries, gpus) && entries.Count == 5 && gpus.Count == 2);
            HudFrame f = new HudFrame();
            HudMahm.Publish(entries, gpus, f);
            T.Eq("hud mahm: gpu.temp is taken from the card with the most memory", 61.0, f.Get("gpu.temp").Value);
            T.Eq("hud mahm: fan RPM of the main card", 1210.0, f.Get("gpu.fanrpm").Value);
            T.Check("hud mahm: total cpu temperature maps to cpu.temp", Math.Abs(f.Get("cpu.temp").Value - 49.75) < 0.01);
            T.Check("hud mahm: raw rows keep the Afterburner name", f.Get("ab.0.0") != null && f.Get("ab.0.0").Label == "GPU1 temperature" && f.Get("ab.0.0").Kind == HudKind.Temp);
            T.Check("hud mahm: FLT_MAX (no data) is skipped", f.Get("fps") == null && f.Get("ab.80.x") == null);
            T.Check("hud mahm: wrong signature is rejected", !HudMahm.Parse(new byte[64], new List<HudMahm.Entry>(), null));

            // Типичная связка: встроенная AMD первой, RTX 4090 второй, объём памяти у обеих 0, напряжение — только у 4090.
            byte[] zero = MahmBuffer(new[]
            {
                new object[] { "GPU1 power", "W", 32f, 0u, HudMahm.SrcPower },
                new object[] { "GPU2 power", "W", 66.9f, 1u, HudMahm.SrcPower },
                new object[] { "GPU2 voltage", "V", 0.895f, 1u, HudMahm.SrcVoltage },
            }, new uint[] { 0, 0 }, new[] { "VEN_1002&DEV_164E&SUBSYS_D0001458&REV_C5&BUS_18&DEV_0&FN_0", "VEN_10DE&DEV_2684&SUBSYS_88E01043&REV_A1&BUS_1&DEV_0&FN_0" });
            entries.Clear(); gpus.Clear();
            HudMahm.Parse(zero, entries, gpus);
            HudFrame zf = new HudFrame();
            HudMahm.Publish(entries, gpus, zf, 0x10DE, 0x2684);
            T.Check("hud mahm: zero memory amounts — main card found by VEN/DEV of the DXGI primary",
                    zf.Get("gpu.voltage") != null && Math.Abs(zf.Get("gpu.voltage").Value - 0.895) < 0.001 && Math.Abs(zf.Get("gpu.power").Value - 66.9) < 0.01);
            T.Eq("hud mahm: unknown DXGI card — falls back to the first", 0u, HudMahm.MainIndex(gpus, 0, 0));

            byte[] live = HudBytes.Snapshot(HudMahm.MapName, null, 4 * 1024 * 1024);
            if (live == null) { T.Skip("hud mahm live: MSI Afterburner shared memory parses", "Afterburner is not running"); return; }
            entries.Clear(); gpus.Clear();
            bool ok = HudMahm.Parse(live, entries, gpus);
            HudFrame lf = new HudFrame();
            if (ok) HudMahm.Publish(entries, gpus, lf);
            T.Check("hud mahm live: running Afterburner is read without admin rights", ok && entries.Count > 0 && lf.Values.Count > 0, entries.Count + " entries");
            // Живой путь источника: если Afterburner публикует напряжение главной (по DXGI) карты — строка «ГП В» есть.
            DxgiAdapter primary = HudAdapters.Primary();
            uint mainIdx = primary == null ? 0 : HudMahm.MainIndex(gpus, primary.VendorId, primary.DeviceId);
            bool hasVoltage = false;
            foreach (HudMahm.Entry e in entries) if (e.SrcId == HudMahm.SrcVoltage && e.Gpu == mainIdx && e.Data < 3.0e38f) hasVoltage = true;
            if (!hasVoltage) { T.Skip("hud mahm live: GPU voltage of the main card reaches gpu.voltage", "Afterburner publishes no voltage for the main card"); return; }
            HudMahmSource src = new HudMahmSource();
            HudFrame sf = new HudFrame();
            src.Collect(sf);
            T.Check("hud mahm live: GPU voltage of the main card reaches gpu.voltage", sf.Get("gpu.voltage") != null,
                    primary == null ? "no DXGI primary" : primary.Name + " idx " + mainIdx);
        }

        // ---- HWiNFO ----
        // Имя датчика «оригинал|как в интерфейсе»; у показания шестой элемент — подпись на языке интерфейса. Без них обе одинаковые.
        private static byte[] HwinfoBuffer(string[] sensorNames, object[][] readings, long pollUnix)
        {
            const int header = 48, sSize = 392, rSize = 460;
            byte[] b = new byte[header + sensorNames.Length * sSize + readings.Length * rSize];
            Put(b, 0, HudHwinfo.Signature); Put(b, 4, 2); Put(b, 8, 2);
            Array.Copy(BitConverter.GetBytes(pollUnix), 0, b, 12, 8);
            Put(b, 20, header); Put(b, 24, sSize); Put(b, 28, (uint)sensorNames.Length);
            Put(b, 32, (uint)(header + sensorNames.Length * sSize)); Put(b, 36, rSize); Put(b, 40, (uint)readings.Length);
            for (int i = 0; i < sensorNames.Length; i++)
            {
                int o = header + i * sSize;
                Put(b, o, 0xF000u + (uint)i);
                string[] names = sensorNames[i].Split('|');
                PutStr(b, o + 8, names[0]);
                byte[] utf = Encoding.UTF8.GetBytes(names[names.Length - 1]);
                Array.Copy(utf, 0, b, o + 264, utf.Length);
            }
            for (int i = 0; i < readings.Length; i++)
            {
                int o = header + sensorNames.Length * sSize + i * rSize;
                Put(b, o, (uint)readings[i][0]);
                Put(b, o + 4, (uint)readings[i][1]);
                Put(b, o + 8, 0x1000000u + (uint)i);
                PutStr(b, o + 12, (string)readings[i][2]);
                byte[] utf = Encoding.UTF8.GetBytes((string)readings[i][readings[i].Length > 5 ? 5 : 2]);
                Array.Copy(utf, 0, b, o + 316, utf.Length);
                byte[] unit = Encoding.UTF8.GetBytes((string)readings[i][3]);
                Array.Copy(unit, 0, b, o + 444, unit.Length);
                Array.Copy(BitConverter.GetBytes((double)readings[i][4]), 0, b, o + 284, 8);
            }
            return b;
        }

        private static void Hwinfo()
        {
            long now = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            byte[] buf = HwinfoBuffer(
                new[] { "CPU [#0]: AMD Ryzen 9 7950X3D: Enhanced", "GPU [#0]: AMD Radeon(TM) Graphics", "GPU [#1]: NVIDIA GeForce RTX 4090: ", "Memory Timings" },
                new[]
                {
                    new object[] { HudHwinfo.TypeTemp, 0u, "Core Max", "°C", 71.0 },
                    new object[] { HudHwinfo.TypeTemp, 0u, "CPU (Tctl/Tdie)", "°C", 68.5 },
                    new object[] { HudHwinfo.TypeTemp, 0u, "Distance to TjMAX", "°C", 120.0 },
                    new object[] { HudHwinfo.TypePower, 0u, "CPU Package Power", "W", 88.2 },
                    new object[] { HudHwinfo.TypeTemp, 1u, "GPU Hot Spot Temperature", "°C", 55.0 },
                    new object[] { HudHwinfo.TypeTemp, 2u, "GPU Hot Spot Temperature", "°C", 77.0 },
                    new object[] { HudHwinfo.TypeTemp, 2u, "GPU Memory Junction Temperature", "°C", 74.0 },
                    new object[] { HudHwinfo.TypeClock, 3u, "Memory Frequency", "MHz", 5999.6 },
                    new object[] { HudHwinfo.TypeOther, 3u, "Tcas", "T", 30.0 },
                    new object[] { HudHwinfo.TypeOther, 3u, "Trcd", "T", 36.0 },
                    new object[] { HudHwinfo.TypeOther, 3u, "Trp", "T", 36.0 },
                    new object[] { HudHwinfo.TypeOther, 3u, "Tras", "T", 76.0 },
                    new object[] { HudHwinfo.TypeOther, 3u, "Command Rate", "T", 1.0 },
                }, now);
            List<HudHwinfo.Sensor> sensors = new List<HudHwinfo.Sensor>();
            List<HudHwinfo.Reading> readings = new List<HudHwinfo.Reading>();
            long poll;
            T.Check("hud hwinfo: buffer parses (utf-8 names)", HudHwinfo.Parse(buf, sensors, readings, out poll)
                    && sensors.Count == 4 && readings.Count == 13 && poll == now && sensors[0].Name.StartsWith("CPU [#0]"));
            HudFrame f = new HudFrame();
            HudHwinfo.Publish(sensors, readings, "NVIDIA GeForce RTX 4090", f);
            T.Eq("hud hwinfo: cpu.temp prefers Tctl/Tdie over Core Max", 68.5, f.Get("cpu.temp").Value);
            T.Eq("hud hwinfo: cpu power", 88.2, f.Get("cpu.power").Value);
            T.Eq("hud hwinfo: hot spot is taken from the main card, not the iGPU", 77.0, f.Get("gpu.hotspot").Value);
            T.Eq("hud hwinfo: memory junction", 74.0, f.Get("gpu.memtemp").Value);
            T.Eq("hud hwinfo: timings line", Tr.S("6000 МГц · CL30-36-36-76 · 1T", "6000 MHz · CL30-36-36-76 · 1T"), f.Get("mem.timings").Text);
            string rawId = HudHwinfo.IdOf(sensors[0], readings[0]);
            T.Check("hud hwinfo: every reading gets a stable id with the sensor as group", f.Get(rawId) != null && f.Get(rawId).Note == sensors[0].Name
                    && HudItem.ValidId(rawId) && HudCatalog.Find(rawId).Group == HudGroups.HwinfoPrefix + "?", rawId);

            HudSource hw = new FakeSource(f);
            HudFrame merged = HudCollector.Merge(new List<HudSource> { hw });
            HudValue hot = merged.Get("hot.max");
            T.Check("hud: hottest point skips «Distance to TjMAX» and names the sensor", hot != null && hot.Value == 77.0 && hot.Note.Contains("GPU Hot Spot"),
                    hot == null ? "null" : hot.Value + " " + hot.Note);

            HudFrame acpi = new HudFrame();
            HudValue board = new HudValue("cpu.temp", HudKind.Temp, 40);
            acpi.Put(board);
            acpi.Put("gpu.load", HudKind.Percent, 12);
            HudFrame m2 = HudCollector.Merge(new List<HudSource> { hw, new FakeSource(acpi) });
            T.Check("hud: an earlier source wins (HWiNFO core sensor over ACPI), later ones fill the gaps",
                    m2.Get("cpu.temp").Value == 68.5 && m2.Get("gpu.load").Value == 12);

            // Русский интерфейс HWiNFO: отображаемые имена переведены, английские оригиналы лежат рядом. Раньше сверка шла по
            // переведённым — мощность ЦП, горячая точка и память ГП были «нет данных», а «Тепловой предел ГП» 80 °C
            // становился «самой горячей точкой».
            byte[] ru = HwinfoBuffer(
                new[] { "CPU [#0]: AMD Ryzen 9 7950X: Enhanced|ЦП [#0]: AMD Ryzen 9 7950X: Enhanced", "iGPU [#1]: AMD Radeon",
                        "dGPU [#0]: NVIDIA GeForce RTX 4090: GIGABYTE AORUS", "Memory Timings|Тайминги памяти" },
                new[]
                {
                    new object[] { HudHwinfo.TypeTemp, 0u, "CPU (Tctl/Tdie)", "°C", 49.75, "ЦП (Tctl/Tdie)" },
                    new object[] { HudHwinfo.TypePower, 0u, "CPU Package Power", "W", 59.5, "Полная потребляемая мощность ЦП" },
                    new object[] { HudHwinfo.TypePower, 0u, "CPU PPT", "W", 58.1, "ЦП PPT" },
                    new object[] { HudHwinfo.TypeVolt, 0u, "CPU VDDCR_VDD Voltage (SVI3 TFN)", "V", 0.98, "ЦП VDDCR_VDD напряжение (SVI3 TFN)" },
                    new object[] { HudHwinfo.TypeTemp, 1u, "GPU Temperature", "°C", 44.0, "Температура ГП" },
                    new object[] { HudHwinfo.TypeTemp, 2u, "GPU Hot Spot Temperature", "°C", 44.5, "Горячая точка ГП" },
                    new object[] { HudHwinfo.TypeTemp, 2u, "GPU Memory Junction Temperature", "°C", 40.0, "Температура памяти ГП" },
                    new object[] { HudHwinfo.TypeTemp, 2u, "GPU Thermal Limit", "°C", 80.0, "Тепловой предел ГП" },
                    new object[] { HudHwinfo.TypeOther, 3u, "Tcas", "T", 32.0, "Tcas" },
                }, now);
            sensors.Clear(); readings.Clear();
            T.Check("hud hwinfo ru: buffer parses, display names stay localized", HudHwinfo.Parse(ru, sensors, readings, out poll)
                    && sensors[0].Name.StartsWith("ЦП") && sensors[0].NameOrig.StartsWith("CPU") && readings[2].Label == "ЦП PPT" && readings[2].LabelOrig == "CPU PPT");
            HudFrame rf = new HudFrame();
            HudHwinfo.Publish(sensors, readings, "NVIDIA GeForce RTX 4090", rf);
            T.Check("hud hwinfo ru: cpu power found by the English label", rf.Get("cpu.power") != null && rf.Get("cpu.power").Value == 59.5);
            T.Check("hud hwinfo ru: cpu voltage (SVI3 VDDCR_VDD)", rf.Get("cpu.voltage") != null && rf.Get("cpu.voltage").Value == 0.98);
            T.Check("hud hwinfo ru: «dGPU [#0]» is the main card for hot spot and memory",
                    rf.Get("gpu.hotspot") != null && rf.Get("gpu.hotspot").Value == 44.5 && rf.Get("gpu.memtemp") != null && rf.Get("gpu.memtemp").Value == 40.0);
            HudValue rhot = HudCollector.Merge(new List<HudSource> { new FakeSource(rf) }).Get("hot.max");
            T.Check("hud hwinfo ru: a translated «thermal limit» is not the hottest point", rhot != null && rhot.Value == 49.75,
                    rhot == null ? "null" : rhot.Value + " " + rhot.Note);

            byte[] stale = HwinfoBuffer(new[] { "CPU" }, new object[0][], now - 3600);
            T.Check("hud hwinfo: dead signature is rejected", !HudHwinfo.Parse(new byte[64], sensors, readings, out poll));
            sensors.Clear(); readings.Clear();
            T.Check("hud hwinfo: poll time of a closed HWiNFO is visible to the caller", HudHwinfo.Parse(stale, sensors, readings, out poll) && poll == now - 3600);

            byte[] live = HudBytes.Snapshot(HudHwinfo.MapName, HudHwinfo.MutexName, 16 * 1024 * 1024);
            if (live == null) T.Skip("hud hwinfo live: shared memory parses", "HWiNFO with shared memory is not running");
            else
            {
                sensors.Clear(); readings.Clear();
                T.Check("hud hwinfo live: shared memory parses", HudHwinfo.Parse(live, sensors, readings, out poll) && readings.Count > 0, readings.Count + " readings");
                // Живой путь источника на любом языке интерфейса HWiNFO: есть «CPU Package Power» у процессора — есть и cpu.power.
                bool hasPower = false;
                foreach (HudHwinfo.Reading r in readings)
                    if (r.Type == HudHwinfo.TypePower && r.SensorIndex < sensors.Count && sensors[(int)r.SensorIndex].NameOrig.StartsWith("CPU")
                        && (r.LabelOrig == "CPU Package Power" || r.LabelOrig == "CPU PPT")) hasPower = true;
                if (!hasPower) T.Skip("hud hwinfo live: CPU package power reaches cpu.power", "HWiNFO publishes no CPU package power");
                else
                {
                    HudFrame lf = new HudFrame();
                    new HudHwinfoSource().Collect(lf);
                    T.Check("hud hwinfo live: CPU package power reaches cpu.power", lf.Get("cpu.power") != null, sensors.Count > 0 ? sensors[0].Name : "");
                }
            }
        }

        private sealed class FakeSource : HudSource
        {
            public FakeSource(HudFrame f) { Last = f; }
            public override string Name { get { return "fake"; } }
            public override void Collect(HudFrame f) { }
        }
    }
}
