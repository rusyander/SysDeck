// Windows Process Cleaner — «Загрузки», торренты: куски ↔ файлы на диске, битовое поле, быстрое возобновление, очередь
// дисковых операций.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Недокачанный файл лежит как «имя.wpcpart» и получает настоящее имя, когда все его куски проверены. Существующий файл
// не перезаписывается никогда: имя корня выбирает движок при добавлении (DlFiles.UniqueName), здесь при переименовании
// занятое имя — ошибка, а не замена. Пути: корень + относительный путь из BtMeta, каждый элемент которого уже очищен;
// хранилище ещё раз сверяет форму пути и отказывается писать через junction или символическую ссылку.
// Открытие через CreateFileW с \\?\: exe собран без целевой платформы 4.6.2, у FileStream старая обработка путей и
// предел 260 символов, а в раздачах игр пути глубже.
// Смещения в битовом поле на диске никогда не опережают сброшенные данные: состояние пишется после FlushFileBuffers.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WindowsProcessCleaner.Downloads
{
    // ------------------------------------------------------------------ //
    //  Битовое поле кусков (BEP 3: старший бит первого байта — кусок 0)
    // ------------------------------------------------------------------ //
    internal sealed class BtBitfield
    {
        private readonly byte[] _bits;
        private int _set;
        public readonly int Count;

        public BtBitfield(int count)
        {
            Count = Math.Max(0, count);
            _bits = new byte[(Count + 7) / 8];
        }

        public bool this[int i]
        {
            get
            {
                if (i < 0 || i >= Count) return false;
                lock (_bits) return (_bits[i >> 3] & (0x80 >> (i & 7))) != 0;
            }
            set
            {
                if (i < 0 || i >= Count) return;
                lock (_bits)
                {
                    bool was = (_bits[i >> 3] & (0x80 >> (i & 7))) != 0;
                    if (was == value) return;
                    if (value) { _bits[i >> 3] |= (byte)(0x80 >> (i & 7)); _set++; }
                    else { _bits[i >> 3] &= (byte)~(0x80 >> (i & 7)); _set--; }
                }
            }
        }

        public int SetCount { get { lock (_bits) return _set; } }
        public bool All { get { return SetCount == Count; } }

        public byte[] ToBytes()
        {
            lock (_bits) return (byte[])_bits.Clone();
        }

        // Длина не та или выставлены лишние биты за последним куском — null (BEP 3 требует рвать соединение).
        public static BtBitfield FromBytes(byte[] b, int count)
        {
            if (b == null || count < 0 || b.Length != (count + 7) / 8) return null;
            if (count % 8 != 0 && b.Length > 0 && (b[b.Length - 1] & (0xFF >> (count % 8))) != 0) return null;
            BtBitfield f = new BtBitfield(count);
            Buffer.BlockCopy(b, 0, f._bits, 0, b.Length);
            int set = 0;
            for (int i = 0; i < count; i++) if ((b[i >> 3] & (0x80 >> (i & 7))) != 0) set++;
            f._set = set;
            return f;
        }

        public BtBitfield Clone() { return FromBytes(ToBytes(), Count); }
    }

    // ------------------------------------------------------------------ //
    //  Файлы торрента на диске
    // ------------------------------------------------------------------ //
    internal sealed class BtStorage : IDisposable
    {
        public const int MaxOpenFiles = 32;

        private readonly BtMeta _meta;
        private readonly string _folder;
        private readonly string _rootName;
        private readonly object _gate = new object();
        private readonly Dictionary<int, FileStream> _open = new Dictionary<int, FileStream>();
        private readonly LinkedList<int> _lru = new LinkedList<int>();
        private readonly bool[] _done;
        private bool _disposed;

        // folder — проверенная папка загрузки; rootName — имя папки торрента (многофайловый) или файла (однофайловый),
        // уже свободное на диске.
        public BtStorage(BtMeta meta, string folder, string rootName)
        {
            _meta = meta;
            _folder = (folder ?? "").TrimEnd('\\');
            _rootName = rootName ?? "";
            _done = new bool[meta.Files.Count];
        }

        public BtMeta Meta { get { return _meta; } }
        public string Folder { get { return _folder; } }
        public string RootName { get { return _rootName; } }
        public bool IsMultiFile { get { return _meta.RootDir.Length > 0; } }

        // Папка торрента (многофайловый) или сам файл (однофайловый).
        public string RootPath { get { return _folder + "\\" + _rootName; } }

        public string FinalPath(int file)
        {
            return IsMultiFile ? RootPath + "\\" + _meta.Files[file].RelPath : RootPath;
        }

        public string PartPath(int file) { return FinalPath(file) + DlPaths.PartSuffix; }

        public bool IsFileDone(int file) { lock (_gate) return _done[file]; }

        // Файл, который вообще пишется на диск: не заполнитель, не ссылка, не пустой.
        public bool IsStored(int file)
        {
            BtFile f = _meta.Files[file];
            return !f.Pad && !f.Symlink && f.Length > 0;
        }

        // ---------- проверка путей ----------
        // null — всё в порядке. Каждый элемент уже очищен, «..» и двоеточий нет по построению; здесь — сверка формы и отказ
        // от точек повторной обработки на пути от папки загрузки до файла.
        public string Validate()
        {
            if (_folder.Length < 3 || DlFiles.SanitizeName(_rootName) != _rootName) return Tr.S("недопустимое имя торрента", "invalid torrent name");
            if (DlFiles.IsReparse(_folder)) return Tr.S("папка загрузки — ссылка на другое место", "the download folder is a link to another place");
            HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _meta.Files.Count; i++)
            {
                if (!IsStored(i)) continue;
                if (IsMultiFile)
                {
                    string[] parts = _meta.Files[i].RelPath.Split('\\');
                    string dir = RootPath;
                    for (int k = 0; k < parts.Length; k++)
                    {
                        if (parts[k].Length == 0 || BtMeta.CleanElement(parts[k]) != parts[k]) return Tr.S("недопустимый путь в торренте", "invalid path in the torrent");
                        if (k < parts.Length - 1) { dir += "\\" + parts[k]; dirs.Add(dir); }
                    }
                    dirs.Add(RootPath);
                }
                string final = FinalPath(i);
                if (DlFiles.IsReparse(final) || DlFiles.IsReparse(final + DlPaths.PartSuffix))
                    return Tr.S("файл торрента — ссылка на другое место: ", "a torrent file is a link to another place: ") + final;
            }
            foreach (string d in dirs)
                if (DlFiles.IsReparse(d)) return Tr.S("папка внутри торрента — ссылка на другое место: ", "a folder inside the torrent is a link to another place: ") + d;
            return null;
        }

        // Какие файлы уже готовы (есть под настоящим именем, частичного нет). Вызывается при открытии.
        public void ProbeDone()
        {
            lock (_gate)
                for (int i = 0; i < _done.Length; i++)
                    _done[i] = !IsStored(i) || (DlFiles.Exists(FinalPath(i)) && !DlFiles.Exists(PartPath(i)));
        }

        public long FileSizeOnDisk(int file)
        {
            string p = IsFileDone(file) ? FinalPath(file) : PartPath(file);
            return BtFs.Length(p);
        }

        // ---------- ввод-вывод ----------
        // Пишет блок куска. Байты заполнителей и ссылок пропускаются. null — удачно, иначе текст ошибки.
        public string Write(int piece, int begin, byte[] data, int offset, int count)
        {
            return Io(piece, begin, data, offset, count, true);
        }

        // Читает блок; байты заполнителей — нули. null — удачно.
        public string Read(int piece, int begin, byte[] data, int offset, int count)
        {
            return Io(piece, begin, data, offset, count, false);
        }

        private string Io(int piece, int begin, byte[] data, int offset, int count, bool write)
        {
            if (piece < 0 || piece >= _meta.PieceCount || begin < 0 || count < 0 || begin + count > _meta.PieceSize(piece)) return "bad block range";
            long pos = _meta.PieceStart(piece) + begin;
            long end = pos + count;
            List<int> files = _meta.FilesOfPiece(piece);
            lock (_gate)
            {
                if (_disposed) return "storage closed";
                long covered = pos;
                foreach (int fi in files)
                {
                    BtFile f = _meta.Files[fi];
                    long from = Math.Max(pos, f.Offset), to = Math.Min(end, f.End);
                    if (from >= to) continue;
                    int bufAt = offset + (int)(from - pos);
                    int len = (int)(to - from);
                    if (!write && from > covered) Array.Clear(data, offset + (int)(covered - pos), (int)(from - covered));
                    covered = to;
                    if (!IsStored(fi))
                    {
                        if (!write) Array.Clear(data, bufAt, len);
                        continue;
                    }
                    try
                    {
                        FileStream fs = Handle(fi, write);
                        if (fs == null)
                        {
                            if (write) return Tr.S("не удалось открыть файл: ", "could not open the file: ") + CurrentPath(fi);
                            return Tr.S("файла нет: ", "the file is missing: ") + CurrentPath(fi);
                        }
                        fs.Position = from - f.Offset;
                        if (write) fs.Write(data, bufAt, len);
                        else
                        {
                            int got = 0;
                            while (got < len)
                            {
                                int n = fs.Read(data, bufAt + got, len - got);
                                if (n <= 0) break;
                                got += n;
                            }
                            if (got < len) return Tr.S("файл короче ожидаемого: ", "the file is shorter than expected: ") + CurrentPath(fi);
                        }
                    }
                    catch (IOException ex) { return ex.Message; }
                    catch (UnauthorizedAccessException ex) { return ex.Message; }
                }
                if (!write && covered < end) Array.Clear(data, offset + (int)(covered - pos), (int)(end - covered));
            }
            return null;
        }

        private string CurrentPath(int file) { return _done[file] ? FinalPath(file) : PartPath(file); }

        private FileStream Handle(int file, bool create)
        {
            FileStream fs;
            if (_open.TryGetValue(file, out fs))
            {
                _lru.Remove(file);
                _lru.AddFirst(file);
                return fs;
            }
            string path = CurrentPath(file);
            bool exists = DlFiles.Exists(path);
            if (!exists && !create) return null;
            if (!exists) CreateDirectories(ParentOf(path));
            SafeFileHandle h = CreateFileW(Native.LongPathOf(path), GenericRead | GenericWrite, ShareRead | ShareWrite | ShareDelete, IntPtr.Zero,
                                           exists ? OpenExisting : CreateNew, FileAttributeNormal, IntPtr.Zero);
            if (h.IsInvalid)
            {
                h.Dispose();
                return null;
            }
            fs = new FileStream(h, FileAccess.ReadWrite, 1);
            if (!exists) DlFiles.SetSparse(fs, true);
            while (_open.Count >= MaxOpenFiles && _lru.Last != null)
            {
                int old = _lru.Last.Value;
                _lru.RemoveLast();
                CloseLocked(old, false);
            }
            _open[file] = fs;
            _lru.AddFirst(file);
            return fs;
        }

        private void CloseLocked(int file, bool clearSparse)
        {
            FileStream fs;
            if (!_open.TryGetValue(file, out fs)) return;
            _open.Remove(file);
            _lru.Remove(file);
            try
            {
                fs.Flush(true);
                if (clearSparse) DlFiles.SetSparse(fs, false);
            }
            catch (Exception ex) { DlLog.Report(ex); }
            try { fs.Dispose(); } catch { }
        }

        // Path.GetDirectoryName при старой обработке путей бросает исключение на пути длиннее 260 символов.
        private static string ParentOf(string path)
        {
            int cut = path.LastIndexOf('\\');
            return cut <= 2 ? null : path.Substring(0, cut);
        }

        private static void CreateDirectories(string dir)
        {
            if (string.IsNullOrEmpty(dir) || DlFiles.Exists(dir)) return;
            CreateDirectories(ParentOf(dir));
            if (!CreateDirectoryW(Native.LongPathOf(dir), IntPtr.Zero) && Marshal.GetLastWin32Error() != 183 /* ERROR_ALREADY_EXISTS */)
                throw new IOException(Tr.S("не удалось создать папку: ", "could not create the folder: ") + dir);
        }

        // Сброс всех открытых файлов на диск — перед записью состояния возобновления.
        public void FlushAll()
        {
            lock (_gate)
                foreach (FileStream fs in _open.Values)
                    try { fs.Flush(true); } catch (Exception ex) { DlLog.Report(ex); }
        }

        // Проверка куска по хешу. buffer — не меньше длины куска (переиспользуется вызывающим).
        public bool CheckPiece(int piece, byte[] buffer)
        {
            int size = _meta.PieceSize(piece);
            if (size <= 0 || buffer == null || buffer.Length < size) return false;
            if (Read(piece, 0, buffer, 0, size) != null) return false;
            if (_meta.IsPureV2) return _meta.CheckPieceV2(piece, buffer, size);
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(buffer, 0, size);
                for (int i = 0; i < 20; i++) if (h[i] != _meta.PieceHashes[piece * 20 + i]) return false;
                return true;
            }
        }

        // Кусок проверен: файлы, у которых теперь есть все куски, получают настоящее имя. Возвращает ошибки (пусто — нет).
        public List<string> PieceVerified(int piece, BtBitfield have)
        {
            List<string> errors = new List<string>();
            foreach (int fi in _meta.FilesOfPiece(piece))
            {
                if (!IsStored(fi) || IsFileDone(fi)) continue;
                BtFile f = _meta.Files[fi];
                bool all = true;
                for (int p = f.FirstPiece; p <= f.LastPiece && all; p++) all = have[p];
                if (!all) continue;
                string why = CompleteFile(fi);
                if (why != null) errors.Add(why);
            }
            return errors;
        }

        private string CompleteFile(int file)
        {
            lock (_gate)
            {
                if (_done[file]) return null;
                string part = PartPath(file), final = FinalPath(file);
                if (!_open.ContainsKey(file))
                {
                    // Файл мог быть не открыт в этом запуске (весь скачан до перезапуска) — открыть, чтобы снять разреженность.
                    Handle(file, false);
                }
                CloseLocked(file, true);
                if (DlFiles.Exists(final))
                    return Tr.S("имя уже занято другим файлом, данные остались в ", "the name is taken by another file, the data stays in ") + part;
                if (!MoveFileExW(Native.LongPathOf(part), Native.LongPathOf(final), MoveFileWriteThrough))
                    return Tr.S("не удалось переименовать ", "could not rename ") + part + " (" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")";
                _done[file] = true;
                return null;
            }
        }

        // Полная проверка данных на диске. Куски, чьих файлов нет, пропускаются без чтения. cancel — прервать (have частичный).
        // progress(checkedPieces) — из этого же потока.
        public BtBitfield Recheck(Func<bool> cancel, Action<int> progress)
        {
            ProbeDone();
            BtBitfield have = new BtBitfield(_meta.PieceCount);
            byte[] buffer = new byte[_meta.PieceLength];
            for (int p = 0; p < _meta.PieceCount; p++)
            {
                if (cancel != null && cancel()) break;
                bool present = true;
                foreach (int fi in _meta.FilesOfPiece(p))
                    if (IsStored(fi) && BtFs.Length(CurrentPath(fi)) < 0) { present = false; break; }
                if (present && CheckPiece(p, buffer)) have[p] = true;
                if (progress != null && (p % 16 == 15 || p == _meta.PieceCount - 1)) progress(p + 1);
            }
            return have;
        }

        // Сколько байт займут выбранные файлы (priorities[i] == 0 — файл не нужен).
        public long WantedBytes(int[] priorities)
        {
            long sum = 0;
            for (int i = 0; i < _meta.Files.Count; i++)
                if (IsStored(i) && (priorities == null || i >= priorities.Length || priorities[i] > 0)) sum += _meta.Files[i].Length;
            return sum;
        }

        // Удаление данных — только в Корзину. Папка торрента уходит целиком, если в ней нет чужих файлов; иначе — по файлам.
        public List<string> RecycleData()
        {
            List<string> errors = new List<string>();
            lock (_gate)
            {
                foreach (int fi in new List<int>(_open.Keys)) CloseLocked(fi, false);
                _disposed = true;
            }
            List<string> ours = new List<string>();
            for (int i = 0; i < _meta.Files.Count; i++)
            {
                if (!IsStored(i)) continue;
                foreach (string p in new[] { FinalPath(i), PartPath(i) }) if (DlFiles.Exists(p)) ours.Add(p);
            }
            if (IsMultiFile && Directory.Exists(RootPath) && !DlFiles.IsReparse(RootPath) && OnlyOurs(RootPath, ours))
            {
                string why = DlFiles.Recycle(RootPath);
                if (why != null) errors.Add(why);
                return errors;
            }
            foreach (string p in ours)
            {
                string why = DlFiles.Recycle(p);
                if (why != null) errors.Add(why);
            }
            return errors;
        }

        private static bool OnlyOurs(string root, List<string> ours)
        {
            HashSet<string> set = new HashSet<string>(ours, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string f in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    if (!set.Contains(f)) return false;
                return true;
            }
            catch { return false; }
        }

        public void Close()
        {
            lock (_gate)
            {
                foreach (int fi in new List<int>(_open.Keys)) CloseLocked(fi, false);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (int fi in new List<int>(_open.Keys)) CloseLocked(fi, false);
                _disposed = true;
            }
        }

        private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
        private const uint ShareRead = 1, ShareWrite = 2, ShareDelete = 4;
        private const uint CreateNew = 1, OpenExisting = 3, FileAttributeNormal = 0x80;
        private const uint MoveFileWriteThrough = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr sa);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string from, string to, uint flags);
    }

    // Размер и время записи по пути любой длины (\?\); -1 — файла нет.
    internal static class BtFs
    {
        // FILETIME — два DWORD: без Pack = 4 поле long выровнялось бы на 8 и все смещения съехали бы.
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct AttributeData
        {
            public uint Attributes;
            public long Created, Accessed, Written;
            public uint SizeHigh, SizeLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetFileAttributesExW(string path, int level, out AttributeData data);

        public static long Length(string path)
        {
            AttributeData d;
            if (string.IsNullOrEmpty(path) || !GetFileAttributesExW(Native.LongPathOf(path), 0, out d) || (d.Attributes & 0x10) != 0) return -1;
            return ((long)d.SizeHigh << 32) | d.SizeLow;
        }

        public static long WriteTicks(string path)
        {
            AttributeData d;
            if (string.IsNullOrEmpty(path) || !GetFileAttributesExW(Native.LongPathOf(path), 0, out d)) return 0;
            return DateTime.FromFileTimeUtc(d.Written).Ticks;
        }
    }

    // ------------------------------------------------------------------ //
    //  Быстрое возобновление: torrents\<hash>.resume.json
    // ------------------------------------------------------------------ //
    internal sealed class BtResume
    {
        public string Hash = "";
        public string RootName = "";
        public int PieceCount;
        public byte[] Have = new byte[0];
        public long[] FileSizes = new long[0];      // размер файла на диске в момент записи; -1 — файла не было
        public long[] FileStamps = new long[0];     // LastWriteTimeUtc.Ticks в момент записи
        public int[] Priorities = new int[0];       // 0 — не качать, 1 — обычный, 2 — высокий
        public long Uploaded, Downloaded, Wasted;
        public long SeedSeconds, ActiveSeconds;

        public static string FileFor(string torrentsDir, string hash) { return Path.Combine(torrentsDir, hash + ".resume.json"); }

        // Снимок после FlushAll: размеры и время записи — то, с чем сверится следующий запуск.
        public static BtResume Capture(BtStorage st, BtBitfield have, int[] priorities)
        {
            BtResume r = new BtResume();
            r.Hash = st.Meta.HexHash;
            r.RootName = st.RootName;
            r.PieceCount = st.Meta.PieceCount;
            r.Have = have.ToBytes();
            int n = st.Meta.Files.Count;
            r.FileSizes = new long[n];
            r.FileStamps = new long[n];
            for (int i = 0; i < n; i++)
            {
                if (!st.IsStored(i)) { r.FileSizes[i] = -1; continue; }
                string p = st.IsFileDone(i) ? st.FinalPath(i) : st.PartPath(i);
                r.FileSizes[i] = BtFs.Length(p);
                r.FileStamps[i] = r.FileSizes[i] < 0 ? 0 : BtFs.WriteTicks(p);
            }
            r.Priorities = priorities == null ? new int[0] : (int[])priorities.Clone();
            return r;
        }

        // Битовое поле для продолжения. Куски файлов, изменившихся на диске после записи, не доверяются: попадают в recheck
        // (их надо проверить хешем, прежде чем раздавать или докачивать).
        public BtBitfield Restore(BtStorage st, List<int> recheck)
        {
            BtMeta meta = st.Meta;
            BtBitfield have = PieceCount == meta.PieceCount ? BtBitfield.FromBytes(Have, meta.PieceCount) : null;
            if (have == null) return new BtBitfield(meta.PieceCount);
            st.ProbeDone();
            HashSet<int> suspect = new HashSet<int>();
            for (int i = 0; i < meta.Files.Count; i++)
            {
                if (!st.IsStored(i)) continue;
                BtFile f = meta.Files[i];
                string p = st.IsFileDone(i) ? st.FinalPath(i) : st.PartPath(i);
                long size = BtFs.Length(p);
                bool same = i < FileSizes.Length && size == FileSizes[i] && size >= 0 && BtFs.WriteTicks(p) == FileStamps[i];
                if (same) continue;
                for (int piece = f.FirstPiece; piece <= f.LastPiece; piece++)
                    if (have[piece]) { have[piece] = false; suspect.Add(piece); }
            }
            if (recheck != null) { recheck.AddRange(suspect); recheck.Sort(); }
            return have;
        }

        public JVal ToJson()
        {
            JVal o = JVal.NewObj();
            o.Set("Hash", DlJson.S(Hash));
            o.Set("RootName", DlJson.S(RootName));
            o.Set("PieceCount", DlJson.N(PieceCount));
            o.Set("Have", DlJson.S(Convert.ToBase64String(Have)));
            o.Set("FileSizes", Longs(FileSizes));
            o.Set("FileStamps", Longs(FileStamps));
            JVal pr = JVal.NewArr();
            foreach (int p in Priorities) pr.V.Add(DlJson.N(p));
            o.Set("Priorities", pr);
            o.Set("Uploaded", DlJson.N(Uploaded));
            o.Set("Downloaded", DlJson.N(Downloaded));
            o.Set("Wasted", DlJson.N(Wasted));
            o.Set("SeedSeconds", DlJson.N(SeedSeconds));
            o.Set("ActiveSeconds", DlJson.N(ActiveSeconds));
            return o;
        }

        public static BtResume FromJson(JVal o)
        {
            if (o == null || o.Kind != JKind.Obj) return null;
            BtResume r = new BtResume();
            r.Hash = DlJson.Str(o, "Hash", "");
            r.RootName = DlJson.Str(o, "RootName", "");
            r.PieceCount = DlJson.Int(o, "PieceCount", 0);
            try { r.Have = Convert.FromBase64String(DlJson.Str(o, "Have", "")); } catch { r.Have = new byte[0]; }
            r.FileSizes = ReadLongs(o, "FileSizes");
            r.FileStamps = ReadLongs(o, "FileStamps");
            long[] pr = ReadLongs(o, "Priorities");
            r.Priorities = new int[pr.Length];
            for (int i = 0; i < pr.Length; i++) r.Priorities[i] = (int)Math.Max(0, Math.Min(2, pr[i]));
            r.Uploaded = Math.Max(0, DlJson.Long(o, "Uploaded", 0));
            r.Downloaded = Math.Max(0, DlJson.Long(o, "Downloaded", 0));
            r.Wasted = Math.Max(0, DlJson.Long(o, "Wasted", 0));
            r.SeedSeconds = Math.Max(0, DlJson.Long(o, "SeedSeconds", 0));
            r.ActiveSeconds = Math.Max(0, DlJson.Long(o, "ActiveSeconds", 0));
            return r;
        }

        public void Save(string file) { DlPaths.WriteAtomic(file, Jsn.Write(ToJson())); }

        public static BtResume Load(string file) { return FromJson(DlPaths.ReadJson(file)); }

        private static JVal Longs(long[] values)
        {
            JVal a = JVal.NewArr();
            foreach (long v in values) a.V.Add(DlJson.N(v));
            return a;
        }

        private static long[] ReadLongs(JVal o, string name)
        {
            JVal v = o.Get(name);
            if (v == null || v.Kind != JKind.Arr) return new long[0];
            List<long> list = new List<long>();
            foreach (JVal e in v.V)
            {
                long n;
                if (e.Kind == JKind.Num && long.TryParse(e.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) list.Add(n);
            }
            return list.ToArray();
        }
    }

    // ------------------------------------------------------------------ //
    //  Очередь дисковых операций: один поток на сессию, строгий порядок (запись блока раньше проверки его куска).
    //  Обратные вызовы выполняются в потоке диска — вызывающий переносит результат в свой поток сам.
    // ------------------------------------------------------------------ //
    internal sealed class BtDisk : IDisposable
    {
        private readonly Queue<Action> _jobs = new Queue<Action>();
        private readonly object _gate = new object();
        private readonly Thread _thread;
        private long _queuedBytes;
        private bool _stopping;

        public BtDisk(string name)
        {
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = name ?? "wpc-bt-disk";
            _thread.Start();
        }

        // Байт в очереди на запись: при большом хвосте сеть перестаёт просить блоки (давление назад).
        public long QueuedBytes { get { return Interlocked.Read(ref _queuedBytes); } }

        public int Pending { get { lock (_gate) return _jobs.Count; } }

        public void Write(BtStorage st, int piece, int begin, byte[] data, int offset, int count, Action<string> done)
        {
            Interlocked.Add(ref _queuedBytes, count);
            Enqueue(delegate
            {
                string why;
                try { why = st.Write(piece, begin, data, offset, count); }
                finally { Interlocked.Add(ref _queuedBytes, -count); }
                if (done != null) done(why);
            });
        }

        public void Read(BtStorage st, int piece, int begin, int count, Action<byte[], string> done)
        {
            Enqueue(delegate
            {
                byte[] buf = new byte[count];
                string why = st.Read(piece, begin, buf, 0, count);
                done(why == null ? buf : null, why);
            });
        }

        // Проверка куска после записи всех его блоков. ok = true — хеш сошёлся; renameErrors — ошибки переименования файлов.
        public void Check(BtStorage st, int piece, BtBitfield have, Action<bool, List<string>> done)
        {
            Enqueue(delegate
            {
                bool ok = st.CheckPiece(piece, new byte[st.Meta.PieceSize(piece)]);
                List<string> errors = null;
                if (ok)
                {
                    have[piece] = true;
                    errors = st.PieceVerified(piece, have);
                }
                done(ok, errors);
            });
        }

        public void Run(Action job) { Enqueue(job); }

        private void Enqueue(Action job)
        {
            lock (_gate)
            {
                if (_stopping) return;
                _jobs.Enqueue(job);
                Monitor.Pulse(_gate);
            }
        }

        private void Loop()
        {
            while (true)
            {
                Action job;
                lock (_gate)
                {
                    while (_jobs.Count == 0 && !_stopping) Monitor.Wait(_gate);
                    if (_jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }
                try { job(); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        // Остаток очереди выполняется до конца (записи не теряются), затем поток выходит.
        public void Dispose()
        {
            lock (_gate)
            {
                _stopping = true;
                Monitor.PulseAll(_gate);
            }
            if (Thread.CurrentThread != _thread) _thread.Join(30000);
        }
    }
}
