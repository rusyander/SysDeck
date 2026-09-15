// SysDeck — «Загрузки», торренты: bencode (BEP 3) — разбор и запись.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Всё, что сюда приходит, — чужие байты: .torrent из интернета, сообщения пиров, ответы трекеров и узлов DHT. Поэтому
// разбор строгий (числа без ведущих нулей и «-0», длина строки не больше оставшихся байт, ключи словаря только строки)
// и ограниченный: глубина вложенности и число элементов не дают одной посылке съесть стек или память.
// Ключи словаря — байты, а не текст: в `piece layers` (BEP 52) ключ — сырой 32-байтовый хеш.
// Каждый элемент помнит своё место в исходном буфере: info-hash считается по байтам словаря `info` ровно так, как они
// лежат в файле, даже если файл записан не в каноническом порядке ключей.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SysDeck.Downloads
{
    internal enum BKind { Int, Bytes, List, Dict }

    internal sealed class BVal
    {
        public BKind Kind;
        public long I;
        public byte[] B;
        public List<BVal> L;
        public List<KeyValuePair<byte[], BVal>> D;
        public int Start;             // смещение первого байта элемента в разобранном буфере
        public int End;               // смещение за последним байтом

        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        // ---------- создание ----------
        public static BVal Int(long v) { BVal b = new BVal(); b.Kind = BKind.Int; b.I = v; return b; }
        public static BVal Bytes(byte[] v) { BVal b = new BVal(); b.Kind = BKind.Bytes; b.B = v ?? new byte[0]; return b; }
        public static BVal Str(string v) { return Bytes(Utf8.GetBytes(v ?? "")); }
        public static BVal NewList() { BVal b = new BVal(); b.Kind = BKind.List; b.L = new List<BVal>(); return b; }
        public static BVal NewDict() { BVal b = new BVal(); b.Kind = BKind.Dict; b.D = new List<KeyValuePair<byte[], BVal>>(); return b; }

        // Ключ заменяется, если уже есть: словарь без повторов.
        public BVal Set(string key, BVal value) { return Set(Utf8.GetBytes(key), value); }

        public BVal Set(byte[] key, BVal value)
        {
            for (int i = 0; i < D.Count; i++)
                if (Bencode.SameBytes(D[i].Key, key)) { D[i] = new KeyValuePair<byte[], BVal>(key, value); return this; }
            D.Add(new KeyValuePair<byte[], BVal>(key, value));
            return this;
        }

        public BVal Add(BVal value) { L.Add(value); return this; }

        // ---------- чтение ----------
        public BVal Get(string key)
        {
            if (Kind != BKind.Dict) return null;
            byte[] k = Utf8.GetBytes(key);
            foreach (KeyValuePair<byte[], BVal> kv in D) if (Bencode.SameBytes(kv.Key, k)) return kv.Value;
            return null;
        }

        public BVal Get(string key, BKind kind)
        {
            BVal v = Get(key);
            return v != null && v.Kind == kind ? v : null;
        }

        public long GetInt(string key, long fallback)
        {
            BVal v = Get(key, BKind.Int);
            return v == null ? fallback : v.I;
        }

        public byte[] GetBytes(string key)
        {
            BVal v = Get(key, BKind.Bytes);
            return v == null ? null : v.B;
        }

        // Текст UTF-8; битые последовательности заменяются на U+FFFD, а не роняют разбор.
        public string GetStr(string key)
        {
            byte[] b = GetBytes(key);
            return b == null ? null : Utf8.GetString(b);
        }

        public string Text { get { return Kind == BKind.Bytes ? Utf8.GetString(B) : null; } }
    }

    internal static class Bencode
    {
        public const int MaxDepth = 64;
        public const int MaxItems = 4000000;       // 200 ГБ по 64 КБ — около трёх миллионов кусков в списке хешей v2

        public static bool SameBytes(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        // Весь буфер — ровно один элемент, без хвоста.
        public static BVal Decode(byte[] data, out string error)
        {
            if (data == null) { error = "no data"; return null; }
            int consumed;
            BVal v = DecodePrefix(data, 0, data.Length, out consumed, out error);
            if (v != null && consumed != data.Length) { error = "trailing bytes after the root element"; return null; }
            return v;
        }

        // Первый элемент буфера; consumed — сколько байт он занял (ut_metadata: за словарём идут сырые данные куска).
        public static BVal DecodePrefix(byte[] data, int offset, int count, out int consumed, out string error)
        {
            consumed = 0;
            error = null;
            if (data == null || offset < 0 || count < 0 || offset + count > data.Length) { error = "bad range"; return null; }
            Reader r = new Reader(data, offset, offset + count);
            try
            {
                BVal v = r.Value(0);
                consumed = r.Pos - offset;
                return v;
            }
            catch (FormatException ex)
            {
                error = ex.Message + " at " + (r.Pos - offset).ToString(CultureInfo.InvariantCulture);
                return null;
            }
        }

        private sealed class Reader
        {
            private readonly byte[] _d;
            private readonly int _end;
            public int Pos;
            private int _items;

            public Reader(byte[] d, int start, int end) { _d = d; Pos = start; _end = end; }

            private static FormatException Bad(string what) { return new FormatException(what); }

            public BVal Value(int depth)
            {
                if (depth > MaxDepth) throw Bad("nesting too deep");
                if (++_items > MaxItems) throw Bad("too many elements");
                if (Pos >= _end) throw Bad("unexpected end");
                int start = Pos;
                byte c = _d[Pos];
                BVal v;
                if (c == (byte)'i')
                {
                    Pos++;
                    v = BVal.Int(Number((byte)'e', true));
                }
                else if (c >= (byte)'0' && c <= (byte)'9')
                {
                    long len = Number((byte)':', false);
                    if (len > _end - Pos) throw Bad("string longer than the data");
                    byte[] b = new byte[len];
                    Buffer.BlockCopy(_d, Pos, b, 0, (int)len);
                    Pos += (int)len;
                    v = BVal.Bytes(b);
                }
                else if (c == (byte)'l')
                {
                    Pos++;
                    v = BVal.NewList();
                    while (true)
                    {
                        if (Pos >= _end) throw Bad("unterminated list");
                        if (_d[Pos] == (byte)'e') { Pos++; break; }
                        v.L.Add(Value(depth + 1));
                    }
                }
                else if (c == (byte)'d')
                {
                    Pos++;
                    v = BVal.NewDict();
                    while (true)
                    {
                        if (Pos >= _end) throw Bad("unterminated dictionary");
                        if (_d[Pos] == (byte)'e') { Pos++; break; }
                        if (_d[Pos] < (byte)'0' || _d[Pos] > (byte)'9') throw Bad("dictionary key is not a string");
                        BVal key = Value(depth + 1);
                        BVal val = Value(depth + 1);
                        v.D.Add(new KeyValuePair<byte[], BVal>(key.B, val));
                    }
                }
                else throw Bad("unexpected byte 0x" + c.ToString("x2", CultureInfo.InvariantCulture));
                v.Start = start;
                v.End = Pos;
                return v;
            }

            // Десятичное число до terminator. Для длины строки знак запрещён.
            private long Number(byte terminator, bool signed)
            {
                bool negative = false;
                if (signed && Pos < _end && _d[Pos] == (byte)'-') { negative = true; Pos++; }
                int digitsStart = Pos;
                long n = 0;
                while (true)
                {
                    if (Pos >= _end) throw Bad("unterminated number");
                    byte b = _d[Pos];
                    if (b == terminator) break;
                    if (b < (byte)'0' || b > (byte)'9') throw Bad("bad digit in a number");
                    if (n > (long.MaxValue - (b - '0')) / 10) throw Bad("number overflow");
                    n = n * 10 + (b - '0');
                    Pos++;
                }
                int digits = Pos - digitsStart;
                if (digits == 0) throw Bad("empty number");
                if (digits > 1 && _d[digitsStart] == (byte)'0') throw Bad("leading zero in a number");
                if (negative && n == 0) throw Bad("negative zero");
                Pos++;
                return negative ? -n : n;
            }
        }

        // ---------- запись: ключи словаря в порядке сырых байт (канон BEP 3) ----------
        public static byte[] Encode(BVal v)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                Write(ms, v, 0);
                return ms.ToArray();
            }
        }

        private static void Write(Stream s, BVal v, int depth)
        {
            if (depth > MaxDepth) throw new InvalidOperationException("bencode nesting too deep");
            switch (v.Kind)
            {
                case BKind.Int:
                    Ascii(s, "i" + v.I.ToString(CultureInfo.InvariantCulture) + "e");
                    break;
                case BKind.Bytes:
                    WriteBytes(s, v.B);
                    break;
                case BKind.List:
                    s.WriteByte((byte)'l');
                    foreach (BVal e in v.L) Write(s, e, depth + 1);
                    s.WriteByte((byte)'e');
                    break;
                default:
                    List<KeyValuePair<byte[], BVal>> sorted = new List<KeyValuePair<byte[], BVal>>(v.D);
                    sorted.Sort(delegate(KeyValuePair<byte[], BVal> a, KeyValuePair<byte[], BVal> b) { return CompareBytes(a.Key, b.Key); });
                    s.WriteByte((byte)'d');
                    foreach (KeyValuePair<byte[], BVal> kv in sorted)
                    {
                        WriteBytes(s, kv.Key);
                        Write(s, kv.Value, depth + 1);
                    }
                    s.WriteByte((byte)'e');
                    break;
            }
        }

        private static void WriteBytes(Stream s, byte[] b)
        {
            Ascii(s, b.Length.ToString(CultureInfo.InvariantCulture) + ":");
            s.Write(b, 0, b.Length);
        }

        private static void Ascii(Stream s, string text)
        {
            byte[] b = Encoding.ASCII.GetBytes(text);
            s.Write(b, 0, b.Length);
        }

        public static int CompareBytes(byte[] a, byte[] b)
        {
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Length.CompareTo(b.Length);
        }

        public static string Hex(byte[] b)
        {
            if (b == null) return "";
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static byte[] FromHex(string hex)
        {
            if (hex == null || hex.Length % 2 != 0) return null;
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                int hi = HexDigit(hex[2 * i]), lo = HexDigit(hex[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                b[i] = (byte)(hi * 16 + lo);
            }
            return b;
        }

        private static int HexDigit(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }
    }
}
