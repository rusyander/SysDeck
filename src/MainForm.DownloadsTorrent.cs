// SysDeck — вкладка «Загрузки»: диалог добавления торрента (файл .torrent или magnet-ссылка).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Диалог разбирает торрент сам — чтобы показать файлы до добавления, — но окончательно проверяет его процесс загрузок
// (размер, хеш, повтор, папка, свободное имя). У magnet-ссылки файлов до метаданных нет: качаются все, выбор меняется потом
// на вкладке «Файлы» карточки.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Downloads;

namespace SysDeck
{
    public partial class MainForm
    {
        // До этого размера файл торрента уходит процессу в самой команде (base64 укладывается в кадр канала), больше — путём.
        private const int DlTorrentInlineBytes = 8 * 1024 * 1024;

        private string _dlStartTorrent;

        // /torrent при запуске окна; от повторного запуска — OpenTorrent.
        public void SetTorrentStart(string arg)
        {
            _dlStartTorrent = arg;
            _dlStartPage = true;
        }

        private void DlOpenStartTorrent()
        {
            string arg = _dlStartTorrent;
            _dlStartTorrent = null;
            if (arg != null) DlOpenTorrentArgument(arg);
        }

        public void OpenTorrent(string arg)
        {
            OpenDownloads();
            DlOpenTorrentArgument(arg);
        }

        // Аргумент пришёл через порт активации — любой локальный процесс мог его прислать: проверяется заново, а добавляет всё равно человек.
        private void DlOpenTorrentArgument(string arg)
        {
            string ok = BtAssoc.ValidateOpenArgument(arg);
            if (ok == null) return;
            if (ok.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) DlShowTorrents(null, new List<string> { ok });
            else DlShowTorrents(new List<string> { ok }, null);
        }

        private sealed class DlTorrentPlan
        {
            public string File = "", Magnet = "", Folder = "", RootName = "", Title = "";
            public byte[] Bytes;
            public int[] Priorities;              // null — все файлы
            public bool Paused, Sequential, UseExisting;
            public int Priority;
            public string OnTopicMatch = DlEngine.TopicMatchAsk;
        }

        // Кнопка «Торрент…»: несколько файлов — по диалогу на каждый.
        private void DlPickTorrentFiles()
        {
            string[] files;
            using (OpenFileDialog od = new OpenFileDialog())
            {
                od.Title = Tr.S("Добавить торрент", "Add a torrent");
                od.Filter = Tr.S("Торренты (*.torrent)|*.torrent|Все файлы|*.*", "Torrents (*.torrent)|*.torrent|All files|*.*");
                od.Multiselect = true;
                if (od.ShowDialog(this) != DialogResult.OK) return;
                files = od.FileNames;
            }
            DlShowTorrents(new List<string>(files), null);
        }

        // Очередь диалогов: файлы, затем magnet-ссылки. Отмена одного не отменяет остальные.
        private void DlShowTorrents(List<string> files, List<string> magnets)
        {
            if (files != null)
                foreach (string f in files)
                {
                    DlTorrentPlan plan = DlAskTorrent(f, null);
                    if (plan != null) DlSubmitTorrent(plan);
                }
            if (magnets != null)
                foreach (string m in magnets)
                {
                    DlTorrentPlan plan = DlAskTorrent(null, m);
                    if (plan != null) DlSubmitTorrent(plan);
                }
        }

