// Windows Process Cleaner — «Загрузки»: модель загрузки, сегменты, журнал событий и хранилище на диске.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Каждая загрузка — свой файл items\<id>.json (атомарная запись с .bak), порядок — index.json. Смещения сегментов на диске
// никогда не опережают байты, которые уже сброшены в файл (Durable): после убитого процесса или пропавшего питания
// докачка начинается с места, где данные точно есть.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    internal enum DlState
    {
        Queued,      // ждёт места в очереди (или повтора после ошибки — NextRetryUtc)
        Scheduled,   // отложена до StartAtUtc
        Waiting,     // держит условие: простой ПК, расписание, лимитная сеть, батарея (причина — WaitReason)
        Active,
        Paused,
        Completed,
        Failed,
        NeedsLink,   // ссылка истекла или сервер отказал при докачке: нужна свежая ссылка, скачанное сохранено
        Checking,    // торрент: проверка данных на диске
        Seeding      // торрент: всё выбранное скачано и проверено, идёт раздача
    }

    internal enum DlErrorKind
    {
        None,
        Network,          // обрыв, таймаут, DNS — повторяем
        Server,           // 5xx — повторяем
        RateLimited,      // 429/503 с Retry-After — повторяем после паузы сервера
        Client,           // 4xx — не повторяем
        LinkExpired,      // 401/403/404/410 при докачке — NeedsLink
        Changed,          // файл на сервере другой (If-Range → 200, другой размер)
        Policy,           // схема, небезопасный редирект, лишние редиректы, путь
        Disk,             // нет места, нет доступа к папке
        HashMismatch,
        Blocked           // антивирус или политика вложений не пропустили файл
    }

    internal sealed class DlSegment
    {
        public long Start;
        public long End = -1;       // включительно; -1 — до конца потока (размер неизвестен)
        public long Done;           // байт записано в файл
        public long Durable;        // байт сброшено на диск (FlushFileBuffers) — это и хранится
        public bool Busy;           // сегмент качает поток (не хранится)

        public long Length { get { return End < 0 ? -1 : End - Start + 1; } }
        public bool Finished { get { return End >= 0 && Done >= End - Start + 1; } }
        public long Next { get { return Start + Done; } }
    }

    internal sealed class DlEvent
    {
        public DateTime Utc;
        public string Text;
    }

    internal sealed class DlItem
    {
        public const int MaxEvents = 100;

        public string Id = "";
        public string Url = "";                  // текущая ссылка (после «обновить ссылку» — новая)
        public string OriginalUrl = "";
        public string FinalUrl = "";             // после редиректов
        public readonly List<string> Redirects = new List<string>();
        public readonly List<string> Mirrors = new List<string>();
        public string Referrer = "";
        public string PageUrl = "";
        public string UserAgent = "";
        public string Cookies = "";              // только в памяти: в JSON не попадает никогда
        public string CookieHost = "";           // для какого хоста выданы cookies
        public string Source = "manual";         // manual | chrome | edge | yandex | firefox
        public string Folder = "";
        public string FileName = "";
        public bool NameFixed;                   // имя задал пользователь — не брать из ответа сервера
        public long Total = -1;
        public string ETag = "";
        public string LastModified = "";
        public bool AcceptRanges;
        public readonly List<DlSegment> Segments = new List<DlSegment>();
        public DlState State = DlState.Queued;
        public string WaitReason = "";
        public DlErrorKind ErrorKind = DlErrorKind.None;
        public string Error = "";
        public int Attempts;
        public DateTime NextRetryUtc = DateTime.MinValue;
        public int Priority;                     // 1 — высокий, 0 — обычный, -1 — низкий
        public int LimitKBps;                    // 0 — без собственного лимита
        public int Connections;                  // 0 — из настроек
        public DateTime StartAtUtc = DateTime.MinValue;
        public bool WhenIdle;
        public bool AllowHttpDowngrade;          // пользователь согласился на редирект https → http
        public string ExpectedHash = "";         // sha256:… | sha1:… | md5:…
        public string Sha256 = "";
        public DateTime AddedUtc = DateTime.MinValue;
        public DateTime CompletedUtc = DateTime.MinValue;
        public readonly List<DlEvent> Events = new List<DlEvent>();

        // Перенос или копирование в другую папку (Downloads.Relocate.cs). MoveTo пусто — ничего не переносится.
        public string MoveTo = "";
        public string MoveName = "";             // имя в новой папке, выбрано при старте переноса
        public bool MoveCopy;
        public string MoveError = "";            // чем закончилась последняя попытка; пусто — удачно или не было

        // Торрент (Downloads.Torrent.cs). Url — magnet-ссылка (пусто, если добавлен файлом), FileName — корень раздачи: папка
        // многофайлового торрента или сам файл. Метаданные и снимок для продолжения — рядом с записью, torrents\<InfoHash>.*.
        public string Kind = KindHttp;
        public string InfoHash = "";             // 40 hex, строчные
        public bool Sequential;
        public int[] FilePriorities;             // по файлам торрента: 0 — не качать, 1 — обычный, 2 — высокий; null — все
        public long Uploaded;                    // отдано за всё время
        // «Обновить раздачу» (Downloads.TorrentUpdate.cs): тема на сайте и новая версия, найденная там или пришедшая файлом.
        public string TopicUrl = "";             // каноническая ссылка на тему (BtTopic); пусто — неизвестна
        public string UpdateHash = "";           // info-hash новой версии; пусто — обновления нет
        public string UpdateDismissed = "";      // версия, от которой отказались: проверка по теме её больше не предложит
        public DateTime UpdateCheckedUtc = DateTime.MinValue;

        // Живые показатели (не хранятся).
        public long SpeedBps;
        public int ActiveConnections;
        public long MoveDone;
        public long TorrentDone;                 // байт выбранных файлов в проверенных кусках
        public long UpBps;
        public int Seeds;
        public double CheckProgress;

        public const string KindHttp = "http", KindTorrent = "torrent", KindMedia = "media";
        public DlMedia Media;                    // только у Kind = "media": выбор дорожек и счётчики (Media.Contracts.cs)

        public bool IsTorrent { get { return Kind == KindTorrent; } }
        public bool IsMedia { get { return Kind == KindMedia; } }
        public string TargetPath { get { return Path.Combine(Folder ?? "", FileName ?? ""); } }
        public string PartPath { get { return TargetPath + DlPaths.PartSuffix; } }

        public long DoneBytes
        {
            get
            {
                if (IsTorrent) return System.Threading.Interlocked.Read(ref TorrentDone);
                long sum = 0;
                lock (Segments) foreach (DlSegment s in Segments) sum += s.Done;
                return sum;
            }
        }

        public string Host
        {
            get
            {
                Uri u;
                return Uri.TryCreate(string.IsNullOrEmpty(FinalUrl) ? Url : FinalUrl, UriKind.Absolute, out u) ? u.Host.ToLowerInvariant() : "";
            }
        }

        public void Log(DateTime utc, string text)
        {
            lock (Events)
            {
                DlEvent e = new DlEvent();
                e.Utc = utc;
                e.Text = text ?? "";
                Events.Add(e);
                if (Events.Count > MaxEvents) Events.RemoveRange(0, Events.Count - MaxEvents);
            }
        }

        public static string NewId()
        {
            return DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        // Имя файла из id: только буквы, цифры и дефис — id из канала не может увести запись из папки хранилища.
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id) || id.Length > 64) return false;
            foreach (char c in id)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '-')) return false;
            return true;
        }

        public static bool IsHexHash(string s)
        {
            if (s == null || s.Length != 40) return false;
            foreach (char c in s)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f')) return false;
            return true;
        }

        // durable = true — на диск: смещения сегментов берутся из Durable. false — для клиента: живые Done.
        public JVal ToJson(bool durable)
        {
            JVal o = JVal.NewObj();
            o.Set("Id", DlJson.S(Id));
            o.Set("Url", DlJson.S(Url));
            o.Set("OriginalUrl", DlJson.S(OriginalUrl));
            o.Set("FinalUrl", DlJson.S(FinalUrl));
            o.Set("Redirects", DlJson.Strings(Redirects));
            o.Set("Mirrors", DlJson.Strings(Mirrors));
            o.Set("Referrer", DlJson.S(Referrer));
            o.Set("PageUrl", DlJson.S(PageUrl));
            o.Set("UserAgent", DlJson.S(UserAgent));
            o.Set("Source", DlJson.S(Source));
            o.Set("Folder", DlJson.S(Folder));
            o.Set("FileName", DlJson.S(FileName));
            o.Set("NameFixed", DlJson.B(NameFixed));
            o.Set("Total", DlJson.N(Total));
            o.Set("ETag", DlJson.S(ETag));
            o.Set("LastModified", DlJson.S(LastModified));
            o.Set("AcceptRanges", DlJson.B(AcceptRanges));
            JVal segs = JVal.NewArr();
            lock (Segments)
                foreach (DlSegment s in Segments)
                {
                    JVal a = JVal.NewArr();
                    a.V.Add(DlJson.N(s.Start));
                    a.V.Add(DlJson.N(s.End));
                    a.V.Add(DlJson.N(durable ? s.Durable : s.Done));
                    segs.V.Add(a);
                }
            o.Set("Segments", segs);
            o.Set("State", DlJson.S(State.ToString()));
            o.Set("WaitReason", DlJson.S(WaitReason));
            o.Set("ErrorKind", DlJson.S(ErrorKind.ToString()));
            o.Set("Error", DlJson.S(Error));
            o.Set("Attempts", DlJson.N(Attempts));
            o.Set("NextRetry", DlJson.D(NextRetryUtc));
            o.Set("Priority", DlJson.N(Priority));
            o.Set("LimitKBps", DlJson.N(LimitKBps));
            o.Set("Connections", DlJson.N(Connections));
            o.Set("StartAt", DlJson.D(StartAtUtc));
            o.Set("WhenIdle", DlJson.B(WhenIdle));
            o.Set("AllowHttpDowngrade", DlJson.B(AllowHttpDowngrade));
            o.Set("ExpectedHash", DlJson.S(ExpectedHash));
            o.Set("Sha256", DlJson.S(Sha256));
            o.Set("Added", DlJson.D(AddedUtc));
            o.Set("Completed", DlJson.D(CompletedUtc));
            o.Set("MoveTo", DlJson.S(MoveTo));
            o.Set("MoveName", DlJson.S(MoveName));
            o.Set("MoveCopy", DlJson.B(MoveCopy));
            o.Set("MoveError", DlJson.S(MoveError));
            o.Set("Kind", DlJson.S(Kind));
            o.Set("InfoHash", DlJson.S(InfoHash));
            o.Set("Sequential", DlJson.B(Sequential));
            if (FilePriorities != null)
            {
                JVal pr = JVal.NewArr();
                foreach (int p in FilePriorities) pr.V.Add(DlJson.N(p));
                o.Set("FilePriorities", pr);
            }
            o.Set("Uploaded", DlJson.N(System.Threading.Interlocked.Read(ref Uploaded)));
            if (IsTorrent)
            {
                o.Set("TopicUrl", DlJson.S(TopicUrl));
                o.Set("UpdateHash", DlJson.S(UpdateHash));
                o.Set("UpdateDismissed", DlJson.S(UpdateDismissed));
                o.Set("UpdateChecked", DlJson.D(UpdateCheckedUtc));
            }
            if (IsMedia) o.Set("Media", (Media ?? new DlMedia()).ToJson());
            JVal events = JVal.NewArr();
            lock (Events)
                foreach (DlEvent e in Events)
                {
                    JVal j = JVal.NewObj();
                    j.Set("t", DlJson.D(e.Utc));
                    j.Set("m", DlJson.S(e.Text));
                    events.V.Add(j);
                }
            o.Set("Events", events);
            if (!durable)
            {
                o.Set("Done", DlJson.N(DoneBytes));
                o.Set("Speed", DlJson.N(SpeedBps));
                o.Set("ActiveConnections", DlJson.N(ActiveConnections));
                o.Set("HasCookies", DlJson.B(!string.IsNullOrEmpty(Cookies)));
                o.Set("MoveDone", DlJson.N(System.Threading.Interlocked.Read(ref MoveDone)));
                if (IsTorrent)
                {
                    o.Set("UpSpeed", DlJson.N(UpBps));
                    o.Set("Seeds", DlJson.N(Seeds));
                    o.Set("CheckProgress", DlJson.S(CheckProgress.ToString("0.####", CultureInfo.InvariantCulture)));
                }
            }
            return o;
        }

        public static DlItem FromJson(JVal o)
        {
            if (o == null || o.Kind != JKind.Obj) return null;
            DlItem it = new DlItem();
            it.Id = DlJson.Str(o, "Id", "");
            if (!IsValidId(it.Id)) return null;
            it.Url = DlJson.Str(o, "Url", "");
            it.OriginalUrl = DlJson.Str(o, "OriginalUrl", it.Url);
            it.FinalUrl = DlJson.Str(o, "FinalUrl", "");
            it.Redirects.AddRange(DlJson.StrList(o, "Redirects"));
            it.Mirrors.AddRange(DlJson.StrList(o, "Mirrors"));
            it.Referrer = DlJson.Str(o, "Referrer", "");
            it.PageUrl = DlJson.Str(o, "PageUrl", "");
            it.UserAgent = DlJson.Str(o, "UserAgent", "");
            it.Source = DlJson.Str(o, "Source", "manual");
            it.Folder = DlJson.Str(o, "Folder", "");
            it.FileName = DlJson.Str(o, "FileName", "");
            it.NameFixed = DlJson.Bool(o, "NameFixed", false);
            it.Total = DlJson.Long(o, "Total", -1);
            it.ETag = DlJson.Str(o, "ETag", "");
            it.LastModified = DlJson.Str(o, "LastModified", "");
            it.AcceptRanges = DlJson.Bool(o, "AcceptRanges", false);
            JVal segs = o.Get("Segments");
            if (segs != null && segs.Kind == JKind.Arr)
                foreach (JVal a in segs.V)
                {
                    if (a.Kind != JKind.Arr || a.V.Count != 3) continue;
                    long start, end, done;
                    if (!long.TryParse(a.V[0].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out start)
                        || !long.TryParse(a.V[1].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out end)
                        || !long.TryParse(a.V[2].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out done)) continue;
                    if (start < 0 || done < 0 || (end >= 0 && done > end - start + 1)) continue;
                    DlSegment s = new DlSegment();
                    s.Start = start; s.End = end; s.Done = done; s.Durable = done;
                    it.Segments.Add(s);
                }
            it.State = DlJson.EnumOr(o, "State", DlState.Queued);
            it.WaitReason = DlJson.Str(o, "WaitReason", "");
            it.ErrorKind = DlJson.EnumOr(o, "ErrorKind", DlErrorKind.None);
            it.Error = DlJson.Str(o, "Error", "");
            it.Attempts = DlJson.Int(o, "Attempts", 0);
            it.NextRetryUtc = DlJson.Date(o, "NextRetry");
            it.Priority = DlJson.Clamp(DlJson.Int(o, "Priority", 0), -1, 1);
            it.LimitKBps = DlJson.Clamp(DlJson.Int(o, "LimitKBps", 0), 0, 10 * 1024 * 1024);
            it.Connections = DlJson.Clamp(DlJson.Int(o, "Connections", 0), 0, 16);
            it.StartAtUtc = DlJson.Date(o, "StartAt");
            it.WhenIdle = DlJson.Bool(o, "WhenIdle", false);
            it.AllowHttpDowngrade = DlJson.Bool(o, "AllowHttpDowngrade", false);
            it.ExpectedHash = DlJson.Str(o, "ExpectedHash", "");
            it.Sha256 = DlJson.Str(o, "Sha256", "");
            it.AddedUtc = DlJson.Date(o, "Added");
            it.CompletedUtc = DlJson.Date(o, "Completed");
            it.MoveTo = DlJson.Str(o, "MoveTo", "");
            it.MoveName = DlJson.Str(o, "MoveName", "");
            it.MoveCopy = DlJson.Bool(o, "MoveCopy", false);
            it.MoveError = DlJson.Str(o, "MoveError", "");
            string kind = DlJson.Str(o, "Kind", KindHttp);
            it.Kind = kind == KindTorrent ? KindTorrent : kind == KindMedia ? KindMedia : KindHttp;
            if (it.IsMedia) it.Media = DlMedia.FromJson(o.Get("Media"));
            it.InfoHash = DlJson.Str(o, "InfoHash", "").ToLowerInvariant();
            // Хеш — часть имени файлов рядом с записью: только 40 hex, иначе запись не торрент.
            if (it.IsTorrent && !IsHexHash(it.InfoHash)) return null;
            it.Sequential = DlJson.Bool(o, "Sequential", false);
            JVal prio = o.Get("FilePriorities");
            if (prio != null && prio.Kind == JKind.Arr)
            {
                it.FilePriorities = new int[prio.V.Count];
                for (int i = 0; i < prio.V.Count; i++)
                {
                    int p;
                    it.FilePriorities[i] = int.TryParse(prio.V[i].Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out p) ? Math.Max(0, Math.Min(2, p)) : 1;
                }
            }
            it.Uploaded = Math.Max(0, DlJson.Long(o, "Uploaded", 0));
            it.TopicUrl = BtTopic.Normalize(DlJson.Str(o, "TopicUrl", ""));
            it.UpdateHash = DlJson.Str(o, "UpdateHash", "").ToLowerInvariant();
            if (!IsHexHash(it.UpdateHash) || it.UpdateHash == it.InfoHash) it.UpdateHash = "";
            it.UpdateDismissed = DlJson.Str(o, "UpdateDismissed", "").ToLowerInvariant();
            if (!IsHexHash(it.UpdateDismissed)) it.UpdateDismissed = "";
            it.UpdateCheckedUtc = DlJson.Date(o, "UpdateChecked");
            JVal events = o.Get("Events");
            if (events != null && events.Kind == JKind.Arr)
                foreach (JVal j in events.V)
                {
                    DlEvent e = new DlEvent();
                    e.Utc = DlJson.Date(j, "t");
                    e.Text = DlJson.Str(j, "m", "");
                    it.Events.Add(e);
                }
            return it;
        }
    }

    // ------------------------------------------------------------------ //
    //  Хранилище: items\<id>.json + index.json
    // ------------------------------------------------------------------ //
    internal sealed class DlStore
    {
        private readonly string _dir;
        private readonly object _gate = new object();

        public DlStore(string dir) { _dir = dir; }

        public string Dir { get { return _dir; } }
        private string ItemsDir { get { return Path.Combine(_dir, "items"); } }
        private string IndexFile { get { return Path.Combine(_dir, "index.json"); } }
        private string ItemFile(string id) { return Path.Combine(ItemsDir, id + ".json"); }

        public List<DlItem> LoadAll()
        {
            lock (_gate)
            {
                List<DlItem> items = new List<DlItem>();
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                JVal index = DlPaths.ReadJson(IndexFile);
                foreach (string id in DlJson.StrList(index, "order"))
                {
                    if (!DlItem.IsValidId(id) || seen.Contains(id)) continue;
                    DlItem it = DlItem.FromJson(DlPaths.ReadJson(ItemFile(id)));
                    if (it == null || it.Id != id) continue;
                    seen.Add(id);
                    items.Add(it);
                }
                // Файлы, которых нет в индексе (индекс не успел записаться), — тоже загрузки.
                if (Directory.Exists(ItemsDir))
                    foreach (string file in Directory.GetFiles(ItemsDir, "*.json"))
                    {
                        string id = Path.GetFileNameWithoutExtension(file);
                        if (!DlItem.IsValidId(id) || seen.Contains(id)) continue;
                        DlItem it = DlItem.FromJson(DlPaths.ReadJson(file));
                        if (it == null || it.Id != id) continue;
                        seen.Add(id);
                        items.Add(it);
                    }
                return items;
            }
        }

        public void Save(DlItem item)
        {
            if (item == null || !DlItem.IsValidId(item.Id)) return;
            string text = Jsn.Write(item.ToJson(true));
            lock (_gate)
            {
                try { DlPaths.WriteAtomic(ItemFile(item.Id), text); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        public void SaveOrder(IEnumerable<string> ids)
        {
            JVal o = JVal.NewObj();
            o.Set("order", DlJson.Strings(ids));
            string text = Jsn.Write(o);
            lock (_gate)
            {
                try { DlPaths.WriteAtomic(IndexFile, text); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        public void Delete(string id)
        {
            if (!DlItem.IsValidId(id)) return;
            lock (_gate)
            {
                string file = ItemFile(id);
                foreach (string f in new[] { file, file + ".bak", file + ".tmp" })
                    try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { DlLog.Report(ex); }
            }
        }
    }
}
