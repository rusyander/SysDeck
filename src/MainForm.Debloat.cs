// Windows Process Cleaner — вкладка «Windows: лишнее»: каталог телеметрии, рекламы, Copilot,
// предустановленных приложений, служб и компонентов. Слева дерево с галочками, справа —
// что это, зачем выключать, чем рискуете. Механика — Engine.Debloat.cs, каталог —
// Engine.DebloatCatalog.cs. Сборка: build.bat (csc.exe из .NET Framework 4.x).

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    // Двойной клик по галочке в TreeView с CheckBoxes переключает её на экране, но AfterCheck
    // не приходит — состояние узла и картинка расходятся (старая ошибка WinForms). Второй клик
    // по флажку глотаем: первый уже сработал.
    internal class CheckTree : TreeView
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x203 && CheckBoxes)   // WM_LBUTTONDBLCLK
            {
                TreeViewHitTestInfo h = HitTest(PointToClient(Cursor.Position));
                if (h.Location == TreeViewHitTestLocations.StateImage) { m.Result = IntPtr.Zero; return; }
            }
            base.WndProc(ref m);
        }
    }

    public partial class MainForm
    {
        private TreeView _tvDebloat;
        private RichTextBox _rtbDebloat;
        private Label _lblDebloatInfo;
        private Button _btnDebloatCheck, _btnDebloatDisable, _btnDebloatRemove, _btnDebloatRestore, _btnDebloatRecommended, _btnDebloatNone;
        private Button _btnDebloatStop;
        private List<DebloatItem> _debloatItems;
        private bool _suppressDebloat, _debloatDetected;
        private int _debloatBusy;
        private Font _fontDebloatBold, _fontDebloatTitle;
        // Секундомер вкладки: DISM на один пункт живёт до получаса, и без него на экране
        // неподвижная строка, неотличимая от зависшего приложения.
        private System.Windows.Forms.Timer _debloatTick;
        private DateTime _debloatStarted;
        private string _debloatPhase;

        // ---------- Вкладка: Windows: лишнее ----------
        private Control BuildDebloatTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();
            _btnDebloatCheck = MkFlowButton(Tr.S("Проверить состояние", "Check state"), 180, true);
            _btnDebloatCheck.Click += delegate { RefreshDebloat(true); };
            _btnDebloatDisable = MkFlowButton(Tr.S("Выключить отмеченное", "Disable checked"), 190, false);
            _btnDebloatDisable.Click += delegate { DebloatRun(Engine.DebloatDisable); };
            _btnDebloatRemove = MkFlowButton(Tr.S("Удалить отмеченное", "Remove checked"), 180, false);
            _btnDebloatRemove.Click += delegate { DebloatRun(Engine.DebloatRemove); };
            _btnDebloatRestore = MkFlowButton(Tr.S("Вернуть отмеченное", "Restore checked"), 180, false);
            _btnDebloatRestore.Click += delegate { DebloatRun(Engine.DebloatRestore); };
            _btnDebloatStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnDebloatStop.Enabled = false;
            _btnDebloatStop.Click += delegate { CancelDebloat(); };
            _btnDebloatRecommended = MkFlowButton(Tr.S("Отметить рекомендуемое", "Check recommended"), 200, false);
            _btnDebloatRecommended.Click += delegate { DebloatCheckRecommended(); };
            _btnDebloatNone = MkFlowButton(Tr.S("Снять все", "Uncheck all"), 110, false);
            _btnDebloatNone.Click += delegate { DebloatSetAll(false); };
            top.Controls.Add(_btnDebloatCheck);
            top.Controls.Add(_btnDebloatDisable);
            top.Controls.Add(_btnDebloatRemove);
            top.Controls.Add(_btnDebloatRestore);
            top.Controls.Add(_btnDebloatStop);
            top.Controls.Add(_btnDebloatRecommended);
            top.Controls.Add(_btnDebloatNone);
            // Кнопки «Перезапустить от администратора» здесь больше нет: набор пунктов
            // уходит одним заданием помощнику, и права поднимаются сами — ровно один раз,
            // на само действие. Предлагать перезапуск всего приложения значило бы просить
            // максимальных прав там, где хватает одной операции.

            Label warn = MkNote(Tr.S("Галочкой по умолчанию отмечен только универсальный мусор. Перед каждым действием сохраняется снимок — «Вернуть отмеченное» откатывает по нему.",
                                     "Only universal junk is checked by default. A snapshot is saved before every action — “Restore checked” rolls back from it."), true);
            _lblDebloatInfo = MkNote(Tr.S("Нажмите «Проверить состояние»", "Click “Check state”"), false);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            // Размер задаём ПЕРВЫМ — иначе Panel2MinSize пересчитывает SplitterDistance
            // и бросает исключение в конструкторе формы (см. вкладку «Браузеры»).
            split.Size = new Size(1100, 520);
            split.Panel1MinSize = 320;
            split.Panel2MinSize = 320;
            split.SplitterDistance = 620;
            split.Panel1.Padding = new Padding(1);
            split.Panel2.Padding = new Padding(1);

            _fontDebloatBold = new Font(Font, FontStyle.Bold);
            _fontDebloatTitle = new Font(Font.FontFamily, Font.Size + 2.5F, FontStyle.Bold);

            _tvDebloat = new CheckTree();
            _tvDebloat.Dock = DockStyle.Fill;
            _tvDebloat.HideSelection = false;
            _tvDebloat.BorderStyle = BorderStyle.FixedSingle;
            _tvDebloat.CheckBoxes = true;
            _tvDebloat.ShowLines = false;
            _tvDebloat.ShowRootLines = true;
            _tvDebloat.ItemHeight = Px(24);
            _tvDebloat.DrawMode = TreeViewDrawMode.OwnerDrawText;
            _tvDebloat.DrawNode += Debloat_DrawNode;
            _tvDebloat.AfterCheck += Debloat_AfterCheck;
            _tvDebloat.AfterSelect += delegate(object s, TreeViewEventArgs e) { ShowDebloatNode(e.Node); };
            split.Panel1.Controls.Add(_tvDebloat);

            _rtbDebloat = new RichTextBox();
            _rtbDebloat.Dock = DockStyle.Fill;
            _rtbDebloat.ReadOnly = true;
            _rtbDebloat.WordWrap = true;
            _rtbDebloat.BorderStyle = BorderStyle.FixedSingle;
            _rtbDebloat.Font = Font;
            _rtbDebloat.Text = Tr.S("Выберите пункт слева — здесь будет описание: что это, зачем выключать и чем рискуете.",
                                    "Pick an item on the left — its description appears here: what it is, why disable it, what you risk.");
            Panel outBox = MkBox(_rtbDebloat, new Padding(10, 8, 6, 8));
            outBox.Dock = DockStyle.Fill;
            split.Panel2.Controls.Add(outBox);

            tab.Controls.Add(split);
            tab.Controls.Add(_lblDebloatInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        private static bool IsElevated()
        {
            try { return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        // Узел: «Название · состояние». Название цветом текста, состояние приглушённо;
        // отсутствующие в системе пункты целиком приглушены. Text содержит обе части,
        // чтобы Bounds (а с ними выделение) покрывали всю строку.
        private void Debloat_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            if (e.Node == null || _brSel == null) return;
            Rectangle r = e.Bounds;
            if (r.Width <= 0 || r.Height <= 0) return;
            bool sel = (e.State & TreeNodeStates.Selected) != 0;
            e.Graphics.FillRectangle(sel ? _brSel : _brSurface, r);
            Font f = e.Node.NodeFont != null ? e.Node.NodeFont : _tvDebloat.Font;
            TextFormatFlags flags = TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
            DebloatItem it = e.Node.Tag as DebloatItem;
            string title = it != null ? DebloatNodeTitle(it) : e.Node.Text;
            string rest = e.Node.Text.Length > title.Length ? e.Node.Text.Substring(title.Length) : "";
            bool absent = it != null && (it.State == DebloatState.Absent || it.State == DebloatState.Removed);
            int x = r.X + 2;
            int w = TextRenderer.MeasureText(e.Graphics, title, f, new Size(int.MaxValue, r.Height), flags).Width;
            TextRenderer.DrawText(e.Graphics, title, f, new Rectangle(x, r.Y, Math.Max(1, r.Right - x), r.Height), absent ? _theme.Subtle : _theme.Text, flags);
            if (rest.Length > 0 && x + w < r.Right)
                TextRenderer.DrawText(e.Graphics, rest, f, new Rectangle(x + w, r.Y, r.Right - x - w, r.Height), _theme.Subtle, flags);
        }

        private static string DebloatNodeTitle(DebloatItem it) { return it.Serious ? "⚠ " + it.Title : it.Title; }

        private static string DebloatNodeText(DebloatItem it)
        {
            string st = it.StateText;
            if (string.IsNullOrEmpty(st)) st = Tr.S("не проверено", "not checked");
            if (it.HasSnapshot) st += Tr.S(", есть снимок", ", snapshot saved");
            return DebloatNodeTitle(it) + "  ·  " + st;
        }

        // Галочка категории каскадом ставит/снимает всех детей; пункт, которого нет в системе,
        // отметить нельзя — галочка возвращается. Программные изменения идут под _suppressDebloat.
        private void Debloat_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressDebloat || e.Node == null) return;
            _suppressDebloat = true;
            try
            {
                DebloatItem it = e.Node.Tag as DebloatItem;
                if (it != null)
                {
                    if (e.Node.Checked && !DebloatCheckable(it)) e.Node.Checked = false;
                }
                else
                {
                    int on = 0;
                    foreach (TreeNode ch in e.Node.Nodes)
                    {
                        DebloatItem c = ch.Tag as DebloatItem;
                        ch.Checked = e.Node.Checked && c != null && DebloatCheckable(c);
                        if (ch.Checked) on++;
                    }
                    // категория, в которой отметить нечего (всё отсутствует), галочку не держит
                    if (e.Node.Checked && on == 0) e.Node.Checked = false;
                }
            }
            finally { _suppressDebloat = false; }
            DebloatRememberNode(e.Node);
            UpdateDebloatInfo();
        }

        // Отмечать есть смысл то, что можно выключить/удалить или вернуть.
        private static bool DebloatCheckable(DebloatItem it)
        {
            if (it.Ops.Count == 0) return false;
            if (it.HasSnapshot) return true;
            return it.State != DebloatState.Absent && it.State != DebloatState.Removed;
        }

        // «Остановить» появилась только после того, как движок научился прерывать DISM и
        // PowerShell (CancelDebloatWork). Кнопка гаснет сразу: повторные нажатия по уже
        // отменённой работе выглядели бы как «щёлкаю, а ничего не происходит».
        private void CancelDebloat()
        {
            if (_debloatBusy == 0) return;
            if (_btnDebloatStop != null) _btnDebloatStop.Enabled = false;
            _engine.CancelDebloatWork();
            _debloatPhase = Tr.S("Останавливаю", "Stopping");
        }

        // Строка «выключить 3/12: …» обновлялась раз на пункт и стояла неподвижно десятками
        // минут. Полусекундный таймер дописывает к ней секундомер и живую строку движка
        // (DebloatStatus — последняя строка вывода DISM/PowerShell).
        private void StartDebloatTicker(string phase)
        {
            _debloatPhase = phase;
            _debloatStarted = DateTime.UtcNow;
            if (_debloatTick == null)
            {
                _debloatTick = new System.Windows.Forms.Timer();
                _debloatTick.Interval = 500;
                _debloatTick.Tick += delegate { DebloatTick(); };
            }
            _debloatTick.Start();
            DebloatTick();
        }

        private void StopDebloatTicker()
        {
            if (_debloatTick != null) _debloatTick.Stop();
        }

        private void DebloatTick()
        {
            if (_debloatBusy == 0) { StopDebloatTicker(); return; }
            string st = _engine.DebloatStatus;
            _lblDebloatInfo.Text = _debloatPhase + "   ·   " + Elapsed(DateTime.UtcNow - _debloatStarted)
                + (string.IsNullOrEmpty(st) ? "" : "   ·   " + st)
                + (_engine.DebloatCancelled ? Tr.S("   ·   останавливаюсь…", "   ·   stopping…") : "");
        }

        private void RefreshDebloat(bool force)
        {
            if (_debloatItems == null)
            {
                _debloatItems = _engine.DebloatCatalog();
                PopulateDebloat(true);
            }
            if (!force) return;
            if (Interlocked.CompareExchange(ref _debloatBusy, 1, 0) != 0)
            {
                // раньше здесь был молчаливый return, и повторный щелчок выглядел проигнорированным
                _lblDebloatInfo.Text = Tr.S("Работа уже идёт — дождитесь её или нажмите «Остановить».",
                                            "Work is already running — wait for it or click “Stop”.");
                return;
            }
            _engine.ResetDebloatCancel();
            SetDebloatButtons(false);
            if (_btnDebloatStop != null) _btnDebloatStop.Enabled = true;
            StartDebloatTicker(Tr.S("Проверка состояния", "Checking state"));
            List<DebloatItem> items = _debloatItems;
            Thread t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    _engine.DebloatDetect(items, delegate(string stage)
                    {
                        UiPost(delegate { _debloatPhase = Tr.S("Проверка: ", "Checking: ") + stage; });
                    });
                }
                catch (Exception ex) { err = ex.Message; }
                bool stopped = _engine.DebloatCancelled;
                UiPost(delegate
                {
                    // флаг снимаем внутри UiPost: сброшенный раньше, он пускал новый запуск,
                    // которому этот же обработчик тут же гасил кнопки и обнулял строку состояния
                    Interlocked.Exchange(ref _debloatBusy, 0);
                    StopDebloatTicker();
                    // после остановки состояние прочитано частично — действия к нему применять нельзя
                    _debloatDetected = err == null && !stopped;
                    PopulateDebloat(false);
                    SetDebloatButtons(true);
                    if (_btnDebloatStop != null) _btnDebloatStop.Enabled = false;
                    if (stopped)
                        _lblDebloatInfo.Text = Tr.S("Проверка остановлена — состояние прочитано не полностью, запустите её заново.",
                                                    "The check was stopped — the state is incomplete, run it again.");
                    ShowDebloatNode(_tvDebloat.SelectedNode);
                    if (err != null) MsgError(err);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetDebloatButtons(bool on)
        {
            foreach (Button b in new Button[] { _btnDebloatCheck, _btnDebloatDisable, _btnDebloatRemove, _btnDebloatRestore, _btnDebloatRecommended, _btnDebloatNone })
                if (b != null) b.Enabled = on;
        }

        // Первое заполнение ставит галочки по каталогу (DefaultChecked), повторные — сохраняют
        // выбор пользователя; пункты, которых в системе нет, галочку теряют.
        private void PopulateDebloat(bool first)
        {
            Dictionary<string, bool> keep = new Dictionary<string, bool>();
            string selectedId = null;
            if (!first)
            {
                foreach (TreeNode cat in _tvDebloat.Nodes)
                    foreach (TreeNode n in cat.Nodes) { DebloatItem it = n.Tag as DebloatItem; if (it != null) keep[it.Id] = n.Checked; }
                DebloatItem selIt = _tvDebloat.SelectedNode != null ? _tvDebloat.SelectedNode.Tag as DebloatItem : null;
                if (selIt != null) selectedId = selIt.Id;
            }
            _suppressDebloat = true;
            _tvDebloat.BeginUpdate();
            TreeNode select = null;
            try
            {
                _tvDebloat.Nodes.Clear();
                foreach (string cat in Engine.DebloatCategories())
                {
                    // Полужирный NodeFont шире шрифта дерева, которым контрол считает Bounds: без
                    // хвоста из пробелов последние буквы категории обрезались («Виджеты и новост»).
                    TreeNode cn = new TreeNode(cat + "        ");
                    cn.NodeFont = _fontDebloatBold;
                    int checkedKids = 0, checkable = 0;
                    foreach (DebloatItem it in _debloatItems)
                    {
                        if (it.Category != cat) continue;
                        TreeNode n = new TreeNode(DebloatNodeText(it));
                        n.Tag = it;
                        bool want;
                        // В пределах сеанса выбор берём из keep, при первом показе — из памяти
                        // выбора: отмеченное в прошлый раз переживает перезапуск приложения.
                        if (first || !keep.TryGetValue(it.Id, out want))
                            want = MemBool(DebloatScope, it.Id, it.DefaultChecked, true);
                        bool can = _debloatDetected ? DebloatCheckable(it) : it.Ops.Count > 0;
                        n.Checked = want && can;
                        if (can) checkable++;
                        if (n.Checked) checkedKids++;
                        if (it.Id == selectedId) select = n;
                        cn.Nodes.Add(n);
                    }
                    if (cn.Nodes.Count == 0) continue;
                    cn.Checked = checkable > 0 && checkedKids == checkable;
                    _tvDebloat.Nodes.Add(cn);
                    cn.Expand();
                }
            }
            finally
            {
                _tvDebloat.EndUpdate();
                _suppressDebloat = false;
            }
            if (select != null) _tvDebloat.SelectedNode = select;
            UpdateDebloatInfo();
        }

        private void UpdateDebloatInfo()
        {
            if (_debloatItems == null) return;
            int total = 0, on = 0, admin = 0, marked = 0;
            foreach (TreeNode cat in _tvDebloat.Nodes)
                foreach (TreeNode n in cat.Nodes)
                {
                    DebloatItem it = n.Tag as DebloatItem;
                    if (it == null) continue;
                    total++;
                    if (n.Checked) marked++;
                    if (it.State == DebloatState.On || it.State == DebloatState.Partial) on++;
                    if (it.State == DebloatState.NeedsAdmin) admin++;
                }
            StringBuilder sb = new StringBuilder();
            sb.Append(Tr.S("Пунктов: ", "Items: ")).Append(total).Append(Tr.S("   ·   отмечено: ", "   ·   checked: ")).Append(marked);
            if (_debloatDetected)
            {
                sb.Append(Tr.S("   ·   активно: ", "   ·   active: ")).Append(on);
                if (admin > 0) sb.Append(Tr.S("   ·   нужны права администратора: ", "   ·   need administrator rights: ")).Append(admin);
                if (!_engine.DebloatIsAdmin)
                    sb.Append(Tr.S("   ·   без прав администратора: только текущий пользователь, приложения Store и PowerToys",
                                   "   ·   without administrator rights: current user, Store apps and PowerToys only"));
            }
            else sb.Append(Tr.S("   ·   состояние ещё не проверено", "   ·   state not checked yet"));
            _lblDebloatInfo.Text = sb.ToString();
        }

        private const string DebloatScope = "debloat";

        // «Рекомендуемые» / «Все» / «Ничего» меняют сотню пунктов разом — там записывается всё
        // дерево. На одиночный щелчок обходить его целиком незачем: пишется только тронутый
        // узел (у категории — её дети, которых обработчик и переставил).
        private void DebloatRemember()
        {
            foreach (TreeNode cat in _tvDebloat.Nodes) DebloatRememberNode(cat);
        }

        private void DebloatRememberNode(TreeNode node)
        {
            if (node == null) return;
            DebloatItem it = node.Tag as DebloatItem;
            if (it != null) { MemSetBool(DebloatScope, it.Id, node.Checked, true); return; }
            foreach (TreeNode ch in node.Nodes)
            {
                DebloatItem c = ch.Tag as DebloatItem;
                if (c != null) MemSetBool(DebloatScope, c.Id, ch.Checked, true);
            }
        }

        private void DebloatCheckRecommended()
        {
            if (_debloatItems == null) return;
            _suppressDebloat = true;
            try
            {
                foreach (TreeNode cat in _tvDebloat.Nodes)
                {
                    int kids = 0, on = 0;
                    foreach (TreeNode n in cat.Nodes)
                    {
                        DebloatItem it = n.Tag as DebloatItem;
                        bool can = it != null && (_debloatDetected ? DebloatCheckable(it) : it.Ops.Count > 0);
                        n.Checked = it != null && it.DefaultChecked && can;
                        if (can) kids++;
                        if (n.Checked) on++;
                    }
                    cat.Checked = kids > 0 && on == kids;
                }
            }
            finally { _suppressDebloat = false; }
            DebloatRemember();
            UpdateDebloatInfo();
        }

        private void DebloatSetAll(bool on)
        {
            if (_debloatItems == null) return;
            _suppressDebloat = true;
            try
            {
                foreach (TreeNode cat in _tvDebloat.Nodes)
                {
                    cat.Checked = on;
                    foreach (TreeNode n in cat.Nodes)
                    {
                        DebloatItem it = n.Tag as DebloatItem;
                        n.Checked = on && it != null && DebloatCheckable(it);
                    }
                }
            }
            finally { _suppressDebloat = false; }
            DebloatRemember();
            UpdateDebloatInfo();
        }

        // ---------- Правая панель ----------
        private void ShowDebloatNode(TreeNode node)
        {
            if (_rtbDebloat == null) return;
            DebloatItem it = node != null ? node.Tag as DebloatItem : null;
            if (it == null)
            {
                if (node == null) return;
                int n = 0, on = 0;
                foreach (TreeNode ch in node.Nodes)
                {
                    DebloatItem c = ch.Tag as DebloatItem;
                    if (c == null) continue;
                    n++;
                    if (c.State == DebloatState.On || c.State == DebloatState.Partial) on++;
                }
                RtbBegin();
                RtbLine(node.Text.TrimEnd(), _fontDebloatTitle, _theme.Accent);
                RtbLine(Tr.S("Пунктов: ", "Items: ") + n + (_debloatDetected ? Tr.S(", активно: ", ", active: ") + on : ""), Font, _theme.Subtle);
                RtbLine("", Font, _theme.Text);
                RtbLine(Tr.S("Галочка на категории отмечает все её пункты, которые есть в системе. Выберите пункт, чтобы прочитать, что он делает.",
                             "Checking the category checks every item of it present on this system. Pick an item to read what it does."), Font, _theme.Text);
                RtbEnd();
                return;
            }
            RtbBegin();
            RtbLine(DebloatNodeTitle(it), _fontDebloatTitle, _theme.Accent);
            string st = string.IsNullOrEmpty(it.StateText) ? Tr.S("не проверено", "not checked") : it.StateText;
            RtbLine(Tr.S("Состояние: ", "State: ") + st + (string.IsNullOrEmpty(it.Detail) ? "" : "  ·  " + it.Detail), Font, _theme.Subtle);
            if (it.HasSnapshot) RtbLine(Tr.S("Есть снимок: «Вернуть отмеченное» восстановит прежнее состояние.", "Snapshot saved: “Restore checked” brings the previous state back."), Font, _theme.Subtle);
            RtbLine("", Font, _theme.Text);
            RtbSection(Tr.S("Что это", "What it is"), it.What);
            RtbSection(Tr.S("Зачем выключать", "Why disable it"), it.Pro);
            RtbSection(Tr.S("Чем рискуете", "What you risk"), it.Con);
            string rec = it.Recommend == 2 ? Tr.S("удалить", "remove") : it.Recommend == 1 ? Tr.S("выключить", "disable")
                       : Tr.S("по желанию — оставьте, если пользуетесь", "optional — keep it if you use it");
            RtbSection(Tr.S("Рекомендация", "Recommendation"), rec + (it.Serious ? Tr.S(". Серьёзное изменение — прочитайте «Чем рискуете».", ". A serious change — read “What you risk”.") : ""));
            RtbSection(Tr.S("Как выполняется", "How it is done"), DebloatHowText(it));
            RtbSection(Tr.S("Вернуть", "Restore"), DebloatRestoreText(it));
            RtbEnd();
        }

        private string DebloatHowText(DebloatItem it)
        {
            StringBuilder sb = new StringBuilder();
            foreach (DebloatOp o in it.Ops)
            {
                if (sb.Length > 0) sb.Append("\r\n");
                switch (o.Kind)
                {
                    case "reg": sb.Append(Tr.S("Реестр: ", "Registry: ")).Append(o.Root).Append('\\').Append(o.Key).Append('\\').Append(o.Name).Append(" = ").Append(o.Value); break;
                    case "svc": sb.Append(Tr.S("Служба ", "Service ")).Append(o.Name).Append(Tr.S(": остановить, тип запуска «отключена»", ": stop, startup type “disabled”")); break;
                    case "task": sb.Append(Tr.S("Задача планировщика ", "Scheduled task ")).Append(o.Name).Append(Tr.S(": отключить", ": disable")); break;
                    case "appx":
                        sb.Append(Tr.S("Приложение Store ", "Store app ")).Append(o.Name).Append(": ");
                        sb.Append(o.OnDisable ? Tr.S("«Выключить» снимает у текущего пользователя; ", "“Disable” removes it for the current user; ") : "");
                        sb.Append(Tr.S("«Удалить» снимает у всех пользователей и убирает из образа Windows", "“Remove” removes it for all users and deprovisions it from the Windows image"));
                        break;
                    case "cap": sb.Append(Tr.S("Возможность Windows ", "Windows capability ")).Append(o.Name).Append(Tr.S(": удалить (DISM, нужны права администратора)", ": remove (DISM, administrator rights)")); break;
                    case "feature": sb.Append(Tr.S("Компонент Windows ", "Windows feature ")).Append(o.Name).Append(o.RemovePayload ? Tr.S(": выключить и удалить файлы (DISM, права администратора)", ": disable and remove its files (DISM, administrator rights)") : Tr.S(": выключить (DISM, права администратора)", ": disable (DISM, administrator rights)")); break;
                    case "pt": sb.Append(Tr.S("PowerToys: модуль ", "PowerToys: module ")).Append(o.Name).Append(Tr.S(" выключается в settings.json — так же, как переключатель в настройках", " is switched off in settings.json — same as the toggle in its settings")); break;
                    case "onedrive": sb.Append(Tr.S("OneDrive: «Выключить» — остановить, запретить политикой DisableFileSyncNGSC и снять с автозапуска; «Удалить» — OneDriveSetup.exe /uninstall", "OneDrive: “Disable” — stop, block via the DisableFileSyncNGSC policy and drop from startup; “Remove” — OneDriveSetup.exe /uninstall")); break;
                    default: sb.Append(o.Kind).Append(' ').Append(o.Name); break;
                }
            }
            if (sb.Length == 0) sb.Append(Tr.S("Действий нет — пункт информационный.", "No actions — an informational item."));
            return sb.ToString();
        }

        private static string DebloatRestoreText(DebloatItem it)
        {
            bool pkg = it.HasKind("appx") || it.HasKind("cap") || it.HasKind("feature");
            string s = Tr.S("Перед первым действием прежние значения сохраняются в debloat-snapshot.json; «Вернуть отмеченное» восстанавливает их.",
                            "Before the first action the previous values are saved to debloat-snapshot.json; “Restore checked” puts them back.");
            if (pkg) s += Tr.S(" Приложения Store регистрируются заново из образа Windows, а если их там уже нет — открывается поиск в Store; компоненты и возможности ставятся обратно через DISM.",
                               " Store apps are re-registered from the Windows image, or a Store search opens if they are gone; features and capabilities come back through DISM.");
            if (it.HasKind("onedrive")) s += Tr.S(" Удалённый OneDrive ставится заново через OneDriveSetup.exe.", " A removed OneDrive is reinstalled via OneDriveSetup.exe.");
            return s;
        }

        private void RtbBegin() { _rtbDebloat.SuspendLayout(); _rtbDebloat.Clear(); }
        private void RtbEnd() { _rtbDebloat.SelectionStart = 0; _rtbDebloat.ScrollToCaret(); _rtbDebloat.ResumeLayout(); }

        private void RtbLine(string text, Font f, Color c)
        {
            _rtbDebloat.SelectionStart = _rtbDebloat.TextLength;
            _rtbDebloat.SelectionFont = f;
            _rtbDebloat.SelectionColor = c;
            _rtbDebloat.AppendText(text + "\r\n");
        }

        private void RtbSection(string head, string body)
        {
            if (string.IsNullOrEmpty(body)) return;
            RtbLine(head, _fontDebloatBold, _theme.Text);
            RtbLine(body, Font, _theme.Text);
            RtbLine("", Font, _theme.Text);
        }

        private void ShowDebloatLog(string title, string log)
        {
            RtbBegin();
            RtbLine(title, _fontDebloatTitle, _theme.Accent);
            RtbLine("", Font, _theme.Text);
            RtbLine(log.Length == 0 ? Tr.S("(пусто)", "(empty)") : log, Font, _theme.Text);
            RtbEnd();
        }

        // ---------- Действия ----------
        private bool DebloatApplicable(DebloatItem it, int action)
        {
            if (action == Engine.DebloatRestore) return it.HasSnapshot;
            if (action == Engine.DebloatDisable) return it.CanDisable && (it.State == DebloatState.On || it.State == DebloatState.Partial);
            return it.CanRemove && (it.State == DebloatState.On || it.State == DebloatState.Partial || it.State == DebloatState.Off);
        }

        private void DebloatRun(int action)
        {
            // раньше оба условия давали молчаливый выход, и щелчок выглядел «проглоченным»
            if (_debloatItems == null)
            {
                _lblDebloatInfo.Text = Tr.S("Список пунктов ещё не построен — откройте вкладку заново.",
                                            "The item list is not built yet — reopen the tab.");
                return;
            }
            if (_debloatBusy != 0)
            {
                _lblDebloatInfo.Text = Tr.S("Работа уже идёт — дождитесь её или нажмите «Остановить».",
                                            "Work is already running — wait for it or click “Stop”.");
                return;
            }
            if (!_debloatDetected)
            {
                MessageBox.Show(this, Tr.S("Сначала нажмите «Проверить состояние»: действия применяются к тому, что реально есть в системе.",
                                           "Click “Check state” first: actions apply to what is actually on this system."),
                    Tr.S("Windows: лишнее", "Windows bloat"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            List<DebloatItem> sel = new List<DebloatItem>();
            int skipped = 0;
            foreach (TreeNode cat in _tvDebloat.Nodes)
                foreach (TreeNode n in cat.Nodes)
                {
                    DebloatItem it = n.Tag as DebloatItem;
                    if (it == null || !n.Checked) continue;
                    if (DebloatApplicable(it, action)) sel.Add(it); else skipped++;
                }
            string verb = action == Engine.DebloatRestore ? Tr.S("вернуть", "restore") : action == Engine.DebloatRemove ? Tr.S("удалить", "remove") : Tr.S("выключить", "disable");
            if (sel.Count == 0)
            {
                _lblDebloatInfo.Text = skipped > 0
                    ? Tr.S("Среди отмеченных нечего ", "Nothing among the checked items to ") + verb + Tr.S(": они уже в нужном состоянии или без снимка.", ": they are already in that state or have no snapshot.")
                    : Tr.S("Ничего не отмечено.", "Nothing is checked.");
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(Tr.S("Сейчас: ", "About to: ")).Append(verb).Append(' ').Append(Tr.N(sel.Count, "пункт", "пункта", "пунктов", "item", "items"));
            if (skipped > 0) sb.Append(Tr.S(" (ещё ", " (another ")).Append(skipped).Append(Tr.S(" пропускается — уже в нужном состоянии)", " skipped — already in that state)"));
            sb.Append(".\r\n");
            List<string> serious = new List<string>();
            bool needsAdmin = false;
            foreach (DebloatItem it in sel)
            {
                if (it.Serious) serious.Add(it.Title);
                if (!_engine.DebloatIsAdmin && (it.HasKind("svc") || it.HasKind("feature") || it.HasKind("cap") || it.HasKind("task") || it.HasKind("onedrive"))) needsAdmin = true;
                foreach (DebloatOp o in it.Ops) if (o.Kind == "reg" && o.Root == "HKLM" && !_engine.DebloatIsAdmin) needsAdmin = true;
            }
            if (serious.Count > 0 && action != Engine.DebloatRestore)
                sb.Append("\r\n").Append(Tr.S("⚠ Серьёзные изменения: ", "⚠ Serious changes: ")).Append(string.Join(", ", serious.ToArray())).Append(Tr.S(". Прочитайте «Чем рискуете» у каждого.", ". Read “What you risk” for each.")).Append("\r\n");
            if (action == Engine.DebloatRemove)
                sb.Append("\r\n").Append(Tr.S("Удаление приложений Store действует для всех пользователей и убирает пакет из образа Windows. Вернуть — повторной регистрацией или из Store.",
                                              "Removing Store apps applies to all users and deprovisions the package from the Windows image. Restore = re-register or reinstall from the Store.")).Append("\r\n");
            if (needsAdmin)
                sb.Append("\r\n").Append(Tr.S("Часть действий (HKLM, службы, задачи, компоненты, OneDrive) требует прав администратора: Windows один раз спросит подтверждение на весь список. Откажетесь — эти пункты будут пропущены.",
                                              "Some actions (HKLM, services, tasks, features, OneDrive) need administrator rights: Windows asks for confirmation once for the whole list. Decline and those items are skipped.")).Append("\r\n");
            sb.Append("\r\n").Append(Tr.S("Продолжить?", "Continue?"));
            if (MessageBox.Show(this, sb.ToString(), Tr.S("Windows: лишнее", "Windows bloat"), MessageBoxButtons.YesNo,
                                serious.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes)
            {
                _lblDebloatInfo.Text = Tr.S("Отменено, ничего не изменено.", "Cancelled, nothing was changed.");
                return;
            }

            if (Interlocked.CompareExchange(ref _debloatBusy, 1, 0) != 0)
            {
                _lblDebloatInfo.Text = Tr.S("Работа уже идёт — дождитесь её или нажмите «Остановить».",
                                            "Work is already running — wait for it or click “Stop”.");
                return;
            }
            _engine.ResetDebloatCancel();
            SetDebloatButtons(false);
            if (_btnDebloatStop != null) _btnDebloatStop.Enabled = true;
            StartDebloatTicker(verb + " 1/" + sel.Count);
            string op = Tr.S("Windows: лишнее", "Windows bloat");
            BeginWrite(op);
            List<DebloatItem> items = _debloatItems;
            Thread t = new Thread(delegate()
            {
                StringBuilder log = new StringBuilder();
                int errors = 0, applied = 0;
                try
                {
                    // Права нужны, а у окна их нет: весь набор уходит одним заданием помощнику.
                    // Спрашивать подтверждение на каждый из полутора сотен пунктов бессмысленно —
                    // читать их перестанут после третьего.
                    if (!Elevated && needsAdmin)
                    {
                        ElevResult r = DebloatApplyElevated(action, sel,
                            delegate(string s) { UiPost(delegate { _debloatPhase = verb + ": " + s; }); },
                            delegate { return _engine.DebloatCancelled; });
                        if (r.Lines != null) foreach (string ln in r.Lines) log.AppendLine(ln);
                        applied = r.Count;
                        if (!r.Ok)
                        {
                            errors++;
                            log.AppendLine("  ! " + (r.Declined ? DeclinedNote() : (r.Message ?? "")));
                        }
                    }
                    else
                    for (int i = 0; i < sel.Count; i++)
                    {
                        // отмена проверяется между пунктами; внутри пункта её отрабатывает сам
                        // движок — он убивает текущий DISM/PowerShell, не дожидаясь таймаута
                        if (_engine.DebloatCancelled)
                        {
                            log.AppendLine(Tr.S("Остановлено пользователем; не тронуто пунктов: ", "Stopped by the user; items left untouched: ")
                                           + (sel.Count - i));
                            break;
                        }
                        DebloatItem it = sel[i];
                        int idx = i + 1;
                        UiPost(delegate { _debloatPhase = verb + " " + idx + "/" + sel.Count + ": " + it.Title; });
                        log.AppendLine(it.Title);
                        applied++;
                        string err = null;
                        try { err = _engine.DebloatApply(it, action, log); }
                        catch (Exception ex) { err = ex.Message; }
                        if (err != null) { errors++; log.AppendLine("  ! " + err); }
                    }
                    // повторная проверка после отмены смысла не имеет: её всё равно прервут
                    if (!_engine.DebloatCancelled)
                    {
                        try
                        {
                            _engine.DebloatDetect(items, delegate(string stage)
                            {
                                UiPost(delegate { _debloatPhase = Tr.S("Повторная проверка: ", "Re-checking: ") + stage; });
                            });
                        }
                        catch (Exception ex) { log.AppendLine("! " + ex.Message); }
                    }
                }
                finally { EndWrite(op); }
                string logText = log.ToString();
                int done = applied - errors;
                int appliedCopy = applied;
                bool stopped = _engine.DebloatCancelled;
                UiPost(delegate
                {
                    // флаг снимаем здесь, а не в потоке: иначе щелчок в зазоре запускал новую
                    // работу, которой этот обработчик тут же ломал кнопки и строку состояния
                    Interlocked.Exchange(ref _debloatBusy, 0);
                    StopDebloatTicker();
                    // состояние после остановки прочитано частично — требуем новую проверку
                    if (stopped) _debloatDetected = false;
                    PopulateDebloat(false);
                    SetDebloatButtons(true);
                    if (_btnDebloatStop != null) _btnDebloatStop.Enabled = false;
                    ShowDebloatLog(Tr.S("Журнал: ", "Log: ") + verb, logText);
                    _lblDebloatInfo.Text = (stopped ? Tr.S("Остановлено: ", "Stopped: ") : Tr.S("Готово: ", "Done: "))
                        + verb + " " + done + "/" + sel.Count
                        + (stopped && appliedCopy < sel.Count
                            ? Tr.S(", не тронуто: ", ", left untouched: ") + (sel.Count - appliedCopy)
                              + Tr.S(" — состояние нужно проверить заново", " — the state has to be re-checked")
                            : "")
                        + (errors > 0 ? Tr.S(", с ошибками: ", ", with errors: ") + errors + Tr.S(" — подробности в журнале справа", " — details in the log on the right") : "");
                    if (errors > 0)
                        MessageBox.Show(this, Tr.S("Часть действий не выполнена: ", "Some actions failed: ") + errors + Tr.S(". Журнал — в правой панели.", ". See the log on the right."),
                            op, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
