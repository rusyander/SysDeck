// Windows Process Cleaner — «Загрузки», видео: субтитры (склейка сегментов WebVTT, перевод VTT/TTML в SRT, файлы рядом с видео).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// HLS режет субтитры на сегменты со своими X-TIMESTAMP-MAP: время реплики = местное − LOCAL + MPEGTS/90000. Склейка
// переводит всё в абсолютное время MPEG-TS (с учётом переполнения 33 бит) и пишет один заголовок MPEGTS:0,LOCAL:0 —
// так файл остаётся правильным HLS-субтитром. Рядом с готовым видео время сдвигается к нулю видео (base100 склейки),
// но только если в файле была карта: без неё время и так отсчитывается от начала.
// Одна и та же реплика на стыке сегментов повторяется в обоих — точные повторы выбрасываются.
// Вход не больше 16 МиБ; TTML читается XmlReader без DTD и внешних ссылок.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Xml;

namespace WindowsProcessCleaner.Downloads
{
    internal static class MdSubs
    {
        internal const long MaxInput = 16L << 20;
        private const long Wrap33 = 1L << 33;

        internal sealed class Cue
        {
            public long StartMs, EndMs;
            public string Id = "";
            public string Settings = "";
            public string Text = "";         // разметка VTT (для TTML — текст с экранированными &, <, >)
            public int Order;
        }

        // Состояние разбора последовательности сегментов: последняя карта времени.
        private sealed class MapState
        {
            public bool HasMap;
            public long LastMpegts = -1;     // развёрнутое значение
        }

