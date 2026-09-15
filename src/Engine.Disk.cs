// SysDeck — вкладка «Диск»: карта папок, крупные файлы, пустые папки, дубликаты
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysDeck
{
    // Узел карты папок. Хранит только своё имя — полный путь собирается по цепочке
    // родителей: на системном диске полмиллиона каталогов, и путь в каждом узле стоил
    // бы больше ста мегабайт.
    public class DiskDir
    {
        public string Name;
        public DiskDir Parent;
        public List<DiskDir> Children;       // null = подпапок нет
        public long OwnSize;                 // файлы непосредственно в папке
        public long Size;                    // вместе с подпапками
        public int OwnFiles;
        public int Files;                    // вместе с подпапками
        public int Dirs;                     // подпапок всего (вложенно)
        public int Errors;                   // недоступных каталогов в поддереве
        public int Skipped;                  // пропущенных точек повторного разбора (junction/symlink) в поддереве
        public int Depth;

        public string Path
        {
            get
            {
                if (Parent == null) return Name;
                string p = Parent.Path;
                return p.EndsWith("\\") ? p + Name : p + "\\" + Name;
            }
        }
        // Пустая — ни одного файла в поддереве и ничего не пропущено/не отказано:
        // папка, внутри которой лежит недоступный или пропущенный каталог, пустой не считается.
        public bool IsEmpty { get { return Files == 0 && Errors == 0 && Skipped == 0; } }
    }

    public class DiskFile
    {
        public string Name;
        public DiskDir Dir;
        public long Size;
        public long ModifiedTicks;           // UTC FILETIME -> ticks; DateTime не хранится ради памяти
        public bool HiddenOrSystem;
        public string Path { get { string d = Dir.Path; return d.EndsWith("\\") ? d + Name : d + "\\" + Name; } }
        public DateTime Modified { get { try { return DateTime.FromFileTimeUtc(ModifiedTicks).ToLocalTime(); } catch { return DateTime.MinValue; } } }
    }

    public class DiskScanResult
    {
        public string Root;
        public DiskDir RootDir;
        public List<DiskDir> AllDirs = new List<DiskDir>();      // в порядке обхода: родитель всегда раньше ребёнка
        public List<DiskFile> BigFiles = new List<DiskFile>();   // файлы от Engine.DiskBigFileBytes
        public long TotalSize;
        public int TotalFiles;
        public int TotalDirs;
        public int Errors;
        public int Skipped;
        public bool Cancelled;
        public long ElapsedMs;
        public long MinBytes;                                    // порог «крупного файла», с которым шёл обход
    }

    public class DupGroup
    {
        public long Size;                    // размер одного файла
        public string Hash;                  // SHA-256 содержимого (hex)
        public List<DiskFile> Files = new List<DiskFile>();
        public long Waste { get { return Size * (Files.Count - 1); } }
    }

    public class DriveRow
    {
        public string Name;                  // "C:\"
        public string Label;
        public long Total;
        public long Free;
        public long Used { get { return Total - Free; } }
        public double UsedFraction { get { return Total > 0 ? (double)Used / Total : 0; } }
    }

    public partial class Engine
    {
        // Порог «крупного файла»: список хранит только такие, дерево папок считает всё.
        public const long DiskBigFileBytes = 1L << 20;
        // Порог, ниже которого дубликаты не ищутся: мелких одинаковых файлов (иконки,
        // локализации, .gitkeep) тысячи, и все они легитимны.
        public const long DiskDupMinBytes = 1L << 20;

        private volatile bool _cancelScan;
        public void CancelDiskScan() { _cancelScan = true; }
        public void ResetDiskScanCancel() { _cancelScan = false; }
        public bool DiskScanCancelled { get { return _cancelScan; } }

        public delegate void DiskProgress(long bytes, int files, int dirs, string current);

        private static string LongPath(string p)
        {
            if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\")) return p;
            return @"\\?\" + p;
        }

        // Обход без рекурсии, точки повторного разбора (junction/symlink/OneDrive-папки)
        // не раскрываются: за ними может лежать корень диска или облако. Файлы-плейсхолдеры
        // OneDrive считаются по логическому размеру — так же, как их показывает Проводник.
        public DiskScanResult ScanDisk(string root, DiskProgress progress)
        {
            return ScanDisk(root, DiskBigFileBytes, progress);
        }

        // minBytes — порог, от которого файлы попадают в список крупных (не ниже DiskBigFileBytes);
        // дерево папок считает всё независимо от порога.
        public DiskScanResult ScanDisk(string root, long minBytes, DiskProgress progress)
        {
            if (minBytes < DiskBigFileBytes) minBytes = DiskBigFileBytes;
            DiskScanResult r = new DiskScanResult();
            r.MinBytes = minBytes;
            Stopwatch sw = Stopwatch.StartNew();
            string rootPath;
            try { rootPath = Path.GetFullPath(root); } catch { rootPath = root; }
            if (rootPath.Length > 3) rootPath = rootPath.TrimEnd('\\');
            r.Root = rootPath;
            DiskDir top = new DiskDir();
            top.Name = rootPath;
            top.Depth = 0;
            r.RootDir = top;
            r.AllDirs.Add(top);

            Stack<DiskDir> stack = new Stack<DiskDir>();
            stack.Push(top);
            int sinceProgress = 0;
            long bytes = 0; int files = 0; int dirs = 0;
            while (stack.Count > 0)
            {
                if (_cancelScan) { r.Cancelled = true; break; }
                DiskDir dir = stack.Pop();
                string dirPath = dir.Path;
                Native.WIN32_FIND_DATA fd;
                string pattern = LongPath(dirPath.EndsWith("\\") ? dirPath + "*" : dirPath + "\\*");
                IntPtr h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out fd,
                                                   Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                if (h == Native.INVALID_HANDLE_VALUE)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 18 /* ERROR_NO_MORE_FILES: пустая папка */) dir.Errors++;
                    continue;
                }
                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) { dir.Skipped++; continue; }
                            if (IsDotName(name)) continue;
                            if (dir.Depth >= MaxWalkDepth) { dir.Skipped++; continue; }
                            DiskDir sub = new DiskDir();
                            sub.Name = name; sub.Parent = dir; sub.Depth = dir.Depth + 1;
                            if (dir.Children == null) dir.Children = new List<DiskDir>();
                            dir.Children.Add(sub);
                            r.AllDirs.Add(sub);
                            stack.Push(sub);
                            dirs++;
                        }
                        else
                        {
                            long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                            dir.OwnSize += size;
                            dir.OwnFiles++;
                            bytes += size; files++;
                            if (size >= minBytes)
                            {
                                DiskFile f = new DiskFile();
                                f.Name = name; f.Dir = dir; f.Size = size;
                                f.ModifiedTicks = ((long)fd.ftLastWriteTime.dwHighDateTime << 32) | (uint)fd.ftLastWriteTime.dwLowDateTime;
                                f.HiddenOrSystem = (fd.dwFileAttributes & (Native.FILE_ATTRIBUTE_HIDDEN | Native.FILE_ATTRIBUTE_SYSTEM)) != 0;
                                r.BigFiles.Add(f);
                            }
                        }
                    } while (Native.FindNextFileW(h, out fd));
                }
                finally { Native.FindClose(h); }

                if (progress != null && ++sinceProgress >= 200)
                {
                    sinceProgress = 0;
                    try { progress(bytes, files, dirs, dirPath); } catch { }
                }
            }

            // Свёртка снизу вверх: дети всегда стоят в списке позже родителя, поэтому один
            // проход с конца даёт точные суммы без рекурсии.
            for (int i = r.AllDirs.Count - 1; i >= 0; i--)
            {
                DiskDir d = r.AllDirs[i];
                d.Size += d.OwnSize;
                d.Files += d.OwnFiles;
                if (d.Parent != null)
                {
                    d.Parent.Size += d.Size;
                    d.Parent.Files += d.Files;
                    d.Parent.Dirs += d.Dirs + 1;
                    d.Parent.Errors += d.Errors;
                    d.Parent.Skipped += d.Skipped;
                }
            }
            r.TotalSize = top.Size; r.TotalFiles = top.Files; r.TotalDirs = top.Dirs;
            r.Errors = top.Errors; r.Skipped = top.Skipped;
            r.BigFiles.Sort(delegate(DiskFile a, DiskFile b) { return b.Size.CompareTo(a.Size); });
            r.ElapsedMs = sw.ElapsedMilliseconds;
            return r;
        }

        public static bool IsUnder(DiskDir d, DiskDir ancestor)
        {
            for (DiskDir p = d; p != null; p = p.Parent) if (ReferenceEquals(p, ancestor)) return true;
            return false;
        }

        // Папки, внутри которых пустые каталоги и дубликаты не предлагаются: Windows и
        // Program Files содержат пустые папки по замыслу установщиков, а одинаковые DLL
        // в разных программах — норма, удаление любой из них ломает приложение.
        private static readonly string[] _diskProtectedSegments = new string[] {
            "\\windows\\", "\\program files\\", "\\program files (x86)\\", "\\programdata\\",
            "\\$recycle.bin\\", "\\system volume information\\", "\\appdata\\local\\packages\\",
            "\\node_modules\\", "\\.git\\", "\\.svn\\", "\\.hg\\",
        };

        public static bool IsDiskProtected(string pathLower)
        {
            string p = pathLower.TrimEnd('\\') + "\\";
            foreach (string seg in _diskProtectedSegments)
                if (p.IndexOf(seg, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Только ВЕРХНИЕ пустые папки: если пуста a\b\c, показывается a — её удаление
        // уносит и вложенные. Количество вложенных пустых отдаётся отдельно.
        public static List<DiskDir> EmptyFolders(DiskScanResult r, DiskDir under, out int nested)
        {
            nested = 0;
            List<DiskDir> outList = new List<DiskDir>();
            if (r == null) return outList;
            DiskDir scope = under ?? r.RootDir;
            foreach (DiskDir d in r.AllDirs)
            {
                if (ReferenceEquals(d, r.RootDir) || !d.IsEmpty) continue;
                if (!IsUnder(d, scope)) continue;
                if (IsDiskProtected(d.Path.ToLowerInvariant())) continue;   // ни в список, ни в счётчик вложенных
                if (d.Parent != null && d.Parent.IsEmpty && !ReferenceEquals(d.Parent, r.RootDir) && IsUnder(d.Parent, scope))
                { nested++; continue; }
                outList.Add(d);
            }
            outList.Sort(delegate(DiskDir a, DiskDir b) { return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); });
            return outList;
        }

        // Дубликаты: размер -> хэш первых 64 КБ -> полный SHA-256. Читаются только файлы,
        // у которых нашёлся ровесник по размеру, и полностью — только те, что совпали по голове.
        public List<DupGroup> FindDuplicates(DiskScanResult r, DiskDir under, Action<long, long> progress)
        {
            return FindDuplicates(r, under, DiskDupMinBytes, progress);
        }

        public List<DupGroup> FindDuplicates(DiskScanResult r, DiskDir under, long minBytes, Action<long, long> progress)
        {
            if (minBytes < DiskDupMinBytes) minBytes = DiskDupMinBytes;
            List<DupGroup> result = new List<DupGroup>();
            if (r == null) return result;
            DiskDir scope = under ?? r.RootDir;
            Dictionary<long, List<DiskFile>> bySize = new Dictionary<long, List<DiskFile>>();
            foreach (DiskFile f in r.BigFiles)
            {
                if (f.Size < minBytes || f.HiddenOrSystem) continue;
                if (!IsUnder(f.Dir, scope)) continue;
                if (IsDiskProtected(f.Dir.Path.ToLowerInvariant())) continue;
                List<DiskFile> l;
                if (!bySize.TryGetValue(f.Size, out l)) { l = new List<DiskFile>(); bySize[f.Size] = l; }
                l.Add(f);
            }
            long toHash = 0, hashed = 0;
            List<List<DiskFile>> candidates = new List<List<DiskFile>>();
            foreach (KeyValuePair<long, List<DiskFile>> kv in bySize)
                if (kv.Value.Count > 1) { candidates.Add(kv.Value); toHash += kv.Key * kv.Value.Count; }
            candidates.Sort(delegate(List<DiskFile> a, List<DiskFile> b) { return (b[0].Size * b.Count).CompareTo(a[0].Size * a.Count); });

            byte[] buf = new byte[1 << 20];
            foreach (List<DiskFile> sizeGroup in candidates)
            {
                if (_cancelScan) break;
                // 1) голова
                Dictionary<string, List<DiskFile>> byHead = new Dictionary<string, List<DiskFile>>();
                foreach (DiskFile f in sizeGroup)
                {
                    string hh = HashHead(f.Path, buf);
                    if (hh == null) continue;
                    List<DiskFile> l;
                    if (!byHead.TryGetValue(hh, out l)) { l = new List<DiskFile>(); byHead[hh] = l; }
                    l.Add(f);
                }
                foreach (List<DiskFile> headGroup in byHead.Values)
                {
                    if (headGroup.Count < 2) { hashed += headGroup[0].Size; continue; }
                    // 2) полное содержимое
                    Dictionary<string, DupGroup> byFull = new Dictionary<string, DupGroup>();
                    foreach (DiskFile f in headGroup)
                    {
                        if (_cancelScan) break;
                        string fh = HashFull(f.Path, buf);
                        hashed += f.Size;
                        if (progress != null) { try { progress(hashed, toHash); } catch { } }
                        if (fh == null) continue;
                        DupGroup g;
                        if (!byFull.TryGetValue(fh, out g)) { g = new DupGroup(); g.Size = f.Size; g.Hash = fh; byFull[fh] = g; }
                        g.Files.Add(f);
                    }
                    foreach (DupGroup g in byFull.Values)
                        if (g.Files.Count > 1)
                        {
                            // старший по дате — первым: обычно это оригинал
                            g.Files.Sort(delegate(DiskFile a, DiskFile b) { return a.ModifiedTicks.CompareTo(b.ModifiedTicks); });
                            result.Add(g);
                        }
                }
            }
            result.Sort(delegate(DupGroup a, DupGroup b) { return b.Waste.CompareTo(a.Waste); });
            return result;
        }

        private static string HashHead(string path, byte[] buf)
        {
            try
            {
                using (FileStream fs = Native.OpenReadLong(path, 1 << 16))
                {
                    if (fs == null) return null;
                    int n = 0, want = 1 << 16;
                    while (n < want) { int k = fs.Read(buf, n, want - n); if (k <= 0) break; n += k; }
                    using (SHA1 sha = SHA1.Create()) return Hex(sha.ComputeHash(buf, 0, n));
                }
            }
            catch { return null; }
        }

        private static string HashFull(string path, byte[] buf)
        {
            try
            {
                using (FileStream fs = Native.OpenReadLong(path, buf.Length))
                using (SHA256 sha = SHA256.Create())
                {
                    if (fs == null) return null;
                    int k;
                    while ((k = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, k, null, 0);
                    sha.TransformFinalBlock(buf, 0, 0);
                    return Hex(sha.Hash);
                }
            }
            catch { return null; }
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        // Есть ли у тома, где лежит путь, Корзина. На сетевых дисках и съёмных носителях
        // (флешки, карты памяти) Windows Корзину не ведёт: SHFileOperation с FOF_ALLOWUNDO
        // там удаляет безвозвратно и без вопросов — пользователь должен об этом узнать ДО «Да».
        // Неопределимо (нет буквы диска, ошибка) — считаем, что Корзины нет: предупредить лишний раз безопаснее.
        public static bool RecycleBinAvailable(string path)
        {
            try
            {
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false;
                return new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        // Удаление в Корзину списком (одна операция оболочки на всё). Возвращает число
        // путей, которых после операции нет на диске; message — текст ошибки оболочки.
        // Пути длиннее MAX_PATH уходят оболочке в виде имён 8.3 (Native.ShellPath); наличие
        // проверяется через \\?\, иначе длинный путь считался бы исчезнувшим всегда.
        public static int RecycleToBin(IList<string> paths, out string message)
        {
            message = null;
            if (paths == null || paths.Count == 0) return 0;
            bool aborted = false; int tooLong = 0;
            int rc = 0;
            try { rc = Native.RecycleFiles(IntPtr.Zero, paths, out aborted, out tooLong); }
            catch (Exception ex) { message = ex.Message; }
            int gone = 0;
            foreach (string p in paths) if (!Native.PathExists(p)) gone++;
            if (rc != 0 && message == null)
                message = Tr.S("оболочка вернула код 0x" + rc.ToString("X"), "shell returned code 0x" + rc.ToString("X"));
            else if (aborted && message == null)
                message = Tr.S("операция прервана", "operation aborted");
            if (tooLong > 0)
                message = (message == null ? "" : message + "; ")
                    + Tr.S("путей длиннее 260 знаков без короткого имени 8.3: ", "paths over 260 chars without an 8.3 short name: ") + tooLong;
            return gone;
        }

        // Есть ли в папке (вложенно) хоть что-то — проверка перед удалением «пустой» папки.
        // Идёт через FindFirstFileEx и \\?\, чтобы длинные пути не давали ложного ответа.
        // Файл, точка повторного разбора, недоступный каталог — всё считается содержимым.
        public static bool DirHasContent(string path)
        {
            Stack<string> stack = new Stack<string>();
            stack.Push(path);
            int guard = 0;
            while (stack.Count > 0)
            {
                if (++guard > 100000) return true;
                string dir = stack.Pop();
                Native.WIN32_FIND_DATA fd;
                string pattern = LongPath(dir.EndsWith("\\") ? dir + "*" : dir + "\\*");
                IntPtr h = Native.FindFirstFileExW(pattern, Native.FindExInfoBasic, out fd,
                                                   Native.FindExSearchNameMatch, IntPtr.Zero, Native.FIND_FIRST_EX_LARGE_FETCH);
                if (h == Native.INVALID_HANDLE_VALUE)
                {
                    if (Marshal.GetLastWin32Error() == 18 /* ERROR_NO_MORE_FILES */) continue;
                    return true;
                }
                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;
                        if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_DIRECTORY) == 0) return true;
                        if ((fd.dwFileAttributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0) return true;
                        if (IsDotName(name)) return true;
                        stack.Push(dir.EndsWith("\\") ? dir + name : dir + "\\" + name);
                    } while (Native.FindNextFileW(h, out fd));
                }
                finally { Native.FindClose(h); }
            }
            return false;
        }

        // Вычесть удалённые файлы из карты: по цепочке родителей, без повторного обхода.
        public static void ForgetFile(DiskScanResult r, DiskFile f)
        {
            if (r == null || f == null) return;
            r.BigFiles.Remove(f);
            f.Dir.OwnSize -= f.Size; f.Dir.OwnFiles--;
            for (DiskDir d = f.Dir; d != null; d = d.Parent) { d.Size -= f.Size; d.Files--; }
            r.TotalSize = r.RootDir.Size; r.TotalFiles = r.RootDir.Files;
        }

        public static void ForgetDir(DiskScanResult r, DiskDir d)
        {
            if (r == null || d == null || d.Parent == null) return;
            int removedDirs = d.Dirs + 1;
            d.Parent.Children.Remove(d);
            if (d.Parent.Children.Count == 0) d.Parent.Children = null;
            for (DiskDir p = d.Parent; p != null; p = p.Parent) { p.Dirs -= removedDirs; p.Size -= d.Size; p.Files -= d.Files; }
            r.AllDirs.RemoveAll(delegate(DiskDir x) { return IsUnder(x, d); });
            r.BigFiles.RemoveAll(delegate(DiskFile x) { return IsUnder(x.Dir, d); });
            r.TotalDirs = r.RootDir.Dirs; r.TotalSize = r.RootDir.Size; r.TotalFiles = r.RootDir.Files;
        }

        public static List<DriveRow> Drives()
        {
            List<DriveRow> rows = new List<DriveRow>();
            try
            {
                foreach (DriveInfo di in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (di.DriveType != DriveType.Fixed || !di.IsReady) continue;
                        DriveRow d = new DriveRow();
                        d.Name = di.Name; d.Label = di.VolumeLabel;
                        d.Total = di.TotalSize; d.Free = di.TotalFreeSpace;
                        rows.Add(d);
                    }
                    catch { }
                }
            }
            catch { }
            return rows;
        }
    }
}
