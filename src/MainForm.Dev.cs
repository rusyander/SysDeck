// Windows Process Cleaner — вкладка «Dev Cleanup»: группы процессов и занятые порты
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
        private Control BuildDevTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Dock = DockStyle.Top;
            flow.AutoSize = true;
            flow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            flow.Padding = new Padding(0, 6, 0, 0);

            AddDevButton(flow, Tr.S("Все Node", "All Node"), new string[] { "node.exe", "next.exe" });
            AddDevButton(flow, Tr.S("Все Python", "All Python"), new string[] { "python.exe", "pythonw.exe" });
            AddDevButton(flow, Tr.S("Все Java", "All Java"), new string[] { "java.exe", "gradle.exe" });
            AddDevButton(flow, Tr.S("Все Vite", "All Vite"), new string[] { "vite.exe" });
            AddDevButton(flow, Tr.S("Все Webpack", "All Webpack"), new string[] { "webpack.exe" });
            AddDevButton(flow, Tr.S("Весь npm", "All npm"), new string[] { "npm.exe" });
            AddDevButton(flow, Tr.S("Весь pnpm", "All pnpm"), new string[] { "pnpm.exe" });
            AddDevButton(flow, Tr.S("Весь yarn/bun", "All yarn/bun"), new string[] { "yarn.exe", "bun.exe" });
            AddDevButton(flow, "Docker Compose", new string[] { "docker-compose.exe", "docker.exe" });
            AddDevButton(flow, "Go / Cargo / Deno", new string[] { "go.exe", "cargo.exe", "deno.exe" });

            Label lblP = MkNote(Tr.S("Занятые dev-порты", "Busy dev ports"), false);
            lblP.Height = 30; lblP.Padding = new Padding(0, 10, 0, 0);
            Label lblPHint = MkNote(Tr.S("список портов задаётся в Настройках; отметьте строки и завершите процессы, которые их держат",
                                         "the port list is set in Settings; tick rows and terminate the processes holding them"), true);
            FlowLayoutPanel portsBar = MkToolbar();
            Button btnRefresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 110, false);
            btnRefresh.Click += delegate { RefreshPorts(); };
            Button btnKillPort = MkFlowButton(Tr.S("Завершить выбранные порты", "Kill selected ports"), 240, false);
            btnKillPort.Click += delegate { KillSelectedPorts(); };
            // «Остановить» в общем списке кнопок не состоит: гасить надо всё, кроме неё.
            _btnDevStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnDevStop.Enabled = false;
            _btnDevStop.Click += delegate
            {
                _devCancel = true;
                _btnDevStop.Enabled = false;
                _devPhase = Tr.S("Останавливаю — принудительно завершать не буду",
                                 "Stopping — nothing will be force-terminated");
            };
            portsBar.Controls.Add(btnRefresh);
            portsBar.Controls.Add(btnKillPort);
            portsBar.Controls.Add(_btnDevStop);
            _devButtons.Add(btnRefresh);
            _devButtons.Add(btnKillPort);

            _lblDev = MkNote(Tr.S("Нажмите «Обновить», чтобы перечитать занятые порты.",
                                  "Click “Refresh” to re-read the busy ports."), false);

            _lvPorts = new FastListView();
            _lvPorts.Dock = DockStyle.Fill;
            _lvPorts.View = View.Details;
            _lvPorts.CheckBoxes = true;
            _lvPorts.FullRowSelect = true;
            // Порт живёт ровно столько, сколько держащий его процесс: ключ — порт и имя
            // процесса, полка сеансовая. Список перечитывается часто, выбор больше не слетает.
            MemWatch(_lvPorts, PortsScope, false, PortsMemKey);
            _lvPorts.Columns.Add(Tr.S("Порт", "Port"), 90);
            _lvPorts.Columns.Add("PID", 90);
            _lvPorts.Columns.Add(Tr.S("Процесс", "Process"), 340);
            SetupOwnerDraw(_lvPorts);

            tab.Controls.Add(_lvPorts);
            tab.Controls.Add(_lblDev);
            tab.Controls.Add(portsBar);
            tab.Controls.Add(lblPHint);
            tab.Controls.Add(lblP);
            tab.Controls.Add(flow);
            return tab;
        }

        private void AddDevButton(FlowLayoutPanel flow, string title, string[] names)
        {
            Button b = new RoundButton();
            b.Text = title;
            b.Width = 150; b.Height = 36;
            b.Margin = new Padding(0, 0, 8, 8);
            b.Click += delegate { StartDevKill(title, names, null); };
            flow.Controls.Add(b);
            _devButtons.Add(b);
        }

        // Вся работа страницы (обе кнопки портов и десять групповых) идёт через один флаг
        // занятости, погашенные кнопки и одну строку состояния с секундомером. Раньше
        // групповое завершение считалось прямо в обработчике клика, а это гарантированные
        // секунды: WM_CLOSE всем окнам, общее ожидание 2.5 с и добивание не ушедших. Окно
        // на всё это время переставало отвечать, лишние клики копились в очереди сообщений
        // и после разморозки прокручивали цикл завершения ещё раз.
        private Label _lblDev;
        private readonly List<Button> _devButtons = new List<Button>();
        private int _devBusy;
        private System.Windows.Forms.Timer _devTick;
        private DateTime _devStarted;
        private volatile string _devPhase;
        private Button _btnDevStop;
        private volatile bool _devCancel;
        // Что делает движок прямо сейчас: рассылка WM_CLOSE, ожидание закрытия, добивание.
        private volatile string _devStage;

        private void StartDevTicker(string phase)
        {
            _devPhase = phase;
            _devStarted = DateTime.UtcNow;
            if (_devTick == null)
            {
                _devTick = new System.Windows.Forms.Timer();
                _devTick.Interval = 500;
                _devTick.Tick += delegate { DevTick(); };
            }
            _devTick.Start();
            DevTick();
        }

        private void StopDevTicker()
        {
            if (_devTick != null) _devTick.Stop();
        }

        private void DevTick()
        {
            if (_devBusy == 0) { StopDevTicker(); return; }
            // Elapsed() объявлен на вкладке очистки — класс один, формат секундомера общий
            string stage = _devStage;
            _lblDev.Text = _devPhase + "   ·   " + Elapsed(DateTime.UtcNow - _devStarted)
                         + (string.IsNullOrEmpty(stage) ? "" : "   ·   " + stage);
        }

        private void SetDevBusy(bool busy)
        {
            foreach (Button b in _devButtons) b.Enabled = !busy;
        }

        // names != null — группа по именам процессов, иначе завершаются перечисленные pid.
        private void StartDevKill(string title, string[] names, List<int> pids)
        {
            if (_lblDev == null) return;
            if (Interlocked.CompareExchange(ref _devBusy, 1, 0) != 0)
            {
                _lblDev.Text = Tr.S("Идёт другая операция — дождитесь её окончания.",
                                    "Another operation is running — wait for it to finish.");
                return;
            }
            SetDevBusy(true);
            _devCancel = false;
            _devStage = null;
            _btnDevStop.Enabled = true;
            StartDevTicker(Tr.S("Завершаю: ", "Terminating: ") + title);

            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                int matched = pids != null ? pids.Count : 0;
                string err = null;
                Action<string> stage = delegate(string s) { _devStage = s; };
                Func<bool> cancel = delegate { return _devCancel || _closing; };
                try
                {
                    killed = names != null ? _engine.TerminateByNames(names, out freed, out matched, stage, cancel)
                                           : _engine.TerminateMany(pids, out freed, stage, cancel);
                }
                catch (Exception ex) { err = ex.Message; }
                Interlocked.Exchange(ref _devBusy, 0);
                int matchedCopy = matched;
                UiPost(delegate
                {
                    StopDevTicker();
                    SetDevBusy(false);
                    _btnDevStop.Enabled = false;
                    _devStage = null;
                    // Ноль завершённых — это не «готово», а отдельный ответ: процессов нет
                    // либо их не отдала система. Раньше пользователь видел «Завершено: 0».
                    string msg = err != null
                        ? Tr.S("не удалось: ", "failed: ") + err
                        : killed > 0
                            ? Tr.S("завершено процессов: ", "terminated: ") + killed
                              + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(freed)
                              + (_devCancel ? Tr.S("  ·  остановлено, остальные оставлены работать",
                                                   "  ·  stopped, the rest were left running") : "")
                            : _devCancel
                                ? Tr.S("остановлено — ничего не завершено", "stopped — nothing was terminated")
                                : matchedCopy > 0
                                    ? Tr.S("найдено процессов: ", "matching processes: ") + matchedCopy
                                      + Tr.S(", но ни один не завершился — нет прав или процесс не отвечает",
                                             ", but none of them terminated — no rights, or the process is not responding")
                                    : Tr.S("завершать нечего — таких процессов не запущено",
                                           "nothing to terminate — no such processes are running");
                    _lblDev.Text = title + "   ·   " + msg;
                    if (_tray != null && err == null && killed > 0)
                        _tray.ShowBalloonTip(2000, title, msg, ToolTipIcon.Info);
                    RefreshPorts();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Память выбора для списка портов: PID меняется при каждом перезапуске сервера,
        // поэтому ключ — сам порт и имя процесса.
        private const string PortsScope = "dev.port";

        private static string PortsMemKey(ListViewItem it)
        {
            if (!(it.Tag is PortRow)) return null;
            PortRow pr = (PortRow)it.Tag;
            return pr.Port + "/" + (pr.ProcName == null ? "" : pr.ProcName);
        }

        private void RefreshPorts()
        {
            if (_lvPorts == null || _lblDev == null) return;
            // Занята страница — список перечитается сам, когда операция закончится.
            if (Interlocked.CompareExchange(ref _devBusy, 1, 0) != 0) return;
            SetDevBusy(true);
            StartDevTicker(Tr.S("Читаю занятые порты", "Reading busy ports"));

            Thread t = new Thread(delegate()
            {
                List<PortRow> rows = null;
                string err = null;
                try { rows = _engine.DevPortRows(); }
                catch (Exception ex) { err = ex.Message; }
                Interlocked.Exchange(ref _devBusy, 0);
                UiPost(delegate
                {
                    StopDevTicker();
                    SetDevBusy(false);
                    // Сбой чтения таблицы TCP раньше молча оставлял старый список: пользователь
                    // не мог отличить «портов нет» от «не смогли посмотреть».
                    if (rows == null)
                    {
                        _lblDev.Text = Tr.S("Не удалось прочитать список портов: ", "Could not read the port list: ")
                                     + (err != null ? err : Tr.S("неизвестная ошибка", "unknown error"))
                                     + Tr.S("  ·  показан прежний список", "  ·  the previous list is still shown");
                        return;
                    }
                    MemBeginFill();
                    _lvPorts.BeginUpdate();
                    try
                    {
                        _lvPorts.Items.Clear();
                        List<ListViewItem> items = new List<ListViewItem>();
                        foreach (PortRow pr in rows)
                        {
                            ListViewItem it = new ListViewItem(pr.Port.ToString());
                            it.SubItems.Add(pr.Pid.ToString());
                            it.SubItems.Add(pr.ProcName);
                            it.Tag = pr;
                            items.Add(it);
                        }
                        _lvPorts.Items.AddRange(items.ToArray());
                        MemEndFill(_lvPorts, PortsScope, false, PortsMemKey, null);
                    }
                    finally { _lvPorts.EndUpdate(); }
                    AutoFillLastColumnDeferred(_lvPorts);
                    _lblDev.Text = rows.Count > 0
                        ? Tr.S("Занятых dev-портов: ", "Busy dev ports: ") + rows.Count
                          + Tr.S("  ·  отметьте строки и нажмите «Завершить выбранные порты»",
                                 "  ·  tick rows and click “Kill selected ports”")
                        : Tr.S("Ни один из отслеживаемых dev-портов не занят — список портов задаётся в Настройках.",
                               "None of the tracked dev ports is busy — the port list is set in Settings.");
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void KillSelectedPorts()
        {
            List<int> pids = new List<int>();
            foreach (ListViewItem it in _lvPorts.Items)
                if (it.Checked && it.Tag is PortRow) pids.Add(((PortRow)it.Tag).Pid);
            if (pids.Count == 0)
            {
                _lblDev.Text = Tr.S("Не отмечено ни одного порта — поставьте галочки в списке.",
                                    "No ports ticked — tick the rows in the list first.");
                MsgInfo(Tr.S("Не выбрано ни одного порта.", "No ports selected."), "Dev Cleanup");
                return;
            }
            StartDevKill(Tr.S("Порты: ", "Ports: ") + pids.Count, null, pids);
        }
    }
}
