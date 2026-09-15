// SysDeck — страница «Скрипты»: список встроенных скриптов обслуживания, их состояние в Windows и параметры.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Слева — список с галочками (что поставить одной кнопкой), справа — выбранный пункт: описание, состояние задач,
// поля параметров и кнопки. Чтение состояния (Планировщик через COM, файлы, реестр) идёт в фоновом потоке:
// Планировщик на загруженной машине отвечает секундами. Действия — MainForm.ToolkitActions.cs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Toolkit;

namespace SysDeck
{
    public partial class MainForm
    {
        private FastListView _lvTk;
        private Label _lblTkInfo;
        private Button _btnTkInstall, _btnTkRefresh;
        private Panel _tkDetail;
        private FlowLayoutPanel _tkDetailFlow;
        private Label _lblTkResult;
        private Font _fontTkTitle, _fontTkBold;
        private int _tkBusy;
        private bool _tkLoaded, _tkFilling;
        private string _tkSelected;
        // Состояние по id — последнее прочитанное; правки полей, ещё не сохранённые, — отдельно, чтобы перечитывание
        // состояния (после действия или по «Обновить») не стирало то, что человек набрал.
        private readonly Dictionary<string, TkStatus> _tkStatus = new Dictionary<string, TkStatus>();
        private readonly Dictionary<string, Dictionary<string, string>> _tkEdits = new Dictionary<string, Dictionary<string, string>>();
        private readonly Dictionary<string, string> _tkLastResult = new Dictionary<string, string>();

        // ---------- Вкладка: Скрипты ----------
        private Control BuildToolkitTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();
            _btnTkInstall = MkFlowButton(Tr.S("Установить отмеченные", "Install checked"), 220, true);
            _btnTkInstall.Click += delegate { ToolkitInstallChecked(); };
            _btnTkRefresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 120, false);
            _btnTkRefresh.Click += delegate { ToolkitRefresh(); };
            top.Controls.Add(_btnTkInstall);
            top.Controls.Add(_btnTkRefresh);

            Label warn = MkNote(Tr.S("Скрипты встроены в SysDeck: установка копирует их на диск и создаёт задачи Планировщика. Параметры меняются справа, «Сохранить» перерегистрирует задачи.",
                                     "Scripts ship inside SysDeck: installing copies them to disk and creates Task Scheduler tasks. Change parameters on the right; “Save” re-registers the tasks."), true);
            _lblTkInfo = MkNote(Tr.S("Чтение состояния…", "Reading state…"), false);

            SplitContainer split = new SplitContainer();
            split.Dock = DockStyle.Fill;
            split.SplitterWidth = 6;
            // Размер первым — иначе Panel2MinSize пересчитывает SplitterDistance и бросает исключение (см. «Браузеры»).
            split.Size = new Size(1100, 520);
            split.Panel1MinSize = 380;
            split.Panel2MinSize = 360;
            split.SplitterDistance = 640;
            split.Panel1.Padding = new Padding(1);
            split.Panel2.Padding = new Padding(1);

            _lvTk = new FastListView();
            _lvTk.Dock = DockStyle.Fill;
            _lvTk.View = View.Details;
            _lvTk.CheckBoxes = true;
            _lvTk.FullRowSelect = true;
            _lvTk.HideSelection = false;
            _lvTk.MultiSelect = false;
            _lvTk.Columns.Add(Tr.S("Скрипт", "Script"), 290);
            _lvTk.Columns.Add(Tr.S("Состояние", "State"), 180);
            _lvTk.Columns.Add(Tr.S("Когда работает", "When it runs"), 160);
            SetupOwnerDraw(_lvTk);
            _lvTk.ItemChecked += delegate { if (!_tkFilling) ToolkitUpdateInstallButton(); };
            _lvTk.SelectedIndexChanged += delegate
            {
                if (_tkFilling || _lvTk.SelectedItems.Count == 0) return;
                string id = _lvTk.SelectedItems[0].Name;
                if (id == _tkSelected) return;
                _tkSelected = id;
                ToolkitShowDetail();
            };
            split.Panel1.Controls.Add(_lvTk);

