// Windows Process Cleaner — вкладка «Захват»: фоновый процесс снимков экрана, горячие клавиши, папки и уведомления.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using WindowsProcessCleaner.Capture;

namespace WindowsProcessCleaner
{
    public partial class MainForm
    {
        // Страница сама ничего не снимает: снимки делает фоновый процесс (--capture, обычные права) — горячие клавиши
        // нужны и при закрытом окне. Здесь его настройки (capture\settings.json) и сигналы ему именованными событиями.
        private Panel _capScroll;
        private FlowLayoutPanel _capBody;
        private Label _lblCapStatus, _lblCapInfo, _lblCapKeysNote, _lblCapGameCenter, _lblCapFolder, _lblCapPreview, _lblCapPrtScn;
        private Button _btnCapStart, _btnCapStop, _btnCapTry, _btnCapImport;
        private CheckBox _chkCapAutostart, _chkCapPerApp, _chkCapCursor, _chkCapSound, _chkCapToasts, _chkCapDefer, _chkCapPrtScn, _chkCapGallery;
        private RoundComboBox _cmbCapFormat, _cmbCapAfter, _cmbCapScreen, _cmbCapDelay, _cmbCapToastSec, _cmbCapQuality;
        private TextBox _txtCapTemplate;
        private Label _lblCapVideoFolder, _lblCapVideoNote, _lblCapMicLevel, _lblCapMicNote;
        private CheckBox _chkCapVideoCursor, _chkCapRecPanel, _chkCapSysAudio, _chkCapWindowAudio, _chkCapMic, _chkCapMicMono, _chkCapTracks;
        private RoundComboBox _cmbCapCodec, _cmbCapEncoder, _cmbCapVQuality, _cmbCapFps, _cmbCapHeight, _cmbCapCountdown, _cmbCapLimit,
                              _cmbCapMicDevice, _cmbCapMicVolume;
        private Button _btnCapRec, _btnCapMicTest;
        private List<AudioDeviceInfo> _capMics = new List<AudioDeviceInfo>();
        private MicLevelMeter _capMeter;
        private System.Windows.Forms.Timer _capMeterTick;
        private DateTime _capMeterEnds;
        private readonly Dictionary<CapAction, HotkeyBox> _capKeyBoxes = new Dictionary<CapAction, HotkeyBox>();
        private readonly Dictionary<CapAction, Label> _capKeyStatus = new Dictionary<CapAction, Label>();
        private System.Windows.Forms.Timer _capTick, _capTemplateSave;
        private bool _capLoading;
        private int _capBusy;

        private static readonly int[] CapQualities = { 100, 95, 92, 85, 75 };
        private static readonly int[] CapToastSeconds = { 3, 4, 6, 8, 10, 15 };
        private static readonly string[] CapCodecs = { "h264", "hevc", "av1" };
        private static readonly string[] CapEncoders = { "auto", "nvidia", "amd", "intel", "software" };
        private static readonly string[] CapVideoQualities = { "low", "optimal", "high", "max" };
        private static readonly int[] CapHeights = { 0, 1080, 720 };
        private static readonly int[] CapCountdowns = { 0, 3, 5 };
        private static readonly int[] CapLimits = { 0, 10, 30, 60, 120 };
        private static readonly int[] CapMicVolumes = { 50, 75, 100, 150, 200, 300 };

        private Control BuildCaptureTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(18, 14, 18, 14);
            // Та же раскладка, что у «Размеров папок»: непристыкованный столбик в прокручиваемой обёртке.
            _capScroll = new Panel();
            _capScroll.Dock = DockStyle.Fill;
            _capScroll.AutoScroll = true;
            tab.Controls.Add(_capScroll);
            _capBody = new FlowLayoutPanel();
            _capBody.Location = new Point(0, 0);
            _capBody.AutoSize = true;
            _capBody.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            _capBody.FlowDirection = FlowDirection.TopDown;
            _capBody.WrapContents = false;
            _capScroll.Controls.Add(_capBody);

            CapSection(Tr.S("Фоновый процесс", "Background process"));
            CapNote(Tr.S("Скриншоты по горячим клавишам: область, экран, активное окно. Работает отдельным процессом без прав "
                         + "администратора — и тогда, когда это окно закрыто. Снимки не отправляются в интернет.",
                         "Screenshots by hotkeys: region, screen, active window. Runs as a separate process without administrator "
                         + "rights — even while this window is closed. Nothing is uploaded anywhere."), true);
            _lblCapStatus = CapNote("", false);
            _lblCapStatus.Font = new Font(Font.FontFamily, 10.5F, FontStyle.Bold);
            FlowLayoutPanel bar = CapRow();
            _btnCapStart = MkFlowButton(Tr.S("Запустить", "Start"), 130, true);
            _btnCapStart.Click += delegate { CapStart(); };
            _btnCapStop = MkFlowButton(Tr.S("Остановить", "Stop"), 130, false);
            _btnCapStop.Click += delegate { CapStop(); };
            _btnCapTry = MkFlowButton(Tr.S("Снимок области", "Region screenshot"), 160, false);
            _btnCapTry.Click += delegate { CapShot("ShotRegion"); };
            _btnCapRec = MkFlowButton(Tr.S("Видео области", "Region video"), 150, false);
            _btnCapRec.Click += delegate { CapShot("RecRegion"); };
            bar.Controls.AddRange(new Control[] { _btnCapStart, _btnCapStop, _btnCapTry, _btnCapRec });
            _chkCapAutostart = CapCheck(Tr.S("Запускать вместе с Windows", "Start with Windows"));

            CapSection(Tr.S("Горячие клавиши", "Hotkeys"));
            CapNote(Tr.S("Щёлкните поле и нажмите сочетание. Backspace — убрать сочетание, Esc — оставить как было.",
                         "Click a field and press the shortcut. Backspace clears it, Esc keeps the old one."), true);
            foreach (CapAction a in CapActions.All) CapHotkeyRow(a);
            _lblCapKeysNote = CapNote("", false);
            _lblCapKeysNote.Name = "warn";
            _lblCapGameCenter = CapNote("", false);
            _lblCapGameCenter.Name = "warn";
            FlowLayoutPanel keyBar = CapRow();
            _btnCapImport = MkFlowButton(Tr.S("Перенести из VK Play GameCenter", "Import from VK Play GameCenter"), 220, false);
            _btnCapImport.Click += delegate { CapImportGameCenter(); };
            Button defaults = MkFlowButton(Tr.S("Сочетания по умолчанию", "Default shortcuts"), 200, false);
            defaults.Click += delegate { CapResetHotkeys(); };
            keyBar.Controls.AddRange(new Control[] { _btnCapImport, defaults });
            _chkCapPrtScn = CapCheck(Tr.S("Не отдавать PrtScn «Ножницам» Windows", "Don't give PrtScn to the Windows Snipping Tool"));
            _lblCapPrtScn = CapNote(Tr.S("Нужно, только если сочетание с PrtScn назначено здесь. Меняет параметр Windows «Использовать кнопку PrtScn "
                                         + "для открытия ножниц» (HKCU); при снятии галочки прежнее значение возвращается.",
                                         "Only needed if a PrtScn shortcut is set here. Changes the Windows setting “Use the Print screen key to open "
                                         + "screen capture” (HKCU); unticking restores the previous value."), true);

