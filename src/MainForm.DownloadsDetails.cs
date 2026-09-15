// SysDeck — вкладка «Загрузки»: карточка выбранной загрузки (обзор, сегменты, сеть, журнал).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Ссылки в карточке проходят через DlLog.Redact — токены из строки запроса не видны через плечо и не уходят в скриншот.
// Cookies не показываются вовсе, только «есть» / «нет».
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Downloads;

namespace SysDeck
{
    public partial class MainForm
    {
        // Вкладки карточки. У торрента вместо «Сегментов» и «Сети» — «Файлы», «Пиры», «Трекеры».
        private const int DlCardOverview = 0, DlCardSegments = 1, DlCardNetwork = 2, DlCardJournal = 3, DlCardFiles = 4, DlCardPeers = 5, DlCardTrackers = 6;
        private const int DlCardTabCount = 7;

        private Label _lblDlCard;
        private FlowLayoutPanel _dlCardTabs;
        private readonly RoundButton[] _dlCardTabButtons = new RoundButton[DlCardTabCount];
        private FastListView _lvDlCard;
        private RamPanel _dlSegMap;
        private int _dlCardTab = DlCardOverview, _dlCardColumnsFor = -1, _dlCardShown = DlCardOverview;
        private bool _dlCardTorrentTabs;
        private DlRow _dlCardLogRow;
        private DlTorrentCard _dlTorrentCard;
        private string _dlTorrentCardId;
        private DateTime _dlTorrentCardAt = DateTime.MinValue;
        private readonly List<int> _dlCardFileIndex = new List<int>();
        private MenuItem _dlCardPrioSkip, _dlCardPrioNormal, _dlCardPrioHigh, _dlCardPrioSep;
        private readonly Dictionary<string, DlSignatureInfo> _dlSignatures = new Dictionary<string, DlSignatureInfo>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _dlSignatureBusy = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Control BuildDownloadsCard()
        {
            Panel card = new Panel();
            card.Dock = DockStyle.Fill;

            _lblDlCard = MkNote("", false);
            _lblDlCard.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);

            _dlCardTabs = MkToolbar();
            _dlCardTabs.Padding = new Padding(0, 2, 0, 0);
            string[] titles = { Tr.S("Обзор", "Overview"), Tr.S("Сегменты", "Segments"), Tr.S("Сеть", "Network"), Tr.S("Журнал", "Log"),
                                Tr.S("Файлы", "Files"), Tr.S("Пиры", "Peers"), Tr.S("Трекеры", "Trackers") };
            int[] order = { DlCardOverview, DlCardSegments, DlCardFiles, DlCardNetwork, DlCardPeers, DlCardTrackers, DlCardJournal };
            foreach (int i in order)
            {
                RoundButton b = (RoundButton)MkFlowButton(titles[i], 90, false);
                b.Height = 30;
                b.Margin = new Padding(0, 0, 6, 6);
                int tab = i;
                b.Click += delegate { DlCardSelectTab(tab); };
                _dlCardTabs.Controls.Add(b);
                _dlCardTabButtons[i] = b;
            }

            _dlSegMap = new RamPanel();
            _dlSegMap.Dock = DockStyle.Top;
            _dlSegMap.Height = 30;
            _dlSegMap.Visible = false;
            _dlSegMap.Paint += DlPaintSegmentMap;

            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(1);
            _lvDlCard = new FastListView();
            _lvDlCard.Dock = DockStyle.Fill;
            _lvDlCard.FullRowSelect = true;
            _lvDlCard.MultiSelect = true;
            _lvDlCard.HideSelection = false;
            _lvDlCard.Columns.Add("", 200);
            _lvDlCard.Columns.Add("", 600);
            SetupOwnerDraw(_lvDlCard);
            _lvDlCard.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { e.Handled = true; DlCardCopy(); }
            };
            ContextMenu menu = new ContextMenu();
            menu.MenuItems.Add(new MenuItem(Tr.S("Копировать", "Copy"), delegate { DlCardCopy(); }));
            _dlCardPrioSep = new MenuItem("-");
            _dlCardPrioHigh = new MenuItem(Tr.S("Качать в первую очередь", "Download first"), delegate { DlCardSetPriority(2); });
            _dlCardPrioNormal = new MenuItem(Tr.S("Качать", "Download"), delegate { DlCardSetPriority(1); });
            _dlCardPrioSkip = new MenuItem(Tr.S("Не качать", "Don't download"), delegate { DlCardSetPriority(0); });
            menu.MenuItems.AddRange(new MenuItem[] { _dlCardPrioSep, _dlCardPrioHigh, _dlCardPrioNormal, _dlCardPrioSkip });
            menu.Popup += delegate
            {
                bool files = _dlCardShown == DlCardFiles && _lvDlCard.SelectedItems.Count > 0 && _dlTorrentCard != null && _dlTorrentCard.FileCount > 0;
                _dlCardPrioSep.Visible = _dlCardPrioHigh.Visible = _dlCardPrioNormal.Visible = _dlCardPrioSkip.Visible = files;
            };
            _lvDlCard.ContextMenu = menu;
            listHost.Controls.Add(_lvDlCard);

