// SysDeck — вкладка «Загрузки»: команды движку, вставка и перетаскивание ссылок, контекстное меню.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Downloads;

namespace SysDeck
{
    public partial class MainForm
    {
        // ---------- команды процессу ----------

        // Команда в фоновом потоке; процесс поднимается, если не запущен. done получает ответ с ok = true.
        private void DlSend(JVal req, string busy, Action<JVal> done)
        {
            if (busy != null) DlInfo(busy);
            Thread t = new Thread(delegate()
            {
                string error;
                JVal answer = null;
                try { answer = DlCallStarting(req, out error); }
                catch (Exception ex) { error = ex.Message; }
                JVal result = answer;
                string why = error;
                UiPost(delegate
                {
                    if (result != null && !DlJson.Bool(result, "ok", false)) why = DlJson.Str(result, "error", Tr.S("отказ без причины", "refused without a reason"));
                    if (why != null) DlInfo(Tr.S("Не выполнено: ", "Not done: ") + why);
                    else
                    {
                        if (busy != null) DlInfo(Tr.S("Готово.", "Done."));
                        if (done != null) done(result);
                    }
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Канал открывается чуть позже мьютекса — несколько попыток подключиться.
        internal static JVal DlCallStarting(JVal req, out string error)
        {
            error = null;
            if (!DlIpc.IsRunning())
            {
                string why = DlLauncher.StartAgent();
                if (why != null) { error = Tr.S("процесс загрузок не запустился: ", "the download process did not start: ") + why; return null; }
                if (!DlLauncher.WaitRunning(5000)) { error = Tr.S("процесс загрузок не ответил за 5 секунд", "the download process did not respond within 5 seconds"); return null; }
            }
            for (int i = 0; i < 30; i++)
            {
                JVal answer = DlClient.Call(req, 1000);
                if (answer != null) return answer;
                Thread.Sleep(100);
            }
            error = Tr.S("процесс загрузок не отвечает", "the download process does not respond");
            return null;
        }

        private static JVal DlCommandFor(string cmd, string id)
        {
            JVal req = DlClient.Command(cmd);
            req.Set("id", DlJson.S(id));
            return req;
        }

        // Одна команда по всем выделенным — в одном потоке, по очереди. extra дописывает поля запроса.
        private void DlForSelected(string cmd, Action<JVal> extra)
        {
            List<string> ids = DlTargetIds();
            if (ids.Count == 0) { DlInfo(Tr.S("Отметьте или выберите загрузку в списке.", "Check or select a download in the list.")); return; }
            DlInfo(Tr.S("Выполняю…", "Working…"));
            Thread t = new Thread(delegate()
            {
                int ok = 0;
                string lastError = null;
                foreach (string id in ids)
                {
                    JVal req = DlCommandFor(cmd, id);
                    if (extra != null) extra(req);
                    string error;
                    JVal answer = DlCallStarting(req, out error);
                    if (answer != null && DlJson.Bool(answer, "ok", false)) ok++;
                    else lastError = answer != null ? DlJson.Str(answer, "error", "") : error;
                    if (answer == null) break;
                }
                int done = ok;
                string why = lastError;
                UiPost(delegate
                {
                    DlInfo(why == null ? Tr.S("Готово.", "Done.")
                                       : Tr.S("Выполнено: ", "Done: ") + done + Tr.S(" из ", " of ") + ids.Count + " · " + why);
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlTogglePause()
        {
            List<DlRow> rows = DlTargetRows();
            if (rows.Count == 0) return;
            if (DlCanPause(rows[0].Item)) DlForSelected("pause", null);
            else if (DlCanResume(rows[0].Item)) DlForSelected("resume", null);
        }

        private void DlRemoveSelected(bool withFile)
        {
            List<DlRow> rows = DlTargetRows();
            if (rows.Count == 0) { DlInfo(Tr.S("Отметьте или выберите загрузку в списке.", "Check or select a download in the list.")); return; }
            string title = Tr.S("Загрузки", "Downloads");
            string names = DlNames(rows);
            string question;
            if (withFile)
                question = Tr.S("Удалить из списка и отправить файлы в Корзину?\r\n\r\n", "Remove from the list and send the files to the Recycle Bin?\r\n\r\n") + names
                           + Tr.S("\r\n\r\nУ недокачанных в Корзину уходит частичный файл .wpcpart. На томе без Корзины файл не удаляется.",
                                  "\r\n\r\nFor unfinished ones the partial .wpcpart file goes there. On a volume without a Recycle Bin nothing is deleted.");
            else
            {
                bool partial = false;
                foreach (DlRow r in rows) if (!DlView.IsDone(r.Item) && r.Done > 0) partial = true;
                question = Tr.S("Удалить из списка?\r\n\r\n", "Remove from the list?\r\n\r\n") + names
                           + Tr.S("\r\n\r\nФайлы остаются на диске.", "\r\n\r\nFiles stay on disk.")
                           + (partial ? Tr.S(" Недокачанная часть (.wpcpart) тоже останется — удалить её можно командой «Удалить вместе с файлом».",
                                             " The unfinished part (.wpcpart) stays too — “Remove with the file” deletes it.") : "");
            }
            if (MessageBox.Show(this, question, title, MessageBoxButtons.YesNo, withFile ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                                MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            bool recycle = withFile;
            DlForSelected("remove", delegate(JVal req) { req.Set("recycle", DlJson.B(recycle)); });
        }

        private static string DlNames(List<DlRow> rows)
        {
            List<string> names = new List<string>();
            for (int i = 0; i < rows.Count && i < 8; i++) names.Add("• " + DlView.DisplayName(rows[i].Item));
            if (rows.Count > 8) names.Add(Tr.S("… и ещё ", "… and ") + (rows.Count - 8));
            return string.Join("\r\n", names.ToArray());
        }

        // ---------- режим скорости ----------

        private void DlLoadMode()
        {
            if (_cmbDlMode == null) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSettings s = DlReadSettings();
                UiPost(delegate
                {
                    _dlModeLoading = true;
                    try { _cmbDlMode.SelectedIndex = (int)s.Mode; }
                    finally { _dlModeLoading = false; }
                });
            });
        }

        // Настройки у работающего процесса, иначе — из файла (только чтение).
        private static DlSettings DlReadSettings()
        {
            if (DlIpc.IsRunning())
            {
                JVal answer = DlClient.Call(DlClient.Command("settings"), 400);
                DlSettings live = answer != null && DlJson.Bool(answer, "ok", false) ? DlSettings.FromJson(answer.Get("settings")) : null;
                if (live != null) return live;
            }
            return DlSettings.Load();
        }

        private void DlSetMode(DlSpeedMode mode)
        {
            DlChangeSettings(delegate(DlSettings s) { s.Mode = mode; },
                             Tr.S("Скорость: ", "Speed: ") + _cmbDlMode.Text);
        }

        // ---------- ссылки: буфер и перетаскивание ----------

        private void DlPasteLinks()
        {
            string text = "";
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
            catch (Exception ex) { DlInfo(Tr.S("Буфер обмена не прочитан: ", "The clipboard could not be read: ") + ex.Message); return; }
            DlDrop drop = new DlDrop();
            DlView.ParseLinks(text, drop.Links, null);
            DlView.ParseMagnets(text, drop.Magnets);
            if (drop.Links.Count == 0 && drop.Magnets.Count == 0)
            {
                DlInfo(Tr.S("В буфере обмена нет ссылок http, https или magnet.", "The clipboard has no http, https or magnet links."));
                return;
            }
            DlOpenDrop(drop);
        }

        // Что принесли буфер или перетаскивание: ссылки http(s) — в диалог добавления, торренты — каждый в свой диалог.
        private sealed class DlDrop
        {
            public readonly List<string> Links = new List<string>();
            public readonly List<string> Magnets = new List<string>();
            public readonly List<string> TorrentFiles = new List<string>();
        }

        private void DlOpenDrop(DlDrop drop)
        {
            if (drop.Links.Count > 0) DlShowAdd(string.Join("\r\n", drop.Links.ToArray()));
            if (drop.TorrentFiles.Count > 0 || drop.Magnets.Count > 0) DlShowTorrents(drop.TorrentFiles, drop.Magnets);
        }

        // Перетаскиваемое: текст со ссылками, ярлыки .url, файлы .torrent. null — ничего подходящего.
        private static DlDrop DlDropped(IDataObject data)
        {
            try
            {
                DlDrop drop = new DlDrop();
                if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                {
                    string text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
                    DlView.ParseLinks(text, drop.Links, null);
                    DlView.ParseMagnets(text, drop.Magnets);
                }
                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null)
                        foreach (string f in files)
                        {
                            if (f.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                            {
                                if (File.Exists(f) && !drop.TorrentFiles.Contains(f)) drop.TorrentFiles.Add(f);
                                continue;
                            }
                            if (!f.EndsWith(".url", StringComparison.OrdinalIgnoreCase)) continue;
                            FileInfo fi = new FileInfo(f);
                            if (!fi.Exists || fi.Length > 64 * 1024) continue;
                            string url = DlView.LinkFromShortcut(File.ReadAllText(f));
                            if (url != null && !drop.Links.Contains(url)) drop.Links.Add(url);
                        }
                }
                return drop.Links.Count == 0 && drop.Magnets.Count == 0 && drop.TorrentFiles.Count == 0 ? null : drop;
            }
            catch { return null; }
        }

        // ---------- контекстное меню ----------

        private ContextMenu DlBuildMenu()
        {
            ContextMenu menu = new ContextMenu();
            MenuItem open = new MenuItem(Tr.S("Открыть файл", "Open the file"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlOpenFile(r); });
            MenuItem folder = new MenuItem(Tr.S("Показать в папке", "Show in folder"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlShowInFolder(r); });
            MenuItem copy = new MenuItem(Tr.S("Копировать ссылку", "Copy the link"), delegate { DlCopyLink(); });
            MenuItem pause = new MenuItem(Tr.S("Пауза", "Pause"), delegate { DlForSelected("pause", null); });
            MenuItem resume = new MenuItem(Tr.S("Продолжить", "Resume"), delegate { DlForSelected("resume", null); });
            MenuItem restart = new MenuItem(Tr.S("Скачать заново с нуля…", "Download again from zero…"), delegate { DlRestart(); });
            MenuItem refresh = new MenuItem(Tr.S("Обновить ссылку…", "Refresh the link…"), delegate { DlRefreshLink(); });
            MenuItem mirror = new MenuItem(Tr.S("Добавить зеркало…", "Add a mirror…"), delegate { DlAddMirror(); });
            MenuItem recheck = new MenuItem(Tr.S("Проверить данные", "Check the data"), delegate { DlForSelected("recheck", null); });
            MenuItem reannounce = new MenuItem(Tr.S("Запросить пиров у трекеров", "Ask the trackers for peers"), delegate { DlForSelected("reannounce", null); });
            MenuItem update = new MenuItem(Tr.S("Обновить раздачу…", "Update the torrent…"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlShowUpdate(r.Item.Id, DlView.DisplayName(r.Item)); });
            MenuItem updates = new MenuItem(Tr.S("Новая версия раздачи", "New version of the torrent"));
            MenuItem updCheck = new MenuItem(Tr.S("Проверить на rutracker сейчас", "Check rutracker now"), delegate
            {
                DlRow r = DlFirstSelected();
                if (r != null)
                    DlSend(DlCommandFor("checkUpdate", r.Item.Id), Tr.S("Проверяю новую версию…", "Checking for a new version…"),
                           delegate { DlInfo(Tr.S("Проверка идёт: новая версия придёт уведомлением, итог — в журнале записи.", "Checking: a new version comes as a notification, the outcome is in the item's log.")); });
            });
            MenuItem updFile = new MenuItem(Tr.S("Взять из файла .torrent…", "Take it from a .torrent file…"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlUpdateFromFile(r.Item.Id, DlView.DisplayName(r.Item)); });
            MenuItem updTopic = new MenuItem(Tr.S("Открыть страницу темы", "Open the topic page"), delegate { DlRow r = DlFirstSelected(); if (r != null) DlOpenUrl(r.Item.TopicUrl); });
            updates.MenuItems.AddRange(new MenuItem[] { updCheck, updFile, updTopic });
            MenuItem sequential = new MenuItem(Tr.S("Качать по порядку (для просмотра на ходу)", "Download in order (to watch while downloading)"), delegate
            {
                DlRow r = DlFirstSelected();
                bool on = r == null || !r.Item.Sequential;
                DlForSelected("setSequential", delegate(JVal req) { req.Set("on", DlJson.B(on)); });
            });

            // Видео: растущий файл можно смотреть, не дожидаясь конца; у трансляции конца нет — её останавливает человек.
            MenuItem watch = new MenuItem(Tr.S("Смотреть сейчас", "Watch now"), delegate { DlWatchPreview(); });
            MenuItem stopLive = new MenuItem(Tr.S("Остановить запись", "Stop recording"), delegate { DlStopLive(); });

            MenuItem limit = new MenuItem(Tr.S("Ограничить скорость", "Limit speed"));
            foreach (int kbps in DlLimits)
            {
                int value = kbps;
                MenuItem mi = new MenuItem(DlView.Limit(kbps), delegate { DlForSelected("setLimit", delegate(JVal req) { req.Set("kbps", DlJson.N(value)); }); });
                mi.Tag = value;
                limit.MenuItems.Add(mi);
            }
            MenuItem priority = new MenuItem(Tr.S("Приоритет", "Priority"));
            foreach (int p in new[] { 1, 0, -1 })
            {
                int value = p;
                MenuItem mi = new MenuItem(p > 0 ? Tr.S("высокий", "high") : p < 0 ? Tr.S("низкий", "low") : Tr.S("обычный", "normal"),
                                           delegate { DlForSelected("setPriority", delegate(JVal req) { req.Set("priority", DlJson.N(value)); }); });
                mi.Tag = value;
                priority.MenuItems.Add(mi);
            }
            MenuItem idle = new MenuItem(Tr.S("Только когда ПК простаивает", "Only when the PC is idle"), delegate
            {
                DlRow r = DlFirstSelected();
                bool on = r == null || !r.Item.WhenIdle;
                DlForSelected("whenIdle", delegate(JVal req) { req.Set("on", DlJson.B(on)); });
            });
            MenuItem postpone = new MenuItem(Tr.S("Отложить", "Postpone"));
            foreach (int minutes in new[] { 10, 60, 180, 480 })
            {
                int value = minutes;
                postpone.MenuItems.Add(new MenuItem(Tr.S("на ", "for ") + DlView.Duration(value * 60L),
                    delegate { DlForSelected("postpone", delegate(JVal req) { req.Set("afterMinutes", DlJson.N(value)); }); }));
            }
            MenuItem unpostpone = new MenuItem(Tr.S("Не откладывать", "Don't postpone"), delegate { DlForSelected("postpone", null); });
            postpone.MenuItems.Add("-");
            postpone.MenuItems.Add(unpostpone);

            MenuItem move = new MenuItem(Tr.S("Переместить в…", "Move to…"), delegate { DlRelocate(false); });
            MenuItem copyTo = new MenuItem(Tr.S("Копировать в…", "Copy to…"), delegate { DlRelocate(true); });
            MenuItem cancelMove = new MenuItem(Tr.S("Отменить перенос", "Cancel the move"), delegate { DlForSelected("cancelMove", null); });
            MenuItem hash = new MenuItem(Tr.S("Посчитать хеш", "Compute a hash"));
            foreach (string algo in new[] { "md5", "sha1", "sha256" })
            {
                string a = algo;
                hash.MenuItems.Add(new MenuItem(algo == "md5" ? "MD5" : algo == "sha1" ? "SHA-1" : "SHA-256", delegate { DlComputeHash(a); }));
            }
            MenuItem remove = new MenuItem(Tr.S("Удалить из списка…", "Remove from the list…"), delegate { DlRemoveSelected(false); });
            MenuItem removeFile = new MenuItem(Tr.S("Удалить вместе с файлом (в Корзину)…", "Remove with the file (to the Recycle Bin)…"), delegate { DlRemoveSelected(true); });
            MenuItem history = new MenuItem(Tr.S("Очистить историю", "Clear history"));
            history.MenuItems.Add(new MenuItem(Tr.S("Готовые старше 30 дней", "Finished more than 30 days ago"), delegate { DlClearHistory(30, false); }));
            history.MenuItems.Add(new MenuItem(Tr.S("Готовые, чьих файлов уже нет", "Finished whose files are gone"), delegate { DlClearHistory(0, true); }));
            history.MenuItems.Add(new MenuItem(Tr.S("Все готовые", "All finished"), delegate { DlClearHistory(0, false); }));

            MenuItem checkAll = new MenuItem(Tr.S("Отметить все", "Check all"), delegate { DlCheckAll(true); });
            MenuItem uncheckAll = new MenuItem(Tr.S("Снять отметки", "Clear the checks"), delegate { DlCheckAll(false); });

            menu.MenuItems.AddRange(new MenuItem[] { open, folder, copy, new MenuItem("-"), pause, resume, restart, refresh, mirror, recheck, reannounce, update, updates, new MenuItem("-"),
                                                     watch, stopLive, limit, priority, idle, sequential, postpone, new MenuItem("-"), move, copyTo, cancelMove, hash, new MenuItem("-"),
                                                     checkAll, uncheckAll, new MenuItem("-"), remove, removeFile, new MenuItem("-"), history });
            menu.Popup += delegate
            {
                uncheckAll.Enabled = DlCheckedCount() > 0;
                checkAll.Enabled = _lvDl != null && _lvDl.Items.Count > 0 && DlCheckedCount() < _lvDl.Items.Count;
                List<DlRow> rows = DlTargetRows();
                DlRow first = rows.Count > 0 ? rows[0] : null;
                bool any = first != null;
                bool done = any && DlView.IsDone(first.Item);
                bool moving = any && first.Item.MoveTo.Length > 0;
                bool single = rows.Count == 1;
                // Торренту не подходят команды одной ссылки (заново, ссылка, зеркало) и перенос файла; у него свои — проверка, трекеры, порядок.
                bool torrent = any && first.Item.IsTorrent;
                bool canPause = false, canResume = false, anyMoving = false, anyUnfinished = false, allTorrents = any, allDoneTorrents = any;
                foreach (DlRow r in rows)
                {
                    if (DlCanPause(r.Item)) canPause = true;
                    if (DlCanResume(r.Item)) canResume = true;
                    if (r.Item.MoveTo.Length > 0) anyMoving = true;
                    if (!DlView.IsDone(r.Item)) anyUnfinished = true;
                    if (!r.Item.IsTorrent) allTorrents = false;
                    if (!(r.Item.IsTorrent && r.Item.State == DlState.Completed)) allDoneTorrents = false;
                }
                open.Text = torrent && first.Item.FileName.Length > 0 && Native.IsDirectoryPath(first.Item.TargetPath)
                    ? Tr.S("Открыть папку раздачи", "Open the torrent folder") : Tr.S("Открыть файл", "Open the file");
                open.Enabled = single && done && !moving;
                open.DefaultItem = open.Enabled;
                folder.Enabled = single;
                copy.Enabled = any;
                copy.Text = allTorrents ? Tr.S("Копировать magnet-ссылку", "Copy the magnet link") : Tr.S("Копировать ссылку", "Copy the link");
                pause.Enabled = canPause;
                resume.Enabled = canResume;
                resume.Text = allDoneTorrents ? Tr.S("Раздавать снова", "Seed again") : Tr.S("Продолжить", "Resume");
                restart.Visible = refresh.Visible = mirror.Visible = !allTorrents;
                restart.Enabled = single && !done && !moving && !torrent;
                refresh.Enabled = single && !done && !moving && !torrent;
                mirror.Enabled = single && !done && !torrent;
                recheck.Visible = reannounce.Visible = sequential.Visible = allTorrents;
                recheck.Enabled = allTorrents;
                reannounce.Enabled = allTorrents && (first.Item.State == DlState.Active || first.Item.State == DlState.Seeding);
                sequential.Enabled = allTorrents && anyUnfinished;
                sequential.Checked = torrent && first.Item.Sequential;
                // Смотреть можно, только когда движок действительно ведёт растущий файл; остановить — только идущую запись.
                bool media = any && single && first.Item.IsMedia && first.Item.Media != null;
                watch.Visible = media && first.Item.Media.PreviewPath.Length > 0;
                watch.Enabled = watch.Visible && !done;
                stopLive.Visible = media && first.Item.Media.Live;
                stopLive.Enabled = stopLive.Visible && !first.Item.Media.StopLive && first.Item.State == DlState.Active;
                // Новая версия — у одной раздачи: окно «что изменится» показывает одну замену.
                update.Visible = single && torrent && first.Item.UpdateHash.Length > 0;
                updates.Visible = single && torrent;
                updCheck.Enabled = single && torrent && BtTopic.RutrackerId(first.Item.TopicUrl) != null;
                updTopic.Enabled = single && torrent && first.Item.TopicUrl.Length > 0;
                limit.Enabled = priority.Enabled = postpone.Enabled = any && anyUnfinished;
                idle.Enabled = any && anyUnfinished;
                idle.Checked = any && first.Item.WhenIdle;
                foreach (MenuItem mi in limit.MenuItems) mi.Checked = any && (int)mi.Tag == first.Item.LimitKBps;
                foreach (MenuItem mi in priority.MenuItems) mi.Checked = any && (int)mi.Tag == first.Item.Priority;
                move.Visible = copyTo.Visible = hash.Visible = !allTorrents;
                move.Enabled = single && !moving && !torrent && first.Item.State != DlState.Active;
                copyTo.Enabled = single && done && !moving && !torrent;
                cancelMove.Enabled = anyMoving;
                hash.Enabled = single && done && !moving && !torrent;
                remove.Enabled = removeFile.Enabled = any;
                removeFile.Text = allTorrents ? Tr.S("Удалить вместе с данными (в Корзину)…", "Remove with the data (to the Recycle Bin)…")
                                              : Tr.S("Удалить вместе с файлом (в Корзину)…", "Remove with the file (to the Recycle Bin)…");
            };
            return menu;
        }

        // ---------- действия над одной записью ----------

        // Открытие исполняемого — только после вопроса, где видно издателя и метку «из интернета». Сам файл запускает
        // оболочка Windows, поэтому SmartScreen и предупреждение о вложении срабатывают как обычно.
        // Растущий файл видео: проигрыватель открывает его как обычный файл. Подпись не проверяем — это не программа,
        // и файл ещё не дописан; спрашивать «открыть?» на своём же куске видео незачем.
        private void DlWatchPreview()
        {
            DlRow r = DlFirstSelected();
            if (r == null || !r.Item.IsMedia || r.Item.Media == null) return;
            string path = r.Item.Media.PreviewPath;
            if (path.Length == 0) { DlInfo(Tr.S("Просмотр на ходу для этой записи не включён.", "Watching while downloading is off for this item.")); return; }
            if (!File.Exists(path)) { DlInfo(Tr.S("Файла для просмотра ещё нет — подождите первых кусков.", "There is no file to watch yet — wait for the first parts.")); return; }
            DlShellOpen(path, Path.GetDirectoryName(path));
        }

        private void DlStopLive()
        {
            DlRow r = DlFirstSelected();
            if (r == null || !r.Item.IsMedia || r.Item.Media == null || !r.Item.Media.Live) return;
            DlSend(DlCommandFor("stopLive", r.Item.Id), Tr.S("Останавливаю запись…", "Stopping the recording…"),
                   delegate { DlInfo(Tr.S("Запись останавливается: начатые куски докачаются, потом файл соберётся.",
                                          "The recording is stopping: the started parts finish, then the file is assembled.")); });
        }

        private void DlOpenFile(DlRow r)
        {
            DlItem it = r.Item;
            if (!DlView.IsDone(it) || it.MoveTo.Length > 0)
            {
                DlInfo(Tr.S("Файл ещё не готов: ", "The file is not ready yet: ") + DlView.StateText(r, DateTime.UtcNow));
                return;
            }
            string path = it.TargetPath;
            // Торрент из нескольких файлов — папка: её открывает Проводник, спрашивать нечего.
            if (it.IsTorrent && it.FileName.Length > 0 && Native.IsDirectoryPath(path)) { OpenInExplorer(path, false); return; }
            if (!File.Exists(path))
            {
                DlInfo(Tr.S("Файла больше нет на месте: ", "The file is no longer there: ") + path);
                return;
            }
            if (!DlView.OpenNeedsConfirm(it.FileName)) { DlShellOpen(path, Path.GetDirectoryName(path)); return; }
            DlInfo(Tr.S("Проверяю подпись…", "Checking the signature…"));
            string host = it.Host;
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSignatureInfo sig = DlVerify.Check(path);
                string zone = DlVerify.ZoneText(path);
                UiPost(delegate
                {
                    DlInfo("");
                    string text = it.FileName + "\r\n\r\n"
                                  + Tr.S("Это программа или сценарий: открытие выполнит его код на этом компьютере.\r\n\r\n",
                                         "This is a program or a script: opening it runs its code on this computer.\r\n\r\n")
                                  + DlVerify.Describe(sig) + "\r\n"
                                  + Tr.S("Сайт: ", "Site: ") + (host.Length > 0 ? host : "—") + "\r\n"
                                  + Tr.S("Метка: ", "Mark: ") + (zone.Length > 0 ? zone : Tr.S("нет", "none")) + "\r\n\r\n"
                                  + Tr.S("Открыть?", "Open it?");
                    MessageBoxIcon icon = sig.Kind == DlSignature.Valid ? MessageBoxIcon.Question : MessageBoxIcon.Warning;
                    if (MessageBox.Show(this, text, Tr.S("Загрузки", "Downloads"), MessageBoxButtons.YesNo, icon, MessageBoxDefaultButton.Button2) == DialogResult.Yes)
                        DlShellOpen(path, Path.GetDirectoryName(path));
                });
            });
        }

        // workDir null — без рабочей папки (ссылка, а не файл).
        private void DlShellOpen(string path, string workDir)
        {
            Thread t = new Thread(delegate()
            {
                string err = null;
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo(path);
                    psi.UseShellExecute = true;
                    if (workDir != null) psi.WorkingDirectory = workDir;
                    Process p = Process.Start(psi);
                    if (p != null) p.Dispose();
                }
                catch (Exception ex) { err = ex.Message; }
                string e = err;
                if (e != null) UiPost(delegate { DlInfo(Tr.S("Не открылось: ", "Did not open: ") + e); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlShowInFolder(DlRow r)
        {
            DlItem it = r.Item;
            string file = DlView.IsDone(it) ? it.TargetPath : it.PartPath;
            // У недокачанного торрента корень — папка с частичными файлами внутри (или один файл .wpcpart).
            if (it.IsTorrent && !DlView.IsDone(it) && !Native.PathExists(file)) file = it.TargetPath;
            if (!string.IsNullOrEmpty(it.FileName) && Native.PathExists(file)) { OpenInExplorer(file, true); return; }
            if (!string.IsNullOrEmpty(it.Folder) && Native.IsDirectoryPath(it.Folder)) { OpenInExplorer(it.Folder, false); return; }
            DlInfo(Tr.S("Ни файла, ни папки нет на месте: ", "Neither the file nor the folder is there: ") + (it.Folder ?? ""));
        }

        private void DlCopyLink()
        {
            List<string> links = new List<string>();
            foreach (DlRow r in DlSelectedRows())
            {
                DlItem it = r.Item;
                if (it.IsTorrent) links.Add(DlView.MagnetLink(it, it.Url.Length > 0 ? null : DlEngine.LoadTorrentMeta(DlPaths.TorrentsDir, it.InfoHash)));
                else links.Add(it.OriginalUrl.Length > 0 ? it.OriginalUrl : it.Url);
            }
            if (links.Count == 0) return;
            try
            {
                Clipboard.SetText(string.Join("\r\n", links.ToArray()));
                DlInfo(Tr.N(links.Count, "Скопирована ссылка", "Скопированы ссылки", "Скопировано ссылок", "Link copied", "Links copied")
                       + (links.Count > 1 ? ": " + links.Count : "."));
            }
            catch (Exception ex) { DlInfo(ex.Message); }
        }

        private void DlRestart()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            if (!MsgAsk(Tr.S("Скачать «", "Download “") + DlView.DisplayName(r.Item) + Tr.S("» заново с нуля?\r\n\r\nУже скачанная часть (", "” again from zero?\r\n\r\nThe part already downloaded (")
                        + Engine.FormatBytes(r.Done) + Tr.S(") будет удалена.", ") will be deleted."), Tr.S("Загрузки", "Downloads"))) return;
            DlSend(DlCommandFor("restart", r.Item.Id), Tr.S("Начинаю заново…", "Restarting…"), null);
        }

        private void DlRefreshLink()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            string url = DlPromptText(Tr.S("Обновить ссылку", "Refresh the link"),
                                      Tr.S("Новая ссылка на тот же файл. Скачанное сохранится, если сервер отдаёт файл того же размера:",
                                           "A new link to the same file. What was downloaded is kept if the server returns a file of the same size:"),
                                      "");
            if (url == null) return;
            if (!DlHttp.IsAllowedScheme(url.Trim())) { DlInfo(Tr.S("Это не ссылка http или https.", "This is not an http or https link.")); return; }
            JVal req = DlCommandFor("refreshLink", r.Item.Id);
            req.Set("url", DlJson.S(url.Trim()));
            DlSend(req, Tr.S("Обновляю ссылку…", "Refreshing the link…"), null);
        }

        private void DlAddMirror()
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            string url = DlPromptText(Tr.S("Зеркало", "Mirror"),
                                      Tr.S("Другая ссылка на тот же файл — на неё загрузка переключится, если основной сервер откажет:",
                                           "Another link to the same file — the download switches to it if the main server fails:"),
                                      "");
            if (url == null) return;
            if (!DlHttp.IsAllowedScheme(url.Trim())) { DlInfo(Tr.S("Это не ссылка http или https.", "This is not an http or https link.")); return; }
            JVal req = DlCommandFor("addMirror", r.Item.Id);
            req.Set("url", DlJson.S(url.Trim()));
            DlSend(req, Tr.S("Добавляю зеркало…", "Adding the mirror…"), null);
        }

        private void DlRelocate(bool copy)
        {
            DlRow r = DlFirstSelected();
            if (r == null) return;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = copy ? Tr.S("Куда положить копию", "Where to put the copy") : Tr.S("Куда перенести файл загрузки", "Where to move the download's file");
                dlg.ShowNewFolderButton = true;
                try { if (Directory.Exists(r.Item.Folder)) dlg.SelectedPath = r.Item.Folder; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                JVal req = DlCommandFor("move", r.Item.Id);
                req.Set("folder", DlJson.S(dlg.SelectedPath));
                req.Set("copy", DlJson.B(copy));
                DlSend(req, copy ? Tr.S("Копирую…", "Copying…") : Tr.S("Переношу…", "Moving…"), null);
            }
        }

        private void DlComputeHash(string algo)
        {
            DlRow r = DlFirstSelected();
            if (r == null || r.Item.State != DlState.Completed) return;
            string path = r.Item.TargetPath;
            string title = algo == "md5" ? "MD5" : algo == "sha1" ? "SHA-1" : "SHA-256";
            string expected = r.Item.ExpectedHash;
            DlInfo(Tr.S("Считаю ", "Computing ") + title + "…");
            Thread t = new Thread(delegate()
            {
                string hex = DlFinish.Hash(path, algo, delegate { return _closing; });
                UiPost(delegate
                {
                    if (hex == null) { DlInfo(title + Tr.S(" не посчитан: файл не прочитан.", " not computed: the file could not be read.")); return; }
                    string verdict = "";
                    string eAlgo, eHex;
                    if (DlFinish.ParseExpected(expected, out eAlgo, out eHex) && eAlgo == algo)
                        verdict = string.Equals(eHex, hex, StringComparison.OrdinalIgnoreCase)
                            ? Tr.S(" · совпадает с ожидаемым", " · matches the expected one")
                            : Tr.S(" · НЕ совпадает с ожидаемым", " · does NOT match the expected one");
                    try { Clipboard.SetText(hex); } catch { }
                    DlInfo(title + ": " + hex + Tr.S(" (скопирован в буфер)", " (copied to the clipboard)") + verdict);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlClearHistory(int days, bool missingOnly)
        {
            string what = missingOnly ? Tr.S("готовые загрузки, чьих файлов уже нет", "finished downloads whose files are gone")
                        : days > 0 ? Tr.S("готовые загрузки старше ", "finished downloads older than ") + days + Tr.S(" дней", " days")
                        : Tr.S("все готовые загрузки", "all finished downloads");
            if (!MsgAsk(Tr.S("Убрать из списка ", "Remove from the list: ") + what + Tr.S("?\r\n\r\nФайлы на диске не трогаются.", "?\r\n\r\nFiles on disk are not touched."),
                        Tr.S("Загрузки", "Downloads"))) return;
            JVal req = DlClient.Command("clearHistory");
            req.Set("days", DlJson.N(days));
            req.Set("missingOnly", DlJson.B(missingOnly));
            DlSend(req, Tr.S("Очищаю историю…", "Clearing history…"), delegate(JVal answer)
            {
                DlInfo(Tr.S("Убрано записей: ", "Records removed: ") + DlJson.Int(answer, "removed", 0));
            });
        }

        // ---------- «Главная» ----------

        // Строка проверки состояния. null — загрузками не пользовались. Вызывается из рабочего потока проверки.
        private HealthItem HealthDownloadsItem()
        {
            DlSnapshot s = null;
            try
            {
                if (DlIpc.IsRunning()) s = DlSnapshot.FromList(DlClient.Call(DlClient.Command("list"), 500));
                if (s == null) s = DlSnapshot.FromStore(new DlStore(DlPaths.DataDir).LoadAll());
            }
            catch (Exception ex) { DlLog.Report(ex); }
            if (s == null || s.Rows.Count == 0) return null;
            HealthItem h = new HealthItem();
            h.Id = "dlqueue";
            h.Title = Tr.S("Очередь загрузок", "Download queue");
            h.Action = Tr.S("Открыть «Загрузки»", "Open “Downloads”");
            h.ActionKind = "page:Downloads";
            int running = DlView.Count(s, DlStateFilter.Running), waiting = DlView.Count(s, DlStateFilter.Waiting), errors = DlView.Count(s, DlStateFilter.Errors);

            // Мало места там, куда ещё качается: остаток больше свободного или свободно меньше гигабайта.
            Dictionary<string, long> need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (DlRow r in s.Rows)
            {
                DlItem it = r.Item;
                if (it.State == DlState.Completed || it.State == DlState.Failed || string.IsNullOrEmpty(it.Folder)) continue;
                string root;
                try { root = Path.GetPathRoot(it.Folder); } catch { continue; }
                if (string.IsNullOrEmpty(root)) continue;
                long left = it.Total > 0 ? Math.Max(0, it.Total - r.Done) : 0;
                long sum;
                need[root] = need.TryGetValue(root, out sum) ? sum + left : left;
            }
            string lowSpace = null;
            foreach (KeyValuePair<string, long> kv in need)
            {
                long free = DlFiles.FreeSpace(kv.Key);
                if (free >= 0 && (free < kv.Value || free < 1024L * 1024 * 1024))
                    lowSpace = kv.Key.TrimEnd('\\') + Tr.S(" — свободно ", " — free ") + Engine.FormatBytes(free)
                               + (kv.Value > 0 ? Tr.S(", осталось докачать ", ", still to download ") + Engine.FormatBytes(kv.Value) : "");
            }

            List<string> parts = new List<string>();
            if (running > 0) parts.Add(Tr.S("качаются: ", "downloading: ") + running + (s.Speed > 0 ? " (" + DlView.Speed(s.Speed) + ")" : ""));
            if (waiting > 0) parts.Add(Tr.S("ждут: ", "waiting: ") + waiting);
            if (errors > 0) parts.Add(Tr.S("с ошибкой: ", "failed: ") + errors);
            if (!s.Live && waiting > 0) parts.Add(Tr.S("процесс загрузок не запущен", "the download process is not running"));
            if (lowSpace != null) parts.Insert(0, Tr.S("мало места: ", "low space: ") + lowSpace);
            if (parts.Count == 0) parts.Add(Tr.S("всё скачано", "everything is downloaded"));
            h.Detail = string.Join(" · ", parts.ToArray());
            h.Level = lowSpace != null ? HealthLevel.Warn : errors > 0 || (!s.Live && waiting > 0) ? HealthLevel.Info : HealthLevel.Ok;
            if (h.Level == HealthLevel.Ok && running == 0 && waiting == 0) { h.Action = null; h.ActionKind = null; }
            return h;
        }
    }
}
