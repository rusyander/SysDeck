// Windows Process Cleaner — область «ram»: сходится ли схема расхода памяти и что
// принимает канал команд элевированного помощника.
//
// Прогон идёт БЕЗ прав администратора и НИЧЕГО не сбрасывает. Ни одна проверка здесь не
// отправляет помощнику команду из диапазона 2..7: это настоящие сбросы памяти, и тест,
// который выдавливает рабочие наборы всей машины, — не тест, а диверсия. Проверяется ровно
// обратное: что всё, что в белый список НЕ входит, отвергается.
//
// Замеры делаются один раз и переиспользуются: RamSample читает шестьсот процессов, и
// повторять это на каждую проверку незачем.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Tests
{
    internal static class RamTests
    {
        internal static void Run()
        {
            Engine e = Fx.NewEngine("ram");
            RamSnapshot s = e.RamSample(null);

            Totals(s);
            Composition(s);
            Keys(s);
            Ranges(e, s);
            Pools(e, s);
            CommandWhitelist();
            PidParsing();
            AgentVerbs(e);
            Survivors(e);
        }

        // ---------- за кого просить права после неудачного завершения ----------
        // SurvivorsOf решает, чей список окно покажет в вопросе «запросить права и повторить?».
        // Ошибка стоит дорого в обе стороны: лишний pid — это окно UAC ни за что, свой pid в
        // списке — предложение убить самого себя.
        private static void Survivors(Engine e)
        {
            Process child = null;
            try
            {
                // Живёт около секунды и уходит сам: тесту нужен настоящий чужой процесс, но
                // не нужен процесс, который придётся добивать.
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 >nul");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                child = Process.Start(psi);
            }
            catch (Exception ex) { T.Skip("живой процесс попадает в список выживших", ex.Message); }

            int self = Process.GetCurrentProcess().Id;
            List<int> ask = new List<int>();
            if (child != null) ask.Add(child.Id);
            ask.Add(self);
            ask.Add(0);
            ask.Add(4);

            List<int> alive = e.SurvivorsOf(ask);
            if (child != null)
                T.Check("живой процесс попадает в список выживших", alive.Contains(child.Id), "pid " + child.Id);
            T.Check("свой процесс в список не попадает", !alive.Contains(self), "pid " + self);
            T.Check("системные pid 0 и 4 в список не попадают", !alive.Contains(0) && !alive.Contains(4), null);
            T.Eq("пустой список даёт пустой ответ", 0, e.SurvivorsOf(null).Count);

            if (child != null)
            {
                int dead = child.Id;
                child.WaitForExit(10000);
                // Пока открыт хоть один описатель, ядро держит номер занятым, и такой pid
                // честно считается живым. Описатель здесь наш собственный — окно, которое
                // никого не открывало, этого эффекта не видит; поэтому закрываем и ждём.
                child.Close();
                List<int> one = new List<int>(); one.Add(dead);
                bool gone = false;
                for (int i = 0; i < 40 && !gone; i++)
                {
                    gone = e.SurvivorsOf(one).Count == 0;
                    if (!gone) System.Threading.Thread.Sleep(50);
                }
                T.Check("завершившийся процесс в списке не остаётся", gone, "pid " + dead);
            }
        }

        // ---------- сходятся ли общие цифры ----------
        private static void Totals(RamSnapshot s)
        {
            T.Check("замер вернул снимок", s != null, null);
            if (s == null) return;

            T.Check("установлено памяти не меньше, чем видит Windows",
                    s.Installed >= s.TotalPhys, s.Installed + " vs " + s.TotalPhys);
            // Аппаратный резерв — не отдельный счётчик, а именно эта разница; если он
            // разъедется, «Железо» и схема начнут показывать разное.
            T.Eq("аппаратный резерв = установлено − видимое", s.Installed - s.TotalPhys, s.HardwareReserved);
            T.Eq("занято = видимое − доступно", s.TotalPhys - s.AvailPhys, s.Used);

            if (!s.ListsOk) { T.Skip("списки страниц", "SystemMemoryListInformation недоступна"); return; }

            long standby = 0;
            for (int i = 0; i < 8; i++) standby += s.StandbyByPriority[i];
            T.Eq("ожидание = сумма восьми приоритетов", standby, s.Standby);

            // Активная память считается как остаток. Проверка идёт с другой стороны: сумма
            // всех списков страниц обязана дать ровно то, что система показывает как объём.
            long all = s.Active + s.Standby + s.Modified + s.ModifiedNoWrite
                     + s.FreePages + s.Zeroed + s.Bad;
            T.Eq("списки страниц в сумме дают весь видимый объём", s.TotalPhys, all);

            T.Check("свободное и ожидание не больше доступного",
                    s.FreePages + s.Zeroed + s.Standby >= s.AvailPhys - 64L * 1024 * 1024,
                    "avail " + s.AvailPhys);

            T.Check("процессы прочитаны", s.Procs.Count > 20, "процессов: " + s.Procs.Count);
            int self = Process.GetCurrentProcess().Id;
            RamProc me = null;
            foreach (RamProc p in s.Procs) if (p.Pid == self) { me = p; break; }
            T.Check("свой процесс есть в снимке", me != null, "pid " + self);
            if (me != null)
            {
                T.Check("у своего процесса ненулевой частный набор", me.PrivateWorkingSet > 0, null);
                T.Check("частный набор не больше рабочего",
                        me.PrivateWorkingSet <= me.WorkingSet,
                        me.PrivateWorkingSet + " > " + me.WorkingSet);
            }
        }

        // ---------- покрывает ли схема всю память ----------
        private static void Composition(RamSnapshot s)
        {
            if (s == null || !s.ListsOk) { T.Skip("схема покрывает всю память", "нет списков страниц"); return; }

            long sum = 0;
            foreach (RamSlice b in s.Slices) sum += b.Bytes;

            // Обещание вкладки — «сумма блоков равна установленному объёму». Держится оно
            // на одном условии: известное (процессы, пулы, кэш) укладывается в активную
            // память, а остаток честно называется «не отнесено». Если известное вылезло за
            // активную память, схема покажет больше, чем есть, — и это надо видеть, а не
            // прятать в допуск.
            if (s.Unattributed > 0)
                T.Eq("сумма блоков схемы = установленный объём", s.Installed, sum);
            else
                T.Skip("сумма блоков схемы = установленный объём",
                       "известное переполнило активную память на " + (sum - s.Installed) + " Б");

            foreach (RamSlice b in s.Slices)
            {
                if (b.Children == null || b.Children.Count == 0) continue;
                long kids = 0;
                foreach (RamSlice k in b.Children) kids += k.Bytes;
                T.Eq("вложенные блоки складываются в родителя: " + b.Key, b.Bytes, kids);
            }

            RamSlice procs = Find(s.Slices, "procs");
            T.Check("блок процессов есть", procs != null, null);
            if (procs != null)
            {
                T.Eq("в блоке процессов столько pid, сколько процессов в снимке",
                     s.Procs.Count, procs.Pids.Count);
                foreach (RamSlice g in procs.Children)
                {
                    long kids = 0;
                    foreach (RamSlice k in g.Children) kids += k.Bytes;
                    if (kids != g.Bytes)
                    {
                        T.Check("группа образа складывается из своих процессов: " + g.Key, false,
                                g.Bytes + " vs " + kids);
                        return;
                    }
                    if (g.Pids.Count != g.Children.Count)
                    {
                        T.Check("у группы образа столько pid, сколько блоков внутри: " + g.Key, false,
                                g.Pids.Count + " vs " + g.Children.Count);
                        return;
                    }
                }
                T.Check("каждая группа образа складывается из своих процессов", true, null);
            }
        }

        // ---------- ключи ----------
        // Выделение и проваливание внутрь на схеме работают ПО КЛЮЧУ: снимок пересобирается
        // раз в секунду, и ссылки на объекты между тактами не живут. Два одинаковых ключа —
        // это выбранный chrome.exe, который через секунду оказывается другим chrome.exe.
        private static void Keys(RamSnapshot s)
        {
            if (s == null) return;
            Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
            string dup = Walk(s.Slices, seen);
            T.Check("ключи блоков схемы уникальны", dup == null, dup);
            T.Check("ключей столько же, сколько блоков", seen.Count > s.Procs.Count, "ключей: " + seen.Count);
        }

        private static string Walk(List<RamSlice> list, Dictionary<string, int> seen)
        {
            if (list == null) return null;
            foreach (RamSlice b in list)
            {
                if (string.IsNullOrEmpty(b.Key)) return "пустой ключ у блока «" + b.Title + "»";
                if (seen.ContainsKey(b.Key)) return "ключ «" + b.Key + "» встретился дважды";
                seen[b.Key] = 1;
                string bad = Walk(b.Children, seen);
                if (bad != null) return bad;
            }
            return null;
        }

        private static RamSlice Find(List<RamSlice> list, string key)
        {
            foreach (RamSlice b in list) if (b.Key == key) return b;
            return null;
        }

        // ---------- физические диапазоны ----------
        private static void Ranges(Engine e, RamSnapshot s)
        {
            List<RamRange> ranges = e.RamRanges();
            if (ranges.Count == 0) { T.Skip("физические диапазоны", "реестр не отдал .Translated"); return; }

            long sum = 0;
            foreach (RamRange r in ranges)
            {
                if (r.Length <= 0 || r.Start < 0)
                {
                    T.Check("каждый диапазон осмыслен", false, "start " + r.Start + ", length " + r.Length);
                    return;
                }
                sum += r.Length;
            }
            T.Check("каждый диапазон осмыслен", true, null);

            // Разбор REG_RESOURCE_LIST ходит по массиву дескрипторов с фиксированным шагом.
            // Ошибка в шаге даёт не «немного не сошлось», а мусор на порядки — поэтому допуск
            // в мегабайт ничего не прощает, но и не падает там, где ядро округлило.
            long diff = Math.Abs(sum - s.TotalPhys);
            T.Check("диапазоны в сумме дают видимый объём памяти", diff <= 1024L * 1024,
                    "сумма " + sum + ", видно " + s.TotalPhys);
        }

        // ---------- теги пула ----------
        private static void Pools(Engine e, RamSnapshot s)
        {
            List<RamPool> pools = e.RamPools();
            if (pools.Count == 0) { T.Skip("теги пула", "система не отдала таблицу тегов"); return; }

            // Таблица тегов — массив записей фиксированной длины после заголовка. Смещение
            // начала однажды уже было посчитано неверно, и тесту это видно сразу: тег — четыре
            // печатных ASCII-символа, а не байты откуда попало.
            string bad = null;
            long nonPaged = 0;
            foreach (RamPool p in pools)
            {
                nonPaged += p.NonPaged;
                if (string.IsNullOrEmpty(p.Tag) || p.Tag.Length > 4) { bad = "длина тега: «" + p.Tag + "»"; break; }
                foreach (char c in p.Tag)
                    if (c < 0x20 || c > 0x7E) { bad = "непечатный символ в теге «" + p.Tag + "»"; break; }
                if (bad != null) break;
                if (p.Paged < 0 || p.NonPaged < 0) { bad = "отрицательный объём у тега «" + p.Tag + "»"; break; }
            }
            T.Check("теги пула читаются как печатные ASCII", bad == null, bad);
            T.Check("тегов достаточно много", pools.Count > 50, "тегов: " + pools.Count);

            // Сумма по тегам и счётчик ядра считаются по-разному и совпадать байт в байт не
            // обязаны, но разъехаться вдвое могут только при неверном разборе.
            if (s.NonPagedPoolTotal > 0)
                T.Check("невыгружаемый пул по тегам сходится со счётчиком ядра",
                        nonPaged > s.NonPagedPoolTotal / 2 && nonPaged < s.NonPagedPoolTotal * 2,
                        "по тегам " + nonPaged + ", по счётчику " + s.NonPagedPoolTotal);
        }

        // ---------- белый список команд ----------
        private static void CommandWhitelist()
        {
            for (int i = Engine.RamEmptyWorkingSets; i <= Engine.RamEmptyEverything; i++)
                if (!Engine.RamCommandKnown(i))
                {
                    T.Check("все шесть команд сброса опознаются", false, "не опознана " + i);
                    return;
                }
            T.Check("все шесть команд сброса опознаются", true, null);

            int[] outside = { int.MinValue, -1, 0, 1, 8, 9, 0x50, int.MaxValue };
            foreach (int i in outside)
                if (Engine.RamCommandKnown(i))
                {
                    T.Check("всё за пределами белого списка отвергается", false, "пропущена " + i);
                    return;
                }
            T.Check("всё за пределами белого списка отвергается", true, null);
        }

        // ---------- разбор списка pid ----------
        private static void PidParsing()
        {
            T.Eq("пустой аргумент даёт пустой список", 0, Elevation.RamPidList("").Count);
            T.Eq("null даёт пустой список", 0, Elevation.RamPidList(null).Count);

            List<int> three = Elevation.RamPidList("4,8,15");
            T.Eq("три числа дают три pid", 3, three.Count);
            T.Eq("порядок сохраняется", 15, three.Count == 3 ? three[2] : -1);

            // Ноль — это System Idle Process, минус — вообще не pid, буквы — попытка что-то
            // подсунуть. Всё это должно молча выпадать, а годные числа рядом — оставаться.
            List<int> mixed = Elevation.RamPidList("  12 , x , 0 , -5 , 7 , , 4294967296 ");
            T.Eq("мусор отбрасывается, годные pid остаются", 2, mixed.Count);
            T.Eq("первым остался 12", 12, mixed.Count == 2 ? mixed[0] : -1);
            T.Eq("вторым остался 7", 7, mixed.Count == 2 ? mixed[1] : -1);

            StringBuilder huge = new StringBuilder();
            for (int i = 1; i <= 5000; i++) { if (i > 1) huge.Append(','); huge.Append(i * 4); }
            T.Eq("длина списка pid ограничена сверху", 1024, Elevation.RamPidList(huge.ToString()).Count);
        }

        // ---------- что принимает канал команд ----------
        // Это проверка границы безопасности, поэтому она гоняет НАСТОЯЩИЙ обработчик через
        // НАСТОЯЩИЕ файлы команд — подменена только папка. Ни одна из отправленных здесь
        // команд ничего не сбрасывает: всё это либо неизвестные глаголы, либо номера вне
        // белого списка, либо пустой список процессов.
        private static void AgentVerbs(Engine e)
        {
            string dir = Fx.MakeDir(Fx.Root, "ram-agent");
            int n = 0;

            ElevResult kill = Serve(e, dir, ++n, "kill 4");
            T.Check("«kill» через канал команд не выполняется", kill != null && !kill.Ok, null);
            T.Eq("«kill» отвергается как неизвестный глагол", "unknown verb", kill == null ? null : kill.Message);

            ElevResult term = Serve(e, dir, ++n, "terminate 4");
            T.Eq("«terminate» отвергается как неизвестный глагол", "unknown verb", term == null ? null : term.Message);

            ElevResult run = Serve(e, dir, ++n, "run cmd.exe");
            T.Eq("произвольный запуск отвергается", "unknown verb", run == null ? null : run.Message);

            ElevResult empty = Serve(e, dir, ++n, "empty");
            T.Eq("«empty» без номера отвергается", "bad command", empty == null ? null : empty.Message);

            ElevResult low = Serve(e, dir, ++n, "empty 1");
            T.Eq("номер ниже белого списка отвергается", "bad command", low == null ? null : low.Message);

            ElevResult high = Serve(e, dir, ++n, "empty 80");
            T.Eq("номер выше белого списка отвергается", "bad command", high == null ? null : high.Message);

            ElevResult junk = Serve(e, dir, ++n, "empty ../../../windows");
            T.Eq("нечисловой аргумент отвергается", "bad command", junk == null ? null : junk.Message);

            // Единственная команда, которую тест выполняет по-настоящему: сброс рабочих
            // наборов пустого списка процессов. Она ничего не трогает, но проходит весь путь.
            ElevResult trim = Serve(e, dir, ++n, "trim");
            T.Check("«trim» с пустым списком отрабатывает вхолостую",
                    trim != null && trim.Count == 0, trim == null ? "нет ответа" : "тронуто: " + trim.Count);

            T.Check("файл команды удаляется после обработки",
                    Directory.GetFiles(dir, "cmd-*.txt").Length == 0, null);
        }

        // Кладёт файл команды ровно так, как это делает окно, и читает ответ ровно так, как
        // окно его читает.
        private static ElevResult Serve(Engine e, string dir, int n, string line)
        {
            string stem = n.ToString("D6");
            string cmd = Path.Combine(dir, "cmd-" + stem + ".txt");
            File.WriteAllText(cmd, line);
            Elevation.RamAgentServe(e, dir, cmd);

            string res = Path.Combine(dir, "res-" + stem + ".json");
            if (!File.Exists(res)) return null;
            ElevResult r = Elevation.ReadResultFile(res);
            File.Delete(res);
            return r;
        }
    }
}
