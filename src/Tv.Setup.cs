// SysDeck — «ТВ-трансляция»: всё, что нужно поставить, — по кнопке, без консоли и winget.
// ffmpeg скачивается с gyan.dev (сборка, на которую ссылается ffmpeg.org), сверяется по SHA-256 и распаковывается
// в папку данных; правило брандмауэра ставится одним запросом UAC. Общее ядро с PcStream: только BCL.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;

namespace SysDeck.TvStream
{
    // Распаковка нужных файлов из zip по имени внутри «…/bin/». Без System.IO.Compression.FileSystem:
    // центральный каталог читается вручную, данные — DeflateStream. Проверки — как у архива Deno (MdZip):
    // размеры, степень сжатия, CRC; имена с «..» и абсолютные пути не принимаются.
    internal static class TvZip
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

        // Имя записи подходит, если это «bin/<file>» в корне или в одной папке версии: «ffmpeg-9.0-essentials_build/bin/ffmpeg.exe».
        public static bool Matches(string entry, string file)
        {
            if (string.IsNullOrEmpty(entry) || entry.IndexOf('\\') >= 0 || entry.StartsWith("/") || entry.IndexOf(':') >= 0) return false;
            string[] parts = entry.Split('/');
            foreach (string p in parts) if (p == ".." || p == ".") return false;
            if (parts.Length == 2) return parts[0] == "bin" && parts[1].Equals(file, StringComparison.OrdinalIgnoreCase);
            if (parts.Length == 3) return parts[0].Length > 0 && parts[1] == "bin" && parts[2].Equals(file, StringComparison.OrdinalIgnoreCase);
            return false;
        }

        // wanted: имя файла → путь, куда его записать. Все должны найтись; при отказе записанное удаляется.
        public static bool Extract(Stream zip, IDictionary<string, string> wanted, long maxBytes, Func<bool> cancel, out string error)
        {
            List<string> written = new List<string>();
            bool ok = false;
            try
            {
                ok = ExtractCore(zip, wanted, maxBytes, cancel, written, out error);
                return ok;
            }
            catch (IOException ex) { error = Bad(ex.Message); return false; }
            catch (InvalidDataException ex) { error = Bad(ex.Message); return false; }
            catch (UnauthorizedAccessException ex) { error = Bad(ex.Message); return false; }
            finally
            {
                if (!ok)
                    foreach (string f in written)
                    {
                        try { if (File.Exists(f)) File.Delete(f); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
            }
        }

        private static string Bad(string why) { return Tr.S("архив ffmpeg отклонён: ", "the ffmpeg archive was rejected: ") + why; }

        private static bool ExtractCore(Stream zip, IDictionary<string, string> wanted, long maxBytes, Func<bool> cancel, List<string> written, out string error)
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
                || cdSize == 0xFFFFFFFFu || cdOff == 0xFFFFFFFFu || cdOff + cdSize > eocdPos || cdSize > (8 << 20))
            {
                error = Bad(Tr.S("многотомный, zip64 или повреждённый каталог", "multi-volume, zip64 or damaged directory"));
                return false;
            }
            byte[] cd = ReadAt(zip, cdOff, (int)cdSize);
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int pos = 0;
            for (int n = 0; n < total; n++)
            {
                if (pos + 46 > cd.Length || U32(cd, pos) != 0x02014b50u) { error = Bad(Tr.S("повреждённая запись каталога", "damaged directory entry")); return false; }
                int flags = U16(cd, pos + 8), method = U16(cd, pos + 10);
                uint crc = U32(cd, pos + 16);
                long csize = U32(cd, pos + 20), usize = U32(cd, pos + 24);
                int nlen = U16(cd, pos + 28), xlen = U16(cd, pos + 30), clen = U16(cd, pos + 32);
                long lho = U32(cd, pos + 42);
                if (pos + 46 + nlen + xlen + clen > cd.Length) { error = Bad(Tr.S("повреждённая запись каталога", "damaged directory entry")); return false; }
                string name = Encoding.UTF8.GetString(cd, pos + 46, nlen);
                pos += 46 + nlen + xlen + clen;

                string target = null, key = null;
                foreach (KeyValuePair<string, string> w in wanted)
                    if (Matches(name, w.Key)) { key = w.Key; target = w.Value; break; }
                if (target == null) continue;
                if (!found.Add(key)) { error = Bad(Tr.S("файл встречается дважды: ", "the file appears twice: ") + key); return false; }

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
                long data = lho + 30 + lnlen + lxlen;
                if (data + csize > cdOff) { error = Bad(Tr.S("данные записи выходят за границу", "entry data crosses the boundary")); return false; }

                written.Add(target);
                zip.Position = data;
                using (TvSubStream body = new TvSubStream(zip, csize))
                using (Stream src = method == 8 ? (Stream)new DeflateStream(body, CompressionMode.Decompress, true) : body)
                using (FileStream o = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buf = new byte[81920];
                    long got = 0;
                    uint c = 0xFFFFFFFFu;
                    int r;
                    while ((r = src.Read(buf, 0, buf.Length)) > 0)
                    {
                        got += r;
                        if (got > usize) { error = Bad(Tr.S("распакованные данные длиннее заявленного", "unpacked data longer than declared")); return false; }
                        for (int i = 0; i < r; i++) c = CrcTable[(c ^ buf[i]) & 0xFF] ^ (c >> 8);
                        o.Write(buf, 0, r);
                        if (cancel != null && cancel()) { error = Tr.S("отменено", "cancelled"); return false; }
                    }
                    if (got != usize) { error = Bad(Tr.S("распакованные данные короче заявленного", "unpacked data shorter than declared")); return false; }
                    if ((c ^ 0xFFFFFFFFu) != crc) { error = Bad(Tr.S("CRC не совпал", "CRC mismatch")); return false; }
                }
            }
            foreach (string k in wanted.Keys)
                if (!found.Contains(k)) { error = Bad(Tr.S("внутри нет ", "missing inside: ") + "bin/" + k); return false; }
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
    }

