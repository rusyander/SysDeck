// Тесты страницы «Экраны»: запреты, из-за которых SetDisplayConfig не должен вызываться вовсе.
//
// Живой список экранов (Displays.List) и сама смена топологии проверяются прогоном на настоящей машине —
// здесь проверяется решение, принимаемое ДО вызова Windows: именно оно защищает рабочий стол от того, чтобы
// остаться без единого экрана или без основного. Ошибка в нём стоит дорого: вернуть изображение придётся
// вслепую, поэтому проверяется и то, что при запрете возвращается отказ, а не «уже сделано».
using System.Collections.Generic;
using SysDeck;

namespace SysDeck.Tests
{
    internal static class DisplayTests
    {
        private static DisplayInfo D(string key, bool active, bool primary)
        {
            DisplayInfo d = new DisplayInfo();
            d.Key = key;
            d.Name = key;
            d.Active = active;
            d.Primary = primary;
            d.Width = active ? 1920 : 0;
            d.Height = active ? 1080 : 0;
            d.Hz = active ? 60 : 0;
            return d;
        }

        private static List<DisplayInfo> Two()
        {
            List<DisplayInfo> l = new List<DisplayInfo>();
            l.Add(D("main", true, true));
            l.Add(D("tv", true, false));
            return l;
        }

        public static void Run()
        {
            List<DisplayInfo> two = Two();

            DisplayResult off = Displays.Refuse(two, "tv", false);
            T.Check("displays: a second, non-primary display may be turned off", off == null);

            DisplayResult primary = Displays.Refuse(two, "main", false);
            T.Check("displays: the primary display is never turned off", primary != null && !primary.Ok, primary == null ? "allowed" : primary.Message);

            List<DisplayInfo> one = new List<DisplayInfo>();
            one.Add(D("main", true, true));
            one.Add(D("tv", false, false));
            DisplayResult last = Displays.Refuse(one, "main", false);
            T.Check("displays: the only display that is on is never turned off", last != null && !last.Ok, last == null ? "allowed" : last.Message);

            // Единственный включённый, но НЕ основной (Windows оставляет так после отключения кабеля основного):
            // запрет должен срабатывать и здесь, иначе рабочий стол остался бы без изображения.
            List<DisplayInfo> lonely = new List<DisplayInfo>();
            lonely.Add(D("tv", true, false));
            lonely.Add(D("main", false, true));
            DisplayResult lastNotPrimary = Displays.Refuse(lonely, "tv", false);
            T.Check("displays: the last one on is kept even when it is not the primary", lastNotPrimary != null && !lastNotPrimary.Ok);

            T.Check("displays: turning on a display that is off is allowed", Displays.Refuse(one, "tv", true) == null);
            DisplayResult already = Displays.Refuse(two, "tv", true);
            T.Check("displays: a display that is already on is a no-op, not a refusal", already != null && already.Ok, already == null ? "null" : already.Message);
            DisplayResult gone = Displays.Refuse(two, "unplugged", false);
            T.Check("displays: an unknown row is refused instead of touching Windows", gone != null && !gone.Ok);
            T.Check("displays: an empty key is refused", Displays.Refuse(two, "", false) != null);

            // Ключ строки — путь устройства; он и сравнивается без учёта регистра (Windows отдаёт его по-разному).
            T.Check("displays: the row key matches case-insensitively", Displays.Refuse(two, "TV", false) == null);

            // Выбор пути под включение. Форма данных — снятая с этой машины 19.09.2026: у телевизора на
            // карте «nv» четыре пути (источники 0..3), и первые два ведут на источники, занятые включёнными
            // мониторами. Взять такой — получить от Windows отказ 87, что и случилось в живом прогоне.
            string[] cards = { "nv", "nv", "nv", "nv", "nv", "nv" };
            uint[] sources = { 0, 1, 0, 1, 2, 3 };
            bool[] active = { true, true, false, false, false, false };
            bool[] mine = { false, false, true, true, true, true };
            uint src;
            T.Eq("displays: the path chosen for turning on is the one with a free source", 4, Displays.PickPath(cards, sources, active, mine, out src));
            T.Eq("displays: and it keeps that path's own source", 2u, src);

            // Источник с тем же номером на ДРУГОЙ карте ничему не мешает: занятость считается по своей карте.
            string[] two2 = { "nv", "intel" };
            uint[] src2 = { 0, 0 };
            bool[] act2 = { true, false };
            bool[] mine2 = { false, true };
            T.Eq("displays: a source busy on another card does not block the path", 1, Displays.PickPath(two2, src2, act2, mine2, out src));
            T.Eq("displays: and its own source number is kept", 0u, src);

            // Свободного пути Windows не предложила — берём первый и назначаем ему наименьший незанятый номер.
            string[] full = { "nv", "nv", "nv" };
            uint[] fullSrc = { 0, 1, 1 };
            bool[] fullAct = { true, true, false };
            bool[] fullMine = { false, false, true };
            T.Eq("displays: with no free path the first one of that display is taken", 2, Displays.PickPath(full, fullSrc, fullAct, fullMine, out src));
            T.Eq("displays: and it gets the lowest free source number", 2u, src);

            // Путей этого экрана нет вовсе — звать Windows незачем.
            bool[] none = { false, false, false };
            T.Eq("displays: a display with no path of its own is not turned on", -1, Displays.PickPath(full, fullSrc, fullAct, none, out src));

            DisplayInfo info = D("tv", true, false);
            T.Eq("displays: the mode of an active display reads as WxH @ Hz", "1920x1080 @ 60 Hz", info.Mode);
            T.Eq("displays: an inactive display has no mode", "—", D("tv", false, false).Mode);
        }
    }
}
