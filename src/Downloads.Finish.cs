// Windows Process Cleaner — «Загрузки»: завершение — хеш, переименование частичного файла, Mark-of-the-Web, антивирус.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Порядок важен: сначала хеш (не совпал — файл остаётся .wpcpart и случайно не запустится), потом имя на место, потом
// метка «из интернета». Метку ставит IAttachmentExecute — тот же путь, что у браузеров: он учитывает политики зон и
// вызывает зарегистрированный антивирус. Если COM недоступен, пишется поток Zone.Identifier вручную. Адреса в метке
// без query-строки: в ней бывают токены.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace WindowsProcessCleaner.Downloads
{
    internal static class DlFinish
    {
        public static DlFailure Complete(DlItem item, DlSettings settings, IDlEnvironment env, IDlTransferHost host, Func<bool> cancel)
        {
            string target = DlFiles.PathInside(item.Folder, item.FileName);
            if (target == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
            string part = target + DlPaths.PartSuffix;
            if (DlFiles.IsReparse(part)) return DlFailure.Make(DlErrorKind.Policy, Tr.S("на месте частичного файла ссылка", "a link sits where the partial file is"));

            try
            {
                using (FileStream fs = new FileStream(part, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete))
                {
                    if (item.Total >= 0 && fs.Length != item.Total) fs.SetLength(item.Total);
                    DlFiles.SetSparse(fs, false);
                }
            }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, ex.Message); }

            host.Journal(item, Tr.S("все байты получены, проверка", "all bytes received, verifying"));
            string sha256 = Hash(part, "sha256", cancel);
            if (sha256 == null) return cancel() ? null : DlFailure.Make(DlErrorKind.Disk, Tr.S("файл не читается для проверки", "the file cannot be read for verification"));
            item.Sha256 = sha256;
            string expectedAlgo, expected;
            if (ParseExpected(item.ExpectedHash, out expectedAlgo, out expected))
            {
                string actual = expectedAlgo == "sha256" ? sha256 : Hash(part, expectedAlgo, cancel);
                if (actual == null) return cancel() ? null : DlFailure.Make(DlErrorKind.Disk, Tr.S("файл не читается для проверки", "the file cannot be read for verification"));
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return DlFailure.Make(DlErrorKind.HashMismatch, Tr.S("хеш не совпал с ожидаемым (", "the hash does not match the expected one (") + expectedAlgo + ": " + actual
                                                                    + Tr.S(") — файл оставлен недокачанным (.wpcpart)", ") — the file is kept as partial (.wpcpart)"));
            }

            // Пока качали, на месте итогового имени мог появиться файл: чужой не перезаписываем.
            if (DlFiles.Exists(target))
            {
                string unique = host.ReserveName(item, item.Folder, item.FileName);
                if (unique == null) return DlFailure.Make(DlErrorKind.Disk, Tr.S("не найдено свободное имя файла", "no free file name found"));
                string newTarget = DlFiles.PathInside(item.Folder, unique);
                if (newTarget == null) return DlFailure.Make(DlErrorKind.Policy, Tr.S("имя файла уводит из папки загрузки", "the file name leads out of the download folder"));
                try { File.Move(part, newTarget + DlPaths.PartSuffix); }
                catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, ex.Message); }
                item.FileName = unique;
                target = newTarget;
                part = target + DlPaths.PartSuffix;
            }
            try { File.Move(part, target); }
            catch (Exception ex) { return DlFailure.Make(DlErrorKind.Disk, Tr.S("файл не переименовать: ", "the file cannot be renamed: ") + ex.Message); }

            if (settings.MarkOfTheWeb)
            {
                string why = DlMotw.Apply(target, string.IsNullOrEmpty(item.FinalUrl) ? item.Url : item.FinalUrl, item.Referrer);
                if (!DlFiles.Exists(target))
                    return DlFailure.Make(DlErrorKind.Blocked, Tr.S("антивирус или политика безопасности удалили файл", "the antivirus or a security policy removed the file")
                                                              + (why != null ? " (" + why + ")" : ""));
                if (why != null) host.Journal(item, Tr.S("метка «из интернета»: ", "mark of the web: ") + why);
            }
            if (settings.DefenderScan)
            {
                string threat = DlMotw.DefenderScan(target, cancel);
                if (threat != null) return DlFailure.Make(DlErrorKind.Blocked, threat);
            }
            item.CompletedUtc = env.UtcNow;
            host.Journal(item, Tr.S("готово: ", "done: ") + target + ", SHA-256 " + sha256);
            return null;
        }

        // «sha256:HEX», «sha1:HEX», «md5:HEX» или просто HEX (алгоритм по длине).
        public static bool ParseExpected(string text, out string algo, out string hex)
        {
            algo = hex = "";
            string t = (text ?? "").Trim();
            if (t.Length == 0) return false;
            int colon = t.IndexOf(':');
            if (colon > 0)
            {
                algo = t.Substring(0, colon).Trim().ToLowerInvariant().Replace("-", "");
                hex = t.Substring(colon + 1).Trim();
            }
            else
            {
                hex = t;
                algo = hex.Length == 64 ? "sha256" : hex.Length == 40 ? "sha1" : hex.Length == 32 ? "md5" : "";
            }
            int want = algo == "sha256" ? 64 : algo == "sha1" ? 40 : algo == "md5" ? 32 : -1;
            if (want < 0 || hex.Length != want) return false;
            foreach (char c in hex)
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f' || c >= 'A' && c <= 'F')) return false;
            return true;
        }

        public static string Hash(string path, string algo, Func<bool> cancel)
        {
            HashAlgorithm h = algo == "sha1" ? (HashAlgorithm)SHA1.Create() : algo == "md5" ? (HashAlgorithm)MD5.Create() : SHA256.Create();
            try
            {
                using (h)
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1024 * 1024))
                {
                    byte[] buf = new byte[1024 * 1024];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (cancel != null && cancel()) return null;
                        h.TransformBlock(buf, 0, n, null, 0);
                    }
                    h.TransformFinalBlock(buf, 0, 0);
                    StringBuilder sb = new StringBuilder(h.Hash.Length * 2);
                    foreach (byte b in h.Hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return null;
            }
        }
    }

    // ------------------------------------------------------------------ //
    //  Mark-of-the-Web и антивирус
    // ------------------------------------------------------------------ //
    internal static class DlMotw
    {
        private static readonly Guid ClientGuid = new Guid("5B0E1D8C-2F53-4C1B-9E0A-6D2F7C4A1B30");

        [ComImport, Guid("73DB1241-1E85-4581-8E4F-A81E1D0F8C57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAttachmentExecute
        {
            [PreserveSig] int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string path);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            [PreserveSig] int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
            [PreserveSig] int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
            [PreserveSig] int CheckPolicy();
            [PreserveSig] int Prompt(IntPtr hwnd, int prompt, out int action);
            [PreserveSig] int Save();
        }

        // Без query и фрагмента — токены в метку не попадают.
        public static string StripQuery(string url)
        {
            if (string.IsNullOrEmpty(url)) return "";
            int q = url.IndexOfAny(new[] { '?', '#' });
            return q < 0 ? url : url.Substring(0, q);
        }

        // null — метка поставлена; иначе причина (файл при этом мог быть удалён антивирусом — проверяет вызывающий).
        public static string Apply(string path, string sourceUrl, string referrer)
        {
            string source = StripQuery(sourceUrl);
            string refer = StripQuery(referrer);
            string comError = null;
            int hr = 0;
            // COM вложений — однопоточный объект: вызываем в своём STA-потоке, а не в потоке загрузки (MTA).
            Thread t = new Thread(delegate()
            {
                object o = null;
                try
                {
                    o = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("4125DD96-E03A-4103-8F70-E0597D803B9C")));
                    IAttachmentExecute ae = (IAttachmentExecute)o;
                    Guid g = ClientGuid;
                    ae.SetClientGuid(ref g);
                    ae.SetLocalPath(path);
                    if (source.Length > 0) ae.SetSource(source);
                    if (refer.Length > 0) ae.SetReferrer(refer);
                    hr = ae.Save();
                }
                catch (Exception ex) { comError = ex.Message; }
                finally { if (o != null) Marshal.ReleaseComObject(o); }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            if (!t.Join(120000)) return Tr.S("служба вложений не ответила за 2 минуты", "the attachment service did not answer in 2 minutes");
            if (comError == null && hr >= 0) return null;
            if (comError == null && !DlFiles.Exists(path))
                return "0x" + hr.ToString("X8");
            // COM недоступен или отказал, а файл на месте — пишем поток вручную, как это делают загрузчики без COM.
            string why = WriteZoneStream(path, source, refer);
            return why == null ? null : (comError ?? "0x" + hr.ToString("X8")) + "; " + why;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

        // FileStream в .NET Framework не принимает путь «файл:поток» — открываем через CreateFileW.
        public static string WriteZoneStream(string path, string source, string referrer)
        {
            StringBuilder sb = new StringBuilder("[ZoneTransfer]\r\nZoneId=3\r\n");
            if (!string.IsNullOrEmpty(referrer)) sb.Append("ReferrerUrl=").Append(StripQuery(referrer)).Append("\r\n");
            if (!string.IsNullOrEmpty(source)) sb.Append("HostUrl=").Append(StripQuery(source)).Append("\r\n");
            try
            {
                using (SafeFileHandle h = CreateFileW(path + ":Zone.Identifier", 0x40000000 /* GENERIC_WRITE */, 0, IntPtr.Zero, 2 /* CREATE_ALWAYS */, 0x80, IntPtr.Zero))
                {
                    if (h.IsInvalid) return "CreateFileW " + Marshal.GetLastWin32Error();
                    using (FileStream fs = new FileStream(h, FileAccess.Write))
                    {
                        byte[] bytes = Encoding.ASCII.GetBytes(sb.ToString());
                        fs.Write(bytes, 0, bytes.Length);
                    }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        public static string ReadZoneStream(string path)
        {
            try
            {
                using (SafeFileHandle h = CreateFileW(path + ":Zone.Identifier", 0x80000000 /* GENERIC_READ */, 1, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero))
                {
                    if (h.IsInvalid) return null;
                    using (FileStream fs = new FileStream(h, FileAccess.Read))
                    using (StreamReader r = new StreamReader(fs, Encoding.ASCII))
                        return r.ReadToEnd();
                }
            }
            catch { return null; }
        }

        public static string DefenderPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe"); }
        }

        public static string DefenderArguments(string path) { return "-Scan -ScanType 3 -File \"" + path + "\""; }

        // По желанию пользователя: код 2 у MpCmdRun — найдена угроза. null — чисто или проверить нечем (это пишется в журнал).
        public static string DefenderScan(string path, Func<bool> cancel)
        {
            string exe = DefenderPath;
            if (!File.Exists(exe)) return null;
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, DefenderArguments(path));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return null;
                    Stopwatch w = Stopwatch.StartNew();
                    while (!p.WaitForExit(200))
                    {
                        if ((cancel != null && cancel()) || w.Elapsed.TotalMinutes > 10) { try { p.Kill(); } catch { } return null; }
                    }
                    return p.ExitCode == 2 ? Tr.S("Microsoft Defender нашёл угрозу в файле", "Microsoft Defender found a threat in the file") : null;
                }
            }
            catch (Exception ex) { DlLog.Report(ex); return null; }
        }
    }
}
