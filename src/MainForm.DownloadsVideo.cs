// SysDeck — вкладка «Загрузки»: окно «Видео по ссылке» (YouTube, Twitch и другие сайты, которые понимает yt-dlp).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Ссылка → yt-dlp описывает страницу (в этом процессе, в фоне, с отменой при закрытии окна) → человек выбирает
// качество, звук, субтитры и контейнер → запись уходит процессу загрузок командой addMedia с id выбранных дорожек.
// Дальше всё как у любой видео-загрузки: очередь, пауза, продолжение, склейка. Списки качеств — Downloads.VideoPick.cs.
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
        private static readonly MdOutput[] DlVidOutputs = { MdOutput.Auto, MdOutput.Mp4, MdOutput.WebM };

        // Выбор из окна; null — окно закрыто без загрузки.
        private sealed class DlVideoPlan
        {
            public MdManifest Manifest;
            public MdPickQuality Quality;
            public MdTrack Subtitle;
            public string Folder = "";
            public bool Paused;
        }

        private void DlShowVideo(string initialText)
        {
            DlVideoPlan plan = DlAskVideo(initialText ?? "");
            if (plan != null) DlSubmitVideo(plan, false);
        }

        private DlVideoPlan DlAskVideo(string initialText)
        {
            DlSettings settings = DlSettings.Load();
            Form dlg = new Form();
            dlg.Text = Tr.S("Видео по ссылке", "Video by link");
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 760; dlg.Height = 640;
            dlg.MinimumSize = new Size(620, 480);
            dlg.MinimizeBox = false; dlg.MaximizeBox = false;
            dlg.ShowInTaskbar = false;
            dlg.BackColor = _theme.Bg; dlg.ForeColor = _theme.Text;
            dlg.Font = Font;

            Panel scroll = new Panel();
            scroll.Dock = DockStyle.Fill;
            scroll.AutoScroll = true;
            FlowLayoutPanel body = new FlowLayoutPanel();
            body.Location = new Point(14, 12);
            body.AutoSize = true;
            body.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            body.FlowDirection = FlowDirection.TopDown;
            body.WrapContents = false;
            scroll.Controls.Add(body);

            body.Controls.Add(DlDialogLabel(Tr.S("Ссылка на видео — YouTube, Twitch или другой сайт, который понимает yt-dlp:",
                                                 "Video link — YouTube, Twitch or another site yt-dlp understands:"), false));
            FlowLayoutPanel urlRow = DlDialogRow(body);
            TextBox url = new TextBox();
            url.Width = 560;
            url.Margin = new Padding(0, 7, 8, 6);
            url.Text = initialText;
            urlRow.Controls.Add(url);
            Button find = MkFlowButton(Tr.S("Найти", "Find"), 110, false);
            urlRow.Controls.Add(find);
            Label site = DlDialogLabel("", true);
            body.Controls.Add(site);
            Label status = DlDialogLabel("", true);
            body.Controls.Add(status);
            FlowLayoutPanel toolsRow = DlDialogRow(body);
            Button install = MkFlowButton(Tr.S("Установить yt-dlp…", "Install yt-dlp…"), 200, false);
            toolsRow.Controls.Add(install);

            // Всё ниже — после того, как yt-dlp описал страницу.
            FlowLayoutPanel found = new FlowLayoutPanel();
            found.AutoSize = true;
            found.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            found.FlowDirection = FlowDirection.TopDown;
            found.WrapContents = false;
            found.Margin = new Padding(0, 6, 0, 0);
            body.Controls.Add(found);
            Label title = new Label();
            title.AutoSize = true;
            title.MaximumSize = new Size(700, 0);
            title.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            title.Margin = new Padding(0, 4, 0, 2);
            found.Controls.Add(title);
            Label meta = DlDialogLabel("", true);
            found.Controls.Add(meta);

            FlowLayoutPanel qualityRow = DlDialogRow(found);
            qualityRow.Controls.Add(MkFlowLabel(Tr.S("Качество:", "Quality:"), false));
            RoundComboBox quality = DlCombo(qualityRow, 420);
            CheckBox all = new CheckBox();
            all.Text = Tr.S("все форматы", "all formats");
            all.AutoSize = true;
            all.Margin = new Padding(0, 8, 0, 4);
            qualityRow.Controls.Add(all);

            FlowLayoutPanel outRow = DlDialogRow(found);
            outRow.Controls.Add(MkFlowLabel(Tr.S("Файл:", "File:"), false));
            RoundComboBox output = DlCombo(outRow, 190, Tr.S("по дорожкам", "match the tracks"), "MP4", "WebM");
            outRow.Controls.Add(MkFlowLabel(Tr.S("Субтитры:", "Subtitles:"), false));
            RoundComboBox subtitles = DlCombo(outRow, 250);

            found.Controls.Add(DlDialogLabel(Tr.S("Папка:", "Folder:"), false));
            FlowLayoutPanel folderRow = DlDialogRow(found);
            TextBox folder = new TextBox();
            folder.Width = 420;
            folder.Margin = new Padding(0, 7, 8, 6);
            folderRow.Controls.Add(folder);
            Button browse = MkFlowButton(Tr.S("Выбрать…", "Choose…"), 110, false);
            Button last = MkFlowButton(Tr.S("Последняя", "Last used"), 110, false);
            folderRow.Controls.Add(browse);
            folderRow.Controls.Add(last);
            Label folderNote = DlDialogLabel("", true);
            found.Controls.Add(folderNote);
            CheckBox paused = new CheckBox();
            paused.Text = Tr.S("Добавить на паузе", "Add paused");
            paused.AutoSize = true;
            paused.Margin = new Padding(0, 4, 0, 4);
            found.Controls.Add(paused);
            Label liveNote = DlDialogLabel(Tr.S("Это трансляция: запись идёт, пока вы её не остановите — правая кнопка по записи → «Остановить запись».",
                                                "This is a live stream: it records until you stop it — right-click the entry → “Stop recording”."), true);
            found.Controls.Add(liveNote);

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

            int cw = dlg.ClientSize.Width;
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 62; bottom.Width = cw;
            Label problem = MkNote("", false);
            problem.Dock = DockStyle.None;
            problem.Name = "warn";
            problem.Left = 14; problem.Top = 10; problem.Width = cw - 14 - 120 - 8 - 110 - 14 - 16; problem.Height = 44;
            Button ok = MkButton(Tr.S("Скачать", "Download"), cw - 14 - 110 - 8 - 120, 12, 120, true);
            Button cancel = MkButton(Tr.S("Отмена", "Cancel"), cw - 14 - 110, 12, 110, false);
            ok.Anchor = cancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            problem.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            bottom.Controls.Add(problem);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);
            dlg.Controls.Add(scroll);
            dlg.Controls.Add(bottom);

            // ---------- состояние окна ----------
            MdManifest manifest = null;
            string probed = "";                          // адрес, для которого показан manifest или идёт разбор
            int generation = 0;                          // разбор старше текущего поколения — устарел, ответ выбрасывается
            bool closed = false, filling = false;
            List<MdPickQuality> qualities = new List<MdPickQuality>();
            List<MdTrack> subs = new List<MdTrack>();
            System.Windows.Forms.Timer debounce = new System.Windows.Forms.Timer();
            debounce.Interval = 700;
            int oi = Array.IndexOf(DlVidOutputs, settings.MdOutput);
            output.SelectedIndex = oi >= 0 ? oi : 0;

            Action<string, bool> setStatus = delegate(string text, bool warn)
            {
                status.Text = text;
                status.Name = warn ? "warn" : "muted";
                status.ForeColor = warn ? DlWarnColor() : _theme.Subtle;
                status.Visible = text.Length > 0;
            };

            MethodInvoker updateProblem = delegate
            {
                int qi = quality.SelectedIndex;
                MdPickQuality q = qi >= 0 && qi < qualities.Count ? qualities[qi] : null;
                output.Enabled = q != null && !q.AudioOnly;
                string why = manifest == null ? "" : MdPick.Problem(q) ?? "";
                problem.Text = why;
                ok.Enabled = manifest != null && why.Length == 0;
            };

            // Пересобрать список качеств под контейнер и галку «все форматы», не теряя выбранный вариант.
            MethodInvoker fillQualities = delegate
            {
                if (manifest == null) return;
                filling = true;
                int was = quality.SelectedIndex;
                string keepId = was >= 0 && was < qualities.Count ? qualities[was].Variant.Id + (qualities[was].AudioOnly ? "|" + qualities[was].Output : "") : "";
                MdOutput o = DlVidOutputs[Math.Max(0, output.SelectedIndex)];
                qualities = MdPick.Qualities(manifest, o, all.Checked, MdMux.Mp3Available);
                quality.Items.Clear();
                int pick = -1;
                for (int i = 0; i < qualities.Count; i++)
                {
                    quality.Items.Add(qualities[i].Label);
                    string id = qualities[i].Variant.Id + (qualities[i].AudioOnly ? "|" + qualities[i].Output : "");
                    if (id == keepId) pick = i;
                }
                if (pick < 0) pick = MdPick.DefaultIndex(qualities, settings.MdMaxHeight);
                quality.SelectedIndex = pick;
                filling = false;
                bool anyVideo = false;
                foreach (MdPickQuality q in qualities) if (!q.AudioOnly) anyVideo = true;
                if (!anyVideo)
                    setStatus(o == MdOutput.Auto ? Tr.S("Среди форматов нет такого, который собирается без перекодирования.",
                                                         "None of the formats can be assembled without transcoding.")
                                                  : Tr.S("В этом контейнере собрать нечего — выберите «по дорожкам».",
                                                         "Nothing can be assembled into this container — choose “match the tracks”."), true);
                else setStatus("", false);
                updateProblem();
            };

            MethodInvoker updateFolder = delegate
            {
                string chosen = folder.Text.Trim();
                string guess = manifest != null && manifest.Title.Length > 0 ? DlFiles.SanitizeName(manifest.Title) + ".mp4" : "video.mp4";
                string target = chosen.Length > 0 ? chosen : DlFiles.FolderFor(settings, probed.Length > 0 ? probed : "https://localhost/", guess);
                string note = chosen.Length > 0 ? "" : Tr.S("Пусто — по правилам настроек: ", "Empty — by the settings rules: ") + target;
                long free = DlFiles.FreeSpace(target);
                if (free >= 0)
                {
                    note += (note.Length > 0 ? " · " : "") + Tr.S("свободно ", "free ") + Engine.FormatBytes(free);
                    if (free < 1024L * 1024 * 1024) note += Tr.S(" — места мало", " — low on space");
                }
                folderNote.Text = note;
                folderNote.Name = free >= 0 && free < 1024L * 1024 * 1024 ? "warn" : "muted";
                folderNote.ForeColor = folderNote.Name == "warn" ? DlWarnColor() : _theme.Subtle;
            };

            Action<MdManifest, string> show = delegate(MdManifest m, string error)
            {
                find.Enabled = true;
                if (m == null)
                {
                    probed = "";
                    setStatus(string.IsNullOrEmpty(error) ? Tr.S("yt-dlp не вернул форматы.", "yt-dlp returned no formats.") : error, true);
                    return;
                }
                if (m.Refused.Length > 0) { probed = ""; setStatus(m.Refused, true); return; }
                manifest = m;
                title.Text = m.Title.Length > 0 ? m.Title : Tr.S("(без названия)", "(untitled)");
                string d = MdPick.Duration(m.DurationSec);
                meta.Text = (m.Live ? Tr.S("трансляция в эфире", "live now") : d.Length > 0 ? Tr.S("длительность ", "duration ") + d : "")
                            + (m.Variants.Count > 0 ? (m.Live || d.Length > 0 ? " · " : "") + Tr.S("форматов: ", "formats: ") + m.Variants.Count : "");
                liveNote.Visible = m.Live;
                subs = MdPick.Subtitles(m);
                subtitles.Items.Clear();
                subtitles.Items.Add(subs.Count > 0 ? Tr.S("без субтитров", "no subtitles") : Tr.S("нет", "none"));
                foreach (MdTrack s in subs) subtitles.Items.Add(s.Name.Length > 0 ? s.Name : s.Language);
                subtitles.SelectedIndex = MdPick.DefaultSubtitle(subs, settings.MdSubtitles) + 1;
                subtitles.Enabled = subs.Count > 0;
                found.Visible = true;
                fillQualities();
                updateFolder();
            };

            MethodInvoker probe = delegate
            {
                debounce.Stop();
                string u = MdPick.Normalize(url.Text);
                site.Text = MdPick.Describe(u);
                site.Visible = site.Text.Length > 0;
                if (u.Length == 0)
                {
                    setStatus(url.Text.Trim().Length > 0 ? Tr.S("Это не ссылка http или https.", "This is not an http or https link.") : "", url.Text.Trim().Length > 0);
                    return;
                }
                bool ready = MdYtdlp.Available;
                toolsRow.Visible = !ready;
                if (!ready)
                {
                    setStatus(Tr.S("Чтобы разобрать страницу, нужен yt-dlp — он ставится кнопкой ниже, с проверкой контрольной суммы.",
                                   "Reading the page needs yt-dlp — install it with the button below; its checksum is verified."), true);
                    return;
                }
                if (u == probed) return;
                probed = u;
                manifest = null;
                found.Visible = false;
                problem.Text = "";
                ok.Enabled = false;
                find.Enabled = false;
                int g = ++generation;
                setStatus(Tr.S("Смотрю страницу через yt-dlp… обычно 5–20 секунд.", "Reading the page with yt-dlp… usually 5–20 seconds."), false);
                Thread t = new Thread(delegate()
                {
                    string error = null;
                    MdManifest m = null;
                    try { m = MdYtdlp.Extract(u, delegate { return closed || g != generation; }, out error); }
                    catch (Exception ex) { DlLog.Report(ex); error = ex.Message; }
                    MdManifest mc = m;
                    string ec = error;
                    UiPost(delegate { if (!closed && g == generation) show(mc, ec); });
                });
                t.IsBackground = true;
                t.Start();
            };

            install.Click += delegate
            {
                if (_dlMdBusy) { setStatus(Tr.S("yt-dlp уже ставится — подождите.", "yt-dlp is already being installed — please wait."), false); return; }
                if (!DlMdAskInstall(dlg)) return;
                _dlMdBusy = true;
                install.Enabled = false;
                setStatus(Tr.S("Скачиваю yt-dlp…", "Downloading yt-dlp…"), false);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    string error = null;
                    bool done = false;
                    try
                    {
                        done = MdTools.Install(false, delegate(string what, double fraction)
                        {
                            int percent = (int)Math.Floor(Math.Max(0, Math.Min(1, fraction)) * 100);
                            string text = (what ?? "").Length > 0 ? what + " · " + percent + " %" : percent + " %";
                            UiPost(delegate { if (!closed) setStatus(text, false); });
                        }, delegate { return closed; }, out error);
                    }
                    catch (Exception ex) { DlLog.Report(ex); error = ex.Message; }
                    bool okCopy = done;
                    string failure = error;
                    UiPost(delegate
                    {
                        _dlMdBusy = false;
                        DlMdLoadTools();
                        if (closed) return;
                        install.Enabled = true;
                        if (!okCopy) { setStatus(Tr.S("Не установлено: ", "Not installed: ") + (failure ?? ""), true); return; }
                        probed = "";
                        probe();
                    });
                });
            };

            url.TextChanged += delegate
            {
                string u = MdPick.Normalize(url.Text);
                site.Text = MdPick.Describe(u);
                site.Visible = site.Text.Length > 0;
                debounce.Stop();
                if (u.Length > 0 && u != probed) debounce.Start();
            };
            url.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true;
                probed = "";
                probe();
            };
            debounce.Tick += delegate { probe(); };
            find.Click += delegate { probed = ""; probe(); };
            quality.SelectedIndexChanged += delegate { if (!filling) updateProblem(); };
            output.SelectedIndexChanged += delegate { fillQualities(); };
            all.CheckedChanged += delegate { fillQualities(); };
            folder.TextChanged += delegate { updateFolder(); };

            DlVideoPlan plan = null;
            ok.DialogResult = DialogResult.None;
            ok.Click += delegate
            {
                updateProblem();
                int qi = quality.SelectedIndex;
                if (manifest == null || qi < 0 || qi >= qualities.Count || problem.Text.Length > 0) return;
                DlVideoPlan p = new DlVideoPlan();
                p.Manifest = manifest;
                p.Quality = qualities[qi];
                int si = subtitles.SelectedIndex - 1;
                p.Subtitle = si >= 0 && si < subs.Count ? subs[si] : null;
                p.Folder = folder.Text.Trim();
                p.Paused = paused.Checked;
                plan = p;
                dlg.DialogResult = DialogResult.OK;
            };
            cancel.DialogResult = DialogResult.Cancel;
            dlg.CancelButton = cancel;
            ApplyThemeTo(dlg);
            found.Visible = false;
            liveNote.Visible = false;
            toolsRow.Visible = !MdYtdlp.Available;
            ok.Enabled = false;
            setStatus(MdYtdlp.Available ? "" : Tr.S("Для разбора страниц нужен yt-dlp — он ставится кнопкой ниже.",
                                                    "Reading pages needs yt-dlp — install it with the button below."), !MdYtdlp.Available);
            site.Visible = false;
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            dlg.Shown += delegate
            {
                url.Focus();
                url.SelectionStart = url.TextLength;
                if (MdPick.Normalize(url.Text).Length > 0) probe();
            };
            dlg.FormClosed += delegate { closed = true; debounce.Stop(); };
            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            closed = true;
            debounce.Dispose();
            dlg.Dispose();
            if (dr != DialogResult.OK || plan == null) return null;
            if (plan.Folder.Length > 0) MemSet(DlScope, "lastFolder", plan.Folder, true);
            return plan;
        }

        // Отправка в фоне; запись с той же ссылкой уже есть — один вопрос и повтор с разрешением на дубликат.
        private void DlSubmitVideo(DlVideoPlan plan, bool allowDuplicate)
        {
            DlInfo(Tr.S("Добавляю видео…", "Adding the video…"));
            JVal cmd = MdPick.Command(plan.Manifest, plan.Quality, plan.Subtitle, plan.Folder, plan.Paused, allowDuplicate);
            string name = plan.Manifest.Title.Length > 0 ? plan.Manifest.Title : plan.Manifest.PageUrl;
            Thread t = new Thread(delegate()
            {
                string error;
                JVal answer = DlCallStarting(cmd, out error);
                UiPost(delegate
                {
                    if (answer == null) { DlInfo(Tr.S("Не добавлено: ", "Not added: ") + error); return; }
                    if (DlJson.Bool(answer, "ok", false))
                    {
                        DlInfo(Tr.S("Добавлено видео: ", "Video added: ") + DlShort(name, 90));
                        DlPoll();
                        return;
                    }
                    if (!allowDuplicate && DlJson.Str(answer, "duplicateOf", "").Length > 0 && !_closing)
                    {
                        DlInfo("");
                        if (MsgAsk(Tr.S("Это видео уже есть в списке загрузок:\r\n\r\n", "This video is already in the download list:\r\n\r\n")
                                   + "• " + DlShort(name, 90)
                                   + Tr.S("\r\n\r\nСкачать ещё раз? Новый файл получит другое имя.", "\r\n\r\nDownload again? The new file gets a different name."),
                                   Tr.S("Загрузки", "Downloads")))
                            DlSubmitVideo(plan, true);
                        return;
                    }
                    DlInfo(Tr.S("Не добавлено: ", "Not added: ") + DlJson.Str(answer, "error", ""));
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
