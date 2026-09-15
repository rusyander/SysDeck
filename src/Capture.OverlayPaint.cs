// SysDeck — «Захват»: оверлей выбора области — отрисовка, лупа, панель кнопок и переход в редактор.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
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
    internal sealed partial class RegionOverlay : Form
    {
        private List<Rectangle> DecorRects()
        {
            List<Rectangle> list = new List<Rectangle>();
            Rectangle h = Highlight;
            if (!h.IsEmpty)
            {
                int pad = Px(8);
                // Только рамка и ручки; вошедшая в выделение или вышедшая из него часть добавляется в Change, при сдвиге — целиком.
                Rectangle outer = Rectangle.Inflate(h, pad, pad);
                if (_mode == Mode.Moving || h.Width <= pad * 4 || h.Height <= pad * 4) list.Add(outer);
                else
                {
                    list.Add(new Rectangle(outer.X, outer.Y, outer.Width, pad * 2));
                    list.Add(new Rectangle(outer.X, outer.Bottom - pad * 2, outer.Width, pad * 2));
                    list.Add(new Rectangle(outer.X, outer.Y, pad * 2, outer.Height));
                    list.Add(new Rectangle(outer.Right - pad * 2, outer.Y, pad * 2, outer.Height));
                }
                list.Add(SizeLabelRect(h));
            }
            if (ShowMagnifier) list.Add(MagnifierRect());
            if (_mode == Mode.Selected) { list.Add(PanelRect()); list.Add(TipBubble()); }
            if (_mode == Mode.Idle) list.Add(HintRect());
            if (_flash != null) list.Add(FlashRect());
            return list;
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Rectangle clip = Rectangle.Intersect(e.ClipRectangle, ClientBounds);
            if (clip.Width <= 0 || clip.Height <= 0) return;
            if (_buffer == null || _buffer.Width < clip.Width || _buffer.Height < clip.Height)
            {
                DibBuffer grown = new DibBuffer(Math.Max(clip.Width, _buffer == null ? 0 : _buffer.Width),
                                                Math.Max(clip.Height, _buffer == null ? 0 : _buffer.Height));
                if (_buffer != null) _buffer.Dispose();
                _buffer = grown;
            }
            using (Graphics g = Graphics.FromImage(_buffer.Image))
            {
                g.TranslateTransform(-clip.X, -clip.Y);
                g.SetClip(clip);
                Compose(g, clip);
            }
            // На экран — одним BitBlt: Graphics.DrawImage в DC окна для кадра всех мониторов занимал ~630 мс, и после
            // нажатия клавиши экран полсекунды не темнел. BitBlt той же памяти — единицы миллисекунд.
            IntPtr hdc = e.Graphics.GetHdc();
            try { _buffer.CopyTo(hdc, clip); }
            finally { e.Graphics.ReleaseHdc(hdc); }
        }

        private void Compose(Graphics g, Rectangle clip)
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(_frame, clip, clip, GraphicsUnit.Pixel);
            Rectangle h = Highlight;
            g.CompositingMode = CompositingMode.SourceOver;
            g.PixelOffsetMode = PixelOffsetMode.Default;
            using (Region dark = new Region(clip))
            {
                if (!h.IsEmpty) dark.Exclude(h);
                g.FillRegion(_shade, dark);
            }
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            Color accent = Color.FromArgb(59, 130, 246);

            if (!h.IsEmpty)
            {
                bool hoverOnly = _sel.IsEmpty;
                using (Pen pen = new Pen(hoverOnly ? Color.FromArgb(200, accent) : accent, Math.Max(1, Px(hoverOnly ? 3 : 2))))
                {
                    pen.Alignment = PenAlignment.Inset;
                    // Внутреннюю полосу закрыл бы холст — рамка правки идёт снаружи выделения.
                    int t = Editing ? (int)pen.Width : 0;
                    g.DrawRectangle(pen, h.X - t, h.Y - t, h.Width + t * 2 - 1, h.Height + t * 2 - 1);
                }
                if (_mode == Mode.Selected || _mode == Mode.Resizing || _mode == Mode.Moving)
                    using (SolidBrush fill = new SolidBrush(Color.White))
                    using (Pen edge = new Pen(accent, 1))
                        foreach (Rectangle r in RegionMath.Handles(h, Px(8)))
                        {
                            g.FillRectangle(fill, r);
                            g.DrawRectangle(edge, r.X, r.Y, r.Width - 1, r.Height - 1);
                        }
                DrawSizeLabel(g, h);
            }
            if (ShowMagnifier) DrawMagnifier(g);
            if (_mode == Mode.Selected) DrawPanel(g, accent);
            if (_mode == Mode.Idle) DrawBubble(g, HintRect(), HintText, _hintFont);
            if (_flash != null) DrawBubble(g, FlashRect(), _flash, _hintFont);
        }

        // Текст только через GDI+: TextRenderer (GDI) не видит TranslateTransform буфера — надпись уезжает на смещение
        // участка — и обнуляет альфу 32bpp-растра, так что при SourceCopy на экран глифы пропадают.
        private static StringFormat TextFormat(bool center)
        {
            StringFormat f = new StringFormat(StringFormat.GenericTypographic);
            f.FormatFlags |= StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces;
            f.Alignment = center ? StringAlignment.Center : StringAlignment.Near;
            f.LineAlignment = center ? StringAlignment.Center : StringAlignment.Near;
            return f;
        }

        private Size Measure(string text, Font font)
        {
            using (StringFormat f = TextFormat(false))
            {
                SizeF s = _measure.MeasureString(text, font, PointF.Empty, f);
                return new Size((int)Math.Ceiling(s.Width), (int)Math.Ceiling(s.Height));
            }
        }

        private static void DrawText(Graphics g, string text, Font font, Rectangle r, Color color, bool center)
        {
            using (StringFormat f = TextFormat(center))
            using (SolidBrush b = new SolidBrush(color))
                g.DrawString(text, font, b, r, f);
        }

        private string SizeText(Rectangle h)
        {
            string s = h.Width.ToString(CultureInfo.InvariantCulture) + " × " + h.Height.ToString(CultureInfo.InvariantCulture);
            if (_video)
            {
                // Видео пишется в чётном размере (NV12 и H.264): показываем, каким будет файл.
                Size even = RegionMath.EvenSize(h.Size);
                if (even != h.Size) s += "  → " + even.Width.ToString(CultureInfo.InvariantCulture) + " × " + even.Height.ToString(CultureInfo.InvariantCulture);
            }
            if (_shift && (_mode == Mode.Dragging)) s += "  " + _ratioNames[_ratioIndex];
            return s;
        }

        private Rectangle SizeLabelRect(Rectangle h)
        {
            Size text = Measure(SizeText(h) + "  1:1", _labelFont);
            int w = text.Width + Px(12), ht = text.Height + Px(6);
            int y = h.Y - ht - Px(6);
            if (y < MonitorRect(h.Location).Top) y = h.Y + Px(6);
            return new Rectangle(h.X, y, w, ht);
        }

        private void DrawSizeLabel(Graphics g, Rectangle h)
        {
            Rectangle r = SizeLabelRect(h);
            DrawBubble(g, new Rectangle(r.X, r.Y, Measure(SizeText(h), _labelFont).Width + Px(12), r.Height), SizeText(h), _labelFont);
        }

        private static readonly string HintTextRu = "Выделите область мышью · щелчок — окно · двойной щелчок — монитор · L — прошлая область · Esc — выход";
        private static readonly string HintTextEn = "Drag to select · click — window · double-click — monitor · L — last region · Esc — exit";
        private static readonly string VideoHintRu = "Запись видео: выделите область · щелчок — окно · двойной щелчок — монитор · Enter — начать · Esc — выход";
        private static readonly string VideoHintEn = "Video: drag to select · click — window · double-click — monitor · Enter — start · Esc — exit";
        private string HintText { get { return _video ? Tr.S(VideoHintRu, VideoHintEn) : Tr.S(HintTextRu, HintTextEn); } }

        private Rectangle HintRect()
        {
            Rectangle m = MonitorRect(_mouse);
            Size s = Measure(HintText, _hintFont);
            int w = s.Width + Px(28), ht = s.Height + Px(14);
            return new Rectangle(m.X + (m.Width - w) / 2, m.Y + Px(24), w, ht);
        }

        private Rectangle FlashRect()
        {
            Rectangle m = MonitorRect(_mouse);
            Size s = Measure(_flash ?? "", _hintFont);
            int w = s.Width + Px(28), ht = s.Height + Px(14);
            return new Rectangle(m.X + (m.Width - w) / 2, m.Bottom - ht - Px(48), w, ht);
        }

        private void DrawBubble(Graphics g, Rectangle r, string text, Font font)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Rounded(r, Px(6)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(215, 24, 24, 27)))
                g.FillPath(bg, path);
            g.SmoothingMode = SmoothingMode.None;
            DrawText(g, text, font, r, Color.White, true);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // ---------- лупа ----------

        private bool ShowMagnifier { get { return _mode == Mode.Idle || _mode == Mode.Pressing || _mode == Mode.Dragging || _mode == Mode.Resizing; } }

        private const int MagCells = 15, MagZoom = 8;

        private Rectangle MagnifierRect()
        {
            int box = MagCells * MagZoom;
            int w = box + Px(4), h = box + Px(48);
            Rectangle m = MonitorRect(_mouse);
            int x = _mouse.X + Px(24), y = _mouse.Y + Px(24);
            if (x + w > m.Right) x = _mouse.X - Px(24) - w;
            if (y + h > m.Bottom) y = _mouse.Y - Px(24) - h;
            return new Rectangle(x, y, w, h);
        }

        private void DrawMagnifier(Graphics g)
        {
            Rectangle r = MagnifierRect();
            int box = MagCells * MagZoom;
            Rectangle zoom = new Rectangle(r.X + Px(2), r.Y + Px(2), box, box);
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(235, 24, 24, 27))) g.FillRectangle(bg, r);
            Rectangle src = new Rectangle(_mouse.X - MagCells / 2, _mouse.Y - MagCells / 2, MagCells, MagCells);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (SolidBrush black = new SolidBrush(Color.Black)) g.FillRectangle(black, zoom);
            g.DrawImage(_frame, zoom, src, GraphicsUnit.Pixel);
            g.PixelOffsetMode = PixelOffsetMode.Default;
            int c = (MagCells / 2) * MagZoom;
            using (Pen cross = new Pen(Color.FromArgb(160, 59, 130, 246), 1))
            {
                g.DrawLine(cross, zoom.X, zoom.Y + c + MagZoom / 2, zoom.Right, zoom.Y + c + MagZoom / 2);
                g.DrawLine(cross, zoom.X + c + MagZoom / 2, zoom.Y, zoom.X + c + MagZoom / 2, zoom.Bottom);
            }
            using (Pen cell = new Pen(Color.White, 1)) g.DrawRectangle(cell, zoom.X + c, zoom.Y + c, MagZoom, MagZoom);
            Color px = PixelAt(_mouse);
            string hex = "#" + px.R.ToString("X2") + px.G.ToString("X2") + px.B.ToString("X2");
            string coords = (_mouse.X + _origin.X).ToString(CultureInfo.InvariantCulture) + ", " + (_mouse.Y + _origin.Y).ToString(CultureInfo.InvariantCulture);
            Rectangle swatch = new Rectangle(r.X + Px(6), zoom.Bottom + Px(8), Px(14), Px(14));
            using (SolidBrush sb = new SolidBrush(px)) g.FillRectangle(sb, swatch);
            using (Pen edge = new Pen(Color.Gray, 1)) g.DrawRectangle(edge, swatch);
            DrawText(g, hex + "   " + coords, _labelFont, Rectangle.FromLTRB(swatch.Right + Px(6), swatch.Y - Px(1), r.Right - Px(4), swatch.Bottom + Px(2)),
                     Color.White, false);
            DrawText(g, Tr.S("C — копировать цвет", "C — copy color"), _labelFont, Rectangle.FromLTRB(r.X + Px(6), swatch.Bottom + Px(4), r.Right - Px(4), r.Bottom),
                     Color.FromArgb(170, 170, 170), false);
        }

        // ---------- панель действий ----------

        private bool IsShown(PanelButton b)
        {
            // Видео области пишет экран, а не нарисованное: с начатыми правками кнопки записи нет.
            return !(Editing && b.Kind == PanelKind.Action && b.Action == OverlayAction.Record);
        }

        private void LayoutPanel()
        {
            if (_mode != Mode.Selected || _sel.IsEmpty) return;
            int size = Px(36), gap = Px(2), groupGap = Px(12), pad = Px(4), rowGap = Px(9), edge = Px(4);
            Rectangle mon = PanelMonitor();
            // Панель всегда в две строки: сверху инструменты и цвета, снизу правка, действия, «Сохранить» и «Отмена».
            // С подписями нижняя строка шире; не влезла в монитор — главные кнопки сжимаются до значков.
            _captions = true;
            int w0 = RowWidth(0, size, gap, groupGap), w1 = RowWidth(1, size, gap, groupGap);
            if (Math.Max(w0, w1) + pad * 2 > mon.Width - edge * 2)
            {
                _captions = false;
                w1 = RowWidth(1, size, gap, groupGap);
            }
            int rows = (w0 > 0 ? 1 : 0) + (w1 > 0 ? 1 : 0);
            int w = Math.Max(w0, w1) + pad * 2;
            int h = rows * size + (rows - 1) * rowGap + pad * 2;
            int x = Math.Min(_sel.Right - w, mon.Right - w - edge);
            int y = _sel.Bottom + Px(8);
            if (y + h > mon.Bottom - edge) y = _sel.Top - h - Px(8);
            if (y < mon.Top + edge) y = _sel.Bottom - h - Px(8);
            // Выделение вровень с краем экрана (или шире монитора) — панель всё равно целиком на мониторе,
            // иначе «Сохранить» уходит за край и до него не дотянуться.
            x = Math.Max(mon.Left + edge, Math.Min(x, mon.Right - w - edge));
            y = Math.Max(mon.Top + edge, Math.Min(y, mon.Bottom - h - edge));
            int rowY = y + pad;
            for (int row = 0; row < 2; row++)
            {
                int rw = row == 0 ? w0 : w1;
                if (rw == 0) continue;
                // Нижняя строка прижата вправо: «Сохранить» и «Отмена» — в углу панели, у края выделения.
                int bx = row == 0 ? x + pad : x + w - pad - rw, last = -1;
                foreach (PanelButton b in _buttons)
                {
                    if (PanelRow(b) != row) continue;
                    if (!IsShown(b)) { b.Bounds = Rectangle.Empty; continue; }
                    bx += last < 0 ? 0 : b.Group != last ? groupGap : gap;
                    int bw = ButtonWidth(b, size);
                    b.Bounds = new Rectangle(bx, rowY, bw, size);
                    bx += bw;
                    last = b.Group;
                }
                rowY += size + rowGap;
            }
        }

        private static int PanelRow(PanelButton b) { return b.Group < 2 ? 0 : 1; }

        // Монитор панели — тот, где правый нижний угол выделения; угол в щели между мониторами разной высоты —
        // тот, что больше всех пересекается с выделением.
        private Rectangle PanelMonitor()
        {
            MonitorInfo m = MonitorAtClient(new Point(Math.Max(0, _sel.Right - 1), Math.Max(0, _sel.Bottom - 1)));
            if (m != null) return ToClient(m.Bounds);
            Rectangle best = ClientBounds;
            long bestArea = -1;
            foreach (MonitorInfo mi in _monitors)
            {
                Rectangle r = ToClient(mi.Bounds), i = Rectangle.Intersect(r, _sel);
                long area = i.IsEmpty ? 0 : (long)i.Width * i.Height;
                if (area > bestArea) { bestArea = area; best = r; }
            }
            return best;
        }

        private int RowWidth(int row, int size, int gap, int groupGap)
        {
            int w = 0, last = -1;
            foreach (PanelButton b in _buttons)
            {
                if (PanelRow(b) != row || !IsShown(b)) continue;
                w += (last < 0 ? 0 : b.Group != last ? groupGap : gap) + ButtonWidth(b, size);
                last = b.Group;
            }
            return w;
        }

        // Подписанная кнопка: отступ, значок, промежуток, текст, отступ.
        private int ButtonWidth(PanelButton b, int size)
        {
            if (b.Caption == null || !_captions) return size;
            return Px(12) + Px(18) + Px(6) + Measure(b.Caption, _captionFont).Width + Px(14);
        }

        private Rectangle PanelRect()
        {
            Rectangle all = Rectangle.Empty;
            foreach (PanelButton b in _buttons)
                if (!b.Bounds.IsEmpty) all = all.IsEmpty ? b.Bounds : Rectangle.Union(all, b.Bounds);
            return all.IsEmpty ? all : Rectangle.Inflate(all, Px(4), Px(4));
        }

        // Подсказка у кнопки — по ту сторону панели, где нет выделения; не поместилась — с другой стороны.
        private Rectangle TipBubble()
        {
            if (_hotButton < 0 || _hotButton >= _buttons.Count || _buttons[_hotButton].Bounds.IsEmpty) return Rectangle.Empty;
            Rectangle b = _buttons[_hotButton].Bounds, p = PanelRect();
            Size s = Measure(_buttons[_hotButton].Tip, _labelFont);
            int w = s.Width + Px(16), h = s.Height + Px(8);
            Rectangle mon = MonitorRect(new Point(b.X + b.Width / 2, b.Y));
            int x = Math.Max(mon.Left + Px(4), Math.Min(b.X + b.Width / 2 - w / 2, mon.Right - w - Px(4)));
            int y = p.Top >= _sel.Bottom ? p.Bottom + Px(6) : p.Top - h - Px(6);
            if (y + h > mon.Bottom) y = p.Top - h - Px(6);
            if (y < mon.Top) y = p.Bottom + Px(6);
            return new Rectangle(x, y, w, h);
        }

        private bool IsEnabled(PanelButton b)
        {
            if (b.Kind == PanelKind.Undo) return Editing && _doc.CanUndo;
            if (b.Kind == PanelKind.Redo) return Editing && _doc.CanRedo;
            if (b.Kind == PanelKind.Delete) return Editing && _canvas.Selected != null;
            return true;
        }

        private bool IsChecked(PanelButton b)
        {
            switch (b.Kind)
            {
                case PanelKind.Tool: return Editing && _style.Tool == b.Tool;
                case PanelKind.Color: return _style.Color.ToArgb() == b.Color.ToArgb();
                case PanelKind.CustomColor: return !InPalette(_style.Color);
            }
            return false;
        }

        private void DrawPanel(Graphics g, Color accent)
        {
            Rectangle p = PanelRect();
            if (p.IsEmpty) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = Rounded(p, Px(8)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(240, 32, 32, 36)))
            using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1))
            {
                g.FillPath(bg, path);
                g.DrawPath(edge, path);
            }
            PanelButton prev = null;
            for (int i = 0; i < _buttons.Count; i++)
            {
                PanelButton b = _buttons[i];
                if (b.Bounds.IsEmpty) continue;
                if (prev != null && prev.Group != b.Group)
                    using (SolidBrush sep = new SolidBrush(Color.FromArgb(60, 255, 255, 255)))
                    {
                        if (prev.Bounds.Y == b.Bounds.Y)
                            g.FillRectangle(sep, (prev.Bounds.Right + b.Bounds.Left) / 2, b.Bounds.Y + Px(8), 1, b.Bounds.Height - Px(16));
                        else
                            g.FillRectangle(sep, p.Left + Px(10), (prev.Bounds.Bottom + b.Bounds.Top) / 2, p.Width - Px(20), 1);
                    }
                prev = b;
                bool enabled = IsEnabled(b), on = IsChecked(b), hot = i == _hotButton && enabled;
                PointF center = new PointF(b.Bounds.X + b.Bounds.Width / 2f, b.Bounds.Y + b.Bounds.Height / 2f);
                if (b.Kind == PanelKind.Color || b.Kind == PanelKind.CustomColor)
                {
                    EditorBar.DrawSwatch(g, center, _scale, b.Kind == PanelKind.Color ? b.Color : _customColor, b.Kind == PanelKind.CustomColor, on, hot);
                    continue;
                }
                if (b.Caption != null && _captions)
                {
                    DrawCaptioned(g, b, accent, hot);
                    continue;
                }
                Color back = Color.Empty;
                if (hot) back = b.Kind == PanelKind.Action && b.Action == OverlayAction.Cancel ? Color.FromArgb(196, 43, 28)
                              : b.Primary ? ControlPaint.Light(accent, 0.1f) : accent;
                else if (b.Primary) back = accent;
                else if (on) back = Color.FromArgb(110, accent);
                if (back != Color.Empty)
                    using (GraphicsPath hp = Rounded(b.Bounds, Px(6)))
                    using (SolidBrush fill = new SolidBrush(back))
                        g.FillPath(fill, hp);
                Color fg = enabled ? Color.White : Color.FromArgb(90, 255, 255, 255);
                RectangleF icon = new RectangleF(center.X - Px(10), center.Y - Px(10), Px(20), Px(20));
                switch (b.Kind)
                {
                    case PanelKind.Tool: EditorBar.DrawToolIcon(g, b.Tool, icon, fg); break;
                    case PanelKind.Undo: EditorBar.DrawTurn(g, icon, fg, false, true); break;
                    case PanelKind.Redo: EditorBar.DrawTurn(g, icon, fg, true, true); break;
                    default: DrawText(g, b.Glyph, _glyphFont, b.Bounds, fg, true); break;
                }
            }
            g.SmoothingMode = SmoothingMode.None;
            Rectangle tip = TipBubble();
            if (!tip.IsEmpty) DrawBubble(g, tip, _buttons[_hotButton].Tip, _labelFont);
        }

        // Подписанная кнопка в идиоме страниц программы (MainForm.Theme): главная залита акцентом без рамки,
        // обычная — тёмная поверхность с мягкой рамкой; «Отмена» под курсором краснеет, как закрытие.
        private void DrawCaptioned(Graphics g, PanelButton b, Color accent, bool hot)
        {
            bool cancel = b.Action == OverlayAction.Cancel;
            Color fill = b.Primary ? (hot ? ControlPaint.Light(accent, 0.1f) : accent)
                       : hot && cancel ? Color.FromArgb(196, 43, 28)
                       : hot ? Color.FromArgb(78, 82, 90) : Color.FromArgb(58, 61, 68);
            Rectangle r = b.Bounds;
            using (GraphicsPath path = Rounded(r, Px(8)))
            {
                using (SolidBrush br = new SolidBrush(fill))
                    g.FillPath(br, path);
                if (!b.Primary && !(hot && cancel))
                    using (Pen edge = new Pen(Color.FromArgb(84, 88, 96), 1))
                        g.DrawPath(edge, path);
            }
            Rectangle icon = new Rectangle(r.X + Px(12), r.Y, Px(18), r.Height);
            DrawText(g, b.Glyph, _glyphFont, icon, Color.White, true);
            int tx = icon.Right + Px(6);
            Rectangle text = new Rectangle(tx, r.Y, Math.Max(1, r.Right - Px(14) - tx), r.Height);
            DrawText(g, b.Caption, _captionFont, text, Color.White, true);
        }

        // ---------- правки поверх выделения ----------

        private bool Editing { get { return _canvas != null; } }

        private void Press(PanelButton b)
        {
            if (!IsEnabled(b)) return;
            switch (b.Kind)
            {
                case PanelKind.Action: Finish(b.Action == OverlayAction.Save ? OverlayAction.Default : b.Action); break;
                case PanelKind.Tool:
                    Change(delegate
                    {
                        _style.Tool = b.Tool;
                        if (Editing) _canvas.ToolChanged();
                        else BeginEditing();
                    });
                    break;
                case PanelKind.Color: SetColor(b.Color); break;
                case PanelKind.CustomColor: PickColor(); break;
                case PanelKind.Undo: _canvas.Undo(); break;
                case PanelKind.Redo: _canvas.Redo(); break;
                case PanelKind.Delete: _canvas.DeleteSelected(); break;
            }
        }

        // Цвет следующих фигур и набираемого текста. Только что нарисованная рамка остаётся выделенной ради ручек, но не
        // перекрашивается: иначе «красная рамка, затем зелёная» давала бы две зелёные.
        private void SetColor(Color c)
        {
            Change(delegate
            {
                _style.Color = c;
                if (!InPalette(c)) _customColor = c;
            });
            if (Editing && _canvas.EditingText) _canvas.ApplyStyleField(StyleField.Color);
        }

        private void PickColor()
        {
            Color picked = Color.Empty;
            _modal = true;
            try
            {
                using (ColorDialog dlg = new ColorDialog())
                {
                    dlg.FullOpen = true;
                    dlg.Color = _customColor;
                    if (dlg.ShowDialog(this) == DialogResult.OK) picked = Color.FromArgb(255, dlg.Color);
                }
            }
            finally
            {
                _modal = false;
            }
            if (_done) return;
            Activate();
            if (Editing) _canvas.Focus();
            if (!picked.IsEmpty) SetColor(picked);
        }

        private void BeginEditing()
        {
            Rectangle area = Rectangle.Intersect(_sel, ClientBounds);
            if (area.Width < 1 || area.Height < 1) return;
            _doc = new EditorDoc(_frame.Clone(area, PixelFormat.Format32bppArgb));
            _canvas = new EditorCanvas(_doc, _style);
            _canvas.Inline = true;
            _canvas.ApplyScale(_scale);
            _canvas.Bounds = area;
            _canvas.Changed += delegate { if (!_done) Change(delegate { }); };
            _canvas.MouseEnter += delegate { if (_hotButton >= 0) Change(delegate { _hotButton = -1; }); };
            Controls.Add(_canvas);
            CutCanvas(true);
            _canvas.Focus();
        }

        // Область растянули или сжали с начатыми правками: холст берёт новый кусок кадра, фигуры остаются на своих местах
        // экрана — ушедшие за край просто не видны и вернутся, если область снова расширить.
        private void Reframe()
        {
            Rectangle area = Rectangle.Intersect(_sel, ClientBounds);
            if (area.Width < 1 || area.Height < 1 || area == _canvas.Bounds) return;
            Point old = _canvas.Location;
            _doc.Reframe(_frame.Clone(area, PixelFormat.Format32bppArgb), old.X - area.X, old.Y - area.Y);
            _canvas.Bounds = area;
            _canvas.Invalidate();
            CutCanvas(true);
        }

        // Холст — отдельное окно поверх выделения; панель и подсказка могут лечь на выделение (выделен весь монитор),
        // поэтому под ними холст вырезан.
        private void CutCanvas(bool force)
        {
            if (!Editing) return;
            Rectangle panel = _mode == Mode.Selected ? PanelRect() : Rectangle.Empty;
            Rectangle tip = _mode == Mode.Selected ? TipBubble() : Rectangle.Empty;
            if (!force && panel == _cutPanel && tip == _cutTip) return;
            _cutPanel = panel;
            _cutTip = tip;
            Region region = new Region(new Rectangle(Point.Empty, _canvas.Size));
            Point o = _canvas.Location;
            if (!panel.IsEmpty) region.Exclude(new Rectangle(panel.X - o.X, panel.Y - o.Y, panel.Width, panel.Height));
            if (!tip.IsEmpty) region.Exclude(new Rectangle(tip.X - o.X, tip.Y - o.Y, tip.Width, tip.Height));
            Region old = _canvas.Region;
            _canvas.Region = region;
            if (old != null) old.Dispose();
        }
    }
}
