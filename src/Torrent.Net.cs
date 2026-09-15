// SysDeck — «Загрузки», торренты: сетевой реактор пиров — один поток на все соединения сессии.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Сокеты неблокирующие, ожидание — Socket.Select с таймаутом 50 мс; поток на пира не заводится. Всё состояние пиров,
// выбора кусков и дросселя меняется только в этом потоке: остальные потоки (диск, пул, интерфейс) передают работу
// через Post. Чтобы Select не ждал таймаута, Post будит его датаграммой в свой UDP-сокет на 127.0.0.1 (петля не открывает
// брандмауэр). Скорость режется ведрами токенов без ожидания (TryTake): сокет, которому не хватило токенов, выпадает из
// списка Select на 50 мс — иначе поток крутился бы вхолостую.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SysDeck.Downloads
{
    internal static class BtNetClock
    {
        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        public static long Ms { get { return Clock.ElapsedMilliseconds; } }
    }

    internal static class BtNetThrottle
    {
        // Сначала ведро торрента, затем общее; недобранное общим возвращается в ведро торрента.
        public static int Take(DlTokenBucket local, DlTokenBucket global, int want)
        {
            if (want <= 0) return 0;
            int n = want;
            if (local != null)
            {
                n = local.TryTake(n);
                if (n <= 0) return 0;
            }
            if (global != null)
            {
                int g = global.TryTake(n);
                if (g < n && local != null) local.Return(n - g);
                n = g;
            }
            return n;
        }

        public static void Give(DlTokenBucket local, DlTokenBucket global, int unused)
        {
            if (unused <= 0) return;
            if (local != null) local.Return(unused);
            if (global != null) global.Return(unused);
        }
    }

    internal sealed class BtTxItem
    {
        public readonly byte[] Data;
        public readonly int Payload;         // байт данных кусков в кадре — для счётчика отданного
        public BtTxItem(byte[] data, int payload) { Data = data; Payload = payload; }
    }

    // ------------------------------------------------------------------ //
    //  Соединение: буферы, шифр потока, дроссель. Протокол — в наследнике (Torrent.Peer.cs)
    // ------------------------------------------------------------------ //
    internal abstract class BtConnection
    {
        public const int IoChunk = 64 * 1024;

        internal readonly Socket Socket;
        internal readonly BtEndpoint Remote;
        internal readonly bool IsOutgoing;
        internal bool Connecting;
        internal bool Dead;
        internal string CloseReason = "";
        internal long OpenedMs, LastRecvMs, LastSendMs;

        protected byte[] Rx = new byte[32 * 1024];
        protected int RxCount;
        protected IBtCipher TxCipher, RxCipher;

        private readonly LinkedList<BtTxItem> _tx = new LinkedList<BtTxItem>();
        private int _txOffset;
        private long _txBytes;
        private long _rxHoldUntil, _txHoldUntil;

        protected BtConnection(Socket socket, BtEndpoint remote, bool outgoing)
        {
            Socket = socket;
            Remote = remote;
            IsOutgoing = outgoing;
            OpenedMs = LastRecvMs = LastSendMs = BtNetClock.Ms;
        }

        internal long TxQueued { get { return _txBytes; } }

        protected abstract DlTokenBucket LocalDown { get; }
        protected abstract DlTokenBucket LocalUp { get; }
        protected abstract DlTokenBucket GlobalDown { get; }
        protected abstract DlTokenBucket GlobalUp { get; }

        protected abstract void OnReceived();
        protected abstract void OnPayloadSent(int payload);
        protected abstract void OnSent();
        internal abstract void OnConnected();
        protected abstract void OnClosed(string reason);

        internal bool WantRead(long now) { return !Dead && !Connecting && now >= _rxHoldUntil && RxCount < Rx.Length; }
        internal bool WantWrite(long now) { return !Dead && !Connecting && _tx.Count > 0 && now >= _txHoldUntil; }

        // Кадр протокола: после рукопожатия шифрования идёт через шифр (копия — кадр может быть общим для многих пиров).
        protected void Send(byte[] frame, int payload)
        {
            if (Dead || frame == null || frame.Length == 0) return;
            byte[] data = frame;
            if (TxCipher != null)
            {
                data = (byte[])frame.Clone();
                TxCipher.Apply(data, 0, data.Length);
            }
            _tx.AddLast(new BtTxItem(data, payload));
            _txBytes += data.Length;
        }

        // Байты рукопожатия шифрования — как есть.
        protected void SendRaw(byte[] data)
        {
            if (Dead || data == null || data.Length == 0) return;
            _tx.AddLast(new BtTxItem(data, 0));
            _txBytes += data.Length;
        }

        protected void EnsureRx(int need)
        {
            if (need <= Rx.Length) return;
            byte[] b = new byte[Math.Min(Math.Max(need, Rx.Length * 2), BtWire.MaxMessage + 4)];
            Buffer.BlockCopy(Rx, 0, b, 0, RxCount);
            Rx = b;
        }

        internal void DoRead(long now)
        {
            int want = Math.Min(IoChunk, Rx.Length - RxCount);
            int allowed = BtNetThrottle.Take(LocalDown, GlobalDown, want);
            if (allowed <= 0)
            {
                _rxHoldUntil = now + 50;
                return;
            }
            int n;
            SocketError err;
            try { n = Socket.Receive(Rx, RxCount, allowed, SocketFlags.None, out err); }
            catch (ObjectDisposedException) { n = 0; err = SocketError.Shutdown; }
            BtNetThrottle.Give(LocalDown, GlobalDown, allowed - Math.Max(0, n));
            if (err == SocketError.WouldBlock) return;
            if (err != SocketError.Success) { Close(Tr.S("ошибка сети: ", "network error: ") + err); return; }
            if (n <= 0) { Close(Tr.S("пир закрыл соединение", "the peer closed the connection")); return; }
            if (RxCipher != null) RxCipher.Apply(Rx, RxCount, n);
            RxCount += n;
            LastRecvMs = now;
            OnReceived();
        }

        internal void DoWrite(long now)
        {
            if (Dead || _tx.Count == 0 || now < _txHoldUntil) return;
            int allowed = BtNetThrottle.Take(LocalUp, GlobalUp, (int)Math.Min(IoChunk, _txBytes));
            if (allowed <= 0)
            {
                _txHoldUntil = now + 50;
                return;
            }
            int sent = 0;
            while (_tx.Count > 0 && sent < allowed)
            {
                BtTxItem item = _tx.First.Value;
                int chunk = Math.Min(item.Data.Length - _txOffset, allowed - sent);
                int n;
                SocketError err;
                try { n = Socket.Send(item.Data, _txOffset, chunk, SocketFlags.None, out err); }
                catch (ObjectDisposedException) { n = 0; err = SocketError.Shutdown; }
                if (err == SocketError.WouldBlock) break;
                if (err != SocketError.Success)
                {
                    BtNetThrottle.Give(LocalUp, GlobalUp, allowed - sent);
                    Close(Tr.S("ошибка сети: ", "network error: ") + err);
                    return;
                }
                if (n <= 0) break;
                sent += n;
                _txOffset += n;
                _txBytes -= n;
                if (_txOffset == item.Data.Length)
                {
                    _tx.RemoveFirst();
                    _txOffset = 0;
                    if (item.Payload > 0) OnPayloadSent(item.Payload);
                }
                if (n < chunk) break;
            }
            BtNetThrottle.Give(LocalUp, GlobalUp, allowed - sent);
            if (sent > 0)
            {
                LastSendMs = now;
                OnSent();
            }
        }

        // Только из потока реактора. Повторный вызов ничего не делает.
        internal void Close(string reason)
        {
            if (Dead) return;
            Dead = true;
            CloseReason = reason ?? "";
            try { Socket.Close(); } catch { }
            _tx.Clear();
            _txBytes = 0;
            try { OnClosed(CloseReason); }
            catch (Exception ex) { DlLog.Report(ex); }
        }
    }

    // ------------------------------------------------------------------ //
    //  Реактор
    // ------------------------------------------------------------------ //
    internal sealed class BtReactor : IDisposable
    {
        private readonly Thread _thread;
        private readonly Socket _wake;
        private readonly EndPoint _wakeTo;
        private readonly Queue<Action> _posted = new Queue<Action>();
        private readonly object _gate = new object();
        private readonly List<BtConnection> _conns = new List<BtConnection>();
        private readonly Dictionary<Socket, BtConnection> _bySocket = new Dictionary<Socket, BtConnection>();
        private Socket _listener;
        private volatile bool _stop;
        private int _wakeSent;
        private long _lastTick;

        public Action<Socket> Accepted;      // поток реактора; сокет уже неблокирующий
        public Action<long> Tick;            // около 10 раз в секунду

        public BtReactor(string name)
        {
            _wake = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _wake.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            _wakeTo = _wake.LocalEndPoint;
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = name ?? "wpc-bt-net";
        }

        public void Start() { _thread.Start(); }

        public bool OnThread { get { return Thread.CurrentThread == _thread; } }

        public int Count { get { return _conns.Count; } }

        // Работа в потоке реактора; из самого реактора — тоже в очередь (выполнится на этом же проходе цикла).
        public void Post(Action action)
        {
            if (action == null || _stop) return;
            lock (_gate) _posted.Enqueue(action);
            if (!OnThread && Interlocked.Exchange(ref _wakeSent, 1) == 0)
            {
                try { _wake.SendTo(new byte[1], _wakeTo); }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }

        public void Add(BtConnection c)
        {
            _conns.Add(c);
            _bySocket[c.Socket] = c;
        }

        // Слушатель TCP (null — закрыть). Только из потока реактора или до Start.
        public void SetListener(Socket listener)
        {
            if (_listener != null && _listener != listener)
                try { _listener.Close(); } catch { }
            _listener = listener;
        }

        public bool HasListener { get { return _listener != null; } }

        // Неблокирующее соединение; connected — установилось сразу. null — отказ до попытки (error).
        public static Socket BeginConnect(BtEndpoint ep, out bool connected, out string error)
        {
            connected = false;
            error = null;
            Socket s = null;
            try
            {
                s = new Socket(ep.IsV6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.Blocking = false;
                s.NoDelay = true;
                s.Connect(new IPEndPoint(ep.Address, ep.Port));
                connected = true;
                return s;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.WouldBlock || ex.SocketErrorCode == SocketError.InProgress) return s;
                error = ex.SocketErrorCode.ToString();
            }
            catch (Exception ex) { error = ex.Message; }
            if (s != null) try { s.Close(); } catch { }
            return null;
        }

        private void Loop()
        {
            List<Socket> r = new List<Socket>(), w = new List<Socket>(), e = new List<Socket>();
            byte[] drain = new byte[16];
            while (!_stop)
            {
                long now = BtNetClock.Ms;
                r.Clear();
                w.Clear();
                e.Clear();
                r.Add(_wake);
                if (_listener != null) r.Add(_listener);
                foreach (BtConnection c in _conns)
                {
                    if (c.Dead) continue;
                    if (c.Connecting) { w.Add(c.Socket); e.Add(c.Socket); continue; }
                    if (c.WantRead(now)) r.Add(c.Socket);
                    if (c.WantWrite(now)) w.Add(c.Socket);
                }
                try { Socket.Select(r, w, e, 50000); }
                catch (SocketException) { r.Clear(); w.Clear(); e.Clear(); }
                catch (ObjectDisposedException) { r.Clear(); w.Clear(); e.Clear(); }
                if (_stop) break;
                now = BtNetClock.Ms;

                foreach (Socket s in r)
                {
                    if (s == _wake)
                    {
                        Interlocked.Exchange(ref _wakeSent, 0);
                        try { while (_wake.Available > 0) _wake.Receive(drain); }
                        catch (SocketException) { }
                        continue;
                    }
                    if (s == _listener) { AcceptAll(); continue; }
                    BtConnection c;
                    if (!_bySocket.TryGetValue(s, out c) || c.Dead) continue;
                    try { c.DoRead(now); }
                    catch (Exception ex) { Fault(c, ex); }
                }
                foreach (Socket s in e)
                {
                    BtConnection c;
                    if (_bySocket.TryGetValue(s, out c) && !c.Dead && c.Connecting) Fail(c);
                }
                foreach (Socket s in w)
                {
                    BtConnection c;
                    if (!_bySocket.TryGetValue(s, out c) || c.Dead) continue;
                    if (c.Connecting)
                    {
                        int soError = 0;
                        try { soError = (int)s.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error); }
                        catch (SocketException) { soError = -1; }
                        if (soError != 0) { Fail(c); continue; }
                        c.Connecting = false;
                        c.LastRecvMs = c.LastSendMs = now;
                        try { c.OnConnected(); }
                        catch (Exception ex) { Fault(c, ex); }
                        continue;
                    }
                    try { c.DoWrite(now); }
                    catch (Exception ex) { Fault(c, ex); }
                }
                RunPosted();
                if (now - _lastTick >= 100)
                {
                    _lastTick = now;
                    if (Tick != null)
                        try { Tick(now); }
                        catch (Exception ex) { DlLog.Report(ex); }
                }
                // Ответы, накопленные при разборе, уходят в этом же проходе, не дожидаясь следующего Select.
                for (int i = 0; i < _conns.Count; i++)
                {
                    BtConnection c = _conns[i];
                    if (c.Dead || c.Connecting || c.TxQueued == 0) continue;
                    try { c.DoWrite(now); }
                    catch (Exception ex) { Fault(c, ex); }
                }
                Reap();
            }
        }

        private void RunPosted()
        {
            while (true)
            {
                Action a;
                lock (_gate)
                {
                    if (_posted.Count == 0) return;
                    a = _posted.Dequeue();
                }
                try { a(); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private static void Fail(BtConnection c)
        {
            c.Close(Tr.S("не удалось соединиться", "could not connect"));
        }

        private static void Fault(BtConnection c, Exception ex)
        {
            DlLog.Report(ex);
            c.Close(Tr.S("ошибка протокола: ", "protocol error: ") + ex.Message);
        }

        private void Reap()
        {
            for (int i = _conns.Count - 1; i >= 0; i--)
            {
                if (!_conns[i].Dead) continue;
                _bySocket.Remove(_conns[i].Socket);
                _conns.RemoveAt(i);
            }
        }

        private void AcceptAll()
        {
            for (int i = 0; i < 32; i++)
            {
                Socket s;
                try { s = _listener.Accept(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                try
                {
                    s.Blocking = false;
                    s.NoDelay = true;
                }
                catch (Exception)
                {
                    try { s.Close(); } catch { }
                    continue;
                }
                if (Accepted != null) Accepted(s);
                else try { s.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _wake.SendTo(new byte[1], _wakeTo); } catch { }
            if (_thread.IsAlive && !OnThread) _thread.Join(5000);
            RunPosted();
            foreach (BtConnection c in new List<BtConnection>(_conns)) c.Close(Tr.S("сессия закрыта", "the session is closed"));
            _conns.Clear();
            _bySocket.Clear();
            if (_listener != null) try { _listener.Close(); } catch { }
            _listener = null;
            try { _wake.Close(); } catch { }
        }
    }
}
