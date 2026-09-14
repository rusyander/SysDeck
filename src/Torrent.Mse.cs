// Windows Process Cleaner — «Загрузки», торренты: шифрование соединений MSE/PE (Message Stream Encryption).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// A — кто соединяется, B — кто принимает:
//   1 A→B  Ya, PadA
//   2 B→A  Yb, PadB
//   3 A→B  HASH('req1', S), HASH('req2', SKEY) xor HASH('req3', S), ENCRYPT(VC, crypto_provide, len(PadC), PadC, len(IA)),
//          ENCRYPT(IA)
//   4 B→A  ENCRYPT(VC, crypto_select, len(PadD), PadD), дальше поток
// DH на 768-битном простом из спецификации, G = 2, закрытый ключ 160 бит; RC4 без первых 1024 байт ключевого потока.
// Границ сообщений в протоколе нет: B ищет HASH('req1', S) после Ya, A — зашифрованный VC после Yb, не дальше 512 байт
// заполнителя. Не нашлось в этом окне — это не MSE: рукопожатие проваливается сразу, а не копит чужие байты.
// RC4 и DH-768 от активного перехватчика давно не защищают; смысл MSE — обход шейпинга провайдера, не тайна переписки.
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    // RC4 на месте; один экземпляр — одно направление одного соединения, без блокировок.
    internal sealed class BtRc4 : IBtCipher
    {
        private readonly byte[] _s = new byte[256];
        private int _i, _j;

        public BtRc4(byte[] key, int drop)
        {
            for (int k = 0; k < 256; k++) _s[k] = (byte)k;
            int j = 0;
            for (int k = 0; k < 256; k++)
            {
                j = (j + _s[k] + key[k % key.Length]) & 0xFF;
                byte t = _s[k]; _s[k] = _s[j]; _s[j] = t;
            }
            if (drop > 0) Apply(new byte[drop], 0, drop);
        }

        public void Apply(byte[] data, int offset, int count)
        {
            byte[] s = _s;
            int i = _i, j = _j;
            for (int n = 0; n < count; n++)
            {
                i = (i + 1) & 0xFF;
                j = (j + s[i]) & 0xFF;
                byte t = s[i]; s[i] = s[j]; s[j] = t;
                data[offset + n] ^= s[(s[i] + s[j]) & 0xFF];
            }
            _i = i; _j = j;
        }
    }

    internal static class BtMse
    {
        public const int KeySize = 96;
        public const int MaxPad = 512;
        public const int CryptoPlain = 1, CryptoRc4 = 2;
        public const int RcDrop = 1024;
        // Окно синхронизации: B — Ya + PadA + HASH('req1') = 628 байт; A — Yb + PadB + VC = 616 байт.
        public const int SyncLimitIncoming = KeySize + MaxPad + 20;
        public const int SyncLimitOutgoing = KeySize + MaxPad + 8;
        // IA у честных клиентов — рукопожатие BitTorrent (68 байт); больше нескольких килобайт не бывает.
        public const int MaxIa = 4096;

        private static readonly BigInteger Prime = FromHex(
            "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B" +
            "302B0A6DF25F14374FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563");

        public static IBtStreamHandshake Outgoing(byte[] infoHash, BtEncryption mode)
        {
            return CreateOutgoing(infoHash, mode, null, -1, 0);
        }

        // req2Lookup: HASH('req2', SKEY) → info-hash торрента или null (см. Req2).
        public static IBtStreamHandshake Incoming(Func<byte[], byte[]> req2Lookup, BtEncryption mode)
        {
            return CreateIncoming(req2Lookup, mode, -1, 0);
        }

        // Ключ поиска торрента во входящем рукопожатии: сессия считает его заранее для каждого торрента.
        public static byte[] Req2(byte[] infoHash) { return Sha1(Ascii("req2"), infoHash); }

        // ia — начальные данные (рукопожатие BitTorrent) внутри шага 3; padAB < 0 — случайный 0..512; padCD — длина PadC/PadD.
        internal static IBtStreamHandshake CreateOutgoing(byte[] infoHash, BtEncryption mode, byte[] ia, int padAB, int padCD)
        {
            if (infoHash == null || infoHash.Length != 20) throw new ArgumentException("infoHash");
            return new Handshake(true, mode, infoHash, null, ia, padAB, padCD);
        }

        internal static IBtStreamHandshake CreateIncoming(Func<byte[], byte[]> req2Lookup, BtEncryption mode, int padAB, int padCD)
        {
            if (req2Lookup == null) throw new ArgumentNullException("req2Lookup");
            return new Handshake(false, mode, null, req2Lookup, null, padAB, padCD);
        }

        // ---------- DH ----------
        internal static byte[] DhPublic(byte[] privateKey)
        {
            return ToBytes(BigInteger.ModPow(2, FromBytes(privateKey, 0, privateKey.Length), Prime));
        }

        // null — чужой ключ вырожденный (0, 1, P−1 и вне поля): такой общий секрет предсказуем.
        internal static byte[] DhSecret(byte[] privateKey, byte[] otherPublic, int offset)
        {
            BigInteger y = FromBytes(otherPublic, offset, KeySize);
            if (y <= BigInteger.One || y >= Prime - BigInteger.One) return null;
            return ToBytes(BigInteger.ModPow(y, FromBytes(privateKey, 0, privateKey.Length), Prime));
        }

        private static BigInteger FromBytes(byte[] be, int offset, int count)
        {
            byte[] le = new byte[count + 1];
            for (int i = 0; i < count; i++) le[i] = be[offset + count - 1 - i];
            return new BigInteger(le);
        }

        private static byte[] ToBytes(BigInteger v)
        {
            byte[] le = v.ToByteArray();
            byte[] be = new byte[KeySize];
            for (int i = 0; i < le.Length && i < KeySize; i++) be[KeySize - 1 - i] = le[i];
            return be;
        }

        private static BigInteger FromHex(string hex)
        {
            byte[] b = Bencode.FromHex(hex);
            return FromBytes(b, 0, b.Length);
        }

        internal static byte[] Sha1(params byte[][] parts)
        {
            using (SHA1 h = SHA1.Create())
            {
                foreach (byte[] p in parts) h.TransformBlock(p, 0, p.Length, null, 0);
                h.TransformFinalBlock(new byte[0], 0, 0);
                return h.Hash;
            }
        }

        private static byte[] Ascii(string s) { return Encoding.ASCII.GetBytes(s); }

        // ------------------------------------------------------------------ //
        //  Рукопожатие одной стороны
        // ------------------------------------------------------------------ //
        private sealed class Handshake : IBtStreamHandshake
        {
            private readonly bool _outgoing;
            private readonly BtEncryption _mode;
            private readonly Func<byte[], byte[]> _lookup;
            private readonly byte[] _ia;
            private readonly int _padAB, _padCD;
            private byte[] _skey;
            private byte[] _private, _secret, _sync;
            private byte[] _buf = new byte[1024];
            private int _len, _pos, _searchFrom, _step, _padLen, _iaLen, _provide;
            private BtRc4 _enc, _dec;
            private BtHandshakeState _state = BtHandshakeState.NeedMore;
            private IBtCipher _encryptor, _decryptor;
            private byte[] _remaining;
            private string _error;

            public Handshake(bool outgoing, BtEncryption mode, byte[] skey, Func<byte[], byte[]> lookup, byte[] ia, int padAB, int padCD)
            {
                _outgoing = outgoing;
                _mode = mode;
                _skey = skey;
                _lookup = lookup;
                _ia = ia ?? new byte[0];
                _padAB = padAB;
                _padCD = Math.Max(0, Math.Min(MaxPad, padCD));
                if (_ia.Length > MaxIa) throw new ArgumentException("ia");
            }

            public IBtCipher Encryptor { get { return _encryptor; } }
            public IBtCipher Decryptor { get { return _decryptor; } }
            public byte[] InfoHash { get { return _state == BtHandshakeState.Done ? _skey : null; } }
            public byte[] Remaining { get { return _remaining ?? new byte[0]; } }
            public string Error { get { return _error; } }

            public void Begin(List<byte[]> send)
            {
                if (!_outgoing || _step != 0) return;
                send.Add(KeyAndPad());
                _step = 1;
            }

            public BtHandshakeState Feed(byte[] data, int offset, int count, List<byte[]> send)
            {
                if (_state != BtHandshakeState.NeedMore) return _state;
                if (data == null || offset < 0 || count < 0 || offset + count > data.Length) return Fail(Tr.S("неверный буфер", "bad buffer"));
                if (_outgoing && _step == 0) return Fail(Tr.S("рукопожатие не начато", "handshake was not started"));
                Append(data, offset, count);
                try
                {
                    while (_state == BtHandshakeState.NeedMore && (_outgoing ? StepOutgoing(send) : StepIncoming(send))) { }
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
                return _state;
            }

            // ---------- A ----------
            private bool StepOutgoing(List<byte[]> send)
            {
                switch (_step)
                {
                    case 1:
                        {
                            if (_len < KeySize) return false;
                            _secret = DhSecret(_private, _buf, 0);
                            if (_secret == null) { Fail(Tr.S("недопустимый ключ DH", "invalid DH key")); return false; }
                            _enc = new BtRc4(Sha1(Ascii("keyA"), _secret, _skey), RcDrop);
                            _dec = new BtRc4(Sha1(Ascii("keyB"), _secret, _skey), RcDrop);
                            byte[] x = Xor(Sha1(Ascii("req2"), _skey), Sha1(Ascii("req3"), _secret));
                            int provide = _mode == BtEncryption.Require ? CryptoRc4 : _mode == BtEncryption.Off ? CryptoPlain : CryptoRc4 | CryptoPlain;
                            byte[] body = new byte[8 + 4 + 2 + _padCD + 2 + _ia.Length];
                            PutInt(body, 8, provide);
                            PutShort(body, 12, _padCD);
                            PutShort(body, 14 + _padCD, _ia.Length);
                            Buffer.BlockCopy(_ia, 0, body, 16 + _padCD, _ia.Length);
                            _enc.Apply(body, 0, body.Length);
                            byte[] msg = new byte[40 + body.Length];
                            Buffer.BlockCopy(Sha1(Ascii("req1"), _secret), 0, msg, 0, 20);
                            Buffer.BlockCopy(x, 0, msg, 20, 20);
                            Buffer.BlockCopy(body, 0, msg, 40, body.Length);
                            send.Add(msg);
                            // Ищем VC уже зашифрованным: 8 нулей через ключевой поток B — поток при этом продвигается ровно на VC.
                            _sync = new byte[8];
                            _dec.Apply(_sync, 0, 8);
                            _provide = provide;
                            _searchFrom = KeySize;
                            _step = 2;
                            return true;
                        }
                    case 2:
                        if (!Search(SyncLimitOutgoing)) return false;
                        _step = 3;
                        return true;
                    case 3:
                        {
                            if (_len < _pos + 6) return false;
                            _dec.Apply(_buf, _pos, 6);
                            int select = GetInt(_buf, _pos);
                            _padLen = GetShort(_buf, _pos + 4);
                            _pos += 6;
                            if (_padLen > MaxPad) { Fail(Tr.S("слишком длинный PadD", "PadD too long")); return false; }
                            if ((select != CryptoRc4 && select != CryptoPlain) || (select & _provide) == 0)
                            {
                                Fail(Tr.S("пир выбрал не предложенный способ шифрования", "peer selected a method that was not offered"));
                                return false;
                            }
                            _provide = select;
                            _step = 4;
                            return true;
                        }
                    case 4:
                        if (_len < _pos + _padLen) return false;
                        _dec.Apply(_buf, _pos, _padLen);
                        _pos += _padLen;
                        Finish(_provide == CryptoRc4);
                        return false;
                }
                return false;
            }

            // ---------- B ----------
            private bool StepIncoming(List<byte[]> send)
            {
                switch (_step)
                {
                    case 0:
                        {
                            if (_len < KeySize) return false;
                            send.Add(KeyAndPad());
                            _secret = DhSecret(_private, _buf, 0);
                            if (_secret == null) { Fail(Tr.S("недопустимый ключ DH", "invalid DH key")); return false; }
                            _sync = Sha1(Ascii("req1"), _secret);
                            _searchFrom = KeySize;
                            _step = 1;
                            return true;
                        }
                    case 1:
                        if (!Search(SyncLimitIncoming)) return false;
                        _step = 2;
                        return true;
                    case 2:
                        {
                            if (_len < _pos + 20) return false;
                            byte[] x = new byte[20];
                            Buffer.BlockCopy(_buf, _pos, x, 0, 20);
                            _pos += 20;
                            byte[] req2 = Xor(x, Sha1(Ascii("req3"), _secret));
                            byte[] hash = null;
                            try { hash = _lookup(req2); }
                            catch { hash = null; }
                            if (hash == null || hash.Length != 20) { Fail(Tr.S("неизвестный торрент", "unknown torrent")); return false; }
                            _skey = hash;
                            _dec = new BtRc4(Sha1(Ascii("keyA"), _secret, _skey), RcDrop);
                            _enc = new BtRc4(Sha1(Ascii("keyB"), _secret, _skey), RcDrop);
                            _step = 3;
                            return true;
                        }
                    case 3:
                        {
                            if (_len < _pos + 14) return false;
                            _dec.Apply(_buf, _pos, 14);
                            for (int i = 0; i < 8; i++)
                                if (_buf[_pos + i] != 0) { Fail(Tr.S("неверный VC", "bad verification constant")); return false; }
                            _provide = GetInt(_buf, _pos + 8);
                            _padLen = GetShort(_buf, _pos + 12);
                            _pos += 14;
                            if (_padLen > MaxPad) { Fail(Tr.S("слишком длинный PadC", "PadC too long")); return false; }
                            _step = 4;
                            return true;
                        }
                    case 4:
                        if (_len < _pos + _padLen + 2) return false;
                        _dec.Apply(_buf, _pos, _padLen + 2);
                        _iaLen = GetShort(_buf, _pos + _padLen);
                        _pos += _padLen + 2;
                        if (_iaLen > MaxIa) { Fail(Tr.S("слишком длинный IA", "IA too long")); return false; }
                        _step = 5;
                        return true;
                    case 5:
                        {
                            if (_len < _pos + _iaLen) return false;
                            int select;
                            if ((_provide & CryptoRc4) != 0 && _mode != BtEncryption.Off) select = CryptoRc4;
                            else if ((_provide & CryptoPlain) != 0 && _mode != BtEncryption.Require) select = CryptoPlain;
                            else { Fail(Tr.S("нет общего способа шифрования", "no common encryption method")); return false; }
                            _dec.Apply(_buf, _pos, _iaLen);
                            byte[] reply = new byte[8 + 4 + 2 + _padCD];
                            PutInt(reply, 8, select);
                            PutShort(reply, 12, _padCD);
                            _enc.Apply(reply, 0, reply.Length);
                            send.Add(reply);
                            // IA уже расшифрован, а поток после него — зашифрован только при RC4.
                            int iaStart = _pos;
                            _pos += _iaLen;
                            byte[] rest = TakeRest(select == CryptoRc4);
                            _remaining = new byte[_iaLen + rest.Length];
                            Buffer.BlockCopy(_buf, iaStart, _remaining, 0, _iaLen);
                            Buffer.BlockCopy(rest, 0, _remaining, _iaLen, rest.Length);
                            SetDone(select == CryptoRc4);
                            return false;
                        }
                }
                return false;
            }

            // Поиск _sync в окне; true — найден, _pos за ним. Окно исчерпано — рукопожатие проваливается.
            private bool Search(int limit)
            {
                int n = _sync.Length;
                for (; _searchFrom + n <= _len && _searchFrom + n <= limit; _searchFrom++)
                {
                    bool hit = true;
                    for (int i = 0; i < n && hit; i++) hit = _buf[_searchFrom + i] == _sync[i];
                    if (hit)
                    {
                        _pos = _searchFrom + n;
                        return true;
                    }
                }
                if (_len >= limit) Fail(Tr.S("нет синхронизации MSE — это не зашифрованный пир", "no MSE synchronisation — not an encrypted peer"));
                return false;
            }

            private void Finish(bool rc4)
            {
                _remaining = TakeRest(rc4);
                SetDone(rc4);
            }

            private byte[] TakeRest(bool rc4)
            {
                byte[] rest = new byte[_len - _pos];
                Buffer.BlockCopy(_buf, _pos, rest, 0, rest.Length);
                if (rc4) _dec.Apply(rest, 0, rest.Length);
                _pos = _len;
                return rest;
            }

            private void SetDone(bool rc4)
            {
                _encryptor = rc4 ? _enc : null;
                _decryptor = rc4 ? _dec : null;
                _private = null;
                _state = BtHandshakeState.Done;
            }

            private byte[] KeyAndPad()
            {
                byte[] pad;
                using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
                {
                    _private = new byte[20];
                    rng.GetBytes(_private);
                    int padLen = _padAB;
                    if (padLen < 0)
                    {
                        byte[] r = new byte[2];
                        rng.GetBytes(r);
                        padLen = ((r[0] << 8) | r[1]) % (MaxPad + 1);
                    }
                    pad = new byte[Math.Min(padLen, MaxPad)];
                    rng.GetBytes(pad);
                }
                byte[] y = DhPublic(_private);
                byte[] msg = new byte[KeySize + pad.Length];
                Buffer.BlockCopy(y, 0, msg, 0, KeySize);
                Buffer.BlockCopy(pad, 0, msg, KeySize, pad.Length);
                return msg;
            }

            private void Append(byte[] data, int offset, int count)
            {
                if (_len + count > _buf.Length)
                {
                    byte[] nb = new byte[Math.Max(_buf.Length * 2, _len + count)];
                    Buffer.BlockCopy(_buf, 0, nb, 0, _len);
                    _buf = nb;
                }
                Buffer.BlockCopy(data, offset, _buf, _len, count);
                _len += count;
            }

            private BtHandshakeState Fail(string why)
            {
                _error = "MSE: " + why;
                _state = BtHandshakeState.Failed;
                _enc = _dec = null;
                _private = null;
                return _state;
            }
        }

        private static byte[] Xor(byte[] a, byte[] b)
        {
            byte[] r = new byte[a.Length];
            for (int i = 0; i < r.Length; i++) r[i] = (byte)(a[i] ^ b[i]);
            return r;
        }

        private static void PutInt(byte[] b, int at, int v) { b[at] = (byte)(v >> 24); b[at + 1] = (byte)(v >> 16); b[at + 2] = (byte)(v >> 8); b[at + 3] = (byte)v; }
        private static void PutShort(byte[] b, int at, int v) { b[at] = (byte)(v >> 8); b[at + 1] = (byte)v; }
        private static int GetInt(byte[] b, int at) { return (b[at] << 24) | (b[at + 1] << 16) | (b[at + 2] << 8) | b[at + 3]; }
        private static int GetShort(byte[] b, int at) { return (b[at] << 8) | b[at + 1]; }
    }
}
