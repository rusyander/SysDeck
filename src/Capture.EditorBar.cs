// SysDeck — «Захват»: панель инструментов редактора снимка.
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
    //  Панель инструментов: рисуется целиком сама, переносится на новые строки в узком окне
    // ------------------------------------------------------------------ //
    internal sealed class EditorBar : Control
    {
        private enum Kind { Tool, Command, Swatch, CustomColor, Width, Fill, FontDown, FontLabel, FontUp }

        private sealed class Item
        {
            public Kind Kind;
            public EditTool Tool;
            public EditorCommand Command;
            public Color Swatch;
            public float Value;
            public string Tip;
            public string Label;
            public int Group;
            public bool Output;
            public Rectangle Bounds;
        }

        public static readonly Color[] Palette =
        {
            Color.FromArgb(229, 57, 53), Color.FromArgb(251, 140, 0), Color.FromArgb(253, 216, 53), Color.FromArgb(67, 160, 71),
            Color.FromArgb(30, 136, 229), Color.FromArgb(142, 36, 170), Color.FromArgb(33, 33, 33), Color.FromArgb(255, 255, 255)
        };
        public static readonly float[] Widths = { 2f, 4f, 8f };
        public static readonly float[] FontSizes = { 14f, 18f, 24f, 28f, 36f, 48f, 64f, 88f, 120f };

        private readonly EditorStyle _style;
        private readonly List<Item> _items = new List<Item>();
        private readonly List<Rectangle> _separators = new List<Rectangle>();
        private readonly ToolTip _tip = new ToolTip();
        private float _scale = 1f;
        private Font _labelFont, _glyphFont, _textIconFont;
        private Item _hot, _pressed;
        private Color _customColor = Color.FromArgb(0, 188, 212);

        public Func<EditorCommand, bool> CommandEnabled;
        public event Action ToolChanged;
        public event Action<StyleField> StyleEdited;
        public event Action<EditorCommand> CommandInvoked;

        public EditorBar(EditorStyle style)
        {
            _style = style;
            if (Array.IndexOf(Palette, style.Color) < 0) _customColor = style.Color;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            // Щелчок по панели не забирает фокус: цвет, выбранный во время набора текста, применяется к этому тексту.
            SetStyle(ControlStyles.Selectable, false);
            TabStop = false;
            BackColor = EditorColors.Back;

            AddTool(0, EditTool.Select, Tr.S("Выбор и перемещение (V)", "Select and move (V)"));
            AddTool(0, EditTool.Pen, Tr.S("Карандаш (P)", "Pen (P)"));
            AddTool(0, EditTool.Marker, Tr.S("Маркер (H)", "Highlighter (H)"));
            AddTool(0, EditTool.Line, Tr.S("Линия (L) · Shift — шаг 45°", "Line (L) · Shift snaps to 45°"));
            AddTool(0, EditTool.Arrow, Tr.S("Стрелка (A) · Shift — шаг 45°", "Arrow (A) · Shift snaps to 45°"));
            AddTool(0, EditTool.Rect, Tr.S("Прямоугольник (R) · Shift — квадрат", "Rectangle (R) · Shift for a square"));
            AddTool(0, EditTool.Ellipse, Tr.S("Эллипс (O) · Shift — круг", "Ellipse (O) · Shift for a circle"));
            AddTool(0, EditTool.Step, Tr.S("Номер шага (N)", "Step number (N)"));
            AddTool(0, EditTool.Text, Tr.S("Текст (T)", "Text (T)"));
            AddTool(1, EditTool.Blur, Tr.S("Размытие (B) — скрыть данные", "Blur (B) — hide data"));
            AddTool(1, EditTool.Pixelate, Tr.S("Пикселизация (X) — скрыть данные", "Pixelate (X) — hide data"));
            AddTool(1, EditTool.Crop, Tr.S("Обрезка (C) · Enter — применить", "Crop (C) · Enter to apply"));
            AddCommand(2, EditorCommand.RotateLeft, Tr.S("Повернуть влево", "Rotate left"), null, false);
            AddCommand(2, EditorCommand.RotateRight, Tr.S("Повернуть вправо", "Rotate right"), null, false);
            AddCommand(3, EditorCommand.Undo, Tr.S("Отменить (Ctrl+Z)", "Undo (Ctrl+Z)"), null, false);
            AddCommand(3, EditorCommand.Redo, Tr.S("Повторить (Ctrl+Y)", "Redo (Ctrl+Y)"), null, false);
            foreach (Color c in Palette)
            {
                Item it = new Item();
                it.Kind = Kind.Swatch;
                it.Group = 4;
                it.Swatch = c;
                it.Tip = Tr.S("Цвет", "Color");
                _items.Add(it);
            }
            Item custom = new Item();
            custom.Kind = Kind.CustomColor;
            custom.Group = 4;
            custom.Tip = Tr.S("Другой цвет…", "Another color…");
            _items.Add(custom);
            foreach (float w in Widths)
            {
                Item it = new Item();
                it.Kind = Kind.Width;
                it.Group = 5;
                it.Value = w;
                it.Tip = Tr.S("Толщина линии", "Line width");
                _items.Add(it);
            }
            AddSimple(5, Kind.Fill, Tr.S("Заливка (F): фигура закрашена, текст на подложке", "Fill (F): solid shapes, text on a backing"));
            AddSimple(6, Kind.FontDown, Tr.S("Текст мельче", "Smaller text"));
            AddSimple(6, Kind.FontLabel, Tr.S("Размер текста", "Text size"));
            AddSimple(6, Kind.FontUp, Tr.S("Текст крупнее", "Larger text"));
            AddCommand(7, EditorCommand.Copy, Tr.S("Копировать в буфер обмена (Ctrl+C)", "Copy to clipboard (Ctrl+C)"), Tr.S("Копировать", "Copy"), true);
            AddCommand(7, EditorCommand.Save, Tr.S("Сохранить (Ctrl+S)", "Save (Ctrl+S)"), Tr.S("Сохранить", "Save"), true);
            AddCommand(7, EditorCommand.SaveAs, Tr.S("Сохранить как… (Ctrl+Shift+S)", "Save as… (Ctrl+Shift+S)"), Tr.S("Сохранить как…", "Save as…"), true);
            AddCommand(7, EditorCommand.Folder, Tr.S("Показать в папке", "Show in folder"), Tr.S("Папка", "Folder"), true);
            ApplyScale(1f);
        }

        private void AddTool(int group, EditTool tool, string tip)
        {
            Item it = new Item();
            it.Kind = Kind.Tool;
            it.Group = group;
            it.Tool = tool;
            it.Tip = tip;
            _items.Add(it);
        }

        private void AddCommand(int group, EditorCommand command, string tip, string label, bool output)
        {
            Item it = new Item();
            it.Kind = Kind.Command;
            it.Group = group;
            it.Command = command;
            it.Tip = tip;
            it.Label = label;
            it.Output = output;
            _items.Add(it);
        }

        private void AddSimple(int group, Kind kind, string tip)
        {
            Item it = new Item();
            it.Kind = kind;
            it.Group = group;
            it.Tip = tip;
            _items.Add(it);
        }

        public EditTool Tool { get { return _style.Tool; } }

        public static bool ToolForKey(Keys key, out EditTool tool)
        {
            tool = EditTool.Select;
            switch (key)
            {
                case Keys.V: tool = EditTool.Select; return true;
                case Keys.P: tool = EditTool.Pen; return true;
                case Keys.H: tool = EditTool.Marker; return true;
                case Keys.L: tool = EditTool.Line; return true;
                case Keys.A: tool = EditTool.Arrow; return true;
                case Keys.R: tool = EditTool.Rect; return true;
                case Keys.O: tool = EditTool.Ellipse; return true;
                case Keys.N: tool = EditTool.Step; return true;
                case Keys.T: tool = EditTool.Text; return true;
                case Keys.B: tool = EditTool.Blur; return true;
                case Keys.X: tool = EditTool.Pixelate; return true;
                case Keys.C: tool = EditTool.Crop; return true;
            }
            return false;
        }

        public void SelectTool(EditTool tool)
        {
            _style.Tool = tool;
            Invalidate();
            if (ToolChanged != null) ToolChanged();
        }

        public void ToggleFill()
        {
            _style.Fill = !_style.Fill;
            RaiseStyle(StyleField.Fill);
        }

        private void RaiseStyle(StyleField f)
        {
            Invalidate();
            if (StyleEdited != null) StyleEdited(f);
        }

        public void ApplyScale(float scale)
        {
            _scale = scale;
            if (_labelFont != null) _labelFont.Dispose();
            if (_glyphFont != null) _glyphFont.Dispose();
            if (_textIconFont != null) _textIconFont.Dispose();
            _labelFont = new Font("Segoe UI", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            _glyphFont = CapFonts.Icons(16f * scale);
            _textIconFont = new Font("Segoe UI", 14f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
            LayoutFor(Width > 0 ? Width : 1000);
        }

        private int S(float v) { return (int)Math.Round(v * _scale); }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            LayoutFor(Width);
        }

        private int ItemWidth(Item it)
        {
            switch (it.Kind)
            {
                case Kind.Swatch:
                case Kind.CustomColor: return S(28);
                case Kind.Width: return S(30);
                case Kind.FontDown:
                case Kind.FontUp: return S(28);
                case Kind.FontLabel: return S(34);
            }
            if (!it.Output || it.Label == null) return S(36);
            int text = TextRenderer.MeasureText(it.Label, _labelFont).Width;
            return S(10) + (CapFonts.HasIcons ? S(24) : 0) + text + S(8);
        }

        // Раскладка слева направо с переносом; группа вывода (копировать, сохранить) прижата вправо.
        public void LayoutFor(int width)
        {
            if (_labelFont == null) return;
            int size = S(36), gap = S(2), groupGap = S(14), pad = S(6);
            width = Math.Max(width, S(200));
            _separators.Clear();
            int x = pad, y = pad, lastGroup = -1;
            foreach (Item it in _items)
            {
                if (it.Output) continue;
                int w = ItemWidth(it);
                int step = x == pad ? 0 : it.Group != lastGroup ? groupGap : gap;
                if (x + step + w > width - pad && x > pad)
                {
                    x = pad;
                    y += size + gap;
                    step = 0;
                }
                if (step == groupGap) _separators.Add(new Rectangle(x + groupGap / 2, y + S(8), 1, size - S(16)));
                x += step;
                it.Bounds = new Rectangle(x, y, w, size);
                x += w;
                lastGroup = it.Group;
            }
            int outWidth = 0;
            foreach (Item it in _items) if (it.Output) outWidth += ItemWidth(it) + gap;
            int ox;
            if (x + groupGap + outWidth <= width - pad) ox = width - pad - outWidth;
            else
            {
                y += size + gap;
                ox = Math.Max(pad, width - pad - outWidth);
            }
            foreach (Item it in _items)
            {
                if (!it.Output) continue;
                int w = ItemWidth(it);
                if (ox + w > width - pad && ox > pad) { ox = pad; y += size + gap; }
                it.Bounds = new Rectangle(ox, y, w, size);
                ox += w + gap;
            }
            int height = y + size + pad;
            if (Height != height) Height = height;
            Invalidate();
        }

        private Item ItemAt(Point p)
        {
            foreach (Item it in _items) if (it.Bounds.Contains(p)) return it;
            return null;
        }

        private bool IsEnabled(Item it)
        {
            return it.Kind != Kind.Command || CommandEnabled == null || CommandEnabled(it.Command);
        }

        private bool Checked(Item it)
        {
            switch (it.Kind)
            {
                case Kind.Tool: return it.Tool == _style.Tool;
                case Kind.Swatch: return it.Swatch.ToArgb() == _style.Color.ToArgb();
                case Kind.CustomColor:
                    foreach (Color c in Palette) if (c.ToArgb() == _style.Color.ToArgb()) return false;
                    return true;
                case Kind.Width: return Math.Abs(it.Value - _style.Width) < 0.01f;
                case Kind.Fill: return _style.Fill;
            }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Item it = ItemAt(e.Location);
            if (it == _hot) return;
            _hot = it;
            _tip.Hide(this);
            if (it != null && it.Tip != null)
            {
                string tip = it.Kind == Kind.FontLabel ? it.Tip + ": " + ((int)_style.FontSize).ToString(CultureInfo.InvariantCulture) : it.Tip;
                _tip.Show(tip, this, it.Bounds.X, it.Bounds.Bottom + S(4), 4000);
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hot = null;
            _pressed = null;
            _tip.Hide(this);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            _pressed = ItemAt(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            Item it = ItemAt(e.Location);
            Item pressed = _pressed;
            _pressed = null;
            Invalidate();
            if (it != null && it == pressed && IsEnabled(it)) Activate(it);
        }

        private void Activate(Item it)
        {
            _tip.Hide(this);
            switch (it.Kind)
            {
                case Kind.Tool: SelectTool(it.Tool); break;
                case Kind.Command: if (CommandInvoked != null) CommandInvoked(it.Command); break;
                case Kind.Swatch: _style.Color = it.Swatch; RaiseStyle(StyleField.Color); break;
                case Kind.CustomColor:
                    using (ColorDialog dlg = new ColorDialog())
                    {
                        dlg.FullOpen = true;
                        dlg.Color = _customColor;
                        if (dlg.ShowDialog(FindForm()) != DialogResult.OK) return;
                        _customColor = Color.FromArgb(255, dlg.Color);
                    }
                    _style.Color = _customColor;
                    RaiseStyle(StyleField.Color);
                    break;
                case Kind.Width: _style.Width = it.Value; RaiseStyle(StyleField.Width); break;
                case Kind.Fill: ToggleFill(); break;
                case Kind.FontDown: StepFont(-1); break;
                case Kind.FontUp: StepFont(1); break;
            }
        }

        public void StepFont(int direction)
        {
            float next = _style.FontSize;
            if (direction > 0)
            {
                foreach (float f in FontSizes) if (f > _style.FontSize + 0.01f) { next = f; break; }
            }
            else
            {
                for (int i = FontSizes.Length - 1; i >= 0; i--) if (FontSizes[i] < _style.FontSize - 0.01f) { next = FontSizes[i]; break; }
            }
            if (Math.Abs(next - _style.FontSize) < 0.01f) return;
            _style.FontSize = next;
            RaiseStyle(StyleField.FontSize);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (Pen line = new Pen(EditorColors.Line)) g.DrawLine(line, 0, Height - 1, Width, Height - 1);
            foreach (Rectangle sep in _separators)
                using (SolidBrush b = new SolidBrush(EditorColors.Line)) g.FillRectangle(b, sep);
            foreach (Item it in _items)
            {
                bool enabled = IsEnabled(it), on = Checked(it);
                Rectangle r = it.Bounds;
                if (it.Kind != Kind.Swatch && it.Kind != Kind.CustomColor && it.Kind != Kind.FontLabel)
                {
                    Color back = Color.Empty;
                    if (on) back = Color.FromArgb(90, EditorColors.Accent);
                    else if (enabled && it == _pressed) back = Color.FromArgb(46, 255, 255, 255);
                    else if (enabled && it == _hot) back = Color.FromArgb(26, 255, 255, 255);
                    if (it.Output && it.Command == EditorCommand.Save && !on) back = it == _hot ? Color.FromArgb(215, EditorColors.Accent) : Color.FromArgb(170, EditorColors.Accent);
                    if (back != Color.Empty)
                    {
                        Rectangle box = Rectangle.Inflate(r, -S(2), -S(2));
                        using (GraphicsPath p = EditGeometry.Rounded(box, S(5)))
                        using (SolidBrush b = new SolidBrush(back))
                            g.FillPath(b, p);
                    }
                }
                Color fg = enabled ? EditorColors.Text : Color.FromArgb(90, 90, 90);
                DrawItem(g, it, r, fg, on);
            }
        }

        private void DrawItem(Graphics g, Item it, Rectangle r, Color fg, bool on)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            switch (it.Kind)
            {
                case Kind.Tool:
                    DrawToolIcon(g, it.Tool, new RectangleF(cx - S(10), cy - S(10), S(20), S(20)), fg);
                    return;
                case Kind.Swatch:
                case Kind.CustomColor:
                    DrawSwatch(g, new PointF(cx, cy), _scale, it.Kind == Kind.Swatch ? it.Swatch : _customColor, it.Kind == Kind.CustomColor, on, it == _hot);
                    return;
                case Kind.Width:
                {
                    float w = Math.Max(1.5f, it.Value * _scale * 0.8f);
                    using (Pen p = new Pen(fg, w))
                    {
                        p.StartCap = LineCap.Round;
                        p.EndCap = LineCap.Round;
                        g.DrawLine(p, cx - S(8), cy, cx + S(8), cy);
                    }
                    return;
                }
                case Kind.Fill:
                {
                    RectangleF box = new RectangleF(cx - S(8), cy - S(7), S(16), S(14));
                    using (Pen p = new Pen(fg, Math.Max(1.5f, 1.5f * _scale))) g.DrawRectangle(p, box.X, box.Y, box.Width, box.Height);
                    using (SolidBrush b = new SolidBrush(Color.FromArgb(on ? 255 : 90, fg)))
                        g.FillRectangle(b, box.X + S(3), box.Y + S(3), box.Width - S(6), box.Height - S(6));
                    return;
                }
                case Kind.FontDown:
                case Kind.FontUp:
                case Kind.FontLabel:
                {
                    string text = it.Kind == Kind.FontDown ? "A−" : it.Kind == Kind.FontUp ? "A+" : ((int)_style.FontSize).ToString(CultureInfo.InvariantCulture);
                    Font font = it.Kind == Kind.FontLabel ? _labelFont : _textIconFont;
                    TextRenderer.DrawText(g, text, font, r, it.Kind == Kind.FontLabel ? EditorColors.Dim : fg,
                                          TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    return;
                }
            }
            // Команды
            string glyph = null, fallback = null;
            switch (it.Command)
            {
                case EditorCommand.Copy: glyph = CapFonts.Copy; fallback = "⧉"; break;
                case EditorCommand.Save: glyph = CapFonts.Save; break;
                case EditorCommand.SaveAs: glyph = ""; break;
                case EditorCommand.Folder: glyph = CapFonts.Folder; fallback = Tr.S("Папка", "Folder"); break;
            }
            if (it.Output)
            {
                int x = r.X + S(10);
                if (CapFonts.HasIcons && glyph != null)
                {
                    Rectangle gr = new Rectangle(x, r.Y, it.Label != null ? S(20) : r.Width - S(20), r.Height);
                    TextRenderer.DrawText(g, glyph, _glyphFont, gr, fg, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    x += S(24);
                }
                string label = it.Label ?? (CapFonts.HasIcons ? null : fallback);
                if (label != null)
                    TextRenderer.DrawText(g, label, _labelFont, new Rectangle(x, r.Y, r.Right - x, r.Height), fg,
                                          TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                return;
            }
            RectangleF icon = new RectangleF(cx - S(10), cy - S(10), S(20), S(20));
            switch (it.Command)
            {
                case EditorCommand.RotateLeft: DrawTurn(g, icon, fg, false, false); break;
                case EditorCommand.RotateRight: DrawTurn(g, icon, fg, true, false); break;
                case EditorCommand.Undo: DrawTurn(g, icon, fg, false, true); break;
                case EditorCommand.Redo: DrawTurn(g, icon, fg, true, true); break;
            }
        }

        // Кружок цвета; custom — «другой цвет»: круг из четырёх цветов с выбранным в середине. Общий с панелью оверлея.
        internal static void DrawSwatch(Graphics g, PointF c, float scale, Color color, bool custom, bool on, bool hot)
        {
            float d = (float)Math.Round(18 * scale);
            RectangleF dot = new RectangleF(c.X - d / 2, c.Y - d / 2, d, d);
            if (on || hot)
            {
                float o = (float)Math.Round((on ? 26 : 24) * scale);
                using (Pen ring = new Pen(on ? EditorColors.Accent : Color.FromArgb(90, 255, 255, 255), (float)Math.Round(2 * scale)))
                    g.DrawEllipse(ring, c.X - o / 2, c.Y - o / 2, o, o);
            }
            if (!custom)
            {
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, dot);
            }
            else
            {
                Color[] wheel = { Color.FromArgb(229, 57, 53), Color.FromArgb(253, 216, 53), Color.FromArgb(67, 160, 71), Color.FromArgb(30, 136, 229) };
                for (int i = 0; i < 4; i++)
                    using (SolidBrush b = new SolidBrush(wheel[i])) g.FillPie(b, dot.X, dot.Y, dot.Width, dot.Height, i * 90 - 90, 90);
                float inner = d * 0.5f;
                using (SolidBrush b = new SolidBrush(color)) g.FillEllipse(b, c.X - inner / 2, c.Y - inner / 2, inner, inner);
            }
            using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), 1)) g.DrawEllipse(edge, dot);
        }

        // Значки рисуются векторами в сетке 20x20: одинаково на любом шрифте и масштабе.
        internal static void DrawToolIcon(Graphics g, EditTool tool, RectangleF box, Color fg)
        {
            float k = box.Width / 20f;
            Func<float, float, PointF> P = delegate(float x, float y) { return new PointF(box.X + x * k, box.Y + y * k); };
            using (Pen pen = new Pen(fg, Math.Max(1.4f, 1.6f * k)))
            using (SolidBrush brush = new SolidBrush(fg))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (tool)
                {
                    case EditTool.Select:
                        g.FillPolygon(brush, new PointF[] { P(5, 2), P(5, 16.5f), P(8.6f, 13.2f), P(11.2f, 18.6f), P(13.4f, 17.6f), P(10.9f, 12.3f), P(15.6f, 12.3f) });
                        break;
                    case EditTool.Pen:
                        g.DrawPolygon(pen, new PointF[] { P(4, 16), P(5, 12), P(14, 3), P(17, 6), P(8, 15) });
                        g.DrawLine(pen, P(12, 5), P(15, 8));
                        break;
                    case EditTool.Marker:
                        using (Pen wide = new Pen(Color.FromArgb(150, fg), 5f * k))
                        {
                            wide.StartCap = LineCap.Round;
                            wide.EndCap = LineCap.Round;
                            g.DrawLine(wide, P(4, 13), P(16, 7));
                        }
                        g.DrawLine(pen, P(3, 18), P(17, 18));
                        break;
                    case EditTool.Line:
                        g.DrawLine(pen, P(3.5f, 16.5f), P(16.5f, 3.5f));
                        break;
                    case EditTool.Arrow:
                        g.DrawLine(pen, P(3.5f, 16.5f), P(14.5f, 5.5f));
                        g.FillPolygon(brush, new PointF[] { P(17.5f, 2.5f), P(9.5f, 5), P(15, 10.5f) });
                        break;
                    case EditTool.Rect:
                        g.DrawRectangle(pen, box.X + 3 * k, box.Y + 5 * k, 14 * k, 10 * k);
                        break;
                    case EditTool.Ellipse:
                        g.DrawEllipse(pen, box.X + 2.5f * k, box.Y + 4 * k, 15 * k, 12 * k);
                        break;
                    case EditTool.Step:
                        g.DrawEllipse(pen, box.X + 2.5f * k, box.Y + 2.5f * k, 15 * k, 15 * k);
                        g.DrawLine(pen, P(8, 7.5f), P(10.5f, 6));
                        g.DrawLine(pen, P(10.5f, 6), P(10.5f, 14));
                        break;
                    case EditTool.Text:
                        g.DrawLine(pen, P(4, 4), P(16, 4));
                        g.DrawLine(pen, P(10, 4), P(10, 17));
                        g.DrawLine(pen, P(7.5f, 17), P(12.5f, 17));
                        break;
                    case EditTool.Blur:
                        using (GraphicsPath drop = new GraphicsPath())
                        {
                            drop.AddEllipse(box.X + 3 * k, box.Y + 3 * k, 14 * k, 14 * k);
                            using (PathGradientBrush soft = new PathGradientBrush(drop))
                            {
                                soft.CenterColor = fg;
                                soft.SurroundColors = new Color[] { Color.FromArgb(0, fg) };
                                g.FillPath(soft, drop);
                            }
                        }
                        break;
                    case EditTool.Pixelate:
                        for (int y = 0; y < 3; y++)
                            for (int x = 0; x < 3; x++)
                            {
                                int alpha = (x + y) % 2 == 0 ? 255 : 90;
                                using (SolidBrush cell = new SolidBrush(Color.FromArgb(alpha, fg)))
                                    g.FillRectangle(cell, box.X + (3.5f + x * 4.5f) * k, box.Y + (3.5f + y * 4.5f) * k, 4 * k, 4 * k);
                            }
                        break;
                    case EditTool.Crop:
                        g.DrawLines(pen, new PointF[] { P(6, 2), P(6, 14), P(18, 14) });
                        g.DrawLines(pen, new PointF[] { P(2, 6), P(14, 6), P(14, 18) });
                        break;
                }
            }
        }

        // Поворот — дуга в три четверти с разрывом сверху; отмена и повтор — полукруг над строкой. Стрелка — в конце дуги,
        // по направлению обхода. Углы GDI+ растут по часовой (ось Y вниз).
        internal static void DrawTurn(Graphics g, RectangleF box, Color fg, bool clockwise, bool history)
        {
            float k = box.Width / 20f;
            RectangleF arc;
            float start, sweep;
            if (history)
            {
                arc = new RectangleF(box.X + 3.5f * k, box.Y + 6 * k, 13 * k, 11 * k);
                start = clockwise ? 180f : 0f;
                sweep = clockwise ? 180f : -180f;
            }
            else
            {
                arc = new RectangleF(box.X + 3.5f * k, box.Y + 3.5f * k, 13 * k, 13 * k);
                start = clockwise ? 300f : 240f;
                sweep = clockwise ? 270f : -270f;
            }
            using (Pen pen = new Pen(fg, Math.Max(1.4f, 1.6f * k)))
            using (SolidBrush brush = new SolidBrush(fg))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, arc, start, sweep);
                double at = (start + sweep) * Math.PI / 180;
                PointF end = new PointF((float)(arc.X + arc.Width / 2 + Math.Cos(at) * arc.Width / 2),
                                        (float)(arc.Y + arc.Height / 2 + Math.Sin(at) * arc.Height / 2));
                double tangent = at + (sweep > 0 ? Math.PI / 2 : -Math.PI / 2);
                float tx = (float)Math.Cos(tangent), ty = (float)Math.Sin(tangent), len = 6f * k;
                g.FillPolygon(brush, new PointF[]
                {
                    new PointF(end.X + tx * len * 0.6f, end.Y + ty * len * 0.6f),
                    new PointF(end.X - tx * len * 0.4f - ty * len * 0.5f, end.Y - ty * len * 0.4f + tx * len * 0.5f),
                    new PointF(end.X - tx * len * 0.4f + ty * len * 0.5f, end.Y - ty * len * 0.4f - tx * len * 0.5f)
                });
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _tip.Dispose();
                if (_labelFont != null) _labelFont.Dispose();
                if (_glyphFont != null) _glyphFont.Dispose();
                if (_textIconFont != null) _textIconFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
