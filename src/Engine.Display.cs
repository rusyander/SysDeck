// SysDeck — страница «Экраны»: какие дисплеи видит Windows и переключение каждого из них.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Отключение здесь — программное: путь «источник → приёмник» перестаёт быть активным, кабель остаётся в
// разъёме. Зачем это нужно: телевизор, выключенный пультом, уходит в дежурный режим, но линию HPD держит
// поднятой, поэтому Windows не видит отключения и продолжает держать для него поверхность рабочего стола —
// видеокарта композитит экран, которого никто не видит, а на смешанных частотах это ещё и удерживает
// память карты на максимальной частоте. Замерено на этой машине 19.09.2026: телевизор выключен, а в
// топологии активен как 3840x2160@60.
//
// Два запрета, оба нужны: нельзя погасить последний активный дисплей (рабочий стол остался бы без экрана)
// и нельзя погасить основной (Windows перенесла бы его сама, и куда — неизвестно; пусть пользователь
// сначала назначит основным другой). Оба проверяются до вызова SetDisplayConfig, а не после.
using System;
using System.Collections.Generic;

namespace SysDeck
{
    internal sealed class DisplayInfo
    {
        public string Key;            // путь устройства — устойчив между переподключениями
        public string Name;           // как называется в параметрах экрана
        public string GdiName;        // \\.\DISPLAY1, только у активного
        public string Connector;      // HDMI, DisplayPort, …
        public string AdapterKey;     // LUID видеокарты: по нему видно, что экраны на разных картах
        public string Gpu;            // имя карты из DXGI по тому же LUID; пусто, если DXGI её не отдал
        public bool Active;
        public bool Primary;
        public int Width, Height;
        public double Hz;

        public DispNative.LUID AdapterId;
        public uint TargetId;

        public string Mode
        {
            get
            {
                if (!Active || Width <= 0) return "—";
                string hz = Hz > 0 ? " @ " + Hz.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " Hz" : "";
                return Width.ToString(System.Globalization.CultureInfo.InvariantCulture) + "x"
                     + Height.ToString(System.Globalization.CultureInfo.InvariantCulture) + hz;
            }
        }
    }

    internal sealed class DisplayResult
    {
        public bool Ok;
        public string Message;
        public static DisplayResult Fail(string m) { DisplayResult r = new DisplayResult(); r.Ok = false; r.Message = m; return r; }
        public static DisplayResult Good(string m) { DisplayResult r = new DisplayResult(); r.Ok = true; r.Message = m; return r; }
    }

    internal static class Displays
    {
        // Все дисплеи, которые Windows вообще знает на этой машине, а не только включённые:
        // выключенный надо как-то вернуть, значит он должен быть в списке.
        public static List<DisplayInfo> List()
        {
            List<DisplayInfo> result = new List<DisplayInfo>();
            DispNative.DISPLAYCONFIG_PATH_INFO[] paths;
            DispNative.DISPLAYCONFIG_MODE_INFO[] modes;
            if (!Query(out paths, out modes)) return result;

            Dictionary<string, string> gpus = AdapterNames();
            Dictionary<string, DisplayInfo> byKey = new Dictionary<string, DisplayInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (DispNative.DISPLAYCONFIG_PATH_INFO p in paths)
            {
                string friendly, devicePath;
                uint tech;
                if (!DispNative.TargetName(p.targetInfo.adapterId, p.targetInfo.id, out friendly, out devicePath, out tech)) continue;
                if (string.IsNullOrEmpty(devicePath)) continue;

                bool active = (p.flags & DispNative.DISPLAYCONFIG_PATH_ACTIVE) != 0;
                DisplayInfo d;
                if (byKey.TryGetValue(devicePath, out d))
                {
                    // Один и тот же монитор приходит несколькими путями (по одному на возможный источник).
                    // Активный путь описывает реальность — он и побеждает.
                    if (!active || d.Active) continue;
                }
                else
                {
                    d = new DisplayInfo();
                    byKey[devicePath] = d;
                    result.Add(d);
                }

                d.Key = devicePath;
                d.Name = string.IsNullOrEmpty(friendly) ? Tr.S("Монитор", "Monitor") : friendly;
                d.Connector = DispNative.Connector(tech);
                d.AdapterKey = DispNative.LuidKey(p.targetInfo.adapterId);
                d.Gpu = GpuName(gpus, p.targetInfo.adapterId);
                d.AdapterId = p.targetInfo.adapterId;
                d.TargetId = p.targetInfo.id;
                d.Active = active;
                d.Width = d.Height = 0; d.Hz = 0; d.Primary = false; d.GdiName = null;

                if (!active) continue;

                d.GdiName = DispNative.SourceName(p.sourceInfo.adapterId, p.sourceInfo.id);
                if (p.targetInfo.refreshRate.Denominator > 0)
                    d.Hz = (double)p.targetInfo.refreshRate.Numerator / p.targetInfo.refreshRate.Denominator;

                uint idx = p.sourceInfo.modeInfoIdx;
                if (idx != DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID && idx < modes.Length
                    && modes[idx].infoType == DispNative.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE)
                {
                    d.Width = (int)modes[idx].mode.sourceMode.width;
                    d.Height = (int)modes[idx].mode.sourceMode.height;
                    // Основной — тот, чей левый верхний угол в начале координат рабочего стола.
                    d.Primary = modes[idx].mode.sourceMode.position.x == 0 && modes[idx].mode.sourceMode.position.y == 0;
                }
            }
            return result;
        }

