// SysDeck — тесты установки «ТВ-трансляции» по кнопке: распаковка ffmpeg из zip (имена, CRC, «..»),
// загрузка через локальный HTTP-сервер со сверкой SHA-256, разбор состояния брандмауэра и текст исправления.
// Сборка: tests\run-tests.bat (область tvstream, вызывается из TvStreamTests.Run).

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SysDeck.TvStream;

namespace SysDeck.Tests
{
    internal static class TvSetupTests
    {
        public static void Run(string tmp)
        {
            Names();
            Zips(Path.Combine(tmp, "zip"));
            Sha();
            Install(Path.Combine(tmp, "install"));
            Firewall();
        }

        private static void Names()
        {
            T.Eq("tv zip: versioned bin folder", true, TvZip.Matches("ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: bin at the root", true, TvZip.Matches("bin/ffprobe.exe", "ffprobe.exe"));
            T.Eq("tv zip: ../ refused", false, TvZip.Matches("../bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: ./ refused", false, TvZip.Matches("./bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: absolute refused", false, TvZip.Matches("/bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: drive refused", false, TvZip.Matches("C:/bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: backslashes refused", false, TvZip.Matches("x\\bin\\ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: deeper folder refused", false, TvZip.Matches("a/b/bin/ffmpeg.exe", "ffmpeg.exe"));
            T.Eq("tv zip: other file in bin", false, TvZip.Matches("x/bin/ffplay.exe", "ffmpeg.exe"));
            T.Eq("tv zip: suffix is not a match", false, TvZip.Matches("x/bin/ffmpeg.exe.bak", "ffmpeg.exe"));
        }

        private static Dictionary<string, string> Wanted(string dir)
        {
            Dictionary<string, string> w = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            w["ffmpeg.exe"] = Path.Combine(dir, "ffmpeg.out");
            w["ffprobe.exe"] = Path.Combine(dir, "ffprobe.out");
            return w;
        }

        private static void Zips(string dir)
        {
            Directory.CreateDirectory(dir);
            byte[] ff = Pattern(200000, 3), fp = Pattern(70000, 11);
            string error;

            byte[] good = ZipOf(new ZipItem("v/doc/readme.txt", Encoding.ASCII.GetBytes("docs"), true),
                                new ZipItem("v/bin/ffmpeg.exe", ff, true),
                                new ZipItem("v/bin/ffprobe.exe", fp, false));
            bool ok = TvZip.Extract(new MemoryStream(good), Wanted(dir), 1 << 20, null, out error);
            T.Check("tv zip: both files extracted", ok, error);
            T.Eq("tv zip: deflated file is byte-exact", true, ok && Same(File.ReadAllBytes(Path.Combine(dir, "ffmpeg.out")), ff));
            T.Eq("tv zip: stored file is byte-exact", true, ok && Same(File.ReadAllBytes(Path.Combine(dir, "ffprobe.out")), fp));
            T.Eq("tv zip: other entries not written", false, File.Exists(Path.Combine(dir, "readme.txt")));
            Clean(dir);

            byte[] crc = ZipOf(new ZipItem("v/bin/ffmpeg.exe", ff, true) { BadCrc = true }, new ZipItem("v/bin/ffprobe.exe", fp, true));
            T.Eq("tv zip: CRC mismatch refused", false, TvZip.Extract(new MemoryStream(crc), Wanted(dir), 1 << 20, null, out error));
            T.Check("tv zip: CRC error says so", error.Contains("CRC"), error);
            T.Eq("tv zip: nothing left after a refusal", 0, Directory.GetFiles(dir).Length);

            byte[] missing = ZipOf(new ZipItem("v/bin/ffmpeg.exe", ff, true));
            T.Eq("tv zip: missing ffprobe refused", false, TvZip.Extract(new MemoryStream(missing), Wanted(dir), 1 << 20, null, out error));
            T.Check("tv zip: error names the missing file", error.Contains("bin/ffprobe.exe"), error);
            T.Eq("tv zip: extracted half removed", 0, Directory.GetFiles(dir).Length);

            byte[] evil = ZipOf(new ZipItem("../bin/ffmpeg.exe", ff, true), new ZipItem("v/bin/ffprobe.exe", fp, true));
            T.Eq("tv zip: ../ entry is not taken for ffmpeg", false, TvZip.Extract(new MemoryStream(evil), Wanted(dir), 1 << 20, null, out error));

            byte[] twice = ZipOf(new ZipItem("a/bin/ffmpeg.exe", ff, true), new ZipItem("b/bin/ffmpeg.exe", ff, true), new ZipItem("a/bin/ffprobe.exe", fp, true));
            T.Eq("tv zip: duplicate ffmpeg refused", false, TvZip.Extract(new MemoryStream(twice), Wanted(dir), 1 << 20, null, out error));

            T.Eq("tv zip: entry over the size cap refused", false, TvZip.Extract(new MemoryStream(good), Wanted(dir), 100000, null, out error));
            T.Eq("tv zip: not a zip refused", false, TvZip.Extract(new MemoryStream(Pattern(5000, 1)), Wanted(dir), 1 << 20, null, out error));
            Clean(dir);
        }

        private static void Sha()
        {
            string h = "60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA";
            T.Eq("tv sha: bare hash, lower-cased", h.ToLowerInvariant(), TvFfmpegSetup.ParseSha(h + "\n"));
            T.Eq("tv sha: hash + file name", h.ToLowerInvariant(), TvFfmpegSetup.ParseSha(h + "  ffmpeg.zip"));
            T.Eq("tv sha: short refused", null, TvFfmpegSetup.ParseSha("abc"));
            T.Eq("tv sha: html refused", null, TvFfmpegSetup.ParseSha("<html>" + h.Substring(6)));
        }

        // Настоящий HttpWebRequest против локального сервера: проверка суммы, замена только после успеха.
        private static void Install(string dir)
        {
            byte[] ff = Pattern(150000, 5), fp = Pattern(40000, 9);
            byte[] zip = ZipOf(new ZipItem("ffmpeg-1.0-essentials_build/bin/ffmpeg.exe", ff, true),
                               new ZipItem("ffmpeg-1.0-essentials_build/bin/ffprobe.exe", fp, true));
            string sha;
            using (SHA256 s = SHA256.Create()) sha = BitConverter.ToString(s.ComputeHash(zip)).Replace("-", "").ToLowerInvariant();

            string oldZip = TvFfmpegSetup.ZipUrl, oldSha = TvFfmpegSetup.ShaUrl, oldOwn = TvFfmpeg.OwnDir;
            string oldApi = TvFfmpegSetup.ReleaseApi, oldPrefix = TvFfmpegSetup.AssetPrefix;
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            HttpFixture http = new HttpFixture(files);
            try
            {
                // Сначала GitHub не отвечает (404) — всё ниже идёт через запасной источник gyan.dev.
                TvFfmpegSetup.ReleaseApi = http.Base + "/api/latest";
                TvFfmpegSetup.AssetPrefix = http.Base + "/dl/";
                TvFfmpegSetup.ZipUrl = http.Base + "/ff.zip";
                TvFfmpegSetup.ShaUrl = http.Base + "/ff.zip.sha256";
                files["/ff.zip"] = zip;
                files["/ff.zip.sha256"] = Encoding.ASCII.GetBytes(sha.ToUpperInvariant() + " *ffmpeg-release-essentials.zip\n");

                long last = 0, lastTotal = 0;
                string error;
                bool ok = TvFfmpegSetup.Install(dir, delegate(long got, long total) { last = got; lastTotal = total; }, null, out error);
                T.Check("tv install: downloaded, checked, extracted", ok, error);
                T.Eq("tv install: ffmpeg.exe byte-exact", true, ok && Same(File.ReadAllBytes(Path.Combine(dir, "ffmpeg.exe")), ff));
                T.Eq("tv install: ffprobe.exe byte-exact", true, ok && Same(File.ReadAllBytes(Path.Combine(dir, "ffprobe.exe")), fp));
                T.Eq("tv install: progress reached the full size", zip.Length + "/" + zip.Length, last + "/" + lastTotal);
                T.Eq("tv install: no download or temp files left", 2, Directory.GetFiles(dir).Length);
                TvFfmpeg.OwnDir = dir;
                TvFfmpeg found = TvFfmpeg.Locate();
                T.Check("tv install: Locate finds the own copy first", found.Available && found.FfmpegPath == Path.Combine(dir, "ffmpeg.exe")
                        && found.FfprobePath == Path.Combine(dir, "ffprobe.exe"), found.FfmpegPath);

                files["/ff.zip.sha256"] = Encoding.ASCII.GetBytes(new string('0', 64));
                ok = TvFfmpegSetup.Install(dir, null, null, out error);
                T.Eq("tv install: wrong SHA-256 refused", false, ok);
                T.Check("tv install: error names SHA-256", error != null && error.Contains("SHA-256"), error);
                T.Eq("tv install: previous ffmpeg kept on refusal", true, Same(File.ReadAllBytes(Path.Combine(dir, "ffmpeg.exe")), ff));
                T.Eq("tv install: no leftovers after refusal", 2, Directory.GetFiles(dir).Length);

                files["/ff.zip.sha256"] = Encoding.ASCII.GetBytes(sha);
                ok = TvFfmpegSetup.Install(dir, null, delegate { return true; }, out error);
                T.Eq("tv install: cancel stops it", false, ok);
                T.Eq("tv install: cancel leaves no part file", false, File.Exists(Path.Combine(dir, "ffmpeg.zip.part")));

                files.Remove("/ff.zip.sha256");
                T.Eq("tv install: no checksum → refused before downloading", false, TvFfmpegSetup.Install(dir, null, null, out error));
                T.Check("tv install: checksum error is human", error != null && error.Contains("контрольную сумму"), error);

                // Основной путь: релиз GitHub, сумма из digest; gyan.dev при этом не нужен вовсе.
                files.Remove("/ff.zip");
                byte[] ff2 = Pattern(90000, 3);
                byte[] zip2 = ZipOf(new ZipItem("ffmpeg-8.0-essentials_build/bin/ffmpeg.exe", ff2, true),
                                    new ZipItem("ffmpeg-8.0-essentials_build/bin/ffprobe.exe", fp, false));
                string sha2;
                using (SHA256 s = SHA256.Create()) sha2 = BitConverter.ToString(s.ComputeHash(zip2)).Replace("-", "").ToLowerInvariant();
                files["/dl/8.0/ffmpeg-8.0-essentials_build.zip"] = zip2;
                files["/api/latest"] = Encoding.UTF8.GetBytes(Release(http.Base, "sha256:" + sha2));
                ok = TvFfmpegSetup.Install(dir, null, null, out error);
                T.Check("tv install github: release asset downloaded and checked by digest", ok, error);
                T.Eq("tv install github: ffmpeg.exe replaced with the release copy", true, ok && Same(File.ReadAllBytes(Path.Combine(dir, "ffmpeg.exe")), ff2));

                files["/api/latest"] = Encoding.UTF8.GetBytes(Release(http.Base, "sha256:" + new string('0', 64)));
                ok = TvFfmpegSetup.Install(dir, null, null, out error);
                T.Eq("tv install github: wrong digest refused", false, ok);
                T.Check("tv install github: digest error names SHA-256", error != null && error.Contains("SHA-256"), error);
                T.Eq("tv install github: previous ffmpeg kept", true, Same(File.ReadAllBytes(Path.Combine(dir, "ffmpeg.exe")), ff2));

                string url, picked;
                TvGhRelease foreign = Parse(Release("https://evil.example", "sha256:" + sha2));
                T.Eq("tv github: asset outside the repo releases ignored", false, TvFfmpegSetup.PickAsset(foreign, out url, out picked));
                TvGhRelease noDigest = Parse(Release(http.Base, null));
                T.Eq("tv github: asset without digest ignored", false, TvFfmpegSetup.PickAsset(noDigest, out url, out picked));
                T.Check("tv github: essentials picked, not full", TvFfmpegSetup.PickAsset(Parse(Release(http.Base, "sha256:" + sha2)), out url, out picked)
                        && url.EndsWith("/ffmpeg-8.0-essentials_build.zip") && picked == sha2, url);
                files["/api/latest"] = Encoding.UTF8.GetBytes("{not json");
                T.Eq("tv github: broken JSON → fallback, no throw", false, TvFfmpegSetup.FromGitHub(out url, out picked));
            }
            finally
            {
                TvFfmpegSetup.ReleaseApi = oldApi;
                TvFfmpegSetup.AssetPrefix = oldPrefix;
                TvFfmpegSetup.ZipUrl = oldZip;
                TvFfmpegSetup.ShaUrl = oldSha;
                TvFfmpeg.OwnDir = oldOwn;
                http.Stop();
            }
        }

        // Ответ API в форме GitHub: рядом лежит «full» — выбрать надо именно essentials.
        private static string Release(string host, string digest)
        {
            string d = digest == null ? "null" : "\"" + digest + "\"";
            return "{\"tag_name\":\"8.0\",\"assets\":["
                + "{\"name\":\"ffmpeg-8.0-full_build.zip\",\"size\":1,\"digest\":\"sha256:" + new string('a', 64) + "\",\"browser_download_url\":\"" + host + "/dl/8.0/ffmpeg-8.0-full_build.zip\"},"
                + "{\"name\":\"ffmpeg-8.0-essentials_build.zip\",\"size\":2,\"digest\":" + d + ",\"browser_download_url\":\"" + host + "/dl/8.0/ffmpeg-8.0-essentials_build.zip\",\"uploader\":{\"login\":\"GyanD\"}}"
                + "]}";
        }

        private static TvGhRelease Parse(string json)
        {
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (TvGhRelease)new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(TvGhRelease)).ReadObject(ms);
        }

        private static void Firewall()
        {
            TvFirewallState a = TvFirewall.ParseStatus("WARNING: noise\r\nallow=1;block=0;cat=Public;net=anerlonda\r\n");
            T.Eq("tv firewall: allowed on a public network", "True|True|False|Public|anerlonda", a.Known + "|" + a.Allowed + "|" + a.Blocked + "|" + a.Category + "|" + a.Network);
            TvFirewallState b = TvFirewall.ParseStatus("allow=2;block=1;cat=Private;net=Home");
            T.Eq("tv firewall: a block wins over allows", "True|False|True", b.Known + "|" + b.Allowed + "|" + b.Blocked);
            T.Eq("tv firewall: nothing allowed", false, TvFirewall.ParseStatus("allow=0;block=0;cat=Private;net=x").Allowed);
            T.Eq("tv firewall: unreadable output → unknown", false, TvFirewall.ParseStatus("Get-NetIPAddress : not recognized").Known);

            string fix = TvFirewall.FixScript(@"C:\it's\PcStream.exe", 8090);
            T.Check("tv firewall fix: only this program's BLOCK rules removed", fix.Contains("-Program 'C:\\it''s\\PcStream.exe'") && fix.Contains("$_.Action -eq 'Block'"), fix);
            T.Check("tv firewall fix: port open to the local subnet, any profile", fix.Contains("-LocalPort 8090 -RemoteAddress LocalSubnet -Profile Any"), fix);
            T.Check("tv firewall fix: old rule of the same name replaced", fix.Contains("-DisplayName 'PC Stream TCP 8090' -ErrorAction SilentlyContinue | Remove-NetFirewallRule"), fix);
            T.Check("tv firewall fix: exit code reports failure", fix.Contains("exit 0") && fix.Contains("catch{ exit 1 }"), fix);
            T.Check("tv firewall status: asks for the same rule name", TvFirewall.StatusScript("x.exe", 8191, "192.168.0.5").Contains("'PC Stream TCP 8191'"));
            string enc = TvFirewall.Encoded("Write-Output 'ё'");
            T.Eq("tv firewall: encoded command is UTF-16 base64", "Write-Output 'ё'", Encoding.Unicode.GetString(Convert.FromBase64String(enc)));
        }

        // ---------- фикстуры ----------
        private sealed class ZipItem
        {
            public string Name; public byte[] Data; public bool Deflate; public bool BadCrc;
            public ZipItem(string name, byte[] data, bool deflate) { Name = name; Data = data; Deflate = deflate; }
        }

        // Zip собирается здесь же, независимо от распаковщика: локальные заголовки, центральный каталог, EOCD.
        private static byte[] ZipOf(params ZipItem[] items)
        {
            MemoryStream ms = new MemoryStream();
            BinaryWriter w = new BinaryWriter(ms);
            List<long> offsets = new List<long>();
            List<byte[]> packed = new List<byte[]>();
            foreach (ZipItem it in items)
            {
                byte[] body = it.Data;
                if (it.Deflate)
                {
                    MemoryStream d = new MemoryStream();
                    using (DeflateStream z = new DeflateStream(d, CompressionMode.Compress, true)) z.Write(it.Data, 0, it.Data.Length);
                    body = d.ToArray();
                }
                packed.Add(body);
                byte[] name = Encoding.UTF8.GetBytes(it.Name);
                offsets.Add(ms.Position);
                w.Write(0x04034b50u); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)(it.Deflate ? 8 : 0));
                w.Write((ushort)0); w.Write((ushort)0); w.Write(Crc(it.Data) ^ (it.BadCrc ? 1u : 0u));
                w.Write((uint)body.Length); w.Write((uint)it.Data.Length); w.Write((ushort)name.Length); w.Write((ushort)0);
                w.Write(name); w.Write(body);
            }
            long cd = ms.Position;
            for (int i = 0; i < items.Length; i++)
            {
                ZipItem it = items[i];
                byte[] name = Encoding.UTF8.GetBytes(it.Name);
                w.Write(0x02014b50u); w.Write((ushort)20); w.Write((ushort)20); w.Write((ushort)0); w.Write((ushort)(it.Deflate ? 8 : 0));
                w.Write((ushort)0); w.Write((ushort)0); w.Write(Crc(it.Data) ^ (it.BadCrc ? 1u : 0u));
                w.Write((uint)packed[i].Length); w.Write((uint)it.Data.Length); w.Write((ushort)name.Length);
                w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)0); w.Write(0u); w.Write((uint)offsets[i]);
                w.Write(name);
            }
            long cdEnd = ms.Position;
            w.Write(0x06054b50u); w.Write((ushort)0); w.Write((ushort)0); w.Write((ushort)items.Length); w.Write((ushort)items.Length);
            w.Write((uint)(cdEnd - cd)); w.Write((uint)cd); w.Write((ushort)0);
            w.Flush();
            return ms.ToArray();
        }

        private static uint Crc(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)-(int)(c & 1));
            }
            return ~c;
        }

        private static byte[] Pattern(int n, int seed)
        {
            byte[] b = new byte[n];
            Random r = new Random(seed);
            for (int i = 0; i < n; i++) b[i] = i % 3 == 0 ? (byte)r.Next(256) : (byte)(i * seed);   // сжимается, но не до нуля
            return b;
        }

        private static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        private static void Clean(string dir) { foreach (string f in Directory.GetFiles(dir)) File.Delete(f); }

        // Минимальный HTTP/1.0-сервер на loopback: путь → тело, неизвестный путь → 404.
        private sealed class HttpFixture
        {
            private readonly TcpListener _l;
            private readonly Dictionary<string, byte[]> _files;
            private volatile bool _stop;
            public readonly string Base;

            public HttpFixture(Dictionary<string, byte[]> files)
            {
                _files = files;
                _l = new TcpListener(IPAddress.Loopback, 0);
                _l.Start();
                Base = "http://127.0.0.1:" + ((IPEndPoint)_l.LocalEndpoint).Port;
                Thread t = new Thread(Loop);
                t.IsBackground = true;
                t.Start();
            }

            private void Loop()
            {
                while (!_stop)
                {
                    TcpClient c;
                    try { c = _l.AcceptTcpClient(); }
                    catch (Exception) { return; }
                    try
                    {
                        using (c)
                        {
                            NetworkStream ns = c.GetStream();
                            string head = TvHttp.ReadHead(ns);
                            string path = head == null ? "" : head.Split(' ')[1];
                            byte[] body;
                            lock (_files) _files.TryGetValue(path, out body);
                            string status = body == null ? "404 Not Found" : "200 OK";
                            if (body == null) body = new byte[0];
                            byte[] h = Encoding.ASCII.GetBytes("HTTP/1.0 " + status + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                            ns.Write(h, 0, h.Length);
                            ns.Write(body, 0, body.Length);
                        }
                    }
                    catch (Exception) { }
                }
            }

            public void Stop() { _stop = true; try { _l.Stop(); } catch (Exception) { } }
        }
    }
}
