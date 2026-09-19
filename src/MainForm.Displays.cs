// SysDeck — страница «Экраны»: что Windows считает подключённым и выключатель у каждого экрана.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Галочка в строке — и есть состояние экрана: снял её — Windows перестаёт держать для него рабочий стол,
// поставил — возвращает. Кабель при этом не трогается, поэтому выключенный телевизор на HDMI перестаёт
// занимать видеокарту, а включить его обратно можно этой же галочкой (см. Engine.Display.cs — там же оба запрета).
// Чтение топологии и её смена идут в фоновом потоке: DisplayConfigGetDeviceInfo опрашивает каждый путь, а
// SetDisplayConfig перестраивает рабочий стол целиком — на UI-потоке это выглядело бы как зависание окна.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        private FastListView _lvDsp;
        private Label _lblDspInfo;
        private Button _btnDspRefresh;
        private int _dspBusy;
        private bool _dspFilling;

        // ---------- Вкладка: Экраны ----------
        private Control BuildDisplaysTab()
        {
            Panel tab = new Panel();
            tab.Padding = new Padding(14, 12, 14, 12);

            FlowLayoutPanel top = MkToolbar();
            _btnDspRefresh = MkFlowButton(Tr.S("Обновить", "Refresh"), 130, true);
            _btnDspRefresh.Click += delegate { DisplaysRefresh(); };
            top.Controls.Add(_btnDspRefresh);
            top.Controls.Add(MkFlowLabel(Tr.S("галочка в строке включает и выключает экран", "the checkbox in a row turns the display on and off"), true));

            // Подпись одной строкой: почему выключенный телевизор всё равно занимает видеокарту — в docs/displays.md.
            Label warn = MkNote(Tr.S("Выключение программное: путь до экрана перестаёт быть активным, кабель остаётся в разъёме. Последний включённый и основной экран выключить нельзя.",
                                     "Turning a display off is done in software: its path stops being active, the cable stays in the socket. The last display that is on, and the primary one, cannot be turned off."), true);
            _lblDspInfo = MkNote(Tr.S("Чтение списка экранов…", "Reading the list of displays…"), false);

            _lvDsp = new FastListView();
            _lvDsp.Dock = DockStyle.Fill;
            _lvDsp.View = View.Details;
            _lvDsp.CheckBoxes = true;
            _lvDsp.FullRowSelect = true;
            _lvDsp.HideSelection = false;
            _lvDsp.MultiSelect = false;
            _lvDsp.Columns.Add(Tr.S("Экран", "Display"), 240);
            _lvDsp.Columns.Add(Tr.S("Разрешение", "Resolution"), 190);
            _lvDsp.Columns.Add(Tr.S("Разъём", "Connector"), 130);
            _lvDsp.Columns.Add(Tr.S("Видеокарта", "Graphics card"), 260);
            _lvDsp.Columns.Add(Tr.S("Примечание", "Note"), 200);
            SetupOwnerDraw(_lvDsp);
            _flexColumn[_lvDsp] = 3;
            _lvDsp.ItemChecked += delegate(object s, ItemCheckedEventArgs e)
            {
                if (_dspFilling) return;
                DisplaysToggle(e.Item.Name, e.Item.Checked);
            };

            tab.Controls.Add(_lvDsp);
            tab.Controls.Add(_lblDspInfo);
            tab.Controls.Add(warn);
            tab.Controls.Add(top);
            return tab;
        }

        private void DisplaysEnter()
        {
            // Список перечитывается при каждом заходе: экран могли включить кнопкой на самом мониторе.
            if (_dspBusy == 0) DisplaysRefresh();
        }

        private void DisplaysRefresh() { DisplaysRefresh(null); }

        // note != null — результат только что выполненного переключения: он должен пережить перечитывание списка.
        private void DisplaysRefresh(string note)
        {
            if (Interlocked.CompareExchange(ref _dspBusy, 1, 0) != 0) return;
            _btnDspRefresh.Enabled = false;
            Thread t = new Thread(delegate()
            {
                List<DisplayInfo> got;
                string err = null;
                try { got = Displays.List(); }
                catch (Exception ex) { got = new List<DisplayInfo>(); err = ex.Message; }
                UiPost(delegate
                {
                    Interlocked.Exchange(ref _dspBusy, 0);
                    _btnDspRefresh.Enabled = true;
                    DisplaysFill(got);
                    _lblDspInfo.Text = err != null ? Tr.S("Прочитать список экранов не удалось: ", "Failed to read the list of displays: ") + err
                                     : note != null ? note + Tr.S(" · ", " · ") + DisplaysSummary(got)
                                     : DisplaysSummary(got);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private static string DisplaysSummary(List<DisplayInfo> list)
        {
            int on = 0;
            Dictionary<string, bool> adapters = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (DisplayInfo d in list)
                if (d.Active) { on++; adapters[d.AdapterKey] = true; }
            string text = Tr.S("Включено экранов: ", "Displays on: ") + on.ToString(CultureInfo.InvariantCulture)
                        + Tr.S(" из ", " of ") + list.Count.ToString(CultureInfo.InvariantCulture);
            // Две карты на рабочий стол — причина рывков курсора между экранами; тогда уместен пункт «Отключение MPO».
            if (adapters.Count > 1)
                text += Tr.S(". Рабочий стол собирают ", ". The desktop is composited by ") + adapters.Count.ToString(CultureInfo.InvariantCulture)
                      + Tr.S(" видеокарты — при рывках курсора между экранами смотрите «Отключение MPO» на странице «Скрипты».",
                             " graphics cards — if the cursor stutters between screens, see “Disable MPO” on the “Scripts” page.");
            return text;
        }

        private void DisplaysFill(List<DisplayInfo> list)
        {
            _dspFilling = true;
            _lvDsp.BeginUpdate();
            try
            {
                Dictionary<string, bool> seen = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (DisplayInfo d in list)
                {
                    seen[d.Key] = true;
                    ListViewItem row = _lvDsp.Items[d.Key];
                    if (row == null)
                    {
                        row = new ListViewItem(d.Name);
                        row.Name = d.Key;
                        row.UseItemStyleForSubItems = false;
                        for (int i = 0; i < 4; i++) row.SubItems.Add("");
                        _lvDsp.Items.Add(row);
                    }
                    row.Text = d.Name;
                    row.SubItems[1].Text = d.Mode;
                    row.SubItems[2].Text = d.Connector;
                    row.SubItems[3].Text = string.IsNullOrEmpty(d.Gpu) ? d.AdapterKey : d.Gpu;
                    row.SubItems[4].Text = DisplaysNote(d);
                    row.SubItems[4].ForeColor = d.Primary ? _theme.Text : _theme.Subtle;
                    row.ToolTipText = d.Key;
                    if (row.Checked != d.Active) row.Checked = d.Active;
                }
                for (int i = _lvDsp.Items.Count - 1; i >= 0; i--)
                    if (!seen.ContainsKey(_lvDsp.Items[i].Name)) _lvDsp.Items.RemoveAt(i);
            }
            finally
            {
                _lvDsp.EndUpdate();
                _dspFilling = false;
            }
        }

        private static string DisplaysNote(DisplayInfo d)
        {
            if (!d.Active) return Tr.S("выключен в Windows", "off in Windows");
            string gdi = string.IsNullOrEmpty(d.GdiName) ? "" : " · " + d.GdiName;
            return (d.Primary ? Tr.S("основной", "primary") : Tr.S("включён", "on")) + gdi;
        }

        private void DisplaysToggle(string key, bool on)
        {
            if (Interlocked.CompareExchange(ref _dspBusy, 1, 0) != 0)
            {
                // Галочка уже переставлена мышью; список перечитается и вернёт её на место.
                DisplaysRefresh();
                return;
            }
            _btnDspRefresh.Enabled = false;
            _lvDsp.Enabled = false;
            _lblDspInfo.Text = on ? Tr.S("Включение экрана…", "Turning the display on…") : Tr.S("Выключение экрана…", "Turning the display off…");
            string writeOp = Tr.S("Экраны", "Displays");
            BeginWrite(writeOp);
            Thread t = new Thread(delegate()
            {
                DisplayResult r;
                try { r = Displays.SetActive(key, on); }
                catch (Exception ex) { r = DisplayResult.Fail(ex.Message); }
                DisplayResult res = r;
                UiPost(delegate
                {
                    EndWrite(writeOp);
                    Interlocked.Exchange(ref _dspBusy, 0);
                    _lvDsp.Enabled = true;
                    // Перечитывание — единственный источник правды о галочках: отказ вернёт их как было.
                    DisplaysRefresh(res.Message);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
