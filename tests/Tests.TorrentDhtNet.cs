// SysDeck — область «torrent», часть C: сеть DHT, проброс порта, сверка с libtorrent.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class TorrentTests
    {
        private static void DhtNetworkCases()
        {
            string root = Fx.MakeDir(Fx.Root, "bt-dht");
            List<DhtFxNode> nodes = new List<DhtFxNode>();
            DhtFxClient client = null;
            try
            {
                DhtFxNode n0 = new DhtFxNode(Fx.MakeDir(root, "n0"));
                nodes.Add(n0);
                for (int i = 1; i < 5; i++) nodes.Add(new DhtFxNode(Fx.MakeDir(root, "n" + i), n0.Addr));
                foreach (DhtFxNode n in nodes) n.Dht.Start();
                bool meshed = DhtWaitFor(delegate { return n0.Dht.NodeCount >= 4 && nodes.TrueForAll(delegate(DhtFxNode n) { return n.Dht.NodeCount >= 1; }); }, 8000);
                string counts = string.Join(",", nodes.ConvertAll(delegate(DhtFxNode n) { return n.Dht.NodeCount.ToString(); }).ToArray());
                T.Check("dht: 5 nodes on real BtUdp bootstrap from one and fill their tables", meshed, counts);

                byte[] hash = DhtRandom(20);
                DhtFxSwarm announcer = new DhtFxSwarm();
                announcer.Hash = hash;
                nodes[2].Ctx.InboundOpen = true;
                nodes[2].Dht.GetPeers(announcer, true);
                bool stored = DhtWaitFor(delegate { int s = 0; foreach (DhtFxNode n in nodes) s += n.Dht.StoredPeers(hash); return s > 0; }, 5000);
                T.Check("dht: announce_peer (inbound open) stores the peer on the closest nodes", stored);

                DhtFxSwarm seeker = new DhtFxSwarm();
                seeker.Hash = hash;
                BtEndpoint expected = new BtEndpoint(IPAddress.Loopback, nodes[2].Ctx.Port);
                bool found = DhtWaitFor(delegate
                {
                    if (seeker.Has(expected)) return true;
                    if (nodes[4].Dht.LookupCount == 0) nodes[4].Dht.GetPeers(seeker, false);
                    return false;
                }, 6000);
                T.Check("dht: get_peers on another node finds the announced peer -> swarm.AddPeers(…, Dht)",
                        found && seeker.Origins.TrueForAll(delegate(BtPeerOrigin o) { return o == BtPeerOrigin.Dht; }));

                byte[] closedHash = DhtRandom(20);
                DhtFxSwarm closed = new DhtFxSwarm();
                closed.Hash = closedHash;
                nodes[3].Ctx.InboundOpen = false;
                nodes[3].Dht.GetPeers(closed, true);
                DhtWaitFor(delegate { return nodes[3].Dht.LookupCount == 0; }, 5000);
                Thread.Sleep(300);
                int closedStored = 0;
                foreach (DhtFxNode n in nodes) closedStored += n.Dht.StoredPeers(closedHash);
                T.Check("dht: inbound closed — lookup runs but nothing is announced", nodes[3].Dht.LookupCount == 0 && closedStored == 0, "stored " + closedStored);

                // Частный рой: узел, чья таблица знает только наш сырой сокет. Открытый рой — запрос приходит (контроль канала),
                // частный — ни одного пакета с его info-hash.
                using (DhtFxClient watcher = new DhtFxClient())
                using (DhtFxNode lone = new DhtFxNode(Fx.MakeDir(root, "lone")))
                {
                    lone.Dht.Start();
                    DhtWaitFor(delegate { return lone.Dht.Ready; }, 3000);
                    lone.Dht.InsertNodeForTest(DhtRandom(20), watcher.Udp.Port > 0 ? new BtEndpoint(IPAddress.Loopback, watcher.Udp.Port) : null);
                    lone.Ctx.InboundOpen = true;
                    DhtFxSwarm open = new DhtFxSwarm();
                    open.Hash = DhtRandom(20);
                    DhtFxSwarm priv = new DhtFxSwarm();
                    priv.Private = true;
                    priv.Hash = DhtRandom(20);
                    lone.Dht.GetPeers(priv, true);
                    lone.Dht.GetPeers(open, true);
                    bool control = DhtWaitFor(delegate { return watcher.Any(delegate(BVal v) { return v.GetStr("q") == "get_peers" && Bencode.SameBytes(v.Get("a", BKind.Dict).GetBytes("info_hash"), open.Hash); }); }, 2000);
                    Thread.Sleep(200);
                    bool leaked = watcher.Any(delegate(BVal v) { BVal a = v.Get("a", BKind.Dict); return a != null && Bencode.SameBytes(a.GetBytes("info_hash"), priv.Hash); });
                    T.Check("dht: private swarm — no get_peers/announce leaves the node (public swarm control query arrives)", control && !leaked && priv.Count == 0,
                            "control " + control + " leaked " + leaked);
                }

                // Токены: поддельный отклоняется, настоящий принимается, после одной смены секрета ещё годен, после двух — нет.
                client = new DhtFxClient();
                byte[] th = DhtRandom(20);
                BVal gp = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                BVal gr = gp == null ? null : gp.Get("r", BKind.Dict);
                byte[] token = gr == null ? null : gr.GetBytes("token");
                T.Check("dht: get_peers reply carries a token and compact nodes", token != null && gr.GetBytes("nodes") != null && gr.GetBytes("nodes").Length % 26 == 0);
                if (token != null)
                {
                    byte[] forged = (byte[])token.Clone();
                    forged[0] ^= 0x5A;
                    BVal bad = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7001)).Set("token", BVal.Bytes(forged)));
                    BVal badE = bad == null ? null : bad.Get("e", BKind.List);
                    T.Check("dht: forged token refused (error 203), nothing stored",
                            badE != null && badE.L.Count >= 1 && badE.L[0].I == 203 && n0.Dht.StoredPeers(th) == 0, bad == null ? "no reply" : bad.GetStr("y"));
                    BVal good = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7001)).Set("token", BVal.Bytes(token)));
                    BVal again = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    BVal values = again == null || again.Get("r", BKind.Dict) == null ? null : again.Get("r", BKind.Dict).Get("values", BKind.List);
                    T.Check("dht: valid token accepted — the peer is served back in values",
                            good != null && good.GetStr("y") == "r" && values != null && values.L.Count == 1
                            && Bencode.Hex(values.L[0].B) == Bencode.Hex(new BtEndpoint(IPAddress.Loopback, 7001).ToCompact()));
                    n0.Dht.RotateSecret();
                    BVal prev = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7002)).Set("token", BVal.Bytes(token)));
                    n0.Dht.RotateSecret();
                    BVal stale = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(7003)).Set("token", BVal.Bytes(token)));
                    T.Check("dht: token from the previous secret still accepted, two rotations old refused",
                            prev != null && prev.GetStr("y") == "r" && stale != null && stale.GetStr("y") == "e" && n0.Dht.StoredPeers(th) == 2);
                    BVal fresh = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    byte[] freshToken = fresh == null || fresh.Get("r", BKind.Dict) == null ? new byte[0] : fresh.Get("r", BKind.Dict).GetBytes("token");
                    BVal implied = client.Ask(n0.Ep, "announce_peer", client.Args().Set("info_hash", BVal.Bytes(th)).Set("port", BVal.Int(1))
                        .Set("implied_port", BVal.Int(1)).Set("token", BVal.Bytes(freshToken)));
                    BVal withImplied = client.Ask(n0.Ep, "get_peers", client.Args().Set("info_hash", BVal.Bytes(th)));
                    BVal vi = withImplied == null || withImplied.Get("r", BKind.Dict) == null ? null : withImplied.Get("r", BKind.Dict).Get("values", BKind.List);
                    string wantImplied = Bencode.Hex(new BtEndpoint(IPAddress.Loopback, client.Udp.Port).ToCompact());
                    T.Check("dht: implied_port stores the UDP source port, not the port argument",
                            implied != null && implied.GetStr("y") == "r" && vi != null && vi.L.Exists(delegate(BVal v) { return Bencode.Hex(v.B) == wantImplied; }));
                }
                BVal noId = client.Ask(n0.Ep, "ping", BVal.NewDict());
                BVal unknown = client.Ask(n0.Ep, "vote", client.Args());
                T.Check("dht: malformed query -> 203, unknown method -> 204",
                        noId != null && noId.Get("e", BKind.List) != null && noId.Get("e", BKind.List).L[0].I == 203
                        && unknown != null && unknown.Get("e", BKind.List) != null && unknown.Get("e", BKind.List).L[0].I == 204);

                n0.Dht.RateLimitPerSecond = 5;
                Thread.Sleep(1100);
                int base0 = client.Count;
                for (int i = 0; i < 40; i++) client.Fire(n0.Ep, "ping", client.Args(), new byte[] { (byte)'r', (byte)i });
                Thread.Sleep(400);
                int replies = client.Count - base0;
                n0.Dht.RateLimitPerSecond = 500;
                T.Check("dht: per-IP rate limit — a burst of 40 queries gets at most a few replies", replies >= 1 && replies <= 10, replies + " replies");

                // dht.json: id и живые узлы переживают перезапуск.
                string dir1 = nodes[1].Ctx.TorrentsDir;
                byte[] id1 = nodes[1].Dht.NodeId;
                nodes[1].Dht.Dispose();
                JVal saved = DlPaths.ReadJson(Path.Combine(dir1, "dht.json"));
                JVal savedNodes = saved == null ? null : saved.Get("nodes");
                T.Check("dht: dht.json written atomically on Dispose with id and good nodes",
                        saved != null && saved.GetStr("id") == Bencode.Hex(id1) && savedNodes != null && savedNodes.V.Count >= 1
                        && !File.Exists(Path.Combine(dir1, "dht.json.tmp")));
                nodes[1].Dht = new BtDht(nodes[1].Ctx);
                nodes[1].Dht.Bootstrap.Clear();
                nodes[1].Dht.QueryTimeoutMs = 700;
                nodes[1].Ctx.Dht = nodes[1].Dht;
                nodes[1].Dht.Start();
                bool back = DhtWaitFor(delegate { return nodes[1].Dht.Ready && nodes[1].Dht.NodeCount >= 1; }, 4000);
                T.Check("dht: restart from dht.json — same node id, saved nodes re-joined without bootstrap",
                        back && Bencode.SameBytes(nodes[1].Dht.NodeId, id1), "nodes " + nodes[1].Dht.NodeCount);
                T.Check("dht: status text reports the table", nodes[1].Dht.Status.Contains("DHT"), nodes[1].Dht.Status);
            }
            finally
            {
                if (client != null) client.Dispose();
                foreach (DhtFxNode n in nodes) n.Dispose();
            }
        }

        // ================================================================== //
        //  Проброс порта
        // ================================================================== //
        private sealed class PortMapFxIgd : IDisposable
        {
            private readonly HttpListener _listener = new HttpListener();
            public readonly int Port;
            public readonly List<string> Calls = new List<string>();     // «Action proto port client desc lease»
            public int Gets;
            public int FailAddCode;
            public int DescriptionBytes;

            public PortMapFxIgd()
            {
                TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
                probe.Start();
                Port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
                _listener.Prefixes.Add("http://localhost:" + Port + "/");
                _listener.Start();
                Thread t = new Thread(Accept);
                t.IsBackground = true;
                t.Start();
            }

            public string DescUrl { get { return "http://127.0.0.1:" + Port + "/desc.xml"; } }

            public List<string> Snapshot() { lock (Calls) return new List<string>(Calls); }

            private void Accept()
            {
                while (true)
                {
                    HttpListenerContext c;
                    try { c = _listener.GetContext(); }
                    catch { return; }
                    HttpListenerContext cc = c;
                    ThreadPool.QueueUserWorkItem(delegate { Handle(cc); });
                }
            }

            private static string Tag(string xml, string name)
            {
                int a = xml.IndexOf("<" + name + ">", StringComparison.Ordinal);
                int b = xml.IndexOf("</" + name + ">", StringComparison.Ordinal);
                return a < 0 || b < a ? "" : xml.Substring(a + name.Length + 2, b - a - name.Length - 2);
            }

            private void Handle(HttpListenerContext c)
            {
                string body;
                int status = 200;
                try
                {
                    string path = c.Request.Url.AbsolutePath;
                    if (c.Request.HttpMethod == "GET" && path == "/desc.xml")
                    {
                        lock (Calls) Gets++;
                        body = "<?xml version=\"1.0\"?><root xmlns=\"urn:schemas-upnp-org:device-1-0\"><device><deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:1</deviceType>" +
                               "<deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:1</deviceType><deviceList><device>" +
                               "<serviceList><service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><serviceId>urn:upnp-org:serviceId:WANIPConn1</serviceId>" +
                               "<controlURL>/ctl/IPConn</controlURL></service></serviceList></device></deviceList></device></deviceList></device></root>";
                        if (DescriptionBytes > 0) body = body + "<!--" + new string('x', DescriptionBytes) + "-->";
                    }
                    else if (c.Request.HttpMethod == "POST" && path == "/ctl/IPConn")
                    {
                        string action = c.Request.Headers["SOAPAction"] ?? "";
                        string xml;
                        using (StreamReader r = new StreamReader(c.Request.InputStream, Encoding.UTF8)) xml = r.ReadToEnd();
                        string name = action.Trim('"');
                        name = name.Substring(name.IndexOf('#') + 1);
                        bool typeOk = action.Contains("urn:schemas-upnp-org:service:WANIPConnection:1#");
                        lock (Calls)
                            Calls.Add(name + " " + Tag(xml, "NewProtocol") + " " + Tag(xml, "NewExternalPort") + " " + Tag(xml, "NewInternalClient") + " " +
                                      Tag(xml, "NewPortMappingDescription").Replace(' ', '_') + " " + Tag(xml, "NewLeaseDuration") + (typeOk ? "" : " BADTYPE"));
                        if (name == "AddPortMapping" && FailAddCode > 0)
                        {
                            status = 500;
                            body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><s:Fault><faultcode>s:Client</faultcode>" +
                                   "<faultstring>UPnPError</faultstring><detail><UPnPError xmlns=\"urn:schemas-upnp-org:control-1-0\"><errorCode>" + FailAddCode +
                                   "</errorCode><errorDescription>Conflict</errorDescription></UPnPError></detail></s:Fault></s:Body></s:Envelope>";
                        }
                        else
                            body = "<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body><u:" + name + "Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">" +
                                   (name == "GetExternalIPAddress" ? "<NewExternalIPAddress>203.0.113.7</NewExternalIPAddress>" : "") +
                                   "</u:" + name + "Response></s:Body></s:Envelope>";
                    }
                    else
                    {
                        status = 404;
                        body = "";
                    }
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    c.Response.StatusCode = status;
                    c.Response.ContentType = "text/xml";
                    c.Response.ContentLength64 = bytes.Length;
                    c.Response.OutputStream.Write(bytes, 0, bytes.Length);
                    c.Response.Close();
                }
                catch
                {
                    try { c.Response.Abort(); } catch { }
                }
            }

            public void Dispose()
            {
                try { _listener.Stop(); _listener.Close(); } catch { }
            }
        }

        // UDP-ответчик на петле: SSDP (LOCATION) или NAT-PMP (RFC 6886).
        private sealed class PortMapFxUdp : IDisposable
        {
            private readonly Socket _s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            private readonly Thread _t;
            private volatile bool _stop;
            public readonly List<string> Seen = new List<string>();
            public readonly string Location;      // null — NAT-PMP

            public PortMapFxUdp(string ssdpLocation)
            {
                Location = ssdpLocation;
                _s.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                _t = new Thread(Loop);
                _t.IsBackground = true;
                _t.Start();
            }

            public BtEndpoint Ep { get { return new BtEndpoint(IPAddress.Loopback, ((IPEndPoint)_s.LocalEndPoint).Port); } }
            public List<string> Snapshot() { lock (Seen) return new List<string>(Seen); }

            private void Loop()
            {
                byte[] buf = new byte[2048];
                while (!_stop)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n;
                    try
                    {
                        if (!_s.Poll(100000, SelectMode.SelectRead)) continue;
                        n = _s.ReceiveFrom(buf, ref from);
                    }
                    catch { if (_stop) return; continue; }
                    byte[] reply;
                    if (Location != null)
                    {
                        lock (Seen) Seen.Add(Encoding.ASCII.GetString(buf, 0, n));
                        reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=120\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\nLOCATION: " + Location + "\r\n\r\n");
                    }
                    else if (n == 2 && buf[0] == 0 && buf[1] == 0)
                    {
                        lock (Seen) Seen.Add("ext");
                        reply = new byte[] { 0, 128, 0, 0, 0, 0, 0, 9, 198, 51, 100, 9 };
                    }
                    else if (n == 12 && buf[0] == 0 && (buf[1] == 1 || buf[1] == 2))
                    {
                        int life = (buf[8] << 24) | (buf[9] << 16) | (buf[10] << 8) | buf[11];
                        lock (Seen) Seen.Add((buf[1] == 2 ? "TCP " : "UDP ") + ((buf[4] << 8) | buf[5]) + " " + life);
                        reply = new byte[16];
                        reply[1] = (byte)(128 + buf[1]);
                        reply[7] = 9;
                        Buffer.BlockCopy(buf, 4, reply, 8, 4);
                        Buffer.BlockCopy(buf, 8, reply, 12, 4);
                    }
                    else continue;
                    try { _s.SendTo(reply, from); } catch { }
                }
            }

            public void Dispose()
            {
                _stop = true;
                _t.Join(1000);
                _s.Close();
            }
        }

        private static int PortMapClosedPort()
        {
            TcpListener l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static void PortMapCases()
        {
            using (PortMapFxIgd igd = new PortMapFxIgd())
            {
                BtPortMapper m = new BtPortMapper(null, igd.DescUrl, null);
                m.LeaseSeconds = 2;
                try
                {
                    m.Start(40111, 40112);
                    bool mapped = DhtWaitFor(delegate { return m.Mapped; }, 5000);
                    List<string> calls = igd.Snapshot();
                    T.Check("portmap: UPnP IGD — AddPortMapping TCP and UDP with our description, internal client and lease",
                            mapped && calls.Contains("AddPortMapping TCP 40111 127.0.0.1 SysDeck 2")
                            && calls.Contains("AddPortMapping UDP 40112 127.0.0.1 SysDeck 2"), string.Join(" | ", calls.ToArray()));
                    T.Check("portmap: GetExternalIPAddress -> ExternalAddress, status names the port",
                            IPAddress.Parse("203.0.113.7").Equals(m.ExternalAddress) && m.Status.Contains("40111"), m.Status);
                    bool renewed = DhtWaitFor(delegate { return igd.Snapshot().FindAll(delegate(string s) { return s.StartsWith("AddPortMapping TCP", StringComparison.Ordinal); }).Count >= 2; }, 4000);
                    T.Check("portmap: lease renewed at half its duration", renewed);
                }
                finally
                {
                    m.Dispose();
                }
                List<string> after = igd.Snapshot();
                T.Check("portmap: mapping deleted on Dispose (TCP and UDP)",
                        after.Contains("DeletePortMapping TCP 40111   ") && after.Contains("DeletePortMapping UDP 40112   ") && !m.Mapped,
                        string.Join(" | ", after.ToArray()));

                // SSDP на петлевой адрес: M-SEARCH -> LOCATION -> тот же IGD.
                using (PortMapFxUdp ssdp = new PortMapFxUdp(igd.DescUrl))
                {
                    BtPortMapper ms = new BtPortMapper(ssdp.Ep, null, null);
                    ms.SsdpWaitMs = 1500;
                    try
                    {
                        ms.Start(40121, 40121);
                        bool ok = DhtWaitFor(delegate { return ms.Mapped; }, 6000);
                        List<string> seen = ssdp.Snapshot();
                        T.Check("portmap: SSDP M-SEARCH (ssdp:discover, IGD) -> LOCATION -> mapped",
                                ok && seen.Count >= 1 && seen[0].StartsWith("M-SEARCH * HTTP/1.1", StringComparison.Ordinal) && seen[0].Contains("\"ssdp:discover\"")
                                && seen.Exists(delegate(string s) { return s.Contains("InternetGatewayDevice"); }), ok + " " + seen.Count);
                    }
                    finally
                    {
                        ms.Dispose();
                    }
                }
                int getsBefore = igd.Gets;
                using (PortMapFxUdp foreign = new PortMapFxUdp("http://localhost:" + igd.Port + "/desc.xml"))
                {
                    BtPortMapper mf = new BtPortMapper(foreign.Ep, null, null);
                    mf.SsdpWaitMs = 600;
                    try
                    {
                        mf.Start(40131, 40131);
                        DhtWaitFor(delegate { return foreign.Snapshot().Count > 0; }, 3000);
                        Thread.Sleep(1200);
                        T.Check("portmap: SSDP LOCATION on a host other than the responder is not fetched", !mf.Mapped && igd.Gets == getsBefore, igd.Gets + " gets");
                    }
                    finally
                    {
                        mf.Dispose();
                    }
                }

                igd.FailAddCode = 718;
                BtPortMapper mc = new BtPortMapper(null, igd.DescUrl, null);
                try
                {
                    mc.Start(40141, 40141);
                    bool reported = DhtWaitFor(delegate { return mc.Status.Contains("40141"); }, 5000);
                    T.Check("portmap: SOAP error 718 (conflict) — not mapped, status explains", reported && !mc.Mapped, mc.Status);
                }
                finally
                {
                    mc.Dispose();
                    igd.FailAddCode = 0;
                }

                igd.DescriptionBytes = 300 * 1024;
                int soapBefore = igd.Snapshot().Count;
                getsBefore = igd.Gets;
                BtPortMapper mbig = new BtPortMapper(null, igd.DescUrl, null);
                try
                {
                    mbig.Start(40151, 40151);
                    // Ждём именно отказа после скачивания описания, а не начальный статус.
                    DhtWaitFor(delegate { return igd.Gets > getsBefore && mbig.Status.StartsWith("UPnP", StringComparison.Ordinal); }, 5000);
                    T.Check("portmap: device description over 256 KiB refused — fetched once, no SOAP call", !mbig.Mapped && igd.Gets > getsBefore && igd.Snapshot().Count == soapBefore, mbig.Status);
                }
                finally
                {
                    mbig.Dispose();
                    igd.DescriptionBytes = 0;
                }
            }

            using (PortMapFxUdp pmp = new PortMapFxUdp(null))
            {
                BtPortMapper mp = new BtPortMapper(null, "http://127.0.0.1:" + PortMapClosedPort() + "/desc.xml", pmp.Ep);
                mp.HttpTimeoutMs = 1500;
                try
                {
                    mp.Start(40161, 40162);
                    bool ok = DhtWaitFor(delegate { return mp.Mapped; }, 6000);
                    List<string> seen = pmp.Snapshot();
                    T.Check("portmap: no UPnP -> NAT-PMP fallback maps TCP and UDP, external address from the gateway",
                            ok && seen.Contains("ext") && seen.Contains("TCP 40161 3600") && seen.Contains("UDP 40162 3600")
                            && IPAddress.Parse("198.51.100.9").Equals(mp.ExternalAddress) && mp.Status.Contains("NAT-PMP"),
                            mp.Status + " | " + string.Join(",", seen.ToArray()));
                }
                finally
                {
                    mp.Dispose();
                }
                List<string> after = pmp.Snapshot();
                T.Check("portmap: NAT-PMP mappings deleted on Dispose (lifetime 0)", after.Contains("TCP 40161 0") && after.Contains("UDP 40162 0"), string.Join(",", after.ToArray()));
            }
        }

        // ================================================================== //
        //  libtorrent — независимая реализация
        // ================================================================== //
        private sealed class DhtOracle : IDisposable
        {
            private Process _p;
            private readonly List<string> _lines = new List<string>();

            public static DhtOracle Start(string mode, string work, out string why)
            {
                why = null;
                string py = Environment.GetEnvironmentVariable("SYSDECK_LT_PYTHON");
                if (string.IsNullOrEmpty(py) || !File.Exists(py)) { why = "SYSDECK_LT_PYTHON not set"; return null; }
                string script = null;
                for (string dir = Path.GetDirectoryName(py); dir != null && script == null; dir = Path.GetDirectoryName(dir))
                {
                    string cand = Path.Combine(Path.Combine(Path.Combine(dir, "tests"), "bt-oracle"), "dht_mse.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null)
                {
                    string cand = Path.Combine(Environment.CurrentDirectory, @"tests\bt-oracle\dht_mse.py");
                    if (File.Exists(cand)) script = cand;
                }
                if (script == null) { why = "tests\\bt-oracle\\dht_mse.py not found"; return null; }
                ProcessStartInfo psi = new ProcessStartInfo(py, "\"" + script + "\" " + mode + " \"" + work + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.EnvironmentVariables["PYTHONUNBUFFERED"] = "1";
                DhtOracle o = new DhtOracle();
                o._p = Process.Start(psi);
                o._p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) lock (o._lines) o._lines.Add(e.Data); };
                o._p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { };
                o._p.BeginOutputReadLine();
                o._p.BeginErrorReadLine();
                if (o.Read("READY", 20000) == null)
                {
                    why = "oracle did not start: " + string.Join(" / ", o._lines.ToArray());
                    o.Dispose();
                    return null;
                }
                return o;
            }

            // Первая строка с таким началом (из уже прочитанных тоже); null — не дождались.
            public string Read(string prefix, int ms)
            {
                string found = null;
                DhtWaitFor(delegate
                {
                    lock (_lines)
                        for (int i = 0; i < _lines.Count; i++)
                            if (_lines[i].StartsWith(prefix, StringComparison.Ordinal)) { found = _lines[i]; _lines.RemoveAt(i); return true; }
                    return false;
                }, ms);
                return found;
            }

            public void Send(string command)
            {
                _p.StandardInput.WriteLine(command);
                _p.StandardInput.Flush();
            }

            public void Dispose()
            {
                if (_p == null) return;
                try { Send("quit"); _p.StandardInput.Close(); } catch { }
                try { if (!_p.WaitForExit(5000)) _p.Kill(); } catch { }
                _p.Dispose();
                _p = null;
            }
        }

        private static byte[] BtHandshakeBytes(byte[] hash)
        {
            byte[] h = new byte[68];
            h[0] = 19;
            Buffer.BlockCopy(A("BitTorrent protocol"), 0, h, 1, 19);
            Buffer.BlockCopy(hash, 0, h, 28, 20);
            Buffer.BlockCopy(BtContext.NewPeerId(), 0, h, 48, 20);
            return h;
        }

        // После рукопожатия: BT-рукопожатие пира (68 байт) и следующее сообщение целиком — всё через Decryptor.
        private static byte[] OracleReadStream(Socket s, IBtStreamHandshake hs, int want)
        {
            MemoryStream ms = new MemoryStream();
            ms.Write(hs.Remaining, 0, hs.Remaining.Length);
            byte[] buf = new byte[4096];
            try
            {
                while (ms.Length < want)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    if (hs.Decryptor != null) hs.Decryptor.Apply(buf, 0, n);
                    ms.Write(buf, 0, n);
                }
            }
            catch (SocketException) { }
            return ms.ToArray();
        }

        private static BtHandshakeState OracleHandshake(Socket s, IBtStreamHandshake hs, bool begin)
        {
            List<byte[]> send = new List<byte[]>();
            BtHandshakeState st = BtHandshakeState.NeedMore;
            try
            {
                if (begin) hs.Begin(send);
                byte[] buf = new byte[4096];
                while (true)
                {
                    foreach (byte[] b in send) s.Send(b);
                    send.Clear();
                    if (st != BtHandshakeState.NeedMore) break;
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    st = hs.Feed(buf, 0, n, send);
                }
            }
            catch (SocketException) { }
            return st;
        }

        private static bool OracleBtReply(byte[] got, byte[] hash, out string info)
        {
            info = "got " + (got == null ? 0 : got.Length) + " bytes";
            if (got == null || got.Length < 68 + 5) return false;
            bool hsOk = got[0] == 19 && Encoding.ASCII.GetString(got, 1, 19) == "BitTorrent protocol" && Bencode.Hex(Sub(got, 28, 20)) == Bencode.Hex(hash);
            int len = (got[68] << 24) | (got[69] << 16) | (got[70] << 8) | got[71];
            info += ", next message len " + len + " id " + got[72];
            return hsOk && len >= 1 && len < 1024 && (got[72] == 5 || got[72] == 14 || got[72] == 20);
        }

        private static byte[] Sub(byte[] b, int at, int n)
        {
            byte[] r = new byte[n];
            Buffer.BlockCopy(b, at, r, 0, n);
            return r;
        }

        private static void DhtOracleMse()
        {
            string why;
            string work = Fx.MakeDir(Fx.Root, "bt-oracle-mse");
            DhtOracle lt = DhtOracle.Start("mse", work, out why);
            if (lt == null)
            {
                T.Skip("mse: libtorrent (encryption forced) accepts our MSE Outgoing and answers inside RC4", why);
                T.Skip("mse: libtorrent dials us encrypted — our MSE Incoming completes and decrypts its handshake", why);
                return;
            }
            try
            {
                string hashLine = lt.Read("HASH ", 1000), portLine = lt.Read("PORT ", 1000);
                byte[] hash = hashLine == null ? null : Bencode.FromHex(hashLine.Substring(5).Trim());
                int port = portLine == null ? 0 : int.Parse(portLine.Substring(5).Trim());
                using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    s.ReceiveTimeout = 8000;
                    s.Connect(new IPEndPoint(IPAddress.Loopback, port));
                    IBtStreamHandshake hs = BtMse.Outgoing(hash, BtEncryption.Require);
                    BtHandshakeState st = OracleHandshake(s, hs, true);
                    string info = st + " " + hs.Error;
                    bool ok = false;
                    if (st == BtHandshakeState.Done && hs.Encryptor != null)
                    {
                        byte[] ours = BtHandshakeBytes(hash);
                        hs.Encryptor.Apply(ours, 0, ours.Length);
                        s.Send(ours);
                        ok = OracleBtReply(OracleReadStream(s, hs, 68 + 5), hash, out info);
                    }
                    T.Check("mse: libtorrent (encryption forced) accepts our MSE Outgoing and answers inside RC4", ok, info);
                }

                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                try
                {
                    lt.Send("connect " + ((IPEndPoint)listener.LocalEndpoint).Port);
                    bool dialed = DhtWaitFor(delegate { return listener.Pending(); }, 10000);
                    string info = "not dialed";
                    bool ok = false;
                    if (dialed)
                        using (Socket s = listener.AcceptSocket())
                        {
                            s.ReceiveTimeout = 8000;
                            IBtStreamHandshake hs = BtMse.Incoming(delegate(byte[] req2) { return Bencode.SameBytes(req2, BtMse.Req2(hash)) ? hash : null; }, BtEncryption.Require);
                            BtHandshakeState st = OracleHandshake(s, hs, false);
                            info = st + " " + hs.Error;
                            if (st == BtHandshakeState.Done && hs.Decryptor != null && Bencode.SameBytes(hs.InfoHash, hash))
                            {
                                byte[] theirs = OracleReadStream(s, hs, 68);
                                bool hsOk = theirs.Length >= 68 && theirs[0] == 19 && Bencode.Hex(Sub(theirs, 28, 20)) == Bencode.Hex(hash);
                                byte[] ours = BtHandshakeBytes(hash);
                                hs.Encryptor.Apply(ours, 0, ours.Length);
                                s.Send(ours);
                                byte[] extra = new byte[0];
                                if (theirs.Length > 68) extra = Sub(theirs, 68, theirs.Length - 68);
                                // Следующее сообщение после рукопожатия — тем же расшифровщиком.
                                byte[] next = new byte[0];
                                MemoryStream rest = new MemoryStream();
                                rest.Write(extra, 0, extra.Length);
                                byte[] buf = new byte[4096];
                                try
                                {
                                    while (rest.Length < 5)
                                    {
                                        int n = s.Receive(buf);
                                        if (n <= 0) break;
                                        hs.Decryptor.Apply(buf, 0, n);
                                        rest.Write(buf, 0, n);
                                    }
                                }
                                catch (SocketException) { }
                                next = rest.ToArray();
                                byte[] whole = new byte[68 + next.Length];
                                Buffer.BlockCopy(theirs, 0, whole, 0, 68);
                                Buffer.BlockCopy(next, 0, whole, 68, next.Length);
                                ok = hsOk && OracleBtReply(whole, hash, out info);
                            }
                        }
                    T.Check("mse: libtorrent dials us encrypted — our MSE Incoming completes and decrypts its handshake", ok, info);
                }
                finally
                {
                    listener.Stop();
                }
            }
            finally
            {
                lt.Dispose();
            }
        }

        private static void DhtOracleDht()
        {
            string why;
            string work = Fx.MakeDir(Fx.Root, "bt-oracle-dht");
            DhtOracle lt = DhtOracle.Start("dht", work, out why);
            string[] names =
            {
                "dht: libtorrent node answers our find_node — it enters our routing table",
                "dht: our announce_peer accepted by libtorrent; get_peers from another of our nodes returns the peer",
                "dht: libtorrent get_peers + announce through our node — our replies and token accepted"
            };
            if (lt == null)
            {
                foreach (string n in names) T.Skip(n, why);
                return;
            }
            DhtFxNode a = null, b = null;
            try
            {
                string portLine = lt.Read("PORT ", 1000);
                string ltAddr = "127.0.0.1:" + (portLine == null ? "0" : portLine.Substring(5).Trim());
                a = new DhtFxNode(Fx.MakeDir(work, "a"), ltAddr);
                b = new DhtFxNode(Fx.MakeDir(work, "b"), ltAddr);
                a.Dht.QueryTimeoutMs = 1500;
                b.Dht.QueryTimeoutMs = 1500;
                a.Dht.Start();
                DhtFxNode an = a;
                T.Check(names[0], DhtWaitFor(delegate { return an.Dht.NodeCount >= 1; }, 6000), "nodes " + a.Dht.NodeCount);

                byte[] hash = DhtRandom(20);
                DhtFxSwarm announcer = new DhtFxSwarm();
                announcer.Hash = hash;
                a.Ctx.InboundOpen = true;
                a.Dht.GetPeers(announcer, true);
                DhtWaitFor(delegate { return an.Dht.LookupCount == 0; }, 6000);
                Thread.Sleep(300);
                b.Dht.Start();
                DhtFxSwarm seeker = new DhtFxSwarm();
                seeker.Hash = hash;
                BtEndpoint expected = new BtEndpoint(IPAddress.Loopback, a.Ctx.Port);
                DhtFxNode bn = b;
                bool found = DhtWaitFor(delegate
                {
                    if (seeker.Has(expected)) return true;
                    if (bn.Dht.Ready && bn.Dht.LookupCount == 0) bn.Dht.GetPeers(seeker, false);
                    return false;
                }, 10000);
                T.Check(names[1], found, "seeker got " + seeker.Count);

                byte[] hash2 = DhtRandom(20);
                lt.Send("addnode " + b.Udp.Port);
                lt.Read("OK addnode", 3000);
                Thread.Sleep(1000);
                lt.Send("announce " + Bencode.Hex(hash2) + " 46002");
                lt.Read("OK announce", 3000);
                bool stored = DhtWaitFor(delegate { return bn.Dht.StoredPeers(hash2) + an.Dht.StoredPeers(hash2) >= 1; }, 10000);
                T.Check(names[2], stored, "stored " + b.Dht.StoredPeers(hash2) + "+" + a.Dht.StoredPeers(hash2));
            }
            finally
            {
                if (a != null) a.Dispose();
                if (b != null) b.Dispose();
                lt.Dispose();
            }
        }
    }
}
