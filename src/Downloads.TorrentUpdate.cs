// SysDeck — «Загрузки», «Обновить раздачу»: новая версия торрента на месте прежней.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Новая версия узнаётся тремя путями: проверка темы rutracker раз в BtUpdateCheckHours (и сразу, когда трекер ответил
// «Torrent not registered»); .torrent той же темы из браузера или папки наблюдения — вместо второй записи; «Обновить из
// файла .torrent…». Хеш, найденный по теме, без файла: метаданные берёт у роя проба — magnet нового хеша с трекерами
// прежней версии, после метаданных сразу стоп. Новая версия лежит рядом с записью (torrents\update-<id>.torrent) до
// кнопки: сама замена — только по ней. Запись останавливается, план (Torrent.Update.cs) переносит данные, журнал
// torrents\update-<id>.json делает шаги повторяемыми — после падения процесса Start доводит замену до конца. Запись та же:
// id, папка, имя корня, счётчики отданного; меняются хеш, метаданные и выбор файлов (по соответствию файлов).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed partial class DlEngine
    {
        public const string TopicMatchAsk = "", TopicMatchUpdate = "update", TopicMatchAdd = "add";

        private readonly HashSet<string> _btUpdBlocked = new HashSet<string>();      // замена идёт или не доведена: запуск ждёт
        private readonly HashSet<string> _btUpdBusy = new HashSet<string>();         // замена выполняется сейчас
        private readonly Dictionary<string, BtTorrent> _btProbes = new Dictionary<string, BtTorrent>();   // id записи → проба
        private readonly Dictionary<string, DateTime> _btProbeAt = new Dictionary<string, DateTime>();    // когда начата последняя
        private readonly List<string> _btProbeWant = new List<string>();
        private readonly Dictionary<string, DateTime> _btNotRegAt = new Dictionary<string, DateTime>();
        private readonly HashSet<string> _btUpdForce = new HashSet<string>();        // проверить сейчас, не дожидаясь срока
        private readonly HashSet<string> _btTopicTried = new HashSet<string>();
        private readonly object _btUpdFiles = new object();                          // запись файлов новой версии
        private Thread _btUpdThread;
        private BtRutracker _btRutracker;
        private DateTime _btUpdNextRun = DateTime.MinValue, _btNotRegPollAt = DateTime.MinValue;

        // Для тестов: подменный API на петле, короткие сроки, простой ПК без ожидания.
        internal int BtUpdateCheckHours = 6;
        internal int BtProbeSeconds = 180;
        internal int BtProbeRetrySeconds = 3600;
        internal string BtRutrackerBase = null;
        internal int BtRutrackerPauseMs = 500;
        internal int BtNotRegPollSeconds = 30;
        internal Func<bool> BtSweepAllowed = null;

        private string BtUpdateFile(string id) { return Path.Combine(TorrentsDir, "update-" + id + ".torrent"); }
        private string BtUpdateJournal(string id) { return Path.Combine(TorrentsDir, "update-" + id + ".json"); }

        internal static BtMeta LoadUpdateMeta(string torrentsDir, string id, string hash)
        {
            if (!DlItem.IsValidId(id)) return null;
            return LoadMetaFile(Path.Combine(torrentsDir, "update-" + id + ".torrent"), hash);
        }

        // Из конструктора: запись с недоведённой заменой не стартует, пока Start её не доведёт.
        private void BtUpdateLoad()
        {
            foreach (DlItem it in _items)
                if (it.IsTorrent && File.Exists(BtUpdateJournal(it.Id))) _btUpdBlocked.Add(it.Id);
        }

        // ---------- поиск новой версии ----------
        // Под _lock из Tick: пора ли проверять темы, каким записям нужна проба метаданных.
        private void BtUpdateTickLocked(DlSettings s, DateTime now)
        {
            if (_disposed) return;
            if (_btUpdThread == null)
            {
                bool due = _btUpdForce.Count > 0;
                if (!due && now >= _btUpdNextRun)
                    foreach (DlItem it in _items)
                    {
                        if (!it.IsTorrent) continue;
                        if (it.TopicUrl.Length == 0 && !_btTopicTried.Contains(it.Id)) { due = true; break; }
                        if (s.BtUpdateCheck && BtTopic.RutrackerId(it.TopicUrl) != null && BtCheckDue(it, now)) { due = true; break; }
                    }
                if (due)
                {
                    _btUpdNextRun = now.AddMinutes(15);
                    Thread t = new Thread(BtUpdateCheckRun);
                    t.IsBackground = true;
                    t.Name = "wpc-dl-update-check";
                    _btUpdThread = t;
                    t.Start();
                }
            }
            if (_globalGate.Length > 0) return;
            foreach (DlItem it in _items)
            {
                if (!it.IsTorrent || it.UpdateHash.Length == 0 || _btProbes.ContainsKey(it.Id) || _btProbeWant.Contains(it.Id)
                    || _btUpdBlocked.Contains(it.Id)) continue;
                DateTime at;
                if (_btProbeAt.TryGetValue(it.Id, out at) && now >= at && (now - at).TotalSeconds < BtProbeRetrySeconds) continue;
                _btProbeAt[it.Id] = now;
                _btProbeWant.Add(it.Id);
            }
        }

        private bool BtCheckDue(DlItem it, DateTime now)
        {
            return now < it.UpdateCheckedUtc || (now - it.UpdateCheckedUtc).TotalHours >= BtUpdateCheckHours;
        }

        private void BtUpdateCheckRun()
        {
            try { BtUpdateCheckOnce(); }
            catch (Exception ex) { DlLog.Report(ex); }
            finally
            {
                lock (_lock) _btUpdThread = null;
                _wake.Set();
            }
        }

        // Один проход проверки (поток проверки; тесты зовут напрямую).
        internal void BtUpdateCheckOnce()
        {
            DlSettings s;
            HashSet<string> forced;
            List<string[]> legacy = new List<string[]>();
            DateTime now = _env.UtcNow;
            lock (_lock)
            {
                s = _settings;
                forced = new HashSet<string>(_btUpdForce);
                _btUpdForce.Clear();
                foreach (DlItem it in _items)
                    if (it.IsTorrent && it.TopicUrl.Length == 0 && _btTopicTried.Add(it.Id)) legacy.Add(new[] { it.Id, it.InfoHash });
            }
            // Записи прежних версий программы: тема — из файла торрента рядом.
            foreach (string[] l in legacy)
            {
                string topic = BtTopic.FromMeta(BtLoadMeta(l[1]));
                if (topic.Length == 0) continue;
                lock (_lock)
                {
                    DlItem it = FindLocked(l[0]);
                    if (it == null || it.InfoHash != l[1] || it.TopicUrl.Length > 0) continue;
                    it.TopicUrl = topic;
                    _store.Save(it);
                }
            }

            Dictionary<string, List<string>> byTopic = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            lock (_lock)
                foreach (DlItem it in _items)
                {
                    string topic = it.IsTorrent ? BtTopic.RutrackerId(it.TopicUrl) : null;
                    if (topic == null || _btUpdBlocked.Contains(it.Id)) continue;
                    if (!forced.Contains(it.Id) && !(s.BtUpdateCheck && BtCheckDue(it, now))) continue;
                    List<string> ids;
                    if (!byTopic.TryGetValue(topic, out ids)) byTopic[topic] = ids = new List<string>();
                    ids.Add(it.Id);
                }
            if (byTopic.Count == 0) return;

            string error;
            Dictionary<string, string> found = BtRutrackerClient().Check(byTopic.Keys, out error);
            lock (_lock)
            {
                DateTime at = _env.UtcNow;
                foreach (KeyValuePair<string, List<string>> kv in byTopic)
                {
                    string hash;
                    bool known = found.TryGetValue(kv.Key, out hash);
                    foreach (string id in kv.Value)
                    {
                        DlItem it = FindLocked(id);
                        if (it == null || BtTopic.RutrackerId(it.TopicUrl) != kv.Key) continue;
                        bool asked = forced.Contains(id);
                        if (!known)
                        {
                            if (asked) Journal(it, Tr.S("проверка обновления не закончена: ", "the update check did not finish: ")
                                                   + (error ?? Tr.S("форум темы ещё ищется — поиск идёт, пока компьютер простаивает",
                                                                    "the topic's forum is still being looked for — the search runs while the computer is idle")));
                            continue;
                        }
                        it.UpdateCheckedUtc = at;
                        hash = hash.ToLowerInvariant();
                        if (hash.Length == 0)
                        {
                            if (asked) Journal(it, Tr.S("тема раздачи не найдена на rutracker: закрыта или перенесена", "the torrent's topic is not found on rutracker: closed or moved"));
                        }
                        else if (hash == it.InfoHash || hash == it.UpdateHash || hash == it.UpdateDismissed)
                        {
                            if (asked) Journal(it, hash == it.InfoHash ? Tr.S("обновлений нет: на rutracker эта же версия", "no updates: rutracker has this same version")
                                                                       : Tr.S("на rutracker та же новая версия, что уже найдена", "rutracker has the same new version found before"));
                        }
                        else if (DlItem.IsHexHash(hash)) BtOfferUpdateLocked(it, hash, Tr.S("на rutracker", "on rutracker"), true);
                        _store.Save(it);
                    }
                }
            }
            _wake.Set();
        }

        private BtRutracker BtRutrackerClient()
        {
            BtRutracker rt = _btRutracker;
            if (rt != null) return rt;
            rt = new BtRutracker(Path.Combine(TorrentsDir, "rutracker.json"));
            if (!string.IsNullOrEmpty(BtRutrackerBase)) rt.BaseUrl = BtRutrackerBase;
            rt.PauseMs = BtRutrackerPauseMs;
            rt.Now = delegate { return _env.UtcNow; };
            rt.Cancel = delegate { return _disposed; };
            rt.MaySweep = delegate
            {
                Func<bool> test = BtSweepAllowed;
                if (test != null) return test();
                DlSettings s = Settings;
                return !_env.IsMetered() && !_env.OnBattery() && !_env.FullscreenBusy() && _env.InputIdleSeconds() >= Math.Max(1, s.IdleMinutes) * 60;
            };
            _btRutracker = rt;
            return rt;
        }

        // Новая версия найдена: запись помнит хеш, уведомление — если нашлась сама (не по действию пользователя).
        private void BtOfferUpdateLocked(DlItem it, string hash, string where, bool notify)
        {
            it.UpdateHash = hash;
            it.UpdateDismissed = "";
            _btProbeAt.Remove(it.Id);
            Journal(it, Tr.S("новая версия раздачи ", "a new version of the torrent ") + where + ": " + hash);
            _store.Save(it);
            _wake.Set();
            if (!notify) return;
            Action<DlNotice> handler = Notice;
            if (handler == null) return;
            DlNotice n = new DlNotice();
            n.Kind = DlNoticeKind.UpdateAvailable;
            n.Id = it.Id;
            n.Name = string.IsNullOrEmpty(it.FileName) ? it.InfoHash : it.FileName;
            n.Text = Tr.S("Ничего не заменено. Что изменится — «Обновить раздачу…» в меню записи; обновится только по кнопке.", "Nothing is replaced yet. What changes: “Update the torrent…” in the item's menu; it updates only by the button.");
            n.Source = it.Source ?? "";
            try { handler(n); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // Трекер ответил, что не знает хеш: раздачу на rutracker, скорее всего, перезалили — проверить тему сейчас.
        private void BtNotRegisteredPoll(DateTime now)
        {
            if (now >= _btNotRegPollAt && (now - _btNotRegPollAt).TotalSeconds < BtNotRegPollSeconds) return;
            _btNotRegPollAt = now;
            List<KeyValuePair<string, BtTorrent>> running = new List<KeyValuePair<string, BtTorrent>>();
            lock (_lock)
                foreach (KeyValuePair<string, BtTorrent> kv in _btRun)
                {
                    DlItem it = FindLocked(kv.Key);
                    if (it != null && BtTopic.RutrackerId(it.TopicUrl) != null) running.Add(kv);
                }
            foreach (KeyValuePair<string, BtTorrent> kv in running)
            {
                bool unknown = false;
                foreach (BtTrackerInfo tr in kv.Value.Trackers())
                    if (tr.Message.IndexOf("not registered", StringComparison.OrdinalIgnoreCase) >= 0
                        || tr.Message.IndexOf("unregistered torrent", StringComparison.OrdinalIgnoreCase) >= 0) unknown = true;
                if (!unknown) continue;
                lock (_lock)
                {
                    DlItem it = FindLocked(kv.Key);
                    DateTime last;
                    if (it == null || (_btNotRegAt.TryGetValue(kv.Key, out last) && now >= last && (now - last).TotalHours < 1)) continue;
                    _btNotRegAt[kv.Key] = now;
                    Journal(it, Tr.S("трекер не знает эту версию раздачи — проверка обновления на rutracker", "the tracker does not know this version of the torrent — checking rutracker for an update"));
                    _btUpdForce.Add(kv.Key);
                }
                _wake.Set();
            }
        }

        // ---------- проба метаданных (поток планировщика, вне _lock) ----------
        private void BtUpdateBackground(DlSettings s)
        {
            DateTime now = _env.UtcNow;
            BtNotRegisteredPoll(now);
            List<string> want;
            lock (_lock)
            {
                want = new List<string>(_btProbeWant);
                _btProbeWant.Clear();
            }
            foreach (string id in want) BtProbeStart(id, s);

            List<KeyValuePair<string, BtTorrent>> ended = new List<KeyValuePair<string, BtTorrent>>();
            BtSession ses;
            lock (_lock)
            {
                ses = _bt;
                foreach (KeyValuePair<string, BtTorrent> kv in _btProbes)
                {
                    DateTime at;
                    bool late = !_btProbeAt.TryGetValue(kv.Key, out at) || now < at || (now - at).TotalSeconds >= BtProbeSeconds;
                    if (late || kv.Value.State == BtTorrentState.Error) ended.Add(kv);
                }
                foreach (KeyValuePair<string, BtTorrent> kv in ended)
                {
                    _btProbes.Remove(kv.Key);
                    DlItem it = FindLocked(kv.Key);
                    if (it == null) continue;
                    string err = kv.Value.State == BtTorrentState.Error ? kv.Value.Error : "";
                    Journal(it, err.Length > 0 ? Tr.S("метаданные новой версии не получены: ", "the new version's metadata did not arrive: ") + err
                                               : Tr.S("метаданные новой версии не получены: у роя нет пиров — повтор через час", "the new version's metadata did not arrive: the swarm has no peers — retry in an hour"));
                }
            }
            if (ses != null) foreach (KeyValuePair<string, BtTorrent> kv in ended) ses.Remove(kv.Value);
        }

        private void BtProbeStart(string id, DlSettings s)
        {
            string hash, old, folder;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || it.UpdateHash.Length == 0 || _btProbes.ContainsKey(id) || _btUpdBlocked.Contains(id)) return;
                hash = it.UpdateHash;
                old = it.InfoHash;
                folder = string.IsNullOrEmpty(it.Folder) ? TorrentsDir : it.Folder;
            }
            // Файл новой версии уже есть — пробовать нечего.
            if (LoadUpdateMeta(TorrentsDir, id, hash) != null) return;
            string error;
            BtMagnet magnet = BtMagnet.Parse("magnet:?xt=urn:btih:" + hash, out error);
            BtTorrent t = null;
            if (magnet != null)
            {
                BtAddParams p = new BtAddParams();
                p.Magnet = magnet;
                p.MetadataOnly = true;
                p.Folder = folder;
                BtMeta oldMeta = BtLoadMeta(old);
                if (oldMeta != null)
                {
                    // Трекеры прежней версии (с ключом доступа): новая регистрируется на тех же.
                    p.ExtraTrackers = new List<List<string>>();
                    foreach (List<string> tier in oldMeta.Trackers) p.ExtraTrackers.Add(new List<string>(tier));
                }
                try { t = BtEnsureSession(s).Add(p, out error); }
                catch (Exception ex) { DlLog.Report(ex); error = ex.Message; }
            }
            bool orphan = false;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (t == null)
                {
                    if (it != null) Journal(it, Tr.S("метаданные новой версии не запрошены: ", "the new version's metadata was not requested: ") + error);
                    return;
                }
                if (it == null || it.UpdateHash != hash) orphan = true;
                else
                {
                    BtTorrent probe = t;
                    t.MetadataReceived = delegate { BtProbeDone(id, probe); };
                    _btProbes[id] = t;
                    Journal(it, Tr.S("получение метаданных новой версии", "fetching the new version's metadata"));
                }
            }
            if (orphan)
            {
                BtSession ses = TorrentSession;
                if (ses != null) ses.Remove(t);
            }
        }

        private void BtProbeDone(string id, BtTorrent t)
        {
            BtMeta m = t.Meta;
            BtSession ses;
            lock (_lock)
            {
                BtTorrent cur;
                if (!_btProbes.TryGetValue(id, out cur) || cur != t) return;
                _btProbes.Remove(id);
                ses = _bt;
            }
            if (ses != null) ses.Remove(t);
            if (m == null) return;
            lock (_btUpdFiles)
            {
                string old, topic;
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null || it.UpdateHash != m.HexHash) return;
                    old = it.InfoHash;
                    topic = it.TopicUrl;
                }
                // Словарь info из роя + трекеры и тема прежней версии: без них новая версия не нашла бы ни трекер, ни тему.
                BtMeta file = new BtMeta();
                file.InfoBytes = m.InfoBytes;
                BtMeta oldMeta = BtLoadMeta(old);
                if (oldMeta != null) foreach (List<string> tier in oldMeta.Trackers) if (tier.Count > 0) file.Trackers.Add(new List<string>(tier));
                file.Comment = oldMeta != null && BtTopic.FromMeta(oldMeta) == topic && oldMeta.Comment.Length > 0 ? oldMeta.Comment : topic;
                string why = null;
                try { BtWriteBytes(BtUpdateFile(id), file.BuildTorrentFile()); }
                catch (Exception ex) { why = ex.Message; }
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null) return;
                    Journal(it, why == null ? Tr.S("метаданные новой версии получены: ", "the new version's metadata arrived: ") + m.Name
                                            : Tr.S("метаданные новой версии не сохранены: ", "the new version's metadata was not saved: ") + why);
                }
            }
        }

        // ---------- новая версия файлом ----------
        // .torrent той же темы, что у записи с другим хешем. null — такой записи нет (или совпадений несколько — не угадываем).
        private string BtTopicMatchLocked(string topic, string hash)
        {
            string match = null;
            foreach (DlItem it in _items)
            {
                if (!it.IsTorrent || it.TopicUrl != topic || it.InfoHash == hash) continue;
                if (match != null) return null;
                match = it.Id;
            }
            return match;
        }

        // Новая версия пришла файлом (браузер, папка наблюдения, «Обновить из файла»). null — принята и ждёт кнопки.
        public string OfferUpdateFile(string id, byte[] bytes, bool notify)
        {
            if (bytes == null || bytes.Length == 0) return Tr.S("пустой файл .torrent", "an empty .torrent file");
            if (bytes.Length > BtMaxTorrentBytes) return Tr.S("файл .torrent больше 16 МБ", "the .torrent file is larger than 16 MB");
            string err;
            BtMeta meta = BtMeta.Parse(bytes, out err);
            if (meta == null) return Tr.S("файл .torrent не разобран: ", "the .torrent file is not recognised: ") + err;
            string hash = meta.HexHash;
            lock (_btUpdFiles)
            {
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null || !it.IsTorrent) return Tr.S("это не торрент", "this is not a torrent");
                    if (hash == it.InfoHash) return Tr.S("это та же версия раздачи, что уже в списке", "this is the same version of the torrent that is already listed");
                    if (BtDuplicateLocked(hash) != null) return Tr.S("эта версия раздачи уже есть в списке отдельной загрузкой", "this version of the torrent is already listed as a separate download");
                    if (_btUpdBlocked.Contains(id)) return Tr.S("раздача сейчас обновляется", "the torrent is being updated right now");
                }
                try { BtWriteBytes(BtUpdateFile(id), bytes); }
                catch (Exception ex) { return ex.Message; }
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null) return Tr.S("нет такой загрузки", "no such download");
                    if (it.TopicUrl.Length == 0) it.TopicUrl = BtTopic.FromMeta(meta);
                    if (it.UpdateHash != hash) BtOfferUpdateLocked(it, hash, Tr.S("из файла", "from a file"), notify);
                }
            }
            return null;
        }

        // ---------- команды ----------
        public bool CheckUpdateNow(string id, out string why)
        {
            why = null;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) { why = Tr.S("это не торрент", "this is not a torrent"); return false; }
                if (BtTopic.RutrackerId(it.TopicUrl) == null)
                {
                    why = it.TopicUrl.Length == 0 ? Tr.S("в торренте нет ссылки на тему — новую версию можно взять только файлом", "the torrent has no topic link — a new version can only come as a file")
                                                  : Tr.S("сама программа проверяет только rutracker — откройте страницу темы", "the program itself checks only rutracker — open the topic page");
                    return false;
                }
                _btUpdForce.Add(id);
                _btProbeAt.Remove(id);
                Journal(it, Tr.S("проверка обновления по запросу", "update check requested"));
            }
            _wake.Set();
            return true;
        }

        // Отказаться от найденной версии: проверка по теме её больше не предложит (следующую — предложит).
        public bool DismissUpdate(string id, out string why)
        {
            why = null;
            BtTorrent probe;
            BtSession ses;
            lock (_btUpdFiles)
            {
                lock (_lock)
                {
                    DlItem it = FindLocked(id);
                    if (it == null || !it.IsTorrent) { why = Tr.S("это не торрент", "this is not a torrent"); return false; }
                    if (_btUpdBlocked.Contains(id)) { why = Tr.S("раздача сейчас обновляется", "the torrent is being updated right now"); return false; }
                    if (it.UpdateHash.Length == 0) return true;
                    Journal(it, Tr.S("новая версия отклонена: ", "the new version was declined: ") + it.UpdateHash);
                    it.UpdateDismissed = it.UpdateHash;
                    it.UpdateHash = "";
                    _store.Save(it);
                    if (_btProbes.TryGetValue(id, out probe)) _btProbes.Remove(id);
                    _btProbeWant.Remove(id);
                    ses = _bt;
                }
                BtDeleteUpdateFiles(id, false);
            }
            if (probe != null && ses != null) ses.Remove(probe);
            return true;
        }

        private void BtDeleteUpdateFiles(string id, bool journalToo)
        {
            if (!DlItem.IsValidId(id)) return;
            try
            {
                string file = BtUpdateFile(id);
                foreach (string f in new[] { file, file + ".tmp" }) if (File.Exists(f)) File.Delete(f);
                if (journalToo)
                {
                    string j = BtUpdateJournal(id);
                    foreach (string f in new[] { j, j + ".tmp" }) if (File.Exists(f)) File.Delete(f);
                }
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // Удаление записи: проба снимается, файлы новой версии и журнал замены уходят вместе с ней.
        private void BtUpdateForget(string id)
        {
            BtTorrent probe;
            BtSession ses;
            lock (_lock)
            {
                if (_btProbes.TryGetValue(id, out probe)) _btProbes.Remove(id);
                _btProbeWant.Remove(id);
                _btProbeAt.Remove(id);
                _btUpdForce.Remove(id);
                _btUpdBlocked.Remove(id);
                ses = _bt;
            }
            if (probe != null && ses != null) ses.Remove(probe);
            BtDeleteUpdateFiles(id, true);
        }

        // Для окна: что известно о новой версии и что изменит замена.
        public JVal UpdateJson(string id)
        {
            string hash, update, folder, root, topic;
            int[] prio;
            JVal o = JVal.NewObj();
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) return null;
                hash = it.InfoHash;
                update = it.UpdateHash;
                folder = it.Folder ?? "";
                root = it.FileName ?? "";
                topic = it.TopicUrl;
                prio = it.FilePriorities;
                o.Set("topicUrl", DlJson.S(topic));
                o.Set("site", DlJson.S(BtTopic.SiteKey(topic)));
                o.Set("updateHash", DlJson.S(update));
                o.Set("checked", DlJson.D(it.UpdateCheckedUtc));
                o.Set("probing", DlJson.B(_btProbes.ContainsKey(id)));
                o.Set("checking", DlJson.B(_btUpdThread != null || _btUpdForce.Contains(id)));
                o.Set("busy", DlJson.B(_btUpdBlocked.Contains(id)));
            }
            if (update.Length == 0) return o;
            BtMeta oldMeta = BtLoadMeta(hash), newMeta = LoadUpdateMeta(TorrentsDir, id, update);
            o.Set("ready", DlJson.B(newMeta != null));
            if (newMeta == null) return o;
            o.Set("name", DlJson.S(newMeta.Name));
            BtUpdatePlan plan = BtUpdatePlan.Build(oldMeta, newMeta);
            if (plan.Refusal == null && root.Length > 0)
            {
                using (BtStorage oldSt = new BtStorage(oldMeta, folder, root))
                using (BtStorage newSt = new BtStorage(newMeta, folder, root))
                {
                    string why = oldSt.Validate() ?? newSt.Validate();
                    if (why != null) plan.Refusal = why;
                    else
                    {
                        List<string> taken = BtUpdateDisk.Collisions(plan, oldSt, newSt);
                        if (taken.Count > 0) o.Set("collisions", DlJson.Strings(taken));
                    }
                }
            }
            if (plan.Refusal != null) o.Set("refusal", DlJson.S(plan.Refusal));
            int[] carried = plan.CarryPriorities(prio);
            JVal files = JVal.NewArr();
            foreach (BtUpdateEntry e in plan.Files)
            {
                JVal f = JVal.NewObj();
                BtFile nf = newMeta.Files[e.NewIndex];
                f.Set("path", DlJson.S(nf.RelPath));
                f.Set("size", DlJson.N(nf.Length));
                f.Set("action", DlJson.S(e.Action.ToString()));
                if (e.OldIndex >= 0 && e.Action == BtUpdateAction.Move) f.Set("from", DlJson.S(oldMeta.Files[e.OldIndex].RelPath));
                if (e.Identical) f.Set("identical", DlJson.B(true));
                if (carried != null && carried[e.NewIndex] == 0) f.Set("skipped", DlJson.B(true));
                files.V.Add(f);
            }
            o.Set("files", files);
            JVal removed = JVal.NewArr();
            foreach (int i in plan.Removed)
            {
                JVal f = JVal.NewObj();
                f.Set("path", DlJson.S(oldMeta.Files[i].RelPath));
                f.Set("size", DlJson.N(oldMeta.Files[i].Length));
                removed.V.Add(f);
            }
            o.Set("removed", removed);
            o.Set("keepBytes", DlJson.N(plan.KeepBytes));
            o.Set("moveBytes", DlJson.N(plan.MoveBytes));
            o.Set("changedBytes", DlJson.N(plan.ChangedBytes));
            o.Set("newBytes", DlJson.N(plan.NewBytes));
            o.Set("removedBytes", DlJson.N(plan.RemovedBytes));
            o.Set("identicalBytes", DlJson.N(plan.IdenticalBytes));
            return o;
        }

        // ---------- замена ----------
        public bool ApplyUpdate(string id, out string why)
        {
            why = null;
            string hash, update, folder, root;
            int[] prio;
            DlState after;
            bool resumeJournal;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null || !it.IsTorrent) { why = Tr.S("это не торрент", "this is not a torrent"); return false; }
                if (_btUpdBusy.Contains(id)) { why = Tr.S("раздача уже обновляется", "the torrent is already being updated"); return false; }
                resumeJournal = _btUpdBlocked.Contains(id);
                if (!resumeJournal)
                {
                    if (it.UpdateHash.Length == 0) { why = Tr.S("новой версии нет", "there is no new version"); return false; }
                    if (string.IsNullOrEmpty(it.FileName)) { why = Tr.S("у раздачи ещё нет файлов на диске", "the torrent has no files on disk yet"); return false; }
                    if (BtDuplicateLocked(it.UpdateHash) != null) { why = Tr.S("эта версия раздачи уже есть в списке отдельной загрузкой", "this version of the torrent is already listed as a separate download"); return false; }
                }
                _btUpdBusy.Add(id);
                _btUpdBlocked.Add(id);
                if (_btRun.ContainsKey(id) || _btStarting.Contains(id)) _btStop[id] = DlState.Paused;
                hash = it.InfoHash;
                update = it.UpdateHash;
                folder = it.Folder ?? "";
                root = it.FileName ?? "";
                prio = it.FilePriorities == null ? null : (int[])it.FilePriorities.Clone();
                after = it.State == DlState.Paused || it.State == DlState.Failed || it.State == DlState.Scheduled ? it.State : DlState.Queued;
            }
            _wake.Set();
            try
            {
                if (resumeJournal) return BtUpdateFinish(id, out why);
                int deadline = Environment.TickCount + 20000;
                while (true)
                {
                    lock (_lock)
                        if (!_btRun.ContainsKey(id) && !_btStarting.Contains(id)) break;
                    if (Environment.TickCount - deadline > 0) { why = Tr.S("торрент не остановился", "the torrent did not stop"); return BtUpdateAbort(id, after); }
                    Thread.Sleep(50);
                }
                BtMeta oldMeta = BtLoadMeta(hash), newMeta = LoadUpdateMeta(TorrentsDir, id, update);
                if (newMeta == null) { why = Tr.S("метаданные новой версии ещё не получены", "the new version's metadata has not arrived yet"); return BtUpdateAbort(id, after); }
                BtUpdatePlan plan = BtUpdatePlan.Build(oldMeta, newMeta);
                if (plan.Refusal != null) { why = plan.Refusal; return BtUpdateAbort(id, after); }
                bool[] intact;
                using (BtStorage oldSt = new BtStorage(oldMeta, folder, root))
                using (BtStorage newSt = new BtStorage(newMeta, folder, root))
                {
                    why = oldSt.Validate() ?? newSt.Validate();
                    if (why != null) return BtUpdateAbort(id, after);
                    List<string> taken = BtUpdateDisk.Collisions(plan, oldSt, newSt);
                    if (taken.Count > 0)
                    {
                        why = Tr.S("места файлов новой версии заняты чужими файлами: ", "the new version's file places hold foreign files: ") + string.Join("; ", taken.GetRange(0, Math.Min(3, taken.Count)).ToArray())
                              + (taken.Count > 3 ? " (+" + (taken.Count - 3).ToString(CultureInfo.InvariantCulture) + ")" : "");
                        return BtUpdateAbort(id, after);
                    }
                    intact = BtUpdateDisk.Intact(oldSt, BtResume.Load(BtResume.FileFor(TorrentsDir, hash)));
                }
                // Решение принято — дальше только вперёд: журнал позволяет довести замену после падения процесса.
                JVal j = JVal.NewObj();
                j.Set("old", DlJson.S(hash));
                j.Set("new", DlJson.S(update));
                j.Set("folder", DlJson.S(folder));
                j.Set("root", DlJson.S(root));
                if (prio != null)
                {
                    JVal pr = JVal.NewArr();
                    foreach (int p in prio) pr.V.Add(DlJson.N(p));
                    j.Set("prio", pr);
                }
                JVal ok = JVal.NewArr();
                foreach (bool b in intact) ok.V.Add(DlJson.B(b));
                j.Set("intact", ok);
                j.Set("after", DlJson.S(after.ToString()));
                try
                {
                    Directory.CreateDirectory(TorrentsDir);
                    DlPaths.WriteAtomic(BtUpdateJournal(id), Jsn.Write(j));
                }
                catch (Exception ex) { why = ex.Message; return BtUpdateAbort(id, after); }
                return BtUpdateFinish(id, out why);
            }
            finally
            {
                lock (_lock) _btUpdBusy.Remove(id);
                _wake.Set();
            }
        }

        private bool BtUpdateAbort(string id, DlState after)
        {
            lock (_lock)
            {
                _btUpdBlocked.Remove(id);
                DlItem it = FindLocked(id);
                if (it != null && it.State == DlState.Paused && after != DlState.Paused)
                {
                    it.State = after;
                    _store.Save(it);
                }
            }
            return false;
        }

        // Шаги по журналу: повторяемы, после падения на любом из них следующий проход доделывает несделанное.
        private bool BtUpdateFinish(string id, out string why)
        {
            why = null;
            JVal j;
            try { j = DlPaths.ReadJson(BtUpdateJournal(id)); }
            catch (Exception ex) { j = null; why = ex.Message; }
            string old = j == null ? "" : DlJson.Str(j, "old", ""), update = j == null ? "" : DlJson.Str(j, "new", "");
            string folder = j == null ? "" : DlJson.Str(j, "folder", ""), root = j == null ? "" : DlJson.Str(j, "root", "");
            DlState after = j == null ? DlState.Paused : DlJson.EnumOr(j, "after", DlState.Queued);
            if (!DlItem.IsHexHash(old) || !DlItem.IsHexHash(update) || root.Length == 0)
            {
                BtDeleteUpdateFiles(id, true);
                lock (_lock) _btUpdBlocked.Remove(id);
                why = why ?? Tr.S("журнал обновления испорчен — раздача не менялась", "the update journal is damaged — the torrent was not changed");
                return false;
            }
            string current;
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                current = it == null ? null : it.InfoHash;
            }
            if (current == null) { BtUpdateForget(id); BtDeleteSideFiles(update); return false; }
            if (current == update)
            {
                // Запись уже на новой версии: остались только уборка файлов прежней и журнала.
                BtDeleteSideFiles(old);
                BtDeleteUpdateFiles(id, true);
                lock (_lock) _btUpdBlocked.Remove(id);
                return true;
            }

            BtMeta oldMeta = BtLoadMeta(old), newMeta = LoadUpdateMeta(TorrentsDir, id, update) ?? BtLoadMeta(update);
            BtUpdatePlan plan = BtUpdatePlan.Build(oldMeta, newMeta);
            if (plan.Refusal != null)
            {
                why = Tr.S("обновление не доведено: ", "the update was not completed: ") + plan.Refusal;
                BtUpdateJournalLine(id, why);
                return false;
            }
            int[] prio = null;
            JVal pr = j.Get("prio");
            if (pr != null && pr.Kind == JKind.Arr)
            {
                prio = new int[pr.V.Count];
                for (int i = 0; i < prio.Length; i++)
                {
                    int p;
                    prio[i] = int.TryParse(pr.V[i].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) ? p : 1;
                }
            }
            JVal ok = j.Get("intact");
            bool[] intact = new bool[oldMeta.Files.Count];
            if (ok != null && ok.Kind == JKind.Arr)
                for (int i = 0; i < intact.Length && i < ok.V.Count; i++) intact[i] = ok.V[i].Kind == JKind.Bool && ok.V[i].B;
            int[] carried = plan.CarryPriorities(prio);
            try
            {
                using (BtStorage oldSt = new BtStorage(oldMeta, folder, root))
                using (BtStorage newSt = new BtStorage(newMeta, folder, root))
                {
                    why = oldSt.Validate() ?? newSt.Validate() ?? BtUpdateDisk.Apply(plan, oldSt, newSt);
                    if (why != null)
                    {
                        why = Tr.S("обновление не доведено: ", "the update was not completed: ") + why;
                        BtUpdateJournalLine(id, why + Tr.S(" — повтор кнопкой «Обновить» или при следующем запуске", " — retry with «Update» or at the next start"));
                        return false;
                    }
                    BtResume trust = BtUpdateDisk.TrustIdentical(plan, newSt, intact, carried);
                    BtWriteBytes(Path.Combine(TorrentsDir, update + ".torrent"), newMeta.BuildTorrentFile());
                    BtDeleteResume(update);
                    if (trust != null) trust.Save(BtResume.FileFor(TorrentsDir, update));
                }
            }
            catch (Exception ex)
            {
                why = Tr.S("обновление не доведено: ", "the update was not completed: ") + ex.Message;
                BtUpdateJournalLine(id, why);
                return false;
            }

            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it != null)
                {
                    it.InfoHash = update;
                    it.UpdateHash = "";
                    it.UpdateDismissed = "";
                    it.FilePriorities = carried;
                    it.Total = newMeta.TotalSize;
                    Interlocked.Exchange(ref it.TorrentDone, 0);
                    it.CompletedUtc = DateTime.MinValue;
                    it.State = after;
                    it.WaitReason = "";
                    it.ErrorKind = DlErrorKind.None;
                    it.Error = "";
                    it.NextRetryUtc = DateTime.MinValue;
                    string key = Path.Combine(it.Folder ?? "", it.FileName ?? "");
                    if (!_reserved.ContainsKey(key)) _reserved[key] = id;
                    Journal(it, Tr.S("раздача обновлена: ", "the torrent was updated: ") + BtUpdateSummary(plan) + " — " + update);
                    _store.Save(it);
                }
            }
            BtDeleteSideFiles(old);
            BtDeleteUpdateFiles(id, true);
            lock (_lock) _btUpdBlocked.Remove(id);
            return true;
        }

        private void BtUpdateJournalLine(string id, string text)
        {
            lock (_lock)
            {
                DlItem it = FindLocked(id);
                if (it == null) return;
                Journal(it, text);
                _store.Save(it);
            }
        }

        internal static string BtUpdateSummary(BtUpdatePlan plan)
        {
            List<string> parts = new List<string>();
            int keep = plan.Count(BtUpdateAction.Keep), move = plan.Count(BtUpdateAction.Move);
            int changed = plan.Count(BtUpdateAction.Changed), added = plan.Count(BtUpdateAction.New);
            if (keep > 0) parts.Add(Tr.S("без изменений ", "unchanged ") + keep.ToString(CultureInfo.InvariantCulture) + " (" + Engine.FormatBytes(plan.KeepBytes) + ")");
            if (move > 0) parts.Add(Tr.S("перенесено ", "moved ") + move.ToString(CultureInfo.InvariantCulture) + " (" + Engine.FormatBytes(plan.MoveBytes) + ")");
            if (changed > 0) parts.Add(Tr.S("изменено ", "changed ") + changed.ToString(CultureInfo.InvariantCulture) + " (" + Engine.FormatBytes(plan.ChangedBytes) + ")");
            if (added > 0) parts.Add(Tr.S("новых ", "new ") + added.ToString(CultureInfo.InvariantCulture) + " (" + Engine.FormatBytes(plan.NewBytes) + ")");
            if (plan.Removed.Count > 0) parts.Add(Tr.S("в Корзину ", "to the Recycle Bin ") + plan.Removed.Count.ToString(CultureInfo.InvariantCulture) + " (" + Engine.FormatBytes(plan.RemovedBytes) + ")");
            return parts.Count == 0 ? Tr.S("файлов нет", "no files") : string.Join(", ", parts.ToArray());
        }

        // Из Start, до планировщика: замены, прерванные падением процесса, доводятся до конца.
        private void BtUpdateRollForward()
        {
            List<string> ids;
            lock (_lock) ids = new List<string>(_btUpdBlocked);
            foreach (string id in ids)
            {
                string why;
                lock (_lock) _btUpdBusy.Add(id);
                try
                {
                    if (!BtUpdateFinish(id, out why) && why != null) DlLog.Write(id + " update roll-forward: " + why);
                }
                catch (Exception ex) { DlLog.Report(ex); }
                finally { lock (_lock) _btUpdBusy.Remove(id); }
            }
        }
    }
}
