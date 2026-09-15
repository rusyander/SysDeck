// SysDeck — вкладка «Размеры папок»: управление фоновым режимом (трей, панель у Проводника,
// числа в колонке «Размер»), его настройки, автозапуск и быстрый режим NTFS.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SysDeck.FolderSize;

namespace SysDeck
{
    public partial class MainForm
    {
        // Страница ничего не считает сама: фоновый режим — отдельный процесс со своим значком в трее, он живёт
        // и без окна. Здесь только его настройки (тот же settings.json) и сигналы ему через именованные события.
        private Panel _fsScroll;
        private FlowLayoutPanel _fsBody;
        private Label _lblFsStatus, _lblFsLegacy, _lblFsFastNote, _lblFsInfo;
        private Button _btnFsStart, _btnFsStop, _btnFsShow, _btnFsFast, _btnFsLegacy;
        private CheckBox _chkFsOverlay, _chkFsOverlayFiles, _chkFsPanel, _chkFsUnfocused, _chkFsBadge, _chkFsHidden,
                         _chkFsMakeRoom, _chkFsNtfs, _chkFsFreeSpace, _chkFsHotkey, _chkFsMenuExtras;
        private RoundComboBox _cmbFsSide, _cmbFsSort, _cmbFsTheme;
        private RadioButton _radFsAutoOff, _radFsAutoRun, _radFsAutoTask;
        private System.Windows.Forms.Timer _fsTick;
        private bool _fsLoading;        // контролы выставляет FsLoadToUi — это не изменения пользователя
        private int _fsBusy;            // идёт действие с процессом или задачей Планировщика
        private int _fsTaskProbe;       // идёт опрос schtasks
        private bool _fsHasTask, _fsHasRun;

        private Control BuildFolderSizeTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(18, 14, 18, 14);

            // Столбик сам раскладывает строки по высоте: подписи переносятся по ширине окна и сдвигают
            // всё, что ниже, — на абсолютных координатах двухстрочная подпись наезжала бы на кнопки.
            // Прокручивает обёртка, а столбик лишь растёт по содержимому и НЕ пристыкован: область прокрутки
            // пристыкованного столбика считалась по его «предпочтительному» размеру, на ~200 px больше
            // настоящего, — под последней кнопкой прокручивалась пустота. Непристыкованный меряется по границам.
            _fsScroll = new Panel();
            _fsScroll.Dock = DockStyle.Fill;
            _fsScroll.AutoScroll = true;
            tab.Controls.Add(_fsScroll);
            _fsBody = new FlowLayoutPanel();
            _fsBody.Location = new Point(0, 0);
            _fsBody.AutoSize = true;
            _fsBody.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _fsBody.FlowDirection = FlowDirection.TopDown;
            _fsBody.WrapContents = false;
            _fsScroll.Controls.Add(_fsBody);

            FsSection(Tr.S("Фоновый режим", "Background mode"));
            FsNote(Tr.S("Размеры папок прямо в колонке «Размер» Проводника и боковая панель рядом с его окном. "
                        + "Работает отдельным процессом со своим значком в трее — и тогда, когда это окно закрыто.",
                        "Folder sizes right in Explorer's Size column and a side panel next to its window. "
                        + "Runs as a separate process with its own tray icon — even while this window is closed."), true);
            _lblFsStatus = FsNote("", false);
            _lblFsStatus.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);

            FlowLayoutPanel bar = FsRow();
            _btnFsStart = MkFlowButton(Tr.S("Запустить", "Start"), 130, true);
            _btnFsStart.Click += delegate { FsStart(); };
            _btnFsStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnFsStop.Click += delegate { FsStop(); };
            _btnFsShow = MkFlowButton(Tr.S("Показать панель", "Show the panel"), 150, false);
            _btnFsShow.Click += delegate
            {
                if (!FsIpc.Signal(FsIpc.ShowName)) FsInfo(Tr.S("Фоновый режим не запущен.", "Background mode is not running."));
            };
            _btnFsFast = MkFlowButton(Tr.S("Включить быстрый режим", "Enable fast mode"), 200, false);
            _btnFsFast.Click += delegate { FsEnableFast(); };
            bar.Controls.AddRange(new Control[] { _btnFsStart, _btnFsStop, _btnFsShow, _btnFsFast });
            _lblFsFastNote = FsNote(Tr.S("Быстрый режим читает таблицу файлов NTFS напрямую — диск целиком считается за секунды. "
                                         + "Для этого нужны права администратора: одно окно UAC создаёт задачу Планировщика, "
                                         + "которая запускает фоновый режим с правами при входе в Windows и больше UAC не спрашивает.",
                                         "Fast mode reads the NTFS file table directly — a whole disk is counted in seconds. "
                                         + "It needs administrator rights: one UAC prompt creates a Task Scheduler task "
                                         + "that starts background mode elevated at sign-in and never asks for UAC again."), true);

