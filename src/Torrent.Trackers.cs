// SysDeck — «Загрузки», торренты: трекеры HTTP(S) и UDP (BEP 3/12/15/23/24/41).
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

namespace SysDeck.Downloads
{
    internal sealed partial class BtTrackers : IBtTrackers, IBtUdpHandler
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
    }
}
