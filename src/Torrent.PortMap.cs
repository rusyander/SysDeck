// SysDeck — «Загрузки», торренты: проброс порта на роутере (UPnP IGD, NAT-PMP).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Вся сеть — в своём фоновом потоке: SSDP M-SEARCH, описание устройства, SOAP AddPortMapping (TCP и UDP, аренда 3600 с,
// продление на половине срока), GetExternalIPAddress; роутер без UPnP — NAT-PMP (RFC 6886) к шлюзу по умолчанию.
// Stop и Dispose снимают проброс (Dispose ждёт это ограниченное время: при выходе из приложения порт не должен остаться
// открытым). Адрес описания из ответа SSDP принимается только с того же хоста, что ответил: иначе любой в сети направил
// бы наши HTTP-запросы куда угодно. Прокси не используется — роутер в локальной сети. XML — без DTD, не больше 256 КБ.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security;
using System.Text;
using System.Threading;
using System.Xml;

namespace SysDeck.Downloads
{
    internal sealed class BtPortMapper : IBtPortMapper
    {
        public const string Description = "SysDeck";
        private const int MaxXml = 256 * 1024;
        private static readonly string[] ServiceTypes =
        {
            "urn:schemas-upnp-org:service:WANIPConnection:2",
            "urn:schemas-upnp-org:service:WANIPConnection:1",
            "urn:schemas-upnp-org:service:WANPPPConnection:1"
        };

        // Времена — поля экземпляра: тесты сокращают их до Start.
        internal int LeaseSeconds = 3600;
        internal int HttpTimeoutMs = 5000;
        internal int SsdpWaitMs = 3000;
        internal int NatPmpFirstTimeoutMs = 250;
        internal int NatPmpTries = 4;
        internal int RetrySeconds = 300;

        private readonly BtEndpoint _ssdpTarget;       // null — SSDP не используется
        private readonly string _igdUrl;               // явный адрес описания IGD (тесты)
        private readonly BtEndpoint _natPmp;           // null и _autoGateway — шлюз по умолчанию
        private readonly bool _autoGateway;

        // Свой флаг остановки у каждого запуска потока: Start после Stop не должен «воскресить» ещё не закончивший поток.
        private sealed class RunState
        {
            public volatile bool Stop, PortsChanged;
            public readonly AutoResetEvent Wake = new AutoResetEvent(false);
        }

        [ThreadStatic] private static RunState _threadRun;

        private readonly object _gate = new object();
        private Thread _thread;
        private RunState _run;
        private bool _disposed;
        private volatile int _tcpPort, _udpPort;
        private bool _mapped;
        private IPAddress _external;
        private string _status = "";

        // Текущий проброс: 1 — UPnP, 2 — NAT-PMP.
        private int _method;
        private string _controlUrl, _serviceType, _router;
        private BtEndpoint _gateway;
        private int _mappedTcp, _mappedUdp;

        public BtPortMapper() : this(new BtEndpoint(IPAddress.Parse("239.255.255.250"), 1900), null, null)
        {
            _autoGateway = true;
        }

        // Тесты: SSDP на петлевой адрес, явный адрес описания, NAT-PMP-ответчик на 127.0.0.1. null — этого пути нет.
        internal BtPortMapper(BtEndpoint ssdpTarget, string igdDescriptionUrl, BtEndpoint natPmpGateway)
        {
            _ssdpTarget = ssdpTarget;
            _igdUrl = igdDescriptionUrl;
            _natPmp = natPmpGateway;
            _status = Tr.S("Проброс порта: выключен", "Port mapping: off");
        }

        public bool Mapped { get { lock (_gate) return _mapped; } }
        public IPAddress ExternalAddress { get { lock (_gate) return _external; } }
        public string Status { get { lock (_gate) return _status; } }

        public void Start(int tcpPort, int udpPort)
        {
            if (tcpPort <= 0 || tcpPort > 65535 || udpPort <= 0 || udpPort > 65535) throw new ArgumentOutOfRangeException("tcpPort");
            lock (_gate)
            {
                if (_disposed) return;
                if (_run != null && !_run.Stop)
                {
                    if (_tcpPort == tcpPort && _udpPort == udpPort) return;
                    _tcpPort = tcpPort;
                    _udpPort = udpPort;
                    _run.PortsChanged = true;
                    _run.Wake.Set();
                    return;
                }
                _tcpPort = tcpPort;
                _udpPort = udpPort;
                Thread previous = _thread;
                RunState rs = new RunState();
                _run = rs;
                _thread = new Thread(delegate() { if (previous != null) previous.Join(); _threadRun = rs; Run(); });
                _thread.IsBackground = true;
                _thread.Name = "wpc-bt-portmap";
                _thread.Start();
            }
        }

