// SysDeck — «Загрузки»: браузеры — регистрация хоста native messaging, распаковка расширения, состояние.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Всё в HKCU и в папке данных, права администратора не нужны. Ключ хоста — только с нашим именем org.wpc.downloads:
// чужие хосты (FDM, WPS …) не читаются и не трогаются. Chrome, Yandex, Brave, Vivaldi читают ключ Chrome, Edge — свой
// (и ключ Chrome запасным путём), Firefox — ключ Mozilla. Путь к exe перепроверяется при каждом старте процесса загрузок и
// при открытии настроек: программа переносная, после переноса запись переписывается. Реальные ключи пишет только копия
// с папкой данных по умолчанию — портативная копия, тесты и снимки экрана их не касаются (имя хоста одно на всех).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace SysDeck.Downloads
{
    internal sealed class DlBrowserInfo
    {
        public string Key = "";            // chrome | edge | yandex | firefox
        public string Name = "";
        public string Exe = "";            // пусто — не установлен
        public bool Registered;
        public DateTime LastSeenUtc = DateTime.MinValue;
        public string ExtVersion = "";

        public bool Installed { get { return Exe.Length > 0; } }
        public string Family { get { return Key == "firefox" ? "firefox" : "chromium"; } }
    }

    internal static class DlBrowsers
    {
        public const string ResourcePrefix = "extension/";

        // Подраздел внутри Software и семейство манифеста.
        private static readonly string[][] HostKeys =
        {
            new[] { @"Google\Chrome\NativeMessagingHosts\" + DlBridge.HostName, "chromium" },
            new[] { @"Microsoft\Edge\NativeMessagingHosts\" + DlBridge.HostName, "chromium" },
            new[] { @"Mozilla\NativeMessagingHosts\" + DlBridge.HostName, "firefox" },
        };

        public static string ManifestDir { get { return Path.Combine(DlPaths.DataDir, "nmh"); } }

        public static string ExtensionDir(string family) { return Path.Combine(Path.Combine(DlPaths.DataDir, "extension"), family); }

        public static string ManifestFile(string manifestDir, string family)
        {
            return Path.Combine(manifestDir, DlBridge.HostName + "." + family + ".json");
        }

        public static string ManifestText(string family, string exe)
        {
            JVal o = JVal.NewObj();
            o.Set("name", DlJson.S(DlBridge.HostName));
            o.Set("description", DlJson.S("SysDeck downloads"));
            o.Set("path", DlJson.S(exe));
            o.Set("type", DlJson.S("stdio"));
            if (family == "firefox") o.Set("allowed_extensions", DlJson.Strings(DlBridge.GeckoIds));
            else
            {
                List<string> origins = new List<string>();
                foreach (string id in DlBridge.ChromiumIds) origins.Add("chrome-extension://" + id + "/");
                o.Set("allowed_origins", DlJson.Strings(origins));
            }
            return Jsn.Write(o);
        }

        // software — раздел, играющий роль HKCU\Software (тесты дают свой). null — записано (или уже было так), иначе причина.
        public static string Register(RegistryKey software, string manifestDir, string exe)
        {
            try
            {
                Directory.CreateDirectory(manifestDir);
                foreach (string family in new[] { "chromium", "firefox" })
                {
                    string file = ManifestFile(manifestDir, family);
                    string text = ManifestText(family, exe);
                    string current = null;
                    try { if (File.Exists(file)) current = File.ReadAllText(file, Encoding.UTF8); }
                    catch { }
                    if (current != text) DlPaths.WriteAtomic(file, text);
                }
                foreach (string[] hk in HostKeys)
                {
                    string file = ManifestFile(manifestDir, hk[1]);
                    using (RegistryKey key = software.CreateSubKey(hk[0]))
                    {
                        if (key == null) return "registry key not created: " + hk[0];
                        if (!string.Equals(key.GetValue("") as string, file, StringComparison.Ordinal)) key.SetValue("", file);
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return ex.Message;
            }
        }

        // Удаляет только разделы с нашим именем хоста и наши два манифеста.
        public static string Unregister(RegistryKey software, string manifestDir)
        {
            try
            {
                foreach (string[] hk in HostKeys)
                {
                    using (RegistryKey probe = software.OpenSubKey(hk[0]))
                        if (probe == null) continue;
                    software.DeleteSubKeyTree(hk[0], false);
                }
                foreach (string family in new[] { "chromium", "firefox" })
                {
                    string file = ManifestFile(manifestDir, family);
                    if (File.Exists(file)) File.Delete(file);
                }
                return null;
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return ex.Message;
            }
        }

        // Раздел указывает на наш манифест, а манифест — на этот exe.
        public static bool IsRegistered(RegistryKey software, string manifestDir, string exe, string family, string subKey)
        {
            try
            {
                string file = ManifestFile(manifestDir, family);
                using (RegistryKey key = software.OpenSubKey(subKey))
                {
                    if (key == null || !string.Equals(key.GetValue("") as string, file, StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (!File.Exists(file)) return false;
                JVal m = Jsn.Parse(File.ReadAllText(file, Encoding.UTF8));
                return string.Equals(DlJson.Str(m, "path", ""), exe, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static bool IsDefaultCopy { get { return DlIpc.Channel.Length == 0; } }

        // По настройке: интеграция включена — ключи на месте и указывают на этот exe; выключена — наших ключей нет.
        public static string EnsureDefault(DlSettings s)
        {
            if (!IsDefaultCopy) return "a copy with its own data folder does not register browsers";
            try
            {
                using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software", true))
                {
                    if (software == null) return "HKCU\\Software is not writable";
                    return s != null && s.BrowserIntegration
                        ? Register(software, ManifestDir, DlPaths.ExecutablePath)
                        : Unregister(software, ManifestDir);
                }
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return ex.Message;
            }
        }

        // ---------- состояние по браузерам ----------
        public static List<DlBrowserInfo> Status()
        {
            List<DlBrowserInfo> list = new List<DlBrowserInfo>();
            list.Add(Info("chrome", "Google Chrome", AppPath("chrome.exe", null), 0));
            list.Add(Info("edge", "Microsoft Edge", AppPath("msedge.exe", null), 1));
            list.Add(Info("yandex", Tr.S("Яндекс Браузер", "Yandex Browser"), AppPath("browser.exe", "yandex"), 0));
            list.Add(Info("firefox", "Mozilla Firefox", AppPath("firefox.exe", null), 2));
            return list;
        }

        private static DlBrowserInfo Info(string key, string name, string exe, int hostKey)
        {
            DlBrowserInfo b = new DlBrowserInfo();
            b.Key = key;
            b.Name = name;
            b.Exe = exe ?? "";
            if (IsDefaultCopy)
            {
                try
                {
                    using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software"))
                        b.Registered = software != null && IsRegistered(software, ManifestDir, DlPaths.ExecutablePath, HostKeys[hostKey][1], HostKeys[hostKey][0]);
                }
                catch { }
            }
            string ver;
            b.LastSeenUtc = DlBridgeSeen.LastSeen(key, out ver);
            b.ExtVersion = ver;
            return b;
        }

        // Путь из App Paths (HKCU, затем HKLM); mustContain — отсечь чужой exe с тем же именем (browser.exe).
        private static string AppPath(string exeName, string mustContain)
        {
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (RegistryKey k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exeName))
                    {
                        string path = k == null ? null : (k.GetValue("") as string ?? "").Trim('"', ' ');
                        if (string.IsNullOrEmpty(path) || !File.Exists(path)) continue;
                        if (mustContain != null && path.IndexOf(mustContain, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        return path;
                    }
                }
                catch { }
            }
            return null;
        }

        public static string ExtensionsPage(string key)
        {
            switch (key)
            {
                case "edge": return "edge://extensions/";
                case "yandex": return "browser://extensions/";
                case "firefox": return "about:debugging#/runtime/this-firefox";
                default: return "chrome://extensions/";
            }
        }

        // Страница расширений в самом браузере. Браузер может не открыть внутренний адрес из командной строки — тогда
        // человеку остаются шаги на странице настроек и путь к папке.
        public static string OpenExtensionsPage(DlBrowserInfo b)
        {
            if (b == null || !b.Installed) return Tr.S("браузер не найден", "the browser is not found");
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(b.Exe, ExtensionsPage(b.Key));
                psi.UseShellExecute = false;
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        // ---------- расширение, вшитое в exe ----------
        public static List<string> ResourceNames()
        {
            List<string> names = new List<string>();
            foreach (string n in Assembly.GetExecutingAssembly().GetManifestResourceNames())
                if (n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.IndexOf("/test/", StringComparison.Ordinal) < 0) names.Add(n);
            names.Sort(StringComparer.Ordinal);
            return names;
        }

        // Относительный путь файла расширения для семейства; null — файл этому семейству не нужен.
        internal static string TargetOf(string resource, string family)
        {
            string rel = resource.Substring(ResourcePrefix.Length);
            if (rel == "manifest." + family + ".json") return "manifest.json";
            if (rel.StartsWith("manifest.", StringComparison.Ordinal)) return null;
            if (!rel.StartsWith("src/", StringComparison.Ordinal)) return null;
            rel = rel.Substring(4);
            if (rel.Length == 0 || rel.IndexOf("..", StringComparison.Ordinal) >= 0 || rel.IndexOf(':') >= 0) return null;
            return rel.Replace('/', '\\');
        }

        // Папка для «Загрузить распакованное». Файл переписывается, только если отличается: браузер следит за папкой.
        public static string Unpack(string family, string targetDir, out int files)
        {
            files = 0;
            List<string> names = ResourceNames();
            if (names.Count == 0) return Tr.S("расширение не вшито в эту сборку", "the extension is not embedded in this build");
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                foreach (string n in names)
                {
                    string rel = TargetOf(n, family);
                    if (rel == null) continue;
                    string path = NestedInside(targetDir, rel);
                    if (path == null) continue;
                    byte[] bytes;
                    using (Stream s = asm.GetManifestResourceStream(n))
                    using (MemoryStream ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    bool same = false;
                    try { same = File.Exists(path) && BytesEqual(File.ReadAllBytes(path), bytes); }
                    catch { }
                    if (!same) File.WriteAllBytes(path, bytes);
                    files++;
                }
                return files > 0 && File.Exists(Path.Combine(targetDir, "manifest.json")) ? null : Tr.S("в сборке нет манифеста расширения", "the build has no extension manifest");
            }
            catch (Exception ex)
            {
                DlLog.Report(ex);
                return ex.Message;
            }
        }

        // Путь с подпапками (_locales\ru\messages.json) внутри папки или null: каждая часть — чистое имя, без выхода наружу.
        internal static string NestedInside(string folder, string rel)
        {
            string[] parts = rel.Split('\\');
            foreach (string part in parts)
                if (part.Length == 0 || DlFiles.SanitizeName(part) != part) return null;
            try
            {
                string root = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
                string full = Path.GetFullPath(Path.Combine(root, rel));
                return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && full.IndexOf(':', 2) < 0 ? full : null;
            }
            catch { return null; }
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
