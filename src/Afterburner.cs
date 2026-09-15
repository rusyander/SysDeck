// SysDeck — профили разгона MSI Afterburner: список слотов и выбор профиля «сейчас и при запуске».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Слоты 1–5 Afterburner хранит в cfg каждой видеокарты (Profiles\VEN_…&DEV_…&BUS_…&DEV_…&FN_….cfg) секциями
// [Profile1]…[Profile5]; секция [Startup] — то, что он применяет при входе в Windows (пустая — ничего).
// Profiles\Profile1..4.cfg и MSIAfterburner.cfg — настройки мониторинга, а не разгона, их не трогаем.
// Выбор профиля = копия cfg в Profiles\WPC_Backups, [Startup] := [ProfileN] во всех cfg, где такой слот есть,
// и «MSIAfterburner.exe -ProfileN» (запущенный Afterburner применяет слот сразу). Исполняемый файл требует прав
// администратора, папка Profiles пользователю только на чтение, поэтому Apply выполняет процесс с правами: оверлей
// с правами (команда несёт лишь номер слота) или помощник --elevated-job. Путь к Afterburner — только из Program Files.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace SysDeck
{
    internal sealed class AbGpu
    {
        public string File;
        public string Name;                                      // «VEN_10DE DEV_2684»
        public readonly Dictionary<string, string>[] Slots = new Dictionary<string, string>[Afterburner.MaxSlot + 1];
        public Dictionary<string, string> Startup;
    }

    internal sealed class AbState
    {
        public string Exe;                                       // null — Afterburner не установлен
        public string Error;
        public readonly List<AbGpu> Gpus = new List<AbGpu>();
        public readonly List<int> UsedSlots = new List<int>();   // слоты, заполненные хотя бы у одной видеокарты
        public int StartupSlot;                                  // 0 — при запуске ничего; -1 — не совпадает ни с одним слотом

        // Описание слота по первой видеокарте, где он заполнен: «лимит мощности 85 % · ядро −502 МГц · память +800 МГц».
        public string Describe(int slot)
        {
            foreach (AbGpu g in Gpus)
            {
                Dictionary<string, string> s = g.Slots[slot];
                if (Afterburner.IsEmpty(s)) continue;
                return g.Name + ": " + Afterburner.Describe(s);
            }
            return "";
        }
    }

    internal static class Afterburner
    {
        public const int MaxSlot = 5;
        public const string BackupFolder = "WPC_Backups";
        private const int BackupsKept = 30;

        // ---------- где лежит ----------
        public static string Exe()
        {
            List<string> candidates = new List<string>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\Afterburner"))
                {
                    string icon = k == null ? null : k.GetValue("DisplayIcon") as string;
                    if (!string.IsNullOrEmpty(icon))
                    {
                        string dir = Path.GetDirectoryName(icon.Trim().Trim('"'));
                        if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "MSIAfterburner.exe"));
                    }
                }
            }
            catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"MSI Afterburner\MSIAfterburner.exe"));
            foreach (string c in candidates)
            {
                try
                {
                    if (Capture.HwinfoPaths.IsTrusted(c) && System.IO.File.Exists(c)) return Path.GetFullPath(c);
                }
                catch { }
            }
            return null;
        }

        public static string ProfilesDir(string exe) { return Path.Combine(Path.GetDirectoryName(exe), "Profiles"); }

        // cfg видеокарт; пустышка VEN_0000&DEV_0000 (так Afterburner пишет несуществующий адаптер) пропускается.
        public static string[] GpuFiles(string profilesDir)
        {
            List<string> list = new List<string>();
            if (!Directory.Exists(profilesDir)) return list.ToArray();
            foreach (string f in Directory.GetFiles(profilesDir, "VEN_*.cfg"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("VEN_0000&DEV_0000", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(f);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list.ToArray();
        }

        public static AbState Load()
        {
            AbState st = new AbState();
            try
            {
                st.Exe = Exe();
                if (st.Exe == null) return st;
                foreach (string f in GpuFiles(ProfilesDir(st.Exe)))
                {
                    string text = System.IO.File.ReadAllText(f, Encoding.Default);
                    AbGpu g = new AbGpu();
                    g.File = f;
                    g.Name = GpuName(Path.GetFileNameWithoutExtension(f));
                    for (int i = 1; i <= MaxSlot; i++) g.Slots[i] = Section(text, "Profile" + i.ToString(CultureInfo.InvariantCulture));
                    g.Startup = Section(text, "Startup");
                    st.Gpus.Add(g);
                }
                for (int i = 1; i <= MaxSlot; i++)
                    foreach (AbGpu g in st.Gpus)
                        if (!IsEmpty(g.Slots[i])) { st.UsedSlots.Add(i); break; }
                st.StartupSlot = StartupSlotOf(st.Gpus, st.UsedSlots);
            }
            catch (Exception ex) { st.Error = ex.Message; }
            return st;
        }

        // Слот, который сейчас стоит «при запуске»: у каждой видеокарты [Startup] равен [ProfileN]. Одинаковые слоты
        // (например, у RTX 4090 слоты 2 и 3 совпадают) — побеждает меньший номер.
        internal static int StartupSlotOf(List<AbGpu> gpus, List<int> used)
        {
            bool anyStartup = false;
            foreach (AbGpu g in gpus) if (!IsEmpty(g.Startup)) anyStartup = true;
            if (!anyStartup) return 0;
            foreach (int slot in used)
            {
                bool all = true;
                foreach (AbGpu g in gpus)
                    if (!SameValues(g.Startup, g.Slots[slot])) { all = false; break; }
                if (all) return slot;
            }
            return -1;
        }

        internal static string GpuName(string fileBase)
        {
            string[] parts = fileBase.Split('&');
            return parts.Length >= 2 ? parts[0] + " " + parts[1] : fileBase;
        }

        // ---------- разбор cfg ----------
        // null — секции нет. Ключи без учёта регистра, как читает их Afterburner (GetPrivateProfileString).
        internal static Dictionary<string, string> Section(string text, string name)
        {
            Dictionary<string, string> result = null;
            foreach (string raw in SplitLines(text))
            {
                string line = raw.Trim();
                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    if (result != null) break;
                    if (string.Equals(line.Substring(1, line.Length - 2), name, StringComparison.OrdinalIgnoreCase))
                        result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                if (result == null) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) result[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            return result;
        }

        // Пустой слот — все значения, кроме Format, пустые.
        internal static bool IsEmpty(Dictionary<string, string> section)
        {
            if (section == null) return true;
            foreach (KeyValuePair<string, string> kv in section)
                if (!string.Equals(kv.Key, "Format", StringComparison.OrdinalIgnoreCase) && kv.Value.Length > 0) return false;
            return true;
        }

        // Порядок ключей не важен (Afterburner пишет их в разном порядке), отсутствующий ключ равен пустому.
        internal static bool SameValues(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (IsEmpty(a) || IsEmpty(b)) return IsEmpty(a) && IsEmpty(b);
            return Covers(a, b) && Covers(b, a);
        }

        private static bool Covers(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            foreach (KeyValuePair<string, string> kv in a)
            {
                if (string.Equals(kv.Key, "Format", StringComparison.OrdinalIgnoreCase)) continue;
                string other;
                if (!b.TryGetValue(kv.Key, out other)) other = "";
                if (kv.Value != other) return false;
            }
            return true;
        }

        // Тело [Startup] заменяется строками [ProfileN] как есть; остальной файл не меняется ни на байт.
        // null — слота в файле нет (такую видеокарту не трогаем).
        internal static string CopyToStartup(string text, int slot)
        {
            string profile = "Profile" + slot.ToString(CultureInfo.InvariantCulture);
            List<string> lines = SplitLines(text);
            List<string> body = null;
            bool inProfile = false;
            foreach (string raw in lines)
            {
                string header = HeaderName(raw);
                if (header != null) { inProfile = string.Equals(header, profile, StringComparison.OrdinalIgnoreCase); if (inProfile) body = new List<string>(); continue; }
                if (inProfile && raw.Trim().Length > 0) body.Add(raw);
            }
            if (body == null) return null;

            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            StringBuilder sb = new StringBuilder();
            bool inStartup = false, written = false;
            foreach (string raw in lines)
            {
                string header = HeaderName(raw);
                if (header != null)
                {
                    inStartup = string.Equals(header, "Startup", StringComparison.OrdinalIgnoreCase);
                    Append(sb, raw, nl);
                    if (inStartup && !written) { foreach (string b in body) Append(sb, b, nl); written = true; }
                    continue;
                }
                if (!inStartup) Append(sb, raw, nl);
            }
            if (!written)
            {
                StringBuilder head = new StringBuilder();
                Append(head, "[Startup]", nl);
                foreach (string b in body) Append(head, b, nl);
                sb.Insert(0, head.ToString());
            }
            string result = sb.ToString();
            if (!text.EndsWith("\n", StringComparison.Ordinal) && result.EndsWith(nl, StringComparison.Ordinal))
                result = result.Substring(0, result.Length - nl.Length);
            return result;
        }

        private static void Append(StringBuilder sb, string line, string nl) { sb.Append(line).Append(nl); }

        private static string HeaderName(string raw)
        {
            string line = raw.Trim();
            if (line.Length >= 2 && line[0] == '[' && line[line.Length - 1] == ']') return line.Substring(1, line.Length - 2);
            return null;
        }

        private static List<string> SplitLines(string text)
        {
            List<string> lines = new List<string>(text.Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }

        // ---------- описание слота ----------
        internal static string Describe(Dictionary<string, string> s)
        {
            List<string> parts = new List<string>();
            string v;
            if (Has(s, "PowerLimit", out v)) parts.Add(Tr.S("лимит мощности ", "power limit ") + v + " %");
            if (Has(s, "ThermalLimit", out v)) parts.Add(Tr.S("лимит температуры ", "temp limit ") + v + " °C");
            if (Has(s, "CoreVoltageBoost", out v) && v != "0") parts.Add(Tr.S("напряжение ", "voltage ") + Signed(v, 1) + " %");
            if (Has(s, "CoreClkBoost", out v)) parts.Add(Tr.S("ядро ", "core ") + Signed(v, 1000) + Tr.S(" МГц", " MHz"));
            if (Has(s, "VFCurve", out v)) parts.Add(Tr.S("кривая V/F", "V/F curve"));
            if (Has(s, "MemClkBoost", out v)) parts.Add(Tr.S("память ", "memory ") + Signed(v, 1000) + Tr.S(" МГц", " MHz"));
            return string.Join(" · ", parts.ToArray());
        }

        private static bool Has(Dictionary<string, string> s, string key, out string value)
        {
            value = null;
            return s != null && s.TryGetValue(key, out value) && value.Length > 0;
        }

        // «-502000» кГц → «−502»; «800000» → «+800».
        internal static string Signed(string raw, int divisor)
        {
            long n;
            if (!long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out n)) return raw;
            long q = n / divisor;
            return (q > 0 ? "+" : q < 0 ? "−" : "") + Math.Abs(q).ToString(CultureInfo.InvariantCulture);
        }

        // ---------- выбор профиля (процесс с правами) ----------
        // Сначала слот применяется, затем пишется [Startup]: если Afterburner сам сохранит cfg при переключении,
        // он не затрёт нашу запись. Возвращает null или текст ошибки.
        public static string Apply(int slot)
        {
            if (slot < 1 || slot > MaxSlot) return "slot " + slot.ToString(CultureInfo.InvariantCulture);
            string exe = Exe();
            if (exe == null) return Tr.S("MSI Afterburner не найден в Program Files", "MSI Afterburner was not found in Program Files");

            string runError = RunProfileSwitch(exe, slot);
            if (runError != null) return runError;

            string dir = ProfilesDir(exe);
            foreach (string f in GpuFiles(dir))
            {
                string text = System.IO.File.ReadAllText(f, Encoding.Default);
                string next = CopyToStartup(text, slot);
                if (next == null || next == text) continue;
                Backup(dir, f);
                string tmp = f + ".wpc-tmp";
                System.IO.File.WriteAllText(tmp, next, Encoding.Default);
                System.IO.File.Replace(tmp, f, null);
            }
            return null;
        }

        private static string RunProfileSwitch(string exe, int slot)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(exe, "-Profile" + slot.ToString(CultureInfo.InvariantCulture));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                using (Process p = Process.Start(psi))
                {
                    // Запущенный Afterburner получает команду от второго экземпляра, и тот выходит. Если Afterburner не был
                    // запущен, этот экземпляр остаётся жить — это не ошибка.
                    if (p != null) p.WaitForExit(15000);
                }
                return null;
            }
            catch (Exception ex) { return "MSIAfterburner.exe: " + ex.Message; }
        }

        private static void Backup(string profilesDir, string file)
        {
            string dir = Path.Combine(profilesDir, BackupFolder);
            Directory.CreateDirectory(dir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            System.IO.File.Copy(file, Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + "_" + stamp + ".cfg"), true);
            string[] old = Directory.GetFiles(dir, "*.cfg");
            if (old.Length <= BackupsKept) return;
            Array.Sort(old, delegate(string a, string b) { return System.IO.File.GetCreationTimeUtc(a).CompareTo(System.IO.File.GetCreationTimeUtc(b)); });
            for (int i = 0; i < old.Length - BackupsKept; i++) try { System.IO.File.Delete(old[i]); } catch { }
        }
    }
}
