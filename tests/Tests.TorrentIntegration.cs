// Windows Process Cleaner — область «torrent», обвязка клиента: правило брандмауэра для входящих, привязка .torrent и
// magnet:, передача ссылок движку загрузок.
//
// Брандмауэр здесь только читается (COM HNetCfg.FwPolicy2, права не нужны): добавить или удалить правило может лишь
// помощник с правами, а тесты идут без них и без окна UAC. Проверяется то, что решает безопасность: netsh получает
// фильтр program= и не может получить лишний аргумент через кавычку в пути.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using WindowsProcessCleaner.Downloads;

namespace WindowsProcessCleaner.Tests
{
    internal static partial class TorrentTests
    {
        static partial void RunIntegration()
        {
            FirewallCases();
            AssocCases();
            OpenArgumentCases();
            MagnetTextCases();
            SettingsCases();
        }

        private static void SettingsCases()
        {
            DlSettings s = new DlSettings();
            T.Check("bt settings: inbound closed and port unset by default", !s.BtInbound && s.BtPort == 0 && s.BtEncryption == BtEncryption.Prefer && s.BtMaxActive == 3);
            s.BtPort = 51413; s.BtInbound = true; s.BtPortMapping = false; s.BtEncryption = BtEncryption.Require; s.BtDht = false; s.BtPex = false;
            s.BtLsd = false; s.BtMaxActive = 5; s.BtUpKBps = 700; s.BtMaxConnections = 300; s.BtMaxPerTorrent = 60; s.BtUploadSlots = 6; s.BtSeed = false;
            s.BtRatioPercent = 150; s.BtSeedMinutes = 90; s.BtRecycleTorrentFile = true; s.BtWatchFolder = @"D:\Torrents\in";
            string file = Path.Combine(Fx.MakeDir(Fx.Root, "bt-settings"), "settings.json");
            s.Save(file);
            DlSettings r = DlSettings.Load(file);
            T.Check("bt settings: every torrent field survives save and load", r.BtPort == 51413 && r.BtInbound && !r.BtPortMapping && r.BtEncryption == BtEncryption.Require
                    && !r.BtDht && !r.BtPex && !r.BtLsd && r.BtMaxActive == 5 && r.BtUpKBps == 700 && r.BtMaxConnections == 300 && r.BtMaxPerTorrent == 60
                    && r.BtUploadSlots == 6 && !r.BtSeed && r.BtRatioPercent == 150 && r.BtSeedMinutes == 90 && r.BtRecycleTorrentFile && r.BtWatchFolder == @"D:\Torrents\in");
            File.WriteAllText(file, "{\"BtPort\": 80, \"BtMaxActive\": 999, \"BtEncryption\": \"Garbage\", \"BtUploadSlots\": -5, \"BtRatioPercent\": -1}");
            DlSettings h = DlSettings.Load(file);
            T.Check("bt settings: hostile values fall back or clamp", h.BtPort == 0 && h.BtMaxActive == 20 && h.BtEncryption == BtEncryption.Prefer && h.BtUploadSlots == 1 && h.BtRatioPercent == 0,
                    h.BtPort + " " + h.BtMaxActive + " " + h.BtEncryption + " " + h.BtUploadSlots + " " + h.BtRatioPercent);
        }

