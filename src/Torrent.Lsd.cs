// Windows Process Cleaner — «Загрузки», торренты: поиск пиров в локальной сети (BEP 14, Local Service Discovery).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Свой UDP-сокет на 6771 (SO_REUSEADDR: порт делят с другими клиентами на этой машине), группа 239.192.152.143, только
// IPv4. Свои же объявления возвращаются петлёй группы — отличаем по cookie. Не чаще раза в 5 минут на торрент, частные
// торренты — никогда. Принимаем объявления только с локальных адресов: пир «из интернета» через LSD — подделка.
// Тестовый конструктор привязывает сокет к 127.0.0.1 и шлёт на явные адреса вместо группы — в сеть ничего не уходит.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class BtLsd : IBtLsd
    {
        public static readonly IPAddress GroupAddress = IPAddress.Parse("239.192.152.143");
        public const int GroupPort = 6771;
        internal const int MinAnnounceSeconds = 300;
        private const int MaxDatagram = 1400;
        private const int MaxHashesPerMessage = 16;
        private const int SioUdpConnReset = -1744830452;

        private readonly BtContext _ctx;
        private readonly IPAddress _bind;
        private readonly int _port;
        private readonly bool _multicast;
        private readonly string _cookie;
        private readonly object _gate = new object();
        private readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly List<IPEndPoint> _targets = new List<IPEndPoint>();
        private Socket _socket;
        private Thread _thread;
        private volatile bool _closed;
        private long _sent, _received, _accepted;

        public BtLsd(BtContext ctx) : this(ctx, IPAddress.Any, GroupPort, true)
        {
            _targets.Add(new IPEndPoint(GroupAddress, GroupPort));
        }

        // Тесты: bind 127.0.0.1, port 0, multicast false, адресаты — через AddTarget.
        internal BtLsd(BtContext ctx, IPAddress bind, int port, bool multicast)
        {
            _ctx = ctx;
            _bind = bind;
            _port = port;
            _multicast = multicast;
            byte[] c = new byte[8];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(c);
            _cookie = Bencode.Hex(c);
        }

        internal void AddTarget(IPEndPoint to) { lock (_gate) _targets.Add(to); }
        internal int LocalPort { get { Socket s = _socket; try { return s == null ? 0 : ((IPEndPoint)s.LocalEndPoint).Port; } catch (ObjectDisposedException) { return 0; } } }
        internal long Sent { get { return Interlocked.Read(ref _sent); } }
        internal long Received { get { return Interlocked.Read(ref _received); } }
        internal long Accepted { get { return Interlocked.Read(ref _accepted); } }

        public void Start()
        {
            lock (_gate)
            {
                if (_socket != null || _closed) return;
                Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    s.IOControl(SioUdpConnReset, new byte[4], null);
                    s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    s.Bind(new IPEndPoint(_bind, _port));
                    if (_multicast)
                    {
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                        s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                        JoinGroup(s);
                    }
                }
                catch (Exception ex)
                {
                    s.Close();
                    _ctx.Log("bt lsd: disabled, " + ex.GetType().Name);
                    return;
                }
                _socket = s;
                _thread = new Thread(Loop);
                _thread.IsBackground = true;
                _thread.Name = "wpc-bt-lsd";
                _thread.Start();
            }
        }

        // Группа — на каждом рабочем IPv4-интерфейсе: иначе на машине с VPN слушали бы только интерфейс по умолчанию.
        private static void JoinGroup(Socket s)
        {
            int joined = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || !ni.SupportsMulticast || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (a.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        try
                        {
                            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupAddress, a.Address));
                            joined++;
                        }
                        catch (SocketException) { }
                    }
                }
            }
            catch (NetworkInformationException) { }
            if (joined == 0) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(GroupAddress, IPAddress.Any));
        }

        public void Announce(IBtSwarm swarm)
        {
            if (swarm == null || swarm.IsPrivate || _closed) return;
            byte[] hash = swarm.InfoHash;
            if (hash == null || hash.Length != 20) return;
            string hex = Bencode.Hex(hash);
            IPEndPoint[] targets;
            Socket s;
            lock (_gate)
            {
                s = _socket;
                if (s == null) return;
                DateTime now = DateTime.UtcNow;
                DateTime last;
                if (_last.TryGetValue(hex, out last) && (now - last).TotalSeconds < MinAnnounceSeconds) return;
                _last[hex] = now;
                if (_last.Count > 1000)
                {
                    List<string> old = new List<string>();
                    foreach (KeyValuePair<string, DateTime> kv in _last) if ((now - kv.Value).TotalSeconds >= MinAnnounceSeconds) old.Add(kv.Key);
                    foreach (string k in old) _last.Remove(k);
                }
                targets = _targets.ToArray();
            }
            string text = "BT-SEARCH * HTTP/1.1\r\n"
                          + "Host: " + GroupAddress + ":" + GroupPort.ToString(CultureInfo.InvariantCulture) + "\r\n"
                          + "Port: " + _ctx.Port.ToString(CultureInfo.InvariantCulture) + "\r\n"
                          + "Infohash: " + hex + "\r\n"
                          + "cookie: " + _cookie + "\r\n"
                          + "\r\n\r\n";
            byte[] data = Encoding.ASCII.GetBytes(text);
            foreach (IPEndPoint to in targets)
            {
                try
                {
                    s.SendTo(data, to);
                    Interlocked.Increment(ref _sent);
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { return; }
            }
        }

        private void Loop()
        {
            byte[] buffer = new byte[MaxDatagram + 1];
            Socket s = _socket;
            while (!_closed)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = s.ReceiveFrom(buffer, ref from); }
                catch (SocketException) { if (_closed) return; continue; }   // в том числе датаграмма длиннее буфера
                catch (ObjectDisposedException) { return; }
                if (n <= 0 || n > MaxDatagram) continue;
                Interlocked.Increment(ref _received);
                try { Handle(((IPEndPoint)from).Address, buffer, n); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private void Handle(IPAddress from, byte[] data, int count)
        {
            if (!IsLocal(from)) return;
            string text = Encoding.ASCII.GetString(data, 0, count);
            string[] lines = text.Replace("\r\n", "\n").Split('\n');
            if (lines.Length < 2 || !string.Equals(lines[0].Trim(), "BT-SEARCH * HTTP/1.1", StringComparison.OrdinalIgnoreCase)) return;
            int port = 0;
            string cookie = null;
            List<byte[]> hashes = new List<byte[]>();
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (name.Equals("Port", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port)) port = 0;
                }
                else if (name.Equals("cookie", StringComparison.OrdinalIgnoreCase)) cookie = value;
                else if (name.Equals("Infohash", StringComparison.OrdinalIgnoreCase) && hashes.Count < MaxHashesPerMessage && value.Length == 40)
                {
                    byte[] h = Bencode.FromHex(value);
                    if (h != null) hashes.Add(h);
                }
            }
            if (cookie != null && cookie == _cookie) return;   // своё объявление, вернувшееся петлёй группы
            BtEndpoint ep = new BtEndpoint(from, port);
            if (!ep.IsUsable || hashes.Count == 0) return;
            foreach (byte[] h in hashes)
            {
                IBtSwarm swarm = _ctx.FindSwarm(h);
                if (swarm == null || swarm.IsPrivate) continue;
                swarm.AddPeers(new List<BtEndpoint> { ep }, BtPeerOrigin.Lsd);
                Interlocked.Increment(ref _accepted);
            }
        }

        // Петля, частные сети, link-local и CGNAT: всё, что может быть «той же сетью».
        internal static bool IsLocal(IPAddress a)
        {
            if (a == null || a.AddressFamily != AddressFamily.InterNetwork) return false;
            byte[] b = a.GetAddressBytes();
            return b[0] == 127 || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
        }

        public void Dispose()
        {
            Socket s;
            Thread t;
            lock (_gate)
            {
                _closed = true;
                s = _socket;
                t = _thread;
            }
            if (s != null) { try { s.Close(); } catch { } }
            if (t != null && Thread.CurrentThread != t) t.Join(2000);
        }
    }
}
