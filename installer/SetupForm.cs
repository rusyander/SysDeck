using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WpcSetup
{
    // Мастер установки: страница параметров → страница работы → страница «готово».
    // Страницы не отдельные окна, а панели в одном теле — так не мигает и не сбивается
    // положение окна между шагами.
    internal sealed class SetupForm : Form, IWorkHost
    {
        private readonly SetupArgs _args;

        private Label _title;
        private Label _subtitle;
        private Panel _body;
        private Button _primary;
        private Button _cancel;

        private Panel _pageOptions;
        private TextBox _path;
        private CheckBox _allUsers;
        private CheckBox _desktop;

        private Panel _pageWork;
        private Label _status;
        private ProgressBar _bar;
        private TextBox _log;

        private Panel _pageDone;
        private Label _doneWhere;
        private CheckBox _launch;

        private string _installedExe;
        private bool _busy;

        public int ExitCode = 1;

        public SetupForm(SetupArgs args)
        {
            _args = args;

            Text = L.S("Установка Windows Process Cleaner", "Windows Process Cleaner Setup");
            Font = Ui.Base;
            BackColor = Color.White;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Ui.Px(600), Ui.Px(440));
            Ui.SetIcon(this);

            BuildUi();
            ShowOptions();
        }

        // ---------- Построение ----------

        private void BuildUi()
        {
            int pad = Ui.Px(22);
            int bodyW = ClientSize.Width - pad * 2;
            int bodyH = ClientSize.Height - Ui.Px(70) - 1 - Ui.Px(58) - pad * 2;

            _body = new Panel();
            _body.Dock = DockStyle.Fill;
            _body.Padding = new Padding(pad);
            _body.BackColor = Color.White;

            Panel sep = new Panel();
            sep.Dock = DockStyle.Top;
            sep.Height = 1;
            sep.BackColor = Ui.Line;

            Panel head = new Panel();
            head.Dock = DockStyle.Top;
            head.Height = Ui.Px(70);
            head.BackColor = Color.White;

            _title = Ui.Text(head, "", Ui.Px(22), Ui.Px(14), bodyW, false);
            _title.Font = Ui.Head;
            _title.Size = new Size(bodyW, Ui.Px(26));
            _subtitle = Ui.Text(head, "", Ui.Px(23), Ui.Px(44), bodyW, true);

            Panel foot = new Panel();
            foot.Dock = DockStyle.Bottom;
            foot.Height = Ui.Px(58);
            foot.BackColor = Ui.Footer;

            Panel footLine = new Panel();
            footLine.Dock = DockStyle.Top;
            footLine.Height = 1;
            footLine.BackColor = Ui.Line;
            foot.Controls.Add(footLine);

            int bw = Ui.Px(118);
            _cancel = Ui.MakeButton(foot, L.S("Отмена", "Cancel"), bw);
            _cancel.Location = new Point(ClientSize.Width - Ui.Px(20) - bw, Ui.Px(14));
            _cancel.Click += delegate { CancelPressed(); };

            _primary = Ui.MakeButton(foot, L.S("Установить", "Install"), bw);
            _primary.Location = new Point(_cancel.Left - Ui.Px(10) - bw, Ui.Px(14));
            _primary.Click += delegate { PrimaryPressed(); };

            BuildOptions(bodyW, bodyH);
            BuildWork(bodyW, bodyH);
            BuildDone(bodyW, bodyH);

            // Порядок добавления обратный порядку раскладки: Fill первым, нижняя полоса последней.
            Controls.Add(_body);
            Controls.Add(sep);
            Controls.Add(head);
            Controls.Add(foot);

            AcceptButton = _primary;
        }

        private Panel NewPage()
        {
            Panel p = new Panel();
            p.Dock = DockStyle.Fill;
            p.BackColor = Color.White;
            p.Visible = false;
            _body.Controls.Add(p);
            return p;
        }

        private void BuildOptions(int w, int h)
        {
            _pageOptions = NewPage();

            Ui.Text(_pageOptions, L.S("Программа будет установлена в папку:", "The program will be installed to:"),
                    0, 0, w, false);

            int browseW = Ui.Px(96);
            _path = new TextBox();
            _path.Font = Ui.Base;
            _path.Location = new Point(0, Ui.Px(24));
            _path.Size = new Size(w - browseW - Ui.Px(8), Ui.Px(24));
            _pageOptions.Controls.Add(_path);

            Button browse = Ui.MakeButton(_pageOptions, L.S("Обзор…", "Browse..."), browseW);
            browse.Size = new Size(browseW, Ui.Px(26));
            browse.Location = new Point(w - browseW, Ui.Px(23));
            browse.Click += delegate { Browse(); };

            _allUsers = Ui.Check(_pageOptions,
                L.S("Для всех пользователей (нужны права администратора)",
                    "For all users (administrator rights required)"), 0, Ui.Px(64), w);
            _allUsers.CheckedChanged += delegate { ScopeChanged(); };

            Ui.Note(_pageOptions,
                L.S("По умолчанию программа ставится только для вас, в профиль пользователя, и запроса прав администратора при установке не будет. С флажком она попадёт в Program Files и станет доступна всем — Windows один раз спросит разрешение.",
                    "By default the program is installed for you only, inside your user profile, with no administrator prompt at all. With the box ticked it goes to Program Files and becomes available to everyone - Windows will ask for permission once."),
                Ui.Px(22), Ui.Px(88), w - Ui.Px(22), 3);

            _desktop = Ui.Check(_pageOptions, L.S("Создать ярлык на рабочем столе", "Create a desktop shortcut"),
                                0, Ui.Px(150), w);
            _desktop.Checked = true;

            Ui.Note(_pageOptions,
                L.S("Кроме программы будут созданы ярлык в меню «Пуск», запись в списке установленных программ и uninstall.exe рядом с ней. Настройки и история лежат отдельно, в %APPDATA%\\WindowsProcessCleaner, и переустановку переживают.",
                    "Along with the program you get a Start menu shortcut, an entry in the installed programs list and uninstall.exe next to it. Settings and history live separately, in %APPDATA%\\WindowsProcessCleaner, and survive a reinstall."),
                0, Ui.Px(186), w, 3);

            _path.Text = Product.DefaultDir(InstallScope.PerUser);
        }

        private void BuildWork(int w, int h)
        {
            _pageWork = NewPage();
            _status = Ui.Text(_pageWork, "", 0, 0, w, false);

            _bar = new ProgressBar();
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;
            _bar.Location = new Point(0, Ui.Px(26));
            _bar.Size = new Size(w, Ui.Px(12));
            _pageWork.Controls.Add(_bar);

            _log = Ui.Log(_pageWork, 0, Ui.Px(50), w, h - Ui.Px(50));
        }

        private void BuildDone(int w, int h)
        {
            _pageDone = NewPage();
            Ui.Note(_pageDone,
                L.S("Windows Process Cleaner установлен. Ярлык добавлен в меню «Пуск», удалить программу можно там же, где остальные — «Приложения» в параметрах Windows.",
                    "Windows Process Cleaner is installed. A Start menu shortcut was created; the program can be removed from the usual place - Apps in Windows Settings."),
                0, 0, w, 3);

            _doneWhere = Ui.Text(_pageDone, "", 0, Ui.Px(64), w, true);

            _launch = Ui.Check(_pageDone, L.S("Запустить Windows Process Cleaner", "Launch Windows Process Cleaner"),
                               0, Ui.Px(100), w);
            _launch.Checked = true;

            Ui.Note(_pageDone,
                L.S("Программе нужны права администратора — при запуске Windows один раз спросит разрешение.",
                    "The program needs administrator rights - Windows will ask for permission when it starts."),
                Ui.Px(22), Ui.Px(126), w - Ui.Px(22), 2);
        }

        // ---------- Страницы ----------

        private void ShowOptions()
        {
            _title.Text = L.S("Установка Windows Process Cleaner", "Install Windows Process Cleaner");
            _subtitle.Text = L.S("Обслуживание Windows в одном окне", "Windows maintenance in a single window");
            _pageWork.Visible = false;
            _pageDone.Visible = false;
            _pageOptions.Visible = true;
            _primary.Text = L.S("Установить", "Install");
            _primary.Enabled = true;
            _cancel.Visible = true;
            _cancel.Enabled = true;
        }

        private void ShowWork()
        {
            _title.Text = L.S("Установка", "Installing");
            _subtitle.Text = L.S("Это займёт несколько секунд", "This takes a few seconds");
            _pageOptions.Visible = false;
            _pageDone.Visible = false;
            _pageWork.Visible = true;
            _primary.Enabled = false;
            _cancel.Enabled = false;
        }

        private void ShowDone(string dir)
        {
            _title.Text = L.S("Установка завершена", "Installation complete");
            _subtitle.Text = "";
            _pageWork.Visible = false;
            _pageOptions.Visible = false;
            _pageDone.Visible = true;
            _doneWhere.Text = dir;
            _primary.Text = L.S("Готово", "Finish");
            _primary.Enabled = true;
            _cancel.Visible = false;
        }

        // ---------- Действия ----------

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);

            // Продолжение после запроса прав: параметры уже выбраны в прошлом окне,
            // спрашивать их второй раз незачем.
            if (_args.Auto)
            {
                _allUsers.Checked = _args.Scope == InstallScope.Machine;
                _desktop.Checked = _args.Desktop;
                if (!string.IsNullOrEmpty(_args.Dir)) _path.Text = _args.Dir;
                BeginInvoke((MethodInvoker)delegate { PrimaryPressed(); });
            }
        }

        private void Browse()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = L.S("Выберите папку для установки", "Choose the installation folder");
                d.ShowNewFolderButton = true;
                try { d.SelectedPath = Path.GetDirectoryName(Product.Norm(_path.Text)); }
                catch { }
                if (d.ShowDialog(this) != DialogResult.OK) return;

                // К выбранной папке добавляется имя программы: деинсталлятор сносит папку
                // установки целиком, и «поставить прямо в D:\Загрузки» кончилось бы плохо.
                string chosen = Product.Norm(d.SelectedPath);
                string leaf = "";
                try { leaf = Path.GetFileName(chosen); }
                catch { }
                _path.Text = string.Equals(leaf, Product.FolderName, StringComparison.OrdinalIgnoreCase)
                    ? chosen : Path.Combine(chosen, Product.FolderName);
            }
        }

        private void ScopeChanged()
        {
            // Свой путь пользователя не трогаем — заменяем только умолчание другой области.
            string now = Product.Norm(_path.Text);
            string other = Product.Norm(Product.DefaultDir(_allUsers.Checked ? InstallScope.PerUser : InstallScope.Machine));
            if (now.Length == 0 || now.Equals(other, StringComparison.OrdinalIgnoreCase))
                _path.Text = Product.DefaultDir(_allUsers.Checked ? InstallScope.Machine : InstallScope.PerUser);
        }

        private void PrimaryPressed()
        {
            if (_pageDone.Visible)
            {
                LaunchIfAsked();
                ExitCode = 0;
                Close();
                return;
            }
            if (_busy) return;
            StartInstall();
        }

        private void CancelPressed()
        {
            if (_busy) return;
            ExitCode = 1;
            Close();
        }

        private void StartInstall()
        {
            string dir = Product.Norm(_path.Text);
            string error;
            if (!ValidDir(dir, out error))
            {
                MessageBox.Show(this, error, L.S("Папка установки", "Install folder"),
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            InstallScope scope = _allUsers.Checked ? InstallScope.Machine : InstallScope.PerUser;
            bool desktop = _desktop.Checked;

            // Права нужны ровно один раз и только для установки всем: перезапускаем себя
            // с verb runas и передаём уже сделанный выбор.
            if (scope == InstallScope.Machine && !Product.IsElevated())
            {
                if (!InstallFlow.RelaunchElevated(dir, desktop))
                {
                    MessageBox.Show(this,
                        L.S("Без прав администратора установить для всех пользователей нельзя. Снимите флажок — и программа встанет только для вас, без запроса прав.",
                            "Installing for all users is not possible without administrator rights. Untick the box to install for you only, with no prompt."),
                        L.S("Нужны права администратора", "Administrator rights required"),
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ExitCode = 0;
                Close();
                return;
            }

            _busy = true;
            _log.Clear();
            ShowWork();

            // Ярлыки создаются через COM оболочки — потоку нужен STA.
            Thread t = new Thread(delegate() { Worker(dir, scope, desktop); });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static bool ValidDir(string dir, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(dir))
                {
                    error = L.S("Укажите папку для установки.", "Specify the installation folder.");
                    return false;
                }
                string full = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(full);
                if (string.IsNullOrEmpty(root))
                {
                    error = L.S("Нужен полный путь, например C:\\Program Files\\WindowsProcessCleaner.",
                                "A full path is required, for example C:\\Program Files\\WindowsProcessCleaner.");
                    return false;
                }
                if (full.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    error = L.S("В корень диска ставить нельзя: при удалении эта папка стирается целиком.",
                                "The drive root cannot be used: uninstalling wipes the install folder entirely.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private void Worker(string dir, InstallScope scope, bool desktop)
        {
            WorkContext c = new WorkContext();
            c.Dir = dir;
            c.Scope = scope;
            c.DesktopShortcut = desktop;
            c.Host = this;

            string error = null;
            bool cancelled = false;
            try { Work.Install(c); }
            catch (SetupCancelled) { cancelled = true; }
            catch (Exception ex) { error = ex.Message; }

            string err = error;
            bool can = cancelled;
            try { BeginInvoke((MethodInvoker)delegate { Finish(dir, err, can); }); }
            catch { }
        }

        private void Finish(string dir, string error, bool cancelled)
        {
            _busy = false;
            _bar.MarqueeAnimationSpeed = 0;

            if (cancelled)
            {
                ShowOptions();
                return;
            }
            if (error != null)
            {
                MessageBox.Show(this, error, L.S("Установка не завершена", "Installation failed"),
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowOptions();
                return;
            }

            _installedExe = Path.Combine(dir, Product.ExeName);
            ShowDone(dir);
        }

        private void LaunchIfAsked()
        {
            if (!_launch.Checked || _installedExe == null || !File.Exists(_installedExe)) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(_installedExe);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(_installedExe);
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // На середине копирования закрывать нечего: остановиться уже некуда.
            if (_busy && e.CloseReason == CloseReason.UserClosing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        // ---------- IWorkHost ----------

        public void Log(string line)
        {
            if (!IsHandleCreated) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _log.AppendText(line + "\r\n");
                    if (line.Length > 0 && line[0] != ' ') _status.Text = line;
                });
            }
            catch { }
        }

        public DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (InvokeRequired)
                return (DialogResult)Invoke((Func<DialogResult>)delegate { return Ask(text, caption, buttons, icon); });
            return MessageBox.Show(this, text, caption, buttons, icon);
        }
    }
}
