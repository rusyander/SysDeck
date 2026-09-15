// SysDeck — «Загрузки», торренты: обмен адресами пиров ut_pex (BEP 11).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Каждому пиру, объявившему ut_pex, не чаще раза в минуту уходит разница с прошлым сообщением: added/added.f/dropped и
// те же для v6, не больше 50 адресов в каждом списке. Время — только из Tick(utcNow) реактора: и отправка, и отсев
// слишком частых входящих считаются по одним часам. Частному торренту (BEP 27) PEX не положен: фабрика Create возвращает
// null, а экземпляр, созданный в обход неё, всё равно молчит и игнорирует входящие.
using System;
using System.Collections.Generic;

namespace SysDeck.Downloads
{
    internal sealed class BtPexExt : IBtExtension
    {
        public const int MaxPerMessage = 50;
        public const int IntervalSeconds = 60;
        // Входящие чаще этого не принимаются: честный клиент шлёт раз в минуту, запас — на дрожание тактов.
        public const int MinReceiveGapSeconds = 45;
        private const int MaxPayload = 64 * 1024;

        // 0x04 (uTP) не ставится: клиент ходит только по TCP.
        public const byte FlagEncryption = 0x01, FlagSeed = 0x02, FlagReachable = 0x10;

        private sealed class PeerState
        {
            public DateTime LastSent = DateTime.MinValue;
            public DateTime LastReceived = DateTime.MinValue;
            public readonly HashSet<BtEndpoint> Advertised = new HashSet<BtEndpoint>();
        }

        private readonly IBtSwarm _swarm;
        private readonly object _gate = new object();
        private readonly Dictionary<IBtPeerLink, PeerState> _peers = new Dictionary<IBtPeerLink, PeerState>();
        private DateTime _clock = DateTime.MinValue;

        // Фабрика для BtFactories.Extensions: частному торренту расширение не создаётся вовсе (BEP 27).
        public static IBtExtension Create(IBtSwarm swarm, BtContext ctx)
        {
            return swarm == null || swarm.IsPrivate ? null : new BtPexExt(swarm, ctx);
        }

        // ctx не нужен: адреса и флаги берутся у роя; параметр — ради общей формы фабрик расширений.
        public BtPexExt(IBtSwarm swarm, BtContext ctx)
        {
            if (swarm == null) throw new ArgumentNullException("swarm");
            _swarm = swarm;
        }

        public string Name { get { return "ut_pex"; } }

        public void FillHandshake(BVal handshake) { }

        public void OnHandshake(IBtPeerLink peer, BVal handshake)
        {
            if (peer == null || _swarm.IsPrivate || !peer.Supports(Name)) return;
            lock (_gate)
                if (!_peers.ContainsKey(peer)) _peers[peer] = new PeerState();
        }

        public void OnClosed(IBtPeerLink peer)
        {
            if (peer == null) return;
            lock (_gate) _peers.Remove(peer);
        }

