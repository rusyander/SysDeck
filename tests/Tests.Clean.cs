// Windows Process Cleaner — тесты обхода и настоящего удаления.
//
// Все проверки здесь ДЕЙСТВИТЕЛЬНО удаляют файлы — но только внутри своей папки под
// %LOCALAPPDATA%\Temp и только через категорию, собранную руками, с выключенной Корзиной
// (Fx.Clean роняет прогон, если цель оказалась вне фикстуры). Размеры и количества сверяются
// с длинами, заданными при создании файлов, а не с тем, что насчитал сам обход.

using System;
using System.IO;

namespace WindowsProcessCleaner.Tests
{
    internal static class CleanTests
    {
        internal static void Run()
        {
            Engine e = Fx.NewEngine("data");
            string area = Fx.MakeDir(Fx.Root, "clean");

            Junction(e, area);
            JunctionAsTarget(e, area);
            StaysInsideTheRoot(e, area);
            MaskAndRecursion(e, area);
            FreshFiles(e, area);
            ContentsOnly(e, area);
            Exclusions(e, area);
            SkippedTargets(e, area);
            LongPaths(e, area);

            T.Check("the clean log is written into the app data folder",
                    File.Exists(e.CleanLogPath) && Fx.IsUnder(e.CleanLogPath, Fx.Root), e.CleanLogPath);
        }

        // Точка соединения внутри цели: за ней может лежать что угодно, вплоть до корня диска.
        // Обход не должен в неё заходить, а удаление — ни снести содержимое, ни снять саму ссылку
        // (Directory.Delete по ссылке удаляет ссылку, но каталог за ней пережить обязан).
        private static void Junction(Engine e, string area)
        {
            string b = Fx.MakeDir(area, "junction");
            string outside = Fx.MakeDir(b, "outside");
            string keep = Fx.MakeFile(Path.Combine(outside, "keep.bin"), 999);
            string target = Fx.MakeDir(b, "target");
            Fx.MakeFile(Path.Combine(target, "junk.bin"), 10);
            Fx.MakeFile(Path.Combine(Fx.MakeDir(target, "inner"), "junk2.bin"), 20);
            string link = Path.Combine(target, "link");
            if (!Fx.Junction(link, outside))
            {
                T.Skip("a junction inside a target is never followed", "mklink /J failed");
                return;
            }

            CleanTarget t = Fx.Tgt(target);
            CleanCategory c = Fx.Analyze(e, t);
            T.Eq("the walker counts only the files inside the target, not those behind a junction",
                 30L, c.Size);
            T.Eq("the walker counts only the files inside the target, not those behind a junction (count)",
                 2, c.FileCount);

            CleanResult res = Fx.Clean(e, Fx.Cat(Fx.Tgt(target)));
            T.Check("a file behind a junction inside the target survives the delete run",
                    File.Exists(keep), keep);
            T.Check("the folder a junction points at survives the delete run",
                    Directory.Exists(outside), outside);
            T.Check("the junction itself is not removed",
                    Native.PathExists(link), link);
            T.Check("ordinary files inside the target are deleted",
                    !File.Exists(Path.Combine(target, "junk.bin")));
            T.Check("empty subfolders left inside the target are removed",
                    !Directory.Exists(Path.Combine(target, "inner")));
            T.Eq("the freed size counts only the files actually deleted", 30L, res.Freed);
            T.Eq("the deleted file count matches the files removed", 2, res.FilesDeleted);
        }

        // Цель, которая сама является точкой соединения: за ней чужая папка — не трогаем ничего.
        private static void JunctionAsTarget(Engine e, string area)
        {
            string b = Fx.MakeDir(area, "junction-root");
            string outside = Fx.MakeDir(b, "outside");
            string keep = Fx.MakeFile(Path.Combine(outside, "keep.bin"), 55);
            string link = Path.Combine(b, "link");
            if (!Fx.Junction(link, outside))
            {
                T.Skip("a target that is itself a junction is neither walked nor deleted", "mklink /J failed");
                return;
            }
            CleanCategory c = Fx.Analyze(e, Fx.Tgt(link));
            T.Eq("a target that is itself a junction is not walked", 0L, c.Size);
            Fx.Clean(e, Fx.Cat(Fx.Tgt(link)));
            T.Check("a target that is itself a junction is not deleted",
                    File.Exists(keep) && Native.PathExists(link));
        }