            _fontTkTitle = new Font(Font.FontFamily, Font.Size + 2.5F, FontStyle.Bold);
            _fontTkBold = new Font(Font, FontStyle.Bold);
            _tkDetail = new Panel();
            _tkDetail.Dock = DockStyle.Fill;
            _tkDetail.AutoScroll = true;
            // Прокрутка только вертикальная: ширину содержимого подгоняет ToolkitLayoutDetail.
            _tkDetail.HorizontalScroll.Maximum = 0;
            _tkDetail.AutoScrollMinSize = Size.Empty;
            _tkDetail.Padding = new Padding(14, 4, 8, 8);
            _tkDetailFlow = new FlowLayoutPanel();
            _tkDetailFlow.FlowDirection = FlowDirection.TopDown;
            _tkDetailFlow.WrapContents = false;
            _tkDetailFlow.AutoSize = true;
            _tkDetailFlow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _tkDetailFlow.Location = new Point(14, 4);
            _tkDetail.Controls.Add(_tkDetailFlow);
            _tkDetail.Resize += delegate { ToolkitLayoutDetail(); };
            split.Panel2.Controls.Add(_tkDetail);

            tab.Controls.Add(split);
            tab.Controls.Add(_lblTkInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        private void ToolkitEnter()
        {
            if (!_tkLoaded) ToolkitRefresh();
        }

        // ---------- чтение состояния ----------

        // only == null — все пункты; иначе только эти (после действия незачем опрашивать Планировщик об остальных).
        private void ToolkitRefresh() { ToolkitRefresh(null); }

        private void ToolkitRefresh(List<TkItem> only)
        {
            if (Interlocked.CompareExchange(ref _tkBusy, 1, 0) != 0) return;
            ToolkitSetButtons(false);
            BusyTicker tick = new BusyTicker(_lblTkInfo, Tr.S("Чтение состояния скриптов и задач", "Reading scripts and tasks"));
            List<TkItem> items = only ?? TkCatalog.All;
            Thread t = new Thread(delegate()
            {
                List<TkStatus> got = new List<TkStatus>();
                string err = null;
                try
                {
                    TkEnv env = TkEnv.Real();
                    foreach (TkItem it in items)
                    {
                        tick.SetStage(Tr.S("Чтение состояния: ", "Reading state: ") + it.Title);
                        got.Add(TkEngine.Probe(it, env));
                    }
                }
                catch (Exception ex) { err = ex.Message; }
                UiPost(delegate
                {
                    tick.Stop();
                    Interlocked.Exchange(ref _tkBusy, 0);
                    ToolkitSetButtons(true);
                    foreach (TkStatus s in got) _tkStatus[s.Item.Id] = s;
                    bool first = !_tkLoaded;
                    _tkLoaded = true;
                    ToolkitFill(first);
                    _lblTkInfo.Text = err != null ? Tr.S("Прочитать состояние не удалось: ", "Failed to read the state: ") + err : ToolkitSummary();
                });
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private string ToolkitSummary()
        {
            int ok = 0, off = 0, need = 0;
            foreach (TkStatus s in _tkStatus.Values)
            {
                if (s.State == TkState.Ok || s.State == TkState.Differs) ok++;
                else if (s.State == TkState.Disabled) off++;
                else if (s.State == TkState.Partial) need++;
            }
            string text = Tr.S("Установлено: ", "Installed: ") + ok.ToString(CultureInfo.InvariantCulture);
            if (off > 0) text += Tr.S(" · выключено: ", " · disabled: ") + off.ToString(CultureInfo.InvariantCulture);
            if (need > 0) text += Tr.S(" · требуют внимания: ", " · need attention: ") + need.ToString(CultureInfo.InvariantCulture);
            return text + Tr.S(" из ", " of ") + TkCatalog.All.Count.ToString(CultureInfo.InvariantCulture)
                 + Tr.S(". Галочки — что поставить или починить одной кнопкой.", ". Checkboxes mark what one button installs or repairs.");
        }

        // Первое заполнение ставит галочки на то, что стоит поставить на ЭТОЙ машине; дальше галочки — решение человека.
        private void ToolkitFill(bool setChecks)
        {
            _tkFilling = true;
            _lvTk.BeginUpdate();
            try
            {
                foreach (TkItem it in TkCatalog.All)
                {
                    TkStatus s;
                    if (!_tkStatus.TryGetValue(it.Id, out s)) continue;
                    ListViewItem row = _lvTk.Items[it.Id];
                    if (row == null)
                    {
                        row = new ListViewItem(it.Title);
                        row.Name = it.Id;
                        row.UseItemStyleForSubItems = false;
                        row.SubItems.Add("");
                        row.SubItems.Add("");
                        _lvTk.Items.Add(row);
                    }
                    row.SubItems[1].Text = ToolkitStateText(s);
                    row.SubItems[1].ForeColor = ToolkitStateColor(s);
                    row.SubItems[2].Text = ToolkitWhen(it, s.Values);
                    row.ToolTipText = it.Title + (s.Note != null ? "\n" + s.Note : "");
                    if (setChecks) row.Checked = s.Relevant && (s.State == TkState.NotInstalled || s.State == TkState.Partial || s.State == TkState.Differs);
                }
            }
            finally { _lvTk.EndUpdate(); _tkFilling = false; }
            if (_tkSelected == null && _lvTk.Items.Count > 0) _lvTk.Items[0].Selected = true;
            else ToolkitShowDetail();
            ToolkitUpdateInstallButton();
            AutoFillLastColumnDeferred(_lvTk);
        }

        private void ToolkitUpdateInstallButton()
        {
            int n = _lvTk.CheckedItems.Count;
            _btnTkInstall.Text = n > 0 ? Tr.S("Установить отмеченные (", "Install checked (") + n.ToString(CultureInfo.InvariantCulture) + ")"
                                       : Tr.S("Установить отмеченные", "Install checked");
            _btnTkInstall.Width = Math.Max(220, Unscaled(TextRenderer.MeasureText(_btnTkInstall.Text, Font).Width) + 28);
            _btnTkInstall.Enabled = n > 0 && _tkBusy == 0;
        }

        private void ToolkitSetButtons(bool on)
        {
            _btnTkRefresh.Enabled = on;
            _btnTkInstall.Enabled = on && _lvTk.CheckedItems.Count > 0;
            if (_tkDetailFlow != null)
                foreach (Control c in _tkDetailFlow.Controls)
                    if (c is FlowLayoutPanel && c.Name == "tkbuttons") foreach (Control b in c.Controls) b.Enabled = on;
        }

        private static bool ToolkitHasTasks(TkStatus s)
        {
            foreach (TkTaskState t in s.Tasks) if (t.Exists) return true;
            return false;
        }

        private static string ToolkitStateText(TkStatus s)
        {
            switch (s.State)
            {
                case TkState.NotInstalled: return Tr.S("не установлен", "not installed");
                case TkState.Disabled: return Tr.S("выключен", "disabled");
                case TkState.Differs: return Tr.S("файлы изменены", "files changed");
                case TkState.Partial: return Tr.S("установлен не полностью", "partly installed");
                default: return s.Item.Tasks.Length > 0 && ToolkitHasTasks(s) ? Tr.S("работает", "running") : Tr.S("установлен", "installed");
            }
        }

        private Color ToolkitStateColor(TkStatus s)
        {
            bool dark = _theme != null && _theme.Dark;
            switch (s.State)
            {
                case TkState.Ok: return dark ? Color.FromArgb(110, 200, 130) : Color.FromArgb(24, 128, 56);
                case TkState.Partial:
                case TkState.Differs: return dark ? Color.FromArgb(235, 180, 90) : Color.FromArgb(170, 100, 0);
                default: return _theme != null ? _theme.Subtle : SystemColors.GrayText;
            }
        }

        // Расписание словами — из параметров, как они сейчас в Windows.
        private static string ToolkitWhen(TkItem it, Dictionary<string, string> v)
        {
            switch (it.Id)
            {
                case "proc-reaper":
                case "vscode-watcher-reaper":
                    return Tr.S("каждые ", "every ") + ToolkitMinutes(v["minutes"]) + Tr.S(" с ", " from ") + v["start"];
                case "agent-governor": return Tr.S("каждые ", "every ") + ToolkitMinutes(v["minutes"]) + Tr.S(" и при входе", " and at sign-in");
                case "freeze-canary": return Tr.S("всё время после входа", "always, from sign-in");
                case "port-watch": return Tr.S("по событию Tcpip 4231", "on Tcpip event 4231");
                case "audio-fix": return Tr.S("загрузка, выход из сна", "boot, resume from sleep");
                case "mpo-fix": return v["reapply"] == "true" ? Tr.S("параметр + при входе", "value + at sign-in") : Tr.S("параметр реестра", "registry value");
                case "docker-maint": return v["auto"] == "true" ? Tr.S("ежедневно в ", "daily at ") + v["time"] : Tr.S("вручную", "by hand");
                case "wslconfig": return Tr.S("при запуске WSL", "when WSL starts");
                case "claude-hook": return Tr.S("конец сессии Claude Code", "Claude Code session end");
                default: return Tr.S("вручную", "by hand");
            }
        }

        private static string ToolkitMinutes(string m)
        {
            int n;
            if (!int.TryParse(m, NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) return m;
            if (n >= 60 && n % 60 == 0) return (n / 60).ToString(CultureInfo.InvariantCulture) + Tr.S(" ч", " h");
            return n.ToString(CultureInfo.InvariantCulture) + Tr.S(" мин", " min");
        }

        // Значения для установки: несохранённые правки поверх прочитанного из Windows.
        private Dictionary<string, string> ToolkitValues(TkItem it)
        {
            Dictionary<string, string> v = new Dictionary<string, string>();
            TkStatus s;
            if (_tkStatus.TryGetValue(it.Id, out s)) foreach (KeyValuePair<string, string> kv in s.Values) v[kv.Key] = kv.Value;
            Dictionary<string, string> e;
            if (_tkEdits.TryGetValue(it.Id, out e)) foreach (KeyValuePair<string, string> kv in e) v[kv.Key] = kv.Value;
            return it.Clean(v);
        }

        private void ToolkitEdit(TkItem it, string key, string value)
        {
            Dictionary<string, string> e;
            if (!_tkEdits.TryGetValue(it.Id, out e)) { e = new Dictionary<string, string>(); _tkEdits[it.Id] = e; }
            e[key] = value;
        }

        // ---------- правая панель ----------

        private void ToolkitShowDetail()
        {
            TkItem it = _tkSelected == null ? null : TkCatalog.Find(_tkSelected);
            TkStatus s;
            if (it == null || !_tkStatus.TryGetValue(it.Id, out s)) return;

            _tkDetail.SuspendLayout();
            try
            {
                int scroll = _tkDetail.VerticalScroll.Value;
                List<Control> old = new List<Control>();
                foreach (Control c in _tkDetailFlow.Controls) old.Add(c);
                _tkDetailFlow.Controls.Clear();
                foreach (Control c in old) c.Dispose();

                Label title = ToolkitLabel(it.Title, false);
                title.Font = _fontTkTitle;
                title.Margin = new Padding(0, 4, 0, 4);
                _tkDetailFlow.Controls.Add(title);

                Label state = ToolkitLabel(ToolkitStateText(s) + " · " + ToolkitWhen(it, s.Values), false);
                state.Font = _fontTkBold;
                state.Name = "tkstate";
                state.Tag = ToolkitStateColor(s);
                _tkDetailFlow.Controls.Add(state);

                foreach (string line in ToolkitFacts(it, s)) _tkDetailFlow.Controls.Add(ToolkitLabel(line, true));
                if (s.Note != null)
                {
                    Label note = ToolkitLabel(s.Note, false);
                    note.Name = "tknote";
                    note.Margin = new Padding(0, 6, 0, 0);
                    _tkDetailFlow.Controls.Add(note);
                }

                Label why = ToolkitLabel(it.Why, false);
                why.Margin = new Padding(0, 10, 0, 6);
                _tkDetailFlow.Controls.Add(why);

                if (it.Params.Count > 0) _tkDetailFlow.Controls.Add(ToolkitParams(it));
                _tkDetailFlow.Controls.Add(ToolkitButtons(it, s));

                string last;
                _lblTkResult = ToolkitLabel(_tkLastResult.TryGetValue(it.Id, out last) ? last : "", true);
                _lblTkResult.Name = "tkresult";
                _lblTkResult.Margin = new Padding(0, 4, 0, 8);
                _tkDetailFlow.Controls.Add(_lblTkResult);

                ApplyThemeTo(_tkDetail);
                foreach (Control c in _tkDetailFlow.Controls)
                {
                    if (c.Name == "tkstate" && c.Tag is Color) c.ForeColor = (Color)c.Tag;
                    if (c.Name == "tknote") c.ForeColor = ToolkitStateColor(new TkStatus { State = TkState.Partial });
                }
                ToolkitLayoutDetail();
                if (_tkBusy != 0) ToolkitSetButtons(false);
                if (scroll > 0 && _tkSelected == it.Id) _tkDetail.VerticalScroll.Value = Math.Min(scroll, _tkDetail.VerticalScroll.Maximum);
            }
            finally { _tkDetail.ResumeLayout(true); }
        }

        private Label ToolkitLabel(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 2, 0, 2);
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            return l;
        }

        // Где лежит, какие задачи и когда запускались — одной строкой на факт.
        private static List<string> ToolkitFacts(TkItem it, TkStatus s)
        {
            List<string> lines = new List<string>();
            if (s.Dir != null) lines.Add(Tr.S("Папка: ", "Folder: ") + s.Dir);
            if (it.Id == "wslconfig") lines.Add(Tr.S("Файл: ", "File: ") + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig"));
            foreach (TkTaskState t in s.Tasks)
            {
                string line = Tr.S("Задача ", "Task ") + t.Name + ": ";
                if (!t.Exists) line += s.WantedTasks.Contains(t.Name) ? Tr.S("нет", "missing") : Tr.S("не нужна при этих параметрах", "not needed with these parameters");
                else if (t.Denied) line += Tr.S("есть, от имени SYSTEM — подробности видны только администратору", "present, runs as SYSTEM — details visible to administrators only");
                else
                {
                    line += t.Enabled ? Tr.S("включена", "enabled") : Tr.S("выключена", "disabled");
                    if (t.State == 4) line += Tr.S(", выполняется", ", running");
                    if (t.LastRun != DateTime.MinValue)
                        line += Tr.S(", последний запуск ", ", last run ") + t.LastRun.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                              + (t.LastResult == 0 || t.LastResult == 0x41301 ? "" : Tr.S(" (код ", " (code ") + "0x" + t.LastResult.ToString("X", CultureInfo.InvariantCulture) + ")");
                    if (t.NextRun != DateTime.MinValue) line += Tr.S(", следующий ", ", next ") + t.NextRun.ToString("dd.MM HH:mm", CultureInfo.InvariantCulture);
                }
                if (t.Error != null) line += " — " + t.Error;
                lines.Add(line);
            }
            if (s.FilesMissing > 0 && s.State != TkState.NotInstalled && s.Note == null && (s.WantedTasks.Count > 0 || it.Place != TkPlace.Protected))
                lines.Add(Tr.S("Не хватает файлов: ", "Missing files: ") + s.FilesMissing.ToString(CultureInfo.InvariantCulture)
                          + Tr.S(" из ", " of ") + s.FilesTotal.ToString(CultureInfo.InvariantCulture) + Tr.S(" — «Сохранить» доложит их", " — “Save” adds them"));
            if (s.FilesDiffer > 0)
                lines.Add(Tr.S("Файлов отличается от встроенных: ", "Files differing from the built-in ones: ") + s.FilesDiffer.ToString(CultureInfo.InvariantCulture)
                          + Tr.S(" — «Сохранить» вернёт встроенные", " — “Save” restores the built-in ones"));
            if (it.Admin) lines.Add(Tr.S("Нужны права администратора: Windows спросит при нажатии.", "Needs administrator rights: Windows asks when you press a button."));
            return lines;
        }

        // Поля параметров: подпись слева, поле справа, подсказка под полем.
        private Control ToolkitParams(TkItem it)
        {
            Dictionary<string, string> v = ToolkitValues(it);
            TableLayoutPanel table = new TableLayoutPanel();
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.ColumnCount = 2;
            table.Margin = new Padding(0, 4, 0, 8);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            foreach (TkParam p in it.Params)
            {
                TkParam param = p;
                Control field;
                Label label = null;
                switch (p.Kind)
                {
                    case TkKind.Bool:
                        {
                            CheckBox cb = new CheckBox();
                            cb.Text = p.Label;
                            cb.AutoSize = true;
                            cb.Checked = v[p.Key] == "true";
                            cb.CheckedChanged += delegate { ToolkitEdit(it, param.Key, cb.Checked ? "true" : "false"); };
                            field = cb;
                            break;
                        }
                    case TkKind.Minutes:
                    case TkKind.Int:
                        {
                            NumericUpDown n = new NumericUpDown();
                            n.Minimum = p.Min;
                            n.Maximum = p.Max;
                            n.Width = 110;
                            n.Value = Math.Max(p.Min, Math.Min(p.Max, int.Parse(v[p.Key], CultureInfo.InvariantCulture)));
                            n.ValueChanged += delegate { ToolkitEdit(it, param.Key, ((int)n.Value).ToString(CultureInfo.InvariantCulture)); };
                            field = n;
                            break;
                        }
                    case TkKind.Choice:
                        {
                            RoundComboBox cmb = new RoundComboBox();
                            cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                            cmb.Width = 300;
                            for (int i = 0; i < p.Choices.Length; i++) cmb.Items.Add(Tr.S(p.ChoiceRu[i], p.ChoiceEn[i]));
                            cmb.SelectedIndex = Math.Max(0, Array.IndexOf(p.Choices, v[p.Key]));
                            cmb.SelectedIndexChanged += delegate { if (cmb.SelectedIndex >= 0) ToolkitEdit(it, param.Key, param.Choices[cmb.SelectedIndex]); };
                            field = cmb;
                            break;
                        }
                    default:
                        {
                            TextBox tb = new TextBox();
                            tb.Text = v[p.Key];
                            tb.Width = p.Kind == TkKind.Time ? 80 : 280;
                            if (p.Kind == TkKind.Time) tb.MaxLength = 5;
                            tb.TextChanged += delegate { ToolkitEdit(it, param.Key, tb.Text); };
                            // Неверное время видно сразу при уходе из поля, а не молчаливой подменой при сохранении.
                            tb.Leave += delegate { string norm = param.Normalize(tb.Text); if (norm != tb.Text) tb.Text = norm; };
                            field = tb;
                            break;
                        }
                }
                int row = table.RowCount;
                table.RowCount = row + 1;
                if (p.Kind != TkKind.Bool)
                {
                    label = ToolkitLabel(p.Label, false);
                    label.Margin = new Padding(0, 6, 12, 2);
                    table.Controls.Add(label, 0, row);
                    field.Margin = new Padding(0, 2, 0, 2);
                    table.Controls.Add(field, 1, row);
                }
                else
                {
                    field.Margin = new Padding(0, 4, 0, 2);
                    table.Controls.Add(field, 0, row);
                    table.SetColumnSpan(field, 2);
                }
                if (p.Hint != null)
                {
                    Label hint = ToolkitLabel(p.Hint, true);
                    hint.Name = "tkhint";
                    hint.Margin = new Padding(p.Kind == TkKind.Bool ? 20 : 0, 0, 0, 4);
                    table.RowCount = row + 2;
                    table.Controls.Add(hint, p.Kind == TkKind.Bool ? 0 : 1, row + 1);
                    if (p.Kind == TkKind.Bool) table.SetColumnSpan(hint, 2);
                }
            }
            return table;
        }

        // Подписи и панель кнопок переносятся по ширине правой панели.
        private void ToolkitLayoutDetail()
        {
            if (_tkDetail == null || _tkDetailFlow == null) return;
            int w = Math.Max(Px(240), _tkDetail.ClientSize.Width - _tkDetail.Padding.Horizontal - Px(8));
            foreach (Control c in _tkDetailFlow.Controls)
            {
                if (c is Label) c.MaximumSize = new Size(w, 0);
                else if (c is FlowLayoutPanel) { c.MaximumSize = new Size(w, 0); c.MinimumSize = new Size(w, 0); }
                else if (c is TableLayoutPanel)
                {
                    // Вторая колонка — всё, что правее самой длинной подписи: поля и подсказки в неё укладываются.
                    TableLayoutPanel table = (TableLayoutPanel)c;
                    // На узкой панели подписи переносятся (не шире 45 %), чтобы полю осталось место.
                    int labelMax = Math.Max(Px(110), w * 45 / 100), labelW = 0;
                    foreach (Control h in table.Controls)
                        if (h is Label && h.Name != "tkhint" && table.GetColumn(h) == 0)
                        {
                            h.MaximumSize = new Size(labelMax - h.Margin.Horizontal, 0);
                            labelW = Math.Max(labelW, Math.Min(labelMax, h.GetPreferredSize(new Size(labelMax - h.Margin.Horizontal, 0)).Width + h.Margin.Horizontal));
                        }
                    int rest = Math.Max(Px(90), w - labelW - Px(6));
                    foreach (Control h in table.Controls)
                    {
                        if (h.Name == "tkhint") h.MaximumSize = new Size(table.GetColumnSpan(h) == 2 ? w - h.Margin.Left : rest, 0);
                        else if (h is TextBox || h is ComboBox) h.Width = Math.Min(Px(h is TextBox && ((TextBox)h).MaxLength == 5 ? 80 : 300), rest - Px(4));
                        else if (h is CheckBox) h.MaximumSize = new Size(w, 0);
                    }
                }
            }
        }
    }
}
