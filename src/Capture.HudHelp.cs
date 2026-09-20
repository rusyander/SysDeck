// SysDeck — пояснения к показателям оверлея для страницы «Оверлей»: что это, зачем смотреть и когда
// число «срабатывает» (краснеет, растёт, говорит о проблеме). Показываются текстом прямо под настройками строки,
// а не всплывающей подсказкой.
using System;

namespace SysDeck.Capture
{
    internal static class HudHelp
    {
        public static string ForGroup(string group)
        {
            switch (group)
            {
                case HudGroups.Fps: return Tr.S(
                    "Кадры считаются по событиям вывода кадра (ETW) того окна, что сейчас активно; у браузеров — по их GPU-процессу. Нужны права: одно окно UAC кнопкой «Разрешить подсчёт кадров» или оверлей с правами администратора. В эксклюзивном полноэкранном режиме кадры считаются, но сам столбик поверх игры не виден.",
                    "Frames are counted from present events (ETW) of the active window; for browsers, from their GPU process. Needs rights: one UAC prompt via “Allow frame counting” or the overlay running as administrator. In exclusive fullscreen frames are still counted, but the column itself is not visible over the game.");
                case HudGroups.App: return Tr.S(
                    "Всё о процессе активного окна вместе с его дочерними процессами (у браузеров и лаунчеров память и кадры живут в дочерних). Переключились в другое окно — числа переключаются на него. Прав администратора не нужно.",
                    "Everything about the active window's process together with its child processes (browsers and launchers keep memory and frames in children). Switch to another window and the numbers follow it. No administrator rights needed.");
                case HudGroups.Cpu: return Tr.S(
                    "Процессор целиком. Температура, мощность и напряжение берутся у HWiNFO или MSI Afterburner, если они запущены; без них Windows даёт только загрузку и частоту.",
                    "The whole CPU. Temperature, power and voltage come from HWiNFO or MSI Afterburner when they run; without them Windows only provides load and frequency.");
                case HudGroups.Cores: return Tr.S(
                    "Загрузка и частота каждого логического процессора. Полезно, когда общая загрузка низкая, а игра упирается в одно-два ядра: одно ядро под 100 % при остальных свободных — это и есть «узкое место процессора». Кнопка «Отметить» берёт первые N ядер.",
                    "Load and frequency of every logical processor. Useful when total load is low but the game is bound by one or two cores: one core at 100% with the rest idle is the CPU bottleneck. “Apply” ticks the first N cores.");
                case HudGroups.Gpu: return Tr.S(
                    "Видеокарта: загрузка — из Windows, остальное — из драйвера NVIDIA (NVML), HWiNFO или Afterburner. Загрузка ниже ~95 % в игре без ограничения кадров обычно значит, что упираетесь не в видеокарту.",
                    "GPU: load comes from Windows, the rest from the NVIDIA driver (NVML), HWiNFO or Afterburner. Load below ~95% in a game without a frame cap usually means the GPU is not the bottleneck.");
                case HudGroups.Memory: return Tr.S(
                    "Память всей системы. Для памяти конкретной игры — группа «Игра».",
                    "System-wide memory. For a particular game's memory see the “Game” group.");
                case HudGroups.Temps: return Tr.S(
                    "Самая горячая точка среди всех настоящих датчиков температуры — чтобы не держать в столбике десяток строк.",
                    "The hottest of all real temperature sensors, so the column does not need a dozen rows.");
                case HudGroups.Disk: return Tr.S("Скорость чтения и записи всех физических дисков вместе.", "Read and write speed of all physical disks together.");
                case HudGroups.Net: return Tr.S("Скорость приёма и отдачи всех сетевых адаптеров вместе.", "Download and upload speed of all network adapters together.");
                case HudGroups.System: return Tr.S("Неизменные сведения о железе: читаются из SMBIOS и драйвера раз в минуту.", "Static hardware facts: read from SMBIOS and the driver once a minute.");
                case HudGroups.Afterburner: return Tr.S(
                    "Все показатели, которые публикует запущенный MSI Afterburner (включая данные RTSS). Появляются, пока Afterburner работает.",
                    "Every value published by a running MSI Afterburner (including RTSS data). They appear while Afterburner runs.");
            }
            if (group != null && group.StartsWith(HudGroups.HwinfoPrefix, StringComparison.Ordinal))
                return Tr.S("Датчики этого устройства из HWiNFO. Появляются, пока HWiNFO работает с включённой общей памятью.",
                            "This device's sensors from HWiNFO. They appear while HWiNFO runs with shared memory enabled.");
            return Tr.S("Галочка у группы отмечает или снимает все её строки.", "The group tick checks or unchecks all its rows.");
        }

