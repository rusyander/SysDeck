// Windows Process Cleaner — «Загрузки» отдельным окном: та же страница, вынутая из вкладки в собственное окно.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Страница не копируется, а переезжает: список, карточка и настройки — живые элементы с одним состоянием, второй их
// набор пришлось бы вести отдельно и он расходился бы с первым. Поэтому окно одно, и пока оно открыто, во вкладке
// стоит заглушка с кнопкой «Вернуть во вкладку». Окно — верхнего уровня и не принадлежит главному: его можно увести
// на другой монитор, свернуть отдельно, и оно остаётся, когда главное окно свёрнуто в трей.
// Положение и размер запоминаются (MemSet/MemGet), как у остальных окон программы.
using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private Panel _dlTab;              // вся страница загрузок: список, карточка и настройки
        private Control _dlTabHost;        // куда вернуть страницу
        private Form _dlWindow;
        private Panel _dlTabStub;          // заглушка на месте уехавшей страницы
        private Button _btnDlDetach;

        private const string DlWindowScope = "downloads-window";

        public bool DownloadsDetached { get { return _dlWindow != null && !_dlWindow.IsDisposed; } }

        // Кнопка с главной страницы и из вкладки: открыть загрузки отдельным окном (уже открыто — просто показать).
        public void OpenDownloadsWindow()
        {
            if (DownloadsDetached)
            {
                try
                {
                    if (_dlWindow.WindowState == FormWindowState.Minimized) _dlWindow.WindowState = FormWindowState.Normal;
                    _dlWindow.Show();
                    _dlWindow.Activate();
                }
                catch { }
                return;
            }
            DlDetach();
        }

        private void DlDetach()
        {
            if (_dlTab == null || DownloadsDetached) return;
            if (_dlTabHost == null) _dlTabHost = _dlTab.Parent;
            if (_dlTabHost == null) return;

            Form f = new Form();
            f.Text = Tr.S("Загрузки — Windows Process Cleaner", "Downloads — Windows Process Cleaner");
            f.StartPosition = FormStartPosition.Manual;
            f.Bounds = DlWindowBounds();
            f.BackColor = _theme.Surface;
            f.ForeColor = _theme.Text;
            f.Font = Font;
            try { f.Icon = Icon; }
            catch { }
            f.MinimumSize = new Size(Px(720), Px(420));

            // Страница — элемент массива _pages, а ShowPage раздаёт видимость по этому массиву: не подменив её заглушкой,
            // переход на другую вкладку гасил бы содержимое отдельного окна. По той же причине уехавшую делаем видимой.
            bool wasVisible = _dlTab.Visible;
            _dlTabHost.Controls.Remove(_dlTab);
            _dlTab.Dock = DockStyle.Fill;
            _dlTab.Visible = true;
            f.Controls.Add(_dlTab);
            _dlTabStub = DlBuildStub();
            _dlTabStub.Visible = wasVisible;
            _dlTabHost.Controls.Add(_dlTabStub);
            if (_pages != null && PageDownloads < _pages.Length) _pages[PageDownloads] = _dlTabStub;

            f.FormClosing += delegate { DlSaveWindowBounds(f); };
            f.FormClosed += delegate { DlAttach(); };
            f.Move += delegate { if (f.WindowState == FormWindowState.Normal) DlSaveWindowBounds(f); };
            f.Resize += delegate { if (f.WindowState == FormWindowState.Normal) DlSaveWindowBounds(f); };
            f.HandleCreated += delegate { ApplyTitleBar(f); };
            _dlWindow = f;
            DlDetachButtonText();
            DownloadsEnter();          // окно видно независимо от вкладки — список опрашивается с этой минуты
            f.Show();
            ApplyTitleBar(f);
            f.Activate();
        }

        // Возврат страницы во вкладку: вызывается и при закрытии окна, и кнопкой из заглушки, и при закрытии программы.
        private void DlAttach()
        {
            Form f = _dlWindow;
            _dlWindow = null;
            if (_dlTab == null || _dlTabHost == null) return;
            if (f != null && !f.IsDisposed && f.Controls.Contains(_dlTab)) f.Controls.Remove(_dlTab);
            if (_dlTabStub != null)
            {
                _dlTabHost.Controls.Remove(_dlTabStub);
                _dlTabStub.Dispose();
                _dlTabStub = null;
            }
            if (!_dlTabHost.Controls.Contains(_dlTab))
            {
                _dlTab.Dock = DockStyle.Fill;
                _dlTabHost.Controls.Add(_dlTab);
            }
            if (_pages != null && PageDownloads < _pages.Length) _pages[PageDownloads] = _dlTab;
            _dlTab.Visible = _currentPage == PageDownloads;
            if (_currentPage != PageDownloads) DownloadsLeave();   // страница вернулась на невидимую вкладку — опрос не нужен
            DlDetachButtonText();
            if (f != null && !f.IsDisposed) { try { f.Close(); } catch { } }
        }

        private Panel DlBuildStub()
        {
            Panel stub = new Panel();
            stub.Dock = DockStyle.Fill;
            stub.Padding = new Padding(Px(14), Px(12), Px(14), Px(12));
            Label text = new Label();
            text.AutoSize = false;
            text.Dock = DockStyle.Top;
            text.Height = Px(46);
            text.ForeColor = _theme.Subtle;
            text.Text = Tr.S("Загрузки открыты в отдельном окне. Его можно увести на другой монитор; список и настройки там те же самые.",
                             "Downloads are open in a separate window. It can be moved to another monitor; the list and the settings there are the same ones.");
            FlowLayoutPanel bar = MkToolbar();
            Button show = MkFlowButton(Tr.S("Показать окно", "Show the window"), 170, true);
            show.Click += delegate { OpenDownloadsWindow(); };
            Button back = MkFlowButton(Tr.S("Вернуть во вкладку", "Bring back to the tab"), 190, false);
            back.Click += delegate { DlAttach(); };
            bar.Controls.Add(show);
            bar.Controls.Add(back);
            stub.Controls.Add(text);
            stub.Controls.Add(bar);
            return stub;
        }

        private void DlDetachButtonText()
        {
            if (_btnDlDetach == null) return;
            _btnDlDetach.Text = DownloadsDetached ? Tr.S("Вернуть во вкладку", "Back to the tab") : Tr.S("Отдельным окном", "Separate window");
        }

        // Границы окна: сохранённые, если они попадают на существующий экран, иначе по центру главного окна.
        private Rectangle DlWindowBounds()
        {
            string saved = MemGet(DlWindowScope, "bounds", true);
            if (!string.IsNullOrEmpty(saved))
            {
                string[] parts = saved.Split(',');
                int x, y, w, h;
                if (parts.Length == 4
                    && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                    && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y)
                    && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w)
                    && int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h)
                    && w >= 400 && h >= 300)
                {
                    Rectangle r = new Rectangle(x, y, w, h);
                    foreach (Screen sc in Screen.AllScreens) if (sc.WorkingArea.IntersectsWith(r)) return r;
                }
            }
            Rectangle at = Screen.FromControl(this).WorkingArea;
            int width = Math.Min(Px(1180), at.Width - Px(80));
            int height = Math.Min(Px(760), at.Height - Px(80));
            return new Rectangle(at.X + (at.Width - width) / 2, at.Y + (at.Height - height) / 2, width, height);
        }

        private void DlSaveWindowBounds(Form f)
        {
            if (f == null || f.IsDisposed || f.WindowState != FormWindowState.Normal) return;
            Rectangle r = f.Bounds;
            MemSet(DlWindowScope, "bounds", r.X.ToString(CultureInfo.InvariantCulture) + "," + r.Y.ToString(CultureInfo.InvariantCulture)
                                            + "," + r.Width.ToString(CultureInfo.InvariantCulture) + "," + r.Height.ToString(CultureInfo.InvariantCulture), true);
        }
    }
}
