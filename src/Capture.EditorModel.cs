// Windows Process Cleaner — «Захват»: модель редактора снимка — фигуры, история правок, скрытие данных, сборка картинки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Фигуры векторные и лежат поверх растра: до сохранения растр не меняется. Сам растр меняют только обрезка и поворот —
// они создают новый Bitmap, а история держит ссылку на прежний, поэтому отменяются так же, как рисование. Размытие и
// пикселизация считаются при сборке из того, что под ними (растр и более ранние фигуры): в сохранённый файл исходные
// пиксели скрытой области не попадают.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WindowsProcessCleaner.Capture
{
    internal enum EditTool { Select, Pen, Marker, Line, Arrow, Rect, Ellipse, Step, Text, Blur, Pixelate, Crop }

    internal delegate PointF PointMap(PointF p);

    // ------------------------------------------------------------------ //
    //  Фигуры
    // ------------------------------------------------------------------ //
    internal abstract class CapShape
    {
        public Color Color = Color.FromArgb(229, 57, 53);
        public float Width = 4f;
        public bool Fill;

        public abstract RectangleF Bounds { get; }
        public abstract void Draw(Graphics g);
        public abstract bool HitTest(PointF p, float tolerance);
        // Сдвиг, обрезка и поворот — одно и то же преобразование точек.
        public abstract void Map(PointMap map);

        public void Offset(float dx, float dy)
        {
            Map(delegate(PointF p) { return new PointF(p.X + dx, p.Y + dy); });
        }

        public CapShape Clone()
        {
            CapShape c = (CapShape)MemberwiseClone();
            c.CloneParts();
            return c;
        }

        protected virtual void CloneParts() { }

        // Цвет цифр и текста поверх заливки: белый на тёмном, чёрный на светлом.
        public static Color Contrast(Color c)
        {
            return c.R * 0.299 + c.G * 0.587 + c.B * 0.114 > 160 ? Color.Black : Color.White;
        }
    }

    internal static class EditGeometry
    {
        public static float DistanceToSegment(PointF p, PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 <= 0 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        public static float Distance(PointF a, PointF b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            return (float)Math.Sqrt(dx * dx + dy * dy);
        }

        public static RectangleF Normalize(PointF a, PointF b)
        {
            return RectangleF.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        // Ручки 0..7 — как у выделения области: углы и середины сторон по часовой от левого верхнего.
        public static RectangleF Resize(RectangleF start, int handle, float dx, float dy)
        {
            float l = start.Left, t = start.Top, r = start.Right, b = start.Bottom;
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
            // Сторона, перетянутая ровно на противоположную, не схлопывает фигуру в линию нулевой ширины: такая
            // область скрытия ничего бы не скрывала и была бы не видна, чтобы её поправить.
            RectangleF n = Normalize(new PointF(l, t), new PointF(r, b));
            if (n.Width < 1) n.Width = 1;
            if (n.Height < 1) n.Height = 1;
            return n;
        }

        public static PointF[] HandlePoints(RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return new PointF[]
            {
                new PointF(r.Left, r.Top), new PointF(cx, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, cy),
                new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, cy)
            };
        }

        public static GraphicsPath Rounded(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Max(0.5f, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // Цвет в настройках — строкой «#RRGGBB».
    internal static class HexColor
    {
        public static string Format(Color c)
        {
            return "#" + c.R.ToString("X2", CultureInfo.InvariantCulture) + c.G.ToString("X2", CultureInfo.InvariantCulture)
                   + c.B.ToString("X2", CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string text, out Color color)
        {
            color = Color.Empty;
            string s = (text ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal)) s = s.Substring(1);
            int v;
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out v)) return false;
            color = Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
            return true;
        }
    }

    internal static class EditorFonts
    {
        private static FontFamily _family;

        // Segoe UI есть в любой Windows 10/11; семейство не освобождается — оно общее на весь процесс.
        public static FontFamily Family
        {
            get
            {
                if (_family == null)
                {
                    try { _family = new FontFamily("Segoe UI"); }
                    catch (ArgumentException) { _family = FontFamily.GenericSansSerif; }
                }
                return _family;
            }
        }
    }

    // Карандаш и полупрозрачный маркер: ломаная по точкам мыши.
    internal sealed class StrokeShape : CapShape
    {
        public List<PointF> Points = new List<PointF>();
        public bool Marker;

        public float StrokeWidth { get { return Marker ? Width * 4f : Width; } }

        public override RectangleF Bounds
        {
            get
            {
                if (Points.Count == 0) return RectangleF.Empty;
                float l = Points[0].X, t = Points[0].Y, r = l, b = t;
                foreach (PointF p in Points)
                {
                    l = Math.Min(l, p.X); t = Math.Min(t, p.Y);
                    r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
                }
                float half = StrokeWidth / 2;
                return RectangleF.FromLTRB(l - half, t - half, r + half, b + half);
            }
        }

        public override void Draw(Graphics g)
        {
            if (Points.Count == 0) return;
            Color c = Marker ? Color.FromArgb(110, Color) : Color;
            if (Points.Count == 1)
            {
                float d = StrokeWidth;
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, Points[0].X - d / 2, Points[0].Y - d / 2, d, d);
                return;
            }
            // Одна ломаная, а не отрезки по отдельности: у маркера на стыках не темнеют перекрытия.
            using (Pen pen = new Pen(c, StrokeWidth))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, Points.ToArray());
            }
        }

        public override bool HitTest(PointF p, float tolerance)
        {
            float limit = StrokeWidth / 2 + tolerance;
            if (Points.Count == 1) return EditGeometry.Distance(p, Points[0]) <= limit;
            for (int i = 1; i < Points.Count; i++)
                if (EditGeometry.DistanceToSegment(p, Points[i - 1], Points[i]) <= limit) return true;
            return false;
        }

        public override void Map(PointMap map)
        {
            for (int i = 0; i < Points.Count; i++) Points[i] = map(Points[i]);
        }

        protected override void CloneParts() { Points = new List<PointF>(Points); }
    }

    // Линия и стрелка.
    internal sealed class LineShape : CapShape
    {
        public PointF A, B;
        public bool Arrow;

        public float HeadLength { get { return Math.Max(14f, Width * 4.5f); } }

        public override RectangleF Bounds
        {
            get
            {
                RectangleF r = EditGeometry.Normalize(A, B);
                float pad = Arrow ? HeadLength : Width;
                r.Inflate(pad, pad);
                return r;
            }
        }

        public override void Draw(Graphics g)
        {
            using (Pen pen = new Pen(Color, Width))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                if (!Arrow)
                {
                    g.DrawLine(pen, A, B);
                    return;
                }
                PointF[] head;
                PointF shaftEnd = ArrowGeometry(out head);
                pen.EndCap = LineCap.Flat;
                g.DrawLine(pen, A, shaftEnd);
                using (SolidBrush b = new SolidBrush(Color)) g.FillPolygon(b, head);
            }
        }

        // Треугольник головы и точка, где кончается древко.
        internal PointF ArrowGeometry(out PointF[] head)
        {
            double dx = B.X - A.X, dy = B.Y - A.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ux = len > 0 ? dx / len : 1, uy = len > 0 ? dy / len : 0;
            // Короткая стрелка — голова во всю длину, а не торчащая назад.
            float hl = (float)Math.Min(HeadLength, Math.Max(len, 1));
            float hw = hl * 0.55f;
            PointF basePt = new PointF((float)(B.X - ux * hl), (float)(B.Y - uy * hl));
            head = new PointF[]
            {
                B,
                new PointF((float)(basePt.X - uy * hw), (float)(basePt.Y + ux * hw)),
                new PointF((float)(basePt.X + uy * hw), (float)(basePt.Y - ux * hw))
            };
            // Древко заходит под голову: иначе на стыке виден зазор сглаживания.
            return new PointF((float)(basePt.X + ux * hl * 0.3), (float)(basePt.Y + uy * hl * 0.3));
        }

        public override bool HitTest(PointF p, float tolerance)
        {
            if (EditGeometry.DistanceToSegment(p, A, B) <= Width / 2 + tolerance) return true;
            return Arrow && EditGeometry.Distance(p, B) <= HeadLength * 0.6f + tolerance;
        }

        public override void Map(PointMap map)
        {
            A = map(A);
            B = map(B);
        }
    }

    // Прямоугольник и эллипс: контур или заливка.
    internal sealed class BoxShape : CapShape
    {
        public RectangleF Rect;
        public bool Ellipse;

        public override RectangleF Bounds
        {
            get
            {
                RectangleF r = Rect;
                r.Inflate(Width / 2, Width / 2);
                return r;
            }
        }

        public override void Draw(Graphics g)
        {
            if (Rect.Width < 1 && Rect.Height < 1) return;
            if (Fill)
            {
                using (SolidBrush b = new SolidBrush(Color))
                {
                    if (Ellipse) g.FillEllipse(b, Rect);
                    else g.FillRectangle(b, Rect);
                }
                return;
            }
            using (Pen pen = new Pen(Color, Width))
            {
                if (Ellipse) g.DrawEllipse(pen, Rect);
                else g.DrawRectangle(pen, Rect.X, Rect.Y, Rect.Width, Rect.Height);
            }
        }

        public override bool HitTest(PointF p, float tolerance)
        {
            float edge = Width / 2 + tolerance;
            if (!Ellipse)
            {
                RectangleF outer = Rect;
                outer.Inflate(edge, edge);
                if (!outer.Contains(p)) return false;
                if (Fill) return true;
                RectangleF inner = Rect;
                inner.Inflate(-edge, -edge);
                return inner.Width <= 0 || inner.Height <= 0 || !inner.Contains(p);
            }
            double rx = Rect.Width / 2.0, ry = Rect.Height / 2.0;
            if (rx < 0.5 || ry < 0.5)
                return EditGeometry.DistanceToSegment(p, Rect.Location, new PointF(Rect.Right, Rect.Bottom)) <= edge;
            double nx = (p.X - (Rect.X + rx)) / rx, ny = (p.Y - (Rect.Y + ry)) / ry;
            double v = Math.Sqrt(nx * nx + ny * ny);
            double minR = Math.Min(rx, ry);
            if (Fill) return v <= 1 + edge / minR;
            return Math.Abs(v - 1) * minR <= edge;
        }

        public override void Map(PointMap map)
        {
            Rect = EditGeometry.Normalize(map(Rect.Location), map(new PointF(Rect.Right, Rect.Bottom)));
        }
    }

    // Нумерованная метка шага: круг с числом; размер следует толщине линии.
    internal sealed class StepShape : CapShape
    {
        public PointF Center;
        public int Number = 1;

        public float Radius { get { return 9f + Width * 2.5f; } }

        public override RectangleF Bounds
        {
            get
            {
                float r = Radius + 1;
                return new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
            }
        }

        public override void Draw(Graphics g)
        {
            float r = Radius;
            RectangleF box = new RectangleF(Center.X - r, Center.Y - r, r * 2, r * 2);
            Color fg = Contrast(Color);
            using (SolidBrush b = new SolidBrush(Color)) g.FillEllipse(b, box);
            using (Pen ring = new Pen(Color.FromArgb(200, fg), Math.Max(1.5f, r / 9f))) g.DrawEllipse(ring, box);
            string text = Number.ToString(CultureInfo.InvariantCulture);
            // Через контур, а не DrawString: число встаёт ровно в центр круга без учёта межстрочного запаса шрифта.
            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddString(text, EditorFonts.Family, (int)FontStyle.Bold, r * (text.Length > 1 ? 0.95f : 1.15f), PointF.Empty,
                               StringFormat.GenericTypographic);
                RectangleF ink = path.GetBounds();
                using (Matrix m = new Matrix())
                {
                    m.Translate(Center.X - (ink.X + ink.Width / 2), Center.Y - (ink.Y + ink.Height / 2));
                    path.Transform(m);
                }
                using (SolidBrush brush = new SolidBrush(fg)) g.FillPath(brush, path);
            }
        }

        public override bool HitTest(PointF p, float tolerance) { return EditGeometry.Distance(p, Center) <= Radius + tolerance; }

        public override void Map(PointMap map) { Center = map(Center); }
    }

    // Текст: с подложкой цвета или цветом с контрастной обводкой. Может быть в несколько строк.
    internal sealed class TextShape : CapShape
    {
        public PointF Origin;               // левый верхний угол строки
        public string Text = "";
        public float FontSize = 28f;        // в пикселях изображения

        private float Pad { get { return FontSize * 0.3f; } }

        private GraphicsPath BuildPath()
        {
            GraphicsPath path = new GraphicsPath();
            if (Text.Length > 0)
                path.AddString(Text, EditorFonts.Family, (int)FontStyle.Bold, FontSize, Origin, StringFormat.GenericTypographic);
            return path;
        }

        public override RectangleF Bounds
        {
            get
            {
                if (Text.Trim().Length == 0) return new RectangleF(Origin.X, Origin.Y, FontSize, FontSize * 1.3f);
                using (GraphicsPath path = BuildPath())
                {
                    RectangleF ink = path.GetBounds();
                    float pad = Fill ? Pad : Math.Max(2f, FontSize / 7f);
                    ink.Inflate(pad, pad);
                    return ink;
                }
            }
        }

        public override void Draw(Graphics g)
        {
            if (Text.Trim().Length == 0) return;
            using (GraphicsPath path = BuildPath())
            {
                if (Fill)
                {
                    RectangleF box = path.GetBounds();
                    box.Inflate(Pad, Pad * 0.8f);
                    using (GraphicsPath back = EditGeometry.Rounded(box, Pad))
                    using (SolidBrush b = new SolidBrush(Color))
                        g.FillPath(b, back);
                    using (SolidBrush fg = new SolidBrush(Contrast(Color))) g.FillPath(fg, path);
                    return;
                }
                // Без подложки текст обводится контрастным цветом — читается и на пёстром фоне.
                using (Pen outline = new Pen(Color.FromArgb(210, Contrast(Color)), Math.Max(2f, FontSize / 7f)))
                {
                    outline.LineJoin = LineJoin.Round;
                    g.DrawPath(outline, path);
                }
                using (SolidBrush b = new SolidBrush(Color)) g.FillPath(b, path);
            }
        }

        public override bool HitTest(PointF p, float tolerance)
        {
            RectangleF r = Bounds;
            r.Inflate(tolerance, tolerance);
            return r.Contains(p);
        }

        // Текст не поворачивается вместе с кадром: переносится его центр, строка остаётся горизонтальной.
        public override void Map(PointMap map)
        {
            RectangleF b = Bounds;
            PointF c = new PointF(b.X + b.Width / 2, b.Y + b.Height / 2);
            PointF moved = map(c);
            Origin = new PointF(Origin.X + moved.X - c.X, Origin.Y + moved.Y - c.Y);
        }
    }

    // Скрытие данных: размытие или пикселизация прямоугольника. Не рисуется пером — пересчитывает пиксели холста.
    internal sealed class RedactShape : CapShape
    {
        public RectangleF Rect;
        public bool Pixelate;

        public override RectangleF Bounds { get { return Rect; } }

        public override void Draw(Graphics g) { }

        public override bool HitTest(PointF p, float tolerance)
        {
            RectangleF r = Rect;
            r.Inflate(tolerance, tolerance);
            return r.Contains(p);
        }

        public override void Map(PointMap map)
        {
            Rect = EditGeometry.Normalize(map(Rect.Location), map(new PointF(Rect.Right, Rect.Bottom)));
        }

        public Rectangle PixelRect(Size canvas)
        {
            Rectangle r = Rectangle.FromLTRB((int)Math.Floor(Rect.Left), (int)Math.Floor(Rect.Top),
                                             (int)Math.Ceiling(Rect.Right), (int)Math.Ceiling(Rect.Bottom));
            return Rectangle.Intersect(r, new Rectangle(Point.Empty, canvas));
        }

        public void Apply(Bitmap canvas)
        {
            Rectangle r = PixelRect(canvas.Size);
            if (r.Width < 1 || r.Height < 1) return;
            if (Pixelate) Redaction.Pixelate(canvas, r, Redaction.BlockFor(r));
            else Redaction.Blur(canvas, r, Redaction.RadiusFor(r));
        }
    }

    // ------------------------------------------------------------------ //
    //  Размытие и пикселизация — по пикселям 32bppArgb, без unsafe
    // ------------------------------------------------------------------ //
    internal static class Redaction
    {
        // Крупнее для больших областей, но не мельче 6 px: строка обычного текста превращается в 2–3 квадрата.
        public static int BlockFor(Rectangle r) { return Math.Max(6, Math.Min(32, Math.Min(r.Width, r.Height) / 6)); }

        // Радиус с запасом: слабое размытие текста иногда читается, три прохода коробкой дают почти гауссово пятно.
        public static int RadiusFor(Rectangle r) { return Math.Max(6, Math.Min(40, Math.Min(r.Width, r.Height) / 5)); }

        public static void Pixelate(Bitmap bmp, Rectangle r, int block)
        {
            int[] px = Read(bmp, r);
            int w = r.Width, h = r.Height;
            block = Math.Max(1, block);
            for (int by = 0; by < h; by += block)
                for (int bx = 0; bx < w; bx += block)
                {
                    int ex = Math.Min(w, bx + block), ey = Math.Min(h, by + block);
                    long sa = 0, sr = 0, sg = 0, sb = 0;
                    int n = 0;
                    for (int y = by; y < ey; y++)
                        for (int x = bx; x < ex; x++)
                        {
                            int c = px[y * w + x];
                            sa += (c >> 24) & 0xFF;
                            sr += (c >> 16) & 0xFF;
                            sg += (c >> 8) & 0xFF;
                            sb += c & 0xFF;
                            n++;
                        }
                    int avg = Pack(sa / n, sr / n, sg / n, sb / n);
                    for (int y = by; y < ey; y++)
                        for (int x = bx; x < ex; x++) px[y * w + x] = avg;
                }
            Write(bmp, r, px);
        }

        public static void Blur(Bitmap bmp, Rectangle r, int radius)
        {
            int[] px = Read(bmp, r);
            int[] tmp = new int[px.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                Box(px, tmp, r.Width, r.Height, radius, true);
                Box(tmp, px, r.Width, r.Height, radius, false);
            }
            Write(bmp, r, px);
        }

        // Скользящее окно по строкам (horizontal) или столбцам; за краем повторяется крайний пиксель области.
        private static void Box(int[] src, int[] dst, int w, int h, int radius, bool horizontal)
        {
            int lines = horizontal ? h : w, len = horizontal ? w : h;
            int span = radius * 2 + 1;
            for (int line = 0; line < lines; line++)
            {
                long sa = 0, sr = 0, sg = 0, sb = 0;
                for (int i = -radius; i <= radius; i++)
                {
                    int c = src[Index(horizontal, line, Clamp(i, len), w)];
                    sa += (c >> 24) & 0xFF; sr += (c >> 16) & 0xFF; sg += (c >> 8) & 0xFF; sb += c & 0xFF;
                }
                for (int i = 0; i < len; i++)
                {
                    dst[Index(horizontal, line, i, w)] = Pack(sa / span, sr / span, sg / span, sb / span);
                    int gone = src[Index(horizontal, line, Clamp(i - radius, len), w)];
                    int come = src[Index(horizontal, line, Clamp(i + radius + 1, len), w)];
                    sa += ((come >> 24) & 0xFF) - ((gone >> 24) & 0xFF);
                    sr += ((come >> 16) & 0xFF) - ((gone >> 16) & 0xFF);
                    sg += ((come >> 8) & 0xFF) - ((gone >> 8) & 0xFF);
                    sb += (come & 0xFF) - (gone & 0xFF);
                }
            }
        }

        private static int Index(bool horizontal, int line, int i, int w) { return horizontal ? line * w + i : i * w + line; }

        private static int Clamp(int i, int len) { return i < 0 ? 0 : i >= len ? len - 1 : i; }

        private static int Pack(long a, long r, long g, long b)
        {
            return (int)(((a & 0xFF) << 24) | ((r & 0xFF) << 16) | ((g & 0xFF) << 8) | (b & 0xFF));
        }

        internal static int[] Read(Bitmap bmp, Rectangle r)
        {
            BitmapData d = bmp.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int[] px = new int[r.Width * r.Height];
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), px, y * r.Width, r.Width);
                return px;
            }
            finally { bmp.UnlockBits(d); }
        }

        private static void Write(Bitmap bmp, Rectangle r, int[] px)
        {
            BitmapData d = bmp.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(px, y * r.Width, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), r.Width);
            }
            finally { bmp.UnlockBits(d); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Документ: растр, фигуры, отмена и повтор
    // ------------------------------------------------------------------ //
    internal sealed class EditSnapshot
    {
        public Bitmap Image;
        public List<CapShape> Shapes;
        public int NextStep;
        public int Version;
    }

    internal sealed class EditorDoc : IDisposable
    {
        public const int MaxHistory = 300;

        private Bitmap _image;
        private readonly List<CapShape> _shapes = new List<CapShape>();
        private readonly List<EditSnapshot> _undo = new List<EditSnapshot>();
        private readonly List<EditSnapshot> _redo = new List<EditSnapshot>();
        private readonly List<Bitmap> _images = new List<Bitmap>();
        private int _version, _lastVersion, _savedVersion;
        public int NextStep = 1;

        // Bitmap переходит во владение документа.
        public EditorDoc(Bitmap image)
        {
            if (image == null) throw new ArgumentNullException("image");
            _image = ToArgb(image);
            _images.Add(_image);
        }

        public Bitmap Image { get { return _image; } }
        public List<CapShape> Shapes { get { return _shapes; } }
        public Size Size { get { return _image.Size; } }
        public bool CanUndo { get { return _undo.Count > 0; } }
        public bool CanRedo { get { return _redo.Count > 0; } }
        public int UndoDepth { get { return _undo.Count; } }
        public int RedoDepth { get { return _redo.Count; } }
        public int LiveImages { get { return _images.Count; } }
        public bool Dirty { get { return _version != _savedVersion; } }
        // Номер состояния: у каждой правки свой, отмена возвращает прежний.
        public int Version { get { return _version; } }

        public void MarkSaved() { _savedVersion = _version; }

        // Вернуть состояние, снятое перед незавершённой правкой (Esc посреди перетаскивания). История не меняется.
        public void Revert(EditSnapshot s)
        {
            _image = s.Image;
            _shapes.Clear();
            _shapes.AddRange(s.Shapes);
            NextStep = s.NextStep;
            _version = s.Version;
        }

        // Прежний Bitmap другого формата (32bppRgb с экрана, 24 бита из JPG) приводится к 32bppArgb и освобождается.
        private static Bitmap ToArgb(Bitmap image)
        {
            if (image.PixelFormat == PixelFormat.Format32bppArgb) return image;
            Bitmap b = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(image, new Rectangle(0, 0, image.Width, image.Height), 0, 0, image.Width, image.Height, GraphicsUnit.Pixel);
            }
            image.Dispose();
            return b;
        }

        public EditSnapshot Capture()
        {
            EditSnapshot s = new EditSnapshot();
            s.Image = _image;
            s.Shapes = new List<CapShape>(_shapes.Count);
            foreach (CapShape shape in _shapes) s.Shapes.Add(shape.Clone());
            s.NextStep = NextStep;
            s.Version = _version;
            return s;
        }

        // before — состояние до правки, снятое Capture(); сама правка к этому моменту уже применена.
        public void Commit(EditSnapshot before)
        {
            _undo.Add(before);
            if (_undo.Count > MaxHistory) _undo.RemoveAt(0);
            _redo.Clear();
            _version = ++_lastVersion;
            ReleaseImages();
        }

        public void Change(Action edit)
        {
            EditSnapshot before = Capture();
            edit();
            Commit(before);
        }

        public bool Undo() { return Step(_undo, _redo); }

        public bool Redo() { return Step(_redo, _undo); }

        private bool Step(List<EditSnapshot> from, List<EditSnapshot> to)
        {
            if (from.Count == 0) return false;
            to.Add(Capture());
            EditSnapshot s = from[from.Count - 1];
            from.RemoveAt(from.Count - 1);
            _image = s.Image;
            _shapes.Clear();
            _shapes.AddRange(s.Shapes);
            NextStep = s.NextStep;
            _version = s.Version;
            return true;
        }

        public void Add(CapShape shape) { Change(delegate { _shapes.Add(shape); }); }

        public void Remove(CapShape shape)
        {
            int i = _shapes.IndexOf(shape);
            if (i < 0) return;
            Change(delegate { _shapes.RemoveAt(i); });
        }

        // Другой кусок того же экрана под теми же фигурами: область в оверлее растянули или сжали с уже нарисованным.
        // Фигуры — и в истории тоже — сдвигаются на (dx, dy) и остаются на своих местах экрана; это не шаг отмены.
        // Только для документа без обрезки и поворота: у всей истории один растр, и он заменяется целиком.
        public void Reframe(Bitmap image, float dx, float dy)
        {
            Bitmap next = ToArgb(image);
            foreach (CapShape s in _shapes) s.Offset(dx, dy);
            foreach (EditSnapshot snap in _undo) Shift(snap, next, dx, dy);
            foreach (EditSnapshot snap in _redo) Shift(snap, next, dx, dy);
            foreach (Bitmap b in _images) b.Dispose();
            _images.Clear();
            _images.Add(next);
            _image = next;
        }

        private static void Shift(EditSnapshot snap, Bitmap image, float dx, float dy)
        {
            snap.Image = image;
            foreach (CapShape s in snap.Shapes) s.Offset(dx, dy);
        }

        // Обрезка по прямоугольнику в пикселях текущего растра; фигуры сдвигаются вместе с кадром.
        public bool Crop(Rectangle area)
        {
            Rectangle r = Rectangle.Intersect(area, new Rectangle(Point.Empty, _image.Size));
            if (r.Width < 1 || r.Height < 1 || r.Size == _image.Size) return false;
            Bitmap cropped = _image.Clone(r, PixelFormat.Format32bppArgb);
            Change(delegate
            {
                _images.Add(cropped);
                _image = cropped;
                foreach (CapShape s in _shapes) s.Offset(-r.X, -r.Y);
            });
            return true;
        }

        public void Rotate(bool clockwise)
        {
            Bitmap turned = _image.Clone(new Rectangle(Point.Empty, _image.Size), PixelFormat.Format32bppArgb);
            turned.RotateFlip(clockwise ? RotateFlipType.Rotate90FlipNone : RotateFlipType.Rotate270FlipNone);
            int w = _image.Width, h = _image.Height;
            PointMap map = clockwise
                ? (PointMap)delegate(PointF p) { return new PointF(h - p.Y, p.X); }
                : (PointMap)delegate(PointF p) { return new PointF(p.Y, w - p.X); };
            Change(delegate
            {
                _images.Add(turned);
                _image = turned;
                foreach (CapShape s in _shapes) s.Map(map);
            });
        }

        // Готовая картинка: растр, затем фигуры по порядку. skip — фигура, которую холст сейчас рисует сам.
        public Bitmap Render(CapShape skip)
        {
            Bitmap canvas = _image.Clone(new Rectangle(Point.Empty, _image.Size), PixelFormat.Format32bppArgb);
            Graphics g = null;
            try
            {
                foreach (CapShape s in _shapes)
                {
                    if (s == skip) continue;
                    RedactShape redact = s as RedactShape;
                    if (redact != null)
                    {
                        // Пиксели читаются через LockBits — поверх уже нарисованного, поэтому Graphics закрывается.
                        if (g != null) { g.Dispose(); g = null; }
                        redact.Apply(canvas);
                        continue;
                    }
                    if (g == null) g = Prepare(Graphics.FromImage(canvas));
                    s.Draw(g);
                }
            }
            finally
            {
                if (g != null) g.Dispose();
            }
            return canvas;
        }

        public static Graphics Prepare(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            return g;
        }

        // Растры, на которые больше не ссылается ни история, ни текущее состояние (очищенный повтор, старые шаги).
        private void ReleaseImages()
        {
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                Bitmap b = _images[i];
                if (b == _image || Referenced(_undo, b) || Referenced(_redo, b)) continue;
                b.Dispose();
                _images.RemoveAt(i);
            }
        }

        private static bool Referenced(List<EditSnapshot> list, Bitmap b)
        {
            foreach (EditSnapshot s in list) if (s.Image == b) return true;
            return false;
        }

        public void Dispose()
        {
            foreach (Bitmap b in _images) b.Dispose();
            _images.Clear();
            _undo.Clear();
            _redo.Clear();
        }
    }
}
