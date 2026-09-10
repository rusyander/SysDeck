// Windows Process Cleaner — вкладка «Сканирование»: мониторинг, поиск и завершение процессов, автоочистка
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
        private Control BuildScanTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();

            // ряд 1
            _btnScanRun = MkFlowButton(Tr.S("Сканировать", "Scan"), 150, true);
            _btnScanRun.Click += delegate { DoScan(); };
            Button btnSelAll = MkFlowButton(Tr.S("Выбрать все", "Select all"), 130, false);
            btnSelAll.Click += delegate { SetAllChecks(true); };
            Button btnSelNone = MkFlowButton(Tr.S("Снять выбор", "Clear"), 130, false);
            btnSelNone.Click += delegate { SetAllChecks(false); };

            _chkGlobal = new CheckBox();
            _chkGlobal.Text = Tr.S("Все процессы (глобально)", "All processes (global)");
            _chkGlobal.AutoSize = true;
            _chkGlobal.Margin = new Padding(12, 7, 8, 0);
            _chkGlobal.CheckedChanged += delegate
            {
                _engine.Config.GlobalScan = _chkGlobal.Checked;
                _engine.SaveConfig();
            };
            Label lblWarn = MkFlowLabel(Tr.S("⚠ завершает любые ваши простаивающие/осиротевшие процессы",
                                             "⚠ terminates any of your idle/orphaned processes"), true);

            // ряд 2
            _btnScanClean = MkFlowButton(Tr.S("Очистить выбранные", "Clean selected"), 200, true);
            _btnScanClean.Click += delegate { DoClean(); };
            _btnScanAuto = MkFlowButton(Tr.S("Автоочистка всех неактивных", "Auto-clean all inactive"), 250, true);
            _btnScanAuto.Click += delegate { DoAutoCleanButton(); };
            _btnScanPurge = MkFlowButton(Tr.S("Очистить память", "Purge memory"), 170, false);
            _btnScanPurge.Click += delegate { DoPurgeOnly(); };

            top.Controls.Add(_btnScanRun);
            top.Controls.Add(btnSelAll);
            top.Controls.Add(btnSelNone);
            top.Controls.Add(_chkGlobal);
            top.Controls.Add(lblWarn);
            top.SetFlowBreak(lblWarn, true);   // второй ряд — действия очистки
            top.Controls.Add(_btnScanClean);
            top.Controls.Add(_btnScanAuto);
            top.Controls.Add(_btnScanPurge);

            _lblSummary = MkNote(Tr.S("Нажмите «Сканировать»", "Click “Scan”"), false);

            _lvScan = new FastListView();
            _lvScan.Dock = DockStyle.Fill;
            _lvScan.View = View.Details;
            _lvScan.CheckBoxes = true;
            _lvScan.FullRowSelect = true;
            MemWatch(_lvScan, ScanScope, false, ScanMemKey);
            _lvScan.Columns.Add(Tr.S("Категория", "Category"), 120);
            _lvScan.Columns.Add(Tr.S("Имя", "Name"), 130);
            _lvScan.Columns.Add("PID", 65);
            _lvScan.Columns.Add("PPID", 65);
            _lvScan.Columns.Add("CPU %", 65);
            _lvScan.Columns.Add("RAM", 85);
            _lvScan.Columns.Add(Tr.S("Простой", "Idle"), 80);
            _lvScan.Columns.Add(Tr.S("Окно", "Window"), 55);
            _lvScan.Columns.Add(Tr.S("Порт", "Port"), 55);
            _lvScan.Columns.Add(Tr.S("Дети", "Children"), 55);
            _lvScan.Columns.Add(Tr.S("Статус", "Status"), 330);
            SetupOwnerDraw(_lvScan);

            _lblResult = new Label();
            _lblResult.Dock = DockStyle.Bottom;
            _lblResult.Height = 32;
            _lblResult.TextAlign = ContentAlignment.MiddleLeft;
            _lblResult.Padding = new Padding(2, 0, 0, 0);
            _lblResult.Text = "";

            tab.Controls.Add(_lvScan);
            tab.Controls.Add(_lblResult);
            tab.Controls.Add(_lblSummary);
            tab.Controls.Add(top);
            return tab;
        }

        // ---------- Логика ----------
        // Тик мониторинга: целиком в фоновом потоке, в UI возвращается только
        // обновление иконки трея. Interlocked не даёт тикам наложиться, если один
        // затянулся (много процессов, холодный кэш).
        private void MonitorCallback(object state)
        {
            if (_closing) return;
            if (Interlocked.CompareExchange(ref _monitorBusy, 1, 0) != 0) return;
            try { _engine.MonitorTick(); }
            catch { }
            finally { Interlocked.Exchange(ref _monitorBusy, 0); }
            UiPost(delegate { UpdateTrayState(); });
        }

        private void RestartMonitor()
        {
            if (_monitor != null) { _monitor.Dispose(); _monitor = null; }
            if (_closing || !_engine.Config.MonitorEnabled) return;
            int period = _engine.Config.MonitorIntervalSeconds * 1000;
            _monitor = new System.Threading.Timer(MonitorCallback, null, period, period);
        }

        // Безопасная отправка работы в UI-поток из фонового.
        private void UiPost(MethodInvoker action)
        {
            if (_closing) return;
            try
            {
                if (!IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch { }
        }

        // Живой счётчик времени для долгих операций. Одной статичной надписи мало:
        // стадия может стоять минутами (перечисление процессов, разрешение ярлыков,
        // разбор профиля браузера), и неподвижный текст неотличим от зависшего окна.
        // Рабочий поток только называет стадию (SetStage — присваивание volatile-строки),
        // рисует её вместе с секундомером таймер UI-потока.
        private class BusyTicker
        {
            private readonly Label _label;
            private readonly System.Windows.Forms.Timer _timer;
            private readonly DateTime _from;
            private volatile string _stage;

            public BusyTicker(Label label, string stage)
            {
                _label = label;
                _stage = stage;
                _from = DateTime.UtcNow;
                _timer = new System.Windows.Forms.Timer();
                _timer.Interval = 500;
                _timer.Tick += delegate { Paint(); };
                _timer.Start();
                Paint();
            }

            public void SetStage(string stage) { _stage = stage; }

            // Только из UI-потока: таймер формы иначе не остановить.
            public void Stop()
            {
                _timer.Stop();
                _timer.Dispose();
            }

            private void Paint()
            {
                try { _label.Text = _stage + "   ·   " + Elapsed(DateTime.UtcNow - _from); }
                catch { }
            }
        }

        private BusyTicker _procTicker;
        private Button _btnScanRun, _btnScanClean, _btnScanAuto, _btnScanPurge;

        private static string BusyWaitText()
        {
            return Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish.");
        }

        // Кнопки вкладки гасятся на время работы: раньше повторный клик просто
        // проваливался в занятый флаг и молча ничего не делал.
        private void SetScanButtons(bool enabled)
        {
            // Включаем, только когда не осталось ни одной идущей операции: скан после
            // завершения процессов заканчивается раньше самой очистки и «оживил» бы кнопки досрочно.
            if (enabled && (_killBusy != 0 || _scanBusy != 0)) return;
            if (_btnScanRun != null) _btnScanRun.Enabled = enabled;
            if (_btnScanClean != null) _btnScanClean.Enabled = enabled;
            if (_btnScanAuto != null) _btnScanAuto.Enabled = enabled;
            if (_btnScanPurge != null) _btnScanPurge.Enabled = enabled;
        }

        // Одни ворота на все операции над процессами. Флаги _killBusy/_purgeBusy/_autoBusy
        // жили независимо, и автоочистка по расписанию могла начаться поверх ручной:
        // память чистилась дважды, в историю попадали две записи, а обе операции писали
        // в одну и ту же строку результата. Берём все три сразу, отпускаем все три сразу.
        private bool BeginProcOp(string stage)
        {
            if (Interlocked.CompareExchange(ref _killBusy, 1, 0) != 0) return false;
            Interlocked.Exchange(ref _purgeBusy, 1);
            Interlocked.Exchange(ref _autoBusy, 1);
            SetScanButtons(false);
            _procTicker = new BusyTicker(_lblResult, stage);
            return true;
        }

        // Зовётся из фонового потока, поэтому поле сначала читается в локальную переменную.
        private void ProcStage(string stage)
        {
            BusyTicker t = _procTicker;
            if (t != null) t.SetStage(stage);
        }

        private void EndProcOp()
        {
            if (_procTicker != null) { _procTicker.Stop(); _procTicker = null; }
            Interlocked.Exchange(ref _autoBusy, 0);
            Interlocked.Exchange(ref _purgeBusy, 0);
            Interlocked.Exchange(ref _killBusy, 0);
            SetScanButtons(true);
        }

        private List<ProcInfo> _lastScan = new List<ProcInfo>();
        private bool _autoAfterScan;          // «Автоочистка» нажата до первого сканирования

        // Сканирование процессов — в фоне. Раньше Scan() вместе с чтением путей и SID
        // всех процессов шло в UI-потоке, и окно висело на всё время обхода.
        private void DoScan()
        {
            if (Interlocked.CompareExchange(ref _scanBusy, 1, 0) != 0)
            {
                _lblSummary.Text = Tr.S("Сканирование уже идёт — дождитесь окончания.", "A scan is already running — wait for it to finish.");
                return;
            }
            SetScanButtons(false);
            BusyTicker tick = new BusyTicker(_lblSummary, Tr.S("Сканирование процессов", "Scanning processes"));
            bool global = _engine.Config.GlobalScan;
            Thread t = new Thread(delegate()
            {
                List<ProcInfo> found = null;
                string err = null;
                try { found = _engine.Scan(global); }
                catch (Exception ex) { err = ex.Message; }
                List<ProcInfo> res = found;
                string emsg = err;
                Interlocked.Exchange(ref _scanBusy, 0);
                UiPost(delegate
                {
                    tick.Stop();
                    SetScanButtons(true);
                    if (res == null)
                    {
                        // Сбой подменялся пустым списком, и пользователю писали «найдено: 0» —
                        // то есть уверяли, что машина чиста, хотя обход не состоялся.
                        _lblSummary.Text = Tr.S("Сканирование не удалось: ", "Scan failed: ") + emsg;
                        if (_autoAfterScan)
                        {
                            _autoAfterScan = false;
                            _lblResult.Text = Tr.S("Автоочистка отменена: сканирование не удалось.", "Auto-clean cancelled: the scan failed.");
                        }
                        return;
                    }
                    PopulateScan(res);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Память выбора для списка процессов: в пределах сеанса один и тот же живой процесс
        // сохраняет PID, поэтому ключ — PID и имя. Полка сеансовая: после перезапуска
        // приложения те же номера принадлежат уже другим процессам.
        private const string ScanScope = "scan.proc";

        private static string ScanMemKey(ListViewItem it)
        {
            ProcInfo p = it.Tag as ProcInfo;
            return p == null ? null : p.Pid + "/" + (p.Name == null ? "" : p.Name);
        }

        private static bool ScanMemDefault(ListViewItem it)
        {
            ProcInfo p = it.Tag as ProcInfo;
            return p != null && p.IsCandidate;
        }

        private void PopulateScan(List<ProcInfo> found)
        {
            _lastScan = found ?? new List<ProcInfo>();
            Dictionary<string, int> byCat = new Dictionary<string, int>();
            int candidates = 0;
            MemBeginFill();

            // BeginUpdate обязателен: без него каждый Add перерисовывает весь список,
            // а с owner-draw это 300 полных перерисовок на одно заполнение.
            _lvScan.BeginUpdate();
            try
            {
                _lvScan.Items.Clear();
                ListViewItem[] rows = new ListViewItem[_lastScan.Count];
                for (int i = 0; i < _lastScan.Count; i++)
                {
                    ProcInfo p = _lastScan[i];
                    ListViewItem it = new ListViewItem(p.Category);
                    it.SubItems.Add(p.Name);
                    it.SubItems.Add(p.Pid.ToString());
                    it.SubItems.Add(p.ParentPid.ToString());
                    it.SubItems.Add(p.CpuPercent.ToString("0.00", CultureInfo.InvariantCulture));
                    it.SubItems.Add(Engine.FormatBytes(p.RamBytes));
                    it.SubItems.Add(FormatSpan(p.IdleFor));
                    it.SubItems.Add(YesNo(p.HasWindow));
                    it.SubItems.Add(YesNo(p.ListensTcp));
                    it.SubItems.Add(YesNo(p.HasChildren));
                    it.SubItems.Add(p.Reason);
                    it.ToolTipText = p.Name + " (pid " + p.Pid + ")" +
                        (string.IsNullOrEmpty(p.Path) ? "" : "\r\n" + p.Path) + "\r\n" + p.Reason;
                    it.Tag = p;
                    // Умолчание — решение движка; если пользователь переставил галочку сам,
                    // MemEndFill вернёт её после пересканирования (тот же процесс = тот же PID).
                    it.Checked = p.IsCandidate;
                    it.ForeColor = _theme.Text;
                    if (p.IsCandidate) it.BackColor = _theme.CandidateBg;
                    else if (p.Whitelisted) it.BackColor = _theme.WhiteBg;
                    else it.BackColor = _theme.Surface;
                    rows[i] = it;

                    int c;
                    byCat[p.Category] = byCat.TryGetValue(p.Category, out c) ? c + 1 : 1;
                    if (p.IsCandidate) candidates++;
                }
                _lvScan.Items.AddRange(rows);
            }
            finally
            {
                _lvScan.EndUpdate();
                MemEndFill(_lvScan, ScanScope, false, ScanMemKey, ScanMemDefault);
                AutoFillLastColumnDeferred(_lvScan);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(Tr.S("Найдено: ", "Found: ") + _lastScan.Count +
                      Tr.S("  ·  кандидатов на завершение: ", "  ·  termination candidates: ") + candidates + "   ");
            // Простой считается только тиками мониторинга: при выключенном мониторинге
            // IdleFor всегда 0 и кандидатов не будет никогда. Раньше это выглядело как
            // «всё активно» без объяснения — теперь причина названа прямо в сводке.
            AppConfig cfg = _engine.Config;
            int idleReq = cfg.GlobalScan ? Math.Max(cfg.IdleMinutes, cfg.GlobalIdleMinutes) : cfg.IdleMinutes;
            if (candidates == 0 && _lastScan.Count > 0 && !cfg.MonitorEnabled && idleReq > 0)
                sb.Append(Tr.S("⚠ мониторинг выключен — простой не измеряется, кандидатов не будет (см. Настройки)   ",
                               "⚠ monitoring is off — idle time is not measured, so no candidates (see Settings)   "));
            List<string> parts = new List<string>();
            foreach (KeyValuePair<string, int> kv in byCat) parts.Add(kv.Key + " " + kv.Value);
            sb.Append(string.Join("  ", parts.ToArray()));
            _lblSummary.Text = sb.ToString();
            UpdateTrayState();

            if (_autoAfterScan)
            {
                _autoAfterScan = false;
                if (_lastScan.Count > 0) DoAutoCleanButton();
                else _lblResult.Text = Tr.S("Процессов по списку не найдено — завершать нечего.",
                                            "No matching processes found — nothing to terminate.");
            }
        }

        private void SetAllChecks(bool value)
        {
            _lvScan.BeginUpdate();
            try { foreach (ListViewItem it in _lvScan.Items) it.Checked = value; }
            finally { _lvScan.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvScan);
            _lvScan.Invalidate();
        }

        private void DoClean()
        {
            // Отказ — до диалога подтверждения: спрашивать «завершить 12 процессов?»,
            // чтобы затем отказать, значит потратить ответ пользователя впустую.
            if (_killBusy != 0) { _lblResult.Text = BusyWaitText(); return; }
            List<ProcInfo> toKill = new List<ProcInfo>();
            foreach (ListViewItem it in _lvScan.Items)
                if (it.Checked && it.Tag is ProcInfo) toKill.Add((ProcInfo)it.Tag);

            if (toKill.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Не выбрано ни одного процесса.", "No processes selected."),
                    Tr.S("Очистка", "Clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Завершить процессов: ", "Terminate processes: ") + toKill.Count + "?",
                Tr.S("Подтверждение", "Confirm"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            ExecuteKill(toKill);
        }

        // Автоочистка по кнопке: завершить все найденные неактивные (кандидаты).
        private void DoAutoCleanButton()
        {
            if (_killBusy != 0) { _lblResult.Text = BusyWaitText(); return; }
            // Сканирование идёт в фоне: без готового списка запускаем его, а автоочистка
            // продолжится сама из PopulateScan — второй клик больше не нужен.
            if (_lastScan == null || _lastScan.Count == 0)
            {
                _autoAfterScan = true;
                _lblResult.Text = Tr.S("Сканирование… автоочистка продолжится, как только появится список.",
                                       "Scanning… auto-clean will continue as soon as the list appears.");
                DoScan();
                return;
            }
            List<ProcInfo> cands = _lastScan.Where(p => p.IsCandidate).ToList();
            if (cands.Count == 0)
            {
                MessageBox.Show(this, Tr.S("Неактивных (заброшенных) процессов не найдено.", "No inactive (abandoned) processes found."),
                    Tr.S("Автоочистка", "Auto-clean"), MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StringBuilder list = new StringBuilder();
            foreach (ProcInfo p in cands.Take(20)) list.AppendLine("• " + p.Name + " (pid " + p.Pid + ")");
            if (cands.Count > 20) list.AppendLine(Tr.S("… и ещё ", "… and ") + (cands.Count - 20) + Tr.S("", " more"));
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Найдено неактивных процессов: ", "Inactive processes found: ") + cands.Count +
                Tr.S(".\r\nЗавершить все?\r\n\r\n", ".\r\nTerminate all?\r\n\r\n") + list,
                Tr.S("Автоочистка", "Auto-clean"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (dr != DialogResult.Yes) return;
            ExecuteKill(cands);
        }

        // Общий исполнитель: завершает список, чистит память, пишет историю, обновляет UI.
        // Завершение — в фоне. TerminateProcess ждёт закрытия до нескольких секунд;
        // раньше на 20 процессах UI стоял минуты. Внутри — пакетный TerminateMany:
        // WM_CLOSE рассылается всем сразу, ожидание общее.
        private void ExecuteKill(List<ProcInfo> list)
        {
            // Второй запуск поверх идущего дважды чистил память и писал историю параллельно.
            if (!BeginProcOp(Tr.S("Завершение процессов", "Terminating processes")))
            {
                _lblResult.Text = BusyWaitText();
                return;
            }
            List<int> pids = new List<int>();
            List<string> names = new List<string>();
            foreach (ProcInfo p in list) { pids.Add(p.Pid); names.Add(p.Name + " (pid " + p.Pid + ")"); }

            string op = Tr.S("завершение процессов", "terminating processes");
            BeginWrite(op);
            ProcStage(string.Format(Tr.S("Завершение процессов: {0}", "Terminating processes: {0}"), pids.Count));
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                string err = null;
                Engine.MemResult mr = null;
                // Стадии называются по очереди: внутри TerminateMany уходит несколько секунд
                // на WM_CLOSE и добивание упрямых, и раньше всё это время строка молчала.
                // Теперь движок сам называет свой этап — «прошу закрыться», «жду закрытия окон (2 с)»,
                // «завершаю принудительно: N».
                try
                {
                    killed = _engine.TerminateMany(pids, out freed,
                        delegate(string s) { ProcStage(Tr.S("Завершение: ", "Terminating: ") + s); },
                        delegate { return _closing; });
                }
                catch (Exception ex) { err = ex.Message; }
                ProcStage(Tr.S("Очистка памяти", "Purging memory"));
                try { mr = PurgeStandbyMaybeElevated(true, ProcStage, delegate { return _closing; }); }
                catch (Exception ex) { if (err == null) err = ex.Message; }
                long totalFreed = freed + (mr != null ? mr.FreedBytes : 0);
                string msg = mr != null && mr.Message != null ? mr.Message : "";
                int killedCopy = killed;
                ProcStage(Tr.S("Запись в историю", "Writing history"));
                try { SaveHistory(killedCopy, totalFreed, names); } catch { }
                EndWrite(op);
                string emsg = err;
                int asked = pids.Count;

                UiPost(delegate
                {
                    EndProcOp();
                    StringBuilder sb = new StringBuilder();
                    if (emsg != null) sb.Append(Tr.S("⚠ Ошибка: ", "⚠ Error: ")).Append(emsg).Append("    ·  ");
                    sb.Append(Tr.S("✓ Завершено процессов: ", "✓ Terminated: ")).Append(killedCopy)
                      .Append(Tr.S("    ✓ Освобождено RAM: ", "    ✓ Freed RAM: ")).Append(Engine.FormatBytes(totalFreed));
                    if (msg.Length > 0) sb.Append("    ·  ").Append(msg);
                    // «Завершено: 0» без пояснения читается как поломка кнопки.
                    if (killedCopy == 0 && emsg == null && asked > 0)
                        sb.Append(Tr.S("    ·  ни один процесс не завершён: они уже закрыты или защищены системой",
                                       "    ·  no process was terminated: they are already gone or protected by the system"));
                    _lblResult.Text = sb.ToString();
                    DoScan();
                    RefreshHistory();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DoPurgeOnly()
        {
            if (!BeginProcOp(Tr.S("Очистка памяти", "Purging memory")))
            {
                _lblResult.Text = BusyWaitText();
                return;
            }
            Thread t = new Thread(delegate()
            {
                Engine.MemResult mr = null;
                string err = null;
                try { mr = PurgeStandbyMaybeElevated(true, ProcStage, delegate { return _closing; }); }
                catch (Exception ex) { err = ex.Message; }
                Engine.MemResult res = mr;
                string emsg = err;
                UiPost(delegate
                {
                    EndProcOp();
                    if (res == null)
                    {
                        // Причину больше не глотаем: чаще всего это отсутствие прав администратора.
                        _lblResult.Text = Tr.S("Очистить память не удалось", "Memory purge failed")
                            + (emsg != null ? ": " + emsg : Tr.S(" (нужны права администратора).", " (administrator rights required)."));
                        return;
                    }
                    string msg = res.Message + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(res.FreedBytes);
                    _lblResult.Text = msg;
                    if (_tray != null)
                        _tray.ShowBalloonTip(2500, "Standby Memory", msg,
                            res.Ok ? ToolTipIcon.Info : ToolTipIcon.Warning);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Автоочистка: сканирует и завершает только кандидатов. Вызывается и из таймера
        // расписания, поэтому вся тяжёлая часть — в фоновом потоке.
        private void RunAutoClean(bool interactive)
        {
            if (!BeginProcOp(Tr.S("Автоочистка", "Auto-clean")))
            {
                // Расписание молчит (оно просто пропустит такт), ручной вызов — нет:
                // клик по «Очистить сейчас» обязан объяснить, почему ничего не началось.
                if (interactive) _lblResult.Text = BusyWaitText();
                return;
            }
            string op = Tr.S("автоочистка", "auto-clean");
            BeginWrite(op);
            Thread t = new Thread(delegate()
            {
                int killed = 0; long freed = 0;
                Engine.MemResult mr = null;
                string err = null;
                List<string> names = new List<string>();
                int cands = 0;
                try
                {
                    ProcStage(Tr.S("Сканирование процессов", "Scanning processes"));
                    List<ProcInfo> scan = _engine.Scan(_engine.Config.GlobalScan);
                    List<int> pids = new List<int>();
                    foreach (ProcInfo p in scan)
                        if (p.IsCandidate) { pids.Add(p.Pid); names.Add(p.Name + " (pid " + p.Pid + ")"); }
                    cands = pids.Count;
                    ProcStage(string.Format(Tr.S("Завершение процессов: {0}", "Terminating processes: {0}"), cands));
                    killed = _engine.TerminateMany(pids, out freed,
                        delegate(string s) { ProcStage(Tr.S("Завершение: ", "Terminating: ") + s); },
                        delegate { return _closing; });
                    ProcStage(Tr.S("Очистка памяти", "Purging memory"));
                    // interactive = кнопку нажал человек; по таймеру окно UAC не показываем
                    mr = PurgeStandbyMaybeElevated(interactive, ProcStage, delegate { return _closing; });
                }
                catch (Exception ex) { err = ex.Message; }
                long total = freed + (mr != null ? mr.FreedBytes : 0);
                ProcStage(Tr.S("Запись в историю", "Writing history"));
                try { SaveHistory(killed, total, names); } catch { }
                EndWrite(op);

                int killedCopy = killed;
                int candsCopy = cands;
                string emsg = err;
                UiPost(delegate
                {
                    EndProcOp();
                    string msg = emsg != null
                        ? Tr.S("Автоочистка не удалась: ", "Auto-clean failed: ") + emsg
                        : (candsCopy == 0
                            ? Tr.S("Неактивных процессов не найдено — завершать было нечего.", "No inactive processes found — nothing to terminate.")
                            : Tr.S("Завершено: ", "Terminated: ") + killedCopy
                              + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(total));
                    _lblResult.Text = msg;
                    if (_tray != null)
                        _tray.ShowBalloonTip(3000,
                            emsg != null ? Tr.S("Автоочистка не удалась", "Auto-clean failed") : Tr.S("Автоочистка выполнена", "Auto-clean done"),
                            msg, emsg != null ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    if (interactive && Visible) { DoScan(); RefreshHistory(); }
                    UpdateTrayState();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void SaveHistory(int killed, long freed, List<string> names)
        {
            HistoryEntry e = new HistoryEntry();
            e.DateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            e.TerminatedCount = killed;
            e.FreedBytes = freed;
            e.Processes = names;
            _engine.AppendHistory(e);
        }
    }
}
