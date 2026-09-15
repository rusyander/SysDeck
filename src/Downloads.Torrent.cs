// SysDeck — «Загрузки»: торренты в очереди движка — добавление, запуск в сессии, состояние, остановка, удаление.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Одна BtSession на процесс --downloads: открывается, когда торренту пора стартовать, и закрывается через 2 минуты без
// торрентов. Торрент находится в сессии, только пока запись Active/Checking/Seeding. Пауза, условие очереди (расписание,
// батарея, простой ПК) и предел раздачи выводят его из сессии со снимком для продолжения: файлы освобождаются, следующий
// старт проверяет хешем только изменившиеся файлы. Рядом с записями — torrents\<hash>.torrent (файл торрента или словарь
// info, полученный по magnet) и <hash>.resume.json. Снимок, закрытие файлов и запуск сессии ждут диск и сокеты, поэтому их
// делает поток планировщика вне _lock (BtBackground); Tick под _lock только решает, что остановить и что запустить.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed class DlTorrentRequest
    {
        public byte[] TorrentBytes;              // содержимое .torrent
        public string Magnet = "";               // или magnet-ссылка
        public string Folder = "";               // пусто — папка из настроек
        public string RootName = "";             // имя папки (файла) раздачи из диалога; пусто — из метаданных
        public int[] Priorities;                 // по файлам; null — все обычные
        public bool Sequential;
        public bool StartPaused;
        public bool UseExisting;                 // данные уже лежат под этим именем: проверить их, докачать и раздавать
        public string Source = "manual";
        public int Priority;
        public string OnTopicMatch = DlEngine.TopicMatchAsk;   // .torrent новой версии раздачи из списка: спросить, обновить, добавить
        public string UpdateOf;                  // ответ: запись, новой версией которой оказался торрент (null — не оказался)
    }

    internal sealed partial class DlEngine
    {
        public const int BtMaxTorrentBytes = 16 * 1024 * 1024;

        private readonly Dictionary<string, BtTorrent> _btRun = new Dictionary<string, BtTorrent>();
        private readonly HashSet<string> _btStarting = new HashSet<string>();         // выбраны к запуску, ещё не в сессии
        private readonly List<string> _btStart = new List<string>();
        private readonly Dictionary<string, DlState> _btStop = new Dictionary<string, DlState>();   // id → состояние после выхода
        private readonly object _btOps = new object();                              // BtBackground и закрытие движка
        private readonly DlTokenBucket _btUp = new DlTokenBucket(0);
        private BtSession _bt;
        private IBtPortMapper _btMapper;
        private string _btKey = "";
        private int _btInboundState = -1;
        private int _btAutoPort;
        private DateTime _btIdleSince = DateTime.MinValue, _btResumeAt = DateTime.MinValue, _btRetryAt = DateTime.MinValue;

        // Для тестов: сессия на петле, входящие без проверки брандмауэра, короткие интервалы.
        internal IPAddress BtBind = IPAddress.Any;
        internal Func<bool> BtInboundReady = null;
        internal int BtIdleCloseSeconds = 120;
        internal int BtResumeSaveSeconds = 60;

        // Порт выбран движком (в настройках был 0) — настройки с ним надо сохранить. Задаёт процесс --downloads.
        public Action<DlSettings> SettingsPersist;

        public string TorrentsDir { get { return Path.Combine(_store.Dir, "torrents"); } }
        internal BtSession TorrentSession { get { lock (_lock) return _bt; } }

        private bool IsTorrentId(string id)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                return it != null && it.IsTorrent;
            }
        }

        // ---------- добавление ----------
        public string AddTorrent(DlTorrentRequest r, out string duplicateOf, out string error)
        {
            duplicateOf = null;
            error = null;
            if (r == null) { error = Tr.S("пустой запрос", "an empty request"); return null; }
            BtMeta meta = null;
            BtMagnet magnet = null;
            string err;
            if (r.TorrentBytes != null)
            {
                if (r.TorrentBytes.Length > BtMaxTorrentBytes) { error = Tr.S("файл .torrent больше 16 МБ", "the .torrent file is larger than 16 MB"); return null; }
                meta = BtMeta.Parse(r.TorrentBytes, out err);
                if (meta == null) { error = Tr.S("файл .torrent не разобран: ", "the .torrent file is not recognised: ") + err; return null; }
            }
            else
            {
                magnet = BtMagnet.Parse((r.Magnet ?? "").Trim(), out err);
                if (magnet == null) { error = Tr.S("magnet-ссылка не разобрана: ", "the magnet link is not recognised: ") + err; return null; }
            }
            string hash = meta != null ? meta.HexHash : Bencode.Hex(magnet.SwarmHash);
            if (!DlItem.IsHexHash(hash)) { error = Tr.S("нет info-hash", "no info-hash"); return null; }
            if (meta != null && r.Priorities != null && r.Priorities.Length != meta.Files.Count)
            {
                error = Tr.S("выбор файлов не совпадает с торрентом", "the file selection does not match the torrent");
                return null;
            }
            string why;
            string folder = DlFiles.CheckFolder(string.IsNullOrEmpty(r.Folder) ? Settings.EffectiveFolder : r.Folder, out why);
            if (folder == null) { error = why; return null; }
            string rootWanted = null;
            if (meta != null)
            {
                rootWanted = DlFiles.SanitizeName(string.IsNullOrEmpty(r.RootName) ? (meta.RootDir.Length > 0 ? meta.RootDir : meta.Name) : r.RootName);
                if (string.IsNullOrEmpty(rootWanted)) rootWanted = hash;
            }

            string topic = BtTopic.FromMeta(meta);
            lock (_lock)
            {
                duplicateOf = BtDuplicateLocked(hash);
                if (duplicateOf != null) { error = BtDuplicateText(); return null; }
                if (topic.Length > 0 && r.OnTopicMatch != TopicMatchAdd) r.UpdateOf = BtTopicMatchLocked(topic, hash);
            }
            if (r.UpdateOf != null)
            {
                // Та же тема, другой хеш — новая версия раздачи, которая уже в списке: не вторая запись, а обновление.
                if (r.OnTopicMatch != TopicMatchUpdate) { error = Tr.S("это новая версия раздачи, которая уже есть в списке", "this is a new version of a torrent that is already listed"); return null; }
                error = OfferUpdateFile(r.UpdateOf, r.TorrentBytes, r.Source != "manual");
                return null;
            }
            // Метаданные — до записи: запись без файла торрента пришлось бы запускать как magnet без ссылки.
            try
            {
                if (meta != null) BtWriteBytes(Path.Combine(TorrentsDir, hash + ".torrent"), r.TorrentBytes);
                // Снимок от прежней записи с этим хешем (убрана из списка) указывал бы на её папку и её куски.
                BtDeleteResume(hash);
            }
            catch (Exception ex) { error = ex.Message; return null; }

            DateTime now = _env.UtcNow;
            DlItem it = new DlItem();
            it.Id = DlItem.NewId();
            it.Kind = DlItem.KindTorrent;
            it.InfoHash = hash;
            it.Url = it.OriginalUrl = magnet != null ? r.Magnet.Trim() : "";
            it.Source = string.IsNullOrEmpty(r.Source) ? "manual" : r.Source;
            it.Folder = folder;
            it.Priority = DlJson.Clamp(r.Priority, -1, 1);
            it.Sequential = r.Sequential;
            it.FilePriorities = ClampPriorities(r.Priorities);
            if (meta != null) it.Total = meta.TotalSize;
            it.TopicUrl = topic;
            it.AddedUtc = now;
            it.State = r.StartPaused ? DlState.Paused : DlState.Queued;
            lock (_lock)
            {
                duplicateOf = BtDuplicateLocked(hash);
                if (duplicateOf != null) { error = BtDuplicateText(); return null; }
                if (rootWanted != null)
                {
                    string root;
                    if (r.UseExisting)
                    {
                        // Данные пользователя под этим именем: торрент проверит их хешем. Чужая незавершённая загрузка — нет.
                        if (_reserved.ContainsKey(Path.Combine(folder, rootWanted)))
                        {
                            error = Tr.S("под этим именем уже идёт другая загрузка", "another download already uses this name");
                            return null;
                        }
                        root = rootWanted;
                    }
                    else
                    {
                        root = BtUniqueRootLocked(folder, rootWanted, meta.RootDir.Length > 0, it.Id);
                        if (root == null) { error = Tr.S("не удалось подобрать свободное имя", "no free name was found"); return null; }
                    }
                    it.FileName = root;
                    _reserved[Path.Combine(folder, root)] = it.Id;
                }
                it.Log(now, Tr.S("добавлен торрент: ", "torrent added: ") + (meta != null ? it.FileName : (magnet.Name.Length > 0 ? magnet.Name : hash)));
                _items.Add(it);
                _store.Save(it);
                SaveOrder();
            }
            _wake.Set();
            return it.Id;
        }

        private string BtDuplicateLocked(string hash)
        {
            foreach (DlItem other in _items)
                if (other.IsTorrent && other.InfoHash == hash) return other.Id;
            return null;
        }

        private static string BtDuplicateText() { return Tr.S("этот торрент уже есть в списке загрузок", "this torrent is already in the download list"); }

        private static int[] ClampPriorities(int[] p)
        {
            if (p == null) return null;
            int[] copy = new int[p.Length];
            for (int i = 0; i < p.Length; i++) copy[i] = Math.Max(0, Math.Min(2, p[i]));
            return copy;
        }

        // Свободное имя корня: ни на диске (файл, частичный файл, папка), ни у другой загрузки. Папка «a.b» → «a.b (1)».
        private string BtUniqueRootLocked(string folder, string name, bool directory, string id)
        {
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in _reserved)
                if (kv.Value != id) taken.Add(kv.Key.ToLowerInvariant());
            if (!directory) return DlFiles.UniqueName(folder, name, taken);
            for (int n = 0; n < 10000; n++)
            {
                string candidate = n == 0 ? name : name + " (" + n + ")";
                string full = Path.Combine(folder, candidate);
                if (DlFiles.Exists(full) || taken.Contains(full.ToLowerInvariant())) continue;
                return candidate;
            }
            return null;
        }

        // Magnet: имя корня выбирается, когда пришли метаданные (поток диска сессии).
        private string BtChooseRoot(string id, BtMeta meta)
        {
            string name = DlFiles.SanitizeName(meta.RootDir.Length > 0 ? meta.RootDir : meta.Name);
            if (string.IsNullOrEmpty(name)) name = meta.HexHash;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return meta.HexHash;
                if (!string.IsNullOrEmpty(it.FileName)) return it.FileName;
                string root = BtUniqueRootLocked(it.Folder, name, meta.RootDir.Length > 0, id) ?? meta.HexHash;
                it.FileName = root;
                _reserved[Path.Combine(it.Folder, root)] = id;
                _store.Save(it);
                return root;
            }
        }

        // ---------- решения планировщика (под _lock) ----------
        private void BtTickLocked(DlSettings s, DateTime now, string idleReason)
        {
            _btUp.Rate = (long)s.BtUpKBps * 1024;
            foreach (KeyValuePair<string, BtTorrent> kv in _btRun)
            {
                DlItem it = FindLocked(kv.Key);
                if (it == null) continue;
                BtTorrent t = kv.Value;
                BtTorrentStats st = t.Stats();
                Interlocked.Exchange(ref it.TorrentDone, st.Done);
                Interlocked.Exchange(ref it.Uploaded, st.Uploaded);
                it.SpeedBps = st.DownBps;
                it.UpBps = st.UpBps;
                it.ActiveConnections = st.Peers;
                it.Seeds = st.Seeds;
                it.CheckProgress = st.CheckProgress;
                if (st.Wanted > 0) it.Total = st.Wanted;
                else if (it.Total < 0 && t.Meta != null) it.Total = t.Meta.TotalSize;
                t.DownLimit = (long)it.LimitKBps * 1024;
                t.RatioLimit = s.BtRatioPercent > 0 ? s.BtRatioPercent / 100.0 : 0;
                t.SeedTimeLimitSeconds = s.BtSeedMinutes > 0 ? s.BtSeedMinutes * 60L : 0;
                if (t.Sequential != it.Sequential) t.Sequential = it.Sequential;
                if (_btStop.ContainsKey(kv.Key)) continue;

                string gate = _globalGate.Length > 0 ? _globalGate : (it.WhenIdle || s.OnlyWhenIdle) ? idleReason : null;
                if (!string.IsNullOrEmpty(gate))
                {
                    Journal(it, Tr.S("остановлено: ", "stopped: ") + gate);
                    BtRequestStop(it, DlState.Waiting, gate);
                    continue;
                }
                switch (t.State)
                {
                    case BtTorrentState.FetchingMetadata:
                        BtShow(it, DlState.Active, Tr.S("получение метаданных", "fetching metadata"));
                        break;
                    case BtTorrentState.Checking:
                        BtShow(it, DlState.Checking, "");
                        break;
                    case BtTorrentState.Downloading:
                        // Выбрали ещё файлы после окончания (или данные пропали) — снова качающая загрузка.
                        it.CompletedUtc = DateTime.MinValue;
                        BtShow(it, DlState.Active, "");
                        break;
                    case BtTorrentState.Seeding:
                        if (it.CompletedUtc == DateTime.MinValue)
                        {
                            it.CompletedUtc = now;
                            it.State = DlState.Seeding;
                            it.WaitReason = "";
                            it.ErrorKind = DlErrorKind.None;
                            it.Error = "";
                            Journal(it, Tr.S("скачано: все выбранные файлы проверены", "downloaded: all selected files are verified"));
                            _store.Save(it);
                            RaiseNotice(it);
                        }
                        if (s.BtSeed) BtShow(it, DlState.Seeding, "");
                        else
                        {
                            Journal(it, Tr.S("раздача выключена в настройках", "seeding is turned off in the settings"));
                            BtRequestStop(it, DlState.Completed, "");
                        }
                        break;
                    case BtTorrentState.Finished:
                        BtRequestStop(it, DlState.Completed, "");
                        break;
                    case BtTorrentState.Error:
                        it.ErrorKind = DlErrorKind.Client;
                        it.Error = t.Error;
                        BtRequestStop(it, DlState.Failed, "");
                        break;
                }
            }
        }

        private void BtShow(DlItem it, DlState state, string reason)
        {
            if (it.State == state && it.WaitReason == reason) return;
            it.State = state;
            it.WaitReason = reason;
            _store.Save(it);
        }

        private void BtRequestStop(DlItem it, DlState after, string reason)
        {
            _btStop[it.Id] = after;
            if (after == DlState.Waiting) it.WaitReason = reason;
            _wake.Set();
        }

        private void BtPickStarts(DlSettings s, DateTime now)
        {
            if (_disposed || now < _btRetryAt) return;
            List<DlItem> queue = new List<DlItem>();
            foreach (DlItem it in _items)
                if (it.IsTorrent && it.State == DlState.Queued && it.NextRetryUtc <= now && !_btRun.ContainsKey(it.Id)
                    && !_btStarting.Contains(it.Id) && it.MoveTo.Length == 0 && !_btUpdBlocked.Contains(it.Id)) queue.Add(it);
            if (queue.Count == 0) return;
            List<DlItem> order = new List<DlItem>(_items);
            queue.Sort(delegate(DlItem a, DlItem b)
            {
                if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
                return order.IndexOf(a).CompareTo(order.IndexOf(b));
            });
            int downloading = BtDownloadingCount();
            foreach (DlItem it in queue)
            {
                // Раздачи (всё выбранное уже скачано) в предел одновременных не входят.
                if (it.CompletedUtc == DateTime.MinValue)
                {
                    if (downloading >= s.BtMaxActive) continue;
                    downloading++;
                }
                _btStarting.Add(it.Id);
                _btStart.Add(it.Id);
            }
        }

        // Качающие торренты — в сессии (и не выходят из неё) или уже выбранные к запуску; раздачи не считаются.
        private int BtDownloadingCount()
        {
            int n = 0;
            foreach (DlItem it in _items)
            {
                if (!it.IsTorrent || it.CompletedUtc != DateTime.MinValue) continue;
                if ((_btRun.ContainsKey(it.Id) && !_btStop.ContainsKey(it.Id)) || _btStarting.Contains(it.Id)) n++;
            }
            return n;
        }

        // ---------- работа с сессией (поток планировщика, вне _lock) ----------
        private void BtBackground()
        {
            lock (_btOps)
            {
                if (_disposed) return;
                List<KeyValuePair<string, DlState>> stops;
                List<string> starts;
                DlSettings s;
                BtSession ses;
                lock (_lock)
                {
                    stops = new List<KeyValuePair<string, DlState>>(_btStop);
                    starts = new List<string>(_btStart);
                    _btStart.Clear();
                    s = _settings;
                    ses = _bt;
                }
                foreach (KeyValuePair<string, DlState> kv in stops) BtStopOne(kv.Key, kv.Value);

                // Сетевые настройки поменялись: торренты выходят со снимком, сессия закрывается, следующий Tick запустит их в новой.
                if (ses != null && BtKey(s) != _btKey)
                {
                    List<string> ids;
                    lock (_lock) ids = new List<string>(_btRun.Keys);
                    foreach (string id in ids) BtStopOne(id, DlState.Queued);
                    BtCloseSession(false);
                    ses = null;
                }
                if (ses != null) BtApplyInbound(ses, s);
                foreach (string id in starts) BtStartOne(id, s);
                BtUpdateBackground(s);

                DateTime now = _env.UtcNow;
                if ((now - _btResumeAt).TotalSeconds >= BtResumeSaveSeconds || now < _btResumeAt)
                {
                    _btResumeAt = now;
                    List<BtTorrent> running;
                    lock (_lock) running = new List<BtTorrent>(_btRun.Values);
                    foreach (BtTorrent t in running) BtSaveResume(t);
                }

                bool idle;
                lock (_lock)
                {
                    ses = _bt;
                    idle = _btRun.Count == 0 && _btStarting.Count == 0 && _btProbes.Count == 0;
                }
                if (ses == null || !idle) _btIdleSince = DateTime.MinValue;
                else if (_btIdleSince == DateTime.MinValue || now < _btIdleSince) _btIdleSince = now;
                else if ((now - _btIdleSince).TotalSeconds >= BtIdleCloseSeconds)
                {
                    BtCloseSession(false);
                    _btIdleSince = DateTime.MinValue;
                }
            }
        }

        private int BtPortFor(DlSettings s) { return s.BtPort > 0 ? s.BtPort : _btAutoPort; }

        private string BtKey(DlSettings s)
        {
            return string.Join("|", new[]
            {
                BtPortFor(s).ToString(CultureInfo.InvariantCulture), s.BtEncryption.ToString(), s.BtDht ? "1" : "0", s.BtLsd ? "1" : "0",
                s.BtPex ? "1" : "0", s.BtMaxConnections.ToString(CultureInfo.InvariantCulture),
                s.BtMaxPerTorrent.ToString(CultureInfo.InvariantCulture), s.BtUploadSlots.ToString(CultureInfo.InvariantCulture)
            });
        }

        private bool BtInboundAllowed()
        {
            Func<bool> hook = BtInboundReady;
            if (hook != null) return hook();
            // Без правила брандмауэра слушатель вызвал бы окно Windows — его открывает только кнопка пользователя.
            return Engine.FirewallState(DlPaths.ExecutablePath) == FirewallInbound.Allowed;
        }

        private static int BtInboundStateOf(DlSettings s) { return (s.BtInbound ? 1 : 0) | (s.BtPortMapping ? 2 : 0); }

        // Правило брандмауэра добавлено или снято: проверить заново, даже если настройки те же.
        public void RecheckInbound()
        {
            lock (_lock) _btInboundState = -1;
            _wake.Set();
        }

        private void BtApplyInbound(BtSession ses, DlSettings s)
        {
            int state = BtInboundStateOf(s);
            lock (_lock)
            {
                if (state == _btInboundState) return;
                _btInboundState = state;
            }
            bool open = s.BtInbound && BtInboundAllowed();
            ses.SetInboundOpen(open);
            BtMapperSet(open && s.BtPortMapping, ses.Context.Port);
        }

        private void BtMapperSet(bool on, int port)
        {
            IBtPortMapper old;
            lock (_lock) old = _btMapper;
            if (on == (old != null)) return;
            if (!on)
            {
                lock (_lock) _btMapper = null;
                ThreadPool.QueueUserWorkItem(delegate { try { old.Dispose(); } catch (Exception ex) { DlLog.Report(ex); } });
                return;
            }
            Func<IBtPortMapper> factory = BtFactories.PortMapper;
            if (factory == null) return;
            try
            {
                IBtPortMapper m = factory();
                if (m == null) return;
                m.Start(port, port);
                lock (_lock) _btMapper = m;
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private BtSession BtEnsureSession(DlSettings s)
        {
            lock (_lock) if (_bt != null) return _bt;
            BtWiring.Install();
            BtSessionOptions o = new BtSessionOptions();
            o.Bind = BtBind;
            int port = BtPortFor(s);
            o.Port = port > 0 ? port : 49152 + new Random().Next(16384);
            o.TorrentsDir = TorrentsDir;
            Directory.CreateDirectory(o.TorrentsDir);
            o.Env = _env;
            o.DownGlobal = _global;
            o.UpGlobal = _btUp;
            o.Encryption = s.BtEncryption;
            o.InboundOpen = s.BtInbound && BtInboundAllowed();
            o.EnableDht = s.BtDht;
            o.EnableLsd = s.BtLsd;
            o.EnablePex = s.BtPex;
            o.MaxConnections = s.BtMaxConnections;
            o.MaxConnectionsPerTorrent = s.BtMaxPerTorrent;
            o.UploadSlots = s.BtUploadSlots;
            o.Log = delegate(string line) { DlLog.Write("bt: " + line); };
            BtSession ses = new BtSession(o);
            ses.Start();
            int actual = ses.Context.Port;
            DlSettings persist = null;
            lock (_lock)
            {
                if (s.BtPort == 0 && _btAutoPort == 0)
                {
                    _btAutoPort = actual;
                    if (_settings.BtPort == 0)
                    {
                        _settings.BtPort = actual;
                        persist = _settings.Clone();
                    }
                }
                _bt = ses;
                _btKey = BtKey(s);
                _btInboundState = BtInboundStateOf(s);
            }
            if (o.InboundOpen && s.BtPortMapping) BtMapperSet(true, actual);
            Action<DlSettings> save = SettingsPersist;
            if (persist != null && save != null)
                try { save(persist); } catch (Exception ex) { DlLog.Report(ex); }
            return ses;
        }

        private void BtCloseSession(bool waitMapper)
        {
            BtSession ses;
            IBtPortMapper m;
            lock (_lock)
            {
                ses = _bt;
                m = _btMapper;
                _bt = null;
                _btMapper = null;
                _btKey = "";
                // Пробы живут только в сессии: следующий Tick попросит их заново.
                foreach (string id in _btProbes.Keys) _btProbeAt.Remove(id);
                _btProbes.Clear();
                _btInboundState = -1;
            }
            if (ses != null) try { ses.Dispose(); } catch (Exception ex) { DlLog.Report(ex); }
            if (m == null) return;
            // Снятие проброса ждёт роутер до ~14 с: при выходе процесса — дождаться, иначе — в фоне.
            if (waitMapper) try { m.Dispose(); } catch (Exception ex) { DlLog.Report(ex); }
            else ThreadPool.QueueUserWorkItem(delegate { try { m.Dispose(); } catch (Exception ex) { DlLog.Report(ex); } });
        }

        private void BtStartOne(string id, DlSettings s)
        {
            string hash, link, folder, root;
            int[] prio;
            bool sequential;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                DlState after;
                if (it == null || _btRun.ContainsKey(id)) { _btStarting.Remove(id); return; }
                if (_btStop.TryGetValue(id, out after))
                {
                    // Остановку попросили раньше, чем торрент дошёл до сессии.
                    _btStop.Remove(id);
                    _btStarting.Remove(id);
                    it.State = after;
                    if (after != DlState.Waiting) it.WaitReason = "";
                    _store.Save(it);
                    return;
                }
                if (it.State != DlState.Queued) { _btStarting.Remove(id); return; }
                hash = it.InfoHash;
                link = it.Url ?? "";
                folder = it.Folder;
                root = it.FileName;
                prio = it.FilePriorities == null ? null : (int[])it.FilePriorities.Clone();
                sequential = it.Sequential;
            }

            BtAddParams p = new BtAddParams();
            string error = null;
            try
            {
                p.Meta = BtLoadMeta(hash);
                string e;
                BtMagnet magnet = link.Length > 0 ? BtMagnet.Parse(link, out e) : null;
                if (p.Meta == null) p.Magnet = magnet;
                else if (magnet != null)
                {
                    // Трекеры и пиры ссылки не входят в словарь info: у метаданных, полученных по ней, их нет.
                    p.ExtraTrackers = new List<List<string>>();
                    foreach (string u in magnet.Trackers)
                        if (BtMeta.IsTrackerUrl(u) && !BtHasTracker(p.Meta, u)) p.ExtraTrackers.Add(new List<string> { u });
                    p.Peers = new List<BtEndpoint>();
                    foreach (string pe in magnet.Peers)
                    {
                        BtEndpoint ep = BtEndpoint.TryParse(pe);
                        if (ep != null) p.Peers.Add(ep);
                    }
                }
                if (p.Meta == null && p.Magnet == null)
                    error = Tr.S("нет метаданных торрента: пропал файл ", "no torrent metadata: missing file ") + hash + ".torrent";
                p.Folder = folder;
                p.RootName = string.IsNullOrEmpty(root) ? null : root;
                p.ChooseRootName = delegate(BtMeta m) { return BtChooseRoot(id, m); };
                p.Resume = BtResume.Load(BtResume.FileFor(TorrentsDir, hash));
                p.Priorities = prio;
                p.Sequential = sequential;
            }
            catch (Exception ex) { error = ex.Message; }

            BtTorrent t = null;
            if (error == null)
            {
                BtSession ses;
                try { ses = BtEnsureSession(s); }
                catch (Exception ex)
                {
                    DlLog.Report(ex);
                    lock (_lock)
                    {
                        _btStarting.Remove(id);
                        _btRetryAt = _env.UtcNow.AddSeconds(30);
                        DlItem it = FindLocked(id);
                        if (it != null)
                        {
                            it.WaitReason = Tr.S("порт торрентов не открылся, повтор через 30 с", "the torrent port did not open, retry in 30 s");
                            Journal(it, it.WaitReason + ": " + ex.Message);
                        }
                    }
                    return;
                }
                t = ses.Add(p, out error);
                if (t != null)
                {
                    BtTorrent added = t;
                    t.MetadataReceived = delegate { BtOnMetadata(id, added); };
                    t.Journaled = delegate(BtTorrent x, string text) { BtJournal(id, text); };
                    t.StateChanged = delegate { _wake.Set(); };
                }
            }

            bool orphan = false;
            lock (_lock)
            {
                _btStarting.Remove(id);
                DlItem it = FindLocked(id);
                if (t == null)
                {
                    if (it == null) return;
                    it.State = DlState.Failed;
                    it.ErrorKind = DlErrorKind.Client;
                    it.Error = error ?? "";
                    Journal(it, Tr.S("ошибка: ", "error: ") + it.Error);
                    _store.Save(it);
                    RaiseNotice(it);
                    return;
                }
                if (it == null) orphan = true;
                else
                {
                    _btRun[id] = t;
                    it.State = t.State == BtTorrentState.Checking ? DlState.Checking : DlState.Active;
                    it.WaitReason = "";
                    it.ErrorKind = DlErrorKind.None;
                    it.Error = "";
                    _store.Save(it);
                }
            }
            if (orphan)
            {
                BtSession ses = TorrentSession;
                if (ses != null) ses.Remove(t);
            }
        }

        private static bool BtHasTracker(BtMeta meta, string url)
        {
            foreach (List<string> tier in meta.Trackers)
                foreach (string u in tier)
                    if (string.Equals(u, url, StringComparison.Ordinal)) return true;
            return false;
        }

        private void BtStopOne(string id, DlState after)
        {
            BtTorrent t;
            BtSession ses;
            lock (_lock)
            {
                if (!_btRun.TryGetValue(id, out t))
                {
                    // Ещё запускается — остановку применит BtStartOne; иначе запрос устарел.
                    if (!_btStarting.Contains(id)) _btStop.Remove(id);
                    return;
                }
                ses = _bt;
            }
            BtSaveResume(t);
            if (ses != null)
            {
                ses.Remove(t);
                if (!BtWaitDisk(ses, 15000)) DlLog.Write(id + " bt: files were not closed within 15 s");
            }
            lock (_lock)
            {
                _btRun.Remove(id);
                DlState requested;
                // Пока шла остановка, попросили другое (пауза поверх условия очереди) — последнее слово за последним запросом.
                if (_btStop.TryGetValue(id, out requested)) after = requested;
                _btStop.Remove(id);
                DlItem it = FindLocked(id);
                if (it == null) return;
                it.SpeedBps = 0;
                it.UpBps = 0;
                it.ActiveConnections = 0;
                it.Seeds = 0;
                it.CheckProgress = 0;
                it.State = after;
                if (after != DlState.Waiting) it.WaitReason = "";
                if (after == DlState.Completed) ReleaseReservation(id);
                _store.Save(it);
                if (after == DlState.Failed) RaiseNotice(it);
            }
        }

        // Сессия закрыла файлы торрента: закрытие идёт в потоке диска после выхода из реактора — ждём своей очереди там же.
        private static bool BtWaitDisk(BtSession ses, int ms)
        {
            ManualResetEvent done = new ManualResetEvent(false);   // без Dispose: поздний Set из потока диска не должен падать
            ses.Reactor.Post(delegate
            {
                BtDisk disk = ses.Disk;
                if (disk == null) done.Set();
                else disk.Run(delegate { done.Set(); });
            });
            return done.WaitOne(ms);
        }

        private void BtShutdown()
        {
            lock (_btOps)
            {
                List<KeyValuePair<string, BtTorrent>> running;
                lock (_lock) running = new List<KeyValuePair<string, BtTorrent>>(_btRun);
                foreach (KeyValuePair<string, BtTorrent> kv in running) BtSaveResume(kv.Value);
                BtCloseSession(true);
                lock (_lock)
                {
                    foreach (KeyValuePair<string, BtTorrent> kv in running)
                    {
                        DlItem it = FindLocked(kv.Key);
                        if (it == null) continue;
                        it.SpeedBps = 0;
                        it.UpBps = 0;
                        it.ActiveConnections = 0;
                        DlState after;
                        if (_btStop.TryGetValue(kv.Key, out after)) it.State = after;
                        else if (it.State == DlState.Active || it.State == DlState.Checking || it.State == DlState.Seeding) it.State = DlState.Queued;
                    }
                    _btRun.Clear();
                    _btStarting.Clear();
                    _btStart.Clear();
                    _btStop.Clear();
                }
            }
        }

        // ---------- обратные вызовы торрента (пул потоков) ----------
        private void BtOnMetadata(string id, BtTorrent t)
        {
            try { BtEnsureSideFile(t); }
            catch (Exception ex) { DlLog.Report(ex); }
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || t.Meta == null) return;
                if (it.Total < 0) it.Total = t.Meta.TotalSize;
                _store.Save(it);
            }
        }
    }
}
