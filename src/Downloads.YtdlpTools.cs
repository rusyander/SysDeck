// SysDeck — «Загрузки», видео: установка и обновление yt-dlp и Deno с GitHub.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Оба инструмента — официальные сборки из выпусков GitHub, лежат в <данные загрузок>\tools. Тег выпуска берётся из
// редиректа releases/latest (API GitHub ограничивает частоту запросов), а файл и его контрольная сумма — из ЭТОГО тега,
// чтобы не смешать два выпуска, вышедших между запросами. Файл качается во .tmp и сверяется по SHA-256 до любого запуска;
// Deno приходит в zip — свой разбор центрального каталога, ровно одна запись «deno.exe». Новый exe сначала отвечает на
// --version под временным именем и только потом встаёт на место старого — до этого старый не трогается.
// Доверие к установленному — запись в tools.json (SHA-256, размер, время записи): файл, изменённый после установки,
// считается непроверенным и не запускается. Без cmd, ShellExecute и прав администратора.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SysDeck.Downloads
{
    internal sealed class MdToolStatus
    {
        public string ToolsDir = "";
        public string YtdlpPath = "";
        public string DenoPath = "";
        public bool YtdlpInstalled;                  // файл есть и совпадает с записью об установке
        public bool DenoInstalled;
        public string YtdlpVersion = "";             // ответ --version; пусто — не ответил
        public string DenoVersion = "";
        public string LatestYtdlp = "";              // теги последней проверки
        public string LatestDeno = "";
        public DateTime CheckedUtc = DateTime.MinValue;
        public bool AutoUpdate = true;
        public bool UpdateAvailable;                 // чего-то нет или известен тег новее установленного
    }

    internal static class MdTools
    {
        public const string YtdlpRepo = "yt-dlp/yt-dlp";
        public const string DenoRepo = "denoland/deno";
        public const string YtdlpAsset = "yt-dlp.exe";
        public const string YtdlpSums = "SHA2-256SUMS";
        public const string DenoAsset = "deno-x86_64-pc-windows-msvc.zip";
        public const string DenoExe = "deno.exe";
        public static readonly Version MinDeno = new Version(2, 3, 0);   // ниже yt-dlp не принимает Deno как JS-среду

        private const long MaxDownloadBytes = 256L << 20;
        private const long MaxSumsBytes = 1L << 20;
        private const long MaxZipEntryBytes = 512L << 20;
        private const int VersionTimeoutMs = 15000;

        // Для тестов: своя папка, свой «GitHub» на петле, свои часы.
        internal static string DirOverride = null;
        internal static string GitHubBase = "https://github.com";
        internal static Func<DateTime> Clock = delegate { return DateTime.UtcNow; };

        private static readonly object Gate = new object();
        // Проверенные в этом процессе файлы: путь → «размер|время записи|sha». Хэш 100 МБ не считается на каждый запуск.
        private static readonly Dictionary<string, string> Verified = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string ToolsDir { get { return string.IsNullOrEmpty(DirOverride) ? Path.Combine(DlPaths.DataDir, "tools") : DirOverride; } }
        public static string YtdlpPath { get { return Path.Combine(ToolsDir, YtdlpAsset); } }
        public static string DenoPath { get { return Path.Combine(ToolsDir, DenoExe); } }
        private static string StateFile { get { return Path.Combine(ToolsDir, "tools.json"); } }

        // Для тестов: как в новом процессе — следующая проверка снова считает SHA-256.
        internal static void ForgetVerified() { lock (Gate) Verified.Clear(); }

        public static bool YtdlpReady { get { return Trusted(ReadState(), "Ytdlp", YtdlpPath); } }
        public static bool DenoReady { get { return Trusted(ReadState(), "Deno", DenoPath); } }

        // ------------------------------------------------------------------ //
        //  Состояние
        // ------------------------------------------------------------------ //
        public static MdToolStatus Status()
        {
            MdToolStatus s = new MdToolStatus();
            s.ToolsDir = ToolsDir;
            s.YtdlpPath = YtdlpPath;
            s.DenoPath = DenoPath;
            JVal st = ReadState();
            s.AutoUpdate = DlJson.Bool(st, "AutoUpdate", true);
            s.LatestYtdlp = DlJson.Str(st, "LatestYtdlp", "");
            s.LatestDeno = DlJson.Str(st, "LatestDeno", "");
            s.CheckedUtc = DlJson.Date(st, "Checked");
            s.YtdlpInstalled = Trusted(st, "Ytdlp", s.YtdlpPath);
            s.DenoInstalled = Trusted(st, "Deno", s.DenoPath);
            string err;
            if (s.YtdlpInstalled) s.YtdlpVersion = YtdlpVersionOf(s.YtdlpPath, null, out err);
            if (s.DenoInstalled)
            {
                Version v;
                s.DenoVersion = DenoVersionOf(s.DenoPath, null, out v, out err);
            }
            s.UpdateAvailable = UpdateNeeded(st, s.YtdlpInstalled, s.DenoInstalled);
            return s;
        }

        public static void SetAutoUpdate(bool on)
        {
            lock (Gate)
            {
                JVal st = ReadState();
                st.Set("AutoUpdate", DlJson.B(on));
                WriteState(st);
            }
        }

        // Проверка новых выпусков — не чаще раза в сутки, если не force. true — запрос к GitHub выполнен сейчас;
        // false — пропущен по расписанию или не удался (тогда error). updateAvailable — по свежим или сохранённым тегам.
        public static bool CheckUpdate(bool force, Func<bool> cancel, out bool updateAvailable, out string error)
        {
            error = "";
            JVal st = ReadState();
            DateTime last = DlJson.Date(st, "Checked");
            DateTime now = Clock();
            bool due = force || last == DateTime.MinValue || now - last >= TimeSpan.FromDays(1) || last > now.AddHours(1);
            bool ran = false;
            if (due)
            {
                string yt = ResolveTag(YtdlpRepo, cancel, out error);
                string dn = yt == null ? null : ResolveTag(DenoRepo, cancel, out error);
                if (yt != null && dn != null)
                {
                    SaveLatest(yt, dn);
                    ran = true;
                }
            }
            st = ReadState();
            updateAvailable = UpdateNeeded(st, Trusted(st, "Ytdlp", YtdlpPath), Trusted(st, "Deno", DenoPath));
            return ran;
        }

        // updateOnly: не качать то, что уже стоит и совпадает с последним тегом. Ошибка на первом инструменте останавливает
        // установку; всё уже стоявшее остаётся рабочим.
        public static bool Install(bool updateOnly, Action<string, double> progress, Func<bool> cancel, out string error)
        {
            error = "";
            string dir = ToolsDir;
            Mutex mutex = null;
            bool owned = false;
            try
            {
                Directory.CreateDirectory(dir);
                if (DlFiles.IsReparse(dir))
                {
                    error = Tr.S("папка инструментов — ссылка на другое место, установка отменена", "the tools folder is a link to another place, installation cancelled");
                    return false;
                }
                mutex = new Mutex(false, "Local\\WpcMdTools-" + Sha256Hex(Encoding.UTF8.GetBytes(Path.GetFullPath(dir).ToLowerInvariant())).Substring(0, 16));
                try { owned = mutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }
                if (!owned)
                {
                    error = Tr.S("установка инструментов уже идёт", "tool installation is already running");
                    return false;
                }
                Report(progress, "check", 0);
                string ytTag = ResolveTag(YtdlpRepo, cancel, out error);
                if (ytTag == null) return false;
                string dnTag = ResolveTag(DenoRepo, cancel, out error);
                if (dnTag == null) return false;
                SaveLatest(ytTag, dnTag);
                JVal st = ReadState();
                bool needYt = !updateOnly || !Trusted(st, "Ytdlp", YtdlpPath) || RecordTag(st, "Ytdlp") != ytTag;
                bool needDn = !updateOnly || !Trusted(st, "Deno", DenoPath) || RecordTag(st, "Deno") != dnTag;
                if (needYt && !InstallYtdlp(ytTag, progress, cancel, out error)) return false;
                if (needDn && !InstallDeno(dnTag, progress, cancel, out error)) return false;
                Report(progress, "done", 1);
                return true;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is WebException || ex is InvalidOperationException
                      || ex is System.ComponentModel.Win32Exception || ex is NotSupportedException || ex is ArgumentException)) throw;
                error = Tr.S("установка не удалась: ", "installation failed: ") + ex.Message;
                return false;
            }
            finally
            {
                if (owned) { try { mutex.ReleaseMutex(); } catch (ApplicationException) { } }
                if (mutex != null) mutex.Dispose();
            }
        }

        private static bool InstallYtdlp(string tag, Action<string, double> progress, Func<bool> cancel, out string error)
        {
            string dir = ToolsDir;
            string baseUrl = ReleaseBase(YtdlpRepo, tag);
            string tmp = Path.Combine(dir, YtdlpAsset + ".tmp");
            string fresh = Path.Combine(dir, "yt-dlp.new.exe");
            try
            {
                Report(progress, "yt-dlp", 0.02);
                byte[] sums = FetchBytes(baseUrl + YtdlpSums, MaxSumsBytes, cancel, out error);
                if (sums == null) return false;
                string expected = HashFromSums(sums, YtdlpAsset, true);
                if (expected == null)
                {
                    error = Tr.S("в SHA2-256SUMS нет суммы для yt-dlp.exe — файл не установлен", "SHA2-256SUMS has no checksum for yt-dlp.exe, nothing installed");
                    return false;
                }
                DeleteOwn(tmp);
                DeleteOwn(fresh);
                if (!FetchFile(baseUrl + YtdlpAsset, tmp, MaxDownloadBytes, delegate(double p) { Report(progress, "yt-dlp", 0.02 + p * 0.43); }, cancel, out error))
                    return false;
                string version;
                // Файл держится открытым без права записи от проверки суммы до конца пробного запуска: подменить нечем.
                using (FileStream hold = new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    if (!HashEquals(Sha256Hex(hold), expected))
                    {
                        error = Tr.S("контрольная сумма yt-dlp.exe не совпала — файл не установлен", "yt-dlp.exe checksum mismatch, nothing installed");
                        return false;
                    }
                    File.Move(tmp, fresh);
                    version = YtdlpVersionOf(fresh, cancel, out error);
                    if (version.Length == 0)
                    {
                        error = Tr.S("новый yt-dlp.exe не ответил на --version: ", "the new yt-dlp.exe did not answer --version: ") + error;
                        return false;
                    }
                }
                Place(fresh, YtdlpPath);
                Record("Ytdlp", tag, version, YtdlpPath);
                Report(progress, "yt-dlp", 0.5);
                return true;
            }
            finally
            {
                DeleteOwn(tmp);
                DeleteOwn(fresh);
            }
        }

        private static bool InstallDeno(string tag, Action<string, double> progress, Func<bool> cancel, out string error)
        {
            string dir = ToolsDir;
            string baseUrl = ReleaseBase(DenoRepo, tag);
            string zipTmp = Path.Combine(dir, "deno.zip.tmp");
            string exeTmp = Path.Combine(dir, "deno.exe.tmp");
            string fresh = Path.Combine(dir, "deno.new.exe");
            try
            {
                Report(progress, "deno", 0.5);
                byte[] sums = FetchBytes(baseUrl + DenoAsset + ".sha256sum", MaxSumsBytes, cancel, out error);
                if (sums == null) return false;
                string expected = HashFromSums(sums, DenoAsset, false);
                if (expected == null)
                {
                    error = Tr.S("в файле суммы Deno нет SHA-256 — архив не установлен", "the Deno checksum file has no SHA-256, nothing installed");
                    return false;
                }
                DeleteOwn(zipTmp);
                DeleteOwn(exeTmp);
                DeleteOwn(fresh);
                if (!FetchFile(baseUrl + DenoAsset, zipTmp, MaxDownloadBytes, delegate(double p) { Report(progress, "deno", 0.5 + p * 0.4); }, cancel, out error))
                    return false;
                using (FileStream zip = new FileStream(zipTmp, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!HashEquals(Sha256Hex(zip), expected))
                    {
                        error = Tr.S("контрольная сумма архива Deno не совпала — ничего не установлено", "Deno archive checksum mismatch, nothing installed");
                        return false;
                    }
                    zip.Position = 0;
                    if (!MdZip.ExtractSingle(zip, DenoExe, exeTmp, MaxZipEntryBytes, cancel, out error)) return false;
                }
                DeleteOwn(zipTmp);
                string line;
                Version v;
                using (FileStream hold = new FileStream(exeTmp, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                {
                    File.Move(exeTmp, fresh);
                    line = DenoVersionOf(fresh, cancel, out v, out error);
                    if (v == null)
                    {
                        error = Tr.S("новый deno.exe не ответил на --version: ", "the new deno.exe did not answer --version: ") + error;
                        return false;
                    }
                    if (v < MinDeno)
                    {
                        error = string.Format(Tr.S("Deno {0} слишком старый: нужен {1} или новее", "Deno {0} is too old: {1} or newer is required"), v, MinDeno);
                        return false;
                    }
                }
                Place(fresh, DenoPath);
                Record("Deno", tag, v.ToString(3), DenoPath);
                Report(progress, "deno", 0.95);
                return true;
            }
            finally
            {
                DeleteOwn(zipTmp);
                DeleteOwn(exeTmp);
                DeleteOwn(fresh);
            }
        }

        // Старый файл заменяется одним ReplaceFile; его копия .bak удаляется только после того, как новый встал.
        private static void Place(string fresh, string final)
        {
            if (File.Exists(final))
            {
                string bak = final + ".bak";
                DeleteOwn(bak);
                File.Replace(fresh, final, bak, true);
                DeleteOwn(bak);
            }
            else File.Move(fresh, final);
            lock (Gate) Verified.Remove(final);
        }

        private static void Record(string key, string tag, string version, string path)
        {
            FileInfo fi = new FileInfo(path);
            string sha;
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) sha = Sha256Hex(fs);
            lock (Gate)
            {
                JVal st = ReadState();
                JVal r = JVal.NewObj();
                r.Set("Tag", DlJson.S(tag));
                r.Set("Version", DlJson.S(version));
                r.Set("Sha256", DlJson.S(sha));
                r.Set("Size", DlJson.N(fi.Length));
                r.Set("WriteTicks", DlJson.N(fi.LastWriteTimeUtc.Ticks));
                r.Set("Installed", DlJson.D(Clock()));
                st.Set(key, r);
                WriteState(st);
                Verified[path] = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + sha;
            }
        }

        private static void SaveLatest(string ytTag, string denoTag)
        {
            lock (Gate)
            {
                JVal st = ReadState();
                st.Set("LatestYtdlp", DlJson.S(ytTag));
                st.Set("LatestDeno", DlJson.S(denoTag));
                st.Set("Checked", DlJson.D(Clock()));
                WriteState(st);
            }
        }

        private static string RecordTag(JVal st, string key)
        {
            return DlJson.Str(st.Get(key), "Tag", "");
        }

        private static bool UpdateNeeded(JVal st, bool ytInstalled, bool denoInstalled)
        {
            if (!ytInstalled || !denoInstalled) return true;
            string ly = DlJson.Str(st, "LatestYtdlp", ""), ld = DlJson.Str(st, "LatestDeno", "");
            return ly.Length > 0 && ly != RecordTag(st, "Ytdlp") || ld.Length > 0 && ld != RecordTag(st, "Deno");
        }

        // Файл совпадает с записью об установке: размер, время записи и — один раз за процесс на каждое их сочетание — SHA-256.
        private static bool Trusted(JVal st, string key, string path)
        {
            try
            {
                JVal r = st.Get(key);
                if (r == null || r.Kind != JKind.Obj) return false;
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists || DlFiles.IsReparse(path)) return false;
                string sha = DlJson.Str(r, "Sha256", "");
                if (sha.Length != 64 || fi.Length != DlJson.Long(r, "Size", -1) || fi.LastWriteTimeUtc.Ticks != DlJson.Long(r, "WriteTicks", -1)) return false;
                string stamp = fi.Length + "|" + fi.LastWriteTimeUtc.Ticks + "|" + sha;
                lock (Gate)
                {
                    string known;
                    if (Verified.TryGetValue(path, out known) && known == stamp) return true;
                }
                string actual;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) actual = Sha256Hex(fs);
                if (!HashEquals(actual, sha)) return false;
                lock (Gate) Verified[path] = stamp;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        private static JVal ReadState()
        {
            JVal st = DlPaths.ReadJson(StateFile);
            return st != null && st.Kind == JKind.Obj ? st : JVal.NewObj();
        }

        private static void WriteState(JVal st)
        {
            DlPaths.WriteAtomic(StateFile, Jsn.Write(st));
        }

        // ------------------------------------------------------------------ //
        //  Версии: пробный запуск с --version
        // ------------------------------------------------------------------ //
        internal static string YtdlpVersionOf(string exe, Func<bool> cancel, out string error)
        {
            error = "";
            MdRunResult r = MdProc.Run(exe, "--version", VersionTimeoutMs, cancel, 64 * 1024);
            if (!r.Ok) { error = r.Describe(); return ""; }
            string v = FirstLine(r.Stdout);
            if (!Regex.IsMatch(v, @"^[0-9]{4}\.[0-9]{1,2}\.[0-9]{1,2}(\.[0-9]{1,8})?$"))
            {
                error = Tr.S("неожиданный ответ: ", "unexpected answer: ") + Short(v);
                return "";
            }
            return v;
        }

        // «deno 2.5.0 (stable, release, x86_64-pc-windows-msvc)» → 2.5.0.
        internal static string DenoVersionOf(string exe, Func<bool> cancel, out Version version, out string error)
        {
            error = "";
            version = null;
            MdRunResult r = MdProc.Run(exe, "--version", VersionTimeoutMs, cancel, 64 * 1024);
            if (!r.Ok) { error = r.Describe(); return ""; }
            string line = FirstLine(r.Stdout);
            Match m = Regex.Match(line, @"^deno ([0-9]{1,5})\.([0-9]{1,5})\.([0-9]{1,5})");
            if (!m.Success)
            {
                error = Tr.S("неожиданный ответ: ", "unexpected answer: ") + Short(line);
                return "";
            }
            version = new Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture),
                                  int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            return version.ToString(3);
        }

        private static string FirstLine(string s)
        {
            string t = (s ?? "").TrimStart('\uFEFF', ' ', '\r', '\n');
            int nl = t.IndexOfAny(new[] { '\r', '\n' });
            return (nl < 0 ? t : t.Substring(0, nl)).Trim();
        }

        private static string Short(string s)
        {
            return s.Length > 80 ? s.Substring(0, 80) + "…" : s;
        }

        // ------------------------------------------------------------------ //
        //  Суммы
        // ------------------------------------------------------------------ //
        // Строки «<hex>  имя» (sha256sum) или «Hash : HEX» (PowerShell Get-FileHash | Format-List, в том числе UTF-16).
        // requireName: сумма берётся только из строки с этим именем. Иначе допускается и единственная сумма без имени.
        internal static string HashFromSums(byte[] data, string name, bool requireName)
        {
            if (data == null) return null;
            string text;
            if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) text = Encoding.Unicode.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
            else if (data.Length >= 4 && data[1] == 0 && data[3] == 0) text = Encoding.Unicode.GetString(data);
            else text = Encoding.UTF8.GetString(data).TrimStart('\uFEFF');
            HashSet<string> unnamed = new HashSet<string>();
            foreach (string raw in text.Split('\n'))
            {
                string line = raw.Trim();
                Match m = Regex.Match(line, @"^([0-9A-Fa-f]{64})(?:\s+\*?(.+))?$");
                if (m.Success)
                {
                    if (m.Groups[2].Success)
                    {
                        if (string.Equals(m.Groups[2].Value.Trim(), name, StringComparison.Ordinal)) return m.Groups[1].Value.ToLowerInvariant();
                    }
                    else unnamed.Add(m.Groups[1].Value.ToLowerInvariant());
                    continue;
                }
                m = Regex.Match(line, @"^Hash\s*:\s*([0-9A-Fa-f]{64})$");
                if (m.Success) unnamed.Add(m.Groups[1].Value.ToLowerInvariant());
            }
            if (requireName || unnamed.Count != 1) return null;
            foreach (string h in unnamed) return h;
            return null;
        }

        internal static string Sha256Hex(Stream s)
        {
            using (SHA256 h = SHA256.Create()) return Hex(h.ComputeHash(s));
        }

        internal static string Sha256Hex(byte[] b)
        {
            using (SHA256 h = SHA256.Create()) return Hex(h.ComputeHash(b));
        }

        private static string Hex(byte[] b)
        {
            StringBuilder sb = new StringBuilder(b.Length * 2);
            foreach (byte x in b) sb.Append(x.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        private static bool HashEquals(string a, string b)
        {
            return !string.IsNullOrEmpty(a) && a.Length == 64 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ //
        //  Сеть
        // ------------------------------------------------------------------ //
        private static string ReleaseBase(string repo, string tag)
        {
            return GitHubBase.TrimEnd('/') + "/" + repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/";
        }

        // Тег последнего выпуска: releases/latest отвечает редиректом на …/releases/tag/<тег>.
        internal static string ResolveTag(string repo, Func<bool> cancel, out string error)
        {
            error = "";
            string url = GitHubBase.TrimEnd('/') + "/" + repo + "/releases/latest";
            HttpWebResponse resp = null;
            try
            {
                if (cancel != null && cancel()) { error = Cancelled(); return null; }
                HttpWebRequest req = NewRequest(url);
                resp = Send(req);
                int code = (int)resp.StatusCode;
                string loc = resp.Headers["Location"];
                if (code < 300 || code > 399 || string.IsNullOrEmpty(loc))
                {
                    error = string.Format(Tr.S("GitHub не назвал последний выпуск {0} (HTTP {1})", "GitHub did not name the latest release of {0} (HTTP {1})"), repo, code);
                    return null;
                }
                Uri target;
                if (!Uri.TryCreate(new Uri(url), loc, out target))
                {
                    error = Tr.S("GitHub вернул непонятный адрес выпуска", "GitHub returned an unreadable release address");
                    return null;
                }
                string path = target.AbsolutePath;
                int at = path.IndexOf("/releases/tag/", StringComparison.Ordinal);
                string tag = at < 0 ? "" : Uri.UnescapeDataString(path.Substring(at + 14)).TrimEnd('/');
                if (!Regex.IsMatch(tag, @"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$"))
                {
                    error = Tr.S("GitHub вернул непонятный тег выпуска", "GitHub returned an unreadable release tag");
                    return null;
                }
                return tag;
            }
            catch (WebException ex)
            {
                error = NetError(ex, cancel);
                return null;
            }
            catch (IOException ex)
            {
                error = Tr.S("нет связи с GitHub: ", "no connection to GitHub: ") + ex.Message;
                return null;
            }
            finally
            {
                if (resp != null) resp.Close();
            }
        }

        private static byte[] FetchBytes(string url, long cap, Func<bool> cancel, out string error)
        {
            using (MemoryStream ms = new MemoryStream())
                return Fetch(url, ms, cap, null, cancel, out error) ? ms.ToArray() : null;
        }

        private static bool FetchFile(string url, string file, long cap, Action<double> progress, Func<bool> cancel, out string error)
        {
            bool ok = false;
            try
            {
                using (FileStream fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    ok = Fetch(url, fs, cap, progress, cancel, out error);
                    if (ok) fs.Flush(true);
                }
                return ok;
            }
            finally
            {
                if (!ok) DeleteOwn(file);
            }
        }

        // GET с ручными редиректами: каждый шаг — DlHttp.CheckRedirect. Длина из заголовка обязана совпасть с полученной.
        private static bool Fetch(string url, Stream sink, long cap, Action<double> progress, Func<bool> cancel, out string error)
        {
            error = "";
            string cur = url;
            for (int hop = 0; hop <= DlHttp.MaxRedirects; hop++)
            {
                if (cancel != null && cancel()) { error = Cancelled(); return false; }
                if (!DlHttp.IsAllowedScheme(cur))
                {
                    error = Tr.S("недопустимый адрес: ", "disallowed address: ") + DlLog.Redact(cur);
                    return false;
                }
                HttpWebRequest req = NewRequest(cur);
                HttpWebResponse resp = null;
                Timer watchdog = null;
                try
                {
                    if (cancel != null)
                        watchdog = new Timer(delegate { try { if (cancel()) req.Abort(); } catch (Exception) { } }, null, 250, 250);
                    resp = Send(req);
                    int code = (int)resp.StatusCode;
                    if (code >= 300 && code <= 399)
                    {
                        string loc = resp.Headers["Location"];
                        Uri next;
                        if (string.IsNullOrEmpty(loc) || !Uri.TryCreate(new Uri(cur), loc, out next))
                        {
                            error = Tr.S("редирект без адреса: ", "redirect without an address: ") + DlLog.Redact(cur);
                            return false;
                        }
                        DlFailure f = DlHttp.CheckRedirect(cur, next.AbsoluteUri, false);
                        if (f != null) { error = f.Message; return false; }
                        cur = next.AbsoluteUri;
                        continue;
                    }
                    if (code != 200)
                    {
                        error = string.Format(Tr.S("сервер ответил HTTP {0}: {1}", "the server answered HTTP {0}: {1}"), code, DlLog.Redact(cur));
                        return false;
                    }
                    long len = resp.ContentLength;
                    if (len > cap)
                    {
                        error = Tr.S("файл больше допустимого: ", "the file is larger than allowed: ") + DlLog.Redact(cur);
                        return false;
                    }
                    long got = 0;
                    byte[] buf = new byte[81920];
                    using (Stream body = resp.GetResponseStream())
                    {
                        int n;
                        while ((n = body.Read(buf, 0, buf.Length)) > 0)
                        {
                            got += n;
                            if (got > cap)
                            {
                                error = Tr.S("файл больше допустимого: ", "the file is larger than allowed: ") + DlLog.Redact(cur);
                                return false;
                            }
                            sink.Write(buf, 0, n);
                            if (progress != null && len > 0) progress((double)got / len);
                            if (cancel != null && cancel()) { error = Cancelled(); return false; }
                        }
                    }
                    if (len >= 0 && got != len)
                    {
                        error = Tr.S("загрузка оборвалась: ", "the download was cut off: ") + DlLog.Redact(cur);
                        return false;
                    }
                    return true;
                }
                catch (WebException ex)
                {
                    error = NetError(ex, cancel);
                    return false;
                }
                catch (IOException ex)
                {
                    error = (cancel != null && cancel()) ? Cancelled() : Tr.S("загрузка оборвалась: ", "the download was cut off: ") + ex.Message;
                    return false;
                }
                finally
                {
                    if (watchdog != null) watchdog.Dispose();
                    if (resp != null) resp.Close();
                }
            }
            error = Tr.S("слишком много редиректов: ", "too many redirects: ") + DlLog.Redact(url);
            return false;
        }

        private static HttpWebRequest NewRequest(string url)
        {
            DlHttp.Init();
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "GET";
            req.AllowAutoRedirect = false;
            req.AutomaticDecompression = DecompressionMethods.None;
            req.UserAgent = DlSettings.DefaultUserAgent;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 30000;
            return req;
        }

        // Ответ 4xx/5xx .NET отдаёт исключением — здесь он возвращается как обычный ответ.
        private static HttpWebResponse Send(HttpWebRequest req)
        {
            try { return (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex)
            {
                HttpWebResponse r = ex.Response as HttpWebResponse;
                if (ex.Status == WebExceptionStatus.ProtocolError && r != null) return r;
                throw;
            }
        }

        private static string NetError(WebException ex, Func<bool> cancel)
        {
            if (ex.Status == WebExceptionStatus.RequestCanceled || cancel != null && cancel()) return Cancelled();
            return Tr.S("нет связи с GitHub: ", "no connection to GitHub: ") + ex.Message;
        }

        private static string Cancelled() { return Tr.S("отменено", "cancelled"); }

        private static void Report(Action<string, double> progress, string stage, double value)
        {
            if (progress != null) progress(stage, Math.Max(0, Math.Min(1, value)));
        }

        // Только собственные временные файлы по точным именам.
        private static void DeleteOwn(string path)
        {
            try { if (File.Exists(path) && !DlFiles.IsReparse(path)) File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------------ //
    //  Свой разбор zip: центральный каталог, одна запись, deflate
    // ------------------------------------------------------------------ //
    internal static class MdZip
    {
        private const int MaxRatio = 100;
        private static readonly uint[] CrcTable = MakeCrcTable();

        private static uint[] MakeCrcTable()
        {
            uint[] t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        // В архиве ровно одна запись, её имя — ровно entryName (без папок и «..»). Иначе отказ; outPath при отказе не остаётся.
        internal static bool ExtractSingle(Stream zip, string entryName, string outPath, long maxBytes, Func<bool> cancel, out string error)
        {
            error = "";
            bool ok = false;
            try
            {
                ok = Extract(zip, entryName, outPath, maxBytes, cancel, out error);
                return ok;
            }
            catch (IOException ex) { error = Bad(ex.Message); return false; }
            catch (InvalidDataException ex) { error = Bad(ex.Message); return false; }
            catch (UnauthorizedAccessException ex) { error = Bad(ex.Message); return false; }
            finally
            {
                if (!ok)
                {
                    try { if (File.Exists(outPath)) File.Delete(outPath); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static string Bad(string why)
        {
            return Tr.S("архив Deno отклонён: ", "the Deno archive was rejected: ") + why;
        }

        private static bool Extract(Stream zip, string entryName, string outPath, long maxBytes, Func<bool> cancel, out string error)
        {
            error = "";
            long len = zip.Length;
            if (len < 22) { error = Bad(Tr.S("слишком короткий", "too short")); return false; }
            int tail = (int)Math.Min(len, 22 + 65535);
            byte[] t = ReadAt(zip, len - tail, tail);
            int eocd = -1;
            for (int i = tail - 22; i >= 0; i--)
                if (U32(t, i) == 0x06054b50u && i + 22 + U16(t, i + 20) == tail) { eocd = i; break; }
            if (eocd < 0) { error = Bad(Tr.S("нет центрального каталога", "no central directory")); return false; }
            int total = U16(t, eocd + 10);
            long cdSize = U32(t, eocd + 12), cdOff = U32(t, eocd + 16);
            long eocdPos = len - tail + eocd;
            if (U16(t, eocd + 4) != 0 || U16(t, eocd + 6) != 0 || U16(t, eocd + 8) != total || total == 0xFFFF
                || cdSize == 0xFFFFFFFFu || cdOff == 0xFFFFFFFFu || cdOff + cdSize > eocdPos || cdSize > (1 << 20))
            {
                error = Bad(Tr.S("многотомный, zip64 или повреждённый каталог", "multi-volume, zip64 or damaged directory"));
                return false;
            }
            if (total != 1)
            {
                error = Bad(string.Format(Tr.S("записей {0}, ожидалась одна «{1}»", "{0} entries, exactly one \"{1}\" expected"), total, entryName));
                return false;
            }
            byte[] cd = ReadAt(zip, cdOff, (int)cdSize);
            if (cd.Length < 46 || U32(cd, 0) != 0x02014b50u) { error = Bad(Tr.S("повреждённая запись каталога", "damaged directory entry")); return false; }
            int flags = U16(cd, 8), method = U16(cd, 10);
            uint crc = U32(cd, 16);
            long csize = U32(cd, 20), usize = U32(cd, 24);
            int nlen = U16(cd, 28), xlen = U16(cd, 30), clen = U16(cd, 32);
            long lho = U32(cd, 42);
            if (46 + nlen + xlen + clen > cd.Length) { error = Bad(Tr.S("повреждённая запись каталога", "damaged directory entry")); return false; }
            byte[] want = Encoding.ASCII.GetBytes(entryName);
            if (!SameBytes(cd, 46, nlen, want))
            {
                error = Bad(Tr.S("неожиданное имя файла внутри: ", "unexpected file name inside: ") + Printable(cd, 46, nlen));
                return false;
            }
            if ((flags & 1) != 0) { error = Bad(Tr.S("зашифрован", "encrypted")); return false; }
            if (method != 0 && method != 8) { error = Bad(Tr.S("неизвестное сжатие", "unknown compression")); return false; }
            if (csize == 0xFFFFFFFFu || usize == 0xFFFFFFFFu || usize > maxBytes) { error = Bad(Tr.S("файл внутри слишком большой", "the file inside is too large")); return false; }
            if (method == 0 && csize != usize || method == 8 && usize > 0 && (csize == 0 || usize / csize > MaxRatio))
            {
                error = Bad(Tr.S("размеры сжатых данных не сходятся", "compressed sizes do not add up"));
                return false;
            }
            if (lho + 30 > cdOff) { error = Bad(Tr.S("локальный заголовок вне архива", "local header outside the archive")); return false; }
            byte[] lh = ReadAt(zip, lho, 30);
            int lnlen = U16(lh, 26), lxlen = U16(lh, 28);
            if (U32(lh, 0) != 0x04034b50u || lnlen != nlen) { error = Bad(Tr.S("локальный заголовок не совпадает с каталогом", "local header does not match the directory")); return false; }
            byte[] lname = ReadAt(zip, lho + 30, lnlen);
            if (!SameBytes(lname, 0, lnlen, want)) { error = Bad(Tr.S("локальный заголовок не совпадает с каталогом", "local header does not match the directory")); return false; }
            long data = lho + 30 + lnlen + lxlen;
            if (data + csize > cdOff) { error = Bad(Tr.S("данные записи выходят за границу", "entry data crosses the boundary")); return false; }
            zip.Position = data;
            using (MdSubStream body = new MdSubStream(zip, csize))
            using (Stream src = method == 8 ? (Stream)new DeflateStream(body, CompressionMode.Decompress, true) : body)
            using (FileStream o = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buf = new byte[81920];
                long got = 0;
                uint c = 0xFFFFFFFFu;
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    got += n;
                    if (got > usize) { error = Bad(Tr.S("распакованные данные длиннее заявленного", "unpacked data longer than declared")); return false; }
                    for (int i = 0; i < n; i++) c = CrcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
                    o.Write(buf, 0, n);
                    if (cancel != null && cancel()) { error = Tr.S("отменено", "cancelled"); return false; }
                }
                if (got != usize) { error = Bad(Tr.S("распакованные данные короче заявленного", "unpacked data shorter than declared")); return false; }
                if ((c ^ 0xFFFFFFFFu) != crc) { error = Bad(Tr.S("CRC не совпал", "CRC mismatch")); return false; }
                o.Flush(true);
            }
            return true;
        }

        private static byte[] ReadAt(Stream s, long pos, int count)
        {
            byte[] b = new byte[count];
            s.Position = pos;
            int got = 0;
            while (got < count)
            {
                int n = s.Read(b, got, count - got);
                if (n <= 0) throw new InvalidDataException("unexpected end of archive");
                got += n;
            }
            return b;
        }

        private static int U16(byte[] b, int i) { return b[i] | b[i + 1] << 8; }
        private static uint U32(byte[] b, int i) { return (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16) | (uint)b[i + 3] << 24; }

        private static bool SameBytes(byte[] b, int off, int len, byte[] want)
        {
            if (len != want.Length || off + len > b.Length) return false;
            for (int i = 0; i < len; i++) if (b[off + i] != want[i]) return false;
            return true;
        }

        private static string Printable(byte[] b, int off, int len)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < len && i < 80 && off + i < b.Length; i++) sb.Append(b[off + i] >= 0x20 && b[off + i] < 0x7F ? (char)b[off + i] : '?');
            return sb.ToString();
        }
    }

    // Окно чтения базового потока: не больше заданного числа байт, базовый поток не закрывается.
    internal sealed class MdSubStream : Stream
    {
        private readonly Stream _base;
        private long _left;

        public MdSubStream(Stream b, long length) { _base = b; _left = length; }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left <= 0) return 0;
            int n = _base.Read(buffer, offset, (int)Math.Min(count, _left));
            if (n > 0) _left -= n;
            return n;
        }
    }
}
