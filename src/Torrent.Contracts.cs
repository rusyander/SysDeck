// Windows Process Cleaner — «Загрузки», торренты: общие типы и интерфейсы между частями клиента.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Части клиента не ссылаются друг на друга напрямую, только на этот файл: трекеры и magnet (Torrent.Trackers.cs,
// Torrent.Metadata.cs, Torrent.Lsd.cs), соединения пиров и раздача (Torrent.Net/Wire/Peer/Picker/Choker/Swarm/Session.cs),
// DHT, PEX, шифрование MSE и проброс порта (Torrent.Dht/Pex/Mse/PortMap.cs). Сессия собирает их через BtFactories.
//
// Потоки. Сеть пиров — один поток реактора (Socket.Select), UDP — один поток приёма (Torrent.Udp.cs), диск — BtDisk,
// HTTP-трекеры — пул потоков. Любой метод интерфейса ниже может быть вызван из любого из них: реализации потокобезопасны
// и никогда не блокируют вызывающего (никакого диска, HTTP или Sleep внутри обратного вызова).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace WindowsProcessCleaner.Downloads
{
    // ------------------------------------------------------------------ //
    //  Адрес пира или узла
    // ------------------------------------------------------------------ //
    internal sealed class BtEndpoint : IEquatable<BtEndpoint>
    {
        public readonly IPAddress Address;
        public readonly int Port;

        public BtEndpoint(IPAddress address, int port)
        {
            Address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            Port = port;
        }

        public bool IsV6 { get { return Address.AddressFamily == AddressFamily.InterNetworkV6; } }

        // Годится для соединения: порт 1..65535, не широковещательный, не «любой» адрес, не групповой.
        public bool IsUsable
        {
            get
            {
                if (Port <= 0 || Port > 65535) return false;
                if (Address.Equals(IPAddress.Any) || Address.Equals(IPAddress.IPv6Any) || Address.Equals(IPAddress.Broadcast)) return false;
                byte[] b = Address.GetAddressBytes();
                if (!IsV6 && b[0] >= 224) return false;
                if (IsV6 && b[0] == 0xFF) return false;
                return true;
            }
        }

        // Компактная форма BEP 23 / BEP 7: 4 (или 16) байта адреса + 2 байта порта, сетевой порядок.
        public static List<BtEndpoint> ParseCompact(byte[] data, bool v6)
        {
            List<BtEndpoint> list = new List<BtEndpoint>();
            int size = v6 ? 18 : 6;
            if (data == null) return list;
            for (int i = 0; i + size <= data.Length; i += size)
            {
                byte[] a = new byte[size - 2];
                Buffer.BlockCopy(data, i, a, 0, a.Length);
                int port = (data[i + size - 2] << 8) | data[i + size - 1];
                BtEndpoint ep = new BtEndpoint(new IPAddress(a), port);
                if (ep.IsUsable) list.Add(ep);
            }
            return list;
        }

        public byte[] ToCompact()
        {
            byte[] a = Address.GetAddressBytes();
            byte[] b = new byte[a.Length + 2];
            Buffer.BlockCopy(a, 0, b, 0, a.Length);
            b[a.Length] = (byte)(Port >> 8);
            b[a.Length + 1] = (byte)Port;
            return b;
        }

        // «host:port», «[v6]:port». Имя хоста не разрешается: только литерал адреса.
        public static BtEndpoint TryParse(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int colon = text.LastIndexOf(':');
            if (colon <= 0) return null;
            string host = text.Substring(0, colon).Trim('[', ']');
            int port;
            IPAddress ip;
            if (!int.TryParse(text.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out port) || !IPAddress.TryParse(host, out ip)) return null;
            BtEndpoint ep = new BtEndpoint(ip, port);
            return ep.IsUsable ? ep : null;
        }

        public bool Equals(BtEndpoint other) { return other != null && Port == other.Port && Address.Equals(other.Address); }
        public override bool Equals(object obj) { return Equals(obj as BtEndpoint); }
        public override int GetHashCode() { return Address.GetHashCode() * 31 + Port; }
        public override string ToString() { return (IsV6 ? "[" + Address + "]" : Address.ToString()) + ":" + Port.ToString(CultureInfo.InvariantCulture); }
    }

    internal enum BtPeerOrigin { Tracker, Dht, Pex, Lsd, Incoming, Manual }

    internal enum BtEncryption
    {
        Prefer,     // исходящие — сначала MSE, при отказе — открытый текст; входящие — любые
        Require,    // только MSE с RC4 в обе стороны
        Off         // только открытый текст
    }

    internal enum BtAnnounceEvent { None, Started, Completed, Stopped }

    // Снимок соединённого пира — для PEX и карточки загрузки.
    internal sealed class BtPeerInfo
    {
        public BtEndpoint Endpoint;          // адрес, по которому к пиру можно подключиться (порт из 'p' расширенного рукопожатия)
        public bool Outgoing;
        public bool Seed;
        public bool Encrypted;
        public string Client = "";
        public BtPeerOrigin Origin;
        public long DownBps, UpBps;
        public long Downloaded, Uploaded;
        public double Progress;              // 0..1
        public string Flags = "";            // буквы состояния для карточки (как у qBittorrent: D, U, d, u, K, ?, X, E …)
    }

    // ------------------------------------------------------------------ //
    //  Рой одного торрента — реализует Torrent.Swarm.cs; пользуются трекеры, DHT, PEX, LSD, ut_metadata
    // ------------------------------------------------------------------ //
    internal interface IBtSwarm
    {
        byte[] InfoHash { get; }             // 20 байт: рукопожатие, трекеры, DHT
        bool IsPrivate { get; }              // BEP 27: без DHT, PEX, LSD
        BtMeta Meta { get; }                 // null — пока метаданные не получены (magnet)
        long Uploaded { get; }               // байт отдано за эту сессию объявления (для трекера)
        long Downloaded { get; }
        long Left { get; }                   // байт осталось; до метаданных — 0 и Meta == null
        bool IsSeed { get; }
        int NumWant { get; }                 // сколько пиров просить у трекера или DHT; 0 — хватает
        bool Active { get; }                 // false — на паузе или остановлен: объявления не нужны

        // Найденные адреса. Дубликаты, свои адреса и лишнее сверх лимита рой отбрасывает сам.
        void AddPeers(IList<BtEndpoint> peers, BtPeerOrigin origin);

        // Пиры, с которыми есть соединение, — для PEX.
        List<BtPeerInfo> ConnectedPeers();

        // Метаданные собраны по ut_metadata и сошлись с info-hash. false — рой их не принял (уже есть или не подошли).
        bool OnMetadata(byte[] infoBytes);

        void Journal(string text);           // строка в журнал загрузки (видна пользователю в карточке)
    }

    // ------------------------------------------------------------------ //
    //  Одно соединение с пиром — реализует Torrent.Peer.cs; пользуются расширения BEP 10 (ut_metadata, ut_pex)
    // ------------------------------------------------------------------ //
    internal interface IBtPeerLink
    {
        BtEndpoint Endpoint { get; }         // адрес сокета
        int ListenPort { get; }              // 'p' из расширенного рукопожатия; 0 — не сообщил
        bool Outgoing { get; }
        bool Encrypted { get; }
        bool IsSeed { get; }
        string Client { get; }

        bool Supports(string extension);     // пир объявил расширение в 'm'

        // Сообщение расширения (id 20, свой номер пира берётся из его 'm'). Неизвестное пиру расширение — молча ничего.
        void SendExtended(string extension, byte[] payload);

        void Close(string reason);
    }

    // ------------------------------------------------------------------ //
    //  Расширение BEP 10: по экземпляру на торрент, создаёт BtFactories.Extensions
    // ------------------------------------------------------------------ //
    internal interface IBtExtension
    {
        string Name { get; }                 // "ut_metadata", "ut_pex"

        // Своё поле в наше расширенное рукопожатие (например metadata_size). handshake — корневой словарь.
        void FillHandshake(BVal handshake);

        void OnHandshake(IBtPeerLink peer, BVal handshake);
        void OnMessage(IBtPeerLink peer, byte[] payload, int offset, int count);
        void OnClosed(IBtPeerLink peer);

        // Раз в секунду из потока реактора (таймауты, периодические рассылки).
        void Tick(DateTime utcNow);
    }

    // ------------------------------------------------------------------ //
    //  Трекеры торрента (BEP 3/12/15/23/41) — Torrent.Trackers.cs
    // ------------------------------------------------------------------ //
    internal sealed class BtTrackerInfo
    {
        public string Url = "";              // для показа: passkey скрыт (BtRedact.Url)
        public int Tier;
        public string Status = "";           // «работает», «ошибка: …», «ждёт», «не связывался»
        public int Seeders = -1, Leechers = -1, Downloaded = -1;
        public int PeersReceived;
        public DateTime NextAnnounceUtc = DateTime.MinValue;
        public string Message = "";          // warning message / failure reason трекера
    }

    internal interface IBtTrackers : IDisposable
    {
        // Раз в секунду. Сама работа — в пуле потоков (HTTP) и через BtContext.Udp (UDP); вызов не блокирует.
        void Tick(DateTime utcNow);
        void Announce(BtAnnounceEvent e);    // Started при старте, Completed при завершении, Stopped при паузе (без ожидания ответа)
        void Reannounce();                   // «обновить трекеры» — вне интервала, но не чаще min interval
        void AddTracker(string url, int tier);
        List<BtTrackerInfo> Snapshot();
    }

    // ------------------------------------------------------------------ //
    //  DHT (BEP 5) — Torrent.Dht.cs; один узел на сессию
    // ------------------------------------------------------------------ //
    internal interface IBtDht : IBtUdpHandler, IDisposable
    {
        void Start();                        // загрузка dht.json, bootstrap
        void AddNode(BtEndpoint node);       // из сообщения PORT и из magnet x.pe
        // Поиск пиров для роя; результаты — в swarm.AddPeers(…, Dht). announce — объявить себя (только если входящие открыты).
        void GetPeers(IBtSwarm swarm, bool announce);
        int NodeCount { get; }
        string Status { get; }
    }

    // ------------------------------------------------------------------ //
    //  UDP-сокет сессии — Torrent.Udp.cs; обработчики: UDP-трекеры, DHT
    // ------------------------------------------------------------------ //
    internal interface IBtUdpHandler
    {
        // Датаграмма пришла; true — это сообщение обработчика (дальше по списку не передаётся). Поток приёма UDP.
        bool HandleDatagram(BtEndpoint from, byte[] data, int count);
    }

    internal interface IBtUdp
    {
        int Port { get; }
        void Send(BtEndpoint to, byte[] data, int count);   // не блокирует; ошибки глотаются (UDP)
        void AddHandler(IBtUdpHandler handler);
        void RemoveHandler(IBtUdpHandler handler);
    }

    // ------------------------------------------------------------------ //
    //  Локальное обнаружение пиров (BEP 14) — Torrent.Lsd.cs
    // ------------------------------------------------------------------ //
    internal interface IBtLsd : IDisposable
    {
        void Start();
        void Announce(IBtSwarm swarm);       // не чаще раза в 5 минут на торрент; частные торренты — никогда
    }

    // ------------------------------------------------------------------ //
    //  Проброс порта на роутере (UPnP IGD, NAT-PMP) — Torrent.PortMap.cs
    // ------------------------------------------------------------------ //
    internal interface IBtPortMapper : IDisposable
    {
        void Start(int tcpPort, int udpPort);   // в фоне; повтор при потере аренды
        void Stop();                             // снять проброс (Dispose тоже снимает)
        bool Mapped { get; }
        IPAddress ExternalAddress { get; }       // null — неизвестен
        string Status { get; }                   // для карточки настроек: «UPnP: порт 51413 открыт на 192.168.1.1» …
    }

    // ------------------------------------------------------------------ //
    //  Рукопожатие шифрования MSE/PE — Torrent.Mse.cs
    // ------------------------------------------------------------------ //
    internal interface IBtCipher
    {
        void Apply(byte[] data, int offset, int count);   // RC4 на месте
    }

    internal enum BtHandshakeState { NeedMore, Done, Failed }

    internal interface IBtStreamHandshake
    {
        // Исходящее: первое сообщение (Ya + PadA) — в send. Входящее — ничего.
        void Begin(List<byte[]> send);

        // Принятые байты; то, что надо отправить, добавляется в send по порядку.
        BtHandshakeState Feed(byte[] data, int offset, int count, List<byte[]> send);

        IBtCipher Encryptor { get; }         // после Done: null — дальше открытый текст
        IBtCipher Decryptor { get; }
        byte[] InfoHash { get; }             // входящее: торрент, найденный по HASH('req2', SKEY)
        byte[] Remaining { get; }            // уже расшифрованные байты после рукопожатия (начало рукопожатия BitTorrent)
        string Error { get; }
    }

    // ------------------------------------------------------------------ //
    //  Общее для всех частей одной сессии
    // ------------------------------------------------------------------ //
    internal sealed class BtContext
    {
        public byte[] PeerId;                // 20 байт: "-WP1000-" + 12 случайных
        public int Port;                     // TCP и UDP порт сессии
        public volatile bool InboundOpen;    // слушатель TCP работает (правило брандмауэра есть, пользователь разрешил)
        public BtEncryption Encryption = BtEncryption.Prefer;
        public IDlEnvironment Env;
        public IBtUdp Udp;
        public IBtDht Dht;                   // null — DHT выключен
        public IBtLsd Lsd;                   // null — выключено
        public DlTokenBucket DownGlobal;     // общий с HTTP-движком
        public DlTokenBucket UpGlobal;
        public string TorrentsDir = "";      // DlPaths.DataDir\torrents
        public uint AnnounceKey;             // key= для трекеров, один на сессию
        public IPAddress ExternalAddress;    // известный внешний адрес (трекер, UPnP, yourip); null — неизвестен
        public Func<byte[], IBtSwarm> FindSwarm = delegate { return null; };   // по 20-байтовому хешу
        public Action<string> Log = delegate { };                              // DlLog без секретов

        public const string ClientName = "WPC 1.0";

        public static byte[] NewPeerId()
        {
            byte[] id = new byte[20];
            byte[] prefix = System.Text.Encoding.ASCII.GetBytes("-WP1000-");
            Buffer.BlockCopy(prefix, 0, id, 0, prefix.Length);
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                byte[] tail = new byte[12];
                rng.GetBytes(tail);
                Buffer.BlockCopy(tail, 0, id, 8, 12);
            }
            return id;
        }
    }

    // ------------------------------------------------------------------ //
    //  Сборка частей. Пусто — части нет (торрент работает без неё); заполняет Torrent.Wiring.cs при сборке клиента
    // ------------------------------------------------------------------ //
    internal static class BtFactories
    {
        public static Func<IBtSwarm, BtContext, IList<List<string>>, IBtTrackers> Trackers;
        public static Func<BtContext, IBtDht> Dht;
        public static Func<BtContext, IBtLsd> Lsd;
        public static Func<IBtPortMapper> PortMapper;
        // Расширения BEP 10 для торрента; private-торренту PEX не создаётся (решает фабрика по swarm.IsPrivate).
        public static readonly List<Func<IBtSwarm, BtContext, IBtExtension>> Extensions = new List<Func<IBtSwarm, BtContext, IBtExtension>>();
        public static Func<byte[], BtEncryption, IBtStreamHandshake> MseOutgoing;                        // (infoHash, режим)
        public static Func<Func<byte[], byte[]>, BtEncryption, IBtStreamHandshake> MseIncoming;          // (HASH('req2',SKEY) → infoHash|null, режим)
    }

    // ------------------------------------------------------------------ //
    //  Секреты в адресах трекеров: passkey в пути и в query не показываются и не пишутся в журнал
    // ------------------------------------------------------------------ //
    internal static class BtRedact
    {
        public static string Url(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return "";
            string path = u.AbsolutePath;
            // /<32+ hex>/announce (многие частные трекеры держат ключ в пути)
            string[] parts = path.Split('/');
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].Length >= 16 && IsKeyLike(parts[i])) parts[i] = "***";
            return u.Scheme + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port.ToString(CultureInfo.InvariantCulture)) + string.Join("/", parts)
                   + (u.Query.Length > 1 ? "?***" : "");
        }

        private static bool IsKeyLike(string s)
        {
            foreach (char c in s)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z' || c == '-' || c == '_')) return false;
            return true;
        }
    }
}
