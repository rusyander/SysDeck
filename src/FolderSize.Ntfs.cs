// Windows Process Cleaner — «Размеры папок»: быстрый режим, размеры всех папок тома из таблицы $MFT.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WindowsProcessCleaner.FolderSize
{
    internal struct DirTotals
    {
        public long Bytes, Files, Directories;

        public DirTotals(long bytes, long files, long directories)
        {
            Bytes = bytes; Files = files; Directories = directories;
        }
    }

    // ------------------------------------------------------------------ //
    //  Размеры папок одного тома NTFS, выведенные из его Master File Table.
    //  Зачем: обход диска спрашивает файловую систему о каждой папке по очереди. MFT — одна плоская таблица
    //  всех файлов тома; последовательное чтение отвечает «сколько весит каждая папка» за один проход,
    //  секунды вместо минут, и точно, а не выборочно.
    //  Жёсткая ссылка считается в каждой папке, где у файла есть имя, — как у обхода и в свойствах Проводника:
    //  иначе папка проекта pnpm весила бы с правами на сотни мегабайт меньше, чем без них. Точки соединения
    //  и символьные ссылки не вносят ничего — данных у них нет.
    // ------------------------------------------------------------------ //
    internal sealed class NtfsVolumeIndex
    {
        private const uint RootRecord = 5;

        private readonly Dictionary<uint, DirNode> _dirs;

        public char Drive { get; private set; }
        public DateTime BuiltAtUtc { get; private set; }
        public long RecordsScanned { get; private set; }

        internal NtfsVolumeIndex(char drive, Dictionary<uint, DirNode> dirs, long recordsScanned)
        {
            Drive = drive;
            _dirs = dirs;
            RecordsScanned = recordsScanned;
            BuiltAtUtc = DateTime.UtcNow;
        }

        internal sealed class DirNode
        {
            public uint Parent;
            public string Name = "";
            public List<uint> ChildDirs;
            public long DirectBytes;
            public long DirectFiles;
            public long TotalBytes;
            public long TotalFiles;
            public long TotalDirs;
            public Dictionary<string, uint> Lookup;
        }

        public bool TryResolveDirectory(string absolutePath, out uint dirIndex)
        {
            dirIndex = RootRecord;
            try
            {
                string full = Path.GetFullPath(absolutePath);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root)) return false;
                if (char.ToUpperInvariant(root[0]) != char.ToUpperInvariant(Drive)) return false;
                string rest = full.Substring(root.Length).Trim('\\');
                if (rest.Length == 0) return _dirs.ContainsKey(RootRecord);
                uint current = RootRecord;
                foreach (string part in rest.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries))
                    if (!TryGetChildIndex(current, part, out current)) return false;
                dirIndex = current;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryGetTotals(uint dirIndex, out DirTotals totals)
        {
            DirNode node;
            if (_dirs.TryGetValue(dirIndex, out node))
            {
                totals = new DirTotals(node.TotalBytes, node.TotalFiles, node.TotalDirs);
                return true;
            }
            totals = new DirTotals();
            return false;
        }

        public bool TryGetChildTotals(uint dirIndex, string childName, out DirTotals totals)
        {
            uint child;
            if (TryGetChildIndex(dirIndex, childName, out child)) return TryGetTotals(child, out totals);
            totals = new DirTotals();
            return false;
        }

        private bool TryGetChildIndex(uint dirIndex, string name, out uint child)
        {
            child = 0;
            DirNode node;
            if (!_dirs.TryGetValue(dirIndex, out node)) return false;
            // Строится при первом обращении: большинство папок никогда не открывают, и таблицы поиска для
            // всех сразу удвоили бы построение индекса впустую.
            if (node.Lookup == null)
            {
                Dictionary<string, uint> lookup = new Dictionary<string, uint>(node.ChildDirs == null ? 0 : node.ChildDirs.Count, StringComparer.OrdinalIgnoreCase);
                if (node.ChildDirs != null)
                {
                    foreach (uint c in node.ChildDirs)
                    {
                        DirNode cn;
                        if (_dirs.TryGetValue(c, out cn)) lookup[cn.Name] = c;
                    }
                }
                node.Lookup = lookup;
            }
            return node.Lookup.TryGetValue(name, out child);
        }
    }

    // Читает $MFT с сырого тома и сворачивает её в итоги по папкам.
    internal static class MftIndexBuilder
    {
        private const int ChunkSize = 4 * 1024 * 1024;
        private const uint RootIndex = 5;                  // закреплено форматом: запись 5 — всегда корень тома
        private const long MaxRecords = 20000000;          // ~13 байт ОЗУ на запись; выше разумнее обход

        private const uint AttrAttributeList = 0x20;
        private const uint AttrFileName = 0x30;
        private const uint AttrData = 0x80;
        private const uint AttrEnd = 0xFFFFFFFF;

        private const ushort FlagInUse = 0x0001;
        private const ushort FlagDirectory = 0x0002;

        private const byte NamespaceDos = 2;

        [Flags]
        private enum RecordFlag : byte
        {
            None = 0,
            InUse = 1,
            Directory = 2,
            HasParent = 4,
        }

        public static NtfsVolumeIndex Build(char drive, Action<int> progress, CancellationToken ct)
        {
            Stopwatch sw = Stopwatch.StartNew();
            using (RawVolume volume = RawVolume.Open(drive))
            {
                byte[] boot = new byte[512];
                volume.Read(0, boot, 0, boot.Length);
                VolumeGeometry geometry = VolumeGeometry.Parse(boot);

                // Запись 0 — сама $MFT: список отрезков её $DATA — карта таблицы, которую предстоит прочитать.
                byte[] firstRecord = new byte[geometry.RecordSize];
                volume.Read(geometry.MftOffset, firstRecord, 0, firstRecord.Length);
                ApplyFixups(firstRecord, 0, firstRecord.Length);

                long dataSize;
                List<Extent> extents = ReadMftDataAttribute(firstRecord, geometry, out dataSize);
                long recordCount = dataSize / geometry.RecordSize;
                if (recordCount <= 0 || recordCount > MaxRecords)
                    throw new NotSupportedException(Tr.S("В $MFT тома " + drive + ": " + recordCount + " записей — вне поддерживаемого диапазона",
                                                         "$MFT of volume " + drive + ": has " + recordCount + " records — outside the supported range"));

                MftStream stream = new MftStream(volume, extents, geometry.ClusterSize, dataSize);
                long[] sizes = new long[recordCount];
                uint[] parents = new uint[recordCount];
                RecordFlag[] flags = new RecordFlag[recordCount];
                Dictionary<uint, string> dirNames = new Dictionary<uint, string>(1024);
                List<long> links = new List<long>();

                ScanRecords(stream, geometry, recordCount, sizes, parents, flags, dirNames, links, progress, ct);
                Dictionary<uint, NtfsVolumeIndex.DirNode> dirs = Aggregate(recordCount, sizes, parents, flags, dirNames, links, ct);
                FsLog.Trace("MFT " + drive + ": " + recordCount + " records, " + dirs.Count + " dirs in " + sw.ElapsedMilliseconds + " ms");
                return new NtfsVolumeIndex(drive, dirs, recordCount);
            }
        }

        private static void ScanRecords(MftStream stream, VolumeGeometry geometry, long recordCount,
            long[] sizes, uint[] parents, RecordFlag[] flags, Dictionary<uint, string> dirNames, List<long> links,
            Action<int> progress, CancellationToken ct)
        {
            int recordSize = geometry.RecordSize;
            int recordsPerChunk = Math.Max(1, ChunkSize / recordSize);
            byte[] buffer = new byte[recordsPerChunk * recordSize];
            for (long start = 0; start < recordCount; start += recordsPerChunk)
            {
                ct.ThrowIfCancellationRequested();
                int count = (int)Math.Min(recordsPerChunk, recordCount - start);
                stream.Read(start * recordSize, buffer, count * recordSize);
                for (int i = 0; i < count; i++)
                    ParseRecord(buffer, i * recordSize, recordSize, (uint)(start + i), sizes, parents, flags, dirNames, links);
                if (progress != null) progress((int)((start + count) * 100 / recordCount));
            }
        }

        private static ushort U16(byte[] b, int o) { return (ushort)(b[o] | (b[o + 1] << 8)); }
        private static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
        private static ulong U64(byte[] b, int o) { return U32(b, o) | ((ulong)U32(b, o + 4) << 32); }

        internal static void ParseRecordForTest(byte[] record, uint index, long[] sizes, uint[] parents,
            byte[] flagsOut, Dictionary<uint, string> dirNames, List<long> links)
        {
            RecordFlag[] flags = new RecordFlag[flagsOut.Length];
            ParseRecord(record, 0, record.Length, index, sizes, parents, flags, dirNames, links);
            for (int i = 0; i < flags.Length; i++) flagsOut[i] = (byte)flags[i];
        }

        // Записи — по порядку, как их отдаёт том; флаги копятся между вызовами, как в ScanRecords.
        internal static Dictionary<uint, NtfsVolumeIndex.DirNode> IndexRecordsForTest(IList<byte[]> records, IList<uint> indexes, int recordCount,
            long[] sizes, uint[] parents, byte[] flagsOut, Dictionary<uint, string> dirNames, List<long> links)
        {
            RecordFlag[] flags = new RecordFlag[recordCount];
            for (int i = 0; i < records.Count; i++)
                ParseRecord(records[i], 0, records[i].Length, indexes[i], sizes, parents, flags, dirNames, links);
            for (int i = 0; i < flags.Length && i < flagsOut.Length; i++) flagsOut[i] = (byte)flags[i];
            return Aggregate(recordCount, sizes, parents, flags, dirNames, links, CancellationToken.None);
        }

        private static void ParseRecord(byte[] buf, int rec, int recordSize, uint index,
            long[] sizes, uint[] parents, RecordFlag[] flags, Dictionary<uint, string> dirNames, List<long> links)
        {
            if (U32(buf, rec) != 0x454C4946) return;          // "FILE"
            if (!ApplyFixups(buf, rec, recordSize)) return;

            ushort recordFlags = U16(buf, rec + 0x16);
            if ((recordFlags & FlagInUse) == 0) return;

            int attrOffset = U16(buf, rec + 0x14);
            int limit = (int)Math.Min((uint)recordSize, U32(buf, rec + 0x18));
            if (attrOffset < 0x18 || attrOffset >= limit) return;

            ulong baseRef = U64(buf, rec + 0x20) & 0x0000FFFFFFFFFFFFUL;
            bool isExtension = baseRef != 0;
            bool isDirectory = (recordFlags & FlagDirectory) != 0;

            // Куда относить байты $DATA: данные записи-расширения принадлежат её базовому файлу.
            if (isExtension && baseRef >= (ulong)sizes.Length) return;
            uint owner = isExtension ? (uint)baseRef : index;
            if (owner >= sizes.Length) return;

            long dataBytes = 0;
            ulong parentRef = 0;
            string name = null;
            int bestRank = int.MaxValue;
            bool linked = false;                          // первое имя файла уже заняло parents[index]

            int offset = attrOffset;
            while (offset + 16 <= limit)
            {
                uint type = U32(buf, rec + offset);
                if (type == AttrEnd) break;
                uint length = U32(buf, rec + offset + 4);
                if (length < 16 || offset + length > (uint)limit) break;

                int attr = rec + offset;
                int attrLength = (int)length;
                bool nonResident = buf[attr + 0x08] != 0;
                byte nameLength = buf[attr + 0x09];

                if (type == AttrData && nameLength == 0)
                {
                    // Именованные потоки (ADS) исключены намеренно: число совпадает с тем, что для того же
                    // файла сообщают обход и Проводник.
                    if (!nonResident)
                    {
                        dataBytes += U32(buf, attr + 0x10);
                    }
                    else if (attrLength >= 0x38 && U64(buf, attr + 0x10) == 0)     // размер несёт отрезок с StartVCN 0
                    {
                        dataBytes += (long)U64(buf, attr + 0x30);
                    }
                }
                else if (type == AttrFileName && !nonResident)
                {
                    int valueOffset = U16(buf, attr + 0x14);
                    if (valueOffset + 0x42 <= attrLength)
                    {
                        int value = attr + valueOffset;
                        int valueLength = attrLength - valueOffset;
                        byte nameSpace = buf[value + 0x41];
                        ulong parent = U64(buf, value) & 0x0000FFFFFFFFFFFFUL;
                        // Каждое имя файла, кроме DOS-псевдонима 8.3, — отдельная жёсткая ссылка, и папка, где она
                        // лежит, считает файл целиком. Имена сверх первого (и имена из записей-расширений: у файла
                        // с сотней ссылок они не помещаются в базовую запись) — в links, парой «запись, папка».
                        if (!isDirectory && nameSpace != NamespaceDos && parent < (ulong)parents.Length)
                        {
                            if (!isExtension && !linked)
                            {
                                parents[index] = (uint)parent;
                                linked = true;
                            }
                            else
                            {
                                links.Add(((long)owner << 32) | (uint)parent);
                            }
                        }
                        // Папка же имеет одно место в дереве: берётся лучшее имя, которое видит пользователь.
                        int rank = NameRank(nameSpace);
                        if (isDirectory && !isExtension && rank < bestRank)
                        {
                            parentRef = parent;
                            bestRank = rank;
                            int nameBytes = buf[value + 0x40] * 2;
                            if (0x42 + nameBytes <= valueLength) name = Encoding.Unicode.GetString(buf, value + 0x42, nameBytes);
                        }
                    }
                }
                offset += attrLength;
            }

            if (dataBytes > 0) sizes[owner] += dataBytes;
            if (isExtension) return;

            RecordFlag f = RecordFlag.InUse;
            if (isDirectory) f |= RecordFlag.Directory;
            if (bestRank != int.MaxValue && parentRef < (ulong)parents.Length)
            {
                parents[index] = (uint)parentRef;
                f |= RecordFlag.HasParent;
            }
            if (linked) f |= RecordFlag.HasParent;
            flags[index] = f;

            // Собственное имя корня — "."; панели нужно пустое, чтобы пути склеивались без мусора.
            if (isDirectory) dirNames[index] = index == RootIndex ? "" : (name ?? "");
        }

        // Меньше — лучше. DOS-псевдоним 8.3 принимается, только когда у файла больше ничего нет.
        private static int NameRank(byte nameNamespace)
        {
            switch (nameNamespace)
            {
                case 3: return 0;           // имя Win32, которое уже законно в 8.3
                case 1: return 1;           // Win32
                case 0: return 2;           // POSIX
                case NamespaceDos: return 3;
                default: return 4;
            }
        }

        private static Dictionary<uint, NtfsVolumeIndex.DirNode> Aggregate(long recordCount,
            long[] sizes, uint[] parents, RecordFlag[] flags, Dictionary<uint, string> dirNames, List<long> links, CancellationToken ct)
        {
            const uint root = RootIndex;
            Dictionary<uint, NtfsVolumeIndex.DirNode> dirs = new Dictionary<uint, NtfsVolumeIndex.DirNode>(dirNames.Count + 1);
            foreach (KeyValuePair<uint, string> kv in dirNames)
            {
                NtfsVolumeIndex.DirNode node = new NtfsVolumeIndex.DirNode();
                node.Name = kv.Value;
                node.Parent = (flags[kv.Key] & RecordFlag.HasParent) != 0 ? parents[kv.Key] : root;
                dirs[kv.Key] = node;
            }
            if (!dirs.ContainsKey(root))
            {
                NtfsVolumeIndex.DirNode rootNode = new NtfsVolumeIndex.DirNode();
                rootNode.Parent = root;
                dirs[root] = rootNode;
            }

            foreach (KeyValuePair<uint, NtfsVolumeIndex.DirNode> kv in dirs)
            {
                if (kv.Key == root) continue;
                NtfsVolumeIndex.DirNode parent;
                if (!dirs.TryGetValue(kv.Value.Parent, out parent)) continue;
                if (parent.ChildDirs == null) parent.ChildDirs = new List<uint>();
                parent.ChildDirs.Add(kv.Key);
            }

            for (long i = 0; i < recordCount; i++)
            {
                if ((i & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
                RecordFlag f = flags[i];
                if ((f & RecordFlag.InUse) == 0 || (f & RecordFlag.Directory) != 0 || (f & RecordFlag.HasParent) == 0) continue;
                NtfsVolumeIndex.DirNode node;
                if (dirs.TryGetValue(parents[i], out node))
                {
                    node.DirectBytes += sizes[i];
                    node.DirectFiles++;
                }
            }
            // Остальные жёсткие ссылки. Флаги владельца проверяются здесь: запись-расширение с именем может
            // идти в таблице раньше своей базовой записи.
            for (int i = 0; i < links.Count; i++)
            {
                if ((i & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
                uint owner = (uint)(links[i] >> 32);
                RecordFlag f = flags[owner];
                if ((f & RecordFlag.InUse) == 0 || (f & RecordFlag.Directory) != 0) continue;
                NtfsVolumeIndex.DirNode node;
                if (dirs.TryGetValue((uint)links[i], out node))
                {
                    node.DirectBytes += sizes[owner];
                    node.DirectFiles++;
                }
            }

            // Прямой порядок от корня, затем в обратную сторону: каждый ребёнок досчитан раньше родителя.
            List<uint> order = new List<uint>(dirs.Count);
            HashSet<uint> visited = new HashSet<uint>();
            Stack<uint> stack = new Stack<uint>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                uint current = stack.Pop();
                if (!visited.Add(current)) continue;          // защита от цикла в повреждённой таблице
                order.Add(current);
                NtfsVolumeIndex.DirNode node;
                if (dirs.TryGetValue(current, out node) && node.ChildDirs != null)
                    foreach (uint child in node.ChildDirs) stack.Push(child);
            }

            for (int i = order.Count - 1; i >= 0; i--)
            {
                uint index = order[i];
                NtfsVolumeIndex.DirNode node = dirs[index];
                node.TotalBytes += node.DirectBytes;
                node.TotalFiles += node.DirectFiles;
                NtfsVolumeIndex.DirNode parent;
                if (index != root && dirs.TryGetValue(node.Parent, out parent))
                {
                    parent.TotalBytes += node.TotalBytes;
                    parent.TotalFiles += node.TotalFiles;
                    parent.TotalDirs += node.TotalDirs + 1;
                }
            }
            return dirs;
        }

        internal struct VolumeGeometry
        {
            public int ClusterSize, RecordSize;
            public long MftOffset;

            public VolumeGeometry(int clusterSize, int recordSize, long mftOffset)
            {
                ClusterSize = clusterSize; RecordSize = recordSize; MftOffset = mftOffset;
            }

            public static VolumeGeometry Parse(byte[] boot)
            {
                if (boot[3] != (byte)'N' || boot[4] != (byte)'T' || boot[5] != (byte)'F' || boot[6] != (byte)'S')
                    throw new NotSupportedException(Tr.S("Том не отформатирован в NTFS", "The volume is not formatted as NTFS"));
                int bytesPerSector = U16(boot, 0x0B);
                byte rawSectorsPerCluster = boot[0x0D];
                int sectorsPerCluster = rawSectorsPerCluster <= 0x80 ? rawSectorsPerCluster : 1 << (256 - rawSectorsPerCluster);
                long mftLcn = (long)U64(boot, 0x30);
                sbyte rawRecordSize = unchecked((sbyte)boot[0x40]);
                if (bytesPerSector < 256 || bytesPerSector > 65536 || sectorsPerCluster <= 0)
                    throw new NotSupportedException(Tr.S("Неожиданная геометрия тома NTFS", "Unexpected NTFS volume geometry"));
                int clusterSize = bytesPerSector * sectorsPerCluster;
                int recordSize = rawRecordSize > 0 ? rawRecordSize * clusterSize : 1 << (-rawRecordSize);
                // Запись может быть меньше сектора: на томе Storage Spaces с сектором 4096 записи по 1024 байта.
                if (recordSize < FixupStride || recordSize > 65536 || recordSize % FixupStride != 0)
                    throw new NotSupportedException(Tr.S("Неожиданный размер записи MFT: ", "Unexpected MFT record size: ") + recordSize);
                return new VolumeGeometry(clusterSize, recordSize, mftLcn * clusterSize);
            }
        }

        internal struct Extent
        {
            public long Lcn, Clusters;
            public Extent(long lcn, long clusters) { Lcn = lcn; Clusters = clusters; }
        }

        private static List<Extent> ReadMftDataAttribute(byte[] record, VolumeGeometry geometry, out long dataSize)
        {
            int offset = U16(record, 0x14);
            int limit = (int)Math.Min((uint)record.Length, U32(record, 0x18));
            while (offset + 16 <= limit)
            {
                uint type = U32(record, offset);
                if (type == AttrEnd) break;
                uint length = U32(record, offset + 4);
                if (length < 16 || offset + length > (uint)limit) break;

                if (type == AttrAttributeList)
                {
                    // $MFT, разнесённая по записям-расширениям, бывает только на сильно фрагментированных томах.
                    // Вместо того чтобы прочитать полтаблицы, работа отдаётся обходу.
                    throw new NotSupportedException(Tr.S("$MFT использует ATTRIBUTE_LIST — быстрый режим недоступен",
                                                         "$MFT uses ATTRIBUTE_LIST — fast mode is unavailable"));
                }
                if (type == AttrData && record[offset + 0x09] == 0 && record[offset + 0x08] != 0 && length >= 0x40)
                {
                    dataSize = (long)U64(record, offset + 0x30);
                    int runOffset = U16(record, offset + 0x20);
                    if (runOffset >= length) break;
                    List<Extent> extents = ParseRuns(record, offset + runOffset, offset + (int)length);
                    long covered = 0;
                    foreach (Extent e in extents) covered += e.Clusters;
                    if (covered * geometry.ClusterSize < dataSize)
                        throw new NotSupportedException(Tr.S("Список отрезков $MFT неполон — быстрый режим недоступен",
                                                             "The $MFT run list is incomplete — fast mode is unavailable"));
                    return extents;
                }
                offset += (int)length;
            }
            throw new NotSupportedException(Tr.S("В записи $MFT не найден непрерывный атрибут $DATA",
                                                 "No non-resident $DATA attribute in the $MFT record"));
        }

        // Список отрезков NTFS: у каждого длина и смещение кластера относительно предыдущего отрезка.
        internal static List<Extent> ParseRuns(byte[] b, int start, int end)
        {
            List<Extent> extents = new List<Extent>(16);
            long lcn = 0;
            int i = start;
            while (i < end)
            {
                byte header = b[i++];
                if (header == 0) break;
                int lengthBytes = header & 0x0F;
                int offsetBytes = (header >> 4) & 0x0F;
                if (lengthBytes == 0 || lengthBytes > 8 || offsetBytes > 8 || i + lengthBytes + offsetBytes > end) break;
                long clusters = ReadUnsigned(b, i, lengthBytes);
                i += lengthBytes;
                if (offsetBytes == 0) continue;            // разреженный отрезок: кластеров на диске нет
                lcn += ReadSigned(b, i, offsetBytes);
                i += offsetBytes;
                if (clusters > 0 && lcn > 0) extents.Add(new Extent(lcn, clusters));
            }
            return extents;
        }

        private static long ReadUnsigned(byte[] b, int start, int count)
        {
            long value = 0;
            for (int i = count - 1; i >= 0; i--) value = (value << 8) | b[start + i];
            return value;
        }

        private static long ReadSigned(byte[] b, int start, int count)
        {
            long value = unchecked((sbyte)b[start + count - 1]);
            for (int i = count - 2; i >= 0; i--) value = (value << 8) | b[start + i];
            return value;
        }

        // NTFS забирает последние два байта каждого сектора записи под проверку оборванной записи, а оригиналы
        // хранит в маленьком массиве. Это надо откатить до чтения чего-либо, иначе всё у границы сектора — мусор.
        // Шаг защиты записи — всегда 512 байт, каким бы ни был сектор тома: запись в 1024 байта несёт
        // три слова массива исправлений, в 4096 — девять. Шаг «по сектору» на томе с сектором 4096 отвергал все записи.
        private const int FixupStride = 512;

        internal static bool ApplyFixups(byte[] b, int rec, int recordSize)
        {
            int usaOffset = U16(b, rec + 0x04);
            int usaCount = U16(b, rec + 0x06);
            if (usaCount < 2 || usaOffset + usaCount * 2 > recordSize) return false;
            ushort stamp = U16(b, rec + usaOffset);
            for (int stride = 0; stride < usaCount - 1; stride++)
            {
                int tail = (stride + 1) * FixupStride - 2;
                if (tail + 2 > recordSize) return false;
                if (U16(b, rec + tail) != stamp) return false;
                b[rec + tail] = b[rec + usaOffset + 2 + stride * 2];
                b[rec + tail + 1] = b[rec + usaOffset + 3 + stride * 2];
            }
            return true;
        }

        // «Байт N в $MFT» → «байт N тома» по списку отрезков.
        private sealed class MftStream
        {
            private readonly RawVolume _volume;
            private readonly List<Extent> _extents;
            private readonly int _clusterSize;
            private readonly long _length;

            public MftStream(RawVolume volume, List<Extent> extents, int clusterSize, long length)
            {
                _volume = volume; _extents = extents; _clusterSize = clusterSize; _length = length;
            }

            public void Read(long logicalOffset, byte[] destination, int size)
            {
                long remaining = Math.Min(size, _length - logicalOffset);
                if (remaining <= 0)
                {
                    Array.Clear(destination, 0, size);
                    return;
                }
                int written = 0;
                long cursor = 0;                             // логическое начало текущего отрезка
                foreach (Extent extent in _extents)
                {
                    long extentBytes = extent.Clusters * _clusterSize;
                    if (logicalOffset + written < cursor + extentBytes)
                    {
                        long insideExtent = logicalOffset + written - cursor;
                        int take = (int)Math.Min(extentBytes - insideExtent, remaining - written);
                        if (take > 0)
                        {
                            _volume.Read(extent.Lcn * _clusterSize + insideExtent, destination, written, take);
                            written += take;
                            if (written >= remaining) break;
                        }
                    }
                    cursor += extentBytes;
                }
                if (written < size) Array.Clear(destination, written, size - written);
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Дескриптор сырого тома (\\.\C:) только на чтение. Нужны права администратора — ровно поэтому быстрый
    //  режим необязателен. Никогда не открывается на запись и никогда не пишется.
    // ------------------------------------------------------------------ //
    internal sealed class RawVolume : IDisposable
    {
        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint OPEN_EXISTING = 3;

        private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY = 0x00070000;
        private const int FallbackSectorSize = 4096;         // кратно и 512, и 4096: годится, если размер узнать не удалось

        private readonly SafeFileHandle _handle;
        private readonly int _sectorSize;

        public char DriveLetter { get; private set; }

        private RawVolume(SafeFileHandle handle, char driveLetter)
        {
            _handle = handle;
            DriveLetter = driveLetter;
            _sectorSize = QuerySectorSize(handle);
        }

        // Сырой том принимает только чтения, кратные логическому сектору: на диске с сектором 4096 байт
        // чтение 512 байт загрузочного сектора падает с ERROR_INVALID_PARAMETER.
        private static int QuerySectorSize(SafeFileHandle handle)
        {
            byte[] geometry = new byte[24];                  // DISK_GEOMETRY: BytesPerSector — последние 4 байта
            int returned;
            if (DeviceIoControl(handle, IOCTL_DISK_GET_DRIVE_GEOMETRY, IntPtr.Zero, 0, geometry, geometry.Length, out returned, IntPtr.Zero)
                && returned >= 24)
            {
                int size = BitConverter.ToInt32(geometry, 20);
                if (size >= 512 && size <= 65536 && (size & (size - 1)) == 0) return size;
            }
            return FallbackSectorSize;
        }

        public static RawVolume Open(char driveLetter)
        {
            SafeFileHandle handle = CreateFileW(@"\\.\" + char.ToUpperInvariant(driveLetter) + ":", GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,           // пока мы читаем, том полностью доступен остальным
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(Tr.S("Не удалось открыть том " + driveLetter + ": для чтения", "Cannot open volume " + driveLetter + ": for reading"),
                    new System.ComponentModel.Win32Exception(error));
            }
            return new RawVolume(handle, driveLetter);
        }

        public void Read(long offset, byte[] buffer, int index, int count)
        {
            if (count <= 0) return;
            long start = offset - offset % _sectorSize;
            long end = offset + count;
            if (end % _sectorSize != 0) end += _sectorSize - end % _sectorSize;
            if (start == offset && end == offset + count)
            {
                ReadAligned(offset, buffer, index, count);
                return;
            }
            // Невыровненный запрос читается целыми секторами и копируется: так на любом размере сектора.
            byte[] whole = new byte[end - start];
            ReadAligned(start, whole, 0, whole.Length);
            Buffer.BlockCopy(whole, (int)(offset - start), buffer, index, count);
        }

        private void ReadAligned(long offset, byte[] buffer, int index, int count)
        {
            GCHandle pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                IntPtr start = pin.AddrOfPinnedObject();
                int done = 0;
                while (done < count)
                {
                    long at = offset + done;
                    NativeOverlapped ov = new NativeOverlapped();
                    ov.OffsetLow = unchecked((int)(at & 0xFFFFFFFF));
                    ov.OffsetHigh = unchecked((int)(at >> 32));
                    int read;
                    if (!ReadFile(_handle, new IntPtr(start.ToInt64() + index + done), count - done, out read, ref ov))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == 38) read = 0;           // ERROR_HANDLE_EOF
                        else throw new IOException(Tr.S("Том " + DriveLetter + ": ошибка чтения на смещении ", "Volume " + DriveLetter + ": read error at offset ") + at,
                            new System.ComponentModel.Win32Exception(error));
                    }
                    if (read == 0)
                        throw new EndOfStreamException(Tr.S("Том " + DriveLetter + ": закончился на смещении ", "Volume " + DriveLetter + ": ended at offset ") + at);
                    done += read;
                }
            }
            finally { pin.Free(); }
        }

        public void Dispose() { _handle.Dispose(); }

        // Быстрый режим — только для локального тома NTFS, который действительно открывается сырым.
        public static bool LooksSupported(string path)
        {
            try
            {
                if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;   // UNC: сырого доступа нет
                string root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root) || root.Length < 2 || root[1] != ':') return false;
                DriveInfo drive = new DriveInfo(root);
                return drive.IsReady
                    && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable)
                    && string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(SafeFileHandle hFile, IntPtr lpBuffer, int nNumberOfBytesToRead, out int lpNumberOfBytesRead, ref NativeOverlapped lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize,
            byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
    }
}
