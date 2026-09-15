// SysDeck — инварианты каталога категорий (BuildCleanCategories) и подсчёта итога.
//
// Каталог собирается по НАСТОЯЩЕЙ машине: цель появляется, только если папка существует.
// Поэтому проверки написаны как утверждения обо всём каталоге целиком («ни одна цель не лежит
// в защищённом дереве»), а не про отдельные пути — их наличие от машины к машине разное.
// Проверки, которым нужна конкретная папка, живут в Tests.Regress.cs и честно отмечаются SKIP.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace SysDeck.Tests
{
    internal static class CatalogTests
    {
        // Категории, которые пользователь получает отмеченными. Список — независимый литерал:
        // если однажды в него попадёт «Dev: скачанные тулчейны» или «Кэши шейдеров», тест упадёт.
        private static readonly string[] RecommendedIds = new string[] {
            "dev", "sys", "shell", "store", "appcache", "nvidia",
        };

        // Категории, после которых пользователь платит временем (перекачка, перекомпиляция,
        // потерянные офлайн-загрузки, невозможность отката обновления) — не отмечаются никогда.
        private static readonly string[] NeverRecommendedIds = new string[] {
            "devbig", "shaders", "offline", "drivers", "oldver", "winsxs", "driverstore",
        };

        // Единственные имена, которым разрешено брать владение файлом (остатки обновлений
        // Windows принадлежат TrustedInstaller).
        private static readonly string[] OwnershipNames = new string[] {
            "Windows.old", "$Windows.~BT", "$Windows.~WS", "$WinREAgent",
            "$GetCurrent", "$SysReset", "ESD",
        };

        internal static void Run()
        {
            Engine e = Fx.NewEngine("data");
            List<CleanCategory> cats = e.BuildCleanCategories();
            T.Check("the catalog is not empty", cats.Count > 0, cats.Count + " categories");

            // В подменённой папке данных файла правил нет: значит всё ниже — про встроенный каталог.
            bool anyRules = false;
            foreach (CleanCategory c in cats)
                if (c.Id != null && c.Id.StartsWith("winapp2:", StringComparison.Ordinal)) anyRules = true;
            T.Check("without a rules file the catalog holds built-in categories only", !anyRules);

            Identity(cats);
            NoProtectedPaths(cats);
            Recommendations(cats);
            Ownership(cats);
            ActionCategories(cats);
            KeepsTheNewestVersion(e);
            DistinctTotal();
        }

        private static void Identity(List<CleanCategory> cats)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool uniqueIds = true, haveIds = true;
            foreach (CleanCategory c in cats)
            {
                if (string.IsNullOrEmpty(c.Id)) { haveIds = false; continue; }
                if (!ids.Add(c.Id)) uniqueIds = false;
            }
            T.Check("every category has an id", haveIds);
            T.Check("category ids are unique", uniqueIds);

            // Одна и та же папка, пришедшая двумя путями (%TEMP% и %LOCALAPPDATA%\Temp),
            // обходилась и удалялась бы дважды.
            string dup = null;
            foreach (CleanCategory c in cats)
            {
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (CleanTarget t in c.Targets)
                {
                    string key = t.Path.TrimEnd('\\') + "|" + (t.Mask ?? "");
                    if (!seen.Add(key) && dup == null) dup = c.Id + " -> " + key;
                }
            }
            T.Check("no category holds the same target path and mask twice", dup == null, dup);

            // Ключ выбора («не чистить эту папку») должен быть у каждой цели и не повторяться:
            // на одинаковых ключах снятая галочка гасила бы чужую цель.
            string dupKey = null;
            bool haveKeys = true;
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                {
                    if (string.IsNullOrEmpty(t.Key)) { haveKeys = false; continue; }
                    if (!keys.Add(t.Key) && dupKey == null) dupKey = t.Key;
                }
            T.Check("every catalog target carries a choice key", haveKeys);
            T.Check("choice keys are unique across the whole catalog", dupKey == null, dupKey);

            bool builtIn = true;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                    if (t.FromRules) builtIn = false;
            T.Check("no built-in target is marked as coming from a rules file", builtIn);
        }

        // Список запретных деревьев — независимый литерал из решений проекта, а не то, что
        // движок считает запретным: тест, спрашивающий у проверяемого кода, что ему нельзя,
        // не доказывает ничего.
        private static void NoProtectedPaths(List<CleanCategory> cats)
        {
            string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string sysDrive = Path.GetPathRoot(win);
            string sysProfile = Path.Combine(win, "System32\\config\\systemprofile\\AppData\\Local");

            string[] forbidden = new string[] {
                Path.Combine(win, "System32"),
                Path.Combine(win, "SysWOW64"),
                Path.Combine(win, "WinSxS"),
                Path.Combine(win, "Fonts"),
                Path.Combine(win, "assembly"),
                Path.Combine(win, "servicing"),
                Path.Combine(win, "Boot"),
                Path.Combine(win, "inf"),
                Path.Combine(win, "Installer"),
                Path.Combine(win, "Prefetch"),
                Path.Combine(sysDrive, "System Volume Information"),
                Path.Combine(sysDrive, "$Recycle.Bin"),
            };
            // Задокументированные исключения: temp и кэш IE служебного профиля (туда пишут
            // установщики) и базовые копии MSI-патчей (только по явному выбору пользователя).
            string[] allowed = new string[] {
                Path.Combine(sysProfile, "Temp"),
                Path.Combine(sysProfile, "Microsoft\\Windows\\INetCache"),
                Path.Combine(win, "SysWOW64\\config\\systemprofile\\AppData\\Local\\Microsoft\\Windows\\INetCache"),
                Path.Combine(win, "Installer\\$PatchCache$"),
            };

            string bad = null;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                {
                    if (!UnderAny(t.Path, forbidden)) continue;
                    if (UnderAny(t.Path, allowed)) continue;
                    if (bad == null) bad = c.Id + " -> " + t.Path;
                }
            T.Check("no catalog target sits inside a protected system tree", bad == null, bad);

            // Кэш загрузки программ: удалять нечего (мегабайты), терять есть что (каждая
            // программа стартует медленнее, пока Windows не соберёт его заново).
            string prefetch = null;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                    if (t.Path.EndsWith("\\Prefetch", StringComparison.OrdinalIgnoreCase)) prefetch = t.Path;
            T.Check("the Prefetch folder is not a catalog target", prefetch == null, prefetch);

            // Цель, совпавшая с корнем пользовательских данных, допустима ТОЛЬКО как «файлы по
            // имени в этой папке»: MEMORY.DMP в C:\Windows, IconCache.db в %LOCALAPPDATA%.
            string[] roots = SpecialRoots();
            string rootBad = null;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                {
                    if (!IsOneOf(t.Path, roots)) continue;
                    bool shallowMask = !string.IsNullOrEmpty(t.Mask) && !t.Recurse
                                       && t.Mask != "*" && t.Mask != "*.*";
                    if (!shallowMask && rootBad == null) rootBad = c.Id + " -> " + t.Path;
                }
            T.Check("a target that equals a protected root always carries a shallow file mask",
                    rootBad == null, rootBad);
        }

        private static void Recommendations(List<CleanCategory> cats)
        {
            string unexpected = null;
            foreach (CleanCategory c in cats)
                if (c.Recommended && Array.IndexOf(RecommendedIds, c.Id) < 0 && unexpected == null)
                    unexpected = c.Id;
            T.Check("only the known-safe categories are recommended", unexpected == null, unexpected);

            string risky = null;
            foreach (CleanCategory c in cats)
                if (c.Recommended && Array.IndexOf(NeverRecommendedIds, c.Id) >= 0 && risky == null)
                    risky = c.Id;
            T.Check("a category that costs the user real time is never recommended", risky == null, risky);

            // Корзина предлагается ровно одной категорией: иначе её объём попадал бы в итог дважды.
            int bins = 0;
            foreach (CleanCategory c in cats) if (c.RecycleBin) bins++;
            T.Eq("exactly one category offers the Recycle Bin", 1, bins);
        }

        private static void Ownership(List<CleanCategory> cats)
        {
            string wrong = null;
            int owning = 0;
            foreach (CleanCategory c in cats)
                foreach (CleanTarget t in c.Targets)
                {
                    string name = Path.GetFileName(t.Path.TrimEnd('\\'));
                    bool mayOwn = Array.IndexOf(OwnershipNames, name) >= 0
                                  && string.Equals(c.Id, "drivers", StringComparison.Ordinal);
                    if (t.TakeOwnership) owning++;
                    if (t.TakeOwnership != mayOwn && wrong == null)
                        wrong = c.Id + " -> " + t.Path + " TakeOwnership=" + t.TakeOwnership;
                }
            T.Check("only the Windows update leftovers are allowed to take ownership of a file",
                    wrong == null, wrong);
            if (owning == 0)
                T.Skip("at least one update leftover on this machine takes ownership",
                       "no Windows update leftovers present");
            else
                T.Check("at least one update leftover on this machine takes ownership", true);
        }

        private static void ActionCategories(List<CleanCategory> cats)
        {
            CleanCategory ds = ById(cats, "driverstore");
            CleanCategory sxs = ById(cats, "winsxs");
            T.Check("the DriverStore category is an action, not a folder list",
                    ds != null && ds.Kind == "driverstore" && ds.Targets.Count == 0);
            T.Check("the component store category is an action, not a folder list",
                    sxs != null && sxs.Kind == "winsxs" && sxs.Targets.Count == 0);
        }

        // «Оставить только самую новую версию». Настоящие точки вызова прибиты к %LOCALAPPDATA%,
        // а его .NET подменить не даёт (GetFolderPath игнорирует переменную окружения —
        // проверено), поэтому это единственное место в наборе, где приватный помощник вызывается
        // через отражение: иначе проверку пришлось бы ставить на папку ms-playwright конкретной
        // машины, то есть не проверять вовсе.
        private static void KeepsTheNewestVersion(Engine e)
        {
            MethodInfo mi = typeof(Engine).GetMethod("AddOldVersionsByPrefix",
                                                     BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null)
            {
                T.Check("the newest downloaded build is never scheduled for deletion", false,
                        "Engine.AddOldVersionsByPrefix not found");
                return;
            }

            string parent = Fx.MakeDir(Fx.Root, "versions");
            Fx.MakeDir(parent, "chromium-90");
            Fx.MakeDir(parent, "chromium-1200");
            Fx.MakeDir(parent, "chromium-1300");
            Fx.MakeDir(parent, "ffmpeg-1010");
            Fx.MakeDir(parent, "ffmpeg-1011");
            Fx.MakeDir(parent, "single-1");
            // Имена без номера версии (хеш сборки) сравниваются по дате изменения папки.
            string older = Fx.MakeDir(parent, "mcp-chrome-aaaaaa");
            string newer = Fx.MakeDir(parent, "mcp-chrome-bbbbbb");
            Directory.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            Directory.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            CleanCategory c = new CleanCategory();
            mi.Invoke(e, new object[] { c, parent, 1, null });
            HashSet<string> doomed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (CleanTarget t in c.Targets) doomed.Add(Path.GetFileName(t.Path.TrimEnd('\\')));

            T.Check("the newest numbered build of a group is never scheduled for deletion",
                    !doomed.Contains("chromium-1300") && !doomed.Contains("ffmpeg-1011"),
                    string.Join(",", new List<string>(doomed).ToArray()));
            T.Check("older numbered builds of a group are scheduled for deletion",
                    doomed.Contains("chromium-1200") && doomed.Contains("chromium-90")
                    && doomed.Contains("ffmpeg-1010"));
            T.Check("a group with a single build is left alone", !doomed.Contains("single-1"));
            T.Check("a build number is compared as a number, not as text",
                    !doomed.Contains("chromium-1300") && doomed.Contains("chromium-90"));
            T.Check("without a parsable version the newest folder by date is kept",
                    !doomed.Contains("mcp-chrome-bbbbbb") && doomed.Contains("mcp-chrome-aaaaaa"));

            CleanCategory only = new CleanCategory();
            mi.Invoke(e, new object[] { only, parent, 1, "chromium-" });
            bool onlyChromium = true;
            foreach (CleanTarget t in only.Targets)
                if (!Path.GetFileName(t.Path.TrimEnd('\\')).StartsWith("chromium-", StringComparison.OrdinalIgnoreCase))
                    onlyChromium = false;
            T.Check("a prefix filter keeps other groups out of the deletion list",
                    onlyChromium && only.Targets.Count == 2);
        }

        // Итог по всем категориям без двойного счёта: цели вкладываются друг в друга и
        // повторяются между категориями, а Корзина одна на всю систему.
        private static void DistinctTotal()
        {
            CleanCategory a = new CleanCategory();
            a.Targets.Add(Sized("C:\\wpc-tests-x", 100));
            a.Targets.Add(Sized("C:\\wpc-tests-x\\inner", 40));
            T.Eq("a target covered by a parent tree is counted once",
                 100L, Engine.DistinctSize(new CleanCategory[] { a }));

            CleanCategory b = new CleanCategory();
            b.Targets.Add(Sized("C:\\wpc-tests-x", 100));
            T.Eq("the same folder in two categories is counted once",
                 100L, Engine.DistinctSize(new CleanCategory[] { a, b }));

            CleanCategory off = new CleanCategory();
            CleanTarget disabled = Sized("C:\\wpc-tests-y", 70);
            disabled.Enabled = false;
            off.Targets.Add(disabled);
            T.Eq("a target the user unchecked does not count towards the total",
                 0L, Engine.DistinctSize(new CleanCategory[] { off }));

            CleanCategory guarded = new CleanCategory();
            CleanTarget cut = Sized("C:\\wpc-tests-z", 70);
            cut.Guarded = true;
            guarded.Targets.Add(cut);
            T.Eq("a target cut off by the guard does not count towards the total",
                 0L, Engine.DistinctSize(new CleanCategory[] { guarded }));

            CleanCategory bin1 = new CleanCategory();
            bin1.RecycleBin = true; bin1.BinEnabled = true; bin1.BinSize = 50;
            CleanCategory bin2 = new CleanCategory();
            bin2.RecycleBin = true; bin2.BinEnabled = true; bin2.BinSize = 50;
            T.Eq("the Recycle Bin counts once even if two categories offer it",
                 50L, Engine.DistinctSize(new CleanCategory[] { bin1, bin2 }));
        }

        // ---------- помощники ----------
        private static CleanTarget Sized(string path, long size)
        {
            CleanTarget t = new CleanTarget();
            t.Path = path;
            t.Size = size;
            t.Analyzed = true;
            return t;
        }

        internal static CleanCategory ById(List<CleanCategory> cats, string id)
        {
            foreach (CleanCategory c in cats)
                if (string.Equals(c.Id, id, StringComparison.Ordinal)) return c;
            return null;
        }

        private static string[] SpecialRoots()
        {
            Environment.SpecialFolder[] sf = new Environment.SpecialFolder[] {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
            };
            List<string> roots = new List<string>();
            foreach (Environment.SpecialFolder f in sf)
            {
                string p = null;
                try { p = Environment.GetFolderPath(f); } catch { }
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }
            return roots.ToArray();
        }

        private static bool IsOneOf(string path, string[] roots)
        {
            foreach (string r in roots)
                if (string.Equals(path.TrimEnd('\\'), r.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static bool UnderAny(string path, string[] dirs)
        {
            string p = path.TrimEnd('\\').ToLowerInvariant();
            foreach (string d in dirs)
            {
                string dd = d.TrimEnd('\\').ToLowerInvariant();
                if (p == dd || p.StartsWith(dd + "\\", StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
