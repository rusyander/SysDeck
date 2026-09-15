// SysDeck — шапка окна: мощность процессора (максимальное состояние процессора схемы питания).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Видна над любой вкладкой. Ни одна кнопка не выбрана «по умолчанию»: подсвечена та, чьё значение сейчас стоит
// в Windows (99 % — «Пониженная», 100 % — «Максимальная»), при любом другом значении не подсвечена ни одна.
// Значение перечитывается при каждой активации окна — его могли сменить в панели управления или сменой схемы.
// Второй ряд — профили разгона MSI Afterburner (src/Afterburner.cs), если он установлен и хоть один слот заполнен:
// нажатие применяет слот и оставляет его «при запуске». Подсвечен слот, записанный сейчас в [Startup].
using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using SysDeck.Capture;

namespace SysDeck
{
    public partial class MainForm
    {
        private FlowLayoutPanel _cpuPowerBar;
        private Button _btnCpuReduced, _btnCpuMax;
        private Label _lblCpuPower;
        private Label _lblAbTitle, _lblAb;
        private readonly Button[] _btnAbSlots = new Button[Afterburner.MaxSlot + 1];
        private ToolTip _abTips;
        private int _abBusySlot;          // слот, который переключается сейчас; 0 — ничего
        private string _abLastMessage;    // итог последнего переключения, пока его не сменит следующее

        private Control BuildCpuPowerBar()
        {
            FlowLayoutPanel bar = MkToolbar();
            bar.Padding = new Padding(14, 8, 14, 0);
            bar.Controls.Add(MkFlowLabel(Tr.S("Мощность процессора:", "CPU power:"), false));
            _btnCpuReduced = MkFlowButton(Tr.S("Пониженная · 99 %", "Reduced · 99 %"), 170, false);
            _btnCpuReduced.Click += delegate { CpuPowerSet(CpuPowerMode.Reduced); };
            _btnCpuMax = MkFlowButton(Tr.S("Максимальная · 100 %", "Maximum · 100 %"), 190, false);
            _btnCpuMax.Click += delegate { CpuPowerSet(CpuPowerMode.Max); };
            _lblCpuPower = MkFlowLabel("", true);
            bar.Controls.AddRange(new Control[] { _btnCpuReduced, _btnCpuMax, _lblCpuPower });
            bar.SetFlowBreak(_lblCpuPower, true);

            _abTips = new ToolTip();
            _abTips.AutoPopDelay = 20000;
            _abTips.InitialDelay = 300;
            _lblAbTitle = MkFlowLabel(Tr.S("Видеокарта (Afterburner):", "Graphics card (Afterburner):"), false);
            bar.Controls.Add(_lblAbTitle);
            for (int i = 1; i <= Afterburner.MaxSlot; i++)
            {
                int slot = i;
                Button b = MkFlowButton(Tr.S("Профиль ", "Profile ") + slot.ToString(CultureInfo.InvariantCulture), 100, false);
                b.Click += delegate { AbApply(slot); };
                _btnAbSlots[slot] = b;
                bar.Controls.Add(b);
            }
            _lblAb = MkFlowLabel("", true);
            bar.Controls.Add(_lblAb);
            bar.Paint += delegate(object s, PaintEventArgs pe)
            {
                using (Pen pen = new Pen(_theme.Border))
                    pe.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
            };
            _cpuPowerBar = bar;
            Activated += delegate { CpuPowerRefresh(null); };
            CpuPowerRefresh(null);
            return bar;
        }

        private void CpuPowerSet(CpuPowerMode mode)
        {
            string err = CpuPower.Write(CpuPower.PercentOf(mode));
            CpuPowerRefresh(err);
        }

        private void CpuPowerRefresh(string writeError)
        {
            if (_cpuPowerBar == null) return;
            CpuPowerState st = CpuPower.Read();
            CpuPowerMode mode = st.Mode;
            string oldReduced = _btnCpuReduced.Tag as string, oldMax = _btnCpuMax.Tag as string;
            _btnCpuReduced.Tag = mode == CpuPowerMode.Reduced ? "primary" : null;
            _btnCpuMax.Tag = mode == CpuPowerMode.Max ? "primary" : null;

            string text;
            if (!st.Ok)
                text = Tr.S("не удалось прочитать схему питания: ", "cannot read the power scheme: ") + st.Error;
            else
            {
                text = Tr.S("максимальное состояние процессора сейчас ", "maximum processor state now ")
                     + st.Ac.ToString(CultureInfo.InvariantCulture) + " %";
                if (st.Dc >= 0 && st.Dc != st.Ac)
                    text = Tr.S("максимальное состояние процессора: от сети ", "maximum processor state: plugged in ")
                         + st.Ac.ToString(CultureInfo.InvariantCulture) + Tr.S(" %, от батареи ", " %, on battery ")
                         + st.Dc.ToString(CultureInfo.InvariantCulture) + " %";
            }
            if (writeError != null) text = Tr.S("не удалось изменить: ", "could not change: ") + writeError + " · " + text;
            _lblCpuPower.Text = text;
            bool restyle = !string.Equals(oldReduced, _btnCpuReduced.Tag as string) || !string.Equals(oldMax, _btnCpuMax.Tag as string);
            if (AbRefresh()) restyle = true;
            if (restyle) ApplyThemeTo(_cpuPowerBar);
        }

