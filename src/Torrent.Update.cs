// SysDeck — «Обновить раздачу»: как данные прежней версии торрента переходят в раскладку новой.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// План (BtUpdatePlan) — чистая функция двух словарей info: для каждого файла новой версии — оставить на месте, пометить
// изменённым (другая длина или другой корень v2), перенести с прежнего пути (тот же корень v2 или то же имя и длина —
// только однозначные пары) или качать заново; файлы прежней версии, которым места нет, — в Корзину. Файловые шаги
// (BtUpdateDisk) повторяемы: после падения процесса второй проход доделывает несделанное. Решает, какие куски годны,
// обычная проверка хешем новой версии; без чтения доверяются только файлы с тем же корнем v2, не менявшиеся на диске.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysDeck.Downloads
{
    internal enum BtUpdateAction { Keep, Changed, Move, New }

    internal sealed class BtUpdateEntry
    {
        public int NewIndex;
        public int OldIndex = -1;            // файл прежней версии, чьи данные переходят сюда; -1 — нет
        public BtUpdateAction Action;
        public bool Identical;               // корни v2 равны: содержимое совпадает побайтно
    }

    internal sealed class BtUpdatePlan
    {
        public string Refusal;               // null — план возможен
        public readonly List<BtUpdateEntry> Files = new List<BtUpdateEntry>();   // по хранимым файлам новой версии
        public readonly List<int> Removed = new List<int>();                    // хранимые файлы прежней версии без места
        public int NewFileCount;
        public long KeepBytes, ChangedBytes, MoveBytes, NewBytes, RemovedBytes, IdenticalBytes;

        public BtUpdateEntry EntryFor(int newIndex)
        {
            foreach (BtUpdateEntry e in Files) if (e.NewIndex == newIndex) return e;
            return null;
        }

        public int Count(BtUpdateAction a)
        {
            int n = 0;
            foreach (BtUpdateEntry e in Files) if (e.Action == a) n++;
            return n;
        }

        private static bool Stored(BtFile f) { return !f.Pad && !f.Symlink && f.Length > 0; }

        private static bool RootsEqual(BtFile a, BtFile b)
        {
            if (a.Root == null || b.Root == null || a.Root.Length != b.Root.Length) return false;
            for (int i = 0; i < a.Root.Length; i++) if (a.Root[i] != b.Root[i]) return false;
            return a.Length == b.Length;
        }

        private static bool RootsDiffer(BtFile a, BtFile b) { return a.Root != null && b.Root != null && !RootsEqual(a, b); }

        private static string FileNameOf(string rel)
        {
            int cut = rel.LastIndexOf('\\');
            return (cut < 0 ? rel : rel.Substring(cut + 1)).ToLowerInvariant();
        }

        public static BtUpdatePlan Build(BtMeta oldMeta, BtMeta newMeta)
        {
            BtUpdatePlan plan = new BtUpdatePlan();
            if (oldMeta == null || newMeta == null) { plan.Refusal = Tr.S("нет метаданных одной из версий", "the metadata of one of the versions is missing"); return plan; }
            plan.NewFileCount = newMeta.Files.Count;
            if (string.Equals(oldMeta.HexHash, newMeta.HexHash, StringComparison.OrdinalIgnoreCase))
            {
                plan.Refusal = Tr.S("это та же версия раздачи", "this is the same version of the torrent");
                return plan;
            }
            if (newMeta.IsPureV2)
            {
                plan.Refusal = Tr.S("новая версия — торрент только BitTorrent v2, такие пока не поддерживаются", "the new version is a BitTorrent v2-only torrent, not supported yet");
                return plan;
            }
            bool oldMulti = oldMeta.RootDir.Length > 0, newMulti = newMeta.RootDir.Length > 0;
            if (oldMulti != newMulti)
            {
                plan.Refusal = newMulti
                    ? Tr.S("в новой версии раздача стала папкой — добавьте её отдельно", "the new version is a folder now — add it separately")
                    : Tr.S("в новой версии раздача стала одним файлом — добавьте её отдельно", "the new version is a single file now — add it separately");
                return plan;
            }

            // 1. Тот же путь (Windows не различает регистр). У однофайловой раздачи путь один — сам корень.
            Dictionary<string, int> oldByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < oldMeta.Files.Count; i++)
                if (Stored(oldMeta.Files[i])) oldByPath[oldMulti ? oldMeta.Files[i].RelPath : ""] = i;
            bool[] oldUsed = new bool[oldMeta.Files.Count];
            List<int> pending = new List<int>();
            for (int j = 0; j < newMeta.Files.Count; j++)
            {
                BtFile nf = newMeta.Files[j];
                if (!Stored(nf)) continue;
                int i;
                if (!oldByPath.TryGetValue(newMulti ? nf.RelPath : "", out i)) { pending.Add(j); continue; }
                BtFile of = oldMeta.Files[i];
                oldUsed[i] = true;
                BtUpdateEntry e = new BtUpdateEntry();
                e.NewIndex = j;
                e.OldIndex = i;
                e.Identical = RootsEqual(of, nf);
                e.Action = e.Identical || (of.Length == nf.Length && !RootsDiffer(of, nf)) ? BtUpdateAction.Keep : BtUpdateAction.Changed;
                plan.Files.Add(e);
            }

            // 2. Переносы: сначала по корню v2, затем по имени файла и длине. Пара берётся, только если она единственная с обеих сторон.
            Dictionary<int, BtUpdateEntry> moved = new Dictionary<int, BtUpdateEntry>();
            PairUp(oldMeta, newMeta, pending, oldUsed, moved, delegate(BtFile f) { return f.Root == null ? null : Bencode.Hex(f.Root) + "|" + f.Length; });
            PairUp(oldMeta, newMeta, pending, oldUsed, moved, delegate(BtFile f) { return FileNameOf(f.RelPath) + "|" + f.Length; });

            foreach (int j in pending)
            {
                BtUpdateEntry e;
                if (!moved.TryGetValue(j, out e))
                {
                    e = new BtUpdateEntry();
                    e.NewIndex = j;
                    e.Action = BtUpdateAction.New;
                }
                plan.Files.Add(e);
            }
            plan.Files.Sort(delegate(BtUpdateEntry a, BtUpdateEntry b) { return a.NewIndex.CompareTo(b.NewIndex); });
            for (int i = 0; i < oldMeta.Files.Count; i++)
                if (Stored(oldMeta.Files[i]) && !oldUsed[i]) { plan.Removed.Add(i); plan.RemovedBytes += oldMeta.Files[i].Length; }

            foreach (BtUpdateEntry e in plan.Files)
            {
                long len = newMeta.Files[e.NewIndex].Length;
                if (e.Identical) plan.IdenticalBytes += len;
                switch (e.Action)
                {
                    case BtUpdateAction.Keep: plan.KeepBytes += len; break;
                    case BtUpdateAction.Changed: plan.ChangedBytes += len; break;
                    case BtUpdateAction.Move: plan.MoveBytes += len; break;
                    default: plan.NewBytes += len; break;
                }
            }
            return plan;
        }

        private static void PairUp(BtMeta oldMeta, BtMeta newMeta, List<int> pending, bool[] oldUsed, Dictionary<int, BtUpdateEntry> moved,
                                   Func<BtFile, string> keyOf)
        {
            Dictionary<string, List<int>> olds = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            Dictionary<string, List<int>> news = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (int i = 0; i < oldMeta.Files.Count; i++)
            {
                BtFile f = oldMeta.Files[i];
                if (!Stored(f) || oldUsed[i]) continue;
                string k = keyOf(f);
                if (k == null) continue;
                List<int> l;
                if (!olds.TryGetValue(k, out l)) olds[k] = l = new List<int>();
                l.Add(i);
            }
            foreach (int j in pending)
            {
                if (moved.ContainsKey(j)) continue;
                string k = keyOf(newMeta.Files[j]);
                if (k == null) continue;
                List<int> l;
                if (!news.TryGetValue(k, out l)) news[k] = l = new List<int>();
                l.Add(j);
            }
            foreach (KeyValuePair<string, List<int>> kv in news)
            {
                List<int> o;
                if (kv.Value.Count != 1 || !olds.TryGetValue(kv.Key, out o) || o.Count != 1) continue;
                int i = o[0], j = kv.Value[0];
                // Известно, что содержимое другое (корни v2 не равны) — переносить нечего: прежний файл уйдёт в Корзину.
                if (RootsDiffer(oldMeta.Files[i], newMeta.Files[j])) continue;
                BtUpdateEntry e = new BtUpdateEntry();
                e.NewIndex = j;
                e.OldIndex = i;
                e.Action = BtUpdateAction.Move;
                e.Identical = RootsEqual(oldMeta.Files[i], newMeta.Files[j]);
                oldUsed[i] = true;
                moved[j] = e;
            }
        }

        // Выбор файлов переходит по соответствию: снятый прежде файл остаётся снятым, новые файлы — обычные. null — все обычные.
        public int[] CarryPriorities(int[] oldPriorities)
        {
            if (oldPriorities == null) return null;
            int[] p = new int[NewFileCount];
            for (int k = 0; k < p.Length; k++) p[k] = 1;
            foreach (BtUpdateEntry e in Files)
                if (e.OldIndex >= 0 && e.OldIndex < oldPriorities.Length) p[e.NewIndex] = Math.Max(0, Math.Min(2, oldPriorities[e.OldIndex]));
            return p;
        }
    }

    // ------------------------------------------------------------------ //
    //  Шаги на диске
    // ------------------------------------------------------------------ //
    internal static class BtUpdateDisk
    {
        private static bool Stored(BtMeta m, int i) { BtFile f = m.Files[i]; return !f.Pad && !f.Symlink && f.Length > 0; }

        // Пути новой версии, занятые не файлами прежней: торрент не пишет в чужое — отказ со списком. Папки на месте файлов тоже.
        public static List<string> Collisions(BtUpdatePlan plan, BtStorage oldSt, BtStorage newSt)
        {
            HashSet<string> ours = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < oldSt.Meta.Files.Count; i++)
                if (Stored(oldSt.Meta, i)) { ours.Add(oldSt.FinalPath(i)); ours.Add(oldSt.PartPath(i)); }
            List<string> list = new List<string>();
            foreach (BtUpdateEntry e in plan.Files)
            {
                if (e.Action != BtUpdateAction.New && e.Action != BtUpdateAction.Move) continue;
                foreach (string p in new[] { newSt.FinalPath(e.NewIndex), newSt.PartPath(e.NewIndex) })
                    if (DlFiles.Exists(p) && !ours.Contains(p) && !list.Contains(p)) list.Add(p);
                string dir = ParentOf(newSt.FinalPath(e.NewIndex));
                while (dir != null && dir.Length > newSt.RootPath.Length)
                {
                    if (DlFiles.Exists(dir) && !Native.IsDirectoryPath(dir) && !ours.Contains(dir) && !list.Contains(dir)) list.Add(dir);
                    dir = ParentOf(dir);
                }
            }
            return list;
        }

        // Файлы прежней версии, которые не менялись с последнего снимка возобновления и были целиком проверены.
        public static bool[] Intact(BtStorage oldSt, BtResume oldResume)
        {
            BtMeta m = oldSt.Meta;
            bool[] ok = new bool[m.Files.Count];
            if (oldResume == null || oldResume.PieceCount != m.PieceCount) return ok;
            BtBitfield have = BtBitfield.FromBytes(oldResume.Have, m.PieceCount);
            if (have == null) return ok;
            for (int i = 0; i < m.Files.Count; i++)
            {
                if (!Stored(m, i) || i >= oldResume.FileSizes.Length || i >= oldResume.FileStamps.Length) continue;
                string final = oldSt.FinalPath(i);
                long size = BtFs.Length(final);
                if (size != m.Files[i].Length || DlFiles.Exists(oldSt.PartPath(i))) continue;
                if (size != oldResume.FileSizes[i] || BtFs.WriteTicks(final) != oldResume.FileStamps[i]) continue;
                bool all = true;
                for (int p = m.Files[i].FirstPiece; p <= m.Files[i].LastPiece && all; p++) all = have[p];
                ok[i] = all;
            }
            return ok;
        }

        // Файловые шаги плана. null — готово; иначе текст первой ошибки (сделанное остаётся сделанным, повтор доделает).
        public static string Apply(BtUpdatePlan plan, BtStorage oldSt, BtStorage newSt)
        {
            // 1. Прежние файлы без места — в Корзину (и частичные).
            foreach (int i in plan.Removed)
                foreach (string p in new[] { oldSt.FinalPath(i), oldSt.PartPath(i) })
                {
                    string why = DlFiles.Recycle(p);
                    if (why != null) return why;
                }

            foreach (BtUpdateEntry e in plan.Files)
            {
                long len = newSt.Meta.Files[e.NewIndex].Length;
                if (e.Action == BtUpdateAction.Move)
                {
                    // 2. Перенос готового файла — под настоящее имя, частичного — под частичное.
                    string fromFinal = oldSt.FinalPath(e.OldIndex), fromPart = oldSt.PartPath(e.OldIndex);
                    bool final = DlFiles.Exists(fromFinal);
                    string from = final ? fromFinal : DlFiles.Exists(fromPart) ? fromPart : null;
                    if (from == null) continue;   // прежних данных нет (файл не выбирали или перенос уже сделан)
                    string to = final ? newSt.FinalPath(e.NewIndex) : newSt.PartPath(e.NewIndex);
                    if (DlFiles.Exists(to)) return Tr.S("место файла новой версии занято: ", "the new version's file place is taken: ") + to;
                    string why = Move(from, to);
                    if (why != null) return why;
                }
                else if (e.Action == BtUpdateAction.Changed)
                {
                    // 3. Изменённый файл больше не готов: под частичное имя и нужной длины — годные куски найдёт проверка.
                    string final = newSt.FinalPath(e.NewIndex), part = newSt.PartPath(e.NewIndex);
                    if (DlFiles.Exists(final))
                    {
                        if (DlFiles.Exists(part)) return Tr.S("рядом с файлом лежит его частичная копия: ", "a partial copy lies beside the file: ") + part;
                        string why = Move(final, part);
                        if (why != null) return why;
                    }
                    if (DlFiles.Exists(part) && BtFs.Length(part) != len)
                    {
                        string why = SetLength(part, len);
                        if (why != null) return why;
                    }
                }
            }

            // 4. Опустевшие папки прежней раскладки, которых нет в новой, — в Корзину (пустые, без ссылок).
            if (oldSt.IsMultiFile)
            {
                HashSet<string> keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int j = 0; j < newSt.Meta.Files.Count; j++)
                {
                    if (!Stored(newSt.Meta, j)) continue;
                    for (string d = ParentOf(newSt.FinalPath(j)); d != null && d.Length > newSt.RootPath.Length; d = ParentOf(d)) keep.Add(d);
                }
                List<string> dirs = new List<string>();
                for (int i = 0; i < oldSt.Meta.Files.Count; i++)
                {
                    if (!Stored(oldSt.Meta, i)) continue;
                    for (string d = ParentOf(oldSt.FinalPath(i)); d != null && d.Length > oldSt.RootPath.Length; d = ParentOf(d))
                        if (!keep.Contains(d) && !dirs.Contains(d)) dirs.Add(d);
                }
                dirs.Sort(delegate(string a, string b) { int c = b.Length.CompareTo(a.Length); return c != 0 ? c : string.CompareOrdinal(a, b); });
                foreach (string d in dirs)
                {
                    if (!Native.IsDirectoryPath(d) || DlFiles.IsReparse(d)) continue;
                    bool empty;
                    try { empty = Directory.GetFileSystemEntries(d).Length == 0; } catch { empty = false; }
                    if (!empty) continue;
                    string why = DlFiles.Recycle(d);
                    if (why != null) return why;
                }
            }
            return null;
        }

        // Снимок, которому новая версия поверит без чтения: помечены все куски, но размер «-2» у файлов без доказанного
        // совпадения делает их куски подозрительными — проверка хешем читает только их (и куски на стыке с ними).
        // null — доверять нечему, проверяется всё.
        public static BtResume TrustIdentical(BtUpdatePlan plan, BtStorage newSt, bool[] oldIntact, int[] priorities)
        {
            BtMeta m = newSt.Meta;
            bool any = false;
            long[] sizes = new long[m.Files.Count], stamps = new long[m.Files.Count];
            for (int j = 0; j < m.Files.Count; j++) sizes[j] = Stored(m, j) ? -2 : -1;
            foreach (BtUpdateEntry e in plan.Files)
            {
                if (!e.Identical || e.OldIndex < 0 || oldIntact == null || e.OldIndex >= oldIntact.Length || !oldIntact[e.OldIndex]) continue;
                if (e.Action != BtUpdateAction.Keep && e.Action != BtUpdateAction.Move) continue;
                string final = newSt.FinalPath(e.NewIndex);
                long size = BtFs.Length(final);
                if (size != m.Files[e.NewIndex].Length || DlFiles.Exists(newSt.PartPath(e.NewIndex))) continue;
                sizes[e.NewIndex] = size;
                stamps[e.NewIndex] = BtFs.WriteTicks(final);
                any = true;
            }
            if (!any) return null;
            BtBitfield have = new BtBitfield(m.PieceCount);
            for (int p = 0; p < m.PieceCount; p++) have[p] = true;
            BtResume r = new BtResume();
            r.Hash = m.HexHash;
            r.RootName = newSt.RootName;
            r.PieceCount = m.PieceCount;
            r.Have = have.ToBytes();
            r.FileSizes = sizes;
            r.FileStamps = stamps;
            r.Priorities = priorities == null ? new int[0] : (int[])priorities.Clone();
            return r;
        }

        private static string ParentOf(string path)
        {
            int cut = path.LastIndexOf('\\');
            return cut <= 2 ? null : path.Substring(0, cut);
        }

        private static string Move(string from, string to)
        {
            try
            {
                string dir = ParentOf(to);
                if (dir != null) CreateDirectories(dir);
            }
            catch (IOException ex) { return ex.Message; }
            if (!MoveFileExW(Native.LongPathOf(from), Native.LongPathOf(to), MoveFileWriteThrough))
                return Tr.S("не удалось перенести ", "could not move ") + from + " (" + Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture) + ")";
            return null;
        }

        private static void CreateDirectories(string dir)
        {
            if (string.IsNullOrEmpty(dir) || DlFiles.Exists(dir)) return;
            CreateDirectories(ParentOf(dir));
            if (!CreateDirectoryW(Native.LongPathOf(dir), IntPtr.Zero) && Marshal.GetLastWin32Error() != 183 /* ERROR_ALREADY_EXISTS */)
                throw new IOException(Tr.S("не удалось создать папку: ", "could not create the folder: ") + dir);
        }

        private static string SetLength(string path, long length)
        {
            using (SafeFileHandle h = CreateFileW(Native.LongPathOf(path), GenericWrite, ShareRead, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero))
            {
                if (h.IsInvalid) return Tr.S("не удалось открыть файл: ", "could not open the file: ") + path;
                try
                {
                    using (FileStream fs = new FileStream(h, FileAccess.Write, 1)) fs.SetLength(length);
                }
                catch (IOException ex) { return ex.Message; }
            }
            return null;
        }

        private const uint GenericWrite = 0x40000000, ShareRead = 1, OpenExisting = 3, FileAttributeNormal = 0x80, MoveFileWriteThrough = 8;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateDirectoryW(string path, IntPtr sa);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileExW(string from, string to, uint flags);
    }
}
