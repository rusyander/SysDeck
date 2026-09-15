// SysDeck — тесты «Загрузки»: передача, возобновление, редиректы, зеркала, ограничения.
// Сборка и запуск: tests\run-tests.bat (компилирует src\*.cs и tests\*.cs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class DownloadsTests
    {
        // ---------- движок против сервера ----------
        private static void Segmented(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("seg", new DlTestServer.Res());
            r.Size = 5 * 1024 * 1024 + 123;
            r.Seed = 1;
            r.Disposition = "attachment; filename*=UTF-8''%D0%BE%D1%82%D1%87%D1%91%D1%82.bin";
            string folder = Dir("seg");
            FakeDlEnv env = new FakeDlEnv();
            using (DlEngine e = NewEngine(Dir("seg-store"), NewSettings(folder), env))
            {
                string id = Add(e, srv.Url("seg"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 20000);
                DlItem it = e.Find(id);
                T.Check("206 download with 4 segments completes", done, Info(e, id));
                T.Eq("name from filename*", "отчёт.bin", it.FileName);
                T.Eq("assembled file matches the source SHA-256", DlTestServer.Sha256Of(1, r.Size), FileHash(Path.Combine(folder, "отчёт.bin")));
                T.Eq("engine SHA-256 equals the independent one", DlTestServer.Sha256Of(1, r.Size), it.Sha256);
                T.Eq("four segments were planned", 4, it.Segments.Count);
                T.Check("the server saw parallel connections", r.MaxConcurrent >= 2, r.MaxConcurrent.ToString());
                T.Check("no partial file is left", !File.Exists(Path.Combine(folder, "отчёт.bin" + DlPaths.PartSuffix)));
                T.Check("first request asked for bytes=0-", r.RangesSeen.Count > 0 && r.RangesSeen[0] == "bytes=0-");
            }
        }

        private static void SingleStream(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("single", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 2;
            r.Ranges = false;
            string folder = Dir("single");
            using (DlEngine e = NewEngine(Dir("single-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("single"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                DlItem it = e.Find(id);
                T.Check("200 without ranges falls back to one stream", done && !it.AcceptRanges && it.Segments.Count == 1, Info(e, id));
                T.Eq("single stream: one connection only", 1, r.MaxConcurrent);
                T.Eq("single stream: content is intact", DlTestServer.Sha256Of(2, r.Size), FileHash(it.TargetPath));
                T.Eq("name from the URL path", "single", it.FileName);
            }
        }

        private static void UnknownLength(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("chunked.dat", new DlTestServer.Res());
            r.Size = 700 * 1024 + 5;
            r.Seed = 3;
            r.Ranges = false;
            r.NoLength = true;
            string folder = Dir("chunked");
            using (DlEngine e = NewEngine(Dir("chunked-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("chunked.dat"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                DlItem it = e.Find(id);
                T.Check("chunked response of unknown size completes", done, Info(e, id));
                T.Eq("size is learned from the end of the stream", (long)r.Size, it.Total);
                T.Eq("chunked: content is intact", DlTestServer.Sha256Of(3, r.Size), FileHash(it.TargetPath));
            }
        }

        private static void ResumeAfterRestart(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("resume.bin", new DlTestServer.Res());
            r.Size = 3 * 1024 * 1024;
            r.Seed = 4;
            r.RateBps = 700 * 1024;
            string folder = Dir("resume");
            string store = Dir("resume-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 2;
            string id;
            long doneBefore;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("resume.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 600 * 1024; }, 10000);
            }
            DlItem saved = new DlStore(store).LoadAll().Find(delegate(DlItem x) { return x.Id == id; });
            doneBefore = saved == null ? -1 : saved.DoneBytes;
            T.Check("stopping the engine keeps the partial download", saved != null && doneBefore > 0 && doneBefore < r.Size && saved.State == DlState.Queued,
                    saved == null ? "no item" : saved.State + " " + doneBefore);
            T.Check("the partial file is on disk", File.Exists(Path.Combine(folder, "resume.bin" + DlPaths.PartSuffix)));
            int requestsBefore = r.Requests;
            r.RateBps = 0;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                T.Check("a new engine resumes the download by itself", done, Info(e, id));
                T.Eq("resumed file is intact", DlTestServer.Sha256Of(4, r.Size), FileHash(Path.Combine(folder, "resume.bin")));
            }
            bool rangedResume = false, ifRangeSent = false;
            lock (srv.Gate)
                for (int i = requestsBefore; i < r.RangesSeen.Count; i++)
                {
                    if (r.RangesSeen[i].StartsWith("bytes=") && !r.RangesSeen[i].StartsWith("bytes=0-")) rangedResume = true;
                    if (r.IfRanges[i] == "\"v1\"") ifRangeSent = true;
                }
            T.Check("resume asked the server for the missing ranges only", rangedResume);
            T.Check("resume sent If-Range with the ETag", ifRangeSent);
        }

        private static void ChangedOnServer(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("changing.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 5;
            r.RateBps = 600 * 1024;
            string folder = Dir("changed");
            string store = Dir("changed-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 1;
            string id;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("changing.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 300 * 1024; }, 10000);
            }
            lock (srv.Gate) { r.ETag = "\"v2\""; r.Seed = 55; r.RateBps = 0; }
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool failed = WaitFor(delegate { return StateOf(e, id) == DlState.Failed; }, 10000);
                DlItem it = e.Find(id);
                T.Check("a file changed on the server stops the resume (If-Range → 200)", failed && it.ErrorKind == DlErrorKind.Changed, Info(e, id));
                T.Check("the partial data is kept for the user to decide", File.Exists(Path.Combine(folder, "changing.bin" + DlPaths.PartSuffix)));
                string why;
                T.Check("restart from zero is accepted", e.Restart(id, out why), why);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("restart downloads the new version", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(55, r.Size), Info(e, id));
            }
        }

        private static void PauseResume(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("pause.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 6;
            r.RateBps = 800 * 1024;
            string folder = Dir("pause");
            using (DlEngine e = NewEngine(Dir("pause-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("pause.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 200 * 1024; }, 10000);
                e.Pause(id);
                bool paused = WaitFor(delegate { return StateOf(e, id) == DlState.Paused; }, 5000);
                long frozen = e.Find(id).DoneBytes;
                Thread.Sleep(700);
                T.Check("pause stops the transfer", paused && e.Find(id).DoneBytes == frozen && frozen < r.Size, Info(e, id));
                r.RateBps = 0;
                e.Resume(id);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("resume after pause completes intact", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(6, r.Size), Info(e, id));
            }
        }

        private static void Redirects(DlTestServer srv)
        {
            DlTestServer.Res target = srv.Add("target.bin", new DlTestServer.Res());
            target.Size = 300 * 1024;
            target.Seed = 7;
            DlTestServer.Res hop = srv.Add("hop", new DlTestServer.Res());
            hop.RedirectTo = srv.UrlLocalhost("target.bin");
            DlTestServer.Res loop = srv.Add("loop", new DlTestServer.Res());
            loop.RedirectTo = srv.Url("loop");
            string folder = Dir("redirect");
            using (DlEngine e = NewEngine(Dir("redirect-store"), NewSettings(folder), new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("hop");
                req.Cookies = "sid=TOPSECRET";
                string dup, err;
                string id = e.Add(req, out dup, out err);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                DlItem it = e.Find(id);
                T.Check("redirect to another host completes", done && it.FinalUrl.StartsWith("http://localhost:"), Info(e, id));
                T.Check("the redirect chain is recorded", it.Redirects.Count == 1);
                bool cookieToOrigin, cookieElsewhere = false;
                lock (srv.Gate)
                {
                    cookieToOrigin = hop.Cookies.Count > 0 && hop.Cookies[0] == "sid=TOPSECRET";
                    foreach (string c in target.Cookies) if (c.Length > 0) cookieElsewhere = true;
                }
                T.Check("cookies go to the host they were given for", cookieToOrigin);
                T.Check("cookies are not forwarded to another host", !cookieElsewhere);

                string loopId = Add(e, srv.Url("loop"));
                bool failed = WaitFor(delegate { return StateOf(e, loopId) == DlState.Failed; }, 10000);
                T.Check("a redirect loop stops at the limit", failed && e.Find(loopId).ErrorKind == DlErrorKind.Policy && loop.Requests == DlHttp.MaxRedirects + 1,
                        Info(e, loopId) + " requests=" + loop.Requests);
            }
        }

        private static void RetryAndNotRetry(DlTestServer srv)
        {
            DlTestServer.Res busy = srv.Add("busy.bin", new DlTestServer.Res());
            busy.Size = 200 * 1024;
            busy.Seed = 8;
            busy.FailFirst = 2;
            busy.FailCode = 503;
            busy.RetryAfter = "1";
            DlTestServer.Res gone = srv.Add("gone.bin", new DlTestServer.Res());
            gone.Status = 404;
            string folder = Dir("retry");
            using (DlEngine e = NewEngine(Dir("retry-store"), NewSettings(folder), new FakeDlEnv()))
            {
                Stopwatch w = Stopwatch.StartNew();
                string id = Add(e, srv.Url("busy.bin"));
                string goneId = Add(e, srv.Url("gone.bin"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000);
                T.Check("503 with Retry-After is retried until success", done && busy.Requests == 3, Info(e, id) + " requests=" + busy.Requests);
                T.Check("the server pause was honoured (≥ 2 s for two 1 s pauses)", w.ElapsedMilliseconds >= 1900, w.ElapsedMilliseconds.ToString());
                T.Eq("attempts reset after success", 0, e.Find(id).Attempts);
                bool failed = WaitFor(delegate { return StateOf(e, goneId) == DlState.Failed; }, 5000);
                Thread.Sleep(1500);
                T.Check("404 is not retried", failed && gone.Requests == 1 && e.Find(goneId).ErrorKind == DlErrorKind.Client, Info(e, goneId) + " requests=" + gone.Requests);
            }
        }

        private static void LinkExpiredAndRefresh(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("expiring.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024;
            r.Seed = 9;
            r.RateBps = 600 * 1024;
            DlTestServer.Res fresh = srv.Add("fresh-link.bin", new DlTestServer.Res());
            fresh.Size = r.Size;
            fresh.Seed = 9;
            fresh.ETag = "\"other-node\"";
            string folder = Dir("expired");
            string store = Dir("expired-store");
            DlSettings s = NewSettings(folder);
            s.Segments = 1;
            string id;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                id = Add(e, srv.Url("expiring.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 300 * 1024; }, 10000);
            }
            lock (srv.Gate) r.ForbidResume = true;
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                bool needs = WaitFor(delegate { return StateOf(e, id) == DlState.NeedsLink; }, 10000);
                long kept = e.Find(id).DoneBytes;
                T.Check("403 while resuming asks for a fresh link and keeps the data", needs && kept > 0, Info(e, id));
                string why;
                T.Check("refresh link is accepted", e.RefreshLink(id, srv.Url("fresh-link.bin"), null, out why), why);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("the fresh link finishes the same file", done && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(9, r.Size), Info(e, id));
                bool fromOffset = false;
                lock (srv.Gate) foreach (string range in fresh.RangesSeen) if (range.StartsWith("bytes=") && !range.StartsWith("bytes=0-")) fromOffset = true;
                T.Check("the fresh link continued from the kept bytes", fromOffset);
            }
        }

        private static void MirrorAndHash(DlTestServer srv)
        {
            DlTestServer.Res primary = srv.Add("primary-missing.bin", new DlTestServer.Res());
            primary.Status = 404;
            DlTestServer.Res mirror = srv.Add("mirror.bin", new DlTestServer.Res());
            mirror.Size = 400 * 1024;
            mirror.Seed = 10;
            DlTestServer.Res bad = srv.Add("badhash.bin", new DlTestServer.Res());
            bad.Size = 100 * 1024;
            bad.Seed = 11;
            string folder = Dir("mirror");
            using (DlEngine e = NewEngine(Dir("mirror-store"), NewSettings(folder), new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("primary-missing.bin");
                req.Mirrors.Add(srv.Url("mirror.bin"));
                req.ExpectedHash = "sha256:" + DlTestServer.Sha256Of(10, mirror.Size);
                string dup, err;
                string id = e.Add(req, out dup, out err);
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("404 on the primary switches to the mirror, the expected hash matches", done, Info(e, id));

                DlAddRequest wrong = new DlAddRequest();
                wrong.Url = srv.Url("badhash.bin");
                wrong.ExpectedHash = "sha256:" + new string('0', 64);
                string wid = e.Add(wrong, out dup, out err);
                bool failed = WaitFor(delegate { return StateOf(e, wid) == DlState.Failed; }, 10000);
                DlItem it = e.Find(wid);
                T.Check("a hash mismatch fails the download", failed && it.ErrorKind == DlErrorKind.HashMismatch, Info(e, wid));
                T.Check("a mismatched file stays partial and never gets its real name",
                        File.Exists(Path.Combine(folder, "badhash.bin" + DlPaths.PartSuffix)) && !File.Exists(Path.Combine(folder, "badhash.bin")));
            }
        }

        private static void Collision(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("collide", new DlTestServer.Res());
            r.Size = 50 * 1024;
            r.Seed = 12;
            r.Disposition = "attachment; filename=\"same.bin\"";
            string folder = Dir("collide");
            string existing = Fx.MakeFile(Path.Combine(folder, "same.bin"), 333);
            string before = FileHash(existing);
            using (DlEngine e = NewEngine(Dir("collide-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("collide"));
                bool done = WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                T.Check("an existing file gets a numbered sibling", done && e.Find(id).FileName == "same (1).bin", Info(e, id));
                T.Eq("the existing file is untouched", before, FileHash(existing));
            }
        }

        private static void Throttle(DlTestServer srv)
        {
            DlTestServer.Res one = srv.Add("limit-item.bin", new DlTestServer.Res());
            one.Size = 1536 * 1024;
            one.Seed = 13;
            string folder = Dir("throttle");
            DlSettings s = NewSettings(folder);
            s.Segments = 2;
            using (DlEngine e = NewEngine(Dir("throttle-store"), s, new FakeDlEnv()))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("limit-item.bin");
                req.LimitKBps = 512;
                string dup, err;
                string id = e.Add(req, out dup, out err);
                double rate = MeasureRate(e, new[] { id }, 20000);
                T.Check("per-download limit 512 KB/s holds within ±15 %", rate > 512 * 1024 * 0.85 && rate < 512 * 1024 * 1.15, (rate / 1024).ToString("0") + " KB/s");
            }

            DlTestServer.Res a = srv.Add("limit-a.bin", new DlTestServer.Res());
            a.Size = 768 * 1024; a.Seed = 14;
            DlTestServer.Res b = srv.Add("limit-b.bin", new DlTestServer.Res());
            b.Size = 768 * 1024; b.Seed = 15;
            DlSettings g = NewSettings(Dir("throttle-global"));
            g.Mode = DlSpeedMode.Normal;             // общий лимит действует только в обычном режиме, а по умолчанию теперь «без ограничения»
            g.LimitKBps = 512;
            using (DlEngine e = NewEngine(Dir("throttle-global-store"), g, new FakeDlEnv()))
            {
                string ia = Add(e, srv.Url("limit-a.bin"));
                string ib = Add(e, srv.Url("limit-b.bin"));
                double rate = MeasureRate(e, new[] { ia, ib }, 20000);
                T.Check("global limit 512 KB/s holds for two downloads together within ±15 %", rate > 512 * 1024 * 0.85 && rate < 512 * 1024 * 1.15, (rate / 1024).ToString("0") + " KB/s");
            }
        }

        // Байты в секунду между первым замером с данными и завершением всех загрузок.
        private static double MeasureRate(DlEngine e, string[] ids, int timeoutMs)
        {
            Func<long> done = delegate
            {
                long sum = 0;
                foreach (string id in ids) { DlItem it = e.Find(id); if (it != null) sum += it.State == DlState.Completed ? Math.Max(0, it.Total) : it.DoneBytes; }
                return sum;
            };
            Func<bool> all = delegate
            {
                foreach (string id in ids) if (StateOf(e, id) != DlState.Completed) return false;
                return true;
            };
            if (!WaitFor(delegate { return done() > 64 * 1024; }, timeoutMs)) return -1;
            Stopwatch w = Stopwatch.StartNew();
            long start = done();
            if (!WaitFor(all, timeoutMs)) return -2;
            return (done() - start) / w.Elapsed.TotalSeconds;
        }

        private static void Concurrency(DlTestServer srv)
        {
            string[] keys = { "q1.bin", "q2.bin", "q3.bin", "q4.bin" };
            foreach (string k in keys)
            {
                DlTestServer.Res r = srv.Add(k, new DlTestServer.Res());
                r.Size = 256 * 1024;
                r.Seed = 20;
                r.RateBps = 256 * 1024;
            }
            lock (srv.Gate) srv.MaxActiveKeys = 0;
            DlSettings s = NewSettings(Dir("queue"));
            s.Segments = 1;
            s.MaxActive = 2;
            s.MaxPerHost = 10;
            using (DlEngine e = NewEngine(Dir("queue-store"), s, new FakeDlEnv()))
            {
                List<string> ids = new List<string>();
                foreach (string k in keys) ids.Add(Add(e, srv.Url(k)));
                bool done = WaitFor(delegate { foreach (string id in ids) if (StateOf(e, id) != DlState.Completed) return false; return true; }, 20000);
                T.Check("max active 2: all four complete", done);
                T.Check("max active 2: never more than two at once, and two did run together", srv.MaxActiveKeys == 2, srv.MaxActiveKeys.ToString());
            }

            string[] hostKeys = { "h1.bin", "h2.bin", "h3.bin" };
            foreach (string k in hostKeys)
            {
                DlTestServer.Res r = srv.Add(k, new DlTestServer.Res());
                r.Size = 256 * 1024;
                r.Seed = 21;
                r.RateBps = 256 * 1024;
            }
            lock (srv.Gate) { srv.MaxActiveKeys = 0; srv.HostMax.Clear(); }
            DlSettings h = NewSettings(Dir("perhost"));
            h.Segments = 1;
            h.MaxActive = 10;
            h.MaxPerHost = 1;
            using (DlEngine e = NewEngine(Dir("perhost-store"), h, new FakeDlEnv()))
            {
                string a = Add(e, srv.Url("h1.bin"));
                string b = Add(e, srv.Url("h2.bin"));
                string c = Add(e, srv.UrlLocalhost("h3.bin"));
                bool done = WaitFor(delegate { return StateOf(e, a) == DlState.Completed && StateOf(e, b) == DlState.Completed && StateOf(e, c) == DlState.Completed; }, 20000);
                int loopbackMax;
                lock (srv.Gate) srv.HostMax.TryGetValue("127.0.0.1", out loopbackMax);
                T.Check("per host 1: all complete", done);
                T.Check("per host 1: one at a time from 127.0.0.1", loopbackMax == 1, loopbackMax.ToString());
                T.Check("per host 1: another host runs in parallel", srv.MaxActiveKeys >= 2, srv.MaxActiveKeys.ToString());
            }
        }

        private static void Gates(DlTestServer srv)
        {
            DlTestServer.Res later = srv.Add("later.bin", new DlTestServer.Res());
            later.Size = 100 * 1024; later.Seed = 30;
            DlTestServer.Res sched = srv.Add("sched.bin", new DlTestServer.Res());
            sched.Size = 100 * 1024; sched.Seed = 31;
            DlTestServer.Res idle = srv.Add("idle.bin", new DlTestServer.Res());
            idle.Size = 2 * 1024 * 1024; idle.Seed = 32; idle.RateBps = 300 * 1024;
            DlTestServer.Res net = srv.Add("metered.bin", new DlTestServer.Res());
            net.Size = 100 * 1024; net.Seed = 33;

            // Отложенный старт
            FakeDlEnv env = new FakeDlEnv();
            using (DlEngine e = NewEngine(Dir("later-store"), NewSettings(Dir("later")), env))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("later.bin");
                req.StartAtUtc = env.UtcNow.AddMinutes(10);
                string dup, err;
                string id = e.Add(req, out dup, out err);
                Thread.Sleep(800);
                T.Check("a postponed download waits for its time", StateOf(e, id) == DlState.Scheduled && later.Requests == 0, Info(e, id));
                env.Advance(TimeSpan.FromMinutes(11));
                T.Check("the postponed download starts when its time comes", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }

            // Расписание
            env = new FakeDlEnv();
            DlSettings s = NewSettings(Dir("sched"));
            int nowMinute = DlSettings.MinuteOfDay(env.LocalNow);
            s.ScheduleEnabled = true;
            s.ScheduleFrom = (nowMinute + 120) % 1440;
            s.ScheduleTo = (nowMinute + 240) % 1440;
            using (DlEngine e = NewEngine(Dir("sched-store"), s, env))
            {
                string id = Add(e, srv.Url("sched.bin"));
                Thread.Sleep(800);
                T.Check("outside the schedule the download waits", StateOf(e, id) == DlState.Waiting && sched.Requests == 0, Info(e, id));
                env.Advance(TimeSpan.FromMinutes(150));
                T.Check("inside the schedule it runs", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }

            // Простой ПК
            env = new FakeDlEnv();
            env.IdleSeconds = 0;
            DlSettings si = NewSettings(Dir("idle"));
            si.Segments = 1;
            using (DlEngine e = NewEngine(Dir("idle-store"), si, env))
            {
                DlAddRequest req = new DlAddRequest();
                req.Url = srv.Url("idle.bin");
                req.WhenIdle = true;
                string dup, err;
                string id = e.Add(req, out dup, out err);
                Thread.Sleep(800);
                T.Check("«when idle» waits while the user is active", StateOf(e, id) == DlState.Waiting && idle.Requests == 0, Info(e, id));
                env.IdleSeconds = 100000;
                env.Cpu = 90;
                Thread.Sleep(800);
                T.Check("«when idle» still waits while the CPU is busy", StateOf(e, id) == DlState.Waiting, Info(e, id));
                env.Cpu = 5;
                bool started = WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 200 * 1024; }, 8000);
                T.Check("«when idle» starts once the PC is idle", started, Info(e, id));
                env.Cpu = 90;
                Thread.Sleep(600);
                T.Check("our own load does not stop an idle download", StateOf(e, id) == DlState.Active, Info(e, id));
                env.Fullscreen = true;
                T.Check("a fullscreen app puts it on hold", WaitFor(delegate { return StateOf(e, id) == DlState.Waiting; }, 5000), Info(e, id));
                env.Fullscreen = false;
                env.Cpu = 5;
                T.Check("the fullscreen app gone — it runs again", WaitFor(delegate { return StateOf(e, id) == DlState.Active; }, 5000), Info(e, id));
                env.IdleSeconds = 3;
                bool held = WaitFor(delegate { return StateOf(e, id) == DlState.Waiting; }, 5000);
                long kept = e.Find(id).DoneBytes;
                T.Check("the user coming back puts it on hold, data kept", held && kept > 0 && kept < idle.Size, Info(e, id));
                env.IdleSeconds = 100000;
                env.Cpu = 5;
                idle.RateBps = 0;
                T.Check("idle again — it finishes intact",
                        WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000) && FileHash(e.Find(id).TargetPath) == DlTestServer.Sha256Of(32, idle.Size), Info(e, id));
            }

            // Лимитная сеть и батарея
            env = new FakeDlEnv();
            env.Metered = true;
            using (DlEngine e = NewEngine(Dir("metered-store"), NewSettings(Dir("metered")), env))
            {
                string id = Add(e, srv.Url("metered.bin"));
                Thread.Sleep(800);
                T.Check("a metered connection holds the queue", StateOf(e, id) == DlState.Waiting && net.Requests == 0 && e.GlobalGate.Length > 0, Info(e, id));
                env.Metered = false;
                env.Battery = true;
                Thread.Sleep(800);
                T.Check("battery power holds the queue", StateOf(e, id) == DlState.Waiting && net.Requests == 0, Info(e, id));
                env.Battery = false;
                T.Check("back on AC and unmetered — it runs", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 8000), Info(e, id));
            }
        }

        private static void RemoveAndDuplicates(DlTestServer srv, List<string> recycled)
        {
            DlTestServer.Res r = srv.Add("keep.bin", new DlTestServer.Res());
            r.Size = 60 * 1024; r.Seed = 40;
            DlTestServer.Res r2 = srv.Add("recycle.bin", new DlTestServer.Res());
            r2.Size = 60 * 1024; r2.Seed = 41;
            string folder = Dir("remove");
            using (DlEngine e = NewEngine(Dir("remove-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string keep = Add(e, srv.Url("keep.bin"));
                string gone = Add(e, srv.Url("recycle.bin"));
                WaitFor(delegate { return StateOf(e, keep) == DlState.Completed && StateOf(e, gone) == DlState.Completed; }, 10000);

                DlAddRequest again = new DlAddRequest();
                again.Url = srv.Url("keep.bin") + "#fragment";
                string dup, err;
                T.Check("the same link is refused as a duplicate", e.Add(again, out dup, out err) == null && dup == keep, err);
                again.AllowDuplicate = true;
                T.Check("a duplicate is accepted when the user insists", e.Add(again, out dup, out err) != null && dup == keep);

                DlAddRequest badScheme = new DlAddRequest();
                badScheme.Url = "file:///C:/Windows/win.ini";
                T.Check("add refuses file: links", e.Add(badScheme, out dup, out err) == null && err != null);
                DlAddRequest badFolder = new DlAddRequest();
                badFolder.Url = srv.Url("keep.bin");
                badFolder.AllowDuplicate = true;
                badFolder.Folder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                T.Check("add refuses a system folder", e.Add(badFolder, out dup, out err) == null && err != null);

                string why;
                string keepPath = e.Find(keep).TargetPath;
                T.Check("removing the record only", e.Remove(keep, false, out why) && e.Find(keep) == null, why);
                T.Check("the file stays when only the record is removed", File.Exists(keepPath));

                string gonePath = e.Find(gone).TargetPath;
                lock (recycled) recycled.Clear();
                T.Check("remove with files goes through the Recycle Bin", e.Remove(gone, true, out why), why);
                lock (recycled) T.Check("exactly the downloaded file was handed to the Recycle Bin", recycled.Count == 1 && recycled[0] == gonePath, string.Join("; ", recycled.ToArray()));
                T.Check("the engine never deletes the file itself", File.Exists(gonePath));

                string third = Add(e, srv.Url("recycle.bin"));
                WaitFor(delegate { return StateOf(e, third) == DlState.Completed; }, 10000);
                Func<string, string> ok = DlFiles.Recycler;
                DlFiles.Recycler = delegate { return "no Recycle Bin on this volume"; };
                try
                {
                    T.Check("no Recycle Bin — nothing removed, the record stays", !e.Remove(third, true, out why) && e.Find(third) != null && File.Exists(e.Find(third).TargetPath), why);
                }
                finally { DlFiles.Recycler = ok; }
            }
        }

        // ---------- канал ----------
        private static void Pipe(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("pipe.bin", new DlTestServer.Res());
            r.Size = 80 * 1024; r.Seed = 50;
            string name = "SysDeck.dl.test-" + Process.GetCurrentProcess().Id;
            using (DlEngine e = NewEngine(Dir("pipe-store"), NewSettings(Dir("pipe")), new FakeDlEnv()))
            {
                bool shutdown = false;
                DlCommands commands = new DlCommands(e, delegate { shutdown = true; }, null);
                using (DlPipeServer server = new DlPipeServer(name, commands.Handle, DlIpc.IsOwnImage))
                {
                    server.Start();
                    JVal hello = DlClient.Call(name, DlClient.Command("hello"), 3000);
                    T.Check("pipe: hello from our own exe is answered", hello != null && DlJson.Bool(hello, "ok", false) && DlJson.Int(hello, "version", 0) == 1);

                    JVal add = DlClient.Command("add");
                    add.Set("url", JVal.NewStr(srv.Url("pipe.bin")));
                    JVal added = DlClient.Call(name, add, 3000);
                    string id = DlJson.Str(added, "id", "");
                    T.Check("pipe: add returns an id", DlItem.IsValidId(id), added == null ? "null" : Jsn.Write(added));
                    WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 10000);
                    JVal list = DlClient.Call(name, DlClient.Command("list"), 3000);
                    bool listed = false;
                    JVal items = list == null ? null : list.Get("items");
                    if (items != null) foreach (JVal it in items.V) if (DlJson.Str(it, "Id", "") == id && DlJson.Str(it, "State", "") == "Completed") listed = true;
                    T.Check("pipe: list shows the completed download", listed);
                    T.Check("pipe: list carries no journals unless asked", items != null && items.V.Count > 0 && items.V[0].Get("Events") == null);

                    JVal bad = DlClient.Command("remove");
                    bad.Set("id", JVal.NewStr("..\\..\\Windows"));
                    JVal badResp = DlClient.Call(name, bad, 3000);
                    T.Check("pipe: an id that looks like a path is refused", badResp != null && !DlJson.Bool(badResp, "ok", true) && DlJson.Str(badResp, "error", "") == "bad id");
                    JVal unknown = DlClient.Call(name, DlClient.Command("runFile"), 3000);
                    T.Check("pipe: unknown command is refused", unknown != null && !DlJson.Bool(unknown, "ok", true));
                    JVal sysFolder = DlClient.Command("setSettings");
                    DlSettings evil = NewSettings(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                    sysFolder.Set("settings", evil.ToJson());
                    JVal sysResp = DlClient.Call(name, sysFolder, 3000);
                    T.Check("pipe: settings with a system folder are refused", sysResp != null && !DlJson.Bool(sysResp, "ok", true));

                    // Кадр длиной 2 ГБ: сервер рвёт соединение, а не выделяет память.
                    bool closed = false;
                    try
                    {
                        using (NamedPipeClientStream raw = new NamedPipeClientStream(".", name, PipeDirection.InOut))
                        {
                            raw.Connect(3000);
                            raw.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0x7F }, 0, 4);
                            raw.Flush();
                            closed = raw.ReadByte() == -1;
                        }
                    }
                    catch (IOException) { closed = true; }
                    T.Check("pipe: an oversized frame closes the connection", closed);
                    JVal after = DlClient.Call(name, DlClient.Command("hello"), 3000);
                    T.Check("pipe: the server keeps serving after a bad client", after != null);

                    PipeSecurity sec = DlIpc.CreatePipeSecurity();
                    AuthorizationRuleCollection rules = sec.GetAccessRules(true, true, typeof(System.Security.Principal.SecurityIdentifier));
                    bool onlyUser = rules.Count == 1 && ((PipeAccessRule)rules[0]).IdentityReference.Value == DlIpc.User().Value
                                    && ((PipeAccessRule)rules[0]).AccessControlType == AccessControlType.Allow;
                    T.Check("pipe: the ACL grants only the current user, inheritance cut", onlyUser && sec.AreAccessRulesProtected, rules.Count.ToString());

                    string ps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
                    if (File.Exists(ps))
                    {
                        int refusedBefore = server.Refused;
                        string script = "$r=-1; try { $p = New-Object System.IO.Pipes.NamedPipeClientStream('.', '" + name + "', 'InOut'); $p.Connect(5000); "
                                        + "$b=[byte[]](12,0,0,0,123,34,99,109,100,34,58,34,104,105,34,125); $p.Write($b,0,16); $p.Flush(); $r = $p.ReadByte() } catch { $r = -1 }; exit ($r + 10)";
                        ProcessStartInfo psi = new ProcessStartInfo(ps, "-NoProfile -NonInteractive -Command \"" + script + "\"");
                        psi.UseShellExecute = false;
                        psi.CreateNoWindow = true;
                        int exit = -100;
                        using (Process p = Process.Start(psi))
                        {
                            if (p.WaitForExit(30000)) exit = p.ExitCode;
                            else { try { p.Kill(); } catch { } }
                        }
                        T.Check("pipe: another program (PowerShell) is refused without an answer", exit == 9 && server.Refused > refusedBefore,
                                "exit " + exit + ", refused " + server.Refused);
                    }
                    else T.Skip("pipe: another program is refused", "powershell.exe not found");

                    JVal stop = DlClient.Call(name, DlClient.Command("shutdown"), 3000);
                    T.Check("pipe: shutdown reaches the agent", stop != null && shutdown);
                }
            }
        }
    }
}
