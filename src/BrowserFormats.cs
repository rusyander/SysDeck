// SysDeck — форматы Chromium: Snappy, LevelDB, protobuf, SNSS, Pickle
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace SysDeck
{
    // Распаковка snappy — блоки LevelDB сжаты именно им.
    public static class Snappy
    {
        public static byte[] Decompress(byte[] src, int off, int len)
        {
            int p = off, end = off + len;
            int total = (int)ReadVarint(src, ref p, end);
            byte[] outBuf = new byte[total];
            int o = 0;
            while (p < end)
            {
                int tag = src[p++];
                int t = tag & 3;
                if (t == 0)
                {
                    int n = tag >> 2;
                    if (n >= 60)
                    {
                        int extra = n - 59;
                        n = 0;
                        for (int k = 0; k < extra; k++) n |= src[p + k] << (8 * k);
                        p += extra;
                    }
                    n++;
                    if (o + n > total || p + n > end) throw new InvalidDataException("snappy literal");
                    Buffer.BlockCopy(src, p, outBuf, o, n);
                    p += n; o += n;
                }
                else
                {
                    int n, offset;
                    if (t == 1)
                    {
                        n = 4 + ((tag >> 2) & 7);
                        offset = ((tag >> 5) << 8) | src[p]; p += 1;
                    }
                    else if (t == 2)
                    {
                        n = (tag >> 2) + 1;
                        offset = src[p] | (src[p + 1] << 8); p += 2;
                    }
                    else
                    {
                        n = (tag >> 2) + 1;
                        offset = src[p] | (src[p + 1] << 8) | (src[p + 2] << 16) | (src[p + 3] << 24); p += 4;
                    }
                    if (offset <= 0 || offset > o || o + n > total) throw new InvalidDataException("snappy copy");
                    int s = o - offset;
                    for (int k = 0; k < n; k++) outBuf[o + k] = outBuf[s + k];
                    o += n;
                }
            }
            if (o != total) throw new InvalidDataException("snappy size");
            return outBuf;
        }

        public static ulong ReadVarint(byte[] b, ref int i, int end)
        {
            ulong r = 0; int s = 0;
            while (i < end)
            {
                byte c = b[i++];
                r |= (ulong)(c & 0x7f) << s;
                if ((c & 0x80) == 0) return r;
                s += 7;
                if (s > 63) break;
            }
            throw new InvalidDataException("varint");
        }
    }

    // Читалка LevelDB «только на чтение», без записи и без compaction.
    // Полная реализация не нужна: берём все .ldb и .log и оставляем для каждого
    // ключа запись с наибольшим sequence number — это и есть актуальное значение.
    public static class LevelDbLite
    {
        private static readonly byte[] Magic = { 0x57, 0xfb, 0x80, 0x8b, 0x24, 0x75, 0x47, 0xdb };

        private struct Rec { public byte[] Val; public ulong Seq; public bool Live; }

        public static Dictionary<string, byte[]> ReadAll(string dir)
        {
            Dictionary<string, Rec> best = new Dictionary<string, Rec>(StringComparer.Ordinal);
            if (!Directory.Exists(dir)) return new Dictionary<string, byte[]>();

            foreach (string f in SafeFiles(dir, "*.ldb").Concat(SafeFiles(dir, "*.sst")))
                try { ReadTable(f, best); } catch { }
            foreach (string f in SafeFiles(dir, "*.log"))
                try { ReadLog(f, best); } catch { }

            Dictionary<string, byte[]> res = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Rec> kv in best)
                if (kv.Value.Live) res[kv.Key] = kv.Value.Val;
            return res;
        }

        private static IEnumerable<string> SafeFiles(string dir, string mask)
        {
            try { return Directory.GetFiles(dir, mask); }
            catch { return new string[0]; }
        }

        // Chrome держит свои файлы открытыми — без FileShare.ReadWrite|Delete
        // чтение падает с «файл используется другим процессом».
        public static byte[] ReadShared(string path)
        {
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                  FileShare.ReadWrite | FileShare.Delete))
            {
                byte[] b = new byte[fs.Length];
                int got = 0;
                while (got < b.Length)
                {
                    int n = fs.Read(b, got, b.Length - got);
                    if (n <= 0) break;
                    got += n;
                }
                if (got != b.Length) Array.Resize(ref b, got);
                return b;
            }
        }

        private static void Put(Dictionary<string, Rec> best, byte[] key, byte[] val, ulong seq, bool live)
        {
            string k = Encoding.UTF8.GetString(key);
            Rec cur;
            if (best.TryGetValue(k, out cur) && cur.Seq > seq) return;
            Rec r = new Rec(); r.Val = val; r.Seq = seq; r.Live = live;
            best[k] = r;
        }

        // ---- SSTable (.ldb) ----
        private static void ReadTable(string path, Dictionary<string, Rec> best)
        {
            byte[] b = ReadShared(path);
            if (b.Length < 48) return;
            for (int i = 0; i < 8; i++) if (b[b.Length - 8 + i] != Magic[i]) return;

            int p = b.Length - 48, end = b.Length - 8;
            Snappy.ReadVarint(b, ref p, end);                 // metaindex offset
            Snappy.ReadVarint(b, ref p, end);                 // metaindex size
            long idxOff = (long)Snappy.ReadVarint(b, ref p, end);
            long idxSize = (long)Snappy.ReadVarint(b, ref p, end);

            byte[] index = ReadBlock(b, idxOff, idxSize);
            if (index == null) return;
            foreach (KeyValuePair<byte[], byte[]> e in ParseBlock(index))
            {
                int q = 0;
                long off = (long)Snappy.ReadVarint(e.Value, ref q, e.Value.Length);
                long size = (long)Snappy.ReadVarint(e.Value, ref q, e.Value.Length);
                byte[] data;
                try { data = ReadBlock(b, off, size); } catch { continue; }
                if (data == null) continue;
                foreach (KeyValuePair<byte[], byte[]> kv in ParseBlock(data))
                {
                    byte[] ik = kv.Key;
                    if (ik.Length < 8) continue;
                    ulong trailer = BitConverter.ToUInt64(ik, ik.Length - 8);
                    byte[] uk = new byte[ik.Length - 8];
                    Buffer.BlockCopy(ik, 0, uk, 0, uk.Length);
                    Put(best, uk, kv.Value, trailer >> 8, (trailer & 0xff) == 1);
                }
            }
        }

        private static byte[] ReadBlock(byte[] b, long off, long size)
        {
            if (off < 0 || size < 0 || off + size + 1 > b.Length) return null;
            byte ctype = b[off + size];
            if (ctype == 0)
            {
                byte[] raw = new byte[size];
                Buffer.BlockCopy(b, (int)off, raw, 0, (int)size);
                return raw;
            }
            if (ctype == 1) return Snappy.Decompress(b, (int)off, (int)size);
            return null;                                        // zstd и прочее не поддерживаем
        }

        private static List<KeyValuePair<byte[], byte[]>> ParseBlock(byte[] blk)
        {
            List<KeyValuePair<byte[], byte[]>> res = new List<KeyValuePair<byte[], byte[]>>();
            if (blk.Length < 4) return res;
            int nRestarts = BitConverter.ToInt32(blk, blk.Length - 4);
            int limit = blk.Length - 4 - 4 * nRestarts;
            if (limit < 0 || limit > blk.Length) return res;
            int i = 0;
            byte[] key = new byte[0];
            while (i < limit)
            {
                int shared = (int)Snappy.ReadVarint(blk, ref i, limit);
                int nonShared = (int)Snappy.ReadVarint(blk, ref i, limit);
                int vlen = (int)Snappy.ReadVarint(blk, ref i, limit);
                if (shared == 0 && nonShared == 0 && vlen == 0) break;
                if (shared > key.Length || i + nonShared + vlen > blk.Length) break;
                byte[] k = new byte[shared + nonShared];
                Buffer.BlockCopy(key, 0, k, 0, shared);
                Buffer.BlockCopy(blk, i, k, shared, nonShared);
                i += nonShared;
                byte[] v = new byte[vlen];
                Buffer.BlockCopy(blk, i, v, 0, vlen);
                i += vlen;
                key = k;
                res.Add(new KeyValuePair<byte[], byte[]>(k, v));
            }
            return res;
        }

        // ---- журнал (.log): блоки по 32 КБ, записи с заголовком crc(4)+len(2)+type(1) ----
        private static void ReadLog(string path, Dictionary<string, Rec> best)
        {
            byte[] b = ReadShared(path);
            const int BLOCK = 32768;
            int i = 0;
            List<byte> pending = new List<byte>();
            List<byte[]> records = new List<byte[]>();
            while (i + 7 <= b.Length)
            {
                int left = BLOCK - (i % BLOCK);
                if (left < 7) { i += left; continue; }
                int len = b[i + 4] | (b[i + 5] << 8);
                int type = b[i + 6];
                i += 7;
                if (len == 0 && type == 0) { i += left - 7; continue; }
                if (i + len > b.Length) break;
                byte[] data = new byte[len];
                Buffer.BlockCopy(b, i, data, 0, len);
                i += len;
                if (type == 1) records.Add(data);
                else if (type == 2) { pending.Clear(); pending.AddRange(data); }
                else if (type == 3) pending.AddRange(data);
                else if (type == 4) { pending.AddRange(data); records.Add(pending.ToArray()); pending.Clear(); }
            }
            foreach (byte[] r in records)
            {
                if (r.Length < 12) continue;
                ulong seq = BitConverter.ToUInt64(r, 0);
                int count = BitConverter.ToInt32(r, 8);
                int j = 12;
                for (int n = 0; n < count && j < r.Length; n++)
                {
                    int t = r[j++];
                    int kl = (int)Snappy.ReadVarint(r, ref j, r.Length);
                    if (j + kl > r.Length) break;
                    byte[] key = new byte[kl];
                    Buffer.BlockCopy(r, j, key, 0, kl); j += kl;
                    byte[] val = new byte[0];
                    if (t == 1)
                    {
                        int vl = (int)Snappy.ReadVarint(r, ref j, r.Length);
                        if (j + vl > r.Length) break;
                        val = new byte[vl];
                        Buffer.BlockCopy(r, j, val, 0, vl); j += vl;
                    }
                    Put(best, key, val, seq, t == 1);
                    seq++;
                }
            }
        }
    }

    // Разбор protobuf «вслепую»: схема нам не нужна, достаточно номеров полей.
    public static class Pb
    {
        public static Dictionary<int, List<object>> Parse(byte[] b) { return Parse(b, 0, b.Length); }

        public static Dictionary<int, List<object>> Parse(byte[] b, int off, int len)
        {
            Dictionary<int, List<object>> res = new Dictionary<int, List<object>>();
            int i = off, end = off + len;
            while (i < end)
            {
                ulong tag;
                try { tag = Snappy.ReadVarint(b, ref i, end); }
                catch { break; }
                int field = (int)(tag >> 3), wire = (int)(tag & 7);
                if (field == 0) break;
                object val;
                if (wire == 0)
                {
                    try { val = Snappy.ReadVarint(b, ref i, end); } catch { break; }
                }
                else if (wire == 2)
                {
                    int l;
                    try { l = (int)Snappy.ReadVarint(b, ref i, end); } catch { break; }
                    if (l < 0 || i + l > end) break;
                    byte[] v = new byte[l];
                    Buffer.BlockCopy(b, i, v, 0, l);
                    i += l;
                    val = v;
                }
                else if (wire == 5) { if (i + 4 > end) break; i += 4; continue; }
                else if (wire == 1) { if (i + 8 > end) break; i += 8; continue; }
                else break;
                List<object> lst;
                if (!res.TryGetValue(field, out lst)) { lst = new List<object>(); res[field] = lst; }
                lst.Add(val);
            }
            return res;
        }

        public static byte[] Bytes(Dictionary<int, List<object>> m, int field)
        {
            List<object> l;
            if (m != null && m.TryGetValue(field, out l) && l.Count > 0) return l[0] as byte[];
            return null;
        }

        public static string Str(Dictionary<int, List<object>> m, int field)
        {
            byte[] b = Bytes(m, field);
            return b == null ? null : Encoding.UTF8.GetString(b);
        }

        public static ulong U64(Dictionary<int, List<object>> m, int field)
        {
            List<object> l;
            if (m != null && m.TryGetValue(field, out l) && l.Count > 0 && l[0] is ulong) return (ulong)l[0];
            return 0;
        }

        public static Dictionary<int, List<object>> Msg(Dictionary<int, List<object>> m, int field)
        {
            byte[] b = Bytes(m, field);
            return b == null ? null : Parse(b);
        }

        public static bool Has(Dictionary<int, List<object>> m, int field)
        {
            return m != null && m.ContainsKey(field);
        }
    }

    // Чтение файлов сеанса Chrome (формат SNSS) — окна, вкладки и группы.
    public static class Snss
    {
        public class Cmd { public byte Id; public byte[] Data; }

        public static List<Cmd> Read(string path)
        {
            List<Cmd> res = new List<Cmd>();
            byte[] b = LevelDbLite.ReadShared(path);
            if (b.Length < 8 || b[0] != 'S' || b[1] != 'N' || b[2] != 'S' || b[3] != 'S') return res;
            int i = 8;
            while (i + 2 <= b.Length)
            {
                int size = b[i] | (b[i + 1] << 8);
                i += 2;
                if (size == 0 || i + size > b.Length) break;
                Cmd c = new Cmd();
                c.Id = b[i];
                c.Data = new byte[size - 1];
                Buffer.BlockCopy(b, i + 1, c.Data, 0, size - 1);
                res.Add(c);
                i += size;
            }
            return res;
        }
    }

    // base::Pickle: числа выровнены по 4 байтам, строки — длина + данные + добивка.
    public class PickleReader
    {
        private readonly byte[] _b;
        private int _i;

        public PickleReader(byte[] b, int start) { _b = b; _i = start; }
        public bool Ok { get { return _i <= _b.Length; } }
        public int Pos { get { return _i; } }

        public int Int32()
        {
            if (_i + 4 > _b.Length) { _i = _b.Length + 1; return 0; }
            int v = BitConverter.ToInt32(_b, _i); _i += 4; return v;
        }

        public long Int64()
        {
            if (_i + 8 > _b.Length) { _i = _b.Length + 1; return 0; }
            long v = BitConverter.ToInt64(_b, _i); _i += 8; return v;
        }

        public string Str()
        {
            int n = Int32();
            if (n < 0 || _i + n > _b.Length) { _i = _b.Length + 1; return null; }
            string s = Encoding.UTF8.GetString(_b, _i, n);
            _i += n;
            Align(n);
            return s;
        }

        public string Str16()
        {
            int n = Int32();
            if (n < 0 || _i + n * 2 > _b.Length) { _i = _b.Length + 1; return null; }
            string s = Encoding.Unicode.GetString(_b, _i, n * 2);
            _i += n * 2;
            Align(n * 2);
            return s;
        }

        private void Align(int n)
        {
            int r = n % 4;
            if (r != 0) _i += 4 - r;
        }
    }
}