        // ------------------------------------------------------------------ //
        //  Публичное
        // ------------------------------------------------------------------ //
        public static bool JoinVtt(IList<string> segmentFiles, string outVtt, out string error)
        {
            error = null;
            try
            {
                if (segmentFiles == null || segmentFiles.Count == 0) { error = Tr.S("Нет сегментов субтитров", "No subtitle segments"); return false; }
                List<Cue> cues = new List<Cue>();
                MapState map = new MapState();
                foreach (string f in segmentFiles)
                {
                    string text;
                    if (!ReadText(f, out text, out error)) return false;
                    if (!ParseVtt(text, cues, map, out error)) { error = error + " (" + Path.GetFileName(f) + ")"; return false; }
                }
                SortDedupe(cues);
                WriteVtt(outVtt, cues, map.HasMap);
                return true;
            }
            catch (IOException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (UnauthorizedAccessException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (ArgumentException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (NotSupportedException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            return false;
        }

        public static bool ToSrt(string inPath, string outSrt, out string error)
        {
            error = null;
            try
            {
                List<Cue> cues;
                bool hasMap;
                if (!Load(inPath, out cues, out hasMap, out error)) return false;
                SortDedupe(cues);
                WriteSrt(outSrt, cues);
                return true;
            }
            catch (IOException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (UnauthorizedAccessException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (ArgumentException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            catch (NotSupportedException ex) { error = Tr.S("Не удалось записать субтитры: ", "Could not write subtitles: ") + ex.Message; }
            return false;
        }

        // Файлы рядом с итогом: «<имя>.<язык|название|n>.vtt» и «.srt». Сломанные субтитры пропускаются — видео важнее.
        internal static void WriteSidecars(List<MdInput> subs, string final, long base100, List<string> files)
        {
            if (subs == null || subs.Count == 0) return;
            string dir = Path.GetDirectoryName(final);
            string stem = Path.GetFileNameWithoutExtension(final);
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < subs.Count; i++)
            {
                MdInput s = subs[i];
                string tag = SafeTag(s.Language);
                if (tag.Length == 0) tag = SafeTag(s.Name);
                if (tag.Length == 0) tag = (i + 1).ToString(CultureInfo.InvariantCulture);
                if (!used.Add(tag))
                {
                    int n = 2;
                    while (!used.Add(tag + "-" + n)) n++;
                    tag = tag + "-" + n;
                }
                try
                {
                    List<Cue> cues;
                    bool hasMap;
                    string error;
                    if (!Load(s.Path, out cues, out hasMap, out error)) continue;
                    SortDedupe(cues);
                    long shift = 0;
                    if (hasMap)
                    {
                        // Абсолютное время MPEG-TS → от начала видео; видео могло начаться до переполнения 33 бит.
                        shift = base100 / 10000;
                        long wrapMs = Wrap33 / 90;
                        if (cues.Count > 0 && cues[0].StartMs - shift < -wrapMs / 2) shift -= wrapMs;
                    }
                    string vtt = Path.Combine(dir, stem + "." + tag + ".vtt");
                    string srt = Path.Combine(dir, stem + "." + tag + ".srt");
                    if (MdMux.SamePath(vtt, s.Path) || MdMux.SamePath(srt, s.Path)) continue;
                    List<Cue> shifted = new List<Cue>();
                    foreach (Cue c in cues)
                    {
                        Cue d = Clone(c);
                        d.StartMs = Math.Max(0, c.StartMs - shift);
                        d.EndMs = Math.Max(0, c.EndMs - shift);
                        if (d.EndMs > d.StartMs) shifted.Add(d);
                    }
                    WriteVtt(vtt, shifted, false);
                    files.Add(vtt);
                    WriteSrt(srt, shifted);
                    files.Add(srt);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
        }

        // ------------------------------------------------------------------ //
        //  Чтение
        // ------------------------------------------------------------------ //
        internal static bool Load(string path, out List<Cue> cues, out bool hasMap, out string error)
        {
            cues = new List<Cue>();
            hasMap = false;
            error = null;
            byte[] bytes;
            if (!ReadBytes(path, out bytes, out error)) return false;
            MdLayout layout = MdMux.ProbeBytes(bytes, Math.Min(bytes.Length, 64 << 10));
            if (layout == MdLayout.Ttml) return ParseTtml(bytes, cues, out error);
            if (layout != MdLayout.Vtt) { error = Tr.S("Файл субтитров не WebVTT и не TTML", "The subtitle file is neither WebVTT nor TTML"); return false; }
            MapState map = new MapState();
            if (!ParseVtt(Decode(bytes), cues, map, out error)) return false;
            hasMap = map.HasMap;
            return true;
        }

        private static bool ReadBytes(string path, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) { error = Tr.S("Нет файла субтитров", "Subtitle file is missing"); return false; }
                if (fi.Length > MaxInput) { error = Tr.S("Файл субтитров больше 16 МиБ", "Subtitle file is larger than 16 MiB"); return false; }
                bytes = File.ReadAllBytes(path);
                if (bytes.Length > MaxInput) { error = Tr.S("Файл субтитров больше 16 МиБ", "Subtitle file is larger than 16 MiB"); return false; }
                return true;
            }
            catch (IOException ex) { error = ex.Message; }
            catch (UnauthorizedAccessException ex) { error = ex.Message; }
            catch (ArgumentException ex) { error = ex.Message; }
            catch (NotSupportedException ex) { error = ex.Message; }
            return false;
        }

        private static bool ReadText(string path, out string text, out string error)
        {
            text = null;
            byte[] b;
            if (!ReadBytes(path, out b, out error)) return false;
            text = Decode(b);
            return true;
        }

        private static string Decode(byte[] b)
        {
            int s = b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF ? 3 : 0;
            return new UTF8Encoding(false, false).GetString(b, s, b.Length - s);
        }

        // Один файл может содержать несколько сегментов подряд (движок склеил байты): каждый «WEBVTT» — новый заголовок.
        private static bool ParseVtt(string text, List<Cue> cues, MapState map, out string error)
        {
            error = null;
            string[] lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length == 0 || !IsHeader(lines[0])) { error = Tr.S("Нет заголовка WEBVTT", "No WEBVTT header"); return false; }
            int i = 0;
            long segOffset = 0;
            bool segMap = false;
            while (i < lines.Length)
            {
                while (i < lines.Length && lines[i].Trim().Length == 0) i++;
                if (i >= lines.Length) break;
                int start = i;
                while (i < lines.Length && lines[i].Trim().Length > 0) i++;
                // блок: lines[start..i)
                string first = lines[start];
                if (IsHeader(first))
                {
                    segMap = false;
                    for (int k = start + 1; k < i; k++)
                    {
                        long mpegts, local;
                        if (ParseMap(lines[k], out mpegts, out local))
                        {
                            if (map.LastMpegts >= 0)
                            {
                                long d = (mpegts - map.LastMpegts) % Wrap33;
                                if (d < 0) d += Wrap33;
                                if (d > Wrap33 / 2) d -= Wrap33;
                                mpegts = map.LastMpegts + d;
                            }
                            map.LastMpegts = mpegts;
                            map.HasMap = true;
                            segMap = true;
                            segOffset = mpegts / 90 - local;
                        }
                    }
                    // сегмент без карты после сегментов с картой — со сдвигом предыдущего
                    if (!segMap && !map.HasMap) segOffset = 0;
                    continue;
                }
                if (first.StartsWith("NOTE", StringComparison.Ordinal) || first == "STYLE" || first == "REGION"
                    || first.StartsWith("STYLE ", StringComparison.Ordinal) || first.StartsWith("REGION ", StringComparison.Ordinal))
                {
                    if (first.IndexOf("-->", StringComparison.Ordinal) < 0) continue;
                }
                int timing = start;
                string id = "";
                if (first.IndexOf("-->", StringComparison.Ordinal) < 0)
                {
                    id = first.Trim();
                    timing = start + 1;
                }
                if (timing >= i) continue;           // блок без времени — не реплика, пропуск
                long st, en;
                string settings;
                if (!ParseTiming(lines[timing], out st, out en, out settings)) continue;
                StringBuilder sb = new StringBuilder();
                for (int k = timing + 1; k < i; k++)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(lines[k]);
                }
                Cue c = new Cue();
                c.StartMs = Math.Max(0, st + segOffset);
                c.EndMs = Math.Max(0, en + segOffset);
                c.Id = id;
                c.Settings = settings;
                c.Text = sb.ToString();
                c.Order = cues.Count;
                if (cues.Count >= 1000000) { error = Tr.S("Слишком много реплик", "Too many cues"); return false; }
                cues.Add(c);
            }
            return true;
        }

        private static bool IsHeader(string line)
        {
            return line.StartsWith("WEBVTT", StringComparison.Ordinal) && (line.Length == 6 || line[6] == ' ' || line[6] == '\t');
        }

        // X-TIMESTAMP-MAP=MPEGTS:900000,LOCAL:00:00:00.000 (порядок частей любой)
        internal static bool ParseMap(string line, out long mpegts, out long localMs)
        {
            mpegts = -1;
            localMs = -1;
            const string key = "X-TIMESTAMP-MAP=";
            string l = line.Trim();
            if (!l.StartsWith(key, StringComparison.Ordinal)) return false;
            foreach (string part in l.Substring(key.Length).Split(','))
            {
                string p = part.Trim();
                if (p.StartsWith("MPEGTS:", StringComparison.Ordinal))
                {
                    long v;
                    if (long.TryParse(p.Substring(7), NumberStyles.None, CultureInfo.InvariantCulture, out v) && v < Wrap33 * 2) mpegts = v % Wrap33;
                }
                else if (p.StartsWith("LOCAL:", StringComparison.Ordinal))
                {
                    long v;
                    if (ParseTime(p.Substring(6), out v)) localMs = v;
                }
            }
            if (mpegts < 0) return false;
            if (localMs < 0) localMs = 0;
            return true;
        }

        private static bool ParseTiming(string line, out long start, out long end, out string settings)
        {
            start = end = 0;
            settings = "";
            int arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0) return false;
            string left = line.Substring(0, arrow).Trim();
            string right = line.Substring(arrow + 3).Trim();
            int sp = right.IndexOfAny(new char[] { ' ', '\t' });
            if (sp >= 0)
            {
                settings = right.Substring(sp + 1).Trim();
                right = right.Substring(0, sp);
            }
            return ParseTime(left, out start) && ParseTime(right, out end) && end >= start;
        }

        // [чч:]мм:сс.ттт; запятая вместо точки допускается
        internal static bool ParseTime(string s, out long ms)
        {
            ms = 0;
            s = s.Trim();
            string[] parts = s.Split(':');
            if (parts.Length < 2 || parts.Length > 3) return false;
            long h = 0, m;
            int idx = 0;
            if (parts.Length == 3 && !ParseDigits(parts[idx++], 1, 9, out h)) return false;
            if (!ParseDigits(parts[idx++], 1, 2, out m) || m > 59) return false;
            string sec = parts[idx].Replace(',', '.');
            int dot = sec.IndexOf('.');
            long whole, frac = 0;
            if (dot < 0) { if (!ParseDigits(sec, 1, 2, out whole)) return false; }
            else
            {
                if (!ParseDigits(sec.Substring(0, dot), 1, 2, out whole)) return false;
                string f = sec.Substring(dot + 1);
                if (f.Length == 0 || f.Length > 9) return false;
                long fv;
                if (!ParseDigits(f, 1, 9, out fv)) return false;
                frac = f.Length >= 3 ? long.Parse(f.Substring(0, 3), CultureInfo.InvariantCulture) : fv * (f.Length == 1 ? 100 : 10);
            }
            if (whole > 59) return false;
            ms = ((h * 60 + m) * 60 + whole) * 1000 + frac;
            return true;
        }

        private static bool ParseDigits(string s, int min, int max, out long v)
        {
            v = 0;
            if (s.Length < min || s.Length > max) return false;
            foreach (char ch in s)
            {
                if (ch < '0' || ch > '9') return false;
                v = v * 10 + (ch - '0');
            }
            return true;
        }

        // ---------- TTML ----------
        private sealed class TtScope
        {
            public int Depth;
            public long Begin, End;          // абсолютные мс; End = -1 — не задан
            public bool Para;
        }

        private static bool ParseTtml(byte[] bytes, List<Cue> cues, out string error)
        {
            error = null;
            XmlReaderSettings xs = new XmlReaderSettings();
            xs.DtdProcessing = DtdProcessing.Prohibit;
            xs.XmlResolver = null;
            xs.MaxCharactersInDocument = MaxInput * 2;
            xs.IgnoreComments = true;
            xs.IgnoreProcessingInstructions = true;
            double frameRate = 30, subFrameRate = 1, tickRate = 0;
            bool sawTt = false;
            try
            {
                using (MemoryStream ms = new MemoryStream(bytes, false))
                using (XmlReader r = XmlReader.Create(ms, xs))
                {
                    List<TtScope> stack = new List<TtScope>();
                    StringBuilder text = null;
                    bool moved = false;
                    while (moved || r.Read())
                    {
                        moved = false;
                        if (r.NodeType == XmlNodeType.Element)
                        {
                            string name = r.LocalName;
                            if (name == "tt")
                            {
                                sawTt = true;
                                double v;
                                if (TryAttrDouble(r, "frameRate", out v) && v > 0 && v <= 1000) frameRate = v;
                                if (TryAttrDouble(r, "subFrameRate", out v) && v > 0 && v <= 1000) subFrameRate = v;
                                if (TryAttrDouble(r, "tickRate", out v) && v > 0 && v <= 1e9) tickRate = v;
                                r.MoveToElement();
                            }
                            if (name == "head" || name == "metadata" || name == "styling" || name == "layout")
                            {
                                if (!r.IsEmptyElement) { r.Skip(); moved = true; }
                                continue;
                            }
                            if (name == "br")
                            {
                                if (text != null) text.Append('\u2028');
                                if (!r.IsEmptyElement) stack.Add(Scope(stack, r.Depth, 0, -1, false));
                                continue;
                            }
                            TtScope parent = stack.Count > 0 ? stack[stack.Count - 1] : null;
                            long pBegin = parent != null ? parent.Begin : 0;
                            long pEnd = parent != null ? parent.End : -1;
                            double rate = tickRate > 0 ? tickRate : frameRate * subFrameRate;
                            long b = 0, e = -1, dur = -1;
                            string a = Attr(r, "begin");
                            if (a != null && !TtTime(a, frameRate, subFrameRate, rate, out b)) b = 0;
                            a = Attr(r, "end");
                            if (a != null && !TtTime(a, frameRate, subFrameRate, rate, out e)) e = -1;
                            a = Attr(r, "dur");
                            if (a != null && !TtTime(a, frameRate, subFrameRate, rate, out dur)) dur = -1;
                            r.MoveToElement();
                            long begin = pBegin + b;
                            long end = e >= 0 ? pBegin + e : (dur >= 0 ? begin + dur : pEnd);
                            if (pEnd >= 0 && (end < 0 || end > pEnd)) end = pEnd;
                            bool para = name == "p";
                            if (r.IsEmptyElement) continue;
                            TtScope sc = Scope(stack, r.Depth, begin, end, para);
                            stack.Add(sc);
                            if (para) text = new StringBuilder();
                        }
                        else if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA
                            || r.NodeType == XmlNodeType.Whitespace || r.NodeType == XmlNodeType.SignificantWhitespace)
                        {
                            if (text != null)
                            {
                                if (text.Length > (1 << 20)) { error = Tr.S("Слишком длинная реплика TTML", "TTML cue is too long"); return false; }
                                text.Append(r.Value);
                            }
                        }
                        else if (r.NodeType == XmlNodeType.EndElement)
                        {
                            if (stack.Count == 0) continue;
                            TtScope sc = stack[stack.Count - 1];
                            stack.RemoveAt(stack.Count - 1);
                            if (sc.Para && text != null)
                            {
                                string t = CollapseTt(text.ToString());
                                text = null;
                                if (t.Length > 0 && sc.End > sc.Begin)
                                {
                                    if (cues.Count >= 1000000) { error = Tr.S("Слишком много реплик", "Too many cues"); return false; }
                                    Cue c = new Cue();
                                    c.StartMs = sc.Begin;
                                    c.EndMs = sc.End;
                                    c.Text = EscapeVtt(t);
                                    c.Order = cues.Count;
                                    cues.Add(c);
                                }
                            }
                        }
                    }
                }
            }
            catch (XmlException ex) { error = Tr.S("Повреждённый TTML: ", "Damaged TTML: ") + ex.Message; return false; }
            catch (InvalidOperationException ex) { error = Tr.S("Повреждённый TTML: ", "Damaged TTML: ") + ex.Message; return false; }
            catch (DecoderFallbackException ex) { error = Tr.S("Повреждённый TTML: ", "Damaged TTML: ") + ex.Message; return false; }
            if (!sawTt) { error = Tr.S("Нет элемента tt", "No tt element"); return false; }
            return true;
        }

        private static TtScope Scope(List<TtScope> stack, int depth, long begin, long end, bool para)
        {
            TtScope s = new TtScope();
            s.Depth = depth;
            s.Begin = begin;
            s.End = end;
            s.Para = para;
            return s;
        }

        private static string Attr(XmlReader r, string localName)
        {
            if (r.MoveToFirstAttribute())
                do
                {
                    if (r.LocalName == localName) return r.Value;
                }
                while (r.MoveToNextAttribute());
            return null;
        }

        private static bool TryAttrDouble(XmlReader r, string localName, out double v)
        {
            v = 0;
            string a = Attr(r, localName);
            return a != null && double.TryParse(a.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // «чч:мм:сс(.доли|:кадры)» или «12.5s», «500ms», «1.5h», «2m», «10f», «100t»
        private static bool TtTime(string s, double frameRate, double subFrameRate, double tickRate, out long ms)
        {
            ms = 0;
            s = s.Trim();
            if (s.Length == 0 || s.Length > 64) return false;
            if (s.IndexOf(':') >= 0)
            {
                string[] p = s.Split(':');
                if (p.Length < 3 || p.Length > 4) return false;
                double h, m, sec, frames = 0;
                if (!Num(p[0], out h) || !Num(p[1], out m) || !Num(p[2], out sec)) return false;
                if (p.Length == 4)
                {
                    string f = p[3];
                    int dot = f.IndexOf('.');
                    double sub = 0;
                    if (dot >= 0) { if (!Num(f.Substring(dot + 1), out sub)) return false; f = f.Substring(0, dot); }
                    if (!Num(f, out frames)) return false;
                    frames += sub / subFrameRate;
                }
                double total = (h * 3600 + m * 60 + sec) * 1000 + frames * 1000 / frameRate;
                if (total < 0 || total > 1e12) return false;
                ms = (long)Math.Round(total);
                return true;
            }
            string unit;
            int u = s.Length;
            while (u > 0 && char.IsLetter(s[u - 1])) u--;
            unit = s.Substring(u);
            double n;
            if (!Num(s.Substring(0, u), out n)) return false;
            double mult;
            switch (unit)
            {
                case "h": mult = 3600000; break;
                case "m": mult = 60000; break;
                case "s": mult = 1000; break;
                case "ms": mult = 1; break;
                case "f": mult = 1000 / frameRate; break;
                case "t": mult = tickRate > 0 ? 1000 / tickRate : 1000; break;
                default: return false;
            }
            double v = n * mult;
            if (v < 0 || v > 1e12) return false;
            ms = (long)Math.Round(v);
            return true;
        }

        private static bool Num(string s, out double v)
        {
            v = 0;
            if (s.Length == 0) return false;
            foreach (char ch in s) if ((ch < '0' || ch > '9') && ch != '.') return false;
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // Пробелы XML схлопываются; <br/> (U+2028) — перевод строки
        private static string CollapseTt(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (char ch in s)
            {
                if (ch == ' ' || ch == '\t' || ch == '\n' || ch == '\r') { space = true; continue; }
                if (ch == '\u2028')
                {
                    sb.Append('\n');
                    space = false;
                    continue;
                }
                if (space && sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append(' ');
                space = false;
                sb.Append(ch);
            }
            string[] lines = sb.ToString().Split('\n');
            List<string> kept = new List<string>();
            foreach (string l in lines) if (l.Trim().Length > 0) kept.Add(l.Trim());
            return string.Join("\n", kept.ToArray());
        }

        private static string EscapeVtt(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        // ------------------------------------------------------------------ //
        //  Запись
        // ------------------------------------------------------------------ //
        private static void SortDedupe(List<Cue> cues)
        {
            cues.Sort(delegate(Cue a, Cue b)
            {
                int c = a.StartMs.CompareTo(b.StartMs);
                if (c == 0) c = a.EndMs.CompareTo(b.EndMs);
                if (c == 0) c = a.Order.CompareTo(b.Order);
                return c;
            });
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            List<Cue> kept = new List<Cue>(cues.Count);
            foreach (Cue c in cues)
            {
                string key = c.StartMs + "|" + c.EndMs + "|" + c.Settings + "|" + c.Text;
                if (!seen.Add(key)) continue;
                if (c.Id.Length > 0 && !ids.Add(c.Id)) c.Id = "";
                kept.Add(c);
            }
            cues.Clear();
            cues.AddRange(kept);
        }

        private static Cue Clone(Cue c)
        {
            Cue d = new Cue();
            d.StartMs = c.StartMs;
            d.EndMs = c.EndMs;
            d.Id = c.Id;
            d.Settings = c.Settings;
            d.Text = c.Text;
            d.Order = c.Order;
            return d;
        }

        // UTF-8 без BOM, LF
        private static void WriteVtt(string path, List<Cue> cues, bool absoluteMap)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("WEBVTT\n");
            if (absoluteMap) sb.Append("X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000\n");
            sb.Append('\n');
            foreach (Cue c in cues)
            {
                if (c.Id.Length > 0) sb.Append(c.Id).Append('\n');
                sb.Append(Stamp(c.StartMs, '.')).Append(" --> ").Append(Stamp(c.EndMs, '.'));
                if (c.Settings.Length > 0) sb.Append(' ').Append(c.Settings);
                sb.Append('\n');
                if (c.Text.Length > 0) sb.Append(c.Text).Append('\n');
                sb.Append('\n');
            }
            WriteAtomically(path, new UTF8Encoding(false).GetBytes(sb.ToString()));
        }

        // UTF-8 с BOM, CRLF, номера с 1, теги убраны, сущности раскрыты
        private static void WriteSrt(string path, List<Cue> cues)
        {
            StringBuilder sb = new StringBuilder();
            int n = 0;
            foreach (Cue c in cues)
            {
                string text = PlainText(c.Text);
                if (text.Length == 0) continue;
                n++;
                sb.Append(n.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                sb.Append(Stamp(c.StartMs, ',')).Append(" --> ").Append(Stamp(c.EndMs, ',')).Append("\r\n");
                sb.Append(text.Replace("\n", "\r\n")).Append("\r\n\r\n");
            }
            byte[] body = new UTF8Encoding(false).GetBytes(sb.ToString());
            byte[] all = new byte[body.Length + 3];
            all[0] = 0xEF;
            all[1] = 0xBB;
            all[2] = 0xBF;
            Buffer.BlockCopy(body, 0, all, 3, body.Length);
            WriteAtomically(path, all);
        }

        private static void WriteAtomically(string path, byte[] data)
        {
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, data);
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        internal static string Stamp(long ms, char sep)
        {
            if (ms < 0) ms = 0;
            long h = ms / 3600000, m = ms / 60000 % 60, s = ms / 1000 % 60, f = ms % 1000;
            return h.ToString("00", CultureInfo.InvariantCulture) + ":" + m.ToString("00", CultureInfo.InvariantCulture) + ":"
                + s.ToString("00", CultureInfo.InvariantCulture) + sep + f.ToString("000", CultureInfo.InvariantCulture);
        }

        // Разметка VTT → простой текст: теги (<i>, <c.x>, <v Имя>, <00:00:01.000>) убираются, сущности раскрываются
        internal static string PlainText(string vtt)
        {
            StringBuilder sb = new StringBuilder(vtt.Length);
            int i = 0;
            while (i < vtt.Length)
            {
                char ch = vtt[i];
                if (ch == '<')
                {
                    int close = vtt.IndexOf('>', i + 1);
                    if (close < 0) break;
                    i = close + 1;
                    continue;
                }
                if (ch == '&')
                {
                    int semi = vtt.IndexOf(';', i + 1);
                    if (semi > i && semi - i <= 10)
                    {
                        string ent = vtt.Substring(i + 1, semi - i - 1);
                        string rep = Entity(ent);
                        if (rep != null)
                        {
                            sb.Append(rep);
                            i = semi + 1;
                            continue;
                        }
                    }
                }
                sb.Append(ch);
                i++;
            }
            string[] lines = sb.ToString().Split('\n');
            List<string> kept = new List<string>();
            foreach (string l in lines) if (l.Trim().Length > 0) kept.Add(l.TrimEnd());
            return string.Join("\n", kept.ToArray());
        }

        private static string Entity(string e)
        {
            switch (e)
            {
                case "amp": return "&";
                case "lt": return "<";
                case "gt": return ">";
                case "quot": return "\"";
                case "apos": return "'";
                case "nbsp": return " ";
                case "lrm": return "\u200E";
                case "rlm": return "\u200F";
            }
            if (e.Length >= 2 && e[0] == '#')
            {
                int code;
                bool ok = e[1] == 'x' || e[1] == 'X'
                    ? int.TryParse(e.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)
                    : int.TryParse(e.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out code);
                if (ok && code > 0 && code <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF)) return char.ConvertFromUtf32(code);
            }
            return null;
        }

        private static string SafeTag(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            char[] bad = Path.GetInvalidFileNameChars();
            foreach (char ch in s.Trim())
            {
                if (sb.Length >= 32) break;
                if (Array.IndexOf(bad, ch) >= 0 || ch == '.' || char.IsWhiteSpace(ch)) { if (sb.Length > 0 && sb[sb.Length - 1] != '_') sb.Append('_'); continue; }
                sb.Append(ch);
            }
            return sb.ToString().Trim('_');
        }
    }
}
