// SysDeck — вкладка «Память»: права, сбросы, освобождение рабочих наборов и завершение процессов.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        // ------------------------------------------------------------------ //
        //  Права
        // ------------------------------------------------------------------ //

        private void RamUpdateRights()
        {
            if (_ramRights == null) return;
            if (Elevated)
            {
                _ramRights.Text = Tr.S("● права администратора есть", "● administrator rights are in place");
                _ramBtnRights.Visible = false;
            }
            else if (_ramAgent.Running)
            {
                _ramRights.Text = Tr.S("● права получены — сбросы идут без запросов",
                                       "● rights obtained — resets run without prompts");
                _ramBtnRights.Visible = false;
            }
            else
            {
                _ramRights.Text = Tr.S("○ сбросы спросят права один раз за сеанс",
                                       "○ resets will ask for rights once per session");
                _ramBtnRights.Visible = true;
            }
        }

        // Поднять помощника. Вызывается из фонового потока (окно UAC блокирует) либо с
        // кнопки — тогда тоже уходит в поток, чтобы окно не подвисало на время запроса.
        private void RamEnsureRights(bool fromButton, Action then)
        {
            if (Elevated || _ramAgent.Running)
            {
                if (then != null) then();
                return;
            }
            if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
            RamSetBusy(true);
            RamSay(Tr.S("Запрашиваю права администратора…", "Asking for administrator rights…"));

            Thread t = new Thread(delegate()
            {
                string err = _ramAgent.Start(_engine);
                bool declined = _ramAgent.Declined;
                Interlocked.Exchange(ref _ramActBusy, 0);
                UiPost(delegate
                {
                    RamSetBusy(false);
                    RamUpdateRights();
                    if (err == null)
                    {
                        RamSay(Tr.S("Права получены. Дальше сбросы выполняются сразу, без окон UAC.",
                                    "Rights obtained. From now on resets run instantly, with no UAC dialogs."));
                        if (then != null) then();
                        return;
                    }
                    RamSay(declined
                        ? Tr.S("Запрос прав отклонён — сбросы недоступны.", "The rights prompt was declined — resets are unavailable.")
                        : Tr.S("Не удалось получить права: ", "Could not obtain rights: ") + err);
                    if (fromButton && !declined) MsgError(err);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RamSetBusy(bool busy)
        {
            foreach (Button b in _ramActionButtons) b.Enabled = !busy;
            if (_ramBtnRights != null) _ramBtnRights.Enabled = !busy;
            if (_ramBtnTrim != null) _ramBtnTrim.Enabled = !busy && RamSelectedPids().Count > 0;
            if (_ramBtnKill != null) _ramBtnKill.Enabled = !busy && RamSelectedPids().Count > 0;
        }

        // ------------------------------------------------------------------ //
        //  Сбросы
        // ------------------------------------------------------------------ //

        private void RamRunReset(int command, string title)
        {
            if (_ramActBusy != 0) return;
            // «Полный сброс» выдавливает рабочие наборы ВСЕХ процессов: система на несколько
            // секунд становится вязкой, пока страницы читаются обратно. Об этом спрашивают.
            if (command == Engine.RamEmptyEverything || command == Engine.RamEmptyWorkingSets)
            {
                string what = command == Engine.RamEmptyEverything
                    ? Tr.S("Полный сброс: рабочие наборы всех процессов, системный кэш, изменённые страницы и список ожидания.",
                           "Full reset: the working sets of all processes, the system cache, the modified pages and the standby list.")
                    : Tr.S("Рабочие наборы всех процессов будут выдавлены в файл подкачки и кэш.",
                           "The working sets of all processes will be pushed out to the page file and the cache.");
                if (!MsgAsk(what + "\r\n\r\n"
                    + Tr.S("Ничего не потеряется, но несколько секунд система будет заметно медленнее: страницы придётся читать обратно. Продолжить?",
                           "Nothing is lost, but for a few seconds the system will be noticeably slower: the pages have to be read back. Continue?"),
                    Tr.S("Память", "Memory"))) return;
            }

            RamEnsureRights(false, delegate { RamSendReset(command, title); });
        }

        private void RamSendReset(int command, string title)
        {
            if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
            RamSetBusy(true);
            RamSay(title + Tr.S("  ·  выполняется…", "  ·  running…"));
            BeginWrite(Tr.S("сброс памяти", "memory reset"));

            int cmd = command;
            string name = title;
            Thread t = new Thread(delegate()
            {
                RamAction a;
                try
                {
                    if (Elevated) a = _engine.RamRunEmpty(cmd);
                    else a = _ramAgent.Send(RamAgent.CmdEmpty, cmd.ToString(CultureInfo.InvariantCulture), 60000);
                }
                catch (Exception ex) { a = new RamAction(); a.Message = ex.Message; }
                EndWrite(Tr.S("сброс памяти", "memory reset"));
                Interlocked.Exchange(ref _ramActBusy, 0);
                RamAction done = a;
                UiPost(delegate
                {
                    RamSetBusy(false);
                    RamUpdateRights();
                    RamSay(name + "  ·  " + (done.Ok
                        ? Tr.S("освобождено ", "freed ") + Engine.FormatBytes(done.Freed)
                        : Tr.S("не выполнено: ", "not done: ") + (done.Message ?? "")));
                    if (done.Ok && _tray != null)
                        _tray.ShowBalloonTip(2000, Tr.S("Память", "Memory"),
                            name + ": " + Tr.S("освобождено ", "freed ") + Engine.FormatBytes(done.Freed), ToolTipIcon.Info);
                    RamTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Сброс рабочих наборов выбранного. Своим процессам прав не нужно вовсе — помощник
        // поднимается, только если система отказала.
        private void RamTrimSelected()
        {
            List<int> pids = RamSelectedPids();
            if (pids.Count == 0) return;
            if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
            RamSetBusy(true);
            RamSay(Tr.S("Сбрасываю рабочие наборы…", "Emptying working sets…"));

            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                RamAction a;
                try { a = _engine.RamTrimProcesses(list); }
                catch (Exception ex) { a = new RamAction(); a.Message = ex.Message; }
                Interlocked.Exchange(ref _ramActBusy, 0);
                RamAction done = a;
                UiPost(delegate
                {
                    RamSetBusy(false);
                    RamSay(done.Message + (done.Freed > 0
                        ? Tr.S("  ·  освобождено ≈", "  ·  freed ≈") + Engine.FormatBytes(done.Freed) : ""));
                    // Часть процессов не отдалась без прав — предложить помощника, а не молчать.
                    if (done.Denied > 0) RamOfferElevatedTrim(list, done.Denied);
                    RamTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void RamOfferElevatedTrim(List<int> pids, int denied)
        {
            if (Elevated) return;
            if (!MsgAsk(Tr.S("Не удалось сбросить рабочие наборы. Процессов, которым нужны права администратора: ",
                             "Could not empty the working sets. Processes that need administrator rights: ")
                        + denied
                        + Tr.S(".\r\n\r\nЗапросить права и повторить?", ".\r\n\r\nAsk for rights and retry?"),
                        Tr.S("Память", "Memory"))) return;

            List<int> list = pids;
            RamEnsureRights(true, delegate
            {
                if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
                RamSetBusy(true);
                Thread t = new Thread(delegate()
                {
                    string arg = string.Join(",", RamPidStrings(list));
                    RamAction a = _ramAgent.Send(RamAgent.CmdTrim, arg, 60000);
                    Interlocked.Exchange(ref _ramActBusy, 0);
                    RamAction done = a;
                    UiPost(delegate
                    {
                        RamSetBusy(false);
                        RamSay(done.Message ?? "");
                        RamTick();
                    });
                });
                t.IsBackground = true;
                t.Start();
            });
        }

        private static string[] RamPidStrings(List<int> pids)
        {
            string[] a = new string[pids.Count];
            for (int i = 0; i < pids.Count; i++) a[i] = pids[i].ToString(CultureInfo.InvariantCulture);
            return a;
        }

        // Завершение идёт прежним путём приложения: подтверждение со списком, затем
        // TerminateMany. Через резидентного помощника завершения НЕ ходят — см. Elevation.Ram.cs.
        private void RamKillSelected()
        {
            List<int> pids = RamSelectedPids();
            if (pids.Count == 0) return;
            if (_ramActBusy != 0) return;

            List<string> shown = new List<string>();
            RamSnapshot s = _ramSnap;
            if (s != null)
                foreach (int pid in pids)
                    foreach (RamProc p in s.Procs)
                        if (p.Pid == pid)
                        {
                            shown.Add(p.Name + " (pid " + pid + ")  ·  " + Engine.FormatBytes(p.PrivateWorkingSet));
                            break;
                        }
            if (shown.Count == 0) foreach (int pid in pids) shown.Add("pid " + pid);
            if (!MsgAsk(DevKillQuestion(Tr.S("Память", "Memory"), shown), Tr.S("Память", "Memory"))) return;

            if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
            RamSetBusy(true);
            RamSay(Tr.S("Завершаю выбранные процессы…", "Terminating the selected processes…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));

            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                string err = null;
                try { killed = _engine.TerminateMany(list, out freed, null, delegate { return _closing; }); }
                catch (Exception ex) { err = ex.Message; }
                // Кто устоял — спрашиваем сразу здесь, пока не начались новые снимки: список
                // нужен, чтобы предложить права ровно за них.
                List<int> alive;
                try { alive = _engine.SurvivorsOf(list); }
                catch { alive = new List<int>(); }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                Interlocked.Exchange(ref _ramActBusy, 0);
                int killedCopy = killed;
                long freedCopy = freed;
                string errCopy = err;
                List<int> aliveCopy = alive;
                UiPost(delegate
                {
                    RamSetBusy(false);
                    _ramChecked.Clear();
                    RamSelect(null);
                    RamSay(errCopy != null
                        ? Tr.S("Не удалось: ", "Failed: ") + errCopy
                        : killedCopy > 0
                            ? Tr.S("Завершено процессов: ", "Terminated: ") + killedCopy
                              + Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(freedCopy)
                            : Tr.S("Ни один процесс не завершился — нужны права администратора или процесс защищён.",
                                   "Not a single process terminated — administrator rights are needed, or the process is protected."));
                    RamTick();
                    if (errCopy == null && aliveCopy.Count > 0) RamOfferElevatedKill(aliveCopy);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Процесс, который не открылся под обычными правами, обычной попыткой не закрыть
        // никогда — предлагаем права один раз, за оставшихся, со списком в вопросе.
        private void RamOfferElevatedKill(List<int> pids)
        {
            if (Elevated || pids.Count == 0) return;

            List<string> shown = new List<string>();
            RamSnapshot s = _ramSnap;
            foreach (int pid in pids)
            {
                string name = null;
                if (s != null)
                    foreach (RamProc p in s.Procs)
                        if (p.Pid == pid) { name = p.Name; break; }
                shown.Add((name != null ? name + " " : "") + "(pid " + pid + ")");
            }
            if (!MsgAsk(Tr.S("Не удалось завершить процессов: ", "Processes that would not terminate: ")
                        + pids.Count
                        + Tr.S(" — им нужны права администратора.\r\n\r\n", " — they need administrator rights.\r\n\r\n")
                        + string.Join("\r\n", shown.ToArray())
                        + Tr.S("\r\n\r\nЗапросить права и повторить?", "\r\n\r\nAsk for rights and retry?"),
                        Tr.S("Память", "Memory"))) return;

            if (Interlocked.CompareExchange(ref _ramActBusy, 1, 0) != 0) return;
            RamSetBusy(true);
            RamSay(Tr.S("Запрашиваю права администратора…", "Asking for administrator rights…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));

            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                ElevResult r;
                try { r = KillElevated(list, null, delegate { return _closing; }); }
                catch (Exception ex) { r = new ElevResult(); r.Message = ex.Message; }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                Interlocked.Exchange(ref _ramActBusy, 0);
                ElevResult done = r;
                UiPost(delegate
                {
                    RamSetBusy(false);
                    RamSay(done.Ok
                        ? Tr.S("Завершено процессов: ", "Terminated: ") + done.Count
                          + (done.Freed > 0 ? Tr.S("  ·  освобождено ~", "  ·  freed ~") + Engine.FormatBytes(done.Freed) : "")
                        : done.Declined
                            ? Tr.S("Запрос прав отклонён — процессы остались на месте.",
                                   "The rights prompt was declined — the processes are still running.")
                            : Tr.S("Не удалось завершить: ", "Could not terminate: ") + (done.Message ?? ""));
                    RamTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
