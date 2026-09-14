// Windows Process Cleaner — вкладка «Загрузки»: раздел настроек «Видео» — качество, контейнер, субтитры, yt-dlp и Deno.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// yt-dlp и Deno не входят в программу и не ставятся сами: их скачивает только эта кнопка, по нажатию человека.
// Контрольная сумма проверяется до запуска (Downloads.YtdlpTools.cs); без этих программ работают потоки HLS/DASH
// и прямые файлы, а страницы видеосайтов — нет.
using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private CheckBox _chkDlMdSubs, _chkDlMdToolsUpdate;
        private RoundComboBox _cbDlMdQuality, _cbDlMdOutput;
        private Label _lblDlMdTools;
        private Button _btnDlMdInstall;
        private bool _dlMdBusy;

        // Порядок = порядок в выпадающем списке; 0 — «лучшее из доступного».
        private static readonly int[] DlMdHeights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        private static readonly MdOutput[] DlMdOutputs = { MdOutput.Auto, MdOutput.Mp4, MdOutput.WebM, MdOutput.M4a, MdOutput.Mp3 };

        private void BuildDownloadsMediaSection()
        {
            DlSetSection(Tr.S("Видео", "Video"));
            FlowLayoutPanel row = DlSetRow();
            _cbDlMdQuality = DlSetCombo(row, Tr.S("Качество:", "Quality:"), 190,
                Tr.S("лучшее из доступного", "best available"), "2160p (4K)", "1440p", "1080p", "720p", "480p", "360p");
            _cbDlMdOutput = DlSetCombo(row, Tr.S("Файл:", "File:"), 190,
                Tr.S("по дорожкам", "match the tracks"), "MP4", "WebM", Tr.S("M4A (только звук)", "M4A (audio only)"), Tr.S("MP3 (только звук)", "MP3 (audio only)"));
            DlSetNote(Tr.S("Качество выбирается не выше указанного; если такого нет — ближайшее снизу. Прерванная запись продолжается "
                           + "в том качестве, с которого начата. «По дорожкам» — MP4, а для дорожек WebM — WebM.",
                           "The quality picked is never above the one set; when it is missing, the closest lower one. An interrupted recording "
                           + "continues in the quality it started with. “Match the tracks” means MP4, or WebM for WebM tracks."), true);
            _chkDlMdSubs = DlSetCheck(Tr.S("Скачивать субтитры отдельными файлами рядом с видео",
                                           "Save subtitles as separate files next to the video"));

            _lblDlMdTools = DlSetNote("", false);
            FlowLayoutPanel toolsRow = DlSetRow();
            _btnDlMdInstall = MkFlowButton(Tr.S("Установить yt-dlp…", "Install yt-dlp…"), 200, false);
            _btnDlMdInstall.Click += delegate { DlMdInstall(); };
            Button check = MkFlowButton(Tr.S("Проверить обновление", "Check for an update"), 190, false);
            check.Click += delegate { DlMdCheckUpdate(); };
            toolsRow.Controls.AddRange(new Control[] { _btnDlMdInstall, check });
            _chkDlMdToolsUpdate = DlSetCheck(Tr.S("Держать yt-dlp свежим (проверка не чаще раза в сутки)",
                                                  "Keep yt-dlp up to date (checked at most once a day)"));
            DlSetNote(Tr.S("yt-dlp и Deno — отдельные программы с открытым исходным кодом, они не входят в состав этой программы и "
                           + "скачиваются только по этой кнопке, с проверкой контрольной суммы. Нужны для страниц видеосайтов; "
                           + "прямые ссылки и потоки HLS/DASH работают и без них.",
                           "yt-dlp and Deno are separate open-source programs. They are not part of this app and are downloaded only by this "
                           + "button, with a checksum verified first. They are needed for video site pages; direct links and HLS/DASH streams "
                           + "work without them."), true);
        }

        private void DlMdSettingsToUi(DlSettings s)
        {
            int qi = Array.IndexOf(DlMdHeights, s.MdMaxHeight);
            _cbDlMdQuality.SelectedIndex = qi >= 0 ? qi : 0;
            int oi = Array.IndexOf(DlMdOutputs, s.MdOutput);
            _cbDlMdOutput.SelectedIndex = oi >= 0 ? oi : 0;
            _chkDlMdSubs.Checked = s.MdSubtitles;
            _chkDlMdToolsUpdate.Checked = s.MdToolsUpdate;
        }

        private void DlMdUiToSettings(DlSettings ui)
        {
            int qi = _cbDlMdQuality.SelectedIndex;
            ui.MdMaxHeight = qi >= 0 && qi < DlMdHeights.Length ? DlMdHeights[qi] : 0;
            int oi = _cbDlMdOutput.SelectedIndex;
            ui.MdOutput = oi >= 0 && oi < DlMdOutputs.Length ? DlMdOutputs[oi] : MdOutput.Auto;
            ui.MdSubtitles = _chkDlMdSubs.Checked;
            ui.MdToolsUpdate = _chkDlMdToolsUpdate.Checked;
        }

        private static void DlMdCopyTunables(DlSettings from, DlSettings to)
        {
            to.MdMaxHeight = from.MdMaxHeight;
            to.MdOutput = from.MdOutput;
            to.MdSubtitles = from.MdSubtitles;
            to.MdToolsUpdate = from.MdToolsUpdate;
        }

        // Состояние инструментов читается с диска: файл на месте и совпадает с записью об установке.
        private void DlMdLoadTools()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                MdToolStatus st = null;
                try { st = MdTools.Status(); }
                catch (Exception ex) { DlLog.Report(ex); }
                UiPost(delegate { DlMdShowTools(st); });
            });
        }

        private void DlMdShowTools(MdToolStatus st)
        {
            if (_closing || _lblDlMdTools == null) return;
            if (st == null) { _lblDlMdTools.Text = Tr.S("Состояние yt-dlp прочитать не удалось.", "The yt-dlp state could not be read."); return; }
            string text;
            if (!st.YtdlpInstalled && !st.DenoInstalled)
                text = Tr.S("yt-dlp не установлен — страницы видеосайтов пока недоступны.",
                            "yt-dlp is not installed — video site pages are unavailable for now.");
            else
            {
                text = "yt-dlp: " + (st.YtdlpInstalled ? (st.YtdlpVersion.Length > 0 ? st.YtdlpVersion : Tr.S("установлен", "installed"))
                                                      : Tr.S("нет", "missing"))
                       + " · Deno: " + (st.DenoInstalled ? (st.DenoVersion.Length > 0 ? st.DenoVersion : Tr.S("установлен", "installed"))
                                                         : Tr.S("нет", "missing"));
                if (st.UpdateAvailable) text += Tr.S(" · есть обновление", " · an update is available");
                if (st.CheckedUtc != DateTime.MinValue)
                    text += Tr.S(" · проверено ", " · checked ") + st.CheckedUtc.ToLocalTime().ToString("dd.MM HH:mm");
            }
            _lblDlMdTools.Text = text;
            _btnDlMdInstall.Text = st.YtdlpInstalled && st.DenoInstalled
                ? Tr.S("Обновить yt-dlp…", "Update yt-dlp…") : Tr.S("Установить yt-dlp…", "Install yt-dlp…");
            _btnDlMdInstall.Enabled = !_dlMdBusy;
        }

        private void DlMdInstall()
        {
            if (_dlMdBusy) return;
            if (MessageBox.Show(this,
                    Tr.S("Скачать yt-dlp и Deno с их страниц выпусков на GitHub?\r\n\r\nЭто отдельные программы с открытым исходным кодом; "
                         + "они не входят в состав этой программы. Контрольная сумма проверяется до первого запуска.",
                         "Download yt-dlp and Deno from their GitHub release pages?\r\n\r\nThese are separate open-source programs and are not "
                         + "part of this app. A checksum is verified before either one is ever run."),
                    Tr.S("Загрузки", "Downloads"), MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            _dlMdBusy = true;
            _btnDlMdInstall.Enabled = false;
            DlSetInfo(Tr.S("Скачиваю yt-dlp…", "Downloading yt-dlp…"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                bool ok = false;
                try { ok = MdTools.Install(false, DlMdProgress, DlMdNoCancel, out error); }
                catch (Exception ex) { DlLog.Report(ex); error = ex.Message; }
                string failure = error;
                bool done = ok;
                UiPost(delegate
                {
                    _dlMdBusy = false;
                    DlSetInfo(done ? Tr.S("yt-dlp готов.", "yt-dlp is ready.")
                                   : Tr.S("Не установлено: ", "Not installed: ") + (failure ?? ""));
                    DlMdLoadTools();
                });
            });
        }

        private void DlMdCheckUpdate()
        {
            if (_dlMdBusy) return;
            _dlMdBusy = true;
            DlSetInfo(Tr.S("Проверяю обновление…", "Checking for an update…"));
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                bool available = false;
                try { MdTools.CheckUpdate(true, DlMdNoCancel, out available, out error); }
                catch (Exception ex) { DlLog.Report(ex); error = ex.Message; }
                string failure = error;
                bool upd = available;
                UiPost(delegate
                {
                    _dlMdBusy = false;
                    DlSetInfo(failure != null ? Tr.S("Проверка не удалась: ", "The check failed: ") + failure
                              : upd ? Tr.S("Есть обновление — нажмите «Обновить yt-dlp…».", "An update is available — press “Update yt-dlp…”.")
                              : Tr.S("Установлено свежее.", "What is installed is up to date."));
                    DlMdLoadTools();
                });
            });
        }

        private void DlMdProgress(string what, double fraction)
        {
            string text = what ?? "";
            int percent = (int)Math.Floor(Math.Max(0, Math.Min(1, fraction)) * 100);
            UiPost(delegate { DlSetInfo(text.Length > 0 ? text + " · " + percent + " %" : percent + " %"); });
        }

        private static bool DlMdNoCancel() { return false; }
    }
}
