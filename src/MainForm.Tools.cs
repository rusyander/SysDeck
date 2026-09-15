// SysDeck — вкладка «Инструменты»: быстрые исправления, защита, штатные средства Windows
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        private RichTextBox _rtbTools;
        private Label _lblToolsInfo, _lblToolsLog;
        private Button _btnToolsStop, _btnHiber;
        private ToolTip _toolTips;
        private int _toolsBusy;
        private volatile bool _toolsCancel;
        private readonly Dictionary<string, Button> _toolButtons = new Dictionary<string, Button>();
        private readonly List<KeyValuePair<Label, FlowLayoutPanel>> _toolSections = new List<KeyValuePair<Label, FlowLayoutPanel>>();
        private TextBox _txtToolsFind;
        private Panel _toolsBody;
        private string _toolsInfoDefault;
        // Секундомер страницы: подпись «Выполняется: …» ставилась один раз и висела неподвижно
        // всё время работы SFC/DISM/chkdsk — до часа. Теперь к ней раз в полсекунды дописываются
        // прошедшее время и последняя строка вывода: видно, что работа жива.
        private System.Windows.Forms.Timer _toolsTick;
        private DateTime _toolsStarted;
        private string _toolsPhase, _toolsTitle;
        private string _toolsLastLine;

        // ---------- Вкладка: Инструменты ----------
        private Control BuildToolsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);
            _toolTips = new ToolTip();
            _toolTips.AutoPopDelay = 20000;
            _toolTips.InitialDelay = 400;

            // Журнал снизу, кнопки сверху в прокручиваемом теле: Fill добавляется первым, Bottom —
            // после, подписи Top — последними (docking в обратном порядке добавления).
            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;

            Panel logPanel = new Panel();
            logPanel.Dock = DockStyle.Bottom;
            logPanel.Height = 210;

            FlowLayoutPanel logBar = MkToolbar();
            _lblToolsLog = MkFlowLabel(Tr.S("Журнал выполнения", "Execution log"), false);
            _btnToolsStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnToolsStop.Enabled = false;
            _btnToolsStop.Click += delegate { StopRunningTool(); };
            Button btnClear = MkFlowButton(Tr.S("Очистить журнал", "Clear log"), 150, false);
            btnClear.Click += delegate { _rtbTools.Clear(); };
            logBar.Controls.Add(_lblToolsLog);
            logBar.Controls.Add(_btnToolsStop);
            logBar.Controls.Add(btnClear);

            _rtbTools = new RichTextBox();
            _rtbTools.Dock = DockStyle.Fill;
            _rtbTools.ReadOnly = true;
            _rtbTools.WordWrap = false;
            _rtbTools.BorderStyle = BorderStyle.FixedSingle;
            _rtbTools.Font = new Font("Consolas", 9.5F);
            _rtbTools.Text = Tr.S("Здесь появляется вывод запущенного исправления.", "Output of the running fix appears here.");
            Panel outBox = MkBox(_rtbTools, new Padding(8, 6, 4, 6));
            outBox.Dock = DockStyle.Fill;
            logPanel.Controls.Add(outBox);
            logPanel.Controls.Add(logBar);

            _lblToolsInfo = MkNote(Tr.S("Наведите на кнопку — здесь появится описание. Исправления, меняющие систему, сначала спрашивают.",
                                        "Hover a button to see its description here. Fixes that change the system ask first."), false);
            _toolsInfoDefault = _lblToolsInfo.Text;
            Label warn = MkNote(Tr.S("Быстрые исправления — штатные средства Windows одной кнопкой; долгие (SFC, DISM, проверка Защитника) можно остановить.",
                                     "Quick fixes are built-in Windows tools behind one button; long ones (SFC, DISM, Defender scan) can be stopped."), true);

            // Поиск по 45 кнопкам: подстрока названия или описания, Esc очищает.
            FlowLayoutPanel findBar = MkToolbar();
            findBar.Padding = new Padding(0, 2, 0, 0);
            Label findLbl = MkFlowLabel(Tr.S("Поиск:", "Find:"), false);
            findLbl.Margin = new Padding(0, 8, 8, 0);
            _txtToolsFind = new TextBox();
            _txtToolsFind.AutoSize = false;     // иначе однострочное поле не растягивается в обёртке
            Panel findBox = MkBox(_txtToolsFind, new Padding(6, 4, 6, 4));
            findBox.Width = 300; findBox.Height = 30; findBox.Margin = new Padding(0, 2, 12, 6);
            Label findHint = MkFlowLabel(Tr.S("по названию и описанию; Esc — очистить", "by title and description; Esc clears"), true);
            findHint.Margin = new Padding(0, 9, 12, 0);
            _txtToolsFind.TextChanged += delegate { FilterTools(); };
            _txtToolsFind.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { _txtToolsFind.Text = ""; e.SuppressKeyPress = true; e.Handled = true; }
            };
            findBar.Controls.Add(findLbl);
            findBar.Controls.Add(findBox);
            findBar.Controls.Add(findHint);
            _toolsBody = body;

            List<ToolItem> all = Engine.ToolCatalog();
            AddToolSection(body, Tr.S("Инструменты Windows", "Windows tools"), all, Engine.ToolsOpen);
            AddToolSection(body, Tr.S("Защита", "Protection"), all, Engine.ToolsProtect);
            AddToolSection(body, Tr.S("Быстрые исправления", "Quick fixes"), all, Engine.ToolsFix);

            tab.Controls.Add(body);
            tab.Controls.Add(logPanel);
            tab.Controls.Add(findBar);
            tab.Controls.Add(_lblToolsInfo);
            tab.Controls.Add(warn);
            return tab;
        }

        // Секции добавляются в обратном порядке (Dock Top): вызывающий перечисляет их снизу вверх.
        private void AddToolSection(Panel body, string title, List<ToolItem> all, string group)
        {
            FlowLayoutPanel flow = MkToolbar();
            flow.Padding = new Padding(0, 4, 0, 6);
            foreach (ToolItem t in all)
            {
                if (t.Group != group) continue;
                Button b = MkFlowButton(t.Title, 200, false);
                b.Tag = t;
                b.Click += delegate { RunTool(t); };
                b.MouseEnter += delegate { _lblToolsInfo.Text = t.Title + " — " + t.Desc; };
                _toolTips.SetToolTip(b, t.Desc);
                if (t.Id == "hiber") _btnHiber = b;
                _toolButtons[t.Id] = b;
                flow.Controls.Add(b);
            }
            Label head = new Label();
            head.Text = title;
            head.Name = "section";
            head.Dock = DockStyle.Top;
            head.Height = 30;
            head.TextAlign = ContentAlignment.BottomLeft;
            head.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            body.Controls.Add(flow);
            body.Controls.Add(head);
            _toolSections.Add(new KeyValuePair<Label, FlowLayoutPanel>(head, flow));
        }

        // Фильтр кнопок по строке поиска: секция без совпадений скрывается вместе с заголовком.
        private void FilterTools()
        {
            if (_toolsBody == null) return;
            string q = (_txtToolsFind.Text ?? "").Trim();
            int total = 0;
            _toolsBody.SuspendLayout();
            try
            {
                foreach (KeyValuePair<Label, FlowLayoutPanel> sec in _toolSections)
                {
                    int shown = 0;
                    foreach (Control c in sec.Value.Controls)
                    {
                        ToolItem t = c.Tag as ToolItem;
                        bool ok = q.Length == 0 || t == null
                                  || t.Title.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0
                                  || (t.Desc != null && t.Desc.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0);
                        c.Visible = ok;
                        if (ok) shown++;
                    }
                    sec.Value.Visible = shown > 0;
                    sec.Key.Visible = shown > 0;
                    total += shown;
                }
            }
            finally { _toolsBody.ResumeLayout(); }
            _toolsBody.AutoScrollPosition = new Point(0, 0);
            if (q.Length == 0) _lblToolsInfo.Text = _toolsInfoDefault;
            else if (total == 0) _lblToolsInfo.Text = Tr.S("Ничего не найдено: ", "Nothing found: ") + q;
            else _lblToolsInfo.Text = Tr.S("Найдено: ", "Found: ") + Tr.N(total, "инструмент", "инструмента", "инструментов", "tool", "tools");
        }

        private void RefreshToolsState()
        {
            if (_btnHiber == null) return;
            long size;
            bool on = Engine.HibernationEnabled(out size);
            _btnHiber.Text = on
                ? Tr.S("Отключить гибернацию", "Turn hibernation off") + (size > 0 ? " (" + Engine.FormatBytes(size) + ")" : "")
                : Tr.S("Включить гибернацию", "Turn hibernation on");
            _btnHiber.Width = Math.Max(200, Unscaled(TextRenderer.MeasureText(_btnHiber.Text, Font).Width) + 28);
        }

        private void RunToolById(string id)
        {
            ToolItem t = Engine.FindTool(id);
            if (t != null) RunTool(t);
        }

        private bool _toolsLogFresh = true;   // в журнале ещё подсказка-заглушка — первая строка её заменяет

        private void ToolsLog(string line)
        {
            if (_closing || _rtbTools == null) return;
            if (_toolsLogFresh) { _rtbTools.Clear(); _toolsLogFresh = false; }
            _rtbTools.AppendText(line + "\r\n");
            _rtbTools.SelectionStart = _rtbTools.TextLength;
            _rtbTools.ScrollToCaret();
        }

        // Хвост длинной строки вывода в однострочную подпись не влезает и распирает раскладку.
        private static string ToolsShort(string s)
        {
            string t = (s ?? "").Trim();
            return t.Length > 90 ? t.Substring(0, 90) + "…" : t;
        }

        // «Остановить» раньше только поднимала флаг: кнопка оставалась нажимаемой, подпись —
        // прежней, а подтверждение приходило строкой в журнал спустя четверть секунды.
        private void StopRunningTool()
        {
            if (_toolsBusy == 0) return;
            _toolsCancel = true;
            _btnToolsStop.Enabled = false;
            _toolsPhase = Tr.S("Останавливаю: ", "Stopping: ") + _toolsTitle;
            ToolsLog(Tr.S("=== запрошена остановка, дочерний процесс завершается", "=== stop requested, the child process is being terminated"));
        }

        private void StartToolsTicker(string phase)
        {
            _toolsPhase = phase;
            _toolsLastLine = null;
            _toolsStarted = DateTime.UtcNow;
            if (_toolsTick == null)
            {
                _toolsTick = new System.Windows.Forms.Timer();
                _toolsTick.Interval = 500;
                _toolsTick.Tick += delegate { ToolsTick(); };
            }
            _toolsTick.Start();
            ToolsTick();
        }

        private void StopToolsTicker()
        {
            if (_toolsTick != null) _toolsTick.Stop();
        }

        private void ToolsTick()
        {
            if (_toolsBusy == 0) { StopToolsTicker(); return; }
            _lblToolsInfo.Text = _toolsPhase + "   ·   " + Elapsed(DateTime.UtcNow - _toolsStarted)
                + (string.IsNullOrEmpty(_toolsLastLine) ? "" : "   ·   " + _toolsLastLine);
        }

        // .msc, .cpl и ms-settings: уходят через ShellExecuteEx в холодный старт MMC или
        // «Параметров» — на UI-потоке это от сотен миллисекунд до нескольких секунд мёртвого
        // окна. Запуск ушёл в фон, а до ответа виден курсор ожидания и строка «Запускаю: …».
        private void OpenToolAsync(ToolItem t)
        {
            _lblToolsInfo.Text = Tr.S("Запускаю: ", "Starting: ") + t.Title + "…";
            Cursor = Cursors.AppStarting;
            Thread th = new Thread(delegate()
            {
                string err;
                try { err = Engine.ToolOpen(t); }
                catch (Exception ex) { err = ex.Message; }
                string errCopy = err;
                UiPost(delegate
                {
                    Cursor = Cursors.Default;
                    if (errCopy != null)
                    {
                        _lblToolsInfo.Text = t.Title + ": " + errCopy;
                        MsgError(t.Title + ": " + errCopy);
                    }
                    else _lblToolsInfo.Text = Tr.S("Запущено: ", "Started: ") + t.Title;
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private void RunTool(ToolItem t)
        {
            if (t.Group == Engine.ToolsOpen || (t.Open != null && t.Id != "wucheck"))
            {
                OpenToolAsync(t);
                return;
            }
            if (t.Id == "clipboard")
            {
                try { Clipboard.Clear(); _lblToolsInfo.Text = Tr.S("Буфер обмена очищен.", "Clipboard cleared."); }
                catch (Exception ex) { MsgError(ex.Message); }
                return;
            }
            if (Interlocked.CompareExchange(ref _toolsBusy, 1, 0) != 0)
            {
                _lblToolsInfo.Text = Tr.S("Дождитесь завершения текущего исправления или остановите его.", "Wait for the current fix to finish or stop it.");
                return;
            }
            // Раньше здесь предлагалось перезапустить ВСЁ приложение от администратора ради
            // одного исправления. Теперь права поднимаются под саму операцию: окно остаётся
            // работать с обычными правами, а инструмент выполняет отдельный процесс-помощник.
            // Окно UAC покажет сама Windows — предупреждаем о нём, чтобы оно не выглядело
            // выскочившим ниоткуда.
            if (t.Admin && !Elevated)
            {
                if (!MsgAsk(t.Title + Tr.S(": нужны права администратора. Windows спросит подтверждение — продолжить?",
                                           ": administrator rights are required. Windows will ask for confirmation — continue?"),
                            Tr.S("Инструменты", "Tools")))
                {
                    Interlocked.Exchange(ref _toolsBusy, 0);
                    _lblToolsInfo.Text = t.Title + Tr.S(": пропущено, нужны права администратора.", ": skipped, administrator rights are required.");
                    return;
                }
            }
            if (t.Confirm)
            {
                string q = t.ConfirmText;
                if (t.Id == "hiber")
                {
                    long size;
                    bool on = Engine.HibernationEnabled(out size);
                    q = on ? Tr.S("Выключить гибернацию? Файл hiberfil.sys (", "Turn hibernation off? hiberfil.sys (") + Engine.FormatBytes(size)
                             + Tr.S(") будет удалён, режим гибернации и быстрый запуск станут недоступны. Включить обратно можно этой же кнопкой.",
                                    ") will be deleted; hibernation and fast startup become unavailable. The same button turns it back on.")
                           : Tr.S("Включить гибернацию? Windows снова создаст hiberfil.sys на системном диске.", "Turn hibernation on? Windows will recreate hiberfil.sys on the system drive.");
                }
                if (!MsgAsk(q, t.Title))
                {
                    Interlocked.Exchange(ref _toolsBusy, 0);
                    _lblToolsInfo.Text = t.Title + Tr.S(": отменено, ничего не изменено.", ": cancelled, nothing was changed.");
                    return;
                }
            }

            _toolsCancel = false;
            _toolsTitle = t.Title;
            // Все долгие инструменты теперь останавливаются по-настоящему: отмена доходит и до
            // PowerShell в «Создать точку восстановления», и до пауз в перезапуске Проводника.
            bool canStop = t.Long;
            _btnToolsStop.Enabled = canStop;
            ToolsLog("=== " + t.Title + "  [" + DateTime.Now.ToString("HH:mm:ss") + "]");
            StartToolsTicker(Tr.S("Выполняется: ", "Running: ") + t.Title
                + (t.Long ? Tr.S(" — минуты, можно остановить", " — minutes, can be stopped") : ""));
            // Для долгих — «стрелка с песочными часами»: окно отвечает, работа идёт в фоне.
            // Сплошные песочные часы часами подряд читались как зависание.
            Cursor = t.Long ? Cursors.AppStarting : Cursors.WaitCursor;
            string op = t.Confirm || t.Long ? t.Title : null;
            if (op != null) BeginWrite(op);
            Thread th = new Thread(delegate()
            {
                bool ok = false; string err = null;
                try
                {
                    ok = ToolRunMaybeElevated(t,
                        delegate(string line) { UiPost(delegate { ToolsLog(line); _toolsLastLine = ToolsShort(line); }); },
                        delegate { return _toolsCancel || _closing; });
                }
                catch (Exception ex) { err = ex.Message; }
                finally { if (op != null) EndWrite(op); }
                bool okCopy = ok; string errCopy = err; bool stopped = _toolsCancel;
                UiPost(delegate
                {
                    // Флаг занятости снимаем здесь, а не в потоке: снятый раньше, он пускал
                    // следующий запуск, которому этот же обработчик тут же гасил «Остановить»
                    // и затирал строку состояния результатом предыдущего.
                    Interlocked.Exchange(ref _toolsBusy, 0);
                    StopToolsTicker();
                    Cursor = Cursors.Default;
                    _btnToolsStop.Enabled = false;
                    string res = errCopy != null ? Tr.S("ошибка: ", "error: ") + errCopy
                               : stopped ? Tr.S("остановлено", "stopped")
                               : okCopy ? Tr.S("выполнено", "done") : Tr.S("завершилось с ошибкой — см. журнал", "finished with an error — see the log");
                    string took = Tr.S("   ·   заняло ", "   ·   took ") + Elapsed(DateTime.UtcNow - _toolsStarted);
                    ToolsLog("=== " + t.Title + ": " + res + took);
                    _lblToolsInfo.Text = t.Title + ": " + res + took;
                    RefreshToolsState();
                    if (_tray != null && t.Long) _tray.ShowBalloonTip(3000, Tr.S("Инструменты", "Tools"), t.Title + ": " + res, ToolTipIcon.Info);
                });
            });
            th.IsBackground = true;
            th.Start();
        }
    }
}
