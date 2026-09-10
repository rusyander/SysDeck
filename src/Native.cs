// Windows Process Cleaner — P/Invoke: kernel32 / user32 / advapi32 / psapi / iphlpapi / shell32 / setupapi / ntdll
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    // ------------------------------------------------------------------ //
    //  WinAPI
    // ------------------------------------------------------------------ //
    internal static partial class Native
    {
        // --- Toolhelp снимок процессов ---
        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        public const uint TH32CS_SNAPPROCESS = 0x00000002;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr h);

        // --- Окна ---
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        // --- TCP таблица (владелец PID) ---
        [DllImport("iphlpapi.dll", SetLastError = true)]
        public static extern uint GetExtendedTcpTable(IntPtr pTable, ref int size,
            bool order, int af, int tableClass, int reserved);

        public const int AF_INET = 2;
        public const int AF_INET6 = 23;
        public const int TCP_TABLE_OWNER_PID_ALL = 5;
        public const int MIB_TCP_STATE_LISTEN = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCP6ROW_OWNER_PID
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] localAddr;
            public uint localScopeId;
            public uint localPort;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] remoteAddr;
            public uint remoteScopeId;
            public uint remotePort;
            public uint state;
            public uint owningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MIB_TCPROW_OWNER_PID
        {
            public uint state;
            public uint localAddr;
            public uint localPort;   // сетевой порядок байт, значимы младшие 2 байта
            public uint remoteAddr;
            public uint remotePort;
            public uint owningPid;
        }

        // --- Завершение по WM_CLOSE ---
        public const uint WM_CLOSE = 0x0010;
        public const uint SMTO_ABORTIFHUNG = 0x0002;   // не ждать зависшее окно весь таймаут
        [DllImport("user32.dll")]
        public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam,
            IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        // --- Standby Memory (ntdll) ---
        [DllImport("ntdll.dll")]
        public static extern int NtSetSystemInformation(int infoClass, IntPtr info, int length);
        public const int SystemMemoryListInformation = 0x50;
        public const int MemoryPurgeStandbyList = 4;
        public const int MemoryEmptyWorkingSets = 2;

        // --- Привилегии ---
        [StructLayout(LayoutKind.Sequential)]
        public struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_PRIVILEGES { public uint Count; public LUID Luid; public uint Attributes; }

        public const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        public const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        public const uint TOKEN_QUERY = 0x0008;

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern bool LookupPrivilegeValue(string host, string name, out LUID luid);
        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll,
            ref TOKEN_PRIVILEGES newState, uint len, IntPtr prev, IntPtr retLen);
        [DllImport("kernel32.dll")]
        public static extern IntPtr GetCurrentProcess();

        // --- Владелец процесса (SID) ---
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")]
        public static extern IntPtr LocalFree(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        public struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)]
        public struct TOKEN_USER { public SID_AND_ATTRIBUTES User; }

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int infoClass,
            IntPtr buf, int len, out int retLen);
        [DllImport("advapi32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr str);
        public const int TokenUser = 1;

        public static string GetProcessUserSid(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try { return GetProcessUserSid(h); }
            finally { CloseHandle(h); }
        }

        // Тот же SID, но по уже открытому хэндлу — чтобы не открывать процесс повторно.
        public static string GetProcessUserSid(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            {
                IntPtr token;
                if (!OpenProcessToken(h, TOKEN_QUERY, out token)) return null;
                try
                {
                    int len = 0;
                    GetTokenInformation(token, TokenUser, IntPtr.Zero, 0, out len);
                    if (len <= 0) return null;
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        if (!GetTokenInformation(token, TokenUser, buf, len, out len)) return null;
                        TOKEN_USER tu = (TOKEN_USER)Marshal.PtrToStructure(buf, typeof(TOKEN_USER));
                        IntPtr sidStr;
                        if (!ConvertSidToStringSid(tu.User.Sid, out sidStr)) return null;
                        try { return Marshal.PtrToStringAuto(sidStr); }
                        finally { LocalFree(sidStr); }
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                finally { CloseHandle(token); }
            }
        }

        // --- Быстрый опрос процесса ---
        // Process.GetProcessById() в .NET Framework на КАЖДЫЙ вызов снимает полный
        // список всех процессов системы (NtQuerySystemInformation с большим буфером).
        // Опрос N процессов через него стоит O(N^2); при 300 процессах тик мониторинга
        // занимает секунды и вешает UI. Ниже — прямые вызовы: один OpenProcess на процесс.
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_TERMINATE = 0x0001;
        public const uint SYNCHRONIZE = 0x00100000;
        public const uint WAIT_OBJECT_0 = 0;
        public const uint WAIT_TIMEOUT = 0x102;
        public const uint STILL_ACTIVE = 259;

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit,
            out long kernel, out long user);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROCESS_MEMORY_COUNTERS
        {
            public uint cb;
            public uint PageFaultCount;
            public IntPtr PeakWorkingSetSize;
            public IntPtr WorkingSetSize;
            public IntPtr QuotaPeakPagedPoolUsage;
            public IntPtr QuotaPagedPoolUsage;
            public IntPtr QuotaPeakNonPagedPoolUsage;
            public IntPtr QuotaNonPagedPoolUsage;
            public IntPtr PagefileUsage;
            public IntPtr PeakPagefileUsage;
        }
        [DllImport("psapi.dll", SetLastError = true)]
        public static extern bool GetProcessMemoryInfo(IntPtr h, out PROCESS_MEMORY_COUNTERS c, uint size);

        // Путь к exe без Process.MainModule: MainModule перечисляет ВСЕ модули процесса
        // и бросает исключение при несовпадении битности — на цикле это очень дорого.
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern bool QueryFullProcessImageName(IntPtr h, uint flags,
            StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetExitCodeProcess(IntPtr h, out uint code);

        public static string QueryImagePath(IntPtr h)
        {
            if (h == IntPtr.Zero) return null;
            StringBuilder sb = new StringBuilder(1024);
            int len = sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref len)) return sb.ToString(0, len);
            return null;
        }

        public static bool QueryTimes(IntPtr h, out TimeSpan cpu, out DateTime startLocal)
        {
            cpu = TimeSpan.Zero; startLocal = DateTime.MinValue;
            long creation, exit, kernel, user;
            if (!GetProcessTimes(h, out creation, out exit, out kernel, out user)) return false;
            cpu = TimeSpan.FromTicks(kernel + user);
            try { startLocal = DateTime.FromFileTime(creation); } catch { startLocal = DateTime.MinValue; }
            return true;
        }

        public static long QueryWorkingSet(IntPtr h)
        {
            PROCESS_MEMORY_COUNTERS c;
            if (GetProcessMemoryInfo(h, out c, (uint)Marshal.SizeOf(typeof(PROCESS_MEMORY_COUNTERS))))
                return c.WorkingSetSize.ToInt64();
            return 0;
        }

        // --- Память системы ---
        [StructLayout(LayoutKind.Sequential)]
        public struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        // Аптайм системы: Environment.TickCount переполняется через 24,9 дня, 64-битный счётчик — нет.
        [DllImport("kernel32.dll")]
        public static extern ulong GetTickCount64();

        // --- Тёмный заголовок окна (DWM) ---
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Тёмные полосы прокрутки у TreeView/ListView: та же тема, что у Проводника.
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        public const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;

        // --- Корзина ---
        [StructLayout(LayoutKind.Sequential, Pack = 0)]
        public struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHQueryRecycleBin(string rootPath, ref SHQUERYRBINFO info);
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        public static extern int SHEmptyRecycleBin(IntPtr hwnd, string rootPath, uint flags);
        public const uint SHERB_NOCONFIRMATION = 0x1;
        public const uint SHERB_NOPROGRESSUI = 0x2;
        public const uint SHERB_NOSOUND = 0x4;

        // --- Быстрый обход диска (вкладка «Диск») ---
        // FindFirstFileEx с FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH: не запрашивает
        // короткие 8.3-имена и забирает записи каталога большими порциями — на системном
        // диске это в 2-3 раза быстрее DirectoryInfo.EnumerateFileSystemInfos.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }
        public const int FindExInfoBasic = 1;
        public const int FindExSearchNameMatch = 0;
        public const int FIND_FIRST_EX_LARGE_FETCH = 2;
        public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);
        public const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
        public const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
        public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        public const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindFirstFileExW(string fileName, int infoLevel, out WIN32_FIND_DATA data,
                                                     int searchOp, IntPtr filter, int flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool FindNextFileW(IntPtr h, out WIN32_FIND_DATA data);
        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FindClose(IntPtr h);

        // --- Удаление по длинному пути (движок очистки) ---
        // DeleteFileW/RemoveDirectoryW/SetFileAttributesW принимают \\?\; File.Delete и
        // Directory.Delete в режиме старых путей .NET (нет app.config) падают за MAX_PATH.
        public const uint FILE_ATTRIBUTE_READONLY = 0x1;
        public const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFileW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool RemoveDirectoryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool SetFileAttributesW(string path, uint attrs);

        // --- Владение файлом (остатки обновлений Windows принадлежат TrustedInstaller) ---
        public const int ERROR_ACCESS_DENIED = 5;
        private const int SE_FILE_OBJECT = 1;
        private const uint OWNER_SECURITY_INFORMATION = 0x00000001;
        private const uint DACL_SECURITY_INFORMATION = 0x00000004;
        private const string AdministratorsSid = "S-1-5-32-544";
        private static bool _ownerPrivilegesTried;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool ConvertStringSidToSidW(string sid, out IntPtr psid);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int SetNamedSecurityInfoW(string name, int objectType, uint secInfo,
                                                       IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

        public static bool TakeOwnership(string path) { return TakeOwnership(path, AdministratorsSid); }

        // Владелец — Администраторы, затем пустой (NULL) DACL: полный доступ всем. Годится только
        // для объекта, который тут же удаляется, — на что-то живое так делать нельзя.
        // SeTakeOwnership даёт право стать владельцем чужого объекта, SeRestore — назначить
        // владельцем не себя, а группу; обе есть у администратора с повышением (UAC).
        public static bool TakeOwnership(string path, string ownerSid)
        {
            if (!_ownerPrivilegesTried)
            {
                _ownerPrivilegesTried = true;
                EnablePrivilege("SeTakeOwnershipPrivilege");
                EnablePrivilege("SeRestorePrivilege");
            }
            IntPtr sid;
            if (!ConvertStringSidToSidW(ownerSid, out sid)) return false;
            try
            {
                if (SetNamedSecurityInfoW(path, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION, sid, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                    return false;
                return SetNamedSecurityInfoW(path, SE_FILE_OBJECT, DACL_SECURITY_INFORMATION, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
            }
            finally { LocalFree(sid); }
        }

        // Атрибуты пути (через \\?\); INVALID_FILE_ATTRIBUTES — пути нет или нет доступа.
        public static uint AttributesOf(string p)
        {
            if (string.IsNullOrEmpty(p)) return INVALID_FILE_ATTRIBUTES;
            try { return GetFileAttributesW(LongPathOf(p)); } catch { return INVALID_FILE_ATTRIBUTES; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetLongPathNameW(string shortPath, StringBuilder longPath, uint bufLen);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder path, uint bufLen, uint flags);

        // Канонический вид пути: 8.3-псевдонимы раскрыты (RUSYAN~1 → rusyander), junction/symlink в
        // самом пути заменены целью, регистр — как на диске. Исключение «не чистить эти пути» должно
        // совпадать с корнем обхода, как бы пользователь его ни записал. Несуществующий или
        // недоступный путь (и путь на томе без буквы) возвращается как есть, без префикса \\?\.
        public static string CanonicalPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return p;
            string s = p;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint n = GetLongPathNameW(LongPathOf(s), sb, (uint)sb.Capacity);
                if (n > (uint)sb.Capacity) { sb.EnsureCapacity((int)n + 1); n = GetLongPathNameW(LongPathOf(s), sb, (uint)sb.Capacity); }
                if (n > 0 && n <= (uint)sb.Capacity) s = KeepDos(StripLongPrefix(sb.ToString()), s);
                Microsoft.Win32.SafeHandles.SafeFileHandle h = CreateFileW(LongPathOf(s), 0, 7u /* READ|WRITE|DELETE */, IntPtr.Zero,
                                                                           3u /* OPEN_EXISTING */, 0x02000000u /* FILE_FLAG_BACKUP_SEMANTICS */, IntPtr.Zero);
                if (h == null || h.IsInvalid) { if (h != null) h.Dispose(); return s; }
                using (h)
                {
                    sb.Length = 0;
                    n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0 /* FILE_NAME_NORMALIZED | VOLUME_NAME_DOS */);
                    if (n > (uint)sb.Capacity) { sb.EnsureCapacity((int)n + 1); n = GetFinalPathNameByHandleW(h, sb, (uint)sb.Capacity, 0); }
                    if (n > 0 && n <= (uint)sb.Capacity) s = KeepDos(StripLongPrefix(sb.ToString()), s);
                }
            }
            catch { }
            return s;
        }

        private static string StripLongPrefix(string s)
        {
            if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s.Substring(8);
            if (s.StartsWith(@"\\?\")) return s.Substring(4);
            return s;
        }

        // Только пути вида «X:\…» или «\\сервер\…»; «Volume{GUID}\…» (том без буквы) — оставить прежний.
        private static string KeepDos(string candidate, string fallback)
        {
            if (candidate.Length >= 2 && char.IsLetter(candidate[0]) && candidate[1] == ':') return candidate;
            if (candidate.StartsWith(@"\\") && !candidate.StartsWith(@"\\?\")) return candidate;
            return fallback;
        }

        // --- Удаление в Корзину (SHFileOperation, FOF_ALLOWUNDO) ---
        // На x86 структура упакована по 1 байту, на x64 — с естественным выравниванием;
        // неверная раскладка даёт «ошибку» 0x57/0x402 без единого удалённого файла.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct SHFILEOPSTRUCT64
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
        public struct SHFILEOPSTRUCT32
        {
            public IntPtr hwnd; public uint wFunc; public string pFrom; public string pTo; public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings; public string lpszProgressTitle;
        }
        public const uint FO_DELETE = 3;
        public const ushort FOF_SILENT = 0x0004;
        public const ushort FOF_NOCONFIRMATION = 0x0010;
        public const ushort FOF_ALLOWUNDO = 0x0040;
        public const ushort FOF_NOERRORUI = 0x0400;
        public const ushort FOF_WANTNUKEWARNING = 0x4000;   // спросить, если файл не влезает в Корзину и будет удалён насовсем
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        public static extern int SHFileOperation64(ref SHFILEOPSTRUCT64 op);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHFileOperationW")]
        public static extern int SHFileOperation32(ref SHFILEOPSTRUCT32 op);

        // Список путей через \0, двойной \0 в конце. Возвращает 0 при успехе, иначе код
        // оболочки (0x71 DE_SAMEFILE, 0x7C DE_INVALIDFILES, 0x402 — неверный путь и т.п.).
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);

        // Чтение файла по длинному пути (\?\): FileStream(string) в .NET Framework без
        // app.config отвергает префикс \?\ («формат пути не поддерживается»), а через
        // дескриптор CreateFileW читается что угодно. Null — файл открыть не удалось.
        public static FileStream OpenReadLong(string path, int bufferSize)
        {
            string p = path.StartsWith(@"\\?\") || path.StartsWith(@"\\") ? path : @"\\?\" + path;
            Microsoft.Win32.SafeHandles.SafeFileHandle h = null;
            try
            {
                h = CreateFileW(p, 0x80000000u /* GENERIC_READ */, 7u /* READ|WRITE|DELETE */, IntPtr.Zero,
                                3u /* OPEN_EXISTING */, 0x08000000u /* FILE_FLAG_SEQUENTIAL_SCAN */, IntPtr.Zero);
                if (h == null || h.IsInvalid) { if (h != null) h.Dispose(); return null; }
                return new FileStream(h, FileAccess.Read, bufferSize, false);
            }
            catch { if (h != null) h.Dispose(); return null; }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributesW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint bufLen);
        public const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        public const int MaxShellPath = 259;   // MAX_PATH без завершающего нуля

        public static string LongPathOf(string p)
        {
            return p.StartsWith(@"\\?\") || p.StartsWith(@"\\") ? p : @"\\?\" + p;
        }

        // Есть ли путь на диске — с учётом путей длиннее 260 знаков: File.Exists и
        // Directory.Exists в .NET Framework (режим старых путей) на таком пути молча
        // отвечают «нет», и неудавшееся удаление засчитывалось как удачное.
        public static bool PathExists(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try { return GetFileAttributesW(LongPathOf(p)) != INVALID_FILE_ATTRIBUTES; } catch { return false; }
        }

        public static bool IsDirectoryPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            try
            {
                uint a = GetFileAttributesW(LongPathOf(p));
                return a != INVALID_FILE_ATTRIBUTES && (a & FILE_ATTRIBUTE_DIRECTORY) != 0;
            }
            catch { return false; }
        }

        // Путь в виде, который примет оболочка: SHFileOperation не понимает \\?\ и не
        // принимает ничего длиннее MAX_PATH. Длинный путь заменяется на имена 8.3
        // (GetShortPathName). Null — укоротить нельзя (на томе отключены короткие имена).
        public static string ShellPath(string p)
        {
            if (string.IsNullOrEmpty(p)) return null;
            if (p.Length <= MaxShellPath && !p.StartsWith(@"\\?\")) return p;
            try
            {
                string lp = LongPathOf(p);
                StringBuilder sb = new StringBuilder(lp.Length + 16);
                uint n = GetShortPathNameW(lp, sb, (uint)sb.Capacity);
                if (n == 0 || n >= sb.Capacity) return null;
                string s = sb.ToString();
                if (s.StartsWith(@"\\?\")) s = s.Substring(4);
                return s.Length <= MaxShellPath ? s : null;
            }
            catch { return null; }
        }

        // tooLong — сколько путей не удалось передать оболочке (длиннее MAX_PATH и без 8.3).
        public static int RecycleFiles(IntPtr owner, IList<string> paths, out bool aborted, out int tooLong)
        {
            aborted = false; tooLong = 0;
            if (paths == null || paths.Count == 0) return 0;
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (string p in paths)
            {
                string sp = ShellPath(p);
                if (sp == null) { tooLong++; continue; }
                sb.Append(sp).Append('\0'); n++;
            }
            if (n == 0) return 0;
            sb.Append('\0');
            // Без FOF_WANTNUKEWARNING файл крупнее квоты Корзины оболочка молча удаляла насовсем — при
            // том, что пользователю задавался вопрос «переместить в Корзину». Теперь она спросит.
            ushort flags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING;
            int rc;
            if (IntPtr.Size == 8)
            {
                SHFILEOPSTRUCT64 op = new SHFILEOPSTRUCT64();
                op.hwnd = owner; op.wFunc = FO_DELETE; op.pFrom = sb.ToString(); op.fFlags = flags;
                rc = SHFileOperation64(ref op);
                aborted = op.fAnyOperationsAborted;
            }
            else
            {
                SHFILEOPSTRUCT32 op = new SHFILEOPSTRUCT32();
                op.hwnd = owner; op.wFunc = FO_DELETE; op.pFrom = sb.ToString(); op.fFlags = flags;
                rc = SHFileOperation32(ref op);
                aborted = op.fAnyOperationsAborted;
            }
            return rc;
        }

        // --- SetupAPI: какие INF привязаны к присутствующим устройствам ---
        [StructLayout(LayoutKind.Sequential)]
        public struct SP_DEVINFO_DATA { public uint cbSize; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVPROPKEY { public Guid fmtid; public uint pid; }
        public static readonly DEVPROPKEY DEVPKEY_Device_DriverInfPath =
            new DEVPROPKEY { fmtid = new Guid("a8b865dd-2e3d-4094-ad97-e593a70c75d6"), pid = 5 };
        public const uint DIGCF_PRESENT = 0x2;
        public const uint DIGCF_ALLCLASSES = 0x4;
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr hwndParent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInfo(IntPtr devInfoSet, uint memberIndex, ref SP_DEVINFO_DATA data);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDevicePropertyW(IntPtr devInfoSet, ref SP_DEVINFO_DATA data, ref DEVPROPKEY key,
                                                            out uint propType, byte[] buf, uint bufSize, out uint required, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr devInfoSet);

        public static bool EnablePrivilege(string name)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                return false;
            try
            {
                LUID luid;
                if (!LookupPrivilegeValue(null, name, out luid)) return false;
                TOKEN_PRIVILEGES tp = new TOKEN_PRIVILEGES();
                tp.Count = 1;
                tp.Luid = luid;
                tp.Attributes = SE_PRIVILEGE_ENABLED;
                if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero)) return false;
                // AdjustTokenPrivileges возвращает TRUE и когда привилегия НЕ выдана
                // (ERROR_NOT_ALL_ASSIGNED = 1300): без этой проверки вызывающий код
                // считал, что SeDebug/SeProfileSingleProcess у него есть, и шёл дальше.
                return Marshal.GetLastWin32Error() != 1300;
            }
            finally { CloseHandle(token); }
        }
    }
}
