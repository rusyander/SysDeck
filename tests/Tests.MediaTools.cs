// SysDeck — область «media», часть yt-dlp: установка инструментов, извлечение форматов, запуск процессов.
//
// Установка идёт по-настоящему: «GitHub» — DlTestServer на петле (редирект releases/latest, файл сумм, редирект на другой
// хост за самим файлом), exe — крошечная заглушка, собранная здесь же CSharpCodeProvider'ом и размноженная правкой строки
// версии прямо в двоичном файле (разные SHA-256 и разные ответы --version). Процессы — настоящие: заглушка, ping.exe на
// 127.0.0.1, where.exe. Разбор ответа yt-dlp — на записанном живом ответе (ip/sig/lsig/n вычищены): и через шов Runner, и
// через настоящий процесс-заглушку, печатающий тот же ответ.
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CSharp;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        private const string ToolsDenoLine = "deno {0} (stable, release, x86_64-pc-windows-msvc)|v8 13.0|typescript 5.8";
        private const int ToolsSlots = 80;
        private static Func<string, string, Func<bool>, string> _toolsRunner;

        static partial void RunTools()
        {
            string live = Environment.GetEnvironmentVariable("SYSDECK_MD_C_LIVE");
            if (!string.IsNullOrEmpty(live)) { ToolsLiveRecord(live); return; }

            string wasDir = MdTools.DirOverride, wasBase = MdTools.GitHubBase;
            Func<DateTime> wasClock = MdTools.Clock;
            Func<string, string, Func<bool>, string> wasRunner = MdYtdlp.Runner;
            int wasTimeout = MdYtdlp.TimeoutMs;
            bool wasEn = Tr.En;
            _toolsRunner = wasRunner;
            try
            {
                string root = Fx.MakeDir(Fx.Root, "md-tools");
                string why;
                byte[] stub = ToolsBuildStub(root, out why);
                T.Check("tools: stub exe compiled for the real process checks", stub != null, why);
                ToolsZipCases(root);
                ToolsSums();
                ToolsProcessCases(root, stub);
                ToolsMappingCases();
                ToolsErrorCases();
                if (stub != null) ToolsInstallAndExtract(root, stub);
            }
            finally
            {
                MdTools.DirOverride = wasDir;
                MdTools.GitHubBase = wasBase;
                MdTools.Clock = wasClock;
                MdYtdlp.Runner = wasRunner;
                MdYtdlp.TimeoutMs = wasTimeout;
                Tr.En = wasEn;
                MdTools.ForgetVerified();
            }
        }

        // ------------------------------------------------------------------ //
        //  Заглушка: --version, --echo, --sleep, --spawn, --big, -J
        // ------------------------------------------------------------------ //
        private const string ToolsStubSource =
            "using System; using System.Diagnostics; using System.IO; using System.Text; using System.Threading;\n" +
            "static class StubMain {\n" +
            "  static readonly string V = \"WPCSTUBVERSION:________________________________________________________________________________\";\n" +
            "  static void Out(Stream s, string t) { byte[] b = Encoding.UTF8.GetBytes(t); s.Write(b, 0, b.Length); s.Flush(); }\n" +
            "  static int Main(string[] a) {\n" +
            "    string exe = typeof(StubMain).Assembly.Location; string dir = Path.GetDirectoryName(exe);\n" +
            "    string ver = V.Substring(15).TrimEnd('_').Replace('|', '\\n');\n" +
            "    Stream so = Console.OpenStandardOutput();\n" +
            "    if (a.Length == 1 && a[0] == \"--version\") {\n" +
            "      File.AppendAllText(Path.Combine(dir, \"stub-runs.txt\"), Path.GetFileName(exe) + \" \" + ver.Split('\\n')[0] + \"\\n\");\n" +
            "      if (ver.StartsWith(\"EXIT\")) return 3;\n" +
            "      Out(so, ver + \"\\n\"); return 0; }\n" +
            "    if (a.Length >= 1 && a[0] == \"--echo\") { for (int i = 1; i < a.Length; i++) Out(so, Convert.ToBase64String(Encoding.UTF8.GetBytes(a[i])) + \"\\n\"); return 0; }\n" +
            "    if (a.Length == 2 && a[0] == \"--sleep\") { Thread.Sleep(int.Parse(a[1])); return 0; }\n" +
            "    if (a.Length == 2 && a[0] == \"--spawn\") {\n" +
            "      ProcessStartInfo psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, \"PING.EXE\"), \"-n 60 127.0.0.1\");\n" +
            "      psi.UseShellExecute = false; psi.CreateNoWindow = true;\n" +
            "      Process c = Process.Start(psi); File.WriteAllText(a[1], c.Id.ToString()); Thread.Sleep(60000); return 0; }\n" +
            "    if (a.Length == 2 && a[0] == \"--big\") { byte[] b = new byte[65536]; for (int i = 0; i < b.Length; i++) b[i] = 120;\n" +
            "      long left = long.Parse(a[1]); while (left > 0) { int n = (int)Math.Min(left, b.Length); so.Write(b, 0, n); left -= n; } return 0; }\n" +
            "    if (a.Length > 0 && a[0] == \"-J\") {\n" +
            "      StringBuilder sb = new StringBuilder(); foreach (string x in a) sb.Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(x))).Append('\\n');\n" +
            "      File.AppendAllText(Path.Combine(dir, \"stub-args.txt\"), sb.ToString() + \"#\\n\");\n" +
            "      string fail = Path.Combine(dir, \"stub-stderr.txt\");\n" +
            "      if (File.Exists(fail)) { byte[] e = File.ReadAllBytes(fail); Stream se = Console.OpenStandardError(); se.Write(e, 0, e.Length); se.Flush(); return 1; }\n" +
            "      byte[] j = File.ReadAllBytes(Path.Combine(dir, \"stub-json.txt\")); so.Write(j, 0, j.Length); so.Flush(); return 0; }\n" +
            "    return 2;\n" +
            "  }\n" +
            "}\n";

        private static byte[] ToolsBuildStub(string root, out string why)
        {
            why = null;
            string outPath = Path.Combine(root, "stub-build.exe");
            try
            {
                using (CSharpCodeProvider cp = new CSharpCodeProvider())
                {
                    CompilerParameters ps = new CompilerParameters(new[] { "System.dll" }, outPath);
                    ps.GenerateExecutable = true;
                    ps.CompilerOptions = "/optimize+";
                    CompilerResults res = cp.CompileAssemblyFromSource(ps, ToolsStubSource);
                    if (res.Errors.HasErrors) { why = res.Errors[0].ToString(); return null; }
                }
                byte[] b = File.ReadAllBytes(outPath);
                if (ToolsIndexOf(b, Encoding.Unicode.GetBytes("WPCSTUBVERSION:"), 0) < 0) { why = "version marker not found in the compiled stub"; return null; }
                return b;
            }
            catch (Exception ex)
            {
                why = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // Та же заглушка с другой строкой версии: другой SHA-256 и другой ответ --version.
        private static byte[] ToolsStubWith(byte[] stub, string version)
        {
            byte[] marker = Encoding.Unicode.GetBytes("WPCSTUBVERSION:");
            byte[] copy = (byte[])stub.Clone();
            byte[] vb = Encoding.Unicode.GetBytes(version.PadRight(ToolsSlots, '_'));
            for (int at = ToolsIndexOf(copy, marker, 0); at >= 0; at = ToolsIndexOf(copy, marker, at + 1))
                Array.Copy(vb, 0, copy, at + marker.Length, vb.Length);
            return copy;
        }

        private static int ToolsIndexOf(byte[] hay, byte[] needle, int from)
        {
            for (int i = Math.Max(0, from); i + needle.Length <= hay.Length; i++)
            {
                int k = 0;
                while (k < needle.Length && hay[i + k] == needle[k]) k++;
                if (k == needle.Length) return i;
            }
            return -1;
        }

        private static string ToolsSha(byte[] b)
        {
            using (SHA256 h = SHA256.Create()) return DlTestServer.Hex(h.ComputeHash(b));
        }

        private static string ToolsFileSha(string path)
        {
            return File.Exists(path) ? ToolsSha(File.ReadAllBytes(path)) : "";
        }

        // ------------------------------------------------------------------ //
        //  Zip: свой писатель (CRC считается независимо) и таблица отказов разборщика
        // ------------------------------------------------------------------ //
        private sealed class ToolsZipSpec
        {
            public string Name = "";
            public byte[] Data;
            public bool Deflate = true;
            public long UsizeOverride = -1;
            public bool BadCrc;
        }

        private static ToolsZipSpec ToolsEntry(string name, byte[] data)
        {
            ToolsZipSpec z = new ToolsZipSpec();
            z.Name = name;
            z.Data = data;
            return z;
        }

        private static uint ToolsCrc(byte[] data)
        {
            uint c = 0xFFFFFFFFu;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++) c = (c >> 1) ^ (0xEDB88320u & (uint)(-(int)(c & 1)));
            }
            return ~c;
        }

        private static byte[] ToolsMakeZip(params ToolsZipSpec[] entries)
        {
            MemoryStream z = new MemoryStream();
            MemoryStream cd = new MemoryStream();
            foreach (ToolsZipSpec e in entries)
            {
                byte[] body;
                if (e.Deflate)
                {
                    using (MemoryStream ms = new MemoryStream())
                    {
                        using (DeflateStream ds = new DeflateStream(ms, CompressionMode.Compress, true)) ds.Write(e.Data, 0, e.Data.Length);
                        body = ms.ToArray();
                    }
                }
                else body = e.Data;
                byte[] name = Encoding.UTF8.GetBytes(e.Name);
                uint crc = ToolsCrc(e.Data) ^ (e.BadCrc ? 1u : 0u);
                long usize = e.UsizeOverride >= 0 ? e.UsizeOverride : e.Data.Length;
                long lho = z.Position;
                ToolsWord(z, 0x04034b50u, 4); ToolsWord(z, 20, 2); ToolsWord(z, 0, 2); ToolsWord(z, e.Deflate ? 8u : 0u, 2); ToolsWord(z, 0, 2); ToolsWord(z, 0x21, 2);
                ToolsWord(z, crc, 4); ToolsWord(z, (uint)body.Length, 4); ToolsWord(z, (uint)usize, 4); ToolsWord(z, (uint)name.Length, 2); ToolsWord(z, 0, 2);
                z.Write(name, 0, name.Length);
                z.Write(body, 0, body.Length);
                ToolsWord(cd, 0x02014b50u, 4); ToolsWord(cd, 20, 2); ToolsWord(cd, 20, 2); ToolsWord(cd, 0, 2); ToolsWord(cd, e.Deflate ? 8u : 0u, 2); ToolsWord(cd, 0, 2);
                ToolsWord(cd, 0x21, 2); ToolsWord(cd, crc, 4); ToolsWord(cd, (uint)body.Length, 4); ToolsWord(cd, (uint)usize, 4); ToolsWord(cd, (uint)name.Length, 2);
                ToolsWord(cd, 0, 2); ToolsWord(cd, 0, 2); ToolsWord(cd, 0, 2); ToolsWord(cd, 0, 2); ToolsWord(cd, 0, 4); ToolsWord(cd, (uint)lho, 4);
                cd.Write(name, 0, name.Length);
            }
            long cdOff = z.Position;
            byte[] c = cd.ToArray();
            z.Write(c, 0, c.Length);
            ToolsWord(z, 0x06054b50u, 4); ToolsWord(z, 0, 2); ToolsWord(z, 0, 2); ToolsWord(z, (uint)entries.Length, 2); ToolsWord(z, (uint)entries.Length, 2);
            ToolsWord(z, (uint)c.Length, 4); ToolsWord(z, (uint)cdOff, 4); ToolsWord(z, 0, 2);
            return z.ToArray();
        }

        private static void ToolsWord(Stream s, uint v, int bytes)
        {
            for (int i = 0; i < bytes; i++) s.WriteByte((byte)(v >> (8 * i)));
        }

        private static bool ToolsZipTry(string root, byte[] zip, out byte[] result, out string error)
        {
            string outPath = Path.Combine(root, "zip-out.bin");
            result = null;
            using (MemoryStream ms = new MemoryStream(zip))
            {
                bool ok = MdZip.ExtractSingle(ms, "deno.exe", outPath, 1 << 20, null, out error);
                if (File.Exists(outPath)) { result = File.ReadAllBytes(outPath); File.Delete(outPath); }
                return ok;
            }
        }

        private static void ToolsZipCases(string root)
        {
            byte[] data = new byte[50000];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)((i % 251) ^ (i >> 7));
            byte[] got;
            string err;
            bool ok = ToolsZipTry(root, ToolsMakeZip(ToolsEntry("deno.exe", data)), out got, out err);
            T.Check("tools: zip deflate entry deno.exe extracted byte-exact", ok && got != null && ToolsSha(got) == ToolsSha(data), err);
            ToolsZipSpec stored = ToolsEntry("deno.exe", data);
            stored.Deflate = false;
            ok = ToolsZipTry(root, ToolsMakeZip(stored), out got, out err);
            T.Check("tools: zip stored entry extracted byte-exact", ok && got != null && ToolsSha(got) == ToolsSha(data), err);

            string[] badNames = { "..\\deno.exe", "../deno.exe", "bin/deno.exe", "DENO.EXE", "deno.exe ", "C:\\deno.exe" };
            foreach (string n in badNames)
            {
                ok = ToolsZipTry(root, ToolsMakeZip(ToolsEntry(n, data)), out got, out err);
                T.Check("tools: zip entry named \"" + n + "\" refused, nothing written", !ok && got == null && err.Length > 0, err);
            }
            ok = ToolsZipTry(root, ToolsMakeZip(ToolsEntry("deno.exe", data), ToolsEntry("readme.txt", new byte[] { 1, 2, 3 })), out got, out err);
            T.Check("tools: zip with an extra entry refused", !ok && got == null, err);
            ok = ToolsZipTry(root, ToolsMakeZip(ToolsEntry("readme.txt", new byte[] { 1 }), ToolsEntry("deno.exe", data)), out got, out err);
            T.Check("tools: zip where deno.exe is one of two entries refused", !ok && got == null, err);
            ToolsZipSpec crc = ToolsEntry("deno.exe", data);
            crc.BadCrc = true;
            ok = ToolsZipTry(root, ToolsMakeZip(crc), out got, out err);
            T.Check("tools: zip CRC mismatch refused, partial output removed", !ok && got == null && err.Length > 0, err);
            ok = ToolsZipTry(root, ToolsMakeZip(ToolsEntry("deno.exe", new byte[200000])), out got, out err);
            T.Check("tools: zip compression ratio over the limit refused (200 KB of zeros)", !ok && got == null, err);
            ToolsZipSpec big = ToolsEntry("deno.exe", data);
            big.UsizeOverride = 2L << 20;
            ok = ToolsZipTry(root, ToolsMakeZip(big), out got, out err);
            T.Check("tools: zip declared size over the cap refused", !ok && got == null, err);
            ToolsZipSpec lie = ToolsEntry("deno.exe", data);
            lie.UsizeOverride = data.Length - 10;
            ok = ToolsZipTry(root, ToolsMakeZip(lie), out got, out err);
            T.Check("tools: zip entry longer than declared refused", !ok && got == null, err);
            byte[] good = ToolsMakeZip(ToolsEntry("deno.exe", data));
            byte[] cut = new byte[good.Length / 2];
            Array.Copy(good, cut, cut.Length);
            ok = ToolsZipTry(root, cut, out got, out err);
            T.Check("tools: truncated zip refused", !ok && got == null, err);
            ok = ToolsZipTry(root, Encoding.ASCII.GetBytes("this is not a zip at all, only text padding padding padding"), out got, out err);
            T.Check("tools: garbage instead of a zip refused without an exception", !ok && got == null, err);
        }

        // ------------------------------------------------------------------ //
        //  Файлы контрольных сумм
        // ------------------------------------------------------------------ //
        private static void ToolsSums()
        {
            string h1 = new string('a', 64), h2 = new string('b', 64), h3 = "C0FFEE" + new string('D', 58);
            byte[] ytSums = Encoding.UTF8.GetBytes(h1 + "  yt-dlp_linux\n" + h2 + "  yt-dlp.exe\n" + h3 + "  yt-dlp_x86.exe\n");
            T.Eq("tools: SHA2-256SUMS picks the yt-dlp.exe line only", h2, MdTools.HashFromSums(ytSums, "yt-dlp.exe", true));
            T.Eq("tools: SHA2-256SUMS without a yt-dlp.exe line gives nothing", null,
                 MdTools.HashFromSums(Encoding.UTF8.GetBytes(h1 + "  yt-dlp_linux\n" + h3 + "  yt-dlp.exe.zip\n"), "yt-dlp.exe", true));
            T.Eq("tools: a bare hash is not accepted where a name is required", null, MdTools.HashFromSums(Encoding.UTF8.GetBytes(h2 + "\n"), "yt-dlp.exe", true));
            string formatList = "\r\nAlgorithm : SHA256\r\nHash      : " + h3 + "\r\nPath      : D:\\a\\deno\\deno\\target\\release\\" + MdTools.DenoAsset + "\r\n\r\n";
            T.Eq("tools: Deno sum as PowerShell Format-List in UTF-8 (the shape recorded live)", h3.ToLowerInvariant(),
                 MdTools.HashFromSums(Encoding.UTF8.GetBytes(formatList), MdTools.DenoAsset, false));
            byte[] body = Encoding.Unicode.GetBytes(formatList);
            byte[] all = new byte[body.Length + 2];
            all[0] = 0xFF;
            all[1] = 0xFE;
            Array.Copy(body, 0, all, 2, body.Length);
            T.Eq("tools: Deno sum in UTF-16 with a BOM", h3.ToLowerInvariant(), MdTools.HashFromSums(all, MdTools.DenoAsset, false));
            T.Eq("tools: Deno sum in sha256sum form", h1, MdTools.HashFromSums(Encoding.UTF8.GetBytes(h1 + " *" + MdTools.DenoAsset + "\n"), MdTools.DenoAsset, false));
            T.Eq("tools: two different hashes without a name are ambiguous", null,
                 MdTools.HashFromSums(Encoding.UTF8.GetBytes("Hash : " + h1 + "\nHash : " + h2 + "\n"), MdTools.DenoAsset, false));
            T.Eq("tools: an empty sum file gives nothing", null, MdTools.HashFromSums(new byte[0], MdTools.DenoAsset, false));
        }

        // ------------------------------------------------------------------ //
        //  Процессы: без оболочки, кавычки, таймаут, отмена с деревом, предел вывода
        // ------------------------------------------------------------------ //
        private static void ToolsProcessCases(string root, byte[] stubBytes)
        {
            string sys = Environment.SystemDirectory;
            string where = Path.Combine(sys, "where.exe");
            MdRunResult r = MdProc.Run(where, "where.exe", 15000, null, 1 << 20);
            T.Check("tools: real where.exe runs without a shell, exit 0, stdout captured",
                    r.Ok && r.Stdout.IndexOf("where.exe", StringComparison.OrdinalIgnoreCase) >= 0, r.Describe() + " " + r.Stdout);
            r = MdProc.Run(where, "/Q wpc-no-such-program-" + Guid.NewGuid().ToString("N") + ".exe", 15000, null, 1 << 20);
            T.Check("tools: non-zero exit code of a real process is a failure", !r.Ok && r.ExitCode == 1, r.Describe());
            r = MdProc.Run(Path.Combine(Path.Combine(root, "no such dir"), "nope.exe"), "", 5000, null, 1024);
            T.Check("tools: missing exe = start error, no exception", !r.Ok && r.StartError.Length > 0, r.Describe());
            Stopwatch w = Stopwatch.StartNew();
            r = MdProc.Run(Path.Combine(sys, "PING.EXE"), "-n 30 127.0.0.1", 800, null, 1 << 20);
            T.Check("tools: timeout stops a long real process (ping 127.0.0.1) within seconds", r.TimedOut && !r.Ok && w.ElapsedMilliseconds < 12000,
                    w.ElapsedMilliseconds + " ms, " + r.Describe());
            if (stubBytes == null) return;

            string dir = Fx.MakeDir(root, "proc кавычки");
            string stub = Path.Combine(dir, "stub echo.exe");
            File.WriteAllBytes(stub, stubBytes);
            string[] odd = { "plain", "a b", "\"quoted\"", "тест кириллица «ёлка»", "C:\\dir with space\\", "back\\\"slash", "", "tab\tx", "a\\\\b", "\\\\server\\share\\", "end\\", "--", "-J" };
            r = MdProc.Run(stub, "--echo " + MdProc.Join(odd), 15000, null, 1 << 20);
            string[] lines = r.Stdout.Replace("\r", "").Split('\n');
            int count = lines.Length;
            if (count > 0 && lines[count - 1].Length == 0) count--;          // хвост после последнего перевода строки
            List<string> back = new List<string>();
            for (int i = 0; i < count; i++) back.Add(Encoding.UTF8.GetString(Convert.FromBase64String(lines[i])));
            bool same = r.Ok && back.Count == odd.Length;
            string diff = "";
            for (int i = 0; same && i < odd.Length; i++)
                if (back[i] != odd[i]) { same = false; diff = i + ": [" + back[i] + "] vs [" + odd[i] + "]"; }
            T.Check("tools: arguments with spaces, quotes, backslashes, tabs, empty and Cyrillic reach the process byte-exact", same,
                    r.Describe() + " got " + back.Count + " " + diff);

            string pidFile = Path.Combine(dir, "child.pid");
            w.Restart();
            r = MdProc.Run(stub, "--spawn " + MdProc.Quote(pidFile), 60000, delegate { return w.ElapsedMilliseconds > 1500; }, 1 << 20);
            long cancelMs = w.ElapsedMilliseconds;
            string childInfo;
            bool childGone = ToolsChildGone(pidFile, out childInfo);
            T.Check("tools: cancel closes the process and the child it spawned (job object), within seconds",
                    r.Cancelled && childGone && cancelMs < 15000, cancelMs + " ms, " + r.Describe() + ", child " + childInfo);

            w.Restart();
            r = MdProc.Run(stub, "--big 3000000", 15000, null, 1 << 20);
            T.Check("tools: stdout over the cap is refused and the process closed", r.TooLarge && !r.Ok && r.Stdout.Length == 0 && w.ElapsedMilliseconds < 15000, r.Describe());
            r = MdProc.Run(stub, "--sleep 30000", 700, null, 1 << 20);
            T.Check("tools: timeout on a sleeping stub", r.TimedOut && !r.Ok, r.Describe());
        }

        private static bool ToolsChildGone(string pidFile, out string info)
        {
            info = "";
            int pid;
            if (!File.Exists(pidFile) || !int.TryParse(File.ReadAllText(pidFile).Trim(), out pid)) { info = "no pid file"; return false; }
            info = "pid " + pid;
            try
            {
                using (Process c = Process.GetProcessById(pid))
                {
                    if (!string.Equals(c.ProcessName, "PING", StringComparison.OrdinalIgnoreCase)) return true;   // номер уже занят другим процессом
                    bool exited = c.WaitForExit(5000);
                    if (!exited) { info += " still alive"; try { c.Kill(); } catch (Exception) { } }
                    return exited;
                }
            }
            catch (ArgumentException) { return true; }
            catch (InvalidOperationException) { return true; }
        }

        // ------------------------------------------------------------------ //
        //  Разбор ответа: записанный живой ответ и таблица особых случаев
        // ------------------------------------------------------------------ //
        private static void ToolsMappingCases()
        {
            DateTime fixedNow = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            MdTools.Clock = delegate { return fixedNow; };
            Tr.En = true;
            string err;
            MdManifest m = MdYtdlp.FromJson(ToolsSampleJson, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", out err);
            T.Check("ytdlp: the recorded YouTube answer maps to a manifest", m != null && err.Length == 0 && m.Refused.Length == 0, err);
            if (m != null) ToolsCheckRecorded(m, "ytdlp: recorded");

            string syn = "{\"title\":\"t\",\"formats\":[" +
                "{\"format_id\":\"a1\",\"protocol\":\"https\",\"url\":\"https://cdn.example/a1?expire=1789407000\",\"ext\":\"webm\",\"vcodec\":\"none\",\"acodec\":\"opus\",\"tbr\":70}," +
                "{\"format_id\":\"v1\",\"protocol\":\"https\",\"url\":\"https://cdn.example/v1\",\"ext\":\"mp4\",\"vcodec\":\"avc1.640028\",\"acodec\":\"none\",\"height\":720,\"width\":1280,\"fps\":25,\"tbr\":900," +
                    "\"http_headers\":{\"Referer\":\"https://site.example/\",\"Cookie\":\"sid=secret\",\"Authorization\":\"Bearer secret\",\"X-Auth-Token\":\"secret\",\"X-Bad\":\"a\\r\\nInjected: 1\"}}," +
                "{\"format_id\":\"m1\",\"protocol\":\"https\",\"url\":\"https://cdn.example/m1\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"mp4a.40.2\",\"height\":360}," +
                "{\"format_id\":\"u1\",\"protocol\":\"https\",\"url\":\"https://cdn.example/u1\",\"ext\":\"m4a\",\"audio_ext\":\"m4a\",\"video_ext\":\"none\"}," +
                "{\"format_id\":\"sb\",\"protocol\":\"mhtml\",\"url\":\"https://i.example/sb\",\"ext\":\"mhtml\",\"vcodec\":\"none\",\"acodec\":\"none\"}," +
                "{\"format_id\":\"r1\",\"protocol\":\"rtmp\",\"url\":\"rtmp://x.example/live\",\"vcodec\":\"h264\",\"acodec\":\"aac\"}," +
                "{\"format_id\":\"f1\",\"protocol\":\"https\",\"url\":\"file:///C:/Windows/win.ini\",\"vcodec\":\"h264\",\"acodec\":\"aac\"}," +
                "{\"format_id\":\"h1\",\"protocol\":\"m3u8_native\",\"url\":\"https://cdn.example/h1/index.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"mp4a.40.2\",\"height\":1080}," +
                "{\"format_id\":\"d1\",\"protocol\":\"http_dash_segments\",\"fragment_base_url\":\"https://cdn.example/dash/\",\"manifest_url\":\"https://cdn.example/dash/m.mpd\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"none\",\"height\":480," +
                    "\"fragments\":[{\"path\":\"init.mp4\"},{\"path\":\"seg-1.m4s\",\"duration\":4},{\"path\":\"seg-2.m4s\",\"duration\":4}]}," +
                "{\"format_id\":\"v1\",\"protocol\":\"https\",\"url\":\"https://cdn.example/dup\",\"ext\":\"mp4\",\"vcodec\":\"avc1\",\"acodec\":\"none\",\"height\":240}" +
                "],\"subtitles\":{\"ru\":[{\"ext\":\"json3\",\"url\":\"https://s.example/ru.json3\"},{\"ext\":\"vtt\",\"url\":\"https://s.example/ru.vtt\",\"name\":\"Русский\"}]," +
                "\"xx\":[{\"ext\":\"vtt\",\"url\":\"javascript:alert(1)\"}]}," +
                "\"automatic_captions\":{\"en\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/en.vtt\",\"name\":\"English\"}]}}";
            Tr.En = false;
            m = MdYtdlp.FromJson(syn, "https://site.example/v", out err);
            T.Check("ytdlp: the synthetic format table maps", m != null, err);
            if (m != null)
            {
                MdTrack a1 = ToolsTrack(m, "a1"), v1 = ToolsTrack(m, "v1"), m1 = ToolsTrack(m, "m1"), u1 = ToolsTrack(m, "u1"), h1 = ToolsTrack(m, "h1"), d1 = ToolsTrack(m, "d1");
                T.Check("ytdlp: vcodec none ⇒ Audio, acodec none ⇒ Video, both ⇒ Muxed, video_ext none ⇒ Audio",
                        a1 != null && a1.Kind == MdTrackKind.Audio && v1 != null && v1.Kind == MdTrackKind.Video && m1 != null && m1.Kind == MdTrackKind.Muxed
                        && u1 != null && u1.Kind == MdTrackKind.Audio);
                T.Check("ytdlp: storyboard, rtmp and file: formats are skipped",
                        ToolsTrack(m, "sb") == null && ToolsTrack(m, "r1") == null && ToolsTrack(m, "f1") == null);
                T.Check("ytdlp: m3u8 protocol ⇒ Layout Ts with the media playlist URL", h1 != null && h1.Layout == MdLayout.Ts && h1.Url == "https://cdn.example/h1/index.m3u8");
                T.Check("ytdlp: DASH fragments ⇒ segments against fragment_base_url, Layout Fmp4, URL = the MPD",
                        d1 != null && d1.Layout == MdLayout.Fmp4 && d1.Segments.Count == 3 && d1.Segments[1].Url == "https://cdn.example/dash/seg-1.m4s"
                        && d1.Url == "https://cdn.example/dash/m.mpd", d1 == null ? "" : d1.Segments.Count + " " + d1.Url);
                T.Check("ytdlp: sensitive and injected headers dropped, Referer kept",
                        v1 != null && v1.Headers.Count == 1 && v1.Headers.ContainsKey("Referer"),
                        v1 == null ? "" : string.Join(",", new List<string>(v1.Headers.Keys).ToArray()));
                MdTrack dup = ToolsTrack(m, "v1-2");
                T.Check("ytdlp: duplicate format_id gets a unique track id", dup != null && dup.FormatId == "v1" && dup.Url == "https://cdn.example/dup");
                T.Eq("ytdlp: expire= in a format URL ⇒ ExpiresUtc", new DateTime(2026, 9, 14, 17, 30, 0, DateTimeKind.Utc), m.ExpiresUtc);
                T.Eq("ytdlp: subtitles — vtt entries only, unusable URL skipped, auto captions marked (авто)", "sub-ru=Русский;auto-en=English (авто)", ToolsSubs(m));
                T.Check("ytdlp: every variant points at the subtitle group when there are subtitles",
                        m.Variants.Count > 0 && m.Variants.TrueForAll(delegate(MdVariant v) { return v.SubtitleGroup == MdYtdlp.SubtitleGroupId; }));
                T.Eq("ytdlp: variants sorted by height (1080 m3u8, 720, 480 dash, 360 muxed, 240)", "h1,v1,d1,m1,v1-2", ToolsIds(m.Variants));
                MdVariant vv = ToolsVariant(m, "v1"), mv = ToolsVariant(m, "m1");
                T.Check("ytdlp: a video-only variant uses the audio group, a muxed one does not",
                        vv != null && vv.AudioGroup == MdYtdlp.AudioGroupId && mv != null && mv.AudioGroup == "");
                T.Eq("ytdlp: Russian label with comma decimals", "720p · 25 к/с · 900 кбит/с · H.264", vv == null ? "" : vv.Label);
            }

            m = MdYtdlp.FromJson("{\"is_live\":true,\"title\":\"live\",\"formats\":[{\"format_id\":\"hls\",\"protocol\":\"m3u8\",\"url\":\"https://live.example/x/index.m3u8\",\"vcodec\":\"avc1\",\"acodec\":\"mp4a\"}]}",
                                 "https://live.example/x", out err);
            T.Check("ytdlp: is_live ⇒ live manifest and track; no expire ⇒ now + 5 h",
                    m != null && m.Live && m.Variants.Count == 1 && m.Variants[0].Main.Live && m.ExpiresUtc == fixedNow.AddHours(5), err);
            m = MdYtdlp.FromJson("{\"formats\":[{\"format_id\":\"x\",\"protocol\":\"https\",\"url\":\"https://r.example/videoplayback/expire/1789407259/id/abc\",\"vcodec\":\"avc1\",\"acodec\":\"none\"}]}",
                                 "https://r.example/v", out err);
            T.Eq("ytdlp: /expire/<unix>/ in the path ⇒ ExpiresUtc", new DateTime(2026, 9, 14, 17, 34, 19, DateTimeKind.Utc), m == null ? DateTime.MinValue : m.ExpiresUtc);
            m = MdYtdlp.FromJson("{\"formats\":[{\"format_id\":\"w\",\"protocol\":\"https\",\"url\":\"https://drm.example/a\",\"has_drm\":true,\"vcodec\":\"avc1\",\"acodec\":\"none\"}," +
                                 "{\"format_id\":\"w2\",\"protocol\":\"https\",\"url\":\"https://drm.example/b\",\"has_drm\":true,\"vcodec\":\"none\",\"acodec\":\"mp4a\"}]}", "https://drm.example/v", out err);
            T.Check("ytdlp: DRM-only formats ⇒ Refused manifest, no variants, no error", m != null && m.Refused.Length > 0 && m.Variants.Count == 0 && err.Length == 0, err);
            m = MdYtdlp.FromJson("{\"_type\":\"playlist\",\"title\":\"p\",\"entries\":[{\"id\":\"1\"},{\"id\":\"2\"},{\"id\":\"3\"}]}", "https://www.youtube.com/playlist?list=x", out err);
            T.Check("ytdlp: playlist ⇒ error naming the count", m == null && err.IndexOf("(3)", StringComparison.Ordinal) >= 0, err);
            m = MdYtdlp.FromJson("{\"title\":\"x\",\"formats\":[]}", "https://e.example/v", out err);
            T.Check("ytdlp: no formats ⇒ error", m == null && err.Length > 0);
            m = MdYtdlp.FromJson("ERROR: not json", "https://e.example/v", out err);
            T.Check("ytdlp: non-JSON output ⇒ error, no exception", m == null && err.Length > 0);
            m = MdYtdlp.FromJson(new string('[', 100000) + new string(']', 100000), "https://e.example/v", out err);
            T.Check("ytdlp: 100000-deep nesting refused before parsing (no stack overflow)", m == null && err.Length > 0);
            m = MdYtdlp.FromJson("{\"title\":\"one\",\"url\":\"https://direct.example/v.mp4\",\"ext\":\"mp4\",\"format_id\":\"0\"}", "https://direct.example/page", out err);
            T.Check("ytdlp: an answer without a formats list maps to a single variant",
                    m != null && m.Variants.Count == 1 && m.Variants[0].Main.Url == "https://direct.example/v.mp4" && m.Variants[0].Main.Layout == MdLayout.Mp4, err);
            Tr.En = true;
        }

        private static void ToolsCheckRecorded(MdManifest m, string p)
        {
            T.Eq(p + " title", "Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film", m.Title);
            T.Eq(p + " duration", 635.0, m.DurationSec);
            T.Check(p + " not live, source Ytdlp, page address kept",
                    !m.Live && m.Source == MdSource.Ytdlp && m.PageUrl.StartsWith("https://www.youtube.com/watch?v=aqz-KE-bpKQ", StringComparison.Ordinal));
            T.Eq(p + " variants sorted by height/fps/bandwidth, storyboard skipped", "299,303,399,136,18", ToolsIds(m.Variants));
            T.Eq(p + " audio sorted by bandwidth", "258,380,140,251", ToolsIds(m.Audio));
            MdTrack v299 = ToolsTrack(m, "299"), v303 = ToolsTrack(m, "303"), m18 = ToolsTrack(m, "18"), a140 = ToolsTrack(m, "140"), a251 = ToolsTrack(m, "251");
            T.Check(p + " kinds and layouts: 299 Video Mp4, 303 Video WebM, 18 Muxed Mp4, 140 Audio Mp4, 251 Audio WebM",
                    v299 != null && v299.Kind == MdTrackKind.Video && v299.Layout == MdLayout.Mp4
                    && v303 != null && v303.Kind == MdTrackKind.Video && v303.Layout == MdLayout.WebM
                    && m18 != null && m18.Kind == MdTrackKind.Muxed && m18.Layout == MdLayout.Mp4
                    && a140 != null && a140.Kind == MdTrackKind.Audio && a140.Layout == MdLayout.Mp4
                    && a251 != null && a251.Kind == MdTrackKind.Audio && a251.Layout == MdLayout.WebM);
            if (v299 == null || m18 == null || a251 == null || a140 == null) return;
            T.Eq(p + " codecs of 299 / 18 / 251", "avc1.64002a|avc1.42001E,mp4a.40.2|opus", v299.Codec + "|" + m18.Codec + "|" + a251.Codec);
            T.Eq(p + " 299 geometry and bandwidth", "1920x1080@60 3247821", v299.Width + "x" + v299.Height + "@" + v299.Fps + " " + v299.Bandwidth);
            T.Eq(p + " size hints: filesize of 299, filesize_approx of 18", "257619653|28526904", v299.SizeHint + "|" + m18.SizeHint);
            T.Eq(p + " http_chunk_size ⇒ ChunkBytes", 10485760L, v299.ChunkBytes);
            T.Check(p + " four safe headers kept, the format URL is https",
                    v299.Headers.Count == 4 && v299.Headers.ContainsKey("User-Agent") && v299.Url.StartsWith("https://", StringComparison.Ordinal), v299.Headers.Count.ToString());
            T.Eq(p + " expire= ⇒ ExpiresUtc", new DateTime(2026, 9, 14, 17, 34, 19, DateTimeKind.Utc), m.ExpiresUtc);
            MdVariant first = m.Variants[0];
            MdVariant muxed = ToolsVariant(m, "18");
            T.Check(p + " video-only variant has the audio group, the muxed one has none",
                    first.AudioGroup == MdYtdlp.AudioGroupId && muxed != null && muxed.AudioGroup == "");
            bool was = Tr.En;
            Tr.En = true;
            try
            {
                T.Eq(p + " label of 299", "1080p · 60 fps · 3.2 Mbit/s · H.264", MdYtdlp.Label(v299));
                T.Eq(p + " label of 18", "360p · 30 fps · 360 kbit/s · H.264+AAC", MdYtdlp.Label(m18));
            }
            finally { Tr.En = was; }
            bool pair = false;
            foreach (MdVariant v in m.Variants)
                foreach (MdTrack a in m.Audio)
                    if (v.Main.Kind == MdTrackKind.Video && v.Main.Layout == MdLayout.Mp4 && v.Main.Codec.StartsWith("avc1", StringComparison.Ordinal)
                        && a.Layout == MdLayout.Mp4 && a.Codec.StartsWith("mp4a", StringComparison.Ordinal)) pair = true;
            T.Check(p + " an MP4-muxable pair exists (H.264 video + AAC audio)", pair);
            DlAddRequest req = MdYtdlp.ToAddRequest(m, first, a140, null, MdOutput.Auto);
            T.Check(p + " add request: page address, media state with format ids 299+140, no sensitive headers",
                    req.Media != null && req.Url == m.PageUrl && req.Media.Source == MdSource.Ytdlp && req.Media.FormatIds == "299+140"
                    && req.Media.VariantId == "299" && req.Media.AudioId == "140" && req.FileName.EndsWith(".mp4", StringComparison.Ordinal)
                    && !req.Media.Headers.ContainsKey("Cookie"), req.Media == null ? "" : req.Media.FormatIds + " " + req.FileName);
        }

        private static MdTrack ToolsTrack(MdManifest m, string id)
        {
            foreach (MdVariant v in m.Variants) if (v.Main != null && v.Main.Id == id) return v.Main;
            foreach (MdTrack t in m.Audio) if (t.Id == id) return t;
            return null;
        }

        private static MdVariant ToolsVariant(MdManifest m, string id)
        {
            foreach (MdVariant v in m.Variants) if (v.Id == id) return v;
            return null;
        }

        private static string ToolsIds(List<MdVariant> list)
        {
            List<string> ids = new List<string>();
            foreach (MdVariant v in list) ids.Add(v.Id);
            return string.Join(",", ids.ToArray());
        }

        private static string ToolsIds(List<MdTrack> list)
        {
            List<string> ids = new List<string>();
            foreach (MdTrack t in list) ids.Add(t.Id);
            return string.Join(",", ids.ToArray());
        }

        private static string ToolsSubs(MdManifest m)
        {
            List<string> s = new List<string>();
            foreach (MdTrack t in m.Subtitles) s.Add(t.Id + "=" + t.Name);
            return string.Join(";", s.ToArray());
        }

        // ------------------------------------------------------------------ //
        //  Классы ошибок yt-dlp
        // ------------------------------------------------------------------ //
        private static void ToolsErrorCases()
        {
            string[][] table =
            {
                new[] { "ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.", "Age" },
                new[] { "ERROR: [youtube] aqz-KE-bpKQ: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.", "Login" },
                new[] { "ERROR: [youtube] x: Private video. Sign in if you've been granted access to this video", "Login" },
                new[] { "ERROR: [youtube] x: The uploader has not made this video available in your country", "Geo" },
                new[] { "ERROR: [youtube] x: Video unavailable. This video has been removed by the uploader", "Unavailable" },
                new[] { "ERROR: Unsupported URL: https://example.com/page", "Unsupported" },
                new[] { "WARNING: [youtube] x: n challenge solving failed: Some formats may be missing. Ensure you have a supported JavaScript runtime", "JsRuntime" },
                new[] { "ERROR: [generic] Unable to download webpage: <urlopen error [Errno 11001] getaddrinfo failed>", "Network" },
                new[] { "ERROR: something odd happened", "Other" },
                new[] { "", "Other" },
            };
            foreach (string[] row in table)
                T.Eq("ytdlp: error class of «" + (row[0].Length > 48 ? row[0].Substring(0, 48) : row[0]) + "»", row[1], MdYtdlp.Classify(row[0]).ToString());
            string other = MdYtdlp.ErrorText(MdYtdlpError.Other, "WARNING: a\nERROR: failed https://cdn.example/v?token=SECRET123&sig=abc\n");
            T.Check("ytdlp: an unknown error shows the last ERROR line with the URL query stripped",
                    other.IndexOf("failed https://cdn.example/v", StringComparison.Ordinal) >= 0 && other.IndexOf("SECRET123", StringComparison.Ordinal) < 0, other);
            HashSet<string> texts = new HashSet<string>();
            foreach (MdYtdlpError k in new[] { MdYtdlpError.Login, MdYtdlpError.Age, MdYtdlpError.Geo, MdYtdlpError.Unavailable, MdYtdlpError.Unsupported,
                                               MdYtdlpError.JsRuntime, MdYtdlpError.Network, MdYtdlpError.Timeout })
                texts.Add(MdYtdlp.ErrorText(k, "ERROR: x"));
            T.Eq("ytdlp: every error class has its own message", 8, texts.Count);
        }

        // ------------------------------------------------------------------ //
        //  Установка с «GitHub» на петле, затем извлечение настоящим процессом
        // ------------------------------------------------------------------ //
        private sealed class ToolsFakeGh
        {
            public readonly DlTestServer Srv;
            public ToolsFakeGh(DlTestServer s) { Srv = s; }

            public DlTestServer.Res Latest(string repo, string tag)
            {
                DlTestServer.Res r = new DlTestServer.Res();
                r.RedirectTo = Srv.Url(repo + "/releases/tag/" + tag);
                return Srv.Add(repo + "/releases/latest", r);
            }

            public DlTestServer.Res Text(string repo, string tag, string name, byte[] body)
            {
                DlTestServer.Res r = new DlTestServer.Res();
                r.Body = body;
                r.Size = body.Length;
                return Srv.Add(repo + "/releases/download/" + tag + "/" + name, r);
            }

            // Файл выпуска — редиректом на другой хост (localhost вместо 127.0.0.1), как у GitHub на release-assets.
            public DlTestServer.Res Asset(string repo, string tag, string name, byte[] body)
            {
                string key = "assets/" + repo.Replace('/', '_') + "/" + tag + "/" + name;
                DlTestServer.Res a = new DlTestServer.Res();
                a.Body = body;
                a.Size = body.Length;
                Srv.Add(key, a);
                DlTestServer.Res hop = new DlTestServer.Res();
                hop.RedirectTo = Srv.UrlLocalhost(key);
                Srv.Add(repo + "/releases/download/" + tag + "/" + name, hop);
                return a;
            }

            public void Redirect(string repo, string tag, string name, string to)
            {
                DlTestServer.Res hop = new DlTestServer.Res();
                hop.RedirectTo = to;
                Srv.Add(repo + "/releases/download/" + tag + "/" + name, hop);
            }
        }

        private static DlTestServer.Res ToolsPublishYt(ToolsFakeGh gh, string tag, byte[] exe, string sums)
        {
            gh.Latest(MdTools.YtdlpRepo, tag);
            if (sums == null)
                sums = ToolsSha(new byte[] { 1, 2, 3 }) + "  yt-dlp_linux\n" + ToolsSha(exe) + "  " + MdTools.YtdlpAsset + "\n" + ToolsSha(new byte[] { 4 }) + "  yt-dlp_macos\n";
            gh.Text(MdTools.YtdlpRepo, tag, MdTools.YtdlpSums, Encoding.UTF8.GetBytes(sums));
            return gh.Asset(MdTools.YtdlpRepo, tag, MdTools.YtdlpAsset, exe);
        }

        private static DlTestServer.Res ToolsPublishDeno(ToolsFakeGh gh, string tag, byte[] zip, bool formatList)
        {
            gh.Latest(MdTools.DenoRepo, tag);
            string sum = formatList
                ? "\r\nAlgorithm : SHA256\r\nHash      : " + ToolsSha(zip).ToUpperInvariant() + "\r\nPath      : D:\\a\\deno\\deno\\target\\release\\" + MdTools.DenoAsset + "\r\n\r\n\r\n"
                : ToolsSha(zip) + "  " + MdTools.DenoAsset + "\n";
            gh.Text(MdTools.DenoRepo, tag, MdTools.DenoAsset + ".sha256sum", Encoding.UTF8.GetBytes(sum));
            return gh.Asset(MdTools.DenoRepo, tag, MdTools.DenoAsset, zip);
        }

        private static byte[] ToolsDenoExe(byte[] stub, string version)
        {
            return ToolsStubWith(stub, string.Format(ToolsDenoLine, version));
        }

        private static byte[] ToolsDenoZip(byte[] stub, string version)
        {
            return ToolsMakeZip(ToolsEntry(MdTools.DenoExe, ToolsDenoExe(stub, version)));
        }

        private static string ToolsLeftovers(string dir)
        {
            List<string> bad = new List<string>();
            foreach (string f in Directory.GetFiles(dir))
            {
                string n = Path.GetFileName(f);
                if (n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".new.exe", StringComparison.OrdinalIgnoreCase)
                    || n.EndsWith(".exe.bak", StringComparison.OrdinalIgnoreCase)) bad.Add(n);
            }
            return string.Join(",", bad.ToArray());
        }

        // Фоновая проверка идёт в своём потоке: ждём результата, а не спим наугад.
        private static bool ToolsWait(Func<bool> done)
        {
            for (int i = 0; i < 200; i++)
            {
                if (done()) return true;
                Thread.Sleep(25);
            }
            return false;
        }
    }

    // Сервер, обещающий в Content-Length весь файл и закрывающий соединение на половине.
    internal sealed class ToolsTruncServer : IDisposable
    {
        private readonly TcpListener _l = new TcpListener(IPAddress.Loopback, 0);
        private readonly byte[] _body;
        private readonly int _send;
        private readonly Thread _t;
        private volatile bool _stop;
        public readonly int Port;
        public int Served;

        public ToolsTruncServer(byte[] body, int send)
        {
            _body = body;
            _send = send;
            _l.Start();
            Port = ((IPEndPoint)_l.LocalEndpoint).Port;
            _t = new Thread(Loop);
            _t.IsBackground = true;
            _t.Start();
        }

        private void Loop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _l.AcceptTcpClient(); }
                catch (SocketException) { return; }
                catch (ObjectDisposedException) { return; }
                catch (InvalidOperationException) { return; }
                try
                {
                    c.ReceiveTimeout = 5000;
                    NetworkStream s = c.GetStream();
                    byte[] buf = new byte[8192];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = s.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                        if (Encoding.ASCII.GetString(buf, 0, got).IndexOf("\r\n\r\n", StringComparison.Ordinal) >= 0) break;
                    }
                    byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/octet-stream\r\nContent-Length: "
                                                          + _body.Length + "\r\nConnection: close\r\n\r\n");
                    s.Write(head, 0, head.Length);
                    s.Write(_body, 0, _send);
                    s.Flush();
                    Interlocked.Increment(ref Served);
                }
                catch (IOException) { }
                catch (SocketException) { }
                finally { c.Close(); }
            }
        }

        public void Dispose()
        {
            _stop = true;
            try { _l.Stop(); } catch (SocketException) { }
            _t.Join(3000);
        }
    }
}
