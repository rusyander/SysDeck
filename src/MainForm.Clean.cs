// SysDeck — вкладка «Очистка диска»: анализ, состав категории, удаление, winapp2
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
        // ---------- Вкладка: Очистка диска ----------
        private Control BuildCleanTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            Button btnAnalyze = MkFlowButton(Tr.S("Анализировать", "Analyze"), 150, true);
            btnAnalyze.Click += delegate { DoAnalyzeDisk(); };
            Button btnClean = MkFlowButton(Tr.S("Удалить выбранное", "Delete selected"), 180, true);
            btnClean.Click += delegate { DoCleanDisk(); };
            _btnCleanCancel = MkFlowButton(Tr.S("Стоп", "Stop"), 80, false);
            _btnCleanCancel.Enabled = false;
            _btnCleanCancel.Click += delegate { CancelDisk(); };
            Button btnAll = MkFlowButton(Tr.S("Все", "All"), 70, false);
            btnAll.Click += delegate { SetCleanChecks(true); };
            Button btnNone = MkFlowButton(Tr.S("Ничего", "None"), 90, false);
            btnNone.Click += delegate { SetCleanChecks(false); };
            _btnCleanRules = MkFlowButton(Tr.S("Правила winapp2", "winapp2 rules"), 170, false);
            _btnCleanRules.Click += delegate { DoLoadWinapp2(); };
            Button btnLog = MkFlowButton(Tr.S("Лог", "Log"), 80, false);
            btnLog.Click += delegate { OpenCleanLog(); };
            Button btnDetails = MkFlowButton(Tr.S("Состав…", "Contents…"), 110, false);
            btnDetails.Click += delegate { ShowCleanDetails(SelectedCleanCategory()); };

            Label warn = MkNote(Tr.S("⚠ Файлы удаляются безвозвратно. Код, проекты, системные папки и данные не трогаются. Закройте браузеры для полной очистки кэша.",
                                     "⚠ Files are deleted permanently. Code, projects, system folders and data are never touched. Close browsers to fully clear cache."), true);
            _lblCleanTotal = MkNote(Tr.S("Нажмите «Анализировать»", "Click “Analyze”"), false);

            top.Controls.Add(btnAnalyze);
            top.Controls.Add(btnClean);
            top.Controls.Add(_btnCleanCancel);
            top.Controls.Add(btnAll);
            top.Controls.Add(btnNone);
            top.Controls.Add(_btnCleanRules);
            top.Controls.Add(btnLog);
            top.Controls.Add(btnDetails);

            _lvClean = new FastListView();
            _lvClean.Dock = DockStyle.Fill;
            _lvClean.View = View.Details;
            _lvClean.CheckBoxes = true;
            _lvClean.FullRowSelect = true;
            _lvClean.Columns.Add(Tr.S("Категория", "Category"), 230);
            _lvClean.Columns.Add(Tr.S("Размер", "Size"), 110);
            _lvClean.Columns.Add(Tr.S("Файлов", "Files"), 90);
            _lvClean.Columns.Add(Tr.S("Что чистится", "What is cleaned"), 520);
            SetupOwnerDraw(_lvClean);
            // Двойной клик открывает состав категории. Штатный ListView на двойной клик ещё и
            // переключает галочку — гасим это через ItemCheck, иначе категория «случайно»
            // снимается с очистки.
            _lvClean.MouseDown += delegate(object s, MouseEventArgs e) { _cleanDblClick = e.Clicks > 1; };
            _lvClean.ItemCheck += delegate(object s, ItemCheckEventArgs e)
            {
                if (_cleanDblClick) { e.NewValue = e.CurrentValue; _cleanDblClick = false; }
            };
            _lvClean.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                ListViewItem hit = _lvClean.GetItemAt(e.X, e.Y);
                if (hit != null) ShowCleanDetails(hit.Tag as CleanCategory);
            };
            _lvClean.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.Handled = true; ShowCleanDetails(SelectedCleanCategory()); }
            };
            // Итог «отмечено к удалению» следует за галочками. BeginUpdate не гасит ItemChecked,
            // поэтому «Все»/«Ничего» пересчитывали итог на каждой строке, а каждый пересчёт —
            // это два прохода DistinctSize по всем целям (с winapp2 их тысячи). Пересчёт на
            // время массовой простановки выключается флагом и делается один раз в конце.
            _lvClean.ItemChecked += delegate
            {
                if (!_suspendCleanTotal && _diskBusy == 0 && _cleanCats != null) UpdateCleanTotal(true);
            };
            // Отмеченные категории переживают и повторный анализ, и перезапуск: раньше выбор
            // сбрасывался к «рекомендованным» на каждом обновлении строки.
            MemWatch(_lvClean, CleanScope, true, CleanMemKey);

            tab.Controls.Add(_lvClean);
            tab.Controls.Add(_lblCleanTotal);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        private int _diskBusy;
        private System.Windows.Forms.Timer _cleanTick;
        private DateTime _cleanStarted;
        private string _cleanPhase;         // «Анализ» / «Удаление» — для строки состояния
        private int _cleanDone, _cleanTotal;
        private Button _btnCleanRules;      // гасим на время загрузки winapp2.ini
        private bool _suspendCleanTotal;    // идёт массовая простановка галочек — итог считаем один раз в конце
        private bool _cleanStopped;         // последняя работа прервана «Стопом» — итог неполный, и это надо сказать
        private string _cleanResultText;    // «✓ Освобождено: …» — держится в строке, пока идёт перепроверка после удаления

        // Анализ идёт в фоне и показывает категории по мере готовности, а не одним
        // куском в конце: обход .nuget\packages или Windows.old — это минуты, и раньше
        // всё это время список был пуст без признаков жизни.
        //
        // Счётчика «сделано/всего» для этого мало: последней категорией может остаться одна
        // очень долгая (DISM по хранилищу компонентов), и надпись «13/14» стоит неподвижно
        // минутами. Поэтому пока идёт работа, тикает таймер: секундомер, что именно сейчас
        // считается и живые цифры в строках.
        private void StartCleanTicker(string phase, int total)
        {
            _cleanPhase = phase;
            _cleanDone = 0;
            _cleanTotal = total;
            _cleanStarted = DateTime.UtcNow;
            // Статус движка от прошлой работы (последний шаг DISM/pnputil) остаётся в поле:
            // без сброса новая фаза первые секунды показывала бы чужую строку.
            _engine.DiskStatus = null;
            if (_cleanTick == null)
            {
                _cleanTick = new System.Windows.Forms.Timer();
                _cleanTick.Interval = 500;
                _cleanTick.Tick += delegate { CleanTick(); };
            }
            _cleanTick.Start();
            CleanTick();
        }

        private void StopCleanTicker()
        {
            if (_cleanTick != null) _cleanTick.Stop();
        }

        private static string Elapsed(TimeSpan ts)
        {
            return ((int)ts.TotalMinutes) + ":" + ts.Seconds.ToString("00");
        }

        private void CleanTick()
        {
            if (_diskBusy == 0) { StopCleanTicker(); return; }
            string el = Elapsed(DateTime.UtcNow - _cleanStarted);

            if (_cleanTotal > 0 && _cleanCats != null)
            {
                // живые цифры в строках ещё не досчитанных категорий
                List<string> running = new List<string>();
                foreach (ListViewItem it in _lvClean.Items)
                {
                    CleanCategory c = it.Tag as CleanCategory;
                    if (c == null || c.Analyzed) continue;
                    string p = c.Progress;
                    it.SubItems[1].Text = p != null ? p : (c.Size > 0 ? Engine.FormatBytes(c.Size) : "…");
                    it.SubItems[2].Text = c.FileCount > 0 ? c.FileCount.ToString() : "";
                    if (running.Count < 2) running.Add(c.Title);
                }
                _lblCleanTotal.Text = CleanLine(_cleanPhase + " " + _cleanDone + "/" + _cleanTotal + "   ·   " + el
                    + (running.Count > 0 ? "   ·   " + Tr.S("считается: ", "working on: ") + string.Join(", ", running.ToArray()) : ""));
            }
            else
            {
                string s = _engine.DiskStatus;
                _lblCleanTotal.Text = CleanLine(_cleanPhase + "   ·   " + el + (string.IsNullOrEmpty(s) ? "" : "   ·   " + s));
            }
        }

        // Результат последнего удаления держится в начале строки, пока идёт перепроверка:
        // раньше «✓ Освобождено: …» затиралось первым же «Анализ…» в том же обработчике и
        // не успевало отрисоваться — цифру показывал только всплывающий значок в трее.
        private string CleanLine(string s)
        {
            return string.IsNullOrEmpty(_cleanResultText) ? s : _cleanResultText + "   ·   " + s;
        }

        private void DoAnalyzeDisk()
        {
            DoAnalyzeDisk(false);
        }

        // keepResult — перепроверка сразу после удаления: итог очистки остаётся в строке.
        private void DoAnalyzeDisk(bool keepResult)
        {
            if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0)
            {
                _lblCleanTotal.Text = CleanLine(Tr.S("Уже идёт работа с диском — дождитесь её окончания или нажмите «Стоп».",
                                                     "Disk work is already running — wait for it or click “Stop”."));
                return;
            }
            if (!keepResult) _cleanResultText = null;
            _cleanStopped = false;
            _engine.ResetDiskCancel();
            _lblCleanTotal.Text = CleanLine(Tr.S("Анализ…", "Analyzing…"));
            _lvClean.Items.Clear();
            _cleanCats = null;
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = true;
            StartCleanTicker(Tr.S("Анализ", "Analyzing"), 0);

            Thread t = new Thread(delegate()
            {
                List<CleanCategory> cats;
                try { cats = _engine.BuildCleanCategories(); }
                catch { cats = new List<CleanCategory>(); }

                UiPost(delegate { _cleanCats = cats; PopulateClean(cats); _cleanTotal = cats.Count; });

                int done = 0;
                try
                {
                    _engine.AnalyzeCategories(cats, delegate(CleanCategory c)
                    {
                        int n = Interlocked.Increment(ref done);
                        UiPost(delegate { UpdateCleanRow(c, n, cats.Count); });
                    });
                }
                catch { }

                Interlocked.Exchange(ref _diskBusy, 0);
                UiPost(delegate
                {
                    StopCleanTicker();
                    UpdateCleanTotal(true);
                    if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SetCleanChecks(bool value)
        {
            _lvClean.BeginUpdate();
            _suspendCleanTotal = true;
            try { foreach (ListViewItem it in _lvClean.Items) it.Checked = value; }
            finally { _suspendCleanTotal = false; _lvClean.EndUpdate(); }
            if (_diskBusy == 0 && _cleanCats != null) UpdateCleanTotal(true);
            AutoFillLastColumnDeferred(_lvClean);
        }

        // «Стоп» не останавливает работу мгновенно: обход текущей папки и запущенная утилита
        // (DISM, pnputil) доигрывают до ближайшей проверки флага. Пока это происходит, кнопка
        // гаснет, а строка честно говорит «останавливаю», иначе нажатие выглядит проигнорированным.
        private void CancelDisk()
        {
            _engine.CancelDiskWork();
            _cleanStopped = true;
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
            _cleanPhase = Tr.S("Останавливаю…", "Stopping…");
            _lblCleanTotal.Text = CleanLine(_diskBusy != 0
                ? Tr.S("Останавливаю… (жду завершения текущего шага)", "Stopping… (waiting for the current step)")
                : Tr.S("Остановлено пользователем.", "Cancelled by user."));
        }

        // Область и ключ памяти выбора для списка категорий (см. MainForm.Memory.cs).
        private const string CleanScope = "clean.cat";

        private static string CleanMemKey(ListViewItem it)
        {
            CleanCategory c = it.Tag as CleanCategory;
            return c == null ? null : c.Id;
        }

        // Как категория выглядит, если про неё ничего не помним: отмеченная по умолчанию и непустая.
        private static bool CleanMemDefault(ListViewItem it)
        {
            CleanCategory c = it.Tag as CleanCategory;
            return c != null && CleanCheckedByDefault(c) && c.Size > 0;
        }

        // Галочка окна — не то же, что Recommended: по Recommended работает /auto без окна. Отличия — выбранный
        // набор: старые логи, остатки обновлений, кэши шейдеров, скачанные тулчейны и хранилище компонентов (WinSxS) отмечены,
        // кэши NVIDIA и Windows (эскизы, иконки) — нет.
        internal static bool CleanCheckedByDefault(CleanCategory c)
        {
            switch (c.Id)
            {
                case "logs": case "drivers": case "shaders": case "devbig": case "winsxs": return true;
                case "nvidia": case "shell": return false;
                default: return c.Recommended;
            }
        }

        private void PopulateClean(List<CleanCategory> cats)
        {
            MemBeginFill();
            _lvClean.BeginUpdate();
            try
            {
                _lvClean.Items.Clear();
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (CleanCategory c in cats)
                {
                    ListViewItem it = new ListViewItem(c.Title);
                    it.SubItems.Add("…");
                    it.SubItems.Add("");
                    it.SubItems.Add(CleanRowDesc(c));
                    it.Tag = c;
                    it.ForeColor = _theme.Text;
                    it.BackColor = _theme.Surface;
                    rows.Add(it);
                }
                _lvClean.Items.AddRange(rows.ToArray());
                MemEndFill(_lvClean, CleanScope, true, CleanMemKey, CleanMemDefault);
            }
            finally { _lvClean.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvClean);
        }

        private void UpdateCleanRow(CleanCategory c, int done, int total)
        {
            ListViewItem it = FindCleanRow(c);
            if (it != null)
            {
                FillCleanRow(it, c);
                // Размер категории становится известен только сейчас, поэтому умолчание
                // «рекомендована и непустая» применяется здесь же — но лишь пока пользователь
                // сам ничего не решил: его галочку анализ больше не сбивает.
                MemBeginFill();
                try { it.Checked = MemBool(CleanScope, c.Id, CleanCheckedByDefault(c) && c.Size > 0, true); }
                finally { MemEndFillPlain(); }
            }
            // саму надпись рисует тикер (он же показывает секундомер и текущую категорию)
            _cleanDone = done; _cleanTotal = total;
            if (_cleanTick == null || !_cleanTick.Enabled)
                _lblCleanTotal.Text = CleanLine(Tr.S("Анализ… ", "Analyzing… ") + done + "/" + total);
            if (done == total) UpdateCleanTotal(true);
        }

        private ListViewItem FindCleanRow(CleanCategory c)
        {
            foreach (ListViewItem it in _lvClean.Items) if (it.Tag == c) return it;
            return null;
        }

        private static void FillCleanRow(ListViewItem it, CleanCategory c)
        {
            it.SubItems[1].Text = Engine.FormatBytes(c.Size);
            it.SubItems[2].Text = c.FileCount.ToString();
            // Desc у категорий-действий формируется при анализе — перечитываем всегда
            it.SubItems[3].Text = CleanRowDesc(c);
        }

        private static string CleanRowDesc(CleanCategory c)
        {
            string s = c.Desc ?? "";
            // пометка об исключениях идёт первой: в конце её съедало многоточие колонки
            if (c.TargetsOff > 0)
                s = Tr.S("отключено вами: ", "disabled by you: ") + c.TargetsOff
                  + (c.SizeOff > 0 ? " (" + Engine.FormatBytes(c.SizeOff) + ")" : "")
                  + (s.Length > 0 ? "  ·  " + s : "");
            if (!string.IsNullOrEmpty(c.Note)) s += "  ·  " + c.Note;
            return s;
        }

        private CleanCategory SelectedCleanCategory()
        {
            if (_lvClean.SelectedItems.Count == 0) return null;
            return _lvClean.SelectedItems[0].Tag as CleanCategory;
        }

        private bool _cleanDblClick;

        private static string AgeText(int minutes)
        {
            if (minutes >= 1440 && minutes % 1440 == 0) return (minutes / 1440) + Tr.S(" дн.", " d");
            if (minutes >= 60 && minutes % 60 == 0) return (minutes / 60) + Tr.S(" ч", " h");
            return minutes + Tr.S(" мин", " min");
        }

        private static string TargetNote(CleanTarget t)
        {
            List<string> p = new List<string>();
            if (t.Guarded) p.Add(Tr.S("защищено правилами", "protected by guard"));
            p.Add(t.ContentsOnly ? Tr.S("только содержимое", "contents only") : Tr.S("папка целиком", "whole folder"));
            if (!t.Recurse) p.Add(Tr.S("без подпапок", "no subfolders"));
            if (t.MinAgeMinutes > 0) p.Add(Tr.S("старше ", "older than ") + AgeText(t.MinAgeMinutes));
            if (t.Errors > 0) p.Add(Tr.S("недоступно: ", "inaccessible: ") + t.Errors);
            return string.Join(" · ", p.ToArray());
        }

        private static long DetailSize(object tag, CleanCategory c)
        {
            CleanTarget t = tag as CleanTarget;
            if (t != null) return t.Size;
            DriverPackage d = tag as DriverPackage;
            if (d != null) return d.Size;
            return c.BinSize;
        }

        private static string DetailPath(object tag)
        {
            CleanTarget t = tag as CleanTarget;
            if (t != null) return t.Path;
            DriverPackage d = tag as DriverPackage;
            if (d != null) return d.RepoDir;
            return "shell:RecycleBinFolder";
        }

        // Состав категории: её папки/пакеты с галочками. Снятая галочка запоминается в
        // конфиге и действует при подсчёте и удалении — так из «Кэшей приложений» можно
        // навсегда исключить, скажем, кэш Telegram, не отказываясь от всей категории.
        private void ShowCleanDetails(CleanCategory c)
        {
            string title = Tr.S("Состав категории", "Category contents");
            // Пока идёт анализ, пул потоков движка считает эту же категорию: «OK» переписал бы
            // t.Enabled и пересчитал итоги под работающим анализом — данные разъезжались.
            if (_diskBusy != 0)
            {
                MessageBox.Show(this,
                    Tr.S("Идёт работа с диском: состав категории можно менять после её окончания — или нажмите «Стоп».",
                         "Disk work is running: category contents can be changed after it finishes — or click “Stop”."),
                    title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (c == null)
            {
                MessageBox.Show(this, Tr.S("Выберите категорию в списке.", "Select a category in the list."),
                                title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (c.Kind == "winsxs")
            {
                MessageBox.Show(this, Tr.S("Хранилище компонентов обслуживает DISM: у него нет отдельных папок, которые можно исключить.",
                                           "The component store is serviced by DISM: it has no individual folders to exclude."),
                                c.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            bool drivers = c.Kind == "driverstore";
            if (drivers && (c.Drivers == null || c.Drivers.Count == 0))
            {
                MessageBox.Show(this, c.Analyzed
                        ? Tr.S("Устаревших пакетов драйверов не найдено.", "No superseded driver packages found.")
                        : Tr.S("Список пакетов появится после анализа.", "The package list appears after analysis."),
                    c.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (!drivers && c.Targets.Count == 0 && !c.RecycleBin)
            {
                MessageBox.Show(this, Tr.S("На этом компьютере у категории нет ни одной папки.", "This category has no folders on this computer."),
                                c.Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Form dlg = new Form();
            dlg.Text = Tr.S("Состав: ", "Contents: ") + c.Title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 980; dlg.Height = 640;
            dlg.MinimumSize = new Size(680, 420);
            dlg.MinimizeBox = false; dlg.MaximizeBox = true; dlg.ShowInTaskbar = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;
            int cw = dlg.ClientSize.Width;

            // Ширина панелей задаётся ДО добавления детей: якоря считаются от размера
            // родителя в момент первой раскладки, а у свежей Panel это 200 px — с ними
            // OK/Отмена уезжали за правый край окна.
            Panel head = new Panel();
            head.Dock = DockStyle.Top; head.Height = 62; head.Width = cw;
            Label desc = new Label();
            desc.Left = 14; desc.Top = 10; desc.Width = cw - 28; desc.Height = 20;
            desc.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            desc.AutoEllipsis = true; desc.ForeColor = _theme.Text;
            desc.Text = c.Desc ?? "";
            Label hint = new Label();
            hint.Left = 14; hint.Top = 34; hint.Width = cw - 28; hint.Height = 20;
            hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            hint.AutoEllipsis = true; hint.ForeColor = _theme.Subtle;
            hint.Text = Tr.S("Снимите галочку с того, что удалять не нужно — выбор запоминается и действует при каждой очистке. Двойной клик открывает папку.",
                             "Untick what should not be deleted — the choice is remembered and applied on every cleanup. Double-click opens the folder.");
            head.Controls.Add(desc);
            head.Controls.Add(hint);

            FastListView lv = new FastListView();
            lv.Dock = DockStyle.Fill;
            lv.View = View.Details;
            lv.CheckBoxes = true;
            lv.FullRowSelect = true;
            lv.HideSelection = false;
            if (drivers)
            {
                lv.Columns.Add(Tr.S("Пакет", "Package"), 360);
                lv.Columns.Add(Tr.S("Размер", "Size"), 100);
                lv.Columns.Add(Tr.S("Версия", "Version"), 130);
                lv.Columns.Add(Tr.S("Поставщик · класс", "Provider · class"), 300);
            }
            else
            {
                lv.Columns.Add(Tr.S("Папка", "Folder"), 520);
                lv.Columns.Add(Tr.S("Размер", "Size"), 100);
                lv.Columns.Add(Tr.S("Файлов", "Files"), 80);
                lv.Columns.Add(Tr.S("Примечание", "Note"), 240);
            }
            _flexColumn[lv] = 0;
            if (!drivers) _pathColumns[lv] = 0;
            SetupOwnerDraw(lv);

            List<ListViewItem> rows = new List<ListViewItem>();
            if (drivers)
            {
                foreach (DriverPackage d in c.Drivers)
                {
                    ListViewItem it = new ListViewItem(d.Published + "   " + d.Original);
                    it.SubItems.Add(d.RepoDir == null ? "?" : Engine.FormatBytes(d.Size));
                    it.SubItems.Add(d.Version ?? "");
                    it.SubItems.Add(((d.Provider ?? "") + (string.IsNullOrEmpty(d.ClassName) ? "" : " · " + d.ClassName)).Trim(' ', '·'));
                    it.Tag = d; it.Checked = d.Enabled;
                    rows.Add(it);
                }
            }
            else
            {
                foreach (CleanTarget t in c.Targets)
                {
                    ListViewItem it = new ListViewItem(t.Path + (string.IsNullOrEmpty(t.Mask) ? "" : "  [" + t.Mask + "]"));
                    it.SubItems.Add(t.Analyzed ? Engine.FormatBytes(t.Size) : "…");
                    it.SubItems.Add(t.Analyzed ? t.FileCount.ToString() : "");
                    it.SubItems.Add(TargetNote(t));
                    it.Tag = t; it.Checked = t.Enabled;
                    rows.Add(it);
                }
                if (c.RecycleBin)
                {
                    // SHEmptyRecycleBin без корня — Корзины всех дисков, не только системного
                    ListViewItem it = new ListViewItem(Tr.S("Корзина (все диски)", "Recycle Bin (all drives)"));
                    it.SubItems.Add(c.Analyzed ? Engine.FormatBytes(c.BinSize) : "…");
                    it.SubItems.Add(c.Analyzed ? c.BinCount.ToString() : "");
                    it.SubItems.Add(Tr.S("очищается целиком", "emptied completely"));
                    it.Tag = "recyclebin"; it.Checked = c.BinEnabled;
                    rows.Add(it);
                }
            }
            // самое крупное — сверху: за этим и открывают состав
            if (c.Analyzed)
                rows.Sort(delegate(ListViewItem a, ListViewItem b) { return DetailSize(b.Tag, c).CompareTo(DetailSize(a.Tag, c)); });
            foreach (ListViewItem it in rows)
            {
                it.ForeColor = it.Checked ? _theme.Text : _theme.Subtle;
                it.BackColor = _theme.Surface;
            }
            lv.Items.AddRange(rows.ToArray());

            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom; bottom.Height = 58; bottom.Width = cw;
            Button all = MkButton(Tr.S("Все", "All"), 14, 11, 80, false);
            Button none = MkButton(Tr.S("Ничего", "None"), 102, 11, 90, false);
            Button open = MkButton(Tr.S("Открыть папку", "Open folder"), 200, 11, 150, false);
            Label sum = new Label();
            sum.Left = 362; sum.Top = 19; sum.Width = cw - 362 - 250; sum.Height = 20;
            sum.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            sum.AutoEllipsis = true; sum.ForeColor = _theme.Subtle;
            Button ok = MkButton("OK", cw - 14 - 110 - 8 - 110, 11, 110, true);
            Button cancel = MkButton(Tr.S("Отмена", "Cancel"), cw - 14 - 110, 11, 110, false);
            ok.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            bottom.Controls.Add(all);
            bottom.Controls.Add(none);
            bottom.Controls.Add(open);
            bottom.Controls.Add(sum);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            // Пока создаётся handle списка, ListView переигрывает ItemChecked для каждой
            // отмеченной строки, а Items в этот момент ещё содержит null — обработчик
            // включается только после Shown.
            bool ready = false;
            MethodInvoker refreshSum = delegate
            {
                int n = 0; long sz = 0;
                foreach (ListViewItem it in lv.Items)
                    if (it != null && it.Checked) { n++; sz += DetailSize(it.Tag, c); }
                sum.Text = Tr.S("Отмечено: ", "Ticked: ") + n + Tr.S(" из ", " of ") + rows.Count
                         + (c.Analyzed ? "  ·  " + Engine.FormatBytes(sz) : "");
            };
            lv.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (!ready || e.Item == null) return;
                e.Item.ForeColor = e.Item.Checked ? _theme.Text : _theme.Subtle;
                refreshSum();
            };
            // Тот же приём, что и в списке категорий: ItemChecked не гасится через BeginUpdate,
            // а каждый его вызов пересчитывает всю сумму — на тысячах строк winapp2 «Все» и
            // «Ничего» стоили квадрата от числа строк. Считаем один раз в конце.
            all.Click += delegate
            {
                lv.BeginUpdate();
                ready = false;
                try { foreach (ListViewItem it in lv.Items) { it.Checked = true; it.ForeColor = _theme.Text; } }
                finally { ready = true; lv.EndUpdate(); }
                refreshSum();
            };
            none.Click += delegate
            {
                lv.BeginUpdate();
                ready = false;
                try { foreach (ListViewItem it in lv.Items) { it.Checked = false; it.ForeColor = _theme.Subtle; } }
                finally { ready = true; lv.EndUpdate(); }
                refreshSum();
            };
            MethodInvoker openSel = delegate
            {
                if (lv.SelectedItems.Count == 0) return;
                string path = DetailPath(lv.SelectedItems[0].Tag);
                if (string.IsNullOrEmpty(path)) return;
                if (!path.StartsWith("shell:") && !Directory.Exists(path)) return;
                try { Process.Start("explorer.exe", path.StartsWith("shell:") ? path : "\"" + path + "\""); } catch { }
            };
            open.Click += delegate { openSel(); };
            // двойной клик — открыть папку; галочку при этом не трогаем (см. _lvClean)
            bool dbl = false;
            lv.MouseDown += delegate(object s, MouseEventArgs e) { dbl = e.Clicks > 1; };
            lv.ItemCheck += delegate(object s, ItemCheckEventArgs e) { if (dbl) { e.NewValue = e.CurrentValue; dbl = false; } };
            lv.MouseDoubleClick += delegate(object s, MouseEventArgs e)
            {
                if (lv.GetItemAt(e.X, e.Y) != null) openSel();
            };

            Panel mid = new Panel();
            mid.Dock = DockStyle.Fill;
            mid.Padding = new Padding(12, 0, 12, 0);   // поля под скруглённую рамку списка
            mid.Controls.Add(lv);
            dlg.Controls.Add(mid);
            dlg.Controls.Add(head);
            dlg.Controls.Add(bottom);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            lv.BackColor = _theme.Surface; lv.ForeColor = _theme.Text;
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            dlg.Shown += delegate { ready = true; refreshSum(); AutoFillLastColumnDeferred(lv); };

            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            if (dr == DialogResult.OK)
            {
                // Ключи пишутся только для реально изменённых строк, а пересчёт и запись конфига —
                // только если что-то изменилось: SetTargetEnabled линейно просматривает список
                // исключений, и на тысячах строк winapp2 «OK» заметно подвисал даже без правок.
                bool changed = false;
                foreach (ListViewItem it in lv.Items)
                {
                    CleanTarget t = it.Tag as CleanTarget;
                    DriverPackage d = it.Tag as DriverPackage;
                    if (t != null)
                    {
                        if (t.Enabled == it.Checked) continue;
                        t.Enabled = it.Checked; _engine.SetTargetEnabled(t.Key, it.Checked);
                    }
                    else if (d != null)
                    {
                        if (d.Enabled == it.Checked) continue;
                        d.Enabled = it.Checked; _engine.SetTargetEnabled(Engine.DriverKey(d), it.Checked);
                    }
                    else
                    {
                        if (c.BinEnabled == it.Checked) continue;
                        c.BinEnabled = it.Checked; _engine.SetTargetEnabled(Engine.BinKey(c), it.Checked);
                    }
                    changed = true;
                }
                if (changed)
                {
                    Engine.RecalcCategory(c);
                    _engine.SaveConfig();
                    ListViewItem row = FindCleanRow(c);
                    if (row != null)
                    {
                        if (c.Analyzed) FillCleanRow(row, c); else row.SubItems[3].Text = CleanRowDesc(c);
                        if (c.Analyzed && c.Size == 0) row.Checked = false;
                    }
                    if (_diskBusy == 0 && _cleanCats != null) UpdateCleanTotal(true);
                    AutoFillLastColumnDeferred(_lvClean);
                }
            }
            _flexColumn.Remove(lv);
            _pathColumns.Remove(lv);
            dlg.Dispose();
        }

        private void UpdateCleanTotal(bool finished)
        {
            // без двойного счёта вложенных целей (Engine.DistinctSize) — раньше Temp и
            // сборки Playwright входили в итог дважды
            long total = _cleanCats != null ? Engine.DistinctSize(_cleanCats) : 0;
            List<CleanCategory> checkedCats = new List<CleanCategory>();
            foreach (ListViewItem it in _lvClean.Items)
            {
                CleanCategory c = it == null ? null : it.Tag as CleanCategory;
                if (c != null && it.Checked) checkedCats.Add(c);
            }
            long chosen = Engine.DistinctSize(checkedCats);
            string extra = "";
            if (_engine.Winapp2RuleCount > 0)
            {
                extra = Tr.S("   ·   правил winapp2: ", "   ·   winapp2 rules: ") + _engine.Winapp2RuleCount;
                // Откуда взяты правила — это вопрос доверия: файл вне защищённой папки может
                // переписать любой процесс пользователя, а чистим мы от администратора.
                if (!_engine.Winapp2Protected)
                    extra += Tr.S("   ·   файл правил вне защищённой папки — системные пути для него закрыты",
                                  "   ·   the rule file is outside the protected folder — system paths are closed to it");
            }
            // Прерванный «Стопом» анализ даёт заведомо неполные цифры — молчать об этом нельзя:
            // итог выглядел бы как честный результат полного прохода.
            if (_cleanStopped)
                extra += Tr.S("   ·   ОСТАНОВЛЕНО — посчитано не всё", "   ·   STOPPED — not everything was counted");
            _lblCleanTotal.Text = CleanLine((finished ? Tr.S("Всего мусора найдено: ", "Total junk found: ")
                                                      : Tr.S("Найдено пока: ", "Found so far: "))
                + Engine.FormatBytes(total)
                + (finished ? Tr.S("   ·   отмечено к удалению: ", "   ·   checked for deletion: ") + Engine.FormatBytes(chosen) : "")
                + Tr.S("   ·   двойной клик — состав категории", "   ·   double-click a category for its contents") + extra);
        }

        private void DoCleanDisk()
        {
            string cleanTitle = Tr.S("Очистка диска", "Disk cleanup");
            if (_cleanCats == null) { MsgInfo(Tr.S("Сначала нажмите «Анализировать».", "Click “Analyze” first."), cleanTitle); return; }
            if (_diskBusy != 0)
            {
                MsgInfo(Tr.S("Дождитесь окончания анализа.", "Wait for the analysis to finish."), cleanTitle);
                return;
            }
            List<CleanCategory> sel = new List<CleanCategory>();
            long size = 0;
            foreach (ListViewItem it in _lvClean.Items)
                if (it.Checked && it.Tag is CleanCategory) { CleanCategory c = (CleanCategory)it.Tag; sel.Add(c); size += c.Size; }
            if (sel.Count == 0) { MsgInfo(Tr.S("Не выбрано ни одной категории.", "No categories selected."), cleanTitle); return; }

            DialogResult dr = MessageBox.Show(this,
                Tr.S("Удалить ", "Delete ") + Engine.FormatBytes(size) +
                Tr.S(" в " + sel.Count + " категориях?\r\nДействие необратимо (файлы удаляются мимо Корзины).",
                     " across " + sel.Count + " categories?\r\nThis is irreversible (files bypass the Recycle Bin)."),
                Tr.S("Очистка диска", "Disk Cleanup"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;

            if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0) return;
            _cleanResultText = null;
            _cleanStopped = false;
            _engine.ResetDiskCancel();
            _lblCleanTotal.Text = Tr.S("Удаление…", "Deleting…");
            string op = Tr.S("очистка диска (удаление файлов)", "disk cleanup (deleting files)");
            BeginWrite(op);
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = true;
            StartCleanTicker(Tr.S("Удаление", "Deleting"), 0);

            Thread t = new Thread(delegate()
            {
                CleanResult res = null;
                string err = null;
                try
                {
                    res = CleanCategoriesMaybeElevated(sel, true,
                        delegate(string s) { _engine.DiskStatus = s; },
                        delegate { return _engine.DiskCancelled || _closing; });
                }
                catch (Exception ex) { err = ex.Message; }
                // Признак неполного итога несёт сам итог: флаг движка к моменту отрисовки
                // уже мог быть сброшен следующей операцией.
                bool cancelled = res != null ? res.Cancelled : _engine.DiskCancelled;
                EndWrite(op);
                Interlocked.Exchange(ref _diskBusy, 0);
                UiPost(delegate
                {
                    StopCleanTicker();
                    if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
                    if (res == null)
                    {
                        _lblCleanTotal.Text = Tr.S("Очистка не выполнена", "Cleanup failed")
                            + (err != null ? ": " + err : ".");
                        return;
                    }
                    // Итог держится в строке и во время перепроверки: она стартует тут же и
                    // раньше затирала его своим «Анализ…» ещё до отрисовки.
                    _cleanResultText = Tr.S("✓ Освобождено: ", "✓ Freed: ") + Engine.FormatBytes(res.Freed)
                        + Tr.S("   ·   файлов: ", "   ·   files: ") + res.FilesDeleted
                        + (res.Errors > 0 ? Tr.S("   ·   пропущено (заняты/нет доступа): ",
                                                 "   ·   skipped (locked/no access): ") + res.Errors : "")
                        + (cancelled ? Tr.S("   ·   ОСТАНОВЛЕНО — удалено не всё отмеченное",
                                            "   ·   STOPPED — not everything ticked was deleted") : "");
                    _lblCleanTotal.Text = _cleanResultText;
                    if (_tray != null)
                        _tray.ShowBalloonTip(3000, Tr.S("Очистка диска", "Disk Cleanup"),
                            Tr.S("Освобождено ~", "Freed ~") + Engine.FormatBytes(res.Freed), ToolTipIcon.Info);
                    DoAnalyzeDisk(true);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Подключение базы winapp2.ini (как в FluentCleaner) — скачивание по запросу.
        private void DoLoadWinapp2()
        {
            string have = _engine.Winapp2Path;
            string msg = have != null
                ? Tr.S("База правил уже подключена:\r\n", "Rule database already attached:\r\n") + have
                  + Tr.S("\r\n\r\nСкачать свежую версию?", "\r\n\r\nDownload a fresh copy?")
                : Tr.S("Скачать базу правил winapp2.ini (~5 МБ) из открытого репозитория Winapp2?\r\n\r\n"
                       + "Это тысячи готовых правил «где у какого приложения лежит кэш» — тот же формат, "
                       + "что использует FluentCleaner. Реестр не чистится ни при каких правилах.",
                       "Download the winapp2.ini rule database (~5 MB) from the public Winapp2 repository?\r\n\r\n"
                       + "These are thousands of ready rules describing where each application keeps its cache — "
                       + "the same format FluentCleaner uses. The registry is never cleaned, whatever a rule says.");
            if (!MsgAsk(msg, "winapp2.ini")) return;

            // Каждый клик раньше запускал ещё одну загрузку: все они писали один и тот же
            // winapp2.ini.tmp и дрались за File.Replace. Флаг занятости страницы (тот же, что
            // у анализа и удаления) не даёт запустить вторую и заодно разводит загрузку с
            // анализом; кнопка гаснет, а секундомер показывает, что загрузка идёт.
            if (Interlocked.CompareExchange(ref _diskBusy, 1, 0) != 0)
            {
                _lblCleanTotal.Text = CleanLine(Tr.S("Идёт работа с диском — дождитесь её окончания.",
                                                     "Disk work is running — wait for it to finish."));
                return;
            }
            _cleanResultText = null;
            _cleanStopped = false;
            _engine.ResetDiskCancel();   // «Стоп» работает и на загрузке — она идёт через тот же флаг
            if (_btnCleanRules != null) _btnCleanRules.Enabled = false;
            if (_btnCleanCancel != null) _btnCleanCancel.Enabled = true;
            StartCleanTicker(Tr.S("Загрузка базы правил (~5 МБ)", "Downloading the rule database (~5 MB)"), 0);

            Thread t = new Thread(delegate()
            {
                string err = null;
                bool stopped = false;
                try
                {
                    // Счётчик байтов — единственный признак живой загрузки: пять мегабайт
                    // на медленном канале это минуты, и раньше строка всё это время не менялась.
                    _engine.DownloadWinapp2(delegate(long got, long total)
                    {
                        _engine.DiskStatus = total > 0
                            ? Engine.FormatBytes(got) + Tr.S(" из ", " of ") + Engine.FormatBytes(total)
                            : Tr.S("получено ", "received ") + Engine.FormatBytes(got);
                    }, delegate { return _engine.DiskCancelled || _closing; });
                }
                catch (OperationCanceledException) { stopped = true; }
                catch (Exception ex) { err = ex.Message; }
                _engine.DiskStatus = null;
                Interlocked.Exchange(ref _diskBusy, 0);
                bool stoppedCopy = stopped;
                UiPost(delegate
                {
                    StopCleanTicker();
                    if (_btnCleanRules != null) _btnCleanRules.Enabled = true;
                    if (_btnCleanCancel != null) _btnCleanCancel.Enabled = false;
                    if (stoppedCopy)
                    {
                        _lblCleanTotal.Text = CleanLine(Tr.S("Загрузка остановлена — прежняя база правил осталась как была.",
                                                             "Download stopped — the previous rule database is unchanged."));
                        return;
                    }
                    if (err != null)
                    {
                        _lblCleanTotal.Text = Tr.S("Не удалось скачать базу правил: ", "Rule database download failed: ") + err;
                        return;
                    }
                    DoAnalyzeDisk();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OpenCleanLog()
        {
            string path = _engine.CleanLogPath;
            if (!File.Exists(path))
            {
                MsgInfo(Tr.S("Лог пока пуст — очистка ещё не выполнялась.",
                             "The log is empty — no cleanup has run yet."), Tr.S("Очистка диска", "Disk cleanup"));
                return;
            }
            try { Process.Start("notepad.exe", "\"" + path + "\""); }
            catch (Exception ex) { MsgError(ex.Message); }
        }
    }
}
