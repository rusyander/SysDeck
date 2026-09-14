// Windows Process Cleaner — вкладка «Загрузки»: список загрузок, фильтры, действия над записями.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Страница сама ничего не качает: загрузки ведёт фоновый процесс (--downloads), окно — клиент его канала. Процесс живёт
// по требованию: первое действие здесь его запускает, а без работы он уходит сам. Пока его нет, список читается из
// хранилища только на чтение — ни одной записи окно в обход процесса не меняет (кроме настроек, см. DownloadsSettings).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private const string DlScope = "downloads";
        private const int DlColName = 0, DlColSize = 1, DlColProgress = 2, DlColSpeed = 3, DlColEta = 4, DlColState = 5,
                          DlColSource = 6, DlColFolder = 7, DlColAdded = 8;
        private static readonly int[] DlLimits = { 0, 128, 512, 1024, 5120, 10240 };
        private static readonly DlStateFilter[] DlFilters = { DlStateFilter.All, DlStateFilter.Running, DlStateFilter.Waiting,
                                                              DlStateFilter.Paused, DlStateFilter.Done, DlStateFilter.Errors };

        private Panel _dlMain;
        private FlowLayoutPanel _dlChips;
        private Label _lblDlStatus, _lblDlInfo;
        private Button _btnDlPause, _btnDlResume, _btnDlRemove, _btnDlSettings;
        private RoundComboBox _cmbDlMode, _cmbDlType, _cmbDlSource;
        private readonly Dictionary<DlStateFilter, RoundButton> _dlChipButtons = new Dictionary<DlStateFilter, RoundButton>();
        private FastListView _lvDl;
        private SplitContainer _dlSplit;
        private System.Windows.Forms.Timer _dlTick;
        private DlSnapshot _dlSnap;
        private DlStateFilter _dlFilter = DlStateFilter.All;
        private string _dlType = "", _dlSource = "";
        private readonly Dictionary<string, string> _dlProgressKey = new Dictionary<string, string>();
        // Отмеченные галочками — по идентификаторам: список перестраивается целиком, ссылки на строки не переживают обновление.
        private readonly HashSet<string> _dlChecked = new HashSet<string>();
        private int _dlPollBusy;
        private DateTime _dlStoreAt = DateTime.MinValue;
        private bool _dlFilling, _dlModeLoading, _dlSplitRestored;
        private bool _dlStartPage;

        // /downloads при запуске окна; от повторного запуска — OpenDownloads.
        public void SetDownloadsStart() { _dlStartPage = true; }

        public void OpenDownloads()
        {
            ShowWindow();
            ShowPage(PageDownloads);
        }

        private Control BuildDownloadsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);
            _dlTab = tab;

            _dlMain = new Panel();
            _dlMain.Dock = DockStyle.Fill;
            tab.Controls.Add(_dlMain);
            tab.Controls.Add(BuildDownloadsSettingsView());

            FlowLayoutPanel bar = MkToolbar();
            Button add = MkFlowButton(Tr.S("Добавить…", "Add…"), 120, true);
            add.Click += delegate { DlShowAdd(""); };
            Button paste = MkFlowButton(Tr.S("Вставить ссылки", "Paste links"), 150, false);
            paste.Click += delegate { DlPasteLinks(); };
            Button torrent = MkFlowButton(Tr.S("Торрент…", "Torrent…"), 110, false);
            torrent.Click += delegate { DlPickTorrentFiles(); };
            _btnDlPause = MkFlowButton(Tr.S("Пауза", "Pause"), 90, false);
            _btnDlPause.Click += delegate { DlForSelected("pause", null); };
            _btnDlResume = MkFlowButton(Tr.S("Продолжить", "Resume"), 120, false);
            _btnDlResume.Click += delegate { DlForSelected("resume", null); };
            _btnDlRemove = MkFlowButton(Tr.S("Удалить", "Remove"), 100, false);
            _btnDlRemove.Click += delegate { DlRemoveSelected(false); };
            Button pauseAll = MkFlowButton(Tr.S("Всё на паузу", "Pause all"), 130, false);
            pauseAll.Click += delegate { DlSend(DlClient.Command("pauseAll"), Tr.S("Ставлю всё на паузу…", "Pausing everything…"), null); };
            Button resumeAll = MkFlowButton(Tr.S("Продолжить всё", "Resume all"), 150, false);
            resumeAll.Click += delegate { DlSend(DlClient.Command("resumeAll"), Tr.S("Продолжаю всё…", "Resuming everything…"), null); };
            bar.Controls.AddRange(new Control[] { add, paste, torrent, _btnDlPause, _btnDlResume, _btnDlRemove, pauseAll, resumeAll });
            bar.SetFlowBreak(resumeAll, true);   // второй ряд — скорость и настройки: подпись не отрывается от своего списка
            bar.Controls.Add(MkFlowLabel(Tr.S("Скорость:", "Speed:"), false));
            _cmbDlMode = new RoundComboBox();
            _cmbDlMode.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDlMode.Width = 190;
            _cmbDlMode.Margin = new Padding(0, 4, 12, 8);
            _cmbDlMode.Items.AddRange(new object[] { Tr.S("обычная", "normal"), Tr.S("тихая — не мешать сети", "quiet — spare the network"), Tr.S("без ограничений", "unlimited") });
            _cmbDlMode.SelectedIndexChanged += delegate { if (!_dlModeLoading) DlSetMode((DlSpeedMode)_cmbDlMode.SelectedIndex); };
            bar.Controls.Add(_cmbDlMode);
            _btnDlDetach = MkFlowButton(Tr.S("Отдельным окном", "Separate window"), 170, false);
            _btnDlDetach.Click += delegate { if (DownloadsDetached) DlAttach(); else DlDetach(); };
            bar.Controls.Add(_btnDlDetach);
            _btnDlSettings = MkFlowButton(Tr.S("Настройки загрузок", "Download settings"), 170, false);
            _btnDlSettings.Click += delegate { DlShowSettings(true); };
            bar.Controls.Add(_btnDlSettings);

            _lblDlStatus = MkNote("", false);
            _lblDlInfo = MkNote(Tr.S("Ссылку можно вставить Ctrl+V в списке или перетащить из браузера. Буфер обмена читается только по этому действию.",
                                     "Paste a link with Ctrl+V in the list or drag it from a browser. The clipboard is read only on that action."), true);

            _dlChips = MkToolbar();
            foreach (DlStateFilter f in DlFilters)
            {
                RoundButton chip = (RoundButton)MkFlowButton(DlFilterTitle(f) + " 000", 80, false);
                chip.Height = 30;
                chip.Margin = new Padding(0, 0, 6, 6);
                DlStateFilter captured = f;
                chip.Click += delegate { DlSetFilter(captured); };
                _dlChips.Controls.Add(chip);
                _dlChipButtons[f] = chip;
            }
            _cmbDlType = DlFilterCombo(170);
            _cmbDlType.Items.Add(Tr.S("все типы", "all types"));
            foreach (string c in DlView.Categories) _cmbDlType.Items.Add(DlView.CategoryTitle(c));
            _cmbDlType.SelectedIndexChanged += delegate
            {
                if (_dlFilling) return;
                _dlType = _cmbDlType.SelectedIndex <= 0 ? "" : DlView.Categories[_cmbDlType.SelectedIndex - 1];
                MemSet(DlScope, "type", _dlType, true);
                DlFillList();
            };
            _cmbDlSource = DlFilterCombo(170);
            _cmbDlSource.Items.AddRange(new object[] { Tr.S("все источники", "all sources"), DlView.SourceTitle("manual"), "Chrome", "Edge",
                                                       DlView.SourceTitle("yandex"), "Firefox", "Chromium", DlView.SourceTitle("watch") });
            _cmbDlSource.SelectedIndexChanged += delegate
            {
                if (_dlFilling) return;
                _dlSource = DlSourceKey(_cmbDlSource.SelectedIndex);
                MemSet(DlScope, "source", _dlSource, true);
                DlFillList();
            };

            _dlSplit = new SplitContainer();
            _dlSplit.Dock = DockStyle.Fill;
            _dlSplit.Orientation = Orientation.Horizontal;
            _dlSplit.SplitterWidth = 6;
            _dlSplit.Size = new Size(1000, 600);          // размер ПЕРВЫМ, иначе Panel2MinSize бросает (см. Браузеры)
            _dlSplit.Panel1MinSize = 140;
            _dlSplit.Panel2MinSize = 150;
            _dlSplit.FixedPanel = FixedPanel.Panel2;
            _dlSplit.SplitterDistance = 330;
            _dlSplit.Panel1.Padding = new Padding(1);    // место под скруглённую рамку (Boxed)
            _dlSplit.Panel2.Padding = new Padding(1, 4, 1, 1);
            _dlSplit.SplitterMoved += delegate
            {
                if (_dlSplitRestored) MemSet(DlScope, "card-height", Unscaled(_dlSplit.Panel2.Height).ToString(), true);
            };

            _lvDl = new FastListView();
            _lvDl.Dock = DockStyle.Fill;
            _lvDl.FullRowSelect = true;
            _lvDl.MultiSelect = true;
            _lvDl.HideSelection = false;
            _lvDl.CheckBoxes = true;      // групповые действия: отмеченные важнее выделения
            _lvDl.AllowDrop = true;
            int[] widths = DlColumnWidths();
            _lvDl.Columns.Add(Tr.S("Имя", "Name"), widths[0]);
            _lvDl.Columns.Add(Tr.S("Размер", "Size"), widths[1]);
            _lvDl.Columns.Add(Tr.S("Прогресс", "Progress"), widths[2]);
            _lvDl.Columns.Add(Tr.S("Скорость", "Speed"), widths[3]);
            _lvDl.Columns.Add(Tr.S("Осталось", "Left"), widths[4]);
            _lvDl.Columns.Add(Tr.S("Состояние", "State"), widths[5]);
            _lvDl.Columns.Add(Tr.S("Источник", "Source"), widths[6]);
            _lvDl.Columns.Add(Tr.S("Папка", "Folder"), widths[7]);
            _lvDl.Columns.Add(Tr.S("Добавлено", "Added"), widths[8]);
            SetupOwnerDraw(_lvDl);
            _lvDl.DrawSubItem += DlDrawProgress;
            _flexColumn[_lvDl] = DlColName;
            _pathColumns[_lvDl] = DlColFolder;
            _lvDl.ColumnWidthChanged += delegate(object s, ColumnWidthChangedEventArgs e)
            {
                if (!_inAutoFill && e.ColumnIndex != DlColName) DlSaveColumnWidths();
            };
            _lvDl.SelectedIndexChanged += delegate { if (!_dlFilling) DlSelectionChanged(); };
            _lvDl.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                DlRow r = e.Item == null ? null : e.Item.Tag as DlRow;
                if (r == null) return;
                if (e.Item.Checked) _dlChecked.Add(r.Item.Id); else _dlChecked.Remove(r.Item.Id);
                if (!_dlFilling) { DlUpdateButtons(); DlShowChecked(); }
            };
            _lvDl.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                ListViewItem hit = _lvDl.GetItemAt(e.X, e.Y);
                DlRow r = hit == null ? null : hit.Tag as DlRow;
                if (r != null) DlOpenFile(r);
            };
            _lvDl.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.V) { e.Handled = true; e.SuppressKeyPress = true; DlPasteLinks(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.Handled = true; DlSelectAll(); }
                else if (e.KeyCode == Keys.Delete) { e.Handled = true; DlRemoveSelected(e.Shift); }
                else if (e.KeyCode == Keys.Enter) { DlRow r = DlFirstSelected(); if (r != null) { e.Handled = true; DlOpenFile(r); } }
                else if (e.KeyCode == Keys.Space) { e.Handled = true; e.SuppressKeyPress = true; DlTogglePause(); }
            };
            _lvDl.DragEnter += delegate(object s, DragEventArgs e) { e.Effect = DlDropped(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None; };
            _lvDl.DragDrop += delegate(object s, DragEventArgs e)
            {
                DlDrop drop = DlDropped(e.Data);
                if (drop != null) BeginInvoke((MethodInvoker)delegate { DlOpenDrop(drop); });
            };
            _lvDl.ContextMenu = DlBuildMenu();
            _dlSplit.Panel1.Controls.Add(_lvDl);
            _dlSplit.Panel2.Controls.Add(BuildDownloadsCard());

            // Порядок Dock: последний добавленный Top — самый верхний.
            _dlMain.Controls.Add(_dlSplit);
            _dlMain.Controls.Add(_dlChips);
            _dlMain.Controls.Add(_lblDlInfo);
            _dlMain.Controls.Add(_lblDlStatus);
            _dlMain.Controls.Add(bar);

            _dlFilling = true;
            try
            {
                string filter = MemGet(DlScope, "filter", true);
                for (int i = 0; i < DlFilters.Length; i++) if (DlFilters[i].ToString() == filter) _dlFilter = DlFilters[i];
                _dlType = MemGet(DlScope, "type", true) ?? "";
                int typeIndex = Array.IndexOf(DlView.Categories, _dlType);
                if (typeIndex < 0) _dlType = "";
                _cmbDlType.SelectedIndex = typeIndex + 1;
                _dlSource = MemGet(DlScope, "source", true) ?? "";
                int sourceIndex = 0;
                for (int i = 0; i < _cmbDlSource.Items.Count; i++) if (DlSourceKey(i) == _dlSource) sourceIndex = i;
                _dlSource = DlSourceKey(sourceIndex);
                _cmbDlSource.SelectedIndex = sourceIndex;
            }
            finally { _dlFilling = false; }
            DlUpdateChips();
            DlUpdateButtons();
            return tab;
        }

        private RoundComboBox DlFilterCombo(int width)
        {
            RoundComboBox cb = new RoundComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Width = width;
            cb.Margin = new Padding(8, 0, 0, 6);
            _dlChips.Controls.Add(cb);
            return cb;
        }

        private static string DlSourceKey(int index)
        {
            switch (index)
            {
                case 1: return "manual";
                case 2: return "chrome";
                case 3: return "edge";
                case 4: return "yandex";
                case 5: return "firefox";
                case 6: return "chromium";
                case 7: return "watch";
                default: return "";
            }
        }

        private static string DlFilterTitle(DlStateFilter f)
        {
            switch (f)
            {
                case DlStateFilter.Running: return Tr.S("Качаются", "Downloading");
                case DlStateFilter.Waiting: return Tr.S("Ждут", "Waiting");
                case DlStateFilter.Paused: return Tr.S("На паузе", "Paused");
                case DlStateFilter.Done: return Tr.S("Готовые", "Done");
                case DlStateFilter.Errors: return Tr.S("Ошибки", "Errors");
                default: return Tr.S("Все", "All");
            }
        }

        // Ширины колонок живут в памяти окна в пикселях макета: при другом DPI SetupOwnerDraw умножит их сам.
        private int[] DlColumnWidths()
        {
            int[] w = { 250, 140, 120, 80, 70, 120, 80, 140, 90 };   // имя тянется на остаток
            string saved = MemGet(DlScope, "columns", true);
            if (string.IsNullOrEmpty(saved)) return w;
            string[] parts = saved.Split(',');
            if (parts.Length != w.Length) return w;
            for (int i = 0; i < w.Length; i++)
            {
                int v;
                if (int.TryParse(parts[i], out v) && v >= 40 && v <= 1200) w[i] = v;
            }
            return w;
        }

        private void DlSaveColumnWidths()
        {
            if (_lvDl == null || _lvDl.Columns.Count != 9) return;
            string[] parts = new string[9];
            for (int i = 0; i < 9; i++) parts[i] = Unscaled(_lvDl.Columns[i].Width).ToString();
            MemSet(DlScope, "columns", string.Join(",", parts), true);
        }

        // ---------- вход и уход ----------

        private void DownloadsEnter()
        {
            if (_dlTick == null)
            {
                _dlTick = new System.Windows.Forms.Timer();
                _dlTick.Interval = 700;
                _dlTick.Tick += delegate { DlPoll(); };
            }
            _dlTick.Start();
            DlRestoreSplit();
            DlLoadMode();
            DlPoll();
        }

        // Высота карточки — после первого показа: до раскладки у SplitContainer ещё макетный размер.
        private void DlRestoreSplit()
        {
            if (_dlSplitRestored || _dlSplit.Height <= 0) return;
            int saved;
            if (int.TryParse(MemGet(DlScope, "card-height", true), out saved) && saved > 0)
            {
                int distance = _dlSplit.Height - Px(saved) - _dlSplit.SplitterWidth;
                if (distance >= _dlSplit.Panel1MinSize && _dlSplit.Height - distance - _dlSplit.SplitterWidth >= _dlSplit.Panel2MinSize)
                    _dlSplit.SplitterDistance = distance;
            }
            _dlSplitRestored = true;
        }

        private void DownloadsLeave()
        {
            // Открепленное окно видно всегда, какая бы вкладка ни была открыта в главном: опрос ему нужен по-прежнему.
            if (DownloadsDetached) return;
            if (_dlTick != null) _dlTick.Stop();
            DlSettingsFlush();
        }

        // ---------- опрос ----------

        // Список без событий раз в 700 мс — только пока страница видна. Пока процесс работает, опрос держит его живым;
        // без процесса хранилище перечитывается раз в 3 секунды (менять его, кроме процесса, некому).
        private void DlPoll()
        {
            if (_closing || Interlocked.CompareExchange(ref _dlPollBusy, 1, 0) != 0) return;
            bool storeFresh = _dlSnap != null && !_dlSnap.Live && (DateTime.UtcNow - _dlStoreAt).TotalSeconds < 3;
            string logId = DlCardWantsLog();
            string torrentId = DlCardWantsTorrent();
            DlItem torrentItem = null;
            if (torrentId != null) torrentItem = DlSelectedRows()[0].Item;
            string torrentHash = torrentItem != null ? torrentItem.InfoHash : null;
            int[] torrentPriorities = torrentItem != null ? torrentItem.FilePriorities : null;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSnapshot snap = null;
                DlRow logged = null;
                DlTorrentCard tcard = null;
                bool fromStore = false;
                try
                {
                    if (DlIpc.IsRunning())
                    {
                        snap = DlSnapshot.FromList(DlClient.Call(DlClient.Command("list"), 400));
                        if (snap != null && logId != null) logged = DlGetItem(logId);
                        if (snap != null && torrentId != null) tcard = DlGetTorrent(torrentId);
                    }
                    if (snap == null && !storeFresh)
                    {
                        snap = DlSnapshot.FromStore(new DlStore(DlPaths.DataDir).LoadAll());
                        fromStore = true;
                    }
                    // Процесс не запущен или торрент ему не известен: файлы и выбор — из файла торрента и записи.
                    if (torrentId != null && tcard == null)
                        tcard = DlTorrentCard.FromMeta(DlEngine.LoadTorrentMeta(DlPaths.TorrentsDir, torrentHash), torrentPriorities);
                }
                catch (Exception ex) { DlLog.Report(ex); }
                DlSnapshot result = snap;
                DlRow withLog = logged;
                DlTorrentCard withTorrent = tcard;
                bool store = fromStore;
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _dlPollBusy, 0);
                    if (result != null)
                    {
                        if (store) _dlStoreAt = DateTime.UtcNow;
                        DlApply(result);
                        if (withLog != null) DlCardLog(withLog);
                    }
                    if (torrentId != null) DlCardTorrent(torrentId, withTorrent);
                });
            });
        }

        // Карточка торрента (команда torrent); null — процесс не ответил или торрента у него нет.
        private static DlTorrentCard DlGetTorrent(string id)
        {
            JVal req = DlClient.Command("torrent");
            req.Set("id", DlJson.S(id));
            JVal answer = DlClient.Call(req, 400);
            if (answer == null || !DlJson.Bool(answer, "ok", false)) return null;
            return DlTorrentCard.FromJson(answer.Get("torrent"));
        }

        // Одна запись с журналом (команда get); null — процесс не ответил.
        private static DlRow DlGetItem(string id)
        {
            JVal req = DlClient.Command("get");
            req.Set("id", DlJson.S(id));
            JVal answer = DlClient.Call(req, 400);
            if (answer == null || !DlJson.Bool(answer, "ok", false)) return null;
            return DlSnapshot.RowFromJson(answer.Get("item"));
        }

        private void DlApply(DlSnapshot snap)
        {
            if (_closing || _lvDl == null) return;
            bool wasLive = _dlSnap != null && _dlSnap.Live;
            _dlSnap = snap;
            DlUpdateStatus();
            DlUpdateChips();
            DlFillList();
            DlUpdateButtons();
            DlCardRefresh(false);
            if (snap.Live != wasLive) DlLoadMode();
        }

        private void DlUpdateStatus()
        {
            DlSnapshot s = _dlSnap;
            if (s == null) return;
            string text = DlView.ProcessStatusText(s);
            if (_lblDlStatus.Text != text) _lblDlStatus.Text = text;
        }

        private void DlUpdateChips()
        {
            foreach (KeyValuePair<DlStateFilter, RoundButton> kv in _dlChipButtons)
            {
                int n = DlView.Count(_dlSnap, kv.Key);
                string text = DlFilterTitle(kv.Key) + (n > 0 || kv.Key == DlStateFilter.All ? " " + n : "");
                if (kv.Value.Text != text) kv.Value.Text = text;
                string tag = kv.Key == _dlFilter ? "primary" : null;
                if ((kv.Value.Tag as string) != tag) kv.Value.Tag = tag;
            }
            ApplyThemeTo(_dlChips);
        }

        private void DlSetFilter(DlStateFilter f)
        {
            _dlFilter = f;
            MemSet(DlScope, "filter", f.ToString(), true);
            DlUpdateChips();
            DlFillList();
            DlUpdateButtons();
        }

        // ---------- список ----------

        private void DlFillList()
        {
            List<DlRow> rows = new List<DlRow>();
            if (_dlSnap != null)
                foreach (DlRow r in _dlSnap.Rows)
                    if (DlView.Matches(r.Item, _dlFilter, _dlType, _dlSource)) rows.Add(r);
            if (_dlSnap != null && _dlChecked.Count > 0)
            {
                // Удалённые записи не держат отметку: иначе набор рос бы вечно и «отмечено» врало.
                HashSet<string> alive = new HashSet<string>();
                foreach (DlRow r in _dlSnap.Rows) alive.Add(r.Item.Id);
                List<string> gone = new List<string>();
                foreach (string checkedId in _dlChecked) if (!alive.Contains(checkedId)) gone.Add(checkedId);
                foreach (string checkedId in gone) _dlChecked.Remove(checkedId);
            }

            bool same = rows.Count == _lvDl.Items.Count;
            for (int i = 0; same && i < rows.Count; i++)
            {
                DlRow old = _lvDl.Items[i].Tag as DlRow;
                same = old != null && old.Item.Id == rows[i].Item.Id;
            }
            if (same)
            {
                for (int i = 0; i < rows.Count; i++) DlUpdateItem(_lvDl.Items[i], rows[i]);
            }
            else
            {
                HashSet<string> selected = new HashSet<string>(DlSelectedIds());
                _dlFilling = true;
                _lvDl.BeginUpdate();
                try
                {
                    _lvDl.Items.Clear();
                    _dlProgressKey.Clear();
                    ListViewItem[] items = new ListViewItem[rows.Count];
                    for (int i = 0; i < rows.Count; i++)
                    {
                        items[i] = new ListViewItem(new string[9]);
                        items[i].UseItemStyleForSubItems = false;
                        DlUpdateItem(items[i], rows[i]);
                    }
                    _lvDl.Items.AddRange(items);
                    foreach (ListViewItem it in _lvDl.Items)
                    {
                        string itemId = ((DlRow)it.Tag).Item.Id;
                        if (selected.Contains(itemId)) it.Selected = true;
                        if (_dlChecked.Contains(itemId)) it.Checked = true;
                    }
                }
                finally
                {
                    _lvDl.EndUpdate();
                    _dlFilling = false;
                }
                AutoFillLastColumnDeferred(_lvDl);
                DlCardRefresh(true);
            }
            if (rows.Count == 0 && _dlSnap != null)
            {
                string empty = _dlSnap.Rows.Count == 0
                    ? Tr.S("Загрузок пока нет. «Добавить…», Ctrl+V или перетащите ссылку в список.", "No downloads yet. Use “Add…”, Ctrl+V or drop a link onto the list.")
                    : Tr.S("Под фильтр ничего не попало — выберите «Все».", "Nothing matches the filter — pick “All”.");
                DlInfo(empty);
            }
        }

        private void DlUpdateItem(ListViewItem lvi, DlRow r)
        {
            DlItem it = r.Item;
            DateTime now = DateTime.UtcNow;
            string[] texts = new string[9];
            texts[DlColName] = DlView.DisplayName(it);
            texts[DlColSize] = DlView.SizeText(r);
            texts[DlColProgress] = "";
            texts[DlColSpeed] = DlView.SpeedText(r);
            texts[DlColEta] = DlView.EtaText(r);
            texts[DlColState] = DlView.StateText(r, now);
            texts[DlColSource] = DlView.SourceTitle(it.Source);
            texts[DlColFolder] = it.Folder;
            texts[DlColAdded] = DlView.Local(it.AddedUtc, now);
            lvi.Tag = r;
            for (int i = 0; i < 9; i++)
                if (lvi.SubItems[i].Text != texts[i]) lvi.SubItems[i].Text = texts[i];
            lvi.ToolTipText = texts[DlColName] + "\r\n" + texts[DlColState];

            Color stateColor = DlStateColor(it);
            for (int i = 0; i < 9; i++)
            {
                Color want = i == DlColState ? stateColor : _theme.Text;
                if (lvi.SubItems[i].ForeColor != want) lvi.SubItems[i].ForeColor = want;
                if (lvi.SubItems[i].BackColor != _theme.Surface) lvi.SubItems[i].BackColor = _theme.Surface;
            }

            // Полоса рисуется по Tag, текста в ячейке нет — перерисовать её надо самим, когда прогресс сдвинулся.
            double f = DlView.Fraction(r);
            string key = it.State + "|" + (f < 0 ? "-" : ((int)(f * 1000)).ToString()) + "|" + DlSegmentsKey(it);
            string old;
            if (!_dlProgressKey.TryGetValue(it.Id, out old) || old != key)
            {
                _dlProgressKey[it.Id] = key;
                if (lvi.ListView != null)
                    try { lvi.ListView.Invalidate(lvi.SubItems[DlColProgress].Bounds); } catch { }
            }
        }

        private static string DlSegmentsKey(DlItem it)
        {
            if (it.Total <= 0) return "";
            long step = Math.Max(1, it.Total / 200);
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            lock (it.Segments) foreach (DlSegment s in it.Segments) sb.Append(s.Done / step).Append(',');
            return sb.ToString();
        }

        private Color DlWarnColor() { return _theme.Dark ? Color.FromArgb(245, 158, 11) : Color.FromArgb(217, 119, 6); }

        // Общие кирпичики диалога добавления и настроек: там они различаются только отступами.
        private Label DlLabel(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            return l;
        }

        private static FlowLayoutPanel DlFlowRow(FlowLayoutPanel parent)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 2, 0, 2);
            parent.Controls.Add(f);
            return f;
        }

        private static RoundComboBox DlCombo(FlowLayoutPanel row, int width, params string[] items)
        {
            RoundComboBox cb = new RoundComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Width = width;
            cb.Margin = new Padding(0, 4, 16, 8);
            cb.Items.AddRange(items);
            row.Controls.Add(cb);
            return cb;
        }

        private Color DlStateColor(DlItem it)
        {
            if (it.MoveTo.Length > 0 || it.State == DlState.Active || it.State == DlState.Checking) return _theme.Accent;
            if (it.State == DlState.Failed || it.State == DlState.NeedsLink || it.MoveError.Length > 0) return DlWarnColor();
            if (DlView.IsDone(it)) return HealthLevelColor(HealthLevel.Ok);
            if (it.State == DlState.Paused) return _theme.Subtle;
            return _theme.Text;
        }

        // Полоса прогресса и под ней — карта сегментов (что уже лежит в файле).
        private void DlDrawProgress(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ColumnIndex != DlColProgress) return;
            DlRow r = e.Item.Tag as DlRow;
            if (r == null) return;
            Rectangle cell = e.Bounds;
            int pad = Px(8);
            int percentW = Px(40);
            int barH = Px(7);
            Rectangle bar = new Rectangle(cell.Left + pad, cell.Top + (cell.Height - barH) / 2 - Px(2), cell.Width - pad * 2 - percentW, barH);
            if (bar.Width < Px(20)) return;
            Graphics g = e.Graphics;
            double f = DlView.Fraction(r);
            Color fill = DlStateColor(r.Item);
            if (r.Item.State == DlState.Queued || r.Item.State == DlState.Scheduled || r.Item.State == DlState.Waiting || r.Item.State == DlState.Checking)
                fill = Mix(_theme.Accent, _theme.Surface, 0.45f);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath track = RoundedRect(bar, barH / 2))
                g.FillPath(RowBrush(_theme.Header), track);
            if (f > 0)
            {
                int fw = Math.Max(barH, (int)Math.Round(bar.Width * f));
                using (GraphicsPath done = RoundedRect(new Rectangle(bar.X, bar.Y, fw, barH), barH / 2))
                    g.FillPath(RowBrush(fill), done);
            }
            g.SmoothingMode = SmoothingMode.Default;

            DlItem it = r.Item;
            if (it.Total > 0 && !DlView.IsDone(it) && it.MoveTo.Length == 0)
            {
                List<DlSegment> segs;
                lock (it.Segments) segs = new List<DlSegment>(it.Segments);
                if (segs.Count > 1)
                {
                    int y = bar.Bottom + Px(2), h = Math.Max(1, Px(2));
                    foreach (DlSegment s in segs)
                    {
                        if (s.Done <= 0) continue;
                        int x0 = bar.X + (int)((double)s.Start / it.Total * bar.Width);
                        int x1 = bar.X + (int)((double)Math.Min(it.Total, s.Start + s.Done) / it.Total * bar.Width);
                        if (x1 > x0) g.FillRectangle(RowBrush(_theme.Subtle), x0, y, x1 - x0, h);
                    }
                }
            }
            string pct = f < 0 ? "" : (int)Math.Floor(f * 100) + " %";
            TextRenderer.DrawText(g, pct, _lvDl.Font, new Rectangle(bar.Right + Px(4), cell.Top, percentW, cell.Height), e.Item.Selected ? _theme.Text : _theme.Subtle,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }

        private List<string> DlSelectedIds()
        {
            List<string> ids = new List<string>();
            if (_lvDl == null) return ids;
            foreach (ListViewItem it in _lvDl.SelectedItems)
            {
                DlRow r = it.Tag as DlRow;
                if (r != null) ids.Add(r.Item.Id);
            }
            return ids;
        }

        private List<DlRow> DlSelectedRows()
        {
            List<DlRow> rows = new List<DlRow>();
            if (_lvDl == null) return rows;
            foreach (ListViewItem it in _lvDl.SelectedItems)
            {
                DlRow r = it.Tag as DlRow;
                if (r != null) rows.Add(r);
            }
            return rows;
        }

        private DlRow DlFirstSelected()
        {
            List<DlRow> rows = DlSelectedRows();
            return rows.Count == 0 ? null : rows[0];
        }

        // Цель групповых действий: отмеченные галочками, а если не отмечено ничего — выделенные.
        // Карточка внизу за этим не идёт: она всегда показывает ту строку, по которой щёлкнули.
        private List<DlRow> DlTargetRows()
        {
            List<DlRow> rows = new List<DlRow>();
            if (_lvDl == null) return rows;
            foreach (ListViewItem it in _lvDl.Items)
            {
                DlRow r = it.Tag as DlRow;
                if (it.Checked && r != null) rows.Add(r);
            }
            return rows.Count > 0 ? rows : DlSelectedRows();
        }

        private List<string> DlTargetIds()
        {
            List<string> ids = new List<string>();
            foreach (DlRow r in DlTargetRows()) ids.Add(r.Item.Id);
            return ids;
        }

        private int DlCheckedCount()
        {
            int n = 0;
            if (_lvDl != null) foreach (ListViewItem it in _lvDl.Items) if (it.Checked) n++;
            return n;
        }

        private void DlCheckAll(bool on)
        {
            if (_lvDl == null) return;
            _dlFilling = true;
            try
            {
                foreach (ListViewItem it in _lvDl.Items)
                {
                    DlRow r = it.Tag as DlRow;
                    if (r == null) continue;
                    it.Checked = on;
                    if (on) _dlChecked.Add(r.Item.Id); else _dlChecked.Remove(r.Item.Id);
                }
            }
            finally { _dlFilling = false; }
            DlUpdateButtons();
            DlShowChecked();
        }

        private void DlShowChecked()
        {
            int n = DlCheckedCount();
            if (n == 0) return;
            long bytes = 0;
            foreach (DlRow r in DlTargetRows()) bytes += r.Item.Total > 0 ? r.Item.Total : r.Done;
            DlInfo(Tr.S("Отмечено: ", "Checked: ") + n.ToString(CultureInfo.InvariantCulture)
                   + (bytes > 0 ? " · " + Engine.FormatBytes(bytes) : "")
                   + Tr.S(" · «Пауза», «Продолжить» и «Удалить» действуют на отмеченные.",
                          " · “Pause”, “Resume” and “Remove” act on the checked ones."));
        }

        private void DlSelectAll()
        {
            _dlFilling = true;
            try { foreach (ListViewItem it in _lvDl.Items) it.Selected = true; }
            finally { _dlFilling = false; }
            DlSelectionChanged();
        }

        private void DlSelectionChanged()
        {
            DlUpdateButtons();
            DlCardRefresh(true);
        }

        private void DlUpdateButtons()
        {
            if (_btnDlPause == null) return;
            bool canPause = false, canResume = false;
            List<DlRow> rows = DlTargetRows();
            foreach (DlRow r in rows)
            {
                if (DlCanPause(r.Item)) canPause = true;
                if (DlCanResume(r.Item)) canResume = true;
            }
            _btnDlPause.Enabled = canPause;
            _btnDlResume.Enabled = canResume;
            _btnDlRemove.Enabled = rows.Count > 0;
        }

        private static bool DlCanPause(DlItem it)
        {
            return it.MoveTo.Length == 0 && (it.State == DlState.Active || it.State == DlState.Queued || it.State == DlState.Scheduled || it.State == DlState.Waiting
                                             || it.State == DlState.Checking || it.State == DlState.Seeding);
        }

        // Готовый торрент «продолжить» — снова раздавать.
        private static bool DlCanResume(DlItem it)
        {
            return it.MoveTo.Length == 0 && (it.State == DlState.Paused || it.State == DlState.Failed || it.State == DlState.NeedsLink
                                             || (it.IsTorrent && it.State == DlState.Completed));
        }

        private void DlInfo(string text)
        {
            if (_lblDlInfo != null && _lblDlInfo.Text != (text ?? "")) _lblDlInfo.Text = text ?? "";
        }

        // ---------- команды процессу ----------

        // Команда в фоновом потоке; процесс поднимается, если не запущен. done получает ответ с ok = true.
        private void DlSend(JVal req, string busy, Action<JVal> done)
        {
            if (busy != null) DlInfo(busy);
            Thread t = new Thread(delegate()
            {
                string error;
                JVal answer = null;
                try { answer = DlCallStarting(req, out error); }
                catch (Exception ex) { error = ex.Message; }
                JVal result = answer;
                string why = error;
                UiPost(delegate
                {
                    if (result != null && !DlJson.Bool(result, "ok", false)) why = DlJson.Str(result, "error", Tr.S("отказ без причины", "refused without a reason"));
                    if (why != null) DlInfo(Tr.S("Не выполнено: ", "Not done: ") + why);
                    else
                    {
                        if (busy != null) DlInfo(Tr.S("Готово.", "Done."));
                        if (done != null) done(result);
                    }
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Канал открывается чуть позже мьютекса — несколько попыток подключиться.
        internal static JVal DlCallStarting(JVal req, out string error)
        {
            error = null;
            if (!DlIpc.IsRunning())
            {
                string why = DlLauncher.StartAgent();
                if (why != null) { error = Tr.S("процесс загрузок не запустился: ", "the download process did not start: ") + why; return null; }
                if (!DlLauncher.WaitRunning(5000)) { error = Tr.S("процесс загрузок не ответил за 5 секунд", "the download process did not respond within 5 seconds"); return null; }
            }
            for (int i = 0; i < 30; i++)
            {
                JVal answer = DlClient.Call(req, 1000);
                if (answer != null) return answer;
                Thread.Sleep(100);
            }
            error = Tr.S("процесс загрузок не отвечает", "the download process does not respond");
            return null;
        }

        private static JVal DlCommandFor(string cmd, string id)
        {
            JVal req = DlClient.Command(cmd);
            req.Set("id", DlJson.S(id));
            return req;
        }

        // Одна команда по всем выделенным — в одном потоке, по очереди. extra дописывает поля запроса.
        private void DlForSelected(string cmd, Action<JVal> extra)
        {
            List<string> ids = DlTargetIds();
            if (ids.Count == 0) { DlInfo(Tr.S("Отметьте или выберите загрузку в списке.", "Check or select a download in the list.")); return; }
            DlInfo(Tr.S("Выполняю…", "Working…"));
            Thread t = new Thread(delegate()
            {
                int ok = 0;
                string lastError = null;
                foreach (string id in ids)
                {
                    JVal req = DlCommandFor(cmd, id);
                    if (extra != null) extra(req);
                    string error;
                    JVal answer = DlCallStarting(req, out error);
                    if (answer != null && DlJson.Bool(answer, "ok", false)) ok++;
                    else lastError = answer != null ? DlJson.Str(answer, "error", "") : error;
                    if (answer == null) break;
                }
                int done = ok;
                string why = lastError;
                UiPost(delegate
                {
                    DlInfo(why == null ? Tr.S("Готово.", "Done.")
                                       : Tr.S("Выполнено: ", "Done: ") + done + Tr.S(" из ", " of ") + ids.Count + " · " + why);
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlTogglePause()
        {
            List<DlRow> rows = DlTargetRows();
            if (rows.Count == 0) return;
            if (DlCanPause(rows[0].Item)) DlForSelected("pause", null);
            else if (DlCanResume(rows[0].Item)) DlForSelected("resume", null);
        }

        private void DlRemoveSelected(bool withFile)
        {
            List<DlRow> rows = DlTargetRows();
            if (rows.Count == 0) { DlInfo(Tr.S("Отметьте или выберите загрузку в списке.", "Check or select a download in the list.")); return; }
            string title = Tr.S("Загрузки", "Downloads");
            string names = DlNames(rows);
            string question;
            if (withFile)
                question = Tr.S("Удалить из списка и отправить файлы в Корзину?\r\n\r\n", "Remove from the list and send the files to the Recycle Bin?\r\n\r\n") + names
                           + Tr.S("\r\n\r\nУ недокачанных в Корзину уходит частичный файл .wpcpart. На томе без Корзины файл не удаляется.",
                                  "\r\n\r\nFor unfinished ones the partial .wpcpart file goes there. On a volume without a Recycle Bin nothing is deleted.");
            else
            {
                bool partial = false;
                foreach (DlRow r in rows) if (!DlView.IsDone(r.Item) && r.Done > 0) partial = true;
                question = Tr.S("Удалить из списка?\r\n\r\n", "Remove from the list?\r\n\r\n") + names
                           + Tr.S("\r\n\r\nФайлы остаются на диске.", "\r\n\r\nFiles stay on disk.")
                           + (partial ? Tr.S(" Недокачанная часть (.wpcpart) тоже останется — удалить её можно командой «Удалить вместе с файлом».",
                                             " The unfinished part (.wpcpart) stays too — “Remove with the file” deletes it.") : "");
            }
            if (MessageBox.Show(this, question, title, MessageBoxButtons.YesNo, withFile ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            bool recycle = withFile;
            DlForSelected("remove", delegate(JVal req) { req.Set("recycle", DlJson.B(recycle)); });
        }

        private static string DlNames(List<DlRow> rows)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < rows.Count && i < 8; i++) names.Add("• " + DlView.DisplayName(rows[i].Item));
            if (rows.Count > 8) names.Add(Tr.S("… и ещё ", "… and ") + (rows.Count - 8));
            return string.Join("\r\n", names.ToArray());
        }

        // ---------- режим скорости ----------

        private void DlLoadMode()
        {
            if (_cmbDlMode == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSettings s = DlReadSettings();
                UiPost(delegate
                {
                    _dlModeLoading = true;
                    try { _cmbDlMode.SelectedIndex = (int)s.Mode; }
                    finally { _dlModeLoading = false; }
                });
            });
        }

        // Настройки у работающего процесса, иначе — из файла (только чтение).
        private static DlSettings DlReadSettings()
        {
            if (DlIpc.IsRunning())
            {
                JVal answer = DlClient.Call(DlClient.Command("settings"), 400);
                DlSettings live = answer != null && DlJson.Bool(answer, "ok", false) ? DlSettings.FromJson(answer.Get("settings")) : null;
                if (live != null) return live;
            }
            return DlSettings.Load();
        }

        private void DlSetMode(DlSpeedMode mode)
        {
            DlChangeSettings(delegate(DlSettings s) { s.Mode = mode; },
                             Tr.S("Скорость: ", "Speed: ") + _cmbDlMode.Text);
        }

        // ---------- ссылки: буфер и перетаскивание ----------

        private void DlPasteLinks()
        {
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (Exception ex) { DlInfo(Tr.S("Буфер обмена не прочитан: ", "The clipboard could not be read: ") + ex.Message); return; }
            DlDrop drop = new DlDrop();
            DlView.ParseLinks(text, drop.Links, null);
            DlView.ParseMagnets(text, drop.Magnets);
            if (drop.Links.Count == 0 && drop.Magnets.Count == 0)
            {
                DlInfo(Tr.S("В буфере обмена нет ссылок http, https или magnet.", "The clipboard has no http, https or magnet links."));
                return;
            }
            DlOpenDrop(drop);
        }

        // Что принесли буфер или перетаскивание: ссылки http(s) — в диалог добавления, торренты — каждый в свой диалог.
        private sealed class DlDrop
        {
            public readonly List<string> Links = new List<string>();
            public readonly List<string> Magnets = new List<string>();
            public readonly List<string> TorrentFiles = new List<string>();
        }

        private void DlOpenDrop(DlDrop drop)
        {
            if (drop.Links.Count > 0) DlShowAdd(string.Join("\r\n", drop.Links.ToArray()));
            if (drop.TorrentFiles.Count > 0 || drop.Magnets.Count > 0) DlShowTorrents(drop.TorrentFiles, drop.Magnets);
        }

        // Перетаскиваемое: текст со ссылками, ярлыки .url, файлы .torrent. null — ничего подходящего.
        private static DlDrop DlDropped(IDataObject data)
        {
            try
            {
                DlDrop drop = new DlDrop();
                if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                {
                    string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
                    DlView.ParseLinks(text, drop.Links, null);
                    DlView.ParseMagnets(text, drop.Magnets);
                }
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                        foreach (string f in files)
                        {
                            if (f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(f) && !drop.TorrentFiles.Contains(f)) drop.TorrentFiles.Add(f);
                                continue;
                            }
                            if (!f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                            FileInfo fi = new FileInfo(f);
                            if (!fi.Exists || fi.Length > 64 * 1024) continue;
                            string url = DlView.LinkFromShortcut(File.ReadAllText(f));
                            if (url != null && !drop.Links.Contains(url)) drop.Links.Add(url);
                        }
                }
                return drop.Links.Count == 0 && drop.Magnets.Count == 0 && drop.TorrentFiles.Count == 0 ? null : drop;
            }
            catch { return null; }
        }

        // ---------- контекстное меню ----------

        private ContextMenu DlBuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem open = new MenuItem(Tr.S("Открыть файл", "Open the file"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlOpenFile(r); });
            MenuItem folder = new MenuItem(Tr.S("Показать в папке", "Show in folder"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlShowInFolder(r); });
            MenuItem copy = new MenuItem(Tr.S("Копировать ссылку", "Copy the link"), delegate { DlCopyLink(); });
            MenuItem pause = new MenuItem(Tr.S("Пауза", "Pause"), delegate { DlForSelected("pause", null); });
            MenuItem resume = new MenuItem(Tr.S("Продолжить", "Resume"), delegate { DlForSelected("resume", null); });
            MenuItem restart = new MenuItem(Tr.S("Скачать заново с нуля…", "Download again from zero…"), delegate { DlRestart(); });
            MenuItem refresh = new MenuItem(Tr.S("Обновить ссылку…", "Refresh the link…"), delegate { DlRefreshLink(); });
            MenuItem mirror = new MenuItem(Tr.S("Добавить зеркало…", "Add a mirror…"), delegate { DlAddMirror(); });
            MenuItem recheck = new MenuItem(Tr.S("Проверить данные", "Check the data"), delegate { DlForSelected("recheck", null); });
            MenuItem reannounce = new MenuItem(Tr.S("Запросить пиров у трекеров", "Ask the trackers for peers"), delegate { DlForSelected("reannounce", null); });
            MenuItem update = new MenuItem(Tr.S("Обновить раздачу…", "Update the torrent…"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlShowUpdate(r.Item.Id, DlView.DisplayName(r.Item)); });
            MenuItem updates = new MenuItem(Tr.S("Новая версия раздачи", "New version of the torrent"));
            MenuItem updCheck = new MenuItem(Tr.S("Проверить на rutracker сейчас", "Check rutracker now"), delegate
            {
                DlRow r = DlFirstSelected();
                if (r != null)
                    DlSend(DlCommandFor("checkUpdate", r.Item.Id), Tr.S("Проверяю новую версию…", "Checking for a new version…"),
                           delegate { DlInfo(Tr.S("Проверка идёт: новая версия придёт уведомлением, итог — в журнале записи.", "Checking: a new version comes as a notification, the outcome is in the item's log.")); });
            });
            MenuItem updFile = new MenuItem(Tr.S("Взять из файла .torrent…", "Take it from a .torrent file…"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlUpdateFromFile(r.Item.Id, DlView.DisplayName(r.Item)); });
            MenuItem updTopic = new MenuItem(Tr.S("Открыть страницу темы", "Open the topic page"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlOpenUrl(r.Item.TopicUrl); });
            updates.MenuItems.AddRange(new MenuItem[] { updCheck, updFile, updTopic });
            MenuItem sequential = new MenuItem(Tr.S("Качать по порядку (для просмотра на ходу)", "Download in order (to watch while downloading)"), delegate
            {
                DlRow r = DlFirstSelected();
                bool on = r == null || !r.Item.Sequential;
                DlForSelected("setSequential", delegate(JVal req) { req.Set("on", DlJson.B(on)); });
            });

            // Видео: растущий файл можно смотреть, не дожидаясь конца; у трансляции конца нет — её останавливает человек.
            MenuItem watch = new MenuItem(Tr.S("Смотреть сейчас", "Watch now"), delegate { DlWatchPreview(); });
            MenuItem stopLive = new MenuItem(Tr.S("Остановить запись", "Stop recording"), delegate { DlStopLive(); });

            MenuItem limit = new MenuItem(Tr.S("Ограничить скорость", "Limit speed"));
            foreach (int kbps in DlLimits)
            {
                int value = kbps;
                MenuItem mi = new MenuItem(DlView.Limit(kbps), delegate { DlForSelected("setLimit", delegate(JVal req) { req.Set("kbps", DlJson.N(value)); }); });
                mi.Tag = value;
                limit.MenuItems.Add(mi);
            }
            MenuItem priority = new MenuItem(Tr.S("Приоритет", "Priority"));
            foreach (int p in new[] { 1, 0, -1 })
            {
                int value = p;
                MenuItem mi = new MenuItem(p > 0 ? Tr.S("высокий", "high") : p < 0 ? Tr.S("низкий", "low") : Tr.S("обычный", "normal"),
                                           delegate { DlForSelected("setPriority", delegate(JVal req) { req.Set("priority", DlJson.N(value)); }); });
                mi.Tag = value;
                priority.MenuItems.Add(mi);
            }
            MenuItem idle = new MenuItem(Tr.S("Только когда ПК простаивает", "Only when the PC is idle"), delegate
            {
                DlRow r = DlFirstSelected();
                bool on = r == null || !r.Item.WhenIdle;
                DlForSelected("whenIdle", delegate(JVal req) { req.Set("on", DlJson.B(on)); });
            });
            MenuItem postpone = new MenuItem(Tr.S("Отложить", "Postpone"));
            foreach (int minutes in new[] { 10, 60, 180, 480 })
            {
                int value = minutes;
                postpone.MenuItems.Add(new MenuItem(Tr.S("на ", "for ") + DlView.Duration(value * 60L),
                    delegate { DlForSelected("postpone", delegate(JVal req) { req.Set("afterMinutes", DlJson.N(value)); }); }));
            }
            MenuItem unpostpone = new MenuItem(Tr.S("Не откладывать", "Don't postpone"), delegate { DlForSelected("postpone", null); });
            postpone.MenuItems.Add("-");
            postpone.MenuItems.Add(unpostpone);

            MenuItem move = new MenuItem(Tr.S("Переместить в…", "Move to…"), delegate { DlRelocate(false); });
            MenuItem copyTo = new MenuItem(Tr.S("Копировать в…", "Copy to…"), delegate { DlRelocate(true); });
            MenuItem cancelMove = new MenuItem(Tr.S("Отменить перенос", "Cancel the move"), delegate { DlForSelected("cancelMove", null); });
            MenuItem hash = new MenuItem(Tr.S("Посчитать хеш", "Compute a hash"));
            foreach (string algo in new[] { "md5", "sha1", "sha256" })
            {
                string a = algo;
                hash.MenuItems.Add(new MenuItem(algo == "md5" ? "MD5" : algo == "sha1" ? "SHA-1" : "SHA-256", delegate { DlComputeHash(a); }));
            }
            MenuItem remove = new MenuItem(Tr.S("Удалить из списка…", "Remove from the list…"), delegate { DlRemoveSelected(false); });
            MenuItem removeFile = new MenuItem(Tr.S("Удалить вместе с файлом (в Корзину)…", "Remove with the file (to the Recycle Bin)…"), delegate { DlRemoveSelected(true); });
            MenuItem history = new MenuItem(Tr.S("Очистить историю", "Clear history"));
            history.MenuItems.Add(new MenuItem(Tr.S("Готовые старше 30 дней", "Finished more than 30 days ago"), delegate { DlClearHistory(30, false); }));
            history.MenuItems.Add(new MenuItem(Tr.S("Готовые, чьих файлов уже нет", "Finished whose files are gone"), delegate { DlClearHistory(0, true); }));
            history.MenuItems.Add(new MenuItem(Tr.S("Все готовые", "All finished"), delegate { DlClearHistory(0, false); }));

            MenuItem checkAll = new MenuItem(Tr.S("Отметить все", "Check all"), delegate { DlCheckAll(true); });
            MenuItem uncheckAll = new MenuItem(Tr.S("Снять отметки", "Clear the checks"), delegate { DlCheckAll(false); });

            menu.MenuItems.AddRange(new MenuItem[] { open, folder, copy, new MenuItem("-"), pause, resume, restart, refresh, mirror, recheck, reannounce, update, updates, new MenuItem("-"),
                                                     watch, stopLive, limit, priority, idle, sequential, postpone, new MenuItem("-"), move, copyTo, cancelMove, hash, new MenuItem("-"),
                                                     checkAll, uncheckAll, new MenuItem("-"), remove, removeFile, new MenuItem("-"), history });
            menu.Popup += delegate
            {
                uncheckAll.Enabled = DlCheckedCount() > 0;
                checkAll.Enabled = _lvDl != null && _lvDl.Items.Count > 0 && DlCheckedCount() < _lvDl.Items.Count;
                List<DlRow> rows = DlTargetRows();
                DlRow first = rows.Count > 0 ? rows[0] : null;
                bool any = first != null;
                bool done = any && DlView.IsDone(first.Item);
                bool moving = any && first.Item.MoveTo.Length > 0;
                bool single = rows.Count == 1;
                // Торренту не подходят команды одной ссылки (заново, ссылка, зеркало) и перенос файла; у него свои — проверка, трекеры, порядок.
                bool torrent = any && first.Item.IsTorrent;
                bool canPause = false, canResume = false, anyMoving = false, anyUnfinished = false, allTorrents = any, allDoneTorrents = any;
                foreach (DlRow r in rows)
                {
                    if (DlCanPause(r.Item)) canPause = true;
                    if (DlCanResume(r.Item)) canResume = true;
                    if (r.Item.MoveTo.Length > 0) anyMoving = true;
                    if (!DlView.IsDone(r.Item)) anyUnfinished = true;
                    if (!r.Item.IsTorrent) allTorrents = false;
                    if (!(r.Item.IsTorrent && r.Item.State == DlState.Completed)) allDoneTorrents = false;
                }
                open.Text = torrent && first.Item.FileName.Length > 0 && Native.IsDirectoryPath(first.Item.TargetPath)
                    ? Tr.S("Открыть папку раздачи", "Open the torrent folder") : Tr.S("Открыть файл", "Open the file");
                open.Enabled = single && done && !moving;
                open.DefaultItem = open.Enabled;
                folder.Enabled = single;
                copy.Enabled = any;
                copy.Text = allTorrents ? Tr.S("Копировать magnet-ссылку", "Copy the magnet link") : Tr.S("Копировать ссылку", "Copy the link");
                pause.Enabled = canPause;
                resume.Enabled = canResume;
                resume.Text = allDoneTorrents ? Tr.S("Раздавать снова", "Seed again") : Tr.S("Продолжить", "Resume");
                restart.Visible = refresh.Visible = mirror.Visible = !allTorrents;
                restart.Enabled = single && !done && !moving && !torrent;
                refresh.Enabled = single && !done && !moving && !torrent;
                mirror.Enabled = single && !done && !torrent;
                recheck.Visible = reannounce.Visible = sequential.Visible = allTorrents;
                recheck.Enabled = allTorrents;
                reannounce.Enabled = allTorrents && (first.Item.State == DlState.Active || first.Item.State == DlState.Seeding);
                sequential.Enabled = allTorrents && anyUnfinished;
                sequential.Checked = torrent && first.Item.Sequential;
                // Смотреть можно, только когда движок действительно ведёт растущий файл; остановить — только идущую запись.
                bool media = any && single && first.Item.IsMedia && first.Item.Media != null;
                watch.Visible = media && first.Item.Media.PreviewPath.Length > 0;
                watch.Enabled = watch.Visible && !done;
                stopLive.Visible = media && first.Item.Media.Live;
                stopLive.Enabled = stopLive.Visible && !first.Item.Media.StopLive && first.Item.State == DlState.Active;
                // Новая версия — у одной раздачи: окно «что изменится» показывает одну замену.
                update.Visible = single && torrent && first.Item.UpdateHash.Length > 0;
                updates.Visible = single && torrent;
                updCheck.Enabled = single && torrent && BtTopic.RutrackerId(first.Item.TopicUrl) != null;
                updTopic.Enabled = single && torrent && first.Item.TopicUrl.Length > 0;
                limit.Enabled = priority.Enabled = postpone.Enabled = any && anyUnfinished;
                idle.Enabled = any && anyUnfinished;
                idle.Checked = any && first.Item.WhenIdle;
                foreach (MenuItem mi in limit.MenuItems) mi.Checked = any && (int)mi.Tag == first.Item.LimitKBps;
                foreach (MenuItem mi in priority.MenuItems) mi.Checked = any && (int)mi.Tag == first.Item.Priority;
                move.Visible = copyTo.Visible = hash.Visible = !allTorrents;
                move.Enabled = single && !moving && !torrent && first.Item.State != DlState.Active;
                copyTo.Enabled = single && done && !moving && !torrent;
                cancelMove.Enabled = anyMoving;
                hash.Enabled = single && done && !moving && !torrent;
                remove.Enabled = removeFile.Enabled = any;
                removeFile.Text = allTorrents ? Tr.S("Удалить вместе с данными (в Корзину)…", "Remove with the data (to the Recycle Bin)…")
                                              : Tr.S("Удалить вместе с файлом (в Корзину)…", "Remove with the file (to the Recycle Bin)…");
            };
            return menu;
        }

        // ---------- действия над одной записью ----------

        // Открытие исполняемого — только после вопроса, где видно издателя и метку «из интернета». Сам файл запускает
        // оболочка Windows, поэтому SmartScreen и предупреждение о вложении срабатывают как обычно.
        // Растущий файл видео: проигрыватель открывает его как обычный файл. Подпись не проверяем — это не программа,
        // и файл ещё не дописан; спрашивать «открыть?» на своём же куске видео незачем.
        private void DlWatchPreview()
        {
            DlRow r = DlFirstSelected();
            if (r == null || !r.Item.IsMedia || r.Item.Media == null) return;
            string path = r.Item.Media.PreviewPath;
            if (path.Length == 0) { DlInfo(Tr.S("Просмотр на ходу для этой записи не включён.", "Watching while downloading is off for this item.")); return; }
            if (!File.Exists(path)) { DlInfo(Tr.S("Файла для просмотра ещё нет — подождите первых кусков.", "There is no file to watch yet — wait for the first parts.")); return; }
            DlShellOpen(path, Path.GetDirectoryName(path));
        }

        private void DlStopLive()
        {
            DlRow r = DlFirstSelected();
            if (r == null || !r.Item.IsMedia || r.Item.Media == null || !r.Item.Media.Live) return;
            DlSend(DlCommandFor("stopLive", r.Item.Id), Tr.S("Останавливаю запись…", "Stopping the recording…"),
                   delegate { DlInfo(Tr.S("Запись останавливается: начатые куски докачаются, потом файл соберётся.",
                                          "The recording is stopping: the started parts finish, then the file is assembled.")); });
        }

        private void DlOpenFile(DlRow r)
        {
            DlItem it = r.Item;
            if (!DlView.IsDone(it) || it.MoveTo.Length > 0)
            {
                DlInfo(Tr.S("Файл ещё не готов: ", "The file is not ready yet: ") + DlView.StateText(r, DateTime.UtcNow));
                return;
            }
            string path = it.TargetPath;
            // Торрент из нескольких файлов — папка: её открывает Проводник, спрашивать нечего.
            if (it.IsTorrent && it.FileName.Length > 0 && Native.IsDirectoryPath(path)) { OpenInExplorer(path, false); return; }
            if (!File.Exists(path))
            {
                DlInfo(Tr.S("Файла больше нет на месте: ", "The file is no longer there: ") + path);
                return;
            }
            if (!DlView.OpenNeedsConfirm(it.FileName)) { DlShellOpen(path, Path.GetDirectoryName(path)); return; }
            DlInfo(Tr.S("Проверяю подпись…", "Checking the signature…"));
            string host = it.Host;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSignatureInfo sig = DlVerify.Check(path);
                string zone = DlVerify.ZoneText(path);
                UiPost(delegate
                {
                    DlInfo("");
                    string text = it.FileName + "\r\n\r\n"
                                  + Tr.S("Это программа или сценарий: открытие выполнит его код на этом компьютере.\r\n\r\n",
                                         "This is a program or a script: opening it runs its code on this computer.\r\n\r\n")
                                  + DlVerify.Describe(sig) + "\r\n"
                                  + Tr.S("Сайт: ", "Site: ") + (host.Length > 0 ? host : "—") + "\r\n"
                                  + Tr.S("Метка: ", "Mark: ") + (zone.Length > 0 ? zone : Tr.S("нет", "none")) + "\r\n\r\n"
                                  + Tr.S("Открыть?", "Open it?");
                    MessageBoxIcon icon = sig.Kind == DlSignature.Valid ? MessageBoxIcon.Question : MessageBoxIcon.Warning;
                    if (MessageBox.Show(this, text, Tr.S("Загрузки", "Downloads"), MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        DlShellOpen(path, Path.GetDirectoryName(path));
                });
            });
        }

        // workDir null — без рабочей папки (ссылка, а не файл).
        private void DlShellOpen(string path, string workDir)
        {
            Thread t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(path);
                    psi.UseShellExecute = true;
                    if (workDir != null) psi.WorkingDirectory = workDir;
                    Process p = Process.Start(psi);
                    if (p != null) p.Dispose();
                }
                catch (Exception ex) { err = ex.Message; }
                string e = err;
                if (e != null) UiPost(delegate { DlInfo(Tr.S("Не открылось: ", "Did not open: ") + e); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlShowInFolder(DlRow r)
        {
            DlItem it = r.Item;
            string file = DlView.IsDone(it) ? it.TargetPath : it.PartPath;
            // У недокачанного торрента корень — папка с частичными файлами внутри (или один файл .wpcpart).
            if (it.IsTorrent && !DlView.IsDone(it) && !Native.PathExists(file)) file = it.TargetPath;
            if (!string.IsNullOrEmpty(it.FileName) && Native.PathExists(file)) { OpenInExplorer(file, true); return; }
            if (!string.IsNullOrEmpty(it.Folder) && Native.IsDirectoryPath(it.Folder)) { OpenInExplorer(it.Folder, false); return; }
            DlInfo(Tr.S("Ни файла, ни папки нет на месте: ", "Neither the file nor the folder is there: ") + (it.Folder ?? ""));
        }

        private void DlCopyLink()
        {
            List<string> links = new List<string>();
            foreach (DlRow r in DlSelectedRows())
            {
                DlItem it = r.Item;
                if (it.IsTorrent) links.Add(DlView.MagnetLink(it, it.Url.Length > 0 ? null : DlEngine.LoadTorrentMeta(DlPaths.TorrentsDir, it.InfoHash)));
                else links.Add(it.OriginalUrl.Length > 0 ? it.OriginalUrl : it.Url);
            }
            if (links.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join("\r\n", links.ToArray()));
                DlInfo(Tr.N(links.Count, "Скопирована ссылка", "Скопированы ссылки", "Скопировано ссылок", "Link copied", "Links copied")
                       + (links.Count > 1 ? ": " + links.Count : "."));
            }
            catch (Exception ex) { DlInfo(ex.Message); }
        }

        private void DlRestart()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            if (!MsgAsk(Tr.S("Скачать «", "Download “") + DlView.DisplayName(r.Item) + Tr.S("» заново с нуля?\r\n\r\nУже скачанная часть (", "” again from zero?\r\n\r\nThe part already downloaded (")
                        + Engine.FormatBytes(r.Done) + Tr.S(") будет удалена.", ") will be deleted."), Tr.S("Загрузки", "Downloads"))) return;
            DlSend(DlCommandFor("restart", r.Item.Id), Tr.S("Начинаю заново…", "Restarting…"), null);
        }

        private void DlRefreshLink()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            string url = DlPromptText(Tr.S("Обновить ссылку", "Refresh the link"),
                                      Tr.S("Новая ссылка на тот же файл. Скачанное сохранится, если сервер отдаёт файл того же размера:",
                                           "A new link to the same file. What was downloaded is kept if the server returns a file of the same size:"),
                                      "");
            if (url == null) return;
            if (!DlHttp.IsAllowedScheme(url.Trim())) { DlInfo(Tr.S("Это не ссылка http или https.", "This is not an http or https link.")); return; }
            JVal req = DlCommandFor("refreshLink", r.Item.Id);
            req.Set("url", DlJson.S(url.Trim()));
            DlSend(req, Tr.S("Обновляю ссылку…", "Refreshing the link…"), null);
        }

        private void DlAddMirror()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            string url = DlPromptText(Tr.S("Зеркало", "Mirror"),
                                      Tr.S("Другая ссылка на тот же файл — на неё загрузка переключится, если основной сервер откажет:",
                                           "Another link to the same file — the download switches to it if the main server fails:"),
                                      "");
            if (url == null) return;
            if (!DlHttp.IsAllowedScheme(url.Trim())) { DlInfo(Tr.S("Это не ссылка http или https.", "This is not an http or https link.")); return; }
            JVal req = DlCommandFor("addMirror", r.Item.Id);
            req.Set("url", DlJson.S(url.Trim()));
            DlSend(req, Tr.S("Добавляю зеркало…", "Adding the mirror…"), null);
        }

        private void DlRelocate(bool copy)
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = copy ? Tr.S("Куда положить копию", "Where to put the copy") : Tr.S("Куда перенести файл загрузки", "Where to move the download's file");
                dlg.ShowNewFolderButton = true;
                try { if (Directory.Exists(r.Item.Folder)) dlg.SelectedPath = r.Item.Folder; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                JVal req = DlCommandFor("move", r.Item.Id);
                req.Set("folder", DlJson.S(dlg.SelectedPath));
                req.Set("copy", DlJson.B(copy));
                DlSend(req, copy ? Tr.S("Копирую…", "Copying…") : Tr.S("Переношу…", "Moving…"), null);
            }
        }

        private void DlComputeHash(string algo)
        {
            DlRow r = DlFirstSelected();
            if (r == null || r.Item.State != DlState.Completed) return;
            string path = r.Item.TargetPath;
            string title = algo == "md5" ? "MD5" : algo == "sha1" ? "SHA-1" : "SHA-256";
            string expected = r.Item.ExpectedHash;
            DlInfo(Tr.S("Считаю ", "Computing ") + title + "…");
            Thread t = new Thread(delegate()
            {
                string hex = DlFinish.Hash(path, algo, delegate { return _closing; });
                UiPost(delegate
                {
                    if (hex == null) { DlInfo(title + Tr.S(" не посчитан: файл не прочитан.", " not computed: the file could not be read.")); return; }
                    string verdict = "";
                    string eAlgo, eHex;
                    if (DlFinish.ParseExpected(expected, out eAlgo, out eHex) && eAlgo == algo)
                        verdict = string.Equals(eHex, hex, StringComparison.OrdinalIgnoreCase)
                            ? Tr.S(" · совпадает с ожидаемым", " · matches the expected one")
                            : Tr.S(" · НЕ совпадает с ожидаемым", " · does NOT match the expected one");
                    try { Clipboard.SetText(hex); } catch { }
                    DlInfo(title + ": " + hex + Tr.S(" (скопирован в буфер)", " (copied to the clipboard)") + verdict);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlClearHistory(int days, bool missingOnly)
        {
            string what = missingOnly ? Tr.S("готовые загрузки, чьих файлов уже нет", "finished downloads whose files are gone")
                        : days > 0 ? Tr.S("готовые загрузки старше ", "finished downloads older than ") + days + Tr.S(" дней", " days")
                        : Tr.S("все готовые загрузки", "all finished downloads");
            if (!MsgAsk(Tr.S("Убрать из списка ", "Remove from the list: ") + what + Tr.S("?\r\n\r\nФайлы на диске не трогаются.", "?\r\n\r\nFiles on disk are not touched."),
                        Tr.S("Загрузки", "Downloads"))) return;
            JVal req = DlClient.Command("clearHistory");
            req.Set("days", DlJson.N(days));
            req.Set("missingOnly", DlJson.B(missingOnly));
            DlSend(req, Tr.S("Очищаю историю…", "Clearing history…"), delegate(JVal answer)
            {
                DlInfo(Tr.S("Убрано записей: ", "Records removed: ") + DlJson.Int(answer, "removed", 0));
            });
        }

        // ---------- «Главная» ----------

        // Строка проверки состояния. null — загрузками не пользовались. Вызывается из рабочего потока проверки.
        private HealthItem HealthDownloadsItem()
        {
            DlSnapshot s = null;
            try
            {
                if (DlIpc.IsRunning()) s = DlSnapshot.FromList(DlClient.Call(DlClient.Command("list"), 500));
                if (s == null) s = DlSnapshot.FromStore(new DlStore(DlPaths.DataDir).LoadAll());
            }
            catch (Exception ex) { DlLog.Report(ex); }
            if (s == null || s.Rows.Count == 0) return null;
            HealthItem h = new HealthItem();
            h.Id = "dlqueue";
            h.Title = Tr.S("Очередь загрузок", "Download queue");
            h.Action = Tr.S("Открыть «Загрузки»", "Open “Downloads”");
            h.ActionKind = "page:Downloads";
            int running = DlView.Count(s, DlStateFilter.Running), waiting = DlView.Count(s, DlStateFilter.Waiting), errors = DlView.Count(s, DlStateFilter.Errors);

            // Мало места там, куда ещё качается: остаток больше свободного или свободно меньше гигабайта.
            Dictionary<string, long> need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (DlRow r in s.Rows)
            {
                DlItem it = r.Item;
                if (it.State == DlState.Completed || it.State == DlState.Failed || string.IsNullOrEmpty(it.Folder)) continue;
                string root;
                try { root = Path.GetPathRoot(it.Folder); } catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;
                long left = it.Total > 0 ? Math.Max(0, it.Total - r.Done) : 0;
                long sum;
                need[root] = need.TryGetValue(root, out sum) ? sum + left : left;
            }
            string lowSpace = null;
            foreach (KeyValuePair<string, long> kv in need)
            {
                long free = DlFiles.FreeSpace(kv.Key);
                if (free >= 0 && (free < kv.Value || free < 1024L * 1024 * 1024))
                    lowSpace = kv.Key.TrimEnd('\\') + Tr.S(" — свободно ", " — free ") + Engine.FormatBytes(free)
                               + (kv.Value > 0 ? Tr.S(", осталось докачать ", ", still to download ") + Engine.FormatBytes(kv.Value) : "");
            }

            List<string> parts = new List<string>();
            if (running > 0) parts.Add(Tr.S("качаются: ", "downloading: ") + running + (s.Speed > 0 ? " (" + DlView.Speed(s.Speed) + ")" : ""));
            if (waiting > 0) parts.Add(Tr.S("ждут: ", "waiting: ") + waiting);
            if (errors > 0) parts.Add(Tr.S("с ошибкой: ", "failed: ") + errors);
            if (!s.Live && waiting > 0) parts.Add(Tr.S("процесс загрузок не запущен", "the download process is not running"));
            if (lowSpace != null) parts.Insert(0, Tr.S("мало места: ", "low space: ") + lowSpace);
            if (parts.Count == 0) parts.Add(Tr.S("всё скачано", "everything is downloaded"));
            h.Detail = string.Join(" · ", parts.ToArray());
            h.Level = lowSpace != null ? HealthLevel.Warn : errors > 0 || (!s.Live && waiting > 0) ? HealthLevel.Info : HealthLevel.Ok;
            if (h.Level == HealthLevel.Ok && running == 0 && waiting == 0) { h.Action = null; h.ActionKind = null; }
            return h;
        }
    }
}
