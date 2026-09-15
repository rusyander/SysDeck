// SysDeck — вкладка «Загрузки»: список загрузок, фильтры, действия над записями.
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
using SysDeck.Downloads;

namespace SysDeck
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
            int[] w = { 156, 140, 120, 80, 70, 120, 80, 140, 90 };   // имя тянется на остаток
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

        private const int DlCardHeightDefault = 264;   // пиксели макета, как и сохранённое значение

        // Высота карточки — после первого показа: до раскладки у SplitContainer ещё макетный размер.
        private void DlRestoreSplit()
        {
            if (_dlSplitRestored || _dlSplit.Height <= 0) return;
            int saved;
            if (!int.TryParse(MemGet(DlScope, "card-height", true), out saved) || saved <= 0) saved = DlCardHeightDefault;
            int distance = _dlSplit.Height - Px(saved) - _dlSplit.SplitterWidth;
            if (distance >= _dlSplit.Panel1MinSize && _dlSplit.Height - distance - _dlSplit.SplitterWidth >= _dlSplit.Panel2MinSize)
                _dlSplit.SplitterDistance = distance;
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
    }
}
