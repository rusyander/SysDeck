// SysDeck — нативный слой вкладки «Видеопамять».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Три источника, и все три не зависят от производителя видеокарты — NVIDIA, AMD, Intel,
// встроенная или дискретная, для Windows они одинаковы (проверено на живой системе
// 2026-09-13: RTX 4090 + встроенная AMD Radeon + Microsoft Basic Render):
//  * DXGI — список видеокарт с НАСТОЯЩИМ объёмом памяти. WMI Win32_VideoController.AdapterRAM
//    для этого не годится: поле 32-битное и на любой карте больше 4 ГБ показывает 4 ГБ;
//  * счётчики производительности «GPU Process Memory», «GPU Adapter Memory», «GPU Engine»
//    через pdh.dll — сколько памяти у какого процесса на какой карте и насколько карта занята.
//    Их пишет само графическое ядро Windows (dxgkrnl), поэтому драйвер тут ни при чём.
//    Читаются без прав администратора. Через PerformanceCounterCategory из .NET тот же
//    замер идёт 4,4 секунды, через PDH напрямую — 3 миллисекунды;
//  * командная строка процесса — чтобы отличить GPU-процесс Chromium/Electron от вкладки.
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SysDeck
{
    // Видеокарта глазами DXGI. Luid — тот же идентификатор, что стоит в именах экземпляров
    // счётчиков («luid_0x00000000_0x00014e6a»), по нему они и сводятся.
    internal class DxgiAdapter
    {
        public string Name;
        public int VendorId;
        public int DeviceId;
        public long Dedicated;
        public long Shared;
        public string Luid;
        public bool Software;
    }

    internal static partial class Native
    {
        // ------------------------------------------------------------------ //
        //  DXGI
        // ------------------------------------------------------------------ //

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_ADAPTER_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public uint LuidLow;
            public int LuidHigh;
            public uint Flags;
        }

        private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DxgiEnumAdapters1(IntPtr self, uint index, out IntPtr adapter);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int DxgiGetDesc1(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

        // Метод COM-интерфейса по номеру в таблице виртуальных функций. Номера:
        // IUnknown 0–2, IDXGIObject 3–6, IDXGIFactory 7–11, IDXGIFactory1.EnumAdapters1 = 12;
        // IDXGIAdapter 7–9, IDXGIAdapter1.GetDesc1 = 10.
        private static T VtableSlot<T>(IntPtr com, int slot) where T : class
        {
            IntPtr vtable = Marshal.ReadIntPtr(com);
            IntPtr fn = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        public static string LuidKey(int high, uint low)
        {
            return "0x" + ((uint)high).ToString("x8") + "_0x" + low.ToString("x8");
        }

        public static List<DxgiAdapter> DxgiAdapters()
        {
            List<DxgiAdapter> list = new List<DxgiAdapter>();
            Guid iid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");   // IDXGIFactory1
            IntPtr factory;
            if (CreateDXGIFactory1(ref iid, out factory) != 0 || factory == IntPtr.Zero) return list;
            try
            {
                DxgiEnumAdapters1 enumAdapters = VtableSlot<DxgiEnumAdapters1>(factory, 12);
                for (uint i = 0; i < 64; i++)
                {
                    IntPtr adapter;
                    if (enumAdapters(factory, i, out adapter) != 0 || adapter == IntPtr.Zero) break;
                    try
                    {
                        DXGI_ADAPTER_DESC1 d;
                        if (VtableSlot<DxgiGetDesc1>(adapter, 10)(adapter, out d) != 0) continue;
                        DxgiAdapter a = new DxgiAdapter();
                        a.Name = (d.Description ?? "").Trim();
                        a.VendorId = (int)d.VendorId;
                        a.DeviceId = (int)d.DeviceId;
                        a.Dedicated = (long)d.DedicatedVideoMemory.ToUInt64();
                        a.Shared = (long)d.SharedSystemMemory.ToUInt64();
                        a.Luid = LuidKey(d.LuidHigh, d.LuidLow);
                        a.Software = (d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0;
                        list.Add(a);
                    }
                    finally { Marshal.Release(adapter); }
                }
            }
            finally { Marshal.Release(factory); }
            return list;
        }

        // ------------------------------------------------------------------ //
        //  PDH — счётчики производительности
        // ------------------------------------------------------------------ //

        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhOpenQueryW(string dataSource, IntPtr userData, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        public static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
        [DllImport("pdh.dll")]
        public static extern int PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")]
        public static extern int PdhCloseQuery(IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
        private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize,
                                                              out uint itemCount, IntPtr buffer);

        private const uint PDH_FMT_DOUBLE = 0x00000200;
        private const uint PDH_FMT_LARGE = 0x00000400;
        private const int PDH_MORE_DATA = unchecked((int)0x800007D2);

        // Все экземпляры одного счётчика с подстановкой (*) разом: имя → значение.
        // Экземпляры с одинаковым именем (так бывает у счётчиков с несколькими узлами)
        // складываются. Элемент массива — PDH_FMT_COUNTERVALUE_ITEM_W: указатель на имя, затем
        // DWORD CStatus и объединение значений, выровненное по 8 байтам. На x64 это 8 + 4 + 4 + 8 =
        // 24 байта, на x86 — 4 + 4 + 8 = 16.
        public static Dictionary<string, double> PdhReadAll(IntPtr counter, bool asDouble)
        {
            Dictionary<string, double> map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (counter == IntPtr.Zero) return map;
            uint format = asDouble ? PDH_FMT_DOUBLE : PDH_FMT_LARGE;
            uint size = 0, count;
            int st = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, IntPtr.Zero);
            if (st != PDH_MORE_DATA || size == 0) return map;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                st = PdhGetFormattedCounterArrayW(counter, format, ref size, out count, buf);
                if (st != 0) return map;
                int stride = IntPtr.Size == 8 ? 24 : 16;
                int valueAt = IntPtr.Size == 8 ? 16 : 8;
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = new IntPtr(buf.ToInt64() + (long)i * stride);
                    string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    uint status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
                    // PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1; остальное — мусор.
                    if (status > 1 || name == null) continue;
                    long raw = Marshal.ReadInt64(item, valueAt);
                    double v = asDouble ? BitConverter.Int64BitsToDouble(raw) : raw;
                    if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                    double prev;
                    map[name] = map.TryGetValue(name, out prev) ? prev + v : v;
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return map;
        }

        // ------------------------------------------------------------------ //
        //  Командная строка чужого процесса
        // ------------------------------------------------------------------ //

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr buffer,
                                                            int length, out int returnLength);

        private const int ProcessCommandLineInformation = 60;    // Windows 8.1+

        // Хватает PROCESS_QUERY_LIMITED_INFORMATION — то есть любой процесс своего пользователя
        // открывается без прав администратора. null — не открылся или система старше 8.1.
        public static string CommandLineOf(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                int len = 1024;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    IntPtr buf = Marshal.AllocHGlobal(len);
                    try
                    {
                        int need;
                        int st = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out need);
                        if (st == 0)
                        {
                            // UNICODE_STRING { USHORT Length; USHORT MaximumLength; PWSTR Buffer; } —
                            // строка лежит в том же буфере сразу за заголовком.
                            int chars = Marshal.ReadInt16(buf) / 2;
                            IntPtr text = Marshal.ReadIntPtr(buf, IntPtr.Size == 8 ? 8 : 4);
                            if (chars <= 0 || text == IntPtr.Zero) return "";
                            return Marshal.PtrToStringUni(text, chars);
                        }
                        if (need <= len) return null;
                        len = need + 64;
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                return null;
            }
            finally { CloseHandle(h); }
        }

        // ------------------------------------------------------------------ //
        //  Перезапуск видеодрайвера — Win+Ctrl+Shift+B
        // ------------------------------------------------------------------ //

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public IntPtr dwExtraInfo;
        }

        // Объединение нужно ради РАЗМЕРА: SendInput сверяет cbSize с sizeof(INPUT), а тот
        // определяется самым длинным членом (MOUSEINPUT) — 40 байт на x64, 28 на x86. Структура
        // с одним KEYBDINPUT короче, и SendInput молча возвращает 0.
        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private static INPUT Key(ushort vk, bool up, bool extended)
        {
            INPUT i = new INPUT();
            i.type = INPUT_KEYBOARD;
            i.u.ki.wVk = vk;
            i.u.ki.wScan = (ushort)MapVirtualKey(vk, 0);
            i.u.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
            return i;
        }

        // То же сочетание, что нажимает человек: графическое ядро перезапускает драйвер
        // дисплея, экран гаснет на секунду. Возвращает число принятых событий (8 — все).
        public static uint SendGraphicsResetHotkey()
        {
            const ushort VK_LWIN = 0x5B, VK_LCONTROL = 0xA2, VK_LSHIFT = 0xA0, VK_B = 0x42;
            INPUT[] seq = new INPUT[]
            {
                Key(VK_LWIN, false, true), Key(VK_LCONTROL, false, false), Key(VK_LSHIFT, false, false), Key(VK_B, false, false),
                Key(VK_B, true, false), Key(VK_LSHIFT, true, false), Key(VK_LCONTROL, true, false), Key(VK_LWIN, true, true)
            };
            return SendInput((uint)seq.Length, seq, Marshal.SizeOf(typeof(INPUT)));
        }
    }
}
