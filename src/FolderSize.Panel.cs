// Windows Process Cleaner — «Размеры папок»: боковая панель рядом с Проводником и её список.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace WindowsProcessCleaner.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Панель. Не активируется намеренно: щелчок по ней не должен уводить фокус из Проводника, иначе каждый
    //  щелчок делал бы окно, к которому она пристёгнута, неактивным.
    //  Масштаб — от монитора, на котором панель стоит (GetDpiForWindow), а шрифт задан в пикселях: WinForms
    //  .NET Framework сам по мониторам не масштабирует, а поток фонового режима работает в Per-Monitor V2.
    // ------------------------------------------------------------------ //
    internal sealed class PanelForm : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        private readonly FsSettings _settings;
        private readonly SizeListView _list = new SizeListView();
        private readonly System.Windows.Forms.Timer _pulse = new System.Windows.Forms.Timer();

        private FsTheme _theme = FsTheme.Dark;
        private Font _titleFont;
        private int _dpi = 96;
        private ScanProgress _progress = ScanProgress.Idle;
        private ListingSnapshot _snapshot;
        private int _pulsePhase;
        private bool _resizing;
        private int _resizeStartX;
        private int _resizeStartWidth;
        private readonly List<HeaderButton> _buttons = new List<HeaderButton>();
        private int _hoverButton = -1;
        private Rectangle _fastHint = Rectangle.Empty;
        private VolumeSpace _space;                  // свободное место тома текущей папки; null — ещё не известно
        private int _spaceQuery;                     // 1 — запрос уже идёт в пуле

        public event Action FastModeRequested;
        public event Action RefreshRequested;
        public event Action<Point> MenuRequested;
        public event Action<SizeRow> RowActivated;
        public event Action<SizeRow, Point> RowMenuRequested;
        public event Action<SizeSortMode> SortRequested;
        public event Action WidthChanged;

        private enum HeaderAction { Refresh, Menu, Hide }

        private struct HeaderButton
        {
            public HeaderAction Action;
            public Rectangle Bounds;
            public HeaderButton(HeaderAction action, Rectangle bounds) { Action = action; Bounds = bounds; }
        }

        public PanelForm(FsSettings settings)
        {
            _settings = settings;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = Tr.S("Размеры папок", "Folder sizes");
            // Намеренно НЕ поверх всех постоянно. Спутник, который выше любой программы, мешает, как только
            // перестаёшь смотреть на Проводник: закрывает то, на что переключился, и убрать его нечем. Он едет
            // чуть выше своего окна Проводника и уходит за всё, что пользователь выводит вперёд.
            TopMost = false;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            KeyPreview = true;
            ApplyDpi(96);

            _list.SortRequested += delegate(SizeSortMode mode) { Action<SizeSortMode> h = SortRequested; if (h != null) h(mode); };
            _list.RowActivated += delegate(SizeRow row) { Action<SizeRow> h = RowActivated; if (h != null) h(row); };
            _list.RowMenuRequested += delegate(SizeRow row, Point point) { Action<SizeRow, Point> h = RowMenuRequested; if (h != null) h(row, point); };
            Controls.Add(_list);

            _pulse.Interval = 60;
            _pulse.Tick += delegate
            {
                _pulsePhase = (_pulsePhase + 1) % 200;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
            };
            ApplyTheme();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        private int Px(int logical) { return (int)Math.Round(logical * _dpi / 96.0); }

        private int HeaderHeight { get { return Px(58); } }
        private int TotalsHeight { get { return Px(26); } }
        private int SpaceHeight { get { return _settings.ShowFreeSpace ? Px(22) : 0; } }
        private int FooterHeight { get { return TotalsHeight + SpaceHeight; } }
        private int GripWidth { get { return Px(5); } }
        private int Pad { get { return Px(10); } }
        private int MinWidth { get { return FsSettings.MinPanelWidth; } }
        private int MinHeight { get { return Px(120); } }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SyncDpi();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_DPICHANGED) SyncDpi();     // предложенный прямоугольник не нужен: место задаёт DockTo
        }

        private void SyncDpi()
        {
            int dpi = IsHandleCreated ? Win32.GetDpiForWindow(Handle) : 0;
            if (dpi > 0 && dpi != _dpi) ApplyDpi(dpi);
        }

        private void ApplyDpi(int dpi)
        {
            _dpi = dpi;
            // Segoe UI 9 пт — шрифт окна .NET по умолчанию, пересчитанный под этот монитор.
            Font = new Font("Segoe UI", 9f * dpi / 72f, FontStyle.Regular, GraphicsUnit.Pixel);
            _list.SetDpi(dpi);
            LayoutChildren();
        }

        // Настройки, от которых зависит раскладка панели, поменялись (меню трея или страница приложения).
        public void ApplySettings()
        {
            LayoutChildren();
            if (_settings.ShowFreeSpace && _snapshot != null) QuerySpace(_snapshot.Path, true);
        }

        public void ApplyTheme()
        {
            _theme = FsTheme.For(_settings.IsDark);
            BackColor = _theme.Background;
            _list.SetTheme(_theme);
            _list.SortMode = _settings.Sort;
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (_titleFont != null) _titleFont.Dispose();
            _titleFont = new Font(Font, FontStyle.Bold);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        private void LayoutChildren()
        {
            _list.Bounds = new Rectangle(0, HeaderHeight, Width, Math.Max(0, Height - HeaderHeight - FooterHeight));
            Invalidate();
        }

        // Ставит панель к окну Проводника, не выпуская её за экран.
        public void DockTo(ExplorerState state)
        {
            if (!state.Visible || state.Bounds.IsEmpty) return;
            // Масштаб — монитора Проводника: панель встаёт на тот же экран.
            int explorerDpi = Win32.GetDpiForWindow(state.Hwnd);
            if (explorerDpi > 0 && explorerDpi != _dpi) ApplyDpi(explorerDpi);

            Rectangle explorerBounds = state.Bounds;
            Rectangle work = Screen.FromRectangle(explorerBounds).WorkingArea;
            int width = FsMath.Clamp(_settings.PanelWidth, MinWidth, Math.Max(MinWidth, work.Width / 2));
            int top = Math.Max(work.Top, explorerBounds.Top);
            int bottom = Math.Min(work.Bottom, explorerBounds.Bottom);
            int height = Math.Max(MinHeight, bottom - top);

            int x = _settings.DockRight ? explorerBounds.Right : explorerBounds.Left - width;
            bool fits = _settings.DockRight ? x + width <= work.Right : x >= work.Left;
            if (!fits && _settings.MakeRoomForPanel && MakeRoom(state, width, work)) return;   // изменение размера придёт новым DockTo через трекер
            // Места рядом нет: встать поверх края окна, а не уехать за экран.
            if (!fits) x = _settings.DockRight ? explorerBounds.Right - width : explorerBounds.Left;
            x = FsMath.Clamp(x, work.Left, Math.Max(work.Left, work.Right - width));

            Rectangle target = new Rectangle(x, top, width, height);
            if (Bounds != target)
            {
                // Поверх всех, и только пока Проводник впереди: HWND_TOP был бы проигнорирован — Windows не даёт
                // фоновому процессу поставить окно выше окна АКТИВНОЙ программы. Флаг отдаётся в Hide, а панель
                // показана только пока впереди Проводник, так что ничто наше не стоит выше другой программы.
                // SWP_NOACTIVATE оставляет фокус там, где его оставил пользователь.
                Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, target.X, target.Y, target.Width, target.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
                Bounds = target;
                LayoutChildren();
            }
            else
            {
                // Проводник поднимает себя на каждый щелчок; без этого панель уезжала бы под него и возвращалась
                // только при следующей смене геометрии.
                Win32.SetWindowPos(Handle, Win32.HWND_TOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            }
        }

        // Уходит из слоя topmost при любом способе скрытия: как только Проводник не впереди, ничто наше не
        // стоит выше того, что впереди.
        protected override void SetVisibleCore(bool value)
        {
            if (!value && IsHandleCreated)
            {
                Win32.SetWindowPos(Handle, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER);
            }
            base.SetVisibleCore(value);
        }

        // Сужает окно Проводника, чтобы у панели была своя полоса экрана. По желанию.
        private bool MakeRoom(ExplorerState state, int panelWidth, Rectangle work)
        {
            Rectangle bounds = state.Bounds;
            int available = _settings.DockRight ? work.Right - bounds.Left : bounds.Right - work.Left;
            int newWidth = available - panelWidth;
            if (newWidth < Px(400)) return false;
            if (Math.Abs(bounds.Width - newWidth) <= Px(4)) return false;       // уже сделано
            if (Win32.IsMaximized(state.Hwnd)) Win32.ShowWindow(state.Hwnd, Win32.SW_SHOWNORMAL);
            // GetWindowRect включает невидимую рамку изменения размера; без поправки окно ползло бы на
            // несколько пикселей при каждой подгонке.
            RECT raw;
            if (!Win32.GetWindowRect(state.Hwnd, out raw)) return false;
            Rectangle visual = Win32.GetVisualBounds(state.Hwnd);
            int leftSlack = visual.Left - raw.Left;
            int rightSlack = raw.Right - visual.Right;
            int topSlack = visual.Top - raw.Top;
            int bottomSlack = raw.Bottom - visual.Bottom;
            int targetLeft = _settings.DockRight ? bounds.Left : work.Left + panelWidth;
            Win32.SetWindowPos(state.Hwnd, IntPtr.Zero, targetLeft - leftSlack, bounds.Top - topSlack,
                newWidth + leftSlack + rightSlack, bounds.Height + topSlack + bottomSlack, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
            return true;
        }

        public void ShowSnapshot(ListingSnapshot snapshot)
        {
            bool samePath = _snapshot != null && string.Equals(_snapshot.Path, snapshot.Path, StringComparison.OrdinalIgnoreCase);
            _snapshot = snapshot;
            // Новая папка — сразу; та же — когда подсчёт закончен: место могло уйти, пока считали.
            if (_settings.ShowFreeSpace) QuerySpace(snapshot.Path, !samePath || snapshot.Final);
            _list.SetRows(snapshot.Rows, samePath);
            _list.SetEmptyText(snapshot.Error ?? (snapshot.Final ? Tr.S("Папка пуста", "The folder is empty") : Tr.S("Читаю содержимое…", "Reading the contents…")));
            Invalidate();
        }

        public void ShowProgress(ScanProgress progress)
        {
            bool wasBusy = _progress.IsBusy;
            _progress = progress;
            if (progress.IsBusy && !_pulse.Enabled) _pulse.Start();
            else if (!progress.IsBusy && _pulse.Enabled) _pulse.Stop();
            if (wasBusy != progress.IsBusy) Invalidate();
            else Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Background);
            DrawHeader(g);
            DrawFooter(g);
            using (Pen border = new Pen(_theme.Border))
                g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        }

        private void DrawHeader(Graphics g)
        {
            Rectangle header = new Rectangle(0, 0, Width, HeaderHeight);
            using (SolidBrush surface = new SolidBrush(_theme.Surface)) g.FillRectangle(surface, header);
            LayoutButtons();
            int buttonsLeft = _buttons.Count > 0 ? _buttons[_buttons.Count - 1].Bounds.Left : Width;

            string path = _snapshot == null ? "" : (_snapshot.Path ?? "");
            string title = path.Length == 0 ? Tr.S("Проводник не открыт", "Explorer is not open") : FolderTitle(path);
            TextRenderer.DrawText(g, title, _titleFont, new Rectangle(Pad, Px(6), buttonsLeft - Pad * 2, Px(20)), _theme.Text,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, StatusText(path), Font, new Rectangle(Pad, Px(26), Width - Pad * 2, Px(20)), _theme.Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.PathEllipsis | TextFormatFlags.NoPrefix);
            DrawButtons(g);
            using (Pen line = new Pen(_theme.Border)) g.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
            if (_progress.IsBusy) DrawProgressLine(g);
        }

        internal static string FolderTitle(string path)
        {
            string trimmed = path.TrimEnd('\\');
            int slash = trimmed.LastIndexOf('\\');
            string leaf = slash >= 0 ? trimmed.Substring(slash + 1) : trimmed;
            return leaf.Length == 0 ? path : leaf;
        }

        private string StatusText(string path)
        {
            switch (_progress.Phase)
            {
                case ScanPhase.Listing:
                    return Tr.S("Читаю содержимое…", "Reading the contents…");
                case ScanPhase.IndexingVolume:
                    return _progress.Percent > 0
                        ? Tr.S("Индексирую том ", "Indexing volume ") + _progress.Detail + " — " + _progress.Percent + "%"
                        : Tr.S("Индексирую том ", "Indexing volume ") + _progress.Detail + "…";
                case ScanPhase.MeasuringFolders:
                    // «3/12» — честная мера прогресса здесь: сколько папок досчитано. Процент пришлось бы
                    // выдумывать — сколько весит остальное, неизвестно.
                    string counted = string.IsNullOrEmpty(_progress.Detail) ? "" : " " + _progress.Detail;
                    return _progress.ItemsSeen > 0
                        ? Tr.S("Считаю папки" + counted + " — просмотрено " + SizeFormat.Count(_progress.ItemsSeen) + " объектов",
                               "Measuring folders" + counted + " — " + SizeFormat.Count(_progress.ItemsSeen) + " items scanned")
                        : Tr.S("Считаю папки", "Measuring folders") + counted + "…";
                case ScanPhase.Unavailable:
                    return _progress.Detail ?? Tr.S("Не удалось прочитать папку", "Could not read the folder");
                default:
                    return path.Length > 0 ? path : Tr.S("Откройте любую папку в Проводнике", "Open any folder in Explorer");
            }
        }

        // Определённая полоса при индексации (известна доля прочитанной $MFT), бегущий отрезок при обходе
        // папок — там итог действительно неизвестен, и выдуманный процент был бы враньём.
        private void DrawProgressLine(Graphics g)
        {
            int height = Px(2);
            Rectangle track = new Rectangle(0, HeaderHeight - height, Width, height);
            using (SolidBrush background = new SolidBrush(Color.FromArgb(_theme.IsDark ? 60 : 40, _theme.Accent)))
                g.FillRectangle(background, track);
            using (SolidBrush fill = new SolidBrush(_theme.Accent))
            {
                if (_progress.Phase == ScanPhase.IndexingVolume && _progress.Percent > 0)
                {
                    g.FillRectangle(fill, track.X, track.Y, track.Width * _progress.Percent / 100, track.Height);
                    return;
                }
                int segment = Math.Max(Px(40), Width / 4);
                int span = Width + segment;
                int x = _pulsePhase * span / 200 - segment;
                g.FillRectangle(fill, x, track.Y, segment, track.Height);
            }
        }

        // GetDiskFreeSpaceEx на сетевом пути может думать секундами — поэтому в пуле, а панель рисует последнее известное.
        private void QuerySpace(string path, bool wanted)
        {
            if (!wanted || string.IsNullOrEmpty(path)) return;
            if (Interlocked.CompareExchange(ref _spaceQuery, 1, 0) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                VolumeSpace space = VolumeSpace.Query(path);
                Interlocked.Exchange(ref _spaceQuery, 0);
                if (!IsHandleCreated || IsDisposed) return;
                FsLog.Swallow(delegate
                {
                    BeginInvoke(new Action(delegate
                    {
                        if (_snapshot == null || !string.Equals(_snapshot.Path, path, StringComparison.OrdinalIgnoreCase)) return;
                        _space = space;
                        Invalidate(new Rectangle(0, Height - FooterHeight, Width, FooterHeight));
                    }));
                });
            });
        }

        private void DrawFooter(Graphics g)
        {
            Rectangle whole = new Rectangle(0, Height - FooterHeight, Width, FooterHeight);
            using (SolidBrush surface = new SolidBrush(_theme.Surface)) g.FillRectangle(surface, whole);
            using (Pen line = new Pen(_theme.Border)) g.DrawLine(line, 0, whole.Top, Width, whole.Top);
            if (SpaceHeight > 0) DrawSpace(g, new Rectangle(0, whole.Top, Width, SpaceHeight));
            Rectangle footer = new Rectangle(0, whole.Top + SpaceHeight, Width, TotalsHeight);

            bool offer;
            string mode = FooterMode(out offer);
            // Места под режим — ровно по тексту: пустая пометка (размеры из памяти, без прав) не должна обрезать итоги.
            TextFormatFlags modeFlags = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            // Замер без EndEllipsis: с ним и пустым размером TextRenderer отдаёт ширину многоточия, а не текста.
            int modeWidth = mode.Length == 0 ? 0 : Math.Min(Px(170), TextRenderer.MeasureText(g, mode, Font,
                new Size(int.MaxValue, TotalsHeight), TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width + Pad);

            string left = "";
            if (_snapshot != null && _snapshot.Rows.Count > 0)
            {
                DirStats totals = _snapshot.Totals;
                left = Tr.S("Итого ", "Total ") + SizeFormat.Short(totals.Bytes) + " · " + SizeFormat.Items(totals.Files, totals.Directories);
            }
            TextRenderer.DrawText(g, left, Font, new Rectangle(Pad, footer.Top, Math.Max(0, Width - Pad * 2 - modeWidth), TotalsHeight),
                _theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            _fastHint = offer ? new Rectangle(Width - Pad - modeWidth, footer.Top, modeWidth, TotalsHeight) : Rectangle.Empty;
            TextRenderer.DrawText(g, mode, Font, new Rectangle(Width - Pad - modeWidth, footer.Top, modeWidth, TotalsHeight),
                offer ? _theme.Accent : _theme.Muted, modeFlags);
        }

        // «Диск C: — свободно 120 ГБ из 1,82 ТБ» и полоска занятого. Меньше десятой доли свободно — полоска красная.
        private void DrawSpace(Graphics g, Rectangle area)
        {
            VolumeSpace space = _snapshot == null ? null : _space;
            if (space == null || space.Total <= 0) return;
            int barWidth = Px(72);
            int barHeight = Px(6);
            string text = Tr.S("Диск ", "Drive ") + space.Label + " — " + Tr.S("свободно ", "free ") + SizeFormat.Short(space.Free)
                          + Tr.S(" из ", " of ") + SizeFormat.Short(space.Total);
            TextRenderer.DrawText(g, text, Font, new Rectangle(Pad, area.Top, Math.Max(0, area.Width - Pad * 3 - barWidth), area.Height),
                _theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Rectangle track = new Rectangle(area.Right - Pad - barWidth, area.Top + (area.Height - barHeight) / 2, barWidth, barHeight);
            double used = Math.Max(0.0, Math.Min(1.0, 1.0 - (double)space.Free / space.Total));
            bool low = space.Free < space.Total / 10;
            Color ink = low ? (_theme.IsDark ? Color.FromArgb(232, 72, 85) : Color.FromArgb(196, 43, 28)) : _theme.Accent;
            using (SolidBrush background = new SolidBrush(Color.FromArgb(_theme.IsDark ? 60 : 40, ink)))
            using (GraphicsPath path = RoundedRect(track, barHeight / 2))
                g.FillPath(background, path);
            int filled = (int)Math.Round(track.Width * used);
            if (filled >= barHeight)
            {
                using (SolidBrush fill = new SolidBrush(ink))
                using (GraphicsPath path = RoundedRect(new Rectangle(track.X, track.Y, filled, track.Height), barHeight / 2))
                    g.FillPath(fill, path);
            }
        }

        // Медленный путь медленен по одной причине, с которой пользователь может что-то сделать: индексу NTFS
        // нужны права администратора. Сказать это там, где режим уже назван, один раз и без диалога.
        private string FooterMode(out bool offer)
        {
            offer = false;
            if (_snapshot != null && _snapshot.FastMode) return Tr.S("быстрый режим", "fast mode");
            if (!_settings.UseFastNtfsIndex || Elevation.IsElevated) return "";
            offer = true;
            return Tr.S("⚡ включить быстрый режим", "⚡ enable fast mode");
        }

        private void LayoutButtons()
        {
            _buttons.Clear();
            int size = Px(20);
            int gap = Px(4);
            int y = Px(6);
            int x = Width - Pad - size;
            foreach (HeaderAction action in new[] { HeaderAction.Hide, HeaderAction.Menu, HeaderAction.Refresh })
            {
                _buttons.Add(new HeaderButton(action, new Rectangle(x, y, size, size)));
                x -= size + gap;
            }
        }

        private int ButtonAt(Point p)
        {
            for (int i = 0; i < _buttons.Count; i++) if (_buttons[i].Bounds.Contains(p)) return i;
            return -1;
        }

        private void DrawButtons(Graphics g)
        {
            for (int i = 0; i < _buttons.Count; i++)
            {
                HeaderButton button = _buttons[i];
                if (i == _hoverButton)
                {
                    using (SolidBrush hover = new SolidBrush(_theme.RowHover))
                    using (GraphicsPath path = RoundedRect(button.Bounds, Px(4)))
                        g.FillPath(hover, path);
                }
                Color ink = i == _hoverButton ? _theme.Text : _theme.Muted;
                Rectangle r = button.Bounds;
                int cx = r.X + r.Width / 2;
                int cy = r.Y + r.Height / 2;
                int s = Px(5);
                using (Pen pen = new Pen(ink, 1.4f * _dpi / 96f))
                {
                    switch (button.Action)
                    {
                        case HeaderAction.Hide:
                            g.DrawLine(pen, cx - s, cy - s, cx + s, cy + s);
                            g.DrawLine(pen, cx + s, cy - s, cx - s, cy + s);
                            break;
                        case HeaderAction.Menu:
                            using (SolidBrush dot = new SolidBrush(ink))
                            {
                                int d = Px(3);
                                for (int k = -1; k <= 1; k++) g.FillEllipse(dot, cx - d / 2, cy + k * Px(5) - d / 2, d, d);
                            }
                            break;
                        case HeaderAction.Refresh:
                            g.DrawArc(pen, cx - s, cy - s, s * 2, s * 2, 40, 280);
                            g.DrawLine(pen, cx + s, cy - s, cx + s, cy - s + Px(4));
                            g.DrawLine(pen, cx + s, cy - s, cx + s - Px(4), cy - s);
                            break;
                    }
                }
            }
        }

        internal static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0 || rect.Width <= 0 || rect.Height <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_resizing)
            {
                int delta = _settings.DockRight ? _resizeStartX - Cursor.Position.X : Cursor.Position.X - _resizeStartX;
                // Ширина хранится в пикселях этого монитора — как у отдельной программы.
                _settings.PanelWidth = FsMath.Clamp(_resizeStartWidth + delta, MinWidth, FsSettings.MaxPanelWidth);
                Action h = WidthChanged;
                if (h != null) h();
                return;
            }
            Cursor = InGrip(e.Location) ? Cursors.SizeWE : _fastHint.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
            int hover = ButtonAt(e.Location);
            if (hover != _hoverButton)
            {
                _hoverButton = hover;
                Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
            }
        }

        private bool InGrip(Point p) { return _settings.DockRight ? p.X <= GripWidth : p.X >= Width - GripWidth; }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (InGrip(e.Location))
            {
                _resizing = true;
                _resizeStartX = Cursor.Position.X;
                _resizeStartWidth = Width;
                Capture = true;
                return;
            }
            if (_fastHint.Contains(e.Location))
            {
                Action fast = FastModeRequested;
                if (fast != null) fast();
                return;
            }
            int index = ButtonAt(e.Location);
            if (index < 0) return;
            switch (_buttons[index].Action)
            {
                case HeaderAction.Refresh:
                    Action refresh = RefreshRequested;
                    if (refresh != null) refresh();
                    break;
                case HeaderAction.Menu:
                    Action<Point> menu = MenuRequested;
                    if (menu != null) menu(PointToScreen(new Point(_buttons[index].Bounds.Left, _buttons[index].Bounds.Bottom)));
                    break;
                case HeaderAction.Hide:
                    Hide();
                    break;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_resizing) return;
            _resizing = false;
            Capture = false;
            _settings.Save();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hoverButton == -1) return;
            _hoverButton = -1;
            Invalidate(new Rectangle(0, 0, Width, HeaderHeight));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _pulse.Dispose();
                if (_titleFont != null) _titleFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Сам список: строка на запись, пропорциональная полоска за именем, размер справа. Полностью рисуется
    //  сам — стандартный ListView не покажет полоску, спиннер и строку в теме сразу и мерцает, когда строки
    //  заменяются десятки раз в секунду, пока приходят размеры.
    // ------------------------------------------------------------------ //
    internal sealed class SizeListView : Control
    {
        private static readonly string[] SpinnerFrames = { "·  ", "·· ", "···", " ··", "  ·" };

        private readonly System.Windows.Forms.Timer _spinner = new System.Windows.Forms.Timer();
        private readonly ToolTip _tip = new ToolTip();

        private IList<SizeRow> _rows = new SizeRow[0];
        private FsTheme _theme = FsTheme.Dark;
        private Font _boldFont;
        private int _dpi = 96;
        private int _scroll;
        private int _hover = -1;
        private int _spinnerPhase;
        private bool _draggingThumb;
        private string _emptyText = "";

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public SizeSortMode SortMode { get; set; }

        public event Action<SizeSortMode> SortRequested;
        public event Action<SizeRow> RowActivated;
        public event Action<SizeRow, Point> RowMenuRequested;

        public SizeListView()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SortMode = SizeSortMode.SizeDescending;
            _boldFont = new Font(Font, FontStyle.Bold);
            _tip.ShowAlways = true;
            _tip.InitialDelay = 350;
            _tip.ReshowDelay = 120;
            _spinner.Interval = 160;
            _spinner.Tick += delegate
            {
                _spinnerPhase = (_spinnerPhase + 1) % SpinnerFrames.Length;
                Invalidate();
            };
        }

        public void SetDpi(int dpi)
        {
            _dpi = dpi;
            Invalidate();
        }

        private int Px(int logical) { return (int)Math.Round(logical * _dpi / 96.0); }

        private int RowHeight { get { return Px(26); } }
        private int HeaderHeight { get { return Px(28); } }
        private int GlyphWidth { get { return Px(22); } }
        private int SizeColumnWidth { get { return Px(88); } }
        private int SidePadding { get { return Px(10); } }
        private int ScrollbarWidth { get { return Px(8); } }

        private int ViewportHeight { get { return Math.Max(0, Height - HeaderHeight); } }
        private int ContentHeight { get { return _rows.Count * RowHeight; } }
        private int MaxScroll { get { return Math.Max(0, ContentHeight - ViewportHeight); } }
        private bool NeedsScrollbar { get { return ContentHeight > ViewportHeight; } }

        public void SetTheme(FsTheme theme)
        {
            _theme = theme;
            BackColor = theme.Background;
            Invalidate();
        }

        public void SetRows(IList<SizeRow> rows, bool keepScroll)
        {
            _rows = rows;
            if (!keepScroll) _scroll = 0;
            _scroll = Math.Min(_scroll, MaxScroll);
            bool busy = false;
            foreach (SizeRow row in rows)
            {
                if (row.State == RowState.Pending || row.State == RowState.Measuring) { busy = true; break; }
            }
            if (busy && !_spinner.Enabled) _spinner.Start();
            else if (!busy && _spinner.Enabled) _spinner.Stop();
            Invalidate();
        }

        public void SetEmptyText(string text)
        {
            _emptyText = text ?? "";
            Invalidate();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            _boldFont.Dispose();
            _boldFont = new Font(Font, FontStyle.Bold);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_theme.Background);
            DrawHeader(g);
            if (_rows.Count == 0)
            {
                if (_emptyText.Length > 0)
                    TextRenderer.DrawText(g, _emptyText, Font, new Rectangle(0, HeaderHeight, Width, ViewportHeight), _theme.Muted,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }
            int first = Math.Max(0, _scroll / RowHeight);
            int last = Math.Min(_rows.Count - 1, (_scroll + ViewportHeight) / RowHeight);
            int barRight = Width - (NeedsScrollbar ? ScrollbarWidth : 0);
            GraphicsState clip = g.Save();
            g.SetClip(new Rectangle(0, HeaderHeight, Width, ViewportHeight));
            for (int i = first; i <= last; i++)
                DrawRow(g, _rows[i], i, HeaderHeight + i * RowHeight - _scroll, barRight);
            g.Restore(clip);
            if (NeedsScrollbar) DrawScrollbar(g);
        }

        private void DrawHeader(Graphics g)
        {
            using (SolidBrush background = new SolidBrush(_theme.Surface)) g.FillRectangle(background, 0, 0, Width, HeaderHeight);
            using (Pen border = new Pen(_theme.Border)) g.DrawLine(border, 0, HeaderHeight - 1, Width, HeaderHeight - 1);
            string name = Tr.S("Имя", "Name") + Indicator(SizeSortMode.NameAscending);
            string size = Indicator(SizeSortMode.SizeDescending) + Tr.S("Размер", "Size");
            TextRenderer.DrawText(g, name, Font, new Rectangle(SidePadding, 0, Width - SizeColumnWidth - SidePadding * 2, HeaderHeight),
                _theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, size, Font, new Rectangle(Width - SizeColumnWidth - SidePadding, 0, SizeColumnWidth, HeaderHeight),
                _theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
        }

        private string Indicator(SizeSortMode mode) { return SortMode == mode ? " ▾" : ""; }

        private void DrawRow(Graphics g, SizeRow row, int index, int y, int barRight)
        {
            Rectangle rowRect = new Rectangle(0, y, barRight, RowHeight);
            if (row.Share > 0)
            {
                int barWidth = (int)Math.Round(row.Share * (barRight - SidePadding));
                if (barWidth > 0)
                    using (SolidBrush bar = new SolidBrush(row.IsDirectory ? _theme.BarFolder : _theme.BarFile))
                        g.FillRectangle(bar, 0, y + 1, barWidth, RowHeight - 2);
            }
            if (index == _hover)
                using (SolidBrush hover = new SolidBrush(_theme.RowHover))
                    g.FillRectangle(hover, rowRect);

            DrawGlyph(g, row, new Rectangle(SidePadding, y, GlyphWidth, RowHeight));

            Color nameColor = row.IsHidden ? _theme.Muted : _theme.Text;
            Rectangle nameRect = new Rectangle(SidePadding + GlyphWidth, y, barRight - SidePadding * 2 - GlyphWidth - SizeColumnWidth, RowHeight);
            // Имя, которое печатает Проводник, — чтобы два списка читались одинаково: «Пользователи», а не «Users».
            TextRenderer.DrawText(g, row.DisplayName ?? row.Name, Font, nameRect, nameColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            Rectangle sizeRect = new Rectangle(barRight - SizeColumnWidth - SidePadding, y, SizeColumnWidth, RowHeight);
            if (row.IsDirectory && row.IsReparsePoint)
            {
                // Не ноль и не «неизвестно»: байты настоящие, но принадлежат папке, на которую это указывает.
                TextRenderer.DrawText(g, "→", Font, sizeRect, _theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            else if (row.State == RowState.Pending)
            {
                TextRenderer.DrawText(g, SpinnerFrames[_spinnerPhase], Font, sizeRect, _theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            else if (row.State == RowState.Measuring)
            {
                // Приглушённо и не жирно: число настоящее, но не окончательное — промежуточная сумма или результат
                // прошлого сеанса. Жирный — только для досчитанных чисел.
                TextRenderer.DrawText(g, SizeFormat.Short(row.Bytes), Font, sizeRect, _theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
            else
            {
                // «≥» не украшение: часть поддерева не прочиталась, и число — нижняя граница.
                string text = (row.State == RowState.Partial ? "≥ " : "") + SizeFormat.Short(row.Bytes);
                TextRenderer.DrawText(g, text, _boldFont, sizeRect, row.State == RowState.Failed ? _theme.Muted : _theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }

        // Нарисован, а не взят из шрифта значков: без зависимости от того, какой Segoe пришёл с системой.
        private void DrawGlyph(Graphics g, SizeRow row, Rectangle bounds)
        {
            int size = Px(12);
            int x = bounds.X;
            int y = bounds.Y + (bounds.Height - size) / 2;
            using (Pen pen = new Pen(row.IsDirectory ? _theme.Accent : _theme.Muted, Math.Max(1, Px(1))))
            {
                if (row.IsDirectory)
                {
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(row.IsReparsePoint ? 40 : 90, _theme.Accent)))
                    {
                        Rectangle body = new Rectangle(x, y + size / 5, size, size - size / 5);
                        g.FillRectangle(fill, body);
                        g.DrawRectangle(pen, body);
                    }
                    g.DrawLine(pen, x, y + size / 5, x + size / 3, y + size / 5);
                    g.DrawLine(pen, x + size / 3, y + size / 5, x + size / 3, y);
                    g.DrawLine(pen, x + size / 3, y, x + (int)(size * 0.6), y);
                }
                else
                {
                    Rectangle body = new Rectangle(x + size / 8, y, size - size / 4, size);
                    g.DrawRectangle(pen, body);
                    g.DrawLine(pen, body.Right - size / 3, body.Y, body.Right, body.Y + size / 3);
                }
            }
        }

        private int ThumbHeight
        {
            get { return Math.Max(Px(24), (int)((long)ViewportHeight * ViewportHeight / Math.Max(1, ContentHeight))); }
        }

        private void DrawScrollbar(Graphics g)
        {
            int travel = ViewportHeight - ThumbHeight;
            int thumbY = HeaderHeight + (MaxScroll == 0 ? 0 : (int)((long)travel * _scroll / MaxScroll));
            int pad = Px(2);
            Rectangle rect = new Rectangle(Width - ScrollbarWidth + pad, thumbY, ScrollbarWidth - pad * 2, ThumbHeight);
            using (SolidBrush brush = new SolidBrush(_theme.Scrollbar))
            using (GraphicsPath path = PanelForm.RoundedRect(rect, rect.Width / 2))
                g.FillPath(brush, path);
        }

        private int RowAt(Point p)
        {
            if (p.Y < HeaderHeight) return -1;
            int index = (p.Y - HeaderHeight + _scroll) / RowHeight;
            return index >= 0 && index < _rows.Count ? index : -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingThumb)
            {
                DragThumbTo(e.Y);
                return;
            }
            int index = RowAt(e.Location);
            if (index == _hover) return;
            _hover = index;
            _tip.SetToolTip(this, index >= 0 ? Describe(_rows[index]) : string.Empty);
            Invalidate();
        }

        internal static string Describe(SizeRow row)
        {
            List<string> lines = new List<string>();
            lines.Add(row.FullPath);
            if (row.IsDirectory && row.IsReparsePoint)
            {
                lines.Add(Tr.S("Ссылка на другую папку — её содержимое считается там, а не здесь",
                               "A link to another folder — its contents are counted there, not here"));
                return string.Join(Environment.NewLine, lines.ToArray());
            }
            switch (row.State)
            {
                case RowState.Pending:
                    lines.Add(Tr.S("Идёт подсчёт…", "Counting…"));
                    break;
                case RowState.Measuring:
                    lines.Add(SizeFormat.ExactBytes(row.Bytes) + (row.FromCache
                        ? Tr.S(" — результат прошлого подсчёта, идёт проверка", " — last count's result, being re-checked")
                        : Tr.S(" — насчитано на эту секунду, подсчёт продолжается", " — counted so far, still counting")));
                    break;
                case RowState.Partial:
                    lines.Add(SizeFormat.ExactBytes(row.Bytes) + Tr.S(" — часть содержимого недоступна, это нижняя граница",
                                                                      " — part of the contents is unreadable, this is a lower bound"));
                    break;
                case RowState.Failed:
                    lines.Add(Tr.S("Не удалось прочитать", "Could not read"));
                    break;
                default:
                    lines.Add(SizeFormat.ExactBytes(row.Bytes));
                    break;
            }
            if (row.IsDirectory && (row.State == RowState.Ready || row.State == RowState.Partial))
                lines.Add(Tr.S("Внутри: ", "Inside: ") + SizeFormat.Items(row.Files, row.Directories));
            if (row.IsReparsePoint)
                lines.Add(Tr.S("Символьная ссылка или точка соединения — содержимое не учитывается",
                               "Symbolic link or junction — its contents are not counted"));
            return string.Join(Environment.NewLine, lines.ToArray());
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Y < HeaderHeight)
            {
                if (e.Button == MouseButtons.Left)
                {
                    bool nameColumn = e.X < Width - SizeColumnWidth - SidePadding;
                    Action<SizeSortMode> sort = SortRequested;
                    if (sort != null) sort(nameColumn ? SizeSortMode.NameAscending : SizeSortMode.SizeDescending);
                }
                return;
            }
            if (NeedsScrollbar && e.X >= Width - ScrollbarWidth && e.Button == MouseButtons.Left)
            {
                _draggingThumb = true;
                DragThumbTo(e.Y);
                Capture = true;
                return;
            }
            int index = RowAt(e.Location);
            if (index >= 0 && e.Button == MouseButtons.Right)
            {
                Action<SizeRow, Point> menu = RowMenuRequested;
                if (menu != null) menu(_rows[index], PointToScreen(e.Location));
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!_draggingThumb) return;
            _draggingThumb = false;
            Capture = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || e.Y < HeaderHeight) return;
            int index = RowAt(e.Location);
            if (index < 0) return;
            Action<SizeRow> activated = RowActivated;
            if (activated != null) activated(_rows[index]);
        }

        private void DragThumbTo(int mouseY)
        {
            int travel = Math.Max(1, ViewportHeight - ThumbHeight);
            int position = mouseY - HeaderHeight - ThumbHeight / 2;
            ScrollTo((int)((long)FsMath.Clamp(position, 0, travel) * MaxScroll / travel));
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(_scroll - e.Delta * RowHeight * SystemInformation.MouseWheelScrollLines / 120);
        }

        private void ScrollTo(int value)
        {
            int clamped = FsMath.Clamp(value, 0, MaxScroll);
            if (clamped == _scroll) return;
            _scroll = clamped;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _spinner.Dispose();
                _tip.Dispose();
                _boldFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
