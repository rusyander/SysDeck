// SysDeck — вкладка «Оверлей»: загрузка и сохранение, права, сцены, ограничитель NVIDIA, состояние и сведения.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck
{
    public partial class MainForm
    {
        // ---------- настройки ----------

        private void OvLoad()
        {
            OvSaveNow();
            CapSettings s = CapSettings.Load();
            _ovLoading = true;
            _ovSuppress = true;
            try
            {
                _ovScene = Math.Max(0, Math.Min(2, s.HudScene));
                _cmbOvScene.SelectedIndex = _ovScene;
                _ovItems = HudItem.Grouped(HudItem.ParseList(s.ActiveItems));
                foreach (KeyValuePair<string, TreeNode> kv in _ovNodes)
                    if (!kv.Key.StartsWith(OvGroupTag, StringComparison.Ordinal)) kv.Value.Checked = OvFind(kv.Key) != null;
                foreach (TreeNode g in _tvOv.Nodes) OvUpdateGroupCheck(g);
                _cmbOvCorner.SelectedIndex = Math.Max(0, Math.Min(8, (int)s.HudCorner));
                _cmbOvMonitor.Items.Clear();
                int monitors = Math.Max(HudLayout.MonitorCount, s.HudMonitor + 1);
                for (int i = 0; i < monitors; i++)
                    _cmbOvMonitor.Items.Add(i == 0 ? Tr.S("основной", "primary") : Tr.S("монитор ", "monitor ") + (i + 1).ToString(CultureInfo.InvariantCulture));
                _cmbOvMonitor.SelectedIndex = Math.Max(0, s.HudMonitor);
                _cmbOvOpacity.SelectedIndex = Math.Max(0, CapNearest(OvOpacities, s.HudOpacity));
                _cmbOvScale.SelectedIndex = Math.Max(0, CapNearest(OvScales, s.HudScale));
                _cmbOvGraphSec.SelectedIndex = Math.Max(0, CapNearest(OvGraphSeconds, s.HudGraphSeconds));
                _cmbOvGraphWidth.SelectedIndex = Math.Max(0, CapNearest(OvGraphWidths, s.HudGraphWidth));
                _cmbOvStatsSec.SelectedIndex = Math.Max(0, CapNearest(OvStatsSeconds, s.HudStatsSeconds));
                _cmbOvFont.SelectedIndex = Math.Max(0, Array.IndexOf(HudStyle.Fonts, HudStyle.ValidFont(s.HudFont)));
                _cmbOvFontSize.SelectedIndex = Math.Max(0, CapNearest(OvFontSizes, s.HudFontSize));
                _chkOvBold.Checked = s.HudBoldLabels;
                _chkOvGroupColors.Checked = s.HudGroupColors;
                _chkOvShadow.Checked = s.HudShadow;
                _chkOvRowLayout.Checked = s.HudRowLayout;
                _chkOvInCaptures.Checked = s.HudInCaptures;
                _chkOvHwinfo.Checked = s.HudHwinfo;
                _chkOvElevated.Checked = s.HudElevated;
            }
            finally
            {
                _ovSuppress = false;
                _ovLoading = false;
            }
            OvShowEditor();
        }

        // Файл перечитывается перед записью: другие поля CapSettings пишут «Захват» и сам агент.
        private void OvSaveNow()
        {
            if (_ovSaveTimer != null) _ovSaveTimer.Stop();
            if (!_ovDirty) return;
            _ovDirty = false;
            try
            {
                CapSettings s = CapSettings.Load();
                s.SetSceneItems(_ovScene, HudItem.FormatList(_ovItems));
                s.HudScene = _ovScene;
                s.HudStatsSeconds = OvPick(OvStatsSeconds, _cmbOvStatsSec, s.HudStatsSeconds);
                if (_cmbOvFont.SelectedIndex >= 0) s.HudFont = HudStyle.Fonts[_cmbOvFont.SelectedIndex];
                s.HudFontSize = OvPick(OvFontSizes, _cmbOvFontSize, s.HudFontSize);
                s.HudBoldLabels = _chkOvBold.Checked;
                s.HudGroupColors = _chkOvGroupColors.Checked;
                s.HudShadow = _chkOvShadow.Checked;
                s.HudRowLayout = _chkOvRowLayout.Checked;
                if (_cmbOvCorner.SelectedIndex >= 0) s.HudCorner = (HudCorner)_cmbOvCorner.SelectedIndex;
                if (_cmbOvMonitor.SelectedIndex >= 0) s.HudMonitor = _cmbOvMonitor.SelectedIndex;
                s.HudOpacity = OvPick(OvOpacities, _cmbOvOpacity, s.HudOpacity);
                s.HudScale = OvPick(OvScales, _cmbOvScale, s.HudScale);
                s.HudGraphSeconds = OvPick(OvGraphSeconds, _cmbOvGraphSec, s.HudGraphSeconds);
                s.HudGraphWidth = OvPick(OvGraphWidths, _cmbOvGraphWidth, s.HudGraphWidth);
                s.HudInCaptures = _chkOvInCaptures.Checked;
                s.HudHwinfo = _chkOvHwinfo.Checked;
                if (!s.Save()) { OvInfo(Tr.S("Не удалось сохранить настройки — подробности в crash.log папки данных.", "Could not save the settings — see crash.log in the data folder.")); return; }
                HudIpc.Signal("Reload");
                OvInfo(Tr.S("Сохранено.", "Saved."));
            }
            catch (Exception ex) { OvInfo(Tr.S("Не удалось сохранить: ", "Could not save: ") + ex.Message); }
        }

        // Галочка прав: задачу Планировщика создаёт (или удаляет) помощник с правами, затем оверлей перезапускается
        // уже по-новому. Отказ от UAC возвращает галочку назад.
        private void OvSetElevated(bool on)
        {
            _chkOvElevated.Enabled = false;
            Thread t = new Thread(delegate()
            {
                string error = null;
                try
                {
                    if (on || HudLauncher.TaskExists())
                    {
                        ElevJob job = new ElevJob();
                        job.Kind = "hudtask";
                        job.Flag = on;
                        ElevResult r = Elevation.Run(_engine, job, null, null);
                        if (!r.Ok) error = r.Declined ? DeclinedNote() : (r.Message ?? "schtasks");
                    }
                    if (error == null)
                    {
                        CapSettings s = CapSettings.Load();
                        s.HudElevated = on;
                        if (!s.Save()) error = Tr.S("не удалось сохранить настройки", "could not save the settings");
                    }
                    if (error == null) HudLauncher.Restart();
                }
                catch (Exception ex) { error = ex.Message; }
                UiPost(delegate
                {
                    _chkOvElevated.Enabled = true;
                    if (error != null)
                    {
                        _ovLoading = true;
                        try { _chkOvElevated.Checked = !on; }
                        finally { _ovLoading = false; }
                        OvInfo(Tr.S("Права оверлея: ", "Overlay rights: ") + error);
                    }
                    else OvInfo(on ? Tr.S("Оверлей работает с правами администратора.", "The overlay runs with administrator rights.")
                                   : Tr.S("Оверлей работает без прав администратора.", "The overlay runs without administrator rights."));
                    OvRefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private string _ovLastLag;

        // Другой набор строк: текущий сохраняется, выбранный становится и редактируемым, и показанным.
        private void OvSwitchScene(int scene)
        {
            if (scene < 0 || scene > 2 || scene == _ovScene) return;
            _ovDirty = true;
            OvSaveNow();
            CapSettings s = CapSettings.Load();
            s.HudScene = scene;
            if (!s.Save()) { OvInfo(Tr.S("Не удалось сохранить настройки.", "Could not save the settings.")); return; }
            HudIpc.Signal("Reload");
            OvLoad();
            if (_ovItems.Count == 0)
                OvInfo(Tr.S("Набор пуст — отметьте строки слева или выберите «Готовые наборы». Клавиша «следующий набор» переключает только непустые.",
                            "The set is empty — tick rows on the left or pick a preset. The “next set” hotkey skips empty sets."));
        }

        private void OvOpenReports()
        {
            try
            {
                string dir = HudLagRecorder.BaseFolder;
                if (!Directory.Exists(dir)) { OvInfo(Tr.S("Отчётов ещё нет — они появятся в ", "No reports yet — they will appear in ") + dir); return; }
                using (Process p = Process.Start("explorer.exe", "\"" + dir + "\"")) { }
            }
            catch (Exception ex) { OvInfo(ex.Message); }
        }

        // NVAPI проверяется один раз в фоне: строка ограничителя видна только на машине с драйвером NVIDIA.
        private void OvNvCheck()
        {
            _ovNvChecked = true;
            ThreadPool.QueueUserWorkItem(delegate
            {
                bool available = false;
                int fps = -1;
                string error = null;
                try
                {
                    available = NvFrameLimit.Available;
                    if (available) fps = NvFrameLimit.Get(null, out error);
                }
                catch (Exception ex) { error = ex.Message; }
                UiPost(delegate
                {
                    _ovNvRow.Visible = available;
                    if (!available) return;
                    _ovLoading = true;
                    try { _cmbOvNvFps.SelectedIndex = Math.Max(0, CapNearest(OvNvFps, Math.Max(0, fps))); }
                    finally { _ovLoading = false; }
                    if (fps > 0) _lblOvNv.Text = Tr.S("для всех игр сейчас: ", "for all games now: ") + fps.ToString(CultureInfo.InvariantCulture) + " FPS";
                    else if (error != null) _lblOvNv.Text = error;
                });
            });
        }

        private void OvNvPickGame()
        {
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            HudValue app = f == null ? null : f.Get("fps.app");
            string name = app != null && !string.IsNullOrEmpty(app.Text) ? app.Text : null;
            if (name == null) { OvInfo(Tr.S("Игра не видна: запустите её, вернитесь сюда (Alt+Tab) и нажмите ещё раз — берётся последнее окно, выводившее кадры.", "No game seen: start it, come back (Alt+Tab) and press again — the last window that presented frames is taken.")); return; }
            _txtOvNvExe.Text = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name : name + ".exe";
        }

        // Запись в профиль драйвера; если драйвер требует прав — одно окно UAC через помощник.
        private void OvNvApply()
        {
            int fps = OvPick(OvNvFps, _cmbOvNvFps, 0);
            string exeText = _txtOvNvExe.Text.Trim();
            string exe = exeText.Length == 0 ? null : NvFrameLimit.ValidExe(exeText);
            if (exeText.Length > 0 && exe == null) { OvInfo(Tr.S("Имя exe без пути, например game.exe.", "An exe name without a path, e.g. game.exe.")); return; }
            _btnOvNvApply.Enabled = false;
            Thread t = new Thread(delegate()
            {
                string error;
                try
                {
                    error = NvFrameLimit.Set(exe, fps);
                    if (NvFrameLimit.IsPrivilegeError(error))
                    {
                        ElevJob job = new ElevJob();
                        job.Kind = "nvlimit";
                        job.Number = fps;
                        job.Arg = exe ?? "";
                        ElevResult r = Elevation.Run(_engine, job, null, null);
                        error = r.Ok ? null : r.Declined ? DeclinedNote() : (r.Message ?? "nvlimit");
                    }
                }
                catch (Exception ex) { error = ex.Message; }
                UiPost(delegate
                {
                    _btnOvNvApply.Enabled = true;
                    string who = exe ?? Tr.S("всех игр", "all games");
                    OvInfo(error != null ? Tr.S("Предел кадров NVIDIA: ", "NVIDIA frame limit: ") + error
                         : fps == 0 ? Tr.S("Предел кадров снят для ", "Frame limit removed for ") + who + "."
                         : Tr.S("Предел ", "Limit of ") + fps.ToString(CultureInfo.InvariantCulture) + Tr.S(" FPS записан в драйвер для ", " FPS written to the driver for ") + who
                           + Tr.S(". Уже запущенную игру перезапустите.", ". Restart a game that is already running."));
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------- состояние процесса оверлея ----------

        private void OvRefreshStatus()
        {
            if (_lblOvStatus == null) return;
            HudStatus st = HudStatus.Read();
            string text;
            if (st == null) text = Tr.S("Столбик не запущен — «Показать / скрыть» запустит его.", "The column is not running — “Show / hide” starts it.");
            else
            {
                text = (st.Shown ? Tr.S("Столбик показан", "Column shown") : Tr.S("Столбик скрыт", "Column hidden"))
                       + " · " + (st.Elevated ? Tr.S("с правами администратора", "with administrator rights") : Tr.S("без прав администратора", "without administrator rights"))
                       + " · HWiNFO: " + OvHwinfoText(st.Hwinfo);
                if (!string.IsNullOrEmpty(st.Note)) text += " (" + st.Note + ")";
            }
            // Состояние кадров — у показанного столбика его собственное, иначе — сессии предпросмотра этой страницы.
            string fps = st != null && st.Shown && !string.IsNullOrEmpty(st.Fps) ? st.Fps : HudFpsSource.LastState;
            text += " · FPS: " + OvFpsText(fps);
            bool recording = st != null && st.Lag != null && st.Lag.StartsWith("rec|", StringComparison.Ordinal);
            if (recording) text += " · " + Tr.S("идёт запись лагов", "recording lags");
            string lagText = recording ? Tr.S("■ Остановить запись", "■ Stop recording") : Tr.S("● Записать лаги", "● Record lags");
            if (_btnOvLag != null && _btnOvLag.Text != lagText) _btnOvLag.Text = lagText;
            if (st != null && st.Lag != null && st.Lag != _ovLastLag)
            {
                _ovLastLag = st.Lag;
                if (st.Lag.StartsWith("done|", StringComparison.Ordinal)) OvInfo(Tr.S("Отчёт о лагах: ", "Lag report: ") + st.Lag.Substring(5));
                else if (st.Lag.StartsWith("error|", StringComparison.Ordinal)) OvInfo(Tr.S("Запись лагов: ", "Lag recording: ") + st.Lag.Substring(6));
            }
            if (!_ovNvChecked) OvNvCheck();
            if (_lblOvStatus.Text != text) _lblOvStatus.Text = text;
            bool ask = (fps == HudEtwSession.StateNoRights) && !(st != null && st.Elevated);
            if (_btnOvFps != null && _btnOvFps.Visible != ask) _btnOvFps.Visible = ask;
        }

        internal static string OvFpsText(string state)
        {
            switch (state ?? "")
            {
                case HudEtwSession.StateOk: return Tr.S("считаются", "counted");
                case HudEtwSession.StateNoRights: return Tr.S("нет прав (нужна группа «Пользователи журналов производительности» или оверлей с правами)",
                                                              "no rights (needs the “Performance Log Users” group or the overlay with rights)");
                case HudEtwSession.StateRelogon: return Tr.S("права выданы — выйдите из Windows и войдите снова", "rights granted — sign out of Windows and back in");
                case HudEtwSession.State32Bit: return Tr.S("только в 64-битной Windows", "64-bit Windows only");
                case HudEtwSession.StateError: return Tr.S("сессия ETW не запустилась", "the ETW session did not start");
                case HudEtwSession.StateOff: case "": return Tr.S("не считаются", "not counted");
            }
            return state;
        }

        // Одно окно UAC: помощник добавляет вошедшего пользователя в группу. Токен меняется только при новом входе.
        private void OvAllowFps()
        {
            _btnOvFps.Enabled = false;
            Thread t = new Thread(delegate()
            {
                string error = null;
                try
                {
                    ElevJob job = new ElevJob();
                    job.Kind = "perflog";
                    ElevResult r = Elevation.Run(_engine, job, null, null);
                    if (!r.Ok) error = r.Declined ? DeclinedNote() : (r.Message ?? "perflog");
                }
                catch (Exception ex) { error = ex.Message; }
                if (error == null) HudFpsSource.LastState = HudEtwSession.StateRelogon;
                UiPost(delegate
                {
                    _btnOvFps.Enabled = true;
                    OvInfo(error != null ? Tr.S("Подсчёт кадров: ", "Frame counting: ") + error
                                         : Tr.S("Вы добавлены в группу «Пользователи журналов производительности». Кадры начнут считаться после выхода из Windows и нового входа.",
                                                "You were added to “Performance Log Users”. Frames are counted after you sign out of Windows and back in."));
                    OvRefreshStatus();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        internal static string OvHwinfoText(string state)
        {
            switch (state ?? "")
            {
                case "off": return Tr.S("не используется", "not used");
                case "missing": return Tr.S("не установлен", "not installed");
                case "needs-admin": return Tr.S("запустить можно только с правами администратора", "can only be started with administrator rights");
                case "external": return Tr.S("запущен вами, данные идут", "started by you, data flows");
                case "external-no-sm": return Tr.S("запущен вами без общей памяти (Shared Memory Support выключен)", "started by you without shared memory (Shared Memory Support is off)");
                case "starting": return Tr.S("запускается", "starting");
                case "ours": return Tr.S("запущен в фоне, данные идут", "running in the background, data flows");
                case "ours-no-sm": return Tr.S("запущен в фоне, данных пока нет", "running in the background, no data yet");
                case "failed": return Tr.S("не удалось запустить", "failed to start");
                case "": return "—";
            }
            return state;
        }

        // ---------- сведения о системе ----------

        private void OvRefreshInfo()
        {
            _btnOvInfoRefresh.Enabled = false;
            Thread t = new Thread(delegate()
            {
                HudSysInfo info = null;
                string error = null;
                try { info = HudSysInfo.Collect(); }
                catch (Exception ex) { error = ex.Message; CapLog.Report(ex); }
                UiPost(delegate
                {
                    _btnOvInfoRefresh.Enabled = true;
                    if (info == null) { OvInfo(Tr.S("Сведения не собраны: ", "Could not collect the information: ") + error); return; }
                    OvFillInfo(info);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void OvFillInfo(HudSysInfo info)
        {
            List<string[]> rows = new List<string[]>();
            foreach (HudSysInfo.Line l in info.Lines) rows.Add(new[] { l.Section, l.Key, l.Value });
            // Живые строки, которых нет в SMBIOS: тайминги и самая горячая точка — только когда датчики их дают.
            HudFrame f;
            lock (_ovGate) f = _ovFrame;
            string sensors = Tr.S("Датчики сейчас", "Sensors now");
            foreach (string id in new[] { "mem.timings", "hot.max", "cpu.temp", "gpu.temp", "gpu.hotspot", "gpu.memtemp", "cpu.voltage", "gpu.pcie" })
            {
                HudValue v = f == null ? null : f.Get(id);
                if (v == null || !f.Has(id)) continue;
                string value = HudFormat.Row(new HudItem(id), v).Value;
                if (id == "hot.max" && !string.IsNullOrEmpty(v.Note)) value += " — " + v.Note;
                HudDef def = OvDef(id);
                rows.Add(new[] { sensors, def == null ? id : def.Title, value });
            }
            _lvOvInfo.BeginUpdate();
            try
            {
                _lvOvInfo.Items.Clear();
                string last = null;
                foreach (string[] r in rows)
                {
                    ListViewItem it = new ListViewItem(r[0] == last ? "" : r[0]);
                    it.SubItems.Add(r[1]);
                    it.SubItems.Add(r[2]);
                    it.Tag = r;
                    last = r[0];
                    _lvOvInfo.Items.Add(it);
                }
            }
            finally { _lvOvInfo.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvOvInfo);
            OvInfo(f == null ? Tr.S("Датчики ещё не опрошены — «Обновить» через пару секунд добавит живые строки.", "Sensors not polled yet — “Refresh” in a couple of seconds adds live rows.") : "");
        }

        private void OvCopyInfo()
        {
            StringBuilder sb = new StringBuilder();
            foreach (ListViewItem it in _lvOvInfo.Items)
            {
                string[] r = it.Tag as string[];
                if (r != null) sb.Append(r[0]).Append('\t').Append(r[1]).Append('\t').Append(r[2]).Append("\r\n");
            }
            if (sb.Length == 0) return;
            try { Clipboard.SetText(sb.ToString()); OvInfo(Tr.S("Скопировано.", "Copied.")); }
            catch (Exception ex) { OvInfo(Tr.S("Не удалось скопировать: ", "Could not copy: ") + ex.Message); }
        }
    }
}
