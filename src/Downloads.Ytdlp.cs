// SysDeck — «Загрузки», видео: извлечение форматов со страниц сайтов через yt-dlp.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// yt-dlp только описывает страницу (-J): форматы, прямые ссылки, заголовки, субтитры. Качает наш движок — с паузой,
// продолжением и ограничением скорости. Запуск — ProcessStartInfo без оболочки, аргументы экранируются по правилам
// MSVCRT, процесс и всё, что он породил (yt-dlp.exe — распаковщик PyInstaller со вторым процессом, Deno), живут в
// объекте задания Windows: отмена или таймаут закрывают всё дерево. Cookies браузера в yt-dlp не передаются (им
// пришлось бы лечь файлом на диск) — страницы со входом объясняются понятной ошибкой.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SysDeck.Downloads
{
    // ------------------------------------------------------------------ //
    //  Запуск внешнего exe: задание Windows, таймаут, предел вывода
    // ------------------------------------------------------------------ //
    internal sealed class MdRunResult
    {
        public int ExitCode = -1;
        public string Stdout = "";
        public string StderrTail = "";               // последние 4 КиБ
        public bool TimedOut, Cancelled, TooLarge;
        public string StartError = "";

        public bool Ok { get { return StartError.Length == 0 && !TimedOut && !Cancelled && !TooLarge && ExitCode == 0; } }

        public string Describe()
        {
            if (StartError.Length > 0) return Tr.S("не запустился: ", "did not start: ") + StartError;
            if (Cancelled) return Tr.S("отменено", "cancelled");
            if (TimedOut) return Tr.S("не ответил вовремя", "did not answer in time");
            if (TooLarge) return Tr.S("слишком большой вывод", "output too large");
            string tail = StderrTail.Trim();
            return Tr.S("код выхода ", "exit code ") + ExitCode + (tail.Length > 0 ? ": " + MdYtdlp.LastLine(tail) : "");
        }
    }

    internal sealed class MdRunException : Exception
    {
        public readonly MdRunResult Result;
        public MdRunException(MdRunResult r) : base(r.Describe()) { Result = r; }
    }

    internal static class MdProc
    {
        private const int StderrKeep = 4096;

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimit
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimit
        {
            public BasicLimit Basic;
            public IoCounters Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint KillOnJobClose = 0x2000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimit info, int length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateJobObject(IntPtr job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        // Одна строка командной строки по правилам MSVCRT/CommandLineToArgvW: пробелы и кавычки — в кавычки, обратные
        // слэши перед кавычкой и в конце удваиваются.
        internal static string Quote(string arg)
        {
            string a = arg ?? "";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return a;
            StringBuilder sb = new StringBuilder(a.Length + 8);
            sb.Append('"');
            for (int i = 0; ; i++)
            {
                int slashes = 0;
                while (i < a.Length && a[i] == '\\') { i++; slashes++; }
                if (i == a.Length) { sb.Append('\\', slashes * 2); break; }
                if (a[i] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', slashes); sb.Append(a[i]); }
            }
            sb.Append('"');
            return sb.ToString();
        }

        internal static string Join(IList<string> args)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string a in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(Quote(a));
            }
            return sb.ToString();
        }

        // Без оболочки и окна; stdout — не больше maxStdout байт (больше — процесс закрывается), stderr — хвост 4 КиБ.
        internal static MdRunResult Run(string exe, string args, int timeoutMs, Func<bool> cancel, long maxStdout)
        {
            MdRunResult r = new MdRunResult();
            IntPtr job = IntPtr.Zero;
            Process p = null;
            Thread outReader = null, errReader = null;
            MemoryStream stdout = new MemoryStream();
            byte[] errRing = new byte[StderrKeep];
            int errLen = 0;
            long errTotal = 0;
            object errGate = new object();
            bool tooLarge = false;
            try
            {
                job = CreateJobObject(IntPtr.Zero, null);
                if (job != IntPtr.Zero)
                {
                    ExtendedLimit info = new ExtendedLimit();
                    info.Basic.LimitFlags = KillOnJobClose;
                    if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf(typeof(ExtendedLimit))))
                    {
                        CloseHandle(job);
                        job = IntPtr.Zero;
                    }
                }
                ProcessStartInfo psi = new ProcessStartInfo(exe, args ?? "");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.ErrorDialog = false;
                psi.RedirectStandardInput = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(exe));
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                psi.EnvironmentVariables["NO_COLOR"] = "1";
                try { p = Process.Start(psi); }
                catch (Win32Exception ex) { r.StartError = ex.Message; return r; }
                catch (InvalidOperationException ex) { r.StartError = ex.Message; return r; }
                if (p == null) { r.StartError = "no process"; return r; }
                // Сразу после старта: распаковщик PyInstaller порождает второй процесс не раньше, чем распакуется.
                if (job != IntPtr.Zero)
                {
                    try { AssignProcessToJobObject(job, p.Handle); }
                    catch (InvalidOperationException) { }
                }
                try { p.StandardInput.Close(); }
                catch (IOException) { }
                Process proc = p;
                outReader = new Thread(delegate()
                {
                    byte[] buf = new byte[65536];
                    try
                    {
                        Stream s = proc.StandardOutput.BaseStream;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (stdout.Length + n > maxStdout) { tooLarge = true; break; }
                            stdout.Write(buf, 0, n);
                        }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                errReader = new Thread(delegate()
                {
                    byte[] buf = new byte[4096];
                    try
                    {
                        Stream s = proc.StandardError.BaseStream;
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                            lock (errGate)
                            {
                                for (int i = 0; i < n; i++) errRing[(int)((errTotal + i) % StderrKeep)] = buf[i];
                                errTotal += n;
                                errLen = (int)Math.Min(StderrKeep, errTotal);
                            }
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                outReader.IsBackground = true;
                errReader.IsBackground = true;
                outReader.Start();
                errReader.Start();
                Stopwatch w = Stopwatch.StartNew();
                while (!p.WaitForExit(100))
                {
                    if (tooLarge) { r.TooLarge = true; break; }
                    if (cancel != null && cancel()) { r.Cancelled = true; break; }
                    if (w.ElapsedMilliseconds > timeoutMs) { r.TimedOut = true; break; }
                }
                if (tooLarge) r.TooLarge = true;
                if (r.TooLarge || r.Cancelled || r.TimedOut) Kill(job, p);
                else r.ExitCode = p.ExitCode;
                // Всё, что процесс оставил после себя и что держит канал вывода, закрывается вместе с заданием.
                if (job != IntPtr.Zero) { CloseHandle(job); job = IntPtr.Zero; }
                if (!outReader.Join(5000) || !errReader.Join(5000))
                {
                    try { p.StandardOutput.BaseStream.Close(); } catch (IOException) { }
                    try { p.StandardError.BaseStream.Close(); } catch (IOException) { }
                }
                if (tooLarge) r.TooLarge = true;
                if (!r.TooLarge)
                {
                    byte[] bytes = stdout.ToArray();
                    int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
                    r.Stdout = Encoding.UTF8.GetString(bytes, skip, bytes.Length - skip);
                }
                lock (errGate)
                {
                    byte[] tail = new byte[errLen];
                    long start = errTotal - errLen;
                    for (int i = 0; i < errLen; i++) tail[i] = errRing[(int)((start + i) % StderrKeep)];
                    r.StderrTail = Encoding.UTF8.GetString(tail);
                }
                return r;
            }
            finally
            {
                if (p != null)
                {
                    if (job != IntPtr.Zero && !SafeExited(p)) Kill(job, p);
                    p.Dispose();
                }
                if (job != IntPtr.Zero) CloseHandle(job);
            }
        }

        private static bool SafeExited(Process p)
        {
            try { return p.HasExited; }
            catch (InvalidOperationException) { return true; }
            catch (Win32Exception) { return true; }
        }

        private static void Kill(IntPtr job, Process p)
        {
            if (job != IntPtr.Zero) TerminateJobObject(job, 1);
            try { if (!p.HasExited) p.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
            try { p.WaitForExit(5000); }
            catch (InvalidOperationException) { }
            catch (SystemException) { }
        }
    }

    // ------------------------------------------------------------------ //
    //  yt-dlp: описание страницы → MdManifest
    // ------------------------------------------------------------------ //
    internal enum MdYtdlpError { None, Login, Age, Geo, Unavailable, Unsupported, JsRuntime, Network, Timeout, TooLarge, Other }

    internal static class MdYtdlp
    {
        public const int TimeoutMsDefault = 120000;
        private const long MaxStdout = 64L << 20;
        private const int MaxDepth = 256;
        private const int MaxFormats = 2000;
        private const int MaxSubtitles = 400;
        public const string AudioGroupId = "ytdlp-audio";
        public const string SubtitleGroupId = "ytdlp-subs";

        internal static int TimeoutMs = TimeoutMsDefault;

        // Граница процесса: (exe, аргументы, отмена) → stdout. Ненулевой код, таймаут, отмена — исключения
        // (MdRunException, OperationCanceledException). Тесты подменяют записанным ответом.
        internal static Func<string, string, Func<bool>, string> Runner = DefaultRunner;

        public static bool Available { get { return MdTools.YtdlpReady && MdTools.DenoReady; } }

        private static string DefaultRunner(string exe, string args, Func<bool> cancel)
        {
            MdRunResult r = MdProc.Run(exe, args, TimeoutMs, cancel, MaxStdout);
            if (r.Cancelled) throw new OperationCanceledException();
            if (!r.Ok) throw new MdRunException(r);
            return r.Stdout;
        }

        // --ignore-config: чужой конфиг yt-dlp у пользователя (cookies из браузера, свой вывод) не участвует.
        // --flat-playlist: адрес плейлиста не разворачивается целиком — ответ «это плейлист» приходит сразу.
        internal static List<string> BuildArgs(string pageUrl, string denoPath, string extractorArgs)
        {
            List<string> a = new List<string>();
            a.Add("-J");
            a.Add("--no-playlist");
            a.Add("--flat-playlist");
            a.Add("--ignore-config");
            a.Add("--no-warnings");
            a.Add("--no-progress");
            a.Add("--no-cache-dir");
            a.Add("--js-runtimes");
            a.Add("deno:" + denoPath);
            if (!string.IsNullOrEmpty(extractorArgs))
            {
                a.Add("--extractor-args");
                a.Add(extractorArgs);
            }
            a.Add("--");
            a.Add(pageUrl);
            return a;
        }

        // YouTube отвечает «подтвердите, что вы не робот» адресам с плохой репутацией (VPN, провайдеры) на обычные клиенты,
        // а встраиваемый плеер при этом отдаёт форматы. Один повтор с ним — только для YouTube и только на эту ошибку.
        public const string YoutubeFallbackArgs = "youtube:player_client=default,web_embedded";

        internal static bool IsYoutube(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            string h = u.Host.ToLowerInvariant();
            return h == "youtube.com" || h.EndsWith(".youtube.com", StringComparison.Ordinal) || h == "youtu.be"
                   || h == "youtube-nocookie.com" || h.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal);
        }

        private static string RunExtract(string url, Func<bool> cancel)
        {
            try { return Runner(MdTools.YtdlpPath, MdProc.Join(BuildArgs(url, MdTools.DenoPath, null)), cancel); }
            catch (MdRunException ex)
            {
                string tail = ex.Result.StderrTail ?? "";
                if (!IsYoutube(url) || ex.Result.TimedOut || ex.Result.TooLarge || tail.IndexOf("not a bot", StringComparison.OrdinalIgnoreCase) < 0) throw;
                if (cancel != null && cancel()) throw new OperationCanceledException();
                DlLog.Write("yt-dlp: bot check, one retry with the embedded player client");
                return Runner(MdTools.YtdlpPath, MdProc.Join(BuildArgs(url, MdTools.DenoPath, YoutubeFallbackArgs)), cancel);
            }
        }

        // Совпадает с MdHooks.ExtractFn. Ошибка — null и понятный текст; DRM — манифест с Refused.
        public static MdManifest Extract(string pageUrl, Func<bool> cancel, out string error)
        {
            error = "";
            string url = (pageUrl ?? "").Trim();
            try
            {
                if (!DlHttp.IsAllowedScheme(url) || url.Length > 8192 || Regex.IsMatch(url, @"[\x00-\x20\x7F]"))
                {
                    error = Tr.S("yt-dlp принимает только адрес страницы http или https", "yt-dlp accepts only an http or https page address");
                    return null;
                }
                if (!Available)
                {
                    error = Tr.S("yt-dlp и Deno не установлены — установите их в настройках загрузок",
                                 "yt-dlp and Deno are not installed — install them in the download settings");
                    return null;
                }
                string stdout = RunExtract(url, cancel);
                if (cancel != null && cancel()) { error = Tr.S("отменено", "cancelled"); return null; }
                return FromJson(stdout, url, out error);
            }
            catch (OperationCanceledException)
            {
                error = Tr.S("отменено", "cancelled");
                return null;
            }
            catch (MdRunException ex)
            {
                MdYtdlpError kind = ex.Result.TimedOut ? MdYtdlpError.Timeout : ex.Result.TooLarge ? MdYtdlpError.TooLarge
                                  : ex.Result.StartError.Length > 0 ? MdYtdlpError.Other : Classify(ex.Result.StderrTail);
                error = ErrorText(kind, ex.Result.StartError.Length > 0 ? ex.Result.Describe() : ex.Result.StderrTail);
                DlLog.Write("yt-dlp: " + kind + " " + DlLog.Redact(url));
                return null;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception || ex is InvalidOperationException
                      || ex is FormatException || ex is ArgumentException || ex is OutOfMemoryException)) throw;
                DlLog.Report(ex);
                error = Tr.S("yt-dlp: ", "yt-dlp: ") + ex.Message;
                return null;
            }
        }

        // ---------- ошибки ----------
        internal static MdYtdlpError Classify(string stderr)
        {
            string s = (stderr ?? "").ToLowerInvariant().Replace('’', '\'');
            if (s.Trim().Length == 0) return MdYtdlpError.Other;
            if (Has(s, "javascript runtime", "js runtime", "--js-runtimes", "challenge solving failed", "signature solving failed", "n challenge"))
                return MdYtdlpError.JsRuntime;
            if (Has(s, "confirm your age", "age-restricted", "age restricted", "inappropriate for some users", "age verification"))
                return MdYtdlpError.Age;
            // «The uploader has not made this video available in your country» — так это звучит у YouTube.
            if (Has(s, "available in your country", "geo restrict", "geo-restrict", "geoblock", "available from your location", "in your region"))
                return MdYtdlpError.Geo;
            if (Has(s, "sign in", "log in", "login", "cookies", "private video", "members-only", "members only", "premium", "authentication", "account"))
                return MdYtdlpError.Login;
            if (Has(s, "unsupported url"))
                return MdYtdlpError.Unsupported;
            if (Has(s, "video unavailable", "is unavailable", "has been removed", "does not exist", "http error 404", "not found", "no longer available", "was deleted"))
                return MdYtdlpError.Unavailable;
            if (Has(s, "unable to download webpage", "timed out", "getaddrinfo", "connection", "network is unreachable", "ssl", "urlopen error"))
                return MdYtdlpError.Network;
            return MdYtdlpError.Other;
        }

        private static bool Has(string s, params string[] parts)
        {
            foreach (string p in parts) if (s.IndexOf(p, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        internal static string ErrorText(MdYtdlpError kind, string detail)
        {
            switch (kind)
            {
                case MdYtdlpError.Login:
                    return Tr.S("сайт требует входа или проверки «я не робот» — такие видео через yt-dlp пока не скачиваются",
                                "the site requires signing in or a \"not a robot\" check — such videos cannot be downloaded through yt-dlp yet");
                case MdYtdlpError.Age:
                    return Tr.S("видео с ограничением по возрасту: сайт требует входа, это пока не поддерживается",
                                "age-restricted video: the site requires signing in, which is not supported yet");
                case MdYtdlpError.Geo:
                    return Tr.S("видео недоступно в вашей стране", "the video is not available in your country");
                case MdYtdlpError.Unavailable:
                    return Tr.S("видео недоступно: удалено, скрыто или не существует", "the video is unavailable: removed, hidden or does not exist");
                case MdYtdlpError.Unsupported:
                    return Tr.S("yt-dlp не знает этот сайт — попробуйте прямую ссылку на видео", "yt-dlp does not support this site — try a direct link to the video");
                case MdYtdlpError.JsRuntime:
                    return Tr.S("yt-dlp не смог выполнить JavaScript сайта через Deno — обновите инструменты в настройках загрузок",
                                "yt-dlp could not run the site's JavaScript through Deno — update the tools in the download settings");
                case MdYtdlpError.Network:
                    return Tr.S("yt-dlp не смог связаться с сайтом", "yt-dlp could not reach the site");
                case MdYtdlpError.Timeout:
                    return Tr.S("yt-dlp не ответил за отведённое время", "yt-dlp did not answer in time");
                case MdYtdlpError.TooLarge:
                    return Tr.S("ответ yt-dlp слишком большой", "the yt-dlp answer is too large");
                default:
                    string line = LastLine(detail ?? "");
                    return line.Length > 0 ? "yt-dlp: " + line : Tr.S("yt-dlp завершился с ошибкой", "yt-dlp failed");
            }
        }

        // Последняя строка «ERROR: …» (или просто последняя), адреса без query, не длиннее 300 знаков.
        internal static string LastLine(string text)
        {
            string pick = "";
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0) continue;
                if (l.StartsWith("ERROR:", StringComparison.Ordinal) || pick.Length == 0 || !pick.StartsWith("ERROR:", StringComparison.Ordinal)) pick = l;
            }
            pick = Regex.Replace(pick, @"(https?://[^\s?#""']+)[?#][^\s""']*", "$1?…");
            return pick.Length > 300 ? pick.Substring(0, 300) + "…" : pick;
        }

        // ---------- JSON → манифест ----------
        internal static MdManifest FromJson(string json, string pageUrl, out string error)
        {
            error = "";
            if (string.IsNullOrEmpty(json) || json.Length > MaxStdout || !DepthOk(json))
            {
                error = Tr.S("yt-dlp вернул пустой или непригодный ответ", "yt-dlp returned an empty or unusable answer");
                return null;
            }
            JVal root;
            try { root = Jsn.Parse(json); }
            catch (FormatException) { root = null; }
            catch (ArgumentException) { root = null; }
            if (root == null || root.Kind != JKind.Obj)
            {
                error = Tr.S("yt-dlp вернул не JSON", "yt-dlp did not return JSON");
                return null;
            }
            string type = Str(root, "_type");
            if (type == "playlist" || type == "multi_video")
            {
                JVal entries = root.Get("entries");
                long n = entries != null && entries.Kind == JKind.Arr ? entries.V.Count : (long)Clamp(Num(root, "playlist_count", 0), 0, 1e9);
                error = string.Format(Tr.S("это плейлист ({0}) — добавьте видео по одному", "this is a playlist ({0}) — add the videos one by one"), n);
                return null;
            }
            MdManifest m = new MdManifest();
            m.Source = MdSource.Ytdlp;
            m.PageUrl = pageUrl ?? "";
            string web = Str(root, "webpage_url");
            m.Url = DlHttp.IsAllowedScheme(web) ? web : m.PageUrl;
            m.Title = Clip(Str(root, "title"), 400);
            double dur = Num(root, "duration", 0);
            m.DurationSec = dur > 0 && dur < 1e8 ? dur : 0;
            m.Live = Bool(root, "is_live") || Str(root, "live_status") == "is_live";

            List<JVal> formats = new List<JVal>();
            JVal fa = root.Get("formats");
            if (fa != null && fa.Kind == JKind.Arr) { foreach (JVal f in fa.V) if (f.Kind == JKind.Obj && formats.Count < MaxFormats) formats.Add(f); }
            else if (Str(root, "url").Length > 0) formats.Add(root);

            int drm = 0;
            long expire = long.MaxValue;
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            List<MdTrack> tracks = new List<MdTrack>();
            foreach (JVal f in formats)
            {
                if (Bool(f, "has_drm")) { drm++; continue; }
                MdTrack t = MapFormat(f, m.Live);
                if (t == null) continue;
                string id = t.FormatId.Length > 0 ? t.FormatId : "f" + tracks.Count;
                string unique = id;
                for (int k = 2; ids.Contains(unique); k++) unique = id + "-" + k;
                ids.Add(unique);
                t.Id = unique;
                tracks.Add(t);
                expire = Math.Min(expire, ExpireOf(t.Url));
                expire = Math.Min(expire, ExpireOf(Str(f, "manifest_url")));
            }
            m.ExpiresUtc = expire != long.MaxValue ? UnixToUtc(expire) : MdTools.Clock().AddHours(5);

            foreach (MdTrack t in tracks)
                if (t.Kind == MdTrackKind.Audio) { t.GroupId = AudioGroupId; m.Audio.Add(t); }
            m.Audio.Sort(delegate(MdTrack a, MdTrack b)
            {
                int c = b.Bandwidth.CompareTo(a.Bandwidth);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });

            AddSubtitles(m, root.Get("subtitles"), false);
            AddSubtitles(m, root.Get("automatic_captions"), true);

            bool anyVideo = false;
            foreach (MdTrack t in tracks) if (t.Kind != MdTrackKind.Audio) anyVideo = true;
            foreach (MdTrack t in tracks)
            {
                if (anyVideo && t.Kind == MdTrackKind.Audio) continue;
                MdVariant v = new MdVariant();
                v.Id = t.Id;
                v.Bandwidth = t.Bandwidth;
                v.Width = t.Width;
                v.Height = t.Height;
                v.Fps = t.Fps;
                v.Codecs = t.Codec;
                v.Main = t;
                v.AudioGroup = t.Kind == MdTrackKind.Video && m.Audio.Count > 0 ? AudioGroupId : "";
                v.SubtitleGroup = m.Subtitles.Count > 0 ? SubtitleGroupId : "";
                v.Label = Label(t);
                m.Variants.Add(v);
            }
            m.Variants.Sort(delegate(MdVariant a, MdVariant b)
            {
                int c = b.Height.CompareTo(a.Height);
                if (c == 0) c = b.Fps.CompareTo(a.Fps);
                if (c == 0) c = b.Bandwidth.CompareTo(a.Bandwidth);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });

            if (m.Variants.Count == 0)
            {
                if (drm > 0)
                {
                    m.Refused = Tr.S("видео защищено DRM — такие потоки не скачиваются", "the video is DRM-protected — such streams are not downloaded");
                    return m;
                }
                error = Tr.S("yt-dlp не нашёл форматов, которые можно скачать", "yt-dlp found no downloadable formats");
                return null;
            }
            return m;
        }

        private static MdTrack MapFormat(JVal f, bool live)
        {
            string protocol = Str(f, "protocol").ToLowerInvariant();
            string url = Str(f, "url");
            string vcodec = Str(f, "vcodec"), acodec = Str(f, "acodec");
            bool noVideo = vcodec == "none" || vcodec.Length == 0 && Str(f, "video_ext") == "none";
            bool noAudio = acodec == "none" || acodec.Length == 0 && Str(f, "audio_ext") == "none";
            if (noVideo && noAudio) return null;          // раскадровки и картинки

            MdTrack t = new MdTrack();
            t.Kind = noVideo ? MdTrackKind.Audio : noAudio ? MdTrackKind.Video : MdTrackKind.Muxed;
            string ext = Str(f, "ext").ToLowerInvariant();
            string container = Str(f, "container").ToLowerInvariant();
            if (protocol.StartsWith("m3u8", StringComparison.Ordinal))
            {
                if (!DlHttp.IsAllowedScheme(url)) return null;
                t.Layout = MdLayout.Ts;
                t.Url = url;
            }
            else if (protocol == "https" || protocol == "http" || protocol.Length == 0 && DlHttp.IsAllowedScheme(url))
            {
                if (!DlHttp.IsAllowedScheme(url)) return null;
                t.Layout = LayoutOf(ext, container);
                t.Url = url;
            }
            else if (protocol == "http_dash_segments")
            {
                if (!Fragments(f, t)) return null;
                MdLayout l = LayoutOf(ext, container);
                t.Layout = l == MdLayout.Mp4 ? MdLayout.Fmp4 : l;
                string mu = Str(f, "manifest_url");
                t.Url = DlHttp.IsAllowedScheme(mu) ? mu : t.Segments[0].Url;
            }
            else return null;                             // mhtml, rtmp, f4m, ism, websocket — не наш транспорт

            string vc = noVideo ? "" : Clip(vcodec, 64);
            string ac = noAudio ? "" : Clip(acodec, 64);
            t.Codec = t.Kind == MdTrackKind.Audio ? ac : t.Kind == MdTrackKind.Video ? vc : (vc.Length > 0 && ac.Length > 0 ? vc + "," + ac : vc + ac);
            t.FormatId = Clip(Str(f, "format_id"), 64);
            t.Width = (int)Clamp(Num(f, "width", 0), 0, 100000);
            t.Height = (int)Clamp(Num(f, "height", 0), 0, 100000);
            t.Fps = Clamp(Num(f, "fps", 0), 0, 1000);
            double tbr = Num(f, "tbr", 0);
            if (tbr <= 0) tbr = Num(f, "vbr", 0) + Num(f, "abr", 0);
            t.Bandwidth = (long)Clamp(tbr * 1000, 0, 1e12);
            string lang = Str(f, "language");
            t.Language = Regex.IsMatch(lang, @"^[A-Za-z0-9-]{1,35}$") ? lang : "";
            t.Name = Clip(Str(f, "format_note"), 120);
            t.Default = Num(f, "language_preference", -1) >= 10 || t.Name.IndexOf("default", StringComparison.OrdinalIgnoreCase) >= 0;
            double size = Num(f, "filesize", -1);
            if (size <= 0) size = Num(f, "filesize_approx", -1);
            t.SizeHint = size > 0 && size < 1e15 ? (long)size : -1;
            JVal opts = f.Get("downloader_options");
            double chunk = opts != null ? Num(opts, "http_chunk_size", 0) : 0;
            t.ChunkBytes = chunk > 0 && chunk < 1e12 ? (long)chunk : 0;
            JVal headers = f.Get("http_headers");
            if (headers != null && headers.Kind == JKind.Obj)
                for (int i = 0; i < headers.K.Count && t.Headers.Count < 32; i++)
                {
                    string name = headers.K[i], value = headers.V[i].Kind == JKind.Str ? headers.V[i].Raw : null;
                    if (value == null || DlMedia.SensitiveHeader(name) || !Regex.IsMatch(name, @"^[A-Za-z0-9-]{1,64}$")) continue;
                    if (value.Length > 4096 || value.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0) continue;
                    t.Headers[name] = value;
                }
            t.Live = live;
            return t;
        }

        // DASH-фрагменты, которые yt-dlp уже развернул: fragment_base_url + path или полный url.
        private static bool Fragments(JVal f, MdTrack t)
        {
            JVal fr = f.Get("fragments");
            if (fr == null || fr.Kind != JKind.Arr || fr.V.Count == 0 || fr.V.Count > 200000) return false;
            string baseUrl = Str(f, "fragment_base_url");
            long seq = 0;
            foreach (JVal x in fr.V)
            {
                if (x.Kind != JKind.Obj) return false;
                string u = Str(x, "url");
                if (u.Length == 0)
                {
                    Uri b, abs;
                    string path = Str(x, "path");
                    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out b) || !Uri.TryCreate(b, path, out abs)) return false;
                    u = abs.AbsoluteUri;
                }
                if (!DlHttp.IsAllowedScheme(u)) return false;
                MdSegment s = new MdSegment();
                s.Url = u;
                s.Duration = Clamp(Num(x, "duration", 0), 0, 1e6);
                s.Sequence = seq++;
                t.Segments.Add(s);
            }
            return true;
        }

        private static MdLayout LayoutOf(string ext, string container)
        {
            if (container.StartsWith("mp4", StringComparison.Ordinal) || container.StartsWith("m4a", StringComparison.Ordinal)) return MdLayout.Mp4;
            if (container.StartsWith("webm", StringComparison.Ordinal)) return MdLayout.WebM;
            switch (ext)
            {
                case "mp4": case "m4a": case "m4v": case "mov": case "3gp": return MdLayout.Mp4;
                case "webm": case "weba": return MdLayout.WebM;
                case "mp3": return MdLayout.Mp3;
                case "aac": return MdLayout.Adts;
                case "ts": return MdLayout.Ts;
                default: return MdLayout.Unknown;
            }
        }

        private static void AddSubtitles(MdManifest m, JVal map, bool auto)
        {
            if (map == null || map.Kind != JKind.Obj) return;
            for (int i = 0; i < map.K.Count && m.Subtitles.Count < MaxSubtitles; i++)
            {
                string lang = map.K[i];
                JVal list = map.V[i];
                if (list.Kind != JKind.Arr || !Regex.IsMatch(lang, @"^[A-Za-z0-9_-]{1,40}$")) continue;
                foreach (JVal e in list.V)
                {
                    if (e.Kind != JKind.Obj || Str(e, "ext").ToLowerInvariant() != "vtt") continue;
                    string url = Str(e, "url");
                    string protocol = Str(e, "protocol").ToLowerInvariant();
                    if (!DlHttp.IsAllowedScheme(url) || protocol.Length > 0 && protocol != "https" && protocol != "http") continue;
                    MdTrack s = new MdTrack();
                    s.Kind = MdTrackKind.Subtitles;
                    s.Layout = MdLayout.Vtt;
                    s.Id = (auto ? "auto-" : "sub-") + lang;
                    s.Language = lang;
                    string name = Clip(Str(e, "name"), 120);
                    s.Name = (name.Length > 0 ? name : lang) + (auto ? Tr.S(" (авто)", " (auto)") : "");
                    s.GroupId = SubtitleGroupId;
                    s.Url = url;
                    m.Subtitles.Add(s);
                    break;
                }
            }
        }

        // «1080p · 30 к/с · 4,2 Мбит/с · VP9»; звук без видео — «звук · 128 кбит/с · Opus».
        internal static string Label(MdTrack t)
        {
            List<string> parts = new List<string>();
            if (t.Kind == MdTrackKind.Audio) parts.Add(Tr.S("звук", "audio"));
            else if (t.Height > 0) parts.Add(t.Height + "p");
            else if (t.Width > 0) parts.Add(t.Width + "w");
            if (t.Kind != MdTrackKind.Audio && t.Fps > 0) parts.Add(Number(t.Fps, "0.##") + Tr.S(" к/с", " fps"));
            if (t.Bandwidth >= 1000000) parts.Add(Number(t.Bandwidth / 1e6, "0.0") + Tr.S(" Мбит/с", " Mbit/s"));
            else if (t.Bandwidth > 0) parts.Add(Math.Round(t.Bandwidth / 1e3) + Tr.S(" кбит/с", " kbit/s"));
            string codec = CodecName(t.Codec);
            if (codec.Length > 0) parts.Add(codec);
            return string.Join(" · ", parts.ToArray());
        }

        private static string CodecName(string codecs)
        {
            List<string> names = new List<string>();
            foreach (string raw in (codecs ?? "").Split(','))
            {
                string c = raw.Trim().ToLowerInvariant();
                string n = c.StartsWith("avc", StringComparison.Ordinal) || c == "h264" ? "H.264"
                         : c.StartsWith("hvc1", StringComparison.Ordinal) || c.StartsWith("hev1", StringComparison.Ordinal) || c == "h265" || c == "hevc" ? "HEVC"
                         : c.StartsWith("vp09", StringComparison.Ordinal) || c.StartsWith("vp9", StringComparison.Ordinal) ? "VP9"
                         : c.StartsWith("vp8", StringComparison.Ordinal) ? "VP8"
                         : c.StartsWith("av01", StringComparison.Ordinal) ? "AV1"
                         : c.StartsWith("mp4a", StringComparison.Ordinal) || c == "aac" ? "AAC"
                         : c == "opus" ? "Opus" : c == "mp3" ? "MP3" : c.Length > 0 && c.Length <= 12 ? c : "";
                if (n.Length > 0 && !names.Contains(n)) names.Add(n);
            }
            return string.Join("+", names.ToArray());
        }

        private static string Number(double v, string format)
        {
            string s = v.ToString(format, CultureInfo.InvariantCulture);
            return Tr.En ? s : s.Replace('.', ',');
        }

        // Срок жизни ссылки: «expire=<unix>» в query или «/expire/<unix>/» в пути (так у googlevideo).
        internal static long ExpireOf(string url)
        {
            if (string.IsNullOrEmpty(url)) return long.MaxValue;
            Match m = Regex.Match(url, @"[?&/]expire[=/]([0-9]{9,11})(?:[&/#]|$)");
            long v;
            if (!m.Success || !long.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return long.MaxValue;
            return v;
        }

        private static DateTime UnixToUtc(long seconds)
        {
            return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
        }

        // Глубина вложенности до разбора: рекурсивный разборщик на враждебном «[[[[…» уронил бы процесс переполнением стека.
        private static bool DepthOk(string s)
        {
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                }
                else if (c == '"') inStr = true;
                else if (c == '{' || c == '[') { if (++depth > MaxDepth) return false; }
                else if (c == '}' || c == ']') depth--;
            }
            return true;
        }

        private static string Str(JVal o, string name)
        {
            JVal v = o == null ? null : o.Get(name);
            return v != null && v.Kind == JKind.Str ? v.Raw : "";
        }

        private static double Num(JVal o, string name, double fallback)
        {
            JVal v = o == null ? null : o.Get(name);
            double d;
            return v != null && v.Kind == JKind.Num && double.TryParse(v.Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && !double.IsNaN(d) && !double.IsInfinity(d) ? d : fallback;
        }

        private static bool Bool(JVal o, string name)
        {
            JVal v = o == null ? null : o.Get(name);
            return v != null && v.Kind == JKind.Bool && v.B;
        }

        private static double Clamp(double v, double min, double max) { return v < min ? min : v > max ? max : v; }

        private static string Clip(string s, int max)
        {
            string t = (s ?? "").Trim();
            return t.Length > max ? t.Substring(0, max) : t;
        }

        // ---------- выбор пользователя → запрос на добавление ----------
        // Адрес записи — страница: движок при устаревших ссылках извлекает форматы заново (MdHooks.Extract). Cookie не переносятся.
        public static DlAddRequest ToAddRequest(MdManifest m, MdVariant variant, MdTrack audio, IList<MdTrack> subtitles, MdOutput output)
        {
            DlAddRequest r = new DlAddRequest();
            if (m == null || variant == null || variant.Main == null) return r;
            r.Url = m.PageUrl.Length > 0 ? m.PageUrl : m.Url;
            r.PageUrl = r.Url;
            r.Source = "ytdlp";
            string title = DlFiles.SanitizeName((m.Title ?? "").Replace('/', '-').Replace('\\', '-'));
            string ext = output == MdOutput.M4a ? ".m4a" : output == MdOutput.Mp3 ? ".mp3" : output == MdOutput.WebM ? ".webm" : output == MdOutput.Ts ? ".ts"
                       : variant.Main.Layout == MdLayout.WebM || audio != null && audio.Layout == MdLayout.WebM ? ".webm" : ".mp4";
            r.FileName = (title.Length > 0 ? title : "video") + ext;
            DlMedia media = new DlMedia();
            media.Source = MdSource.Ytdlp;
            media.ManifestUrl = r.Url;
            media.Title = m.Title ?? "";
            media.VariantId = variant.Id;
            media.AudioId = audio != null ? audio.Id : "";
            if (subtitles != null) foreach (MdTrack s in subtitles) if (s != null) media.SubtitleIds.Add(s.Id);
            media.Output = output;
            media.Live = m.Live;
            media.FormatIds = variant.Main.FormatId + (audio != null && audio.FormatId.Length > 0 ? "+" + audio.FormatId : "");
            foreach (KeyValuePair<string, string> kv in variant.Main.Headers)
                if (!DlMedia.SensitiveHeader(kv.Key)) media.Headers[kv.Key] = kv.Value;
            media.ExtractedUtc = MdTools.Clock();
            r.Media = media;
            return r;
        }
    }
}
