// SysDeck — «Захват»: окно галереи. Снимки и видео одним списком с превью, тремя разделами
// («Всё», «Снимки», «Видео»), выделением мышью и клавиатурой, копированием, перетаскиванием и удалением в Корзину.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Окно живёт в потоке интерфейса фонового процесса захвата и не принадлежит главному окну программы: его можно
// двигать, сворачивать и закрывать отдельно, оно остаётся, когда главное окно свёрнуто в трей, и наоборот.
// Превью берутся у оболочки Windows (IShellItemImageFactory) — те же, что показывает Проводник, поэтому у видео
// есть кадр, а не значок. Рисуются в фоновом STA-потоке, интерфейс на них не ждёт.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    internal enum GalleryKind { All, Shots, Videos }

    internal sealed class GalleryItem
    {
        public string Path = "";
        public string Name = "";
        public string Folder = "";          // подпапка программы («Chrome», «Desktop»…), если раскладка по программам
        public DateTime Written;
        public long Size;
        public bool Video;
        public bool Selected;
    }

    // ------------------------------------------------------------------ //
    //  Что лежит в папках снимков и видео
    // ------------------------------------------------------------------ //
    internal static class GalleryLibrary
    {
        public const int MaxItems = 4000;          // столько показываем; остальное — только через Проводник
        private const int MaxScan = 20000;         // предохранитель на случай папки, выбранной по ошибке (весь диск)
        private const int MaxDepth = 4;

        public static List<GalleryItem> Read(CapSettings s)
        {
            Dictionary<string, GalleryItem> byPath = new Dictionary<string, GalleryItem>(StringComparer.OrdinalIgnoreCase);
            Collect(s.EffectiveShotFolder, byPath);
            Collect(s.EffectiveVideoFolder, byPath);
            List<GalleryItem> list = new List<GalleryItem>(byPath.Values);
            list.Sort(delegate(GalleryItem a, GalleryItem b)
            {
                int byDate = b.Written.CompareTo(a.Written);
                return byDate != 0 ? byDate : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            });
            if (list.Count > MaxItems) list.RemoveRange(MaxItems, list.Count - MaxItems);
            return list;
        }

        // Раздел окна: «Всё» — как есть, «Снимки» и «Видео» — по типу файла.
        public static List<GalleryItem> Filter(IList<GalleryItem> all, GalleryKind kind)
        {
            List<GalleryItem> view = new List<GalleryItem>();
            foreach (GalleryItem it in all)
                if (kind == GalleryKind.All || (kind == GalleryKind.Videos) == it.Video) view.Add(it);
            return view;
        }

        public static int CountVideos(IList<GalleryItem> all)
        {
            int n = 0;
            foreach (GalleryItem it in all) if (it.Video) n++;
            return n;
        }

        // Обход с ограничением глубины; точки повторного разбора не проходим — папка снимков не должна уводить
        // обход в чужое дерево через ссылку.
        private static void Collect(string root, Dictionary<string, GalleryItem> map)
        {
            if (string.IsNullOrEmpty(root)) return;
            List<DirectoryInfo> level = new List<DirectoryInfo>();
            try
            {
                DirectoryInfo start = new DirectoryInfo(root);
                if (!start.Exists) return;
                level.Add(start);
            }
            catch { return; }

            for (int depth = 0; depth < MaxDepth && level.Count > 0 && map.Count < MaxScan; depth++)
            {
                List<DirectoryInfo> next = new List<DirectoryInfo>();
                foreach (DirectoryInfo dir in level)
                {
                    if (map.Count >= MaxScan) break;
                    FileInfo[] files;
                    try { files = dir.GetFiles(); }
                    catch { continue; }
                    foreach (FileInfo f in files)
                    {
                        bool video = ImageStore.IsVideo(f.Name);
                        if (!video && !ImageStore.IsImage(f.Name)) continue;
                        GalleryItem it = new GalleryItem();
                        it.Path = f.FullName;
                        it.Name = f.Name;
                        it.Folder = string.Equals(dir.FullName, root, StringComparison.OrdinalIgnoreCase) ? "" : dir.Name;
                        it.Video = video;
                        try { it.Written = f.LastWriteTime; it.Size = f.Length; }
                        catch { }
                        map[it.Path] = it;
                        if (map.Count >= MaxScan) break;
                    }
                    if (depth + 1 >= MaxDepth) continue;
                    DirectoryInfo[] subs;
                    try { subs = dir.GetDirectories(); }
                    catch { continue; }
                    foreach (DirectoryInfo sub in subs)
                    {
                        try { if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                        catch { continue; }
                        next.Add(sub);
                    }
                }
                level = next;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Превью оболочки Windows
    // ------------------------------------------------------------------ //
    internal static class ShellThumb
    {
        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage(SIZE size, int flags, out IntPtr bitmap);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int cx, cy; }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bind,
                                                               [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        private const int ResizeToFit = 0x00;
        private const int ThumbnailOnly = 0x08;
        private static readonly Guid FactoryIid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        // Сначала настоящее превью (кадр видео, уменьшенная картинка); если его нет — значок типа файла.
        public static Image Get(string path, int size)
        {
            Image img = Ask(path, size, ThumbnailOnly);
            return img ?? Ask(path, size, ResizeToFit);
        }

        private static Image Ask(string path, int size, int flags)
        {
            object shellItem = null;
            IntPtr bitmap = IntPtr.Zero;
            try
            {
                Guid iid = FactoryIid;
                SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out shellItem);
                IShellItemImageFactory factory = shellItem as IShellItemImageFactory;
                if (factory == null) return null;
                SIZE box;
                box.cx = size;
                box.cy = size;
                factory.GetImage(box, flags, out bitmap);
                if (bitmap == IntPtr.Zero) return null;
                // FromHbitmap копирует пиксели, поэтому дескриптор оболочки освобождаем сразу.
                using (Bitmap shell = Image.FromHbitmap(bitmap)) return new Bitmap(shell);
            }
            catch { return null; }
            finally
            {
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (shellItem != null) Marshal.ReleaseComObject(shellItem);
            }
        }
    }

    // Очередь превью: интерфейс просит картинки для видимых плиток, фоновый поток их готовит.
    internal sealed class ThumbCache : IDisposable
    {
        private const int Cap = 600;                 // ~50 МБ на превью; выше — вытесняем невидимые

        private readonly Control _ui;
        private readonly int _px;
        private readonly object _gate = new object();
        private readonly Dictionary<string, Image> _done = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new List<string>();
        private readonly List<string> _queue = new List<string>();
        private readonly Dictionary<string, bool> _seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private readonly Thread _worker;
        private volatile bool _stop;

        public ThumbCache(Control ui, int px)
        {
            _ui = ui;
            _px = px;
            _worker = new Thread(Work);
            _worker.IsBackground = true;
            _worker.Name = "capture-thumbs";
            // Оболочка отдаёт превью только из STA-потока.
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        public Image Get(string path)
        {
            lock (_gate)
            {
                Image img;
                return _done.TryGetValue(path, out img) ? img : null;
            }
        }

        // Вызывается из отрисовки: что видно — то и готовим, остальное вытесняем.
        public void Want(IList<string> visible)
        {
            bool any = false;
            lock (_gate)
            {
                foreach (string path in visible)
                {
                    if (_seen.ContainsKey(path)) continue;
                    _seen[path] = true;
                    _queue.Add(path);
                    any = true;
                }
                if (_done.Count > Cap) Evict(visible);
            }
            if (any) _wake.Set();
        }

        // Под _gate. Освобождать можно только то, чего нет на экране: рисование уже закончилось.
        private void Evict(IList<string> visible)
        {
            Dictionary<string, bool> keep = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in visible) keep[path] = true;
            int target = Cap * 3 / 4;
            for (int i = 0; i < _order.Count && _done.Count > target; )
            {
                string path = _order[i];
                if (keep.ContainsKey(path)) { i++; continue; }
                Image img;
                if (_done.TryGetValue(path, out img))
                {
                    _done.Remove(path);
                    if (img != null) img.Dispose();
                }
                _seen.Remove(path);
                _order.RemoveAt(i);
            }
        }

        public void Forget()
        {
            lock (_gate)
            {
                foreach (Image img in _done.Values) if (img != null) img.Dispose();
                _done.Clear();
                _order.Clear();
                _seen.Clear();
                _queue.Clear();
            }
        }

        private void Work()
        {
            while (!_stop)
            {
                string path = null;
                lock (_gate)
                {
                    // С конца: последними просят то, что сейчас на экране.
                    if (_queue.Count > 0)
                    {
                        path = _queue[_queue.Count - 1];
                        _queue.RemoveAt(_queue.Count - 1);
                    }
                }
                if (path == null) { _wake.WaitOne(400); continue; }
                Image img = null;
                try { if (File.Exists(path)) img = ShellThumb.Get(path, _px); }
                catch (Exception ex) { CapLog.Report(ex); }
                lock (_gate)
                {
                    // null тоже запоминаем: файл без превью не должен опрашиваться на каждой отрисовке.
                    if (!_done.ContainsKey(path)) _order.Add(path);
                    _done[path] = img;
                }
                Ping();
            }
        }

        private void Ping()
        {
            try
            {
                if (_stop || _ui == null || _ui.IsDisposed || !_ui.IsHandleCreated) return;
                _ui.BeginInvoke(new MethodInvoker(delegate
                {
                    if (!_ui.IsDisposed) _ui.Invalidate();
                }));
            }
            catch { }
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            try { _worker.Join(300); }
            catch { }
            Forget();
            _wake.Close();
        }
    }

    // ------------------------------------------------------------------ //
    //  Три раздела
    // ------------------------------------------------------------------ //
    internal sealed class GalleryTabs : Control
    {
        private readonly string[] _titles = { Tr.S("Всё", "Everything"), Tr.S("Снимки", "Screenshots"), Tr.S("Видео", "Videos") };
        private readonly int[] _counts = new int[3];
        private readonly Rectangle[] _boxes = new Rectangle[3];
        private GalleryKind _kind = GalleryKind.All;
        private int _hot = -1;
        private float _scale = 1f;
        private Font _font;

        public event Action Changed;

        public GalleryTabs()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = EditorColors.Back;
            ApplyScale(1f);
        }

        public GalleryKind Kind
        {
            get { return _kind; }
            set { if (_kind == value) return; _kind = value; Invalidate(); if (Changed != null) Changed(); }
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_font != null) _font.Dispose();
            _font = new Font("Segoe UI", 10f * scale, FontStyle.Regular, GraphicsUnit.Point);
            Height = (int)Math.Round(44 * scale);
            Invalidate();
        }

        public void SetCounts(int all, int shots, int videos)
        {
            _counts[0] = all;
            _counts[1] = shots;
            _counts[2] = videos;
            Invalidate();
        }

        private string Caption(int i) { return _titles[i] + "  " + _counts[i].ToString(CultureInfo.InvariantCulture); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(EditorColors.Back)) g.FillRectangle(back, ClientRectangle);
            int x = (int)Math.Round(14 * _scale);
            int pad = (int)Math.Round(16 * _scale);
            for (int i = 0; i < 3; i++)
            {
                Size size = TextRenderer.MeasureText(g, Caption(i), _font);
                Rectangle box = new Rectangle(x, 0, size.Width + pad * 2, Height);
                _boxes[i] = box;
                bool active = (int)_kind == i;
                if (active || _hot == i)
                {
                    using (SolidBrush b = new SolidBrush(active ? Color.FromArgb(48, 48, 48) : Color.FromArgb(40, 40, 40)))
                        g.FillRectangle(b, box);
                }
                TextRenderer.DrawText(g, Caption(i), _font, box, active ? EditorColors.Text : EditorColors.Dim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (active)
                    using (SolidBrush b = new SolidBrush(EditorColors.Accent))
                        g.FillRectangle(b, box.X, box.Bottom - (int)Math.Round(3 * _scale), box.Width, (int)Math.Round(3 * _scale));
                x = box.Right + (int)Math.Round(4 * _scale);
            }
            using (Pen p = new Pen(EditorColors.Line)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        private int At(Point p)
        {
            for (int i = 0; i < 3; i++) if (_boxes[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = At(e.Location);
            if (i == _hot) return;
            _hot = i;
            Cursor = i >= 0 ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = -1;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            int i = At(e.Location);
            if (i >= 0) Kind = (GalleryKind)i;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _font != null) { _font.Dispose(); _font = null; }
            base.Dispose(disposing);
        }
    }

    // ------------------------------------------------------------------ //
    //  Сетка плиток
    // ------------------------------------------------------------------ //
    internal sealed class GalleryGrid : Panel
    {
        private readonly List<GalleryItem> _items = new List<GalleryItem>();
        private readonly ThumbCache _thumbs;
        private readonly List<string> _visible = new List<string>();
        private float _scale = 1f;
        private Font _nameFont, _metaFont, _emptyFont;
        private int _anchor = -1;
        private int _hot = -1;
        private Point _down;
        private bool _banding, _mayDrag;
        private Rectangle _band;
        private bool[] _bandBase = new bool[0];

        public event Action SelectionChanged;
        public event Action<GalleryItem> Activated;
        public event Action CopyRequested;
        public event Action DeleteRequested;

        public GalleryGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            DoubleBuffered = true;
            TabStop = true;
            AutoScroll = true;
            BackColor = EditorColors.Canvas;
            ApplyScale(1f);
            _thumbs = new ThumbCache(this, 256);
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        private int Pad { get { return S(14); } }
        private int TileW { get { return S(196); } }
        private int TileH { get { return S(178); } }
        private int ThumbH { get { return S(118); } }

        private int Columns
        {
            get
            {
                int usable = ClientSize.Width - Pad;
                return Math.Max(1, usable / (TileW + Pad));
            }
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_nameFont != null) _nameFont.Dispose();
            if (_metaFont != null) _metaFont.Dispose();
            if (_emptyFont != null) _emptyFont.Dispose();
            _nameFont = new Font("Segoe UI", 9f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _metaFont = new Font("Segoe UI", 8f * scale, FontStyle.Regular, GraphicsUnit.Point);
            _emptyFont = new Font("Segoe UI", 11f * scale, FontStyle.Regular, GraphicsUnit.Point);
            Relayout();
        }

        public int Count { get { return _items.Count; } }

        public void SetItems(List<GalleryItem> items)
        {
            _items.Clear();
            _items.AddRange(items);
            if (_anchor >= _items.Count) _anchor = -1;
            _hot = -1;
            Relayout();
            Invalidate();
            Raise();
        }

        public List<GalleryItem> Selected()
        {
            List<GalleryItem> list = new List<GalleryItem>();
            foreach (GalleryItem it in _items) if (it.Selected) list.Add(it);
            return list;
        }

        public void SelectPath(string path)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (!string.Equals(_items[i].Path, path, StringComparison.OrdinalIgnoreCase)) continue;
                SelectOnly(i);
                _anchor = i;
                EnsureVisible(i);
                Invalidate();
                Raise();
                return;
            }
        }

        public void SelectAll()
        {
            foreach (GalleryItem it in _items) it.Selected = true;
            Invalidate();
            Raise();
        }

        public void ClearSelection()
        {
            foreach (GalleryItem it in _items) it.Selected = false;
            Invalidate();
            Raise();
        }

        private void Raise() { if (SelectionChanged != null) SelectionChanged(); }

        private void Relayout()
        {
            int rows = (_items.Count + Columns - 1) / Columns;
            AutoScrollMinSize = new Size(0, _items.Count == 0 ? 0 : Pad + rows * (TileH + Pad));
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
            Invalidate();
        }

        private Rectangle TileRect(int index)
        {
            int cols = Columns;
            int row = index / cols, col = index % cols;
            return new Rectangle(Pad + col * (TileW + Pad), Pad + row * (TileH + Pad), TileW, TileH);
        }

        private Point Content(Point client)
        {
            return new Point(client.X - AutoScrollPosition.X, client.Y - AutoScrollPosition.Y);
        }

        private int HitTest(Point content)
        {
            int cols = Columns;
            int row = (content.Y - Pad) / (TileH + Pad);
            int col = (content.X - Pad) / (TileW + Pad);
            if (row < 0 || col < 0 || col >= cols) return -1;
            int index = row * cols + col;
            if (index < 0 || index >= _items.Count) return -1;
            return TileRect(index).Contains(content) ? index : -1;
        }

        private void EnsureVisible(int index)
        {
            if (index < 0 || index >= _items.Count) return;
            Rectangle r = TileRect(index);
            int top = -AutoScrollPosition.Y;
            int bottom = top + ClientSize.Height;
            if (r.Top < top) AutoScrollPosition = new Point(0, Math.Max(0, r.Top - Pad));
            else if (r.Bottom > bottom) AutoScrollPosition = new Point(0, Math.Max(0, r.Bottom - ClientSize.Height + Pad));
        }

        // ---- отрисовка ----

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush back = new SolidBrush(EditorColors.Canvas)) g.FillRectangle(back, ClientRectangle);
            if (_items.Count == 0)
            {
                TextRenderer.DrawText(g, Tr.S("Пока ничего нет — сделайте снимок или запись", "Nothing here yet — take a screenshot or record something"),
                                      _emptyFont, ClientRectangle, EditorColors.Dim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                return;
            }
            int cols = Columns;
            int viewTop = -AutoScrollPosition.Y;
            int first = Math.Max(0, ((viewTop - Pad) / (TileH + Pad)) * cols);
            int last = Math.Min(_items.Count - 1, ((viewTop + ClientSize.Height - Pad) / (TileH + Pad) + 1) * cols + cols - 1);
            _visible.Clear();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = first; i <= last; i++)
            {
                DrawTile(g, i);
                _visible.Add(_items[i].Path);
            }
            g.SmoothingMode = SmoothingMode.None;
            if (_banding && _band.Width > 0 && _band.Height > 0)
            {
                Rectangle band = _band;
                band.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(48, EditorColors.Accent)))
                using (Pen p = new Pen(EditorColors.Accent))
                {
                    g.FillRectangle(b, band);
                    g.DrawRectangle(p, band);
                }
            }
            _thumbs.Want(_visible);
        }

        private void DrawTile(Graphics g, int index)
        {
            GalleryItem it = _items[index];
            Rectangle r = TileRect(index);
            r.Offset(AutoScrollPosition.X, AutoScrollPosition.Y);
            using (SolidBrush b = new SolidBrush(it.Selected ? Color.FromArgb(38, 54, 82) : Color.FromArgb(40, 40, 40)))
                g.FillRectangle(b, r);
            if (it.Selected)
            {
                using (Pen p = new Pen(EditorColors.Accent, 2f)) g.DrawRectangle(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            }
            else if (index == _hot)
            {
                using (Pen p = new Pen(Color.FromArgb(88, 88, 88))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }

            Rectangle box = new Rectangle(r.X + S(8), r.Y + S(8), r.Width - S(16), ThumbH);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 26, 26))) g.FillRectangle(b, box);
            Image thumb = _thumbs.Get(it.Path);
            if (thumb != null)
            {
                Rectangle fit = Fit(thumb.Size, box);
                g.DrawImage(thumb, fit);
            }
            else
            {
                string ext = (Path.GetExtension(it.Name) ?? "").TrimStart('.').ToUpperInvariant();
                TextRenderer.DrawText(g, ext, _metaFont, box, EditorColors.Dim,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            if (it.Video) DrawPlay(g, box);

            Rectangle nameRect = new Rectangle(r.X + S(10), box.Bottom + S(6), r.Width - S(20), S(18));
            TextRenderer.DrawText(g, it.Name, _nameFont, nameRect, EditorColors.Text,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            string meta = it.Written.ToString("dd.MM.yy HH:mm", CultureInfo.CurrentCulture) + "  ·  " + Bytes(it.Size);
            if (it.Folder.Length > 0) meta = it.Folder + "  ·  " + meta;
            Rectangle metaRect = new Rectangle(nameRect.X, nameRect.Bottom, nameRect.Width, S(16));
            TextRenderer.DrawText(g, meta, _metaFont, metaRect, EditorColors.Dim,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        private void DrawPlay(Graphics g, Rectangle box)
        {
            int d = S(26);
            Rectangle circle = new Rectangle(box.X + S(6), box.Bottom - d - S(6), d, d);
            using (SolidBrush b = new SolidBrush(Color.FromArgb(170, 0, 0, 0))) g.FillEllipse(b, circle);
            PointF[] tri =
            {
                new PointF(circle.X + d * 0.38f, circle.Y + d * 0.28f),
                new PointF(circle.X + d * 0.38f, circle.Y + d * 0.72f),
                new PointF(circle.X + d * 0.74f, circle.Y + d * 0.5f)
            };
            using (SolidBrush b = new SolidBrush(Color.FromArgb(235, 235, 235))) g.FillPolygon(b, tri);
        }

        private static Rectangle Fit(Size src, Rectangle box)
        {
            if (src.Width <= 0 || src.Height <= 0) return box;
            double k = Math.Min((double)box.Width / src.Width, (double)box.Height / src.Height);
            if (k > 1) k = 1;                                   // мелкое превью не растягиваем
            int w = Math.Max(1, (int)Math.Round(src.Width * k));
            int h = Math.Max(1, (int)Math.Round(src.Height * k));
            return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        internal static string Bytes(long n)
        {
            if (n < 0) return "?";
            string[] units = { Tr.S("Б", "B"), Tr.S("КБ", "KB"), Tr.S("МБ", "MB"), Tr.S("ГБ", "GB") };
            double v = n;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0", CultureInfo.CurrentCulture) : v.ToString("0.0", CultureInfo.CurrentCulture)) + " " + units[u];
        }

        // ---- мышь ----

        private void SelectOnly(int index)
        {
            for (int i = 0; i < _items.Count; i++) _items[i].Selected = i == index;
        }

        private void SelectRange(int from, int to)
        {
            int a = Math.Min(from, to), b = Math.Max(from, to);
            for (int i = 0; i < _items.Count; i++) _items[i].Selected = i >= a && i <= b;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            Point p = Content(e.Location);
            _down = p;
            _banding = false;
            _mayDrag = false;
            int index = HitTest(p);
            bool ctrl = (ModifierKeys & Keys.Control) != 0;
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (index >= 0)
            {
                if (ctrl) { _items[index].Selected = !_items[index].Selected; _anchor = index; }
                else if (shift && _anchor >= 0) SelectRange(_anchor, index);
                else
                {
                    // Правая кнопка и перетаскивание не должны сбрасывать уже набранное выделение.
                    if (!_items[index].Selected) SelectOnly(index);
                    _anchor = index;
                    _mayDrag = e.Button == MouseButtons.Left;
                }
                Invalidate();
                Raise();
            }
            else if (e.Button == MouseButtons.Left)
            {
                if (!ctrl) foreach (GalleryItem it in _items) it.Selected = false;
                _bandBase = new bool[_items.Count];
                for (int i = 0; i < _items.Count; i++) _bandBase[i] = _items[i].Selected;
                _banding = true;
                _band = Rectangle.Empty;
                Invalidate();
                Raise();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = Content(e.Location);
            if (_banding)
            {
                _band = Rectangle.FromLTRB(Math.Min(_down.X, p.X), Math.Min(_down.Y, p.Y), Math.Max(_down.X, p.X), Math.Max(_down.Y, p.Y));
                for (int i = 0; i < _items.Count; i++)
                {
                    bool inside = TileRect(i).IntersectsWith(_band);
                    _items[i].Selected = (i < _bandBase.Length && _bandBase[i]) || inside;
                }
                Invalidate();
                Raise();
                return;
            }
            if (_mayDrag && e.Button == MouseButtons.Left)
            {
                if (Math.Abs(p.X - _down.X) > S(5) || Math.Abs(p.Y - _down.Y) > S(5))
                {
                    _mayDrag = false;
                    StartDrag();
                    return;
                }
            }
            int index = HitTest(p);
            if (index == _hot) return;
            _hot = index;
            Invalidate();
        }

        // Перетаскивание в другую программу: то же, что перетащить файлы из Проводника.
        private void StartDrag()
        {
            List<GalleryItem> picked = Selected();
            if (picked.Count == 0) return;
            StringCollection files = new StringCollection();
            foreach (GalleryItem it in picked) files.Add(it.Path);
            DataObject data = new DataObject();
            data.SetFileDropList(files);
            try { DoDragDrop(data, DragDropEffects.Copy); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            _banding = false;
            _mayDrag = false;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot < 0) return;
            _hot = -1;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int index = HitTest(Content(e.Location));
            if (index >= 0 && Activated != null) Activated(_items[index]);
        }

        // ---- клавиатура ----

        protected override bool IsInputKey(Keys key)
        {
            Keys code = key & Keys.KeyCode;
            if (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down
                || code == Keys.Home || code == Keys.End || code == Keys.Enter) return true;
            return base.IsInputKey(key);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Control && e.KeyCode == Keys.A) { SelectAll(); e.Handled = true; return; }
            if (e.Control && e.KeyCode == Keys.C) { if (CopyRequested != null) CopyRequested(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Delete) { if (DeleteRequested != null) DeleteRequested(); e.Handled = true; return; }
            if (e.KeyCode == Keys.Enter)
            {
                List<GalleryItem> picked = Selected();
                if (picked.Count > 0 && Activated != null) Activated(picked[0]);
                e.Handled = true;
                return;
            }
            int step = 0;
            switch (e.KeyCode)
            {
                case Keys.Left: step = -1; break;
                case Keys.Right: step = 1; break;
                case Keys.Up: step = -Columns; break;
                case Keys.Down: step = Columns; break;
                case Keys.Home: step = int.MinValue; break;
                case Keys.End: step = int.MaxValue; break;
                default: return;
            }
            if (_items.Count == 0) return;
            int current = _anchor >= 0 ? _anchor : 0;
            int next = step == int.MinValue ? 0 : step == int.MaxValue ? _items.Count - 1 : current + step;
            next = Math.Max(0, Math.Min(_items.Count - 1, next));
            if (e.Shift && _anchor >= 0) SelectRange(_anchor, next);
            else { SelectOnly(next); _anchor = next; }
            EnsureVisible(next);
            Invalidate();
            Raise();
            e.Handled = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _thumbs.Dispose();
                if (_nameFont != null) { _nameFont.Dispose(); _nameFont = null; }
                if (_metaFont != null) { _metaFont.Dispose(); _metaFont = null; }
                if (_emptyFont != null) { _emptyFont.Dispose(); _emptyFont = null; }
            }
            base.Dispose(disposing);
        }
    }
}
