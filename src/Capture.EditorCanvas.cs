// SysDeck — «Захват»: холст редактора — масштаб, отрисовка, фигуры и ручки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  Холст: вид (масштаб, прокрутка), рисование, выделение, текст, обрезка
    // ------------------------------------------------------------------ //
    internal sealed partial class EditorCanvas : Control
    {
        private enum Drag { None, Draw, Move, Handle, Pan, CropNew, CropMove, CropHandle }

        private const float MinZoom = 0.05f, MaxZoom = 16f;

        private readonly EditorDoc _doc;
        private readonly EditorStyle _style;
        private float _scale = 1f, _zoom = 1f;
        private bool _fit = true, _space, _moved, _textDirty, _editingNew, _inline;
        private PointF _pan;

        private Bitmap _composite, _scaled;
        private int _compositeVersion = -1;
        private CapShape _compositeSkip;
        private Bitmap _compositeImage;
        private float _scaledZoom;

        private CapShape _selected, _live, _hover;
        private EditSnapshot _before;
        private Drag _drag;
        private PointF _dragStart, _lastImage, _startA, _startB;
        private RectangleF _startRect;
        private int _handle = -1;
        private Point _panMouse, _downClient;
        private PointF _panStart;
        private Rectangle _crop, _cropStart;

        private TextBox _textBox;
        private TextShape _editing;
        private EditSnapshot _textBefore;
        private Font _textFont;
        private Font _labelFont;

        public event Action Changed;
        public event Action ViewChanged;
        public event Action CropApplied;

        public EditorCanvas(EditorDoc doc, EditorStyle style)
        {
            _doc = doc;
            _style = style;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = EditorColors.Canvas;
            ApplyScale(1f);
        }

        public float Zoom { get { return _zoom; } }
        public bool IsFit { get { return _fit; } }
        public bool EditingText { get { return _textBox != null; } }
        internal CapShape Selected { get { return _selected; } }
        internal Rectangle CropArea { get { return _crop; } }

        // Холст поверх выделения в оверлее захвата: снимок 1:1 без прокрутки и масштаба, ручки размера — у любой фигуры
        // при любом инструменте (рядом с исходными пикселями экрана случайно задеть чужую ручку мелким масштабом нельзя).
        internal bool Inline
        {
            get { return _inline; }
            set
            {
                _inline = value;
                _fit = !value;
                _zoom = 1f;
                _pan = PointF.Empty;
                Invalidate();
            }
        }

        public bool SpaceHeld
        {
            set
            {
                if (_space == value) return;
                _space = value;
                UpdateCursor(PointToClient(Cursor.Position));
            }
        }

        public string Hint
        {
            get
            {
                switch (_style.Tool)
                {
                    case EditTool.Select: return Tr.S("Щелчок — выбрать фигуру, перетаскивание — переместить, Delete — удалить, двойной щелчок по тексту — править",
                                                      "Click to select, drag to move, Delete removes, double-click text to edit");
                    case EditTool.Pen:
                    case EditTool.Marker: return Tr.S("Shift — прямая линия · Ctrl+колесо — масштаб · пробел или средняя кнопка — сдвиг",
                                                      "Shift for a straight line · Ctrl+wheel zooms · Space or middle button pans");
                    case EditTool.Step: return Tr.S("Щелчок ставит следующий номер: ", "Click places the next number: ") + _doc.NextStep.ToString(CultureInfo.InvariantCulture);
                    case EditTool.Text: return Tr.S("Щелчок — новый текст, Enter — готово, Shift+Enter — новая строка, Esc — отмена",
                                                    "Click for new text, Enter to finish, Shift+Enter for a new line, Esc cancels");
                    case EditTool.Blur:
                    case EditTool.Pixelate: return Tr.S("Выделите область: в сохранённом снимке исходных пикселей под ней не останется",
                                                        "Drag over an area: the saved image keeps none of the original pixels under it");
                    case EditTool.Crop: return _crop.IsEmpty
                        ? Tr.S("Выделите, что оставить", "Drag over what to keep")
                        : Tr.S("Enter или двойной щелчок — обрезать, Esc — отмена · ", "Enter or double-click to crop, Esc cancels · ")
                          + _crop.Width.ToString(CultureInfo.InvariantCulture) + " × " + _crop.Height.ToString(CultureInfo.InvariantCulture);
                }
                return Tr.S("Shift — ровно · Ctrl+колесо — масштаб · пробел или средняя кнопка — сдвиг",
                            "Shift to constrain · Ctrl+wheel zooms · Space or middle button pans");
            }
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        // Фокус берётся только в активном окне: настоящий щелчок сперва активирует окно, а сообщение, пришедшее
        // неактивному окну, не должно отнимать фокус у того, с чем пользователь сейчас работает.
        private bool WindowActive
        {
            get
            {
                Form form = FindForm();
                return form != null && Form.ActiveForm == form;
            }
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_labelFont != null) _labelFont.Dispose();
            _labelFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            if (_textBox != null) PlaceTextBox();
            Invalidate();
        }

        private void RaiseChanged()
        {
            Invalidate();
            if (Changed != null) Changed();
        }

        private void RaiseView()
        {
            if (_textBox != null) PlaceTextBox();
            Invalidate();
            if (ViewChanged != null) ViewChanged();
        }

        // ---------- вид ----------

        private PointF ToImage(PointF client) { return new PointF((client.X - _pan.X) / _zoom, (client.Y - _pan.Y) / _zoom); }

        private PointF ToClient(PointF image) { return new PointF(image.X * _zoom + _pan.X, image.Y * _zoom + _pan.Y); }

        private RectangleF ToClient(RectangleF r)
        {
            return new RectangleF(r.X * _zoom + _pan.X, r.Y * _zoom + _pan.Y, r.Width * _zoom, r.Height * _zoom);
        }

        private RectangleF ImageOnClient { get { return ToClient(new RectangleF(PointF.Empty, _doc.Size)); } }

        private float FitZoom()
        {
            int margin = S(24);
            float zx = (ClientSize.Width - margin * 2) / (float)_doc.Size.Width;
            float zy = (ClientSize.Height - margin * 2) / (float)_doc.Size.Height;
            return Math.Max(MinZoom, Math.Min(1f, Math.Min(zx, zy)));
        }

        public void FitView()
        {
            _fit = true;
            _zoom = FitZoom();
            ClampPan();
            RaiseView();
        }

        public void ToggleFit()
        {
            if (_fit && Math.Abs(_zoom - 1f) > 0.001f) ZoomTo(1f);
            else FitView();
        }

        public void ZoomTo(float zoom)
        {
            ZoomAt(zoom, new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        }

        public void ZoomStep(int direction)
        {
            ZoomAt(NextZoom(_zoom, direction), new Point(ClientSize.Width / 2, ClientSize.Height / 2));
        }

        // Шаг ×1.25, но через 100 % не перескакивает: на нём пиксели снимка совпадают с пикселями экрана.
        internal static float NextZoom(float zoom, int direction)
        {
            float next = direction > 0 ? zoom * 1.25f : zoom / 1.25f;
            if ((zoom < 1f && next > 1f) || (zoom > 1f && next < 1f)) next = 1f;
            return Math.Max(MinZoom, Math.Min(MaxZoom, next));
        }

        private void ZoomAt(float zoom, Point anchor)
        {
            zoom = Math.Max(MinZoom, Math.Min(MaxZoom, zoom));
            PointF img = ToImage(anchor);
            _zoom = zoom;
            _fit = false;
            _pan = new PointF(anchor.X - img.X * zoom, anchor.Y - img.Y * zoom);
            ClampPan();
            RaiseView();
        }

        // Снимок меньше окна — посередине; больше — прокручивается, но не уезжает за край дальше отступа.
        private void ClampPan()
        {
            if (_inline) { _pan = PointF.Empty; return; }
            int margin = S(24);
            float w = _doc.Size.Width * _zoom, h = _doc.Size.Height * _zoom;
            float x = _pan.X, y = _pan.Y;
            if (w + margin * 2 <= ClientSize.Width) x = (ClientSize.Width - w) / 2;
            else x = Math.Min(margin, Math.Max(ClientSize.Width - w - margin, x));
            if (h + margin * 2 <= ClientSize.Height) y = (ClientSize.Height - h) / 2;
            else y = Math.Min(margin, Math.Max(ClientSize.Height - h - margin, y));
            _pan = new PointF((float)Math.Round(x), (float)Math.Round(y));
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (_doc == null) return;
            if (_fit) _zoom = FitZoom();
            ClampPan();
            RaiseView();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_inline) return;
            int notches = e.Delta / 120;
            if (notches == 0) notches = Math.Sign(e.Delta);
            if ((ModifierKeys & Keys.Control) != 0)
            {
                float z = _zoom;
                for (int i = 0; i < Math.Abs(notches); i++) z = NextZoom(z, Math.Sign(notches));
                ZoomAt(z, e.Location);
                return;
            }
            float step = S(60) * notches;
            if ((ModifierKeys & Keys.Shift) != 0) _pan = new PointF(_pan.X + step, _pan.Y);
            else _pan = new PointF(_pan.X, _pan.Y + step);
            ClampPan();
            RaiseView();
        }

        // ---------- слой изображения ----------

        // Фигура, которую сейчас тянут или правят, не входит в собранную картинку: её рисует холст поверх.
        private CapShape SkipShape
        {
            get
            {
                if ((_drag == Drag.Move || _drag == Drag.Handle) && _moved) return _selected;
                if (_editing != null && !_editingNew) return _editing;
                return null;
            }
        }

        private void EnsureComposite()
        {
            CapShape skip = SkipShape;
            if (_composite != null && _compositeVersion == _doc.Version && _compositeSkip == skip && _compositeImage == _doc.Image) return;
            if (_composite != null) _composite.Dispose();
            using (Bitmap render = _doc.Render(skip))
            {
                _composite = new Bitmap(render.Width, render.Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(_composite))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(render, 0, 0);
                }
            }
            _compositeVersion = _doc.Version;
            _compositeSkip = skip;
            _compositeImage = _doc.Image;
            DisposeScaled();
        }

        private void DisposeScaled()
        {
            if (_scaled != null) { _scaled.Dispose(); _scaled = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            EnsureComposite();
            RectangleF img = ImageOnClient;
            if (_zoom < 1f)
            {
                // Уменьшенная копия считается один раз на масштаб: бикубика по кадру 4K на каждое движение мыши тормозит.
                if (_scaled == null || Math.Abs(_scaledZoom - _zoom) > 0.0001f)
                {
                    DisposeScaled();
                    int w = Math.Max(1, (int)Math.Round(_composite.Width * _zoom)), h = Math.Max(1, (int)Math.Round(_composite.Height * _zoom));
                    _scaled = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                    using (Graphics sg = Graphics.FromImage(_scaled))
                    {
                        sg.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        sg.CompositingMode = CompositingMode.SourceCopy;
                        using (ImageAttributes clamp = new ImageAttributes())
                        {
                            clamp.SetWrapMode(WrapMode.TileFlipXY);
                            sg.DrawImage(_composite, new Rectangle(0, 0, w, h), 0, 0, _composite.Width, _composite.Height, GraphicsUnit.Pixel, clamp);
                        }
                    }
                    _scaledZoom = _zoom;
                }
                g.DrawImageUnscaled(_scaled, (int)_pan.X, (int)_pan.Y);
            }
            else
            {
                RectangleF clip = RectangleF.Intersect(e.ClipRectangle, img);
                if (clip.Width > 0 && clip.Height > 0)
                {
                    int x0 = Math.Max(0, (int)Math.Floor((clip.X - _pan.X) / _zoom));
                    int y0 = Math.Max(0, (int)Math.Floor((clip.Y - _pan.Y) / _zoom));
                    int x1 = Math.Min(_composite.Width, (int)Math.Ceiling((clip.Right - _pan.X) / _zoom));
                    int y1 = Math.Min(_composite.Height, (int)Math.Ceiling((clip.Bottom - _pan.Y) / _zoom));
                    if (x1 > x0 && y1 > y0)
                    {
                        g.InterpolationMode = InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        g.DrawImage(_composite, new RectangleF(_pan.X + x0 * _zoom, _pan.Y + y0 * _zoom, (x1 - x0) * _zoom, (y1 - y0) * _zoom),
                                    new RectangleF(x0, y0, x1 - x0, y1 - y0), GraphicsUnit.Pixel);
                        g.PixelOffsetMode = PixelOffsetMode.Default;
                    }
                }
            }
            using (Pen frame = new Pen(Color.FromArgb(70, 255, 255, 255))) g.DrawRectangle(frame, img.X - 1, img.Y - 1, img.Width + 1, img.Height + 1);

            // Живые фигуры — в координатах изображения.
            GraphicsState state = g.Save();
            g.TranslateTransform(_pan.X, _pan.Y);
            g.ScaleTransform(_zoom, _zoom);
            EditorDoc.Prepare(g);
            g.SetClip(new RectangleF(PointF.Empty, _doc.Size));
            CapShape skip = SkipShape;
            if (skip != null && skip != _editing) DrawLive(g, skip);
            if (_live != null) DrawLive(g, _live);
            g.Restore(state);

            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (_hover != null && _hover != _selected && _drag == Drag.None)
            {
                DrawOutline(g, _hover, Color.FromArgb(120, 255, 255, 255));
                if (_inline && _editing == null) DrawHandles(g, _hover);
            }
            if (_selected != null && _editing == null) DrawSelection(g, _selected);
            if (_style.Tool == EditTool.Crop) DrawCrop(g, img);
        }

        private void DrawLive(Graphics g, CapShape shape)
        {
            RedactShape redact = shape as RedactShape;
            if (redact == null) { shape.Draw(g); return; }
            Rectangle r = redact.PixelRect(_doc.Size);
            if (r.Width < 1 || r.Height < 1) return;
            // Предпросмотр по уже собранной картинке — тем же кодом, что при сохранении. Огромная область — только штриховкой.
            if ((long)r.Width * r.Height <= 1500000)
            {
                using (Bitmap piece = _composite.Clone(r, PixelFormat.Format32bppArgb))
                {
                    RedactShape local = (RedactShape)redact.Clone();
                    local.Rect = new RectangleF(0, 0, r.Width, r.Height);
                    local.Apply(piece);
                    g.DrawImage(piece, r);
                }
            }
            else
            {
                using (HatchBrush hatch = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(120, 255, 255, 255), Color.FromArgb(120, 0, 0, 0)))
                    g.FillRectangle(hatch, r);
            }
        }

        private void DrawOutline(Graphics g, CapShape shape, Color color)
        {
            RectangleF b = ToClient(shape.Bounds);
            using (Pen p = new Pen(color, 1))
            {
                p.DashStyle = DashStyle.Dash;
                g.DrawRectangle(p, b.X, b.Y, b.Width, b.Height);
            }
        }

        private void DrawSelection(Graphics g, CapShape shape)
        {
            if (!(shape is LineShape)) DrawOutline(g, shape, Color.FromArgb(200, EditorColors.Accent));
            if (_style.Tool == EditTool.Select || _inline) DrawHandles(g, shape);
        }

        private void DrawHandles(Graphics g, CapShape shape)
        {
            float hs = S(4);
            foreach (PointF p in HandlesOf(shape))
            {
                RectangleF box = new RectangleF(p.X - hs, p.Y - hs, hs * 2, hs * 2);
                using (SolidBrush fill = new SolidBrush(Color.White)) g.FillEllipse(fill, box);
                using (Pen edge = new Pen(EditorColors.Accent, Math.Max(1f, S(1.5f)))) g.DrawEllipse(edge, box);
            }
        }

        private void DrawCrop(Graphics g, RectangleF img)
        {
            if (_crop.IsEmpty) return;
            RectangleF cr = ToClient(_crop);
            using (Region outside = new Region(img))
            {
                outside.Exclude(cr);
                using (SolidBrush shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(shade, outside);
            }
            using (Pen thirds = new Pen(Color.FromArgb(60, 255, 255, 255), 1))
                for (int i = 1; i < 3; i++)
                {
                    g.DrawLine(thirds, cr.X + cr.Width * i / 3, cr.Y, cr.X + cr.Width * i / 3, cr.Bottom);
                    g.DrawLine(thirds, cr.X, cr.Y + cr.Height * i / 3, cr.Right, cr.Y + cr.Height * i / 3);
                }
            using (Pen border = new Pen(Color.White, Math.Max(1f, S(1.5f)))) g.DrawRectangle(border, cr.X, cr.Y, cr.Width, cr.Height);
            float hs = S(4);
            foreach (PointF p in EditGeometry.HandlePoints(cr))
            {
                using (SolidBrush fill = new SolidBrush(Color.White)) g.FillRectangle(fill, p.X - hs, p.Y - hs, hs * 2, hs * 2);
                using (Pen edge = new Pen(Color.FromArgb(40, 40, 40), 1)) g.DrawRectangle(edge, p.X - hs, p.Y - hs, hs * 2, hs * 2);
            }
            string label = _crop.Width.ToString(CultureInfo.InvariantCulture) + " × " + _crop.Height.ToString(CultureInfo.InvariantCulture);
            Size sz = TextRenderer.MeasureText(label, _labelFont);
            Rectangle lr = new Rectangle((int)cr.X, (int)cr.Y - sz.Height - S(8), sz.Width + S(12), sz.Height + S(4));
            if (lr.Y < 0) lr.Y = (int)cr.Y + S(6);
            using (SolidBrush back = new SolidBrush(Color.FromArgb(200, 20, 20, 20))) g.FillRectangle(back, lr);
            TextRenderer.DrawText(g, label, _labelFont, lr, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        // ---------- фигуры под курсором и ручки ----------

        private CapShape ShapeAt(PointF image)
        {
            float tolerance = S(5) / _zoom;
            List<CapShape> shapes = _doc.Shapes;
            for (int i = shapes.Count - 1; i >= 0; i--)
                if (shapes[i].HitTest(image, tolerance)) return shapes[i];
            return null;
        }

        private List<PointF> HandlesOf(CapShape shape)
        {
            List<PointF> list = new List<PointF>();
            LineShape line = shape as LineShape;
            if (line != null)
            {
                list.Add(ToClient(line.A));
                list.Add(ToClient(line.B));
                return list;
            }
            RectangleF rect;
            if (RectOf(shape, out rect)) list.AddRange(EditGeometry.HandlePoints(ToClient(rect)));
            return list;
        }

        private static bool RectOf(CapShape shape, out RectangleF rect)
        {
            BoxShape box = shape as BoxShape;
            if (box != null) { rect = box.Rect; return true; }
            RedactShape redact = shape as RedactShape;
            if (redact != null) { rect = redact.Rect; return true; }
            rect = RectangleF.Empty;
            return false;
        }

        private static void SetRect(CapShape shape, RectangleF rect)
        {
            BoxShape box = shape as BoxShape;
            if (box != null) box.Rect = rect;
            RedactShape redact = shape as RedactShape;
            if (redact != null) redact.Rect = rect;
        }

        private int HandleAt(Point client)
        {
            CapShape owner;
            return HandleAt(client, out owner);
        }

        // owner — фигура, чью ручку взяли: выделенная, а на холсте оверлея — и любая другая, сверху вниз.
        private int HandleAt(Point client, out CapShape owner)
        {
            owner = null;
            // В окне редактора ручки берёт только «Выбор»: рисующим инструментом следующая фигура, начатая вплотную к углу
            // предыдущей, при мелком масштабе растягивала бы предыдущую вместо новой.
            if (_style.Tool != EditTool.Select && !_inline) return -1;
            List<CapShape> candidates = new List<CapShape>();
            if (_selected != null) candidates.Add(_selected);
            if (_inline)
                for (int i = _doc.Shapes.Count - 1; i >= 0; i--)
                    if (_doc.Shapes[i] != _selected) candidates.Add(_doc.Shapes[i]);
            foreach (CapShape shape in candidates)
            {
                List<PointF> handles = HandlesOf(shape);
                for (int i = handles.Count - 1; i >= 0; i--)
                    if (EditGeometry.Distance(handles[i], client) <= S(7)) { owner = shape; return i; }
            }
            return -1;
        }

        private int CropHandleAt(Point client)
        {
            if (_crop.IsEmpty) return -1;
            PointF[] handles = EditGeometry.HandlePoints(ToClient(_crop));
            for (int i = 0; i < handles.Length; i++)
                if (EditGeometry.Distance(handles[i], client) <= S(8)) return i;
            return -1;
        }

        private static Cursor HandleCursor(int handle)
        {
            switch (handle)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE;
            }
        }

        private void UpdateCursor(Point client)
        {
            if (_drag == Drag.Pan || _space) { Cursor = _drag == Drag.Pan ? Cursors.SizeAll : Cursors.Hand; return; }
            PointF image = ToImage(client);
            if (_style.Tool == EditTool.Crop)
            {
                int ch = CropHandleAt(client);
                if (ch >= 0) Cursor = HandleCursor(ch);
                else Cursor = !_crop.IsEmpty && ToClient(_crop).Contains(client) ? Cursors.SizeAll : Cursors.Cross;
                return;
            }
            CapShape owner;
            int h = HandleAt(client, out owner);
            if (h >= 0) { Cursor = owner is LineShape ? Cursors.SizeAll : HandleCursor(h); return; }
            switch (_style.Tool)
            {
                case EditTool.Select: Cursor = ShapeAt(image) != null ? Cursors.SizeAll : Cursors.Default; break;
                case EditTool.Text: Cursor = Cursors.IBeam; break;
                default: Cursor = Cursors.Cross; break;
            }
        }

        // ---------- мышь ----------

        private StyleSnapshot Current { get { return new StyleSnapshot(_style); } }

        private struct StyleSnapshot
        {
            public readonly Color Color;
            public readonly float Width, FontSize;
            public readonly bool Fill;

            public StyleSnapshot(EditorStyle s)
            {
                Color = s.Color;
                Width = s.Width;
                FontSize = s.FontSize;
                Fill = s.Fill;
            }
        }

        private void ApplyStyleTo(CapShape shape)
        {
            StyleSnapshot st = Current;
            shape.Color = st.Color;
            shape.Width = st.Width;
            shape.Fill = st.Fill;
            TextShape text = shape as TextShape;
            if (text != null) text.FontSize = st.FontSize;
        }

        private CapShape NewShape(EditTool tool, PointF at)
        {
            CapShape shape;
            switch (tool)
            {
                case EditTool.Pen:
                case EditTool.Marker:
                {
                    StrokeShape s = new StrokeShape();
                    s.Marker = tool == EditTool.Marker;
                    s.Points.Add(at);
                    shape = s;
                    break;
                }
                case EditTool.Line:
                case EditTool.Arrow:
                {
                    LineShape s = new LineShape();
                    s.Arrow = tool == EditTool.Arrow;
                    s.A = at;
                    s.B = at;
                    shape = s;
                    break;
                }
                case EditTool.Rect:
                case EditTool.Ellipse:
                {
                    BoxShape s = new BoxShape();
                    s.Ellipse = tool == EditTool.Ellipse;
                    s.Rect = new RectangleF(at, SizeF.Empty);
                    shape = s;
                    break;
                }
                case EditTool.Blur:
                case EditTool.Pixelate:
                {
                    RedactShape s = new RedactShape();
                    s.Pixelate = tool == EditTool.Pixelate;
                    s.Rect = new RectangleF(at, SizeF.Empty);
                    shape = s;
                    break;
                }
                default: return null;
            }
            ApplyStyleTo(shape);
            return shape;
        }

        internal static PointF Snap45(PointF from, PointF to)
        {
            double dx = to.X - from.X, dy = to.Y - from.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            return new PointF((float)(from.X + Math.Cos(angle) * len), (float)(from.Y + Math.Sin(angle) * len));
        }

        internal static PointF SquareCorner(PointF from, PointF to)
        {
            float dx = to.X - from.X, dy = to.Y - from.Y;
            float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            return new PointF(from.X + (dx < 0 ? -side : side), from.Y + (dy < 0 ? -side : side));
        }

        private PointF ClampToImage(PointF p)
        {
            return new PointF(Math.Max(0, Math.Min(_doc.Size.Width, p.X)), Math.Max(0, Math.Min(_doc.Size.Height, p.Y)));
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_composite != null) _composite.Dispose();
                DisposeScaled();
                if (_textFont != null) _textFont.Dispose();
                if (_labelFont != null) _labelFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
