// SysDeck — «ТВ-трансляция»: HTTP-сервер для телевизора.
// TcpListener, а не HttpListener: тому на адресе не-localhost нужен urlacl от администратора, а здесь достаточно
// разрешения брандмауэра. Протокол — минимальный HTTP/1.1: GET/HEAD, keep-alive, Range для перемотки.
// Общее ядро с отдельным приложением PcStream (см. Tv.Model.cs).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace SysDeck.TvStream
{
    internal sealed class TvRequest
    {
        public string Method, Path, Version;
        public readonly Dictionary<string, string> Query = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string Header(string name) { string v; return Headers.TryGetValue(name, out v) ? v : null; }
        public string Q(string name) { string v; return Query.TryGetValue(name, out v) ? v : null; }

        public bool KeepAlive
        {
            get
            {
                string c = Header("Connection") ?? "";
                if (Version == "HTTP/1.0") return c.IndexOf("keep-alive", StringComparison.OrdinalIgnoreCase) >= 0;
                return c.IndexOf("close", StringComparison.OrdinalIgnoreCase) < 0;
            }
        }
    }

    internal static class TvHttp
    {
        public const int MaxHead = 16 * 1024;
        public const int RangeNone = 0, RangeOk = 1, RangeBad = 2;

        // Заголовок запроса целиком (до пустой строки). null — соединение закрыто или заголовок больше MaxHead.
        public static string ReadHead(Stream s)
        {
            byte[] buf = new byte[MaxHead];
            int len = 0;
            while (len < buf.Length)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                buf[len++] = (byte)b;
                if (len >= 4 && buf[len - 1] == '\n' && buf[len - 2] == '\r' && buf[len - 3] == '\n' && buf[len - 4] == '\r')
                    return Encoding.ASCII.GetString(buf, 0, len);   // адрес в заголовке — только ASCII (проценты)
            }
            return null;
        }

        public static TvRequest Parse(string head)
        {
            if (string.IsNullOrEmpty(head)) return null;
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3) return null;
            TvRequest r = new TvRequest();
            r.Method = first[0].ToUpperInvariant();
            r.Version = first[2];
            string target = first[1];
            int q = target.IndexOf('?');
            string rawPath = q < 0 ? target : target.Substring(0, q);
            if (!rawPath.StartsWith("/")) return null;
            try { r.Path = Uri.UnescapeDataString(rawPath); }
            catch (Exception) { return null; }
            if (q >= 0)
                foreach (string pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string k = eq < 0 ? pair : pair.Substring(0, eq);
                    string v = eq < 0 ? "" : pair.Substring(eq + 1);
                    try { r.Query[Uri.UnescapeDataString(k.Replace('+', ' '))] = Uri.UnescapeDataString(v.Replace('+', ' ')); }
                    catch (Exception) { }
                }
            for (int i = 1; i < lines.Length; i++)
            {
                int c = lines[i].IndexOf(':');
                if (c > 0) r.Headers[lines[i].Substring(0, c).Trim()] = lines[i].Substring(c + 1).Trim();
            }
            return r;
        }

        // «bytes=a-b», «bytes=a-», «bytes=-n». Несколько диапазонов не поддерживаем — отдаём файл целиком (RangeNone).
        public static int ParseRange(string header, long size, out long start, out long end)
        {
            start = 0; end = size - 1;
            if (string.IsNullOrEmpty(header)) return RangeNone;
            header = header.Trim();
            if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return RangeNone;
            string spec = header.Substring(6).Trim();
            if (spec.IndexOf(',') >= 0) return RangeNone;
            int dash = spec.IndexOf('-');
            if (dash < 0) return RangeBad;
            string a = spec.Substring(0, dash).Trim(), b = spec.Substring(dash + 1).Trim();
            long x, y;
            if (a.Length == 0)
            {
                if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out y) || y <= 0) return RangeBad;
                if (size == 0) return RangeBad;
                start = Math.Max(0, size - y);
                end = size - 1;
                return RangeOk;
            }
            if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out x)) return RangeBad;
            if (x >= size) return RangeBad;
            start = x;
            if (b.Length == 0) { end = size - 1; return RangeOk; }
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out y) || y < x) return RangeBad;
            end = Math.Min(y, size - 1);
            return RangeOk;
        }

        public static string Mime(string name)
        {
            string e = (Path.GetExtension(name) ?? "").ToLowerInvariant();
            switch (e)
            {
                case ".mp4": case ".m4v": return "video/mp4";
                case ".webm": return "video/webm";
                case ".mov": return "video/quicktime";
                case ".jpg": return "image/jpeg";
                case ".html": return "text/html; charset=utf-8";
                case ".pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }
    }

    // Кто сейчас смотрит: устройство + файл. Плеер шлёт много Range-запросов подряд — это одна запись,
    // она живёт, пока запросы идут, и пропадает через минуту тишины.
    internal sealed class TvSession
    {
        public string Remote, Device, File, Mode;
        public DateTime Started, LastSeen;
        public int Active;   // открытые прямо сейчас ответы (поток идёт)
    }

    internal sealed class TvServer
    {
        public const int SessionIdleSec = 60;

        private readonly object _lock = new object();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private int _port;
        private readonly List<TcpClient> _clients = new List<TcpClient>();
        private readonly List<Process> _procs = new List<Process>();
        private readonly Dictionary<string, TvSession> _sessions = new Dictionary<string, TvSession>();
        private TvSession _lastVisit;

        // Хост (TvHost) подставляет снимок настроек, ffmpeg, папку кэша и встроенные файлы.
        public Func<TvSettings> Settings;
        public Func<TvFfmpeg> Ffmpeg;
        public string ThumbDir;
        public Func<string, byte[]> Resource;
        public event EventHandler Changed;

        public bool Running { get { return _running; } }
        public int Port { get { return _port; } }

        public bool Start(int port, out string error)
        {
            error = null;
            lock (_lock)
            {
                if (_running) return true;
                TcpListener l = new TcpListener(IPAddress.Any, port);
                try { l.Start(); }
                catch (SocketException ex)
                {
                    error = ex.SocketErrorCode == SocketError.AddressAlreadyInUse
                        ? SysDeck.Tr.S("Порт " + port + " уже занят другой программой — выберите другой.", "Port " + port + " is already in use — pick another one.")
                        : ex.Message;
                    return false;
                }
                _listener = l;
                _port = port;
                _running = true;
                _acceptThread = new Thread(AcceptLoop);
                _acceptThread.IsBackground = true;
                _acceptThread.Name = "tv-accept";
                _acceptThread.Start();
            }
            Fire();
            return true;
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running) return;
                _running = false;
                try { _listener.Stop(); } catch (Exception) { }
                foreach (TcpClient c in _clients) { try { c.Close(); } catch (Exception) { } }
                _clients.Clear();
                foreach (Process p in _procs) { try { if (!p.HasExited) p.Kill(); } catch (Exception) { } }
                _procs.Clear();
                _sessions.Clear();
            }
            Fire();
        }

        // Последнее обращение с другого устройства (не с этого компьютера): пошаговое окно по нему видит,
        // что телевизор уже достучался — Media Station X («msx») или браузер («browser»), — ещё до первого видео.
        public TvSession LastVisit()
        {
            lock (_lock) return _lastVisit;
        }

        private void NoteVisit(string remote, string userAgent, string path)
        {
            if (remote == "127.0.0.1" || remote == "::1") return;
            string app = path.StartsWith("/msx/") ? "msx" : "browser";
            bool changed;
            lock (_lock)
            {
                changed = _lastVisit == null || _lastVisit.Remote != remote || _lastVisit.Mode != app;
                if (changed)
                {
                    _lastVisit = new TvSession();
                    _lastVisit.Remote = remote;
                    _lastVisit.Device = TvNet.DeviceName(userAgent);
                    _lastVisit.Mode = app;
                    _lastVisit.Started = DateTime.Now;
                }
                _lastVisit.LastSeen = DateTime.Now;
            }
            if (changed) Fire();
        }

        public List<TvSession> Sessions()
        {
            List<TvSession> list = new List<TvSession>();
            lock (_lock)
            {
                List<string> dead = new List<string>();
                foreach (KeyValuePair<string, TvSession> kv in _sessions)
                {
                    if (kv.Value.Active == 0 && (DateTime.Now - kv.Value.LastSeen).TotalSeconds > SessionIdleSec) dead.Add(kv.Key);
                    else list.Add(kv.Value);
                }
                foreach (string k in dead) _sessions.Remove(k);
            }
            list.Sort(delegate(TvSession a, TvSession b) { return b.Started.CompareTo(a.Started); });
            return list;
        }

        private void Fire()
        {
            EventHandler h = Changed;
            if (h != null) { try { h(this, EventArgs.Empty); } catch (Exception) { } }
        }

        private void AcceptLoop()
        {
            TcpListener l = _listener;
            while (_running)
            {
                TcpClient c;
                try { c = l.AcceptTcpClient(); }
                catch (Exception) { break; }   // Stop() закрыл слушатель
                lock (_lock)
                {
                    if (!_running) { c.Close(); break; }
                    _clients.Add(c);
                }
                ThreadPool.QueueUserWorkItem(Serve, c);
            }
        }

        private void Serve(object state)
        {
            TcpClient c = (TcpClient)state;
            try
            {
                IPEndPoint ep = c.Client.RemoteEndPoint as IPEndPoint;
                c.NoDelay = true;
                c.ReceiveTimeout = 30000;   // простаивающий keep-alive не держит поток вечно
                c.SendTimeout = 60000;
                NetworkStream ns = c.GetStream();
                while (_running)
                {
                    TvRequest r = TvHttp.Parse(TvHttp.ReadHead(ns));
                    if (r == null) break;
                    if (ep == null || !TvNet.IsLocal(ep.Address))
                    {
                        SendText(ns, 403, "Forbidden", "text/plain", "PC Stream: only the local network is served", false);
                        break;
                    }
                    if (!Handle(r, ns, ep.Address.ToString()) || !r.KeepAlive) break;
                }
            }
            catch (Exception) { }   // клиент оборвал соединение — обычное дело для видеоплеера
            finally
            {
                lock (_lock) _clients.Remove(c);
                try { c.Close(); } catch (Exception) { }
            }
        }

        // true — соединение можно использовать дальше (ответ с известной длиной).
        private bool Handle(TvRequest r, Stream s, string remote)
        {
            if (r.Method != "GET" && r.Method != "HEAD") return SendText(s, 405, "Method Not Allowed", "text/plain", "", r.KeepAlive);
            bool head = r.Method == "HEAD";
            NoteVisit(remote, r.Header("User-Agent"), r.Path ?? "");
            TvSettings set = Settings();
            List<TvFolder> roots = set.Folders;
            string host = r.Header("Host");
            if (string.IsNullOrEmpty(host) || host.IndexOfAny(new[] { '/', '\\', '"', ' ' }) >= 0) host = TvNet.LanAddress() + ":" + _port;
            string baseUrl = "http://" + host;
            string p = r.Path;

            if (p == "/" || p == "/index.html") return SendResource(s, "tvstream/index.html", "text/html; charset=utf-8", head, r.KeepAlive);
            if (p == "/guide.pdf") return SendResource(s, "tvstream/guide.pdf", "application/pdf", head, r.KeepAlive);
            if (p == "/favicon.ico") return SendText(s, 204, "No Content", "text/plain", "", r.KeepAlive);

            TvFfmpeg ff = Ffmpeg();
            if (p == "/api/info") return SendJson(s, TvApi.Info(TvNet.LanAddress(), _port, roots, ff.Available), head, r.KeepAlive);
            if (p == "/msx/start.json") return SendJson(s, TvMsx.Start(baseUrl), head, r.KeepAlive);
            if (p == "/msx/menu.json") return SendJson(s, TvMsx.Menu(baseUrl, roots), head, r.KeepAlive);

            if (p == "/api/list" || p == "/msx/list.json")
            {
                int root;
                if (!int.TryParse(r.Q("root") ?? "", NumberStyles.None, CultureInfo.InvariantCulture, out root)) return NotFound(s, r);
                string rel = (r.Q("path") ?? "").Replace('\\', '/').Trim('/');
                string dir = TvLibrary.Resolve(roots, root, rel);
                if (dir == null || !Directory.Exists(dir)) return NotFound(s, r);
                List<string> dirs = new List<string>(), files = new List<string>();
                try { TvLibrary.List(dir, dirs, files); }
                catch (Exception) { return SendText(s, 500, "Internal Server Error", "text/plain", "cannot read folder", r.KeepAlive); }
                if (p == "/msx/list.json")
                {
                    string headline = rel.Length == 0 ? roots[root].Name : rel.Substring(rel.LastIndexOf('/') + 1);
                    return SendJson(s, TvMsx.List(baseUrl, root, rel, headline, dirs, files), head, r.KeepAlive);
                }
                return SendJson(s, TvApi.List(root, rel, dirs, files, delegate(string fileRel)
                {
                    string full = TvLibrary.Resolve(roots, root, fileRel);
                    return full == null ? 0 : ff.Probe(full).Duration;
                }), head, r.KeepAlive);
            }

            string kind = null, tail = null;
            foreach (string k in new[] { "media", "tx", "thumb" })
                if (p.StartsWith("/" + k + "/")) { kind = k; tail = p.Substring(k.Length + 2); break; }
            if (kind == null) return NotFound(s, r);
            int slash = tail.IndexOf('/');
            int rootIdx;
            if (slash <= 0 || !int.TryParse(tail.Substring(0, slash), NumberStyles.None, CultureInfo.InvariantCulture, out rootIdx)) return NotFound(s, r);
            string file = TvLibrary.Resolve(roots, rootIdx, tail.Substring(slash + 1));
            if (file == null || !File.Exists(file) || !TvLibrary.IsVideo(file)) return NotFound(s, r);

            if (kind == "thumb")
            {
                string jpg = set.Thumbnails ? ff.Thumbnail(file, ThumbDir) : null;
                if (jpg == null) return NotFound(s, r);
                return SendFile(s, r, jpg, head, null);
            }
            // Без ffmpeg поток не начнётся — и зрителя в списке быть не должно.
            if (kind == "tx" && !ff.Available) return SendTranscode(s, file, 0, head);
            TvSession ses = Touch(remote, r.Header("User-Agent"), file, kind == "tx" ? "transcode" : "direct");
            try
            {
                if (kind == "media") return SendFile(s, r, file, head, ses);
                double t;
                double.TryParse(r.Q("t") ?? "0", NumberStyles.Float, CultureInfo.InvariantCulture, out t);
                return SendTranscode(s, file, t, head);
            }
            finally
            {
                lock (_lock) { ses.Active--; ses.LastSeen = DateTime.Now; }
                Fire();
            }
        }

        private TvSession Touch(string remote, string ua, string file, string mode)
        {
            TvSession ses;
            bool added = false;
            lock (_lock)
            {
                string key = remote + "|" + file;
                if (!_sessions.TryGetValue(key, out ses))
                {
                    ses = new TvSession();
                    ses.Remote = remote;
                    ses.Device = TvNet.DeviceName(ua);
                    ses.File = file;
                    ses.Started = DateTime.Now;
                    _sessions[key] = ses;
                    added = true;
                }
                ses.Mode = mode;
                ses.LastSeen = DateTime.Now;
                ses.Active++;
            }
            if (added) Fire();
            return ses;
        }

        // ---------- ответы ----------
        private static void WriteHead(Stream s, int code, string reason, string type, long length, bool keepAlive, string extra)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Server: PC Stream\r\n");
            if (type != null) sb.Append("Content-Type: ").Append(type).Append("\r\n");
            if (length >= 0) sb.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Access-Control-Allow-Origin: *\r\n");   // Media Station X грузит JSON со своего адреса
            sb.Append(keepAlive && length >= 0 ? "Connection: keep-alive\r\n" : "Connection: close\r\n");
            if (extra != null) sb.Append(extra);
            sb.Append("\r\n");
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            s.Write(b, 0, b.Length);
        }

        private static bool SendBytes(Stream s, int code, string reason, string type, byte[] body, bool head, bool keepAlive, string extra)
        {
            WriteHead(s, code, reason, type, body.Length, keepAlive, extra);
            if (!head && body.Length > 0) s.Write(body, 0, body.Length);
            s.Flush();
            return keepAlive;
        }

        private static bool SendText(Stream s, int code, string reason, string type, string text, bool keepAlive)
        {
            return SendBytes(s, code, reason, type, Encoding.UTF8.GetBytes(text ?? ""), false, keepAlive, null);
        }

        private static bool SendJson(Stream s, string json, bool head, bool keepAlive)
        {
            return SendBytes(s, 200, "OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), head, keepAlive, "Cache-Control: no-cache\r\n");
        }

        private static bool NotFound(Stream s, TvRequest r) { return SendText(s, 404, "Not Found", "text/plain", "not found", r.KeepAlive); }

        private bool SendResource(Stream s, string name, string type, bool head, bool keepAlive)
        {
            byte[] b = Resource == null ? null : Resource(name);
            if (b == null) return SendText(s, 404, "Not Found", "text/plain", "missing resource " + name, keepAlive);
            return SendBytes(s, 200, "OK", type, b, head, keepAlive, "Cache-Control: no-cache\r\n");
        }

        private bool SendFile(Stream s, TvRequest r, string file, bool head, TvSession ses)
        {
            using (FileStream fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
            {
                long size = fs.Length, start, end;
                int range = TvHttp.ParseRange(r.Header("Range"), size, out start, out end);
                string type = TvHttp.Mime(file);
                if (range == TvHttp.RangeBad)
                {
                    WriteHead(s, 416, "Range Not Satisfiable", type, 0, r.KeepAlive, "Content-Range: bytes */" + size + "\r\n");
                    s.Flush();
                    return r.KeepAlive;
                }
                long len = end - start + 1;
                if (range == TvHttp.RangeOk)
                    WriteHead(s, 206, "Partial Content", type, len, r.KeepAlive, "Accept-Ranges: bytes\r\nContent-Range: bytes " + start + "-" + end + "/" + size + "\r\n");
                else
                    WriteHead(s, 200, "OK", type, size, r.KeepAlive, "Accept-Ranges: bytes\r\n" + (ses == null ? "Cache-Control: max-age=86400\r\n" : ""));
                if (!head && len > 0)
                {
                    fs.Position = start;
                    byte[] buf = new byte[256 * 1024];
                    long left = len;
                    while (left > 0 && _running)
                    {
                        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                        if (n <= 0) break;
                        s.Write(buf, 0, n);
                        left -= n;
                        if (ses != null) ses.LastSeen = DateTime.Now;
                    }
                }
                s.Flush();
                return r.KeepAlive;
            }
        }

        // Поток ffmpeg без длины: ответ закрывает соединение (Connection: close), перемотка — новый запрос с ?t=.
        private bool SendTranscode(Stream s, string file, double t, bool head)
        {
            TvFfmpeg ff = Ffmpeg();
            if (!ff.Available)
            {
                SendText(s, 501, "Not Implemented", "text/plain", "ffmpeg is not installed on the PC", false);
                return false;
            }
            WriteHead(s, 200, "OK", "video/mp4", -1, false, "Cache-Control: no-cache\r\n");
            if (head) { s.Flush(); return false; }
            Process p = ff.StartTranscode(file, t);
            if (p == null) return false;
            lock (_lock)
            {
                if (!_running) { try { p.Kill(); } catch (Exception) { } return false; }
                _procs.Add(p);
            }
            try
            {
                Stream src = p.StandardOutput.BaseStream;
                byte[] buf = new byte[256 * 1024];
                int n;
                while (_running && (n = src.Read(buf, 0, buf.Length)) > 0) s.Write(buf, 0, n);
                s.Flush();
            }
            finally
            {
                // Телевизор закрыл поток (перемотка, выход) — ffmpeg больше не нужен.
                try { if (!p.HasExited) p.Kill(); } catch (Exception) { }
                lock (_lock) _procs.Remove(p);
                p.Dispose();
            }
            return false;
        }
    }
}
