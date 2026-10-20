// SysDeck — размеры папок: SizeEngine (карта тома или обход, кэш, публикация).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SysDeck.FolderSize
{
    // ------------------------------------------------------------------ //
    //  «Проводник показывает эту папку» → строки с настоящими размерами.
    //  Локальный диск: числа берутся из карты тома (FolderSize.Map.cs) — она строится один раз в фоне и
    //  дальше обновляется только там, где что-то изменилось, так что заход в папку не трогает диск вовсе.
    //  Сеть и съёмные диски: подсчёт по заходу, в фоновом режиме ввода-вывода. Числа прошлого сеанса
    //  заполняют экран, пока нет настоящих. Каждый шаг отменяем: уход из папки не оставляет работу за панелью.
    // ------------------------------------------------------------------ //
    internal sealed class SizeEngine : IDisposable
    {
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);
        private const int PublishIntervalMs = 200;
        private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan MapSaveInterval = TimeSpan.FromMinutes(2);

        private readonly FsSettings _settings;
        private readonly VolumeMaps _maps;
        private readonly FolderChangeWatcher _watcher = new FolderChangeWatcher();
        private readonly SizeCacheStore _store;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache = new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly SynchronizationContext _ui;

        // Что сейчас показано из карты — его перечитывает каждое изменение карты.
        private readonly object _served = new object();
        private VolumeMap _servedMap;
        private string _servedPath;
        private List<SizeRow> _servedRows;
        private List<SizeRow> _servedDirs;
        private CancellationToken _servedToken;
        private bool _servedFinal;

        private CancellationTokenSource _cts;
        private string _path;
        private List<SizeRow> _rows;
        private bool _scanning;
        private bool _dirty;
        private bool _mapsMft;
        private DateTime _savedAtUtc = DateTime.MinValue;
        private bool _disposed;

        public event Action<ScanProgress> ProgressChanged;
        public event Action<ListingSnapshot> RowsChanged;

        private struct CacheEntry
        {
            public DirStats Stats;
            public DateTime AtUtc;
        }

        public SizeEngine(FsSettings settings) : this(settings, null, VolumeMaps.ForLocalDrives(settings.UseFastNtfsIndex))
        {
            Prewarm();
        }

        // Без карт: каждый заход считается обходом (тесты обхода).
        internal SizeEngine(FsSettings settings, string storeFile) : this(settings, storeFile, null) { }

        internal SizeEngine(FsSettings settings, string storeFile, VolumeMaps maps)
        {
            _settings = settings;
            _maps = maps;
            _mapsMft = settings.UseFastNtfsIndex;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _store = new SizeCacheStore(storeFile);
            _watcher.Changed += OnFolderChanged;
            if (_maps != null) _maps.Changed += OnMapChanged;
            _store.Load();
        }

        // Карты строятся заранее — системный диск и диски, которые уже открывали: к первому заходу числа готовы.
        private void Prewarm()
        {
            HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { roots.Add(Path.GetPathRoot(Environment.SystemDirectory)); } catch { }
            foreach (string path in _store.Paths)
                if (path.Length >= 3 && path[1] == ':' && path[2] == '\\') roots.Add(char.ToUpperInvariant(path[0]) + @":\");
            VolumeMaps maps = _maps;
            Task.Factory.StartNew(delegate
            {
                foreach (string root in roots) FsLog.Swallow(delegate { maps.For(root); });
            });
        }

        public void ShowFolder(string path, bool force)
        {
            if (_disposed) return;
            if (!force && string.Equals(path, _path, StringComparison.OrdinalIgnoreCase)) return;
            _path = path;
            Restart();
        }

        public void ShowFolder(string path) { ShowFolder(path, false); }

        // «Обновить»: показанная папка перепроверяется по диску. С картой — только её поддерево, в фоне;
        // прежние числа остаются на экране приглушёнными до проверки.
        public void Refresh()
        {
            _cache.Clear();
            VolumeMap map = _maps == null || _path == null ? null : _maps.For(_path);
            if (map != null) map.Verify(_path);
            Restart();
        }

        // Настройки, влияющие на листинг (скрытые файлы) или на источник чисел (быстрый режим).
        public void SettingsChanged()
        {
            if (_maps != null && _mapsMft != _settings.UseFastNtfsIndex)
            {
                _mapsMft = _settings.UseFastNtfsIndex;
                _maps.Reset(_mapsMft);
            }
            Restart();
        }

        // Что-то изменилось в папке на экране. С картой это только новый листинг: числа она обновит сама.
        // Без карты перезапуск идущего подсчёта делал системный диск невозможным: на C:\ что-то пишется
        // всегда, и каждое событие выбрасывало минуты счёта. Идущий подсчёт не трогаем и повторяем один раз
        // после, если он мерил устаревшее. Файл, лишь сменивший размер, чинится даром.
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
                FsLog.Trace("folder changed — relisting");
                _cache.Clear();
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
            Publish(Snapshot(_path, rows, !_scanning && _servedFinal, false));
        }

        private void Restart()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            lock (_served) _servedMap = null;
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

                VolumeMap map = _maps == null ? null : _maps.For(path);
                if (map == null)
                {
                    ApplyFreshCache(pending);
                    pending.RemoveAll(delegate(SizeRow r) { return r.State != RowState.Pending; });
                }

                // Числа прошлого сеанса — на экран сразу, приглушённо, до прихода настоящих: папка, которая
                // считается две минуты, не должна две минуты быть пустой строкой.
                ApplyStoredNumbers(pending);

                if (map != null)
                {
                    Serve(map, path, rows, pending, ct);
                    return;
                }

                _servedFinal = pending.Count == 0;
                Publish(Snapshot(path, rows, pending.Count == 0, false));
                if (pending.Count == 0)
                {
                    Report(new ScanProgress(ScanPhase.Done, null, 100, 0));
                    return;
                }
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

        // ---------------- карта тома ----------------

        private void Serve(VolumeMap map, string path, List<SizeRow> rows, List<SizeRow> dirs, CancellationToken ct)
        {
            lock (_served)
            {
                if (ct.IsCancellationRequested) return;
                _servedMap = map;
                _servedPath = path;
                _servedRows = rows;
                _servedDirs = dirs;
                _servedToken = ct;
                _servedFinal = false;
            }
            map.Focus(path);
            ReadMap(true);
        }

        private void OnMapChanged(VolumeMap map)
        {
            if (_disposed) return;
            bool ours;
            lock (_served) ours = _servedMap == map;
            if (ours) ReadMap(false);
        }

        // Одна сверка показанных строк с картой: микросекунды под замком карты, без единого обращения к диску.
        // Публикуется, только если что-то изменилось, — на тихой машине панель не перерисовывается вовсе.
        private void ReadMap(bool first)
        {
            try
            {
                lock (_served)
                {
                    VolumeMap map = _servedMap;
                    if (map == null || _servedToken.IsCancellationRequested) return;
                    List<SizeRow> dirs = _servedDirs;
                    string[] names = new string[dirs.Count];
                    for (int i = 0; i < names.Length; i++) names[i] = dirs[i].Name;
                    MapEntry[] entries = map.Read(_servedPath, names);

                    bool changed = first, stored = false;
                    int ready = 0;
                    for (int i = 0; i < dirs.Count; i++)
                    {
                        SizeRow row = dirs[i];
                        MapEntry e = entries[i];
                        if (!e.Found) continue;
                        if (e.Complete)
                        {
                            ready++;
                            RowState state = e.Stats.Partial ? RowState.Partial : RowState.Ready;
                            if (!row.FromCache && row.State == state && Same(row, e.Stats)) continue;
                            Apply(row, e.Stats);
                            _store.Put(row.FullPath, e.Stats);
                            changed = stored = true;
                        }
                        else if (!row.FromCache && !Same(row, e.Stats))
                        {
                            // Пока поддерево досчитывается, растущая сумма лучше пустой строки. Число прошлого
                            // сеанса стоит до конца: промежуточная сумма с нуля выглядела бы как уменьшившаяся папка.
                            row.Bytes = e.Stats.Bytes;
                            row.Files = e.Stats.Files;
                            row.Directories = e.Stats.Directories;
                            row.State = RowState.Measuring;
                            changed = true;
                        }
                    }
                    bool final = ready == dirs.Count;
                    bool wasFinal = _servedFinal;
                    _servedFinal = final;

                    bool building;
                    int percent;
                    long items;
                    map.Progress(out building, out percent, out items);
                    if (changed) Publish(Snapshot(_servedPath, _servedRows, final, map.FromMft));
                    if (final)
                    {
                        if (changed || !wasFinal) Report(new ScanProgress(ScanPhase.Done, null, 100, items));
                    }
                    else if (building) Report(new ScanProgress(ScanPhase.IndexingVolume, map.RootPath, percent, 0));
                    else Report(new ScanProgress(ScanPhase.MeasuringFolders, ready + "/" + dirs.Count, 0, items));

                    if (stored && !first && DateTime.UtcNow - _savedAtUtc > MapSaveInterval)
                    {
                        _savedAtUtc = DateTime.UtcNow;
                        SizeCacheStore store = _store;
                        Task.Factory.StartNew(delegate { store.Save(); });
                    }
                }
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        private static bool Same(SizeRow row, DirStats stats)
        {
            return row.Bytes == stats.Bytes && row.Files == stats.Files && row.Directories == stats.Directories;
        }

        // ---------------- обход (сеть, съёмные диски) ----------------

        // Все папки сразу, а не одна за другой, — но в фоновом режиме: диск остаётся за человеком.
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
            FsLog.Trace("measure start " + path + " folders=" + jobs.Count + " workers=" + TreeMeasurer.BackgroundWorkers);
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
                }, ct, true);
            }
            ct.ThrowIfCancellationRequested();
            FsLog.Trace("measure done " + path + " in " + clock.ElapsedMilliseconds + " ms, " + counters.Items + " objects");
            _servedFinal = true;
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
            lock (_served) _servedMap = null;
            _watcher.Dispose();
            if (_maps != null) _maps.Dispose();
            _store.Save();
        }
    }
}
