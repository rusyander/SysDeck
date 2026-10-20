// SysDeck — «ТВ-трансляция»: окно управления — папки, включение, адрес для телевизора, кто смотрит, настройки.
// Одно и то же окно: в SysDeck встроено страницей «ТВ-трансляция» (Embed), в отдельном PcStream — главное окно.
// Общее ядро с PcStream (см. Tv.Model.cs): из SysDeck здесь только Tr, Theme и RoundButton — их PcStream копирует.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.TvStream
{
    // Карточка-секция со скруглённой рамкой: углы закрашиваются цветом родителя, как у RoundButton.
    internal sealed class TvCard : Panel
    {
        public Color Border = Color.Gray;
        public TvCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(TvUi.Opaque(Parent))) e.Graphics.FillRectangle(b, ClientRectangle);
            Rectangle r = ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            if (r.Width < 20 || r.Height < 20) return;
            int d = 20;
            using (GraphicsPath p = new GraphicsPath())
            {
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush b = new SolidBrush(BackColor)) e.Graphics.FillPath(b, p);
                using (Pen pen = new Pen(Border)) e.Graphics.DrawPath(pen, p);
            }
        }
    }

    internal sealed class TvDot : Control
    {
        public Color Fill = Color.Gray;
        public TvDot() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            int s = Math.Min(Width, Height) - 2;
            using (SolidBrush b = new SolidBrush(Fill)) e.Graphics.FillEllipse(b, 1, (Height - s) / 2, s, s);
        }
    }

    internal sealed class TvList : ListView
    {
        public TvList()
        {
            DoubleBuffered = true;   // LVS_EX_DOUBLEBUFFER — единственная буферизация, что работает с OwnerDraw
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Nonclickable;
            OwnerDraw = true;
        }
    }

    // Общие элементы окон «ТВ-трансляции» (главное окно и пошаговое): подписи, кнопки, ряды, перекраска по теме.
    internal static class TvUi
    {
        // Цвет под прозрачной подложкой — первый непрозрачный предок. Прозрачная заливка в двойном буфере даёт чёрные углы.
        public static Color Opaque(Control c)
        {
            for (; c != null; c = c.Parent) if (c.BackColor.A == 255) return c.BackColor;
            return SystemColors.Control;
        }

        public static Label Label(string text, float size, FontStyle style, string role)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font("Segoe UI", size, style);
            l.BackColor = Color.Transparent;
            l.Tag = role;
            return l;
        }

        public static RoundButton Button(string text, bool primary, int width)
        {
            RoundButton b = new RoundButton();
            b.Text = text;
            b.Width = width;
            b.Height = 34;
            b.Radius = 8;
            b.Margin = new Padding(0, 4, 10, 4);
            b.Tag = primary ? "primary" : "button";
            b.Cursor = Cursors.Hand;
            return b;
        }

        public static FlowLayoutPanel Row()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.FlowDirection = FlowDirection.LeftToRight;
            f.WrapContents = false;
            f.BackColor = Color.Transparent;
            f.Tag = "surface";
            return f;
        }

        public static Color Ok(Theme t) { return t.Dark ? Color.FromArgb(74, 222, 128) : Color.FromArgb(22, 128, 61); }
        public static Color Warn(Theme t) { return t.Dark ? Color.FromArgb(251, 191, 36) : Color.FromArgb(180, 110, 0); }
        public static Color Error(Theme t) { return t.Dark ? Color.FromArgb(248, 113, 113) : Color.FromArgb(200, 40, 40); }

        // Роль элемента — в Tag: primary / button / card / subtle / accent / error / ok / warn / input / surface.
        public static void Recolor(Control root, Theme t)
        {
            foreach (Control c in root.Controls)
            {
                string role = c.Tag as string;
                RoundButton rb = c as RoundButton;
                if (rb != null)
                {
                    bool primary = role == "primary";
                    rb.Fill = primary ? t.Accent : t.Header;
                    rb.FillHover = primary ? Mix(t.Accent, Color.White, 0.12f) : Mix(t.Header, t.Text, 0.08f);
                    rb.FillPressed = primary ? Mix(t.Accent, Color.Black, 0.15f) : Mix(t.Header, t.Text, 0.14f);
                    rb.FillDisabled = t.Header;
                    rb.Border = primary ? Color.Empty : t.Border;
                    rb.TextColor = primary ? t.AccentText : t.Text;
                    rb.TextDisabled = t.Subtle;
                    rb.ForeColor = rb.TextColor;
                }
                else if (c is TvCard) { c.BackColor = t.Surface; ((TvCard)c).Border = t.Border; }
                else if (c is ListView) { c.BackColor = t.Surface; c.ForeColor = t.Text; }
                else if (c is TextBox) { c.BackColor = t.Bg; c.ForeColor = t.Text; }
                else if (c is Label || c is CheckBox)
                {
                    c.ForeColor = role == "subtle" ? t.Subtle : role == "accent" ? t.Accent : role == "error" ? Error(t)
                                : role == "ok" ? Ok(t) : role == "warn" ? Warn(t) : t.Text;
                }
                else if (c is Panel && role == null) c.BackColor = Color.Transparent;
                if (c.HasChildren) Recolor(c, t);
            }
        }

        public static Color Mix(Color a, Color b, float k)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));
        }
    }

    internal sealed class TvForm : Form
    {
        private Theme _t;
        private readonly float _scale;
        private readonly string _appName;

        private TvDot _dot;
        private Label _lblStatus, _lblStatusHint, _lblError, _lblAddr, _lblAddrHint, _lblFf, _lblFoldersEmpty, _lblSesEmpty, _lblSesCount;
        private RoundButton _btnPower, _btnFf, _btnPortApply;
        private TvList _lvFolders, _lvSessions;
        private TextBox _txtPort;
        private CheckBox _chkAuto, _chkThumbs, _chkLogon;
        private System.Windows.Forms.Timer _tick;
        private Panel _body;
        private TvGuideForm _guide;
        private readonly Dictionary<string, int> _counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _counting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private bool _loading;
        private bool _opened;

        // Только отдельное приложение: автозапуск с Windows и сворачивание в трей вместо закрытия.
        public Func<bool> GetRunAtLogon { get; set; }
        public Action<bool> SetRunAtLogon { get; set; }
        public bool HideOnClose { get; set; }

        public TvForm(Theme theme, string appName)
        {
            _t = theme;
            _appName = appName;
            using (Graphics g = CreateGraphics()) _scale = g.DpiX / 96f;
            Text = Tr.S("ТВ-трансляция — ", "TV streaming — ") + appName;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9.5F);
            ClientSize = new Size(920, 780);
            MinimumSize = new Size(780, 600);
            KeyPreview = true;
            BuildUi();
            ApplyTheme(theme);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;   // после построения: иначе размеры масштабируются дважды

            TvHost.Changed += OnHostChanged;
            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 2000;
            _tick.Tick += delegate { RefreshSessions(); };
            _tick.Start();
            Shown += delegate { Opened(); };
        }

        // Страница внутри чужого окна: без рамки и своей кнопки в панели задач, растягивается по родителю.
        public void Embed()
        {
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            MinimumSize = Size.Empty;
            Dock = DockStyle.Fill;
        }

        // Окно, которому подчиняются пошаговое окно и диалоги: само это окно или то, во что оно встроено.
        internal Form HostForm { get { return TopLevel ? this : (TopLevelControl as Form) ?? this; } }

        // Показ окна (Shown) или вход на страницу: у встроенного окна Shown не гарантирован, поэтому вход зовёт это сам.
        public void Opened()
        {
            if (_opened) { RefreshAll(); TvHost.CheckFirewallAsync(); return; }
            _opened = true;
            if (_chkLogon != null && GetRunAtLogon != null) { _loading = true; _chkLogon.Checked = GetRunAtLogon(); _loading = false; }
            RefreshAll();
            // Первым по порядку обхода стоит поле «Порт» в нижней карточке: без этого окно открывалось прокрученным вниз.
            ActiveControl = _btnPower;
            _body.AutoScrollPosition = Point.Empty;
            TvHost.CheckFirewallAsync();
            // Первое открытие — сразу пошаговая инструкция: без неё непонятно, что вводить в телевизоре.
            TvSettings first = TvHost.Snapshot();
            if (!first.GuideSeen)
            {
                first.GuideSeen = true;
                TvHost.Apply(first);
                OpenGuide();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (HideOnClose && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            Detach();
            base.OnFormClosed(e);
        }

        // Встроенное окно не закрывают, а уничтожают вместе со страницей — отписка и здесь.
        protected override void Dispose(bool disposing)
        {
            if (disposing) Detach();
            base.Dispose(disposing);
        }

        private void Detach()
        {
            TvHost.Changed -= OnHostChanged;
            _tick.Stop();
            _tick.Dispose();
        }

        private int Px(int v) { return (int)Math.Round(v * _scale); }

        private void OnHostChanged(object sender, EventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((MethodInvoker)RefreshAll); } catch (InvalidOperationException) { }
        }

        // ================= построение =================
        private void BuildUi()
        {
            Panel body = new Panel();
            _body = body;
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;
            body.Padding = new Padding(20, 4, 20, 20);

            // Dock = Top: последняя добавленная карточка оказывается сверху — перечисляем снизу вверх.
            body.Controls.Add(Gap(0));
            body.Controls.Add(BuildSettingsCard());
            body.Controls.Add(Gap(14));
            body.Controls.Add(BuildSessionsCard());
            body.Controls.Add(Gap(14));
            body.Controls.Add(BuildFoldersCard());
            body.Controls.Add(Gap(14));
            body.Controls.Add(BuildAddressCard());

            Controls.Add(body);
            Controls.Add(BuildHeader());
        }

        private static Control Gap(int h) { Panel p = new Panel(); p.Dock = DockStyle.Top; p.Height = h; return p; }

        private Control BuildHeader()
        {
            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 112;
            head.Padding = new Padding(20, 16, 20, 8);

            Label title = MkLabel(Tr.S("ТВ-трансляция", "TV streaming"), 17F, FontStyle.Bold, "");
            title.Location = new Point(20, 14);
            Label sub = MkLabel(Tr.S("Видео с этого компьютера на телевизоре по Wi-Fi — Hisense VIDAA, Media Station X или браузер ТВ.",
                                     "Videos from this PC on your TV over Wi-Fi — Hisense VIDAA, Media Station X or the TV browser."), 9.5F, FontStyle.Regular, "subtle");
            sub.Location = new Point(22, 50);

            _dot = new TvDot();
            _dot.Size = new Size(14, 14);
            _dot.Location = new Point(22, 82);
            _lblStatus = MkLabel("", 10.5F, FontStyle.Bold, "");
            _lblStatus.Location = new Point(40, 79);
            _lblStatusHint = MkLabel("", 9.5F, FontStyle.Regular, "subtle");
            _lblStatusHint.Location = new Point(40, 79);

            _btnPower = MkButton("", true, 220);
            _btnPower.Height = 40;
            _btnPower.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _btnPower.Location = new Point(head.Width - 20 - 220, 20);
            _btnPower.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            _btnPower.Click += delegate { TogglePower(); };

            _lblError = MkLabel("", 9.5F, FontStyle.Regular, "error");
            _lblError.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblError.AutoSize = false;
            _lblError.TextAlign = ContentAlignment.TopRight;
            _lblError.Size = new Size(420, 40);
            _lblError.Location = new Point(head.Width - 20 - 420, 66);

            head.Controls.Add(title);
            head.Controls.Add(sub);
            head.Controls.Add(_dot);
            head.Controls.Add(_lblStatus);
            head.Controls.Add(_lblStatusHint);
            head.Controls.Add(_btnPower);
            head.Controls.Add(_lblError);
            head.Resize += delegate
            {
                _btnPower.Left = head.ClientSize.Width - Px(20) - _btnPower.Width;
                _lblError.Left = head.ClientSize.Width - Px(20) - _lblError.Width;
            };
            return head;
        }

        private TvCard MkCard(string title, string hint, int height, out Panel content)
        {
            TvCard card = new TvCard();
            card.Dock = DockStyle.Top;
            card.Height = height;
            card.Padding = new Padding(18, 14, 18, 14);
            card.Tag = "card";

            content = new Panel();
            content.Dock = DockStyle.Fill;
            content.BackColor = Color.Transparent;
            content.Tag = "surface";

            Label h = MkLabel(title, 12F, FontStyle.Bold, "");
            h.AutoSize = false;
            h.Dock = DockStyle.Top;
            h.Height = 26;
            card.Controls.Add(content);
            if (!string.IsNullOrEmpty(hint))
            {
                Label hl = MkLabel(hint, 9F, FontStyle.Regular, "subtle");
                hl.AutoSize = false;
                hl.Dock = DockStyle.Top;
                hl.Height = 24;
                card.Controls.Add(hl);
            }
            card.Controls.Add(h);
            return card;
        }

        private Control BuildAddressCard()
        {
            Panel c;
            TvCard card = MkCard(Tr.S("Адрес для телевизора", "Address for the TV"), null, 196, out c);

            _lblAddr = MkLabel("", 24F, FontStyle.Bold, "accent");
            _lblAddr.Font = new Font("Consolas", 24F, FontStyle.Bold);
            _lblAddr.Location = new Point(0, 2);

            _lblAddrHint = MkLabel("", 9.5F, FontStyle.Regular, "subtle");
            _lblAddrHint.Location = new Point(2, 52);

            FlowLayoutPanel row = MkRow();
            row.Location = new Point(0, 98);
            row.Size = new Size(880, 44);
            RoundButton steps = MkButton(Tr.S("Как подключить ТВ — по шагам", "Connect the TV — step by step"), true, 250);
            steps.Click += delegate { OpenGuide(); };
            RoundButton copy = MkButton(Tr.S("Копировать адрес", "Copy address"), false, 160);
            copy.Click += delegate
            {
                try { Clipboard.SetText(TvHost.Address); copy.Text = Tr.S("Скопировано ✓", "Copied ✓"); }
                catch (Exception) { }
                System.Windows.Forms.Timer back = new System.Windows.Forms.Timer();
                back.Interval = 1500;
                back.Tick += delegate { back.Stop(); back.Dispose(); if (!copy.IsDisposed) copy.Text = Tr.S("Копировать адрес", "Copy address"); };
                back.Start();
            };
            RoundButton browser = MkButton(Tr.S("Открыть как на ТВ", "Open as on the TV"), false, 170);
            browser.Click += delegate { TvHost.OpenInBrowser(); };
            RoundButton fw = MkButton(Tr.S("Разрешить в брандмауэре", "Allow in firewall"), false, 210);
            fw.Click += delegate { FixFirewall(this); };
            row.Controls.Add(steps);
            row.Controls.Add(copy);
            row.Controls.Add(browser);
            row.Controls.Add(fw);

            c.Controls.Add(_lblAddr);
            c.Controls.Add(_lblAddrHint);
            c.Controls.Add(row);
            return card;
        }

        private Control BuildFoldersCard()
        {
            Panel c;
            TvCard card = MkCard(Tr.S("Папки с видео", "Video folders"),
                Tr.S("Каждая папка — отдельный раздел на телевизоре, вложенные папки открываются. Можно перетащить папку сюда из Проводника.",
                     "Each folder is a section on the TV; subfolders open too. You can drag a folder here from Explorer."), 300, out c);

            FlowLayoutPanel row = MkRow();
            row.Dock = DockStyle.Bottom;
            row.Height = 48;
            row.Padding = new Padding(0, 8, 0, 0);
            RoundButton add = MkButton(Tr.S("+ Добавить папку…", "+ Add folder…"), true, 170);
            add.Click += delegate { AddFolderDialog(); };
            RoundButton rename = MkButton(Tr.S("Переименовать", "Rename"), false, 140);
            rename.Click += delegate { if (_lvFolders.SelectedItems.Count > 0) _lvFolders.SelectedItems[0].BeginEdit(); };
            RoundButton remove = MkButton(Tr.S("Убрать из списка", "Remove from list"), false, 160);
            remove.Click += delegate { RemoveSelectedFolder(); };
            RoundButton open = MkButton(Tr.S("Показать в Проводнике", "Show in Explorer"), false, 190);
            open.Click += delegate
            {
                if (_lvFolders.SelectedItems.Count == 0) return;
                try { System.Diagnostics.Process.Start("explorer.exe", "\"" + ((TvFolder)_lvFolders.SelectedItems[0].Tag).Path + "\""); } catch (Exception) { }
            };
            row.Controls.Add(add);
            row.Controls.Add(rename);
            row.Controls.Add(remove);
            row.Controls.Add(open);

            _lvFolders = new TvList();
            _lvFolders.Dock = DockStyle.Fill;
            _lvFolders.LabelEdit = true;
            _lvFolders.MultiSelect = false;
            _lvFolders.AllowDrop = true;
            _lvFolders.Columns.Add(Tr.S("Название на ТВ", "Name on the TV"), Px(200));
            _lvFolders.Columns.Add(Tr.S("Папка на диске", "Folder on disk"), Px(420));
            _lvFolders.Columns.Add(Tr.S("Видео", "Videos"), Px(90), HorizontalAlignment.Right);
            WireList(_lvFolders, 1);
            _lvFolders.AfterLabelEdit += delegate(object s, LabelEditEventArgs e)
            {
                if (e.Label == null) return;
                string name = e.Label.Trim();
                if (name.Length == 0) { e.CancelEdit = true; return; }
                TvSettings next = TvHost.Snapshot();
                next.Folders[e.Item].Name = name;
                TvHost.Apply(next);
            };
            _lvFolders.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Delete) RemoveSelectedFolder();
                else if (e.KeyCode == Keys.F2 && _lvFolders.SelectedItems.Count > 0) _lvFolders.SelectedItems[0].BeginEdit();
            };
            _lvFolders.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; };
            _lvFolders.DragDrop += delegate(object s, DragEventArgs e)
            {
                string[] paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths == null) return;
                List<string> dirs = new List<string>();
                foreach (string p in paths) if (Directory.Exists(p)) dirs.Add(p);
                AddFolders(dirs);
            };

            _lblFoldersEmpty = MkLabel(Tr.S("Папок пока нет. Нажмите «+ Добавить папку…» и выберите папку с фильмами или сериалами.",
                                            "No folders yet. Press «+ Add folder…» and pick a folder with movies or shows."), 10F, FontStyle.Regular, "subtle");
            _lblFoldersEmpty.AutoSize = false;
            _lblFoldersEmpty.Dock = DockStyle.Fill;
            _lblFoldersEmpty.TextAlign = ContentAlignment.MiddleCenter;

            c.Controls.Add(_lvFolders);
            c.Controls.Add(_lblFoldersEmpty);
            c.Controls.Add(row);
            return card;
        }

        private Control BuildSessionsCard()
        {
            Panel c;
            TvCard card = MkCard(Tr.S("Сейчас смотрят", "Watching now"), null, 210, out c);
            _lblSesCount = MkLabel("", 9.5F, FontStyle.Regular, "subtle");
            _lblSesCount.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            _lblSesCount.AutoSize = false;
            _lblSesCount.TextAlign = ContentAlignment.MiddleRight;
            _lblSesCount.Size = new Size(300, 24);
            _lblSesCount.Location = new Point(card.Width - 18 - 300, 14);
            card.Controls.Add(_lblSesCount);
            _lblSesCount.BringToFront();
            card.Resize += delegate { _lblSesCount.Left = card.ClientSize.Width - Px(18) - _lblSesCount.Width; };

            _lvSessions = new TvList();
            _lvSessions.Dock = DockStyle.Fill;
            _lvSessions.Columns.Add(Tr.S("Устройство", "Device"), Px(150));
            _lvSessions.Columns.Add(Tr.S("Адрес", "Address"), Px(120));
            _lvSessions.Columns.Add(Tr.S("Что смотрит", "Watching"), Px(330));
            _lvSessions.Columns.Add(Tr.S("Режим", "Mode"), Px(130));
            _lvSessions.Columns.Add(Tr.S("С", "Since"), Px(70));
            WireList(_lvSessions, 2);

            _lblSesEmpty = MkLabel(Tr.S("Пока никто не смотрит. Откройте видео на телевизоре — оно появится здесь.",
                                        "Nobody is watching yet. Open a video on the TV and it shows up here."), 10F, FontStyle.Regular, "subtle");
            _lblSesEmpty.AutoSize = false;
            _lblSesEmpty.Dock = DockStyle.Fill;
            _lblSesEmpty.TextAlign = ContentAlignment.MiddleCenter;

            c.Controls.Add(_lvSessions);
            c.Controls.Add(_lblSesEmpty);
            return card;
        }

        private Control BuildSettingsCard()
        {
            Panel c;
            TvCard card = MkCard(Tr.S("Настройки", "Settings"), null, GetRunAtLogonPlaceholder() ? 230 : 200, out c);

            FlowLayoutPanel portRow = MkRow();
            portRow.Dock = DockStyle.Top;
            portRow.Height = 40;
            Label portLbl = MkLabel(Tr.S("Порт:", "Port:"), 10F, FontStyle.Regular, "");
            portLbl.Margin = new Padding(0, 9, 8, 0);
            _txtPort = new TextBox();
            _txtPort.Width = 80;
            _txtPort.MaxLength = 5;
            _txtPort.BorderStyle = BorderStyle.FixedSingle;
            _txtPort.Margin = new Padding(0, 6, 8, 0);
            _txtPort.Tag = "input";
            _txtPort.TextChanged += delegate { UpdatePortButton(); };
            _txtPort.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { ApplyPort(); e.SuppressKeyPress = true; } };
            _btnPortApply = MkButton(Tr.S("Применить", "Apply"), false, 110);
            _btnPortApply.Click += delegate { ApplyPort(); };
            Label portHint = MkLabel(Tr.S("после смены порта введите новый адрес в телевизоре", "after changing the port enter the new address on the TV"), 9F, FontStyle.Regular, "subtle");
            portHint.Margin = new Padding(8, 10, 0, 0);
            portRow.Controls.Add(portLbl);
            portRow.Controls.Add(_txtPort);
            portRow.Controls.Add(_btnPortApply);
            portRow.Controls.Add(portHint);

            _chkAuto = MkCheck(Tr.S("Включать трансляцию при запуске программы", "Start streaming when the app starts"));
            _chkAuto.CheckedChanged += delegate { if (_loading) return; TvSettings n = TvHost.Snapshot(); n.AutoStart = _chkAuto.Checked; TvHost.Apply(n); };
            _chkThumbs = MkCheck(Tr.S("Превью кадров на плитках (нужен ffmpeg)", "Frame previews on tiles (needs ffmpeg)"));
            _chkThumbs.CheckedChanged += delegate { if (_loading) return; TvSettings n = TvHost.Snapshot(); n.Thumbnails = _chkThumbs.Checked; TvHost.Apply(n); };

            FlowLayoutPanel ffRow = MkRow();
            ffRow.Dock = DockStyle.Top;
            ffRow.Height = 42;
            _lblFf = MkLabel("", 9.5F, FontStyle.Regular, "");
            _lblFf.Margin = new Padding(0, 11, 10, 0);
            _btnFf = MkButton(Tr.S("Скачать ffmpeg", "Download ffmpeg"), true, 170);
            _btnFf.Click += delegate { ToggleFfmpegDownload(); };
            RoundButton recheck = MkButton(Tr.S("Проверить снова", "Check again"), false, 150);
            recheck.Click += delegate { TvHost.RelocateFfmpeg(); };
            ffRow.Controls.Add(_lblFf);
            ffRow.Controls.Add(_btnFf);
            ffRow.Controls.Add(recheck);

            // Dock = Top в обратном порядке: снизу вверх.
            c.Controls.Add(ffRow);
            if (GetRunAtLogonPlaceholder())
            {
                _chkLogon = MkCheck(Tr.S("Запускать " + _appName + " вместе с Windows (в трее)", "Start " + _appName + " with Windows (in the tray)"));
                _chkLogon.CheckedChanged += delegate { if (!_loading && SetRunAtLogon != null) SetRunAtLogon(_chkLogon.Checked); };
                c.Controls.Add(_chkLogon);
            }
            c.Controls.Add(_chkThumbs);
            c.Controls.Add(_chkAuto);
            c.Controls.Add(portRow);
            return card;
        }

        // Флажок автозапуска нужен только отдельному приложению: SysDeck управляет своим автозапуском сам.
        // Окно строится в конструкторе, раньше, чем хост присвоит делегаты, поэтому решает имя приложения.
        private bool GetRunAtLogonPlaceholder() { return _appName == "PC Stream"; }

        // ================= элементы =================
        private static Label MkLabel(string text, float size, FontStyle style, string role) { return TvUi.Label(text, size, style, role); }

        private static RoundButton MkButton(string text, bool primary, int width) { return TvUi.Button(text, primary, width); }

        private static FlowLayoutPanel MkRow() { return TvUi.Row(); }

        private CheckBox MkCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.Dock = DockStyle.Top;
            c.Height = 30;
            c.Font = new Font("Segoe UI", 10F);
            c.BackColor = Color.Transparent;
            c.Tag = "";
            return c;
        }

        // Шапка списка рисуется цветом темы; строки — штатно. Последняя «гибкая» колонка забирает остаток ширины.
        private void WireList(TvList lv, int flexColumn)
        {
            lv.DrawColumnHeader += delegate(object s, DrawListViewColumnHeaderEventArgs e)
            {
                using (SolidBrush b = new SolidBrush(_t.Header)) e.Graphics.FillRectangle(b, e.Bounds);
                Rectangle r = e.Bounds;
                r.Inflate(-6, 0);
                TextFormatFlags f = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis
                                    | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, r, _t.Subtle, f);
            };
            lv.DrawItem += delegate(object s, DrawListViewItemEventArgs e) { e.DrawDefault = true; };
            lv.DrawSubItem += delegate(object s, DrawListViewSubItemEventArgs e) { e.DrawDefault = true; };
            lv.Resize += delegate { FitColumns(lv, flexColumn); };
        }

        private static void FitColumns(ListView lv, int flex)
        {
            int fixedW = 0;
            for (int i = 0; i < lv.Columns.Count; i++) if (i != flex) fixedW += lv.Columns[i].Width;
            int rest = lv.ClientSize.Width - fixedW - 2;
            if (rest > 80 && lv.Columns[flex].Width != rest) lv.Columns[flex].Width = rest;
        }

        // ================= тема =================
        public void ApplyTheme(Theme t)
        {
            _t = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            TvUi.Recolor(this, t);
            Invalidate(true);
            if (_guide != null && !_guide.IsDisposed) _guide.ApplyTheme(t);
        }


        // ================= состояние =================
        private void RefreshAll()
        {
            if (IsDisposed) return;
            _loading = true;
            try
            {
                TvSettings s = TvHost.Snapshot();
                bool on = TvHost.Running;
                string addr = TvHost.Address;
                _dot.Fill = on ? Color.FromArgb(34, 197, 94) : _t.Subtle;
                _dot.Invalidate();
                _lblStatus.Text = on ? Tr.S("Трансляция включена", "Streaming is on") : Tr.S("Трансляция выключена", "Streaming is off");
                _lblStatusHint.Left = _lblStatus.Right + Px(8);
                _lblStatusHint.Text = s.Folders.Count == 0 ? Tr.S("— добавьте хотя бы одну папку", "— add at least one folder")
                                     : on ? Tr.S("— телевизор может подключаться", "— the TV can connect")
                                     : Tr.S("— телевизор сейчас ничего не увидит", "— the TV sees nothing right now");
                _btnPower.Text = on ? Tr.S("■  Выключить трансляцию", "■  Stop streaming") : Tr.S("▶  Включить трансляцию", "▶  Start streaming");
                _lblError.Text = TvHost.LastError ?? "";

                _lblAddr.Text = addr;
                _lblAddr.ForeColor = on ? _t.Accent : _t.Subtle;
                _lblAddrHint.Text = Tr.S("Media Station X на ТВ:  Settings → Start Parameter → Setup → наберите адрес выше, без http://\n"
                                         + "Браузер телевизора:  http://" + addr + "        Компьютер и ТВ — в одной домашней сети.",
                                         "Media Station X on the TV:  Settings → Start Parameter → Setup → type the address above, no http://\n"
                                         + "TV browser:  http://" + addr + "        PC and TV on the same home network.");

                if (_txtPort.Focused == false) _txtPort.Text = s.Port.ToString();
                UpdatePortButton();
                _chkAuto.Checked = s.AutoStart;
                _chkThumbs.Checked = s.Thumbnails;
                TvFfmpeg ff = TvHost.Ffmpeg;
                _lblFf.Text = FfmpegLine();
                _lblFf.ForeColor = TvHost.FfmpegError != null ? TvUi.Error(_t) : ff.Available || TvHost.FfmpegBusy ? _t.Text : TvUi.Warn(_t);
                _btnFf.Visible = !ff.Available || TvHost.FfmpegBusy;
                _btnFf.Text = TvHost.FfmpegBusy ? Tr.S("Отменить", "Cancel") : Tr.S("Скачать ffmpeg", "Download ffmpeg");
                if (_guide != null && !_guide.IsDisposed) _guide.RefreshSteps();

                RefreshFolders(s);
                RefreshSessions();
            }
            finally { _loading = false; }
        }

        private void RefreshFolders(TvSettings s)
        {
            int sel = _lvFolders.SelectedIndices.Count > 0 ? _lvFolders.SelectedIndices[0] : -1;
            _lvFolders.BeginUpdate();
            _lvFolders.Items.Clear();
            foreach (TvFolder f in s.Folders)
            {
                bool exists = Directory.Exists(f.Path);
                int n;
                string count = !exists ? Tr.S("нет папки", "missing") : _counts.TryGetValue(f.Path, out n) ? (n >= 5000 ? "5000+" : n.ToString()) : "…";
                ListViewItem it = new ListViewItem(new[] { f.Name, f.Path, count });
                it.Tag = f;
                if (!exists) it.ForeColor = _t.Subtle;
                _lvFolders.Items.Add(it);
                if (exists && !_counts.ContainsKey(f.Path)) CountAsync(f.Path);
            }
            if (sel >= 0 && sel < _lvFolders.Items.Count) _lvFolders.Items[sel].Selected = true;
            _lvFolders.EndUpdate();
            bool empty = s.Folders.Count == 0;
            _lvFolders.Visible = !empty;
            _lblFoldersEmpty.Visible = empty;
            FitColumns(_lvFolders, 1);
        }

        private void CountAsync(string path)
        {
            lock (_counting) { if (!_counting.Add(path)) return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                int n = TvLibrary.CountVideos(path, 5000);
                lock (_counting) _counting.Remove(path);
                if (IsDisposed || !IsHandleCreated) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _counts[path] = n;
                        RefreshFolders(TvHost.Snapshot());
                    });
                }
                catch (InvalidOperationException) { }
            });
        }

        private void RefreshSessions()
        {
            if (IsDisposed || TvHost.Server == null) return;
            List<TvSession> list = TvHost.Server.Sessions();
            _lvSessions.BeginUpdate();
            _lvSessions.Items.Clear();
            foreach (TvSession ses in list)
            {
                string mode = ses.Mode == "transcode" ? Tr.S("перекодирование", "transcoding") : Tr.S("напрямую", "direct");
                if (ses.Active == 0) mode += Tr.S(" · пауза", " · paused");
                ListViewItem it = new ListViewItem(new[] { ses.Device, ses.Remote, Path.GetFileName(ses.File), mode, ses.Started.ToString("HH:mm") });
                _lvSessions.Items.Add(it);
            }
            _lvSessions.EndUpdate();
            _lvSessions.Visible = list.Count > 0;
            _lblSesEmpty.Visible = list.Count == 0;
            _lblSesCount.Text = list.Count == 0 ? "" : Tr.N(list.Count, "устройство", "устройства", "устройств", "device", "devices");
            FitColumns(_lvSessions, 2);
        }

        // ================= действия =================
        // Строка о ffmpeg — общая для настроек и пошагового окна.
        internal static string FfmpegLine()
        {
            if (TvHost.FfmpegBusy)
            {
                long got = TvHost.FfmpegGot, total = Math.Max(1, TvHost.FfmpegTotal);
                return Tr.S("Скачивается ffmpeg: ", "Downloading ffmpeg: ") + (got * 100 / total) + " % (" + (got >> 20) + Tr.S(" из ", " of ") + (total >> 20) + Tr.S(" МБ)…", " MB)…");
            }
            if (TvHost.FfmpegError != null) return TvHost.FfmpegError;
            return TvHost.Ffmpeg.Available
                ? Tr.S("ffmpeg есть — MKV, AVI и другие форматы перекодируются на лету, есть превью.", "ffmpeg is here — MKV, AVI and other formats are transcoded on the fly, previews on.")
                : Tr.S("ffmpeg нет — играют только MP4/WebM/MOV, без превью. Скачается по кнопке (~115 МБ).", "No ffmpeg — only MP4/WebM/MOV play, no previews. One click downloads it (~115 MB).");
        }

        internal static void ToggleFfmpegDownload()
        {
            if (TvHost.FfmpegBusy) TvHost.CancelFfmpegDownload();
            else TvHost.DownloadFfmpeg();
        }

        internal static void FixFirewall(IWin32Window owner)
        {
            bool ok = TvHost.FixFirewall();
            MessageBox.Show(owner, ok
                ? Tr.S("Готово: порт " + TvHost.Snapshot().Port + " открыт для устройств вашей домашней сети.", "Done: port " + TvHost.Snapshot().Port + " is open to devices on your home network.")
                : Tr.S("Правило не добавлено: запрос прав администратора отклонён или Windows вернула ошибку.", "The rule was not added: the administrator prompt was declined or Windows returned an error."),
                Tr.S("Брандмауэр Windows", "Windows Firewall"));
        }

        internal void OpenGuide()
        {
            if (_guide == null || _guide.IsDisposed)
            {
                _guide = new TvGuideForm(_t, this);
                _guide.Icon = HostForm.Icon;
                _guide.FormClosed += delegate { _guide = null; };
                _guide.Show(HostForm);
            }
            if (_guide.WindowState == FormWindowState.Minimized) _guide.WindowState = FormWindowState.Normal;
            _guide.Activate();
        }

        internal void TogglePower()
        {
            if (TvHost.Running) { TvHost.Stop(); return; }
            if (TvHost.Snapshot().Folders.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Сначала добавьте папку с видео — без неё телевизору нечего показать.",
                                           "Add a video folder first — otherwise the TV has nothing to show."), Text);
                return;
            }
            TvHost.Start();
        }

        internal void AddFolderDialog()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = Tr.S("Выберите папку с видео — она станет разделом на телевизоре.", "Pick a video folder — it becomes a section on the TV.");
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) == DialogResult.OK) AddFolders(new List<string> { d.SelectedPath });
            }
        }

        private void AddFolders(List<string> paths)
        {
            if (paths.Count == 0) return;
            TvSettings next = TvHost.Snapshot();
            foreach (string p in paths) next.Folders.Add(new TvFolder(p, TvSettings.DefaultName(p)));
            bool wasEmpty = TvHost.Snapshot().Folders.Count == 0;
            TvHost.Apply(next);
            // Первая папка — значит, человек собирается смотреть: включаем, если он это разрешил.
            if (wasEmpty && next.AutoStart && !TvHost.Running) TvHost.Start();
        }

        private void RemoveSelectedFolder()
        {
            if (_lvFolders.SelectedIndices.Count == 0) return;
            int i = _lvFolders.SelectedIndices[0];
            TvSettings next = TvHost.Snapshot();
            if (i >= next.Folders.Count) return;
            next.Folders.RemoveAt(i);   // файлы на диске не трогаются — папка только перестаёт быть видна телевизору
            TvHost.Apply(next);
        }

        private int ParsedPort()
        {
            int p;
            return int.TryParse(_txtPort.Text.Trim(), out p) && p >= 1024 && p <= 65535 ? p : -1;
        }

        private void UpdatePortButton()
        {
            int p = ParsedPort();
            _btnPortApply.Enabled = p > 0 && p != TvHost.Snapshot().Port;
        }

        private void ApplyPort()
        {
            int p = ParsedPort();
            if (p < 0)
            {
                MessageBox.Show(this, Tr.S("Порт — число от 1024 до 65535.", "The port is a number from 1024 to 65535."), Text);
                return;
            }
            TvSettings next = TvHost.Snapshot();
            if (next.Port == p) return;
            next.Port = p;
            TvHost.Apply(next);
        }
    }
}
