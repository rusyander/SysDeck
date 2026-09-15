// SysDeck — нативный слой вкладки «Память».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Всё, чем меряется физическая память, живёт здесь, а не в Engine: одно место, где видно,
// какая структура какого размера и почему смещения именно такие.
//
// Главное, что стоит знать про эти вызовы (проверено на живой системе 2026-09-10):
//  * ЧИТАТЬ состояние памяти права администратора не нужны вовсе — ни списки страниц, ни
//    приоритеты standby, ни размеры пулов, ни снимок всех процессов, ни теги пула. Поэтому
//    вкладка показывает полную картину сразу после запуска, без единого окна UAC;
//  * права нужны только на ДЕЙСТВИЯ (очистка списков, сброс системного рабочего набора,
//    сброс рабочего набора чужого процесса из сессии 0). Их поднимает Elevation.Ram.cs;
//  * структуры ниже упакованы по-разному в 32 и 64 битах, поэтому смещения считаются от
//    IntPtr.Size, а не забиты числами. Ошибка в смещении здесь — это не «неверная цифра
//    на экране», а чтение чужой памяти, поэтому каждая проверена на реальном выводе.
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace SysDeck
{
    internal static partial class Native
    {
        // --- Классы информации NtQuerySystemInformation ---
        public const int SystemPerformanceInformation = 0x02;
        public const int SystemProcessInformation = 0x05;
        public const int SystemFileCacheInformation = 0x15;
        public const int SystemPoolTagInformation = 0x16;
        // SystemMemoryListInformation (0x50) и команды списков объявлены в Native.cs —
        // ими же чистится Standby Memory из «Ускорить», и разъезжаться им нельзя.
        public const int MemoryFlushModifiedList = 3;
        public const int MemoryPurgeLowPriorityStandbyList = 5;

        public const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
        public const uint STATUS_ACCESS_DENIED = 0xC0000022;
        public const uint STATUS_PRIVILEGE_NOT_HELD = 0xC0000061;

        public const uint PROCESS_SET_QUOTA = 0x0100;

        [DllImport("ntdll.dll")]
        public static extern int NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returned);

        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, int size);

        // Сброс рабочего набора одного процесса. Для процессов ТОГО ЖЕ пользователя работает
        // без всяких прав (проверено на explorer.exe и chrome.exe); на процессы сессии 0
        // (lsass, svchost) OpenProcess отвечает отказом 5 — там нужен помощник.
        [DllImport("psapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EmptyWorkingSet(IntPtr process);

        // Таблица SMBIOS: сколько планок стоит и по сколько. Разница между этой суммой и
        // тем, что видит система, и есть «аппаратно зарезервировано».
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint GetSystemFirmwareTable(uint provider, uint tableId, byte[] buffer, uint size);

        // Физические диапазоны лежат в реестре значением типа REG_RESOURCE_LIST (8), а его
        // RegistryKey.GetValue не отдаёт вовсе — возвращает null. Отсюда прямой вызов.
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegOpenKeyExW(IntPtr key, string subKey, int options, int sam, out IntPtr result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved,
                                                  out int type, byte[] data, ref int cb);
        [DllImport("advapi32.dll")]
        public static extern int RegCloseKey(IntPtr key);

        public static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));
        public const int KEY_READ = 0x20019;

        // PERFORMANCE_INFORMATION: cb + 10 значений размером с указатель + 3 счётчика.
        // Все размеры — В СТРАНИЦАХ, умножать на PageSize обязан вызывающий.
        [StructLayout(LayoutKind.Sequential)]
        public struct PERFORMANCE_INFORMATION
        {
            public uint cb;
            public IntPtr CommitTotal;
            public IntPtr CommitLimit;
            public IntPtr CommitPeak;
            public IntPtr PhysicalTotal;
            public IntPtr PhysicalAvailable;
            public IntPtr SystemCache;
            public IntPtr KernelTotal;
            public IntPtr KernelPaged;
            public IntPtr KernelNonpaged;
            public IntPtr PageSize;
            public uint HandleCount;
            public uint ProcessCount;
            public uint ThreadCount;
        }

        // ---------- чтение значений разрядности указателя ----------
        public static long ReadPtr(IntPtr buf, int offset)
        {
            return IntPtr.Size == 8 ? Marshal.ReadInt64(buf, offset)
                                    : (long)(uint)Marshal.ReadInt32(buf, offset);
        }

        public static long ReadU32(IntPtr buf, int offset)
        {
            return (long)(uint)Marshal.ReadInt32(buf, offset);
        }

        // ---------- SYSTEM_MEMORY_LIST_INFORMATION ----------
        // 5 счётчиков + 8 приоритетов standby + 8 «перепрофилированных» + модифицированные
        // в файле подкачки. Всё в страницах, все поля размером с указатель.
        public const int MemoryListFields = 22;
        public static int MemoryListSize { get { return MemoryListFields * IntPtr.Size; } }

        // ---------- SYSTEM_FILECACHE_INFORMATION ----------
        // CurrentSize, PeakSize (указатель), PageFaultCount (ULONG + выравнивание),
        // MinimumWorkingSet, MaximumWorkingSet, CurrentSizeIncludingTransitionInPages,
        // PeakSizeIncludingTransitionInPages (указатель), TransitionRePurposeCount, Flags.
        public static int FileCacheSize { get { return IntPtr.Size == 8 ? 64 : 36; } }
        public static int FileCacheMinOffset { get { return IntPtr.Size == 8 ? 24 : 12; } }
        public static int FileCacheTransitionOffset { get { return FileCacheMinOffset + 2 * IntPtr.Size; } }

        // ---------- SYSTEM_POOLTAG_INFORMATION ----------
        // { ULONG Count; SYSTEM_POOLTAG Tags[]; } — на x64 массив начинается с 8, а не с 4:
        // за Count идёт выравнивание до 8 байт (проверено: 157528 == 8 + 3938 * 40).
        public static int PoolTagHead { get { return IntPtr.Size == 8 ? 8 : 4; } }
        public static int PoolTagEntry { get { return IntPtr.Size == 8 ? 40 : 28; } }
        public static int PoolTagPagedUsed { get { return IntPtr.Size == 8 ? 16 : 12; } }
        public static int PoolTagNonPagedUsed { get { return IntPtr.Size == 8 ? 32 : 24; } }

        // ---------- SYSTEM_PERFORMANCE_INFORMATION ----------
        // Дальше первых четырёх LARGE_INTEGER идут только ULONG, поэтому смещения одинаковы
        // в обеих разрядностях. Отсюда берётся разбор ядра, которого нет больше нигде:
        // сколько выгружаемого пула реально сидит в памяти, сколько занято кодом драйверов.
        public const int PerfAvailablePages = 44;
        public const int PerfCommittedPages = 48;
        public const int PerfCommitLimit = 52;
        public const int PerfPeakCommitment = 56;
        public const int PerfPageFaultCount = 60;
        public const int PerfTransitionCount = 68;
        public const int PerfDemandZeroCount = 76;
        public const int PerfPageReadCount = 80;
        public const int PerfPageReadIoCount = 84;
        public const int PerfPagedPoolPages = 112;
        public const int PerfNonPagedPoolPages = 116;
        public const int PerfResidentSystemCodePage = 140;
        public const int PerfTotalSystemDriverPages = 144;
        public const int PerfResidentSystemCachePage = 164;
        public const int PerfResidentPagedPoolPage = 168;
        public const int PerfResidentSystemDriverPage = 172;
        public const int PerfMinSize = 176;      // столько нужно, чтобы прочесть всё перечисленное

        // ---------- SYSTEM_PROCESS_INFORMATION ----------
        // Снимок всех процессов одним вызовом. Именно так это делает Диспетчер задач; на
        // 612 процессах вызов стоит около 5 мс, поэтому опрос раз в секунду ничего не стоит.
        // Смещения различаются разрядностью из-за UNICODE_STRING и HANDLE внутри структуры.
        public sealed class ProcInfoLayout
        {
            public readonly int NextEntry = 0;
            public readonly int ThreadCount = 4;
            public readonly int WorkingSetPrivate = 8;      // LARGE_INTEGER, Vista+
            public readonly int HardFaultCount = 16;        // ULONG, Win7+
            public readonly int ImageName = 56;             // UNICODE_STRING
            public readonly int ImageNameBuffer;
            public readonly int UniqueProcessId;
            public readonly int InheritedFromPid;
            public readonly int HandleCount;
            public readonly int SessionId;
            public readonly int PageFaultCount;
            public readonly int WorkingSetSize;
            public readonly int QuotaPagedPoolUsage;
            public readonly int QuotaNonPagedPoolUsage;
            public readonly int PrivatePageCount;

            public ProcInfoLayout()
            {
                if (IntPtr.Size == 8)
                {
                    ImageNameBuffer = 64;
                    UniqueProcessId = 80;
                    InheritedFromPid = 88;
                    HandleCount = 96;
                    SessionId = 100;
                    PageFaultCount = 128;
                    WorkingSetSize = 144;
                    QuotaPagedPoolUsage = 160;
                    QuotaNonPagedPoolUsage = 176;
                    PrivatePageCount = 200;
                }
                else
                {
                    ImageNameBuffer = 60;
                    UniqueProcessId = 68;
                    InheritedFromPid = 72;
                    HandleCount = 76;
                    SessionId = 80;
                    PageFaultCount = 96;
                    WorkingSetSize = 104;
                    QuotaPagedPoolUsage = 112;
                    QuotaNonPagedPoolUsage = 120;
                    PrivatePageCount = 132;
                }
            }
        }

        public static readonly ProcInfoLayout ProcLayout = new ProcInfoLayout();

        // Имя образа лежит не в структуре, а по указателю внутри неё; у процесса Idle
        // указателя нет вовсе.
        public static string ReadImageName(IntPtr entry)
        {
            try
            {
                int len = (ushort)Marshal.ReadInt16(entry, ProcLayout.ImageName);
                if (len <= 0 || len > 1024) return null;
                IntPtr p = IntPtr.Size == 8
                    ? new IntPtr(Marshal.ReadInt64(entry, ProcLayout.ImageNameBuffer))
                    : new IntPtr(Marshal.ReadInt32(entry, ProcLayout.ImageNameBuffer));
                if (p == IntPtr.Zero) return null;
                return Marshal.PtrToStringUni(p, len / 2);
            }
            catch { return null; }
        }

        // ---------- CM_RESOURCE_LIST ----------
        // Дескриптор упакован по 4 байта, но объединение внутри него — 16 байт (самый
        // крупный вариант, Connection), поэтому шаг равен 20, а не 16. С шагом 16 разбор
        // «работает» и выдаёт правдоподобный мусор — это стоило отдельной проверки:
        // сумма диапазонов при верном шаге совпала с PhysicalTotal байт в байт.
        public const int ResourceDescriptorStride = 20;
        public const byte CmResourceTypeMemory = 3;
        public const byte CmResourceTypeMemoryLarge = 7;
        public const ushort CM_RESOURCE_MEMORY_LARGE_40 = 0x0200;
        public const ushort CM_RESOURCE_MEMORY_LARGE_48 = 0x0400;
        public const ushort CM_RESOURCE_MEMORY_LARGE_64 = 0x0800;

        // Чтение значения типа REG_RESOURCE_LIST целиком.
        public static byte[] ReadRawRegistryValue(string subKey, string name)
        {
            IntPtr key;
            if (RegOpenKeyExW(HKEY_LOCAL_MACHINE, subKey, 0, KEY_READ, out key) != 0) return null;
            try
            {
                int type, cb = 0;
                if (RegQueryValueExW(key, name, IntPtr.Zero, out type, null, ref cb) != 0) return null;
                if (cb <= 0 || cb > 1 << 20) return null;
                byte[] data = new byte[cb];
                if (RegQueryValueExW(key, name, IntPtr.Zero, out type, data, ref cb) != 0) return null;
                return data;
            }
            finally { RegCloseKey(key); }
        }

        // ---------- SMBIOS ----------
        public const uint FirmwareProviderRSMB = 0x52534D42;   // 'RSMB'

        public static byte[] ReadSmbios()
        {
            uint need = GetSystemFirmwareTable(FirmwareProviderRSMB, 0, null, 0);
            if (need == 0 || need > 4 << 20) return null;
            byte[] buf = new byte[need];
            uint got = GetSystemFirmwareTable(FirmwareProviderRSMB, 0, buf, need);
            if (got == 0 || got > need) return null;
            return buf;
        }

        // Строки структуры SMBIOS идут сразу за её телом, разделены нулём, набор кончается
        // двумя нулями подряд. Индекс 0 означает «строки нет».
        public static string SmbiosString(byte[] b, int stringsAt, int index)
        {
            if (index <= 0 || b == null) return null;
            int p = stringsAt;
            for (int n = 1; p < b.Length; n++)
            {
                int start = p;
                while (p < b.Length && b[p] != 0) p++;
                if (p == start) return null;                 // конец набора
                if (n == index) return Encoding.ASCII.GetString(b, start, p - start).Trim();
                p++;
            }
            return null;
        }
    }
}
