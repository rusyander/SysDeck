// SysDeck — «Размеры папок»: карта тома — дерево всех папок диска с итогами, живущее всё время фонового режима.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SysDeck.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Одна папка карты. Свои байты и файлы — только непосредственное содержимое; Bytes/Files/Dirs — всё
    //  поддерево. Pending — сколько папок поддерева ещё не прочитано (0 = число окончательное), Partial —
    //  сколько из них прочитаны не целиком (> 0 = «≥»). Все поля меняются только под замком карты.
    // ------------------------------------------------------------------ //
    internal sealed class MapNode
    {
        public MapNode Parent;
        public string Name;
        public List<MapNode> Kids;
        public Dictionary<string, MapNode> Lookup;     // строится, когда детей много и кто-то ищет по имени
        public long OwnBytes, OwnFiles;
        public long Bytes, Files, Dirs;
        public int Pending, Partial;
        public bool Listed, OwnPartial, Leaf, Gone, Busy;
        public long DueTicks;                          // 0 — не помечена; иначе когда перечитать
        public long QuietUntilTicks;                   // раньше этого не перечитывать: горячая папка не съест диск
    }

    // Что карта знает о ребёнке показанной папки.
    internal struct MapEntry
    {
        public bool Found, Complete;
        public DirStats Stats;
    }

    // ------------------------------------------------------------------ //
    //  Ввод-вывод в режиме фоновой обработки Windows: очень низкий приоритет диска и памяти, так что подсчёт
    //  уступает всему, что делает человек. Включается только на время чтения диска: поток в этом режиме,
    //  держащий замок, заставил бы ждать всех, кто читает карту.
    // ------------------------------------------------------------------ //
    internal static class BackgroundIo
    {
        private const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
        private const int THREAD_MODE_BACKGROUND_END = 0x00020000;

        [ThreadStatic] private static bool _on;

        public static bool IsOn { get { return _on; } }

        public static void Begin()
        {
            if (_on) return;
            _on = SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        }

        public static void End()
        {
            if (!_on) return;
            SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
            _on = false;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetThreadPriority(IntPtr hThread, int nPriority);
    }

    // ------------------------------------------------------------------ //
    //  Карта тома (или, в тестах, любой папки). Строится один раз: из $MFT, когда есть права и NTFS, иначе
    //  обходом в фоне. Дальше её держит в актуальном виде рекурсивный наблюдатель: изменение где угодно —
    //  перечитать ОДНУ папку, где оно случилось, и разнести разницу по предкам. Обход всего дерева заново
    //  не нужен ни при заходе в папку, ни при изменении в ней.
    // ------------------------------------------------------------------ //
    internal sealed partial class VolumeMap : IDisposable
    {
        private const int Workers = 2;
        private const int DebounceMs = 2000;
        private const int MinQuietMs = 3000;
        private const int CostFactor = 50;               // папка перечитывается не чаще, чем раз в 50 своих чтений
        private const int ChangedEveryMs = 1000;
        private const int LateLimit = 100000;
        private const int SmallKids = 8;
        private const uint ReparseAttribute = 0x400;
        private static readonly TimeSpan MftRefresh = TimeSpan.FromHours(6);
        private static readonly TimeSpan MftOverflowGap = TimeSpan.FromMinutes(2);

        private readonly object _sync = new object();
        private readonly string _rootPath;
        private readonly string _rootKey;
        private readonly bool _mftAllowed;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly Stack<MapNode> _focus = new Stack<MapNode>();
        private readonly Stack<MapNode> _crawl = new Stack<MapNode>();
        private readonly List<MapNode> _dirty = new List<MapNode>();
        private readonly HashSet<string> _late = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _notify;
        private readonly Stopwatch _age = Stopwatch.StartNew();
        private Thread[] _threads;
        private FileSystemWatcher _watcher;

        private MapNode _root;
        private MapNode _focusRoot;
        private string _focusPath;
        private bool _rebuild, _building, _mftFailed, _completeNoted;
        private long _rebuildNotBefore;
        private long _builtAtTicks;
        private int _buildPercent;
        private long _itemsSeen;
        private int _changed;
        private long _listings;
        private bool _disposed;

        public event Action<VolumeMap> Changed;          // не чаще раза в секунду, на фоновом потоке
        public event Action<VolumeMap> Milestone;        // построена, досчитана, сломалась — для maps.json

        public string RootPath { get { return _rootPath; } }
        public bool FromMft { get; private set; }
        public bool Dead { get; private set; }           // корень пропал (отключённый диск) — карту пора выбросить
        public string Failure { get; private set; }
        public long BuildMs { get; private set; }
        public long CompleteMs { get; private set; }

        public VolumeMap(string rootPath, bool mftAllowed)
        {
            _rootPath = rootPath;
            _rootKey = rootPath.TrimEnd('\\');
            _mftAllowed = mftAllowed;
            _notify = new Timer(delegate { RaiseChanged(); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_threads != null) return;
                if (_mftAllowed) _rebuild = true;
                else StartCrawl();
                _threads = new Thread[Workers];
                for (int i = 0; i < Workers; i++)
                {
                    Thread t = new Thread(Worker);
                    t.IsBackground = true;
                    t.Priority = ThreadPriority.BelowNormal;
                    t.Name = "FolderSize map " + _rootKey + " #" + i;
                    _threads[i] = t;
                }
            }
            // Наблюдатель — раньше построения: всё, что изменится, пока строится карта, будет перечитано после.
            StartWatcher();
            foreach (Thread t in _threads) t.Start();
        }

        // ---------------- чтение ----------------

        // Дети папки по именам — одним взглядом под замком: микросекунды, сколько бы ни весил диск.
        public MapEntry[] Read(string path, IList<string> names)
        {
            MapEntry[] result = new MapEntry[names.Count];
            lock (_sync)
            {
                MapNode dir = Resolve(path);
                if (dir == null || dir.Leaf) return result;
                for (int i = 0; i < names.Count; i++)
                {
                    MapNode kid = FindKid(dir, names[i]);
                    if (kid == null) continue;
                    result[i].Found = true;
                    result[i].Complete = kid.Pending == 0;
                    result[i].Stats = new DirStats(kid.Bytes, kid.Files, kid.Dirs, kid.Partial > 0);
                }
            }
            return result;
        }

        public void Progress(out bool building, out int percent, out long items)
        {
            lock (_sync)
            {
                building = _building && _root == null;
                percent = _buildPercent;
                items = _itemsSeen;
            }
        }

        // Строится ли индекс $MFT прямо сейчас (впервые или заново).
        public bool Indexing { get { lock (_sync) return _building; } }

        internal bool IsComplete { get { lock (_sync) return _root != null && _root.Pending == 0 && !_building; } }

        internal DirStats RootStats { get { lock (_sync) return _root == null ? new DirStats() : new DirStats(_root.Bytes, _root.Files, _root.Dirs, _root.Partial > 0); } }

        internal int DirectoryCount { get { lock (_sync) return _root == null ? 0 : (int)_root.Dirs + 1; } }

        // Сколько папок прочитано с диска за жизнь карты — тесты сверяют, что изменение не вызвало обход.
        internal long Listings { get { return Interlocked.Read(ref _listings); } }

        // ---------------- что смотрит человек ----------------

        // Показанная папка читается раньше всего остального. Если до неё обход ещё не дошёл, первыми идут
        // папки на пути к ней — по одной, а не всё дерево под ближайшим известным предком.
        public void Focus(string path)
        {
            lock (_sync)
            {
                _focusPath = path;
                _focusRoot = null;
                while (_focus.Count > 0) _crawl.Push(_focus.Pop());
                RefreshFocus();
                Monitor.PulseAll(_sync);
            }
        }

        // «Обновить»: поддерево показанной папки перечитывается заново, числа остаются на экране до проверки.
        // Корень диска при карте из $MFT быстрее перечитать из неё целиком.
        public void Verify(string path)
        {
            lock (_sync)
            {
                MapNode node = Resolve(path);
                if (node == null) return;
                if (node == _root && FromMft && !_mftFailed)
                {
                    _rebuild = true;
                    _rebuildNotBefore = 0;
                }
                else
                {
                    Restale(node);
                    _focusRoot = null;
                    RefreshFocus();
                }
                Monitor.PulseAll(_sync);
            }
            SignalChanged();
        }

        private void RefreshFocus()
        {
            if (_focusPath == null || _root == null) return;
            MapNode target = Resolve(_focusPath);
            if (target != null)
            {
                if (target == _focusRoot) return;
                _focusRoot = target;
                PushFrontier(target, _focus);
                return;
            }
            MapNode nearest = Nearest(_focusPath);
            if (nearest != null && NeedsListing(nearest)) _focus.Push(nearest);
        }

        // Непрочитанные папки поддерева, ближайшие к его корню; прочитанные целиком ветки пропускаются.
        private static void PushFrontier(MapNode top, Stack<MapNode> into)
        {
            Stack<MapNode> walk = new Stack<MapNode>();
            walk.Push(top);
            while (walk.Count > 0)
            {
                MapNode n = walk.Pop();
                if (n.Pending == 0) continue;
                if (NeedsListing(n))
                {
                    into.Push(n);
                    continue;
                }
                if (n.Kids != null) foreach (MapNode k in n.Kids) walk.Push(k);
            }
        }

        // ---------------- наблюдатель ----------------

        private void StartWatcher()
        {
            try
            {
                FileSystemWatcher w = new FileSystemWatcher(_rootPath);
                w.IncludeSubdirectories = true;
                w.InternalBufferSize = 64 * 1024;
                w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size;
                w.Created += OnEntry;
                w.Deleted += OnEntry;
                w.Changed += OnEntry;
                w.Renamed += OnRenamed;
                w.Error += OnWatchError;
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex)
            {
                // Без наблюдателя карта остаётся верной на момент построения; «Обновить» перечитывает папку.
                FsLog.Report(ex);
            }
        }

        private void OnEntry(object sender, FileSystemEventArgs e) { Touch(ParentOf(e.FullPath)); }

        private void OnRenamed(object sender, RenamedEventArgs e)
        {
            // Переименованная или перенесённая папка уносит своё поддерево с собой: без этого её содержимое
            // пришлось бы выбросить в старом месте и обойти заново в новом.
            lock (_sync) { if (_root != null && !_building) Move(e.OldFullPath, e.FullPath); }
            Touch(ParentOf(e.OldFullPath));
            Touch(ParentOf(e.FullPath));
        }

        // Переполнение буфера: изменений было больше, чем Windows смогла описать. Где именно — неизвестно,
        // поэтому карта перепроверяется вся, в фоне: из $MFT заново или обходом с прежними числами на экране.
        private void OnWatchError(object sender, ErrorEventArgs e)
        {
            if (!Directory.Exists(_rootPath))
            {
                Dead = true;
                Failure = Tr.S("Диск отключён", "The drive is gone");
                RaiseMilestone();
                return;
            }
            FsLog.Trace("map " + _rootKey + ": watcher overflow — " + (FromMft ? "MFT rebuild" : "recrawl"));
            lock (_sync)
            {
                if (_root == null) return;
                if (FromMft && !_mftFailed)
                {
                    _rebuild = true;
                    _rebuildNotBefore = Math.Max(_rebuildNotBefore, _builtAtTicks + MftOverflowGap.Ticks);
                }
                else
                {
                    Restale(_root);
                    _crawl.Push(_root);
                    _focusRoot = null;
                    RefreshFocus();
                }
                Monitor.PulseAll(_sync);
            }
            try
            {
                FileSystemWatcher w = _watcher;
                if (w != null) { w.EnableRaisingEvents = false; w.EnableRaisingEvents = true; }
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        // Что-то изменилось внутри папки dirPath — перечитать её, но не сразу (одно копирование — тысячи
        // событий) и не чаще, чем позволяет цена её чтения.
        private void Touch(string dirPath)
        {
            if (dirPath == null) return;
            lock (_sync)
            {
                if (_disposed) return;
                if (_building && _late.Count < LateLimit) _late.Add(dirPath);
                if (_root == null) return;
                MapNode n = Resolve(dirPath);
                if (n == null || !n.Listed || n.Leaf) return;       // ещё не прочитана — обход до неё дойдёт сам
                MarkDirty(n);
            }
        }

        private void MarkDirty(MapNode n)
        {
            if (n.DueTicks != 0) return;
            n.DueTicks = Now() + DebounceMs * TimeSpan.TicksPerMillisecond;
            _dirty.Add(n);
            Monitor.PulseAll(_sync);
        }

        // ---------------- рабочие потоки ----------------

        private enum JobKind { None, Build, List }

        private void Worker()
        {
            while (true)
            {
                MapNode node;
                JobKind kind = Next(out node);
                if (kind == JobKind.None) return;
                try
                {
                    if (kind == JobKind.Build) Build();
                    else List(node);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    FsLog.Report(ex);
                    lock (_sync) { if (node != null) node.Busy = false; }
                }
            }
        }

        // Порядок: перестроение $MFT → помеченные изменения, чей срок настал → показанная папка → остальной обход.
        private JobKind Next(out MapNode node)
        {
            node = null;
            lock (_sync)
            {
                while (true)
                {
                    if (_disposed) return JobKind.None;
                    long now = Now();
                    if (FromMft && !_building && !_mftFailed && now - _builtAtTicks > MftRefresh.Ticks) _rebuild = true;
                    long wait = long.MaxValue;
                    if (_rebuild && !_building && !_mftFailed)
                    {
                        if (now >= _rebuildNotBefore)
                        {
                            _rebuild = false;
                            _building = true;
                            _buildPercent = 0;
                            return JobKind.Build;
                        }
                        wait = _rebuildNotBefore - now;
                    }
                    else if (_rebuild && _mftFailed) _rebuild = false;

                    for (int i = 0; i < _dirty.Count; i++)
                    {
                        MapNode n = _dirty[i];
                        if (!Alive(n) || n.Leaf)
                        {
                            n.DueTicks = 0;
                            _dirty.RemoveAt(i--);
                            continue;
                        }
                        if (n.Busy) continue;
                        long at = Math.Max(n.DueTicks, n.QuietUntilTicks);
                        if (at <= now)
                        {
                            n.DueTicks = 0;
                            _dirty.RemoveAt(i);
                            n.Busy = true;
                            node = n;
                            return JobKind.List;
                        }
                        wait = Math.Min(wait, at - now);
                    }
                    node = PopListable(_focus) ?? PopListable(_crawl);
                    if (node != null)
                    {
                        node.Busy = true;
                        return JobKind.List;
                    }
                    if (wait == long.MaxValue) Monitor.Wait(_sync);
                    else Monitor.Wait(_sync, (int)Math.Min(int.MaxValue, Math.Max(1, wait / TimeSpan.TicksPerMillisecond)));
                }
            }
        }

        private MapNode PopListable(Stack<MapNode> stack)
        {
            while (stack.Count > 0)
            {
                MapNode n = stack.Pop();
                if (NeedsListing(n) && !n.Busy && Alive(n)) return n;
            }
            return null;
        }

        private static bool NeedsListing(MapNode n) { return !n.Gone && !n.Leaf && !n.Listed; }

        // Узел из текущего дерева: после перестроения старое дерево выброшено целиком, и работа над его
        // узлами, начатая раньше, ничего не должна в новом менять.
        private bool Alive(MapNode n)
        {
            if (n.Gone) return false;
            MapNode top = n;
            while (top.Parent != null) top = top.Parent;
            return top == _root;
        }

        private struct DirEntry
        {
            public string Name;
            public bool Reparse, Unwalkable;
        }

        private sealed class Listing
        {
            public long Bytes, Files;
            public bool Partial, Denied, Missing, Reparse;
            public readonly List<DirEntry> Dirs = new List<DirEntry>();
        }

        // Одна папка: файлы — в её собственный итог, подпапки — сверка с детьми узла.
        private void List(MapNode node)
        {
            string path;
            lock (_sync)
            {
                if (!Alive(node) || node.Leaf)
                {
                    node.Busy = false;
                    return;
                }
                path = PathOf(node);
            }
            Stopwatch cost = Stopwatch.StartNew();
            Listing listing;
            BackgroundIo.Begin();
            try { listing = ReadListing(path, node.Parent == null); }
            finally { BackgroundIo.End(); }
            Interlocked.Increment(ref _listings);
            long spent = cost.ElapsedTicks * TimeSpan.TicksPerSecond / Stopwatch.Frequency;

            bool missing = false;
            lock (_sync)
            {
                node.Busy = false;
                if (!Alive(node)) return;
                node.QuietUntilTicks = Now() + Math.Max(MinQuietMs * TimeSpan.TicksPerMillisecond, spent * CostFactor);
                if (listing.Missing)
                {
                    // Папки уже нет: её убирает перечитывание родителя.
                    if (node.Parent != null) MarkDirty(node.Parent);
                    missing = true;
                }
                else if (listing.Reparse && node.Parent != null) MakeLeaf(node);
                else if (listing.Denied && node.Listed) { }   // прежние числа (из $MFT) правдивее пустого «нет доступа»
                else Apply(node, listing);
                _itemsSeen += listing.Files + listing.Dirs.Count;
                if (_focusRoot == null && _focusPath != null) RefreshFocus();
                NoteComplete();
                Monitor.PulseAll(_sync);
            }
            if (!missing) SignalChanged();
        }

        private static Listing ReadListing(string path, bool isRoot)
        {
            Listing result = new Listing();
            uint attributes = Native.AttributesOf(path);
            if (attributes == Native.INVALID_FILE_ATTRIBUTES)
            {
                if (!Directory.Exists(path)) result.Missing = true;
                else result.Denied = result.Partial = true;
                return result;
            }
            // Точка соединения внутри карты — второе имя чужого дерева: её байты принадлежат цели.
            if (!isRoot && (attributes & ReparseAttribute) != 0)
            {
                result.Reparse = true;
                return result;
            }
            try
            {
                foreach (DirectoryWalker.Node n in DirectoryWalker.Enumerate(path, null))
                {
                    if (n.IsDirectory)
                    {
                        DirEntry e = new DirEntry();
                        e.Name = n.Name;
                        e.Reparse = (n.Attributes & FileAttributes.ReparsePoint) != 0;
                        e.Unwalkable = !e.Reparse && DirectoryWalker.IsUnwalkableDirectory(DirectoryWalker.Combine(path, n.Name));
                        result.Dirs.Add(e);
                    }
                    else
                    {
                        result.Files++;
                        result.Bytes += n.Length;
                    }
                }
            }
            catch (DirectoryNotFoundException) { result.Missing = true; }
            catch (UnauthorizedAccessException) { result.Denied = result.Partial = true; }
            catch (IOException) { result.Partial = true; }
            catch (System.Security.SecurityException) { result.Denied = result.Partial = true; }
            return result;
        }

        // ---------------- изменение дерева (только под замком) ----------------

        private void Apply(MapNode node, Listing listing)
        {
            int partial = listing.Partial ? 1 : 0;
            Bubble(node, listing.Bytes - node.OwnBytes, listing.Files - node.OwnFiles, 0,
                   node.Listed ? 0 : -1, partial - (node.OwnPartial ? 1 : 0));
            node.OwnBytes = listing.Bytes;
            node.OwnFiles = listing.Files;
            node.OwnPartial = listing.Partial;
            node.Listed = true;

            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DirEntry e in listing.Dirs)
            {
                if (!seen.Add(e.Name)) continue;          // два имени, различающихся регистром (WSL): считается одно
                bool leaf = e.Reparse || e.Unwalkable;
                MapNode kid = FindKid(node, e.Name);
                if (kid != null && (kid.Leaf != leaf || kid.OwnPartial != e.Unwalkable))
                {
                    Remove(kid);
                    kid = null;
                }
                if (kid == null)
                {
                    kid = NewNode(e.Name, leaf, e.Unwalkable);
                    Attach(node, kid);
                    if (!leaf) Schedule(kid);
                }
                else if (!string.Equals(kid.Name, e.Name, StringComparison.Ordinal)) kid.Name = e.Name;
                else if (NeedsListing(kid)) Schedule(kid);
            }
            if (node.Kids == null) return;
            List<MapNode> gone = null;
            foreach (MapNode kid in node.Kids)
                if (!seen.Contains(kid.Name)) (gone ?? (gone = new List<MapNode>())).Add(kid);
            if (gone != null) foreach (MapNode kid in gone) Remove(kid);
        }

        // Ссылка или имя, в которое Win32 не войдёт: запись есть, содержимого нет. Имя-ловушка — нижняя граница.
        private static MapNode NewNode(string name, bool leaf, bool unwalkable)
        {
            MapNode n = new MapNode();
            n.Name = name;
            n.Leaf = leaf;
            n.Listed = leaf;
            n.OwnPartial = unwalkable;
            n.Partial = unwalkable ? 1 : 0;
            n.Pending = leaf ? 0 : 1;
            return n;
        }

        private void Schedule(MapNode kid)
        {
            if (_focusRoot != null && IsUnder(kid, _focusRoot)) _focus.Push(kid);
            else _crawl.Push(kid);
        }

        private static bool IsUnder(MapNode n, MapNode top)
        {
            for (MapNode p = n; p != null; p = p.Parent) if (p == top) return true;
            return false;
        }

        private void MakeLeaf(MapNode node)
        {
            MapNode parent = node.Parent;
            string name = node.Name;
            Remove(node);
            MapNode leaf = NewNode(name, true, false);
            Attach(parent, leaf);
        }

        private static void Attach(MapNode parent, MapNode kid)
        {
            if (parent.Kids == null) parent.Kids = new List<MapNode>(4);
            parent.Kids.Add(kid);
            if (parent.Lookup != null) parent.Lookup[kid.Name] = kid;
            kid.Parent = parent;
            Bubble(parent, kid.Bytes, kid.Files, kid.Dirs + 1, kid.Pending, kid.Partial);
        }

        private static void Detach(MapNode kid)
        {
            MapNode parent = kid.Parent;
            if (parent == null) return;
            parent.Kids.Remove(kid);
            if (parent.Lookup != null) parent.Lookup.Remove(kid.Name);
            kid.Parent = null;
            Bubble(parent, -kid.Bytes, -kid.Files, -(kid.Dirs + 1), -kid.Pending, -kid.Partial);
        }

        private static void Remove(MapNode kid)
        {
            Detach(kid);
            Stack<MapNode> walk = new Stack<MapNode>();
            walk.Push(kid);
            while (walk.Count > 0)
            {
                MapNode n = walk.Pop();
                n.Gone = true;
                if (n.Kids != null) foreach (MapNode k in n.Kids) walk.Push(k);
            }
        }

        // Разница одной папки — ей и всем предкам до корня: глубина дерева, а не его размер.
        private static void Bubble(MapNode from, long bytes, long files, long dirs, int pending, int partial)
        {
            if (bytes == 0 && files == 0 && dirs == 0 && pending == 0 && partial == 0) return;
            for (MapNode n = from; n != null; n = n.Parent)
            {
                n.Bytes += bytes;
                n.Files += files;
                n.Dirs += dirs;
                n.Pending += pending;
                n.Partial += partial;
            }
        }

        private void Move(string oldPath, string newPath)
        {
            MapNode node = Resolve(oldPath);
            if (node == null || node == _root) return;
            MapNode target = Resolve(ParentOf(newPath));
            if (target == null || target.Leaf || IsUnder(target, node))
            {
                Remove(node);                         // уехала туда, где карты нет: родитель досчитает сам
                return;
            }
            string name = DirectoryWalker.LeafOf(newPath.TrimEnd('\\'));
            MapNode clash = FindKid(target, name);
            if (clash != null && clash != node) Remove(clash);
            Detach(node);
            node.Name = name;
            Attach(target, node);
        }

        // Всё поддерево — «прочитать заново», числа остаются: строки на экране приглушены, но не пусты.
        private void Restale(MapNode top)
        {
            List<MapNode> order = new List<MapNode>();
            Stack<MapNode> walk = new Stack<MapNode>();
            walk.Push(top);
            while (walk.Count > 0)
            {
                MapNode n = walk.Pop();
                order.Add(n);
                if (n.Kids != null) foreach (MapNode k in n.Kids) walk.Push(k);
            }
            int before = top.Pending;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                MapNode n = order[i];
                if (!n.Leaf) n.Listed = false;
                int pending = n.Listed ? 0 : 1;
                if (n.Kids != null) foreach (MapNode k in n.Kids) pending += k.Pending;
                n.Pending = pending;
            }
            if (top.Parent != null) Bubble(top.Parent, 0, 0, 0, 0 + top.Pending - before, 0);
            _crawl.Push(top);
            _completeNoted = false;
        }

        private static MapNode FindKid(MapNode dir, string name)
        {
            List<MapNode> kids = dir.Kids;
            if (kids == null) return null;
            if (kids.Count <= SmallKids)
            {
                foreach (MapNode k in kids) if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
                return null;
            }
            if (dir.Lookup == null)
            {
                Dictionary<string, MapNode> lookup = new Dictionary<string, MapNode>(kids.Count, StringComparer.OrdinalIgnoreCase);
                foreach (MapNode k in kids) lookup[k.Name] = k;
                dir.Lookup = lookup;
            }
            MapNode found;
            return dir.Lookup.TryGetValue(name, out found) ? found : null;
        }

        // ---------------- пути ----------------

        private MapNode Resolve(string path)
        {
            if (_root == null || string.IsNullOrEmpty(path)) return null;
            string key = path.TrimEnd('\\');
            if (!key.StartsWith(_rootKey, StringComparison.OrdinalIgnoreCase)) return null;
            if (key.Length == _rootKey.Length) return _root;
            if (key[_rootKey.Length] != '\\') return null;
            MapNode n = _root;
            foreach (string part in key.Substring(_rootKey.Length + 1).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (n.Leaf) return null;
                n = FindKid(n, part);
                if (n == null) return null;
            }
            return n;
        }

        // Ближайший известный предок пути, до которого обход ещё не добрался.
        private MapNode Nearest(string path)
        {
            if (_root == null) return null;
            string key = path.TrimEnd('\\');
            if (!key.StartsWith(_rootKey, StringComparison.OrdinalIgnoreCase)) return null;
            if (key.Length == _rootKey.Length) return _root;
            if (key[_rootKey.Length] != '\\') return null;
            MapNode n = _root;
            foreach (string part in key.Substring(_rootKey.Length + 1).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (n.Leaf) return null;
                MapNode next = FindKid(n, part);
                if (next == null) return n;
                n = next;
            }
            return n;
        }

        private string PathOf(MapNode node)
        {
            if (node.Parent == null) return _rootPath;
            List<string> parts = new List<string>();
            for (MapNode n = node; n.Parent != null; n = n.Parent) parts.Add(n.Name);
            string path = _rootPath;
            for (int i = parts.Count - 1; i >= 0; i--) path = DirectoryWalker.Combine(path, parts[i]);
            return path;
        }

        // Без Path.GetDirectoryName: .NET Framework бросает на путях длиннее 260 знаков, а наблюдатель их отдаёт.
        internal static string ParentOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            string key = path.TrimEnd('\\');
            int slash = key.LastIndexOf('\\');
            if (slash < 0) return null;
            if (slash == 2 && key.Length > 1 && key[1] == ':') return key.Substring(0, 3);
            return key.Substring(0, slash);
        }

        private static long Now() { return DateTime.UtcNow.Ticks; }

        // ---------------- уведомления ----------------

        private void SignalChanged()
        {
            if (Interlocked.Exchange(ref _changed, 1) != 0) return;
            try { _notify.Change(ChangedEveryMs, Timeout.Infinite); } catch (ObjectDisposedException) { }
        }

        private void RaiseChanged()
        {
            Interlocked.Exchange(ref _changed, 0);
            if (_disposed) return;
            Action<VolumeMap> handler = Changed;
            if (handler == null) return;
            try { handler(this); } catch (Exception ex) { FsLog.Report(ex); }
        }

        private void RaiseMilestone()
        {
            Action<VolumeMap> handler = Milestone;
            if (handler == null || _disposed) return;
            try { handler(this); } catch (Exception ex) { FsLog.Report(ex); }
        }

        // ---------------- для тестов ----------------

        internal void OverflowForTest() { OnWatchError(this, new ErrorEventArgs(new InternalBufferOverflowException())); }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                Monitor.PulseAll(_sync);
            }
            _cts.Cancel();
            FileSystemWatcher w = _watcher;
            _watcher = null;
            if (w != null)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _notify.Dispose();
        }
    }
}
