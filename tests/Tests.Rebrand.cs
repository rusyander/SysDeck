// SysDeck — тесты переезда со старого имени (Rebrand, BtAssoc.MigrateLegacy).
//
// Папки — во временном дереве тестов, реестр — под HKCU\Software\WPC-Tests\<pid>: настоящие %APPDATA%, ключи Run, привязки
// и задачи Планировщика не трогаются. Живой путь MigrateOnStart (остановка старых процессов, настоящая папка данных)
// здесь не проверяется — он зависит от чужих процессов и данных пользователя.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SysDeck.Downloads;

namespace SysDeck.Tests
{
    internal static class RebrandTests
    {
        internal static void Run()
        {
            DataDirCases();
            CommandCases();
            RegistryCases();
            T.Eq("rebrand: the elevated job has a human title", false, Elevation.JobTitle("rebrand") == "rebrand");
        }

        private static void DataDirCases()
        {
            string root = Path.Combine(Fx.Root, "rebrand");
            string legacy = Path.Combine(root, "WindowsProcessCleaner"), now = Path.Combine(root, "SysDeck");
            Directory.CreateDirectory(Path.Combine(legacy, "downloads"));
            File.WriteAllText(Path.Combine(legacy, "config.json"), "{\"Language\":\"ru\"}");
            File.WriteAllText(Path.Combine(Path.Combine(legacy, "downloads"), "index.json"), "[]");

            T.Eq("rebrand: before the move the legacy folder is the data folder", legacy, Rebrand.Resolve(legacy, now));

            // Занятый файл: переименовать папку нельзя — всё остаётся на месте, данные по-прежнему в старой папке.
            using (new FileStream(Path.Combine(legacy, "config.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool moved = Rebrand.MigrateDataDir(legacy, now, false);
                T.Check("rebrand: a file held open blocks the move and nothing is half-moved",
                        !moved && Rebrand.IsRealDirectory(legacy) && !Directory.Exists(now) && Rebrand.Resolve(legacy, now) == legacy);
            }

            T.Check("rebrand: the folder moves once nothing holds it", Rebrand.MigrateDataDir(legacy, now, false));
            T.Check("rebrand: files are in the new folder", File.ReadAllText(Path.Combine(now, "config.json")) == "{\"Language\":\"ru\"}"
                    && File.Exists(Path.Combine(Path.Combine(now, "downloads"), "index.json")));
            T.Check("rebrand: the old path is a junction, not a copy", Directory.Exists(legacy) && !Rebrand.IsRealDirectory(legacy));
            T.Check("rebrand: the old path still reads the same files (unpacked extension, old window)",
                    File.Exists(Path.Combine(Path.Combine(legacy, "downloads"), "index.json")));
            T.Eq("rebrand: after the move the new folder is the data folder", now, Rebrand.Resolve(legacy, now));
            T.Check("rebrand: a second run does nothing", !Rebrand.MigrateDataDir(legacy, now, false) && File.Exists(Path.Combine(now, "config.json")));

            // Новая папка уже есть (свежая установка рядом со старыми данными) — ничего не сливается и не перезаписывается.
            string legacy2 = Path.Combine(root, "old2"), now2 = Path.Combine(root, "new2");
            Directory.CreateDirectory(legacy2);
            Directory.CreateDirectory(now2);
            File.WriteAllText(Path.Combine(legacy2, "config.json"), "old");
            T.Check("rebrand: an existing new folder is never overwritten", !Rebrand.MigrateDataDir(legacy2, now2, false)
                    && !File.Exists(Path.Combine(now2, "config.json")) && Rebrand.Resolve(legacy2, now2) == now2);
            T.Check("rebrand: nothing to move without a legacy folder", !Rebrand.MigrateDataDir(Path.Combine(root, "none"), Path.Combine(root, "none-new"), false)
                    && !Directory.Exists(Path.Combine(root, "none-new")));
        }

        private static void CommandCases()
        {
            string args;
            T.Eq("rebrand: quoted command exe", @"C:\A B\WindowsProcessCleaner.exe", Rebrand.SplitCommand("\"C:\\A B\\WindowsProcessCleaner.exe\" --capture", out args));
            T.Eq("rebrand: quoted command args keep the leading space", " --capture", args);
            T.Eq("rebrand: unquoted command exe", @"C:\A\WindowsProcessCleaner.exe", Rebrand.SplitCommand(@"C:\A\WindowsProcessCleaner.exe --downloads", out args));
            T.Eq("rebrand: broken quote gives nothing", null, Rebrand.SplitCommand("\"C:\\A\\x.exe --capture", out args));

            string dir = Path.Combine(Fx.Root, "rebrand-exe");
            string other = Path.Combine(Fx.Root, "rebrand-other");
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(other);
            string exe = Path.Combine(dir, "SysDeck.exe");
            File.WriteAllText(Path.Combine(dir, "WindowsProcessCleaner.exe"), "");
            File.WriteAllText(Path.Combine(other, "WindowsProcessCleaner.exe"), "");
            T.Check("rebrand: the old exe beside the new one is this copy", Rebrand.IsLegacyCopyOf(Path.Combine(dir, "WindowsProcessCleaner.exe"), exe));
            T.Check("rebrand: a vanished old exe anywhere is this copy (renamed in place)", Rebrand.IsLegacyCopyOf(@"Z:\gone\WindowsProcessCleaner.exe", exe));
            T.Check("rebrand: a live old copy in another folder is left alone", !Rebrand.IsLegacyCopyOf(Path.Combine(other, "WindowsProcessCleaner.exe"), exe));
            T.Check("rebrand: another program's exe is never ours", !Rebrand.IsLegacyCopyOf(@"Z:\gone\other.exe", exe));

            T.Eq("rebrand: task command read from schtasks xml, entities decoded", @"C:\Wpc & Co\WindowsProcessCleaner.exe",
                 Rebrand.TaskCommand("<Exec>\r\n      <Command>\"C:\\Wpc &amp; Co\\WindowsProcessCleaner.exe\"</Command>\r\n      <Arguments>/tray</Arguments></Exec>"));
            T.Eq("rebrand: xml without a command", null, Rebrand.TaskCommand("<Task/>"));
        }

        private static void RegistryCases()
        {
            string sub = @"Software\WPC-Tests\" + Process.GetCurrentProcess().Id + "-rebrand";
            string dir = Path.Combine(Fx.Root, "rebrand-exe");
            string exe = Path.Combine(dir, "SysDeck.exe");
            string oldExe = Path.Combine(dir, "WindowsProcessCleaner.exe");
            string foreign = Path.Combine(Path.Combine(Fx.Root, "rebrand-other"), "WindowsProcessCleaner.exe");
            string realRun = RealRun();
            try
            {
                using (RegistryKey sw = Registry.CurrentUser.CreateSubKey(sub))
                {
                    using (RegistryKey run = sw.CreateSubKey("Run"))
                    {
                        run.SetValue("WindowsProcessCleaner.Capture", "\"" + oldExe + "\" --capture");
                        run.SetValue("WindowsProcessCleaner.Downloads", "\"" + foreign + "\" --downloads");
                        run.SetValue("WindowsProcessCleaner.FolderSize", "\"" + oldExe + "\" --foldersize");
                        run.SetValue("SysDeck.FolderSize", "\"" + exe + "\" --foldersize --keep");
                        List<string> done = Rebrand.MigrateRunValues(run, exe);
                        T.Eq("rebrand: run value of this copy moves with its arguments", "\"" + exe + "\" --capture", run.GetValue("SysDeck.Capture") as string);
                        T.Check("rebrand: the old run value is gone", run.GetValue("WindowsProcessCleaner.Capture") == null);
                        T.Check("rebrand: a live copy elsewhere keeps its run value", run.GetValue("WindowsProcessCleaner.Downloads") != null && run.GetValue("SysDeck.Downloads") == null);
                        T.Check("rebrand: an existing new value is not overwritten, the old one is still removed",
                                run.GetValue("SysDeck.FolderSize") as string == "\"" + exe + "\" --foldersize --keep" && run.GetValue("WindowsProcessCleaner.FolderSize") == null);
                        T.Eq("rebrand: two values reported moved", 2, done.Count);
                        T.Eq("rebrand: run values a second time - nothing", 0, Rebrand.MigrateRunValues(run, exe).Count);
                    }

                    // Привязки старой сборки: .torrent открывался ею, magnet тоже, до неё был qBittorrent.
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\WindowsProcessCleaner.Torrent\shell\open\command")) k.SetValue("", "\"" + oldExe + "\" /torrent \"%1\"");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\WindowsProcessCleaner.Magnet\shell\open\command")) k.SetValue("", "\"" + oldExe + "\" /torrent \"%1\"");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\WindowsProcessCleaner.Torrent")) k.SetValue("", "Торрент-файл");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\.torrent")) k.SetValue("", "WindowsProcessCleaner.Torrent");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\.torrent\OpenWithProgids")) k.SetValue("WindowsProcessCleaner.Torrent", "");
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\magnet\shell\open\command")) k.SetValue("", "\"" + oldExe + "\" /torrent \"%1\"");
                    using (RegistryKey k = sw.CreateSubKey(@"WindowsProcessCleaner\Associations")) k.SetValue("TorrentDefault", "qBittorrent.File.Torrent");
                    using (RegistryKey k = sw.CreateSubKey(@"WindowsProcessCleaner\Capabilities")) k.SetValue("ApplicationName", "WindowsProcessCleaner");
                    using (RegistryKey k = sw.CreateSubKey("RegisteredApplications")) k.SetValue("WindowsProcessCleaner", @"Software\WindowsProcessCleaner\Capabilities");

