// SysDeck — «Размеры папок»: карта тома, часть 2 — построение (из $MFT или обходом) и карты всех дисков.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime;
using System.Threading;

namespace SysDeck.FolderSize
{
    internal sealed partial class VolumeMap
    {
        private void StartCrawl()
        {
            MapNode root = NewNode(_rootPath, false, false);
            _root = root;
            FromMft = false;
            _crawl.Clear();
            _focus.Clear();
            _crawl.Push(root);
            _focusRoot = null;
            RefreshFocus();
        }

        // $MFT читается целиком в фоне (секунды — десятки секунд на большом диске), новое дерево готовится
        // вне замка и подменяет старое разом. Изменения, случившиеся за это время, перечитываются следом.
        private void Build()
        {
            Stopwatch clock = Stopwatch.StartNew();
            MapNode fresh = null;
            string failure = null;
            try
            {
                BackgroundIo.Begin();
                NtfsVolumeIndex index;
                try
                {
                    index = MftIndexBuilder.Build(_rootPath[0], delegate(int p) { _buildPercent = p; }, _cts.Token);
                }
                finally { BackgroundIo.End(); }
                fresh = FromNtfs(index.Dirs, _rootPath);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Любая неожиданность здесь не смертельна: обход даёт те же числа, только дольше.
                FsLog.Report(ex);
                failure = ex.Message;
            }
            lock (_sync)
            {
                _building = false;
                if (_disposed) return;
                if (fresh != null)
                {
                    if (_root != null) Remove(_root);
                    _root = fresh;
                    FromMft = true;
                    Failure = null;
                    BuildMs = clock.ElapsedMilliseconds;
                    _builtAtTicks = Now();
                    _crawl.Clear();
                    _focus.Clear();
                    _dirty.Clear();
                    _focusRoot = null;
                    foreach (string late in _late)
                    {
                        MapNode n = Resolve(late);
                        if (n != null && n.Listed && !n.Leaf) MarkDirty(n);
                    }
                    RefreshFocus();
                    _completeNoted = false;
                    NoteComplete();
                }
                else
                {
                    _mftFailed = true;
                    Failure = failure;
                    if (_root == null) StartCrawl();
                }
                _late.Clear();
                Monitor.PulseAll(_sync);
            }
            FsLog.Trace("map " + _rootKey + ": " + (fresh != null ? "MFT in " + clock.ElapsedMilliseconds + " ms" : "MFT failed — crawling"));
            // Таблица большого диска — сотни мегабайт временных массивов в куче больших объектов. Без уплотнения
            // процесс, живущий в трее весь день, держал бы их за собой до следующего построения.
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
            RaiseMilestone();
            SignalChanged();
        }

        internal static MapNode FromNtfs(Dictionary<uint, NtfsVolumeIndex.DirNode> dirs, string rootPath)
        {
            const uint rootIndex = 5;
            NtfsVolumeIndex.DirNode top;
            if (!dirs.TryGetValue(rootIndex, out top)) throw new InvalidDataException("no root directory in the MFT index");
            MapNode root = Convert(top, rootPath);
            Stack<KeyValuePair<NtfsVolumeIndex.DirNode, MapNode>> walk = new Stack<KeyValuePair<NtfsVolumeIndex.DirNode, MapNode>>();
            walk.Push(new KeyValuePair<NtfsVolumeIndex.DirNode, MapNode>(top, root));
            while (walk.Count > 0)
            {
                KeyValuePair<NtfsVolumeIndex.DirNode, MapNode> pair = walk.Pop();
                if (pair.Key.ChildDirs == null) continue;
                pair.Value.Kids = new List<MapNode>(pair.Key.ChildDirs.Count);
                foreach (uint c in pair.Key.ChildDirs)
                {
                    NtfsVolumeIndex.DirNode child;
                    if (c == rootIndex || !dirs.TryGetValue(c, out child)) continue;
                    MapNode kid = Convert(child, child.Name);
                    kid.Parent = pair.Value;
                    pair.Value.Kids.Add(kid);
                    walk.Push(new KeyValuePair<NtfsVolumeIndex.DirNode, MapNode>(child, kid));
                }
            }
            return root;
        }

        private static MapNode Convert(NtfsVolumeIndex.DirNode d, string name)
        {
            MapNode n = new MapNode();
            n.Name = name;
            n.OwnBytes = d.DirectBytes;
            n.OwnFiles = d.DirectFiles;
            n.Bytes = d.TotalBytes;
            n.Files = d.TotalFiles;
            n.Dirs = d.TotalDirs;
            n.Listed = true;
            return n;
        }

        private void NoteComplete()
        {
            if (_completeNoted || _root == null || _root.Pending != 0 || _building) return;
            _completeNoted = true;
            CompleteMs = _age.ElapsedMilliseconds;
            ThreadPool.QueueUserWorkItem(delegate { RaiseMilestone(); });
        }
    }

