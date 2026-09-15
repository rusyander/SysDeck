// SysDeck — вкладка «Память»: схема расхода ОЗУ и сбросы.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Вкладка отвечает на один вопрос — «куда делась оперативная память» — и отвечает целиком:
// сумма блоков схемы равна установленному объёму, включая то, что забрало железо, и то, что
// не отнести ни к одному процессу. Разложение считает Engine.Ram.cs, здесь — только показ.
//
// Устройство:
//  * СХЕМА — плитка (treemap): площадь блока пропорциональна занятым байтам. Процессы
//    сгруппированы по имени образа: «chrome.exe × 24» отвечает на вопрос, а двадцать четыре
//    отдельных блока — нет. Щелчок по группе проваливается внутрь, к отдельным процессам;
//  * ПОЛОСА и ГРАФИК сверху и снизу показывают то же самое во времени, раз в секунду;
//  * СПИСКИ — те же данные таблицей: списки страниц, процессы, теги пула драйверов, железо;
//  * СБРОСЫ — пять команд ядра (те же, что в меню Empty у RAMMap) плюс сброс рабочего набора
//    выбранных процессов и их завершение.
//
// Права: ЧИТАТЬ состояние памяти можно без администратора, поэтому вкладка живёт и показывает
// всё сразу после запуска. Права нужны только на сбросы — их поднимает резидентный помощник
// (Elevation.Ram.cs), одно окно UAC на сеанс. Само окно прав не просит: запрос появляется
// только после нажатия на кнопку сброса, как и везде в приложении.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    // Панель с двойной буферизацией: схема и график перерисовываются раз в секунду целиком,
    // и без неё вкладка мерцает.
    internal class RamPanel : Panel
    {
        public RamPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            // Panel по умолчанию не поднимает DoubleClick — без этого проваливание внутрь
            // блока схемы просто не срабатывает.
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
        }
    }

    // Готовый к отрисовке блок схемы: сама доля, её место и уровень вложенности.
    internal class RamCell
    {
        public RamSlice Slice;
        public RectangleF Rect;
        public int Level;
        public RamCell(RamSlice slice, RectangleF rect, int level) { Slice = slice; Rect = rect; Level = level; }
    }

    // Точка истории: ровно те четыре величины, из которых складывается физическая память.
    internal struct RamPoint
    {
        public long Active, Modified, Standby, Free;
    }

    public partial class MainForm
    {
        private const int RamViewMap = 0, RamViewLists = 1, RamViewProcs = 2, RamViewPools = 3, RamViewHw = 4;
        private const string RamScope = "ram";

        private RamSnapshot _ramSnap, _ramPrev;
        private System.Windows.Forms.Timer _ramTimer;
        private int _ramBusy;                        // 0/1 — замер уже идёт
        private int _ramActBusy;                     // 0/1 — идёт сброс
        private int _ramView;
        private bool _ramPaused;
        private int _ramInterval = 1000;
        private string _ramDrill;                    // ключ блока, внутрь которого провалились
        private string _ramSelKey;                   // выбранный блок переживает перерисовку
        private RamSlice _ramSel;
        private string _ramHover;
        private readonly List<RamCell> _ramCells = new List<RamCell>();
        private readonly List<RamPoint> _ramHistory = new List<RamPoint>();
        private readonly HashSet<int> _ramChecked = new HashSet<int>();
        private readonly RamAgent _ramAgent = new RamAgent();
        private int _ramSort = 2;
        private bool _ramSortDesc = true;

        private RamPanel _ramHead, _ramMap, _ramChart;
        private Panel _ramCenter;
        private Label _ramNote, _ramSelLabel, _ramRights;
        private ListView _lvRamLists, _lvRamProcs, _lvRamPools, _lvRamHw;
        private Button[] _ramViewButtons;
        private Button _ramBtnPause, _ramBtnRights, _ramBtnTrim, _ramBtnKill, _ramBtnBack;
        private readonly List<Button> _ramActionButtons = new List<Button>();
        private ComboBox _ramCmbInterval;

        // ------------------------------------------------------------------ //
        //  Сборка вкладки
        // ------------------------------------------------------------------ //
        private Control BuildRamTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            // --- центральная часть: пять представлений, видно одно ---
            _ramCenter = new Panel();
            _ramCenter.Dock = DockStyle.Fill;

            _ramMap = new RamPanel();
            _ramMap.Dock = DockStyle.Fill;
            _ramMap.Paint += RamMapPaint;
            _ramMap.MouseMove += RamMapMouseMove;
            _ramMap.MouseLeave += delegate { _ramHover = null; RamUpdateNote(); };
            _ramMap.MouseDown += RamMapMouseDown;
            _ramMap.MouseDoubleClick += RamMapDoubleClick;
            // Раскладка плитки привязана к размеру панели: без пересчёта после ресайза
            // блоки рисовались бы по старым координатам до следующего такта.
            _ramMap.Resize += delegate { RamBuildCells(); _ramMap.Invalidate(); };

            _lvRamLists = RamMakeList(false, new string[] { Tr.S("Список страниц", "Page list"), Tr.S("Объём", "Size"), Tr.S("Доля", "Share"), Tr.S("Что это", "What it is") },
                                      new int[] { 260, 110, 80, 520 });
            // Заголовки короткие намеренно: «Частный рабочий набор» в колонку не влезает ни при
            // какой ширине, а что это такое, объясняет подсказка внизу и всплывающая на схеме.
            _lvRamProcs = RamMakeList(true, new string[] { Tr.S("Процесс", "Process"), "PID", Tr.S("Частный", "Private"), Tr.S("Рабочий", "Working set"),
                                                           Tr.S("Выделено", "Committed"), Tr.S("Δ за такт", "Δ per tick"), Tr.S("Жёстких/с", "Hard/s"),
                                                           Tr.S("Потоки", "Threads"), Tr.S("Дескрипторы", "Handles"), Tr.S("Сессия", "Session") },
                                      new int[] { 190, 80, 115, 115, 105, 95, 95, 80, 120, 70 });
            // Во всём приложении заголовки списков некликабельны (их рисует тема), но здесь
            // сортировка — половина смысла: «кто вырос за такт» и «кто читает с диска»
            // отвечаются именно перестановкой по колонке. Заголовки рисуются всё той же темой.
            _lvRamProcs.HeaderStyle = ColumnHeaderStyle.Clickable;
            _ramProcHeaders = new string[_lvRamProcs.Columns.Count];
            for (int i = 0; i < _ramProcHeaders.Length; i++) _ramProcHeaders[i] = _lvRamProcs.Columns[i].Text;
            RamMarkSortColumn();
            _lvRamProcs.ColumnClick += RamProcsColumnClick;
            _lvRamProcs.ItemChecked += RamProcsItemChecked;
            _lvRamProcs.SelectedIndexChanged += delegate { RamSyncSelectionFromList(); };

            _lvRamPools = RamMakeList(false, new string[] { Tr.S("Тег", "Tag"), Tr.S("Невыгружаемый", "Non-paged"), Tr.S("Выгружаемый", "Paged"),
                                                            Tr.S("Всего", "Total"), Tr.S("Живых выделений", "Live allocations") },
                                      new int[] { 110, 150, 150, 150, 180 });
            _lvRamHw = RamMakeList(false, new string[] { Tr.S("Раздел", "Section"), Tr.S("Что", "What"), Tr.S("Объём", "Size"), Tr.S("Подробности", "Details") },
                                   new int[] { 150, 320, 130, 420 });

            _ramCenter.Controls.Add(_ramMap);
            _ramCenter.Controls.Add(_lvRamLists);
            _ramCenter.Controls.Add(_lvRamProcs);
            _ramCenter.Controls.Add(_lvRamPools);
            _ramCenter.Controls.Add(_lvRamHw);

            // --- график истории ---
            _ramChart = new RamPanel();
            _ramChart.Dock = DockStyle.Bottom;
            _ramChart.Height = 104;
            _ramChart.Paint += RamChartPaint;

            // --- нижняя строка: что выбрано и что с этим можно сделать ---
            FlowLayoutPanel selBar = MkToolbar();
            selBar.Dock = DockStyle.Bottom;
            selBar.Padding = new Padding(0, 6, 0, 0);
            _ramSelLabel = MkFlowLabel(Tr.S("Ничего не выбрано", "Nothing selected"), false);
            _ramBtnBack = MkFlowButton(Tr.S("← Назад", "← Back"), 110, false);
            _ramBtnBack.Click += delegate { _ramDrill = null; RamRelayout(); };
            _ramBtnBack.Visible = false;
            _ramBtnTrim = MkFlowButton(Tr.S("Сбросить рабочий набор", "Empty working set"), 230, false);
            _ramBtnTrim.Click += delegate { RamTrimSelected(); };
            _ramBtnKill = MkFlowButton(Tr.S("Завершить процессы", "Terminate processes"), 210, false);
            _ramBtnKill.Click += delegate { RamKillSelected(); };
            _ramBtnTrim.Enabled = false;
            _ramBtnKill.Enabled = false;
            selBar.Controls.Add(_ramBtnBack);
            selBar.Controls.Add(_ramBtnTrim);
            selBar.Controls.Add(_ramBtnKill);
            selBar.Controls.Add(_ramSelLabel);

            // --- строка состояния ---
            _ramNote = MkNote(Tr.S("Наведите указатель на блок схемы — здесь появится, что это и сколько занимает.",
                                   "Point at a block of the scheme — what it is and how much it takes shows up here."), true);

            // --- шапка: полоса и главные числа ---
            _ramHead = new RamPanel();
            _ramHead.Dock = DockStyle.Top;
            _ramHead.Height = 132;
            _ramHead.Paint += RamHeadPaint;
            _ramHead.Resize += delegate { RamFitHead(); };

            // --- панель сбросов ---
            FlowLayoutPanel actions = MkToolbar();
            RamAddAction(actions, Tr.S("Рабочие наборы", "Working sets"), 170, Engine.RamEmptyWorkingSets);
            RamAddAction(actions, Tr.S("Системный кэш", "System cache"), 170, Engine.RamEmptySystemWorkingSet);
            RamAddAction(actions, Tr.S("Изменённые", "Modified"), 150, Engine.RamFlushModified);
            RamAddAction(actions, "Standby", 130, Engine.RamPurgeStandby);
            RamAddAction(actions, Tr.S("Standby, приоритет 0", "Standby, priority 0"), 200, Engine.RamPurgeLowStandby);
            RamAddAction(actions, Tr.S("Полный сброс", "Full reset"), 160, Engine.RamEmptyEverything);
            _ramRights = MkFlowLabel("", true);
            _ramBtnRights = MkFlowButton(Tr.S("Получить права", "Get rights"), 170, false);
            _ramBtnRights.Click += delegate { RamEnsureRights(true, null); };
            actions.Controls.Add(_ramBtnRights);
            actions.Controls.Add(_ramRights);

            // --- верхняя панель: представления и опрос ---
            FlowLayoutPanel top = MkToolbar();
            string[] views = { Tr.S("Схема", "Scheme"), Tr.S("Списки страниц", "Page lists"), Tr.S("Процессы", "Processes"),
                               Tr.S("Пулы драйверов", "Driver pools"), Tr.S("Железо", "Hardware") };
            _ramViewButtons = new Button[views.Length];
            for (int i = 0; i < views.Length; i++)
            {
                Button b = MkFlowButton(views[i], 130, i == 0);
                int idx = i;
                b.Click += delegate { RamShowView(idx); };
                top.Controls.Add(b);
                _ramViewButtons[i] = b;
            }
            _ramBtnPause = MkFlowButton(Tr.S("Пауза", "Pause"), 110, false);
            _ramBtnPause.Click += delegate { RamTogglePause(); };
            top.Controls.Add(_ramBtnPause);
            top.Controls.Add(MkFlowLabel(Tr.S("Опрос:", "Poll:"), true));
            _ramCmbInterval = new RoundComboBox();
            _ramCmbInterval.DropDownStyle = ComboBoxStyle.DropDownList;
            _ramCmbInterval.Width = 120;
            _ramCmbInterval.Margin = new Padding(0, 4, 8, 8);
            _ramCmbInterval.Items.AddRange(new object[] { Tr.S("0,5 с", "0.5 s"), Tr.S("1 с", "1 s"), Tr.S("2 с", "2 s"), Tr.S("5 с", "5 s") });
            _ramCmbInterval.SelectedIndexChanged += delegate { RamIntervalChanged(); };
            top.Controls.Add(_ramCmbInterval);

            tab.Controls.Add(_ramCenter);
            tab.Controls.Add(_ramChart);
            tab.Controls.Add(selBar);
            tab.Controls.Add(_ramNote);
            tab.Controls.Add(_ramHead);
            tab.Controls.Add(actions);
            tab.Controls.Add(top);
            return tab;
        }

        private ListView RamMakeList(bool checks, string[] columns, int[] widths)
        {
            ListView lv = new FastListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.FullRowSelect = true;
            lv.CheckBoxes = checks;
            lv.HideSelection = false;
            lv.Visible = false;
            for (int i = 0; i < columns.Length; i++) lv.Columns.Add(columns[i], widths[i]);
            SetupOwnerDraw(lv);
            return lv;
        }

        private void RamAddAction(FlowLayoutPanel bar, string title, int width, int command)
        {
            // Ни одна из этих кнопок не «основная»: синяя подсветка на «Полном сбросе» звала
            // нажать самое разрушительное действие на вкладке.
            Button b = MkFlowButton(title, width, false);
            int cmd = command;
            string name = title;
            b.Click += delegate { RamRunReset(cmd, name); };
            bar.Controls.Add(b);
            _ramActionButtons.Add(b);
        }

        // ------------------------------------------------------------------ //
        //  Жизненный цикл страницы
        // ------------------------------------------------------------------ //

        private void RamEnter()
        {
            if (_ramCmbInterval != null && _ramCmbInterval.SelectedIndex < 0)
            {
                string saved = MemGet(RamScope, "interval", true);
                int idx;
                if (saved == null || !int.TryParse(saved, out idx) || idx < 0 || idx > 3) idx = 1;
                _ramCmbInterval.SelectedIndex = idx;                 // отсюда же выставится _ramInterval
                string view = MemGet(RamScope, "view", true);
                int vi;
                if (view != null && int.TryParse(view, out vi) && vi >= 0 && vi <= RamViewHw) RamShowView(vi);
                else RamShowView(RamViewPools);
            }
            RamUpdateRights();
            if (_ramTimer == null)
            {
                _ramTimer = new System.Windows.Forms.Timer();
                _ramTimer.Tick += delegate { RamTick(); };
            }
            _ramTimer.Interval = _ramInterval;
            if (!_ramPaused) _ramTimer.Start();
            RamTick();
        }

        private void RamLeave()
        {
            if (_ramTimer != null) _ramTimer.Stop();
        }

        // Помощник живёт ровно столько, сколько окно: при выходе флаг остановки уходит ему сам.
        private void RamShutdown()
        {
            RamLeave();
            try { _ramAgent.Stop(); } catch { }
        }

        private void RamTogglePause()
        {
            _ramPaused = !_ramPaused;
            _ramBtnPause.Text = _ramPaused ? Tr.S("Продолжить", "Resume") : Tr.S("Пауза", "Pause");
            if (_ramTimer == null) return;
            if (_ramPaused) _ramTimer.Stop();
            else { _ramTimer.Start(); RamTick(); }
        }

        private void RamIntervalChanged()
        {
            int i = _ramCmbInterval.SelectedIndex;
            int[] ms = { 500, 1000, 2000, 5000 };
            if (i < 0 || i >= ms.Length) return;
            _ramInterval = ms[i];
            if (_ramTimer != null) _ramTimer.Interval = _ramInterval;
            MemSet(RamScope, "interval", i.ToString(CultureInfo.InvariantCulture), true);
        }

        private void RamShowView(int view)
        {
            _ramView = view;
            if (_ramMap != null) _ramMap.Visible = view == RamViewMap;
            if (_lvRamLists != null) _lvRamLists.Visible = view == RamViewLists;
            if (_lvRamProcs != null) _lvRamProcs.Visible = view == RamViewProcs;
            if (_lvRamPools != null) _lvRamPools.Visible = view == RamViewPools;
            if (_lvRamHw != null) _lvRamHw.Visible = view == RamViewHw;
            if (_ramViewButtons != null && _ramViewButtons.Length > 0)
            {
                for (int i = 0; i < _ramViewButtons.Length; i++)
                    _ramViewButtons[i].Tag = i == view ? "primary" : null;
                ApplyThemeTo(_ramViewButtons[0].Parent);       // одна перекраска панели, а не пять
            }
            MemSet(RamScope, "view", view.ToString(CultureInfo.InvariantCulture), true);
            _ramBtnBack.Visible = view == RamViewMap && _ramDrill != null;
            if (_ramSnap != null) RamFillCurrentView();
            RamUpdateSelectionUi();
            RamUpdateNote();
            if (view == RamViewHw) RamFillHardware();
        }

        // ------------------------------------------------------------------ //
        //  Такт опроса
        // ------------------------------------------------------------------ //

        private void RamTick()
        {
            if (_closing || _currentPage != PageRam) return;
            if (Interlocked.CompareExchange(ref _ramBusy, 1, 0) != 0) return;

            RamSnapshot prev = _ramSnap;
            Thread t = new Thread(delegate()
            {
                RamSnapshot s = null;
                string err = null;
                try { s = _engine.RamSample(prev); }
                catch (Exception ex) { err = ex.Message; }
                Interlocked.Exchange(ref _ramBusy, 0);
                RamSnapshot done = s;
                string error = err;
                UiPost(delegate
                {
                    if (done == null)
                    {
                        RamSay(Tr.S("Не удалось прочитать состояние памяти: ", "Could not read the memory state: ")
                               + (error != null ? error : Tr.S("неизвестная ошибка", "unknown error")));
                        return;
                    }
                    _ramPrev = _ramSnap;
                    _ramSnap = done;
                    RamPushHistory(done);
                    RamRelayout();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RamPushHistory(RamSnapshot s)
        {
            RamPoint p = new RamPoint();
            p.Active = s.Active;
            p.Modified = s.Modified + s.ModifiedNoWrite;
            p.Standby = s.Standby;
            p.Free = s.FreePages + s.Zeroed;
            _ramHistory.Add(p);
            while (_ramHistory.Count > 300) _ramHistory.RemoveAt(0);
        }

        // Пересчёт всего, что видно: схема, шапка, график, текущий список.
        private void RamRelayout()
        {
            if (_ramSnap == null) return;
            RamBuildCells();
            RamRestoreSelection();
            RamFillCurrentView();
            _ramBtnBack.Visible = _ramView == RamViewMap && _ramDrill != null;
            if (_ramHead != null) _ramHead.Invalidate();
            if (_ramMap != null) _ramMap.Invalidate();
            if (_ramChart != null) _ramChart.Invalidate();
            RamUpdateNote();
        }

        private void RamFillCurrentView()
        {
            switch (_ramView)
            {
                case RamViewLists: RamFillLists(); break;
                case RamViewProcs: RamFillProcs(); break;
                case RamViewPools: RamFillPools(); break;
            }
            // «Железо» в такт не перечитывается: планки и физические диапазоны за время
            // работы машины не меняются, и Engine отдаёт их из разобранного один раз кэша.
        }

        // ------------------------------------------------------------------ //
        //  Схема
        // ------------------------------------------------------------------ //

        // Корень схемы: либо всё разложение, либо содержимое блока, внутрь которого провалились.
        private List<RamSlice> RamRoot()
        {
            if (_ramSnap == null) return new List<RamSlice>();
            if (_ramDrill == null) return _ramSnap.Slices;
            RamSlice s = RamFind(_ramSnap.Slices, _ramDrill);
            if (s != null && s.Children != null && s.Children.Count > 0) return s.Children;
            _ramDrill = null;                        // блок исчез (процесс закрылся) — назад наверх
            return _ramSnap.Slices;
        }

        private static RamSlice RamFind(List<RamSlice> list, string key)
        {
            if (list == null || key == null) return null;
            foreach (RamSlice s in list)
            {
                if (s.Key == key) return s;
                RamSlice found = RamFind(s.Children, key);
                if (found != null) return found;
            }
            return null;
        }

        private void RamBuildCells()
        {
            SliceBuildCells(_ramMap, RamRoot(), _ramCells);
        }

        // Раскладка плитки для любой схемы из RamSlice — ею пользуются и «Память», и «Видеопамять».
        private void SliceBuildCells(Control map, List<RamSlice> root, List<RamCell> cells)
        {
            cells.Clear();
            if (map == null || !map.IsHandleCreated) return;
            Rectangle client = map.ClientRectangle;
            if (client.Width < 40 || client.Height < 40) return;

            List<RamSlice> shown = new List<RamSlice>();
            foreach (RamSlice s in root) if (s.Bytes > 0) shown.Add(s);
            if (shown.Count == 0) return;

            RectangleF area = new RectangleF(client.X + 1, client.Y + 1, client.Width - 2, client.Height - 2);
            RamSquarify(shown, area, cells, 0);

            // Второй уровень рисуется внутри первого, если блок достаточно велик, чтобы там
            // было что разглядеть. Иначе внутренности только мешают.
            int headerH = Px(19);
            List<RamCell> level0 = new List<RamCell>(cells);
            foreach (RamCell c in level0)
            {
                if (c.Slice.Children == null || c.Slice.Children.Count < 2) continue;
                if (c.Rect.Width < Px(120) || c.Rect.Height < Px(70)) continue;
                RectangleF inner = new RectangleF(c.Rect.X + 2, c.Rect.Y + headerH,
                                                  c.Rect.Width - 4, c.Rect.Height - headerH - 2);
                if (inner.Width < 20 || inner.Height < 16) continue;
                List<RamSlice> kids = new List<RamSlice>();
                foreach (RamSlice k in c.Slice.Children) if (k.Bytes > 0) kids.Add(k);
                if (kids.Count == 0) continue;
                RamSquarify(kids, inner, cells, 1);
            }
        }

        // Плитка по алгоритму squarified: блоки стремятся к квадрату, поэтому подписи в них
        // помещаются, а глазу видно соотношение площадей, а не длину полосок.
        private static void RamSquarify(List<RamSlice> items, RectangleF area, List<RamCell> cells, int level)
        {
            double total = 0;
            foreach (RamSlice s in items) total += s.Bytes;
            if (total <= 0) return;

            int i = 0, n = items.Count;
            RectangleF r = area;
            while (i < n && r.Width > 1f && r.Height > 1f && total > 0)
            {
                double scale = (double)r.Width * r.Height / total;
                double side = Math.Min(r.Width, r.Height);
                int j = i;
                double rowSum = 0, rowMin = 0, rowMax = 0, bestWorst = double.MaxValue;
                while (j < n)
                {
                    double a = items[j].Bytes * scale;
                    if (a <= 0) { j++; continue; }
                    double sum = rowSum + a;
                    double mn = rowSum <= 0 ? a : Math.Min(rowMin, a);
                    double mx = rowSum <= 0 ? a : Math.Max(rowMax, a);
                    double worst = RamWorst(sum, mn, mx, side);
                    if (j > i && worst > bestWorst) break;
                    bestWorst = worst; rowSum = sum; rowMin = mn; rowMax = mx; j++;
                }
                // Ни один элемент не влез (нулевые доли) — берём один принудительно, иначе цикл вечен.
                if (j == i) { rowSum = items[i].Bytes * scale; j = i + 1; }
                if (rowSum <= 0) break;

                float thick = (float)(rowSum / side);
                bool alongHeight = r.Width >= r.Height;
                float pos = 0;
                for (int k = i; k < j; k++)
                {
                    double a = items[k].Bytes * scale;
                    float len = (float)(a / rowSum * side);
                    RectangleF cell = alongHeight
                        ? new RectangleF(r.X, r.Y + pos, thick, len)
                        : new RectangleF(r.X + pos, r.Y, len, thick);
                    cells.Add(new RamCell(items[k], cell, level));
                    pos += len;
                }
                if (alongHeight) r = new RectangleF(r.X + thick, r.Y, r.Width - thick, r.Height);
                else r = new RectangleF(r.X, r.Y + thick, r.Width, r.Height - thick);
                total -= rowSum / scale;
                i = j;
            }
        }

        private static double RamWorst(double sum, double mn, double mx, double side)
        {
            if (sum <= 0 || mn <= 0 || side <= 0) return double.MaxValue;
            double s2 = sum * sum, w2 = side * side;
            return Math.Max(w2 * mx / s2, s2 / (w2 * mn));
        }

        private void RamMapPaint(object sender, PaintEventArgs e)
        {
            SlicePaint(e.Graphics, _ramMap, _ramSnap == null ? null : _ramCells, _ramSel, RamColor,
                       Tr.S("Читаю состояние памяти…", "Reading the memory state…"));
        }

        // Отрисовка плитки из RamCell. cells == null или пусто — вместо схемы подпись empty.
        private void SlicePaint(Graphics g, Control map, List<RamCell> cells, RamSlice selected,
                                Func<int, int, Color> colorOf, string empty)
        {
            g.Clear(_theme.Bg);
            if (cells == null || cells.Count == 0)
            {
                TextRenderer.DrawText(g, empty, Font, map.ClientRectangle, _theme.Subtle,
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            g.SmoothingMode = SmoothingMode.None;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            Font small = new Font(Font.FontFamily, 8.25F);
            Font bold = new Font(Font.FontFamily, 9F, FontStyle.Bold);
            try
            {
                foreach (RamCell c in cells)
                {
                    Rectangle rr = Rectangle.Round(c.Rect);
                    if (rr.Width < 2 || rr.Height < 2) continue;
                    Color fill = colorOf(c.Slice.Kind, c.Level);
                    using (SolidBrush br = new SolidBrush(fill)) g.FillRectangle(br, rr);
                    using (Pen pen = new Pen(_theme.Dark ? Color.FromArgb(28, 0, 0, 0) : Color.FromArgb(48, 255, 255, 255)))
                        g.DrawRectangle(pen, rr);

                    if (rr.Width < Px(44) || rr.Height < Px(18)) continue;
                    Color text = RamTextOn(fill);
                    Rectangle tr = new Rectangle(rr.X + 4, rr.Y + 2, rr.Width - 8, rr.Height - 4);
                    string title = c.Slice.Title;
                    string size = Engine.FormatBytes(c.Slice.Bytes);
                    if (rr.Height >= Px(34) && rr.Width >= Px(90))
                    {
                        TextRenderer.DrawText(g, title, c.Level == 0 ? bold : small, tr, text,
                                              TextFormatFlags.WordEllipsis | TextFormatFlags.NoPrefix);
                        Rectangle sr = new Rectangle(tr.X, tr.Y + Px(15), tr.Width, tr.Height - Px(15));
                        TextRenderer.DrawText(g, size, small, sr, text,
                                              TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    }
                    else
                    {
                        TextRenderer.DrawText(g, title, small, tr, text,
                                              TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix
                                              | TextFormatFlags.VerticalCenter);
                    }
                }

                // Выбранный блок — поверх всех, чтобы рамку не перекрыл сосед.
                if (selected != null)
                    foreach (RamCell c in cells)
                        if (ReferenceEquals(c.Slice, selected))
                        {
                            Rectangle rr = Rectangle.Round(c.Rect);
                            rr.Inflate(-1, -1);
                            if (rr.Width > 2 && rr.Height > 2)
                                using (Pen pen = new Pen(_theme.Accent, Px(2))) g.DrawRectangle(pen, rr);
                        }
            }
            finally { small.Dispose(); bold.Dispose(); }
        }

        // Цвета блоков. Каждый вид — свой оттенок в обеих палитрах; вложенный уровень чуть
        // светлее, чтобы вложенность читалась без рамок.
        private Color RamColor(int kind, int level)
        {
            Color c;
            switch (kind)
            {
                case RamKind.Process: c = Color.FromArgb(59, 130, 246); break;      // синий — процессы
                case RamKind.Compressed: c = Color.FromArgb(139, 92, 246); break;   // фиолетовый — сжатое
                case RamKind.NonPagedPool: c = Color.FromArgb(217, 70, 110); break; // малиновый — ядро
                case RamKind.PagedPool: c = Color.FromArgb(236, 122, 72); break;
                case RamKind.KernelCode: c = Color.FromArgb(180, 83, 60); break;
                case RamKind.SystemCache: c = Color.FromArgb(20, 165, 160); break;  // бирюзовый — кэш
                case RamKind.Unattributed: c = Color.FromArgb(120, 113, 108); break;// серый — «не отнесено»
                case RamKind.Standby: c = Color.FromArgb(46, 160, 96); break;       // зелёный — ожидание
                case RamKind.Modified: c = Color.FromArgb(202, 160, 40); break;
                case RamKind.Free: c = Color.FromArgb(110, 118, 129); break;
                case RamKind.Reserved: c = Color.FromArgb(88, 92, 104); break;
                case RamKind.Bad: c = Color.FromArgb(190, 40, 40); break;
                default: c = _theme.Accent; break;
            }
            if (_theme.Dark) c = ControlPaint.Dark(c, 0.12f);
            if (level > 0) c = _theme.Dark ? ControlPaint.Light(c, 0.25f) : ControlPaint.Light(c, 0.18f);
            return c;
        }

        private static Color RamTextOn(Color back)
        {
            return back.GetBrightness() > 0.58f ? Color.FromArgb(20, 22, 26) : Color.White;
        }

        private RamCell RamHit(Point p)
        {
            // Вложенные блоки добавлены позже — ищем с конца, иначе всегда попадём в родителя.
            for (int i = _ramCells.Count - 1; i >= 0; i--)
                if (_ramCells[i].Rect.Contains(p)) return _ramCells[i];
            return null;
        }

        private void RamMapMouseMove(object sender, MouseEventArgs e)
        {
            RamCell c = RamHit(e.Location);
            string text = c == null ? null : RamDescribe(c.Slice);
            if (text == _ramHover) return;
            _ramHover = text;
            RamUpdateNote();
        }

        private string RamDescribe(RamSlice s)
        {
            if (s == null || _ramSnap == null) return null;
            double share = _ramSnap.Installed > 0 ? 100.0 * s.Bytes / _ramSnap.Installed : 0;
            string text = s.Title + "  ·  " + Engine.FormatBytes(s.Bytes)
                        + "  ·  " + share.ToString("0.0", CultureInfo.InvariantCulture) + " %";
            if (!string.IsNullOrEmpty(s.Hint)) text += "  ·  " + s.Hint;
            return text;
        }

        // Итог действия держится в строке состояния несколько секунд. Без этого «освобождено
        // 1.4 ГБ» жило ровно до следующего такта опроса, то есть меньше секунды, — прочитать
        // его человек не успевал.
        private void RamSay(string text)
        {
            _ramStatus = text;
            _ramStatusUntil = DateTime.UtcNow.AddSeconds(12);
            RamUpdateNote();
        }

        private string _ramStatus;
        private DateTime _ramStatusUntil;

        private void RamUpdateNote()
        {
            if (_ramNote == null) return;
            if (_ramHover != null) { _ramNote.Text = _ramHover; return; }
            if (_ramStatus != null && DateTime.UtcNow < _ramStatusUntil) { _ramNote.Text = _ramStatus; return; }
            _ramStatus = null;
            if (_ramSnap == null) { _ramNote.Text = Tr.S("Читаю состояние памяти…", "Reading the memory state…"); return; }
            if (!_ramSnap.ListsOk)
            {
                _ramNote.Text = Tr.S("Списки страниц система не отдала — показаны только общие цифры.",
                                     "The system did not return the page lists — only the totals are shown.");
                return;
            }
            switch (_ramView)
            {
                case RamViewLists:
                    _ramNote.Text = Tr.S("Все страницы физической памяти, разложенные ядром по спискам; их сумма — весь видимый объём.",
                                         "Every page of physical memory as the kernel files it; the sum is the whole visible amount.");
                    break;
                case RamViewProcs:
                    _ramNote.Text = Tr.S("Отметьте процессы галочками — их можно сбросить или завершить кнопками внизу. Щелчок по заголовку сортирует.",
                                         "Tick processes — the buttons below empty or terminate them. A click on a header sorts.");
                    break;
                case RamViewPools:
                    _ramNote.Text = Tr.S("Четырёхбуквенные теги, которыми драйверы помечают свои выделения в пуле ядра.",
                                         "The four-letter tags drivers stamp on their allocations in the kernel pool.");
                    break;
                case RamViewHw:
                    _ramNote.Text = Tr.S("Планки по данным прошивки и физические адреса, отданные под ОЗУ.",
                                         "The modules as the firmware reports them and the physical addresses given to RAM.");
                    break;
                default:
                    _ramNote.Text = Tr.S("Схема покрывает всю память: ", "The scheme covers all memory: ")
                        + Engine.FormatBytes(_ramSnap.Installed)
                        + Tr.S(". Щелчок по блоку выбирает его, двойной — проваливается внутрь.",
                               ". A click selects a block, a double click drills into it.");
                    break;
            }
        }

        private void RamMapMouseDown(object sender, MouseEventArgs e)
        {
            RamCell c = RamHit(e.Location);
            RamSelect(c == null ? null : c.Slice);
        }

        private void RamMapDoubleClick(object sender, MouseEventArgs e)
        {
            RamCell c = RamHit(e.Location);
            if (c == null) return;
            // Внутрь проваливаются только блоки, у которых есть что показать внутри.
            if (c.Slice.Children != null && c.Slice.Children.Count > 1)
            {
                _ramDrill = c.Slice.Key;
                RamSelect(null);
                RamRelayout();
            }
            else if (c.Slice.Pid != 0) RamKillSelected();
        }

        private void RamSelect(RamSlice s)
        {
            _ramSel = s;
            _ramSelKey = s == null ? null : s.Key;
            RamUpdateSelectionUi();
            if (_ramMap != null) _ramMap.Invalidate();
        }

        // Выбор задан ключом, а не ссылкой: каждый такт строит новые объекты, и без ключа
        // выделение слетало бы раз в секунду.
        private void RamRestoreSelection()
        {
            if (_ramSelKey == null) { _ramSel = null; RamUpdateSelectionUi(); return; }
            _ramSel = RamFind(_ramSnap.Slices, _ramSelKey);
            if (_ramSel == null) _ramSelKey = null;
            RamUpdateSelectionUi();
        }

        private void RamUpdateSelectionUi()
        {
            if (_ramSelLabel == null) return;
            List<int> pids = RamSelectedPids();
            bool any = pids.Count > 0;
            _ramBtnTrim.Enabled = any && _ramActBusy == 0;
            _ramBtnKill.Enabled = any && _ramActBusy == 0;
            if (_ramSel != null)
                _ramSelLabel.Text = Tr.S("Выбрано: ", "Selected: ") + _ramSel.Title
                                  + "  ·  " + Engine.FormatBytes(_ramSel.Bytes)
                                  + (pids.Count > 1 ? Tr.S("  ·  процессов: ", "  ·  processes: ") + pids.Count : "");
            else if (pids.Count > 0)
                _ramSelLabel.Text = Tr.S("Отмечено процессов: ", "Ticked processes: ") + pids.Count;
            else
                _ramSelLabel.Text = Tr.S("Ничего не выбрано", "Nothing selected");
        }

        // Что именно считается выбранным: на схеме — блок, в списке процессов — галочки.
        private List<int> RamSelectedPids()
        {
            List<int> pids = new List<int>();
            if (_ramView == RamViewProcs)
            {
                foreach (int pid in _ramChecked) pids.Add(pid);
                if (pids.Count > 0) return pids;
            }
            if (_ramSel != null && _ramSel.Pids != null) pids.AddRange(_ramSel.Pids);
            return pids;
        }
    }
}
