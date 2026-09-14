// Windows Process Cleaner — «Загрузки», торренты без диалога: скачанный .torrent и папка наблюдения.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Готовая http-загрузка с именем *.torrent (ссылка из браузера или добавленная вручную) становится торрентом с настройками по
// умолчанию; http-запись остаётся в истории с отметкой в журнале. Папка наблюдения просматривается раз в несколько секунд,
// только верхний уровень и только *.torrent: файл, который ещё пишется (изменён только что), ждёт следующего прохода. Что уже
// обработано — путь, размер и время изменения — запоминается в torrents\watch-seen.txt, чтобы убранный из списка торрент
// не возвращался после перезапуска. Удаление .torrent после добавления — только в Корзину и только по настройке.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed partial class DlEngine
    {
        private readonly List<string> _btChain = new List<string>();                  // id готовых http-загрузок .torrent
        private readonly object _btIntakeGate = new object();
        private Dictionary<string, string> _btWatchSeen;                               // путь → «размер|время изменения»
        private DateTime _btWatchAt = DateTime.MinValue;
        internal int BtWatchSeconds = 5;
        internal int BtWatchSettleSeconds = 2;
        private const int BtWatchSeenMax = 2000;

        private static bool IsTorrentName(string name)
        {
            return !string.IsNullOrEmpty(name) && name.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);
        }

        // Из Finished под _lock: сам торрент добавляется потом, вне замка (AddTorrent пишет файл и берёт замок сам).
        private void BtChainLocked(DlItem it)
        {
            if (!it.IsTorrent && it.State == DlState.Completed && IsTorrentName(it.FileName) && !_btChain.Contains(it.Id)) _btChain.Add(it.Id);
        }

        // Шаг фонового цикла; вызывается и тестами напрямую.
        internal void BtIntake()
        {
            lock (_btIntakeGate)
            {
                BtIntakeChain();
                BtIntakeWatch();
            }
        }

        private void BtIntakeChain()
        {
            List<string> ids;
            lock (_lock)
            {
                if (_btChain.Count == 0) return;
                ids = new List<string>(_btChain);
                _btChain.Clear();
            }
            foreach (string id in ids)
            {
                DlItem it;
                string path;
                DlSettings s;
                lock (_lock)
                {
                    it = FindLocked(id);
                    if (it == null || it.State != DlState.Completed || it.MoveTo.Length > 0) continue;
                    path = DlFiles.PathInside(it.Folder ?? "", it.FileName);
                    s = _settings;
                }
                if (path == null) continue;
                string name, error;
                bool duplicate;
                if (!BtAddFromFile(path, it.Source, out name, out duplicate, out error))
                {
                    // Не торрент (страница входа с именем .torrent) — обычный файл, молча.
                    if (error != null) Journal(it, Tr.S("файл .torrent не добавлен торрентом: ", "the .torrent file was not added as a torrent: ") + error);
                    continue;
                }
                Journal(it, duplicate ? Tr.S("этот торрент уже в списке", "this torrent is already in the list")
                                      : Tr.S("добавлен торрент: ", "torrent added: ") + name);
                if (s.BtRecycleTorrentFile)
                {
                    string why = DlFiles.Recycle(path);
                    if (why != null) Journal(it, Tr.S("файл .torrent не убран в Корзину: ", "the .torrent file was not moved to the Recycle Bin: ") + why);
                    else Remove(id, false, out why);
                }
                lock (_lock) { if (FindLocked(id) != null) _store.Save(it); }
            }
        }

        private void BtIntakeWatch()
        {
            DlSettings s;
            lock (_lock) s = _settings;
            DateTime now = _env.UtcNow;
            if (s.BtWatchFolder.Length == 0) return;
            if (now >= _btWatchAt && (now - _btWatchAt).TotalSeconds < BtWatchSeconds) return;
            _btWatchAt = now;
            string why;
            string dir = DlFiles.CheckFolder(s.BtWatchFolder, out why);
            if (dir == null || !Directory.Exists(dir) || DlFiles.IsReparse(dir)) return;
            if (_btWatchSeen == null) _btWatchSeen = BtLoadWatchSeen();

            string[] files;
            try { files = Directory.GetFiles(dir, "*.torrent", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) { DlLog.Report(ex); return; }
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            bool changed = false;
            foreach (string file in files)
            {
                // GetFiles с маской *.torrent находит и «x.torrent_old» (короткие имена 8.3) — проверяется точное окончание.
                if (!IsTorrentName(file)) continue;
                FileInfo fi;
                try
                {
                    fi = new FileInfo(file);
                    if (!fi.Exists || DlFiles.IsReparse(file)) continue;
                }
                catch { continue; }
                DateTime written = fi.LastWriteTimeUtc;
                if ((DateTime.UtcNow - written).TotalSeconds < BtWatchSettleSeconds) continue;
                string stamp = fi.Length.ToString(CultureInfo.InvariantCulture) + "|" + written.Ticks.ToString(CultureInfo.InvariantCulture);
                string key = file.ToUpperInvariant();
                string seen;
                if (_btWatchSeen.TryGetValue(key, out seen) && seen == stamp) continue;

                string name, error;
                bool duplicate;
                bool listed = BtAddFromFile(file, "watch", out name, out duplicate, out error);
                _btWatchSeen[key] = stamp;
                changed = true;
                if (!listed)
                {
                    DlLog.Write("watch folder: not added: " + Path.GetFileName(file) + (error != null ? " — " + error : ""));
                    continue;
                }
                if (s.BtRecycleTorrentFile)
                {
                    string recycled = DlFiles.Recycle(file);
                    if (recycled != null) DlLog.Write("watch folder: not recycled: " + Path.GetFileName(file) + " — " + recycled);
                    else _btWatchSeen.Remove(key);
                }
            }
            if (changed) BtSaveWatchSeen();
        }

        // true — торрент в списке: добавлен сейчас (duplicate = false) или уже был. error null при false — файл не торрент.
        private bool BtAddFromFile(string path, string source, out string name, out bool duplicate, out string error)
        {
            name = null;
            duplicate = false;
            error = null;
            byte[] bytes;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || DlFiles.IsReparse(path)) return false;
                if (fi.Length > BtMaxTorrentBytes) { error = Tr.S("файл .torrent больше 16 МБ", "the .torrent file is larger than 16 MB"); return false; }
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex) { error = ex.Message; return false; }
            string parseError;
            BtMeta meta = BtMeta.Parse(bytes, out parseError);
            if (meta == null) return false;
            DlTorrentRequest r = new DlTorrentRequest();
            r.TorrentBytes = bytes;
            r.Source = string.IsNullOrEmpty(source) ? "manual" : source;
            r.OnTopicMatch = TopicMatchUpdate;
            string duplicateOf;
            string id = AddTorrent(r, out duplicateOf, out error);
            name = meta.Name;
            if (id != null) return true;
            if (r.UpdateOf != null && error == null) { duplicate = true; return true; }
            if (duplicateOf != null) { duplicate = true; error = null; return true; }
            return false;
        }

        private string WatchSeenFile { get { return Path.Combine(TorrentsDir, "watch-seen.txt"); } }

        private Dictionary<string, string> BtLoadWatchSeen()
        {
            Dictionary<string, string> seen = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                if (!File.Exists(WatchSeenFile)) return seen;
                foreach (string line in File.ReadAllLines(WatchSeenFile, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab > 0) seen[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch (Exception ex) { DlLog.Report(ex); }
            return seen;
        }

        // Только файлы, которые ещё лежат в папке: удалённые пользователем записи не копятся.
        private void BtSaveWatchSeen()
        {
            try
            {
                List<string> lines = new List<string>();
                foreach (KeyValuePair<string, string> kv in _btWatchSeen)
                    if (File.Exists(kv.Key)) lines.Add(kv.Key + "\t" + kv.Value);
                if (lines.Count > BtWatchSeenMax) lines.RemoveRange(0, lines.Count - BtWatchSeenMax);
                Directory.CreateDirectory(TorrentsDir);
                string tmp = WatchSeenFile + ".tmp";
                File.WriteAllLines(tmp, lines.ToArray(), new UTF8Encoding(false));
                if (File.Exists(WatchSeenFile)) File.Replace(tmp, WatchSeenFile, null);
                else File.Move(tmp, WatchSeenFile);
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }
    }
}
