using System;
using System.Drawing;
using System.Windows.Forms;

namespace WpcSetup
{
    // Окна установщика собираются руками, без автомасштабирования WinForms: манифест
    // объявляет dpiAware, размер в точках шрифт получает сам, а все пиксельные отступы
    // проходят через Px() — так на 125 % ничего не съезжает и не обрезается.
    internal static class Ui
    {
        public static readonly float Scale = DetectScale();

        public static readonly Font Base = new Font("Segoe UI", 9f);
        public static readonly Font Head = new Font("Segoe UI", 13.5f);
        public static readonly Font Strong = new Font("Segoe UI", 9f, FontStyle.Bold);

        public static readonly Color Ink = Color.FromArgb(30, 30, 30);
        public static readonly Color Dim = Color.FromArgb(112, 112, 112);
        public static readonly Color Line = Color.FromArgb(222, 222, 222);
        public static readonly Color Footer = Color.FromArgb(243, 243, 243);

        private static float DetectScale()
        {
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
            }
            catch { return 1f; }
        }

        public static int Px(int v) { return (int)Math.Round(v * Scale); }

        public static Label Text(Control parent, string text, int x, int y, int w, bool dim)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.Font = Base;
            l.ForeColor = dim ? Dim : Ink;
            l.BackColor = Color.Transparent;
            l.Location = new Point(x, y);
            l.Size = new Size(w, Px(18));
            parent.Controls.Add(l);
            return l;
        }

        // Подпись в несколько строк: у Label фиксированной высоты последняя строка
        // пропадает, поэтому высота задаётся явно и с запасом.
        public static Label Note(Control parent, string text, int x, int y, int w, int lines)
        {
            Label l = Text(parent, text, x, y, w, true);
            l.Size = new Size(w, Px(17) * lines + Px(4));
            return l;
        }

        public static Button MakeButton(Control parent, string text, int w)
        {
            Button b = new Button();
            b.Text = text;
            b.Font = Base;
            b.FlatStyle = FlatStyle.System;
            b.Size = new Size(w, Px(30));
            b.UseVisualStyleBackColor = true;
            parent.Controls.Add(b);
            return b;
        }

        public static CheckBox Check(Control parent, string text, int x, int y, int w)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Font = Base;
            c.ForeColor = Ink;
            c.BackColor = Color.Transparent;
            c.AutoSize = false;
            c.FlatStyle = FlatStyle.System;
            c.Location = new Point(x, y);
            c.Size = new Size(w, Px(22));
            parent.Controls.Add(c);
            return c;
        }

        public static TextBox Log(Control parent, int x, int y, int w, int h)
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Vertical;
            t.BackColor = Color.White;
            t.Font = Base;
            t.BorderStyle = BorderStyle.FixedSingle;
            t.Location = new Point(x, y);
            t.Size = new Size(w, h);
            t.TabStop = false;
            parent.Controls.Add(t);
            return t;
        }

        public static void SetIcon(Form f)
        {
            try { f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
            catch { }
        }
    }
}