    // ------------------------------------------------------------------ //
    //  Карты всех локальных дисков, по одной на диск, создаются при первом заходе и живут до выхода.
    //  Сетевые пути и съёмные диски карты не получают: там подсчёт по заходу, тоже в фоновом режиме.
    // ------------------------------------------------------------------ //
    internal sealed class VolumeMaps : IDisposable
    {
        private readonly Dictionary<string, VolumeMap> _maps = new Dictionary<string, VolumeMap>(StringComparer.OrdinalIgnoreCase);
        private readonly string[] _fixedRoots;           // тестам — свои корни вместо дисков
        private readonly string _statusFile;
        private bool _allowMft;
        private bool _disposed;

        public event Action<VolumeMap> Changed;

        private VolumeMaps(string[] roots, bool allowMft, string statusFile)
        {
            _fixedRoots = roots;
            _allowMft = allowMft;
            _statusFile = statusFile;
        }

        public static VolumeMaps ForLocalDrives(bool allowMft) { return new VolumeMaps(null, allowMft, FsPaths.MapsFile); }

        internal static VolumeMaps ForRoots(params string[] roots) { return new VolumeMaps(roots, false, null); }

        // Карта, отвечающая за путь, или null, если путь ей не принадлежит (сеть, съёмный диск).
        public VolumeMap For(string path)
        {
            string root = RootOf(path);
            if (root == null) return null;
            VolumeMap map;
            lock (_maps)
            {
                if (_disposed) return null;
                if (_maps.TryGetValue(root, out map) && map.Dead)
                {
                    _maps.Remove(root);
                    map.Dispose();
                    map = null;
                }
                if (map == null)
                {
                    bool mft = _fixedRoots == null && _allowMft && Elevation.IsElevated && RawVolume.LooksSupported(root);
                    map = new VolumeMap(root, mft);
                    map.Changed += OnChanged;
                    map.Milestone += delegate { WriteStatus(); };
                    _maps[root] = map;
                    map.Start();
                    FsLog.Trace("map " + root + " started, mft=" + mft);
                }
            }
            return map;
        }

        // Быстрый режим включили или выключили: карты строятся заново, уже по-новому.
        public void Reset(bool allowMft)
        {
            List<VolumeMap> old;
            lock (_maps)
            {
                _allowMft = allowMft;
                old = new List<VolumeMap>(_maps.Values);
                _maps.Clear();
            }
            foreach (VolumeMap m in old) m.Dispose();
        }

        private string RootOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (_fixedRoots != null)
            {
                foreach (string r in _fixedRoots)
                {
                    string key = r.TrimEnd('\\');
                    string p = path.TrimEnd('\\');
                    if (p.StartsWith(key, StringComparison.OrdinalIgnoreCase) && (p.Length == key.Length || p[key.Length] == '\\')) return r;
                }
                return null;
            }
            try
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length < 2 || path[1] != ':') return null;
                string root = char.ToUpperInvariant(path[0]) + @":\";
                DriveInfo drive = new DriveInfo(root);
                return drive.DriveType == DriveType.Fixed && drive.IsReady ? root : null;
            }
            catch { return null; }
        }

        private void OnChanged(VolumeMap map)
        {
            Action<VolumeMap> handler = Changed;
            if (handler != null) handler(map);
        }

        // Что знает каждая карта — для диагностики: откуда построена, за сколько, сколько в ней папок.
        private void WriteStatus()
        {
            if (_statusFile == null) return;
            try
            {
                List<VolumeMap> maps;
                lock (_maps) maps = new List<VolumeMap>(_maps.Values);
                JVal root = JVal.NewObj();
                foreach (VolumeMap m in maps)
                {
                    DirStats stats = m.RootStats;
                    JVal o = JVal.NewObj();
                    o.Set("source", JVal.NewStr(m.FromMft ? "mft" : "crawl"));
                    o.Set("complete", Bool(m.IsComplete));
                    o.Set("folders", Num(m.DirectoryCount));
                    o.Set("files", Num(stats.Files));
                    o.Set("bytes", Num(stats.Bytes));
                    o.Set("mftMs", Num(m.BuildMs));
                    o.Set("completeMs", Num(m.CompleteMs));
                    if (m.Failure != null) o.Set("failure", JVal.NewStr(m.Failure));
                    o.Set("at", JVal.NewStr(DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture)));
                    root.Set(m.RootPath, o);
                }
                lock (_maps) FsPaths.WriteAtomic(_statusFile, Jsn.Write(root));
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        private static JVal Num(long v) { return JVal.NewNum(v.ToString(System.Globalization.CultureInfo.InvariantCulture)); }

        private static JVal Bool(bool v)
        {
            JVal j = new JVal();
            j.Kind = JKind.Bool;
            j.B = v;
            return j;
        }

        public void Dispose()
        {
            List<VolumeMap> all;
            lock (_maps)
            {
                _disposed = true;
                all = new List<VolumeMap>(_maps.Values);
                _maps.Clear();
            }
            foreach (VolumeMap m in all) m.Dispose();
        }
    }
}
