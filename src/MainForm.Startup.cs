// Windows Process Cleaner — вкладка «Автозапуск»: реестр Run и папки Startup
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
        // ---------- Вкладка: Автозапуск ----------
        private Control BuildStartupTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            _btnStartupRefresh = MkFlowButton(Tr.S("Обновить список", "Refresh list"), 170, true);
            _btnStartupRefresh.Click += delegate { RefreshStartup(true); };
            _btnStartupStop = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnStartupStop.Enabled = false;
            _btnStartupStop.Click += delegate { StopStartupRead(); };

            Label warn = MkNote(Tr.S("Галочка = запускается при входе. Снять — отключить, как в Диспетчере задач (запись сохраняется), поставить — включить.",
                                     "Checkbox = starts at sign-in. Uncheck to disable as Task Manager does (the entry is kept), check to enable."), true);
            _lblStartupInfo = MkNote(Tr.S("Нажмите «Обновить список»", "Click “Refresh list”"), false);

            top.Controls.Add(_btnStartupRefresh);
            top.Controls.Add(_btnStartupStop);

            _lvStartup = new FastListView();
            _lvStartup.Dock = DockStyle.Fill;
            _lvStartup.View = View.Details;
            _lvStartup.CheckBoxes = true;
            _lvStartup.FullRowSelect = true;
            _lvStartup.Columns.Add(Tr.S("Программа", "Program"), 300);
            _lvStartup.Columns.Add(Tr.S("Издатель / источник", "Publisher / source"), 220);
            _lvStartup.Columns.Add(Tr.S("Файл автозапуска", "Startup target"), 460);
            SetupOwnerDraw(_lvStartup);
            _pathColumns[_lvStartup] = 2;
            _lvStartup.ItemChecked += Startup_ItemChecked;

            tab.Controls.Add(_lvStartup);
            tab.Controls.Add(_lblStartupInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        // Та же история, что и с вкладкой программ, только хуже: помимо реестра здесь
        // разрешаются .lnk из папок автозагрузки. В UI-потоке это подвешивало окно
        // на каждый вход на вкладку.
        private int _startupBusy;
        private List<AutostartEntry> _autostartCache;
        private Dictionary<string, List<AutostartEntry>> _autostartByExe;
        private volatile bool _startupCancel;
        private BusyTicker _startupTicker;
        private Button _btnStartupRefresh, _btnStartupStop;
        // Данные, по которым список УЖЕ построен: вход на вкладку не должен строить его заново.
        private List<InstalledApp> _startupShownApps;
        private List<AutostartEntry> _startupShownEntries;

        private void RefreshStartup() { RefreshStartup(false); }

        private void RefreshStartup(bool force)
        {
            if (!force && _apps != null && _autostartCache != null)
            {
                // Кэш тот же и список уже заполнен — выходим сразу. Раньше каждый вход
                // на вкладку перестраивал все строки с подсказками заново, и переключение
                // само по себе выглядело как зависание.
                if (ReferenceEquals(_startupShownApps, _apps) && ReferenceEquals(_startupShownEntries, _autostartCache)
                    && _lvStartup.Items.Count > 0) return;
                PopulateStartup(_apps, _autostartCache);
                return;
            }
            if (Interlocked.CompareExchange(ref _startupBusy, 1, 0) != 0)
            {
                _lblStartupInfo.Text = Tr.S("Чтение автозапуска уже идёт — дождитесь окончания или нажмите «Стоп».",
                                            "Startup entries are already being read — wait for it or press “Stop”.");
                return;
            }
            _startupCancel = false;
            _btnStartupRefresh.Enabled = false;
            _btnStartupStop.Enabled = true;
            BusyTicker tick = new BusyTicker(_lblStartupInfo,
                Tr.S("Чтение списка установленных программ", "Reading the installed programs list"));
            _startupTicker = tick;
            Thread t = new Thread(delegate()
            {
                List<InstalledApp> apps = null;
                List<AutostartEntry> entries = null;
                string err = null;
                try
                {
                    apps = _engine.GetInstalledApps(delegate(string s) { tick.SetStage(s); });
                    // Между стадиями даём «Стопу» сработать: ярлыки из папок автозагрузки
                    // разрешаются через COM WScript.Shell, и один ярлык на отвалившийся
                    // сетевой путь тянет секунды. Прервать сам обход нечем — прерываем до него.
                    if (!_startupCancel)
                    {
                        tick.SetStage(Tr.S("Чтение записей автозапуска", "Reading startup entries"));
                        entries = _engine.GetAutostartEntries(delegate(string s)
                        {
                            tick.SetStage(Tr.S("Чтение записей автозапуска: ", "Reading startup entries: ") + s);
                        });
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                List<InstalledApp> apps2 = apps;
                List<AutostartEntry> entries2 = entries;
                string emsg = err;
                Interlocked.Exchange(ref _startupBusy, 0);
                UiPost(delegate
                {
                    tick.Stop();
                    if (ReferenceEquals(_startupTicker, tick)) _startupTicker = null;
                    _btnStartupStop.Enabled = false;
                    _btnStartupRefresh.Enabled = true;
                    if (emsg != null || apps2 == null || entries2 == null)
                    {
                        // Пустые списки вместо ошибки давали «Программ: 0» — вид пустой машины
                        // вместо честного «прочитать не удалось».
                        _lblStartupInfo.Text = emsg != null
                            ? Tr.S("Прочитать автозапуск не удалось: ", "Failed to read startup entries: ") + emsg
                            : Tr.S("Чтение прервано. Нажмите «Обновить список», чтобы прочитать заново.",
                                   "Reading stopped. Click “Refresh list” to read again.");
                        return;
                    }
                    _apps = apps2; _autostartCache = entries2;
                    PopulateStartup(apps2, entries2);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // «Стоп» не может оборвать уже начатый обход реестра или разрешение ярлыка —
        // поэтому кнопка гаснет сразу и честно говорит «останавливаю», а работа
        // прекращается на ближайшей границе стадии.
        private void StopStartupRead()
        {
            _startupCancel = true;
            _btnStartupStop.Enabled = false;
            BusyTicker t = _startupTicker;
            if (t != null) t.SetStage(Tr.S("Останавливаю чтение", "Stopping the read"));
        }

        // Индекс «нормализованный путь exe → записи автозапуска». Ключ считает сам движок
        // (Engine.NormPath): своя копия правила разъехалась бы с ним при первой же правке,
        // и часть записей перестала бы находиться.
        private static string StartupKey(string p)
        {
            return Engine.NormPath(p);
        }

        private static Dictionary<string, List<AutostartEntry>> IndexEntries(List<AutostartEntry> entries)
        {
            Dictionary<string, List<AutostartEntry>> map = new Dictionary<string, List<AutostartEntry>>(StringComparer.Ordinal);
            if (entries == null) return map;
            foreach (AutostartEntry e in entries)
            {
                string k = StartupKey(e.ExePath);
                if (k == null) continue;
                List<AutostartEntry> lst;
                if (!map.TryGetValue(k, out lst)) { lst = new List<AutostartEntry>(); map[k] = lst; }
                lst.Add(e);
            }
            return map;
        }

        // Общий пустой список: возвращается по ссылке и никем не изменяется.
        private static readonly List<AutostartEntry> NoAutostartEntries = new List<AutostartEntry>();

        private static List<AutostartEntry> EntriesFor(Dictionary<string, List<AutostartEntry>> map, string exe)
        {
            string k = StartupKey(exe);
            List<AutostartEntry> lst;
            if (map == null || k == null || !map.TryGetValue(k, out lst)) return NoAutostartEntries;
            return lst;
        }

        private void PopulateStartup(List<InstalledApp> apps, List<AutostartEntry> entries)
        {
            // Галочки выставляются программно, а обработчик ItemChecked пишет в реестр:
            // без этого флага одно заполнение списка перезаписало бы весь автозапуск.
            _suppressStartup = true;
            _lvStartup.BeginUpdate();
            HashSet<string> appExes = new HashSet<string>();
            int onCount = 0;
            // Индекс строится один раз на заполнение. Раньше на каждую программу трижды
            // звался EntriesForExe (галочка, издатель, подсказка), а он гоняет
            // Path.GetFullPath по ВСЕМ записям: ≈400 программ × ~80 записей × 3 вызова —
            // сотня тысяч обращений к файловой системе в UI-потоке на каждый вход на вкладку.
            Dictionary<string, List<AutostartEntry>> byExe = IndexEntries(entries);
            _autostartByExe = byExe;
            try
            {
                _lvStartup.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (InstalledApp a in apps)
                {
                    List<AutostartEntry> mine = EntriesFor(byExe, a.ExePath);
                    // «В автозапуске» = есть хотя бы одна ВКЛЮЧЁННАЯ запись (как в Диспетчере задач).
                    bool on = false;
                    foreach (AutostartEntry en in mine) if (en.Enabled) { on = true; break; }
                    a.InAutostart = on;
                    if (!string.IsNullOrEmpty(a.ExePath)) appExes.Add(a.ExePath.ToLowerInvariant());

                    ListViewItem it = new ListViewItem(a.Name);
                    it.SubItems.Add(StartupPublisherText(a, mine));
                    it.SubItems.Add(a.ExePath != null ? a.ExePath : Tr.S("(exe не найден)", "(exe not found)"));
                    it.ToolTipText = StartupAppTip(a, mine);
                    it.Tag = a;
                    it.Checked = on;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                    if (on) onCount++;
                }

                // записи автозапуска, не сопоставленные с установленными программами
                foreach (AutostartEntry e in entries)
                {
                    string ep = e.ExePath != null ? e.ExePath.ToLowerInvariant() : null;
                    if (ep != null && appExes.Contains(ep)) continue;
                    ListViewItem it = new ListViewItem(e.Name);
                    it.SubItems.Add(StartupSourceText(e));
                    it.SubItems.Add(e.Command != null ? e.Command : "");
                    it.ToolTipText = e.Name + "\r\n" + (e.Command != null ? e.Command : "");
                    it.Tag = e;
                    it.Checked = e.Enabled;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.CandidateBg;
                    rows.Add(it);
                    if (e.Enabled) onCount++;
                }
                _lvStartup.Items.AddRange(rows.ToArray());
            }
            finally
            {
                _lvStartup.EndUpdate();
                AutoFillLastColumnDeferred(_lvStartup);
                _suppressStartup = false;
            }

            _startupPrograms = apps.Count;
            _startupShownApps = apps;
            _startupShownEntries = entries;
            UpdateStartupInfo(onCount);
            AutoFillLastColumnDeferred(_lvStartup);
        }

        private int _startupPrograms;

        private void UpdateStartupInfo(int onCount)
        {
            _lblStartupInfo.Text = Tr.S("Программ: ", "Programs: ") + _startupPrograms +
                Tr.S("   ·   запускаются при входе: ", "   ·   start at sign-in: ") + onCount +
                Tr.S("   ·   оранжевым — записи автозапуска вне списка установленных", "   ·   orange — startup entries outside the installed list");
        }

        private static string DisabledMark() { return Tr.S("отключено", "disabled"); }

        private static string StartupSourceText(AutostartEntry e)
        {
            string src = e.SourceLabel != null ? e.SourceLabel : "";
            return e.Enabled ? src : src + " · " + DisabledMark();
        }

        // Издатель; если записи программы есть, но все отключены в Windows — пометка
        // «отключено»: без неё снятая галочка выглядела бы как «записи нет вовсе».
        // Обоим методам передаются УЖЕ отобранные записи этой программы: сами они больше
        // ничего не ищут, иначе поиск повторялся бы для каждой строки списка.
        private static string StartupPublisherText(InstalledApp a, List<AutostartEntry> mine)
        {
            string pub = a.Publisher != null ? a.Publisher : "";
            if (a.InAutostart || mine.Count == 0) return pub;
            return pub.Length == 0 ? DisabledMark() : pub + " · " + DisabledMark();
        }

        private static string StartupAppTip(InstalledApp a, List<AutostartEntry> mine)
        {
            StringBuilder sb = new StringBuilder(a.Name);
            if (!string.IsNullOrEmpty(a.ExePath)) sb.Append("\r\n").Append(a.ExePath);
            foreach (AutostartEntry e in mine)
                sb.Append("\r\n").Append(e.SourceLabel).Append(e.Enabled ? "" : " · " + DisabledMark()).Append(": ").Append(e.Command);
            return sb.ToString();
        }

        private int StartupCheckedCount()
        {
            int n = 0;
            foreach (ListViewItem it in _lvStartup.Items) if (it.Checked) n++;
            return n;
        }

        private void RevertStartupCheck(ListViewItem it)
        {
            _suppressStartup = true; it.Checked = !it.Checked; _suppressStartup = false;
        }

        private void Startup_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_suppressStartup) return;
            ListViewItem it = e.Item;
            object tag = it.Tag;
            try
            {
                // Список записей нужен для переключения: без него программа «отключалась»
                // бы вхолостую. Кэш живёт, пока мы сами его не меняем, — «Обновить список»
                // перечитывает его целиком. Читать его ЗДЕСЬ нельзя: разрешение ярлыков
                // идёт через COM и вешало окно на секунды прямо в обработчике галочки.
                if (_autostartCache == null)
                {
                    RevertStartupCheck(it);
                    _lblStartupInfo.Text = Tr.S("Список автозапуска ещё не прочитан — читаю его, повторите после обновления списка.",
                                                "The startup list has not been read yet — reading it now, try again once the list refreshes.");
                    RefreshStartup(true);
                    return;
                }
                if (tag is InstalledApp)
                {
                    InstalledApp app = (InstalledApp)tag;
                    if (string.IsNullOrEmpty(app.ExePath))
                    {
                        if (it.Checked)
                        {
                            MessageBox.Show(this, Tr.S("Не удалось определить exe этой программы — добавить в автозапуск нельзя.",
                                                 "Could not determine this program's exe — cannot add to startup."),
                                Tr.S("Автозапуск", "Startup"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            RevertStartupCheck(it);
                        }
                        return;
                    }
                    if (it.Checked) _engine.EnableAutostartForExe(app.Name, app.ExePath, _autostartCache);
                    else _engine.DisableAutostartForExe(app.ExePath, _autostartCache);
                    app.InAutostart = it.Checked;
                    // Включение могло дописать в кэш новую запись HKCU\Run — индекс
                    // пересобираем, иначе издатель и подсказка показывали бы состояние до правки.
                    _autostartByExe = IndexEntries(_autostartCache);
                    List<AutostartEntry> mine = EntriesFor(_autostartByExe, app.ExePath);
                    it.SubItems[1].Text = StartupPublisherText(app, mine);
                    it.ToolTipText = StartupAppTip(app, mine);
                }
                else if (tag is AutostartEntry)
                {
                    // Только флаг StartupApproved — как Диспетчер задач. Раньше снятая галочка
                    // УДАЛЯЛА значение Run или ярлык, а возвращённая писала новую запись в
                    // HKCU\Run без параметров командной строки.
                    AutostartEntry ent = (AutostartEntry)tag;
                    _engine.SetAutostartEnabled(ent, it.Checked);
                    it.SubItems[1].Text = StartupSourceText(ent);
                }
                UpdateStartupInfo(StartupCheckedCount());
            }
            catch (Exception ex)
            {
                // В Windows ничего не изменилось — галочка возвращается, иначе список
                // показывал бы состояние, которого нет (раньше ошибка глоталась молча).
                RevertStartupCheck(it);
                MsgError(ex.Message);
            }
        }
    }
}
