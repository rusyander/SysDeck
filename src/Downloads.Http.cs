// Windows Process Cleaner — «Загрузки»: HTTP(S) — первый запрос, редиректы, имя из заголовков, сегменты и докачка.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Первый запрос — GET с «Range: bytes=0-», без HEAD (многие серверы отвечают на HEAD не так, как на GET). 206 с
// Content-Range — диапазоны есть, файл делится на сегменты; 200 — один поток. Сжатие выключено: у сжатого ответа
// диапазоны не имеют смысла. Редиректы обрабатываются вручную: каждый шаг проверяет схему, https → http требует
// согласия, cookies не уходят на другой хост. Докачка шлёт If-Range: если файл на сервере другой, придёт 200 вместо
// 206 — загрузка останавливается с ясной причиной, а не склеивает два разных файла.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class DlFailure
    {
        public DlErrorKind Kind;
        public string Message = "";
        public int RetryAfterSeconds;
        public int Status;

        public static DlFailure Make(DlErrorKind kind, string message)
        {
            DlFailure f = new DlFailure();
            f.Kind = kind;
            f.Message = message ?? "";
            return f;
        }

        public bool Retryable
        {
            get { return Kind == DlErrorKind.Network || Kind == DlErrorKind.Server || Kind == DlErrorKind.RateLimited; }
        }
    }

    // ------------------------------------------------------------------ //
    //  Разбор заголовков и классификация ответов — без сети
    // ------------------------------------------------------------------ //
    internal static class DlHttp
    {
        public const int MaxRedirects = 10;

        static DlHttp()
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)(3072 | 12288); }
            catch { try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { } }
            // Иначе клиенту .NET на один хост положено два соединения, и сегменты стояли бы в очереди друг за другом.
            if (ServicePointManager.DefaultConnectionLimit < 64) ServicePointManager.DefaultConnectionLimit = 64;
        }

        public static void Init() { }

        public static bool IsAllowedScheme(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
                   && !string.IsNullOrEmpty(u.Host) && !u.IsUnc && !u.IsFile;
        }

        // RFC 6266: filename* (RFC 5987, charset'lang'%XX) важнее filename. Сервер, приславший имя в сыром UTF-8,
        // приходит сюда «латиницей» (байт = символ) — такое имя перекодируется. Имя с %XX раскодируется, как в браузере.
        public static string ContentDispositionName(string header)
        {
            if (string.IsNullOrEmpty(header)) return "";
            string star = null, plain = null;
            foreach (string part in SplitParams(header))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string name = part.Substring(0, eq).Trim().ToLowerInvariant();
                string value = part.Substring(eq + 1).Trim();
                if (name == "filename*") star = DecodeExtValue(value);
                else if (name == "filename") plain = Unquote(value);
            }
            if (!string.IsNullOrEmpty(star)) return star;
            if (string.IsNullOrEmpty(plain)) return "";
            string fixedUtf8 = FromLatin1Utf8(plain);
            if (fixedUtf8 != null) return fixedUtf8;
            if (plain.IndexOf('%') >= 0)
            {
                string decoded = PercentDecode(plain, new UTF8Encoding(false, true));
                if (decoded != null) return decoded;
            }
            return plain;
        }

        private static List<string> SplitParams(string header)
        {
            List<string> list = new List<string>();
            StringBuilder sb = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < header.Length; i++)
            {
                char c = header[i];
                if (c == '"') quoted = !quoted;
                else if (c == '\\' && quoted && i + 1 < header.Length) { sb.Append(c); sb.Append(header[++i]); continue; }
                if (c == ';' && !quoted) { list.Add(sb.ToString()); sb.Length = 0; continue; }
                sb.Append(c);
            }
            list.Add(sb.ToString());
            return list;
        }

        private static string Unquote(string v)
        {
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"')
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 1; i < v.Length - 1; i++)
                {
                    if (v[i] == '\\' && i + 1 < v.Length - 1) i++;
                    sb.Append(v[i]);
                }
                return sb.ToString();
            }
            return v;
        }

        private static string DecodeExtValue(string v)
        {
            v = Unquote(v);
            int a = v.IndexOf('\'');
            int b = a < 0 ? -1 : v.IndexOf('\'', a + 1);
            if (a < 0 || b < 0) return null;
            string charset = v.Substring(0, a).Trim();
            Encoding enc;
            try { enc = string.Equals(charset, "utf-8", StringComparison.OrdinalIgnoreCase) ? new UTF8Encoding(false, true) : Encoding.GetEncoding(charset); }
            catch { return null; }
            return PercentDecode(v.Substring(b + 1), enc);
        }

        private static string PercentDecode(string s, Encoding enc)
        {
            List<byte> bytes = new List<byte>();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '%' && i + 2 < s.Length && IsHex(s[i + 1]) && IsHex(s[i + 2]))
                {
                    bytes.Add(byte.Parse(s.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    i += 2;
                }
                else if (c < 0x80) bytes.Add((byte)c);
                else return null;
            }
            try { return enc.GetString(bytes.ToArray()); }
            catch { return null; }
        }

        private static bool IsHex(char c) { return c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F'; }

        // Все символы ≤ 0xFF, есть хоть один ≥ 0x80, и байты — корректный UTF-8: значит, это UTF-8, прочитанный как Latin-1.
        private static string FromLatin1Utf8(string s)
        {
            bool high = false;
            foreach (char c in s)
            {
                if (c > 0xFF) return null;
                if (c >= 0x80) high = true;
            }
            if (!high) return null;
            byte[] bytes = new byte[s.Length];
            for (int i = 0; i < s.Length; i++) bytes[i] = (byte)s[i];
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch { return null; }
        }

        // Шаг редиректа: только http/https, и с https на http — лишь с согласия пользователя. null — можно.
        public static DlFailure CheckRedirect(string from, string to, bool allowDowngrade)
        {
            if (!IsAllowedScheme(to))
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("редирект на недопустимую схему: ", "redirect to a disallowed scheme: ") + DlLog.Redact(to));
            if (from.StartsWith("https:", StringComparison.OrdinalIgnoreCase) && to.StartsWith("http:", StringComparison.OrdinalIgnoreCase) && !allowDowngrade)
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("сервер перенаправляет с https на незащищённый http — нужно согласие",
                                                               "the server redirects from https to plain http — consent is needed"));
            return null;
        }

        // «bytes 0-99/1234», «bytes 0-99/*» (total = -1).
        public static bool TryParseContentRange(string header, out long start, out long end, out long total)
        {
            start = end = total = -1;
            if (string.IsNullOrEmpty(header)) return false;
            string h = header.Trim();
            if (!h.StartsWith("bytes", StringComparison.OrdinalIgnoreCase)) return false;
            h = h.Substring(5).Trim();
            int dash = h.IndexOf('-'), slash = h.IndexOf('/');
            if (dash <= 0 || slash <= dash) return false;
            if (!long.TryParse(h.Substring(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out start)) return false;
            if (!long.TryParse(h.Substring(dash + 1, slash - dash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out end)) return false;
            string t = h.Substring(slash + 1).Trim();
            if (t != "*" && !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out total)) return false;
            return end >= start && (total < 0 || end < total);
        }

        // Retry-After: секунды или HTTP-дата. 0 — нет или непонятно.
        public static int RetryAfterSeconds(string header, DateTime utcNow)
        {
            if (string.IsNullOrEmpty(header)) return 0;
            int s;
            if (int.TryParse(header.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out s)) return Math.Max(0, Math.Min(s, 24 * 3600));
            DateTime d;
            if (DateTime.TryParse(header.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return (int)Math.Max(0, Math.Min(24 * 3600, (d - utcNow).TotalSeconds));
            return 0;
        }

        // resuming — у загрузки уже есть скачанные байты: 401/403/404/410 значат «ссылка истекла», а не «ошибка навсегда».
        public static DlFailure Classify(int status, bool resuming, string retryAfter, DateTime utcNow)
        {
            DlFailure f;
            string code = "HTTP " + status.ToString(CultureInfo.InvariantCulture);
            if (status == 408)
                f = DlFailure.Make(DlErrorKind.Network, code + Tr.S(": сервер не дождался запроса", ": the server timed out the request"));
            else if (status == 429 || status == 503)
            {
                f = DlFailure.Make(DlErrorKind.RateLimited, code + Tr.S(": сервер просит подождать", ": the server asks to wait"));
                f.RetryAfterSeconds = RetryAfterSeconds(retryAfter, utcNow);
            }
            else if (status >= 500)
                f = DlFailure.Make(DlErrorKind.Server, code + Tr.S(": ошибка на сервере", ": server error"));
            else if (resuming && (status == 401 || status == 403 || status == 404 || status == 410))
                f = DlFailure.Make(DlErrorKind.LinkExpired, code + Tr.S(": ссылка истекла или доступ закрыт — нужна свежая ссылка со страницы, скачанное сохранено",
                                                                         ": the link expired or access is closed — a fresh link from the page is needed, downloaded data is kept"));
            else if (resuming && status == 416)
                f = DlFailure.Make(DlErrorKind.Changed, code + Tr.S(": на сервере файл другого размера", ": the file on the server has a different size"));
            else if (status == 404 || status == 410)
                f = DlFailure.Make(DlErrorKind.Client, code + Tr.S(": файла по этой ссылке нет", ": nothing at this link"));
            else if (status == 401 || status == 403)
                f = DlFailure.Make(DlErrorKind.Client, code + Tr.S(": доступ запрещён (возможно, нужен вход на сайте)", ": access denied (a site login may be needed)"));
            else
                f = DlFailure.Make(DlErrorKind.Client, code);
            f.Status = status;
            return f;
        }

        // Для If-Range годится сильный ETag или Last-Modified; слабый ETag (W/"…") по RFC 7233 не годится.
        public static string IfRangeValue(DlItem item)
        {
            string etag = (item.ETag ?? "").Trim();
            if (etag.Length > 0 && !etag.StartsWith("W/", StringComparison.Ordinal)) return etag;
            return (item.LastModified ?? "").Trim();
        }

        public static bool IsDiskFull(Exception ex)
        {
            IOException io = ex as IOException;
            if (io == null) return false;
            int hr = System.Runtime.InteropServices.Marshal.GetHRForException(io);
            return hr == unchecked((int)0x80070070) || hr == unchecked((int)0x80070027);
        }
    }

    // Что транзакция просит у движка: занять имя (под общей блокировкой), сохранить, записать в журнал.
    internal interface IDlTransferHost
    {
        string ReserveName(DlItem item, string folder, string name);
        void Persist(DlItem item);
        void Journal(DlItem item, string text);
    }

    // ------------------------------------------------------------------ //
    //  Один запуск загрузки: от подготовки до завершения, паузы или ошибки
    // ------------------------------------------------------------------ //
    internal sealed class DlTransfer : IDlRun
    {
        private const int BufferSize = 64 * 1024;
        public const long MinSegmentBytes = 1024 * 1024;
        private const int FlushMs = 4000;
        private const int QuickRetries = 3;

        private readonly DlItem _item;
        private readonly DlSettings _settings;
        private readonly DlTokenBucket _global;
        private readonly IDlTransferHost _host;
        private readonly IDlEnvironment _env;
        public readonly DlTokenBucket Bucket;
        public readonly DlSpeedMeter Meter = new DlSpeedMeter();
        private readonly List<HttpWebRequest> _live = new List<HttpWebRequest>();
        private readonly Dictionary<HttpWebResponse, HttpWebRequest> _requestOf = new Dictionary<HttpWebResponse, HttpWebRequest>();
        private readonly object _gate = new object();
        private Thread _thread;
        private volatile int _stop;
        private volatile bool _done;
        private int _maxConn;
        private int _workers;
        private DlFailure _failure;

        public DlTransfer(DlItem item, DlSettings settings, DlTokenBucket global, IDlEnvironment env, IDlTransferHost host)
        {
            DlHttp.Init();
            _item = item;
            _settings = settings;
            _global = global;
            _env = env;
            _host = host;
            Bucket = new DlTokenBucket((long)item.LimitKBps * 1024);
        }

        public DlItem Item { get { return _item; } }
        DlTokenBucket IDlRun.Bucket { get { return Bucket; } }
        DlSpeedMeter IDlRun.Meter { get { return Meter; } }
        public bool Finished { get { return _done; } }
        public bool StopRequested { get { return _stop != 0; } }
        public int Workers { get { return Thread.VolatileRead(ref _workers); } }

        public DlFailure Failure
        {
            get { lock (_gate) return _failure; }
        }

        public void Start()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "wpc-dl-" + _item.Id;
            _thread.Start();
        }

        // Пауза: потоки дописывают прочитанное, сбрасывают файл на диск и выходят. Висящие запросы обрываются.
        public void RequestStop()
        {
            _stop = 1;
            lock (_live) foreach (HttpWebRequest r in _live) { try { r.Abort(); } catch { } }
        }

        public bool Join(int timeoutMs) { return _thread == null || _thread.Join(timeoutMs); }

        // Закрыть ответ и забыть его запрос. Abort обязателен: Close недочитанного ответа .NET дочитывает до конца ради
        // keep-alive — для сегмента, остановленного на своей границе, это скачивание остатка файла вхолостую.
        internal void Release(HttpWebResponse resp)
        {
            if (resp == null) return;
            HttpWebRequest req;
            lock (_live)
            {
                if (_requestOf.TryGetValue(resp, out req))
                {
                    _requestOf.Remove(resp);
                    _live.Remove(req);
                }
            }
            if (req != null) { try { req.Abort(); } catch { } }
            try { resp.Close(); } catch { }
        }

        private bool Stopping() { return _stop != 0; }

        private void SetFailure(DlFailure f)
        {
            lock (_gate) if (_failure == null) _failure = f;
        }

        private bool HasFailure { get { lock (_gate) return _failure != null; } }

        private void Run()
        {
            try
            {
                HttpWebResponse first;
                DlSegment firstSegment;
                DlFailure f = Prepare(out first, out firstSegment);
                if (f != null) { SetFailure(f); return; }
                if (Stopping()) { Release(first); return; }
                RunWorkers(first, firstSegment);
                if (Stopping() || HasFailure) return;
                if (!AllFinished())
                {
                    SetFailure(DlFailure.Make(DlErrorKind.Network, Tr.S("соединение оборвалось до конца файла", "the connection closed before the end of the file")));
                    return;
                }
                f = DlFinish.Complete(_item, _settings, _env, _host, Stopping);
                if (f != null) SetFailure(f);
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                SetFailure(DlFailure.Make(DlHttp.IsDiskFull(ex) ? DlErrorKind.Disk : DlErrorKind.Network, ex.Message));
            }
            finally
            {
                _item.ActiveConnections = 0;
                _done = true;
            }
        }

        private bool AllFinished()
        {
            lock (_item.Segments)
            {
                foreach (DlSegment s in _item.Segments) if (!s.Finished) return false;
                return true;
            }
        }

        // ---------- подготовка ----------
        private DlFailure Prepare(out HttpWebResponse first, out DlSegment firstSegment)
        {
            first = null;
            firstSegment = null;
            if (!DlHttp.IsAllowedScheme(_item.Url))
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"));

            bool resumable;
            lock (_item.Segments) resumable = _item.Segments.Count > 0;
            if (resumable)
            {
                string why;
                resumable = CanResume(out why);
                if (!resumable)
                {
                    if (why != null) _host.Journal(_item, why);
                    DlFailure reset = ResetPartial();
                    if (reset != null) return reset;
                }
            }
            if (resumable)
            {
                try
                {
                    using (FileStream fs = new FileStream(_item.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        if (fs.Length != _item.Total) fs.SetLength(_item.Total);
                }
                catch (Exception ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, Tr.S("частичный файл не открывается: ", "the partial file cannot be opened: ") + ex.Message);
                }
                _host.Journal(_item, Tr.S("докачка с ", "resuming at ") + Human(_item.DoneBytes) + " / " + Human(_item.Total));
                return null;
            }
            return PrepareFresh(out first, out firstSegment);
        }

        private bool CanResume(out string why)
        {
            why = null;
            if (!_item.AcceptRanges || _item.Total <= 0)
            {
                why = Tr.S("сервер не поддерживает докачку — загрузка начнётся заново", "the server does not support resuming — starting over");
                return false;
            }
            string part = DlFiles.PathInside(_item.Folder, _item.FileName);
            if (part == null) return false;
            part += DlPaths.PartSuffix;
            if (!DlFiles.Exists(part) || DlFiles.IsReparse(part))
            {
                why = Tr.S("частичного файла нет — загрузка начнётся заново", "the partial file is gone — starting over");
                return false;
            }
            return true;
        }

        // Своё частичное (.wpcpart внутри папки загрузки, не ссылка) удаляется сразу: это недокачанные байты, а не файл пользователя.
        private DlFailure ResetPartial()
        {
            lock (_item.Segments) _item.Segments.Clear();
            string part = DlFiles.PathInside(_item.Folder ?? "", _item.FileName ?? "");
            if (part != null)
            {
                part += DlPaths.PartSuffix;
                if (DlFiles.IsReparse(part))
                    return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте частичного файла ссылка — не трогаю", "a link sits where the partial file was — left alone"));
                try { if (File.Exists(part)) File.Delete(part); }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, ex.Message); }
            }
            if (!_item.NameFixed) _item.FileName = "";
            return null;
        }

        private DlFailure PrepareFresh(out HttpWebResponse first, out DlSegment firstSegment)
        {
            first = null;
            firstSegment = null;
            List<string> chain = new List<string>();
            string finalUrl;
            DlFailure f;
            HttpWebResponse resp = Open(_item.Url, 0, -1, true, false, chain, out finalUrl, out f);
            if (f != null) return f;
            bool keep = false;
            try
            {
                int code = (int)resp.StatusCode;
                if (code != 200 && code != 206) return DlHttp.Classify(code, false, resp.Headers["Retry-After"], _env.UtcNow);
                lock (_item.Redirects) { _item.Redirects.Clear(); _item.Redirects.AddRange(chain); }
                _item.FinalUrl = finalUrl;

                long total;
                bool ranges;
                if (code == 206)
                {
                    long s, e, t;
                    if (!DlHttp.TryParseContentRange(resp.Headers["Content-Range"], out s, out e, out t) || s != 0)
                        return DlFailure.Make(DlErrorKind.Server, Tr.S("сервер прислал неверный диапазон", "the server sent a wrong range"));
                    total = t;
                    ranges = t > 0;
                }
                else
                {
                    total = resp.ContentLength;
                    ranges = total > 0 && string.Equals((resp.Headers["Accept-Ranges"] ?? "").Trim(), "bytes", StringComparison.OrdinalIgnoreCase);
                }
                _item.Total = total;
                _item.AcceptRanges = ranges;
                _item.ETag = resp.Headers["ETag"] ?? "";
                _item.LastModified = resp.Headers["Last-Modified"] ?? "";

                string name = _item.NameFixed ? _item.FileName : "";
                if (string.IsNullOrEmpty(name)) name = DlHttp.ContentDispositionName(resp.Headers["Content-Disposition"]);
                if (string.IsNullOrEmpty(name)) name = DlFiles.NameFromUrl(finalUrl);
                if (string.IsNullOrEmpty(name)) name = DlFiles.NameFromUrl(_item.Url);
                name = DlFiles.SanitizeName(name);
                string ext = "";
                try { ext = Path.GetExtension(name); } catch { }
                if (ext.Length == 0) name = DlFiles.SanitizeName(name + DlFiles.ExtensionForMime(resp.ContentType));

                string why;
                string rawFolder = string.IsNullOrEmpty(_item.Folder) ? DlFiles.FolderFor(_settings, finalUrl, name) : _item.Folder;
                string folder = DlFiles.CheckFolder(rawFolder, out why);
                if (folder == null) return DlFailure.Make(DlErrorKind.Policy, why);
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("папка недоступна: ", "the folder is not available: ") + ex.Message); }

                if (total > 0)
                {
                    long free = DlFiles.FreeSpace(folder);
                    if (free >= 0 && free < total + 16 * 1024 * 1024)
                        return DlFailure.Make(DlErrorKind.Disk, Tr.S("на диске не хватает места: нужно ", "not enough disk space: needs ") + Human(total)
                                                                 + Tr.S(", свободно ", ", free ") + Human(free));
                }

                string unique = _host.ReserveName(_item, folder, name);
                if (unique == null) return DlFailure.Make(DlErrorKind.Disk, Tr.S("не найдено свободное имя файла", "no free file name found"));
                string target = DlFiles.PathInside(folder, unique);
                if (target == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
                _item.Folder = folder;
                _item.FileName = unique;

                try
                {
                    // CreateNew: никогда не открываем чужой файл или ссылку, оказавшиеся на этом месте.
                    using (FileStream fs = new FileStream(target + DlPaths.PartSuffix, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                        if (ranges && total > MinSegmentBytes) DlFiles.SetSparse(fs, true);
                        if (total > 0) fs.SetLength(total);
                    }
                }
                catch (Exception ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, Tr.S("файл не создаётся: ", "the file cannot be created: ") + ex.Message);
                }

                int conns = Connections();
                lock (_item.Segments)
                {
                    _item.Segments.Clear();
                    if (total == 0) { }
                    else if (ranges)
                    {
                        long parts = Math.Max(1, Math.Min(conns, total / MinSegmentBytes));
                        long size = total / parts;
                        for (long i = 0; i < parts; i++)
                        {
                            DlSegment s = new DlSegment();
                            s.Start = i * size;
                            s.End = i == parts - 1 ? total - 1 : (i + 1) * size - 1;
                            _item.Segments.Add(s);
                        }
                    }
                    else
                    {
                        DlSegment s = new DlSegment();
                        s.Start = 0;
                        s.End = total > 0 ? total - 1 : -1;
                        _item.Segments.Add(s);
                    }
                    if (_item.Segments.Count > 0)
                    {
                        firstSegment = _item.Segments[0];
                        firstSegment.Busy = true;
                    }
                }
                _host.Persist(_item);
                _host.Journal(_item, Tr.S("начало: ", "started: ") + unique + ", " + (total >= 0 ? Human(total) : Tr.S("размер неизвестен", "size unknown"))
                                     + (ranges ? Tr.S(", сегментов ", ", segments ") + _item.Segments.Count : Tr.S(", один поток (сервер не отдаёт диапазоны)", ", single stream (no ranges)"))
                                     + (chain.Count > 0 ? Tr.S(", редиректов ", ", redirects ") + chain.Count : ""));
                if (firstSegment != null) { first = resp; keep = true; }
                return null;
            }
            finally
            {
                if (!keep) Release(resp);
            }
        }

        private int Connections()
        {
            int n = _item.Connections > 0 ? _item.Connections : _settings.Segments;
            return Math.Max(1, Math.Min(n, _settings.MaxPerServer));
        }

        // ---------- запрос с редиректами ----------
        private HttpWebRequest Create(string url, long from, long to, bool range, bool ifRange)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.AutomaticDecompression = DecompressionMethods.None;
            req.Headers["Accept-Encoding"] = "identity";
            req.UserAgent = string.IsNullOrEmpty(_item.UserAgent) ? _settings.EffectiveUserAgent : _item.UserAgent;
            req.Accept = "*/*";
            if (!string.IsNullOrEmpty(_item.Referrer) && DlHttp.IsAllowedScheme(_item.Referrer)) req.Referer = _item.Referrer;
            req.KeepAlive = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            try { if (req.ServicePoint.ConnectionLimit < 64) req.ServicePoint.ConnectionLimit = 64; } catch { }
            if (range)
            {
                if (to >= 0) req.AddRange(from, to);
                else req.AddRange(from);
            }
            if (ifRange)
            {
                string v = DlHttp.IfRangeValue(_item);
                if (v.Length > 0) req.Headers["If-Range"] = v;
            }
            // Cookies — только тому хосту, для которого их выдал браузер.
            if (!string.IsNullOrEmpty(_item.Cookies) && string.Equals(req.RequestUri.Host, _item.CookieHost, StringComparison.OrdinalIgnoreCase))
                req.Headers["Cookie"] = _item.Cookies;
            return req;
        }

        internal HttpWebResponse Open(string url, long from, long to, bool range, bool ifRange, List<string> chain, out string finalUrl, out DlFailure failure)
        {
            failure = null;
            finalUrl = url;
            string current = url;
            for (int hop = 0; hop <= DlHttp.MaxRedirects; hop++)
            {
                if (Stopping()) { failure = DlFailure.Make(DlErrorKind.Network, "stopped"); return null; }
                if (!DlHttp.IsAllowedScheme(current))
                {
                    failure = DlFailure.Make(DlErrorKind.Policy, Tr.S("редирект на недопустимую схему: ", "redirect to a disallowed scheme: ") + DlLog.Redact(current));
                    return null;
                }
                HttpWebRequest req;
                try { req = Create(current, from, to, range, ifRange); }
                catch (Exception ex) { failure = DlFailure.Make(DlErrorKind.Policy, ex.Message); return null; }
                lock (_live) _live.Add(req);
                HttpWebResponse resp = null;
                try
                {
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException we)
                    {
                        resp = we.Response as HttpWebResponse;
                        if (resp == null)
                        {
                            failure = DlFailure.Make(DlErrorKind.Network, we.Message);
                            return null;
                        }
                    }
                }
                catch (Exception ex)
                {
                    failure = DlFailure.Make(DlErrorKind.Network, ex.Message);
                    return null;
                }
                finally
                {
                    if (resp == null) lock (_live) _live.Remove(req);
                }
                int code = (int)resp.StatusCode;
                if (code == 301 || code == 302 || code == 303 || code == 307 || code == 308)
                {
                    string location = resp.Headers["Location"];
                    resp.Close();
                    lock (_live) _live.Remove(req);
                    Uri next;
                    if (string.IsNullOrEmpty(location) || !Uri.TryCreate(new Uri(current), location, out next))
                    {
                        failure = DlFailure.Make(DlErrorKind.Server, Tr.S("редирект без адреса", "a redirect without a location"));
                        return null;
                    }
                    failure = DlHttp.CheckRedirect(current, next.AbsoluteUri, _item.AllowHttpDowngrade);
                    if (failure != null) return null;
                    current = next.AbsoluteUri;
                    if (chain != null) chain.Add(code.ToString(CultureInfo.InvariantCulture) + " " + current);
                    continue;
                }
                finalUrl = current;
                lock (_live) _requestOf[resp] = req;
                return resp;
            }
            failure = DlFailure.Make(DlErrorKind.Policy, Tr.S("больше 10 редиректов подряд", "more than 10 redirects in a row"));
            return null;
        }

        // ---------- потоки сегментов ----------
        private void RunWorkers(HttpWebResponse first, DlSegment firstSegment)
        {
            _maxConn = Connections();
            int unfinished = 0;
            lock (_item.Segments) foreach (DlSegment s in _item.Segments) if (!s.Finished) unfinished++;
            int count = Math.Max(firstSegment != null ? 1 : 0, Math.Min(_maxConn, unfinished));
            List<Thread> threads = new List<Thread>();
            for (int i = 0; i < count; i++)
            {
                object[] state = i == 0 && first != null ? new object[] { first, firstSegment } : null;
                Thread t = new Thread(Worker);
                t.IsBackground = true;
                t.Name = "wpc-dl-" + _item.Id + "-" + i;
                Interlocked.Increment(ref _workers);
                threads.Add(t);
                t.Start(state);
            }
            foreach (Thread t in threads) t.Join();
        }

        private DlSegment Claim()
        {
            lock (_item.Segments)
            {
                foreach (DlSegment s in _item.Segments)
                    if (!s.Busy && !s.Finished) { s.Busy = true; return s; }
                return null;
            }
        }

        private void Worker(object state)
        {
            object[] pair = state as object[];
            HttpWebResponse initial = pair == null ? null : (HttpWebResponse)pair[0];
            DlSegment preset = pair == null ? null : (DlSegment)pair[1];
            _item.ActiveConnections = Thread.VolatileRead(ref _workers);
            try
            {
                while (!Stopping() && !HasFailure)
                {
                    DlSegment seg = preset ?? Claim();
                    preset = null;
                    if (seg == null) break;
                    bool refused;
                    DlFailure f;
                    try { f = Pump(seg, ref initial, out refused); }
                    finally { lock (_item.Segments) seg.Busy = false; }
                    if (f != null)
                    {
                        if (refused && Thread.VolatileRead(ref _workers) > 1)
                        {
                            int left = Math.Max(1, Thread.VolatileRead(ref _workers) - 1);
                            if (left < _maxConn)
                            {
                                _maxConn = left;
                                _host.Journal(_item, Tr.S("сервер ограничил число соединений: ", "the server limited connections to ") + left + " (" + f.Message + ")");
                            }
                            break;
                        }
                        if (!Stopping()) SetFailure(f);
                        break;
                    }
                    if (Thread.VolatileRead(ref _workers) > _maxConn) break;
                }
            }
            catch (Exception ex)
            {
                if (!Stopping()) { DlLog.Report(ex); SetFailure(DlFailure.Make(DlErrorKind.Network, ex.Message)); }
            }
            finally
            {
                Release(initial);
                _item.ActiveConnections = Math.Max(0, Interlocked.Decrement(ref _workers));
            }
        }

        // Докачать один сегмент. refused — сервер отказал лишнему соединению (429/503/403 при живых соседях или 200 вместо 206).
        private DlFailure Pump(DlSegment seg, ref HttpWebResponse initial, out bool refused)
        {
            refused = false;
            int quick = 0;
            bool triedOriginal = false;
            while (true)
            {
                if (Stopping() || HasFailure) return null;
                lock (_item.Segments) if (seg.Finished) return null;

                HttpWebResponse resp = initial;
                initial = null;
                if (resp == null)
                {
                    long from, to;
                    lock (_item.Segments)
                    {
                        if (!_item.AcceptRanges && seg.Done > 0)
                        {
                            // Без диапазонов продолжить нельзя: поток идёт с нуля, записанное перезаписывается.
                            seg.Done = 0;
                            seg.Durable = 0;
                        }
                        from = seg.Next;
                        to = seg.End;
                    }
                    bool resuming = _item.DoneBytes > 0;
                    string url = string.IsNullOrEmpty(_item.FinalUrl) || triedOriginal ? _item.Url : _item.FinalUrl;
                    string finalUrl;
                    DlFailure f;
                    List<string> chain = new List<string>();
                    resp = Open(url, from, to, _item.AcceptRanges, _item.AcceptRanges, chain, out finalUrl, out f);
                    if (f != null)
                    {
                        if (Stopping()) return null;
                        if (f.Kind != DlErrorKind.Network) return f;
                        if (++quick > QuickRetries) return f;
                        if (!SleepUnlessStopped(1000 * quick)) return null;
                        continue;
                    }
                    int code = (int)resp.StatusCode;
                    if (code == 206 && _item.AcceptRanges)
                    {
                        long s, e, t;
                        bool parsed = DlHttp.TryParseContentRange(resp.Headers["Content-Range"], out s, out e, out t);
                        if (!parsed || s != from || (t >= 0 && _item.Total >= 0 && t != _item.Total))
                        {
                            Release(resp);
                            return DlFailure.Make(DlErrorKind.Changed, Tr.S("на сервере файл другого размера — скачанное не подходит", "the file on the server has a different size — downloaded data does not match"));
                        }
                        if (DlHttp.IfRangeValue(_item).Length == 0)
                        {
                            // После «обновить ссылку» валидаторов нет: берём их у нового сервера, совпадение размера уже проверено.
                            _item.ETag = resp.Headers["ETag"] ?? "";
                            _item.LastModified = resp.Headers["Last-Modified"] ?? "";
                        }
                        if (!string.Equals(finalUrl, _item.FinalUrl, StringComparison.Ordinal)) _item.FinalUrl = finalUrl;
                    }
                    else if (code == 200 && _item.AcceptRanges)
                    {
                        Release(resp);
                        if (resuming && DlHttp.IfRangeValue(_item).Length > 0)
                            return DlFailure.Make(DlErrorKind.Changed, Tr.S("файл на сервере изменился (If-Range → 200) — нужно скачать заново", "the file on the server changed (If-Range → 200) — it has to be downloaded again"));
                        refused = true;
                        return DlFailure.Make(DlErrorKind.Server, Tr.S("сервер не отдаёт диапазон этому соединению", "the server does not serve a range to this connection"));
                    }
                    else if (code == 200 && !_item.AcceptRanges) { }
                    else
                    {
                        DlFailure c = DlHttp.Classify(code, resuming, resp.Headers["Retry-After"], _env.UtcNow);
                        Release(resp);
                        if (c.Kind == DlErrorKind.LinkExpired && !triedOriginal && !string.Equals(url, _item.Url, StringComparison.Ordinal))
                        {
                            // Прямая ссылка после редиректа истекла — пробуем исходную: сервер выдаст новую.
                            triedOriginal = true;
                            continue;
                        }
                        refused = (code == 429 || code == 503 || code == 403) && Thread.VolatileRead(ref _workers) > 1;
                        return c;
                    }
                }

                string ioStage = "net";
                try
                {
                    Copy(resp, seg, ref ioStage, ref quick);
                    lock (_item.Segments) if (seg.Finished || seg.End < 0) return null;
                    if (Stopping()) return null;
                    throw new IOException(Tr.S("сервер закрыл соединение раньше конца", "the server closed the connection early"));
                }
                catch (Exception ex)
                {
                    if (Stopping()) return null;
                    if (ioStage == "disk")
                        return DlFailure.Make(DlErrorKind.Disk, (DlHttp.IsDiskFull(ex) ? Tr.S("диск заполнен: ", "the disk is full: ") : Tr.S("ошибка записи: ", "write error: ")) + ex.Message);
                    if (++quick > QuickRetries) return DlFailure.Make(DlErrorKind.Network, ex.Message);
                    if (!SleepUnlessStopped(1000 * quick)) return null;
                }
            }
        }

        private void Copy(HttpWebResponse resp, DlSegment seg, ref string ioStage, ref int quick)
        {
            try
            {
                CopyBody(resp, seg, ref ioStage, ref quick);
            }
            finally
            {
                Release(resp);
            }
        }

        private void CopyBody(HttpWebResponse resp, DlSegment seg, ref string ioStage, ref int quick)
        {
            using (Stream net = resp.GetResponseStream())
            {
                ioStage = "disk";
                // bufferSize 1 — без буфера .NET: каждый Write сразу уходит в ОС, и убитый процесс не теряет прочитанное.
                using (FileStream fs = new FileStream(_item.PartPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    long pos;
                    lock (_item.Segments) pos = seg.Next;
                    fs.Seek(pos, SeekOrigin.Begin);
                    byte[] buf = new byte[BufferSize];
                    Stopwatch flush = Stopwatch.StartNew();
                    try
                    {
                        while (!Stopping() && !HasFailure)
                        {
                            long remaining;
                            lock (_item.Segments) remaining = seg.End >= 0 ? seg.End - seg.Next + 1 : BufferSize;
                            if (remaining <= 0) break;
                            int want = (int)Math.Min(BufferSize, remaining);
                            int grant = Bucket.Take(want, Stopping);
                            if (grant <= 0) break;
                            int g = _global.Take(grant, Stopping);
                            if (g < grant) Bucket.Return(grant - g);
                            if (g <= 0) break;
                            ioStage = "net";
                            int read = net.Read(buf, 0, g);
                            if (read < g) { Bucket.Return(g - read); _global.Return(g - read); }
                            if (read <= 0)
                            {
                                lock (_item.Segments)
                                    if (seg.End < 0)
                                    {
                                        // Размер не был известен: конец потока и есть конец файла.
                                        if (seg.Done == 0) _item.Segments.Remove(seg);
                                        else seg.End = seg.Start + seg.Done - 1;
                                        _item.Total = seg.Done;
                                    }
                                break;
                            }
                            ioStage = "disk";
                            fs.Write(buf, 0, read);
                            lock (_item.Segments) seg.Done += read;
                            Meter.Add(read);
                            quick = 0;
                            if (flush.ElapsedMilliseconds >= FlushMs)
                            {
                                long d;
                                lock (_item.Segments) d = seg.Done;
                                fs.Flush(true);
                                lock (_item.Segments) seg.Durable = d;
                                flush.Restart();
                            }
                        }
                    }
                    finally
                    {
                        try
                        {
                            long d;
                            lock (_item.Segments) d = seg.Done;
                            fs.Flush(true);
                            lock (_item.Segments) seg.Durable = d;
                        }
                        catch { }
                    }
                }
            }
        }

        private bool SleepUnlessStopped(int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (Stopping()) return false;
                Thread.Sleep(50);
            }
            return !Stopping();
        }

        public static string Human(long bytes)
        {
            if (bytes < 0) return "?";
            string[] units = { Tr.S("Б", "B"), Tr.S("КБ", "KB"), Tr.S("МБ", "MB"), Tr.S("ГБ", "GB"), Tr.S("ТБ", "TB") };
            double v = bytes;
            int u = 0;
            while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
            return (u == 0 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[u];
        }
    }
}
