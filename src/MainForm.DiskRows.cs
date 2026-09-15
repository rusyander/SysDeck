// SysDeck — вкладка «Диск»: строки, дубликаты, открытие, удаление в Корзину, статус.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    public partial class MainForm
    {
        private void FillDiskRows(List<ListViewItem> rows)
        {
            MemBeginFill();
            _lvDisk.BeginUpdate();
            _suspendDiskChecked = true;    // очистка списка с отмеченными строками тоже шлёт ItemChecked
            try
            {
                _lvDisk.Items.Clear();
                _lvDisk.Items.AddRange(rows.ToArray());
                MemEndFill(_lvDisk, DiskScope, false, DiskMemKey, null);
            }
            finally { _suspendDiskChecked = false; _lvDisk.EndUpdate(); }
            AutoFillLastColumnDeferred(_lvDisk);
        }

        private void RefreshDupList()
        {
            DiskDir scope = _diskSelected ?? _diskScan.RootDir;
            long min = DiskEffectiveMin();
            if (_dupGroups != null && ReferenceEquals(_dupScope, scope) && _dupMin == min) { ShowDupGroups(scope); return; }
            if (Interlocked.CompareExchange(ref _diskScanBusy, 1, 0) != 0)
            {
                // Выбор другой папки во время хэширования больше не пропадает: он ставится в
                // очередь и пересчитывается сам. Раньше клик просто отклонялся, и готовый
                // список принадлежал прошлой папке, тогда как в дереве подсвечена была новая.
                if (_dupSearching)
                {
                    _dupRerun = true;
                    _diskDeferred = Tr.S("после текущего поиска пересчитаю для ", "will recount for ") + scope.Path;
                }
                else _lblDiskStatus.Text = Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish.");
                return;
            }
            _dupSearching = true;
            _engine.ResetDiskScanCancel();
            _recycleCancel = false;
            _btnDiskStop.Enabled = true;
            _btnDiskScan.Enabled = false;      // иначе «Сканировать» во время поиска выглядел мёртвым
            FillDiskRows(new List<ListViewItem>());
            StartDiskTicker(Tr.S("Поиск дубликатов в ", "Finding duplicates in ") + scope.Path);
            _diskDetail = Tr.S("сравнение по размеру…", "comparing sizes…");
            DiskScanResult snap = _diskScan;
            Thread t = new Thread(delegate()
            {
                List<DupGroup> groups = null;
                try
                {
                    groups = _engine.FindDuplicates(snap, scope, min, delegate(long done, long all)
                    {
                        DateTime now = DateTime.UtcNow;
                        if ((now - _diskProgressAt).TotalMilliseconds < 250) return;
                        _diskProgressAt = now;
                        _diskDetail = Tr.S("прочитано ", "hashed ") + Engine.FormatBytes(done)
                                    + Tr.S(" из ", " of ") + Engine.FormatBytes(all);
                    });
                }
                catch { }
                bool cancelled = _engine.DiskScanCancelled;
                Interlocked.Exchange(ref _diskScanBusy, 0);
                UiPost(delegate
                {
                    StopDiskTicker();
                    _dupSearching = false;
                    _btnDiskStop.Enabled = false;
                    _btnDiskScan.Enabled = true;
                    bool rerun = _dupRerun;
                    _dupRerun = false;
                    if (!ReferenceEquals(snap, _diskScan)) return;      // за это время начали новый обход
                    _dupGroups = groups ?? new List<DupGroup>();
                    _dupScope = cancelled ? null : scope;               // прерванный поиск не кэшируем
                    _dupCancelled = cancelled;
                    _dupMin = min;
                    if (_diskMode == "dups") ShowDupGroups(scope);
                    // Остановленный поиск не перезапускаем: «Стоп» значит «хватит».
                    if (rerun && !cancelled && _diskMode == "dups") RefreshDupList();
                    else ApplyPendingDiskMin();
                });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void ShowDupGroups(DiskDir scope)
        {
            List<ListViewItem> rows = new List<ListViewItem>();
            long waste = 0; int files = 0;
            int gi = 0;
            foreach (DupGroup g in _dupGroups)
            {
                gi++;
                waste += g.Waste; files += g.Files.Count;
                ListViewItem head = new ListViewItem(Tr.S("Группа ", "Group ") + gi + "  ·  " + g.Files.Count
                    + " × " + Engine.FormatBytes(g.Size));
                head.SubItems.Add(""); head.SubItems.Add("");
                head.SubItems.Add(Tr.S("лишних ", "redundant ") + Engine.FormatBytes(g.Waste));
                head.Tag = NoCheckTag;
                rows.Add(head);
                foreach (DiskFile f in g.Files) rows.Add(FileRow(f));
            }
            FillDiskRows(rows);
            _diskNote = _dupGroups.Count == 0
                ? Tr.S("Дубликатов (от ", "No duplicates (") + (_dupMin >> 20) + Tr.S(" МБ) в ", " MB and up) in ") + scope.Path
                  + Tr.S(" не найдено.", ".") + DiskRescanHint() + DupStoppedHint()
                : Tr.S("Дубликаты (от ", "Duplicates (") + (_dupMin >> 20) + Tr.S(" МБ) в ", " MB and up) in ") + scope.Path
                  + Tr.S(": групп ", ": groups ") + _dupGroups.Count
                  + "  ·  " + Tr.Files(files) + Tr.S("  ·  можно освободить ", "  ·  reclaimable ") + Engine.FormatBytes(waste)
                  + Tr.S("  ·  «Все» оставляет в каждой группе самый старый файл", "  ·  “All” keeps the oldest file of each group")
                  + DupStoppedHint();
            _lblDiskStatus.Text = _diskNote;
        }

        // Поиск остановлен на середине: группы после точки останова не проверены.
        private string DupStoppedHint()
        {
            return _dupCancelled ? Tr.S("  ·  ОСТАНОВЛЕНО — список неполный", "  ·  STOPPED — the list is incomplete") : "";
        }

        // ---------- Действия ----------
        private void SetDiskChecks(bool value)
        {
            _lvDisk.BeginUpdate();
            _suspendDiskChecked = true;
            try
            {
                // В дубликатах «Все» никогда не отмечает всю группу: первый (самый старый)
                // файл остаётся — иначе одним нажатием можно удалить все копии сразу.
                bool keepNext = false;
                foreach (ListViewItem it in _lvDisk.Items)
                {
                    if (ReferenceEquals(it.Tag, NoCheckTag)) { keepNext = value && _diskMode == "dups"; continue; }
                    if (keepNext) { it.Checked = false; keepNext = false; continue; }
                    it.Checked = value;
                }
            }
            finally { _suspendDiskChecked = false; _lvDisk.EndUpdate(); }
            UpdateDiskChecked();
        }

        private void UpdateDiskChecked()
        {
            long size = 0; int n = 0;
            foreach (ListViewItem it in _lvDisk.Items)
            {
                if (it == null || !it.Checked) continue;
                DiskFile f = it.Tag as DiskFile; DiskDir d = it.Tag as DiskDir;
                if (f != null) { size += f.Size; n++; }
                else if (d != null) n++;
            }
            if (n == 0) { _lblDiskStatus.Text = _diskNote; return; }   // снята последняя галочка — вернуть подпись списка
            _lblDiskStatus.Text = Tr.S("Отмечено: ", "Checked: ") + n + (size > 0 ? "  ·  " + Engine.FormatBytes(size) : "")
                + Tr.S("  ·  «В Корзину» переместит их в Корзину (можно восстановить)", "  ·  “Recycle” moves them to the Recycle Bin (restorable)");
        }

        private void OpenDiskRow(ListViewItem it)
        {
            DiskFile f = it.Tag as DiskFile; DiskDir d = it.Tag as DiskDir;
            if (f != null) OpenInExplorer(f.Path, true);
            else if (d != null) OpenInExplorer(d.Path, false);
        }

        private void OpenDiskSelection()
        {
            if (_lvDisk.SelectedItems.Count > 0) { OpenDiskRow(_lvDisk.SelectedItems[0]); return; }
            if (_diskSelected != null) OpenInExplorer(_diskSelected.Path, false);
        }

        private void OpenInExplorer(string path, bool select)
        {
            try
            {
                bool isDir = Native.IsDirectoryPath(path);
                if (select && !isDir && Native.PathExists(path)) Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (isDir) Process.Start("explorer.exe", "\"" + path + "\"");
                else MsgInfo(Tr.S("Путь больше не существует:\r\n", "The path no longer exists:\r\n") + path, Tr.S("Диск", "Disk"));
            }
            catch (Exception ex) { MsgError(ex.Message); }
        }

        private void RecycleDiskSelection()
        {
            string title = Tr.S("Диск", "Disk");
            if (_diskScan == null) return;
            if (_diskScanBusy != 0) { MsgInfo(Tr.S("Дождитесь окончания текущей операции.", "Wait for the current operation to finish."), title); return; }
            List<ListViewItem> picked = new List<ListViewItem>();
            long size = 0;
            foreach (ListViewItem it in _lvDisk.Items)
            {
                if (!it.Checked) continue;
                DiskFile f = it.Tag as DiskFile; DiskDir d = it.Tag as DiskDir;
                if (f == null && d == null) continue;
                if (f != null) size += f.Size;
                picked.Add(it);
            }
            if (picked.Count == 0) { MsgInfo(Tr.S("Отметьте файлы или папки галочками.", "Tick files or folders first."), title); return; }
            bool anyDir = false;
            foreach (ListViewItem it in picked) if (it.Tag is DiskDir) { anyDir = true; break; }
            // На сетевом или съёмном томе Корзины нет: оболочка удалит безвозвратно — говорим это прямо в вопросе
            bool bin = Engine.RecycleBinAvailable(_diskScan.Root);
            string q = (bin ? Tr.S("Переместить в Корзину ", "Move to the Recycle Bin: ")
                            : Tr.S("УДАЛИТЬ БЕЗВОЗВРАТНО ", "DELETE PERMANENTLY: ")) + picked.Count
                     + Tr.S(" элемент(ов)", " item(s)") + (size > 0 ? " (" + Engine.FormatBytes(size) + ")" : "") + "?"
                     + (bin ? "" : Tr.S("\r\n\r\n⚠ На этом томе (сетевой диск или съёмный носитель) Windows не ведёт Корзину: восстановить файлы будет нельзя.",
                                        "\r\n\r\n⚠ This volume (network drive or removable media) has no Recycle Bin: the files cannot be restored."))
                     + (anyDir ? Tr.S("\r\n\r\nПапки, в которых что-то появилось после обхода, будут пропущены — их число покажем в итоге.",
                                      "\r\n\r\nFolders that gained content since the scan will be skipped — the count is reported at the end.") : "");
            if (!MsgAsk(q, title)) return;
            // На время переноса страница занята: второй клик по «В Корзину» или пересканирование
            // поверх идущего SHFileOperation давали «не удалось» на уже перенесённых путях.
            if (Interlocked.CompareExchange(ref _diskScanBusy, 1, 0) != 0) return;
            _recycleCancel = false;
            _btnDiskScan.Enabled = false;
            _btnDiskStop.Enabled = true;
            string op = Tr.S("перемещение в Корзину", "moving to the Recycle Bin");
            BeginWrite(op);
            StartDiskTicker(bin ? Tr.S("Перемещение в Корзину", "Moving to the Recycle Bin")
                                : Tr.S("Удаление безвозвратно", "Deleting permanently"));
            DiskScanResult snap = _diskScan;
            Thread t = new Thread(delegate()
            {
                // Перепроверка «пуста ли ещё папка» тоже здесь: она рекурсивно обходит каждую
                // отмеченную папку (страховка — сто тысяч записей), и на UI-потоке окно
                // замирало ещё ДО того, как пользователь увидит вопрос.
                List<ListViewItem> ok = new List<ListViewItem>();
                List<string> paths = new List<string>();
                int skipped = 0;
                if (anyDir) _diskDetail = Tr.S("проверяю, пусты ли ещё папки…", "re-checking that the folders are still empty…");
                foreach (ListViewItem it in picked)
                {
                    if (_recycleCancel) break;
                    DiskFile f = it.Tag as DiskFile; DiskDir d = it.Tag as DiskDir;
                    if (d != null && !DirStillEmpty(d.Path)) { skipped++; continue; }
                    ok.Add(it);
                    paths.Add(f != null ? f.Path : d.Path);
                }

                // Порциями: оболочка вызывается без своего окна прогресса, и на большом списке
                // одна операция висела молча, без счётчика и без возможности прерваться.
                string message = null;
                int sent = 0;
                for (int i = 0; i < paths.Count && !_recycleCancel; i += RecycleChunk)
                {
                    int n = Math.Min(RecycleChunk, paths.Count - i);
                    string m = null;
                    try { Engine.RecycleToBin(paths.GetRange(i, n), out m); }
                    catch (Exception ex) { m = ex.Message; }
                    if (m != null && message == null) message = m;
                    sent += n;
                    _diskDetail = sent + Tr.S(" из ", " of ") + paths.Count;
                }
                bool cancelled = _recycleCancel;
                EndWrite(op);
                Interlocked.Exchange(ref _diskScanBusy, 0);
                UiPost(delegate
                {
                    StopDiskTicker();
                    _btnDiskScan.Enabled = true;
                    _btnDiskStop.Enabled = false;
                    if (!ReferenceEquals(snap, _diskScan)) return;
                    long freed = 0; int removed = 0;
                    List<DiskDir> removedDirs = new List<DiskDir>();
                    foreach (ListViewItem it in ok)
                    {
                        DiskFile f = it.Tag as DiskFile; DiskDir d = it.Tag as DiskDir;
                        string p = f != null ? f.Path : d.Path;
                        if (Native.PathExists(p)) continue;     // с учётом путей длиннее 260 знаков
                        removed++;
                        if (f != null) { freed += f.Size; Engine.ForgetFile(snap, f); }
                        else { Engine.ForgetDir(snap, d); removedDirs.Add(d); }
                    }
                    if (removed > 0) { _dupGroups = null; _dupScope = null; }
                    PruneDiskTree(removedDirs);
                    RefreshDiskTreeText();
                    _diskListQuiet = true;          // итог переноса в строке важнее подписи списка
                    RefreshDiskList();
                    _drivesAt = DateTime.MinValue; _diskBars.Invalidate();
                    _lblDiskStatus.Text = Tr.S("В Корзину: ", "Recycled: ") + removed + (freed > 0 ? " (" + Engine.FormatBytes(freed) + ")" : "")
                        + (removed < sent ? Tr.S("  ·  не удалось: ", "  ·  failed: ") + (sent - removed)
                                                    + (message != null ? " — " + message : "") : "")
                        + (skipped > 0 ? Tr.S("  ·  пропущено (уже не пусты): ", "  ·  skipped (no longer empty): ") + skipped : "")
                        + (cancelled ? Tr.S("  ·  ОСТАНОВЛЕНО — перенесено не всё отмеченное",
                                            "  ·  STOPPED — not everything ticked was moved") : "");
                });
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
        }

        // Узлы удалённых папок уходят из дерева вместе с поддеревом; родитель, оставшийся без
        // подпапок, теряет заглушку «…» (иначе у него остаётся стрелка раскрытия в никуда).
        // Удаление выбранного узла дерево обычно само переносит выделение на соседа (AfterSelect
        // обновляет _diskSelected); если не перенесло — выбирается ближайший живой предок, иначе
        // список строился бы для папки, которой уже нет.
        private void PruneDiskTree(List<DiskDir> removed)
        {
            if (removed == null || removed.Count == 0 || _tvDisk.Nodes.Count == 0) return;
            _tvDisk.BeginUpdate();
            try
            {
                List<TreeNode> stack = new List<TreeNode>();
                stack.Add(_tvDisk.Nodes[0]);
                while (stack.Count > 0)
                {
                    TreeNode n = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1);
                    DiskDir d = n.Tag as DiskDir;
                    if (d == null) continue;
                    if (IsRemovedDir(d, removed)) { n.Remove(); continue; }
                    if (d.Children == null) { if (n.Nodes.Count > 0) n.Nodes.Clear(); continue; }
                    foreach (TreeNode k in n.Nodes) stack.Add(k);
                }
            }
            finally { _tvDisk.EndUpdate(); }
            if (_diskSelected != null && IsRemovedDir(_diskSelected, removed))
            {
                DiskDir live = _diskSelected.Parent;
                while (live != null && IsRemovedDir(live, removed)) live = live.Parent;
                _diskSelected = live;
                TreeNode ln = FindDiskNode(_tvDisk.Nodes, live);
                if (ln != null) _tvDisk.SelectedNode = ln;
            }
        }

        private static bool IsRemovedDir(DiskDir d, List<DiskDir> removed)
        {
            foreach (DiskDir r in removed) if (Engine.IsUnder(d, r)) return true;
            return false;
        }

        private static TreeNode FindDiskNode(TreeNodeCollection nodes, DiskDir d)
        {
            if (d == null) return null;
            foreach (TreeNode n in nodes)
            {
                if (ReferenceEquals(n.Tag, d)) return n;
                TreeNode k = FindDiskNode(n.Nodes, d);
                if (k != null) return k;
            }
            return null;
        }

        // Через FindFirstFileEx и \\?\: Directory.Exists на пути длиннее 260 знаков отвечает
        // «нет», и такая папка попадала бы в «уже не пуста».
        private static bool DirStillEmpty(string path)
        {
            try { return Native.IsDirectoryPath(path) && !Engine.DirHasContent(path); }
            catch { return false; }
        }

        private void UpdateDiskStatus()
        {
            DiskScanResult r = _diskScan;
            if (r == null) return;
            string s = r.Root + "  —  " + Engine.FormatBytes(r.TotalSize)
                + Tr.S("  ·  файлов: ", "  ·  files: ") + r.TotalFiles.ToString("N0", CultureInfo.CurrentCulture)
                + Tr.S("  ·  папок: ", "  ·  folders: ") + r.TotalDirs.ToString("N0", CultureInfo.CurrentCulture)
                + "  ·  " + (r.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + Tr.S(" с", " s")
                + (r.Errors > 0 ? Tr.S("  ·  недоступно папок: ", "  ·  inaccessible folders: ") + r.Errors : "")
                + (r.Skipped > 0 ? Tr.S("  ·  пропущено ссылок/junction: ", "  ·  links/junctions skipped: ") + r.Skipped : "")
                + (r.Cancelled ? Tr.S("  ·  ОСТАНОВЛЕНО — данные неполные", "  ·  STOPPED — data is incomplete") : "");
            _lblDiskStatus.Text = s;
            _drivesAt = DateTime.MinValue;        // свободное место изменилось — полоски перечитать
            _diskBars.Invalidate();
        }

        // ---------- Полоски дисков ----------
        // Опрос дисков (DriveInfo.GetDrives + IsReady + TotalFreeSpace) стоял прямо в Paint:
        // на уснувшем или заблокированном фиксированном диске он блокирует перерисовку, то есть
        // весь UI-поток. Теперь Paint рисует только готовый снимок, а устаревший обновляется
        // в фоне — до его прихода на экране остаётся прежний.
        // Список областей поиска берёт тот же снимок, что и полоски: повторный опрос дисков
        // в обработчике — ещё одна возможная остановка UI на неотвечающем томе. Первый раз
        // (окно ещё строится) снимок всё же читается синхронно: без него нечего показать.
        private List<DriveRow> DrivesCached()
        {
            List<DriveRow> d = _drives;
            if (d != null) return d;
            d = Engine.Drives();
            _drives = d;
            _drivesAt = DateTime.UtcNow;
            return d;
        }

        private void EnsureDrives()
        {
            if ((DateTime.UtcNow - _drivesAt).TotalSeconds <= 10) return;
            if (Interlocked.CompareExchange(ref _drivesBusy, 1, 0) != 0) return;
            Thread t = new Thread(delegate()
            {
                List<DriveRow> rows = null;
                try { rows = Engine.Drives(); }
                catch { }
                if (rows != null) _drives = rows;
                Interlocked.Exchange(ref _drivesBusy, 0);
                UiPost(delegate { _drivesAt = DateTime.UtcNow; _diskBars.Invalidate(); });
            });
            t.IsBackground = true;
            t.Start();
        }

        private void DiskBars_Paint(object sender, PaintEventArgs e)
        {
            EnsureDrives();
            List<DriveRow> drives = _drives;
            if (drives == null) return;            // первый снимок ещё читается — рисовать нечего
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = 0, y = 6;
            int barW = 150, barH = 12;
            Font small = new Font(Font.FontFamily, 9F);
            Font bold = new Font(Font, FontStyle.Bold);
            using (small) using (bold)
            using (SolidBrush track = new SolidBrush(_theme.Dark ? ControlPaint.Light(_theme.Bg, 0.25f) : ControlPaint.Dark(_theme.Bg, 0.08f)))
            using (SolidBrush fill = new SolidBrush(_theme.Accent))
            using (SolidBrush warn = new SolidBrush(Color.FromArgb(220, 76, 60)))
            using (SolidBrush text = new SolidBrush(_theme.Text))
            using (SolidBrush subtle = new SolidBrush(_theme.Subtle))
            {
                foreach (DriveRow d in drives)
                {
                    string name = d.Name.TrimEnd('\\') + (string.IsNullOrEmpty(d.Label) ? "" : " " + d.Label);
                    SizeF ns = g.MeasureString(name, bold);
                    g.DrawString(name, bold, text, x, y);
                    int bx = x + (int)ns.Width + 8;
                    Rectangle tr = new Rectangle(bx, y + 4, barW, barH);
                    using (GraphicsPath p = RoundedRect(tr, 6)) g.FillPath(track, p);
                    int used = (int)Math.Round(barW * Math.Min(1.0, d.UsedFraction));
                    if (used > 0)
                    {
                        Rectangle ur = new Rectangle(bx, y + 4, Math.Max(used, 6), barH);
                        using (GraphicsPath p = RoundedRect(ur, 6)) g.FillPath(d.UsedFraction >= 0.9 ? warn : fill, p);
                    }
                    string info = Engine.FormatBytes(d.Used) + Tr.S(" из ", " of ") + Engine.FormatBytes(d.Total)
                                + "  (" + Tr.S("свободно ", "free ") + Engine.FormatBytes(d.Free) + ")";
                    g.DrawString(info, small, subtle, bx, y + barH + 6);
                    SizeF isz = g.MeasureString(info, small);
                    x = bx + Math.Max(barW, (int)isz.Width) + 28;
                    if (x > _diskBars.Width - 200) break;
                }
            }
        }
    }
}
