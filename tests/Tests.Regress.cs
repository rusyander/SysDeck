// Windows Process Cleaner — регрессии на конкретные ошибки каталога (аудит 2026-09-10b, раунды 30-33).
//
// Каждая проверка здесь стоит за уже случившейся ошибкой: удалённый registry.bin работающего
// демона Gradle, снесённая гигабайтами перекачка Gradle/Maven в «рекомендованных», вложение
// письма, стёртое прямо из-под открытого Word, вырванная из-под идущего обновления Windows.old.
// Цель появляется в каталоге, только если папка есть на машине: чего здесь нет — SKIP, но
// никогда не молчаливое «прошло».

using System;
using System.Collections.Generic;
using System.IO;

namespace WindowsProcessCleaner.Tests
{
    internal static class RegressTests
    {
        // Своё, не умолчательное значение окна свежести: так видно, что цель берёт его из
        // настроек, а не совпала с числом 10 случайно.
        private const int Fresh = 13;

        internal static void Run()
        {
            Engine e = Fx.NewEngine("data");
            e.Config.CleanSkipRecentMinutes = Fresh;
            List<CleanCategory> cats = e.BuildCleanCategories();

            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDrive = Path.GetPathRoot(win);

            // C-14: в ~/.gradle/daemon рядом с логами лежит registry.bin работающих демонов —
            // удалить папку целиком значит осиротить их. Берутся только логи.
            InCategory(cats, "logs", Path.Combine(up, ".gradle\\daemon"),
                       "the Gradle daemon folder is cleaned by a log mask only",
                       delegate(CleanTarget t)
                       {
                           return string.Equals(t.Mask, "*.log", StringComparison.OrdinalIgnoreCase)
                                  && t.Recurse && t.ContentsOnly;
                       });

            // C-13: ~/.gradle/caches — тот же класс, что репозиторий Maven (гигабайты и минуты
            // повторной загрузки). В рекомендованных Dev-кэшах ему не место.
            MovedTo(cats, Path.Combine(up, ".gradle\\caches"), "devbig", "dev",
                    "the Gradle dependency cache is a heavy toolchain, not a recommended dev cache");
            MovedTo(cats, Path.Combine(up, ".nuget\\packages"), "devbig", "dev",
                    "the global NuGet package cache is a heavy toolchain, not a recommended dev cache");
            MovedTo(cats, Path.Combine(up, ".m2\\repository"), "devbig", "dev",
                    "the Maven repository is a heavy toolchain, not a recommended dev cache");

            // C-17: INetCache — это ещё и Content.Outlook/Content.Word, где лежит вложение,
            // открытое прямо из письма. Пока его редактируют, файл свежий и неприкосновенный.
            InCategory(cats, "shell", Path.Combine(lad, "Microsoft\\Windows\\INetCache"),
                       "the INetCache folder keeps the fresh-files window",
                       delegate(CleanTarget t) { return t.MinAgeMinutes == Fresh; });

            // Файл, который Центр обновления докачивает прямо сейчас, — не мусор.
            InCategory(cats, "sys", Path.Combine(win, "SoftwareDistribution\\Download"),
                       "the Windows Update download folder keeps the fresh-files window",
                       delegate(CleanTarget t) { return t.MinAgeMinutes == Fresh; });

            // Окно свежести из настроек доходит и до обычных temp-целей.
            InCategory(cats, "sys", Path.Combine(lad, "Temp"),
                       "the temp folder takes the fresh-files window from the settings",
                       delegate(CleanTarget t) { return t.MinAgeMinutes == Fresh; });

            // C-11: система откатывается на Windows.old первые 10 дней после обновления —
            // тот же срок неприкосновенности, что у остальных остатков обновления.
            const int grace = 10 * 24 * 60;
            string[] leftovers = new string[] {
                "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WinREAgent", "$GetCurrent", "$SysReset", "ESD",
            };
            foreach (string name in leftovers)
            {
                string path = Path.Combine(sysDrive, name);
                string claim = "the " + name + " leftover keeps the ten-day rollback window";
                InCategory(cats, "drivers", path, claim,
                           delegate(CleanTarget t) { return t.MinAgeMinutes == grace; });
            }

            // R32b: у Playwright удаляются только СТАРЫЕ сборки; сама папка ms-playwright и
            // текущая сборка остаются, иначе проект требует npx playwright install.
            PlaywrightOldBuildsOnly(cats, Path.Combine(lad, "ms-playwright"));

            // C-32/K: pypoetry\Cache целиком — это ещё и virtualenvs всех Poetry-проектов.
            // Кэшем являются только cache (индексы PyPI) и artifacts (скачанные wheel).
            string poetry = Path.Combine(lad, "pypoetry\\Cache");
            if (Directory.Exists(poetry))
            {
                T.Check("the Poetry cache root, which also holds the virtualenvs, is not a target",
                        !HasPath(cats, poetry));
                T.Check("only the Poetry cache subfolders are targets",
                        HasPath(cats, Path.Combine(poetry, "cache"))
                        || HasPath(cats, Path.Combine(poetry, "artifacts")));
            }
            else
            {
                T.Skip("the Poetry cache root, which also holds the virtualenvs, is not a target",
                       "no pypoetry cache on this machine");
            }

            // R32: Storage\ext\<id> — это данные расширений (IndexedDB, Local Storage), кэшем
            // являются только их собственные подпапки.
            ChromiumExtStorage(cats);

            // Инцидент 06.09.2026: база распознавания игр NVIDIA App — не кэш.
            string ontology = null;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                    if (t.Path.IndexOf("\\NvBackend\\ApplicationOntology",
                                       StringComparison.OrdinalIgnoreCase) >= 0) ontology = t.Path;
            T.Check("the NVIDIA game-detection database is never a catalog target",
                    ontology == null, ontology);
        }

