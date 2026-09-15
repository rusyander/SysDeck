// SysDeck — тесты предохранителя путей.
//
// Что здесь закрепляется: какие цели движок вообще соглашается чистить. Это самая дорогая
// ошибка в приложении, которое удаляет файлы: пропущенный запрет — это чужие данные, лишний —
// всего лишь непочищенная папка. Наблюдаем ровно то же, что видит окно: анализ категории
// выставляет CleanTarget.Guarded, и по нему судим о предохранителе.

using System;
using System.IO;

namespace SysDeck.Tests
{
    internal static class PathTests
    {
        internal static void Run()
        {
            Engine e = Fx.NewEngine("data");
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDrive = Path.GetPathRoot(win);
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string area = Fx.MakeDir(Fx.Root, "paths");

            // ---------- собственная папка данных ----------
            // Там лежат конфиг, история и лог очистки: чистилка, снёсшая их, теряет и настройки
            // пользователя, и запись о том, что сама же удалила.
            T.Check("the app never cleans its own data folder",
                    Fx.Guarded(e, Fx.Tgt(e.DataDir)), e.DataDir);
            T.Check("the app never cleans a subfolder of its own data folder",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(e.DataDir, "browser-backups"))));
            // Сравнение по префиксу без разделителя посчитало бы «своей» и соседнюю папку.
            T.Check("a sibling folder whose name merely starts with the data dir name is not guarded",
                    !Fx.Guarded(e, Fx.Tgt(e.DataDir + "-not-mine")));

            // ---------- корни и системные деревья ----------
            T.Check("the drive root is never a valid target",
                    Fx.Guarded(e, Fx.Tgt(sysDrive)), sysDrive);
            T.Check("the Windows folder itself is never a valid target",
                    Fx.Guarded(e, Fx.Tgt(win)));
            T.Check("a folder under System32 is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(win, "System32\\wpc-tests-nonexistent"))));
            T.Check("the WinSxS backup subtree is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(win, "WinSxS\\Backup"))));
            T.Check("the DriverStore subtree is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(win, "System32\\DriverStore\\FileRepository"))));
            T.Check("the event log store is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(win, "System32\\winevt\\Logs"))));
            T.Check("System Volume Information is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(sysDrive, "System Volume Information"))));
            T.Check("a per-user Recycle Bin folder is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(sysDrive, "$Recycle.Bin\\S-1-5-21-0-0-0-1001"))));

            // Единственные разрешённые исключения из запретных деревьев — temp и кэш IE
            // служебного профиля: туда пишут установщики, руками их никто не чистит.
            string sysProfile = Path.Combine(win, "System32\\config\\systemprofile\\AppData\\Local");
            T.Check("the service profile temp folder is on the never-touch allow list",
                    !Fx.Guarded(e, Fx.Tgt(Path.Combine(sysProfile, "Temp"))));
            T.Check("a folder next to it in the same profile stays guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(sysProfile, "wpc-tests-nonexistent"))));

            // ---------- данные пользователя ----------
            T.Check("the user profile root is guarded",
                    Fx.Guarded(e, Fx.Tgt(up)));
            T.Check("the Documents folder is guarded",
                    Fx.Guarded(e, Fx.Tgt(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments))));
            T.Check("%LOCALAPPDATA% as a whole is guarded",
                    Fx.Guarded(e, Fx.Tgt(lad)));

            // Послабление для встроенных правил вида «IconCache.db в %LOCALAPPDATA%»: маска,
            // без рекурсии — файлы по имени, папка не трогается.
            CleanTarget shallow = Fx.Tgt(lad);
            shallow.Mask = "IconCache.db";
            shallow.Recurse = false;
            T.Check("%LOCALAPPDATA% with a shallow file mask is allowed",
                    !Fx.Guarded(e, shallow));

            CleanTarget deepMask = Fx.Tgt(lad);
            deepMask.Mask = "IconCache.db";
            deepMask.Recurse = true;
            T.Check("%LOCALAPPDATA% with a recursive mask is still guarded",
                    Fx.Guarded(e, deepMask));

            // Сохранения игр не мусор ни по какому правилу — сегмент запрещён где угодно,
            // а не только в известных местах.
            T.Check("a Saved Games folder is guarded wherever it appears",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(Path.Combine(area, "proj\\Saved Games\\slot1")))));
            T.Check("a Steam userdata folder is guarded wherever it appears",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(Path.Combine(area, "Steam\\userdata\\1\\remote")))));
            // База распознавания игр NVIDIA App: её удаление 06.09.2026 сломало NVIDIA App.
            T.Check("the NVIDIA ApplicationOntology database is guarded wherever it appears",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(Path.Combine(area, "NvBackend\\ApplicationOntology")))));

