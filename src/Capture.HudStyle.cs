// SysDeck — оформление оверлея: пороги подсветки строк, вид столбика (шрифт, жирные подписи, цвета
// групп, тень, строка или столбик), палитра цветов для страницы настроек и готовые наборы строк.
// Без окон и счётчиков — проверяется тестами.
using System;
using System.Collections.Generic;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Пороги подсветки: у каждой строки свои или по умолчанию для показателя
    // ------------------------------------------------------------------ //
    internal static class HudAlarm
    {
        // Кадры и предел частоты процессора плохи, когда их МАЛО. Остальное — когда много.
        public static bool LowIsBad(string id)
        {
            return id == "fps" || id == "fps.low1" || id == "fps.low01" || id == "cpu.perflimit";
        }

        // warn — жёлтый, crit — красный. NaN — строка по умолчанию не подсвечивается: загрузка видеокарты 99 % в игре —
        // это хорошо, а не тревога.
        public static void Defaults(string id, HudKind kind, out double warn, out double crit)
        {
            warn = crit = double.NaN;
            switch (id)
            {
                case "fps": warn = 60; crit = 30; return;
                case "fps.low1": case "fps.low01": warn = 45; crit = 25; return;
                case "fps.frametime": warn = 25; crit = 50; return;
                case "fps.stutter": warn = 2; crit = 10; return;
                case "cpu.perflimit": warn = 90; crit = 70; return;
                case "ram.hardfaults": warn = 300; crit = 1500; return;
                case "cpu.temp": warn = 85; crit = 95; return;
                case "gpu.temp": warn = 80; crit = 87; return;
                case "gpu.hotspot": warn = 95; crit = 105; return;
                case "gpu.memtemp": warn = 90; crit = 100; return;
                case "gpu.load": case "app.gpu": case "gpu.memload": case "gpu.enc": case "gpu.dec": case "gpu.fan": case "app.cpu": return;
            }
            if (id != null && id.StartsWith("cpu.core.", StringComparison.Ordinal)) return;
            switch (kind)
            {
                case HudKind.Percent: warn = 70; crit = 90; return;
                case HudKind.Temp: warn = 75; crit = 85; return;
                case HudKind.Memory: warn = 80; crit = 90; return;     // проценты от объёма
            }
        }

        public static void Effective(HudItem item, HudKind kind, out double warn, out double crit)
        {
            Defaults(item.Id, kind, out warn, out crit);
            if (HudFormat.Valid(item.Warn)) warn = item.Warn;
            if (HudFormat.Valid(item.Crit)) crit = item.Crit;
        }

        // 0 — обычное, 1 — высокое, 2 — критическое.
        public static int Level(HudItem item, HudKind kind, HudValue v)
        {
            if (item == null || item.NoAlarm || v == null || kind == HudKind.Text) return 0;
            double x = v.Value;
            if (!HudFormat.Valid(x)) return 0;
            if (kind == HudKind.Memory)
            {
                if (!HudFormat.Valid(v.Total) || v.Total <= 0) return 0;
                x = x * 100.0 / v.Total;
            }
            double warn, crit;
            Effective(item, kind, out warn, out crit);
            if (LowIsBad(item.Id))
            {
                if (HudFormat.Valid(crit) && x <= crit) return 2;
                if (HudFormat.Valid(warn) && x <= warn) return 1;
                return 0;
            }
            if (HudFormat.Valid(crit) && x >= crit) return 2;
            if (HudFormat.Valid(warn) && x >= warn) return 1;
            return 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Вид столбика
    // ------------------------------------------------------------------ //
    internal sealed class HudStyle
    {
        public const string DefaultFont = "Segoe UI";
        public const int MinFontSize = 9, MaxFontSize = 40;
        public const int MinGraphWidth = 100, MaxGraphWidth = 400;
        public static readonly string[] Fonts = { "Segoe UI", "Bahnschrift", "Consolas", "Arial", "Verdana", "Tahoma" };

        public string Font = DefaultFont;
        public int FontSize = 13;
        public bool BoldLabels = true, GroupColors = true, Shadow = true, RowLayout;
        public int Opacity = 75;
        // Ширина полосы графика в процентах от обычной. Полоса идёт во всю ширину столбика, поэтому столбик
        // раздаётся до неё: узкого графика в широком столбике не бывает, а текст остаётся на своих местах.
        public int GraphWidth = 100;
        public string Header;        // строка над строками («● Запись лагов 00:42», «Набор 2»); null — нет
        public int HeaderColor;        // ARGB заголовка; 0 — белый
        public bool MoveMode;          // рамка и подсказка «перетащите мышью»

        public static string ValidFont(string name)
        {
            foreach (string f in Fonts) if (string.Equals(f, name, StringComparison.OrdinalIgnoreCase)) return f;
            return DefaultFont;
        }

        public static HudStyle From(CapSettings s)
        {
            HudStyle st = new HudStyle();
            if (s == null) return st;
            st.Font = ValidFont(s.HudFont);
            st.FontSize = Math.Max(MinFontSize, Math.Min(MaxFontSize, s.HudFontSize));
            st.BoldLabels = s.HudBoldLabels;
            st.GroupColors = s.HudGroupColors;
            st.Shadow = s.HudShadow;
            st.RowLayout = s.HudRowLayout;
            st.Opacity = s.HudOpacity;
            st.GraphWidth = Math.Max(MinGraphWidth, Math.Min(MaxGraphWidth, s.HudGraphWidth));
            return st;
        }

        public HudStyle Clone() { return (HudStyle)MemberwiseClone(); }

        public const int NeutralLabel = unchecked((int)0xFFB9B9BE);

        // Цвета подписей по группам — как привыкли по RTSS: процессор голубой, видеокарта зелёная, кадры янтарные.
        public static int GroupColor(string group)
        {
            switch (group)
            {
                case HudGroups.Fps: return unchecked((int)0xFFFFC440);
                case HudGroups.App: return unchecked((int)0xFFFF9A5A);
                case HudGroups.Cpu: case HudGroups.Cores: return unchecked((int)0xFF5AB4FF);
                case HudGroups.Gpu: return unchecked((int)0xFF78DC6E);
                case HudGroups.Memory: return unchecked((int)0xFFC88CFF);
                case HudGroups.Temps: return unchecked((int)0xFFFF6E64);
                case HudGroups.Disk: return unchecked((int)0xFF5ADCD2);
                case HudGroups.Net: return unchecked((int)0xFF96C8FF);
                case HudGroups.Afterburner: return unchecked((int)0xFFAABED2);
            }
            if (group != null && group.StartsWith(HudGroups.HwinfoPrefix, StringComparison.Ordinal)) return unchecked((int)0xFFAABED2);
            return NeutralLabel;
        }

        // Палитра на странице настроек: название и ARGB.
        public static KeyValuePair<string, int>[] Palette()
        {
            return new KeyValuePair<string, int>[]
            {
                new KeyValuePair<string, int>(Tr.S("Белый", "White"), unchecked((int)0xFFFFFFFF)),
                new KeyValuePair<string, int>(Tr.S("Серый", "Grey"), unchecked((int)0xFFB9B9BE)),
                new KeyValuePair<string, int>(Tr.S("Голубой", "Sky blue"), unchecked((int)0xFF5AB4FF)),
                new KeyValuePair<string, int>(Tr.S("Синий", "Blue"), unchecked((int)0xFF4A7DFF)),
                new KeyValuePair<string, int>(Tr.S("Бирюзовый", "Teal"), unchecked((int)0xFF5ADCD2)),
                new KeyValuePair<string, int>(Tr.S("Зелёный", "Green"), unchecked((int)0xFF78DC6E)),
                new KeyValuePair<string, int>(Tr.S("Салатовый", "Lime"), unchecked((int)0xFFC4F05A)),
                new KeyValuePair<string, int>(Tr.S("Жёлтый", "Yellow"), unchecked((int)0xFFFFE45A)),
                new KeyValuePair<string, int>(Tr.S("Янтарный", "Amber"), unchecked((int)0xFFFFC440)),
                new KeyValuePair<string, int>(Tr.S("Оранжевый", "Orange"), unchecked((int)0xFFFF9A5A)),
                new KeyValuePair<string, int>(Tr.S("Красный", "Red"), unchecked((int)0xFFFF6056)),
                new KeyValuePair<string, int>(Tr.S("Розовый", "Pink"), unchecked((int)0xFFFF7EC8)),
                new KeyValuePair<string, int>(Tr.S("Фиолетовый", "Violet"), unchecked((int)0xFFC88CFF)),
            };
        }
    }

    // ------------------------------------------------------------------ //
    //  Готовые наборы строк (флаги: 1 число, 2 график, 4 мин./сред./макс.)
    // ------------------------------------------------------------------ //
    internal static class HudPresets
    {
        public static KeyValuePair<string, string>[] All()
        {
            return new KeyValuePair<string, string>[]
            {
                new KeyValuePair<string, string>(Tr.S("Минимум — только кадры", "Minimal — frames only"), "fps:1:0:0;fps.low1:1:0:0"),
                new KeyValuePair<string, string>(Tr.S("Основной", "Basic"), HudItem.Default),
                new KeyValuePair<string, string>(Tr.S("Игровой — кадры с графиком, процессор, видеокарта, память", "Gaming — frames with a graph, CPU, GPU, memory"),
                    "fps:5:0:0;fps.frametime:3:250:0;fps.low1:1:0:0;cpu.load:1:0:0;cpu.coremax:1:0:0;cpu.temp:1:0:0;gpu.load:1:0:0;gpu.temp:1:0:0;gpu.power:1:0:0;vram:1:0:0;ram:1:0:0"),
                new KeyValuePair<string, string>(Tr.S("Разбор лагов — всё, что объясняет фризы", "Lag hunt — everything that explains stutter"),
                    "fps:5:0:0;fps.frametime:3:250:0;fps.low1:1:0:0;fps.low01:1:0:0;fps.stutter:1:0:0;fps.bottleneck:1:0:0;fps.vsync:1:0:0;fps.presentmode:1:0:0;"
                    + "cpu.load:1:0:0;cpu.coremax:5:0:0;cpu.perflimit:1:0:0;gpu.load:1:0:0;gpu.throttle:1:0:0;vram:1:0:0;ram:1:0:0;ram.hardfaults:1:0:0;app.io.read:1:0:0;disk.read:1:0:0"),
                new KeyValuePair<string, string>(Tr.S("Видеокарта целиком", "Graphics card in full"),
                    "gpu.load:3:0:0;gpu.temp:5:0:0;gpu.hotspot:1:0:0;gpu.memtemp:1:0:0;gpu.power:5:0:0;gpu.powerlimit:1:0:0;gpu.fan:1:0:0;gpu.clock:1:0:0;"
                    + "gpu.memclock:1:0:0;gpu.voltage:1:0:0;gpu.pstate:1:0:0;gpu.throttle:1:0:0;vram:3:2000:0;fps.perwatt:1:0:0"),
                new KeyValuePair<string, string>(Tr.S("Температуры и питание", "Temperatures and power"),
                    "hot.max:1:0:0;cpu.temp:5:0:0;cpu.power:1:0:0;gpu.temp:5:0:0;gpu.hotspot:1:0:0;gpu.memtemp:1:0:0;gpu.power:1:0:0;cpu.perflimit:1:0:0;gpu.throttle:1:0:0"),
            };
        }
    }
}