        // Playwright: папка целиком под удаление не идёт, самая новая сборка каждой группы тоже.
        // «Самая новая» считается здесь заново, по номеру в имени, а не тем же кодом, что в движке.
        private static void PlaywrightOldBuildsOnly(List<CleanCategory> cats, string root)
        {
            if (!Directory.Exists(root))
            {
                T.Skip("only old Playwright builds are scheduled for deletion", "no ms-playwright folder");
                return;
            }
            T.Check("the Playwright folder as a whole is never a target", !HasPath(cats, root));

            Dictionary<string, string> newest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, long> newestNum = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string d in Directory.GetDirectories(root))
            {
                string name = Path.GetFileName(d);
                int dash = name.LastIndexOf('-');
                if (dash <= 0 || dash == name.Length - 1) continue;
                string group = name.Substring(0, dash);
                long num;
                if (!long.TryParse(name.Substring(dash + 1), out num)) continue;
                long have;
                if (!newestNum.TryGetValue(group, out have) || num > have)
                {
                    newestNum[group] = num;
                    newest[group] = d;
                }
            }
            if (newest.Count == 0)
            {
                T.Skip("only old Playwright builds are scheduled for deletion", "no numbered builds");
                return;
            }
            string doomed = null;
            foreach (string keep in newest.Values)
                if (HasPath(cats, keep)) doomed = keep;
            T.Check("only old Playwright builds are scheduled for deletion", doomed == null, doomed);
        }

        private static void ChromiumExtStorage(List<CleanCategory> cats)
        {
            string whole = null;
            bool sawExt = false;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                {
                    if (t.Path.IndexOf("\\Storage\\ext", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    sawExt = true;
                    if (t.Path.EndsWith("\\Storage\\ext", StringComparison.OrdinalIgnoreCase)) whole = t.Path;
                }
            if (!sawExt)
                T.Skip("browser extension storage is never a target as a whole", "no chromium extension storage");
            else
                T.Check("browser extension storage is never a target as a whole", whole == null, whole);
        }

        // ---------- помощники ----------
        // Цель существует на этой машине — проверяем её свойства; нет — честный SKIP.
        private static void InCategory(List<CleanCategory> cats, string id, string path,
                                       string claim, Predicate<CleanTarget> ok)
        {
            if (!Directory.Exists(path)) { T.Skip(claim, "no " + path); return; }
            CleanCategory c = CatalogTests.ById(cats, id);
            if (c == null) { T.Check(claim, false, "category " + id + " missing"); return; }
            CleanTarget t = Find(c, path);
            if (t == null) { T.Check(claim, false, path + " is not a target of " + id); return; }
            T.Check(claim, ok(t), "mask=" + (t.Mask ?? "<null>") + " recurse=" + t.Recurse
                    + " contentsOnly=" + t.ContentsOnly + " minAge=" + t.MinAgeMinutes);
        }

        private static void MovedTo(List<CleanCategory> cats, string path, string wanted, string forbidden, string claim)
        {
            if (!Directory.Exists(path)) { T.Skip(claim, "no " + path); return; }
            CleanCategory want = CatalogTests.ById(cats, wanted);
            CleanCategory bad = CatalogTests.ById(cats, forbidden);
            bool inWanted = want != null && Find(want, path) != null;
            bool inForbidden = bad != null && Find(bad, path) != null;
            T.Check(claim, inWanted && !inForbidden,
                    "in " + wanted + "=" + inWanted + ", in " + forbidden + "=" + inForbidden);
        }

        private static CleanTarget Find(CleanCategory c, string path)
        {
            string p = path.TrimEnd('\\');
            foreach (CleanTarget t in c.Targets)
                if (string.Equals(t.Path.TrimEnd('\\'), p, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }

        private static bool HasPath(List<CleanCategory> cats, string path)
        {
            foreach (CleanCategory c in cats)
                if (Find(c, path) != null) return true;
            return false;
        }
    }
}
