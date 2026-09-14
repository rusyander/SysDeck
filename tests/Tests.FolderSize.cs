// Windows Process Cleaner — область «foldersize»: сходятся ли размеры папок на настоящем дереве файлов
// (обходчик, движок панели и консольная --foldersize-measure собранного exe), не уходит ли обход по
// ссылкам и в папки с именами, которые Win32 переписывает, и держится ли разбор записей $MFT на мусоре.
//
// Прогон не запускает фоновый режим, не трогает автозапуск (ни ключ Run, ни Планировщик), не сигналит
// событиям настоящего фонового процесса и не читает том напрямую: для $MFT нужны права администратора,
// поэтому разбор записи проверяется на записях, собранных по формату NTFS.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml;
using WindowsProcessCleaner.FolderSize;

namespace WindowsProcessCleaner.Tests
{
    internal static class FolderSizeTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool RemoveDirectoryW(string path);

        internal static void Run()
        {
            Format();
            Unwalkable();
            Settings();
            TaskDefinition();
            Ipc();
            MftRecords();
            MftHardLinks();
            Helpers();
            Extras();

            string root = Fx.MakeDir(Fx.Root, "foldersize");
            Tree tree = BuildTree(root);
            try
            {
                Walker(tree);
                EngineLive(tree);
                CommandLine(tree);
            }
            finally
            {
                // Папку «x. » обычным путём не удалить: Win32 срезал бы хвост и промахнулся.
                if (tree.BadDir != null) RemoveDirectoryW(@"\\?\" + tree.BadDir);
            }
        }

        // ---------- числа как у Проводника ----------
        private static void Format()
        {
            bool was = Tr.En;
            try
            {
                Tr.En = true;
                T.Eq("zero bytes", "0 B", SizeFormat.Short(0));
                T.Eq("under a kilobyte stays in bytes", "1,023 B", SizeFormat.Short(1023));
                T.Eq("one and a half kilobytes", "1.50 KB", SizeFormat.Short(1536));
                T.Eq("tens keep one decimal", "15.0 MB", SizeFormat.Short(15L * 1024 * 1024));
                T.Eq("hundreds keep none", "150 GB", SizeFormat.Short(150L * 1024 * 1024 * 1024));
                T.Eq("negative is a dash", "—", SizeFormat.Short(-1));
                T.Eq("exact bytes, singular", "1 byte", SizeFormat.ExactBytes(1));
                T.Eq("items, English", "3 files, 1 folder", SizeFormat.Items(3, 1));

                SizeRow link = new SizeRow();
                link.IsDirectory = true;
                link.Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint;
                T.Eq("a link cell shows an arrow, not a size", "→", InlineSizeOverlay.CellText(link, 0));
                SizeRow partial = new SizeRow();
                partial.IsDirectory = true;
                partial.State = RowState.Partial;
                partial.Bytes = 2048;
                T.Eq("a partial size is a lower bound", "≥ 2.00 KB", InlineSizeOverlay.CellText(partial, 0));

                Tr.En = false;
                T.Eq("Russian units", "1,50 КБ", SizeFormat.Short(1536));
                string[] expect = { "файл", "файла", "файлов", "файлов", "файл", "файлов", "файла" };
                long[] n = { 1, 2, 5, 11, 21, 112, 1024 };
                for (int i = 0; i < n.Length; i++)
                    T.Eq("Russian plural for " + n[i], expect[i], SizeFormat.Plural(n[i], "файл", "файла", "файлов"));
            }
            finally { Tr.En = was; }
        }

        // ---------- имена, которые Win32 переписывает ----------
        private static void Unwalkable()
        {
            T.Check("NUL is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\NUL"));
            T.Check("com1.txt is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\com1.txt"));
            T.Check("'.. ' is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\.. "));
            T.Check("a trailing dot is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\name."));
            T.Check("a trailing space is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\name "));
            T.Check("console is a normal name", !DirectoryWalker.IsUnwalkableDirectory(@"C:\x\console"));
            T.Check("a dotted normal name is fine", !DirectoryWalker.IsUnwalkableDirectory(@"C:\x\v1.2.release"));
            T.Check("an empty leaf is refused", DirectoryWalker.IsUnwalkableDirectory(@"C:\x\"));
        }

        // ---------- settings.json ----------
        private static void Settings()
        {
            FsSettings s = new FsSettings();
            s.InlineOverlay = false; s.OverlayFileSizes = true; s.ShowSidePanel = false; s.DockRight = false;
            s.PanelWidth = 500; s.Sort = SizeSortMode.ItemsDescending; s.FollowSystemTheme = false; s.DarkTheme = false;
            s.UseFastNtfsIndex = false; s.ShowHidden = false; s.MakeRoomForPanel = true;
            FsSettings back = FsSettings.FromJson(s.ToJson());
            T.Check("settings survive a round trip",
                    back != null && !back.InlineOverlay && back.OverlayFileSizes && !back.ShowSidePanel && !back.DockRight
                    && back.PanelWidth == 500 && back.Sort == SizeSortMode.ItemsDescending && !back.FollowSystemTheme
                    && !back.DarkTheme && !back.UseFastNtfsIndex && !back.ShowHidden && back.MakeRoomForPanel);

            FsSettings wide = FsSettings.FromJson("{\"PanelWidth\": 99999, \"Sort\": \"NameAscending\"}");
            T.Eq("panel width is clamped", FsSettings.MaxPanelWidth, wide.PanelWidth);
            T.Eq("the standalone program's string sort is read", SizeSortMode.NameAscending, wide.Sort);
            T.Eq("an out-of-range numeric sort keeps the default", SizeSortMode.SizeDescending, FsSettings.FromJson("{\"Sort\": 7}").Sort);
            T.Check("garbage is not a settings object", FsSettings.FromJson("{not json") == null);

            string file = Path.Combine(Fx.MakeDir(Fx.Root, "fs-settings"), "settings.json");
            File.WriteAllText(file, "\u0000\u0001garbage");
            FsSettings fallback = FsSettings.Load(file);
            T.Check("a broken file loads defaults", fallback.InlineOverlay && fallback.PanelWidth == 360 && fallback.UseFastNtfsIndex);
            s.Save(file);
            T.Eq("save then load keeps the sort", SizeSortMode.ItemsDescending, FsSettings.Load(file).Sort);
            T.Check("the data folder is redirected with the app's", Fx.IsUnder(FsPaths.DataDir, Fx.Root), FsPaths.DataDir);
        }

        // ---------- горячая клавиша, свободное место, копирование списка ----------
        private static void Extras()
        {
            FsSettings defaults = FsSettings.FromJson("{\"InlineOverlay\": true}");
            T.Check("a settings file from before the extras turns them on",
                    defaults.PanelHotkey && defaults.ShowFreeSpace && defaults.PanelMenuExtras);
            FsSettings off = new FsSettings();
            off.PanelHotkey = false; off.ShowFreeSpace = false; off.PanelMenuExtras = false;
            FsSettings back = FsSettings.FromJson(off.ToJson());
            T.Check("switched-off extras survive a round trip", !back.PanelHotkey && !back.ShowFreeSpace && !back.PanelMenuExtras);
            FsSettings copy = new FsSettings();
            copy.CopyFrom(off);
            T.Check("CopyFrom carries the extras (reload from the app page)", !copy.PanelHotkey && !copy.ShowFreeSpace && !copy.PanelMenuExtras);

            T.Eq("drive label", "C:", VolumeSpace.LabelOf(@"c:\Users\x"));
            T.Eq("share label", @"\\srv\share", VolumeSpace.LabelOf(@"\\srv\share\dir\sub"));
            VolumeSpace space = VolumeSpace.Query(Fx.Root);
            T.Check("free space of the test folder's volume is read", space != null && space.Total > 0 && space.Free >= 0 && space.Free <= space.Total,
                    space == null ? "null" : space.Free + "/" + space.Total);
            T.Check("a missing folder gives no space instead of throwing", VolumeSpace.Query(Path.Combine(Fx.Root, "no-such-dir-" + Guid.NewGuid().ToString("N"))) == null);

            bool was = Tr.En;
            try
            {
                Tr.En = true;
                ListingSnapshot listing = new ListingSnapshot();
                listing.Path = @"D:\data";
                SizeRow done = new SizeRow { Name = "big", FullPath = @"D:\data\big", IsDirectory = true, Bytes = 3L << 30, Files = 12, Directories = 3, State = RowState.Ready };
                SizeRow pending = new SizeRow { Name = "wait", FullPath = @"D:\data\wait", IsDirectory = true, State = RowState.Pending };
                SizeRow file = new SizeRow { Name = "a.txt", DisplayName = "a.txt", FullPath = @"D:\data\a.txt", Bytes = 10, State = RowState.Ready };
                listing.Rows = new List<SizeRow> { done, pending, file };
                listing.Totals = new DirStats((3L << 30) + 10, 13, 3, false);
                string[] lines = TrayApp.ListingText(listing).Split(new[] { "\r\n" }, StringSplitOptions.None);
                T.Eq("list: path, header, three rows, total, trailing break", 7, lines.Length);
                T.Eq("list header is tab-separated", "Name\tSize\tBytes\tFiles\tFolders", lines[1]);
                T.Eq("a counted folder carries exact bytes and counts", "big\t3.00 GB\t3221225472\t12\t3", lines[2]);
                T.Eq("a folder still counting has empty cells, not zero", "wait\t\t\t\t", lines[3]);
                T.Eq("a file has no folder counts", "a.txt\t10 B\t10\t\t", lines[4]);
                T.Check("total line", lines[5].StartsWith("Total\t", StringComparison.Ordinal) && lines[5].EndsWith("\t3221225482\t13\t3", StringComparison.Ordinal), lines[5]);
                T.Eq("no listing copies nothing", "", TrayApp.ListingText(null));
            }
            finally { Tr.En = was; }
        }

        // ---------- задача Планировщика: XML, который schtasks примет ----------
        private static void TaskDefinition()
        {
            string exe = @"C:\Program Files\A & B <x>\WindowsProcessCleaner.exe";
            XmlDocument doc = new XmlDocument();
            string error = null;
            try { doc.LoadXml(FsAutoStart.TaskXml(exe)); }
            catch (Exception ex) { error = ex.Message; }
            T.Check("the task XML is well-formed with & and < in the path", error == null, error);
            if (error != null) return;
            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
            XmlNode command = doc.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", ns);
            XmlNode args = doc.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Arguments", ns);
            XmlNode level = doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:RunLevel", ns);
            XmlNode logon = doc.SelectSingleNode("/t:Task/t:Triggers/t:LogonTrigger", ns);
            XmlNode limit = doc.SelectSingleNode("/t:Task/t:Settings/t:ExecutionTimeLimit", ns);
            T.Eq("the task runs this exe", exe, command == null ? null : command.InnerText);
            T.Eq("the task starts background mode", "--foldersize", args == null ? null : args.InnerText);
            T.Eq("the task runs with highest rights", "HighestAvailable", level == null ? null : level.InnerText);
            T.Check("the task starts at logon", logon != null);
            T.Eq("the task is never killed by a time limit", "PT0S", limit == null ? null : limit.InnerText);
        }

        // ---------- именованные события: окно без прав должно достучаться ----------
        private static void Ipc()
        {
            string name = @"Local\WindowsProcessCleaner.FolderSize.Test-" + Process.GetCurrentProcess().Id;
            using (EventWaitHandle ev = FsIpc.CreateEvent(name))
            {
                T.Check("a created event is not set", !ev.WaitOne(0));
                T.Check("signal opens the event by name", FsIpc.Signal(name));
                T.Check("the signal arrives", ev.WaitOne(0));
                T.Check("auto-reset: it arrives once", !ev.WaitOne(0));
            }
            T.Check("signalling a missing event reports false", !FsIpc.Signal(name + ".missing"));
        }

        // ---------- записи $MFT ----------
        private const int Rec = 1024, Sector = 512;

        private static void MftRecords()
        {
            long[] sizes = new long[64];
            uint[] parents = new uint[64];
            byte[] flags = new byte[64];
            Dictionary<uint, string> dirs = new Dictionary<uint, string>();
            List<long> links = new List<long>();

            byte[] file = Record(false, 0);
            int at = AddFileName(file, 0x38, 5, 1, "report.txt");
            at = AddResidentData(file, at, 100);
            End(file, at);
            MftIndexBuilder.ParseRecordForTest(file, 40, sizes, parents, flags, dirs, links);
            T.Eq("resident data size is counted", 100L, sizes[40]);
            T.Eq("the parent comes from the file name", 5u, parents[40]);
            T.Eq("a file is in use and has a parent", (byte)5, flags[40]);
            T.Check("a file is not a directory name", !dirs.ContainsKey(40));

            byte[] dir = Record(true, 0);
            at = AddFileName(dir, 0x38, 7, 2, "DOCUME~1");
            at = AddFileName(dir, at, 5, 1, "Документы");
            End(dir, at);
            MftIndexBuilder.ParseRecordForTest(dir, 41, sizes, parents, flags, dirs, links);
            T.Eq("the Win32 name wins over the 8.3 alias", "Документы", dirs.ContainsKey(41) ? dirs[41] : null);
            T.Eq("and so does its parent", 5u, parents[41]);

            byte[] ext = Record(false, 40);
            at = AddNonResidentData(ext, 0x38, 5000);
            End(ext, at);
            MftIndexBuilder.ParseRecordForTest(ext, 42, sizes, parents, flags, dirs, links);
            T.Eq("an extension record's data belongs to its base file", 5100L, sizes[40]);
            T.Eq("an extension record is not an entry of its own", (byte)0, flags[42]);

            byte[] root = Record(true, 0);
            at = AddFileName(root, 0x38, 5, 1, ".");
            End(root, at);
            MftIndexBuilder.ParseRecordForTest(root, 5, sizes, parents, flags, dirs, links);
            T.Eq("the root's own name is empty", "", dirs.ContainsKey(5) ? dirs[5] : null);

            byte[] torn = Record(false, 0);
            at = AddResidentData(torn, 0x38, 777);
            End(torn, at);
            torn[Sector - 2] = 0x00;       // сектор записан не до конца: штамп не совпал
            MftIndexBuilder.ParseRecordForTest(torn, 43, sizes, parents, flags, dirs, links);
            T.Check("a torn record is ignored", sizes[43] == 0 && flags[43] == 0);

            byte[] liar = Record(false, 0);
            WriteU32(liar, 0x38, 0x80);
            WriteU32(liar, 0x3C, 0x7FFFFFF0);    // длина атрибута за пределами записи
            MftIndexBuilder.ParseRecordForTest(liar, 44, sizes, parents, flags, dirs, links);
            T.Check("an attribute longer than the record is not read", sizes[44] == 0);

            Random rnd = new Random(20260913);
            string crash = null;
            for (int i = 0; i < 3000 && crash == null; i++)
            {
                byte[] junk = Record(rnd.Next(2) == 0, (ulong)rnd.Next(80));
                byte[] noise = new byte[Rec - 0x38 - 64];
                rnd.NextBytes(noise);
                Buffer.BlockCopy(noise, 0, junk, 0x38, noise.Length);
                Stamp(junk);
                try { MftIndexBuilder.ParseRecordForTest(junk, (uint)rnd.Next(64), sizes, parents, flags, dirs, links); }
                catch (Exception ex) { crash = ex.GetType().Name + " at #" + i; }
            }
            T.Check("3000 records of random attributes never throw", crash == null, crash);

            // Том с логическим сектором 4096 (Storage Spaces, 4Kn): защита записи идёт шагом 512, как на любом томе.
            MftIndexBuilder.VolumeGeometry small = MftIndexBuilder.VolumeGeometry.Parse(Boot(4096, 1, 0x30000, 0xF6));
            T.Check("a 4K-sector volume with 1024-byte records is accepted",
                    small.RecordSize == 1024 && small.ClusterSize == 4096 && small.MftOffset == 0x30000L * 4096,
                    small.RecordSize + "/" + small.ClusterSize + "/" + small.MftOffset);
            T.Eq("a 4K-sector volume with 4096-byte records", 4096, MftIndexBuilder.VolumeGeometry.Parse(Boot(4096, 1, 4, 0xF4)).RecordSize);
            byte[] big = Record(false, 0, 4096);
            at = AddResidentData(big, 0x48, 300);
            End(big, at);
            MftIndexBuilder.ParseRecordForTest(big, 45, sizes, parents, flags, dirs, links);
            T.Eq("a 4096-byte record's nine fixups are applied every 512 bytes", 300L, sizes[45]);
            big = Record(false, 0, 4096);
            at = AddResidentData(big, 0x48, 301);
            End(big, at);
            big[7 * Sector - 2] = 0x00;
            MftIndexBuilder.ParseRecordForTest(big, 46, sizes, parents, flags, dirs, links);
            T.Check("a torn seventh stride of a 4096-byte record is caught", sizes[46] == 0 && flags[46] == 0);
        }

        // Жёсткие ссылки pnpm: обход и Проводник считают файл в каждой папке, где у него есть имя, — индекс тоже.
        private static void MftHardLinks()
        {
            byte[] root = Record(true, 0);
            End(root, AddFileName(root, 0x38, 5, 1, "."));
            byte[] dirA = Record(true, 0);
            End(dirA, AddFileName(dirA, 0x38, 5, 1, "a"));
            byte[] dirB = Record(true, 0);
            End(dirB, AddFileName(dirB, 0x38, 5, 1, "b"));

            byte[] shared = Record(false, 0);                  // x.bin в a и y.bin в b, у второго ещё DOS-псевдоним
            int at = AddFileName(shared, 0x38, 41, 3, "x.bin");
            at = AddFileName(shared, at, 42, 1, "y-long-name.bin");
            at = AddFileName(shared, at, 42, 2, "Y-LONG~1.BIN");
            End(shared, AddResidentData(shared, at, 100));

            byte[] big = Record(false, 0);                     // имя в a, второе имя — в записи-расширении
            End(big, AddResidentData(big, AddFileName(big, 0x38, 41, 1, "big"), 50));
            byte[] bigExt = Record(false, 43);
            End(bigExt, AddFileName(bigExt, 0x38, 42, 1, "big"));

            long[] sizes = new long[64];
            uint[] parents = new uint[64];
            byte[] flags = new byte[64];
            Dictionary<uint, string> dirNames = new Dictionary<uint, string>();
            List<long> links = new List<long>();
            // Запись-расширение 39 идёт раньше своей базовой 43 — так бывает в настоящей таблице.
            Dictionary<uint, NtfsVolumeIndex.DirNode> index = MftIndexBuilder.IndexRecordsForTest(
                new[] { root, bigExt, shared, dirA, dirB, big }, new uint[] { 5, 39, 40, 41, 42, 43 }, 64,
                sizes, parents, flags, dirNames, links);

            NtfsVolumeIndex.DirNode a = index[41], b = index[42], top = index[5];
            T.Check("a folder counts a hard-linked file under its name there", a.TotalBytes == 150 && a.TotalFiles == 2,
                    a.TotalBytes + " B, " + a.TotalFiles + " files");
            T.Check("the other folder counts it too, the DOS alias adds nothing, a name from an extension record counts",
                    b.TotalBytes == 150 && b.TotalFiles == 2, b.TotalBytes + " B, " + b.TotalFiles + " files");
            T.Check("the volume total is the sum of its folders, as a walk reports it",
                    top.TotalBytes == 300 && top.TotalFiles == 4 && top.TotalDirs == 2,
                    top.TotalBytes + " B, " + top.TotalFiles + " files, " + top.TotalDirs + " dirs");
        }

        private static byte[] Boot(int bytesPerSector, byte sectorsPerCluster, long mftLcn, byte recordSize)
        {
            byte[] b = new byte[512];
            b[3] = (byte)'N'; b[4] = (byte)'T'; b[5] = (byte)'F'; b[6] = (byte)'S';
            WriteU16(b, 0x0B, (ushort)bytesPerSector);
            b[0x0D] = sectorsPerCluster;
            WriteU32(b, 0x30, (uint)mftLcn);
            b[0x40] = recordSize;
            return b;
        }

        private static byte[] Record(bool directory, ulong baseRef) { return Record(directory, baseRef, Rec); }

        private static byte[] Record(bool directory, ulong baseRef, int size)
        {
            byte[] r = new byte[size];
            int fixups = size / Sector;
            r[0] = (byte)'F'; r[1] = (byte)'I'; r[2] = (byte)'L'; r[3] = (byte)'E';
            WriteU16(r, 0x04, 0x30);                          // массив исправлений
            WriteU16(r, 0x06, (ushort)(fixups + 1));          // штамп + по слову на каждые 512 байт
            WriteU16(r, 0x14, (ushort)Align8(0x30 + (fixups + 1) * 2));   // первый атрибут — сразу за массивом
            WriteU16(r, 0x16, (ushort)(directory ? 3 : 1));
            WriteU32(r, 0x18, (uint)size);
            WriteU32(r, 0x20, (uint)baseRef);
            WriteU16(r, 0x30, 0xABCD);
            Stamp(r);
            return r;
        }

        // Хвосты шагов по 512 байт на диске несут штамп, а настоящие байты лежат в массиве исправлений.
        private static void Stamp(byte[] r)
        {
            for (int stride = 1; stride <= r.Length / Sector; stride++)
            {
                int tail = stride * Sector - 2;
                r[0x30 + stride * 2] = r[tail];
                r[0x30 + stride * 2 + 1] = r[tail + 1];
                WriteU16(r, tail, 0xABCD);
            }
        }

        private static int AddFileName(byte[] r, int at, uint parent, byte nameSpace, string name)
        {
            int valueLength = 0x42 + name.Length * 2;
            int length = Align8(0x18 + valueLength);
            WriteU32(r, at, 0x30);
            WriteU32(r, at + 4, (uint)length);
            WriteU32(r, at + 0x10, (uint)valueLength);
            WriteU16(r, at + 0x14, 0x18);
            int value = at + 0x18;
            WriteU32(r, value, parent);
            r[value + 0x40] = (byte)name.Length;
            r[value + 0x41] = nameSpace;
            byte[] chars = Encoding.Unicode.GetBytes(name);
            Buffer.BlockCopy(chars, 0, r, value + 0x42, chars.Length);
            return at + length;
        }

        private static int AddResidentData(byte[] r, int at, int bytes)
        {
            int length = Align8(0x18 + bytes);
            WriteU32(r, at, 0x80);
            WriteU32(r, at + 4, (uint)length);
            WriteU32(r, at + 0x10, (uint)bytes);
            WriteU16(r, at + 0x14, 0x18);
            return at + length;
        }

        private static int AddNonResidentData(byte[] r, int at, long bytes)
        {
            WriteU32(r, at, 0x80);
            WriteU32(r, at + 4, 0x48);
            r[at + 8] = 1;
            WriteU32(r, at + 0x30, (uint)bytes);      // реальный размер; StartVCN (0x10) = 0
            return at + 0x48;
        }

        private static void End(byte[] r, int at) { WriteU32(r, at, 0xFFFFFFFF); }
        private static int Align8(int n) { return (n + 7) & ~7; }
        private static void WriteU16(byte[] b, int o, ushort v) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
        private static void WriteU32(byte[] b, int o, uint v) { WriteU16(b, o, (ushort)v); WriteU16(b, o + 2, (ushort)(v >> 16)); }

        // ---------- мелочи интерфейса ----------
        private static void Helpers()
        {
            T.Eq("the title of a drive root is the drive", "C:", PanelForm.FolderTitle(@"C:\"));
            T.Eq("the title is the leaf", "Projects", PanelForm.FolderTitle(@"D:\Work\Projects\"));
            List<string> candidates = new List<string>(new string[] { @"C:\A\Docs", @"D:\B\Docs2" });
            T.Eq("of two same-titled windows the exact leaf wins", @"D:\B\Docs2", ShellPathResolver.Pick(candidates, "Docs2"));
            T.Check("a CLSID location is not a folder", !ShellPathResolver.IsRealDirectory("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
            T.Check("a relative path is not a folder", !ShellPathResolver.IsRealDirectory("Documents"));
            T.Check("the fixture root is a folder", ShellPathResolver.IsRealDirectory(Fx.Root));
            ExplorerState a = new ExplorerState(new IntPtr(1), @"C:\A", new Rectangle(0, 0, 10, 10), true);
            T.Check("the same window state compares equal", a.SameAs(new ExplorerState(new IntPtr(1), @"C:\A", new Rectangle(0, 0, 10, 10), true)));
            T.Check("a moved window is a change", !a.SameAs(new ExplorerState(new IntPtr(1), @"C:\A", new Rectangle(1, 0, 10, 10), true)));
        }

        // ---------- настоящее дерево ----------
        private sealed class Tree
        {
            public string Root, A, B, Link, BadDir;
            public bool HasLink;
            public const long ABytes = 1000 + 2345, CBytes = 77, HiddenBytes = 10;
        }

        private static Tree BuildTree(string root)
        {
            Tree t = new Tree();
            t.Root = root;
            t.A = Fx.MakeDir(root, "a");
            Fx.MakeFile(Path.Combine(t.A, "f1.bin"), 1000);
            Fx.MakeFile(Path.Combine(Fx.MakeDir(t.A, "sub"), "f2.bin"), 2345);
            t.B = Fx.MakeDir(root, "b");
            Fx.MakeFile(Path.Combine(root, "c.txt"), 77);
            string hidden = Fx.MakeFile(Path.Combine(root, "h.bin"), 10);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            t.Link = Path.Combine(root, "link");
            t.HasLink = Fx.Junction(t.Link, t.A);
            // «x. » внутри a: Win32 открыл бы вместо неё саму a, и обход пошёл бы по кругу.
            string bad = Path.Combine(t.A, "x. ");
            if (CreateDirectoryW(@"\\?\" + bad, IntPtr.Zero)) t.BadDir = bad;
            return t;
        }

        private static void Walker(Tree t)
        {
            List<SizeRow> visible = DirectoryWalker.ListChildren(t.Root, false);
            List<SizeRow> all = DirectoryWalker.ListChildren(t.Root, true);
            T.Check("hidden files are listed only on request",
                    !visible.Exists(delegate(SizeRow r) { return r.Name == "h.bin"; }) && all.Exists(delegate(SizeRow r) { return r.Name == "h.bin"; }));

            TreeMeasurer.Job a = NewJob(t.A), b = NewJob(t.B);
            List<TreeMeasurer.Job> jobs = new List<TreeMeasurer.Job>(new TreeMeasurer.Job[] { a, b });
            int done = 0;
            Stopwatch sw = Stopwatch.StartNew();
            TreeMeasurer.Run(jobs, new ProgressCounters(), delegate { Interlocked.Increment(ref done); }, CancellationToken.None);
            T.Eq("a folder is the sum of every file inside", Tree.ABytes, a.Bytes);
            T.Eq("files are counted through subfolders", 2L, a.Files);
            T.Eq("an empty folder is zero", 0L, b.Bytes);
            T.Eq("each job reports done once", 2, done);
            T.Check("both jobs are done", a.Done && b.Done);
            if (t.BadDir != null)
            {
                T.Check("a folder named 'x. ' is not walked into and the size is a lower bound", a.Partial && a.Directories == 2, a.Directories + " dirs");
                T.Check("and the walk ends instead of looping", sw.ElapsedMilliseconds < 30000, sw.ElapsedMilliseconds + " ms");
            }
            else T.Skip("unwalkable folder name", "CreateDirectoryW with \\\\?\\ refused 'x. '");

            if (t.HasLink)
            {
                TreeMeasurer.Job outer = NewJob(t.Root);
                TreeMeasurer.Run(new List<TreeMeasurer.Job>(new TreeMeasurer.Job[] { outer }), null, null, CancellationToken.None);
                T.Eq("a junction's target is not counted twice", Tree.ABytes + Tree.CBytes + Tree.HiddenBytes, outer.Bytes);
            }
            else T.Skip("junction not followed", "mklink /J failed");

            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();
            bool threw = false;
            try { TreeMeasurer.Run(new List<TreeMeasurer.Job>(new TreeMeasurer.Job[] { NewJob(t.Root) }), null, null, cts.Token); }
            catch (OperationCanceledException) { threw = true; }
            T.Check("a cancelled measurement stops with OperationCanceledException", threw);
        }

        private static TreeMeasurer.Job NewJob(string path)
        {
            SizeRow row = new SizeRow();
            row.Name = DirectoryWalker.LeafOf(path);
            row.FullPath = path;
            row.IsDirectory = true;
            TreeMeasurer.Job job = new TreeMeasurer.Job();
            job.Row = row;
            return job;
        }

        // Движок панели шлёт результаты через контекст синхронизации. Здесь это один поток с очередью —
        // как у окна, но без окна: так проверяется весь путь «листинг → подсчёт → итоговый снимок».
        private sealed class Pump : SynchronizationContext
        {
            private readonly BlockingCollection<KeyValuePair<SendOrPostCallback, object>> _queue =
                new BlockingCollection<KeyValuePair<SendOrPostCallback, object>>();

            public override void Post(SendOrPostCallback d, object state) { _queue.Add(new KeyValuePair<SendOrPostCallback, object>(d, state)); }
            public override void Send(SendOrPostCallback d, object state) { throw new NotSupportedException(); }

            public bool RunUntil(Func<bool> done, int timeoutMs)
            {
                Stopwatch sw = Stopwatch.StartNew();
                while (!done())
                {
                    long left = timeoutMs - sw.ElapsedMilliseconds;
                    if (left <= 0) return false;
                    KeyValuePair<SendOrPostCallback, object> item;
                    if (_queue.TryTake(out item, (int)Math.Min(left, 100))) item.Key(item.Value);
                }
                return true;
            }
        }

        private static void EngineLive(Tree t)
        {
            SynchronizationContext was = SynchronizationContext.Current;
            Pump pump = new Pump();
            SynchronizationContext.SetSynchronizationContext(pump);
            try
            {
                FsSettings settings = new FsSettings();
                settings.UseFastNtfsIndex = false;
                settings.ShowHidden = true;
                settings.Sort = SizeSortMode.SizeDescending;
                string store = Path.Combine(Fx.MakeDir(Fx.Root, "fs-engine"), "sizes.json");
                ListingSnapshot final = null;
                int snapshots = 0;
                using (SizeEngine engine = new SizeEngine(settings, store))
                {
                    engine.RowsChanged += delegate(ListingSnapshot s)
                    {
                        snapshots++;
                        if (s.Final && string.Equals(s.Path, t.Root, StringComparison.OrdinalIgnoreCase)) final = s;
                    };
                    engine.ShowFolder(t.Root);
                    bool ok = pump.RunUntil(delegate { return final != null; }, 60000);
                    T.Check("the engine publishes a final snapshot", ok, snapshots + " snapshots");
                    if (!ok) return;

                    SizeRow a = Find(final, "a"), b = Find(final, "b"), link = Find(final, "link");
                    T.Eq("engine: folder a", Tree.ABytes, a == null ? -1 : a.Bytes);
                    T.Eq("engine: folder b", 0L, b == null ? -1 : b.Bytes);
                    T.Check("engine: the largest row is first when sorted by size", final.Rows.Count > 0 && final.Rows[0].Name == "a");
                    if (t.BadDir != null) T.Eq("engine: a is marked as a lower bound", RowState.Partial, a == null ? RowState.Failed : a.State);
                    if (t.HasLink)
                        T.Check("engine: the junction row is not measured", link != null && link.IsReparsePoint && link.Bytes == 0,
                                link == null ? "no row" : link.Bytes + " B");
                    long expected = Tree.ABytes + Tree.CBytes + Tree.HiddenBytes;
                    T.Eq("engine: listing total", expected, final.Totals.Bytes);
                }
                SizeCacheStore reread = new SizeCacheStore(store);
                reread.Load();
                SizeCacheStore.Entry cached;
                T.Check("measured folders are remembered for the next start",
                        reread.TryGet(t.A, out cached) && cached.Bytes == Tree.ABytes, "entries " + reread.Count);
            }
            finally { SynchronizationContext.SetSynchronizationContext(was); }
        }

        private static SizeRow Find(ListingSnapshot s, string name)
        {
            foreach (SizeRow r in s.Rows) if (r.Name == name) return r;
            return null;
        }

        // ---------- --foldersize-measure собранного exe: вход, которым пользуется человек ----------
        private static void CommandLine(Tree t)
        {
            string app = Environment.GetEnvironmentVariable("WPC_TEST_APP");
            if (string.IsNullOrEmpty(app) || !File.Exists(app)) { T.Skip("--foldersize-measure", "WPC_TEST_APP not set"); return; }
            // Exe старее порта не знает ключа и открыл бы главное окно — это не проверка, а помеха.
            string source = Path.Combine(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(app)), "src"), "FolderSize.Tray.cs");
            if (File.Exists(source) && File.GetLastWriteTimeUtc(app) < File.GetLastWriteTimeUtc(source))
            {
                T.Skip("--foldersize-measure", "the app binary predates src\\FolderSize.Tray.cs — run build.bat");
                return;
            }

            string output = RunApp(app, FsMode.MeasureSwitch + " \"" + t.Root + "\"");
            T.Check("measure prints a total", output != null && output.Contains("total "), output);
            if (output == null) return;
            T.Eq("measure: total without hidden files", Tree.ABytes + Tree.CBytes, TotalOf(output));
            if (t.HasLink) T.Check("measure: the junction is reported as not followed", output.Contains("link(s) not followed: link"), output);

            string withHidden = RunApp(app, FsMode.MeasureSwitch + " \"" + t.Root + "\" --hidden");
            T.Eq("measure --hidden: total with hidden files", Tree.ABytes + Tree.CBytes + Tree.HiddenBytes, TotalOf(withHidden));

            string missing = RunApp(app, FsMode.MeasureSwitch + " \"" + Path.Combine(t.Root, "no-such") + "\"");
            T.Check("measure of a missing folder says so", missing != null && missing.Contains("not a folder"), missing);
        }

        private static string RunApp(string app, string arguments)
        {
            ProcessStartInfo psi = new ProcessStartInfo(app, arguments);
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Encoding.UTF8;
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string err = null;
                    Thread reader = new Thread(delegate() { err = p.StandardError.ReadToEnd(); });
                    reader.Start();
                    string text = p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return null; }
                    reader.Join(5000);
                    return text + err;
                }
            }
            catch { return null; }
        }

        // «total 3 422 B (…)»: разделитель тысяч зависит от языка системы, поэтому — только цифры.
        private static long TotalOf(string output)
        {
            if (output == null) return -1;
            int at = output.IndexOf("total ", StringComparison.Ordinal);
            if (at < 0) return -1;
            int end = output.IndexOf(" B", at, StringComparison.Ordinal);
            if (end < 0) return -1;
            StringBuilder digits = new StringBuilder();
            for (int i = at + 6; i < end; i++) if (char.IsDigit(output[i])) digits.Append(output[i]);
            long v;
            return long.TryParse(digits.ToString(), out v) ? v : -1;
        }
    }
}
