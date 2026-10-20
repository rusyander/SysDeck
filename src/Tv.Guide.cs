// SysDeck — «ТВ-трансляция»: пошаговое окно «Как подключить телевизор». Шаги на компьютере — с живым статусом
// и кнопкой (папки, включение, брандмауэр, ffmpeg); шаги на телевизоре — точные пункты меню Media Station X
// и настоящий адрес этого компьютера. Открывается из главного окна (TvForm) и само при первом запуске.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SysDeck.TvStream
{
    internal sealed class TvGuideForm : Form
    {
        private sealed class Step
        {
            public TvCard Card;
            public Label Num, Title, Status;
            public readonly List<Control> Body = new List<Control>();   // сверху вниз: текст, крупный адрес, ряд кнопок
        }

        private Theme _t;
        private readonly TvForm _owner;
        private readonly float _scale;
        private readonly Panel _body;
        private readonly List<Step> _steps = new List<Step>();
        private readonly System.Windows.Forms.Timer _tick;
        private Step _sFolders, _sPower, _sFirewall, _sFfmpeg, _sMsx, _sWatch, _sBrowser;
        private RoundButton _btnAdd, _btnPower, _btnFw, _btnFf;
        private Label _lblMsxAddr, _lblBrowserAddr;

        public TvGuideForm(Theme theme, TvForm owner)
        {
            _t = theme;
            _owner = owner;
            using (Graphics g = CreateGraphics()) _scale = g.DpiX / 96f;
            Text = Tr.S("Как подключить телевизор — по шагам", "Connect the TV — step by step");
            Font = new Font("Segoe UI", 9.5F);
            ClientSize = new Size(780, 860);
            MinimumSize = new Size(620, 480);
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;

            _body = new Panel();
            _body.Dock = DockStyle.Fill;
            _body.AutoScroll = true;
            _body.Tag = null;
            BuildSteps();
            Controls.Add(_body);
            Controls.Add(BuildHeader());
            ApplyTheme(theme);
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;   // после построения: иначе размеры масштабируются дважды

            _body.Resize += delegate { LayoutSteps(); };
            _tick = new System.Windows.Forms.Timer();
            _tick.Interval = 2000;
            _tick.Tick += delegate { RefreshSteps(); };
            _tick.Start();
            Load += delegate { PlaceNextToOwner(); };
            Shown += delegate
            {
                TvHost.CheckFirewallAsync();
                RefreshSteps();
                LayoutSteps();
                _body.AutoScrollPosition = Point.Empty;
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            _tick.Dispose();
            base.OnFormClosed(e);
        }

        private int Px(int v) { return (int)Math.Round(v * _scale); }

        // Справа от главного окна, если там есть место на том же экране; иначе — по центру над ним.
        private void PlaceNextToOwner()
        {
            Form host = _owner.HostForm;
            Rectangle area = Screen.FromControl(host).WorkingArea;
            Rectangle o = host.Bounds;
            int h = Math.Min(Height, area.Height);
            if (o.Right + Width <= area.Right) Location = new Point(o.Right, Math.Max(area.Top, Math.Min(o.Top, area.Bottom - h)));
            else Location = new Point(Math.Max(area.Left, o.Left + (o.Width - Width) / 2), Math.Max(area.Top, o.Top + 30));
            Height = h;
        }

        // ================= построение =================
        private Control BuildHeader()
        {
            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = 92;
            Label title = TvUi.Label(Tr.S("Подключение телевизора", "Connecting the TV"), 16F, FontStyle.Bold, "");
            title.Location = new Point(20, 12);
            Label sub = TvUi.Label(Tr.S("Шаги 1–4 — на компьютере, 5–8 — на телевизоре. Зелёная галочка — шаг уже сделан.",
                                        "Steps 1–4 are on the PC, 5–8 on the TV. A green tick means the step is done."), 9.5F, FontStyle.Regular, "subtle");
            sub.Location = new Point(22, 50);
            RoundButton pdf = TvUi.Button(Tr.S("Инструкция (PDF)", "Guide (PDF)"), false, 160);
            pdf.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            pdf.Location = new Point(head.Width - 20 - 160, 16);
            pdf.Click += delegate
            {
                if (!TvHost.OpenGuide())
                    MessageBox.Show(this, Tr.S("Не удалось открыть инструкцию: нет программы для PDF?", "Could not open the guide: no PDF viewer?"), Text);
            };
            head.Controls.Add(title);
            head.Controls.Add(sub);
            head.Controls.Add(pdf);
            head.Resize += delegate { pdf.Left = head.ClientSize.Width - Px(20) - pdf.Width; };
            return head;
        }

        private void BuildSteps()
        {
            // ---- компьютер ----
            _sFolders = AddStep(Tr.S("Компьютер: выберите папки с видео", "PC: pick your video folders"),
                Tr.S("Каждая папка станет отдельным разделом на телевизоре, вложенные папки тоже откроются. Файлы никуда не копируются.",
                     "Each folder becomes a section on the TV; subfolders open too. Nothing is copied."));
            _btnAdd = TvUi.Button(Tr.S("+ Добавить папку…", "+ Add folder…"), true, 180);
            _btnAdd.Click += delegate { _owner.AddFolderDialog(); RefreshSteps(); };
            AddButtons(_sFolders, _btnAdd);

            _sPower = AddStep(Tr.S("Компьютер: включите трансляцию", "PC: start streaming"),
                Tr.S("Пока трансляция включена, телевизор видит папки. Окно программы можно закрыть — она продолжит работать в трее.",
                     "While streaming is on, the TV sees the folders. You can close the window — the app keeps running in the tray."));
            _btnPower = TvUi.Button(Tr.S("▶  Включить трансляцию", "▶  Start streaming"), true, 220);
            _btnPower.Click += delegate { if (!TvHost.Running) _owner.TogglePower(); RefreshSteps(); };
            AddButtons(_sPower, _btnPower);

            _sFirewall = AddStep(Tr.S("Компьютер: разрешите доступ в брандмауэре Windows", "PC: allow access in Windows Firewall"),
                Tr.S("Без этого Windows не пустит телевизор к компьютеру. Кнопка спросит права администратора один раз и откроет порт только "
                     + "для устройств вашей домашней сети. Если раньше в окне Windows нажали «Отмена», кнопка снимет и этот запрет.",
                     "Without this Windows keeps the TV out. The button asks for administrator rights once and opens the port only to devices "
                     + "on your home network. If «Cancel» was pressed in the Windows prompt earlier, the button lifts that block too."));
            _btnFw = TvUi.Button(Tr.S("Разрешить в брандмауэре", "Allow in firewall"), true, 220);
            _btnFw.Click += delegate { TvForm.FixFirewall(this); RefreshSteps(); };
            RoundButton recheckFw = TvUi.Button(Tr.S("Проверить снова", "Check again"), false, 150);
            recheckFw.Click += delegate { TvHost.CheckFirewallAsync(); };
            AddButtons(_sFirewall, _btnFw, recheckFw);

            _sFfmpeg = AddStep(Tr.S("Компьютер: скачайте ffmpeg (для MKV, AVI и превью)", "PC: download ffmpeg (for MKV, AVI and previews)"),
                Tr.S("MP4 телевизор играет и без него. MKV, AVI и другие форматы ffmpeg перекодирует на лету и делает превью кадров. "
                     + "Кнопка скачает его (~115 МБ, проверка по SHA-256) в папку программы — ничего устанавливать не нужно.",
                     "The TV plays MP4 without it. ffmpeg transcodes MKV, AVI and other formats on the fly and makes frame previews. "
                     + "The button downloads it (~115 MB, SHA-256 checked) into the app folder — nothing to install."));
            _btnFf = TvUi.Button(Tr.S("Скачать ffmpeg", "Download ffmpeg"), true, 180);
            _btnFf.Click += delegate { TvForm.ToggleFfmpegDownload(); RefreshSteps(); };
            AddButtons(_sFfmpeg, _btnFf);

            // ---- телевизор ----
            Step msxApp = AddStep(Tr.S("Телевизор: установите Media Station X (один раз)", "TV: install Media Station X (once)"),
                Tr.S("1. Подключите телевизор к той же домашней сети, что и компьютер (тот же роутер; гостевой Wi-Fi не подойдёт).\n"
                     + "2. Нажмите на пульте кнопку VIDAA (или «Домой» ⌂) и откройте магазин приложений — App Store.\n"
                     + "3. В поиске наберите  Media Station X  — это бесплатный плеер, в списке он так и называется (коротко — MSX).\n"
                     + "4. Нажмите «Установить» (Install), затем «Открыть» (Open).",
                     "1. Connect the TV to the same home network as the PC (same router; a guest Wi-Fi will not do).\n"
                     + "2. Press VIDAA (or Home ⌂) on the remote and open the App Store.\n"
                     + "3. Search for  Media Station X  — a free player listed under exactly that name (MSX for short).\n"
                     + "4. Press Install, then Open."));
            msxApp.Status.Text = Tr.S("на телевизоре", "on the TV");
            msxApp.Status.Tag = "subtle";

            _sMsx = AddStep(Tr.S("Телевизор: впишите адрес компьютера в Media Station X (один раз)", "TV: enter the PC address in Media Station X (once)"),
                Tr.S("1. В Media Station X откройте меню слева и выберите  Settings  (значок шестерёнки).\n"
                     + "2. Выберите  Start Parameter, затем  Setup.\n"
                     + "3. Экранной клавиатурой наберите ровно это — цифры, точки и двоеточие, без http:// и без пробелов:",
                     "1. In Media Station X open the left menu and choose  Settings  (the gear icon).\n"
                     + "2. Choose  Start Parameter, then  Setup.\n"
                     + "3. With the on-screen keyboard type exactly this — digits, dots and the colon, no http:// and no spaces:"));
            _lblMsxAddr = BigAddress(_sMsx);
            _sMsx.Body.Add(Text2(Tr.S("4. Нажмите OK (Done). На вопрос, применить ли стартовый параметр, ответьте  Yes / OK.\n"
                                      + "5. Media Station X перезапустится, и слева появятся ваши папки. Статус этого шага станет зелёным, "
                                      + "как только телевизор достучится до компьютера.",
                                      "4. Press OK (Done). When asked whether to apply the start parameter, answer  Yes / OK.\n"
                                      + "5. Media Station X restarts and your folders appear on the left. This step turns green "
                                      + "as soon as the TV reaches the PC.")));

            _sWatch = AddStep(Tr.S("Телевизор: смотрите", "TV: watch"),
                Tr.S("Стрелками выберите папку или видео и нажмите OK. Пауза — OK, перемотка — стрелки ← / →, назад — кнопка Back (↩). "
                     + "В следующий раз просто откройте Media Station X — адрес вводить больше не нужно.",
                     "Pick a folder or video with the arrows and press OK. Pause — OK, seek — ← / →, back — the Back (↩) button. "
                     + "Next time just open Media Station X — no need to enter the address again."));

            _sBrowser = AddStep(Tr.S("Запасной путь: браузер телевизора", "Fallback: the TV browser"),
                Tr.S("Если Media Station X не ставится, откройте на телевизоре приложение «Браузер» (Browser) и наберите в адресной строке:",
                     "If Media Station X will not install, open the Browser app on the TV and type in the address bar:"));
            _lblBrowserAddr = BigAddress(_sBrowser);
            _sBrowser.Body.Add(Text2(Tr.S("Откроется та же библиотека: разделы, превью, «Продолжить просмотр». Управление — стрелками и OK.",
                                          "The same library opens: sections, previews, «Continue watching». Use the arrows and OK.")));

            Step help = AddStep(Tr.S("Если не получается", "If it does not work"),
                Tr.S("• Компьютер и телевизор должны быть подключены к одному роутеру. В гостевой сети устройства не видят друг друга.\n"
                     + "• Адрес изменился? Роутер выдал компьютеру новый IP — здесь уже показан новый, повторите шаг 6. "
                     + "Чтобы адрес не менялся, закрепите IP за компьютером в настройках роутера.\n"
                     + "• Программа должна работать: окно можно закрыть, но не выходить через «Выход» в меню значка в трее.\n"
                     + "• Подробнее, с ответами на частые вопросы, — кнопка «Инструкция (PDF)» вверху.",
                     "• The PC and the TV must be on the same router. Devices on a guest network cannot see each other.\n"
                     + "• Address changed? The router gave the PC a new IP — the new one is shown here, repeat step 6. "
                     + "To keep it fixed, reserve the PC's IP in the router settings.\n"
                     + "• The app must keep running: you may close the window, but do not use «Exit» in the tray icon menu.\n"
                     + "• More detail and common questions — the «Guide (PDF)» button at the top."));
            help.Num.Text = "?";
            foreach (Step st in _steps) foreach (Control c in st.Body) st.Card.Controls.Add(c);
        }

        private Step AddStep(string title, string text)
        {
            Step s = new Step();
            s.Card = new TvCard();
            s.Card.Tag = "card";
            s.Num = TvUi.Label((_steps.Count + 1).ToString(), 13F, FontStyle.Bold, "accent");
            s.Num.AutoSize = false;
            s.Num.TextAlign = ContentAlignment.MiddleCenter;
            s.Title = TvUi.Label(title, 11.5F, FontStyle.Bold, "");
            s.Status = TvUi.Label("", 9.5F, FontStyle.Bold, "subtle");
            s.Body.Add(Text2(text));
            s.Card.Controls.Add(s.Num);
            s.Card.Controls.Add(s.Title);
            s.Card.Controls.Add(s.Status);
            _body.Controls.Add(s.Card);
            _steps.Add(s);
            return s;
        }

        private static Label Text2(string text) { return TvUi.Label(text, 10F, FontStyle.Regular, ""); }

        private Label BigAddress(Step s)
        {
            FlowLayoutPanel row = TvUi.Row();
            row.AutoSize = true;
            row.WrapContents = true;
            Label addr = TvUi.Label("", 20F, FontStyle.Bold, "accent");
            addr.Font = new Font("Consolas", 20F, FontStyle.Bold);
            addr.Margin = new Padding(0, 2, 14, 2);
            RoundButton copy = TvUi.Button(Tr.S("Копировать", "Copy"), false, 120);
            copy.Margin = new Padding(0, 6, 0, 0);
            copy.Click += delegate
            {
                try { Clipboard.SetText(addr.Text); copy.Text = Tr.S("Скопировано ✓", "Copied ✓"); }
                catch (Exception) { }
            };
            row.Controls.Add(addr);
            row.Controls.Add(copy);
            s.Body.Add(row);
            return addr;
        }

        private static void AddButtons(Step s, params RoundButton[] buttons)
        {
            FlowLayoutPanel row = TvUi.Row();
            row.AutoSize = true;
            row.WrapContents = true;
            foreach (RoundButton b in buttons) row.Controls.Add(b);
            s.Body.Add(row);
        }

        // Ручная раскладка сверху вниз: высота карточки — по переносу текста при текущей ширине окна.
        private void LayoutSteps()
        {
            if (_steps.Count == 0) return;
            int scrollY = _body.AutoScrollPosition.Y;
            int side = Px(20), pad = Px(16), numW = Px(30), gap = Px(12);
            int w = _body.ClientSize.Width - side * 2;
            if (w < Px(300)) return;
            int inner = w - pad * 2 - numW - Px(10);
            int y = Px(4);
            _body.SuspendLayout();
            foreach (Step s in _steps)
            {
                int x = pad + numW + Px(10);
                s.Num.SetBounds(pad, pad - Px(2), numW, numW);
                s.Title.MaximumSize = new Size(inner, 0);
                s.Title.Location = new Point(x, pad);
                int cy = s.Title.Bottom + Px(2);
                if (s.Status.Text.Length > 0)
                {
                    s.Status.MaximumSize = new Size(inner, 0);
                    s.Status.Location = new Point(x, cy);
                    s.Status.Visible = true;
                    cy = s.Status.Bottom + Px(6);
                }
                else { s.Status.Visible = false; cy += Px(4); }
                foreach (Control c in s.Body)
                {
                    if (!c.Visible) continue;
                    c.MaximumSize = new Size(inner, 0);
                    c.Location = new Point(x - (c is FlowLayoutPanel ? 0 : 1), cy);
                    cy = c.Bottom + Px(6);
                }
                s.Card.SetBounds(side, y + scrollY, w, cy + pad - Px(6));
                y += s.Card.Height + gap;
            }
            _body.ResumeLayout();
        }

        // ================= состояние =================
        public void RefreshSteps()
        {
            if (IsDisposed) return;
            TvSettings set = TvHost.Snapshot();
            bool on = TvHost.Running;
            string addr = TvHost.Address;

            int n = set.Folders.Count;
            Mark(_sFolders, n > 0, n > 0 ? "✓ " + Tr.S("Папок: ", "Folders: ") + n + " — " + string.Join(", ", NamesOf(set)) : Tr.S("Папок пока нет", "No folders yet"));
            _btnAdd.Tag = n > 0 ? "button" : "primary";

            Mark(_sPower, on, on ? Tr.S("✓ Трансляция включена", "✓ Streaming is on")
                               : n == 0 ? Tr.S("Сначала добавьте папку (шаг 1)", "Add a folder first (step 1)") : Tr.S("Трансляция выключена", "Streaming is off"));
            _btnPower.Visible = !on;

            TvFirewallState fw = TvHost.Firewall;
            string net = fw != null && fw.Known && !string.IsNullOrEmpty(fw.Network)
                ? Tr.S("  ·  сеть «", "  ·  network «") + fw.Network + "» — " + CategoryName(fw.Category) : "";
            if (fw == null) Mark(_sFirewall, null, Tr.S("Проверяю…", "Checking…"));
            else if (!fw.Known) Mark(_sFirewall, null, Tr.S("Проверить не удалось — нажмите кнопку на всякий случай", "Could not check — press the button to be safe"));
            else if (fw.Blocked) Mark(_sFirewall, false, Tr.S("✗ Windows запрещает телевизору подключаться (раньше нажали «Отмена»)", "✗ Windows blocks the TV (Cancel was pressed earlier)") + net);
            else if (fw.Allowed) Mark(_sFirewall, true, Tr.S("✓ Доступ разрешён", "✓ Access allowed") + net);
            else Mark(_sFirewall, false, Tr.S("✗ Доступ пока не разрешён", "✗ Access not allowed yet") + net);
            _btnFw.Tag = fw != null && fw.Known && fw.Allowed ? "button" : "primary";

            bool ff = TvHost.Ffmpeg.Available;
            Mark(_sFfmpeg, TvHost.FfmpegBusy || TvHost.FfmpegError != null && !ff ? (bool?)null : ff,
                 (ff && !TvHost.FfmpegBusy ? "✓ " : "") + TvForm.FfmpegLine());
            if (TvHost.FfmpegError != null) { _sFfmpeg.Status.Tag = "error"; PaintStatus(_sFfmpeg); }
            _btnFf.Visible = !ff || TvHost.FfmpegBusy;
            _btnFf.Text = TvHost.FfmpegBusy ? Tr.S("Отменить загрузку", "Cancel download") : Tr.S("Скачать ffmpeg", "Download ffmpeg");

            _lblMsxAddr.Text = addr;
            _lblBrowserAddr.Text = "http://" + addr;
            TvSession visit = TvHost.Server != null ? TvHost.Server.LastVisit() : null;
            if (visit != null && visit.Mode == "msx")
                Mark(_sMsx, true, Tr.S("✓ Media Station X подключился: ", "✓ Media Station X connected: ") + visit.Device + ", " + visit.Remote + Tr.S(", в ", ", at ") + visit.LastSeen.ToString("HH:mm"));
            else Mark(_sMsx, null, on ? Tr.S("Телевизор к компьютеру пока не обращался", "The TV has not reached the PC yet")
                                      : Tr.S("Сначала включите трансляцию (шаг 2)", "Start streaming first (step 2)"));

            List<TvSession> ses = TvHost.Server != null ? TvHost.Server.Sessions() : new List<TvSession>();
            if (ses.Count > 0) Mark(_sWatch, true, "✓ " + Tr.S("Сейчас смотрят: ", "Watching now: ") + ses[0].Device + " — " + System.IO.Path.GetFileName(ses[0].File));
            else Mark(_sWatch, null, "");

            if (visit != null && visit.Mode == "browser")
                Mark(_sBrowser, true, Tr.S("✓ Браузер подключался: ", "✓ A browser connected: ") + visit.Device + ", " + visit.Remote);
            else Mark(_sBrowser, null, "");

            TvUi.Recolor(_btnAdd.Parent, _t);   // кнопка становится главной (акцентной), пока шаг не сделан
            TvUi.Recolor(_btnFw.Parent, _t);
            LayoutSteps();
        }

        private static List<string> NamesOf(TvSettings s)
        {
            List<string> names = new List<string>();
            foreach (TvFolder f in s.Folders) { if (names.Count == 3) { names.Add("…"); break; } names.Add(f.Name); }
            return names;
        }

        private static string CategoryName(string c)
        {
            if (c == "Private") return Tr.S("частная", "private");
            if (c == "DomainAuthenticated") return Tr.S("доменная", "domain");
            return Tr.S("общедоступная", "public");
        }

        // done: true — зелёная галочка, false — предупреждение, null — нейтрально.
        private void Mark(Step s, bool? done, string text)
        {
            s.Status.Text = text;
            s.Status.Tag = done == true ? "ok" : done == false ? "warn" : "subtle";
            s.Num.Text = done == true ? "✓" : (_steps.IndexOf(s) + 1).ToString();
            s.Num.Tag = done == true ? "ok" : "accent";
            PaintStatus(s);
        }

        private void PaintStatus(Step s)
        {
            string role = s.Status.Tag as string;
            s.Status.ForeColor = role == "ok" ? TvUi.Ok(_t) : role == "warn" ? TvUi.Warn(_t) : role == "error" ? TvUi.Error(_t) : _t.Subtle;
            s.Num.ForeColor = (s.Num.Tag as string) == "ok" ? TvUi.Ok(_t) : _t.Accent;
        }

        public void ApplyTheme(Theme t)
        {
            _t = t;
            BackColor = t.Bg;
            ForeColor = t.Text;
            TvUi.Recolor(this, t);
            Invalidate(true);
        }
    }
}