        // Решение «можно ли» отделено от самой смены топологии: всё, из-за чего SetDisplayConfig звать нельзя,
        // проверяется по списку до вызова — и проверяется тестами без единого настоящего монитора.
        // Не null — ответ пользователю и повод не трогать Windows (Ok = менять нечего, Fail = запрет).
        internal static DisplayResult Refuse(List<DisplayInfo> before, string key, bool on)
        {
            if (string.IsNullOrEmpty(key)) return DisplayResult.Fail(Tr.S("Экран не выбран.", "No display selected."));

            DisplayInfo target = null;
            int activeCount = 0;
            foreach (DisplayInfo d in before)
            {
                if (d.Active) activeCount++;
                if (string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase)) target = d;
            }
            if (target == null) return DisplayResult.Fail(Tr.S("Экран больше не подключён.", "The display is no longer connected."));
            if (target.Active == on)
                return DisplayResult.Good(on ? Tr.S("Экран уже включён.", "The display is already on.")
                                             : Tr.S("Экран уже выключен.", "The display is already off."));
            if (on) return null;

            if (activeCount <= 1)
                return DisplayResult.Fail(Tr.S("Это единственный включённый экран — рабочий стол остался бы без изображения.",
                                               "This is the only display that is on — the desktop would be left without a screen."));
            if (target.Primary)
                return DisplayResult.Fail(Tr.S("Это основной экран. Сначала назначьте основным другой — иначе Windows перенесёт его сама.",
                                               "This is the primary display. Make another one primary first — otherwise Windows moves it on its own."));
            return null;
        }

