// SysDeck — размеры папок: значок прогресса.
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
    // ------------------------------------------------------------------ //
    //  Значок «работаю» в углу самого окна Проводника.
    //  Без него программу не отличить от сломанной: папка, которая считается две минуты, показывает пустую
    //  ячейку «Размер», и ничто на экране не говорит, происходит ли вообще что-то. Поэтому состояние стоит
    //  там, где задают вопрос, — над Проводником, а не в подсказке трея, — и оно движется, потому что
    //  неподвижная картинка спиннера и есть вид зависшей программы.
    //  Прозрачен для мыши и не активируется: на него можно смотреть, но нельзя нажать, и он не уводит фокус.
    // ------------------------------------------------------------------ //
    internal sealed class ProgressBadge : IDisposable
    {
        private const int AnimationMs = 80;

        // Папка из кэша готова за миллисекунды; объявлять это — значок, мигающий на каждом переходе.
        // Появляется только работа, которая действительно заставляет ждать.
        private const long QuietMs = 400;

        private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        private readonly FsSettings _settings;
        private readonly ClickThroughWindow _window = new ClickThroughWindow();
        private readonly System.Windows.Forms.Timer _animation = new System.Windows.Forms.Timer();

        private FsTheme _theme = FsTheme.Dark;
        private ScanProgress _progress = ScanProgress.Idle;
        private IntPtr _target;
        private Rectangle _anchor;
        private bool _targetVisible;
        private Font _title;
        private Font _detail;
        private int _fontDpi;
        private int _phase;
        private long _busySince;
        private volatile bool _disposed;

        public ProgressBadge(FsSettings settings)
        {
            _settings = settings;
            _animation.Interval = AnimationMs;
            _animation.Tick += delegate { FsLog.Swallow(Frame); };
            // Создаётся скрытым: без дескриптора DPI неизвестен, и первый кадр был бы разложен под 96 dpi
            // на масштабированном экране.
            IntPtr unused = _window.Handle;
            GC.KeepAlive(unused);
        }

        public void SetTheme(FsTheme theme) { _theme = theme; }

        public void SetTarget(IntPtr explorerHwnd, Rectangle bounds, bool visible)
        {
            _target = explorerHwnd;
            _anchor = bounds;
            _targetVisible = visible;
            Sync();
        }

        public void ShowProgress(ScanProgress progress)
        {
            if (progress.IsBusy && !_progress.IsBusy) _busySince = FsMath.NowMs;
            _progress = progress;
            Sync();
        }

        public void SetEnabled() { Sync(); }

        private bool ShouldRun
        {
            get { return !_disposed && _settings.ShowScanBadge && _targetVisible && _target != IntPtr.Zero && !_anchor.IsEmpty && _progress.IsBusy; }
        }

        private bool ShouldPaint { get { return ShouldRun && FsMath.NowMs - _busySince >= QuietMs; } }

        private void Sync()
        {
            if (ShouldRun)
            {
                if (!_animation.Enabled) _animation.Start();
                Frame();
            }
            else
            {
                if (_animation.Enabled) _animation.Stop();
                Hide();
            }
        }

        private void Frame()
        {
            if (!ShouldRun) { Sync(); return; }
            if (!ShouldPaint) return;              // ещё внутри тихого окна; таймер продолжает ждать
            _phase = (_phase + 1) % 360;

            string detail;
            string title = Caption(_progress, out detail);
            Size size;
            using (Bitmap bitmap = Draw(title, detail, out size))
            {
                Point location = Place(size);
                // Закрыт другим окном? Тогда здесь нечего подписывать из Проводника, а значок над чужой
                // программой — ровно та навязчивость «поверх всех», которой надо избегать.
                if (!Win32.IsShowingAt(_target, new Point(location.X + size.Width / 2, location.Y + size.Height / 2)))
                {
                    Hide();
                    return;
                }
                if (!_window.Visible) _window.Show();
                LayeredWindow.Paint(_window.Handle, bitmap, location);
                _window.KeepOnTop();
            }
        }

        internal static string Caption(ScanProgress progress, out string detail)
        {
            detail = null;
            switch (progress.Phase)
            {
                case ScanPhase.Listing:
                    return Tr.S("Читаю содержимое папки…", "Reading the folder…");
                case ScanPhase.IndexingVolume:
                    if (progress.Percent > 0) detail = progress.Percent + "%";
                    return Tr.S("Индексирую диск ", "Indexing drive ") + progress.Detail + "…";
                case ScanPhase.MeasuringFolders:
                    detail = Counted(progress);
                    return Tr.S("Считаю размеры папок…", "Measuring folder sizes…");
                default:
                    return Tr.S("Работаю…", "Working…");
            }
        }

        private static string Counted(ScanProgress progress)
        {
            string done = progress.Detail;
            int slash = string.IsNullOrEmpty(done) ? -1 : done.IndexOf('/');
            string folders = slash > 0
                ? Tr.S("готово " + done.Substring(0, slash) + " из " + done.Substring(slash + 1), done.Substring(0, slash) + " of " + done.Substring(slash + 1) + " done")
                : "";
            string items = progress.ItemsSeen > 0
                ? Tr.S("просмотрено " + SizeFormat.Count(progress.ItemsSeen) + " объектов", SizeFormat.Count(progress.ItemsSeen) + " items scanned")
                : "";
            if (folders.Length == 0 && items.Length == 0) return null;
            if (folders.Length == 0) return items;
            if (items.Length == 0) return folders;
            return folders + " · " + items;
        }

        private static Size Measure(string text, Font font)
        {
            return TextRenderer.MeasureText(text, font, new Size(int.MaxValue, int.MaxValue), TextFlags);
        }

        private Bitmap Draw(string title, string detail, out Size size)
        {
            EnsureFonts();
            float scale = _fontDpi / 96f;
            int pad = (int)(12 * scale);
            int spinner = (int)(16 * scale);
            int gap = (int)(10 * scale);
            int lineGap = (int)(3 * scale);

            Size titleSize = Measure(title, _title);
            Size detailSize = detail == null ? Size.Empty : Measure(detail, _detail);
            // Пиксель запаса: NoPadding меряет чернила, а хвост буквы или широкая запятая может выйти за них на
            // полпикселя — обрезанный текст единственный артефакт, которого не прощают.
            int textWidth = Math.Max(titleSize.Width, detailSize.Width) + (int)(2 * scale);
            int textHeight = titleSize.Height + (detail == null ? 0 : detailSize.Height + lineGap);
            size = new Size(pad + spinner + gap + textWidth + pad, Math.Max(spinner + pad * 2, textHeight + pad * 2));

            Bitmap bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            List<GdiTextLayer.Cell> cells = new List<GdiTextLayer.Cell>(2);
            // Тот же приём двух поверхностей, что в колонке «Размер»: GDI не пишет альфу, поэтому рисует белым
            // по чёрному, и эта яркость становится покрытием, с которым собирается значок.
            using (Bitmap mask = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                using (Graphics glyphs = Graphics.FromImage(mask))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    glyphs.Clear(Color.Black);

                    int radius = (int)(8 * scale);
                    Rectangle body = new Rectangle(0, 0, size.Width - 1, size.Height - 1);
                    using (GraphicsPath path = Rounded(body, radius))
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(238, _theme.Surface)))
                    using (Pen edge = new Pen(Color.FromArgb(220, _theme.Border), scale))
                    {
                        g.FillPath(fill, path);
                        g.DrawPath(edge, path);
                    }

                    DrawSpinner(g, new Rectangle(pad, (size.Height - spinner) / 2, spinner, spinner), scale);

                    int textLeft = pad + spinner + gap;
                    int top = (size.Height - textHeight) / 2;
                    Rectangle titleBand = new Rectangle(textLeft, top, textWidth, titleSize.Height);
                    TextRenderer.DrawText(glyphs, title, _title, titleBand, Color.White, TextFlags);
                    cells.Add(new GdiTextLayer.Cell(titleBand, _theme.Text));
                    if (detail != null)
                    {
                        Rectangle detailBand = new Rectangle(textLeft, top + titleSize.Height + lineGap, textWidth, detailSize.Height);
                        TextRenderer.DrawText(glyphs, detail, _detail, detailBand, Color.White, TextFlags);
                        cells.Add(new GdiTextLayer.Cell(detailBand, _theme.Muted));
                    }
                }
                GdiTextLayer.Compose(bitmap, mask, cells);
            }
            return bitmap;
        }

        // Бегущая дуга: итог действительно неизвестен, пока обходятся папки, а процент наугад был бы враньём,
        // которое шапка панели уже отказывается произносить.
        private void DrawSpinner(Graphics g, Rectangle box, float scale)
        {
            float thickness = Math.Max(1.6f, 2f * scale);
            Rectangle inner = Rectangle.Inflate(box, -(int)thickness, -(int)thickness);
            using (Pen track = new Pen(Color.FromArgb(60, _theme.Accent), thickness))
                g.DrawEllipse(track, inner);
            using (Pen pen = new Pen(_theme.Accent, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, inner, _phase * 12 % 360, 100);
            }
        }

        // Внизу справа внутри окна Проводника, выше его строки состояния, никогда за краем экрана.
        private Point Place(Size size)
        {
            float scale = _fontDpi / 96f;
            int margin = (int)(20 * scale);
            int statusBar = (int)(44 * scale);
            int x = _anchor.Right - size.Width - margin;
            int y = _anchor.Bottom - size.Height - statusBar;
            Rectangle work = Screen.FromRectangle(_anchor).WorkingArea;
            x = FsMath.Clamp(x, Math.Min(work.Left, _anchor.Left), Math.Max(work.Left, work.Right - size.Width));
            y = FsMath.Clamp(y, Math.Min(work.Top, _anchor.Top), Math.Max(work.Top, work.Bottom - size.Height));
            return new Point(x, y);
        }

        private void EnsureFonts()
        {
            // DPI монитора Проводника: значок стоит в его углу. Своё окно ещё может помнить прошлый монитор.
            int dpi = Win32.GetDpiForWindow(_target);
            if (dpi <= 0 && _window.IsHandleCreated) dpi = Win32.GetDpiForWindow(_window.Handle);
            if (dpi <= 0) dpi = 96;
            if (_title != null && dpi == _fontDpi) return;
            if (_title != null) _title.Dispose();
            if (_detail != null) _detail.Dispose();
            _fontDpi = dpi;
            // Шрифт оболочки Windows, а не зашитый «Segoe UI 13px»: семейство, кегль и хинтинг — от системы, и
            // значок читается частью оболочки. Заголовок на ступень крупнее и полужирный — обычная строка на
            // полупрозрачной карточке такого размера выглядела блёклой.
            _title = ShellFont.At(dpi, 1f, FontStyle.Bold);
            _detail = ShellFont.At(dpi, 0f, FontStyle.Regular);
        }

        private static GraphicsPath Rounded(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void Hide()
        {
            if (_window.IsHandleCreated && _window.Visible) _window.Hide();
        }

        public void Dispose()
        {
            _disposed = true;
            _animation.Stop();
            _animation.Dispose();
            if (_title != null) _title.Dispose();
            if (_detail != null) _detail.Dispose();
            _window.Dispose();
        }
    }
}