                    T.Check("rebrand: legacy associations reported moved", BtAssoc.MigrateLegacy(sw, exe));
                    T.Eq("rebrand: .torrent opens with SysDeck", BtAssocState.On, BtAssoc.TorrentState(sw, exe));
                    T.Eq("rebrand: magnet opens with SysDeck", BtAssocState.On, BtAssoc.MagnetState(sw, exe));
                    T.Check("rebrand: no legacy keys left", sw.OpenSubKey(@"Classes\WindowsProcessCleaner.Torrent") == null && sw.OpenSubKey(@"Classes\WindowsProcessCleaner.Magnet") == null
                            && sw.OpenSubKey("WindowsProcessCleaner") == null && Val(sw, "RegisteredApplications", "WindowsProcessCleaner") == null
                            && Val(sw, @"Classes\.torrent\OpenWithProgids", "WindowsProcessCleaner.Torrent") == null);
                    T.Eq("rebrand: registered for Default apps under the new name", @"Software\SysDeck\Capabilities", Val(sw, "RegisteredApplications", "SysDeck"));
                    T.Check("rebrand: associations a second time - nothing", !BtAssoc.MigrateLegacy(sw, exe));
                    BtAssoc.DisableTorrent(sw, exe);
                    T.Eq("rebrand: turning .torrent off restores the handler remembered by the old build", "qBittorrent.File.Torrent", Val(sw, @"Classes\.torrent", ""));
                    BtAssoc.DisableMagnet(sw, exe);
                    T.Check("rebrand: both off leave no keys of ours", sw.OpenSubKey("SysDeck") == null && Val(sw, "RegisteredApplications", "SysDeck") == null);

