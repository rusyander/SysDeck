// SysDeck — «Загрузки»: то, что показывает страница: снимок списка, фильтры, подписи состояния, разбор ссылок.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Здесь нет ни окна, ни канала — только данные и правила, которые проверяются тестами без UI.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SysDeck.Downloads
{
    // Строка страницы: запись и живые значения, которые знает только работающий процесс.
    internal sealed class DlRow
    {
        public DlItem Item;
        public long Done;
        public long Speed;
        public int Connections;
        public long MoveDone;
        public bool HasCookies;
        public long UpSpeed;                      // торрент: отдача, сиды среди пиров, доля проверенных данных
        public int Seeds;
        public double CheckProgress;
    }

    internal sealed class DlSnapshot
    {
        public bool Live;                         // от процесса; false — прочитано из хранилища, пока процесс не запущен
        public readonly List<DlRow> Rows = new List<DlRow>();
        public long Speed;
        public long Limit;
        public int Active;
        public int Seeding;
        public string Gate = "";

        // Ответ команды list (items без журналов).
        public static DlSnapshot FromList(JVal root)
        {
            if (root == null || !DlJson.Bool(root, "ok", false)) return null;
            DlSnapshot s = new DlSnapshot();
            s.Live = true;
            s.Speed = DlJson.Long(root, "speed", 0);
            s.Limit = DlJson.Long(root, "limit", 0);
            s.Active = DlJson.Int(root, "active", 0);
            s.Seeding = DlJson.Int(root, "seeding", 0);
            s.Gate = DlJson.Str(root, "gate", "");
            JVal items = root.Get("items");
            if (items != null && items.Kind == JKind.Arr)
                foreach (JVal j in items.V)
                {
                    DlRow r = RowFromJson(j);
                    if (r != null) s.Rows.Add(r);
                }
            return s;
        }

        public static DlRow RowFromJson(JVal j)
        {
            DlItem it = DlItem.FromJson(j);
            if (it == null) return null;
            DlRow r = new DlRow();
            r.Item = it;
            r.Done = DlJson.Long(j, "Done", it.DoneBytes);
            r.Speed = DlJson.Long(j, "Speed", 0);
            r.Connections = DlJson.Int(j, "ActiveConnections", 0);
            r.MoveDone = DlJson.Long(j, "MoveDone", 0);
            r.HasCookies = DlJson.Bool(j, "HasCookies", false);
            r.UpSpeed = DlJson.Long(j, "UpSpeed", 0);
            r.Seeds = DlJson.Int(j, "Seeds", 0);
            double check;
            r.CheckProgress = double.TryParse(DlJson.Str(j, "CheckProgress", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out check)
                              ? Math.Max(0, Math.Min(1, check)) : 0;
            return r;
        }

        // Процесс не запущен: хранилище только читается. «Качается» в записи значит «процесс закрылся посреди загрузки» —
        // при запуске такая загрузка продолжится, поэтому и показывается как ждущая очереди.
        public static DlSnapshot FromStore(List<DlItem> items)
        {
            DlSnapshot s = new DlSnapshot();
            foreach (DlItem it in items)
            {
                if (it.State == DlState.Active || it.State == DlState.Waiting || it.State == DlState.Checking || it.State == DlState.Seeding)
                {
                    it.State = DlState.Queued;
                    it.WaitReason = "";
                }
                DlRow r = new DlRow();
                r.Item = it;
                r.Done = it.DoneBytes;
                s.Rows.Add(r);
            }
            return s;
        }

        public DlRow Find(string id)
        {
            foreach (DlRow r in Rows) if (r.Item.Id == id) return r;
            return null;
        }
    }

    internal enum DlStateFilter { All, Running, Waiting, Paused, Done, Errors }

    internal static class DlView
    {
        // Порядок = порядок в выпадающем списке «Тип» после «все типы».
        public static readonly string[] Categories = { "programs", "archives", "video", "audio", "documents", "images", "other" };

        private static readonly string[] Programs = { ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".bat", ".cmd", ".ps1", ".jar", ".apk" };
        private static readonly string[] Archives = { ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".tar", ".cab", ".lz", ".lzma" };
        private static readonly string[] Video = { ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".flv", ".mpg", ".mpeg" };
        private static readonly string[] Audio = { ".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".ape" };
        private static readonly string[] Documents = { ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".odt", ".ods", ".rtf", ".txt", ".csv", ".epub", ".djvu", ".md" };
        private static readonly string[] Images = { ".iso", ".img", ".vhd", ".vhdx", ".vmdk", ".qcow2", ".wim", ".esd" };

        public static string CategoryTitle(string category)
        {
            switch (category)
            {
                case "programs": return Tr.S("программы", "programs");
                case "archives": return Tr.S("архивы", "archives");
                case "video": return Tr.S("видео", "video");
                case "audio": return Tr.S("музыка", "music");
                case "documents": return Tr.S("документы", "documents");
                case "images": return Tr.S("образы дисков", "disk images");
                default: return Tr.S("другое", "other");
            }
        }

        public static string CategoryOf(string fileName)
        {
            string ext = "";
            try { ext = Path.GetExtension(fileName ?? "").ToLowerInvariant(); } catch { }
            if (ext.Length == 0) return "other";
            if (Array.IndexOf(Programs, ext) >= 0) return "programs";
            if (Array.IndexOf(Archives, ext) >= 0) return "archives";
            if (Array.IndexOf(Video, ext) >= 0) return "video";
            if (Array.IndexOf(Audio, ext) >= 0) return "audio";
            if (Array.IndexOf(Documents, ext) >= 0) return "documents";
            if (Array.IndexOf(Images, ext) >= 0) return "images";
            return "other";
        }

        public static string SourceTitle(string source)
        {
            switch (source ?? "")
            {
                case "chrome": return "Chrome";
                case "edge": return "Edge";
                case "yandex": return Tr.S("Яндекс Браузер", "Yandex Browser");
                case "firefox": return "Firefox";
                case "chromium": return "Chromium";
                case "watch": return Tr.S("папка наблюдения", "watch folder");
                default: return Tr.S("вручную", "manual");
            }
        }

        public static bool InState(DlItem it, DlStateFilter f)
        {
            bool moving = it.MoveTo.Length > 0;
            switch (f)
            {
                case DlStateFilter.Running: return it.State == DlState.Active || it.State == DlState.Checking || moving;
                case DlStateFilter.Waiting: return !moving && (it.State == DlState.Queued || it.State == DlState.Scheduled || it.State == DlState.Waiting);
                case DlStateFilter.Paused: return !moving && it.State == DlState.Paused;
                // Раздача — скачанный торрент: в «Готово», хотя сессия с ним работает.
                case DlStateFilter.Done: return !moving && (it.State == DlState.Completed || it.State == DlState.Seeding);
                case DlStateFilter.Errors: return !moving && (it.State == DlState.Failed || it.State == DlState.NeedsLink);
                default: return true;
            }
        }

        // category / source пустые — любые.
        public static bool Matches(DlItem it, DlStateFilter f, string category, string source)
        {
            if (!InState(it, f)) return false;
            if (!string.IsNullOrEmpty(category) && CategoryOf(it.FileName) != category) return false;
            if (!string.IsNullOrEmpty(source) && (it.Source ?? "manual") != source) return false;
            return true;
        }

        public static int Count(DlSnapshot s, DlStateFilter f)
        {
            int n = 0;
            if (s != null) foreach (DlRow r in s.Rows) if (InState(r.Item, f)) n++;
            return n;
        }

        // ---------- числа ----------

        public static string Speed(long bytesPerSecond)
        {
            if (bytesPerSecond <= 0) return "";
            return Engine.FormatBytes(bytesPerSecond) + Tr.S("/с", "/s");
        }

        public static string Limit(int kbps)
        {
            return kbps <= 0 ? Tr.S("без лимита", "no limit") : Engine.FormatBytes((long)kbps * 1024) + Tr.S("/с", "/s");
        }

        // Пусто — не оценить (нет скорости или размера).
        public static string Eta(long remaining, long bytesPerSecond)
        {
            if (remaining <= 0 || bytesPerSecond <= 0) return "";
            return Duration((long)Math.Ceiling((double)remaining / bytesPerSecond));
        }

        public static string Duration(long seconds)
        {
            if (seconds < 0) seconds = 0;
            if (seconds < 60) return seconds + Tr.S(" с", " s");
            long minutes = (seconds + 59) / 60;
            if (minutes < 60) return minutes + Tr.S(" мин", " min");
            long hours = minutes / 60, rest = minutes % 60;
            if (hours < 48) return hours + Tr.S(" ч", " h") + (rest > 0 ? " " + rest + Tr.S(" мин", " min") : "");
            return (hours / 24) + Tr.S(" дн", " d");
        }

        // 0..1; -1 — размер неизвестен.
        public static double Fraction(DlRow r)
        {
            DlItem it = r.Item;
            if (it.MoveTo.Length > 0)
            {
                long size = MoveSize(r);
                return size > 0 ? Math.Min(1.0, (double)r.MoveDone / size) : -1;
            }
            if (it.State == DlState.Completed || it.State == DlState.Seeding) return 1.0;
            // Видео: доля по кускам. У трансляции конца нет (SegmentsTotal = -1) — полоса остаётся бегущей.
            if (it.IsMedia && it.Media != null && it.State != DlState.Failed)
                return it.Media.SegmentsTotal > 0
                       ? Math.Max(0, Math.Min(1.0, (double)it.Media.SegmentsDone / it.Media.SegmentsTotal)) : -1;
            if (it.Total <= 0) return -1;
            return Math.Max(0, Math.Min(1.0, (double)r.Done / it.Total));
        }

        // Скачанное целиком: готовая загрузка или раздающийся торрент.
        public static bool IsDone(DlItem it) { return it.State == DlState.Completed || it.State == DlState.Seeding; }

        private static long MoveSize(DlRow r)
        {
            return r.Item.Total > 0 ? r.Item.Total : r.Done;
        }

        public static string SizeText(DlRow r)
        {
            DlItem it = r.Item;
            if (IsDone(it)) return it.Total >= 0 ? Engine.FormatBytes(it.Total) : Engine.FormatBytes(r.Done);
            // У видео байты лежат в папке частей, а не в .wpcpart: размер станет известен только после склейки,
            // поэтому до неё показываем сегменты и записанное время.
            string media = MediaSizeText(r);
            if (media != null) return media;
            if (it.Total > 0) return Engine.FormatBytes(r.Done) + Tr.S(" из ", " of ") + Engine.FormatBytes(it.Total);
            return r.Done > 0 ? Engine.FormatBytes(r.Done) : "";
        }

        // null — запись не видео или показывать нечего. Трансляция (SegmentsTotal = -1) не имеет конца: только время.
        private static string MediaSizeText(DlRow r)
        {
            DlItem it = r.Item;
            if (!it.IsMedia || it.Media == null) return null;
            DlMedia md = it.Media;
            string time = md.SecondsDone >= 1 ? Duration((long)md.SecondsDone) : "";
            if (md.SegmentsTotal > 0)
            {
                string text = md.SegmentsDone + Tr.S(" из ", " of ") + md.SegmentsTotal + Tr.S(" кусков", " parts");
                return time.Length > 0 ? text + " · " + time : text;
            }
            if (md.SegmentsDone > 0)
            {
                string text = md.SegmentsDone + Tr.S(" кусков", " parts");
                return time.Length > 0 ? text + " · " + time : text;
            }
            return time.Length > 0 ? time : null;
        }

        // Колонка «Скорость»: приём у качающейся загрузки, отдача у раздачи.
        public static string SpeedText(DlRow r)
        {
            DlItem it = r.Item;
            if (it.State == DlState.Active) return Speed(r.Speed);
            if (it.State == DlState.Seeding && r.UpSpeed > 0) return "↑ " + Speed(r.UpSpeed);
            return "";
        }

        // magnet-ссылка торрента: исходная, если он добавлен по ней, иначе из метаданных (meta null — только info-hash).
        // У закрытого торрента трекеры не добавляются: в их адресах ключ участника.
        public static string MagnetLink(DlItem it, BtMeta meta)
        {
            if (it.Url.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)) return it.Url;
            System.Text.StringBuilder sb = new System.Text.StringBuilder("magnet:?");
            if (meta == null) sb.Append("xt=urn:btih:").Append(it.InfoHash);
            else
            {
                if (meta.InfoHash != null) sb.Append("xt=urn:btih:").Append(Bencode.Hex(meta.InfoHash));
                if (meta.InfoHashV2 != null) sb.Append(meta.InfoHash != null ? "&" : "").Append("xt=urn:btmh:1220").Append(Bencode.Hex(meta.InfoHashV2));
            }
            string name = meta != null && meta.Name.Length > 0 ? meta.Name : it.FileName;
            if (!string.IsNullOrEmpty(name)) sb.Append("&dn=").Append(Uri.EscapeDataString(name));
            if (meta != null && !meta.Private)
                foreach (List<string> tier in meta.Trackers)
                    foreach (string tracker in tier) sb.Append("&tr=").Append(Uri.EscapeDataString(tracker));
            return sb.ToString();
        }

        // Строка над списком: работает ли процесс и что он делает.
        public static string ProcessStatusText(DlSnapshot s)
        {
            string text;
            if (s.Live)
            {
                text = Tr.S("● Процесс загрузок работает", "● The download process is running");
                if (s.Active > 0) text += Tr.S(" · качаются: ", " · downloading: ") + s.Active;
                if (s.Seeding > 0) text += Tr.S(" · раздаются: ", " · seeding: ") + s.Seeding;
                if (s.Speed > 0) text += " · " + Speed(s.Speed);
                if (s.Limit > 0) text += Tr.S(" · общий лимит ", " · overall limit ") + Speed(s.Limit);
                if (s.Gate.Length > 0) text += Tr.S(" · очередь ждёт: ", " · the queue waits: ") + s.Gate;
                int moving = 0;
                foreach (DlRow r in s.Rows) if (r.Item.MoveTo.Length > 0) moving++;
                if (moving > 0) text += Tr.S(" · переносится: ", " · moving: ") + moving;
                if (s.Active == 0 && s.Seeding == 0 && moving == 0 && Count(s, DlStateFilter.Waiting) == 0)
                    text += Tr.S(" · работы нет — закроется сам через минуту после ухода со страницы",
                                 " · nothing to do — exits by itself a minute after you leave this page");
            }
            else
            {
                int pending = Count(s, DlStateFilter.Waiting);
                text = Tr.S("○ Процесс загрузок не запущен — список прочитан с диска. Любое действие запустит его",
                            "○ The download process is not running — the list is read from disk. Any action starts it");
                if (pending > 0)
                    text += Tr.S(", и очередь (", ", and the queue (") + pending + Tr.S(") продолжит качаться", ") will continue");
            }
            return text;
        }

        public static string EtaText(DlRow r)
        {
            DlItem it = r.Item;
            if (it.State != DlState.Active || it.Total <= 0) return "";
            return Eta(it.Total - r.Done, r.Speed);
        }

        public static string StateText(DlRow r, DateTime utcNow)
        {
            string text = StateCore(r, utcNow);
            DlItem it = r.Item;
            return it.IsTorrent && it.UpdateHash.Length > 0 && it.MoveTo.Length == 0 ? text + Tr.S(" · есть новая версия", " · new version available") : text;
        }

        private static string StateCore(DlRow r, DateTime utcNow)
        {
            DlItem it = r.Item;
            if (it.MoveTo.Length > 0)
            {
                double f = Fraction(r);
                return (it.MoveCopy ? Tr.S("копируется", "copying") : Tr.S("переносится", "moving"))
                       + (f >= 0 ? " · " + (int)Math.Floor(f * 100) + " %" : "");
            }
            if (it.IsTorrent)
            {
                switch (it.State)
                {
                    case DlState.Active:
                        if (it.WaitReason.Length > 0) return it.WaitReason;
                        return Tr.S("качается", "downloading") + TorrentPeers(r) + (r.UpSpeed > 0 ? " · ↑ " + Speed(r.UpSpeed) : "");
                    case DlState.Checking:
                        return Tr.S("проверка данных", "checking data") + " · " + (int)Math.Floor(r.CheckProgress * 100) + " %";
                    case DlState.Seeding:
                        return Tr.S("раздаётся", "seeding") + TorrentPeers(r) + (r.UpSpeed > 0 ? " · ↑ " + Speed(r.UpSpeed) : "");
                }
            }
            if (it.IsMedia && it.State == DlState.Active && it.Media != null)
            {
                // После сегментов идут склейка и проверка: это минуты без сети, и молчащая полоса выглядела бы как зависание.
                if (it.Media.Phase == "mux") return Tr.S("склейка", "assembling");
                if (it.Media.Phase == "verify") return Tr.S("проверка файла", "checking the file");
                if (it.Media.Live) return it.Media.StopLive
                    ? Tr.S("запись останавливается", "recording is stopping")
                    : Tr.S("идёт запись трансляции", "recording a live stream");
            }
            switch (it.State)
            {
                case DlState.Active:
                    return Tr.S("качается", "downloading") + (r.Connections > 0 ? " · " + r.Connections + Tr.S(" соед.", " conn.") : "");
                case DlState.Queued:
                    return it.WaitReason.Length > 0 ? it.WaitReason : Tr.S("в очереди", "queued");
                case DlState.Scheduled:
                    return Tr.S("отложена до ", "postponed until ") + Local(it.StartAtUtc, utcNow);
                case DlState.Waiting:
                    return it.WaitReason.Length > 0 ? it.WaitReason : Tr.S("ждёт", "waiting");
                case DlState.Paused:
                    return Tr.S("пауза", "paused") + (it.MoveError.Length > 0 ? " · " + Tr.S("перенос не удался: ", "move failed: ") + it.MoveError : "");
                case DlState.Completed:
                    return Tr.S("готово", "done") + (it.MoveError.Length > 0 ? " · " + Tr.S("перенос не удался: ", "move failed: ") + it.MoveError : "");
                case DlState.NeedsLink:
                    return Tr.S("нужна новая ссылка", "needs a fresh link") + (it.Error.Length > 0 ? " · " + it.Error : "");
                default:
                    return Tr.S("ошибка", "error") + (it.Error.Length > 0 ? ": " + it.Error : "");
            }
        }

        // « · 5 пиров (2 сида)»; пусто — никого.
        private static string TorrentPeers(DlRow r)
        {
            if (r.Connections <= 0) return "";
            return " · " + r.Connections + Tr.S(" пир.", " peers") + (r.Seeds > 0 ? " (" + r.Seeds + Tr.S(" сид.", " seeds") + ")" : "");
        }

        // Сегодня — только время, иначе дата и время (местные).
        public static string Local(DateTime utc, DateTime utcNow)
        {
            if (utc == DateTime.MinValue) return "";
            DateTime l = utc.ToLocalTime();
            return l.Date == utcNow.ToLocalTime().Date ? l.ToString("HH:mm", CultureInfo.InvariantCulture)
                                                      : l.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);
        }

        public static string DisplayName(DlItem it)
        {
            if (!string.IsNullOrEmpty(it.FileName)) return it.FileName;
            if (it.IsTorrent)
            {
                // Magnet без метаданных: имя из dn, иначе начало info-hash — ссылку целиком в списке не показывать.
                string err;
                BtMagnet m = it.Url.Length > 0 ? BtMagnet.Parse(it.Url, out err) : null;
                if (m != null && m.Name.Length > 0) return DlFiles.SanitizeName(m.Name);
                return "magnet " + (it.InfoHash.Length >= 8 ? it.InfoHash.Substring(0, 8) : it.InfoHash);
            }
            string fromUrl = DlFiles.NameFromUrl(it.Url);
            return fromUrl.Length > 0 ? fromUrl : DlLog.Redact(it.Url);
        }

        // ---------- ссылки ----------

        // Каждая непустая строка — одна или несколько ссылок через пробел. Строка без единой ссылки http/https уходит в bad
        // целиком: пользователь увидит, что именно не принято. Повторы убираются, порядок сохраняется.
        public static void ParseLinks(string text, List<string> ok, List<string> bad)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                int found = 0;
                foreach (string token in line.Split(new[] { ' ', '\t', '"', '\'', '<', '>' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string url = token.TrimEnd(',', ';', ')', ']', '}', '.');
                    if (!DlHttp.IsAllowedScheme(url)) continue;
                    found++;
                    if (seen.Add(url)) ok.Add(url);
                }
                if (found == 0 && bad != null) bad.Add(line);
            }
        }

        // magnet-ссылки с info-hash из произвольного текста: разделители — пробелы, кавычки и угловые скобки, как у ParseLinks.
        // Повторы одного и того же хеша убираются: в тексте одна раздача бывает дважды с разными трекерами.
        public static void ParseMagnets(string text, List<string> ok)
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string token in (text ?? "").Split(new[] { ' ', '\t', '\r', '\n', '"', '\'', '<', '>' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!token.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) continue;
                string link = token.TrimEnd(',', ';', ')', ']', '}');
                string err;
                BtMagnet m = BtMagnet.Parse(link, out err);
                if (m == null || m.SwarmHash == null) continue;
                if (seen.Add(Bencode.Hex(m.SwarmHash))) ok.Add(link);
            }
        }

        // Ярлык интернета (.url): строка URL= в разделе [InternetShortcut]. null — не ярлык или ссылка не http(s).
        public static string LinkFromShortcut(string content)
        {
            bool section = false;
            foreach (string raw in (content ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("[")) { section = string.Equals(line, "[InternetShortcut]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!section || !line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) continue;
                string url = line.Substring(4).Trim();
                return DlHttp.IsAllowedScheme(url) ? url : null;
            }
            return null;
        }

        // Открыть сам файл без вопроса можно только не исполняемый; остальное — после подтверждения с подписью издателя.
        public static bool OpenNeedsConfirm(string fileName)
        {
            return DlFiles.IsDangerous(fileName);
        }
    }
}
