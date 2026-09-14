// Windows Process Cleaner — «Загрузки»: движок — очередь, лимиты, условия запуска, повторы, зеркала, обновление ссылки.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Один поток-планировщик раз в 250 мс: собирает завершившиеся загрузки, проверяет условия (расписание, лимитная сеть,
// батарея, простой ПК, отложенный старт), останавливает активные, чьё условие закрылось, и запускает новые в пределах
// «всего одновременно» и «с одного сайта». Он же держит ПК от сна, пока что-то качается, и раз в 2 с сохраняет
// смещения. Пауза и остановка сохраняют скачанное; при следующем запуске процесса активные продолжают сами.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class DlAddRequest
    {
        public string Url = "";
        public string Folder = "";
        public string FileName = "";
        public string Referrer = "";
        public string PageUrl = "";
        public string UserAgent = "";
        public string Cookies = "";
        public string Source = "manual";
        public string ExpectedHash = "";
        public readonly List<string> Mirrors = new List<string>();
        public DateTime StartAtUtc = DateTime.MinValue;
        public bool WhenIdle;
        public int LimitKBps;
        public int Priority;
        public int Connections;
        public bool AllowDuplicate;
        public bool StartPaused;
        public bool AllowHttpDowngrade;
        public DlMedia Media;                    // не null — видео-загрузка (Kind = "media"): выбор дорожек из окна добавления
    }

    internal sealed partial class DlEngine : IDlTransferHost, IDisposable
    {
        public const int TickMs = 250;

        private readonly object _lock = new object();
        private readonly List<DlItem> _items;
        private readonly Dictionary<string, IDlRun> _active = new Dictionary<string, IDlRun>();
        private readonly Dictionary<string, DlState> _afterStop = new Dictionary<string, DlState>();
        private readonly Dictionary<string, string> _reserved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _mirrorsTried = new Dictionary<string, HashSet<string>>();
        private readonly DlStore _store;
        private readonly IDlEnvironment _env;
        private readonly DlTokenBucket _global = new DlTokenBucket(0);
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private DlSettings _settings;
        private Thread _loop;
        private volatile bool _disposed;
        private DateTime _persistedAt = DateTime.MinValue;
        private bool _awake;
        private bool _idleHeld;
        private string _globalGate = "";

        public DlEngine(DlStore store, DlSettings settings, IDlEnvironment env)
        {
            _store = store;
            _settings = settings ?? new DlSettings();
            _env = env ?? new DlSystemEnvironment();
            _items = store.LoadAll();
            foreach (DlItem it in _items)
            {
                // Процесс прошлый раз закрылся посреди загрузки (или его убили) — продолжить.
                if (it.State == DlState.Active || it.State == DlState.Waiting || it.State == DlState.Checking || it.State == DlState.Seeding)
                {
                    it.State = DlState.Queued;
                    it.WaitReason = "";
                }
                if (it.State != DlState.Completed && !string.IsNullOrEmpty(it.FileName) && !string.IsNullOrEmpty(it.Folder))
                    _reserved[it.TargetPath] = it.Id;
            }
            BtUpdateLoad();
        }

        public DlSettings Settings { get { lock (_lock) return _settings; } }
        public long GlobalLimitBytes { get { return _global.Rate; } }
        public string GlobalGate { get { lock (_lock) return _globalGate; } }

        public void UpdateSettings(DlSettings s)
        {
            if (s == null) return;
            lock (_lock)
            {
                _settings = s.Clone();
                // Страница прислала настройки, прочитанные до того, как движок выбрал порт: выбранный остаётся.
                if (_settings.BtPort == 0 && _btAutoPort > 0) _settings.BtPort = _btAutoPort;
            }
            _wake.Set();
        }

        public void Start()
        {
            if (_loop != null) return;
            BtUpdateRollForward();
            _loop = new Thread(Loop);
            _loop.IsBackground = true;
            _loop.Name = "wpc-dl-scheduler";
            _loop.Start();
            ResumeMoves();
        }

        // ---------- команды ----------
        public string Add(DlAddRequest r, out string duplicateOf, out string error)
        {
            duplicateOf = null;
            error = null;
            string url = (r.Url ?? "").Trim();
            if (!DlHttp.IsAllowedScheme(url)) { error = Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"); return null; }
            DlItem it = new DlItem();
            it.Id = DlItem.NewId();
            it.Url = url;
            it.OriginalUrl = url;
            if (r.Media != null) { it.Kind = DlItem.KindMedia; it.Media = r.Media; }
            it.Referrer = (r.Referrer ?? "").Trim();
            it.PageUrl = (r.PageUrl ?? "").Trim();
            it.UserAgent = (r.UserAgent ?? "").Trim();
            it.Source = string.IsNullOrEmpty(r.Source) ? "manual" : r.Source;
            if (!string.IsNullOrEmpty(r.Cookies))
            {
                it.Cookies = r.Cookies;
                it.CookieHost = new Uri(url).Host;
            }
            if (!string.IsNullOrEmpty(r.Folder))
            {
                string why;
                string folder = DlFiles.CheckFolder(r.Folder, out why);
                if (folder == null) { error = why; return null; }
                it.Folder = folder;
            }
            if (!string.IsNullOrEmpty(r.FileName))
            {
                it.FileName = DlFiles.SanitizeName(r.FileName);
                it.NameFixed = true;
            }
            if (!string.IsNullOrEmpty(r.ExpectedHash))
            {
                string algo, hex;
                if (!DlFinish.ParseExpected(r.ExpectedHash, out algo, out hex)) { error = Tr.S("ожидаемый хеш не разобран (sha256:…, sha1:…, md5:…)", "the expected hash is not recognised (sha256:…, sha1:…, md5:…)"); return null; }
                it.ExpectedHash = algo + ":" + hex.ToLowerInvariant();
            }
            foreach (string m in r.Mirrors)
                if (DlHttp.IsAllowedScheme(m) && !string.Equals(m, url, StringComparison.Ordinal) && !it.Mirrors.Contains(m)) it.Mirrors.Add(m);
            it.WhenIdle = r.WhenIdle;
            it.LimitKBps = DlJson.Clamp(r.LimitKBps, 0, 10 * 1024 * 1024);
            it.Priority = DlJson.Clamp(r.Priority, -1, 1);
            it.Connections = DlJson.Clamp(r.Connections, 0, 16);
            it.AllowHttpDowngrade = r.AllowHttpDowngrade;
            DateTime now = _env.UtcNow;
            it.AddedUtc = now;
            if (r.StartPaused) it.State = DlState.Paused;
            else if (r.StartAtUtc > now) { it.State = DlState.Scheduled; it.StartAtUtc = r.StartAtUtc; }
            else it.State = DlState.Queued;

            lock (_lock)
            {
                string key = UrlKey(url);
                foreach (DlItem other in _items)
                {
                    if (other.State == DlState.Failed) continue;
                    if (UrlKey(other.OriginalUrl) == key || UrlKey(other.Url) == key) { duplicateOf = other.Id; break; }
                }
                if (duplicateOf != null && !r.AllowDuplicate)
                {
                    error = Tr.S("эта ссылка уже есть в списке загрузок", "this link is already in the download list");
                    return null;
                }
                it.Log(now, Tr.S("добавлено: ", "added: ") + DlLog.Redact(url));
                _items.Add(it);
                _store.Save(it);
                SaveOrder();
            }
            _wake.Set();
            return it.Id;
        }

        // Ссылка без фрагмента, схема и хост в нижнем регистре — ключ для поиска повторов.
        internal static string UrlKey(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return (url ?? "").Trim();
            return u.Scheme.ToLowerInvariant() + "://" + u.Authority.ToLowerInvariant() + u.PathAndQuery;
        }

        public bool Pause(string id) { return StopOrSet(id, DlState.Paused, ""); }

        public bool Resume(string id)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || _active.ContainsKey(id) || _btRun.ContainsKey(id) || _btStarting.Contains(id) || _btUpdBlocked.Contains(id)) return false;
                // Готовый торрент «продолжить» — снова раздавать.
                if ((it.State == DlState.Completed && !it.IsTorrent) || it.MoveTo.Length > 0) return false;
                it.State = it.StartAtUtc > _env.UtcNow ? DlState.Scheduled : DlState.Queued;
                it.WaitReason = "";
                it.Attempts = 0;
                it.NextRetryUtc = DateTime.MinValue;
                it.Log(_env.UtcNow, Tr.S("продолжить", "resume"));
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        // Пояснение к стоящей записи — человеку в списке видно, чего она ждёт. Состояние не меняется.
        public bool SetWaitReason(string id, string reason)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return false;
                it.WaitReason = reason ?? "";
                _store.Save(it);
            }
            return true;
        }

        // Папка до первого байта: только для записи, которая ещё стоит и ничего не скачала. Когда файл уже начат,
        // папку меняет перенос (Downloads.Relocate.cs) — он умеет двигать сам файл, а это просто присвоение.
        public bool SetFolderBeforeStart(string id, string folder, out string why)
        {
            why = null;
            string checkedFolder = DlFiles.CheckFolder(folder, out why);
            if (checkedFolder == null) return false;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                if (_active.ContainsKey(id) || _btRun.ContainsKey(id) || _btStarting.Contains(id))
                {
                    why = Tr.S("загрузка уже идёт — используйте «Переместить в…»", "the download is already running — use «Move to…»");
                    return false;
                }
                if (it.DoneBytes > 0 || it.State == DlState.Completed)
                {
                    why = Tr.S("часть уже скачана — используйте «Переместить в…»", "part of it is already downloaded — use «Move to…»");
                    return false;
                }
                it.Folder = checkedFolder;
                it.Log(_env.UtcNow, Tr.S("папка: ", "folder: ") + checkedFolder);
                _store.Save(it);
            }
            return true;
        }

        public void PauseAll()
        {
            List<string> ids = new List<string>();
            lock (_lock) foreach (DlItem it in _items) if (it.State != DlState.Completed && it.State != DlState.Paused) ids.Add(it.Id);
            foreach (string id in ids) Pause(id);
        }

        public void ResumeAll()
        {
            List<string> ids = new List<string>();
            lock (_lock) foreach (DlItem it in _items) if (it.State == DlState.Paused) ids.Add(it.Id);
            foreach (string id in ids) Resume(id);
        }

        // Скачать заново: скачанное (своё .wpcpart) удаляется, имя берётся снова. Только с согласия пользователя — это его кнопка.
        public bool Restart(string id, out string why)
        {
            why = null;
            if (IsTorrentId(id)) { why = Tr.S("для торрента — «Проверить данные»", "for a torrent use «Check data»"); return false; }
            IDlRun tr = StopAndWait(id, DlState.Paused);
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                if (it.State == DlState.Completed) { why = Tr.S("загрузка уже завершена", "the download is already complete"); return false; }
                if (tr != null && !tr.Finished) { why = Tr.S("загрузка не остановилась", "the download did not stop"); return false; }
                if (it.MoveTo.Length > 0) { why = MovingWhy(); return false; }
                why = DeletePartial(it);
                if (why != null) return false;
                // Видео: куски и журнал лежат отдельно от .wpcpart — без этого «заново» продолжило бы старую сборку.
                why = DeleteMediaParts(it);
                if (why != null) return false;
                lock (it.Segments) it.Segments.Clear();
                it.Total = -1;
                it.ETag = it.LastModified = it.FinalUrl = "";
                it.ErrorKind = DlErrorKind.None;
                it.Error = "";
                it.Attempts = 0;
                it.State = DlState.Queued;
                it.Log(_env.UtcNow, Tr.S("заново с нуля", "restart from zero"));
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        public bool Remove(string id, bool recycleFiles, out string why)
        {
            why = null;
            if (IsTorrentId(id)) return BtRemove(id, recycleFiles, out why);
            IDlRun tr = StopAndWait(id, DlState.Paused);
            string path = null;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                if (tr != null && !tr.Finished) { why = Tr.S("загрузка не остановилась", "the download did not stop"); return false; }
                if (it.MoveTo.Length > 0) { why = MovingWhy(); return false; }
                if (recycleFiles && !string.IsNullOrEmpty(it.FileName))
                {
                    string target = DlFiles.PathInside(it.Folder ?? "", it.FileName);
                    if (target != null) path = it.State == DlState.Completed ? target : target + DlPaths.PartSuffix;
                }
            }
            if (path != null)
            {
                why = DlFiles.Recycle(path);
                if (why != null) return false;
            }
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return true;
                DeleteMediaParts(it);                   // куски видео — всегда свои файлы, корзина для них не нужна
                _items.Remove(it);
                ReleaseReservation(it.Id);
                _mirrorsTried.Remove(it.Id);
                _store.Delete(it.Id);
                SaveOrder();
            }
            return true;
        }

        // «Остановить запись» у трансляции: докачать начатые куски и собрать файл. Обратно не выключается —
        // запись уже завершается, и второе нажатие ничего не значит.
        public bool StopLive(string id, out string why)
        {
            why = null;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                if (!it.IsMedia || it.Media == null || !it.Media.Live) { why = Tr.S("это не запись трансляции", "this is not a live recording"); return false; }
                if (it.Media.StopLive) return true;
                it.Media.StopLive = true;
                it.Log(_env.UtcNow, Tr.S("остановка записи по кнопке", "recording stopped by the user"));
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        public bool SetLimit(string id, int kbps)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return false;
                it.LimitKBps = DlJson.Clamp(kbps, 0, 10 * 1024 * 1024);
                IDlRun tr;
                if (_active.TryGetValue(id, out tr)) tr.Bucket.Rate = (long)it.LimitKBps * 1024;
                _store.Save(it);
                return true;
            }
        }

        public bool SetPriority(string id, int priority)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return false;
                it.Priority = DlJson.Clamp(priority, -1, 1);
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        public bool SetWhenIdle(string id, bool on)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return false;
                it.WhenIdle = on;
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        // utc = MinValue — отменить откладывание.
        public bool Postpone(string id, DateTime utc)
        {
            bool future = utc > _env.UtcNow;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || it.State == DlState.Completed) return false;
                it.StartAtUtc = future ? utc : DateTime.MinValue;
            }
            if (future) StopOrSet(id, DlState.Scheduled, "");
            else
            {
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it != null && it.State == DlState.Scheduled) it.State = DlState.Queued;
                    if (it != null) _store.Save(it);
                }
            }
            _wake.Set();
            return true;
        }

        // Свежая ссылка на тот же файл: скачанное сохраняется, если размер у нового сервера тот же (проверяет докачка).
        public bool RefreshLink(string id, string url, string cookies, out string why)
        {
            why = null;
            if (IsTorrentId(id)) { why = Tr.S("у торрента нет ссылки на файл", "a torrent has no file link"); return false; }
            if (!DlHttp.IsAllowedScheme(url)) { why = Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"); return false; }
            IDlRun tr = StopAndWait(id, DlState.Paused);
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) { why = Tr.S("нет такой загрузки", "no such download"); return false; }
                if (it.State == DlState.Completed) { why = Tr.S("загрузка уже завершена", "the download is already complete"); return false; }
                if (tr != null && !tr.Finished) { why = Tr.S("загрузка не остановилась", "the download did not stop"); return false; }
                if (it.MoveTo.Length > 0) { why = MovingWhy(); return false; }
                it.Url = url.Trim();
                // Видео читает адрес из Media.ManifestUrl и только потом из Url — обновляем оба, иначе новый адрес не увидят.
                if (it.IsMedia && it.Media != null) it.Media.ManifestUrl = it.Url;
                it.FinalUrl = "";
                it.ETag = "";
                it.LastModified = "";
                it.Cookies = cookies ?? "";
                it.CookieHost = string.IsNullOrEmpty(cookies) ? "" : new Uri(it.Url).Host;
                it.ErrorKind = DlErrorKind.None;
                it.Error = "";
                it.Attempts = 0;
                it.NextRetryUtc = DateTime.MinValue;
                it.State = DlState.Queued;
                it.Log(_env.UtcNow, Tr.S("ссылка обновлена: ", "link refreshed: ") + DlLog.Redact(it.Url));
                _store.Save(it);
            }
            _wake.Set();
            return true;
        }

        public bool AddMirror(string id, string url)
        {
            if (!DlHttp.IsAllowedScheme(url)) return false;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || it.IsTorrent || it.Mirrors.Contains(url)) return false;
                it.Mirrors.Add(url);
                _store.Save(it);
                return true;
            }
        }

        // ---------- чтение ----------
        public DlItem Find(string id)
        {
            lock (_lock) return FindLocked(id);
        }

        private DlItem FindLocked(string id)
        {
            foreach (DlItem it in _items) if (it.Id == id) return it;
            return null;
        }

        public List<string> Ids()
        {
            lock (_lock)
            {
                List<string> ids = new List<string>();
                foreach (DlItem it in _items) ids.Add(it.Id);
                return ids;
            }
        }

        public int ActiveCount { get { lock (_lock) return _active.Count; } }

        // withEvents = false — для списка на странице (журналы у сотни загрузок — лишние мегабайты раз в полсекунды).
        public JVal ListJson(bool withEvents)
        {
            lock (_lock)
            {
                JVal root = JVal.NewObj();
                JVal arr = JVal.NewArr();
                long speed = 0;
                int torrents = 0, seeding = 0;
                foreach (DlItem it in _items)
                {
                    JVal j = it.ToJson(false);
                    if (!withEvents) j.Remove("Events");
                    arr.V.Add(j);
                    speed += it.SpeedBps;
                    // Торренты работают в сессии, а не в _active: качающиеся и проверяемые — загрузки, раздачи — отдельный счёт.
                    if (!it.IsTorrent) continue;
                    if (it.State == DlState.Active || it.State == DlState.Checking) torrents++;
                    else if (it.State == DlState.Seeding) seeding++;
                }
                root.Set("items", arr);
                root.Set("speed", DlJson.N(speed));
                root.Set("limit", DlJson.N(_global.Rate));
                root.Set("active", DlJson.N(_active.Count + torrents));
                root.Set("seeding", DlJson.N(seeding));
                root.Set("gate", DlJson.S(_globalGate));
                return root;
            }
        }

        public JVal ItemJson(string id)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                return it == null ? null : it.ToJson(false);
            }
        }

        // ---------- IDlTransferHost ----------
        public string ReserveName(DlItem item, string folder, string name)
        {
            lock (_lock)
            {
                HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, string> kv in _reserved)
                    if (kv.Value != item.Id) taken.Add(kv.Key.ToLowerInvariant());
                string unique = DlFiles.UniqueName(folder, name, taken);
                if (unique == null) return null;
                ReleaseReservation(item.Id);
                _reserved[System.IO.Path.Combine(folder, unique)] = item.Id;
                return unique;
            }
        }

        public void Persist(DlItem item) { _store.Save(item); }

        public void Journal(DlItem item, string text)
        {
            item.Log(_env.UtcNow, text);
            DlLog.Write(item.Id + " " + text);
        }

        private void ReleaseReservation(string id)
        {
            List<string> drop = new List<string>();
            foreach (KeyValuePair<string, string> kv in _reserved) if (kv.Value == id) drop.Add(kv.Key);
            foreach (string k in drop) _reserved.Remove(k);
        }

        // ---------- остановка ----------
        private bool StopOrSet(string id, DlState state, string reason)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || it.State == DlState.Completed) return false;
                IDlRun tr;
                if (_btRun.ContainsKey(id) || _btStarting.Contains(id))
                {
                    // Торрент выходит из сессии в потоке планировщика: снимок для продолжения и закрытие файлов ждут диск.
                    _btStop[id] = state;
                    it.WaitReason = reason;
                    _wake.Set();
                }
                else if (_active.TryGetValue(id, out tr))
                {
                    _afterStop[id] = state;
                    it.WaitReason = reason;
                    tr.RequestStop();
                }
                else
                {
                    it.State = state;
                    it.WaitReason = reason;
                    _store.Save(it);
                }
                return true;
            }
        }

        private IDlRun StopAndWait(string id, DlState state)
        {
            IDlRun tr;
            lock (_lock)
            {
                if (!_active.TryGetValue(id, out tr)) return null;
            }
            StopOrSet(id, state, "");
            tr.Join(15000);
            lock (_lock) CollectFinished();
            return tr;
        }

        // Своё частичное внутри папки загрузки, не ссылка. null — удалено или его не было.
        private static string DeletePartial(DlItem it)
        {
            if (string.IsNullOrEmpty(it.FileName)) return null;
            string target = DlFiles.PathInside(it.Folder ?? "", it.FileName);
            if (target == null) return null;
            string part = target + DlPaths.PartSuffix;
            if (!DlFiles.Exists(part)) return null;
            if (DlFiles.IsReparse(part)) return Tr.S("на месте частичного файла ссылка — не трогаю", "a link sits where the partial file was — left alone");
            try { System.IO.File.Delete(part); return null; }
            catch (Exception ex) { return ex.Message; }
        }

        // Папка частей видео (<цель>.wpcmedia): удаляет только свои файлы по точным именам и лишь пустую папку.
        // Не видео или папки нет — тишина; отсутствие модуля видео в этой сборке тоже не ошибка.
        private static string DeleteMediaParts(DlItem it)
        {
            if (it == null || !it.IsMedia || it.Media == null || MdHooks.DeleteParts == null) return null;
            try { return MdHooks.DeleteParts(it); }
            catch (Exception ex) { DlLog.Report(ex); return ex.Message; }
        }

        // ---------- планировщик ----------
        [DllImport("kernel32.dll")]
        private static extern uint SetThreadExecutionState(uint flags);
        private const uint EsContinuous = 0x80000000, EsSystemRequired = 0x00000001;

        private void Loop()
        {
            try
            {
                while (!_disposed)
                {
                    try { Tick(); }
                    catch (Exception ex) { DlLog.Report(ex); }
                    try { BtBackground(); }
                    catch (Exception ex) { DlLog.Report(ex); }
                    try { BtIntake(); }
                    catch (Exception ex) { DlLog.Report(ex); }
                    _wake.WaitOne(TickMs);
                }
            }
            finally
            {
                if (_awake) SetThreadExecutionState(EsContinuous);
            }
        }

        internal void Tick()
        {
            lock (_lock)
            {
                if (_disposed) return;
                DateTime now = _env.UtcNow;
                DateTime local = _env.LocalNow;
                DlSettings s = _settings;
                CollectFinished();

                _globalGate = GlobalGateReason(s, local);
                bool anyIdleItems = s.OnlyWhenIdle;
                foreach (DlItem it in _items) if (it.WhenIdle && it.State != DlState.Completed) anyIdleItems = true;
                string idleReason = anyIdleItems ? IdleReason(s) : null;

                foreach (DlItem it in _items)
                {
                    if (it.State == DlState.Scheduled && it.StartAtUtc <= now)
                    {
                        it.State = DlState.Queued;
                        it.StartAtUtc = DateTime.MinValue;
                        Journal(it, Tr.S("отложенный старт наступил", "the postponed start has come"));
                        _store.Save(it);
                    }
                    if (it.State != DlState.Queued && it.State != DlState.Waiting) continue;
                    string reason = _globalGate.Length > 0 ? _globalGate : (it.WhenIdle || s.OnlyWhenIdle) ? idleReason : null;
                    if (reason != null)
                    {
                        if (it.State != DlState.Waiting || it.WaitReason != reason)
                        {
                            it.State = DlState.Waiting;
                            it.WaitReason = reason;
                            _store.Save(it);
                        }
                    }
                    else if (it.State == DlState.Waiting)
                    {
                        it.State = DlState.Queued;
                        it.WaitReason = "";
                        _store.Save(it);
                    }
                }

                foreach (KeyValuePair<string, IDlRun> kv in _active)
                {
                    if (kv.Value.StopRequested) continue;
                    DlItem it = kv.Value.Item;
                    string reason = _globalGate.Length > 0 ? _globalGate : (it.WhenIdle || s.OnlyWhenIdle) ? idleReason : null;
                    if (!string.IsNullOrEmpty(reason))
                    {
                        _afterStop[kv.Key] = DlState.Waiting;
                        it.WaitReason = reason;
                        Journal(it, Tr.S("остановлено: ", "stopped: ") + reason);
                        kv.Value.RequestStop();
                    }
                }

                BtTickLocked(s, now, idleReason);
                BtUpdateTickLocked(s, now);
                MdWiring.ToolsTick(s.MdToolsUpdate, now);
                StartCandidates(s, now);
                BtPickStarts(s, now);

                _global.Rate = s.EffectiveLimitBytes(local);
                foreach (IDlRun tr in _active.Values)
                {
                    tr.Item.SpeedBps = tr.Meter.Sample();
                    tr.Bucket.Rate = (long)tr.Item.LimitKBps * 1024;
                }

                if ((now - _persistedAt).TotalSeconds >= 2 || now < _persistedAt)
                {
                    foreach (IDlRun tr in _active.Values) _store.Save(tr.Item);
                    foreach (string id in _btRun.Keys)
                    {
                        DlItem it = FindLocked(id);
                        if (it != null) _store.Save(it);
                    }
                    _persistedAt = now;
                }

                // Раздача сон не держит — только скачивание.
                bool wantAwake = s.PreventSleep && (_active.Count > 0 || BtDownloadingCount() > 0);
                if (wantAwake != _awake && Thread.CurrentThread == _loop)
                {
                    SetThreadExecutionState(wantAwake ? EsContinuous | EsSystemRequired : EsContinuous);
                    _awake = wantAwake;
                }
            }
        }

        private string GlobalGateReason(DlSettings s, DateTime local)
        {
            if (!s.ScheduleAllows(local)) return Tr.S("вне расписания", "outside the schedule");
            if (s.PauseOnMetered && _env.IsMetered()) return Tr.S("лимитное подключение", "metered connection");
            if (s.PauseOnBattery && _env.OnBattery()) return Tr.S("питание от батареи", "on battery power");
            return "";
        }

        // null — ПК простаивает. Процессор проверяется только для старта: наша же загрузка и проверка хеша грузят его сами,
        // и остановка по нему качала бы туда-сюда. Остановка — только по вводу пользователя или полноэкранной игре.
        private string IdleReason(DlSettings s)
        {
            int idle = _env.InputIdleSeconds();
            bool fullscreen = s.IdleNoFullscreen && _env.FullscreenBusy();
            if (idle < s.IdleMinutes * 60 || fullscreen)
            {
                _idleHeld = false;
                return fullscreen ? Tr.S("ждёт простоя ПК: полноэкранное приложение", "waits for an idle PC: a fullscreen app")
                                  : Tr.S("ждёт простоя ПК: пользователь за компьютером", "waits for an idle PC: the user is active");
            }
            if (_idleHeld) return null;
            if (_env.CpuPercent() >= s.IdleCpuPercent) return Tr.S("ждёт простоя ПК: процессор занят", "waits for an idle PC: the CPU is busy");
            _idleHeld = true;
            return null;
        }

        private void StartCandidates(DlSettings s, DateTime now)
        {
            List<DlItem> queue = new List<DlItem>();
            foreach (DlItem it in _items)
                if (!it.IsTorrent && it.State == DlState.Queued && it.NextRetryUtc <= now && !_active.ContainsKey(it.Id) && it.MoveTo.Length == 0) queue.Add(it);
            if (queue.Count == 0) return;
            List<DlItem> order = new List<DlItem>(_items);
            queue.Sort(delegate(DlItem a, DlItem b)
            {
                if (a.Priority != b.Priority) return b.Priority.CompareTo(a.Priority);
                return order.IndexOf(a).CompareTo(order.IndexOf(b));
            });

            int big = 0;
            Dictionary<string, int> perHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IDlRun tr in _active.Values)
            {
                if (!IsSmall(s, tr.Item)) big++;
                string h = tr.Item.Host;
                int n;
                perHost.TryGetValue(h, out n);
                perHost[h] = n + 1;
            }
            foreach (DlItem it in queue)
            {
                bool small = IsSmall(s, it);
                if (!small && big >= s.MaxActive) continue;
                string host = it.Host;
                int onHost;
                perHost.TryGetValue(host, out onHost);
                if (onHost >= s.MaxPerHost) continue;
                if (!small) big++;
                perHost[host] = onHost + 1;
                StartTransfer(it, s);
            }
        }

        private static bool IsSmall(DlSettings s, DlItem it)
        {
            return s.SmallFileMB > 0 && it.Total >= 0 && it.Total < (long)s.SmallFileMB * 1024 * 1024;
        }

        private void StartTransfer(DlItem it, DlSettings s)
        {
            IDlRun tr;
            if (it.IsMedia)
            {
                // Видео-загрузку собирает MdHooks.CreateRun (Downloads.MediaWiring.cs); без неё запись не запускается.
                tr = MdHooks.CreateRun == null ? null : MdHooks.CreateRun(it, s, _global, _env, this);
                if (tr == null)
                {
                    it.State = DlState.Failed;
                    it.ErrorKind = DlErrorKind.Policy;
                    it.Error = Tr.S("видео-загрузки недоступны в этой сборке", "video downloads are unavailable in this build");
                    _store.Save(it);
                    return;
                }
            }
            else tr = new DlTransfer(it, s, _global, _env, this);
            it.State = DlState.Active;
            it.WaitReason = "";
            _active[it.Id] = tr;
            _store.Save(it);
            tr.Start();
        }

        private void CollectFinished()
        {
            List<IDlRun> done = new List<IDlRun>();
            foreach (IDlRun tr in _active.Values) if (tr.Finished) done.Add(tr);
            foreach (IDlRun tr in done) Finished(tr);
        }

        private void Finished(IDlRun tr)
        {
            DlItem it = tr.Item;
            _active.Remove(it.Id);
            it.SpeedBps = 0;
            it.ActiveConnections = 0;
            DateTime now = _env.UtcNow;
            DlState after;
            bool stopped = _afterStop.TryGetValue(it.Id, out after);
            _afterStop.Remove(it.Id);
            DlFailure f = tr.Failure;

            if (stopped && (f == null || tr.StopRequested) && !IsAllDone(it, f))
            {
                it.State = after;
                if (after != DlState.Waiting) it.WaitReason = "";
            }
            else if (f == null)
            {
                it.State = DlState.Completed;
                it.ErrorKind = DlErrorKind.None;
                it.Error = "";
                it.Attempts = 0;
                it.WaitReason = "";
                ReleaseReservation(it.Id);
            }
            else
            {
                it.ErrorKind = f.Kind;
                it.Error = f.Message;
                Journal(it, Tr.S("ошибка: ", "error: ") + f.Message);
                if (f.Retryable)
                {
                    it.Attempts++;
                    if (it.Attempts > _settings.MaxRetries)
                    {
                        if (!TryMirror(it)) it.State = DlState.Failed;
                    }
                    else
                    {
                        int delay = f.RetryAfterSeconds > 0 ? Math.Min(f.RetryAfterSeconds, 24 * 3600)
                                                            : (int)Math.Min(_settings.RetryMaxSeconds, Math.Pow(2, Math.Min(20, it.Attempts - 1)));
                        it.NextRetryUtc = now.AddSeconds(delay);
                        it.State = DlState.Queued;
                        it.WaitReason = Tr.S("повтор ", "retry ") + it.Attempts + Tr.S(" через ", " in ") + delay.ToString(CultureInfo.InvariantCulture) + Tr.S(" с", " s");
                    }
                }
                else if (f.Kind == DlErrorKind.LinkExpired)
                {
                    if (!TryMirror(it)) it.State = DlState.NeedsLink;
                }
                else if (f.Kind == DlErrorKind.Client && !TryMirror(it)) it.State = DlState.Failed;
                else if (f.Kind != DlErrorKind.Client) it.State = DlState.Failed;
            }
            _store.Save(it);
            if (it.State == DlState.Completed) BtChainLocked(it);
            if (it.State == DlState.Completed || it.State == DlState.Failed || it.State == DlState.NeedsLink) RaiseNotice(it);
        }

        // Остановка пришла, когда всё уже скачано и проверено: итог — «готово», а не «пауза».
        private static bool IsAllDone(DlItem it, DlFailure f)
        {
            return f == null && it.CompletedUtc != DateTime.MinValue;
        }

        private bool TryMirror(DlItem it)
        {
            HashSet<string> tried;
            if (!_mirrorsTried.TryGetValue(it.Id, out tried)) { tried = new HashSet<string>(StringComparer.Ordinal); _mirrorsTried[it.Id] = tried; }
            tried.Add(it.Url);
            foreach (string m in it.Mirrors)
            {
                if (tried.Contains(m)) continue;
                tried.Add(m);
                it.Url = m;
                it.FinalUrl = "";
                // Валидаторы другого сервера не годятся для If-Range: докачка сверит размер, а хеш (если задан) — содержимое.
                it.ETag = "";
                it.LastModified = "";
                it.Attempts = 0;
                it.NextRetryUtc = DateTime.MinValue;
                it.State = DlState.Queued;
                Journal(it, Tr.S("переключение на зеркало: ", "switching to a mirror: ") + DlLog.Redact(m));
                return true;
            }
            return false;
        }

        private void SaveOrder()
        {
            List<string> ids = new List<string>();
            foreach (DlItem it in _items) ids.Add(it.Id);
            _store.SaveOrder(ids);
        }

        // Остановить всё, сохранить: активные при следующем запуске продолжат сами.
        public void Dispose()
        {
            if (_disposed) return;
            List<IDlRun> running;
            lock (_lock)
            {
                _disposed = true;
                running = new List<IDlRun>(_active.Values);
                foreach (IDlRun tr in running)
                {
                    if (!_afterStop.ContainsKey(tr.Item.Id)) _afterStop[tr.Item.Id] = DlState.Queued;
                    tr.RequestStop();
                }
            }
            _wake.Set();
            if (_loop != null) _loop.Join(5000);
            foreach (IDlRun tr in running) tr.Join(15000);
            JoinMoves(15000);
            BtShutdown();
            lock (_lock)
            {
                CollectFinished();
                foreach (DlItem it in _items) _store.Save(it);
                SaveOrder();
            }
        }
    }
}
