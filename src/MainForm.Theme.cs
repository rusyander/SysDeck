// SysDeck — иконки, owner-draw таблиц, применение темы
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    public partial class MainForm
    {
        private void BuildIcons()
        {
            _iconIdle = MakeIcon(Color.FromArgb(58, 166, 85));    // зелёная — чисто
            _iconActive = MakeIcon(Color.FromArgb(224, 150, 40)); // оранжевая — есть кандидаты
            _iconWindow = MakeIcon(Color.FromArgb(45, 120, 224));  // синяя — иконка окна/панели задач
        }

        // Многоразмерная иконка из файла (крипче в трее/на панели задач); фолбэк — рисованная.
        private Icon LoadAppIcon()
        {
            try
            {
                string path = Path.Combine(Application.StartupPath, "icon.ico");
                if (File.Exists(path)) return new Icon(path);
            }
            catch { }
            return _iconWindow;
        }

        // Смесь двух цветов: t = 0 — a, t = 1 — b (неактивные состояния кнопок).
        private static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Чёткая иконка с настоящим альфа-каналом: собираем многоразмерный .ico
        // из PNG (16..64px). GetHicon НЕ используем — он теряет прозрачность и даёт
        // чёрный ореол/невидимость в трее.
        private Icon MakeIcon(Color c)
        {
            int[] sizes = { 16, 20, 24, 32, 48, 64 };
            Bitmap[] bmps = new Bitmap[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) bmps[i] = DrawIconBitmap(sizes[i], c);
            Icon ico = IconFromBitmaps(bmps);
            foreach (Bitmap b in bmps) b.Dispose();
            return ico;
        }

        private Bitmap DrawIconBitmap(int S, Color c)
        {
            Bitmap bmp = new Bitmap(S, S);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                int m = Math.Max(1, (int)Math.Round(S * 0.07));
                int rad = Math.Max(2, (int)Math.Round(S * 0.24));
                Rectangle rect = new Rectangle(m, m, S - 2 * m, S - 2 * m);
                using (GraphicsPath gp = RoundedRect(rect, rad))
                using (LinearGradientBrush br = new LinearGradientBrush(
                    rect, ControlPaint.Light(c, 0.28f), ControlPaint.Dark(c, 0.10f), 90f))
                    g.FillPath(br, gp);
                using (Pen p = new Pen(Color.White, Math.Max(1.4f, S * 0.11f)))
                {
                    p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
                    g.DrawLines(p, new PointF[] {
                        new PointF(S * 0.30f, S * 0.53f),
                        new PointF(S * 0.44f, S * 0.68f),
                        new PointF(S * 0.72f, S * 0.33f) });
                }
            }
            return bmp;
        }

        // Сборка .ico из набора PNG (сохраняет альфу) и создание Icon из потока.
        private static Icon IconFromBitmaps(Bitmap[] sizes)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                BinaryWriter bw = new BinaryWriter(ms);
                bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)sizes.Length);
                byte[][] pngs = new byte[sizes.Length][];
                for (int i = 0; i < sizes.Length; i++)
                {
                    using (MemoryStream s = new MemoryStream())
                    {
                        sizes[i].Save(s, ImageFormat.Png);
                        pngs[i] = s.ToArray();
                    }
                }
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    int S = sizes[i].Width;
                    bw.Write((byte)(S >= 256 ? 0 : S));
                    bw.Write((byte)(S >= 256 ? 0 : S));
                    bw.Write((byte)0); bw.Write((byte)0);
                    bw.Write((ushort)1); bw.Write((ushort)32);
                    bw.Write((uint)pngs[i].Length);
                    bw.Write((uint)offset);
                    offset += pngs[i].Length;
                }
                for (int i = 0; i < sizes.Length; i++) bw.Write(pngs[i]);
                bw.Flush();
                ms.Position = 0;
                return new Icon(ms);
            }
        }

        // ---------- Красивая отрисовка таблиц (owner-draw под тему) ----------
        // Метка строки БЕЗ галочки в списке с CheckBoxes: заголовок группы дубликатов,
        // информационная строка во вкладке браузеров. Рисуется без квадратика и не
        // отмечается ни кликом, ни «Все».
        internal static readonly object NoCheckTag = new object();

        // Дерево: текст узла рисуем сами, чтобы выделение было в цветах темы, а не
        // системным белым прямоугольником в тёмной теме.
        private void SetupOwnerDraw(TreeView tv)
        {
            tv.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tv.DrawNode += delegate(object s, DrawTreeNodeEventArgs e)
            {
                if (e.Node == null || _brSel == null) return;
                bool sel = (e.State & TreeNodeStates.Selected) != 0;
                Rectangle r = e.Bounds;
                if (r.Width <= 0 || r.Height <= 0) return;
                e.Graphics.FillRectangle(sel ? _brSel : _brSurface, r);
                TextRenderer.DrawText(e.Graphics, e.Node.Text, tv.Font, new Rectangle(r.X + 2, r.Y, r.Width - 2, r.Height), _theme.Text,
                                      TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            };
        }

        private void SetupOwnerDraw(ListView lv)
        {
            lv.OwnerDraw = true;
            lv.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                try
                {
                    if (e.Index >= 0 && e.Index < lv.Items.Count && lv.Items[e.Index] != null
                        && ReferenceEquals(lv.Items[e.Index].Tag, NoCheckTag)) e.NewValue = CheckState.Unchecked;
                }
                catch { }
            };
            lv.GridLines = false;
            lv.ShowItemToolTips = true; // полный текст обрезанных ячеек по наведению
            lv.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lv.DrawColumnHeader += Lv_DrawColumnHeader;
            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = false; };
            lv.DrawSubItem += Lv_DrawSubItem;
            // последняя колонка занимает всю оставшуюся ширину — без белой "добивки" заголовка
            lv.Resize += delegate
            {
                if (_inAutoFill) return;
                AutoFillLastColumn(lv);
                ScheduleAutoFillSettle(lv);   // отложенный проход с force — см. Layout.cs
            };
            // ширины колонок заданы под 96 DPI; сама форма их не масштабирует
            if (_dpiScale != 1f) foreach (ColumnHeader ch in lv.Columns) ch.Width = Px(ch.Width);
        }

        private bool _inAutoFill, _autoFillPending;
        private readonly List<ListView> _autoFillQueue = new List<ListView>();
        // Список -> колонка, которая забирает остаток ширины. По умолчанию последняя;
        // задаётся явно, если последняя колонка узкая по смыслу (см. _lvBrowser: её
        // «Ссылка» иначе схлопывается до нечитаемых ~45 px и «нет домена» не влезает).
        private readonly Dictionary<ListView, int> _flexColumn = new Dictionary<ListView, int>();
        // колонка с путём: многоточие в середине, чтобы конец пути (имя папки) оставался виден
        private readonly Dictionary<ListView, int> _pathColumns = new Dictionary<ListView, int>();

        private int FlexColumn(ListView lv)
        {
            int idx;
            if (_flexColumn.TryGetValue(lv, out idx) && idx >= 0 && idx < lv.Columns.Count) return idx;
            return lv.Columns.Count - 1;
        }

        private void AutoFillLastColumn(ListView lv) { AutoFillLastColumn(lv, false); }

        // force = «дёрнуть ширину дважды». Common controls не сбрасывают уже
        // выставленный WS_HSCROLL сами: замерено — при colSum=995 и client=997 полоса
        // продолжала висеть, и исчезала только после сообщения о смене ширины колонки.
        // В обработчике Resize это не нужно (там ширина и так меняется) и вызвало бы
        // лишнюю перерисовку на каждый пиксель растягивания окна.
        private void AutoFillLastColumn(ListView lv, bool force)
        {
            if (lv == null || lv.Columns.Count == 0) return;
            // Width = -2 сам вызывает Resize; без флага обработчик Resize уходит в
            // рекурсивный шторм пересчётов ширины при каждом растягивании окна.
            if (_inAutoFill) return;
            _inAutoFill = true;
            try
            {
                // Ширину считаем сами, а не через -2 (LVSCW_AUTOSIZE_USEHEADER): нативный
                // расчёт стабильно перебирает на 2 px, и из-за этого во ВСЕХ списках висел
                // бесполезный горизонтальный скроллбар (замерено: colSum=999 при client=997).
                // ClientSize.Width уже не включает вертикальный скроллбар, поэтому остаток
                // получается точным.
                int flex = FlexColumn(lv);
                int used = 0;
                for (int i = 0; i < lv.Columns.Count; i++) if (i != flex) used += lv.Columns[i].Width;
                // 2 px запаса: при сумме РОВНО в ширину клиента control не сбрасывает
                // уже выставленный WS_HSCROLL, и полоса остаётся висеть впустую.
                int rest = lv.ClientSize.Width - used - 2;
                if (rest >= 60)
                {
                    if (force && lv.Columns[flex].Width == rest) lv.Columns[flex].Width = rest - 1;
                    lv.Columns[flex].Width = rest;
                }
                else if (flex == lv.Columns.Count - 1) lv.Columns[flex].Width = -2;  // не влезает — пусть решает система
                else lv.Columns[flex].Width = 60;    // окно уже некуда сужать: полоса прокрутки честнее обрезанного текста
            }
            catch { }
            finally { _inAutoFill = false; }
        }

        // Второй проход ПОСЛЕ того, как список разложится: на момент AddRange
        // вертикального скроллбара ещё нет, ClientSize шире на его толщину, и
        // посчитанная там последняя колонка оказывается на ~17 px слишком широкой —
        // отсюда и лишний горизонтальный скроллбар.
        // Планировать этот проход ИЗ САМОГО AutoFillLastColumn нельзя: каждый вызов
        // ставил бы следующий, и UI-поток забивался бы сообщениями насмерть
        // (проверено — приложение переставало отвечать).
        // Вызывается только из UI-потока (обработчики идут через UiPost), поэтому
        // очередь без блокировок.
        private void AutoFillLastColumnDeferred(ListView lv)
        {
            if (lv == null) return;
            AutoFillLastColumn(lv);
            if (!IsHandleCreated) return;
            if (!_autoFillQueue.Contains(lv)) _autoFillQueue.Add(lv);
            if (_autoFillPending) return;
            _autoFillPending = true;
            BeginInvoke(new MethodInvoker(delegate
            {
                _autoFillPending = false;
                ListView[] pend = _autoFillQueue.ToArray();
                _autoFillQueue.Clear();
                foreach (ListView t in pend)
                    if (t != null && !t.IsDisposed) AutoFillLastColumn(t, true);
            }));
        }

        // Кисти/перья/шрифты для owner-draw живут в полях, а не создаются на каждую
        // ячейку: при 300 строках × 11 колонок это было ~6600 GDI-объектов на одну
        // перерисовку списка, а new Font в отрисовке заголовка — самый дорогой из них.
        private SolidBrush _brHeader, _brSurface, _brSel, _brAccent;
        private Pen _penBorder, _penAccent, _penCheck, _penSurfaceEdge;
        private Font _fontHeader;
        private readonly Dictionary<int, SolidBrush> _rowBrushes = new Dictionary<int, SolidBrush>();

        private void DisposeThemeGdi()
        {
            if (_brHeader != null) { _brHeader.Dispose(); _brHeader = null; }
            if (_brSurface != null) { _brSurface.Dispose(); _brSurface = null; }
            if (_brSel != null) { _brSel.Dispose(); _brSel = null; }
            if (_brAccent != null) { _brAccent.Dispose(); _brAccent = null; }
            if (_penBorder != null) { _penBorder.Dispose(); _penBorder = null; }
            if (_penAccent != null) { _penAccent.Dispose(); _penAccent = null; }
            if (_penCheck != null) { _penCheck.Dispose(); _penCheck = null; }
            if (_penSurfaceEdge != null) { _penSurfaceEdge.Dispose(); _penSurfaceEdge = null; }
            if (_fontHeader != null) { _fontHeader.Dispose(); _fontHeader = null; }
            foreach (SolidBrush b in _rowBrushes.Values) b.Dispose();
            _rowBrushes.Clear();
        }

        private void BuildThemeGdi()
        {
            DisposeThemeGdi();
            _brHeader = new SolidBrush(_theme.Header);
            _brSurface = new SolidBrush(_theme.Surface);
            _brSel = new SolidBrush(_theme.Dark ? ControlPaint.Light(_theme.Accent, 0.15f)
                                                : ControlPaint.Light(_theme.Accent, 0.72f));
            _brAccent = new SolidBrush(_theme.Accent);
            _penBorder = new Pen(_theme.Border);
            _penAccent = new Pen(_theme.Accent, 1.6f);
            _penSurfaceEdge = new Pen(_theme.Border, 1.6f);
            _penCheck = new Pen(_theme.AccentText, 2.2f);
            _penCheck.StartCap = LineCap.Round; _penCheck.EndCap = LineCap.Round;
            _penCheck.LineJoin = LineJoin.Round;
            _fontHeader = new Font(Font, FontStyle.Bold);
        }

        private SolidBrush RowBrush(Color c)
        {
            int key = c.ToArgb();
            SolidBrush b;
            if (_rowBrushes.TryGetValue(key, out b)) return b;
            b = new SolidBrush(c);
            _rowBrushes[key] = b;
            return b;
        }

        private void Lv_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            if (_brHeader == null) BuildThemeGdi();
            e.Graphics.FillRectangle(_brHeader, e.Bounds);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Right - 1, e.Bounds.Top + 4, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            Rectangle tr = new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, _fontHeader, tr, _theme.Subtle,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void Lv_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (_brSurface == null) BuildThemeGdi();
            ListView lv = (ListView)sender;

            bool noCheck = ReferenceEquals(e.Item.Tag, NoCheckTag);
            Color bg = e.Item.BackColor;
            if (bg.IsEmpty || bg.A == 0) bg = noCheck ? _theme.Header : _theme.Surface;
            e.Graphics.FillRectangle(e.Item.Selected ? _brSel : RowBrush(bg), e.Bounds);
            e.Graphics.DrawLine(_penBorder, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            int textX = e.Bounds.Left + 8;
            if (e.ColumnIndex == 0 && lv.CheckBoxes && !noCheck)
            {
                int box = Px(17);
                int bx = e.Bounds.Left + Px(6);
                int by = e.Bounds.Top + (e.Bounds.Height - box) / 2;
                DrawCheck(e.Graphics, new Rectangle(bx, by, box, box), e.Item.Checked);
                textX = bx + box + 8;
            }

            Color fg = e.Item.ForeColor;
            if (fg.IsEmpty || fg.A == 0) fg = noCheck ? _theme.Subtle : _theme.Text;
            Rectangle rt = new Rectangle(textX, e.Bounds.Top, e.Bounds.Right - textX - 6, e.Bounds.Height);
            int pathCol;
            TextFormatFlags ell = _pathColumns.TryGetValue(lv, out pathCol) && pathCol == e.ColumnIndex
                ? TextFormatFlags.PathEllipsis : TextFormatFlags.EndEllipsis;
            TextRenderer.DrawText(e.Graphics, e.SubItem != null ? e.SubItem.Text : "", lv.Font, rt, fg,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | ell);
        }

        private void DrawCheck(Graphics g, Rectangle r, bool check)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = RoundedRect(r, 4))
            {
                g.FillPath(check ? _brAccent : _brSurface, gp);
                g.DrawPath(check ? _penAccent : _penSurfaceEdge, gp);
            }
            if (check)
                g.DrawLines(_penCheck, new Point[] {
                    new Point(r.Left + Px(4), r.Top + Px(9)),
                    new Point(r.Left + Px(7), r.Top + Px(12)),
                    new Point(r.Left + Px(13), r.Top + Px(5)) });
            g.SmoothingMode = SmoothingMode.Default;
        }

        private readonly HashSet<Control> _rounded = new HashSet<Control>();

        private void RoundControl(Control c, int radius)
        {
            try
            {
                if (c.Width <= 2 || c.Height <= 2) return;
                using (GraphicsPath gp = RoundedRect(new Rectangle(0, 0, c.Width, c.Height), radius))
                    c.Region = new Region(gp);
                // Region задаётся под текущий размер: кнопка, которую потом растянули
                // (вкладки навигации при ресайзе окна), обрезалась по старой границе.
                if (_rounded.Add(c)) c.Resize += delegate { RoundControl(c, radius); };
            }
            catch { }
        }

        // ---------- Тема ----------
        private void ApplyThemeAll()
        {
            BuildThemeGdi();
            BackColor = _theme.Bg;
            ForeColor = _theme.Text;
            ApplyThemeTo(this);
            RecolorRows(this);
            Control nav = null;
            foreach (Control c in Controls) if (c.Name == "nav") { nav = c; break; }
            if (nav != null) nav.BackColor = _theme.Bg;
            UpdateNav();
            ApplyHomeTheme();
            ApplyTitleBar();
            // правая панель «Windows: лишнее» красит заголовки цветом темы посимвольно — перерисовать
            if (_tvDebloat != null) ShowDebloatNode(_tvDebloat.SelectedNode);
            Invalidate();
        }

        private void ApplyTitleBar() { ApplyTitleBar(this); }

        // Строки списков красятся цветами палитры в момент заполнения (Surface, CandidateBg, WhiteBg,
        // Text, Subtle). При смене темы без перезаполнения — предпросмотр в настройках, переключение
        // Windows при теме «как в системе» — список становился тёмным, а его строки оставались
        // светлыми. Перекрашиваем строки всех списков: цвет прежней палитры → тот же цвет новой.
        private Theme _themeApplied;

        private void RecolorRows(Control root)
        {
            Theme from = _themeApplied;
            _themeApplied = _theme;
            if (from == null || from == _theme) return;
            RecolorRows(root, from, _theme);
        }

        private static void RecolorRows(Control root, Theme from, Theme to)
        {
            foreach (Control c in root.Controls)
            {
                ListView lv = c as ListView;
                if (lv != null && lv.Items.Count > 0)
                {
                    lv.BeginUpdate();
                    try
                    {
                        foreach (ListViewItem it in lv.Items)
                        {
                            Color m = MapColor(it.ForeColor, from, to); if (m != it.ForeColor) it.ForeColor = m;
                            m = MapColor(it.BackColor, from, to); if (m != it.BackColor) it.BackColor = m;
                            if (!it.UseItemStyleForSubItems)
                                for (int i = 1; i < it.SubItems.Count; i++)
                                {
                                    ListViewItem.ListViewSubItem si = it.SubItems[i];
                                    m = MapColor(si.ForeColor, from, to); if (m != si.ForeColor) si.ForeColor = m;
                                    m = MapColor(si.BackColor, from, to); if (m != si.BackColor) si.BackColor = m;
                                }
                        }
                    }
                    finally { lv.EndUpdate(); }
                }
                if (c.Controls.Count > 0) RecolorRows(c, from, to);
            }
        }

        private static Color MapColor(Color x, Theme from, Theme to)
        {
            if (x.IsEmpty) return x;
            int a = x.ToArgb();
            if (a == from.Surface.ToArgb()) return to.Surface;
            if (a == from.CandidateBg.ToArgb()) return to.CandidateBg;
            if (a == from.WhiteBg.ToArgb()) return to.WhiteBg;
            if (a == from.Bg.ToArgb()) return to.Bg;
            if (a == from.Header.ToArgb()) return to.Header;
            if (a == from.Text.ToArgb()) return to.Text;
            if (a == from.Subtle.ToArgb()) return to.Subtle;
            if (a == from.Accent.ToArgb()) return to.Accent;
            if (a == from.AccentText.ToArgb()) return to.AccentText;
            if (a == from.Border.ToArgb()) return to.Border;
            return x;
        }

        // Тёмный заголовок окна (DWM) — для главного окна и для дочерних диалогов: без этого в тёмной
        // теме у диалога «Состав…»/«Куда перенести» оставалась белая полоса заголовка.
        private void ApplyTitleBar(Form f)
        {
            if (f == null || !f.IsHandleCreated) return;
            try
            {
                int on = _theme.Dark ? 1 : 0;
                if (Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, 4) != 0)
                    Native.DwmSetWindowAttribute(f.Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref on, 4);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        // Тема «как в системе» следует за переключением Windows без перезапуска:
        // при смене светлой/тёмной приходит WM_SETTINGCHANGE с lParam "ImmersiveColorSet".
        private const int WM_SETTINGCHANGE = 0x001A;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != WM_SETTINGCHANGE || _engine == null || _theme == null) return;
            if (_engine.Config.Theme != "system") return;
            string what = null;
            try { if (m.LParam != IntPtr.Zero) what = Marshal.PtrToStringUni(m.LParam); } catch { }
            if (what != "ImmersiveColorSet") return;
            if (Theme.SystemIsLight() != _theme.Dark) return;   // уже совпадает
            _theme = Theme.Resolve("system");
            ApplyThemeAll();
        }

        // Полосы прокрутки — не клиентская область, их рисует система по теме ОКНА: без
        // DarkMode_Explorer в тёмной палитре они оставались светлыми (списки, вывод Docker,
        // прокручиваемая страница настроек). Пересоздание дескриптора сбрасывает тему —
        // поэтому подписка на HandleCreated, один раз на контрол.
        private readonly HashSet<Control> _scrollThemed = new HashSet<Control>();

        private void DarkScrollbars(Control c)
        {
            try { Native.SetWindowTheme(c.Handle, _theme.Dark ? "DarkMode_Explorer" : null, null); } catch { }
            if (_scrollThemed.Add(c)) c.HandleCreated += delegate { DarkScrollbars(c); };
        }

        private void ApplyThemeTo(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is RoundButton)
                {
                    // Все кнопки страниц и диалогов: сглаженный радиус, цвета состояний
                    // от темы. Акцентная — без рамки, обычная — с мягкой рамкой Border.
                    RoundButton b = (RoundButton)c;
                    bool primary = (b.Tag as string) == "primary";
                    b.Radius = Px(8);
                    if (primary)
                    {
                        b.BackColor = _theme.Accent;
                        b.ForeColor = _theme.AccentText;
                        b.Fill = _theme.Accent;
                        b.FillHover = ControlPaint.Light(_theme.Accent, 0.1f);
                        b.FillPressed = ControlPaint.Dark(_theme.Accent, 0.08f);
                        b.FillDisabled = Mix(_theme.Accent, _theme.Bg, 0.55f);
                        b.Border = Color.Empty;
                        b.TextColor = _theme.AccentText;
                        b.TextDisabled = Mix(_theme.AccentText, _theme.Bg, 0.35f);
                    }
                    else
                    {
                        b.BackColor = _theme.Surface;
                        b.ForeColor = _theme.Text;
                        b.Fill = _theme.Surface;
                        b.FillHover = _theme.Dark
                            ? ControlPaint.Light(_theme.Surface, 0.15f)
                            : ControlPaint.Dark(_theme.Surface, 0.03f);
                        b.FillPressed = _theme.Dark
                            ? ControlPaint.Light(_theme.Surface, 0.25f)
                            : ControlPaint.Dark(_theme.Surface, 0.07f);
                        b.FillDisabled = _theme.Surface;
                        b.Border = _theme.Border;
                        b.TextColor = _theme.Text;
                        b.TextDisabled = _theme.Subtle;
                    }
                    b.Invalidate();
                }
                else if (c is Button)
                {
                    // Вкладки навигации: плоские, без рамки; цвета им задаёт UpdateNav.
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderSize = 0;
                    b.UseVisualStyleBackColor = false;
                    b.BackColor = _theme.Bg;
                    b.ForeColor = _theme.Text;
                    RoundControl(b, Px(8));
                }
                else if (c is RichTextBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((RichTextBox)c).BorderStyle = BorderStyle.None;
                    BoxUnlessWrapped(c);
                    DarkScrollbars(c);
                }
                else if (c is TextBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((TextBox)c).BorderStyle = BorderStyle.None;
                    BoxUnlessWrapped(c);
                    if (((TextBox)c).Multiline) DarkScrollbars(c);
                }
                else if (c is NumericUpDown)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((NumericUpDown)c).BorderStyle = BorderStyle.None;
                    ThemeSpinButtons((NumericUpDown)c);
                    Boxed(c);
                }
                else if (c is ComboBox)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                    RoundComboBox rc = c as RoundComboBox;
                    if (rc != null)
                    {
                        // рамка, кнопка и стрелка в цветах темы поверх штатной отрисовки
                        rc.Radius = Px(8);
                        rc.DpiScale = _dpiScale;
                        rc.Border = _theme.Border;
                        rc.FocusBorder = _theme.Accent;
                        rc.Surface = _theme.Surface;
                        rc.TextColor = _theme.Text;
                        rc.Arrow = _theme.Subtle;
                        // строки раскрытого списка — как выделение строк в списках приложения
                        rc.Highlight = _theme.Dark ? ControlPaint.Light(_theme.Accent, 0.15f)
                                                   : ControlPaint.Light(_theme.Accent, 0.72f);
                        rc.HighlightText = _theme.Text;
                        rc.DarkList = _theme.Dark;
                        rc.ApplyListTheme();
                        rc.ApplyRowHeight();
                        rc.Invalidate();
                    }
                }
                else if (c is ListView)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((ListView)c).BorderStyle = BorderStyle.None;
                    Boxed(c);
                    DarkScrollbars(c);
                }
                else if (c is TreeView)
                {
                    c.BackColor = _theme.Surface;
                    c.ForeColor = _theme.Text;
                    ((TreeView)c).BorderStyle = BorderStyle.None;
                    Boxed(c);
                    ((TreeView)c).LineColor = _theme.Border;
                    // тема Проводника в обеих палитрах: одинаковые стрелки-шевроны и тёмные полосы прокрутки
                    try { Native.SetWindowTheme(c.Handle, _theme.Dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
                }
                else if (c is SplitContainer)
                {
                    c.BackColor = _theme.Bg;
                    c.ForeColor = _theme.Text;
                }
                else if (c is Label)
                {
                    c.BackColor = Color.Transparent;
                    if (c.Name == "section") c.ForeColor = _theme.Accent;
                    else if (c.Name == "warn" || c.Name == "muted") c.ForeColor = _theme.Subtle;
                    else c.ForeColor = _theme.Text;
                }
                else if (c is CheckBox || c is RadioButton)
                {
                    c.BackColor = Color.Transparent;
                    c.ForeColor = _theme.Text;
                }
                else if (c is TabControl)
                {
                    c.BackColor = _theme.Bg;
                    c.ForeColor = _theme.Text;
                }
                else if (c is TabPage || c is Panel || c is FlowLayoutPanel)
                {
                    bool box = c.Name == "box";     // обёртка поля ввода: фон поля и рамка
                    c.BackColor = box ? _theme.Surface : _theme.Bg;
                    c.ForeColor = _theme.Text;
                    if (box) Boxed(c);
                    if (c is ScrollableControl && ((ScrollableControl)c).AutoScroll) DarkScrollbars(c);
                }
                else if (c.BackColor.ToArgb() == SystemColors.Control.ToArgb() || c.BackColor.ToArgb() == SystemColors.Window.ToArgb()
                         || c.BackColor.ToArgb() == Color.White.ToArgb())
                {
                    // контрол не из списка выше с системным фоном: хотя бы фон и текст — по теме,
                    // чтобы новый тип контрола не всплыл светлым пятном в тёмной палитре
                    c.BackColor = _theme.Bg;
                    c.ForeColor = _theme.Text;
                }
                if (c.Controls.Count > 0) ApplyThemeTo(c);
            }
        }

        // Кнопки «вверх/вниз» у NumericUpDown — отдельный внутренний контрол, который
        // рисует себя системной темой и на BackColor не реагирует: в тёмной теме к
        // каждому полю приклеен белый квадрат. Дорисовываем поверх штатной отрисовки —
        // UpDownButtons поднимает событие Paint в конце своего OnPaint.
        private void ThemeSpinButtons(NumericUpDown n)
        {
            foreach (Control ch in n.Controls)
            {
                if (ch is TextBox) continue;            // это поле ввода, оно уже покрашено
                if ((ch.Tag as string) != "spin")
                {
                    ch.Tag = "spin";
                    ch.Paint += SpinButtonsPaint;
                }
                ch.Invalidate();
            }
        }

        private void SpinButtonsPaint(object sender, PaintEventArgs e)
        {
            if (!_theme.Dark) return;                   // в светлой теме системный вид уместен
            Control b = (Control)sender;
            Graphics g = e.Graphics;
            int w = b.ClientSize.Width, h = b.ClientSize.Height;
            using (SolidBrush bg = new SolidBrush(_theme.Surface))
                g.FillRectangle(bg, 0, 0, w, h);
            using (Pen pen = new Pen(_theme.Border))
            {
                g.DrawRectangle(pen, 0, 0, w - 1, h - 1);
                g.DrawLine(pen, 1, h / 2, w - 2, h / 2);
            }
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (SolidBrush ink = new SolidBrush(_theme.Text))
            {
                int cx = w / 2, uy = h / 4, dy = h - h / 4;
                g.FillPolygon(ink, new Point[] {
                    new Point(cx - 3, uy + 2), new Point(cx + 3, uy + 2), new Point(cx, uy - 2) });
                g.FillPolygon(ink, new Point[] {
                    new Point(cx - 3, dy - 2), new Point(cx + 3, dy - 2), new Point(cx, dy + 2) });
            }
        }
    }
}