        public static string ForId(string id)
        {
            switch (id)
            {
                case "fps": return Tr.S(
                    "Сколько кадров игра вывела за последнюю секунду. Сама по себе цифра мало говорит о плавности — смотрите вместе с «1 % худших» и графиком времени кадра.",
                    "Frames the game presented in the last second. On its own it says little about smoothness — read it together with “1% low” and the frame time graph.");
                case "fps.frametime": return Tr.S(
                    "Сколько миллисекунд занял кадр: 16,7 мс = 60 FPS, 6,9 мс = 144 FPS. На графике рисуется каждый кадр. Ровная линия — плавно; «пила» и одиночные пики — рывки, даже если средний FPS высокий. Включите «График» — это главный показатель плавности.",
                    "How many milliseconds a frame took: 16.7 ms = 60 FPS, 6.9 ms = 144 FPS. The graph plots every frame. A flat line is smooth; a sawtooth and single spikes are hitches even with a high average FPS. Turn on “Graph” — this is the main smoothness metric.");
                case "fps.screen": return Tr.S(
                    "Сколько кадров за последнюю секунду реально ушло на вывод. «Кадров в секунду» считает вызовы игры, а эта строка — кадры, которые приняты на показ: если она заметно ниже, часть кадров пропадает впустую (кадров больше, чем герц, или их съедает композиция).",
                    "How many frames actually went out to the display in the last second. “Frames per second” counts the game's calls; this row counts the frames accepted for display: noticeably lower means frames are being thrown away (more frames than hertz, or composition eats them).");
                case "fps.screenms": return Tr.S(
                    "Время между кадрами НА ВЫВОДЕ. Сравните с графиком «Время кадра»: там пила почти всегда — вызовы Present() гуляют сами по себе и глазом не видны. Здесь разброс обычно меньше, и пила тут — это уже настоящая неровность картинки. Ровная линия на уровне периода монитора (6,1 мс при 165 Гц) — плавно.",
                    "Time between frames AS THEY GO OUT. Compare it with the “Frame time” graph: a sawtooth there is almost always present — Present() calls jitter on their own and the eye never sees it. Here the spread is usually smaller, and a sawtooth is real unevenness. A flat line at the monitor's period (6.1 ms at 165 Hz) is smooth.");
                case "fps.low1": return Tr.S(
                    "Средний FPS самого медленного 1 % кадров за последние 30 с. Если он сильно ниже обычного FPS (например, 144 и 60), игра заметно подтормаживает: подгрузка ресурсов, компиляция шейдеров, нехватка памяти или упор в процессор.",
                    "Average FPS of the slowest 1% of frames over the last 30 s. If it is far below the normal FPS (say 144 vs 60), the game visibly hitches: asset streaming, shader compilation, low memory or a CPU bottleneck.");
                case "fps.low01": return Tr.S(
                    "То же для 0,1 % самых медленных кадров — ловит редкие, но сильные рывки, которые «1 % худших» размазывает.",
                    "The same for the slowest 0.1% of frames — catches rare but severe hitches that “1% low” averages out.");
                case "fps.stutter": return Tr.S(
                    "Сколько раз за последнюю минуту кадр занял не меньше 25 мс и был в 2,5 раза дольше обычного (медианы предыдущих 15). Ноль — плавно; постоянно больше нуля — фризы, которые видны глазом.",
                    "How many times in the last minute a frame took at least 25 ms and was 2.5× longer than usual (median of the previous 15). Zero is smooth; constantly above zero means visible stutters.");
                case "fps.bottleneck": return Tr.S(
                    "Что сейчас держит кадры: «видеокарта» — она загружена на 95 % и больше; «процессор» — одно ядро за 90 % (игре не хватает скорости потока); «частота монитора / V-Sync» — кадры упираются в частоту экрана; «ограничитель или игра» — и видеокарта, и процессор недогружены. Это оценка по трём числам, а не замер.",
                    "What holds the frame rate now: «GPU» — it is loaded 95% or more; «CPU» — one core is above 90% (the game lacks single-thread speed); «refresh rate / V-Sync» — frames sit at the display refresh rate; «limiter or the game» — neither GPU nor CPU is busy. An estimate from three numbers, not a measurement.");
                case "fps.perwatt": return Tr.S(
                    "Кадры в секунду, делённые на мощность видеокарты. Удобно сравнивать настройки и ограничители: те же кадры при меньших ваттах — тише и холоднее.",
                    "Frames per second divided by GPU power. Handy for comparing settings and limiters: the same frames for fewer watts is quieter and cooler.");
                case "fps.vsync": return Tr.S(
                    "Синхронизация, которую игра просит у DXGI при выводе кадра: «вкл» — кадр ждёт обновления экрана, «выкл, с разрывами» — выводится сразу, возможны разрывы. Только для игр на Direct3D 10–12; синхронизацию, навязанную драйвером, отсюда не видно.",
                    "The sync interval the game asks DXGI for when presenting: «on» waits for the display refresh, «off, tearing allowed» shows the frame at once and may tear. Direct3D 10–12 games only; sync forced by the driver is not visible here.");
                case "fps.api": return Tr.S(
                    "Через что игра выводит кадры: DXGI (Direct3D 10–12), Direct3D 9 или другое (OpenGL, Vulkan — их кадры видны только по событиям ядра).",
                    "How the game presents frames: DXGI (Direct3D 10–12), Direct3D 9, or other (OpenGL, Vulkan — their frames are only visible through kernel events).");
                case "fps.presentmode": return Tr.S(
                    "Собирает ли кадр Windows (DWM): «через DWM» добавляет задержку и бывает в оконном режиме, «напрямую» — полноэкранный или независимый вывод, самый быстрый. Определяется по событиям ядра видеоподсистемы; на части драйверов может не показываться.",
                    "Whether Windows (DWM) composes the frame: «via DWM» adds latency and happens in windowed mode, «direct» is fullscreen or independent flip, the fastest. Detected from graphics kernel events; some drivers may not report it.");
                case "display.hz": return Tr.S(
                    "Частота обновления монитора, на котором активное окно. По ней на графике времени кадра рисуется пунктир: всё, что выше него, монитор не успевает показать вовремя.",
                    "Refresh rate of the monitor with the active window. The frame-time graph draws a dotted line at it: anything above the line misses the display refresh.");
                case "fps.app": return Tr.S("Имя процесса, по кадрам которого считаются строки группы. Проверка, что считается именно игра, а не браузер на втором мониторе.",
                                            "Name of the process whose frames the group counts. A check that the game is measured, not a browser on the second monitor.");

                case "app.name": return Tr.S("Процесс активного окна; «+N» — сколько дочерних процессов учтено вместе с ним.", "The active window's process; “+N” is how many child processes are counted with it.");
                case "app.ram": return Tr.S(
                    "Сколько физической памяти сейчас держит игра (рабочий набор, как «Память» в диспетчере задач). Растёт весь сеанс без остановки — возможна утечка. Если вместе с ним ОЗУ системы под 90 % — Windows начнёт выгружать в файл подкачки, появятся фризы.",
                    "Physical memory the game holds now (working set, like “Memory” in Task Manager). Growing all session without stopping may mean a leak. With system RAM near 90%, Windows starts paging and stutters appear.");
                case "app.private": return Tr.S(
                    "Сколько памяти игра запросила у системы только для себя, включая выгруженное на диск. Больше «ОЗУ игры» — часть лежит в файле подкачки.",
                    "Memory the game requested for itself, including what is paged out. Larger than “Game RAM” means part of it sits in the page file.");
                case "app.vram": return Tr.S(
                    "Сколько выделенной видеопамяти занимает игра. Если вместе со всем остальным упирается в объём карты, текстуры уходят в общую память — это рывки и падение «1 % худших».",
                    "Dedicated video memory the game occupies. When it and everything else reach the card's capacity, textures spill into shared memory — hitches and a drop in “1% low”.");
                case "app.vramshared": return Tr.S(
                    "Видеопамять игры, взятая из оперативной памяти. Заметные сотни мегабайт у дискретной карты — видеопамяти не хватило, снизьте качество текстур.",
                    "The game's GPU memory borrowed from system RAM. Hundreds of megabytes on a discrete card mean VRAM ran out — lower texture quality.");
                case "app.cpu": return Tr.S(
                    "Доля всех ядер, занятая игрой (100 % = все ядра). Низкое число при низкой загрузке видеокарты — смотрите ядра по отдельности: игра может упираться в одно ядро.",
                    "Share of all cores used by the game (100% = every core). A low number together with low GPU load — check cores individually: the game may be bound by one core.");
                case "app.gpu": return Tr.S(
                    "Какую часть видеокарты занимает именно игра. Сильно меньше общей загрузки — видеокарту делит что-то ещё (запись видео, браузер).",
                    "How much of the GPU the game itself uses. Much lower than total GPU load means something else shares the card (recording, a browser).");
                case "app.io.read": return Tr.S("Сколько игра читает в секунду (диск, сеть, прочий ввод-вывод). Всплески совпадают с рывками — это подгрузка ресурсов.",
                                                "How much the game reads per second (disk, network, other I/O). Spikes that coincide with hitches are asset loading.");
                case "app.io.write": return Tr.S("Сколько игра пишет в секунду: сохранения, кэш шейдеров, журналы.", "How much the game writes per second: saves, shader cache, logs.");
                case "app.threads": return Tr.S("Сколько потоков у игры и её дочерних процессов.", "Threads of the game and its child processes.");
                case "app.handles": return Tr.S("Открытые дескрипторы. Непрерывный рост за сеанс — утечка в игре или моде.", "Open handles. Continuous growth during a session is a leak in the game or a mod.");
                case "app.uptime": return Tr.S("Сколько времени запущен процесс игры.", "How long the game process has been running.");

                case "cpu.coremax": return Tr.S(
                    "Загрузка самого занятого логического ядра. Общая загрузка 30 %, а здесь 100 % — игра упёрлась в один поток, и более мощная видеокарта кадров не добавит.",
                    "Load of the busiest logical core. Overall 30% but 100% here means the game is bound to one thread, and a faster GPU will not add frames.");
                case "cpu.perflimit": return Tr.S(
                    "Какую долю своей частоты процессору сейчас разрешено держать. 100 % — ограничений нет; меньше — Windows или прошивка сбросили частоту (перегрев, питание, план энергопотребления). Желтеет с 90 %, краснеет с 70 %.",
                    "Share of its frequency the CPU is currently allowed. 100% — no limit; lower means Windows or firmware capped it (heat, power, power plan). Yellow at 90%, red at 70%.");
                case "cpu.load": return Tr.S("Загрузка всех ядер вместе. Под 100 % — процессор ограничивает кадры.", "Load of all cores together. Near 100% the CPU limits frames.");
                case "cpu.temp": return Tr.S(
                    "Температура процессора. Желтеет с 85 °C, краснеет с 95 °C (пороги меняются на странице «Оверлей»); у многих процессоров сброс частоты (троттлинг) начинается около 95–100 °C. Без HWiNFO/Afterburner — только датчик платы, он занижает.",
                    "CPU temperature. Yellow from 85 °C, red from 95 °C (thresholds are adjustable on the Overlay page); many CPUs start throttling around 95–100 °C. Without HWiNFO/Afterburner only the board sensor is available and it reads low.");
                case "cpu.mhz": return Tr.S("Средняя частота ядер. Проседает под нагрузкой — троттлинг по температуре или питанию, либо включён режим пониженной мощности.",
                                            "Average core frequency. Dropping under load means thermal or power throttling, or a reduced power mode.");
                case "cpu.power": return Tr.S("Сколько ватт потребляет процессор (пакет). Упирается в одно и то же число при падении частоты — предел мощности.", "CPU package power in watts. Stuck at the same number while frequency drops means a power limit.");
                case "cpu.voltage": return Tr.S("Напряжение ядра. Нужно при разгоне и андервольте.", "Core voltage. Useful for overclocking and undervolting.");
                case "gpu.load": return Tr.S("Загрузка самого занятого движка видеокарты (3D, видео…), как в диспетчере задач.", "Load of the busiest GPU engine (3D, video…), as in Task Manager.");
                case "gpu.temp": return Tr.S("Температура ядра видеокарты. Желтеет с 80 °C, краснеет с 87 °C.", "GPU core temperature. Yellow from 80 °C, red from 85 °C.");
                case "gpu.hotspot": return Tr.S("Самая горячая точка кристалла. Разница с температурой ядра больше ~20 °C — стоит проверить термопасту.", "Hottest point of the die. A gap above ~20 °C from the core temperature suggests checking the thermal paste.");
                case "gpu.memtemp": return Tr.S("Температура видеопамяти. У GDDR6X выше ~100 °C начинается сброс частоты.", "Video memory temperature. GDDR6X starts throttling above ~100 °C.");
                case "gpu.power": return Tr.S("Сколько ватт потребляет видеокарта. Держится на пределе — карта ограничена питанием (см. «Предел мощности» и «Причина сброса частоты»).", "GPU power draw in watts. Sitting at the limit means the card is power-limited (see “Power limit” and “Throttle reason”).");
                case "gpu.powerlimit": return Tr.S("Текущий предел мощности, заданный драйвером или профилем Afterburner.", "Current power limit set by the driver or an Afterburner profile.");
                case "gpu.fan": case "gpu.fanrpm": return Tr.S("Скорость вентиляторов видеокарты.", "GPU fan speed.");
                case "gpu.clock": return Tr.S("Частота ядра видеокарты. В игре ниже обычной — карта сбросила частоту или ушла в экономичный режим.", "GPU core clock. Lower than usual in a game means the card throttled or went into a power-saving state.");
                case "gpu.memclock": return Tr.S("Частота видеопамяти.", "Video memory clock.");
                case "gpu.voltage": return Tr.S("Напряжение ядра видеокарты — для андервольта и разгона.", "GPU core voltage — for undervolting and overclocking.");
                case "gpu.memload": return Tr.S("Загрузка контроллера видеопамяти (сколько обращений к памяти), не объём.", "Memory controller load (how busy memory access is), not the amount used.");
                case "gpu.enc": return Tr.S("Загрузка аппаратного кодировщика — идёт запись или трансляция.", "Hardware encoder load — recording or streaming is running.");
                case "gpu.dec": return Tr.S("Загрузка аппаратного декодера — воспроизводится видео.", "Hardware decoder load — video is playing.");
                case "gpu.pstate": return Tr.S("Режим питания: P0 — полная производительность, P8 и выше — простой. В игре не P0 — карта не разогналась.", "Performance state: P0 is full performance, P8 and higher is idle. Not P0 in a game means the card did not ramp up.");
                case "gpu.throttle": return Tr.S(
                    "Почему видеокарта сейчас сбрасывает частоту: предел мощности, температура, простой, синхронизация и т. д. Пусто или «нет» — ничего не ограничивает.",
                    "Why the GPU is lowering its clock right now: power limit, temperature, idle, sync and so on. Empty or “none” means nothing is limiting it.");
                case "gpu.pcie": return Tr.S("Текущая скорость шины PCIe. В простое карта сама понижает её — смотреть под нагрузкой.", "Current PCIe link. The card lowers it at idle — check under load.");
                case "ram": case "ram.load": return Tr.S(
                    "Занятая оперативная память всей системы. Желтеет с 80 %, краснеет с 90 %: дальше Windows выгружает память на диск, и игры фризят.",
                    "System RAM in use. Yellow from 80%, red from 90%: beyond that Windows pages memory to disk and games stutter.");
                case "ram.hardfaults": return Tr.S(
                    "Сколько страниц памяти в секунду Windows читает обратно с диска. В игре сотни и тысячи — памяти не хватает, отсюда фризы. Желтеет с 300, краснеет с 1500.",
                    "How many memory pages per second Windows reads back from disk. Hundreds or thousands during a game mean RAM is short and cause stutter. Yellow from 300, red from 1500.");
                case "commit": return Tr.S("Выделено всеми процессами из ОЗУ и файла подкачки. Упёрлось во «всего» — программы начнут падать с нехваткой памяти.", "Committed by all processes from RAM and the page file. Hitting the total makes programs fail with out-of-memory errors.");
                case "vram": return Tr.S("Занятая видеопамять всей карты. Под 100 % — рывки из-за подкачки текстур.", "Video memory used on the whole card. Near 100% means hitches from texture swapping.");
                case "mem.timings": return Tr.S("Частота и основные тайминги памяти (CL-tRCD-tRP-tRAS) из HWiNFO — проверить, что включён профиль XMP/EXPO.", "Memory speed and primary timings (CL-tRCD-tRP-tRAS) from HWiNFO — check that the XMP/EXPO profile is on.");
                case "hot.max": return Tr.S("Максимум среди всех настоящих датчиков температуры; какой датчик — видно в строке «Источник».", "Maximum across all real temperature sensors; which sensor it is shows in the “Source” line.");
                case "disk.read": case "disk.write": return Tr.S("Скорость всех дисков вместе.", "Speed of all disks together.");
                case "net.down": case "net.up": return Tr.S("Скорость всех сетевых адаптеров вместе.", "Speed of all network adapters together.");
                case "clock": return Tr.S("Текущее время.", "Current time.");
                case "sys.uptime": return Tr.S("Сколько работает Windows с последней загрузки (быстрый запуск не сбрасывает).", "Windows uptime since the last boot (fast startup does not reset it).");
            }
            int core; string metric;
            if (HudCatalog.TryCore(id, out core, out metric))
                return metric == "mhz"
                    ? Tr.S("Частота этого логического процессора. Ниже базовой под нагрузкой — сброс частоты.", "Frequency of this logical processor. Below base under load means throttling.")
                    : Tr.S("Загрузка этого логического процессора. Одно ядро под 100 % при свободных остальных — игра упирается в него.", "Load of this logical processor. One core at 100% with the rest idle means the game is bound by it.");
            if (id != null && id.StartsWith("sys.", StringComparison.Ordinal))
                return ForGroup(HudGroups.System);
            if (id != null && id.StartsWith("hw.", StringComparison.Ordinal))
                return Tr.S("Показание датчика из HWiNFO, как оно названо в HWiNFO.", "A sensor reading from HWiNFO, named as HWiNFO names it.");
            if (id != null && id.StartsWith("ab.", StringComparison.Ordinal))
                return Tr.S("Показатель из MSI Afterburner, как он назван в Afterburner.", "A value from MSI Afterburner, named as Afterburner names it.");
            return "";
        }
    }
}
