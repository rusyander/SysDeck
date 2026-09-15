// SysDeck — «Размеры папок»: обход, параллельный подсчёт, кэш, наблюдение за папкой, движок.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SysDeck.FolderSize
{
    internal enum RowState
    {
        Pending,     // размер ещё неизвестен — вместо числа анимация
        Measuring,   // число на экране, но подсчёт идёт: промежуточная сумма или прошлый результат. Приглушённо
        Ready,
        Partial,     // часть поддерева не прочиталась (права) — число это нижняя граница
        Failed,
    }

    internal sealed class SizeRow
    {
        public string Name;
        public string DisplayName;          // имя, которое печатает Проводник, если оно не совпадает с именем на диске
        public string FullPath;
        public bool IsDirectory;
        public FileAttributes Attributes;
        public long Bytes;
        public long Files;
        public long Directories;
        public RowState State = RowState.Pending;
        public bool FromCache;              // число на экране — из прошлого сеанса, до прихода свежего
        public double Share;                // доля от самой большой строки листинга: полоска за именем

        public bool IsHidden { get { return (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0; } }
        public bool IsReparsePoint { get { return (Attributes & FileAttributes.ReparsePoint) != 0; } }
    }

    internal struct DirStats
    {
        public long Bytes, Files, Directories;
        public bool Partial;

        public DirStats(long bytes, long files, long directories, bool partial)
        {
            Bytes = bytes; Files = files; Directories = directories; Partial = partial;
        }

        public static DirStats operator +(DirStats a, DirStats b)
        {
            return new DirStats(a.Bytes + b.Bytes, a.Files + b.Files, a.Directories + b.Directories, a.Partial || b.Partial);
        }
    }

    internal enum ScanPhase { Idle, Listing, IndexingVolume, MeasuringFolders, Done, Unavailable }

    // Что показывает шапка панели, пока идёт работа.
    internal struct ScanProgress
    {
        public ScanPhase Phase;
        public string Detail;
        public int Percent;
        public long ItemsSeen;

        public ScanProgress(ScanPhase phase, string detail, int percent, long itemsSeen)
        {
            Phase = phase; Detail = detail; Percent = percent; ItemsSeen = itemsSeen;
        }

        public static readonly ScanProgress Idle = new ScanProgress(ScanPhase.Idle, null, 0, 0);

        public bool IsBusy
        {
            get { return Phase == ScanPhase.Listing || Phase == ScanPhase.IndexingVolume || Phase == ScanPhase.MeasuringFolders; }
        }
    }

    internal static class SizeSorting
    {
        // Общий для движка и щелчков по заголовку — порядок строк всегда один и тот же.
        public static SizeRow[] Apply(IEnumerable<SizeRow> rows, SizeSortMode mode)
        {
            SizeRow[] sorted = new List<SizeRow>(rows).ToArray();
            Comparison<SizeRow> comparison;
            switch (mode)
            {
                case SizeSortMode.NameAscending:
                    comparison = delegate(SizeRow a, SizeRow b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); };
                    break;
                case SizeSortMode.ItemsDescending:
                    comparison = delegate(SizeRow a, SizeRow b) { return (b.Files + b.Directories).CompareTo(a.Files + a.Directories); };
                    break;
                default:
                    comparison = delegate(SizeRow a, SizeRow b) { return b.Bytes.CompareTo(a.Bytes); };
                    break;
            }
            Array.Sort(sorted, comparison);
            return sorted;
        }
    }

    // Свободное место тома, на котором лежит папка. GetDiskFreeSpaceEx принимает любую папку, в том числе
    // сетевую, и отвечает за точку монтирования, а не за букву: папка тома, смонтированного в C:\mnt, — это его место.
    internal sealed class VolumeSpace
    {
        public string Label;
        public long Free;
        public long Total;

        public static VolumeSpace Query(string path)
        {
            try
            {
                string directory = path.EndsWith("\\", StringComparison.Ordinal) ? path : path + "\\";
                long available, total, free;
                if (!GetDiskFreeSpaceExW(directory, out available, out total, out free) || total <= 0) return null;
                VolumeSpace space = new VolumeSpace();
                space.Label = LabelOf(path);
                space.Free = free;
                space.Total = total;
                return space;
            }
            catch (Exception ex)
            {
                FsLog.ReportOnce(ex);
                return null;
            }
        }

        // «C:» для буквы, «\\server\share» для сетевого пути.
        internal static string LabelOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            if (path.Length >= 2 && path[1] == ':') return char.ToUpperInvariant(path[0]) + ":";
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                string[] parts = path.Substring(2).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) return @"\\" + parts[0] + "\\" + parts[1];
            }
            return path;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out long lpFreeBytesAvailable, out long lpTotalNumberOfBytes,
            out long lpTotalNumberOfFreeBytes);
    }

    // Байты так, как их пишет Проводник: двоичные единицы. Точное число доступно всегда — панель
    // существует ради настоящего размера, и округлённый вид никогда не остаётся единственным.
    internal static class SizeFormat
    {
        private static readonly CultureInfo Ru = CultureInfo.GetCultureInfo("ru-RU");
        private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");
        private static readonly string[] UnitsRu = { "Б", "КБ", "МБ", "ГБ", "ТБ", "ПБ", "ЭБ" };
        private static readonly string[] UnitsEn = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };

        private static CultureInfo Culture { get { return Tr.En ? En : Ru; } }

        public static string Short(long bytes)
        {
            string[] units = Tr.En ? UnitsEn : UnitsRu;
            if (bytes < 0) return "—";
            if (bytes < 1024) return bytes.ToString("N0", Culture) + " " + units[0];
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            string number = value >= 100 ? value.ToString("N0", Culture)
                : value >= 10 ? value.ToString("N1", Culture)
                : value.ToString("N2", Culture);
            return number + " " + units[unit];
        }

        public static string ExactBytes(long bytes)
        {
            if (bytes < 0) return "—";
            return bytes.ToString("N0", Culture) + " " + Tr.S(Plural(bytes, "байт", "байта", "байт"), bytes == 1 ? "byte" : "bytes");
        }

        public static string Count(long value) { return value.ToString("N0", Culture); }

        public static string Items(long files, long dirs)
        {
            if (files == 0 && dirs == 0) return Tr.S("пусто", "empty");
            List<string> parts = new List<string>(2);
            if (files > 0) parts.Add(Count(files) + " " + Tr.S(Plural(files, "файл", "файла", "файлов"), files == 1 ? "file" : "files"));
            if (dirs > 0) parts.Add(Count(dirs) + " " + Tr.S(Plural(dirs, "папка", "папки", "папок"), dirs == 1 ? "folder" : "folders"));
            return string.Join(", ", parts.ToArray());
        }

        public static string Plural(long n, string one, string few, string many)
        {
            long mod100 = Math.Abs(n) % 100;
            if (mod100 >= 11 && mod100 <= 14) return many;
            long mod10 = mod100 % 10;
            if (mod10 == 1) return one;
            if (mod10 >= 2 && mod10 <= 4) return few;
            return many;
        }
    }

    internal sealed class ProgressCounters
    {
        private long _items;
        private long _bytes;

        public long Items { get { return Interlocked.Read(ref _items); } }
        public long Bytes { get { return Interlocked.Read(ref _bytes); } }

        public void Add(long items, long bytes)
        {
            Interlocked.Add(ref _items, items);
            Interlocked.Add(ref _bytes, bytes);
        }
    }

    // ------------------------------------------------------------------ //
    //  Обход одной папки. Универсальный источник: без прав, любая файловая система, локальная или сетевая.
    //  FindFirstFileEx с крупной выборкой — ни FileInfo на запись, ни полного пути на каждый файл.
    // ------------------------------------------------------------------ //
    internal static class DirectoryWalker
    {
        // FindFirstFileExW / FindNextFileW / FindClose и WIN32_FIND_DATA — общие, из Native.cs.
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetFullPathNameW(string lpFileName, int nBufferLength, StringBuilder lpBuffer, IntPtr lpFilePart);

        internal struct Node
        {
            public string Name;
            public bool IsDirectory;
            public long Length;
            public FileAttributes Attributes;
        }

        // Непосредственное содержимое папки — всегда быстро, чтобы панель нарисовалась до любого подсчёта.
        public static List<SizeRow> ListChildren(string path, bool showHidden)
        {
            List<SizeRow> rows = new List<SizeRow>(64);
            foreach (Node node in Enumerate(path, null))
            {
                bool hidden = (node.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;
                if (hidden && !showHidden) continue;
                string full = Combine(path, node.Name);
                SizeRow row = new SizeRow();
                row.Name = node.Name;
                // Что Проводник печатает для этой записи — «Пользователи» для Users. Без этого строку не
                // найти в его собственном списке, и ячейка «Размер» остаётся пустой.
                row.DisplayName = ShellDisplayName.For(full, node.Attributes);
                row.FullPath = full;
                row.IsDirectory = node.IsDirectory;
                row.Attributes = node.Attributes;
                row.Bytes = node.IsDirectory ? 0 : node.Length;
                row.Files = node.IsDirectory ? 0 : 1;
                row.State = node.IsDirectory ? RowState.Pending : RowState.Ready;
                rows.Add(row);
            }
            return rows;
        }

        // Одна папка: сумма её файлов и подпапки, в которые стоит спускаться. Точки соединения и символьные
        // ссылки считаются записями, но не раскрываются: их байты принадлежат тому, на что они указывают.
        public static DirStats ReadLevel(string path, CancellationToken ct, List<string> subdirs)
        {
            long bytes = 0, files = 0, dirs = 0;
            bool partial = false;
            try
            {
                foreach (Node node in Enumerate(path, null))
                {
                    ct.ThrowIfCancellationRequested();
                    if (node.IsDirectory)
                    {
                        dirs++;
                        if ((node.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        string full = Combine(path, node.Name);
                        if (IsUnwalkableDirectory(full)) { partial = true; continue; }
                        subdirs.Add(full);
                    }
                    else
                    {
                        files++;
                        bytes += node.Length;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException) { partial = true; }
            catch (IOException) { partial = true; }
            catch (System.Security.SecurityException) { partial = true; }
            return new DirStats(bytes, files, dirs, partial);
        }

        private static readonly string[] DeviceNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        // Папка, чьё имя Windows переписывает по дороге к диску, и в которую поэтому спускаться нельзя.
        // Win32 срезает хвостовые точки и пробелы у каждого компонента пути: папка с буквальным именем
        // ".. " (её создают через NT API архиваторы, WSL, сломанные скрипты) открывается как родитель
        // собственного родителя — обход идёт ВВЕРХ, находит себя снова и не кончается никогда. На C:\ это
        // оставляло одну строку считаться вечно. Имена устройств (NUL, CON, COM1…) — того же рода.
        // Обычная программа такие папки тоже не откроет, так что пропуск не прячет доступных байтов;
        // родитель помечается нижней границей.
        public static bool IsUnwalkableDirectory(string fullPath)
        {
            string name = LeafOf(fullPath);
            if (name.Length == 0) return true;
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ')
            {
                string trimmed = name.TrimEnd('.', ' ');
                if (trimmed.Length == 0) return true;
                // GetFullPathName делает ровно ту канонизацию, которую сделал бы Win32: вернувшееся без
                // изменений имя безопасно.
                string canonical = FullPathOf(fullPath);
                return canonical == null || !string.Equals(LeafOf(canonical), name, StringComparison.Ordinal);
            }
            int dot = name.IndexOf('.');
            string stem = dot < 0 ? name : name.Substring(0, dot);
            if (stem.Length < 3 || stem.Length > 4) return false;
            foreach (string device in DeviceNames)
                if (string.Equals(stem, device, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        internal static string LeafOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            int slash = path.LastIndexOf('\\');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        private static string FullPathOf(string path)
        {
            StringBuilder sb = new StringBuilder(Math.Max(260, path.Length + 16));
            int n = GetFullPathNameW(path, sb.Capacity, sb, IntPtr.Zero);
            if (n <= 0) return null;
            if (n > sb.Capacity)
            {
                sb = new StringBuilder(n + 1);
                n = GetFullPathNameW(path, sb.Capacity, sb, IntPtr.Zero);
                if (n <= 0 || n > sb.Capacity) return null;
            }
            return sb.ToString();
        }

        internal static string Combine(string dir, string name)
        {
            return dir.Length > 0 && dir[dir.Length - 1] == '\\' ? dir + name : dir + "\\" + name;
        }

        // Длинный путь — с префиксом \\?\: без него FindFirstFile дальше 260 символов не пройдёт.
        private static string SearchPattern(string dir)
        {
            string pattern = Combine(dir, "*");
            if (pattern.Length < 250 || pattern.StartsWith(@"\\?\", StringComparison.Ordinal)) return pattern;
            if (pattern.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + pattern.Substring(2);
            return @"\\?\" + pattern;
        }

        internal static IEnumerable<Node> Enumerate(string dir, object unused)
        {
            Native.WIN32_FIND_DATA data;
            IntPtr handle = Native.FindFirstFileExW(SearchPattern(dir), Native.FindExInfoBasic, out data, Native.FindExSearchNameMatch,
                IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
            if (handle == Native.INVALID_HANDLE_VALUE)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 2 || error == 18) yield break;                   // пусто (корень пустого тома)
                throw ErrorFor(error, dir);
            }
            try
            {
                while (true)
                {
                    string name = data.cFileName;
                    if (name != "." && name != "..")
                    {
                        Node node = new Node();
                        node.Name = name;
                        node.Attributes = (FileAttributes)data.dwFileAttributes;
                        node.IsDirectory = (data.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0;
                        node.Length = node.IsDirectory ? 0 : ((long)data.nFileSizeHigh << 32) | data.nFileSizeLow;
                        yield return node;
                    }
                    if (!Native.FindNextFileW(handle, out data))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 18) yield break;
                        throw ErrorFor(error, dir);
                    }
                }
            }
            finally { Native.FindClose(handle); }
        }

        private static Exception ErrorFor(int error, string dir)
        {
            string message = new System.ComponentModel.Win32Exception(error).Message + " (" + dir + ")";
            if (error == 5) return new UnauthorizedAccessException(message);
            if (error == 3) return new DirectoryNotFoundException(message);
            return new IOException(message, error);
        }
    }

    // ------------------------------------------------------------------ //
    //  Несколько папок сразу из ОДНОЙ общей очереди каталогов.
    //  Очевидная форма — папка за папкой, каждая параллельно внутри — на системном диске выглядит сломанной:
    //  C:\Windows минутами занимает все потоки, а остальные строки крутят анимацию. Здесь поток берёт
    //  следующий каталог у того корня, которому он нужен, — все строки растут одновременно.
    //  Очередь — стек: обход в глубину держит в памяти пути пропорционально глубине дерева, а не ширине.
    // ------------------------------------------------------------------ //
    internal static class TreeMeasurer
    {
        // Перечисление больше ждёт диск, чем считает, — потоков больше ядер выгодно, но до предела:
        // перегруженный механический диск становится медленнее, а не быстрее.
        public static readonly int Workers = FsMath.Clamp(Environment.ProcessorCount, 4, 16);

        // Страховка, а не настоящий предел: известные способы построить бесконечное дерево отсечены в
        // DirectoryWalker, это — на случай драйвера, сетевой шары или новой причуды Windows.
        private const int MaxDepth = 256;

        // Одна измеряемая папка. Счётчики живые: интерфейс читает их, пока идёт работа.
        internal sealed class Job
        {
            public SizeRow Row;
            internal long BytesSeen, FilesSeen, DirsSeen;
            internal int Outstanding, PartialFlag, DoneFlag;

            public long Bytes { get { return Interlocked.Read(ref BytesSeen); } }
            public long Files { get { return Interlocked.Read(ref FilesSeen); } }
            public long Directories { get { return Interlocked.Read(ref DirsSeen); } }
            public bool Partial { get { return Volatile.Read(ref PartialFlag) != 0; } }
            public bool Done { get { return Volatile.Read(ref DoneFlag) != 0; } }
            public DirStats Stats { get { return new DirStats(Bytes, Files, Directories, Partial); } }
        }

        private struct WorkItem
        {
            public Job Owner;
            public string Directory;
            public int Depth;
            public WorkItem(Job owner, string directory, int depth) { Owner = owner; Directory = directory; Depth = depth; }
        }

        // Блокирует до конца всех заданий или отмены. onJobDone вызывается на рабочем потоке в тот момент,
        // когда одна папка досчитана: так маленькая папка не ждёт большую рядом.
        public static void Run(IList<Job> jobs, ProgressCounters counters, Action<Job> onJobDone, CancellationToken ct)
        {
            if (jobs.Count == 0) return;
            new Session(jobs, counters, onJobDone, ct).Execute();
            ct.ThrowIfCancellationRequested();
        }

        private sealed class Session
        {
            private readonly ConcurrentStack<WorkItem> _work = new ConcurrentStack<WorkItem>();
            private readonly SemaphoreSlim _available = new SemaphoreSlim(0);
            private readonly IList<Job> _jobs;
            private readonly ProgressCounters _counters;
            private readonly Action<Job> _onJobDone;
            private readonly CancellationToken _ct;
            private int _outstanding;
            private int _finished;

            public Session(IList<Job> jobs, ProgressCounters counters, Action<Job> onJobDone, CancellationToken ct)
            {
                _jobs = jobs; _counters = counters; _onJobDone = onJobDone; _ct = ct;
            }

            public void Execute()
            {
                foreach (Job job in _jobs)
                {
                    job.Outstanding = 1;
                    _outstanding++;
                    _work.Push(new WorkItem(job, job.Row.FullPath, 0));
                }
                using (_ct.Register(WakeEveryone))
                {
                    _available.Release(_jobs.Count);
                    Task[] workers = new Task[Workers];
                    // Отдельные потоки, а не пул: рабочий всю жизнь ждёт на семафоре, и так припаркованные
                    // потоки пула морят голодом всё остальное в процессе.
                    for (int i = 0; i < workers.Length; i++)
                        workers[i] = Task.Factory.StartNew(Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    try { Task.WaitAll(workers); }
                    finally { _available.Dispose(); }
                }
            }

            private void WakeEveryone()
            {
                if (Interlocked.Exchange(ref _finished, 1) != 0) return;
                try { _available.Release(Workers); } catch (ObjectDisposedException) { }
            }

            private void Worker()
            {
                while (true)
                {
                    _available.Wait();
                    if (Volatile.Read(ref _finished) != 0) return;
                    WorkItem item;
                    if (!_work.TryPop(out item)) continue;
                    try { Process(item); }
                    catch (OperationCanceledException) { }    // уход из папки посреди обхода — норма, не сбой
                    catch (Exception ex)
                    {
                        FsLog.Report(ex);
                        Volatile.Write(ref item.Owner.PartialFlag, 1);
                    }
                    if (Interlocked.Decrement(ref item.Owner.Outstanding) == 0)
                    {
                        Volatile.Write(ref item.Owner.DoneFlag, 1);
                        if (_onJobDone != null)
                        {
                            try { _onJobDone(item.Owner); } catch (Exception ex) { FsLog.Report(ex); }
                        }
                    }
                    if (Interlocked.Decrement(ref _outstanding) == 0) WakeEveryone();
                }
            }

            private void Process(WorkItem item)
            {
                List<string> subdirs = new List<string>();
                DirStats own = DirectoryWalker.ReadLevel(item.Directory, _ct, subdirs);
                Interlocked.Add(ref item.Owner.BytesSeen, own.Bytes);
                Interlocked.Add(ref item.Owner.FilesSeen, own.Files);
                Interlocked.Add(ref item.Owner.DirsSeen, own.Directories);
                if (own.Partial) Volatile.Write(ref item.Owner.PartialFlag, 1);
                if (_counters != null) _counters.Add(own.Files + own.Directories, own.Bytes);
                if (subdirs.Count == 0 || _ct.IsCancellationRequested) return;
                if (item.Depth >= MaxDepth)
                {
                    Volatile.Write(ref item.Owner.PartialFlag, 1);
                    return;
                }
                // Дети засчитываются ДО того, как этот каталог вычтен, — иначе задание на миг выглядело бы
                // законченным и опубликовало бы число, которое ещё растёт.
                Interlocked.Add(ref item.Owner.Outstanding, subdirs.Count);
                Interlocked.Add(ref _outstanding, subdirs.Count);
                foreach (string dir in subdirs) _work.Push(new WorkItem(item.Owner, dir, item.Depth + 1));
                try { _available.Release(subdirs.Count); } catch (ObjectDisposedException) { }
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Имя, которое печатает Проводник, — не всегда имя на диске.
    //  На русской Windows список пишет «Пользователи», «Документы», «Загрузки», «Рабочий стол», а папки
    //  называются Users, Documents, Downloads, Desktop. Сопоставление по имени на диске оставляло пустыми
    //  ровно эти ячейки. Оболочка подставляет имя только папкам с ReadOnly или System (для них она и читает
    //  desktop.ini), поэтому дорогой вызов идёт для горстки папок на листинг.
    // ------------------------------------------------------------------ //
    internal static class ShellDisplayName
    {
        private const uint SHGFI_DISPLAYNAME = 0x000000200;
        private const int CacheLimit = 4096;
        private static readonly ConcurrentDictionary<string, string> Cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string For(string fullPath, FileAttributes attributes)
        {
            if ((attributes & (FileAttributes.ReadOnly | FileAttributes.System)) == 0) return null;
            string cached;
            if (Cache.TryGetValue(fullPath, out cached)) return cached.Length == 0 ? null : cached;
            string resolved = Query(fullPath);
            string name = DirectoryWalker.LeafOf(fullPath.TrimEnd('\\'));
            if (string.IsNullOrEmpty(resolved) || string.Equals(resolved, name, StringComparison.Ordinal)) resolved = null;
            if (Cache.Count > CacheLimit) Cache.Clear();
            Cache[fullPath] = resolved ?? "";
            return resolved;
        }

        private static string Query(string fullPath)
        {
            try
            {
                SHFILEINFO info = new SHFILEINFO();
                IntPtr result = SHGetFileInfoW(fullPath, 0, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), SHGFI_DISPLAYNAME);
                return result == IntPtr.Zero ? null : info.szDisplayName;
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
                return null;
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfoW(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
    }

    // ------------------------------------------------------------------ //
    //  Запомненные размеры между запусками. C:\Windows считается минутами, и число почти всегда то же.
    //  Прошлый результат показывается сразу — заметно как прошлый — пока за ним идёт свежий подсчёт.
    //  Ничто отсюда не выдаётся за текущее.
    // ------------------------------------------------------------------ //
    internal sealed class SizeCacheStore
    {
        private const int MaxEntries = 8000;
        private static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
        private static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        private readonly ConcurrentDictionary<string, Entry> _entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly string _file;
        private int _dirty;

        internal struct Entry
        {
            public long Bytes, Files, Directories;
            public bool Partial;
            public DateTime AtUtc;
        }

        public SizeCacheStore(string file) { _file = file ?? FsPaths.SizesFile; }

        public int Count { get { return _entries.Count; } }

        public bool TryGet(string path, out Entry entry)
        {
            if (!_entries.TryGetValue(path, out entry)) return false;
            if (DateTime.UtcNow - entry.AtUtc <= MaxAge) return true;
            Entry removed;
            _entries.TryRemove(path, out removed);
            return false;
        }

        public void Put(string path, DirStats stats) { Put(path, stats, DateTime.UtcNow); }

        internal void Put(string path, DirStats stats, DateTime atUtc)
        {
            Entry e = new Entry();
            e.Bytes = stats.Bytes; e.Files = stats.Files; e.Directories = stats.Directories; e.Partial = stats.Partial; e.AtUtc = atUtc;
            _entries[path] = e;
            Volatile.Write(ref _dirty, 1);
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_file)) return;
                JVal root = Jsn.Parse(File.ReadAllText(_file));
                if (root == null || root.Kind != JKind.Arr) return;
                DateTime cutoff = DateTime.UtcNow - MaxAge;
                foreach (JVal r in root.V)
                {
                    if (r == null || r.Kind != JKind.Obj) continue;
                    string path = r.GetStr("p");
                    if (string.IsNullOrEmpty(path)) continue;
                    DateTime at = Epoch.AddSeconds(Num(r, "t"));
                    if (at < cutoff) continue;
                    Entry e = new Entry();
                    e.Bytes = Num(r, "b"); e.Files = Num(r, "f"); e.Directories = Num(r, "d");
                    JVal x = r.Get("x");
                    e.Partial = x != null && x.Kind == JKind.Bool && x.B;
                    e.AtUtc = at;
                    _entries[path] = e;
                }
            }
            catch (Exception ex)
            {
                // Битый кэш не стоит ни слова пользователю: он стоит одного медленного подсчёта.
                FsLog.Report(ex);
            }
        }

        public void Save()
        {
            if (Interlocked.Exchange(ref _dirty, 0) == 0) return;
            try
            {
                List<KeyValuePair<string, Entry>> all = new List<KeyValuePair<string, Entry>>(_entries);
                all.Sort(delegate(KeyValuePair<string, Entry> a, KeyValuePair<string, Entry> b) { return b.Value.AtUtc.CompareTo(a.Value.AtUtc); });
                JVal arr = JVal.NewArr();
                for (int i = 0; i < all.Count && i < MaxEntries; i++)
                {
                    Entry e = all[i].Value;
                    JVal o = JVal.NewObj();
                    o.Set("p", JVal.NewStr(all[i].Key));
                    o.Set("b", JVal.NewNum(e.Bytes.ToString(CultureInfo.InvariantCulture)));
                    o.Set("f", JVal.NewNum(e.Files.ToString(CultureInfo.InvariantCulture)));
                    o.Set("d", JVal.NewNum(e.Directories.ToString(CultureInfo.InvariantCulture)));
                    JVal x = new JVal(); x.Kind = JKind.Bool; x.B = e.Partial;
                    o.Set("x", x);
                    o.Set("t", JVal.NewNum(((long)(e.AtUtc - Epoch).TotalSeconds).ToString(CultureInfo.InvariantCulture)));
                    arr.V.Add(o);
                }
                // Рядом и на место: обрыв питания посреди записи не должен оставить половину кэша,
                // потому что половина кэша читается как целый.
                FsPaths.WriteAtomic(_file, Jsn.Write(arr));
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
                Volatile.Write(ref _dirty, 1);
            }
        }

        private static long Num(JVal o, string name)
        {
            long v;
            string s = o.GetStr(name);
            return s != null && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Что случилось в показанной папке. Structural — появился, исчез или переименован ребёнок: листинг
    //  строится заново. ResizedFiles — файлы, которые лишь выросли или уменьшились: новое число нужно
    //  только их строкам.
    // ------------------------------------------------------------------ //
    internal sealed class FolderChange
    {
        public bool Structural;
        public string[] ResizedFiles;
    }

    // Сообщает движку, что папка на экране уже не та, что он измерил. С задержкой: одно копирование — тысячи событий.
    internal sealed class FolderChangeWatcher : IDisposable
    {
        private const int DebounceMs = 1500;
        private readonly Timer _debounce;
        private readonly object _gate = new object();
        private readonly HashSet<string> _resized = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _structural;
        private FileSystemWatcher _watcher;
        private string _path;
        private bool _disposed;

        public event Action<FolderChange> Changed;    // на фоновом потоке

        public FolderChangeWatcher()
        {
            _debounce = new Timer(delegate { Fire(); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        private void Fire()
        {
            FolderChange change = new FolderChange();
            lock (_gate)
            {
                change.Structural = _structural;
                change.ResizedFiles = new List<string>(_resized).ToArray();
                _structural = false;
                _resized.Clear();
            }
            if (!change.Structural && change.ResizedFiles.Length == 0) return;
            Action<FolderChange> handler = Changed;
            if (handler != null) handler(change);
        }

        public void Watch(string path)
        {
            if (_disposed || string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
            _path = path;
            Stop();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try
            {
                // Намеренно НЕ рекурсивно. C:\ с подпапками — событие на каждую запись где угодно на системном
                // диске: журналы, кэши браузеров, временные файлы, несколько раз в секунду вечно. Важна папка
                // на экране: её собственные дети. Размеры глубже подхватит следующий заход или «Обновить».
                _watcher = new FileSystemWatcher(path);
                _watcher.IncludeSubdirectories = false;
                _watcher.InternalBufferSize = 64 * 1024;
                _watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size;
                _watcher.Created += OnStructure;
                _watcher.Deleted += OnStructure;
                _watcher.Renamed += OnRenamed;
                _watcher.Changed += OnContent;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                // Сетевые шары и папки, исчезнувшие посреди настройки: без наблюдателя теряется свежесть, не точность.
                FsLog.Report(ex);
                Stop();
            }
        }

        private void OnStructure(object sender, FileSystemEventArgs e)
        {
            lock (_gate) _structural = true;
            Schedule();
        }

        private void OnRenamed(object sender, RenamedEventArgs e) { OnStructure(sender, e); }

        // Изменение размера — и причина, по которой панель нельзя было оставить открытой на системном диске:
        // Windows непрерывно пишет pagefile.sys, hiberfil.sys и swapfile.sys, и C:\ давал событие каждую
        // секунду-две. Считать это «папка изменилась» значило пересчитывать весь диск вечно — около 29 секунд
        // процессора каждые полминуты на простаивающей машине. Нерекурсивный наблюдатель сообщает только о
        // ПРЯМОМ ребёнке, так что устарело число одного файла: строка обновляется по его длине, без обхода.
        private void OnContent(object sender, FileSystemEventArgs e)
        {
            lock (_gate)
            {
                if (Directory.Exists(e.FullPath)) _structural = true;
                else _resized.Add(e.FullPath);
            }
            Schedule();
        }

        private void OnError(object sender, ErrorEventArgs e)
        {
            // Переполнение буфера: изменений слишком много, чтобы описать по одному, — «изменилось всё».
            lock (_gate) _structural = true;
            Schedule();
        }

        private void Schedule()
        {
            if (_disposed) return;
            try { _debounce.Change(DebounceMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private void Stop()
        {
            if (_watcher == null) return;
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        public void Dispose()
        {
            _disposed = true;
            Stop();
            _debounce.Dispose();
        }
    }

    internal sealed class ListingSnapshot
    {
        public string Path;
        public IList<SizeRow> Rows;
        public bool Final;
        public bool FastMode;
        public DirStats Totals;
        public string Error;
    }
}
