// SysDeck — область «media», ядро: fMP4 со списком правки, на котором Media Foundation молча отдаёт ноль кадров.
//
// Так лежат некоторые форматы YouTube (133 «240p»): одна правка с media_time ≥ 0 у видеодорожки — и IMFSourceReader
// отдаёт только звук. Фикстура hls-fmp4 переделывается здесь же: её правка видео (пустая + media_time 2048) заменяется
// на одну правку того же вида, что у YouTube; размер бокса edts не меняется (хвост — бокс free).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        private static void CoreEditList(string dir)
        {
            string init = MdFx.File("hls-fmp4\\init.mp4");
            string[] segs = FixtureSet("hls-fmp4", "seg", 4);
            if (init == null || segs == null) { T.Skip("core: fmp4 with a single edit -> mp4", "hls-fmp4 fixtures missing"); return; }

            byte[] head = File.ReadAllBytes(init);
            int at = IndexOf(head, "edts");
            T.Check("core: edit list fixture has a 48-byte edts in the video track", at > 4 && BE(head, at - 4) == 48);
            if (at <= 4 || BE(head, at - 4) != 48) return;
            Put(head, at + 4, 28, "elst", 0, 1, 1136, 2048, 0x10000);   // одна правка: 1136 по часам mvhd, начало 2048
            Put(head, at + 32, 12, "free", 0);
            string patched = Path.Combine(dir, "edit-init.mp4");
            File.WriteAllBytes(patched, head);
            List<string> parts = new List<string>();
            parts.Add(patched);
            parts.AddRange(segs);
            string src = Concat(Path.Combine(dir, "edit-all.data"), parts.ToArray());

            List<long> found;
            using (FileStream fs = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read)) found = MdEditList.Find(fs);
            T.Eq("core: edit lists found in both tracks", 2, found.Count);
            string plain = Path.Combine(dir, "no-moov.data");
            File.WriteAllBytes(plain, new byte[] { 0, 0, 0, 16, (byte)'m', (byte)'d', (byte)'a', (byte)'t', 1, 2, 3, 4, 5, 6, 7, 8 });
            T.Check("core: file without moov gives no edit-list copy", MdEditList.CopyWithout(plain) == null && !File.Exists(plain + ".noedit"));

            string before = Hash(src);
            MdMuxResult r = Mux(Path.Combine(dir, "edit.mp4"), MdOutput.Auto, false, MkIn(src, MdTrackKind.Muxed));
            T.Check("core: fmp4 with a single edit -> mp4", r.Ok && File.Exists(r.OutPath), r.Error);
            T.Check("core: edit-list input is left byte-identical", Hash(src) == before);
            T.Check("core: edit-list copy is removed after the mux", !File.Exists(src + ".noedit"));
            if (!r.Ok) return;
            T.Eq("core: fmp4 with a single edit keeps all 40 video frames", 40, r.VideoFrames);
            int v, a;
            string why;
            if (MdFx.ReadBack(r.OutPath, out v, out a, out why)) T.Eq("core: fmp4 with a single edit -> mp4 read back 40 video samples", 40, v);
            else T.Check("core: fmp4 with a single edit -> mp4 read back 40 video samples", false, why);
        }

        private static int IndexOf(byte[] b, string type)
        {
            for (int i = 0; i + 4 <= b.Length; i++)
                if (b[i] == type[0] && b[i + 1] == type[1] && b[i + 2] == type[2] && b[i + 3] == type[3]) return i;
            return -1;
        }

        private static int BE(byte[] b, int o) { return b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]; }

        // Бокс по месту: размер, тип, затем 32-битные поля подряд.
        private static void Put(byte[] b, int o, int size, string type, params int[] fields)
        {
            int[] all = new int[fields.Length + 1];
            all[0] = size;
            Array.Copy(fields, 0, all, 1, fields.Length);
            for (int i = 0; i < 4; i++) b[o + 4 + i] = (byte)type[i];
            for (int k = 0; k < all.Length; k++)
            {
                int p = o + (k == 0 ? 0 : 4 + 4 * k);
                b[p] = (byte)(all[k] >> 24); b[p + 1] = (byte)(all[k] >> 16); b[p + 2] = (byte)(all[k] >> 8); b[p + 3] = (byte)all[k];
            }
        }

        private static string Hash(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream fs = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(fs));
        }
    }
}
