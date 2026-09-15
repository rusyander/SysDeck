// SysDeck — вкладка «Память»: полоса, график и списки (процессы, пулы, железо).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        // ------------------------------------------------------------------ //
        //  Шапка: полоса и главные числа
        // ------------------------------------------------------------------ //

        // Сколько столбцов «что — сколько» влезает по ширине. То же число нужно и при
        // отрисовке, и при подгонке высоты, иначе шапка либо режет строки, либо пустует.
        private int RamHeadColumns()
        {
            int avail = _ramHead.ClientSize.Width - Px(2) * 2;
            return Math.Max(1, Math.Min(3, avail / Px(250)));
        }

        // Узкое окно раскладывает те же девять пар в большее число строк — высоту шапки
        // приходится добирать, иначе нижние цифры просто исчезают.
        private void RamFitHead()
        {
            if (_ramHead == null || _ramHead.ClientSize.Width < 40) return;
            int rows = (9 + RamHeadColumns() - 1) / RamHeadColumns();
            int want = Px(2) + Px(30) + Px(6) + Px(19) * (1 + rows) + Px(6);
            if (Math.Abs(_ramHead.Height - want) > 1) _ramHead.Height = want;
        }

        private void RamHeadPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            RamSnapshot s = _ramSnap;
            if (s == null) return;

            int pad = Px(2);
            int barH = Px(30);
            Rectangle bar = new Rectangle(pad, pad, _ramHead.ClientSize.Width - pad * 2, barH);
            if (bar.Width < 40) return;

            // Полоса — та же четвёрка, что и на графике: занято, изменено, ожидание, свободно.
            long total = s.Installed > 0 ? s.Installed : s.TotalPhys;
            if (total <= 0) return;
            long[] parts = { s.Active, s.Modified + s.ModifiedNoWrite, s.Standby, s.FreePages + s.Zeroed, s.HardwareReserved };
            int[] kinds = { RamKind.Process, RamKind.Modified, RamKind.Standby, RamKind.Free, RamKind.Reserved };
            float x = bar.X;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i] <= 0) continue;
                float w = (float)bar.Width * parts[i] / total;
                RectangleF seg = new RectangleF(x, bar.Y, w, bar.Height);
                using (SolidBrush br = new SolidBrush(RamColor(kinds[i], 0))) g.FillRectangle(br, seg);
                x += w;
            }
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, bar);

            Font f = new Font(Font.FontFamily, 9.5F);
            Font fb = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            try
            {
                int y = bar.Bottom + Px(6);
                int lineH = Px(19);

                // Первая строка — крупно и по делу: сколько всего и сколько занято.
                string head = Engine.FormatBytes(s.Installed) + Tr.S(" всего", " total")
                            + "   ·   " + Tr.S("занято ", "in use ") + Engine.FormatBytes(s.Used)
                            + " (" + s.UsedPercent.ToString("0.0", CultureInfo.InvariantCulture) + " %)"
                            + "   ·   " + Tr.S("доступно ", "available ") + Engine.FormatBytes(s.AvailPhys);
                TextRenderer.DrawText(g, head, fb, new Point(pad, y), _theme.Text, TextFormatFlags.NoPrefix);
                y += lineH;

                // Девять пар «что — сколько», разложенных по столько столбцов, сколько влезло:
                // при узком окне лучше три строки по две колонки, чем обрезанная третья.
                string[] cells = new string[]
                {
                    Tr.S("Процессы", "Processes"), Engine.FormatBytes(s.ProcPrivate),
                    Tr.S("Ожидание (кэш)", "Standby (cache)"), Engine.FormatBytes(s.Standby),
                    Tr.S("Выделено / лимит", "Committed / limit"),
                        Engine.FormatBytes(s.CommitTotal) + " / " + Engine.FormatBytes(s.CommitLimit),
                    Tr.S("Ядро и драйверы", "Kernel and drivers"),
                        Engine.FormatBytes(s.NonPagedPoolTotal + s.ResidentPagedPool + s.ResidentKernelCode + s.ResidentDriver),
                    Tr.S("Изменённые", "Modified"), Engine.FormatBytes(s.Modified + s.ModifiedNoWrite),
                    Tr.S("Файловый кэш", "File cache"), Engine.FormatBytes(s.CacheWithTransition),
                    Tr.S("Сжатая память", "Compressed"), Engine.FormatBytes(s.Compressed),
                    Tr.S("Не отнесено", "Unattributed"), Engine.FormatBytes(s.Unattributed),
                    Tr.S("Промахов страниц/с", "Page faults/s"),
                        s.FaultsPerSec.ToString("N0", CultureInfo.CurrentCulture)
                        + Tr.S("  ·  с диска ", "  ·  from disk ") + s.HardReadsPerSec.ToString("N0", CultureInfo.CurrentCulture)
                };

                int avail = _ramHead.ClientSize.Width - pad * 2;
                int cols = RamHeadColumns();
                int colW = avail / cols;
                int pairs = cells.Length / 2;
                for (int i = 0; i < pairs; i++)
                {
                    int col = i % cols, row = i / cols;
                    int cx = pad + col * colW;
                    int cy = y + row * lineH;
                    if (cy + lineH > _ramHead.ClientSize.Height) break;
                    string key = cells[i * 2], val = cells[i * 2 + 1];
                    TextRenderer.DrawText(g, key, f, new Point(cx, cy), _theme.Subtle, TextFormatFlags.NoPrefix);
                    int keyW = TextRenderer.MeasureText(key, f).Width + Px(8);
                    TextRenderer.DrawText(g, val, fb, new Rectangle(cx + keyW, cy, Math.Max(20, colW - keyW - Px(10)), lineH),
                                          _theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
            finally { f.Dispose(); fb.Dispose(); }
        }

        // ------------------------------------------------------------------ //
        //  График истории
        // ------------------------------------------------------------------ //

        private void RamChartPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            Rectangle box = _ramChart.ClientRectangle;
            box.Inflate(-1, -1);
            if (box.Width < 30 || box.Height < 20) return;
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, box);

            RamSnapshot s = _ramSnap;
            if (s == null || _ramHistory.Count < 2 || s.Installed <= 0)
            {
                TextRenderer.DrawText(g, Tr.S("История наполняется…", "History is filling up…"), Font, box,
                                      _theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }

            long total = s.TotalPhys > 0 ? s.TotalPhys : s.Installed;
            int n = _ramHistory.Count;
            float step = (float)(box.Width - 2) / Math.Max(1, n - 1);
            int[] kinds = { RamKind.Process, RamKind.Modified, RamKind.Standby };
            // Три слоя: занято, +изменённые, +ожидание. Остаток до верха — свободно.
            // Рисуются они от САМОГО ВЫСОКОГО к самому низкому: каждый следующий закрашивает
            // нижнюю часть предыдущего. В обратном порядке верхний слой съедает все остальные
            // и график становится одноцветным.
            for (int layer = 2; layer >= 0; layer--)
            {
                PointF[] pts = new PointF[n + 2];
                for (int i = 0; i < n; i++)
                {
                    RamPoint p = _ramHistory[i];
                    long v = p.Active;
                    if (layer >= 1) v += p.Modified;
                    if (layer >= 2) v += p.Standby;
                    float h = (float)box.Height * v / total;
                    if (h > box.Height) h = box.Height;
                    pts[i] = new PointF(box.X + 1 + i * step, box.Bottom - h);
                }
                pts[n] = new PointF(box.Right - 1, box.Bottom - 1);
                pts[n + 1] = new PointF(box.X + 1, box.Bottom - 1);
                using (SolidBrush br = new SolidBrush(RamColor(kinds[layer], 0)))
                    g.FillPolygon(br, pts);
            }

            Font f = new Font(Font.FontFamily, 8.25F);
            try
            {
                string span = Tr.S("последние ", "last ") + RamHistorySpan();
                TextRenderer.DrawText(g, span, f, new Point(box.X + 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
                string right = Engine.FormatBytes(total);
                Size sz = TextRenderer.MeasureText(right, f);
                TextRenderer.DrawText(g, right, f, new Point(box.Right - sz.Width - 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
            }
            finally { f.Dispose(); }
        }

        private string RamHistorySpan()
        {
            int seconds = _ramHistory.Count * _ramInterval / 1000;
            if (seconds < 90) return seconds + Tr.S(" с", " s");
            return (seconds / 60) + Tr.S(" мин", " min");
        }

        // ------------------------------------------------------------------ //
        //  Списки
        // ------------------------------------------------------------------ //

        private void RamFillLists()
        {
            RamSnapshot s = _ramSnap;
            if (s == null || _lvRamLists == null) return;
            long total = s.TotalPhys > 0 ? s.TotalPhys : 1;
            _lvRamLists.BeginUpdate();
            try
            {
                _lvRamLists.Items.Clear();
                RamListRow(Tr.S("Активная (в работе)", "Active (in use)"), s.Active, total,
                           Tr.S("страницы, которыми кто-то пользуется прямо сейчас", "pages somebody is using right now"));
                RamListRow(Tr.S("Изменённые", "Modified"), s.Modified, total,
                           Tr.S("изменены и ещё не записаны на диск", "changed and not yet written to disk"));
                RamListRow(Tr.S("Изменённые без записи", "Modified no-write"), s.ModifiedNoWrite, total,
                           Tr.S("изменены, но записывать их нельзя", "changed, but must not be written"));
                for (int i = 7; i >= 0; i--)
                    RamListRow(Tr.S("Ожидание, приоритет ", "Standby, priority ") + i, s.StandbyByPriority[i], total,
                               i == 0 ? Tr.S("освобождается первым", "released first")
                                      : Tr.S("кэш файлов и данных, отданный про запас", "cached file and program data kept just in case"));
                RamListRow(Tr.S("Ожидание, всего", "Standby, total"), s.Standby, total,
                           Tr.S("сумма восьми приоритетов — это и есть «кэш» в Диспетчере задач",
                                "the sum of the eight priorities — this is what Task Manager calls “cached”"));
                RamListRow(Tr.S("Свободные", "Free"), s.FreePages, total,
                           Tr.S("свободны, но ещё не обнулены", "free, but not zeroed yet"));
                RamListRow(Tr.S("Обнулённые", "Zeroed"), s.Zeroed, total,
                           Tr.S("готовы к выдаче любому процессу", "ready to be handed to any process"));
                RamListRow(Tr.S("Плохие", "Bad"), s.Bad, total,
                           Tr.S("помечены как сбойные и не используются", "marked faulty and never used"));
                RamListRow(Tr.S("Аппаратно зарезервировано", "Hardware reserved"), s.HardwareReserved,
                           s.Installed > 0 ? s.Installed : total,
                           Tr.S("разница между планками и тем, что видит Windows", "the gap between the modules and what Windows sees"));
            }
            finally { _lvRamLists.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvRamLists);
        }

        private void RamListRow(string title, long bytes, long total, string what)
        {
            ListViewItem it = new ListViewItem(title);
            it.SubItems.Add(Engine.FormatBytes(bytes));
            it.SubItems.Add((total > 0 ? 100.0 * bytes / total : 0).ToString("0.0", CultureInfo.InvariantCulture) + " %");
            it.SubItems.Add(what);
            it.Tag = NoCheckTag;
            _lvRamLists.Items.Add(it);
        }

        private void RamFillProcs()
        {
            RamSnapshot s = _ramSnap;
            if (s == null || _lvRamProcs == null) return;
            List<RamProc> rows = new List<RamProc>(s.Procs);
            rows.Sort(RamProcComparer);

            int topIndex = 0;
            if (_lvRamProcs.TopItem != null) topIndex = _lvRamProcs.TopItem.Index;

            _lvRamProcs.BeginUpdate();
            try
            {
                _lvRamProcs.Items.Clear();
                ListViewItem[] items = new ListViewItem[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    RamProc p = rows[i];
                    ListViewItem it = new ListViewItem(p.Name);
                    it.SubItems.Add(p.Pid.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(Engine.FormatBytes(p.PrivateWorkingSet));
                    it.SubItems.Add(Engine.FormatBytes(p.WorkingSet));
                    it.SubItems.Add(Engine.FormatBytes(p.Commit));
                    it.SubItems.Add(p.Delta == 0 ? "" : (p.Delta > 0 ? "+" : "−") + Engine.FormatBytes(Math.Abs(p.Delta)));
                    it.SubItems.Add(p.HardFaultRate >= 1 ? p.HardFaultRate.ToString("N0", CultureInfo.CurrentCulture) : "");
                    it.SubItems.Add(p.Threads.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(p.Handles.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(p.SessionId.ToString(CultureInfo.InvariantCulture));
                    it.Tag = p;
                    it.Checked = _ramChecked.Contains(p.Pid);
                    items[i] = it;
                }
                _ramFillingProcs = true;
                _lvRamProcs.Items.AddRange(items);

                // Список перечитывается каждую секунду. Без возврата прокрутки его нельзя
                // читать, а без возврата выделения выбранная строка гасла бы через секунду
                // после щелчка. Флаг заполнения снимается только после обоих возвратов —
                // иначе они сами сойдут за действия пользователя.
                if (topIndex > 0 && topIndex < _lvRamProcs.Items.Count)
                    try { _lvRamProcs.TopItem = _lvRamProcs.Items[topIndex]; }
                    catch { }
                if (_ramSelKey != null && _ramSelKey.StartsWith("pid:", StringComparison.Ordinal))
                    foreach (ListViewItem it in _lvRamProcs.Items)
                    {
                        RamProc p = it.Tag as RamProc;
                        if (p != null && "pid:" + p.Pid == _ramSelKey) { it.Selected = true; break; }
                    }
            }
            finally
            {
                _ramFillingProcs = false;
                _lvRamProcs.EndUpdate();
            }
            AutoFillLastColumnDeferred(_lvRamProcs);
        }

        private bool _ramFillingProcs;

        private int RamProcComparer(RamProc a, RamProc b)
        {
            int r;
            switch (_ramSort)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = a.Pid.CompareTo(b.Pid); break;
                case 3: r = a.WorkingSet.CompareTo(b.WorkingSet); break;
                case 4: r = a.Commit.CompareTo(b.Commit); break;
                case 5: r = a.Delta.CompareTo(b.Delta); break;
                case 6: r = a.HardFaultRate.CompareTo(b.HardFaultRate); break;
                case 7: r = a.Threads.CompareTo(b.Threads); break;
                case 8: r = a.Handles.CompareTo(b.Handles); break;
                case 9: r = a.SessionId.CompareTo(b.SessionId); break;
                default: r = a.PrivateWorkingSet.CompareTo(b.PrivateWorkingSet); break;
            }
            if (r == 0) r = a.Pid.CompareTo(b.Pid);
            return _ramSortDesc ? -r : r;
        }

        private void RamProcsColumnClick(object sender, ColumnClickEventArgs e)
        {
            // Числовые колонки при первом щелчке идут по убыванию: «кто больше всех» —
            // единственный вопрос, ради которого их сортируют. Имя — по возрастанию.
            if (e.Column == _ramSort) _ramSortDesc = !_ramSortDesc;
            else { _ramSort = e.Column; _ramSortDesc = e.Column >= 2; }
            RamMarkSortColumn();
            RamFillProcs();
        }

        // Стрелка в заголовке: без неё после щелчка не видно, по чему список отсортирован.
        private void RamMarkSortColumn()
        {
            if (_ramProcHeaders == null) return;
            for (int i = 0; i < _ramProcHeaders.Length && i < _lvRamProcs.Columns.Count; i++)
                _lvRamProcs.Columns[i].Text = i == _ramSort
                    ? _ramProcHeaders[i] + (_ramSortDesc ? "  ▼" : "  ▲")
                    : _ramProcHeaders[i];
        }

        private string[] _ramProcHeaders;

        private void RamProcsItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_ramFillingProcs) return;
            RamProc p = e.Item.Tag as RamProc;
            if (p == null) return;
            if (e.Item.Checked) _ramChecked.Add(p.Pid);
            else _ramChecked.Remove(p.Pid);
            RamUpdateSelectionUi();
        }

        // Щелчок по строке списка выбирает тот же процесс, что и блок на схеме.
        private void RamSyncSelectionFromList()
        {
            if (_ramFillingProcs) return;                 // это возврат выделения, а не щелчок
            if (_lvRamProcs == null || _lvRamProcs.SelectedItems.Count == 0) return;
            RamProc p = _lvRamProcs.SelectedItems[0].Tag as RamProc;
            if (p == null || _ramSnap == null) return;
            RamSelect(RamFind(_ramSnap.Slices, "pid:" + p.Pid));
        }

        private void RamFillPools()
        {
            if (_lvRamPools == null) return;
            List<RamPool> pools = null;
            try { pools = _engine.RamPools(); }
            catch { }
            // Таблица перечитывается каждый такт — без возврата прокрутки список уезжал бы
            // в начало под пальцами.
            int topIndex = _lvRamPools.TopItem != null ? _lvRamPools.TopItem.Index : 0;
            _lvRamPools.BeginUpdate();
            try
            {
                _lvRamPools.Items.Clear();
                if (pools == null || pools.Count == 0)
                {
                    ListViewItem none = new ListViewItem(Tr.S("Теги пула недоступны", "Pool tags are unavailable"));
                    none.SubItems.Add(""); none.SubItems.Add(""); none.SubItems.Add("");
                    none.SubItems.Add(Tr.S("система не отдала таблицу тегов", "the system did not return the tag table"));
                    none.Tag = NoCheckTag;
                    _lvRamPools.Items.Add(none);
                    return;
                }
                int shown = 0;
                foreach (RamPool p in pools)
                {
                    if (shown++ >= 300) break;
                    ListViewItem it = new ListViewItem(p.Tag);
                    it.SubItems.Add(Engine.FormatBytes(p.NonPaged));
                    it.SubItems.Add(Engine.FormatBytes(p.Paged));
                    it.SubItems.Add(Engine.FormatBytes(p.Total));
                    it.SubItems.Add((p.NonPagedAllocs + p.PagedAllocs).ToString("N0", CultureInfo.CurrentCulture));
                    it.Tag = NoCheckTag;
                    _lvRamPools.Items.Add(it);
                }
            }
            finally { _lvRamPools.EndUpdate(); }
            if (topIndex > 0 && topIndex < _lvRamPools.Items.Count)
                try { _lvRamPools.TopItem = _lvRamPools.Items[topIndex]; }
                catch { }
            AutoFillLastColumnDeferred(_lvRamPools);
        }

        private void RamFillHardware()
        {
            if (_lvRamHw == null) return;
            _lvRamHw.BeginUpdate();
            try
            {
                _lvRamHw.Items.Clear();
                List<RamModule> mods = _engine.RamModules();
                foreach (RamModule m in mods)
                {
                    string what = (m.Slot ?? Tr.S("слот", "slot"));
                    if (!string.IsNullOrEmpty(m.Bank)) what += " · " + m.Bank;
                    string details = (m.Kind ?? "") + (m.Speed > 0 ? " · " + m.Speed + Tr.S(" МТ/с", " MT/s") : "");
                    if (!string.IsNullOrEmpty(m.Maker)) details += " · " + m.Maker;
                    if (!string.IsNullOrEmpty(m.Part)) details += " · " + m.Part;
                    RamHwRow(Tr.S("Планка", "Module"), what, m.Bytes, details.Trim(' ', '·'));
                }
                if (mods.Count == 0)
                    RamHwRow(Tr.S("Планки", "Modules"), Tr.S("нет данных SMBIOS", "no SMBIOS data"), 0,
                             Tr.S("прошивка не отдала таблицу памяти", "the firmware did not return the memory table"));

                RamSnapshot s = _ramSnap;
                if (s != null)
                {
                    RamHwRow(Tr.S("Итого", "Total"), Tr.S("установлено", "installed"), s.Installed, "");
                    RamHwRow(Tr.S("Итого", "Total"), Tr.S("видит Windows", "seen by Windows"), s.TotalPhys, "");
                    RamHwRow(Tr.S("Итого", "Total"), Tr.S("аппаратно зарезервировано", "hardware reserved"), s.HardwareReserved,
                             Tr.S("забрали чипсет, видеоядро и прошивка", "taken by the chipset, the iGPU and the firmware"));
                }

                List<RamRange> ranges = _engine.RamRanges();
                long sum = 0;
                foreach (RamRange r in ranges)
                {
                    sum += r.Length;
                    RamHwRow(Tr.S("Диапазон", "Range"),
                             "0x" + r.Start.ToString("X12", CultureInfo.InvariantCulture) + " … 0x"
                             + (r.Start + r.Length - 1).ToString("X12", CultureInfo.InvariantCulture),
                             r.Length, "");
                }
                if (ranges.Count > 0)
                    RamHwRow(Tr.S("Диапазоны", "Ranges"), Tr.S("сумма", "sum"), sum,
                             Tr.S("столько физических адресов отдано под ОЗУ", "this much of the physical address space is RAM"));
            }
            finally { _lvRamHw.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvRamHw);
        }

        private void RamHwRow(string section, string what, long bytes, string details)
        {
            ListViewItem it = new ListViewItem(section);
            it.SubItems.Add(what);
            it.SubItems.Add(bytes > 0 ? Engine.FormatBytes(bytes) : "");
            it.SubItems.Add(details ?? "");
            it.Tag = NoCheckTag;
            _lvRamHw.Items.Add(it);
        }
    }
}
