// SysDeck — «Загрузки», торренты: протокол пиров — рукопожатие, сообщения BEP 3, BEP 6 (fast), BEP 10.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Кодек без состояния соединения: сборка сообщений в байты и разбор кадров из приёмного буфера. Длина кадра сверяется
// до выделения памяти: кусок — не больше блока 16 КБ + 13 байт кадра, всё остальное — не больше 1 МБ.
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace SysDeck.Downloads
{
    internal static class BtWire
    {
        public const int HandshakeLength = 68;
        public const byte Choke = 0, Unchoke = 1, Interested = 2, NotInterested = 3, Have = 4, Bitfield = 5, Request = 6, Piece = 7,
                          Cancel = 8, Port = 9, Suggest = 0x0D, HaveAll = 0x0E, HaveNone = 0x0F, Reject = 0x10, AllowedFast = 0x11,
                          Extended = 20;

        // Значение 4-байтовой длины: кусок — id + 8 байт заголовка + блок; прочее (bitfield, расширения) — до 1 МБ.
        public const int MaxPieceMessage = 9 + BtMeta.BlockSize;
        public const int MaxMessage = 1024 * 1024;
        public const int MaxExtHandshake = 64 * 1024;

        private static readonly byte[] Protocol = Encoding.ASCII.GetBytes("BitTorrent protocol");

        // ---------- рукопожатие ----------
        public static byte[] Handshake(byte[] infoHash, byte[] peerId, bool dht)
        {
            byte[] b = new byte[HandshakeLength];
            b[0] = 19;
            Buffer.BlockCopy(Protocol, 0, b, 1, 19);
            b[25] = 0x10;                        // BEP 10
            b[27] = (byte)(0x04 | (dht ? 0x01 : 0));   // BEP 6, BEP 5
            Buffer.BlockCopy(infoHash, 0, b, 28, 20);
            Buffer.BlockCopy(peerId, 0, b, 48, 20);
            return b;
        }

        // Начало буфера похоже на открытое рукопожатие (сколько байт есть — столько и сверяется, до 20).
        public static bool LooksPlain(byte[] b, int off, int count)
        {
            if (count < 1 || b[off] != 19) return false;
            int n = Math.Min(count - 1, 19);
            for (int i = 0; i < n; i++) if (b[off + 1 + i] != Protocol[i]) return false;
            return true;
        }

        public static bool ParseHandshake(byte[] b, int off, out byte[] reserved, out byte[] infoHash, out byte[] peerId)
        {
            reserved = infoHash = peerId = null;
            if (b == null || off < 0 || off + HandshakeLength > b.Length || !LooksPlain(b, off, 20)) return false;
            reserved = new byte[8];
            infoHash = new byte[20];
            peerId = new byte[20];
            Buffer.BlockCopy(b, off + 20, reserved, 0, 8);
            Buffer.BlockCopy(b, off + 28, infoHash, 0, 20);
            Buffer.BlockCopy(b, off + 48, peerId, 0, 20);
            return true;
        }

        public static bool SupportsExtended(byte[] reserved) { return reserved != null && (reserved[5] & 0x10) != 0; }
        public static bool SupportsFast(byte[] reserved) { return reserved != null && (reserved[7] & 0x04) != 0; }
        public static bool SupportsDht(byte[] reserved) { return reserved != null && (reserved[7] & 0x01) != 0; }

        // ---------- кадры ----------
        // Полный размер первого кадра (с 4 байтами длины): 0 — ещё не весь в буфере, -1 — длина вне пределов.
        // need — сколько байт нужно для кадра целиком (для роста буфера).
        public static int FrameSize(byte[] b, int off, int count, out int need)
        {
            need = 4;
            if (count < 4) return 0;
            int len = ReadInt(b, off);
            if (len < 0 || len > MaxMessage) return -1;
            need = 4 + len;
            if (len > MaxPieceMessage)
            {
                if (count < 5) return 0;
                if (b[off + 4] == Piece) return -1;
            }
            return count < need ? 0 : need;
        }

        public static int ReadInt(byte[] b, int off)
        {
            return (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];
        }

        public static void WriteInt(byte[] b, int off, int v)
        {
            b[off] = (byte)(v >> 24);
            b[off + 1] = (byte)(v >> 16);
            b[off + 2] = (byte)(v >> 8);
            b[off + 3] = (byte)v;
        }

        public static byte[] KeepAlive() { return new byte[4]; }

        public static byte[] Simple(byte id)
        {
            byte[] b = new byte[5];
            WriteInt(b, 0, 1);
            b[4] = id;
            return b;
        }

        // have, suggest, allowed fast
        public static byte[] WithIndex(byte id, int index)
        {
            byte[] b = new byte[9];
            WriteInt(b, 0, 5);
            b[4] = id;
            WriteInt(b, 5, index);
            return b;
        }

        // request, cancel, reject
        public static byte[] Block(byte id, int piece, int begin, int length)
        {
            byte[] b = new byte[17];
            WriteInt(b, 0, 13);
            b[4] = id;
            WriteInt(b, 5, piece);
            WriteInt(b, 9, begin);
            WriteInt(b, 13, length);
            return b;
        }

        public static byte[] BitfieldMsg(byte[] bits)
        {
            byte[] b = new byte[5 + bits.Length];
            WriteInt(b, 0, 1 + bits.Length);
            b[4] = Bitfield;
            Buffer.BlockCopy(bits, 0, b, 5, bits.Length);
            return b;
        }

        public static byte[] PieceMsg(int piece, int begin, byte[] data, int offset, int count)
        {
            byte[] b = new byte[13 + count];
            WriteInt(b, 0, 9 + count);
            b[4] = Piece;
            WriteInt(b, 5, piece);
            WriteInt(b, 9, begin);
            Buffer.BlockCopy(data, offset, b, 13, count);
            return b;
        }

        public static byte[] PortMsg(int port)
        {
            byte[] b = new byte[7];
            WriteInt(b, 0, 3);
            b[4] = Port;
            b[5] = (byte)(port >> 8);
            b[6] = (byte)port;
            return b;
        }

        public static byte[] ExtendedMsg(int extId, byte[] payload)
        {
            int n = payload == null ? 0 : payload.Length;
            byte[] b = new byte[6 + n];
            WriteInt(b, 0, 2 + n);
            b[4] = Extended;
            b[5] = (byte)extId;
            if (n > 0) Buffer.BlockCopy(payload, 0, b, 6, n);
            return b;
        }

        // ---------- BEP 6: набор allowed fast ----------
        // Алгоритм из спецификации: SHA-1 от (IPv4 & 255.255.255.0) + info-hash, по 4 байта на индекс, пока не наберётся k.
        public static List<int> AllowedFastSet(IPAddress ip, byte[] infoHash, int pieceCount, int k)
        {
            List<int> set = new List<int>();
            if (ip == null || ip.AddressFamily != AddressFamily.InterNetwork || infoHash == null || infoHash.Length != 20 || pieceCount <= 0) return set;
            k = Math.Min(k, pieceCount);
            byte[] a = ip.GetAddressBytes();
            byte[] x = new byte[24];
            x[0] = a[0];
            x[1] = a[1];
            x[2] = a[2];
            Buffer.BlockCopy(infoHash, 0, x, 4, 20);
            using (SHA1 sha = SHA1.Create())
                while (set.Count < k)
                {
                    x = sha.ComputeHash(x);
                    for (int i = 0; i < 5 && set.Count < k; i++)
                    {
                        uint y = (uint)ReadInt(x, i * 4);
                        int index = (int)(y % (uint)pieceCount);
                        if (!set.Contains(index)) set.Add(index);
                    }
                }
            return set;
        }
    }

    // ------------------------------------------------------------------ //
    //  BEP 10: расширенное рукопожатие
    // ------------------------------------------------------------------ //
    internal sealed class BtExtHandshake
    {
        public const int DefaultReqQ = 250;

        public readonly Dictionary<string, int> M = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Disabled = new List<string>();     // 'm' с номером 0 — пир выключил расширение
        public int ListenPort;
        public string Client = "";
        public int ReqQ = DefaultReqQ;
        public IPAddress YourIp;
        public BVal Root;

        // names[i] — имя расширения с номером i + 1 (null — места нет, номер пропускается).
        public static byte[] Build(IList<string> names, int listenPort, IPAddress yourIp, IList<IBtExtension> fill)
        {
            BVal root = BVal.NewDict();
            BVal m = BVal.NewDict();
            for (int i = 0; i < names.Count; i++)
                if (!string.IsNullOrEmpty(names[i])) m.Set(names[i], BVal.Int(i + 1));
            root.Set("m", m);
            if (listenPort > 0 && listenPort <= 65535) root.Set("p", BVal.Int(listenPort));
            root.Set("reqq", BVal.Int(DefaultReqQ));
            root.Set("v", BVal.Str(BtContext.ClientName));
            if (yourIp != null) root.Set("yourip", BVal.Bytes(yourIp.GetAddressBytes()));
            if (fill != null)
                foreach (IBtExtension e in fill)
                    if (e != null)
                    {
                        try { e.FillHandshake(root); }
                        catch (Exception ex) { DlLog.Report(ex); }
                    }
            return Bencode.Encode(root);
        }

        // null — не словарь bencode или больше предела.
        public static BtExtHandshake Parse(byte[] data, int offset, int count)
        {
            if (count <= 0 || count > BtWire.MaxExtHandshake) return null;
            byte[] copy = new byte[count];
            Buffer.BlockCopy(data, offset, copy, 0, count);
            string error;
            BVal root = Bencode.Decode(copy, out error);
            if (root == null || root.Kind != BKind.Dict) return null;
            BtExtHandshake h = new BtExtHandshake();
            h.Root = root;
            BVal m = root.Get("m", BKind.Dict);
            if (m != null)
                foreach (KeyValuePair<byte[], BVal> kv in m.D)
                {
                    if (kv.Value.Kind != BKind.Int || kv.Key.Length == 0 || kv.Key.Length > 64) continue;
                    string name = Encoding.UTF8.GetString(kv.Key);
                    if (kv.Value.I > 0 && kv.Value.I < 256) h.M[name] = (int)kv.Value.I;
                    else if (kv.Value.I == 0) { h.M.Remove(name); h.Disabled.Add(name); }
                }
            long p = root.GetInt("p", 0);
            if (p > 0 && p <= 65535) h.ListenPort = (int)p;
            string v = root.GetStr("v");
            if (v != null) h.Client = Printable(v, 64);
            long reqq = root.GetInt("reqq", DefaultReqQ);
            h.ReqQ = (int)Math.Max(1, Math.Min(2000, reqq));
            byte[] ip = root.GetBytes("yourip");
            if (ip != null && (ip.Length == 4 || ip.Length == 16)) h.YourIp = new IPAddress(ip);
            return h;
        }

        // Имя клиента показывается в карточке: без управляющих символов и не длиннее предела.
        private static string Printable(string s, int max)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (sb.Length >= max) break;
                if (!char.IsControl(c)) sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
