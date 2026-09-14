// Windows Process Cleaner — прогон тестов: изоляция данных, реестр областей, помощники фикстур.
// Сборка и запуск: tests\run-tests.bat (тот же csc.exe, что и build.bat; src\*.cs + tests\*.cs).
//
// Почему всё устроено именно так:
//  * приложение УДАЛЯЕТ файлы. Поэтому каждый разрушающий тест работает только внутри своей
//    папки под %LOCALAPPDATA%\Temp, с категорией, собранной руками и с выключенной Корзиной;
//    Fx.Clean отказывается запускать удаление, если хоть одна цель лежит вне фикстуры;
//  * движок хранит конфиг, историю и логи в %APPDATA%\WindowsProcessCleaner. Прогон подменяет
//    эту папку переменной WPC_DATA_DIR и убеждается в подмене ДО первого new Engine(): иначе
//    первый же тест переписал бы настройки и историю пользователя;
//  * результат каждой проверки — одна строка PASS/FAIL/SKIP, в конце «RESULT n passed, m failed»
//    и код возврата, равный числу падений. Ровно так же вели себя одиночные пробники, из
//    которых этот набор вырос.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsProcessCleaner.Tests
{
    // ------------------------------------------------------------------ //
    //  Счёт проверок
    // ------------------------------------------------------------------ //
    internal static class T
    {
        internal static int Passed;
        internal static int Failed;
        internal static int Skipped;

        internal static void Check(string name, bool ok) { Check(name, ok, null); }

        internal static void Check(string name, bool ok, string info)
        {
            if (ok)
            {
                Passed++;
                Console.WriteLine("PASS " + name);
            }
            else
            {
                Failed++;
                Console.WriteLine("FAIL " + name + (string.IsNullOrEmpty(info) ? "" : "  [" + info + "]"));
            }
        }

        // Ожидание — всегда независимый литерал или значение, посчитанное не так, как его
        // считает проверяемый код: тест, повторяющий реализацию, не доказывает ничего.
        internal static void Eq(string name, object expected, object actual)
        {
            bool ok = expected == null ? actual == null : expected.Equals(actual);
            Check(name, ok, ok ? null : "expected " + Show(expected) + ", got " + Show(actual));
        }

        private static string Show(object v)
        {
            if (v == null) return "<null>";
            return "<" + v + ">";
        }

        // Проверка, для которой на ЭТОЙ машине нет данных (папки приложения, которое здесь не
        // установлено). Не провал и не успех: считается отдельно, чтобы зелёный прогон нигде
        // не выдавал непроверенное за проверенное.
        internal static void Skip(string name, string why)
        {
            Skipped++;
            Console.WriteLine("SKIP " + name + "  [" + why + "]");
        }
    }

    // ------------------------------------------------------------------ //
    //  Фикстуры: временное дерево, файлы известной длины, ссылки, длинные пути
    // ------------------------------------------------------------------ //
    internal static class Fx
    {
        // Корень всех фикстур. Берём именно %LOCALAPPDATA%\Temp, а не %TEMP%: у второго на
        // многих машинах короткое имя 8.3 (C:\Users\RUSYAN~1\...), и половина проверок путей
        // сравнивала бы канонические пути с неканоническими.
        internal static string Root;

        internal static string MakeDir(string a, string b) { return MakeDir(Path.Combine(a, b)); }
        internal static string MakeDir(string a, string b, string c) { return MakeDir(Path.Combine(Path.Combine(a, b), c)); }

        internal static string MakeDir(string path)
        {
            Directory.CreateDirectory(path);
            return path;
        }

        // Файл заданной длины: размер в тестах сверяется с этим числом, а не с тем, что
        // насчитал обход, — иначе проверка размера была бы тавтологией.
        internal static string MakeFile(string path, int bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            byte[] data = new byte[bytes];
            for (int i = 0; i < bytes; i++) data[i] = (byte)(i & 0xFF);
            File.WriteAllBytes(path, data);
            return path;
        }

        // Обе даты выставляются явно: окно свежести смотрит и на изменение, и на создание.
        internal static void SetTimes(string path, DateTime write, DateTime create)
        {
            File.SetCreationTime(path, create);
            File.SetLastWriteTime(path, write);
        }

        // Точка соединения (junction) через mklink /J: прав администратора не требует,
        // в отличие от символической ссылки.
        internal static bool Junction(string link, string target)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                "/c mklink /J \"" + link + "\" \"" + target + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                }
            }
            catch { return false; }
            uint a = Native.AttributesOf(link);
            return a != Native.INVALID_FILE_ATTRIBUTES && (a & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0;
        }

        // ---------- длинные пути ----------
        // Directory.CreateDirectory и File.WriteAllBytes в режиме старых путей .NET падают за
        // 260 знаков, а проверять поведение движка на длинных путях надо именно там.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
                                                         IntPtr securityAttributes, uint disposition,
                                                         uint flags, IntPtr template);

        internal static bool MakeLongDir(string path)
        {
            if (Native.IsDirectoryPath(path)) return true;
            return CreateDirectoryW(Native.LongPathOf(path), IntPtr.Zero);
        }

        internal static bool MakeLongFile(string path, int bytes)
        {
            using (SafeFileHandle h = CreateFileW(Native.LongPathOf(path), 0x40000000u /* GENERIC_WRITE */,
                                                  0u, IntPtr.Zero, 2u /* CREATE_ALWAYS */, 0x80u, IntPtr.Zero))
            {
                if (h.IsInvalid) return false;
                using (FileStream fs = new FileStream(h, FileAccess.Write))
                {
                    byte[] data = new byte[bytes];
                    for (int i = 0; i < bytes; i++) data[i] = (byte)(i & 0xFF);
                    fs.Write(data, 0, bytes);
                }
            }
            return true;
        }

        // ---------- уборка ----------
        // Своя рекурсия, а не Directory.Delete(recursive): та в .NET Framework заходит ВНУТРЬ
        // точки соединения и снесла бы папку, которая по замыслу теста должна уцелеть; плюс
        // она не умеет пути длиннее 260 знаков, которые тест создаёт сам.
        internal static void RemoveTree(string path)
        {
            uint a = Native.AttributesOf(path);
            if (a == Native.INVALID_FILE_ATTRIBUTES) return;
            string lp = Native.LongPathOf(path);
            if ((a & Native.FILE_ATTRIBUTE_DIRECTORY) == 0)
            {
                Native.SetFileAttributesW(lp, Native.FILE_ATTRIBUTE_NORMAL);
                Native.DeleteFileW(lp);
                return;
            }
            if ((a & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0)
            {
                Native.RemoveDirectoryW(lp);   // снимается сама ссылка, не то, на что она смотрит
                return;
            }
            List<string> kids = new List<string>();
            Native.WIN32_FIND_DATA fd;
            IntPtr h = Native.FindFirstFileExW(Native.LongPathOf(path + "\\*"), Native.FindExInfoBasic,
                                               out fd, Native.FindExSearchNameMatch, IntPtr.Zero,
                                               Native.FIND_FIRST_EX_LARGE_FETCH);
            if (h != Native.INVALID_HANDLE_VALUE)
            {
                try
                {
                    do
                    {
                        if (fd.cFileName != "." && fd.cFileName != "..")
                            kids.Add(path + "\\" + fd.cFileName);
                    } while (Native.FindNextFileW(h, out fd));
                }
                finally { Native.FindClose(h); }
            }
            foreach (string k in kids) RemoveTree(k);
            Native.SetFileAttributesW(lp, Native.FILE_ATTRIBUTE_NORMAL);
            Native.RemoveDirectoryW(lp);
        }

        // ---------- движок ----------
        internal static bool IsUnder(string path, string dir)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(dir)) return false;
            string p, d;
            try
            {
                p = Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant();
                d = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant();
            }
            catch { return false; }
            return p == d || p.StartsWith(d + "\\", StringComparison.Ordinal);
        }

        // Единственный способ, которым тесты заводят движок: папка данных подменена и проверена
        // дважды — до конструктора (что переменную вообще читают) и после (что движок взял её).
        internal static Engine NewEngine(string sub)
        {
            string dir = MakeDir(Root, sub);
            Environment.SetEnvironmentVariable("WPC_DATA_DIR", dir);
            string seen = Engine.DefaultDataDir();
            if (!IsUnder(seen, Root))
                throw new InvalidOperationException("WPC_DATA_DIR ignored, data dir would be " + seen);
            Engine e = new Engine();
            if (!IsUnder(e.DataDir, Root))
                throw new InvalidOperationException("engine data dir escaped the fixture: " + e.DataDir);
            return e;
        }

        internal static CleanTarget Tgt(string path)
        {
            CleanTarget t = new CleanTarget();
            t.Path = path;
            return t;
        }

        internal static CleanCategory Cat(params CleanTarget[] targets)
        {
            CleanCategory c = new CleanCategory();
            c.Id = "wpc-tests";
            c.Title = "wpc-tests";
            c.RecycleBin = false;      // ни один тест не трогает Корзину
            foreach (CleanTarget t in targets) c.Targets.Add(t);
            return c;
        }

        // Отсечена ли цель предохранителем — так, как это видит окно: через анализ категории.
        internal static bool Guarded(Engine e, CleanTarget t)
        {
            CleanCategory c = Cat(t);
            e.AnalyzeCategory(c);
            return t.Guarded;
        }

        internal static CleanCategory Analyze(Engine e, CleanTarget t)
        {
            CleanCategory c = Cat(t);
            e.AnalyzeCategory(c);
            return c;
        }

        // Вторая разрешённая для удаления область: фикстура в %ProgramData%, которую тест путей заводит
        // сам, чтобы проверить очистку общесистемного дерева на настоящих путях. Вне блока — пусто.
        internal static string MachineRoot = "";

        // Последний рубеж перед настоящим удалением: цель вне фикстуры или включённая Корзина —
        // это ошибка теста, и лучше уронить прогон, чем выяснить это по пропавшим файлам.
        internal static CleanResult Clean(Engine e, CleanCategory c)
        {
            if (c.RecycleBin)
                throw new InvalidOperationException("a test category must never enable the Recycle Bin");
            foreach (CleanTarget t in c.Targets)
                if (!IsUnder(t.Path, Root) && !(MachineRoot.Length > 0 && IsUnder(t.Path, MachineRoot)))
                    throw new InvalidOperationException("refusing to run a delete outside the fixture: " + t.Path);
            List<CleanCategory> list = new List<CleanCategory>();
            list.Add(c);
            return e.CleanCategories(list);
        }
    }

    // ------------------------------------------------------------------ //
    //  Точка входа прогона
    // ------------------------------------------------------------------ //
    internal static class TestMain
    {
        // Имена областей в аргументах — прогнать только их (run-tests.bat downloads); без аргументов — все.
        private static string[] _only;

        private static int Main(string[] args)
        {
            // Этот же exe запускается тестами «как браузер» (native messaging host) — до изоляции и прогона.
            int hostCode;
            if (WindowsProcessCleaner.Downloads.DlBridge.TryRun(args, out hostCode)) return hostCode;
            _only = args != null && args.Length > 0 ? args : null;
            // Слепок настоящего конфига пользователя: в конце сверяем, что он не изменился.
            string realConfig = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                             "WindowsProcessCleaner"), "config.json");
            DateTime realStamp = DateTime.MinValue;
            bool realExisted = File.Exists(realConfig);
            if (realExisted) realStamp = File.GetLastWriteTimeUtc(realConfig);

            string root = Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp"),
                "wpc-tests-" + Process.GetCurrentProcess().Id + "-" + DateTime.Now.Ticks);
            Directory.CreateDirectory(root);
            Fx.Root = root;

            // Проверка изоляции ДО первого new Engine(). Не сошлось — прогон не начинается вовсе.
            Environment.SetEnvironmentVariable("WPC_DATA_DIR", Path.Combine(root, "data"));
            string dataDir = Engine.DefaultDataDir();
            if (!Fx.IsUnder(dataDir, root))
            {
                Console.WriteLine("ABORT data isolation failed: Engine.DefaultDataDir() = " + dataDir);
                Console.WriteLine("RESULT 0 passed, 1 failed, 0 skipped");
                return 1;
            }
            Console.WriteLine("data dir  " + dataDir);
            Console.WriteLine("fixtures  " + root);
            Console.WriteLine();

            try
            {
                Area("paths", PathTests.Run);
                Area("clean", CleanTests.Run);
                Area("catalog", CatalogTests.Run);
                Area("regress", RegressTests.Run);
                Area("config", ConfigTests.Run);
                Area("rules", RuleTests.Run);
                Area("autostart", AutostartTests.Run);
                Area("elevation", ElevationTests.Run);
                Area("ram", RamTests.Run);
                Area("gpu", GpuTests.Run);
                Area("foldersize", FolderSizeTests.Run);
                Area("capture", CaptureTests.Run);
                Area("browsers", BrowserTests.Run);
                Area("downloads", DownloadsTests.Run);
                Area("torrent", TorrentTests.Run);
                Area("media", MediaTests.Run);
            }
            finally
            {
                Fx.RemoveTree(root);
            }

            Console.WriteLine();
            Console.WriteLine("--- teardown ---");
            T.Check("the fixture tree is removed after the run", !Native.PathExists(root), root);
            if (realExisted)
                T.Check("the real user config.json was not written during the run",
                        File.Exists(realConfig) && File.GetLastWriteTimeUtc(realConfig) == realStamp,
                        realConfig);
            else
                T.Check("the run did not create a config.json in the real data dir",
                        !File.Exists(realConfig), realConfig);

            Console.WriteLine();
            Console.WriteLine("RESULT " + T.Passed + " passed, " + T.Failed + " failed, "
                              + T.Skipped + " skipped");
            return T.Failed;
        }

        // Область падает целиком — это тоже провал, но остальные должны отработать.
        private static void Area(string name, Action body)
        {
            if (_only != null && Array.FindIndex(_only, delegate(string a) { return string.Equals(a, name, StringComparison.OrdinalIgnoreCase); }) < 0) return;
            Console.WriteLine("--- " + name + " ---");
            try { body(); }
            catch (Exception ex)
            {
                T.Check(name + " area runs to the end without an exception", false,
                        ex.GetType().Name + ": " + ex.Message);
            }
            Console.WriteLine();
        }
    }
}
