// SysDeck — область «foldersize», карта тома: дерево папок с итогами, которое строится один раз и дальше
// обновляется только там, где что-то изменилось. Проверяется на настоящих папках во временном каталоге:
// итоги после обхода, ссылки и имена-ловушки, изменения глубоко внутри, переименование, удаление,
// переполнение наблюдателя, движок панели поверх карты, перенос итогов из индекса $MFT и разбор ответа
// FSCTL_GET_RETRIEVAL_POINTERS. Сырой том не читается: для этого нужны права администратора.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using SysDeck.FolderSize;

namespace SysDeck.Tests
{
    internal static partial class FolderSizeTests
    {
        private const int MapWaitMs = 30000;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadPriority(IntPtr thread, int priority);

        private static void MapTests(Tree t)
        {
            MapParentOf();
            MapFromNtfs();
            MapRetrievalPointers();
            MapBackgroundIo();
            MapBackgroundMeasure(t);
            MapCrawl(t);
            MapLive();
            MapEngine();
        }

        private static bool WaitFor(Func<bool> done, int timeoutMs)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!done())
            {
                if (DateTime.UtcNow > until) return false;
                Thread.Sleep(50);
            }
            return true;
        }

        private static MapEntry ReadOne(VolumeMap map, string dir, string name)
        {
            return map.Read(dir, new string[] { name })[0];
        }

        // ---------- пути без Path.GetDirectoryName ----------
        private static void MapParentOf()
        {
            T.Eq("map: parent of a top folder is the drive root", @"C:\", VolumeMap.ParentOf(@"C:\a"));
            T.Eq("map: parent of a nested folder", @"C:\a", VolumeMap.ParentOf(@"C:\a\b\"));
            T.Eq("map: the drive root has no parent", null, VolumeMap.ParentOf(@"C:\"));
            string deep = @"C:\" + new string('x', 300) + @"\y";
            T.Eq("map: a path over 260 characters still has a parent", @"C:\" + new string('x', 300), VolumeMap.ParentOf(deep));
        }

        // ---------- итоги из индекса $MFT переносятся в карту как есть ----------
        private static void MapFromNtfs()
        {
            Dictionary<uint, NtfsVolumeIndex.DirNode> dirs = new Dictionary<uint, NtfsVolumeIndex.DirNode>();
            NtfsVolumeIndex.DirNode root = new NtfsVolumeIndex.DirNode();
            root.ChildDirs = new List<uint> { 5, 41, 99 };            // 5 — сам корень, 99 — нет в таблице: оба пропускаются
            root.DirectBytes = 10; root.DirectFiles = 1;
            root.TotalBytes = 160; root.TotalFiles = 4; root.TotalDirs = 2;
            NtfsVolumeIndex.DirNode a = new NtfsVolumeIndex.DirNode();
            a.Name = "a"; a.Parent = 5; a.ChildDirs = new List<uint> { 42 };
            a.DirectBytes = 100; a.DirectFiles = 2;
            a.TotalBytes = 150; a.TotalFiles = 3; a.TotalDirs = 1;
            NtfsVolumeIndex.DirNode b = new NtfsVolumeIndex.DirNode();
            b.Name = "b"; b.Parent = 41;
            b.DirectBytes = 50; b.DirectFiles = 1;
            b.TotalBytes = 50; b.TotalFiles = 1;
            dirs[5] = root; dirs[41] = a; dirs[42] = b;

            MapNode top = VolumeMap.FromNtfs(dirs, @"Q:\");
            T.Eq("mft map: root total", 160L, top.Bytes);
            T.Eq("mft map: only real children are attached", 1, top.Kids == null ? 0 : top.Kids.Count);
            MapNode na = top.Kids != null && top.Kids.Count > 0 ? top.Kids[0] : null;
            T.Check("mft map: the child keeps its name, parent and subtree totals",
                    na != null && na.Name == "a" && na.Parent == top && na.Bytes == 150 && na.Files == 3 && na.Dirs == 1 && na.OwnBytes == 100,
                    na == null ? "no child" : na.Name + " " + na.Bytes);
            MapNode nb = na != null && na.Kids != null && na.Kids.Count > 0 ? na.Kids[0] : null;
            T.Check("mft map: grandchildren are attached too", nb != null && nb.Name == "b" && nb.Bytes == 50 && nb.Parent == na);
            T.Check("mft map: every node counts as read", top.Listed && top.Pending == 0 && na != null && na.Pending == 0);

            bool threw = false;
            try { VolumeMap.FromNtfs(new Dictionary<uint, NtfsVolumeIndex.DirNode>(), @"Q:\"); }
            catch (InvalidDataException) { threw = true; }
            T.Check("mft map: an index without the root record is refused", threw);
        }

        // ---------- отрезки $MFT, которые отдаёт NTFS ----------
        private static void MapRetrievalPointers()
        {
            byte[] b = RetrievalBuffer(0, new long[] { 10, 100, 25, 500 });
            List<MftIndexBuilder.Extent> extents = new List<MftIndexBuilder.Extent>();
            long next = MftIndexBuilder.ParseRetrievalPointers(b, b.Length, extents);
            T.Eq("retrieval pointers: two extents", 2, extents.Count);
            T.Check("retrieval pointers: lengths are VCN differences",
                    extents.Count == 2 && extents[0].Lcn == 100 && extents[0].Clusters == 10 && extents[1].Lcn == 500 && extents[1].Clusters == 15);
            T.Eq("retrieval pointers: the continuation VCN is the last NextVcn", 25L, next);

            List<MftIndexBuilder.Extent> more = new List<MftIndexBuilder.Extent>();
            byte[] tail = RetrievalBuffer(25, new long[] { 40, 900 });
            MftIndexBuilder.ParseRetrievalPointers(tail, tail.Length, more);
            T.Check("retrieval pointers: a continued answer starts from its own VCN", more.Count == 1 && more[0].Clusters == 15 && more[0].Lcn == 900);

            byte[] hole = RetrievalBuffer(0, new long[] { 10, -1 });
            bool refused = false;
            try { MftIndexBuilder.ParseRetrievalPointers(hole, hole.Length, new List<MftIndexBuilder.Extent>()); }
            catch (NotSupportedException) { refused = true; }
            T.Check("retrieval pointers: an extent with no disk space is refused", refused);

            bool truncated = false;
            try { MftIndexBuilder.ParseRetrievalPointers(b, 24, new List<MftIndexBuilder.Extent>()); }
            catch (InvalidDataException) { truncated = true; }
            T.Check("retrieval pointers: a truncated answer is refused", truncated);
        }

        private static byte[] RetrievalBuffer(long startVcn, long[] pairs)
        {
            byte[] b = new byte[16 + pairs.Length * 8];
            BitConverter.GetBytes(pairs.Length / 2).CopyTo(b, 0);
            BitConverter.GetBytes(startVcn).CopyTo(b, 8);
            for (int i = 0; i < pairs.Length; i++) BitConverter.GetBytes(pairs[i]).CopyTo(b, 16 + i * 8);
            return b;
        }

        // ---------- фоновый режим ввода-вывода ----------
        private static void MapBackgroundIo()
        {
            bool on = false, again = false, off = false, refusedTwice = false, leftMode = false;
            int error = 0, endError = 0;
            Thread th = new Thread(delegate()
            {
                BackgroundIo.Begin();
                on = BackgroundIo.IsOn;
                // Windows сама подтверждает режим: второй вход отвергается с ERROR_THREAD_MODE_ALREADY_BACKGROUND.
                refusedTwice = !SetThreadPriority(GetCurrentThread(), 0x00010000);
                error = Marshal.GetLastWin32Error();
                BackgroundIo.Begin();
                again = BackgroundIo.IsOn;
                BackgroundIo.End();
                off = !BackgroundIo.IsOn;
                // Уже вне режима: повторный выход отвергается с ERROR_THREAD_MODE_NOT_BACKGROUND.
                leftMode = !SetThreadPriority(GetCurrentThread(), 0x00020000);
                endError = Marshal.GetLastWin32Error();
            });
            th.Start();
            th.Join();
            T.Check("background io: the thread enters background mode", on);
            T.Check("background io: Windows reports the thread as already in background mode", refusedTwice && error == 400, "error " + error);
            T.Check("background io: a second Begin is harmless and End leaves the mode", again && off);
            T.Check("background io: Windows confirms the thread left background mode", leftMode && endError == 401, "error " + endError);
        }

        private static void MapBackgroundMeasure(Tree t)
        {
            TreeMeasurer.Job a = NewJob(t.A);
            TreeMeasurer.Run(new List<TreeMeasurer.Job>(new TreeMeasurer.Job[] { a }), null, null, CancellationToken.None, true);
            T.Eq("background walk: same total as the normal walk", Tree.ABytes, a.Bytes);
        }

        // ---------- обход в фоне: итоги, ссылки, имена-ловушки ----------
        private static void MapCrawl(Tree t)
        {
            using (VolumeMaps maps = VolumeMaps.ForRoots(t.Root))
            {
                VolumeMap map = maps.For(t.Root);
                T.Check("map: a path under the root gets a map", map != null);
                T.Check("map: a path outside the root gets none", maps.For(t.Root + "-other") == null && maps.For(@"Q:\elsewhere") == null);
                if (map == null) return;
                bool done = WaitFor(delegate { return map.IsComplete; }, MapWaitMs);
                T.Check("map: the crawl finishes", done);
                if (!done) return;

                MapEntry[] e = map.Read(t.Root, new string[] { "a", "b", "link", "nope" });
                T.Check("map: folder a is complete", e[0].Found && e[0].Complete);
                T.Eq("map: folder a total", Tree.ABytes, e[0].Stats.Bytes);
                T.Eq("map: files are counted through subfolders", 2L, e[0].Stats.Files);
                T.Eq("map: empty folder b", 0L, e[1].Stats.Bytes);
                T.Check("map: an unknown name is not found", !e[3].Found);
                if (t.BadDir != null)
                    T.Check("map: a folder named 'x. ' makes a a lower bound", e[0].Stats.Partial, "dirs " + e[0].Stats.Directories);
                if (t.HasLink)
                    T.Check("map: the junction is a leaf with no bytes", e[2].Found && e[2].Complete && e[2].Stats.Bytes == 0, e[2].Stats.Bytes + " B");
                T.Eq("map: the root total counts the junction target once", Tree.ABytes + Tree.CBytes + Tree.HiddenBytes, map.RootStats.Bytes);
                T.Check("map: a junction is not entered", map.Read(t.Link, new string[] { "sub" })[0].Found == false);
            }
        }

        // ---------- изменения подхватываются без нового обхода ----------
        private static void MapLive()
        {
            string root = Fx.MakeDir(Fx.Root, "fs-map-live");
            string x = Fx.MakeDir(root, "x");
            string y = Fx.MakeDir(x, "y");
            string z = Fx.MakeDir(y, "z");
            Fx.MakeFile(Path.Combine(z, "base.bin"), 300);
            Fx.MakeDir(root, "gone");
            using (VolumeMaps maps = VolumeMaps.ForRoots(root))
            {
                VolumeMap map = maps.For(root);
                if (!WaitFor(delegate { return map.IsComplete; }, MapWaitMs))
                {
                    T.Check("live map: the crawl finishes", false);
                    return;
                }
                T.Eq("live map: initial total", 300L, ReadOne(map, root, "x").Stats.Bytes);
                long listed = map.Listings;

                Fx.MakeFile(Path.Combine(z, "new.bin"), 500);
                bool grew = WaitFor(delegate { MapEntry m = ReadOne(map, root, "x"); return m.Complete && m.Stats.Bytes == 800; }, MapWaitMs);
                T.Check("live map: a file added three levels down reaches the top folder", grew, ReadOne(map, root, "x").Stats.Bytes + " B");
                T.Check("live map: and only the changed folder was read again", map.Listings - listed <= 2, (map.Listings - listed) + " listings");

                string w = Path.Combine(x, "w");
                Directory.Move(y, w);
                bool moved = WaitFor(delegate
                {
                    MapEntry[] e = map.Read(x, new string[] { "y", "w" });
                    return !e[0].Found && e[1].Found && e[1].Complete && e[1].Stats.Bytes == 800;
                }, MapWaitMs);
                T.Check("live map: a renamed folder keeps its subtree under the new name", moved);
                T.Eq("live map: a rename does not change the parent", 800L, ReadOne(map, root, "x").Stats.Bytes);
                T.Check("live map: the moved subtree is still addressable", ReadOne(map, w, "z").Stats.Bytes == 800);

                File.Delete(Path.Combine(Path.Combine(w, "z"), "new.bin"));
                bool shrank = WaitFor(delegate { MapEntry m = ReadOne(map, root, "x"); return m.Complete && m.Stats.Bytes == 300; }, MapWaitMs);
                T.Check("live map: a deleted file leaves the totals", shrank, ReadOne(map, root, "x").Stats.Bytes + " B");

                Directory.Delete(Path.Combine(root, "gone"));
                bool removed = WaitFor(delegate { return !ReadOne(map, root, "gone").Found; }, MapWaitMs);
                T.Check("live map: a deleted folder leaves the map", removed);
                T.Eq("live map: root total after the changes", 300L, map.RootStats.Bytes);

                // Переполнение: где изменения — неизвестно, перепроверяется всё, прежние числа остаются на виду.
                long before = map.Listings;
                map.OverflowForTest();
                MapEntry kept = ReadOne(map, root, "x");
                T.Check("overflow: the numbers stay visible while the map is rechecked", kept.Found && kept.Stats.Bytes == 300, kept.Stats.Bytes + " B");
                bool rechecked = WaitFor(delegate { return map.IsComplete && map.Listings - before >= 4; }, MapWaitMs);
                T.Check("overflow: every folder is read again and the map is complete", rechecked, (map.Listings - before) + " listings");
                T.Eq("overflow: same total after the recheck", 300L, map.RootStats.Bytes);
            }
        }

        // ---------- движок панели поверх карты ----------
        private static void MapEngine()
        {
            string root = Fx.MakeDir(Fx.Root, "fs-map-engine");
            string p = Fx.MakeDir(root, "p");
            Fx.MakeFile(Path.Combine(Fx.MakeDir(p, "q"), "one.bin"), 1000);
            Fx.MakeDir(root, "r");

            SynchronizationContext was = SynchronizationContext.Current;
            Pump pump = new Pump();
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                FsSettings settings = new FsSettings();
                settings.UseFastNtfsIndex = false;
                settings.ShowHidden = true;
                settings.Sort = SizeSortMode.SizeDescending;
                string store = Path.Combine(Fx.MakeDir(Fx.Root, "fs-map-store"), "sizes.json");
                ListingSnapshot last = null;
                using (SizeEngine engine = new SizeEngine(settings, store, VolumeMaps.ForRoots(root)))
                {
                    engine.RowsChanged += delegate(ListingSnapshot s) { if (string.Equals(s.Path, root, StringComparison.OrdinalIgnoreCase)) last = s; };
                    engine.ShowFolder(root);
                    bool ok = pump.RunUntil(delegate { return last != null && last.Final; }, MapWaitMs);
                    T.Check("map engine: a final snapshot arrives", ok);
                    if (!ok) return;
                    SizeRow row = Find(last, "p");
                    T.Eq("map engine: folder p", 1000L, row == null ? -1 : row.Bytes);
                    T.Check("map engine: the larger folder is first", last.Rows.Count > 0 && last.Rows[0].Name == "p");

                    // Без нового захода в папку: изменение внутри доходит до строки само.
                    Fx.MakeFile(Path.Combine(Path.Combine(p, "q"), "two.bin"), 4000);
                    bool updated = pump.RunUntil(delegate
                    {
                        SizeRow r = last == null ? null : Find(last, "p");
                        return r != null && last.Final && r.Bytes == 5000;
                    }, MapWaitMs);
                    SizeRow now = Find(last, "p");
                    T.Check("map engine: a change deep inside updates the shown row without re-navigation", updated, now == null ? "no row" : now.Bytes + " B");
                }
                SizeCacheStore reread = new SizeCacheStore(store);
                reread.Load();
                SizeCacheStore.Entry cached;
                T.Check("map engine: map numbers are remembered for the next start", reread.TryGet(p, out cached) && cached.Bytes == 5000,
                        "entries " + reread.Count);
            }
            finally { SynchronizationContext.SetSynchronizationContext(was); }
        }
    }
}
