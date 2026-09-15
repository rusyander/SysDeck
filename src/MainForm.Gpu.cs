// SysDeck — вкладка «Видеопамять»: схема расхода памяти видеокарты и сбросы.
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

namespace SysDeck
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
    }
}
