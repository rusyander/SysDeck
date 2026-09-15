// SysDeck — «Загрузки», видео: запросы (плейлисты, ключи, сегменты), определение вида ссылки, sidx.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Тот же сетевой порядок, что у DlTransfer: редиректы вручную (каждый шаг — DlHttp.CheckRedirect, https → http только
// с согласия), сжатие выключено, cookies уходят только хосту, для которого их выдал браузер, и не пишутся никуда.
// Чтение сегмента берёт токены у ведра загрузки и у общего ведра движка. Текст плейлиста, ключ и индекс читаются в
// память с потолком (16 МиБ, ключ — 64 байта): длина из сети не решает, сколько памяти выделить.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace SysDeck.Downloads
{
    // ------------------------------------------------------------------ //
    //  Запросы одной видео-загрузки
    // ------------------------------------------------------------------ //
    internal sealed class MdFetcher
    {
        private const int BufferSize = 64 * 1024;
        private static readonly string[] Restricted = { "host", "content-length", "connection", "range", "transfer-encoding", "expect", "date",
                                                        "if-modified-since", "proxy-connection", "keep-alive", "te", "upgrade", "accept-encoding",
                                                        "cookie", "user-agent", "referer", "accept", "if-range" };

        private readonly DlItem _item;
        private readonly DlSettings _settings;
        private readonly DlTokenBucket _bucket, _global;
        private readonly DlSpeedMeter _meter;
        private readonly Func<bool> _stop;
        private readonly List<HttpWebRequest> _live = new List<HttpWebRequest>();
        private readonly Dictionary<HttpWebResponse, HttpWebRequest> _requestOf = new Dictionary<HttpWebResponse, HttpWebRequest>();

        public MdFetcher(DlItem item, DlSettings settings, DlTokenBucket bucket, DlTokenBucket global, DlSpeedMeter meter, Func<bool> stop)
        {
            DlHttp.Init();
            _item = item;
            _settings = settings ?? new DlSettings();
            _bucket = bucket ?? new DlTokenBucket(0);
            _global = global ?? new DlTokenBucket(0);
            _meter = meter ?? new DlSpeedMeter();
            _stop = stop ?? delegate { return false; };
        }

        public DlItem Item { get { return _item; } }

        private bool Stopping() { return _stop(); }

        public void AbortAll()
        {
            lock (_live) foreach (HttpWebRequest r in _live.ToArray()) { try { r.Abort(); } catch { } }
        }

        public void Release(HttpWebResponse resp)
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

        // Шаг редиректа: адрес следующего запроса или отказ. Его же зовёт Open на каждом 3xx.
        internal static DlFailure NextHop(string current, string location, bool allowDowngrade, out string next)
        {
            next = null;
            Uri cur, n;
            if (string.IsNullOrEmpty(location) || !Uri.TryCreate(current, UriKind.Absolute, out cur) || !Uri.TryCreate(cur, location, out n))
                return DlFailure.Make(DlErrorKind.Server, Tr.S("редирект без адреса", "a redirect without a location"));
            DlFailure f = DlHttp.CheckRedirect(current, n.AbsoluteUri, allowDowngrade);
            if (f != null) return f;
            next = n.AbsoluteUri;
            return null;
        }

        private HttpWebRequest Create(string url, long offset, long length, IDictionary<string, string> headers)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.AutomaticDecompression = DecompressionMethods.None;
            req.Headers["Accept-Encoding"] = "identity";
            string ua = Header(headers, "User-Agent");
            if (ua.Length == 0) ua = string.IsNullOrEmpty(_item.UserAgent) ? _settings.EffectiveUserAgent : _item.UserAgent;
            req.UserAgent = ua;
            string accept = Header(headers, "Accept");
            req.Accept = accept.Length > 0 ? accept : "*/*";
            string referer = Header(headers, "Referer");
            if (referer.Length == 0) referer = _item.Referrer ?? "";
            if (referer.Length > 0 && DlHttp.IsAllowedScheme(referer)) req.Referer = referer;
            req.KeepAlive = true;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            try { if (req.ServicePoint.ConnectionLimit < 64) req.ServicePoint.ConnectionLimit = 64; } catch { }
            if (offset >= 0)
            {
                if (length > 0) req.AddRange(offset, offset + length - 1);
                else req.AddRange(offset);
            }
            if (headers != null)
                foreach (KeyValuePair<string, string> kv in headers)
                {
                    if (DlMedia.SensitiveHeader(kv.Key) || Array.IndexOf(Restricted, (kv.Key ?? "").Trim().ToLowerInvariant()) >= 0) continue;
                    try { req.Headers[kv.Key] = kv.Value ?? ""; }
                    catch (ArgumentException) { }
                }
            // Cookies — только тому хосту, для которого их выдал браузер.
            if (!string.IsNullOrEmpty(_item.Cookies) && string.Equals(req.RequestUri.Host, _item.CookieHost, StringComparison.OrdinalIgnoreCase))
                req.Headers["Cookie"] = _item.Cookies;
            return req;
        }

        private static string Header(IDictionary<string, string> headers, string name)
        {
            if (headers == null) return "";
            foreach (KeyValuePair<string, string> kv in headers)
                if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return (kv.Value ?? "").Trim();
            return "";
        }

        // Ответ на любой код, кроме редиректа (их проходит сам). offset -1 — без Range; length -1 — до конца.
        public HttpWebResponse Open(string url, long offset, long length, IDictionary<string, string> headers, out string finalUrl, out DlFailure failure)
        {
            failure = null;
            finalUrl = url;
            string current = url;
            for (int hop = 0; hop <= DlHttp.MaxRedirects; hop++)
            {
                if (Stopping()) { failure = DlFailure.Make(DlErrorKind.Network, "stopped"); return null; }
                if (!DlHttp.IsAllowedScheme(current))
                {
                    failure = DlFailure.Make(DlErrorKind.Policy, Tr.S("недопустимая схема адреса: ", "a disallowed address scheme: ") + DlLog.Redact(current));
                    return null;
                }
                HttpWebRequest req;
                try { req = Create(current, offset, length, headers); }
                catch (Exception ex) { failure = DlFailure.Make(DlErrorKind.Policy, ex.Message); return null; }
                lock (_live) _live.Add(req);
                HttpWebResponse resp = null;
                try
                {
                    try { resp = (HttpWebResponse)req.GetResponse(); }
                    catch (WebException we)
                    {
                        resp = we.Response as HttpWebResponse;
                        if (resp == null) { failure = DlFailure.Make(DlErrorKind.Network, we.Message); return null; }
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
                    try { resp.Close(); } catch { }
                    lock (_live) _live.Remove(req);
                    string next;
                    failure = NextHop(current, location, _item.AllowHttpDowngrade, out next);
                    if (failure != null) return null;
                    current = next;
                    continue;
                }
                finalUrl = current;
                lock (_live) _requestOf[resp] = req;
                return resp;
            }
            failure = DlFailure.Make(DlErrorKind.Policy, Tr.S("больше 10 редиректов подряд", "more than 10 redirects in a row"));
            return null;
        }

        // Проверка кода ответа на запрос диапазона. skip — сколько байт тела пропустить (сервер отдал весь файл на Range).
        public DlFailure Accept(HttpWebResponse resp, long offset, bool expiredMeansLink, out long skip)
        {
            skip = 0;
            int code = (int)resp.StatusCode;
            if (code == 206)
            {
                long s, e, t;
                if (offset >= 0 && (!DlHttp.TryParseContentRange(resp.Headers["Content-Range"], out s, out e, out t) || s != offset))
                    return DlFailure.Make(DlErrorKind.Changed, Tr.S("сервер прислал не тот диапазон", "the server sent a different range"));
                return null;
            }
            if (code == 200)
            {
                skip = offset > 0 ? offset : 0;
                return null;
            }
            return DlHttp.Classify(code, expiredMeansLink, resp.Headers["Retry-After"], DateTime.UtcNow);
        }

        // Небольшой ответ целиком в память (плейлист, ключ, init, индекс). max — потолок: больше — отказ.
        public byte[] GetBytes(string url, long offset, long length, int max, IDictionary<string, string> headers, bool expiredMeansLink,
                               out string finalUrl, out string contentType, out DlFailure failure)
        {
            contentType = "";
            HttpWebResponse resp = Open(url, offset, length, headers, out finalUrl, out failure);
            if (failure != null) return null;
            try
            {
                long skip;
                failure = Accept(resp, offset, expiredMeansLink, out skip);
                if (failure != null) return null;
                contentType = resp.ContentType ?? "";
                long want = length > 0 ? length : resp.ContentLength;
                if (want > max)
                {
                    failure = DlFailure.Make(DlErrorKind.Client, Tr.S("ответ больше допустимого (", "the response is larger than allowed (") + DlTransfer.Human(max) + ")");
                    return null;
                }
                using (Stream net = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    DlFailure f = Pump(net, skip, length > 0 ? length : -1, ms, max);
                    if (f != null) { failure = f; return null; }
                    return ms.ToArray();
                }
            }
            catch (Exception ex)
            {
                failure = DlFailure.Make(DlErrorKind.Network, ex.Message);
                return null;
            }
            finally
            {
                Release(resp);
            }
        }

        // Тело ответа в sink с ограничением скорости. length -1 — до конца потока; cap — потолок байт (0 — без).
        // Ошибка сети — Network (повторяемая), записи — Disk.
        public DlFailure Pump(Stream net, long skip, long length, Stream sink, long cap)
        {
            byte[] buf = new byte[BufferSize];
            long skipped = 0, got = 0;
            string stage = "net";
            try
            {
                while (!Stopping())
                {
                    long remaining = skipped < skip ? skip - skipped : length >= 0 ? length - got : BufferSize;
                    if (remaining <= 0) break;
                    int want = (int)Math.Min(BufferSize, remaining);
                    int grant = _bucket.Take(want, _stop);
                    if (grant <= 0) break;
                    int g = _global.Take(grant, _stop);
                    if (g < grant) _bucket.Return(grant - g);
                    if (g <= 0) break;
                    stage = "net";
                    int read = net.Read(buf, 0, g);
                    if (read < g) { _bucket.Return(g - read); _global.Return(g - read); }
                    if (read <= 0) break;
                    _meter.Add(read);
                    if (skipped < skip) { skipped += read; if (skipped > skip) { int over = (int)(skipped - skip); skipped = skip; WriteTail(buf, read - over, over, sink, ref got, cap, ref stage); } continue; }
                    WriteTail(buf, 0, read, sink, ref got, cap, ref stage);
                }
                if (Stopping()) return DlFailure.Make(DlErrorKind.Network, "stopped");
                if (length >= 0 && got < length)
                    return DlFailure.Make(DlErrorKind.Network, Tr.S("сервер закрыл соединение раньше конца", "the server closed the connection early"));
                return null;
            }
            catch (CapExceeded)
            {
                return DlFailure.Make(DlErrorKind.Client, Tr.S("ответ больше допустимого", "the response is larger than allowed"));
            }
            catch (Exception ex)
            {
                if (Stopping()) return DlFailure.Make(DlErrorKind.Network, "stopped");
                if (stage == "disk")
                    return DlFailure.Make(DlErrorKind.Disk, (DlHttp.IsDiskFull(ex) ? Tr.S("диск заполнен: ", "the disk is full: ") : Tr.S("ошибка записи: ", "write error: ")) + ex.Message);
                return DlFailure.Make(DlErrorKind.Network, ex.Message);
            }
        }

        private sealed class CapExceeded : Exception { }

        private static void WriteTail(byte[] buf, int from, int count, Stream sink, ref long got, long cap, ref string stage)
        {
            if (count <= 0) return;
            if (cap > 0 && got + count > cap) throw new CapExceeded();
            stage = "disk";
            sink.Write(buf, from, count);
            got += count;
            stage = "net";
        }
    }

    // ------------------------------------------------------------------ //
    //  Что за ссылка: HLS, DASH, прямой файл или страница
    // ------------------------------------------------------------------ //
    internal static class MdLoader
    {
        public const int MaxManifest = 16 * 1024 * 1024;
        public const int MaxKey = 64;
        public const long DirectChunk = 4 * 1024 * 1024;
        private const int SniffBytes = 1024;

        public static string PageRefusal
        {
            get { return Tr.S("это страница, а не видео — попробуйте через yt-dlp", "this is a web page, not a video — try yt-dlp"); }
        }

        public static MdManifest Load(string url, DlItem ctx, Func<bool> cancel, out DlFailure failure)
        {
            MdFetcher fx = new MdFetcher(ctx ?? new DlItem(), null, null, null, null, cancel);
            return Load(fx, url, ctx != null && ctx.Media != null ? ctx.Media.Headers : null, out failure);
        }

        internal static MdManifest Load(MdFetcher fx, string url, IDictionary<string, string> headers, out DlFailure failure)
        {
            failure = null;
            if (!DlHttp.IsAllowedScheme(url))
            {
                failure = DlFailure.Make(DlErrorKind.Policy, Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"));
                return null;
            }
            string kind, text, finalUrl, contentType;
            long size;
            bool ranges;
            failure = Sniff(fx, url, headers, false, out kind, out text, out finalUrl, out contentType, out size, out ranges);
            if (failure != null) return null;
            if (kind == "page")
            {
                failure = DlFailure.Make(DlErrorKind.Client, PageRefusal);
                return null;
            }
            if (kind == "hls")
            {
                string err;
                MdManifest m = MdHls.ParseMaster(text, finalUrl, out err);
                if (err != null && m.Refused != MdHls.DrmRefusal && !m.Refused.StartsWith(MdHls.DrmRefusal, StringComparison.Ordinal))
                {
                    failure = DlFailure.Make(DlErrorKind.Client, err);
                    return null;
                }
                return m;
            }
            if (kind == "dash")
            {
                string err;
                MdManifest m = MdDash.Parse(text, finalUrl, out err);
                if (err != null)
                {
                    failure = DlFailure.Make(DlErrorKind.Client, err);
                    return null;
                }
                return m;
            }
            MdManifest d = new MdManifest();
            d.Source = MdSource.Direct;
            d.Url = finalUrl;
            MdTrack t = new MdTrack();
            t.Id = "direct";
            t.Kind = contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ? MdTrackKind.Audio : MdTrackKind.Muxed;
            t.Layout = LayoutByType(contentType, finalUrl);
            t.Url = finalUrl;
            t.SizeHint = size;
            MdVariant v = new MdVariant();
            v.Id = "direct";
            v.Label = Tr.S("файл", "file");
            v.Main = t;
            d.Variants.Add(v);
            return d;
        }

        // Открыть адрес и по типу, а затем по первым байтам понять, что это. Тело плейлиста читается целиком (≤ 16 МиБ),
        // тело видео — нет (соединение обрывается).
        private static DlFailure Sniff(MdFetcher fx, string url, IDictionary<string, string> headers, bool expiredMeansLink,
                                       out string kind, out string text, out string finalUrl, out string contentType, out long size, out bool ranges)
        {
            kind = "direct";
            text = null;
            contentType = "";
            size = -1;
            ranges = false;
            DlFailure f;
            HttpWebResponse resp = fx.Open(url, -1, -1, headers, out finalUrl, out f);
            if (f != null) return f;
            try
            {
                int code = (int)resp.StatusCode;
                if (code != 200 && code != 206) return DlHttp.Classify(code, expiredMeansLink, resp.Headers["Retry-After"], DateTime.UtcNow);
                contentType = (resp.ContentType ?? "").Trim();
                string ct = contentType.ToLowerInvariant();
                size = code == 200 ? resp.ContentLength : -1;
                ranges = string.Equals((resp.Headers["Accept-Ranges"] ?? "").Trim(), "bytes", StringComparison.OrdinalIgnoreCase);
                bool hlsType = ct.Contains("mpegurl");
                bool dashType = ct.Contains("dash+xml");
                if ((ct.StartsWith("video/", StringComparison.Ordinal) || ct.StartsWith("audio/", StringComparison.Ordinal)) && !hlsType) return null;
                if (resp.ContentLength > MaxManifest && !hlsType && !dashType) return null;
                if (resp.ContentLength > MaxManifest)
                    return DlFailure.Make(DlErrorKind.Client, Tr.S("плейлист слишком большой", "the playlist is too large"));
                using (Stream net = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    // Сначала немного — хватит понять, текст ли это; видео дальше не читаем.
                    byte[] head = new byte[SniffBytes];
                    int n = 0, r;
                    while (n < head.Length && (r = net.Read(head, n, head.Length - n)) > 0) n += r;
                    string start = Encoding.UTF8.GetString(head, 0, n).TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
                    bool hls = hlsType || start.StartsWith("#EXTM3U", StringComparison.Ordinal);
                    bool dash = dashType || start.IndexOf("<MPD", StringComparison.Ordinal) >= 0;
                    bool page = !hls && !dash && (ct.Contains("html") || start.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase)
                                                  || start.StartsWith("<html", StringComparison.OrdinalIgnoreCase));
                    if (page) { kind = "page"; return null; }
                    if (!hls && !dash) return null;
                    ms.Write(head, 0, n);
                    f = fx.Pump(net, 0, -1, ms, MaxManifest);
                    if (f != null) return f.Kind == DlErrorKind.Client ? DlFailure.Make(DlErrorKind.Client, Tr.S("плейлист слишком большой", "the playlist is too large")) : f;
                    text = Decode(ms.ToArray());
                    kind = hls ? "hls" : "dash";
                    return null;
                }
            }
            catch (Exception ex)
            {
                return DlFailure.Make(DlErrorKind.Network, ex.Message);
            }
            finally
            {
                fx.Release(resp);
            }
        }

        internal static string Decode(byte[] bytes)
        {
            string s = Encoding.UTF8.GetString(bytes);
            return s.Length > 0 && s[0] == '\uFEFF' ? s.Substring(1) : s;
        }

        internal static MdLayout LayoutByType(string contentType, string url)
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            if (ct.Contains("mp2t")) return MdLayout.Ts;
            if (ct.Contains("webm")) return MdLayout.WebM;
            if (ct.StartsWith("video/mp4", StringComparison.Ordinal) || ct.StartsWith("audio/mp4", StringComparison.Ordinal)) return MdLayout.Mp4;
            if (ct.Contains("aac")) return MdLayout.Adts;
            if (ct.Contains("mpeg") && ct.StartsWith("audio/", StringComparison.Ordinal)) return MdLayout.Mp3;
            if (ct.Contains("vtt")) return MdLayout.Vtt;
            MdLayout byName = MdHls.LayoutByName(url);
            return byName == MdLayout.Fmp4 ? MdLayout.Mp4 : byName == MdLayout.Ts && !(url ?? "").ToLowerInvariant().Contains(".ts") ? MdLayout.Unknown : byName;
        }

        public static bool ResolveTrack(MdTrack t, DlItem ctx, Func<bool> cancel, out DlFailure f)
        {
            MdFetcher fx = new MdFetcher(ctx ?? new DlItem(), null, null, null, null, cancel);
            return ResolveTrack(fx, t, ctx != null && ctx.Media != null ? ctx.Media.Headers : null, false, out f);
        }

        // Заполнить сегменты дорожки: media-плейлист HLS, sidx у DASH SegmentBase, прямой файл — кусками.
        // expiredMeansLink — 401/403/404/410 значат «ссылка устарела» (дорожки yt-dlp, продолжение загрузки).
        internal static bool ResolveTrack(MdFetcher fx, MdTrack t, IDictionary<string, string> headers, bool expiredMeansLink, out DlFailure f)
        {
            f = null;
            if (t == null) { f = DlFailure.Make(DlErrorKind.Client, "no track"); return false; }
            Dictionary<string, string> all = Merge(headers, t.Headers);
            MdDash.SidxRef sidx = MdDash.SidxOf(t);
            if (sidx != null) return ResolveSidx(fx, t, sidx, all, expiredMeansLink, out f);
            if (t.Segments.Count > 0) return true;
            if (!DlHttp.IsAllowedScheme(t.Url))
            {
                f = DlFailure.Make(DlErrorKind.Policy, Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"));
                return false;
            }

            // Размер известен (yt-dlp) и куски заданы — без лишнего запроса.
            if (t.SizeHint > 0 && t.ChunkBytes > 0 && t.Layout != MdLayout.Ts)
            {
                Chunk(t, t.Url, t.SizeHint, t.ChunkBytes);
                return true;
            }
            string kind, text, finalUrl, contentType;
            long size;
            bool ranges;
            f = Sniff(fx, t.Url, all, expiredMeansLink || t.FormatId.Length > 0, out kind, out text, out finalUrl, out contentType, out size, out ranges);
            if (f != null) return false;
            if (kind == "page") { f = DlFailure.Make(DlErrorKind.Client, PageRefusal); return false; }
            if (kind == "hls")
            {
                string err;
                bool refused;
                if (!MdHls.ParseMedia(text, finalUrl, t, out err, out refused))
                {
                    f = DlFailure.Make(refused ? DlErrorKind.Policy : DlErrorKind.Client, err);
                    return false;
                }
                return true;
            }
            if (kind == "dash")
            {
                f = DlFailure.Make(DlErrorKind.Client, Tr.S("у дорожки вместо сегментов манифест DASH", "the track points to a DASH manifest instead of segments"));
                return false;
            }
            if (t.Layout == MdLayout.Unknown) t.Layout = LayoutByType(contentType, finalUrl);
            if (size <= 0) size = t.SizeHint;
            if (size > 0) t.SizeHint = size;
            if (size > 0 && (ranges || t.ChunkBytes > 0)) Chunk(t, t.Url, size, t.ChunkBytes > 0 ? t.ChunkBytes : DirectChunk);
            else
            {
                MdSegment whole = new MdSegment();
                whole.Url = t.Url;
                whole.Sequence = 0;
                t.Segments.Add(whole);
            }
            return true;
        }

        private static void Chunk(MdTrack t, string url, long size, long chunk)
        {
            long n = 0;
            for (long off = 0; off < size; off += chunk, n++)
            {
                MdSegment s = new MdSegment();
                s.Url = url;
                s.Offset = off;
                s.Length = Math.Min(chunk, size - off);
                s.Sequence = n;
                t.Segments.Add(s);
            }
        }

        internal static Dictionary<string, string> Merge(IDictionary<string, string> a, IDictionary<string, string> b)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (a != null) foreach (KeyValuePair<string, string> kv in a) d[kv.Key] = kv.Value;
            if (b != null) foreach (KeyValuePair<string, string> kv in b) d[kv.Key] = kv.Value;
            return d;
        }

        // ---------- sidx (ISO/IEC 14496-12 §8.16.3) ----------
        private static bool ResolveSidx(MdFetcher fx, MdTrack t, MdDash.SidxRef r, IDictionary<string, string> headers, bool expiredMeansLink, out DlFailure f)
        {
            f = null;
            t.Segments.Clear();
            List<long[]> refs = new List<long[]>();
            f = ReadSidx(fx, r.Url, r.IndexOffset, r.IndexLength, headers, expiredMeansLink, refs, 0);
            if (f != null) return false;
            long n = 1;
            foreach (long[] x in refs)
            {
                MdSegment s = new MdSegment();
                s.Url = r.Url;
                s.Offset = x[0];
                s.Length = x[1];
                s.Duration = x[2] / 1000.0;
                s.Sequence = n++;
                if (r.InitOffset >= 0)
                {
                    s.InitUrl = r.Url;
                    s.InitOffset = r.InitOffset;
                    s.InitLength = r.InitLength;
                }
                t.Segments.Add(s);
            }
            if (t.Segments.Count == 0) { f = DlFailure.Make(DlErrorKind.Client, Tr.S("индекс sidx пуст", "the sidx index is empty")); return false; }
            return true;
        }

        private static DlFailure ReadSidx(MdFetcher fx, string url, long offset, long length, IDictionary<string, string> headers, bool expiredMeansLink, List<long[]> refs, int depth)
        {
            if (depth > 3) return DlFailure.Make(DlErrorKind.Client, Tr.S("слишком глубокий индекс sidx", "the sidx index is nested too deep"));
            if (length <= 0 || length > MaxManifest) return DlFailure.Make(DlErrorKind.Client, Tr.S("неверный диапазон индекса", "a bad index range"));
            string finalUrl, ct;
            DlFailure f;
            byte[] data = fx.GetBytes(url, offset, length, (int)length, headers, expiredMeansLink, out finalUrl, out ct, out f);
            if (f != null) return f;
            List<long[]> local = new List<long[]>();
            string err;
            if (!ParseSidx(data, offset, local, out err)) return DlFailure.Make(DlErrorKind.Client, err);
            foreach (long[] x in local)
            {
                if (x[3] == 1)
                {
                    f = ReadSidx(fx, url, x[0], x[1], headers, expiredMeansLink, refs, depth + 1);
                    if (f != null) return f;
                }
                else refs.Add(x);
            }
            return null;
        }

        // Ссылки первого sidx в data (data[0] лежит в файле на fileOffset): {смещение, длина, длительность мс, тип}.
        internal static bool ParseSidx(byte[] data, long fileOffset, List<long[]> refs, out string error)
        {
            error = null;
            int pos = 0;
            while (data != null && pos + 8 <= data.Length)
            {
                long size = U32(data, pos);
                string type = Encoding.ASCII.GetString(data, pos + 4, 4);
                int header = 8;
                if (size == 1)
                {
                    if (pos + 16 > data.Length) break;
                    size = (long)((ulong)U32(data, pos + 8) << 32 | (ulong)U32(data, pos + 12));
                    header = 16;
                }
                else if (size == 0) size = data.Length - pos;
                if (size < header || pos + size > data.Length) break;
                if (type != "sidx") { pos += (int)size; continue; }
                int p = pos + header;
                int end = pos + (int)size;
                if (p + 12 > end) break;
                int version = data[p];
                p += 4;                                   // версия и флаги
                p += 4;                                   // reference_ID
                long timescale = U32(data, p);
                p += 4;
                long firstOffset;
                if (version == 0)
                {
                    if (p + 8 > end) break;
                    p += 4;
                    firstOffset = U32(data, p);
                    p += 4;
                }
                else
                {
                    if (p + 16 > end) break;
                    p += 8;
                    firstOffset = (long)((ulong)U32(data, p) << 32 | (ulong)U32(data, p + 4));
                    p += 8;
                }
                if (p + 4 > end) break;
                int count = (data[p + 2] << 8) | data[p + 3];
                p += 4;
                if (p + (long)count * 12 > end) break;
                // Первая ссылка начинается сразу за концом коробки sidx плюс first_offset.
                long at = fileOffset + end + firstOffset;
                for (int i = 0; i < count; i++)
                {
                    long a = U32(data, p);
                    long dur = U32(data, p + 4);
                    p += 12;
                    long refType = (a >> 31) & 1;
                    long refSize = a & 0x7FFFFFFF;
                    if (refSize <= 0) { error = Tr.S("пустая ссылка в sidx", "an empty reference in sidx"); return false; }
                    refs.Add(new long[] { at, refSize, timescale > 0 ? dur * 1000 / timescale : 0, refType });
                    at += refSize;
                }
                return true;
            }
            error = Tr.S("в индексе нет sidx", "no sidx in the index");
            return false;
        }

        private static long U32(byte[] b, int p)
        {
            return ((long)b[p] << 24) | ((long)b[p + 1] << 16) | ((long)b[p + 2] << 8) | b[p + 3];
        }
    }
}
