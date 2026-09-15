// SysDeck — общие кирпичики раскладки: панели кнопок с переносом,
// строки-подписи, масштаб DPI, добивка ширины списков после ресайза, колонки настроек.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        // ---------- DPI ----------
        // Коэффициент DPI (1.0 при 96, 1.25 при 120, 1.5 при 144). Координаты и размеры
        // контролов масштабирует сама форма (AutoScaleMode.Dpi), а всё, что рисуем или
        // задаём сами — галочки в списках, ширины колонок — умножаем через Px().
        private float _dpiScale = 1f;

        private int Px(int v) { return (int)Math.Round(v * _dpiScale); }

        // Обратно: пиксели экрана → пиксели макета. Нужен там, где размер считается из
        // измеренного текста ДО прохода масштабирования формы (BuildUi): шрифт уже
        // отрисован под DPI экрана, и без деления форма умножила бы ширину второй раз.
        private int Unscaled(int devicePx) { return (int)Math.Ceiling(devicePx / _dpiScale); }

        private void InitDpi()
        {
            try { using (Graphics g = CreateGraphics()) _dpiScale = g.DpiX / 96f; }
            catch { _dpiScale = 1f; }
            if (_dpiScale < 0.5f || _dpiScale > 8f) _dpiScale = 1f;
        }

        // Включает масштабирование под DPI. Вызывать ПОСЛЕ того, как все контролы добавлены:
        // форма масштабирует детей один раз при ближайшей раскладке, а всё, что добавлено
        // после этого прохода, остаётся в координатах 96 DPI. Порядок свойств как у
        // дизайнера: сначала базовые размеры, потом режим.
        private static void ApplyDpiTo(Form f)
        {
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.AutoScaleMode = AutoScaleMode.Dpi;
        }

        // На экране 1366×768 окно высотой 740 не влезало под панель задач; при крупном DPI
        // и масштабированный минимум может оказаться больше рабочей области — тогда минимум
        // уступает экрану, а вкладки навигации ужимаются пропорционально (layoutNav).
        private void ClampToScreen()
        {
            Rectangle wa = Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
            if (wa.Width <= 0 || wa.Height <= 0) return;
            if (MinimumSize.Width > wa.Width || MinimumSize.Height > wa.Height)
                MinimumSize = new Size(Math.Min(MinimumSize.Width, wa.Width), Math.Min(MinimumSize.Height, wa.Height));
            if (Width > wa.Width) Width = Math.Max(MinimumSize.Width, wa.Width);
            if (Height > wa.Height) Height = Math.Max(MinimumSize.Height, wa.Height);
        }

        // ---------- Панели кнопок ----------
        // Кнопки идут в ряд и переносятся на следующую строку, когда окно уже. Раньше
        // панели были свёрстаны по фиксированным X, и при крупном DPI или длинных
        // английских подписях последние кнопки вылезали за правый край.
        private FlowLayoutPanel MkToolbar()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Dock = DockStyle.Top;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Padding = new Padding(0, 6, 0, 0);
            return f;
        }

        // Кнопка для панели с переносом. Ширина не меньше текста: Button режет подпись
        // без многоточия, и «…образы» с «…тома» на кнопках удаления выглядели одинаково.
        private Button MkFlowButton(string text, int w, bool primary)
        {
            Button b = new RoundButton();
            b.Text = text;
            b.Width = Math.Max(w, Unscaled(TextRenderer.MeasureText(text, Font).Width) + 28);
            b.Height = 36;
            b.Margin = new Padding(0, 0, 8, 8);
            if (primary) b.Tag = "primary";
            return b;
        }

        // Подпись внутри панели кнопок, по вертикали — по центру кнопки высотой 36.
        private Label MkFlowLabel(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            l.Margin = new Padding(0, muted ? 11 : 9, 12, 0);
            return l;
        }

        // Строка-подпись под панелью кнопок: одна строка на всю ширину, хвост в многоточие
        // (полный текст — во всплывающей подсказке ListView его нет, поэтому коротко).
        private Label MkNote(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.Dock = DockStyle.Top;
            l.AutoEllipsis = true;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Height = muted ? 20 : 26;
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            return l;
        }

        // ---------- Добивка колонок после ресайза ----------
        // Обработчик Resize подгоняет остаточную колонку без «дёрганья» (force=false), а
        // common controls не сбрасывают уже выставленный WS_HSCROLL сами. При двухшаговом
        // ресайзе (SplitContainer на вкладке «Диск») полоса прокрутки так и оставалась
        // висеть. Один отложенный проход с force через 150 мс после последнего Resize
        // убирает её; таймер один на все списки, перезапуск = дебаунс.
        private System.Windows.Forms.Timer _fillSettle;
        private readonly List<ListView> _settleQueue = new List<ListView>();

        private void ScheduleAutoFillSettle(ListView lv)
        {
            if (lv == null || _closing || !IsHandleCreated) return;
            if (!_settleQueue.Contains(lv)) _settleQueue.Add(lv);
            if (_fillSettle == null)
            {
                _fillSettle = new System.Windows.Forms.Timer();
                _fillSettle.Interval = 150;
                _fillSettle.Tick += delegate
                {
                    _fillSettle.Stop();
                    ListView[] pend = _settleQueue.ToArray();
                    _settleQueue.Clear();
                    foreach (ListView t in pend)
                        if (t != null && !t.IsDisposed && t.IsHandleCreated) AutoFillLastColumn(t, true);
                };
            }
            _fillSettle.Stop();
            _fillSettle.Start();
        }

        // ---------- Рамки контентных блоков ----------
        // Системный BorderStyle.FixedSingle рисует у списков и полей резкую линию цветом
        // Windows — в тёмной теме белую. Вместо неё: у контрола рамки нет, углы срезаны
        // Region, а родитель рисует вокруг него скруглённую линию цветом темы (Border).
        private readonly HashSet<Control> _boxed = new HashSet<Control>();
        private readonly HashSet<Control> _boxedParents = new HashSet<Control>();
        private const int BoxRadius = 8;

        private void Boxed(Control c)
        {
            if (c == null || c.Parent == null) return;
            if (!_boxed.Add(c)) return;
            RoundControl(c, Px(BoxRadius));
            Control parent = c.Parent;
            if (_boxedParents.Add(parent))
            {
                parent.Paint += Boxed_ParentPaint;
                parent.Disposed += delegate { _boxedParents.Remove(parent); };
            }
            EventHandler inval = delegate { if (c.Parent != null) c.Parent.Invalidate(); };
            c.Resize += inval;
            c.LocationChanged += inval;
            c.VisibleChanged += inval;
            c.Disposed += delegate { _boxed.Remove(c); };
        }

        // Поле или список, у которого рамку рисует обёртка «box» или сам NumericUpDown, —
        // не оборачиваем второй раз.
        private void BoxUnlessWrapped(Control c)
        {
            if (c == null || c.Parent == null) return;
            if (c.Parent is NumericUpDown || c.Parent.Name == "box") return;
            Boxed(c);
        }

        private void Boxed_ParentPaint(object sender, PaintEventArgs e)
        {
            Control parent = sender as Control;
            if (parent == null || _theme == null) return;
            SmoothingMode was = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(_theme.Border))
            {
                foreach (Control c in parent.Controls)
                {
                    if (!c.Visible || !_boxed.Contains(c)) continue;
                    Rectangle r = c.Bounds;
                    r.Inflate(1, 1);
                    r.Width -= 1; r.Height -= 1;
                    if (r.Width < 4 || r.Height < 4) continue;
                    using (GraphicsPath gp = RoundedRect(r, Px(BoxRadius) + 1))
                    {
                        // Углы контрола срезаны Region ступенчато; подложка его же цветом
                        // под сглаженной рамкой прячет ступеньки — угол выглядит гладким.
                        using (SolidBrush fb = new SolidBrush(c.BackColor)) e.Graphics.FillPath(fb, gp);
                        e.Graphics.DrawPath(pen, gp);
                    }
                }
            }
            e.Graphics.SmoothingMode = was;
        }

        // Поле ввода внутри «коробки»: у TextBox без рамки текст прилипает к краю, поэтому
        // рамку и внутренний отступ несёт панель-обёртка, а само поле растянуто внутри.
        private Panel MkBox(Control inner, Padding pad)
        {
            Panel box = new Panel();
            box.Name = "box";
            box.Padding = pad;
            inner.Dock = DockStyle.Fill;
            box.Controls.Add(inner);
            return box;
        }

        // ---------- Колонки настроек ----------
        // Раскладка страницы настроек считается от текста, а не от фиксированных X:
        // поля выравниваются по самой длинной подписи слева, правая колонка начинается
        // после самого длинного чекбокса и растягивается до правого края. Иначе при
        // крупном DPI или английских подписях текст наезжал на поля.
        private readonly List<Label> _setLabels = new List<Label>();
        private readonly List<Control> _setFields = new List<Control>();
        private readonly List<CheckBox> _setChecks = new List<CheckBox>();
        private readonly List<Control> _setRight = new List<Control>();
        // Вертикаль правой колонки: базовые (до масштаба DPI) отступы сверху и высоты
        // многострочных списков; на высоком окне списки растягиваются на свободное место.
        private readonly Dictionary<Control, int> _setTop = new Dictionary<Control, int>();
        private readonly Dictionary<Control, int> _setStretch = new Dictionary<Control, int>();
        private Control _setSpacer;
        private Panel _setBody;
        private int _setLeftBottom, _setRightBottom;

        private void LayoutSettings(Panel body)
        {
            if (_setLabels.Count == 0 || _setRight.Count == 0) return;
            body.SuspendLayout();
            try
            {
                int lx = _setLabels[0].Left;
                int cx = lx;
                foreach (Label l in _setLabels) cx = Math.Max(cx, l.Right + Px(12));
                int fieldsRight = cx;
                foreach (Control f in _setFields) { f.Left = cx; fieldsRight = Math.Max(fieldsRight, f.Right); }
                int rx = fieldsRight + Px(60);
                foreach (CheckBox c in _setChecks) rx = Math.Max(rx, c.Right + Px(24));
                int rw = Math.Max(Px(300), body.ClientSize.Width - lx - rx);
                foreach (Control r in _setRight) { r.Left = rx; if (!(r is Label)) r.Width = rw; }

                // Вертикаль: списки правой колонки забирают свободную высоту окна — на
                // высоком экране под ними не остаётся пустоты. Когда места нет, остаются
                // базовые высоты и страница прокручивается (кнопки закреплены снизу).
                // Базовые значения записаны до масштаба DPI, поэтому здесь Px(); до первого
                // показа формы не трогаем — она ещё масштабирует детей сама.
                if (!_ready || _setStretch.Count == 0) return;
                int scrollY = body.AutoScrollPosition.Y;
                int leftBottom = Px(_setLeftBottom), rightBottom = Px(_setRightBottom);
                int target = Math.Max(body.ClientSize.Height - Px(8), leftBottom);
                int extra = Math.Max(0, target - rightBottom);
                int baseSum = 0;
                foreach (int h in _setStretch.Values) baseSum += h;
                int shift = 0, given = 0, left = _setStretch.Count;
                foreach (Control r in _setRight)
                {
                    int top;
                    if (!_setTop.TryGetValue(r, out top)) continue;
                    r.Top = Px(top) + shift + scrollY;
                    int bh;
                    if (!_setStretch.TryGetValue(r, out bh)) continue;
                    left--;
                    int add = left == 0 ? extra - given : (int)((long)extra * bh / baseSum);
                    given += add;
                    r.Height = Px(bh) + add;
                    shift += add;
                }
                if (_setSpacer != null) _setSpacer.Top = Math.Max(leftBottom, rightBottom + extra) + scrollY;
            }
            finally { body.ResumeLayout(); }
        }
    }
}
