// Windows Process Cleaner — «Загрузки»: перенос и копирование скачанного в другую папку, очистка истории, уведомления.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Тот же том — переименование: мгновенно, метка MOTW остаётся на файле. Другой том или копия — поток во временный
// «<имя>.wpcmove» рядом с целью, сверка размера и SHA-256, переименование; исходник при переносе уходит только в Корзину.
// Прерванный перенос (процесс закрыли) продолжается при следующем запуске с уже скопированного места: хеш в конце
// ловит испорченный хвост, и тогда копия делается заново с нуля.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal enum DlNoticeKind { Completed, Failed, NeedsLink, Intercepted, UpdateAvailable }

    // Что показать в уведомлении. Строки скопированы из записи: обработчик живёт в другом потоке.
    internal sealed class DlNotice
    {
        public DlNoticeKind Kind;
        public string Id = "";
        public string Name = "";
        public string Path = "";
        public string Text = "";
        public string Source = "";
        // Кнопки уведомления «перехвачено» (задаёт процесс загрузок): открыть окно, вернуть загрузку браузеру.
        public Action OpenApp;
        public Action GiveBack;
    }

    internal sealed partial class DlEngine
    {
        public const string MoveSuffix = ".wpcmove";

        // Возвращается из копирования вместо причины: процесс закрывается (перенос продолжится) или пользователь отменил.
        private static readonly string StoppedByDispose = new string('d', 1);
        private static readonly string StoppedByUser = new string('u', 1);

        private readonly Dictionary<string, Thread> _moves = new Dictionary<string, Thread>();
        private readonly HashSet<string> _moveCancel = new HashSet<string>();

        // Вызывается под замком движка: обработчик только ставит уведомление в очередь своего потока.
        public Action<DlNotice> Notice;

        private static string MovingWhy()
        {
            return Tr.S("файл загрузки сейчас переносится — дождитесь конца или отмените перенос", "the download's file is being moved — wait for it to finish or cancel the move");
        }

        // ---------- перенос и копирование ----------

        // copy = false — перенести (запись смотрит на новое место), true — положить копию, запись не меняется.
        public bool Relocate(string id, string folder, bool copy, out string why)
        {
            string dest = DlFiles.CheckFolder(folder, out why);
            if (dest == null) return false;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                // Файлы торрента привязаны к снимку для продолжения и раздаче: перенос сломал бы и то и другое.
                if (it.IsTorrent) { why = Tr.S("перенос торрента пока не поддерживается", "moving a torrent is not supported yet"); return false; }
                if (it.MoveTo.Length > 0) { why = MovingWhy(); return false; }
                if (_active.ContainsKey(id)) { why = Tr.S("загрузка идёт — сначала поставьте её на паузу", "the download is running — pause it first"); return false; }
                bool complete = it.State == DlState.Completed;
                if (copy && !complete) { why = Tr.S("копировать можно только завершённую загрузку", "only a finished download can be copied"); return false; }
                if (!complete && it.State != DlState.Paused && it.State != DlState.Failed && it.State != DlState.NeedsLink)
                {
                    why = Tr.S("сначала поставьте загрузку на паузу", "pause the download first");
                    return false;
                }
                string current = string.IsNullOrEmpty(it.Folder) ? "" : (Native.CanonicalPath(it.Folder) ?? it.Folder);
                if (current.Length > 0 && string.Equals(current.TrimEnd('\\'), dest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    why = Tr.S("файл уже в этой папке", "the file is already in this folder");
                    return false;
                }

                // Файла ещё нет (загрузка не начиналась) — меняется только папка.
                string source = SourceOf(it, complete);
                if (!complete && (source == null || !DlFiles.Exists(source)))
                {
                    it.Folder = dest;
                    ReleaseReservation(it.Id);
                    Journal(it, Tr.S("папка изменена: ", "folder changed: ") + dest);
                    _store.Save(it);
                    return true;
                }
                if (source == null || !DlFiles.Exists(source)) { why = Tr.S("файла загрузки нет на месте", "the downloaded file is gone"); return false; }
                if (DlFiles.IsReparse(source)) { why = Tr.S("на месте файла ссылка — не трогаю", "a link sits where the file was — left alone"); return false; }

                it.MoveTo = dest;
                it.MoveCopy = copy;
                it.MoveName = "";
                it.MoveError = "";
                Interlocked.Exchange(ref it.MoveDone, 0);
                Journal(it, (copy ? Tr.S("копирование в ", "copying to ") : Tr.S("перенос в ", "moving to ")) + dest);
                _store.Save(it);
                StartMoveLocked(it);
            }
            return true;
        }

        public bool CancelRelocate(string id)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || it.MoveTo.Length == 0) return false;
                _moveCancel.Add(id);
                return true;
            }
        }

        private static string SourceOf(DlItem it, bool complete)
        {
            if (string.IsNullOrEmpty(it.FileName) || string.IsNullOrEmpty(it.Folder)) return null;
            string target = DlFiles.PathInside(it.Folder, it.FileName);
            if (target == null) return null;
            return complete ? target : target + DlPaths.PartSuffix;
        }

        private void ResumeMoves()
        {
            lock (_lock)
                foreach (DlItem it in _items)
                    if (it.MoveTo.Length > 0) StartMoveLocked(it);
        }

        private void StartMoveLocked(DlItem it)
        {
            if (_disposed || _moves.ContainsKey(it.Id)) return;
            DlItem item = it;
            Thread t = new Thread(delegate() { RunMove(item); });
            t.IsBackground = true;
            t.Name = "wpc-dl-move";
            _moves[it.Id] = t;
            t.Start();
        }

        private void JoinMoves(int timeoutMs)
        {
            List<Thread> threads;
            lock (_lock) threads = new List<Thread>(_moves.Values);
            foreach (Thread t in threads) t.Join(timeoutMs);
        }

        private int MoveStop(string id)
        {
            if (_disposed) return 1;
            lock (_lock) return _moveCancel.Contains(id) ? 2 : 0;
        }

        private void RunMove(DlItem it)
        {
            string source, dest, name, error;
            string note = null;
            bool copy, complete;
            lock (_lock)
            {
                complete = it.State == DlState.Completed;
                copy = it.MoveCopy;
                dest = it.MoveTo;
                source = SourceOf(it, complete);
                name = it.MoveName;
                if (name.Length == 0 && source != null)
                {
                    name = FreeMoveName(it, dest, complete);
                    if (name != null)
                    {
                        it.MoveName = name;
                        if (!complete) _reserved[Path.Combine(dest, name)] = it.Id;
                        _store.Save(it);
                    }
                }
            }

            if (source == null) error = Tr.S("файла загрузки нет на месте", "the downloaded file is gone");
            else if (name == null) error = Tr.S("в папке назначения не нашлось свободного имени", "no free name in the destination folder");
            else
            {
                string target = Path.Combine(dest, name) + (complete ? "" : DlPaths.PartSuffix);
                try { error = MoveFile(it, source, target, copy, complete, out note); }
                catch (Exception ex)
                {
                    DlLog.Report(ex);
                    error = ex.Message;
                }
            }

            lock (_lock)
            {
                _moves.Remove(it.Id);
                bool byUser = _moveCancel.Remove(it.Id);
                // Процесс закрывается: состояние переноса сохранено, следующий запуск продолжит.
                if (ReferenceEquals(error, StoppedByDispose) && !byUser) { _store.Save(it); return; }
                if (error == null)
                {
                    if (!copy)
                    {
                        it.Folder = dest;
                        it.FileName = name;
                    }
                    Journal(it, (copy ? Tr.S("скопировано: ", "copied: ") + Path.Combine(dest, name) : Tr.S("перенесено: ", "moved: ") + it.TargetPath)
                                + (note == null ? "" : " · " + note));
                }
                else if (ReferenceEquals(error, StoppedByUser) || ReferenceEquals(error, StoppedByDispose))
                {
                    Journal(it, Tr.S("перенос отменён", "move cancelled"));
                }
                else
                {
                    it.MoveError = error;
                    Journal(it, Tr.S("перенос не удался: ", "move failed: ") + error);
                }
                it.MoveTo = "";
                it.MoveName = "";
                it.MoveCopy = false;
                Interlocked.Exchange(ref it.MoveDone, 0);
                ReleaseReservation(it.Id);
                if (it.State != DlState.Completed && !string.IsNullOrEmpty(it.FileName)) _reserved[it.TargetPath] = it.Id;
                _store.Save(it);
            }
        }

        // Имя, не занятое ни файлом, ни частичным файлом, ни чужим временным файлом переноса, ни другой загрузкой.
        private string FreeMoveName(DlItem it, string dest, bool complete)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in _reserved)
                if (kv.Value != it.Id) taken.Add(kv.Key.ToLowerInvariant());
            for (int guard = 0; guard < 64; guard++)
            {
                string name = DlFiles.UniqueName(dest, it.FileName, taken);
                if (name == null) return null;
                string full = Path.Combine(dest, name) + (complete ? "" : DlPaths.PartSuffix);
                if (!DlFiles.Exists(full + MoveSuffix)) return name;
                taken.Add(Path.Combine(dest, name).ToLowerInvariant());
            }
            return null;
        }

        // null — готово; иначе причина или один из маркеров остановки. note — что сказать вдобавок к успеху.
        private string MoveFile(DlItem it, string source, string target, bool copy, bool complete, out string note)
        {
            note = null;
            string tmp = target + MoveSuffix;
            if (DlFiles.Exists(target)) return Tr.S("на месте цели уже лежит файл: ", "a file already sits at the target: ") + target;
            if (!copy && !DlFiles.Exists(tmp) && SameVolume(source, target))
            {
                try
                {
                    File.Move(source, target);
                    return null;
                }
                catch (IOException ex)
                {
                    // Точка монтирования под той же буквой — это другой том: переименование невозможно, копируем.
                    DlLog.Write(it.Id + " rename failed, copying instead: " + ex.Message);
                }
            }

            string expect = complete ? (it.Sha256 ?? "") : "";
            for (int attempt = 0; attempt < 2; attempt++)
            {
                // Продолжать с места можно, только если в конце есть чем сверить: у частичного файла хеша нет.
                bool resume = attempt == 0 && expect.Length == 64 && DlFiles.Exists(tmp);
                string err = CopyStream(it, source, tmp, resume);
                if (err != null) return err;
                if (new FileInfo(tmp).Length != new FileInfo(source).Length)
                {
                    TryDelete(tmp);
                    return Tr.S("размер копии не совпал с исходником", "the copy's size does not match the source");
                }
                if (expect.Length != 64) break;
                string id = it.Id;
                string got = DlFinish.Hash(tmp, "sha256", delegate { return MoveStop(id) != 0; });
                int stop = MoveStop(id);
                if (stop == 1) return StoppedByDispose;
                if (stop == 2) { TryDelete(tmp); return StoppedByUser; }
                if (got == null) return Tr.S("не удалось прочитать копию для сверки", "could not read the copy to verify it");
                if (string.Equals(got, expect, StringComparison.OrdinalIgnoreCase)) break;
                TryDelete(tmp);
                DlLog.Write(it.Id + " copy hash mismatch, attempt " + (attempt + 1));
                if (attempt == 1) return Tr.S("копия не совпала с исходником по SHA-256", "the copy does not match the source by SHA-256");
            }

            // Поток копируется без альтернативных потоков NTFS: метку «из интернета» ставим заново, если она была.
            if (complete && DlMotw.ReadZoneStream(source) != null)
            {
                string why = DlMotw.WriteZoneStream(tmp, string.IsNullOrEmpty(it.FinalUrl) ? it.Url : it.FinalUrl, it.Referrer);
                if (why != null) DlLog.Write(it.Id + " zone stream on copy: " + why);
            }
            if (DlFiles.Exists(target))
            {
                TryDelete(tmp);
                return Tr.S("на месте цели уже лежит файл: ", "a file already sits at the target: ") + target;
            }
            File.Move(tmp, target);
            if (!copy)
            {
                string why = DlFiles.Recycle(source);
                if (why != null) note = Tr.S("исходный файл остался на месте: ", "the source file stayed in place: ") + why;
            }
            return null;
        }

        private string CopyStream(DlItem it, string source, string tmp, bool resume)
        {
            const int Chunk = 1024 * 1024;
            const long FlushEvery = 64L * 1024 * 1024;
            byte[] buf = new byte[Chunk];
            using (FileStream input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, Chunk, FileOptions.SequentialScan))
            using (FileStream output = new FileStream(tmp, resume ? FileMode.OpenOrCreate : FileMode.Create, FileAccess.Write, FileShare.None, Chunk))
            {
                // Последний мегабайт перед обрывом мог не дойти до диска — переписываем его.
                long start = resume ? Math.Max(0, Math.Min(output.Length, input.Length) - Chunk) : 0;
                long free = DlFiles.FreeSpace(Path.GetDirectoryName(tmp));
                if (free >= 0 && free < input.Length - start) return Tr.S("на диске назначения не хватает места", "not enough space on the destination disk");
                output.SetLength(start);
                output.Position = start;
                input.Position = start;
                Interlocked.Exchange(ref it.MoveDone, start);
                long flushed = start;
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    int stop = MoveStop(it.Id);
                    if (stop == 1) { output.Flush(true); return StoppedByDispose; }
                    if (stop == 2) break;
                    output.Write(buf, 0, n);
                    long done = Interlocked.Add(ref it.MoveDone, n);
                    if (done - flushed >= FlushEvery) { output.Flush(true); flushed = done; }
                }
                output.Flush(true);
            }
            if (MoveStop(it.Id) == 2)
            {
                TryDelete(tmp);
                return StoppedByUser;
            }
            return null;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path) && !DlFiles.IsReparse(path)) File.Delete(path); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        internal static bool SameVolume(string a, string b)
        {
            try
            {
                string ra = Path.GetPathRoot(Path.GetFullPath(a)), rb = Path.GetPathRoot(Path.GetFullPath(b));
                return !string.IsNullOrEmpty(ra) && string.Equals(ra, rb, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ---------- история ----------

        // Только записи — файлы остаются на месте. days = 0 — все завершённые; missingOnly — только те, чьего файла уже нет.
        public int ClearHistory(int days, bool missingOnly)
        {
            DateTime border = _env.UtcNow.AddDays(-Math.Max(0, days));
            int removed = 0;
            lock (_lock)
            {
                foreach (DlItem it in new List<DlItem>(_items))
                {
                    if (it.State != DlState.Completed || it.MoveTo.Length > 0 || _btRun.ContainsKey(it.Id) || _btStarting.Contains(it.Id)) continue;
                    if (days > 0 && it.CompletedUtc > border) continue;
                    if (missingOnly && DlFiles.Exists(it.TargetPath)) continue;
                    _items.Remove(it);
                    ReleaseReservation(it.Id);
                    _mirrorsTried.Remove(it.Id);
                    _store.Delete(it.Id);
                    if (it.IsTorrent) BtDeleteSideFiles(it.InfoHash);
                    removed++;
                }
                if (removed > 0) SaveOrder();
            }
            return removed;
        }

        // ---------- работа процесса ----------

        // Есть что делать: очередь, отложенный старт, ожидание условия, загрузка или перенос. Иначе процесс может уйти.
        public bool HasPendingWork
        {
            get
            {
                lock (_lock)
                {
                    if (_active.Count > 0 || _moves.Count > 0 || _btRun.Count > 0 || _btStarting.Count > 0 || _btProbes.Count > 0
                        || _btUpdThread != null || _btUpdBusy.Count > 0) return true;
                    foreach (DlItem it in _items)
                        if (it.State == DlState.Queued || it.State == DlState.Scheduled || it.State == DlState.Waiting
                            || it.State == DlState.Active || it.State == DlState.Checking || it.State == DlState.Seeding
                            || it.MoveTo.Length > 0) return true;
                    return false;
                }
            }
        }

        private void RaiseNotice(DlItem it)
        {
            Action<DlNotice> handler = Notice;
            if (handler == null) return;
            DlNotice n = new DlNotice();
            // Торрент сообщает «готово», когда скачано всё выбранное, — раздача после этого может идти ещё долго.
            bool done = it.State == DlState.Completed || (it.IsTorrent && it.State != DlState.Failed && it.CompletedUtc != DateTime.MinValue);
            n.Kind = done ? DlNoticeKind.Completed : it.State == DlState.NeedsLink ? DlNoticeKind.NeedsLink : DlNoticeKind.Failed;
            n.Id = it.Id;
            n.Name = string.IsNullOrEmpty(it.FileName) ? (it.IsTorrent ? it.InfoHash : DlLog.Redact(it.Url)) : it.FileName;
            n.Path = done ? it.TargetPath : "";
            n.Text = it.Error ?? "";
            n.Source = it.Source ?? "";
            try { handler(n); }
            catch (Exception ex) { DlLog.Report(ex); }
        }
    }

    // Процесс загрузок живёт по требованию: окно запускает его первым действием, а сам он уходит, когда минуту нечего
    // делать и никто не спрашивает (страница «Загрузки» опрашивает его, пока открыта).
    internal sealed class DlIdleExit
    {
        public const int DefaultSeconds = 60;

        private readonly int _seconds;
        private DateTime _busyUtc;

        public DlIdleExit(DateTime startUtc, int seconds)
        {
            _busyUtc = startUtc;
            _seconds = seconds;
        }

        public void Touch(DateTime utc)
        {
            if (utc > _busyUtc) _busyUtc = utc;
        }

        // busy — незавершённая работа или открытое уведомление. Часы, ушедшие назад, отсчёт не ускоряют.
        public bool ShouldExit(DateTime utc, bool busy)
        {
            if (busy) { Touch(utc); return false; }
            if (utc < _busyUtc) { _busyUtc = utc; return false; }
            return (utc - _busyUtc).TotalSeconds >= _seconds;
        }
    }
}
