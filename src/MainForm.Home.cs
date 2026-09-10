// Windows Process Cleaner — вкладка «Главная»: карточки состояния, «Ускорить», проверка состояния, умное ускорение
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    // Карточка «Главной»: заголовок, крупное значение, подпись и полоса заполнения. Рисует себя сама —
    // цвета темы ей выставляет форма (ApplyHomeTheme), пиксельные размеры умножаются на Dpi.
    public class HomeCard : Panel
    {
        public string Title = "", Value = "", Sub = "";
        public double Fraction = -1;     // -1 = без полосы
        public bool Warn;
        public Color Surface, Line, TextColor, Subtle, Accent, Track, WarnColor;
        public float Dpi = 1f;

        public HomeCard()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Max(1, radius * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private int S(int v) { return (int)Math.Round(v * Dpi); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Color parentBg = Parent != null ? Parent.BackColor : BackColor;
            using (SolidBrush b = new SolidBrush(parentBg)) g.FillRectangle(b, ClientRectangle);
            Rectangle r = ClientRectangle;
            r.Width -= 1; r.Height -= 1;
            if (r.Width < 8 || r.Height < 8 || Surface.IsEmpty) return;

            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath gp = Rounded(r, S(10)))
            {
                using (SolidBrush b = new SolidBrush(Surface)) g.FillPath(b, gp);
                using (Pen p = new Pen(Line)) g.DrawPath(p, gp);
            }
            g.SmoothingMode = SmoothingMode.Default;

            int pad = S(14);
            int x = r.X + pad, w = r.Width - pad * 2;
            TextFormatFlags f = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine;
            using (Font ft = new Font(Font.FontFamily, 9.5F))
            using (Font fv = new Font(Font.FontFamily, 19F, FontStyle.Bold))
            {
                int hT = TextRenderer.MeasureText("Xg", ft).Height;
                int hV = TextRenderer.MeasureText("Xg", fv).Height;
                int y = r.Y + pad - S(2);
                TextRenderer.DrawText(g, Title, ft, new Rectangle(x, y, w, hT), Subtle, f);
                y += hT + S(2);
                TextRenderer.DrawText(g, Value, fv, new Rectangle(x, y, w, hV), Warn ? WarnColor : TextColor, f);
                y += hV;
                TextRenderer.DrawText(g, Sub, ft, new Rectangle(x, y, w, hT), Subtle, f);
            }
            if (Fraction >= 0)
            {
                int bh = S(7);
                Rectangle track = new Rectangle(x, r.Bottom - pad - bh, w, bh);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath gp = Rounded(track, bh / 2))
                using (SolidBrush b = new SolidBrush(Track)) g.FillPath(b, gp);
                int fw = (int)Math.Round(w * Math.Max(0, Math.Min(1, Fraction)));
                if (fw >= bh)
                {
                    Rectangle fill = new Rectangle(track.X, track.Y, fw, bh);
                    using (GraphicsPath gp = Rounded(fill, bh / 2))
                    using (SolidBrush b = new SolidBrush(Warn ? WarnColor : Accent)) g.FillPath(b, gp);
                }
                g.SmoothingMode = SmoothingMode.Default;
            }
        }
    }

    public partial class MainForm
    {
        private Panel _homeHead, _boostBox;
        private HomeCard _cardRam, _cardDisk, _cardStatus;
        private RoundButton _btnBoost;
        private CheckBox _chkBoostTemp, _chkSmartHome;
        private Label _lblBoostResult, _lblHealthInfo, _lblNavAdmin;
        private ListView _lvHealth;
        private Button _btnHealthRun, _btnHealthAct, _btnHomeStop;
        private List<HealthItem> _health;
        private int _healthBusy, _boostBusy, _smartBusy;
        private volatile bool _healthCancel;
        private volatile bool _boostCancel;
        private int _homeDrivesBusy;                 // фоновое чтение томов для карточки «Системный диск»
        private DateTime _lastHealthOpen;        // защита от повторов при удержании Enter на строке
        // Секундомер «Главной»: и проверка состояния, и «Ускорить» обходят мусор минутами,
        // а на экране висела одна и та же строка без признаков жизни.
        private System.Windows.Forms.Timer _homeTick;
        private DateTime _healthStarted, _boostStarted;
        // volatile: этап пишет рабочий поток (движок зовёт колбэк оттуда), читает таймер UI
        private volatile string _healthPhase, _boostPhase;
        private System.Windows.Forms.Timer _homeTimer;
        private SystemSnapshot _snap;
        private DateTime _homeDrivesAt = DateTime.MinValue, _healthAt = DateTime.MinValue;
        private DateTime _lastSmartBoost = DateTime.MinValue;
        private bool _suppressSmart;

        // ---------- Вкладка: Главная ----------
        private Control BuildHomeTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            _homeHead = new Panel();
            _homeHead.Dock = DockStyle.Top;
            _homeHead.Height = 122;

            _cardRam = new HomeCard(); _cardRam.Title = Tr.S("Оперативная память", "Memory");
            _cardDisk = new HomeCard(); _cardDisk.Title = Tr.S("Системный диск", "System drive");
            _cardStatus = new HomeCard(); _cardStatus.Title = Tr.S("Состояние", "Status"); _cardStatus.Fraction = -1;
            _cardStatus.Value = "—";
            _cardStatus.Sub = Tr.S("проверка ещё не выполнялась", "not checked yet");

            _boostBox = new Panel();
            _boostBox.Width = 262; _boostBox.Height = 122;
            _btnBoost = (RoundButton)MkButton(Tr.S("⚡ Ускорить", "⚡ Boost"), 0, 0, 250, true);
            _btnBoost.Height = 46;
            _btnBoost.Font = new Font(Font.FontFamily, 12.5F, FontStyle.Bold);
            _btnBoost.Click += delegate { DoBoost(); };
            _chkBoostTemp = new CheckBox();
            _chkBoostTemp.Text = Tr.S("и временные файлы (спросит)", "and temp files (asks first)");
            _chkBoostTemp.AutoSize = true;
            _chkBoostTemp.Left = 2; _chkBoostTemp.Top = 54;
            _chkBoostTemp.Checked = MemBool("home", "boostTemp", false, true);
            _chkBoostTemp.CheckedChanged += delegate { MemSetBool("home", "boostTemp", _chkBoostTemp.Checked, true); };
            _chkSmartHome = new CheckBox();
            _chkSmartHome.Text = Tr.S("умное ускорение при RAM ≥ 90 %", "smart boost at RAM ≥ 90 %");
            _chkSmartHome.AutoSize = true;
            _chkSmartHome.Left = 2; _chkSmartHome.Top = 82;
            _chkSmartHome.CheckedChanged += delegate
            {
                if (_suppressSmart) return;
                _engine.Config.SmartBoostEnabled = _chkSmartHome.Checked;
                _engine.SaveConfig();
                if (_chkSmartBoost != null) _chkSmartBoost.Checked = _chkSmartHome.Checked;
            };
            _boostBox.Controls.Add(_btnBoost);
            _boostBox.Controls.Add(_chkBoostTemp);
            _boostBox.Controls.Add(_chkSmartHome);

            _homeHead.Controls.Add(_cardRam);
            _homeHead.Controls.Add(_cardDisk);
            _homeHead.Controls.Add(_cardStatus);
            _homeHead.Controls.Add(_boostBox);
            _homeHead.Resize += delegate { LayoutHomeHead(); };

            _lblBoostResult = MkNote(Tr.S("«Ускорить» завершает заброшенные процессы и очищает Standby Memory; временные файлы — только с галочкой и после подтверждения со списком.",
                                          "“Boost” terminates abandoned processes and purges Standby Memory; temp files only with the checkbox and after a confirmation with the list."), true);

            FlowLayoutPanel top = MkToolbar();
            _btnHealthRun = MkFlowButton(Tr.S("Проверить состояние", "Check health"), 190, true);
            _btnHealthRun.Click += delegate { RunHealthCheck(); };
            _btnHealthAct = MkFlowButton(Tr.S("Выполнить действие", "Run the action"), 180, false);
            _btnHealthAct.Click += delegate { HealthActSelected(); };
            _btnHomeStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnHomeStop.Enabled = false;
            _btnHomeStop.Click += delegate { CancelHomeWork(); };
            Label hint = MkFlowLabel(Tr.S("двойной щелчок по строке — выполнить её действие", "double-click a row to run its action"), true);
            top.Controls.Add(_btnHealthRun);
            top.Controls.Add(_btnHealthAct);
            top.Controls.Add(_btnHomeStop);
            top.Controls.Add(hint);

            _lblHealthInfo = MkNote(Tr.S("Проверка состояния запустится при открытии окна", "The health check runs when the window opens"), false);

            _lvHealth = new FastListView();
            _lvHealth.Dock = DockStyle.Fill;
            _lvHealth.View = View.Details;
            _lvHealth.FullRowSelect = true;
            _lvHealth.MultiSelect = false;
            _lvHealth.Columns.Add(Tr.S("Статус", "Status"), 120);
            _lvHealth.Columns.Add(Tr.S("Проверка", "Check"), 220);
            _lvHealth.Columns.Add(Tr.S("Подробности", "Details"), 520);
            _lvHealth.Columns.Add(Tr.S("Действие", "Action"), 240);
            SetupOwnerDraw(_lvHealth);
            _flexColumn[_lvHealth] = 2;
            _lvHealth.DoubleClick += delegate { HealthActSelected(); };
            _lvHealth.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { HealthActSelected(); e.Handled = true; }
            };

            tab.Controls.Add(_lvHealth);
            tab.Controls.Add(_lblHealthInfo);
            tab.Controls.Add(top);
            tab.Controls.Add(_lblBoostResult);
            tab.Controls.Add(_homeHead);

            _homeTimer = new System.Windows.Forms.Timer();
            _homeTimer.Interval = 2000;
            _homeTimer.Tick += delegate { UpdateHomeCards(); };
            return tab;
        }

        // Три карточки делят ширину поровну, блок «Ускорить» фиксированной ширины справа. Все размеры
        // берутся у контролов (их масштабирует форма), поэтому раскладка верна при любом DPI.
        private void LayoutHomeHead()
        {
            if (_homeHead == null || _boostBox == null) return;
            int w = _homeHead.ClientSize.Width, h = _homeHead.ClientSize.Height;
            int bw = _boostBox.Width;
            int gap = Math.Max(6, h / 12);
            int cardW = Math.Max(120, (w - bw - gap * 3) / 3);
            HomeCard[] cards = { _cardRam, _cardDisk, _cardStatus };
            for (int i = 0; i < cards.Length; i++)
                cards[i].SetBounds(i * (cardW + gap), 0, cardW, h);
            _boostBox.SetBounds(w - bw, 0, bw, h);
        }

        private void ApplyHomeTheme()
        {
            Color warn = _theme.Dark ? Color.FromArgb(245, 158, 11) : Color.FromArgb(217, 119, 6);
            foreach (HomeCard c in new HomeCard[] { _cardRam, _cardDisk, _cardStatus })
            {
                if (c == null) continue;
                c.Surface = _theme.Surface; c.Line = _theme.Border; c.TextColor = _theme.Text; c.Subtle = _theme.Subtle;
                c.Accent = _theme.Accent; c.Track = _theme.Header; c.WarnColor = warn; c.Dpi = _dpiScale;
                c.Invalidate();
            }
            if (_lblNavAdmin != null) _lblNavAdmin.ForeColor = _theme.Subtle;
            // цвет статуса не из палитры — после смены темы выставляется заново
            if (_lvHealth != null)
                foreach (ListViewItem it in _lvHealth.Items)
                {
                    HealthItem h = it.Tag as HealthItem;
                    if (h != null) it.SubItems[0].ForeColor = HealthLevelColor(h.Level);
                }
        }

        // ---------- Карточки ----------
        private void HomeEnter()
        {
            UpdateHomeCards();
            if (_homeTimer != null && !_homeTimer.Enabled) _homeTimer.Start();
            // первая проверка — при первом показе «Главной» (в т.ч. из трея после старта свёрнутым);
            // повторная сама — если прошлой больше часа: карточка «Состояние» не должна врать
            if (Visible && _healthBusy == 0 && (_health == null || (DateTime.Now - _healthAt).TotalMinutes >= 60)) RunHealthCheck();
        }

        private void HomeLeave()
        {
            if (_homeTimer != null) _homeTimer.Stop();
            // Проверку состояния догонять некому: страницы не видно, а обход мусора и опрос
            // PowerShell продолжали грузить диск ещё минуты. Уходим — останавливаем.
            if (_healthBusy != 0) { _healthCancel = true; UpdateHomeStopButton(); }
        }

        private void UpdateHomeCards()
        {
            if (_closing || _cardRam == null) return;
            // Snapshot(false) — только счётчики памяти и аптайм. Раз в 30 секунд здесь же
            // читались тома: IsReady/VolumeLabel/TotalSize будят спящий HDD и упираются в
            // заблокированный BitLocker, то есть подвешивали цикл сообщений на секунды.
            SystemSnapshot s = Engine.Snapshot(false);
            if (_snap != null) { s.Drives = _snap.Drives; s.SystemDrive = _snap.SystemDrive; }
            _snap = s;

            _cardRam.Value = s.MemoryLoad + " %";
            _cardRam.Sub = Tr.S("свободно ", "free ") + Engine.FormatBytes(s.MemAvail) + Tr.S(" из ", " of ") + Engine.FormatBytes(s.MemTotal);
            _cardRam.Fraction = s.MemoryLoad / 100.0;
            _cardRam.Warn = s.MemoryLoad >= 85;
            _cardRam.Invalidate();

            UpdateDiskCard();
            RefreshHomeDrives();
            UpdateStatusCard();
        }

        private void UpdateDiskCard()
        {
            if (_cardDisk == null) return;
            DriveRow d = _snap != null ? _snap.SystemDrive : null;
            if (d != null)
            {
                _cardDisk.Title = Tr.S("Системный диск ", "System drive ") + d.Name.TrimEnd('\\');
                _cardDisk.Value = (int)Math.Round(d.UsedFraction * 100) + " %";
                _cardDisk.Sub = Tr.S("свободно ", "free ") + Engine.FormatBytes(d.Free) + Tr.S(" из ", " of ") + Engine.FormatBytes(d.Total);
                _cardDisk.Fraction = d.UsedFraction;
                _cardDisk.Warn = d.Total > 0 && (double)d.Free / d.Total < 0.10;
            }
            else
            {
                _cardDisk.Value = "—";
                // пустая карточка без объяснения читалась как поломка
                _cardDisk.Sub = _homeDrivesBusy != 0 ? Tr.S("читаю диски…", "reading drives…") : Tr.S("диски не прочитаны", "drives not read");
                _cardDisk.Fraction = -1;
            }
            _cardDisk.Invalidate();
        }

        // Перечисление томов ушло в фоновый поток; карточка получает готовые значения.
        private void RefreshHomeDrives()
        {
            if (_snap != null && (DateTime.Now - _homeDrivesAt).TotalSeconds <= 30) return;
            if (Interlocked.CompareExchange(ref _homeDrivesBusy, 1, 0) != 0) return;
            Thread t = new Thread(delegate()
            {
                SystemSnapshot d = null;
                try { d = Engine.Snapshot(true); }
                catch { }
                SystemSnapshot copy = d;
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _homeDrivesBusy, 0);
                    _homeDrivesAt = DateTime.Now;
                    if (copy != null && _snap != null) { _snap.Drives = copy.Drives; _snap.SystemDrive = copy.SystemDrive; }
                    UpdateDiskCard();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Ни проверка состояния, ни «Ускорить» останавливаться не умели: _healthCancel только
        // сбрасывался, а обход мусора внутри обеих слушает общий дисковый флаг движка.
        private void CancelHomeWork()
        {
            if (_healthBusy == 0 && _boostBusy == 0) return;
            _healthCancel = true;
            _boostCancel = true;
            // Флаг общий с вкладкой очистки, поэтому поднимаем его только пока работает сама
            // «Главная» — иначе оборвали бы чужой анализ, запущенный на другой странице.
            _engine.CancelDiskWork();
            UpdateHomeStopButton();
        }

        private void UpdateHomeStopButton()
        {
            if (_btnHomeStop == null) return;
            _btnHomeStop.Enabled = (_healthBusy != 0 && !_healthCancel) || (_boostBusy != 0 && !_boostCancel);
        }

        private void StartHomeTicker()
        {
            if (_homeTick == null)
            {
                _homeTick = new System.Windows.Forms.Timer();
                _homeTick.Interval = 500;
                _homeTick.Tick += delegate { HomeTick(); };
            }
            _homeTick.Start();
            HomeTick();
        }

        // Одна строка на обе работы: у каждой своя подпись и свой секундомер, живой этап берём
        // из DiskStatus движка — там видно, какую папку он сейчас считает или чистит.
        private void HomeTick()
        {
            if (_closing) return;
            if (_healthBusy == 0 && _boostBusy == 0)
            {
                if (_homeTick != null) _homeTick.Stop();
                return;
            }
            string st = _engine.DiskStatus;
            string live = string.IsNullOrEmpty(st) ? "" : "   ·   " + st;
            string stopping = Tr.S("   ·   останавливаюсь…", "   ·   stopping…");
            if (_healthBusy != 0)
                _lblHealthInfo.Text = _healthPhase + "   ·   " + Elapsed(DateTime.UtcNow - _healthStarted) + live + (_healthCancel ? stopping : "");
            if (_boostBusy != 0)
                _lblBoostResult.Text = _boostPhase + "   ·   " + Elapsed(DateTime.UtcNow - _boostStarted) + live + (_boostCancel ? stopping : "");
        }

        private void UpdateStatusCard()
        {
            if (_cardStatus == null) return;
            if (_health == null)
            {
                _cardStatus.Value = "—";
                _cardStatus.Warn = false;
                _cardStatus.Sub = _snap != null ? Tr.S("без перезагрузки ", "uptime ") + Engine.FormatUptime(_snap.Uptime) : "";
            }
            else
            {
                int warn = 0, info = 0;
                foreach (HealthItem h in _health) { if (h.Level == HealthLevel.Warn) warn++; else if (h.Level == HealthLevel.Info) info++; }
                bool running = _healthBusy != 0;
                _cardStatus.Warn = warn > 0;
                _cardStatus.Value = warn > 0 ? Tr.S("Внимание: ", "Attention: ") + warn : running ? Tr.S("Проверяю…", "Checking…") : Tr.S("Хорошо", "Good");
                _cardStatus.Sub = (running ? Tr.S("проверка идёт", "check running") : Tr.S("проверено в ", "checked at ") + _healthAt.ToString("HH:mm"))
                                + Tr.S("  ·  советов: ", "  ·  tips: ") + info;
            }
            _cardStatus.Invalidate();
        }

        // ---------- Проверка состояния ----------
        private void RunHealthCheck()
        {
            if (Interlocked.CompareExchange(ref _healthBusy, 1, 0) != 0)
            {
                _lblHealthInfo.Text = Tr.S("Проверка уже идёт — дождитесь её или нажмите «Остановить».",
                                           "A check is already running — wait for it or click “Stop”.");
                return;
            }
            _healthCancel = false;
            _health = new List<HealthItem>();
            _lvHealth.Items.Clear();
            _healthPhase = Tr.S("Проверяю (Защитник, обновления и точки восстановления — последними)",
                                "Checking (Defender, updates and restore points come last)");
            _healthStarted = DateTime.UtcNow;
            _btnHealthRun.Enabled = false;
            UpdateHomeStopButton();
            StartHomeTicker();
            UpdateStatusCard();
            Thread t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    _engine.HealthCheck(delegate(HealthItem h) { UiPost(delegate { AddHealthRow(h); }); },
                                        delegate { return _healthCancel || _closing; });
                }
                catch (Exception ex) { err = ex.Message; }
                string errCopy = err;
                bool stopped = _healthCancel;
                UiPost(delegate
                {
                    // флаг снимаем внутри UiPost: снятый в потоке, он пускал новую проверку,
                    // которой этот же обработчик тут же затирал строку итога
                    Interlocked.Exchange(ref _healthBusy, 0);
                    UpdateHomeStopButton();
                    AddHealthRow(HealthUpdatesItem());
                    _healthAt = DateTime.Now;
                    int warn = 0, info = 0;
                    foreach (HealthItem h in _health) { if (h.Level == HealthLevel.Warn) warn++; else if (h.Level == HealthLevel.Info) info++; }
                    _lblHealthInfo.Text = (stopped ? Tr.S("Остановлено, проверено частично: ", "Stopped, partially checked: ") : Tr.S("Проверено: ", "Checked: "))
                        + _health.Count
                        + Tr.S("   ·   требуют внимания: ", "   ·   need attention: ") + warn
                        + Tr.S("   ·   советов: ", "   ·   tips: ") + info
                        + Tr.S("   ·   в порядке: ", "   ·   fine: ") + (_health.Count - warn - info)
                        + Tr.S("   ·   заняло ", "   ·   took ") + Elapsed(DateTime.UtcNow - _healthStarted)
                        + (errCopy != null ? Tr.S("   ·   ошибка: ", "   ·   error: ") + errCopy : "");
                    _btnHealthRun.Enabled = true;
                    UpdateStatusCard();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Обновления программ проверяются на своей вкладке (winget/choco — десятки секунд), здесь только
        // результат последней проверки, если она была.
        private HealthItem HealthUpdatesItem()
        {
            HealthItem h = new HealthItem();
            h.Id = "updates"; h.Title = Tr.S("Обновления программ", "Program updates");
            h.Action = Tr.S("Открыть «Обновления»", "Open “Updates”"); h.ActionKind = "page:Updates";
            if (_updates == null)
            {
                h.Level = HealthLevel.Info;
                h.Detail = Tr.S("в этом сеансе не проверялись — вкладка «Обновления» опрашивает winget и Chocolatey",
                                "not checked in this session — the “Updates” tab queries winget and Chocolatey");
                return h;
            }
            int n = 0;
            foreach (UpdateItem u in _updates) if (!u.Duplicate) n++;
            if (n == 0) { h.Level = HealthLevel.Ok; h.Detail = Tr.S("по последней проверке всё актуально", "everything was current at the last check"); h.Action = null; h.ActionKind = null; }
            else { h.Level = HealthLevel.Info; h.Detail = Tr.S("доступно ", "available: ") + Tr.N(n, "обновление", "обновления", "обновлений", "update", "updates"); }
            return h;
        }

        private Color HealthLevelColor(int level)
        {
            if (level == HealthLevel.Warn) return _theme.Dark ? Color.FromArgb(245, 158, 11) : Color.FromArgb(217, 119, 6);
            if (level == HealthLevel.Info) return _theme.Accent;
            return _theme.Dark ? Color.FromArgb(74, 222, 128) : Color.FromArgb(22, 163, 74);
        }

        private static string HealthLevelText(int level)
        {
            if (level == HealthLevel.Warn) return Tr.S("⚠ Внимание", "⚠ Attention");
            if (level == HealthLevel.Info) return Tr.S("ℹ Совет", "ℹ Tip");
            return Tr.S("✔ В порядке", "✔ Fine");
        }

        private void AddHealthRow(HealthItem h)
        {
            if (_closing || h == null) return;
            _health.Add(h);
            ListViewItem it = new ListViewItem(HealthLevelText(h.Level));
            it.SubItems.Add(h.Title);
            it.SubItems.Add(h.Detail ?? "");
            it.SubItems.Add(h.Action ?? "");
            it.ToolTipText = h.Title + "\r\n" + h.Detail;
            it.Tag = h;
            it.ForeColor = _theme.Text;
            it.BackColor = h.Level == HealthLevel.Warn ? _theme.CandidateBg : _theme.Surface;
            // статус — своим цветом (зелёный / акцент / оранжевый), остальные ячейки — цветами строки
            it.UseItemStyleForSubItems = false;
            for (int i = 1; i < it.SubItems.Count; i++) { it.SubItems[i].ForeColor = it.ForeColor; it.SubItems[i].BackColor = it.BackColor; }
            it.SubItems[0].ForeColor = HealthLevelColor(h.Level);
            _lvHealth.Items.Add(it);
            AutoFillLastColumnDeferred(_lvHealth);
            UpdateStatusCard();
        }

        private void HealthActSelected()
        {
            if (_lvHealth.SelectedItems.Count == 0)
            {
                _lblHealthInfo.Text = Tr.S("Выберите строку с действием.", "Select a row that has an action.");
                return;
            }
            HealthItem h = _lvHealth.SelectedItems[0].Tag as HealthItem;
            if (h == null) return;
            if (h.ActionKind == null)
            {
                _lblHealthInfo.Text = h.Title + Tr.S(": действий не требуется.", ": nothing to do.");
                return;
            }
            HealthAction(h);
        }

        private void HealthAction(HealthItem h)
        {
            string k = h.ActionKind;
            if (k == "boost") { DoBoost(); return; }
            if (k.StartsWith("page:"))
            {
                switch (k.Substring(5))
                {
                    case "Disk": ShowPage(PageDisk); break;
                    case "Clean": ShowPage(PageClean); break;
                    case "Startup": ShowPage(PageStartup); break;
                    case "Updates": ShowPage(PageUpdates); break;
                    case "Tools": ShowPage(PageTools); break;
                    case "Scan": ShowPage(PageScan); break;
                }
                return;
            }
            if (k.StartsWith("disk:"))
            {
                string path = k.Substring(5);
                ShowPage(PageDisk);
                bool exists = false;
                try { exists = Directory.Exists(path); }
                catch { }
                if (exists) { FillDiskScopes(path); DoDiskScan(); }
                // раньше здесь не было ветки else: переехавшая или удалённая папка означала
                // переход на «Диск» и полную тишину — щелчок выглядел ничего не сделавшим
                else _lblHealthInfo.Text = Tr.S("Папка не найдена: ", "Folder not found: ") + path;
                return;
            }
            if (k.StartsWith("tool:"))
            {
                ShowPage(PageTools);
                RunToolById(k.Substring(5));
                return;
            }
            if (k.StartsWith("open:"))
            {
                // Двойной щелчок и удержанный Enter повторяют действие: без задержки каждое
                // повторение поднимало ещё одну копию окна.
                if ((DateTime.UtcNow - _lastHealthOpen).TotalMilliseconds < 1000) return;
                _lastHealthOpen = DateTime.UtcNow;
                string target = k.Substring(5);
                _lblHealthInfo.Text = Tr.S("Открываю: ", "Opening: ") + target + "…";
                // ShellExecute на UI-потоке подвешивал окно на время холодного старта цели
                Thread t = new Thread(delegate()
                {
                    string err = null;
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(target);
                        psi.UseShellExecute = true;
                        Process.Start(psi);
                    }
                    catch (Exception ex) { err = ex.Message; }
                    string errCopy = err;
                    UiPost(delegate
                    {
                        if (errCopy == null) _lblHealthInfo.Text = Tr.S("Открыто: ", "Opened: ") + target;
                        else
                        {
                            _lblHealthInfo.Text = Tr.S("Не удалось открыть ", "Could not open ") + target + ": " + errCopy;
                            MsgError(errCopy);
                        }
                    });
                });
                t.IsBackground = true;
                t.Start();
            }
        }

        // ---------- Ускорить ----------
        // Заброшенные процессы + Standby Memory — то же, что делает автоочистка по таймеру. Временные
        // файлы — только по галочке, и всё равно через подтверждение с размерами: файлы менее обратимы.
        private void DoBoost()
        {
            if (Interlocked.CompareExchange(ref _boostBusy, 1, 0) != 0)
            {
                _lblBoostResult.Text = Tr.S("Ускорение уже идёт — дождитесь его или нажмите «Остановить».",
                                            "A boost is already running — wait for it or click “Stop”.");
                return;
            }
            _boostCancel = false;
            bool temp = _chkBoostTemp != null && _chkBoostTemp.Checked;
            string was = _btnBoost.Text;
            _btnBoost.Enabled = false;
            _btnBoost.Text = Tr.S("Ускоряю…", "Boosting…");
            _boostPhase = Tr.S("Завершаю заброшенные процессы и очищаю Standby Memory", "Terminating abandoned processes and purging Standby Memory");
            _boostStarted = DateTime.UtcNow;
            UpdateHomeStopButton();
            StartHomeTicker();
            Thread t = new Thread(delegate()
            {
                int killed = 0; long freedProc = 0, freedMem = 0, freedDisk = 0; int files = 0;
                string memNote = null, err = null;
                bool tempSkipped = false;
                List<string> names = new List<string>();
                try
                {
                    List<int> pids = new List<int>();
                    foreach (ProcInfo p in _engine.Scan(_engine.Config.GlobalScan))
                        if (p.IsCandidate) { pids.Add(p.Pid); names.Add(p.Name + " (pid " + p.Pid + ")"); }
                    if (pids.Count > 0)
                        killed = _engine.TerminateMany(pids, out freedProc,
                            delegate(string s) { _boostPhase = Tr.S("Завершаю процессы: ", "Terminating processes: ") + s; },
                            delegate { return _boostCancel || _closing; });
                    Engine.MemResult mr = PurgeStandbyMaybeElevated(true,
                        delegate(string s) { _boostPhase = s; },
                        delegate { return _boostCancel || _closing; });
                    freedMem = mr.FreedBytes;
                    if (!mr.Ok) memNote = mr.Message;

                    if (temp && !_boostCancel)
                    {
                        UiPost(delegate { _boostPhase = Tr.S("Считаю временные файлы", "Measuring temporary files"); });
                        CleanCategory sys = null;
                        foreach (CleanCategory c in _engine.BuildCleanCategories()) if (c.Id == "sys") { sys = c; break; }
                        if (sys != null)
                        {
                            // не сбрасывать чужой «Стоп»: флаг общий с вкладкой очистки
                            _engine.TryResetDiskCancel();
                            _engine.AnalyzeCategory(sys);
                            if (_boostCancel) tempSkipped = true;
                            else if (sys.Size > 0)
                            {
                                bool yes = false;
                                string q = Tr.S("Удалить временные файлы?\r\n\r\n", "Delete temporary files?\r\n\r\n")
                                    + sys.Title + ": " + Engine.FormatBytes(sys.Size) + ", " + Tr.N(sys.FileCount, "файл", "файла", "файлов", "file", "files")
                                    + (sys.BinEnabled && sys.BinSize > 0 ? Tr.S("\r\n  в том числе Корзина: ", "\r\n  including the Recycle Bin: ") + Engine.FormatBytes(sys.BinSize) : "")
                                    + Tr.S("\r\n\r\nУдаляется то же, что категория «Системный мусор» на вкладке «Очистка диска», с учётом ваших исключений. Файлы удаляются безвозвратно.",
                                           "\r\n\r\nSame as the “System junk” category on the “Disk Cleanup” tab, honouring your exclusions. Files are deleted permanently.");
                                if (!_closing && IsHandleCreated)
                                    Invoke((MethodInvoker)delegate { yes = MsgAsk(q, Tr.S("Ускорить", "Boost")); });
                                if (yes)
                                {
                                    string op = Tr.S("удаление временных файлов", "temporary files deletion");
                                    // подпись оставалась «Считаю временные файлы…» всё удаление,
                                    // то есть врала как раз в самой необратимой фазе
                                    UiPost(delegate { _boostPhase = Tr.S("Удаляю временные файлы", "Deleting temporary files"); });
                                    BeginWrite(op);
                                    try
                                    {
                                        List<CleanCategory> one = new List<CleanCategory>(); one.Add(sys);
                                        CleanResult res = _engine.CleanCategories(one);
                                        freedDisk = res.Freed; files = res.FilesDeleted;
                                    }
                                    finally { EndWrite(op); }
                                }
                                else tempSkipped = true;
                            }
                        }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                try { if (killed > 0 || freedMem > 0) SaveHistory(killed, freedProc + freedMem, names); } catch { }

                bool stopped = _boostCancel;
                UiPost(delegate
                {
                    // флаг снимаем здесь, а не в потоке: снятый раньше, он пускал второе
                    // ускорение, которому этот же обработчик тут же возвращал кнопку и подпись
                    Interlocked.Exchange(ref _boostBusy, 0);
                    UpdateHomeStopButton();
                    string msg = (stopped ? Tr.S("Остановлено: процессов завершено ", "Stopped: processes terminated ")
                                          : Tr.S("Готово: процессов завершено ", "Done: processes terminated ")) + killed
                               + Tr.S("  ·  памяти освобождено ~", "  ·  memory freed ~") + Engine.FormatBytes(freedProc + freedMem);
                    if (temp && !tempSkipped) msg += Tr.S("  ·  файлов удалено ", "  ·  files deleted ") + files + " (" + Engine.FormatBytes(freedDisk) + ")";
                    if (tempSkipped) msg += stopped ? Tr.S("  ·  временные файлы не тронуты: остановлено", "  ·  temp files untouched: stopped")
                                                    : Tr.S("  ·  временные файлы пропущены", "  ·  temp files skipped");
                    if (memNote != null) msg += "  ·  " + memNote;
                    if (err != null) msg += Tr.S("  ·  ошибка: ", "  ·  error: ") + err;
                    msg += Tr.S("  ·  заняло ", "  ·  took ") + Elapsed(DateTime.UtcNow - _boostStarted);
                    _lblBoostResult.Text = msg;
                    _btnBoost.Text = was;
                    _btnBoost.Enabled = true;
                    if (_tray != null) _tray.ShowBalloonTip(3000, stopped ? Tr.S("Ускорение остановлено", "Boost stopped") : Tr.S("Ускорение выполнено", "Boost done"), msg, ToolTipIcon.Info);
                    UpdateHomeCards();
                    RefreshHistory();
                    UpdateTrayState();
                    // после остановки не запускаем следом проверку состояния: пользователь
                    // только что попросил перестать грузить диск
                    if (_health != null && Visible && !stopped) RunHealthCheck();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- Умное ускорение ----------
        // Вызывается из таймера расписания (каждые 30 с, UI-поток): при занятости RAM выше порога чистит
        // Standby Memory в фоне, не чаще раза в 15 минут — иначе на постоянно загруженной машине
        // уведомления шли бы потоком, а толку от повторной очистки нет.
        private void SmartBoostTick()
        {
            AppConfig c = _engine.Config;
            if (!c.SmartBoostEnabled || _closing) return;
            if ((DateTime.Now - _lastSmartBoost).TotalMinutes < 15) return;
            SystemSnapshot s = Engine.Snapshot(false);
            if (s.MemoryLoad < c.SmartBoostPercent) return;
            if (Interlocked.CompareExchange(ref _smartBusy, 1, 0) != 0) return;
            _lastSmartBoost = DateTime.Now;
            int load = s.MemoryLoad;
            Thread t = new Thread(delegate()
            {
                Engine.MemResult mr = null;
                // Умное ускорение срабатывает само, по порогу RAM: запрос прав здесь запрещён.
                // Без прав (окно запущено обычным пользователем) операция пропускается с
                // объяснением — а запущенное задачей автозапуска окно уже элевировано, и
                // ускорение работает как раньше, молча.
                try { mr = PurgeStandbyMaybeElevated(false, null, null); } catch { }
                Interlocked.Exchange(ref _smartBusy, 0);
                Engine.MemResult res = mr;
                UiPost(delegate
                {
                    if (res == null) return;
                    string msg = Tr.S("RAM была занята на ", "RAM was at ") + load + " %  ·  " + res.Message
                               + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(res.FreedBytes);
                    if (_lblBoostResult != null) _lblBoostResult.Text = Tr.S("Умное ускорение: ", "Smart boost: ") + msg;
                    if (_tray != null) _tray.ShowBalloonTip(3000, Tr.S("Умное ускорение", "Smart boost"), msg, res.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                    UpdateHomeCards();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SyncSmartHome()
        {
            if (_chkSmartHome == null) return;
            _suppressSmart = true;
            _chkSmartHome.Checked = _engine.Config.SmartBoostEnabled;
            _chkSmartHome.Text = Tr.S("умное ускорение при RAM ≥ ", "smart boost at RAM ≥ ") + _engine.Config.SmartBoostPercent + " %";
            _suppressSmart = false;
        }
    }
}
