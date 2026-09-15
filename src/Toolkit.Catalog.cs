// SysDeck — страница «Скрипты»: каталог встроенных скриптов обслуживания Windows и их параметров.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs; файлы toolkit\ встраиваются ресурсами).
//
// Скрипты лежат в exe ресурсами «toolkit/<папка>/<файл>» и копируются на диск кнопкой. Задачи Планировщика программа
// описывает сама (Toolkit.Tasks.cs), а не запускает install-task.ps1: так параметры (интервал, время) видны и меняются
// здесь, повторное «Сохранить» просто перерегистрирует задачу, и консольное окно PowerShell не мелькает.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SysDeck.Toolkit
{
    internal enum TkKind { Minutes, Time, Int, Bool, Text, Choice }

    // Куда ставится скрипт. Protected — для того, что работает от SYSTEM: папка в ProgramData, писать в которую может
    // только администратор (иначе любой процесс пользователя подменил бы скрипт, исполняемый с наивысшими правами).
    internal enum TkPlace { UserTools, UserHome, Protected, ClaudeHooks, None }

    internal sealed class TkParam
    {
        public string Key, Ru, En, HintRu, HintEn, Default;
        public TkKind Kind;
        public int Min, Max;
        public string[] Choices, ChoiceRu, ChoiceEn;

        public string Label { get { return Tr.S(Ru, En); } }
        public string Hint { get { return HintRu == null ? null : Tr.S(HintRu, HintEn); } }

        // Любое значение, пришедшее из окна, файла или задания помощнику, проходит здесь: неверное становится умолчанием.
        public string Normalize(string raw)
        {
            string s = (raw ?? "").Trim();
            switch (Kind)
            {
                case TkKind.Minutes:
                case TkKind.Int:
                    {
                        int v;
                        if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return Default;
                        return Math.Max(Min, Math.Min(Max, v)).ToString(CultureInfo.InvariantCulture);
                    }
                case TkKind.Time:
                    {
                        int h, m;
                        return TryTime(s, out h, out m) ? h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) : Default;
                    }
                case TkKind.Bool:
                    if (s.Equals("true", StringComparison.OrdinalIgnoreCase)) return "true";
                    if (s.Equals("false", StringComparison.OrdinalIgnoreCase)) return "false";
                    return Default;
                case TkKind.Choice:
                    foreach (string c in Choices) if (c.Equals(s, StringComparison.OrdinalIgnoreCase)) return c;
                    return Default;
                default:
                    {
                        // Текст уходит в JSON-настройку скрипта и в имя ярлыка: без управляющих символов и не длиннее 80.
                        System.Text.StringBuilder sb = new System.Text.StringBuilder();
                        foreach (char ch in s) if (!char.IsControl(ch)) sb.Append(ch);
                        string t = sb.ToString().Trim();
                        if (t.Length > 80) t = t.Substring(0, 80);
                        return Max > 0 && t.Length == 0 ? Default : t;
                    }
            }
        }

        internal static bool TryTime(string s, out int h, out int m)
        {
            h = m = 0;
            string[] p = (s ?? "").Trim().Split(':');
            return p.Length == 2 && int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out h)
                && int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out m) && h >= 0 && h < 24 && m >= 0 && m < 60;
        }
    }

    internal sealed class TkItem
    {
        public string Id, Ru, En, WhyRu, WhyEn, Folder;
        public TkPlace Place;
        public bool Admin;
        public string[] Tasks = new string[0];          // все задачи, которыми пункт может владеть (часть — по параметру)
        public string[] KeepIfPresent = new string[0];  // настройки, которые пользователь правит сам: при обновлении не затираются
        public string LogPath;                          // с переменными окружения
        public List<TkParam> Params = new List<TkParam>();

        public string Title { get { return Tr.S(Ru, En); } }
        public string Why { get { return Tr.S(WhyRu, WhyEn); } }

        public TkParam Param(string key)
        {
            foreach (TkParam p in Params) if (p.Key == key) return p;
            return null;
        }

        public Dictionary<string, string> Defaults()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            foreach (TkParam p in Params) d[p.Key] = p.Default;
            return d;
        }

        // Значения из чужого источника — только известные ключи и только нормализованные.
        public Dictionary<string, string> Clean(IDictionary<string, string> raw)
        {
            Dictionary<string, string> d = Defaults();
            if (raw != null)
                foreach (TkParam p in Params)
                {
                    string v;
                    if (raw.TryGetValue(p.Key, out v)) d[p.Key] = p.Normalize(v);
                }
            return d;
        }
    }

    internal static class TkCatalog
    {
        public static readonly List<TkItem> All = Build();

        public static TkItem Find(string id)
        {
            foreach (TkItem it in All) if (it.Id == id) return it;
            return null;
        }

        private static TkParam Minutes(string key, string def, int min, int max, string ru, string en)
        {
            TkParam p = new TkParam();
            p.Key = key; p.Kind = TkKind.Minutes; p.Default = def; p.Min = min; p.Max = max; p.Ru = ru; p.En = en;
            return p;
        }

        private static TkParam Time(string key, string def, string ru, string en)
        {
            TkParam p = new TkParam();
            p.Key = key; p.Kind = TkKind.Time; p.Default = def; p.Ru = ru; p.En = en;
            return p;
        }

        private static TkParam Int(string key, string def, int min, int max, string ru, string en)
        {
            TkParam p = new TkParam();
            p.Key = key; p.Kind = TkKind.Int; p.Default = def; p.Min = min; p.Max = max; p.Ru = ru; p.En = en;
            return p;
        }

        private static TkParam Bool(string key, bool def, string ru, string en)
        {
            TkParam p = new TkParam();
            p.Key = key; p.Kind = TkKind.Bool; p.Default = def ? "true" : "false"; p.Ru = ru; p.En = en;
            return p;
        }

        // required — пустое значение недопустимо (становится умолчанием).
        private static TkParam Text(string key, string def, bool required, string ru, string en)
        {
            TkParam p = new TkParam();
            p.Key = key; p.Kind = TkKind.Text; p.Default = def; p.Max = required ? 1 : 0; p.Ru = ru; p.En = en;
            return p;
        }

        private static TkParam Hint(TkParam p, string ru, string en) { p.HintRu = ru; p.HintEn = en; return p; }

        private static TkItem Item(string id, string folder, TkPlace place, string ru, string en, string whyRu, string whyEn)
        {
            TkItem it = new TkItem();
            it.Id = id; it.Folder = folder; it.Place = place; it.Ru = ru; it.En = en; it.WhyRu = whyRu; it.WhyEn = whyEn;
            return it;
        }

        // Умолчания — проверенная рабочая настройка; «Установить отмеченные» повторяет её на любом компьютере.
        private static List<TkItem> Build()
        {
            List<TkItem> list = new List<TkItem>();

            TkItem reaper = Item("proc-reaper", "proc-reaper", TkPlace.UserTools,
                "Уборщик зависших процессов", "Stuck process reaper",
                "«npm run dev» и прочие dev-серверы оставляют после смерти оболочки деревья node → cmd → conhost, которые живут до "
                + "перезагрузки (однажды их накопилось 1330). Задача раз в несколько часов убирает осиротевшие процессы из белого "
                + "списка (node, python, cmd, dev-серверы…); редактор, агенты и всё с открытым окном не трогаются. Список и пороги — "
                + "в reap.config.json.",
                "“npm run dev” and other dev servers leave node → cmd → conhost trees alive after their shell dies, until a reboot "
                + "(1330 of them once). Every few hours the task reaps orphaned processes from a whitelist (node, python, cmd, dev "
                + "servers…); editors, agents and anything with a window are left alone. Lists and thresholds live in reap.config.json.");
            reaper.Tasks = new[] { "ProcReaper" };
            reaper.KeepIfPresent = new[] { "reap.config.json" };
            reaper.LogPath = @"%LOCALAPPDATA%\proc-reaper\reap.log";
            reaper.Params.Add(Hint(Minutes("minutes", "240", 15, 1440, "Интервал, минут", "Interval, minutes"),
                "Каждый проход убивает 5–20 деревьев и на ~15 с нагружает реестр — ежечасно это заметно как подтормаживание.",
                "Each pass kills 5–20 trees and churns the registry for ~15 s — hourly that shows up as a stutter."));
            reaper.Params.Add(Time("start", "03:50", "Отсчёт от", "Starting at"));
            list.Add(reaper);

            TkItem watcher = Item("vscode-watcher-reaper", "vscode-watcher-reaper", TkPlace.UserTools,
                "Залипшие наблюдатели VS Code", "Stuck VS Code file watchers",
                "Процесс file-watcher (по одному на окно VS Code) срывается в холостой цикл и держит целое ядро до перезапуска окна — "
                + "ошибка @parcel/watcher (microsoft/vscode#303702). Задача узнаёт залипший по подписи (сотни тысяч операций ввода-вывода "
                + "в секунду при нуле байт) и завершает только его: VS Code сам поднимает новый.",
                "The file-watcher process (one per VS Code window) falls into an idle spin and holds a whole core until the window "
                + "restarts — an @parcel/watcher bug (microsoft/vscode#303702). The task recognises a stuck one by its signature "
                + "(hundreds of thousands of I/O operations a second with zero bytes) and ends only it: VS Code starts a fresh one.");
            watcher.Tasks = new[] { "VSCodeWatcherReaper" };
            watcher.LogPath = @"%LOCALAPPDATA%\vscode-watcher-reaper\watcher-reap.log";
            watcher.Params.Add(Minutes("minutes", "120", 15, 1440, "Интервал, минут", "Interval, minutes"));
            watcher.Params.Add(Hint(Time("start", "00:40", "Отсчёт от", "Starting at"),
                "Время сдвинуто относительно уборщика процессов, чтобы два прохода не совпадали.",
                "Offset from the process reaper so the two passes never land together."));
            list.Add(watcher);

            TkItem governor = Item("agent-governor", "agent-governor", TkPlace.UserTools,
                "Приоритет агентов Claude", "Claude agent priority",
                "Параллельные сессии Claude (vitest, MCP, node) забирали все потоки процессора и вешали игру. Задача переводит каждое "
                + "дерево claude.exe в пониженный приоритет и на верхнюю половину логических процессоров (на машинах меньше 24 потоков — "
                + "только приоритет); дочерние процессы наследуют оба свойства.",
                "Parallel Claude sessions (vitest, MCP, node) took every CPU thread and froze games. The task moves each claude.exe "
                + "tree to below-normal priority and the upper half of logical CPUs (below 24 threads — priority only); children "
                + "inherit both.");
            governor.Tasks = new[] { "AgentGovernor" };
            governor.Params.Add(Hint(Minutes("minutes", "10", 1, 240, "Проверять новые сессии, минут", "Check for new sessions, minutes"),
                "И ещё раз при входе в Windows. Проход без новых сессий почти бесплатный.",
                "And once more at sign-in. A pass with no new sessions costs almost nothing."));
            list.Add(governor);

            TkItem canary = Item("freeze-canary", "freeze-canary", TkPlace.UserTools,
                "Регистратор фризов", "Freeze recorder",
                "Ловит зависание системы в момент, когда оно идёт: поток повышенного приоритета просыпается раз в 200 мс и, если "
                + "опоздал на секунду, пишет в журнал длительность, время DPC и прерываний (шторм драйвера виден только там), очередь "
                + "диска, свободную память, процессы, съевшие процессор, и программу на переднем плане. Диагностика: включайте, когда "
                + "лаги есть, и выключайте, когда причина найдена.",
                "Catches a system stall while it happens: an above-normal thread wakes every 200 ms and, if a second late, logs the "
                + "duration, DPC and interrupt time (a driver storm shows only there), disk queue, free memory, the processes that "
                + "burned CPU and the foreground app. A diagnostic: turn it on while lags happen, off once the cause is found.");
            canary.Tasks = new[] { "FreezeCanary" };
            canary.LogPath = @"%LOCALAPPDATA%\freeze-canary\stalls.log";
            list.Add(canary);

            TkItem ports = Item("port-watch", "port-watch", TkPlace.UserTools,
                "Ловушка нехватки TCP-портов", "TCP port exhaustion trap",
                "Windows пишет событие Tcpip 4231 («не удалось выделить порт»), но не называет виновника, а через минуты всплеск уже не "
                + "восстановить. Задача срабатывает прямо на событие и записывает, какой процесс сколько портов держит. До повторения "
                + "проблемы не стоит ничего.",
                "Windows logs Tcpip event 4231 (“could not allocate a port”) but never names the culprit, and minutes later the burst "
                + "is gone. The task fires on the event itself and records which process holds how many ports. Costs nothing until "
                + "it happens again.");
            ports.Tasks = new[] { "PortWatch4231" };
            ports.LogPath = @"%LOCALAPPDATA%\port-watch\4231.log";
            list.Add(ports);

            TkItem audio = Item("audio-fix", "audio-fix", TkPlace.Protected,
                "Ремонт молчащего звука", "Silent audio repair",
                "Цифровой выход (S/PDIF, HDMI) после сна может молчать, хотя Windows считает устройство рабочим: обратной связи у "
                + "такого выхода нет, поэтому лечение безусловное. Задачи от имени SYSTEM после загрузки заново применяют настройки "
                + "питания USB, а после выхода из сна перезапускают аудиоустройство. Ярлык «Починить звук» на рабочем столе — ремонт "
                + "вручную.",
                "A digital output (S/PDIF, HDMI) can stay silent after sleep while Windows reports the device as working: such an "
                + "output has no feedback, so the repair is unconditional. SYSTEM tasks re-apply USB power settings after boot and "
                + "re-initialise the audio device after resume. The “Fix sound” desktop shortcut repairs it by hand.");
            audio.Admin = true;
            audio.Tasks = new[] { "AudioFix-Boot", "AudioFix-Wake", "AudioFix-Watch" };
            audio.LogPath = @"%ProgramData%\audio-fix\audio-fix.log";
            audio.Params.Add(Hint(Text("preferredEndpoint", "Realtek Digital Output", false, "Устройство вывода (часть имени)", "Output device (part of the name)"),
                "Пусто — устройство по умолчанию в Windows.", "Empty means the Windows default device."));
            audio.Params.Add(Hint(Int("wakeDelaySec", "90", 0, 600, "Пауза после выхода из сна, с", "Delay after resume, s"),
                "Дать системе проснуться до сброса порта.", "Let the system settle before the port reset."));
            audio.Params.Add(Hint(Int("wakeCooldownMin", "30", 0, 240, "Не чаще, чем раз в, минут", "At most once every, minutes"),
                "Один ремонт на цикл сна, а не на каждое событие.", "One repair per sleep cycle, not per event."));
            audio.Params.Add(Bool("restartServices", true, "Перезапускать службы звука", "Restart audio services"));
            audio.Params.Add(Bool("portReset", true, "Сбрасывать порт устройства (перезапуск чипа)", "Reset the device port (restarts the chip)"));
            audio.Params.Add(Bool("allowDriverReinstall", false, "Разрешить переустановку драйвера", "Allow driver reinstall"));
            audio.Params.Add(Hint(Bool("watch", true, "Записывать состояние устройства (раз в 30 с)", "Record the device state (every 30 s)"),
                "Задача AudioFix-Watch: только чтение, нужна, чтобы следующую поломку объяснить, а не угадывать.",
                "Task AudioFix-Watch: read-only, so the next failure can be explained instead of guessed."));
            audio.Params.Add(Bool("shortcut", true, "Ярлык на рабочем столе", "Desktop shortcut"));
            audio.Params.Add(Text("shortcutName", "Починить звук", true, "Имя ярлыка", "Shortcut name"));
            list.Add(audio);

            TkItem mpo = Item("mpo-fix", "mpo-fix", TkPlace.Protected,
                "Отключение MPO (фризы курсора)", "Disable MPO (cursor freezes)",
                "На нескольких мониторах с разной частотой или на разных видеокартах наложение MPO даёт фризы курсора и окон при "
                + "переходе между экранами. Лечение — параметр DWM OverlayTestMode = 5. Windows и обновления драйверов его молча "
                + "стирают, поэтому можно переприменять при каждом входе. Нужен только на смешанных мультимониторных системах; "
                + "«Удалить» возвращает MPO как было.",
                "With several monitors at different refresh rates or on different GPUs, MPO overlays freeze the cursor and windows "
                + "when crossing screens. The cure is the DWM value OverlayTestMode = 5. Windows and driver updates silently erase "
                + "it, so it can be re-applied at every sign-in. Only for mixed multi-monitor setups; “Remove” restores MPO.");
            mpo.Admin = true;
            mpo.Tasks = new[] { "MpoFix-Logon" };
            mpo.Params.Add(Bool("reapply", false, "Переприменять при входе в Windows", "Re-apply at sign-in"));
            list.Add(mpo);

            TkItem docker = Item("docker-maint", "docker-maint", TkPlace.UserTools,
                "Обслуживание Docker / WSL2", "Docker / WSL2 upkeep",
                "Скрипт показывает, сколько памяти и диска держит Docker, и чистит остановленные контейнеры, висячие образы и кэш "
                + "сборки. Ежедневный проход удаляет только старое и никогда не трогает тома и работающие контейнеры. Сжатие диска "
                + "Docker — на странице «Docker».",
                "The script reports how much memory and disk Docker holds and clears stopped containers, dangling images and build "
                + "cache. The daily pass removes only old items and never touches volumes or running containers. Compacting the "
                + "Docker disk is on the “Docker” page.");
            docker.Tasks = new[] { "DockerMaint-AutoPrune" };
            docker.Params.Add(Bool("auto", true, "Чистить автоматически каждый день", "Clean automatically every day"));
            docker.Params.Add(Time("time", "04:00", "Время", "Time"));
            list.Add(docker);

            TkItem wsl = Item("wslconfig", "wsl", TkPlace.None,
                "Лимиты памяти WSL2 / Docker Desktop", "WSL2 / Docker Desktop memory limits",
                "Без настройки WSL2 берёт до половины памяти компьютера и не возвращает освобождённое (VmmemWSL держал 5,7 ГБ на трёх "
                + "простаивающих контейнерах). Пишутся только эти ключи раздела [wsl2] файла .wslconfig, остальное в нём остаётся. "
                + "Вступает в силу после перезапуска WSL — программа предложит его после сохранения.",
                "Without it WSL2 takes up to half of the computer's memory and never gives freed memory back (VmmemWSL held 5.7 GB "
                + "for three idle containers). Only these keys of the [wsl2] section in .wslconfig are written; everything else stays. "
                + "Takes effect after WSL restarts — the program offers that after saving.");
            wsl.Params.Add(Int("memory", DefaultWslMemoryGb().ToString(CultureInfo.InvariantCulture), 1, 1024, "Предел памяти, ГБ", "Memory limit, GB"));
            wsl.Params.Add(Int("swap", "8", 0, 256, "Файл подкачки, ГБ", "Swap, GB"));
            TkParam reclaim = new TkParam();
            reclaim.Key = "autoMemoryReclaim"; reclaim.Kind = TkKind.Choice; reclaim.Default = "dropcache";
            reclaim.Ru = "Возврат памяти Windows"; reclaim.En = "Give memory back to Windows";
            reclaim.Choices = new[] { "dropcache", "gradual", "disabled" };
            reclaim.ChoiceRu = new[] { "сразу при простое (dropcache)", "постепенно (gradual)", "не возвращать" };
            reclaim.ChoiceEn = new[] { "at once when idle (dropcache)", "gradually (gradual)", "never" };
            wsl.Params.Add(reclaim);
            wsl.Params.Add(Bool("sparseVhd", true, "Виртуальный диск уменьшается после удаления данных", "The virtual disk shrinks after deleting data"));
            list.Add(wsl);

            TkItem hook = Item("claude-hook", "claude-hook", TkPlace.ClaudeHooks,
                "Хук Claude Code: уборка в конце сессии", "Claude Code hook: cleanup at session end",
                "Когда сессия Claude Code закрывается, хук запускает уборщик процессов и убирает то, что оставили её оболочки. "
                + "Регистрируется в %USERPROFILE%\\.claude\\settings.json (событие SessionEnd), рядом создаётся копия settings.json.bak. "
                + "Нужен установленный уборщик процессов и Node.js.",
                "When a Claude Code session closes, the hook runs the process reaper and removes what its shells left behind. "
                + "Registered in %USERPROFILE%\\.claude\\settings.json (SessionEnd event), with a settings.json.bak copy next to it. "
                + "Needs the process reaper and Node.js.");
            list.Add(hook);

            TkItem tv = Item("tv-switch", "tv-switch", TkPlace.UserHome,
                "Выключатель телевизора на HDMI", "HDMI TV switch",
                "Выключенный телевизор на HDMI может переподключаться каждые несколько секунд, и каждый раз Windows перестраивает "
                + "рабочий стол — это короткие стопы по всей системе. «Выключить в Windows» отключает дисплей программно (кабель "
                + "остаётся), «Включить» возвращает. Нужен модуль PowerShell DisplayConfig (ставится из PowerShell Gallery).",
                "A switched-off HDMI TV can reconnect every few seconds, and each time Windows rebuilds the desktop — short stalls "
                + "across the whole system. “Turn off in Windows” detaches the display in software (the cable stays), “Turn on” "
                + "brings it back. Needs the DisplayConfig PowerShell module (installed from the PowerShell Gallery).");
            tv.Params.Add(Hint(Text("name", "", false, "Имя телевизора (часть)", "TV name (part)"),
                "Как телевизор назван в параметрах дисплея Windows. Без имени и кода скрипт не установится.",
                "As the TV is named in Windows display settings. Without a name or an id the script will not install."));
            tv.Params.Add(Text("hwid", "", false, "Код оборудования (часть)", "Hardware id (part)"));
            list.Add(tv);

            return list;
        }

        // 24 ГБ — на машине от 48 ГБ; на машине меньше 48 ГБ — половина памяти, как у самого WSL.
        internal static int DefaultWslMemoryGb()
        {
            ulong total = 0;
            try
            {
                Native.MEMORYSTATUSEX m = new Native.MEMORYSTATUSEX();
                m.dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
                if (Native.GlobalMemoryStatusEx(ref m)) total = m.ullTotalPhys;
            }
            catch { }
            if (total == 0) return 24;
            int half = (int)(total / (1024UL * 1024UL * 1024UL) / 2);
            return Math.Max(1, Math.Min(24, half));
        }
    }
}
