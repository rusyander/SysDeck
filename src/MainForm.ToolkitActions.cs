// SysDeck — страница «Скрипты»: кнопки — установить/сохранить, включить, удалить, запустить, журнал, особые действия пунктов.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Пункты без прав администратора окно выполняет само в фоновом потоке. Пункты с Admin (задачи SYSTEM, HKLM) уходят
// помощнику одним заданием «toolkit» — одно окно UAC на все отмеченные, и только после нажатой кнопки.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Toolkit;

namespace SysDeck
{
    public partial class MainForm
    {
        private const int TkOpInstall = 0, TkOpRemove = 1, TkOpEnable = 2, TkOpDisable = 3;

        private Control ToolkitButtons(TkItem it, TkStatus s)
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.Name = "tkbuttons";
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 6, 0, 0);
            List<TkItem> one = new List<TkItem>();
            one.Add(it);

            string primary = s.State == TkState.NotInstalled ? Tr.S("Установить", "Install")
                           : it.Params.Count > 0 ? Tr.S("Сохранить", "Save") : Tr.S("Установить заново", "Reinstall");
            ToolkitButton(f, primary, true, delegate { ToolkitRun(one, TkOpInstall); });

            bool tasksVisible = false, tasksDenied = false;
            foreach (TkTaskState t in s.Tasks) { if (t.Exists && !t.Denied) tasksVisible = true; if (t.Denied) tasksDenied = true; }
            if (tasksVisible && !it.Admin)
                ToolkitButton(f, Tr.S("Запустить сейчас", "Run now"), false, delegate { ToolkitRunTasks(it); });
            if (tasksVisible || tasksDenied)
            {
                if (s.State == TkState.Disabled) ToolkitButton(f, Tr.S("Включить", "Enable"), false, delegate { ToolkitRun(one, TkOpEnable); });
                else ToolkitButton(f, Tr.S("Выключить", "Disable"), false, delegate { ToolkitRun(one, TkOpDisable); });
            }

