// SysDeck — область «torrent»: трекеры — HTTP и UDP обмен.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed partial class BtTrackers : IBtTrackers, IBtUdpHandler
    {
        // ------------------------------------------------------------------ //
        //  HTTP
        // ------------------------------------------------------------------ //
        private void HttpBeginLocked(Entry e, Stats st, BtAnnounceEvent ev, bool oneShot, List<Action> after)
        {
            string url = HttpUrl(e, st, ev);
            int attempt = e.Attempt;
            after.Add(delegate
            {
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Reply r = HttpFetch(url, !oneShot);
                    if (!oneShot) Complete(e, attempt, r);
                });
            });
        }

        private string HttpUrl(Entry e, Stats st, BtAnnounceEvent ev)
        {
            string url = e.Url;
            int hash = url.IndexOf('#');
            if (hash >= 0) url = url.Substring(0, hash);
            StringBuilder sb = new StringBuilder(url);
            if (!url.EndsWith("?") && !url.EndsWith("&")) sb.Append(url.IndexOf('?') >= 0 ? '&' : '?');
            sb.Append("info_hash=").Append(Escape(st.InfoHash));
            sb.Append("&peer_id=").Append(Escape(_ctx.PeerId));
            sb.Append("&port=").Append(_ctx.Port.ToString(CultureInfo.InvariantCulture));
            sb.Append("&uploaded=").Append(st.Up.ToString(CultureInfo.InvariantCulture));
            sb.Append("&downloaded=").Append(st.Down.ToString(CultureInfo.InvariantCulture));
            sb.Append("&left=").Append(st.Left.ToString(CultureInfo.InvariantCulture));
            sb.Append("&compact=1&no_peer_id=1");
            sb.Append("&numwant=").Append((ev == BtAnnounceEvent.Stopped ? 0 : st.NumWant).ToString(CultureInfo.InvariantCulture));
            sb.Append("&key=").Append(_ctx.AnnounceKey.ToString("x8", CultureInfo.InvariantCulture));
            if (ev == BtAnnounceEvent.Started) sb.Append("&event=started");
            else if (ev == BtAnnounceEvent.Completed) sb.Append("&event=completed");
            else if (ev == BtAnnounceEvent.Stopped) sb.Append("&event=stopped");
            if (!string.IsNullOrEmpty(e.TrackerId)) sb.Append("&trackerid=").Append(Escape(Encoding.UTF8.GetBytes(e.TrackerId)));
            if (_ctx.Encryption == BtEncryption.Require) sb.Append("&supportcrypto=1&requirecrypto=1");
            else if (_ctx.Encryption == BtEncryption.Prefer) sb.Append("&supportcrypto=1");
            return sb.ToString();
        }

        // Байты как есть: незарезервированные символы RFC 3986 — буквой, остальное — %XX (Uri их уже не переписывает).
        internal static string Escape(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b == null ? 0 : b.Length * 3);
            if (b == null) return "";
            foreach (byte x in b)
            {
                char c = (char)x;
                if (c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '.' || c == '_' || c == '~') sb.Append(c);
                else sb.Append('%').Append(x.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private Reply HttpFetch(string url, bool abortOnDispose)
        {
            HttpWebRequest req = null;
            Stopwatch clock = Stopwatch.StartNew();
            try
            {
                req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "GET";
                req.AllowAutoRedirect = true;
                req.MaximumAutomaticRedirections = MaxRedirects;
                req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
                req.UserAgent = BtContext.ClientName;
                req.KeepAlive = false;
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                // Два соединения на хост по умолчанию: объявления десятка торрентов на один трекер стояли бы в очереди.
                try { if (req.ServicePoint.ConnectionLimit < 16) req.ServicePoint.ConnectionLimit = 16; } catch { }
                if (abortOnDispose)
                    lock (_live)
                    {
                        if (_disposed) return Fail(Tr.S("остановлено", "stopped"));
                        _live.Add(req);
                    }
                using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                {
                    string error;
                    byte[] body = ReadCapped(resp, req, clock, out error);
                    if (body == null) return Fail(error);
                    return ParseHttpBody(body);
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse resp = ex.Response as HttpWebResponse;
                if (resp != null)
                {
                    using (resp)
                    {
                        int code = (int)resp.StatusCode;
                        if (code >= 300 && code < 400) return Fail(Tr.S("слишком много перенаправлений", "too many redirects"));
                        // Часть трекеров отдаёт failure reason с кодом 4xx.
                        string ignored;
                        byte[] body = ReadCapped(resp, req, clock, out ignored);
                        if (body != null)
                        {
                            Reply parsed = ParseHttpBody(body);
                            if (parsed.Failure != null) return parsed;
                        }
                        return Fail("HTTP " + code.ToString(CultureInfo.InvariantCulture));
                    }
                }
                return Fail(WebErrorText(ex.Status));
            }
            catch (Exception ex)
            {
                // Текст исключения может содержать адрес — в журнал только тип.
                return Fail(Tr.S("сбой запроса: ", "request failed: ") + ex.GetType().Name);
            }
            finally
            {
                if (req != null) lock (_live) _live.Remove(req);
            }
        }

        private static string WebErrorText(WebExceptionStatus s)
        {
            switch (s)
            {
                case WebExceptionStatus.Timeout: return Tr.S("нет ответа за 15 с", "no reply within 15 s");
                case WebExceptionStatus.NameResolutionFailure: return Tr.S("имя трекера не найдено", "tracker host not found");
                case WebExceptionStatus.ConnectFailure: return Tr.S("соединение не установлено", "connection failed");
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure: return Tr.S("сертификат HTTPS не принят", "HTTPS certificate rejected");
                case WebExceptionStatus.RequestCanceled: return Tr.S("остановлено", "stopped");
                default: return Tr.S("сбой соединения: ", "connection error: ") + s;
            }
        }

        private static byte[] ReadCapped(HttpWebResponse resp, HttpWebRequest req, Stopwatch clock, out string error)
        {
            error = null;
            try
            {
                using (Stream s = resp.GetResponseStream())
                using (MemoryStream ms = new MemoryStream())
                {
                    byte[] buf = new byte[16384];
                    while (true)
                    {
                        if (clock.ElapsedMilliseconds > HttpTimeoutMs)
                        {
                            try { req.Abort(); } catch { }
                            error = Tr.S("нет ответа за 15 с", "no reply within 15 s");
                            return null;
                        }
                        int n = s.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        if (ms.Length + n > MaxBodyBytes)
                        {
                            try { req.Abort(); } catch { }
                            error = Tr.S("ответ трекера больше 2 МБ", "tracker reply larger than 2 MB");
                            return null;
                        }
                        ms.Write(buf, 0, n);
                    }
                    return ms.ToArray();
                }
            }
            catch (IOException) { error = Tr.S("ответ оборвался", "reply interrupted"); }
            catch (WebException) { error = Tr.S("ответ оборвался", "reply interrupted"); }
            catch (ObjectDisposedException) { error = Tr.S("остановлено", "stopped"); }
            catch (InvalidDataException) { error = Tr.S("сжатый ответ повреждён", "compressed reply is corrupt"); }
            return null;
        }

        private static Reply ParseHttpBody(byte[] body)
        {
            string err;
            BVal root = Bencode.Decode(body, out err);
            if (root == null || root.Kind != BKind.Dict) return Fail(Tr.S("ответ трекера не разобран", "tracker reply is not bencode"));
            Reply r = new Reply();
            string failure = root.GetStr("failure reason");
            if (failure != null)
            {
                r.Failure = CleanText(failure);
                r.Error = r.Failure;
                return r;
            }
            r.Ok = true;
            r.Warning = CleanText(root.GetStr("warning message"));
            r.Interval = ClampInt(root.GetInt("interval", -1), 30, 86400);
            r.MinInterval = ClampInt(root.GetInt("min interval", -1), 0, 86400);
            r.Seeders = ClampInt(root.GetInt("complete", -1), 0, int.MaxValue);
            r.Leechers = ClampInt(root.GetInt("incomplete", -1), 0, int.MaxValue);
            r.Downloaded = ClampInt(root.GetInt("downloaded", -1), 0, int.MaxValue);
            string id = root.GetStr("tracker id");
            if (id != null && id.Length <= 256) r.TrackerId = id;

            BVal peers = root.Get("peers");
            if (peers != null && peers.Kind == BKind.Bytes) AddCapped(r.Peers, BtEndpoint.ParseCompact(peers.B, false));
            else if (peers != null && peers.Kind == BKind.List)
                foreach (BVal p in peers.L)
                {
                    if (r.Peers.Count >= MaxPeersPerReply) break;
                    if (p.Kind != BKind.Dict) continue;
                    string ipText = p.GetStr("ip");
                    long port = p.GetInt("port", 0);
                    IPAddress ip;
                    // Только литерал адреса: имя хоста из ответа не разрешается.
                    if (ipText == null || port <= 0 || port > 65535 || !IPAddress.TryParse(ipText, out ip)) continue;
                    BtEndpoint ep = new BtEndpoint(ip, (int)port);
                    if (ep.IsUsable) r.Peers.Add(ep);
                }
            byte[] peers6 = root.GetBytes("peers6");
            if (peers6 != null) AddCapped(r.Peers, BtEndpoint.ParseCompact(peers6, true));
            byte[] ext = root.GetBytes("external ip");
            if (ext != null && (ext.Length == 4 || ext.Length == 16)) r.External = new IPAddress(ext);
            return r;
        }

        private static void AddCapped(List<BtEndpoint> to, List<BtEndpoint> from)
        {
            foreach (BtEndpoint ep in from)
            {
                if (to.Count >= MaxPeersPerReply) return;
                to.Add(ep);
            }
        }

        private static int ClampInt(long v, int min, int max)
        {
            if (v < 0) return -1;
            if (v < min) return min;
            return v > max ? max : (int)v;
        }

        // ------------------------------------------------------------------ //
        //  UDP (BEP 15, BEP 41)
        // ------------------------------------------------------------------ //
        private void UdpBeginLocked(Entry e, DateTime now, Stats st, List<Action> after)
        {
            if (_ctx.Udp == null)
            {
                CompleteLocked(e, Fail(Tr.S("UDP-сокет сессии не запущен", "session UDP socket is not running")), now, st, after);
                return;
            }
            int port = e.Uri.Port;
            if (port <= 0 || port > 65535)
            {
                CompleteLocked(e, Fail(Tr.S("в адресе нет порта", "no port in the address")), now, st, after);
                return;
            }
            if (e.UdpEp == null || (now - e.ResolvedUtc).TotalMinutes >= 30)
            {
                IPAddress ip;
                string host = e.Uri.Host.Trim('[', ']');
                if (IPAddress.TryParse(host, out ip))
                {
                    e.UdpEp = new BtEndpoint(ip, port);
                    e.ResolvedUtc = now;
                }
                else
                {
                    // DNS — только в пуле: вызывающий может быть потоком UDP или таймером.
                    int attempt = e.Attempt;
                    after.Add(delegate { ThreadPool.QueueUserWorkItem(delegate { Resolve(e, attempt, host, port); }); });
                    return;
                }
            }
            if (e.UdpEp.IsV6 || !e.UdpEp.IsUsable)
            {
                CompleteLocked(e, Fail(Tr.S("адрес UDP-трекера не IPv4", "UDP tracker address is not IPv4")), now, st, after);
                return;
            }
            UdpFirstPacketLocked(e, now, st, after);
        }

        private void Resolve(Entry e, int attempt, string host, int port)
        {
            IPAddress found = null;
            try
            {
                foreach (IPAddress a in Dns.GetHostAddresses(host))
                    if (a.AddressFamily == AddressFamily.InterNetwork) { found = a; break; }
            }
            catch (SocketException) { }
            catch (ArgumentException) { }
            Stats st = Snap();
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (_disposed || e.Attempt != attempt || !e.InFlight) return;
                DateTime now = DateTime.UtcNow;
                if (found == null) CompleteLocked(e, Fail(Tr.S("имя трекера не найдено", "tracker host not found")), now, st, after);
                else
                {
                    e.UdpEp = new BtEndpoint(found, port);
                    e.ResolvedUtc = now;
                    if (!e.UdpEp.IsUsable) CompleteLocked(e, Fail(Tr.S("адрес UDP-трекера не подходит", "UDP tracker address is unusable")), now, st, after);
                    else UdpFirstPacketLocked(e, now, st, after);
                }
            }
            Run(after);
        }

        private void UdpFirstPacketLocked(Entry e, DateTime now, Stats st, List<Action> after)
        {
            e.Tries = 0;
            if (e.ConnUtc != DateTime.MinValue && (now - e.ConnUtc).TotalSeconds < 60)
            {
                e.Phase = 2;
                e.Packet = AnnouncePacket(e, st, NewTidLocked(e));
            }
            else
            {
                e.Phase = 1;
                e.Packet = ConnectPacket(NewTidLocked(e));
            }
            e.SentUtc = now;
            SendLocked(e, after);
        }

        private void UdpTimerLocked(Entry e, DateTime now, Stats st, List<Action> after)
        {
            double wait = UdpRetransmitBaseSeconds * (1 << e.Tries);
            if ((now - e.SentUtc).TotalSeconds < wait) return;
            if (e.OneShot)
            {
                e.InFlight = false;
                e.Phase = 0;
                DropTidLocked(e);
                return;
            }
            if (e.Tries >= UdpMaxRetransmits)
            {
                CompleteLocked(e, Fail(Tr.S("UDP-трекер не отвечает", "UDP tracker does not reply")), now, st, after);
                return;
            }
            e.Tries++;
            // connection id истёк, пока повторяли announce, — BEP 15 требует нового connect.
            if (e.Phase == 2 && (now - e.ConnUtc).TotalSeconds >= 60)
            {
                e.Phase = 1;
                e.ConnUtc = DateTime.MinValue;
                e.Packet = ConnectPacket(NewTidLocked(e));
            }
            e.SentUtc = now;
            SendLocked(e, after);
        }

        public bool HandleDatagram(BtEndpoint from, byte[] data, int count)
        {
            if (data == null || count < 8 || count > data.Length) return false;
            int action = GetInt32(data, 0);
            if (action < 0 || action > 3) return false;   // DHT («d1:…») сюда не проходит
            int tid = GetInt32(data, 4);
            // Сокет общий на все торренты: чужой ответ отсекается до чтения роя.
            lock (_gate) if (!MatchLocked(tid, from)) return false;
            Stats st = Snap();
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (!MatchLocked(tid, from)) return true;
                Entry e = _byTid[tid];
                DateTime now = DateTime.UtcNow;
                if (action == 3)
                {
                    string msg = CleanText(Encoding.UTF8.GetString(data, 8, Math.Min(count - 8, 1024)));
                    Reply r = Fail(msg);
                    r.Failure = msg;
                    CompleteLocked(e, r, now, st, after);
                }
                else if (action == 0 && e.Phase == 1 && count >= 16)
                {
                    e.ConnId = GetInt64(data, 8);
                    e.ConnUtc = now;
                    e.Phase = 2;
                    e.Tries = 0;
                    e.Packet = AnnouncePacket(e, st, NewTidLocked(e));
                    e.SentUtc = now;
                    SendLocked(e, after);
                }
                else if (action == 1 && e.Phase == 2 && count >= 20)
                {
                    Reply r = new Reply();
                    r.Ok = true;
                    r.Interval = ClampInt(GetInt32(data, 8), 30, 86400);
                    r.Leechers = ClampInt(GetInt32(data, 12), 0, int.MaxValue);
                    r.Seeders = ClampInt(GetInt32(data, 16), 0, int.MaxValue);
                    int n = Math.Min((count - 20) / 6, MaxPeersPerReply);
                    byte[] compact = new byte[n * 6];
                    Buffer.BlockCopy(data, 20, compact, 0, compact.Length);
                    r.Peers.AddRange(BtEndpoint.ParseCompact(compact, false));
                    CompleteLocked(e, r, now, st, after);
                }
            }
            Run(after);
            return true;
        }

        private bool MatchLocked(int tid, BtEndpoint from)
        {
            Entry e;
            return !_disposed && _byTid.TryGetValue(tid, out e) && e.UdpEp != null && e.UdpEp.Equals(from);
        }

        private int NewTidLocked(Entry e)
        {
            DropTidLocked(e);
            byte[] b = new byte[4];
            int tid;
            do
            {
                Rng.GetBytes(b);
                tid = BitConverter.ToInt32(b, 0);
            } while (tid == 0 || _byTid.ContainsKey(tid));
            e.Tid = tid;
            _byTid[tid] = e;
            return tid;
        }

        private void DropTidLocked(Entry e)
        {
            if (e.Tid != 0)
            {
                Entry owner;
                if (_byTid.TryGetValue(e.Tid, out owner) && owner == e) _byTid.Remove(e.Tid);
                e.Tid = 0;
            }
        }

        private void SendLocked(Entry e, List<Action> after)
        {
            IBtUdp udp = _ctx.Udp;
            BtEndpoint to = e.UdpEp;
            byte[] packet = e.Packet;
            if (udp == null || to == null || packet == null) return;
            after.Add(delegate { udp.Send(to, packet, packet.Length); });
        }

        private static byte[] ConnectPacket(int tid)
        {
            byte[] b = new byte[16];
            PutInt64(b, 0, UdpProtocolId);
            PutInt32(b, 8, 0);
            PutInt32(b, 12, tid);
            return b;
        }

        private byte[] AnnouncePacket(Entry e, Stats st, int tid)
        {
            MemoryStream ms = new MemoryStream(128);
            byte[] b = new byte[98];
            PutInt64(b, 0, e.ConnId);
            PutInt32(b, 8, 1);
            PutInt32(b, 12, tid);
            if (st.InfoHash != null) Buffer.BlockCopy(st.InfoHash, 0, b, 16, Math.Min(20, st.InfoHash.Length));
            if (_ctx.PeerId != null) Buffer.BlockCopy(_ctx.PeerId, 0, b, 36, Math.Min(20, _ctx.PeerId.Length));
            PutInt64(b, 56, st.Down);
            PutInt64(b, 64, st.Left);
            PutInt64(b, 72, st.Up);
            int ev = e.Event == BtAnnounceEvent.Completed ? 1 : e.Event == BtAnnounceEvent.Started ? 2 : e.Event == BtAnnounceEvent.Stopped ? 3 : 0;
            PutInt32(b, 80, ev);
            PutInt32(b, 84, 0);
            PutInt32(b, 88, (int)_ctx.AnnounceKey);
            PutInt32(b, 92, e.Event == BtAnnounceEvent.Stopped ? 0 : st.NumWant);
            b[96] = (byte)(_ctx.Port >> 8);
            b[97] = (byte)_ctx.Port;
            ms.Write(b, 0, b.Length);
            // BEP 41: путь и query трекера (в них бывает passkey) — кусками по 255 байт, в конце «конец опций».
            string pq = e.Uri.PathAndQuery;
            if (!string.IsNullOrEmpty(pq) && pq != "/")
            {
                byte[] url = Encoding.UTF8.GetBytes(pq);
                for (int i = 0; i < url.Length; i += 255)
                {
                    int n = Math.Min(255, url.Length - i);
                    ms.WriteByte(2);
                    ms.WriteByte((byte)n);
                    ms.Write(url, i, n);
                }
                ms.WriteByte(0);
            }
            return ms.ToArray();
        }

        internal static int GetInt32(byte[] b, int o) { return (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]; }

        internal static long GetInt64(byte[] b, int o) { return ((long)(uint)GetInt32(b, o) << 32) | (uint)GetInt32(b, o + 4); }

        internal static void PutInt32(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24);
            b[o + 1] = (byte)(v >> 16);
            b[o + 2] = (byte)(v >> 8);
            b[o + 3] = (byte)v;
        }

        internal static void PutInt64(byte[] b, int o, long v)
        {
            PutInt32(b, o, (int)(v >> 32));
            PutInt32(b, o + 4, (int)v);
        }
    }
}
