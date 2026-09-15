// SysDeck — вкладка «Загрузки»: раздел настроек «Браузеры» — связь с расширением, правила перехвата, установка.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Регистрацию хоста (ключи HKCU NativeMessagingHosts) пишет процесс загрузок при старте и при смене настроек; окно
// перепроверяет её при открытии раздела — программу могли перенести. Копия со своей папкой данных браузеры не регистрирует.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Downloads;

namespace SysDeck
{
    public partial class MainForm
    {
        private CheckBox _chkDlBrInteg, _chkDlBrIntercept, _chkDlBrIncognito;
        private TextBox _txtDlBrSkipHosts, _txtDlBrSkipExt;
        private FastListView _lvDlBrowsers;
        private Label _lblDlBrCopy, _lblDlBrSteps;
        private List<DlBrowserInfo> _dlBrList = new List<DlBrowserInfo>();

        private void BuildDownloadsBrowsersSection()
        {
            DlSetSection(Tr.S("Браузеры", "Browsers"));
            _lblDlBrCopy = DlSetNote("", false);
            // Не через отложенное сохранение: переключатель сразу пишет или удаляет ключи реестра.
            _chkDlBrInteg = new CheckBox();
            _chkDlBrInteg.Text = Tr.S("Связь с браузерами — расширение передаёт загрузки программе", "Browser integration — the extension hands downloads to the app");
            _chkDlBrInteg.AutoSize = true;
            _chkDlBrInteg.Margin = new Padding(0, 2, 0, 4);
            _chkDlBrInteg.Click += delegate { DlBrSetIntegration(_chkDlBrInteg.Checked); };
            _dlSetBody.Controls.Add(_chkDlBrInteg);
            _chkDlBrIntercept = DlSetCheck(Tr.S("Забирать у браузера все загрузки, любого размера", "Take every browser download, of any size"));
            _chkDlBrIncognito = DlSetCheck(Tr.S("И в режиме инкогнито (приватные окна)", "In incognito (private) windows too"));
            FlowLayoutPanel skipRow = DlSetRow();
            skipRow.Controls.Add(MkFlowLabel(Tr.S("Не забирать с сайтов:", "Leave to the browser on sites:"), false));
            _txtDlBrSkipHosts = DlSetText(skipRow, 330);
            skipRow.Controls.Add(MkFlowLabel(Tr.S("Не забирать файлы:", "Leave to the browser files:"), false));
            _txtDlBrSkipExt = DlSetText(skipRow, 220);
            DlSetNote(Tr.S("Сайты и расширения — через запятую: «example.com, *.example.org», «.pdf, .html». Загрузка остаётся браузеру и тогда, когда "
                           + "при щелчке зажат Alt, и когда программа одним запросом видит, что сервер отдаёт ей не тот файл (страницу входа, другой "
                           + "размер).",
                           "Sites and extensions are comma-separated: “example.com, *.example.org”, “.pdf, .html”. A download also stays with the "
                           + "browser when Alt is held on the click, and when one request shows the server gives the app a different file (a sign-in "
                           + "page, another size)."), true);

            _lvDlBrowsers = new FastListView();
            _lvDlBrowsers.FullRowSelect = true;
            _lvDlBrowsers.MultiSelect = false;
            _lvDlBrowsers.HideSelection = false;
            _lvDlBrowsers.Size = new Size(760, 118);
            _lvDlBrowsers.Margin = new Padding(1, 6, 1, 8);
            _lvDlBrowsers.Columns.Add(Tr.S("Браузер", "Browser"), 170);
            _lvDlBrowsers.Columns.Add(Tr.S("Установлен", "Installed"), 110);
            _lvDlBrowsers.Columns.Add(Tr.S("Связь с программой", "Link to the app"), 180);
            _lvDlBrowsers.Columns.Add(Tr.S("Расширение", "Extension"), 290);
            SetupOwnerDraw(_lvDlBrowsers);
            _lvDlBrowsers.SelectedIndexChanged += delegate { DlBrShowSteps(); };
            _dlSetBody.Controls.Add(_lvDlBrowsers);
            FlowLayoutPanel brBar = DlSetRow();
            Button install = MkFlowButton(Tr.S("Установить расширение…", "Install the extension…"), 210, false);
            install.Click += delegate { DlBrInstall(); };
            Button refresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 110, false);
            refresh.Click += delegate { DlBrLoad(); };
            brBar.Controls.AddRange(new Control[] { install, refresh });
            _lblDlBrSteps = DlSetNote("", true);
        }

