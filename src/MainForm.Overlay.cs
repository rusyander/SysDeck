// SysDeck — страница «Оверлей»: какие строки показывает столбик поверх всего, у каждой строки — текст,
// график, мин./сред./макс., пороги подсветки, свой интервал и цвета; вид столбика (шрифт, цвета групп, строкой или
// столбиком), три набора строк, запись лагов и ограничитель кадров драйвера NVIDIA; справа живой предпросмотр. Второй вид страницы —
// «Сведения о системе». Сам столбик живёт в процессе --hud (Capture.HudHost.cs): страница пишет настройки и шлёт Reload.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Пока страница открыта, у неё свой сборщик (те же источники, что у оверлея): предпросмотр не зависит от того,
// запущен ли столбик. Ушли со страницы — сборщик освобождается, счётчики не крутятся впустую.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck
{
    // Предпросмотр: картинки рисует HudRender (тот же код, что у настоящего столбика), панель раскладывает их на «обоях»,
    // чтобы прозрачность подложки было видно.
    internal sealed class HudPreviewPanel : Panel
    {
        private Bitmap _column, _selection;
        private string _columnCaption = "", _selectionCaption = "";

        public HudPreviewPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public void SetImages(Bitmap column, string columnCaption, Bitmap selection, string selectionCaption)
        {
            if (_column != null && _column != column) _column.Dispose();
            if (_selection != null && _selection != selection) _selection.Dispose();
            _column = column;
            _selection = selection;
            _columnCaption = columnCaption ?? "";
            _selectionCaption = selectionCaption ?? "";
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle r = ClientRectangle;
            if (r.Width <= 0 || r.Height <= 0) return;
            using (LinearGradientBrush wall = new LinearGradientBrush(r, Color.FromArgb(38, 66, 104), Color.FromArgb(128, 96, 66), 35f))
                g.FillRectangle(wall, r);
            int pad = Math.Max(8, Font.Height / 2), capH = Font.Height + 4;
            int x = pad, y = pad, columnH = 0;
            if (_column != null)
            {
                Caption(g, _columnCaption, x, y);
                // Строка вдоль экрана шире панели — предпросмотр ужимается по ширине, а не обрезается.
                int room = r.Width - pad * 2;
                if (_column.Width > room && room > 0)
                {
                    columnH = Math.Max(1, _column.Height * room / _column.Width);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(_column, new Rectangle(x, y + capH, room, columnH));
                    x += room + pad * 3;
                }
                else
                {
                    columnH = _column.Height;
                    g.DrawImageUnscaled(_column, x, y + capH);
                    x += _column.Width + pad * 3;
                }
            }
            if (_selection != null)
            {
                // Не помещается справа — ставится под столбиком.
                if (_column != null && x + _selection.Width > r.Width - pad) { x = pad; y += capH + columnH + pad * 2; }
                Caption(g, _selectionCaption, x, y);
                g.DrawImageUnscaled(_selection, x, y + capH);
            }
        }

        private void Caption(Graphics g, string text, int x, int y)
        {
            TextFormatFlags f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            TextRenderer.DrawText(g, text, Font, new Point(x + 1, y + 1), Color.FromArgb(20, 20, 24), f);
            TextRenderer.DrawText(g, text, Font, new Point(x, y), Color.White, f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) SetImages(null, null, null, null);
            base.Dispose(disposing);
        }
    }

    // Прокрутка, которая не возвращается к элементу в фокусе при каждой перекладке: подписи оверлея обновляются
    // четыре раза в секунду, и обычная Panel через миг откатывала прокрученную страницу к дереву.
    internal sealed class StillScrollPanel : Panel
    {
        protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }
    }

    public partial class MainForm
    {
        private const string OvGroupTag = "#";     // Tag узла группы = "#" + группа; в идентификаторе показателя «#» не бывает
        private const int OvGroupPreviewRows = 16;

        private CheckTree _tvOv;
        private HudPreviewPanel _ovPreview;
        private Panel _ovSetView, _ovInfoView;
        private FastListView _lvOvInfo;
        private Label _lblOvStatus, _lblOvInfo, _lblOvTitle, _lblOvSource, _lblOvHelp;
        private CheckBox _chkOvText, _chkOvGraph, _chkOvInCaptures, _chkOvHwinfo, _chkOvElevated, _chkOvCoreLoad, _chkOvCoreMhz;
        private RoundComboBox _cmbOvInterval, _cmbOvCorner, _cmbOvMonitor, _cmbOvOpacity, _cmbOvScale, _cmbOvGraphSec, _cmbOvCoreCount;
        private Button _btnOvFps, _btnOvViewSet, _btnOvViewInfo, _btnOvUp, _btnOvDown, _btnOvInfoRefresh;
        private FlowLayoutPanel _ovItemRow1, _ovItemRow2, _ovGroupRow, _ovCoresRow, _ovItemRow3, _ovNvRow;
        private RoundComboBox _cmbOvScene, _cmbOvStatsSec, _cmbOvFont, _cmbOvFontSize, _cmbOvValueColor, _cmbOvLabelColor, _cmbOvNvFps;
        private CheckBox _chkOvBold, _chkOvGroupColors, _chkOvShadow, _chkOvRowLayout, _chkOvStats, _chkOvNoAlarm;
        private TextBox _txtOvWarn, _txtOvCrit, _txtOvNvExe;
        private Label _lblOvThresholds, _lblOvNv;
        private Button _btnOvLag, _btnOvNvApply;
        private int _ovScene;
        private bool _ovNvChecked;

        private List<HudItem> _ovItems = new List<HudItem>();
        private readonly List<HudItem> _ovAllItems = new List<HudItem>();
        private readonly Dictionary<string, TreeNode> _ovNodes = new Dictionary<string, TreeNode>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _ovTitles = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _ovValues = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HudBoard _ovBoard = new HudBoard(), _ovAllBoard = new HudBoard();
        private readonly object _ovGate = new object();
        private HudCollector _ovCollector;
        private HudFrame _ovFrame;
        private System.Threading.Timer _ovSampleTimer;
        private System.Windows.Forms.Timer _ovUiTimer, _ovSaveTimer;
        private Panel _ovScroll;
        // Меньше этой высоты дерево строк и предпросмотр не сжимаются — дальше прокручивается вся страница.
        private const int OvViewMinHeight = 600;
        private int _ovBusy, _ovTicks;
        private bool _ovLoading, _ovSuppress, _ovDirty, _ovPreviewDirty;

        private static readonly int[] OvIntervals = { 0, 250, 500, 1000, 2000, 5000, 10000, 30000, 60000 };
        private static readonly int[] OvOpacities = { 20, 40, 60, 75, 90, 100 };
        private static readonly int[] OvScales = { 75, 100, 125, 150, 200 };
        private static readonly int[] OvGraphSeconds = { 30, 60, 120, 300, 600 };
        private static readonly int[] OvCoreCounts = { 1, 2, 4, 6, 8, 12, 16, 0 };   // 0 — все
        private static readonly int[] OvStatsSeconds = { 0, 30, 60, 120, 300, 600 };  // 0 — как окно графиков
        private static readonly int[] OvFontSizes = { 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32 };
        private static readonly int[] OvNvFps = { 0, 30, 40, 45, 50, 60, 72, 75, 90, 100, 120, 144, 165, 200, 240 };

        // ---------- построение ----------

        private Control BuildOverlayTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel bar = MkToolbar();
            Button toggle = MkFlowButton(Tr.S("Показать / скрыть", "Show / hide"), 170, true);
            toggle.Click += delegate { HudLauncher.ToggleAsync(); };
            _btnOvViewSet = MkFlowButton(Tr.S("Строки столбика", "Column rows"), 160, false);
            _btnOvViewSet.Click += delegate { OvShowView(false); };
            _btnOvViewInfo = MkFlowButton(Tr.S("Сведения о системе", "System information"), 180, false);
            _btnOvViewInfo.Click += delegate { OvShowView(true); };
            Button basic = MkFlowButton(Tr.S("Готовые наборы ▾", "Presets ▾"), 170, false);
            basic.Click += delegate { OvShowPresets(basic); };
            Button move = MkFlowButton(Tr.S("Перетащить мышью", "Drag with the mouse"), 180, false);
            move.Click += delegate
            {
                HudLauncher.CommandAsync("Move", true);
                OvInfo(Tr.S("Столбик обведён рамкой — перетащите его мышью и отпустите. Место запомнится («Положение: своё место»).",
                            "The column is framed — drag it with the mouse and release. The place is remembered (“Position: custom”)."));
            };
            _btnOvLag = MkFlowButton(Tr.S("● Записать лаги", "● Record lags"), 170, false);
            _btnOvLag.Click += delegate { HudLauncher.CommandAsync("LagToggle", true); OvInfo(Tr.S("Команда отправлена оверлею…", "Command sent to the overlay…")); };
            Button reports = MkFlowButton(Tr.S("Отчёты о лагах", "Lag reports"), 150, false);
            reports.Click += delegate { OvOpenReports(); };
            _lblOvStatus = MkFlowLabel("", true);
            bar.Controls.AddRange(new Control[] { toggle, _btnOvViewSet, _btnOvViewInfo, basic, move, _btnOvLag, reports, _lblOvStatus });

            FlowLayoutPanel opts = MkToolbar();
            // Пункты в порядке HudCorner.
            _cmbOvCorner = OvCombo(opts, Tr.S("Положение:", "Position:"), 190, Tr.S("сверху слева", "top left"), Tr.S("сверху справа", "top right"),
                                   Tr.S("снизу слева", "bottom left"), Tr.S("снизу справа", "bottom right"),
                                   Tr.S("слева по центру", "middle left"), Tr.S("справа по центру", "middle right"),
                                   Tr.S("сверху по центру", "top center"), Tr.S("снизу по центру", "bottom center"),
                                   Tr.S("своё место (перетащить)", "custom (dragged)"));
            _cmbOvMonitor = OvCombo(opts, Tr.S("Монитор:", "Monitor:"), 130);
            _cmbOvOpacity = OvCombo(opts, Tr.S("Подложка:", "Background:"), 80, "20 %", "40 %", "60 %", "75 %", "90 %", "100 %");
            _cmbOvScale = OvCombo(opts, Tr.S("Размер:", "Size:"), 80, "75 %", "100 %", "125 %", "150 %", "200 %");
            _cmbOvGraphSec = OvCombo(opts, Tr.S("Окно графиков:", "Graph window:"), 90, "30 " + Tr.S("с", "s"), "1 " + Tr.S("мин", "min"),
                                     "2 " + Tr.S("мин", "min"), "5 " + Tr.S("мин", "min"), "10 " + Tr.S("мин", "min"));
            _cmbOvStatsSec = OvCombo(opts, Tr.S("Мин./сред./макс. за:", "Min/avg/max over:"), 120, Tr.S("как графики", "graph window"), "30 " + Tr.S("с", "s"),
                                     "1 " + Tr.S("мин", "min"), "2 " + Tr.S("мин", "min"), "5 " + Tr.S("мин", "min"), "10 " + Tr.S("мин", "min"));
            _cmbOvScene = OvCombo(opts, Tr.S("Набор строк:", "Row set:"), 90, "1", "2", "3");

            FlowLayoutPanel style = MkToolbar();
            _cmbOvFont = OvCombo(style, Tr.S("Шрифт:", "Font:"), 130, HudStyle.Fonts);
            string[] sizes = new string[OvFontSizes.Length];
            for (int i = 0; i < sizes.Length; i++) sizes[i] = OvFontSizes[i].ToString(CultureInfo.InvariantCulture) + " px";
            _cmbOvFontSize = OvCombo(style, Tr.S("Кегль:", "Size:"), 80, sizes);
            _chkOvBold = OvCheck(style, Tr.S("Жирные подписи", "Bold labels"));
            _chkOvGroupColors = OvCheck(style, Tr.S("Подписи цветом группы", "Labels in group colours"));
            _chkOvShadow = OvCheck(style, Tr.S("Тень под текстом", "Text shadow"));
            _chkOvRowLayout = OvCheck(style, Tr.S("Строкой вдоль экрана", "As a line along the screen"));

            // Ограничитель драйвера NVIDIA — строка видна, только если NVAPI отозвался.
            _ovNvRow = MkToolbar();
            _ovNvRow.Visible = false;
            string[] nvItems = new string[OvNvFps.Length];
            for (int i = 0; i < nvItems.Length; i++) nvItems[i] = OvNvFps[i] == 0 ? Tr.S("без предела", "no limit") : OvNvFps[i].ToString(CultureInfo.InvariantCulture) + " FPS";
            _cmbOvNvFps = OvCombo(_ovNvRow, Tr.S("Предел кадров NVIDIA:", "NVIDIA frame limit:"), 120, nvItems);
            _ovNvRow.Controls.Add(MkFlowLabel(Tr.S("для exe:", "for exe:"), false));
            _txtOvNvExe = new TextBox();
            _txtOvNvExe.Width = Px(170);
            _txtOvNvExe.Margin = new Padding(2, 9, 8, 8);
            _ovNvRow.Controls.Add(_txtOvNvExe);
            Button nvGame = MkFlowButton(Tr.S("Активная игра", "Active game"), 140, false);
            nvGame.Click += delegate { OvNvPickGame(); };
            _btnOvNvApply = MkFlowButton(Tr.S("Применить", "Apply"), 110, true);
            _btnOvNvApply.Click += delegate { OvNvApply(); };
            _lblOvNv = MkFlowLabel(Tr.S("пусто — для всех игр; держит драйвер, в игру ничего не внедряется", "empty — all games; the driver holds it, nothing is injected"), true);
            _ovNvRow.Controls.AddRange(new Control[] { nvGame, _btnOvNvApply, _lblOvNv });

            FlowLayoutPanel checks = MkToolbar();
            _chkOvInCaptures = OvCheck(checks, Tr.S("Виден на своих снимках и видео", "Visible in own screenshots and videos"));
            _chkOvHwinfo = OvCheck(checks, Tr.S("Запускать HWiNFO в фоне ради датчиков", "Run HWiNFO in the background for sensors"));
            _chkOvElevated = OvCheck(checks, Tr.S("С правами администратора (одно окно UAC при включении)", "With administrator rights (one UAC prompt when enabled)"));
            _chkOvElevated.Click += delegate { OvSetElevated(_chkOvElevated.Checked); };
            _btnOvFps = MkFlowButton(Tr.S("Разрешить подсчёт кадров (FPS)…", "Allow frame counting (FPS)…"), 260, false);
            _btnOvFps.Visible = false;
            _btnOvFps.Click += delegate { OvAllowFps(); };
            checks.Controls.Add(_btnOvFps);
            _lblOvInfo = MkNote("", true);

            // --- вид «строки столбика»: дерево слева, правка и предпросмотр справа ---
            _ovSetView = new Panel();
            _ovSetView.Dock = DockStyle.Fill;
            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            split.Size = new Size(1100, 520);      // размер раньше минимумов — см. «Windows: лишнее»
            split.Panel1MinSize = 300;
            split.Panel2MinSize = 360;
            split.SplitterDistance = 440;
            split.Panel1.Padding = new Padding(1);

            _tvOv = new CheckTree();
            _tvOv.Dock = DockStyle.Fill;
            _tvOv.HideSelection = false;
            _tvOv.BorderStyle = BorderStyle.FixedSingle;
            _tvOv.CheckBoxes = true;
            _tvOv.ShowLines = false;
            _tvOv.ShowRootLines = true;
            _tvOv.ItemHeight = Px(24);
            _tvOv.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tvOv.DrawNode += OvDrawNode;
            _tvOv.AfterCheck += OvAfterCheck;
            _tvOv.AfterSelect += delegate { OvShowEditor(); };
            split.Panel1.Controls.Add(_tvOv);

            Panel right = split.Panel2;
            right.Padding = new Padding(10, 0, 0, 0);
            FlowLayoutPanel editor = new FlowLayoutPanel();
            editor.Dock = DockStyle.Top;
            editor.AutoSize = true;
            editor.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            editor.FlowDirection = FlowDirection.TopDown;
            editor.WrapContents = false;
            _lblOvTitle = new Label();
            _lblOvTitle.AutoSize = true;
            _lblOvTitle.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            _lblOvTitle.Name = "section";
            _lblOvTitle.Margin = new Padding(0, 0, 0, 4);
            _lblOvSource = new Label();
            _lblOvSource.AutoSize = true;
            _lblOvSource.Name = "muted";
            _lblOvSource.Font = new Font(Font.FontFamily, 9.5F);
            _lblOvSource.Margin = new Padding(0, 0, 0, 6);
            // Пояснение прямо на странице, а не всплывающей подсказкой: что это, зачем и когда число говорит о проблеме.
            _lblOvHelp = new Label();
            _lblOvHelp.AutoSize = true;
            _lblOvHelp.Font = new Font(Font.FontFamily, 9.5F);
            _lblOvHelp.Margin = new Padding(0, 0, 0, 8);
            editor.Controls.Add(_lblOvTitle);
            editor.Controls.Add(_lblOvSource);
            editor.Controls.Add(_lblOvHelp);

            _ovItemRow1 = OvRow(editor);
            _chkOvText = OvCheck(_ovItemRow1, Tr.S("Число", "Value"));
            _chkOvGraph = OvCheck(_ovItemRow1, Tr.S("График", "Graph"));
            _cmbOvInterval = OvCombo(_ovItemRow1, Tr.S("Обновлять:", "Refresh:"), 170, "", "0,25 " + Tr.S("с", "s"), "0,5 " + Tr.S("с", "s"), "1 " + Tr.S("с", "s"),
                                     "2 " + Tr.S("с", "s"), "5 " + Tr.S("с", "s"), "10 " + Tr.S("с", "s"), "30 " + Tr.S("с", "s"), "1 " + Tr.S("мин", "min"));

            _chkOvStats = OvCheck(_ovItemRow1, Tr.S("Мин./сред./макс.", "Min/avg/max"));

            _ovItemRow2 = OvRow(editor);
            _cmbOvValueColor = OvCombo(_ovItemRow2, Tr.S("Цвет числа:", "Value colour:"), 150, OvColorItems(Tr.S("по уровню", "by level")));
            _cmbOvLabelColor = OvCombo(_ovItemRow2, Tr.S("подписи:", "label:"), 150, OvColorItems(Tr.S("по группе", "by group")));
            _btnOvUp = MkFlowButton(Tr.S("▲ Выше", "▲ Up"), 90, false);
            _btnOvUp.Click += delegate { OvMove(-1); };
            _btnOvDown = MkFlowButton(Tr.S("▼ Ниже", "▼ Down"), 90, false);
            _btnOvDown.Click += delegate { OvMove(1); };
            _ovItemRow2.Controls.AddRange(new Control[] { _btnOvUp, _btnOvDown });

            _ovItemRow3 = OvRow(editor);
            _ovItemRow3.Controls.Add(MkFlowLabel(Tr.S("Жёлтый с:", "Yellow at:"), false));
            _txtOvWarn = OvNumberBox(_ovItemRow3);
            _ovItemRow3.Controls.Add(MkFlowLabel(Tr.S("красный с:", "red at:"), false));
            _txtOvCrit = OvNumberBox(_ovItemRow3);
            _chkOvNoAlarm = OvCheck(_ovItemRow3, Tr.S("не подсвечивать", "no highlighting"));
            _lblOvThresholds = MkFlowLabel("", true);
            _ovItemRow3.Controls.Add(_lblOvThresholds);

            _ovGroupRow = OvRow(editor);
            Button all = MkFlowButton(Tr.S("Отметить всю группу", "Check the whole group"), 190, false);
            all.Click += delegate { OvCheckGroup(true); };
            Button none = MkFlowButton(Tr.S("Снять всю группу", "Uncheck the whole group"), 170, false);
            none.Click += delegate { OvCheckGroup(false); };
            _ovGroupRow.Controls.AddRange(new Control[] { all, none });

            _ovCoresRow = OvRow(editor);
            string[] counts = new string[OvCoreCounts.Length];
            for (int i = 0; i < counts.Length; i++)
                counts[i] = OvCoreCounts[i] == 0 ? Tr.S("все", "all") : Tr.S("первые ", "first ") + OvCoreCounts[i].ToString(CultureInfo.InvariantCulture);
            _cmbOvCoreCount = OvCombo(_ovCoresRow, Tr.S("Ядра:", "Cores:"), 120, counts);
            _cmbOvCoreCount.SelectedIndex = 2;
            _chkOvCoreLoad = OvCheck(_ovCoresRow, Tr.S("загрузка", "load"));
            _chkOvCoreLoad.Checked = true;
            _chkOvCoreMhz = OvCheck(_ovCoresRow, Tr.S("частота", "frequency"));
            Button apply = MkFlowButton(Tr.S("Отметить", "Apply"), 110, false);
            apply.Click += delegate { OvApplyCores(); };
            _ovCoresRow.Controls.Add(apply);

            _ovPreview = new HudPreviewPanel();
            _ovPreview.Dock = DockStyle.Fill;
            right.Controls.Add(_ovPreview);
            right.Controls.Add(editor);
            _ovSetView.Controls.Add(split);

            // --- вид «сведения о системе» ---
            _ovInfoView = new Panel();
            _ovInfoView.Dock = DockStyle.Fill;
            _ovInfoView.Visible = false;
            FlowLayoutPanel infoBar = MkToolbar();
            _btnOvInfoRefresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 110, false);
            _btnOvInfoRefresh.Click += delegate { OvRefreshInfo(); };
            Button copy = MkFlowButton(Tr.S("Скопировать всё", "Copy all"), 160, false);
            copy.Click += delegate { OvCopyInfo(); };
            infoBar.Controls.AddRange(new Control[] { _btnOvInfoRefresh, copy,
                MkFlowLabel(Tr.S("Модель, плата, модули памяти (SMBIOS), видеокарты (DXGI, NVML); тайминги и напряжения — из HWiNFO, когда он даёт данные",
                                 "Model, board, memory modules (SMBIOS), GPUs (DXGI, NVML); timings and voltages come from HWiNFO when it provides data"), true) });
            _lvOvInfo = new FastListView();
            _lvOvInfo.Dock = DockStyle.Fill;
            _lvOvInfo.View = View.Details;
            _lvOvInfo.FullRowSelect = true;
            _lvOvInfo.Columns.Add(Tr.S("Раздел", "Section"), 160);
            _lvOvInfo.Columns.Add(Tr.S("Параметр", "Parameter"), 300);
            _lvOvInfo.Columns.Add(Tr.S("Значение", "Value"), 560);
            SetupOwnerDraw(_lvOvInfo);
            _ovInfoView.Controls.Add(_lvOvInfo);
            _ovInfoView.Controls.Add(infoBar);

            // Прокручиваемая обёртка: на невысоком окне пять панелей сверху съедали место, дереву оставалось ~330 px,
            // а низ правой части обрезался без возможности до него добраться.
            _ovScroll = new StillScrollPanel();
            _ovScroll.Dock = DockStyle.Fill;
            _ovScroll.AutoScroll = true;
            _ovScroll.Controls.Add(_ovSetView);
            _ovScroll.Controls.Add(_ovInfoView);
            _ovScroll.Controls.Add(_lblOvInfo);
            _ovScroll.Controls.Add(_ovNvRow);
            _ovScroll.Controls.Add(checks);
            _ovScroll.Controls.Add(style);
            _ovScroll.Controls.Add(opts);
            _ovScroll.Controls.Add(bar);
            foreach (Control c in _ovScroll.Controls)
                if (c.Dock == DockStyle.Top)
                {
                    c.SizeChanged += delegate { OvFitScroll(); };
                    c.VisibleChanged += delegate { OvFitScroll(); };
                }
            _ovScroll.Resize += delegate { OvFitScroll(); };
            foreach (Control c in _ovScroll.Controls) OvWheelToPage(c);
            tab.Controls.Add(_ovScroll);

            _ovSaveTimer = new System.Windows.Forms.Timer();
            _ovSaveTimer.Interval = 500;
            _ovSaveTimer.Tick += delegate { OvSaveNow(); };
            OvShowView(false);
            return tab;
        }

        // Высота прокрутки = панели сверху + минимум для дерева и предпросмотра. Пристыкованное «Fill» раскладывается
        // по DisplayRectangle, поэтому AutoScrollMinSize одновременно и растягивает вид, и включает полосу прокрутки.
        private void OvFitScroll()
        {
            if (_ovScroll == null) return;
            int top = 0;
            foreach (Control c in _ovScroll.Controls)
                if (c.Visible && c.Dock == DockStyle.Top) top += c.Height;
            int need = top + Px(OvViewMinHeight);
            if (_ovScroll.AutoScrollMinSize.Height != need) _ovScroll.AutoScrollMinSize = new Size(0, need);
        }

        // Колесо над панелями, подписями и предпросмотром листает страницу: сами они его не прокручивают, а до обёртки
        // оно не доходило. Дерево и список сведений листают себя. Над выпадающим списком колесо тоже уходит странице —
        // иначе прокрутка страницы молча меняла бы положение, шрифт или предел кадров.
        private void OvWheelToPage(Control c)
        {
            if (c is TreeView || c is ListView) return;
            c.MouseWheel += OvPageWheel;
            foreach (Control child in c.Controls) OvWheelToPage(child);
        }

        private void OvPageWheel(object sender, MouseEventArgs e)
        {
            HandledMouseEventArgs h = e as HandledMouseEventArgs;
            if (h != null)
            {
                if (h.Handled) return;
                h.Handled = true;
            }
            if (!_ovScroll.VerticalScroll.Visible) return;
            int lines = SystemInformation.MouseWheelScrollLines > 0 ? SystemInformation.MouseWheelScrollLines : 3;
            int y = -_ovScroll.AutoScrollPosition.Y - e.Delta * lines * Px(20) / 120;
            _ovScroll.AutoScrollPosition = new Point(0, Math.Max(0, y));
        }

        // Поле порога: пусто — порог по умолчанию. Сохраняется через полсекунды после ввода.
        private TextBox OvNumberBox(Control parent)
        {
            TextBox t = new TextBox();
            t.Width = Px(60);
            t.Margin = new Padding(2, 9, 12, 8);
            t.TextChanged += delegate { OvControlsChanged(t); };
            parent.Controls.Add(t);
            return t;
        }

        // «по уровню / по группе», палитра, «своё…».
        private static string[] OvColorItems(string auto)
        {
            KeyValuePair<string, int>[] palette = HudStyle.Palette();
            string[] items = new string[palette.Length + 2];
            items[0] = auto;
            for (int i = 0; i < palette.Length; i++) items[i + 1] = palette[i].Key;
            items[items.Length - 1] = Tr.S("своё…", "custom…");
            return items;
        }

        private static int OvColorIndex(int argb)
        {
            if (argb == 0) return 0;
            KeyValuePair<string, int>[] palette = HudStyle.Palette();
            for (int i = 0; i < palette.Length; i++) if (palette[i].Value == argb) return i + 1;
            return palette.Length + 1;
        }

        // Цвет из выпадающего списка; «своё…» открывает выбор цвета. cancelled — окно закрыли, цвет прежний.
        private int OvColorFromCombo(RoundComboBox cb, int current, out bool cancelled)
        {
            cancelled = false;
            KeyValuePair<string, int>[] palette = HudStyle.Palette();
            int i = cb.SelectedIndex;
            if (i <= 0) return 0;
            if (i <= palette.Length) return palette[i - 1].Value;
            using (ColorDialog d = new ColorDialog())
            {
                d.FullOpen = true;
                d.Color = current != 0 ? Color.FromArgb(current) : Color.FromArgb(90, 200, 255);
                if (d.ShowDialog(this) != DialogResult.OK) { cancelled = true; return current; }
                return Color.FromArgb(255, d.Color).ToArgb();
            }
        }

        private static string OvThresholdText(double v)
        {
            return HudFormat.Valid(v) ? v.ToString("0.##", CultureInfo.CurrentCulture) : "";
        }

        private static double OvParseThreshold(string text)
        {
            double v;
            text = (text ?? "").Trim().Replace(',', '.');
            return text.Length > 0 && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : double.NaN;
        }

        private FlowLayoutPanel OvRow(Control parent)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 2, 0, 2);
            parent.Controls.Add(f);
            return f;
        }

        private CheckBox OvCheck(Control parent, string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 9, 18, 8);
            c.CheckedChanged += delegate { OvControlsChanged(c); };
            parent.Controls.Add(c);
            return c;
        }

        private RoundComboBox OvCombo(Control parent, string label, int width, params string[] items)
        {
            // Подпись и список — одним блоком: при узком окне ряд переносится целиком, подпись не отрывается от списка.
            FlowLayoutPanel pair = new FlowLayoutPanel();
            pair.AutoSize = true;
            pair.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            pair.WrapContents = false;
            pair.Margin = Padding.Empty;
            pair.Padding = Padding.Empty;
            parent.Controls.Add(pair);
            parent = pair;
            parent.Controls.Add(MkFlowLabel(label, false));
            RoundComboBox cb = new RoundComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Width = width;
            cb.Margin = new Padding(0, 4, 20, 8);
            cb.Items.AddRange(items);
            cb.SelectedIndexChanged += delegate { OvControlsChanged(cb); };
            parent.Controls.Add(cb);
            return cb;
        }

        private void OvInfo(string text)
        {
            if (_lblOvInfo != null) _lblOvInfo.Text = text ?? "";
        }

        private void OvShowView(bool info)
        {
            _ovInfoView.Visible = info;
            _ovSetView.Visible = !info;
            // Кнопка текущего вида неактивна — видно, где находишься.
            _btnOvViewSet.Enabled = info;
            _btnOvViewInfo.Enabled = !info;
            if (info && _lvOvInfo.Items.Count == 0 && _ready) OvRefreshInfo();
            if (info) AutoFillLastColumnDeferred(_lvOvInfo);
        }

        // ---------- вход и уход ----------

        private void OverlayEnter()
        {
            OvLoad();
            if (_ovUiTimer == null)
            {
                _ovUiTimer = new System.Windows.Forms.Timer();
                _ovUiTimer.Interval = 250;
                _ovUiTimer.Tick += delegate { OvUiTick(); };
            }
            _ovUiTimer.Start();
            OvStartSampling();
            OvRefreshStatus();
            if (_ovInfoView.Visible && _lvOvInfo.Items.Count == 0) OvRefreshInfo();
        }

        private void OverlayLeave()
        {
            if (_ovUiTimer != null) _ovUiTimer.Stop();
            OvSaveNow();
            OvStopSampling();
        }

        // ---------- замер для предпросмотра ----------

        private void OvStartSampling()
        {
            lock (_ovGate)
            {
                if (_ovSampleTimer != null) return;
                _ovSampleTimer = new System.Threading.Timer(delegate { OvSample(); }, null, 0, 250);
            }
        }

        private void OvSample()
        {
            // Замер дольше такта (PDH при нагрузке) не копит очередь.
            if (Interlocked.CompareExchange(ref _ovBusy, 1, 0) != 0) return;
            try
            {
                HudCollector c;
                lock (_ovGate)
                {
                    if (_ovSampleTimer == null) return;
                    if (_ovCollector == null) _ovCollector = new HudCollector();
                    c = _ovCollector;
                }
                HudFrame f = c.Tick();
                lock (_ovGate) { if (_ovSampleTimer != null) _ovFrame = f; }
            }
            catch (Exception ex) { CapLog.Report(ex); }
            finally { Interlocked.Exchange(ref _ovBusy, 0); }
        }

        private void OvStopSampling()
        {
            System.Threading.Timer t;
            lock (_ovGate) { t = _ovSampleTimer; _ovSampleTimer = null; }
            if (t == null) return;
            t.Dispose();
            // Сборщик освобождается в фоне, когда текущий замер закончится; успели вернуться на страницу — остаётся.
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool got = false;
                for (int i = 0; i < 250 && !got; i++)
                {
                    got = Interlocked.CompareExchange(ref _ovBusy, 1, 0) == 0;
                    if (!got) Thread.Sleep(20);
                }
                if (!got) return;
                HudCollector c = null;
                lock (_ovGate)
                {
                    if (_ovSampleTimer == null) { c = _ovCollector; _ovCollector = null; _ovFrame = null; }
                }
                try { if (c != null) c.Dispose(); }
                catch (Exception ex) { CapLog.Report(ex); }
                Interlocked.Exchange(ref _ovBusy, 0);
            });
        }

        private void OvUiTick()
        {
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            _ovTicks++;
            if (_ovTicks % 8 == 1) OvRefreshStatus();
            if (f == null) return;
            DateTime now = DateTime.Now;
            if (_ovTicks % 4 == 1 || _ovAllItems.Count == 0)
            {
                OvSyncTree(f);
                OvUpdateNodeTexts(f);
                OvUpdateSource(f);
            }
            bool changed = _ovBoard.Advance(f, _ovItems, now);
            changed |= _ovAllBoard.Advance(f, _ovAllItems, now);
            if (changed || _ovPreviewDirty) OvRenderPreview();
        }

        private void OvRenderPreview()
        {
            _ovPreviewDirty = false;
            float scale = _dpiScale * OvPick(OvScales, _cmbOvScale, 100) / 100f;
            int opacity = OvPick(OvOpacities, _cmbOvOpacity, 75);
            int seconds = OvPick(OvGraphSeconds, _cmbOvGraphSec, 60);
            int stats = OvPick(OvStatsSeconds, _cmbOvStatsSec, 0);
            HudStyle style = OvStyle();
            style.Opacity = opacity;
            Bitmap column = HudRender.Draw(_ovBoard.Rows(_ovItems, seconds, stats), scale, style);
            string caption;
            List<HudItem> sel = OvSelectionItems(out caption);
            HudStyle selStyle = style.Clone();
            selStyle.RowLayout = false;
            Bitmap selection = sel.Count == 0 ? null : HudRender.Draw(_ovAllBoard.Rows(sel, seconds, stats), scale, selStyle);
            _ovPreview.SetImages(column, Tr.S("Столбик целиком", "The whole column"), selection, caption);
        }

        private HudStyle OvStyle()
        {
            HudStyle st = new HudStyle();
            if (_cmbOvFont.SelectedIndex >= 0) st.Font = HudStyle.Fonts[_cmbOvFont.SelectedIndex];
            st.FontSize = OvPick(OvFontSizes, _cmbOvFontSize, 13);
            st.BoldLabels = _chkOvBold.Checked;
            st.GroupColors = _chkOvGroupColors.Checked;
            st.Shadow = _chkOvShadow.Checked;
            st.RowLayout = _chkOvRowLayout.Checked;
            return st;
        }

        private static int OvPick(int[] values, ComboBox cb, int fallback)
        {
            return cb != null && cb.SelectedIndex >= 0 && cb.SelectedIndex < values.Length ? values[cb.SelectedIndex] : fallback;
        }

        // Выбранная строка — с числом и графиком; выбранная группа — все её строки (первые OvGroupPreviewRows).
        // Цвет — как настроен; интервал — по умолчанию, по нему копится история общего предпросмотра.
        private List<HudItem> OvSelectionItems(out string caption)
        {
            List<HudItem> list = new List<HudItem>();
            caption = "";
            TreeNode n = _tvOv == null ? null : _tvOv.SelectedNode;
            string tag = n == null ? null : n.Tag as string;
            if (tag == null) return list;
            List<string> ids = new List<string>();
            if (tag.StartsWith(OvGroupTag, StringComparison.Ordinal))
            {
                foreach (TreeNode child in n.Nodes) { string id = child.Tag as string; if (id != null) ids.Add(id); }
                caption = OvGroupTitle(tag.Substring(OvGroupTag.Length));
                if (ids.Count > OvGroupPreviewRows)
                {
                    caption += Tr.S(" — первые ", " — first ") + OvGroupPreviewRows.ToString(CultureInfo.InvariantCulture)
                               + Tr.S(" из ", " of ") + ids.Count.ToString(CultureInfo.InvariantCulture);
                    ids.RemoveRange(OvGroupPreviewRows, ids.Count - OvGroupPreviewRows);
                }
            }
            else
            {
                ids.Add(tag);
                caption = Tr.S("Выбранная строка", "Selected row");
            }
            foreach (string id in ids)
            {
                HudItem it = new HudItem(id);
                HudItem cfg = OvFind(id);
                HudDef def = OvDef(id);
                it.Graph = def == null || def.Kind != HudKind.Text;
                if (cfg != null)
                {
                    it.Color = cfg.Color;
                    it.LabelColor = cfg.LabelColor;
                    it.Warn = cfg.Warn;
                    it.Crit = cfg.Crit;
                    it.NoAlarm = cfg.NoAlarm;
                    it.Stats = cfg.Stats;
                }
                list.Add(it);
            }
            return list;
        }

        // ---------- дерево ----------

        private HudDef OvDef(string id)
        {
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            HudValue v = f == null ? null : f.Get(id);
            foreach (HudDef d in HudCatalog.BuiltIn) if (d.Id == id) return d;
            return HudCatalog.Dynamic(id, v);
        }

        private HudItem OvFind(string id)
        {
            foreach (HudItem it in _ovItems) if (it.Id == id) return it;
            return null;
        }

        private static string OvGroupTitle(string group)
        {
            if (group == HudGroups.HwinfoPrefix + "?") return Tr.S("HWiNFO — сейчас без данных", "HWiNFO — no data right now");
            return HudGroups.Title(group);
        }

        // Группы HWiNFO (по датчикам) — перед Afterburner, в порядке появления.
        private static int OvGroupRank(string group)
        {
            return HudGroups.Rank(group);
        }

        private TreeNode OvGroupNode(string group)
        {
            TreeNode g;
            if (_ovNodes.TryGetValue(OvGroupTag + group, out g)) return g;
            g = new TreeNode(OvGroupTitle(group));
            g.Tag = OvGroupTag + group;
            _ovTitles[OvGroupTag + group] = g.Text;
            int rank = OvGroupRank(group), at = _tvOv.Nodes.Count;
            for (int i = 0; i < _tvOv.Nodes.Count; i++)
            {
                string t = _tvOv.Nodes[i].Tag as string;
                if (t != null && OvGroupRank(t.Substring(OvGroupTag.Length)) > rank) { at = i; break; }
            }
            _tvOv.Nodes.Insert(at, g);
            _ovNodes[OvGroupTag + group] = g;
            return g;
        }

        private bool OvEnsureNode(HudDef def)
        {
            if (def == null || _ovNodes.ContainsKey(def.Id)) return false;
            TreeNode g = OvGroupNode(def.Group);
            TreeNode n = new TreeNode(def.Title);
            n.Tag = def.Id;
            n.Checked = OvFind(def.Id) != null;
            g.Nodes.Add(n);
            _ovNodes[def.Id] = n;
            _ovTitles[def.Id] = def.Title;
            HudItem all = new HudItem(def.Id);
            _ovAllItems.Add(all);
            return true;
        }
    }
}