        private static void StaysInsideTheRoot(Engine e, string area)
        {
            string b = Fx.MakeDir(area, "root-bound");
            string target = Fx.MakeDir(b, "target");
            Fx.MakeFile(Path.Combine(target, "junk.bin"), 8);
            string sibling = Fx.MakeFile(Path.Combine(Fx.MakeDir(b, "sibling"), "keep.bin"), 8);
            string parentFile = Fx.MakeFile(Path.Combine(b, "keep-parent.bin"), 8);

            Fx.Clean(e, Fx.Cat(Fx.Tgt(target)));
            T.Check("a sibling folder of the target is untouched", File.Exists(sibling));
            T.Check("a file in the parent of the target is untouched", File.Exists(parentFile));
            T.Check("the target folder itself is removed when ContentsOnly is off",
                    !Directory.Exists(target));
        }

        private static void MaskAndRecursion(Engine e, string area)
        {
            string b = Fx.MakeDir(area, "mask");
            Fx.MakeFile(Path.Combine(b, "a.log"), 11);
            Fx.MakeFile(Path.Combine(b, "keep.txt"), 33);
            Fx.MakeFile(Path.Combine(Fx.MakeDir(b, "sub"), "b.log"), 22);

            CleanTarget flat = Fx.Tgt(b);
            flat.Mask = "*.log";
            flat.Recurse = false;
            flat.ContentsOnly = true;
            CleanCategory c = Fx.Analyze(e, flat);
            T.Eq("a masked target does not recurse unless asked", 11L, c.Size);

            CleanTarget deep = Fx.Tgt(b);
            deep.Mask = "*.log";
            deep.Recurse = true;
            deep.ContentsOnly = true;
            c = Fx.Analyze(e, deep);
            T.Eq("a masked target with recursion takes matching files in subfolders too", 33L, c.Size);

            // Маска сравнивается без учёта регистра: имена в Windows регистронезависимы.
            string caseDir = Fx.MakeDir(area, "mask-case");
            Fx.MakeFile(Path.Combine(caseDir, "A.LOG"), 7);
            CleanTarget ci = Fx.Tgt(caseDir);
            ci.Mask = "*.log";
            ci.Recurse = false;
            ci.ContentsOnly = true;
            T.Eq("a file mask matches regardless of letter case", 7L, Fx.Analyze(e, ci).Size);

            // Маска с звёздочкой в середине (thumbcache_*.db) — то, чем чистятся кэши эскизов.
            string midDir = Fx.MakeDir(area, "mask-middle");
            Fx.MakeFile(Path.Combine(midDir, "thumbcache_96.db"), 5);
            Fx.MakeFile(Path.Combine(midDir, "other.db"), 6);
            CleanTarget mid = Fx.Tgt(midDir);
            mid.Mask = "thumbcache_*.db";
            mid.Recurse = false;
            mid.ContentsOnly = true;
            T.Eq("a mask with a star in the middle matches only the intended names", 5L, Fx.Analyze(e, mid).Size);

            // Настоящее удаление по маске: папки не трогаются вообще, даже пустые.
            Fx.Clean(e, Fx.Cat(flat));
            T.Check("a masked delete removes the matching file", !File.Exists(Path.Combine(b, "a.log")));
            T.Check("a masked delete leaves files that do not match", File.Exists(Path.Combine(b, "keep.txt")));
            T.Check("a masked, non-recursive delete leaves files in subfolders",
                    File.Exists(Path.Combine(b, "sub\\b.log")));
            T.Check("a masked delete never removes a folder", Directory.Exists(Path.Combine(b, "sub")));
        }

