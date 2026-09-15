// SysDeck — оверлей показателей: столбик (или строка) поверх всего со строками, графиками и
// мин./сред./макс. Модель и каталог — Capture.HudModel.cs, пороги и вид — Capture.HudStyle.cs, источники чисел —
// Capture.HudSources.cs / Capture.HudExternal.cs, запись лагов — Capture.HudLag.cs.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Окно слоистое (UpdateLayeredWindow с попиксельной альфой), всегда наверху, мышь его не видит (WS_EX_TRANSPARENT),
// фокус не берёт. Поверх обычных окон и игр в оконном или безрамочном режиме оно видно; в эксклюзивном
// полноэкранном DirectX — нет: для этого пришлось бы внедряться в процесс игры, а это не делается сознательно.
// Исключение — режим «перетащить»: на это время окно ловит мышь, отпускание кнопки сохраняет место.
//
// Такт сборщика — 250 мс; у каждой строки свой интервал: значение и точка графика обновляются, только когда он
// прошёл (память раз в 2 с, частоты раз в секунду — как выбрал человек). Отрисовка — только если что-то сменилось.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.Capture
{
    // Порядок не менять — имя хранится в настройках. Центральные положения и своё место добавлены позже.
    internal enum HudCorner { TopLeft, TopRight, BottomLeft, BottomRight, MiddleLeft, MiddleRight, TopCenter, BottomCenter, Custom }

    // ------------------------------------------------------------------ //
    //  Отрисовка в картинку — общая для оверлея и предпросмотра на странице настроек
    // ------------------------------------------------------------------ //
    internal static class HudRender
    {
        public static Bitmap Draw(List<HudRow> rows, float scale, int opacityPercent)
        {
            HudStyle style = new HudStyle();
            style.Opacity = opacityPercent;
            return Draw(rows, scale, style);
        }

        private sealed class Fonts : IDisposable
        {
            public Font Label, Value, Stats, Header;

            public Fonts(HudStyle st, float scale)
            {
                float px = Math.Max(HudStyle.MinFontSize, Math.Min(HudStyle.MaxFontSize, st.FontSize)) * scale;
                Label = new Font(st.Font, px * 0.92f, st.BoldLabels ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                Value = new Font(st.Font, px, FontStyle.Bold, GraphicsUnit.Pixel);
                Stats = new Font(st.Font, px * 0.78f, FontStyle.Regular, GraphicsUnit.Pixel);
                Header = new Font(st.Font, px * 0.92f, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            public void Dispose() { Label.Dispose(); Value.Dispose(); Stats.Dispose(); Header.Dispose(); }
        }

        public static Bitmap Draw(List<HudRow> rows, float scale, HudStyle style)
        {
            style = style ?? new HudStyle();
            if ((rows == null || rows.Count == 0) && string.IsNullOrEmpty(style.Header))
                rows = new List<HudRow> { new HudRow(Tr.S("Показатели", "Metrics"), Tr.S("ничего не выбрано", "nothing selected"), 0) };
            if (rows == null) rows = new List<HudRow>();
            using (Fonts fonts = new Fonts(style, scale))
            using (Bitmap measureBmp = new Bitmap(1, 1))
            using (Graphics mg = Graphics.FromImage(measureBmp))
            {
                // Мерить тем же движком и форматом, которым рисуется: TextRenderer (GDI) и DrawString (GDI+) расходятся
                // на несколько пикселей, и хвост значения («ГБ») обрезался рамкой столбца.
                mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                return style.RowLayout ? DrawLine(rows, scale, style, fonts, mg) : DrawColumn(rows, scale, style, fonts, mg);
            }
        }

        private static int Measure(Graphics mg, string text, Font font)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            return (int)Math.Ceiling(mg.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic).Width) + 1;
        }

        // ---- столбик: «подпись — значение — мин/сред/макс», график полосой под строкой ----
        private static Bitmap DrawColumn(List<HudRow> rows, float scale, HudStyle style, Fonts fonts, Graphics mg)
        {
            int pad = (int)(8 * scale), gap = (int)(12 * scale), rowH = (int)Math.Ceiling(fonts.Value.GetHeight() + 4 * scale);
            int graphW = (int)(140 * scale), maxValueW = (int)(560 * scale);
            int labelW = 0, valueW = 0, statsW = 0;
            bool anyGraph = false;
            foreach (HudRow r in rows)
            {
                labelW = Math.Max(labelW, Measure(mg, r.Label, fonts.Label));
                if (r.TextShown) valueW = Math.Max(valueW, Measure(mg, r.Value, fonts.Value));
                statsW = Math.Max(statsW, Measure(mg, r.Stats, fonts.Stats));
                if (r.Graph) anyGraph = true;
            }
            valueW = Math.Min(valueW, maxValueW);
            int headerW = Measure(mg, style.Header, fonts.Header);
            // График — полосой ПОД строкой во всю ширину (как в Afterburner/RTSS): сбоку от числа он читался как
            // стоящий перед значением следующей строки.
            int contentW = Math.Max(Math.Max(labelW + (valueW > 0 ? gap + valueW : 0) + (statsW > 0 ? gap + statsW : 0), anyGraph ? graphW : 0), headerW);
            int height = pad * 2 + (headerW > 0 ? rowH : 0);
            foreach (HudRow r in rows) height += RowHeight(r, rowH);
            Size size = new Size(Math.Max(pad * 2 + contentW, (int)(60 * scale)), height);
            Bitmap bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Background(g, size, scale, style);
                int y = pad;
                if (headerW > 0)
                {
                    Text(g, style.Header, fonts.Header, style.HeaderColor != 0 ? Color.FromArgb(style.HeaderColor) : Color.White,
                         new RectangleF(pad, y, contentW + 1, rowH), false, style.Shadow, scale);
                    y += rowH;
                }
                int valueRight = pad + contentW - (statsW > 0 ? gap + statsW : 0);
                foreach (HudRow r in rows)
                {
                    int h = RowHeight(r, rowH);
                    if (r.Level >= 2) Alert(g, new Rectangle((int)(pad / 2), y, size.Width - pad, rowH), scale);
                    Text(g, r.Label, fonts.Label, LabelColor(r, style), new RectangleF(pad, y, labelW + 1, rowH), false, style.Shadow, scale);
                    if (r.TextShown && valueW > 0)
                        Text(g, r.Value, fonts.Value, ValueColor(r), new RectangleF(valueRight - valueW - 1, y, valueW + 1, rowH), true, style.Shadow, scale);
                    if (!string.IsNullOrEmpty(r.Stats))
                        Text(g, r.Stats, fonts.Stats, Color.FromArgb(200, 200, 205), new RectangleF(pad + contentW - statsW - 1, y, statsW + 1, rowH), true, style.Shadow, scale);
                    if (r.Graph)
                        Graph(g, r, new Rectangle(pad, y + rowH, contentW, rowH - (int)(2 * scale)), scale);
                    y += h;
                }
                if (style.MoveMode) MoveFrame(g, size, scale);
            }
            return bmp;
        }

        // ---- строка вдоль экрана: ячейки «подпись значение», графики под ячейками ----
        private static Bitmap DrawLine(List<HudRow> rows, float scale, HudStyle style, Fonts fonts, Graphics mg)
        {
            int pad = (int)(8 * scale), gap = (int)(6 * scale), cellGap = (int)(16 * scale);
            int rowH = (int)Math.Ceiling(fonts.Value.GetHeight() + 4 * scale), graphMin = (int)(90 * scale);
            List<int> widths = new List<int>();
            bool anyGraph = false;
            int total = 0;
            int headerW = Measure(mg, style.Header, fonts.Header);
            if (headerW > 0) total += headerW + cellGap;
            foreach (HudRow r in rows)
            {
                int w = Measure(mg, r.Label, fonts.Label);
                if (r.TextShown) w += gap + Measure(mg, r.Value, fonts.Value);
                int sw = Measure(mg, r.Stats, fonts.Stats);
                if (sw > 0) w += gap + sw;
                if (r.Graph) { w = Math.Max(w, graphMin); anyGraph = true; }
                widths.Add(w);
                total += w + cellGap;
            }
            if (total > 0) total -= cellGap;
            Size size = new Size(Math.Max(pad * 2 + total, (int)(60 * scale)), pad * 2 + rowH * (anyGraph ? 2 : 1));
            Bitmap bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                Background(g, size, scale, style);
                int x = pad, y = pad;
                if (headerW > 0)
                {
                    Text(g, style.Header, fonts.Header, style.HeaderColor != 0 ? Color.FromArgb(style.HeaderColor) : Color.White,
                         new RectangleF(x, y, headerW + 1, rowH), false, style.Shadow, scale);
                    x += headerW + cellGap;
                }
                for (int i = 0; i < rows.Count; i++)
                {
                    HudRow r = rows[i];
                    int w = widths[i];
                    if (r.Level >= 2) Alert(g, new Rectangle(x - gap, y, w + gap * 2, rowH), scale);
                    int lw = Measure(mg, r.Label, fonts.Label);
                    Text(g, r.Label, fonts.Label, LabelColor(r, style), new RectangleF(x, y, lw + 1, rowH), false, style.Shadow, scale);
                    int cx = x + lw;
                    if (r.TextShown)
                    {
                        int vw = Measure(mg, r.Value, fonts.Value);
                        Text(g, r.Value, fonts.Value, ValueColor(r), new RectangleF(cx + gap, y, vw + 1, rowH), false, style.Shadow, scale);
                        cx += gap + vw;
                    }
                    int sw = Measure(mg, r.Stats, fonts.Stats);
                    if (sw > 0) Text(g, r.Stats, fonts.Stats, Color.FromArgb(200, 200, 205), new RectangleF(cx + gap, y, sw + 1, rowH), false, style.Shadow, scale);
                    if (r.Graph) Graph(g, r, new Rectangle(x, y + rowH, w, rowH - (int)(2 * scale)), scale);
                    if (i < rows.Count - 1)
                        using (Pen sep = new Pen(Color.FromArgb(60, 255, 255, 255), Math.Max(1f, scale)))
                            g.DrawLine(sep, x + w + cellGap / 2, y + 3 * scale, x + w + cellGap / 2, y + rowH - 3 * scale);
                    x += w + cellGap;
                }
                if (style.MoveMode) MoveFrame(g, size, scale);
            }
            return bmp;
        }

        private static void Background(Graphics g, Size size, float scale, HudStyle style)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            int alpha = Math.Max(0, Math.Min(255, style.Opacity * 255 / 100));
            using (GraphicsPath path = Rounded(new Rectangle(0, 0, size.Width - 1, size.Height - 1), (int)(8 * scale)))
            using (SolidBrush bg = new SolidBrush(Color.FromArgb(alpha, 18, 18, 22)))
                g.FillPath(bg, path);
        }

        // Критическое значение — красная подложка под всей строкой: видно боковым зрением, не читая числа.
        private static void Alert(Graphics g, Rectangle r, float scale)
        {
            using (GraphicsPath path = Rounded(r, (int)(4 * scale)))
            using (SolidBrush b = new SolidBrush(Color.FromArgb(70, 255, 70, 60)))
                g.FillPath(b, path);
        }

        private static void MoveFrame(Graphics g, Size size, float scale)
        {
            using (Pen pen = new Pen(Color.FromArgb(255, 90, 180, 255), Math.Max(2f, 2f * scale)))
            {
                pen.DashStyle = DashStyle.Dash;
                g.DrawRectangle(pen, 1, 1, size.Width - 3, size.Height - 3);
            }
        }

        private static Color LabelColor(HudRow r, HudStyle style)
        {
            if (r.LabelColor != 0) return Color.FromArgb(255, Color.FromArgb(r.LabelColor));
            return Color.FromArgb(style.GroupColors ? HudStyle.GroupColor(r.Group) : HudStyle.NeutralLabel);
        }

        private static Color ValueColor(HudRow r)
        {
            // Тревога важнее выбранного цвета: иначе раскрашенная строка никогда не покраснеет.
            if (r.Level > 0) return LevelColor(r.Level);
            return r.Color != 0 ? Color.FromArgb(255, Color.FromArgb(r.Color)) : Color.White;
        }

        private static void Text(Graphics g, string text, Font font, Color color, RectangleF box, bool right, bool shadow, float scale)
        {
            if (string.IsNullOrEmpty(text)) return;
            using (StringFormat f = new StringFormat(StringFormat.GenericTypographic))
            {
                f.Alignment = right ? StringAlignment.Far : StringAlignment.Near;
                f.LineAlignment = StringAlignment.Center;
                f.Trimming = StringTrimming.EllipsisCharacter;
                f.FormatFlags |= StringFormatFlags.NoWrap;
                if (shadow)
                {
                    float o = Math.Max(1f, scale);
                    using (SolidBrush sb = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                        g.DrawString(text, font, sb, new RectangleF(box.X + o, box.Y + o, box.Width, box.Height), f);
                }
                using (SolidBrush b = new SolidBrush(color))
                    g.DrawString(text, font, b, box, f);
            }
        }

        // Строка с графиком — строка текста и под ней полоса графика той же высоты.
        private static int RowHeight(HudRow r, int rowH) { return r.Graph ? rowH * 2 : rowH; }

        private static void Graph(Graphics g, HudRow r, Rectangle box, float scale)
        {
            using (SolidBrush band = new SolidBrush(Color.FromArgb(28, 255, 255, 255)))
                g.FillRectangle(band, box);
            double[] pts = r.Points;
            if (pts == null || pts.Length == 0) return;
            double min = r.Min, max = r.Max;
            if (max <= min)
            {
                min = 0; max = 0;
                foreach (double v in pts) if (HudFormat.Valid(v)) max = Math.Max(max, v);
                if (r.Target > 0) max = Math.Max(max, r.Target);
                max = max <= 0 ? 1 : max * 1.15;
            }
            else
                foreach (double v in pts) if (HudFormat.Valid(v) && v > max) max = v;   // температура выше 100 — шкала растёт
            int slots = Math.Max(2, Math.Max(r.Slots, pts.Length));
            float step = (float)box.Width / (slots - 1);
            Color c = r.Color != 0 ? Color.FromArgb(255, Color.FromArgb(r.Color)) : r.Level >= 2 ? LevelColor(2) : Color.FromArgb(90, 200, 255);
            // Линия цели: время кадра под частоту монитора — всё, что выше неё, монитор уже не покажет вовремя.
            if (r.Target > 0 && r.Target > min && r.Target < max)
                using (Pen target = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, scale)))
                {
                    target.DashStyle = DashStyle.Dot;
                    float ty = box.Bottom - (float)((r.Target - min) / (max - min) * box.Height);
                    g.DrawLine(target, box.Left, ty, box.Right, ty);
                }
            List<PointF> line = new List<PointF>();
            using (Pen pen = new Pen(c, Math.Max(1f, 1.4f * scale)))
            using (SolidBrush fill = new SolidBrush(Color.FromArgb(60, c)))
            {
                pen.LineJoin = LineJoin.Round;
                for (int i = 0; i <= pts.Length; i++)
                {
                    bool gap = i == pts.Length || !HudFormat.Valid(pts[i]);
                    if (!gap)
                    {
                        float x = box.Right - (pts.Length - 1 - i) * step;
                        double k = (Math.Max(min, Math.Min(max, pts[i])) - min) / (max - min);
                        line.Add(new PointF(x, box.Bottom - (float)(k * box.Height)));
                        continue;
                    }
                    if (line.Count >= 2)
                    {
                        List<PointF> area = new List<PointF>(line);
                        area.Add(new PointF(line[line.Count - 1].X, box.Bottom));
                        area.Add(new PointF(line[0].X, box.Bottom));
                        g.FillPolygon(fill, area.ToArray());
                        g.DrawLines(pen, line.ToArray());
                    }
                    line.Clear();
                }
            }
        }

        public static Color LevelColor(int level)
        {
            return level >= 2 ? Color.FromArgb(255, 96, 86) : level == 1 ? Color.FromArgb(255, 204, 64) : Color.White;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(2, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }

    // ------------------------------------------------------------------ //
    //  Окно: слоистое, наверху, насквозь для мыши, без фокуса
    // ------------------------------------------------------------------ //
    internal sealed class HudWindow : Form
    {
        private const int WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80,
                          WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000, GWL_EXSTYLE = -20;
        private const int WM_NCHITTEST = 0x84, WM_EXITSIZEMOVE = 0x232, HTCAPTION = 2;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_NOOWNERZORDER = 0x200, SWP_FRAMECHANGED = 0x20;

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

        private bool _inCaptures, _move;

        // Место после перетаскивания: левый верхний угол и размер окна на экране.
        public event Action<Rectangle> Moved;

        public HudWindow()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Text = "SysDeck — HUD";
            Bounds = new Rectangle(-32000, -32000, 1, 1);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        public bool MoveMode { get { return _move; } }

        // Режим «перетащить»: окно начинает ловить мышь (без WS_EX_TRANSPARENT), вся его площадь — «заголовок».
        public void SetMoveMode(bool on)
        {
            _move = on;
            if (!IsHandleCreated) return;
            long ex = GetWindowLongPtr(Handle, GWL_EXSTYLE).ToInt64();
            ex = on ? ex & ~(long)WS_EX_TRANSPARENT : ex | WS_EX_TRANSPARENT;
            SetWindowLongPtr(Handle, GWL_EXSTYLE, new IntPtr(ex));
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        protected override void WndProc(ref Message m)
        {
            if (_move && m.Msg == WM_NCHITTEST) { m.Result = new IntPtr(HTCAPTION); return; }
            base.WndProc(ref m);
            if (_move && m.Msg == WM_EXITSIZEMOVE)
            {
                Action<Rectangle> h = Moved;
                if (h != null) h(Bounds);
            }
        }

        // Показывать ли столбик на своих же снимках и видео. Нет — окно исключается из захвата (Windows 10 2004+).
        public void SetInCaptures(bool value)
        {
            _inCaptures = value;
            if (IsHandleCreated) ApplyAffinity();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyAffinity();
            if (_move) SetMoveMode(true);
        }

        private void ApplyAffinity()
        {
            try { CapNative.SetWindowDisplayAffinity(Handle, _inCaptures ? 0u : CapNative.WDA_EXCLUDEFROMCAPTURE); }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static float ScaleFor(int monitor, int scalePercent)
        {
            Rectangle area = HudLayout.MonitorArea(monitor);
            return Math.Max(1f, CapDpi.ScaleAt(new Point(area.X + area.Width / 2, area.Y + area.Height / 2)))
                   * Math.Max(50, Math.Min(300, scalePercent)) / 100f;
        }

        public void Render(List<HudRow> rows, CapSettings s, HudStyle style)
        {
            Rectangle area = HudLayout.MonitorArea(s.HudMonitor);
            float scale = ScaleFor(s.HudMonitor, s.HudScale);
            using (Bitmap bmp = HudRender.Draw(rows, scale, style))
            {
                // Пока человек тащит окно, место задаёт мышь, а не настройки.
                Point at = _move && Bounds.X > -30000 ? Bounds.Location
                         : HudLayout.Place(area, bmp.Size, s.HudCorner, (int)(12 * scale), s.HudX, s.HudY);
                FolderSize.LayeredWindow.Paint(Handle, bmp, at);
            }
            // Безрамочная игра или другое окно «всегда наверху» могли встать выше — возвращаемся наверх каждый такт.
            SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    internal static class HudLayout
    {
        private static List<Screen> Ordered()
        {
            List<Screen> ordered = new List<Screen>();
            ordered.Add(Screen.PrimaryScreen);
            foreach (Screen s in Screen.AllScreens) if (!s.Primary) ordered.Add(s);
            return ordered;
        }

        // 0 — основной монитор, дальше остальные в порядке Windows. Отключённый монитор — основной.
        public static Rectangle MonitorArea(int index)
        {
            List<Screen> ordered = Ordered();
            Screen chosen = index >= 0 && index < ordered.Count ? ordered[index] : Screen.PrimaryScreen;
            return chosen.WorkingArea;
        }

        public static int MonitorIndexAt(Point p)
        {
            List<Screen> ordered = Ordered();
            for (int i = 0; i < ordered.Count; i++) if (ordered[i].Bounds.Contains(p)) return i;
            return 0;
        }

        public static int MonitorCount { get { return Screen.AllScreens.Length; } }

        public static Point Place(Rectangle area, Size size, HudCorner corner, int margin)
        {
            return Place(area, size, corner, margin, 1000, 0);
        }

        // Своё место — доля свободного поля монитора в тысячных: у левого края 0, у правого 1000. Так место
        // переживает смену разрешения и размера столбика.
        public static Point Place(Rectangle area, Size size, HudCorner corner, int margin, int xPermille, int yPermille)
        {
            if (corner == HudCorner.Custom)
            {
                int fx = Math.Max(0, area.Width - size.Width), fy = Math.Max(0, area.Height - size.Height);
                return new Point(area.Left + (int)((long)fx * Math.Max(0, Math.Min(1000, xPermille)) / 1000),
                                 area.Top + (int)((long)fy * Math.Max(0, Math.Min(1000, yPermille)) / 1000));
            }
            bool right = corner == HudCorner.TopRight || corner == HudCorner.BottomRight || corner == HudCorner.MiddleRight;
            bool bottom = corner == HudCorner.BottomLeft || corner == HudCorner.BottomRight || corner == HudCorner.BottomCenter;
            bool hCenter = corner == HudCorner.TopCenter || corner == HudCorner.BottomCenter;
            bool vCenter = corner == HudCorner.MiddleLeft || corner == HudCorner.MiddleRight;
            int x = hCenter ? area.Left + (area.Width - size.Width) / 2
                  : right ? area.Right - size.Width - margin : area.Left + margin;
            int y = vCenter ? area.Top + (area.Height - size.Height) / 2
                  : bottom ? area.Bottom - size.Height - margin : area.Top + margin;
            return new Point(Math.Max(area.Left, x), Math.Max(area.Top, y));
        }

        // Обратное к Place для своего места: где встал столбик → доли свободного поля.
        public static void ToPermille(Rectangle area, Rectangle window, out int xPermille, out int yPermille)
        {
            int fx = Math.Max(1, area.Width - window.Width), fy = Math.Max(1, area.Height - window.Height);
            xPermille = Math.Max(0, Math.Min(1000, (int)((long)(window.Left - area.Left) * 1000 / fx)));
            yPermille = Math.Max(0, Math.Min(1000, (int)((long)(window.Top - area.Top) * 1000 / fy)));
        }
    }

    // ------------------------------------------------------------------ //
    //  Строки по выбору: у каждой свой интервал, история для графика и мин./сред./макс.
    // ------------------------------------------------------------------ //
    internal sealed class HudBoard
    {
        private readonly HudHistory _history = new HudHistory(2400);
        private readonly Dictionary<string, HudValue> _shown = new Dictionary<string, HudValue>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _due = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _statsFrom;
        private double _hz = double.NaN;
        private DateTime _now = DateTime.Now;

        // Обновить строки, чей интервал прошёл. true — что-то сменилось и столбик надо перерисовать.
        public bool Advance(HudFrame frame, IList<HudItem> items, DateTime now)
        {
            bool changed = false;
            _now = now;
            HudValue hz = frame.Get("display.hz");
            _hz = hz != null ? hz.Value : double.NaN;
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (HudItem it in items)
            {
                ids.Add(it.Id);
                long due;
                if (_due.TryGetValue(it.Id, out due) && now.Ticks < due) continue;
                HudValue v = frame.Get(it.Id);
                HudDef def = HudCatalog.Find(it.Id) ?? HudCatalog.Dynamic(it.Id, v);
                _due[it.Id] = now.Ticks + TimeSpan.TicksPerMillisecond * it.EffectiveInterval(def);
                _shown[it.Id] = v;
                _history.Push(it.Id, now, v == null ? double.NaN : v.Value);
                changed = true;
            }
            _history.Retain(ids);
            return changed;
        }

        // Мин./сред./макс. начинают копиться заново (клавиша «сбросить»). История графиков остаётся.
        public void ResetStats() { _statsFrom = DateTime.Now.Ticks; }

        public List<HudRow> Rows(IList<HudItem> items, int graphSeconds)
        {
            return Rows(items, graphSeconds, graphSeconds);
        }

        public List<HudRow> Rows(IList<HudItem> items, int graphSeconds, int statsSeconds)
        {
            List<HudRow> rows = new List<HudRow>();
            if (statsSeconds <= 0) statsSeconds = graphSeconds;
            long statsFrom = Math.Max(_statsFrom, _now.Ticks - TimeSpan.TicksPerSecond * statsSeconds);
            // Блоками даже для списков, сохранённых до группировки.
            foreach (HudItem it in HudItem.Grouped(items))
            {
                HudValue v;
                _shown.TryGetValue(it.Id, out v);
                HudRow row = HudFormat.Row(it, v);
                HudRing ring = _history.Get(it.Id);
                if (it.Stats && row.Kind != HudKind.Text && ring != null) row.Stats = Stats(ring, statsFrom, row.Kind);
                if (HudFormat.Valid(_hz) && _hz > 0)
                {
                    if (it.Id == "fps.frametime") row.Target = 1000.0 / _hz;
                    else if (it.Id == "fps") row.Target = _hz;
                }
                if (row.Graph)
                {
                    HudDef def = HudCatalog.Find(it.Id) ?? HudCatalog.Dynamic(it.Id, v);
                    int interval = it.EffectiveInterval(def);
                    row.Slots = Math.Max(2, graphSeconds * 1000 / interval);
                    // Время кадра рисуется по каждому кадру («пила»), а не точкой раз в интервал.
                    if (v != null && v.Series != null && v.Series.Length > 0)
                    {
                        row.Points = (double[])v.Series.Clone();
                        row.Slots = HudFrameMath.SeriesMax;
                    }
                    else if (ring != null)
                    {
                        int n = Math.Min(ring.Count, row.Slots);
                        row.Points = new double[n];
                        for (int i = 0; i < n; i++) row.Points[i] = ring.Value(ring.Count - n + i);
                    }
                }
                rows.Add(row);
            }
            return rows;
        }

        internal static string Stats(HudRing ring, long fromTicks, HudKind kind)
        {
            double min = double.MaxValue, max = double.MinValue, sum = 0;
            int n = 0;
            for (int i = ring.Count - 1; i >= 0; i--)
            {
                if (ring.Ticks(i) < fromTicks) break;
                double x = ring.Value(i);
                if (!HudFormat.Valid(x)) continue;
                if (x < min) min = x;
                if (x > max) max = x;
                sum += x;
                n++;
            }
            return n == 0 ? null : HudFormat.StatsText(kind, min, sum / n, max);
        }
    }

    // ------------------------------------------------------------------ //
    //  Такт 250 мс; замер — в фоне, отрисовка — в потоке интерфейса
    // ------------------------------------------------------------------ //
    internal sealed class HudController : IDisposable
    {
        private readonly Action<Action> _post;
        private HudWindow _window;
        private HudCollector _collector;
        private readonly HudBoard _board = new HudBoard();
        private System.Threading.Timer _tick;
        private CapSettings _settings = new CapSettings();
        private List<HudItem> _items = HudItem.ParseList(HudItem.Default);
        private HudStyle _style = new HudStyle();
        private bool _visible, _move;
        private int _busy;
        private string _flash;
        private DateTime _flashUntil;
        private DateTime _moveUntil;
        private HudLagRecorder _recorder;
        private readonly object _gate = new object();

        // Перетащили: окно на экране; сохраняет HudApp.
        public event Action<Rectangle> Moved;

        public HudController(Action<Action> post) { _post = post; }

        public bool Visible { get { return _visible; } }
        public bool Recording { get { lock (_gate) return _recorder != null; } }

        public void Apply(CapSettings settings)
        {
            lock (_gate)
            {
                _settings = settings ?? new CapSettings();
                _items = HudItem.ParseList(_settings.ActiveItems);
                _style = HudStyle.From(_settings);
            }
            if (_window != null) _window.SetInCaptures(_settings.HudInCaptures);
        }

        public void Show() { _visible = true; Ensure(); }

        public void Hide()
        {
            _visible = false;
            if (_move) EndMove();
            Ensure();
        }

        public void ResetStats()
        {
            lock (_gate) _board.ResetStats();
            Flash(Tr.S("Мин. / сред. / макс. сброшены", "Min / avg / max reset"));
        }

        // Короткая надпись над строками на полторы секунды («Набор 2», «сброшено»).
        public void Flash(string text) { Flash(text, 1500); }

        public void Flash(string text, int ms)
        {
            lock (_gate) { _flash = text; _flashUntil = DateTime.Now.AddMilliseconds(ms); }
        }

        public void BeginMove()
        {
            if (!_visible) Show();
            _move = true;
            _moveUntil = DateTime.Now.AddSeconds(60);
            if (_window != null) _window.SetMoveMode(true);
        }

        public void EndMove()
        {
            _move = false;
            if (_window != null) _window.SetMoveMode(false);
        }

        public bool MoveMode { get { return _move; } }

        public HudLagRecorder Recorder { get { lock (_gate) return _recorder; } }

        public void StartRecording(HudLagRecorder recorder)
        {
            lock (_gate) _recorder = recorder;
            Ensure();
        }

        public HudLagRecorder StopRecording()
        {
            HudLagRecorder r;
            lock (_gate) { r = _recorder; _recorder = null; }
            Ensure();
            return r;
        }

        // Окно и сборщик живут, пока столбик показан или идёт запись лагов (тогда видна хотя бы строка «запись»).
        private void Ensure()
        {
            bool want = _visible || Recording;
            if (want && _window == null)
            {
                _window = new HudWindow();
                _window.SetInCaptures(_settings.HudInCaptures);
                _window.Moved += OnMoved;
                _window.Show();
                if (_move) _window.SetMoveMode(true);
                lock (_gate) { if (_collector == null) _collector = new HudCollector(); }
                _tick = new System.Threading.Timer(delegate { Tick(); }, null, 0, 250);
            }
            else if (!want && _window != null)
            {
                if (_tick != null) { _tick.Dispose(); _tick = null; }
                lock (_gate) { if (_collector != null) { _collector.Dispose(); _collector = null; } }
                _window.Moved -= OnMoved;
                _window.Close();
                _window.Dispose();
                _window = null;
            }
        }

        private void OnMoved(Rectangle bounds)
        {
            EndMove();
            Action<Rectangle> h = Moved;
            if (h != null) h(bounds);
        }

        private void Tick()
        {
            // Замер дольше такта (PDH на нагруженной машине) не должен копить очередь.
            if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
            try
            {
                List<HudRow> rows;
                CapSettings s;
                HudStyle style;
                lock (_gate)
                {
                    if (_collector == null) return;
                    HudFrame frame = _collector.Tick();
                    if (_recorder != null)
                    {
                        try { _recorder.Sample(frame, _collector); }
                        catch (Exception ex) { CapLog.Report(ex); }
                    }
                    s = _settings;
                    DateTime now = DateTime.Now;
                    bool changed = _board.Advance(frame, _items, now);
                    style = _style.Clone();
                    style.MoveMode = _move;
                    if (_recorder != null)
                    {
                        style.Header = _recorder.HeaderText();
                        style.HeaderColor = unchecked((int)0xFFFF6056);
                        changed = true;
                    }
                    else if (_move)
                    {
                        style.Header = Tr.S("Перетащите мышью и отпустите", "Drag with the mouse and release");
                        style.HeaderColor = unchecked((int)0xFF5AB4FF);
                        changed = true;
                    }
                    else if (_flash != null)
                    {
                        if (now < _flashUntil) { style.Header = _flash; changed = true; }
                        else { _flash = null; changed = true; }
                    }
                    if (!changed) return;
                    rows = _visible ? _board.Rows(_items, s.HudGraphSeconds, s.HudStatsSeconds) : new List<HudRow>();
                }
                if (_move && DateTime.Now > _moveUntil) _post(EndMove);
                _post(delegate
                {
                    if (_window == null || _window.IsDisposed) return;
                    _window.Render(rows, s, style);
                });
            }
            catch (Exception ex) { CapLog.Report(ex); }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        public void Dispose()
        {
            _visible = false;
            lock (_gate) _recorder = null;
            Ensure();
        }
    }
}
