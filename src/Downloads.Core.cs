// Windows Process Cleaner — «Загрузки»: пути, журнал, атомарная запись, настройки, окружение (часы, простой ПК, питание).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Загрузки живут в отдельном фоновом процессе того же exe — ключ --downloads, обычные права, свой мьютекс: закрытое окно
// не останавливает загрузку. Главное окно и мост к браузерам — клиенты по именованному каналу (Downloads.Agent.cs).
// Cookies и прочие секреты из браузера живут только в памяти процесса и на диск не пишутся.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace WindowsProcessCleaner.Downloads
{
    // ------------------------------------------------------------------ //
    //  Пути и атомарная запись
    // ------------------------------------------------------------------ //
    internal static class DlPaths
    {
        // Внутри папки данных приложения: WPC_DATA_DIR уводит туда же и тесты.
        public static string DataDir { get { return Path.Combine(Engine.DefaultDataDir(), "downloads"); } }
        public static string ItemsDir { get { return Path.Combine(DataDir, "items"); } }
        public static string TorrentsDir { get { return Path.Combine(DataDir, "torrents"); } }   // = DlEngine.TorrentsDir у процесса
        public static string SettingsFile { get { return Path.Combine(DataDir, "settings.json"); } }
        public static string LogFile { get { return Path.Combine(DataDir, "engine.log"); } }
        public static string ExecutablePath { get { return Application.ExecutablePath; } }

        public const string PartSuffix = ".wpcpart";

        private static readonly Guid FolderIdDownloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");

        [DllImport("shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

        // Папка «Загрузки» пользователя — та, что указана в её свойствах (её переносят на другой диск), а не %USERPROFILE%\Downloads.
        public static string DefaultFolder
        {
            get
            {
                IntPtr p = IntPtr.Zero;
                try
                {
                    if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
                    {
                        string s = Marshal.PtrToStringUni(p);
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
                catch { }
                finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            }
        }

        // Рядом и на место; прежняя версия остаётся в .bak. Оборванная запись не оставит ни половину файла, ни пустоту:
        // загрузчик читает основной файл, а если он не разбирается — .bak.
        public static void WriteAtomic(string file, string text)
        {
            string dir = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string tmp = file + ".tmp";
            byte[] bytes = new UTF8Encoding(false).GetBytes(text);
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(file)) File.Replace(tmp, file, file + ".bak", true);
            else File.Move(tmp, file);
        }

        // Текст основного файла, а если его нет или он не разбирается — резервной копии. null — нет ни того, ни другого.
        public static JVal ReadJson(string file)
        {
            foreach (string candidate in new[] { file, file + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    JVal v = Jsn.Parse(File.ReadAllText(candidate, Encoding.UTF8));
                    if (v != null && v.Kind == JKind.Obj) return v;
                }
                catch { }
            }
            return null;
        }
    }

    // ------------------------------------------------------------------ //
    //  Журнал движка. Адреса попадают сюда без query-строки: в ней бывают токены и подписи.
    // ------------------------------------------------------------------ //
    internal static class DlLog
    {
        private static readonly object Gate = new object();
        private const long MaxBytes = 1024 * 1024;

        public static void Report(Exception ex)
        {
            if (ex != null) Write(ex.ToString());
        }

        public static void Write(string text)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(DlPaths.DataDir);
                    string file = DlPaths.LogFile;
                    FileInfo fi = new FileInfo(file);
                    if (fi.Exists && fi.Length > MaxBytes)
                    {
                        string old = file + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(file, old);
                    }
                    File.AppendAllText(file,
                        "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "] " + text + "\r\n", Encoding.UTF8);
                }
            }
            catch { }
        }

        // scheme://host[:port]/path, без query и фрагмента.
        public static string Redact(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOfAny(new[] { '?', '#' });
            return q < 0 ? url : url.Substring(0, q) + (url[q] == '?' ? "?…" : "");
        }
    }

    // ------------------------------------------------------------------ //
    //  Чтение и запись полей JSON с проверкой типа
    // ------------------------------------------------------------------ //
    internal static class DlJson
    {
        public static JVal B(bool value)
        {
            JVal j = new JVal();
            j.Kind = JKind.Bool;
            j.B = value;
            return j;
        }

        public static JVal N(long value) { return JVal.NewNum(value.ToString(CultureInfo.InvariantCulture)); }
        public static JVal S(string value) { return JVal.NewStr(value ?? ""); }
        public static JVal D(DateTime utc) { return JVal.NewStr(utc == DateTime.MinValue ? "" : utc.ToString("o", CultureInfo.InvariantCulture)); }

        public static JVal Strings(IEnumerable<string> values)
        {
            JVal arr = JVal.NewArr();
            if (values != null) foreach (string s in values) arr.V.Add(JVal.NewStr(s ?? ""));
            return arr;
        }

        public static bool Bool(JVal root, string name, bool fallback)
        {
            JVal v = root == null ? null : root.Get(name);
            return v != null && v.Kind == JKind.Bool ? v.B : fallback;
        }

        public static string Str(JVal root, string name, string fallback)
        {
            JVal v = root == null ? null : root.Get(name);
            return v != null && v.Kind == JKind.Str ? v.Raw : fallback;
        }

        public static int Int(JVal root, string name, int fallback)
        {
            JVal v = root == null ? null : root.Get(name);
            int n;
            return v != null && v.Kind == JKind.Num && int.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        public static long Long(JVal root, string name, long fallback)
        {
            JVal v = root == null ? null : root.Get(name);
            long n;
            return v != null && v.Kind == JKind.Num && long.TryParse(v.Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : fallback;
        }

        public static DateTime Date(JVal root, string name)
        {
            string s = Str(root, name, "");
            DateTime d;
            if (s.Length > 0 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out d)) return d.ToUniversalTime();
            return DateTime.MinValue;
        }

        public static List<string> StrList(JVal root, string name)
        {
            List<string> list = new List<string>();
            JVal v = root == null ? null : root.Get(name);
            if (v != null && v.Kind == JKind.Arr)
                foreach (JVal e in v.V) if (e.Kind == JKind.Str && !string.IsNullOrEmpty(e.Raw)) list.Add(e.Raw);
            return list;
        }

        public static T EnumOr<T>(JVal root, string name, T fallback) where T : struct
        {
            JVal v = root == null ? null : root.Get(name);
            T parsed;
            if (v != null && v.Kind == JKind.Str && Enum.TryParse(v.Raw, true, out parsed) && Enum.IsDefined(typeof(T), parsed)) return parsed;
            return fallback;
        }

        public static int Clamp(int v, int min, int max) { return v < min ? min : v > max ? max : v; }
    }

    // ------------------------------------------------------------------ //
    //  Настройки движка
    // ------------------------------------------------------------------ //
    internal enum DlSpeedMode { Normal, Quiet, Unlimited }

    // Правило папки: «сайт → папка» (Pattern = хост или *.домен) или «тип → папка» (Pattern = расширения через запятую).
    internal sealed class DlFolderRule
    {
        public string Kind = "ext";      // ext | site
        public string Pattern = "";
        public string Folder = "";
    }

    internal sealed class DlSettings
    {
        public const string DefaultUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) WindowsProcessCleaner/1.0";

        public int Segments = 4;                 // потоков на файл
        public int MaxPerServer = 8;             // соединений на один сервер
        public int MaxActive = 3;                // одновременных загрузок
        public int MaxPerHost = 2;               // одновременных загрузок с одного сайта
        public int SmallFileMB = 5;              // меньше — вне очереди (но с учётом лимита сайта); 0 — выключено
        public DlSpeedMode Mode = DlSpeedMode.Normal;
        public int LimitKBps;                    // общий лимит в обычном режиме; 0 — без ограничения
        public int QuietKBps = 256;              // «тихий» режим
        public bool NightEnabled;                // ночной лимит скорости
        public int NightFrom = 60;               // минуты от полуночи
        public int NightTo = 7 * 60;
        public int NightKBps;
        public bool ScheduleEnabled;             // качать только в окне
        public int ScheduleDays = 127;           // бит 0 — понедельник … бит 6 — воскресенье
        public int ScheduleFrom;                 // from == to — весь день
        public int ScheduleTo;
        public int MaxRetries = 8;
        public int RetryMaxSeconds = 300;
        public bool OnlyWhenIdle;                // вся очередь ждёт простоя ПК
        public int IdleMinutes = 5;
        public int IdleCpuPercent = 30;
        public bool IdleNoFullscreen = true;
        public bool PauseOnMetered = true;
        public bool PauseOnBattery = true;
        public bool PreventSleep = true;
        public bool MarkOfTheWeb = true;
        public bool DefenderScan;
        public bool ResumeAtLogon = true;
        public bool NotifyComplete = true;       // уведомление «готово» от фонового процесса
        public bool NotifyErrors = true;         // уведомление об ошибке и истёкшей ссылке
        public string Folder = "";               // пусто — «Загрузки» пользователя
        public string UserAgent = "";            // пусто — DefaultUserAgent
        public readonly List<DlFolderRule> Rules = new List<DlFolderRule>();
        // Спрашивать папку у человека, когда загрузка приходит из браузера без указанной папки. Запомненные пути —
        // общий список мест, из которого выбирают в один щелчок; к сайту и типу файла он не привязан (это делают Rules).
        public bool AskFolder = true;
        public readonly List<string> RecentFolders = new List<string>();
        public const int MaxRecentFolders = 12;
        // Мост к браузерам (Downloads.Bridge.cs): порога размера нет — маленький .torrent тоже перехватывается.
        public bool BrowserIntegration = true;   // ключи NativeMessagingHosts записаны; выкл. — ключи удалены
        public bool BrowserIntercept = true;     // расширение отдаёт загрузки программе
        public bool BrowserIncognito;            // перехватывать и в режиме инкогнито
        public readonly List<string> BrowserSkipHosts = new List<string>();   // хост или *.домен
        public readonly List<string> BrowserSkipExt = new List<string>();     // без точки, строчными
        // Торренты (Torrent.*.cs). Входящие закрыты, пока пользователь сам не нажал «Разрешить входящие»: только тогда есть
        // правило брандмауэра, TCP-слушатель и проброс порта на роутере.
        public int BtPort;                       // 0 — выбрать свободный при первом запуске и запомнить
        public bool BtInbound;
        public bool BtPortMapping = true;        // UPnP / NAT-PMP, пока входящие разрешены
        public BtEncryption BtEncryption = BtEncryption.Prefer;
        public bool BtDht = true;
        public bool BtPex = true;
        public bool BtLsd = true;
        public int BtMaxActive = 3;              // одновременно качающихся торрентов; раздачи не считаются
        public int BtUpKBps;                     // общий лимит отдачи; 0 — без ограничения
        public int BtMaxConnections = 200;
        public int BtMaxPerTorrent = 50;
        public int BtUploadSlots = 4;
        public bool BtSeed = true;               // раздавать после загрузки
        public int BtRatioPercent;               // остановить раздачу при рейтинге (150 = 1,5); 0 — без предела
        public int BtSeedMinutes;                // остановить раздачу через столько минут; 0 — без предела
        public bool BtRecycleTorrentFile;        // .torrent из браузера или папки наблюдения — в Корзину после добавления
        public string BtWatchFolder = "";        // пусто — не следить
        public bool BtUpdateCheck = true;        // искать новые версии раздач на трекере (rutracker) раз в несколько часов
        // Видео (Media.*.cs, Downloads.Hls/Dash/Media/Ytdlp.cs). Инструменты (yt-dlp, Deno) ставит только кнопка в настройках.
        public int MdMaxHeight;                  // предпочитаемая высота: 0 — лучшее, иначе 2160/1440/1080/720/480/360
        public MdOutput MdOutput = MdOutput.Auto;// контейнер по умолчанию; Auto — по дорожкам
        public bool MdToolsUpdate = true;        // держать yt-dlp свежим (проверка не чаще раза в сутки)
        public bool MdSubtitles = true;          // скачивать субтитры отдельными файлами рядом с видео

        public string EffectiveFolder { get { return string.IsNullOrEmpty(Folder) ? DlPaths.DefaultFolder : Folder; } }
        public string EffectiveUserAgent { get { return string.IsNullOrEmpty(UserAgent) ? DefaultUserAgent : UserAgent; } }

        // Общий лимит в байтах в секунду на этот момент; 0 — без ограничения.
        public long EffectiveLimitBytes(DateTime local)
        {
            if (Mode == DlSpeedMode.Unlimited) return 0;
            if (Mode == DlSpeedMode.Quiet) return (long)QuietKBps * 1024;
            if (NightEnabled && InWindow(MinuteOfDay(local), NightFrom, NightTo)) return (long)NightKBps * 1024;
            return (long)LimitKBps * 1024;
        }

        public bool ScheduleAllows(DateTime local)
        {
            if (!ScheduleEnabled) return true;
            int day = ((int)local.DayOfWeek + 6) % 7;   // понедельник = 0
            if ((ScheduleDays & (1 << day)) == 0) return false;
            return InWindow(MinuteOfDay(local), ScheduleFrom, ScheduleTo);
        }

        public static int MinuteOfDay(DateTime t) { return t.Hour * 60 + t.Minute; }

        // [from, to) с переходом через полночь; from == to — весь день.
        public static bool InWindow(int minute, int from, int to)
        {
            if (from == to) return true;
            if (from < to) return minute >= from && minute < to;
            return minute >= from || minute < to;
        }

        public static DlSettings Load() { return Load(DlPaths.SettingsFile); }

        public static DlSettings Load(string file)
        {
            DlSettings s = FromJson(DlPaths.ReadJson(file));
            return s ?? new DlSettings();
        }

        public bool Save() { return Save(DlPaths.SettingsFile); }

        public bool Save(string file)
        {
            try { DlPaths.WriteAtomic(file, Jsn.Write(ToJson())); return true; }
            catch (Exception ex) { DlLog.Report(ex); return false; }
        }

        public DlSettings Clone() { return FromJson(ToJson()) ?? new DlSettings(); }

        internal static DlSettings FromJson(JVal root)
        {
            if (root == null || root.Kind != JKind.Obj) return null;
            DlSettings s = new DlSettings();
            s.Segments = DlJson.Clamp(DlJson.Int(root, "Segments", s.Segments), 1, 16);
            s.MaxPerServer = DlJson.Clamp(DlJson.Int(root, "MaxPerServer", s.MaxPerServer), 1, 16);
            s.MaxActive = DlJson.Clamp(DlJson.Int(root, "MaxActive", s.MaxActive), 1, 20);
            s.MaxPerHost = DlJson.Clamp(DlJson.Int(root, "MaxPerHost", s.MaxPerHost), 1, 10);
            s.SmallFileMB = DlJson.Clamp(DlJson.Int(root, "SmallFileMB", s.SmallFileMB), 0, 1024);
            s.Mode = DlJson.EnumOr(root, "Mode", s.Mode);
            s.LimitKBps = DlJson.Clamp(DlJson.Int(root, "LimitKBps", s.LimitKBps), 0, 10 * 1024 * 1024);
            s.QuietKBps = DlJson.Clamp(DlJson.Int(root, "QuietKBps", s.QuietKBps), 1, 10 * 1024 * 1024);
            s.NightEnabled = DlJson.Bool(root, "NightEnabled", s.NightEnabled);
            s.NightFrom = DlJson.Clamp(DlJson.Int(root, "NightFrom", s.NightFrom), 0, 1439);
            s.NightTo = DlJson.Clamp(DlJson.Int(root, "NightTo", s.NightTo), 0, 1439);
            s.NightKBps = DlJson.Clamp(DlJson.Int(root, "NightKBps", s.NightKBps), 0, 10 * 1024 * 1024);
            s.ScheduleEnabled = DlJson.Bool(root, "ScheduleEnabled", s.ScheduleEnabled);
            s.ScheduleDays = DlJson.Clamp(DlJson.Int(root, "ScheduleDays", s.ScheduleDays), 0, 127);
            s.ScheduleFrom = DlJson.Clamp(DlJson.Int(root, "ScheduleFrom", s.ScheduleFrom), 0, 1439);
            s.ScheduleTo = DlJson.Clamp(DlJson.Int(root, "ScheduleTo", s.ScheduleTo), 0, 1439);
            s.MaxRetries = DlJson.Clamp(DlJson.Int(root, "MaxRetries", s.MaxRetries), 0, 100);
            s.RetryMaxSeconds = DlJson.Clamp(DlJson.Int(root, "RetryMaxSeconds", s.RetryMaxSeconds), 1, 3600);
            s.OnlyWhenIdle = DlJson.Bool(root, "OnlyWhenIdle", s.OnlyWhenIdle);
            s.IdleMinutes = DlJson.Clamp(DlJson.Int(root, "IdleMinutes", s.IdleMinutes), 1, 240);
            s.IdleCpuPercent = DlJson.Clamp(DlJson.Int(root, "IdleCpuPercent", s.IdleCpuPercent), 1, 100);
            s.IdleNoFullscreen = DlJson.Bool(root, "IdleNoFullscreen", s.IdleNoFullscreen);
            s.PauseOnMetered = DlJson.Bool(root, "PauseOnMetered", s.PauseOnMetered);
            s.PauseOnBattery = DlJson.Bool(root, "PauseOnBattery", s.PauseOnBattery);
            s.PreventSleep = DlJson.Bool(root, "PreventSleep", s.PreventSleep);
            s.MarkOfTheWeb = DlJson.Bool(root, "MarkOfTheWeb", s.MarkOfTheWeb);
            s.DefenderScan = DlJson.Bool(root, "DefenderScan", s.DefenderScan);
            s.ResumeAtLogon = DlJson.Bool(root, "ResumeAtLogon", s.ResumeAtLogon);
            s.NotifyComplete = DlJson.Bool(root, "NotifyComplete", s.NotifyComplete);
            s.NotifyErrors = DlJson.Bool(root, "NotifyErrors", s.NotifyErrors);
            s.Folder = DlJson.Str(root, "Folder", s.Folder);
            s.UserAgent = DlJson.Str(root, "UserAgent", s.UserAgent);
            JVal rules = root.Get("Rules");
            if (rules != null && rules.Kind == JKind.Arr)
                foreach (JVal r in rules.V)
                {
                    if (r.Kind != JKind.Obj) continue;
                    DlFolderRule rule = new DlFolderRule();
                    rule.Kind = DlJson.Str(r, "Kind", "ext") == "site" ? "site" : "ext";
                    rule.Pattern = DlJson.Str(r, "Pattern", "").Trim();
                    rule.Folder = DlJson.Str(r, "Folder", "").Trim();
                    if (rule.Pattern.Length > 0 && rule.Folder.Length > 0) s.Rules.Add(rule);
                }
            s.AskFolder = DlJson.Bool(root, "AskFolder", s.AskFolder);
            foreach (string f in DlJson.StrList(root, "RecentFolders"))
            {
                string folder = (f ?? "").Trim();
                if (folder.Length > 0 && !s.RecentFolders.Contains(folder) && s.RecentFolders.Count < MaxRecentFolders) s.RecentFolders.Add(folder);
            }
            s.BrowserIntegration = DlJson.Bool(root, "BrowserIntegration", s.BrowserIntegration);
            s.BrowserIntercept = DlJson.Bool(root, "BrowserIntercept", s.BrowserIntercept);
            s.BrowserIncognito = DlJson.Bool(root, "BrowserIncognito", s.BrowserIncognito);
            foreach (string h in DlJson.StrList(root, "BrowserSkipHosts"))
            {
                string host = h.Trim().ToLowerInvariant();
                if (host.Length > 0 && !s.BrowserSkipHosts.Contains(host)) s.BrowserSkipHosts.Add(host);
            }
            foreach (string e in DlJson.StrList(root, "BrowserSkipExt"))
            {
                string ext = e.Trim().TrimStart('.').ToLowerInvariant();
                if (ext.Length > 0 && !s.BrowserSkipExt.Contains(ext)) s.BrowserSkipExt.Add(ext);
            }
            int port = DlJson.Int(root, "BtPort", 0);
            s.BtPort = port >= 1024 && port <= 65535 ? port : 0;
            s.BtInbound = DlJson.Bool(root, "BtInbound", s.BtInbound);
            s.BtPortMapping = DlJson.Bool(root, "BtPortMapping", s.BtPortMapping);
            s.BtEncryption = DlJson.EnumOr(root, "BtEncryption", s.BtEncryption);
            s.BtDht = DlJson.Bool(root, "BtDht", s.BtDht);
            s.BtPex = DlJson.Bool(root, "BtPex", s.BtPex);
            s.BtLsd = DlJson.Bool(root, "BtLsd", s.BtLsd);
            s.BtMaxActive = DlJson.Clamp(DlJson.Int(root, "BtMaxActive", s.BtMaxActive), 1, 20);
            s.BtUpKBps = DlJson.Clamp(DlJson.Int(root, "BtUpKBps", s.BtUpKBps), 0, 10 * 1024 * 1024);
            s.BtMaxConnections = DlJson.Clamp(DlJson.Int(root, "BtMaxConnections", s.BtMaxConnections), 10, 2000);
            s.BtMaxPerTorrent = DlJson.Clamp(DlJson.Int(root, "BtMaxPerTorrent", s.BtMaxPerTorrent), 2, 500);
            s.BtUploadSlots = DlJson.Clamp(DlJson.Int(root, "BtUploadSlots", s.BtUploadSlots), 1, 50);
            s.BtSeed = DlJson.Bool(root, "BtSeed", s.BtSeed);
            s.BtRatioPercent = DlJson.Clamp(DlJson.Int(root, "BtRatioPercent", s.BtRatioPercent), 0, 100000);
            s.BtSeedMinutes = DlJson.Clamp(DlJson.Int(root, "BtSeedMinutes", s.BtSeedMinutes), 0, 1000000);
            s.BtRecycleTorrentFile = DlJson.Bool(root, "BtRecycleTorrentFile", s.BtRecycleTorrentFile);
            s.BtWatchFolder = DlJson.Str(root, "BtWatchFolder", s.BtWatchFolder).Trim();
            s.BtUpdateCheck = DlJson.Bool(root, "BtUpdateCheck", s.BtUpdateCheck);
            s.MdMaxHeight = DlJson.Clamp(DlJson.Int(root, "MdMaxHeight", s.MdMaxHeight), 0, 4320);
            s.MdOutput = DlJson.EnumOr(root, "MdOutput", s.MdOutput);
            s.MdToolsUpdate = DlJson.Bool(root, "MdToolsUpdate", s.MdToolsUpdate);
            s.MdSubtitles = DlJson.Bool(root, "MdSubtitles", s.MdSubtitles);
            return s;
        }

        internal JVal ToJson()
        {
            JVal o = JVal.NewObj();
            o.Set("Segments", DlJson.N(Segments));
            o.Set("MaxPerServer", DlJson.N(MaxPerServer));
            o.Set("MaxActive", DlJson.N(MaxActive));
            o.Set("MaxPerHost", DlJson.N(MaxPerHost));
            o.Set("SmallFileMB", DlJson.N(SmallFileMB));
            o.Set("Mode", DlJson.S(Mode.ToString()));
            o.Set("LimitKBps", DlJson.N(LimitKBps));
            o.Set("QuietKBps", DlJson.N(QuietKBps));
            o.Set("NightEnabled", DlJson.B(NightEnabled));
            o.Set("NightFrom", DlJson.N(NightFrom));
            o.Set("NightTo", DlJson.N(NightTo));
            o.Set("NightKBps", DlJson.N(NightKBps));
            o.Set("ScheduleEnabled", DlJson.B(ScheduleEnabled));
            o.Set("ScheduleDays", DlJson.N(ScheduleDays));
            o.Set("ScheduleFrom", DlJson.N(ScheduleFrom));
            o.Set("ScheduleTo", DlJson.N(ScheduleTo));
            o.Set("MaxRetries", DlJson.N(MaxRetries));
            o.Set("RetryMaxSeconds", DlJson.N(RetryMaxSeconds));
            o.Set("OnlyWhenIdle", DlJson.B(OnlyWhenIdle));
            o.Set("IdleMinutes", DlJson.N(IdleMinutes));
            o.Set("IdleCpuPercent", DlJson.N(IdleCpuPercent));
            o.Set("IdleNoFullscreen", DlJson.B(IdleNoFullscreen));
            o.Set("PauseOnMetered", DlJson.B(PauseOnMetered));
            o.Set("PauseOnBattery", DlJson.B(PauseOnBattery));
            o.Set("PreventSleep", DlJson.B(PreventSleep));
            o.Set("MarkOfTheWeb", DlJson.B(MarkOfTheWeb));
            o.Set("DefenderScan", DlJson.B(DefenderScan));
            o.Set("ResumeAtLogon", DlJson.B(ResumeAtLogon));
            o.Set("NotifyComplete", DlJson.B(NotifyComplete));
            o.Set("NotifyErrors", DlJson.B(NotifyErrors));
            o.Set("Folder", DlJson.S(Folder));
            o.Set("UserAgent", DlJson.S(UserAgent));
            JVal rules = JVal.NewArr();
            foreach (DlFolderRule r in Rules)
            {
                JVal j = JVal.NewObj();
                j.Set("Kind", DlJson.S(r.Kind));
                j.Set("Pattern", DlJson.S(r.Pattern));
                j.Set("Folder", DlJson.S(r.Folder));
                rules.V.Add(j);
            }
            o.Set("Rules", rules);
            o.Set("AskFolder", DlJson.B(AskFolder));
            o.Set("RecentFolders", DlJson.Strings(RecentFolders));
            o.Set("BrowserIntegration", DlJson.B(BrowserIntegration));
            o.Set("BrowserIntercept", DlJson.B(BrowserIntercept));
            o.Set("BrowserIncognito", DlJson.B(BrowserIncognito));
            o.Set("BrowserSkipHosts", DlJson.Strings(BrowserSkipHosts));
            o.Set("BrowserSkipExt", DlJson.Strings(BrowserSkipExt));
            o.Set("BtPort", DlJson.N(BtPort));
            o.Set("BtInbound", DlJson.B(BtInbound));
            o.Set("BtPortMapping", DlJson.B(BtPortMapping));
            o.Set("BtEncryption", DlJson.S(BtEncryption.ToString()));
            o.Set("BtDht", DlJson.B(BtDht));
            o.Set("BtPex", DlJson.B(BtPex));
            o.Set("BtLsd", DlJson.B(BtLsd));
            o.Set("BtMaxActive", DlJson.N(BtMaxActive));
            o.Set("BtUpKBps", DlJson.N(BtUpKBps));
            o.Set("BtMaxConnections", DlJson.N(BtMaxConnections));
            o.Set("BtMaxPerTorrent", DlJson.N(BtMaxPerTorrent));
            o.Set("BtUploadSlots", DlJson.N(BtUploadSlots));
            o.Set("BtSeed", DlJson.B(BtSeed));
            o.Set("BtRatioPercent", DlJson.N(BtRatioPercent));
            o.Set("BtSeedMinutes", DlJson.N(BtSeedMinutes));
            o.Set("BtRecycleTorrentFile", DlJson.B(BtRecycleTorrentFile));
            o.Set("BtWatchFolder", DlJson.S(BtWatchFolder));
            o.Set("BtUpdateCheck", DlJson.B(BtUpdateCheck));
            o.Set("MdMaxHeight", DlJson.N(MdMaxHeight));
            o.Set("MdOutput", DlJson.S(MdOutput.ToString()));
            o.Set("MdToolsUpdate", DlJson.B(MdToolsUpdate));
            o.Set("MdSubtitles", DlJson.B(MdSubtitles));
            return o;
        }
    }

    // ------------------------------------------------------------------ //
    //  Окружение: часы и сигналы ОС. Единственное, что тесты подменяют, — это граница с системой.
    // ------------------------------------------------------------------ //
    internal interface IDlEnvironment
    {
        DateTime UtcNow { get; }
        DateTime LocalNow { get; }
        bool IsMetered();
        bool OnBattery();
        int InputIdleSeconds();
        int CpuPercent();
        bool FullscreenBusy();
    }

    internal sealed class DlSystemEnvironment : IDlEnvironment
    {
        private readonly object _gate = new object();
        private long _idlePrev, _kernelPrev, _userPrev;
        private int _cpu;
        private DateTime _cpuAt = DateTime.MinValue;
        private bool _metered;
        private DateTime _meteredAt = DateTime.MinValue;

        public DateTime UtcNow { get { return DateTime.UtcNow; } }
        public DateTime LocalNow { get { return DateTime.Now; } }

        [StructLayout(LayoutKind.Sequential)]
        private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
            public int BatteryLifeTime, BatteryFullLifeTime;
        }

        [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
        [DllImport("kernel32.dll")] private static extern uint GetTickCount();
        [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
        [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        [ComImport, Guid("DCB00008-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface INetworkCostManager
        {
            [PreserveSig] int GetCost(out uint cost, IntPtr destination);
        }

        private const uint CostFixed = 0x2, CostVariable = 0x4, CostOverLimit = 0x10000, CostRoaming = 0x40000;

        // Лимитное подключение по мнению Windows (Параметры → Сеть → «Лимитное подключение»). Спрашиваем не чаще раза в 5 с.
        public bool IsMetered()
        {
            lock (_gate)
            {
                if ((DateTime.UtcNow - _meteredAt).TotalSeconds < 5) return _metered;
                _meteredAt = DateTime.UtcNow;
                object manager = null;
                try
                {
                    manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")));
                    uint cost;
                    INetworkCostManager m = (INetworkCostManager)manager;
                    _metered = m.GetCost(out cost, IntPtr.Zero) == 0 && (cost & (CostFixed | CostVariable | CostOverLimit | CostRoaming)) != 0;
                }
                catch { _metered = false; }
                finally { if (manager != null) Marshal.ReleaseComObject(manager); }
                return _metered;
            }
        }

        // ACLineStatus: 0 — от батареи, 1 — от сети, 255 — неизвестно (у настольного ПК без батареи бывает и так).
        public bool OnBattery()
        {
            SYSTEM_POWER_STATUS s;
            return GetSystemPowerStatus(out s) && s.ACLineStatus == 0;
        }

        public int InputIdleSeconds()
        {
            LASTINPUTINFO info = new LASTINPUTINFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref info)) return 0;
            return (int)((GetTickCount() - info.dwTime) / 1000);
        }

        // Загрузка процессора всей системы между двумя вызовами (не чаще раза в 2 с, иначе замер шумит).
        public int CpuPercent()
        {
            lock (_gate)
            {
                if ((DateTime.UtcNow - _cpuAt).TotalSeconds < 2) return _cpu;
                long idle, kernel, user;
                if (!GetSystemTimes(out idle, out kernel, out user)) return _cpu;
                if (_cpuAt != DateTime.MinValue)
                {
                    long total = (kernel - _kernelPrev) + (user - _userPrev);   // kernel включает idle
                    long busy = total - (idle - _idlePrev);
                    _cpu = total <= 0 ? 0 : (int)Math.Max(0, Math.Min(100, busy * 100 / total));
                }
                _idlePrev = idle; _kernelPrev = kernel; _userPrev = user;
                _cpuAt = DateTime.UtcNow;
                return _cpu;
            }
        }

        // Полноэкранная игра или презентация: то же состояние оболочки, что откладывает уведомления «Захвата».
        public bool FullscreenBusy()
        {
            try
            {
                int state;
                if (Capture.CapNative.SHQueryUserNotificationState(out state) != 0) return false;
                return state == Capture.CapNative.QUNS_BUSY || state == Capture.CapNative.QUNS_RUNNING_D3D_FULL_SCREEN
                       || state == Capture.CapNative.QUNS_PRESENTATION_MODE;
            }
            catch { return false; }
        }
    }
}