            CapSection(Tr.S("Сохранение", "Saving"));
            _lblCapFolder = CapNote("", false);
            FlowLayoutPanel folderBar = CapRow();
            Button browse = MkFlowButton(Tr.S("Выбрать папку…", "Choose folder…"), 150, false);
            browse.Click += delegate { CapPickFolder(false); };
            Button openFolder = MkFlowButton(Tr.S("Открыть папку", "Open folder"), 140, false);
            openFolder.Click += delegate { CapOpenFolder(false); };
            Button defFolder = MkFlowButton(Tr.S("По умолчанию", "Default"), 130, false);
            defFolder.Click += delegate { CapChange(delegate(CapSettings s) { s.ShotFolder = ""; }); };
            Button gallery = MkFlowButton(Tr.S("Галерея…", "Gallery…"), 130, false);
            gallery.Click += delegate { CapTrayShot("Gallery"); };
            folderBar.Controls.AddRange(new Control[] { browse, openFolder, defFolder, gallery });
            _chkCapPerApp = CapCheck(Tr.S("Раскладывать по папкам программ (Chrome, Desktop, игра…)", "Sort into per-program folders (Chrome, Desktop, a game…)"));
            _chkCapGallery = CapCheck(Tr.S("Открывать галерею при запуске", "Open the gallery at startup"));
            CapNote(Tr.S("Галерея — отдельное окно со снимками и видео: превью, выделение мышью, копирование, перетаскивание в другую "
                         + "программу и удаление в Корзину. Открывается тем же сочетанием, что и из уведомления о снимке.",
                         "The gallery is a separate window with screenshots and videos: previews, mouse selection, copying, dragging into "
                         + "another program and deletion to the Recycle Bin. Opens with the same shortcut as from a screenshot toast."), true);

            FlowLayoutPanel nameRow = CapRow();
            nameRow.Controls.Add(MkFlowLabel(Tr.S("Имя файла:", "File name:"), false));
            _txtCapTemplate = new TextBox();
            _txtCapTemplate.Width = 300;
            _txtCapTemplate.Margin = new Padding(2, 9, 16, 8);
            _txtCapTemplate.TextChanged += delegate
            {
                if (_capLoading) return;
                CapUpdatePreview();
                _capTemplateSave.Stop();
                _capTemplateSave.Start();
            };
            nameRow.Controls.Add(_txtCapTemplate);
            _capTemplateSave = new System.Windows.Forms.Timer();
            _capTemplateSave.Interval = 700;
            _capTemplateSave.Tick += delegate
            {
                _capTemplateSave.Stop();
                string text = _txtCapTemplate.Text.Trim();
                CapChange(delegate(CapSettings s) { s.NameTemplate = text.Length == 0 ? NameTemplate.Default : text; });
            };
            _lblCapPreview = CapNote("", true);
            CapNote(Tr.S("{app} — программа, остальное в фигурных скобках — дата и время: {yyyy-MM-dd_HH-mm-ss}, {dd.MM.yy}, {HHmm}. "
                         + "Одинаковые имена получают _2, _3…",
                         "{app} is the program, anything else in braces is a date/time format: {yyyy-MM-dd_HH-mm-ss}, {dd.MM.yy}, {HHmm}. "
                         + "Duplicate names get _2, _3…"), true);

            FlowLayoutPanel fmtRow = CapRow();
            _cmbCapFormat = CapCombo(fmtRow, Tr.S("Формат:", "Format:"), 150, Tr.S("PNG — без потерь", "PNG — lossless"), "JPG");
            _cmbCapQuality = CapCombo(fmtRow, Tr.S("Качество JPG:", "JPG quality:"), 110, "100", "95", "92", "85", "75");
            _cmbCapAfter = CapCombo(fmtRow, Tr.S("После снимка:", "After a shot:"), 260,
                                    Tr.S("сохранить и скопировать", "save and copy"), Tr.S("только сохранить", "save only"),
                                    Tr.S("только скопировать в буфер", "copy to clipboard only"), Tr.S("открыть редактор", "open the editor"));

            CapSection(Tr.S("Снимок", "Shot"));
            FlowLayoutPanel shotRow = CapRow();
            _cmbCapScreen = CapCombo(shotRow, Tr.S("«Скриншот экрана» снимает:", "“Screen screenshot” captures:"), 230,
                                     Tr.S("монитор под курсором", "the monitor under the cursor"), Tr.S("все мониторы", "all monitors"),
                                     Tr.S("активное окно", "the active window"));
            _cmbCapDelay = CapCombo(shotRow, Tr.S("Задержка:", "Delay:"), 110, Tr.S("нет", "none"), "3 " + Tr.S("с", "s"), "5 " + Tr.S("с", "s"), "10 " + Tr.S("с", "s"));
            _chkCapCursor = CapCheck(Tr.S("Показывать курсор мыши на снимке", "Include the mouse cursor"));
            _chkCapSound = CapCheck(Tr.S("Звук затвора", "Shutter sound"));

