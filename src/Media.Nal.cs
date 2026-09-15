// SysDeck — медиа: разбор NAL (H.264/HEVC).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.IO;

namespace SysDeck.Downloads
{
    // ------------------------------------------------------------------ //
    //  NAL: разбиение Annex B, SPS
    // ------------------------------------------------------------------ //
    internal static class MdNal
    {
        // Отрезки NAL (начало, длина) без стартовых кодов и хвостовых нулей.
        public static List<int[]> Split(byte[] a, int off, int len)
        {
            List<int[]> list = new List<int[]>();
            int end = off + len;
            int i = off;
            int start = -1;
            while (i + 2 < end)
            {
                if (a[i] == 0 && a[i + 1] == 0 && a[i + 2] == 1)
                {
                    if (start >= 0) AddNal(list, a, start, i);
                    i += 3;
                    start = i;
                    continue;
                }
                i++;
            }
            if (start >= 0) AddNal(list, a, start, end);
            return list;
        }

        private static void AddNal(List<int[]> list, byte[] a, int start, int stop)
        {
            while (stop > start && a[stop - 1] == 0) stop--;
            if (stop > start) list.Add(new int[] { start, stop - start });
        }

        public static byte[] Rbsp(byte[] a, int off, int len)
        {
            byte[] r = new byte[Math.Max(0, len)];
            int n = 0, zeros = 0;
            for (int i = off; i < off + len; i++)
            {
                byte b = a[i];
                if (zeros >= 2 && b == 3) { zeros = 0; continue; }
                r[n++] = b;
                zeros = b == 0 ? zeros + 1 : 0;
            }
            Array.Resize(ref r, n);
            return r;
        }

        private sealed class Bits
        {
            private readonly byte[] _a;
            private long _bit;
            public bool Over;
            public Bits(byte[] a) { _a = a; }

            public uint Bit()
            {
                long idx = _bit >> 3;
                if (idx >= _a.Length) { Over = true; return 0; }
                uint v = (uint)((_a[idx] >> (int)(7 - (_bit & 7))) & 1);
                _bit++;
                return v;
            }

            public uint U(int n)
            {
                uint v = 0;
                for (int i = 0; i < n; i++) v = (v << 1) | Bit();
                return v;
            }

            public void Skip(int n) { _bit += n; if ((_bit >> 3) > _a.Length) Over = true; }

            public uint Ue()
            {
                int zeros = 0;
                while (Bit() == 0)
                {
                    if (Over || ++zeros > 31) { Over = true; return 0; }
                }
                if (zeros == 0) return 0;
                return ((1u << zeros) - 1) + U(zeros);
            }

            public int Se()
            {
                uint k = Ue();
                return (k & 1) != 0 ? (int)((k + 1) / 2) : -(int)(k / 2);
            }
        }

        public static bool H264Size(byte[] a, int off, int len, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (len < 4) return false;
            Bits b = new Bits(Rbsp(a, off + 1, len - 1));
            uint profile = b.U(8);
            b.U(16);
            b.Ue();
            uint chroma = 1;
            uint separate = 0;
            if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 || profile == 83 || profile == 86
                || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134 || profile == 135)
            {
                chroma = b.Ue();
                if (chroma == 3) separate = b.U(1);
                b.Ue();
                b.Ue();
                b.U(1);
                if (b.U(1) == 1)
                {
                    int lists = chroma != 3 ? 8 : 12;
                    for (int i = 0; i < lists && !b.Over; i++)
                        if (b.U(1) == 1)
                        {
                            int size = i < 6 ? 16 : 64, last = 8, next = 8;
                            for (int j = 0; j < size && !b.Over; j++)
                            {
                                if (next != 0) next = (last + b.Se() + 256) % 256;
                                if (next != 0) last = next;
                            }
                        }
                }
            }
            b.Ue();
            uint poc = b.Ue();
            if (poc == 0) b.Ue();
            else if (poc == 1)
            {
                b.U(1);
                b.Se();
                b.Se();
                uint cycle = b.Ue();
                if (cycle > 255) return false;
                for (uint i = 0; i < cycle; i++) b.Se();
            }
            b.Ue();
            b.U(1);
            uint wMbs = b.Ue(), hMaps = b.Ue();
            uint frameMbsOnly = b.U(1);
            if (frameMbsOnly == 0) b.U(1);
            b.U(1);
            uint cl = 0, cr = 0, ct = 0, cb = 0;
            if (b.U(1) == 1) { cl = b.Ue(); cr = b.Ue(); ct = b.Ue(); cb = b.Ue(); }
            if (b.Over || wMbs > 1024 || hMaps > 1024) return false;
            uint arrayType = separate == 1 ? 0 : chroma;
            long subW = arrayType == 1 || arrayType == 2 ? 2 : 1, subH = arrayType == 1 ? 2 : 1;
            long cropX = arrayType == 0 ? 1 : subW, cropY = (arrayType == 0 ? 1 : subH) * (2 - frameMbsOnly);
            long w = (wMbs + 1) * 16L - cropX * (cl + cr);
            long h = (2 - frameMbsOnly) * (hMaps + 1) * 16L - cropY * (ct + cb);
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }

        public static bool HevcSize(byte[] a, int off, int len, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (len < 6) return false;
            Bits b = new Bits(Rbsp(a, off + 2, len - 2));
            b.U(4);
            int maxSub = (int)b.U(3);
            b.U(1);
            b.Skip(88);
            b.U(8);
            bool[] profilePresent = new bool[8], levelPresent = new bool[8];
            for (int i = 0; i < maxSub; i++) { profilePresent[i] = b.U(1) == 1; levelPresent[i] = b.U(1) == 1; }
            if (maxSub > 0) for (int i = maxSub; i < 8; i++) b.Skip(2);
            for (int i = 0; i < maxSub; i++)
            {
                if (profilePresent[i]) b.Skip(88);
                if (levelPresent[i]) b.Skip(8);
            }
            b.Ue();
            uint chroma = b.Ue();
            if (chroma == 3) b.U(1);
            uint pw = b.Ue(), ph = b.Ue();
            uint l = 0, r = 0, t = 0, bo = 0;
            if (b.U(1) == 1) { l = b.Ue(); r = b.Ue(); t = b.Ue(); bo = b.Ue(); }
            if (b.Over) return false;
            long subW = chroma == 1 || chroma == 2 ? 2 : 1, subH = chroma == 1 ? 2 : 1;
            long w = pw - subW * (l + r), h = ph - subH * (t + bo);
            if (w <= 0 || h <= 0 || w > 16384 || h > 16384) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }
    }
}