            _lblFsLegacy = FsNote("", false);
            _lblFsLegacy.Name = "warn";
            _lblFsLegacy.Visible = false;
            FlowLayoutPanel legacyBar = FsRow();
            _btnFsLegacy = MkFlowButton(Tr.S("Остановить FolderSizePanel и убрать из автозапуска", "Stop FolderSizePanel and remove it from startup"), 200, false);
            _btnFsLegacy.Click += delegate { FsStopLegacy(); };
            _btnFsLegacy.Visible = false;
            legacyBar.Controls.Add(_btnFsLegacy);

            FsSection(Tr.S("Что показывать", "What to show"));
            _chkFsOverlay = FsCheck(Tr.S("Размеры прямо в Проводнике (колонка «Размер»)", "Sizes right in Explorer (the Size column)"));
            _chkFsOverlayFiles = FsCheck(Tr.S("Перекрывать и размеры файлов", "Cover file sizes too"));
            _chkFsPanel = FsCheck(Tr.S("Боковая панель рядом с Проводником", "Side panel next to Explorer"));
            _chkFsUnfocused = FsCheck(Tr.S("Показывать, пока Проводник открыт, а не только в фокусе", "Show while Explorer is open, not only focused"));
            _chkFsBadge = FsCheck(Tr.S("Индикатор подсчёта в углу окна Проводника", "Counting indicator in the corner of Explorer's window"));
            _chkFsHidden = FsCheck(Tr.S("Учитывать скрытые и системные файлы", "Count hidden and system files"));
            _chkFsMakeRoom = FsCheck(Tr.S("Не перекрывать Проводник — сдвигать его окно", "Don't cover Explorer — move its window"));
            _chkFsNtfs = FsCheck(Tr.S("Быстрый режим (чтение NTFS), когда фоновый режим запущен с правами",
                                      "Fast mode (NTFS read) when background mode runs elevated"));
            _chkFsFreeSpace = FsCheck(Tr.S("Свободное место диска внизу панели", "Free disk space at the bottom of the panel"));
            _chkFsHotkey = FsCheck(Tr.S("Показывать и прятать панель по " + PanelHotkey.Caption,
                                        "Show and hide the panel with " + PanelHotkey.Caption));
            _chkFsMenuExtras = FsCheck(Tr.S("В меню строки панели: «Открыть в карте диска» и «Копировать список»",
                                            "In the panel row menu: “Open in the disk map” and “Copy the list”"));

            FlowLayoutPanel combos = FsRow();
            _cmbFsSide = FsCombo(combos, Tr.S("Панель:", "Panel:"), 190,
                                 Tr.S("справа от Проводника", "right of Explorer"), Tr.S("слева от Проводника", "left of Explorer"));
            _cmbFsSort = FsCombo(combos, Tr.S("Сортировка:", "Sort:"), 190,
                                 Tr.S("по размеру", "by size"), Tr.S("по имени", "by name"), Tr.S("по количеству файлов", "by file count"));
            _cmbFsTheme = FsCombo(combos, Tr.S("Тема:", "Theme:"), 150,
                                  Tr.S("как в системе", "follow system"), Tr.S("тёмная", "dark"), Tr.S("светлая", "light"));

            FsSection(Tr.S("Запуск вместе с Windows", "Start with Windows"));
            FlowLayoutPanel radios = new FlowLayoutPanel();
            radios.FlowDirection = FlowDirection.TopDown;
            radios.WrapContents = false;
            radios.AutoSize = true;
            radios.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            radios.Margin = new Padding(0, 0, 0, 4);
            _radFsAutoOff = FsRadio(radios, Tr.S("Не запускать", "Don't start"));
            _radFsAutoRun = FsRadio(radios, Tr.S("Запускать без прав (обычный подсчёт)", "Start without rights (regular counting)"));
            _radFsAutoTask = FsRadio(radios, Tr.S("Запускать с правами администратора — быстрый режим, одно окно UAC сейчас",
                                                  "Start as administrator — fast mode, one UAC prompt now"));
            _fsBody.Controls.Add(radios);

