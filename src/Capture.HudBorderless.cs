// SysDeck — «игра без рамки на весь экран» (сочетание Ctrl+Alt+B, выполняет процесс оверлея).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Поверх эксклюзивного полноэкранного режима никакое внешнее окно не видно — так устроена Windows. Игра в оконном
// режиме выводит кадры через DWM, и столбик над ней виден. Сочетание снимает с активного окна рамку и заголовок и
// растягивает его на монитор, где оно стоит; повторное нажатие возвращает стиль и место, какими они были.
// В процесс игры ничего не встраивается: меняются только стиль и положение окна, как у Borderless Gaming.
// Окно игры, запущенной с правами администратора, меняется только из оверлея с правами (иначе Windows отказывает).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;

namespace SysDeck.Capture
{
    internal static class HudBorderless
    {
        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        internal const long WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000,
                            WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000, WS_CHILD = 0x40000000;
        internal const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_WINDOWEDGE = 0x100, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;
        private const uint SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_NOOWNERZORDER = 0x200, SWP_SHOWWINDOW = 0x40;
        private const int SW_RESTORE = 9, SW_MAXIMIZE = 3, MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out CapNative.RECT rect);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

        private sealed class Saved
        {
            public long Style, ExStyle;
            public Rectangle Bounds;
            public bool Maximized;
        }

        // Окна, которым рамку сняли мы: по ним повторное нажатие возвращает всё как было.
        private static readonly Dictionary<IntPtr, Saved> _saved = new Dictionary<IntPtr, Saved>();
        private static readonly object _gate = new object();

        internal static long BorderlessStyle(long style)
        {
            return style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        }

        internal static long BorderlessExStyle(long exStyle)
        {
            return exStyle & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        }

        // Активное окно — сюда; ответ — надпись для столбика.
        public static string ToggleForeground()
        {
            IntPtr hwnd = CapNative.GetForegroundWindow();
            int pid;
            CapNative.GetWindowThreadProcessId(hwnd, out pid);
            if (hwnd != IntPtr.Zero && pid == System.Diagnostics.Process.GetCurrentProcess().Id)
                return Tr.S("Это окно самой программы", "This is the program's own window");
            return Toggle(hwnd);
        }

        public static string Toggle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || hwnd == GetShellWindow())
                return Tr.S("Нет активного окна игры", "No active game window");
            lock (_gate)
            {
                Saved was;
                if (_saved.TryGetValue(hwnd, out was))
                {
                    _saved.Remove(hwnd);
                    if (!Apply(hwnd, was.Style, was.ExStyle, was.Bounds)) return Denied();
                    if (was.Maximized) ShowWindow(hwnd, SW_MAXIMIZE);
                    return Tr.S("Рамка окна возвращена", "Window frame restored");
                }
                ForgetClosed();

                long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                if ((style & WS_CHILD) != 0) return Tr.S("Нет активного окна игры", "No active game window");
                Rectangle monitor = MonitorBounds(hwnd);
                if (monitor.IsEmpty) return Tr.S("Не найден монитор окна", "The window's monitor was not found");
                CapNative.RECT r;
                GetWindowRect(hwnd, out r);
                if (BorderlessStyle(style) == style && r.ToRectangle() == monitor)
                    return Tr.S("Окно уже на весь экран без рамки. Не видно столбик — включите в игре оконный режим и нажмите ещё раз",
                                "The window is already borderless full screen. Overlay not visible — switch the game to windowed mode and press again");

                Saved s = new Saved();
                s.Style = style; s.ExStyle = ex; s.Maximized = IsZoomed(hwnd);
                if (s.Maximized) ShowWindow(hwnd, SW_RESTORE);
                GetWindowRect(hwnd, out r);
                s.Bounds = r.ToRectangle();
                if (!Apply(hwnd, BorderlessStyle(style), BorderlessExStyle(ex), monitor)) return Denied();
                _saved[hwnd] = s;
                return Tr.S("Окно без рамки на весь экран (Ctrl+Alt+B — вернуть)", "Borderless full screen (Ctrl+Alt+B — undo)");
            }
        }

        public static bool IsBorderlessByUs(IntPtr hwnd)
        {
            lock (_gate) return _saved.ContainsKey(hwnd);
        }

        private static bool Apply(IntPtr hwnd, long style, long ex, Rectangle bounds)
        {
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style));
            if (GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() != style) return false;
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex));
            return SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                                SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
        }

        private static string Denied()
        {
            return Tr.S("Windows не дала изменить окно: игра запущена с правами администратора — включите «Оверлей с правами»",
                        "Windows refused to change the window: the game runs as administrator — enable the elevated overlay");
        }

        private static Rectangle MonitorBounds(IntPtr hwnd)
        {
            IntPtr mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            CapNative.MONITORINFOEX mi = new CapNative.MONITORINFOEX();
            mi.cbSize = Marshal.SizeOf(typeof(CapNative.MONITORINFOEX));
            if (mon == IntPtr.Zero || !CapNative.GetMonitorInfo(mon, ref mi)) return Rectangle.Empty;
            return mi.rcMonitor.ToRectangle();
        }

        private static void ForgetClosed()
        {
            List<IntPtr> gone = new List<IntPtr>();
            foreach (IntPtr h in _saved.Keys) if (!IsWindow(h)) gone.Add(h);
            foreach (IntPtr h in gone) _saved.Remove(h);
        }
    }
}