            switch (it.Id)
            {
                case "proc-reaper":
                    ToolkitButton(f, Tr.S("Что убрал бы сейчас", "What it would reap now"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "reap.ps1"), "", false); });
                    break;
                case "audio-fix":
                    ToolkitButton(f, Tr.S("Починить звук сейчас", "Fix sound now"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "audio-fix.ps1"), "", true); });
                    ToolkitButton(f, Tr.S("Проверить устройство", "Check the device"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "audio-fix.ps1"), "-Audit", false); });
                    break;
                case "win-key-watch":
                    ToolkitButton(f, Tr.S("Снять залипшие клавиши", "Unstick keys now"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "unstick-modifiers.ps1"), "", false); });
                    ToolkitButton(f, Tr.S("Что зажато сейчас", "What is held now"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "unstick-modifiers.ps1"), "-WhatIf", false); });
                    break;
                case "docker-maint":
                    ToolkitButton(f, Tr.S("Отчёт Docker", "Docker report"), false,
                        delegate { ToolkitConsole(ToolkitScript(it, "docker-maint.ps1"), "", false); });
                    break;
                case "wslconfig":
                    ToolkitButton(f, Tr.S("Перезапустить WSL", "Restart WSL"), false, delegate { ToolkitRestartWsl(); });
                    break;
                case "tv-switch":
                    if (s.State != TkState.NotInstalled)
                    {
                        ToolkitButton(f, Tr.S("Выключить ТВ", "TV off"), false, delegate { ToolkitCmd(it, "tv-off.cmd"); });
                        ToolkitButton(f, Tr.S("Включить ТВ", "TV on"), false, delegate { ToolkitCmd(it, "tv-on.cmd"); });
                        ToolkitButton(f, Tr.S("Состояние ТВ", "TV status"), false, delegate { ToolkitCmd(it, "tv-status.cmd"); });
                    }
                    if (!TkEngine.DisplayConfigPresent(TkEnv.Real()))
                        ToolkitButton(f, Tr.S("Установить модуль", "Install module"), false, delegate { ToolkitInstallDisplayConfig(); });
                    break;
            }

            string log = it.LogPath == null ? null : Environment.ExpandEnvironmentVariables(it.LogPath);
            if (log != null && File.Exists(log))
                ToolkitButton(f, Tr.S("Журнал", "Log"), false, delegate { ToolkitOpenLog(log); });
            if (s.Dir != null && Directory.Exists(s.Dir))
                ToolkitButton(f, Tr.S("Открыть папку", "Open folder"), false, delegate { OpenInExplorer(s.Dir, false); });
            if (s.State != TkState.NotInstalled)
                ToolkitButton(f, Tr.S("Удалить", "Remove"), false, delegate { ToolkitRun(one, TkOpRemove); });
            return f;
        }

        private void ToolkitButton(FlowLayoutPanel f, string text, bool primary, EventHandler click)
        {
            Button b = MkFlowButton(text, 100, primary);
            b.Click += click;
            f.Controls.Add(b);
        }

        // Кнопки «Главной»: то же, что ярлыки на рабочем столе, но из установленной копии скрипта —
        // своей копии окно не заводит, иначе после «Сохранить» на странице «Скрипты» они разъехались бы.
        private void HomeFixAudio()
        {
            ToolkitConsole(ToolkitScript(TkCatalog.Find("audio-fix"), "audio-fix.ps1"), "", true);
        }

        // Права скрипт поднимает себе сам и только если инъекции KEYUP не хватило: обычно окна UAC нет.
        private void HomeUnstickKeys()
        {
            ToolkitConsole(ToolkitScript(TkCatalog.Find("win-key-watch"), "unstick-modifiers.ps1"), "", false);
        }

        private void ToolkitInstallChecked()
        {
            List<TkItem> items = new List<TkItem>();
            foreach (ListViewItem row in _lvTk.CheckedItems)
            {
                TkItem it = TkCatalog.Find(row.Name);
                if (it != null) items.Add(it);
            }
            if (items.Count > 0) ToolkitRun(items, TkOpInstall);
        }

        private static string ToolkitOpVerb(int op)
        {
            switch (op)
            {
                case TkOpRemove: return Tr.S("Удаление", "Removing");
                case TkOpEnable: return Tr.S("Включение", "Enabling");
                case TkOpDisable: return Tr.S("Выключение", "Disabling");
                default: return Tr.S("Установка", "Installing");
            }
        }

        private void ToolkitRun(List<TkItem> items, int op)
        {
            string title = Tr.S("Скрипты", "Scripts");
            if (_tkBusy != 0) { MsgInfo(BusyWaitText(), title); return; }
            if (op == TkOpRemove)
            {
                TkItem it = items[0];
                string extra = it.Id == "mpo-fix" ? Tr.S("\r\nMPO снова включится (после перезапуска Windows).", "\r\nMPO comes back on (after Windows restarts).")
                             : it.Id == "wslconfig" ? Tr.S("\r\nИз .wslconfig уберутся только ключи этой страницы.", "\r\nOnly this page's keys are removed from .wslconfig.")
                             : it.KeepIfPresent.Length > 0 ? Tr.S("\r\nВаши настройки (", "\r\nYour settings (") + string.Join(", ", it.KeepIfPresent) + Tr.S(") останутся.", ") stay.")
                             : "";
                if (MessageBox.Show(this, Tr.S("Удалить «", "Remove “") + it.Title + Tr.S("»: задачи и файлы скрипта?", "”: its tasks and files?") + extra,
                                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }

            // Значения — в UI-потоке: правки полей живут здесь.
            Dictionary<string, Dictionary<string, string>> values = new Dictionary<string, Dictionary<string, string>>();
            List<TkItem> user = new List<TkItem>(), admin = new List<TkItem>();
            foreach (TkItem it in items)
            {
                values[it.Id] = ToolkitValues(it);
                if (it.Admin) admin.Add(it); else user.Add(it);
            }
            bool wslSaved = false;
            foreach (TkItem it in items) if (it.Id == "wslconfig" && op == TkOpInstall) wslSaved = true;

            Interlocked.Exchange(ref _tkBusy, 1);
            ToolkitSetButtons(false);
            string verb = ToolkitOpVerb(op);
            BusyTicker tick = new BusyTicker(_lblTkInfo, verb + "…");
            string writeOp = title + ": " + verb;
            BeginWrite(writeOp);
            Thread t = new Thread(delegate()
            {
                Dictionary<string, string> results = new Dictionary<string, string>();
                int errors = 0;
                TkEnv env = TkEnv.Real();
                foreach (TkItem it in user)
                {
                    tick.SetStage(verb + ": " + it.Title);
                    List<string> log = new List<string>();
                    string err;
                    try { err = ToolkitApply(it, values[it.Id], env, op, log); }
                    catch (Exception ex) { err = ex.Message; }
                    if (err != null) errors++;
                    results[it.Id] = ToolkitResultText(err, log);
                }
                if (admin.Count > 0)
                {
                    tick.SetStage(verb + ": " + Tr.S("нужны права администратора — подтвердите в окне Windows", "administrator rights needed — confirm in the Windows prompt"));
                    if (Elevated)
                        foreach (TkItem it in admin)
                        {
                            List<string> log = new List<string>();
                            string err;
                            try { err = ToolkitApply(it, values[it.Id], env, op, log); }
                            catch (Exception ex) { err = ex.Message; }
                            if (err != null) errors++;
                            results[it.Id] = ToolkitResultText(err, log);
                        }
                    else
                    {
                        ElevJob job = new ElevJob();
                        job.Kind = "toolkit";
                        job.Number = op;
                        List<string> ids = new List<string>(), packed = new List<string>();
                        foreach (TkItem it in admin) { ids.Add(it.Id); packed.Add(TkEngine.Pack(it, values[it.Id])); }
                        job.Arg = string.Join(",", ids.ToArray());
                        job.Items = packed.ToArray();
                        ElevResult r = Elevation.Run(_engine, job, delegate(string p) { tick.SetStage(verb + ": " + p); }, null);
                        string text = r.Ok ? ToolkitResultText(null, new List<string>(r.Lines ?? new string[0]))
                                    : r.Declined ? DeclinedNote() : ToolkitResultText(r.Message ?? "?", new List<string>(r.Lines ?? new string[0]));
                        if (!r.Ok) errors++;
                        foreach (TkItem it in admin) results[it.Id] = text;
                    }
                }
                int errCount = errors;
                UiPost(delegate
                {
                    tick.Stop();
                    EndWrite(writeOp);
                    Interlocked.Exchange(ref _tkBusy, 0);
                    foreach (KeyValuePair<string, string> kv in results) _tkLastResult[kv.Key] = kv.Value;
                    if (op == TkOpInstall) foreach (TkItem it in items) _tkEdits.Remove(it.Id);
                    _lblTkInfo.Text = errCount == 0 ? verb + Tr.S(": готово", ": done") : verb + Tr.S(": есть ошибки — подробности справа", ": there were errors — details on the right");
                    ToolkitRefresh(items);
                    if (wslSaved && errCount == 0) ToolkitRestartWsl();
                });
            });
            t.IsBackground = true;
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
        }

        private static string ToolkitApply(TkItem it, Dictionary<string, string> v, TkEnv env, int op, List<string> log)
        {
            switch (op)
            {
                case TkOpRemove: return TkEngine.Remove(it, env, log);
                case TkOpEnable: return TkEngine.SetEnabled(it, v, env, true, log);
                case TkOpDisable: return TkEngine.SetEnabled(it, v, env, false, log);
                default: return TkEngine.Install(it, v, env, log);
            }
        }

        private static string ToolkitResultText(string err, List<string> log)
        {
            string stamp = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " — ";
            string done = log.Count > 0 ? string.Join("; ", log.ToArray()) : Tr.S("изменений нет", "no changes");
            return err == null ? stamp + done : stamp + Tr.S("ошибка: ", "error: ") + err + (log.Count > 0 ? Tr.S(". Сделано: ", ". Done: ") + done : "");
        }

        private void ToolkitSetResult(TkItem it, string text)
        {
            _tkLastResult[it.Id] = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + " — " + text;
            if (_tkSelected == it.Id && _lblTkResult != null && !_lblTkResult.IsDisposed) _lblTkResult.Text = _tkLastResult[it.Id];
        }

        private void ToolkitRunTasks(TkItem it)
        {
            TkStatus s;
            if (!_tkStatus.TryGetValue(it.Id, out s)) return;
            List<string> names = new List<string>(s.WantedTasks);
            Thread t = new Thread(delegate()
            {
                TkComScheduler com = new TkComScheduler();
                List<string> errs = new List<string>();
                foreach (string n in names) { string e = com.Run(n); if (e != null) errs.Add(e); }
                UiPost(delegate
                {
                    ToolkitSetResult(it, errs.Count == 0 ? Tr.S("запущено: ", "started: ") + string.Join(", ", names.ToArray()) : string.Join("; ", errs.ToArray()));
                    List<TkItem> one = new List<TkItem>();
                    one.Add(it);
                    ToolkitRefresh(one);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Скрипт из установленной копии; у звука — из старой, если новой ещё нет.
        private static string ToolkitScript(TkItem it, string file)
        {
            TkEnv env = TkEnv.Real();
            string path = Path.Combine(TkEngine.DeployDir(it, env), file);
            if (!File.Exists(path) && it.Id == "audio-fix")
            {
                string legacy = Path.Combine(TkEngine.LegacyAudioDir(env), file);
                if (File.Exists(legacy)) return legacy;
            }
            return path;
        }

        // Окно PowerShell с выводом скрипта остаётся открытым: это отчёт, который человек читает.
        private void ToolkitConsole(string script, string args, bool admin)
        {
            if (!File.Exists(script))
            {
                MsgInfo(Tr.S("Скрипт ещё не установлен — поставьте его пункт на странице «Скрипты»: ",
                             "The script is not installed yet — install its item on the “Scripts” page: ") + script, Tr.S("Скрипты", "Scripts"));
                return;
            }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -ExecutionPolicy Bypass -NoExit -File \"" + script + "\"" + (string.IsNullOrEmpty(args) ? "" : " " + args));
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(script);
                if (admin) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception ex) { if (ex.NativeErrorCode != 1223) MsgError(ex.Message); }
            catch (Exception ex) { MsgError(ex.Message); }
        }

        private void ToolkitCmd(TkItem it, string file)
        {
            string path = Path.Combine(TkEngine.DeployDir(it, TkEnv.Real()), file);
            if (!File.Exists(path)) { MsgInfo(Tr.S("Файл не найден: ", "File not found: ") + path, Tr.S("Скрипты", "Scripts")); return; }
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(path);
                psi.UseShellExecute = true;
                psi.WorkingDirectory = Path.GetDirectoryName(path);
                Process.Start(psi);
            }
            catch (Exception ex) { MsgError(ex.Message); }
        }

        private void ToolkitInstallDisplayConfig()
        {
            if (MessageBox.Show(this, Tr.S("Установить модуль PowerShell DisplayConfig из PowerShell Gallery для текущего пользователя? Откроется окно PowerShell — оно может спросить про доверие к репозиторию.",
                                           "Install the DisplayConfig PowerShell module from the PowerShell Gallery for the current user? A PowerShell window opens — it may ask whether to trust the repository."),
                                Tr.S("Скрипты", "Scripts"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                    "-NoProfile -NoExit -Command \"Install-Module DisplayConfig -Scope CurrentUser\"");
                psi.UseShellExecute = true;
                Process.Start(psi);
            }
            catch (Exception ex) { MsgError(ex.Message); }
        }

        private void ToolkitOpenLog(string path)
        {
            try { Process.Start(Path.Combine(Environment.SystemDirectory, "notepad.exe"), "\"" + path + "\""); }
            catch (Exception ex) { MsgError(ex.Message); }
        }

        // Лимиты .wslconfig действуют после перезапуска WSL; перезапуск останавливает все дистрибутивы и Docker Desktop.
        private void ToolkitRestartWsl()
        {
            string wsl = Path.Combine(Environment.SystemDirectory, "wsl.exe");
            if (!File.Exists(wsl)) return;
            if (MessageBox.Show(this, Tr.S("Перезапустить WSL сейчас, чтобы новые лимиты вступили в силу?\r\nВсе дистрибутивы WSL и контейнеры Docker Desktop остановятся; Docker Desktop поднимет свои сам.",
                                           "Restart WSL now so the new limits take effect?\r\nAll WSL distributions and Docker Desktop containers stop; Docker Desktop brings its own back."),
                                Tr.S("Скрипты", "Scripts"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            TkItem it = TkCatalog.Find("wslconfig");
            Thread t = new Thread(delegate()
            {
                string res;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(wsl, "--shutdown");
                    psi.UseShellExecute = false;
                    psi.CreateNoWindow = true;
                    using (Process p = Process.Start(psi))
                    {
                        if (!p.WaitForExit(60000)) res = Tr.S("wsl --shutdown не закончился за минуту", "wsl --shutdown did not finish within a minute");
                        else res = p.ExitCode == 0 ? Tr.S("WSL остановлен — лимиты действуют со следующего запуска", "WSL stopped — the limits apply from its next start")
                                                   : "wsl --shutdown: " + p.ExitCode.ToString(CultureInfo.InvariantCulture);
                    }
                }
                catch (Exception ex) { res = ex.Message; }
                UiPost(delegate { ToolkitSetResult(it, res); });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
