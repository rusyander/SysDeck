// SysDeck — «Размеры папок»: панель уступает меню и диалогам своего Проводника.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Панель стоит в слое topmost, иначе Проводник закрывал бы её на каждом щелчке. Но контекстное меню
    //  Windows 11 (Microsoft.UI.Content.PopupWindowSiteBridge), подменю «Создать», «Свойства», диалог
    //  копирования — обычные окна того же процесса, НЕ topmost, и любое из них, попавшее на полосу панели,
    //  оказывалось под ней: пункты меню были не видны и не нажимались. Пока такое окно пересекает панель,
    //  она выходит из слоя topmost и встаёт прямо под него; окно закрылось — возвращается наверх.
    // ------------------------------------------------------------------ //
    internal sealed class PanelYield : IDisposable
    {
        private const int CheckMs = 80;               // подменю открывается наведением — заметить его до того, как в него целятся
        private const uint GW_HWNDPREV = 3;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOPMOST = 0x00000008;

        [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hWnd, uint command);
        [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);

        private readonly Form _owner;
        private readonly Timer _timer = new Timer();
        private IntPtr _explorer;
        private IntPtr _yieldedTo;

        public PanelYield(Form owner)
        {
            _owner = owner;
            _timer.Interval = CheckMs;
            _timer.Tick += delegate { if (Cover() != _yieldedTo) Place(Rectangle.Empty); };
            _owner.VisibleChanged += delegate
            {
                if (_owner.Visible) _timer.Start();
                else { _timer.Stop(); _yieldedTo = IntPtr.Zero; }
            };
        }

        public IntPtr Explorer { set { _explorer = value; } }

        // Поднять панель (и, если target не пуст, поставить на место): наверх — или под окно Проводника,
        // которое её сейчас перекрывает.
        public void Place(Rectangle target)
        {
            if (!_owner.IsHandleCreated) return;
            uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;
            if (target.IsEmpty) flags |= Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;
            IntPtr cover = Cover(target.IsEmpty ? _owner.Bounds : target);
            if (cover == IntPtr.Zero)
            {
                Win32.SetWindowPos(_owner.Handle, Win32.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height, flags);
            }
            else
            {
                // NOTOPMOST сам по себе ставит окно ВЫШЕ всех обычных, то есть снова над меню, — нужен второй шаг.
                Win32.SetWindowPos(_owner.Handle, Win32.HWND_NOTOPMOST, target.X, target.Y, target.Width, target.Height, flags);
                Win32.SetWindowPos(_owner.Handle, cover, 0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            }
            _yieldedTo = cover;
        }

        private IntPtr Cover() { return Cover(_owner.Bounds); }

        // Ближайшее к Проводнику окно его же процесса, стоящее над ним в обычном слое и пересекающее панель.
        // Идём вверх по z-порядку от самого Проводника: всё, что выше него из того же процесса, — его меню и
        // диалоги. Панель задач и прочие окна оболочки — topmost и отсеиваются.
        private IntPtr Cover(Rectangle area)
        {
            if (_explorer == IntPtr.Zero || area.IsEmpty || !Win32.IsWindow(_explorer)) return IntPtr.Zero;
            uint explorerPid;
            Win32.GetWindowThreadProcessId(_explorer, out explorerPid);
            IntPtr own = _owner.IsHandleCreated ? _owner.Handle : IntPtr.Zero;
            int guard = 0;
            for (IntPtr h = GetWindow(_explorer, GW_HWNDPREV); h != IntPtr.Zero && guard < 512; h = GetWindow(h, GW_HWNDPREV), guard++)
            {
                if (h == own || !Win32.IsWindowVisible(h)) continue;
                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) break;   // дальше только слой topmost
                uint pid;
                Win32.GetWindowThreadProcessId(h, out pid);
                if (pid != explorerPid) continue;
                if (Win32.GetVisualBounds(h).IntersectsWith(area)) return h;
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            _timer.Stop();
            _timer.Dispose();
        }
    }
}
