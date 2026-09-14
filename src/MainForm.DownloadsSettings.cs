// Windows Process Cleaner — вкладка «Загрузки»: настройки загрузок (папки и правила, скорость, очередь, когда качать, безопасность).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Настройки принадлежат процессу загрузок: пока он работает, изменение уходит ему по каналу и он применяет его сразу.
// Пока его нет — файл пишется напрямую (с той же проверкой папок), чтобы щелчок по галочке не запускал очередь.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private Panel _dlSetView, _dlSetScroll;
        private FlowLayoutPanel _dlSetBody;
        private Label _lblDlSetFolder, _lblDlSetInfo, _lblDlSetAgent;
        private FastListView _lvDlRules;
        private RoundComboBox _cmbDlSetMode, _cmbDlNightFrom, _cmbDlNightTo, _cmbDlSchedFrom, _cmbDlSchedTo;
        private NumericUpDown _numDlLimit, _numDlQuiet, _numDlNight, _numDlSegments, _numDlPerServer, _numDlActive, _numDlPerHost,
                              _numDlSmall, _numDlRetries, _numDlRetryMax, _numDlIdleMin, _numDlIdleCpu;
        private CheckBox _chkDlAskFolder;
        private FastListView _lvDlPlaces;
        private CheckBox _chkDlNight, _chkDlSched, _chkDlIdle, _chkDlIdleGame, _chkDlMetered, _chkDlBattery, _chkDlSleep, _chkDlMotw,
                         _chkDlDefender, _chkDlResume, _chkDlNotifyDone, _chkDlNotifyErr;
        private readonly CheckBox[] _chkDlDays = new CheckBox[7];
        private TextBox _txtDlUserAgent;
        private System.Windows.Forms.Timer _dlSetSave;
        private DlSettings _dlSetShown;
        private bool _dlSetLoading, _dlSetDirty;
        private static readonly object DlSetLock = new object();

        private Control BuildDownloadsSettingsView()
        {
            _dlSetView = new Panel();
            _dlSetView.Dock = DockStyle.Fill;
            _dlSetView.Visible = false;

            _dlSetScroll = new Panel();
            _dlSetScroll.Dock = DockStyle.Fill;
            _dlSetScroll.AutoScroll = true;
            _dlSetView.Controls.Add(_dlSetScroll);
            _dlSetBody = new FlowLayoutPanel();
            _dlSetBody.Location = new Point(0, 0);
            _dlSetBody.AutoSize = true;
            _dlSetBody.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _dlSetBody.FlowDirection = FlowDirection.TopDown;
            _dlSetBody.WrapContents = false;
            _dlSetScroll.Controls.Add(_dlSetBody);

            FlowLayoutPanel top = MkToolbar();
            Button back = MkFlowButton(Tr.S("← К списку загрузок", "← Back to downloads"), 190, true);
            back.Click += delegate { DlShowSettings(false); };
            top.Controls.Add(back);
            _lblDlSetInfo = MkFlowLabel("", true);
            top.Controls.Add(_lblDlSetInfo);
            _dlSetView.Controls.Add(top);

            DlSetSection(Tr.S("Куда сохранять", "Where to save"));
            _lblDlSetFolder = DlSetNote("", false);
            FlowLayoutPanel folderBar = DlSetRow();
            Button browse = MkFlowButton(Tr.S("Выбрать папку…", "Choose folder…"), 150, false);
            browse.Click += delegate { DlPickDefaultFolder(); };
            Button open = MkFlowButton(Tr.S("Открыть папку", "Open folder"), 140, false);
            open.Click += delegate
            {
                string dir = _dlSetShown != null ? _dlSetShown.EffectiveFolder : DlPaths.DefaultFolder;
                if (Native.IsDirectoryPath(dir)) OpenInExplorer(dir, false);
                else DlSetInfo(Tr.S("Папки нет: ", "The folder does not exist: ") + dir);
            };
            Button def = MkFlowButton(Tr.S("По умолчанию", "Default"), 130, false);
            def.Click += delegate { DlChangeSettings(delegate(DlSettings s) { s.Folder = ""; }, Tr.S("Папка — «Загрузки» Windows.", "Folder — the Windows Downloads folder.")); };
            folderBar.Controls.AddRange(new Control[] { browse, open, def });

            _chkDlAskFolder = DlSetCheck(Tr.S("Спрашивать папку у каждой загрузки из браузера",
                                              "Ask where to save every download that comes from a browser"));
            DlSetNote(Tr.S("Вопрос показывается до первого байта: пока папка не выбрана, загрузка стоит. В окне вопроса — места из списка ниже; "
                           + "галочка «Запомнить эту папку» добавляет выбранное место в этот список. Список общий: он не привязан ни к сайту, ни к типу файла.",
                           "The question comes before the first byte: until a folder is picked the download waits. The question lists the places below; "
                           + "the “Remember this folder” box adds the chosen place to that list. The list is shared: it is tied to neither site nor file type."), true);
            _lvDlPlaces = new FastListView();
            _lvDlPlaces.FullRowSelect = true;
            _lvDlPlaces.MultiSelect = false;
            _lvDlPlaces.HideSelection = false;
            _lvDlPlaces.Size = new Size(760, 110);
            _lvDlPlaces.Margin = new Padding(1, 2, 1, 8);
            _lvDlPlaces.Columns.Add(Tr.S("Запомненные места", "Remembered places"), 750);
            SetupOwnerDraw(_lvDlPlaces);
            _pathColumns[_lvDlPlaces] = 0;
            _dlSetBody.Controls.Add(_lvDlPlaces);
            FlowLayoutPanel placeBar = DlSetRow();
            Button addPlace = MkFlowButton(Tr.S("+ Запомнить папку…", "+ Remember a folder…"), 190, false);
            addPlace.Click += delegate { DlAddPlace(); };
            Button delPlace = MkFlowButton(Tr.S("Забыть", "Forget"), 110, false);
            delPlace.Click += delegate { DlForgetPlace(); };
            placeBar.Controls.AddRange(new Control[] { addPlace, delPlace });

            DlSetNote(Tr.S("Правила выбирают папку по сайту или по расширению файла. Сначала проверяются правила по сайту, потом по расширению, "
                           + "в каждой группе — сверху вниз. «example.com» — только этот сайт, «*.example.com» — он и все поддомены. "
                           + "Расширения через запятую: .iso, .img.",
                           "Rules pick a folder by site or by file extension. Site rules are checked first, then extension rules, each "
                           + "group top to bottom. “example.com” is that site only, “*.example.com” is it and all subdomains. "
                           + "Extensions are comma-separated: .iso, .img."), true);
            _lvDlRules = new FastListView();
            _lvDlRules.FullRowSelect = true;
            _lvDlRules.MultiSelect = false;
            _lvDlRules.HideSelection = false;
            _lvDlRules.Size = new Size(760, 130);
            _lvDlRules.Margin = new Padding(1, 2, 1, 8);
            _lvDlRules.Columns.Add(Tr.S("По", "By"), 110);
            _lvDlRules.Columns.Add(Tr.S("Что", "What"), 220);
            _lvDlRules.Columns.Add(Tr.S("Папка", "Folder"), 420);
            SetupOwnerDraw(_lvDlRules);
            _pathColumns[_lvDlRules] = 2;
            _dlSetBody.Controls.Add(_lvDlRules);
            FlowLayoutPanel ruleBar = DlSetRow();
            Button addExt = MkFlowButton(Tr.S("+ По расширению…", "+ By extension…"), 170, false);
            addExt.Click += delegate { DlAddRule("ext"); };
            Button addSite = MkFlowButton(Tr.S("+ По сайту…", "+ By site…"), 140, false);
            addSite.Click += delegate { DlAddRule("site"); };
            Button delRule = MkFlowButton(Tr.S("Удалить правило", "Remove rule"), 150, false);
            delRule.Click += delegate { DlRemoveRule(); };
            Button upRule = MkFlowButton(Tr.S("Выше", "Up"), 80, false);
            upRule.Click += delegate { DlMoveRule(-1); };
            ruleBar.Controls.AddRange(new Control[] { addExt, addSite, delRule, upRule });

            DlSetSection(Tr.S("Скорость", "Speed"));
            FlowLayoutPanel modeRow = DlSetRow();
            _cmbDlSetMode = DlSetCombo(modeRow, Tr.S("Режим:", "Mode:"), 230, Tr.S("обычная", "normal"), Tr.S("тихая — не мешать сети", "quiet — spare the network"),
                                       Tr.S("без ограничений", "unlimited"));
            FlowLayoutPanel limitRow = DlSetRow();
            _numDlLimit = DlSetNumber(limitRow, Tr.S("Общий лимит в обычном режиме, КБ/с:", "Overall limit in normal mode, KB/s:"), 0, 10 * 1024 * 1024, 110);
            _numDlQuiet = DlSetNumber(limitRow, Tr.S("В тихом, КБ/с:", "In quiet mode, KB/s:"), 1, 10 * 1024 * 1024, 110);
            DlSetNote(Tr.S("0 — без ограничения. 1024 КБ/с = 1 МБ/с. У отдельной загрузки свой лимит — в её меню.",
                           "0 means no limit. 1024 KB/s = 1 MB/s. A single download has its own limit in its menu."), true);
            _chkDlNight = DlSetCheck(Tr.S("Ночью другой лимит", "A different limit at night"));
            FlowLayoutPanel nightRow = DlSetRow();
            _cmbDlNightFrom = DlSetTimeCombo(nightRow, Tr.S("с", "from"));
            _cmbDlNightTo = DlSetTimeCombo(nightRow, Tr.S("до", "to"));
            _numDlNight = DlSetNumber(nightRow, Tr.S("лимит, КБ/с:", "limit, KB/s:"), 0, 10 * 1024 * 1024, 110);

            DlSetSection(Tr.S("Очередь и соединения", "Queue and connections"));
            FlowLayoutPanel qRow = DlSetRow();
            _numDlActive = DlSetNumber(qRow, Tr.S("Одновременно загрузок:", "Downloads at once:"), 1, 20, 70);
            _numDlPerHost = DlSetNumber(qRow, Tr.S("С одного сайта:", "From one site:"), 1, 10, 70);
            FlowLayoutPanel cRow = DlSetRow();
            _numDlSegments = DlSetNumber(cRow, Tr.S("Потоков на файл:", "Streams per file:"), 1, 16, 70);
            _numDlPerServer = DlSetNumber(cRow, Tr.S("Соединений на сервер:", "Connections per server:"), 1, 16, 70);
            _numDlSmall = DlSetNumber(cRow, Tr.S("Вне очереди до, МБ:", "Skip the queue up to, MB:"), 0, 1024, 80);
            DlSetNote(Tr.S("Файлы меньше порога начинают качаться сразу, не дожидаясь места в очереди (лимит сайта соблюдается); 0 — выключено. "
                           + "Много потоков помогают на медленных серверах, но часть сайтов режет или блокирует такие загрузки.",
                           "Files below the threshold start at once without waiting for a queue slot (the per-site limit still applies); 0 turns it off. "
                           + "More streams help with slow servers, but some sites throttle or block such downloads."), true);
            FlowLayoutPanel rRow = DlSetRow();
            _numDlRetries = DlSetNumber(rRow, Tr.S("Повторов после сбоя:", "Retries after a failure:"), 0, 100, 70);
            _numDlRetryMax = DlSetNumber(rRow, Tr.S("Пауза между повторами до, с:", "Wait between retries up to, s:"), 1, 3600, 80);

            DlSetSection(Tr.S("Когда качать", "When to download"));
            _chkDlSched = DlSetCheck(Tr.S("Только по расписанию", "Only on a schedule"));
            FlowLayoutPanel days = DlSetRow();
            string[] dayNames = { Tr.S("Пн", "Mon"), Tr.S("Вт", "Tue"), Tr.S("Ср", "Wed"), Tr.S("Чт", "Thu"), Tr.S("Пт", "Fri"), Tr.S("Сб", "Sat"), Tr.S("Вс", "Sun") };
            for (int i = 0; i < 7; i++)
            {
                CheckBox c = new CheckBox();
                c.Text = dayNames[i];
                c.AutoSize = true;
                c.Margin = new Padding(0, 6, 14, 4);
                c.CheckedChanged += delegate { DlSetChanged(); };
                days.Controls.Add(c);
                _chkDlDays[i] = c;
            }
            _cmbDlSchedFrom = DlSetTimeCombo(days, Tr.S("с", "from"));
            _cmbDlSchedTo = DlSetTimeCombo(days, Tr.S("до", "to"));
            DlSetNote(Tr.S("«с» и «до» одинаковые — весь день; окно может переходить через полночь.", "Equal “from” and “to” mean the whole day; the window may cross midnight."), true);
            _chkDlIdle = DlSetCheck(Tr.S("Вся очередь — только когда компьютер простаивает", "The whole queue — only while the computer is idle"));
            FlowLayoutPanel idleRow = DlSetRow();
            _numDlIdleMin = DlSetNumber(idleRow, Tr.S("Простой — без ввода, мин:", "Idle — no input for, min:"), 1, 240, 70);
            _numDlIdleCpu = DlSetNumber(idleRow, Tr.S("и загрузка ЦП ниже, %:", "and CPU load below, %:"), 1, 100, 70);
            _chkDlIdleGame = DlSetCheck(Tr.S("Не считать простоем полноэкранную игру или фильм", "A fullscreen game or movie is not idle"));
            _chkDlMetered = DlSetCheck(Tr.S("Пауза на лимитном подключении (мобильный интернет)", "Pause on a metered connection (mobile internet)"));
            _chkDlBattery = DlSetCheck(Tr.S("Пауза при работе от батареи", "Pause on battery power"));
            _chkDlSleep = DlSetCheck(Tr.S("Не давать компьютеру уснуть, пока идёт загрузка", "Keep the computer awake while downloading"));

            DlSetSection(Tr.S("Безопасность", "Safety"));
            _chkDlMotw = DlSetCheck(Tr.S("Помечать скачанное как «из интернета» (Windows спросит перед запуском программы)",
                                         "Mark downloads as “from the internet” (Windows asks before running a program)"));
            _chkDlDefender = DlSetCheck(Tr.S("Проверять готовый файл Защитником Windows", "Scan the finished file with Windows Defender"));
            FlowLayoutPanel uaRow = DlSetRow();
            uaRow.Controls.Add(MkFlowLabel(Tr.S("User-Agent:", "User-Agent:"), false));
            _txtDlUserAgent = DlSetText(uaRow, 520);
            DlSetNote(Tr.S("Пусто — стандартный. Ссылки только http и https; cookies браузера хранятся лишь в памяти процесса и на диск не пишутся.",
                           "Empty means the default one. Only http and https links; browser cookies live only in the process memory and are never written to disk."), true);

            BuildDownloadsBrowsersSection();
            BuildDownloadsMediaSection();
            BuildDownloadsTorrentSection();

            DlSetSection(Tr.S("Фоновый процесс и уведомления", "Background process and notifications"));
            _lblDlSetAgent = DlSetNote("", false);
            _chkDlResume = DlSetCheck(Tr.S("Продолжать незавершённые загрузки после входа в Windows", "Continue unfinished downloads after signing in to Windows"));
            _chkDlNotifyDone = DlSetCheck(Tr.S("Уведомлять о готовой загрузке", "Notify when a download finishes"));
            _chkDlNotifyErr = DlSetCheck(Tr.S("Уведомлять об ошибке и истёкшей ссылке", "Notify about errors and expired links"));
            DlSetNote(Tr.S("Процесс запускается первым действием на этой странице и закрывается сам через минуту, когда качать нечего и страница "
                           + "не открыта. В автозапуске он только пока есть незавершённые загрузки.",
                           "The process starts with your first action on this page and exits by itself a minute after there is nothing to download "
                           + "and the page is closed. It stays in startup only while unfinished downloads exist."), true);
            FlowLayoutPanel agentBar = DlSetRow();
            Button stop = MkFlowButton(Tr.S("Остановить процесс", "Stop the process"), 170, false);
            stop.Click += delegate { DlStopAgent(); };
            Button dataDir = MkFlowButton(Tr.S("Папка данных", "Data folder"), 150, false);
            dataDir.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(DlPaths.DataDir);
                    Process.Start("explorer.exe", "\"" + DlPaths.DataDir + "\"");
                }
                catch (Exception ex) { DlSetInfo(ex.Message); }
            };
            agentBar.Controls.AddRange(new Control[] { stop, dataDir });

            DlSetSection(Tr.S("История", "History"));
            DlSetNote(Tr.S("Убирает записи о готовых загрузках. Файлы на диске не трогаются.", "Removes records of finished downloads. Files on disk are not touched."), true);
            FlowLayoutPanel histBar = DlSetRow();
            Button h30 = MkFlowButton(Tr.S("Старше 30 дней", "Older than 30 days"), 160, false);
            h30.Click += delegate { DlClearHistory(30, false); };
            Button hGone = MkFlowButton(Tr.S("Чьих файлов нет", "Whose files are gone"), 170, false);
            hGone.Click += delegate { DlClearHistory(0, true); };
            Button hAll = MkFlowButton(Tr.S("Все готовые", "All finished"), 130, false);
            hAll.Click += delegate { DlClearHistory(0, false); };
            histBar.Controls.AddRange(new Control[] { h30, hGone, hAll });

            _dlSetSave = new System.Windows.Forms.Timer();
            _dlSetSave.Interval = 600;
            _dlSetSave.Tick += delegate { _dlSetSave.Stop(); DlSettingsFlush(); };
            _dlSetScroll.Resize += delegate { DlSetWrapNotes(); };
            return _dlSetView;
        }

        // ---------- строительные блоки (как у «Захвата») ----------

        private void DlSetSection(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            l.Margin = new Padding(0, _dlSetBody.Controls.Count == 0 ? 4 : 14, 0, 6);
            _dlSetBody.Controls.Add(l);
        }

        private Label DlSetNote(string text, bool muted)
        {
            Label l = DlLabel(text, muted);
            l.Margin = new Padding(0, 0, 0, 6);
            _dlSetBody.Controls.Add(l);
            return l;
        }

        private FlowLayoutPanel DlSetRow() { return DlFlowRow(_dlSetBody); }

        private CheckBox DlSetCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 2, 0, 4);
            c.CheckedChanged += delegate { DlSetChanged(); };
            _dlSetBody.Controls.Add(c);
            return c;
        }

        private RoundComboBox DlSetCombo(FlowLayoutPanel row, string label, int width, params string[] items)
        {
            row.Controls.Add(MkFlowLabel(label, false));
            RoundComboBox cb = DlCombo(row, width, items);
            cb.Margin = new Padding(0, 4, 24, 8);
            cb.SelectedIndexChanged += delegate { DlSetChanged(); };
            return cb;
        }

        // Время с шагом полчаса: индекс = минуты / 30.
        private RoundComboBox DlSetTimeCombo(FlowLayoutPanel row, string label)
        {
            string[] items = new string[48];
            for (int i = 0; i < 48; i++) items[i] = (i / 2).ToString("00") + ":" + (i % 2 == 0 ? "00" : "30");
            return DlSetCombo(row, label, 90, items);
        }

        private TextBox DlSetText(FlowLayoutPanel row, int width)
        {
            TextBox t = new TextBox();
            t.Width = width;
            t.Margin = new Padding(2, 9, 16, 8);
            t.TextChanged += delegate { DlSetChanged(); };
            row.Controls.Add(t);
            return t;
        }

        private NumericUpDown DlSetNumber(FlowLayoutPanel row, string label, int min, int max, int width)
        {
            row.Controls.Add(MkFlowLabel(label, false));
            NumericUpDown n = new NumericUpDown();
            n.Minimum = min;
            n.Maximum = max;
            n.Width = width;
            n.Margin = new Padding(0, 6, 24, 8);
            n.ValueChanged += delegate { DlSetChanged(); };
            row.Controls.Add(n);
            return n;
        }

        private void DlSetWrapNotes()
        {
            if (_dlSetScroll == null) return;
            int w = Math.Max(Px(300), _dlSetScroll.ClientSize.Width - Px(24));
            foreach (Control c in _dlSetBody.Controls)
                if (c is Label) c.MaximumSize = new Size(w, 0);
            _lvDlRules.Width = Math.Max(Px(400), Math.Min(w, Px(1000)));
            _lvDlBrowsers.Width = _lvDlRules.Width;
            _dlSetBody.PerformLayout();
            _dlSetScroll.PerformLayout();
        }

        private void DlSetInfo(string text)
        {
            if (_lblDlSetInfo != null) _lblDlSetInfo.Text = text ?? "";
        }

        // ---------- показ ----------

        private void DlShowSettings(bool on)
        {
            if (!on) DlSettingsFlush();
            _dlSetView.Visible = on;
            _dlMain.Visible = !on;
            if (on)
            {
                DlSetInfo("");
                DlSetWrapNotes();
                DlLoadSettingsView();
                AutoFillLastColumnDeferred(_lvDlRules);
                DlBrLoad();
                DlMdLoadTools();
                DlBtLoad();
            }
            else
            {
                DlLoadMode();
                AutoFillLastColumnDeferred(_lvDl);
            }
        }

        private void DlLoadSettingsView()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSettings s = DlReadSettings();
                bool live = DlIpc.IsRunning();
                UiPost(delegate { DlSettingsToUi(s, live); });
            });
        }

        private void DlSettingsToUi(DlSettings s, bool live)
        {
            if (_closing || _dlSetView == null) return;
            _dlSetShown = s;
            _dlSetLoading = true;
            try
            {
                _lblDlSetFolder.Text = Tr.S("Папка по умолчанию: ", "Default folder: ") + s.EffectiveFolder
                                       + (string.IsNullOrEmpty(s.Folder) ? Tr.S(" («Загрузки» Windows)", " (Windows Downloads)") : "");
                _lvDlRules.BeginUpdate();
                try
                {
                    _lvDlRules.Items.Clear();
                    foreach (DlFolderRule r in s.Rules)
                    {
                        ListViewItem it = new ListViewItem(r.Kind == "site" ? Tr.S("сайту", "site") : Tr.S("расширению", "extension"));
                        it.SubItems.Add(r.Pattern);
                        it.SubItems.Add(r.Folder);
                        _lvDlRules.Items.Add(it);
                    }
                }
                finally { _lvDlRules.EndUpdate(); }
                _chkDlAskFolder.Checked = s.AskFolder;
                _lvDlPlaces.BeginUpdate();
                try
                {
                    _lvDlPlaces.Items.Clear();
                    foreach (string place in s.RecentFolders) _lvDlPlaces.Items.Add(new ListViewItem(place));
                }
                finally { _lvDlPlaces.EndUpdate(); }
                _cmbDlSetMode.SelectedIndex = (int)s.Mode;
                DlSetNum(_numDlLimit, s.LimitKBps);
                DlSetNum(_numDlQuiet, s.QuietKBps);
                _chkDlNight.Checked = s.NightEnabled;
                _cmbDlNightFrom.SelectedIndex = Math.Min(47, s.NightFrom / 30);
                _cmbDlNightTo.SelectedIndex = Math.Min(47, s.NightTo / 30);
                DlSetNum(_numDlNight, s.NightKBps);
                DlSetNum(_numDlActive, s.MaxActive);
                DlSetNum(_numDlPerHost, s.MaxPerHost);
                DlSetNum(_numDlSegments, s.Segments);
                DlSetNum(_numDlPerServer, s.MaxPerServer);
                DlSetNum(_numDlSmall, s.SmallFileMB);
                DlSetNum(_numDlRetries, s.MaxRetries);
                DlSetNum(_numDlRetryMax, s.RetryMaxSeconds);
                _chkDlSched.Checked = s.ScheduleEnabled;
                for (int i = 0; i < 7; i++) _chkDlDays[i].Checked = (s.ScheduleDays & (1 << i)) != 0;
                _cmbDlSchedFrom.SelectedIndex = Math.Min(47, s.ScheduleFrom / 30);
                _cmbDlSchedTo.SelectedIndex = Math.Min(47, s.ScheduleTo / 30);
                _chkDlIdle.Checked = s.OnlyWhenIdle;
                DlSetNum(_numDlIdleMin, s.IdleMinutes);
                DlSetNum(_numDlIdleCpu, s.IdleCpuPercent);
                _chkDlIdleGame.Checked = s.IdleNoFullscreen;
                _chkDlMetered.Checked = s.PauseOnMetered;
                _chkDlBattery.Checked = s.PauseOnBattery;
                _chkDlSleep.Checked = s.PreventSleep;
                _chkDlMotw.Checked = s.MarkOfTheWeb;
                _chkDlDefender.Checked = s.DefenderScan;
                if (_txtDlUserAgent.Text != s.UserAgent) _txtDlUserAgent.Text = s.UserAgent;
                _chkDlResume.Checked = s.ResumeAtLogon;
                _chkDlNotifyDone.Checked = s.NotifyComplete;
                _chkDlNotifyErr.Checked = s.NotifyErrors;
                DlBrSettingsToUi(s);
                DlMdSettingsToUi(s);
                DlBtSettingsToUi(s);
                _lblDlSetAgent.Text = live
                    ? Tr.S("● Процесс загрузок работает — изменения применяются сразу.", "● The download process is running — changes apply at once.")
                    : Tr.S("○ Процесс загрузок не запущен — изменения записываются в файл настроек и подхватятся при запуске.",
                           "○ The download process is not running — changes go to the settings file and apply when it starts.");
                DlUpdateSettingsEnabled();
            }
            finally { _dlSetLoading = false; }
            AutoFillLastColumnDeferred(_lvDlRules);
        }

        private static void DlSetNum(NumericUpDown n, int value)
        {
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, value));
        }

        private void DlUpdateSettingsEnabled()
        {
            _cmbDlNightFrom.Enabled = _cmbDlNightTo.Enabled = _numDlNight.Enabled = _chkDlNight.Checked;
            _cmbDlSchedFrom.Enabled = _cmbDlSchedTo.Enabled = _chkDlSched.Checked;
            foreach (CheckBox c in _chkDlDays) c.Enabled = _chkDlSched.Checked;
            DlBtUpdateEnabled();
        }

        // ---------- сохранение ----------

        private void DlSetChanged()
        {
            if (_dlSetLoading || _closing || _dlSetSave == null) return;
            DlUpdateSettingsEnabled();
            _dlSetDirty = true;
            _dlSetSave.Stop();
            _dlSetSave.Start();
        }

        // Отложенное сохранение полей (без папок и правил — у них свои кнопки). Синхронно: вызывается и при уходе со страницы.
        private void DlSettingsFlush()
        {
            if (_dlSetSave != null) _dlSetSave.Stop();
            if (!_dlSetDirty) return;
            _dlSetDirty = false;
            DlSettings ui = new DlSettings();
            ui.Mode = (DlSpeedMode)Math.Max(0, _cmbDlSetMode.SelectedIndex);
            ui.LimitKBps = (int)_numDlLimit.Value;
            ui.QuietKBps = (int)_numDlQuiet.Value;
            ui.NightEnabled = _chkDlNight.Checked;
            ui.NightFrom = Math.Max(0, _cmbDlNightFrom.SelectedIndex) * 30;
            ui.NightTo = Math.Max(0, _cmbDlNightTo.SelectedIndex) * 30;
            ui.NightKBps = (int)_numDlNight.Value;
            ui.MaxActive = (int)_numDlActive.Value;
            ui.MaxPerHost = (int)_numDlPerHost.Value;
            ui.Segments = (int)_numDlSegments.Value;
            ui.MaxPerServer = (int)_numDlPerServer.Value;
            ui.SmallFileMB = (int)_numDlSmall.Value;
            ui.MaxRetries = (int)_numDlRetries.Value;
            ui.RetryMaxSeconds = (int)_numDlRetryMax.Value;
            ui.ScheduleEnabled = _chkDlSched.Checked;
            int days = 0;
            for (int i = 0; i < 7; i++) if (_chkDlDays[i].Checked) days |= 1 << i;
            ui.ScheduleDays = days;
            ui.ScheduleFrom = Math.Max(0, _cmbDlSchedFrom.SelectedIndex) * 30;
            ui.ScheduleTo = Math.Max(0, _cmbDlSchedTo.SelectedIndex) * 30;
            ui.OnlyWhenIdle = _chkDlIdle.Checked;
            ui.IdleMinutes = (int)_numDlIdleMin.Value;
            ui.IdleCpuPercent = (int)_numDlIdleCpu.Value;
            ui.IdleNoFullscreen = _chkDlIdleGame.Checked;
            ui.PauseOnMetered = _chkDlMetered.Checked;
            ui.PauseOnBattery = _chkDlBattery.Checked;
            ui.PreventSleep = _chkDlSleep.Checked;
            ui.MarkOfTheWeb = _chkDlMotw.Checked;
            ui.DefenderScan = _chkDlDefender.Checked;
            ui.UserAgent = _txtDlUserAgent.Text.Trim();
            ui.ResumeAtLogon = _chkDlResume.Checked;
            ui.NotifyComplete = _chkDlNotifyDone.Checked;
            ui.NotifyErrors = _chkDlNotifyErr.Checked;
            ui.AskFolder = _chkDlAskFolder.Checked;
            DlBrUiToSettings(ui);
            DlMdUiToSettings(ui);
            DlBtUiToSettings(ui);
            string error = DlApplySettings(delegate(DlSettings s) { DlCopyTunables(ui, s); });
            DlSetInfo(error == null ? Tr.S("Сохранено.", "Saved.") : Tr.S("Не сохранено: ", "Not saved: ") + error);
            if (_cmbDlMode != null && _cmbDlMode.SelectedIndex != (int)ui.Mode)
            {
                _dlModeLoading = true;
                try { _cmbDlMode.SelectedIndex = (int)ui.Mode; }
                finally { _dlModeLoading = false; }
            }
        }

        private static void DlCopyTunables(DlSettings from, DlSettings to)
        {
            to.Mode = from.Mode;
            to.LimitKBps = from.LimitKBps;
            to.QuietKBps = from.QuietKBps;
            to.NightEnabled = from.NightEnabled;
            to.NightFrom = from.NightFrom;
            to.NightTo = from.NightTo;
            to.NightKBps = from.NightKBps;
            to.MaxActive = from.MaxActive;
            to.MaxPerHost = from.MaxPerHost;
            to.Segments = from.Segments;
            to.MaxPerServer = from.MaxPerServer;
            to.SmallFileMB = from.SmallFileMB;
            to.MaxRetries = from.MaxRetries;
            to.RetryMaxSeconds = from.RetryMaxSeconds;
            to.ScheduleEnabled = from.ScheduleEnabled;
            to.ScheduleDays = from.ScheduleDays;
            to.ScheduleFrom = from.ScheduleFrom;
            to.ScheduleTo = from.ScheduleTo;
            to.OnlyWhenIdle = from.OnlyWhenIdle;
            to.IdleMinutes = from.IdleMinutes;
            to.IdleCpuPercent = from.IdleCpuPercent;
            to.IdleNoFullscreen = from.IdleNoFullscreen;
            to.PauseOnMetered = from.PauseOnMetered;
            to.PauseOnBattery = from.PauseOnBattery;
            to.PreventSleep = from.PreventSleep;
            to.MarkOfTheWeb = from.MarkOfTheWeb;
            to.DefenderScan = from.DefenderScan;
            to.UserAgent = from.UserAgent;
            to.ResumeAtLogon = from.ResumeAtLogon;
            to.NotifyComplete = from.NotifyComplete;
            to.NotifyErrors = from.NotifyErrors;
            to.AskFolder = from.AskFolder;
            DlBrCopyTunables(from, to);
            DlMdCopyTunables(from, to);
            DlBtCopyTunables(from, to);
        }

        // Изменение из кнопки: в фоне, потом перечитать вид. done — строка «что сделано».
        private void DlChangeSettings(Action<DlSettings> mutate, string done)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = DlApplySettings(mutate);
                UiPost(delegate
                {
                    string text = error == null ? done : Tr.S("Не сохранено: ", "Not saved: ") + error;
                    DlSetInfo(text);
                    DlInfo(text);
                    if (_dlSetView != null && _dlSetView.Visible) DlLoadSettingsView();
                });
            });
        }

        // null — сохранено. Прочитать → изменить → отдать процессу или записать файл; под замком, чтобы два сохранения не перетёрли друг друга.
        private static string DlApplySettings(Action<DlSettings> mutate)
        {
            lock (DlSetLock)
            {
                try
                {
                    if (DlIpc.IsRunning())
                    {
                        JVal answer = DlClient.Call(DlClient.Command("settings"), 1000);
                        DlSettings live = answer != null && DlJson.Bool(answer, "ok", false) ? DlSettings.FromJson(answer.Get("settings")) : null;
                        if (live != null)
                        {
                            mutate(live);
                            JVal req = DlClient.Command("setSettings");
                            req.Set("settings", live.ToJson());
                            JVal result = DlClient.Call(req, 2000);
                            if (result != null)
                                return DlJson.Bool(result, "ok", false) ? null : DlJson.Str(result, "error", Tr.S("процесс отказал", "the process refused"));
                        }
                    }
                    // Процесса нет (или он ушёл на полпути) — та же проверка папок, что делает он сам, и запись файла.
                    DlSettings s = DlSettings.Load();
                    mutate(s);
                    string why;
                    if (!string.IsNullOrEmpty(s.Folder) && DlFiles.CheckFolder(s.Folder, out why) == null) return why;
                    foreach (DlFolderRule rule in s.Rules)
                        if (DlFiles.CheckFolder(rule.Folder, out why) == null) return why;
                    if (!s.Save()) return Tr.S("файл настроек не записан — подробности в crash.log папки данных", "the settings file was not written — see crash.log in the data folder");
                    bool pending = false;
                    foreach (DlItem it in new DlStore(DlPaths.DataDir).LoadAll()) if (DlLauncher.IsPending(it)) pending = true;
                    DlLauncher.SyncAutostart(s, pending);
                    return null;
                }
                catch (Exception ex)
                {
                    DlLog.Report(ex);
                    return ex.Message;
                }
            }
        }

        // ---------- папки и правила ----------

        private void DlPickDefaultFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Куда сохранять загрузки", "Where to save downloads");
                dlg.ShowNewFolderButton = true;
                string current = _dlSetShown != null ? _dlSetShown.EffectiveFolder : DlPaths.DefaultFolder;
                try { if (Directory.Exists(current)) dlg.SelectedPath = current; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string picked = dlg.SelectedPath;
                DlChangeSettings(delegate(DlSettings s) { s.Folder = picked; }, Tr.S("Папка: ", "Folder: ") + picked);
            }
        }

        private void DlAddRule(string kind)
        {
            string pattern = DlPromptText(kind == "site" ? Tr.S("Правило по сайту", "Rule by site") : Tr.S("Правило по расширению", "Rule by extension"),
                                          kind == "site" ? Tr.S("Сайт: github.com — только он, *.github.com — он и поддомены:", "Site: github.com is that site only, *.github.com also covers subdomains:")
                                                         : Tr.S("Расширения через запятую (например, .iso, .img):", "Comma-separated extensions (e.g. .iso, .img):"),
                                          "");
            if (pattern == null) return;
            pattern = pattern.Trim();
            if (pattern.Length == 0) return;
            if (kind == "site")
            {
                Uri u;
                if (Uri.TryCreate(pattern, UriKind.Absolute, out u) && (u.Scheme == "http" || u.Scheme == "https")) pattern = u.Host;
                pattern = pattern.TrimEnd('/').ToLowerInvariant();
                if (pattern.IndexOfAny(new[] { '/', ' ', '\\', ':' }) >= 0) { DlSetInfo(Tr.S("Это не имя сайта: ", "This is not a site name: ") + pattern); return; }
            }
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Папка для «", "Folder for “") + pattern + Tr.S("»", "”");
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                DlFolderRule rule = new DlFolderRule();
                rule.Kind = kind;
                rule.Pattern = pattern;
                rule.Folder = dlg.SelectedPath;
                DlChangeSettings(delegate(DlSettings s) { s.Rules.Add(rule); }, Tr.S("Правило добавлено.", "Rule added."));
            }
        }

        private void DlAddPlace()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Место для списка «куда скачивать»", "A place for the “where to download” list");
                dlg.ShowNewFolderButton = true;
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string folder = dlg.SelectedPath;
                DlChangeSettings(delegate(DlSettings s) { DlFolderAsk.RememberFolder(s, folder); }, Tr.S("Место запомнено.", "The place is remembered."));
            }
        }

        private void DlForgetPlace()
        {
            if (_lvDlPlaces.SelectedIndices.Count == 0) { DlSetInfo(Tr.S("Выберите место в списке.", "Select a place in the list.")); return; }
            string folder = _lvDlPlaces.Items[_lvDlPlaces.SelectedIndices[0]].Text;
            DlChangeSettings(delegate(DlSettings s)
            {
                for (int i = s.RecentFolders.Count - 1; i >= 0; i--)
                    if (string.Equals(s.RecentFolders[i], folder, StringComparison.OrdinalIgnoreCase)) s.RecentFolders.RemoveAt(i);
            }, Tr.S("Место забыто.", "The place is forgotten."));
        }

        private void DlRemoveRule()
        {
            if (_lvDlRules.SelectedIndices.Count == 0) { DlSetInfo(Tr.S("Выберите правило в списке.", "Select a rule in the list.")); return; }
            int index = _lvDlRules.SelectedIndices[0];
            string pattern = _lvDlRules.Items[index].SubItems[1].Text;
            DlChangeSettings(delegate(DlSettings s)
            {
                if (index < s.Rules.Count && s.Rules[index].Pattern == pattern) s.Rules.RemoveAt(index);
            }, Tr.S("Правило удалено.", "Rule removed."));
        }

        private void DlMoveRule(int delta)
        {
            if (_lvDlRules.SelectedIndices.Count == 0) return;
            int index = _lvDlRules.SelectedIndices[0];
            string pattern = _lvDlRules.Items[index].SubItems[1].Text;
            DlChangeSettings(delegate(DlSettings s)
            {
                int to = index + delta;
                if (index >= s.Rules.Count || to < 0 || to >= s.Rules.Count || s.Rules[index].Pattern != pattern) return;
                DlFolderRule r = s.Rules[index];
                s.Rules.RemoveAt(index);
                s.Rules.Insert(to, r);
            }, Tr.S("Порядок правил изменён.", "Rule order changed."));
        }

        private void DlStopAgent()
        {
            if (!DlIpc.IsRunning()) { DlSetInfo(Tr.S("Процесс загрузок и так не запущен.", "The download process is not running anyway.")); return; }
            bool active = _dlSnap != null && _dlSnap.Active > 0;
            if (active && !MsgAsk(Tr.S("Идут загрузки. Остановить процесс? Скачанное сохранится, загрузки продолжатся при следующем запуске.",
                                       "Downloads are in progress. Stop the process? What was downloaded is kept; downloads continue at the next start."),
                                  Tr.S("Загрузки", "Downloads"))) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool signalled = DlIpc.SignalShutdown();
                Stopwatch clock = Stopwatch.StartNew();
                while (signalled && DlIpc.IsRunning() && clock.ElapsedMilliseconds < 8000) Thread.Sleep(100);
                bool stopped = !DlIpc.IsRunning();
                UiPost(delegate
                {
                    DlSetInfo(stopped ? Tr.S("Процесс загрузок остановлен.", "The download process has stopped.")
                                      : Tr.S("Процесс загрузок не остановился за 8 секунд.", "The download process did not stop within 8 seconds."));
                    DlLoadSettingsView();
                });
            });
        }
    }
}
