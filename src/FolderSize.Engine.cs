// SysDeck — размеры папок: SizeEngine (обход, кэш, публикация) и индекс тома.
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
    // ------------------------------------------------------------------ //
    //  «Проводник показывает эту папку» → строки с настоящими размерами.
    //  Порядок источников: измеренное секунды назад (мгновенно) → индекс NTFS (секунды на весь диск) →
    //  подсчёт всех папок параллельно (работает всегда). Числа прошлого сеанса заполняют экран, пока идёт
    //  последний. Каждый шаг отменяем: уход из папки не оставляет работу крутиться за панелью.
    // ------------------------------------------------------------------ //
    internal sealed class SizeEngine : IDisposable
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
        private const int PublishIntervalMs = 200;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(20);

        private readonly FsSettings _settings;
        private readonly VolumeIndexProvider _indexes = new VolumeIndexProvider();
        private readonly FolderChangeWatcher _watcher = new FolderChangeWatcher();
        private readonly SizeCacheStore _store;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly SynchronizationContext _ui;

        private CancellationTokenSource _cts;
        private string _path;
        private List<SizeRow> _rows;
        private bool _scanning;
        private bool _dirty;
        private DateTime _savedAtUtc = DateTime.MinValue;
        private bool _disposed;

        public event Action<ScanProgress> ProgressChanged;
        public event Action<ListingSnapshot> RowsChanged;

        private struct CacheEntry
        {
            public DirStats Stats;
            public DateTime AtUtc;
        }

        public SizeEngine(FsSettings settings) : this(settings, null) { }

        internal SizeEngine(FsSettings settings, string storeFile)
        {
            _settings = settings;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _store = new SizeCacheStore(storeFile);
            _watcher.Changed += OnFolderChanged;
            _store.Load();
        }

        public void ShowFolder(string path, bool force)
        {
            if (_disposed) return;
            if (!force && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
            _path = path;
            Restart();
        }

        public void ShowFolder(string path) { ShowFolder(path, false); }

        public void Refresh()
        {
            _cache.Clear();
            _indexes.InvalidateAll();
            Restart();
        }

        // Что-то изменилось в папке на экране. Перезапуск идущего подсчёта здесь делал системный диск
        // невозможным: на C:\ что-то пишется всегда, и каждое событие выбрасывало минуты счёта. Идущий
        // подсчёт не трогаем и повторяем один раз после, если он мерил устаревшее. Файл, лишь сменивший
        // размер, чинится даром — неверна может быть только его строка.
        private void OnFolderChanged(FolderChange change)
        {
            _ui.Post(delegate
            {
                if (_disposed) return;
                if (!change.Structural)
                {
                    RefreshFileRows(change.ResizedFiles);
                    return;
                }
                if (_scanning)
                {
                    FsLog.Trace("folder changed while scanning — queued, scan left running");
                    _dirty = true;
                    return;
                }
                FsLog.Trace("folder changed — rescanning");
                _cache.Clear();
                InvalidateIndexForCurrentPath();
                Restart();
            }, null);
        }

        // Новая длина для строк-файлов прямо из файловой системы — без обхода.
        private void RefreshFileRows(string[] paths)
        {
            List<SizeRow> rows = _rows;
            if (rows == null || paths.Length == 0 || _path == null) return;
            bool touched = false;
            foreach (string file in paths)
            {
                SizeRow row = null;
                foreach (SizeRow r in rows)
                    if (!r.IsDirectory && string.Equals(r.FullPath, file, StringComparison.OrdinalIgnoreCase)) { row = r; break; }
                if (row == null) continue;
                try
                {
                    long length = new FileInfo(file).Length;
                    if (length == row.Bytes) continue;
                    row.Bytes = length;
                    touched = true;
                }
                catch (IOException) { }                 // удалён или занят между событием и этим местом
                catch (UnauthorizedAccessException) { }
            }
            if (!touched) return;
            FsLog.Trace("file sizes refreshed without a scan: " + paths.Length);
            Publish(Snapshot(_path, rows, !_scanning, false));
        }

        private void InvalidateIndexForCurrentPath()
        {
            if (_path == null) return;
            string root = SafeRoot(_path);
            if (!string.IsNullOrEmpty(root)) _indexes.Invalidate(root[0]);
        }

        internal static string SafeRoot(string path)
        {
            try { return Path.GetPathRoot(path); }
            catch { return null; }
        }

        private void Restart()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            _dirty = false;
            string path = _path;
            _watcher.Watch(path);
            if (string.IsNullOrEmpty(path))
            {
                _scanning = false;
                ListingSnapshot empty = new ListingSnapshot();
                empty.Rows = new SizeRow[0];
                empty.Final = true;
                Publish(empty);
                Report(ScanProgress.Idle);
                return;
            }
            CancellationTokenSource cts = new CancellationTokenSource();
            _cts = cts;
            _scanning = true;
            CancellationToken token = cts.Token;
            Task.Factory.StartNew(delegate { Run(path, token); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        private void Run(string path, CancellationToken ct)
        {
            ProgressCounters counters = new ProgressCounters();
            try
            {
                Report(new ScanProgress(ScanPhase.Listing, path, 0, 0));
                List<SizeRow> rows;
                try
                {
                    rows = DirectoryWalker.ListChildren(path, _settings.ShowHidden);
                }
                catch (Exception ex)
                {
                    if (!(ex is UnauthorizedAccessException || ex is IOException)) throw;
                    ListingSnapshot failed = new ListingSnapshot();
                    failed.Path = path;
                    failed.Rows = new SizeRow[0];
                    failed.Final = true;
                    failed.Error = ex is UnauthorizedAccessException ? Tr.S("Нет доступа к этой папке", "No access to this folder")
                                                                      : Tr.S("Папка недоступна", "The folder is unavailable");
                    Publish(failed);
                    Report(new ScanProgress(ScanPhase.Unavailable, ex.Message, 0, 0));
                    return;
                }
                ct.ThrowIfCancellationRequested();

                // Хранится, чтобы подросшему файлу можно было обновить строку без пересчёта.
                _rows = rows;

                List<SizeRow> pending = new List<SizeRow>();
                foreach (SizeRow r in rows) if (r.IsDirectory) pending.Add(r);

                // Точка соединения или символьная ссылка — второе имя дерева, живущего в другом месте. Здесь
                // C:\c — ссылка на сам C:\, обход считал весь диск дважды и показывал полтерабайта, которых нет.
                // Ссылки перечисляются, но не раскрываются. Папка с именем, которое Win32 переписывает
                // (".. ", "NUL"), отвергается по той же причине.
                foreach (SizeRow r in pending)
                    if (r.IsReparsePoint || DirectoryWalker.IsUnwalkableDirectory(r.FullPath)) r.State = RowState.Ready;
                pending.RemoveAll(delegate(SizeRow r) { return r.State != RowState.Pending; });

                ApplyFreshCache(pending);
                pending.RemoveAll(delegate(SizeRow r) { return r.State != RowState.Pending; });

                // Числа прошлого сеанса — на экран сразу, приглушённо, до прихода настоящих: папка, которая
                // считается две минуты, не должна две минуты быть пустой строкой.
                ApplyStoredNumbers(pending);

                Publish(Snapshot(path, rows, pending.Count == 0, false));
                if (pending.Count == 0)
                {
                    Report(new ScanProgress(ScanPhase.Done, null, 100, 0));
                    return;
                }
                if (TryIndex(path, pending, rows, ct)) return;
                Measure(path, rows, pending, counters, ct);
            }
            catch (OperationCanceledException) { }       // уход из папки — обычный конец подсчёта
            catch (Exception ex)
            {
                FsLog.Report(ex);
                Report(new ScanProgress(ScanPhase.Unavailable, ex.Message, 0, 0));
            }
            finally
            {
                if (!ct.IsCancellationRequested) _ui.Post(delegate { FinishScan(); }, null);
            }
        }

        // Индекс NTFS отвечает за весь диск за секунды — когда он вообще доступен.
        private bool TryIndex(string path, List<SizeRow> pending, List<SizeRow> rows, CancellationToken ct)
        {
            if (!_settings.UseFastNtfsIndex || !VolumeIndexProvider.CanEverWork(path)) return false;
            string root = SafeRoot(path);
            Report(new ScanProgress(ScanPhase.IndexingVolume, root, 0, 0));
            NtfsVolumeIndex index = _indexes.Get(path, delegate(int p) { Report(new ScanProgress(ScanPhase.IndexingVolume, root, p, 0)); }, ct);
            uint dirIndex;
            if (index == null || !index.TryResolveDirectory(path, out dirIndex)) return false;
            foreach (SizeRow row in pending)
            {
                DirTotals totals;
                if (!index.TryGetChildTotals(dirIndex, row.Name, out totals)) continue;
                Remember(row, new DirStats(totals.Bytes, totals.Files, totals.Directories, false));
            }
            pending.RemoveAll(delegate(SizeRow r) { return r.State == RowState.Ready || r.State == RowState.Partial; });
            if (pending.Count > 0) return false;
            Publish(Snapshot(path, rows, true, true));
            Report(new ScanProgress(ScanPhase.Done, null, 100, 0));
            return true;
        }

        // Всё, на что индекс не ответил (нет прав, точка монтирования, не NTFS), считается обычным образом,
        // но все папки сразу, а не одна за другой.
        private void Measure(string path, List<SizeRow> rows, List<SizeRow> pending, ProgressCounters counters, CancellationToken ct)
        {
            List<TreeMeasurer.Job> jobs = new List<TreeMeasurer.Job>(pending.Count);
            foreach (SizeRow row in pending)
            {
                TreeMeasurer.Job job = new TreeMeasurer.Job();
                job.Row = row;
                jobs.Add(job);
            }
            int completed = 0;
            Stopwatch clock = Stopwatch.StartNew();
            FsLog.Trace("measure start " + path + " folders=" + jobs.Count + " workers=" + TreeMeasurer.Workers);
            Report(new ScanProgress(ScanPhase.MeasuringFolders, "0/" + jobs.Count, 0, 0));

            using (Timer ticker = new Timer(delegate
            {
                foreach (TreeMeasurer.Job job in jobs)
                {
                    // Число из кэша стоит, пока не досчитан свой подсчёт: промежуточная сумма с нуля
                    // выглядела бы как уменьшившаяся папка.
                    if (job.Done || job.Row.FromCache) continue;
                    if (job.Files + job.Directories == 0) continue;
                    job.Row.Bytes = job.Bytes;
                    job.Row.Files = job.Files;
                    job.Row.Directories = job.Directories;
                    job.Row.State = RowState.Measuring;
                }
                Publish(Snapshot(path, rows, false, false));
                Report(new ScanProgress(ScanPhase.MeasuringFolders, Volatile.Read(ref completed) + "/" + jobs.Count, 0, counters.Items));
            }, null, PublishIntervalMs, PublishIntervalMs))
            {
                TreeMeasurer.Run(jobs, counters, delegate(TreeMeasurer.Job job)
                {
                    Remember(job.Row, job.Stats);
                    int done = Interlocked.Increment(ref completed);
                    FsLog.Trace("  " + done + "/" + jobs.Count + " " + job.Row.Name + " = " + job.Bytes + " B, "
                                + job.Files + " files, " + job.Directories + " dirs at " + clock.ElapsedMilliseconds + " ms");
                }, ct);
            }
            ct.ThrowIfCancellationRequested();
            FsLog.Trace("measure done " + path + " in " + clock.ElapsedMilliseconds + " ms, " + counters.Items + " objects");
            Publish(Snapshot(path, rows, true, false));
            Report(new ScanProgress(ScanPhase.Done, null, 100, counters.Items));
        }

        // Снова на потоке интерфейса, когда подсчёт кончился: сохранить кэш, повторить, если он устарел.
        private void FinishScan()
        {
            if (_disposed) return;
            _scanning = false;
            if (DateTime.UtcNow - _savedAtUtc > SaveInterval)
            {
                _savedAtUtc = DateTime.UtcNow;
                SizeCacheStore store = _store;
                Task.Factory.StartNew(delegate { store.Save(); });
            }
            if (!_dirty) return;
            _dirty = false;
            _cache.Clear();
            InvalidateIndexForCurrentPath();
            Restart();
        }

        private void ApplyFreshCache(List<SizeRow> rows)
        {
            DateTime now = DateTime.UtcNow;
            foreach (SizeRow row in rows)
            {
                CacheEntry entry;
                if (_cache.TryGetValue(row.FullPath, out entry) && now - entry.AtUtc < CacheTtl) Apply(row, entry.Stats);
            }
        }

        private void ApplyStoredNumbers(List<SizeRow> rows)
        {
            foreach (SizeRow row in rows)
            {
                SizeCacheStore.Entry entry;
                if (!_store.TryGet(row.FullPath, out entry)) continue;
                row.Bytes = entry.Bytes;
                row.Files = entry.Files;
                row.Directories = entry.Directories;
                row.State = RowState.Measuring;
                row.FromCache = true;
            }
        }

        private void Remember(SizeRow row, DirStats stats)
        {
            Apply(row, stats);
            CacheEntry entry = new CacheEntry();
            entry.Stats = stats;
            entry.AtUtc = DateTime.UtcNow;
            _cache[row.FullPath] = entry;
            _store.Put(row.FullPath, stats);
        }

        private static void Apply(SizeRow row, DirStats stats)
        {
            row.Bytes = stats.Bytes;
            row.Files = stats.Files;
            row.Directories = stats.Directories;
            row.FromCache = false;
            row.State = stats.Partial ? RowState.Partial : RowState.Ready;
        }

        internal ListingSnapshot Snapshot(string path, List<SizeRow> rows, bool final, bool fast)
        {
            // Строки переупорядочиваются только когда всё известно: пересортировка во время прихода
            // размеров заставляла бы список прыгать под курсором.
            IList<SizeRow> ordered = final ? (IList<SizeRow>)SizeSorting.Apply(rows, _settings.Sort) : rows.ToArray();
            long max = 0;
            DirStats totals = new DirStats();
            foreach (SizeRow row in ordered)
            {
                if (row.Bytes > max) max = row.Bytes;
                totals += new DirStats(row.Bytes, row.IsDirectory ? row.Files : 1, row.IsDirectory ? row.Directories + 1 : 0, false);
            }
            foreach (SizeRow row in ordered) row.Share = max > 0 ? (double)row.Bytes / max : 0;
            ListingSnapshot s = new ListingSnapshot();
            s.Path = path;
            s.Rows = ordered;          // уже копия: интерфейс не должен видеть список, который ещё меняется
            s.Final = final;
            s.FastMode = fast;
            s.Totals = totals;
            return s;
        }

        private void Publish(ListingSnapshot snapshot)
        {
            _ui.Post(delegate
            {
                Action<ListingSnapshot> handler = RowsChanged;
                if (handler != null) handler(snapshot);
            }, null);
        }

        private void Report(ScanProgress progress)
        {
            _ui.Post(delegate
            {
                Action<ScanProgress> handler = ProgressChanged;
                if (handler != null) handler(progress);
            }, null);
        }

        public void Dispose()
        {
            _disposed = true;
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _watcher.Dispose();
            _indexes.Dispose();
            _store.Save();
        }
    }

    // ------------------------------------------------------------------ //
    //  Один индекс NTFS на диск: строится не больше одного раза за раз и помнит, когда диск его дать не
    //  может (нет прав, не NTFS, необычная $MFT), чтобы панель перестала спрашивать.
    // ------------------------------------------------------------------ //
    internal sealed class VolumeIndexProvider : IDisposable
    {
        private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);
        private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

        private sealed class Slot
        {
            public readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
            public NtfsVolumeIndex Index;
            public DateTime FailedAtUtc = DateTime.MinValue;
            public string FailureReason;
            public bool Dirty;
        }

        private readonly ConcurrentDictionary<char, Slot> _slots = new ConcurrentDictionary<char, Slot>();
        private bool _disposed;

        public string LastFailureReason { get; private set; }

        public static bool CanEverWork(string path) { return Elevation.IsElevated && RawVolume.LooksSupported(path); }

        public void Invalidate(char drive)
        {
            Slot slot;
            if (_slots.TryGetValue(char.ToUpperInvariant(drive), out slot)) slot.Dirty = true;
        }

        public void InvalidateAll()
        {
            foreach (Slot slot in _slots.Values) slot.Dirty = true;
        }

        // Годный индекс либо null, когда этот диск прямо сейчас его дать не может.
        public NtfsVolumeIndex Get(string path, Action<int> progress, CancellationToken ct)
        {
            if (_disposed || !CanEverWork(path)) return null;
            string root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch { return null; }
            if (string.IsNullOrEmpty(root)) return null;
            char drive = char.ToUpperInvariant(root[0]);
            Slot slot = _slots.GetOrAdd(drive, delegate { return new Slot(); });
            if (slot.FailedAtUtc != DateTime.MinValue && DateTime.UtcNow - slot.FailedAtUtc < RetryAfterFailure)
            {
                LastFailureReason = slot.FailureReason;
                return null;
            }
            slot.Gate.Wait(ct);
            try
            {
                bool fresh = slot.Index != null && !slot.Dirty && DateTime.UtcNow - slot.Index.BuiltAtUtc < MaxAge;
                if (fresh) return slot.Index;
                NtfsVolumeIndex index = MftIndexBuilder.Build(drive, progress, ct);
                slot.Index = index;
                slot.Dirty = false;
                slot.FailedAtUtc = DateTime.MinValue;
                slot.FailureReason = null;
                LastFailureReason = null;
                return index;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Любая неожиданность здесь не смертельна: обход даёт те же числа, только медленнее.
                FsLog.Report(ex);
                slot.FailedAtUtc = DateTime.UtcNow;
                slot.FailureReason = ex.Message;
                LastFailureReason = ex.Message;
                return null;
            }
            finally
            {
                try { slot.Gate.Release(); } catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _slots.Clear();
        }
    }
}
