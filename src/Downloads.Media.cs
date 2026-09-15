// SysDeck — «Загрузки», видео: запуск видео-загрузки — сегменты, журнал частей, трансляция, склейка.
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

namespace SysDeck.Downloads
{
    internal sealed partial class DlMediaRun : IDlRun
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
    }
}