            CapSection(Tr.S("Видео", "Video"));
            CapNote(Tr.S("Запись области, экрана или окна в MP4. Повторное нажатие той же клавиши останавливает запись; пока идёт запись, "
                         + "рядом с областью видна панель с таймером, а в трее — красный значок (щелчок по нему — стоп).",
                         "Records a region, the screen or a window to MP4. Pressing the same key again stops; while recording, a panel with a "
                         + "timer sits next to the region and a red tray icon appears (click it to stop)."), true);
            _lblCapVideoFolder = CapNote("", false);
            FlowLayoutPanel videoFolderBar = CapRow();
            Button vBrowse = MkFlowButton(Tr.S("Выбрать папку…", "Choose folder…"), 150, false);
            vBrowse.Click += delegate { CapPickFolder(true); };
            Button vOpen = MkFlowButton(Tr.S("Открыть папку", "Open folder"), 140, false);
            vOpen.Click += delegate { CapOpenFolder(true); };
            Button vDefault = MkFlowButton(Tr.S("По умолчанию", "Default"), 130, false);
            vDefault.Click += delegate { CapChange(delegate(CapSettings s) { s.VideoFolder = ""; }); };
            videoFolderBar.Controls.AddRange(new Control[] { vBrowse, vOpen, vDefault });
            FlowLayoutPanel codecRow = CapRow();
            _cmbCapCodec = CapCombo(codecRow, Tr.S("Кодек:", "Codec:"), 150, "H.264", "HEVC (H.265)", "AV1");
            _cmbCapEncoder = CapCombo(codecRow, Tr.S("Кодировщик:", "Encoder:"), 150, Tr.S("авто", "auto"), "NVIDIA", "AMD", "Intel",
                                      Tr.S("программный", "software"));
            _cmbCapVQuality = CapCombo(codecRow, Tr.S("Качество:", "Quality:"), 150, Tr.S("экономно", "compact"), Tr.S("оптимально", "balanced"),
                                       Tr.S("высокое", "high"), Tr.S("максимальное", "maximum"));
            FlowLayoutPanel frameRow = CapRow();
            _cmbCapFps = CapCombo(frameRow, Tr.S("Кадров в секунду:", "Frames per second:"), 80, "30", "60");
            _cmbCapHeight = CapCombo(frameRow, Tr.S("Размер:", "Size:"), 170, Tr.S("как у источника", "as the source"), Tr.S("не больше 1080p", "at most 1080p"),
                                     Tr.S("не больше 720p", "at most 720p"));
            _cmbCapCountdown = CapCombo(frameRow, Tr.S("Отсчёт:", "Countdown:"), 90, Tr.S("нет", "none"), "3 " + Tr.S("с", "s"), "5 " + Tr.S("с", "s"));
            _cmbCapLimit = CapCombo(frameRow, Tr.S("Не дольше:", "At most:"), 160, Tr.S("без ограничения", "no limit"), "10 " + Tr.S("мин", "min"),
                                    "30 " + Tr.S("мин", "min"), "60 " + Tr.S("мин", "min"), "120 " + Tr.S("мин", "min"));
            _lblCapVideoNote = CapNote("", false);
            _lblCapVideoNote.Name = "warn";
            _chkCapVideoCursor = CapCheck(Tr.S("Показывать курсор мыши в видео", "Include the mouse cursor in videos"));
            _chkCapRecPanel = CapCheck(Tr.S("Рамка области и панель записи (в видео не попадают)", "Region frame and recording panel (not recorded)"));
            FlowLayoutPanel encBar = CapRow();
            Button encCheck = MkFlowButton(Tr.S("Проверить кодировщики", "Check encoders"), 200, false);
            encCheck.Click += delegate { CapCheckEncoders(); };
            encBar.Controls.Add(encCheck);

            CapSection(Tr.S("Звук в видео", "Sound in videos"));
            _chkCapSysAudio = CapCheck(Tr.S("Звук системы (всё, что слышно в колонках)", "System sound (everything you hear)"));
            _chkCapWindowAudio = CapCheck(Tr.S("При записи окна — только его звук (Windows 10 2004 и новее)", "When recording a window — only its sound (Windows 10 2004+)"));
            _chkCapMic = CapCheck(Tr.S("Микрофон", "Microphone"));
            FlowLayoutPanel micRow = CapRow();
            _cmbCapMicDevice = CapCombo(micRow, Tr.S("Устройство:", "Device:"), 300, Tr.S("по умолчанию в Windows", "Windows default"));
            _cmbCapMicVolume = CapCombo(micRow, Tr.S("Громкость:", "Volume:"), 90, "50 %", "75 %", "100 %", "150 %", "200 %", "300 %");
            _chkCapMicMono = CapCheck(Tr.S("Микрофон в моно (голос одинаково в обоих каналах)", "Mono microphone (voice in both channels)"));
            FlowLayoutPanel micBar = CapRow();
            _btnCapMicTest = MkFlowButton(Tr.S("Проверить микрофон", "Test the microphone"), 180, false);
            _btnCapMicTest.Click += delegate { CapToggleMicTest(); };
            micBar.Controls.Add(_btnCapMicTest);
            _lblCapMicLevel = MkFlowLabel("", false);
            micBar.Controls.Add(_lblCapMicLevel);
            _lblCapMicNote = CapNote("", false);
            _lblCapMicNote.Name = "warn";
            _chkCapTracks = CapCheck(Tr.S("Отдельные дорожки для монтажа: 1 — общий звук, 2 — система, 3 — микрофон",
                                          "Separate tracks for editing: 1 — mixed, 2 — system, 3 — microphone"));

            CapSection(Tr.S("Уведомления", "Notifications"));
            _chkCapToasts = CapCheck(Tr.S("Показывать уведомление с миниатюрой (ошибки показываются всегда)",
                                          "Show a notification with a thumbnail (errors are always shown)"));
            FlowLayoutPanel toastRow = CapRow();
            _cmbCapToastSec = CapCombo(toastRow, Tr.S("Держать:", "Keep for:"), 110, "3 " + Tr.S("с", "s"), "4 " + Tr.S("с", "s"), "6 " + Tr.S("с", "s"),
                                       "8 " + Tr.S("с", "s"), "10 " + Tr.S("с", "s"), "15 " + Tr.S("с", "s"));
            _chkCapDefer = CapCheck(Tr.S("В полноэкранной игре — показать после выхода из неё", "In a fullscreen game — show after leaving it"));

            _lblCapInfo = CapNote("", true);
            FlowLayoutPanel tail = CapRow();
            Button dataDir = MkFlowButton(Tr.S("Папка данных", "Data folder"), 150, false);
            dataDir.Click += delegate
            {
                try
                {
                    Directory.CreateDirectory(CapPaths.DataDir);
                    Process.Start("explorer.exe", "\"" + CapPaths.DataDir + "\"");
                }
                catch (Exception ex) { CapInfo(Tr.S("Не удалось открыть папку данных: ", "Could not open the data folder: ") + ex.Message); }
            };
            tail.Controls.Add(dataDir);

            _capScroll.Resize += delegate { CapWrapNotes(); };
            CapLoadToUi();
            return tab;
        }

        // ---------- строительные блоки ----------

        private void CapSection(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            l.Margin = new Padding(0, _capBody.Controls.Count == 0 ? 0 : 14, 0, 6);
            _capBody.Controls.Add(l);
        }

