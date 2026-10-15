// SysDeck — «Загрузки», окно «Видео по ссылке»: что за ссылка и какие качества из ответа yt-dlp показать человеку.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Без окна и без сети: ссылка → сайт, манифест yt-dlp → короткий список качеств, каждое из которых склейка точно
// соберёт без перекодирования. MP4 берёт H.264/HEVC со звуком AAC, WebM — VP9/Opus; AV1 у YouTube лежит в MP4,
// а склейка MP4 его не пишет, — такие форматы видны только в полном списке. Звук подбирается из того же семейства.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SysDeck.Downloads
{
    internal enum MdSite { None, Youtube, Twitch, Other }

    // Одна строка списка качеств: вариант видео и подобранный к нему звук, или только звук.
    internal sealed class MdPickQuality
    {
        public string Label = "";
        public MdVariant Variant;                    // всегда не null: движок без варианта запись не начнёт
        public MdTrack Audio;                        // null — звук внутри варианта (или его нет)
        public bool AudioOnly;
        public MdOutput Output = MdOutput.Auto;      // для «только звук» — M4A или MP3, иначе выбор человека
        public long SizeBytes = -1;                  // -1 — неизвестен (трансляция, нет ни размера, ни битрейта)
        public string Blocked = "";                  // не пусто — заведомо не соберётся, причина для человека
    }

    internal static class MdPick
    {
        // Первая непустая строка; без схемы, но с узнаваемым адресом («youtube.com/…», «www.…») — https://.
        // Не ссылка http/https — пустая строка.
        public static string Normalize(string text)
        {
            string s = "";
            foreach (string line in (text ?? "").Split('\n'))
            {
                s = line.Trim().Trim('<', '>', '"', '\'', ' ', '\t', '\r');
                if (s.Length > 0) break;
            }
            if (s.Length == 0 || s.Length > 8192) return "";
            if (s.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                string low = s.ToLowerInvariant();
                if (low.StartsWith("www.", StringComparison.Ordinal) || low.StartsWith("m.", StringComparison.Ordinal) || KnownBare(low)) s = "https://" + s;
            }
            foreach (char c in s) if (c <= ' ' || c == 0x7F) return "";
            Uri u;
            if (!Uri.TryCreate(s, UriKind.Absolute, out u) || u.Host.Length == 0) return "";
            return DlHttp.IsAllowedScheme(s) ? s : "";
        }

        private static bool KnownBare(string low)
        {
            foreach (string h in new[] { "youtube.com/", "youtu.be/", "music.youtube.com/", "twitch.tv/", "clips.twitch.tv/" })
                if (low.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }

        public static MdSite SiteOf(string url)
        {
            Uri u;
            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out u)) return MdSite.None;
            if (MdYtdlp.IsYoutube(url)) return MdSite.Youtube;
            string h = u.Host.ToLowerInvariant();
            if (h == "twitch.tv" || h.EndsWith(".twitch.tv", StringComparison.Ordinal)) return MdSite.Twitch;
            return MdSite.Other;
        }

        // Что за страница — до запуска yt-dlp, по одному адресу. Пусто — сказать нечего.
        public static string Describe(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "";
            string path = u.AbsolutePath.ToLowerInvariant();
            string query = u.Query.ToLowerInvariant();
            switch (SiteOf(url))
            {
                case MdSite.Youtube:
                    if (path.StartsWith("/playlist", StringComparison.Ordinal))
                        return Tr.S("YouTube · плейлист — целиком не скачивается, откройте нужное видео и вставьте его ссылку",
                                    "YouTube · a playlist — it is not downloaded whole; open the video you need and paste its link");
                    if (path.StartsWith("/shorts/", StringComparison.Ordinal)) return "YouTube · Shorts";
                    if (path.StartsWith("/live/", StringComparison.Ordinal)) return Tr.S("YouTube · трансляция или её запись", "YouTube · a live stream or its recording");
                    if (query.Contains("list=")) return Tr.S("YouTube · видео (из плейлиста берётся только оно)", "YouTube · a video (only this one from the playlist)");
                    return Tr.S("YouTube · видео", "YouTube · a video");
                case MdSite.Twitch:
                    if (u.Host.ToLowerInvariant().StartsWith("clips.", StringComparison.Ordinal) || path.Contains("/clip/"))
                        return Tr.S("Twitch · клип", "Twitch · a clip");
                    if (path.StartsWith("/videos/", StringComparison.Ordinal)) return Tr.S("Twitch · запись трансляции", "Twitch · a past broadcast");
                    string[] seg = path.Trim('/').Split('/');
                    if (seg.Length == 1 && seg[0].Length > 0)
                        return Tr.S("Twitch · канал — если он сейчас в эфире, пойдёт запись трансляции",
                                    "Twitch · a channel — if it is live now, the broadcast gets recorded");
                    return "Twitch";
                case MdSite.Other:
                    return u.Host;
                default:
                    return "";
            }
        }

        // ---------- качества ----------
        // Чем выше, тем лучше подходит для «Авто»: H.264 играет везде, VP9 — в WebM, HEVC — в MP4 не на всех системах.
        private static int CodecRank(MdTrack t)
        {
            string c = (t.Codec ?? "").ToLowerInvariant();
            if (c.StartsWith("avc", StringComparison.Ordinal) || c.StartsWith("h264", StringComparison.Ordinal)) return 4;
            if (c.StartsWith("vp09", StringComparison.Ordinal) || c.StartsWith("vp9", StringComparison.Ordinal)) return 3;
            if (c.StartsWith("hvc1", StringComparison.Ordinal) || c.StartsWith("hev1", StringComparison.Ordinal) || c.StartsWith("h265", StringComparison.Ordinal)) return 2;
            if (c.Length == 0) return 1;                 // muxed поток без описания кодека (HLS Twitch) — склейка разберёт сама
            return 0;
        }

        private static bool IsAv1(MdTrack t)
        {
            return (t.Codec ?? "").ToLowerInvariant().StartsWith("av01", StringComparison.Ordinal);
        }

        private static bool Mp4Family(MdLayout l)
        {
            return l == MdLayout.Mp4 || l == MdLayout.Fmp4 || l == MdLayout.Ts || l == MdLayout.Adts;
        }

        // Ложится ли дорожка видео в выбранный контейнер без перекодирования.
        public static bool VideoFits(MdTrack t, MdOutput output)
        {
            if (t == null) return false;
            if (output == MdOutput.WebM) return t.Layout == MdLayout.WebM;
            bool mp4 = Mp4Family(t.Layout) && !IsAv1(t) && !(t.Codec ?? "").ToLowerInvariant().StartsWith("vp", StringComparison.Ordinal);
            if (output == MdOutput.Mp4) return mp4;
            return mp4 || t.Layout == MdLayout.WebM || t.Layout == MdLayout.Unknown && !IsAv1(t);
        }

        // Звук к варианту: то же семейство контейнеров, что у видео; среди них — дорожка по умолчанию, потом битрейт.
        public static MdTrack AudioFor(MdManifest m, MdVariant v)
        {
            if (m == null || v == null || v.Main == null || v.AudioGroup.Length == 0) return null;
            bool webm = v.Main.Layout == MdLayout.WebM;
            MdTrack best = null;
            foreach (MdTrack a in m.Audio)
            {
                if (a.GroupId != v.AudioGroup || (a.Layout == MdLayout.WebM) != webm) continue;
                if (Better(a, best)) best = a;
            }
            if (best != null) return best;
            foreach (MdTrack a in m.Audio)
                if (a.GroupId == v.AudioGroup && (best == null || a.Bandwidth > best.Bandwidth)) best = a;
            return best;
        }

        // Звук для «только звук»: MP4-семейство (M4A и MP3 из него собираются), дорожка по умолчанию, потом битрейт.
        private static MdTrack AudioOnlyTrack(MdManifest m)
        {
            MdTrack best = null;
            foreach (MdTrack a in m.Audio)
            {
                if (!Mp4Family(a.Layout)) continue;
                if (Better(a, best)) best = a;
            }
            return best;
        }

        // Поток HLS (m3u8). У YouTube такие форматы — сегменты fMP4, а не TS, и склейка их не собирает; там, где есть
        // прямая ссылка на те же байты, берётся она. У Twitch прямых нет — там HLS всё, и выбор от этого не меняется.
        private static bool Hls(MdTrack t)
        {
            return t.Layout == MdLayout.Ts;
        }

        // Видео одной высоты и частоты: кодец по совместимости, затем прямая ссылка, затем битрейт.
        private static bool BetterVideo(MdTrack a, MdTrack have)
        {
            int ra = CodecRank(a), rb = CodecRank(have);
            if (ra != rb) return ra > rb;
            if (Hls(a) != Hls(have)) return !Hls(a);
            return a.Bandwidth > have.Bandwidth;
        }

        // Прямая ссылка; дорожка по умолчанию; затем без DRC — у YouTube «-drc» это «стабильная громкость» со сжатой динамикой, сам
        // yt-dlp по умолчанию берёт исходную; затем битрейт.
        private static bool Better(MdTrack a, MdTrack best)
        {
            if (best == null) return true;
            if (Hls(a) != Hls(best)) return !Hls(a);
            if (a.Default != best.Default) return a.Default;
            bool da = IsDrc(a), db = IsDrc(best);
            if (da != db) return !da;
            return a.Bandwidth > best.Bandwidth;
        }

        private static bool IsDrc(MdTrack t)
        {
            return (t.FormatId ?? "").EndsWith("-drc", StringComparison.OrdinalIgnoreCase)
                || (t.Name ?? "").IndexOf("DRC", StringComparison.Ordinal) >= 0;
        }

        public static long SizeOf(MdManifest m, MdTrack video, MdTrack audio)
        {
            if (m == null || m.Live) return -1;
            long total = 0;
            foreach (MdTrack t in new[] { video, audio })
            {
                if (t == null) continue;
                if (t.SizeHint > 0) total += t.SizeHint;
                else if (t.Bandwidth > 0 && m.DurationSec > 0) total += (long)(t.Bandwidth / 8.0 * m.DurationSec);
                else return -1;
            }
            return total > 0 ? total : -1;
        }

        // Список для выпадающего меню. all = false — по одной строке на высоту и частоту кадров, только то, что
        // ляжет в выбранный контейнер; all = true — все варианты, как их отдал yt-dlp. Звук — в конце, если он есть.
        public static List<MdPickQuality> Qualities(MdManifest m, MdOutput output, bool all, bool mp3Available)
        {
            List<MdPickQuality> list = new List<MdPickQuality>();
            if (m == null || m.Variants.Count == 0) return list;
            if (all)
            {
                foreach (MdVariant v in m.Variants) list.Add(Make(m, v, output, true));
            }
            else
            {
                List<string> keys = new List<string>();
                Dictionary<string, MdVariant> pick = new Dictionary<string, MdVariant>();
                foreach (MdVariant v in m.Variants)
                {
                    if (v.Main == null || v.Main.Kind == MdTrackKind.Audio || !VideoFits(v.Main, output)) continue;
                    string key = v.Height + "|" + (v.Fps > 31 ? "hi" : "lo");
                    MdVariant have;
                    if (!pick.TryGetValue(key, out have)) { keys.Add(key); pick[key] = v; continue; }
                    if (BetterVideo(v.Main, have.Main)) pick[key] = v;
                }
                foreach (string k in keys) list.Add(Make(m, pick[k], output, false));
            }
            MdTrack sound = AudioOnlyTrack(m);
            MdVariant carrier = Carrier(m);
            if (sound != null && carrier != null)
            {
                list.Add(AudioOnly(m, carrier, sound, MdOutput.M4a));
                if (mp3Available) list.Add(AudioOnly(m, carrier, sound, MdOutput.Mp3));
            }
            return list;
        }

        // Для «только звук» движку всё равно нужен вариант; сам он не скачивается, звук движок находит по id (Choose).
        // Лучше с группой звука — тогда и пропавшая при повторном извлечении дорожка заменится из той же группы.
        // У Twitch группы нет: видео и звук в одном потоке, а «audio_only» лежит отдельно.
        private static MdVariant Carrier(MdManifest m)
        {
            MdVariant best = null;
            foreach (MdVariant v in m.Variants)
            {
                if (best != null && (best.AudioGroup.Length > 0) != (v.AudioGroup.Length > 0))
                {
                    if (v.AudioGroup.Length > 0) best = v;
                    continue;
                }
                if (best == null || v.Bandwidth < best.Bandwidth) best = v;
            }
            return best;
        }

        private static MdPickQuality Make(MdManifest m, MdVariant v, MdOutput output, bool full)
        {
            MdPickQuality q = new MdPickQuality();
            q.Variant = v;
            q.Audio = AudioFor(m, v);
            q.Output = output;
            q.SizeBytes = SizeOf(m, v.Main, q.Audio);
            if (v.Main != null && Hls(v.Main) && MdYtdlp.IsYoutube(m.PageUrl.Length > 0 ? m.PageUrl : m.Url))
                q.Blocked = Tr.S("Потоки HLS у YouTube (сегменты fMP4) склейка пока не собирает — выберите формат из короткого списка.",
                                 "YouTube HLS streams (fMP4 segments) cannot be assembled yet — pick a format from the short list.");
            List<string> parts = new List<string>();
            if (full) parts.Add(v.Label.Length > 0 ? v.Label : v.Id);
            else
            {
                string head = v.Height > 0 ? v.Height + "p" + (v.Fps > 31 ? Number(Math.Round(v.Fps), "0") : "")
                            : v.Main != null && v.Main.Name.Length > 0 ? v.Main.Name : v.Id;
                parts.Add(head);
                string codec = CodecLabel(v.Main);
                if (codec.Length > 0) parts.Add(codec);
            }
            if (q.SizeBytes > 0) parts.Add("≈ " + Engine.FormatBytes(q.SizeBytes));
            q.Label = string.Join(" · ", parts.ToArray());
            return q;
        }

        private static MdPickQuality AudioOnly(MdManifest m, MdVariant carrier, MdTrack sound, MdOutput output)
        {
            MdPickQuality q = new MdPickQuality();
            q.Variant = carrier;
            q.Audio = sound;
            q.AudioOnly = true;
            q.Output = output;
            q.SizeBytes = SizeOf(m, null, sound);
            string head = output == MdOutput.Mp3 ? Tr.S("Только звук · MP3", "Audio only · MP3") : Tr.S("Только звук · M4A", "Audio only · M4A");
            if (sound.Bandwidth > 0) head += " · " + Math.Round(sound.Bandwidth / 1e3) + Tr.S(" кбит/с", " kbit/s");
            if (q.SizeBytes > 0) head += " · ≈ " + Engine.FormatBytes(q.SizeBytes);
            q.Label = head;
            return q;
        }

        private static string CodecLabel(MdTrack t)
        {
            if (t == null) return "";
            string c = (t.Codec ?? "").ToLowerInvariant();
            if (c.StartsWith("avc", StringComparison.Ordinal) || c.StartsWith("h264", StringComparison.Ordinal)) return "H.264";
            if (c.StartsWith("vp09", StringComparison.Ordinal) || c.StartsWith("vp9", StringComparison.Ordinal)) return "VP9";
            if (c.StartsWith("hvc1", StringComparison.Ordinal) || c.StartsWith("hev1", StringComparison.Ordinal) || c.StartsWith("h265", StringComparison.Ordinal)) return "HEVC";
            if (c.StartsWith("av01", StringComparison.Ordinal)) return "AV1";
            return "";
        }

        // Номер строки списка, которую выбрать сразу: лучшая не выше maxHeight из настроек (0 — без предела); все выше
        // предела — самая низкая из них, как и движок выбирает замену (Downloads.Media.cs, ChooseVariant).
        public static int DefaultIndex(List<MdPickQuality> list, int maxHeight)
        {
            int lowest = -1;
            for (int i = 0; i < list.Count; i++)
            {
                MdPickQuality q = list[i];
                if (q.AudioOnly) continue;
                lowest = i;
                int h = q.Variant != null ? q.Variant.Height : 0;
                if (maxHeight <= 0 || h <= 0 || h <= maxHeight) return i;
            }
            return lowest >= 0 ? lowest : list.Count > 0 ? 0 : -1;
        }

        // Ляжет ли выбор в контейнер — до того, как запись встанет в очередь и упадёт на склейке. null — ляжет.
        public static string Problem(MdPickQuality q)
        {
            if (q == null || q.Variant == null || q.Variant.Main == null) return Tr.S("Качество не выбрано.", "No quality is chosen.");
            if (q.Blocked.Length > 0) return q.Blocked;
            if (q.AudioOnly)
                return q.Audio != null && Mp4Family(q.Audio.Layout) ? null
                     : Tr.S("Звук этого видео лежит в WebM — в M4A и MP3 он без перекодирования не собирается.",
                            "This video's audio is in WebM — it cannot go into M4A or MP3 without transcoding.");
            if (!VideoFits(q.Variant.Main, q.Output))
                return q.Output == MdOutput.Mp4
                    ? Tr.S("Это качество в MP4 без перекодирования не собирается — выберите «по дорожкам» или WebM.",
                           "This quality cannot go into MP4 without transcoding — choose “match the tracks” or WebM.")
                    : q.Output == MdOutput.WebM
                    ? Tr.S("Это качество не в WebM — выберите «по дорожкам» или MP4.", "This quality is not WebM — choose “match the tracks” or MP4.")
                    : Tr.S("Этот формат (AV1 в MP4) склейка без перекодирования не собирает — выберите другое качество.",
                           "This format (AV1 in MP4) cannot be assembled without transcoding — pick another quality.");
            if (q.Audio != null && (q.Audio.Layout == MdLayout.WebM) != (q.Variant.Main.Layout == MdLayout.WebM))
                return Tr.S("Видео и звук этого качества в разных контейнерах — без перекодирования не собираются. Выберите другое качество.",
                            "The video and audio of this quality sit in different containers and cannot be assembled without transcoding. Pick another quality.");
            return null;
        }

        // Субтитры для списка: все загруженные автором и автоматические только на языке интерфейса, английском и
        // исходном («-orig»): YouTube отдаёт автоперевод на ~150 языков, такой список выбрать нельзя.
        public static List<MdTrack> Subtitles(MdManifest m)
        {
            List<MdTrack> list = new List<MdTrack>();
            if (m == null) return list;
            string ui = Tr.En ? "en" : "ru";
            foreach (MdTrack s in m.Subtitles)
            {
                if (!s.Id.StartsWith("auto-", StringComparison.Ordinal)) { list.Add(s); continue; }
                string lang = (s.Language ?? "").ToLowerInvariant();
                if (lang == ui || lang == "en" || lang.EndsWith("-orig", StringComparison.Ordinal)) list.Add(s);
            }
            return list;
        }

        // Субтитры, выбранные сразу: при включённой настройке — загруженные автором на языке интерфейса.
        public static int DefaultSubtitle(List<MdTrack> list, bool wanted)
        {
            if (!wanted) return -1;
            string ui = Tr.En ? "en" : "ru";
            for (int i = 0; i < list.Count; i++)
            {
                string lang = (list[i].Language ?? "").ToLowerInvariant();
                if (!list[i].Id.StartsWith("auto-", StringComparison.Ordinal) && (lang == ui || lang.StartsWith(ui + "-", StringComparison.Ordinal))) return i;
            }
            return -1;
        }

        public static string Duration(double sec)
        {
            if (sec <= 0 || double.IsNaN(sec) || double.IsInfinity(sec)) return "";
            long s = (long)Math.Round(sec);
            return s >= 3600 ? (s / 3600) + ":" + (s / 60 % 60).ToString("00") + ":" + (s % 60).ToString("00")
                             : (s / 60) + ":" + (s % 60).ToString("00");
        }

        private static string Number(double v, string format)
        {
            return v.ToString(format, CultureInfo.InvariantCulture);
        }

        // Команда процессу загрузок: адрес страницы и сделанный в окне выбор. Движок при устаревших ссылках извлекает
        // форматы заново и находит те же дорожки по их id.
        public static JVal Command(MdManifest m, MdPickQuality q, MdTrack subtitle, string folder, bool paused, bool allowDuplicate)
        {
            JVal c = DlClient.Command("addMedia");
            string page = m.PageUrl.Length > 0 ? m.PageUrl : m.Url;
            c.Set("url", DlJson.S(page));
            c.Set("kind", DlJson.S("page"));
            c.Set("source", DlJson.S("manual"));
            c.Set("title", DlJson.S(m.Title ?? ""));
            c.Set("variantId", DlJson.S(q.Variant.Id));
            if (q.Audio != null) c.Set("audioId", DlJson.S(q.Audio.Id));
            if (subtitle != null) c.Set("subtitleIds", DlJson.Strings(new List<string> { subtitle.Id }));
            c.Set("output", DlJson.S(q.Output.ToString()));
            if (!string.IsNullOrEmpty(folder)) c.Set("folder", DlJson.S(folder));
            if (paused) c.Set("paused", DlJson.B(true));
            if (allowDuplicate) c.Set("allowDuplicate", DlJson.B(true));
            return c;
        }
    }
}