        public void Stop()
        {
            lock (_gate)
            {
                if (_run == null) return;
                _run.Stop = true;
                _run.Wake.Set();
            }
        }

        private static bool Stopping { get { RunState rs = _threadRun; return rs == null || rs.Stop; } }

        public void Dispose()
        {
            Thread t;
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                t = _thread;
            }
            Stop();
            if (t != null && t != Thread.CurrentThread) t.Join(HttpTimeoutMs * 2 + NatPmpFirstTimeoutMs * 6 + 2000);
        }

        // ------------------------------------------------------------------ //
        //  Фоновый поток
        // ------------------------------------------------------------------ //
        private void Run()
        {
            try
            {
                DateTime renewAt = DateTime.MaxValue;
                while (!Stopping)
                {
                    if (_threadRun.PortsChanged)
                    {
                        _threadRun.PortsChanged = false;
                        Unmap();
                    }
                    if (_method == 0)
                    {
                        if (TryMap(out renewAt)) continue;
                        Sleep(RetrySeconds * 1000);
                        continue;
                    }
                    int wait = (int)Math.Max(0, Math.Min(int.MaxValue, (renewAt - DateTime.UtcNow).TotalMilliseconds));
                    if (wait > 0)
                    {
                        Sleep(wait);
                        continue;
                    }
                    // Продление — тот же запрос проброса; не вышло — роутер перезагрузился или сменился: ищем заново.
                    if (!Renew(out renewAt))
                    {
                        lock (_gate) _mapped = false;
                        _method = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
            }
            finally
            {
                try { Unmap(); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private static void Sleep(int ms)
        {
            RunState rs = _threadRun;
            if (rs.Stop || rs.PortsChanged) return;
            rs.Wake.WaitOne(ms);
        }

        private bool TryMap(out DateTime renewAt)
        {
            renewAt = DateTime.MaxValue;
            SetStatus(Tr.S("Проброс порта: поиск роутера…", "Port mapping: looking for the router…"), false, null);
            string error = null;
            if (TryUpnp(ref error, out renewAt)) return true;
            if (Stopping) return false;
            if (TryNatPmp(ref error, out renewAt)) return true;
            SetStatus(error ?? Tr.S("Проброс порта: роутер с UPnP или NAT-PMP не найден", "Port mapping: no UPnP or NAT-PMP router found"), false, null);
            return false;
        }

        private bool Renew(out DateTime renewAt)
        {
            string error = null;
            if (_method == 1) return MapUpnp(ref error, out renewAt);
            return MapNatPmp(ref error, out renewAt);
        }

        private void Unmap()
        {
            int method = _method;
            _method = 0;
            if (method == 1)
            {
                string body;
                int code;
                Soap(_controlUrl, _serviceType, "DeletePortMapping", DeleteArgs(_mappedTcp, "TCP"), out body, out code);
                Soap(_controlUrl, _serviceType, "DeletePortMapping", DeleteArgs(_mappedUdp, "UDP"), out body, out code);
            }
            else if (method == 2)
            {
                PmpMap(_gateway, 2, _mappedTcp, 0, 0, 2);
                PmpMap(_gateway, 1, _mappedUdp, 0, 0, 2);
            }
            if (method != 0) SetStatus(Tr.S("Проброс порта снят", "Port mapping removed"), false, null);
        }

        private void SetStatus(string text, bool mapped, IPAddress external)
        {
            lock (_gate)
            {
                _status = text;
                _mapped = mapped;
                if (external != null) _external = external;
            }
        }

        // ------------------------------------------------------------------ //
        //  UPnP IGD
        // ------------------------------------------------------------------ //
        private bool TryUpnp(ref string error, out DateTime renewAt)
        {
            renewAt = DateTime.MaxValue;
            List<string> locations = new List<string>();
            if (_igdUrl != null) locations.Add(_igdUrl);
            else if (_ssdpTarget != null) locations.AddRange(SsdpSearch());
            foreach (string location in locations)
            {
                if (Stopping) return false;
                string xml = Http(location, null, null, out error);
                if (xml == null) continue;
                string control, service;
                if (!FindService(xml, location, out control, out service))
                {
                    error = Tr.S("UPnP: у устройства нет службы WANIPConnection", "UPnP: the device has no WANIPConnection service");
                    continue;
                }
                _controlUrl = control;
                _serviceType = service;
                _router = new Uri(control).Host;
                if (MapUpnp(ref error, out renewAt)) return true;
            }
            return false;
        }

        private bool MapUpnp(ref string error, out DateTime renewAt)
        {
            renewAt = DateTime.MaxValue;
            string local = LocalAddressTo(new Uri(_controlUrl));
            if (local == null) return false;
            string body;
            int code;
            IPAddress external = null;
            if (Soap(_controlUrl, _serviceType, "GetExternalIPAddress", "", out body, out code))
            {
                IPAddress ip;
                if (IPAddress.TryParse(XmlValue(body, "NewExternalIPAddress") ?? "", out ip) && !ip.Equals(IPAddress.Any)) external = ip;
            }
            int lease = LeaseSeconds;
            foreach (string proto in new[] { "TCP", "UDP" })
            {
                int port = proto == "TCP" ? _tcpPort : _udpPort;
                bool ok = Soap(_controlUrl, _serviceType, "AddPortMapping", AddArgs(port, proto, local, lease), out body, out code);
                // 725 OnlyPermanentLeasesSupported: старые роутеры принимают только бессрочный проброс.
                if (!ok && code == 725 && lease != 0)
                {
                    lease = 0;
                    ok = Soap(_controlUrl, _serviceType, "AddPortMapping", AddArgs(port, proto, local, 0), out body, out code);
                }
                if (!ok)
                {
                    error = code == 718
                        ? Tr.S("UPnP: порт " + Num(port) + " на роутере занят другим устройством", "UPnP: port " + Num(port) + " is taken on the router by another device")
                        : Tr.S("UPnP: роутер отказал в пробросе", "UPnP: the router refused the mapping") + (code > 0 ? " (" + Num(code) + ")" : "");
                    if (proto == "UDP") Soap(_controlUrl, _serviceType, "DeletePortMapping", DeleteArgs(_tcpPort, "TCP"), out body, out code);
                    return false;
                }
            }
            _method = 1;
            _mappedTcp = _tcpPort;
            _mappedUdp = _udpPort;
            renewAt = DateTime.UtcNow.AddSeconds(lease > 0 ? Math.Max(1, lease / 2) : 1800);
            SetStatus(Tr.S("UPnP: порт " + Num(_tcpPort) + " открыт на " + _router, "UPnP: port " + Num(_tcpPort) + " is open on " + _router), true, external);
            return true;
        }

        private static string AddArgs(int port, string proto, string local, int lease)
        {
            return "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + Num(port) + "</NewExternalPort><NewProtocol>" + proto +
                   "</NewProtocol><NewInternalPort>" + Num(port) + "</NewInternalPort><NewInternalClient>" + SecurityElement.Escape(local) +
                   "</NewInternalClient><NewEnabled>1</NewEnabled><NewPortMappingDescription>" + SecurityElement.Escape(Description) +
                   "</NewPortMappingDescription><NewLeaseDuration>" + Num(lease) + "</NewLeaseDuration>";
        }

        private static string DeleteArgs(int port, string proto)
        {
            return "<NewRemoteHost></NewRemoteHost><NewExternalPort>" + Num(port) + "</NewExternalPort><NewProtocol>" + proto + "</NewProtocol>";
        }

        private List<string> SsdpSearch()
        {
            List<string> found = new List<string>();
            IPAddress local = LocalAddressTo(_ssdpTarget);
            if (local == null) return found;
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Bind(new IPEndPoint(local, 0));
                if (_ssdpTarget.Address.GetAddressBytes()[0] >= 224) s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 2);
                EndPoint to = new IPEndPoint(_ssdpTarget.Address, _ssdpTarget.Port);
                foreach (string st in new[] { "urn:schemas-upnp-org:device:InternetGatewayDevice:1", "urn:schemas-upnp-org:service:WANIPConnection:1", "urn:schemas-upnp-org:service:WANPPPConnection:1" })
                {
                    byte[] msg = Encoding.ASCII.GetBytes("M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nST: " + st + "\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\n\r\n");
                    try { s.SendTo(msg, to); }
                    catch (SocketException) { return found; }
                }
                byte[] buf = new byte[8192];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(SsdpWaitMs);
                while (!Stopping && DateTime.UtcNow < deadline)
                {
                    bool readable;
                    try { readable = s.Poll(100 * 1000, SelectMode.SelectRead); }
                    catch (SocketException) { break; }
                    if (!readable) continue;
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try { n = s.ReceiveFrom(buf, ref from); }
                    catch (SocketException) { continue; }
                    string location = HeaderValue(Encoding.ASCII.GetString(buf, 0, n), "LOCATION");
                    Uri u;
                    if (location == null || !Uri.TryCreate(location, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttp) continue;
                    IPAddress host;
                    if (!IPAddress.TryParse(u.Host, out host) || !host.Equals(((IPEndPoint)from).Address)) continue;
                    if (!found.Contains(location)) found.Add(location);
                    // Первый ответ есть — ещё полсекунды на остальные устройства, не весь срок.
                    DateTime soon = DateTime.UtcNow.AddMilliseconds(500);
                    if (soon < deadline) deadline = soon;
                }
            }
            return found;
        }

        private static string HeaderValue(string response, string name)
        {
            foreach (string line in response.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                int colon = line.IndexOf(':');
                if (colon > 0 && string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(colon + 1).Trim();
            }
            return null;
        }

        // Служба WANIPConnection/WANPPPConnection; адрес управления — на том же хосте, что и описание.
        private static bool FindService(string xml, string location, out string control, out string service)
        {
            control = service = null;
            XmlDocument doc = LoadXml(xml);
            if (doc == null) return false;
            Uri baseUri = new Uri(location);
            string urlBase = null;
            foreach (XmlNode n in doc.GetElementsByTagName("*"))
                if (n.LocalName == "URLBase") { urlBase = n.InnerText.Trim(); break; }
            Uri b;
            if (!string.IsNullOrEmpty(urlBase) && Uri.TryCreate(urlBase, UriKind.Absolute, out b)) baseUri = b;
            int best = int.MaxValue;
            foreach (XmlNode n in doc.GetElementsByTagName("*"))
            {
                if (n.LocalName != "service") continue;
                string type = null, url = null;
                foreach (XmlNode c in n.ChildNodes)
                {
                    if (c.LocalName == "serviceType") type = c.InnerText.Trim();
                    else if (c.LocalName == "controlURL") url = c.InnerText.Trim();
                }
                int rank = Array.IndexOf(ServiceTypes, type);
                if (rank < 0 || rank >= best || string.IsNullOrEmpty(url)) continue;
                Uri full;
                if (!Uri.TryCreate(baseUri, url, out full) || full.Scheme != Uri.UriSchemeHttp) continue;
                if (!string.Equals(full.Host, new Uri(location).Host, StringComparison.OrdinalIgnoreCase)) continue;
                best = rank;
                control = full.AbsoluteUri;
                service = type;
            }
            return control != null;
        }

        private bool Soap(string controlUrl, string serviceType, string action, string args, out string body, out int upnpError)
        {
            upnpError = 0;
            body = null;
            if (controlUrl == null) return false;
            string envelope = "<?xml version=\"1.0\"?>\r\n<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                              "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body><u:" + action + " xmlns:u=\"" + serviceType + "\">" +
                              args + "</u:" + action + "></s:Body></s:Envelope>\r\n";
            string error;
            int status;
            body = Http(controlUrl, envelope, "\"" + serviceType + "#" + action + "\"", out error, out status);
            if (body != null && status == 200) return true;
            int code;
            if (body != null && int.TryParse(XmlValue(body, "errorCode") ?? "", NumberStyles.Integer, CultureInfo.InvariantCulture, out code)) upnpError = code;
            return false;
        }

        private string Http(string url, string post, string soapAction, out string error)
        {
            int status;
            string body = Http(url, post, soapAction, out error, out status);
            return status == 200 ? body : null;
        }

        // Ответ с кодом ошибки тоже читается: в нём UPnPError. Тело больше 256 КБ — отказ.
        private string Http(string url, string post, string soapAction, out string error, out int status)
        {
            error = null;
            status = 0;
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || u.Scheme != Uri.UriSchemeHttp) return null;
            try
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create(u);
                req.Proxy = null;
                req.Timeout = HttpTimeoutMs;
                req.ReadWriteTimeout = HttpTimeoutMs;
                req.AllowAutoRedirect = false;
                req.KeepAlive = false;
                req.UserAgent = "SysDeck UPnP/1.1";
                if (post != null)
                {
                    byte[] data = Encoding.UTF8.GetBytes(post);
                    req.Method = "POST";
                    req.ServicePoint.Expect100Continue = false;
                    req.ContentType = "text/xml; charset=\"utf-8\"";
                    req.Headers["SOAPAction"] = soapAction;
                    req.ContentLength = data.Length;
                    using (Stream s = req.GetRequestStream()) s.Write(data, 0, data.Length);
                }
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException wex)
                {
                    resp = wex.Response as HttpWebResponse;
                    if (resp == null) throw;
                }
                using (resp)
                {
                    status = (int)resp.StatusCode;
                    using (Stream s = resp.GetResponseStream())
                    using (MemoryStream ms = new MemoryStream())
                    {
                        byte[] buf = new byte[8192];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            ms.Write(buf, 0, n);
                            if (ms.Length > MaxXml) { error = Tr.S("UPnP: слишком большой ответ роутера", "UPnP: the router response is too large"); return null; }
                        }
                        return Encoding.UTF8.GetString(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                if (!(ex is WebException || ex is IOException || ex is NotSupportedException || ex is ProtocolViolationException)) DlLog.Report(ex);
                error = Tr.S("UPnP: роутер не отвечает", "UPnP: the router does not respond");
                return null;
            }
        }

        private static XmlDocument LoadXml(string xml)
        {
            try
            {
                XmlReaderSettings settings = new XmlReaderSettings();
                settings.DtdProcessing = DtdProcessing.Prohibit;
                settings.XmlResolver = null;
                XmlDocument doc = new XmlDocument();
                doc.XmlResolver = null;
                using (XmlReader r = XmlReader.Create(new StringReader(xml), settings)) doc.Load(r);
                return doc;
            }
            catch (XmlException) { return null; }
        }

        private static string XmlValue(string xml, string localName)
        {
            XmlDocument doc = LoadXml(xml);
            if (doc == null) return null;
            foreach (XmlNode n in doc.GetElementsByTagName("*"))
                if (n.LocalName == localName) return n.InnerText.Trim();
            return null;
        }

        // Адрес своего интерфейса, через который виден хост: connect у UDP-сокета ничего не отправляет.
        private static string LocalAddressTo(Uri u)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(u.Host, out ip))
            {
                try { ip = Array.Find(Dns.GetHostAddresses(u.Host), delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork; }); }
                catch (Exception) { ip = null; }
                if (ip == null) return null;
            }
            IPAddress local = LocalAddressTo(new BtEndpoint(ip, u.Port));
            return local == null ? null : local.ToString();
        }

        private static IPAddress LocalAddressTo(BtEndpoint remote)
        {
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(remote.Address, remote.Port));
                    return ((IPEndPoint)s.LocalEndPoint).Address;
                }
            }
            catch (SocketException) { return null; }
        }

        // ------------------------------------------------------------------ //
        //  NAT-PMP (RFC 6886)
        // ------------------------------------------------------------------ //
        private bool TryNatPmp(ref string error, out DateTime renewAt)
        {
            renewAt = DateTime.MaxValue;
            BtEndpoint gw = _natPmp;
            if (gw == null && _autoGateway) gw = DefaultGateway();
            if (gw == null) return false;
            _gateway = gw;
            return MapNatPmp(ref error, out renewAt);
        }

        private bool MapNatPmp(ref string error, out DateTime renewAt)
        {
            renewAt = DateTime.MaxValue;
            byte[] ext = PmpRequest(_gateway, new byte[] { 0, 0 }, 12, NatPmpTries);
            if (ext == null) return false;
            if (GetShort(ext, 2) != 0)
            {
                error = Tr.S("NAT-PMP: роутер отказал", "NAT-PMP: the router refused") + " (" + Num(GetShort(ext, 2)) + ")";
                return false;
            }
            IPAddress external = new IPAddress(new[] { ext[8], ext[9], ext[10], ext[11] });
            int lifeTcp, lifeUdp;
            lifeUdp = 0;
            int tcp = PmpMap(_gateway, 2, _tcpPort, _tcpPort, LeaseSeconds, NatPmpTries, out lifeTcp);
            int udp = tcp > 0 ? PmpMap(_gateway, 1, _udpPort, _udpPort, LeaseSeconds, NatPmpTries, out lifeUdp) : 0;
            if (tcp <= 0 || udp <= 0)
            {
                if (tcp > 0) PmpMap(_gateway, 2, _tcpPort, 0, 0, 2);
                error = Tr.S("NAT-PMP: роутер отказал в пробросе", "NAT-PMP: the router refused the mapping");
                return false;
            }
            int life = Math.Min(lifeTcp, lifeUdp);
            _method = 2;
            _mappedTcp = _tcpPort;
            _mappedUdp = _udpPort;
            renewAt = DateTime.UtcNow.AddSeconds(Math.Max(1, life / 2));
            string gwText = _gateway.Address.ToString();
            // Роутер вправе выдать другой внешний порт — пользователю показываем тот, что открыт на самом деле.
            SetStatus(Tr.S("NAT-PMP: порт " + Num(tcp) + " открыт на " + gwText, "NAT-PMP: port " + Num(tcp) + " is open on " + gwText), true, external);
            return true;
        }

        // Снятие проброса — лучшее усилие, две попытки: выход из приложения не ждёт молчащий шлюз.
        private void PmpMap(BtEndpoint gw, int op, int internalPort, int externalPort, int lifetime, int tries)
        {
            int life;
            PmpMap(gw, op, internalPort, externalPort, lifetime, tries, out life);
        }

        // op 1 — UDP, 2 — TCP. Результат — внешний порт (0 — отказ или нет ответа).
        private int PmpMap(BtEndpoint gw, int op, int internalPort, int externalPort, int lifetime, int tries, out int grantedLifetime)
        {
            grantedLifetime = 0;
            if (gw == null) return 0;
            byte[] req = new byte[12];
            req[1] = (byte)op;
            PutShort(req, 4, internalPort);
            PutShort(req, 6, externalPort);
            req[8] = (byte)(lifetime >> 24); req[9] = (byte)(lifetime >> 16); req[10] = (byte)(lifetime >> 8); req[11] = (byte)lifetime;
            byte[] resp = PmpRequest(gw, req, 16, tries);
            if (resp == null || GetShort(resp, 2) != 0 || GetShort(resp, 8) != internalPort) return 0;
            grantedLifetime = (int)Math.Min(int.MaxValue, ((uint)resp[12] << 24) | ((uint)resp[13] << 16) | ((uint)resp[14] << 8) | resp[15]);
            return lifetime == 0 ? internalPort : GetShort(resp, 10);
        }

        // Повтор 250 мс, 500 мс, 1 с, 2 с. Сокет «соединён» со шлюзом — ответы с других адресов система отбрасывает сама.
        private byte[] PmpRequest(BtEndpoint gw, byte[] request, int minLength, int tries)
        {
            int op = request[1] + 128;
            try
            {
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    s.Connect(new IPEndPoint(gw.Address, gw.Port));
                    byte[] buf = new byte[64];
                    int timeout = NatPmpFirstTimeoutMs;
                    for (int attempt = 0; attempt < tries; attempt++)
                    {
                        s.Send(request);
                        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeout);
                        while (DateTime.UtcNow < deadline)
                        {
                            if (!s.Poll(50 * 1000, SelectMode.SelectRead)) continue;
                            int n = s.Receive(buf);
                            if (n >= minLength && buf[0] == 0 && buf[1] == op)
                            {
                                byte[] r = new byte[n];
                                Buffer.BlockCopy(buf, 0, r, 0, n);
                                return r;
                            }
                        }
                        timeout *= 2;
                    }
                }
            }
            catch (SocketException) { }
            return null;
        }

        private static BtEndpoint DefaultGateway()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (GatewayIPAddressInformation g in ni.GetIPProperties().GatewayAddresses)
                        if (g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                            return new BtEndpoint(g.Address, 5351);
                }
            }
            catch (NetworkInformationException) { }
            return null;
        }

        private static string Num(int v) { return v.ToString(CultureInfo.InvariantCulture); }
        private static void PutShort(byte[] b, int at, int v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
        private static int GetShort(byte[] b, int at) { return (b[at] << 8) | b[at + 1]; }
    }
}