            _lblFsInfo = FsNote("", true);
            FlowLayoutPanel tail = FsRow();
            Button openDir = MkFlowButton(Tr.S("Папка данных", "Data folder"), 150, false);
            openDir.Click += delegate
            {
                try
                {
                    System.IO.Directory.CreateDirectory(FsPaths.DataDir);
                    Process.Start("explorer.exe", "\"" + FsPaths.DataDir + "\"");
                }
                catch (Exception ex) { FsInfo(Tr.S("Не удалось открыть папку данных: ", "Could not open the data folder: ") + ex.Message); }
            };
            tail.Controls.Add(openDir);

            _fsScroll.Resize += delegate { FsWrapNotes(); };
            FsLoadToUi();
            return tab;
        }

        // ---------- строительные блоки столбика ----------

        private void FsSection(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            l.Margin = new Padding(0, _fsBody.Controls.Count == 0 ? 0 : 14, 0, 6);
            _fsBody.Controls.Add(l);
        }

        private Label FsNote(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 0, 0, 6);
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            _fsBody.Controls.Add(l);
            return l;
        }

        private FlowLayoutPanel FsRow()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 2, 0, 2);
            _fsBody.Controls.Add(f);
            return f;
        }

        private CheckBox FsCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 2, 0, 4);
            c.CheckedChanged += delegate { FsSettingsChanged(); };
            _fsBody.Controls.Add(c);
            return c;
        }

        private RoundComboBox FsCombo(FlowLayoutPanel row, string label, int width, params string[] items)
        {
            row.Controls.Add(MkFlowLabel(label, false));
            RoundComboBox cb = new RoundComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Width = width;
            cb.Margin = new Padding(0, 4, 24, 8);
            cb.Items.AddRange(items);
            cb.SelectedIndexChanged += delegate { FsSettingsChanged(); };
            row.Controls.Add(cb);
            return cb;
        }

        private RadioButton FsRadio(FlowLayoutPanel group, string text)
        {
            RadioButton r = new RadioButton();
            r.Text = text;
            r.AutoSize = true;
            r.Margin = new Padding(0, 2, 0, 4);
            // Click, а не CheckedChanged: переключение из кода (FsLoadToUi) не должно создавать задачу.
            r.Click += delegate { FsAutostartPicked(); };
            group.Controls.Add(r);
            return r;
        }

        // Длинные подписи переносятся по ширине страницы, а не уходят за правый край.
        private void FsWrapNotes()
        {
            if (_fsScroll == null) return;
            int w = Math.Max(Px(300), _fsScroll.ClientSize.Width - Px(24));
            foreach (Control c in _fsBody.Controls)
                if (c is Label) c.MaximumSize = new Size(w, 0);
            // Пока окно строится, обёртка узкая, и подписи переносятся на много строк. Столбик потом
            // становится ниже, а область прокрутки сама не уменьшается — под кнопками прокручивалась пустота.
            _fsBody.PerformLayout();
            _fsScroll.PerformLayout();
        }

        private void FsInfo(string text)
        {
            if (_lblFsInfo != null) _lblFsInfo.Text = text ?? "";
        }

        // ---------- вход и уход со страницы ----------

        private void FolderSizeEnter()
        {
            FsLoadToUi();
            FsWrapNotes();
            FsProbeTask();
            if (_fsTick == null)
            {
                // Состояние процесса меняется и без страницы: «Выход» в трее, перезапуск с правами.
                _fsTick = new System.Windows.Forms.Timer();
                _fsTick.Interval = 2000;
                _fsTick.Tick += delegate { FsRefreshStatus(); };
            }
            _fsTick.Start();
        }

        private void FolderSizeLeave()
        {
            if (_fsTick != null) _fsTick.Stop();
        }

        // ---------- настройки ----------

        private void FsLoadToUi()
        {
            FsSettings s = FsSettings.Load();
            _fsLoading = true;
            try
            {
                _chkFsOverlay.Checked = s.InlineOverlay;
                _chkFsOverlayFiles.Checked = s.OverlayFileSizes;
                _chkFsPanel.Checked = s.ShowSidePanel;
                _chkFsUnfocused.Checked = s.ShowWhenExplorerUnfocused;
                _chkFsBadge.Checked = s.ShowScanBadge;
                _chkFsHidden.Checked = s.ShowHidden;
                _chkFsMakeRoom.Checked = s.MakeRoomForPanel;
                _chkFsNtfs.Checked = s.UseFastNtfsIndex;
                _chkFsFreeSpace.Checked = s.ShowFreeSpace;
                _chkFsHotkey.Checked = s.PanelHotkey;
                _chkFsMenuExtras.Checked = s.PanelMenuExtras;
                _cmbFsSide.SelectedIndex = s.DockRight ? 0 : 1;
                _cmbFsSort.SelectedIndex = s.Sort == SizeSortMode.NameAscending ? 1 : s.Sort == SizeSortMode.ItemsDescending ? 2 : 0;
                _cmbFsTheme.SelectedIndex = s.FollowSystemTheme ? 0 : s.DarkTheme ? 1 : 2;
            }
            finally { _fsLoading = false; }
            FsRefreshStatus();
        }

        // Файл перечитывается перед записью: фоновый режим сам сохраняет ширину панели и то, что
        // переключили в его меню, — страница не должна затирать это своей старой копией.
        private void FsSettingsChanged()
        {
            if (_fsLoading || _closing) return;
            try
            {
                FsSettings s = FsSettings.Load();
                s.InlineOverlay = _chkFsOverlay.Checked;
                s.OverlayFileSizes = _chkFsOverlayFiles.Checked;
                s.ShowSidePanel = _chkFsPanel.Checked;
                s.ShowWhenExplorerUnfocused = _chkFsUnfocused.Checked;
                s.ShowScanBadge = _chkFsBadge.Checked;
                s.ShowHidden = _chkFsHidden.Checked;
                s.MakeRoomForPanel = _chkFsMakeRoom.Checked;
                s.UseFastNtfsIndex = _chkFsNtfs.Checked;
                s.ShowFreeSpace = _chkFsFreeSpace.Checked;
                s.PanelHotkey = _chkFsHotkey.Checked;
                s.PanelMenuExtras = _chkFsMenuExtras.Checked;
                s.DockRight = _cmbFsSide.SelectedIndex != 1;
                s.Sort = _cmbFsSort.SelectedIndex == 1 ? SizeSortMode.NameAscending
                       : _cmbFsSort.SelectedIndex == 2 ? SizeSortMode.ItemsDescending : SizeSortMode.SizeDescending;
                s.FollowSystemTheme = _cmbFsTheme.SelectedIndex <= 0;
                if (_cmbFsTheme.SelectedIndex > 0) s.DarkTheme = _cmbFsTheme.SelectedIndex == 1;
                s.Save();
                FsIpc.Signal(FsIpc.ReloadName);
                FsInfo(Tr.S("Сохранено.", "Saved."));
            }
            catch (Exception ex) { FsInfo(Tr.S("Не удалось сохранить: ", "Could not save: ") + ex.Message); }
        }

        // ---------- состояние ----------

        private void FsRefreshStatus()
        {
            if (_lblFsStatus == null || _closing) return;
            FsStatus st = FsStatus.Read();
            bool running = st != null;
            bool busy = _fsBusy != 0;
            if (!running)
                _lblFsStatus.Text = Tr.S("○ Не запущен", "○ Not running");
            else if (st.Elevated)
                _lblFsStatus.Text = Tr.S("● Работает с правами администратора — быстрый режим доступен", "● Running as administrator — fast mode is available")
                                    + (st.Pid > 0 ? " · PID " + st.Pid : "");
            else
                _lblFsStatus.Text = Tr.S("● Работает без прав — обычный подсчёт", "● Running without rights — regular counting")
                                    + (st.Pid > 0 ? " · PID " + st.Pid : "");

            _btnFsStart.Enabled = !running && !busy;
            _btnFsStop.Enabled = running && !busy;
            _btnFsShow.Enabled = running && !busy;
            _btnFsFast.Enabled = !busy && !(running && st.Elevated);
            _btnFsFast.Text = running && st.Elevated ? Tr.S("Быстрый режим включён", "Fast mode is on")
                            : _fsHasTask ? Tr.S("Перезапустить с правами", "Restart as administrator")
                            : Tr.S("Включить быстрый режим", "Enable fast mode");

            _fsLoading = true;
            try
            {
                _radFsAutoOff.Checked = !_fsHasTask && !_fsHasRun;
                _radFsAutoRun.Checked = !_fsHasTask && _fsHasRun;
                _radFsAutoTask.Checked = _fsHasTask;
                _radFsAutoOff.Enabled = _radFsAutoRun.Enabled = _radFsAutoTask.Enabled = !busy && _fsTaskProbe == 0;
            }
            finally { _fsLoading = false; }

            // Отдельная программа, из которой перенесён этот режим: вдвоём они рисуют числа поверх друг друга.
            bool legacyRuns = FsIpc.IsLegacyRunning();
            bool legacyRun = FsAutoStart.LegacyRunEntryPresent();
            bool legacy = legacyRuns || legacyRun;
            if (_lblFsLegacy.Visible != legacy) { _lblFsLegacy.Visible = legacy; _btnFsLegacy.Visible = legacy; }
            if (legacy)
                _lblFsLegacy.Text = (legacyRuns ? Tr.S("Работает отдельная программа FolderSizePanel", "The standalone FolderSizePanel is running")
                                                : Tr.S("Отдельная FolderSizePanel стоит в автозапуске", "The standalone FolderSizePanel is set to start with Windows"))
                                    + Tr.S(" — вместе с этим режимом числа в Проводнике будут нарисованы дважды.",
                                           " — together with this mode, sizes in Explorer are drawn twice.");
            _btnFsLegacy.Enabled = !busy;
        }

        // Наличие задачи знает только schtasks.exe — это процесс, поэтому не на потоке окна.
        private void FsProbeTask()
        {
            if (Interlocked.CompareExchange(ref _fsTaskProbe, 1, 0) != 0) return;
            Thread t = new Thread(delegate()
            {
                bool task = false, run = false;
                try
                {
                    task = FsAutoStart.HasScheduledTask();
                    run = FsAutoStart.HasRunEntry();
                }
                catch { }
                UiPost(delegate
                {
                    _fsHasTask = task;
                    _fsHasRun = run;
                    Interlocked.Exchange(ref _fsTaskProbe, 0);
                    FsRefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- действия ----------

        // Одно действие за раз: запуск, остановка, задача — всё это ждёт другой процесс, окно — нет.
        private void FsRun(string busyText, Func<string> work)
        {
            if (Interlocked.CompareExchange(ref _fsBusy, 1, 0) != 0) return;
            FsInfo(busyText);
            FsRefreshStatus();
            Thread t = new Thread(delegate()
            {
                string msg;
                try { msg = work(); }
                catch (Exception ex) { msg = ex.Message; }
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _fsBusy, 0);
                    FsInfo(msg);
                    FsProbeTask();
                    FsRefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void FsStart()
        {
            FsRun(Tr.S("Запуск…", "Starting…"), delegate
            {
                if (FsIpc.IsRunning()) return Tr.S("Уже запущен.", "Already running.");
                // Есть задача с правами — запуск через неё: быстрый режим без окна UAC.
                if (FsAutoStart.HasScheduledTask() && FsAutoStart.RunScheduledTask())
                    return FsWaitStarted() ? Tr.S("Запущен с правами администратора.", "Started as administrator.")
                                           : Tr.S("Задача запущена, но фоновый режим не ответил.", "The task ran, but background mode did not respond.");
                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, FsMode.Switch);
                psi.UseShellExecute = false;
                Process.Start(psi).Dispose();
                return FsWaitStarted() ? Tr.S("Запущен.", "Started.") : Tr.S("Фоновый режим не ответил.", "Background mode did not respond.");
            });
        }

        private static bool FsWaitStarted()
        {
            for (int i = 0; i < 50; i++)
            {
                if (FsIpc.IsRunning()) return true;
                Thread.Sleep(100);
            }
            return false;
        }

        private void FsStop()
        {
            FsRun(Tr.S("Остановка…", "Stopping…"), delegate
            {
                if (!FsIpc.Signal(FsIpc.ShutdownName)) return Tr.S("Не запущен.", "Not running.");
                return FsIpc.WaitStopped(8000) ? Tr.S("Остановлен.", "Stopped.")
                                               : Tr.S("Фоновый режим не остановился за 8 секунд.", "Background mode did not stop within 8 seconds.");
            });
        }

        // Одно окно UAC: помощник создаёт задачу, а запускает её уже само окно — пользователь владеет ею
        // и стартует её без прав. Работающий экземпляр без прав сначала уходит, иначе новый увидит его мьютекс.
        private void FsEnableFast()
        {
            FsRun(Tr.S("Жду подтверждения прав: ", "Waiting for the rights prompt: ") + Elevation.JobTitle("foldersize"), delegate
            {
                if (!FsAutoStart.HasScheduledTask())
                {
                    string err = FsCreateTask(true);
                    if (err != null) return err;
                }
                FsAutoStart.RemoveRunEntry();
                return FsRestartViaTask();
            });
        }

        private static string FsRestartViaTask()
        {
            if (FsIpc.IsRunning())
            {
                FsIpc.Signal(FsIpc.ShutdownName);
                if (!FsIpc.WaitStopped(8000))
                    return Tr.S("Задача создана, но работающий фоновый режим не остановился — перезапустите его из трея.",
                                "The task is created, but the running background mode did not stop — restart it from the tray.");
            }
            if (!FsAutoStart.RunScheduledTask())
                return Tr.S("Задача создана, но не запустилась.", "The task is created, but it did not start.");
            return FsWaitStarted() ? Tr.S("Быстрый режим включён: фоновый режим работает с правами и будет так запускаться при входе в Windows.",
                                          "Fast mode is on: background mode runs elevated and will start that way at sign-in.")
                                   : Tr.S("Задача запущена, но фоновый режим не ответил.", "The task ran, but background mode did not respond.");
        }

        // null — успех. Создать или удалить задачу с наивысшими правами может только процесс с правами.
        private string FsCreateTask(bool create)
        {
            ElevJob job = new ElevJob();
            job.Kind = "foldersize";
            job.Flag = create;
            ElevResult r = Elevation.Run(_engine, job, null, null);
            if (r.Ok) return null;
            return r.Declined ? DeclinedNote() : (r.Message ?? "schtasks");
        }

        private void FsAutostartPicked()
        {
            if (_fsLoading) return;
            bool wantTask = _radFsAutoTask.Checked, wantRun = _radFsAutoRun.Checked;
            if (wantTask == _fsHasTask && wantRun == (_fsHasRun && !_fsHasTask)) return;
            FsRun(wantTask ? Tr.S("Жду подтверждения прав: ", "Waiting for the rights prompt: ") + Elevation.JobTitle("foldersize")
                           : Tr.S("Меняю автозапуск…", "Changing startup…"), delegate
            {
                if (wantTask)
                {
                    string err = FsAutoStart.HasScheduledTask() ? null : FsCreateTask(true);
                    if (err != null) return err;
                    FsAutoStart.RemoveRunEntry();
                    return Tr.S("Фоновый режим будет запускаться с правами администратора при входе в Windows.",
                                "Background mode will start as administrator at sign-in.");
                }
                // Задача с наивысшими правами удаляется тоже только с правами.
                if (FsAutoStart.HasScheduledTask())
                {
                    string err = FsCreateTask(false);
                    if (err != null) return err;
                }
                if (wantRun)
                {
                    if (!FsAutoStart.CreateRunEntry()) return Tr.S("Не удалось записать автозапуск.", "Could not write the startup entry.");
                    return Tr.S("Фоновый режим будет запускаться при входе в Windows, без прав.", "Background mode will start at sign-in, without rights.");
                }
                FsAutoStart.RemoveRunEntry();
                return Tr.S("Автозапуск выключен.", "Startup is off.");
            });
        }

        // Отдельная программа останавливается своим же событием — так же, как её «Выход» в трее.
        private void FsStopLegacy()
        {
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Остановить отдельную программу FolderSizePanel и убрать её из автозапуска?\r\n\r\nСама программа и её файлы останутся на диске.",
                     "Stop the standalone FolderSizePanel and remove it from startup?\r\n\r\nThe program and its files stay on disk."),
                Tr.S("Размеры папок", "Folder sizes"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            FsRun(Tr.S("Останавливаю FolderSizePanel…", "Stopping FolderSizePanel…"), delegate
            {
                FsAutoStart.RemoveLegacyRunEntry();
                if (FsIpc.IsLegacyRunning())
                {
                    FsIpc.Signal(FsIpc.LegacyShutdownName);
                    for (int i = 0; i < 50 && FsIpc.IsLegacyRunning(); i++) Thread.Sleep(100);
                }
                string msg = FsIpc.IsLegacyRunning()
                    ? Tr.S("FolderSizePanel убрана из автозапуска, но не остановилась — закройте её из трея.",
                           "FolderSizePanel is removed from startup, but did not stop — close it from the tray.")
                    : Tr.S("FolderSizePanel остановлена и убрана из автозапуска.", "FolderSizePanel is stopped and removed from startup.");
                if (FsAutoStart.LegacyTaskPresent())
                    msg += Tr.S(" Её задача «FolderSizePanel» в Планировщике осталась.", " Its «FolderSizePanel» Task Scheduler task remains.");
                return msg;
            });
        }
    }
}
