// Windows Process Cleaner — «Загрузки», торренты: одно соединение с пиром — рукопожатия, состояние, запросы и отдача.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Все методы, кроме реализации IBtPeerLink, вызываются только из потока реактора. IBtPeerLink может прийти из любого
// потока (расширения, PEX) — тогда работа уходит в реактор через Post.
// Входящее соединение не знает торрента до рукопожатия: первый байт 19 и «BitTorrent protocol» — открытый текст,
// иначе — рукопожатие MSE (если часть шифрования есть в сборке). Всё, что прислал пир, считается враждебным: неверная
// длина, индекс вне торрента, битовое поле с лишними битами — разрыв.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    internal enum BtPeerPhase { Crypto, Handshake, Active }

    internal sealed class BtPeer : BtConnection, IBtPeerLink, IBtPickPeer
    {
        public const int ConnectTimeoutMs = 10000;
        public const int HandshakeTimeoutMs = 20000;
        public const int KeepAliveMs = 120000;
        public const int IdleMs = 180000;
        public const int SnubMs = 60000;
        public const int MaxTheirRequests = BtExtHandshake.DefaultReqQ;
        public const int MaxAllowedFast = 64;
        public const long DiskBacklog = 16L * 1024 * 1024;

        private readonly BtSession _s;
        private readonly BtContext _ctx;
        internal BtTorrent Torrent;
        internal BtCandidate Candidate;
        internal readonly BtPeerOrigin Origin;
        internal BtPeerPhase Phase = BtPeerPhase.Handshake;
        internal readonly bool PlainOnly;
        private IBtStreamHandshake _hs;
        private bool _plainChecked;
        internal bool CryptoFailed;
        internal bool BothSeeds;
        private bool _halfOpen;

        internal byte[] PeerId;
        internal bool FastExt, ExtProto, DhtProto;
        internal bool AmChoking = true, AmInterested, PeerChoking = true, PeerInterested;
        internal BtBitfield Bits;
        internal bool SeedFlag;
        private BtPicker _countedIn;
        private byte[] _pendingBits;
        private bool _pendingAll;
        private List<int> _pendingHaves;
        private bool _anyMessage;

        internal readonly List<BtBlockReq> Outstanding = new List<BtBlockReq>();
        private readonly LinkedList<BtBlockReq> _theirs = new LinkedList<BtBlockReq>();
        private readonly List<BtBlockReq> _reading = new List<BtBlockReq>();
        internal readonly HashSet<int> AllowedFastIn = new HashSet<int>();
        private readonly HashSet<int> _allowedOut = new HashSet<int>();
        private readonly Dictionary<string, int> _theirExt = new Dictionary<string, int>(StringComparer.Ordinal);
        private volatile int _listenPort;
        private volatile string _client = "";
        private int _reqq = BtExtHandshake.DefaultReqQ;

        internal readonly DlSpeedMeter DownMeter = new DlSpeedMeter(), UpMeter = new DlSpeedMeter();
        internal long DownBps, UpBps, Downloaded, Uploaded;
        internal long LastPieceMs, UnchokedAtMs;
        internal bool Snubbed, Optimistic;
        private long _flushCloseBy;
        private string _flushReason;

        public BtPeer(BtSession session, Socket socket, BtEndpoint remote, bool outgoing, BtTorrent torrent, BtPeerOrigin origin, bool plainOnly)
            : base(socket, remote, outgoing)
        {
            _s = session;
            _ctx = session.Context;
            Torrent = torrent;
            Origin = origin;
            PlainOnly = plainOnly;
            LastPieceMs = OpenedMs;
            if (outgoing)
            {
                Connecting = true;
                _halfOpen = true;
                _s.HalfOpen++;
            }
        }

        // ---------- дроссель ----------
        protected override DlTokenBucket LocalDown { get { return Torrent == null ? null : Torrent.DownBucket; } }
        protected override DlTokenBucket LocalUp { get { return Torrent == null ? null : Torrent.UpBucket; } }
        protected override DlTokenBucket GlobalDown { get { return _ctx.DownGlobal; } }
        protected override DlTokenBucket GlobalUp { get { return _ctx.UpGlobal; } }

        // ---------- IBtPeerLink (любой поток) ----------
        public BtEndpoint Endpoint { get { return Remote; } }
        public int ListenPort { get { return _listenPort; } }
        public bool Outgoing { get { return IsOutgoing; } }
        public bool Encrypted { get { return TxCipher != null || RxCipher != null; } }
        public bool IsSeed { get { return SeedFlag; } }
        public string Client { get { return _client; } }

        public bool Supports(string extension)
        {
            if (extension == null) return false;
            lock (_theirExt) return _theirExt.ContainsKey(extension);
        }

        public void SendExtended(string extension, byte[] payload)
        {
            if (extension == null) return;
            if (_s.Reactor.OnThread) DoSendExtended(extension, payload);
            else _s.Reactor.Post(delegate { DoSendExtended(extension, payload); });
        }

        void IBtPeerLink.Close(string reason)
        {
            if (_s.Reactor.OnThread) Close(reason);
            else _s.Reactor.Post(delegate { Close(reason); });
        }

        private void DoSendExtended(string extension, byte[] payload)
        {
            if (Dead || Phase != BtPeerPhase.Active || (payload != null && payload.Length > BtWire.MaxMessage - 2)) return;
            int id;
            lock (_theirExt)
                if (!_theirExt.TryGetValue(extension, out id)) return;
            Send(BtWire.ExtendedMsg(id, payload), 0);
        }

        // ---------- IBtPickPeer ----------
        public bool HasPiece(int piece) { return SeedFlag || (Bits != null && Bits[piece]); }
        public bool CanRequest(int piece) { return HasPiece(piece) && (!PeerChoking || AllowedFastIn.Contains(piece)); }
        public string BanKey { get { return Remote.Address.ToString(); } }

        // ---------- соединение и рукопожатия ----------
        internal override void OnConnected()
        {
            ClearHalfOpen();
            if (Torrent == null) { Close(Tr.S("торрент не найден", "unknown torrent")); return; }
            if (!PlainOnly && BtFactories.MseOutgoing != null && _ctx.Encryption != BtEncryption.Off)
            {
                _hs = BtFactories.MseOutgoing(Torrent.InfoHash, _ctx.Encryption);
                if (_hs != null)
                {
                    List<byte[]> send = new List<byte[]>();
                    _hs.Begin(send);
                    foreach (byte[] b in send) SendRaw(b);
                    Phase = BtPeerPhase.Crypto;
                    return;
                }
            }
            if (_ctx.Encryption == BtEncryption.Require)
            {
                Close(Tr.S("нужно шифрование, а его нет", "encryption is required but unavailable"));
                return;
            }
            SendHandshake();
        }

        private void ClearHalfOpen()
        {
            if (!_halfOpen) return;
            _halfOpen = false;
            _s.HalfOpen--;
        }

        private void SendHandshake()
        {
            Send(BtWire.Handshake(Torrent.InfoHash, _ctx.PeerId, _ctx.Dht != null), 0);
        }

        protected override void OnReceived()
        {
            int pos = 0;
            while (!Dead)
            {
                int avail = RxCount - pos;
                if (avail <= 0) break;
                if (Phase == BtPeerPhase.Crypto)
                {
                    if (!FeedCrypto(pos, avail)) return;
                    pos = 0;
                    continue;
                }
                if (Phase == BtPeerPhase.Handshake)
                {
                    if (!IsOutgoing && Torrent == null && !_plainChecked)
                    {
                        if (!BtWire.LooksPlain(Rx, pos, avail))
                        {
                            if (!StartIncomingCrypto()) return;
                            continue;
                        }
                        if (avail < 20) break;
                        if (_ctx.Encryption == BtEncryption.Require)
                        {
                            Close(Tr.S("открытый текст запрещён настройкой шифрования", "plaintext is refused by the encryption setting"));
                            return;
                        }
                        _plainChecked = true;
                    }
                    if (avail < BtWire.HandshakeLength) break;
                    if (!OnHandshake(pos)) return;
                    pos += BtWire.HandshakeLength;
                    continue;
                }
                int need;
                int size = BtWire.FrameSize(Rx, pos, avail, out need);
                if (size < 0) { Close(Tr.S("сообщение длиннее допустимого", "a message is longer than allowed")); return; }
                if (size == 0)
                {
                    if (need > Rx.Length)
                    {
                        Compact(pos);
                        pos = 0;
                        EnsureRx(need);
                    }
                    break;
                }
                HandleMessage(pos + 4, size - 4);
                pos += size;
            }
            if (!Dead) Compact(pos);
        }

        private void Compact(int pos)
        {
            if (pos <= 0) return;
            if (pos < RxCount) Buffer.BlockCopy(Rx, pos, Rx, 0, RxCount - pos);
            RxCount -= pos;
        }

        private bool StartIncomingCrypto()
        {
            if (BtFactories.MseIncoming == null || _ctx.Encryption == BtEncryption.Off)
            {
                Close(Tr.S("неизвестный протокол", "unknown protocol"));
                return false;
            }
            _hs = BtFactories.MseIncoming(_s.Req2Lookup, _ctx.Encryption);
            if (_hs == null)
            {
                Close(Tr.S("неизвестный протокол", "unknown protocol"));
                return false;
            }
            List<byte[]> send = new List<byte[]>();
            _hs.Begin(send);
            foreach (byte[] b in send) SendRaw(b);
            Phase = BtPeerPhase.Crypto;
            return true;
        }

        // Рукопожатие шифрования забирает все принятые байты; после Done остаток (уже расшифрованный) — начало потока BT.
        private bool FeedCrypto(int pos, int avail)
        {
            List<byte[]> send = new List<byte[]>();
            BtHandshakeState st;
            try { st = _hs.Feed(Rx, pos, avail, send); }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                st = BtHandshakeState.Failed;
            }
            foreach (byte[] b in send) SendRaw(b);
            RxCount = 0;
            if (st == BtHandshakeState.NeedMore) return false;
            if (st == BtHandshakeState.Failed)
            {
                CryptoFailed = true;
                Close(Tr.S("рукопожатие шифрования не удалось: ", "the encryption handshake failed: ") + (_hs.Error ?? ""));
                return false;
            }
            TxCipher = _hs.Encryptor;
            RxCipher = _hs.Decryptor;
            if (_ctx.Encryption == BtEncryption.Require && (TxCipher == null || RxCipher == null))
            {
                Close(Tr.S("пир выбрал открытый текст, а нужно шифрование", "the peer chose plaintext but encryption is required"));
                return false;
            }
            byte[] rest = _hs.Remaining;
            if (rest != null && rest.Length > 0)
            {
                EnsureRx(rest.Length);
                Buffer.BlockCopy(rest, 0, Rx, 0, rest.Length);
                RxCount = rest.Length;
            }
            _plainChecked = true;
            Phase = BtPeerPhase.Handshake;
            if (IsOutgoing) SendHandshake();
            return true;
        }

        private bool OnHandshake(int pos)
        {
            byte[] reserved, infoHash, peerId;
            if (!BtWire.ParseHandshake(Rx, pos, out reserved, out infoHash, out peerId))
            {
                Close(Tr.S("неверное рукопожатие", "a bad handshake"));
                return false;
            }
            if (Torrent == null)
            {
                BtTorrent t = _s.FindForIncoming(infoHash);
                string why = t == null ? Tr.S("торрент не найден", "unknown torrent") : t.RefuseIncoming(this);
                if (why != null) { Close(why); return false; }
                Torrent = t;
                _s.Adopt(this);
                t.Attach(this);
                SendHandshake();
            }
            else if (!Bencode.SameBytes(infoHash, Torrent.InfoHash))
            {
                Close(Tr.S("info-hash не совпал", "info-hash mismatch"));
                return false;
            }
            if (Bencode.SameBytes(peerId, _ctx.PeerId)) { Close(Tr.S("соединение с самим собой", "connected to ourselves")); return false; }
            if (Torrent.HasPeerId(peerId, this)) { Close(Tr.S("повторное соединение с тем же пиром", "a duplicate connection to the same peer")); return false; }
            PeerId = peerId;
            FastExt = BtWire.SupportsFast(reserved);
            ExtProto = BtWire.SupportsExtended(reserved);
            DhtProto = BtWire.SupportsDht(reserved);
            Phase = BtPeerPhase.Active;
            LastPieceMs = BtNetClock.Ms;
            SendHaveState();
            if (ExtProto) Send(BtWire.ExtendedMsg(0, Torrent.BuildExtHandshake(this)), 0);
            if (DhtProto && _ctx.Dht != null) Send(BtWire.PortMsg(_ctx.Port), 0);
            Torrent.OnPeerActive(this);
            return true;
        }

        private void SendHaveState()
        {
            BtBitfield have = Torrent.Have;
            BtMeta meta = Torrent.Meta;
            if (have == null || meta == null || Torrent.Picker == null)
            {
                if (FastExt) Send(BtWire.Simple(BtWire.HaveNone), 0);
                return;
            }
            int set = have.SetCount;
            if (FastExt && set == have.Count) Send(BtWire.Simple(BtWire.HaveAll), 0);
            else if (FastExt && set == 0) Send(BtWire.Simple(BtWire.HaveNone), 0);
            else if (set > 0) Send(BtWire.BitfieldMsg(have.ToBytes()), 0);
            // Allowed fast — только на больших торрентах: иначе набор из 10 кусков обходил бы очередь слотов целиком.
            if (FastExt && meta.PieceCount >= 100 && !Remote.IsV6)
                foreach (int index in BtWire.AllowedFastSet(Remote.Address, Torrent.InfoHash, meta.PieceCount, 10))
                {
                    _allowedOut.Add(index);
                    Send(BtWire.WithIndex(BtWire.AllowedFast, index), 0);
                }
        }

        // ---------- сообщения ----------
        private void Violation(int id)
        {
            Close(Tr.S("нарушение протокола, сообщение ", "a protocol violation, message ") + id);
        }

        private void HandleMessage(int at, int len)
        {
            if (len == 0) return;                                        // keep-alive
            int id = Rx[at];
            int p = at + 1, n = len - 1;
            // «Первое сообщение» для bitfield/have all/have none: расширенное рукопожатие и PORT libtorrent шлёт раньше них.
            bool first = !_anyMessage;
            if (id != BtWire.Extended && id != BtWire.Port) _anyMessage = true;
            long now = BtNetClock.Ms;
            switch (id)
            {
                case BtWire.Choke:
                    if (n != 0) { Violation(id); return; }
                    PeerChoking = true;
                    ReleaseRequests(true);
                    break;
                case BtWire.Unchoke:
                    if (n != 0) { Violation(id); return; }
                    PeerChoking = false;
                    FillRequests(now);
                    break;
                case BtWire.Interested:
                case BtWire.NotInterested:
                    if (n != 0) { Violation(id); return; }
                    PeerInterested = id == BtWire.Interested;
                    Torrent.OnPeerInterest(this, now);
                    break;
                case BtWire.Have:
                    if (n != 4) { Violation(id); return; }
                    OnHave(BtWire.ReadInt(Rx, p), now);
                    break;
                case BtWire.Bitfield:
                    if (!first || n > (1 << 20)) { Violation(id); return; }
                    OnBitfield(p, n, now);
                    break;
                case BtWire.Request:
                    if (n != 12) { Violation(id); return; }
                    OnRequest(BtWire.ReadInt(Rx, p), BtWire.ReadInt(Rx, p + 4), BtWire.ReadInt(Rx, p + 8));
                    break;
                case BtWire.Piece:
                    if (n < 8) { Violation(id); return; }
                    OnPiece(BtWire.ReadInt(Rx, p), BtWire.ReadInt(Rx, p + 4), p + 8, n - 8, now);
                    break;
                case BtWire.Cancel:
                    if (n != 12) { Violation(id); return; }
                    OnCancel(BtWire.ReadInt(Rx, p), BtWire.ReadInt(Rx, p + 4), BtWire.ReadInt(Rx, p + 8));
                    break;
                case BtWire.Port:
                    if (n != 2) { Violation(id); return; }
                    if (_ctx.Dht != null)
                    {
                        BtEndpoint node = new BtEndpoint(Remote.Address, (Rx[p] << 8) | Rx[p + 1]);
                        if (node.IsUsable)
                            try { _ctx.Dht.AddNode(node); }
                            catch (Exception ex) { DlLog.Report(ex); }
                    }
                    break;
                case BtWire.Suggest:
                    if (!FastExt || n != 4) { Violation(id); return; }
                    break;
                case BtWire.HaveAll:
                case BtWire.HaveNone:
                    if (!FastExt || n != 0 || !first) { Violation(id); return; }
                    if (id == BtWire.HaveAll) OnHaveAll(now);
                    break;
                case BtWire.Reject:
                    if (!FastExt || n != 12) { Violation(id); return; }
                    OnReject(BtWire.ReadInt(Rx, p), BtWire.ReadInt(Rx, p + 4), BtWire.ReadInt(Rx, p + 8));
                    break;
                case BtWire.AllowedFast:
                    if (!FastExt || n != 4) { Violation(id); return; }
                    int index = BtWire.ReadInt(Rx, p);
                    BtMeta meta = Torrent.Meta;
                    if (meta != null && index >= 0 && index < meta.PieceCount && AllowedFastIn.Count < MaxAllowedFast)
                    {
                        AllowedFastIn.Add(index);
                        FillRequests(now);
                    }
                    break;
                case BtWire.Extended:
                    if (!ExtProto || n < 1) { Violation(id); return; }
                    OnExtended(Rx[p], p + 1, n - 1);
                    break;
            }
        }

        private void OnHave(int piece, long now)
        {
            BtMeta meta = Torrent.Meta;
            if (meta == null)
            {
                if (_pendingHaves == null) _pendingHaves = new List<int>();
                if (piece < 0 || _pendingHaves.Count >= 1000000) { Violation(BtWire.Have); return; }
                _pendingHaves.Add(piece);
                return;
            }
            if (piece < 0 || piece >= meta.PieceCount) { Violation(BtWire.Have); return; }
            if (SeedFlag) return;
            if (Bits == null) Bits = new BtBitfield(meta.PieceCount);
            if (Bits[piece]) return;
            Bits[piece] = true;
            BtPicker picker = Torrent.Picker;
            bool counted = _countedIn != null && _countedIn == picker;
            if (counted) picker.PeerHave(piece);
            if (Bits.All)
            {
                SeedFlag = true;
                if (counted) picker.PeerBecameSeed(Bits);
                Torrent.OnPeerBecameSeed(this);
                if (Dead) return;
            }
            if (!AmInterested && picker != null && picker.Wants(piece)) SetInterested(true);
            FillRequests(now);
        }

        private void OnBitfield(int at, int n, long now)
        {
            BtMeta meta = Torrent.Meta;
            byte[] raw = new byte[n];
            Buffer.BlockCopy(Rx, at, raw, 0, n);
            if (meta == null)
            {
                _pendingBits = raw;
                return;
            }
            BtBitfield bits = BtBitfield.FromBytes(raw, meta.PieceCount);
            if (bits == null) { Close(Tr.S("неверное битовое поле", "a bad bitfield")); return; }
            // При рукопожатии пир уже учтён выборщиком — пустым; вклад снимается по прежнему набору, иначе новый не попадёт в счёт.
            UnregisterBits();
            Bits = bits;
            SeedFlag = bits.All;
            AfterBits(now);
        }

        private void OnHaveAll(long now)
        {
            if (Torrent.Meta == null) { _pendingAll = true; return; }
            UnregisterBits();
            SeedFlag = true;
            AfterBits(now);
        }

        // Метаданные пришли после битового поля (magnet): отложенное наличие проверяется на размер торрента.
        internal void ApplyPendingBits(BtMeta meta)
        {
            if (_pendingAll) SeedFlag = true;
            else if (_pendingBits != null)
            {
                Bits = BtBitfield.FromBytes(_pendingBits, meta.PieceCount);
                if (Bits == null) { Close(Tr.S("неверное битовое поле", "a bad bitfield")); return; }
            }
            if (_pendingHaves != null && !SeedFlag)
                foreach (int piece in _pendingHaves)
                {
                    if (piece >= meta.PieceCount) { Violation(BtWire.Have); return; }
                    if (Bits == null) Bits = new BtBitfield(meta.PieceCount);
                    Bits[piece] = true;
                }
            if (Bits != null && Bits.All) SeedFlag = true;
            _pendingBits = null;
            _pendingHaves = null;
            _pendingAll = false;
        }

        private void AfterBits(long now)
        {
            BtPicker picker = Torrent.Picker;
            if (picker != null) RegisterBits(picker);
            if (SeedFlag)
            {
                Torrent.OnPeerBecameSeed(this);
                if (Dead) return;
            }
            UpdateInterest();
            FillRequests(now);
        }

        internal void RegisterBits(BtPicker picker)
        {
            if (picker == null || _countedIn == picker) return;
            UnregisterBits();
            picker.AddPeerBits(Bits, SeedFlag);
            _countedIn = picker;
        }

        private void UnregisterBits()
        {
            if (_countedIn == null) return;
            _countedIn.RemovePeerBits(Bits, SeedFlag);
            _countedIn = null;
        }

        internal void SendFrame(byte[] frame)
        {
            if (!Dead && Phase == BtPeerPhase.Active) Send(frame, 0);
        }

        internal void SendHave(int piece) { SendFrame(BtWire.WithIndex(BtWire.Have, piece)); }

        internal void SetInterested(bool value)
        {
            if (AmInterested == value || Phase != BtPeerPhase.Active) return;
            AmInterested = value;
            Send(BtWire.Simple(value ? BtWire.Interested : BtWire.NotInterested), 0);
        }

        // Есть ли у пира нужный нам кусок.
        internal void UpdateInterest()
        {
            if (Phase != BtPeerPhase.Active) return;
            BtPicker picker = Torrent.Picker;
            bool want = false;
            if (picker != null && Torrent.IsDownloading && picker.WantedMissing > 0)
            {
                if (SeedFlag) want = true;
                else if (Bits != null)
                    for (int i = 0; i < Bits.Count && !want; i++) want = Bits[i] && picker.Wants(i);
            }
            SetInterested(want);
        }

        // ---------- скачивание ----------
        internal void FillRequests(long now)
        {
            if (Dead || Phase != BtPeerPhase.Active || !AmInterested || !Torrent.IsDownloading) return;
            BtPicker picker = Torrent.Picker;
            if (picker == null || (PeerChoking && AllowedFastIn.Count == 0) || _s.Disk.QueuedBytes > DiskBacklog) return;
            int depth = Snubbed ? 1 : (int)Math.Min(Math.Min(_reqq, 128), 4 + DownBps / BtMeta.BlockSize);
            int room = depth - Outstanding.Count;
            if (room <= 0) return;
            List<BtBlockReq> picked = new List<BtBlockReq>();
            picker.Pick(this, room, now, picked);
            foreach (BtBlockReq r in picked)
            {
                Outstanding.Add(r);
                Send(BtWire.Block(BtWire.Request, r.Piece, r.Begin, r.Length), 0);
            }
        }

        private int FindOutstanding(int piece, int begin, int length)
        {
            for (int i = 0; i < Outstanding.Count; i++) if (Outstanding[i].Same(piece, begin, length)) return i;
            return -1;
        }

        private void OnPiece(int piece, int begin, int at, int count, long now)
        {
            int i = FindOutstanding(piece, begin, count);
            if (i >= 0) Outstanding.RemoveAt(i);
            LastPieceMs = now;
            Snubbed = false;
            Downloaded += count;
            DownMeter.Add(count);
            Torrent.OnBlock(this, piece, begin, Rx, at, count);
            if (!Dead) FillRequests(now);
        }

        // Другой пир прислал этот блок раньше (эндшпиль).
        internal void CancelRequest(BtBlockReq r)
        {
            int i = FindOutstanding(r.Piece, r.Begin, r.Length);
            if (i < 0 || Dead) return;
            Outstanding.RemoveAt(i);
            Send(BtWire.Block(BtWire.Cancel, r.Piece, r.Begin, r.Length), 0);
        }

        private void OnReject(int piece, int begin, int length)
        {
            int i = FindOutstanding(piece, begin, length);
            if (i < 0) return;
            Outstanding.RemoveAt(i);
            BtPicker picker = Torrent.Picker;
            if (picker != null) picker.Release(this, piece, begin);
        }

        // Задушили (keepAllowed — оставить запросы allowed fast), пир тормозит или уходит: блоки возвращаются в общий выбор.
        internal void ReleaseRequests(bool keepAllowed)
        {
            BtPicker picker = Torrent == null ? null : Torrent.Picker;
            for (int i = Outstanding.Count - 1; i >= 0; i--)
            {
                BtBlockReq r = Outstanding[i];
                if (keepAllowed && AllowedFastIn.Contains(r.Piece)) continue;
                Outstanding.RemoveAt(i);
                if (picker != null) picker.Release(this, r.Piece, r.Begin);
            }
        }

        // ---------- отдача ----------
        private void OnRequest(int piece, int begin, int length)
        {
            BtMeta meta = Torrent.Meta;
            BtBitfield have = Torrent.Have;
            bool ok = meta != null && have != null && Torrent.CanUpload && piece >= 0 && piece < meta.PieceCount && begin >= 0
                      && length > 0 && length <= BtMeta.BlockSize && (long)begin + length <= meta.PieceSize(piece) && have[piece];
            if (ok && AmChoking && !_allowedOut.Contains(piece)) ok = false;
            if (ok && _theirs.Count >= MaxTheirRequests) ok = false;
            if (!ok)
            {
                if (FastExt) Send(BtWire.Block(BtWire.Reject, piece, begin, length), 0);
                return;
            }
            foreach (BtBlockReq q in _theirs) if (q.Same(piece, begin, length)) return;
            _theirs.AddLast(new BtBlockReq(piece, begin, length));
            ServeUploads();
        }

        private void OnCancel(int piece, int begin, int length)
        {
            for (LinkedListNode<BtBlockReq> node = _theirs.First; node != null; node = node.Next)
                if (node.Value.Same(piece, begin, length)) { _theirs.Remove(node); break; }
            for (int i = 0; i < _reading.Count; i++)
                if (_reading[i].Same(piece, begin, length)) { _reading.RemoveAt(i); break; }
        }

        // Чтение с диска — не больше двух блоков вперёд и пока очередь отправки мала: память ограничена, скорость режет сокет.
        internal void ServeUploads()
        {
            if (Dead || Phase != BtPeerPhase.Active) return;
            BtStorage st = Torrent.Storage;
            if (st == null) return;
            while (_theirs.Count > 0 && _reading.Count < 2 && TxQueued < 2 * BtMeta.BlockSize)
            {
                BtBlockReq r = _theirs.First.Value;
                _theirs.RemoveFirst();
                _reading.Add(r);
                BtBlockReq req = r;
                _s.Disk.Read(st, r.Piece, r.Begin, r.Length, delegate(byte[] data, string why)
                {
                    _s.Reactor.Post(delegate { OnBlockRead(req, data, why); });
                });
            }
        }

        private void OnBlockRead(BtBlockReq r, byte[] data, string why)
        {
            if (!_reading.Remove(r) || Dead) return;
            if (data == null)
            {
                Torrent.OnReadError(why);
                return;
            }
            if (AmChoking && !_allowedOut.Contains(r.Piece)) return;
            Send(BtWire.PieceMsg(r.Piece, r.Begin, data, 0, r.Length), r.Length);
            ServeUploads();
        }

        protected override void OnPayloadSent(int payload)
        {
            Uploaded += payload;
            UpMeter.Add(payload);
            if (Torrent != null) Torrent.AddUploaded(payload);
        }

        protected override void OnSent()
        {
            if (_flushCloseBy > 0 && TxQueued == 0) { Close(_flushReason); return; }
            if (_theirs.Count > 0) ServeUploads();
        }

        internal void SetChoke(bool choke, long now)
        {
            if (AmChoking == choke || Phase != BtPeerPhase.Active) return;
            AmChoking = choke;
            Send(BtWire.Simple(choke ? BtWire.Choke : BtWire.Unchoke), 0);
            if (!choke)
            {
                UnchokedAtMs = now;
                return;
            }
            Optimistic = false;
            for (LinkedListNode<BtBlockReq> node = _theirs.First; node != null; )
            {
                LinkedListNode<BtBlockReq> next = node.Next;
                if (!_allowedOut.Contains(node.Value.Piece))
                {
                    if (FastExt) Send(BtWire.Block(BtWire.Reject, node.Value.Piece, node.Value.Begin, node.Value.Length), 0);
                    _theirs.Remove(node);
                }
                node = next;
            }
        }

        // Мягкое закрытие: сначала уходит то, что уже в очереди (последние блоки раздачи), но не дольше waitMs.
        internal void CloseAfterFlush(string reason, int waitMs)
        {
            if (Dead) return;
            if (TxQueued == 0 || Connecting) { Close(reason); return; }
            _flushReason = reason;
            _flushCloseBy = BtNetClock.Ms + waitMs;
            _theirs.Clear();
        }

        // ---------- расширения ----------
        private void OnExtended(int extId, int at, int count)
        {
            if (extId == 0)
            {
                BtExtHandshake h = BtExtHandshake.Parse(Rx, at, count);
                if (h == null) { Close(Tr.S("неверное расширенное рукопожатие", "a bad extension handshake")); return; }
                lock (_theirExt)
                {
                    foreach (string off in h.Disabled) _theirExt.Remove(off);
                    foreach (KeyValuePair<string, int> kv in h.M) _theirExt[kv.Key] = kv.Value;
                }
                if (h.ListenPort > 0) _listenPort = h.ListenPort;
                if (h.Client.Length > 0) _client = h.Client;
                _reqq = h.ReqQ;
                if (h.YourIp != null && !IPAddress.IsLoopback(h.YourIp) && !h.YourIp.Equals(IPAddress.Any)) _ctx.ExternalAddress = h.YourIp;
                Torrent.OnExtHandshake(this, h);
                return;
            }
            IBtExtension e = Torrent.ExtensionById(extId);
            if (e == null) return;
            try { e.OnMessage(this, Rx, at, count); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // ---------- таймеры ----------
        internal void SampleRates()
        {
            DownBps = DownMeter.Sample();
            UpBps = UpMeter.Sample();
        }

        internal void Tick(long now)
        {
            if (Dead) return;
            if (Connecting)
            {
                if (now - OpenedMs > ConnectTimeoutMs) Close(Tr.S("время ожидания соединения истекло", "the connection timed out"));
                return;
            }
            if (_flushCloseBy > 0 && now > _flushCloseBy) { Close(_flushReason); return; }
            if (Phase != BtPeerPhase.Active)
            {
                if (now - OpenedMs > HandshakeTimeoutMs)
                {
                    if (Phase == BtPeerPhase.Crypto && IsOutgoing) CryptoFailed = true;
                    Close(Tr.S("рукопожатие не завершилось вовремя", "the handshake did not finish in time"));
                }
                return;
            }
            if (now - LastRecvMs > IdleMs) { Close(Tr.S("пир молчит", "the peer is silent")); return; }
            if (now - LastSendMs > KeepAliveMs) Send(BtWire.KeepAlive(), 0);
            if (Outstanding.Count > 0 && now - Math.Max(LastPieceMs, Outstanding[0].SentMs) > SnubMs)
            {
                Snubbed = true;
                ReleaseRequests(false);
            }
        }

        protected override void OnClosed(string reason)
        {
            ClearHalfOpen();
            if (Phase == BtPeerPhase.Crypto && IsOutgoing && _hs != null) CryptoFailed = true;
            UnregisterBits();
            ReleaseRequests(false);
            _theirs.Clear();
            _reading.Clear();
            if (Torrent != null) Torrent.OnPeerClosed(this, reason);
            _s.Orphaned(this);
        }

        // ---------- снимок ----------
        internal BtPeerInfo Info()
        {
            BtPeerInfo i = new BtPeerInfo();
            int lp = _listenPort;
            i.Endpoint = IsOutgoing || lp <= 0 ? Remote : new BtEndpoint(Remote.Address, lp);
            i.Outgoing = IsOutgoing;
            i.Seed = SeedFlag;
            i.Encrypted = Encrypted;
            i.Client = _client;
            i.Origin = Origin;
            i.DownBps = DownBps;
            i.UpBps = UpBps;
            i.Downloaded = Downloaded;
            i.Uploaded = Uploaded;
            BtBitfield bits = Bits;
            i.Progress = SeedFlag ? 1 : bits == null || bits.Count == 0 ? 0 : (double)bits.SetCount / bits.Count;
            StringBuilder f = new StringBuilder();
            if (AmInterested) f.Append(PeerChoking ? 'd' : 'D');
            else if (!PeerChoking) f.Append('K');
            if (PeerInterested) f.Append(AmChoking ? 'u' : 'U');
            else if (!AmChoking) f.Append('?');
            if (Optimistic) f.Append('O');
            if (Snubbed) f.Append('S');
            if (!IsOutgoing) f.Append('I');
            if (Encrypted) f.Append('E');
            if (Origin == BtPeerOrigin.Pex) f.Append('X');
            if (Origin == BtPeerOrigin.Dht) f.Append('H');
            if (Origin == BtPeerOrigin.Lsd) f.Append('L');
            i.Flags = f.ToString();
            return i;
        }
    }
}
