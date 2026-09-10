// Windows Process Cleaner — вкладка «Браузеры»: дерево профилей, закладки, группы, сеансы, проверка ссылок
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        // ================== Браузеры: данные и структура ==================

        private TreeView _tvBrowser;
        private ListView _lvBrowser;
        private Label _lblBrowserInfo;
        private Button _btnBrowserStop;
        private Button _btnBrowserRefresh, _btnBrowserDelete, _btnBrowserMove, _btnBrowserDup, _btnBrowserLinks;
        private List<BrowserSnapshot> _snapshots;
        // Один флаг на всю вкладку: чтение, проверка ссылок и запись закладок не должны
        // идти вперемешку (см. BeginBrowserOp).
        private int _browserBusy;
        private volatile bool _browserCancel;
        private BusyTicker _browserTicker;
        // Начатые HTTP-запросы проверки: «Стоп» рвёт их, иначе кнопка молчала бы
        // до истечения десятисекундного таймаута каждого.
        private readonly List<HttpWebRequest> _linkRequests = new List<HttpWebRequest>();
        private ContextMenu _browserMenu;

        // Что именно выбрано в дереве. Одного enum мало: нужен и снимок профиля,
        // и конкретный узел, к которому относятся действия.
        private class BrowseTag
        {
            public BrowserSnapshot Snap;
            public string Kind;                 // profile | bookmarks | folder | groups | group | reading | session | window | duplicates
            public BmNode Bm;
            public TabGroupRec Grp;
            public OpenWindowRec Win;
        }

        private Control BuildBrowserTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            _btnBrowserRefresh = MkFlowButton(Tr.S("Прочитать браузеры", "Read browsers"), 190, true);
            _btnBrowserRefresh.Click += delegate { RefreshBrowsers(true); };
            _btnBrowserDelete = MkFlowButton(Tr.S("Удалить выбранное", "Delete selected"), 180, false);
            _btnBrowserDelete.Click += delegate { DeleteSelectedBookmarks(); };
            _btnBrowserMove = MkFlowButton(Tr.S("Переместить…", "Move to…"), 150, false);
            _btnBrowserMove.Click += delegate { MoveSelectedBookmarks(); };
            _btnBrowserDup = MkFlowButton(Tr.S("Дубликаты", "Duplicates"), 130, false);
            _btnBrowserDup.Click += delegate { ShowDuplicates(); };
            _btnBrowserLinks = MkFlowButton(Tr.S("Проверить ссылки", "Check links"), 175, false);
            _btnBrowserLinks.Click += delegate { CheckLinks(); };
            _btnBrowserStop = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnBrowserStop.Enabled = false;
            _btnBrowserStop.Click += delegate { StopBrowserWork(); };

            Label warn = MkNote(Tr.S("Правятся только закладки и только при закрытом браузере (перед записью — копия). Группы вкладок и список чтения — просмотр: они в базе синхронизации, удаление оттуда браузер откатит.",
                                     "Only bookmarks are edited, and only while the browser is closed (a backup is taken first). Tab groups and the reading list are view-only: they live in the sync database, so a deletion there gets rolled back."), true);
            _lblBrowserInfo = MkNote(Tr.S("Нажмите «Прочитать браузеры»", "Click “Read browsers”"), false);

            top.Controls.Add(_btnBrowserRefresh);
            top.Controls.Add(_btnBrowserDelete);
            top.Controls.Add(_btnBrowserMove);
            top.Controls.Add(_btnBrowserDup);
            top.Controls.Add(_btnBrowserLinks);
            top.Controls.Add(_btnBrowserStop);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            // Размер задаём ПЕРВЫМ. У свежего SplitContainer ширина 150, и уже
            // присвоение Panel2MinSize=300 пересчитывает SplitterDistance и бросает
            // InvalidOperationException прямо в конструкторе формы — окно тогда
            // не открывается вообще (проверено: приложение падало на старте).
            split.Size = new Size(1000, 520);
            split.Panel1MinSize = 180;
            split.Panel2MinSize = 300;
            split.FixedPanel = FixedPanel.Panel1;
            split.SplitterDistance = 300;
            split.Panel1.Padding = new Padding(1);   // место под скруглённую рамку (Boxed)
            split.Panel2.Padding = new Padding(1);

            _tvBrowser = new TreeView();
            _tvBrowser.Dock = DockStyle.Fill;
            _tvBrowser.HideSelection = false;
            _tvBrowser.BorderStyle = BorderStyle.FixedSingle;
            _tvBrowser.ShowLines = true;
            _tvBrowser.AfterSelect += delegate(object s, TreeViewEventArgs e) { ShowBrowserNode(e.Node); };
            SetupOwnerDraw(_tvBrowser);   // выделение узла в цветах темы, не системным прямоугольником
            split.Panel1.Controls.Add(_tvBrowser);

            _lvBrowser = new FastListView();
            _lvBrowser.Dock = DockStyle.Fill;
            _lvBrowser.View = View.Details;
            _lvBrowser.CheckBoxes = true;
            _lvBrowser.FullRowSelect = true;
            MemWatch(_lvBrowser, BrowserScope, false, BrowserMemKey);
            // Ширины подобраны так, чтобы все шесть колонок влезали в правую панель
            // при ширине окна по умолчанию. Остаток забирает URL, а не последняя
            // колонка: «Ссылка» держит результат проверки («нет домена», «сертификат»),
            // и как остаточная она схлопывалась до ~45 px — замерено на 1060 px окне.
            _lvBrowser.Columns.Add(Tr.S("Название", "Name"), 175);
            _lvBrowser.Columns.Add("URL", 180);
            _lvBrowser.Columns.Add(Tr.S("Где", "Where"), 90);
            _lvBrowser.Columns.Add(Tr.S("Добавлено", "Added"), 100);
            _lvBrowser.Columns.Add(Tr.S("Открыт.", "Used"), 100);
            _lvBrowser.Columns.Add(Tr.S("Ссылка", "Link"), 100);
            _flexColumn[_lvBrowser] = 1;
            SetupOwnerDraw(_lvBrowser);
            _lvBrowser.DoubleClick += delegate { OpenSelectedUrl(); };
            split.Panel2.Controls.Add(_lvBrowser);

            _browserMenu = new ContextMenu();
            _browserMenu.MenuItems.Add(Tr.S("Открыть в браузере", "Open in browser"), delegate { OpenSelectedUrl(); });
            _browserMenu.MenuItems.Add(Tr.S("Копировать URL", "Copy URL"), delegate { CopySelectedUrl(); });
            _browserMenu.MenuItems.Add("-");
            _browserMenu.MenuItems.Add(Tr.S("Отметить всё", "Check all"), delegate { SetAllBrowserChecks(true); });
            _browserMenu.MenuItems.Add(Tr.S("Снять отметки", "Uncheck all"), delegate { SetAllBrowserChecks(false); });
            _browserMenu.MenuItems.Add("-");
            _browserMenu.MenuItems.Add(Tr.S("Переместить…", "Move to…"), delegate { MoveSelectedBookmarks(); });
            _browserMenu.MenuItems.Add(Tr.S("Удалить выбранное", "Delete selected"), delegate { DeleteSelectedBookmarks(); });
            _lvBrowser.ContextMenu = _browserMenu;

            ContextMenu treeMenu = new ContextMenu();
            treeMenu.MenuItems.Add(Tr.S("Сохранить группу в закладки", "Save group as bookmarks"), delegate { ExportGroupToBookmarks(); });
            treeMenu.MenuItems.Add(Tr.S("Удалить пустые папки", "Delete empty folders"), delegate { RemoveEmptyFolders(); });
            treeMenu.MenuItems.Add("-");
            treeMenu.MenuItems.Add(Tr.S("Удалить эту папку", "Delete this folder"), delegate { DeleteSelectedFolderNode(); });
            _tvBrowser.ContextMenu = treeMenu;

            tab.Controls.Add(split);
            tab.Controls.Add(_lblBrowserInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        // ---------- занятость вкладки ----------

        // Все длинные операции вкладки ходят через эти ворота. Раньше флаг брали только
        // чтение и проверка ссылок, а запись закладок — нет: удаление во время проверки
        // подменяло снимок профиля, дерево и список перестраивались, и восемь потоков
        // проверки дописывали результаты в уже отсоединённые строки — проверка «заканчивалась»,
        // а её результат исчезал бесследно.
        private bool BeginBrowserOp(string stage, bool cancellable)
        {
            if (Interlocked.CompareExchange(ref _browserBusy, 1, 0) != 0)
            {
                _lblBrowserInfo.Text = Tr.S("Уже идёт чтение, проверка ссылок или запись закладок — дождитесь окончания или нажмите «Стоп».",
                                            "A read, a link check or a bookmarks write is already running — wait for it or press “Stop”.");
                return false;
            }
            _browserCancel = false;
            SetBrowserButtons(false);
            _btnBrowserStop.Enabled = cancellable;
            _browserTicker = new BusyTicker(_lblBrowserInfo, stage);
            return true;
        }

        // Только из UI-потока. report == null — текст метки оставляем как есть
        // (его пишет тот, кто заполняет дерево).
        private void EndBrowserOp(string report)
        {
            if (_browserTicker != null) { _browserTicker.Stop(); _browserTicker = null; }
            _btnBrowserStop.Enabled = false;
            SetBrowserButtons(true);
            Interlocked.Exchange(ref _browserBusy, 0);
            if (report != null) _lblBrowserInfo.Text = report;
        }

        // Зовётся из фоновых потоков, поэтому поле читается в локальную переменную.
        private void BrowserStage(string stage)
        {
            BusyTicker t = _browserTicker;
            if (t != null) t.SetStage(stage);
        }

        private void SetBrowserButtons(bool enabled)
        {
            if (_btnBrowserRefresh != null) _btnBrowserRefresh.Enabled = enabled;
            if (_btnBrowserDelete != null) _btnBrowserDelete.Enabled = enabled;
            if (_btnBrowserMove != null) _btnBrowserMove.Enabled = enabled;
            if (_btnBrowserDup != null) _btnBrowserDup.Enabled = enabled;
            if (_btnBrowserLinks != null) _btnBrowserLinks.Enabled = enabled;
        }

        // «Стоп» обязан отзываться мгновенно. Одного флага мало: HEAD-запрос висит до
        // 10 секунд, и всё это время кнопка выглядела живой, но бесполезной. Поэтому
        // гасим саму кнопку, называем стадию и рвём уже начатые запросы.
        private void StopBrowserWork()
        {
            _browserCancel = true;
            _btnBrowserStop.Enabled = false;
            BrowserStage(Tr.S("Останавливаю", "Stopping"));
            AbortLinkRequests();
        }

        private void AbortLinkRequests()
        {
            HttpWebRequest[] arr;
            lock (_linkRequests) { arr = _linkRequests.ToArray(); _linkRequests.Clear(); }
            foreach (HttpWebRequest r in arr) { try { r.Abort(); } catch { } }
        }

        // ---------- чтение ----------

        private void RefreshBrowsers(bool force)
        {
            if (!force && _snapshots != null) return;
            if (!BeginBrowserOp(Tr.S("Чтение профилей браузеров", "Reading browser profiles"), true)) return;
            Thread t = new Thread(delegate()
            {
                List<BrowserSnapshot> res = new List<BrowserSnapshot>();
                string err = null;
                int total = 0, read = 0;
                bool stopped = false;
                try
                {
                    List<BrowserProfile> profiles = BrowserData.FindProfiles();
                    total = profiles.Count;
                    for (int i = 0; i < profiles.Count; i++)
                    {
                        // Один профиль — это чтение всей папки Sync Data\LevelDB и разбор
                        // файлов сеанса, то есть секунды. Проверяем «Стоп» между профилями:
                        // внутри BrowserData.Load прерваться нечем.
                        if (_browserCancel) { stopped = true; break; }
                        BrowserProfile p = profiles[i];
                        BrowserStage(string.Format(Tr.S("Чтение профилей: {0} из {1} · {2}", "Reading profiles: {0} of {1} · {2}"),
                                                   i + 1, total, p.Display));
                        try { res.Add(BrowserData.Load(p)); read++; }
                        catch (Exception ex) { if (err == null) err = p.Display + ": " + ex.Message; }
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                List<BrowserSnapshot> found = res;
                string emsg = err;
                bool cancelled = stopped;
                int okCount = read, all = total;
                UiPost(delegate
                {
                    // Таймер гасим ДО заполнения дерева: иначе его следующий тик затрёт итог.
                    EndBrowserOp(null);
                    // Прерванное на первом же профиле чтение не должно стирать уже прочитанное:
                    // пустой результат в этом случае не подменяет старое дерево.
                    if (found.Count > 0 || !cancelled || _snapshots == null)
                    {
                        _snapshots = found;
                        PopulateBrowserTree();
                    }
                    else _lblBrowserInfo.Text = Tr.S("Прежний список профилей оставлен без изменений.",
                                                     "The previous profile list was left unchanged.");
                    string note = null;
                    if (cancelled)
                        note = string.Format(Tr.S("чтение прервано: прочитано {0} из {1} профилей", "reading stopped: {0} of {1} profiles read"), okCount, all);
                    else if (emsg != null)
                        note = Tr.S("часть профилей прочитать не удалось: ", "some profiles could not be read: ") + emsg;
                    if (note != null) _lblBrowserInfo.Text = _lblBrowserInfo.Text + "   ·   " + note;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private static TreeNode MkNode(string text, BrowseTag tag)
        {
            TreeNode n = new TreeNode(text);
            n.Tag = tag;
            return n;
        }

        private void PopulateBrowserTree()
        {
            _tvBrowser.BeginUpdate();
            try
            {
                _tvBrowser.Nodes.Clear();
                if (_snapshots == null || _snapshots.Count == 0)
                {
                    _lblBrowserInfo.Text = Tr.S("Профили браузеров не найдены.", "No browser profiles found.");
                    return;
                }
                int bm = 0, grp = 0, rd = 0, opn = 0;
                foreach (BrowserSnapshot s in _snapshots)
                {
                    BrowseTag pt = new BrowseTag(); pt.Snap = s; pt.Kind = "profile";
                    TreeNode pn = MkNode(s.Profile.Display, pt);

                    if (s.Roots.Count > 0)
                    {
                        BrowseTag bt = new BrowseTag(); bt.Snap = s; bt.Kind = "bookmarks";
                        TreeNode bn = MkNode(Tr.S("Закладки", "Bookmarks") + " (" + s.BookmarkCount + ")", bt);
                        foreach (BmNode r in s.Roots) bn.Nodes.Add(FolderNode(s, r));
                        pn.Nodes.Add(bn);

                        BrowseTag dt = new BrowseTag(); dt.Snap = s; dt.Kind = "duplicates";
                        pn.Nodes.Add(MkNode(Tr.S("Дубликаты URL", "Duplicate URLs"), dt));
                        bm += s.BookmarkCount;
                    }

                    if (s.Groups.Count > 0)
                    {
                        BrowseTag gt = new BrowseTag(); gt.Snap = s; gt.Kind = "groups";
                        TreeNode gn = MkNode(Tr.S("Группы вкладок", "Tab groups") + " (" + s.Groups.Count + ")", gt);
                        foreach (TabGroupRec g in s.Groups)
                        {
                            BrowseTag t1 = new BrowseTag(); t1.Snap = s; t1.Kind = "group"; t1.Grp = g;
                            gn.Nodes.Add(MkNode(g.Title + "  [" + g.Color + ", " + g.Tabs.Count + "]", t1));
                        }
                        pn.Nodes.Add(gn);
                        grp += s.Groups.Count;
                    }

                    if (s.Reading.Count > 0)
                    {
                        BrowseTag rt = new BrowseTag(); rt.Snap = s; rt.Kind = "reading";
                        pn.Nodes.Add(MkNode(Tr.S("Список для чтения", "Reading list") + " (" + s.Reading.Count + ")", rt));
                        rd += s.Reading.Count;
                    }

                    if (s.Windows.Count > 0)
                    {
                        int tabs = 0;
                        foreach (OpenWindowRec w in s.Windows) tabs += w.Tabs.Count;
                        BrowseTag st = new BrowseTag(); st.Snap = s; st.Kind = "session";
                        TreeNode sn = MkNode(Tr.S("Открытые вкладки", "Open tabs") + " (" + tabs + ")", st);
                        int i = 1;
                        foreach (OpenWindowRec w in s.Windows)
                        {
                            BrowseTag wt = new BrowseTag(); wt.Snap = s; wt.Kind = "window"; wt.Win = w;
                            sn.Nodes.Add(MkNode(Tr.S("Окно ", "Window ") + (i++) + " (" + w.Tabs.Count + ")", wt));
                        }
                        pn.Nodes.Add(sn);
                        opn += tabs;
                    }

                    _tvBrowser.Nodes.Add(pn);
                    // Развёрнут только Chrome: остальные профили сворачиваем, иначе
                    // дерево из 10+ профилей открывается на несколько экранов.
                    if (s.Profile.Browser == "Google Chrome") pn.Expand();
                }
                _lblBrowserInfo.Text = string.Format(
                    Tr.S("Профилей: {0} · закладок: {1} · групп вкладок: {2} · в списке чтения: {3} · открытых вкладок: {4}",
                         "Profiles: {0} · bookmarks: {1} · tab groups: {2} · reading list: {3} · open tabs: {4}"),
                    _snapshots.Count, bm, grp, rd, opn);
            }
            finally { _tvBrowser.EndUpdate(); }
            if (_tvBrowser.Nodes.Count > 0) _tvBrowser.SelectedNode = _tvBrowser.Nodes[0];
        }

        private TreeNode FolderNode(BrowserSnapshot s, BmNode f)
        {
            BrowseTag t = new BrowseTag(); t.Snap = s; t.Kind = "folder"; t.Bm = f;
            int direct = 0;
            foreach (BmNode c in f.Children) if (!c.IsFolder) direct++;
            TreeNode n = MkNode(f.Name + " (" + direct + (f.TotalUrls != direct ? "/" + f.TotalUrls : "") + ")", t);
            foreach (BmNode c in f.Children)
                if (c.IsFolder) n.Nodes.Add(FolderNode(s, c));
            return n;
        }

        // ---------- показ содержимого ----------

        private BrowseTag CurrentTag()
        {
            TreeNode n = _tvBrowser == null ? null : _tvBrowser.SelectedNode;
            return n == null ? null : n.Tag as BrowseTag;
        }

        private void ShowBrowserNode(TreeNode node)
        {
            BrowseTag t = node == null ? null : node.Tag as BrowseTag;
            if (t == null) return;
            List<ListViewItem> rows = new List<ListViewItem>();

            switch (t.Kind)
            {
                case "profile":
                    foreach (string note in t.Snap.Notes) rows.Add(InfoRow(note));
                    if (t.Snap.SessionFileNote != null)
                        rows.Add(InfoRow(Tr.S("Сеанс прочитан из файла ", "Session read from ") + t.Snap.SessionFileNote));
                    rows.Add(InfoRow(t.Snap.Profile.Dir));
                    break;

                case "bookmarks":
                    foreach (BmNode r in t.Snap.Roots) AddBmRows(rows, r, false);
                    break;

                case "folder":
                    AddBmRows(rows, t.Bm, false);
                    break;

                case "duplicates":
                    rows = DuplicateRows(t.Snap);
                    break;

                case "groups":
                    foreach (TabGroupRec g in t.Snap.Groups)
                        foreach (TabRec tr in g.Tabs) rows.Add(TabRow(tr, g.Title));
                    break;

                case "group":
                    foreach (TabRec tr in t.Grp.Tabs) rows.Add(TabRow(tr, t.Grp.Title));
                    break;

                case "reading":
                    foreach (ReadingRec r in t.Snap.Reading)
                    {
                        ListViewItem it = new ListViewItem(r.Title);
                        it.SubItems.Add(r.Url);
                        it.SubItems.Add(r.Read ? Tr.S("прочитано", "read") : Tr.S("не прочитано", "unread"));
                        it.SubItems.Add(Dt(r.Added));
                        it.SubItems.Add(Dt(r.Updated));
                        it.SubItems.Add(r.LinkState ?? "");
                        it.Tag = r;
                        rows.Add(it);
                    }
                    break;

                case "session":
                    foreach (OpenWindowRec w in t.Snap.Windows)
                        foreach (OpenTabRec o in w.Tabs) rows.Add(OpenRow(o));
                    break;

                case "window":
                    foreach (OpenTabRec o in t.Win.Tabs) rows.Add(OpenRow(o));
                    break;
            }

            MemBeginFill();
            _lvBrowser.BeginUpdate();
            try
            {
                _lvBrowser.Items.Clear();
                _lvBrowser.Items.AddRange(rows.ToArray());
                MemEndFill(_lvBrowser, BrowserScope, false, BrowserMemKey, null);
            }
            finally { _lvBrowser.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvBrowser);
        }

        // Память выбора для списка браузера. Строки тут разной природы — закладки, вкладки,
        // список для чтения, — но у всех первые две колонки это название и адрес, чего для
        // опознания хватает. Полка сеансовая: снимок профиля перечитывается каждый раз.
        private const string BrowserScope = "browser.row";

        private static string BrowserMemKey(ListViewItem it)
        {
            if (MemRowSkipped(it)) return null;
            return it.Text + "" + (it.SubItems.Count > 1 ? it.SubItems[1].Text : "");
        }

        private static string Dt(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "-";
        }

        private ListViewItem InfoRow(string text)
        {
            ListViewItem it = new ListViewItem(text);
            for (int i = 1; i < _lvBrowser.Columns.Count; i++) it.SubItems.Add("");
            it.Tag = NoCheckTag;           // информационная строка: без галочки
            return it;
        }

        private ListViewItem TabRow(TabRec t, string group)
        {
            ListViewItem it = new ListViewItem(t.Title);
            it.SubItems.Add(t.Url);
            it.SubItems.Add(group);
            it.SubItems.Add(Dt(t.Created));
            it.SubItems.Add(Dt(t.Updated));
            it.SubItems.Add(t.LinkState ?? "");
            it.Tag = t;
            return it;
        }

        private ListViewItem OpenRow(OpenTabRec o)
        {
            ListViewItem it = new ListViewItem(o.Title);
            it.SubItems.Add(o.Url);
            it.SubItems.Add(o.Group);
            it.SubItems.Add("-");
            it.SubItems.Add("-");
            it.SubItems.Add(o.LinkState ?? "");
            it.Tag = o;
            return it;
        }

        private ListViewItem BmRow(BmNode b)
        {
            ListViewItem it = new ListViewItem((b.IsFolder ? "[" + Tr.S("папка", "folder") + "] " : "") + b.Name);
            it.SubItems.Add(b.IsFolder ? Tr.S("ссылок внутри: ", "links inside: ") + b.TotalUrls : b.Url);
            it.SubItems.Add(b.Parent != null ? b.Parent.PathText : "");
            it.SubItems.Add(Dt(b.Added));
            it.SubItems.Add(Dt(b.LastUsed));
            it.SubItems.Add(b.LinkState ?? "");
            it.Tag = b;
            return it;
        }

        // Внутри папки показываем и вложенные папки: иначе не видно, что папка
        // не пустая, а просто состоит из подпапок.
        private void AddBmRows(List<ListViewItem> rows, BmNode folder, bool recurse)
        {
            foreach (BmNode c in folder.Children)
            {
                rows.Add(BmRow(c));
                if (recurse && c.IsFolder) AddBmRows(rows, c, true);
            }
        }

        private List<ListViewItem> DuplicateRows(BrowserSnapshot s)
        {
            Dictionary<string, List<BmNode>> byUrl = new Dictionary<string, List<BmNode>>(StringComparer.OrdinalIgnoreCase);
            foreach (BmNode r in s.Roots) CollectUrls(r, byUrl);
            List<ListViewItem> rows = new List<ListViewItem>();
            int dupUrls = 0, extra = 0;
            List<string> keys = byUrl.Keys.ToList();
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string k in keys)
            {
                List<BmNode> lst = byUrl[k];
                if (lst.Count < 2) continue;
                dupUrls++;
                // Оставляем ту копию, которую открывали последней, остальные отмечаем.
                lst.Sort(delegate(BmNode a, BmNode b)
                {
                    DateTime da = a.LastUsed ?? a.Added ?? DateTime.MinValue;
                    DateTime db = b.LastUsed ?? b.Added ?? DateTime.MinValue;
                    return db.CompareTo(da);
                });
                for (int i = 0; i < lst.Count; i++)
                {
                    ListViewItem it = BmRow(lst[i]);
                    if (i > 0) { it.Checked = true; extra++; }
                    rows.Add(it);
                }
            }
            _lblBrowserInfo.Text = string.Format(
                Tr.S("Повторяющихся адресов: {0}, лишних копий: {1} (отмечены все, кроме самой свежей)",
                     "Duplicate addresses: {0}, redundant copies: {1} (all but the most recent are checked)"),
                dupUrls, extra);
            return rows;
        }

        private void CollectUrls(BmNode n, Dictionary<string, List<BmNode>> byUrl)
        {
            foreach (BmNode c in n.Children)
            {
                if (c.IsFolder) CollectUrls(c, byUrl);
                else if (!string.IsNullOrEmpty(c.Url))
                {
                    List<BmNode> lst;
                    if (!byUrl.TryGetValue(c.Url, out lst)) { lst = new List<BmNode>(); byUrl[c.Url] = lst; }
                    lst.Add(c);
                }
            }
        }


        // Кнопка «Дубликаты» просто переводит дерево на узел дубликатов текущего
        // профиля: список формируется тем же кодом, что и при выборе узла вручную.
        private void ShowDuplicates()
        {
            BrowseTag cur = CurrentTag();
            BrowserSnapshot want = cur != null ? cur.Snap : null;
            TreeNode found = FindDupNode(_tvBrowser.Nodes, want);
            if (found == null) found = FindDupNode(_tvBrowser.Nodes, null);
            if (found == null)
            {
                MessageBox.Show(this, Tr.S("Сначала прочитайте браузеры.", "Read the browsers first."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            _tvBrowser.SelectedNode = found;
            found.EnsureVisible();
        }

        private TreeNode FindDupNode(TreeNodeCollection nodes, BrowserSnapshot want)
        {
            foreach (TreeNode n in nodes)
            {
                BrowseTag t = n.Tag as BrowseTag;
                if (t != null && t.Kind == "duplicates" && (want == null || t.Snap == want)) return n;
                TreeNode deep = FindDupNode(n.Nodes, want);
                if (deep != null) return deep;
            }
            return null;
        }

        // ---------- мелкие действия ----------

        private string SelectedUrl()
        {
            if (_lvBrowser.SelectedItems.Count == 0) return null;
            object tag = _lvBrowser.SelectedItems[0].Tag;
            BmNode b = tag as BmNode;
            if (b != null) return b.Url;
            TabRec t = tag as TabRec;
            if (t != null) return t.Url;
            ReadingRec r = tag as ReadingRec;
            if (r != null) return r.Url;
            OpenTabRec o = tag as OpenTabRec;
            if (o != null) return o.Url;
            return null;
        }

        private void OpenSelectedUrl()
        {
            string url = SelectedUrl();
            if (string.IsNullOrEmpty(url))
            {
                // Двойной клик по папке, по информационной строке или по записи без адреса
                // раньше не делал вообще ничего — и это было неотличимо от поломки.
                _lblBrowserInfo.Text = _lvBrowser.SelectedItems.Count == 0
                    ? Tr.S("Выберите строку со ссылкой.", "Select a row with a link.")
                    : Tr.S("У этой строки нет адреса — открывать нечего.", "This row has no address — nothing to open.");
                return;
            }
            _lblBrowserInfo.Text = Tr.S("Открываю: ", "Opening: ") + url;
            string target = url;
            // ShellExecute держит поток, пока поднимается холодный браузер, — это секунды
            // с намертво замершим окном, поэтому запуск уходит в фоновый поток.
            Thread t = new Thread(delegate()
            {
                string err = null;
                try { Process.Start(target); }
                catch (Exception ex) { err = ex.Message; }
                string emsg = err;
                UiPost(delegate
                {
                    if (emsg != null)
                        _lblBrowserInfo.Text = Tr.S("Открыть ссылку не удалось: ", "Failed to open the link: ") + emsg;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void CopySelectedUrl()
        {
            string url = SelectedUrl();
            if (string.IsNullOrEmpty(url))
            {
                _lblBrowserInfo.Text = _lvBrowser.SelectedItems.Count == 0
                    ? Tr.S("Выберите строку со ссылкой.", "Select a row with a link.")
                    : Tr.S("У этой строки нет адреса — копировать нечего.", "This row has no address — nothing to copy.");
                return;
            }
            // Буфер обмена занимает другая программа — это обычное дело, и молчаливый
            // отказ выглядел как удачное копирование.
            try
            {
                Clipboard.SetText(url);
                _lblBrowserInfo.Text = Tr.S("Скопировано: ", "Copied: ") + url;
            }
            catch (Exception ex)
            {
                _lblBrowserInfo.Text = Tr.S("Скопировать не удалось: ", "Copy failed: ") + ex.Message;
            }
        }

        private void SetAllBrowserChecks(bool on)
        {
            _lvBrowser.BeginUpdate();
            try { foreach (ListViewItem it in _lvBrowser.Items) if (it.Tag != null && !ReferenceEquals(it.Tag, NoCheckTag)) it.Checked = on; }
            finally { _lvBrowser.EndUpdate(); }
        }

        // ---------- изменение закладок ----------

        // Общая проверка «есть с чем работать». Без прочитанного дерева все кнопки правки
        // молча возвращались из-за пустого CurrentTag(), и клик выглядел как поломка.
        private BrowseTag CurrentWritableTag()
        {
            BrowseTag t = CurrentTag();
            if (t == null || t.Snap == null)
            {
                _lblBrowserInfo.Text = Tr.S("Сначала нажмите «Прочитать браузеры» и выберите профиль или папку в дереве слева.",
                                            "Click “Read browsers” first, then pick a profile or a folder in the tree on the left.");
                return null;
            }
            return t;
        }

        // Дешёвая часть проверки: она смотрит только на уже прочитанные данные.
        private bool CanWriteSync(BrowserSnapshot s)
        {
            if (s == null || s.BookmarksDoc == null)
            {
                MessageBox.Show(this, Tr.S("Для этого профиля закладки не прочитаны.", "Bookmarks were not read for this profile."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (!s.ChecksumOk)
            {
                MessageBox.Show(this,
                    string.Format(Tr.S("{0} считает контрольную сумму файла закладок по-своему — наша не совпала с записанной. Править файл в таком виде опасно: браузер сочтёт его повреждённым.\n\nЭтот профиль доступен только для просмотра.",
                                       "{0} computes the bookmarks checksum differently — ours does not match the stored one. Editing the file would make the browser treat it as corrupted.\n\nThis profile is view-only."),
                                  s.Profile.Browser),
                    Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        // Дорогая часть: Process.GetProcessesByName перебирает процессы всей системы и на
        // нагруженной машине даёт заметную паузу. Раньше она шла в UI-потоке перед каждым
        // диалогом подтверждения — окно замирало ещё до того, как пользователя о чём-то спросили.
        // Продолжение действия (onClosed) возвращается в UI-поток, порядок шагов сохранён:
        // проверка «браузер закрыт» была и остаётся ДО вопроса «удалить?».
        // Вызывать только внутри взятых BeginBrowserOp ворот: отказ их отпускает.
        private void CheckBrowserClosed(BrowserSnapshot s, MethodInvoker onClosed)
        {
            BrowserStage(Tr.S("Проверка, закрыт ли браузер", "Checking whether the browser is closed"));
            Thread t = new Thread(delegate()
            {
                bool running;
                string err = null;
                try { running = BrowserData.IsRunning(s.Profile); }
                catch (Exception ex) { running = true; err = ex.Message; }   // не смогли проверить — считаем запущенным
                bool run = running;
                string emsg = err;
                UiPost(delegate
                {
                    if (!run) { onClosed(); return; }
                    EndBrowserOp(emsg != null
                        ? Tr.S("Проверить, запущен ли браузер, не удалось: ", "Could not check whether the browser is running: ") + emsg
                        : Tr.S("Браузер запущен — правка отменена.", "The browser is running — the edit was cancelled."));
                    MessageBox.Show(this,
                        emsg != null
                            ? Tr.S("Не удалось проверить, запущен ли браузер: ", "Could not check whether the browser is running: ") + emsg
                            : string.Format(Tr.S("{0} сейчас запущен. Он держит закладки в памяти и перезапишет файл при выходе — правка будет потеряна.\n\nЗакройте браузер полностью и повторите.",
                                                 "{0} is running. It keeps bookmarks in memory and rewrites the file on exit, so the edit would be lost.\n\nClose the browser completely and try again."),
                                          s.Profile.Browser),
                        Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private List<BmNode> CheckedBookmarks()
        {
            List<BmNode> res = new List<BmNode>();
            foreach (ListViewItem it in _lvBrowser.Items)
            {
                if (!it.Checked) continue;
                BmNode b = it.Tag as BmNode;
                if (b != null) res.Add(b);
            }
            return res;
        }

        private static bool DetachNode(BmNode n)
        {
            if (n == null || n.Parent == null || n.Parent.Raw == null) return false;
            JVal kids = n.Parent.Raw.Get("children");
            if (kids == null || kids.Kind != JKind.Arr) return false;
            if (!kids.V.Remove(n.Raw)) return false;
            n.Parent.Children.Remove(n);
            return true;
        }

        // Отбрасываем узлы, чей предок тоже отмечен: иначе одно и то же удалялось бы дважды.
        private static List<BmNode> TopMostNodes(List<BmNode> nodes)
        {
            HashSet<BmNode> set = new HashSet<BmNode>(nodes);
            List<BmNode> res = new List<BmNode>();
            foreach (BmNode n in nodes)
            {
                bool covered = false;
                for (BmNode p = n.Parent; p != null; p = p.Parent)
                    if (set.Contains(p)) { covered = true; break; }
                if (!covered) res.Add(n);
            }
            return res;
        }

        private void DeleteSelectedBookmarks()
        {
            BrowseTag t = CurrentWritableTag();
            if (t == null) return;
            List<BmNode> sel = TopMostNodes(CheckedBookmarks());
            if (sel.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Отметьте галочками, что удалить. Группы вкладок, список для чтения и открытые вкладки отсюда не удаляются.",
                                           "Tick what to delete. Tab groups, the reading list and open tabs cannot be deleted from here."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!CanWriteSync(t.Snap)) return;
            if (!BeginBrowserOp(Tr.S("Подготовка удаления", "Preparing the deletion"), false)) return;

            BrowserSnapshot snap = t.Snap;
            CheckBrowserClosed(snap, delegate
            {
                int links = 0, folders = 0, inside = 0;
                foreach (BmNode n in sel)
                {
                    if (n.IsFolder) { folders++; inside += n.TotalUrls; }
                    else links++;
                }
                string msg = string.Format(
                    Tr.S("Удалить: ссылок — {0}, папок — {1} (внутри них ещё {2} ссылок)?\n\nБудет сохранена копия файла закладок.",
                         "Delete {0} links and {1} folders (holding {2} more links)?\n\nA backup of the bookmarks file will be saved."),
                    links, folders, inside);
                BrowserStage(Tr.S("Ожидание подтверждения", "Waiting for confirmation"));
                if (MessageBox.Show(this, msg, Tr.S("Удаление закладок", "Delete bookmarks"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { EndBrowserOp(Tr.S("Удаление отменено.", "Deletion cancelled.")); return; }

                int done = 0;
                foreach (BmNode n in sel) if (DetachNode(n)) done++;
                if (done == 0)
                { EndBrowserOp(Tr.S("Удалять нечего: отмеченных записей в дереве уже нет.", "Nothing to delete: the ticked entries are no longer in the tree.")); return; }
                CommitBookmarks(snap, string.Format(Tr.S("Удалено записей: {0}", "Deleted entries: {0}"), done));
            });
        }

        private void DeleteSelectedFolderNode()
        {
            BrowseTag t = CurrentWritableTag();
            if (t == null) return;
            if (t.Kind != "folder" || t.Bm == null)
            {
                // Пункт меню есть на каждом узле дерева, поэтому на профиле, «Закладках»
                // или «Группах вкладок» он раньше просто ничего не делал.
                _lblBrowserInfo.Text = Tr.S("Выберите в дереве папку закладок — этот узел удалить нельзя.",
                                            "Pick a bookmarks folder in the tree — this node cannot be deleted.");
                return;
            }
            if (t.Bm.Parent == null)
            {
                MessageBox.Show(this, Tr.S("Это корневая папка браузера, её удалить нельзя.", "This is a browser root folder, it cannot be deleted."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!CanWriteSync(t.Snap)) return;
            if (!BeginBrowserOp(Tr.S("Подготовка удаления папки", "Preparing the folder deletion"), false)) return;

            BrowserSnapshot snap = t.Snap;
            BmNode folder = t.Bm;
            CheckBrowserClosed(snap, delegate
            {
                string msg = string.Format(Tr.S("Удалить папку «{0}» вместе с {1} ссылками внутри?", "Delete folder “{0}” with {1} links inside?"),
                                           folder.Name, folder.TotalUrls);
                BrowserStage(Tr.S("Ожидание подтверждения", "Waiting for confirmation"));
                if (MessageBox.Show(this, msg, Tr.S("Удаление папки", "Delete folder"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                { EndBrowserOp(Tr.S("Удаление отменено.", "Deletion cancelled.")); return; }
                if (!DetachNode(folder))
                { EndBrowserOp(Tr.S("Папку удалить не удалось: её уже нет в файле закладок.", "The folder could not be deleted: it is no longer in the bookmarks file.")); return; }
                CommitBookmarks(snap, Tr.S("Папка удалена", "Folder deleted"));
            });
        }

        private void RemoveEmptyFolders()
        {
            BrowseTag t = CurrentWritableTag();
            if (t == null) return;
            if (!CanWriteSync(t.Snap)) return;
            List<BmNode> empty = new List<BmNode>();
            foreach (BmNode r in t.Snap.Roots) CollectEmpty(r, empty);
            if (empty.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Пустых папок нет.", "No empty folders."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!BeginBrowserOp(Tr.S("Подготовка удаления пустых папок", "Preparing the empty folders deletion"), false)) return;

            BrowserSnapshot snap = t.Snap;
            CheckBrowserClosed(snap, delegate
            {
                BrowserStage(Tr.S("Ожидание подтверждения", "Waiting for confirmation"));
                if (MessageBox.Show(this, string.Format(Tr.S("Удалить пустых папок: {0}?", "Delete {0} empty folders?"), empty.Count),
                                    Tr.S("Пустые папки", "Empty folders"),
                                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                { EndBrowserOp(Tr.S("Удаление отменено.", "Deletion cancelled.")); return; }
                int done = 0;
                foreach (BmNode n in empty) if (DetachNode(n)) done++;
                if (done == 0)
                { EndBrowserOp(Tr.S("Удалять нечего: этих папок в файле закладок уже нет.", "Nothing to delete: those folders are no longer in the bookmarks file.")); return; }
                CommitBookmarks(snap, string.Format(Tr.S("Удалено пустых папок: {0}", "Empty folders deleted: {0}"), done));
            });
        }

        // Снизу вверх: папка, в которой остались только пустые папки, тоже пустая.
        private void CollectEmpty(BmNode n, List<BmNode> outList)
        {
            foreach (BmNode c in n.Children)
                if (c.IsFolder) CollectEmpty(c, outList);
            if (n.Parent != null && n.IsFolder && n.TotalUrls == 0) outList.Add(n);
        }

        private void MoveSelectedBookmarks()
        {
            BrowseTag t = CurrentWritableTag();
            if (t == null) return;
            List<BmNode> sel = TopMostNodes(CheckedBookmarks());
            if (sel.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Отметьте галочками, что переносить.", "Tick what to move."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!CanWriteSync(t.Snap)) return;
            if (!BeginBrowserOp(Tr.S("Подготовка переноса", "Preparing the move"), false)) return;

            BrowserSnapshot snap = t.Snap;
            CheckBrowserClosed(snap, delegate
            {
                bool mergeFolders;
                BrowserStage(Tr.S("Выбор папки-приёмника", "Picking the destination folder"));
                BmNode target = PickFolder(snap, sel, out mergeFolders);
                if (target == null) { EndBrowserOp(Tr.S("Перенос отменён.", "Move cancelled.")); return; }

                JVal targetKids = target.Raw.Get("children");
                if (targetKids == null) { targetKids = JVal.NewArr(); target.Raw.Set("children", targetKids); }

                int moved = 0, merged = 0, skipped = 0;
                foreach (BmNode n in sel)
                {
                    if (n == target) { skipped++; continue; }
                    if (IsAncestor(n, target)) { skipped++; continue; }   // папку нельзя перенести внутрь себя

                    if (mergeFolders && n.IsFolder)
                    {
                        JVal kids = n.Raw.Get("children");
                        if (kids != null && kids.Kind == JKind.Arr)
                        {
                            List<BmNode> childCopy = new List<BmNode>(n.Children);
                            foreach (BmNode c in childCopy)
                            {
                                if (!DetachNode(c)) continue;
                                targetKids.V.Add(c.Raw);
                                c.Parent = target;
                                target.Children.Add(c);
                                moved++;
                            }
                        }
                        if (DetachNode(n)) merged++;
                        continue;
                    }

                    if (!DetachNode(n)) { skipped++; continue; }
                    targetKids.V.Add(n.Raw);
                    n.Parent = target;
                    target.Children.Add(n);
                    moved++;
                }
                if (moved == 0 && merged == 0)
                {
                    // Отмеченной была сама папка-приёмник или её предок: переносить в себя нельзя.
                    EndBrowserOp(Tr.S("Переносить нечего: выбрана та же папка или её родитель.",
                                      "Nothing to move: the target folder itself or its parent was ticked."));
                    return;
                }
                CommitBookmarks(snap, string.Format(
                    Tr.S("Перенесено: {0}, папок объединено: {1} → «{2}»", "Moved: {0}, folders merged: {1} → “{2}”"),
                    moved, merged, target.Name)
                    + (skipped > 0 ? string.Format(Tr.S("  ·  пропущено: {0}", "  ·  skipped: {0}"), skipped) : ""));
            });
        }

        private static bool IsAncestor(BmNode maybeAncestor, BmNode node)
        {
            for (BmNode p = node; p != null; p = p.Parent) if (p == maybeAncestor) return true;
            return false;
        }

        // Диалог выбора папки-приёмника. Отдельная форма, а не TreeView в главном окне:
        // выбирать приёмник в том же дереве, где стоят галочки, невозможно.
        private BmNode PickFolder(BrowserSnapshot s, List<BmNode> moving, out bool mergeFolders)
        {
            mergeFolders = false;
            Form dlg = new Form();
            dlg.Text = Tr.S("Куда перенести", "Move to");
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 520; dlg.Height = 560;
            dlg.MinimizeBox = false; dlg.MaximizeBox = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;

            TreeView tv = new TreeView();
            tv.Dock = DockStyle.Fill;
            tv.BorderStyle = BorderStyle.FixedSingle;
            tv.HideSelection = false;
            tv.BackColor = _theme.Surface; tv.ForeColor = _theme.Text;
            SetupOwnerDraw(tv);
            foreach (BmNode r in s.Roots) tv.Nodes.Add(PickerNode(r));
            foreach (TreeNode n in tv.Nodes) n.Expand();   // корни свёрнутыми выглядели как пустые

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 92;

            CheckBox chk = new CheckBox();
            chk.Text = Tr.S("Объединять: переносить содержимое отмеченных папок, а сами папки удалять",
                            "Merge: move the contents of ticked folders and delete the folders themselves");
            chk.Left = 12; chk.Top = 8; chk.Width = 480; chk.Height = 34;
            chk.ForeColor = _theme.Text;

            Button ok = MkButton(Tr.S("Перенести", "Move"), 250, 46, 120, true);
            Button cancel = MkButton(Tr.S("Отмена", "Cancel"), 380, 46, 110, false);
            // «Перенести» без выбранной папки раньше просто закрывало диалог, и перенос
            // пропадал молча — ровно как при «Отмене». Теперь диалог не закрывается,
            // пока папка не выбрана, и говорит, чего от пользователя ждёт.
            Label pick = MkNote(Tr.S("Выберите папку-приёмник в дереве.", "Pick a destination folder in the tree."), true);
            pick.Dock = DockStyle.None;
            pick.Left = 12; pick.Top = 52; pick.Width = 230; pick.Height = 34;
            ok.DialogResult = DialogResult.None;
            ok.Click += delegate
            {
                BmNode sel = tv.SelectedNode != null ? tv.SelectedNode.Tag as BmNode : null;
                if (sel == null)
                {
                    pick.ForeColor = _theme.Accent;
                    pick.Text = Tr.S("Папка не выбрана — отметьте её в дереве выше.", "No folder picked — select one in the tree above.");
                    return;
                }
                dlg.DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(chk);
            bottom.Controls.Add(pick);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Panel mid = new Panel();
            mid.Dock = DockStyle.Fill;
            mid.Padding = new Padding(12, 12, 12, 0);
            mid.Controls.Add(tv);
            dlg.Controls.Add(mid);
            dlg.Controls.Add(bottom);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            tv.BackColor = _theme.Surface; tv.ForeColor = _theme.Text;
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };

            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            BmNode chosen = dr == DialogResult.OK && tv.SelectedNode != null ? tv.SelectedNode.Tag as BmNode : null;
            mergeFolders = chk.Checked;
            dlg.Dispose();
            return chosen;
        }

        private TreeNode PickerNode(BmNode f)
        {
            TreeNode n = new TreeNode(f.Name);
            n.Tag = f;
            foreach (BmNode c in f.Children) if (c.IsFolder) n.Nodes.Add(PickerNode(c));
            return n;
        }

        // Группу вкладок из базы синхронизации удалить нельзя, но можно перенести
        // её содержимое в обычную папку закладок — и дальше чистить уже как закладки.
        private void ExportGroupToBookmarks()
        {
            BrowseTag t = CurrentTag();
            if (t == null || t.Kind != "group" || t.Grp == null)
            {
                MessageBox.Show(this, Tr.S("Выберите в дереве группу вкладок.", "Select a tab group in the tree."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!CanWriteSync(t.Snap)) return;
            if (!BeginBrowserOp(Tr.S("Подготовка сохранения группы", "Preparing the group export"), false)) return;

            BrowserSnapshot snap = t.Snap;
            TabGroupRec grp = t.Grp;
            CheckBrowserClosed(snap, delegate
            {
                BmNode other = null;
                foreach (BmNode r in snap.Roots)
                {
                    if (other == null) other = r;
                    // У всех Chromium-браузеров корень «Другие закладки» имеет фиксированный GUID;
                    // имя зависит от языка интерфейса, поэтому по нему — только запасной вариант.
                    if (string.Equals(r.Guid, "82b081ec-3dd3-529c-8475-ab6c344590dd", StringComparison.OrdinalIgnoreCase)) { other = r; break; }
                    if (r.Name.IndexOf("ругие", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        r.Name.IndexOf("Other", StringComparison.OrdinalIgnoreCase) >= 0) { other = r; break; }
                }
                if (other == null)
                {
                    // Корни закладок разобрать не удалось — раньше команда после всех проверок
                    // просто ничего не делала.
                    EndBrowserOp(Tr.S("В этом профиле нет корневой папки закладок — сохранять некуда.",
                                      "This profile has no root bookmarks folder — there is nowhere to save."));
                    return;
                }

                long nextId = MaxBookmarkId(snap.BookmarksDoc) + 1;
                string now = ((ulong)(DateTime.UtcNow - new DateTime(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / 10).ToString();

                JVal folder = JVal.NewObj();
                folder.Set("children", JVal.NewArr());
                folder.Set("date_added", JVal.NewStr(now));
                folder.Set("date_last_used", JVal.NewStr("0"));
                folder.Set("date_modified", JVal.NewStr(now));
                folder.Set("guid", JVal.NewStr(Guid.NewGuid().ToString()));
                folder.Set("id", JVal.NewStr((nextId++).ToString()));
                folder.Set("name", JVal.NewStr(grp.Title));
                folder.Set("type", JVal.NewStr("folder"));

                JVal kids = folder.Get("children");
                foreach (TabRec tr in grp.Tabs)
                {
                    if (string.IsNullOrEmpty(tr.Url)) continue;
                    JVal b = JVal.NewObj();
                    b.Set("date_added", JVal.NewStr(now));
                    b.Set("date_last_used", JVal.NewStr("0"));
                    b.Set("guid", JVal.NewStr(Guid.NewGuid().ToString()));
                    b.Set("id", JVal.NewStr((nextId++).ToString()));
                    b.Set("name", JVal.NewStr(string.IsNullOrEmpty(tr.Title) ? tr.Url : tr.Title));
                    b.Set("type", JVal.NewStr("url"));
                    b.Set("url", JVal.NewStr(tr.Url));
                    kids.V.Add(b);
                }
                if (kids.V.Count == 0)
                {
                    EndBrowserOp(Tr.S("В этой группе нет вкладок с адресами — сохранять нечего.",
                                      "This group has no tabs with addresses — nothing to save."));
                    return;
                }

                JVal otherKids = other.Raw.Get("children");
                if (otherKids == null) { otherKids = JVal.NewArr(); other.Raw.Set("children", otherKids); }
                otherKids.V.Add(folder);

                CommitBookmarks(snap, string.Format(
                    Tr.S("Группа «{0}» сохранена в «{1}» ({2} ссылок). Саму группу удалите в браузере.",
                         "Group “{0}” saved into “{1}” ({2} links). Delete the group itself in the browser."),
                    grp.Title, other.Name, kids.V.Count));
            });
        }

        private static long MaxBookmarkId(JVal doc)
        {
            long max = 0;
            JVal roots = doc.Get("roots");
            if (roots == null) return 0;
            foreach (JVal r in roots.V) if (r.Kind == JKind.Obj) MaxIdWalk(r, ref max);
            return max;
        }

        private static void MaxIdWalk(JVal n, ref long max)
        {
            long v;
            string id = n.GetStr("id");
            if (id != null && long.TryParse(id, out v) && v > max) max = v;
            JVal kids = n.Get("children");
            if (kids != null && kids.Kind == JKind.Arr)
                foreach (JVal c in kids.V) if (c.Kind == JKind.Obj) MaxIdWalk(c, ref max);
        }

        // Запись закладок и перечитывание профиля идут в фоне. На UI-потоке это было самым
        // тяжёлым местом всей вкладки: MD5 по всему дереву закладок, копия файла, атомарная
        // замена — и следом ПОЛНЫЙ повторный разбор профиля (вся папка Sync Data\LevelDB
        // плюс файлы сеанса). Окно умирало на секунды и не перерисовывалось вообще.
        // Ворота BeginBrowserOp к этому моменту уже взяты вызывающим — отпускаем их здесь.
        private void CommitBookmarks(BrowserSnapshot s, string report)
        {
            BrowserStage(Tr.S("Сохранение закладок", "Saving bookmarks"));
            string op = Tr.S("запись закладок", "writing bookmarks");
            BeginWrite(op);
            Thread t = new Thread(delegate()
            {
                string backup = null, err = null;
                try { backup = BrowserData.SaveBookmarks(s.Profile, s.BookmarksDoc); }
                catch (Exception ex) { err = ex.Message; }
                // Перечитываем профиль целиком: после правки id, пути и счётчики уже другие.
                // Читаем и после неудачной записи — иначе дерево в окне показывало бы правку,
                // которой на диске нет.
                BrowserStage(Tr.S("Перечитывание профиля", "Re-reading the profile"));
                BrowserSnapshot fresh;
                try { fresh = BrowserData.Load(s.Profile); }
                catch { fresh = s; }
                EndWrite(op);
                BrowserSnapshot loaded = fresh;
                string bak = backup, emsg = err;
                UiPost(delegate
                {
                    EndBrowserOp(null);
                    int idx = _snapshots != null ? _snapshots.IndexOf(s) : -1;
                    if (idx >= 0) _snapshots[idx] = loaded;
                    PopulateBrowserTree();
                    _lblBrowserInfo.Text = emsg != null
                        ? Tr.S("Записать закладки не удалось: ", "Failed to write bookmarks: ") + emsg
                          + Tr.S("  ·  файл закладок не изменён", "  ·  the bookmarks file is unchanged")
                        : report + Tr.S("  ·  копия: ", "  ·  backup: ") + Path.GetFileName(bak);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- проверка ссылок ----------

        private void CheckLinks()
        {
            if (!BeginBrowserOp(Tr.S("Подготовка проверки ссылок", "Preparing the link check"), true)) return;

            List<object> targets = new List<object>();
            List<string> urls = new List<string>();
            // Без Begin/EndUpdate список с owner-draw перерисовывался целиком на КАЖДУЮ
            // строку (дважды: «не http» и «…»), и на тысяче закладок клик по кнопке
            // выглядел как зависание ещё до первого запроса.
            _lvBrowser.BeginUpdate();
            try
            {
                foreach (ListViewItem it in _lvBrowser.Items)
                {
                    string u = null;
                    BmNode b = it.Tag as BmNode;
                    if (b != null && !b.IsFolder) u = b.Url;
                    TabRec t = it.Tag as TabRec;
                    if (t != null) u = t.Url;
                    ReadingRec r = it.Tag as ReadingRec;
                    if (r != null) u = r.Url;
                    OpenTabRec o = it.Tag as OpenTabRec;
                    if (o != null) u = o.Url;
                    if (string.IsNullOrEmpty(u)) continue;
                    if (!u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                        !u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        it.SubItems[5].Text = Tr.S("не http", "not http");
                        continue;
                    }
                    it.SubItems[5].Text = "…";
                    targets.Add(it);
                    urls.Add(u);
                }
            }
            finally { _lvBrowser.EndUpdate(); }

            if (urls.Count == 0)
            {
                EndBrowserOp(Tr.S("Проверять нечего: в списке нет http-ссылок.", "Nothing to check: the list has no http links."));
                MessageBox.Show(this, Tr.S("В списке нет http-ссылок для проверки.", "No http links in the list."),
                                Tr.S("Браузеры", "Browsers"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            BrowserStage(string.Format(Tr.S("Проверка ссылок: 0 из {0}", "Checking links: 0 of {0}"), urls.Count));

            Thread t2 = new Thread(delegate()
            {
                try { ServicePointManager.DefaultConnectionLimit = 32; } catch { }
                // TLS 1.3 (12288) есть в .NET 4.8 на Windows 11; где нет — остаёмся на TLS 1.2.
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
                catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }

                int next = -1, done = 0;
                const int workers = 8;
                Thread[] pool = new Thread[workers];
                for (int w = 0; w < workers; w++)
                {
                    pool[w] = new Thread(delegate()
                    {
                        while (!_browserCancel)
                        {
                            int i = Interlocked.Increment(ref next);
                            if (i >= urls.Count) break;
                            string state = ProbeUrl(urls[i]);
                            if (_browserCancel) break;    // оборванный запрос — не результат, писать его нельзя
                            ListViewItem row = (ListViewItem)targets[i];
                            int d = Interlocked.Increment(ref done);
                            // Счётчик обновляем на КАЖДОЙ готовой ссылке. Раньше он двигался
                            // раз в десять штук, а один адрес стоит до 20 с — на коротком
                            // списке цифра не менялась ни разу за всю проверку.
                            BrowserStage(string.Format(Tr.S("Проверка ссылок: {0} из {1}, осталось {2}",
                                                            "Checking links: {0} of {1}, {2} left"), d, urls.Count, urls.Count - d));
                            UiPost(delegate
                            {
                                try
                                {
                                    row.SubItems[5].Text = state;
                                    BmNode bn = row.Tag as BmNode; if (bn != null) bn.LinkState = state;
                                    TabRec tr = row.Tag as TabRec; if (tr != null) tr.LinkState = state;
                                    ReadingRec rr = row.Tag as ReadingRec; if (rr != null) rr.LinkState = state;
                                    OpenTabRec or = row.Tag as OpenTabRec; if (or != null) or.LinkState = state;
                                }
                                catch { }
                            });
                        }
                    });
                    pool[w].IsBackground = true;
                    pool[w].Start();
                }
                foreach (Thread p in pool) p.Join();
                AbortLinkRequests();                       // на случай, если что-то осталось в списке
                int checkedCount = done;
                UiPost(delegate
                {
                    bool stopped = _browserCancel;
                    if (stopped)
                    {
                        // Непроверенные строки так и остались бы с многоточием, будто проверка идёт.
                        _lvBrowser.BeginUpdate();
                        try
                        {
                            foreach (ListViewItem it in _lvBrowser.Items)
                                if (it.SubItems.Count > 5 && it.SubItems[5].Text == "…")
                                    it.SubItems[5].Text = Tr.S("не проверено", "not checked");
                        }
                        finally { _lvBrowser.EndUpdate(); }
                    }
                    EndBrowserOp(stopped
                        ? string.Format(Tr.S("Проверка прервана: проверено {0} из {1}, остальные помечены «не проверено».",
                                             "Check stopped: {0} of {1} checked, the rest are marked “not checked”."), checkedCount, urls.Count)
                        : string.Format(Tr.S("Проверено ссылок: {0}. Отметьте нерабочие и удалите.",
                                             "Links checked: {0}. Tick the dead ones and delete."), urls.Count));
                });
            });
            t2.IsBackground = true;
            t2.Start();
        }

        // HEAD поддерживают не все — на 405/501 повторяем обычным GET.
        private string ProbeUrl(string url)
        {
            string byHead = Probe(url, "HEAD");
            if (_browserCancel) return byHead;
            if (byHead == "405" || byHead == "501" || byHead == "403") return Probe(url, "GET");
            return byHead;
        }

        // Не static: запрос заносится в общий список, чтобы «Стоп» мог его оборвать.
        private string Probe(string url, string method)
        {
            HttpWebResponse resp = null;
            HttpWebRequest req = null;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = method;
                req.Timeout = 10000;
                req.ReadWriteTimeout = 10000;
                req.AllowAutoRedirect = true;
                req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WindowsProcessCleaner";
                req.Accept = "*/*";
                lock (_linkRequests) _linkRequests.Add(req);
                if (_browserCancel) { try { req.Abort(); } catch { } }
                resp = (HttpWebResponse)req.GetResponse();
                int code = (int)resp.StatusCode;
                // Успех — весь диапазон 2xx, а не только 200: на HEAD YouTube отвечает
                // 204, и живая ссылка выглядела в списке как код ошибки.
                return code >= 200 && code < 300 ? "OK" : code.ToString();
            }
            catch (WebException we)
            {
                HttpWebResponse r = we.Response as HttpWebResponse;
                if (r != null)
                {
                    int code = (int)r.StatusCode;
                    try { r.Close(); } catch { }
                    return code.ToString();
                }
                if (we.Status == WebExceptionStatus.RequestCanceled) return Tr.S("не проверено", "not checked");
                if (we.Status == WebExceptionStatus.Timeout) return Tr.S("таймаут", "timeout");
                if (we.Status == WebExceptionStatus.NameResolutionFailure) return Tr.S("нет домена", "no DNS");
                if (we.Status == WebExceptionStatus.TrustFailure) return Tr.S("сертификат", "TLS");
                return Tr.S("нет связи", "no reply");
            }
            catch { return Tr.S("ошибка", "error"); }
            finally
            {
                if (req != null) lock (_linkRequests) _linkRequests.Remove(req);
                if (resp != null) try { resp.Close(); } catch { }
            }
        }
    }
}