        public static DisplayResult SetActive(string key, bool on)
        {
            DisplayResult refused = Refuse(List(), key, on);
            if (refused != null) return refused;

            DispNative.DISPLAYCONFIG_PATH_INFO[] paths;
            DispNative.DISPLAYCONFIG_MODE_INFO[] modes;
            if (!Query(out paths, out modes)) return DisplayResult.Fail(Tr.S("Windows не отдала конфигурацию экранов.", "Windows did not return the display configuration."));

            uint flags = DispNative.SDC_APPLY | DispNative.SDC_USE_SUPPLIED_DISPLAY_CONFIG
                       | DispNative.SDC_SAVE_TO_DATABASE | DispNative.SDC_ALLOW_CHANGES;
            int rc;
            bool found = on ? TurnOn(paths, modes, key, flags, out rc) : TurnOff(paths, modes, key, flags, out rc);
            if (!found) return DisplayResult.Fail(Tr.S("Путь этого экрана не найден.", "The path of this display was not found."));
            if (rc != DispNative.ERROR_SUCCESS)
                return DisplayResult.Fail(Tr.S("Windows отказала, код ", "Windows refused, code ") + rc.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return DisplayResult.Good(on ? Tr.S("Экран включён.", "The display is on.") : Tr.S("Экран выключен в Windows.", "The display is off in Windows."));
        }

        // Выключение: снимаем признак «в составе рабочего стола» с путей этого экрана и отдаём таблицу целиком
        // вместе с режимами — режимы остальных экранов остаются теми же, и Windows ничего не пересобирает.
        private static bool TurnOff(DispNative.DISPLAYCONFIG_PATH_INFO[] paths, DispNative.DISPLAYCONFIG_MODE_INFO[] modes,
                                    string key, uint flags, out int rc)
        {
            rc = DispNative.ERROR_SUCCESS;
            int touched = 0;
            for (int i = 0; i < paths.Length; i++)
            {
                if (!PathIs(paths[i], key)) continue;
                paths[i].flags &= ~DispNative.DISPLAYCONFIG_PATH_ACTIVE;
                // У выключенного пути режима нет вовсе: ссылку в таблицу режимов обнуляем, иначе она повиснет.
                paths[i].sourceInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                paths[i].targetInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                touched++;
            }
            if (touched == 0) return false;
            rc = DispNative.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
            return true;
        }

        // Какой из путей приёмника отдавать под включение — единственное решение в этой операции, и оно
        // отделено от Windows, чтобы проверяться тестами без единого настоящего монитора. Правило: источник
        // должен быть свободен на ТОЙ ЖЕ карте — занятый означает для Windows клон, а это отказ 87.
        // Параллельные массивы описывают таблицу путей: карта, источник, включён ли путь сейчас и наш ли это
        // экран. Возврат — индекс выбранного пути (-1, если путей этого экрана нет) и источник для него.
        internal static int PickPath(string[] cards, uint[] sources, bool[] active, bool[] mine, out uint sourceId)
        {
            sourceId = 0;
            Dictionary<string, List<uint>> busy = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < cards.Length; i++)
            {
                if (!active[i]) continue;
                if (!busy.ContainsKey(cards[i])) busy[cards[i]] = new List<uint>();
                busy[cards[i]].Add(sources[i]);
            }

            int first = -1;
            for (int i = 0; i < cards.Length; i++)
            {
                if (!mine[i]) continue;
                if (first < 0) first = i;
                List<uint> taken;
                if (!busy.TryGetValue(cards[i], out taken) || !taken.Contains(sources[i]))
                {
                    sourceId = sources[i];
                    return i;
                }
            }
            if (first < 0) return -1;

            // Свободного пути Windows не предложила — берём первый и сами назначаем ему незанятый источник.
            List<uint> used;
            busy.TryGetValue(cards[first], out used);
            uint id = 0;
            while (used != null && used.Contains(id)) id++;
            sourceId = id;
            return first;
        }