        // ---------- привязка .torrent и magnet: ----------
        private static void AssocCases()
        {
            string realTorrent = RealDefault(@"Software\Classes\.torrent");
            string realMagnet = RealDefault(@"Software\Classes\magnet\shell\open\command");
            string sub = @"Software\WPC-Tests\" + System.Diagnostics.Process.GetCurrentProcess().Id + "-assoc";
            string exe = @"C:\Apps\WPC\WindowsProcessCleaner.exe";
            string other = @"D:\Portable\WPC\WindowsProcessCleaner.exe";
            try
            {
                using (RegistryKey sw = Registry.CurrentUser.CreateSubKey(sub))
                {
                    // Чистый профиль: включить → своё значение, выключить → ни следа.
                    T.Eq("assoc: .torrent enabled on a clean profile", BtAssocState.On, BtAssoc.EnableTorrent(sw, exe));
                    T.Eq("assoc: .torrent default is our ProgID", BtAssoc.TorrentProgId, Def(sw, @"Classes\.torrent"));
                    T.Eq("assoc: open command passes the file to /torrent", "\"" + exe + "\" /torrent \"%1\"", Def(sw, @"Classes\" + BtAssoc.TorrentProgId + @"\shell\open\command"));
                    T.Eq("assoc: registered for Default apps", @"Software\WindowsProcessCleaner\Capabilities", Val(sw, "RegisteredApplications", BtAssoc.AppName));
                    T.Eq("assoc: another copy of the app is not reported as on", BtAssocState.Off, BtAssoc.TorrentState(sw, other));
                    T.Eq("assoc: magnet enabled on a clean profile", BtAssocState.On, BtAssoc.EnableMagnet(sw, exe));
                    T.Check("assoc: magnet key is a URL protocol", Val(sw, @"Classes\magnet", "URL Protocol") == "");
                    BtAssoc.DisableTorrent(sw, exe);
                    T.Eq("assoc: .torrent off", BtAssocState.Off, BtAssoc.TorrentState(sw, exe));
                    T.Check("assoc: magnet stays on when only .torrent is off", BtAssoc.MagnetState(sw, exe) == BtAssocState.On
                            && Val(sw, @"WindowsProcessCleaner\Capabilities\URLAssociations", "magnet") == BtAssoc.MagnetProgId
                            && Val(sw, @"WindowsProcessCleaner\Capabilities\FileAssociations", ".torrent") == null);
                    BtAssoc.DisableMagnet(sw, exe);
                    T.Check("assoc: both off leave no keys of ours", sw.OpenSubKey("WindowsProcessCleaner") == null && sw.OpenSubKey(@"Classes\magnet") == null
                            && sw.OpenSubKey(@"Classes\" + BtAssoc.TorrentProgId) == null && Def(sw, @"Classes\.torrent") == null
                            && Val(sw, "RegisteredApplications", BtAssoc.AppName) == null && Val(sw, @"Classes\.torrent\OpenWithProgids", BtAssoc.TorrentProgId) == null);

                    // Чужой обработчик: запоминается и возвращается как был.
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\.torrent")) k.SetValue("", "qBittorrent.File.Torrent");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\magnet")) { k.SetValue("", "URL:Magnet link"); k.SetValue("URL Protocol", ""); }
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\magnet\shell\open\command")) k.SetValue("", "\"C:\\qbt\\qbittorrent.exe\" \"%1\"");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\magnet\DefaultIcon")) k.SetValue("", "\"C:\\qbt\\qbittorrent.exe\",1");
                    BtAssoc.EnableTorrent(sw, exe);
                    BtAssoc.EnableMagnet(sw, exe);
                    BtAssoc.EnableMagnet(sw, exe);   // второй раз не должен запомнить себя вместо чужого
                    T.Check("assoc: foreign handlers replaced while on", BtAssoc.TorrentState(sw, exe) == BtAssocState.On && BtAssoc.MagnetState(sw, exe) == BtAssocState.On);
                    BtAssoc.DisableTorrent(sw, exe);
                    BtAssoc.DisableMagnet(sw, exe);
                    T.Eq("assoc: previous .torrent handler restored", "qBittorrent.File.Torrent", Def(sw, @"Classes\.torrent"));
                    T.Eq("assoc: previous magnet command restored", "\"C:\\qbt\\qbittorrent.exe\" \"%1\"", Def(sw, @"Classes\magnet\shell\open\command"));
                    T.Eq("assoc: previous magnet icon restored", "\"C:\\qbt\\qbittorrent.exe\",1", Def(sw, @"Classes\magnet\DefaultIcon"));

                    // UserChoice другого приложения Windows не даёт перебить — нужна страница настроек.
                    using (RegistryKey k = sw.CreateSubKey(@"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice")) k.SetValue("ProgId", "qBittorrent.File.Torrent");
                    T.Eq("assoc: foreign UserChoice means the settings page", BtAssocState.NeedsSettings, BtAssoc.EnableTorrent(sw, exe));
                    T.Eq("assoc: UserChoice itself is never written", "qBittorrent.File.Torrent", Val(sw, @"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice", "ProgId"));
                    using (RegistryKey k = sw.CreateSubKey(@"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice")) k.SetValue("ProgId", BtAssoc.TorrentProgId);
                    T.Eq("assoc: UserChoice pointing at us is on", BtAssocState.On, BtAssoc.TorrentState(sw, exe));
                    BtAssoc.DisableTorrent(sw, exe);
                }
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software\WPC-Tests", true))
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0) { parent.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\WPC-Tests", false); }
            }
            T.Check("assoc: the real .torrent and magnet handlers are untouched", RealDefault(@"Software\Classes\.torrent") == realTorrent
                    && RealDefault(@"Software\Classes\magnet\shell\open\command") == realMagnet);
        }

