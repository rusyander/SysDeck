// SysDeck — вкладка «Браузеры»: дерево профилей, закладки, группы, сеансы, проверка ссылок
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

namespace SysDeck
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
    }
}