    // Окно чтения базового потока: не больше заданного числа байт, базовый поток не закрывается.
    internal sealed class TvSubStream : Stream
    {
        private readonly Stream _base;
        private long _left;

        public TvSubStream(Stream b, long length) { _base = b; _left = length; }

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

#pragma warning disable 649   // поля заполняет DataContractJsonSerializer
    // Ответ GitHub API о релизе: нужны только файлы, их размер, адрес и SHA-256 (поле digest).
    [DataContract]
    internal sealed class TvGhRelease
    {
        [DataMember(Name = "assets")] public List<TvGhAsset> Assets;
    }

    [DataContract]
    internal sealed class TvGhAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "digest")] public string Digest;
        [DataMember(Name = "browser_download_url")] public string Url;
    }
#pragma warning restore 649

    internal static class TvFfmpegSetup
    {
        // Основной источник — та же сборка gyan.dev, выложенная автором на GitHub (GyanD/codexffmpeg): CDN GitHub
        // отдаёт её в десятки раз быстрее (проверено: 6 МБ/с против 100 КБ/с), SHA-256 — из поля digest релиза.
        // Не ответил GitHub (лимит API, сеть) — качаем с gyan.dev, сумма из его файла .sha256.
        // Адреса переопределяют тесты (локальный сервер); пользователь их не меняет.
        internal static string ReleaseApi = "https://api.github.com/repos/GyanD/codexffmpeg/releases/latest";
        internal static string AssetPrefix = "https://github.com/GyanD/codexffmpeg/releases/download/";
        internal static string ZipUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
        internal static string ShaUrl = "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip.sha256";
        public const long ApproxBytes = 115L << 20;           // для подписи кнопки, до первого ответа сервера
        private const long MaxZipBytes = 600L << 20;
        private const long MaxEntryBytes = 400L << 20;

        // Скачать, сверить SHA-256, распаковать ffmpeg.exe и ffprobe.exe в dir. Старые файлы заменяются только после успеха.
        public static bool Install(string dir, Action<long, long> progress, Func<bool> cancel, out string error)
        {
            error = null;
            string part = Path.Combine(dir, "ffmpeg.zip.part");
            Dictionary<string, string> tmp = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            tmp["ffmpeg.exe"] = Path.Combine(dir, "ffmpeg.exe.new");
            tmp["ffprobe.exe"] = Path.Combine(dir, "ffprobe.exe.new");
            try
            {
                Directory.CreateDirectory(dir);
                EnableTls12();
                string url, want;
                if (!FromGitHub(out url, out want)) { url = ZipUrl; want = ReadSha(ShaUrl); }
                if (want == null) { error = Tr.S("Не удалось получить контрольную сумму ffmpeg — нет интернета?", "Could not get the ffmpeg checksum — no internet?"); return false; }

                string got = Download(url, part, progress, cancel, out error);
                if (got == null) return false;
                if (!got.Equals(want, StringComparison.OrdinalIgnoreCase))
                {
                    error = Tr.S("Скачанный архив ffmpeg повреждён (SHA-256 не совпал). Попробуйте ещё раз.", "The downloaded ffmpeg archive is damaged (SHA-256 mismatch). Try again.");
                    return false;
                }
                using (FileStream zs = new FileStream(part, FileMode.Open, FileAccess.Read, FileShare.Read))
                    if (!TvZip.Extract(zs, tmp, MaxEntryBytes, cancel, out error)) return false;
                foreach (KeyValuePair<string, string> kv in tmp)
                {
                    string final = Path.Combine(dir, kv.Key);
                    if (File.Exists(final)) File.Delete(final);
                    File.Move(kv.Value, final);
                }
                return true;
            }
            catch (WebException ex) { error = Tr.S("Загрузка ffmpeg не удалась: ", "ffmpeg download failed: ") + ex.Message; return false; }
            catch (IOException ex) { error = Tr.S("Не удалось записать ffmpeg: ", "Could not write ffmpeg: ") + ex.Message; return false; }
            catch (UnauthorizedAccessException ex) { error = Tr.S("Не удалось записать ffmpeg: ", "Could not write ffmpeg: ") + ex.Message; return false; }
            finally
            {
                TryDelete(part);
                foreach (string f in tmp.Values) TryDelete(f);
            }
        }

        // Последний релиз: файл «ffmpeg-<версия>-essentials_build.zip» и его «sha256:<hex>».
        internal static bool FromGitHub(out string url, out string sha)
        {
            url = null; sha = null;
            try
            {
                HttpWebRequest r = Request(ReleaseApi);
                r.Accept = "application/vnd.github+json";
                using (WebResponse resp = r.GetResponse())
                using (Stream st = resp.GetResponseStream())
                {
                    TvGhRelease rel = (TvGhRelease)new DataContractJsonSerializer(typeof(TvGhRelease)).ReadObject(st);
                    return PickAsset(rel, out url, out sha);
                }
            }
            catch (WebException) { return false; }
            catch (SerializationException) { return false; }
            catch (IOException) { return false; }
        }

        public static bool PickAsset(TvGhRelease rel, out string url, out string sha)
        {
            url = null; sha = null;
            if (rel == null || rel.Assets == null) return false;
            foreach (TvGhAsset a in rel.Assets)
            {
                if (a == null || a.Name == null || !a.Name.StartsWith("ffmpeg-", StringComparison.Ordinal)
                    || !a.Name.EndsWith("-essentials_build.zip", StringComparison.Ordinal)) continue;
                if (a.Digest == null || !a.Digest.StartsWith("sha256:", StringComparison.Ordinal)) continue;
                string h = ParseSha(a.Digest.Substring(7));
                // Качаем только из релизов этого репозитория — подменённый ответ не уведёт загрузку на чужой сервер.
                if (h == null || a.Url == null || !a.Url.StartsWith(AssetPrefix, StringComparison.Ordinal)) continue;
                url = a.Url; sha = h;
                return true;
            }
            return false;
        }

        private static void TryDelete(string f)
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // .NET 4.x по умолчанию может предложить серверу только TLS 1.0 — современные сайты такое отклоняют.
        private static void EnableTls12()
        {
            try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; }
            catch (NotSupportedException) { }
        }

        // Файл .sha256 — 64 шестнадцатеричных знака, иногда с именем файла после пробела.
        public static string ParseSha(string text)
        {
            string s = (text ?? "").Trim();
            int sp = s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
            if (sp > 0) s = s.Substring(0, sp);
            if (s.Length != 64) return null;
            foreach (char c in s) if (Uri.IsHexDigit(c) == false) return null;
            return s.ToLowerInvariant();
        }

        private static string ReadSha(string url)
        {
            try
            {
                HttpWebRequest r = Request(url);
                using (WebResponse resp = r.GetResponse())
                using (StreamReader sr = new StreamReader(resp.GetResponseStream(), Encoding.ASCII))
                {
                    char[] buf = new char[512];
                    int n = sr.Read(buf, 0, buf.Length);
                    return ParseSha(new string(buf, 0, Math.Max(0, n)));
                }
            }
            catch (WebException) { return null; }   // нет сети или файла суммы — понятное сообщение даст Install
        }

        private static HttpWebRequest Request(string url)
        {
            HttpWebRequest r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "PC-Stream/1.0";
            r.AllowAutoRedirect = true;
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        // Возвращает SHA-256 скачанного (hex) или null с error.
        private static string Download(string url, string file, Action<long, long> progress, Func<bool> cancel, out string error)
        {
            error = null;
            HttpWebRequest r = Request(url);
            using (HttpWebResponse resp = (HttpWebResponse)r.GetResponse())
            using (Stream s = resp.GetResponseStream())
            using (FileStream o = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None))
            using (SHA256 sha = SHA256.Create())
            {
                long total = resp.ContentLength > 0 ? resp.ContentLength : ApproxBytes;
                if (total > MaxZipBytes) { error = Tr.S("Архив ffmpeg неожиданно большой — загрузка остановлена.", "The ffmpeg archive is unexpectedly large — download stopped."); return null; }
                byte[] buf = new byte[81920];
                long got = 0;
                int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    got += n;
                    if (got > MaxZipBytes) { error = Tr.S("Архив ffmpeg неожиданно большой — загрузка остановлена.", "The ffmpeg archive is unexpectedly large — download stopped."); return null; }
                    sha.TransformBlock(buf, 0, n, null, 0);
                    o.Write(buf, 0, n);
                    if (progress != null) progress(got, Math.Max(total, got));
                    if (cancel != null && cancel()) { error = Tr.S("Загрузка отменена.", "Download cancelled."); return null; }
                }
                if (resp.ContentLength > 0 && got != resp.ContentLength) { error = Tr.S("Загрузка оборвалась — попробуйте ещё раз.", "The download was cut off — try again."); return null; }
                sha.TransformFinalBlock(buf, 0, 0);
                StringBuilder hex = new StringBuilder(64);
                foreach (byte b in sha.Hash) hex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return hex.ToString();
            }
        }
    }

    // Состояние брандмауэра для порта трансляции — читается без прав администратора.
    internal sealed class TvFirewallState
    {
        public bool Known;          // проверка удалась
        public bool Allowed;        // входящие на порт разрешены для текущей сети (правило порта или программы)
        public bool Blocked;        // есть запрещающее правило для этой программы (ответ «Отмена» на запрос Windows)
        public string Category;     // Public / Private / DomainAuthenticated
        public string Network;      // имя сети в Windows
    }

    internal static class TvFirewall
    {
        public static string RuleName(int port) { return "PC Stream TCP " + port; }

        private static string Quote(string s) { return "'" + (s ?? "").Replace("'", "''") + "'"; }

        // ProgressPreference: иначе PowerShell пишет в stderr XML-полосу «Подготовка модулей» при каждом запуске.
        // Чтение: правила программы и правило порта, категория сети, через которую ходит телевизор (по LAN-адресу).
        public static string StatusScript(string exe, int port, string lanIp)
        {
            return "$ErrorActionPreference='SilentlyContinue';$ProgressPreference='SilentlyContinue';"
                 + "$cat=(Get-NetIPAddress -IPAddress " + Quote(lanIp) + " | Get-NetConnectionProfile);"
                 + "if(-not $cat){$cat=Get-NetConnectionProfile | Select-Object -First 1};"
                 + "$c=[string]$cat.NetworkCategory; if($c -eq ''){$c='Public'};"
                 + "$rules=@(Get-NetFirewallApplicationFilter -Program " + Quote(exe) + " | Get-NetFirewallRule | Where-Object {$_.Direction -eq 'Inbound' -and $_.Enabled -eq 'True'});"
                 + "$rules+=@(Get-NetFirewallRule -DisplayName " + Quote(RuleName(port)) + " | Where-Object {$_.Enabled -eq 'True'});"
                 + "$fits={param($r) $p=[string]$r.Profile; $p -eq 'Any' -or $p -match $c};"
                 + "$allow=@($rules | Where-Object {$_.Action -eq 'Allow' -and (& $fits $_)}).Count;"
                 + "$block=@($rules | Where-Object {$_.Action -eq 'Block' -and (& $fits $_)}).Count;"
                 + "'allow='+$allow+';block='+$block+';cat='+$c+';net='+$cat.Name";
        }

        public static TvFirewallState ParseStatus(string output)
        {
            TvFirewallState s = new TvFirewallState();
            string line = null;
            foreach (string l in (output ?? "").Split('\n')) if (l.Trim().StartsWith("allow=")) line = l.Trim();
            if (line == null) return s;
            int allow = 0, block = 0;
            foreach (string kv in line.Split(';'))
            {
                int eq = kv.IndexOf('=');
                if (eq <= 0) continue;
                string k = kv.Substring(0, eq), v = kv.Substring(eq + 1);
                if (k == "allow") int.TryParse(v, out allow);
                else if (k == "block") int.TryParse(v, out block);
                else if (k == "cat") s.Category = v;
                else if (k == "net") s.Network = v;
            }
            s.Known = true;
            s.Blocked = block > 0;
            s.Allowed = allow > 0 && block == 0;
            return s;
        }

        // Исправление одним UAC: снять запреты для этой программы (они сильнее любого разрешения) и поставить правило
        // порта. RemoteAddress LocalSubnet вместо профиля «Частная»: домашнюю сеть Windows часто помечает общедоступной,
        // а открыт порт всё равно только своей подсети. Чужие запреты и разрешения других программ не трогаются.
        public static string FixScript(string exe, int port)
        {
            return "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';try{"
                 + "Get-NetFirewallApplicationFilter -Program " + Quote(exe) + " -ErrorAction SilentlyContinue | Get-NetFirewallRule -ErrorAction SilentlyContinue"
                 + " | Where-Object {$_.Direction -eq 'Inbound' -and $_.Action -eq 'Block'} | Remove-NetFirewallRule;"
                 + "Get-NetFirewallRule -DisplayName " + Quote(RuleName(port)) + " -ErrorAction SilentlyContinue | Remove-NetFirewallRule;"
                 + "New-NetFirewallRule -DisplayName " + Quote(RuleName(port)) + " -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + port
                 + " -RemoteAddress LocalSubnet -Profile Any | Out-Null; exit 0 }catch{ exit 1 }";
        }

        public static string Encoded(string script) { return Convert.ToBase64String(Encoding.Unicode.GetBytes(script)); }

        public static ProcessStartInfo PowerShell(string script)
        {
            ProcessStartInfo psi = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Encoded(script));
            psi.WindowStyle = ProcessWindowStyle.Hidden;
            psi.CreateNoWindow = true;
            return psi;
        }

        public static TvFirewallState Check(string exe, int port, string lanIp)
        {
            try
            {
                ProcessStartInfo psi = PowerShell(StatusScript(exe, port, lanIp));
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(20000);
                    return ParseStatus(output);
                }
            }
            catch (Exception) { return new TvFirewallState(); }
        }

        public static bool Fix(string exe, int port)
        {
            try
            {
                ProcessStartInfo psi = PowerShell(FixScript(exe, port));
                psi.UseShellExecute = true;   // runas работает только через оболочку
                psi.Verb = "runas";
                using (Process p = Process.Start(psi)) { p.WaitForExit(60000); return p.HasExited && p.ExitCode == 0; }
            }
            catch (Exception) { return false; }   // UAC отклонён
        }
    }
}