        private static void OpenArgumentCases()
        {
            string dir = Fx.MakeDir(Fx.Root, "bt-open");
            string good = Path.Combine(dir, "debian.torrent");
            File.WriteAllBytes(good, BtFx.Build("x", new[] { new BtFxFile(BtFx.Data(40000, 1)) }, 16384, 1, false, null));
            string empty = Path.Combine(dir, "empty.torrent");
            File.WriteAllBytes(empty, new byte[0]);
            string text = Path.Combine(dir, "readme.txt");
            File.WriteAllText(text, "x");
            T.Eq("open arg: magnet link accepted", "magnet:?xt=urn:btih:0123", BtAssoc.ValidateOpenArgument(" magnet:?xt=urn:btih:0123 "));
            T.Eq("open arg: existing .torrent accepted", good, BtAssoc.ValidateOpenArgument(good));
            T.Check("open arg: relative, missing, empty, other extension and other schemes refused",
                    BtAssoc.ValidateOpenArgument("debian.torrent") == null && BtAssoc.ValidateOpenArgument(Path.Combine(dir, "missing.torrent")) == null
                    && BtAssoc.ValidateOpenArgument(empty) == null && BtAssoc.ValidateOpenArgument(text) == null
                    && BtAssoc.ValidateOpenArgument("http://example.org/a.torrent") == null && BtAssoc.ValidateOpenArgument("magnet:evil") == null
                    && BtAssoc.ValidateOpenArgument("C:\\bad|name.torrent") == null && BtAssoc.ValidateOpenArgument(null) == null);

            // Командная строка exe: /torrent <путь или magnet> в любом месте, голая magnet-ссылка — только первым аргументом.
            MethodInfo arg = typeof(WindowsProcessCleaner.Program).GetMethod("TorrentArgument", BindingFlags.NonPublic | BindingFlags.Static);
            Func<string[], string> parse = delegate(string[] a) { return (string)arg.Invoke(null, new object[] { a }); };
            string magnet = "magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567";
            T.Check("command line: /torrent with a file or a magnet, and a bare magnet first, are taken",
                    parse(new[] { "/tray", BtAssoc.OpenSwitch, good }) == good && parse(new[] { BtAssoc.OpenSwitch.ToUpperInvariant(), magnet }) == magnet
                    && parse(new[] { magnet }) == magnet);
            T.Check("command line: no value after /torrent, a bare magnet not first, a bare path and a bad file give nothing",
                    parse(new[] { BtAssoc.OpenSwitch }) == null && parse(new[] { "/tray", magnet }) == null && parse(new[] { good }) == null
                    && parse(new[] { BtAssoc.OpenSwitch, text }) == null && parse(new string[0]) == null && parse(null) == null);
        }

        private static void MagnetTextCases()
        {
            const string A = "0123456789abcdef0123456789abcdef01234567";
            const string B = "89abcdef0123456789abcdef0123456789abcdef";
            string text = "первая: <magnet:?xt=urn:btih:" + A + "&dn=one>, та же с трекером \"magnet:?xt=urn:btih:" + A.ToUpperInvariant()
                          + "&tr=udp%3A%2F%2Ft.example%3A80\"\r\nвторая magnet:?xt=urn:btih:" + B + "); битая magnet:?xt=urn:btih:zz и http://example.org/x.torrent";
            List<string> ok = new List<string>();
            DlView.ParseMagnets(text, ok);
            T.Check("magnets in text: two distinct hashes in order, the same hash with another tracker once, trailing punctuation cut, broken and http skipped",
                    ok.Count == 2 && ok[0] == "magnet:?xt=urn:btih:" + A + "&dn=one" && ok[1] == "magnet:?xt=urn:btih:" + B, string.Join(" | ", ok.ToArray()));
            List<string> none = new List<string>();
            DlView.ParseMagnets(null, none);
            T.Check("magnets in text: null text gives nothing", none.Count == 0);
        }

        private static string Def(RegistryKey sw, string path) { return Val(sw, path, ""); }

        private static string Val(RegistryKey sw, string path, string name)
        {
            using (RegistryKey k = sw.OpenSubKey(path)) return k == null ? null : k.GetValue(name) as string;
        }

        private static string RealDefault(string path)
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(path)) return k == null ? null : k.GetValue("") as string;
        }

        private static void FirewallCases()
        {
            string exe = @"C:\Program Files\Windows Process Cleaner\WindowsProcessCleaner.exe";
            string del = Engine.FirewallDeleteArgs(exe);
            T.Check("firewall: delete is limited to inbound rules of this exe", del.Contains(" dir=in ") && del.EndsWith(" program=\"" + exe + "\""), del);
            string add = Engine.FirewallAddArgs(exe);
            T.Check("firewall: add allows inbound for this exe only, fixed name", add.Contains("name=\"" + Engine.FirewallRuleName + "\"")
                    && add.Contains(" dir=in action=allow ") && add.Contains(" program=\"" + exe + "\" ") && !add.Contains("localport") && !add.Contains("remoteip"), add);

            bool refused = false;
            try { Engine.FirewallDeleteArgs("C:\\x.exe\" name=all dir=out program=\"C:\\y.exe"); }
            catch (ArgumentException) { refused = true; }
            T.Check("firewall: a quote in the path cannot append netsh arguments", refused);

            string missing = Path.Combine(Fx.MakeDir(Fx.Root, "bt-firewall"), "no-rule-for-this.exe");
            FirewallInbound state = Engine.FirewallState(missing);
            if (state == FirewallInbound.Unknown) T.Skip("firewall: state of an exe without rules is None", "firewall service or COM unavailable");
            else T.Eq("firewall: state of an exe without rules is None", FirewallInbound.None, state);
        }
    }
}
