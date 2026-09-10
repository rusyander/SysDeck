// Windows Process Cleaner — тесты правил winapp2.ini.
//
// Файл правил кладут в папку, куда пишет любой процесс от имени пользователя, а чистим мы от
// администратора. Поэтому здесь проверяется не только разбор (что читается, а что сознательно
// пропускается), но и то, что цель из файла проходит более строгий предохранитель, чем
// встроенная. Фикстурный winapp2.ini кладётся в подменённую папку данных — настоящий файл
// пользователя и защищённая копия в %ProgramData% не трогаются.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Tests
{
    internal static class RuleTests
    {
        private const int Fresh = 13;

        internal static void Run()
        {
            string dataDir = Fx.MakeDir(Fx.Root, "rules");
            string tree = Fx.MakeDir(Fx.Root, "rules-tree");
            // Все папки создаются заранее — включая те, которые правило НЕ должно дать почистить:
            // иначе «цели нет» доказывало бы лишь отсутствие папки, а не работу пропуска секции.
            string junk = Fx.MakeDir(tree, "junk");
            string deep = Fx.MakeDir(tree, "deep");
            string multi = Fx.MakeDir(tree, "multi");
            string reg = Fx.MakeDir(tree, "reg");
            string never = Fx.MakeDir(tree, "never");
            string never2 = Fx.MakeDir(tree, "never2");
            string selfgone = Fx.MakeDir(tree, "selfgone");
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string escape = Path.Combine(win, "ServiceProfiles");

            string ini = Path.Combine(dataDir, "winapp2.ini");
            File.WriteAllText(ini, Ini(tree), Encoding.UTF8);

            Engine e = Fx.NewEngine("rules");
            e.Config.CleanSkipRecentMinutes = Fresh;
            T.Eq("the engine reads the rules file from its own data folder", ini, e.Winapp2Path);

            List<CleanCategory> cats = e.BuildCleanCategories();
            CleanCategory c = null;
            foreach (CleanCategory x in cats)
                if (string.Equals(x.Id, "winapp2:WPC Tests", StringComparison.Ordinal)) c = x;
            if (c == null)
            {
                T.Check("rules from a file become their own category", false,
                        "no winapp2:WPC Tests category among " + cats.Count);
                return;
            }
            T.Check("rules from a file become their own category", true);

            // ---------- что сознательно пропускается ----------
            T.Check("a section carrying an ExcludeKey is skipped whole", Find(c, never) == null, never);
            T.Check("a section carrying a Warning is skipped whole", Find(c, never2) == null, never2);
            T.Check("a RegKey line is ignored but the file rule of that section still applies",
                    Find(c, reg) != null, reg);

            // ---------- как разбираются флаги и маски ----------
            CleanTarget tJunk = Find(c, junk, "*.log");
            CleanTarget tDeep = Find(c, deep);
            CleanTarget tSelf = Find(c, selfgone);
            T.Check("a rule mask becomes the target mask",
                    tJunk != null && tJunk.Mask == "*.log");
            T.Check("a rule without RECURSE does not descend into subfolders",
                    tJunk != null && !tJunk.Recurse);
            T.Check("a rule mask of every file becomes no mask at all",
                    tDeep != null && tDeep.Mask == null);
            T.Check("a rule with RECURSE descends into subfolders",
                    tDeep != null && tDeep.Recurse);
            T.Check("a rule without REMOVESELF keeps the folder itself",
                    tJunk != null && tJunk.ContentsOnly);
            T.Check("REMOVESELF lets the rule remove the folder itself",
                    tSelf != null && !tSelf.ContentsOnly && tSelf.Recurse);

            int masks = 0;
            foreach (CleanTarget t in c.Targets)
                if (string.Equals(t.Path.TrimEnd('\\'), multi.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    masks++;
            T.Eq("a rule listing several masks becomes one target per mask", 2, masks);

            // ---------- общие свойства целей из файла ----------
            bool allFromRules = true;
            bool allFresh = true;
            foreach (CleanTarget t in c.Targets)
            {
                if (!t.FromRules) allFromRules = false;
                if (t.MinAgeMinutes != Fresh) allFresh = false;
            }
            T.Check("every target from a rules file is marked as rule-sourced", allFromRules);
            T.Check("every target from a rules file inherits the fresh-files window from the settings",
                    allFresh, "expected " + Fresh);

            T.Eq("the rule counter counts only the sections that produced targets", 4, e.Winapp2RuleCount);
            T.Check("a rules file sitting in a user-writable folder is not called protected",
                    !e.Winapp2Protected);

            // ---------- строгий предохранитель ----------
            // %WinDir%\ServiceProfiles не входит в короткий список заведомо мусорных подпапок
            // Windows, разрешённых правилам из файла.
            CleanTarget tEscape = Find(c, escape);
            if (tEscape == null)
            {
                T.Check("a rule reaching into a protected Windows folder is cut off by the guard", false,
                        "the escape target never made it into the category");
            }
            else
            {
                e.AnalyzeCategory(c);
                T.Check("a rule reaching into a protected Windows folder is cut off by the guard",
                        tEscape.Guarded, escape);
                T.Check("targets from the rules file that stay inside their own tree are not cut off",
                        tDeep != null && !tDeep.Guarded);
            }
        }

        private static string Ini(string tree)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("; fixture rules for the test suite");
            sb.AppendLine("[WpcTestsBasic*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("FileKey1=" + Path.Combine(tree, "junk") + "|*.log");
            sb.AppendLine("FileKey2=" + Path.Combine(tree, "deep") + "|*.*|RECURSE");
            sb.AppendLine("FileKey3=" + Path.Combine(tree, "multi") + "|*.log;*.tmp");
            sb.AppendLine();
            sb.AppendLine("[WpcTestsExcluded*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("FileKey1=" + Path.Combine(tree, "never") + "|*.*|RECURSE");
            sb.AppendLine("ExcludeKey1=FILE|" + Path.Combine(tree, "never") + "|keep.txt");
            sb.AppendLine();
            sb.AppendLine("[WpcTestsWarned*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("Warning=This rule removes data that cannot be restored");
            sb.AppendLine("FileKey1=" + Path.Combine(tree, "never2") + "|*.*|RECURSE");
            sb.AppendLine();
            sb.AppendLine("[WpcTestsRegistry*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("RegKey1=HKCU\\Software\\WpcTestsNoSuchKey|Nothing");
            sb.AppendLine("FileKey1=" + Path.Combine(tree, "reg") + "|*.log");
            sb.AppendLine();
            sb.AppendLine("[WpcTestsRemoveSelf*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("FileKey1=" + Path.Combine(tree, "selfgone") + "|*.*|REMOVESELF");
            sb.AppendLine();
            sb.AppendLine("[WpcTestsEscape*]");
            sb.AppendLine("Section=WPC Tests");
            sb.AppendLine("FileKey1=%WinDir%\\ServiceProfiles|*.*|RECURSE");
            return sb.ToString();
        }

        private static CleanTarget Find(CleanCategory c, string path)
        {
            foreach (CleanTarget t in c.Targets)
                if (string.Equals(t.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }

        private static CleanTarget Find(CleanCategory c, string path, string mask)
        {
            foreach (CleanTarget t in c.Targets)
                if (string.Equals(t.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(t.Mask ?? "", mask ?? "", StringComparison.OrdinalIgnoreCase))
                    return t;
            return null;
        }
    }
}
