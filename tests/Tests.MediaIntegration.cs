// Windows Process Cleaner — область «media»: собранная цепочка. Всё, что до этого файла проверялось по частям,
// здесь идёт настоящим путём: очередь → DlMediaRun → настоящая склейка ядра → готовый файл, который читает
// Media Foundation. Ни одна часть не подменяется: MdWiring.Install() ставит те же точки, что и рабочая программа.
//
// Отдельно проверяется то, что принадлежит только стыковке и чего не мог проверить ни один поток: удаление и
// «заново» убирают папку частей, обновление ссылки пишет новый адрес туда, откуда его читает движок, команда
// addMedia переводит вид из расширения в источник, а настройка качества доходит до выбора варианта.
using System;
using System.Globalization;
using System.IO;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class MediaTests
    {
        static partial void RunIntegration()
        {
            WholeChain();
            PartsAreCleanedUp();
            RefreshLinkReachesTheRun();
            AddMediaCommand();
            QualitySettingReachesTheChoice();
            SettingsRoundTrip();
            ToolsUpdateReachesTheTick();
            MediaRowText();
        }

        // ---------- вся цепочка: поток HLS → настоящая склейка → читаемый MP4 ----------
        // Ни MdHooks.Mux, ни MdHooks.CreateRun не подменены: работает то же, что у пользователя.
        private static void WholeChain()
        {
            MdWiring.Install();
            T.Check("integration: wiring fills every hook",
                    MdHooks.CreateRun != null && MdHooks.Mux != null && MdHooks.Extract != null && MdHooks.DeleteParts != null);

            using (DlTestServer srv = new DlTestServer())
            {
                string url = MdFx.Serve(srv, "hls-ts", "whole", "index.m3u8");
                string folder = MdDir("whole");
                DlSettings s = MdSettings(folder);
                DlMedia md = new DlMedia();
                md.Source = MdSource.Hls;
                md.ManifestUrl = url;
                md.Output = MdOutput.Mp4;
                using (DlEngine e = StartEngine(MdDir("whole-store"), s))
                {
                    string id = AddMedia(e, url, md, folder, "movie.mp4");
                    bool done = MdWait(delegate { return Done(e, id); }, 60000);
                    DlItem it = e.Find(id);
                    T.Check("integration: an HLS stream goes through the real core and finishes", done, MdInfo(e, id));
                    if (!done || it == null) return;
                    T.Check("integration: the assembled file exists and is not empty",
                            File.Exists(it.TargetPath) && new FileInfo(it.TargetPath).Length > 0, it.TargetPath);
                    T.Check("integration: the parts folder is gone after success",
                            it.Media.PartsDir.Length > 0 && !Directory.Exists(it.Media.PartsDir), it.Media.PartsDir);
                    T.Eq("integration: every segment is counted", "4/4", it.Media.SegmentsDone + "/" + it.Media.SegmentsTotal);
                    T.Eq("integration: the phase is cleared at the end", "", it.Media.Phase);
                    // Независимая сверка: файл читает Media Foundation, а не наш же код.
                    int video, audio;
                    string why;
                    bool read = MdFx.ReadBack(it.TargetPath, out video, out audio, out why);
                    T.Check("integration: Media Foundation reads the assembled file", read, why);
                    T.Check("integration: the assembled file holds the video frames of all four segments", read && video == 40,
                            video.ToString(CultureInfo.InvariantCulture));
                    T.Check("integration: the assembled file holds its audio", read && audio > 0,
                            audio.ToString(CultureInfo.InvariantCulture));
                }
            }
        }

        // ---------- удаление и «заново» убирают папку частей ----------
        // Прерываем на паузе: папка частей с журналом остаётся на диске, и дальше её судьбу решает очередь.
        private static void PartsAreCleanedUp()
        {
            MdWiring.Install();
            using (DlTestServer srv = new DlTestServer())
            {
                string url = MdFx.Serve(srv, "hls-ts", "parts", "index.m3u8");
                foreach (string what in new[] { "remove", "restart" })
                {
                    string folder = MdDir("parts-" + what);
                    using (DlEngine e = StartEngine(MdDir("parts-store-" + what), MdSettings(folder)))
                    {
                        DlMedia md = new DlMedia();
                        md.Source = MdSource.Hls;
                        md.ManifestUrl = url;
                        string id = AddMedia(e, url, md, folder, "movie.mp4");
                        MdWait(delegate
                        {
                            DlItem x = e.Find(id);
                            return x != null && x.Media.PartsDir.Length > 0 && Directory.Exists(x.Media.PartsDir);
                        }, 30000);
                        DlItem it = e.Find(id);
                        string parts = it == null ? "" : it.Media.PartsDir;
                        bool appeared = parts.Length > 0 && Directory.Exists(parts);
                        T.Check("integration: the parts folder appears while downloading (" + what + ")", appeared, parts);
                        if (!appeared) continue;
                        string why;
                        if (what == "remove")
                        {
                            bool ok = e.Remove(id, false, out why);
                            T.Check("integration: remove succeeds", ok, why);
                            T.Check("integration: remove takes the parts folder with it", !Directory.Exists(parts), parts);
                        }
                        else
                        {
                            bool ok = e.Restart(id, out why);
                            T.Check("integration: restart succeeds", ok, why);
                            T.Check("integration: restart clears the parts folder, so the journal cannot resume the old data",
                                    !Directory.Exists(parts), parts);
                        }
                    }
                }
            }
        }

        // ---------- обновление ссылки пишет адрес туда, откуда его читает движок ----------
        // Движок берёт Media.ManifestUrl и лишь потом Url: обновление только Url оставило бы его на мёртвом адресе.
        private static void RefreshLinkReachesTheRun()
        {
            using (DlEngine e = StartEngine(MdDir("refresh-store"), MdSettings(MdDir("refresh"))))
            {
                DlMedia md = new DlMedia();
                md.Source = MdSource.Hls;
                md.ManifestUrl = "http://127.0.0.1:1/old/index.m3u8";
                string id = AddMedia(e, md.ManifestUrl, md, MdDir("refresh-dst"), "movie.mp4");
                e.Pause(id);
                MdWait(delegate { DlItem x = e.Find(id); return x != null && x.State == DlState.Paused; }, 10000);
                string why;
                bool ok = e.RefreshLink(id, "http://127.0.0.1:1/new/index.m3u8", "", out why);
                DlItem it = e.Find(id);
                T.Check("integration: refreshing the link of a media item succeeds", ok, why);
                T.Eq("integration: the fresh address lands in ManifestUrl, where the run reads it",
                     "http://127.0.0.1:1/new/index.m3u8", it == null ? "" : it.Media.ManifestUrl);
                T.Eq("integration: the item url is refreshed too", "http://127.0.0.1:1/new/index.m3u8", it == null ? "" : it.Url);
            }
        }

        // ---------- команда addMedia: вид из расширения → источник ----------
        private static void AddMediaCommand()
        {
            string folder = MdDir("cmd");
            using (DlEngine e = StartEngine(MdDir("cmd-store"), MdSettings(folder)))
            {
                DlCommands commands = new DlCommands(e, delegate { }, null);
                string[,] cases = { { "hls", "Hls" }, { "dash", "Dash" }, { "file", "Direct" }, { "", "Direct" } };
                for (int i = 0; i < cases.GetLength(0); i++)
                {
                    JVal req = DlClient.Command("addMedia");
                    req.Set("url", DlJson.S("http://127.0.0.1:1/v" + i + "/index.m3u8"));
                    req.Set("kind", DlJson.S(cases[i, 0]));
                    req.Set("title", DlJson.S("t" + i));
                    req.Set("folder", DlJson.S(folder));
                    req.Set("paused", DlJson.B(true));
                    JVal answer = commands.Handle(req);
                    string newId = DlJson.Str(answer, "id", "");
                    DlItem it = newId.Length > 0 ? e.Find(newId) : null;
                    T.Check("integration: addMedia «" + cases[i, 0] + "» is accepted", it != null, Jsn.Write(answer));
                    if (it == null) continue;
                    T.Check("integration: addMedia makes a media item", it.IsMedia && it.Media != null);
                    T.Eq("integration: addMedia maps «" + cases[i, 0] + "» to a source", cases[i, 1], it.Media.Source.ToString());
                    T.Eq("integration: addMedia keeps the address as the manifest", it.Url, it.Media.ManifestUrl);
                }
                // Ссылка не http/https не должна доходить до очереди.
                JVal bad = DlClient.Command("addMedia");
                bad.Set("url", DlJson.S("file:///C:/windows/system32/calc.exe"));
                bad.Set("kind", DlJson.S("file"));
                JVal badAnswer = commands.Handle(bad);
                T.Check("integration: addMedia refuses a non-http link", !DlJson.Bool(badAnswer, "ok", false), Jsn.Write(badAnswer));
            }
        }

        // ---------- настройка качества доходит до выбора варианта ----------
        // Настройка живёт в окне, а выбирает вариант движок: проверяем ту же функцию, которую он и зовёт.
        private static void QualitySettingReachesTheChoice()
        {
            MdManifest m = new MdManifest();
            int[] heights = { 240, 480, 720, 1080 };
            foreach (int h in heights)
            {
                MdVariant v = new MdVariant();
                v.Id = h + "p";
                v.Height = h;
                v.Bandwidth = h * 3000L;
                v.Main = new MdTrack();
                m.Variants.Add(v);
            }
            T.Eq("integration: quality «best» takes the highest variant", "1080p", DlMediaRun.ChooseVariant(m, 0, 0).Id);
            T.Eq("integration: quality 720 never goes above it", "720p", DlMediaRun.ChooseVariant(m, 720, 0).Id);
            T.Eq("integration: quality 600 falls to the closest one below", "480p", DlMediaRun.ChooseVariant(m, 600, 0).Id);
            T.Eq("integration: quality below every variant still picks the lowest", "240p", DlMediaRun.ChooseVariant(m, 100, 0).Id);
        }

        // ---------- настройки видео переживают запись и чтение ----------
        private static void SettingsRoundTrip()
        {
            DlSettings s = new DlSettings();
            s.MdMaxHeight = 720;
            s.MdOutput = MdOutput.WebM;
            s.MdSubtitles = false;
            s.MdToolsUpdate = false;
            DlSettings back = DlSettings.FromJson(Jsn.Parse(Jsn.Write(s.ToJson())));
            T.Eq("integration: the quality setting survives a save", 720, back.MdMaxHeight);
            T.Eq("integration: the container setting survives a save", "WebM", back.MdOutput.ToString());
            T.Check("integration: the subtitle setting survives a save", !back.MdSubtitles);
            T.Check("integration: the tools-update setting survives a save", !back.MdToolsUpdate);
            DlSettings defaults = DlSettings.FromJson(Jsn.Parse("{}"));
            T.Eq("integration: quality defaults to «best»", 0, defaults.MdMaxHeight);
            T.Eq("integration: the container defaults to «by tracks»", "Auto", defaults.MdOutput.ToString());
            T.Check("integration: keeping yt-dlp fresh is on by default", defaults.MdToolsUpdate);
        }

        // ---------- флажок «держать yt-dlp свежим» доходит из настроек до шва проверки ----------
        // Без этого настройка сохранялась бы в файл и не значила ничего: проверку обновлений зовёт только Tick очереди.
        private static void ToolsUpdateReachesTheTick()
        {
            DlSettings s = MdSettings(MdDir("tools-tick"));
            s.MdToolsUpdate = false;
            using (DlEngine e = StartEngine(MdDir("tools-tick-store"), s))
            {
                int before = MdWiring.ToolsTicks;
                MdWiring.ToolsTickEnabled = true;
                e.Tick();
                T.Check("integration: the queue tick reaches the tools-update check with the setting off",
                        MdWiring.ToolsTicks > before && !MdWiring.ToolsTickEnabled);
                before = MdWiring.ToolsTicks;
                s.MdToolsUpdate = true;
                e.UpdateSettings(s);
                e.Tick();
                T.Check("integration: the same tick carries the setting when it is on",
                        MdWiring.ToolsTicks > before && MdWiring.ToolsTickEnabled);
            }
        }

        // ---------- строка списка у видео ----------
        // У видео байты лежат в папке частей, DoneBytes = 0: без своей ветки строка была бы пустой, а полоса — нулевой.
        private static void MediaRowText()
        {
            DlRow r = new DlRow();
            r.Item = new DlItem();
            r.Item.Kind = DlItem.KindMedia;
            r.Item.State = DlState.Active;
            r.Item.Media = new DlMedia();
            r.Item.Media.SegmentsTotal = 10;
            r.Item.Media.SegmentsDone = 4;
            r.Item.Media.SecondsDone = 42;
            T.Check("integration: a media row shows parts instead of an empty size",
                    DlView.SizeText(r).IndexOf("4", StringComparison.Ordinal) >= 0 && DlView.SizeText(r).Length > 0, DlView.SizeText(r));
            T.Eq("integration: the progress bar follows the parts", "0,4", DlView.Fraction(r).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")));
            r.Item.Media.Phase = "mux";
            T.Check("integration: assembling is named, so a silent bar is not mistaken for a freeze",
                    DlView.StateText(r, DateTime.UtcNow).Length > 0 && DlView.StateText(r, DateTime.UtcNow) != Tr.S("качается", "downloading"),
                    DlView.StateText(r, DateTime.UtcNow));
            // Трансляция: конца нет, полоса обязана остаться бегущей.
            r.Item.Media.Phase = "";
            r.Item.Media.Live = true;
            r.Item.Media.SegmentsTotal = -1;
            T.Eq("integration: a live recording has no progress fraction", -1.0, DlView.Fraction(r));
        }
    }
}
