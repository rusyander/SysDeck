// Windows Process Cleaner — вкладка «Загрузки»: новая версия раздачи — что изменится, обновление по кнопке, версия из файла.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Окно только показывает ответ процесса загрузок (updateInfo) и шлёт команды: план замены, Корзину и проверку хешем
// делает процесс. Страница темы открывается браузером пользователя — сама программа с rutracker.org не говорит.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        // «Обновить раздачу…»: ответ процесса — в фоне, окно — когда он пришёл.
        private void DlShowUpdate(string id, string title)
        {
            DlSend(DlCommandFor("updateInfo", id), Tr.S("Смотрю, что изменится…", "Looking at what changes…"), delegate(JVal result)
            {
                DlUpdateInfo info = DlUpdateInfo.FromJson(result.Get("update"));
                if (info == null) { DlInfo(Tr.S("Не выполнено: ответ не разобран", "Not done: the answer is not recognised")); return; }
                DlInfo("");
                DlUpdateDialog(id, title, info);
            });
        }

        private void DlUpdateDialog(string id, string title, DlUpdateInfo info)
        {
            Form dlg = new Form();
            dlg.Text = Tr.S("Новая версия раздачи", "New version of the torrent");
            dlg.StartPosition = FormStartPosition.CenterParent;
            dlg.Width = 820; dlg.Height = info.Ready ? 640 : 340;
            dlg.MinimumSize = new Size(640, info.Ready ? 480 : 320);
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
            foreach (string line in DlTorrentView.UpdateLead(info))
            {
                Label l = DlDialogLabel(line, true);
                l.MaximumSize = new Size(cw - 28, 0);
                head.Controls.Add(l);
            }

            // ---------- файлы ----------
            Panel listHost = new Panel();
            listHost.Dock = DockStyle.Fill;
            listHost.Padding = new Padding(14, 4, 14, 4);
            FlowLayoutPanel tools = MkToolbar();
            Button topic = MkFlowButton(Tr.S("Открыть страницу темы", "Open the topic page"), 210, false);
            topic.Enabled = info.TopicUrl.Length > 0;
            Button fromFile = MkFlowButton(Tr.S("Из файла .torrent…", "From a .torrent file…"), 190, false);
            tools.Controls.AddRange(new Control[] { topic, fromFile });
            if (info.Ready)
            {
                FastListView files = new FastListView();
                files.Dock = DockStyle.Fill;
                files.View = View.Details;
                files.FullRowSelect = true;
                files.HideSelection = false;
                files.Columns.Add(Tr.S("Файл", "File"), 400);
                files.Columns.Add(Tr.S("Размер", "Size"), 100);
                files.Columns.Add(Tr.S("Что будет", "What happens"), 260);
                _flexColumn[files] = 2;
                SetupOwnerDraw(files);
                List<ListViewItem> rows = new List<ListViewItem>();
                foreach (string[] cells in DlTorrentView.UpdateRows(info))
                {
                    ListViewItem it = new ListViewItem(cells);
                    it.ToolTipText = string.Join("  ", cells);
                    rows.Add(it);
                }
                files.Items.AddRange(rows.ToArray());
                Panel box = new Panel();
                box.Dock = DockStyle.Fill;
                box.Padding = new Padding(1);
                box.Controls.Add(files);
                listHost.Controls.Add(box);
            }
            listHost.Controls.Add(tools);

            // ---------- кнопки ----------
            Panel bottom = new Panel();
            bottom.Dock = DockStyle.Bottom;
            bottom.Height = 62; bottom.Width = cw;
            Label problem = MkNote(DlTorrentView.UpdateBlocker(info), false);
            problem.Dock = DockStyle.None;
            problem.Name = "warn";
            problem.Left = 14; problem.Top = 8; problem.Width = cw - 14 - 130 - 8 - 140 - 8 - 110 - 14 - 16; problem.Height = 46;
            Button ok = MkButton(Tr.S("Обновить", "Update"), cw - 14 - 110 - 8 - 140 - 8 - 130, 12, 130, true);
            Button decline = MkButton(Tr.S("Не обновлять", "Don't update"), cw - 14 - 110 - 8 - 140, 12, 140, false);
            Button close = MkButton(Tr.S("Закрыть", "Close"), cw - 14 - 110, 12, 110, false);
            ok.Anchor = decline.Anchor = close.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            problem.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            ok.Enabled = info.CanApply;
            decline.Enabled = !info.Busy && info.UpdateHash.Length > 0;
            bottom.Controls.Add(problem);
            bottom.Controls.Add(ok);
            bottom.Controls.Add(decline);
            bottom.Controls.Add(close);

            // Порядок Dock: Fill — первым, затем низ и шапка.
            dlg.Controls.Add(listHost);
            dlg.Controls.Add(bottom);
            dlg.Controls.Add(head);

            string action = null;
            ok.Click += delegate { action = "apply"; dlg.DialogResult = DialogResult.OK; };
            decline.Click += delegate { action = "dismiss"; dlg.DialogResult = DialogResult.OK; };
            fromFile.Click += delegate { action = "file"; dlg.DialogResult = DialogResult.OK; };
            topic.Click += delegate { DlOpenUrl(info.TopicUrl); };
            close.DialogResult = DialogResult.Cancel;
            dlg.CancelButton = close;
            ApplyThemeTo(dlg);
            dlg.HandleCreated += delegate { ApplyTitleBar(dlg); };
            ApplyDpiTo(dlg);
            DialogResult dr = dlg.ShowDialog(this);
            dlg.Dispose();
            if (dr != DialogResult.OK || action == null) return;
            if (action == "file") { DlUpdateFromFile(id, title); return; }
            if (action == "dismiss")
            {
                DlSend(DlCommandFor("dismissUpdate", id), Tr.S("Отказываюсь от новой версии…", "Declining the new version…"),
                       delegate { DlInfo(Tr.S("Эта версия больше не предлагается; следующая — будет.", "This version is not offered again; the next one will be.")); });
                return;
            }
            DlSend(DlCommandFor("applyUpdate", id), Tr.S("Обновляю раздачу: останавливаю, переношу файлы…", "Updating the torrent: stopping, moving files…"),
                   delegate { DlInfo(Tr.S("Раздача обновлена: данные проверяются хешем, изменённое докачается.", "The torrent was updated: the data is being checked, the changes will download.")); });
        }

        // «Взять из файла .torrent…»: любой трекер — сверки темы нет; ту же версию процесс отклонит, остальное видно в окне «что изменится» до кнопки.
        private void DlUpdateFromFile(string id, string title)
        {
            string file;
            using (OpenFileDialog od = new OpenFileDialog())
            {
                od.Title = Tr.S("Новая версия раздачи: ", "New version of the torrent: ") + title;
                od.Filter = Tr.S("Торренты (*.torrent)|*.torrent|Все файлы|*.*", "Torrents (*.torrent)|*.torrent|All files|*.*");
                if (od.ShowDialog(this) != DialogResult.OK) return;
                file = od.FileName;
            }
            DlOfferUpdate(id, title, file, null);
        }

        // Новая версия файлом: процесс кладёт её рядом с записью, потом окно «что изменится».
        private void DlOfferUpdate(string id, string title, string file, byte[] bytes)
        {
            JVal req = DlCommandFor("updateFromFile", id);
            if (bytes != null && bytes.Length <= DlTorrentInlineBytes) req.Set("data", DlJson.S(Convert.ToBase64String(bytes)));
            else req.Set("file", DlJson.S(file ?? ""));
            DlSend(req, Tr.S("Читаю новую версию раздачи…", "Reading the new version of the torrent…"), delegate { DlShowUpdate(id, title); });
        }

        // .torrent той же темы, что у раздачи в списке, с другим хешем: update — обновить её, add — отдельной загрузкой, null — отмена.
        private string DlAskTopicMatch(string title, string listedName)
        {
            DialogResult r = MessageBox.Show(this,
                Tr.S("«", "“") + title + Tr.S("» — новая версия раздачи «", "” is a new version of “") + listedName + Tr.S("», которая уже есть в списке.", "”, which is already listed.")
                + Tr.S("\r\n\r\nДа — обновить её: скачанное останется, докачается только изменённое; что изменится, будет видно до обновления.\r\nНет — добавить отдельной загрузкой.",
                       "\r\n\r\nYes — update it: what was downloaded stays and only the changes download; you see what changes before updating.\r\nNo — add it as a separate download."),
                Tr.S("Загрузки", "Downloads"), MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            return r == DialogResult.Yes ? DlEngine.TopicMatchUpdate : r == DialogResult.No ? DlEngine.TopicMatchAdd : null;
        }

        // Запись списка, новой версией которой выглядит торрент: та же тема, другой хеш, ровно одна (как решает процесс).
        private DlItem DlTopicMatch(BtMeta meta)
        {
            DlSnapshot snap = _dlSnap;
            string topic = BtTopic.FromMeta(meta);
            if (snap == null || topic.Length == 0) return null;
            DlItem match = null;
            foreach (DlRow r in snap.Rows)
            {
                DlItem it = r.Item;
                if (!it.IsTorrent) continue;
                if (it.InfoHash == meta.HexHash) return null;
                if (it.TopicUrl != topic) continue;
                if (match != null) return null;
                match = it;
            }
            return match;
        }

        // Ссылка темы — только http/https (её нормализовал BtTopic); открывает браузер по умолчанию.
        private void DlOpenUrl(string url)
        {
            if (!string.IsNullOrEmpty(url) && DlHttp.IsAllowedScheme(url)) DlShellOpen(url, null);
        }
    }
}