                    // Magnet другой живой копии старой сборки — не наш, не переносится.
                    using (RegistryKey k = sw.CreateSubKey(@"Classes\magnet\shell\open\command")) k.SetValue("", "\"" + foreign + "\" /torrent \"%1\"");
                    T.Check("rebrand: magnet of another live old copy is left alone", !BtAssoc.MigrateLegacy(sw, exe)
                            && Val(sw, @"Classes\magnet\shell\open\command", "") == "\"" + foreign + "\" /torrent \"%1\"");
                }
            }
            finally
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(sub, false); } catch { }
                using (RegistryKey parent = Registry.CurrentUser.OpenSubKey(@"Software\WPC-Tests", true))
                    if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0) { parent.Close(); Registry.CurrentUser.DeleteSubKey(@"Software\WPC-Tests", false); }
            }
            T.Eq("rebrand: the real Run key is untouched", realRun, RealRun());
        }

        private static string Val(RegistryKey sw, string path, string name)
        {
            using (RegistryKey k = sw.OpenSubKey(path)) return k == null ? null : k.GetValue(name) as string;
        }

        private static string RealRun()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (k == null) return "";
                List<string> parts = new List<string>();
                foreach (string n in k.GetValueNames()) parts.Add(n + "=" + k.GetValue(n));
                parts.Sort(StringComparer.Ordinal);
                return string.Join("|", parts.ToArray());
            }
        }
    }
}
