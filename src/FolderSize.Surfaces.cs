// SysDeck — «Размеры папок»: поверхности поверх Проводника — числа в колонке «Размер» и значок подсчёта.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.FolderSize
{
    // Плоская двухтоновая палитра, которая стоит рядом с Проводником и не кричит на него.
    internal sealed class FsTheme
    {
        public Color Background, Surface, Border, Text, Muted, Accent, BarFolder, BarFile, RowHover, Scrollbar;
        public bool IsDark;

        public static readonly FsTheme Dark = new FsTheme
        {
            IsDark = true,
            Background = Color.FromArgb(32, 32, 32),
            Surface = Color.FromArgb(43, 43, 43),
            Border = Color.FromArgb(58, 58, 58),
            Text = Color.FromArgb(240, 240, 240),
            Muted = Color.FromArgb(155, 155, 155),
            Accent = Color.FromArgb(76, 156, 240),
            BarFolder = Color.FromArgb(48, 76, 156, 240),
            BarFile = Color.FromArgb(38, 150, 150, 150),
            RowHover = Color.FromArgb(56, 56, 56),
            Scrollbar = Color.FromArgb(90, 90, 90),
        };

        public static readonly FsTheme Light = new FsTheme
        {
            IsDark = false,
            Background = Color.FromArgb(249, 249, 249),
            Surface = Color.White,
            Border = Color.FromArgb(226, 226, 226),
            Text = Color.FromArgb(28, 28, 28),
            Muted = Color.FromArgb(112, 112, 112),
            Accent = Color.FromArgb(0, 95, 184),
            BarFolder = Color.FromArgb(40, 0, 120, 212),
            BarFile = Color.FromArgb(34, 120, 120, 120),
            RowHover = Color.FromArgb(240, 240, 240),
            Scrollbar = Color.FromArgb(170, 170, 170),
        };

        public static FsTheme For(bool dark) { return dark ? Dark : Light; }
    }

    // ------------------------------------------------------------------ //
    //  Поверхность, которая рисуется поверх Проводника и больше ничем не владеет: попиксельная альфа,
    //  прозрачна для мыши, не активируется, не видна в Alt+Tab и на панели задач.
    //  Правило z-порядка живёт здесь, потому что ошибка в нём не громкая, а невидимая. Фоновому процессу
    //  Windows не даёт поставить окно выше окна АКТИВНОЙ программы — HWND_TOP принимается и игнорируется,
    //  и поверхность безупречно рисуется в пиксели, которых никто не видит. Отвечает только слой topmost —
    //  он и используется, и отдаётся в тот же миг, как окно прячется: ничто наше не должно стоять выше
    //  программы, на которую пользователь действительно смотрит.
    // ------------------------------------------------------------------ //
    internal class ClickThroughWindow : Form
    {
        public ClickThroughWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = false;                       // ставится через SetWindowPos — см. KeepOnTop
            Bounds = new Rectangle(0, 0, 1, 1);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_LAYERED | Win32.WS_EX_TRANSPARENT | Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        // Повторяется на каждом обновлении, а не только при смене картинки: Проводник поднимает себя на каждый
        // щелчок, «Назад» тоже, а закопанная поверхность не меняет на экране ничего, что вызвало бы
        // перерисовку. Это и было «числа появляются и исчезают насовсем».
        public void KeepOnTop()
        {
            if (!IsHandleCreated || !Visible) return;
            Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
        }

        protected override void SetVisibleCore(bool value)
        {
            if (!value && IsHandleCreated)
            {
                Win32.SetWindowPos(Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            }
            base.SetVisibleCore(value);
        }
    }

    // Окно с попиксельной альфой. Окно с цветовым ключом дало бы кайму у каждого сглаженного глифа;
    // UpdateLayeredWindow с 32-битной ARGB-поверхностью кладёт чёткий текст на что угодно под ним, а полностью
    // прозрачные пиксели оставляет прозрачными для мыши.
    internal static class LayeredWindow
    {
        private const byte AC_SRC_OVER = 0x00;
        private const byte AC_SRC_ALPHA = 0x01;
        private const int ULW_ALPHA = 0x02;

        public static bool Paint(IntPtr hwnd, Bitmap bitmap, Point topLeft)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Win32.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero;
            IntPtr previous = IntPtr.Zero;
            try
            {
                hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));     // 32 бит с нетронутым альфа-каналом
                previous = Win32.SelectObject(memoryDc, hBitmap);
                POINT source = new POINT(0, 0);
                POINT destination = new POINT(topLeft.X, topLeft.Y);
                SIZE size = new SIZE(bitmap.Width, bitmap.Height);
                BLENDFUNCTION blend = new BLENDFUNCTION();
                blend.BlendOp = AC_SRC_OVER;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = AC_SRC_ALPHA;
                return Win32.UpdateLayeredWindow(hwnd, screenDc, ref destination, ref size, memoryDc, ref source, 0, ref blend, ULW_ALPHA);
            }
            finally
            {
                if (previous != IntPtr.Zero) Win32.SelectObject(memoryDc, previous);
                if (hBitmap != IntPtr.Zero) Win32.DeleteObject(hBitmap);
                Win32.DeleteDC(memoryDc);
                Win32.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        // Пиксель прямо с экрана — чтобы совпасть с фоном строки самого Проводника.
        public static Color SampleScreen(int x, int y)
        {
            IntPtr dc = Win32.GetDC(IntPtr.Zero);
            try
            {
                int value = Win32.GetPixel(dc, x, y);
                if (value == -1) return Color.Empty;
                return Color.FromArgb(value & 0xFF, (value >> 8) & 0xFF, (value >> 16) & 0xFF);
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, dc);
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Шрифт, которым сама Windows подписывает элементы оболочки, в размере, который просит этот монитор.
    //  Два источника, и ни одному нельзя верить в одиночку. Системный шрифт — правильный ответ (то же
    //  семейство и кегль, что у Проводника), но его пиксельная высота переведена в пункты не для этого
    //  монитора и на масштабированном рабочем столе приходит уже раздутой. Высота строки о масштабе честна
    //  всегда, а о размере — лишь приблизительно. Поэтому системный размер берётся, когда он в пределах 35 %
    //  от того, что вмещает строка, а иначе решает строка.
    // ------------------------------------------------------------------ //
    internal static class ShellFont
    {
        private static string _family;
        private static float _points;

        private static void EnsureRead()
        {
            if (_family != null) return;
            try
            {
                Font font = SystemFonts.IconTitleFont ?? SystemFonts.DefaultFont;
                using (font)
                {
                    _points = font.SizeInPoints;
                    _family = font.Name;
                }
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
                _points = 9f;
                _family = "Segoe UI";
            }
        }

        public static Font For(IntPtr window, int cellHeight)
        {
            EnsureRead();
            float pixels = PixelsFor(_points, window == IntPtr.Zero ? 0 : Win32.GetDpiForWindow(window), cellHeight);
            return new Font(_family, pixels, FontStyle.Regular, GraphicsUnit.Pixel);
        }

        internal static float PixelsFor(float systemPoints, int dpi, int cellHeight)
        {
            float rowBased = Math.Max(8f, cellHeight * 0.62f);
            float systemBased = dpi == 0 ? 0 : systemPoints * dpi / 72f;
            return systemBased > 0 && Math.Abs(systemBased - rowBased) <= rowBased * 0.35f ? systemBased : rowBased;
        }

        // Шрифт оболочки для своей поверхности — значка, подписи — в её масштабе. pointsOffset отступает от
        // системного кегля, когда строка — заголовок.
        public static Font At(int dpi, float pointsOffset, FontStyle style)
        {
            EnsureRead();
            float points = Math.Max(7f, _points + pointsOffset);
            return new Font(_family, points * Math.Max(96, dpi) / 72f, style, GraphicsUnit.Pixel);
        }
    }

    // ------------------------------------------------------------------ //
    //  Текст на слоистой поверхности, растеризованный GDI — тем же движком, которым Проводник рисует свои
    //  числа. Graphics.DrawString здесь заметно неправ: у GDI+ свой растеризатор, тот же шрифт выходит тоньше
    //  и мягче, чем число Проводника двумя пикселями левее, а в колонке, вся цель которой — выглядеть так,
    //  будто она всегда там была, эта разница и есть ошибка.
    //  Прямо GDI тоже нельзя: он пишет цвет и не трогает альфу, так что его глифы для UpdateLayeredWindow
    //  полностью прозрачны — идеально нарисованы и невидимы. Выход: GDI рисует белым по чёрному, и это
    //  читается обратно как покрытие — какая доля пикселя под глифом и ЕСТЬ яркость. ClearType по каналам
    //  не выживает (у слоистого окна одна альфа на пиксель), каналы складываются в один — выходит
    //  градационное сглаживание с хинтингом и толщиной штриха GDI.
    // ------------------------------------------------------------------ //
    internal static class GdiTextLayer
    {
        // Как GDI раскладывает текст колонки оболочки.
        public const TextFormatFlags CellFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        internal struct Cell
        {
            public Rectangle Band;
            public Color Color;
            public Cell(Rectangle band, Color color) { Band = band; Color = color; }
        }

        // Складывает глифы, нарисованные в mask, на target — каждую полосу своим цветом. Полосы не
        // перекрываются: по одной на строку, как и положено колонке.
        public static void Compose(Bitmap target, Bitmap mask, IList<Cell> cells)
        {
            Rectangle full = new Rectangle(0, 0, target.Width, target.Height);
            BitmapData targetData = target.LockBits(full, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            BitmapData maskData = mask.LockBits(full, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = targetData.Stride;
                byte[] t = new byte[stride * target.Height];
                byte[] m = new byte[maskData.Stride * mask.Height];
                Marshal.Copy(targetData.Scan0, t, 0, t.Length);
                Marshal.Copy(maskData.Scan0, m, 0, m.Length);
                ComposeBytes(t, stride, m, maskData.Stride, target.Width, target.Height, cells);
                Marshal.Copy(t, 0, targetData.Scan0, t.Length);
            }
            finally
            {
                mask.UnlockBits(maskData);
                target.UnlockBits(targetData);
            }
        }

        internal static void ComposeBytes(byte[] t, int tStride, byte[] m, int mStride, int width, int height, IList<Cell> cells)
        {
            foreach (Cell cell in cells)
            {
                int top = Math.Max(0, cell.Band.Top);
                int bottom = Math.Min(height, cell.Band.Bottom);
                int left = Math.Max(0, cell.Band.Left);
                int right = Math.Min(width, cell.Band.Right);
                for (int y = top; y < bottom; y++)
                {
                    int maskRow = y * mStride;
                    int targetRow = y * tStride;
                    for (int x = left; x < right; x++)
                    {
                        int mi = maskRow + x * 4;
                        int coverage = Math.Max(m[mi], Math.Max(m[mi + 1], m[mi + 2]));
                        if (coverage == 0) continue;
                        int ti = targetRow + x * 4;
                        int under = t[ti + 3];
                        int alpha = coverage + under * (255 - coverage) / 255;
                        if (alpha <= 0) continue;
                        t[ti] = Over(cell.Color.B, t[ti], coverage, under, alpha);
                        t[ti + 1] = Over(cell.Color.G, t[ti + 1], coverage, under, alpha);
                        t[ti + 2] = Over(cell.Color.R, t[ti + 2], coverage, under, alpha);
                        t[ti + 3] = (byte)alpha;
                    }
                }
            }
        }

        // «Источник поверх приёмника» в непредумноженной альфе — в том виде, в каком её уже хранят поверхности GDI+.
        private static byte Over(byte source, byte under, int coverage, int underAlpha, int alpha)
        {
            return (byte)FsMath.Clamp((source * coverage + under * underAlpha * (255 - coverage) / 255) / alpha, 0, 255);
        }
    }

    // ------------------------------------------------------------------ //
    //  Размеры папок — прямо в собственную колонку «Размер» Проводника.
    //  У папок Проводник эту ячейку оставляет пустой: закрывать нечего, и числа просто появляются там, где их
    //  уже ищут. Поверхность — слоистое окно, прозрачное для мыши: оно не владеет вводом, ничего не меняет
    //  внутри Проводника и исчезает вместе с ним.
    // ------------------------------------------------------------------ //
    internal sealed class InlineSizeOverlay : IDisposable
    {
        private const int IdlePollMs = 150;          // как часто перечитывать список, когда ничего не движется
        private const int MovingPollMs = 15;         // а когда движется — так быстро, как позволяет чтение: числа едут со строками
        private const int MissesBeforeHide = 4;      // пустых чтений до того, как числа снимаются с экрана

        private readonly FsSettings _settings;
        private readonly ExplorerListReader _reader = new ExplorerListReader();
        private readonly ClickThroughWindow _window = new ClickThroughWindow();
        private readonly System.Threading.Timer _timer;
        private readonly SynchronizationContext _ui;

        private Dictionary<string, SizeRow> _sizes = new Dictionary<string, SizeRow>(StringComparer.OrdinalIgnoreCase);
        private FsTheme _theme = FsTheme.Dark;
        private Font _font;
        private int _fontForHeight;
        private IntPtr _target;
        private bool _targetVisible;
        private string _snapshotPath;
        private int _reading;
        private int _lastSignature;
        private int _geometry;
        private int _misses;
        private volatile bool _disposed;

        public InlineSizeOverlay(FsSettings settings)
        {
            _settings = settings;
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            _timer = new System.Threading.Timer(delegate { Poll(); }, null, Timeout.Infinite, Timeout.Infinite);
        }

        public void SetTheme(FsTheme theme)
        {
            _theme = theme;
            _lastSignature = 0;
        }

        public void SetSnapshot(ListingSnapshot snapshot)
        {
            Dictionary<string, SizeRow> map = new Dictionary<string, SizeRow>(snapshot.Rows.Count, StringComparer.OrdinalIgnoreCase);
            foreach (SizeRow row in snapshot.Rows)
            {
                map[row.Name] = row;
                // Проводник пишет «Пользователи», где на диске Users. Сопоставление только по имени на диске
                // оставляло без числа все папки профиля — Документы, Загрузки, Рабочий стол, — ровно там, куда
                // смотрят первым делом.
                if (!string.IsNullOrEmpty(row.DisplayName)) map[row.DisplayName] = row;
            }
            _sizes = map;
            _lastSignature = 0;
            // Числа прошлой папки не должны стоять рядом со строками новой: переход очищает экран сразу,
            // не дожидаясь следующего удачного чтения.
            if (!string.Equals(_snapshotPath, snapshot.Path, StringComparison.OrdinalIgnoreCase))
            {
                _snapshotPath = snapshot.Path;
                HideWindow();
            }
        }

        public void SetTarget(IntPtr explorerHwnd, bool visible)
        {
            if (explorerHwnd != _target) HideWindow();
            _target = explorerHwnd;
            _targetVisible = visible;
            Reschedule();
        }

        public void SetEnabled(bool enabled)
        {
            if (!enabled) HideWindow();
            Reschedule();
        }

        private bool Active { get { return !_disposed && _settings.InlineOverlay && _targetVisible && _target != IntPtr.Zero; } }

        private void Reschedule()
        {
            if (_disposed) return;
            if (Active) Arm(0);
            else
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);
                HideWindow();
            }
        }

        // По одному выстрелу, перезаряжается после каждого чтения. Постоянный период либо отставал бы от
        // прокручиваемого списка, либо зря перечитывал Проводник, пока ничего не движется.
        private void Arm(int delayMs)
        {
            if (_disposed) return;
            try
            {
                if (!Active) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
                _timer.Change(delayMs, Timeout.Infinite);
            }
            catch (ObjectDisposedException) { }
        }

        // На потоке пула: вызовы UI Automation уходят в explorer.exe и не должны держать интерфейс.
        private void Poll()
        {
            if (_disposed || Interlocked.Exchange(ref _reading, 1) == 1) return;
            int next = IdlePollMs;
            try
            {
                IntPtr hwnd = _target;
                if (!Active) return;
                Stopwatch clock = Stopwatch.StartNew();
                ExplorerListLayout layout = _reader.Read(hwnd);
                // Строки сдвинулись с прошлого чтения — пользователь прокручивает, и число, пришедшее на 150 мс
                // позже, стоит рядом не с той строкой. Читать снова сразу.
                int geometry = GeometryOf(layout);
                bool moving = layout != null && geometry != _geometry;
                _geometry = geometry;
                next = moving ? MovingPollMs : IdlePollMs;
                if (FsLog.TraceEnabled)
                {
                    FsLog.Trace(layout == null
                        ? "overlay: no layout from hwnd=" + hwnd + " in " + clock.ElapsedMilliseconds + " ms"
                        : "overlay: rows=" + layout.Rows.Count + " sizeCol=" + layout.SizeColumn + " view=" + layout.ItemsView
                          + " moving=" + moving + " read=" + clock.ElapsedMilliseconds + " ms");
                }
                _ui.Post(delegate { FsLog.Swallow(delegate { Render(layout); }); }, null);
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
                // Dispose пришёл посреди чтения и не стал освобождать UIA под ним — освобождаем здесь.
                if (_disposed) ReleaseReaderOnce();
                else Arm(next);
            }
        }

        // Ровно один из двоих — Dispose или завершившийся опрос — забирает _reading и освобождает читатель.
        private void ReleaseReaderOnce()
        {
            if (Interlocked.CompareExchange(ref _reading, 1, 0) == 0) _reader.Dispose();
        }

        // Дешёвый отпечаток «сдвинулось ли что-то»: какие строки и где каждая сейчас стоит.
        private static int GeometryOf(ExplorerListLayout layout)
        {
            if (layout == null) return 0;
            int hash = 17;
            hash = FsMath.Mix(hash, layout.SizeColumn);
            hash = FsMath.Mix(hash, layout.ItemsView);
            foreach (ExplorerRow row in layout.Rows)
            {
                hash = FsMath.Mix(hash, row.Name);
                hash = FsMath.Mix(hash, row.SizeCell.Y);
            }
            return hash == 0 ? 1 : hash;
        }

        private void Render(ExplorerListLayout layout)
        {
            if (_disposed) return;
            if (!_settings.InlineOverlay || !_targetVisible)
            {
                HideWindow();
                return;
            }
            if (layout == null)
            {
                Miss("no layout");
                return;
            }
            List<Painted> painted = Collect(layout);
            if (painted.Count == 0)
            {
                Miss("nothing to paint — layout rows=" + layout.Rows.Count + ", known sizes=" + _sizes.Count
                     + ", first row='" + (layout.Rows.Count > 0 ? layout.Rows[0].Name : "<none>") + "'");
                return;
            }
            _misses = 0;

            int signature = Signature(layout, painted);
            if (signature == _lastSignature && _window.Visible)
            {
                // Перерисовывать нечего — но числа всё равно надо вернуть наверх. Проводник поднимает себя на
                // каждый щелчок, «Назад» тоже, и закопанный слой не меняет на экране ничего, что вызвало бы
                // перерисовку. Поэтому размеры появлялись, пока папка считалась, и исчезали навсегда, как
                // только подсчёт успокаивался.
                _window.KeepOnTop();
                return;
            }
            _lastSignature = signature;

            Rectangle bounds = SurfaceBounds(layout, painted);
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                Miss("empty surface " + bounds + " for " + painted.Count + " cells");
                return;
            }
            FsLog.Trace("overlay: painting " + painted.Count + " cells at " + bounds);

            // Фон строки Проводника снимается ДО того, как слой что-то закроет, и вне нашей полосы — чтобы
            // никогда не прочитать собственные пиксели.
            Color[] backgrounds = _settings.OverlayFileSizes ? SampleBackgrounds(painted, layout) : null;

            using (Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            using (Bitmap mask = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                List<GdiTextLayer.Cell> cells = new List<GdiTextLayer.Cell>(painted.Count);
                using (Graphics g = Graphics.FromImage(bitmap))
                using (Graphics glyphs = Graphics.FromImage(mask))
                {
                    g.Clear(Color.Transparent);
                    glyphs.Clear(Color.Black);           // поверхность, против которой читается покрытие GDI
                    Font font = FontFor(painted[0].Row.SizeCell.Height);
                    for (int i = 0; i < painted.Count; i++)
                        DrawCell(g, glyphs, font, painted[i], bounds, backgrounds == null ? Color.Empty : backgrounds[i], cells);
                }
                GdiTextLayer.Compose(bitmap, mask, cells);

                if (!_window.Visible) _window.Show();
                bool accepted = LayeredWindow.Paint(_window.Handle, bitmap, bounds.Location);
                _window.KeepOnTop();
                if (FsLog.TraceEnabled) DumpFrame(bitmap, accepted);
            }
        }

        // Чтение, вернувшееся ни с чем, — обычно список, движущийся под нами (Проводник перестраивает дерево
        // автоматизации посреди прокрутки), а не неверные числа. Гасить колонку на первом таком чтении —
        // это и было мигание и скачки размеров при прокрутке; последний удачный кадр держится несколько
        // чтений, и с экрана его снимает только настоящее исчезновение.
        private void Miss(string reason)
        {
            FsLog.Trace("overlay: " + reason + " (miss " + (_misses + 1) + "/" + MissesBeforeHide + ")");
            if (++_misses < MissesBeforeHide && _window.Visible) return;
            HideWindow();
        }

        // С трассировкой — точная поверхность, отданная окну, и приняло ли окно вызов. «Говорит, что нарисовал»
        // и «на экране есть пиксели» — разные утверждения, и какое из них не выполнилось, решает только картинка.
        private void DumpFrame(Bitmap bitmap, bool accepted)
        {
            try
            {
                Rectangle rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int opaque = 0;
                try
                {
                    byte[] bytes = new byte[data.Stride * bitmap.Height];
                    Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
                    for (int y = 0; y < bitmap.Height; y++)
                        for (int x = 0; x < bitmap.Width; x++)
                            if (bytes[y * data.Stride + x * 4 + 3] != 0) opaque++;
                }
                finally { bitmap.UnlockBits(data); }
                FsLog.Trace("overlay: UpdateLayeredWindow=" + accepted + ", non-transparent px=" + opaque
                            + ", window visible=" + _window.Visible + " bounds=" + _window.Bounds);
                bitmap.Save(Path.Combine(FsPaths.DataDir, "overlay-frame.png"), ImageFormat.Png);
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
            }
        }

        private struct Painted
        {
            public ExplorerRow Row;
            public SizeRow Size;
            public bool Overpaint;
            public Painted(ExplorerRow row, SizeRow size, bool overpaint) { Row = row; Size = size; Overpaint = overpaint; }
        }

        private List<Painted> Collect(ExplorerListLayout layout)
        {
            List<Painted> result = new List<Painted>(layout.Rows.Count);
            foreach (ExplorerRow row in layout.Rows)
            {
                SizeRow size;
                if (!_sizes.TryGetValue(row.Name, out size)) continue;
                if (!IsCellShowing(row)) continue;
                // Папки: ячейка Проводника пуста, мы её заполняем. Файлы: Проводник и так печатает точный
                // размер, так что закрывать его — по желанию и по умолчанию выключено.
                if (size.IsDirectory) result.Add(new Painted(row, size, false));
                else if (_settings.OverlayFileSizes) result.Add(new Painted(row, size, true));
            }
            return result;
        }

        // Эта ячейка Проводника — всё ещё то, что на экране в этой точке? Это и делает безопасным «держать
        // числа, пока Проводник не активен»: слой обязан быть поверх всех, чтобы вообще показаться над
        // Проводником, так что честный предел один — ничего не рисовать над тем, что не Проводник: другим
        // окном, контекстным меню, подсказкой.
        private bool IsCellShowing(ExplorerRow row)
        {
            Rectangle cell = row.SizeCell;
            if (cell.Width <= 0 || cell.Height <= 0) return false;
            return Win32.IsShowingAt(_target, new Point(cell.Right - 3, cell.Top + cell.Height / 2));
        }

        private static Rectangle SurfaceBounds(ExplorerListLayout layout, List<Painted> painted)
        {
            int top = int.MaxValue, bottom = int.MinValue;
            foreach (Painted item in painted)
            {
                top = Math.Min(top, item.Row.SizeCell.Top);
                bottom = Math.Max(bottom, item.Row.SizeCell.Bottom);
            }
            Rectangle strip = Rectangle.FromLTRB(layout.SizeColumn.Left, top, layout.SizeColumn.Right, bottom);
            // Строки у края видимой области Проводник обрезает; обрезаем так же, иначе числа залезли бы на
            // заголовок и строку состояния.
            Rectangle viewport = layout.ItemsView;
            if (!viewport.IsEmpty)
            {
                int viewportTop = viewport.Top + layout.SizeColumn.Height;    // ниже заголовка колонки
                strip = Rectangle.FromLTRB(strip.Left, Math.Max(strip.Top, viewportTop), strip.Right, Math.Min(strip.Bottom, viewport.Bottom));
            }
            return strip;
        }

        private static Color[] SampleBackgrounds(List<Painted> painted, ExplorerListLayout layout)
        {
            Color[] colors = new Color[painted.Count];
            for (int i = 0; i < painted.Count; i++)
            {
                Rectangle cell = painted[i].Row.SizeCell;
                int x = Math.Max(layout.ItemsView.Left, layout.SizeColumn.Left - 4);
                colors[i] = LayeredWindow.SampleScreen(x, cell.Top + cell.Height / 2);
            }
            return colors;
        }

        // Фон — прямо на поверхность; число — в маску глифов, потому что оно должно выйти из того же
        // растеризатора, что у Проводника (см. GdiTextLayer).
        private void DrawCell(Graphics surface, Graphics glyphs, Font font, Painted item, Rectangle bounds, Color background, List<GdiTextLayer.Cell> cells)
        {
            Rectangle cell = item.Row.SizeCell;
            Rectangle local = new Rectangle(cell.X - bounds.X, cell.Y - bounds.Y, cell.Width, cell.Height);
            if (local.Bottom <= 0 || local.Top >= bounds.Height) return;

            if (item.Overpaint && background != Color.Empty)
            {
                using (SolidBrush fill = new SolidBrush(background))
                    surface.FillRectangle(fill, new Rectangle(0, local.Y, bounds.Width, local.Height));
            }

            string text = CellText(item.Size, PendingPhase);
            // Приглушённо, пока подсчёт не кончился: число, которое ещё растёт, не должно читаться как ответ.
            bool link = item.Size.IsDirectory && item.Size.IsReparsePoint;
            bool settled = !link && (item.Size.State == RowState.Ready || item.Size.State == RowState.Partial);

            int padding = Math.Max(3, cell.Height / 3);
            Rectangle textRect = new Rectangle(local.X, local.Y, local.Width - padding, local.Height);
            TextRenderer.DrawText(glyphs, text, font, textRect, Color.White, GdiTextLayer.CellFlags);
            cells.Add(new GdiTextLayer.Cell(local, settled ? _theme.Text : _theme.Muted));
        }

        internal static string CellText(SizeRow size, int phase)
        {
            if (size.IsDirectory && size.IsReparsePoint) return "→";
            if (size.State == RowState.Pending) return Working(phase);
            return (size.State == RowState.Partial ? "≥ " : "") + SizeFormat.Short(size.Bytes);
        }

        // Строка без числа говорит об этом движением. Неподвижное «…» не отличить от колонки, которую
        // программа просто не смогла заполнить, — и именно на эту неясность и жаловались: ничто на экране
        // не говорило, считает ли оно ещё.
        private static string Working(int phase)
        {
            switch (phase)
            {
                case 0: return "·";
                case 1: return "· ·";
                default: return "· · ·";
            }
        }

        private static int PendingPhase { get { return (int)(FsMath.NowMs / 300 % 3); } }

        private Font FontFor(int cellHeight)
        {
            if (_font != null && _fontForHeight == cellHeight) return _font;
            if (_font != null) _font.Dispose();
            _font = ShellFont.For(_target, cellHeight);
            _fontForHeight = cellHeight;
            return _font;
        }

        private static int Signature(ExplorerListLayout layout, List<Painted> painted)
        {
            int hash = 17;
            hash = FsMath.Mix(hash, layout.SizeColumn);
            hash = FsMath.Mix(hash, layout.ItemsView);
            bool pending = false;
            foreach (Painted item in painted)
            {
                hash = FsMath.Mix(hash, item.Row.Name);
                hash = FsMath.Mix(hash, item.Row.SizeCell);
                hash = FsMath.Mix(hash, item.Size.Bytes);
                hash = FsMath.Mix(hash, (int)item.Size.State);
                pending |= item.Size.State == RowState.Pending;
            }
            // Маркер ожидания анимирован — его кадр тоже часть того, что на экране.
            if (pending) hash = FsMath.Mix(hash, PendingPhase);
            return hash == 0 ? 1 : hash;          // 0 зарезервирован за «перерисовать обязательно»
        }

        private void HideWindow()
        {
            _lastSignature = 0;
            _misses = 0;
            if (!_window.IsHandleCreated || !_window.Visible) return;
            // Флаг topmost отдаётся внутри Hide — см. ClickThroughWindow.
            _ui.Post(delegate { if (!_window.IsDisposed) _window.Hide(); }, null);
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
            ReleaseReaderOnce();
            if (_font != null) _font.Dispose();
            _window.Dispose();
        }
    }
}
