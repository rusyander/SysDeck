// Windows Process Cleaner — «Загрузки», видео: запуск видео-загрузки — сегменты, журнал частей, трансляция, склейка.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Папка частей <цель>.wpcmedia: у каждой дорожки один сплошной файл данных (init, затем сегменты строго по порядку),
// сегмент, скачанный раньше очереди, ждёт в seg-<дорожка>-<номер>.bin (всего не больше 64 МиБ), журнал media.json —
// что уже дописано и что лежит в .bin. Журнал пишется только после сброса данных на диск (Flush(true)), поэтому
// после убитого процесса он может отставать, но не опережать: файл данных обрезается до записанной в журнале длины,
// недописанное качается заново. Сегмент, который первым в очереди, пишется прямо в файл данных — просмотр во время
// загрузки видит растущий файл, а большой одиночный файл не копируется второй раз. В журнале нет адресов и ключей:
// только номера и короткие хеши сегментов; ключи AES при продолжении запрашиваются снова.
// Склейка — MdHooks.Mux (ядро), готовый файл проходит тот же DlFinish.Complete, что и обычная загрузка.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace WindowsProcessCleaner.Downloads
{
    internal sealed class DlMediaRun : IDlRun
    {
        public const long BufferCap = 64L * 1024 * 1024;
        public const string JournalName = "media.json";
        public const string PartsSuffix = ".wpcmedia";
        private const int QuickRetries = 3;
        private const int LiveStartSegments = 3;
        private const int Pending = 0, InBin = 1, Appended = 2, Skipped = 3;

        // Сколько секунд сверх 3×TargetDuration ждать новых сегментов трансляции; тесты сокращают.
        internal static int LiveIdleExtraSeconds = 30;

        // ---------- манифест, уже извлечённый окном добавления (yt-dlp): не извлекать второй раз ----------
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, MdManifest> Cache = new Dictionary<string, MdManifest>(StringComparer.Ordinal);

        internal static void RememberManifest(string itemId, MdManifest m)
        {
            if (string.IsNullOrEmpty(itemId)) return;
            lock (CacheGate)
            {
                if (m == null) Cache.Remove(itemId);
                else Cache[itemId] = m;
            }
        }

        internal static MdManifest Remembered(string itemId)
        {
            MdManifest m;
            lock (CacheGate) return Cache.TryGetValue(itemId ?? "", out m) ? m : null;
        }

        // ---------- состояние ----------
        private sealed class Seg
        {
            public MdSegment Src;          // null — известен только по журналу (трансляция до паузы)
            public long Seq;
            public string Id = "";
            public double Duration;
            public int State;
            public long Bytes;
            public bool Busy;
        }

        private sealed class Track
        {
            public MdTrack Src;
            public string Id = "", FileId = "", File = "";
            public MdTrackKind Kind;
            public MdLayout Layout;
            public bool Live;
            public readonly List<Seg> Segs = new List<Seg>();
            public readonly Dictionary<long, Seg> BySeq = new Dictionary<long, Seg>();
            public int Head;               // первый сегмент, который ещё не в файле данных
            public long Committed;         // байт в файле данных, сброшенных на диск
            public bool InitDone;
            public bool Writing, Draining;
            public long Buffered;          // байт в .bin
            public double Seconds;
        }

        private sealed class Claim
        {
            public Track Track;
            public Seg Seg;
            public bool Direct;
            public bool Committed;
            public string Host = "";
        }

        private readonly DlItem _item;
        private readonly DlSettings _settings;
        private readonly DlTokenBucket _global;
        private readonly IDlEnvironment _env;
        private readonly IDlTransferHost _host;
        public readonly DlTokenBucket Bucket;
        public readonly DlSpeedMeter Meter = new DlSpeedMeter();
        private readonly MdFetcher _fx;
        private readonly object _gate = new object();
        private readonly object _journalGate = new object();
        private readonly object _extractGate = new object();
        private readonly object _keyGate = new object();
        private readonly Dictionary<string, byte[]> _keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _hostBusy = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Track> _tracks = new List<Track>();
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private Thread _thread;
        private volatile int _stop, _killed;
        private volatile bool _done, _liveEnded;
        private DlFailure _failure;
        private string _partsDir = "";
        private MdManifest _manifest;
        private bool _live;
        private int _generation;
        private bool _reextracted;
        private int _rr, _busy;
        private long _journalAt = -100000, _persistAt = -100000;
        private string _variantId = "";
        private int _variantHeight;
        private long _variantBandwidth;

        public DlMediaRun(DlItem item, DlSettings settings, DlTokenBucket global, IDlEnvironment env, IDlTransferHost host)
        {
            _item = item;
            _settings = settings ?? new DlSettings();
            _global = global ?? new DlTokenBucket(0);
            _env = env ?? new DlSystemEnvironment();
            _host = host;
            if (_item.Media == null) _item.Media = new DlMedia();
            Bucket = new DlTokenBucket((long)item.LimitKBps * 1024);
            _fx = new MdFetcher(item, _settings, Bucket, _global, Meter, Stopping);
        }

        public DlItem Item { get { return _item; } }
        DlTokenBucket IDlRun.Bucket { get { return Bucket; } }
        DlSpeedMeter IDlRun.Meter { get { return Meter; } }
        public bool Finished { get { return _done; } }
        public bool StopRequested { get { return _stop != 0; } }
        internal string PartsDir { get { return _partsDir; } }

        public DlFailure Failure
        {
            get { lock (_gate) return _failure; }
        }

        public void Start()
        {
            _thread = new Thread(Run);
            _thread.IsBackground = true;
            _thread.Name = "wpc-md-" + _item.Id;
            _thread.Start();
        }

        // Пауза: сегменты в работе обрываются, записанное остаётся, журнал сохраняется.
        public void RequestStop()
        {
            _stop = 1;
            _fx.AbortAll();
            lock (_gate) Monitor.PulseAll(_gate);
        }

        public bool Join(int timeoutMs) { return _thread == null || _thread.Join(timeoutMs); }

        // Для проверки «процесс убит»: потоки выходят, но ни журнал, ни запись загрузки больше не пишутся.
        internal void KillForTest()
        {
            _killed = 1;
            _stop = 1;
            _fx.AbortAll();
            lock (_gate) Monitor.PulseAll(_gate);
        }

        private bool Stopping() { return _stop != 0; }

        private void SetFailure(DlFailure f)
        {
            lock (_gate) if (_failure == null) _failure = f;
        }

        private bool HasFailure { get { lock (_gate) return _failure != null; } }

        private void Note(string text)
        {
            if (_host != null) _host.Journal(_item, text);
        }

        // ------------------------------------------------------------------ //
        //  Поток запуска
        // ------------------------------------------------------------------ //
        private void Run()
        {
            try
            {
                DlFailure f = Prepare();
                if (f != null) { if (!Stopping()) SetFailure(f); return; }
                if (Stopping()) return;
                _item.Media.Phase = "segments";
                UpdateCounters();
                Persist(true);
                RunWorkers();
                foreach (Track rt in _tracks) Drain(rt);
                if (Stopping() || HasFailure) return;
                if (!AllAppended())
                {
                    SetFailure(DlFailure.Make(DlErrorKind.Network, Tr.S("получены не все сегменты", "not all segments were received")));
                    return;
                }
                SaveJournal(true);
                f = Finish();
                if (f != null && !Stopping()) SetFailure(f);
            }
            catch (Exception ex)
            {
                if (!Stopping())
                {
                    DlLog.Report(ex);
                    SetFailure(DlFailure.Make(DlHttp.IsDiskFull(ex) ? DlErrorKind.Disk : DlErrorKind.Network, ex.Message));
                }
            }
            finally
            {
                if (_partsDir.Length > 0 && Directory.Exists(_partsDir)) SaveJournal(true);
                _item.ActiveConnections = 0;
                UpdateCounters();
                _done = true;
            }
        }

        // ---------- подготовка ----------
        private DlFailure Prepare()
        {
            DlMedia md = _item.Media;
            if (!DlHttp.IsAllowedScheme(_item.Url))
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("поддерживаются только ссылки http и https", "only http and https links are supported"));
            bool ytdlp = md.Source == MdSource.Ytdlp;
            JVal journal = null;
            bool placed = false;
            List<MdTrack> chosen = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                DlFailure f;
                MdManifest m = AcquireManifest(attempt > 0, out f);
                if (f != null || m == null) return f ?? DlFailure.Make(DlErrorKind.Client, Tr.S("манифест не получен", "no manifest"));
                if (Stopping()) return null;
                if (m.Refused.Length > 0)
                {
                    Note(Tr.S("отказ: ", "refused: ") + m.Refused);
                    return DlFailure.Make(DlErrorKind.Policy, m.Refused);
                }
                _manifest = m;
                if (md.Title.Length == 0 && m.Title.Length > 0) md.Title = m.Title;
                if (!placed)
                {
                    f = EnsurePlace(m);
                    if (f != null) return f;
                    journal = DlPaths.ReadJson(Path.Combine(_partsDir, JournalName));
                    placed = true;
                }
                chosen = Choose(m, journal, out f);
                if (f != null) return f;
                bool expired = false;
                foreach (MdTrack t in chosen)
                {
                    if (MdLoader.ResolveTrack(_fx, t, md.Headers, ytdlp, out f)) continue;
                    if (Stopping()) return null;
                    if (ytdlp && attempt == 0 && f.Kind == DlErrorKind.LinkExpired && MdHooks.Extract != null) { expired = true; break; }
                    if (f.Kind == DlErrorKind.Policy) Note(Tr.S("отказ: ", "refused: ") + f.Message);
                    return f;
                }
                if (!expired) break;
                _reextracted = true;
                Note(Tr.S("ссылки устарели — повторное извлечение", "links expired — extracting again"));
            }
            if (Stopping()) return null;

            DlFailure bf = BuildTracks(chosen, journal);
            if (bf != null) return bf;
            _live = false;
            foreach (Track rt in _tracks) if (rt.Live) _live = true;
            md.Live = _live;
            DlFailure fi = FetchInits();
            if (fi != null) return fi;
            SaveJournal(true);
            int segs = 0;
            foreach (Track rt in _tracks) segs += rt.Segs.Count;
            Note(Tr.S("видео: дорожек ", "video: tracks ") + _tracks.Count + Tr.S(", сегментов ", ", segments ") + segs
                 + (_live ? Tr.S(", трансляция", ", live") : ""));
            return null;
        }

        private MdManifest AcquireManifest(bool forceExtract, out DlFailure f)
        {
            f = null;
            DlMedia md = _item.Media;
            if (md.Source == MdSource.Ytdlp)
            {
                MdManifest cached = forceExtract ? null : Remembered(_item.Id);
                DateTime now = _env.UtcNow;
                if (cached != null)
                {
                    DateTime expires = cached.ExpiresUtc != DateTime.MinValue ? cached.ExpiresUtc
                                     : md.ExtractedUtc != DateTime.MinValue ? md.ExtractedUtc.AddHours(5) : DateTime.MinValue;
                    if (expires > now.AddMinutes(1)) return cached;
                }
                return Extract(forceExtract, out f);
            }
            string url = md.ManifestUrl.Length > 0 ? md.ManifestUrl : _item.Url;
            return MdLoader.Load(_fx, url, md.Headers, out f);
        }

        private MdManifest Extract(bool expired, out DlFailure f)
        {
            f = null;
            MdHooks.ExtractFn ex = MdHooks.Extract;
            if (ex == null)
            {
                f = DlFailure.Make(expired ? DlErrorKind.LinkExpired : DlErrorKind.Policy, Tr.S("yt-dlp недоступен — ссылки не обновить", "yt-dlp is unavailable — the links cannot be refreshed"));
                return null;
            }
            string page = string.IsNullOrEmpty(_item.PageUrl) ? _item.Url : _item.PageUrl;
            Note(Tr.S("извлечение ссылок через yt-dlp: ", "extracting links via yt-dlp: ") + DlLog.Redact(page));
            string err = null;
            MdManifest m = null;
            try { m = ex(page, Stopping, out err); }
            catch (Exception e) { err = e.Message; m = null; }
            if (m == null)
            {
                f = DlFailure.Make(expired ? DlErrorKind.LinkExpired : DlErrorKind.Client,
                                   string.IsNullOrEmpty(err) ? Tr.S("yt-dlp не вернул форматы", "yt-dlp returned no formats") : err);
                return null;
            }
            _item.Media.ExtractedUtc = _env.UtcNow;
            RememberManifest(_item.Id, m);
            return m;
        }

        private static string ExtFor(MdOutput o, bool webm)
        {
            switch (o)
            {
                case MdOutput.Mp4: return ".mp4";
                case MdOutput.WebM: return ".webm";
                case MdOutput.M4a: return ".m4a";
                case MdOutput.Mp3: return ".mp3";
                case MdOutput.Ts: return ".ts";
                default: return webm ? ".webm" : ".mp4";
            }
        }

        // Имя цели, папка и папка частей рядом с целью.
        private DlFailure EnsurePlace(MdManifest m)
        {
            DlMedia md = _item.Media;
            bool named = !string.IsNullOrEmpty(_item.FileName);
            string name = _item.FileName ?? "";
            if (!named)
            {
                MdVariant v = FindVariant(m, md.VariantId);
                if (v == null && m.Variants.Count > 0) v = m.Variants[0];
                bool webm = v != null && v.Main != null && v.Main.Layout == MdLayout.WebM;
                string stem = md.Title.Length > 0 ? md.Title : m.Title;
                if (string.IsNullOrEmpty(stem))
                {
                    string fromUrl = DlFiles.NameFromUrl(string.IsNullOrEmpty(_item.PageUrl) ? _item.Url : _item.PageUrl) ?? "";
                    try { stem = Path.GetFileNameWithoutExtension(fromUrl); } catch (ArgumentException) { stem = ""; }
                }
                if (string.IsNullOrEmpty(stem)) stem = "video";
                name = DlFiles.SanitizeName(stem + ExtFor(md.Output, webm));
            }
            {
                string rawFolder = string.IsNullOrEmpty(_item.Folder) ? DlFiles.FolderFor(_settings, _item.Url, name) : _item.Folder;
                string why;
                string folder = DlFiles.CheckFolder(rawFolder, out why);
                if (folder == null) return DlFailure.Make(DlErrorKind.Policy, why);
                try { Directory.CreateDirectory(folder); }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("папка недоступна: ", "the folder is not available: ") + ex.Message); }
                if (!named)
                {
                    string unique = _host.ReserveName(_item, folder, name);
                    if (unique == null) return DlFailure.Make(DlErrorKind.Disk, Tr.S("не найдено свободное имя файла", "no free file name found"));
                    _item.FileName = unique;
                }
                _item.Folder = folder;
            }
            string target = DlFiles.PathInside(_item.Folder, _item.FileName);
            if (target == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
            string parts = md.PartsDir;
            try
            {
                // Папка частей — только внутри папки загрузки: путь из записи мог быть подменён.
                if (parts.Length == 0 || !DlFiles.IsSameOrUnder(Path.GetFullPath(parts), Path.GetFullPath(_item.Folder))) parts = target + PartsSuffix;
            }
            catch (Exception) { parts = target + PartsSuffix; }
            if (DlFiles.IsReparse(parts)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте папки частей ссылка — не трогаю", "a link sits where the parts folder is — left alone"));
            if (File.Exists(parts)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте папки частей лежит файл", "a file sits where the parts folder is"));
            try { Directory.CreateDirectory(parts); }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("папка частей не создаётся: ", "the parts folder cannot be created: ") + ex.Message); }
            md.PartsDir = parts;
            _partsDir = parts;
            Persist(true);
            return null;
        }

        private static MdVariant FindVariant(MdManifest m, string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (MdVariant v in m.Variants) if (v.Id == id) return v;
            return null;
        }

        // Замена пропавшего варианта: не выше выбранной высоты, ближайший по битрейту. Высота 0 — не ограничивать.
        internal static MdVariant ChooseVariant(MdManifest m, int maxHeight, long wantBandwidth)
        {
            if (m == null || m.Variants.Count == 0) return null;
            List<MdVariant> fit = new List<MdVariant>();
            foreach (MdVariant v in m.Variants)
                if (maxHeight <= 0 || v.Height <= 0 || v.Height <= maxHeight) fit.Add(v);
            if (fit.Count == 0)
            {
                MdVariant low = m.Variants[0];
                foreach (MdVariant v in m.Variants)
                    if (v.Height < low.Height || (v.Height == low.Height && v.Bandwidth < low.Bandwidth)) low = v;
                return low;
            }
            MdVariant best = fit[0];
            foreach (MdVariant v in fit)
            {
                if (wantBandwidth > 0)
                {
                    long dv = Math.Abs(v.Bandwidth - wantBandwidth), db = Math.Abs(best.Bandwidth - wantBandwidth);
                    if (dv < db || (dv == db && v.Bandwidth < best.Bandwidth)) best = v;
                }
                else if (v.Height > best.Height || (v.Height == best.Height && v.Bandwidth > best.Bandwidth)) best = v;
            }
            return best;
        }

        private List<MdTrack> Choose(MdManifest m, JVal journal, out DlFailure f)
        {
            f = null;
            DlMedia md = _item.Media;
            List<MdTrack> list = new List<MdTrack>();
            JVal jv = journal == null ? null : journal.Get("variant");
            int jh = DlJson.Int(jv, "h", 0);
            long jbw = DlJson.Long(jv, "bw", 0);
            MdVariant v = FindVariant(m, md.VariantId);
            if (v != null)
            {
                if (jv == null) { _variantHeight = v.Height; _variantBandwidth = v.Bandwidth; }
            }
            else
            {
                // Журнал (продолжение) важнее настройки: возобновлённая запись обязана остаться в том же качестве.
                v = ChooseVariant(m, jh > 0 ? jh : _settings.MdMaxHeight, jbw);
                if (v == null) { f = DlFailure.Make(DlErrorKind.Client, Tr.S("в потоке нет вариантов качества", "the stream has no quality variants")); return null; }
                if (md.VariantId.Length > 0)
                    Note(Tr.S("вариант «", "variant «") + md.VariantId + Tr.S("» недоступен, выбран «", "» is unavailable, picked «") + v.Id + "» (" + v.Label + ")");
                if (jv == null) { _variantHeight = v.Height; _variantBandwidth = v.Bandwidth; }
            }
            if (jv != null) { _variantHeight = jh; _variantBandwidth = jbw; }
            _variantId = md.VariantId.Length > 0 ? md.VariantId : v.Id;
            if (v.Main == null) { f = DlFailure.Make(DlErrorKind.Client, Tr.S("у варианта нет дорожки", "the variant has no track")); return null; }

            MdTrack audio = null;
            if (md.AudioId.Length > 0) audio = FindTrack(m.Audio, md.AudioId);
            if (audio == null && v.AudioGroup.Length > 0)
            {
                if (md.AudioId.Length > 0) Note(Tr.S("звуковая дорожка «", "audio track «") + md.AudioId + Tr.S("» недоступна, взята другая", "» is unavailable, another one is used"));
                foreach (MdTrack t in m.Audio)
                    if (t.GroupId == v.AudioGroup && (audio == null || (t.Default && !audio.Default))) audio = t;
                if (audio == null && m.Audio.Count > 0) audio = m.Audio[0];
            }
            bool audioOnly = md.Output == MdOutput.M4a || md.Output == MdOutput.Mp3;
            if (!(audioOnly && audio != null)) list.Add(v.Main);
            if (audio != null && audio != v.Main) list.Add(audio);
            foreach (string sid in md.SubtitleIds)
            {
                MdTrack s = FindTrack(m.Subtitles, sid);
                if (s != null && !list.Contains(s)) list.Add(s);
                else if (s == null) Note(Tr.S("субтитры «", "subtitles «") + sid + Tr.S("» недоступны", "» are unavailable"));
            }
            return list;
        }

        private static MdTrack FindTrack(List<MdTrack> list, string id)
        {
            foreach (MdTrack t in list) if (t.Id == id) return t;
            return null;
        }

        // Та же дорожка в свежем манифесте: по Id, иначе по format_id yt-dlp.
        private static MdTrack FindSame(MdManifest m, MdTrack old)
        {
            List<MdTrack> all = new List<MdTrack>();
            foreach (MdVariant v in m.Variants) if (v.Main != null) all.Add(v.Main);
            all.AddRange(m.Audio);
            all.AddRange(m.Subtitles);
            foreach (MdTrack t in all) if (t.Id == old.Id) return t;
            if (old.FormatId.Length > 0)
                foreach (MdTrack t in all) if (t.FormatId == old.FormatId) return t;
            return null;
        }

        internal static string SafeId(string id)
        {
            string s = id ?? "";
            bool ok = s.Length > 0 && s.Length <= 48;
            foreach (char c in s)
                if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '-' || c == '_'))) ok = false;
            return ok ? s : "t" + ShortHash(s);
        }

        private static string ShortHash(string s)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] d = h.ComputeHash(Encoding.UTF8.GetBytes(s ?? ""));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(d[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // Опознание сегмента между запусками: номер, адрес без query, диапазон. У yt-dlp адреса меняются при каждом
        // извлечении — там только номер и диапазон.
        internal static string SegIdentity(MdSegment s, bool ignoreUrl)
        {
            string url = ignoreUrl ? "" : DlMotw.StripQuery(s.Url);
            return ShortHash(s.Sequence.ToString(CultureInfo.InvariantCulture) + "|" + url + "|" + s.Offset.ToString(CultureInfo.InvariantCulture)
                             + "|" + s.Length.ToString(CultureInfo.InvariantCulture));
        }

        private static string PreviewExt(MdLayout l)
        {
            switch (l)
            {
                case MdLayout.Fmp4:
                case MdLayout.Mp4: return ".mp4";
                case MdLayout.WebM: return ".webm";
                case MdLayout.Adts: return ".aac";
                case MdLayout.Mp3: return ".mp3";
                default: return ".ts";
            }
        }

        private string DataPath(Track rt) { return Path.Combine(_partsDir, rt.File); }

        private string BinPath(Track rt, long seq)
        {
            return Path.Combine(_partsDir, "seg-" + rt.FileId + "-" + seq.ToString(CultureInfo.InvariantCulture) + ".bin");
        }

        private DlFailure BuildTracks(List<MdTrack> chosen, JVal journal)
        {
            DlMedia md = _item.Media;
            bool ytdlp = md.Source == MdSource.Ytdlp;
            Dictionary<string, JVal> jt = new Dictionary<string, JVal>(StringComparer.Ordinal);
            JVal jtracks = journal == null ? null : journal.Get("tracks");
            if (jtracks != null && jtracks.Kind == JKind.Arr)
                foreach (JVal j in jtracks.V)
                {
                    string id = DlJson.Str(j, "id", "");
                    if (id.Length > 0 && !jt.ContainsKey(id)) jt[id] = j;
                }

            bool first = true;
            md.PreviewPath = "";
            foreach (MdTrack t in chosen)
            {
                Track rt = new Track();
                rt.Src = t;
                rt.Id = t.Id;
                rt.FileId = SafeId(t.Id);
                rt.Kind = t.Kind;
                rt.Layout = t.Layout;
                rt.Live = t.Live;
                bool preview = md.Watch && first && t.Kind != MdTrackKind.Subtitles;
                rt.File = preview ? "preview" + PreviewExt(t.Layout) : rt.FileId + ".data";
                first = false;
                List<MdSegment> segs = new List<MdSegment>(t.Segments);
                segs.Sort(delegate(MdSegment a, MdSegment b) { return a.Sequence.CompareTo(b.Sequence); });
                foreach (MdSegment s in segs)
                {
                    if (rt.BySeq.ContainsKey(s.Sequence)) continue;
                    Seg x = NewSeg(s, ytdlp);
                    rt.Segs.Add(x);
                    rt.BySeq[s.Sequence] = x;
                }
                JVal j;
                if (jt.TryGetValue(t.Id, out j))
                {
                    jt.Remove(t.Id);
                    if (!MergeJournal(rt, j, ytdlp))
                    {
                        Note(Tr.S("части дорожки «", "parts of track «") + t.Id + Tr.S("» не подходят к потоку — дорожка качается заново", "» do not match the stream — the track starts over"));
                        ResetTrack(rt, j, ytdlp);
                    }
                }
                else
                {
                    DeleteOwn(Path.Combine(_partsDir, rt.File));
                    if (rt.Live && rt.Segs.Count > LiveStartSegments)
                    {
                        // Запись трансляции начинается у живого края, а не с начала окна.
                        int drop = rt.Segs.Count - LiveStartSegments;
                        for (int i = 0; i < drop; i++) rt.BySeq.Remove(rt.Segs[i].Seq);
                        rt.Segs.RemoveRange(0, drop);
                    }
                }
                if (preview) md.PreviewPath = DataPath(rt);
                _tracks.Add(rt);
            }
            // Дорожки прошлого выбора — свои файлы по точным именам.
            foreach (JVal j in jt.Values) DeleteJournalTrackFiles(j);
            UpdateCounters();
            return null;
        }

        private static Seg NewSeg(MdSegment s, bool ytdlp)
        {
            Seg x = new Seg();
            x.Src = s;
            x.Seq = s.Sequence;
            x.Id = SegIdentity(s, ytdlp);
            x.Duration = s.Duration;
            return x;
        }

        // Продолжение по журналу. false — части не подходят (поток другой, файл данных короче записанного).
        private bool MergeJournal(Track rt, JVal j, bool ytdlp)
        {
            string oldFile = DlJson.Str(j, "file", "");
            if (oldFile.Length == 0 || DlFiles.SanitizeName(oldFile) != oldFile) return false;
            string oldPath = Path.Combine(_partsDir, oldFile), newPath = DataPath(rt);
            if (oldFile != rt.File && File.Exists(oldPath) && !File.Exists(newPath) && !DlFiles.IsReparse(oldPath))
            {
                try { File.Move(oldPath, newPath); }
                catch (Exception) { return false; }
            }
            long committed = DlJson.Long(j, "committed", 0);
            long len = -1;
            try { if (File.Exists(newPath) && !DlFiles.IsReparse(newPath)) len = new FileInfo(newPath).Length; }
            catch (Exception) { len = -1; }
            if (committed < 0 || len < committed) return committed == 0 && len < 0 ? MergeEntries(rt, j, 0) : false;
            if (!MergeEntries(rt, j, committed)) return false;
            try
            {
                if (len > committed)
                    using (FileStream fs = new FileStream(newPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                        fs.SetLength(committed);
            }
            catch (Exception) { return false; }
            return true;
        }

        private bool MergeEntries(Track rt, JVal j, long committed)
        {
            JVal arr = j.Get("segs");
            Dictionary<long, JVal> entries = new Dictionary<long, JVal>();
            long journalMaxSeq = long.MinValue, appendedMaxSeq = long.MinValue;
            if (arr != null && arr.Kind == JKind.Arr)
                foreach (JVal e in arr.V)
                {
                    if (e.Kind != JKind.Arr || e.V.Count < 5) return false;
                    long q;
                    if (!long.TryParse(e.V[0].Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out q)) return false;
                    entries[q] = e;
                    if (q > journalMaxSeq) journalMaxSeq = q;
                    if (EntryInt(e, 2) == Appended && q > appendedMaxSeq) appendedMaxSeq = q;
                }

            if (rt.Live)
            {
                // Сегменты нового окна старше уже записанного — позади, их не качаем.
                rt.Segs.RemoveAll(delegate(Seg s)
                {
                    bool behind = !entries.ContainsKey(s.Seq) && s.Seq < appendedMaxSeq;
                    if (behind) rt.BySeq.Remove(s.Seq);
                    return behind;
                });
                long newMin = long.MaxValue;
                foreach (Seg s in rt.Segs) if (!entries.ContainsKey(s.Seq) && s.Seq < newMin) newMin = s.Seq;
                foreach (KeyValuePair<long, JVal> kv in entries)
                {
                    if (rt.BySeq.ContainsKey(kv.Key)) continue;
                    Seg x = new Seg();
                    x.Seq = kv.Key;
                    x.Id = kv.Value.V[1].Raw ?? "";
                    x.Duration = EntryInt(kv.Value, 4) / 1000.0;
                    rt.Segs.Add(x);
                    rt.BySeq[x.Seq] = x;
                }
                rt.Segs.Sort(delegate(Seg a, Seg b) { return a.Seq.CompareTo(b.Seq); });
                if (journalMaxSeq != long.MinValue && newMin != long.MaxValue && newMin > journalMaxSeq + 1)
                    Note(Tr.S("пропуск ", "gap of ") + (newMin - journalMaxSeq - 1) + Tr.S(" сегментов (пауза трансляции)", " segments (live paused)"));
            }
            else
            {
                foreach (long q in entries.Keys) if (!rt.BySeq.ContainsKey(q)) return false;
            }

            int missingLive = 0;
            foreach (Seg s in rt.Segs)
            {
                JVal e;
                if (!entries.TryGetValue(s.Seq, out e)) { s.State = Pending; continue; }
                if (s.Src != null && (e.V[1].Raw ?? "") != s.Id) return false;
                int st = EntryInt(e, 2);
                long b = EntryLong(e, 3);
                if (st == Appended) { s.State = Appended; s.Bytes = b; }
                else if (st == Skipped) s.State = Skipped;
                else if (st == InBin && BinOk(BinPath(rt, s.Seq), b)) { s.State = InBin; s.Bytes = b; }
                else s.State = Pending;
                if (s.Src == null && s.State == Pending) { s.State = Skipped; missingLive++; }
            }
            if (missingLive > 0) Note(Tr.S("пропуск ", "gap of ") + missingLive + Tr.S(" сегментов: окно трансляции ушло вперёд", " segments: the live window moved on"));

            // Дописанные сегменты обязаны идти подряд с начала: иначе журнал не про этот файл.
            int head = 0;
            while (head < rt.Segs.Count && (rt.Segs[head].State == Appended || rt.Segs[head].State == Skipped)) head++;
            long sum = 0;
            for (int i = 0; i < rt.Segs.Count; i++)
            {
                if (rt.Segs[i].State == Appended && i >= head) return false;
                if (rt.Segs[i].State == Appended) sum += rt.Segs[i].Bytes;
            }
            bool init = DlJson.Bool(j, "init", false);
            if (sum > committed) return false;
            rt.Head = head;
            rt.Committed = committed;
            rt.InitDone = init;
            rt.Buffered = 0;
            rt.Seconds = 0;
            foreach (Seg s in rt.Segs)
            {
                if (s.State == InBin) rt.Buffered += s.Bytes;
                if (s.State == Appended) rt.Seconds += s.Duration;
            }
            return true;
        }

        private static int EntryInt(JVal e, int i)
        {
            int v;
            return i < e.V.Count && int.TryParse(e.V[i].Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static long EntryLong(JVal e, int i)
        {
            long v;
            return i < e.V.Count && long.TryParse(e.V[i].Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        private static bool BinOk(string path, long bytes)
        {
            try { return File.Exists(path) && !DlFiles.IsReparse(path) && new FileInfo(path).Length == bytes; }
            catch (Exception) { return false; }
        }

        private void ResetTrack(Track rt, JVal j, bool ytdlp)
        {
            DeleteJournalTrackFiles(j);
            DeleteOwn(DataPath(rt));
            rt.Segs.Clear();
            rt.BySeq.Clear();
            List<MdSegment> segs = new List<MdSegment>(rt.Src.Segments);
            segs.Sort(delegate(MdSegment a, MdSegment b) { return a.Sequence.CompareTo(b.Sequence); });
            foreach (MdSegment s in segs)
            {
                if (rt.BySeq.ContainsKey(s.Sequence)) continue;
                Seg x = NewSeg(s, ytdlp);
                rt.Segs.Add(x);
                rt.BySeq[s.Sequence] = x;
            }
            rt.Head = 0;
            rt.Committed = 0;
            rt.InitDone = false;
            rt.Buffered = 0;
            rt.Seconds = 0;
        }

        // Файлы дорожки из журнала: файл данных и .bin — только свои, по точным именам.
        private void DeleteJournalTrackFiles(JVal j)
        {
            DeleteTrackFiles(_partsDir, j);
        }

        private static void DeleteTrackFiles(string partsDir, JVal j)
        {
            string file = DlJson.Str(j, "file", "");
            if (file.Length > 0 && DlFiles.SanitizeName(file) == file) DeleteOwn(Path.Combine(partsDir, file));
            string fileId = SafeId(DlJson.Str(j, "id", ""));
            JVal arr = j.Get("segs");
            if (arr != null && arr.Kind == JKind.Arr)
                foreach (JVal e in arr.V)
                    if (e.Kind == JKind.Arr && e.V.Count > 0)
                    {
                        long q;
                        if (long.TryParse(e.V[0].Raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out q))
                            DeleteOwn(Path.Combine(partsDir, "seg-" + fileId + "-" + q.ToString(CultureInfo.InvariantCulture) + ".bin"));
                    }
        }

        private static void DeleteOwn(string path)
        {
            try
            {
                if (File.Exists(path) && !DlFiles.IsReparse(path)) File.Delete(path);
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // Удалить папку частей записи: свои файлы по журналу и точным именам, затем папку, если пуста. null — удалено.
        internal static string DeleteParts(DlItem item)
        {
            if (item == null || item.Media == null || string.IsNullOrEmpty(item.Media.PartsDir)) return null;
            string dir = item.Media.PartsDir;
            try
            {
                if (!Directory.Exists(dir)) return null;
                if (DlFiles.IsReparse(dir)) return Tr.S("на месте папки частей ссылка — не трогаю", "a link sits where the parts folder is — left alone");
                if (!string.IsNullOrEmpty(item.Folder) && !DlFiles.IsSameOrUnder(Path.GetFullPath(dir), Path.GetFullPath(item.Folder)))
                    return Tr.S("папка частей вне папки загрузки — не трогаю", "the parts folder is outside the download folder — left alone");
                JVal journal = DlPaths.ReadJson(Path.Combine(dir, JournalName));
                JVal tracks = journal == null ? null : journal.Get("tracks");
                if (tracks != null && tracks.Kind == JKind.Arr) foreach (JVal j in tracks.V) DeleteTrackFiles(dir, j);
                foreach (string n in new[] { JournalName, JournalName + ".bak", JournalName + ".tmp", "out.mp4", "out.webm", "out.m4a", "out.mp3", "out.ts",
                                             "out.mp4.tmp", "out.webm.tmp", "out.m4a.tmp", "out.mp3.tmp", "out.ts.tmp" })
                    DeleteOwn(Path.Combine(dir, n));
                if (Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir, false);
                item.Media.PreviewPath = "";
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------- init-сегменты: один раз в начало файла данных ----------
        private DlFailure FetchInits()
        {
            foreach (Track rt in _tracks)
            {
                if (Stopping()) return null;
                MdSegment first = null;
                lock (_gate)
                {
                    if (rt.InitDone || rt.Committed > 0) { rt.InitDone = true; continue; }
                    foreach (Seg s in rt.Segs) if (s.Src != null) { first = s.Src; break; }
                }
                if (first == null || first.InitUrl.Length == 0)
                {
                    lock (_gate) rt.InitDone = true;
                    continue;
                }
                byte[] init = null;
                DlFailure f = null;
                for (int quick = 0; ; quick++)
                {
                    string fu, ct;
                    init = _fx.GetBytes(first.InitUrl, first.InitOffset, first.InitLength, MdLoader.MaxManifest, Headers(rt), true, out fu, out ct, out f);
                    if (f == null || Stopping()) break;
                    if ((f.Kind != DlErrorKind.Network && f.Kind != DlErrorKind.Server) || quick >= QuickRetries || !SleepUnlessStopped(1000 * (quick + 1))) break;
                }
                if (Stopping()) return null;
                if (f != null) return f;
                string path = DataPath(rt);
                if (DlFiles.IsReparse(path)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте файла данных ссылка", "a link sits where the data file is"));
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
                    {
                        fs.Write(init, 0, init.Length);
                        fs.Flush(true);
                    }
                }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, ex.Message); }
                lock (_gate)
                {
                    rt.Committed = init.Length;
                    rt.InitDone = true;
                }
            }
            return null;
        }

        private Dictionary<string, string> Headers(Track rt)
        {
            return MdLoader.Merge(_item.Media.Headers, rt.Src == null ? null : rt.Src.Headers);
        }

        // ------------------------------------------------------------------ //
        //  Сегменты
        // ------------------------------------------------------------------ //
        private void RunWorkers()
        {
            int n = _item.Connections > 0 ? _item.Connections : _settings.Segments;
            n = Math.Max(1, Math.Min(16, n));
            _liveEnded = !_live || _item.Media.StopLive;
            List<Thread> threads = new List<Thread>();
            for (int i = 0; i < n; i++)
            {
                Thread t = new Thread(Worker);
                t.IsBackground = true;
                t.Name = "wpc-md-" + _item.Id + "-" + i;
                threads.Add(t);
                t.Start();
            }
            if (!_liveEnded) LiveLoop();
            _liveEnded = true;
            lock (_gate) Monitor.PulseAll(_gate);
            foreach (Thread t in threads) t.Join();
        }

        private void Worker()
        {
            try
            {
                while (!Stopping() && !HasFailure)
                {
                    Claim c = TakeClaim();
                    if (c == null)
                    {
                        lock (_gate)
                        {
                            if (_liveEnded && !AnyPendingLocked()) break;
                            Monitor.Wait(_gate, 200);
                        }
                        continue;
                    }
                    _item.ActiveConnections = Interlocked.Increment(ref _busy);
                    try
                    {
                        DlFailure f = Fetch(c);
                        if (f != null && !Stopping()) SetFailure(f);
                    }
                    finally
                    {
                        _item.ActiveConnections = Math.Max(0, Interlocked.Decrement(ref _busy));
                        lock (_gate)
                        {
                            c.Seg.Busy = false;
                            int b;
                            if (_hostBusy.TryGetValue(c.Host, out b))
                            {
                                if (b <= 1) _hostBusy.Remove(c.Host);
                                else _hostBusy[c.Host] = b - 1;
                            }
                            if (c.Direct && !c.Committed) c.Track.Writing = false;
                            Monitor.PulseAll(_gate);
                        }
                        Drain(c.Track);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!Stopping()) { DlLog.Report(ex); SetFailure(DlFailure.Make(DlErrorKind.Network, ex.Message)); }
            }
        }

        private bool AnyPendingLocked()
        {
            foreach (Track rt in _tracks)
                for (int i = rt.Head; i < rt.Segs.Count; i++)
                    if (rt.Segs[i].State == Pending) return true;
            return false;
        }

        private bool AllAppended()
        {
            lock (_gate)
            {
                foreach (Track rt in _tracks)
                    foreach (Seg s in rt.Segs)
                        if (s.State != Appended && s.State != Skipped) return false;
                return true;
            }
        }

        private static string HostOf(string url)
        {
            Uri u;
            return Uri.TryCreate(url ?? "", UriKind.Absolute, out u) ? u.Host.ToLowerInvariant() : "";
        }

        private Claim TakeClaim()
        {
            lock (_gate)
            {
                int nt = _tracks.Count;
                int perHost = Math.Max(1, _settings.MaxPerServer);
                for (int k = 0; k < nt; k++)
                {
                    Track rt = _tracks[(_rr + k) % nt];
                    for (int i = rt.Head; i < rt.Segs.Count; i++)
                    {
                        Seg s = rt.Segs[i];
                        if (s.State != Pending || s.Busy) continue;
                        if (s.Src == null) { s.State = Skipped; continue; }
                        bool direct = i == rt.Head && !rt.Writing && !rt.Draining;
                        if (!direct && rt.Buffered >= BufferCap) break;
                        string host = HostOf(s.Src.Url);
                        int busy;
                        _hostBusy.TryGetValue(host, out busy);
                        if (busy >= perHost) break;
                        _hostBusy[host] = busy + 1;
                        s.Busy = true;
                        if (direct) rt.Writing = true;
                        _rr = (_rr + k + 1) % nt;
                        Claim c = new Claim();
                        c.Track = rt;
                        c.Seg = s;
                        c.Direct = direct;
                        c.Host = host;
                        return c;
                    }
                }
                return null;
            }
        }

        private DlFailure Fetch(Claim c)
        {
            int quick = 0;
            while (true)
            {
                if (Stopping() || HasFailure) return null;
                MdSegment src;
                lock (_gate) src = c.Seg.Src;
                int gen = Thread.VolatileRead(ref _generation);
                long bytes;
                DlFailure f = FetchOnce(c, src, out bytes);
                if (f == null)
                {
                    Commit(c, bytes);
                    return null;
                }
                if (Stopping()) return null;
                if (f.Kind == DlErrorKind.LinkExpired)
                {
                    if (c.Track.Live && (f.Status == 404 || f.Status == 410))
                    {
                        SkipGone(c);
                        return null;
                    }
                    if (_item.Media.Source == MdSource.Ytdlp)
                    {
                        DlFailure rf;
                        if (Reextract(gen, out rf)) continue;
                        return rf;
                    }
                    return f;
                }
                if (f.Kind == DlErrorKind.Network || f.Kind == DlErrorKind.Server)
                {
                    if (++quick > QuickRetries) return f;
                    if (!SleepUnlessStopped(1000 * quick)) return null;
                    continue;
                }
                return f;
            }
        }

        // IV ключа из плейлиста или номер сегмента big-endian (RFC 8216 §5.2).
        internal static byte[] IvFor(MdSegment s)
        {
            byte[] iv = new byte[16];
            if (s.Key != null && s.Key.Iv != null)
            {
                int n = Math.Min(16, s.Key.Iv.Length);
                Array.Copy(s.Key.Iv, s.Key.Iv.Length - n, iv, 16 - n, n);
                return iv;
            }
            long q = s.Sequence;
            for (int i = 15; i >= 8; i--)
            {
                iv[i] = (byte)(q & 0xFF);
                q >>= 8;
            }
            return iv;
        }

        private DlFailure FetchOnce(Claim c, MdSegment src, out long bytes)
        {
            bytes = 0;
            Track rt = c.Track;
            DlFailure f;
            byte[] key = null;
            if (src.Key != null && src.Key.Method == "AES-128")
            {
                key = GetKey(rt, src.Key.Uri, out f);
                if (key == null) return f ?? DlFailure.Make(DlErrorKind.Network, "stopped");
            }
            string path = c.Direct ? DataPath(rt) : BinPath(rt, c.Seg.Seq);
            if (DlFiles.IsReparse(path)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте файла частей ссылка", "a link sits where a part file is"));
            string finalUrl;
            HttpWebResponse resp = _fx.Open(src.Url, src.Offset, src.Length, Headers(rt), out finalUrl, out f);
            if (f != null) return f;
            try
            {
                long skip;
                f = _fx.Accept(resp, src.Offset, true, out skip);
                if (f != null) return f;
                long length = src.Length > 0 ? src.Length : -1;
                if (length < 0 && (int)resp.StatusCode == 200 && resp.ContentLength > 0) length = resp.ContentLength;
                FileStream fs;
                long start;
                try
                {
                    if (c.Direct)
                    {
                        long committed;
                        lock (_gate) committed = rt.Committed;
                        // bufferSize 1: каждый Write сразу в ОС — убитый процесс не теряет прочитанное.
                        fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                        if (fs.Length != committed) fs.SetLength(committed);
                        fs.Seek(committed, SeekOrigin.Begin);
                        start = committed;
                    }
                    else
                    {
                        fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1);
                        start = 0;
                    }
                }
                catch (Exception ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, Tr.S("файл частей не открывается: ", "a part file cannot be opened: ") + ex.Message);
                }
                Aes aes = null;
                ICryptoTransform dec = null;
                CryptoStream cs = null;
                try
                {
                    Stream sink = fs;
                    if (key != null)
                    {
                        aes = Aes.Create();
                        aes.Mode = CipherMode.CBC;
                        aes.Padding = PaddingMode.PKCS7;
                        aes.Key = key;
                        aes.IV = IvFor(src);
                        dec = aes.CreateDecryptor();
                        cs = new CryptoStream(fs, dec, CryptoStreamMode.Write);
                        sink = cs;
                    }
                    using (Stream net = resp.GetResponseStream())
                        f = _fx.Pump(net, skip, length, sink, 0);
                    if (f != null) return f;
                    if (cs != null)
                    {
                        try { cs.FlushFinalBlock(); }
                        catch (CryptographicException)
                        {
                            Note(Tr.S("ключ AES-128 не подходит к сегменту ", "the AES-128 key does not fit segment ") + c.Seg.Seq);
                            return DlFailure.Make(DlErrorKind.Client, Tr.S("сегмент не расшифровывается: ключ AES-128 не подходит", "the segment does not decrypt: the AES-128 key does not fit"));
                        }
                    }
                    fs.Flush(true);
                    bytes = fs.Position - start;
                    return null;
                }
                catch (IOException ex)
                {
                    return DlFailure.Make(DlErrorKind.Disk, (DlHttp.IsDiskFull(ex) ? Tr.S("диск заполнен: ", "the disk is full: ") : Tr.S("ошибка записи: ", "write error: ")) + ex.Message);
                }
                finally
                {
                    if (cs != null) { try { cs.Dispose(); } catch (Exception) { } }
                    try { fs.Dispose(); } catch (Exception) { }
                    if (dec != null) dec.Dispose();
                    if (aes != null) aes.Dispose();
                }
            }
            finally
            {
                _fx.Release(resp);
            }
        }

        private byte[] GetKey(Track rt, string uri, out DlFailure f)
        {
            f = null;
            lock (_keyGate)
            {
                byte[] k;
                if (_keys.TryGetValue(uri, out k)) return k;
            }
            for (int quick = 0; ; quick++)
            {
                string fu, ct;
                byte[] b = _fx.GetBytes(uri, -1, -1, MdLoader.MaxKey, Headers(rt), true, out fu, out ct, out f);
                if (f == null)
                {
                    if (b.Length != 16)
                    {
                        f = DlFailure.Make(DlErrorKind.Client, Tr.S("ключ AES-128 неверной длины: ", "the AES-128 key has a wrong length: ") + b.Length);
                        Note(f.Message);
                        return null;
                    }
                    lock (_keyGate) _keys[uri] = b;
                    return b;
                }
                if (Stopping()) return null;
                if ((f.Kind == DlErrorKind.Network || f.Kind == DlErrorKind.Server) && quick < QuickRetries && SleepUnlessStopped(1000 * (quick + 1))) continue;
                Note(Tr.S("ключ не получен: ", "the key was not received: ") + f.Message);
                return null;
            }
        }

        private void Commit(Claim c, long bytes)
        {
            Track rt = c.Track;
            lock (_gate)
            {
                c.Seg.Bytes = bytes;
                if (c.Direct)
                {
                    rt.Committed += bytes;
                    c.Seg.State = Appended;
                    rt.Seconds += c.Seg.Duration;
                    rt.Writing = false;
                    c.Committed = true;
                    AdvanceHeadLocked(rt);
                }
                else
                {
                    c.Seg.State = InBin;
                    rt.Buffered += bytes;
                }
            }
            Drain(rt);
            UpdateCounters();
            SaveJournal(false);
            Persist(false);
        }

        // Живое окно ушло вперёд, сегмента на сервере больше нет: пропуск, запись продолжается.
        private void SkipGone(Claim c)
        {
            lock (_gate)
            {
                c.Seg.State = Skipped;
                AdvanceHeadLocked(c.Track);
            }
            Note(Tr.S("пропуск 1 сегмента: сервер его уже не отдаёт (№", "gap of 1 segment: the server no longer serves it (#") + c.Seg.Seq + ")");
        }

        private static void AdvanceHeadLocked(Track rt)
        {
            while (rt.Head < rt.Segs.Count && (rt.Segs[rt.Head].State == Appended || rt.Segs[rt.Head].State == Skipped)) rt.Head++;
        }

        // Дописать в файл данных готовые .bin, стоящие в очереди следующими. Один дописывающий на дорожку.
        private void Drain(Track rt)
        {
            lock (_gate)
            {
                if (rt.Writing || rt.Draining) return;
                rt.Draining = true;
            }
            try
            {
                while (true)
                {
                    Seg s;
                    long committed;
                    lock (_gate)
                    {
                        AdvanceHeadLocked(rt);
                        if (rt.Writing || rt.Head >= rt.Segs.Count || rt.Segs[rt.Head].State != InBin || _killed != 0)
                        {
                            rt.Draining = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }
                        s = rt.Segs[rt.Head];
                        committed = rt.Committed;
                    }
                    string bin = BinPath(rt, s.Seq);
                    long appended = AppendFile(bin, DataPath(rt), committed, s.Bytes);
                    lock (_gate)
                    {
                        if (appended < 0)
                        {
                            rt.Buffered -= s.Bytes;
                            s.Bytes = 0;
                            s.State = Pending;
                            rt.Draining = false;
                            Monitor.PulseAll(_gate);
                            return;
                        }
                        rt.Committed = committed + appended;
                        rt.Buffered -= s.Bytes;
                        rt.Seconds += s.Duration;
                        s.State = Appended;
                        AdvanceHeadLocked(rt);
                    }
                    DeleteOwn(bin);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    rt.Draining = false;
                    Monitor.PulseAll(_gate);
                }
                if (!Stopping()) SetFailure(DlFailure.Make(DlErrorKind.Disk, Tr.S("ошибка записи: ", "write error: ") + ex.Message));
            }
        }

        // -1 — .bin пропал или не той длины (качать заново).
        private static long AppendFile(string bin, string data, long committed, long expected)
        {
            if (!File.Exists(bin) || DlFiles.IsReparse(bin) || DlFiles.IsReparse(data)) return -1;
            using (FileStream src = new FileStream(bin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (src.Length != expected) return -1;
                using (FileStream dst = new FileStream(data, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1))
                {
                    if (dst.Length != committed) dst.SetLength(committed);
                    dst.Seek(committed, SeekOrigin.Begin);
                    byte[] buf = new byte[1024 * 1024];
                    int n;
                    while ((n = src.Read(buf, 0, buf.Length)) > 0) dst.Write(buf, 0, n);
                    dst.Flush(true);
                    return src.Length;
                }
            }
        }

        // ---------- ссылки yt-dlp устарели посреди загрузки ----------
        private bool Reextract(int gen, out DlFailure f)
        {
            f = null;
            lock (_extractGate)
            {
                if (Thread.VolatileRead(ref _generation) != gen) return true;
                if (_reextracted)
                {
                    f = DlFailure.Make(DlErrorKind.LinkExpired, Tr.S("ссылка снова устарела — нужна свежая со страницы, скачанное сохранено",
                                                                     "the link expired again — a fresh one from the page is needed, downloaded data is kept"));
                    return false;
                }
                _reextracted = true;
                Note(Tr.S("сервер отказал (ссылка устарела) — повторное извлечение", "the server refused (link expired) — extracting again"));
                DlFailure ef;
                MdManifest m = Extract(true, out ef);
                if (m == null) { f = ef; return false; }
                foreach (Track rt in _tracks)
                {
                    MdTrack nt = FindSame(m, rt.Src);
                    if (nt == null)
                    {
                        f = DlFailure.Make(DlErrorKind.LinkExpired, Tr.S("дорожка «", "track «") + rt.Id + Tr.S("» больше не отдаётся", "» is no longer offered"));
                        return false;
                    }
                    if (!MdLoader.ResolveTrack(_fx, nt, _item.Media.Headers, true, out ef))
                    {
                        f = ef;
                        return false;
                    }
                    Dictionary<long, MdSegment> fresh = new Dictionary<long, MdSegment>();
                    foreach (MdSegment s in nt.Segments) fresh[s.Sequence] = s;
                    lock (_gate)
                    {
                        foreach (Seg s in rt.Segs)
                        {
                            if (s.State == Appended || s.State == Skipped || s.State == InBin) continue;
                            MdSegment ns;
                            if (!fresh.TryGetValue(s.Seq, out ns) || s.Src == null || ns.Offset != s.Src.Offset || ns.Length != s.Src.Length)
                            {
                                f = DlFailure.Make(DlErrorKind.Changed, Tr.S("после обновления ссылок поток другой — нужно скачать заново", "after refreshing the links the stream differs — it has to be downloaded again"));
                                return false;
                            }
                            s.Src = ns;
                        }
                        rt.Src = nt;
                    }
                }
                _manifest = m;
                Interlocked.Increment(ref _generation);
                Note(Tr.S("ссылки обновлены", "links refreshed"));
                return true;
            }
        }

        // ------------------------------------------------------------------ //
        //  Трансляция
        // ------------------------------------------------------------------ //
        private void LiveLoop()
        {
            double target = 0;
            foreach (Track rt in _tracks) if (rt.Live && rt.Src.TargetDuration > target) target = rt.Src.TargetDuration;
            if (target <= 0) target = 5;
            target = Math.Max(1, Math.Min(30, target));
            Stopwatch idle = Stopwatch.StartNew();
            int failures = 0;
            while (!Stopping() && !HasFailure)
            {
                if (_item.Media.StopLive) { Note(Tr.S("запись трансляции остановлена — сборка того, что есть", "live recording stopped — assembling what is there")); break; }
                if (!SleepLive((int)(target * 1000))) break;
                if (_item.Media.StopLive) continue;
                bool anyNew = false, stillLive = false, anyLiveTrack = false;
                foreach (Track rt in _tracks)
                {
                    if (!rt.Live) continue;
                    anyLiveTrack = true;
                    MdTrack fresh;
                    DlFailure f = Reload(rt, out fresh);
                    if (Stopping()) break;
                    if (f != null)
                    {
                        stillLive = true;
                        failures++;
                        if (failures <= 3 || failures % 20 == 0) Note(Tr.S("плейлист трансляции не обновился: ", "the live playlist did not refresh: ") + f.Message);
                        continue;
                    }
                    failures = 0;
                    int added, gap;
                    MergeLive(rt, fresh, out added, out gap);
                    if (gap > 0) Note(Tr.S("пропуск ", "gap of ") + gap + Tr.S(" сегментов: окно трансляции ушло вперёд", " segments: the live window moved on"));
                    if (added > 0) anyNew = true;
                    if (fresh.Live) stillLive = true;
                    else lock (_gate) rt.Live = false;
                }
                if (anyNew)
                {
                    idle.Restart();
                    UpdateCounters();
                    lock (_gate) Monitor.PulseAll(_gate);
                    SaveJournal(false);
                }
                if (!anyLiveTrack || !stillLive) { Note(Tr.S("трансляция закончилась", "the live stream ended")); break; }
                if (idle.Elapsed.TotalSeconds > 3 * target + LiveIdleExtraSeconds) { Note(Tr.S("новых сегментов нет — запись завершена", "no new segments — recording finished")); break; }
            }
        }

        private bool SleepLive(int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (Stopping()) return false;
                if (_item.Media.StopLive) return true;
                Thread.Sleep(50);
            }
            return !Stopping();
        }

        private DlFailure Reload(Track rt, out MdTrack fresh)
        {
            fresh = null;
            DlMedia md = _item.Media;
            DlFailure f;
            if (_manifest != null && _manifest.Source == MdSource.Dash)
            {
                string url = md.ManifestUrl.Length > 0 ? md.ManifestUrl : _item.Url;
                MdManifest m = MdLoader.Load(_fx, url, md.Headers, out f);
                if (f != null) return f;
                fresh = FindSame(m, rt.Src);
                if (fresh == null) return DlFailure.Make(DlErrorKind.Client, Tr.S("дорожка пропала из манифеста", "the track disappeared from the manifest"));
                fresh.Live = m.Live;
                return null;
            }
            string fu, ct;
            byte[] body = _fx.GetBytes(rt.Src.Url, -1, -1, MdLoader.MaxManifest, Headers(rt), false, out fu, out ct, out f);
            if (f != null) return f;
            MdTrack t = new MdTrack();
            t.Id = rt.Id;
            t.Kind = rt.Kind;
            t.Layout = rt.Layout;
            t.Url = rt.Src.Url;
            string err;
            bool refused;
            if (!MdHls.ParseMedia(MdLoader.Decode(body), fu, t, out err, out refused))
                return DlFailure.Make(refused ? DlErrorKind.Policy : DlErrorKind.Client, err);
            fresh = t;
            return null;
        }

        // Новые сегменты окна — по номеру, без повторов; разрыв номеров — пропуск (только у сплошной нумерации).
        private void MergeLive(Track rt, MdTrack fresh, out int added, out int gap)
        {
            added = 0;
            gap = 0;
            bool ytdlp = _item.Media.Source == MdSource.Ytdlp;
            List<MdSegment> list = new List<MdSegment>(fresh.Segments);
            list.Sort(delegate(MdSegment a, MdSegment b) { return a.Sequence.CompareTo(b.Sequence); });
            bool contiguous = true;
            for (int i = 1; i < list.Count; i++) if (list[i].Sequence != list[i - 1].Sequence + 1) contiguous = false;
            lock (_gate)
            {
                long maxSeq = rt.Segs.Count > 0 ? rt.Segs[rt.Segs.Count - 1].Seq : long.MinValue;
                bool first = true;
                foreach (MdSegment s in list)
                {
                    if (rt.BySeq.ContainsKey(s.Sequence)) continue;
                    if (s.Sequence < maxSeq) continue;
                    if (first && contiguous && maxSeq != long.MinValue && s.Sequence > maxSeq + 1)
                        gap = (int)Math.Min(int.MaxValue, s.Sequence - maxSeq - 1);
                    first = false;
                    Seg x = NewSeg(s, ytdlp);
                    rt.Segs.Add(x);
                    rt.BySeq[s.Sequence] = x;
                    maxSeq = s.Sequence;
                    added++;
                }
            }
        }

        // ------------------------------------------------------------------ //
        //  Склейка и завершение
        // ------------------------------------------------------------------ //
        private DlFailure Finish()
        {
            DlMedia md = _item.Media;
            md.Phase = "mux";
            Persist(true);
            Func<MdMuxJob, MdMuxResult> mux = MdHooks.Mux;
            if (mux == null)
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка недоступна в этой сборке — части сохранены", "assembly is unavailable in this build — the parts are kept"));
            MdMuxJob job = new MdMuxJob();
            bool webm = false;
            lock (_gate)
                foreach (Track rt in _tracks)
                {
                    if (rt.Committed <= 0) continue;
                    MdInput input = new MdInput();
                    input.Path = DataPath(rt);
                    input.Kind = rt.Kind;
                    input.Layout = rt.Kind == MdTrackKind.Subtitles && rt.Layout != MdLayout.Ttml ? MdLayout.Vtt : rt.Layout;
                    input.Codec = rt.Src == null ? "" : rt.Src.Codec;
                    input.Language = rt.Src == null ? "" : rt.Src.Language;
                    input.Name = rt.Src == null ? "" : rt.Src.Name;
                    job.Inputs.Add(input);
                    if (rt.Layout == MdLayout.WebM) webm = true;
                }
            if (job.Inputs.Count == 0) return DlFailure.Make(DlErrorKind.Client, Tr.S("нечего собирать: ни одного сегмента", "nothing to assemble: no segments"));
            string outPath = Path.Combine(_partsDir, "out" + ExtFor(md.Output, webm));
            DeleteOwn(outPath);
            DeleteOwn(outPath + ".tmp");
            job.OutPath = outPath;
            job.Output = md.Output;
            job.AllowTruncated = _live;
            job.Cancel = Stopping;
            Note(Tr.S("склейка: дорожек ", "assembly: tracks ") + job.Inputs.Count);
            MdMuxResult result;
            try { result = mux(job); }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                result = new MdMuxResult();
                result.Error = ex.Message;
            }
            if (Stopping()) return null;
            if (result == null || !result.Ok)
            {
                string why = result == null || string.IsNullOrEmpty(result.Error) ? Tr.S("неизвестная ошибка", "unknown error") : result.Error;
                Note(Tr.S("склейка не удалась: ", "assembly failed: ") + why);
                return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка не удалась: ", "assembly failed: ") + why);
            }
            string actual = string.IsNullOrEmpty(result.OutPath) ? outPath : result.OutPath;
            try
            {
                if (!File.Exists(actual) || !DlFiles.IsSameOrUnder(Path.GetFullPath(actual), Path.GetFullPath(_partsDir)))
                    return DlFailure.Make(DlErrorKind.Policy, Tr.S("склейка не оставила файла в папке частей", "the assembly left no file in the parts folder"));
            }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Policy, ex.Message); }
            Note(Tr.S("склейка готова: ", "assembly done: ") + result.Output + (result.DurationMs > 0 ? ", " + (result.DurationMs / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + Tr.S(" с", " s") : ""));

            // Расширение цели — по фактическому контейнеру.
            string ext = Path.GetExtension(actual);
            string stem = Path.GetFileNameWithoutExtension(_item.FileName);
            string wanted = DlFiles.SanitizeName(stem + ext);
            if (!string.Equals(wanted, _item.FileName, StringComparison.OrdinalIgnoreCase))
            {
                string unique = _host.ReserveName(_item, _item.Folder, wanted);
                if (unique == null) return DlFailure.Make(DlErrorKind.Disk, Tr.S("не найдено свободное имя файла", "no free file name found"));
                _item.FileName = unique;
            }
            string target = DlFiles.PathInside(_item.Folder, _item.FileName);
            if (target == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
            string part = target + DlPaths.PartSuffix;
            if (DlFiles.IsReparse(part)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте частичного файла ссылка", "a link sits where the partial file is"));
            try
            {
                if (File.Exists(part)) File.Delete(part);
                File.Move(actual, part);
                _item.Total = new FileInfo(part).Length;
            }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("готовый файл не переносится: ", "the result cannot be moved: ") + ex.Message); }
            md.Phase = "verify";
            Persist(true);
            DlFailure f = DlFinish.Complete(_item, _settings, _env, _host, Stopping);
            if (f != null || _item.CompletedUtc == DateTime.MinValue) return f;

            // Готово: субтитры рядом с целью, свои части — по точным именам.
            string baseName = Path.GetFileNameWithoutExtension(outPath);
            string targetStem = Path.GetFileNameWithoutExtension(_item.FileName);
            foreach (string sf in result.SubtitleFiles)
            {
                try
                {
                    if (!File.Exists(sf) || DlFiles.IsReparse(sf) || !DlFiles.IsSameOrUnder(Path.GetFullPath(sf), Path.GetFullPath(_partsDir))) continue;
                    string fileName = Path.GetFileName(sf);
                    string suffix = fileName.StartsWith(baseName + ".", StringComparison.OrdinalIgnoreCase) ? fileName.Substring(baseName.Length) : "." + fileName;
                    string name = DlFiles.UniqueName(_item.Folder, DlFiles.SanitizeName(targetStem + suffix), null);
                    if (name == null) continue;
                    File.Move(sf, Path.Combine(_item.Folder, name));
                    Note(Tr.S("субтитры: ", "subtitles: ") + name);
                }
                catch (Exception ex) { Note(Tr.S("субтитры не перенесены: ", "subtitles were not moved: ") + ex.Message); }
            }
            CleanupParts(outPath);
            md.Phase = "";
            md.PreviewPath = "";
            Persist(true);
            return null;
        }

        private void CleanupParts(string outPath)
        {
            lock (_journalGate)
            {
                lock (_gate)
                {
                    foreach (Track rt in _tracks)
                    {
                        DeleteOwn(DataPath(rt));
                        foreach (Seg s in rt.Segs) DeleteOwn(BinPath(rt, s.Seq));
                    }
                }
                DeleteOwn(outPath);
                DeleteOwn(outPath + ".tmp");
                string journal = Path.Combine(_partsDir, JournalName);
                DeleteOwn(journal);
                DeleteOwn(journal + ".bak");
                DeleteOwn(journal + ".tmp");
                try
                {
                    if (Directory.Exists(_partsDir) && !DlFiles.IsReparse(_partsDir) && Directory.GetFileSystemEntries(_partsDir).Length == 0)
                        Directory.Delete(_partsDir, false);
                }
                catch (Exception ex) { DlLog.Report(ex); }
                _partsDir = "";
            }
        }

        // ------------------------------------------------------------------ //
        //  Счётчики, журнал, сохранение записи
        // ------------------------------------------------------------------ //
        private void UpdateCounters()
        {
            DlMedia md = _item.Media;
            lock (_gate)
            {
                int total = 0, done = 0;
                double secs = 0;
                for (int t = 0; t < _tracks.Count; t++)
                {
                    Track rt = _tracks[t];
                    foreach (Seg s in rt.Segs)
                    {
                        if (s.State == Skipped) continue;
                        total++;
                        if (s.State == InBin || s.State == Appended)
                        {
                            done++;
                            if (t == 0) secs += s.Duration;
                        }
                    }
                }
                md.SegmentsTotal = _live ? -1 : total;
                md.SegmentsDone = done;
                md.SecondsDone = secs;
            }
        }

        private void Persist(bool force)
        {
            if (_killed != 0 || _host == null) return;
            long now = _clock.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref _persistAt) < 1000) return;
            Interlocked.Exchange(ref _persistAt, now);
            _host.Persist(_item);
        }

        private void SaveJournal(bool force)
        {
            if (_killed != 0) return;
            long now = _clock.ElapsedMilliseconds;
            if (!force && now - Interlocked.Read(ref _journalAt) < 1000) return;
            lock (_journalGate)
            {
                if (_killed != 0 || _partsDir.Length == 0) return;
                Interlocked.Exchange(ref _journalAt, _clock.ElapsedMilliseconds);
                string text;
                lock (_gate) text = Jsn.Write(JournalJson());
                try { DlPaths.WriteAtomic(Path.Combine(_partsDir, JournalName), text); }
                catch (Exception ex) { DlLog.Report(ex); }
            }
        }

        private JVal JournalJson()
        {
            JVal o = JVal.NewObj();
            o.Set("v", DlJson.N(1));
            JVal v = JVal.NewObj();
            v.Set("id", DlJson.S(_variantId));
            v.Set("h", DlJson.N(_variantHeight));
            v.Set("bw", DlJson.N(_variantBandwidth));
            o.Set("variant", v);
            o.Set("live", DlJson.B(_live));
            JVal arr = JVal.NewArr();
            foreach (Track rt in _tracks)
            {
                JVal t = JVal.NewObj();
                t.Set("id", DlJson.S(rt.Id));
                t.Set("file", DlJson.S(rt.File));
                t.Set("kind", DlJson.S(rt.Kind.ToString()));
                t.Set("layout", DlJson.S(rt.Layout.ToString()));
                t.Set("init", DlJson.B(rt.InitDone));
                t.Set("committed", DlJson.N(rt.Committed));
                JVal segs = JVal.NewArr();
                foreach (Seg s in rt.Segs)
                {
                    JVal e = JVal.NewArr();
                    e.V.Add(DlJson.N(s.Seq));
                    e.V.Add(DlJson.S(s.Id));
                    // Сегмент в работе в журнале — ещё не скачан.
                    e.V.Add(DlJson.N(s.State));
                    e.V.Add(DlJson.N(s.State == InBin || s.State == Appended ? s.Bytes : 0));
                    e.V.Add(DlJson.N((long)Math.Round(s.Duration * 1000)));
                    segs.V.Add(e);
                }
                t.Set("segs", segs);
                arr.V.Add(t);
            }
            o.Set("tracks", arr);
            return o;
        }

        private bool SleepUnlessStopped(int ms)
        {
            Stopwatch w = Stopwatch.StartNew();
            while (w.ElapsedMilliseconds < ms)
            {
                if (Stopping()) return false;
                Thread.Sleep(50);
            }
            return !Stopping();
        }
    }
}
