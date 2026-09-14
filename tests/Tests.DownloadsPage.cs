// Windows Process Cleaner — область «downloads», этап 2 (страница): перенос и копирование скачанного, продолжение
// прерванного переноса, очистка истории, уведомления, «есть ли работа» для ухода процесса, команды канала страницы.
//
// Всё — настоящий движок против локального HTTP-сервера и настоящих файлов фикстуры. Подменены только часы и Корзина
// (как во всей области). Перенос между томами идёт на второй локальный диск, если он есть и в него можно писать
// (папка WPC-tests-<pid> в корне, удаляется в конце); нет такого диска — проверка пропускается с причиной.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using WindowsProcessCleaner.Capture;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class DownloadsTests
    {
        private static void PageStage(DlTestServer srv, List<string> recycled)
        {
            RelocateCompleted(srv, recycled);
            RelocatePausedThenResume(srv);
            RelocateRefusals(srv);
            RelocateResumesInterruptedCopy(srv, recycled);
            RelocateCrossVolume(srv, recycled);
            HistoryAndNotices(srv);
            IdleExitClock();
            PagePipeCommands(srv);
        }

        private static bool WaitMoved(DlEngine e, string id)
        {
            return WaitFor(delegate { DlItem it = e.Find(id); return it != null && it.MoveTo.Length == 0; }, 20000);
        }

        private static string Completed(DlEngine e, DlTestServer srv, string key)
        {
            string id = Add(e, srv.Url(key));
            T.Check("fixture download " + key + " completes", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 15000), Info(e, id));
            return id;
        }

        private static string Journal(DlItem it)
        {
            List<string> lines = new List<string>();
            lock (it.Events) foreach (DlEvent ev in it.Events) lines.Add(ev.Text);
            return string.Join(" | ", lines.ToArray());
        }

        // ---------- перенос и копирование завершённой загрузки на том же томе ----------
        private static void RelocateCompleted(DlTestServer srv, List<string> recycled)
        {
            DlTestServer.Res r = srv.Add("move-a.bin", new DlTestServer.Res());
            r.Size = 300 * 1024; r.Seed = 60;
            DlTestServer.Res c = srv.Add("copy-a.bin", new DlTestServer.Res());
            c.Size = 200 * 1024; c.Seed = 61;
            string folder = Dir("reloc"), moveTo = Dir("reloc-moved"), copyTo = Dir("reloc-copies");
            string expectMove = DlTestServer.Sha256Of(60, r.Size), expectCopy = DlTestServer.Sha256Of(61, c.Size);
            using (DlEngine e = NewEngine(Dir("reloc-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string moved = Completed(e, srv, "move-a.bin");
                string oldPath = e.Find(moved).TargetPath;
                lock (recycled) recycled.Clear();
                string why;
                T.Check("move of a finished download is accepted", e.Relocate(moved, moveTo, false, out why), why);
                T.Check("the move finishes", WaitMoved(e, moved), Journal(e.Find(moved)));
                DlItem it = e.Find(moved);
                string newPath = Path.Combine(moveTo, "move-a.bin");
                T.Eq("the record now points to the new folder", newPath.ToLowerInvariant(), it.TargetPath.ToLowerInvariant());
                T.Eq("the moved file has the source bytes", expectMove, FileHash(newPath));
                T.Check("nothing is left at the old place", !File.Exists(oldPath));
                lock (recycled) T.Check("a rename on one volume hands nothing to the Recycle Bin", recycled.Count == 0, string.Join("; ", recycled.ToArray()));
                T.Check("no temporary move file is left", !File.Exists(newPath + DlEngine.MoveSuffix));
                T.Check("no move error recorded", it.MoveError.Length == 0, it.MoveError);

                string copied = Completed(e, srv, "copy-a.bin");
                string source = e.Find(copied).TargetPath;
                // В папке копий уже лежит чужой файл с тем же именем — его байты не должны измениться.
                string foreign = Fx.MakeFile(Path.Combine(copyTo, "copy-a.bin"), 1234);
                string foreignHash = FileHash(foreign);
                T.Check("copy of a finished download is accepted", e.Relocate(copied, copyTo, true, out why), why);
                T.Check("the copy finishes", WaitMoved(e, copied), Journal(e.Find(copied)));
                string copyPath = Path.Combine(copyTo, "copy-a (1).bin");
                T.Eq("the copy takes a free name next to a foreign file", expectCopy, FileHash(copyPath));
                T.Eq("the foreign file keeps its bytes", foreignHash, FileHash(foreign));
                T.Eq("the source stays in place after a copy", expectCopy, FileHash(source));
                T.Eq("a copy does not change the record's path", source.ToLowerInvariant(), e.Find(copied).TargetPath.ToLowerInvariant());
                T.Check("no temporary copy file is left", !File.Exists(copyPath + DlEngine.MoveSuffix));
                lock (recycled) T.Check("a copy hands nothing to the Recycle Bin", recycled.Count == 0);

                // Та же папка — отказ, а не перенос в себя.
                T.Check("moving into the same folder is refused", !e.Relocate(moved, moveTo, false, out why) && why != null);
            }
        }

        // ---------- недокачанная загрузка на паузе: частичный файл уезжает, докачка продолжается в новой папке ----------
        private static void RelocatePausedThenResume(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("move-part.bin", new DlTestServer.Res());
            r.Size = 1024 * 1024; r.Seed = 62; r.RateBps = 256 * 1024;
            string folder = Dir("part"), moveTo = Dir("part-moved");
            FakeDlEnv env = new FakeDlEnv();
            using (DlEngine e = NewEngine(Dir("part-store"), NewSettings(folder), env))
            {
                string id = Add(e, srv.Url("move-part.bin"));
                WaitFor(delegate { DlItem x = e.Find(id); return x != null && x.DoneBytes > 200 * 1024; }, 15000);
                e.Pause(id);
                T.Check("the slow download pauses", WaitFor(delegate { return StateOf(e, id) == DlState.Paused; }, 10000), Info(e, id));
                DlItem it = e.Find(id);
                long doneBefore = it.DoneBytes;
                string oldPart = it.PartPath;
                T.Check("a partial file exists before the move", File.Exists(oldPart));
                T.Check("copying an unfinished download is refused", !e.Relocate(id, moveTo, true, out _whyScratch) && _whyScratch != null);
                string why;
                T.Check("moving a paused download is accepted", e.Relocate(id, moveTo, false, out why), why);
                T.Check("the partial move finishes", WaitMoved(e, id), Journal(e.Find(id)));
                T.Check("the partial file left the old folder", !File.Exists(oldPart));
                T.Check("the partial file sits in the new folder", File.Exists(Path.Combine(moveTo, "move-part.bin" + DlPaths.PartSuffix)));
                int requestsBefore = r.RangesSeen.Count;
                T.Check("resume is accepted after the move", e.Resume(id));
                T.Check("the moved download completes", WaitFor(delegate { return StateOf(e, id) == DlState.Completed; }, 30000), Info(e, id));
                T.Eq("the finished file in the new folder has the source bytes", DlTestServer.Sha256Of(62, r.Size), FileHash(Path.Combine(moveTo, "move-part.bin")));
                bool resumedFromOffset = false;
                lock (srv.Gate)
                    for (int i = requestsBefore; i < r.RangesSeen.Count; i++)
                        if (r.RangesSeen[i] != "bytes=0-") resumedFromOffset = true;
                T.Check("after the move the download continued instead of starting over (" + doneBefore + " bytes kept)", resumedFromOffset);
            }
        }

        private static string _whyScratch;

        // ---------- отказы ----------
        private static void RelocateRefusals(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("move-busy.bin", new DlTestServer.Res());
            r.Size = 2 * 1024 * 1024; r.Seed = 63; r.RateBps = 64 * 1024;
            string folder = Dir("busy"), other = Dir("busy-other");
            using (DlEngine e = NewEngine(Dir("busy-store"), NewSettings(folder), new FakeDlEnv()))
            {
                string id = Add(e, srv.Url("move-busy.bin"));
                WaitFor(delegate { return StateOf(e, id) == DlState.Active && e.Find(id).DoneBytes > 0; }, 10000);
                string why;
                T.Check("moving a running download is refused", !e.Relocate(id, other, false, out why) && why != null, Info(e, id));
                T.Check("moving into a system folder is refused", !e.Relocate(id, Environment.GetFolderPath(Environment.SpecialFolder.Windows), false, out why) && why != null);
                T.Check("cancel of a move that does not exist is refused", !e.CancelRelocate(id));
                T.Check("nothing was created in the other folder", Directory.GetFileSystemEntries(other).Length == 0);
                e.Pause(id);
                WaitFor(delegate { return StateOf(e, id) == DlState.Paused; }, 10000);
            }
        }

        // ---------- прерванный перенос продолжается при следующем запуске, испорченный хвост пойман хешем ----------
        private static void RelocateResumesInterruptedCopy(DlTestServer srv, List<string> recycled)
        {
            DlTestServer.Res good = srv.Add("resume-good.bin", new DlTestServer.Res());
            good.Size = 3 * 1024 * 1024; good.Seed = 64;
            DlTestServer.Res bad = srv.Add("resume-bad.bin", new DlTestServer.Res());
            bad.Size = 3 * 1024 * 1024; bad.Seed = 65;
            string folder = Dir("resume-move"), dest = Dir("resume-move-dest"), store = Dir("resume-move-store");
            string goodId, badId, goodSrc, badSrc;
            DlSettings s = NewSettings(folder);
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                goodId = Completed(e, srv, "resume-good.bin");
                badId = Completed(e, srv, "resume-bad.bin");
                goodSrc = e.Find(goodId).TargetPath;
                badSrc = e.Find(badId).TargetPath;
                // Состояние, в котором процесс закрыли посреди копирования: запись знает цель и имя, временный файл — половина.
                foreach (string id in new[] { goodId, badId })
                {
                    DlItem it = e.Find(id);
                    it.MoveTo = dest;
                    it.MoveName = it.FileName;
                    it.MoveCopy = false;
                }
            }
            string goodTmp = Path.Combine(dest, "resume-good.bin") + DlEngine.MoveSuffix;
            string badTmp = Path.Combine(dest, "resume-bad.bin") + DlEngine.MoveSuffix;
            CopyPrefix(goodSrc, goodTmp, 2 * 1024 * 1024, -1);
            CopyPrefix(badSrc, badTmp, 2 * 1024 * 1024, 300 * 1024);    // байт в начале испорчен: докачка с 1 МБ его не перепишет

            lock (recycled) recycled.Clear();
            using (DlEngine e = NewEngine(store, s, new FakeDlEnv()))
            {
                T.Check("an interrupted move resumes on start", WaitMoved(e, goodId) && WaitMoved(e, badId), Journal(e.Find(goodId)) + " || " + Journal(e.Find(badId)));
                T.Eq("resumed move: the target has the source bytes", DlTestServer.Sha256Of(64, good.Size), FileHash(Path.Combine(dest, "resume-good.bin")));
                T.Eq("resumed move over a corrupted temp: the hash check forced a clean copy", DlTestServer.Sha256Of(65, bad.Size), FileHash(Path.Combine(dest, "resume-bad.bin")));
                T.Check("resumed moves record no error", e.Find(goodId).MoveError.Length == 0 && e.Find(badId).MoveError.Length == 0, e.Find(badId).MoveError);
                T.Check("no temporary files are left", !File.Exists(goodTmp) && !File.Exists(badTmp));
                lock (recycled)
                    T.Check("after a copying move both sources went to the Recycle Bin (and only them)",
                            recycled.Count == 2 && recycled.Contains(goodSrc) && recycled.Contains(badSrc), string.Join("; ", recycled.ToArray()));
                T.Eq("the record follows the moved file", Path.Combine(dest, "resume-good.bin").ToLowerInvariant(), e.Find(goodId).TargetPath.ToLowerInvariant());
            }
        }

        private static void CopyPrefix(string source, string target, int length, int corruptAt)
        {
            byte[] all = File.ReadAllBytes(source);
            byte[] part = new byte[Math.Min(length, all.Length)];
            Array.Copy(all, part, part.Length);
            if (corruptAt >= 0 && corruptAt < part.Length) part[corruptAt] ^= 0x5A;
            File.WriteAllBytes(target, part);
        }

        // ---------- между томами: настоящее копирование, сверка, метка «из интернета», исходник в Корзину ----------
        private static void RelocateCrossVolume(DlTestServer srv, List<string> recycled)
        {
            string other = OtherVolumeDir();
            if (other == null)
            {
                T.Skip("move between volumes", "no second writable local volume on this machine");
                return;
            }
            try
            {
                DlTestServer.Res r = srv.Add("cross.bin", new DlTestServer.Res());
                r.Size = 5 * 1024 * 1024 + 123; r.Seed = 66;
                string folder = Dir("cross");
                using (DlEngine e = NewEngine(Dir("cross-store"), NewSettings(folder), new FakeDlEnv()))
                {
                    string id = Completed(e, srv, "cross.bin");
                    string source = e.Find(id).TargetPath;
                    T.Check("fixture: zone stream written on the source", DlMotw.WriteZoneStream(source, srv.Url("cross.bin") + "?token=secret", "") == null);
                    lock (recycled) recycled.Clear();
                    string why;
                    T.Check("move to another volume is accepted", e.Relocate(id, other, false, out why), why);
                    T.Check("move to another volume finishes", WaitMoved(e, id), Journal(e.Find(id)));
                    string target = Path.Combine(other, "cross.bin");
                    T.Eq("cross-volume move: target bytes equal the source", DlTestServer.Sha256Of(66, r.Size), FileHash(target));
                    string zone = DlMotw.ReadZoneStream(target);
                    T.Check("cross-volume move carries the internet zone mark", zone != null && zone.Contains("ZoneId=3") && !zone.Contains("secret"), zone);
                    lock (recycled) T.Check("cross-volume move hands exactly the source to the Recycle Bin", recycled.Count == 1 && recycled[0] == source, string.Join("; ", recycled.ToArray()));
                    T.Check("cross-volume move leaves no temporary file", !File.Exists(target + DlEngine.MoveSuffix));
                    T.Check("the record points to the other volume", e.Find(id).TargetPath.StartsWith(other, StringComparison.OrdinalIgnoreCase), e.Find(id).TargetPath);
                }
            }
            finally
            {
                try { Directory.Delete(other, true); } catch (Exception ex) { Console.WriteLine("cleanup " + other + ": " + ex.Message); }
            }
        }

        private static string OtherVolumeDir()
        {
            string home = Path.GetPathRoot(Fx.Root) ?? "";
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    if (string.Equals(d.RootDirectory.FullName, home, StringComparison.OrdinalIgnoreCase)) continue;
                    if (d.AvailableFreeSpace < 64L * 1024 * 1024) continue;
                    string dir = Path.Combine(d.RootDirectory.FullName, "WPC-tests-" + Process.GetCurrentProcess().Id);
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch { }
            }
            return null;
        }

        // ---------- история, уведомления, «есть работа» ----------
        private static void HistoryAndNotices(DlTestServer srv)
        {
            DlTestServer.Res a = srv.Add("hist-a.bin", new DlTestServer.Res());
            a.Size = 50 * 1024; a.Seed = 67;
            DlTestServer.Res b = srv.Add("hist-b.bin", new DlTestServer.Res());
            b.Size = 50 * 1024; b.Seed = 68;
            DlTestServer.Res missing = srv.Add("hist-404.bin", new DlTestServer.Res());
            missing.Status = 404;
            DlTestServer.Res slow = srv.Add("hist-slow.bin", new DlTestServer.Res());
            slow.Size = 1024 * 1024; slow.Seed = 69; slow.RateBps = 64 * 1024;
            string folder = Dir("hist");
            FakeDlEnv env = new FakeDlEnv();
            List<DlNotice> notices = new List<DlNotice>();
            using (DlEngine e = NewEngine(Dir("hist-store"), NewSettings(folder), env))
            {
                e.Notice = delegate(DlNotice n) { lock (notices) notices.Add(n); };
                T.Check("an empty engine has no pending work", !e.HasPendingWork);
                string ida = Add(e, srv.Url("hist-a.bin"));
                T.Check("a queued download is pending work", e.HasPendingWork);
                WaitFor(delegate { return StateOf(e, ida) == DlState.Completed; }, 10000);
                T.Check("nothing pending once the download is done", WaitFor(delegate { return !e.HasPendingWork; }, 3000));
                string idb = Completed(e, srv, "hist-b.bin");
                string id404 = Add(e, srv.Url("hist-404.bin"));
                WaitFor(delegate { return StateOf(e, id404) == DlState.Failed; }, 10000);
                string idSlow = Add(e, srv.Url("hist-slow.bin"));
                WaitFor(delegate { return StateOf(e, idSlow) == DlState.Active; }, 10000);
                e.Pause(idSlow);
                WaitFor(delegate { return StateOf(e, idSlow) == DlState.Paused; }, 10000);

                lock (notices)
                {
                    DlNotice na = notices.Find(delegate(DlNotice n) { return n.Id == ida; });
                    T.Check("a finished download raises a completed notice with its path",
                            na != null && na.Kind == DlNoticeKind.Completed && string.Equals(na.Path, e.Find(ida).TargetPath, StringComparison.OrdinalIgnoreCase));
                    DlNotice nf = notices.Find(delegate(DlNotice n) { return n.Id == id404; });
                    T.Check("a failed download raises a failed notice without a path", nf != null && nf.Kind == DlNoticeKind.Failed && nf.Path.Length == 0 && nf.Text.Length > 0);
                    T.Check("a pause raises no notice", notices.Find(delegate(DlNotice n) { return n.Id == idSlow; }) == null);
                }
                ToastInfo toast = DlNotifier.InfoFor(notices.Find(delegate(DlNotice n) { return n.Id == ida; }));
                T.Check("the completed toast offers the folder, not the file", toast.Kind == ToastKind.Download && toast.Path == e.Find(ida).TargetPath);

                string bPath = e.Find(idb).TargetPath;
                File.Delete(bPath);
                string aPath = e.Find(ida).TargetPath;
                T.Eq("clear history of records with missing files removes one", 1, e.ClearHistory(0, true));
                T.Check("the record whose file vanished is gone, the other stays", e.Find(idb) == null && e.Find(ida) != null);
                T.Eq("recent completed records survive a 30-day clean", 0, e.ClearHistory(30, false));
                env.Advance(TimeSpan.FromDays(31));
                T.Eq("a 30-day clean removes the old completed record", 1, e.ClearHistory(30, false));
                T.Check("the file of a cleared record stays on disk", File.Exists(aPath));
                T.Check("failed and paused records are never cleared", e.Find(id404) != null && e.Find(idSlow) != null);
            }
        }

        private static void IdleExitClock()
        {
            DateTime t0 = new DateTime(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);
            DlIdleExit idle = new DlIdleExit(t0, 60);
            T.Check("idle exit: not before the minute", !idle.ShouldExit(t0.AddSeconds(59), false));
            T.Check("idle exit: after a quiet minute", idle.ShouldExit(t0.AddSeconds(60), false));
            T.Check("idle exit: pending work keeps the process", !idle.ShouldExit(t0.AddSeconds(120), true));
            T.Check("idle exit: the quiet minute restarts after work", !idle.ShouldExit(t0.AddSeconds(179), false) && idle.ShouldExit(t0.AddSeconds(180), false));
            idle.Touch(t0.AddSeconds(200));
            T.Check("idle exit: a client call restarts the minute", !idle.ShouldExit(t0.AddSeconds(250), false));
            T.Check("idle exit: a clock jump back does not end the process", !idle.ShouldExit(t0.AddSeconds(-3600), false) && !idle.ShouldExit(t0.AddSeconds(-3590), false));
        }

        private static void PagePipeCommands(DlTestServer srv)
        {
            DlTestServer.Res r = srv.Add("pipe-move.bin", new DlTestServer.Res());
            r.Size = 70 * 1024; r.Seed = 70;
            string name = "WindowsProcessCleaner.dl.test2-" + Process.GetCurrentProcess().Id;
            string folder = Dir("pipe2"), dest = Dir("pipe2-dest");
            using (DlEngine e = NewEngine(Dir("pipe2-store"), NewSettings(folder), new FakeDlEnv()))
            {
                DlCommands commands = new DlCommands(e, null, null);
                DateTime before = DateTime.UtcNow.AddSeconds(-1);
                using (DlPipeServer server = new DlPipeServer(name, commands.Handle, DlIpc.IsOwnImage))
                {
                    server.Start();
                    string id = Completed(e, srv, "pipe-move.bin");
                    JVal move = DlClient.Command("move");
                    move.Set("id", JVal.NewStr(id));
                    move.Set("folder", JVal.NewStr(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
                    JVal refused = DlClient.Call(name, move, 3000);
                    T.Check("pipe: move into a system folder is refused with a reason", refused != null && !DlJson.Bool(refused, "ok", true) && DlJson.Str(refused, "error", "").Length > 0);
                    T.Check("pipe: a call marks the client as active", commands.LastCallUtc > before);
                    move.Set("folder", JVal.NewStr(dest));
                    JVal ok = DlClient.Call(name, move, 3000);
                    T.Check("pipe: move is accepted", ok != null && DlJson.Bool(ok, "ok", false), ok == null ? "null" : Jsn.Write(ok));
                    WaitMoved(e, id);
                    T.Eq("pipe: the file arrived", DlTestServer.Sha256Of(70, r.Size), FileHash(Path.Combine(dest, "pipe-move.bin")));
                    JVal got = DlClient.Call(name, IdCommand("get", id), 3000);
                    JVal item = got == null ? null : got.Get("item");
                    T.Check("pipe: get shows the new folder and an empty move state",
                            item != null && string.Equals(DlJson.Str(item, "Folder", ""), dest, StringComparison.OrdinalIgnoreCase) && DlJson.Str(item, "MoveTo", "x") == "");
                    JVal clear = DlClient.Command("clearHistory");
                    JVal cleared = DlClient.Call(name, clear, 3000);
                    T.Check("pipe: clearHistory needs no id and reports the count", cleared != null && DlJson.Bool(cleared, "ok", false) && DlJson.Int(cleared, "removed", -1) == 1,
                            cleared == null ? "null" : Jsn.Write(cleared));
                    T.Check("pipe: the moved file survives clearing the history", File.Exists(Path.Combine(dest, "pipe-move.bin")));
                }
            }
        }

        // ---------- страница: снимок, фильтры, разбор ссылок, подпись, канал ----------

        private static void PageView()
        {
            // Канал: у хранилища по умолчанию имён с суффиксом нет, у любой другой папки — свои, устойчивые к регистру и слэшу.
            string standard = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WindowsProcessCleaner", "downloads");
            T.Eq("channel: the default data folder keeps the old names", "", DlIpc.ChannelFor(standard, standard));
            T.Eq("channel: case and a trailing slash do not matter", "", DlIpc.ChannelFor(standard.ToUpperInvariant() + "\\", standard));
            string portable = DlIpc.ChannelFor(@"D:\Portable\WPC\data\downloads", standard);
            T.Check("channel: another data folder gets a dot and 12 hex digits", System.Text.RegularExpressions.Regex.IsMatch(portable, "^\\.[0-9a-f]{12}$"), portable);
            T.Eq("channel: the same folder in another case gets the same channel", portable, DlIpc.ChannelFor(@"d:\portable\wpc\DATA\downloads\", standard));
            T.Check("channel: two data folders never share a channel", portable != DlIpc.ChannelFor(@"D:\Portable\WPC2\data\downloads", standard));
            T.Check("channel: this test run (WPC_DATA_DIR) does not talk to the real process",
                    DlIpc.Channel.Length == 13 && DlIpc.PipeName.EndsWith(DlIpc.Channel) && DlIpc.MutexName.EndsWith(DlIpc.Channel)
                    && DlIpc.ShutdownEventName.Contains(DlIpc.Channel) && DlLauncher.RunValueName.EndsWith(DlIpc.Channel),
                    DlIpc.PipeName + " | " + DlIpc.MutexName);

            // Ссылки из буфера, перетаскивания и поля диалога.
            List<string> ok = new List<string>(), bad = new List<string>();
            DlView.ParseLinks("https://a.example/x.zip, http://b.example/y.iso\r\nnot a link\r\n<https://a.example/x.zip>\n\n  ftp://z.example/f  \n(see https://c.example/d.exe).", ok, bad);
            T.Eq("links: http and https kept in order, repeats and punctuation dropped",
                 "https://a.example/x.zip|http://b.example/y.iso|https://c.example/d.exe", string.Join("|", ok.ToArray()));
            T.Eq("links: lines without a link are reported whole", "not a link|ftp://z.example/f", string.Join("|", bad.ToArray()));
            ok.Clear();
            DlView.ParseLinks("javascript:alert(1) file:///C:/Windows/notepad.exe", ok, null);
            T.Check("links: other schemes are never accepted (and bad may be null)", ok.Count == 0);
            T.Eq("shortcut: URL= inside [InternetShortcut]", "https://x.example/y.msi",
                 DlView.LinkFromShortcut("[InternetShortcut]\r\nIDList=\r\nURL=https://x.example/y.msi\r\n"));
            T.Check("shortcut: URL= in another section is ignored", DlView.LinkFromShortcut("[Other]\r\nURL=https://x.example/\r\n") == null);
            T.Check("shortcut: a file:// target is refused", DlView.LinkFromShortcut("[InternetShortcut]\nURL=file:///C:/Windows/System32/calc.exe") == null);

            T.Check("open: programs and scripts ask first", DlView.OpenNeedsConfirm("setup.exe") && DlView.OpenNeedsConfirm("Tool.MSI") && DlView.OpenNeedsConfirm("run.bat"));
            T.Check("open: media and documents open directly", !DlView.OpenNeedsConfirm("movie.mkv") && !DlView.OpenNeedsConfirm("book.pdf"));

            // Фильтры по состоянию, типу и источнику.
            DateTime now = DateTime.UtcNow;
            DlItem active = ViewItem("a.exe", DlState.Active, "chrome");
            DlItem queued = ViewItem("b.zip", DlState.Queued, "manual");
            DlItem scheduled = ViewItem("c.mkv", DlState.Scheduled, "edge");
            scheduled.StartAtUtc = now.AddHours(2);
            DlItem paused = ViewItem("d.iso", DlState.Paused, "manual");
            DlItem done = ViewItem("e.pdf", DlState.Completed, "firefox");
            DlItem moving = ViewItem("f.pdf", DlState.Completed, "manual");
            moving.MoveTo = @"D:\elsewhere";
            DlItem failed = ViewItem("g.bin", DlState.Failed, "yandex");
            DlItem expired = ViewItem("h.msi", DlState.NeedsLink, "chrome");
            List<DlItem> all = new List<DlItem> { active, queued, scheduled, paused, done, moving, failed, expired };
            DlSnapshot snap = DlSnapshot.FromStore(new List<DlItem>(all));
            T.Check("store snapshot is not live", !snap.Live);
            T.Check("store snapshot: a download cut off mid-way shows as queued, not as running", active.State == DlState.Queued && active.WaitReason == "");
            active.State = DlState.Active;
            T.Eq("filter counts all/running/waiting/paused/done/errors", "8/2/2/1/1/2",
                 DlView.Count(snap, DlStateFilter.All) + "/" + DlView.Count(snap, DlStateFilter.Running) + "/" + DlView.Count(snap, DlStateFilter.Waiting) + "/"
                 + DlView.Count(snap, DlStateFilter.Paused) + "/" + DlView.Count(snap, DlStateFilter.Done) + "/" + DlView.Count(snap, DlStateFilter.Errors));
            T.Check("a finished download being moved counts as running, not done", DlView.InState(moving, DlStateFilter.Running) && !DlView.InState(moving, DlStateFilter.Done));
            T.Check("type filter: programs", DlView.Matches(expired, DlStateFilter.All, "programs", "") && !DlView.Matches(done, DlStateFilter.All, "programs", ""));
            T.Check("type + source + state filters combine", DlView.Matches(expired, DlStateFilter.Errors, "programs", "chrome")
                    && !DlView.Matches(expired, DlStateFilter.Errors, "programs", "edge") && !DlView.Matches(expired, DlStateFilter.Done, "", ""));
            T.Eq("categories", "programs|archives|video|images|documents|other",
                 DlView.CategoryOf("a.EXE") + "|" + DlView.CategoryOf("b.tar") + "|" + DlView.CategoryOf("c.webm") + "|" + DlView.CategoryOf("d.vhdx") + "|"
                 + DlView.CategoryOf("e.docx") + "|" + DlView.CategoryOf("noext"));

            // Числа.
            DlRow half = new DlRow();
            half.Item = ViewItem("half.bin", DlState.Active, "manual");
            half.Item.Total = 1000;
            half.Done = 500;
            half.Speed = 100;
            T.Check("fraction: half", Math.Abs(DlView.Fraction(half) - 0.5) < 1e-9);
            T.Check("eta: 500 bytes at 100/s is 5 seconds", DlView.EtaText(half).StartsWith("5 "), DlView.EtaText(half));
            half.Speed = 0;
            T.Eq("eta: no speed, no estimate", "", DlView.EtaText(half));
            half.Item.Total = -1;
            T.Check("fraction: unknown size", DlView.Fraction(half) < 0);
            half.Item.MoveTo = @"D:\x";
            half.MoveDone = 250;
            T.Check("fraction of a move uses the moved bytes", Math.Abs(DlView.Fraction(half) - 0.5) < 1e-9);
            T.Check("durations round up to the next unit", DlView.Duration(125).StartsWith("3") && DlView.Duration(11100).StartsWith("3 ") && DlView.Duration(3 * 86400).StartsWith("3 "),
                    DlView.Duration(125) + " / " + DlView.Duration(11100) + " / " + DlView.Duration(3 * 86400));
            DlRow sched = new DlRow();
            sched.Item = scheduled;
            T.Check("postponed state names the local start time", DlView.StateText(sched, now).Contains(scheduled.StartAtUtc.ToLocalTime().ToString("HH:mm")), DlView.StateText(sched, now));

            // Ответ list → снимок.
            JVal root = JVal.NewObj();
            root.Set("ok", DlJson.B(true));
            root.Set("speed", DlJson.N(4096));
            root.Set("active", DlJson.N(1));
            JVal items = JVal.NewArr();
            JVal one = done.ToJson(false);
            one.Set("Speed", DlJson.N(777));
            one.Set("HasCookies", DlJson.B(true));
            items.V.Add(one);
            items.V.Add(JVal.NewStr("garbage"));
            root.Set("items", items);
            DlSnapshot live = DlSnapshot.FromList(root);
            T.Check("list answer: live snapshot with totals and rows", live != null && live.Live && live.Speed == 4096 && live.Active == 1 && live.Rows.Count == 1
                    && live.Rows[0].Speed == 777 && live.Rows[0].HasCookies && live.Rows[0].Item.Id == done.Id, Jsn.Write(root));
            T.Check("list answer: null or refused gives no snapshot", DlSnapshot.FromList(null) == null && DlSnapshot.FromList(JVal.NewObj()) == null);

            // Подпись: встроенная подпись Microsoft у csc.exe, та же копия с изменённым байтом, файл без подписи.
            string dir = Path.Combine(Path.GetTempPath(), "wpc-dlverify-" + Process.GetCurrentProcess().Id);
            Directory.CreateDirectory(dir);
            try
            {
                string csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework64\v4.0.30319\csc.exe");
                if (!File.Exists(csc)) csc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"Microsoft.NET\Framework\v4.0.30319\csc.exe");
                string copy = Path.Combine(dir, "signed.exe");
                File.Copy(csc, copy);
                DlSignatureInfo valid = DlVerify.Check(copy);
                T.Check("signature: csc.exe (embedded Microsoft signature) is valid with a publisher",
                        valid.Kind == DlSignature.Valid && valid.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0, valid.Kind + " " + valid.Publisher + " " + valid.Detail);
                string patched = Path.Combine(dir, "patched.exe");
                byte[] bytes = File.ReadAllBytes(copy);
                bytes[0x1000] ^= 0xFF;
                File.WriteAllBytes(patched, bytes);
                DlSignatureInfo broken = DlVerify.Check(patched);
                T.Check("signature: one changed byte makes it invalid", broken.Kind == DlSignature.Invalid, broken.Kind + " " + broken.Detail);
                string text = Path.Combine(dir, "fake.exe");
                File.WriteAllText(text, "not a program");
                DlSignatureInfo none = DlVerify.Check(text);
                T.Check("signature: a file without a signature is unsigned", none.Kind == DlSignature.Unsigned, none.Kind + " " + none.Detail);
                T.Check("signature: a missing file is not reported as valid", DlVerify.Check(Path.Combine(dir, "missing.exe")).Kind != DlSignature.Valid);
                T.Eq("zone: no mark, no text", "", DlVerify.ZoneText(text));
                T.Check("fixture: zone stream written", DlMotw.WriteZoneStream(text, "https://example.com/fake.exe", "") == null);
                T.Check("zone: the internet mark is described", DlVerify.ZoneText(text).Length > 0);

                // Папки из правила ещё может не быть — её создаст первая загрузка; место спрашивается у ближайшей существующей.
                long tempFree = DlFiles.FreeSpace(dir);
                long missingFree = DlFiles.FreeSpace(Path.Combine(dir, @"not\yet\created"));
                T.Check("free space: a folder that does not exist yet reports its volume", tempFree >= 0 && missingFree >= 0, tempFree + " / " + missingFree);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        private static DlItem ViewItem(string name, DlState state, string source)
        {
            DlItem it = new DlItem();
            it.Id = DlItem.NewId();
            it.Url = "https://example.com/" + name;
            it.OriginalUrl = it.Url;
            it.FileName = name;
            it.Folder = @"C:\Downloads";
            it.State = state;
            it.Source = source;
            it.AddedUtc = DateTime.UtcNow;
            return it;
        }

        private static JVal IdCommand(string cmd, string id)
        {
            JVal o = DlClient.Command(cmd);
            o.Set("id", JVal.NewStr(id));
            return o;
        }
    }
}
