// SysDeck — тесты инструментов медиа: установка, извлечение, аргументы, запись эфира.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
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
        private static void ToolsInstallAndExtract(string root, byte[] stub)
        {
            string tools = Fx.MakeDir(root, "tools dir тест");
            MdTools.DirOverride = tools;
            DateTime now = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);
            MdTools.Clock = delegate { return now; };
            MdTools.ForgetVerified();
            string runsFile = Path.Combine(tools, "stub-runs.txt");
            using (DlTestServer srv = new DlTestServer())
            {
                MdTools.GitHubBase = "http://127.0.0.1:" + srv.Port;
                ToolsFakeGh gh = new ToolsFakeGh(srv);
                string err;
                T.Check("tools: nothing installed ⇒ yt-dlp not available", !MdYtdlp.Available && !MdTools.YtdlpReady && !MdTools.DenoReady);
                bool called = false;
                MdYtdlp.Runner = delegate { called = true; return ""; };
                MdManifest none = MdYtdlp.Extract("https://www.youtube.com/watch?v=x", null, out err);
                T.Check("ytdlp: Extract without the tools ⇒ clear error, nothing started", none == null && err.Length > 0 && !called, err);
                MdYtdlp.Runner = _toolsRunner;

                // 1. Установка с нуля.
                byte[] yt1 = ToolsStubWith(stub, "2025.01.01");
                DlTestServer.Res yt1Asset = ToolsPublishYt(gh, "2025.01.01", yt1, null);
                DlTestServer.Res deno231Asset = ToolsPublishDeno(gh, "v2.3.1", ToolsDenoZip(stub, "2.3.1"), true);
                double lastP = -1;
                string lastStage = "";
                bool ok = MdTools.Install(false, delegate(string s, double p) { lastStage = s; lastP = p; }, null, out err);
                T.Check("tools: fresh install goes releases/latest → tag → sums → redirect to the asset host",
                        ok && yt1Asset.Requests == 1 && deno231Asset.Requests == 1, err + " yt=" + yt1Asset.Requests + " deno=" + deno231Asset.Requests);
                T.Check("tools: the installed yt-dlp.exe is byte-identical to the published one", ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1));
                T.Check("tools: deno.exe extracted from the zip byte-identical", ToolsFileSha(MdTools.DenoPath) == ToolsSha(ToolsDenoExe(stub, "2.3.1")));
                T.Check("tools: available after the install, progress reached done", MdYtdlp.Available && lastStage == "done" && lastP == 1.0, lastStage + " " + lastP);
                MdToolStatus st = MdTools.Status();
                T.Check("tools: status really runs --version of both and reads the tags",
                        st.YtdlpVersion == "2025.01.01" && st.DenoVersion == "2.3.1" && st.LatestYtdlp == "2025.01.01" && st.LatestDeno == "v2.3.1"
                        && !st.UpdateAvailable && st.AutoUpdate && st.YtdlpInstalled && st.DenoInstalled,
                        st.YtdlpVersion + "/" + st.DenoVersion + "/" + st.LatestYtdlp + "/" + st.LatestDeno + "/" + st.UpdateAvailable);
                T.Eq("tools: no temporary files left after an install", "", ToolsLeftovers(tools));
                MdTools.SetAutoUpdate(false);
                T.Check("tools: the auto-update flag persists in tools.json", !MdTools.Status().AutoUpdate);

                // 2. Только обновление, теги те же — ничего не качается.
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: update-only with unchanged tags downloads nothing", ok && yt1Asset.Requests == 1 && deno231Asset.Requests == 1, err);

                // 3. Неверная сумма: не ставится, не запускается, прежний на месте.
                byte[] yt2 = ToolsStubWith(stub, "2025.02.02");
                ToolsPublishYt(gh, "2025.02.02", yt2, ToolsSha(new byte[] { 9, 9, 9 }) + "  " + MdTools.YtdlpAsset + "\n");
                ok = MdTools.Install(true, null, null, out err);
                string runs = File.Exists(runsFile) ? File.ReadAllText(runsFile) : "";
                T.Check("tools: SHA-256 mismatch refused, the previous yt-dlp.exe kept",
                        !ok && err.Length > 0 && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1) && MdTools.YtdlpReady, err);
                T.Check("tools: a file with a bad checksum is never started", runs.IndexOf("2025.02.02", StringComparison.Ordinal) < 0, runs.Replace("\n", "; "));
                T.Eq("tools: no temporary files left after a refused install", "", ToolsLeftovers(tools));

                // 4. Файл сумм без строки yt-dlp.exe.
                ToolsPublishYt(gh, "2025.02.03", yt2, ToolsSha(yt2) + "  yt-dlp_linux\n");
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: sums without a yt-dlp.exe line refused", !ok && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1), err);

                // 5. Оборванная загрузка: заголовок обещает весь файл, приходит половина.
                byte[] yt4 = ToolsStubWith(stub, "2025.04.04");
                using (ToolsTruncServer trunc = new ToolsTruncServer(yt4, yt4.Length / 2))
                {
                    ToolsPublishYt(gh, "2025.04.04", yt4, null);
                    gh.Redirect(MdTools.YtdlpRepo, "2025.04.04", MdTools.YtdlpAsset, "http://127.0.0.1:" + trunc.Port + "/yt-dlp.exe");
                    ok = MdTools.Install(true, null, null, out err);
                    T.Check("tools: truncated download not installed, previous kept, nothing left over",
                            !ok && trunc.Served >= 1 && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1) && ToolsLeftovers(tools) == "", err + " served=" + trunc.Served);
                }

                // 6. Отмена посреди загрузки.
                byte[] slow = new byte[4 << 20];
                Array.Copy(yt4, slow, yt4.Length);
                DlTestServer.Res slowAsset = ToolsPublishYt(gh, "2025.04.05", slow, null);
                slowAsset.RateBps = 400000;
                Stopwatch cw = Stopwatch.StartNew();
                ok = MdTools.Install(true, null, delegate { return cw.ElapsedMilliseconds > 600; }, out err);
                T.Check("tools: cancel during a download stops within seconds, previous kept, nothing left over",
                        !ok && cw.ElapsedMilliseconds < 10000 && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1) && ToolsLeftovers(tools) == "",
                        cw.ElapsedMilliseconds + " ms " + err);

                // 7. Сумма верна, но exe не отвечает на --version.
                ToolsPublishYt(gh, "2025.05.05", ToolsStubWith(stub, "EXIT"), null);
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: a new exe failing --version is not placed, previous kept",
                        !ok && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt1) && ToolsLeftovers(tools) == "", err);

                // 8. Обновление: новый exe встаёт на место одной заменой, .bak не остаётся.
                byte[] yt3 = ToolsStubWith(stub, "2025.03.03");
                ToolsPublishYt(gh, "2025.03.03", yt3, null);
                ok = MdTools.Install(true, null, null, out err);
                runs = File.ReadAllText(runsFile);
                T.Check("tools: the update replaces yt-dlp.exe only after the new file answered under a temporary name",
                        ok && ToolsFileSha(MdTools.YtdlpPath) == ToolsSha(yt3) && runs.IndexOf("yt-dlp.new.exe 2025.03.03", StringComparison.Ordinal) >= 0
                        && MdTools.Status().YtdlpVersion == "2025.03.03" && ToolsLeftovers(tools) == "", err + " | " + runs.Replace("\n", "; "));

                // 9. Deno ниже 2.3.0.
                ToolsPublishDeno(gh, "v2.2.9", ToolsDenoZip(stub, "2.2.9"), false);
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: Deno below 2.3.0 refused, the previous deno.exe kept",
                        !ok && err.IndexOf("2.3.0", StringComparison.Ordinal) >= 0 && ToolsFileSha(MdTools.DenoPath) == ToolsSha(ToolsDenoExe(stub, "2.3.1"))
                        && ToolsLeftovers(tools) == "", err);

                // 10. Архив Deno с «..\deno.exe» и с лишней записью — через настоящую установку.
                ToolsPublishDeno(gh, "v2.4.0", ToolsMakeZip(ToolsEntry("..\\deno.exe", ToolsDenoExe(stub, "2.4.0"))), false);
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: a Deno archive with ..\\deno.exe refused through install, nothing written outside the folder",
                        !ok && err.Length > 0 && !File.Exists(Path.Combine(root, MdTools.DenoExe)) && ToolsFileSha(MdTools.DenoPath) == ToolsSha(ToolsDenoExe(stub, "2.3.1")), err);
                ToolsPublishDeno(gh, "v2.4.1", ToolsMakeZip(ToolsEntry(MdTools.DenoExe, ToolsDenoExe(stub, "2.4.1")), ToolsEntry("evil.dll", new byte[] { 1 })), false);
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: a Deno archive with an extra entry refused through install",
                        !ok && !File.Exists(Path.Combine(tools, "evil.dll")) && ToolsLeftovers(tools) == "", err);
                ToolsPublishDeno(gh, "v2.5.0", ToolsDenoZip(stub, "2.5.0"), false);
                ok = MdTools.Install(true, null, null, out err);
                T.Check("tools: a Deno update (sha256sum form) replaces deno.exe", ok && ToolsFileSha(MdTools.DenoPath) == ToolsSha(ToolsDenoExe(stub, "2.5.0")), err);

                // 11. Проверка обновлений — не чаще раза в сутки.
                DlTestServer.Res latest = gh.Latest(MdTools.YtdlpRepo, "2025.03.03");
                bool avail;
                now = now.AddHours(1);
                bool ran = MdTools.CheckUpdate(false, null, out avail, out err);
                T.Check("tools: an update check within a day of the last one does not touch GitHub", !ran && latest.Requests == 0 && !avail, err + " " + latest.Requests);
                ran = MdTools.CheckUpdate(true, null, out avail, out err);
                T.Check("tools: a forced update check does touch GitHub", ran && latest.Requests == 1 && !avail, err + " " + latest.Requests);
                latest = gh.Latest(MdTools.YtdlpRepo, "2025.06.06");
                now = now.AddHours(25);
                ran = MdTools.CheckUpdate(false, null, out avail, out err);
                T.Check("tools: a day later the check runs and reports the newer tag",
                        ran && latest.Requests == 1 && avail && MdTools.Status().LatestYtdlp == "2025.06.06", err + " " + latest.Requests);
                gh.Latest(MdTools.YtdlpRepo, "2025.03.03");
                MdTools.CheckUpdate(true, null, out avail, out err);

                // 11а. Фоновая проверка из Tick очереди: флажок «держать yt-dlp свежим» должен доходить до GitHub сам.
                DlTestServer.Res auto = gh.Latest(MdTools.YtdlpRepo, "2025.07.07");
                DateTime tick = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
                now = now.AddHours(25);
                MdWiring.ToolsTick(false, tick);
                Thread.Sleep(200);
                T.Check("tools: the auto-update setting off ⇒ the tick never touches GitHub", auto.Requests == 0, "requests=" + auto.Requests);
                MdWiring.ToolsTick(true, tick);
                bool got = ToolsWait(delegate { return MdTools.Status().LatestYtdlp == "2025.07.07"; });
                T.Check("tools: the auto-update setting on ⇒ the tick asks GitHub and records the newer tag",
                        got && auto.Requests == 1, "requests=" + auto.Requests + " latest=" + MdTools.Status().LatestYtdlp);
                gh.Latest(MdTools.YtdlpRepo, "2025.08.08");
                MdWiring.ToolsTick(true, tick.AddHours(1));
                Thread.Sleep(200);
                T.Eq("tools: a second tick within the interval does not ask again", "2025.07.07", MdTools.Status().LatestYtdlp);
                gh.Latest(MdTools.YtdlpRepo, "2025.03.03");
                now = now.AddHours(25);
                MdTools.CheckUpdate(true, null, out avail, out err);

                // 12. Подмена установленного файла тем же размером и временем записи.
                byte[] cur = File.ReadAllBytes(MdTools.YtdlpPath);
                DateTime stamp = File.GetLastWriteTimeUtc(MdTools.YtdlpPath);
                byte[] tampered = (byte[])cur.Clone();
                tampered[tampered.Length - 1] ^= 0x5A;
                File.WriteAllBytes(MdTools.YtdlpPath, tampered);
                File.SetLastWriteTimeUtc(MdTools.YtdlpPath, stamp);
                MdTools.ForgetVerified();
                T.Check("tools: an exe changed after the install (same size and time) is not trusted", !MdTools.YtdlpReady && !MdYtdlp.Available);
                File.WriteAllBytes(MdTools.YtdlpPath, cur);
                File.SetLastWriteTimeUtc(MdTools.YtdlpPath, stamp);
                MdTools.ForgetVerified();
                T.Check("tools: the restored exe is trusted again", MdTools.YtdlpReady && MdYtdlp.Available);
            }

            ToolsExtractReal(tools);
            ToolsExtractSeam();
        }

        // Извлечение настоящим процессом: установленная заглушка печатает записанный ответ и записывает свои аргументы.
        private static void ToolsExtractReal(string tools)
        {
            MdYtdlp.Runner = _toolsRunner;
            string argsFile = Path.Combine(tools, "stub-args.txt");
            string stderrFile = Path.Combine(tools, "stub-stderr.txt");
            File.WriteAllText(Path.Combine(tools, "stub-json.txt"), ToolsSampleJson, new UTF8Encoding(false));
            if (File.Exists(argsFile)) File.Delete(argsFile);
            if (File.Exists(stderrFile)) File.Delete(stderrFile);
            string url = "https://www.youtube.com/watch?v=aqz-KE-bpKQ&t=1s";
            string err;
            MdManifest m = MdYtdlp.Extract(url, null, out err);
            T.Check("ytdlp: Extract through the real process maps the recorded answer", m != null && m.Variants.Count == 5 && m.Audio.Count == 4, err);
            List<List<string>> calls = ToolsArgs(argsFile);
            string[] expected = { "-J", "--no-playlist", "--flat-playlist", "--ignore-config", "--no-warnings", "--no-progress", "--no-cache-dir",
                                  "--js-runtimes", "deno:" + MdTools.DenoPath, "--", url };
            T.Check("ytdlp: the exact argument vector reached yt-dlp (deno path with a space and Cyrillic, URL after --)",
                    calls.Count == 1 && string.Join("\u0001", calls[0].ToArray()) == string.Join("\u0001", expected),
                    calls.Count == 0 ? "no call recorded" : string.Join(" | ", calls[0].ToArray()));

            File.WriteAllText(stderrFile, "ERROR: [youtube] aqz-KE-bpKQ: Sign in to confirm you’re not a bot. Use --cookies-from-browser or --cookies for the authentication.\n",
                              new UTF8Encoding(false));
            File.Delete(argsFile);
            m = MdYtdlp.Extract(url, null, out err);
            calls = ToolsArgs(argsFile);
            bool retried = calls.Count == 2 && calls[0].IndexOf("--extractor-args") < 0 && calls[1].IndexOf("--extractor-args") >= 0
                           && calls[1][calls[1].IndexOf("--extractor-args") + 1] == MdYtdlp.YoutubeFallbackArgs;
            T.Check("ytdlp: the bot check on YouTube ⇒ exactly one retry with the embedded player client, then the login message",
                    m == null && err == MdYtdlp.ErrorText(MdYtdlpError.Login, "") && retried, err + " calls=" + calls.Count);
            File.Delete(argsFile);
            m = MdYtdlp.Extract("https://vimeo.com/1084537", null, out err);
            T.Check("ytdlp: the same text on a non-YouTube host is not retried", m == null && ToolsArgs(argsFile).Count == 1, err);
            File.WriteAllText(stderrFile, "ERROR: [youtube] x: Sign in to confirm your age. This video may be inappropriate for some users.\n", new UTF8Encoding(false));
            m = MdYtdlp.Extract(url, null, out err);
            T.Eq("ytdlp: the age error of a real process ⇒ the age message", MdYtdlp.ErrorText(MdYtdlpError.Age, ""), err);
            File.Delete(stderrFile);
            MdManifest bad1 = MdYtdlp.Extract("file:///C:/x", null, out err);
            string err2;
            MdManifest bad2 = MdYtdlp.Extract("https://e.example/a b", null, out err2);
            T.Check("ytdlp: Extract refuses page addresses that are not http(s) or contain spaces", bad1 == null && err.Length > 0 && bad2 == null && err2.Length > 0);
        }

        private static List<List<string>> ToolsArgs(string file)
        {
            List<List<string>> calls = new List<List<string>>();
            if (!File.Exists(file)) return calls;
            List<string> cur = new List<string>();
            foreach (string raw in File.ReadAllText(file).Split('\n'))
            {
                string l = raw.Trim();
                if (l == "#") { calls.Add(cur); cur = new List<string>(); }
                else if (l.Length > 0) cur.Add(Encoding.UTF8.GetString(Convert.FromBase64String(l)));
            }
            return calls;
        }

        // Шов Runner: таймаут, отмена и записанный ответ.
        private static void ToolsExtractSeam()
        {
            string err;
            MdRunResult timeout = new MdRunResult();
            timeout.TimedOut = true;
            MdYtdlp.Runner = delegate { throw new MdRunException(timeout); };
            MdManifest m = MdYtdlp.Extract("https://www.youtube.com/watch?v=x", null, out err);
            T.Eq("ytdlp: a runner timeout ⇒ the timeout message", MdYtdlp.ErrorText(MdYtdlpError.Timeout, ""), m == null ? err : "");
            MdYtdlp.Runner = delegate { throw new OperationCanceledException(); };
            m = MdYtdlp.Extract("https://www.youtube.com/watch?v=x", null, out err);
            T.Check("ytdlp: a cancelled runner ⇒ null and the cancelled text", m == null && err.Length > 0, err);
            int calls = 0;
            MdYtdlp.Runner = delegate(string exe, string args, Func<bool> c) { calls++; return ToolsSampleJson; };
            m = MdYtdlp.Extract("https://www.youtube.com/watch?v=aqz-KE-bpKQ", null, out err);
            T.Check("ytdlp: the recorded answer through the Runner seam", m != null && calls == 1 && m.Variants.Count == 5, err);
            if (m != null) ToolsCheckRecorded(m, "ytdlp: via Runner");
            MdHooks.ExtractFn hook = MdYtdlp.Extract;
            T.Check("ytdlp: Extract fits MdHooks.ExtractFn", hook != null);
        }

        // ------------------------------------------------------------------ //
        //  Живой шаг: SYSDECK_MD_C_LIVE=<папка> — настоящая установка с GitHub и настоящий разбор страницы YouTube.
        // ------------------------------------------------------------------ //
        private static void ToolsLiveRecord(string dir)
        {
            Directory.CreateDirectory(dir);
            MdTools.DirOverride = Path.Combine(dir, "tools");
            string err;
            Stopwatch w = Stopwatch.StartNew();
            bool ok = MdTools.Install(false, delegate(string s, double p) { Console.WriteLine("  " + s + " " + p.ToString("0.00")); }, null, out err);
            Console.WriteLine("LIVE install ok=" + ok + " in " + w.ElapsedMilliseconds + " ms " + err);
            MdToolStatus st = MdTools.Status();
            Console.WriteLine("LIVE yt-dlp=" + st.YtdlpVersion + " (" + st.LatestYtdlp + ") deno=" + st.DenoVersion + " (" + st.LatestDeno + ")");
            if (!ok) return;
            Func<string, string, Func<bool>, string> real = MdYtdlp.Runner;
            MdYtdlp.Runner = delegate(string exe, string args, Func<bool> c)
            {
                string json = real(exe, args, c);
                File.WriteAllText(Path.Combine(dir, "raw.json"), json, new UTF8Encoding(false));
                return json;
            };
            try
            {
                w.Restart();
                MdManifest m = MdYtdlp.Extract("https://www.youtube.com/watch?v=aqz-KE-bpKQ", null, out err);
                Console.WriteLine("LIVE extract in " + w.ElapsedMilliseconds + " ms: " + (m == null ? "ERROR " + err
                                  : m.Variants.Count + " variants, " + m.Audio.Count + " audio, " + m.Subtitles.Count + " subs, expires "
                                    + m.ExpiresUtc.ToString("u") + ", title " + m.Title));
                if (m != null)
                    foreach (MdVariant v in m.Variants) Console.WriteLine("  " + v.Id + " " + v.Label + " " + v.Main.Layout + " " + v.Main.SizeHint);
            }
            finally { MdYtdlp.Runner = real; }
        }

        // Записанный живой ответ yt-dlp (14.09.2026, aqz-KE-bpKQ), обрезанный до нужных полей; ip/sig/lsig/n вычищены.
        private const string ToolsSampleJson =
            @"{""id"":""aqz-KE-bpKQ"",""title"":""Big Buck Bunny 60fps 4K - Official Blender Foundation Short Film"",""webpage_url"":""" +
              @"https://www.youtube.com/watch?v=aqz-KE-bpKQ"",""original_url"":""https://www.youtube.com/watch?v=aqz-KE-bpKQ"",""dur" +
              @"ation"":635,""is_live"":false,""was_live"":false,""live_status"":""not_live"",""extractor"":""youtube"",""_type"":""video"",""su" +
              @"btitles"":{},""automatic_captions"":{},""formats"":[{""format_id"":""sb0"",""format_note"":""storyboard"",""ext"":""mhtml"",""pr" +
              @"otocol"":""mhtml"",""url"":""https://i.ytimg.com/sb/aqz-KE-bpKQ/storyboard3_L3/M$M.jpg?sqp=-oaymwENSDfyq4qpAwVwAcABB" +
              @"qLzl_8DBgjEj7SoBg%3D%3D&sigh="",""width"":320,""height"":180,""fps"":0.2015748031496063,""vcodec"":""none"",""acodec"":""non" +
              @"e"",""tbr"":null,""vbr"":0,""abr"":0,""filesize_approx"":null,""http_headers"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10." +
              @"0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/html,appl" +
              @"ication/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""naviga" +
              @"te""},""audio_ext"":""none"",""video_ext"":""none"",""fragments"":[{""url"":""https://i.ytimg.com/sb/aqz-KE-bpKQ/storyboard3" +
              @"_L3/M0.jpg?sqp=-oaymwENSDfyq4qpAwVwAcABBqLzl_8DBgjEj7SoBg%3D%3D&sigh="",""duration"":44.6484375}],""rows"":3,""colum" +
              @"ns"":3},{""format_id"":""140"",""format_note"":""medium"",""ext"":""m4a"",""protocol"":""https"",""url"":""https://rr1---sn-01oxu-" +
              @"u5nr.googlevideo.com/videoplayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcs" +
              @"SnNt6Ho6jrSJfPvrtz1BCwH&itag=140&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385659,&mh" +
              @"=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&bui=AR3Q" +
              @"kAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYqxgKA2X3" +
              @"vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&vprv=1&svpuc=1&mime=audio%2Fmp4&ns=yFs1ir1bNLpQ64zqCYSFuJIY&rqh=1&" +
              @"gir=yes&clen=10271496&dur=634.624&lmt=1719185020722767&mt=1789385328&fvip=5&keepalive=yes&fexp=51565115,521354" +
              @"41&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4532434&n=&sparams=expire,ei,ip,id,itag,source,requiressl,xpc,bui,spc,vprv" +
              @",svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig="",""width""" +
              @":null,""height"":null,""fps"":null,""vcodec"":""none"",""acodec"":""mp4a.40.2"",""tbr"":129.481,""vbr"":0,""abr"":129.481,""files" +
              @"ize"":10271496,""filesize_approx"":10271468,""container"":""m4a_dash"",""http_headers"":{""User-Agent"":""Mozilla/5.0 (Win" +
              @"dows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""tex" +
              @"t/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mo" +
              @"de"":""navigate""},""downloader_options"":{""http_chunk_size"":10485760},""has_drm"":false,""language"":null,""language_pr" +
              @"eference"":-1,""audio_ext"":""m4a"",""video_ext"":""none"",""dynamic_range"":null},{""format_id"":""251"",""format_note"":""medi" +
              @"um"",""ext"":""webm"",""protocol"":""https"",""url"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=17" +
              @"89407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=251&source=yout" +
              @"ube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms" +
              @"=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1" +
              @"Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&" +
              @"vprv=1&svpuc=1&mime=audio%2Fwebm&ns=yFs1ir1bNLpQ64zqCYSFuJIY&rqh=1&gir=yes&clen=10202210&dur=634.601&lmt=17191" +
              @"85012384481&mt=1789385328&fvip=5&keepalive=yes&fexp=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4532434" +
              @"&n=&sparams=expire,ei,ip,id,itag,source,requiressl,xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&ls" +
              @"params=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig="",""width"":null,""height"":null,""fps"":null,""vcodec"":""no" +
              @"ne"",""acodec"":""opus"",""tbr"":128.612,""vbr"":0,""abr"":128.612,""filesize"":10202210,""filesize_approx"":10202162,""contai" +
              @"ner"":""webm_dash"",""http_headers"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (K" +
              @"HTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/html,application/xhtml+xml,application/xml;q=" +
              @"0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""navigate""},""downloader_options"":{""http_chu" +
              @"nk_size"":10485760},""has_drm"":false,""language"":null,""language_preference"":-1,""audio_ext"":""webm"",""video_ext"":""no" +
              @"ne"",""dynamic_range"":null},{""format_id"":""380"",""format_note"":""high"",""ext"":""m4a"",""protocol"":""https"",""url"":""https:" +
              @"//rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeM" +
              @"xqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=380&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&m" +
              @"et=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps" +
              @"=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACi" +
              @"cwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&vprv=1&svpuc=1&mime=audio%2Fmp4&ns=yFs1ir1bNLpQ64" +
              @"zqCYSFuJIY&rqh=1&gir=yes&clen=30468809&dur=634.592&lmt=1719173869197319&mt=1789385328&fvip=5&keepalive=yes&fex" +
              @"p=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4532434&n=&sparams=expire,ei,ip,id,itag,source,requiressl" +
              @",xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndb" +
              @"ps&lsig="",""width"":null,""height"":null,""fps"":null,""vcodec"":""none"",""acodec"":""ac-3"",""tbr"":384.105,""vbr"":0,""abr"":38" +
              @"4.105,""filesize"":30468809,""filesize_approx"":30468745,""container"":""m4a_dash"",""http_headers"":{""User-Agent"":""Mozi" +
              @"lla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""" +
              @"Accept"":""text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""" +
              @"Sec-Fetch-Mode"":""navigate""},""downloader_options"":{""http_chunk_size"":10485760},""has_drm"":false,""language"":null," +
              @"""language_preference"":-1,""audio_ext"":""m4a"",""video_ext"":""none"",""dynamic_range"":null},{""format_id"":""258"",""format" +
              @"_note"":""high"",""ext"":""m4a"",""protocol"":""https"",""url"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?" +
              @"expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=258&so" +
              @"urce=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g" +
              @"5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W" +
              @"3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6" +
              @"MCk00HMZ&vprv=1&svpuc=1&mime=audio%2Fmp4&ns=yFs1ir1bNLpQ64zqCYSFuJIY&rqh=1&gir=yes&clen=30767611&dur=634.624&l" +
              @"mt=1719173887145696&mt=1789385328&fvip=5&keepalive=yes&fexp=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp" +
              @"=4532434&n=&sparams=expire,ei,ip,id,itag,source,requiressl,xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt" +
              @"&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig="",""width"":null,""height"":null,""fps"":null,""vco" +
              @"dec"":""none"",""acodec"":""mp4a.40.2"",""tbr"":387.853,""vbr"":0,""abr"":387.853,""filesize"":30767611,""filesize_approx"":307" +
              @"67602,""container"":""m4a_dash"",""http_headers"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebK" +
              @"it/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/html,application/xhtml+xml,applic" +
              @"ation/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""navigate""},""downloader_options" +
              @""":{""http_chunk_size"":10485760},""has_drm"":false,""language"":null,""language_preference"":-1,""audio_ext"":""m4a"",""vid" +
              @"eo_ext"":""none"",""dynamic_range"":null},{""format_id"":""18"",""format_note"":""360p"",""ext"":""mp4"",""protocol"":""https"",""ur" +
              @"l"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=" +
              @"&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=18&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&" +
              @"cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&in" +
              @"itcwndbps=1723750&bui=AR3QkAkQd-ulXNErqEdYVo7477g5X1sypVymVFwO6IhHLh5E9kxvMb7y4iDKtnB3iDAOoHd3ATIYFcQS&spc=I-r" +
              @"gIaWCCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MBkxOHOBILg&vprv=1&svpuc=1&mime=video%2Fmp4&ns=zB" +
              @"k-y6uHMvAhANrcWIHGYBsY&rqh=1&cnr=14&ratebypass=yes&dur=634.624&lmt=1719195733189356&mt=1789385328&fvip=5&fexp=" +
              @"51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4538434&n=&sparams=expire,ei,ip,id,itag,source,requiressl,x" +
              @"pc,bui,spc,vprv,svpuc,mime,ns,rqh,cnr,ratebypass,dur,lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initc" +
              @"wndbps&lsig="",""width"":640,""height"":360,""fps"":30,""vcodec"":""avc1.42001E"",""acodec"":""mp4a.40.2"",""tbr"":359.607,""vbr" +
              @""":null,""abr"":null,""filesize"":null,""filesize_approx"":28526904,""http_headers"":{""User-Agent"":""Mozilla/5.0 (Window" +
              @"s NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/h" +
              @"tml,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode""" +
              @":""navigate""},""downloader_options"":{""http_chunk_size"":10485760},""has_drm"":false,""language"":null,""language_prefe" +
              @"rence"":-1,""audio_ext"":""none"",""video_ext"":""mp4"",""dynamic_range"":""SDR""},{""format_id"":""136"",""format_note"":""720p""," +
              @"""ext"":""mp4"",""protocol"":""https"",""url"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=1789407" +
              @"259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=136&aitags=133,134,1" +
              @"35,136,160,242,243,244,247,278,298,299,302,303,308,315,394,395,396,397,398,399,400,401&source=youtube&requires" +
              @"sl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=" +
              @"m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1Z9RAyUKaYcWG" +
              @"VIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&vprv=1&svpuc" +
              @"=1&mime=video%2Fmp4&ns=yFs1ir1bNLpQ64zqCYSFuJIY&rqh=1&gir=yes&clen=87293859&dur=634.566&lmt=1719193506342742&m" +
              @"t=1789385328&fvip=5&keepalive=yes&fexp=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4532434&n=&sparams=e" +
              @"xpire,ei,ip,id,aitags,source,requiressl,xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&lsparams=cps," +
              @"met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig="",""width"":1280,""height"":720,""fps"":30,""vcodec"":""avc1.4d401f"",""ac" +
              @"odec"":""none"",""tbr"":1100.517,""vbr"":1100.517,""abr"":0,""filesize"":87293859,""filesize_approx"":87293833,""container"":" +
              @"""mp4_dash"",""http_headers"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, " +
              @"like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/html,application/xhtml+xml,application/xml;q=0.9,*/" +
              @"*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""navigate""},""downloader_options"":{""http_chunk_siz" +
              @"e"":10485760},""has_drm"":false,""language"":null,""language_preference"":-1,""audio_ext"":""none"",""video_ext"":""mp4"",""dy" +
              @"namic_range"":""SDR""},{""format_id"":""299"",""format_note"":""1080p60"",""ext"":""mp4"",""protocol"":""https"",""url"":""https://r" +
              @"r1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqb" +
              @"KwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=299&aitags=133,134,135,136,160,242,243,244,247,278,298,299,302,303," +
              @"308,315,394,395,396,397,398,399,400,401&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385" +
              @"659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&b" +
              @"ui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYq" +
              @"xgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&vprv=1&svpuc=1&mime=video%2Fmp4&ns=yFs1ir1bNLpQ64zqCYSFuJIY" +
              @"&rqh=1&gir=yes&clen=257619653&dur=634.566&lmt=1719195290537273&mt=1789385328&fvip=5&keepalive=yes&fexp=5156511" +
              @"5,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4532434&n=&sparams=expire,ei,ip,id,aitags,source,requiressl,xpc,bu" +
              @"i,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig" +
              @"="",""width"":1920,""height"":1080,""fps"":60,""vcodec"":""avc1.64002a"",""acodec"":""none"",""tbr"":3247.821,""vbr"":3247.821,""a" +
              @"br"":0,""filesize"":257619653,""filesize_approx"":257619597,""container"":""mp4_dash"",""http_headers"":{""User-Agent"":""Mo" +
              @"zilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36""" +
              @",""Accept"":""text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5""" +
              @",""Sec-Fetch-Mode"":""navigate""},""downloader_options"":{""http_chunk_size"":10485760},""has_drm"":false,""language"":nul" +
              @"l,""language_preference"":-1,""audio_ext"":""none"",""video_ext"":""mp4"",""dynamic_range"":""SDR""},{""format_id"":""303"",""for" +
              @"mat_note"":""1080p60"",""ext"":""webm"",""protocol"":""https"",""url"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videopl" +
              @"ayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag" +
              @"=303&aitags=133,134,135,136,160,242,243,244,247,278,298,299,302,303,308,315,394,395,396,397,398,399,400,401&so" +
              @"urce=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D&cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g" +
              @"5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&initcwndbps=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W" +
              @"3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-rgIaWBCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6" +
              @"MCk00HMZ&vprv=1&svpuc=1&mime=video%2Fwebm&ns=yFs1ir1bNLpQ64zqCYSFuJIY&rqh=1&gir=yes&clen=168736189&dur=634.566" +
              @"&lmt=1719197912122364&mt=1789385328&fvip=5&keepalive=yes&fexp=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&t" +
              @"xp=4537434&n=&sparams=expire,ei,ip,id,aitags,source,requiressl,xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur" +
              @",lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl,rms,initcwndbps&lsig="",""width"":1920,""height"":1080,""fps"":60,""v" +
              @"codec"":""vp9"",""acodec"":""none"",""tbr"":2127.264,""vbr"":2127.264,""abr"":0,""filesize"":168736189,""filesize_approx"":1687" +
              @"36175,""container"":""webm_dash"",""http_headers"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWeb" +
              @"Kit/537.36 (KHTML, like Gecko) Chrome/149.0.0.0 Safari/537.36"",""Accept"":""text/html,application/xhtml+xml,appli" +
              @"cation/xml;q=0.9,*/*;q=0.8"",""Accept-Language"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""navigate""},""downloader_option" +
              @"s"":{""http_chunk_size"":10485760},""has_drm"":false,""language"":null,""language_preference"":-1,""audio_ext"":""none"",""v" +
              @"ideo_ext"":""webm"",""dynamic_range"":""SDR""},{""format_id"":""399"",""format_note"":""1080p60"",""ext"":""mp4"",""protocol"":""htt" +
              @"ps"",""url"":""https://rr1---sn-01oxu-u5nr.googlevideo.com/videoplayback?expire=1789407259&ei=u9unasGZEYqrp-oPl93E" +
              @"0Ak&ip=&id=o-ABeMxqbKwAU05mZermNcsSnNt6Ho6jrSJfPvrtz1BCwH&itag=399&aitags=133,134,135,136,160,242,243,244,247," +
              @"278,298,299,302,303,308,315,394,395,396,397,398,399,400,401&source=youtube&requiressl=yes&xpc=EgVo2aDSNQ%3D%3D" +
              @"&cps=371&met=1789385659,&mh=aP&mm=18,29&mn=sn-01oxu-u5nr,sn-4g5ednr7&ms=aub,rdu&mv=m&mvi=1&pl=24&rms=aub,aub&i" +
              @"nitcwndbps=1723750&bui=AR3QkAmtwuG4kvZBjZ_pGaIhP1D9-y7CTOUPh6W3LaMbPcR1Z9RAyUKaYcWGVIN9wJHAMl7FGyAjq0ru&spc=I-" +
              @"rgIaWBCACicwkJ3o6xYqxgKA2X3vBdqcfDXBwlbygxcH1muNDhdTCL9SaszZi6MCk00HMZ&vprv=1&svpuc=1&mime=video%2Fmp4&ns=yFs1" +
              @"ir1bNLpQ64zqCYSFuJIY&rqh=1&gir=yes&clen=124386876&dur=634.566&lmt=1719202580711560&mt=1789385328&fvip=5&keepal" +
              @"ive=yes&fexp=51565115,52135441&c=WEB_EMBEDDED_PLAYER&sefc=1&txp=4537434&n=&sparams=expire,ei,ip,id,aitags,sour" +
              @"ce,requiressl,xpc,bui,spc,vprv,svpuc,mime,ns,rqh,gir,clen,dur,lmt&sig=&lsparams=cps,met,mh,mm,mn,ms,mv,mvi,pl," +
              @"rms,initcwndbps&lsig="",""width"":1920,""height"":1080,""fps"":60,""vcodec"":""av01.0.09M.08"",""acodec"":""none"",""tbr"":1568" +
              @".15,""vbr"":1568.15,""abr"":0,""filesize"":124386876,""filesize_approx"":124386834,""container"":""mp4_dash"",""http_header" +
              @"s"":{""User-Agent"":""Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/149." +
              @"0.0.0 Safari/537.36"",""Accept"":""text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"",""Accept-Langua" +
              @"ge"":""en-us,en;q=0.5"",""Sec-Fetch-Mode"":""navigate""},""downloader_options"":{""http_chunk_size"":10485760},""has_drm"":" +
              @"false,""language"":null,""language_preference"":-1,""audio_ext"":""none"",""video_ext"":""mp4"",""dynamic_range"":""SDR""}]}";
    }
}
