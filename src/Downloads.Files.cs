// Windows Process Cleaner — «Загрузки»: имя и путь файла, правила папок, опасные типы, разреженный файл, Корзина.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Имя приходит от сервера или страницы — то есть от кого угодно. Отсюда: никаких разделителей пути и двоеточий (ADS),
// никаких управляющих и bidi-символов (подмена «txt.exe» → «exe.txt» на экране), никаких зарезервированных имён
// Windows, и итоговый путь обязан лежать внутри выбранной папки. Файл, который уже есть, не перезаписывается никогда:
// берётся имя « (1)», « (2)» …
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WindowsProcessCleaner.Downloads
{
    internal static class DlFiles
    {
        public const int MaxNameLength = 200;
        private const uint ReparsePoint = 0x400, InvalidAttributes = 0xFFFFFFFF;

        private static readonly string[] Reserved =
        {
            "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³"
        };

        // Не открываются сами по завершении ни при каких настройках: только явным действием, после MOTW.
        private static readonly string[] Dangerous =
        {
            ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".msp", ".bat", ".cmd", ".ps1", ".psm1", ".vbs",
            ".vbe", ".js", ".jse", ".wsf", ".wsh", ".hta", ".scr", ".pif", ".com", ".cpl", ".lnk", ".url", ".jar", ".reg",
            ".iso", ".img", ".vhd", ".vhdx", ".application", ".appref-ms", ".gadget", ".inf", ".sct", ".chm", ".dll", ".sys"
        };

        // ---------- имя ----------
        public static string SanitizeName(string name)
        {
            string s = name ?? "";
            // Путь целиком (a/b/c.zip, C:\x\y.exe) — от него остаётся последний элемент.
            int cut = s.LastIndexOfAny(new[] { '/', '\\' });
            if (cut >= 0) s = s.Substring(cut + 1);
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c < 0x20 || c == 0x7F) continue;
                if (c >= 0x80 && c <= 0x9F) continue;
                if (IsBidiOrInvisible(c)) continue;
                if ("<>:\"|?*".IndexOf(c) >= 0) { sb.Append('_'); continue; }
                sb.Append(c);
            }
            s = sb.ToString().Trim();
            // Завершающие точки и пробелы Windows отрезает сама — «a.exe.» стал бы «a.exe» уже после проверок.
            s = s.TrimEnd('.', ' ');
            s = s.TrimStart(' ');
            if (s == "" || s == "." || s == "..") s = "download";
            string stem = s;
            int dot = s.IndexOf('.');
            if (dot >= 0) stem = s.Substring(0, dot);
            foreach (string r in Reserved)
                if (string.Equals(stem.TrimEnd(' '), r, StringComparison.OrdinalIgnoreCase)) { s = "_" + s; break; }
            if (s.Length > MaxNameLength)
            {
                string ext = Path.GetExtension(s);
                if (ext.Length > 16) ext = "";
                s = s.Substring(0, MaxNameLength - ext.Length).TrimEnd('.', ' ') + ext;
            }
            return s;
        }

        private static bool IsBidiOrInvisible(char c)
        {
            return (c >= '\u202A' && c <= '\u202E') || (c >= '\u2066' && c <= '\u2069') || c == '\u200E' || c == '\u200F'
                   || c == '\u061C' || (c >= '\u200B' && c <= '\u200D') || c == '\uFEFF';
        }

        public static bool IsDangerous(string name)
        {
            string ext = "";
            try { ext = Path.GetExtension(name ?? "").ToLowerInvariant(); } catch { return true; }
            return Array.IndexOf(Dangerous, ext) >= 0;
        }

        // Имя из последнего сегмента адреса (после раскодирования).
        public static string NameFromUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) return "";
            string path = u.AbsolutePath;
            int slash = path.LastIndexOf('/');
            string last = slash >= 0 ? path.Substring(slash + 1) : path;
            try { last = Uri.UnescapeDataString(last); } catch { }
            return last.Trim();
        }

        public static string ExtensionForMime(string contentType)
        {
            string m = (contentType ?? "").ToLowerInvariant();
            int semi = m.IndexOf(';');
            if (semi >= 0) m = m.Substring(0, semi);
            switch (m.Trim())
            {
                case "application/zip": case "application/x-zip-compressed": return ".zip";
                case "application/x-7z-compressed": return ".7z";
                case "application/vnd.rar": case "application/x-rar-compressed": return ".rar";
                case "application/gzip": case "application/x-gzip": return ".gz";
                case "application/pdf": return ".pdf";
                case "application/x-msdownload": case "application/vnd.microsoft.portable-executable": return ".exe";
                case "application/x-msi": case "application/x-ole-storage": return ".msi";
                case "application/x-bittorrent": return ".torrent";
                case "application/json": return ".json";
                case "text/plain": return ".txt";
                case "text/html": return ".html";
                case "text/csv": return ".csv";
                case "image/png": return ".png";
                case "image/jpeg": return ".jpg";
                case "image/gif": return ".gif";
                case "image/webp": return ".webp";
                case "audio/mpeg": return ".mp3";
                case "video/mp4": return ".mp4";
                case "video/webm": return ".webm";
                case "application/x-iso9660-image": return ".iso";
                default: return "";
            }
        }

        // ---------- папка ----------
        // Первое совпавшее правило «сайт», затем «тип»; иначе папка по умолчанию.
        public static string FolderFor(DlSettings settings, string url, string fileName)
        {
            string host = "";
            Uri u;
            if (Uri.TryCreate(url ?? "", UriKind.Absolute, out u)) host = u.Host.ToLowerInvariant();
            foreach (DlFolderRule r in settings.Rules)
                if (r.Kind == "site" && HostMatches(host, r.Pattern)) return r.Folder;
            string ext = "";
            try { ext = Path.GetExtension(fileName ?? "").TrimStart('.').ToLowerInvariant(); } catch { }
            if (ext.Length > 0)
                foreach (DlFolderRule r in settings.Rules)
                {
                    if (r.Kind != "ext") continue;
                    foreach (string p in r.Pattern.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                        if (string.Equals(p.Trim().TrimStart('*').TrimStart('.'), ext, StringComparison.OrdinalIgnoreCase)) return r.Folder;
                }
            return settings.EffectiveFolder;
        }

        // «example.com» — сам хост; «*.example.com» — он и все поддомены.
        public static bool HostMatches(string host, string pattern)
        {
            string p = (pattern ?? "").Trim().ToLowerInvariant();
            if (host.Length == 0 || p.Length == 0) return false;
            if (p.StartsWith("*.")) { p = p.Substring(2); return host == p || host.EndsWith("." + p); }
            return host == p;
        }

        // Папка годится для записи: абсолютная, не устройство, не системный каталог. Возвращает канонический путь или null.
        public static string CheckFolder(string folder, out string why)
        {
            why = null;
            string f = (folder ?? "").Trim();
            if (f.Length == 0 || f.StartsWith(@"\\?\") || f.StartsWith(@"\\.\") || f.IndexOf('\0') >= 0)
            {
                why = Tr.S("недопустимая папка", "invalid folder");
                return null;
            }
            string full;
            try
            {
                if (!Path.IsPathRooted(f) || (f.Length >= 2 && f[1] == ':' && (f.Length == 2 || (f[2] != '\\' && f[2] != '/'))))
                {
                    why = Tr.S("папка должна быть указана полным путём", "the folder must be an absolute path");
                    return null;
                }
                full = Path.GetFullPath(f).TrimEnd('\\');
                if (full.Length == 2 && full[1] == ':') full += "\\";
            }
            catch (Exception ex)
            {
                why = ex.Message;
                return null;
            }
            string canonical = Native.CanonicalPath(full) ?? full;
            foreach (string guarded in GuardedRoots())
                if (IsSameOrUnder(canonical, guarded))
                {
                    why = Tr.S("в системную папку загрузки не пишутся: ", "downloads never go into a system folder: ") + guarded;
                    return null;
                }
            return canonical;
        }

        private static IEnumerable<string> GuardedRoots()
        {
            List<string> roots = new List<string>();
            foreach (Environment.SpecialFolder sf in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles,
                                                             Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonApplicationData })
            {
                string p = "";
                try { p = Environment.GetFolderPath(sf); } catch { }
                if (!string.IsNullOrEmpty(p)) roots.Add(Native.CanonicalPath(p) ?? p);
            }
            try { roots.Add(Native.CanonicalPath(Engine.DefaultDataDir()) ?? Engine.DefaultDataDir()); } catch { }
            return roots;
        }

        public static bool IsSameOrUnder(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = path.TrimEnd('\\') + "\\";
            string r = root.TrimEnd('\\') + "\\";
            return p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }

        // Полный путь файла внутри папки или null. Имя проходит SanitizeName ещё раз: сюда могут прийти и старые записи.
        public static string PathInside(string folder, string name)
        {
            string clean = SanitizeName(name);
            if (clean != name) return null;
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(Path.Combine(root, clean));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
                if (full.IndexOf(':', 2) >= 0) return null;
                return full;
            }
            catch { return null; }
        }

        public static bool IsReparse(string path)
        {
            uint a = Native.AttributesOf(path);
            return a != InvalidAttributes && (a & ReparsePoint) != 0;
        }

        public static bool Exists(string path)
        {
            return Native.AttributesOf(path) != InvalidAttributes;
        }

        // Свободное имя: ни файла, ни частичного файла, ни имени, занятого другой загрузкой. «a.zip» → «a (1).zip».
        public static string UniqueName(string folder, string name, ICollection<string> taken)
        {
            string stem = Path.GetFileNameWithoutExtension(name);
            string ext = Path.GetExtension(name);
            // «archive.tar.gz» → «archive (1).tar.gz»
            if (ext.Length > 0 && stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                stem = stem.Substring(0, stem.Length - 4);
                ext = ".tar" + ext;
            }
            for (int n = 0; n < 10000; n++)
            {
                string candidate = n == 0 ? name : stem + " (" + n + ")" + ext;
                string full = Path.Combine(folder, candidate);
                if (Exists(full) || Exists(full + DlPaths.PartSuffix)) continue;
                if (taken != null && taken.Contains(full.ToLowerInvariant())) continue;
                return candidate;
            }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceExW(string directory, out long freeForCaller, out long total, out long totalFree);

        public static long FreeSpace(string folder)
        {
            long free, total, totalFree;
            try
            {
                // Папки ещё может не быть (правило, первая загрузка её создаст): API хочет существующую — берём ближайшую.
                string dir = folder;
                while (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
                if (string.IsNullOrEmpty(dir)) return -1;
                return GetDiskFreeSpaceExW(dir.TrimEnd('\\') + "\\", out free, out total, out totalFree) ? free : -1;
            }
            catch { return -1; }
        }

        // ---------- разреженный файл ----------
        // Сегменты пишут в разные места файла. Обычный файл NTFS при записи далеко за концом допишет нулями всё, что
        // между (минуты на HDD для последнего сегмента большого файла); разреженный — нет. По завершении флаг снимается.
        private const uint FsctlSetSparse = 0x000900C4;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
                                                   out int returned, IntPtr overlapped);

        public static bool SetSparse(FileStream fs, bool on)
        {
            try
            {
                int returned;
                return DeviceIoControl(fs.SafeFileHandle, FsctlSetSparse, new[] { on ? (byte)1 : (byte)0 }, 1, IntPtr.Zero, 0, out returned, IntPtr.Zero);
            }
            catch { return false; }
        }

        // ---------- Корзина ----------
        // Файлы загрузок удаляются только в Корзину. Нет Корзины на томе (сеть, флешка) — отказ, а не безвозвратное удаление.
        // Подменяется в тестах: прогон не кладёт ничего в настоящую Корзину пользователя.
        internal static Func<string, string> Recycler = RecycleToBin;

        private static string RecycleToBin(string path)
        {
            if (!Engine.RecycleBinAvailable(path))
                return Tr.S("на этом томе нет Корзины — файл не удалён", "this volume has no Recycle Bin — the file was not deleted");
            string message;
            int gone = Engine.RecycleToBin(new List<string> { path }, out message);
            return gone == 1 ? null : message ?? Tr.S("файл не удалось переместить в Корзину", "the file could not be moved to the Recycle Bin");
        }

        public static string Recycle(string path)
        {
            if (string.IsNullOrEmpty(path) || !Exists(path)) return null;
            if (IsReparse(path)) return Tr.S("это ссылка, а не файл загрузки — не трогаю", "this is a link, not a downloaded file — left alone");
            return Recycler(path);
        }
    }
}
