// SysDeck — вкладка «Видеокарта»: полоса, график, процессы, карточки, перезапуск и завершение.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
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
        // ------------------------------------------------------------------ //
        //  Шапка
        // ------------------------------------------------------------------ //

        private int GpuHeadColumns()
        {
            int avail = _gpuHead.ClientSize.Width - Px(2) * 2;
            return Math.Max(1, Math.Min(3, avail / Px(250)));
        }

        private void GpuFitHead()
        {
            if (_gpuHead == null || _gpuHead.ClientSize.Width < 40) return;
            int rows = (9 + GpuHeadColumns() - 1) / GpuHeadColumns();
            int want = Px(2) + Px(30) + Px(6) + Px(19) * (1 + rows) + Px(6);
            if (Math.Abs(_gpuHead.Height - want) > 1) _gpuHead.Height = want;
        }

        private void GpuHeadPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            GpuAdapter a = GpuCurrent();
            if (a == null) return;

            int pad = Px(2);
            Rectangle bar = new Rectangle(pad, pad, _gpuHead.ClientSize.Width - pad * 2, Px(30));
            if (bar.Width < 40) return;

            // Полоса — та же схема одной строкой: процессы, GPU-процессы браузеров, драйвер, свободно.
            List<RamSlice> root = GpuRootAll();
            long total = 0, helpers = 0, procs = 0, system = 0, free = 0;
            foreach (RamSlice s in root)
            {
                total += s.Bytes;
                if (s.Kind == GpuKind.System) system += s.Bytes;
                else if (s.Kind == GpuKind.Free) free += s.Bytes;
                else GpuSplitHelpers(s, ref helpers, ref procs);
            }
            if (total > 0)
            {
                long[] parts = { procs, helpers, system, free };
                int[] kinds = { GpuKind.Process, GpuKind.Helper, GpuKind.System, GpuKind.Free };
                float x = bar.X;
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i] <= 0) continue;
                    float w = (float)bar.Width * parts[i] / total;
                    using (SolidBrush br = new SolidBrush(GpuColor(kinds[i], 0))) g.FillRectangle(br, new RectangleF(x, bar.Y, w, bar.Height));
                    x += w;
                }
            }
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, bar);

            Font f = new Font(Font.FontFamily, 9.5F);
            Font fb = new Font(Font.FontFamily, 9.5F, FontStyle.Bold);
            try
            {
                int y = bar.Bottom + Px(6);
                int lineH = Px(19);
                double usedPct = a.DedicatedTotal > 0 ? 100.0 * a.DedicatedUsed / a.DedicatedTotal : 0;
                string head = a.Name
                            + "   ·   " + Tr.S("выделенная ", "dedicated ") + Engine.FormatBytes(a.DedicatedUsed)
                            + Tr.S(" из ", " of ") + Engine.FormatBytes(a.DedicatedTotal)
                            + " (" + usedPct.ToString("0.0", CultureInfo.InvariantCulture) + " %)"
                            + "   ·   " + Tr.S("загрузка ", "load ") + a.Load.ToString("0", CultureInfo.InvariantCulture) + " %";
                TextRenderer.DrawText(g, head, fb, new Rectangle(pad, y, _gpuHead.ClientSize.Width - pad * 2, lineH), _theme.Text,
                                      TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += lineH;

                int helperCount = 0;
                long helperBytes = 0;
                foreach (GpuProc p in _gpuSnap.Procs)
                    if (p.Helper && string.Equals(p.Luid, a.Luid, StringComparison.OrdinalIgnoreCase))
                    {
                        helperCount++;
                        helperBytes += _gpuSharedMode ? p.Shared : p.Dedicated;
                    }

                string[] cells = new string[]
                {
                    Tr.S("Процессы", "Processes"), Engine.FormatBytes(procs + helpers),
                    Tr.S("Система и драйвер", "System and driver"), Engine.FormatBytes(system),
                    Tr.S("Свободно", "Free"), Engine.FormatBytes(free),
                    Tr.S("Общая память", "Shared memory"), Engine.FormatBytes(a.SharedUsed) + Tr.S(" из ", " of ") + Engine.FormatBytes(a.SharedTotal),
                    Tr.S("GPU-процессы браузеров", "Browser GPU processes"),
                        helperCount + "  ·  " + Engine.FormatBytes(helperBytes),
                    Tr.S("Выделено всего", "Committed total"), Engine.FormatBytes(a.Committed),
                    Tr.S("Процессов на карте", "Processes on the card"), a.ProcCount.ToString(CultureInfo.InvariantCulture),
                    Tr.S("Самый занятый движок", "Busiest engine"),
                        string.IsNullOrEmpty(a.LoadEngine) ? "—" : a.LoadEngine + "  ·  " + a.Load.ToString("0", CultureInfo.InvariantCulture) + " %",
                    Tr.S("Производитель", "Vendor"), string.IsNullOrEmpty(a.Vendor) ? "—" : a.Vendor
                };
                int avail = _gpuHead.ClientSize.Width - pad * 2;
                int cols = GpuHeadColumns();
                int colW = avail / cols;
                for (int i = 0; i < cells.Length / 2; i++)
                {
                    int cx = pad + (i % cols) * colW;
                    int cy = y + (i / cols) * lineH;
                    if (cy + lineH > _gpuHead.ClientSize.Height) break;
                    string key = cells[i * 2], val = cells[i * 2 + 1];
                    TextRenderer.DrawText(g, key, f, new Point(cx, cy), _theme.Subtle, TextFormatFlags.NoPrefix);
                    int keyW = TextRenderer.MeasureText(key, f).Width + Px(8);
                    TextRenderer.DrawText(g, val, fb, new Rectangle(cx + keyW, cy, Math.Max(20, colW - keyW - Px(10)), lineH),
                                          _theme.Text, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }
            }
            finally { f.Dispose(); fb.Dispose(); }
        }

        private static void GpuSplitHelpers(RamSlice s, ref long helpers, ref long procs)
        {
            if (s.Children != null && s.Children.Count > 0)
            {
                foreach (RamSlice c in s.Children) GpuSplitHelpers(c, ref helpers, ref procs);
                return;
            }
            if (s.Kind == GpuKind.Helper) helpers += s.Bytes;
            else procs += s.Bytes;
        }

        // ------------------------------------------------------------------ //
        //  График истории: занятая выделенная память заливкой, загрузка — линией
        // ------------------------------------------------------------------ //

        private void GpuChartPaint(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(_theme.Bg);
            Rectangle box = _gpuChart.ClientRectangle;
            box.Inflate(-1, -1);
            if (box.Width < 30 || box.Height < 20) return;
            using (Pen pen = new Pen(_theme.Border)) g.DrawRectangle(pen, box);

            GpuAdapter a = GpuCurrent();
            List<GpuPoint> hist;
            if (a == null || !_gpuHistory.TryGetValue(a.Luid, out hist) || hist.Count < 2)
            {
                TextRenderer.DrawText(g, Tr.S("История наполняется…", "History is filling up…"), Font, box,
                                      _theme.Subtle, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            // Шкала — по пику истории с запасом, а не по объёму карты: 1 ГБ на 24-гигабайтной карте
            // лёг бы на график ниточкой у нижнего края, и рост расхода был бы не виден.
            long card = _gpuSharedMode ? a.SharedTotal : a.DedicatedTotal;
            long peak = 0;
            foreach (GpuPoint p in hist) peak = Math.Max(peak, _gpuSharedMode ? p.Shared : p.Dedicated);
            long total = GpuNiceScale(peak + peak / 4);
            if (card > 0 && total > card) total = card;
            if (total <= 0) total = 1;

            int n = hist.Count;
            float step = (float)(box.Width - 2) / Math.Max(1, n - 1);
            PointF[] area = new PointF[n + 2];
            PointF[] load = new PointF[n];
            for (int i = 0; i < n; i++)
            {
                long v = _gpuSharedMode ? hist[i].Shared : hist[i].Dedicated;
                float h = Math.Min(box.Height, (float)box.Height * v / total);
                area[i] = new PointF(box.X + 1 + i * step, box.Bottom - h);
                float lh = (float)(box.Height * Math.Min(100, hist[i].Load) / 100.0);
                load[i] = new PointF(box.X + 1 + i * step, box.Bottom - 1 - lh);
            }
            area[n] = new PointF(box.Right - 1, box.Bottom - 1);
            area[n + 1] = new PointF(box.X + 1, box.Bottom - 1);
            using (SolidBrush br = new SolidBrush(GpuColor(GpuKind.Process, 0))) g.FillPolygon(br, area);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(236, 122, 72), Px(2))) g.DrawLines(pen, load);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;

            Font f = new Font(Font.FontFamily, 8.25F);
            try
            {
                int seconds = n * _gpuInterval / 1000;
                string span = Tr.S("последние ", "last ") + (seconds < 90 ? seconds + Tr.S(" с", " s") : (seconds / 60) + Tr.S(" мин", " min"))
                            + Tr.S("  ·  заливка — занятая память, линия — загрузка", "  ·  fill — memory in use, line — load");
                TextRenderer.DrawText(g, span, f, new Point(box.X + 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
                string right = Engine.FormatBytes(total);
                Size sz = TextRenderer.MeasureText(right, f);
                TextRenderer.DrawText(g, right, f, new Point(box.Right - sz.Width - 6, box.Y + 4), _theme.Subtle, TextFormatFlags.NoPrefix);
            }
            finally { f.Dispose(); }
        }

        // Круглая граница шкалы: 256 МБ, 512 МБ, 1 ГБ, 2 ГБ… — подпись читается сразу.
        private static long GpuNiceScale(long bytes)
        {
            long v = 256L * 1024 * 1024;
            while (v < bytes && v < long.MaxValue / 2) v *= 2;
            return v;
        }

        // ------------------------------------------------------------------ //
        //  Списки
        // ------------------------------------------------------------------ //

        private void GpuFillProcs()
        {
            GpuAdapter a = GpuCurrent();
            if (_lvGpuProcs == null) return;
            List<GpuProc> rows = new List<GpuProc>();
            if (a != null)
                foreach (GpuProc p in _gpuSnap.Procs)
                    if (string.Equals(p.Luid, a.Luid, StringComparison.OrdinalIgnoreCase)) rows.Add(p);
            rows.Sort(GpuProcComparer);

            int topIndex = _lvGpuProcs.TopItem != null ? _lvGpuProcs.TopItem.Index : 0;
            _lvGpuProcs.BeginUpdate();
            try
            {
                _lvGpuProcs.Items.Clear();
                ListViewItem[] items = new ListViewItem[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    GpuProc p = rows[i];
                    ListViewItem it = new ListViewItem(p.Name);
                    it.SubItems.Add(p.Pid.ToString(CultureInfo.InvariantCulture));
                    it.SubItems.Add(Engine.FormatBytes(p.Dedicated));
                    it.SubItems.Add(Engine.FormatBytes(p.Shared));
                    it.SubItems.Add(p.Committed > 0 ? Engine.FormatBytes(p.Committed) : "");
                    it.SubItems.Add(p.Load >= 0.5 ? p.Load.ToString("0", CultureInfo.InvariantCulture) + " %" : "");
                    it.SubItems.Add(p.Load >= 0.5 && p.EngineType != null ? p.EngineType : "");
                    it.SubItems.Add(GpuWhat(p));
                    it.Tag = p;
                    it.Checked = _gpuChecked.Contains(p.Pid);
                    items[i] = it;
                }
                _gpuFillingProcs = true;
                _lvGpuProcs.Items.AddRange(items);
                if (topIndex > 0 && topIndex < _lvGpuProcs.Items.Count)
                    try { _lvGpuProcs.TopItem = _lvGpuProcs.Items[topIndex]; }
                    catch { }
                if (_gpuSelKey != null)
                    foreach (ListViewItem it in _lvGpuProcs.Items)
                    {
                        GpuProc p = it.Tag as GpuProc;
                        if (p != null && _gpuSelKey.EndsWith("pid:" + p.Pid, StringComparison.Ordinal)) { it.Selected = true; break; }
                    }
            }
            finally
            {
                _gpuFillingProcs = false;
                _lvGpuProcs.EndUpdate();
            }
            AutoFillLastColumnDeferred(_lvGpuProcs);
        }

        private static string GpuWhat(GpuProc p)
        {
            List<string> parts = new List<string>();
            if (p.Helper) parts.Add(Tr.S("GPU-процесс: перезапуск безопасен, приложение поднимет его само",
                                         "GPU process: safe to restart, the app brings it back itself"));
            if (p.Protected) parts.Add(Tr.S("системный процесс — не завершается", "system process — never terminated"));
            if (p.Inflated) parts.Add(Tr.S("счётчик Windows завышен: ", "the Windows counter is inflated: ") + Engine.FormatBytes(p.DedicatedRaw));
            return string.Join("  ·  ", parts.ToArray());
        }

        private int GpuProcComparer(GpuProc a, GpuProc b)
        {
            int r;
            switch (_gpuSort)
            {
                case 0: r = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); break;
                case 1: r = a.Pid.CompareTo(b.Pid); break;
                case 3: r = a.Shared.CompareTo(b.Shared); break;
                case 4: r = a.Committed.CompareTo(b.Committed); break;
                case 5: r = a.Load.CompareTo(b.Load); break;
                case 6: r = string.Compare(a.EngineType ?? "", b.EngineType ?? "", StringComparison.OrdinalIgnoreCase); break;
                case 7: r = string.Compare(GpuWhat(a), GpuWhat(b), StringComparison.OrdinalIgnoreCase); break;
                default: r = a.Dedicated.CompareTo(b.Dedicated); break;
            }
            if (r == 0) r = a.Pid.CompareTo(b.Pid);
            return _gpuSortDesc ? -r : r;
        }

        private void GpuProcsColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == _gpuSort) _gpuSortDesc = !_gpuSortDesc;
            else { _gpuSort = e.Column; _gpuSortDesc = e.Column >= 2 && e.Column <= 5; }
            GpuMarkSortColumn();
            GpuFillProcs();
        }

        private void GpuMarkSortColumn()
        {
            if (_gpuProcHeaders == null) return;
            for (int i = 0; i < _gpuProcHeaders.Length && i < _lvGpuProcs.Columns.Count; i++)
                _lvGpuProcs.Columns[i].Text = i == _gpuSort
                    ? _gpuProcHeaders[i] + (_gpuSortDesc ? "  ▼" : "  ▲")
                    : _gpuProcHeaders[i];
        }

        private void GpuProcsItemChecked(object sender, ItemCheckedEventArgs e)
        {
            if (_gpuFillingProcs) return;
            GpuProc p = e.Item.Tag as GpuProc;
            if (p == null) return;
            if (e.Item.Checked) _gpuChecked.Add(p.Pid);
            else _gpuChecked.Remove(p.Pid);
            GpuUpdateSelectionUi();
        }

        private void GpuSyncSelectionFromList()
        {
            if (_gpuFillingProcs || _lvGpuProcs == null || _lvGpuProcs.SelectedItems.Count == 0) return;
            GpuProc p = _lvGpuProcs.SelectedItems[0].Tag as GpuProc;
            if (p == null) return;
            string key = (_gpuSharedMode ? "gs:" : "gd:") + "pid:" + p.Pid.ToString(CultureInfo.InvariantCulture);
            GpuSelect(RamFind(GpuRootAll(), key));
        }

        private void GpuFillCards()
        {
            if (_lvGpuCards == null || _gpuSnap == null) return;
            _lvGpuCards.BeginUpdate();
            try
            {
                _lvGpuCards.Items.Clear();
                foreach (GpuAdapter a in _gpuSnap.Adapters)
                {
                    ListViewItem it = new ListViewItem(a.Name);
                    it.Name = a.Luid;
                    it.SubItems.Add(a.Vendor);
                    it.SubItems.Add(a.Known ? Engine.FormatBytes(a.DedicatedTotal) : "?");
                    it.SubItems.Add(Engine.FormatBytes(a.DedicatedUsed));
                    it.SubItems.Add(a.Known ? Engine.FormatBytes(a.SharedTotal) : "?");
                    it.SubItems.Add(Engine.FormatBytes(a.SharedUsed));
                    it.SubItems.Add(a.Load.ToString("0", CultureInfo.InvariantCulture) + " %");
                    it.SubItems.Add(a.ProcCount.ToString(CultureInfo.InvariantCulture));
                    it.Tag = NoCheckTag;
                    if (string.Equals(a.Luid, _gpuLuid, StringComparison.OrdinalIgnoreCase)) it.Selected = true;
                    _lvGpuCards.Items.Add(it);
                }
            }
            finally { _lvGpuCards.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvGpuCards);
        }

        // ------------------------------------------------------------------ //
        //  Сбросы
        // ------------------------------------------------------------------ //

        private void GpuSetBusy(bool busy)
        {
            Interlocked.Exchange(ref _gpuActBusy, busy ? 1 : 0);
            GpuUpdateSelectionUi();
        }

        private long GpuUsedNow(string luid)
        {
            if (_gpuSnap == null) return 0;
            long sum = 0;
            foreach (GpuAdapter a in _gpuSnap.Adapters)
                if (luid == null || string.Equals(a.Luid, luid, StringComparison.OrdinalIgnoreCase)) sum += a.DedicatedUsed;
            return sum;
        }

        // Итог сброса считается по замеру через несколько секунд: сразу после действия приложения
        // ещё только поднимают новые поверхности, и «освобождено» было бы обманом в любую сторону.
        private void GpuScheduleReport(string what, string luid, long before, int seconds)
        {
            _gpuReportWhat = what;
            _gpuReportLuid = luid;
            _gpuReportBefore = before;
            _gpuReportAt = DateTime.UtcNow.AddSeconds(seconds);
        }

        private void GpuCheckReport()
        {
            if (_gpuReportAt == DateTime.MinValue || DateTime.UtcNow < _gpuReportAt) return;
            _gpuReportAt = DateTime.MinValue;
            long after = GpuUsedNow(_gpuReportLuid);
            long delta = _gpuReportBefore - after;
            GpuSay(_gpuReportWhat + Tr.S("  ·  видеопамять: было ", "  ·  video memory: was ") + Engine.FormatBytes(_gpuReportBefore)
                   + Tr.S(", стало ", ", now ") + Engine.FormatBytes(after)
                   + (delta > 0 ? Tr.S("  (освобождено ", "  (freed ") + Engine.FormatBytes(delta) + ")"
                                : Tr.S("  (память снова занята — приложения восстановили своё)", "  (the memory is in use again — the apps restored what they need)")));
        }

        private void GpuRestartHelpers()
        {
            if (_gpuActBusy != 0) return;
            List<GpuProc> targets = new List<GpuProc>();
            foreach (int pid in GpuSelectedPids())
            {
                GpuProc p = GpuProcOf(pid);
                if (p != null && p.Helper && !targets.Contains(p)) targets.Add(p);
            }
            if (targets.Count == 0) return;

            List<string> shown = new List<string>();
            foreach (GpuProc p in targets)
                shown.Add("  " + p.Name + " (pid " + p.Pid + ")  ·  " + Engine.FormatBytes(p.Dedicated + p.Shared));
            if (!MsgAsk(Tr.S("Перезапустить GPU-процессы?\r\n\r\n", "Restart the GPU processes?\r\n\r\n")
                        + string.Join("\r\n", shown.ToArray())
                        + Tr.S("\r\n\r\nПриложение само запустит GPU-процесс заново: окна, вкладки и несохранённый текст остаются, изображение может мигнуть. "
                               + "Одно приложение — не чаще раза в три минуты.",
                               "\r\n\r\nThe app starts its GPU process again by itself: windows, tabs and unsaved text stay, the picture may blink. "
                               + "Once every three minutes per app at most."),
                        Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Перезапускаю GPU-процессы…", "Restarting the GPU processes…"));
            string luid = _gpuLuid;
            long before = GpuUsedNow(luid);
            List<int> pids = new List<int>();
            foreach (GpuProc p in targets) pids.Add(p.Pid);

            Thread t = new Thread(delegate()
            {
                List<string> lines = new List<string>();
                int ok = 0;
                foreach (int pid in pids)
                {
                    if (_closing) break;
                    GpuAction r;
                    try { r = _engine.GpuRestartHelper(pid); }
                    catch (Exception ex) { r = new GpuAction(); r.Message = ex.Message; }
                    if (r.Ok) ok++;
                    lines.Add(r.Message ?? "");
                }
                int okCopy = ok;
                string text = string.Join("  ·  ", lines.ToArray());
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    _gpuChecked.Clear();
                    GpuSelect(null);
                    GpuSay(text);
                    if (okCopy > 0) GpuScheduleReport(text, luid, before, 4);
                    GpuTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuKillSelected()
        {
            if (_gpuActBusy != 0) return;
            List<int> pids = new List<int>();
            List<string> shown = new List<string>();
            List<string> skipped = new List<string>();
            foreach (int pid in GpuSelectedPids())
            {
                GpuProc p = GpuProcOf(pid);
                if (p == null || pids.Contains(pid)) continue;
                if (p.Protected) { if (!skipped.Contains(p.Name)) skipped.Add(p.Name); continue; }
                pids.Add(pid);
                shown.Add(p.Name + " (pid " + pid + ")  ·  " + Engine.FormatBytes(p.Dedicated + p.Shared)
                          + (p.Helper ? Tr.S("  ·  лучше «Перезапустить GPU-процесс»", "  ·  «Restart the GPU process» is gentler") : ""));
            }
            if (pids.Count == 0)
            {
                if (skipped.Count > 0)
                    MsgInfo(Tr.S("Системные процессы не завершаются: ", "System processes are never terminated: ")
                            + string.Join(", ", skipped.ToArray())
                            + Tr.S(".\r\n\r\nЕсли видеопамять держит dwm.exe, её освобождает перезапуск видеодрайвера.",
                                   ".\r\n\r\nIf dwm.exe holds the video memory, restarting the graphics driver releases it."),
                            Tr.S("Видеопамять", "Video memory"));
                return;
            }
            string question = DevKillQuestion(Tr.S("Видеопамять", "Video memory"), shown);
            if (skipped.Count > 0)
                question += Tr.S("\r\n\r\nНе будут завершены (системные): ", "\r\n\r\nWill not be terminated (system): ") + string.Join(", ", skipped.ToArray());
            if (!MsgAsk(question, Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Завершаю выбранные процессы…", "Terminating the selected processes…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));
            string luid = _gpuLuid;
            long before = GpuUsedNow(luid);
            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                long freed = 0;
                int killed = 0;
                string err = null;
                try { killed = _engine.TerminateMany(list, out freed, null, delegate { return _closing; }); }
                catch (Exception ex) { err = ex.Message; }
                List<int> alive;
                try { alive = _engine.SurvivorsOf(list); }
                catch { alive = new List<int>(); }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                int killedCopy = killed;
                string errCopy = err;
                List<int> aliveCopy = alive;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    _gpuChecked.Clear();
                    GpuSelect(null);
                    string text = errCopy != null
                        ? Tr.S("Не удалось: ", "Failed: ") + errCopy
                        : killedCopy > 0
                            ? Tr.S("Завершено процессов: ", "Terminated: ") + killedCopy
                            : Tr.S("Ни один процесс не завершился — нужны права администратора или процесс защищён.",
                                   "Not a single process terminated — administrator rights are needed, or the process is protected.");
                    GpuSay(text);
                    if (killedCopy > 0) GpuScheduleReport(text, luid, before, 3);
                    GpuTick();
                    if (errCopy == null && aliveCopy.Count > 0) GpuOfferElevatedKill(aliveCopy, luid, before);
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuOfferElevatedKill(List<int> pids, string luid, long before)
        {
            if (Elevated || pids.Count == 0) return;
            List<string> shown = new List<string>();
            foreach (int pid in pids)
            {
                GpuProc p = GpuProcOf(pid);
                shown.Add((p != null ? p.Name + " " : "") + "(pid " + pid + ")");
            }
            if (!MsgAsk(Tr.S("Не удалось завершить процессов: ", "Processes that would not terminate: ") + pids.Count
                        + Tr.S(" — им нужны права администратора.\r\n\r\n", " — they need administrator rights.\r\n\r\n")
                        + string.Join("\r\n", shown.ToArray())
                        + Tr.S("\r\n\r\nЗапросить права и повторить?", "\r\n\r\nAsk for rights and retry?"),
                        Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            GpuSay(Tr.S("Запрашиваю права администратора…", "Asking for administrator rights…"));
            BeginWrite(Tr.S("завершение процессов", "terminating processes"));
            List<int> list = pids;
            Thread t = new Thread(delegate()
            {
                ElevResult r;
                try { r = KillElevated(list, null, delegate { return _closing; }); }
                catch (Exception ex) { r = new ElevResult(); r.Message = ex.Message; }
                EndWrite(Tr.S("завершение процессов", "terminating processes"));
                ElevResult done = r;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    string text = done.Ok
                        ? Tr.S("Завершено процессов: ", "Terminated: ") + done.Count
                        : done.Declined
                            ? Tr.S("Запрос прав отклонён — процессы остались на месте.", "The rights prompt was declined — the processes are still running.")
                            : Tr.S("Не удалось завершить: ", "Could not terminate: ") + (done.Message ?? "");
                    GpuSay(text);
                    if (done.Ok) GpuScheduleReport(text, luid, before, 3);
                    GpuTick();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void GpuResetDriver()
        {
            if (_gpuActBusy != 0) return;

            // Кто может пострадать: программы, которые прямо сейчас рисуют через 3D или держат
            // заметный объём памяти. Браузеры и окна Windows переживают сброс сами — их не пугаем.
            List<string> risky = new List<string>();
            if (_gpuSnap != null)
                foreach (GpuProc p in _gpuSnap.Procs)
                {
                    if (p.Protected || p.Helper || risky.Contains(p.Name)) continue;
                    bool drawing3d = p.Load >= 1 && string.Equals(p.EngineType, "3D", StringComparison.OrdinalIgnoreCase);
                    if (drawing3d || p.Dedicated >= 256L * 1024 * 1024) risky.Add(p.Name);
                }

            string text = Tr.S("Перезапустить видеодрайвер?\r\n\r\n", "Restart the graphics driver?\r\n\r\n")
                        + Tr.S("Экран погаснет на одну-две секунды, затем изображение вернётся. Браузеры и окна Windows восстанавливаются сами, прав администратора не нужно.",
                               "The screen goes dark for a second or two, then the picture comes back. Browsers and Windows itself recover on their own, no administrator rights needed.");
            if (risky.Count > 0)
                text += Tr.S("\r\n\r\nСейчас видеокарту используют программы, которые могут закрыться с ошибкой — сохраните в них работу:\r\n  ",
                             "\r\n\r\nThese programs are using the graphics card right now and may close with an error — save your work in them:\r\n  ")
                      + string.Join("\r\n  ", risky.ToArray());
            text += Tr.S("\r\n\r\nПродолжить?", "\r\n\r\nContinue?");
            if (!MsgAsk(text, Tr.S("Видеопамять", "Video memory"))) return;

            GpuSetBusy(true);
            long before = GpuUsedNow(null);
            Thread t = new Thread(delegate()
            {
                // Пауза, чтобы окно подтверждения успело исчезнуть: иначе сброс застаёт его
                // посреди анимации закрытия, и на секунду после возврата экрана оно висит призраком.
                Thread.Sleep(400);
                GpuAction r;
                try { r = _engine.GpuResetDriver(); }
                catch (Exception ex) { r = new GpuAction(); r.Message = ex.Message; }
                GpuAction done = r;
                UiPost(delegate
                {
                    GpuSetBusy(false);
                    GpuSay(done.Message ?? "");
                    if (done.Ok) GpuScheduleReport(Tr.S("Перезапуск видеодрайвера", "Graphics driver restart"), null, before, 6);
                });
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
