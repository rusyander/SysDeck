// Windows Process Cleaner — область «torrent», часть «Обновить раздачу»: план перехода данных прежней версии торрента
// в раскладку новой (торренты собраны сборщиком и разобраны настоящим BtMeta), файловые шаги на настоящей файловой
// системе и проверка результата хешем новой версии через BtStorage — тем же кодом, что проверяет данные при старте.
// Подменена только Корзина: делегат переносит файл в папку фикстуры, как настоящая Корзина убирает его с места.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunUpdate()
        {
            Func<string, string> savedRecycler = DlFiles.Recycler;
            try
            {
                UpdPlanCases(1);
                UpdPlanCases(3);
                UpdPlanRefusals();
                UpdDiskCases(1);
                UpdDiskCases(3);
                UpdTopicCases();
                UpdRutrackerCases();
                WireRun("update engine rutracker", UpdEngineRutracker);
                WireRun("update engine crash", UpdEngineCrash);
            }
            finally { DlFiles.Recycler = savedRecycler; }
        }

        // ================================================================== //
        //  Ссылка на тему из .torrent
        // ================================================================== //
        private static void UpdTopicCases()
        {
            string[][] cases =
            {
                new[] { "https://rutracker.org/forum/viewtopic.php?t=6489937", "https://rutracker.org/forum/viewtopic.php?t=6489937" },
                new[] { "http://www.rutracker.net/forum/viewtopic.php?t=0123&start=30#p1", "https://rutracker.org/forum/viewtopic.php?t=123" },
                new[] { "Раздача с https://rutracker.nl/forum/viewtopic.php?t=42 .", "https://rutracker.org/forum/viewtopic.php?t=42" },
                new[] { "https://nnm-club.me/forum/viewtopic.php?p=1&t=1650000", "https://nnmclub.to/forum/viewtopic.php?t=1650000" },
                new[] { "https://kinozal.tv/details.php?id=2000123", "https://kinozal.tv/details.php?id=2000123" },
                new[] { "Torrent downloaded from http://rutor.is/torrent/987654/some-name", "https://rutor.info/torrent/987654" },
                new[] { "https://tracker.example/files/view/12345", "https://tracker.example/files/view/12345" },
                new[] { "https://example.org/topic?t=1", "" },
                new[] { "https://rutor.info", "" },
                new[] { "https://rutracker.org/forum/index.php", "" },
                new[] { "made with qBittorrent v5.2.3", "" },
                new[] { "", "" }
            };
            List<string> bad = new List<string>();
            foreach (string[] c in cases)
                if (BtTopic.Normalize(c[0]) != c[1]) bad.Add("'" + c[0] + "' -> '" + BtTopic.Normalize(c[0]) + "'");
            T.Check("update topic: links to a release page are canonical per site; site roots, short ids and plain text are not topics",
                    bad.Count == 0, string.Join("; ", bad.ToArray()));
            T.Check("update topic: site keys and the rutracker topic id",
                    BtTopic.SiteKey("https://rutracker.org/forum/viewtopic.php?t=42") == "rutracker" && BtTopic.RutrackerId("https://rutracker.org/forum/viewtopic.php?t=42") == "42"
                    && BtTopic.RutrackerId("https://kinozal.tv/details.php?id=2000123") == null && BtTopic.SiteKey("https://tracker.example/files/view/12345") == "");

            // publisher-url читается из .torrent; ссылка сборщика тестов (example.org, t=1) темой не считается.
            string err;
            byte[] plain = BtFx.Build("rel", UpdOldFiles(), 16384, 1, false, null);
            BVal root = Bencode.Decode(plain, out err);
            root.Set("comment", BVal.Str("Torrent from rutracker.org"));
            root.Set("publisher-url", BVal.Str("https://rutracker.org/forum/viewtopic.php?t=5911283"));
            BtMeta withPublisher = BtMeta.Parse(Bencode.Encode(root), out err);
            BtMeta fixture = BtMeta.Parse(plain, out err);
            T.Check("update topic: publisher-url of a rutracker .torrent gives the topic, the fixture's example comment gives none, info-hash unchanged",
                    withPublisher != null && BtTopic.FromMeta(withPublisher) == "https://rutracker.org/forum/viewtopic.php?t=5911283"
                    && BtTopic.FromMeta(fixture) == "" && withPublisher.HexHash == fixture.HexHash);
        }

        // ================================================================== //
        //  rutracker: API по темам выключен → выгрузки форумов, кеш, проход в простое
        // ================================================================== //
        private static string RtDump(params string[] topicHash)
        {
            List<string> parts = new List<string>();
            for (int i = 0; i + 1 < topicHash.Length; i += 2)
                parts.Add("\"" + topicHash[i] + "\":[2,5,1571500824,28158407168,2,[30166638,9816225],1789352462,\"" + topicHash[i + 1] + "\",29524634,4]");
            return "{\"format\":{\"topic_id\":[\"tor_status\",\"seeders\",\"reg_time\",\"tor_size_bytes\",\"keeping_priority\",\"keepers\",\"seeder_last_seen\",\"info_hash\",\"topic_poster\",\"leechers\"]},"
                   + "\"update_time\":1789373175,\"result\":{" + string.Join(",", parts.ToArray()) + "}}";
        }

        private static void UpdRutrackerCases()
        {
            string hA = new string('A', 40), hC = "c" + new string('C', 39), hD = new string('D', 40), hE = new string('E', 40), hF = new string('f', 40);
            string dir = Fx.MakeDir(Fx.Root, "bt-rutracker");
            string cache = Path.Combine(dir, "rutracker.json");
            using (RtFakeApi api = new RtFakeApi())
            {
                api.Set("get_tor_hash?by=topic_id&val=100,300,999", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                api.Set("static/forum_size", "{\"format\":{\"forum_id\":[\"tor_count\",\"tor_size_bytes\"]},\"result\":{\"11\":[5,100],\"22\":[10,100],\"33\":[1,1],\"44\":[0,0]}}", null);
                api.Set("static/pvc/f/22", RtDump("100", hA, "200", new string('B', 40)), "\"t22a\"");
                api.Set("static/pvc/f/11", RtDump("300", hC), "\"t11a\"");
                api.Set("static/pvc/f/33", RtDump(), "\"t33a\"");

                string err;
                BtRutracker rt = new BtRutracker(cache);
                rt.BaseUrl = api.Base;
                rt.PauseMs = 0;
                Dictionary<string, string> r = rt.Check(new[] { "100", "300", "999" }, out err);
                T.Check("rutracker: per-topic API disabled ⇒ forums swept largest first, both topics found, a topic in no forum is absent",
                        err == null && r.Count == 3 && r["100"] == hA && r["300"] == hC.ToUpperInvariant() && r["999"] == ""
                        && api.Paths() == "get_tor_hash?by=topic_id&val=100,300,999|static/forum_size|static/pvc/f/22|static/pvc/f/11|static/pvc/f/33",
                        err + " " + api.Paths());
                T.Check("rutracker: every request names this client (never an empty or Python agent) and the gzip reply is read",
                        api.AllAgents("WindowsProcessCleaner") && api.GzipServed > 0, api.AgentsText());

                // Новый экземпляр — кеш с диска: форумы известны, выгрузки не менялись (304), отсутствующая тема не ищется.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=100,300", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                rt = new BtRutracker(cache);
                rt.BaseUrl = api.Base;
                rt.PauseMs = 0;
                r = rt.Check(new[] { "100", "300", "999" }, out err);
                T.Check("rutracker: second check = one conditional request per known forum, 304 ⇒ cached hashes, no sweep, absent topic not searched again",
                        err == null && r["100"] == hA && r["300"] == hC.ToUpperInvariant() && r["999"] == ""
                        && api.Paths() == "get_tor_hash?by=topic_id&val=100,300|static/pvc/f/22 inm=\"t22a\"|static/pvc/f/11 inm=\"t11a\"" && api.NotModified == 2,
                        err + " " + api.Paths());

                // Раздачу обновили: выгрузка форума другая — новый хеш.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=300", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                api.Set("static/pvc/f/11", RtDump("300", hD), "\"t11b\"");
                r = rt.Check(new[] { "300" }, out err);
                T.Check("rutracker: a changed forum dump gives the release's new info-hash", err == null && r["300"] == hD, err + " " + api.Paths());

                // Тему перенесли в другой форум: в прежней выгрузке её нет — проход находит её заново.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=100", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                api.Set("static/pvc/f/22", RtDump("200", new string('B', 40)), "\"t22b\"");
                api.Set("static/pvc/f/33", RtDump("100", hE), "\"t33b\"");
                r = rt.Check(new[] { "100" }, out err);
                T.Check("rutracker: a topic moved to another forum is found again by the sweep",
                        err == null && r["100"] == hE && api.Paths().EndsWith("|static/forum_size|static/pvc/f/22|static/pvc/f/11|static/pvc/f/33", StringComparison.Ordinal),
                        err + " " + api.Paths());

                // ПК не простаивает: новая тема ждёт, проход не начинается.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=555", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                int calls = 0;
                rt.MaySweep = delegate { return false; };
                r = rt.Check(new[] { "555" }, out err);
                T.Check("rutracker: no idle PC ⇒ an unknown topic stays unanswered and no forum is downloaded",
                        err == null && !r.ContainsKey("555") && api.Paths() == "get_tor_hash?by=topic_id&val=555", err + " " + api.Paths());

                // Простой кончился посреди прохода — следующая проверка продолжает с того же форума.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=555", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                rt.MaySweep = delegate { return ++calls <= 2; };
                r = rt.Check(new[] { "555" }, out err);
                string firstPart = api.Paths();
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=555", "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}", null);
                rt.MaySweep = delegate { return true; };
                r = rt.Check(new[] { "555" }, out err);
                T.Check("rutracker: an interrupted sweep resumes at the next forum, then reports the topic absent",
                        firstPart == "get_tor_hash?by=topic_id&val=555|static/forum_size|static/pvc/f/22"
                        && api.Paths() == "get_tor_hash?by=topic_id&val=555|static/pvc/f/11|static/pvc/f/33" && r["555"] == "",
                        firstPart + " / " + api.Paths());

                // API по темам снова работает — выгрузки не нужны.
                api.Clear();
                api.Set("get_tor_hash?by=topic_id&val=777", "{\"result\":{\"777\":\"" + hF + "\"}}", null);
                r = rt.Check(new[] { "777" }, out err);
                T.Check("rutracker: when the per-topic API answers again it is used alone", err == null && r["777"] == hF.ToUpperInvariant()
                        && api.Paths() == "get_tor_hash?by=topic_id&val=777", err + " " + api.Paths());
            }
        }

        // Прежняя версия: 8 файлов. Новая: a, d без изменений; b другой длины; c той же длины, другое содержимое; new.bin
        // новый; r\same.txt — содержимое p\same.txt (у q\same.txt то же имя и длина); x\moved.dat → y\moved.dat; old\gone.bin
        // исчез. Пути по алфавиту — гибрид требует того же порядка, что у дерева файлов.
        private static List<BtFxFile> UpdOldFiles()
        {
            return new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(50000, 901), "a.bin"),
                new BtFxFile(BtFx.Data(10000, 906), "d.bin"),
                new BtFxFile(BtFx.Data(40000, 902), "dir", "b.bin"),
                new BtFxFile(BtFx.Data(30000, 903), "dir", "c.bin"),
                new BtFxFile(BtFx.Data(20000, 904), "old", "gone.bin"),
                new BtFxFile(BtFx.Data(1000, 907), "p", "same.txt"),
                new BtFxFile(BtFx.Data(1000, 908), "q", "same.txt"),
                new BtFxFile(BtFx.Data(25000, 905), "x", "moved.dat")
            };
        }

        private static List<BtFxFile> UpdNewFiles()
        {
            return new List<BtFxFile>
            {
                new BtFxFile(BtFx.Data(50000, 901), "a.bin"),
                new BtFxFile(BtFx.Data(10000, 906), "d.bin"),
                new BtFxFile(BtFx.Data(45000, 912), "dir", "b.bin"),
                new BtFxFile(BtFx.Data(30000, 913), "dir", "c.bin"),
                new BtFxFile(BtFx.Data(12000, 914), "new.bin"),
                new BtFxFile(BtFx.Data(1000, 907), "r", "same.txt"),
                new BtFxFile(BtFx.Data(25000, 905), "y", "moved.dat")
            };
        }

        private static int UpdIndex(BtMeta m, string rel)
        {
            for (int i = 0; i < m.Files.Count; i++) if (m.Files[i].RelPath == rel) return i;
            return -1;
        }

        // «Keep», «Move* ← x\moved.dat» (звёздочка — совпадение по корню v2).
        private static string UpdAct(BtUpdatePlan plan, BtMeta oldMeta, BtMeta newMeta, string rel)
        {
            BtUpdateEntry e = plan.EntryFor(UpdIndex(newMeta, rel));
            if (e == null) return "none";
            string s = e.Action + (e.Identical ? "*" : "");
            if (e.Action == BtUpdateAction.Move) s += " <- " + oldMeta.Files[e.OldIndex].RelPath;
            return s;
        }

        private static string UpdPlanText(BtUpdatePlan plan, BtMeta oldMeta, BtMeta newMeta)
        {
            List<string> parts = new List<string>();
            foreach (BtUpdateEntry e in plan.Files) parts.Add(newMeta.Files[e.NewIndex].RelPath + "=" + UpdAct(plan, oldMeta, newMeta, newMeta.Files[e.NewIndex].RelPath));
            foreach (int i in plan.Removed) parts.Add("-" + oldMeta.Files[i].RelPath);
            return string.Join(", ", parts.ToArray());
        }

        private static BtMeta UpdMeta(string name, List<BtFxFile> files, int mode)
        {
            string err;
            BtMeta m = BtMeta.Parse(BtFx.Build(name, files, 16384, mode, false, null), out err);
            if (m == null) throw new InvalidOperationException("fixture torrent: " + err);
            return m;
        }

        // ================================================================== //
        //  План
        // ================================================================== //
        private static void UpdPlanCases(int mode)
        {
            string tag = mode == 3 ? "hybrid" : "v1";
            BtMeta oldMeta = UpdMeta("rel", UpdOldFiles(), mode);
            BtMeta newMeta = UpdMeta("rel", UpdNewFiles(), mode);
            BtUpdatePlan plan = BtUpdatePlan.Build(oldMeta, newMeta);
            string text = UpdPlanText(plan, oldMeta, newMeta);
            T.Check("update plan " + tag + ": builds without refusal, one entry per stored new file", plan.Refusal == null && plan.Files.Count == 7, plan.Refusal + " " + text);
            bool hybrid = mode == 3;
            T.Check("update plan " + tag + ": same path + same length ⇒ kept in place" + (hybrid ? ", proven identical by the v2 root" : ""),
                    UpdAct(plan, oldMeta, newMeta, "a.bin") == (hybrid ? "Keep*" : "Keep") && UpdAct(plan, oldMeta, newMeta, "d.bin") == (hybrid ? "Keep*" : "Keep"), text);
            T.Check("update plan " + tag + ": same path, other length ⇒ changed", UpdAct(plan, oldMeta, newMeta, "dir\\b.bin") == "Changed", text);
            T.Check("update plan " + tag + ": same path and length, other content ⇒ " + (hybrid ? "changed (roots differ)" : "kept for the hash check (v1 cannot tell)"),
                    UpdAct(plan, oldMeta, newMeta, "dir\\c.bin") == (hybrid ? "Changed" : "Keep"), text);
            T.Check("update plan " + tag + ": renamed folder, unique name + length ⇒ moved from the old path",
                    UpdAct(plan, oldMeta, newMeta, "y\\moved.dat") == (hybrid ? "Move* <- x\\moved.dat" : "Move <- x\\moved.dat"), text);
            T.Check("update plan " + tag + ": a brand-new file ⇒ downloaded", UpdAct(plan, oldMeta, newMeta, "new.bin") == "New", text);
            // Два прежних same.txt той же длины: по имени пара неоднозначна; гибрид находит настоящий источник по корню v2.
            T.Check("update plan " + tag + ": " + (hybrid ? "the v2 root picks the one true source among same-named files" : "an ambiguous name + length pair is never guessed"),
                    UpdAct(plan, oldMeta, newMeta, "r\\same.txt") == (hybrid ? "Move* <- p\\same.txt" : "New"), text);
            List<string> removed = new List<string>();
            foreach (int i in plan.Removed) removed.Add(oldMeta.Files[i].RelPath);
            string expected = hybrid ? "old\\gone.bin|q\\same.txt" : "old\\gone.bin|p\\same.txt|q\\same.txt";
            T.Check("update plan " + tag + ": old files without a place in the new version are listed for the Recycle Bin",
                    string.Join("|", removed.ToArray()) == expected && plan.RemovedBytes == (hybrid ? 21000 : 22000), text);
            T.Check("update plan " + tag + ": byte totals add up to the new version's size",
                    plan.KeepBytes + plan.ChangedBytes + plan.MoveBytes + plan.NewBytes == newMeta.TotalSize
                    && plan.NewBytes == (hybrid ? 12000 : 13000) && plan.IdenticalBytes == (hybrid ? 86000 : 0),
                    plan.KeepBytes + "+" + plan.ChangedBytes + "+" + plan.MoveBytes + "+" + plan.NewBytes + " vs " + newMeta.TotalSize + ", identical " + plan.IdenticalBytes);

            int[] oldPrio = new int[oldMeta.Files.Count];
            for (int i = 0; i < oldPrio.Length; i++) oldPrio[i] = 1;
            oldPrio[UpdIndex(oldMeta, "d.bin")] = 0;
            oldPrio[UpdIndex(oldMeta, "x\\moved.dat")] = 2;
            int[] carried = plan.CarryPriorities(oldPrio);
            T.Check("update plan " + tag + ": file selection follows the files — unselected stays unselected, high stays high, new files normal",
                    carried != null && carried.Length == newMeta.Files.Count && carried[UpdIndex(newMeta, "d.bin")] == 0
                    && carried[UpdIndex(newMeta, "y\\moved.dat")] == 2 && carried[UpdIndex(newMeta, "new.bin")] == 1 && carried[UpdIndex(newMeta, "a.bin")] == 1
                    && plan.CarryPriorities(null) == null);
        }

        private static void UpdPlanRefusals()
        {
            BtMeta a = UpdMeta("rel", UpdOldFiles(), 1);
            BtMeta again = UpdMeta("rel", UpdOldFiles(), 1);
            T.Check("update plan: the same info-hash is refused (nothing to update)", BtUpdatePlan.Build(a, again).Refusal != null);
            BtMeta single = UpdMeta("rel.iso", new List<BtFxFile> { new BtFxFile(BtFx.Data(60000, 921)) }, 1);
            BtMeta single2 = UpdMeta("rel-2.iso", new List<BtFxFile> { new BtFxFile(BtFx.Data(61000, 922)) }, 1);
            T.Check("update plan: folder → single file (and back) is refused, never mapped", BtUpdatePlan.Build(a, single).Refusal != null && BtUpdatePlan.Build(single, a).Refusal != null);
            BtUpdatePlan sp = BtUpdatePlan.Build(single, single2);
            T.Check("update plan: single-file torrent with a new name and length ⇒ the one file changes in place (the root name on disk stays)",
                    sp.Refusal == null && sp.Files.Count == 1 && sp.Files[0].Action == BtUpdateAction.Changed && sp.Files[0].OldIndex == 0 && sp.Removed.Count == 0, sp.Refusal);
            BtMeta v2 = UpdMeta("rel", UpdNewFiles(), 2);
            T.Check("update plan: a pure v2 new version is refused (the engine cannot run it)", BtUpdatePlan.Build(a, v2).Refusal != null);
            T.Check("update plan: missing metadata is refused", BtUpdatePlan.Build(null, a).Refusal != null && BtUpdatePlan.Build(a, null).Refusal != null);
        }

        // ================================================================== //
        //  Файловые шаги и проверка хешем новой версии
        // ================================================================== //
        private static void UpdDiskCases(int mode)
        {
            string tag = mode == 3 ? "hybrid" : "v1";
            bool hybrid = mode == 3;
            List<BtFxFile> oldFiles = UpdOldFiles(), newFiles = UpdNewFiles();
            BtMeta oldMeta = UpdMeta("rel", oldFiles, mode);
            BtMeta newMeta = UpdMeta("rel", newFiles, mode);
            string root = Fx.MakeDir(Fx.Root, "bt-update-" + tag);
            string dl = Fx.MakeDir(root, "dl");
            string bin = Fx.MakeDir(root, "bin");
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p)
            {
                string to = Path.Combine(bin, recycled.Count + "-" + Path.GetFileName(p));
                if (Directory.Exists(p)) Directory.Move(p, to); else File.Move(p, to);
                recycled.Add(p.Substring(Path.Combine(dl, "rel").Length + 1));
                return null;
            };

            // Прежняя версия на диске: всё скачано, кроме q\same.txt (частичный файл).
            int k = 0;
            for (int i = 0; i < oldMeta.Files.Count; i++)
            {
                if (oldMeta.Files[i].Pad) continue;
                string rel = oldMeta.Files[i].RelPath;
                string path = Path.Combine(Path.Combine(dl, "rel"), rel) + (rel == "q\\same.txt" ? DlPaths.PartSuffix : "");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, oldFiles[k++].Data);
            }
            BtResume oldResume;
            using (BtStorage oldCheck = new BtStorage(oldMeta, dl, "rel"))
            {
                BtBitfield have = oldCheck.Recheck(null, null);
                oldResume = BtResume.Capture(oldCheck, have, null);
            }

            BtUpdatePlan plan = BtUpdatePlan.Build(oldMeta, newMeta);
            using (BtStorage oldSt = new BtStorage(oldMeta, dl, "rel"))
            using (BtStorage newSt = new BtStorage(newMeta, dl, "rel"))
            {
                bool[] intact = BtUpdateDisk.Intact(oldSt, oldResume);
                T.Check("update disk " + tag + ": files verified in the old snapshot and untouched since are intact, the partial one is not",
                        intact[UpdIndex(oldMeta, "a.bin")] && intact[UpdIndex(oldMeta, "x\\moved.dat")] && !intact[UpdIndex(oldMeta, "q\\same.txt")]);
                File.SetLastWriteTimeUtc(oldSt.FinalPath(UpdIndex(oldMeta, "d.bin")), DateTime.UtcNow.AddMinutes(-5));
                bool[] touched = BtUpdateDisk.Intact(oldSt, oldResume);
                T.Check("update disk " + tag + ": a file written after the snapshot is not trusted any more", !touched[UpdIndex(oldMeta, "d.bin")]);
                intact = touched;

                string foreign = newSt.FinalPath(UpdIndex(newMeta, "new.bin"));
                File.WriteAllText(foreign, "user's own file");
                List<string> coll = BtUpdateDisk.Collisions(plan, oldSt, newSt);
                T.Check("update disk " + tag + ": a foreign file on a new file's place is reported as a collision", coll.Count == 1 && coll[0] == foreign, string.Join("; ", coll.ToArray()));
                File.Delete(foreign);
                T.Check("update disk " + tag + ": no collisions once the place is free", BtUpdateDisk.Collisions(plan, oldSt, newSt).Count == 0);

                string err = BtUpdateDisk.Apply(plan, oldSt, newSt);
                string again = BtUpdateDisk.Apply(plan, oldSt, newSt);
                T.Check("update disk " + tag + ": steps apply, a second pass (after a crash) finds nothing left to do", err == null && again == null, err + " / " + again);

                string relRoot = Path.Combine(dl, "rel");
                string gone = hybrid ? "old\\gone.bin|q\\same.txt.wpcpart|old|p|q|x" : "old\\gone.bin|p\\same.txt|q\\same.txt.wpcpart|old|p|q|x";
                T.Check("update disk " + tag + ": removed files and the emptied old folders went to the Recycle Bin, nothing else",
                        string.Join("|", recycled.ToArray()) == gone, string.Join("|", recycled.ToArray()));
                T.Check("update disk " + tag + ": the moved file sits under its new path with the old bytes, not re-created",
                        File.Exists(Path.Combine(relRoot, "y\\moved.dat")) && Bencode.SameBytes(File.ReadAllBytes(Path.Combine(relRoot, "y\\moved.dat")), BtFx.Data(25000, 905))
                        && !Directory.Exists(Path.Combine(relRoot, "x")));
                string bPart = newSt.PartPath(UpdIndex(newMeta, "dir\\b.bin"));
                T.Check("update disk " + tag + ": the changed file lost its final name and has the new length",
                        !File.Exists(newSt.FinalPath(UpdIndex(newMeta, "dir\\b.bin"))) && File.Exists(bPart) && new FileInfo(bPart).Length == 45000);
                T.Check("update disk " + tag + ": same-length file with other content — " + (hybrid ? "partial (roots differ)" : "stays final until the hash check"),
                        File.Exists(newSt.FinalPath(UpdIndex(newMeta, "dir\\c.bin"))) == !hybrid);

                // Проверка хешем новой версии — тот же BtStorage.Recheck, что при старте торрента.
                BtBitfield full;
                using (BtStorage check = new BtStorage(newMeta, dl, "rel")) full = check.Recheck(null, null);
                List<string> wrong = new List<string>();
                for (int p = 0; p < newMeta.PieceCount; p++)
                {
                    bool reusable = true, fresh = false;
                    foreach (int fi in newMeta.FilesOfPiece(p))
                    {
                        string rel = newMeta.Files[fi].RelPath;
                        if (newMeta.Files[fi].Pad) continue;
                        if (rel == "new.bin" || rel == "dir\\b.bin" || rel == "dir\\c.bin") fresh = true;
                        if (rel != "a.bin" && rel != "d.bin" && rel != "y\\moved.dat" && !(hybrid && rel == "r\\same.txt")) reusable = false;
                    }
                    if (reusable && !full[p]) wrong.Add(p + " should verify");
                    if (fresh && full[p] && FilesOnly(newMeta, p, "new.bin")) wrong.Add(p + " cannot verify");
                }
                T.Check("update disk " + tag + ": hash check of the new version verifies every piece of kept/moved data, none of the missing file",
                        wrong.Count == 0 && full.SetCount > 0 && full.SetCount < newMeta.PieceCount, string.Join("; ", wrong.ToArray()) + " have " + full.SetCount + "/" + newMeta.PieceCount);

                BtResume trust = BtUpdateDisk.TrustIdentical(plan, newSt, intact, null);
                if (!hybrid)
                {
                    T.Check("update disk v1: nothing is trusted without v2 roots — the start checks everything", trust == null);
                    return;
                }
                using (BtStorage restore = new BtStorage(newMeta, dl, "rel"))
                {
                    List<int> recheck = new List<int>();
                    BtBitfield have = trust == null ? null : trust.Restore(restore, recheck);
                    byte[] buf = new byte[newMeta.PieceLength];
                    if (have != null) foreach (int p in recheck) have[p] = restore.CheckPiece(p, buf);
                    bool same = have != null && have.SetCount == full.SetCount;
                    if (same) for (int p = 0; p < newMeta.PieceCount; p++) same &= have[p] == full[p];
                    int trustedA = 0, aPieces = 0;
                    BtFile fa = newMeta.Files[UpdIndex(newMeta, "a.bin")];
                    for (int p = fa.FirstPiece; p <= fa.LastPiece; p++) { aPieces++; if (!recheck.Contains(p)) trustedA++; }
                    BtFile fd = newMeta.Files[UpdIndex(newMeta, "d.bin")];
                    bool dRead = false;
                    for (int p = fd.FirstPiece; p <= fd.LastPiece; p++) dRead |= recheck.Contains(p);
                    T.Check("update disk hybrid: identical untouched files are trusted without reading, the rest is read, result equals a full check",
                            same && trustedA == aPieces && dRead && recheck.Count < newMeta.PieceCount,
                            "recheck " + recheck.Count + "/" + newMeta.PieceCount + ", a trusted " + trustedA + "/" + aPieces + ", d read " + dRead + ", same " + same);
                }
            }
        }

        // ================================================================== //
        //  Движок: rutracker → проба метаданных → замена → проверка хешем → раздача; новая версия файлом
        // ================================================================== //
        private const string UpdTopic = "https://rutracker.org/forum/viewtopic.php?t=4242";

        // Торрент фикстуры с темой rutracker в comment и трекером с ключом доступа.
        private static byte[] UpdTorrent(List<BtFxFile> files, int mode, string tracker)
        {
            string err;
            IList<IList<string>> tiers = tracker == null ? null : new List<IList<string>> { new List<string> { tracker } };
            BVal root = Bencode.Decode(BtFx.Build("wire", files, 16384, mode, false, tiers), out err);
            root.Set("comment", BVal.Str(UpdTopic));
            return Bencode.Encode(root);
        }

        private static List<string> UpdRecycleInto(string bin, string dl, Func<int, string> refuse)
        {
            List<string> recycled = new List<string>();
            DlFiles.Recycler = delegate(string p)
            {
                lock (recycled)
                {
                    string why = refuse == null ? null : refuse(recycled.Count);
                    if (why != null) return why;
                    string to = Path.Combine(bin, recycled.Count + "-" + Path.GetFileName(p));
                    if (Directory.Exists(p)) Directory.Move(p, to); else File.Move(p, to);
                    recycled.Add(p.Substring(Path.Combine(dl, "wire").Length + 1));
                    return null;
                }
            };
            return recycled;
        }

        private static string UpdFilesUnder(string dir)
        {
            List<string> list = new List<string>();
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) list.Add(f.Substring(dir.Length + 1) + ":" + new FileInfo(f).Length);
            list.Sort(StringComparer.Ordinal);
            return string.Join(",", list.ToArray());
        }

        private static void UpdEngineRutracker()
        {
            List<BtFxFile> oldFiles = UpdOldFiles(), newFiles = UpdNewFiles();
            string root = Fx.MakeDir(Fx.Root, "bt-update-engine");
            string dl = Fx.MakeDir(root, "dl");
            string bin = Fx.MakeDir(root, "bin");
            string watch = Fx.MakeDir(root, "watch");
            List<string> recycled = UpdRecycleInto(bin, dl, null);
            List<DlNotice> notices = new List<DlNotice>();
            FakeDlEnv env = new FakeDlEnv();
            BtSession seed = null;
            DlEngine e = null;
            // «Обновить трекеры» без 30-секундного min interval: тест не ждёт часы трекера.
            int savedMinInterval = BtTrackers.DefaultMinIntervalSeconds;
            BtTrackers.DefaultMinIntervalSeconds = 0;
            using (RtFakeApi api = new RtFakeApi())
            try
            {
                string tracker = api.Base + "ann?pk=SECRETPK";
                string err;
                byte[] oldBytes = UpdTorrent(oldFiles, 3, tracker), newBytes = UpdTorrent(newFiles, 3, tracker);
                BtMeta oldMeta = BtMeta.Parse(oldBytes, out err), newMeta = BtMeta.Parse(newBytes, out err);
                string disabled = "{\"error\":{\"code\":1,\"text\":\"Temporarily disabled\"}}";
                api.Set("get_tor_hash?by=topic_id&val=4242", disabled, null);
                api.Set("static/forum_size", "{\"result\":{\"7\":[1,1]}}", null);
                api.Set("static/pvc/f/7", RtDump("4242", oldMeta.HexHash.ToUpperInvariant()), "\"v1\"");
                api.Set("ann*", "d8:intervali1800e5:peers0:e", null);

                seed = WireSession(BtEncryption.Prefer);
                WireAddSeed(seed, oldMeta, oldFiles, Fx.MakeDir(root, "seed-old"));
                BtTorrent newSeed = WireAddSeed(seed, newMeta, newFiles, Fx.MakeDir(root, "seed-new"));
                DlSettings s = EngSettings(dl);
                s.BtWatchFolder = watch;
                e = EngEngine(Fx.MakeDir(root, "store"), s, env, notices);
                e.BtRutrackerBase = api.Base;
                e.BtRutrackerPauseMs = 0;
                e.BtNotRegPollSeconds = 1;
                e.BtProbeSeconds = 20;
                e.BtWatchSeconds = 0;
                e.BtWatchSettleSeconds = 60;
                e.Start();

                // 1. Прежняя версия скачана и раздаётся; первая проверка темы — та же версия.
                string id = EngAdd(e, EngFile(oldBytes));
                EngFeed(e, oldMeta, seed);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);
                bool checkedOnce = WireWaitFor(delegate { DlItem x = e.Find(id); return x != null && x.UpdateCheckedUtc != DateTime.MinValue; }, 10000);
                DlItem it = e.Find(id);
                T.Check("update engine: a torrent with a rutracker comment keeps its topic; the first check finds the same version — nothing offered",
                        seeding && checkedOnce && it.TopicUrl == UpdTopic && it.UpdateHash == "" && api.Paths().Contains("static/pvc/f/7")
                        && EngNotices(notices, id, DlNoticeKind.UpdateAvailable) == 0, EngState(e, id) + " " + api.Paths());

                // 2. Раздачу перезалили: трекер больше не знает хеш — проверка сразу, не через 6 часов.
                api.Set("static/pvc/f/7", RtDump("4242", newMeta.HexHash.ToUpperInvariant()), "\"v2\"");
                api.Set("ann*", "d14:failure reason22:Torrent not registerede", null);
                e.Reannounce(id);
                bool offered = WireWaitFor(delegate { DlItem x = e.Find(id); return x != null && x.UpdateHash == newMeta.HexHash; }, 15000);
                T.Check("update engine: tracker «Torrent not registered» ⇒ topic checked at once ⇒ new hash offered with one notice, nothing replaced yet",
                        offered && EngNotices(notices, id, DlNoticeKind.UpdateAvailable) == 1 && e.Find(id).InfoHash == oldMeta.HexHash
                        && EngWaitState(e, id, DlState.Seeding, 100), EngState(e, id) + " | " + EngJournal(e, id));
                WindowsProcessCleaner.Capture.ToastInfo toast = null;
                lock (notices) foreach (DlNotice n in notices) if (n.Id == id && n.Kind == DlNoticeKind.UpdateAvailable) toast = DlNotifier.InfoFor(n);
                T.Check("update engine: the notice's toast says a new version was found, not that the torrent was updated, and names the menu item",
                        toast != null && toast.Title == Tr.S("Новая версия раздачи: ", "New version of the torrent: ") + "wire"
                        && toast.Text.Contains(Tr.S("«Обновить раздачу…»", "“Update the torrent…”")), toast == null ? "no notice" : toast.Title + " / " + toast.Text);

                // 3. Проба: magnet нового хеша с трекерами прежней версии; метаданные — от пира, на диск ничего.
                string before = UpdFilesUnder(Path.Combine(dl, "wire"));
                BtTorrent probe = null;
                WireWaitFor(delegate { BtSession ses = e.TorrentSession; probe = ses == null ? null : ses.Find(newMeta.InfoHash); return probe != null; }, 10000);
                if (probe != null) probe.AddPeer(WireEp(seed));
                string pending = Path.Combine(e.TorrentsDir, "update-" + id + ".torrent");
                bool fetched = WireWaitFor(delegate { return File.Exists(pending) && EngRunning(e, newMeta) == null; }, 15000);
                BtMeta pendingMeta = File.Exists(pending) ? BtMeta.Parse(File.ReadAllBytes(pending), out err) : null;
                T.Check("update engine: the probe gets the new metadata, keeps the old trackers (passkey) and the topic, leaves the session, writes no data",
                        probe != null && fetched && pendingMeta != null && pendingMeta.HexHash == newMeta.HexHash && pendingMeta.Trackers.Count == 1
                        && pendingMeta.Trackers[0][0] == tracker && BtTopic.FromMeta(pendingMeta) == UpdTopic && UpdFilesUnder(Path.Combine(dl, "wire")) == before,
                        EngJournal(e, id) + " | " + before + " -> " + UpdFilesUnder(Path.Combine(dl, "wire")));

                // 4. Что изменится — ответ для окна.
                JVal info = e.UpdateJson(id);
                JVal files = info == null ? null : info.Get("files");
                JVal removed = info == null ? null : info.Get("removed");
                T.Check("update engine: update info is ready — per-file actions, removed list, no refusal and no collisions",
                        info != null && DlJson.Bool(info, "ready", false) && info.Get("refusal") == null && info.Get("collisions") == null && files != null && files.V.Count == 7
                        && removed != null && removed.V.Count == 2 && DlJson.Long(info, "newBytes", 0) == 12000, info == null ? "null" : Jsn.Write(info));

                // 5. Замена по кнопке: та же запись, данные прежней версии на месте, докачано только изменённое.
                string why;
                bool applied = e.ApplyUpdate(id, out why);
                EngFeed(e, newMeta, seed);
                bool seedingNew = EngWaitState(e, id, DlState.Seeding, 20000);
                string sameInfo;
                bool same = WireSameFiles(newMeta, newFiles, dl, out sameInfo);
                long bound = 0;
                foreach (BtFile f in newMeta.Files)
                    if (!f.Pad && (f.RelPath == "dir\\b.bin" || f.RelPath == "dir\\c.bin" || f.RelPath == "new.bin")) bound += (f.LastPiece - f.FirstPiece + 1) * (long)newMeta.PieceLength;
                long uploaded = newSeed.Stats().Uploaded;
                it = e.Find(id);
                T.Check("update engine: apply ⇒ same record on the new hash, verified and seeding, SHA-256 of every new file equal",
                        applied && seedingNew && same && it.InfoHash == newMeta.HexHash && it.UpdateHash == "" && e.Ids().Count == 1 && it.FileName == "wire",
                        why + " " + EngState(e, id) + " " + sameInfo);
                T.Check("update engine: only the changed and new files came from the swarm — kept and moved data were not downloaded again",
                        uploaded > 0 && uploaded <= bound, uploaded + " > " + bound);
                T.Check("update engine: removed files went to the Recycle Bin; old side files, the pending version and the journal are gone",
                        recycled.Contains("old\\gone.bin") && !EngSideFiles(e, oldMeta.HexHash) && !File.Exists(pending)
                        && !File.Exists(Path.Combine(e.TorrentsDir, "update-" + id + ".json")), string.Join("|", recycled.ToArray()));
                BtMeta side = BtMeta.Parse(File.ReadAllBytes(Path.Combine(e.TorrentsDir, newMeta.HexHash + ".torrent")), out err);
                string journal = EngJournal(e, id);
                T.Check("update engine: the new .torrent beside the store keeps the passkey tracker; the journal names the update and never the passkey",
                        side != null && side.Trackers.Count == 1 && side.Trackers[0][0] == tracker && journal.Contains(Tr.S("раздача обновлена", "the torrent was updated"))
                        && !journal.Contains("SECRETPK"), journal);

                // 6. Через 6 часов периодическая проверка находит следующую версию; её .torrent из папки наблюдения — не вторая запись.
                List<BtFxFile> v3Files = UpdNewFiles();
                v3Files[4] = new BtFxFile(BtFx.Data(13000, 915), "new.bin");
                byte[] v3Bytes = UpdTorrent(v3Files, 3, tracker);
                BtMeta v3 = BtMeta.Parse(v3Bytes, out err);
                api.Set("ann*", "d8:intervali1800e5:peers0:e", null);
                api.Set("static/pvc/f/7", RtDump("4242", v3.HexHash.ToUpperInvariant()), "\"v3\"");
                env.Advance(TimeSpan.FromHours(6.5));
                bool periodic = WireWaitFor(delegate { DlItem x = e.Find(id); return x != null && x.UpdateHash == v3.HexHash; }, 15000);
                T.Check("update engine: six hours later the periodic check offers the next version with its own notice",
                        periodic && EngNotices(notices, id, DlNoticeKind.UpdateAvailable) == 2, EngJournal(e, id));
                string watched = Path.Combine(watch, "v3.torrent");
                File.WriteAllBytes(watched, v3Bytes);
                EngAge(-10, watched);
                e.BtIntake();
                BtMeta v3Pending = DlEngine.LoadUpdateMeta(e.TorrentsDir, id, v3.HexHash);
                T.Check("update engine: the same version's .torrent in the watch folder fills in its metadata — no second record, no second notice",
                        e.Ids().Count == 1 && v3Pending != null && EngNotices(notices, id, DlNoticeKind.UpdateAvailable) == 2, e.Ids().Count + " items " + EngJournal(e, id));

                // 7. Версия, пришедшая вручную: без согласия — вопрос; «добавить отдельно» — вторая запись; отказ от найденной.
                List<BtFxFile> v4Files = UpdNewFiles();
                v4Files[0] = new BtFxFile(BtFx.Data(50000, 921), "a.bin");
                byte[] v4Bytes = UpdTorrent(v4Files, 3, tracker);
                DlTorrentRequest ask = EngFile(v4Bytes);
                string dup, askError;
                string askId = e.AddTorrent(ask, out dup, out askError);
                T.Check("update engine: a manual .torrent of the same topic is not added — the answer names the record it updates",
                        askId == null && ask.UpdateOf == id && !string.IsNullOrEmpty(askError) && e.Ids().Count == 1 && e.Find(id).UpdateHash == v3.HexHash, askError);
                DlTorrentRequest separate = EngFile(v4Bytes);
                separate.OnTopicMatch = DlEngine.TopicMatchAdd;
                separate.StartPaused = true;
                string separateId = e.AddTorrent(separate, out dup, out askError);
                T.Check("update engine: «add separately» adds a second record", separateId != null && e.Ids().Count == 2, askError);
                if (separateId != null) e.Remove(separateId, false, out why);

                bool dismissed = e.DismissUpdate(id, out why);
                env.Advance(TimeSpan.FromHours(6.5));
                WireWaitFor(delegate { DlItem x = e.Find(id); return x != null && (env.UtcNow - x.UpdateCheckedUtc).TotalHours < 1; }, 10000);
                it = e.Find(id);
                T.Check("update engine: a declined version is not offered again by the next periodic check",
                        dismissed && it.UpdateHash == "" && it.UpdateDismissed == v3.HexHash && !File.Exists(pending)
                        && EngNotices(notices, id, DlNoticeKind.UpdateAvailable) == 2 && (env.UtcNow - it.UpdateCheckedUtc).TotalHours < 1, EngJournal(e, id));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (seed != null) seed.Dispose();
                BtTrackers.DefaultMinIntervalSeconds = savedMinInterval;
            }
        }

        // ================================================================== //
        //  Падение посреди замены: новый процесс доводит её до конца; команды канала
        // ================================================================== //
        private static void UpdEngineCrash()
        {
            List<BtFxFile> oldFiles = UpdOldFiles(), newFiles = UpdNewFiles();
            string err;
            byte[] oldBytes = UpdTorrent(oldFiles, 1, null), newBytes = UpdTorrent(newFiles, 1, null);
            BtMeta oldMeta = BtMeta.Parse(oldBytes, out err), newMeta = BtMeta.Parse(newBytes, out err);
            string root = Fx.MakeDir(Fx.Root, "bt-update-crash");
            string dl = Fx.MakeDir(root, "dl");
            string bin = Fx.MakeDir(root, "bin");
            string store = Fx.MakeDir(root, "store");
            bool refuseSecond = true;
            List<string> recycled = UpdRecycleInto(bin, dl, delegate(int n) { return refuseSecond && n == 1 ? "файл занят" : null; });
            string pipe = "WindowsProcessCleaner.dl.updtest-" + System.Diagnostics.Process.GetCurrentProcess().Id;
            BtSession seed = null;
            DlEngine e = null, e2 = null;
            try
            {
                seed = WireSession(BtEncryption.Prefer);
                WireAddSeed(seed, oldMeta, oldFiles, Fx.MakeDir(root, "seed-old"));
                WireAddSeed(seed, newMeta, newFiles, Fx.MakeDir(root, "seed-new"));
                e = EngEngine(store, EngSettings(dl), new FakeDlEnv(), null);
                e.BtRutrackerBase = "http://127.0.0.1:9/v1/";
                e.Start();
                string id = EngAdd(e, EngFile(oldBytes));
                EngFeed(e, oldMeta, seed);
                bool seeding = EngWaitState(e, id, DlState.Seeding, 20000);

                DlCommands commands = new DlCommands(e, delegate { }, null);
                using (DlPipeServer server = new DlPipeServer(pipe, commands.Handle, DlIpc.IsOwnImage))
                {
                    server.Start();
                    string file = Path.Combine(root, "new.torrent");
                    File.WriteAllBytes(file, newBytes);
                    JVal offer = DlClient.Command("updateFromFile");
                    offer.Set("id", JVal.NewStr(id));
                    offer.Set("file", JVal.NewStr(file));
                    JVal offered = DlClient.Call(pipe, offer, 3000);
                    JVal ask = DlClient.Command("updateInfo");
                    ask.Set("id", JVal.NewStr(id));
                    JVal info = DlClient.Call(pipe, ask, 3000);
                    JVal u = info == null ? null : info.Get("update");
                    T.Check("update pipe: updateFromFile stores the new version without a notice; updateInfo lists 3 files to the Recycle Bin",
                            seeding && offered != null && DlJson.Bool(offered, "ok", false) && u != null && u.GetStr("updateHash") == newMeta.HexHash
                            && DlJson.Bool(u, "ready", false) && u.Get("removed") != null && u.Get("removed").V.Count == 3,
                            (offered == null ? "null" : Jsn.Write(offered)) + " " + (info == null ? "null" : Jsn.Write(info)));

                    // Окно страницы строится из этого же ответа: новое и изменённое сверху, уходящее в Корзину — снизу, «Обновить» доступна.
                    DlUpdateInfo view = DlUpdateInfo.FromJson(u);
                    List<string[]> rows = view == null ? new List<string[]>() : DlTorrentView.UpdateRows(view);
                    List<string> order = new List<string>();
                    foreach (string[] row in rows) order.Add(row[0] + "=" + row[2]);
                    string lead = view == null ? "" : string.Join(" / ", DlTorrentView.UpdateLead(view).ToArray());
                    T.Check("update pipe → window: rows new, changed, moved, kept, then the Recycle Bin; summary counts; «Update» enabled",
                            view != null && view.CanApply && DlTorrentView.UpdateBlocker(view) == "" && rows.Count == 10
                            && rows[0][0] == "new.bin" && rows[1][0] == "r\\same.txt" && rows[2][0] == "dir\\b.bin"
                            && rows[3][2] == Tr.S("перенесётся из ", "moves from ") + "x\\moved.dat" && rows[9][0] == "q\\same.txt"
                            && rows[9][2] == Tr.S("в Корзину — в новой версии его нет", "to the Recycle Bin — not in the new version")
                            && lead.Contains(Tr.S("в Корзину 3", "to the Recycle Bin 3")) && lead.Contains(newMeta.HexHash),
                            string.Join(" | ", order.ToArray()) + " / " + lead);
                    DlSnapshot listed = DlSnapshot.FromList(commands.Handle(DlClient.Command("list")));
                    DlRow listedRow = listed == null ? null : listed.Find(id);
                    T.Check("update pipe → page: the list row says a new version is available, the card row points at the menu item",
                            listedRow != null && DlView.StateText(listedRow, DateTime.UtcNow).EndsWith(Tr.S(" · есть новая версия", " · new version available"), StringComparison.Ordinal)
                            && DlTorrentView.UpdateStateText(listedRow.Item, DateTime.UtcNow) == Tr.S("есть — «Обновить раздачу…» в меню записи", "available — “Update the torrent…” in the item's menu"),
                            listedRow == null ? "no row" : DlView.StateText(listedRow, DateTime.UtcNow));

                    JVal check = DlClient.Command("checkUpdate");
                    check.Set("id", JVal.NewStr(id));
                    JVal checkResp = DlClient.Call(pipe, check, 3000);
                    T.Check("update pipe: checkUpdate on a topic of rutracker is accepted", checkResp != null && DlJson.Bool(checkResp, "ok", false),
                            checkResp == null ? "null" : Jsn.Write(checkResp));

                    // Корзина отказала на втором файле: часть шагов сделана, журнал остался, запись не стартует.
                    JVal apply = DlClient.Command("applyUpdate");
                    apply.Set("id", JVal.NewStr(id));
                    JVal applied = DlClient.Call(pipe, apply, 30000);
                    Thread.Sleep(600);
                    DlItem it = e.Find(id);
                    string journalFile = Path.Combine(e.TorrentsDir, "update-" + id + ".json");
                    T.Check("update pipe: a refused recycle stops the update midway — error returned, journal kept, record paused on the old hash and not resumable",
                            applied != null && !DlJson.Bool(applied, "ok", true) && DlJson.Str(applied, "error", "").Contains("файл занят")
                            && File.Exists(journalFile) && it.InfoHash == oldMeta.HexHash && it.State == DlState.Paused && !e.Resume(id)
                            && recycled.Count == 1 && EngRunning(e, oldMeta) == null,
                            (applied == null ? "null" : Jsn.Write(applied)) + " " + EngState(e, id) + " recycled " + string.Join("|", recycled.ToArray()));
                }
                e.Dispose();
                e = null;

                // Новый процесс: Start доводит замену, запись на новой версии докачивает изменённое и раздаёт.
                refuseSecond = false;
                e2 = EngEngine(store, EngSettings(dl), new FakeDlEnv(), null);
                e2.BtRutrackerBase = "http://127.0.0.1:9/v1/";
                e2.Start();
                DlItem after = e2.Find(id);
                bool rolled = after != null && after.InfoHash == newMeta.HexHash && !File.Exists(Path.Combine(e2.TorrentsDir, "update-" + id + ".json"))
                              && !EngSideFiles(e2, oldMeta.HexHash);
                EngFeed(e2, newMeta, seed);
                bool seedingNew = EngWaitState(e2, id, DlState.Seeding, 20000);
                string sameInfo;
                bool same = WireSameFiles(newMeta, newFiles, dl, out sameInfo);
                T.Check("update crash: the next start rolls the update forward — new hash, journal and old side files gone, the rest downloaded, SHA-256 equal",
                        rolled && seedingNew && same && recycled.Contains("old\\gone.bin") && recycled.Contains("p\\same.txt") && recycled.Contains("q\\same.txt"),
                        EngState(e2, id) + " " + sameInfo + " recycled " + string.Join("|", recycled.ToArray()));
            }
            finally
            {
                if (e != null) e.Dispose();
                if (e2 != null) e2.Dispose();
                if (seed != null) seed.Dispose();
            }
        }

        private static bool FilesOnly(BtMeta m, int piece, string rel)
        {
            foreach (int fi in m.FilesOfPiece(piece))
                if (!m.Files[fi].Pad && m.Files[fi].RelPath != rel) return false;
            return true;
        }
    }

    // Подменный api.rutracker.cc на 127.0.0.1: ответы по пути (ключ «префикс*» — любой запрос с этим началом, так отвечает и
    // трекер с ключом доступа), ETag/304, gzip по Accept-Encoding, журнал путей и имён клиента.
    internal sealed class RtFakeApi : IDisposable
    {
        private readonly HttpListener _listener = new HttpListener();
        private readonly Dictionary<string, string[]> _bodies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        private readonly List<string> _log = new List<string>();
        private readonly List<string> _agents = new List<string>();
        public readonly string Base;
        public int GzipServed, NotModified;

        public RtFakeApi()
        {
            TcpListener probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Base = "http://127.0.0.1:" + port + "/v1/";
            _listener.Prefixes.Add(Base);
            _listener.Start();
            Thread t = new Thread(Accept);
            t.IsBackground = true;
            t.Start();
        }

        public void Set(string rel, string body, string etag) { lock (_log) _bodies[rel] = new[] { body, etag }; }

        public void Clear() { lock (_log) { _log.Clear(); _agents.Clear(); GzipServed = 0; NotModified = 0; } }

        public string Paths() { lock (_log) return string.Join("|", _log.ToArray()); }

        public string AgentsText() { lock (_log) return string.Join("|", _agents.ToArray()); }

        public bool AllAgents(string ua)
        {
            lock (_log)
            {
                foreach (string a in _agents) if (a != ua) return false;
                return _agents.Count > 0;
            }
        }

        private void Accept()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch { return; }
                try { Handle(ctx); } catch { }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            string raw = ctx.Request.RawUrl ?? "";
            string rel = raw.StartsWith("/v1/", StringComparison.Ordinal) ? raw.Substring(4) : raw;
            string inm = ctx.Request.Headers["If-None-Match"];
            string[] entry;
            lock (_log)
            {
                _log.Add(rel + (inm != null ? " inm=" + inm : ""));
                _agents.Add(ctx.Request.UserAgent ?? "");
                if (!_bodies.TryGetValue(rel, out entry))
                    foreach (KeyValuePair<string, string[]> kv in _bodies)
                        if (kv.Key.EndsWith("*", StringComparison.Ordinal) && rel.StartsWith(kv.Key.Substring(0, kv.Key.Length - 1), StringComparison.Ordinal)) entry = kv.Value;
            }
            HttpListenerResponse resp = ctx.Response;
            if (entry == null) { resp.StatusCode = 404; resp.Close(); return; }
            if (entry[1] != null) resp.AddHeader("ETag", entry[1]);
            if (entry[1] != null && inm == entry[1])
            {
                lock (_log) NotModified++;
                resp.StatusCode = 304;
                resp.Close();
                return;
            }
            byte[] body = Encoding.UTF8.GetBytes(entry[0]);
            if ((ctx.Request.Headers["Accept-Encoding"] ?? "").IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                MemoryStream ms = new MemoryStream();
                using (GZipStream gz = new GZipStream(ms, CompressionMode.Compress, true)) gz.Write(body, 0, body.Length);
                body = ms.ToArray();
                resp.AddHeader("Content-Encoding", "gzip");
                lock (_log) GzipServed++;
            }
            resp.ContentType = "application/json";
            resp.ContentLength64 = body.Length;
            resp.OutputStream.Write(body, 0, body.Length);
            resp.Close();
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}
