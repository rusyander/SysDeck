// Windows Process Cleaner — «Захват»: снимок пикселей экрана, мониторы, окна под курсором, имя программы, сохранение.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Скриншоты берутся GDI BitBlt с экрана, собранного DWM: 19 мс на 1440p, 43 мс на весь виртуальный экран из трёх
// мониторов (проба 13.09.2026). Этого хватает с запасом, а устройство Direct3D для снимка не нужно. Все координаты —
// физические пиксели: поток агента работает в режиме DPI «на монитор» (PMv2).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace WindowsProcessCleaner.Capture
{
    // DIB-секция с выбранным в неё DC: GDI (BitBlt) и GDI+ (Image) работают с одной и той же памятью, без копий
    // между ними. Строки сверху вниз, BGRA.
    internal sealed class DibBuffer : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPelsPerMeter, YPelsPerMeter, ClrUsed, ClrImportant;
        }

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory")] private static extern void CopyMemory(IntPtr dest, IntPtr src, IntPtr count);

        public readonly int Width, Height;
        public readonly IntPtr Dc, Bits;
        public readonly Bitmap Image;       // PArgb поверх памяти секции
        private readonly IntPtr _hbitmap, _old;

        public DibBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            BITMAPINFOHEADER info = new BITMAPINFOHEADER();
            info.Size = Marshal.SizeOf(typeof(BITMAPINFOHEADER));
            info.Width = width;
            info.Height = -height;
            info.Planes = 1;
            info.BitCount = 32;
            IntPtr bits;
            _hbitmap = CreateDIBSection(IntPtr.Zero, ref info, 0, out bits, IntPtr.Zero, 0);
            if (_hbitmap == IntPtr.Zero || bits == IntPtr.Zero)
                throw new OutOfMemoryException("CreateDIBSection " + width + "x" + height + ": " + Marshal.GetLastWin32Error());
            Bits = bits;
            Dc = CreateCompatibleDC(IntPtr.Zero);
            _old = SelectObject(Dc, _hbitmap);
            Image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        // Левый верхний угол буфера — в точку clip на чужом DC (окна).
        public void CopyTo(IntPtr hdc, Rectangle clip)
        {
            CapNative.BitBlt(hdc, clip.X, clip.Y, clip.Width, clip.Height, Dc, 0, 0, CapNative.SRCCOPY);
        }

        // Самостоятельная копия пикселей (32bppRgb): буфер после этого можно освобождать.
        public Bitmap ToBitmap()
        {
            Bitmap copy = new Bitmap(Width, Height, PixelFormat.Format32bppRgb);
            BitmapData data = copy.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
            try { CopyMemory(data.Scan0, Bits, new IntPtr((long)Width * 4 * Height)); }
            finally { copy.UnlockBits(data); }
            return copy;
        }

        public void Dispose()
        {
            Image.Dispose();
            SelectObject(Dc, _old);
            DeleteDC(Dc);
            CapNative.DeleteObject(_hbitmap);
        }
    }

    internal sealed class MonitorInfo
    {
        public string Device;
        public Rectangle Bounds;
        public Rectangle WorkArea;
        public bool Primary;
    }

    internal static class ScreenGrab
    {
        public static List<MonitorInfo> Monitors()
        {
            List<MonitorInfo> list = new List<MonitorInfo>();
            CapNative.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr h, IntPtr dc, ref CapNative.RECT r, IntPtr data)
            {
                CapNative.MONITORINFOEX mi = new CapNative.MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(CapNative.MONITORINFOEX));
                if (CapNative.GetMonitorInfo(h, ref mi))
                {
                    MonitorInfo m = new MonitorInfo();
                    m.Device = mi.szDevice;
                    m.Bounds = mi.rcMonitor.ToRectangle();
                    m.WorkArea = mi.rcWork.ToRectangle();
                    m.Primary = (mi.dwFlags & CapNative.MONITORINFOF_PRIMARY) != 0;
                    list.Add(m);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static Rectangle VirtualBounds(IList<MonitorInfo> monitors)
        {
            Rectangle all = Rectangle.Empty;
            foreach (MonitorInfo m in monitors) all = all.IsEmpty ? m.Bounds : Rectangle.Union(all, m.Bounds);
            return all;
        }

        public static MonitorInfo MonitorAt(IList<MonitorInfo> monitors, Point p)
        {
            MonitorInfo nearest = null;
            long best = long.MaxValue;
            foreach (MonitorInfo m in monitors)
            {
                if (m.Bounds.Contains(p)) return m;
                long dx = p.X < m.Bounds.Left ? m.Bounds.Left - p.X : p.X >= m.Bounds.Right ? p.X - m.Bounds.Right + 1 : 0;
                long dy = p.Y < m.Bounds.Top ? m.Bounds.Top - p.Y : p.Y >= m.Bounds.Bottom ? p.Y - m.Bounds.Bottom + 1 : 0;
                long d = dx * dx + dy * dy;
                if (d < best) { best = d; nearest = m; }
            }
            return nearest;
        }

        // Монитор, на который приходится большая часть прямоугольника.
        public static MonitorInfo MonitorOf(IList<MonitorInfo> monitors, Rectangle r)
        {
            MonitorInfo bestM = null;
            long best = -1;
            foreach (MonitorInfo m in monitors)
            {
                Rectangle i = Rectangle.Intersect(m.Bounds, r);
                long area = (long)i.Width * i.Height;
                if (area > best) { best = area; bestM = m; }
            }
            return best > 0 ? bestM : MonitorAt(monitors, new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
        }

        // Пиксели прямоугольника экрана. Прямоугольник обрезается по виртуальному экрану; вне мониторов — чёрное.
        public static Bitmap Grab(Rectangle area, bool withCursor)
        {
            if (area.Width <= 0 || area.Height <= 0) throw new ArgumentException("empty capture area");
            // BitBlt — в DIB-секцию, оттуда одна копия памяти в Bitmap. DC самого Bitmap (Graphics.GetHdc) GDI+ отдаёт
            // через свою промежуточную копию: на весь виртуальный экран 23 Мп это ~150 мс против ~100 мс так.
            using (DibBuffer dib = new DibBuffer(area.Width, area.Height))
            {
                IntPtr src = CapNative.GetDC(IntPtr.Zero);
                try
                {
                    // CAPTUREBLT — вместе со слоистыми окнами (всплывающие меню, подсказки).
                    if (!CapNative.BitBlt(dib.Dc, 0, 0, area.Width, area.Height, src, area.X, area.Y, CapNative.SRCCOPY | CapNative.CAPTUREBLT))
                        throw new InvalidOperationException("BitBlt failed: " + Marshal.GetLastWin32Error());
                    if (withCursor) DrawCursor(dib.Dc, area.Location);
                }
                finally
                {
                    CapNative.ReleaseDC(IntPtr.Zero, src);
                }
                return dib.ToBitmap();
            }
        }

        private static void DrawCursor(IntPtr hdc, Point origin)
        {
            CapNative.CURSORINFO ci = new CapNative.CURSORINFO();
            ci.cbSize = Marshal.SizeOf(typeof(CapNative.CURSORINFO));
            if (!CapNative.GetCursorInfo(ref ci) || (ci.flags & CapNative.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
            CapNative.ICONINFO ii;
            int hx = 0, hy = 0;
            if (CapNative.GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot;
                hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) CapNative.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) CapNative.DeleteObject(ii.hbmColor);
            }
            CapNative.DrawIconEx(hdc, ci.ptScreenPos.X - hx - origin.X, ci.ptScreenPos.Y - hy - origin.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, CapNative.DI_NORMAL);
        }

        // Кусок уже снятого кадра (кадр виртуального экрана начинается в origin).
        public static Bitmap Crop(Bitmap frame, Point origin, Rectangle area)
        {
            Rectangle local = new Rectangle(area.X - origin.X, area.Y - origin.Y, area.Width, area.Height);
            Bitmap result = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(Color.Black);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.DrawImage(frame, new Rectangle(0, 0, area.Width, area.Height), local, GraphicsUnit.Pixel);
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ //
    //  Окна верхнего уровня под курсором (подсветка при выделении) и имя программы
    // ------------------------------------------------------------------ //
    internal sealed class WindowCandidate
    {
        public IntPtr Handle;
        public Rectangle Bounds;
        public int Pid;
        public bool IsDesktop;
    }

    internal static class WindowPicker
    {
        // Видимые окна сверху вниз по Z-порядку. Свои окна (оверлей захвата) пропускаются по pid.
        public static List<WindowCandidate> TopLevel(int skipPid)
        {
            List<WindowCandidate> list = new List<WindowCandidate>();
            CapNative.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                try
                {
                    if (!CapNative.IsWindowVisible(h) || CapNative.IsIconic(h) || CapNative.IsCloaked(h)) return true;
                    int pid;
                    CapNative.GetWindowThreadProcessId(h, out pid);
                    if (pid == skipPid) return true;
                    string cls = CapNative.ClassOf(h);
                    bool desktop = cls == "Progman" || cls == "WorkerW";
                    Rectangle b = CapNative.VisualBounds(h);
                    if (b.Width < 8 || b.Height < 8) return true;
                    long ex = CapNative.GetWindowLongPtr(h, CapNative.GWL_EXSTYLE).ToInt64();
                    // Прозрачные для мыши слои (чужие оверлеи, подсветки) — не то, что пользователь хочет снять.
                    if ((ex & CapNative.WS_EX_TRANSPARENT) != 0 && (ex & CapNative.WS_EX_LAYERED) != 0) return true;
                    WindowCandidate c = new WindowCandidate();
                    c.Handle = h;
                    c.Bounds = b;
                    c.Pid = pid;
                    c.IsDesktop = desktop;
                    list.Add(c);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static WindowCandidate At(IList<WindowCandidate> windows, Point p)
        {
            foreach (WindowCandidate w in windows)
                if (!w.IsDesktop && w.Bounds.Contains(p)) return w;
            return null;
        }
    }

    internal static class AppNaming
    {
        public static string Desktop { get { return Tr.S("Рабочий стол", "Desktop"); } }

        // Описание файла программы («Google Chrome»), иначе имя процесса; рабочий стол и панель задач — «Рабочий стол».
        public static string ForWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Desktop;
            try
            {
                IntPtr root = CapNative.GetAncestor(hwnd, CapNative.GA_ROOT);
                if (root != IntPtr.Zero) hwnd = root;
                string cls = CapNative.ClassOf(hwnd);
                if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return Desktop;
                int pid;
                CapNative.GetWindowThreadProcessId(hwnd, out pid);
                return ForProcess(pid);
            }
            catch { return Desktop; }
        }

        public static string ForProcess(int pid)
        {
            if (pid <= 0) return Desktop;
            string path = CapNative.ImagePathOf(pid);
            if (!string.IsNullOrEmpty(path))
            {
                try
                {
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(path);
                    string d = info.FileDescription == null ? "" : info.FileDescription.Trim();
                    if (d.Length > 0 && d.Length <= 60) return d;
                }
                catch { }
                return Path.GetFileNameWithoutExtension(path);
            }
            try { using (Process p = Process.GetProcessById(pid)) return p.ProcessName; }
            catch { return Desktop; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Сохранение, буфер обмена, звук затвора
    // ------------------------------------------------------------------ //
    internal static class ImageStore
    {
        // Сначала во временный файл рядом, потом перенос: галерея и Проводник не увидят половину картинки.
        public static void Save(Image image, string path, string format, int jpegQuality)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = path + ".part";
            try
            {
                if (format == "jpg")
                {
                    ImageCodecInfo codec = Encoder("image/jpeg");
                    using (EncoderParameters ps = new EncoderParameters(1))
                    {
                        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(1, Math.Min(100, jpegQuality)));
                        image.Save(tmp, codec, ps);
                    }
                }
                else image.Save(tmp, ImageFormat.Png);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { File.Delete(tmp); } catch { }
                throw;
            }
        }

        private static ImageCodecInfo Encoder(string mime)
        {
            foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders())
                if (c.MimeType == mime) return c;
            throw new NotSupportedException(mime);
        }

        // Только из STA-потока интерфейса. Кроме битмапа кладём и PNG — его без потерь берут браузеры и мессенджеры.
        public static void CopyImage(Image image)
        {
            DataObject data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, image);
            using (MemoryStream png = new MemoryStream())
            {
                image.Save(png, ImageFormat.Png);
                data.SetData("PNG", false, new MemoryStream(png.ToArray()));
            }
            Clipboard.SetDataObject(data, true, 5, 60);
        }

        // Картинка из файла без блокировки файла: его можно удалить или перезаписать, пока картинка открыта.
        public static Bitmap LoadUnlocked(string path)
        {
            using (MemoryStream ms = new MemoryStream(File.ReadAllBytes(path)))
            using (Image img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        public static bool IsImage(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp" || ext == ".jxr" || ext == ".tif" || ext == ".tiff";
        }

        public static bool IsVideo(string path)
        {
            string ext = (Path.GetExtension(path) ?? "").ToLowerInvariant();
            return ext == ".mp4" || ext == ".mov" || ext == ".mkv" || ext == ".webm" || ext == ".avi" || ext == ".wmv" || ext == ".m4v";
        }
    }

    internal static class Shutter
    {
        private static byte[] _wave;

        // Короткий щелчок синтезируется в памяти: файла со звуком в exe нет и в системе он не ищется.
        public static void Play()
        {
            try
            {
                if (_wave == null) _wave = BuildClick();
                SoundPlayer player = new SoundPlayer(new MemoryStream(_wave));
                player.Play();
            }
            catch { }
        }

        internal static byte[] BuildClick()
        {
            const int rate = 22050;
            int samples = rate * 70 / 1000;
            byte[] pcm = new byte[samples * 2];
            Random rnd = new Random(7);
            for (int i = 0; i < samples; i++)
            {
                double env = Math.Exp(-i / (rate * 0.012));
                double tone = Math.Sin(2 * Math.PI * 1800 * i / rate) * 0.35;
                double noise = (rnd.NextDouble() * 2 - 1) * 0.65;
                short v = (short)(Math.Max(-1, Math.Min(1, (tone + noise) * env)) * 12000);
                pcm[i * 2] = (byte)(v & 0xFF);
                pcm[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            using (MemoryStream ms = new MemoryStream())
            using (BinaryWriter w = new BinaryWriter(ms))
            {
                w.Write(new char[] { 'R', 'I', 'F', 'F' });
                w.Write(36 + pcm.Length);
                w.Write(new char[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
                w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
                w.Write(new char[] { 'd', 'a', 't', 'a' });
                w.Write(pcm.Length);
                w.Write(pcm);
                w.Flush();
                return ms.ToArray();
            }
        }
    }
}
