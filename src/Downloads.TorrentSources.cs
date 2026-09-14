// Windows Process Cleaner — «Обновить раздачу»: где искать новую версию раздачи.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// BtTopic — ссылка на страницу раздачи из .torrent (comment, publisher-url) в каноническом виде: по ней новая версия,
// пришедшая из браузера, папки наблюдения или диалога, узнаётся как обновление уже скачанной, а не как чужой торрент.
// Ссылка на сайт целиком или короткий номер — не тема: ложное совпадение хуже пропущенного.
//
// BtRutracker — текущий info-hash тем rutracker без входа на сайт. Сайт закрыт проверкой Cloudflare, запросы API по одной
// теме с 2026-09 отвечают «Temporarily disabled» (сначала пробуем их: вернутся — возьмём), открыты только выгрузки по
// форумам static/pvc/f/<форум> (тема → [… info_hash …]). Номера форума в .torrent нет: он находится проходом по выгрузкам
// (форумы с большим числом раздач — первыми), только когда ПК простаивает, и запоминается; дальше — один запрос на форум
// с If-None-Match. Между запросами пауза; своё имя клиента (у Python-urllib API отвечает 403, пустое и своё проходят).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsProcessCleaner.Downloads
{
    internal static class BtTopic
    {
        private static readonly Regex UrlInText = new Regex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex Digits4 = new Regex(@"\d{4,}", RegexOptions.CultureInvariant);

        public static string FromMeta(BtMeta m)
        {
            if (m == null) return "";
            string t = Normalize(m.Comment);
            return t.Length > 0 ? t : Normalize(m.PublisherUrl);
        }

        // Каноническая ссылка на тему или "".
        public static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            Match found = UrlInText.Match(text);
            Uri u;
            if (!found.Success || !Uri.TryCreate(found.Value.TrimEnd('.', ',', ')', ';'), UriKind.Absolute, out u)) return "";
            string host = u.Host.ToLowerInvariant();
            if (host.StartsWith("www.", StringComparison.Ordinal)) host = host.Substring(4);
            string path = u.AbsolutePath.ToLowerInvariant();
            string id;
            if (Regex.IsMatch(host, @"^rutracker\.[a-z]+$") && path == "/forum/viewtopic.php" && (id = QueryNumber(u, "t")) != null)
                return "https://rutracker.org/forum/viewtopic.php?t=" + id;
            if (Regex.IsMatch(host, @"^nnm-?club\.[a-z]+$") && path == "/forum/viewtopic.php" && (id = QueryNumber(u, "t")) != null)
                return "https://nnmclub.to/forum/viewtopic.php?t=" + id;
            if (Regex.IsMatch(host, @"^kinozal\.[a-z]+$") && path == "/details.php" && (id = QueryNumber(u, "id")) != null)
                return "https://kinozal.tv/details.php?id=" + id;
            Match rutor = Regex.Match(path, @"^/torrent/(\d+)(/|$)");
            if (Regex.IsMatch(host, @"^rutor\.[a-z]+$") && rutor.Success)
                return "https://rutor.info/torrent/" + rutor.Groups[1].Value;
            // Другие сайты: адрес с длинным номером в пути или запросе — страница раздачи, а не сайт целиком.
            string pq = u.AbsolutePath + u.Query;
            if (!Digits4.IsMatch(pq)) return "";
            return u.Scheme.ToLowerInvariant() + "://" + host + (u.IsDefaultPort ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture)) + pq;
        }

        public static string SiteKey(string topicUrl)
        {
            if (string.IsNullOrEmpty(topicUrl)) return "";
            if (topicUrl.StartsWith("https://rutracker.org/", StringComparison.Ordinal)) return "rutracker";
            if (topicUrl.StartsWith("https://nnmclub.to/", StringComparison.Ordinal)) return "nnmclub";
            if (topicUrl.StartsWith("https://kinozal.tv/", StringComparison.Ordinal)) return "kinozal";
            if (topicUrl.StartsWith("https://rutor.info/", StringComparison.Ordinal)) return "rutor";
            return "";
        }

        // Номер темы rutracker из канонической ссылки или null.
        public static string RutrackerId(string topicUrl)
        {
            const string prefix = "https://rutracker.org/forum/viewtopic.php?t=";
            return SiteKey(topicUrl) == "rutracker" && topicUrl.StartsWith(prefix, StringComparison.Ordinal) ? topicUrl.Substring(prefix.Length) : null;
        }

        private static string QueryNumber(Uri u, string name)
        {
            foreach (string pair in u.Query.TrimStart('?').Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0 || !string.Equals(pair.Substring(0, eq), name, StringComparison.OrdinalIgnoreCase)) continue;
                string v = pair.Substring(eq + 1);
                return Regex.IsMatch(v, @"^\d{1,12}$") ? v.TrimStart('0') : null;
            }
            return null;
        }
    }

    internal sealed class BtRutracker
    {
        public string BaseUrl = "https://api.rutracker.cc/v1/";
        public int PauseMs = 500;
        public string UserAgent = "WindowsProcessCleaner";
        public Func<bool> Cancel = delegate { return false; };
        public Func<bool> MaySweep = delegate { return true; };
        public Func<DateTime> Now = delegate { return DateTime.UtcNow; };
        public int Requests;

        private readonly string _cacheFile;
        private JVal _cache;
        private DateTime _lastRequest = DateTime.MinValue;

        public BtRutracker(string cacheFile) { _cacheFile = cacheFile; }

        // Текущий хеш по темам (40 hex, верхний регистр). "" — темы нет ни в одной выгрузке (закрыта, поглощена).
        // Темы без ответа (форум ещё не найден, ПК не простаивал, сеть) в словарь не попадают. error — последняя ошибка сети.
        public Dictionary<string, string> Check(ICollection<string> topics, out string error)
        {
            error = null;
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> pending = new List<string>();
            foreach (string t in topics) if (Regex.IsMatch(t ?? "", @"^\d{1,12}$") && !pending.Contains(t)) pending.Add(t);
            if (pending.Count == 0) return result;
            LoadCache();
            // Темы, которых проход не нашёл ни в одном форуме, неделю не ищутся заново: проход — сотни запросов.
            foreach (string t in new List<string>(pending))
            {
                JVal e = Topic(t);
                long absent = e == null ? 0 : DlJson.Long(e, "absent", 0);
                if (absent > 0 && Now().Ticks - absent < TimeSpan.FromDays(7).Ticks) { result[t] = ""; pending.Remove(t); }
            }
            if (pending.Count == 0) return result;
            try
            {
                if (TryTopicApi(pending, result)) { SaveCache(); return result; }
                if (Cancel()) return result;

                // Известные форумы: по запросу на форум, неизменившаяся выгрузка (304) — ответ из кеша.
                Dictionary<string, List<string>> byForum = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (string t in pending)
                {
                    JVal e = Topic(t);
                    string f = e == null ? "" : e.GetStr("forum") ?? "";
                    if (f.Length == 0) continue;
                    List<string> l;
                    if (!byForum.TryGetValue(f, out l)) byForum[f] = l = new List<string>();
                    l.Add(t);
                }
                foreach (KeyValuePair<string, List<string>> kv in byForum)
                {
                    if (Cancel()) return result;
                    string body;
                    int status = FetchForum(kv.Key, true, out body);
                    foreach (string t in kv.Value)
                    {
                        if (status == 304) { result[t] = Topic(t).GetStr("hash") ?? ""; continue; }
                        string hash = status == 200 ? FindHash(body, t) : null;
                        if (hash != null) { SetTopic(t, kv.Key, hash); result[t] = hash; }
                        else if (status == 200) SetTopic(t, "", "");   // тему перенесли в другой форум — искать заново
                    }
                    if (status != 200 && status != 304) error = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
                }
                SaveCache();

                List<string> unknown = new List<string>();
                foreach (string t in pending) if (!result.ContainsKey(t)) unknown.Add(t);
                if (unknown.Count > 0 && error == null && MaySweep()) Sweep(unknown, result, ref error);
            }
            catch (WebException ex) { error = ex.Message; }
            catch (IOException ex) { error = ex.Message; }
            SaveCache();
            return result;
        }

        // ---------- запрос по темам (сейчас выключен на стороне сайта) ----------
        private bool TryTopicApi(List<string> topics, Dictionary<string, string> result)
        {
            string body;
            int status = Get("get_tor_hash?by=topic_id&val=" + string.Join(",", topics.ToArray()), null, out body);
            if (status != 200) return false;
            JVal root;
            try { root = Jsn.Parse(body); } catch { return false; }
            JVal res = root == null ? null : root.Get("result");
            if (res == null || res.Kind != JKind.Obj) return false;
            for (int i = 0; i < res.K.Count; i++)
            {
                if (!topics.Contains(res.K[i])) continue;
                JVal v = res.V[i];
                string hash = v != null && v.Kind == JKind.Str && Regex.IsMatch(v.Raw, "^[0-9A-Fa-f]{40}$") ? v.Raw.ToUpperInvariant() : "";
                result[res.K[i]] = hash;
            }
            return true;
        }

        // ---------- проход по выгрузкам форумов ----------
        private void Sweep(List<string> unknown, Dictionary<string, string> result, ref string error)
        {
            for (int round = 0; round < 2 && unknown.Count > 0; round++)
            {
                JVal sweep = _cache.Get("sweep");
                List<string> forums = Strings(sweep == null ? null : sweep.Get("forums"));
                if (forums.Count == 0 || sweep.Get("joined") == null)
                {
                    string list;
                    int st = Get("static/forum_size", null, out list);
                    forums = st == 200 ? ForumsBySize(list) : new List<string>();
                    if (forums.Count == 0) { error = Tr.S("список форумов rutracker недоступен", "the rutracker forum list is unavailable"); return; }
                    sweep = JVal.NewObj();
                    sweep.Set("forums", StrArr(forums));
                    sweep.Set("pos", Num(0));
                    sweep.Set("joined", JVal.NewObj());
                    _cache.Set("sweep", sweep);
                }
                JVal joined = sweep.Get("joined");
                int pos = DlJson.Int(sweep, "pos", 0);
                // С какого форума тема ищется: пришедшей посреди прохода первые форумы ещё предстоит просмотреть.
                foreach (string t in unknown) if (joined.Get(t) == null) joined.Set(t, Num(pos));
                List<string> left = new List<string>(unknown);
                while (pos < forums.Count && left.Count > 0)
                {
                    if (Cancel() || !MaySweep()) return;
                    string body;
                    int status = FetchForum(forums[pos], false, out body);
                    if (status != 200) { error = "HTTP " + status.ToString(CultureInfo.InvariantCulture); return; }
                    foreach (string t in new List<string>(left))
                    {
                        string hash = FindHash(body, t);
                        if (hash == null) continue;
                        SetTopic(t, forums[pos], hash);
                        result[t] = hash;
                        left.Remove(t);
                        joined.Remove(t);
                    }
                    pos++;
                    sweep.Set("pos", Num(pos));
                }
                if (left.Count == 0) return;   // проход остаётся на месте для следующих новых тем
                // Выгрузки кончились: темы, искавшиеся с первого форума, нигде нет; пришедшие позже — ещё раз с начала.
                List<string> again = new List<string>();
                foreach (string t in left)
                {
                    if (DlJson.Int(joined, t, 0) == 0) { SetAbsent(t); result[t] = ""; }
                    else again.Add(t);
                }
                _cache.Remove("sweep");
                unknown = again;
            }
        }

        private static JVal Num(long n) { return JVal.NewNum(n.ToString(CultureInfo.InvariantCulture)); }

        private static List<string> ForumsBySize(string body)
        {
            List<KeyValuePair<string, long>> list = new List<KeyValuePair<string, long>>();
            JVal root = Jsn.Parse(body);
            JVal res = root == null ? null : root.Get("result");
            if (res != null && res.Kind == JKind.Obj)
                for (int i = 0; i < res.K.Count; i++)
                {
                    JVal v = res.V[i];
                    long count = v != null && v.Kind == JKind.Arr && v.V.Count > 0 && v.V[0].Kind == JKind.Num ? long.Parse(v.V[0].Raw, CultureInfo.InvariantCulture) : 0;
                    if (count > 0 && Regex.IsMatch(res.K[i], @"^\d{1,9}$")) list.Add(new KeyValuePair<string, long>(res.K[i], count));
                }
            list.Sort(delegate(KeyValuePair<string, long> a, KeyValuePair<string, long> b)
            {
                int c = b.Value.CompareTo(a.Value);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            List<string> ids = new List<string>();
            foreach (KeyValuePair<string, long> kv in list) ids.Add(kv.Key);
            return ids;
        }

        // Хеш темы в выгрузке форума: ключ "<тема>":[ …, "<40 hex>", … ] — без разбора всего файла (до мегабайта на форум).
        internal static string FindHash(string body, string topic)
        {
            if (string.IsNullOrEmpty(body)) return null;
            string key = "\"" + topic + "\":[";
            int at = body.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return null;
            int depth = 0, end = -1;
            for (int i = at + key.Length - 1; i < body.Length; i++)
            {
                char c = body[i];
                if (c == '[') depth++;
                else if (c == ']' && --depth == 0) { end = i; break; }
            }
            if (end < 0) return null;
            Match m = Regex.Match(body.Substring(at + key.Length, end - at - key.Length), "\"([0-9A-Fa-f]{40})\"");
            return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
        }

        private int FetchForum(string forum, bool conditional, out string body)
        {
            JVal forums = _cache.Get("etags");
            if (forums == null) { forums = JVal.NewObj(); _cache.Set("etags", forums); }
            string etag = conditional ? forums.GetStr(forum) : null;
            string newTag;
            int status = Get("static/pvc/f/" + forum, etag, out body, out newTag);
            if (status == 200) forums.Set(forum, JVal.NewStr(newTag ?? ""));
            return status;
        }

        // ---------- HTTP ----------
        private int Get(string rel, string etag, out string body)
        {
            string tag;
            return Get(rel, etag, out body, out tag);
        }

        private int Get(string rel, string etag, out string body, out string newTag)
        {
            body = null;
            newTag = null;
            int wait = PauseMs - (int)(DateTime.UtcNow - _lastRequest).TotalMilliseconds;
            if (wait > 0 && _lastRequest != DateTime.MinValue) System.Threading.Thread.Sleep(Math.Min(wait, PauseMs));
            _lastRequest = DateTime.UtcNow;
            Requests++;
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(BaseUrl + rel);
            req.UserAgent = UserAgent;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            req.Timeout = 60000;
            req.ReadWriteTimeout = 60000;
            if (!string.IsNullOrEmpty(etag)) req.Headers[HttpRequestHeader.IfNoneMatch] = etag;
            try
            {
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    newTag = resp.Headers[HttpResponseHeader.ETag];
                    using (StreamReader r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
                    return (int)resp.StatusCode;
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp == null) throw;
                using (resp) return (int)resp.StatusCode;
            }
        }

        // ---------- кеш: torrents\rutracker.json ----------
        private void LoadCache()
        {
            if (_cache != null) return;
            try { _cache = DlPaths.ReadJson(_cacheFile); } catch { _cache = null; }
            if (_cache == null || _cache.Kind != JKind.Obj) _cache = JVal.NewObj();
        }

        private void SaveCache()
        {
            if (_cache == null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                DlPaths.WriteAtomic(_cacheFile, Jsn.Write(_cache));
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private JVal Topic(string t)
        {
            JVal topics = _cache.Get("topics");
            return topics == null ? null : topics.Get(t);
        }

        private void SetTopic(string t, string forum, string hash)
        {
            JVal topics = _cache.Get("topics");
            if (topics == null) { topics = JVal.NewObj(); _cache.Set("topics", topics); }
            JVal e = JVal.NewObj();
            e.Set("forum", JVal.NewStr(forum));
            e.Set("hash", JVal.NewStr(hash));
            topics.Set(t, e);
        }

        private void SetAbsent(string t)
        {
            SetTopic(t, "", "");
            Topic(t).Set("absent", Num(Now().Ticks));
        }

        private static List<string> Strings(JVal arr)
        {
            List<string> l = new List<string>();
            if (arr != null && arr.Kind == JKind.Arr) foreach (JVal v in arr.V) if (v.Kind == JKind.Str) l.Add(v.Raw);
            return l;
        }

        private static JVal StrArr(IEnumerable<string> items)
        {
            JVal a = JVal.NewArr();
            foreach (string s in items) a.V.Add(JVal.NewStr(s));
            return a;
        }
    }
}