        private void DlBrSettingsToUi(DlSettings s)
        {
            _chkDlBrInteg.Checked = s.BrowserIntegration;
            _chkDlBrIntercept.Checked = s.BrowserIntercept;
            _chkDlBrIncognito.Checked = s.BrowserIncognito;
            string hosts = string.Join(", ", s.BrowserSkipHosts.ToArray());
            if (_txtDlBrSkipHosts.Text != hosts) _txtDlBrSkipHosts.Text = hosts;
            List<string> exts = new List<string>();
            foreach (string e in s.BrowserSkipExt) exts.Add("." + e);
            string extText = string.Join(", ", exts.ToArray());
            if (_txtDlBrSkipExt.Text != extText) _txtDlBrSkipExt.Text = extText;
            _chkDlBrIntercept.Enabled = _chkDlBrIncognito.Enabled = _txtDlBrSkipHosts.Enabled = _txtDlBrSkipExt.Enabled = s.BrowserIntegration;
        }

        private void DlBrUiToSettings(DlSettings ui)
        {
            ui.BrowserIntercept = _chkDlBrIntercept.Checked;
            ui.BrowserIncognito = _chkDlBrIncognito.Checked;
            foreach (string part in _txtDlBrSkipHosts.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string host = part.Trim().ToLowerInvariant();
                if (host.IndexOfAny(new[] { '/', '\\', ':' }) < 0 && !ui.BrowserSkipHosts.Contains(host)) ui.BrowserSkipHosts.Add(host);
            }
            foreach (string part in _txtDlBrSkipExt.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string ext = part.Trim().TrimStart('.').ToLowerInvariant();
                if (ext.Length > 0 && ext.IndexOfAny(new[] { '/', '\\', ':', '.' }) < 0 && !ui.BrowserSkipExt.Contains(ext)) ui.BrowserSkipExt.Add(ext);
            }
        }

        private static void DlBrCopyTunables(DlSettings from, DlSettings to)
        {
            to.BrowserIntercept = from.BrowserIntercept;
            to.BrowserIncognito = from.BrowserIncognito;
            to.BrowserSkipHosts.Clear();
            to.BrowserSkipHosts.AddRange(from.BrowserSkipHosts);
            to.BrowserSkipExt.Clear();
            to.BrowserSkipExt.AddRange(from.BrowserSkipExt);
        }

