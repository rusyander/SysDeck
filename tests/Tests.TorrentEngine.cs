// SysDeck — область «torrent», движок загрузок с торрентами: DlEngine добавляет .torrent и magnet,
// запускает их в своей сессии, ставит на паузу, переживает падение процесса, качает выбранные файлы, останавливает
// раздачу по рейтингу и условиям очереди, удаляет в Корзину.
//
// Ненастоящее здесь только окружение: пиры — сессии BtSession на 127.0.0.1 (сессия движка тоже на петле), батарея и часы —
// FakeDlEnv, Корзина — подменный делегат, правило брандмауэра — подменная проверка (окно Windows не появляется). DHT, LSD
// и проброс порта выключены: за пределы петли ничего не уходит.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunEngine()
        {
            Func<string, string> savedRecycler = DlFiles.Recycler;
            try
            {
                WireRun("engine .torrent", EngTorrentFile);
                WireRun("engine magnet", EngMagnet);
                WireRun("engine pause", EngPauseResume);
                WireRun("engine crash", EngCrashResume);
                WireRun("engine file selection", EngFileSelection);
                WireRun("engine ratio", EngRatioLimit);
                WireRun("engine refusals", EngRefusals);
                WireRun("engine pipe", EngPipe);
                WireRun("engine watch folder", EngWatchFolder);
                WireRun("engine downloaded .torrent", EngDownloadedTorrent);
            }
            finally { DlFiles.Recycler = savedRecycler; }
        }

        // ================================================================== //
        //  Помощники
        // ================================================================== //
        private static DlSettings EngSettings(string folder)
        {
            DlSettings s = new DlSettings();
            s.Folder = folder;
            s.MarkOfTheWeb = false;
            s.PreventSleep = false;
            s.PauseOnBattery = true;
            s.BtInbound = true;
            s.BtPortMapping = false;
            s.BtDht = false;
            s.BtLsd = false;
            s.BtPex = false;
            return s;
        }

        // Движок как в процессе загрузок, только сессия на петле и «правило брандмауэра есть». Запускает вызывающий.
        private static DlEngine EngEngine(string store, DlSettings s, FakeDlEnv env, List<DlNotice> notices)
        {
            DlEngine e = new DlEngine(new DlStore(store), s, env);
            e.BtBind = IPAddress.Loopback;
            e.BtInboundReady = delegate { return true; };
            if (notices != null) e.Notice = delegate(DlNotice n) { lock (notices) notices.Add(n); };
            return e;
        }

        private static DlTorrentRequest EngFile(byte[] torrent)
        {
            DlTorrentRequest r = new DlTorrentRequest();
            r.TorrentBytes = torrent;
            return r;
        }

        private static string EngAdd(DlEngine e, DlTorrentRequest r)
        {
            string dup, err;
            string id = e.AddTorrent(r, out dup, out err);
            if (id == null) throw new InvalidOperationException("AddTorrent: " + err);
            return id;
        }

        // Торрент движка дошёл до сессии — дать ему адрес пира (у .torrent без трекеров других источников нет).
        private static BtTorrent EngFeed(DlEngine e, BtMeta meta, BtSession peer)
        {
            BtTorrent t = null;
            WireWaitFor(delegate
            {
                BtSession ses = e.TorrentSession;
                t = ses == null ? null : ses.Find(meta.InfoHash);
                return t != null;
            }, 10000);
            if (t != null) t.AddPeer(WireEp(peer));
            return t;
        }

        private static BtTorrent EngRunning(DlEngine e, BtMeta meta)
        {
            BtSession ses = e.TorrentSession;
            return ses == null ? null : ses.Find(meta.InfoHash);
        }

        private static bool EngWaitState(DlEngine e, string id, DlState state, int ms)
        {
            return WireWaitFor(delegate { DlItem it = e.Find(id); return it != null && it.State == state; }, ms);
        }

        private static string EngState(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            if (it == null) return "no item";
            return it.State + " done " + it.DoneBytes + "/" + it.Total + " peers " + it.ActiveConnections + " name '" + it.FileName + "'"
                   + (it.WaitReason.Length > 0 ? " wait " + it.WaitReason : "") + (it.Error.Length > 0 ? " err " + it.Error : "");
        }

        private static string EngJournal(DlEngine e, string id)
        {
            DlItem it = e.Find(id);
            if (it == null) return "";
            List<string> lines = new List<string>();
            lock (it.Events) foreach (DlEvent x in it.Events) lines.Add(x.Text);
            return string.Join(" | ", lines.ToArray());
        }

        // null — каждый файл под папкой открывается без общего доступа (сессия его не держит).
        private static string EngLocked(string dir)
        {
            if (!Directory.Exists(dir)) return "no folder " + dir;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { using (new FileStream(f, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } }
                catch (IOException ex) { return f + ": " + ex.Message; }
            }
            return null;
        }

        private static int EngResumePieces(DlEngine e, BtMeta meta)
        {
            BtResume r = BtResume.Load(BtResume.FileFor(e.TorrentsDir, meta.HexHash));
            if (r == null || r.PieceCount != meta.PieceCount) return 0;
            return BtBitfield.FromBytes(r.Have, r.PieceCount).SetCount;
        }

        private static int EngNotices(List<DlNotice> notices, string id, DlNoticeKind kind)
        {
            int n = 0;
            lock (notices) foreach (DlNotice x in notices) if (x.Id == id && x.Kind == kind) n++;
            return n;
        }

        private static bool EngSideFiles(DlEngine e, string hash)
        {
            return File.Exists(Path.Combine(e.TorrentsDir, hash + ".torrent")) || File.Exists(BtResume.FileFor(e.TorrentsDir, hash));
        }

        // ================================================================== //
        //  .torrent: загрузка → раздача, уведомление, порт, условие очереди, удаление
        // ================================================================== //
        private static void EngTorrentFile()
        {
            List<BtFxFile> files = SesFiles(301, 700000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-file");
            string dl = Fx.MakeDir(root, "dl");
            FakeDlEnv env = new FakeDlEnv();
            List<DlNotice> notices = new List<DlNotice>();
            List<DlSettings> persisted = new List<DlSettings>();
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); return null; };
            BtSession seed = null;
            DlEngine e = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                WireAddSeed(seed, meta, files, Fx.MakeDir(root, "seed"));
                e = EngEngine(Fx.MakeDir(root, "store"), EngSettings(dl), env, notices);
                e.SettingsPersist = delegate(DlSettings x) { lock (persisted) persisted.Add(x); };
                e.Start();
                string id = EngAdd(e, EngFile(torrent));
                BtTorrent t = EngFeed(e, meta, seed);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                DlItem it = e.Find(id);
                T.Check("engine: .torrent add downloads from the peer, verifies and goes on seeding, SHA-256 equal",
                        t != null && seeding && same && it.CompletedUtc != DateTime.MinValue && it.FileName == "wire"
                        && it.Total == meta.TotalSize && it.DoneBytes == meta.TotalSize, EngState(e, id) + " " + info);
                Thread.Sleep(800);
                T.Check("engine: one «completed» notice for the torrent, not one per tick", EngNotices(notices, id, DlNoticeKind.Completed) == 1,
                        EngNotices(notices, id, DlNoticeKind.Completed).ToString());
                T.Check("engine: the .torrent is kept beside the store for later starts",
                        File.Exists(Path.Combine(e.TorrentsDir, meta.HexHash + ".torrent")));

                JVal card = e.TorrentJson(id);
                JVal cardFiles = card == null ? null : card.Get("files");
                bool filesDone = cardFiles != null && cardFiles.V.Count == 2;
                if (filesDone) foreach (JVal f in cardFiles.V) filesDone &= f.GetStr("done") == f.GetStr("size");
                JVal cardSession = card == null ? null : card.Get("session");
                T.Check("engine: torrent card lists both files fully done and the session listening",
                        filesDone && cardSession != null && cardSession.Get("inbound").B && card.Get("peers") != null,
                        card == null ? "null card" : Jsn.Write(card));
                // Страница разбирает этот же ответ: все куски проверены, оба файла «готов», строка сети с портом.
                DlTorrentCard view = DlTorrentCard.FromJson(card);
                List<string[]> rows = view == null ? null : DlTorrentView.FileRows(view);
                T.Check("engine → page: the card parsed from the engine's answer shows every piece verified and both files done",
                        view != null && view.Live && view.PieceCount == meta.PieceCount && view.PiecesHave() == meta.PieceCount
                        && view.PiecesFraction(0, view.PieceCount) == 1 && rows.Count == 2 && rows[0][2] == Tr.S("готов", "done") && rows[1][2] == Tr.S("готов", "done")
                        && DlTorrentView.SessionText(view).Contains(e.TorrentSession.Context.Port.ToString()),
                        view == null ? "null" : view.PiecesHave() + "/" + view.PieceCount + " " + DlTorrentView.SessionText(view));
                // Строка состояния страницы из ответа list: раздача — это работа процесса, а не «работы нет».
                string seedStatus = EngStatusLine(e);
                T.Check("engine → page: a seeding torrent is work in the status line — «seeding: 1», not «nothing to do»",
                        seedStatus.Contains(Tr.S("раздаются: 1", "seeding: 1")) && !seedStatus.Contains(Tr.S("работы нет", "nothing to do")), seedStatus);

                // Порт выбран движком и сохранён; страница, приславшая старые настройки с 0, его не сбрасывает и сессию не пересоздаёт.
                BtSession ses = e.TorrentSession;
                int port = ses == null ? -1 : ses.Context.Port;
                T.Check("engine: the auto-chosen port is persisted once through SettingsPersist",
                        persisted.Count == 1 && persisted[0].BtPort == port && e.Settings.BtPort == port && port >= 49152,
                        persisted.Count + " saves, port " + port + ", settings " + e.Settings.BtPort);
                e.UpdateSettings(EngSettings(dl));
                Thread.Sleep(700);
                T.Check("engine: settings with port 0 from the page keep the chosen port and the same session",
                        e.Settings.BtPort == port && e.TorrentSession == ses && EngWaitState(e, id, DlState.Seeding, 100), EngState(e, id));

                // Батарея: раздача останавливается (файлы отпущены), питание вернулось — снова раздаёт без нового уведомления.
                env.Battery = true;
                bool waiting = EngWaitState(e, id, DlState.Waiting, 5000);
                T.Check("engine: battery gate stops seeding ⇒ Waiting with a reason, files released",
                        waiting && e.Find(id).WaitReason.Length > 0 && EngRunning(e, meta) == null && EngLocked(Path.Combine(dl, "wire")) == null,
                        EngState(e, id) + " " + EngLocked(Path.Combine(dl, "wire")));
                env.Battery = false;
                bool back = EngWaitState(e, id, DlState.Seeding, 10000);
                T.Check("engine: gate cleared ⇒ seeding again, no second notice", back && EngNotices(notices, id, DlNoticeKind.Completed) == 1,
                        EngState(e, id));

                string why;
                bool removed = e.Remove(id, true, out why);
                T.Check("engine: remove with files ⇒ torrent folder recycled as a whole, record and side files gone",
                        removed && recycled.Count == 1 && recycled[0] == Path.Combine(dl, "wire") && e.Find(id) == null
                        && !EngSideFiles(e, meta.HexHash) && EngRunning(e, meta) == null,
                        why + " recycled: " + string.Join("; ", recycled.ToArray()));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        // ================================================================== //
        //  magnet: метаданные от пира из x.pe, имя корня, файл торрента рядом
        // ================================================================== //
        private static void EngMagnet()
        {
            List<BtFxFile> files = new List<BtFxFile> { new BtFxFile(BtFx.Data(500000, 311)) };
            string err;
            BtMeta meta = BtMeta.Parse(BtFx.Build("wire", files, 32768, 1, false, null), out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-magnet");
            string dl = Fx.MakeDir(root, "dl");
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); return null; };
            BtSession seed = null;
            DlEngine e = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                WireAddSeed(seed, meta, files, Fx.MakeDir(root, "seed"));
                e = EngEngine(Fx.MakeDir(root, "store"), EngSettings(dl), new FakeDlEnv(), null);
                e.Start();
                string link = "magnet:?xt=urn:btih:" + meta.HexHash + "&dn=wire&x.pe=127.0.0.1:" + seed.Context.Port;
                DlTorrentRequest r = new DlTorrentRequest();
                r.Magnet = link;
                string id = EngAdd(e, r);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                DlItem it = e.Find(id);
                string side = Path.Combine(e.TorrentsDir, meta.HexHash + ".torrent");
                BtMeta saved = File.Exists(side) ? BtMeta.Parse(File.ReadAllBytes(side), out err) : null;
                T.Check("engine: magnet with x.pe ⇒ metadata from the peer, single file named from it, SHA-256 equal, .torrent saved",
                        seeding && same && it.FileName == "wire" && it.Url == link && it.Total == meta.TotalSize
                        && saved != null && saved.HexHash == meta.HexHash, EngState(e, id) + " " + info);

                string why;
                bool removed = e.Remove(id, true, out why);
                T.Check("engine: remove single-file torrent ⇒ only its file recycled",
                        removed && recycled.Count == 1 && recycled[0] == Path.Combine(dl, "wire") && !EngSideFiles(e, meta.HexHash),
                        why + " recycled: " + string.Join("; ", recycled.ToArray()));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        // ================================================================== //
        //  Пауза: файлы отпущены, снимок сохранён, продолжение не качает проверенное заново
        // ================================================================== //
        private static void EngPauseResume()
        {
            List<BtFxFile> files = SesFiles(321, 1500000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-pause");
            string dl = Fx.MakeDir(root, "dl");
            BtSession seed = null;
            DlEngine e = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                BtTorrent ts = WireAddSeed(seed, meta, files, Fx.MakeDir(root, "seed"));
                ts.UpLimit = 250000;
                e = EngEngine(Fx.MakeDir(root, "store"), EngSettings(dl), new FakeDlEnv(), null);
                e.Start();
                string id = EngAdd(e, EngFile(torrent));
                EngFeed(e, meta, seed);
                bool some = WireWaitFor(delegate { DlItem x = e.Find(id); return x.DoneBytes >= 400000; }, 15000);
                // Доступность пересчитывается раз в секунду.
                JVal stats = null;
                bool counted = WireWaitFor(delegate
                {
                    JVal card = e.TorrentJson(id);
                    stats = card == null ? null : card.Get("stats");
                    double avail;
                    return stats != null && double.TryParse(stats.GetStr("availability"), System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out avail) && avail >= 1 && stats.GetStr("seeds") == "1";
                }, 3000);
                T.Check("engine: card mid-download shows the connected seed (seeds 1, availability ≥ 1)", counted, stats == null ? "null" : Jsn.Write(stats));
                e.Pause(id);
                bool paused = EngWaitState(e, id, DlState.Paused, 10000);
                string locked = EngLocked(Path.Combine(dl, "wire"));
                int saved = EngResumePieces(e, meta);
                T.Check("engine: pause mid-way ⇒ Paused, torrent out of the session, files openable exclusively, resume snapshot saved",
                        some && paused && EngRunning(e, meta) == null && locked == null && saved >= 8 && saved < meta.PieceCount,
                        EngState(e, id) + " locked: " + locked + " saved " + saved + "/" + meta.PieceCount);

                ts.UpLimit = 0;
                e.Resume(id);
                BtTorrent t = EngFeed(e, meta, seed);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                long allowance = (meta.PieceCount - saved) * (long)meta.PieceLength;
                long downloaded = t == null ? -1 : t.Downloaded;
                T.Check("engine: resume after pause completes without re-downloading verified pieces",
                        seeding && same && downloaded >= 0 && downloaded <= allowance,
                        EngState(e, id) + " downloaded " + downloaded + " allowance " + allowance + " " + info);
            }
            finally
            {
                if (e != null) e.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        // ================================================================== //
        //  Падение процесса: Dispose не вызывается; новый движок на том же хранилище продолжает со снимка
        // ================================================================== //
        private static void EngCrashResume()
        {
            List<BtFxFile> files = SesFiles(331, 1500000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-crash");
            string dl = Fx.MakeDir(root, "dl");
            string store = Fx.MakeDir(root, "store");
            BtSession seed = null;
            DlEngine e = null, e2 = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                BtTorrent ts = WireAddSeed(seed, meta, files, Fx.MakeDir(root, "seed"));
                ts.UpLimit = 250000;
                e = EngEngine(store, EngSettings(dl), new FakeDlEnv(), null);
                e.BtResumeSaveSeconds = 1;
                e.Start();
                string id = EngAdd(e, EngFile(torrent));
                EngFeed(e, meta, seed);
                bool some = WireWaitFor(delegate { return EngResumePieces(e, meta) >= 8; }, 15000);

                // «Убит»: планировщик просто перестаёт работать, сокеты и файлы закрывает система (здесь — Dispose сессии).
                typeof(DlEngine).GetField("_disposed", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(e, true);
                BtSession dead = e.TorrentSession;
                if (dead != null) dead.Dispose();
                Thread.Sleep(600);
                int saved = EngResumePieces(e, meta);
                T.Check("engine: periodic resume snapshot exists before the crash, partial", some && saved >= 8 && saved < meta.PieceCount,
                        saved + "/" + meta.PieceCount);

                ts.UpLimit = 0;
                e2 = EngEngine(store, EngSettings(dl), new FakeDlEnv(), null);
                e2.Start();
                BtTorrent t = EngFeed(e2, meta, seed);
                bool seeding = EngWaitState(e2, id, DlState.Seeding, 20000);
                string info;
                bool same = WireSameFiles(meta, files, dl, out info);
                long allowance = (meta.PieceCount - saved) * (long)meta.PieceLength;
                long downloaded = t == null ? -1 : t.Downloaded;
                T.Check("engine: after a crash the reloaded store continues the torrent and completes without re-downloading the snapshot",
                        seeding && same && downloaded >= 0 && downloaded <= allowance,
                        EngState(e2, id) + " downloaded " + downloaded + " allowance " + allowance + " " + info);
            }
            finally
            {
                if (e2 != null) e2.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        // ================================================================== //
        //  Выбор файлов: пропущенный не пишется; выбрали позже — докачивается
        // ================================================================== //
        private static void EngFileSelection()
        {
            List<BtFxFile> files = SesFiles(341, 400000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-select");
            string dl = Fx.MakeDir(root, "dl");
            BtSession seed = null;
            DlEngine e = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                WireAddSeed(seed, meta, files, Fx.MakeDir(root, "seed"));
                e = EngEngine(Fx.MakeDir(root, "store"), EngSettings(dl), new FakeDlEnv(), null);
                e.Start();
                int skip = -1;
                int[] prio = new int[meta.Files.Count];
                for (int i = 0; i < prio.Length; i++)
                {
                    prio[i] = 1;
                    if (meta.Files[i].RelPath == "d\\b.bin") { prio[i] = 0; skip = i; }
                }
                DlTorrentRequest r = EngFile(torrent);
                r.Priorities = prio;
                string id = EngAdd(e, r);
                EngFeed(e, meta, seed);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);
                DlItem it = e.Find(id);
                string skipped = Path.Combine(Path.Combine(dl, "wire"), "d\\b.bin");
                T.Check("engine: a file set to «do not download» is not written; the rest completes and seeds",
                        skip >= 0 && seeding && !File.Exists(skipped) && it.Total == meta.TotalSize - meta.Files[skip].Length && it.DoneBytes == it.Total
                        && File.Exists(Path.Combine(Path.Combine(dl, "wire"), "a.bin")), EngState(e, id));

                string why;
                for (int i = 0; i < prio.Length; i++) prio[i] = 1;
                bool set = e.SetFilePriorities(id, prio, out why);
                string info = "";
                bool all = WireWaitFor(delegate
                {
                    DlItem x = e.Find(id);
                    return x.State == DlState.Seeding && x.DoneBytes == meta.TotalSize && WireSameFiles(meta, files, dl, out info);
                }, 20000);
                T.Check("engine: selecting the skipped file on a seeding torrent downloads it, SHA-256 equal", set && all,
                        why + " " + EngState(e, id) + " " + info + (all ? "" : " journal: " + EngJournal(e, id) + " card: " + Jsn.Write(e.TorrentJson(id))));
                T.Check("engine: the selection is stored with the record", new DlStore(Path.Combine(root, "store")).LoadAll()
                        .Exists(delegate(DlItem x) { return x.Id == id && x.FilePriorities != null && x.FilePriorities.Length == prio.Length && x.FilePriorities[skip] == 1; }));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        // ================================================================== //
        //  Данные уже на диске + предел рейтинга; удаление не уносит чужой файл из папки раздачи
        // ================================================================== //
        private static void EngRatioLimit()
        {
            List<BtFxFile> files = SesFiles(351, 300000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-ratio");
            string dl = Fx.MakeDir(root, "dl");
            WritePlaced(meta, files, dl, "wire");
            string foreign = Path.Combine(Path.Combine(dl, "wire"), "notes.txt");
            File.WriteAllText(foreign, "mine");
            List<DlNotice> notices = new List<DlNotice>();
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); return null; };
            BtSession leecher = null;
            DlEngine e = null;
            try
            {
                DlSettings s = EngSettings(dl);
                s.BtRatioPercent = 100;
                e = EngEngine(Fx.MakeDir(root, "store"), s, new FakeDlEnv(), notices);
                e.Start();
                DlTorrentRequest r = EngFile(torrent);
                r.UseExisting = true;
                string id = EngAdd(e, r);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 15000);
                DlItem it = e.Find(id);
                T.Check("engine: «data already on disk» keeps the name, checks the data and seeds without downloading",
                        seeding && it.FileName == "wire" && it.DoneBytes == meta.TotalSize && EngNotices(notices, id, DlNoticeKind.Completed) == 1, EngState(e, id));

                leecher = WireSession(BtEncryption.Prefer);
                string ldir = Fx.MakeDir(root, "leech");
                BtTorrent lt = WireAdd(leecher, meta, ldir, null);
                BtSession ses = e.TorrentSession;
                if (ses != null) lt.AddPeer(new BtEndpoint(IPAddress.Loopback, ses.Context.Port));
                bool completed = EngWaitState(e, id, DlState.Completed, 20000);
                string info;
                bool same = WireWaitFor(delegate { return lt.State == BtTorrentState.Seeding; }, 5000) && WireSameFiles(meta, files, ldir, out info);
                T.Check("engine: inbound peer downloads everything; ratio 100 % reached ⇒ Completed, out of the session",
                        completed && same && e.Find(id).Uploaded >= meta.TotalSize && EngRunning(e, meta) == null,
                        EngState(e, id) + " uploaded " + e.Find(id).Uploaded);

                string why;
                bool removed = e.Remove(id, true, out why);
                string rootDir = Path.Combine(dl, "wire");
                T.Check("engine: remove with a foreign file in the torrent folder ⇒ only the torrent's files recycled, folder and foreign file stay",
                        removed && recycled.Count == 2 && !recycled.Contains(rootDir) && !recycled.Contains(foreign)
                        && recycled.Contains(Path.Combine(rootDir, "a.bin")) && recycled.Contains(Path.Combine(rootDir, "d\\b.bin")) && e.Find(id) == null,
                        why + " recycled: " + string.Join("; ", recycled.ToArray()));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (leecher != null) leecher.Dispose();
            }
        }

        // ================================================================== //
        //  Отказы и сохранение записи (без сети: движок не запущен)
        // ================================================================== //
        private static void EngRefusals()
        {
            List<BtFxFile> files = SesFiles(361, 100000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-refuse");
            string dl = Fx.MakeDir(root, "dl");
            string store = Fx.MakeDir(root, "store");
            Fx.MakeDir(dl, "wire");
            DlEngine e = EngEngine(store, EngSettings(dl), new FakeDlEnv(), null);
            try
            {
                DlTorrentRequest r = EngFile(torrent);
                r.StartPaused = true;
                r.Sequential = true;
                string id = EngAdd(e, r);
                DlItem it = e.Find(id);
                T.Check("engine: a folder with the torrent's name already exists ⇒ the new torrent gets «wire (1)», starts paused",
                        it.FileName == "wire (1)" && it.State == DlState.Paused && it.IsTorrent && it.InfoHash == meta.HexHash, EngState(e, id));

                string dup, error;
                T.Check("engine: the same .torrent again ⇒ refused as a duplicate of the first",
                        e.AddTorrent(EngFile(torrent), out dup, out error) == null && dup == id && !string.IsNullOrEmpty(error));
                DlTorrentRequest m = new DlTorrentRequest();
                m.Magnet = "magnet:?xt=urn:btih:" + meta.HexHash.ToUpperInvariant();
                T.Check("engine: a magnet with the same info-hash ⇒ refused as a duplicate", e.AddTorrent(m, out dup, out error) == null && dup == id, error);
                m.Magnet = "magnet:?xt=urn:btih:zz";
                T.Check("engine: an unreadable magnet ⇒ refused with a reason", e.AddTorrent(m, out dup, out error) == null && dup == null && !string.IsNullOrEmpty(error));
                DlTorrentRequest bad = EngFile(torrent);
                bad.Priorities = new int[] { 1 };
                T.Check("engine: a file selection of the wrong length ⇒ refused", e.AddTorrent(bad, out dup, out error) == null && !string.IsNullOrEmpty(error));

                string why;
                T.Check("engine: «download again» is refused for a torrent", !e.Restart(id, out why) && !string.IsNullOrEmpty(why));
                T.Check("engine: move/copy is refused for a torrent", !e.Relocate(id, Fx.MakeDir(root, "elsewhere"), false, out why) && !string.IsNullOrEmpty(why));
                T.Check("engine: a file selection of the wrong length is refused on the record", !e.SetFilePriorities(id, new int[] { 0 }, out why) && !string.IsNullOrEmpty(why));

                DlItem loaded = new DlStore(store).LoadAll().Find(delegate(DlItem x) { return x.Id == id; });
                T.Check("engine: the torrent record round-trips through the store (kind, hash, sequential, name)",
                        loaded != null && loaded.IsTorrent && loaded.InfoHash == meta.HexHash && loaded.Sequential && loaded.FileName == "wire (1)");
            }
            finally { e.Dispose(); }
        }

        // ================================================================== //
        //  Команды канала процесса загрузок — тот путь, которым ходит окно (без сети: торренты на паузе)
        // ================================================================== //
        private static void EngPipe()
        {
            List<BtFxFile> files = SesFiles(371, 100000);
            byte[] torrent = BtFx.Build("wire", files, 32768, 1, false, null);
            string err;
            BtMeta meta = BtMeta.Parse(torrent, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-pipe");
            string dl = Fx.MakeDir(root, "dl");
            string name = "SysDeck.dl.bttest-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); return null; };
            DlEngine e = EngEngine(Fx.MakeDir(root, "store"), EngSettings(dl), new FakeDlEnv(), null);
            try
            {
                e.Start();
                DlCommands commands = new DlCommands(e, delegate { }, null);
                using (DlPipeServer server = new DlPipeServer(name, commands.Handle, DlIpc.IsOwnImage))
                {
                    server.Start();
                    JVal add = DlClient.Command("addTorrent");
                    add.Set("data", JVal.NewStr(Convert.ToBase64String(torrent)));
                    add.Set("paused", DlJson.B(true));
                    add.Set("sequential", DlJson.B(true));
                    JVal prio = JVal.NewArr();
                    for (int i = 0; i < meta.Files.Count; i++) prio.V.Add(DlJson.N(i == 0 ? 2 : 0));
                    add.Set("priorities", prio);
                    JVal added = DlClient.Call(name, add, 3000);
                    string id = DlJson.Str(added, "id", "");
                    DlItem it = e.Find(id);
                    T.Check("pipe: addTorrent from base64 ⇒ paused torrent with the file selection and sequential order",
                            it != null && it.IsTorrent && it.State == DlState.Paused && it.Sequential && it.FilePriorities != null
                            && it.FilePriorities.Length == meta.Files.Count && it.FilePriorities[0] == 2 && it.FilePriorities[1] == 0,
                            added == null ? "null" : Jsn.Write(added));

                    JVal get = DlClient.Command("torrent");
                    get.Set("id", JVal.NewStr(id));
                    JVal card = DlClient.Call(name, get, 3000);
                    JVal t = card == null ? null : card.Get("torrent");
                    JVal cardFiles = t == null ? null : t.Get("files");
                    T.Check("pipe: torrent card of a stopped torrent — name, hash, files with priorities, not running",
                            t != null && t.GetStr("hash") == meta.HexHash && t.GetStr("name") == "wire" && !t.Get("running").B
                            && cardFiles != null && cardFiles.V.Count == 2 && cardFiles.V[0].GetStr("priority") == "2" && cardFiles.V[1].GetStr("priority") == "0",
                            card == null ? "null" : Jsn.Write(card));

                    // Страница: карточка из ответа и из файла торрента (процесс не запущен) одинаковы; смена приоритета одного файла
                    // даёт массив для setFilePriorities, который канал принимает.
                    DlTorrentCard live = DlTorrentCard.FromJson(t);
                    DlTorrentCard offline = DlTorrentCard.FromMeta(meta, it.FilePriorities);
                    bool same = live != null && offline != null && live.Files.Count == 2 && offline.Files.Count == 2 && live.FileCount == offline.FileCount;
                    for (int i = 0; same && i < live.Files.Count; i++)
                        same = live.Files[i].Index == offline.Files[i].Index && live.Files[i].Path == offline.Files[i].Path
                               && live.Files[i].Size == offline.Files[i].Size && live.Files[i].Priority == offline.Files[i].Priority && offline.Files[i].Done == -1;
                    int[] chosen = same ? live.PrioritiesWith(new[] { live.Files[1].Index }, 2) : null;
                    T.Check("pipe → page: the card from the answer matches the card from the .torrent file; changing one file keeps the other",
                            same && offline.PiecesHave() == -1 && chosen.Length == meta.Files.Count && chosen[live.Files[0].Index] == 2 && chosen[live.Files[1].Index] == 2,
                            t == null ? "null" : Jsn.Write(t));

                    string file = Path.Combine(root, "same.torrent");
                    File.WriteAllBytes(file, torrent);
                    JVal byFile = DlClient.Command("addTorrent");
                    byFile.Set("file", JVal.NewStr(file));
                    JVal dup = DlClient.Call(name, byFile, 3000);
                    T.Check("pipe: addTorrent by file path of the same torrent ⇒ refused, duplicateOf = first id",
                            dup != null && !DlJson.Bool(dup, "ok", true) && DlJson.Str(dup, "duplicateOf", "") == id, dup == null ? "null" : Jsn.Write(dup));
                    byFile.Set("file", JVal.NewStr(Path.Combine(root, "missing.torrent")));
                    JVal missing = DlClient.Call(name, byFile, 3000);
                    T.Check("pipe: addTorrent with a missing file ⇒ error text", missing != null && !DlJson.Bool(missing, "ok", true) && DlJson.Str(missing, "error", "").Length > 0);

                    JVal setPrio = DlClient.Command("setFilePriorities");
                    setPrio.Set("id", JVal.NewStr(id));
                    JVal one = JVal.NewArr();
                    one.V.Add(DlJson.N(1));
                    setPrio.Set("priorities", one);
                    JVal badPrio = DlClient.Call(name, setPrio, 3000);
                    JVal both = JVal.NewArr();
                    both.V.Add(DlJson.N(1));
                    both.V.Add(DlJson.N(1));
                    setPrio.Set("priorities", both);
                    JVal goodPrio = DlClient.Call(name, setPrio, 3000);
                    T.Check("pipe: setFilePriorities — wrong length refused with a reason, right length stored",
                            badPrio != null && !DlJson.Bool(badPrio, "ok", true) && DlJson.Str(badPrio, "error", "").Length > 0
                            && goodPrio != null && DlJson.Bool(goodPrio, "ok", false) && e.Find(id).FilePriorities[1] == 1);

                    JVal seq = DlClient.Command("setSequential");
                    seq.Set("id", JVal.NewStr(id));
                    seq.Set("on", DlJson.B(false));
                    JVal seqResp = DlClient.Call(name, seq, 3000);
                    JVal restart = DlClient.Command("restart");
                    restart.Set("id", JVal.NewStr(id));
                    JVal restartResp = DlClient.Call(name, restart, 3000);
                    T.Check("pipe: setSequential off applies; restart of a torrent is refused with a reason",
                            seqResp != null && DlJson.Bool(seqResp, "ok", false) && !e.Find(id).Sequential
                            && restartResp != null && !DlJson.Bool(restartResp, "ok", true) && DlJson.Str(restartResp, "error", "").Length > 0);

                    JVal remove = DlClient.Command("remove");
                    remove.Set("id", JVal.NewStr(id));
                    remove.Set("recycle", DlJson.B(true));
                    JVal removed = DlClient.Call(name, remove, 25000);
                    T.Check("pipe: remove of a torrent that never downloaded ⇒ ok, nothing to recycle, record gone",
                            removed != null && DlJson.Bool(removed, "ok", false) && recycled.Count == 0 && e.Find(id) == null && !EngSideFiles(e, meta.HexHash),
                            (removed == null ? "null" : Jsn.Write(removed)) + " recycled: " + string.Join("; ", recycled.ToArray()));
                }
            }
            finally { e.Dispose(); }
        }

        // ================================================================== //
        //  Папка наблюдения: без диалога, один раз, «убрать .torrent» — только в Корзину (движок не запущен: сети нет)
        // ================================================================== //
        private static void EngWatchFolder()
        {
            byte[] first = BtFx.Build("watched", SesFiles(381, 60000), 32768, 1, false, null);
            byte[] second = BtFx.Build("watched-2", SesFiles(382, 60000), 32768, 1, false, null);
            byte[] third = BtFx.Build("watched-3", SesFiles(383, 60000), 32768, 1, false, null);
            string err;
            BtMeta m1 = BtMeta.Parse(first, out err), m2 = BtMeta.Parse(second, out err), m3 = BtMeta.Parse(third, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-watch");
            string watch = Fx.MakeDir(root, "watch");
            string store = Fx.MakeDir(root, "store");
            DlSettings s = EngSettings(Fx.MakeDir(root, "dl"));
            s.BtWatchFolder = watch;
            s.BtRecycleTorrentFile = false;   // по умолчанию файл убирается; здесь проверяется обратный случай
            List<string> recycled = new List<string>();
            string recycleError = null;
            DlFiles.Recycler = delegate(string p)
            {
                lock (recycled) recycled.Add(p);
                if (recycleError != null) return recycleError;
                File.Delete(p);
                return null;
            };

            string a = Path.Combine(watch, "a.torrent");
            File.WriteAllBytes(a, first);
            string page = Path.Combine(watch, "page.torrent");
            File.WriteAllText(page, "<html><body>sign in</body></html>");
            string deep = Path.Combine(Fx.MakeDir(watch, "sub"), "deep.torrent");
            File.WriteAllBytes(deep, third);
            string part = Path.Combine(watch, "c.torrent.part");
            File.WriteAllBytes(part, third);
            DlEngine e = EngEngine(store, s, new FakeDlEnv(), null);
            DlEngine e2 = null;
            // Папку наблюдения человек завёл затем, чтобы его не спрашивали: вопрос о папке здесь не задаётся никогда.
            List<string> watchAsked = new List<string>();
            e.FolderAskNeeded = delegate { return true; };
            e.FolderAsk = delegate(string tid, string n) { lock (watchAsked) watchAsked.Add(n); };
            try
            {
                e.BtWatchSeconds = 0;
                e.BtWatchSettleSeconds = 60;
                e.BtIntake();
                T.Check("watch: a .torrent written just now waits for the next pass", e.Ids().Count == 0, e.Ids().Count + " items");

                EngAge(-10, a, page, deep, part);
                e.BtIntake();
                DlItem it = EngByHash(e, m1.HexHash);
                T.Check("watch: once settled, the top-level .torrent is added from «watch» without ever asking where to save it; a page named .torrent, a subfolder and .torrent.part are not added",
                        it != null && it.Source == "watch" && it.State != DlState.Paused && watchAsked.Count == 0
                        && e.Ids().Count == 1 && File.Exists(a) && recycled.Count == 0, e.Ids().Count + " items, asked: " + watchAsked.Count);
                T.Check("watch → page: the source reads «watch folder», not «manual», and the source filter tells them apart",
                        it != null && DlView.SourceTitle(it.Source) == Tr.S("папка наблюдения", "watch folder")
                        && DlView.Matches(it, DlStateFilter.All, "", "watch") && !DlView.Matches(it, DlStateFilter.All, "", "manual"),
                        it == null ? "no item" : DlView.SourceTitle(it.Source));
                e.BtIntake();
                T.Check("watch: the next pass adds nothing again", e.Ids().Count == 1, e.Ids().Count + " items");

                string why;
                if (it != null) e.Remove(it.Id, false, out why);
                e.Dispose();
                e = null;
                e2 = EngEngine(store, s, new FakeDlEnv(), null);
                e2.BtWatchSeconds = 0;
                e2.BtWatchSettleSeconds = 0;
                e2.BtIntake();
                T.Check("watch: a torrent removed from the list does not come back from the folder after a restart",
                        e2.Ids().Count == 0 && File.Exists(Path.Combine(e2.TorrentsDir, "watch-seen.txt")), e2.Ids().Count + " items");

                File.WriteAllBytes(a, second);
                EngAge(-5, a);
                e2.BtIntake();
                T.Check("watch: another torrent saved under the same file name is new and is added", EngByHash(e2, m2.HexHash) != null && e2.Ids().Count == 1,
                        e2.Ids().Count + " items");

                DlSettings recycle = s.Clone();
                recycle.BtRecycleTorrentFile = true;
                e2.UpdateSettings(recycle);
                string b = Path.Combine(watch, "b.torrent");
                File.WriteAllBytes(b, third);
                EngAge(-10, b);
                e2.BtIntake();
                T.Check("watch: with «remove .torrent» on, the added file goes to the Recycle Bin",
                        EngByHash(e2, m3.HexHash) != null && recycled.Count == 1 && recycled[0] == b && !File.Exists(b), string.Join("; ", recycled.ToArray()));

                string copy = Path.Combine(watch, "copy.torrent");
                File.WriteAllBytes(copy, second);
                EngAge(-10, copy);
                recycleError = "locked";
                e2.BtIntake();
                e2.BtIntake();
                T.Check("watch: a copy of a listed torrent adds nothing but is still recycled; a refused recycle is not retried on every pass",
                        e2.Ids().Count == 2 && recycled.Count == 2 && recycled[1] == copy && File.Exists(copy), string.Join("; ", recycled.ToArray()));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (e2 != null) e2.Dispose();
            }
        }

        // ================================================================== //
        //  .torrent, скачанный обычной загрузкой: становится торрентом (сессия на петле, пиров нет)
        // ================================================================== //
        private static void EngDownloadedTorrent()
        {
            byte[] t1 = BtFx.Build("fetched", SesFiles(391, 60000), 32768, 1, false, null);
            byte[] t2 = BtFx.Build("fetched-2", SesFiles(392, 60000), 32768, 1, false, null);
            byte[] t3 = BtFx.Build("fetched-3", SesFiles(393, 60000), 32768, 1, false, null);
            string err;
            BtMeta m1 = BtMeta.Parse(t1, out err), m2 = BtMeta.Parse(t2, out err), m3 = BtMeta.Parse(t3, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-eng-chain");
            string dl = Fx.MakeDir(root, "dl");
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p) { lock (recycled) recycled.Add(p); File.Delete(p); return null; };
            DlEngine e = null;
            using (DlTestServer srv = new DlTestServer())
            {
                try
                {
                    EngServe(srv, "fetched.torrent", t1);
                    EngServe(srv, "fetched-2.torrent", t2);
                    EngServe(srv, "fetched-3.torrent", t3);
                    EngServe(srv, "login.torrent", Encoding.UTF8.GetBytes("<html><body>sign in</body></html>"));
                    T.Check("downloaded .torrent: out of the box the .torrent file is not kept — it is a delivery slip, the work is the payload",
                            new DlSettings().BtRecycleTorrentFile, "default off");
                    DlSettings keep = EngSettings(dl);
                    keep.BtRecycleTorrentFile = false;   // первый заход — редкий случай «файл .torrent оставить»
                    e = EngEngine(Fx.MakeDir(root, "store"), keep, new FakeDlEnv(), null);
                    e.Start();

                    string h1 = EngHttp(e, srv.Url("fetched.torrent"), "chrome");
                    DlItem t = null;
                    WireWaitFor(delegate { t = EngByHash(e, m1.HexHash); return t != null; }, 20000);
                    DlItem http = e.Find(h1);
                    string journal = EngJournal(e, h1);
                    T.Check("downloaded .torrent: a finished http download named .torrent becomes a torrent with its source; the http record stays with a journal line",
                            t != null && t.Source == "chrome" && http != null && http.State == DlState.Completed && File.Exists(http.TargetPath) && recycled.Count == 0
                            && (journal.Contains("добавлен торрент: fetched") || journal.Contains("torrent added: fetched")), EngState(e, h1) + " | " + journal);
                    bool downloading = t != null && EngWaitState(e, t.Id, DlState.Active, 10000);
                    string status = EngStatusLine(e);
                    T.Check("downloaded .torrent → page: a torrent downloading without peers counts in «downloading» and is not «nothing to do»",
                            downloading && status.Contains(Tr.S("качаются: 1", "downloading: 1")) && !status.Contains(Tr.S("работы нет", "nothing to do")), status);

                    DlSettings recycle = EngSettings(dl);
                    recycle.BtRecycleTorrentFile = true;
                    e.UpdateSettings(recycle);
                    string h2 = EngHttp(e, srv.Url("fetched-2.torrent"), "manual");
                    bool moved = WireWaitFor(delegate { return EngByHash(e, m2.HexHash) != null && e.Find(h2) == null; }, 20000);
                    T.Check("downloaded .torrent: with «remove .torrent» on, the file goes to the Recycle Bin and the http record leaves the list",
                            moved && recycled.Count == 1 && recycled[0].EndsWith(@"\fetched-2.torrent", StringComparison.OrdinalIgnoreCase) && !File.Exists(recycled[0]),
                            EngState(e, h2) + " recycled: " + string.Join("; ", recycled.ToArray()));

                    string h3 = EngHttp(e, srv.Url("login.torrent"), "manual");
                    bool done = EngWaitState(e, h3, DlState.Completed, 20000);
                    Thread.Sleep(1500);
                    string journal3 = EngJournal(e, h3);
                    T.Check("downloaded .torrent: a page saved as login.torrent stays an ordinary finished download — no torrent, no recycle, no complaint",
                            done && e.Find(h3) != null && e.Ids().Count == 4 && recycled.Count == 1
                            && !journal3.Contains("не добавлен торрентом") && !journal3.Contains("not added as a torrent"), EngState(e, h3) + " | " + journal3);

                    // Торрент заводится мимо окна добавления: вопрос «куда скачивать» задаёт движок через хозяина,
                    // иначе перехваченная у браузера раздача уезжала в папку по умолчанию, ничего не спросив.
                    DlSettings asking = EngSettings(dl);
                    asking.BtRecycleTorrentFile = true;
                    asking.AskFolder = true;
                    e.UpdateSettings(asking);
                    DlEngine eng = e;
                    List<string> asked = new List<string>();
                    eng.FolderAskNeeded = delegate(string n) { return DlFolderAsk.Needed(eng.Settings, null, n); };
                    eng.FolderAsk = delegate(string tid, string n)
                    {
                        eng.SetWaitReason(tid, Tr.S("ждёт выбора папки", "waiting for a folder"));
                        lock (asked) asked.Add(n);
                    };
                    string h4 = EngHttp(e, srv.Url("fetched-3.torrent"), "chrome");
                    DlItem t3it = null;
                    WireWaitFor(delegate { t3it = EngByHash(e, m3.HexHash); return t3it != null; }, 20000);
                    Thread.Sleep(1500);
                    if (t3it != null) t3it = e.Find(t3it.Id);
                    T.Check("downloaded .torrent: with «ask where to save» on, the torrent waits paused with the payload name as the question, the .torrent file itself is gone from disk and from the list, and not a byte of the payload is fetched",
                            t3it != null && t3it.State == DlState.Paused && t3it.DoneBytes == 0 && e.Find(h4) == null
                            && recycled.Count == 2 && recycled[1].EndsWith(@"\fetched-3.torrent", StringComparison.OrdinalIgnoreCase) && !File.Exists(recycled[1])
                            && asked.Count == 1 && asked[0] == m3.Name && t3it.WaitReason.Length > 0,
                            t3it == null ? "no torrent" : t3it.State + " done=" + t3it.DoneBytes + " asked=" + string.Join("; ", asked.ToArray()));

                    // Отказ: запись завёл движок, а не человек — она уходит целиком, чтобы повторный щелчок
                    // на трекере снова скачал .torrent и снова спросил, а не упёрся в «уже в списке».
                    string dropped = t3it == null ? null : t3it.Id;
                    if (dropped != null) DlFolderAsk.Apply(e, dropped, null, true);
                    T.Check("downloaded .torrent → «Отмена»: the torrent nobody asked for leaves the list instead of standing in it forever",
                            dropped != null && e.Find(dropped) == null && EngByHash(e, m3.HexHash) == null, dropped == null ? "no torrent" : EngState(e, dropped));

                    EngHttp(e, srv.Url("fetched-3.torrent"), "chrome");
                    DlItem again = null;
                    WireWaitFor(delegate { again = EngByHash(e, m3.HexHash); return again != null; }, 20000);
                    Thread.Sleep(1500);
                    if (again != null) again = e.Find(again.Id);
                    T.Check("downloaded .torrent → the same link once more: it is asked about again from scratch and waits again",
                            again != null && again.State == DlState.Paused && asked.Count == 2, again == null ? "no torrent" : again.State + " asked=" + asked.Count);

                    string chosen = Fx.MakeDir(root, "chosen"), why4;
                    bool set = again != null && e.SetFolderBeforeStart(again.Id, chosen, out why4);
                    if (set) e.Resume(again.Id);
                    T.Check("downloaded .torrent → answer: the chosen folder lands on the standing torrent and only then does it start",
                            set && e.Find(again.Id).Folder == chosen && EngWaitState(e, again.Id, DlState.Active, 10000),
                            again == null ? "no torrent" : EngState(e, again.Id) + " folder=" + e.Find(again.Id).Folder);
                }
                finally { if (e != null) e.Dispose(); }
            }
        }

        // Строка над списком так, как её строит страница: команда list процесса → снимок → текст.
        private static string EngStatusLine(DlEngine e)
        {
            DlSnapshot s = DlSnapshot.FromList(new DlCommands(e, delegate { }, null).Handle(DlClient.Command("list")));
            return s == null ? "no snapshot" : DlView.ProcessStatusText(s);
        }

        private static void EngAge(int minutes, params string[] files)
        {
            foreach (string f in files) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(minutes));
        }

        private static DlItem EngByHash(DlEngine e, string hash)
        {
            foreach (string id in e.Ids())
            {
                DlItem it = e.Find(id);
                if (it != null && it.IsTorrent && it.InfoHash == hash) return it;
            }
            return null;
        }

        private static void EngServe(DlTestServer srv, string key, byte[] body)
        {
            DlTestServer.Res r = srv.Add(key, new DlTestServer.Res());
            r.Body = body;
            r.Size = body.Length;
            r.ContentType = "application/x-bittorrent";
        }

        private static string EngHttp(DlEngine e, string url, string source)
        {
            DlAddRequest r = new DlAddRequest();
            r.Url = url;
            r.Source = source;
            string dup, err;
            string id = e.Add(r, out dup, out err);
            if (id == null) throw new InvalidOperationException("Add: " + err);
            return id;
        }
    }
}