        // Окно свежести: только что изменённый файл может принадлежать идущей установке.
        // Смотреть надо и на дату изменения, и на дату создания: установщики распаковывают
        // файлы с сохранённой старой датой изменения, и по одной LastWrite они считались старыми.
        private static void FreshFiles(Engine e, string area)
        {
            DateTime now = DateTime.Now;
            string b = Fx.MakeDir(area, "fresh");
            string old = Fx.MakeFile(Path.Combine(b, "old.bin"), 100);
            Fx.SetTimes(old, now.AddHours(-2), now.AddHours(-2));
            string young = Fx.MakeFile(Path.Combine(b, "young.bin"), 200);
            Fx.SetTimes(young, now, now);

            CleanTarget t = Fx.Tgt(b);
            t.ContentsOnly = true;
            t.MinAgeMinutes = 60;
            CleanCategory c = Fx.Analyze(e, t);
            T.Eq("a file inside the fresh-files window is not counted", 100L, c.Size);
            T.Eq("a file inside the fresh-files window is not counted (count)", 1, c.FileCount);

            Fx.Clean(e, Fx.Cat(t));
            T.Check("a file inside the fresh-files window is not deleted", File.Exists(young));
            T.Check("a file older than the fresh-files window is deleted", !File.Exists(old));

            // Дата изменения старая, дата создания сегодняшняя — файл всё равно свежий.
            string c2 = Fx.MakeDir(area, "fresh-ctime");
            string tricky = Fx.MakeFile(Path.Combine(c2, "unpacked.bin"), 300);
            Fx.SetTimes(tricky, now.AddHours(-2), now);
            CleanTarget t2 = Fx.Tgt(c2);
            t2.ContentsOnly = true;
            t2.MinAgeMinutes = 60;
            T.Eq("a freshly created file with an old modification date is still fresh",
                 0L, Fx.Analyze(e, t2).Size);
            Fx.Clean(e, Fx.Cat(t2));
            T.Check("a freshly created file with an old modification date is not deleted",
                    File.Exists(tricky));

            // Без окна свежести берётся всё.
            CleanTarget t3 = Fx.Tgt(c2);
            t3.ContentsOnly = true;
            T.Eq("without a fresh-files window every file is counted", 300L, Fx.Analyze(e, t3).Size);
        }

        private static void ContentsOnly(Engine e, string area)
        {
            string keepFolder = Fx.MakeDir(area, "contents-only");
            Fx.MakeFile(Path.Combine(Fx.MakeDir(keepFolder, "sub"), "junk.bin"), 4);
            CleanTarget t = Fx.Tgt(keepFolder);
            t.ContentsOnly = true;
            Fx.Clean(e, Fx.Cat(t));
            T.Check("ContentsOnly leaves the target folder itself in place", Directory.Exists(keepFolder));
            T.Check("ContentsOnly still removes the subfolders inside the target",
                    !Directory.Exists(Path.Combine(keepFolder, "sub")));

            string dropFolder = Fx.MakeDir(area, "contents-and-self");
            Fx.MakeFile(Path.Combine(dropFolder, "junk.bin"), 4);
            Fx.Clean(e, Fx.Cat(Fx.Tgt(dropFolder)));
            T.Check("without ContentsOnly the target folder is removed too", !Directory.Exists(dropFolder));
        }

