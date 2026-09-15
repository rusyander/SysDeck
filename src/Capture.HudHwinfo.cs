// SysDeck — оверлей: пути, INI и фоновый запуск HWiNFO.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace SysDeck.Capture
{
    // ------------------------------------------------------------------ //
    //  HWiNFO в фоне
    // ------------------------------------------------------------------ //
    internal static class HwinfoPaths
    {
        // Только из Program Files: туда пишет лишь администратор. Путь из реестра установщика или стандартный.
        public static string Exe()
        {
            List<string> candidates = new List<string>();
            try
            {
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\HWiNFO64_is1"))
                {
                    string dir = k == null ? null : k.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrEmpty(dir)) candidates.Add(Path.Combine(dir, "HWiNFO64.EXE"));
                }
            }
            catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"HWiNFO64\HWiNFO64.EXE"));
            foreach (string c in candidates)
                if (IsTrusted(c) && File.Exists(c)) return Path.GetFullPath(c);
            return null;
        }

        public static bool IsTrusted(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return false; }
            foreach (Environment.SpecialFolder f in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
            {
                string root = Environment.GetFolderPath(f);
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }

    // Настройки HWiNFO на время фоновой работы: только датчики, свёрнуто, без приветствия, общая память включена.
    // Прежние значения запоминаются рядом с INI (Program Files) и возвращаются, когда фоновый HWiNFO закрыт: иначе
    // «только датчики» без приветствия навсегда закрыл бы человеку главное окно HWiNFO.
    internal static class HwinfoIni
    {
        public static readonly string[][] Wanted =
        {
            new[] { "SensorsOnly", "1" }, new[] { "OpenSensors", "1" }, new[] { "OpenSystemSummary", "0" },
            new[] { "MinimalizeMainWnd", "1" }, new[] { "MinimalizeSensors", "1" }, new[] { "MinimalizeSensorsClose", "1" },
            new[] { "ShowWelcomeAndProgress", "0" }, new[] { "SensorsSM", "1" }, new[] { "AutoUpdate", "0" }
        };

        private const string Absent = "absent";

        // text — INI целиком; originals получает прежние значения затронутых ключей (Absent — ключа не было).
        public static string Patch(string text, Dictionary<string, string> originals)
        {
            Dictionary<string, string> set = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string[] kv in Wanted) set[kv[0]] = kv[1];
            return Apply(text, set, originals);
        }

        public static string Restore(string text, Dictionary<string, string> originals)
        {
            return Apply(text, originals, null);
        }

        private static string Apply(string text, Dictionary<string, string> values, Dictionary<string, string> previous)
        {
            List<string> lines = new List<string>((text ?? "").Replace("\r\n", "\n").Split('\n'));
            if (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
            int section = -1, end = lines.Count;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (!t.StartsWith("[")) continue;
                if (section >= 0) { end = i; break; }
                if (string.Equals(t, "[Settings]", StringComparison.OrdinalIgnoreCase)) section = i;
            }
            if (section < 0) { lines.Add("[Settings]"); section = lines.Count - 1; end = lines.Count; }
            HashSet<string> done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = end - 1; i > section; i--)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                string key = lines[i].Substring(0, eq).Trim();
                string value;
                if (!values.TryGetValue(key, out value)) continue;
                if (previous != null && !previous.ContainsKey(key)) previous[key] = lines[i].Substring(eq + 1);
                done.Add(key);
                if (value == Absent) { lines.RemoveAt(i); end--; }
                else lines[i] = key + "=" + value;
            }
            foreach (KeyValuePair<string, string> kv in values)
            {
                if (done.Contains(kv.Key)) continue;
                if (previous != null && !previous.ContainsKey(kv.Key)) previous[kv.Key] = Absent;
                if (kv.Value == Absent) continue;
                lines.Insert(end, kv.Key + "=" + kv.Value);
                end++;
            }
            return string.Join("\r\n", lines.ToArray()) + "\r\n";
        }

        public static string IniPath(string exe) { return Path.Combine(Path.GetDirectoryName(exe), "HWiNFO64.INI"); }
        public static string BackupPath(string exe) { return IniPath(exe) + ".wpc-original"; }

        public static string Serialize(Dictionary<string, string> originals)
        {
            StringBuilder sb = new StringBuilder();
            foreach (KeyValuePair<string, string> kv in originals) sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
            return sb.ToString();
        }

        public static Dictionary<string, string> Deserialize(string text)
        {
            Dictionary<string, string> d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            return d;
        }

        // Прошлый сеанс оборвался (сбой, выключение) и не вернул настройки — вернуть, если HWiNFO сейчас не работает.
        public static void RecoverAfterCrash()
        {
            try
            {
                string exe = HwinfoPaths.Exe();
                if (exe == null || !File.Exists(BackupPath(exe)) || !Elevation.IsElevated) return;
                if (Process.GetProcessesByName("HWiNFO64").Length > 0) return;
                RestoreFile(exe);
            }
            catch (Exception ex) { CapLog.Report(ex); }
        }

        public static void PatchFile(string exe)
        {
            string ini = IniPath(exe), backup = BackupPath(exe);
            string text = File.Exists(ini) ? File.ReadAllText(ini, Encoding.Default) : "";
            Dictionary<string, string> originals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string patched = Patch(text, originals);
            if (!File.Exists(backup)) File.WriteAllText(backup, Serialize(originals), Encoding.UTF8);
            File.WriteAllText(ini, patched, Encoding.Default);
        }

        public static void RestoreFile(string exe)
        {
            string ini = IniPath(exe), backup = BackupPath(exe);
            if (!File.Exists(backup)) return;
            Dictionary<string, string> originals = Deserialize(File.ReadAllText(backup, Encoding.UTF8));
            string text = File.Exists(ini) ? File.ReadAllText(ini, Encoding.Default) : "";
            File.WriteAllText(ini, Restore(text, originals), Encoding.Default);
            File.Delete(backup);
        }
    }

    internal sealed class HwinfoHost : IDisposable
    {
        // off — не нужен; missing — не установлен; needs-admin — оверлей без прав; external — запущен человеком, данные
        // идут; external-no-sm — запущен человеком без общей памяти; starting — поднимается; ours — наш, данные идут;
        // failed — не запустился.
        public string State = "off";
        public string Note = "";

        private Process _ours;
        private string _oursExe;
        private DateTime _startedUtc;
        private readonly List<DateTime> _starts = new List<DateTime>();

        // Бесплатный HWiNFO выключает общую память через 12 часов — перезапуск чуть раньше.
        private static readonly TimeSpan RestartAfter = TimeSpan.FromHours(11.5);

        public void Tick(bool wanted)
        {
            if (_ours != null && _ours.HasExited) { Cleanup(); }
            if (!wanted) { Stop(); State = "off"; Note = ""; return; }

            bool live = HudHwinfo.Live();
            int ourPid = _ours != null ? _ours.Id : -1;
            bool external = false;
            foreach (Process p in Process.GetProcessesByName("HWiNFO64"))
            {
                if (p.Id != ourPid) external = true;
                p.Dispose();
            }
            if (external)
            {
                State = live ? "external" : "external-no-sm";
                Note = "";
                return;
            }
            if (_ours != null)
            {
                TimeSpan age = DateTime.UtcNow - _startedUtc;
                if (live) { State = "ours"; Note = ""; }
                else if (age.TotalSeconds < 45) State = "starting";
                else State = "ours-no-sm";
                if (age > RestartAfter || (!live && age.TotalMinutes > 2 && CanStart())) { Stop(); StartOurs(); }
                return;
            }
            if (live) { State = "external"; return; }
            string exe = HwinfoPaths.Exe();
            if (exe == null) { State = "missing"; Note = ""; return; }
            if (!Elevation.IsElevated) { State = "needs-admin"; Note = ""; return; }
            if (!CanStart()) { State = "failed"; return; }
            StartOurs();
        }

        // Не больше трёх запусков за десять минут: HWiNFO, который человек сам закрыл из трея, не воскресает бесконечно.
        private bool CanStart()
        {
            _starts.RemoveAll(delegate(DateTime t) { return (DateTime.UtcNow - t).TotalMinutes > 10; });
            return _starts.Count < 3;
        }

        private void StartOurs()
        {
            string exe = HwinfoPaths.Exe();
            if (exe == null) { State = "missing"; return; }
            try
            {
                HwinfoIni.PatchFile(exe);
                ProcessStartInfo psi = new ProcessStartInfo(exe);
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Minimized;
                psi.WorkingDirectory = Path.GetDirectoryName(exe);
                _ours = Process.Start(psi);
                _oursExe = exe;
                _startedUtc = DateTime.UtcNow;
                _starts.Add(_startedUtc);
                State = "starting";
                Note = "";
                CapLog.Write("hwinfo started in background, pid " + (_ours == null ? "?" : _ours.Id.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception ex)
            {
                CapLog.Report(ex);
                State = "failed";
                Note = ex.Message;
                try { HwinfoIni.RestoreFile(exe); } catch { }
            }
        }

        private void Stop()
        {
            if (_ours == null) return;
            try
            {
                if (!_ours.HasExited)
                {
                    _ours.CloseMainWindow();
                    if (!_ours.WaitForExit(4000)) { _ours.Kill(); _ours.WaitForExit(4000); }
                }
            }
            catch (Exception ex) { CapLog.Report(ex); }
            Cleanup();
        }

        private void Cleanup()
        {
            string exe = _oursExe;
            if (_ours != null) { _ours.Dispose(); _ours = null; }
            _oursExe = null;
            // HWiNFO переписывает INI при выходе — возврат прежних значений после него.
            if (exe != null) { try { HwinfoIni.RestoreFile(exe); } catch (Exception ex) { CapLog.Report(ex); } }
        }

        public void Dispose() { Stop(); }
    }
}
