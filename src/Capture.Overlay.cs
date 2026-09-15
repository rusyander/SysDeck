// SysDeck — «Захват»: выделение области поверх замороженного экрана.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Одно окно без рамки на весь виртуальный экран. Кадр снят заранее, окно рисует его затемнённым, а выделение —
// исходными пикселями. Перерисовываются только изменившиеся прямоугольники (рамка, подпись, лупа, панель): полный
// кадр трёх мониторов — это 7040x1447, и перерисовка его целиком на каждое движение мыши заметно тормозила бы.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    internal enum OverlayAction { Cancel, Default, Save, Copy, Edit, Record }

    internal sealed class OverlayResult
    {
        public OverlayAction Action;
        public Rectangle Area;      // координаты виртуального экрана
        public string App;
        public IntPtr Window;       // выбрано окно щелчком (и область не меняли) — запись видео пишет само окно
        public int Pid;
        public EditorDoc Doc;       // нарисованное поверх выделения; переходит во владение подписчика Finished
    }

    internal static class CapDpi
    {
        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        // Масштаб монитора в точке (1.0 = 96 DPI). Windows 8.1+; раньше — 1.0.
        public static float ScaleAt(Point p)
        {
            try
            {
                CapNative.POINT pt = new CapNative.POINT();
                pt.X = p.X; pt.Y = p.Y;
                IntPtr mon = CapNative.MonitorFromPoint(pt, CapNative.MONITOR_DEFAULTTONEAREST);
                uint x, y;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0) return x / 96f;
            }
            catch { }
            return 1f;
        }
    }

    // Чистая геометрия выделения — отдельно от окна, чтобы её можно было проверить тестами.
    internal static class RegionMath
    {
        public static Rectangle Normalize(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);
        }

        // Прямоугольник от якоря к точке с заданным соотношением сторон (ширина/высота); растёт в сторону курсора.
        public static Rectangle WithRatio(Point anchor, Point p, double ratio)
        {
            if (ratio <= 0) return Normalize(anchor, p);
            int dx = p.X - anchor.X, dy = p.Y - anchor.Y;
            int w = Math.Abs(dx) + 1, h = Math.Abs(dy) + 1;
            if (w / (double)h > ratio) w = Math.Max(1, (int)Math.Round(h * ratio));
            else h = Math.Max(1, (int)Math.Round(w / ratio));
            int x = dx >= 0 ? anchor.X : anchor.X - w + 1;
            int y = dy >= 0 ? anchor.Y : anchor.Y - h + 1;
            return new Rectangle(x, y, w, h);
        }

        // Сдвиг, не выпускающий прямоугольник за границы.
        public static Rectangle MoveWithin(Rectangle r, int dx, int dy, Rectangle bounds)
        {
            int x = Math.Max(bounds.Left, Math.Min(bounds.Right - r.Width, r.X + dx));
            int y = Math.Max(bounds.Top, Math.Min(bounds.Bottom - r.Height, r.Y + dy));
            return new Rectangle(x, y, r.Width, r.Height);
        }

        // Для видео: чётные размеры (NV12 и H.264), не больше исходного и не меньше 2.
        public static Size EvenSize(Size s)
        {
            return new Size(Math.Max(2, s.Width & ~1), Math.Max(2, s.Height & ~1));
        }

        // Ручки: 0..7 — углы и середины сторон по часовой от левого верхнего; -1 — не на ручке.
        public static Rectangle[] Handles(Rectangle r, int size)
        {
            int h = size / 2;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            Point[] pts =
            {
                new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right - 1, r.Top), new Point(r.Right - 1, cy),
                new Point(r.Right - 1, r.Bottom - 1), new Point(cx, r.Bottom - 1), new Point(r.Left, r.Bottom - 1), new Point(r.Left, cy)
            };
            Rectangle[] result = new Rectangle[8];
            for (int i = 0; i < 8; i++) result[i] = new Rectangle(pts[i].X - h, pts[i].Y - h, size, size);
            return result;
        }

        public static Rectangle Resize(Rectangle start, int handle, int dx, int dy, Rectangle bounds)
        {
            int l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
            switch (handle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: r += dx; t += dy; break;
                case 3: r += dx; break;
                case 4: r += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
            }
            Rectangle n = Rectangle.FromLTRB(Math.Min(l, r - 1), Math.Min(t, b - 1), Math.Max(r, l + 1), Math.Max(b, t + 1));
            return Rectangle.Intersect(n, bounds);
        }
    }

    internal sealed partial class RegionOverlay : Form
    {
        private enum Mode { Idle, Pressing, Dragging, Selected, Moving, Resizing }

        private enum PanelKind { Action, Tool, Color, CustomColor, Undo, Redo, Delete }

        private sealed class PanelButton
        {
            public PanelKind Kind;
            public OverlayAction Action;
            public EditTool Tool;
            public Color Color;
            public string Glyph;
            public string Tip;
            public string Caption;      // подписанная кнопка (главные действия); null — только значок
            public bool Primary;        // залита акцентом, как главная кнопка страниц программы
            public int Group;
            public Rectangle Bounds;    // пусто — кнопка сейчас не показана
        }

        // Цвета на панели оверлея — часть палитры редактора; остальные — «Другой цвет…».
        private static readonly Color[] InlinePalette = { EditorBar.Palette[0], EditorBar.Palette[2], EditorBar.Palette[3], EditorBar.Palette[4], EditorBar.Palette[7] };

        public event Action<RegionOverlay, OverlayResult> Finished;

        private readonly Bitmap _frame;       // исходный кадр: 32bppRgb снимка экрана или PArgb
        private readonly SolidBrush _shade = new SolidBrush(Color.FromArgb(110, 0, 0, 0));   // затемнение вне выделения
        private readonly Point _origin;
        private readonly List<MonitorInfo> _monitors;
        private readonly List<WindowCandidate> _windows;
        private readonly Rectangle _lastRegion;
        private readonly float _scale;
        private readonly Font _labelFont, _hintFont, _glyphFont, _captionFont;
        private bool _captions = true;              // панель не влезла в монитор с подписями — подписи убираются
        private readonly double[] _ratios = { 1.0, 16.0 / 9.0, 4.0 / 3.0, 21.0 / 9.0 };
        private readonly string[] _ratioNames = { "1:1", "16:9", "4:3", "21:9" };
        private readonly List<PanelButton> _buttons = new List<PanelButton>();
        private readonly Timer _flashTimer = new Timer();
        private readonly Bitmap _measureBitmap = new Bitmap(1, 1);
        private readonly Graphics _measure;

        private Mode _mode = Mode.Idle;
        private Rectangle _sel = Rectangle.Empty;   // клиентские координаты (= виртуальные минус _origin)
        private Rectangle _hover = Rectangle.Empty;
        private Rectangle _dragStartRect;
        private Point _anchor, _pressAt, _mouse;
        private int _handle = -1, _ratioIndex, _hotButton = -1;
        private bool _shift, _space, _done;
        private readonly bool _video;               // выделение для записи видео: Enter — «Начать запись»
        private Rectangle _windowSel = Rectangle.Empty;
        private WindowCandidate _windowPick;
        private string _flash;
        private DibBuffer _buffer;

        // Правки прямо на выделении: холст появляется при выборе инструмента и закрывает выделение собой.
        private EditorStyle _style = new EditorStyle();
        private EditorDoc _doc;
        private EditorCanvas _canvas;
        private Color _customColor = Color.FromArgb(0, 188, 212);
        private Rectangle _cutPanel, _cutTip;
        private bool _modal;

        public RegionOverlay(Bitmap frame, Rectangle virtualBounds, List<MonitorInfo> monitors, List<WindowCandidate> windows, Rectangle lastRegion)
            : this(frame, virtualBounds, monitors, windows, lastRegion, false)
        {
        }

        public RegionOverlay(Bitmap frame, Rectangle virtualBounds, List<MonitorInfo> monitors, List<WindowCandidate> windows, Rectangle lastRegion,
                             bool video)
        {
            _video = video;
            _origin = virtualBounds.Location;
            _monitors = monitors;
            _windows = windows;
            _lastRegion = lastRegion;
            // Снимок экрана (32bppRgb) рисуется как есть: копия в PArgb и заранее затемнённый второй кадр стоили ~90 мс
            // до появления оверлея на кадре всех мониторов (23 Мп) и вдвое больше памяти. Затемнение кладётся при рисовании.
            if (frame.PixelFormat == PixelFormat.Format32bppRgb || frame.PixelFormat == PixelFormat.Format32bppPArgb) _frame = frame;
            else
            {
                _frame = ToPArgb(frame);
                frame.Dispose();
            }

            _mouse = PointToClientCoords(CapNative.CursorPosition());
            _scale = Math.Max(1f, CapDpi.ScaleAt(CapNative.CursorPosition()));
            _labelFont = new Font("Segoe UI", 12f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _hintFont = new Font("Segoe UI", 15f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _glyphFont = CapFonts.Icons(16f * _scale);
            _captionFont = new Font("Segoe UI Semibold", 13.5f * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _measure = Graphics.FromImage(_measureBitmap);
            _measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "SysDeck — capture";
            Bounds = virtualBounds;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
            SetStyle(ControlStyles.OptimizedDoubleBuffer, false);

            // Главные действия — подписанные кнопки в конце панели: «Сохранить» (или «Начать запись») залита акцентом,
            // «Отмена» рядом. Значки без подписи остаются у второстепенных действий.
            if (_video)
                AddCaptioned(OverlayAction.Record, CapFonts.Glyph(CapFonts.Record, "●"), Tr.S("Начать запись", "Start recording"),
                             Tr.S("Начать запись (Enter)", "Start recording (Enter)"), true);
            else
            {
                if (CapFeatures.Editor)
                {
                    AddTool(EditTool.Rect, Tr.S("Рамка · Shift — квадрат", "Box · Shift for a square"));
                    AddTool(EditTool.Ellipse, Tr.S("Овал · Shift — круг", "Ellipse · Shift for a circle"));
                    AddTool(EditTool.Arrow, Tr.S("Стрелка · Shift — шаг 45°", "Arrow · Shift snaps to 45°"));
                    AddTool(EditTool.Line, Tr.S("Линия · Shift — шаг 45°", "Line · Shift snaps to 45°"));
                    AddTool(EditTool.Pen, Tr.S("Карандаш", "Pen"));
                    AddTool(EditTool.Marker, Tr.S("Маркер", "Highlighter"));
                    AddTool(EditTool.Text, Tr.S("Текст · Enter — готово, Shift+Enter — новая строка", "Text · Enter to finish, Shift+Enter for a new line"));
                    AddTool(EditTool.Step, Tr.S("Номер шага", "Step number"));
                    foreach (Color c in InlinePalette) Add(PanelKind.Color, 1, null, Tr.S("Цвет", "Color")).Color = c;
                    Add(PanelKind.CustomColor, 1, null, Tr.S("Другой цвет…", "Another color…"));
                    Add(PanelKind.Undo, 2, null, Tr.S("Отменить (Ctrl+Z)", "Undo (Ctrl+Z)"));
                    Add(PanelKind.Redo, 2, null, Tr.S("Повторить (Ctrl+Y)", "Redo (Ctrl+Y)"));
                    Add(PanelKind.Delete, 2, CapFonts.Glyph(CapFonts.Delete, "⌫"), Tr.S("Удалить выделенную фигуру (Delete) · щелчок по фигуре — выделить",
                                                                                        "Delete the selected shape (Delete) · click a shape to select it"));
                }
                AddAction(OverlayAction.Copy, CapFonts.Glyph(CapFonts.Copy, "⧉"), Tr.S("Копировать (Ctrl+C)", "Copy (Ctrl+C)"));
                if (CapFeatures.Editor)
                    AddAction(OverlayAction.Edit, CapFonts.Glyph(CapFonts.Edit, "✎"),
                              Tr.S("Открыть в редакторе: размытие, обрезка, толщина (E)", "Open in the editor: blur, crop, line width (E)"));
                if (CapFeatures.Video) AddAction(OverlayAction.Record, CapFonts.Glyph(CapFonts.Record, "●"), Tr.S("Записать видео области (R)", "Record this region (R)"));
                AddCaptioned(OverlayAction.Save, CapFonts.Glyph(CapFonts.Save, "↓"), Tr.S("Сохранить", "Save"),
                             Tr.S("Сохранить (Enter)", "Save (Enter)"), true);
            }
            AddCaptioned(OverlayAction.Cancel, CapFonts.Glyph(CapFonts.Close, "×"), Tr.S("Отмена", "Cancel"), Tr.S("Закрыть без сохранения (Esc)", "Close without saving (Esc)"), false);

            _flashTimer.Interval = 1400;
            _flashTimer.Tick += delegate { _flashTimer.Stop(); Change(delegate { _flash = null; }); };
            UpdateHover();
        }

        public Bitmap Frame { get { return _frame; } }
        public Point Origin { get { return _origin; } }
        public List<MonitorInfo> Monitors { get { return _monitors; } }

        // Цвет и инструмент — из настроек редактора; изменённые на панели агент сохраняет обратно.
        public EditorStyle EditStyle
        {
            get { return _style; }
            set
            {
                _style = value ?? new EditorStyle();
                if (!InPalette(_style.Color)) _customColor = _style.Color;
            }
        }

        private PanelButton Add(PanelKind kind, int group, string glyph, string tip)
        {
            PanelButton b = new PanelButton();
            b.Kind = kind;
            b.Group = group;
            b.Glyph = glyph;
            b.Tip = tip;
            _buttons.Add(b);
            return b;
        }

        private void AddTool(EditTool tool, string tip) { Add(PanelKind.Tool, 0, null, tip).Tool = tool; }

        private void AddAction(OverlayAction action, string glyph, string tip) { Add(PanelKind.Action, 3, glyph, tip).Action = action; }

        private void AddCaptioned(OverlayAction action, string glyph, string caption, string tip, bool primary)
        {
            PanelButton b = Add(PanelKind.Action, 4, glyph, tip);
            b.Action = action;
            b.Caption = caption;
            b.Primary = primary;
        }

        private static bool InPalette(Color c)
        {
            foreach (Color p in InlinePalette) if (p.ToArgb() == c.ToArgb()) return true;
            return false;
        }

        private static Bitmap ToPArgb(Bitmap source)
        {
            Bitmap b = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(source, 0, 0);
            }
            return b;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= CapNative.WS_EX_TOOLWINDOW | CapNative.WS_EX_TOPMOST;
                return cp;
            }
        }

        public void ShowAndFocus()
        {
            Show();
            // Form мог урезать размер по SystemInformation.MaxWindowTrackSize: ставим границы напрямую.
            SetWindowPos(Handle, new IntPtr(-1), _origin.X, _origin.Y, _frame.Width, _frame.Height, 0x0040);
            Activate();
            CapNative.SetForegroundWindow(Handle);
            Focus();
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        public void Cancel() { Finish(OverlayAction.Cancel); }

        private Point PointToClientCoords(Point screen) { return new Point(screen.X - _origin.X, screen.Y - _origin.Y); }
        private Rectangle ToClient(Rectangle r) { return new Rectangle(r.X - _origin.X, r.Y - _origin.Y, r.Width, r.Height); }
        private Rectangle ClientBounds { get { return new Rectangle(0, 0, _frame.Width, _frame.Height); } }

        private MonitorInfo MonitorAtClient(Point p)
        {
            return ScreenGrab.MonitorAt(_monitors, new Point(p.X + _origin.X, p.Y + _origin.Y));
        }

        private int Px(float v) { return (int)Math.Round(v * _scale); }

        // ---------- результат ----------

        private void Finish(OverlayAction action)
        {
            if (_done) return;
            _done = true;
            if (_canvas != null) _canvas.CommitText();
            OverlayResult r = new OverlayResult();
            r.Action = action;
            if (action != OverlayAction.Cancel && !_sel.IsEmpty)
            {
                Rectangle area = Rectangle.Intersect(_sel, ClientBounds);
                r.Area = new Rectangle(area.X + _origin.X, area.Y + _origin.Y, area.Width, area.Height);
                WindowCandidate w = WindowPicker.At(_windows, new Point(r.Area.X + r.Area.Width / 2, r.Area.Y + r.Area.Height / 2));
                if (_windowPick != null && _sel == _windowSel && !_windowPick.IsDesktop) w = _windowPick;
                r.App = w == null ? AppNaming.Desktop : AppNaming.ForWindow(w.Handle);
                if (w != null && !w.IsDesktop)
                {
                    r.Pid = w.Pid;
                    if (w == _windowPick && _sel == _windowSel) r.Window = w.Handle;
                }
                if (_doc != null && _doc.Shapes.Count > 0)
                {
                    r.Doc = _doc;
                    _doc = null;
                }
            }
            else r.Action = OverlayAction.Cancel;
            Hide();
            // Подписчик освобождает окно, поэтому вызов — после выхода из обработчика мыши или клавиатуры.
            BeginInvoke((MethodInvoker)delegate
            {
                Action<RegionOverlay, OverlayResult> finished = Finished;
                if (finished != null) finished(this, r);
            });
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            // Переключились в другое окно (Alt+Tab, всплывшее уведомление системы) — выделение отменяется, как у Ножниц.
            // Диалог «Другой цвет…» тоже забирает активность — это не уход из оверлея.
            if (!_done && !_modal) BeginInvoke((MethodInvoker)delegate { if (!_done && !_modal && !ContainsFocus) Finish(OverlayAction.Cancel); });
        }

        // ---------- мышь ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _mouse = e.Location;
            if (e.Button == MouseButtons.Right)
            {
                // С начатыми правками правая кнопка ничего не сбрасывает: нарисованное теряется только по Esc.
                if (Editing) return;
                if (_mode == Mode.Selected) Change(delegate { _sel = Rectangle.Empty; _mode = Mode.Idle; UpdateHover(); });
                else Finish(OverlayAction.Cancel);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (_mode == Mode.Selected)
            {
                int b = ButtonAt(e.Location);
                if (b >= 0) { Press(_buttons[b]); return; }
                int h = HandleAt(e.Location);
                if (h >= 0)
                {
                    if (Editing) _canvas.CommitText();
                    _handle = h; _pressAt = e.Location; _dragStartRect = _sel; _mode = Mode.Resizing;
                    return;
                }
                // Второе нажатие двойного щелчка приходит раньше OnMouseDoubleClick: режим остаётся «выделено», иначе
                // двойной щелчок застал бы перемещение и ничего не сделал.
                if (Editing || (e.Clicks > 1 && _sel.Contains(e.Location))) return;
                if (_sel.Contains(e.Location)) { _pressAt = e.Location; _dragStartRect = _sel; _mode = Mode.Moving; return; }
            }
            Change(delegate
            {
                _anchor = e.Location;
                _pressAt = e.Location;
                _mode = Mode.Pressing;
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Point p = e.Location;
            Change(delegate
            {
                int dx = p.X - _mouse.X, dy = p.Y - _mouse.Y;
                _mouse = p;
                switch (_mode)
                {
                    case Mode.Idle:
                        UpdateHover();
                        break;
                    case Mode.Pressing:
                        if (Math.Abs(p.X - _pressAt.X) > 3 || Math.Abs(p.Y - _pressAt.Y) > 3) { _mode = Mode.Dragging; _hover = Rectangle.Empty; UpdateDrag(dx, dy); }
                        break;
                    case Mode.Dragging:
                        UpdateDrag(dx, dy);
                        break;
                    case Mode.Moving:
                        _sel = RegionMath.MoveWithin(_dragStartRect, p.X - _pressAt.X, p.Y - _pressAt.Y, ClientBounds);
                        break;
                    case Mode.Resizing:
                        _sel = RegionMath.Resize(_dragStartRect, _handle, p.X - _pressAt.X, p.Y - _pressAt.Y, ClientBounds);
                        if (Editing) Reframe();
                        break;
                    case Mode.Selected:
                        _hotButton = ButtonAt(p);
                        break;
                }
            });
            UpdateCursor(p);
        }

        private void UpdateDrag(int dx, int dy)
        {
            // Пробел при растягивании двигает всю область, не меняя размер.
            if (_space) _anchor = new Point(_anchor.X + dx, _anchor.Y + dy);
            Rectangle r = _shift ? RegionMath.WithRatio(_anchor, _mouse, _ratios[_ratioIndex]) : RegionMath.Normalize(_anchor, _mouse);
            _sel = Rectangle.Intersect(r, ClientBounds);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Change(delegate
            {
                switch (_mode)
                {
                    case Mode.Pressing:
                        // Щелчок без протягивания — окно под курсором, а над рабочим столом — монитор целиком.
                        Rectangle target = _hover.IsEmpty ? MonitorRect(e.Location) : _hover;
                        _sel = Rectangle.Intersect(target, ClientBounds);
                        _windowPick = _hover.IsEmpty ? null : WindowPicker.At(_windows, new Point(e.X + _origin.X, e.Y + _origin.Y));
                        _windowSel = _sel;
                        _hover = Rectangle.Empty;
                        _mode = _sel.IsEmpty ? Mode.Idle : Mode.Selected;
                        break;
                    case Mode.Dragging:
                        _mode = _sel.Width >= 2 && _sel.Height >= 2 ? Mode.Selected : Mode.Idle;
                        if (_mode == Mode.Idle) { _sel = Rectangle.Empty; UpdateHover(); }
                        break;
                    case Mode.Moving:
                    case Mode.Resizing:
                        _mode = Mode.Selected;
                        break;
                }
                _hotButton = _mode == Mode.Selected ? ButtonAt(e.Location) : -1;
            });
            UpdateCursor(e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || Editing) return;
            if (_mode == Mode.Selected && _sel.Contains(e.Location) && ButtonAt(e.Location) < 0 && !_sel.Equals(MonitorRect(e.Location)))
            {
                // Двойной щелчок по уже выделенному монитору подтверждает, по другому месту — выделяет монитор.
                Change(delegate { _sel = Rectangle.Intersect(MonitorRect(e.Location), ClientBounds); _mode = Mode.Selected; });
                return;
            }
            if (_mode == Mode.Selected && _sel.Contains(e.Location) && ButtonAt(e.Location) < 0) Finish(_video ? OverlayAction.Record : OverlayAction.Default);
        }

        private Rectangle MonitorRect(Point client)
        {
            MonitorInfo m = MonitorAtClient(client);
            return m == null ? ClientBounds : ToClient(m.Bounds);
        }

        private void UpdateHover()
        {
            WindowCandidate w = WindowPicker.At(_windows, new Point(_mouse.X + _origin.X, _mouse.Y + _origin.Y));
            _hover = w == null ? Rectangle.Empty : Rectangle.Intersect(ToClient(w.Bounds), ClientBounds);
        }

        private void UpdateCursor(Point p)
        {
            Cursor c = Editing ? Cursors.Default : Cursors.Cross;
            if (_mode == Mode.Selected)
            {
                int h = HandleAt(p);
                if (ButtonAt(p) >= 0) c = Cursors.Hand;
                else if (h == 0 || h == 4) c = Cursors.SizeNWSE;
                else if (h == 2 || h == 6) c = Cursors.SizeNESW;
                else if (h == 1 || h == 5) c = Cursors.SizeNS;
                else if (h == 3 || h == 7) c = Cursors.SizeWE;
                else if (_sel.Contains(p) && !Editing) c = Cursors.SizeAll;
            }
            else if (_mode == Mode.Moving) c = Cursors.SizeAll;
            if (Cursor != c) Cursor = c;
        }

        private int HandleAt(Point p)
        {
            if (_sel.IsEmpty) return -1;
            Rectangle[] hs = RegionMath.Handles(_sel, Px(12));
            for (int i = 0; i < hs.Length; i++) if (hs[i].Contains(p)) return i;
            return -1;
        }

        private int ButtonAt(Point p)
        {
            if (_mode != Mode.Selected) return -1;
            for (int i = 0; i < _buttons.Count; i++) if (!_buttons[i].Bounds.IsEmpty && _buttons[i].Bounds.Contains(p)) return i;
            return -1;
        }

        // ---------- клавиатура ----------

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // Набор текста на холсте: Enter, Esc и стрелки достаются полю ввода.
            if (Editing && _canvas.EditingText) return base.ProcessCmdKey(ref msg, keyData);
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;
            switch (key)
            {
                case Keys.Escape:
                    if (Editing && _canvas.Selected != null) { _canvas.Escape(); return true; }
                    Finish(OverlayAction.Cancel);
                    return true;
                case Keys.Enter: if (_mode == Mode.Selected) Finish(_video ? OverlayAction.Record : OverlayAction.Default); return true;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                    if (!Editing) Nudge(key, shift ? 10 : 1, ctrl);
                    return true;
            }
            if (Editing)
                switch (keyData)
                {
                    case Keys.Control | Keys.Z: _canvas.Undo(); return true;
                    case Keys.Control | Keys.Y:
                    case Keys.Control | Keys.Shift | Keys.Z: _canvas.Redo(); return true;
                    case Keys.Delete:
                    case Keys.Back: _canvas.DeleteSelected(); return true;
                }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Editing && _canvas.EditingText) return;
            if (e.KeyCode == Keys.ShiftKey && !_shift) Change(delegate { _shift = true; if (_mode == Mode.Dragging) UpdateDrag(0, 0); });
            else if (e.KeyCode == Keys.Space) _space = true;
            else if (e.KeyCode == Keys.C && e.Control) { if (_mode == Mode.Selected && !_video) Finish(OverlayAction.Copy); }
            else if (e.KeyCode == Keys.S && e.Control) { if (_mode == Mode.Selected && !_video) Finish(OverlayAction.Save); }
            else if (e.KeyCode == Keys.E && CapFeatures.Editor && !_video) { if (_mode == Mode.Selected) Finish(OverlayAction.Edit); }
            else if (e.KeyCode == Keys.R && CapFeatures.Video) { if (_mode == Mode.Selected && !Editing) Finish(OverlayAction.Record); }
            else if (e.KeyCode == Keys.C && !Editing) CopyColor();
            else if (e.KeyCode == Keys.A && !Editing)
                Change(delegate
                {
                    _ratioIndex = (_ratioIndex + 1) % _ratios.Length;
                    if (_mode == Mode.Dragging) UpdateDrag(0, 0);
                    Flash(Tr.S("Пропорции с Shift: ", "Shift ratio: ") + _ratioNames[_ratioIndex]);
                });
            else if (e.KeyCode == Keys.L && !_lastRegion.IsEmpty && !Editing)
                Change(delegate
                {
                    Rectangle r = Rectangle.Intersect(ToClient(_lastRegion), ClientBounds);
                    if (!r.IsEmpty) { _sel = r; _hover = Rectangle.Empty; _mode = Mode.Selected; }
                });
            e.Handled = true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.ShiftKey) Change(delegate { _shift = false; if (_mode == Mode.Dragging) UpdateDrag(0, 0); });
            else if (e.KeyCode == Keys.Space) _space = false;
        }

        // Стрелки: выделение двигается (с Ctrl — растёт вправо и вниз), без выделения — курсор на пиксель.
        private void Nudge(Keys key, int step, bool grow)
        {
            int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
            int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
            if (_mode == Mode.Selected)
            {
                Change(delegate
                {
                    if (grow) _sel = Rectangle.Intersect(new Rectangle(_sel.X, _sel.Y, Math.Max(2, _sel.Width + dx), Math.Max(2, _sel.Height + dy)), ClientBounds);
                    else _sel = RegionMath.MoveWithin(_sel, dx, dy, ClientBounds);
                });
            }
            else
            {
                Point s = CapNative.CursorPosition();
                Cursor.Position = new Point(s.X + dx, s.Y + dy);
            }
        }

        private void CopyColor()
        {
            Color c = PixelAt(_mouse);
            string hex = "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
            try { Clipboard.SetText(hex); } catch { }
            Change(delegate { Flash(Tr.S("Цвет скопирован: ", "Color copied: ") + hex); });
        }

        private void Flash(string text)
        {
            _flash = text;
            _flashTimer.Stop();
            _flashTimer.Start();
        }

        private Color PixelAt(Point p)
        {
            if (!ClientBounds.Contains(p)) return Color.Black;
            return _frame.GetPixel(p.X, p.Y);
        }

        // ---------- перерисовка по изменённым участкам ----------

        private void Change(Action mutate)
        {
            List<Rectangle> before = DecorRects();
            Rectangle litBefore = Highlight;
            mutate();
            LayoutPanel();
            CutCanvas(false);
            List<Rectangle> after = DecorRects();
            Rectangle litAfter = Highlight;
            foreach (Rectangle r in before) Invalidate(r);
            foreach (Rectangle r in after) Invalidate(r);
            // Полосы по краям не покрывают скачок мыши больше их ширины: участок, который вошёл в подсветку или вышел
            // из неё, перерисовывается целиком, иначе там остаётся старое затемнение (или старая яркость).
            if (litBefore != litAfter)
                using (Region changed = new Region(litBefore))
                {
                    changed.Xor(litAfter);
                    Invalidate(changed);
                }
            Update();
        }

        private Rectangle Highlight { get { return _mode == Mode.Idle || _mode == Mode.Pressing ? (_sel.IsEmpty ? _hover : _sel) : _sel; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _flashTimer.Dispose();
                if (_doc != null) _doc.Dispose();
                _frame.Dispose();
                _shade.Dispose();
                if (_buffer != null) _buffer.Dispose();
                _labelFont.Dispose();
                _hintFont.Dispose();
                _glyphFont.Dispose();
                _captionFont.Dispose();
                _measure.Dispose();
                _measureBitmap.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