            card.Controls.Add(listHost);
            card.Controls.Add(_dlSegMap);
            card.Controls.Add(_dlCardTabs);
            card.Controls.Add(_lblDlCard);

            int saved;
            if (int.TryParse(MemGet(DlScope, "card", true), out saved) && saved >= 0 && saved < DlCardTabCount) _dlCardTab = saved;
            DlCardUpdateTabs(false);
            return card;
        }

        private void DlCardSelectTab(int tab)
        {
            _dlCardTab = tab;
            MemSet(DlScope, "card", tab.ToString(), true);
            DlCardRefresh(true);
            if (tab != DlCardOverview) DlPoll();
        }

        // Вкладка, которую видно для записи этого вида: выбор «Сегменты» у торрента показывает «Файлы» и наоборот.
        private static int DlCardTabFor(int tab, bool torrent)
        {
            if (torrent) return tab == DlCardSegments ? DlCardFiles : tab == DlCardNetwork ? DlCardPeers : tab;
            return tab == DlCardFiles ? DlCardSegments : tab == DlCardPeers || tab == DlCardTrackers ? DlCardNetwork : tab;
        }

        private void DlCardUpdateTabs(bool torrent)
        {
            _dlCardTorrentTabs = torrent;
            _dlCardShown = DlCardTabFor(_dlCardTab, torrent);
            for (int i = 0; i < _dlCardTabButtons.Length; i++)
            {
                bool visible = i == DlCardOverview || i == DlCardJournal
                               || (torrent ? i == DlCardFiles || i == DlCardPeers || i == DlCardTrackers : i == DlCardSegments || i == DlCardNetwork);
                if (_dlCardTabButtons[i].Visible != visible) _dlCardTabButtons[i].Visible = visible;
                string tag = i == _dlCardShown ? "primary" : null;
                if ((_dlCardTabButtons[i].Tag as string) != tag) _dlCardTabButtons[i].Tag = tag;
            }
            ApplyThemeTo(_dlCardTabs);
        }

        // Карточка торрента нужна одной выбранной торрент-записи на любой вкладке, кроме журнала; не чаще раза в 1,5 с.
        private string DlCardWantsTorrent()
        {
            if (_dlSetView == null || _dlSetView.Visible) return null;
            List<DlRow> sel = DlSelectedRows();
            if (sel.Count != 1 || !sel[0].Item.IsTorrent || _dlCardShown == DlCardJournal) return null;
            string id = sel[0].Item.Id;
            if (id == _dlTorrentCardId && (DateTime.UtcNow - _dlTorrentCardAt).TotalMilliseconds < 1500) return null;
            return id;
        }

        private void DlCardTorrent(string id, DlTorrentCard card)
        {
            _dlTorrentCardId = id;
            _dlTorrentCardAt = DateTime.UtcNow;
            _dlTorrentCard = card;
            DlCardRefresh(false);
        }

        // Выбор файлов из вкладки «Файлы»: весь массив приоритетов — у отмеченных новый, у остальных прежний.
        private void DlCardSetPriority(int value)
        {
            DlTorrentCard card = _dlTorrentCard;
            List<DlRow> sel = DlSelectedRows();
            if (card == null || sel.Count != 1 || !sel[0].Item.IsTorrent || _dlTorrentCardId != sel[0].Item.Id || _dlCardShown != DlCardFiles) return;
            HashSet<int> indexes = new HashSet<int>();
            foreach (int row in _lvDlCard.SelectedIndices)
                if (row < _dlCardFileIndex.Count) indexes.Add(_dlCardFileIndex[row]);
            if (indexes.Count == 0) return;
            int[] priorities = card.PrioritiesWith(indexes, value);
            JVal req = DlCommandFor("setFilePriorities", sel[0].Item.Id);
            JVal arr = JVal.NewArr();
            foreach (int p in priorities) arr.V.Add(DlJson.N(p));
            req.Set("priorities", arr);
            DlSend(req, Tr.S("Меняю выбор файлов…", "Changing the file selection…"), delegate { _dlTorrentCardAt = DateTime.MinValue; });
        }

        // Журнал живого процесса приходит отдельной командой get — только для одной выбранной записи и открытой вкладки «Журнал».
        private string DlCardWantsLog()
        {
            if (_dlCardTab != DlCardJournal || _dlSetView == null || _dlSetView.Visible) return null;
            List<string> ids = DlSelectedIds();
            return ids.Count == 1 ? ids[0] : null;
        }

        private void DlCardLog(DlRow row)
        {
            _dlCardLogRow = row;
            if (_dlCardTab == DlCardJournal) DlCardRefresh(false);
        }

        // full — выбор или вкладка сменились: список строится заново; иначе значения обновляются на месте.
        private void DlCardRefresh(bool full)
        {
            if (_lvDlCard == null) return;
            List<DlRow> sel = DlSelectedRows();
            DlRow r = null;
            if (sel.Count == 1 && _dlSnap != null) r = _dlSnap.Find(sel[0].Item.Id) ?? sel[0];

            string header;
            if (sel.Count == 0) header = Tr.S("Выберите загрузку — здесь появятся подробности.", "Select a download to see its details here.");
            else if (sel.Count > 1) header = Tr.S("Выбрано загрузок: ", "Downloads selected: ") + sel.Count;
            else header = DlView.DisplayName(r.Item);
            if (_lblDlCard.Text != header) _lblDlCard.Text = header;

            bool torrent = r != null && r.Item.IsTorrent;
            if (torrent != _dlCardTorrentTabs || _dlCardShown != DlCardTabFor(_dlCardTab, torrent)) DlCardUpdateTabs(torrent);
            // Карточка торрента — только своей записи: после смены выбора старая не показывается.
            DlTorrentCard tc = torrent && _dlTorrentCard != null && _dlTorrentCardId == r.Item.Id ? _dlTorrentCard : null;

            _dlSegMap.Visible = r != null && (_dlCardShown == DlCardSegments || (_dlCardShown == DlCardFiles && tc != null && tc.Pieces != null));
            if (_dlSegMap.Visible) _dlSegMap.Invalidate();

            List<string[]> rows = new List<string[]>();
            string[] columns;
            int[] widths;
            int flex;
            switch (_dlCardShown)
            {
                case DlCardFiles:
                    columns = new[] { Tr.S("Файл", "File"), Tr.S("Размер", "Size"), Tr.S("Готово", "Done"), Tr.S("Качать", "Download") };
                    widths = new[] { 460, 100, 150, 110 };
                    flex = 0;
                    _dlCardFileIndex.Clear();
                    if (tc != null)
                    {
                        rows.AddRange(DlTorrentView.FileRows(tc));
                        foreach (DlTorrentFile f in tc.Files) _dlCardFileIndex.Add(f.Index);
                    }
                    if (r != null && rows.Count == 0)
                        rows.Add(new[] { tc != null && !tc.HasMeta || r.Item.Total < 0 ? Tr.S("список файлов придёт вместе с метаданными от пиров", "the file list arrives with the metadata from peers")
                                                                                        : Tr.S("загружается…", "loading…"), "", "", "" });
                    break;
                case DlCardPeers:
                    columns = new[] { Tr.S("Адрес", "Address"), Tr.S("Клиент", "Client"), Tr.S("Есть у пира", "Peer has"), Tr.S("Приём", "Down"), Tr.S("Отдача", "Up"), Tr.S("Подробности", "Details") };
                    widths = new[] { 170, 150, 100, 90, 90, 320 };
                    flex = 5;
                    if (tc != null) rows.AddRange(DlTorrentView.PeerRows(tc));
                    if (r != null && rows.Count == 0)
                        rows.Add(new[] { "", "", "", "", "", tc == null ? Tr.S("загружается…", "loading…")
                                                            : tc.Running ? Tr.S("пиров пока нет — идёт поиск", "no peers yet — searching")
                                                            : Tr.S("торрент не запущен", "the torrent is not running") });
                    break;
                case DlCardTrackers:
                    columns = new[] { Tr.S("Трекер", "Tracker"), Tr.S("Состояние", "State"), Tr.S("Сиды / личи", "Seeds / leechers"), Tr.S("Пиров", "Peers"), Tr.S("Следующий запрос", "Next announce"), Tr.S("Сообщение", "Message") };
                    widths = new[] { 300, 140, 110, 70, 130, 200 };
                    flex = 0;
                    if (tc != null) rows.AddRange(DlTorrentView.TrackerRows(tc, DateTime.UtcNow));
                    if (r != null && rows.Count == 0)
                        rows.Add(new[] { tc == null ? Tr.S("загружается…", "loading…")
                                       : tc.Running ? Tr.S("у торрента нет трекеров — пиры ищутся через DHT и обмен пирами", "the torrent has no trackers — peers come from DHT and peer exchange")
                                       : Tr.S("трекеры опрашиваются, пока торрент запущен", "trackers are queried while the torrent runs"), "", "", "", "", "" });
                    break;
                case DlCardSegments:
                    columns = new[] { "#", Tr.S("Начало", "Start"), Tr.S("Конец", "End"), Tr.S("Скачано", "Downloaded"), Tr.S("Сохранено на диск", "Flushed to disk"), Tr.S("Состояние", "State") };
                    widths = new[] { 50, 110, 110, 170, 160, 130 };
                    flex = 5;
                    if (r != null) DlSegmentRows(r, rows);
                    break;
                case DlCardNetwork:
                    columns = new[] { Tr.S("Свойство", "Property"), Tr.S("Значение", "Value") };
                    widths = new[] { 210, 600 };
                    flex = 1;
                    if (r != null) DlNetworkRows(r, rows);
                    break;
                case DlCardJournal:
                    columns = new[] { Tr.S("Время", "Time"), Tr.S("Событие", "Event") };
                    widths = new[] { 210, 600 };
                    flex = 1;
                    if (r != null) DlLogRows(r, rows);
                    break;
                default:
                    columns = new[] { Tr.S("Свойство", "Property"), Tr.S("Значение", "Value") };
                    widths = new[] { 210, 600 };
                    flex = 1;
                    if (r != null) DlOverviewRows(r, rows);
                    if (tc != null) DlTorrentOverviewRows(tc, rows);
                    break;
            }

            if (_dlCardColumnsFor != _dlCardShown)
            {
                _dlCardColumnsFor = _dlCardShown;
                _lvDlCard.BeginUpdate();
                try
                {
                    _lvDlCard.Items.Clear();
                    _lvDlCard.Columns.Clear();
                    for (int i = 0; i < columns.Length; i++)
                        _lvDlCard.Columns.Add(columns[i], Px(widths[i]));
                    _flexColumn[_lvDlCard] = flex;
                }
                finally { _lvDlCard.EndUpdate(); }
                full = true;
            }

            bool same = !full && rows.Count == _lvDlCard.Items.Count;
            if (same)
            {
                for (int i = 0; i < rows.Count; i++)
                    for (int c = 0; c < columns.Length; c++)
                    {
                        string text = c < rows[i].Length ? rows[i][c] : "";
                        if (_lvDlCard.Items[i].SubItems[c].Text != text) _lvDlCard.Items[i].SubItems[c].Text = text;
                    }
                return;
            }
            _lvDlCard.BeginUpdate();
            try
            {
                _lvDlCard.Items.Clear();
                ListViewItem[] items = new ListViewItem[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    string[] cells = new string[columns.Length];
                    for (int c = 0; c < columns.Length; c++) cells[c] = c < rows[i].Length ? rows[i][c] : "";
                    items[i] = new ListViewItem(cells);
                    items[i].ForeColor = _theme.Text;
                    items[i].BackColor = _theme.Surface;
                    items[i].ToolTipText = string.Join("  ", cells);
                }
                _lvDlCard.Items.AddRange(items);
            }
            finally { _lvDlCard.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvDlCard);
        }

        private static void DlAddRow(List<string[]> rows, string name, string value)
        {
            if (!string.IsNullOrEmpty(value)) rows.Add(new[] { name, value });
        }

        private void DlOverviewRows(DlRow r, List<string[]> rows)
        {
            DlItem it = r.Item;
            DateTime now = DateTime.UtcNow;
            DlAddRow(rows, Tr.S("Состояние", "State"), DlView.StateText(r, now));
            DlAddRow(rows, Tr.S("Размер", "Size"), it.Total >= 0 ? Engine.FormatBytes(it.Total) : Tr.S("сервер не сообщил", "not reported by the server"));
            if (it.State != DlState.Completed)
            {
                double f = DlView.Fraction(r);
                DlAddRow(rows, Tr.S("Скачано", "Downloaded"), Engine.FormatBytes(r.Done) + (f >= 0 ? " (" + (int)Math.Floor(f * 100) + " %)" : ""));
            }
            if (it.State == DlState.Active)
            {
                DlAddRow(rows, Tr.S("Скорость", "Speed"), DlView.Speed(r.Speed) + (r.Connections > 0 ? " · " + r.Connections + Tr.S(" соед.", " conn.") : ""));
                DlAddRow(rows, Tr.S("Осталось", "Time left"), DlView.EtaText(r));
            }
            if (it.IsTorrent)
            {
                // У торрента нет «.wpcpart»: файлы пишутся на свои места сразу. До метаданных корень раздачи не известен.
                DlAddRow(rows, Tr.S("На диске", "On disk"), string.IsNullOrEmpty(it.FileName) || string.IsNullOrEmpty(it.Folder) ? Tr.S("станет известно с метаданными", "known once the metadata arrives")
                                                             : it.TargetPath + (Directory.Exists(it.TargetPath) || File.Exists(it.TargetPath) ? "" : Tr.S(" — ещё не создан", " — not created yet")));
                DlAddRow(rows, Tr.S("Отдано", "Uploaded"), Engine.FormatBytes(Interlocked.Read(ref it.Uploaded)));
                DlAddRow(rows, Tr.S("Тема раздачи", "Topic"), it.TopicUrl);
                DlAddRow(rows, Tr.S("Новая версия", "New version"), DlTorrentView.UpdateStateText(it, now));
            }
            else
            {
                // До ответа сервера у записи нет ни папки, ни имени: PartPath был бы голым «.wpcpart».
                DlAddRow(rows, Tr.S("Файл", "File"), string.IsNullOrEmpty(it.FileName) || string.IsNullOrEmpty(it.Folder) ? Tr.S("ещё не создан", "not created yet")
                                                     : it.State == DlState.Completed ? it.TargetPath : it.PartPath);
            }
            if (it.MoveTo.Length > 0) DlAddRow(rows, it.MoveCopy ? Tr.S("Копируется в", "Copying to") : Tr.S("Переносится в", "Moving to"), it.MoveTo);
            DlAddRow(rows, Tr.S("Сайт", "Site"), it.Host);
            DlAddRow(rows, Tr.S("Источник", "Source"), DlView.SourceTitle(it.Source));
            if (!it.IsTorrent) DlAddRow(rows, Tr.S("Тип", "Type"), DlView.CategoryTitle(DlView.CategoryOf(it.FileName)));
            DlAddRow(rows, Tr.S("Добавлено", "Added"), DlView.Local(it.AddedUtc, now));
            DlAddRow(rows, Tr.S("Готово", "Finished"), DlView.Local(it.CompletedUtc, now));
            if (it.StartAtUtc != DateTime.MinValue && it.State == DlState.Scheduled) DlAddRow(rows, Tr.S("Начнётся", "Starts"), DlView.Local(it.StartAtUtc, now));
            if (it.State != DlState.Completed)
            {
                DlAddRow(rows, Tr.S("Лимит скорости", "Speed limit"), DlView.Limit(it.LimitKBps));
                DlAddRow(rows, Tr.S("Приоритет", "Priority"), it.Priority > 0 ? Tr.S("высокий", "high") : it.Priority < 0 ? Tr.S("низкий", "low") : Tr.S("обычный", "normal"));
                if (it.WhenIdle) DlAddRow(rows, Tr.S("Когда качать", "When"), Tr.S("только когда ПК простаивает", "only while the PC is idle"));
                if (it.Connections > 0 && !it.IsTorrent) DlAddRow(rows, Tr.S("Потоков", "Streams"), it.Connections.ToString());
                if (it.Sequential) DlAddRow(rows, Tr.S("Порядок", "Order"), Tr.S("куски по порядку — файл можно смотреть, пока качается", "pieces in order — the file can be watched while downloading"));
                if (it.Attempts > 0) DlAddRow(rows, Tr.S("Попыток", "Attempts"), it.Attempts.ToString()
                    + (it.NextRetryUtc > now ? Tr.S(" · следующая в ", " · next at ") + DlView.Local(it.NextRetryUtc, now) : ""));
            }
            if (it.Error.Length > 0) DlAddRow(rows, Tr.S("Последняя ошибка", "Last error"), it.Error);
            DlAddRow(rows, Tr.S("Ожидаемый хеш", "Expected hash"), it.ExpectedHash);

            if (it.State == DlState.Completed && it.MoveTo.Length == 0 && !it.IsTorrent)
            {
                string path = it.TargetPath;
                bool exists = File.Exists(path);
                if (!exists) DlAddRow(rows, Tr.S("На диске", "On disk"), Tr.S("файла больше нет", "the file is gone"));
                else if (DlView.OpenNeedsConfirm(it.FileName)) DlAddRow(rows, Tr.S("Подпись", "Signature"), DlSignatureText(path));
                if (exists)
                {
                    string zone = DlVerify.ZoneText(path);
                    DlAddRow(rows, Tr.S("Метка «из интернета»", "Internet mark"), zone.Length > 0 ? zone : Tr.S("нет", "none"));
                }
            }
        }

        // Строки обзора из карточки торрента: статистика, раздача, метаданные, сеть.
        private static void DlTorrentOverviewRows(DlTorrentCard c, List<string[]> rows)
        {
            if (c.HasStats)
            {
                DlAddRow(rows, Tr.S("Принято за всё время", "Downloaded in total"), Engine.FormatBytes(c.Downloaded)
                    + (c.Wasted > 0 ? Tr.S(" · отброшено ", " · discarded ") + Engine.FormatBytes(c.Wasted) : ""));
                DlAddRow(rows, Tr.S("Рейтинг", "Ratio"), c.Ratio.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                if (c.PeerCount > 0 || c.Running) DlAddRow(rows, Tr.S("Пиры", "Peers"), c.PeerCount + Tr.S(", из них сидов ", ", seeds ") + c.SeedCount);
                if (c.ActiveSeconds > 0) DlAddRow(rows, Tr.S("В работе", "Active for"), DlView.Duration(c.ActiveSeconds)
                    + (c.SeedSeconds > 0 ? Tr.S(" · раздавался ", " · seeded ") + DlView.Duration(c.SeedSeconds) : ""));
            }
            int have = c.PiecesHave();
            if (c.PieceCount > 0)
                DlAddRow(rows, Tr.S("Куски", "Pieces"), (have >= 0 ? have + " / " : "") + c.PieceCount + " × " + Engine.FormatBytes(c.PieceLength));
            if (c.Running && c.Availability > 0)
                DlAddRow(rows, Tr.S("Доступность", "Availability"), c.Availability.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
                    + (c.Availability < 1 ? Tr.S(" — у подключённых пиров есть не все куски", " — the connected peers lack some pieces") : ""));
            DlAddRow(rows, "Info-hash", c.Hash);
            if (c.HasMeta) DlAddRow(rows, Tr.S("Формат", "Format"), DlTorrentView.VersionText(c.Version));
            if (c.Private) DlAddRow(rows, Tr.S("Закрытый", "Private"), Tr.S("да — пиры только от трекера, без DHT и обмена пирами", "yes — peers from the tracker only, no DHT or peer exchange"));
            DlAddRow(rows, Tr.S("Комментарий", "Comment"), c.Comment);
            DlAddRow(rows, Tr.S("Создан программой", "Created by"), c.CreatedBy);
            if (c.CreatedUtc != DateTime.MinValue) DlAddRow(rows, Tr.S("Создан", "Created"), DlView.Local(c.CreatedUtc, DateTime.UtcNow));
            string session = DlTorrentView.SessionText(c);
            if (session.Length > 0)
            {
                DlAddRow(rows, Tr.S("Сеть", "Network"), session);
                if (!c.Inbound)
                    DlAddRow(rows, "", Tr.S("пиры не могут подключиться к вам сами — откройте входящие в «Настройки → Торренты»",
                                            "peers cannot connect to you — allow incoming connections in Settings → Torrents"));
            }
        }

        // Подпись считается один раз на файл (путь + время изменения), в фоне; пока не готова — «проверяется…».
        private string DlSignatureText(string path)
        {
            string key;
            try { key = path + "|" + File.GetLastWriteTimeUtc(path).Ticks; }
            catch { return ""; }
            DlSignatureInfo info;
            if (_dlSignatures.TryGetValue(key, out info)) return DlVerify.Describe(info);
            if (_dlSignatureBusy.Add(key))
                ThreadPool.QueueUserWorkItem(delegate
                {
                    DlSignatureInfo result = DlVerify.Check(path);
                    UiPost(delegate
                    {
                        _dlSignatureBusy.Remove(key);
                        if (_dlSignatures.Count > 200) _dlSignatures.Clear();
                        _dlSignatures[key] = result;
                        if (_dlCardTab == DlCardOverview) DlCardRefresh(false);
                    });
                });
            return Tr.S("проверяется…", "checking…");
        }

        private static void DlSegmentRows(DlRow r, List<string[]> rows)
        {
            List<DlSegment> segs;
            lock (r.Item.Segments) segs = new List<DlSegment>(r.Item.Segments);
            for (int i = 0; i < segs.Count; i++)
            {
                DlSegment s = segs[i];
                string state = s.Finished ? Tr.S("готов", "done") : s.Busy ? Tr.S("качается", "downloading") : s.Done > 0 ? Tr.S("начат", "started") : Tr.S("ждёт", "waiting");
                rows.Add(new[]
                {
                    (i + 1).ToString(), Engine.FormatBytes(s.Start), s.End < 0 ? Tr.S("до конца", "to the end") : Engine.FormatBytes(s.End + 1),
                    Engine.FormatBytes(s.Done) + (s.Length > 0 ? " (" + (int)Math.Floor(100.0 * s.Done / s.Length) + " %)" : ""),
                    Engine.FormatBytes(s.Durable), state
                });
            }
            if (segs.Count == 0)
                rows.Add(new[] { "", "", "", "", "", r.Item.State == DlState.Completed ? Tr.S("файл собран", "the file is assembled") : Tr.S("сегментов ещё нет — размер не известен", "no segments yet — the size is unknown") });
        }

        private void DlPaintSegmentMap(object sender, PaintEventArgs e)
        {
            e.Graphics.Clear(_theme.Bg);
            List<DlRow> sel = DlSelectedRows();
            if (sel.Count != 1 || _dlSnap == null) return;
            DlRow r = _dlSnap.Find(sel[0].Item.Id) ?? sel[0];
            DlItem it = r.Item;
            Rectangle bar = new Rectangle(0, Px(6), _dlSegMap.ClientSize.Width - Px(2), _dlSegMap.ClientSize.Height - Px(12));
            if (bar.Width < 10 || bar.Height < 4) return;
            e.Graphics.FillRectangle(RowBrush(_theme.Header), bar);
            if (it.IsTorrent)
            {
                // Карта кусков: столбик пикселя — доля проверенных кусков в его отрезке, от фона к акценту.
                DlTorrentCard tc = _dlTorrentCard;
                if (tc == null || _dlTorrentCardId != it.Id || tc.Pieces == null || tc.PieceCount <= 0) return;
                int step = Math.Max(1, Px(2));
                for (int x = 0; x < bar.Width; x += step)
                {
                    int from = (int)((long)x * tc.PieceCount / bar.Width);
                    int to = Math.Max(from + 1, (int)((long)Math.Min(bar.Width, x + step) * tc.PieceCount / bar.Width));
                    double f = tc.PiecesFraction(from, to);
                    if (f <= 0) continue;
                    Color c = f >= 1 ? _theme.Accent : Mix(_theme.Accent, _theme.Header, (float)(1 - 0.25 - 0.75 * f));
                    e.Graphics.FillRectangle(RowBrush(c), bar.X + x, bar.Y, Math.Min(step, bar.Width - x), bar.Height);
                }
                return;
            }
            if (it.State == DlState.Completed) { e.Graphics.FillRectangle(RowBrush(HealthLevelColor(HealthLevel.Ok)), bar); return; }
            if (it.Total <= 0) return;
            List<DlSegment> segs;
            lock (it.Segments) segs = new List<DlSegment>(it.Segments);
            Color done = _theme.Accent, durable = Mix(_theme.Accent, _theme.Bg, 0.35f);
            foreach (DlSegment s in segs)
            {
                int x0 = bar.X + (int)((double)s.Start / it.Total * bar.Width);
                int xd = bar.X + (int)((double)Math.Min(it.Total, s.Start + s.Durable) / it.Total * bar.Width);
                int x1 = bar.X + (int)((double)Math.Min(it.Total, s.Start + s.Done) / it.Total * bar.Width);
                if (xd > x0) e.Graphics.FillRectangle(RowBrush(durable), x0, bar.Y, xd - x0, bar.Height);
                if (x1 > xd) e.Graphics.FillRectangle(RowBrush(done), xd, bar.Y, x1 - xd, bar.Height);
                if (s.Start > 0) e.Graphics.FillRectangle(RowBrush(_theme.Bg), x0, bar.Y, Math.Max(1, Px(1)), bar.Height);
            }
        }

        private static void DlNetworkRows(DlRow r, List<string[]> rows)
        {
            DlItem it = r.Item;
            DlAddRow(rows, Tr.S("Ссылка", "Link"), DlLog.Redact(it.Url));
            if (it.OriginalUrl.Length > 0 && it.OriginalUrl != it.Url) DlAddRow(rows, Tr.S("Исходная ссылка", "Original link"), DlLog.Redact(it.OriginalUrl));
            if (it.FinalUrl.Length > 0 && it.FinalUrl != it.Url) DlAddRow(rows, Tr.S("После переадресаций", "After redirects"), DlLog.Redact(it.FinalUrl));
            List<string> redirects;
            lock (it.Redirects) redirects = new List<string>(it.Redirects);
            for (int i = 0; i < redirects.Count; i++) DlAddRow(rows, Tr.S("Переадресация ", "Redirect ") + (i + 1), DlLog.Redact(redirects[i]));
            List<string> mirrors;
            lock (it.Mirrors) mirrors = new List<string>(it.Mirrors);
            for (int i = 0; i < mirrors.Count; i++) DlAddRow(rows, Tr.S("Зеркало ", "Mirror ") + (i + 1), DlLog.Redact(mirrors[i]));
            DlAddRow(rows, Tr.S("Откуда пришла", "Referrer"), DlLog.Redact(it.Referrer));
            DlAddRow(rows, Tr.S("Страница", "Page"), DlLog.Redact(it.PageUrl));
            DlAddRow(rows, "User-Agent", it.UserAgent);
            DlAddRow(rows, "Cookies", r.HasCookies ? Tr.S("есть (только в памяти процесса)", "present (process memory only)") : Tr.S("нет", "none"));
            if (it.Total >= 0 || it.AcceptRanges)
                DlAddRow(rows, Tr.S("Докачка", "Resume"), it.AcceptRanges ? Tr.S("сервер поддерживает", "supported by the server") : Tr.S("сервер не поддерживает — обрыв начнёт файл заново", "not supported — a break restarts the file"));
            DlAddRow(rows, "ETag", it.ETag);
            DlAddRow(rows, "Last-Modified", it.LastModified);
            if (it.AllowHttpDowngrade) DlAddRow(rows, Tr.S("Переход на http", "Downgrade to http"), Tr.S("разрешён пользователем", "allowed by the user"));
            if (it.ErrorKind != DlErrorKind.None) DlAddRow(rows, Tr.S("Вид ошибки", "Error kind"), it.ErrorKind.ToString());
        }

        private void DlLogRows(DlRow r, List<string[]> rows)
        {
            DlItem source = r.Item;
            if (_dlSnap != null && _dlSnap.Live)
            {
                if (_dlCardLogRow == null || _dlCardLogRow.Item.Id != r.Item.Id)
                {
                    rows.Add(new[] { "", Tr.S("загружается…", "loading…") });
                    return;
                }
                source = _dlCardLogRow.Item;
            }
            List<DlEvent> events;
            lock (source.Events) events = new List<DlEvent>(source.Events);
            DateTime now = DateTime.UtcNow;
            for (int i = events.Count - 1; i >= 0; i--) rows.Add(new[] { DlView.Local(events[i].Utc, now), events[i].Text });
            if (events.Count == 0) rows.Add(new[] { "", Tr.S("событий нет", "no events") });
        }

        private void DlCardCopy()
        {
            List<string> lines = new List<string>();
            foreach (ListViewItem it in _lvDlCard.SelectedItems)
            {
                List<string> cells = new List<string>();
                foreach (ListViewItem.ListViewSubItem s in it.SubItems) cells.Add(s.Text);
                lines.Add(string.Join("\t", cells.ToArray()));
            }
            if (lines.Count == 0) return;
            try { Clipboard.SetText(string.Join("\r\n", lines.ToArray())); } catch { }
        }
    }
}
