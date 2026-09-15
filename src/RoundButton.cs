// SysDeck — кнопка со сглаженными скруглёнными углами.
// Штатная Button скруглялась через Region: углы выходили ступенчатыми, а плоская рамка
// обрывалась на срезах («съеденный» радиус). Эта кнопка рисует себя сама: углы — цветом
// родителя, тело и рамка — со сглаживанием, Region не используется. Цвета состояний
// выставляет тема (ApplyThemeTo), по умолчанию берутся BackColor/ForeColor.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SysDeck
{
    public class RoundButton : Button
    {
        public int Radius = 8;
        public Color Fill = Color.Empty, FillHover = Color.Empty, FillPressed = Color.Empty, FillDisabled = Color.Empty;
        public Color Border = Color.Empty;           // Empty — без рамки (акцентная кнопка)
        public Color TextColor = Color.Empty, TextDisabled = Color.Empty;
        private bool _hover, _down;

        public RoundButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            UseVisualStyleBackColor = false;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
        // Нажатие с клавиатуры (пробел) выглядит как нажатие мышью.
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space && !_down) { _down = true; Invalidate(); } base.OnKeyDown(e); }
        protected override void OnKeyUp(KeyEventArgs e) { if (_down) { _down = false; Invalidate(); } base.OnKeyUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { _down = false; Invalidate(); base.OnLostFocus(e); }

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

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush bg = new SolidBrush(ParentBg())) g.FillRectangle(bg, ClientRectangle);

            Color fill = !Enabled ? (FillDisabled.IsEmpty ? Fill : FillDisabled)
                       : _down ? FillPressed : _hover ? FillHover : Fill;
            if (fill.IsEmpty) fill = BackColor;
            Rectangle r = ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            if (r.Width < 2 || r.Height < 2) return;
            int rad = Math.Max(1, Math.Min(Radius, Math.Min(r.Width, r.Height) / 2));

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = Rounded(r, rad))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, gp);
                if (!Border.IsEmpty) using (Pen p = new Pen(Border)) g.DrawPath(p, gp);
            }
            g.SmoothingMode = SmoothingMode.Default;

            Color fore = Enabled ? (TextColor.IsEmpty ? ForeColor : TextColor)
                                 : (TextDisabled.IsEmpty ? ForeColor : TextDisabled);
            TextFormatFlags f = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis;
            if (!ShowKeyboardCues) f |= TextFormatFlags.HidePrefix;
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, fore, f);

            if (Focused && ShowFocusCues)
            {
                int inset = Math.Max(2, Radius / 2);   // радиус уже в пикселях экрана (Px)
                Rectangle fr = ClientRectangle; fr.Inflate(-inset, -inset);
                ControlPaint.DrawFocusRectangle(g, fr, fore, fill);
            }
        }
    }
}
