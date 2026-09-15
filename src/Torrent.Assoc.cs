// SysDeck — «Загрузки», торренты: открытие .torrent и magnet: этой программой (реестр текущего пользователя).
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Только HKCU и только по переключателю, который пользователь нажал сам. Пишется своё: ProgID SysDeck.Torrent и
// SysDeck.Magnet, Capabilities + RegisteredApplications (программа появляется в «Приложениях по умолчанию»),
// значение по умолчанию .torrent и ключ протокола magnet. Прежний обработчик из HKCU запоминается в Software\
// SysDeck\Associations и возвращается при отключении, если на его месте всё ещё мы.
// UserChoice (выбор, сделанный в самой Windows) программа не трогает: он защищён хешем, и перебить его — ровно то, за что
// Windows наказывает сбросом ассоциаций. Если там другое приложение, результат — NeedsSettings: окно открывает страницу
// «Приложения по умолчанию» на этой программе.
// Все методы принимают раздел Software (HKCU\Software или тестовый) — тесты не касаются настоящих ассоциаций.
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace SysDeck.Downloads
{
    internal enum BtAssocState { Off, On, NeedsSettings }

    internal static class BtAssoc
    {
        public const string TorrentProgId = "SysDeck.Torrent";
        public const string MagnetProgId = "SysDeck.Magnet";
        public const string AppName = "SysDeck";
        public const string OpenSwitch = "/torrent";
        public const long MaxTorrentFile = 32L * 1024 * 1024;   // крупнейшие .torrent с сотнями тысяч файлов — единицы МБ
        private const string AppKey = @"SysDeck";
        private const string CapabilitiesKey = AppKey + @"\Capabilities";
        private const string SavedKey = AppKey + @"\Associations";
        private const string TorrentChoice = @"Microsoft\Windows\CurrentVersion\Explorer\FileExts\.torrent\UserChoice";
        private const string MagnetChoice = @"Microsoft\Windows\Shell\Associations\UrlAssociations\magnet\UserChoice";

        public static string OpenCommand(string exe) { return "\"" + exe + "\" " + OpenSwitch + " \"%1\""; }

        // ---------- состояние ----------
        public static BtAssocState TorrentState(RegistryKey software, string exe)
        {
            if (!IsOurCommand(software, @"Classes\" + TorrentProgId, exe) || ReadDefault(software, @"Classes\.torrent") != TorrentProgId) return BtAssocState.Off;
            string choice = ReadValue(software, TorrentChoice, "ProgId");
            return choice == null || choice == TorrentProgId ? BtAssocState.On : BtAssocState.NeedsSettings;
        }

        public static BtAssocState MagnetState(RegistryKey software, string exe)
        {
            if (!IsOurCommand(software, @"Classes\magnet", exe)) return BtAssocState.Off;
            string choice = ReadValue(software, MagnetChoice, "ProgId");
            return choice == null || choice == MagnetProgId ? BtAssocState.On : BtAssocState.NeedsSettings;
        }

        // ---------- включение ----------
        public static BtAssocState EnableTorrent(RegistryKey software, string exe)
        {
            WriteProgId(software, TorrentProgId, Tr.S("Торрент-файл", "Torrent file"), exe, false);
            WriteCapabilities(software);
            using (RegistryKey ext = software.CreateSubKey(@"Classes\.torrent"))
            {
                string previous = ext.GetValue("") as string;
                if (previous != TorrentProgId)
                {
                    using (RegistryKey saved = software.CreateSubKey(SavedKey)) saved.SetValue("TorrentDefault", previous ?? "");
                    ext.SetValue("", TorrentProgId);
                }
                if (ext.GetValue("Content Type") == null) ext.SetValue("Content Type", "application/x-bittorrent");
                using (RegistryKey open = ext.CreateSubKey("OpenWithProgids")) open.SetValue(TorrentProgId, "");
            }
            return TorrentState(software, exe);
        }

        public static BtAssocState EnableMagnet(RegistryKey software, string exe)
        {
            WriteProgId(software, MagnetProgId, "URL:Magnet link", exe, true);
            WriteCapabilities(software);
            // Ключ протокола: чужую команду запоминаем целиком (команда и значок), своё пишем поверх.
            if (!IsOurCommand(software, @"Classes\magnet", exe))
            {
                using (RegistryKey saved = software.CreateSubKey(SavedKey))
                {
                    saved.SetValue("MagnetCommand", ReadDefault(software, @"Classes\magnet\shell\open\command") ?? "");
                    saved.SetValue("MagnetIcon", ReadDefault(software, @"Classes\magnet\DefaultIcon") ?? "");
                }
            }
            WriteHandler(software, @"Classes\magnet", "URL:Magnet link", exe, true);
            return MagnetState(software, exe);
        }

        // ---------- отключение ----------
        public static void DisableTorrent(RegistryKey software, string exe)
        {
            using (RegistryKey ext = software.OpenSubKey(@"Classes\.torrent", true))
            {
                if (ext != null)
                {
                    if (ext.GetValue("") as string == TorrentProgId)
                    {
                        string previous = ReadValue(software, SavedKey, "TorrentDefault");
                        if (!string.IsNullOrEmpty(previous)) ext.SetValue("", previous);
                        else ext.DeleteValue("", false);
                    }
                    using (RegistryKey open = ext.OpenSubKey("OpenWithProgids", true))
                        if (open != null) open.DeleteValue(TorrentProgId, false);
                }
            }
            DeleteSaved(software, "TorrentDefault");
            DeleteTreeIfOurs(software, @"Classes\" + TorrentProgId, exe);
            using (RegistryKey fa = software.OpenSubKey(CapabilitiesKey + @"\FileAssociations", true))
                if (fa != null) fa.DeleteValue(".torrent", false);
            CleanupCapabilities(software);
        }

        public static void DisableMagnet(RegistryKey software, string exe)
        {
            if (IsOurCommand(software, @"Classes\magnet", exe))
            {
                string command = ReadValue(software, SavedKey, "MagnetCommand");
                if (!string.IsNullOrEmpty(command))
                {
                    using (RegistryKey c = software.CreateSubKey(@"Classes\magnet\shell\open\command")) c.SetValue("", command);
                    string icon = ReadValue(software, SavedKey, "MagnetIcon");
                    using (RegistryKey i = software.CreateSubKey(@"Classes\magnet\DefaultIcon"))
                    {
                        if (!string.IsNullOrEmpty(icon)) i.SetValue("", icon);
                        else i.DeleteValue("", false);
                    }
                }
                else software.DeleteSubKeyTree(@"Classes\magnet", false);
            }
            DeleteSaved(software, "MagnetCommand");
            DeleteSaved(software, "MagnetIcon");
            DeleteTreeIfOurs(software, @"Classes\" + MagnetProgId, exe);
            using (RegistryKey ua = software.OpenSubKey(CapabilitiesKey + @"\URLAssociations", true))
                if (ua != null) ua.DeleteValue("magnet", false);
            CleanupCapabilities(software);
        }

        // ---------- переезд со старого имени (Rebrand) ----------
        // Привязки старой сборки этой же копии становятся привязками SysDeck. Выбранное пользователем не меняется: .torrent
        // переходит на новую ProgID, только если открывался старой; запомненные чужие привязки переносятся под новый ключ.
        // true — что-то перенесено.
        internal static bool MigrateLegacy(RegistryKey software, string exe)
        {
            string legacyTorrent = Rebrand.LegacyName + ".Torrent", legacyMagnet = Rebrand.LegacyName + ".Magnet";
            bool torrent = ReadDefault(software, @"Classes\" + legacyTorrent) != null;
            string magnetCmd = ReadDefault(software, @"Classes\magnet\shell\open\command"), args;
            bool magnet = magnetCmd != null && magnetCmd.IndexOf(" " + OpenSwitch + " ", StringComparison.OrdinalIgnoreCase) >= 0
                          && Rebrand.IsLegacyCopyOf(Rebrand.SplitCommand(magnetCmd, out args), exe);
            bool legacyApp;
            using (RegistryKey app = software.OpenSubKey(Rebrand.LegacyName)) legacyApp = app != null;
            if (!torrent && !magnet && !legacyApp && ReadDefault(software, @"Classes\" + legacyMagnet) == null) return false;

            using (RegistryKey old = software.OpenSubKey(Rebrand.LegacyName + @"\Associations"))
                if (old != null)
                    using (RegistryKey saved = software.CreateSubKey(SavedKey))
                        foreach (string name in old.GetValueNames())
                            if (saved.GetValue(name) == null) saved.SetValue(name, old.GetValue(name));
            if (torrent)
            {
                WriteProgId(software, TorrentProgId, Tr.S("Торрент-файл", "Torrent file"), exe, false);
                using (RegistryKey ext = software.CreateSubKey(@"Classes\.torrent"))
                {
                    if (ext.GetValue("") as string == legacyTorrent) ext.SetValue("", TorrentProgId);
                    using (RegistryKey open = ext.CreateSubKey("OpenWithProgids"))
                    {
                        open.DeleteValue(legacyTorrent, false);
                        open.SetValue(TorrentProgId, "");
                    }
                }
            }
            if (magnet)
            {
                WriteProgId(software, MagnetProgId, "URL:Magnet link", exe, true);
                WriteHandler(software, @"Classes\magnet", "URL:Magnet link", exe, true);
            }
            software.DeleteSubKeyTree(@"Classes\" + legacyTorrent, false);
            software.DeleteSubKeyTree(@"Classes\" + legacyMagnet, false);
            using (RegistryKey reg = software.OpenSubKey("RegisteredApplications", true))
                if (reg != null) reg.DeleteValue(Rebrand.LegacyName, false);
            software.DeleteSubKeyTree(Rebrand.LegacyName, false);
            if (torrent || magnet) WriteCapabilities(software);
            CleanupCapabilities(software);
            NotifyShell();
            return true;
        }

        // Проводник перечитывает ассоциации только после уведомления.
        public static void NotifyShell()
        {
            try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // «Приложения по умолчанию» сразу на этой программе (Windows 11); старые сборки игнорируют параметр и открывают общий список.
        public static void OpenDefaultAppsSettings()
        {
            try
            {
                Process p = Process.Start("ms-settings:defaultapps?registeredAppUser=" + Uri.EscapeDataString(AppName));
                if (p != null) p.Dispose();
            }
            catch (Exception ex) { DlLog.Report(ex); }
        }

        // ---------- аргумент командной строки ----------
        // Что пришло после /torrent: magnet-ссылка или путь к существующему .torrent. null — ни то ни другое (молча не открываем).
        public static string ValidateOpenArgument(string arg)
        {
            if (string.IsNullOrEmpty(arg) || arg.Length > 64 * 1024) return null;
            string a = arg.Trim();
            if (a.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return a;
            try
            {
                if (!a.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase) || !Path.IsPathRooted(a)) return null;
                if (!Native.PathExists(a) || DlFiles.IsReparse(a)) return null;
                long size = BtFs.Length(a);
                return size > 0 && size <= MaxTorrentFile ? a : null;
            }
            catch (ArgumentException) { return null; }
        }

        // ---------- реестр ----------
        private static void WriteProgId(RegistryKey software, string progId, string title, string exe, bool urlProtocol)
        {
            WriteHandler(software, @"Classes\" + progId, title, exe, urlProtocol);
        }

        private static void WriteHandler(RegistryKey software, string path, string title, string exe, bool urlProtocol)
        {
            using (RegistryKey k = software.CreateSubKey(path))
            {
                k.SetValue("", title);
                if (urlProtocol) k.SetValue("URL Protocol", "");
                using (RegistryKey icon = k.CreateSubKey("DefaultIcon")) icon.SetValue("", "\"" + exe + "\",0");
                using (RegistryKey cmd = k.CreateSubKey(@"shell\open\command")) cmd.SetValue("", OpenCommand(exe));
            }
        }

        private static void WriteCapabilities(RegistryKey software)
        {
            using (RegistryKey cap = software.CreateSubKey(CapabilitiesKey))
            {
                cap.SetValue("ApplicationName", AppName);
                cap.SetValue("ApplicationDescription", Tr.S("Загрузки и торренты", "Downloads and torrents"));
                using (RegistryKey fa = cap.CreateSubKey("FileAssociations")) fa.SetValue(".torrent", TorrentProgId);
                using (RegistryKey ua = cap.CreateSubKey("URLAssociations")) ua.SetValue("magnet", MagnetProgId);
            }
            using (RegistryKey reg = software.CreateSubKey("RegisteredApplications")) reg.SetValue(AppName, @"Software\" + CapabilitiesKey);
        }

        // Пока хоть одна привязка включена, программа остаётся в списке «Приложений по умолчанию» с обеими возможностями
        // (FileAssociations/URLAssociations описывают, что программа умеет, а не что выбрано).
        private static void CleanupCapabilities(RegistryKey software)
        {
            bool torrentOn = ReadDefault(software, @"Classes\" + TorrentProgId) != null;
            bool magnetOn = ReadDefault(software, @"Classes\" + MagnetProgId) != null;
            if (torrentOn || magnetOn) { WriteCapabilitiesKeep(software, torrentOn, magnetOn); return; }
            software.DeleteSubKeyTree(CapabilitiesKey, false);
            using (RegistryKey reg = software.OpenSubKey("RegisteredApplications", true))
                if (reg != null) reg.DeleteValue(AppName, false);
            using (RegistryKey app = software.OpenSubKey(AppKey))
            {
                bool empty = app != null && app.SubKeyCount == 0 && app.ValueCount == 0;
                if (app != null) app.Close();
                if (empty) software.DeleteSubKey(AppKey, false);
            }
        }

        private static void WriteCapabilitiesKeep(RegistryKey software, bool torrent, bool magnet)
        {
            using (RegistryKey cap = software.CreateSubKey(CapabilitiesKey))
            {
                using (RegistryKey fa = cap.CreateSubKey("FileAssociations"))
                    if (torrent) fa.SetValue(".torrent", TorrentProgId); else fa.DeleteValue(".torrent", false);
                using (RegistryKey ua = cap.CreateSubKey("URLAssociations"))
                    if (magnet) ua.SetValue("magnet", MagnetProgId); else ua.DeleteValue("magnet", false);
            }
        }

        private static bool IsOurCommand(RegistryKey software, string path, string exe)
        {
            string cmd = ReadDefault(software, path + @"\shell\open\command");
            return cmd != null && string.Equals(cmd, OpenCommand(exe), StringComparison.OrdinalIgnoreCase);
        }

        private static void DeleteTreeIfOurs(RegistryKey software, string path, string exe)
        {
            string cmd = ReadDefault(software, path + @"\shell\open\command");
            // Команда другой копии программы (портативная в другой папке) — тоже наша ProgID, её ключ удаляется целиком.
            if (cmd == null || cmd.IndexOf(" " + OpenSwitch + " ", StringComparison.OrdinalIgnoreCase) >= 0 || string.Equals(cmd, OpenCommand(exe), StringComparison.OrdinalIgnoreCase))
                software.DeleteSubKeyTree(path, false);
        }

        private static void DeleteSaved(RegistryKey software, string name)
        {
            using (RegistryKey saved = software.OpenSubKey(SavedKey, true))
            {
                if (saved == null) return;
                saved.DeleteValue(name, false);
                bool empty = saved.ValueCount == 0 && saved.SubKeyCount == 0;
                saved.Close();
                if (empty) software.DeleteSubKey(SavedKey, false);
            }
        }

        private static string ReadDefault(RegistryKey software, string path) { return ReadValue(software, path, ""); }

        private static string ReadValue(RegistryKey software, string path, string name)
        {
            using (RegistryKey k = software.OpenSubKey(path))
                return k == null ? null : k.GetValue(name) as string;
        }

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
    }
}
