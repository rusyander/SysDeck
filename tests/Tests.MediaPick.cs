// SysDeck — тесты окна «Видео по ссылке»: разбор ссылки, список качеств, подбор звука, команда процессу загрузок.
// Сборка: tests\run-tests.bat.
//
// Список качеств проверяется на ответе yt-dlp той же формы, что у YouTube (MP4 H.264, WebM VP9, MP4 AV1, звук M4A
// и Opus), и на записанном ответе YouTube. Команда addMedia — через тот же DlCommands, что слушает процесс загрузок.
using System;
using System.Collections.Generic;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static partial class MediaTests
    {
        private const string PickJson = "{\"title\":\"Клип / тест\",\"duration\":100,\"webpage_url\":\"https://www.youtube.com/watch?v=pick\",\"formats\":["
            + "{\"format_id\":\"137\",\"protocol\":\"https\",\"url\":\"https://r.example/137\",\"ext\":\"mp4\",\"vcodec\":\"avc1.640028\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":30,\"tbr\":4000},"
            + "{\"format_id\":\"248\",\"protocol\":\"https\",\"url\":\"https://r.example/248\",\"ext\":\"webm\",\"vcodec\":\"vp9\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":30,\"tbr\":2500},"
            + "{\"format_id\":\"399\",\"protocol\":\"https\",\"url\":\"https://r.example/399\",\"ext\":\"mp4\",\"vcodec\":\"av01.0.08M.08\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":30,\"tbr\":2000},"
            + "{\"format_id\":\"299\",\"protocol\":\"https\",\"url\":\"https://r.example/299\",\"ext\":\"mp4\",\"vcodec\":\"avc1.64002a\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":60,\"tbr\":6000},"
            + "{\"format_id\":\"303\",\"protocol\":\"https\",\"url\":\"https://r.example/303\",\"ext\":\"webm\",\"vcodec\":\"vp9\",\"acodec\":\"none\",\"height\":1080,\"width\":1920,\"fps\":60,\"tbr\":4000},"
            + "{\"format_id\":\"313\",\"protocol\":\"https\",\"url\":\"https://r.example/313\",\"ext\":\"webm\",\"vcodec\":\"vp9\",\"acodec\":\"none\",\"height\":2160,\"width\":3840,\"fps\":30,\"tbr\":15000},"
            + "{\"format_id\":\"401\",\"protocol\":\"https\",\"url\":\"https://r.example/401\",\"ext\":\"mp4\",\"vcodec\":\"av01.0.12M.08\",\"acodec\":\"none\",\"height\":2160,\"width\":3840,\"fps\":30,\"tbr\":12000},"
            + "{\"format_id\":\"571\",\"protocol\":\"https\",\"url\":\"https://r.example/571\",\"ext\":\"mp4\",\"vcodec\":\"av01.0.16M.08\",\"acodec\":\"none\",\"height\":4320,\"width\":7680,\"fps\":30,\"tbr\":30000},"
            + "{\"format_id\":\"269\",\"protocol\":\"m3u8_native\",\"url\":\"https://r.example/269.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"avc1.4D400C\",\"acodec\":\"none\",\"height\":720,\"width\":1280,\"fps\":30,\"tbr\":2600},"
            + "{\"format_id\":\"233\",\"protocol\":\"m3u8_native\",\"url\":\"https://r.example/233.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"none\",\"acodec\":\"mp4a.40.5\",\"format_note\":\"Default, low\",\"tbr\":200},"
            + "{\"format_id\":\"136\",\"protocol\":\"https\",\"url\":\"https://r.example/136\",\"ext\":\"mp4\",\"vcodec\":\"avc1.4d401f\",\"acodec\":\"none\",\"height\":720,\"width\":1280,\"fps\":30,\"tbr\":2000},"
            + "{\"format_id\":\"140\",\"protocol\":\"https\",\"url\":\"https://r.example/140\",\"ext\":\"m4a\",\"vcodec\":\"none\",\"acodec\":\"mp4a.40.2\",\"tbr\":129},"
            + "{\"format_id\":\"251\",\"protocol\":\"https\",\"url\":\"https://r.example/251\",\"ext\":\"webm\",\"vcodec\":\"none\",\"acodec\":\"opus\",\"tbr\":140},"
            + "{\"format_id\":\"251-drc\",\"protocol\":\"https\",\"url\":\"https://r.example/251d\",\"ext\":\"webm\",\"vcodec\":\"none\",\"acodec\":\"opus\",\"tbr\":150,\"format_note\":\"medium, DRC\"},"
            + "{\"format_id\":\"18\",\"protocol\":\"https\",\"url\":\"https://r.example/18\",\"ext\":\"mp4\",\"vcodec\":\"avc1.42001E\",\"acodec\":\"mp4a.40.2\",\"height\":360,\"width\":640,\"fps\":30,\"tbr\":500}"
            + "],\"subtitles\":{\"ru\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/ru.vtt\",\"name\":\"Русский\"}],\"de\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/de.vtt\",\"name\":\"Deutsch\"}]},"
            + "\"automatic_captions\":{\"en\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/a-en.vtt\",\"name\":\"English\"}],\"de\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/a-de.vtt\",\"name\":\"Deutsch\"}],"
            + "\"en-orig\":[{\"ext\":\"vtt\",\"url\":\"https://s.example/a-eo.vtt\",\"name\":\"English (Original)\"}]}}";

        static partial void RunPick()
        {
            bool wasEn = Tr.En;
            try
            {
                Tr.En = false;
                PickLinks();
                PickQualities();
                PickTwitch();
                PickRecorded();
                PickCommand();
            }
            finally { Tr.En = wasEn; }
        }

        private static string PickIds(List<MdPickQuality> list)
        {
            List<string> ids = new List<string>();
            foreach (MdPickQuality q in list) ids.Add(q.AudioOnly ? "a" + q.Audio.Id + "/" + q.Output : q.Variant.Id);
            return string.Join(",", ids.ToArray());
        }

        // ---------- ссылка ----------
        private static void PickLinks()
        {
            T.Eq("pick: bare youtube.com gets https://", "https://youtube.com/watch?v=abc", MdPick.Normalize("  youtube.com/watch?v=abc  "));
            T.Eq("pick: www. gets https://", "https://www.twitch.tv/videos/1", MdPick.Normalize("www.twitch.tv/videos/1"));
            T.Eq("pick: angle brackets and the second line dropped", "https://youtu.be/x", MdPick.Normalize("\r\n<https://youtu.be/x>\r\nhttps://other.example/"));
            T.Eq("pick: ftp refused", "", MdPick.Normalize("ftp://example.com/v"));
            T.Eq("pick: a word is not a link", "", MdPick.Normalize("привет"));
            T.Eq("pick: a space inside refused", "", MdPick.Normalize("https://you tube.com/x"));
            T.Eq("pick: youtu.be is YouTube", MdSite.Youtube, MdPick.SiteOf("https://youtu.be/x"));
            T.Eq("pick: m.twitch.tv is Twitch", MdSite.Twitch, MdPick.SiteOf("https://m.twitch.tv/videos/1"));
            T.Eq("pick: notyoutube.com is not YouTube", MdSite.Other, MdPick.SiteOf("https://notyoutube.com/watch?v=1"));
            T.Eq("pick: twitch.tv.evil.example is not Twitch", MdSite.Other, MdPick.SiteOf("https://twitch.tv.evil.example/x"));
            T.Eq("pick: empty is None", MdSite.None, MdPick.SiteOf(""));
            T.Check("pick: a YouTube playlist is named as such", MdPick.Describe("https://www.youtube.com/playlist?list=PL1").IndexOf("плейлист", StringComparison.Ordinal) >= 0);
            T.Check("pick: a Twitch channel warns about live recording", MdPick.Describe("https://www.twitch.tv/somechannel").IndexOf("эфире", StringComparison.Ordinal) >= 0);
            T.Check("pick: a Twitch clip", MdPick.Describe("https://clips.twitch.tv/AbcDef").IndexOf("клип", StringComparison.Ordinal) >= 0);
            T.Check("pick: a Twitch VOD", MdPick.Describe("https://www.twitch.tv/videos/123").IndexOf("запись", StringComparison.Ordinal) >= 0);
            T.Eq("pick: another site shows its host", "vimeo.com", MdPick.Describe("https://vimeo.com/1"));
            T.Eq("pick: duration under an hour", "1:40", MdPick.Duration(100));
            T.Eq("pick: duration over an hour", "1:02:03", MdPick.Duration(3723));
            T.Eq("pick: unknown duration is empty", "", MdPick.Duration(0));
        }

        // ---------- качества ----------
        private static void PickQualities()
        {
            string err;
            MdManifest m = MdYtdlp.FromJson(PickJson, "https://www.youtube.com/watch?v=pick", out err);
            T.Check("pick: the synthetic answer maps", m != null && m.Variants.Count == 11, err);
            if (m == null) return;

            List<MdPickQuality> auto = MdPick.Qualities(m, MdOutput.Auto, false, false);
            T.Eq("pick: auto — one row per height+fps, H.264 over VP9, direct over HLS, AV1-only 4320p dropped, VP9 at 2160p, audio last",
                 "313,299,137,136,18,a140/M4a", PickIds(auto));
            List<MdPickQuality> withMp3 = MdPick.Qualities(m, MdOutput.Auto, false, true);
            T.Eq("pick: MP3 row only when the encoder exists", "313,299,137,136,18,a140/M4a,a140/Mp3", PickIds(withMp3));
            T.Eq("pick: MP4 — VP9 rows gone", "299,137,136,18,a140/M4a", PickIds(MdPick.Qualities(m, MdOutput.Mp4, false, false)));
            T.Eq("pick: WebM — only VP9 rows", "313,303,248,a140/M4a", PickIds(MdPick.Qualities(m, MdOutput.WebM, false, false)));

            MdPickQuality q313 = auto[0], q299 = auto[1], q137 = auto[2], q18 = auto[4], qa = auto[5];
            T.Eq("pick: VP9 video gets the Opus track, the original over the louder-bitrate DRC one", "251", q313.Audio == null ? "" : q313.Audio.Id);
            T.Eq("pick: H.264 video gets the direct AAC track, not the HLS one YouTube marks Default", "140", q299.Audio == null ? "" : q299.Audio.Id);
            T.Check("pick: a muxed format needs no separate audio", q18.Audio == null);
            T.Check("pick: audio-only row carries a variant for the engine and M4A output",
                    qa.AudioOnly && qa.Variant != null && qa.Variant.AudioGroup.Length > 0 && qa.Output == MdOutput.M4a);
            foreach (MdPickQuality q in withMp3)
                T.Check("pick: curated row «" + q.Label + "» assembles without transcoding", MdPick.Problem(q) == null, MdPick.Problem(q));
            T.Eq("pick: size = bitrate × duration for video + audio", 50000000L + 1612500L, q137.SizeBytes);
            T.Check("pick: label shows height, fps class, codec, size", q299.Label.StartsWith("1080p60 · H.264 · ≈ ", StringComparison.Ordinal), q299.Label);
            T.Check("pick: audio label", qa.Label.StartsWith("Только звук · M4A · 129 кбит/с", StringComparison.Ordinal), qa.Label);

            T.Eq("pick: default with no cap is the best row", 0, MdPick.DefaultIndex(auto, 0));
            T.Eq("pick: default under a 720p cap is 720p", 3, MdPick.DefaultIndex(auto, 720));
            T.Eq("pick: default under a 1440p cap is 1080p60", 1, MdPick.DefaultIndex(auto, 1440));
            T.Eq("pick: everything above the cap ⇒ the lowest video row, never audio", 4, MdPick.DefaultIndex(auto, 240));

            List<MdPickQuality> all = MdPick.Qualities(m, MdOutput.Auto, true, false);
            T.Eq("pick: all formats = every variant + audio", 12, all.Count);
            MdPickQuality av1 = null;
            foreach (MdPickQuality q in all) if (q.Variant.Id == "401") av1 = q;
            T.Check("pick: AV1-in-MP4 from the full list is refused before it is queued", av1 != null && MdPick.Problem(av1) != null);
            MdPickQuality hls = null;
            foreach (MdPickQuality q in all) if (q.Variant.Id == "269") hls = q;
            T.Check("pick: YouTube HLS from the full list is refused before it is queued", hls != null && MdPick.Problem(hls) != null);
            MdPickQuality forcedMp4 = new MdPickQuality();
            forcedMp4.Variant = q313.Variant; forcedMp4.Audio = q313.Audio; forcedMp4.Output = MdOutput.Mp4;
            T.Check("pick: VP9 forced into MP4 is refused", MdPick.Problem(forcedMp4) != null);
            MdPickQuality mixed = new MdPickQuality();
            mixed.Variant = q299.Variant; mixed.Audio = q313.Audio; mixed.Output = MdOutput.Auto;
            T.Check("pick: H.264 with Opus is refused", MdPick.Problem(mixed) != null);

            List<MdTrack> subs = MdPick.Subtitles(m);
            List<string> sid = new List<string>();
            foreach (MdTrack s in subs) sid.Add(s.Id);
            T.Eq("pick: subtitles — all uploaded, auto only in ru/en/-orig", "sub-ru,sub-de,auto-en,auto-en-orig", string.Join(",", sid.ToArray()));
            T.Eq("pick: subtitles preselected in the UI language when the setting is on", 0, MdPick.DefaultSubtitle(subs, true));
            T.Eq("pick: no subtitles preselected when the setting is off", -1, MdPick.DefaultSubtitle(subs, false));
        }

        // Twitch: видео со звуком в одном HLS-потоке, отдельно «audio_only»; группы звука нет.
        private static void PickTwitch()
        {
            string json = "{\"title\":\"vod\",\"duration\":60,\"formats\":["
                + "{\"format_id\":\"audio_only\",\"protocol\":\"m3u8_native\",\"url\":\"https://t.example/a.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"none\",\"acodec\":\"mp4a.40.2\",\"tbr\":160},"
                + "{\"format_id\":\"160p\",\"protocol\":\"m3u8_native\",\"url\":\"https://t.example/160.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"avc1.4D401F\",\"acodec\":\"mp4a.40.2\",\"height\":160,\"fps\":30,\"tbr\":230},"
                + "{\"format_id\":\"1080p60\",\"protocol\":\"m3u8_native\",\"url\":\"https://t.example/1080.m3u8\",\"ext\":\"mp4\",\"vcodec\":\"avc1.64002A\",\"acodec\":\"mp4a.40.2\",\"height\":1080,\"fps\":60,\"tbr\":6900}"
                + "]}";
            string err;
            MdManifest m = MdYtdlp.FromJson(json, "https://www.twitch.tv/videos/1", out err);
            if (m == null) { T.Check("pick: twitch manifest", false, err); return; }
            List<MdPickQuality> list = MdPick.Qualities(m, MdOutput.Auto, false, false);
            T.Eq("pick: twitch — muxed rows, then audio-only although no audio group exists", "1080p60,160p,aaudio_only/M4a", PickIds(list));
            MdPickQuality qa = list[list.Count - 1];
            T.Check("pick: twitch audio-only rides on the smallest variant and assembles", qa.Variant.Id == "160p" && MdPick.Problem(qa) == null, qa.Variant.Id);
            T.Eq("pick: twitch muxed rows carry no separate audio", null, list[0].Audio);

            // Клип Twitch: кодеков нет вовсе, а audio_ext «none» — звук в файле всё равно есть, это не «немое видео».
            string clip = "{\"title\":\"clip\",\"duration\":30,\"formats\":["
                + "{\"format_id\":\"portrait-1080\",\"protocol\":\"https\",\"url\":\"https://c.example/clip.mp4\",\"ext\":\"mp4\",\"video_ext\":\"mp4\",\"audio_ext\":\"none\",\"height\":1080,\"fps\":30}"
                + "]}";
            m = MdYtdlp.FromJson(clip, "https://clips.twitch.tv/Some", out err);
            if (m == null) { T.Check("pick: twitch clip manifest", false, err); return; }
            List<MdPickQuality> cl = MdPick.Qualities(m, MdOutput.Auto, false, false);
            T.Check("pick: twitch clip without codecs counts as video with sound (muxed, nothing stripped at assembly)",
                    cl.Count == 1 && cl[0].Variant.Main.Kind == MdTrackKind.Muxed && cl[0].Audio == null && MdPick.Problem(cl[0]) == null,
                    PickIds(cl) + " " + (cl.Count > 0 ? cl[0].Variant.Main.Kind.ToString() : ""));
            T.Eq("pick: «channel is not currently live» is its own error class", MdYtdlpError.Offline,
                 MdYtdlp.Classify("ERROR: [twitch:stream] somechannel: The channel is not currently live"));
            T.Check("pick: the offline text says what to do instead",
                    MdYtdlp.ErrorText(MdYtdlpError.Offline, "").IndexOf("twitch.tv/videos", StringComparison.Ordinal) >= 0);
        }

        // Записанный ответ YouTube: каждая строка короткого списка собирается, высоты не повторяются.
        private static void PickRecorded()
        {
            string err;
            MdManifest m = MdYtdlp.FromJson(ToolsSampleJson, "https://www.youtube.com/watch?v=aqz-KE-bpKQ", out err);
            T.Check("pick: recorded YouTube answer maps", m != null, err);
            if (m == null) return;
            List<MdPickQuality> list = MdPick.Qualities(m, MdOutput.Auto, false, false);
            HashSet<string> seen = new HashSet<string>();
            bool unique = true, fits = true, hasVideo = false, hasAudio = false;
            foreach (MdPickQuality q in list)
            {
                if (MdPick.Problem(q) != null) fits = false;
                if (q.AudioOnly) { hasAudio = true; continue; }
                hasVideo = true;
                if (!seen.Add(q.Variant.Height + "|" + (q.Variant.Fps > 31))) unique = false;
            }
            T.Check("pick: recorded — video rows and an audio row", hasVideo && hasAudio, PickIds(list));
            T.Check("pick: recorded — no height+fps repeats", unique, PickIds(list));
            T.Check("pick: recorded — every row assembles", fits, PickIds(list));
        }

        // ---------- команда процессу загрузок ----------
        private static void PickCommand()
        {
            string err;
            MdManifest m = MdYtdlp.FromJson(PickJson, "https://www.youtube.com/watch?v=pick&t=5", out err);
            if (m == null) { T.Check("pick: command manifest", false, err); return; }
            List<MdPickQuality> auto = MdPick.Qualities(m, MdOutput.Auto, false, false);
            JVal c = MdPick.Command(m, auto[1], MdPick.Subtitles(m)[0], "", false, false);
            T.Eq("pick: command is addMedia", "addMedia", DlJson.Str(c, "cmd", ""));
            T.Eq("pick: command url is the page the user pasted", "https://www.youtube.com/watch?v=pick&t=5", DlJson.Str(c, "url", ""));
            T.Eq("pick: command kind is page", "page", DlJson.Str(c, "kind", ""));
            T.Check("pick: command carries variant, audio, subtitles, output",
                    DlJson.Str(c, "variantId", "") == "299" && DlJson.Str(c, "audioId", "") == "140" && DlJson.Str(c, "output", "") == "Auto"
                    && c.Get("subtitleIds") != null && c.Get("subtitleIds").V.Count == 1 && c.Get("subtitleIds").V[0].Raw == "sub-ru", Jsn.Write(c));
            T.Check("pick: no folder, paused or duplicate flags unless asked",
                    c.Get("folder") == null && c.Get("paused") == null && c.Get("allowDuplicate") == null, Jsn.Write(c));

            // Тот же путь, что у процесса загрузок: выбор из окна доходит до записи. Вид «hls» — чтобы не требовать yt-dlp.
            string folder = MdDir("pick");
            using (DlEngine e = StartEngine(MdDir("pick-store"), MdSettings(folder)))
            {
                DlCommands commands = new DlCommands(e, delegate { }, null);
                JVal req = DlClient.Command("addMedia");
                req.Set("url", DlJson.S("http://127.0.0.1:1/pick/index.m3u8"));
                req.Set("kind", DlJson.S("hls"));
                req.Set("folder", DlJson.S(folder));
                req.Set("paused", DlJson.B(true));
                req.Set("variantId", DlJson.S("299"));
                req.Set("audioId", DlJson.S("140"));
                req.Set("subtitleIds", DlJson.Strings(new[] { "sub-ru", new string('x', 201) }));
                req.Set("output", DlJson.S("WebM"));
                JVal answer = commands.Handle(req);
                DlItem it = e.Find(DlJson.Str(answer, "id", ""));
                T.Check("pick: addMedia with a choice is accepted", it != null && it.Media != null, Jsn.Write(answer));
                if (it != null && it.Media != null)
                {
                    T.Eq("pick: variant reaches the item", "299", it.Media.VariantId);
                    T.Eq("pick: audio reaches the item", "140", it.Media.AudioId);
                    T.Eq("pick: subtitles reach the item, an over-long id dropped", "sub-ru", string.Join(",", it.Media.SubtitleIds.ToArray()));
                    T.Eq("pick: output from the window wins over the setting", MdOutput.WebM, it.Media.Output);
                }
                JVal again = commands.Handle(req);
                T.Check("pick: the same link again ⇒ duplicateOf, nothing added",
                        !DlJson.Bool(again, "ok", false) && DlJson.Str(again, "duplicateOf", "").Length > 0, Jsn.Write(again));
                req.Set("allowDuplicate", DlJson.B(true));
                req.Set("variantId", DlJson.S(new string('v', 201)));
                req.Set("output", DlJson.S("nonsense"));
                JVal forced = commands.Handle(req);
                DlItem it2 = e.Find(DlJson.Str(forced, "id", ""));
                T.Check("pick: allowDuplicate adds a second item", it2 != null && it2 != it, Jsn.Write(forced));
                if (it2 != null && it2.Media != null)
                {
                    T.Eq("pick: an over-long variant id is dropped — the engine picks by the setting", "", it2.Media.VariantId);
                    T.Eq("pick: an unknown output falls back to the setting", MdOutput.Auto, it2.Media.Output);
                }
            }
        }
    }
}