        // Исключения пользователя действуют не только на корень цели, но и на каждый файл и
        // каждую папку внутри неё.
        private static void Exclusions(Engine e, string area)
        {
            string b = Fx.MakeDir(area, "exclusions");
            string keepDir = Fx.MakeDir(b, "keep-dir");
            string keptInDir = Fx.MakeFile(Path.Combine(keepDir, "inner.bin"), 5);
            string keptFile = Fx.MakeFile(Path.Combine(b, "keep-file.bin"), 6);
            string doomed = Fx.MakeFile(Path.Combine(b, "junk.bin"), 7);

            e.Config.CleanExclude.Add(keepDir);
            e.Config.CleanExclude.Add(keptFile);
            try
            {
                CleanTarget t = Fx.Tgt(b);
                t.ContentsOnly = true;
                T.Eq("an excluded file and an excluded folder are left out of the analysis",
                     7L, Fx.Analyze(e, t).Size);
                Fx.Clean(e, Fx.Cat(t));
                T.Check("an excluded folder inside the target survives the delete run",
                        File.Exists(keptInDir) && Directory.Exists(keepDir));
                T.Check("an excluded file inside the target survives the delete run", File.Exists(keptFile));
                T.Check("a file next to an excluded one is still deleted", !File.Exists(doomed));
            }
            finally { e.Config.CleanExclude.Clear(); }
        }

        // Две причины пропустить цель, и обе должны попасть в лог: снятая пользователем галочка
        // и предохранитель. Молча пропущенная цель — это «почистили», которого не было.
        private static void SkippedTargets(Engine e, string area)
        {
            string offDir = Fx.MakeDir(area, "target-off");
            string offFile = Fx.MakeFile(Path.Combine(offDir, "keep.bin"), 3);
            CleanTarget off = Fx.Tgt(offDir);
            off.Enabled = false;
            CleanResult res = Fx.Clean(e, Fx.Cat(off));
            T.Check("a target the user unchecked is not deleted", File.Exists(offFile));
            T.Check("a target the user unchecked is reported as skipped", HasLine(res, "SKIP (off)"));

            string guardedDir = Fx.MakeDir(area, "target-guarded");
            string guardedFile = Fx.MakeFile(Path.Combine(guardedDir, "keep.bin"), 3);
            e.Config.CleanExclude.Add(guardedDir);
            try
            {
                res = Fx.Clean(e, Fx.Cat(Fx.Tgt(guardedDir)));
                T.Check("a target cut off by the guard is not deleted", File.Exists(guardedFile));
                T.Check("a target cut off by the guard is reported as skipped", HasLine(res, "SKIP (guard)"));
                T.Eq("a fully skipped category frees nothing", 0L, res.Freed);
            }
            finally { e.Config.CleanExclude.Clear(); }
        }

        // Пути длиннее 260 знаков: в режиме старых путей .NET File.Exists отвечает «нет», а
        // DirectoryInfo бросает PathTooLong — целые деревья во временных папках оставались
        // нетронутыми с пометкой «недоступно».
        private static void LongPaths(Engine e, string area)
        {
            string root = Fx.MakeDir(area, "long");
            string p = root;
            string seg = new string('d', 40);
            for (int i = 0; i < 8; i++)
            {
                p = p + "\\" + seg;
                if (!Fx.MakeLongDir(p))
                {
                    T.Skip("a file past the 260-character limit is found and deleted",
                           "cannot create the deep fixture");
                    return;
                }
            }
            string deepFile = p + "\\payload.bin";
            if (!Fx.MakeLongFile(deepFile, 123))
            {
                T.Skip("a file past the 260-character limit is found and deleted",
                       "cannot create the deep fixture file");
                return;
            }
            T.Check("the deep fixture really is past the 260-character limit",
                    deepFile.Length > 260, deepFile.Length + " chars");

            CleanTarget t = Fx.Tgt(root);
            t.ContentsOnly = true;
            CleanCategory c = Fx.Analyze(e, t);
            T.Eq("a file past the 260-character limit is found by the walker", 123L, c.Size);

            CleanResult res = Fx.Clean(e, Fx.Cat(t));
            T.Check("a file past the 260-character limit is deleted", !Native.PathExists(deepFile));
            T.Check("folders past the 260-character limit are removed too", !Native.PathExists(p));
            T.Eq("the freed size of a long-path delete is reported", 123L, res.Freed);
            T.Check("ContentsOnly keeps the short root of a long-path target", Directory.Exists(root));
        }

        private static bool HasLine(CleanResult res, string head)
        {
            foreach (string line in res.Log)
                if (line.StartsWith(head, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
