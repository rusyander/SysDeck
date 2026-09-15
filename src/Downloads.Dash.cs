// SysDeck — «Загрузки», видео: разбор манифестов MPEG-DASH (MPD).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// XML читается XmlReader без DTD и без внешних сущностей (XmlResolver = null): манифест приходит из сети.
// Сегменты раскрываются здесь же: SegmentTemplate ($Number$/$Time$/$RepresentationID$/$Bandwidth$, ширина %05d),
// SegmentTimeline (r = -1 — до следующего S@t или конца периода), SegmentList (mediaRange). SegmentBase с indexRange
// сегментов не даёт — дорожка помечается, MdLoader читает sidx диапазонным запросом. Любой ContentProtection — отказ:
// зашифрованное DRM видео не скачиваем. Номер в MdSegment.Sequence — $Number$, а у шаблона только с $Time$ — время:
// у трансляции окно сдвигается, и лишь эти ключи не повторяются между перечитываниями.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SysDeck.Downloads
{
    internal static class MdDash
    {
        public const int MaxText = 16 * 1024 * 1024;
        public const int MaxSegments = 200000;
        private const long PeriodSeqStep = 1000000000000L;

        // Часы для окна трансляции без SegmentTimeline; тесты подставляют свои.
        internal static Func<DateTime> NowUtc = delegate { return DateTime.UtcNow; };

        // Дорожка SegmentBase: сегменты — из sidx по indexRange (заполняет MdLoader.ResolveTrack).
        internal sealed class SidxRef
        {
            public string Url = "";
            public long IndexOffset = -1, IndexLength = -1;
            public long InitOffset = -1, InitLength = -1;
        }

        private static readonly ConditionalWeakTable<MdTrack, SidxRef> SidxTable = new ConditionalWeakTable<MdTrack, SidxRef>();

        internal static SidxRef SidxOf(MdTrack t)
        {
            SidxRef r;
            return t != null && SidxTable.TryGetValue(t, out r) ? r : null;
        }

        internal static void MarkSidx(MdTrack t, SidxRef r)
        {
            SidxTable.Remove(t);
            SidxTable.Add(t, r);
        }

        public static MdManifest Parse(string xml, string url, out string error)
        {
            MdManifest m = new MdManifest();
            m.Source = MdSource.Dash;
            m.Url = url ?? "";
            error = null;
            try
            {
                ParseCore(xml, url ?? "", m, out error);
            }
            catch (Exception ex)
            {
                error = Tr.S("манифест DASH не разобран: ", "the DASH manifest is not parsed: ") + ex.Message;
            }
            if (error != null)
            {
                m.Variants.Clear();
                m.Audio.Clear();
                m.Subtitles.Clear();
            }
            return m;
        }

        private sealed class Level
        {
            public XmlNode Template, List, Base;
        }

        private static void ParseCore(string xml, string url, MdManifest m, out string error)
        {
            error = null;
            if (xml == null || xml.Length > MaxText) { error = Tr.S("манифест слишком большой", "the manifest is too large"); return; }
            XmlDocument doc = new XmlDocument();
            doc.XmlResolver = null;
            XmlReaderSettings rs = new XmlReaderSettings();
            rs.DtdProcessing = DtdProcessing.Prohibit;
            rs.XmlResolver = null;
            rs.MaxCharactersInDocument = MaxText;
            rs.IgnoreComments = true;
            rs.IgnoreProcessingInstructions = true;
            try
            {
                using (StringReader sr = new StringReader(xml))
                using (XmlReader xr = XmlReader.Create(sr, rs))
                    doc.Load(xr);
            }
            catch (XmlException ex)
            {
                error = Tr.S("манифест DASH не разобран: ", "the DASH manifest is not parsed: ") + ex.Message;
                return;
            }
            XmlElement root = doc.DocumentElement;
            if (root == null || root.LocalName != "MPD") { error = Tr.S("это не манифест DASH", "this is not a DASH manifest"); return; }

            bool dynamic = Attr(root, "type").Equals("dynamic", StringComparison.OrdinalIgnoreCase);
            double mpdDur = Iso(Attr(root, "mediaPresentationDuration"));
            double tsbd = Iso(Attr(root, "timeShiftBufferDepth"));
            double mup = Iso(Attr(root, "minimumUpdatePeriod"));
            DateTime ast = DateTime.MinValue;
            DateTime astParsed;
            if (DateTime.TryParse(Attr(root, "availabilityStartTime"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out astParsed)) ast = astParsed;
            m.Live = dynamic;
            m.DurationSec = dynamic ? 0 : Math.Max(0, mpdDur);
            string mpdBase = BaseOf(url, root);

            List<XmlNode> periods = Children(root, "Period");
            Dictionary<string, MdTrack> byId = new Dictionary<string, MdTrack>(StringComparer.Ordinal);
            List<MdTrack> order = new List<MdTrack>();
            int total = 0;
            double prevEnd = 0;
            for (int pi = 0; pi < periods.Count; pi++)
            {
                XmlNode p = periods[pi];
                string ps = Attr(p, "start");
                double start = ps.Length > 0 ? Iso(ps) : prevEnd;
                double pdur = Iso(Attr(p, "duration"));
                if (pdur <= 0)
                {
                    string nextStart = pi + 1 < periods.Count ? Attr(periods[pi + 1], "start") : "";
                    if (nextStart.Length > 0) pdur = Iso(nextStart) - start;
                    else if (mpdDur > 0) pdur = mpdDur - start;
                }
                prevEnd = start + Math.Max(0, pdur);
                string pBase = BaseOf(mpdBase, p);
                Level pl = LevelOf(p);
                int asIndex = 0;
                foreach (XmlNode aset in Children(p, "AdaptationSet"))
                {
                    asIndex++;
                    if (Children(aset, "ContentProtection").Count > 0) m.Refused = MdHls.DrmRefusal;
                    string asBase = BaseOf(pBase, aset);
                    Level al = LevelOf(aset);
                    int repIndex = 0;
                    foreach (XmlNode rep in Children(aset, "Representation"))
                    {
                        repIndex++;
                        if (Children(rep, "ContentProtection").Count > 0) m.Refused = MdHls.DrmRefusal;
                        string repBase = BaseOf(asBase, rep);
                        Level rl = LevelOf(rep);
                        string repId = Attr(rep, "id");
                        if (repId.Length == 0) repId = asIndex.ToString(CultureInfo.InvariantCulture) + "_" + repIndex.ToString(CultureInfo.InvariantCulture);
                        string mime = Inherit(rep, aset, "mimeType");
                        string codecs = Inherit(rep, aset, "codecs");
                        string contentType = Attr(aset, "contentType");
                        MdTrack t = new MdTrack();
                        t.Kind = KindOf(contentType, mime, codecs);
                        t.Layout = LayoutOf(mime, codecs, t.Kind);
                        t.Codec = codecs;
                        t.Language = Inherit(rep, aset, "lang");
                        t.Name = Inherit(rep, aset, "label");
                        long bw;
                        if (long.TryParse(Attr(rep, "bandwidth"), NumberStyles.None, CultureInfo.InvariantCulture, out bw)) t.Bandwidth = bw;
                        int w, h;
                        if (int.TryParse(Inherit(rep, aset, "width"), NumberStyles.None, CultureInfo.InvariantCulture, out w)) t.Width = w;
                        if (int.TryParse(Inherit(rep, aset, "height"), NumberStyles.None, CultureInfo.InvariantCulture, out h)) t.Height = h;
                        t.Fps = FrameRate(Inherit(rep, aset, "frameRate"));
                        t.Url = repBase;
                        t.Live = dynamic;
                        string prefix = t.Kind == MdTrackKind.Audio ? "a-" : t.Kind == MdTrackKind.Subtitles ? "s-" : "v-";
                        t.Id = prefix + MdHls.Slug(repId);

                        MdTrack target;
                        bool existing = byId.TryGetValue(t.Id, out target);
                        if (!existing) target = t;
                        string err = Segments(target, pl, al, rl, repBase, repId, t.Bandwidth, pi * PeriodSeqStep, start, pdur, dynamic, ast, tsbd, ref total);
                        if (err != null) { error = err; return; }
                        if (target.TargetDuration <= 0) target.TargetDuration = mup > 0 ? mup : MaxDuration(target);
                        if (!existing)
                        {
                            byId[t.Id] = t;
                            order.Add(t);
                        }
                    }
                }
            }

            List<MdTrack> videos = new List<MdTrack>();
            foreach (MdTrack t in order)
            {
                if (t.Kind == MdTrackKind.Audio) { t.GroupId = "audio"; m.Audio.Add(t); }
                else if (t.Kind == MdTrackKind.Subtitles) { t.GroupId = "subs"; m.Subtitles.Add(t); }
                else videos.Add(t);
            }
            List<MdTrack> mains = videos.Count > 0 ? videos : new List<MdTrack>(m.Audio);
            List<MdTrack> ordered = new List<MdTrack>(mains);
            ordered.Sort(delegate(MdTrack a, MdTrack b)
            {
                if (a.Height != b.Height) return b.Height.CompareTo(a.Height);
                if (a.Bandwidth != b.Bandwidth) return b.Bandwidth.CompareTo(a.Bandwidth);
                return mains.IndexOf(a).CompareTo(mains.IndexOf(b));
            });
            foreach (MdTrack t in ordered)
            {
                MdVariant v = new MdVariant();
                v.Id = t.Id;
                v.Main = t;
                v.Bandwidth = t.Bandwidth;
                v.Width = t.Width;
                v.Height = t.Height;
                v.Fps = t.Fps;
                v.Codecs = t.Codec;
                v.Label = MdHls.Label(t.Height, t.Fps, t.Bandwidth);
                if (videos.Count > 0 && t.Kind == MdTrackKind.Video && m.Audio.Count > 0) v.AudioGroup = "audio";
                if (m.Subtitles.Count > 0) v.SubtitleGroup = "subs";
                m.Variants.Add(v);
            }
            if (m.Variants.Count == 0 && m.Refused.Length == 0) error = Tr.S("в манифесте нет дорожек", "the manifest has no tracks");
        }

        private static double MaxDuration(MdTrack t)
        {
            double d = 0;
            foreach (MdSegment s in t.Segments) if (s.Duration > d) d = s.Duration;
            return d;
        }

        private static Level LevelOf(XmlNode n)
        {
            Level l = new Level();
            l.Template = Child(n, "SegmentTemplate");
            l.List = Child(n, "SegmentList");
            l.Base = Child(n, "SegmentBase");
            return l;
        }

        // Атрибут с ближайшего уровня (Representation → AdaptationSet → Period), где он задан.
        private static string Pick(string name, params XmlNode[] nearestFirst)
        {
            foreach (XmlNode n in nearestFirst)
            {
                if (n == null) continue;
                string v = Attr(n, name);
                if (v.Length > 0) return v;
            }
            return "";
        }

        private static XmlNode PickChild(string name, params XmlNode[] nearestFirst)
        {
            foreach (XmlNode n in nearestFirst)
            {
                if (n == null) continue;
                XmlNode c = Child(n, name);
                if (c != null) return c;
            }
            return null;
        }

        private static string Segments(MdTrack t, Level pl, Level al, Level rl, string repBase, string repId, long bandwidth, long seqBase,
                                       double periodStart, double periodDur, bool dynamic, DateTime ast, double tsbd, ref int total)
        {
            XmlNode rt = rl.Template, at = al.Template, pt = pl.Template;
            bool template = rt != null || at != null || pt != null;
            bool list = rl.List != null || al.List != null || pl.List != null;
            bool sbase = rl.Base != null || al.Base != null || pl.Base != null;
            // Ближайший к Representation вид адресации побеждает.
            if (rl.Base != null && rt == null && rl.List == null) { template = false; list = false; }
            else if (rl.List != null && rt == null) template = false;

            if (template)
            {
                long ts = ParseLong(Pick("timescale", rt, at, pt), 1);
                if (ts <= 0) ts = 1;
                long startNumber = ParseLong(Pick("startNumber", rt, at, pt), 1);
                long pto = ParseLong(Pick("presentationTimeOffset", rt, at, pt), 0);
                string media = Pick("media", rt, at, pt);
                string init = Pick("initialization", rt, at, pt);
                if (init.Length == 0)
                {
                    XmlNode ini = PickChild("Initialization", rt, at, pt);
                    if (ini != null) init = Attr(ini, "sourceURL");
                }
                if (media.Length == 0) return Tr.S("SegmentTemplate без media", "SegmentTemplate without media");
                string initUrl = init.Length > 0 ? MdHls.Resolve(repBase, Expand(init, repId, bandwidth, startNumber, 0)) : "";
                bool byTime = media.IndexOf("$Time", StringComparison.Ordinal) >= 0 && media.IndexOf("$Number", StringComparison.Ordinal) < 0;
                XmlNode timeline = PickChild("SegmentTimeline", rt, at, pt);
                if (timeline != null)
                {
                    List<XmlNode> ss = Children(timeline, "S");
                    long time = 0, number = startNumber;
                    double endUnits = periodDur > 0 ? pto + periodDur * ts
                                    : dynamic && ast != DateTime.MinValue ? pto + ((NowUtc() - ast).TotalSeconds - periodStart) * ts : -1;
                    for (int i = 0; i < ss.Count; i++)
                    {
                        XmlNode s = ss[i];
                        string tAttr = Attr(s, "t");
                        if (tAttr.Length > 0) time = ParseLong(tAttr, time);
                        long d = ParseLong(Attr(s, "d"), 0);
                        if (d <= 0) return Tr.S("SegmentTimeline: S без d", "SegmentTimeline: S without d");
                        long r = ParseLong(Attr(s, "r"), 0);
                        long count;
                        if (r < 0)
                        {
                            // До следующего S@t, иначе до конца периода.
                            long until = -1;
                            if (i + 1 < ss.Count && Attr(ss[i + 1], "t").Length > 0) until = ParseLong(Attr(ss[i + 1], "t"), -1);
                            else if (endUnits > 0) until = (long)Math.Ceiling(endUnits);
                            if (until <= time) count = 1;
                            else count = (until - time + d - 1) / d;
                        }
                        else count = r + 1;
                        for (long k = 0; k < count; k++)
                        {
                            if (++total > MaxSegments) return Tr.S("слишком много сегментов", "too many segments");
                            MdSegment seg = new MdSegment();
                            seg.Url = MdHls.Resolve(repBase, Expand(media, repId, bandwidth, number, time));
                            seg.Duration = d / (double)ts;
                            seg.Sequence = seqBase + (byTime ? time : number);
                            seg.InitUrl = initUrl;
                            t.Segments.Add(seg);
                            time += d;
                            number++;
                        }
                    }
                    return null;
                }
                double durUnits = ParseDouble(Pick("duration", rt, at, pt));
                if (durUnits <= 0) return Tr.S("SegmentTemplate без duration и SegmentTimeline", "SegmentTemplate without duration and SegmentTimeline");
                double segDur = durUnits / ts;
                long first = 0, last;
                if (!dynamic)
                {
                    if (periodDur <= 0) return Tr.S("неизвестна длительность периода", "the period duration is unknown");
                    last = (long)Math.Ceiling(periodDur / segDur - 1e-6) - 1;
                }
                else
                {
                    if (ast == DateTime.MinValue) return Tr.S("у трансляции нет availabilityStartTime", "the live stream has no availabilityStartTime");
                    double elapsed = (NowUtc() - ast).TotalSeconds - periodStart;
                    last = (long)Math.Floor(elapsed / segDur) - 1;
                    first = tsbd > 0 ? (long)Math.Floor((elapsed - tsbd) / segDur) : last - 2;
                    if (first < 0) first = 0;
                    if (periodDur > 0) last = Math.Min(last, (long)Math.Ceiling(periodDur / segDur - 1e-6) - 1);
                }
                for (long i = first; i <= last; i++)
                {
                    if (++total > MaxSegments) return Tr.S("слишком много сегментов", "too many segments");
                    long number = startNumber + i;
                    long time = pto + (long)Math.Round(i * durUnits);
                    MdSegment seg = new MdSegment();
                    seg.Url = MdHls.Resolve(repBase, Expand(media, repId, bandwidth, number, time));
                    seg.Duration = segDur;
                    seg.Sequence = seqBase + (byTime ? time : number);
                    seg.InitUrl = initUrl;
                    t.Segments.Add(seg);
                }
                return null;
            }

            if (list)
            {
                XmlNode sl = rl.List ?? al.List ?? pl.List;
                long ts = ParseLong(Pick("timescale", rl.List, al.List, pl.List), 1);
                if (ts <= 0) ts = 1;
                long startNumber = ParseLong(Pick("startNumber", rl.List, al.List, pl.List), 1);
                double durUnits = ParseDouble(Pick("duration", rl.List, al.List, pl.List));
                XmlNode ini = PickChild("Initialization", rl.List, al.List, pl.List);
                string initUrl = "";
                long initOff = -1, initLen = -1;
                if (ini != null)
                {
                    string src = Attr(ini, "sourceURL");
                    initUrl = src.Length > 0 ? MdHls.Resolve(repBase, src) : repBase;
                    ParseByteRange(Attr(ini, "range"), out initOff, out initLen);
                }
                long n = 0;
                foreach (XmlNode su in Children(sl, "SegmentURL"))
                {
                    if (++total > MaxSegments) return Tr.S("слишком много сегментов", "too many segments");
                    MdSegment seg = new MdSegment();
                    string media = Attr(su, "media");
                    seg.Url = media.Length > 0 ? MdHls.Resolve(repBase, media) : repBase;
                    long off, len;
                    if (ParseByteRange(Attr(su, "mediaRange"), out off, out len)) { seg.Offset = off; seg.Length = len; }
                    seg.Duration = durUnits > 0 ? durUnits / ts : 0;
                    seg.Sequence = seqBase + startNumber + n;
                    seg.InitUrl = initUrl;
                    seg.InitOffset = initOff;
                    seg.InitLength = initLen;
                    t.Segments.Add(seg);
                    n++;
                }
                if (t.Layout == MdLayout.Mp4) t.Layout = MdLayout.Fmp4;
                return null;
            }

            if (sbase)
            {
                XmlNode sb = rl.Base ?? al.Base ?? pl.Base;
                SidxRef r = new SidxRef();
                r.Url = repBase;
                long off, len;
                if (ParseByteRange(Attr(sb, "indexRange"), out off, out len)) { r.IndexOffset = off; r.IndexLength = len; }
                XmlNode ini = Child(sb, "Initialization");
                if (ini != null && ParseByteRange(Attr(ini, "range"), out off, out len)) { r.InitOffset = off; r.InitLength = len; }
                if (r.IndexOffset >= 0)
                {
                    MarkSidx(t, r);
                    if (t.Layout == MdLayout.Mp4) t.Layout = MdLayout.Fmp4;
                    return null;
                }
            }

            // Только BaseURL: один файл целиком.
            if (++total > MaxSegments) return Tr.S("слишком много сегментов", "too many segments");
            MdSegment whole = new MdSegment();
            whole.Url = repBase;
            whole.Duration = periodDur > 0 ? periodDur : 0;
            whole.Sequence = seqBase + 1;
            t.Segments.Add(whole);
            return null;
        }

        // $RepresentationID$, $Number%05d$, $Time$, $Bandwidth$, $$.
        internal static string Expand(string template, string repId, long bandwidth, long number, long time)
        {
            StringBuilder sb = new StringBuilder();
            int i = 0;
            while (i < template.Length)
            {
                int a = template.IndexOf('$', i);
                if (a < 0) { sb.Append(template, i, template.Length - i); break; }
                sb.Append(template, i, a - i);
                int b = template.IndexOf('$', a + 1);
                if (b < 0) { sb.Append(template, a, template.Length - a); break; }
                string ident = template.Substring(a + 1, b - a - 1);
                i = b + 1;
                if (ident.Length == 0) { sb.Append('$'); continue; }
                string name = ident, fmt = "";
                int pct = ident.IndexOf('%');
                if (pct >= 0) { name = ident.Substring(0, pct); fmt = ident.Substring(pct); }
                long value;
                switch (name)
                {
                    case "RepresentationID": sb.Append(repId); continue;
                    case "Number": value = number; break;
                    case "Time": value = time; break;
                    case "Bandwidth": value = bandwidth; break;
                    default: sb.Append('$').Append(ident).Append('$'); continue;
                }
                int width = 1;
                Match mm = Regex.Match(fmt, "^%0?([0-9]{1,2})d$");
                if (mm.Success) width = int.Parse(mm.Groups[1].Value, CultureInfo.InvariantCulture);
                sb.Append(value.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0'));
            }
            return sb.ToString();
        }

        private static MdTrackKind KindOf(string contentType, string mime, string codecs)
        {
            string ct = (contentType ?? "").ToLowerInvariant();
            string mt = (mime ?? "").ToLowerInvariant();
            if (ct == "text" || mt.StartsWith("text/", StringComparison.Ordinal) || mt.Contains("ttml")
                || codecs.StartsWith("stpp", StringComparison.OrdinalIgnoreCase) || codecs.StartsWith("wvtt", StringComparison.OrdinalIgnoreCase))
                return MdTrackKind.Subtitles;
            bool video = ct == "video" || mt.StartsWith("video/", StringComparison.Ordinal);
            bool audioCodec = MdHls.CodecPart(codecs, false).Length > 0;
            if (ct == "audio" || mt.StartsWith("audio/", StringComparison.Ordinal)) return MdTrackKind.Audio;
            if (video) return audioCodec && MdHls.HasVideoCodec(codecs) ? MdTrackKind.Muxed : MdTrackKind.Video;
            return MdHls.HasVideoCodec(codecs) ? (audioCodec ? MdTrackKind.Muxed : MdTrackKind.Video) : audioCodec ? MdTrackKind.Audio : MdTrackKind.Muxed;
        }

        private static MdLayout LayoutOf(string mime, string codecs, MdTrackKind kind)
        {
            string mt = (mime ?? "").ToLowerInvariant();
            if (mt.Contains("webm")) return MdLayout.WebM;
            if (mt == "text/vtt") return MdLayout.Vtt;
            if (mt.Contains("ttml")) return MdLayout.Ttml;
            if (mt.Contains("mp2t")) return MdLayout.Ts;
            return MdLayout.Fmp4;
        }

        // «30000/1001», «25».
        private static double FrameRate(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            int slash = s.IndexOf('/');
            double a, b = 1;
            if (!double.TryParse(slash < 0 ? s : s.Substring(0, slash), NumberStyles.Float, CultureInfo.InvariantCulture, out a)) return 0;
            if (slash >= 0 && (!double.TryParse(s.Substring(slash + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out b) || b <= 0)) return 0;
            double r = a / b;
            return r > 0 && r < 1000 ? r : 0;
        }

        // «0-831» → смещение 0, длина 832.
        internal static bool ParseByteRange(string s, out long offset, out long length)
        {
            offset = length = -1;
            if (string.IsNullOrEmpty(s)) return false;
            int dash = s.IndexOf('-');
            long a, b;
            if (dash <= 0 || !long.TryParse(s.Substring(0, dash).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out a)
                || !long.TryParse(s.Substring(dash + 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out b) || b < a) return false;
            offset = a;
            length = b - a + 1;
            return true;
        }

        private static readonly Regex IsoRx = new Regex(
            @"^P(?:(\d+(?:\.\d+)?)Y)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)W)?(?:(\d+(?:\.\d+)?)D)?(?:T(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?)?$",
            RegexOptions.CultureInvariant);

        // ISO 8601 «PT1H2M3.5S» → секунды; 0 — нет или непонятно.
        internal static double Iso(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            Match mm = IsoRx.Match(s.Trim());
            if (!mm.Success) return 0;
            double[] mult = { 365 * 86400.0, 30 * 86400.0, 7 * 86400.0, 86400.0, 3600.0, 60.0, 1.0 };
            double sum = 0;
            for (int i = 0; i < 7; i++)
            {
                double v;
                if (mm.Groups[i + 1].Success && double.TryParse(mm.Groups[i + 1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) sum += v * mult[i];
            }
            return sum;
        }

        private static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse((s ?? "").Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) ? v : fallback;
        }

        private static double ParseDouble(string s)
        {
            double v;
            return double.TryParse((s ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static string BaseOf(string parent, XmlNode n)
        {
            XmlNode b = Child(n, "BaseURL");
            if (b == null) return parent;
            string text = (b.InnerText ?? "").Trim();
            return text.Length == 0 ? parent : MdHls.Resolve(parent, text);
        }

        private static string Inherit(XmlNode rep, XmlNode aset, string name)
        {
            string v = Attr(rep, name);
            return v.Length > 0 ? v : Attr(aset, name);
        }

        private static string Attr(XmlNode n, string name)
        {
            if (n == null || n.Attributes == null) return "";
            XmlAttribute a = n.Attributes[name];
            return a == null ? "" : (a.Value ?? "").Trim();
        }

        private static XmlNode Child(XmlNode n, string localName)
        {
            if (n == null) return null;
            foreach (XmlNode c in n.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.LocalName == localName) return c;
            return null;
        }

        private static List<XmlNode> Children(XmlNode n, string localName)
        {
            List<XmlNode> list = new List<XmlNode>();
            if (n == null) return list;
            foreach (XmlNode c in n.ChildNodes)
                if (c.NodeType == XmlNodeType.Element && c.LocalName == localName) list.Add(c);
            return list;
        }
    }
}
