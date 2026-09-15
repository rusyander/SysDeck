// SysDeck — вкладка «Диск»: карта папок, крупные файлы, пустые папки, дубликаты
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        // ---------- Вкладка: Диск ----------
        // Что занимает место — тот же ответ, что дают WizTree/TreeSize, но без MFT-магии:
        // один проход FindFirstFileEx по выбранной области, дерево папок с размерами,
        // крупные файлы, пустые папки и дубликаты. Удаление — только в Корзину.
        private TreeView _tvDisk;
        private ListView _lvDisk;
        private Label _lblDiskStatus;
        private ComboBox _cmbDiskScope;
        private Panel _diskBars;
        private Button _btnDiskScan, _btnDiskStop, _btnDiskFiles, _btnDiskEmpty, _btnDiskDups;
        private FlowLayoutPanel _diskFlow;
        private NumericUpDown _numDiskMin;         // порог «крупного файла» и дубликатов, МБ (AppConfig.DiskMinMb)
        private string _diskNote = "";             // подпись списка — возвращается, когда снята последняя галочка
        private List<DriveRow> _drives;            // кэш полосок дисков: DriveInfo не опрашивается на каждой перерисовке
        private DateTime _drivesAt;
        private long _dupMin;                      // порог, с которым посчитаны _dupGroups
        private DiskScanResult _diskScan;
        private int _diskScanBusy;                 // 0/1 — идёт обход или поиск дубликатов
        private string _diskMode = "files";        // files | empty | dups
        private List<DupGroup> _dupGroups;
        private DiskDir _dupScope;                 // для какого узла посчитаны _dupGroups
        private bool _dupCancelled;                // поиск дубликатов прерван кнопкой «Стоп» — список неполный
        private DiskDir _diskSelected;
        private int _diskScopeLast;                // индекс до выбора «Другая папка…» (откат при отмене)
        private DateTime _diskProgressAt = DateTime.MinValue;
        private string _diskStartPath;             // /disk <путь>: просканировать сразу после показа окна
        private bool _diskStartPage;

        // Живая строка состояния страницы: фаза + секундомер + подробности от рабочего потока.
        // Без секундомера длинные фазы выглядели зависанием: у поиска дубликатов сравнение
        // «голов» файлов не даёт ни одного отчёта о прогрессе, а один большой файл в полном
        // хэшировании держит счётчик байт неподвижным минутами.
        private System.Windows.Forms.Timer _diskTick;
        private DateTime _diskStarted;
        private string _diskPhase;
        private volatile string _diskDetail;       // пишет рабочий поток, читает таймер UI
        private string _diskDeferred;              // что применится после текущей операции
        private volatile bool _recycleCancel;      // «Стоп» во время переноса в Корзину
        private bool _suspendDiskChecked;          // идёт массовая простановка галочек — считаем один раз
        private bool _dupSearching;                // занят именно поиск дубликатов (а не обход/перенос)
        private bool _dupRerun;                    // за время поиска выбрали другую папку — пересчитать
        private bool _diskMinPending;              // порог меняли во время работы — применить, когда освободимся
        private int _diskListRun;                  // номер последнего запроса списка: ответы старых выбрасываем
        private bool _diskListQuiet;               // список пересобирается после обхода/переноса: их итог в строке важнее
        private int _drivesBusy;                   // идёт фоновое чтение полосок дисков
        private System.Windows.Forms.Timer _diskListTimer, _diskMinTimer;

        private const int DiskListLimit = 500;
        // Перенос в Корзину идёт порциями: оболочка вызывается с FOF_SILENT, своего окна
        // прогресса не показывает, и на тысяче путей одна операция висела молча и без отмены.
        private const int RecycleChunk = 200;

        private class ScopeItem
        {
            public string Text; public string Path; public bool Browse;
            public override string ToString() { return Text; }
        }

        public void SetDiskStart(string path) { _diskStartPage = true; _diskStartPath = path; }

        // /disk <путь> от повторного запуска (например, «Открыть в карте диска» у «Размеров папок»):
        // показать окно, открыть «Диск» и просканировать путь, если сейчас ничего не идёт.
        public void OpenDiskScan(string path)
        {
            ShowWindow();
            ShowPage(PageDisk);
            bool exists = false;
            try { exists = Directory.Exists(path); } catch { }
            if (!exists) return;
            if (_diskScanBusy != 0)
            {
                _lblDiskStatus.Text = Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish.");
                return;
            }
            FillDiskScopes(path);
            DoDiskScan();
        }

        private Control BuildDiskTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            _diskFlow = new FlowLayoutPanel();
            _diskFlow.Dock = DockStyle.Top;
            _diskFlow.AutoSize = true;
            _diskFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _diskFlow.Padding = new Padding(0, 6, 0, 0);
            _diskFlow.WrapContents = true;

            Label lblScope = new Label();
            lblScope.Text = Tr.S("Где искать:", "Scan:");
            lblScope.AutoSize = true;
            lblScope.Margin = new Padding(0, 9, 6, 0);
            _diskFlow.Controls.Add(lblScope);

            _cmbDiskScope = new RoundComboBox();
            _cmbDiskScope.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDiskScope.Width = 330;
            _cmbDiskScope.Margin = new Padding(0, 5, 12, 8);
            FillDiskScopes(null);
            _cmbDiskScope.SelectedIndexChanged += delegate { DiskScopeChanged(); };
            _diskFlow.Controls.Add(_cmbDiskScope);

            _btnDiskScan = MkFlowButton(Tr.S("Сканировать", "Scan"), 140, true);
            _btnDiskScan.Click += delegate { DoDiskScan(); };
            _diskFlow.Controls.Add(_btnDiskScan);
            _btnDiskStop = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnDiskStop.Enabled = false;
            _btnDiskStop.Click += delegate { DiskStopClicked(); };
            _diskFlow.Controls.Add(_btnDiskStop);

            // Порог «крупного файла» (и дубликатов): ниже 1 МБ смысла нет — мелких одинаковых
            // файлов тысячи и все легитимны; выше — короче список и быстрее поиск дубликатов.
            Label lblMin = new Label();
            lblMin.Text = Tr.S("Файлы от", "Files from");
            lblMin.AutoSize = true;
            lblMin.Margin = new Padding(10, 9, 6, 0);
            _diskFlow.Controls.Add(lblMin);
            _numDiskMin = new NumericUpDown();
            _numDiskMin.Minimum = 1; _numDiskMin.Maximum = 10240; _numDiskMin.Increment = 1;
            _numDiskMin.Width = 70;
            _numDiskMin.Margin = new Padding(0, 6, 6, 8);
            _numDiskMin.Value = Math.Max(1, Math.Min(10240, _engine.Config.DiskMinMb));
            _numDiskMin.ValueChanged += delegate { DiskMinChanged(); };
            _diskFlow.Controls.Add(_numDiskMin);
            Label lblMb = new Label();
            lblMb.Text = Tr.S("МБ", "MB");
            lblMb.AutoSize = true;
            lblMb.Margin = new Padding(0, 9, 0, 0);
            _diskFlow.Controls.Add(lblMb);
            _diskFlow.SetFlowBreak(lblMb, true);   // вторая строка: режимы и действия

            _btnDiskFiles = MkFlowButton(Tr.S("Крупные файлы", "Large files"), 150, true);   // режим по умолчанию
            _btnDiskFiles.Margin = new Padding(0, 0, 8, 8);
            _btnDiskFiles.Click += delegate { SetDiskMode("files"); };
            _diskFlow.Controls.Add(_btnDiskFiles);
            _btnDiskEmpty = MkFlowButton(Tr.S("Пустые папки", "Empty folders"), 140, false);
            _btnDiskEmpty.Click += delegate { SetDiskMode("empty"); };
            _diskFlow.Controls.Add(_btnDiskEmpty);
            _btnDiskDups = MkFlowButton(Tr.S("Дубликаты", "Duplicates"), 120, false);
            _btnDiskDups.Click += delegate { SetDiskMode("dups"); };
            _diskFlow.Controls.Add(_btnDiskDups);
            // режим списка запоминается; список ещё пуст, поэтому только подсветка кнопки,
            // без RefreshDiskList — его вызовет первый выбор папки в дереве
            string savedMode = MemGet("disk", "mode", true);
            if (savedMode == "empty" || savedMode == "dups")
            {
                _diskMode = savedMode;
                _btnDiskFiles.Tag = null;
                (savedMode == "empty" ? _btnDiskEmpty : _btnDiskDups).Tag = "primary";
            }

            Button btnOpen = MkFlowButton(Tr.S("Открыть папку", "Open folder"), 140, false);
            btnOpen.Margin = new Padding(16, 0, 8, 8);
            btnOpen.Click += delegate { OpenDiskSelection(); };
            _diskFlow.Controls.Add(btnOpen);
            Button btnRecycle = MkFlowButton(Tr.S("В Корзину", "Recycle"), 120, false);
            btnRecycle.Click += delegate { RecycleDiskSelection(); };
            _diskFlow.Controls.Add(btnRecycle);
            Button btnAll = MkFlowButton(Tr.S("Все", "All"), 70, false);
            btnAll.Click += delegate { SetDiskChecks(true); };
            _diskFlow.Controls.Add(btnAll);
            Button btnNone = MkFlowButton(Tr.S("Ничего", "None"), 90, false);
            btnNone.Click += delegate { SetDiskChecks(false); };
            _diskFlow.Controls.Add(btnNone);

            // Полоски занятости дисков — рисуются сами, чтобы жить в обеих темах.
            _diskBars = new Panel();
            _diskBars.Dock = DockStyle.Top;
            _diskBars.Height = 46;
            _diskBars.Paint += DiskBars_Paint;
            _diskBars.Resize += delegate { _diskBars.Invalidate(); };

            _lblDiskStatus = new Label();
            _lblDiskStatus.Dock = DockStyle.Top;
            _lblDiskStatus.Height = 24;
            _lblDiskStatus.AutoEllipsis = true;
            _lblDiskStatus.Text = Tr.S("Выберите диск или папку и нажмите «Сканировать». Удаление — только в Корзину.",
                                       "Pick a drive or folder and click “Scan”. Deletion always goes to the Recycle Bin.");

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            split.Size = new Size(1000, 500);       // размер ПЕРВЫМ, иначе Panel2MinSize бросает (см. Браузеры)
            split.Panel1MinSize = 200;
            split.Panel2MinSize = 300;
            split.FixedPanel = FixedPanel.Panel1;
            split.SplitterDistance = 390;
            split.Panel1.Padding = new Padding(1);   // место под скруглённую рамку (Boxed)
            split.Panel2.Padding = new Padding(1);

            _tvDisk = new TreeView();
            _tvDisk.Dock = DockStyle.Fill;
            _tvDisk.HideSelection = false;
            _tvDisk.BorderStyle = BorderStyle.FixedSingle;
            _tvDisk.ShowLines = true;
            SetupOwnerDraw(_tvDisk);
            _tvDisk.BeforeExpand += delegate(object s, TreeViewCancelEventArgs e) { ExpandDiskNode(e.Node); };
            _tvDisk.AfterSelect += delegate(object s, TreeViewEventArgs e)
            {
                _diskSelected = e.Node == null ? null : e.Node.Tag as DiskDir;
                ScheduleDiskList();
            };
            _tvDisk.NodeMouseDoubleClick += delegate(object s, TreeNodeMouseClickEventArgs e)
            {
                DiskDir d = e.Node == null ? null : e.Node.Tag as DiskDir;
                if (d != null) OpenInExplorer(d.Path, false);
            };
            split.Panel1.Controls.Add(_tvDisk);

            _lvDisk = new FastListView();
            _lvDisk.Dock = DockStyle.Fill;
            _lvDisk.View = View.Details;
            _lvDisk.CheckBoxes = true;
            MemWatch(_lvDisk, DiskScope, false, DiskMemKey);
            _lvDisk.FullRowSelect = true;
            _lvDisk.Columns.Add(Tr.S("Имя", "Name"), 260);
            _lvDisk.Columns.Add(Tr.S("Размер", "Size"), 100);
            _lvDisk.Columns.Add(Tr.S("Изменён", "Modified"), 110);
            _lvDisk.Columns.Add(Tr.S("Папка", "Folder"), 300);
            SetupOwnerDraw(_lvDisk);
            _pathColumns[_lvDisk] = 3;
            _lvDisk.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                ListViewItem hit = _lvDisk.GetItemAt(e.X, e.Y);
                if (hit != null) OpenDiskRow(hit);
            };
            _lvDisk.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && _lvDisk.SelectedItems.Count > 0) { e.Handled = true; OpenDiskRow(_lvDisk.SelectedItems[0]); }
                if (e.KeyCode == Keys.Delete) { e.Handled = true; RecycleDiskSelection(); }
            };
            // BeginUpdate не гасит ItemChecked, поэтому «Все»/«Ничего» пересчитывали отметки на
            // каждой строке, а каждый пересчёт проходит весь список — на дубликатах (список
            // ничем не ограничен) это квадрат от числа строк. Пересчёт один раз в конце.
            _lvDisk.ItemChecked += delegate
            {
                if (!_suspendDiskChecked && _diskScan != null && _diskScanBusy == 0) UpdateDiskChecked();
            };
            split.Panel2.Controls.Add(_lvDisk);

            tab.Controls.Add(split);
            tab.Controls.Add(_lblDiskStatus);
            tab.Controls.Add(_diskBars);
            tab.Controls.Add(_diskFlow);
            return tab;
        }

        private void FillDiskScopes(string customPath)
        {
            _cmbDiskScope.Items.Clear();
            if (!string.IsNullOrEmpty(customPath))
            {
                ScopeItem cu = new ScopeItem(); cu.Text = customPath; cu.Path = customPath;
                _cmbDiskScope.Items.Add(cu);
            }
            foreach (DriveRow d in DrivesCached())
            {
                ScopeItem it = new ScopeItem();
                it.Path = d.Name;
                it.Text = d.Name + (string.IsNullOrEmpty(d.Label) ? "" : " " + d.Label)
                        + "  —  " + Engine.FormatBytes(d.Total) + Tr.S(", свободно ", ", free ") + Engine.FormatBytes(d.Free);
                _cmbDiskScope.Items.Add(it);
            }
            AddScopeFolder(Tr.S("Профиль пользователя", "User profile"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            AddScopeFolder(Tr.S("Загрузки", "Downloads"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"));
            AddScopeFolder(Tr.S("Документы", "Documents"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            AddScopeFolder(Tr.S("Рабочий стол", "Desktop"), Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            ScopeItem br = new ScopeItem(); br.Text = Tr.S("Другая папка…", "Other folder…"); br.Browse = true;
            _cmbDiskScope.Items.Add(br);
            // «Где искать» тоже помнится: раньше список пересобирался при каждом входе на
            // вкладку и выбор всегда откатывался на первый диск.
            int want = 0;
            if (string.IsNullOrEmpty(customPath))
            {
                string saved = MemGet(DiskScopeScope, "path", true);
                if (!string.IsNullOrEmpty(saved))
                    for (int i = 0; i < _cmbDiskScope.Items.Count; i++)
                    {
                        ScopeItem si = _cmbDiskScope.Items[i] as ScopeItem;
                        if (si != null && !si.Browse && string.Equals(si.Path, saved, StringComparison.OrdinalIgnoreCase))
                        { want = i; break; }
                    }
            }
            if (_cmbDiskScope.Items.Count > 0) _cmbDiskScope.SelectedIndex = want;
            _diskScopeLast = want;
        }

        private const string DiskScopeScope = "disk.scope";

        private void AddScopeFolder(string title, string path)
        {
            try { if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return; } catch { return; }
            ScopeItem it = new ScopeItem(); it.Text = title + "  —  " + path; it.Path = path;
            _cmbDiskScope.Items.Add(it);
        }

        private void DiskScopeChanged()
        {
            ScopeItem it = _cmbDiskScope.SelectedItem as ScopeItem;
            if (it == null) return;
            if (!it.Browse)
            {
                _diskScopeLast = _cmbDiskScope.SelectedIndex;
                MemSet(DiskScopeScope, "path", it.Path, true);
                return;
            }
            if (BrowseDiskScope()) return;
            _cmbDiskScope.SelectedIndex = Math.Min(_diskScopeLast, _cmbDiskScope.Items.Count - 1);
        }

        // Выбор произвольной папки. Общий код для пункта «Другая папка…» и для «Сканировать»,
        // нажатого, когда комбо так на нём и осталось. true — папка выбрана и уже подставлена.
        private bool BrowseDiskScope()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Папка для анализа занятого места", "Folder to analyze");
                dlg.ShowNewFolderButton = false;
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return false;
                FillDiskScopes(dlg.SelectedPath);
                return true;
            }
        }

        private string DiskScopePath()
        {
            ScopeItem it = _cmbDiskScope.SelectedItem as ScopeItem;
            return it == null || it.Browse ? null : it.Path;
        }

        // ---------- Секундомер и «Стоп» ----------
        private void StartDiskTicker(string phase)
        {
            _diskPhase = phase;
            _diskDetail = null;
            _diskDeferred = null;
            _diskStarted = DateTime.UtcNow;
            if (_diskTick == null)
            {
                _diskTick = new System.Windows.Forms.Timer();
                _diskTick.Interval = 500;
                _diskTick.Tick += delegate { DiskTick(); };
            }
            _diskTick.Start();
            DiskTick();
        }

        private void StopDiskTicker()
        {
            if (_diskTick != null) _diskTick.Stop();
        }

        private void DiskTick()
        {
            string d = _diskDetail;
            // Elapsed() объявлен на вкладке очистки — класс один, формат секундомера общий
            _lblDiskStatus.Text = _diskPhase + "   ·   " + Elapsed(DateTime.UtcNow - _diskStarted)
                + (string.IsNullOrEmpty(d) ? "" : "   ·   " + d)
                + (string.IsNullOrEmpty(_diskDeferred) ? "" : "   ·   " + _diskDeferred);
        }

        // «Стоп» не обрывает работу мгновенно: обход доходит до ближайшей проверки флага,
        // перенос в Корзину — до конца текущей порции, хэш текущего файла дочитывается.
        // Кнопка гаснет, фаза меняется на «Останавливаю…» — иначе нажатие выглядело
        // проигнорированным: строка ещё несколько секунд отсчитывала «Сканирование…».
        private void DiskStopClicked()
        {
            _engine.CancelDiskScan();
            _recycleCancel = true;
            _btnDiskStop.Enabled = false;
            _diskPhase = Tr.S("Останавливаю…", "Stopping…");
            if (_diskTick != null && _diskTick.Enabled) DiskTick();
        }

        // ---------- Сканирование ----------
        private void DoDiskScan()
        {
            string path = DiskScopePath();
            // Комбо осталось на пункте «Другая папка…»: раньше клик по «Сканировать» молча
            // не делал ничего — теперь сразу предлагается выбрать папку.
            if (string.IsNullOrEmpty(path))
            {
                if (!BrowseDiskScope())
                {
                    _lblDiskStatus.Text = Tr.S("Папка не выбрана — укажите диск или папку в списке «Где искать».",
                                               "No folder chosen — pick a drive or folder in “Scan”.");
                    return;
                }
                path = DiskScopePath();
                if (string.IsNullOrEmpty(path)) return;
            }
            if (Interlocked.CompareExchange(ref _diskScanBusy, 1, 0) != 0)
            {
                _lblDiskStatus.Text = _dupSearching
                    ? Tr.S("Идёт поиск дубликатов — нажмите «Стоп», чтобы прервать его и начать обход.",
                           "A duplicate search is running — click “Stop” to interrupt it and start a scan.")
                    : Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish.");
                return;
            }
            _engine.ResetDiskScanCancel();
            _recycleCancel = false;
            _diskScan = null; _dupGroups = null; _dupScope = null; _diskSelected = null;
            _tvDisk.Nodes.Clear();
            FillDiskRows(new List<ListViewItem>());
            _btnDiskStop.Enabled = true;
            _btnDiskScan.Enabled = false;
            StartDiskTicker(Tr.S("Сканирование ", "Scanning ") + path);
            long minBytes = DiskMinBytes;

            Thread t = new Thread(delegate()
            {
                DiskScanResult r = null;
                string err = null;
                try
                {
                    // Строка состояния собирается таймером UI: отправлять BeginInvoke на каждый
                    // отчёт обхода незачем — рабочий поток только кладёт текст в поле.
                    r = _engine.ScanDisk(path, minBytes, delegate(long bytes, int files, int dirs, string current)
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _diskProgressAt).TotalMilliseconds < 250) return;
                        _diskProgressAt = now;
                        _diskDetail = Engine.FormatBytes(bytes)
                            + Tr.S("  ·  файлов: ", "  ·  files: ") + files.ToString("N0", CultureInfo.CurrentCulture)
                            + Tr.S("  ·  папок: ", "  ·  folders: ") + dirs.ToString("N0", CultureInfo.CurrentCulture)
                            + "  ·  " + current;
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                Interlocked.Exchange(ref _diskScanBusy, 0);
                UiPost(delegate
                {
                    StopDiskTicker();
                    _btnDiskStop.Enabled = false;
                    _btnDiskScan.Enabled = true;
                    if (r == null)
                    {
                        _lblDiskStatus.Text = Tr.S("Сканирование не удалось", "Scan failed") + (err != null ? ": " + err : ".");
                        return;
                    }
                    _diskScan = r;
                    _diskListQuiet = true;      // итог обхода в строке важнее подписи списка
                    BuildDiskTree(r);
                    UpdateDiskStatus();
                    ApplyPendingDiskMin();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string DirCaption(DiskDir d, bool root)
        {
            string pct = "";
            if (!root && d.Parent != null && d.Parent.Size > 0)
            {
                double p = 100.0 * d.Size / d.Parent.Size;
                pct = "  ·  " + (p < 1 ? "<1" : ((int)Math.Round(p)).ToString()) + "%";
            }
            return (root ? d.Path : d.Name) + "  —  " + Engine.FormatBytes(d.Size) + pct
                 + "  ·  " + Tr.Files(d.Files)
                 + (d.Skipped > 0 ? Tr.S("  ·  ссылок пропущено: ", "  ·  links skipped: ") + d.Skipped : "")
                 + (d.Errors > 0 ? Tr.S("  ·  нет доступа: ", "  ·  no access: ") + d.Errors : "");
        }

        private void BuildDiskTree(DiskScanResult r)
        {
            _tvDisk.BeginUpdate();
            try
            {
                _tvDisk.Nodes.Clear();
                TreeNode root = new TreeNode(DirCaption(r.RootDir, true));
                root.Tag = r.RootDir;
                if (r.RootDir.Children != null) root.Nodes.Add(new TreeNode("…"));
                _tvDisk.Nodes.Add(root);
                root.Expand();
            }
            finally { _tvDisk.EndUpdate(); }
            _tvDisk.SelectedNode = _tvDisk.Nodes[0];
        }

        // Дети подгружаются при раскрытии: у корня системного диска сотни тысяч
        // потомков, строить их все в TreeView заранее — секунды пустого ожидания.
        private void ExpandDiskNode(TreeNode node)
        {
            DiskDir d = node == null ? null : node.Tag as DiskDir;
            if (d == null || d.Children == null) return;
            if (!(node.Nodes.Count == 1 && node.Nodes[0].Tag == null)) return;
            _tvDisk.BeginUpdate();
            try
            {
                node.Nodes.Clear();
                List<DiskDir> kids = new List<DiskDir>(d.Children);
                kids.Sort(delegate(DiskDir a, DiskDir b)
                {
                    int c = b.Size.CompareTo(a.Size);
                    return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
                List<TreeNode> nodes = new List<TreeNode>(kids.Count);
                foreach (DiskDir k in kids)
                {
                    TreeNode n = new TreeNode(DirCaption(k, false));
                    n.Tag = k;
                    if (k.Children != null) n.Nodes.Add(new TreeNode("…"));
                    nodes.Add(n);
                }
                node.Nodes.AddRange(nodes.ToArray());
            }
            finally { _tvDisk.EndUpdate(); }
        }

        private void RefreshDiskTreeText()
        {
            _tvDisk.BeginUpdate();
            try { foreach (TreeNode n in _tvDisk.Nodes) RefreshDiskNodeText(n, true); }
            finally { _tvDisk.EndUpdate(); }
        }

        private void RefreshDiskNodeText(TreeNode n, bool root)
        {
            DiskDir d = n.Tag as DiskDir;
            if (d != null) n.Text = DirCaption(d, root);
            foreach (TreeNode k in n.Nodes) if (k.Tag != null) RefreshDiskNodeText(k, false);
        }

        // ---------- Список: файлы / пустые / дубликаты ----------
        private void SetDiskMode(string mode)
        {
            _diskMode = mode;
            MemSet("disk", "mode", mode, true);
            foreach (Button b in new Button[] { _btnDiskFiles, _btnDiskEmpty, _btnDiskDups }) b.Tag = null;
            (mode == "files" ? _btnDiskFiles : mode == "empty" ? _btnDiskEmpty : _btnDiskDups).Tag = "primary";
            ApplyThemeTo(_diskFlow);
            RefreshDiskList();
        }

        // Клик по дереву — и каждое нажатие стрелки при навигации — перестраивает список.
        // Пока пользователь идёт по дереву клавишами, считать его на каждом промежуточном
        // узле бессмысленно: запрос откладывается на 200 мс тишины. В режиме дубликатов это
        // ещё и защита от запуска полного хэширования на каждом пройденном узле.
        private void ScheduleDiskList()
        {
            if (_diskListTimer == null)
            {
                _diskListTimer = new System.Windows.Forms.Timer();
                _diskListTimer.Interval = 200;
                _diskListTimer.Tick += delegate { _diskListTimer.Stop(); RefreshDiskList(); };
            }
            _diskListTimer.Stop();
            _diskListTimer.Start();
        }

        // Отбор строк идёт в фоне. «Пустые папки» проходят весь список каталогов обхода (для C:
        // это сотни тысяч), приводя каждый путь к нижнему регистру и проверяя одиннадцать
        // защищённых веток; «Крупные файлы» фильтруют десятки тысяч записей подъёмом по цепочке
        // родителей. На UI-потоке это давало паузу до нескольких секунд на каждый клик по дереву.
        // Ответ устаревшего запроса выбрасывается по номеру запроса и по тому же снимку обхода.
        private void RefreshDiskList()
        {
            if (_diskListTimer != null) _diskListTimer.Stop();   // явный запрос отменяет отложенный
            if (_diskScan == null) return;
            bool quiet = _diskListQuiet;        // снимается в любом случае: флаг живёт один запрос
            _diskListQuiet = false;
            if (_diskMode == "dups") { RefreshDupList(); return; }
            DiskDir scope = _diskSelected ?? _diskScan.RootDir;
            DiskScanResult snap = _diskScan;
            long min = DiskEffectiveMin();
            bool empty = _diskMode == "empty";
            int run = ++_diskListRun;
            if (!quiet)
                _lblDiskStatus.Text = empty ? Tr.S("Ищу пустые папки…", "Looking for empty folders…")
                                            : Tr.S("Отбираю крупные файлы…", "Selecting large files…");

            Thread t = new Thread(delegate()
            {
                List<DiskDir> empties = null;
                List<DiskFile> files = null;
                int nested = 0, total = 0;
                try
                {
                    if (empty) empties = Engine.EmptyFolders(snap, scope, out nested);
                    else
                    {
                        files = new List<DiskFile>();
                        foreach (DiskFile f in snap.BigFiles)
                        {
                            if (f.Size < min || !Engine.IsUnder(f.Dir, scope)) continue;
                            total++;
                            if (files.Count < DiskListLimit) files.Add(f);
                        }
                    }
                }
                catch { return; }   // карту правили под нами (удаление) — следом придёт новый запрос
                UiPost(delegate
                {
                    if (run != _diskListRun || !ReferenceEquals(snap, _diskScan)) return;   // ответ устарел
                    List<ListViewItem> rows = new List<ListViewItem>();
                    string note;
                    if (empty)
                    {
                        foreach (DiskDir d in empties)
                        {
                            ListViewItem it = new ListViewItem(d.Name);
                            it.SubItems.Add(d.Dirs > 0 ? Tr.S("папок: ", "folders: ") + (d.Dirs + 1) : "—");
                            it.SubItems.Add("");
                            it.SubItems.Add(d.Parent == null ? "" : d.Parent.Path);
                            it.Tag = d;
                            rows.Add(it);
                        }
                        note = Tr.S("Пустых папок: ", "Empty folders: ") + empties.Count
                             + (nested > 0 ? Tr.S("  (внутри них ещё ", "  (plus ") + nested + Tr.S(" вложенных)", " nested)") : "")
                             + Tr.S("  ·  Windows, Program Files, ProgramData, Packages, node_modules и .git не показываются",
                                    "  ·  Windows, Program Files, ProgramData, Packages, node_modules and .git are not listed");
                    }
                    else
                    {
                        foreach (DiskFile f in files) rows.Add(FileRow(f));
                        note = Tr.S("Крупных файлов (от ", "Large files (") + (min >> 20) + Tr.S(" МБ) в ", " MB and up) in ") + scope.Path + ": "
                             + total.ToString("N0", CultureInfo.CurrentCulture)
                             + (total > rows.Count ? Tr.S("  ·  показаны первые ", "  ·  showing the first ") + rows.Count : "")
                             + Tr.S("  ·  двойной клик — открыть в Проводнике", "  ·  double-click opens Explorer")
                             + DiskRescanHint();
                    }
                    FillDiskRows(rows);
                    _diskNote = note;
                    if (!quiet) _lblDiskStatus.Text = note;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private long DiskMinBytes { get { return (long)_numDiskMin.Value << 20; } }

        // Порог списка не бывает ниже порога обхода: файлы мельче в карту не попали.
        private long DiskEffectiveMin()
        {
            return _diskScan == null ? DiskMinBytes : Math.Max(DiskMinBytes, _diskScan.MinBytes);
        }

        private string DiskRescanHint()
        {
            if (_diskScan == null || DiskMinBytes >= _diskScan.MinBytes) return "";
            return Tr.S("  ·  обход шёл от ", "  ·  the scan collected files from ") + (_diskScan.MinBytes >> 20)
                 + Tr.S(" МБ — для меньшего порога пересканируйте", " MB — rescan for a lower threshold");
        }

        // Каждый щелчок счётчика раньше писал config.json на диск и тут же перестраивал список;
        // при удержании стрелки это десятки записей и перестроений подряд. Значение применяется
        // один раз — через 400 мс тишины.
        private void DiskMinChanged()
        {
            if (_diskMinTimer == null)
            {
                _diskMinTimer = new System.Windows.Forms.Timer();
                _diskMinTimer.Interval = 400;
                _diskMinTimer.Tick += delegate { _diskMinTimer.Stop(); DiskMinCommit(); };
            }
            _diskMinTimer.Stop();
            _diskMinTimer.Start();
        }

        private void DiskMinCommit()
        {
            int mb = (int)_numDiskMin.Value;
            if (_engine.Config.DiskMinMb != mb)
            {
                _engine.Config.DiskMinMb = mb;
                try { _engine.SaveConfig(); } catch { }
            }
            if (_diskScan == null) return;
            if (_diskScanBusy != 0)
            {
                // Раньше новый порог здесь молча уходил в конфиг, а список оставался со старым.
                _diskMinPending = _diskMode != "dups";
                _diskDeferred = Tr.S("порог ", "threshold ") + mb + Tr.S(" МБ применится после текущей операции",
                                                                        " MB will apply after the current operation");
                return;
            }
            if (_diskMode == "dups")
            {
                // Пересчёт дубликатов — это минуты чтения диска: сам собой он не начинается.
                _lblDiskStatus.Text = Tr.S("Порог: ", "Threshold: ") + mb
                    + Tr.S(" МБ  ·  нажмите «Дубликаты», чтобы пересчитать с новым порогом",
                           " MB  ·  click “Duplicates” to recount with the new threshold");
                return;
            }
            RefreshDiskList();
        }

        private void ApplyPendingDiskMin()
        {
            if (!_diskMinPending) return;
            _diskMinPending = false;
            RefreshDiskList();
        }

        private ListViewItem FileRow(DiskFile f)
        {
            ListViewItem it = new ListViewItem(f.Name);
            it.SubItems.Add(Engine.FormatBytes(f.Size));
            DateTime m = f.Modified;
            it.SubItems.Add(m == DateTime.MinValue ? "" : m.ToString("yyyy-MM-dd"));
            it.SubItems.Add(f.Dir.Path);
            it.Tag = f;
            return it;
        }

        // Память выбора для находок на диске: ключ — полный путь, полка сеансовая. Возвращать
        // отметку «удалить» после перезапуска нельзя: по тому же пути к этому времени может
        // лежать уже другой файл, а список здесь ведёт прямиком к удалению.
        private const string DiskScope = "disk.row";

        private static string DiskMemKey(ListViewItem it)
        {
            if (MemRowSkipped(it)) return null;
            DiskFile f = it.Tag as DiskFile;
            if (f != null) return f.Path;
            DiskDir d = it.Tag as DiskDir;
            return d != null ? d.Path : null;
        }
    }
}