        // Включение — отдельная история, и полной таблицей его не сделать. У одного приёмника в QDC_ALL_PATHS
        // столько путей, сколько у карты источников (у HISENSE их было четыре), и первые из них указывают на
        // источники, занятые уже включёнными экранами. Отдать такой путь — попросить клон, режимы под который
        // не сходятся: Windows отвечает 87. Замерено пробой 19.09.2026: полная таблица — 87 при любых режимах,
        // «включённые + один свободный источник» — 0. Поэтому отдаём только активные пути и один выбранный.
        // Первой попыткой у активных путей сохраняются их собственные режимы: там лежат размеры И позиции, и
        // без них Windows выстраивает весь рабочий стол заново — в живом прогоне она перенесла второй DELL
        // слева направо. Режима нет только у включаемого — его и позицию для него Windows подбирает сама.
        private static bool TurnOn(DispNative.DISPLAYCONFIG_PATH_INFO[] paths, DispNative.DISPLAYCONFIG_MODE_INFO[] modes,
                                  string key, uint flags, out int rc)
        {
            rc = DispNative.ERROR_SUCCESS;
            List<DispNative.DISPLAYCONFIG_PATH_INFO> keep = new List<DispNative.DISPLAYCONFIG_PATH_INFO>();
            for (int i = 0; i < paths.Length; i++)
                if ((paths[i].flags & DispNative.DISPLAYCONFIG_PATH_ACTIVE) != 0) keep.Add(paths[i]);

            string[] cards = new string[paths.Length];
            uint[] sources = new uint[paths.Length];
            bool[] active = new bool[paths.Length];
            bool[] mine = new bool[paths.Length];
            for (int i = 0; i < paths.Length; i++)
            {
                cards[i] = DispNative.LuidKey(paths[i].sourceInfo.adapterId);
                sources[i] = paths[i].sourceInfo.id;
                active[i] = (paths[i].flags & DispNative.DISPLAYCONFIG_PATH_ACTIVE) != 0;
                mine[i] = !active[i] && PathIs(paths[i], key);
            }
            uint sourceId;
            int chosen = PickPath(cards, sources, active, mine, out sourceId);
            if (chosen < 0) return false;

            DispNative.DISPLAYCONFIG_PATH_INFO p = paths[chosen];
            p.sourceInfo.id = sourceId;
            p.flags |= DispNative.DISPLAYCONFIG_PATH_ACTIVE;
            p.sourceInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            p.targetInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            keep.Add(p);

            DispNative.DISPLAYCONFIG_PATH_INFO[] send = keep.ToArray();
            rc = DispNative.SetDisplayConfig((uint)send.Length, send, (uint)modes.Length, modes, flags);
            if (rc == DispNative.ERROR_SUCCESS) return true;

            // Режимы не подошли — второй заход без них вовсе: раскладку Windows пересоберёт, зато экран
            // включится. Ссылки в несуществующую таблицу режимов надо снять у всех путей, иначе снова 87.
            for (int i = 0; i < send.Length; i++)
            {
                send[i].sourceInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
                send[i].targetInfo.modeInfoIdx = DispNative.DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
            }
            rc = DispNative.SetDisplayConfig((uint)send.Length, send, 0, null, flags);
            return true;
        }

        private static bool PathIs(DispNative.DISPLAYCONFIG_PATH_INFO p, string key)
        {
            string friendly, devicePath;
            uint tech;
            if (!DispNative.TargetName(p.targetInfo.adapterId, p.targetInfo.id, out friendly, out devicePath, out tech)) return false;
            return string.Equals(devicePath, key, StringComparison.OrdinalIgnoreCase);
        }

        // Имя карты берётся из DXGI по тому же LUID: «на какой карте висит экран» — половина ответа
        // про смешанные частоты и рывки курсора между экранами. Ключи там пишутся иначе, чем в CCD, поэтому
        // сравниваются приведённые к одному виду (Native.LuidKey).
        private static Dictionary<string, string> AdapterNames()
        {
            Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (DxgiAdapter a in Native.DxgiAdapters())
                    if (!string.IsNullOrEmpty(a.Luid) && !map.ContainsKey(a.Luid)) map[a.Luid] = a.Name;
            }
            catch { }
            return map;
        }

        private static string GpuName(Dictionary<string, string> map, DispNative.LUID adapterId)
        {
            string name;
            return map.TryGetValue(Native.LuidKey(adapterId.HighPart, adapterId.LowPart), out name) ? name : "";
        }

        private static bool Query(out DispNative.DISPLAYCONFIG_PATH_INFO[] paths, out DispNative.DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = new DispNative.DISPLAYCONFIG_PATH_INFO[0];
            modes = new DispNative.DISPLAYCONFIG_MODE_INFO[0];
            try
            {
                uint np, nm;
                if (DispNative.GetDisplayConfigBufferSizes(DispNative.QDC_ALL_PATHS, out np, out nm) != DispNative.ERROR_SUCCESS) return false;
                if (np == 0) return false;
                DispNative.DISPLAYCONFIG_PATH_INFO[] p = new DispNative.DISPLAYCONFIG_PATH_INFO[np];
                DispNative.DISPLAYCONFIG_MODE_INFO[] m = new DispNative.DISPLAYCONFIG_MODE_INFO[nm];
                if (DispNative.QueryDisplayConfig(DispNative.QDC_ALL_PATHS, ref np, p, ref nm, m, IntPtr.Zero) != DispNative.ERROR_SUCCESS) return false;
                if (np < p.Length) Array.Resize(ref p, (int)np);
                if (nm < m.Length) Array.Resize(ref m, (int)nm);
                paths = p; modes = m;
                return true;
            }
            catch { return false; }
        }
    }
}
