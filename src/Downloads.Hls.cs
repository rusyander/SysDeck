// Windows Process Cleaner — «Загрузки», видео: разбор плейлистов HLS (RFC 8216) — master и media.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Разборщик не ходит в сеть: текст плейлиста уже скачан (MdLoader), относительные адреса считаются от адреса плейлиста
// после редиректов. Ничего не бросает наружу: ошибка — текст в error или MdManifest.Refused. SAMPLE-AES и ключи
// с KEYFORMAT, отличным от identity (FairPlay, Widevine, PlayReady), — отказ: это DRM, его не скачиваем никогда.
// IV у ключа без атрибута IV не выдумывается здесь: null в MdKey.Iv, движок берёт номер сегмента (RFC 8216 §5.2).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WindowsProcessCleaner.Downloads
{
    internal static class MdHls
    {
        public const int MaxText = 16 * 1024 * 1024;
        public const int MaxSegments = 200000;
        public const int MaxVariants = 1000;

        public static string DrmRefusal
        {
            get { return Tr.S("видео защищено DRM — такое не скачивается", "the video is DRM-protected — it cannot be downloaded"); }
        }

        // Текст начинается с #EXTM3U (BOM и пробелы до него допустимы).
        public static bool LooksLikePlaylist(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            while (i < text.Length && (text[i] == '\uFEFF' || char.IsWhiteSpace(text[i]))) i++;
            return string.CompareOrdinal(text, i, "#EXTM3U", 0, 7) == 0;
        }

        // ------------------------------------------------------------------ //
        //  master
        // ------------------------------------------------------------------ //
        public static MdManifest ParseMaster(string text, string url)
        {
            string error;
            return ParseMaster(text, url, out error);
        }

        // error не пусто — плейлист не разобран (тогда и Refused заполнен тем же текстом: скачать нельзя).
        internal static MdManifest ParseMaster(string text, string url, out string error)
        {
            MdManifest m = new MdManifest();
            m.Source = MdSource.Hls;
            m.Url = url ?? "";
            error = null;
            try
            {
                MasterCore(text, url ?? "", m, out error);
            }
            catch (Exception ex)
            {
                error = Tr.S("плейлист HLS не разобран: ", "the HLS playlist is not parsed: ") + ex.Message;
            }
            if (error != null && m.Refused.Length == 0) m.Refused = error;
            return m;
        }

        private static void MasterCore(string text, string url, MdManifest m, out string error)
        {
            error = null;
            if (text == null || text.Length > MaxText) { error = Tr.S("плейлист слишком большой", "the playlist is too large"); return; }
            if (!LooksLikePlaylist(text)) { error = Tr.S("это не плейлист HLS", "this is not an HLS playlist"); return; }
            List<string> lines = Lines(text);
            bool media = false, master = false;
            foreach (string l in lines)
            {
                if (l.StartsWith("#EXTINF", StringComparison.Ordinal) || l.StartsWith("#EXT-X-TARGETDURATION", StringComparison.Ordinal)) media = true;
                if (l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal)) master = true;
            }

            if (media && !master)
            {
                // Прислали сразу media-плейлист: один вариант, сегменты уже известны.
                MdTrack t = new MdTrack();
                t.Id = "main";
                t.Kind = MdTrackKind.Muxed;
                t.Url = url;
                string err;
                bool refused;
                if (!ParseMedia(text, url, t, out err, out refused))
                {
                    error = err;
                    if (refused) m.Refused = err;
                    return;
                }
                MdVariant v = new MdVariant();
                v.Id = "main";
                v.Main = t;
                v.Label = Label(0, 0, 0);
                m.Variants.Add(v);
                m.Live = t.Live;
                double sum = 0;
                foreach (MdSegment s in t.Segments) sum += s.Duration;
                m.DurationSec = t.Live ? 0 : sum;
                return;
            }

            Dictionary<string, string> pending = null;
            Dictionary<string, bool> groupHasUri = new Dictionary<string, bool>(StringComparer.Ordinal);
            HashSet<string> usedIds = new HashSet<string>(StringComparer.Ordinal);
            List<MdVariant> variants = new List<MdVariant>();
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    string tag, value;
                    SplitTag(line, out tag, out value);
                    if (tag == "#EXT-X-STREAM-INF") pending = Attrs(value);
                    else if (tag == "#EXT-X-MEDIA")
                    {
                        Dictionary<string, string> a = Attrs(value);
                        string type = Get(a, "TYPE").ToUpperInvariant();
                        string group = Get(a, "GROUP-ID");
                        string uri = Get(a, "URI");
                        if (type == "AUDIO")
                        {
                            if (uri.Length == 0) { if (!groupHasUri.ContainsKey(group)) groupHasUri[group] = false; continue; }
                            groupHasUri[group] = true;
                            MdTrack t = Rendition(a, url, MdTrackKind.Audio, "a", usedIds);
                            m.Audio.Add(t);
                        }
                        else if (type == "SUBTITLES" && uri.Length > 0)
                        {
                            MdTrack t = Rendition(a, url, MdTrackKind.Subtitles, "s", usedIds);
                            t.Layout = MdLayout.Vtt;
                            m.Subtitles.Add(t);
                        }
                    }
                    else if (tag == "#EXT-X-SESSION-KEY")
                    {
                        Dictionary<string, string> a = Attrs(value);
                        string method = Get(a, "METHOD").ToUpperInvariant();
                        if (method.Length > 0 && method != "NONE" && (!IsIdentity(Get(a, "KEYFORMAT")) || method != "AES-128"))
                            m.Refused = DrmRefusal;
                    }
                    continue;
                }
                if (pending == null) continue;
                if (variants.Count >= MaxVariants) break;
                variants.Add(Variant(pending, Resolve(url, line), usedIds));
                pending = null;
            }

            // Кодек звука дорожки группы — из CODECS вариантов, которые на неё ссылаются.
            foreach (MdVariant v in variants)
            {
                bool separate;
                if (v.AudioGroup.Length > 0 && groupHasUri.TryGetValue(v.AudioGroup, out separate) && separate)
                {
                    if (HasVideoCodec(v.Codecs) || v.Height > 0) v.Main.Kind = MdTrackKind.Video;
                    string ac = CodecPart(v.Codecs, false);
                    foreach (MdTrack t in m.Audio)
                        if (t.GroupId == v.AudioGroup && t.Codec.Length == 0) t.Codec = ac;
                    if (v.Main.Kind == MdTrackKind.Video) v.Main.Codec = CodecPart(v.Codecs, true);
                }
                else
                {
                    v.AudioGroup = "";
                    if (!HasVideoCodec(v.Codecs) && v.Height == 0 && v.Codecs.Length > 0) v.Main.Kind = MdTrackKind.Audio;
                }
            }
            List<MdVariant> ordered = new List<MdVariant>(variants);
            ordered.Sort(delegate(MdVariant a, MdVariant b)
            {
                if (a.Height != b.Height) return b.Height.CompareTo(a.Height);
                if (a.Bandwidth != b.Bandwidth) return b.Bandwidth.CompareTo(a.Bandwidth);
                return variants.IndexOf(a).CompareTo(variants.IndexOf(b));
            });
            m.Variants.AddRange(ordered);
            if (m.Variants.Count == 0 && m.Refused.Length == 0) error = Tr.S("в плейлисте нет вариантов", "the playlist has no variants");
        }

        private static MdVariant Variant(Dictionary<string, string> a, string uri, HashSet<string> usedIds)
        {
            MdVariant v = new MdVariant();
            long bw;
            if (long.TryParse(Get(a, "BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out bw)) v.Bandwidth = bw;
            string res = Get(a, "RESOLUTION");
            int x = res.IndexOfAny(new[] { 'x', 'X' });
            int w, h;
            if (x > 0 && int.TryParse(res.Substring(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out w)
                && int.TryParse(res.Substring(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out h))
            {
                v.Width = w;
                v.Height = h;
            }
            double fps;
            if (double.TryParse(Get(a, "FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out fps) && fps > 0 && fps < 1000) v.Fps = fps;
            v.Codecs = Get(a, "CODECS");
            v.AudioGroup = Get(a, "AUDIO");
            v.SubtitleGroup = Get(a, "SUBTITLES");
            string id = "v" + v.Height.ToString(CultureInfo.InvariantCulture) + "p-" + (v.Bandwidth / 1000).ToString(CultureInfo.InvariantCulture) + "k";
            v.Id = UniqueId(id, usedIds);
            v.Label = Label(v.Height, v.Fps, v.Bandwidth);
            MdTrack t = new MdTrack();
            t.Id = v.Id;
            t.Kind = MdTrackKind.Muxed;
            t.Codec = v.Codecs;
            t.Bandwidth = v.Bandwidth;
            t.Width = v.Width;
            t.Height = v.Height;
            t.Fps = v.Fps;
            t.Url = uri;
            v.Main = t;
            return v;
        }

        private static MdTrack Rendition(Dictionary<string, string> a, string url, MdTrackKind kind, string prefix, HashSet<string> usedIds)
        {
            MdTrack t = new MdTrack();
            t.Kind = kind;
            t.GroupId = Get(a, "GROUP-ID");
            t.Name = Get(a, "NAME");
            t.Language = Get(a, "LANGUAGE");
            t.Default = Get(a, "DEFAULT").Equals("YES", StringComparison.OrdinalIgnoreCase);
            t.Url = Resolve(url, Get(a, "URI"));
            string tail = t.Language.Length > 0 ? t.Language + "-" + t.Name : t.Name;
            t.Id = UniqueId(prefix + "-" + Slug(t.GroupId) + "-" + Slug(tail), usedIds);
            return t;
        }

        // «720p · 30 к/с · 4,2 Мбит/с»
        internal static string Label(int height, double fps, long bandwidth)
        {
            List<string> parts = new List<string>();
            if (height > 0) parts.Add(height.ToString(CultureInfo.InvariantCulture) + "p");
            if (fps > 0) parts.Add(Math.Round(fps).ToString(CultureInfo.InvariantCulture) + Tr.S(" к/с", " fps"));
            if (bandwidth > 0)
            {
                string n;
                if (bandwidth >= 1000000) n = (bandwidth / 1000000.0).ToString("0.0", CultureInfo.InvariantCulture) + Tr.S(" Мбит/с", " Mbit/s");
                else n = (bandwidth / 1000).ToString(CultureInfo.InvariantCulture) + Tr.S(" кбит/с", " kbit/s");
                parts.Add(Tr.S(n.Replace('.', ','), n));
            }
            return parts.Count == 0 ? Tr.S("поток", "stream") : string.Join(" · ", parts.ToArray());
        }

        // ------------------------------------------------------------------ //
        //  media
        // ------------------------------------------------------------------ //
        public static bool ParseMedia(string text, string url, MdTrack into, out string error)
        {
            bool refused;
            return ParseMedia(text, url, into, out error, out refused);
        }

        // refused — отказ по DRM (текст в error); иначе false — плейлист битый.
        internal static bool ParseMedia(string text, string url, MdTrack into, out string error, out bool refused)
        {
            error = null;
            refused = false;
            if (into == null) { error = "no track"; return false; }
            try
            {
                return MediaCore(text, url ?? "", into, out error, out refused);
            }
            catch (Exception ex)
            {
                into.Segments.Clear();
                error = Tr.S("плейлист HLS не разобран: ", "the HLS playlist is not parsed: ") + ex.Message;
                return false;
            }
        }

        private static bool MediaCore(string text, string url, MdTrack into, out string error, out bool refused)
        {
            error = null;
            refused = false;
            into.Segments.Clear();
            if (text == null || text.Length > MaxText) { error = Tr.S("плейлист слишком большой", "the playlist is too large"); return false; }
            if (!LooksLikePlaylist(text)) { error = Tr.S("это не плейлист HLS", "this is not an HLS playlist"); return false; }

            long seq = 0;
            double dur = 0;
            bool disc = false, gap = false, endlist = false, drmActive = false;
            bool keyGroupIdentity = false, keyGroupDrm = false;
            string type = "";
            MdKey key = null;
            string initUrl = "";
            long initOff = -1, initLen = -1;
            bool hasRange = false;
            long rangeLen = -1, rangeOff = -1;
            string lastRangeUrl = null;
            long lastRangeEnd = -1;

            foreach (string line in Lines(text))
            {
                if (line.Length == 0) continue;
                if (line[0] == '#')
                {
                    if (!line.StartsWith("#EXT", StringComparison.Ordinal)) continue;
                    string tag, value;
                    SplitTag(line, out tag, out value);
                    switch (tag)
                    {
                        case "#EXT-X-TARGETDURATION":
                            {
                                double td;
                                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out td) && td > 0 && td < 86400) into.TargetDuration = td;
                                break;
                            }
                        case "#EXT-X-MEDIA-SEQUENCE":
                            {
                                long ms;
                                if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out ms)) seq = ms;
                                break;
                            }
                        case "#EXT-X-PLAYLIST-TYPE": type = value.Trim().ToUpperInvariant(); break;
                        case "#EXT-X-ENDLIST": endlist = true; break;
                        case "#EXT-X-DISCONTINUITY": disc = true; break;
                        case "#EXT-X-GAP": gap = true; break;
                        case "#EXT-X-STREAM-INF":
                            error = Tr.S("это master-плейлист, а не плейлист сегментов", "this is a master playlist, not a segment playlist");
                            return false;
                        case "#EXTINF":
                            {
                                string d = value;
                                int comma = d.IndexOf(',');
                                if (comma >= 0) d = d.Substring(0, comma);
                                double v;
                                dur = double.TryParse(d.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v < 86400 ? v : 0;
                                break;
                            }
                        case "#EXT-X-BYTERANGE":
                            if (!ParseRange(value, out rangeLen, out rangeOff)) { error = Tr.S("неверный EXT-X-BYTERANGE: ", "bad EXT-X-BYTERANGE: ") + value; return false; }
                            hasRange = true;
                            break;
                        case "#EXT-X-MAP":
                            {
                                Dictionary<string, string> a = Attrs(value);
                                string uri = Get(a, "URI");
                                if (uri.Length == 0) { error = Tr.S("EXT-X-MAP без URI", "EXT-X-MAP without URI"); return false; }
                                initUrl = Resolve(url, uri);
                                initOff = -1;
                                initLen = -1;
                                string br = Get(a, "BYTERANGE");
                                if (br.Length > 0)
                                {
                                    long o;
                                    if (!ParseRange(br, out initLen, out o)) { error = Tr.S("неверный BYTERANGE у EXT-X-MAP", "bad BYTERANGE of EXT-X-MAP"); return false; }
                                    initOff = o < 0 ? 0 : o;
                                }
                                break;
                            }
                        case "#EXT-X-KEY":
                            {
                                Dictionary<string, string> a = Attrs(value);
                                string method = Get(a, "METHOD").ToUpperInvariant();
                                if (!IsIdentity(Get(a, "KEYFORMAT")))
                                {
                                    // Ключ для FairPlay/Widevine/PlayReady. Рядом может стоять обычный identity-ключ — тогда им и качаем.
                                    if (method != "NONE") keyGroupDrm = true;
                                    break;
                                }
                                keyGroupIdentity = true;
                                if (method == "NONE" || method.Length == 0) key = null;
                                else if (method == "AES-128")
                                {
                                    string uri = Get(a, "URI");
                                    if (uri.Length == 0) { error = Tr.S("ключ AES-128 без URI", "an AES-128 key without URI"); return false; }
                                    MdKey k = new MdKey();
                                    k.Method = "AES-128";
                                    k.Uri = Resolve(url, uri);
                                    string iv = Get(a, "IV");
                                    if (iv.Length > 0)
                                    {
                                        k.Iv = ParseIv(iv);
                                        if (k.Iv == null) { error = Tr.S("неверный IV ключа: ", "bad key IV: ") + iv; return false; }
                                    }
                                    key = k;
                                }
                                else
                                {
                                    refused = true;
                                    error = DrmRefusal + " (" + method + ")";
                                    return false;
                                }
                                break;
                            }
                    }
                    continue;
                }

                // Строка адреса — сегмент.
                if (keyGroupDrm && !keyGroupIdentity) drmActive = true;
                else if (keyGroupIdentity) drmActive = false;
                keyGroupDrm = keyGroupIdentity = false;
                if (drmActive)
                {
                    refused = true;
                    error = DrmRefusal;
                    into.Segments.Clear();
                    return false;
                }
                MdSegment s = new MdSegment();
                s.Url = Resolve(url, line);
                s.Duration = dur;
                s.Sequence = seq;
                s.Discontinuity = disc;
                s.Key = key;
                s.InitUrl = initUrl;
                s.InitOffset = initOff;
                s.InitLength = initLen;
                if (hasRange)
                {
                    long off = rangeOff;
                    if (off < 0)
                    {
                        // Без смещения — продолжение предыдущего диапазона того же файла (RFC 8216 §4.3.2.2).
                        if (lastRangeUrl == null || lastRangeUrl != s.Url)
                        {
                            error = Tr.S("EXT-X-BYTERANGE без смещения не продолжает предыдущий диапазон", "EXT-X-BYTERANGE without an offset does not continue the previous range");
                            into.Segments.Clear();
                            return false;
                        }
                        off = lastRangeEnd;
                    }
                    s.Offset = off;
                    s.Length = rangeLen;
                    lastRangeUrl = s.Url;
                    lastRangeEnd = off + rangeLen;
                }
                else lastRangeUrl = null;
                if (!gap)
                {
                    if (into.Segments.Count >= MaxSegments) { error = Tr.S("слишком много сегментов", "too many segments"); into.Segments.Clear(); return false; }
                    into.Segments.Add(s);
                }
                seq++;
                dur = 0;
                disc = gap = hasRange = false;
            }

            into.Live = !endlist && type != "VOD";
            if (into.Layout == MdLayout.Unknown || into.Layout == MdLayout.Ts)
            {
                if (into.Kind == MdTrackKind.Subtitles) into.Layout = MdLayout.Vtt;
                else if (into.Segments.Count > 0) into.Layout = into.Segments[0].InitUrl.Length > 0 ? MdLayout.Fmp4 : LayoutByName(into.Segments[0].Url);
            }
            if (into.Segments.Count == 0 && !into.Live)
            {
                error = Tr.S("в плейлисте нет сегментов", "the playlist has no segments");
                return false;
            }
            return true;
        }

        // «len[@off]»; off = -1, если смещения нет.
        internal static bool ParseRange(string value, out long length, out long offset)
        {
            length = -1;
            offset = -1;
            string v = (value ?? "").Trim().Trim('"');
            int at = v.IndexOf('@');
            string l = at < 0 ? v : v.Substring(0, at);
            if (!long.TryParse(l, NumberStyles.None, CultureInfo.InvariantCulture, out length) || length <= 0) return false;
            if (at >= 0 && (!long.TryParse(v.Substring(at + 1), NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset < 0)) return false;
            return true;
        }

        // «0x0F0E…» → 16 байт big-endian, короче — дополняется нулями слева.
        internal static byte[] ParseIv(string text)
        {
            string h = (text ?? "").Trim();
            if (h.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) h = h.Substring(2);
            if (h.Length == 0 || h.Length > 32) return null;
            h = h.PadLeft(32, '0');
            byte[] iv = new byte[16];
            for (int i = 0; i < 16; i++)
            {
                int hi = Hex(h[2 * i]), lo = Hex(h[2 * i + 1]);
                if (hi < 0 || lo < 0) return null;
                iv[i] = (byte)(hi * 16 + lo);
            }
            return iv;
        }

        private static int Hex(char c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        internal static bool IsIdentity(string keyFormat)
        {
            string f = (keyFormat ?? "").Trim();
            return f.Length == 0 || f.Equals("identity", StringComparison.OrdinalIgnoreCase);
        }

        internal static MdLayout LayoutByName(string url)
        {
            string path = url ?? "";
            int q = path.IndexOfAny(new[] { '?', '#' });
            if (q >= 0) path = path.Substring(0, q);
            int dot = path.LastIndexOf('.');
            int slash = path.LastIndexOf('/');
            string ext = dot > slash && dot >= 0 ? path.Substring(dot).ToLowerInvariant() : "";
            switch (ext)
            {
                case ".aac": return MdLayout.Adts;
                case ".mp3": return MdLayout.Mp3;
                case ".vtt":
                case ".webvtt": return MdLayout.Vtt;
                case ".m4s":
                case ".mp4":
                case ".m4v":
                case ".m4a":
                case ".cmfv":
                case ".cmfa": return MdLayout.Fmp4;
                case ".webm": return MdLayout.WebM;
                default: return MdLayout.Ts;
            }
        }

        // ------------------------------------------------------------------ //
        //  общие мелочи разбора
        // ------------------------------------------------------------------ //
        internal static List<string> Lines(string text)
        {
            List<string> list = new List<string>();
            foreach (string raw in text.Split('\n'))
            {
                string l = raw.Trim().TrimStart('\uFEFF');
                list.Add(l);
            }
            return list;
        }

        private static void SplitTag(string line, out string tag, out string value)
        {
            int colon = line.IndexOf(':');
            tag = colon < 0 ? line.Trim() : line.Substring(0, colon).Trim();
            value = colon < 0 ? "" : line.Substring(colon + 1).Trim();
        }

        // Список атрибутов «KEY=value,KEY="value, с запятой"».
        internal static Dictionary<string, string> Attrs(string s)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ',' || s[i] == ' ')) i++;
                int eq = s.IndexOf('=', i);
                if (eq < 0) break;
                string name = s.Substring(i, eq - i).Trim();
                i = eq + 1;
                string val;
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    val = s.Substring(i + 1, end - i - 1);
                    i = Math.Min(s.Length, end + 1);
                    while (i < s.Length && s[i] != ',') i++;
                }
                else
                {
                    int comma = s.IndexOf(',', i);
                    if (comma < 0) comma = s.Length;
                    val = s.Substring(i, comma - i).Trim();
                    i = comma;
                }
                if (name.Length > 0) d[name] = val;
            }
            return d;
        }

        private static string Get(Dictionary<string, string> a, string name)
        {
            string v;
            return a.TryGetValue(name, out v) ? v ?? "" : "";
        }

        // Относительный адрес — от адреса плейлиста; схемы, кроме http(s), остаются как есть (загрузчик их отвергнет).
        internal static string Resolve(string baseUrl, string rel)
        {
            string r = (rel ?? "").Trim();
            Uri abs;
            if (Uri.TryCreate(r, UriKind.Absolute, out abs) && (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps)) return abs.AbsoluteUri;
            if (r.IndexOf("://", StringComparison.Ordinal) > 0) return r;
            Uri b;
            if (Uri.TryCreate(baseUrl ?? "", UriKind.Absolute, out b) && Uri.TryCreate(b, r, out abs)) return abs.AbsoluteUri;
            return r;
        }

        internal static string Slug(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s ?? "")
            {
                if (sb.Length >= 40) break;
                sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '_') ? c : '_');
            }
            return sb.Length == 0 ? "x" : sb.ToString();
        }

        private static string UniqueId(string id, HashSet<string> used)
        {
            string c = id;
            for (int n = 2; used.Contains(c); n++) c = id + "-" + n.ToString(CultureInfo.InvariantCulture);
            used.Add(c);
            return c;
        }

        private static readonly string[] VideoCodecs = { "avc1", "avc3", "hvc1", "hev1", "vp09", "vp9", "vp8", "av01", "dvh1", "dvhe", "mp4v" };

        internal static bool HasVideoCodec(string codecs)
        {
            return CodecPart(codecs, true).Length > 0;
        }

        // Видео- или звуковая часть списка CODECS.
        internal static string CodecPart(string codecs, bool video)
        {
            List<string> picked = new List<string>();
            foreach (string raw in (codecs ?? "").Split(','))
            {
                string c = raw.Trim();
                if (c.Length == 0) continue;
                bool isVideo = false;
                foreach (string p in VideoCodecs) if (c.StartsWith(p, StringComparison.OrdinalIgnoreCase)) isVideo = true;
                if (isVideo == video) picked.Add(c);
            }
            return string.Join(",", picked.ToArray());
        }
    }
}
