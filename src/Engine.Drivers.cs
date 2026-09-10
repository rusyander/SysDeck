// Windows Process Cleaner — старые пакеты драйверов (pnputil) и хранилище компонентов WinSxS (DISM)
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WindowsProcessCleaner
{
    public partial class Engine
    {
        // Windows хранит в DriverStore КАЖДУЮ когда-либо установленную версию драйвера
        // (у NVIDIA это 70 МБ – 2,7 ГБ на пакет). Кандидат на удаление = пакет, у которого
        // есть более новая версия с тем же исходным INF и поставщиком И который не привязан
        // ни к одному присутствующему устройству. Удаление — pnputil /delete-driver БЕЗ
        // /force: если система считает пакет нужным, pnputil откажет сам.
        private static readonly Regex _rxOemInf = new Regex(@"^oem\d+\.inf$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Дата у pnputil — «MM/dd/yyyy», но на всякий случай принимаем и «dd.MM.yyyy» / «yyyy-MM-dd»:
        // нераспознанная строка раньше давала всем пакетам версию «0», и «самой новой» становилась
        // случайная — под удаление мог попасть свежий пакет вместо старого.
        private static readonly Regex _rxDrvVersion = new Regex(@"^(\d{1,4}[./-]\d{1,2}[./-]\d{1,4})\s+(\d+(?:\.\d+)+)$", RegexOptions.Compiled);
        private static readonly string[] _drvDateFormats = new string[] {
            "MM/dd/yyyy", "M/d/yyyy", "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "yyyy-MM-dd", "yyyy-M-d", "dd-MM-yyyy", "d-M-yyyy" };
        private static readonly Regex _rxGuid = new Regex(@"^\{[0-9a-fA-F-]{36}\}$", RegexOptions.Compiled);

        private static Encoding OemEncoding()
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.Default; }
        }

        private static string PnpUtilPath() { return Path.Combine(Environment.SystemDirectory, "pnputil.exe"); }

        // pnputil /enum-drivers: блоки «Метка: значение», разделённые пустой строкой. Метки
        // локализованы, поэтому поля узнаём по ВИДУ значения (oemNN.inf, *.inf, {GUID},
        // «дата версия»), а поставщика и класс — по фиксированному месту за исходным именем.
        public List<DriverPackage> ListDriverPackages(out string error)
        {
            error = null;
            List<DriverPackage> all = new List<DriverPackage>();
            string so; int code;
            // Перечисление пакетов занимает секунды; полторы минуты ожидания означали только то,
            // что при зависшем pnputil строка «Пакеты драйверов» стояла в «…» полторы минуты.
            bool ran = RunCapture(PnpUtilPath(), "/enum-drivers", 30000, out so, out code, OemEncoding(),
                                  delegate { return _cancelDisk; });
            if (!ran || code != 0)
            {
                error = "pnputil: " + RunFailText(ran, code);
                return all;
            }

            List<List<string>> blocks = new List<List<string>>();
            List<string> cur = null;
            foreach (string raw in so.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { cur = null; continue; }
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                if (cur == null) { cur = new List<string>(); blocks.Add(cur); }
                cur.Add(line.Substring(colon + 1).Trim());
            }

            foreach (List<string> b in blocks)
            {
                DriverPackage d = new DriverPackage();
                int iOrig = -1;
                for (int i = 0; i < b.Count; i++)
                {
                    string v = b[i];
                    if (d.Published == null && _rxOemInf.IsMatch(v)) { d.Published = v; continue; }
                    if (d.Published != null && d.Original == null && v.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
                    { d.Original = v; iOrig = i; continue; }
                    Match m = _rxDrvVersion.Match(v);
                    if (m.Success && d.Version == null)
                    {
                        d.Version = m.Groups[2].Value;
                        DateTime dt;
                        if (DateTime.TryParseExact(m.Groups[1].Value, _drvDateFormats,
                                                   CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) d.Date = dt;
                    }
                }
                if (d.Published == null || d.Original == null) continue;
                if (iOrig + 1 < b.Count && !_rxGuid.IsMatch(b[iOrig + 1])) d.Provider = b[iOrig + 1];
                if (iOrig + 2 < b.Count && !_rxGuid.IsMatch(b[iOrig + 2])) d.ClassName = b[iOrig + 2];
                if (d.Version == null) { d.Version = "?"; d.VersionKnown = false; }
                all.Add(d);
            }
            return all;
        }

        // INF, которыми сейчас пользуются присутствующие устройства (DEVPKEY_Device_DriverInfPath).
        private static HashSet<string> InUseInfNames()
        {
            HashSet<string> set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr h = Native.SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, Native.DIGCF_PRESENT | Native.DIGCF_ALLCLASSES);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) throw new InvalidOperationException("SetupDiGetClassDevs");
            try
            {
                Native.SP_DEVINFO_DATA d = new Native.SP_DEVINFO_DATA();
                d.cbSize = (uint)Marshal.SizeOf(typeof(Native.SP_DEVINFO_DATA));
                Native.DEVPROPKEY key = Native.DEVPKEY_Device_DriverInfPath;
                byte[] buf = new byte[2048];
                for (uint i = 0; Native.SetupDiEnumDeviceInfo(h, i, ref d); i++)
                {
                    uint type, req;
                    if (!Native.SetupDiGetDevicePropertyW(h, ref d, ref key, out type, buf, (uint)buf.Length, out req, 0)) continue;
                    int len = (int)Math.Min(req, (uint)buf.Length);
                    string inf = Encoding.Unicode.GetString(buf, 0, len).TrimEnd('\0').Trim();
                    if (inf.Length > 0) set.Add(inf);
                }
            }
            finally { Native.SetupDiDestroyDeviceInfoList(h); }
            return set;
        }

        // oemNN.inf в %WinDir%\INF — байт в байт копия INF из папки пакета в FileRepository;
        // так пакет сопоставляется со своей папкой (а значит, и с размером).
        private string MatchRepoDir(DriverPackage d, HashSet<string> claimed)
        {
            try
            {
                string infCopy = Path.Combine(_winDir, "INF\\" + d.Published);
                if (!File.Exists(infCopy)) return null;
                byte[] want = File.ReadAllBytes(infCopy);
                string repo = Path.Combine(_winDir, "System32\\DriverStore\\FileRepository");
                foreach (string dir in Directory.GetDirectories(repo, d.Original + "_*"))
                {
                    if (claimed.Contains(dir)) continue;
                    string cand = Path.Combine(dir, d.Original);
                    FileInfo fi = new FileInfo(cand);
                    if (!fi.Exists || fi.Length != want.Length) continue;
                    byte[] have = File.ReadAllBytes(cand);
                    bool same = true;
                    for (int i = 0; i < want.Length; i++) if (want[i] != have[i]) { same = false; break; }
                    if (same) { claimed.Add(dir); return dir; }
                }
            }
            catch { }
            return null;
        }

        private static long DirSize(string dir)
        {
            long s = 0;
            try
            {
                foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
                { try { s += f.Length; } catch { } }
            }
            catch { }
            return s;
        }

        private static int CompareDriverNewestFirst(DriverPackage a, DriverPackage b)
        {
            List<long> ka = VersionKey(a.Version), kb = VersionKey(b.Version);
            int n = Math.Min(ka.Count, kb.Count);
            for (int i = 0; i < n; i++) if (ka[i] != kb[i]) return kb[i].CompareTo(ka[i]);
            if (ka.Count != kb.Count) return kb.Count.CompareTo(ka.Count);
            return b.Date.CompareTo(a.Date);
        }

        public List<DriverPackage> OldDriverPackages(out string error)
        {
            List<DriverPackage> all = ListDriverPackages(out error);
            List<DriverPackage> old = new List<DriverPackage>();
            if (all.Count == 0) return old;

            HashSet<string> inUse;
            try { inUse = InUseInfNames(); }
            catch (Exception ex) { error = "SetupAPI: " + ex.Message; return old; }
            foreach (DriverPackage d in all) d.InUse = inUse.Contains(d.Published);

            Dictionary<string, List<DriverPackage>> groups = new Dictionary<string, List<DriverPackage>>(StringComparer.OrdinalIgnoreCase);
            foreach (DriverPackage d in all)
            {
                string key = d.Original + "|" + (d.Provider ?? "");
                List<DriverPackage> g;
                if (!groups.TryGetValue(key, out g)) { g = new List<DriverPackage>(); groups[key] = g; }
                g.Add(d);
            }
            HashSet<string> claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (List<DriverPackage> g in groups.Values)
            {
                if (g.Count < 2) continue;
                // хоть у одного пакета версия не разобрана — порядок «новее/старее» неизвестен, группу пропускаем
                bool allKnown = true;
                foreach (DriverPackage d in g) if (!d.VersionKnown) { allKnown = false; break; }
                if (!allKnown) continue;
                g.Sort(CompareDriverNewestFirst);
                for (int i = 1; i < g.Count; i++)
                {
                    if (g[i].InUse) continue;
                    if (_cancelDisk) return old;
                    g[i].RepoDir = MatchRepoDir(g[i], claimed);
                    g[i].Size = g[i].RepoDir == null ? 0 : DirSize(g[i].RepoDir);
                    old.Add(g[i]);
                }
            }
            old.Sort(delegate(DriverPackage a, DriverPackage b) { return b.Size.CompareTo(a.Size); });
            return old;
        }

        private void AnalyzeDriverStore(CleanCategory c)
        {
            c.Progress = "pnputil…";
            string err;
            List<DriverPackage> old = OldDriverPackages(out err);
            c.Progress = null;
            c.Drivers = old;
            foreach (DriverPackage d in old) d.Enabled = !IsTargetOff(DriverKey(d));
            RecalcCategory(c);
            c.Analyzed = !_cancelDisk;
            c.Note = err;
            if (old.Count == 0) return;

            // сводка вида «nv_dispi.inf ×4 (NVIDIA), nvhda.inf ×1 (NVIDIA)»
            Dictionary<string, int> byInf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> prov = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            int unknownSize = 0;
            foreach (DriverPackage d in old)
            {
                int n;
                if (!byInf.TryGetValue(d.Original, out n)) { order.Add(d.Original); prov[d.Original] = d.Provider ?? ""; }
                byInf[d.Original] = n + 1;
                if (d.RepoDir == null) unknownSize++;
            }
            StringBuilder sb = new StringBuilder();
            sb.Append(Tr.S("пакетов: ", "packages: ")).Append(old.Count).Append(" · ");
            for (int i = 0; i < order.Count && i < 6; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(order[i]).Append(" ×").Append(byInf[order[i]]);
                string p = prov[order[i]];
                if (p.Length > 0) sb.Append(" (").Append(p.Length > 24 ? p.Substring(0, 24) + "…" : p).Append(")");
            }
            if (order.Count > 6) sb.Append(" …");
            if (unknownSize > 0) sb.Append(Tr.S(" · размер не определён: ", " · size unknown: ")).Append(unknownSize);
            c.Desc = sb.ToString();
        }

        private long DeleteDriverPackages(CleanCategory c, CleanResult res)
        {
            long freed = 0;
            if (c.Drivers == null) return 0;
            int i = 0;
            foreach (DriverPackage d in c.Drivers)
            {
                if (_cancelDisk) break;
                i++;
                if (!d.Enabled)
                {
                    res.Log.Add("SKIP (off)   DriverStore " + d.Published + "  " + d.Original + " " + d.Version);
                    continue;
                }
                c.Progress = Tr.S("пакет ", "package ") + i + "/" + c.Drivers.Count;
                DiskStatus = c.Title + " · " + c.Progress + " · " + d.Original + " " + d.Version;
                string so; int code;
                bool ran = RunCapture(PnpUtilPath(), "/delete-driver " + d.Published, 180000, out so, out code,
                                      OemEncoding(), delegate { return _cancelDisk; });
                // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED — пакет удалён, полный эффект после перезагрузки
                bool ok = ran && (code == 0 || code == 3010);
                if (ok) { freed += d.Size; res.FilesDeleted++; }
                else res.Errors++;
                res.Log.Add((ok ? FormatBytes(d.Size) : "ERR").PadLeft(10) + "  DriverStore " + d.Published
                            + "  " + d.Original + " " + (d.Provider ?? "") + " " + d.Version
                            + (ok ? "" : "  pnputil: " + RunFailText(ran, code)));
            }
            c.Progress = null;
            return freed;
        }

        // ================= WINSXS =================
        // Хранилище компонентов растёт с каждым обновлением: старые версии файлов остаются на
        // случай отката. Единственный поддерживаемый способ его ужать — DISM; /English даёт
        // стабильные метки, чтобы не разбирать локализованный вывод.
        private static string DismPath() { return Path.Combine(Environment.SystemDirectory, "Dism.exe"); }

        private static long ParseDismSize(string v)   // "470.20 MB", "6.90 GB", "0 bytes"
        {
            string[] p = v.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) return 0;
            double n;
            if (!double.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out n)
                && !double.TryParse(p[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out n)) return 0;
            string u = p[1].ToUpperInvariant();
            double mul = u.StartsWith("GB") ? 1L << 30 : u.StartsWith("MB") ? 1L << 20 : u.StartsWith("KB") ? 1024 : 1;
            return (long)(n * mul);
        }

        // Доля процентов из прогресс-бара DISM: «[====   42.0%   ====]».
        private static readonly Regex _rxDismPercent = new Regex(@"(\d{1,3}(?:[.,]\d+)?)\s*%", RegexOptions.Compiled);

        private static string DismStatus(string task, string line, long ms)
        {
            string pct = null;
            if (!string.IsNullOrEmpty(line))
            {
                Match m = _rxDismPercent.Match(line);
                if (m.Success) pct = m.Groups[1].Value.Replace(',', '.') + "%";
            }
            long sec = ms / 1000;
            return task + " " + (pct ?? "…") + "  " + (sec / 60) + ":" + (sec % 60).ToString("00");
        }

        // DISM своё отработал: дальше он либо молчит, либо спрашивает про перезагрузку.
        // Ждать после этой строки нечего — см. комментарий у RunCapture.
        private static bool DismFinishedLine(string line)
        {
            string s = line.Trim();
            return s.StartsWith("The operation completed successfully", StringComparison.OrdinalIgnoreCase)
                || s.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                || s.IndexOf("Restart Windows to complete", StringComparison.OrdinalIgnoreCase) >= 0
                || s.IndexOf("restart the computer now", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool DismSaidOk(string so)
        {
            return so != null && so.IndexOf("The operation completed successfully", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 3010 = ERROR_SUCCESS_REBOOT_REQUIRED: работа сделана, системе нужна перезагрузка.
        // Раньше он считался ошибкой, и разобранный отчёт выбрасывался.
        private static bool DismOk(bool ran, int code, string so)
        {
            return DismSaidOk(so) || (ran && (code == 0 || code == 3010));
        }

        private static bool DismRebootPending(string so)
        {
            return so != null && (so.IndexOf("Restart Windows to complete", StringComparison.OrdinalIgnoreCase) >= 0
                               || so.IndexOf("restart is required", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static bool DismBadOption(string so, int code)
        {
            return code == 87 || (so != null && so.IndexOf("Error: 87", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // /NoRestart гасит вопрос о перезагрузке в корне. Ключ глобальный, но если сборка DISM
        // его вдруг не примет (ошибка 87), повторяем без него: второй рубеж — закрытый stdin и
        // выход сразу после итоговой строки — от зависания защищает и без ключа.
        private bool RunDism(string verb, int timeoutMs, string task, Action<string> status,
                             out string so, out int code)
        {
            bool ran = RunDismOnce(verb + " /NoRestart", timeoutMs, task, status, out so, out code);
            if (DismBadOption(so, code)) ran = RunDismOnce(verb, timeoutMs, task, status, out so, out code);
            return ran;
        }

        private bool RunDismOnce(string args, int timeoutMs, string task, Action<string> status,
                                 out string so, out int code)
        {
            Action<string, long> prog = null;
            if (status != null)
            {
                status(DismStatus(task, null, 0));
                prog = delegate(string line, long ms) { status(DismStatus(task, line, ms)); };
            }
            return RunCapture(DismPath(), args, timeoutMs, out so, out code, null,
                              delegate { return _cancelDisk; }, prog, DismFinishedLine);
        }

        private void AnalyzeComponentStore(CleanCategory c)
        {
            c.Size = 0; c.FileCount = 0; c.Note = null;
            string so; int code;
            // Отчёт готовится за десятки секунд (в разобранном случае — 39 с). Двадцать минут
            // ожидания были не «на всякий случай», а ровно тем, что пользователь видел как зависание;
            // выход теперь и так происходит по итоговой строке отчёта, а это — последний рубеж.
            bool ran = RunDism("/Online /Cleanup-Image /AnalyzeComponentStore /English", 600000,
                               Tr.S("DISM считает", "DISM analyzing"),
                               delegate(string s) { c.Progress = s; }, out so, out code);
            c.Progress = null;
            c.Analyzed = !_cancelDisk;

            long store = 0, reclaim = 0; bool rec = false, got = false; string last = null; int pkgs = -1;
            foreach (string raw in (so ?? "").Split('\n'))
            {
                string line = raw.Trim();
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string label = line.Substring(0, colon).Trim(), val = line.Substring(colon + 1).Trim();
                if (label.StartsWith("Backups and Disabled Features", StringComparison.OrdinalIgnoreCase)) reclaim = ParseDismSize(val);
                else if (label.StartsWith("Actual Size of Component Store", StringComparison.OrdinalIgnoreCase)) { store = ParseDismSize(val); got = true; }
                else if (label.StartsWith("Component Store Cleanup Recommended", StringComparison.OrdinalIgnoreCase)) rec = val.StartsWith("Yes", StringComparison.OrdinalIgnoreCase);
                else if (label.StartsWith("Number of Reclaimable Packages", StringComparison.OrdinalIgnoreCase)) int.TryParse(val, out pkgs);
                else if (label.StartsWith("Date of Last Cleanup", StringComparison.OrdinalIgnoreCase)) last = val;
            }
            // Годность решает разобранный отчёт, а не код возврата: DISM отдаёт 3010, когда
            // системе нужна перезагрузка, и это не ошибка.
            if (!got)
            {
                c.Note = "DISM: " + (_cancelDisk ? Tr.S("остановлено", "cancelled")
                                   : ran && code == 0 ? Tr.S("отчёт не получен", "no report returned")
                                   : RunFailText(ran, code));
                return;
            }
            c.Size = reclaim;
            c.FileCount = pkgs > 0 ? pkgs : 0;
            c.Desc = Tr.S("хранилище: ", "store: ") + FormatBytes(store)
                   + Tr.S(", устаревшие компоненты: ", ", superseded components: ") + FormatBytes(reclaim)
                   + (rec ? Tr.S(" · DISM рекомендует очистку", " · DISM recommends cleanup") : "")
                   + (string.IsNullOrEmpty(last) ? "" : Tr.S(" · последняя очистка: ", " · last cleanup: ") + last)
                   + (DismRebootPending(so) ? Tr.S(" · системе нужна перезагрузка", " · the system needs a restart") : "")
                   + Tr.S(" — после очистки уже заменённые обновления нельзя откатить", " — superseded updates cannot be rolled back afterwards");
        }

        private long CleanComponentStore(CleanCategory c, CleanResult res)
        {
            string so; int code;
            // Сама очистка на большом WinSxS идёт минуты, редко — десятки минут; час ожидания
            // отличал «работает» от «встало» только по секундомеру пользователя. Прогресс DISM
            // виден в строке категории, «Стоп» опрашивается — получаса с запасом хватает.
            bool ran = RunDism("/Online /Cleanup-Image /StartComponentCleanup /English", 1800000,
                               Tr.S("DISM чистит", "DISM cleaning"),
                               delegate(string s) { c.Progress = s; DiskStatus = c.Title + " · " + s; },
                               out so, out code);
            c.Progress = null;
            bool ok = DismOk(ran, code, so);
            if (!ok) res.Errors++;
            res.Log.Add((ok ? FormatBytes(c.Size) : "ERR").PadLeft(10) + "  DISM /StartComponentCleanup"
                        + (ok ? (DismRebootPending(so) ? Tr.S("  (нужна перезагрузка)", "  (restart required)") : "")
                              : "  " + RunFailText(ran, code)));
            return ok ? c.Size : 0;
        }

        // ================= БАЗА ПРАВИЛ winapp2.ini =================
        // Формат, который используют FluentCleaner / BleachBit / CCleaner: тысячи
        // готовых правил "где у какого приложения лежит кэш". Подключается, только если
        // файл реально положен рядом с exe или в каталог данных — своей базы мы не везём.
        //
        // Сознательные ограничения (те же, что у FluentCleaner):
        //  - RegKey* игнорируются: чистка реестра не делается вообще;
        //  - секции с ExcludeKey* и Warning= пропускаются целиком — правило само
        //    сообщает, что там есть чего не трогать, и угадывать мы не будем.
        // Порядок поиска: сначала защищённая копия в %ProgramData% (её не может переписать
        // процесс без прав администратора), затем старое место в %APPDATA% и файл рядом с exe.
        // Оба последних доступны на запись обычному процессу, поэтому правила из них проходят
        // тот же строгий предохранитель IsRuleTargetAllowed — см. Engine.DiskWork.cs.
        public string Winapp2Path
        {
            get
            {
                string prot = Path.Combine(ProtectedRulesDir, "winapp2.ini");
                if (File.Exists(prot)) return prot;
                string local = LegacyWinapp2Path;
                if (File.Exists(local)) return local;
                try
                {
                    string beside = Path.Combine(
                        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "winapp2.ini");
                    if (File.Exists(beside)) return beside;
                }
                catch { }
                return null;
            }
        }
    }
}
