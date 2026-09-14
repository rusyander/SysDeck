﻿// Windows Process Cleaner — «Загрузки», торренты: DHT (BEP 5) — KRPC поверх общего UDP-сокета сессии.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Один узел на сессию. Таблица маршрутов — корзины по длине общего с собственным id префикса: делится только последняя
// корзина (та, в чьём диапазоне лежит свой id), поэтому далёкие узлы занимают по 8 мест на уровень, а близкие — подробно.
// В таблицу узел попадает только ответив на наш запрос: запрос незнакомца лишь вызывает ping, иначе подделанный адрес
// отправителя занимал бы место. Токен announce_peer = SHA1(секрет + IP), секрет меняется раз в 5 минут, прежний тоже
// принимается. Поток приёма UDP только разбирает и отвечает; таймауты, обслуживание таблицы и dht.json — таймер пула,
// DNS начальных узлов — рабочий поток пула, никогда не поток приёма.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class BtDht : IBtDht
    {
        public const int K = 8;
        private const int Alpha = 3;
        private const int MaxLookupQueries = 120;
        private const int MaxCandidates = 64;
        private const int MaxPeersPerHash = 100;
        private const int MaxHashes = 2000;
        private const int MaxValuesInReply = 50;
        private const int MaxPending = 512;
        private const int MaxDatagram = 65507;
        private static readonly byte[] Version = { (byte)'W', (byte)'P', 1, 0 };

        // Начальные узлы продукта. Тесты подменяют список до создания узлов (и восстанавливают в finally).
        internal static string[] DefaultBootstrap = { "router.bittorrent.com:6881", "dht.transmissionbt.com:6881", "router.utorrent.com:6881" };

        // Времена и пределы — поля экземпляра: тесты сокращают их до Start.
        internal int QueryTimeoutMs = 5000;
        internal int RateLimitPerSecond = 20;          // запросов с одного IP в секунду; сверх — молча отбрасываются
        internal int RebootstrapSeconds = 60;
        internal readonly List<string> Bootstrap;

        private sealed class Node
        {
            public byte[] Id;
            public BtEndpoint Ep;
            public DateTime LastResponse, LastPing;
            public int Fails;
        }

        private sealed class Bucket
        {
            public readonly List<Node> Nodes = new List<Node>();
            public readonly List<Node> Spare = new List<Node>();
        }

        private sealed class Candidate
        {
            public byte[] Id;                 // null — начальный узел, id ещё неизвестен
            public BtEndpoint Ep;
            public int State;                 // 0 новый, 1 запрос ушёл, 2 ответил, 3 не ответил
            public byte[] Token;
        }

        private sealed class Lookup
        {
            public byte[] Target;
            public IBtSwarm Swarm;            // null — find_node (самопоиск)
            public bool Announce;
            public readonly List<Candidate> Cands = new List<Candidate>();
            public readonly HashSet<BtEndpoint> Peers = new HashSet<BtEndpoint>();
            public int InFlight, Queries;
            public DateTime Started;
            public bool Waiting;
        }

        private sealed class Pending
        {
            public BtEndpoint Ep;
            public string Method;
            public Lookup Lookup;
            public Candidate Cand;
            public DateTime Sent;
        }

        private struct Out
        {
            public BtEndpoint To;
            public byte[] Data;
        }

        private readonly BtContext _ctx;
        private readonly object _gate = new object();
        private byte[] _id;
        private readonly List<Bucket> _buckets = new List<Bucket>();
        private readonly Dictionary<int, Pending> _pending = new Dictionary<int, Pending>();
        private readonly List<Lookup> _lookups = new List<Lookup>();
        private readonly Dictionary<string, Dictionary<BtEndpoint, DateTime>> _store = new Dictionary<string, Dictionary<BtEndpoint, DateTime>>();
        private readonly Dictionary<IPAddress, int> _rate = new Dictionary<IPAddress, int>();
        private readonly List<BtEndpoint> _bootstrapEps = new List<BtEndpoint>();
        private readonly List<BtEndpoint> _earlyNodes = new List<BtEndpoint>();
        private readonly List<KeyValuePair<IBtSwarm, string>> _notes = new List<KeyValuePair<IBtSwarm, string>>();   // журнал роя — вне блокировки
        private readonly List<Lookup> _announces = new List<Lookup>();                                                // announce_peer — после проверки роя
        private DateTime _rateWindow, _secretAt, _lastBootstrap, _lastSave, _lastRefresh, _lastExpire;
        private byte[] _secret, _prevSecret;
        private int _nextTid;
        private Timer _timer;
        private int _tickBusy;
        private bool _started, _ready, _disposed, _bootstrapping;

        public BtDht(BtContext ctx)
        {
            if (ctx == null) throw new ArgumentNullException("ctx");
            _ctx = ctx;
            _id = Random(20);
            _secret = Random(20);
            _secretAt = DateTime.UtcNow;
            _nextTid = BitConverter.ToUInt16(Random(2), 0);
            _buckets.Add(new Bucket());
            Bootstrap = new List<string>(DefaultBootstrap ?? new string[0]);
        }

        private string StateFile { get { return string.IsNullOrEmpty(_ctx.TorrentsDir) ? null : Path.Combine(_ctx.TorrentsDir, "dht.json"); } }

        // ------------------------------------------------------------------ //
        //  IBtDht
        // ------------------------------------------------------------------ //
        public void Start()
        {
            lock (_gate)
            {
                if (_started || _disposed) return;
                _started = true;
            }
            if (_ctx.Udp != null) _ctx.Udp.AddHandler(this);
            _timer = new Timer(OnTimer, null, 250, 250);
            ThreadPool.QueueUserWorkItem(delegate { LoadAndBootstrap(); });
        }

        public void AddNode(BtEndpoint node)
        {
            if (node == null || node.IsV6 || !node.IsUsable) return;
            List<Out> send = new List<Out>();
            lock (_gate)
            {
                if (_disposed) return;
                if (!_ready)
                {
                    if (_earlyNodes.Count < 64 && !_earlyNodes.Contains(node)) _earlyNodes.Add(node);
                    return;
                }
                Ping(node, send);
            }
            SendAll(send);
        }

        public void GetPeers(IBtSwarm swarm, bool announce)
        {
            // BEP 27: частный торрент не ищется и не объявляется в DHT.
            if (swarm == null || swarm.IsPrivate) return;
            byte[] hash = swarm.InfoHash;
            if (hash == null || hash.Length != 20) return;
            List<Out> send = new List<Out>();
            lock (_gate)
            {
                if (_disposed) return;
                foreach (Lookup l in _lookups)
                    if (l.Swarm != null && Bencode.SameBytes(l.Target, hash))
                    {
                        if (announce) l.Announce = true;
                        return;
                    }
                Lookup nl = new Lookup();
                nl.Target = (byte[])hash.Clone();
                nl.Swarm = swarm;
                nl.Announce = announce;
                StartLookup(nl, send);
            }
            SendAll(send);
        }

        public int NodeCount
        {
            get
            {
                lock (_gate)
                {
                    int n = 0;
                    foreach (Bucket b in _buckets)
                        foreach (Node x in b.Nodes) if (x.Fails < 2) n++;
                    return n;
                }
            }
        }

        public string Status
        {
            get
            {
                bool ready;
                lock (_gate) ready = _ready;
                if (!ready) return Tr.S("DHT: запуск", "DHT: starting");
                int n = NodeCount;
                if (n == 0) return Tr.S("DHT: поиск узлов", "DHT: looking for nodes");
                return Tr.S("DHT: узлов в таблице — ", "DHT: nodes in the table — ") + n.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Только KRPC: словарь bencode с ключом «y». Ответы UDP-трекеров (BEP 15) начинаются с action = 0..3 — не наши.
        public bool HandleDatagram(BtEndpoint from, byte[] data, int count)
        {
            if (data == null || count < 8 || count > MaxDatagram || count > data.Length || data[0] != (byte)'d') return false;
            int consumed;
            string err;
            BVal msg = Bencode.DecodePrefix(data, 0, count, out consumed, out err);
            if (msg == null || consumed != count || msg.Kind != BKind.Dict) return false;
            byte[] y = msg.GetBytes("y");
            if (y == null || y.Length != 1) return false;
            if (from == null || from.IsV6) return true;
            List<Out> send = new List<Out>();
            List<BtEndpoint> found = null;
            IBtSwarm swarm = null;
            lock (_gate)
            {
                if (_disposed || !_ready) return true;
                if (y[0] == (byte)'q') HandleQuery(from, msg, send);
                else if (y[0] == (byte)'r' || y[0] == (byte)'e') HandleReply(from, msg, y[0] == (byte)'r', send, out found, out swarm);
            }
            SendAll(send);
            if (found != null && found.Count > 0 && swarm != null) swarm.AddPeers(found, BtPeerOrigin.Dht);
            return true;
        }

        public void Dispose()
        {
            Timer t;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                t = _timer;
                _timer = null;
            }
            if (t != null) t.Dispose();
            if (_ctx.Udp != null) _ctx.Udp.RemoveHandler(this);
            Save();
        }

        // ------------------------------------------------------------------ //
        //  Запросы к нам
        // ------------------------------------------------------------------ //
        private void HandleQuery(BtEndpoint from, BVal msg, List<Out> send)
        {
            byte[] t = msg.GetBytes("t");
            if (t == null || t.Length == 0 || t.Length > 32) return;
            if (!Allow(from.Address)) return;
            string q = msg.GetStr("q");
            BVal a = msg.Get("a", BKind.Dict);
            byte[] sender = a == null ? null : a.GetBytes("id");
            if (q == null || sender == null || sender.Length != 20)
            {
                send.Add(Error(from, t, 203, "Protocol Error"));
                return;
            }
            NoteQuery(sender, from, send);
            BVal r = BVal.NewDict().Set("id", BVal.Bytes(_id));
            switch (q)
            {
                case "ping":
                    break;
                case "find_node":
                    {
                        byte[] target = a.GetBytes("target");
                        if (target == null || target.Length != 20) { send.Add(Error(from, t, 203, "Protocol Error")); return; }
                        r.Set("nodes", BVal.Bytes(CompactNodes(Closest(target, K))));
                        break;
                    }
                case "get_peers":
                    {
                        byte[] hash = a.GetBytes("info_hash");
                        if (hash == null || hash.Length != 20) { send.Add(Error(from, t, 203, "Protocol Error")); return; }
                        r.Set("token", BVal.Bytes(MakeToken(from.Address, _secret)));
                        Dictionary<BtEndpoint, DateTime> peers;
                        if (_store.TryGetValue(Bencode.Hex(hash), out peers) && peers.Count > 0)
                        {
                            BVal values = BVal.NewList();
                            foreach (BtEndpoint p in peers.Keys)
                            {
                                values.Add(BVal.Bytes(p.ToCompact()));
                                if (values.L.Count >= MaxValuesInReply) break;
                            }
                            r.Set("values", values);
                        }
                        r.Set("nodes", BVal.Bytes(CompactNodes(Closest(hash, K))));
                        break;
                    }
                case "announce_peer":
                    {
                        byte[] hash = a.GetBytes("info_hash");
                        byte[] token = a.GetBytes("token");
                        int port = a.GetInt("implied_port", 0) != 0 ? from.Port : (int)Math.Max(0, Math.Min(int.MaxValue, a.GetInt("port", 0)));
                        if (hash == null || hash.Length != 20 || token == null || port <= 0 || port > 65535)
                        {
                            send.Add(Error(from, t, 203, "Protocol Error"));
                            return;
                        }
                        if (!TokenValid(from.Address, token))
                        {
                            send.Add(Error(from, t, 203, "Bad token"));
                            return;
                        }
                        StorePeer(hash, new BtEndpoint(from.Address, port));
                        break;
                    }
                default:
                    send.Add(Error(from, t, 204, "Method Unknown"));
                    return;
            }
            BVal reply = BVal.NewDict().Set("t", BVal.Bytes(t)).Set("y", BVal.Str("r")).Set("r", r).Set("v", BVal.Bytes(Version));
            send.Add(new Out { To = from, Data = Bencode.Encode(reply) });
        }

        private bool Allow(IPAddress ip)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _rateWindow).TotalMilliseconds >= 1000 || now < _rateWindow)
            {
                _rate.Clear();
                _rateWindow = now;
            }
            int n;
            _rate.TryGetValue(ip, out n);
            if (n == 0 && _rate.Count >= 4096) return false;
            if (n >= RateLimitPerSecond) return false;
            _rate[ip] = n + 1;
            return true;
        }

        private byte[] MakeToken(IPAddress ip, byte[] secret)
        {
            return BtMse.Sha1(secret, ip.GetAddressBytes());
        }

        private bool TokenValid(IPAddress ip, byte[] token)
        {
            if (Bencode.SameBytes(token, MakeToken(ip, _secret))) return true;
            return _prevSecret != null && Bencode.SameBytes(token, MakeToken(ip, _prevSecret));
        }

        // Секрет токенов: новый каждые 5 минут, токен по прежнему ещё принимается (BEP 5 — до 10 минут жизни).
        internal void RotateSecret()
        {
            lock (_gate)
            {
                _prevSecret = _secret;
                _secret = Random(20);
                _secretAt = DateTime.UtcNow;
            }
        }

        private void StorePeer(byte[] hash, BtEndpoint peer)
        {
            if (!peer.IsUsable) return;
            string key = Bencode.Hex(hash);
            Dictionary<BtEndpoint, DateTime> peers;
            if (!_store.TryGetValue(key, out peers))
            {
                if (_store.Count >= MaxHashes) return;
                peers = new Dictionary<BtEndpoint, DateTime>();
                _store[key] = peers;
            }
            if (!peers.ContainsKey(peer) && peers.Count >= MaxPeersPerHash)
            {
                BtEndpoint oldest = null;
                DateTime at = DateTime.MaxValue;
                foreach (KeyValuePair<BtEndpoint, DateTime> kv in peers)
                    if (kv.Value < at) { at = kv.Value; oldest = kv.Key; }
                if (oldest != null) peers.Remove(oldest);
            }
            peers[peer] = DateTime.UtcNow;
        }

        // Незнакомый узел прислал запрос: в таблицу — только если ответит на наш ping и для него есть место.
        private void NoteQuery(byte[] id, BtEndpoint from, List<Out> send)
        {
            if (Bencode.SameBytes(id, _id)) return;
            int bi = BucketIndex(id);
            Bucket b = _buckets[bi];
            foreach (Node n in b.Nodes) if (Bencode.SameBytes(n.Id, id)) return;
            bool room = b.Nodes.Count < K || bi == _buckets.Count - 1;
            if (!room) return;
            foreach (Pending p in _pending.Values) if (p.Ep.Equals(from)) return;
            Ping(from, send);
        }

        // ------------------------------------------------------------------ //
        //  Ответы на наши запросы
        // ------------------------------------------------------------------ //
        private void HandleReply(BtEndpoint from, BVal msg, bool ok, List<Out> send, out List<BtEndpoint> found, out IBtSwarm swarm)
        {
            found = null;
            swarm = null;
            byte[] t = msg.GetBytes("t");
            if (t == null || t.Length != 2) return;
            int tid = (t[0] << 8) | t[1];
            Pending p;
            if (!_pending.TryGetValue(tid, out p) || !p.Ep.Equals(from)) return;
            _pending.Remove(tid);
            BVal r = ok ? msg.Get("r", BKind.Dict) : null;
            byte[] id = r == null ? null : r.GetBytes("id");
            bool valid = id != null && id.Length == 20 && !Bencode.SameBytes(id, _id);
            if (valid) Seen(id, from);
            Lookup l = p.Lookup;
            if (l == null || !_lookups.Contains(l)) return;
            l.InFlight--;
            if (!valid)
            {
                p.Cand.State = 3;
                Advance(l, send);
                return;
            }
            p.Cand.State = 2;
            if (p.Cand.Id == null) p.Cand.Id = id;
            byte[] token = r.GetBytes("token");
            if (token != null && token.Length <= 64) p.Cand.Token = token;
            byte[] nodes = r.GetBytes("nodes");
            if (nodes != null)
                for (int i = 0; i + 26 <= nodes.Length && i < 26 * 64; i += 26)
                {
                    byte[] nid = new byte[20];
                    Buffer.BlockCopy(nodes, i, nid, 0, 20);
                    byte[] addr = new byte[4];
                    Buffer.BlockCopy(nodes, i + 20, addr, 0, 4);
                    BtEndpoint ep = new BtEndpoint(new IPAddress(addr), (nodes[i + 24] << 8) | nodes[i + 25]);
                    if (ep.IsUsable && !Bencode.SameBytes(nid, _id)) AddCandidate(l, nid, ep);
                }
            BVal values = r.Get("values", BKind.List);
            if (values != null && l.Swarm != null)
            {
                foreach (BVal v in values.L)
                {
                    if (v.Kind != BKind.Bytes || v.B.Length != 6 || l.Peers.Count >= 1000) continue;
                    List<BtEndpoint> eps = BtEndpoint.ParseCompact(v.B, false);
                    if (eps.Count == 1 && l.Peers.Add(eps[0]))
                    {
                        if (found == null) found = new List<BtEndpoint>();
                        found.Add(eps[0]);
                    }
                }
                swarm = l.Swarm;
            }
            Advance(l, send);
        }

        // ------------------------------------------------------------------ //
        //  Итеративный поиск: alpha = 3 параллельно, пока K ближайших живых не ответили
        // ------------------------------------------------------------------ //
        private void StartLookup(Lookup l, List<Out> send)
        {
            l.Started = DateTime.UtcNow;
            _lookups.Add(l);
            if (!_ready || !Seed(l))
            {
                l.Waiting = true;
                return;
            }
            Advance(l, send);
        }

        private bool Seed(Lookup l)
        {
            foreach (Node n in Closest(l.Target, K * 2)) AddCandidate(l, n.Id, n.Ep);
            if (l.Cands.Count < K)
                foreach (BtEndpoint ep in _bootstrapEps) AddCandidate(l, null, ep);
            l.Waiting = l.Cands.Count == 0;
            return !l.Waiting;
        }

        private void AddCandidate(Lookup l, byte[] id, BtEndpoint ep)
        {
            foreach (Candidate c in l.Cands)
                if (c.Ep.Equals(ep) || (id != null && c.Id != null && Bencode.SameBytes(c.Id, id))) return;
            Candidate nc = new Candidate();
            nc.Id = id;
            nc.Ep = ep;
            l.Cands.Add(nc);
            if (l.Cands.Count > MaxCandidates)
            {
                SortCandidates(l);
                for (int i = l.Cands.Count - 1; i >= MaxCandidates && l.Cands.Count > MaxCandidates; i--)
                    if (l.Cands[i].State == 0) l.Cands.RemoveAt(i);
            }
        }

        private void SortCandidates(Lookup l)
        {
            byte[] target = l.Target;
            l.Cands.Sort(delegate(Candidate a, Candidate b)
            {
                if (a.Id == null || b.Id == null) return a.Id == null ? (b.Id == null ? 0 : 1) : -1;
                return CompareDistance(a.Id, b.Id, target);
            });
        }

        private void Advance(Lookup l, List<Out> send)
        {
            SortCandidates(l);
            int alive = 0;
            foreach (Candidate c in l.Cands)
            {
                if (c.State == 3) continue;
                if (c.State == 0 && l.InFlight < Alpha && l.Queries < MaxLookupQueries && _pending.Count < MaxPending)
                {
                    BVal args = BVal.NewDict().Set("id", BVal.Bytes(_id));
                    string method;
                    if (l.Swarm != null) { method = "get_peers"; args.Set("info_hash", BVal.Bytes(l.Target)); }
                    else { method = "find_node"; args.Set("target", BVal.Bytes(l.Target)); }
                    c.State = 1;
                    l.InFlight++;
                    l.Queries++;
                    SendQuery(c.Ep, method, args, l, c, send);
                }
                if (++alive >= K) break;
            }
            if (l.InFlight <= 0) FinishLookup(l, send);
        }

        private void FinishLookup(Lookup l, List<Out> send)
        {
            _lookups.Remove(l);
            if (l.Swarm == null) return;
            // Объявляем себя, только если к нам можно подключиться: иначе чужие клиенты ломились бы в закрытый порт.
            // Рой спрашивается о приватности вне блокировки (SendAll): magnet узнаёт флаг private только с метаданными.
            if (l.Announce && _ctx.InboundOpen && _ctx.Port > 0 && _ctx.Port <= 65535) _announces.Add(l);
            if (l.Peers.Count > 0)
                _notes.Add(new KeyValuePair<IBtSwarm, string>(l.Swarm, Tr.S("DHT: найдено пиров — ", "DHT: peers found — ") + l.Peers.Count.ToString(CultureInfo.InvariantCulture)));
        }

        // ------------------------------------------------------------------ //
        //  Таблица маршрутов
        // ------------------------------------------------------------------ //
        private int BucketIndex(byte[] id)
        {
            return Math.Min(CommonPrefix(_id, id), _buckets.Count - 1);
        }

        // Узел ответил: обновить или вставить.
        private void Seen(byte[] id, BtEndpoint ep)
        {
            DateTime now = DateTime.UtcNow;
            int bi = BucketIndex(id);
            Bucket b = _buckets[bi];
            foreach (Node n in b.Nodes)
                if (Bencode.SameBytes(n.Id, id))
                {
                    // Хороший узел не переезжает на другой адрес по одному пакету: иначе id легко увести.
                    if (!n.Ep.Equals(ep))
                    {
                        if (n.Fails == 0 && (now - n.LastResponse).TotalMinutes < 15) return;
                        n.Ep = ep;
                    }
                    n.LastResponse = now;
                    n.Fails = 0;
                    return;
                }
            Node fresh = new Node();
            fresh.Id = (byte[])id.Clone();
            fresh.Ep = ep;
            fresh.LastResponse = now;
            while (true)
            {
                b.Spare.RemoveAll(delegate(Node s) { return Bencode.SameBytes(s.Id, id); });
                if (b.Nodes.Count < K)
                {
                    b.Nodes.Add(fresh);
                    return;
                }
                int bad = b.Nodes.FindIndex(delegate(Node s) { return s.Fails >= 2; });
                if (bad >= 0)
                {
                    b.Nodes[bad] = fresh;
                    return;
                }
                // Делится только корзина со своим id: далёкие диапазоны остаются по K узлов.
                if (bi == _buckets.Count - 1 && _buckets.Count < 160)
                {
                    Split();
                    bi = BucketIndex(id);
                    b = _buckets[bi];
                    continue;
                }
                if (b.Spare.Count >= K) b.Spare.RemoveAt(0);
                b.Spare.Add(fresh);
                return;
            }
        }

        private void Split()
        {
            int depth = _buckets.Count - 1;
            Bucket last = _buckets[depth];
            Bucket next = new Bucket();
            last.Nodes.RemoveAll(delegate(Node n) { if (CommonPrefix(_id, n.Id) > depth) { next.Nodes.Add(n); return true; } return false; });
            last.Spare.RemoveAll(delegate(Node n) { if (CommonPrefix(_id, n.Id) > depth) { next.Spare.Add(n); return true; } return false; });
            _buckets.Add(next);
        }

        private List<Node> Closest(byte[] target, int count)
        {
            List<Node> all = new List<Node>();
            foreach (Bucket b in _buckets)
                foreach (Node n in b.Nodes) if (n.Fails < 2) all.Add(n);
            all.Sort(delegate(Node a, Node c) { return CompareDistance(a.Id, c.Id, target); });
            if (all.Count > count) all.RemoveRange(count, all.Count - count);
            return all;
        }

        private static byte[] CompactNodes(List<Node> nodes)
        {
            byte[] b = new byte[26 * nodes.Count];
            for (int i = 0; i < nodes.Count; i++)
            {
                Buffer.BlockCopy(nodes[i].Id, 0, b, 26 * i, 20);
                Buffer.BlockCopy(nodes[i].Ep.ToCompact(), 0, b, 26 * i + 20, 6);
            }
            return b;
        }

        private static int CommonPrefix(byte[] a, byte[] b)
        {
            for (int i = 0; i < 20; i++)
            {
                int x = a[i] ^ b[i];
                if (x == 0) continue;
                int bits = 0;
                while ((x & 0x80) == 0) { x <<= 1; bits++; }
                return i * 8 + bits;
            }
            return 160;
        }

        private static int CompareDistance(byte[] a, byte[] b, byte[] target)
        {
            for (int i = 0; i < 20; i++)
            {
                int da = a[i] ^ target[i], db = b[i] ^ target[i];
                if (da != db) return da < db ? -1 : 1;
            }
            return 0;
        }

        // ------------------------------------------------------------------ //
        //  Отправка
        // ------------------------------------------------------------------ //
        private void Ping(BtEndpoint ep, List<Out> send)
        {
            if (_pending.Count >= MaxPending) return;
            SendQuery(ep, "ping", BVal.NewDict().Set("id", BVal.Bytes(_id)), null, null, send);
        }

        private void SendQuery(BtEndpoint to, string method, BVal args, Lookup l, Candidate c, List<Out> send)
        {
            int tid;
            do { tid = _nextTid = (_nextTid + 1) & 0xFFFF; } while (_pending.ContainsKey(tid));
            Pending p = new Pending();
            p.Ep = to;
            p.Method = method;
            p.Lookup = l;
            p.Cand = c;
            p.Sent = DateTime.UtcNow;
            _pending[tid] = p;
            BVal m = BVal.NewDict().Set("t", BVal.Bytes(new byte[] { (byte)(tid >> 8), (byte)tid })).Set("y", BVal.Str("q"))
                .Set("q", BVal.Str(method)).Set("a", args).Set("v", BVal.Bytes(Version));
            send.Add(new Out { To = to, Data = Bencode.Encode(m) });
        }

        private static Out Error(BtEndpoint to, byte[] t, int code, string text)
        {
            BVal e = BVal.NewList().Add(BVal.Int(code)).Add(BVal.Str(text));
            BVal m = BVal.NewDict().Set("t", BVal.Bytes(t)).Set("y", BVal.Str("e")).Set("e", e).Set("v", BVal.Bytes(Version));
            return new Out { To = to, Data = Bencode.Encode(m) };
        }

        // После выхода из блокировки: сеть и чужие объекты (рой) никогда не вызываются под _gate.
        private void SendAll(List<Out> send)
        {
            IBtUdp udp = _ctx.Udp;
            if (udp != null) foreach (Out o in send) udp.Send(o.To, o.Data, o.Data.Length);
            List<KeyValuePair<IBtSwarm, string>> notes = null;
            List<Lookup> announces = null;
            lock (_gate)
            {
                if (_notes.Count > 0)
                {
                    notes = new List<KeyValuePair<IBtSwarm, string>>(_notes);
                    _notes.Clear();
                }
                if (_announces.Count > 0)
                {
                    announces = new List<Lookup>(_announces);
                    _announces.Clear();
                }
            }
            if (notes != null) foreach (KeyValuePair<IBtSwarm, string> n in notes) n.Key.Journal(n.Value);
            if (announces == null) return;
            List<Out> more = new List<Out>();
            foreach (Lookup l in announces)
            {
                if (l.Swarm.IsPrivate) continue;
                lock (_gate)
                {
                    if (_disposed) return;
                    int sent = 0;
                    foreach (Candidate c in l.Cands)
                    {
                        if (c.State != 2 || c.Token == null) continue;
                        BVal args = BVal.NewDict().Set("id", BVal.Bytes(_id)).Set("info_hash", BVal.Bytes(l.Target))
                            .Set("port", BVal.Int(_ctx.Port)).Set("token", BVal.Bytes(c.Token)).Set("implied_port", BVal.Int(0));
                        SendQuery(c.Ep, "announce_peer", args, null, null, more);
                        if (++sent >= K) break;
                    }
                }
            }
            if (udp != null) foreach (Out o in more) udp.Send(o.To, o.Data, o.Data.Length);
        }

        // ------------------------------------------------------------------ //
        //  Запуск, обслуживание, dht.json
        // ------------------------------------------------------------------ //
        private void LoadAndBootstrap()
        {
            List<BtEndpoint> saved = new List<BtEndpoint>();
            try
            {
                string file = StateFile;
                JVal j = file == null ? null : DlPaths.ReadJson(file);
                if (j != null)
                {
                    byte[] id = Bencode.FromHex(j.GetStr("id") ?? "");
                    if (id != null && id.Length == 20) lock (_gate) _id = id;
                    JVal nodes = j.Get("nodes");
                    if (nodes != null && nodes.Kind == JKind.Arr)
                        foreach (JVal n in nodes.V)
                        {
                            BtEndpoint ep = n.Kind == JKind.Str ? BtEndpoint.TryParse(n.Raw) : null;
                            if (ep != null && !ep.IsV6 && saved.Count < 200) saved.Add(ep);
                        }
                }
            }
            catch (Exception ex) { DlLog.Report(ex); }
            List<Out> send = new List<Out>();
            lock (_gate)
            {
                if (_disposed) return;
                _ready = true;
                _lastSave = DateTime.UtcNow;
                foreach (BtEndpoint ep in saved) Ping(ep, send);
                foreach (BtEndpoint ep in _earlyNodes) Ping(ep, send);
                _earlyNodes.Clear();
            }
            SendAll(send);
            ResolveAndBootstrap(saved);
        }

        // Рабочий поток пула: DNS здесь допустим.
        private void ResolveAndBootstrap(List<BtEndpoint> extra)
        {
            List<string> hosts;
            lock (_gate)
            {
                if (_disposed) return;
                _bootstrapping = true;
                _lastBootstrap = DateTime.UtcNow;
                hosts = new List<string>(Bootstrap);
            }
            List<BtEndpoint> eps = new List<BtEndpoint>();
            try
            {
                foreach (string h in hosts)
                {
                    BtEndpoint literal = BtEndpoint.TryParse(h);
                    if (literal != null) { if (!literal.IsV6) eps.Add(literal); continue; }
                    int colon = h.LastIndexOf(':');
                    int port;
                    if (colon <= 0 || !int.TryParse(h.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port)) continue;
                    try
                    {
                        foreach (IPAddress ip in Dns.GetHostAddresses(h.Substring(0, colon)))
                            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                BtEndpoint ep = new BtEndpoint(ip, port);
                                if (ep.IsUsable && !eps.Contains(ep)) eps.Add(ep);
                            }
                    }
                    catch (Exception) { }
                }
            }
            finally
            {
                List<Out> send = new List<Out>();
                lock (_gate)
                {
                    _bootstrapping = false;
                    if (!_disposed)
                    {
                        _bootstrapEps.Clear();
                        _bootstrapEps.AddRange(eps);
                        Lookup self = new Lookup();
                        self.Target = (byte[])_id.Clone();
                        if (extra != null) foreach (BtEndpoint ep in extra) AddCandidate(self, null, ep);
                        StartLookup(self, send);
                        foreach (Lookup l in _lookups.ToArray())
                            if (l.Waiting && Seed(l)) Advance(l, send);
                    }
                }
                SendAll(send);
            }
        }

        private void OnTimer(object state)
        {
            if (Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0) return;
            try { Tick(); }
            catch (Exception ex) { DlLog.Report(ex); }
            finally { Interlocked.Exchange(ref _tickBusy, 0); }
        }

        private void Tick()
        {
            DateTime now = DateTime.UtcNow;
            List<Out> send = new List<Out>();
            bool rebootstrap = false, save = false;
            lock (_gate)
            {
                if (_disposed || !_ready) return;
                List<int> expired = new List<int>();
                foreach (KeyValuePair<int, Pending> kv in _pending)
                    if ((now - kv.Value.Sent).TotalMilliseconds > QueryTimeoutMs) expired.Add(kv.Key);
                foreach (int tid in expired)
                {
                    Pending p = _pending[tid];
                    _pending.Remove(tid);
                    Fail(p.Ep);
                    if (p.Lookup != null && _lookups.Contains(p.Lookup))
                    {
                        p.Cand.State = 3;
                        p.Lookup.InFlight--;
                        Advance(p.Lookup, send);
                    }
                }
                foreach (Lookup l in _lookups.ToArray())
                {
                    if (l.Waiting && (now - l.Started).TotalSeconds > 60) _lookups.Remove(l);
                    else if (l.Waiting && Seed(l)) Advance(l, send);
                }
                if ((now - _secretAt).TotalMinutes >= 5)
                {
                    _prevSecret = _secret;
                    _secret = Random(20);
                    _secretAt = now;
                }
                // Сомнительные узлы (15 минут молчания) — ping, не больше трёх за такт.
                int pings = 0;
                foreach (Bucket b in _buckets)
                    foreach (Node n in b.Nodes)
                        if (pings < 3 && (now - n.LastResponse).TotalMinutes >= 15 && (now - n.LastPing).TotalSeconds >= 60)
                        {
                            n.LastPing = now;
                            Ping(n.Ep, send);
                            pings++;
                        }
                if ((now - _lastExpire).TotalSeconds >= 60)
                {
                    _lastExpire = now;
                    foreach (string key in new List<string>(_store.Keys))
                    {
                        Dictionary<BtEndpoint, DateTime> peers = _store[key];
                        foreach (BtEndpoint ep in new List<BtEndpoint>(peers.Keys))
                            if ((now - peers[ep]).TotalMinutes >= 30) peers.Remove(ep);
                        if (peers.Count == 0) _store.Remove(key);
                    }
                }
                int alive = 0;
                foreach (Bucket b in _buckets) foreach (Node n in b.Nodes) if (n.Fails < 2) alive++;
                if (alive == 0 && !_bootstrapping && (now - _lastBootstrap).TotalSeconds >= RebootstrapSeconds)
                {
                    rebootstrap = true;
                    _bootstrapping = true;
                }
                if (alive > 0 && (now - _lastRefresh).TotalMinutes >= 15 && _lastRefresh != DateTime.MinValue)
                {
                    Lookup self = new Lookup();
                    self.Target = (byte[])_id.Clone();
                    StartLookup(self, send);
                }
                if (_lastRefresh == DateTime.MinValue || (now - _lastRefresh).TotalMinutes >= 15) _lastRefresh = now;
                if (alive > 0 && (now - _lastSave).TotalMinutes >= 5)
                {
                    _lastSave = now;
                    save = true;
                }
            }
            SendAll(send);
            if (save) Save();
            if (rebootstrap) ThreadPool.QueueUserWorkItem(delegate { ResolveAndBootstrap(null); });
        }

        private void Fail(BtEndpoint ep)
        {
            foreach (Bucket b in _buckets)
                for (int i = 0; i < b.Nodes.Count; i++)
                {
                    Node n = b.Nodes[i];
                    if (!n.Ep.Equals(ep)) continue;
                    n.Fails++;
                    // Узел плох — его место отдаётся запасному, который когда-то ответил.
                    if (n.Fails >= 2 && b.Spare.Count > 0)
                    {
                        b.Nodes[i] = b.Spare[b.Spare.Count - 1];
                        b.Spare.RemoveAt(b.Spare.Count - 1);
                    }
                    else if (n.Fails >= 5) b.Nodes.RemoveAt(i);
                    return;
                }
        }

        // Атомарно (DlPaths.WriteAtomic): оборванная запись не теряет прежний список узлов.
        private void Save()
        {
            string file = StateFile;
            if (file == null) return;
            JVal root = JVal.NewObj();
            lock (_gate)
            {
                if (!_ready) return;
                root.Set("id", JVal.NewStr(Bencode.Hex(_id)));
                JVal nodes = JVal.NewArr();
                foreach (Bucket b in _buckets)
                    foreach (Node n in b.Nodes)
                        if (n.Fails == 0 && n.LastResponse != DateTime.MinValue && nodes.V.Count < 200) nodes.V.Add(JVal.NewStr(n.Ep.ToString()));
                root.Set("nodes", nodes);
            }
            try { DlPaths.WriteAtomic(file, Jsn.Write(root)); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        private static byte[] Random(int n)
        {
            byte[] b = new byte[n];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return b;
        }

        // ------------------------------------------------------------------ //
        //  Для тестов
        // ------------------------------------------------------------------ //
        internal byte[] NodeId { get { lock (_gate) return (byte[])_id.Clone(); } }
        internal bool Ready { get { lock (_gate) return _ready; } }
        internal int BucketCount { get { lock (_gate) return _buckets.Count; } }
        internal int LookupCount { get { lock (_gate) return _lookups.Count; } }

        // Как будто узел ответил на запрос; true — он в таблице (а не в запасе).
        internal bool InsertNodeForTest(byte[] id, BtEndpoint ep)
        {
            lock (_gate)
            {
                Seen(id, ep);
                foreach (Bucket b in _buckets) foreach (Node n in b.Nodes) if (Bencode.SameBytes(n.Id, id)) return true;
                return false;
            }
        }

        internal int StoredPeers(byte[] hash)
        {
            lock (_gate)
            {
                Dictionary<BtEndpoint, DateTime> peers;
                return _store.TryGetValue(Bencode.Hex(hash), out peers) ? peers.Count : 0;
            }
        }
    }
}
