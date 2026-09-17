// SysDeck — «Размеры папок»: фоновый режим (значок в трее), его ключи командной строки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck.FolderSize
{
    // ------------------------------------------------------------------ //
    //  Ключи exe для «Размеров папок». Проверяются в Main раньше мьютекса основного окна: фоновый режим —
    //  отдельный процесс со своим мьютексом и живёт независимо от окна.
    // ------------------------------------------------------------------ //
    internal static class FsMode
    {
        public const string Switch = "--foldersize";                    // фоновый режим: трей, панель, числа в Проводнике
        public const string ExitSwitch = "--foldersize-exit";           // остановить работающий фоновый режим
        public const string MeasureSwitch = "--foldersize-measure";     // <папка> [--hidden] — подсчёт в консоль
        public const string ProbeSwitch = "--foldersize-probe";         // <hwnd> — что читается из окна Проводника
        public const string AutostartSwitch = "--foldersize-autostart"; // on|off
        public const string ReplaceSwitch = "--foldersize-replace";     // сменить уже работающий экземпляр (перезапуск с правами)

        // true — ключ наш и обработан; exitCode — код возврата процесса.
        public static bool TryRun(string[] args, out int exitCode)
        {
            exitCode = 0;
            if (args == null || args.Length == 0) return false;
            int at;
            if ((at = IndexOf(args, ExitSwitch)) >= 0)
            {
                exitCode = FsIpc.Signal(FsIpc.ShutdownName) ? 0 : 1;
                return true;
            }
            if ((at = IndexOf(args, MeasureSwitch)) >= 0)
            {
                string target = at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : Directory.GetCurrentDirectory();
                exitCode = WithConsole(delegate { return MeasureCommand(target, IndexOf(args, "--hidden") >= 0); });
                return true;
            }
            if ((at = IndexOf(args, ProbeSwitch)) >= 0)
            {
                string handle = at + 1 < args.Length ? args[at + 1] : "";
                exitCode = WithConsole(delegate { return ProbeCommand(handle); });
                return true;
            }
            if ((at = IndexOf(args, AutostartSwitch)) >= 0)
            {
                bool on = at + 1 >= args.Length || !string.Equals(args[at + 1], "off", StringComparison.OrdinalIgnoreCase);
                exitCode = WithConsole(delegate { return AutostartCommand(on); });
                return true;
            }
            if (IndexOf(args, Switch) >= 0)
            {
                exitCode = RunBackground(IndexOf(args, ReplaceSwitch) >= 0);
                return true;
            }
            return false;
        }

        private static int IndexOf(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // exe собран как winexe: своей консоли нет, поэтому вывод прицепляется к консоли того, кто запустил.
        // Перенаправленный в файл вывод работает и без этого.
        private static int WithConsole(Func<int> body)
        {
            try { Win32.AttachConsole(-1); } catch { }
            try
            {
                StreamWriter writer = new StreamWriter(Console.OpenStandardOutput());
                writer.AutoFlush = true;
                Console.SetOut(writer);
            }
            catch { }
            return body();
        }

        private static void LoadLanguage()
        {
            try { Tr.En = new Engine().Config.Language == "en"; }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        // ---- фоновый режим ----
        private static bool _relaunchViaTask;

        // Попросить перезапуск через задачу Планировщика после выхода: задача запускает процесс с правами без
        // окна UAC, но сначала этот экземпляр должен отпустить мьютекс, иначе новый увидит его и тихо уйдёт.
        internal static void RelaunchViaTaskAfterExit() { _relaunchViaTask = true; }

        private static int RunBackground(bool replace)
        {
            LoadLanguage();
            bool first;
            Mutex instance = FsIpc.CreateInstanceMutex(out first);
            if (!first && replace)
            {
                if (instance != null) instance.Dispose();
                FsIpc.Signal(FsIpc.ShutdownName);
                FsIpc.WaitStopped(8000);
                instance = FsIpc.CreateInstanceMutex(out first);
            }
            if (!first)
            {
                // Повторный запуск лишь будит работающую панель, а не создаёт вторую.
                if (instance != null) instance.Dispose();
                FsIpc.Signal(FsIpc.ShowName);
                return 0;
            }

            RegisteredWaitHandle stopWatch = null, reloadWatch = null, showWatch = null;
            EventWaitHandle shutdown = null, reload = null, show = null;
            TrayApp app = null;
            try
            {
                FsPaths.ImportLegacy();
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e) { FsLog.Report(e.Exception); };
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e) { FsLog.Report(e.ExceptionObject as Exception); };
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // Проводник, UI Automation и наши окна — в одних физических пикселях на любом мониторе.
                Win32.UsePerMonitorDpiOnThisThread();

                shutdown = FsIpc.CreateEvent(FsIpc.ShutdownName);
                reload = FsIpc.CreateEvent(FsIpc.ReloadName);
                show = FsIpc.CreateEvent(FsIpc.ShowName);

                app = new TrayApp();
                FsStatus.Write(Elevation.IsElevated);
                TrayApp target = app;
                stopWatch = ThreadPool.RegisterWaitForSingleObject(shutdown, delegate { target.RequestExitFromAnyThread(); }, null, Timeout.Infinite, true);
                reloadWatch = ThreadPool.RegisterWaitForSingleObject(reload, delegate { target.PostToUi(target.ReloadSettings); }, null, Timeout.Infinite, false);
                showWatch = ThreadPool.RegisterWaitForSingleObject(show, delegate { target.PostToUi(target.ShowPanel); }, null, Timeout.Infinite, false);
                Application.Run();
            }
            finally
            {
                if (stopWatch != null) stopWatch.Unregister(null);
                if (reloadWatch != null) reloadWatch.Unregister(null);
                if (showWatch != null) showWatch.Unregister(null);
                if (app != null) app.Dispose();
                FsStatus.Clear();
                if (shutdown != null) shutdown.Dispose();
                if (reload != null) reload.Dispose();
                if (show != null) show.Dispose();
                try { instance.ReleaseMutex(); } catch { }
                instance.Dispose();
            }
            if (_relaunchViaTask) FsAutoStart.RunScheduledTask();
            return 0;
        }

        // ---- --foldersize-measure: тот же подсчёт, что у панели, в консоль и больше никуда ----
        // Чтобы числа можно было сверить с любым другим инструментом, а медленную папку — засечь без
        // снимка панели, которой для этого надо быть впереди.
        private static int MeasureCommand(string path, bool showHidden)
        {
            try
            {
                path = Path.GetFullPath(path);
                if (!Directory.Exists(path))
                {
                    Console.Error.WriteLine("not a folder: " + path);
                    return 2;
                }
                Stopwatch clock = Stopwatch.StartNew();
                List<SizeRow> rows = DirectoryWalker.ListChildren(path, showHidden);
                List<SizeRow> links = new List<SizeRow>(), refused = new List<SizeRow>(), folders = new List<SizeRow>();
                long fileBytes = 0, fileCount = 0;
                foreach (SizeRow r in rows)
                {
                    if (!r.IsDirectory) { fileBytes += r.Bytes; fileCount++; }
                    else if (r.IsReparsePoint) links.Add(r);
                    else if (DirectoryWalker.IsUnwalkableDirectory(r.FullPath)) refused.Add(r);
                    else folders.Add(r);
                }
                Console.Out.WriteLine(path);
                Console.Out.WriteLine("listed in " + clock.ElapsedMilliseconds + " ms: " + folders.Count + " folders, " + links.Count + " links, " + fileCount + " files");
                Console.Out.WriteLine("workers: " + TreeMeasurer.Workers);
                Console.Out.WriteLine();
                foreach (SizeRow link in links) Console.Out.WriteLine("—".PadLeft(10) + "  " + "link".PadLeft(18) + "  " + link.Name);
                foreach (SizeRow trap in refused) Console.Out.WriteLine("—".PadLeft(10) + "  " + "unwalkable name".PadLeft(18) + "  " + trap.Name);

                ProgressCounters counters = new ProgressCounters();
                List<TreeMeasurer.Job> jobs = new List<TreeMeasurer.Job>(folders.Count);
                foreach (SizeRow row in folders)
                {
                    TreeMeasurer.Job job = new TreeMeasurer.Job();
                    job.Row = row;
                    jobs.Add(job);
                }
                int done = 0;
                object gate = new object();
                TreeMeasurer.Run(jobs, counters, delegate(TreeMeasurer.Job job)
                {
                    int n = Interlocked.Increment(ref done);
                    string line = (clock.ElapsedMilliseconds + " ms").PadLeft(11) + "  " + job.Bytes.ToString("N0").PadLeft(18) + "  " + job.Row.Name
                        + "  (" + job.Files.ToString("N0") + " files, " + job.Directories.ToString("N0") + " dirs" + (job.Partial ? ", partial" : "") + ")"
                        + "  [" + n + "/" + jobs.Count + "]";
                    lock (gate) Console.Out.WriteLine(line);
                }, CancellationToken.None);

                long total = fileBytes;
                foreach (TreeMeasurer.Job j in jobs) total += j.Bytes;
                long items = counters.Items + fileCount;
                Console.Out.WriteLine();
                Console.Out.WriteLine("total " + total.ToString("N0") + " B (" + (total / 1024.0 / 1024 / 1024).ToString("N2") + " GiB) over "
                                      + items.ToString("N0") + " objects in " + clock.ElapsedMilliseconds + " ms");
                if (links.Count > 0)
                {
                    List<string> names = new List<string>();
                    foreach (SizeRow l in links) names.Add(l.Name);
                    Console.Out.WriteLine(links.Count + " link(s) not followed: " + string.Join(", ", names.ToArray()));
                }
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        // ---- --foldersize-probe <hwnd>: что слой чисел видит в окне Проводника ----
        // Числа рисуются по этому чтению; когда колонка пуста, это говорит, прочитался ли список вообще, —
        // и без того, чтобы окну надо было быть впереди, как требовал бы снимок экрана.
        private static int ProbeCommand(string handleText)
        {
            long raw;
            if (!long.TryParse(handleText, NumberStyles.Integer, CultureInfo.InvariantCulture, out raw) || raw == 0)
            {
                Console.Error.WriteLine("expected a window handle: " + ProbeSwitch + " <hwnd>");
                return 2;
            }
            int code = 1;
            // UI Automation — из MTA-потока; Main у WinForms — STA.
            Thread thread = new Thread(delegate() { code = Probe(new IntPtr(raw)); });
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
            thread.Join();
            return code;
        }

        private static int Probe(IntPtr hwnd)
        {
            try
            {
                // Координаты Проводника — в физических пикселях, как у фонового режима.
                Win32.UsePerMonitorDpiOnThisThread();
                using (ExplorerListReader reader = new ExplorerListReader())
                {
                    ExplorerListLayout layout = reader.Read(hwnd);
                    if (layout == null)
                    {
                        Console.Out.WriteLine("hwnd " + hwnd + ": no layout — no Size column, no rows, or not a file list");
                        return 1;
                    }
                    Console.Out.WriteLine("hwnd " + hwnd);
                    Console.Out.WriteLine("items view : " + layout.ItemsView);
                    Console.Out.WriteLine("size column: " + layout.SizeColumn);
                    Console.Out.WriteLine("rows       : " + layout.Rows.Count);
                    Console.Out.WriteLine();
                    foreach (ExplorerRow row in layout.Rows)
                        Console.Out.WriteLine("  " + row.Name.PadRight(40) + "  cell=" + row.SizeCell);
                    return 0;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        // ---- --foldersize-autostart on|off: то же, что галочка в трее, для сценария или установщика ----
        private static int AutostartCommand(bool on)
        {
            if (!on) FsAutoStart.Disable();
            else if (!FsAutoStart.Enable()) return 1;
            Console.Out.WriteLine(FsAutoStart.IsEnabled()
                ? "autostart: on (" + (FsAutoStart.IsElevatedAtLogon() ? "scheduled task, elevated" : "HKCU Run") + ")"
                : "autostart: off");
            return 0;
        }
    }

    // ------------------------------------------------------------------ //
    //  Корень фонового режима: значок в трее, панель, трекер и движок, соединённые вместе. Всё, до чего
    //  пользователь может дотянуться, — в меню трея (и на странице «Размеры папок» основного окна).
    // ------------------------------------------------------------------ //
    internal sealed class TrayApp : IDisposable
    {
        private readonly FsSettings _settings;
        private readonly SizeEngine _engine;
        private readonly ExplorerTracker _tracker = new ExplorerTracker();
        private readonly PanelForm _panel;
        private readonly InlineSizeOverlay _overlay;
        private readonly ProgressBadge _badge;
        private readonly NotifyIcon _tray = new NotifyIcon();
        private readonly ContextMenuStrip _menu = new ContextMenuStrip();
        private readonly PanelHotkey _hotkey = new PanelHotkey();

        private ListingSnapshot _snapshot;
        private IntPtr _iconHandle;
        private bool _disposed;

        private bool PanelEnabled
        {
            get { return _settings.ShowSidePanel; }
            set { _settings.ShowSidePanel = value; _settings.Save(); }
        }

        public TrayApp()
        {
            // Движок отправляет результаты через этот контекст; он должен быть до того, как что-то запланирует работу.
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());

            _settings = FsSettings.Load();
            _engine = new SizeEngine(_settings);
            _panel = new PanelForm(_settings);
            _overlay = new InlineSizeOverlay(_settings);
            _overlay.SetTheme(FsTheme.For(_settings.IsDark));
            _badge = new ProgressBadge(_settings);
            _badge.SetTheme(FsTheme.For(_settings.IsDark));
            _tracker.KeepWhenUnfocused = _settings.ShowWhenExplorerUnfocused;

            _engine.RowsChanged += OnRows;
            _engine.ProgressChanged += _panel.ShowProgress;
            _engine.ProgressChanged += _badge.ShowProgress;

            _panel.RefreshRequested += delegate { _engine.Refresh(); };
            _panel.FastModeRequested += RestartElevated;
            _panel.MenuRequested += delegate(Point point) { _menu.Show(point); };
            _panel.RowActivated += OpenRow;
            _panel.RowMenuRequested += ShowRowMenu;
            _panel.SortRequested += ChangeSort;
            _panel.WidthChanged += delegate { _panel.DockTo(_tracker.Current); };
            _tracker.Changed += OnExplorerChanged;

            // Окно создаётся сразу, скрытым. Без дескриптора BeginInvoke не достучится до потока интерфейса:
            // остановка, запрошенная откуда-то ещё (--foldersize-exit, страница приложения), выполняла бы
            // Application.ExitThread на потоке пула и не делала ничего.
            IntPtr handle = _panel.Handle;
            GC.KeepAlive(handle);

            BuildTray();
            _hotkey.Pressed += delegate { SetPanelEnabled(!PanelEnabled); };
            ApplyHotkey(false);
            _tracker.Start();
        }

        // Занятое сочетание не ошибка программы: сказать об этом, когда пользователь его включает.
        private void ApplyHotkey(bool announce)
        {
            if (!_settings.PanelHotkey)
            {
                _hotkey.Unregister();
                return;
            }
            if (!_hotkey.Register() && announce)
                Notify(Tr.S("Сочетание " + PanelHotkey.Caption + " уже занято другой программой", "The " + PanelHotkey.Caption + " shortcut is taken by another program"));
        }

        private void BuildTray()
        {
            _menu.Opening += delegate { FsLog.Swallow(FillMenu); };
            _menu.ShowImageMargin = false;
            _iconHandle = TrayIconArt.CreateHandle();
            _tray.Icon = Icon.FromHandle(_iconHandle);
            _tray.Text = Tr.S("Размеры папок", "Folder sizes");
            _tray.Visible = true;
            _tray.ContextMenuStrip = _menu;
            _tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) SetPanelEnabled(!PanelEnabled);
            };
        }

        private void FillMenu()
        {
            _menu.Items.Clear();
            Add(Tr.S("Размеры прямо в Проводнике", "Sizes right in Explorer"), _settings.InlineOverlay, delegate
            {
                _settings.InlineOverlay = !_settings.InlineOverlay;
                _settings.Save();
                _overlay.SetEnabled(_settings.InlineOverlay);
            });
            Add(Tr.S("Перекрывать и размеры файлов", "Cover file sizes too"), _settings.OverlayFileSizes, delegate
            {
                _settings.OverlayFileSizes = !_settings.OverlayFileSizes;
                _settings.Save();
                _overlay.SetEnabled(_settings.InlineOverlay);
            });
            Add(Tr.S("Боковая панель", "Side panel"), PanelEnabled, delegate { SetPanelEnabled(!PanelEnabled); });
            Add(Tr.S("Показывать, пока Проводник открыт", "Show while Explorer is open"), _settings.ShowWhenExplorerUnfocused, delegate
            {
                _settings.ShowWhenExplorerUnfocused = !_settings.ShowWhenExplorerUnfocused;
                _settings.Save();
                _tracker.KeepWhenUnfocused = _settings.ShowWhenExplorerUnfocused;
                _tracker.Refresh();
            });
            Add(Tr.S("Индикатор подсчёта", "Counting indicator"), _settings.ShowScanBadge, delegate
            {
                _settings.ShowScanBadge = !_settings.ShowScanBadge;
                _settings.Save();
                _badge.SetEnabled();
            });
            Add(Tr.S("Свободное место диска в панели", "Free disk space in the panel"), _settings.ShowFreeSpace, delegate
            {
                _settings.ShowFreeSpace = !_settings.ShowFreeSpace;
                _settings.Save();
                _panel.ApplySettings();
            });
            Add(Tr.S("Панель по ", "Panel on ") + PanelHotkey.Caption, _settings.PanelHotkey, delegate
            {
                _settings.PanelHotkey = !_settings.PanelHotkey;
                _settings.Save();
                ApplyHotkey(true);
            });
            Add(Tr.S("Карта диска и копирование списка в меню панели", "Disk map and list copy in the panel menu"), _settings.PanelMenuExtras, delegate
            {
                _settings.PanelMenuExtras = !_settings.PanelMenuExtras;
                _settings.Save();
            });
            _menu.Items.Add(new ToolStripSeparator());

            Add(Tr.S("Обновить сейчас", "Refresh now"), null, delegate { _engine.Refresh(); });

            ToolStripMenuItem sort = new ToolStripMenuItem(Tr.S("Сортировка", "Sort"));
            AddTo(sort, Tr.S("По размеру", "By size"), _settings.Sort == SizeSortMode.SizeDescending, delegate { SetSort(SizeSortMode.SizeDescending); });
            AddTo(sort, Tr.S("По имени", "By name"), _settings.Sort == SizeSortMode.NameAscending, delegate { SetSort(SizeSortMode.NameAscending); });
            AddTo(sort, Tr.S("По количеству файлов", "By file count"), _settings.Sort == SizeSortMode.ItemsDescending, delegate { SetSort(SizeSortMode.ItemsDescending); });
            _menu.Items.Add(sort);

            ToolStripMenuItem side = new ToolStripMenuItem(Tr.S("Сторона", "Side"));
            AddTo(side, Tr.S("Справа от Проводника", "Right of Explorer"), _settings.DockRight, delegate { _settings.DockRight = true; SaveAndRedock(); });
            AddTo(side, Tr.S("Слева от Проводника", "Left of Explorer"), !_settings.DockRight, delegate { _settings.DockRight = false; SaveAndRedock(); });
            _menu.Items.Add(side);

            ToolStripMenuItem theme = new ToolStripMenuItem(Tr.S("Тема", "Theme"));
            AddTo(theme, Tr.S("Как в системе", "Follow system"), _settings.FollowSystemTheme, delegate { _settings.FollowSystemTheme = true; SaveAndRetheme(); });
            AddTo(theme, Tr.S("Тёмная", "Dark"), !_settings.FollowSystemTheme && _settings.DarkTheme, delegate { _settings.FollowSystemTheme = false; _settings.DarkTheme = true; SaveAndRetheme(); });
            AddTo(theme, Tr.S("Светлая", "Light"), !_settings.FollowSystemTheme && !_settings.DarkTheme, delegate { _settings.FollowSystemTheme = false; _settings.DarkTheme = false; SaveAndRetheme(); });
            _menu.Items.Add(theme);
            _menu.Items.Add(new ToolStripSeparator());

            Add(Tr.S("Показывать скрытые и системные", "Show hidden and system"), _settings.ShowHidden, delegate
            {
                _settings.ShowHidden = !_settings.ShowHidden;
                _settings.Save();
                _engine.Refresh();
            });
            Add(Tr.S("Не перекрывать Проводник (сдвигать окно)", "Don't cover Explorer (move the window)"), _settings.MakeRoomForPanel, delegate
            {
                _settings.MakeRoomForPanel = !_settings.MakeRoomForPanel;
                SaveAndRedock();
            });

            ToolStripMenuItem fast = new ToolStripMenuItem(Elevation.IsElevated
                ? Tr.S("Быстрый режим (чтение NTFS)", "Fast mode (NTFS read)")
                : Tr.S("Быстрый режим — нужны права администратора", "Fast mode — needs administrator rights"));
            fast.Checked = _settings.UseFastNtfsIndex;
            fast.Enabled = Elevation.IsElevated;
            fast.Click += delegate
            {
                FsLog.Swallow(delegate
                {
                    _settings.UseFastNtfsIndex = !_settings.UseFastNtfsIndex;
                    _settings.Save();
                    _engine.Refresh();
                });
            };
            _menu.Items.Add(fast);
            if (!Elevation.IsElevated)
                Add(Tr.S("Перезапустить с правами администратора", "Restart as administrator"), null, RestartElevated);
            _menu.Items.Add(new ToolStripSeparator());

            Add(Tr.S("Запускать вместе с Windows", "Start with Windows"), FsAutoStart.IsEnabled(), ToggleAutoStart);
            Add(Tr.S("О программе", "About"), null, ShowAbout);
            Add(Tr.S("Выход", "Exit"), null, RequestExit);
        }

        private void Add(string text, bool? check, Action action) { AddTo(null, text, check, action); }

        private void AddTo(ToolStripMenuItem parent, string text, bool? check, Action action)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Checked = check == true;
            item.Click += delegate { FsLog.Swallow(action); };
            if (parent == null) _menu.Items.Add(item);
            else parent.DropDownItems.Add(item);
        }

        private void SetSort(SizeSortMode mode)
        {
            _settings.Sort = mode;
            _settings.Save();
            _panel.ApplyTheme();
            ResortCurrent();
        }

        // Щелчок по заголовку переключает два очевидных порядка, не трогая диск заново.
        private void ChangeSort(SizeSortMode requested)
        {
            SetSort(_settings.Sort == requested && requested == SizeSortMode.NameAscending ? SizeSortMode.SizeDescending : requested);
        }

        private void ResortCurrent()
        {
            if (_snapshot == null || !_snapshot.Final) return;
            ListingSnapshot resorted = new ListingSnapshot();
            resorted.Path = _snapshot.Path;
            resorted.Rows = SizeSorting.Apply(_snapshot.Rows, _settings.Sort);
            resorted.Final = true;
            resorted.FastMode = _snapshot.FastMode;
            resorted.Totals = _snapshot.Totals;
            resorted.Error = _snapshot.Error;
            OnRows(resorted);
        }

        private void SaveAndRedock()
        {
            _settings.Save();
            _panel.DockTo(_tracker.Current);
        }

        private void SaveAndRetheme()
        {
            _settings.Save();
            Retheme();
        }

        private void Retheme()
        {
            _panel.ApplyTheme();
            _overlay.SetTheme(FsTheme.For(_settings.IsDark));
            _badge.SetTheme(FsTheme.For(_settings.IsDark));
        }

        // Настройки поменяли на странице основного окна: перечитать файл и применить то, что изменилось.
        public void ReloadSettings()
        {
            if (_disposed) return;
            FsSettings fresh = FsSettings.Load();
            bool rescan = fresh.ShowHidden != _settings.ShowHidden || fresh.UseFastNtfsIndex != _settings.UseFastNtfsIndex;
            bool resort = fresh.Sort != _settings.Sort;
            _settings.CopyFrom(fresh);
            _tracker.KeepWhenUnfocused = _settings.ShowWhenExplorerUnfocused;
            Retheme();
            _overlay.SetEnabled(_settings.InlineOverlay);
            _badge.SetEnabled();
            _panel.ApplySettings();
            ApplyHotkey(false);
            if (rescan) _engine.Refresh();
            else if (resort) ResortCurrent();
            if (!_settings.ShowSidePanel && _panel.Visible) _panel.Hide();
            _tracker.Refresh();
        }

        public void ShowPanel()
        {
            if (!_disposed) SetPanelEnabled(true);
        }

        private void OnRows(ListingSnapshot snapshot)
        {
            _snapshot = snapshot;
            _panel.ShowSnapshot(snapshot);
            _overlay.SetSnapshot(snapshot);
        }

        private void OnExplorerChanged(ExplorerState state)
        {
            bool live = state.Visible && !string.IsNullOrEmpty(state.Path);
            _overlay.SetTarget(state.Hwnd, state.Path, live);
            _badge.SetTarget(state.Hwnd, state.Bounds, live);
            if (!live || !PanelEnabled)
            {
                if (_panel.Visible) _panel.Hide();
                if (!live) return;
            }
            else
            {
                _panel.DockTo(state);
                if (!_panel.Visible) _panel.Show();
            }
            // Намеренно не сбрасывается, когда панель прячется: возврат в ту же папку должен быть мгновенным,
            // а числам в Проводнике нужны те же данные.
            _engine.ShowFolder(state.Path);
        }

        private void SetPanelEnabled(bool enabled)
        {
            PanelEnabled = enabled;
            if (!enabled) _panel.Hide();
            else _tracker.Refresh();
        }

        private void OpenRow(SizeRow row)
        {
            if (row.IsDirectory) ExplorerTracker.OpenInExplorer(row.FullPath);
            else Select(row.FullPath);
        }

        private static void Select(string path)
        {
            FsLog.Swallow(delegate
            {
                ProcessStartInfo psi = new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            });
        }

        private void ShowRowMenu(SizeRow row, Point screenPoint)
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.ShowImageMargin = false;
            menu.Items.Add(new ToolStripMenuItem(row.IsDirectory ? Tr.S("Открыть папку", "Open folder") : Tr.S("Показать в Проводнике", "Show in Explorer"),
                null, delegate { OpenRow(row); }));
            if (row.IsDirectory)
                menu.Items.Add(new ToolStripMenuItem(Tr.S("Показать в Проводнике", "Show in Explorer"), null, delegate { Select(row.FullPath); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem(Tr.S("Копировать путь", "Copy path"), null, delegate { Copy(row.FullPath); }));
            menu.Items.Add(new ToolStripMenuItem(Tr.S("Копировать размер (", "Copy size (") + SizeFormat.ExactBytes(row.Bytes) + ")", null,
                delegate { Copy(row.Bytes.ToString(CultureInfo.InvariantCulture)); }));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem recount = new ToolStripMenuItem(Tr.S("Пересчитать эту папку", "Recount this folder"), null, delegate { _engine.Refresh(); });
            recount.Enabled = row.IsDirectory;
            menu.Items.Add(recount);
            if (_settings.PanelMenuExtras)
            {
                menu.Items.Add(new ToolStripSeparator());
                ToolStripMenuItem map = new ToolStripMenuItem(Tr.S("Открыть в карте диска", "Open in the disk map"), null, delegate { OpenInDiskMap(row.FullPath); });
                map.Enabled = row.IsDirectory && !row.IsReparsePoint;
                menu.Items.Add(map);
                ListingSnapshot listing = _snapshot;
                ToolStripMenuItem list = new ToolStripMenuItem(Tr.S("Копировать список", "Copy the list"), null, delegate { Copy(ListingText(listing)); });
                list.Enabled = listing != null && listing.Rows.Count > 0;
                menu.Items.Add(list);
            }
            menu.Closed += delegate { menu.BeginInvoke(new Action(menu.Dispose)); };
            menu.Show(screenPoint);
        }

        // Вкладка «Диск» основного окна: уже открытое окно получит путь через свой канал активации.
        private static void OpenInDiskMap(string path)
        {
            FsLog.Swallow(delegate
            {
                // Обратная косая перед закрывающей кавычкой её экранировала бы: у корня тома («C:\») она удваивается.
                string argument = path.EndsWith("\\", StringComparison.Ordinal) ? path + "\\" : path;
                ProcessStartInfo psi = new ProcessStartInfo(FsPaths.ExecutablePath, "/disk \"" + argument + "\"");
                psi.UseShellExecute = false;
                Process.Start(psi).Dispose();
            });
        }

        // Таблица через табуляцию: вставляется в Excel столбцами, в сообщение — ровными строками.
        // Точные байты рядом с округлёнными: сортировать и складывать удобно по первым.
        internal static string ListingText(ListingSnapshot listing)
        {
            if (listing == null || listing.Rows == null) return "";
            StringBuilder sb = new StringBuilder();
            sb.Append(listing.Path).Append("\r\n");
            sb.Append(Tr.S("Имя\tРазмер\tБайт\tФайлов\tПапок", "Name\tSize\tBytes\tFiles\tFolders")).Append("\r\n");
            foreach (SizeRow row in listing.Rows)
            {
                bool known = row.State != RowState.Pending && row.State != RowState.Failed;
                sb.Append(row.DisplayName ?? row.Name).Append('\t');
                sb.Append(known ? SizeFormat.Short(row.Bytes) : "").Append('\t');
                sb.Append(known ? row.Bytes.ToString(CultureInfo.InvariantCulture) : "").Append('\t');
                sb.Append(row.IsDirectory && known ? row.Files.ToString(CultureInfo.InvariantCulture) : "").Append('\t');
                sb.Append(row.IsDirectory && known ? row.Directories.ToString(CultureInfo.InvariantCulture) : "").Append("\r\n");
            }
            DirStats totals = listing.Totals;
            sb.Append(Tr.S("Итого", "Total")).Append('\t').Append(SizeFormat.Short(totals.Bytes)).Append('\t')
              .Append(totals.Bytes.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(totals.Files.ToString(CultureInfo.InvariantCulture)).Append('\t')
              .Append(totals.Directories.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            return sb.ToString();
        }

        private static void Copy(string text)
        {
            FsLog.Swallow(delegate { if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text); });
        }

        private void ToggleAutoStart()
        {
            if (FsAutoStart.IsEnabled())
            {
                FsAutoStart.Disable();
                Notify(Tr.S("Автозапуск выключен", "Autostart is off"));
                return;
            }
            bool ok = FsAutoStart.Enable();
            Notify(ok
                ? Elevation.IsElevated
                    ? Tr.S("Автозапуск включён — задача в Планировщике, права администратора сохранятся",
                           "Autostart is on — a Task Scheduler task, administrator rights are kept")
                    : Tr.S("Автозапуск включён. Быстрый режим при старте не включится: для него нужен запуск от администратора",
                           "Autostart is on. Fast mode won't be on at startup: it needs an administrator start")
                : Tr.S("Не удалось включить автозапуск, подробности в crash.log", "Could not enable autostart, see crash.log"));
        }

        // С задачей Планировщика — без окна UAC: этот экземпляр выходит, задача поднимает новый с правами.
        // Без неё — одно окно UAC; новый экземпляр сам попросит этот уйти (--foldersize-replace).
        private void RestartElevated()
        {
            if (FsAutoStart.HasScheduledTask())
            {
                FsMode.RelaunchViaTaskAfterExit();
                RequestExit();
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(FsPaths.ExecutablePath, FsMode.Switch + " " + FsMode.ReplaceSwitch);
                psi.UseShellExecute = true;
                psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                // Сюда же приходит отказ в окне UAC — сообщать нечего, ничего не сломалось.
                FsLog.Report(ex);
            }
        }

        private void ShowAbout()
        {
            string mode = Elevation.IsElevated
                ? Tr.S("включён (чтение NTFS)", "on (NTFS read)")
                : Tr.S("выключен — запуск без прав администратора", "off — started without administrator rights");
            string autostart = FsAutoStart.IsEnabled() ? Tr.S("включён", "on") : Tr.S("выключен", "off");
            string text = Tr.S("Панель размеров папок для Проводника.", "Folder size panel for Explorer.") + "\r\n\r\n"
                + Tr.S("Быстрый режим: ", "Fast mode: ") + mode + "\r\n"
                + Tr.S("Автозапуск: ", "Autostart: ") + autostart + "\r\n"
                + Tr.S("Настройки: ", "Settings: ") + FsPaths.SettingsFile + "\r\n"
                + Tr.S("Журнал ошибок: ", "Error log: ") + FsPaths.CrashLog + "\r\n\r\n"
                + Tr.S("Размеры считаются точно, без оценок:\r\nпапка = сумма реальных размеров всех файлов внутри.\r\nЖёсткие ссылки учитываются один раз, символьные ссылки не раскрываются.",
                       "Sizes are exact, not estimated:\r\na folder = the sum of the real sizes of every file inside.\r\nHard links count once, symbolic links are not followed.");
            MessageBox.Show(text, Tr.S("Размеры папок", "Folder sizes"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Notify(string text)
        {
            FsLog.Swallow(delegate
            {
                _tray.BalloonTipTitle = Tr.S("Размеры папок", "Folder sizes");
                _tray.BalloonTipText = text;
                _tray.ShowBalloonTip(4000);
            });
        }

        public void PostToUi(Action action)
        {
            try
            {
                if (_panel.IsHandleCreated && !_panel.IsDisposed)
                    _panel.BeginInvoke(new Action(delegate { FsLog.Swallow(action); }));
            }
            catch (Exception ex) { FsLog.Report(ex); }
        }

        public void RequestExitFromAnyThread()
        {
            try
            {
                if (_panel.IsHandleCreated)
                {
                    _panel.BeginInvoke(new Action(RequestExit));
                    return;
                }
            }
            catch (Exception ex)
            {
                FsLog.Report(ex);
            }
            // Окна, через которое попросить цикл сообщений вежливо, нет. Оставить процесс живым хуже:
            // «Выход» обязан значить выход.
            Environment.Exit(0);
        }

        private void RequestExit()
        {
            _tray.Visible = false;
            Application.ExitThread();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _settings.Save();
            _hotkey.Dispose();
            _tracker.Dispose();
            _overlay.Dispose();
            _badge.Dispose();
            _engine.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            if (_iconHandle != IntPtr.Zero) Win32.DestroyIcon(_iconHandle);
            _menu.Dispose();
            _panel.Dispose();
        }
    }

    // Глобальное сочетание клавиш для панели. Окно только для сообщений: WM_HOTKEY приходит в поток,
    // зарегистрировавший сочетание, — это поток интерфейса фонового режима.
    internal sealed class PanelHotkey : NativeWindow, IDisposable
    {
        public const string Caption = "Ctrl+Alt+S";
        private const int WM_HOTKEY = 0x0312;
        private const int Id = 1;
        private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_NOREPEAT = 0x4000;
        private const uint VK_S = 0x53;
        private static readonly IntPtr HWND_MESSAGE = new IntPtr(-3);

        private bool _registered;

        public event Action Pressed;

        public bool Register()
        {
            if (_registered) return true;
            if (Handle == IntPtr.Zero)
            {
                CreateParams cp = new CreateParams();
                cp.Parent = HWND_MESSAGE;
                CreateHandle(cp);
            }
            _registered = RegisterHotKey(Handle, Id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_S);
            return _registered;
        }

        public void Unregister()
        {
            if (!_registered) return;
            UnregisterHotKey(Handle, Id);
            _registered = false;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && m.WParam.ToInt32() == Id)
            {
                Action pressed = Pressed;
                if (pressed != null) FsLog.Swallow(pressed);
                return;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            Unregister();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    }

    // Значок трея рисуется на лету — без файла .ico и чётким при любом масштабе.
    internal static class TrayIconArt
    {
        public static IntPtr CreateHandle()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (SolidBrush folder = new SolidBrush(Color.FromArgb(96, 165, 250)))
                    {
                        g.FillRectangle(folder, 3, 9, 26, 19);
                        g.FillRectangle(folder, 3, 5, 12, 5);
                    }
                    using (SolidBrush bar = new SolidBrush(Color.FromArgb(20, 30, 45)))
                    {
                        g.FillRectangle(bar, 8, 20, 4, 5);
                        g.FillRectangle(bar, 14, 16, 4, 9);
                        g.FillRectangle(bar, 20, 12, 4, 13);
                    }
                }
                return bitmap.GetHicon();
            }
        }
    }
}