        private Label CapNote(string text, bool muted)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Margin = new Padding(0, 0, 0, 6);
            if (muted) { l.Name = "muted"; l.Font = new Font(Font.FontFamily, 9.5F); }
            _capBody.Controls.Add(l);
            return l;
        }

        private FlowLayoutPanel CapRow()
        {
            FlowLayoutPanel f = new FlowLayoutPanel();
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.WrapContents = true;
            f.Margin = new Padding(0, 2, 0, 2);
            _capBody.Controls.Add(f);
            return f;
        }

        private CheckBox CapCheck(string text)
        {
            CheckBox c = new CheckBox();
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 2, 0, 4);
            c.CheckedChanged += delegate { CapControlsChanged(c); };
            _capBody.Controls.Add(c);
            return c;
        }

        private RoundComboBox CapCombo(FlowLayoutPanel row, string label, int width, params string[] items)
        {
            row.Controls.Add(MkFlowLabel(label, false));
            RoundComboBox cb = new RoundComboBox();
            cb.DropDownStyle = ComboBoxStyle.DropDownList;
            cb.Width = width;
            cb.Margin = new Padding(0, 4, 24, 8);
            cb.Items.AddRange(items);
            cb.SelectedIndexChanged += delegate { CapControlsChanged(cb); };
            row.Controls.Add(cb);
            return cb;
        }

        private void CapHotkeyRow(CapAction action)
        {
            FlowLayoutPanel row = CapRow();
            row.WrapContents = false;
            Label title = MkFlowLabel(CapActions.Title(action), false);
            title.AutoSize = false;
            title.Size = new Size(300, 26);
            row.Controls.Add(title);
            HotkeyBox box = new HotkeyBox();
            box.Width = 170;
            box.Margin = new Padding(2, 9, 8, 8);
            box.Enabled = CapFeatures.Available(action);
            box.CaptureStarted += delegate { CapIpc.Signal("HotkeysOff"); };
            box.CaptureEnded += delegate { CapIpc.Signal("HotkeysOn"); };
            box.HotkeyPicked += delegate(HotkeySpec spec)
            {
                CapChange(delegate(CapSettings s) { s.Hotkeys[action] = spec.ToString(); });
            };
            row.Controls.Add(box);
            Label status = MkFlowLabel("", false);
            row.Controls.Add(status);
            _capKeyBoxes[action] = box;
            _capKeyStatus[action] = status;
        }

        private void CapWrapNotes()
        {
            if (_capScroll == null) return;
            int w = Math.Max(Px(300), _capScroll.ClientSize.Width - Px(24));
            foreach (Control c in _capBody.Controls)
                if (c is Label) c.MaximumSize = new Size(w, 0);
            _capBody.PerformLayout();
            _capScroll.PerformLayout();
        }

        private void CapInfo(string text)
        {
            if (_lblCapInfo != null) _lblCapInfo.Text = text ?? "";
        }

        // ---------- вход и уход ----------

        private void CaptureEnter()
        {
            CapLoadToUi();
            CapWrapNotes();
            if (_capTick == null)
            {
                // Статус меняется и без страницы: сочетание освободилось, процесс остановили из командной строки.
                _capTick = new System.Windows.Forms.Timer();
                _capTick.Interval = 2000;
                _capTick.Tick += delegate { CapRefreshStatus(); };
            }
            _capTick.Start();
        }

        private void CaptureLeave()
        {
            if (_capTick != null) _capTick.Stop();
            CapStopMicTest(null);
        }

        // ---------- настройки ----------

        private void CapLoadToUi()
        {
            CapSettings s = CapSettings.Load();
            _capLoading = true;
            try
            {
                _chkCapAutostart.Checked = CapLauncher.IsAutostartEnabled();
                foreach (KeyValuePair<CapAction, HotkeyBox> kv in _capKeyBoxes) kv.Value.Spec = s.Hotkey(kv.Key);
                _chkCapPrtScn.Checked = s.PrintScreenOverride;
                _chkCapPerApp.Checked = s.PerAppFolders;
                _chkCapGallery.Checked = s.GalleryOnStart;
                if (!_txtCapTemplate.Focused) _txtCapTemplate.Text = s.NameTemplate;
                _cmbCapFormat.SelectedIndex = s.ImageFormat == "jpg" ? 1 : 0;
                int q = Array.IndexOf(CapQualities, s.JpegQuality);
                _cmbCapQuality.SelectedIndex = q >= 0 ? q : 2;
                _cmbCapQuality.Enabled = s.ImageFormat == "jpg";
                _cmbCapAfter.SelectedIndex = s.After == ShotAfter.Save ? 1 : s.After == ShotAfter.CopyOnly ? 2 : s.After == ShotAfter.OpenEditor ? 3 : 0;
                _cmbCapScreen.SelectedIndex = (int)s.ScreenKey;
                _cmbCapDelay.SelectedIndex = s.DelaySeconds == 3 ? 1 : s.DelaySeconds == 5 ? 2 : s.DelaySeconds == 10 ? 3 : 0;
                _chkCapCursor.Checked = s.CursorInShots;
                _chkCapSound.Checked = s.ShutterSound;
                _chkCapToasts.Checked = s.ToastEnabled;
                int t = Array.IndexOf(CapToastSeconds, s.ToastSeconds);
                _cmbCapToastSec.SelectedIndex = t >= 0 ? t : 2;
                _chkCapDefer.Checked = s.DeferToastsInFullscreen;
                _lblCapFolder.Text = Tr.S("Папка: ", "Folder: ") + s.EffectiveShotFolder;
                _lblCapVideoFolder.Text = Tr.S("Папка видео: ", "Video folder: ") + s.EffectiveVideoFolder;
                _cmbCapCodec.SelectedIndex = Math.Max(0, Array.IndexOf(CapCodecs, s.VideoCodec));
                _cmbCapEncoder.SelectedIndex = Math.Max(0, Array.IndexOf(CapEncoders, s.VideoEncoder));
                _cmbCapVQuality.SelectedIndex = Math.Max(0, Array.IndexOf(CapVideoQualities, s.VideoQuality));
                _cmbCapFps.SelectedIndex = s.VideoFps == 30 ? 0 : 1;
                _cmbCapHeight.SelectedIndex = Math.Max(0, Array.IndexOf(CapHeights, s.VideoHeight));
                _cmbCapCountdown.SelectedIndex = Math.Max(0, Array.IndexOf(CapCountdowns, s.CountdownSeconds));
                int limit = Array.IndexOf(CapLimits, s.MaxMinutes);
                if (limit < 0 && s.MaxMinutes > 0)
                {
                    // Значение из файла не из списка — показывается как есть, без округления.
                    string custom = s.MaxMinutes + " " + Tr.S("мин", "min");
                    if (_cmbCapLimit.Items.Count > CapLimits.Length) _cmbCapLimit.Items[CapLimits.Length] = custom;
                    else _cmbCapLimit.Items.Add(custom);
                    limit = CapLimits.Length;
                }
                _cmbCapLimit.SelectedIndex = Math.Max(0, limit);
                _chkCapVideoCursor.Checked = s.CursorInVideo;
                _chkCapRecPanel.Checked = s.RecordPanel;
                _lblCapVideoNote.Text = s.VideoCodec == "hevc"
                    ? Tr.S("HEVC пишется в обычный MP4: если запись оборвётся (сбой, выключение), файл не восстановить. H.264 и AV1 такой файл переживают.",
                           "HEVC is written as a plain MP4: an interrupted recording (crash, power loss) cannot be recovered. H.264 and AV1 survive it.")
                    : "";
                _lblCapVideoNote.Visible = _lblCapVideoNote.Text.Length > 0;
                _chkCapSysAudio.Checked = s.SystemAudio;
                _chkCapWindowAudio.Checked = s.WindowAudioOnly;
                _chkCapWindowAudio.Enabled = s.SystemAudio && AudioCapture.ProcessLoopbackSupported;
                _chkCapMic.Checked = s.Microphone;
                CapFillMics(s.MicDeviceId);
                _cmbCapMicVolume.SelectedIndex = Math.Max(0, CapNearest(CapMicVolumes, s.MicVolume));
                _chkCapMicMono.Checked = s.MicMono;
                _chkCapTracks.Checked = s.SeparateTracks;
                _chkCapTracks.Enabled = s.SystemAudio && s.Microphone;
                _cmbCapMicDevice.Enabled = _cmbCapMicVolume.Enabled = _chkCapMicMono.Enabled = s.Microphone;
                _lblCapMicNote.Text = s.Microphone && AudioDevices.MicrophonePrivacyDenied()
                    ? Tr.S("Доступ к микрофону выключен в «Параметры → Конфиденциальность → Микрофон» — в видео будет тишина.",
                           "Microphone access is off in Settings → Privacy → Microphone — the video will have silence.")
                    : "";
                _lblCapMicNote.Visible = _lblCapMicNote.Text.Length > 0;
            }
            finally { _capLoading = false; }
            CapUpdatePreview();
            CapRefreshStatus();
        }

        // Файл перечитывается перед записью: агент сам сохраняет последнюю область выделения.
        private void CapChange(Action<CapSettings> mutate)
        {
            if (_capLoading || _closing) return;
            try
            {
                CapSettings s = CapSettings.Load();
                mutate(s);
                if (!s.Save()) { CapInfo(Tr.S("Не удалось сохранить настройки — подробности в crash.log папки данных.", "Could not save the settings — see crash.log in the data folder.")); return; }
                CapIpc.Signal("Reload");
                CapInfo(Tr.S("Сохранено.", "Saved."));
            }
            catch (Exception ex) { CapInfo(Tr.S("Не удалось сохранить: ", "Could not save: ") + ex.Message); }
            CapLoadToUi();
        }

        private void CapControlsChanged(Control source)
        {
            if (_capLoading || _closing) return;
            if (source == _chkCapAutostart)
            {
                bool on = _chkCapAutostart.Checked;
                if (!CapLauncher.SetAutostart(on)) CapInfo(Tr.S("Не удалось изменить автозапуск.", "Could not change startup."));
                else CapInfo(on ? Tr.S("Будет запускаться при входе в Windows.", "Will start at sign-in.") : Tr.S("Убрано из автозапуска.", "Removed from startup."));
                return;
            }
            if (source == _chkCapPrtScn)
            {
                CapTogglePrintScreen(_chkCapPrtScn.Checked);
                return;
            }
            CapChange(delegate(CapSettings s)
            {
                s.PerAppFolders = _chkCapPerApp.Checked;
                s.GalleryOnStart = _chkCapGallery.Checked;
                s.ImageFormat = _cmbCapFormat.SelectedIndex == 1 ? "jpg" : "png";
                if (_cmbCapQuality.SelectedIndex >= 0) s.JpegQuality = CapQualities[_cmbCapQuality.SelectedIndex];
                s.After = _cmbCapAfter.SelectedIndex == 1 ? ShotAfter.Save : _cmbCapAfter.SelectedIndex == 2 ? ShotAfter.CopyOnly
                        : _cmbCapAfter.SelectedIndex == 3 ? ShotAfter.OpenEditor : ShotAfter.SaveAndCopy;
                if (_cmbCapScreen.SelectedIndex >= 0) s.ScreenKey = (ScreenTarget)_cmbCapScreen.SelectedIndex;
                int[] delays = { 0, 3, 5, 10 };
                if (_cmbCapDelay.SelectedIndex >= 0) s.DelaySeconds = delays[_cmbCapDelay.SelectedIndex];
                s.CursorInShots = _chkCapCursor.Checked;
                s.ShutterSound = _chkCapSound.Checked;
                s.ToastEnabled = _chkCapToasts.Checked;
                if (_cmbCapToastSec.SelectedIndex >= 0) s.ToastSeconds = CapToastSeconds[_cmbCapToastSec.SelectedIndex];
                s.DeferToastsInFullscreen = _chkCapDefer.Checked;
                if (_cmbCapCodec.SelectedIndex >= 0) s.VideoCodec = CapCodecs[_cmbCapCodec.SelectedIndex];
                if (_cmbCapEncoder.SelectedIndex >= 0) s.VideoEncoder = CapEncoders[_cmbCapEncoder.SelectedIndex];
                if (_cmbCapVQuality.SelectedIndex >= 0) s.VideoQuality = CapVideoQualities[_cmbCapVQuality.SelectedIndex];
                s.VideoFps = _cmbCapFps.SelectedIndex == 0 ? 30 : 60;
                if (_cmbCapHeight.SelectedIndex >= 0) s.VideoHeight = CapHeights[_cmbCapHeight.SelectedIndex];
                if (_cmbCapCountdown.SelectedIndex >= 0) s.CountdownSeconds = CapCountdowns[_cmbCapCountdown.SelectedIndex];
                if (_cmbCapLimit.SelectedIndex >= 0 && _cmbCapLimit.SelectedIndex < CapLimits.Length) s.MaxMinutes = CapLimits[_cmbCapLimit.SelectedIndex];
                s.CursorInVideo = _chkCapVideoCursor.Checked;
                s.RecordPanel = _chkCapRecPanel.Checked;
                s.SystemAudio = _chkCapSysAudio.Checked;
                s.WindowAudioOnly = _chkCapWindowAudio.Checked;
                s.Microphone = _chkCapMic.Checked;
                int mic = _cmbCapMicDevice.SelectedIndex;
                s.MicDeviceId = mic > 0 && mic - 1 < _capMics.Count ? _capMics[mic - 1].Id : "";
                if (_cmbCapMicVolume.SelectedIndex >= 0) s.MicVolume = CapMicVolumes[_cmbCapMicVolume.SelectedIndex];
                s.MicMono = _chkCapMicMono.Checked;
                s.SeparateTracks = _chkCapTracks.Checked;
            });
        }

        private void CapTogglePrintScreen(bool take)
        {
            CapChange(delegate(CapSettings s)
            {
                if (take && !s.PrintScreenOverride)
                {
                    s.PrintScreenPrevious = PrintScreenKey.Take();
                    s.PrintScreenOverride = true;
                }
                else if (!take && s.PrintScreenOverride)
                {
                    PrintScreenKey.Restore(s.PrintScreenPrevious);
                    s.PrintScreenOverride = false;
                    s.PrintScreenPrevious = -1;
                }
            });
        }

        private void CapUpdatePreview()
        {
            if (_lblCapPreview == null) return;
            try
            {
                CapSettings s = CapSettings.Load();
                string template = _txtCapTemplate.Text.Trim().Length == 0 ? NameTemplate.Default : _txtCapTemplate.Text.Trim();
                string ext = _cmbCapFormat.SelectedIndex == 1 ? ".jpg" : ".png";
                string path = NameTemplate.BuildPath(s.EffectiveShotFolder, _chkCapPerApp.Checked, "Chrome", template, DateTime.Now, ext,
                                                     delegate(string p) { return false; });
                _lblCapPreview.Text = Tr.S("Например: ", "Example: ") + path;
            }
            catch (Exception ex) { _lblCapPreview.Text = Tr.S("Шаблон не подходит: ", "The template does not work: ") + ex.Message; }
        }

        // ---------- состояние ----------

        private void CapRefreshStatus()
        {
            if (_lblCapStatus == null || _closing) return;
            CapStatus st = CapStatus.Read();
            bool running = st != null;
            bool busy = _capBusy != 0;
            _lblCapStatus.Text = running
                ? Tr.S("● Работает", "● Running") + (st.Pid > 0 ? " · PID " + st.Pid : "")
                  + (st.Elevated ? Tr.S(" · с правами администратора: перетаскивание снимков в обычные программы не сработает",
                                        " · elevated: dragging shots into regular programs will not work") : "")
                : Tr.S("○ Не запущен — горячие клавиши не работают", "○ Not running — hotkeys do nothing");
            _btnCapStart.Enabled = !running && !busy;
            _btnCapStop.Enabled = running && !busy;
            _btnCapTry.Enabled = running && !busy;

            CapSettings s = CapSettings.Load();
            Color ok = _theme.Accent;
            Color warn = _theme.Dark ? Color.FromArgb(245, 158, 11) : Color.FromArgb(217, 119, 6);
            List<string> bare = new List<string>();
            foreach (CapAction a in CapActions.All)
            {
                HotkeySpec spec = s.Hotkey(a);
                Label l = _capKeyStatus[a];
                string text;
                Color color = _theme.Subtle;
                string twin = CapDuplicateOf(s, a);
                int error;
                if (spec.IsEmpty) text = Tr.S("не назначено", "not set");
                else if (!CapFeatures.Available(a)) text = Tr.S("заработает вместе с записью видео", "will work once video recording ships");
                else if (twin != null) { text = Tr.S("⚠ то же сочетание у «", "⚠ same shortcut as “") + twin + Tr.S("»", "”"); color = warn; }
                else if (!running) text = Tr.S("процесс не запущен", "process not running");
                else if (!st.HotkeyErrors.TryGetValue(a, out error)) text = Tr.S("применяется…", "applying…");
                else if (error == 0) { text = Tr.S("✓ работает", "✓ works"); color = ok; }
                else
                {
                    text = "⚠ " + HotkeyOwners.Describe(error, spec)
                           + (error == HotkeyOwners.ErrorHotkeyAlreadyRegistered ? Tr.S(" · подхватится само, когда освободится", " · will be picked up once free") : "");
                    color = warn;
                }
                if (l.Text != text) l.Text = text;
                l.ForeColor = color;
                if (spec.IsBareKey && CapFeatures.Available(a)) bare.Add(spec.ToString());
            }
            _lblCapKeysNote.Text = bare.Count == 0 ? ""
                : Tr.S("Одиночные клавиши (", "Single keys (") + string.Join(", ", bare.ToArray())
                  + Tr.S(") пока работает захват, до других программ и игр не доходят — так же было в GameCenter.",
                         ") never reach other programs and games while capture runs — the same as in GameCenter.");
            _lblCapKeysNote.Visible = bare.Count > 0;

            bool gcRunning = CapGameCenterRunning();
            _lblCapGameCenter.Text = gcRunning
                ? Tr.S("VK Play GameCenter запущен и держит свои клавиши. Закройте или удалите его — захват займёт их сам.",
                       "VK Play GameCenter is running and holds its keys. Close or uninstall it — capture takes them over by itself.")
                : "";
            _lblCapGameCenter.Visible = gcRunning;
            _btnCapImport.Visible = File.Exists(GameCenterImport.IniPath);

            bool usesPrtScn = false;
            foreach (CapAction a in CapActions.All) if (s.Hotkey(a).Vk == 0x2C) usesPrtScn = true;
            _chkCapPrtScn.Visible = _lblCapPrtScn.Visible = usesPrtScn || s.PrintScreenOverride;
        }

        private static string CapDuplicateOf(CapSettings s, CapAction a)
        {
            HotkeySpec spec = s.Hotkey(a);
            if (spec.IsEmpty) return null;
            foreach (CapAction other in CapActions.All)
            {
                if (other == a) continue;
                HotkeySpec o = s.Hotkey(other);
                if (o.Mods == spec.Mods && o.Vk == spec.Vk) return CapActions.Title(other);
            }
            return null;
        }

        private static bool CapGameCenterRunning()
        {
            Process[] list = null;
            try
            {
                list = Process.GetProcessesByName("GameCenter");
                return list.Length > 0;
            }
            catch { return false; }
            finally { if (list != null) foreach (Process p in list) p.Dispose(); }
        }

        // ---------- действия ----------

        private void CapRun(string busyText, Func<string> work)
        {
            if (Interlocked.CompareExchange(ref _capBusy, 1, 0) != 0) return;
            CapInfo(busyText);
            CapRefreshStatus();
            Thread t = new Thread(delegate()
            {
                string msg;
                try { msg = work(); }
                catch (Exception ex) { msg = ex.Message; }
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _capBusy, 0);
                    CapInfo(msg);
                    CapRefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void CapStart()
        {
            CapChange(delegate(CapSettings s) { s.Enabled = true; });
            CapRun(Tr.S("Запуск…", "Starting…"), delegate
            {
                string why = CapLauncher.StartAgent();
                if (why != null) return Tr.S("Не удалось запустить: ", "Could not start: ") + why;
                return CapIpc.WaitRunning(5000) ? Tr.S("Запущен.", "Started.") : Tr.S("Процесс не ответил за 5 секунд.", "The process did not respond within 5 seconds.");
            });
        }

        private void CapStop()
        {
            CapChange(delegate(CapSettings s) { s.Enabled = false; });
            CapRun(Tr.S("Остановка…", "Stopping…"), delegate
            {
                if (!CapIpc.Signal("Shutdown")) return Tr.S("Не запущен.", "Not running.");
                return CapIpc.WaitStopped(5000) ? Tr.S("Остановлен.", "Stopped.") : Tr.S("Процесс не остановился за 5 секунд: возможно, редактор снимка ждёт ответа о несохранённых правках.",
                                                                             "The process did not stop within 5 seconds: a screenshot editor may be asking about unsaved changes.");
            });
        }

        private void CapShot(string command)
        {
            if (!CapIpc.Signal(command)) CapInfo(Tr.S("Фоновый процесс не запущен.", "The background process is not running."));
        }

        // Подменю трея. Процесс захвата отсюда сам не запускается: он занял бы горячие клавиши без явного «Запустить».
        private MenuItem CapTrayMenu()
        {
            MenuItem root = new MenuItem(Tr.S("Захват", "Capture"));
            root.MenuItems.Add(new MenuItem(Tr.S("Снимок области", "Region screenshot"), delegate { CapTrayShot("ShotRegion"); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Снимок экрана", "Screen screenshot"), delegate { CapTrayShot("ShotScreen"); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Снимок окна", "Window screenshot"), delegate { CapTrayShot("ShotWindow"); }));
            root.MenuItems.Add("-");
            root.MenuItems.Add(new MenuItem(Tr.S("Видео области", "Region video"), delegate { CapTrayShot("RecRegion"); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Видео экрана", "Screen video"), delegate { CapTrayShot("RecScreen"); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Остановить запись", "Stop recording"), delegate { CapShot("RecStop"); }));
            root.MenuItems.Add("-");
            root.MenuItems.Add(new MenuItem(Tr.S("Галерея", "Gallery"), delegate { CapTrayShot("Gallery"); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Папка снимков", "Screenshots folder"), delegate { CapOpenFolder(false); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Папка видео", "Videos folder"), delegate { CapOpenFolder(true); }));
            root.MenuItems.Add(new MenuItem(Tr.S("Настройки захвата…", "Capture settings…"), delegate { ShowWindow(); ShowPage(PageCapture); }));
            return root;
        }

        private void CapTrayShot(string command)
        {
            if (!CapIpc.IsRunning())
            {
                // Остановленный кнопкой процесс меню не поднимает: иначе он молча занял бы горячие клавиши.
                if (!CapSettings.Load().Enabled)
                {
                    ShowWindow();
                    ShowPage(PageCapture);
                    CapInfo(Tr.S("Фоновый процесс остановлен — нажмите «Запустить».", "The background process is stopped — press Start."));
                    return;
                }
                CapRun(Tr.S("Запуск…", "Starting…"), delegate
                {
                    string why = CapLauncher.StartAgent();
                    if (why != null) return Tr.S("Не удалось запустить: ", "Could not start: ") + why;
                    if (!CapIpc.WaitRunning(5000)) return Tr.S("Процесс не ответил за 5 секунд.", "The process did not respond within 5 seconds.");
                    // Канал команд открывается чуть позже мьютекса — несколько попыток.
                    for (int i = 0; i < 20 && !CapIpc.Signal(command); i++) Thread.Sleep(100);
                    return Tr.S("Запущен.", "Started.");
                });
                return;
            }
            // Меню трея гаснет с анимацией: без паузы его след попадает в замороженный кадр.
            System.Windows.Forms.Timer delay = new System.Windows.Forms.Timer();
            delay.Interval = 250;
            delay.Tick += delegate
            {
                delay.Stop();
                delay.Dispose();
                CapShot(command);
            };
            delay.Start();
        }

        // video — папка видео, иначе папка снимков.
        private void CapPickFolder(bool video)
        {
            CapSettings s = CapSettings.Load();
            string current = video ? s.EffectiveVideoFolder : s.EffectiveShotFolder;
            using (FolderBrowserDialog dlg = new FolderBrowserDialog())
            {
                dlg.Description = video ? Tr.S("Куда сохранять видео", "Where to save videos") : Tr.S("Куда сохранять скриншоты", "Where to save screenshots");
                dlg.ShowNewFolderButton = true;
                try { if (Directory.Exists(current)) dlg.SelectedPath = current; } catch { }
                if (dlg.ShowDialog(this) != DialogResult.OK || string.IsNullOrEmpty(dlg.SelectedPath)) return;
                string picked = dlg.SelectedPath;
                CapChange(delegate(CapSettings x) { if (video) x.VideoFolder = picked; else x.ShotFolder = picked; });
            }
        }

        private void CapOpenFolder(bool video)
        {
            try
            {
                CapSettings s = CapSettings.Load();
                string dir = video ? s.EffectiveVideoFolder : s.EffectiveShotFolder;
                Directory.CreateDirectory(dir);
                Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch (Exception ex) { CapInfo(Tr.S("Не удалось открыть папку: ", "Could not open the folder: ") + ex.Message); }
        }

        private static int CapNearest(int[] values, int v)
        {
            int best = 0;
            for (int i = 1; i < values.Length; i++)
                if (Math.Abs(values[i] - v) < Math.Abs(values[best] - v)) best = i;
            return best;
        }

        // Список микрофонов перечитывается при каждой загрузке страницы: гарнитуру могли подключить или отключить.
        private void CapFillMics(string selectedId)
        {
            _capMics = AudioDevices.Microphones();
            _cmbCapMicDevice.Items.Clear();
            _cmbCapMicDevice.Items.Add(Tr.S("по умолчанию в Windows", "Windows default"));
            foreach (AudioDeviceInfo d in _capMics) _cmbCapMicDevice.Items.Add(string.IsNullOrEmpty(d.Name) ? d.Id : d.Name);
            int index = 0;
            if (!string.IsNullOrEmpty(selectedId))
            {
                index = -1;
                for (int i = 0; i < _capMics.Count; i++)
                    if (string.Equals(_capMics[i].Id, selectedId, StringComparison.OrdinalIgnoreCase)) index = i + 1;
                if (index < 0)
                {
                    // Выбранного устройства сейчас нет (гарнитура отключена): выбор не теряется, запись возьмёт микрофон по умолчанию.
                    _cmbCapMicDevice.Items.Add(Tr.S("не подключено: ", "not connected: ") + selectedId);
                    _capMics.Add(new AudioDeviceInfo { Id = selectedId, Name = "" });
                    index = _cmbCapMicDevice.Items.Count - 1;
                }
            }
            _cmbCapMicDevice.SelectedIndex = index;
        }

        private void CapToggleMicTest()
        {
            if (_capMeter != null) { CapStopMicTest(null); return; }
            CapSettings s = CapSettings.Load();
            string id = string.IsNullOrEmpty(s.MicDeviceId) ? null : s.MicDeviceId;
            try { _capMeter = MicLevelMeter.Start(id); }
            catch (AudioException ex) { CapStopMicTest(RecordSession.AudioReasonText(ex.Reason)); return; }
            catch (Exception ex) { CapStopMicTest(ex.Message); return; }
            _btnCapMicTest.Text = Tr.S("Остановить проверку", "Stop the test");
            _capMeterEnds = DateTime.UtcNow.AddSeconds(15);
            if (_capMeterTick == null)
            {
                _capMeterTick = new System.Windows.Forms.Timer();
                _capMeterTick.Interval = 80;
                _capMeterTick.Tick += delegate
                {
                    if (_capMeter == null || DateTime.UtcNow > _capMeterEnds) { CapStopMicTest(null); return; }
                    float peak = _capMeter.Peak;
                    double db = peak > 0.00001f ? 20 * Math.Log10(peak) : -100;
                    int bars = (int)Math.Round(Math.Max(0, Math.Min(20, (db + 60) / 3)));
                    _lblCapMicLevel.Text = new string('|', bars) + new string('.', 20 - bars) + "  " +
                                           (db <= -99 ? "−∞" : db.ToString("0")) + " dBFS";
                };
            }
            _capMeterTick.Start();
        }

        // Индикатор приватности микрофона гаснет вместе с проверкой: поток не держится, пока страница закрыта.
        private void CapStopMicTest(string message)
        {
            if (_capMeterTick != null) _capMeterTick.Stop();
            if (_capMeter != null) { try { _capMeter.Dispose(); } catch (Exception ex) { CapLog.Report(ex); } _capMeter = null; }
            if (_btnCapMicTest != null) _btnCapMicTest.Text = Tr.S("Проверить микрофон", "Test the microphone");
            if (_lblCapMicLevel != null) _lblCapMicLevel.Text = message ?? "";
        }

        private void CapCheckEncoders()
        {
            CapRun(Tr.S("Проверяю кодировщики видео…", "Checking video encoders…"), delegate
            {
                VideoEncoders.CacheFile = CapPaths.EncoderCacheFile;
                List<string> ok = new List<string>(), bad = new List<string>();
                foreach (EncoderInfo e in VideoEncoders.Enumerate())
                {
                    string name = e.Codec.ToUpperInvariant() + " · " + (e.Hardware ? e.Vendor.ToUpperInvariant() : Tr.S("программный", "software"));
                    if (e.Verified) { if (!ok.Contains(name)) ok.Add(name); }
                    else if (!bad.Contains(name)) bad.Add(name);
                }
                foreach (string n in ok) bad.Remove(n);
                return Tr.S("Работают: ", "Working: ") + (ok.Count > 0 ? string.Join(", ", ok.ToArray()) : Tr.S("нет", "none"))
                       + (bad.Count > 0 ? Tr.S(". Не прошли проверку: ", ". Failed the check: ") + string.Join(", ", bad.ToArray()) : "");
            });
        }

        private void CapImportGameCenter()
        {
            GameCenterImport gc = GameCenterImport.Read(GameCenterImport.IniPath);
            if (gc == null) { CapInfo(Tr.S("В настройках GameCenter нечего переносить.", "Nothing to import from GameCenter settings.")); return; }
            CapChange(delegate(CapSettings s) { gc.ApplyTo(s); });
            List<string> keys = new List<string>();
            foreach (KeyValuePair<CapAction, HotkeySpec> kv in gc.Hotkeys) keys.Add(kv.Value.ToString());
            CapSettings now = CapSettings.Load();
            string msg = Tr.S("Перенесено из GameCenter: ", "Imported from GameCenter: ")
                         + (keys.Count > 0 ? string.Join(", ", keys.ToArray()) : Tr.S("сочетаний нет", "no shortcuts"))
                         + Tr.S("; папка — ", "; folder — ") + now.EffectiveShotFolder;
            if (gc.SkippedKeys > 0) msg += Tr.S(". Сочетания с модификаторами не перенесены: ", ". Shortcuts with modifiers were skipped: ") + gc.SkippedKeys;
            CapInfo(msg);
        }

        private void CapResetHotkeys()
        {
            CapChange(delegate(CapSettings s) { foreach (CapAction a in CapActions.All) s.Hotkeys[a] = CapActions.DefaultHotkey(a); });
        }
    }

    // Поле, в котором сочетание задаётся нажатием. Пока поле в фокусе, агент снимает свои клавиши (иначе F3 в поле
    // открыло бы выделение области), а Alt+F4 и прочие системные сочетания до окна не доходят.
    internal sealed class HotkeyBox : TextBox
    {
        public event Action<HotkeySpec> HotkeyPicked;
        public event EventHandler CaptureStarted, CaptureEnded;

        private HotkeySpec _spec;
        private bool _capturing;

        public HotkeyBox()
        {
            ReadOnly = true;
            ShortcutsEnabled = false;
            TabStop = false;        // фокус при открытии страницы снял бы клавиши агента
            Cursor = Cursors.Hand;
            TextAlign = HorizontalAlignment.Center;
        }

        public HotkeySpec Spec
        {
            get { return _spec; }
            set { _spec = value; if (!_capturing) ShowSpec(); }
        }

        private void ShowSpec() { Text = _spec.IsEmpty ? "—" : _spec.ToString(); }

        protected override void OnEnter(EventArgs e)
        {
            base.OnEnter(e);
            _capturing = true;
            Text = Tr.S("нажмите сочетание…", "press a shortcut…");
            EventHandler h = CaptureStarted;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override void OnLeave(EventArgs e)
        {
            base.OnLeave(e);
            EndCapture();
        }

        private void EndCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            ShowSpec();
            EventHandler h = CaptureEnded;
            if (h != null) h(this, EventArgs.Empty);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!_capturing) return base.ProcessCmdKey(ref msg, keyData);
            Keys key = keyData & Keys.KeyCode;
            Keys mods = keyData & Keys.Modifiers;
            if (key == Keys.Tab && (mods & (Keys.Control | Keys.Alt)) == 0) return base.ProcessCmdKey(ref msg, keyData);
            if (mods == Keys.None && key == Keys.Escape) { Finish(); return true; }
            if (mods == Keys.None && (key == Keys.Back || key == Keys.Delete)) { Pick(new HotkeySpec()); return true; }
            bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
            HotkeySpec spec = HotkeySpec.FromKeys(keyData, win);
            if (!spec.IsEmpty && HotkeySpec.IsKnownKey(spec.Vk)) Pick(spec);
            return true;    // модификатор без клавиши и системные сочетания окну не отдаются
        }

        // PrtScn приходит только отпусканием: нажатие забирает система.
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (_capturing && e.KeyCode == Keys.PrintScreen)
            {
                bool win = (GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0;
                Pick(HotkeySpec.FromKeys(e.KeyData, win));
            }
        }

        private void Pick(HotkeySpec spec)
        {
            _spec = spec;
            Action<HotkeySpec> h = HotkeyPicked;
            if (h != null) h(spec);
            Finish();
        }

        // Фокус уходит с поля — захват окончен, агент возвращает клавиши.
        private void Finish()
        {
            EndCapture();
            Form f = FindForm();
            if (f != null) f.ActiveControl = null;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern short GetKeyState(int vk);
    }
}
