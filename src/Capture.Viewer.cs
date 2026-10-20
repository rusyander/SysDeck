// SysDeck — «Захват»: просмотр снимков из галереи с листанием.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Окно просмотра. «Фотографии» Windows получают соседние файлы только от Проводника: открытые из другой
    //  программы, они показывают один снимок и не листают. Поэтому снимки галереи смотрятся здесь, по списку
    //  галереи и в её порядке, а видео по-прежнему открывает плеер Windows.
    // ------------------------------------------------------------------ //
    internal sealed class GalleryViewer : Form
    {
        private const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        private readonly List<GalleryItem> _list = new List<GalleryItem>();
        // Текущий снимок и его соседи: соседи читаются в фоне заранее, поэтому листание не ждёт диска.
        private readonly Dictionary<string, Bitmap> _cache = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _wanted = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, bool> _loading = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();
        private readonly ViewerCanvas _canvas = new ViewerCanvas();
        private readonly Panel _bar = new Panel();
        private readonly Label _caption = new Label();
        private readonly FlowLayoutPanel _buttons = new FlowLayoutPanel();
        private Button _edit;
        private Font _barFont;
        private int _index = -1;
        private bool _closed;
        private bool _fullScreen;
        private FormWindowState _stateBefore;
        private Rectangle _boundsBefore;
        private float _scale = 1f;

        public event Action<string> EditRequested;
        // Последний показанный файл — галерея выделяет его у себя, чтобы после закрытия было видно, где остановились.
        public event Action<string> Viewed;

        public GalleryViewer(Rectangle work)
        {
            _scale = Math.Max(1f, CapDpi.ScaleAt(new Point(work.X + work.Width / 2, work.Y + work.Height / 2)));
            AutoScaleMode = AutoScaleMode.None;
            StartPosition = FormStartPosition.Manual;
            BackColor = EditorColors.Canvas;
            ForeColor = EditorColors.Text;
            ShowInTaskbar = true;
            Text = Tr.S("Просмотр", "Viewer");
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }

            _bar.Dock = DockStyle.Top;
            _bar.BackColor = EditorColors.Back;
            _caption.Dock = DockStyle.Fill;
            _caption.ForeColor = EditorColors.Text;
            _caption.TextAlign = ContentAlignment.MiddleLeft;
            _caption.AutoEllipsis = true;
            _buttons.Dock = DockStyle.Right;
            _buttons.WrapContents = false;
            _buttons.AutoSize = true;
            _buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _buttons.BackColor = EditorColors.Back;
            _bar.Controls.Add(_caption);
            _bar.Controls.Add(_buttons);
            BuildButtons();

            _canvas.Dock = DockStyle.Fill;
            _canvas.StepRequested += Step;
            _canvas.FullScreenRequested += ToggleFullScreen;
            Controls.Add(_canvas);
            Controls.Add(_bar);

            ApplyScale(_scale);
            int w = Math.Max(MinimumSize.Width, (int)(work.Width * 0.9));
            int h = Math.Max(MinimumSize.Height, (int)(work.Height * 0.9));
            Bounds = new Rectangle(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h);
            WindowState = FormWindowState.Maximized;
        }

        // Снимки раздела галереи в её порядке (видео пропускаются) и место выбранного среди них.
        internal static List<GalleryItem> Images(IList<GalleryItem> view, GalleryItem start, out int index)
        {
            List<GalleryItem> list = new List<GalleryItem>();
            index = -1;
            foreach (GalleryItem it in view)
            {
                if (it.Video) continue;
                if (index < 0 && start != null && string.Equals(it.Path, start.Path, StringComparison.OrdinalIgnoreCase)) index = list.Count;
                list.Add(it);
            }
            if (index < 0 && start != null && !start.Video)
            {
                index = 0;
                list.Insert(0, start);
            }
            return list;
        }

        // Место картинки в области: целиком, по центру и не крупнее своих пикселей (мелкий снимок не размывается).
        internal static Rectangle FitRect(Size image, Rectangle area)
        {
            if (image.Width <= 0 || image.Height <= 0 || area.Width <= 0 || area.Height <= 0) return Rectangle.Empty;
            double k = Math.Min(1.0, Math.Min(area.Width / (double)image.Width, area.Height / (double)image.Height));
            int w = Math.Max(1, (int)Math.Round(image.Width * k)), h = Math.Max(1, (int)Math.Round(image.Height * k));
            return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        public int Count { get { return _list.Count; } }
        public int Position { get { return _index; } }
        public string Current { get { return _index >= 0 && _index < _list.Count ? _list[_index].Path : null; } }
        internal Size ShownSize { get { return _canvas.SourceSize; } }

        public void ShowList(List<GalleryItem> items, int index)
        {
            _list.Clear();
            _list.AddRange(items);
            _index = -1;
            Go(index);
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        private Button Mk(string text, int width, EventHandler click)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.FlatAppearance.BorderColor = EditorColors.Line;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 56, 56);
            b.BackColor = Color.FromArgb(44, 44, 44);
            b.ForeColor = EditorColors.Text;
            b.AutoSize = false;
            b.TabStop = false;
            b.Tag = width;
            b.Click += click;
            // Фокус возвращается картинке: иначе стрелки и колесо достались бы кнопке.
            b.Click += delegate { _canvas.Focus(); };
            _buttons.Controls.Add(b);
            return b;
        }

        private void BuildButtons()
        {
            Mk(Tr.S("Открыть в программе", "Open in app"), 170, delegate { if (Current != null) ToastHost.OpenFile(Current); });
            _edit = Mk(Tr.S("Редактировать", "Edit"), 130, delegate
            {
                string path = Current;
                if (path != null && File.Exists(path) && EditRequested != null) EditRequested(path);
            });
            Mk(Tr.S("Показать в папке", "Show in folder"), 150, delegate { if (Current != null) ToastHost.ShowInFolder(Current); });
            Mk(Tr.S("На весь экран", "Full screen"), 130, delegate { ToggleFullScreen(); });
        }

        private void ApplyScale(float scale)
        {
            _scale = scale;
            MinimumSize = new Size(S(480), S(360));
            _bar.Height = S(46);
            _bar.Padding = new Padding(S(14), S(8), S(10), S(8));
            Font old = _barFont;
            _barFont = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _caption.Font = _barFont;
            foreach (Control c in _buttons.Controls)
            {
                Button b = c as Button;
                if (b == null) continue;
                b.Font = _barFont;
                b.Height = S(30);
                b.Margin = new Padding(S(6), 0, 0, 0);
                b.Width = S((int)b.Tag);
            }
            _canvas.ApplyScale(scale);
            if (old != null) old.Dispose();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                if (Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _canvas.Focus();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                RECT r = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                ApplyScale(Math.Max(1f, ((int)m.WParam & 0xFFFF) / 96f));
                if (WindowState == FormWindowState.Normal && !_fullScreen)
                    Bounds = new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
                PerformLayout();
                Invalidate(true);
                return;
            }
            base.WndProc(ref m);
        }

        // Клавиши перехватываются до кнопок: стрелки и пробел листают, даже если фокус ушёл на панель.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Left:
                case Keys.Up:
                case Keys.PageUp:
                    Step(-1);
                    return true;
                case Keys.Right:
                case Keys.Down:
                case Keys.PageDown:
                case Keys.Space:
                    Step(1);
                    return true;
                case Keys.Home: Go(0); return true;
                case Keys.End: Go(_list.Count - 1); return true;
                case Keys.F11:
                case Keys.Enter:
                    ToggleFullScreen();
                    return true;
                case Keys.Escape:
                    if (_fullScreen) ToggleFullScreen();
                    else Close();
                    return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ---- листание ----

        private void Step(int delta)
        {
            int next = _index + delta;
            if (next < 0 || next >= _list.Count) return;   // на краях стоим, а не перескакиваем в начало
            Go(next);
        }

        private void Go(int i)
        {
            if (_list.Count == 0) { Close(); return; }
            i = Math.Max(0, Math.Min(_list.Count - 1, i));
            if (i == _index) return;
            _index = i;
            GalleryItem it = _list[i];
            string failure;
            Bitmap image = Take(it.Path, out failure);
            _canvas.SetImage(image, failure);
            _canvas.CanPrev = i > 0;
            _canvas.CanNext = i < _list.Count - 1;
            Keep(i);
            Preload(i + 1);
            Preload(i - 1);
            UpdateCaption(image);
            if (Viewed != null) Viewed(it.Path);
        }

        private void UpdateCaption(Bitmap image)
        {
            GalleryItem it = _list[_index];
            string text = it.Name;
            if (!string.IsNullOrEmpty(it.Folder)) text += "  ·  " + it.Folder;
            text += "  ·  " + (_index + 1).ToString(CultureInfo.InvariantCulture) + " / " + _list.Count.ToString(CultureInfo.InvariantCulture);
            if (image != null)
                text += "  ·  " + image.Width.ToString(CultureInfo.InvariantCulture) + "×" + image.Height.ToString(CultureInfo.InvariantCulture);
            text += "  ·  " + GalleryGrid.Bytes(it.Size);
            _caption.Text = text;
            Text = it.Name + " — " + Tr.S("Просмотр", "Viewer");
            _edit.Enabled = image != null;
        }

        private Bitmap Take(string path, out string failure)
        {
            failure = null;
            lock (_cacheLock)
            {
                Bitmap ready;
                if (_cache.TryGetValue(path, out ready)) return ready;
            }
            Bitmap loaded;
            try { loaded = ImageStore.LoadUnlocked(path); }
            catch (FileNotFoundException) { failure = Tr.S("Файла больше нет", "The file is gone"); return null; }
            catch (DirectoryNotFoundException) { failure = Tr.S("Файла больше нет", "The file is gone"); return null; }
            catch (Exception ex)
            {
                failure = Tr.S("Не удалось открыть: ", "Could not open: ") + ex.Message;
                return null;
            }
            lock (_cacheLock)
            {
                Bitmap raced;
                if (_cache.TryGetValue(path, out raced)) { loaded.Dispose(); return raced; }
                _cache[path] = loaded;
            }
            return loaded;
        }

        // В памяти держатся только текущий снимок и два соседа: снимок 4K — это 33 МБ.
        private void Keep(int i)
        {
            List<Bitmap> drop = new List<Bitmap>();
            lock (_cacheLock)
            {
                _wanted.Clear();
                for (int j = i - 1; j <= i + 1; j++)
                    if (j >= 0 && j < _list.Count) _wanted[_list[j].Path] = true;
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, Bitmap> pair in _cache)
                    if (!_wanted.ContainsKey(pair.Key)) stale.Add(pair.Key);
                foreach (string key in stale)
                {
                    drop.Add(_cache[key]);
                    _cache.Remove(key);
                }
            }
            foreach (Bitmap b in drop) b.Dispose();
        }

        private void Preload(int i)
        {
            if (i < 0 || i >= _list.Count) return;
            string path = _list[i].Path;
            lock (_cacheLock)
            {
                if (_closed || _cache.ContainsKey(path) || _loading.ContainsKey(path)) return;
                _loading[path] = true;
            }
            ThreadPool.QueueUserWorkItem(delegate
            {
                Bitmap loaded = null;
                try { loaded = ImageStore.LoadUnlocked(path); }
                catch { }   // ошибку покажет обычная загрузка, когда до снимка долистают
                bool keep = false;
                lock (_cacheLock)
                {
                    _loading.Remove(path);
                    if (loaded != null && !_closed && _wanted.ContainsKey(path) && !_cache.ContainsKey(path))
                    {
                        _cache[path] = loaded;
                        keep = true;
                    }
                }
                if (!keep && loaded != null) loaded.Dispose();
            });
        }

        // ---- полноэкранный режим ----

        private void ToggleFullScreen()
        {
            SuspendLayout();
            if (!_fullScreen)
            {
                _stateBefore = WindowState;
                _boundsBefore = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                Rectangle screen = Screen.FromControl(this).Bounds;
                _fullScreen = true;
                _bar.Visible = false;
                WindowState = FormWindowState.Normal;
                FormBorderStyle = FormBorderStyle.None;
                Bounds = screen;
            }
            else
            {
                _fullScreen = false;
                FormBorderStyle = FormBorderStyle.Sizable;
                Bounds = _boundsBefore;
                WindowState = _stateBefore;
                _bar.Visible = true;
            }
            _canvas.Padded = !_fullScreen;
            ResumeLayout(true);
            _canvas.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            List<Bitmap> drop;
            lock (_cacheLock)
            {
                _closed = true;
                drop = new List<Bitmap>(_cache.Values);
                _cache.Clear();
            }
            _canvas.SetImage(null, null);
            foreach (Bitmap b in drop) b.Dispose();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _barFont != null) { _barFont.Dispose(); _barFont = null; }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Поле картинки: вписанный снимок и стрелки по краям. Уменьшенная копия строится один раз на размер окна,
    //  а не при каждой перерисовке — наведение на стрелку не пересчитывает 4K-картинку.
    // ------------------------------------------------------------------ //
    internal sealed class ViewerCanvas : Control
    {
        private Bitmap _source;
        private Bitmap _fitted;
        private string _message;
        private Font _messageFont;
        private float _scale = 1f;
        private int _hot;
        private int _wheel;
        private bool _canPrev, _canNext, _padded = true;

        public event Action<int> StepRequested;
        public event Action FullScreenRequested;

        public ViewerCanvas()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardDoubleClick, true);
            BackColor = EditorColors.Canvas;
            TabStop = true;
        }

        public bool CanPrev { set { _canPrev = value; Invalidate(); } }
        public bool CanNext { set { _canNext = value; Invalidate(); } }
        public bool Padded { set { _padded = value; DropFitted(); Invalidate(); } }
        public Size SourceSize { get { return _source != null ? _source.Size : Size.Empty; } }

        private int S(float v) { return (int)Math.Round(v * _scale); }
        private int Edge { get { return S(88); } }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            Font old = _messageFont;
            _messageFont = new Font("Segoe UI", 11f * scale, FontStyle.Regular, GraphicsUnit.Point);
            if (old != null) old.Dispose();
            DropFitted();
            Invalidate();
        }

        // Картинкой владеет окно просмотра (кэш соседей), поле её только рисует.
        public void SetImage(Bitmap image, string message)
        {
            _source = image;
            _message = image == null ? message : null;
            DropFitted();
            Invalidate();
        }

        private void DropFitted()
        {
            if (_fitted != null) { _fitted.Dispose(); _fitted = null; }
        }

        private Rectangle Area()
        {
            Rectangle r = ClientRectangle;
            if (_padded) r.Inflate(-S(8), -S(8));
            return r;
        }

        protected override void OnResize(EventArgs e)
        {
            DropFitted();
            base.OnResize(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);
            if (_source != null)
            {
                Rectangle r = GalleryViewer.FitRect(_source.Size, Area());
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                if (r.Size == _source.Size) g.DrawImage(_source, r);
                else if (!r.IsEmpty) g.DrawImage(Fitted(r.Size), r);
            }
            else if (_message != null && _messageFont != null)
            {
                TextRenderer.DrawText(g, _message, _messageFont, ClientRectangle, EditorColors.Dim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            if (_canPrev) DrawArrow(g, -1);
            if (_canNext) DrawArrow(g, 1);
        }

        private Bitmap Fitted(Size size)
        {
            if (_fitted != null && _fitted.Size == size) return _fitted;
            DropFitted();
            _fitted = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_fitted))
            using (ImageAttributes edges = new ImageAttributes())
            {
                edges.SetWrapMode(WrapMode.TileFlipXY);   // без светлой каймы по краю уменьшенной картинки
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(_source, new Rectangle(Point.Empty, size), 0, 0, _source.Width, _source.Height, GraphicsUnit.Pixel, edges);
            }
            return _fitted;
        }

        private void DrawArrow(Graphics g, int side)
        {
            bool hot = _hot == side;
            int d = S(46);
            int cx = side < 0 ? Edge / 2 : ClientSize.Width - Edge / 2;
            int cy = ClientSize.Height / 2;
            Rectangle circle = new Rectangle(cx - d / 2, cy - d / 2, d, d);
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(hot ? 220 : 120, 24, 24, 24)))
                g.FillEllipse(fill, circle);
            using (Pen ring = new Pen(Color.FromArgb(hot ? 140 : 60, 255, 255, 255), Math.Max(1f, _scale)))
                g.DrawEllipse(ring, circle);
            float a = d * 0.16f;
            float tip = cx + side * a * 0.6f;
            using (Pen chevron = new Pen(Color.FromArgb(hot ? 255 : 190, 255, 255, 255), 2.5f * _scale))
            {
                chevron.StartCap = LineCap.Round;
                chevron.EndCap = LineCap.Round;
                chevron.LineJoin = LineJoin.Round;
                g.DrawLines(chevron, new PointF[]
                {
                    new PointF(tip - side * a * 1.2f, cy - a * 1.3f),
                    new PointF(tip, cy),
                    new PointF(tip - side * a * 1.2f, cy + a * 1.3f),
                });
            }
        }

        // -1 — левая полоса «назад», 1 — правая «вперёд», 0 — сама картинка.
        private int Zone(Point p)
        {
            if (_canPrev && p.X < Edge) return -1;
            if (_canNext && p.X >= ClientSize.Width - Edge) return 1;
            return 0;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int zone = Zone(e.Location);
            Cursor = zone != 0 ? Cursors.Hand : Cursors.Default;
            if (zone == _hot) return;
            _hot = zone;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot == 0) return;
            _hot = 0;
            Invalidate();
        }

        // Каждое нажатие на стрелку — шаг, двойное в том числе: быстрые щелчки листают, а не разворачивают окно.
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left) return;
            int zone = Zone(e.Location);
            if (zone != 0 && StepRequested != null) StepRequested(zone);
            else if (zone == 0 && e.Clicks == 2 && FullScreenRequested != null) FullScreenRequested();
            _hot = Zone(e.Location);
            Invalidate();
        }

        // Тачпад присылает колесо мелкими долями — шаг на каждый накопленный «щелчок» колеса.
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            _wheel += e.Delta;
            while (_wheel >= 120) { _wheel -= 120; if (StepRequested != null) StepRequested(-1); }
            while (_wheel <= -120) { _wheel += 120; if (StepRequested != null) StepRequested(1); }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                DropFitted();
                if (_messageFont != null) { _messageFont.Dispose(); _messageFont = null; }
            }
            base.Dispose(disposing);
        }
    }
}
