// Windows Process Cleaner — вкладка «Загрузки»: диалог добавления ссылок и маленький диалог ввода строки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Диалог только собирает параметры: проверяет ссылки, папку и хеш окончательно процесс загрузок — тем же кодом, что и
// запросы от расширений браузеров. Повтор уже скачиваемой ссылки добавляется только после отдельного вопроса.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private const int DlStartNow = 0, DlStartPaused = 1, DlStartAfter = 2, DlStartAt = 3, DlStartIdle = 4;

        // Параметры одного добавления: общие для всех ссылок, имя/хеш/зеркала — только при одной ссылке.
        private sealed class DlAddPlan
        {
            public readonly List<string> Links = new List<string>();
            public readonly List<string> Magnets = new List<string>();   // уходят в диалог торрента, каждая в свой
            public string Folder = "", Name = "", Hash = "";
            public readonly List<string> Mirrors = new List<string>();
            public int Start, AfterMinutes, LimitKBps, Connections, Priority;
            public DateTime AtUtc = DateTime.MinValue;
            public bool AsMedia;                                         // качать как видео: поток, страница или файл-видео
        }

        // Вид записи по самой ссылке: плейлист потока виден по расширению, всё остальное решает движок.
        private static string DlMediaKindOf(string url)
        {
            string path;
            try { path = new Uri(url).AbsolutePath.ToLowerInvariant(); }
            catch { return "file"; }
            if (path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".m3u", StringComparison.Ordinal)) return "hls";
            if (path.EndsWith(".mpd", StringComparison.Ordinal)) return "dash";
            return DlLooksLikeFile(path) ? "file" : "page";
        }

        // Путь с расширением известного медиафайла — прямой файл; иначе это страница, и её разбирает yt-dlp.
        private static bool DlLooksLikeFile(string path)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0 || path.Length - dot > 6) return false;
            string ext = path.Substring(dot);
            foreach (string known in new[] { ".mp4", ".m4v", ".webm", ".mkv", ".mov", ".ts", ".m4a", ".mp3", ".aac", ".opus", ".flac", ".wav", ".ogg" })
                if (ext == known) return true;
            return false;
        }

        private void DlShowAdd(string initialText)
        {
            DlAddPlan plan = DlAskAdd(initialText ?? "");
            if (plan == null) return;
            if (plan.Links.Count > 0) DlSubmit(plan);
            if (plan.Magnets.Count > 0) DlShowTorrents(null, plan.Magnets);
        }

        private DlAddPlan DlAskAdd(string initialText)
        {
            DlSettings settings = DlSettings.Load();
            Form dlg = new Form();
            dlg.Text = Tr.S("Добавить загрузку", "Add a download");
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 760; dlg.Height = 720;
            dlg.MinimumSize = new Size(620, 560);
            dlg.MinimizeBox = false; dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            scroll.Padding = new Padding(14, 12, 14, 0);
            FlowLayoutPanel body = new FlowLayoutPanel();
            body.Location = new Point(14, 12);
            body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.FlowDirection = FlowDirection.TopDown;
            body.WrapContents = false;
            scroll.Controls.Add(body);

            body.Controls.Add(DlDialogLabel(Tr.S("Ссылки http, https или magnet — по одной в строке:", "http, https or magnet links — one per line:"), false));
            TextBox links = new TextBox();
            links.Multiline = true;
            links.ScrollBars = ScrollBars.Vertical;
            links.WordWrap = false;
            links.AcceptsReturn = true;
            links.Text = initialText;
            Panel linksBox = MkBox(links, new Padding(6, 4, 2, 4));
            linksBox.Size = new Size(700, 130);
            linksBox.Margin = new Padding(0, 2, 0, 4);
            body.Controls.Add(linksBox);
            Label parsed = DlDialogLabel("", true);
            body.Controls.Add(parsed);

            body.Controls.Add(DlDialogLabel(Tr.S("Папка:", "Folder:"), false));
            FlowLayoutPanel folderRow = DlDialogRow(body);
            TextBox folder = new TextBox();
            folder.Width = 420;
            folder.Margin = new Padding(0, 7, 8, 6);
            folderRow.Controls.Add(folder);
            Button browse = MkFlowButton(Tr.S("Выбрать…", "Choose…"), 110, false);
            Button last = MkFlowButton(Tr.S("Последняя", "Last used"), 110, false);
            folderRow.Controls.Add(browse);
            folderRow.Controls.Add(last);
            Label folderNote = DlDialogLabel("", true);
            body.Controls.Add(folderNote);
            string lastFolder = MemGet(DlScope, "lastFolder", true) ?? "";
            last.Enabled = lastFolder.Length > 0;
            browse.Click += delegate
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.Description = Tr.S("Куда сохранить", "Where to save");
                    fb.ShowNewFolderButton = true;
                    string initial = folder.Text.Trim().Length > 0 ? folder.Text.Trim() : settings.EffectiveFolder;
                    try { if (Directory.Exists(initial)) fb.SelectedPath = initial; } catch { }
                    if (fb.ShowDialog(dlg) == DialogResult.OK && !string.IsNullOrEmpty(fb.SelectedPath)) folder.Text = fb.SelectedPath;
                }
            };
            last.Click += delegate { folder.Text = lastFolder; };

            body.Controls.Add(DlDialogLabel(Tr.S("Имя файла (только для одной ссылки; пусто — как отдаст сервер):", "File name (single link only; empty — as the server names it):"), false));
            TextBox name = new TextBox();
            name.Width = 700;
            name.Margin = new Padding(0, 2, 0, 8);
            body.Controls.Add(name);

            FlowLayoutPanel startRow = DlDialogRow(body);
            startRow.Controls.Add(MkFlowLabel(Tr.S("Начать:", "Start:"), false));
            RoundComboBox start = DlCombo(startRow, 210, Tr.S("сразу", "now"), Tr.S("на паузе", "paused"), Tr.S("через … минут", "in … minutes"),
                                          Tr.S("в указанное время", "at a set time"), Tr.S("когда ПК простаивает", "when the PC is idle"));
            NumericUpDown after = new NumericUpDown();
            after.Minimum = 1; after.Maximum = 24 * 60; after.Value = 30; after.Width = 80;
            after.Margin = new Padding(0, 6, 12, 8);
            startRow.Controls.Add(after);
            string[] times = new string[48];
            for (int i = 0; i < 48; i++) times[i] = (i / 2).ToString("00") + ":" + (i % 2 == 0 ? "00" : "30");
            RoundComboBox at = DlCombo(startRow, 90, times);
            at.SelectedIndex = Math.Min(47, (DateTime.Now.Hour + 1) % 24 * 2);

            FlowLayoutPanel optRow = DlDialogRow(body);
            optRow.Controls.Add(MkFlowLabel(Tr.S("Лимит:", "Limit:"), false));
            string[] limitItems = new string[DlLimits.Length];
            for (int i = 0; i < DlLimits.Length; i++) limitItems[i] = DlView.Limit(DlLimits[i]);
            RoundComboBox limit = DlCombo(optRow, 140, limitItems);
            optRow.Controls.Add(MkFlowLabel(Tr.S("Потоков:", "Streams:"), false));
            int[] connValues = { 0, 1, 2, 4, 8, 16 };
            RoundComboBox conns = DlCombo(optRow, 150, Tr.S("из настроек (", "from settings (") + settings.Segments + ")", "1", "2", "4", "8", "16");
            optRow.Controls.Add(MkFlowLabel(Tr.S("Приоритет:", "Priority:"), false));
            RoundComboBox priority = DlCombo(optRow, 120, Tr.S("высокий", "high"), Tr.S("обычный", "normal"), Tr.S("низкий", "low"));
            priority.SelectedIndex = 1;

            CheckBox asMedia = new CheckBox();
            asMedia.Text = Tr.S("Скачать как видео: поток (m3u8, mpd) или страница видеосайта",
                                "Download as video: a stream (m3u8, mpd) or a video site page");
            asMedia.AutoSize = true;
            asMedia.Margin = new Padding(0, 4, 0, 4);
            body.Controls.Add(asMedia);
            Label mediaNote = DlDialogLabel(Tr.S("Качество и контейнер берутся из настроек загрузок. Для страниц нужен yt-dlp — он ставится там же, кнопкой.",
                                                 "Quality and container come from the download settings. Pages need yt-dlp — installed there, by a button."), true);
            body.Controls.Add(mediaNote);

            body.Controls.Add(DlDialogLabel(Tr.S("Ожидаемый хеш (только для одной ссылки): sha256:…, sha1:…, md5:… или просто шестнадцатеричная строка",
                                                 "Expected hash (single link only): sha256:…, sha1:…, md5:… or just the hex string"), false));
            TextBox hash = new TextBox();
            hash.Width = 700;
            hash.Margin = new Padding(0, 2, 0, 8);
            body.Controls.Add(hash);

            body.Controls.Add(DlDialogLabel(Tr.S("Зеркала — другие ссылки на тот же файл, по одной в строке (только для одной ссылки):",
                                                 "Mirrors — other links to the same file, one per line (single link only):"), false));
            TextBox mirrors = new TextBox();
            mirrors.Multiline = true;
            mirrors.ScrollBars = ScrollBars.Vertical;
            mirrors.WordWrap = false;
            mirrors.AcceptsReturn = true;
            Panel mirrorsBox = MkBox(mirrors, new Padding(6, 4, 2, 4));
            mirrorsBox.Size = new Size(700, 66);
            mirrorsBox.Margin = new Padding(0, 2, 0, 4);
            body.Controls.Add(mirrorsBox);

            // Ширину панели — до якорей: иначе Right отсчитывается от ширины по умолчанию (200) и кнопки уезжают за край.
            int cw = dlg.ClientSize.Width;
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 62; bottom.Width = cw;
            Label problem = MkNote("", false);
            problem.Dock = DockStyle.None;
            problem.Name = "warn";
            problem.Left = 14; problem.Top = 14; problem.Width = cw - 14 - 120 - 8 - 110 - 14 - 16; problem.Height = 34;
            Button ok = MkButton(Tr.S("Добавить", "Add"), cw - 14 - 110 - 8 - 120, 12, 120, true);
            Button cancel = MkButton(Tr.S("Отмена", "Cancel"), cw - 14 - 110, 12, 110, false);
            ok.Anchor = cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            problem.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bottom.Controls.Add(problem);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            dlg.Controls.Add(scroll);
            dlg.Controls.Add(bottom);

            List<string> goodLinks = new List<string>();
            List<string> magnets = new List<string>();
            bool mediaTouched = false, mediaSetting = false;
            EventHandler update = delegate
            {
                goodLinks.Clear();
                magnets.Clear();
                List<string> bad = new List<string>();
                DlView.ParseLinks(links.Text, goodLinks, bad);
                DlView.ParseMagnets(links.Text, magnets);
                // Строка с magnet-ссылкой — не мусор: она уйдёт в диалог торрента.
                bad.RemoveAll(delegate(string line) { List<string> m = new List<string>(); DlView.ParseMagnets(line, m); return m.Count > 0; });
                string text = Tr.S("Ссылок: ", "Links: ") + goodLinks.Count;
                if (magnets.Count > 0) text += Tr.S(" · торрентов: ", " · torrents: ") + magnets.Count + Tr.S(" (откроются в окне торрента)", " (open in the torrent window)");
                if (bad.Count > 0) text += Tr.S(" · не ссылки (пропущены): ", " · not links (skipped): ") + bad.Count + " — «" + DlShort(bad[0], 60) + "»";
                parsed.Text = text;
                bool single = goodLinks.Count == 1;
                // Плейлист потока распознаётся по ссылке — ставим галку сам, пока человек её не трогал руками.
                if (!mediaTouched)
                {
                    bool allStreams = goodLinks.Count > 0;
                    foreach (string u in goodLinks)
                    {
                        string kind = DlMediaKindOf(u);
                        if (kind != "hls" && kind != "dash") { allStreams = false; break; }
                    }
                    if (allStreams != asMedia.Checked)
                    {
                        mediaSetting = true;
                        asMedia.Checked = allStreams;
                        mediaSetting = false;
                    }
                }
                mediaNote.Visible = asMedia.Checked;
                // У видео нет одной ссылки на файл: хеш и зеркала к нему неприменимы.
                name.Enabled = single;
                hash.Enabled = mirrors.Enabled = single && !asMedia.Checked;
                after.Visible = start.SelectedIndex == DlStartAfter;
                at.Visible = start.SelectedIndex == DlStartAt;

                string chosen = folder.Text.Trim();
                string target;
                if (chosen.Length > 0) target = chosen;
                else if (goodLinks.Count > 0)
                {
                    string guess = name.Text.Trim().Length > 0 && single ? name.Text.Trim() : DlFiles.NameFromUrl(goodLinks[0]);
                    target = DlFiles.FolderFor(settings, goodLinks[0], guess);
                }
                else target = settings.EffectiveFolder;
                string note = chosen.Length > 0 ? "" : Tr.S("Пусто — по правилам настроек: ", "Empty — by the settings rules: ") + target
                                                       + (goodLinks.Count > 1 ? Tr.S(" (для первой ссылки)", " (for the first link)") : "");
                long free = DlFiles.FreeSpace(target);
                if (free >= 0)
                {
                    note += (note.Length > 0 ? " · " : "") + Tr.S("свободно ", "free ") + Engine.FormatBytes(free);
                    if (free < 1024L * 1024 * 1024) note += Tr.S(" — места мало", " — low on space");
                }
                folderNote.Text = note;
                folderNote.Name = free >= 0 && free < 1024L * 1024 * 1024 ? "warn" : "muted";
                folderNote.ForeColor = folderNote.Name == "warn" ? DlWarnColor() : _theme.Subtle;
                problem.Text = "";
            };
            // Галка — такой же повод пересобрать диалог, как и ссылки: от неё зависят подсказка про качество и
            // доступность хеша с зеркалами. Без этого вызова они оставались прежними до правки любого другого поля.
            asMedia.CheckedChanged += delegate(object s, EventArgs e)
            {
                if (!mediaSetting) mediaTouched = true;
                update(s, e);
            };
            links.TextChanged += update;
            folder.TextChanged += update;
            name.TextChanged += update;
            start.SelectedIndexChanged += update;
            start.SelectedIndex = DlStartNow;
            limit.SelectedIndex = 0;
            conns.SelectedIndex = 0;

            DlAddPlan plan = null;
            ok.DialogResult = DialogResult.None;
            ok.Click += delegate
            {
                update(null, EventArgs.Empty);
                if (goodLinks.Count == 0 && magnets.Count == 0) { problem.Text = Tr.S("Нет ни одной ссылки http, https или magnet.", "There is no http, https or magnet link."); return; }
                bool single = goodLinks.Count == 1;
                string hashText = single ? hash.Text.Trim() : "";
                if (hashText.Length > 0)
                {
                    string algo, hex;
                    if (!DlFinish.ParseExpected(hashText, out algo, out hex)) { problem.Text = Tr.S("Хеш не разобран: sha256:…, sha1:…, md5:…", "The hash is not recognised: sha256:…, sha1:…, md5:…"); return; }
                    hashText = algo + ":" + hex;
                }
                DlAddPlan p = new DlAddPlan();
                p.Links.AddRange(goodLinks);
                p.Magnets.AddRange(magnets);
                p.Folder = folder.Text.Trim();
                p.Name = single ? name.Text.Trim() : "";
                p.Hash = hashText;
                if (single)
                {
                    List<string> badMirrors = new List<string>();
                    DlView.ParseLinks(mirrors.Text, p.Mirrors, badMirrors);
                    if (badMirrors.Count > 0) { problem.Text = Tr.S("Зеркало не ссылка: ", "A mirror is not a link: ") + DlShort(badMirrors[0], 50); return; }
                }
                p.Start = start.SelectedIndex;
                p.AfterMinutes = (int)after.Value;
                if (p.Start == DlStartAt)
                {
                    DateTime now = DateTime.Now;
                    DateTime when = now.Date.AddMinutes(at.SelectedIndex * 30);
                    if (when <= now) when = when.AddDays(1);
                    p.AtUtc = when.ToUniversalTime();
                }
                p.LimitKBps = DlLimits[Math.Max(0, limit.SelectedIndex)];
                p.Connections = connValues[Math.Max(0, conns.SelectedIndex)];
                p.Priority = 1 - Math.Max(0, priority.SelectedIndex);
                p.AsMedia = asMedia.Checked;
                plan = p;
                dlg.DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            update(null, EventArgs.Empty);
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            dlg.Shown += delegate { links.Focus(); links.SelectionStart = links.TextLength; };
            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            dlg.Dispose();
            if (dr != DialogResult.OK || plan == null) return null;
            if (plan.Folder.Length > 0) MemSet(DlScope, "lastFolder", plan.Folder, true);
            return plan;
        }

        private static string DlShort(string text, int max)
        {
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private Label DlDialogLabel(string text, bool muted)
        {
            Label l = DlLabel(text, muted);
            l.MaximumSize = new Size(700, 0);
            l.Margin = new Padding(0, muted ? 0 : 4, 0, 4);
            return l;
        }

        private static FlowLayoutPanel DlDialogRow(FlowLayoutPanel body)
        {
            FlowLayoutPanel f = DlFlowRow(body);
            f.MaximumSize = new Size(720, 0);
            return f;
        }

        private static JVal DlAddCommand(DlAddPlan p, string url, bool allowDuplicate)
        {
            // Видео идёт своей командой: у него нет ни хеша, ни зеркал, а выбор качества делает движок по настройкам.
            if (p.AsMedia)
            {
                JVal m = DlClient.Command("addMedia");
                m.Set("url", DlJson.S(url));
                m.Set("kind", DlJson.S(DlMediaKindOf(url)));
                m.Set("source", DlJson.S("manual"));
                if (p.Folder.Length > 0) m.Set("folder", DlJson.S(p.Folder));
                if (p.Priority != 0) m.Set("priority", DlJson.N(p.Priority));
                if (p.Start == DlStartPaused) m.Set("paused", DlJson.B(true));
                return m;
            }
            JVal req = DlClient.Command("add");
            req.Set("url", DlJson.S(url));
            req.Set("source", DlJson.S("manual"));
            if (p.Folder.Length > 0) req.Set("folder", DlJson.S(p.Folder));
            if (p.Name.Length > 0) req.Set("name", DlJson.S(p.Name));
            if (p.Hash.Length > 0) req.Set("expectedHash", DlJson.S(p.Hash));
            if (p.Mirrors.Count > 0) req.Set("mirrors", DlJson.Strings(p.Mirrors));
            if (p.LimitKBps > 0) req.Set("limitKBps", DlJson.N(p.LimitKBps));
            if (p.Connections > 0) req.Set("connections", DlJson.N(p.Connections));
            if (p.Priority != 0) req.Set("priority", DlJson.N(p.Priority));
            if (p.Start == DlStartPaused) req.Set("paused", DlJson.B(true));
            else if (p.Start == DlStartAfter) req.Set("afterMinutes", DlJson.N(p.AfterMinutes));
            else if (p.Start == DlStartAt) req.Set("at", DlJson.D(p.AtUtc));
            else if (p.Start == DlStartIdle) req.Set("whenIdle", DlJson.B(true));
            if (allowDuplicate) req.Set("allowDuplicate", DlJson.B(true));
            return req;
        }

        // Ссылки по одной в фоне. Повторы собираются и спрашиваются одним вопросом в конце.
        private void DlSubmit(DlAddPlan plan)
        {
            DlInfo(Tr.S("Добавляю…", "Adding…"));
            Thread t = new Thread(delegate()
            {
                int added = 0;
                List<string> duplicates = new List<string>();
                List<string> errors = new List<string>();
                foreach (string url in plan.Links)
                {
                    string error;
                    JVal answer = DlCallStarting(DlAddCommand(plan, url, false), out error);
                    if (answer == null) { errors.Add(error); break; }
                    if (DlJson.Bool(answer, "ok", false)) added++;
                    else if (DlJson.Str(answer, "duplicateOf", "").Length > 0) duplicates.Add(url);
                    else errors.Add(DlLog.Redact(url) + " — " + DlJson.Str(answer, "error", ""));
                }
                int addedCopy = added;
                UiPost(delegate { DlSubmitDone(plan, addedCopy, duplicates, errors); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DlSubmitDone(DlAddPlan plan, int added, List<string> duplicates, List<string> errors)
        {
            string text = Tr.S("Добавлено: ", "Added: ") + added;
            if (errors.Count > 0) text += Tr.S(" · не добавлено: ", " · not added: ") + errors.Count + " (" + errors[0] + ")";
            DlInfo(text);
            DlPoll();
            if (duplicates.Count == 0 || _closing) return;
            List<string> names = new List<string>();
            for (int i = 0; i < duplicates.Count && i < 6; i++) names.Add("• " + DlShort(DlLog.Redact(duplicates[i]), 90));
            if (duplicates.Count > 6) names.Add(Tr.S("… и ещё ", "… and ") + (duplicates.Count - 6));
            if (!MsgAsk(Tr.S("Эти ссылки уже есть в списке загрузок:\r\n\r\n", "These links are already in the download list:\r\n\r\n")
                        + string.Join("\r\n", names.ToArray())
                        + Tr.S("\r\n\r\nСкачать ещё раз? Новый файл получит другое имя.", "\r\n\r\nDownload again? The new file gets a different name."),
                        Tr.S("Загрузки", "Downloads"))) return;
            DlAddPlan again = new DlAddPlan();
            again.Links.AddRange(duplicates);
            again.Folder = plan.Folder; again.Name = plan.Name; again.Hash = plan.Hash;
            again.Mirrors.AddRange(plan.Mirrors);
            again.Start = plan.Start; again.AfterMinutes = plan.AfterMinutes; again.AtUtc = plan.AtUtc;
            again.LimitKBps = plan.LimitKBps; again.Connections = plan.Connections; again.Priority = plan.Priority;
            Thread t = new Thread(delegate()
            {
                int ok = 0;
                string last = null;
                foreach (string url in again.Links)
                {
                    string error;
                    JVal answer = DlCallStarting(DlAddCommand(again, url, true), out error);
                    if (answer != null && DlJson.Bool(answer, "ok", false)) ok++;
                    else last = answer != null ? DlJson.Str(answer, "error", "") : error;
                    if (answer == null) break;
                }
                int okCopy = ok;
                string lastCopy = last;
                UiPost(delegate
                {
                    DlInfo(Tr.S("Добавлено повторно: ", "Added again: ") + okCopy + (lastCopy != null ? " · " + lastCopy : ""));
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Одна строка текста. null — отмена.
        private string DlPromptText(string title, string prompt, string initial)
        {
            Form dlg = new Form();
            dlg.Text = title;
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 620; dlg.Height = 220;
            dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
            dlg.MinimizeBox = false; dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;

            Label l = new Label();
            l.Text = prompt;
            l.Left = 14; l.Top = 12; l.Width = 574; l.Height = 46;
            TextBox box = new TextBox();
            box.Left = 14; box.Top = 62; box.Width = 574;
            box.Text = initial ?? "";
            Button ok = MkButton(Tr.S("ОК", "OK"), 350, 110, 110, true);
            Button cancel = MkButton(Tr.S("Отмена", "Cancel"), 478, 110, 110, false);
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            dlg.Controls.AddRange(new Control[] { l, box, ok, cancel });
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            string text = box.Text;
            dlg.Dispose();
            return dr == DialogResult.OK ? text : null;
        }
    }
}
