// Windows Process Cleaner — область «elevation»: кто чем владеет, как задание делится
// между окном и помощником, и почему приложение не может съесть само себя.
//
// Прогон идёт БЕЗ прав администратора и не показывает ни одного окна UAC: помощник
// запускается напрямую ключом --elevated-job, а всё остальное здесь — чистые функции.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Tests
{
    internal static class ElevationTests
    {
        internal static void Run()
        {
            DataDirContract();
            Ownership();
            Classification();
            SplitInvariant();
            Whitelist();
            OwnFolderGuard();
            HelperProtocol();
        }

        // ---------- где лежат данные ----------
        private static void DataDirContract()
        {
            Engine e = Fx.NewEngine("elev-data");
            T.Check("WPC_DATA_DIR решает, где лежат данные", Fx.IsUnder(e.DataDir, Fx.Root), e.DataDir);
            T.Eq("метка портативной сборки называется portable.marker", "portable.marker", Engine.PortableMarker);

            // Метки рядом с exe нет — значит сборка обычная, и данные ищутся в %APPDATA%.
            string exeDir = Engine.ExeDir();
            bool marker = exeDir != null && File.Exists(Path.Combine(exeDir, Engine.PortableMarker));
            T.Eq("портативный режим включает ровно наличие метки", marker, Engine.IsPortable);
            if (!marker)
                T.Check("без метки портативной папки данных нет", Engine.PortableDataDir() == null, null);
        }

        // ---------- чьи это файлы ----------
        private static void Ownership()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

            T.Check("папка профиля — своя", Elevation.PathInUserProfile(profile), null);
            T.Check("%LOCALAPPDATA% — своя", Elevation.PathInUserProfile(Path.Combine(lad, "npm-cache")), null);
            T.Check("C:\\Windows\\Temp — не своя", !Elevation.PathInUserProfile(Path.Combine(win, "Temp")), null);
            T.Check("ProgramData — не своя", !Elevation.PathInUserProfile(common), null);
            T.Check("остатки в корне диска — не свои", !Elevation.PathInUserProfile("C:\\$WinREAgent"), null);

            // Соседний профиль начинается с тех же букв, но принадлежит другому человеку:
            // проверка обязана сравнивать сегменты пути, а не подстроку.
            string sibling = profile.TrimEnd('\\') + "-other\\AppData";
            T.Check("профиль с похожим именем — не свой", !Elevation.PathInUserProfile(sibling), sibling);
            T.Check("пустой путь не считается системным", Elevation.PathInUserProfile(""), null);
        }

        // ---------- каким категориям нужны права ----------
        private static void Classification()
        {
            Engine e = Fx.NewEngine("elev-class");
            List<CleanCategory> cats = e.BuildCleanCategories();
            T.Check("каталог категорий не пуст", cats.Count > 0, cats.Count.ToString());

            Dictionary<string, bool> need = new Dictionary<string, bool>();
            foreach (CleanCategory c in cats) need[c.Id] = Elevation.CategoryNeedsAdmin(c);

            if (need.ContainsKey("browser"))
                T.Check("кэш браузеров чистится без прав", !need["browser"], null);
            if (need.ContainsKey("dev"))
                T.Check("кэши разработчика чистятся без прав", !need["dev"], null);
            if (need.ContainsKey("sys"))
                T.Check("системный мусор требует прав", need["sys"], null);
            if (need.ContainsKey("drivers"))
                T.Check("остатки обновлений требуют прав", need["drivers"], null);

            // Категории-действия управляют системой целиком, независимо от путей.
            foreach (CleanCategory c in cats)
                if (c.Kind == "driverstore" || c.Kind == "winsxs")
                    T.Check("категория-действие «" + c.Id + "» требует прав", Elevation.CategoryNeedsAdmin(c), c.Kind);

            // Собранная руками категория из одной своей папки прав требовать не может.
            CleanCategory mine = Fx.Cat(Fx.Tgt(Fx.MakeDir(Fx.Root, "elev-own")));
            T.Check("категория из одной своей папки прав не требует", !Elevation.CategoryNeedsAdmin(mine), null);
        }

        // ---------- деление категории надвое ----------
        private static void SplitInvariant()
        {
            Engine e = Fx.NewEngine("elev-split");
            int withFolders = 0, mixed = 0;
            foreach (CleanCategory c in e.BuildCleanCategories())
            {
                if (!string.IsNullOrEmpty(c.Kind)) continue;
                if (!Elevation.CategoryNeedsAdmin(c)) continue;
                withFolders++;

                List<string> keys = new List<string>();
                CleanCategory u = Elevation.UserPartOf(c, keys);
                int userTargets = u == null ? 0 : u.Targets.Count;

                // Ни одна цель не потеряна и ни одна не посчитана дважды.
                T.Eq("«" + c.Id + "»: половинки в сумме дают исходную категорию",
                     c.Targets.Count, userTargets + keys.Count);
                T.Check("«" + c.Id + "»: ради чего-то права всё же запрашиваются", keys.Count > 0, null);

                if (u != null)
                {
                    bool leak = false;
                    foreach (CleanTarget t in u.Targets)
                        if (t.Enabled && !t.Guarded && !Elevation.PathInUserProfile(t.Path)) leak = true;
                    T.Check("«" + c.Id + "»: в своей половине не осталось системных путей", !leak, null);
                    if (c.RecycleBin)
                        T.Check("«" + c.Id + "»: Корзина остаётся за окном, а не за помощником", u.RecycleBin, null);
                }
                if (userTargets > 0 && keys.Count > 0) mixed++;
            }
            T.Check("смешанные категории вообще существуют — иначе делить было бы нечего", mixed > 0, mixed.ToString());
            Console.WriteLine("     (категорий с папками, требующих прав: " + withFolders + ", из них смешанных: " + mixed + ")");
        }

        // ---------- перечень целей в задании умеет только сужать ----------
        private static void Whitelist()
        {
            Engine e = Fx.NewEngine("elev-white");

            CleanCategory probe = null;
            List<string> adminKeys = new List<string>();
            foreach (CleanCategory c in e.BuildCleanCategories())
            {
                if (!string.IsNullOrEmpty(c.Kind) || !Elevation.CategoryNeedsAdmin(c)) continue;
                List<string> k = new List<string>();
                CleanCategory u = Elevation.UserPartOf(c, k);
                if (u != null && u.Targets.Count > 0 && k.Count > 0) { probe = c; adminKeys = k; break; }
            }
            if (probe == null)
            {
                T.Skip("перечень целей сужает категорию", "на этой машине нет смешанной категории");
                return;
            }

            T.Eq("голый id означает категорию целиком", probe.Targets.Count, Targets(Pick(e, probe.Id)));

            string one = probe.Id + "\t" + adminKeys[0];
            List<CleanCategory> narrowed = Pick(e, one);
            T.Eq("перечень из одной цели оставляет одну цель", 1, Targets(narrowed));
            if (narrowed.Count == 1 && narrowed[0].Targets.Count == 1)
                T.Eq("остаётся именно названная цель",
                     adminKeys[0], Engine.TargetKey(narrowed[0], narrowed[0].Targets[0]));
            T.Check("у суженной категории Корзины больше нет",
                    narrowed.Count == 1 && !narrowed[0].RecycleBin, null);

            // Главное свойство: файл задания не может назвать путь, которого нет в каталоге.
            T.Eq("выдуманный ключ не совпадает ни с чем", 0,
                 Pick(e, probe.Id + "\tвыдуманный|C:\\Windows|").Count);
            T.Eq("выдуманная категория не совпадает ни с чем", 0, Pick(e, "no-such-category").Count);
            T.Eq("пустое задание не чистит ничего", 0, Pick(e).Count);

            StringBuilder sb = new StringBuilder(probe.Id);
            foreach (string k in adminKeys) sb.Append('\t').Append(k);
            T.Eq("названы все системные цели — вернулись все они", adminKeys.Count, Targets(Pick(e, sb.ToString())));
        }

        private static List<CleanCategory> Pick(Engine e, params string[] items)
        {
            ElevJob j = new ElevJob();
            j.Kind = "clean";
            j.Items = items;
            return Elevation.PickCategories(e, j);
        }

        private static int Targets(List<CleanCategory> cats)
        {
            int n = 0;
            foreach (CleanCategory c in cats) n += c.Targets.Count;
            return n;
        }

        // ---------- приложение не съедает само себя ----------
        // 10.09.2026: тихий прогон с папкой данных под %LOCALAPPDATA%\Temp удалил собственные
        // config.json и журнал. Предохранитель на свой каталог стоял только на КОРНЕ цели, а
        // сюда обход приходил сверху, из чистимой папки. Для портативной сборки, распакованной
        // в «Загрузки» или во временную папку, это не экзотика, а обычное расположение.
        private static void OwnFolderGuard()
        {
            string tree = Fx.MakeDir(Fx.Root, "elev-self");
            Engine e = Fx.NewEngine("elev-self\\data");
            string mine = e.DataDir;
            string keep = Fx.MakeFile(Path.Combine(mine, "history.json"), 400);
            string junk = Fx.MakeFile(Path.Combine(tree, "junk.tmp"), 4000);
            string deepJunk = Fx.MakeFile(Path.Combine(Fx.MakeDir(tree, "sub"), "more.tmp"), 4000);

            Fx.Clean(e, Fx.Cat(Fx.Tgt(tree)));

            T.Check("обычный мусор рядом удалён", !Native.PathExists(junk), junk);
            T.Check("мусор в подпапке удалён", !Native.PathExists(deepJunk), deepJunk);
            T.Check("свой каталог данных уцелел", Native.PathExists(mine), mine);
            T.Check("файлы в своём каталоге уцелели", Native.PathExists(keep), keep);

            // Тот же предохранитель закрывает папку рядом с exe у портативной сборки.
            string around = Fx.MakeDir(Fx.Root, "elev-portable");
            string app = Fx.MakeDir(around, "app");
            string appFile = Fx.MakeFile(Path.Combine(app, "portable.marker"), 0);
            string aroundJunk = Fx.MakeFile(Path.Combine(around, "junk.tmp"), 4000);
            Engine.GuardOwnFolder(app);
            Fx.Clean(e, Fx.Cat(Fx.Tgt(around)));
            T.Check("объявленная своей папка уцелела", Native.PathExists(appFile), appFile);
            T.Check("мусор вокруг неё удалён", !Native.PathExists(aroundJunk), aroundJunk);
        }

        // ---------- разговор с помощником ----------
        // Помощник запускается НАПРЯМУЮ, без runas: так проверяется весь протокол, кроме
        // самого окна UAC, и прогон остаётся без единого запроса прав.
        private static void HelperProtocol()
        {
            string exe = Environment.GetEnvironmentVariable("WPC_TEST_APP");
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                T.Skip("помощник отвечает на задание", "WPC_TEST_APP не указывает на собранное приложение");
                return;
            }

            string childData = Fx.MakeDir(Fx.Root, "elev-child");
            string neighbour = Fx.MakeDir(Fx.Root, "elev-neighbour");
            string witness = Fx.MakeFile(Path.Combine(neighbour, "witness.txt"), 128);

            ElevResult kill = Call(exe, "kill", null, new string[0], childData);
            T.Check("помощник отвечает на задание без работы", kill != null && kill.Ok,
                    kill == null ? "нет ответа" : kill.Message);
            T.Check("помощник взял папку данных из задания", Directory.Exists(childData), childData);

            ElevResult bad = Call(exe, "не-такое-задание", null, null, childData);
            T.Check("неизвестный вид задания отклонён, а не уронил помощника",
                    bad != null && !bad.Ok, bad == null ? "нет ответа" : bad.Message);

            ElevResult standby = Call(exe, "standby", null, null, childData);
            T.Check("отказ помощника объяснён словами",
                    standby != null && !string.IsNullOrEmpty(standby.Message),
                    standby == null ? "нет ответа" : standby.Message);

            ElevResult ghost = Call(exe, "clean", "", new string[] { "выдуманная-категория" }, childData);
            T.Check("выдуманная категория в задании не чистит ничего",
                    ghost != null && ghost.Ok && ghost.Count == 0 && ghost.Freed == 0,
                    ghost == null ? "нет ответа" : ghost.Count + "/" + ghost.Freed);

            T.Check("помощник не тронул соседнюю папку прогона", Native.PathExists(witness), witness);
        }

        // Формат задания собирается ЗДЕСЬ вручную, а не производственным писателем: если
        // формат поедет, тест это заметит, а не повторит ошибку вслед за кодом.
        private static ElevResult Call(string exe, string kind, string arg, string[] items, string dataDir)
        {
            string dir = Fx.MakeDir(Fx.Root, "job-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"Arg\":").Append(arg == null ? "null" : "\"" + arg + "\"");
            sb.Append(",\"DataDir\":\"").Append(dataDir.Replace("\\", "\\\\")).Append("\"");
            sb.Append(",\"En\":false,\"Flag\":false,\"Items\":");
            if (items == null) sb.Append("null");
            else
            {
                sb.Append('[');
                for (int i = 0; i < items.Length; i++) { if (i > 0) sb.Append(','); sb.Append('"').Append(items[i]).Append('"'); }
                sb.Append(']');
            }
            sb.Append(",\"Kind\":\"").Append(kind).Append("\",\"Number\":0}");
            File.WriteAllText(Path.Combine(dir, "job.json"), sb.ToString(), new UTF8Encoding(false));

            ProcessStartInfo psi = new ProcessStartInfo(exe, Elevation.JobSwitch + " \"" + dir + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi))
            {
                if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return null; }
            }

            string res = Path.Combine(dir, "result.json");
            if (!File.Exists(res)) return null;
            string json = File.ReadAllText(res);
            ElevResult r = new ElevResult();
            r.Ok = json.Contains("\"Ok\":true");
            r.Count = Num(json, "\"Count\":");
            r.Freed = Num(json, "\"Freed\":");
            r.Message = Str(json, "\"Message\":\"");
            return r;
        }

        private static int Num(string json, string key)
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return -1;
            i += key.Length;
            int j = i;
            while (j < json.Length && (char.IsDigit(json[j]) || json[j] == '-')) j++;
            int v;
            return int.TryParse(json.Substring(i, j - i), out v) ? v : -1;
        }

        private static string Str(string json, string key)
        {
            int i = json.IndexOf(key, StringComparison.Ordinal);
            if (i < 0) return null;
            i += key.Length;
            int j = json.IndexOf('"', i);
            return j < 0 ? null : json.Substring(i, j - i);
        }
    }
}
