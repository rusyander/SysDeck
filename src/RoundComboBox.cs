// SysDeck — выпадающий список в цветах темы со сглаженными углами.
// Штатный ComboBox даже в FlatStyle.Flat рисует рамку и кнопку со стрелкой системными
// цветами: в тёмной теме — серый прямоугольник с белой кнопкой. Здесь после штатной
// отрисовки (WM_PAINT) поверх кладётся своя: углы — цветом родителя, скруглённая рамка
// цветом Border, кнопка со стрелкой в цветах темы. У DropDownList перерисовывается и
// текст; у редактируемого DropDown поле ввода — отдельное окно и остаётся как есть.
// Раскрытый список рисуется построчно (OwnerDrawFixed): фон Surface, выделенная строка —
// цветом Highlight, как строки списков приложения. В тёмной палитре окну назначается тема
// DarkMode_CFD (тёмные рамка и полоса прокрутки списка). Строки списка выше поля выбора
// (CB_SETITEMHEIGHT для индекса 0), чтобы пункты не слипались. Отступы текста, высота
// строк и стрелка умножаются на DpiScale — коэффициент DPI, который выставляет тема.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SysDeck
{
    public class RoundComboBox : ComboBox
    {
        public int Radius = 8;
        public Color Border = Color.Empty;        // Empty — штатная отрисовка (тема ещё не применена)
        public Color FocusBorder = Color.Empty;   // рамка при фокусе (акцент)
        public Color Surface = Color.Empty, TextColor = Color.Empty, Arrow = Color.Empty;
        public Color Highlight = Color.Empty, HighlightText = Color.Empty; // выделенная строка раскрытого списка
        public bool DarkList;                     // тёмная тема окна раскрытого списка
        public float DpiScale = 1f;                  // коэффициент DPI (1.0 при 96 DPI): отступы, строки, стрелка
        private const int WM_PAINT = 0x000F;
        private const int CB_SETITEMHEIGHT = 0x0153;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public RoundComboBox()
        {
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            ItemHeight = FieldHeight();
        }

        // Высота поля выбора: окно контрола = FontHeight + 7 (PreferredHeight), поле — на рамку меньше.
        private int FieldHeight() { return FontHeight + 4; }
        // Высота строки раскрытого списка — просторнее поля.
        private int RowHeight() { return FontHeight + S(10); }
        // Пиксели макета (96 DPI) → пиксели экрана.
        private int S(int v) { return (int)Math.Round(v * DpiScale); }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ItemHeight = FieldHeight();
            ApplyRowHeight();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyRowHeight();
            ApplyListTheme();
        }

        // WinForms при каждом ItemHeight шлёт CB_SETITEMHEIGHT и для строк, и для поля —
        // после него строкам возвращается своя, большая высота.
        public void ApplyRowHeight()
        {
            if (!IsHandleCreated) return;
            SendMessage(Handle, CB_SETITEMHEIGHT, IntPtr.Zero, (IntPtr)RowHeight());
        }

        // Тема окна списка: в тёмной палитре DarkMode_CFD (тёмные рамка и полоса прокрутки), иначе штатная.
        public void ApplyListTheme()
        {
            if (!IsHandleCreated) return;
            try { Native.SetWindowTheme(Handle, DarkList ? "DarkMode_CFD" : null, null); } catch { }
        }

        // Строка списка (и поле выбора — его затем целиком перерисует PaintFrame).
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            base.OnDrawItem(e);
            Graphics g = e.Graphics;
            bool edit = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool sel = !edit && (e.State & DrawItemState.Selected) != 0;
            Color surf = Surface.IsEmpty ? BackColor : Surface;
            Color bg = sel ? (Highlight.IsEmpty ? SystemColors.Highlight : Highlight) : surf;
            Color fg = !Enabled ? SystemColors.GrayText
                     : sel ? (HighlightText.IsEmpty ? SystemColors.HighlightText : HighlightText)
                     : (TextColor.IsEmpty ? ForeColor : TextColor);
            using (SolidBrush b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
            if (e.Index >= 0 && e.Index < Items.Count)
            {
                string text = GetItemText(Items[e.Index]);
                int pad = edit ? S(4) : S(7);
                Rectangle tr = new Rectangle(e.Bounds.X + pad, e.Bounds.Y, Math.Max(1, e.Bounds.Width - pad - S(3)), e.Bounds.Height);
                TextRenderer.DrawText(g, text ?? "", Font, tr, fg,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && IsHandleCreated && !Border.IsEmpty) PaintFrame();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e) { base.OnDropDownClosed(e); Invalidate(); }

        // Цвет, которым закрашиваются срезанные углы: первый непрозрачный фон вверх по дереву.
        private Color ParentBg()
        {
            for (Control p = Parent; p != null; p = p.Parent)
                if (p.BackColor.A == 255) return p.BackColor;
            return SystemColors.Control;
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
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

        private void PaintFrame()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(Handle))
                {
                    Rectangle rc = ClientRectangle;
                    if (rc.Width < 8 || rc.Height < 8) return;
                    Color surf = Surface.IsEmpty ? BackColor : Surface;
                    int bw = SystemInformation.VerticalScrollBarWidth;          // ширина кнопки со стрелкой
                    Rectangle r = rc; r.Width -= 1; r.Height -= 1;
                    int rad = Math.Max(1, Math.Min(Radius, Math.Min(r.Width, r.Height) / 2));
                    using (GraphicsPath gp = Rounded(r, rad))
                    {
                        if (DropDownStyle == ComboBoxStyle.DropDownList)
                        {
                            // весь контрол наш: фон родителя, скруглённая подложка, текст
                            using (SolidBrush bg = new SolidBrush(ParentBg())) g.FillRectangle(bg, rc);
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            using (SolidBrush sb = new SolidBrush(surf)) g.FillPath(sb, gp);
                            g.SmoothingMode = SmoothingMode.Default;
                            string text = SelectedItem == null ? Text : GetItemText(SelectedItem);
                            Rectangle tr = new Rectangle(rc.X + S(7), rc.Y, Math.Max(1, rc.Width - bw - S(9)), rc.Height);
                            Color fore = !Enabled ? SystemColors.GrayText : (TextColor.IsEmpty ? ForeColor : TextColor);
                            TextRenderer.DrawText(g, text ?? "", Font, tr, fore,
                                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                        }
                        else
                        {
                            // редактируемый: поле ввода — дочернее окно, перекрашиваем углы и кнопку
                            using (Region outside = new Region(rc))
                            {
                                outside.Exclude(gp);
                                using (SolidBrush bg = new SolidBrush(ParentBg())) g.FillRegion(bg, outside);
                            }
                            g.SetClip(gp);
                            using (SolidBrush sb = new SolidBrush(surf))
                                g.FillRectangle(sb, new Rectangle(rc.Right - bw - 2, rc.Y, bw + 2, rc.Height));
                            g.ResetClip();
                        }

                        // стрелка-шеврон по центру кнопки
                        int cx = rc.Right - bw / 2 - 1, cy = rc.Y + rc.Height / 2;
                        Color ac = !Enabled ? SystemColors.GrayText : (Arrow.IsEmpty ? ForeColor : Arrow);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        float ax = 4f * DpiScale, ay = 2f * DpiScale;
                        using (Pen ap = new Pen(ac, 1.6f * DpiScale))
                        {
                            ap.StartCap = LineCap.Round; ap.EndCap = LineCap.Round; ap.LineJoin = LineJoin.Round;
                            g.DrawLines(ap, new PointF[] { new PointF(cx - ax, cy - ay), new PointF(cx, cy + ay), new PointF(cx + ax, cy - ay) });
                        }
                        Color bc = Focused && !FocusBorder.IsEmpty ? FocusBorder : Border;
                        using (Pen p = new Pen(bc)) g.DrawPath(p, gp);
                        g.SmoothingMode = SmoothingMode.Default;
                    }
                }
            }
            catch { }
        }
    }
}