        public void Tick(DateTime utcNow)
        {
            if (_swarm.IsPrivate) return;
            List<IBtPeerLink> due = new List<IBtPeerLink>();
            lock (_gate)
            {
                _clock = utcNow;
                foreach (KeyValuePair<IBtPeerLink, PeerState> kv in _peers)
                    if (kv.Value.LastSent == DateTime.MinValue || (utcNow - kv.Value.LastSent).TotalSeconds >= IntervalSeconds) due.Add(kv.Key);
            }
            if (due.Count == 0) return;
            // Рой — чужой объект: снимок берётся вне своей блокировки.
            List<BtPeerInfo> connected = _swarm.ConnectedPeers() ?? new List<BtPeerInfo>();
            List<KeyValuePair<IBtPeerLink, byte[]>> send = new List<KeyValuePair<IBtPeerLink, byte[]>>();
            lock (_gate)
            {
                foreach (IBtPeerLink peer in due)
                {
                    PeerState st;
                    if (!_peers.TryGetValue(peer, out st)) continue;
                    byte[] payload = Build(peer, st, connected);
                    if (payload == null) continue;
                    st.LastSent = utcNow;
                    send.Add(new KeyValuePair<IBtPeerLink, byte[]>(peer, payload));
                }
            }
            foreach (KeyValuePair<IBtPeerLink, byte[]> s in send)
            {
                try { s.Key.SendExtended(Name, s.Value); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        // null — сообщать нечего.
        private static byte[] Build(IBtPeerLink peer, PeerState st, List<BtPeerInfo> connected)
        {
            Dictionary<BtEndpoint, byte> current = new Dictionary<BtEndpoint, byte>();
            BtEndpoint self = peer.Endpoint;
            BtEndpoint selfListen = self != null && peer.ListenPort > 0 ? new BtEndpoint(self.Address, peer.ListenPort) : null;
            foreach (BtPeerInfo p in connected)
            {
                if (p == null || p.Endpoint == null || !p.Endpoint.IsUsable) continue;
                if (p.Endpoint.Equals(self) || p.Endpoint.Equals(selfListen)) continue;
                byte f = 0;
                if (p.Encrypted) f |= FlagEncryption;
                if (p.Seed) f |= FlagSeed;
                if (p.Outgoing) f |= FlagReachable;
                current[p.Endpoint] = f;
            }
            List<BtEndpoint> added = new List<BtEndpoint>(), added6 = new List<BtEndpoint>();
            List<BtEndpoint> dropped = new List<BtEndpoint>(), dropped6 = new List<BtEndpoint>();
            foreach (BtEndpoint ep in current.Keys)
            {
                if (st.Advertised.Contains(ep)) continue;
                List<BtEndpoint> list = ep.IsV6 ? added6 : added;
                if (list.Count < MaxPerMessage) list.Add(ep);
            }
            foreach (BtEndpoint ep in st.Advertised)
            {
                if (current.ContainsKey(ep)) continue;
                List<BtEndpoint> list = ep.IsV6 ? dropped6 : dropped;
                if (list.Count < MaxPerMessage) list.Add(ep);
            }
            if (added.Count + added6.Count + dropped.Count + dropped6.Count == 0) return null;
            BVal msg = BVal.NewDict();
            msg.Set("added", BVal.Bytes(Compact(added)));
            msg.Set("added.f", BVal.Bytes(Flags(added, current)));
            msg.Set("dropped", BVal.Bytes(Compact(dropped)));
            msg.Set("added6", BVal.Bytes(Compact(added6)));
            msg.Set("added6.f", BVal.Bytes(Flags(added6, current)));
            msg.Set("dropped6", BVal.Bytes(Compact(dropped6)));
            foreach (BtEndpoint ep in added) st.Advertised.Add(ep);
            foreach (BtEndpoint ep in added6) st.Advertised.Add(ep);
            foreach (BtEndpoint ep in dropped) st.Advertised.Remove(ep);
            foreach (BtEndpoint ep in dropped6) st.Advertised.Remove(ep);
            return Bencode.Encode(msg);
        }

        public void OnMessage(IBtPeerLink peer, byte[] payload, int offset, int count)
        {
            if (peer == null || _swarm.IsPrivate || payload == null || count <= 0 || count > MaxPayload) return;
            if (offset < 0 || offset + count > payload.Length) return;
            lock (_gate)
            {
                PeerState st;
                if (!_peers.TryGetValue(peer, out st))
                {
                    st = new PeerState();
                    _peers[peer] = st;
                }
                DateTime now = _clock == DateTime.MinValue ? DateTime.UtcNow : _clock;
                if (st.LastReceived != DateTime.MinValue && (now - st.LastReceived).TotalSeconds < MinReceiveGapSeconds) return;
                st.LastReceived = now;
            }
            int consumed;
            string err;
            BVal msg = Bencode.DecodePrefix(payload, offset, count, out consumed, out err);
            if (msg == null || consumed != count || msg.Kind != BKind.Dict) return;
            bool skipSeeds = _swarm.IsSeed;
            List<BtEndpoint> peers = new List<BtEndpoint>();
            Collect(peers, msg.GetBytes("added"), msg.GetBytes("added.f"), false, skipSeeds);
            Collect(peers, msg.GetBytes("added6"), msg.GetBytes("added6.f"), true, skipSeeds);
            if (peers.Count > 0) _swarm.AddPeers(peers, BtPeerOrigin.Pex);
        }

        // Не больше MaxPerMessage из каждого семейства; раздающему сиды из PEX ни к чему.
        private static void Collect(List<BtEndpoint> into, byte[] compact, byte[] flags, bool v6, bool skipSeeds)
        {
            if (compact == null) return;
            int size = v6 ? 18 : 6;
            int taken = 0;
            for (int i = 0; i + size <= compact.Length && taken < MaxPerMessage; i += size)
            {
                int k = i / size;
                if (skipSeeds && flags != null && k < flags.Length && (flags[k] & FlagSeed) != 0) continue;
                byte[] one = new byte[size];
                Buffer.BlockCopy(compact, i, one, 0, size);
                List<BtEndpoint> ep = BtEndpoint.ParseCompact(one, v6);
                if (ep.Count == 1 && !into.Contains(ep[0]))
                {
                    into.Add(ep[0]);
                    taken++;
                }
            }
        }

        private static byte[] Compact(List<BtEndpoint> list)
        {
            List<byte> b = new List<byte>();
            foreach (BtEndpoint ep in list) b.AddRange(ep.ToCompact());
            return b.ToArray();
        }

        private static byte[] Flags(List<BtEndpoint> list, Dictionary<BtEndpoint, byte> flags)
        {
            byte[] f = new byte[list.Count];
            for (int i = 0; i < list.Count; i++) f[i] = flags[list[i]];
            return f;
        }
    }
}
