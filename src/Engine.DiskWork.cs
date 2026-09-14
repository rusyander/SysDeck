// Windows Process Cleaner — обход папок, анализ и удаление целей, защита путей, журнал очистки
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        // Отмена длинного анализа/удаления: пользователь ушёл с вкладки или закрыл окно.
        private volatile bool _cancelDisk;
        public void CancelDiskWork() { _cancelDisk = true; }
        public void ResetDiskCancel() { _cancelDisk = false; }
        public bool DiskCancelled { get { return _cancelDisk; } }

        // Сколько обходов/удалений идёт прямо сейчас. Флаг отмены один на движок: проверка состояния
        // на «Главной» и «Ускорить» тоже считают системный мусор и раньше сбрасывали его безусловно —
        // нажатый в этот момент «Стоп» на вкладке очистки молча отменялся, и удаление продолжалось.
        private int _diskWorkers;

        // Что дисковая работа делает прямо сейчас — строкой для UI. Иначе на длинном удалении
        // (десятки гигабайт, DISM на хранилище компонентов) пользователь минутами видел
        // неподвижное «Удаление…» и не мог отличить работу от зависшего окна.
        public volatile string DiskStatus;

        // Снять отмену, только если никакой дисковой работы сейчас нет. false — работа идёт,
        // флаг оставлен как есть (вызывающий получит «прервано» вместо чужой отмены).
        public bool TryResetDiskCancel()
        {
            if (Interlocked.CompareExchange(ref _diskWorkers, 0, 0) != 0) return false;
            _cancelDisk = false;
            return true;
        }

        // Обход каталога БЕЗ сбора всех путей в память и БЕЗ рекурсии.
        // Старая версия складывала в List<string> путь каждого файла (для %TEMP% или
        // .nuget\packages это сотни тысяч строк и сотни МБ), а потом делала на каждый
        // ещё один new FileInfo(f).Length — второй поход к ФС за уже полученными данными.
        // EnumerateFileSystemInfos отдаёт размер сразу, стек вместо рекурсии не боится
        // глубоких node_modules.
        // Посетитель файла: полный путь, размер и атрибуты — всё из WIN32_FIND_DATA,
        // без второго похода к ФС за каждым файлом.
        private delegate void FileVisitor(string path, long size, uint attrs);

        // Страховка от циклов, которые не помечены точкой повторного разбора.
        // Реальных деревьев такой глубины не бывает.
        private const int MaxWalkDepth = 96;

        private static bool IsDotName(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            return name.Trim(' ', '.').Length == 0;
        }

        private static long FileTimeOf(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            return ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
        }

        // Пути из настройки «Не чистить эти пути»: полный канонический путь (8.3-имена и junction
        // раскрыты — см. Native.CanonicalPath), без хвостового «\», в нижнем регистре. Считается
        // один раз на обход, не на файл; результат живёт минуту (папка могла появиться позже).
        // Три поля читались и писались из восьми потоков обхода порознь — список одного ключа мог
        // достаться с датой другого. Снимок один и неизменяемый, меняется только ссылка на него.
        private sealed class ExclSnapshot
        {
            public readonly List<string> List; public readonly string Key; public readonly DateTime At;
            public ExclSnapshot(List<string> list, string key, DateTime at) { List = list; Key = key; At = at; }
        }
        private volatile ExclSnapshot _excl;

        private List<string> ExcludeList()
        {
            string key = string.Join("\n", Config.CleanExclude.ToArray());
            ExclSnapshot s = _excl;
            if (s != null && key == s.Key && (DateTime.Now - s.At).TotalSeconds < 60) return s.List;
            List<string> l = new List<string>();
            foreach (string ex in Config.CleanExclude)
            {
                if (string.IsNullOrEmpty(ex)) continue;
                string exl;
                try { exl = Native.CanonicalPath(Path.GetFullPath(ex.Trim())).TrimEnd('\\').ToLowerInvariant(); } catch { continue; }
                if (exl.Length > 0) l.Add(exl);
            }
            _excl = new ExclSnapshot(l, key, DateTime.Now);
            return l;
        }

        // Корень обхода в том же каноническом виде, что и исключения: иначе цель, записанная через
        // 8.3-имя (%TEMP% = C:\Users\RUSYAN~1\…) или junction, не совпадала с исключением по префиксу.
        // Вызывается после проверки, что сам корень — не точка повторного разбора.
        private static string CanonicalRoot(string rootPath)
        {
            string c = NormalizeDir(Native.CanonicalPath(rootPath));
            return c ?? rootPath;
        }

        // Путь равен исключению или лежит внутри него (pathLower — полный путь в нижнем регистре).
        private static bool IsExcluded(string pathLower, List<string> excl)
        {
            for (int i = 0; i < excl.Count; i++)
                if (pathLower == excl[i] || pathLower.StartsWith(excl[i] + "\\", StringComparison.Ordinal)) return true;
            return false;
        }

        // Обход через FindFirstFileEx по пути с префиксом \\?\: DirectoryInfo в режиме старых
        // путей .NET (нет app.config) бросает PathTooLong за 260 знаков, и глубокие деревья во
        // временных папках оставались нетронутыми с пометкой «недоступно». Исключения из
        // настроек действуют на подпапки и файлы внутри цели, а не только на её корень.
        // Точки повторного разбора (junction/symlink) не раскрываются и в dirsOut не попадают —
        // RemoveDirectory снёс бы саму ссылку.
        // Предохранитель сохранений и служебных баз проверяется здесь же, на каждой папке обхода:
        // одной проверки корня цели мало (см. IsGuardedSubPath).
        private void Walk(CleanTarget t, FileVisitor onFile, List<string> dirsOut, ref int errors)
        {
            string rootPath = NormalizeDir(t.Path);
            if (rootPath == null) return;
            uint ra = Native.AttributesOf(rootPath);
            if (ra == Native.INVALID_FILE_ATTRIBUTES || (ra & Native.FILE_ATTRIBUTE_DIRECTORY) == 0
                || (ra & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return;
            rootPath = CanonicalRoot(rootPath);
            // Второй рубеж на случай вызова в обход IsAllowedTarget: сам корень тоже под запретом.
            if (IsGuardedSubPath(rootPath.ToLowerInvariant())) return;

            string mask = string.IsNullOrEmpty(t.Mask) ? "*" : t.Mask;
            // В общесистемном дереве приложений цель без пометки Disposable отдаёт только заведомо
            // одноразовые файлы: там под именем «cache» лежит и дистрибутив программы (см. выше).
            // Фильтр стоит в обходе, а не в удалении, чтобы анализ показывал ровно тот размер,
            // который очистка действительно освободит.
            bool appTree = !t.Disposable && IsAppTreePath(rootPath.ToLowerInvariant());
            long cutoff = t.MinAgeMinutes > 0
                ? DateTime.Now.AddMinutes(-t.MinAgeMinutes).ToFileTime()
                : long.MaxValue;
            List<string> excl = ExcludeList();

            Stack<string> stack = new Stack<string>();
            Stack<int> depths = new Stack<int>();
            stack.Push(rootPath); depths.Push(0);
            while (stack.Count > 0)
            {
                if (_cancelDisk) return;
                string dir = stack.Pop();
                int depth = depths.Pop();
                string prefix = dir.EndsWith("\\") ? dir : dir + "\\";
                Native.WIN32_FIND_DATA fd;
                IntPtr h = Native.FindFirstFileExW(LongPath(prefix + "*"), Native.FindExInfoBasic, out fd,
                                                   Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                if (h == Native.INVALID_HANDLE_VALUE)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 18 ERROR_NO_MORE_FILES — пусто; 2/3 — папка исчезла между обходом и заходом
                    if (err != 18 && err != 2 && err != 3) errors++;
                    continue;
                }
                try
                {
                    do
                    {
                        if (_cancelDisk) return;
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        string full = prefix + name;
                        if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) == 0)
                        {
                            // и по дате создания тоже: установщики распаковывают файлы с сохранённой старой
                            // датой изменения, и окно свежести по одной LastWrite их пропускало
                            if (t.MinAgeMinutes > 0 && (FileTimeOf(fd.ftLastWriteTime) > cutoff || FileTimeOf(fd.ftCreationTime) > cutoff)) continue;
                            if (mask != "*" && !MaskMatch(name, mask)) continue;
                            if (appTree && !IsThrowawayInAppTree(full.ToLowerInvariant())) continue;
                            if (excl.Count > 0 && IsExcluded(full.ToLowerInvariant(), excl)) continue;
                            onFile(full, ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow, fd.dwFileAttributes);
                        }
                        else if (t.Recurse)
                        {
                            // junction/symlink: за ним может лежать что угодно, включая корень диска
                            if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            // Имя вида ".. " (с хвостовым пробелом или точкой): Win32 без \\?\ нормализует
                            // его в родителя и обход зацикливается; легальными такие каталоги не бывают.
                            if (IsDotName(name)) continue;
                            if (depth >= MaxWalkDepth) continue;
                            string fullLower = full.ToLowerInvariant();
                            if (excl.Count > 0 && IsExcluded(fullLower, excl)) continue;
                            // Ни в состав, ни в dirsOut: подпапка с сохранениями или служебной базой
                            // не раскрывается, поэтому её файлы недостижимы и для анализа, и для удаления.
                            if (IsGuardedSubPath(fullLower)) continue;
                            if (dirsOut != null) dirsOut.Add(full);
                            stack.Push(full); depths.Push(depth + 1);
                        }
                    } while (Native.FindNextFileW(h, out fd));
                }
                finally { Native.FindClose(h); }
            }
        }

        // Полный путь папки без хвостового «\» (корень диска остаётся «C:\»); null — путь негодный.
        private static string NormalizeDir(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string p;
            try { p = Path.GetFullPath(path).TrimEnd('\\'); } catch { return null; }
            if (p.Length < 2) return null;
            return p.EndsWith(":") ? p + "\\" : p;
        }

        // Маска в стиле winapp2: "*.log", "thumbcache_*.db", "*".
        private static bool MaskMatch(string name, string mask)
        {
            if (mask == "*" || mask == "*.*") return true;
            int star = mask.IndexOf('*');
            if (star < 0) return string.Equals(name, mask, StringComparison.OrdinalIgnoreCase);
            string head = mask.Substring(0, star);
            string tail = mask.Substring(star + 1);
            if (tail.IndexOf('*') >= 0)
            {
                // несколько звёздочек — сводим к «содержит все куски по порядку»
                string[] parts = mask.Split('*');
                int pos = 0;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0) continue;
                    int at = name.IndexOf(parts[i], pos, StringComparison.OrdinalIgnoreCase);
                    if (at < 0) return false;
                    if (i == 0 && at != 0) return false;
                    pos = at + parts[i].Length;
                }
                string last = parts[parts.Length - 1];
                if (last.Length > 0 && !name.EndsWith(last, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            if (name.Length < head.Length + tail.Length) return false;
            return name.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(tail, StringComparison.OrdinalIgnoreCase);
        }

        public void AnalyzeCategory(CleanCategory c)
        {
            Interlocked.Increment(ref _diskWorkers);
            try { AnalyzeCategoryCore(c); }
            finally { Interlocked.Decrement(ref _diskWorkers); }
        }

        private void AnalyzeCategoryCore(CleanCategory c)
        {
            if (c.Kind == "driverstore") { AnalyzeDriverStore(c); return; }
            if (c.Kind == "winsxs") { AnalyzeComponentStore(c); return; }
            int errors = 0;
            // Счётчики растут прямо в категории: строка списка тикает вживую, а не стоит на «…»
            // всё время обхода (.nuget\packages или Windows.old — это минуты на холодном кэше).
            long baseSize = 0; int baseFiles = 0;
            c.Size = 0; c.FileCount = 0;
            foreach (CleanTarget t in c.Targets)
            {
                if (_cancelDisk) break;
                t.Guarded = !IsAllowedTarget(t);
                if (t.Guarded) { t.Size = 0; t.FileCount = 0; t.Errors = 0; t.Analyzed = true; continue; }
                long ts = 0; int tc = 0; int te = 0;
                bool live = t.Enabled;   // выключенное в «Составе» в итог не входит — и в живой счёт тоже
                Walk(t, delegate(string path, long size, uint attrs)
                {
                    ts += size; tc++;
                    if (live && (tc & 255) == 0) { c.Size = baseSize + ts; c.FileCount = baseFiles + tc; }
                }, null, ref te);
                t.Size = ts; t.FileCount = tc; t.Errors = te; t.Analyzed = true;
                if (live) { baseSize += ts; baseFiles += tc; c.Size = baseSize; c.FileCount = baseFiles; }
                if (t.Enabled) errors += te;   // недоступность отключённой папки пользователя не касается
            }
            if (c.RecycleBin)
            {
                Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                c.BinSize = 0; c.BinCount = 0;
                try { if (Native.SHQueryRecycleBin(null, ref info) == 0) { c.BinSize = info.i64Size; c.BinCount = (int)info.i64NumItems; } }
                catch { }
            }
            RecalcCategory(c);
            c.Analyzed = !_cancelDisk;
            c.Note = errors > 0
                ? Tr.S("часть папок недоступна (" + errors + ")", errors + " folder(s) not accessible")
                : null;
        }

        // Анализ категорий параллельно: узкое место — задержки ФС, а не CPU,
        // поэтому несколько потоков дают кратный выигрыш на холодном кэше.
        public void AnalyzeCategories(List<CleanCategory> cats, Action<CleanCategory> onDone)
        {
            if (cats == null || cats.Count == 0) return;
            int next = -1;
            int workers = Math.Min(cats.Count, Math.Max(2, Environment.ProcessorCount));
            if (workers > 8) workers = 8;
            Thread[] pool = new Thread[workers];
            object gate = new object();

            for (int i = 0; i < workers; i++)
            {
                pool[i] = new Thread(delegate()
                {
                    while (true)
                    {
                        int idx = Interlocked.Increment(ref next);
                        if (idx >= cats.Count || _cancelDisk) return;
                        CleanCategory c = cats[idx];
                        try { AnalyzeCategory(c); } catch { }
                        if (onDone != null) { lock (gate) { try { onDone(c); } catch { } } }
                    }
                });
                pool[i].IsBackground = true;
                pool[i].Start();
            }
            foreach (Thread t in pool) t.Join();
        }

        // Предохранитель: не удаляем корни дисков, ключевые системные папки целиком,
        // папки с данными пользователя и всё, что он сам внёс в исключения.
        private static readonly string[] _neverTouch = new string[] {
            "\\windows\\system32", "\\windows\\syswow64", "\\windows\\winsxs",
            "\\windows\\system32\\drivers", "\\windows\\fonts",
            "\\system volume information", "\\$recycle.bin",
            "\\windows\\system32\\config", "\\windows\\assembly",
            "\\windows\\servicing", "\\windows\\boot", "\\windows\\inf",
            // DriverStore чистится только через pnputil, кэш MSI и бэкап WinSxS — никогда
            "\\windows\\system32\\driverstore", "\\windows\\installer", "\\windows\\winsxs\\backup",
            // журналы событий: правило winapp2 «*.evtx» иначе снесло бы историю системы
            "\\windows\\system32\\winevt",
        };

        // Единственные подпапки запретных деревьев, которые чистить можно: temp и кэш IE служебного
        // профиля и журналы System32\LogFiles (правила winapp2). Кэш MSI-патчей ($PatchCache$) из
        // этого списка убран: из него Windows чинит и удаляет уже установленные патчи.
        private static readonly string[] _neverTouchAllow = new string[] {
            "\\windows\\system32\\config\\systemprofile\\appdata\\local\\temp",
            "\\windows\\system32\\config\\systemprofile\\appdata\\local\\microsoft\\windows\\inetcache",
            "\\windows\\syswow64\\config\\systemprofile\\appdata\\local\\microsoft\\windows\\inetcache",
            "\\windows\\system32\\logfiles",
        };

        private static bool IsNeverTouchAllowed(string pathLower, string rootLower)
        {
            foreach (string ok in _neverTouchAllow)
                if (IsSelfOrUnder(pathLower, rootLower + ok)) return true;
            return false;
        }

        // Цель с маской и без рекурсии удаляет только файлы по маске прямо в папке, саму папку и
        // подпапки не трогает. Для неё корни %WinDir%, AppData и ProgramData допустимы: иначе
        // MEMORY.DMP и *.dmp в C:\Windows, IconCache.db в %LOCALAPPDATA% и правила winapp2 вида
        // «%WinDir%|*.log» вечно стояли под предохранителем и никогда не чистились (06.09.2026).
        // Корни дисков, System32 и прочие _neverTouch, папки сохранений и профиль пользователя
        // (Документы, Рабочий стол…) закрыты по-прежнему, с маской или без.
        private bool IsAllowedTarget(CleanTarget t)
        {
            if (t == null) return false;
            bool shallowMask = !string.IsNullOrEmpty(t.Mask) && !t.Recurse && t.Mask != "*" && t.Mask != "*.*";
            // Цель из внешнего файла правил: послабления для корней не действуют, плюс отдельный
            // список запретов — см. IsRuleTargetAllowed.
            if (t.FromRules)
            {
                if (!IsRuleTargetAllowed(t.Path)) return false;
                shallowMask = false;
            }
            return IsAllowedTarget(t.Path, shallowMask);
        }

        // Что позволено правилу ИЗ ФАЙЛА (winapp2.ini). Файл лежит в папке, куда пишет любой
        // процесс от имени пользователя, а приложение работает от администратора: строка
        // «FileKey1=%WinDir%|*.dll» в подложенном файле означала бы снос C:\Windows правами
        // администратора руками нашего процесса. Поэтому: внутрь Windows — только заведомо
        // мусорные подпапки, в Program Files — никогда, в чужие профили — никогда.
        // Встроенные правила (MEMORY.DMP в C:\Windows, IconCache.db в %LOCALAPPDATA%) это
        // не ограничивает: они в коде, подменить их нельзя.
        private static readonly string[] _ruleWinDirAllow = new string[] {
            "\\temp", "\\prefetch", "\\logs", "\\minidump", "\\debug",
            "\\softwaredistribution\\download", "\\system32\\logfiles", "\\downloaded program files",
        };

        private bool IsRuleTargetAllowed(string path)
        {
            string p;
            try { p = Path.GetFullPath(path).TrimEnd('\\').ToLowerInvariant(); } catch { return false; }

            string win = (_winDir ?? "").TrimEnd('\\').ToLowerInvariant();
            if (win.Length > 0 && IsSelfOrUnder(p, win))
            {
                string rest = p.Length == win.Length ? "" : p.Substring(win.Length);
                foreach (string ok in _ruleWinDirAllow)
                    if (rest == ok || rest.StartsWith(ok + "\\", StringComparison.Ordinal)) return true;
                return false;
            }

            string[] pf = new string[] { _programFiles, _programFilesX86 };
            foreach (string f in pf)
            {
                if (string.IsNullOrEmpty(f)) continue;
                if (IsSelfOrUnder(p, f.TrimEnd('\\').ToLowerInvariant())) return false;
            }

            // Чужие профили: %UserProfile% в правиле раскрывается в текущего пользователя, но
            // подложенный файл может написать путь другого пользователя буквами.
            try
            {
                string me = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\').ToLowerInvariant();
                string users = Path.GetDirectoryName(me);
                if (!string.IsNullOrEmpty(users))
                {
                    users = users.TrimEnd('\\').ToLowerInvariant();
                    string pub = Path.Combine(users, "public").ToLowerInvariant();
                    if (IsSelfOrUnder(p, users) && !IsSelfOrUnder(p, me) && !IsSelfOrUnder(p, pub)) return false;
                }
            }
            catch { }
            return true;
        }

        private static bool IsSelfOrUnder(string pathLower, string dirLower)
        {
            if (dirLower.Length == 0) return false;
            return pathLower == dirLower || pathLower.StartsWith(dirLower + "\\", StringComparison.Ordinal);
        }

        private bool IsAllowedTarget(string path) { return IsAllowedTarget(path, false); }

        private bool IsMaskableRoot(string pathLower)
        {
            string[] roots;
            try
            {
                roots = new string[] {
                    _winDir,
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                };
            }
            catch { return false; }
            foreach (string r in roots)
                if (!string.IsNullOrEmpty(r) && pathLower == r.TrimEnd('\\').ToLowerInvariant()) return true;
            return false;
        }

        private bool IsAllowedTarget(string path, bool shallowMask)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string p;
            try { p = Path.GetFullPath(path).TrimEnd('\\'); } catch { return false; }
            if (p.Length < 4) return false;

            string root = (Path.GetPathRoot(p) ?? "").TrimEnd('\\');
            if (string.Equals(p, root, StringComparison.OrdinalIgnoreCase)) return false;

            string pl = p.ToLowerInvariant();
            bool rootOk = shallowMask && IsMaskableRoot(pl);
            if (!rootOk)
            {
                if (pl == _winDir.ToLowerInvariant()) return false;
                if (!string.IsNullOrEmpty(_programFiles) && pl == _programFiles.ToLowerInvariant()) return false;
                if (!string.IsNullOrEmpty(_programFilesX86) && pl == _programFilesX86.ToLowerInvariant()) return false;
            }

            // Запрет — на всё поддерево, а не только на саму папку: иначе цель вида
            // «System Volume Information\x» или «$Recycle.Bin\<SID>» проходила мимо предохранителя.
            string rootLower = root.ToLowerInvariant();
            foreach (string bad in _neverTouch)
            {
                string full = rootLower + bad;
                if (pl == full || pl.EndsWith(bad)) return false;
                if (pl.StartsWith(full + "\\", StringComparison.Ordinal) && !IsNeverTouchAllowed(pl, rootLower)) return false;
            }

            // папки с данными, которые чистилка не должна затрагивать даже по ошибке в правиле
            if (!rootOk && IsUserDataRoot(pl)) return false;
            if (HasSegment(pl, _saveSegments)) return false;
            if (HasSegment(pl, _appDataSegments)) return false;
            if (HasSegment(pl, _payloadSegments)) return false;

            // никогда не чистим собственный каталог данных (там конфиг, история, логи)
            if (IsSelfOrUnder(pl, _dir.TrimEnd('\\').ToLowerInvariant())) return false;

            if (IsExcluded(pl, ExcludeList())) return false;
            if (IsExcluded(Native.CanonicalPath(p).TrimEnd('\\').ToLowerInvariant(), ExcludeList())) return false;
            return true;
        }

        // Сохранения игр — не мусор ни по какому правилу, включая winapp2: путь с таким
        // сегментом не становится целью вообще (не «не отмечен», а отсутствует в составе)
        // И НЕ РАСКРЫВАЕТСЯ при рекурсивном обходе — см. IsGuardedSubPath и Walk.
        // «remote» в Steam\userdata — облачные сейвы, «wgs» — сейвы Xbox/Game Pass.
        private static readonly string[] _saveSegments = new string[] {
            "\\saved games\\", "\\save games\\", "\\my games\\", "\\saves\\", "\\savegames\\",
            "\\savegame\\", "\\savedata\\", "\\wgs\\", "\\steam\\userdata\\",
        };

        // Служебные базы приложений, которые правило очистки может принять за кэш. Путь с таким
        // сегментом не становится целью ни по какому правилу, включая winapp2.
        // NvBackend\ApplicationOntology — база распознавания игр NVIDIA App: после её удаления
        // 06.09.2026 бэкенд писал «LoadApplicationDetectors failed» на каждый новый процесс,
        // а заново не скачивал (кэш ETag отвечал 304).
        private static readonly string[] _appDataSegments = new string[] {
            "\\nvbackend\\applicationontology\\",
        };

        // Склад установщика: папка, из которой программа ставит и чинит саму себя. По имени он
        // неотличим от кэша, поэтому такие имена закрыты на уровне пути — как сохранения игр.
        // «Package Cache» (WiX/Visual Studio) и «$PatchCache$» (базовые копии для MSI-патчей) —
        // без них восстановление и удаление уже установленной программы просит исходный дистрибутив;
        // «Installer2» — то же самое у драйверов NVIDIA; «depots» — распакованные архивы Logitech.
        private static readonly string[] _payloadSegments = new string[] {
            "\\package cache\\", "\\packagecache\\", "\\$patchcache$\\",
            "\\installer2\\", "\\lghub\\cache\\", "\\lghub\\depots\\",
        };

        // ---------- общесистемные деревья приложений ----------
        //
        // %ProgramData%, Program Files и Program Files (x86) — это места, куда программа кладёт не
        // только кэш, но и собственный дистрибутив: архивы, из которых она доставляет и чинит свои
        // компоненты. Имя папки их не различает («cache», «Downloader», «data\cache»), и правило,
        // написанное по имени, уносит вместе с кэшем способность программы починиться: она
        // продолжает запускаться и начинает жаловаться на отсутствующие файлы.
        //
        // 10.09.2026 так был снесён C:\ProgramData\LGHUB\cache — 698,7 МБ архивов .depot, — и
        // Logitech G HUB перестал устанавливать свои драйверы («Unable to extract extension
        // executable pipeline://driver_logi_lamparray/…»). Тот же класс правил был нацелен на
        // Wargaming GameCenter\cache, Battle.net Agent\data\cache и Adobe\ARM: вычислять каждого
        // производителя отдельно — значит чинить постфактум, по одному сломанному приложению.
        //
        // Поэтому в этих деревьях правило разрешительное, а не запретительное: удаляется только то,
        // что одноразово по своей природе — журналы, дампы, временные файлы, — а остальное остаётся,
        // даже если правило просит папку целиком. Цель, про которую автор правила проверил, что
        // приложение восстановит содержимое само, помечается CleanTarget.Disposable и фильтр обходит.
        private static readonly string[] _throwawaySegments = new string[] {
            "\\logs\\", "\\log\\", "\\logfiles\\", "\\temp\\", "\\tmp\\",
            "\\crashdumps\\", "\\crashes\\", "\\minidump\\", "\\minidumps\\",
            "\\wer\\", "\\reportqueue\\", "\\reportarchive\\",
        };

        private static readonly string[] _throwawayExt = new string[] {
            ".log", ".etl", ".trace", ".dmp", ".mdmp", ".hdmp", ".wer",
            ".tmp", ".temp", ".old", ".bak", ".cache",
        };

        private static string[] _appTrees;

        private string[] AppTrees()
        {
            string[] cached = _appTrees;
            if (cached != null) return cached;
            List<string> roots = new List<string>();
            string pd = null;
            try { pd = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData); }
            catch { }
            string[] candidates = new string[] { pd, _programFiles, _programFilesX86 };
            foreach (string r in candidates)
            {
                if (string.IsNullOrEmpty(r)) continue;
                string low = r.TrimEnd('\\').ToLowerInvariant();
                if (low.Length > 3 && !roots.Contains(low)) roots.Add(low);
            }
            cached = roots.ToArray();
            _appTrees = cached;
            return cached;
        }

        private bool IsAppTreePath(string pathLower)
        {
            foreach (string root in AppTrees()) if (IsSelfOrUnder(pathLower, root)) return true;
            return false;
        }

        // Одноразовый файл: журнал, дамп, временный — или лежащий в папке, которая целиком про них.
        private static bool IsThrowawayInAppTree(string pathLower)
        {
            foreach (string seg in _throwawaySegments)
                if (pathLower.IndexOf(seg, StringComparison.Ordinal) >= 0) return true;
            int dot = pathLower.LastIndexOf('.');
            if (dot <= pathLower.LastIndexOf('\\')) return false;
            string ext = pathLower.Substring(dot);
            foreach (string e in _throwawayExt) if (ext == e) return true;
            return false;
        }

        // Проверка обоих списков разом. Раньше она стояла только в IsAllowedTarget, то есть на корне
        // цели: правило winapp2 с RECURSE над %LocalAppData%\NVIDIA Corporation спокойно доходило до
        // NvBackend\ApplicationOntology, а над профилем — до Saved Games, и повторило бы инцидент
        // 06.09.2026, ради которого предохранитель и добавлялся. Теперь тот же список проверяется
        // на каждой папке обхода, и комментарий выше наконец описывает то, что код действительно делает.
        private static bool IsGuardedSubPath(string pathLower)
        {
            return HasSegment(pathLower, _saveSegments) || HasSegment(pathLower, _appDataSegments)
                || HasSegment(pathLower, _payloadSegments) || IsOwnFolder(pathLower);
        }

        // Собственные папки приложения: каталог данных (конфиг, история, журналы) и — у
        // портативной сборки — папка рядом с exe. Проверка на корне цели уже стояла в
        // IsAllowedTarget, но её одной мало: каталог данных может лежать ВНУТРИ чистимой
        // папки, и обход доходил до него сверху. 10.09.2026 тихий прогон так и снёс свой
        // собственный config.json, оказавшийся под %LOCALAPPDATA%\Temp; для портативной
        // сборки, распакованной в «Загрузки» или во временную папку, это обычный случай,
        // а не экзотика.
        private static string[] _ownFolders = new string[0];

        internal static void GuardOwnFolder(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            string d;
            try { d = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(); }
            catch { return; }
            if (d.Length < 4) return;
            string[] cur = _ownFolders;
            foreach (string s in cur) if (s == d) return;
            string[] next = new string[cur.Length + 1];
            cur.CopyTo(next, 0);
            next[cur.Length] = d;
            _ownFolders = next;      // присваивание ссылки атомарно: читателям из других потоков достанется либо старый список, либо новый целиком
        }

        private static bool IsOwnFolder(string pathLower)
        {
            string[] cur = _ownFolders;
            foreach (string d in cur) if (IsSelfOrUnder(pathLower, d)) return true;
            return false;
        }

        private static bool HasSegment(string pathLower, string[] segments)
        {
            string p = pathLower.TrimEnd('\\') + "\\";
            foreach (string seg in segments)
                if (p.IndexOf(seg, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private bool IsUserDataRoot(string pathLower)
        {
            Environment.SpecialFolder[] guarded = new Environment.SpecialFolder[] {
                Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic,
                Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.Desktop,
                Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
            };
            foreach (Environment.SpecialFolder sf in guarded)
            {
                string f;
                try { f = Environment.GetFolderPath(sf); } catch { continue; }
                if (string.IsNullOrEmpty(f)) continue;
                if (pathLower == f.TrimEnd('\\').ToLowerInvariant()) return true;
            }
            return false;
        }

        // Удаление одной цели потоково: файл удаляется сразу, как только найден.
        // Всё через Win32 по пути \\?\ — File.Delete/Directory.Delete за MAX_PATH не работают.
        private long DeleteTarget(CleanTarget t, CleanResult res)
        {
            long freed = 0;
            string rootPath = NormalizeDir(t.Path);
            if (rootPath == null) return 0;
            uint ra = Native.AttributesOf(rootPath);
            if (ra == Native.INVALID_FILE_ATTRIBUTES || (ra & Native.FILE_ATTRIBUTE_DIRECTORY) == 0) return 0;
            // цель сама junction/symlink: за ней чужая папка — не трогаем ни содержимое, ни ссылку
            if ((ra & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return 0;
            rootPath = CanonicalRoot(rootPath);

            List<string> dirs = new List<string>();
            int errors = 0;
            int deleted = 0;
            bool own = t.TakeOwnership;
            Walk(t, delegate(string path, long size, uint attrs)
            {
                string lp = LongPath(path);
                if ((attrs & (Native.FILE_ATTRIBUTE_READONLY | Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0)
                    Native.SetFileAttributesW(lp, Native.FILE_ATTRIBUTE_NORMAL);
                if (Native.DeleteFileW(lp)) { freed += size; deleted++; return; }
                // Файлы Windows.old / $Windows.~BT принадлежат TrustedInstaller: даже администратору
                // «отказано в доступе», и категория показывала гигабайты, а освобождала ноль.
                // Только для таких целей: стать владельцем, снять атрибуты и повторить.
                if (own && Marshal.GetLastWin32Error() == Native.ERROR_ACCESS_DENIED && Native.TakeOwnership(lp))
                {
                    Native.SetFileAttributesW(lp, Native.FILE_ATTRIBUTE_NORMAL);
                    if (Native.DeleteFileW(lp)) { freed += size; deleted++; return; }
                }
                errors++;   // занят другим процессом или нет прав — это норма
            }, dirs, ref errors);

            // подпапки — от самых глубоких к верхним; только пустые уйдут (reparse-точек в списке нет).
            // В общесистемном дереве приложения не убираем и пустые: там Walk отдал только заведомо
            // одноразовые файлы, а каркас папок — часть дистрибутива, по которой приложение себя чинит.
            if (string.IsNullOrEmpty(t.Mask) && !(!t.Disposable && IsAppTreePath(rootPath.ToLowerInvariant())))
            {
                dirs.Sort(delegate(string a, string b) { return b.Length.CompareTo(a.Length); });
                foreach (string d in dirs) RemoveDir(LongPath(d), own);
                if (!t.ContentsOnly) RemoveDir(LongPath(rootPath), own);
            }

            res.Errors += errors;
            res.FilesDeleted += deleted;
            return freed;
        }

        // Непустая папка отвечает ERROR_DIR_NOT_EMPTY, а не «отказано в доступе» — владение
        // берётся только там, где мешают именно права, и только у целей с TakeOwnership.
        private static void RemoveDir(string longPath, bool own)
        {
            if (Native.RemoveDirectoryW(longPath)) return;
            if (!own || Marshal.GetLastWin32Error() != Native.ERROR_ACCESS_DENIED) return;
            if (!Native.TakeOwnership(longPath)) return;
            Native.SetFileAttributesW(longPath, Native.FILE_ATTRIBUTE_NORMAL);
            Native.RemoveDirectoryW(longPath);
        }

        public CleanResult CleanCategories(List<CleanCategory> cats)
        {
            Interlocked.Increment(ref _diskWorkers);
            try { return CleanCategoriesCore(cats); }
            finally { Interlocked.Decrement(ref _diskWorkers); }
        }

        private CleanResult CleanCategoriesCore(List<CleanCategory> cats)
        {
            CleanResult res = new CleanResult();
            if (cats == null) return res;

            int ci = 0;
            foreach (CleanCategory c in cats)
            {
                if (_cancelDisk) break;
                ci++;
                DiskStatus = ci + "/" + cats.Count + "  " + c.Title;
                long catFreed = 0;
                if (!string.IsNullOrEmpty(c.Kind))
                {
                    catFreed = c.Kind == "driverstore" ? DeleteDriverPackages(c, res) : CleanComponentStore(c, res);
                    res.Freed += catFreed;
                    res.Log.Add("--- " + c.Title + ": " + FormatBytes(catFreed));
                    continue;
                }
                foreach (CleanTarget t in c.Targets)
                {
                    if (_cancelDisk) break;
                    if (!t.Enabled)
                    {
                        res.Log.Add("SKIP (off)   " + t.Path + (string.IsNullOrEmpty(t.Mask) ? "" : "  [" + t.Mask + "]"));
                        continue;
                    }
                    if (!IsAllowedTarget(t))
                    {
                        res.Log.Add("SKIP (guard) " + t.Path);
                        continue;
                    }
                    DiskStatus = ci + "/" + cats.Count + "  " + c.Title + " · " + ShortStatusPath(t.Path);
                    long f = DeleteTarget(t, res);
                    catFreed += f;
                    res.Log.Add(FormatBytes(f).PadLeft(10) + "  " + t.Path
                                + (string.IsNullOrEmpty(t.Mask) ? "" : "  [" + t.Mask + "]"));
                }
                if (c.RecycleBin && !c.BinEnabled)
                    res.Log.Add("SKIP (off)   " + Tr.S("Корзина", "Recycle Bin"));
                if (c.RecycleBin && c.BinEnabled && !_cancelDisk)
                {
                    long binSize = 0;
                    Native.SHQUERYRBINFO info = new Native.SHQUERYRBINFO();
                    info.cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO));
                    try { if (Native.SHQueryRecycleBin(null, ref info) == 0) binSize = info.i64Size; }
                    catch { }
                    try
                    {
                        Native.SHEmptyRecycleBin(IntPtr.Zero, null,
                            Native.SHERB_NOCONFIRMATION | Native.SHERB_NOPROGRESSUI | Native.SHERB_NOSOUND);
                        catFreed += binSize;
                        res.Log.Add(FormatBytes(binSize).PadLeft(10) + "  " + Tr.S("Корзина", "Recycle Bin"));
                    }
                    catch { }
                }
                res.Freed += catFreed;
                res.Log.Add("--- " + c.Title + ": " + FormatBytes(catFreed));
            }

            DiskStatus = null;
            res.Cancelled = _cancelDisk;
            if (res.Cancelled) res.Log.Add(Tr.S("=== остановлено пользователем: список пройден не до конца",
                                                "=== stopped by the user: the list was not finished"));
            if (Config.CleanLogEnabled) WriteCleanLog(res);
            return res;
        }

        // Путь для строки состояния: целиком он не влезает и прыгает по ширине.
        private static string ShortStatusPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length <= 58) return path;
            return path.Substring(0, 24) + "…" + path.Substring(path.Length - 33);
        }

        // Лог очистки — как в FluentCleaner: видно, что именно и сколько было удалено.
        private void WriteCleanLog(CleanResult res)
        {
            try
            {
                string path = Path.Combine(_dir, "clean-" + DateTime.Now.ToString("yyyy-MM") + ".log");
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("=== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
                foreach (string line in res.Log) sb.AppendLine(line);
                sb.AppendLine("TOTAL " + FormatBytes(res.Freed) + "  files=" + res.FilesDeleted
                              + "  skipped=" + res.Errors);
                sb.AppendLine();
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        public string CleanLogPath
        {
            get { return Path.Combine(_dir, "clean-" + DateTime.Now.ToString("yyyy-MM") + ".log"); }
        }
    }
}
