// SysDeck — «Загрузки», торренты: UDP-сокет сессии (DHT, UDP-трекеры) и поток приёма.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Один сокет на порт сессии: ответы трекеров BEP 15 и сообщения DHT различаются по содержимому, и каждый обработчик сам
// решает, его ли датаграмма. Приём — отдельный поток; обработчики обязаны быстро вернуть управление.
// Windows по ICMP «порт недоступен» роняет следующий ReceiveFrom с WSAECONNRESET — SIO_UDP_CONNRESET это выключает.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed class BtUdp : IBtUdp, IDisposable
    {
        private const int SioUdpConnReset = -1744830452;

        private readonly Socket _socket;
        private readonly Thread _thread;
        private IBtUdpHandler[] _handlers = new IBtUdpHandler[0];
        private readonly object _gate = new object();
        private volatile bool _closed;
        private long _received, _sent;

        // bind — адрес (IPAddress.Any или 127.0.0.1 в тестах); port 0 — любой свободный. Занятый порт — исключение у вызывающего.
        public BtUdp(IPAddress bind, int port)
        {
            _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                _socket.IOControl(SioUdpConnReset, new byte[4], null);
                _socket.ReceiveBufferSize = 1024 * 1024;
                _socket.SendBufferSize = 512 * 1024;
                _socket.Bind(new IPEndPoint(bind, port));
            }
            catch
            {
                _socket.Close();
                throw;
            }
            _thread = new Thread(Loop);
            _thread.IsBackground = true;
            _thread.Name = "wpc-bt-udp";
            _thread.Start();
        }

        public int Port { get { return ((IPEndPoint)_socket.LocalEndPoint).Port; } }
        public long Received { get { return Interlocked.Read(ref _received); } }
        public long Sent { get { return Interlocked.Read(ref _sent); } }

        public void AddHandler(IBtUdpHandler handler)
        {
            lock (_gate)
            {
                List<IBtUdpHandler> list = new List<IBtUdpHandler>(_handlers);
                if (!list.Contains(handler)) list.Add(handler);
                _handlers = list.ToArray();
            }
        }

        public void RemoveHandler(IBtUdpHandler handler)
        {
            lock (_gate)
            {
                List<IBtUdpHandler> list = new List<IBtUdpHandler>(_handlers);
                list.Remove(handler);
                _handlers = list.ToArray();
            }
        }

        public void Send(BtEndpoint to, byte[] data, int count)
        {
            if (_closed || to == null || to.IsV6 || data == null || count <= 0) return;
            try
            {
                _socket.SendTo(data, 0, count, SocketFlags.None, new IPEndPoint(to.Address, to.Port));
                Interlocked.Increment(ref _sent);
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
        }

        private void Loop()
        {
            byte[] buffer = new byte[65536];
            while (!_closed)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try { n = _socket.ReceiveFrom(buffer, ref from); }
                catch (SocketException) { if (_closed) return; continue; }
                catch (ObjectDisposedException) { return; }
                if (n <= 0) continue;
                Interlocked.Increment(ref _received);
                IPEndPoint ip = (IPEndPoint)from;
                BtEndpoint ep = new BtEndpoint(ip.Address, ip.Port);
                // Обработчик получает свою копию: буфер приёма переиспользуется.
                byte[] copy = new byte[n];
                Buffer.BlockCopy(buffer, 0, copy, 0, n);
                foreach (IBtUdpHandler h in _handlers)
                {
                    try { if (h.HandleDatagram(ep, copy, n)) break; }
                    catch (Exception ex) { DlLog.Report(ex); }
                }
            }
        }

        public void Dispose()
        {
            _closed = true;
            try { _socket.Close(); } catch { }
            if (Thread.CurrentThread != _thread) _thread.Join(2000);
        }
    }
}
