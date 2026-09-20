// SysDeck — модель оверлея показателей: каталог показателей со строковыми идентификаторами,
// настройка каждой строки (текст, график, свой интервал, цвет), кадр значений, история для графиков и
// превращение значения в текст. Без окон и без счётчиков — всё здесь проверяется тестами.
//
// Идентификаторы стабильны между запусками и попадают в настройки:
//  * встроенные — cpu.load, gpu.temp, ram, disk.read …;
//  * по ядрам — cpu.core.<номер логического процессора>.load / .mhz;
//  * HWiNFO — hw.<id датчика>.<экземпляр>.<id показания>, числа в шестнадцатеричном виде;
//  * MSI Afterburner — ab.<id источника>.<номер карты> (x — общий для всех карт).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SysDeck.Capture
{
    // Bytes — один объём без «из скольких» (память одного процесса); Memory — занято из всего.
    internal enum HudKind { Percent, Temp, Memory, Rate, Mhz, Watts, Volts, Rpm, Fps, Ms, Number, Text, Bytes }

    // Одно значение в кадре. NaN — источник значения не дал; Text — для строк-сведений.
    internal sealed class HudValue
    {
        public string Id;
        public HudKind Kind;
        public double Value = double.NaN;
        public double Total = double.NaN;     // для Memory — объём; для остальных не используется
        public string Text;                   // для Text; для прочих — готовая строка вместо числа (редко)
        public string Unit;                   // для Number — единица от источника
        public string Label;                  // подпись, если источник знает лучше каталога (имя датчика HWiNFO)
        public string Note;                   // пояснение: откуда число (для «самой горячей точки» — какой датчик)
        public bool NotPlace;                 // температура предела или запаса до него — «самая горячая точка» её пропускает
        public double[] Series;               // готовый ряд для графика (время каждого кадра) вместо истории по интервалу

        public HudValue() { }

        public HudValue(string id, HudKind kind, double value)
        {
            Id = id; Kind = kind; Value = value;
        }

        public HudValue Clone()
        {
            return (HudValue)MemberwiseClone();
        }
    }

    // Описание показателя: группа в дереве настроек, подписи, вид, интервал по умолчанию.
    internal sealed class HudDef
    {
        public string Id;
        public string Group;
        public string Title;      // длинное название для настроек
        public string Label;      // короткая подпись в столбике
        public HudKind Kind;
        public int IntervalMs;
        public string Source;     // откуда берётся — для подсказки в настройках

        public HudDef(string id, string group, string title, string label, HudKind kind, int intervalMs, string source)
        {
            Id = id; Group = group; Title = title; Label = label; Kind = kind; IntervalMs = intervalMs; Source = source;
        }
    }

    internal static class HudGroups
    {
        public const string Cpu = "cpu", Cores = "cores", Gpu = "gpu", Memory = "mem", Temps = "temps", Disk = "disk",
                            Net = "net", Fps = "fps", App = "app", System = "sys", Other = "other", Afterburner = "ab", HwinfoPrefix = "hw:";

        // Порядок блоков и в столбике, и в списке страницы «Оверлей»: процессор, видеокарта, кадры, память, остальное.
        public static readonly string[] Order = { Cpu, Cores, Gpu, Fps, Memory, App, Temps, Disk, Net, System, Other, Afterburner };

        public static int Rank(string group)
        {
            int i = Array.IndexOf(Order, group);
            if (i >= 0) return i * 2;
            // Группы HWiNFO (по датчикам) и неизвестное — перед Afterburner.
            return Array.IndexOf(Order, Afterburner) * 2 - 1;
        }

        public static string Title(string group)
        {
            switch (group)
            {
                case Cpu: return Tr.S("Процессор", "CPU");
                case Cores: return Tr.S("Ядра процессора", "CPU cores");
                case Gpu: return Tr.S("Видеокарта", "GPU");
                case Memory: return Tr.S("Память", "Memory");
                case Temps: return Tr.S("Температуры", "Temperatures");
                case Disk: return Tr.S("Диск", "Disk");
                case Net: return Tr.S("Сеть", "Network");
                case Fps: return Tr.S("Кадры (FPS)", "Frames (FPS)");
                case App: return Tr.S("Игра (активное окно)", "Game (active window)");
                case System: return Tr.S("Сведения о системе", "System information");
                case Afterburner: return "MSI Afterburner";
                case Other: return Tr.S("Прочее", "Other");
            }
            if (group != null && group.StartsWith(HwinfoPrefix, StringComparison.Ordinal))
                return "HWiNFO: " + group.Substring(HwinfoPrefix.Length);
            return group ?? "";
        }
    }

    internal static class HudCatalog
    {
        private static List<HudDef> _builtIn;

        public static List<HudDef> BuiltIn
        {
            get
            {
                if (_builtIn == null) _builtIn = Build();
                return _builtIn;
            }
        }

        private static List<HudDef> Build()
        {
            string pdh = "PDH", nvml = "NVIDIA NVML", hw = "HWiNFO", smbios = "SMBIOS", etw = "ETW";
            List<HudDef> d = new List<HudDef>();
            d.Add(new HudDef("fps", HudGroups.Fps, Tr.S("Кадров в секунду", "Frames per second"), "FPS", HudKind.Fps, 500, etw));
            d.Add(new HudDef("fps.frametime", HudGroups.Fps, Tr.S("Время кадра", "Frame time"), Tr.S("Кадр", "Frame"), HudKind.Ms, 250, etw));
            d.Add(new HudDef("fps.screen", HudGroups.Fps, Tr.S("Кадров ушло на вывод в секунду", "Frames going out to the display per second"), Tr.S("На экране", "On screen"), HudKind.Fps, 500, etw));
            d.Add(new HudDef("fps.screenms", HudGroups.Fps, Tr.S("Время между кадрами на выводе", "Time between frames going out"), Tr.S("Экран", "Screen"), HudKind.Ms, 250, etw));
            d.Add(new HudDef("fps.low1", HudGroups.Fps, Tr.S("1 % худших кадров", "1% low"), "1% low", HudKind.Fps, 1000, etw));
            d.Add(new HudDef("fps.low01", HudGroups.Fps, Tr.S("0,1 % худших кадров", "0.1% low"), "0.1% low", HudKind.Fps, 1000, etw));
            d.Add(new HudDef("fps.stutter", HudGroups.Fps, Tr.S("Фризы за минуту", "Stutters per minute"), Tr.S("Фризы", "Stutters"), HudKind.Number, 1000, etw));
            d.Add(new HudDef("fps.bottleneck", HudGroups.Fps, Tr.S("Что ограничивает кадры (узкое место)", "What limits the frame rate (bottleneck)"), Tr.S("Упор", "Limit"), HudKind.Text, 1000, Tr.S("расчёт по ЦП, ГП и FPS", "derived from CPU, GPU and FPS")));
            d.Add(new HudDef("fps.perwatt", HudGroups.Fps, Tr.S("Кадров на ватт видеокарты", "Frames per GPU watt"), "FPS/" + Tr.S("Вт", "W"), HudKind.Number, 1000, Tr.S("расчёт: FPS ÷ мощность ГП", "derived: FPS ÷ GPU power")));
            d.Add(new HudDef("fps.vsync", HudGroups.Fps, Tr.S("Вертикальная синхронизация и разрывы кадра", "V-Sync and tearing"), "V-Sync", HudKind.Text, 1000, etw));
            d.Add(new HudDef("fps.api", HudGroups.Fps, Tr.S("Как игра выводит кадры (API)", "How the game presents frames (API)"), "API", HudKind.Text, 2000, etw));
            d.Add(new HudDef("fps.presentmode", HudGroups.Fps, Tr.S("Режим вывода кадра (через DWM или напрямую)", "Present mode (composed or direct)"), Tr.S("Вывод", "Present"), HudKind.Text, 2000, etw));
            d.Add(new HudDef("display.hz", HudGroups.Fps, Tr.S("Частота обновления монитора с игрой", "Refresh rate of the game's monitor"), Tr.S("Монитор", "Display"), HudKind.Number, 5000, "Windows"));
            d.Add(new HudDef("fps.app", HudGroups.Fps, Tr.S("Игра, по которой считаются кадры", "Game the frames are counted for"), Tr.S("Игра", "Game"), HudKind.Text, 1000, etw));

            string win = "Windows";
            d.Add(new HudDef("app.name", HudGroups.App, Tr.S("Процесс активного окна", "Active window process"), Tr.S("Окно", "Window"), HudKind.Text, 1000, win));
            d.Add(new HudDef("app.ram", HudGroups.App, Tr.S("ОЗУ игры (рабочий набор)", "Game RAM (working set)"), Tr.S("ОЗУ игры", "Game RAM"), HudKind.Bytes, 1000, win));
            d.Add(new HudDef("app.private", HudGroups.App, Tr.S("Выделено игрой (private)", "Game committed (private)"), Tr.S("Выделено", "Private"), HudKind.Bytes, 2000, win));
            d.Add(new HudDef("app.vram", HudGroups.App, Tr.S("Видеопамять игры", "Game video memory"), Tr.S("Видео игры", "Game VRAM"), HudKind.Bytes, 1000, pdh));
            d.Add(new HudDef("app.vramshared", HudGroups.App, Tr.S("Общая видеопамять игры (из ОЗУ)", "Game shared GPU memory"), Tr.S("Общая видео", "Shared VRAM"), HudKind.Bytes, 2000, pdh));
            d.Add(new HudDef("app.cpu", HudGroups.App, Tr.S("Процессор игры, %", "Game CPU, %"), Tr.S("ЦП игры", "Game CPU"), HudKind.Percent, 1000, win));
            d.Add(new HudDef("app.gpu", HudGroups.App, Tr.S("Видеокарта игры, %", "Game GPU, %"), Tr.S("ГП игры", "Game GPU"), HudKind.Percent, 1000, pdh));
            d.Add(new HudDef("app.io.read", HudGroups.App, Tr.S("Игра: чтение (диск и прочий ввод-вывод)", "Game: read I/O"), Tr.S("Игра чт", "Game R"), HudKind.Rate, 1000, win));
            d.Add(new HudDef("app.io.write", HudGroups.App, Tr.S("Игра: запись (диск и прочий ввод-вывод)", "Game: write I/O"), Tr.S("Игра зп", "Game W"), HudKind.Rate, 1000, win));
            d.Add(new HudDef("app.threads", HudGroups.App, Tr.S("Потоки игры", "Game threads"), Tr.S("Потоки", "Threads"), HudKind.Number, 2000, win));
            d.Add(new HudDef("app.handles", HudGroups.App, Tr.S("Дескрипторы игры", "Game handles"), Tr.S("Дескр.", "Handles"), HudKind.Number, 5000, win));
            d.Add(new HudDef("app.uptime", HudGroups.App, Tr.S("Игра запущена", "Game running for"), Tr.S("В игре", "Playing"), HudKind.Text, 1000, win));

            d.Add(new HudDef("cpu.load", HudGroups.Cpu, Tr.S("Загрузка процессора", "CPU load"), Tr.S("ЦП", "CPU"), HudKind.Percent, 1000, pdh));
            d.Add(new HudDef("cpu.coremax", HudGroups.Cpu, Tr.S("Самое загруженное ядро", "Busiest core"), Tr.S("Ядро макс", "Top core"), HudKind.Percent, 1000, pdh));
            d.Add(new HudDef("cpu.perflimit", HudGroups.Cpu, Tr.S("Предел производительности процессора (100 % — частота не сброшена)", "CPU performance limit (100% — not throttled)"), Tr.S("Предел ЦП", "CPU limit"), HudKind.Percent, 1000, pdh));
            d.Add(new HudDef("cpu.temp", HudGroups.Cpu, Tr.S("Температура процессора", "CPU temperature"), Tr.S("ЦП °", "CPU °"), HudKind.Temp, 1000, hw + " / Afterburner / ACPI"));
            d.Add(new HudDef("cpu.mhz", HudGroups.Cpu, Tr.S("Частота процессора (средняя по ядрам)", "CPU frequency (average of cores)"), Tr.S("ЦП МГц", "CPU MHz"), HudKind.Mhz, 1000, pdh));
            d.Add(new HudDef("cpu.power", HudGroups.Cpu, Tr.S("Мощность процессора", "CPU power"), Tr.S("ЦП Вт", "CPU W"), HudKind.Watts, 1000, hw));
            d.Add(new HudDef("cpu.voltage", HudGroups.Cpu, Tr.S("Напряжение ядра процессора", "CPU core voltage"), Tr.S("ЦП В", "CPU V"), HudKind.Volts, 1000, hw));

            d.Add(new HudDef("gpu.load", HudGroups.Gpu, Tr.S("Загрузка видеокарты", "GPU load"), Tr.S("ГП", "GPU"), HudKind.Percent, 1000, pdh));
            d.Add(new HudDef("gpu.temp", HudGroups.Gpu, Tr.S("Температура видеокарты", "GPU temperature"), Tr.S("ГП °", "GPU °"), HudKind.Temp, 1000, nvml));
            d.Add(new HudDef("gpu.hotspot", HudGroups.Gpu, Tr.S("Горячая точка видеокарты (hot spot)", "GPU hot spot"), Tr.S("ГП hot", "GPU hot"), HudKind.Temp, 1000, hw));
            d.Add(new HudDef("gpu.memtemp", HudGroups.Gpu, Tr.S("Температура видеопамяти", "GPU memory temperature"), Tr.S("Видео °", "VRAM °"), HudKind.Temp, 1000, hw));
            d.Add(new HudDef("gpu.power", HudGroups.Gpu, Tr.S("Мощность видеокарты", "GPU power"), Tr.S("ГП Вт", "GPU W"), HudKind.Watts, 1000, nvml));
            d.Add(new HudDef("gpu.powerlimit", HudGroups.Gpu, Tr.S("Предел мощности видеокарты", "GPU power limit"), Tr.S("Предел Вт", "Limit W"), HudKind.Watts, 5000, nvml));
            d.Add(new HudDef("gpu.fan", HudGroups.Gpu, Tr.S("Вентилятор видеокарты, %", "GPU fan, %"), Tr.S("Вент.", "Fan"), HudKind.Percent, 1000, nvml));
            d.Add(new HudDef("gpu.fanrpm", HudGroups.Gpu, Tr.S("Вентилятор видеокарты, об/мин", "GPU fan, RPM"), Tr.S("Вент.", "Fan"), HudKind.Rpm, 1000, "Afterburner / " + hw));
            d.Add(new HudDef("gpu.clock", HudGroups.Gpu, Tr.S("Частота ядра видеокарты", "GPU core clock"), Tr.S("ГП МГц", "GPU MHz"), HudKind.Mhz, 1000, nvml));
            d.Add(new HudDef("gpu.memclock", HudGroups.Gpu, Tr.S("Частота видеопамяти", "GPU memory clock"), Tr.S("Видео МГц", "VRAM MHz"), HudKind.Mhz, 1000, nvml));
            d.Add(new HudDef("gpu.voltage", HudGroups.Gpu, Tr.S("Напряжение ядра видеокарты", "GPU core voltage"), Tr.S("ГП В", "GPU V"), HudKind.Volts, 1000, "Afterburner / " + hw));
            d.Add(new HudDef("gpu.memload", HudGroups.Gpu, Tr.S("Загрузка шины видеопамяти", "GPU memory controller load"), Tr.S("Шина пам.", "Mem ctrl"), HudKind.Percent, 1000, nvml));
            d.Add(new HudDef("gpu.enc", HudGroups.Gpu, Tr.S("Кодировщик видео", "Video encoder"), "NVENC", HudKind.Percent, 1000, nvml));
            d.Add(new HudDef("gpu.dec", HudGroups.Gpu, Tr.S("Декодер видео", "Video decoder"), "NVDEC", HudKind.Percent, 1000, nvml));
            d.Add(new HudDef("gpu.pstate", HudGroups.Gpu, Tr.S("Режим питания видеокарты (P-state)", "GPU performance state"), "P-state", HudKind.Text, 1000, nvml));
            d.Add(new HudDef("gpu.throttle", HudGroups.Gpu, Tr.S("Причина сброса частоты видеокарты", "GPU throttle reason"), Tr.S("Троттлинг", "Throttle"), HudKind.Text, 1000, nvml));
            d.Add(new HudDef("gpu.pcie", HudGroups.Gpu, Tr.S("Шина PCIe видеокарты (сейчас)", "GPU PCIe link (now)"), "PCIe", HudKind.Text, 5000, nvml));

            d.Add(new HudDef("ram", HudGroups.Memory, Tr.S("Оперативная память: занято / всего", "RAM: used / total"), Tr.S("ОЗУ", "RAM"), HudKind.Memory, 2000, "Windows"));
            d.Add(new HudDef("ram.load", HudGroups.Memory, Tr.S("Оперативная память, %", "RAM, %"), Tr.S("ОЗУ", "RAM"), HudKind.Percent, 2000, "Windows"));
            d.Add(new HudDef("ram.hardfaults", HudGroups.Memory, Tr.S("Подкачка с диска (жёсткие ошибки страниц)", "Paging from disk (hard page faults)"), Tr.S("Подкачка", "Paging"), HudKind.Number, 1000, pdh));
            d.Add(new HudDef("commit", HudGroups.Memory, Tr.S("Выделенная память (commit)", "Committed memory"), "Commit", HudKind.Memory, 2000, "Windows"));
            d.Add(new HudDef("vram", HudGroups.Memory, Tr.S("Видеопамять: занято / всего", "Video memory: used / total"), Tr.S("Видео", "VRAM"), HudKind.Memory, 2000, pdh));
            d.Add(new HudDef("mem.timings", HudGroups.Memory, Tr.S("Частота и тайминги памяти", "Memory speed and timings"), Tr.S("Тайминги", "Timings"), HudKind.Text, 10000, hw));

            d.Add(new HudDef("hot.max", HudGroups.Temps, Tr.S("Самая горячая точка (любой датчик)", "Hottest sensor (any)"), Tr.S("Макс °", "Max °"), HudKind.Temp, 1000, hw + " / Afterburner / NVML"));

            d.Add(new HudDef("disk.read", HudGroups.Disk, Tr.S("Диск: чтение", "Disk: read"), Tr.S("Диск чт", "Disk R"), HudKind.Rate, 1000, pdh));
            d.Add(new HudDef("disk.write", HudGroups.Disk, Tr.S("Диск: запись", "Disk: write"), Tr.S("Диск зп", "Disk W"), HudKind.Rate, 1000, pdh));
            d.Add(new HudDef("net.down", HudGroups.Net, Tr.S("Сеть: приём", "Network: download"), Tr.S("Сеть ↓", "Net ↓"), HudKind.Rate, 1000, pdh));
            d.Add(new HudDef("net.up", HudGroups.Net, Tr.S("Сеть: отдача", "Network: upload"), Tr.S("Сеть ↑", "Net ↑"), HudKind.Rate, 1000, pdh));

            d.Add(new HudDef("sys.cpu", HudGroups.System, Tr.S("Модель процессора", "CPU model"), Tr.S("Процессор", "CPU"), HudKind.Text, 60000, smbios));
            d.Add(new HudDef("sys.board", HudGroups.System, Tr.S("Материнская плата и BIOS", "Motherboard and BIOS"), Tr.S("Плата", "Board"), HudKind.Text, 60000, smbios));
            d.Add(new HudDef("sys.mem", HudGroups.System, Tr.S("Модули памяти", "Memory modules"), Tr.S("Модули", "Modules"), HudKind.Text, 60000, smbios));
            d.Add(new HudDef("sys.gpu", HudGroups.System, Tr.S("Видеокарта и драйвер", "GPU and driver"), Tr.S("Видеокарта", "GPU"), HudKind.Text, 60000, "DXGI / " + nvml));
            d.Add(new HudDef("sys.uptime", HudGroups.System, Tr.S("Время работы Windows", "Windows uptime"), Tr.S("Работает", "Uptime"), HudKind.Text, 1000, "Windows"));

            d.Add(new HudDef("clock", HudGroups.Other, Tr.S("Часы", "Clock"), Tr.S("Время", "Time"), HudKind.Text, 1000, "Windows"));
            return d;
        }

        public static HudDef Find(string id)
        {
            foreach (HudDef d in BuiltIn) if (d.Id == id) return d;
            return Dynamic(id, null);
        }

        // Показатели, которых нет в каталоге заранее: по ядрам, датчики HWiNFO и Afterburner. value — последнее значение,
        // если есть (из него берутся подпись и вид).
        public static HudDef Dynamic(string id, HudValue value)
        {
            if (string.IsNullOrEmpty(id)) return null;
            int core; string metric;
            if (TryCore(id, out core, out metric))
            {
                string n = (core + 1).ToString(CultureInfo.InvariantCulture);
                return metric == "mhz"
                    ? new HudDef(id, HudGroups.Cores, Tr.S("Ядро ", "Core ") + n + Tr.S(": частота", ": frequency"), Tr.S("Я", "C") + n + Tr.S(" МГц", " MHz"), HudKind.Mhz, 1000, "PDH")
                    : new HudDef(id, HudGroups.Cores, Tr.S("Ядро ", "Core ") + n + Tr.S(": загрузка", ": load"), Tr.S("Я", "C") + n, HudKind.Percent, 1000, "PDH");
            }
            if (id.StartsWith("hw.", StringComparison.Ordinal) || id.StartsWith("ab.", StringComparison.Ordinal))
            {
                bool hw = id[0] == 'h';
                string label = value != null && !string.IsNullOrEmpty(value.Label) ? value.Label : id;
                string group = hw ? HudGroups.HwinfoPrefix + (value != null && !string.IsNullOrEmpty(value.Note) ? value.Note : "?") : HudGroups.Afterburner;
                return new HudDef(id, group, label, label, value != null ? value.Kind : HudKind.Number, 1000, hw ? "HWiNFO" : "MSI Afterburner");
            }
            return null;
        }

        public static string CoreId(int logical, string metric)
        {
            return "cpu.core." + logical.ToString(CultureInfo.InvariantCulture) + "." + metric;
        }

        public static bool TryCore(string id, out int logical, out string metric)
        {
            logical = -1; metric = null;
            if (id == null || !id.StartsWith("cpu.core.", StringComparison.Ordinal)) return false;
            string[] parts = id.Split('.');
            if (parts.Length != 4 || (parts[3] != "load" && parts[3] != "mhz")) return false;
            if (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out logical) || logical < 0 || logical > 4096) return false;
            metric = parts[3];
            return true;
        }

        // Старый набор («Cpu,CpuTemp,Ram») → строки нового вида. Диск и сеть раньше были одной строкой на два числа.
        public static List<string> FromLegacy(string csv)
        {
            List<string> ids = new List<string>();
            foreach (string raw in (csv ?? "").Split(','))
            {
                string p = raw.Trim().ToLowerInvariant();
                string[] map;
                switch (p)
                {
                    case "cpu": map = new[] { "cpu.load" }; break;
                    case "cputemp": map = new[] { "cpu.temp" }; break;
                    case "ram": map = new[] { "ram" }; break;
                    case "gpu": map = new[] { "gpu.load" }; break;
                    case "gputemp": map = new[] { "gpu.temp" }; break;
                    case "vram": map = new[] { "vram" }; break;
                    case "gpupower": map = new[] { "gpu.power" }; break;
                    case "disk": map = new[] { "disk.read", "disk.write" }; break;
                    case "net": map = new[] { "net.down", "net.up" }; break;
                    case "clock": map = new[] { "clock" }; break;
                    default: map = new string[0]; break;
                }
                foreach (string id in map) if (!ids.Contains(id)) ids.Add(id);
            }
            return ids;
        }
    }

    // Настройка одной строки оверлея.
    internal sealed class HudItem
    {
        public const int TextFlag = 1, GraphFlag = 2, StatsFlag = 4, NoAlarmFlag = 8;
        public const int MinInterval = 250, MaxInterval = 60000;

        public string Id;
        public bool Text = true;
        public bool Graph;
        public int IntervalMs;          // 0 — по умолчанию из каталога
        public int Color;               // ARGB; 0 — цвет по уровню (обычный / высокий / критический)
        public bool Stats;              // мин. / сред. / макс. за окно рядом со значением
        public bool NoAlarm;            // не подсвечивать значения за порогами
        public double Warn = double.NaN, Crit = double.NaN;   // свои пороги; NaN — по умолчанию (HudAlarm)
        public int LabelColor;          // ARGB подписи; 0 — цвет группы или серый (по виду столбика)

        public HudItem() { }
        public HudItem(string id) { Id = id; }

        public int EffectiveInterval(HudDef def)
        {
            int ms = IntervalMs > 0 ? IntervalMs : def != null ? def.IntervalMs : 1000;
            return Math.Max(MinInterval, Math.Min(MaxInterval, ms));
        }

        public HudItem Clone() { return (HudItem)MemberwiseClone(); }

        public static bool ValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (!(char.IsLetterOrDigit(c) && c < 128) && c != '.' && c != '_' && c != '-') return false;
            return true;
        }

        // «cpu.load:3:1000:FF40C0FF;ram:1:0:0» — порядок строк = порядок в столбике. Неизвестный мусор пропускается,
        // повтор идентификатора — тоже; пустая строка — пустой набор (все галочки сняты — это выбор человека).
        public static List<HudItem> ParseList(string text)
        {
            List<HudItem> list = new List<HudItem>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string chunk in (text ?? "").Split(';'))
            {
                string[] f = chunk.Trim().Split(':');
                if (f.Length == 0 || !ValidId(f[0]) || seen.Contains(f[0])) continue;
                HudItem it = new HudItem(f[0]);
                int flags;
                if (f.Length > 1 && int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out flags))
                {
                    it.Text = (flags & TextFlag) != 0;
                    it.Graph = (flags & GraphFlag) != 0;
                    it.Stats = (flags & StatsFlag) != 0;
                    it.NoAlarm = (flags & NoAlarmFlag) != 0;
                    if (!it.Text && !it.Graph) it.Text = true;
                }
                int ms;
                if (f.Length > 2 && int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out ms))
                    it.IntervalMs = ms == 0 ? 0 : Math.Max(MinInterval, Math.Min(MaxInterval, ms));
                uint argb;
                if (f.Length > 3 && uint.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb))
                    it.Color = unchecked((int)argb);
                if (f.Length > 4) it.Warn = Threshold(f[4]);
                if (f.Length > 5) it.Crit = Threshold(f[5]);
                if (f.Length > 6 && uint.TryParse(f[6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb))
                    it.LabelColor = unchecked((int)argb);
                seen.Add(it.Id);
                list.Add(it);
            }
            return list;
        }

        public static string FormatList(IEnumerable<HudItem> items)
        {
            StringBuilder sb = new StringBuilder();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (HudItem it in items)
            {
                if (it == null || !ValidId(it.Id) || !seen.Add(it.Id)) continue;
                if (sb.Length > 0) sb.Append(';');
                int flags = (it.Text || !it.Graph ? TextFlag : 0) | (it.Graph ? GraphFlag : 0) | (it.Stats ? StatsFlag : 0) | (it.NoAlarm ? NoAlarmFlag : 0);
                sb.Append(it.Id).Append(':').Append(flags.ToString(CultureInfo.InvariantCulture)).Append(':')
                  .Append(it.IntervalMs.ToString(CultureInfo.InvariantCulture)).Append(':')
                  .Append(unchecked((uint)it.Color).ToString("X", CultureInfo.InvariantCulture));
                // Пороги и цвет подписи — только когда заданы: прежние настройки остаются байт в байт.
                if (HudFormat.Valid(it.Warn) || HudFormat.Valid(it.Crit) || it.LabelColor != 0)
                    sb.Append(':').Append(ThresholdText(it.Warn)).Append(':').Append(ThresholdText(it.Crit)).Append(':')
                      .Append(unchecked((uint)it.LabelColor).ToString("X", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static double Threshold(string text)
        {
            double v;
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && HudFormat.Valid(v) ? v : double.NaN;
        }

        private static string ThresholdText(double v)
        {
            return HudFormat.Valid(v) ? v.ToString("0.###", CultureInfo.InvariantCulture) : "";
        }

        // Строки блоками: процессор, видеокарта, кадры, память, остальное (HudGroups.Order). Порядок, в котором строки
        // отмечали, и кнопки «Выше» / «Ниже» действуют внутри блока. Сортировка устойчивая.
        public static List<HudItem> Grouped(IEnumerable<HudItem> items)
        {
            List<HudItem> list = new List<HudItem>();
            List<int> ranks = new List<int>();
            foreach (HudItem it in items)
            {
                if (it == null) continue;
                int rank = BlockRank(it.Id);
                int at = list.Count;
                while (at > 0 && ranks[at - 1] > rank) at--;
                list.Insert(at, it);
                ranks.Insert(at, rank);
            }
            return list;
        }

        public static int BlockRank(string id)
        {
            HudDef def = HudCatalog.Find(id);
            return HudGroups.Rank(def == null ? null : def.Group);
        }

        // Основной набор: процессор (загрузка, температура, частота, мощность, напряжение), видеокарта с датчиками,
        // кадры с графиком времени кадра, память. Кнопка «Основной набор» на странице «Оверлей» возвращает этот.
        public const string Default = "cpu.load:1:0:0;cpu.temp:1:0:0;cpu.mhz:1:0:0;cpu.power:1:0:0;cpu.voltage:1:0:0;"
                                    + "gpu.load:1:0:0;gpu.temp:1:0:0;gpu.power:1:0:0;gpu.voltage:1:0:0;gpu.clock:1:0:0;gpu.memtemp:1:0:0;"
                                    + "gpu.hotspot:1:0:0;gpu.fanrpm:1:0:0;gpu.memclock:1:0:0;"
                                    + "fps:1:0:0;fps.frametime:3:250:0;fps.low1:1:0:0;fps.app:1:0:0;fps.low01:1:0:0;"
                                    + "ram:1:0:0;vram:1:0:0;ram.load:1:0:0";
    }

    // Кадр: последнее значение каждого показателя от всех источников.
    internal sealed class HudFrame
    {
        public readonly Dictionary<string, HudValue> Values = new Dictionary<string, HudValue>(StringComparer.Ordinal);
        public DateTime At = DateTime.Now;

        public void Put(HudValue v)
        {
            if (v != null && v.Id != null) Values[v.Id] = v;
        }

        public void Put(string id, HudKind kind, double value)
        {
            Put(new HudValue(id, kind, value));
        }

        public void PutText(string id, string text)
        {
            HudValue v = new HudValue(id, HudKind.Text, double.NaN);
            v.Text = text;
            Put(v);
        }

        public HudValue Get(string id)
        {
            HudValue v;
            return id != null && Values.TryGetValue(id, out v) ? v : null;
        }

        public bool Has(string id)
        {
            HudValue v = Get(id);
            return v != null && (v.Kind == HudKind.Text ? !string.IsNullOrEmpty(v.Text) : HudFormat.Valid(v.Value));
        }

        public void Remove(string id) { Values.Remove(id); }

        public HudFrame Clone()
        {
            HudFrame f = new HudFrame();
            f.At = At;
            foreach (KeyValuePair<string, HudValue> kv in Values) f.Values[kv.Key] = kv.Value;
            return f;
        }
    }

    // История одного показателя — кольцо из времени и значения. Размер с запасом на минуту при 4 Гц.
    internal sealed class HudRing
    {
        private readonly double[] _v;
        private readonly long[] _t;
        private int _head, _count;

        public HudRing(int capacity)
        {
            _v = new double[Math.Max(2, capacity)];
            _t = new long[_v.Length];
        }

        public int Count { get { return _count; } }
        public int Capacity { get { return _v.Length; } }

        public void Push(long ticks, double value)
        {
            _v[_head] = value;
            _t[_head] = ticks;
            _head = (_head + 1) % _v.Length;
            if (_count < _v.Length) _count++;
        }

        // i = 0 — самое старое.
        public double Value(int i) { return _v[Index(i)]; }
        public long Ticks(int i) { return _t[Index(i)]; }

        private int Index(int i) { return ((_head - _count + i) % _v.Length + _v.Length) % _v.Length; }

        public void Clear() { _head = 0; _count = 0; }
    }

    internal sealed class HudHistory
    {
        private readonly Dictionary<string, HudRing> _rings = new Dictionary<string, HudRing>(StringComparer.Ordinal);
        private readonly int _capacity;

        public HudHistory(int capacity) { _capacity = capacity; }

        public void Push(string id, DateTime at, double value)
        {
            HudRing r;
            if (!_rings.TryGetValue(id, out r)) { r = new HudRing(_capacity); _rings[id] = r; }
            r.Push(at.Ticks, value);
        }

        public HudRing Get(string id)
        {
            HudRing r;
            return _rings.TryGetValue(id, out r) ? r : null;
        }

        public void Retain(ICollection<string> ids)
        {
            List<string> drop = new List<string>();
            foreach (string k in _rings.Keys) if (!ids.Contains(k)) drop.Add(k);
            foreach (string k in drop) _rings.Remove(k);
        }
    }

    internal struct HudRow
    {
        public string Id;
        public string Label;
        public string Value;
        public int Level;              // 0 — обычное, 1 — высокое, 2 — критическое
        public bool Graph, TextShown;
        public int Color;
        public HudKind Kind;
        public double Min, Max;        // шкала графика; Max <= Min — по данным
        public double[] Points;        // история для графика, от старого к новому; NaN — пропуск
        public int Slots;              // сколько точек помещается в окно графика
        public string Stats;           // «↓98 ⌀131 ↑165» или null
        public int LabelColor;         // ARGB подписи; 0 — по виду столбика
        public string Group;           // группа каталога — для цвета подписи
        public double Target;          // линия цели на графике (время кадра под частоту монитора); 0 — нет

        public HudRow(string label, string value, int level)
        {
            Id = null; Label = label; Value = value; Level = level; Graph = false; TextShown = true; Color = 0;
            Kind = HudKind.Text; Min = 0; Max = 0; Points = null; Slots = 0; Stats = null; LabelColor = 0; Group = null; Target = 0;
        }
    }

    internal static class HudFormat
    {
        public const string NoData = "—";

        public static bool Valid(double v) { return !double.IsNaN(v) && !double.IsInfinity(v); }

        private static string F(double v, string format) { return v.ToString(format, CultureInfo.InvariantCulture); }

        public static HudRow Row(HudItem item, HudValue v)
        {
            HudDef def = HudCatalog.Find(item.Id) ?? HudCatalog.Dynamic(item.Id, v);
            HudKind kind = v != null ? v.Kind : def != null ? def.Kind : HudKind.Number;
            string label = v != null && !string.IsNullOrEmpty(v.Label) && (def == null || def.Id.StartsWith("hw.") || def.Id.StartsWith("ab."))
                ? v.Label : def != null ? def.Label : item.Id;
            HudRow row = new HudRow(label, NoData, 0);
            row.Id = item.Id;
            row.Kind = kind;
            row.Graph = item.Graph && kind != HudKind.Text;
            row.TextShown = item.Text || !row.Graph;
            // Полоса графика под строкой без подписи непонятна — подпись говорит, что это график.
            if (row.Graph) row.Label = label + Tr.S(" (график)", " (graph)");
            row.Color = item.Color;
            row.LabelColor = item.LabelColor;
            row.Group = def != null ? def.Group : null;
            Scale(kind, v, out row.Min, out row.Max);
            if (v == null) return row;
            int level;
            row.Value = Text(v, out level);
            row.Level = HudAlarm.Level(item, kind, v);
            return row;
        }

        public static void Scale(HudKind kind, HudValue v, out double min, out double max)
        {
            min = 0; max = 0;
            switch (kind)
            {
                case HudKind.Percent: max = 100; break;
                case HudKind.Temp: max = 100; break;
                case HudKind.Memory: max = v != null && Valid(v.Total) && v.Total > 0 ? v.Total : 0; break;
            }
        }

        public static string Text(HudValue v, out int level)
        {
            level = 0;
            if (v == null) return NoData;
            if (v.Kind == HudKind.Text) return string.IsNullOrEmpty(v.Text) ? NoData : v.Text;
            if (!string.IsNullOrEmpty(v.Text)) return v.Text;
            double x = v.Value;
            if (!Valid(x)) return NoData;
            switch (v.Kind)
            {
                case HudKind.Percent:
                {
                    double p = Math.Max(0, Math.Min(100, x));
                    level = p >= 90 ? 2 : p >= 70 ? 1 : 0;
                    return F(Math.Round(p), "0") + "%";
                }
                case HudKind.Temp:
                    level = x >= 85 ? 2 : x >= 75 ? 1 : 0;
                    return F(Math.Round(x), "0") + "°C";
                case HudKind.Memory:
                {
                    if (x < 0 || !Valid(v.Total) || v.Total <= 0) return NoData;
                    double ratio = x / v.Total;
                    level = ratio >= 0.9 ? 2 : ratio >= 0.8 ? 1 : 0;
                    return F(x / 1073741824.0, "0.0") + " / " + F(v.Total / 1073741824.0, "0.0") + Tr.S(" ГБ", " GB");
                }
                case HudKind.Rate: return Rate(x);
                case HudKind.Bytes:
                    if (x < 0) return NoData;
                    return x >= 1073741824.0 ? F(x / 1073741824.0, "0.00") + Tr.S(" ГБ", " GB") : F(Math.Round(x / 1048576.0), "0") + Tr.S(" МБ", " MB");
                case HudKind.Mhz: return F(Math.Round(x), "0") + Tr.S(" МГц", " MHz");
                case HudKind.Watts: return F(x, x < 10 ? "0.0" : "0") + Tr.S(" Вт", " W");
                case HudKind.Volts: return F(x, "0.000") + Tr.S(" В", " V");
                case HudKind.Rpm: return F(Math.Round(x), "0") + Tr.S(" об/мин", " RPM");
                case HudKind.Fps: return F(Math.Round(x), "0");
                case HudKind.Ms: return F(x, "0.0") + Tr.S(" мс", " ms");
                default:
                {
                    string unit = string.IsNullOrEmpty(v.Unit) ? "" : " " + v.Unit;
                    double a = Math.Abs(x);
                    string num = a >= 100 || a == Math.Floor(a) ? F(Math.Round(x), "0") : a >= 10 ? F(x, "0.0") : F(x, "0.00");
                    return num + unit;
                }
            }
        }

        // Байты в секунду: до мегабайта — килобайты целыми, дальше — с одним знаком.
        public static string Rate(double bytesPerSecond)
        {
            double v = Math.Max(0, bytesPerSecond);
            if (v >= 1073741824.0) return F(v / 1073741824.0, "0.0") + Tr.S(" ГБ/с", " GB/s");
            if (v >= 1048576.0) return F(v / 1048576.0, "0.0") + Tr.S(" МБ/с", " MB/s");
            return F(Math.Round(v / 1024.0), "0") + Tr.S(" КБ/с", " KB/s");
        }

        // «Thermal Zone Information\Temperature» отдаёт кельвины. Всё вне 1..150 °C — сломанный датчик, а не жара.
        public static double KelvinToCelsius(double kelvin)
        {
            double c = kelvin - 273.15;
            return c >= 1 && c <= 150 ? c : double.NaN;
        }

        // Короткое число для мин. / сред. / макс.: единица только там, где без неё непонятно.
        public static string Short(HudKind kind, double x)
        {
            if (!Valid(x)) return NoData;
            switch (kind)
            {
                case HudKind.Percent: return F(Math.Round(x), "0") + "%";
                case HudKind.Temp: return F(Math.Round(x), "0") + "°";
                case HudKind.Memory:
                case HudKind.Bytes: return x >= 1073741824.0 ? F(x / 1073741824.0, "0.0") : F(Math.Round(x / 1048576.0), "0") + Tr.S("М", "M");
                case HudKind.Rate: return Rate(x);
                case HudKind.Volts: return F(x, "0.000");
                case HudKind.Ms: return F(x, "0.0");
                case HudKind.Mhz: case HudKind.Watts: case HudKind.Rpm: case HudKind.Fps: return F(Math.Round(x), "0");
                default:
                {
                    double a = Math.Abs(x);
                    return a >= 100 || a == Math.Floor(a) ? F(Math.Round(x), "0") : a >= 10 ? F(x, "0.0") : F(x, "0.00");
                }
            }
        }

        public static string StatsText(HudKind kind, double min, double avg, double max)
        {
            if (!Valid(min) || !Valid(max) || !Valid(avg)) return null;
            return "↓" + Short(kind, min) + " ⌀" + Short(kind, avg) + " ↑" + Short(kind, max);
        }

        public static string Uptime(TimeSpan t)
        {
            int days = (int)t.TotalDays;
            string hms = t.Hours.ToString("00", CultureInfo.InvariantCulture) + ":" + t.Minutes.ToString("00", CultureInfo.InvariantCulture)
                       + ":" + t.Seconds.ToString("00", CultureInfo.InvariantCulture);
            return days > 0 ? days.ToString(CultureInfo.InvariantCulture) + Tr.S(" д ", " d ") + hms : hms;
        }
    }
}