        private DlTorrentPlan DlAskTorrent(string file, string magnetLink)
        {
            BtMeta meta = null;
            BtMagnet magnet = null;
            byte[] bytes = null;
            string err;
            if (file != null)
            {
                try
                {
                    FileInfo fi = new FileInfo(file);
                    if (!fi.Exists) { DlInfo(Tr.S("Файла нет: ", "The file does not exist: ") + file); return null; }
                    if (fi.Length > DlEngine.BtMaxTorrentBytes) { DlInfo(Tr.S("Файл .torrent больше 16 МБ: ", "The .torrent file is larger than 16 MB: ") + fi.Name); return null; }
                    bytes = File.ReadAllBytes(file);
                }
                catch (Exception ex) { DlInfo(Tr.S("Файл не прочитан: ", "The file could not be read: ") + ex.Message); return null; }
                meta = BtMeta.Parse(bytes, out err);
                if (meta == null) { DlInfo(Tr.S("Файл .torrent не разобран: ", "The .torrent file is not recognised: ") + Path.GetFileName(file) + " — " + err); return null; }
            }
            else
            {
                magnet = BtMagnet.Parse(magnetLink, out err);
                if (magnet == null || magnet.SwarmHash == null) { DlInfo(Tr.S("magnet-ссылка не разобрана: ", "The magnet link is not recognised: ") + err); return null; }
            }
            // Новая версия раздачи из списка — вопрос до окна добавления: обновление папку и файлы не спрашивает.
            string onTopicMatch = DlEngine.TopicMatchAsk;
            DlItem listed = meta != null ? DlTopicMatch(meta) : null;
            if (listed != null)
            {
                onTopicMatch = DlAskTopicMatch(meta.Name, DlView.DisplayName(listed));
                if (onTopicMatch == null) return null;
                if (onTopicMatch == DlEngine.TopicMatchUpdate) { DlOfferUpdate(listed.Id, DlView.DisplayName(listed), file, bytes); return null; }
            }

            DlSettings settings = DlSettings.Load();
            string hash = meta != null ? meta.HexHash : Bencode.Hex(magnet.SwarmHash);
            string title = meta != null ? meta.Name : magnet.Name.Length > 0 ? magnet.Name : "magnet " + hash.Substring(0, 8);

            Form dlg = new Form();
            dlg.Text = Tr.S("Добавить торрент", "Add a torrent");
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 820; dlg.Height = meta != null ? 720 : 470;
            dlg.MinimumSize = new Size(640, meta != null ? 560 : 440);
            dlg.MinimizeBox = false; dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;
            int cw = dlg.ClientSize.Width;

            // ---------- шапка ----------
            FlowLayoutPanel head = new FlowLayoutPanel();
            head.Dock = DockStyle.Top;
            head.AutoSize = true;
            head.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            head.FlowDirection = FlowDirection.TopDown;
            head.WrapContents = false;
            head.Padding = new Padding(14, 12, 14, 4);
            Label name = DlDialogLabel(title, false);
            name.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            name.MaximumSize = new Size(cw - 28, 0);
            head.Controls.Add(name);
            List<string> facts = new List<string>();
            if (meta != null)
            {
                int real = 0;
                foreach (BtFile f in meta.Files) if (!f.Pad) real++;
                facts.Add(Engine.FormatBytes(meta.TotalSize));
                facts.Add(Tr.S("файлов: ", "files: ") + real);
                facts.Add(DlTorrentView.VersionText(meta.Version));
                int trackers = 0;
                foreach (List<string> tier in meta.Trackers) trackers += tier.Count;
                facts.Add(Tr.S("трекеров: ", "trackers: ") + trackers);
                if (meta.Private) facts.Add(Tr.S("закрытый — пиры только от трекера", "private — peers from the tracker only"));
            }
            else
            {
                facts.Add("info-hash " + hash);
                facts.Add(Tr.S("трекеров: ", "trackers: ") + magnet.Trackers.Count);
            }
            Label factsLabel = DlDialogLabel(string.Join(" · ", facts.ToArray()), true);
            factsLabel.MaximumSize = new Size(cw - 28, 0);
            head.Controls.Add(factsLabel);
            if (meta != null && meta.Comment.Length > 0)
            {
                Label comment = DlDialogLabel(DlShort(meta.Comment, 300), true);
                comment.MaximumSize = new Size(cw - 28, 0);
                head.Controls.Add(comment);
            }

            // ---------- файлы ----------
            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(14, 4, 14, 4);
            FastListView files = null;
            Label selected = null;
            if (meta != null)
            {
                FlowLayoutPanel pick = MkToolbar();
                Button all = MkFlowButton(Tr.S("Отметить все", "Select all"), 140, false);
                Button none = MkFlowButton(Tr.S("Снять все", "Clear all"), 120, false);
                selected = MkFlowLabel("", true);
                pick.Controls.AddRange(new Control[] { all, none, selected });

                files = new FastListView();
                files.Dock = DockStyle.Fill;
                files.View = View.Details;
                files.CheckBoxes = true;
                files.FullRowSelect = true;
                files.HideSelection = false;
                files.Columns.Add(Tr.S("Файл", "File"), 560);
                files.Columns.Add(Tr.S("Размер", "Size"), 110);
                _flexColumn[files] = 0;
                SetupOwnerDraw(files);
                List<ListViewItem> rows = new List<ListViewItem>();
                for (int i = 0; i < meta.Files.Count; i++)
                {
                    if (meta.Files[i].Pad) continue;
                    ListViewItem it = new ListViewItem(meta.Files[i].RelPath);
                    it.SubItems.Add(Engine.FormatBytes(meta.Files[i].Length));
                    it.Tag = i;
                    it.Checked = true;
                    rows.Add(it);
                }
                files.Items.AddRange(rows.ToArray());
                FastListView lv = files;
                all.Click += delegate { foreach (ListViewItem it in lv.Items) it.Checked = true; };
                none.Click += delegate { foreach (ListViewItem it in lv.Items) it.Checked = false; };
                Panel box = new Panel();
                box.Dock = DockStyle.Fill;
                box.Padding = new Padding(1);
                box.Controls.Add(files);
                listHost.Controls.Add(box);
                listHost.Controls.Add(pick);
            }
            else
            {
                Label wait = DlDialogLabel(Tr.S("Список файлов придёт от пиров вместе с метаданными. Сначала качаются все файлы; ненужные можно "
                                                + "отключить на вкладке «Файлы» карточки, как только список появится.",
                                                "The file list arrives from peers with the metadata. All files download at first; unwanted ones can be "
                                                + "turned off on the Files tab of the card once the list appears."), true);
                wait.Dock = DockStyle.Top;
                wait.MaximumSize = new Size(cw - 28, 0);
                listHost.Controls.Add(wait);
            }

            // ---------- параметры ----------
            FlowLayoutPanel opts = new FlowLayoutPanel();
            opts.Dock = DockStyle.Bottom;
            opts.AutoSize = true;
            opts.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            opts.FlowDirection = FlowDirection.TopDown;
            opts.WrapContents = false;
            opts.Padding = new Padding(14, 4, 14, 0);

            opts.Controls.Add(DlDialogLabel(Tr.S("Папка:", "Folder:"), false));
            FlowLayoutPanel folderRow = DlDialogRow(opts);
            TextBox folder = new TextBox();
            folder.Width = 440;
            folder.Margin = new Padding(0, 7, 8, 6);
            folderRow.Controls.Add(folder);
            Button browse = MkFlowButton(Tr.S("Выбрать…", "Choose…"), 110, false);
            Button last = MkFlowButton(Tr.S("Последняя", "Last used"), 110, false);
            folderRow.Controls.Add(browse);
            folderRow.Controls.Add(last);
            Label folderNote = DlDialogLabel("", true);
            opts.Controls.Add(folderNote);
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

            TextBox root = null;
            CheckBox useExisting = null;
            if (meta != null)
            {
                bool multi = meta.RootDir.Length > 0;
                opts.Controls.Add(DlDialogLabel(multi ? Tr.S("Имя папки раздачи:", "Torrent folder name:") : Tr.S("Имя файла:", "File name:"), false));
                root = new TextBox();
                root.Width = 700;
                root.Margin = new Padding(0, 2, 0, 4);
                root.Text = multi ? meta.RootDir : meta.Name;
                opts.Controls.Add(root);
                useExisting = new CheckBox();
                useExisting.AutoSize = true;
                useExisting.Margin = new Padding(0, 2, 0, 4);
                useExisting.Text = Tr.S("Там уже есть данные этой раздачи — проверить их, докачать недостающее и раздавать",
                                        "The data of this torrent is already there — check it, download what is missing and seed");
                opts.Controls.Add(useExisting);
            }

            FlowLayoutPanel startRow = DlDialogRow(opts);
            startRow.Controls.Add(MkFlowLabel(Tr.S("Начать:", "Start:"), false));
            RoundComboBox start = DlCombo(startRow, 150, Tr.S("сразу", "now"), Tr.S("на паузе", "paused"));
            startRow.Controls.Add(MkFlowLabel(Tr.S("Приоритет:", "Priority:"), false));
            RoundComboBox priority = DlCombo(startRow, 120, Tr.S("высокий", "high"), Tr.S("обычный", "normal"), Tr.S("низкий", "low"));
            CheckBox sequential = new CheckBox();
            sequential.AutoSize = true;
            sequential.Margin = new Padding(0, 2, 0, 6);
            sequential.Text = Tr.S("Качать куски по порядку — видео можно смотреть, пока оно качается (раздача от этого медленнее)",
                                   "Download pieces in order — a video can be watched while it downloads (slower for the swarm)");
            opts.Controls.Add(sequential);

            // ---------- кнопки ----------
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

            // Порядок Dock: Fill — первым, затем снизу вверх и шапка.
            dlg.Controls.Add(listHost);
            dlg.Controls.Add(opts);
            dlg.Controls.Add(bottom);
            dlg.Controls.Add(head);

            FastListView fileList = files;
            Label selectedLabel = selected;
            TextBox rootBox = root;
            CheckBox existingBox = useExisting;
            long wanted = meta != null ? meta.TotalSize : -1;
            EventHandler update = delegate
            {
                if (fileList != null)
                {
                    int count = 0;
                    wanted = 0;
                    foreach (ListViewItem it in fileList.Items)
                        if (it.Checked) { count++; wanted += meta.Files[(int)it.Tag].Length; }
                    selectedLabel.Text = Tr.S("Выбрано: ", "Selected: ") + count + " · " + Engine.FormatBytes(wanted);
                }
                string chosen = folder.Text.Trim();
                string target = chosen.Length > 0 ? chosen : settings.EffectiveFolder;
                string note = chosen.Length > 0 ? "" : Tr.S("Пусто — папка из настроек: ", "Empty — the folder from the settings: ") + target;
                long free = DlFiles.FreeSpace(target);
                bool low = free >= 0 && wanted >= 0 && free < wanted + 64L * 1024 * 1024;
                if (free >= 0) note += (note.Length > 0 ? " · " : "") + Tr.S("свободно ", "free ") + Engine.FormatBytes(free)
                                       + (low ? Tr.S(" — не хватит на выбранные файлы", " — not enough for the selected files") : "");
                folderNote.Text = note;
                folderNote.Name = low ? "warn" : "muted";
                folderNote.ForeColor = low ? DlWarnColor() : _theme.Subtle;

                if (rootBox != null)
                {
                    string clean = DlFiles.SanitizeName(rootBox.Text.Trim());
                    bool exists = false;
                    try { exists = !string.IsNullOrEmpty(clean) && (Directory.Exists(Path.Combine(target, clean)) || File.Exists(Path.Combine(target, clean))); }
                    catch { }
                    existingBox.Visible = exists;
                    if (!exists) existingBox.Checked = false;
                }
                problem.Text = "";
            };
            if (fileList != null) fileList.ItemChecked += delegate { update(null, EventArgs.Empty); };
            folder.TextChanged += update;
            if (rootBox != null) rootBox.TextChanged += update;
            start.SelectedIndex = 0;
            priority.SelectedIndex = 1;

            DlTorrentPlan plan = null;
            ok.DialogResult = DialogResult.None;
            ok.Click += delegate
            {
                update(null, EventArgs.Empty);
                DlTorrentPlan p = new DlTorrentPlan();
                p.Title = title;
                if (meta != null)
                {
                    int[] pr = new int[meta.Files.Count];
                    for (int i = 0; i < pr.Length; i++) pr[i] = 1;
                    bool any = false, allOn = true;
                    foreach (ListViewItem it in fileList.Items)
                    {
                        pr[(int)it.Tag] = it.Checked ? 1 : 0;
                        if (it.Checked) any = true; else allOn = false;
                    }
                    if (!any) { problem.Text = Tr.S("Не выбрано ни одного файла.", "No file is selected."); return; }
                    p.Priorities = allOn ? null : pr;
                    string rootText = rootBox.Text.Trim();
                    if (rootText.Length > 0 && string.IsNullOrEmpty(DlFiles.SanitizeName(rootText))) { problem.Text = Tr.S("Имя не подходит для Windows.", "The name is not valid on Windows."); return; }
                    p.RootName = rootText;
                    p.UseExisting = existingBox.Visible && existingBox.Checked;
                    p.File = file;
                    p.Bytes = bytes;
                }
                else p.Magnet = magnetLink.Trim();
                p.Folder = folder.Text.Trim();
                p.Paused = start.SelectedIndex == 1;
                p.Priority = 1 - Math.Max(0, priority.SelectedIndex);
                p.Sequential = sequential.Checked;
                p.OnTopicMatch = onTopicMatch;
                plan = p;
                dlg.DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            update(null, EventArgs.Empty);
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            dlg.Dispose();
            if (dr != DialogResult.OK || plan == null) return null;
            if (plan.Folder.Length > 0) MemSet(DlScope, "lastFolder", plan.Folder, true);
            return plan;
        }

        private static JVal DlTorrentCommand(DlTorrentPlan p)
        {
            JVal req = DlClient.Command("addTorrent");
            if (p.Bytes != null && p.Bytes.Length <= DlTorrentInlineBytes) req.Set("data", DlJson.S(Convert.ToBase64String(p.Bytes)));
            else if (p.File.Length > 0) req.Set("file", DlJson.S(p.File));
            else req.Set("magnet", DlJson.S(p.Magnet));
            req.Set("source", DlJson.S("manual"));
            if (p.Folder.Length > 0) req.Set("folder", DlJson.S(p.Folder));
            if (p.RootName.Length > 0) req.Set("rootName", DlJson.S(p.RootName));
            if (p.Priorities != null)
            {
                JVal arr = JVal.NewArr();
                foreach (int v in p.Priorities) arr.V.Add(DlJson.N(v));
                req.Set("priorities", arr);
            }
            if (p.Sequential) req.Set("sequential", DlJson.B(true));
            if (p.Paused) req.Set("paused", DlJson.B(true));
            if (p.UseExisting) req.Set("useExisting", DlJson.B(true));
            if (p.Priority != 0) req.Set("priority", DlJson.N(p.Priority));
            if (p.OnTopicMatch.Length > 0) req.Set("onTopicMatch", DlJson.S(p.OnTopicMatch));
            return req;
        }

        private void DlSubmitTorrent(DlTorrentPlan plan)
        {
            DlInfo(Tr.S("Добавляю торрент…", "Adding the torrent…"));
            Thread t = new Thread(delegate()
            {
                string error;
                JVal answer = null;
                try { answer = DlCallStarting(DlTorrentCommand(plan), out error); }
                catch (Exception ex) { error = ex.Message; }
                JVal result = answer;
                string why = error;
                UiPost(delegate
                {
                    // Список устарел, а процесс узнал в торренте новую версию раздачи из списка — тот же вопрос, что до окна.
                    string updateOf = result == null ? "" : DlJson.Str(result, "updateOf", "");
                    if (updateOf.Length > 0 && !DlJson.Bool(result, "ok", false) && plan.OnTopicMatch == DlEngine.TopicMatchAsk)
                    {
                        DlRow listed = _dlSnap == null ? null : _dlSnap.Find(updateOf);
                        string listedName = listed != null ? DlView.DisplayName(listed.Item) : plan.Title;
                        string choice = DlAskTopicMatch(plan.Title, listedName);
                        if (choice == DlEngine.TopicMatchUpdate) DlOfferUpdate(updateOf, listedName, plan.File, plan.Bytes);
                        else if (choice == DlEngine.TopicMatchAdd) { plan.OnTopicMatch = choice; DlSubmitTorrent(plan); }
                        else DlInfo(Tr.S("Торрент не добавлен: ", "The torrent was not added: ") + plan.Title);
                        return;
                    }
                    if (result != null && !DlJson.Bool(result, "ok", false)) why = DlJson.Str(result, "error", Tr.S("отказ без причины", "refused without a reason"));
                    DlInfo(why == null ? Tr.S("Торрент добавлен: ", "Torrent added: ") + plan.Title
                                       : Tr.S("Торрент не добавлен: ", "The torrent was not added: ") + plan.Title + " — " + why);
                    DlPoll();
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
