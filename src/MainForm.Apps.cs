// Windows Process Cleaner — вкладка «Программы»: список и деинсталляция
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
        // ---------- Вкладка: Программы (деинсталляция) ----------

        private Control BuildAppsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            _btnAppsRefresh = MkFlowButton(Tr.S("Обновить список", "Refresh list"), 170, true);
            _btnAppsRefresh.Click += delegate { RefreshApps(true); };
            _btnAppsUninstall = MkFlowButton(Tr.S("Удалить выбранное", "Uninstall selected"), 200, true);
            _btnAppsUninstall.Click += delegate { DoUninstall(); };
            // Раньше на вкладке не было ни одной кнопки остановки, а очередь ждала каждый
            // деинсталлятор до 45 минут: если мастер удаления замер на вопросе, выйти из
            // этого можно было только убив приложение.
            _btnAppsCancel = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnAppsCancel.Enabled = false;
            _btnAppsCancel.Click += delegate { CancelUninstall(); };

            Label warn = MkNote(Tr.S("Запускается штатный деинсталлятор программы (может открыть своё окно/запросить подтверждение).",
                                     "Launches the program's own uninstaller (may open its own window / ask for confirmation)."), true);
            _lblAppsInfo = MkNote(Tr.S("Нажмите «Обновить список»", "Click “Refresh list”"), false);

            top.Controls.Add(_btnAppsRefresh);
            top.Controls.Add(_btnAppsUninstall);
            top.Controls.Add(_btnAppsCancel);

            _lvApps = new FastListView();
            _lvApps.Dock = DockStyle.Fill;
            _lvApps.View = View.Details;
            _lvApps.CheckBoxes = true;
            _lvApps.FullRowSelect = true;
            // Сеансовая полка: «удалить эти программы» — решение на сейчас, возвращать его
            // отмеченным после перезапуска приложения было бы опасной услужливостью.
            MemWatch(_lvApps, AppsScope, true, AppsMemKey);
            _lvApps.Columns.Add(Tr.S("Программа", "Program"), 340);
            _lvApps.Columns.Add(Tr.S("Версия", "Version"), 130);
            _lvApps.Columns.Add(Tr.S("Издатель", "Publisher"), 260);
            _lvApps.Columns.Add(Tr.S("Размер", "Size"), 100);
            SetupOwnerDraw(_lvApps);

            tab.Controls.Add(_lvApps);
            tab.Controls.Add(_lblAppsInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        // Список установленных программ читается из реестра и для каждой записи ищет exe
        // на диске. В UI-потоке это давало многосекундное замирание при каждом
        // переключении на вкладку. Теперь: фон + кэш, чтобы повторный вход был мгновенным.
        private int _appsBusy;
        private int _uninstBusy;      // идёт очередь деинсталляций (фоновый поток)
        private string _appsNote;     // одноразовая приписка к строке «Установленных программ: N»

        private Button _btnAppsRefresh, _btnAppsUninstall, _btnAppsCancel;
        private volatile bool _uninstCancel;   // «Стоп»: перестать ждать и не начинать следующую
        private volatile string _uninstStage;  // что именно сейчас ждём (сообщает Engine.WaitUninstall)
        private volatile string _uninstFail;   // последний отказ запуска — виден сразу, а не в конце очереди
        private System.Windows.Forms.Timer _appsTick;
        private DateTime _appsStarted;         // начало текущей деинсталляции (UI-поток)
        private string _uninstNow;
        private int _uninstIdx, _uninstTotal;

        // Деинсталлятор может открыть окно и ждать ответа сколько угодно; счётчик «(2/5)»
        // при этом не меняется часами. Секундомер и строка «чего ждём» — единственное, что
        // отличает идущую работу от зависшего приложения.
        private void StartAppsTicker()
        {
            _appsStarted = DateTime.UtcNow;
            if (_appsTick == null)
            {
                _appsTick = new System.Windows.Forms.Timer();
                _appsTick.Interval = 500;
                _appsTick.Tick += delegate { AppsTick(); };
            }
            _appsTick.Start();
            AppsTick();
        }

        private void StopAppsTicker()
        {
            if (_appsTick != null) _appsTick.Stop();
        }

        private void AppsTick()
        {
            if (_uninstBusy == 0) { StopAppsTicker(); return; }
            string stage = _uninstStage, fail = _uninstFail;
            _lblAppsInfo.Text = Tr.S("Идёт деинсталляция: ", "Uninstalling: ") + (_uninstNow ?? "")
                + " (" + _uninstIdx + "/" + _uninstTotal + ")"
                + "   ·   " + Elapsed(DateTime.UtcNow - _appsStarted)
                + (string.IsNullOrEmpty(stage) ? "" : "   ·   " + stage)
                + (_uninstCancel ? Tr.S("   ·   останавливаю очередь…", "   ·   stopping the queue…") : "")
                + (string.IsNullOrEmpty(fail) ? "" : "   ·   " + fail);
        }

        private void SetAppsUiBusy(bool busy)
        {
            if (_btnAppsRefresh != null) _btnAppsRefresh.Enabled = !busy;
            if (_btnAppsUninstall != null) _btnAppsUninstall.Enabled = !busy;
            if (_btnAppsCancel != null) _btnAppsCancel.Enabled = busy;
        }

        // Чужой процесс не убиваем: остановленный на середине деинсталлятор оставляет
        // программу в полуудалённом виде. Мы лишь перестаём его ждать и не начинаем следующий.
        private void CancelUninstall()
        {
            _uninstCancel = true;
            AppsTick();
        }

        private void RefreshApps() { RefreshApps(false); }

        private void RefreshApps(bool force)
        {
            // Пока идёт очередь, список не перечитываем: возврат на вкладку затирал строку
            // прогресса — единственный признак того, что удаление вообще идёт, — а сам список
            // показывал ещё не удалённую программу и гонялся с финальным обновлением очереди.
            if (_uninstBusy != 0) return;
            if (!force && _apps != null && _apps.Count > 0) { PopulateApps(_apps); return; }
            if (Interlocked.CompareExchange(ref _appsBusy, 1, 0) != 0) return;
            _lblAppsInfo.Text = Tr.S("Чтение списка программ…", "Reading program list…");
            Thread t = new Thread(delegate()
            {
                List<InstalledApp> found = null;
                string err = null;
                // Чтение трёх ульев с поиском exe на диске — это секунды: движок называет,
                // какой раздел читается сейчас, иначе строка «Чтение списка программ…» стоит молча.
                try
                {
                    found = _engine.GetInstalledApps(delegate(string s)
                    {
                        UiPost(delegate { if (_appsBusy != 0) _lblAppsInfo.Text = Tr.S("Чтение списка программ: ", "Reading program list: ") + s; });
                    });
                }
                catch (Exception ex) { err = ex.Message; found = new List<InstalledApp>(); }
                string errCopy = err;
                UiPost(delegate
                {
                    _apps = found;
                    // Ошибка чтения раньше приводила к пустому списку без единого слова.
                    if (errCopy != null) _appsNote = Tr.S("   ·   ошибка чтения: ", "   ·   read error: ") + errCopy;
                    PopulateApps(found);
                });
                Interlocked.Exchange(ref _appsBusy, 0);
            });
            t.IsBackground = true;
            t.Start();
        }

        // Программа опознаётся разделом реестра, а где его нет — именем: список пересобирается
        // после каждой деинсталляции, и отмеченное пользователем не должно при этом слетать.
        private const string AppsScope = "apps";

        private static string AppsMemKey(ListViewItem it)
        {
            InstalledApp a = it.Tag as InstalledApp;
            if (a == null) return null;
            return !string.IsNullOrEmpty(a.RegKey) ? a.RegKey : a.Name;
        }

        private void PopulateApps(List<InstalledApp> apps)
        {
            MemBeginFill();
            _lvApps.BeginUpdate();
            try
            {
                _lvApps.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (InstalledApp a in apps)
                {
                    ListViewItem it = new ListViewItem(a.Name);
                    it.SubItems.Add(a.Version ?? "");
                    it.SubItems.Add(a.Publisher ?? "");
                    it.SubItems.Add(a.EstimatedSizeBytes > 0 ? Engine.FormatBytes(a.EstimatedSizeBytes) : "");
                    it.ToolTipText = a.Name + (string.IsNullOrEmpty(a.ExePath) ? "" : "\r\n" + a.ExePath);
                    it.Tag = a;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                }
                _lvApps.Items.AddRange(rows.ToArray());
                MemEndFill(_lvApps, AppsScope, true, AppsMemKey, null);
            }
            finally { _lvApps.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvApps);
            string note = _appsNote ?? Tr.S("   ·   отметьте и нажмите «Удалить выбранное»", "   ·   check and click “Uninstall selected”");
            _appsNote = null;
            _lblAppsInfo.Text = Tr.S("Установленных программ: ", "Installed programs: ") + apps.Count + note;
        }

        private void DoUninstall()
        {
            if (_uninstBusy != 0)
            {
                _lblAppsInfo.Text = Tr.S("Дождитесь окончания текущей деинсталляции.", "Wait for the current uninstall to finish.");
                return;
            }
            List<InstalledApp> sel = new List<InstalledApp>();
            foreach (ListViewItem it in _lvApps.Items)
                if (it.Checked && it.Tag is InstalledApp) sel.Add((InstalledApp)it.Tag);
            if (sel.Count == 0) { MsgInfo(Tr.S("Не выбрано ни одной программы.", "No programs selected."), Tr.S("Программы", "Programs")); return; }

            // Раньше здесь был отдельный модальный вопрос на КАЖДУЮ отмеченную программу, и
            // ответ «Нет» на все заканчивался молчаливым выходом: галочки на месте, ни слова
            // на экране — клик выглядел проигнорированным. Теперь один вопрос со списком.
            StringBuilder names = new StringBuilder();
            for (int i = 0; i < sel.Count && i < 12; i++)
                names.Append("\r\n  · ").Append(sel[i].Name);
            if (sel.Count > 12) names.Append(Tr.S("\r\n  · … и ещё ", "\r\n  · … and ")).Append(sel.Count - 12);

            string ask = Tr.S("Удалить программ: ", "Uninstall programs: ") + sel.Count + names.ToString()
                       + Tr.S("\r\n\r\nДеинсталляторы запускаются по очереди — каждый может открыть своё окно и что-то спросить. Продолжить?",
                              "\r\n\r\nThe uninstallers run one after another — each may open its own window and ask questions. Continue?");
            if (MessageBox.Show(this, ask, Tr.S("Деинсталляция", "Uninstall"),
                                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                _lblAppsInfo.Text = Tr.S("Ничего не удалено — вы отказались. Отметки остались на месте.",
                                         "Nothing was uninstalled — you declined. The checkmarks are still set.");
                return;
            }
            List<InstalledApp> queue = sel;
            if (Interlocked.CompareExchange(ref _uninstBusy, 1, 0) != 0) return;

            _uninstCancel = false;
            _uninstStage = null;
            _uninstFail = null;
            _uninstNow = queue[0].Name;
            _uninstIdx = 1;
            _uninstTotal = queue.Count;
            SetAppsUiBusy(true);
            StartAppsTicker();

            // Деинсталляторы — по очереди, с ожиданием: два MSI разом = «уже идёт другая установка»,
            // а запущенные скопом окна перекрывают друг друга. Ждём только сам запущенный процесс:
            // msiexec и setup.exe, передавшие работу дочернему, нам не видны. Отказ запуска
            // (нет команды, деинсталлятор удалён вместе с папкой игры) раньше глотался молча.
            Thread t = new Thread(delegate()
            {
                int removed = 0, notStarted = 0;
                List<string> failed = new List<string>(), kept = new List<string>();
                for (int i = 0; i < queue.Count; i++)
                {
                    if (_uninstCancel) { notStarted = queue.Count - i; break; }
                    InstalledApp a = queue[i];
                    int idx = i + 1;
                    _uninstStage = null;
                    // Имя, номер и точку отсчёта секундомера ставим в UI-потоке: их читает тикер,
                    // и так они не требуют синхронизации.
                    UiPost(delegate
                    {
                        _uninstNow = a.Name;
                        _uninstIdx = idx;
                        _appsStarted = DateTime.UtcNow;
                        AppsTick();
                    });
                    Process p = null; string err;
                    try { err = _engine.RunUninstall(a, out p); }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null)
                    {
                        failed.Add(a.Name + " — " + err);
                        // Об отказе запуска пользователь узнавал только из окна в конце очереди,
                        // то есть, возможно, через часы; теперь строка состояния говорит сразу.
                        _uninstFail = Tr.S("не запустился: ", "did not start: ") + a.Name + " — " + err;
                        continue;
                    }
                    // Ждём не только запущенный процесс, но и его потомков, и исчезновение записи
                    // в реестре: bootstrapper-ы возвращаются сразу, а список обновлялся раньше времени.
                    bool gone = false;
                    try
                    {
                        gone = _engine.WaitUninstall(a, p, 45 * 60 * 1000,
                                                     delegate { return _uninstCancel; },
                                                     delegate(string st) { _uninstStage = st; });
                    }
                    catch { }
                    if (p != null) p.Dispose();
                    if (gone) removed++; else kept.Add(a.Name);
                }
                int removedCopy = removed, notStartedCopy = notStarted;
                bool cancelled = _uninstCancel;
                UiPost(delegate
                {
                    StopAppsTicker();
                    SetAppsUiBusy(false);
                    // Флаг снимаем в UI-потоке и до RefreshApps: пока он стоит, обновление
                    // списка запрещено, а финальное обновление сделать нужно.
                    Interlocked.Exchange(ref _uninstBusy, 0);
                    if (failed.Count > 0)
                        MsgError(Tr.S("Не удалось запустить деинсталлятор:\r\n", "Failed to launch the uninstaller:\r\n") + string.Join("\r\n", failed.ToArray()));
                    _appsNote = (cancelled ? Tr.S("   ·   остановлено", "   ·   stopped") : "")
                        + Tr.S("   ·   удалено: ", "   ·   removed: ") + removedCopy
                        + (notStartedCopy > 0 ? Tr.S("   ·   не начинали: ", "   ·   not started: ") + notStartedCopy : "")
                        + (kept.Count > 0 ? Tr.S("   ·   осталось (отменено или ещё идёт): ", "   ·   still installed (cancelled or still running): ") + string.Join(", ", kept.ToArray()) : "")
                        + (failed.Count > 0 ? Tr.S("   ·   не удалось запустить: ", "   ·   failed to launch: ") + failed.Count : "")
                        + (cancelled ? Tr.S("   ·   уже запущенные деинсталляторы мы не закрывали — они работают сами",
                                            "   ·   uninstallers already running were not closed — they keep going on their own") : "");
                    RefreshApps(true);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