        // true — сменилась подсветка кнопок (нужно перекрасить панель).
        private bool AbRefresh()
        {
            AbState st = Afterburner.Load();
            bool visible = st.Exe != null && (st.UsedSlots.Count > 0 || st.Error != null);
            _lblAbTitle.Visible = visible;
            _lblAb.Visible = visible;
            bool changed = false;
            for (int i = 1; i <= Afterburner.MaxSlot; i++)
            {
                Button b = _btnAbSlots[i];
                b.Visible = visible && st.UsedSlots.Contains(i);
                b.Enabled = _abBusySlot == 0;
                string tag = st.StartupSlot == i ? "primary" : null;
                if (!string.Equals(b.Tag as string, tag)) { b.Tag = tag; changed = true; }
                _abTips.SetToolTip(b, st.Describe(i));
            }
            if (!visible) return changed;

            string text;
            if (st.Error != null) text = Tr.S("не удалось прочитать профили: ", "cannot read the profiles: ") + st.Error;
            else if (_abBusySlot != 0)
                text = Tr.S("переключается на профиль ", "switching to profile ") + _abBusySlot.ToString(CultureInfo.InvariantCulture) + "…";
            else if (st.StartupSlot > 0)
                text = Tr.S("при входе в Windows применяется профиль ", "applied at Windows sign-in: profile ")
                     + st.StartupSlot.ToString(CultureInfo.InvariantCulture);
            else if (st.StartupSlot == 0) text = Tr.S("при входе в Windows разгон не применяется", "no overclocking is applied at Windows sign-in");
            else text = Tr.S("при входе применяются настройки, не совпадающие ни с одним профилем", "the sign-in settings match no profile");
            if (_abLastMessage != null && _abBusySlot == 0) text = _abLastMessage + " · " + text;
            _lblAb.Text = text;
            return changed;
        }

        // Процесс с правами: запущенный оверлей с правами (без окна UAC, команда — только номер слота), иначе помощник
        // --elevated-job с одним окном UAC. Старый оверлей без этой команды события не создаёт — тогда тоже помощник.
        private void AbApply(int slot)
        {
            if (_abBusySlot != 0) return;
            _abBusySlot = slot;
            _abLastMessage = null;
            CpuPowerRefresh(null);
            Thread t = new Thread(delegate()
            {
                bool viaHud;
                string error = null;
                try { error = AbApplyViaHud(slot, out viaHud); }
                catch (Exception ex) { error = ex.Message; viaHud = false; }
                if (!viaHud)
                {
                    try
                    {
                        ElevJob job = new ElevJob();
                        job.Kind = "afterburner";
                        job.Number = slot;
                        ElevResult r = Elevation.Run(_engine, job, null, null);
                        error = r.Ok ? null : r.Declined ? DeclinedNote() : (r.Message ?? "afterburner");
                    }
                    catch (Exception ex) { error = ex.Message; }
                }
                UiPost(delegate
                {
                    _abBusySlot = 0;
                    string name = Tr.S("профиль ", "profile ") + slot.ToString(CultureInfo.InvariantCulture);
                    _abLastMessage = error == null ? name + Tr.S(" применён", " applied")
                                                   : name + Tr.S(" не применён: ", " was not applied: ") + error;
                    CpuPowerRefresh(null);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        // Ждёт в файле состояния оверлея итог с новым номером. viaHud=false — оверлея с правами нет или события нет.
        private static string AbApplyViaHud(int slot, out bool viaHud)
        {
            viaHud = false;
            HudStatus before = HudStatus.Read();
            if (before == null || !before.Elevated) return null;
            if (!HudIpc.Signal("Afterburner" + slot.ToString(CultureInfo.InvariantCulture))) return null;
            viaHud = true;
            Stopwatch clock = Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < 25000)
            {
                Thread.Sleep(200);
                HudStatus now = HudStatus.Read();
                if (now == null) return Tr.S("оверлей закрылся", "the overlay exited");
                if (now.Afterburner == before.Afterburner || now.Afterburner.Length == 0) continue;
                string[] parts = now.Afterburner.Split(new[] { '|' }, 3);
                if (parts.Length < 3) continue;
                return parts[2] == "ok" ? null : parts[2];
            }
            return Tr.S("оверлей не ответил за 25 с", "the overlay did not answer in 25 s");
        }
    }
}
