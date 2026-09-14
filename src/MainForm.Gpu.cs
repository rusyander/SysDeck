// Windows Process Cleaner — вкладка «Видеопамять»: схема расхода памяти видеокарты и сбросы.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Двойник вкладки «Память» — тот же вопрос («куда делась память»), тот же ответ целиком: сумма
// блоков схемы равна объёму памяти выбранной видеокарты. Разложение считает Engine.Gpu.cs,
// плитку раскладывает и рисует общий код MainForm.Ram.cs (SliceBuildCells / SlicePaint).
//
// Устройство:
//  * СХЕМА — плитка по выбранной карте: выделенная память (VRAM) либо общая (часть ОЗУ, которую
//    Windows отдаёт видеокарте). Процессы сгруппированы по имени образа, двойной щелчок по
//    группе проваливается внутрь;
//  * ПРОЦЕССЫ — те же данные таблицей с галочками и сортировкой по колонкам;
//  * ВИДЕОКАРТЫ — все карты сразу, двойной щелчок выбирает карту для схемы;
//  * СБРОСЫ — только щадящие: перезапуск GPU-процесса Chromium/Electron (приложение само
//    поднимает его заново), завершение выбранных процессов (системные не завершаются) и
//    перезапуск видеодрайвера (Win+Ctrl+Shift+B) с перечнем программ, которые могут пострадать.
//    После каждого сброса через несколько секунд вкладка говорит, сколько видеопамяти было и
//    сколько стало, — по замеру, а не по обещанию.
//
// Прав администратора не нужно ни на чтение, ни на щадящие сбросы: GPU-процессы принадлежат
// самому пользователю, а сочетание клавиш — обычный ввод. Права спрашиваются только тогда,
// когда процесс не завершился обычной попыткой, — тем же путём, что на вкладке «Память».
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    // Точка истории одной карты.
    internal struct GpuPoint
    {
        public long Dedicated, Shared;
        public double Load;
    }

    public partial class MainForm
    {
        private const int GpuViewMap = 0, GpuViewProcs = 1, GpuViewCards = 2;
        private const string GpuScope = "gpu";

        private GpuSnapshot _gpuSnap;
        private System.Windows.Forms.Timer _gpuTimer;
        private int _gpuBusy;                        // 0/1 — замер уже идёт
        private int _gpuActBusy;                     // 0/1 — идёт сброс
        private int _gpuView;
        private bool _gpuPaused;
        private bool _gpuSharedMode;                 // схема общей памяти вместо выделенной
        private int _gpuInterval = 1000;
        private string _gpuLuid;                     // выбранная карта
        private string _gpuDrill;
        private string _gpuSelKey;
        private RamSlice _gpuSel;
        private string _gpuHover;
        private string _gpuStatus;
        private DateTime _gpuStatusUntil;
        private readonly List<RamCell> _gpuCells = new List<RamCell>();
        private readonly Dictionary<string, List<GpuPoint>> _gpuHistory = new Dictionary<string, List<GpuPoint>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<int> _gpuChecked = new HashSet<int>();
        private int _gpuSort = 2;
        private bool _gpuSortDesc = true;
        private string[] _gpuProcHeaders;
        private bool _gpuFillingProcs, _gpuFillingAdapters;

        // Отложенный отчёт «было → стало» после сброса.
        private string _gpuReportWhat;
        private string _gpuReportLuid;               // null — сумма по всем картам
        private long _gpuReportBefore;
        private DateTime _gpuReportAt = DateTime.MinValue;

        private RamPanel _gpuHead, _gpuMap, _gpuChart;
        private Label _gpuNote, _gpuSelLabel;
        private ListView _lvGpuProcs, _lvGpuCards;
        private Button[] _gpuViewButtons;
        private Button _gpuBtnPause, _gpuBtnBack, _gpuBtnHelper, _gpuBtnKill, _gpuBtnDriver;
        private ComboBox _gpuCmbAdapter, _gpuCmbMemory, _gpuCmbInterval;
        private readonly List<string> _gpuAdapterLuids = new List<string>();

        // ------------------------------------------------------------------ //
        //  Сборка вкладки
        // ------------------------------------------------------------------ //
        private Control BuildGpuTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            Panel center = new Panel();
            center.Dock = DockStyle.Fill;

            _gpuMap = new RamPanel();
            _gpuMap.Dock = DockStyle.Fill;
            _gpuMap.Paint += delegate(object s, PaintEventArgs e)
            {
                SlicePaint(e.Graphics, _gpuMap, _gpuSnap == null ? null : _gpuCells, _gpuSel, GpuColor, GpuEmptyText());
            };
            _gpuMap.MouseMove += GpuMapMouseMove;
            _gpuMap.MouseLeave += delegate { _gpuHover = null; GpuUpdateNote(); };
            _gpuMap.MouseDown += delegate(object s, MouseEventArgs e)
            {
                RamCell c = GpuHit(e.Location);
                GpuSelect(c == null ? null : c.Slice);
            };
            _gpuMap.MouseDoubleClick += GpuMapDoubleClick;
            _gpuMap.Resize += delegate { GpuBuildCells(); _gpuMap.Invalidate(); };

            _lvGpuProcs = RamMakeList(true, new string[] { Tr.S("Процесс", "Process"), "PID", Tr.S("Выделенная", "Dedicated"),
                                                           Tr.S("Общая", "Shared"), Tr.S("Выделено", "Committed"), Tr.S("Загрузка", "Load"),
                                                           Tr.S("Движок", "Engine"), Tr.S("Что это", "What it is") },
                                      new int[] { 200, 80, 150, 110, 120, 100, 110, 420 });
            _lvGpuProcs.HeaderStyle = ColumnHeaderStyle.Clickable;
            _gpuProcHeaders = new string[_lvGpuProcs.Columns.Count];
            for (int i = 0; i < _gpuProcHeaders.Length; i++) _gpuProcHeaders[i] = _lvGpuProcs.Columns[i].Text;
            GpuMarkSortColumn();
            _lvGpuProcs.ColumnClick += GpuProcsColumnClick;
            _lvGpuProcs.ItemChecked += GpuProcsItemChecked;
            _lvGpuProcs.SelectedIndexChanged += delegate { GpuSyncSelectionFromList(); };

            _lvGpuCards = RamMakeList(false, new string[] { Tr.S("Видеокарта", "Graphics card"), Tr.S("Производитель", "Vendor"),
                                                            Tr.S("Выделенная", "Dedicated"), Tr.S("Занято", "In use"),
                                                            Tr.S("Общая", "Shared"), Tr.S("Занято", "In use"),
                                                            Tr.S("Загрузка", "Load"), Tr.S("Процессов", "Processes") },
                                      new int[] { 280, 110, 120, 120, 120, 120, 90, 110 });
            _lvGpuCards.DoubleClick += delegate
            {
                if (_lvGpuCards.SelectedItems.Count == 0) return;
                string luid = _lvGpuCards.SelectedItems[0].Name;
                if (string.IsNullOrEmpty(luid)) return;
                GpuChooseAdapter(luid);
                GpuShowView(GpuViewMap);
            };

            center.Controls.Add(_gpuMap);
            center.Controls.Add(_lvGpuProcs);
            center.Controls.Add(_lvGpuCards);

            _gpuChart = new RamPanel();
            _gpuChart.Dock = DockStyle.Bottom;
            _gpuChart.Height = 84;
            _gpuChart.Paint += GpuChartPaint;

            FlowLayoutPanel selBar = MkToolbar();
            selBar.Dock = DockStyle.Bottom;
            selBar.Padding = new Padding(0, 6, 0, 0);
            _gpuSelLabel = MkFlowLabel(Tr.S("Ничего не выбрано", "Nothing selected"), false);
            _gpuBtnBack = MkFlowButton(Tr.S("← Назад", "← Back"), 110, false);
            _gpuBtnBack.Click += delegate { _gpuDrill = null; GpuRelayout(); };
            _gpuBtnBack.Visible = false;
            _gpuBtnHelper = MkFlowButton(Tr.S("Перезапустить GPU-процесс", "Restart the GPU process"), 250, false);
            _gpuBtnHelper.Click += delegate { GpuRestartHelpers(); };
            _gpuBtnKill = MkFlowButton(Tr.S("Завершить процессы", "Terminate processes"), 210, false);
            _gpuBtnKill.Click += delegate { GpuKillSelected(); };
            _gpuBtnHelper.Enabled = false;
            _gpuBtnKill.Enabled = false;
            selBar.Controls.Add(_gpuBtnBack);
            selBar.Controls.Add(_gpuBtnHelper);
            selBar.Controls.Add(_gpuBtnKill);
            selBar.Controls.Add(_gpuSelLabel);

            _gpuNote = MkNote(Tr.S("Наведите указатель на блок схемы — здесь появится, что это и сколько занимает.",
                                   "Point at a block of the scheme — what it is and how much it takes shows up here."), true);

            _gpuHead = new RamPanel();
            _gpuHead.Dock = DockStyle.Top;
            _gpuHead.Height = 132;
            _gpuHead.Paint += GpuHeadPaint;
            _gpuHead.Resize += delegate { GpuFitHead(); };

            FlowLayoutPanel actions = MkToolbar();
            _gpuBtnDriver = MkFlowButton(Tr.S("Перезапустить видеодрайвер", "Restart the graphics driver"), 260, false);
            _gpuBtnDriver.Click += delegate { GpuResetDriver(); };
            actions.Controls.Add(_gpuBtnDriver);
            actions.Controls.Add(MkFlowLabel(Tr.S("без прав администратора · экран погаснет на секунду · 3D-программы могут закрыться",
                                                  "no administrator rights · the screen goes dark for a second · 3D programs may close"), true));

            FlowLayoutPanel top = MkToolbar();
            string[] views = { Tr.S("Схема", "Scheme"), Tr.S("Процессы", "Processes"), Tr.S("Видеокарты", "Graphics cards") };
            _gpuViewButtons = new Button[views.Length];
            for (int i = 0; i < views.Length; i++)
            {
                Button b = MkFlowButton(views[i], 130, i == 0);
                int idx = i;
                b.Click += delegate { GpuShowView(idx); };
                top.Controls.Add(b);
                _gpuViewButtons[i] = b;
            }
            _gpuCmbAdapter = new RoundComboBox();
            _gpuCmbAdapter.DropDownStyle = ComboBoxStyle.DropDownList;
            _gpuCmbAdapter.Width = 260;
            _gpuCmbAdapter.Margin = new Padding(8, 4, 8, 8);
            _gpuCmbAdapter.SelectedIndexChanged += delegate
            {
                if (_gpuFillingAdapters) return;
                int i = _gpuCmbAdapter.SelectedIndex;
                if (i >= 0 && i < _gpuAdapterLuids.Count) GpuChooseAdapter(_gpuAdapterLuids[i]);
            };
            top.Controls.Add(_gpuCmbAdapter);
            _gpuCmbMemory = new RoundComboBox();
            _gpuCmbMemory.DropDownStyle = ComboBoxStyle.DropDownList;
            _gpuCmbMemory.Width = 190;
            _gpuCmbMemory.Margin = new Padding(0, 4, 8, 8);
            _gpuCmbMemory.Items.AddRange(new object[] { Tr.S("Выделенная (VRAM)", "Dedicated (VRAM)"), Tr.S("Общая (из ОЗУ)", "Shared (from RAM)") });
            _gpuCmbMemory.SelectedIndexChanged += delegate
            {
                bool shared = _gpuCmbMemory.SelectedIndex == 1;
                if (shared == _gpuSharedMode) return;
                _gpuSharedMode = shared;
                _gpuDrill = null;
                MemSetBool(GpuScope, "shared", shared, true);
                GpuRelayout();
            };
            top.Controls.Add(_gpuCmbMemory);
            _gpuBtnPause = MkFlowButton(Tr.S("Пауза", "Pause"), 110, false);
            _gpuBtnPause.Click += delegate { GpuTogglePause(); };
            top.Controls.Add(_gpuBtnPause);
            top.Controls.Add(MkFlowLabel(Tr.S("Опрос:", "Poll:"), true));
            _gpuCmbInterval = new RoundComboBox();
            _gpuCmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _gpuCmbInterval.Width = 120;
            _gpuCmbInterval.Margin = new Padding(0, 4, 8, 8);
            _gpuCmbInterval.Items.AddRange(new object[] { Tr.S("0,5 с", "0.5 s"), Tr.S("1 с", "1 s"), Tr.S("2 с", "2 s"), Tr.S("5 с", "5 s") });
            _gpuCmbInterval.SelectedIndexChanged += delegate { GpuIntervalChanged(); };
            top.Controls.Add(_gpuCmbInterval);

            tab.Controls.Add(center);
            tab.Controls.Add(_gpuChart);
            tab.Controls.Add(selBar);
            tab.Controls.Add(_gpuNote);
            tab.Controls.Add(_gpuHead);
            tab.Controls.Add(actions);
            tab.Controls.Add(top);
            return tab;
        }

        // ------------------------------------------------------------------ //
        //  Жизненный цикл страницы
        // ------------------------------------------------------------------ //

        private void GpuEnter()
        {
            if (_gpuCmbInterval != null && _gpuCmbInterval.SelectedIndex < 0)
            {
                string saved = MemGet(GpuScope, "interval", true);
                int idx;
                if (saved == null || !int.TryParse(saved, out idx) || idx < 0 || idx > 3) idx = 1;
                _gpuCmbInterval.SelectedIndex = idx;
                _gpuSharedMode = MemGet(GpuScope, "shared", true) == "1";
                _gpuCmbMemory.SelectedIndex = _gpuSharedMode ? 1 : 0;
                _gpuLuid = MemGet(GpuScope, "adapter", true);
                string view = MemGet(GpuScope, "view", true);
                int vi;
                GpuShowView(view != null && int.TryParse(view, out vi) && vi >= 0 && vi <= GpuViewCards ? vi : GpuViewMap);
            }
            if (_gpuTimer == null)
            {
                _gpuTimer = new System.Windows.Forms.Timer();
                _gpuTimer.Tick += delegate { GpuTick(); };
            }
            _gpuTimer.Interval = _gpuInterval;
            if (!_gpuPaused) _gpuTimer.Start();
            GpuTick();
        }

        private void GpuLeave()
        {
            if (_gpuTimer != null) _gpuTimer.Stop();
        }

        private void GpuTogglePause()
        {
            _gpuPaused = !_gpuPaused;
            _gpuBtnPause.Text = _gpuPaused ? Tr.S("Продолжить", "Resume") : Tr.S("Пауза", "Pause");
            if (_gpuTimer == null) return;
            if (_gpuPaused) _gpuTimer.Stop();
            else { _gpuTimer.Start(); GpuTick(); }
        }

        private void GpuIntervalChanged()
        {
            int i = _gpuCmbInterval.SelectedIndex;
            int[] ms = { 500, 1000, 2000, 5000 };
            if (i < 0 || i >= ms.Length) return;
            _gpuInterval = ms[i];
            if (_gpuTimer != null) _gpuTimer.Interval = _gpuInterval;
            MemSet(GpuScope, "interval", i.ToString(CultureInfo.InvariantCulture), true);
        }

        private void GpuShowView(int view)
        {
            _gpuView = view;
            if (_gpuMap != null) _gpuMap.Visible = view == GpuViewMap;
            if (_lvGpuProcs != null) _lvGpuProcs.Visible = view == GpuViewProcs;
            if (_lvGpuCards != null) _lvGpuCards.Visible = view == GpuViewCards;
            if (_gpuViewButtons != null && _gpuViewButtons.Length > 0)
            {
                for (int i = 0; i < _gpuViewButtons.Length; i++)
                    _gpuViewButtons[i].Tag = i == view ? "primary" : null;
                ApplyThemeTo(_gpuViewButtons[0].Parent);
            }
            MemSet(GpuScope, "view", view.ToString(CultureInfo.InvariantCulture), true);
            _gpuBtnBack.Visible = view == GpuViewMap && _gpuDrill != null;
            if (_gpuSnap != null) GpuFillCurrentView();
            GpuUpdateSelectionUi();
            GpuUpdateNote();
        }

        private void GpuChooseAdapter(string luid)
        {
            if (string.Equals(luid, _gpuLuid, StringComparison.OrdinalIgnoreCase)) return;
            _gpuLuid = luid;
            _gpuDrill = null;
            _gpuChecked.Clear();
            GpuSelect(null);
            MemSet(GpuScope, "adapter", luid, true);
            int i = _gpuAdapterLuids.IndexOf(luid);
            if (_gpuCmbAdapter != null && i >= 0 && _gpuCmbAdapter.SelectedIndex != i)
            {
                _gpuFillingAdapters = true;
                try { _gpuCmbAdapter.SelectedIndex = i; }
                finally { _gpuFillingAdapters = false; }
            }
            GpuRelayout();
        }

        // ------------------------------------------------------------------ //
        //  Такт опроса
        // ------------------------------------------------------------------ //

        private void GpuTick()
        {
            if (_closing || _currentPage != PageGpu) return;
            if (Interlocked.CompareExchange(ref _gpuBusy, 1, 0) != 0) return;

            Thread t = new Thread(delegate()
            {
                GpuSnapshot s = null;
                string err = null;
                try { s = _engine.GpuSample(); }
                catch (Exception ex) { err = ex.Message; }
                Interlocked.Exchange(ref _gpuBusy, 0);
                GpuSnapshot done = s;
                string error = err;
                UiPost(delegate
                {
                    if (done == null)
                    {
                        GpuSay(Tr.S("Не удалось прочитать видеопамять: ", "Could not read the video memory: ")
                               + (error ?? Tr.S("неизвестная ошибка", "unknown error")));
                        return;
                    }
                    _gpuSnap = done;
                    GpuPushHistory(done);
                    GpuSyncAdapters();
                    GpuCheckReport();
                    GpuRelayout();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuPushHistory(GpuSnapshot s)
        {
            foreach (GpuAdapter a in s.Adapters)
            {
                List<GpuPoint> h;
                if (!_gpuHistory.TryGetValue(a.Luid, out h)) { h = new List<GpuPoint>(); _gpuHistory[a.Luid] = h; }
                GpuPoint p = new GpuPoint();
                p.Dedicated = a.DedicatedUsed;
                p.Shared = a.SharedUsed;
                p.Load = a.Load;
                h.Add(p);
                while (h.Count > 300) h.RemoveAt(0);
            }
        }

        // Список карт в выпадающем списке — по последнему замеру. Выбор держится по LUID: карта
        // может переехать в списке, если подключили внешнюю.
        private void GpuSyncAdapters()
        {
            if (_gpuSnap == null || _gpuCmbAdapter == null) return;
            List<string> luids = new List<string>();
            List<string> titles = new List<string>();
            foreach (GpuAdapter a in _gpuSnap.Adapters)
            {
                luids.Add(a.Luid);
                titles.Add(a.Name + (a.DedicatedTotal > 0 ? "  ·  " + Engine.FormatBytes(a.DedicatedTotal) : ""));
            }
            bool same = luids.Count == _gpuAdapterLuids.Count;
            for (int i = 0; same && i < luids.Count; i++)
                if (!string.Equals(luids[i], _gpuAdapterLuids[i], StringComparison.OrdinalIgnoreCase)) same = false;

            if (_gpuLuid == null || _gpuSnap.Find(_gpuLuid) == null)
                _gpuLuid = luids.Count > 0 ? luids[0] : null;

            _gpuFillingAdapters = true;
            try
            {
                if (!same)
                {
                    _gpuAdapterLuids.Clear();
                    _gpuAdapterLuids.AddRange(luids);
                    _gpuCmbAdapter.Items.Clear();
                    foreach (string t in titles) _gpuCmbAdapter.Items.Add(t);
                }
                int idx = _gpuLuid == null ? -1 : _gpuAdapterLuids.IndexOf(_gpuLuid);
                if (_gpuCmbAdapter.SelectedIndex != idx) _gpuCmbAdapter.SelectedIndex = idx;
            }
            finally { _gpuFillingAdapters = false; }
        }

        private GpuAdapter GpuCurrent()
        {
            return _gpuSnap == null || _gpuLuid == null ? null : _gpuSnap.Find(_gpuLuid);
        }

        private void GpuRelayout()
        {
            if (_gpuSnap == null) { GpuUpdateNote(); return; }
            GpuBuildCells();
            GpuRestoreSelection();
            GpuFillCurrentView();
            _gpuBtnBack.Visible = _gpuView == GpuViewMap && _gpuDrill != null;
            if (_gpuHead != null) _gpuHead.Invalidate();
            if (_gpuMap != null) _gpuMap.Invalidate();
            if (_gpuChart != null) _gpuChart.Invalidate();
            GpuUpdateNote();
        }

        private void GpuFillCurrentView()
        {
            if (_gpuView == GpuViewProcs) GpuFillProcs();
            else if (_gpuView == GpuViewCards) GpuFillCards();
        }

        // ------------------------------------------------------------------ //
        //  Схема
        // ------------------------------------------------------------------ //

        private List<RamSlice> GpuRootAll()
        {
            GpuAdapter a = GpuCurrent();
            if (a == null) return new List<RamSlice>();
            return _gpuSharedMode ? a.SharedSlices : a.DedicatedSlices;
        }

        // На схеме — только занятая память. Свободная на видеокарте обычно больше девяти десятых
        // объёма, и плитка «Свободно» сжимала бы все процессы в полоску у края; её место — полоса
        // в шапке, где карта показана целиком.
        private List<RamSlice> GpuRoot()
        {
            List<RamSlice> all = GpuRootAll();
            if (_gpuDrill == null)
            {
                List<RamSlice> used = new List<RamSlice>();
                foreach (RamSlice x in all)
                    if (x.Kind != GpuKind.Free && x.Bytes > 0) used.Add(x);
                return used.Count > 0 ? used : all;
            }
            RamSlice s = RamFind(all, _gpuDrill);
            if (s != null && s.Children != null && s.Children.Count > 0) return s.Children;
            _gpuDrill = null;
            return all;
        }

        private void GpuBuildCells()
        {
            SliceBuildCells(_gpuMap, GpuRoot(), _gpuCells);
        }

        private string GpuEmptyText()
        {
            if (_gpuSnap != null && !_gpuSnap.Ok && _gpuSnap.Error != null) return _gpuSnap.Error;
            if (_gpuSnap != null && _gpuSnap.Adapters.Count == 0)
                return Tr.S("Видеокарт с памятью не найдено.", "No graphics cards with memory found.");
            return Tr.S("Читаю видеопамять…", "Reading the video memory…");
        }

        private Color GpuColor(int kind, int level)
        {
            Color c;
            switch (kind)
            {
                case GpuKind.Process: c = Color.FromArgb(59, 130, 246); break;    // синий — процессы
                case GpuKind.Group: c = Color.FromArgb(59, 130, 246); break;
                case GpuKind.Helper: c = Color.FromArgb(20, 165, 160); break;     // бирюзовый — GPU-процессы браузеров
                case GpuKind.System: c = Color.FromArgb(217, 70, 110); break;     // малиновый — драйвер и ядро
                case GpuKind.Free: c = Color.FromArgb(110, 118, 129); break;
                default: c = _theme.Accent; break;
            }
            if (_theme.Dark) c = ControlPaint.Dark(c, 0.12f);
            if (level > 0) c = _theme.Dark ? ControlPaint.Light(c, 0.25f) : ControlPaint.Light(c, 0.18f);
            return c;
        }

        private RamCell GpuHit(Point p)
        {
            for (int i = _gpuCells.Count - 1; i >= 0; i--)
                if (_gpuCells[i].Rect.Contains(p)) return _gpuCells[i];
            return null;
        }

        private void GpuMapMouseMove(object sender, MouseEventArgs e)
        {
            RamCell c = GpuHit(e.Location);
            string text = c == null ? null : GpuDescribe(c.Slice);
            if (text == _gpuHover) return;
            _gpuHover = text;
            GpuUpdateNote();
        }

        private string GpuDescribe(RamSlice s)
        {
            GpuAdapter a = GpuCurrent();
            if (s == null || a == null) return null;
            long total = 0;
            foreach (RamSlice x in GpuRootAll()) total += x.Bytes;
            double share = total > 0 ? 100.0 * s.Bytes / total : 0;
            string text = s.Title + "  ·  " + Engine.FormatBytes(s.Bytes)
                        + "  ·  " + share.ToString("0.0", CultureInfo.InvariantCulture) + Tr.S(" % карты", " % of the card");
            if (!string.IsNullOrEmpty(s.Hint)) text += "  ·  " + s.Hint;
            return text;
        }

        private void GpuMapDoubleClick(object sender, MouseEventArgs e)
        {
            RamCell c = GpuHit(e.Location);
            if (c == null) return;
            if (c.Slice.Children != null && c.Slice.Children.Count > 1)
            {
                _gpuDrill = c.Slice.Key;
                GpuSelect(null);
                GpuRelayout();
            }
        }

        private void GpuSelect(RamSlice s)
        {
            _gpuSel = s;
            _gpuSelKey = s == null ? null : s.Key;
            GpuUpdateSelectionUi();
            if (_gpuMap != null) _gpuMap.Invalidate();
        }

        private void GpuRestoreSelection()
        {
            if (_gpuSelKey == null) { _gpuSel = null; GpuUpdateSelectionUi(); return; }
            _gpuSel = RamFind(GpuRootAll(), _gpuSelKey);
            if (_gpuSel == null) _gpuSelKey = null;
            GpuUpdateSelectionUi();
        }

        private void GpuSay(string text)
        {
            _gpuStatus = text;
            _gpuStatusUntil = DateTime.UtcNow.AddSeconds(15);
            GpuUpdateNote();
        }

        private void GpuUpdateNote()
        {
            if (_gpuNote == null) return;
            if (_gpuHover != null) { _gpuNote.Text = _gpuHover; return; }
            if (_gpuStatus != null && DateTime.UtcNow < _gpuStatusUntil) { _gpuNote.Text = _gpuStatus; return; }
            _gpuStatus = null;
            GpuAdapter a = GpuCurrent();
            if (_gpuSnap == null) { _gpuNote.Text = Tr.S("Читаю видеопамять…", "Reading the video memory…"); return; }
            if (!_gpuSnap.Ok) { _gpuNote.Text = _gpuSnap.Error ?? ""; return; }
            switch (_gpuView)
            {
                case GpuViewProcs:
                    _gpuNote.Text = Tr.S("Отметьте процессы галочками — кнопки внизу перезапускают GPU-процессы браузеров или завершают процессы. Щелчок по заголовку сортирует.",
                                         "Tick processes — the buttons below restart browser GPU processes or terminate processes. A click on a header sorts.");
                    break;
                case GpuViewCards:
                    _gpuNote.Text = Tr.S("Все видеокарты сразу. Двойной щелчок по строке показывает схему этой карты.",
                                         "All graphics cards at once. A double click on a row shows that card's scheme.");
                    break;
                default:
                    if (a == null) { _gpuNote.Text = GpuEmptyText(); break; }
                    long used = _gpuSharedMode ? a.SharedUsed : a.DedicatedUsed;
                    long total = _gpuSharedMode ? a.SharedTotal : a.DedicatedTotal;
                    _gpuNote.Text = (_gpuSharedMode
                            ? Tr.S("Схема — занятая общая память: ", "The scheme is the shared memory in use: ")
                            : Tr.S("Схема — занятая память карты: ", "The scheme is the card memory in use: "))
                        + Engine.FormatBytes(used) + Tr.S(" из ", " of ") + Engine.FormatBytes(total)
                        + Tr.S(", свободное — в полосе сверху. Щелчок выбирает блок, двойной — проваливается внутрь.",
                               ", the free part is in the bar above. A click selects a block, a double click drills into it.")
                        + (a.Scaled ? Tr.S(" Счётчики процессов разошлись с картой — доли ужаты до её объёма.",
                                           " The process counters overshot the card — shares are scaled to its size.") : "");
                    break;
            }
        }

        // Что выбрано: на схеме — блок (группа покрывает все свои процессы), в списке — галочки.
        private List<int> GpuSelectedPids()
        {
            List<int> pids = new List<int>();
            if (_gpuView == GpuViewProcs)
            {
                foreach (int pid in _gpuChecked) pids.Add(pid);
                if (pids.Count > 0) return pids;
            }
            if (_gpuSel != null && _gpuSel.Pids != null) pids.AddRange(_gpuSel.Pids);
            return pids;
        }

        private GpuProc GpuProcOf(int pid)
        {
            if (_gpuSnap == null) return null;
            GpuProc best = null;
            foreach (GpuProc p in _gpuSnap.Procs)
            {
                if (p.Pid != pid) continue;
                if (string.Equals(p.Luid, _gpuLuid, StringComparison.OrdinalIgnoreCase)) return p;
                if (best == null) best = p;
            }
            return best;
        }

        private void GpuUpdateSelectionUi()
        {
            if (_gpuSelLabel == null) return;
            List<int> pids = GpuSelectedPids();
            int helpers = 0, killable = 0;
            foreach (int pid in pids)
            {
                GpuProc p = GpuProcOf(pid);
                if (p == null) continue;
                if (p.Helper) helpers++;
                if (!p.Protected) killable++;
            }
            bool idle = _gpuActBusy == 0;
            _gpuBtnHelper.Enabled = idle && helpers > 0;
            _gpuBtnKill.Enabled = idle && killable > 0;
            if (_gpuBtnDriver != null) _gpuBtnDriver.Enabled = idle;
            if (_gpuSel != null)
                _gpuSelLabel.Text = Tr.S("Выбрано: ", "Selected: ") + _gpuSel.Title
                                  + "  ·  " + Engine.FormatBytes(_gpuSel.Bytes)
                                  + (pids.Count > 1 ? Tr.S("  ·  процессов: ", "  ·  processes: ") + pids.Count : "");
            else if (pids.Count > 0)
                _gpuSelLabel.Text = Tr.S("Отмечено процессов: ", "Ticked processes: ") + pids.Count;
            else
                _gpuSelLabel.Text = Tr.S("Ничего не выбрано", "Nothing selected");
        }

        // ------------------------------------------------------------------ //
        //  Шапка
        // ------------------------------------------------------------------ //

        private int GpuHeadColumns()
        {
            int avail = _gpuHead.ClientSize.Width - Px(2) * 2;
            return Math.Max(1, Math.Min(3, avail / Px(250)));
        }

        private void GpuFitHead()
        {
            if (_gpuHead == null || _gpuHead.ClientSize.Width < 40) return;
            int rows = (9 + GpuHeadColumns() - 1) / GpuHeadColumns();
            int want = Px(2) + Px(30) + Px(6) + Px(19) * (1 + rows) + Px(6);
            if (Math.Abs(_gpuHead.Height - want) > 1) _gpuHead.Height = want;
        }

        private void GpuHeadPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            GpuAdapter a = GpuCurrent();
            if (a == null) return;

            int pad = Px(2);
            Rectangle bar = new Rectangle(pad, pad, _gpuHead.ClientSize.Width - pad * 2, Px(30));
            if (bar.Width < 40) return;

            // Полоса — та же схема одной строкой: процессы, GPU-процессы браузеров, драйвер, свободно.
            List<RamSlice> root = GpuRootAll();
            long total = 0, helpers = 0, procs = 0, system = 0, free = 0;
            foreach (RamSlice s in root)
            {
                total += s.Bytes;
                if (s.Kind == GpuKind.System) system += s.Bytes;
                else if (s.Kind == GpuKind.Free) free += s.Bytes;
                else GpuSplitHelpers(s, ref helpers, ref procs);
            }
            if (total > 0)
            {
                long[] parts = { procs, helpers, system, free };
                int[] kinds = { GpuKind.Process, GpuKind.Helper, GpuKind.System, GpuKind.Free };
                float x = bar.X;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] <= 0) continue;
                    float w = (float)bar.Width * parts[i] / total;
                    using (SolidBrush br = new SolidBrush(GpuColor(kinds[i], 0))) g.FillRectangle(br, new RectangleF(x, bar.Y, w, bar.Height));
                    x += w;
                }
            }
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, bar);

            Font f = new Font(Font.FontFamily, 9.5F);
            Font fb = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            try
            {
                int y = bar.Bottom + Px(6);
                int lineH = Px(19);
                double usedPct = a.DedicatedTotal > 0 ? 100.0 * a.DedicatedUsed / a.DedicatedTotal : 0;
                string head = a.Name
                            + "   ·   " + Tr.S("выделенная ", "dedicated ") + Engine.FormatBytes(a.DedicatedUsed)
                            + Tr.S(" из ", " of ") + Engine.FormatBytes(a.DedicatedTotal)
                            + " (" + usedPct.ToString("0.0", CultureInfo.InvariantCulture) + " %)"
                            + "   ·   " + Tr.S("загрузка ", "load ") + a.Load.ToString("0", CultureInfo.InvariantCulture) + " %";
                TextRenderer.DrawText(g, head, fb, new Rectangle(pad, y, _gpuHead.ClientSize.Width - pad * 2, lineH), _theme.Text,
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += lineH;

                int helperCount = 0;
                long helperBytes = 0;
                foreach (GpuProc p in _gpuSnap.Procs)
                    if (p.Helper && string.Equals(p.Luid, a.Luid, StringComparison.OrdinalIgnoreCase))
                    {
                        helperCount++;
                        helperBytes += _gpuSharedMode ? p.Shared : p.Dedicated;
                    }

                string[] cells = new string[]
                {
                    Tr.S("Процессы", "Processes"), Engine.FormatBytes(procs + helpers),
                    Tr.S("Система и драйвер", "System and driver"), Engine.FormatBytes(system),
                    Tr.S("Свободно", "Free"), Engine.FormatBytes(free),
                    Tr.S("Общая память", "Shared memory"), Engine.FormatBytes(a.SharedUsed) + Tr.S(" из ", " of ") + Engine.FormatBytes(a.SharedTotal),
                    Tr.S("GPU-процессы браузеров", "Browser GPU processes"),
                        helperCount + "  ·  " + Engine.FormatBytes(helperBytes),
                    Tr.S("Выделено всего", "Committed total"), Engine.FormatBytes(a.Committed),
                    Tr.S("Процессов на карте", "Processes on the card"), a.ProcCount.ToString(CultureInfo.InvariantCulture),
                    Tr.S("Самый занятый движок", "Busiest engine"),
                        string.IsNullOrEmpty(a.LoadEngine) ? "—" : a.LoadEngine + "  ·  " + a.Load.ToString("0", CultureInfo.InvariantCulture) + " %",
                    Tr.S("Производитель", "Vendor"), string.IsNullOrEmpty(a.Vendor) ? "—" : a.Vendor
                };
                int avail = _gpuHead.ClientSize.Width - pad * 2;
                int cols = GpuHeadColumns();
                int colW = avail / cols;
                for (int i = 0; i < cells.Length / 2; i++)
                {
                    int cx = pad + (i % cols) * colW;
                    int cy = y + (i / cols) * lineH;
                    if (cy + lineH > _gpuHead.ClientSize.Height) break;
                    string key = cells[i * 2], val = cells[i * 2 + 1];
                    TextRenderer.DrawText(g, key, f, new Point(cx, cy), _theme.Subtle, TextFormatFlags.NoPrefix);
                    int keyW = TextRenderer.MeasureText(key, f).Width + Px(8);
                    TextRenderer.DrawText(g, val, fb, new Rectangle(cx + keyW, cy, Math.Max(20, colW - keyW - Px(10)), lineH),
                                          _theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
            finally { f.Dispose(); fb.Dispose(); }
        }

        private static void GpuSplitHelpers(RamSlice s, ref long helpers, ref long procs)
        {
            if (s.Children != null && s.Children.Count > 0)
            {
                foreach (RamSlice c in s.Children) GpuSplitHelpers(c, ref helpers, ref procs);
                return;
            }
            if (s.Kind == GpuKind.Helper) helpers += s.Bytes;
            else procs += s.Bytes;
        }

        // ------------------------------------------------------------------ //
        //  График истории: занятая выделенная память заливкой, загрузка — линией
        // ------------------------------------------------------------------ //

        private void GpuChartPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            Rectangle box = _gpuChart.ClientRectangle;
            box.Inflate(-1, -1);
            if (box.Width < 30 || box.Height < 20) return;
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, box);

            GpuAdapter a = GpuCurrent();
            List<GpuPoint> hist;
            if (a == null || !_gpuHistory.TryGetValue(a.Luid, out hist) || hist.Count < 2)
            {
                TextRenderer.DrawText(g, Tr.S("История наполняется…", "History is filling up…"), Font, box,
                                      _theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // Шкала — по пику истории с запасом, а не по объёму карты: 1 ГБ на 24-гигабайтной карте
            // лёг бы на график ниточкой у нижнего края, и рост расхода был бы не виден.
            long card = _gpuSharedMode ? a.SharedTotal : a.DedicatedTotal;
            long peak = 0;
            foreach (GpuPoint p in hist) peak = Math.Max(peak, _gpuSharedMode ? p.Shared : p.Dedicated);
            long total = GpuNiceScale(peak + peak / 4);
            if (card > 0 && total > card) total = card;
            if (total <= 0) total = 1;

            int n = hist.Count;
            float step = (float)(box.Width - 2) / Math.Max(1, n - 1);
            PointF[] area = new PointF[n + 2];
            PointF[] load = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                long v = _gpuSharedMode ? hist[i].Shared : hist[i].Dedicated;
                float h = Math.Min(box.Height, (float)box.Height * v / total);
                area[i] = new PointF(box.X + 1 + i * step, box.Bottom - h);
                float lh = (float)(box.Height * Math.Min(100, hist[i].Load) / 100.0);
                load[i] = new PointF(box.X + 1 + i * step, box.Bottom - 1 - lh);
            }
            area[n] = new PointF(box.Right - 1, box.Bottom - 1);
            area[n + 1] = new PointF(box.X + 1, box.Bottom - 1);
            using (SolidBrush br = new SolidBrush(GpuColor(GpuKind.Process, 0))) g.FillPolygon(br, area);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(236, 122, 72), Px(2))) g.DrawLines(pen, load);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            Font f = new Font(Font.FontFamily, 8.25F);
            try
            {
                int seconds = n * _gpuInterval / 1000;
                string span = Tr.S("последние ", "last ") + (seconds < 90 ? seconds + Tr.S(" с", " s") : (seconds / 60) + Tr.S(" мин", " min"))
                            + Tr.S("  ·  заливка — занятая память, линия — загрузка", "  ·  fill — memory in use, line — load");
                TextRenderer.DrawText(g, span, f, new Point(box.X + 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
                string right = Engine.FormatBytes(total);
                Size sz = TextRenderer.MeasureText(right, f);
                TextRenderer.DrawText(g, right, f, new Point(box.Right - sz.Width - 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
            }
            finally { f.Dispose(); }
        }

        // Круглая граница шкалы: 256 МБ, 512 МБ, 1 ГБ, 2 ГБ… — подпись читается сразу.
        private static long GpuNiceScale(long bytes)
        {
            long v = 256L * 1024 * 1024;
            while (v < bytes && v < long.MaxValue / 2) v *= 2;
            return v;
        }

        // ------------------------------------------------------------------ //
        //  Списки
        // ------------------------------------------------------------------ //

        private void GpuFillProcs()
        {
            GpuAdapter a = GpuCurrent();
            if (_lvGpuProcs == null) return;
            List<GpuProc> rows = new List<GpuProc>();
            if (a != null)
                foreach (GpuProc p in _gpuSnap.Procs)
                    if (string.Equals(p.Luid, a.Luid, StringComparison.OrdinalIgnoreCase)) rows.Add(p);
            rows.Sort(GpuProcComparer);

            int topIndex = _lvGpuProcs.TopItem != null ? _lvGpuProcs.TopItem.Index : 0;
            _lvGpuProcs.BeginUpdate();
            try
            {
                _lvGpuProcs.Items.Clear();
                ListViewItem[] items = new ListViewItem[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    GpuProc p = rows[i];
                    ListViewItem it = new ListViewItem(p.Name);
                    it.SubItems.Add(p.Pid.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(Engine.FormatBytes(p.Dedicated));
                    it.SubItems.Add(Engine.FormatBytes(p.Shared));
                    it.SubItems.Add(p.Committed > 0 ? Engine.FormatBytes(p.Committed) : "");
                    it.SubItems.Add(p.Load >= 0.5 ? p.Load.ToString("0", CultureInfo.InvariantCulture) + " %" : "");
                    it.SubItems.Add(p.Load >= 0.5 && p.EngineType != null ? p.EngineType : "");
                    it.SubItems.Add(GpuWhat(p));
                    it.Tag = p;
                    it.Checked = _gpuChecked.Contains(p.Pid);
                    items[i] = it;
                }
                _gpuFillingProcs = true;
                _lvGpuProcs.Items.AddRange(items);
                if (topIndex > 0 && topIndex < _lvGpuProcs.Items.Count)
                    try { _lvGpuProcs.TopItem = _lvGpuProcs.Items[topIndex]; }
                    catch { }
                if (_gpuSelKey != null)
                    foreach (ListViewItem it in _lvGpuProcs.Items)
                    {
                        GpuProc p = it.Tag as GpuProc;
                        if (p != null && _gpuSelKey.EndsWith("pid:" + p.Pid, StringComparison.Ordinal)) { it.Selected = true; break; }
                    }
            }
            finally
            {
                _gpuFillingProcs = false;
                _lvGpuProcs.EndUpdate();
            }
            AutoFillLastColumnDeferred(_lvGpuProcs);
        }

        private static string GpuWhat(GpuProc p)
        {
            List<string> parts = new List<string>();
            if (p.Helper) parts.Add(Tr.S("GPU-процесс: перезапуск безопасен, приложение поднимет его само",
                                         "GPU process: safe to restart, the app brings it back itself"));
            if (p.Protected) parts.Add(Tr.S("системный процесс — не завершается", "system process — never terminated"));
            if (p.Inflated) parts.Add(Tr.S("счётчик Windows завышен: ", "the Windows counter is inflated: ") + Engine.FormatBytes(p.DedicatedRaw));
            return string.Join("  ·  ", parts.ToArray());
        }

        private int GpuProcComparer(GpuProc a, GpuProc b)
        {
            int r;
            switch (_gpuSort)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = a.Pid.CompareTo(b.Pid); break;
                case 3: r = a.Shared.CompareTo(b.Shared); break;
                case 4: r = a.Committed.CompareTo(b.Committed); break;
                case 5: r = a.Load.CompareTo(b.Load); break;
                case 6: r = string.Compare(a.EngineType ?? "", b.EngineType ?? "", StringComparison.OrdinalIgnoreCase); break;
                case 7: r = string.Compare(GpuWhat(a), GpuWhat(b), StringComparison.OrdinalIgnoreCase); break;
                default: r = a.Dedicated.CompareTo(b.Dedicated); break;
            }
            if (r == 0) r = a.Pid.CompareTo(b.Pid);
            return _gpuSortDesc ? -r : r;
        }

        private void GpuProcsColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _gpuSort) _gpuSortDesc = !_gpuSortDesc;
            else { _gpuSort = e.Column; _gpuSortDesc = e.Column >= 2 && e.Column <= 5; }
            GpuMarkSortColumn();
            GpuFillProcs();
        }

        private void GpuMarkSortColumn()
        {
            if (_gpuProcHeaders == null) return;
            for (int i = 0; i < _gpuProcHeaders.Length && i < _lvGpuProcs.Columns.Count; i++)
                _lvGpuProcs.Columns[i].Text = i == _gpuSort
                    ? _gpuProcHeaders[i] + (_gpuSortDesc ? "  ▼" : "  ▲")
                    : _gpuProcHeaders[i];
        }

        private void GpuProcsItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_gpuFillingProcs) return;
            GpuProc p = e.Item.Tag as GpuProc;
            if (p == null) return;
            if (e.Item.Checked) _gpuChecked.Add(p.Pid);
            else _gpuChecked.Remove(p.Pid);
            GpuUpdateSelectionUi();
        }

        private void GpuSyncSelectionFromList()
        {
            if (_gpuFillingProcs || _lvGpuProcs == null || _lvGpuProcs.SelectedItems.Count == 0) return;
            GpuProc p = _lvGpuProcs.SelectedItems[0].Tag as GpuProc;
            if (p == null) return;
            string key = (_gpuSharedMode ? "gs:" : "gd:") + "pid:" + p.Pid.ToString(CultureInfo.InvariantCulture);
            GpuSelect(RamFind(GpuRootAll(), key));
        }

        private void GpuFillCards()
        {
            if (_lvGpuCards == null || _gpuSnap == null) return;
            _lvGpuCards.BeginUpdate();
            try
            {
                _lvGpuCards.Items.Clear();
                foreach (GpuAdapter a in _gpuSnap.Adapters)
                {
                    ListViewItem it = new ListViewItem(a.Name);
                    it.Name = a.Luid;
                    it.SubItems.Add(a.Vendor);
                    it.SubItems.Add(a.Known ? Engine.FormatBytes(a.DedicatedTotal) : "?");
                    it.SubItems.Add(Engine.FormatBytes(a.DedicatedUsed));
                    it.SubItems.Add(a.Known ? Engine.FormatBytes(a.SharedTotal) : "?");
                    it.SubItems.Add(Engine.FormatBytes(a.SharedUsed));
                    it.SubItems.Add(a.Load.ToString("0", CultureInfo.InvariantCulture) + " %");
                    it.SubItems.Add(a.ProcCount.ToString(CultureInfo.InvariantCulture));
                    it.Tag = NoCheckTag;
                    if (string.Equals(a.Luid, _gpuLuid, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
                    _lvGpuCards.Items.Add(it);
                }
            }
            finally { _lvGpuCards.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvGpuCards);
        }

        // ------------------------------------------------------------------ //
        //  Сбросы
        // ------------------------------------------------------------------ //

        private void GpuSetBusy(bool busy)
        {
            Interlocked.Exchange(ref _gpuActBusy, busy ? 1 : 0);
            GpuUpdateSelectionUi();
        }

        private long GpuUsedNow(string luid)
        {
            if (_gpuSnap == null) return 0;
            long sum = 0;
            foreach (GpuAdapter a in _gpuSnap.Adapters)
                if (luid == null || string.Equals(a.Luid, luid, StringComparison.OrdinalIgnoreCase)) sum += a.DedicatedUsed;
            return sum;
        }

        // Итог сброса считается по замеру через несколько секунд: сразу после действия приложения
        // ещё только поднимают новые поверхности, и «освобождено» было бы обманом в любую сторону.
        private void GpuScheduleReport(string what, string luid, long before, int seconds)
        {
            _gpuReportWhat = what;
            _gpuReportLuid = luid;
            _gpuReportBefore = before;
            _gpuReportAt = DateTime.UtcNow.AddSeconds(seconds);
        }

        private void GpuCheckReport()
        {
            if (_gpuReportAt == DateTime.MinValue || DateTime.UtcNow < _gpuReportAt) return;
            _gpuReportAt = DateTime.MinValue;
            long after = GpuUsedNow(_gpuReportLuid);
            long delta = _gpuReportBefore - after;
            GpuSay(_gpuReportWhat + Tr.S("  ·  видеопамять: было ", "  ·  video memory: was ") + Engine.FormatBytes(_gpuReportBefore)
                   + Tr.S(", стало ", ", now ") + Engine.FormatBytes(after)
                   + (delta > 0 ? Tr.S("  (освобождено ", "  (freed ") + Engine.FormatBytes(delta) + ")"
                                : Tr.S("  (память снова занята — приложения восстановили своё)", "  (the memory is in use again — the apps restored what they need)")));
        }

        private void GpuRestartHelpers()
        {
            if (_gpuActBusy != 0) return;
            List<GpuProc> targets = new List<GpuProc>();
            foreach (int pid in GpuSelectedPids())
            {
                GpuProc p = GpuProcOf(pid);
                if (p != null && p.Helper && !targets.Contains(p)) targets.Add(p);
            }
            if (targets.Count == 0) return;

            List<string> shown = new List<string>();
            foreach (GpuProc p in targets)
                shown.Add("  " + p.Name + " (pid " + p.Pid + ")  ·  " + Engine.FormatBytes(p.Dedicated + p.Shared));
            if (!MsgAsk(Tr.S("Перезапустить GPU-процессы?\r\n\r\n", "Restart the GPU processes?\r\n\r\n")
                        + string.Join("\r\n", shown.ToArray())
                        + Tr.S("\r\n\r\nПриложение само запустит GPU-процесс заново: окна, вкладки и несохранённый текст остаются, изображение может мигнуть. "
                               + "Одно приложение — не чаще раза в три минуты.",
                               "\r\n\r\nThe app starts its GPU process again by itself: windows, tabs and unsaved text stay, the picture may blink. "
                               + "Once every three minutes per app at most."),
                        Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Перезапускаю GPU-процессы…", "Restarting the GPU processes…"));
            string luid = _gpuLuid;
            long before = GpuUsedNow(luid);
            List<int> pids = new List<int>();
            foreach (GpuProc p in targets) pids.Add(p.Pid);

            Thread t = new Thread(delegate()
            {
                List<string> lines = new List<string>();
                int ok = 0;
                foreach (int pid in pids)
                {
                    if (_closing) break;
                    GpuAction r;
                    try { r = _engine.GpuRestartHelper(pid); }
                    catch (Exception ex) { r = new GpuAction(); r.Message = ex.Message; }
                    if (r.Ok) ok++;
                    lines.Add(r.Message ?? "");
                }
                int okCopy = ok;
                string text = string.Join("  ·  ", lines.ToArray());
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    _gpuChecked.Clear();
                    GpuSelect(null);
                    GpuSay(text);
                    if (okCopy > 0) GpuScheduleReport(text, luid, before, 4);
                    GpuTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuKillSelected()
        {
            if (_gpuActBusy != 0) return;
            List<int> pids = new List<int>();
            List<string> shown = new List<string>();
            List<string> skipped = new List<string>();
            foreach (int pid in GpuSelectedPids())
            {
                GpuProc p = GpuProcOf(pid);
                if (p == null || pids.Contains(pid)) continue;
                if (p.Protected) { if (!skipped.Contains(p.Name)) skipped.Add(p.Name); continue; }
                pids.Add(pid);
                shown.Add(p.Name + " (pid " + pid + ")  ·  " + Engine.FormatBytes(p.Dedicated + p.Shared)
                          + (p.Helper ? Tr.S("  ·  лучше «Перезапустить GPU-процесс»", "  ·  «Restart the GPU process» is gentler") : ""));
            }
            if (pids.Count == 0)
            {
                if (skipped.Count > 0)
                    MsgInfo(Tr.S("Системные процессы не завершаются: ", "System processes are never terminated: ")
                            + string.Join(", ", skipped.ToArray())
                            + Tr.S(".\r\n\r\nЕсли видеопамять держит dwm.exe, её освобождает перезапуск видеодрайвера.",
                                   ".\r\n\r\nIf dwm.exe holds the video memory, restarting the graphics driver releases it."),
                            Tr.S("Видеопамять", "Video memory"));
                return;
            }
            string question = DevKillQuestion(Tr.S("Видеопамять", "Video memory"), shown);
            if (skipped.Count > 0)
                question += Tr.S("\r\n\r\nНе будут завершены (системные): ", "\r\n\r\nWill not be terminated (system): ") + string.Join(", ", skipped.ToArray());
            if (!MsgAsk(question, Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Завершаю выбранные процессы…", "Terminating the selected processes…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));
            string luid = _gpuLuid;
            long before = GpuUsedNow(luid);
            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                string err = null;
                try { killed = _engine.TerminateMany(list, out freed, null, delegate { return _closing; }); }
                catch (Exception ex) { err = ex.Message; }
                List<int> alive;
                try { alive = _engine.SurvivorsOf(list); }
                catch { alive = new List<int>(); }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                int killedCopy = killed;
                string errCopy = err;
                List<int> aliveCopy = alive;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    _gpuChecked.Clear();
                    GpuSelect(null);
                    string text = errCopy != null
                        ? Tr.S("Не удалось: ", "Failed: ") + errCopy
                        : killedCopy > 0
                            ? Tr.S("Завершено процессов: ", "Terminated: ") + killedCopy
                            : Tr.S("Ни один процесс не завершился — нужны права администратора или процесс защищён.",
                                   "Not a single process terminated — administrator rights are needed, or the process is protected.");
                    GpuSay(text);
                    if (killedCopy > 0) GpuScheduleReport(text, luid, before, 3);
                    GpuTick();
                    if (errCopy == null && aliveCopy.Count > 0) GpuOfferElevatedKill(aliveCopy, luid, before);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuOfferElevatedKill(List<int> pids, string luid, long before)
        {
            if (Elevated || pids.Count == 0) return;
            List<string> shown = new List<string>();
            foreach (int pid in pids)
            {
                GpuProc p = GpuProcOf(pid);
                shown.Add((p != null ? p.Name + " " : "") + "(pid " + pid + ")");
            }
            if (!MsgAsk(Tr.S("Не удалось завершить процессов: ", "Processes that would not terminate: ") + pids.Count
                        + Tr.S(" — им нужны права администратора.\r\n\r\n", " — they need administrator rights.\r\n\r\n")
                        + string.Join("\r\n", shown.ToArray())
                        + Tr.S("\r\n\r\nЗапросить права и повторить?", "\r\n\r\nAsk for rights and retry?"),
                        Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Запрашиваю права администратора…", "Asking for administrator rights…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));
            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                ElevResult r;
                try { r = KillElevated(list, null, delegate { return _closing; }); }
                catch (Exception ex) { r = new ElevResult(); r.Message = ex.Message; }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                ElevResult done = r;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    string text = done.Ok
                        ? Tr.S("Завершено процессов: ", "Terminated: ") + done.Count
                        : done.Declined
                            ? Tr.S("Запрос прав отклонён — процессы остались на месте.", "The rights prompt was declined — the processes are still running.")
                            : Tr.S("Не удалось завершить: ", "Could not terminate: ") + (done.Message ?? "");
                    GpuSay(text);
                    if (done.Ok) GpuScheduleReport(text, luid, before, 3);
                    GpuTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuResetDriver()
        {
            if (_gpuActBusy != 0) return;

            // Кто может пострадать: программы, которые прямо сейчас рисуют через 3D или держат
            // заметный объём памяти. Браузеры и окна Windows переживают сброс сами — их не пугаем.
            List<string> risky = new List<string>();
            if (_gpuSnap != null)
                foreach (GpuProc p in _gpuSnap.Procs)
                {
                    if (p.Protected || p.Helper || risky.Contains(p.Name)) continue;
                    bool drawing3d = p.Load >= 1 && string.Equals(p.EngineType, "3D", StringComparison.OrdinalIgnoreCase);
                    if (drawing3d || p.Dedicated >= 256L * 1024 * 1024) risky.Add(p.Name);
                }

            string text = Tr.S("Перезапустить видеодрайвер?\r\n\r\n", "Restart the graphics driver?\r\n\r\n")
                        + Tr.S("Экран погаснет на одну-две секунды, затем изображение вернётся. Браузеры и окна Windows восстанавливаются сами, прав администратора не нужно.",
                               "The screen goes dark for a second or two, then the picture comes back. Browsers and Windows itself recover on their own, no administrator rights needed.");
            if (risky.Count > 0)
                text += Tr.S("\r\n\r\nСейчас видеокарту используют программы, которые могут закрыться с ошибкой — сохраните в них работу:\r\n  ",
                             "\r\n\r\nThese programs are using the graphics card right now and may close with an error — save your work in them:\r\n  ")
                      + string.Join("\r\n  ", risky.ToArray());
            text += Tr.S("\r\n\r\nПродолжить?", "\r\n\r\nContinue?");
            if (!MsgAsk(text, Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            long before = GpuUsedNow(null);
            Thread t = new Thread(delegate()
            {
                // Пауза, чтобы окно подтверждения успело исчезнуть: иначе сброс застаёт его
                // посреди анимации закрытия, и на секунду после возврата экрана оно висит призраком.
                Thread.Sleep(400);
                GpuAction r;
                try { r = _engine.GpuResetDriver(); }
                catch (Exception ex) { r = new GpuAction(); r.Message = ex.Message; }
                GpuAction done = r;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    GpuSay(done.Message ?? "");
                    if (done.Ok) GpuScheduleReport(Tr.S("Перезапуск видеодрайвера", "Graphics driver restart"), null, before, 6);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
