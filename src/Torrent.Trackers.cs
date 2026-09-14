// Windows Process Cleaner — «Загрузки», торренты: трекеры HTTP(S) и UDP (BEP 3/12/15/23/24/41).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Порядок BEP 12: уровни по очереди, внутри уровня трекеры перемешаны один раз; ответивший встаёт первым в своём уровне,
// следующий уровень — только когда весь текущий не ответил. Молчащий дольше FallbackAfterSeconds трекер очередь не держит
// (у UDP по BEP 15 повторы идут часами): следующий спрашивается параллельно, первый успешный ответ закрывает круг.
// HTTP — пул потоков, таймаут 15 с, ответ не больше 2 МБ, не больше 3 перенаправлений. UDP — общий сокет сессии, повторы
// через 15·2^n с (n ≤ 8), connection id живёт 60 с. Обращения к рою — только вне своей блокировки: рой может звать нас
// из-под своей. Адрес трекера в журнал и карточку — только через BtRedact.Url: в пути и query бывает passkey.
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

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class BtTrackers : IBtTrackers, IBtUdpHandler
    {
        // Тесты уменьшают времена и выключают перемешивание (восстанавливают в finally).
        internal static double UdpRetransmitBaseSeconds = 15;
        internal static double FallbackAfterSeconds = 30;
        internal static int HttpTimeoutMs = 15000;
        internal static int DefaultMinIntervalSeconds = 30;
        internal static bool ShuffleTiers = true;

        internal const int UdpMaxRetransmits = 8;
        internal const int MaxBodyBytes = 2 * 1024 * 1024;
        internal const int MaxRedirects = 3;
        private const long UdpProtocolId = 0x41727101980L;
        private const int MaxPeersPerReply = 2000;
        private const int DefaultInterval = 1800;

        private enum TrState { NotContacted, Updating, Working, Error }

        private sealed class Entry
        {
            public string Url;
            public string Shown;
            public Uri Uri;
            public bool Udp;
            public TrState State;
            public TrState StateBefore;
            public string ErrorText = "";
            public string Message = "";
            public int Seeders = -1, Leechers = -1, Downloaded = -1;
            public int PeersReceived;
            public int Interval, MinInterval;
            public string TrackerId;
            public bool EverWorked;

            public bool InFlight;
            public int Attempt;              // ответы прежних попыток отбрасываются
            public int Round;
            public DateTime StartedUtc;
            public BtAnnounceEvent Event;
            public bool OneShot;             // Stopped: без повторов, на круг не влияет

            public BtEndpoint UdpEp;
            public DateTime ResolvedUtc = DateTime.MinValue;
            public long ConnId;
            public DateTime ConnUtc = DateTime.MinValue;
            public int Phase;                // 0 — нет, 1 — connect, 2 — announce
            public int Tid;
            public int Tries;
            public DateTime SentUtc;
            public byte[] Packet;
        }

        private sealed class Reply
        {
            public bool Ok;
            public string Error = "";        // уже на языке интерфейса
            public string Failure;           // failure reason от трекера
            public string Warning;
            public int Interval = -1, MinInterval = -1;
            public int Seeders = -1, Leechers = -1, Downloaded = -1;
            public string TrackerId;
            public readonly List<BtEndpoint> Peers = new List<BtEndpoint>();
            public IPAddress External;
        }

        private sealed class Stats
        {
            public byte[] InfoHash;
            public long Up, Down, Left;
            public int NumWant;
        }

        private static readonly RandomNumberGenerator Rng = RandomNumberGenerator.Create();

        private readonly IBtSwarm _swarm;
        private readonly BtContext _ctx;
        private readonly object _gate = new object();
        private readonly List<List<Entry>> _tiers = new List<List<Entry>>();
        private readonly Dictionary<int, Entry> _byTid = new Dictionary<int, Entry>();
        private readonly List<HttpWebRequest> _live = new List<HttpWebRequest>();
        private readonly Random _shuffle = new Random(Environment.TickCount ^ 0x5bd1e995);
        private bool _disposed, _udpHooked, _stopped;

        private int _round;
        private bool _roundActive;
        private int _curTier, _curIndex;
        private BtAnnounceEvent _roundEvent, _pendingEvent;
        private bool _wantRound;
        private DateTime _nextRoundUtc = DateTime.MinValue, _lastRoundUtc = DateTime.MinValue;
        private int _roundFailures;
        private int _lastMinInterval;

        public BtTrackers(IBtSwarm swarm, BtContext ctx, IList<List<string>> tiers)
        {
            _swarm = swarm;
            _ctx = ctx;
            DlHttp.Init();   // TLS 1.2/1.3 для https-трекеров: без неё .NET 4 без TargetFramework предлагает только TLS 1.0
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            if (tiers != null)
                foreach (List<string> tier in tiers)
                {
                    if (tier == null) continue;
                    List<Entry> list = new List<Entry>();
                    foreach (string url in tier)
                    {
                        Entry e = NewEntry(url, seen);
                        if (e != null) list.Add(e);
                    }
                    if (list.Count == 0) continue;
                    if (ShuffleTiers) Shuffle(list);
                    _tiers.Add(list);
                }
            lock (_gate) HookUdpLocked();
        }

        private static Entry NewEntry(string url, HashSet<string> seen)
        {
            if (url == null) return null;
            url = url.Trim();
            Uri u;
            if (!BtMeta.IsTrackerUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out u) || !seen.Add(url)) return null;
            Entry e = new Entry();
            e.Url = url;
            e.Uri = u;
            e.Udp = u.Scheme == "udp";
            e.Shown = BtRedact.Url(url);
            return e;
        }

        private void Shuffle(List<Entry> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = _shuffle.Next(i + 1);
                Entry t = list[i];
                list[i] = list[j];
                list[j] = t;
            }
        }

        private void HookUdpLocked()
        {
            if (_udpHooked || _disposed || _ctx.Udp == null) return;
            foreach (List<Entry> tier in _tiers)
                foreach (Entry e in tier)
                    if (e.Udp)
                    {
                        _ctx.Udp.AddHandler(this);
                        _udpHooked = true;
                        return;
                    }
        }

        // ------------------------------------------------------------------ //
        //  Интерфейс
        // ------------------------------------------------------------------ //
        public void Announce(BtAnnounceEvent e)
        {
            Stats st = Snap();
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (_disposed) return;
                if (e == BtAnnounceEvent.Stopped) StopLocked(st, after);
                else
                {
                    _stopped = false;
                    if (e != BtAnnounceEvent.None) _pendingEvent = e;
                    _wantRound = true;
                    if (!_roundActive) StartRoundLocked(DateTime.UtcNow, st, after);
                }
            }
            Run(after);
        }

        public void Reannounce()
        {
            Stats st = Snap();
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (_disposed || _stopped || _roundActive) return;
                DateTime now = DateTime.UtcNow;
                int minIv = _lastMinInterval > 0 ? _lastMinInterval : DefaultMinIntervalSeconds;
                if (_lastRoundUtc == DateTime.MinValue || (now - _lastRoundUtc).TotalSeconds >= minIv) StartRoundLocked(now, st, after);
                else
                {
                    DateTime allowed = _lastRoundUtc.AddSeconds(minIv);
                    if (_nextRoundUtc == DateTime.MinValue || _nextRoundUtc > allowed) _nextRoundUtc = allowed;
                }
            }
            Run(after);
        }

        public void AddTracker(string url, int tier)
        {
            lock (_gate)
            {
                if (_disposed) return;
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (List<Entry> t in _tiers) foreach (Entry x in t) seen.Add(x.Url);
                Entry e = NewEntry(url, seen);
                if (e == null) return;
                if (tier < 0) tier = 0;
                if (tier < _tiers.Count) _tiers[tier].Add(e);
                else _tiers.Add(new List<Entry> { e });
                HookUdpLocked();
                // Трекер пришёл после первого объявления (например, из метаданных magnet) — спросить и его.
                if (_round > 0 && !_stopped && !_roundActive && !AnyWorkingLocked()) _wantRound = true;
            }
        }

        public List<BtTrackerInfo> Snapshot()
        {
            List<BtTrackerInfo> list = new List<BtTrackerInfo>();
            lock (_gate)
            {
                for (int t = 0; t < _tiers.Count; t++)
                    foreach (Entry e in _tiers[t])
                    {
                        BtTrackerInfo i = new BtTrackerInfo();
                        i.Url = e.Shown;
                        i.Tier = t;
                        i.Status = StatusText(e);
                        i.Seeders = e.Seeders;
                        i.Leechers = e.Leechers;
                        i.Downloaded = e.Downloaded;
                        i.PeersReceived = e.PeersReceived;
                        i.NextAnnounceUtc = e.State == TrState.Working ? _nextRoundUtc : DateTime.MinValue;
                        i.Message = e.Message;
                        list.Add(i);
                    }
            }
            return list;
        }

        private static string StatusText(Entry e)
        {
            switch (e.State)
            {
                case TrState.Updating: return Tr.S("обновляется", "updating");
                case TrState.Working: return Tr.S("работает", "working");
                case TrState.Error: return Tr.S("ошибка: ", "error: ") + e.ErrorText;
                default: return Tr.S("не связывался", "not contacted");
            }
        }

        public void Tick(DateTime utcNow)
        {
            Stats st = Snap();
            bool active = _swarm.Active;
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (_disposed) return;
                foreach (List<Entry> tier in _tiers)
                    foreach (Entry e in tier.ToArray())
                        if (e.Udp && e.InFlight && e.Phase != 0) UdpTimerLocked(e, utcNow, st, after);

                if (_roundActive && _curTier < _tiers.Count)
                {
                    Entry cur = _tiers[_curTier][_curIndex];
                    if (cur.InFlight && cur.Round == _round && (utcNow - cur.StartedUtc).TotalSeconds >= FallbackAfterSeconds)
                        AdvanceLocked(utcNow, st, after);
                }
                if (!_roundActive && !_stopped && active
                    && (_wantRound || (_nextRoundUtc != DateTime.MinValue && utcNow >= _nextRoundUtc)))
                    StartRoundLocked(utcNow, st, after);
            }
            Run(after);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _byTid.Clear();
            }
            if (_udpHooked && _ctx.Udp != null) _ctx.Udp.RemoveHandler(this);
            HttpWebRequest[] live;
            lock (_live)
            {
                live = _live.ToArray();
                _live.Clear();
            }
            foreach (HttpWebRequest r in live) { try { r.Abort(); } catch { } }
        }

        // ------------------------------------------------------------------ //
        //  Круг объявления
        // ------------------------------------------------------------------ //
        private void StartRoundLocked(DateTime now, Stats st, List<Action> after)
        {
            _wantRound = false;
            if (_tiers.Count == 0) return;
            _round++;
            _roundActive = true;
            _roundEvent = _pendingEvent;
            _lastRoundUtc = now;
            _curTier = 0;
            _curIndex = 0;
            AttemptLocked(_tiers[0][0], now, st, after);
        }

        private void AttemptLocked(Entry e, DateTime now, Stats st, List<Action> after)
        {
            if (e.InFlight) CancelLocked(e);
            e.Attempt++;
            e.InFlight = true;
            e.OneShot = false;
            e.Round = _round;
            e.StartedUtc = now;
            e.Event = _roundEvent;
            e.StateBefore = e.State;
            e.State = TrState.Updating;
            if (e.Udp) UdpBeginLocked(e, now, st, after);
            else HttpBeginLocked(e, st, e.Event, false, after);
        }

        private void CancelLocked(Entry e)
        {
            e.InFlight = false;
            e.Attempt++;
            DropTidLocked(e);
            e.Phase = 0;
            if (e.State == TrState.Updating) e.State = e.StateBefore;
        }

        private bool IsCursorLocked(Entry e)
        {
            return _curTier < _tiers.Count && _curIndex < _tiers[_curTier].Count && _tiers[_curTier][_curIndex] == e;
        }

        private void AdvanceLocked(DateTime now, Stats st, List<Action> after)
        {
            while (true)
            {
                _curIndex++;
                if (_curIndex >= _tiers[_curTier].Count)
                {
                    _curTier++;
                    _curIndex = 0;
                }
                if (_curTier >= _tiers.Count)
                {
                    CheckRoundEndLocked(now);
                    return;
                }
                Entry next = _tiers[_curTier][_curIndex];
                if (next.InFlight && next.Round == _round && !next.OneShot) continue;
                AttemptLocked(next, now, st, after);
                return;
            }
        }

        // Весь список пройден и никто уже не отвечает — круг провален, повтор с растущей паузой.
        private void CheckRoundEndLocked(DateTime now)
        {
            if (!_roundActive || _curTier < _tiers.Count) return;
            foreach (List<Entry> tier in _tiers)
                foreach (Entry e in tier)
                    if (e.InFlight && e.Round == _round && !e.OneShot) return;
            _roundActive = false;
            _roundFailures++;
            _wantRound = false;
            int backoff = Math.Min(1800, 30 << Math.Min(_roundFailures - 1, 6));
            _nextRoundUtc = now.AddSeconds(backoff);
        }

        private void FinishRoundLocked(Entry winner, DateTime now, Stats st, List<Action> after)
        {
            _roundActive = false;
            _roundFailures = 0;
            if (_pendingEvent == _roundEvent) _pendingEvent = BtAnnounceEvent.None;
            foreach (List<Entry> tier in _tiers)
            {
                int i = tier.IndexOf(winner);
                if (i > 0)
                {
                    tier.RemoveAt(i);
                    tier.Insert(0, winner);
                }
                foreach (Entry e in tier)
                    if (e != winner && e.InFlight && e.Round == _round && !e.OneShot) CancelLocked(e);
            }
            _lastMinInterval = winner.MinInterval;
            _nextRoundUtc = now.AddSeconds(winner.Interval > 0 ? winner.Interval : DefaultInterval);
            if (_wantRound && !_stopped) StartRoundLocked(now, st, after);
        }

        private bool AnyWorkingLocked()
        {
            foreach (List<Entry> tier in _tiers)
                foreach (Entry e in tier)
                    if (e.State == TrState.Working) return true;
            return false;
        }

        private void StopLocked(Stats st, List<Action> after)
        {
            _stopped = true;
            _wantRound = false;
            _pendingEvent = BtAnnounceEvent.None;
            _nextRoundUtc = DateTime.MinValue;
            DateTime now = DateTime.UtcNow;
            foreach (List<Entry> tier in _tiers)
                foreach (Entry e in tier)
                {
                    if (e.InFlight) CancelLocked(e);
                    if (!e.EverWorked) continue;   // трекер нас не знает — прощаться незачем
                    if (e.Udp)
                    {
                        if (e.UdpEp == null || _ctx.Udp == null) continue;
                        e.Attempt++;
                        e.InFlight = true;
                        e.OneShot = true;
                        e.Event = BtAnnounceEvent.Stopped;
                        UdpFirstPacketLocked(e, now, st, after);
                    }
                    else HttpBeginLocked(e, st, BtAnnounceEvent.Stopped, true, after);
                }
            _roundActive = false;
        }

        // Итог попытки. Обращения к рою и журналу — в after, после снятия блокировки.
        private void CompleteLocked(Entry e, Reply r, DateTime now, Stats st, List<Action> after)
        {
            e.InFlight = false;
            e.Phase = 0;
            DropTidLocked(e);
            if (e.OneShot) return;
            string shown = e.Shown;
            if (r.Ok)
            {
                bool wasWorking = e.StateBefore == TrState.Working;
                string previousMessage = e.Message;
                e.State = TrState.Working;
                e.EverWorked = true;
                e.ErrorText = "";
                e.Message = r.Warning ?? "";
                if (r.Interval > 0) e.Interval = r.Interval;
                if (r.MinInterval >= 0) e.MinInterval = r.MinInterval;
                if (r.Seeders >= 0) e.Seeders = r.Seeders;
                if (r.Leechers >= 0) e.Leechers = r.Leechers;
                if (r.Downloaded >= 0) e.Downloaded = r.Downloaded;
                if (!string.IsNullOrEmpty(r.TrackerId)) e.TrackerId = r.TrackerId;
                e.PeersReceived += r.Peers.Count;
                List<BtEndpoint> peers = r.Peers;
                IPAddress external = r.External;
                int count = peers.Count;
                if (count > 0) after.Add(delegate { _swarm.AddPeers(peers, BtPeerOrigin.Tracker); });
                if (external != null) after.Add(delegate { _ctx.ExternalAddress = external; });
                string warning = e.Message == previousMessage ? "" : e.Message;   // одно и то же предупреждение раз в интервал — в журнал один раз
                after.Add(delegate
                {
                    _ctx.Log("bt tracker " + shown + ": ok, peers " + count.ToString(CultureInfo.InvariantCulture));
                    if (!wasWorking) _swarm.Journal(Tr.S("Трекер ", "Tracker ") + shown + Tr.S(": работает", ": working"));
                    if (warning.Length > 0) _swarm.Journal(Tr.S("Трекер ", "Tracker ") + shown + Tr.S(" предупреждает: ", " warns: ") + warning);
                });
                if (e.Round == _round && _roundActive) FinishRoundLocked(e, now, st, after);
            }
            else
            {
                bool repeated = e.StateBefore == TrState.Error && e.ErrorText == r.Error;
                e.State = TrState.Error;
                e.ErrorText = r.Error;
                e.Message = r.Failure ?? "";
                string text = r.Error;
                after.Add(delegate
                {
                    _ctx.Log("bt tracker " + shown + ": " + text);
                    if (!repeated) _swarm.Journal(Tr.S("Трекер ", "Tracker ") + shown + Tr.S(": ошибка: ", ": error: ") + text);
                });
                if (e.Round == _round && _roundActive)
                {
                    if (IsCursorLocked(e)) AdvanceLocked(now, st, after);
                    else CheckRoundEndLocked(now);
                }
            }
        }

        private void Complete(Entry e, int attempt, Reply r)
        {
            Stats st = Snap();
            List<Action> after = new List<Action>();
            lock (_gate)
            {
                if (_disposed || e.Attempt != attempt || !e.InFlight) return;
                CompleteLocked(e, r, DateTime.UtcNow, st, after);
            }
            Run(after);
        }

        private Stats Snap()
        {
            Stats s = new Stats();
            s.InfoHash = _swarm.InfoHash;
            s.Up = Math.Max(0, _swarm.Uploaded);
            s.Down = Math.Max(0, _swarm.Downloaded);
            // Magnet без метаданных: left=0 трекер счёл бы сидом и не дал бы сидов; как libtorrent — один блок.
            s.Left = _swarm.Meta == null && !_swarm.IsSeed ? BtMeta.BlockSize : Math.Max(0, _swarm.Left);
            s.NumWant = Math.Max(0, _swarm.NumWant);
            return s;
        }

        private static void Run(List<Action> after)
        {
            foreach (Action a in after)
            {
                try { a(); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private static Reply Fail(string text)
        {
            Reply r = new Reply();
            r.Error = text;
            return r;
        }

        // Текст от трекера: без управляющих символов и не длиннее 300.
        private static string CleanText(string s)
        {
            if (s == null) return null;
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= 300) break;
                sb.Append(char.IsControl(c) ? ' ' : c);
            }
            return sb.ToString().Trim();
        }

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
