// SysDeck — вкладка «Настройки»: контролы, загрузка и сохранение
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    public partial class MainForm
    {
        // Сохранение уходит в фон: schtasks.exe на UI-потоке держал окно до пяти секунд,
        // а результат его работы никто не читал — «Настройки сохранены» показывалось всегда.
        private Button _btnSettingsSave;
        private Label _lblSettingsStatus;
        private int _settingsBusy;
        private System.Windows.Forms.Timer _settingsTick;
        private DateTime _settingsStarted;
        private string _settingsPhase;
        // Каждое изменение на странице сохраняется само (с задержкой после последнего
        // касания): раньше настройка жила до кнопки «Сохранить», и ушедший на другую
        // вкладку — или закрывший окно — пользователь терял всё, что переставил.
        private System.Windows.Forms.Timer _settingsAutoSave;
        private bool _settingsLoading;      // LoadSettingsToUi выставляет контролы — это не изменения пользователя
        private bool _autostartRerun;       // изменение флага автозапуска пришло, пока schtasks ещё работал
        private bool _autostartOffered;     // предложение включить автозапуск — один раз за сеанс

        private Control BuildSettingsTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(18, 14, 18, 14);

            // Кнопки закреплены снизу, содержимое скроллится: настройки растут,
            // и без этого «Сохранить» уезжает за пределы окна.
            Panel bar = new Panel();
            bar.Dock = DockStyle.Bottom;
            bar.Height = 52;

            Panel body = new Panel();
            body.Dock = DockStyle.Fill;
            body.AutoScroll = true;

            // Порядок важен: docking идёт в обратном порядке добавления,
            // поэтому Fill добавляется первым, а Bottom — последним,
            // иначе bar накроет низ body и он не доскроллится.
            tab.Controls.Add(body);
            tab.Controls.Add(bar);

            // ---- ЛЕВАЯ КОЛОНКА ----
            int lx = 18, cx = 340, y = 8;   // cx: «Простой для глобального режима, мин:» не влезал в 280
            SectionHeader(body, Tr.S("Критерии заброшенности", "Abandonment criteria"), lx, ref y);
            _numCpu = MakeNum(body, Tr.S("Порог CPU, %:", "CPU threshold, %:"), lx, cx, ref y, 0, 100, 2, 0.1M);
            _numIdle = MakeNum(body, Tr.S("Время простоя, мин:", "Idle time, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _numMinLife = MakeNum(body, Tr.S("Мин. время жизни, мин:", "Min lifetime, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _numGlobalIdle = MakeNum(body, Tr.S("Простой для глобального режима, мин:", "Idle for global mode, min:"), lx, cx, ref y, 1, 1440, 0, 1);

            y += 12;
            SectionHeader(body, Tr.S("Автоматизация", "Automation"), lx, ref y);
            _numInterval = MakeNum(body, Tr.S("Автоочистка каждые (часов, 1..24):", "Auto-clean every (hours, 1..24):"), lx, cx, ref y, 1, 24, 0, 1);
            _chkAuto = MakeCheck(body, Tr.S("Включить автоочистку по таймеру", "Enable auto-clean timer"), lx, ref y);
            _chkExcludeInstalled = MakeCheck(body, Tr.S("Глобально: не трогать Program Files", "Global: don't touch Program Files"), lx, ref y);
            _chkAutostart = MakeCheck(body, Tr.S("Запускать вместе с Windows", "Start with Windows"), lx, ref y);
            _chkStartMin = MakeCheck(body, Tr.S("Стартовать свёрнутым в трей", "Start minimized to tray"), lx, ref y);

            y += 12;
            SectionHeader(body, Tr.S("Производительность", "Performance"), lx, ref y);
            _chkMonitor = MakeCheck(body, Tr.S("Фоновый мониторинг CPU процессов", "Background CPU monitoring"), lx, ref y);
            _numMonInterval = MakeNum(body, Tr.S("Период мониторинга, с (5..300):", "Monitor period, s (5..300):"), lx, cx, ref y, 5, 300, 0, 5);
            _chkEmptyWs = MakeCheck(body, Tr.S("Сбрасывать рабочие наборы всех процессов (замедляет систему)",
                                               "Empty working sets of all processes (slows the system down)"), lx, ref y);
            _chkSmartBoost = MakeCheck(body, Tr.S("Умное ускорение: чистить Standby Memory, когда RAM занята сильнее порога",
                                                  "Smart boost: purge Standby Memory when RAM usage exceeds the threshold"), lx, ref y);
            _numSmartBoost = MakeNum(body, Tr.S("Порог умного ускорения, % RAM (50..99):", "Smart boost threshold, % RAM (50..99):"), lx, cx, ref y, 50, 99, 0, 5);

            y += 12;
            SectionHeader(body, Tr.S("Очистка диска", "Disk cleanup"), lx, ref y);
            _numSkipRecent = MakeNum(body, Tr.S("Не удалять файлы свежее, мин:", "Keep files newer than, min:"), lx, cx, ref y, 0, 1440, 0, 1);
            _chkCleanLog = MakeCheck(body, Tr.S("Вести лог очистки", "Write a cleanup log"), lx, ref y);

            y += 12;
            SectionHeader(body, Tr.S("Обновления программ", "Program updates"), lx, ref y);
            _chkUpdUnknown = MakeCheck(body, Tr.S("Показывать с неизвестной текущей версией",
                                                  "Show items with unknown installed version"), lx, ref y);
            _chkUpdChoco = MakeCheck(body, Tr.S("Опрашивать Chocolatey, если установлен",
                                                "Query Chocolatey when installed"), lx, ref y);
            _numUpdBatch = MakeNum(body, Tr.S("Пакетов за одну команду (1..20):", "Packages per command (1..20):"),
                                   lx, cx, ref y, 1, 20, 0, 1);

            y += 12;
            SectionHeader(body, Tr.S("Оформление", "Appearance"), lx, ref y);
            Label lblTheme = new Label();
            lblTheme.Text = Tr.S("Тема оформления:", "Theme:"); lblTheme.Left = lx; lblTheme.Top = y + 4; lblTheme.AutoSize = true;
            body.Controls.Add(lblTheme);
            _setLabels.Add(lblTheme);
            _cmbTheme = new RoundComboBox();
            _cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTheme.Left = cx; _cmbTheme.Top = y; _cmbTheme.Width = 200;
            _setFields.Add(_cmbTheme);
            _cmbTheme.Items.AddRange(new object[] { Tr.S("По системе", "System"), Tr.S("Светлая", "Light"), Tr.S("Тёмная", "Dark") });
            _cmbTheme.SelectedIndexChanged += delegate { PreviewTheme(); };
            body.Controls.Add(_cmbTheme);
            y += 36;

            Label lblLang = new Label();
            lblLang.Text = "Язык / Language:"; lblLang.Left = lx; lblLang.Top = y + 4; lblLang.AutoSize = true;
            body.Controls.Add(lblLang);
            _setLabels.Add(lblLang);
            _cmbLang = new RoundComboBox();
            _cmbLang.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbLang.Left = cx; _cmbLang.Top = y; _cmbLang.Width = 200;
            _setFields.Add(_cmbLang);
            _cmbLang.Items.AddRange(new object[] { "Русский", "English" });
            body.Controls.Add(_cmbLang);
            y += 44;

            // ---- ПРАВАЯ КОЛОНКА ----
            // rx/rw — стартовые значения; настоящие считает LayoutSettings по тексту.
            int rx = 540, ry = 8, rw = 400;
            _setRight.Add(SectionHeader(body, Tr.S("Списки", "Lists"), rx, ref ry));
            AddLabel(body, Tr.S("Отслеживаемые процессы (по одному в строке):", "Watched processes (one per line):"), rx, ref ry);
            _txtWatch = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Белый список — никогда не завершать:", "Whitelist — never terminate:"), rx, ref ry);
            _txtWhite = MakeMultilineAt(body, rx, ref ry, rw, 150);
            AddLabel(body, Tr.S("Dev-порты (через запятую):", "Dev ports (comma-separated):"), rx, ref ry);
            _txtPorts = new TextBox();
            _txtPorts.AutoSize = false;     // иначе однострочное поле не растягивается в обёртке
            Panel portsBox = MkBox(_txtPorts, new Padding(6, 4, 6, 4));
            portsBox.Left = rx; portsBox.Top = ry; portsBox.Width = rw; portsBox.Height = 30;
            body.Controls.Add(portsBox);
            _setRight.Add(portsBox);
            ry += 38;
            AddLabel(body, Tr.S("Не чистить эти пути (по одному в строке):", "Never clean these paths (one per line):"), rx, ref ry);
            _txtCleanExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);
            AddLabel(body, Tr.S("Не предлагать обновления (Id пакета в строке):",
                                "Never offer updates for (package Id per line):"), rx, ref ry);
            _txtUpdExclude = MakeMultilineAt(body, rx, ref ry, rw, 90);

            // Распорка: AutoScroll считает границу по нижнему краю контролов.
            Panel spacer = new Panel();
            spacer.Left = lx; spacer.Top = Math.Max(y, ry); spacer.Width = 8; spacer.Height = 8;
            body.Controls.Add(spacer);

            // Базовая геометрия правой колонки для растяжения списков по высоте (LayoutSettings).
            foreach (Control r in _setRight) _setTop[r] = r.Top;
            _setLeftBottom = y; _setRightBottom = ry;
            _setSpacer = spacer; _setBody = body;

            LayoutSettings(body);
            body.Resize += delegate { LayoutSettings(body); };

            // ---- КНОПКИ ----
            _btnSettingsSave = new RoundButton();
            _btnSettingsSave.Text = Tr.S("Сохранить настройки", "Save settings");
            _btnSettingsSave.Tag = "primary";
            _btnSettingsSave.Left = lx; _btnSettingsSave.Top = 8; _btnSettingsSave.Width = 210; _btnSettingsSave.Height = 36;
            _btnSettingsSave.Click += delegate { SaveSettingsFromUi(); };
            bar.Controls.Add(_btnSettingsSave);

            Button openDir = new RoundButton();
            openDir.Text = Tr.S("Папка данных", "Data folder");
            openDir.Left = lx + 222; openDir.Top = 8; openDir.Width = 160; openDir.Height = 36;
            // Путь шёл без кавычек: у учётной записи с пробелом в имени профиля Проводник
            // открывал не ту папку, и никто об этом не сообщал (ошибка гасилась в catch).
            openDir.Click += delegate
            {
                try { Process.Start("explorer.exe", "\"" + _engine.DataDir + "\""); }
                catch (Exception ex) { SetSettingsStatus(Tr.S("Не удалось открыть папку данных: ", "Could not open the data folder: ") + ex.Message); }
            };
            bar.Controls.Add(openDir);

            _lblSettingsStatus = new Label();
            _lblSettingsStatus.Left = lx + 394; _lblSettingsStatus.Top = 16; _lblSettingsStatus.AutoSize = true;
            _lblSettingsStatus.Text = "";
            bar.Controls.Add(_lblSettingsStatus);

            WireSettingsAutoSave();
            return tab;
        }

        // Любое касание контрола на странице — отложенное сохранение. Кнопка «Сохранить»
        // остаётся: она ещё и пересоздаёт задачу планировщика (путь к exe мог измениться).
        private void WireSettingsAutoSave()
        {
            foreach (CheckBox c in _setChecks) c.CheckedChanged += delegate { SettingsChanged(); };
            foreach (Control f in _setFields)
            {
                NumericUpDown n = f as NumericUpDown;
                if (n != null) { n.ValueChanged += delegate { SettingsChanged(); }; continue; }
                ComboBox cb = f as ComboBox;
                if (cb != null) cb.SelectedIndexChanged += delegate { SettingsChanged(); };
            }
            foreach (TextBox t in new TextBox[] { _txtWatch, _txtWhite, _txtPorts, _txtCleanExclude, _txtUpdExclude })
                if (t != null) t.TextChanged += delegate { SettingsChanged(); };
            // умное ускорение и автоочистка по таймеру живут, только пока приложение запущено:
            // включил — предложим и запуск вместе с Windows
            _chkSmartBoost.CheckedChanged += delegate { if (_chkSmartBoost.Checked) OfferAutostartForBackground(); };
            _chkAuto.CheckedChanged += delegate { if (_chkAuto.Checked) OfferAutostartForBackground(); };
        }

        private void SettingsChanged()
        {
            if (_settingsLoading || _closing) return;
            if (_settingsAutoSave == null)
            {
                _settingsAutoSave = new System.Windows.Forms.Timer();
                _settingsAutoSave.Interval = 800;
                _settingsAutoSave.Tick += delegate { _settingsAutoSave.Stop(); ApplySettingsFromUi(true); };
            }
            _settingsAutoSave.Stop();
            _settingsAutoSave.Start();
        }

        // При выходе ждать таймер уже некому — записываем то, что ещё не успело сохраниться.
        private void FlushSettingsAutoSave()
        {
            if (_settingsAutoSave == null || !_settingsAutoSave.Enabled) return;
            _settingsAutoSave.Stop();
            try { ApplySettingsFromUi(true); } catch { }
        }

        // Умное ускорение и автоочистка работают только внутри запущенного приложения.
        // Без задачи автозапуска после перезагрузки их никто не выполнит — спрашиваем
        // один раз за сеанс, и только когда пользователь сам включил такую функцию.
        private void OfferAutostartForBackground()
        {
            if (_settingsLoading || _closing || _autostartOffered || _engine.Config.Autostart) return;
            _autostartOffered = true;
            DialogResult dr = MessageBox.Show(this,
                Tr.S("Умное ускорение и автоочистка по таймеру работают, только пока приложение запущено.\r\n\r\n"
                     + "Включить «Запускать вместе с Windows» (свёрнутым в трей), чтобы они работали и после перезагрузки?",
                     "Smart boost and the auto-clean timer only work while the app is running.\r\n\r\n"
                     + "Enable “Start with Windows” (minimized to tray) so they keep working after a reboot?"),
                Tr.S("Работа в фоне", "Background work"), MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (dr != DialogResult.Yes) return;
            _settingsLoading = true;
            try { _chkAutostart.Checked = true; _chkStartMin.Checked = true; }
            finally { _settingsLoading = false; }
            SettingsChanged();
        }

        private void SetSettingsStatus(string text)
        {
            if (_lblSettingsStatus != null) _lblSettingsStatus.Text = text;
        }

        private Label SectionHeader(Panel tab, string text, int lx, ref int y)
        {
            Label l = new Label();
            l.Text = text; l.Left = lx; l.Top = y; l.AutoSize = true;
            l.Font = new Font(Font.FontFamily, 11F, FontStyle.Bold);
            l.Name = "section";
            tab.Controls.Add(l);
            y += 30;
            return l;
        }

        private NumericUpDown MakeNum(Panel tab, string label, int lx, int cx, ref int y,
            decimal min, decimal max, int dec, decimal step)
        {
            Label l = new Label();
            l.Text = label; l.Left = lx; l.Top = y + 4; l.AutoSize = true;
            tab.Controls.Add(l);
            _setLabels.Add(l);
            NumericUpDown n = new NumericUpDown();
            n.Left = cx; n.Top = y; n.Width = 120;
            _setFields.Add(n);
            n.Minimum = min; n.Maximum = max; n.DecimalPlaces = dec; n.Increment = step;
            tab.Controls.Add(n);
            y += 34;
            return n;
        }

        private CheckBox MakeCheck(Panel tab, string label, int lx, ref int y)
        {
            CheckBox c = new CheckBox();
            c.Text = label; c.Left = lx; c.Top = y; c.AutoSize = true;
            tab.Controls.Add(c);
            _setChecks.Add(c);
            y += 30;
            return c;
        }

        private void AddLabel(Panel tab, string text, int lx, ref int y)
        {
            Label l = new Label();
            // AutoSize: фиксированные 500 px вылезали за правый край и включали
            // горизонтальный скролл на странице настроек.
            l.Text = text; l.Left = lx; l.Top = y; l.AutoSize = true;
            tab.Controls.Add(l);
            _setRight.Add(l);
            y += 24;
        }

        private TextBox MakeMultilineAt(Panel tab, int lx, ref int y, int w, int h)
        {
            TextBox t = new TextBox();
            t.Multiline = true; t.ScrollBars = ScrollBars.Vertical;
            Panel box = MkBox(t, new Padding(6, 4, 2, 4));
            box.Left = lx; box.Top = y; box.Width = w; box.Height = h;
            tab.Controls.Add(box);
            _setRight.Add(box);
            _setStretch[box] = h;
            y += h + 12;
            return t;
        }

        // ---------- Настройки <-> UI ----------
        private void LoadSettingsToUi()
        {
            _settingsLoading = true;
            try { LoadSettingsToUiCore(); }
            finally { _settingsLoading = false; }
            // Галочка «умное ускорение» на «Главной» раньше не читала настройки при старте:
            // после перезапуска она стояла снятой, хотя функция была включена.
            SyncSmartHome();
        }

        private void LoadSettingsToUiCore()
        {
            AppConfig c = _engine.Config;
            _numCpu.Value = (decimal)Math.Min(100, Math.Max(0, c.CpuThresholdPercent));
            _numIdle.Value = Math.Min(1440, Math.Max(0, c.IdleMinutes));
            _numMinLife.Value = Math.Min(1440, Math.Max(0, c.MinLifetimeMinutes));
            _numInterval.Value = Math.Min(24, Math.Max(1, c.AutoIntervalHours));
            _numGlobalIdle.Value = Math.Min(1440, Math.Max(1, c.GlobalIdleMinutes));
            _chkAuto.Checked = c.AutoEnabled;
            _chkExcludeInstalled.Checked = c.GlobalExcludeInstalled;
            _chkAutostart.Checked = c.Autostart;
            _chkStartMin.Checked = c.StartMinimized;
            _txtWatch.Text = string.Join("\r\n", c.Watchlist.ToArray());
            _txtWhite.Text = string.Join("\r\n", c.Whitelist.ToArray());
            _txtPorts.Text = string.Join(", ", c.DevPorts.Select(p => p.ToString()).ToArray());
            if (_chkMonitor != null) _chkMonitor.Checked = c.MonitorEnabled;
            if (_numMonInterval != null)
                _numMonInterval.Value = Math.Min(300, Math.Max(5, c.MonitorIntervalSeconds));
            if (_chkEmptyWs != null) _chkEmptyWs.Checked = c.EmptyWorkingSets;
            if (_chkSmartBoost != null) _chkSmartBoost.Checked = c.SmartBoostEnabled;
            if (_numSmartBoost != null) _numSmartBoost.Value = Math.Min(99, Math.Max(50, c.SmartBoostPercent));
            if (_numSkipRecent != null)
                _numSkipRecent.Value = Math.Min(1440, Math.Max(0, c.CleanSkipRecentMinutes));
            if (_chkCleanLog != null) _chkCleanLog.Checked = c.CleanLogEnabled;
            if (_txtCleanExclude != null)
                _txtCleanExclude.Text = string.Join("\r\n", c.CleanExclude.ToArray());
            if (_chkUpdUnknown != null) _chkUpdUnknown.Checked = c.UpdateIncludeUnknown;
            if (_chkUpdChoco != null) _chkUpdChoco.Checked = c.UpdateUseChoco;
            if (_numUpdBatch != null)
                _numUpdBatch.Value = Math.Min(20, Math.Max(1, c.UpdateBatchSize));
            if (_txtUpdExclude != null)
                _txtUpdExclude.Text = string.Join("\r\n", c.UpdateExclude.ToArray());
            if (_cmbTheme != null)
            {
                if (c.Theme == "light") _cmbTheme.SelectedIndex = 1;
                else if (c.Theme == "dark") _cmbTheme.SelectedIndex = 2;
                else _cmbTheme.SelectedIndex = 0;
            }
            if (_chkGlobal != null) _chkGlobal.Checked = c.GlobalScan;
            if (_cmbLang != null) _cmbLang.SelectedIndex = (c.Language == "en") ? 1 : 0;
            if (_miAuto != null) _miAuto.Checked = c.AutoEnabled;
        }

        private string ThemeModeFromCombo()
        {
            if (_cmbTheme == null) return "system";
            if (_cmbTheme.SelectedIndex == 1) return "light";
            if (_cmbTheme.SelectedIndex == 2) return "dark";
            return "system";
        }

        private void PreviewTheme()
        {
            _theme = Theme.Resolve(ThemeModeFromCombo());
            ApplyThemeAll();
        }

        // Второй рубеж после ApplyAutostart: спрашиваем сам планировщик, есть задача или нет.
        // Имя берём у движка — раньше здесь стоял его дубликат строкой.
        private static bool AutostartTaskPresent()
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                                                            "/Query /TN \"" + Engine.AutostartTaskName + "\"");
                psi.CreateNoWindow = true;
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return false;
                    // вывод крошечный, поэтому читаем по очереди: труба не переполнится
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return false; }
                    return p.ExitCode == 0;
                }
            }
            catch { return false; }
        }

        private void StartSettingsTicker(string phase)
        {
            _settingsPhase = phase;
            _settingsStarted = DateTime.UtcNow;
            if (_settingsTick == null)
            {
                _settingsTick = new System.Windows.Forms.Timer();
                _settingsTick.Interval = 500;
                _settingsTick.Tick += delegate { SettingsTick(); };
            }
            _settingsTick.Start();
            SettingsTick();
        }

        private void SettingsTick()
        {
            if (_settingsBusy == 0)
            {
                if (_settingsTick != null) _settingsTick.Stop();
                return;
            }
            SetSettingsStatus(_settingsPhase + "   ·   " + Elapsed(DateTime.UtcNow - _settingsStarted));
        }

        private void SaveSettingsFromUi() { ApplySettingsFromUi(false); }

        // quiet = автосохранение: без окна «Настройки сохранены», задача планировщика
        // трогается только когда флаг автозапуска действительно изменился.
        private void ApplySettingsFromUi(bool quiet)
        {
            if (_settingsAutoSave != null) _settingsAutoSave.Stop();
            AppConfig c = _engine.Config;
            c.CpuThresholdPercent = (double)_numCpu.Value;
            c.IdleMinutes = (int)_numIdle.Value;
            c.MinLifetimeMinutes = (int)_numMinLife.Value;
            c.AutoIntervalHours = (int)_numInterval.Value;
            c.GlobalIdleMinutes = (int)_numGlobalIdle.Value;
            c.AutoEnabled = _chkAuto.Checked;
            c.GlobalExcludeInstalled = _chkExcludeInstalled.Checked;
            c.StartMinimized = _chkStartMin.Checked;
            c.Watchlist = ParseLines(_txtWatch.Text);
            c.Whitelist = ParseLines(_txtWhite.Text);
            c.DevPorts = ParsePorts(_txtPorts.Text);
            c.Theme = ThemeModeFromCombo();
            c.EmptyWorkingSets = _chkEmptyWs.Checked;
            c.SmartBoostEnabled = _chkSmartBoost.Checked;
            c.SmartBoostPercent = (int)_numSmartBoost.Value;
            SyncSmartHome();
            c.CleanSkipRecentMinutes = (int)_numSkipRecent.Value;
            c.CleanLogEnabled = _chkCleanLog.Checked;
            c.CleanExclude = ParseLines(_txtCleanExclude.Text);
            c.UpdateIncludeUnknown = _chkUpdUnknown.Checked;
            c.UpdateUseChoco = _chkUpdChoco.Checked;
            c.UpdateBatchSize = (int)_numUpdBatch.Value;
            c.UpdateExclude = ParseLines(_txtUpdExclude.Text);

            // период/включённость мониторинга применяем сразу, без перезапуска
            bool monWas = c.MonitorEnabled;
            int monPeriodWas = c.MonitorIntervalSeconds;
            c.MonitorEnabled = _chkMonitor.Checked;
            c.MonitorIntervalSeconds = (int)_numMonInterval.Value;
            if (monWas != c.MonitorEnabled || monPeriodWas != c.MonitorIntervalSeconds)
                RestartMonitor();

            string newLang = (_cmbLang != null && _cmbLang.SelectedIndex == 1) ? "en" : "ru";
            bool langChanged = c.Language != newLang;
            c.Language = newLang;

            bool autostartWas = c.Autostart;
            c.Autostart = _chkAutostart.Checked;

            _engine.SaveConfig();
            RescheduleAuto();
            if (_miAuto != null) _miAuto.Checked = c.AutoEnabled;

            if (quiet)
            {
                SetSettingsStatus(Tr.S("Сохранено в ", "Saved at ") + DateTime.Now.ToString("HH:mm:ss")
                    + (langChanged ? Tr.S(" · язык изменится после перезапуска", " · the language changes after a restart") : ""));
                if (autostartWas != c.Autostart) StartAutostartApply(c.Autostart, true, langChanged);
                return;
            }
            StartAutostartApply(c.Autostart, false, langChanged);
        }

        // Дальше — schtasks.exe: до пяти секунд ожидания. На UI-потоке это было мёртвое
        // окно, поэтому задача планировщика пересоздаётся в фоне (путь к exe мог измениться),
        // а её результат проверяется запросом к самому планировщику.
        private void StartAutostartApply(bool wantAutostart, bool quiet, bool langChanged)
        {
            if (Interlocked.CompareExchange(ref _settingsBusy, 1, 0) != 0)
            {
                // предыдущий schtasks ещё работает: повторим с актуальным флагом, когда он закончит
                _autostartRerun = true;
                if (!quiet) SetSettingsStatus(Tr.S("Сохранение уже идёт — подождите.", "Saving is already in progress — please wait."));
                return;
            }
            _btnSettingsSave.Enabled = false;
            StartSettingsTicker(Tr.S("Сохраняю: задача автозапуска в планировщике", "Saving: the autostart task in Task Scheduler"));
            Thread th = new Thread(delegate()
            {
                string err = null;
                bool ok = false;
                try
                {
                    // Движок теперь возвращает причину отказа schtasks; проверка запросом
                    // остаётся вторым рубежом — код возврата 0 ещё не значит, что задача есть.
                    err = ApplyAutostartMaybeElevated(wantAutostart);
                    ok = AutostartTaskPresent() == wantAutostart;
                    if (ok) err = null;
                }
                catch (Exception ex) { err = ex.Message; }
                bool okCopy = ok; string errCopy = err;
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _settingsBusy, 0);
                    if (_settingsTick != null) _settingsTick.Stop();
                    _btnSettingsSave.Enabled = true;
                    if (_autostartRerun && !_closing)
                    {
                        _autostartRerun = false;
                        StartAutostartApply(_engine.Config.Autostart, true, false);
                        return;
                    }
                    if (quiet && okCopy)
                    {
                        SetSettingsStatus(Tr.S("Сохранено в ", "Saved at ") + DateTime.Now.ToString("HH:mm:ss")
                            + (wantAutostart ? Tr.S(" · автозапуск включён", " · autostart enabled") : Tr.S(" · автозапуск выключен", " · autostart disabled")));
                        return;
                    }

                    // Одно окно вместо двух подряд («сохранено», потом «язык после перезапуска»).
                    string saved = Tr.S("Настройки сохранены.", "Settings saved.");
                    if (!okCopy)
                        saved += "\r\n\r\n" + (wantAutostart
                            ? Tr.S("Но задачу автозапуска создать не удалось — запуск вместе с Windows не настроен. Обычная причина: нет прав администратора.",
                                   "But the autostart task could not be created — starting with Windows is not set up. The usual cause: no administrator rights.")
                            : Tr.S("Но задачу автозапуска удалить не удалось — приложение по-прежнему может стартовать вместе с Windows.",
                                   "But the autostart task could not be deleted — the app may still start with Windows."))
                          + (errCopy != null ? " (" + errCopy + ")" : "");
                    if (langChanged)
                        saved += "\r\n\r\n" + Tr.S("Язык изменится после перезапуска приложения.",
                                                   "The language will change after you restart the app.");
                    SetSettingsStatus(okCopy
                        ? Tr.S("Сохранено в ", "Saved at ") + DateTime.Now.ToString("HH:mm:ss")
                        : Tr.S("Сохранено, но автозапуск не настроен — см. сообщение.", "Saved, but autostart is not set up — see the message."));
                    MessageBox.Show(this, saved, Tr.S("Настройки", "Settings"), MessageBoxButtons.OK,
                                    okCopy ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
                });
            });
            th.IsBackground = true;
            th.Start();
        }

        private List<string> ParseLines(string text)
        {
            List<string> list = new List<string>();
            foreach (string line in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string t = line.Trim();
                if (t.Length > 0) list.Add(t);
            }
            return list;
        }

        private List<int> ParsePorts(string text)
        {
            List<int> list = new List<int>();
            foreach (string part in text.Split(new char[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int v;
                if (int.TryParse(part.Trim(), out v) && v > 0 && v < 65536) list.Add(v);
            }
            return list;
        }
    }
}