            // ---------- общесистемные деревья приложений ----------
            // %ProgramData%\<вендор>\cache по имени кэш, а по сути склад установщика: из него
            // программа доставляет и чинит свои компоненты. 10.09.2026 правило с таким именем
            // снесло 698,7 МБ архивов .depot, и Logitech G HUB перестал ставить свои драйверы.
            // Здесь проверяется не имя вендора, а сам порядок: в %ProgramData% и Program Files
            // отдаётся только заведомо одноразовое, остальное остаётся у любого приложения.
            string pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string vendor = Path.Combine(pd, "SysDeck-selftest");
            string payload = Path.Combine(vendor, "cache");
            bool inProgramData = false;
            try { Fx.MakeDir(payload); inProgramData = true; }
            catch { }                       // %ProgramData% закрыт на запись — проверка пропускается
            if (inProgramData)
            {
                try
                {
                    Fx.MakeFile(Path.Combine(payload, "core.depot"), 4096);
                    Fx.MakeFile(Path.Combine(payload, "logs\\install.log"), 1024);

                    T.Check("an installer payload under %ProgramData% is not junk, its log next to it is",
                            Fx.Analyze(e, Fx.Tgt(payload)).Size == 1024, Fx.Analyze(e, Fx.Tgt(payload)).Size.ToString());

                    // Тот же обход без предохранителя — то, как правило вело себя до 14.09.2026:
                    // пометка Disposable означает «автор правила проверил, что это восстановится».
                    CleanTarget verified = Fx.Tgt(payload);
                    verified.Disposable = true;
                    T.Check("a target marked as regenerated by the app may still take everything there",
                            Fx.Analyze(e, verified).Size == 4096 + 1024);

                    // Program Files — то же дерево: правило не должно уносить оттуда исполняемое.
                    CleanTarget exe = Fx.Tgt(payload);
                    Fx.MakeFile(Path.Combine(payload, "setup.exe"), 2048);
                    T.Check("an executable under %ProgramData% is never junk either",
                            Fx.Analyze(e, exe).Size == 1024);

                    // Настоящее удаление тем же путём, каким его делает окно: анализ и очистка ходят
                    // одним обходчиком, но папки убирает только очистка — её и проверяем на деле.
                    Fx.MachineRoot = vendor;
                    Fx.Clean(e, Fx.Cat(Fx.Tgt(payload)));
                    T.Check("a real clean in a machine tree takes the log and nothing else",
                            !File.Exists(Path.Combine(payload, "logs\\install.log"))
                            && File.Exists(Path.Combine(payload, "core.depot"))
                            && File.Exists(Path.Combine(payload, "setup.exe")));
                    // Опустевшая папка — часть дистрибутива: по ней приложение находит свои файлы.
                    T.Check("and it leaves the emptied folders of the payload in place",
                            Directory.Exists(Path.Combine(payload, "logs")) && Directory.Exists(payload));
                }
                finally
                {
                    Fx.MachineRoot = "";
                    try { Directory.Delete(vendor, true); }
                    catch { }
                }
            }

            // Склад установщика закрыт и по имени папки — на любой глубине и у любого вендора.
            T.Check("a Package Cache folder is guarded wherever it appears",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(Path.Combine(area, "vendor\\Package Cache")))));
            T.Check("a driver Installer2 folder is guarded wherever it appears",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(Path.Combine(area, "vendor\\Installer2")))));
            T.Check("the MSI patch baseline cache is guarded",
                    Fx.Guarded(e, Fx.Tgt(Path.Combine(win, "Installer\\$PatchCache$"))));

            // ---------- список исключений пользователя ----------
            string excluded = Fx.MakeDir(area, "excluded");
            string neighbour = Fx.MakeDir(area, "excluded-neighbour");
            e.Config.CleanExclude.Add(excluded);
            T.Check("a folder on the exclusion list is guarded",
                    Fx.Guarded(e, Fx.Tgt(excluded)));
            T.Check("a folder inside an excluded folder is guarded",
                    Fx.Guarded(e, Fx.Tgt(Fx.MakeDir(excluded, "inner"))));
            T.Check("a folder whose name merely starts with an excluded path is not guarded",
                    !Fx.Guarded(e, Fx.Tgt(neighbour)));

            // Путь, приведённый к каноническому виду: без этого точка соединения была бы
            // способом обойти исключение — тот же каталог под другим именем.
            string alias = Path.Combine(area, "excluded-alias");
            if (Fx.Junction(alias, excluded))
                T.Check("a junction pointing at an excluded folder is guarded too",
                        Fx.Guarded(e, Fx.Tgt(alias)));
            else
                T.Skip("a junction pointing at an excluded folder is guarded too", "mklink /J failed");
            e.Config.CleanExclude.Clear();

            // ---------- цели из внешнего файла правил ----------
            // Файл winapp2.ini лежит там, куда пишет любой процесс пользователя, а чистим мы
            // от администратора: правилу из файла позволено заметно меньше, чем встроенному.
            RulePair(e, "a target inside Program Files",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                                  "wpc-tests-nonexistent"));
            RulePair(e, "a target inside the Windows folder outside the junk list",
                     Path.Combine(win, "ServiceProfiles\\wpc-tests-nonexistent"));

            string usersRoot = Path.GetDirectoryName(up.TrimEnd('\\'));
            RulePair(e, "a target inside another user's profile",
                     Path.Combine(usersRoot, "wpc-tests-other-user\\AppData\\Local\\Temp"));

            CleanTarget winTemp = Fx.Tgt(Path.Combine(win, "Temp\\wpc-tests-nonexistent"));
            winTemp.FromRules = true;
            T.Check("a rule-sourced target inside the Windows temp folder is allowed",
                    !Fx.Guarded(e, winTemp));

            // Послабление «корень + маска» действует только для встроенных правил.
            CleanTarget ruleMask = Fx.Tgt(lad);
            ruleMask.Mask = "IconCache.db";
            ruleMask.Recurse = false;
            ruleMask.FromRules = true;
            T.Check("a rule-sourced target does not get the root-plus-mask exemption",
                    Fx.Guarded(e, ruleMask));
        }

        // Один и тот же путь: встроенному правилу можно, правилу из файла — нет.
        private static void RulePair(Engine e, string what, string path)
        {
            T.Check(what + " is allowed for a built-in rule", !Fx.Guarded(e, Fx.Tgt(path)), path);
            CleanTarget t = Fx.Tgt(path);
            t.FromRules = true;
            T.Check(what + " is rejected when it comes from a rules file", Fx.Guarded(e, t), path);
        }
    }
}
