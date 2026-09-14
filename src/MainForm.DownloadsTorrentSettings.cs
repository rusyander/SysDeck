// Windows Process Cleaner — вкладка «Загрузки»: раздел настроек «Торренты» — входящие, сеть, раздача, папка наблюдения, ассоциации.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Входящие соединения открываются только кнопкой: правило брандмауэра добавляет элевированный помощник (одно окно UAC), после
// чего процесс загрузок перепроверяет правило и начинает слушать порт. Ассоциации .torrent и magnet пишутся в HKCU только по
// щелчку по переключателю; выбор, сделанный в самой Windows, программа не перебивает, а открывает «Приложения по умолчанию».
using System;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        private Label _lblDlBtInbound, _lblDlBtWatch, _lblDlBtAssoc;
        private Button _btnDlBtAllow, _btnDlBtClose, _btnDlBtDefaultApps;
        private CheckBox _chkDlBtMapping, _chkDlBtDht, _chkDlBtPex, _chkDlBtLsd, _chkDlBtSeed, _chkDlBtRecycle, _chkDlBtUpdateCheck, _chkDlBtAssocTorrent, _chkDlBtAssocMagnet;
        private RoundComboBox _cmbDlBtEncryption;
        private NumericUpDown _numDlBtPort, _numDlBtActive, _numDlBtUp, _numDlBtConns, _numDlBtPerTorrent, _numDlBtSlots, _numDlBtRatio, _numDlBtSeedMin;
        private bool _dlBtInboundOn, _dlBtLoaded;
        private FirewallInbound _dlBtFw = FirewallInbound.Unknown;
        private BtAssocState _dlBtAssocTorrent, _dlBtAssocMagnet;

        private void BuildDownloadsTorrentSection()
        {
            DlSetSection(Tr.S("Торренты", "Torrents"));
            _lblDlBtInbound = DlSetNote("", false);
            FlowLayoutPanel inBar = DlSetRow();
            _btnDlBtAllow = MkFlowButton(Tr.S("Разрешить входящие…", "Allow incoming…"), 200, false);
            _btnDlBtAllow.Click += delegate { DlBtAllowInbound(); };
            _btnDlBtClose = MkFlowButton(Tr.S("Закрыть входящие", "Close incoming"), 170, false);
            _btnDlBtClose.Click += delegate { DlBtCloseInbound(); };
            inBar.Controls.AddRange(new Control[] { _btnDlBtAllow, _btnDlBtClose });
            DlSetNote(Tr.S("Без входящих торрент тоже качает и раздаёт, но только тем пирам, к которым подключился сам, — на редких раздачах этого может "
                           + "не хватить. «Разрешить» один раз спросит права администратора и добавит в брандмауэр Windows правило только для этой "
                           + "программы.",
                           "Without incoming connections a torrent still downloads and seeds, but only with peers it connected to itself — on rare "
                           + "torrents that may not be enough. “Allow” asks for administrator rights once and adds a Windows Firewall rule for this "
                           + "program only."), true);
            _chkDlBtMapping = DlSetCheck(Tr.S("Открывать порт на роутере (UPnP / NAT-PMP), пока входящие разрешены",
                                              "Open the port on the router (UPnP / NAT-PMP) while incoming connections are allowed"));
            FlowLayoutPanel netRow = DlSetRow();
            _numDlBtPort = DlSetNumber(netRow, Tr.S("Порт:", "Port:"), 0, 65535, 90);
            _cmbDlBtEncryption = DlSetCombo(netRow, Tr.S("Шифрование:", "Encryption:"), 250, Tr.S("предпочитать шифрование", "prefer encryption"),
                                            Tr.S("только шифрованные соединения", "encrypted connections only"), Tr.S("без шифрования", "no encryption"));
            DlSetNote(Tr.S("Порт 0 — свободный выбирается при первом запуске и запоминается; порты меньше 1024 не принимаются.",
                           "Port 0 means a free one is picked on the first start and remembered; ports below 1024 are not accepted."), true);
            _chkDlBtDht = DlSetCheck(Tr.S("DHT — искать пиров без трекера", "DHT — find peers without a tracker"));
            _chkDlBtPex = DlSetCheck(Tr.S("Обмен пирами — узнавать о пирах от других пиров", "Peer exchange — learn about peers from other peers"));
            _chkDlBtLsd = DlSetCheck(Tr.S("Искать пиров в локальной сети", "Find peers on the local network"));
            DlSetNote(Tr.S("У закрытых торрентов DHT, обмен пирами и локальный поиск выключены всегда — так требует их трекер.",
                           "Private torrents never use DHT, peer exchange or local discovery — their tracker requires it."), true);

            FlowLayoutPanel limRow = DlSetRow();
            _numDlBtActive = DlSetNumber(limRow, Tr.S("Качается торрентов одновременно:", "Torrents downloading at once:"), 1, 20, 70);
            _numDlBtUp = DlSetNumber(limRow, Tr.S("Лимит отдачи, КБ/с:", "Upload limit, KB/s:"), 0, 10 * 1024 * 1024, 110);
            FlowLayoutPanel connRow = DlSetRow();
            _numDlBtConns = DlSetNumber(connRow, Tr.S("Соединений всего:", "Connections in total:"), 10, 2000, 80);
            _numDlBtPerTorrent = DlSetNumber(connRow, Tr.S("На торрент:", "Per torrent:"), 2, 500, 70);
            _numDlBtSlots = DlSetNumber(connRow, Tr.S("Слотов отдачи:", "Upload slots:"), 1, 50, 70);
            DlSetNote(Tr.S("Раздающиеся торренты в число одновременных не входят. Лимит отдачи 0 — без ограничения; приём подчиняется общему лимиту загрузок.",
                           "Seeding torrents do not count towards the ones at once. Upload limit 0 means no limit; downloading follows the overall download limit."), true);

            _chkDlBtSeed = DlSetCheck(Tr.S("Раздавать после загрузки", "Seed after downloading"));
            FlowLayoutPanel seedRow = DlSetRow();
            _numDlBtRatio = DlSetNumber(seedRow, Tr.S("Остановить при рейтинге:", "Stop at ratio:"), 0, 1000, 80);
            _numDlBtRatio.DecimalPlaces = 2;
            _numDlBtRatio.Increment = 0.1M;
            _numDlBtSeedMin = DlSetNumber(seedRow, Tr.S("или через, мин:", "or after, min:"), 0, 1000000, 100);
            DlSetNote(Tr.S("0 — без предела. Рейтинг 1,00 — отдано столько же, сколько скачано. Остановленную раздачу можно возобновить из меню записи.",
                           "0 means no limit. Ratio 1.00 means as much uploaded as downloaded. A stopped torrent can seed again from its menu."), true);

            _lblDlBtWatch = DlSetNote("", false);
            FlowLayoutPanel watchBar = DlSetRow();
            Button watchPick = MkFlowButton(Tr.S("Следить за папкой…", "Watch a folder…"), 180, false);
            watchPick.Click += delegate { DlBtPickWatchFolder(); };
            Button watchOff = MkFlowButton(Tr.S("Не следить", "Stop watching"), 130, false);
            watchOff.Click += delegate { DlChangeSettings(delegate(DlSettings s) { s.BtWatchFolder = ""; }, Tr.S("Папка наблюдения отключена.", "The watch folder is off.")); };
            watchBar.Controls.AddRange(new Control[] { watchPick, watchOff });
            _chkDlBtRecycle = DlSetCheck(Tr.S("Убирать файл .torrent после добавления", "Remove the .torrent file once added"));
            DlSetNote(Tr.S("Скачанный .torrent и файл из папки наблюдения становятся торрентом сами, с настройками по умолчанию. Папка просматривается, "
                           + "пока работает процесс загрузок. Убранный файл уходит в Корзину, копию браузера удаляет сам браузер; содержимое торрента "
                           + "остаётся в папке данных.",
                           "A downloaded .torrent and a file from the watch folder become a torrent by themselves, with default options. The folder "
                           + "is checked while the download process runs. A removed file goes to the Recycle Bin, the browser deletes its own copy; "
                           + "the torrent content stays in the data folder."), true);

            _chkDlBtUpdateCheck = DlSetCheck(Tr.S("Проверять новые версии раздач rutracker раз в 6 часов", "Check rutracker torrents for new versions every 6 hours"));
            DlSetNote(Tr.S("Проверка читает открытые выгрузки api.rutracker.cc — без входа на сайт; форум темы ищется, пока компьютер простаивает. "
                           + "Трекер, который перестал знать раздачу, запускает проверку сразу. Найденная версия приходит уведомлением, а заменяет "
                           + "раздачу только кнопка «Обновить» после списка изменений. Раздачи других сайтов обновляются файлом .torrent со страницы темы.",
                           "The check reads the public api.rutracker.cc dumps — no sign-in; the topic's forum is looked up while the computer is idle. "
                           + "A tracker that stops knowing the torrent starts a check at once. A found version comes as a notification, and only the "
                           + "“Update” button after the list of changes replaces the torrent. Torrents of other sites update from a .torrent file from the topic page."), true);

            // Не через отложенное сохранение: переключатель сразу пишет или удаляет ключи реестра.
            _chkDlBtAssocTorrent = new CheckBox();
            _chkDlBtAssocTorrent.Text = Tr.S("Открывать файлы .torrent этой программой", "Open .torrent files with this program");
            _chkDlBtAssocTorrent.AutoSize = true;
            _chkDlBtAssocTorrent.Margin = new Padding(0, 8, 0, 4);
            _chkDlBtAssocTorrent.Click += delegate { DlBtSetAssoc(true, _chkDlBtAssocTorrent.Checked); };
            _dlSetBody.Controls.Add(_chkDlBtAssocTorrent);
            _chkDlBtAssocMagnet = new CheckBox();
            _chkDlBtAssocMagnet.Text = Tr.S("Открывать magnet-ссылки этой программой", "Open magnet links with this program");
            _chkDlBtAssocMagnet.AutoSize = true;
            _chkDlBtAssocMagnet.Margin = new Padding(0, 2, 0, 4);
            _chkDlBtAssocMagnet.Click += delegate { DlBtSetAssoc(false, _chkDlBtAssocMagnet.Checked); };
            _dlSetBody.Controls.Add(_chkDlBtAssocMagnet);
            _lblDlBtAssoc = DlSetNote("", true);
            FlowLayoutPanel assocBar = DlSetRow();
            _btnDlBtDefaultApps = MkFlowButton(Tr.S("Приложения по умолчанию…", "Default apps…"), 220, false);
            _btnDlBtDefaultApps.Click += delegate { BtAssoc.OpenDefaultAppsSettings(); };
            assocBar.Controls.Add(_btnDlBtDefaultApps);
        }

        private void DlBtSettingsToUi(DlSettings s)
        {
            _dlBtInboundOn = s.BtInbound;
            _chkDlBtMapping.Checked = s.BtPortMapping;
            DlSetNum(_numDlBtPort, s.BtPort);
            _cmbDlBtEncryption.SelectedIndex = Math.Max(0, Math.Min(2, (int)s.BtEncryption));
            _chkDlBtDht.Checked = s.BtDht;
            _chkDlBtPex.Checked = s.BtPex;
            _chkDlBtLsd.Checked = s.BtLsd;
            DlSetNum(_numDlBtActive, s.BtMaxActive);
            DlSetNum(_numDlBtUp, s.BtUpKBps);
            DlSetNum(_numDlBtConns, s.BtMaxConnections);
            DlSetNum(_numDlBtPerTorrent, s.BtMaxPerTorrent);
            DlSetNum(_numDlBtSlots, s.BtUploadSlots);
            _chkDlBtSeed.Checked = s.BtSeed;
            _numDlBtRatio.Value = Math.Max(_numDlBtRatio.Minimum, Math.Min(_numDlBtRatio.Maximum, s.BtRatioPercent / 100M));
            DlSetNum(_numDlBtSeedMin, s.BtSeedMinutes);
            _chkDlBtRecycle.Checked = s.BtRecycleTorrentFile;
            _chkDlBtUpdateCheck.Checked = s.BtUpdateCheck;
            _lblDlBtWatch.Text = s.BtWatchFolder.Length > 0
                ? Tr.S("Папка наблюдения: ", "Watch folder: ") + s.BtWatchFolder + Tr.S(" — новые .torrent из неё добавляются сами, с настройками по умолчанию",
                                                                                         " — new .torrent files in it are added automatically with default options")
                : Tr.S("Папка наблюдения не задана.", "No watch folder is set.");
            // Настройки и состояние брандмауэра приходят из двух фоновых чтений в любом порядке.
            if (_dlBtLoaded) DlBtFill(_dlBtFw, _dlBtAssocTorrent, _dlBtAssocMagnet);
        }

        private void DlBtUiToSettings(DlSettings ui)
        {
            ui.BtPortMapping = _chkDlBtMapping.Checked;
            int port = (int)_numDlBtPort.Value;
            ui.BtPort = port >= 1024 ? port : 0;
            ui.BtEncryption = (BtEncryption)Math.Max(0, _cmbDlBtEncryption.SelectedIndex);
            ui.BtDht = _chkDlBtDht.Checked;
            ui.BtPex = _chkDlBtPex.Checked;
            ui.BtLsd = _chkDlBtLsd.Checked;
            ui.BtMaxActive = (int)_numDlBtActive.Value;
            ui.BtUpKBps = (int)_numDlBtUp.Value;
            ui.BtMaxConnections = (int)_numDlBtConns.Value;
            ui.BtMaxPerTorrent = (int)_numDlBtPerTorrent.Value;
            ui.BtUploadSlots = (int)_numDlBtSlots.Value;
            ui.BtSeed = _chkDlBtSeed.Checked;
            ui.BtRatioPercent = (int)Math.Round(_numDlBtRatio.Value * 100M);
            ui.BtSeedMinutes = (int)_numDlBtSeedMin.Value;
            ui.BtRecycleTorrentFile = _chkDlBtRecycle.Checked;
            ui.BtUpdateCheck = _chkDlBtUpdateCheck.Checked;
        }

        // Входящие, папка наблюдения и ассоциации меняются своими кнопками, в отложенное сохранение не входят.
        private static void DlBtCopyTunables(DlSettings from, DlSettings to)
        {
            to.BtPortMapping = from.BtPortMapping;
            to.BtPort = from.BtPort;
            to.BtEncryption = from.BtEncryption;
            to.BtDht = from.BtDht;
            to.BtPex = from.BtPex;
            to.BtLsd = from.BtLsd;
            to.BtMaxActive = from.BtMaxActive;
            to.BtUpKBps = from.BtUpKBps;
            to.BtMaxConnections = from.BtMaxConnections;
            to.BtMaxPerTorrent = from.BtMaxPerTorrent;
            to.BtUploadSlots = from.BtUploadSlots;
            to.BtSeed = from.BtSeed;
            to.BtRatioPercent = from.BtRatioPercent;
            to.BtSeedMinutes = from.BtSeedMinutes;
            to.BtRecycleTorrentFile = from.BtRecycleTorrentFile;
            to.BtUpdateCheck = from.BtUpdateCheck;
        }

        private void DlBtUpdateEnabled()
        {
            if (_chkDlBtSeed == null) return;
            _numDlBtRatio.Enabled = _numDlBtSeedMin.Enabled = _chkDlBtSeed.Checked;
        }

        // Состояние правила брандмауэра и ассоциаций читается в фоне: COM брандмауэра перебирает сотни правил.
        private void DlBtLoad()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                FirewallInbound fw = Engine.FirewallState(DlPaths.ExecutablePath);
                BtAssocState torrent = BtAssocState.Off, magnet = BtAssocState.Off;
                try
                {
                    using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software"))
                    {
                        torrent = BtAssoc.TorrentState(software, DlPaths.ExecutablePath);
                        magnet = BtAssoc.MagnetState(software, DlPaths.ExecutablePath);
                    }
                }
                catch (Exception ex) { DlLog.Report(ex); }
                UiPost(delegate { DlBtFill(fw, torrent, magnet); });
            });
        }

        private void DlBtFill(FirewallInbound fw, BtAssocState torrent, BtAssocState magnet)
        {
            if (_closing || _lblDlBtInbound == null) return;
            _dlBtLoaded = true;
            _dlBtFw = fw;
            _dlBtAssocTorrent = torrent;
            _dlBtAssocMagnet = magnet;
            string text;
            switch (fw)
            {
                case FirewallInbound.Allowed:
                    text = _dlBtInboundOn ? Tr.S("● Входящие разрешены: пиры могут подключаться к вам сами.", "● Incoming connections are allowed: peers can connect to you.")
                                          : Tr.S("○ Правило брандмауэра есть, но входящие выключены.", "○ The firewall rule exists but incoming connections are off.");
                    break;
                case FirewallInbound.Blocked:
                    text = Tr.S("○ Брандмауэр запрещает входящие этой программе (например, окно Windows закрыли кнопкой «Отмена»). «Разрешить» заменит запрет правилом.",
                                "○ The firewall blocks incoming connections for this program (e.g. the Windows prompt was cancelled). “Allow” replaces the block with a rule.");
                    break;
                case FirewallInbound.None:
                    text = Tr.S("○ Входящие закрыты: правила брандмауэра для программы нет.", "○ Incoming connections are closed: there is no firewall rule for the program.");
                    break;
                default:
                    text = Tr.S("○ Состояние брандмауэра не прочитано — служба брандмауэра недоступна.", "○ The firewall state could not be read — the firewall service is unavailable.");
                    break;
            }
            _lblDlBtInbound.Text = text;
            _btnDlBtAllow.Enabled = !(fw == FirewallInbound.Allowed && _dlBtInboundOn);
            _btnDlBtClose.Enabled = _dlBtInboundOn || fw == FirewallInbound.Allowed;

            _chkDlBtAssocTorrent.Checked = torrent != BtAssocState.Off;
            _chkDlBtAssocMagnet.Checked = magnet != BtAssocState.Off;
            bool settings = torrent == BtAssocState.NeedsSettings || magnet == BtAssocState.NeedsSettings;
            _lblDlBtAssoc.Text = settings
                ? Tr.S("В Windows для " + (torrent == BtAssocState.NeedsSettings && magnet == BtAssocState.NeedsSettings ? ".torrent и magnet" : torrent == BtAssocState.NeedsSettings ? ".torrent" : "magnet")
                       + " выбрано другое приложение. Программа этот выбор не перебивает — поменяйте его в «Приложениях по умолчанию».",
                       "Windows has another app chosen for " + (torrent == BtAssocState.NeedsSettings && magnet == BtAssocState.NeedsSettings ? ".torrent and magnet" : torrent == BtAssocState.NeedsSettings ? ".torrent" : "magnet")
                       + ". The program does not override that choice — change it in Default apps.")
                : Tr.S("Записи — только в реестре текущего пользователя; при выключении возвращается прежнее приложение.",
                       "Entries go to the current user's registry only; turning it off restores the previous app.");
            _lblDlBtAssoc.Name = settings ? "warn" : "muted";
            _lblDlBtAssoc.ForeColor = settings ? DlWarnColor() : _theme.Subtle;
            _btnDlBtDefaultApps.Visible = settings;
        }

        // Одно окно UAC — правило брандмауэра, затем входящие включаются в настройках и процесс перепроверяет правило.
        private void DlBtAllowInbound()
        {
            _btnDlBtAllow.Enabled = false;
            DlSetInfo(Tr.S("Добавляю правило брандмауэра — Windows спросит права администратора…", "Adding the firewall rule — Windows will ask for administrator rights…"));
            Thread t = new Thread(delegate()
            {
                string error = null;
                if (Engine.FirewallState(DlPaths.ExecutablePath) != FirewallInbound.Allowed)
                {
                    ElevJob job = new ElevJob();
                    job.Kind = "firewall";
                    job.Flag = true;
                    ElevResult r = Elevation.Run(_engine, job, null, null);
                    if (!r.Ok) error = r.Declined ? DeclinedNote() : (r.Message ?? "netsh");
                }
                if (error == null) error = DlApplySettings(delegate(DlSettings s) { s.BtInbound = true; });
                if (error == null) DlBtRecheckInbound();
                UiPost(delegate
                {
                    DlSetInfo(error == null ? Tr.S("Входящие разрешены.", "Incoming connections are allowed.") : Tr.S("Не выполнено: ", "Not done: ") + error);
                    if (_dlSetView != null && _dlSetView.Visible) { DlLoadSettingsView(); DlBtLoad(); }
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Закрыть можно без прав: слушатель выключается настройкой. Правило брандмауэра остаётся — снять его может только администратор,
        // и открытое правило без слушателя ничего не пропускает.
        private void DlBtCloseInbound()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = DlApplySettings(delegate(DlSettings s) { s.BtInbound = false; });
                if (error == null) DlBtRecheckInbound();
                UiPost(delegate
                {
                    DlSetInfo(error == null ? Tr.S("Входящие закрыты. Правило брандмауэра оставлено — без открытого порта оно ничего не пропускает.",
                                                   "Incoming connections are closed. The firewall rule stays — without a listening port it lets nothing in.")
                                            : Tr.S("Не сохранено: ", "Not saved: ") + error);
                    if (_dlSetView != null && _dlSetView.Visible) { DlLoadSettingsView(); DlBtLoad(); }
                });
            });
        }

        private static void DlBtRecheckInbound()
        {
            try { if (DlIpc.IsRunning()) DlClient.Call(DlClient.Command("recheckInbound"), 1000); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private void DlBtPickWatchFolder()
        {
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = Tr.S("Папка, из которой новые .torrent добавляются сами", "The folder whose new .torrent files are added automatically");
                dlg.ShowNewFolderButton = true;
                string current = _dlSetShown != null && _dlSetShown.BtWatchFolder.Length > 0 ? _dlSetShown.BtWatchFolder : DlPaths.DefaultFolder;
                try { if (Directory.Exists(current)) dlg.SelectedPath = current; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string picked = dlg.SelectedPath;
                string why;
                if (DlFiles.CheckFolder(picked, out why) == null) { DlSetInfo(Tr.S("Папка не подходит: ", "The folder does not fit: ") + why); return; }
                DlChangeSettings(delegate(DlSettings s) { s.BtWatchFolder = picked; }, Tr.S("Папка наблюдения: ", "Watch folder: ") + picked);
            }
        }

        private void DlBtSetAssoc(bool torrentFiles, bool on)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = null;
                BtAssocState state = BtAssocState.Off;
                try
                {
                    using (RegistryKey software = Registry.CurrentUser.CreateSubKey("Software"))
                    {
                        string exe = DlPaths.ExecutablePath;
                        if (torrentFiles) { if (on) state = BtAssoc.EnableTorrent(software, exe); else BtAssoc.DisableTorrent(software, exe); }
                        else { if (on) state = BtAssoc.EnableMagnet(software, exe); else BtAssoc.DisableMagnet(software, exe); }
                    }
                    BtAssoc.NotifyShell();
                }
                catch (Exception ex) { error = ex.Message; DlLog.Report(ex); }
                UiPost(delegate
                {
                    string what = torrentFiles ? Tr.S("Файлы .torrent", ".torrent files") : Tr.S("magnet-ссылки", "Magnet links");
                    DlSetInfo(error != null ? Tr.S("Не выполнено: ", "Not done: ") + error
                              : !on ? what + Tr.S(" больше не открываются программой.", " no longer open with the program.")
                              : state == BtAssocState.NeedsSettings ? what + Tr.S(": выберите программу в «Приложениях по умолчанию».", ": pick the program in Default apps.")
                              : what + Tr.S(" открываются программой.", " open with the program."));
                    if (error == null && on && state == BtAssocState.NeedsSettings) BtAssoc.OpenDefaultAppsSettings();
                    DlBtLoad();
                });
            });
        }
    }
}