        // Выключение удаляет наши ключи из реестра: браузер перестаёт запускать программу, расширение показывает «не найдена».
        private void DlBrSetIntegration(bool on)
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = DlApplySettings(delegate(DlSettings s) { s.BrowserIntegration = on; });
                if (error == null && DlBrowsers.IsDefaultCopy) error = DlBrowsers.EnsureDefault(DlReadSettings());
                UiPost(delegate
                {
                    DlSetInfo(error != null ? Tr.S("Не сохранено: ", "Not saved: ") + error
                              : on ? Tr.S("Связь с браузерами включена.", "Browser integration is on.")
                              : Tr.S("Связь с браузерами выключена, записи в реестре удалены.", "Browser integration is off, the registry entries are removed."));
                    if (_dlSetView != null && _dlSetView.Visible) DlLoadSettingsView();
                });
            });
        }

        // Состояние браузеров: перепроверка регистрации (exe могли перенести) и последний выход расширения на связь.
        private void DlBrLoad()
        {
            ThreadPool.QueueUserWorkItem(delegate
            {
                DlSettings s = DlReadSettings();
                if (DlBrowsers.IsDefaultCopy) DlBrowsers.EnsureDefault(s);
                List<DlBrowserInfo> list = DlBrowsers.Status();
                UiPost(delegate { DlBrFill(list); });
            });
        }

        private void DlBrFill(List<DlBrowserInfo> list)
        {
            if (_closing || _lvDlBrowsers == null) return;
            string selected = DlBrSelected() != null ? DlBrSelected().Key : null;
            _dlBrList = list;
            _lblDlBrCopy.Text = DlBrowsers.IsDefaultCopy ? ""
                : Tr.S("Эта копия программы работает со своей папкой данных — браузеры с ней не связываются. Связь регистрирует только копия с папкой данных по умолчанию.",
                       "This copy of the app uses its own data folder — browsers do not connect to it. Only the copy with the default data folder registers the link.");
            _lblDlBrCopy.Visible = !DlBrowsers.IsDefaultCopy;
            _lvDlBrowsers.BeginUpdate();
            try
            {
                _lvDlBrowsers.Items.Clear();
                foreach (DlBrowserInfo b in list)
                {
                    ListViewItem it = new ListViewItem(b.Name);
                    it.SubItems.Add(b.Installed ? Tr.S("да", "yes") : Tr.S("не найден", "not found"));
                    it.SubItems.Add(!DlBrowsers.IsDefaultCopy ? Tr.S("не у этой копии", "not for this copy")
                                    : b.Registered ? Tr.S("зарегистрирована", "registered") : Tr.S("нет", "no"));
                    it.SubItems.Add(b.LastSeenUtc == DateTime.MinValue ? Tr.S("ещё не выходило на связь", "has not connected yet")
                                    : Tr.S("на связи ", "connected ") + b.LastSeenUtc.ToLocalTime().ToString("dd.MM HH:mm")
                                      + (b.ExtVersion.Length > 0 ? ", " + Tr.S("версия ", "version ") + b.ExtVersion : ""));
                    _lvDlBrowsers.Items.Add(it);
                }
                int pick = -1;
                for (int i = 0; i < list.Count; i++)
                    if (list[i].Key == selected || (selected == null && pick < 0 && list[i].Installed)) pick = i;
                if (pick >= 0) _lvDlBrowsers.Items[pick].Selected = true;
            }
            finally { _lvDlBrowsers.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvDlBrowsers);
            DlBrShowSteps();
        }

        private DlBrowserInfo DlBrSelected()
        {
            if (_lvDlBrowsers == null || _lvDlBrowsers.SelectedIndices.Count == 0) return null;
            int i = _lvDlBrowsers.SelectedIndices[0];
            return i < _dlBrList.Count ? _dlBrList[i] : null;
        }

        private void DlBrShowSteps()
        {
            DlBrowserInfo b = DlBrSelected();
            if (b == null) { _lblDlBrSteps.Text = Tr.S("Выберите браузер в списке.", "Pick a browser in the list."); return; }
            string dir = DlBrowsers.ExtensionDir(b.Family);
            _lblDlBrSteps.Text = b.Family == "firefox"
                ? Tr.S("Firefox: «Установить расширение…» распакует его в " + dir + " и откроет about:debugging → «Этот Firefox» → «Загрузить временное "
                       + "дополнение…» → выберите manifest.json из этой папки. Временное дополнение работает до перезапуска Firefox; постоянная "
                       + "установка — подписанным файлом из каталога дополнений Mozilla.",
                       "Firefox: “Install the extension…” unpacks it to " + dir + " and opens about:debugging → “This Firefox” → “Load Temporary "
                       + "Add-on…” → pick manifest.json in that folder. A temporary add-on lasts until Firefox restarts; a permanent install is "
                       + "the signed file from the Mozilla add-ons site.")
                : Tr.S(b.Name + ": «Установить расширение…» распакует его в " + dir + " и откроет страницу расширений браузера. Там включите "
                       + "«Режим разработчика», нажмите «Загрузить распакованное» и выберите эту папку. Если страница не открылась — наберите "
                       + DlBrowsers.ExtensionsPage(b.Key) + " в адресной строке.",
                       b.Name + ": “Install the extension…” unpacks it to " + dir + " and opens the browser's extensions page. Turn on "
                       + "“Developer mode”, press “Load unpacked” and pick that folder. If the page did not open, type "
                       + DlBrowsers.ExtensionsPage(b.Key) + " in the address bar.");
        }

        private void DlBrInstall()
        {
            DlBrowserInfo b = DlBrSelected();
            if (b == null) { DlSetInfo(Tr.S("Выберите браузер в списке.", "Pick a browser in the list.")); return; }
            ThreadPool.QueueUserWorkItem(delegate
            {
                string dir = DlBrowsers.ExtensionDir(b.Family);
                int files;
                string error = DlBrowsers.Unpack(b.Family, dir, out files);
                string opened = error == null ? DlBrowsers.OpenExtensionsPage(b) : null;
                UiPost(delegate
                {
                    if (error != null)
                    {
                        DlSetInfo(Tr.S("Расширение не распаковано: ", "The extension is not unpacked: ") + error);
                        return;
                    }
                    OpenInExplorer(dir, false);
                    DlSetInfo(opened == null
                        ? Tr.S("Расширение в папке " + dir + " — дальше шаги под списком.", "The extension is in " + dir + " — the steps are below the list.")
                        : Tr.S("Расширение в папке " + dir + "; браузер не открылся: " + opened, "The extension is in " + dir + "; the browser did not open: " + opened));
                });
            });
        }
    }
}
