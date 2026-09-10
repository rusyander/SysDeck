using System;
using System.Globalization;
using System.IO;
using System.Security.Principal;

namespace WpcSetup
{
    internal enum InstallScope
    {
        PerUser,
        Machine
    }

    // Установщик — отдельная программа: он собирается без src\*.cs, поэтому ни Tr, ни Engine
    // ему недоступны. Всё, о чём он с приложением договорился, собрано здесь одним списком:
    // имя задачи планировщика (Engine.Autostart.TaskName), папка данных в профиле
    // (Engine.DefaultDataDir), имя exe. Разъедется одно из этих имён — деинсталлятор молча
    // оставит после себя мусор, поэтому менять их надо парой.
    internal static class Product
    {
        public const string Name = "Windows Process Cleaner";
        public const string ExeName = "WindowsProcessCleaner.exe";
        public const string UninstallExeName = "uninstall.exe";
        public const string FolderName = "WindowsProcessCleaner";
        public const string ShortcutFile = "Windows Process Cleaner.lnk";
        public const string TaskName = "WindowsProcessCleaner";
        public const string DataFolderName = "WindowsProcessCleaner";
        public const string Publisher = "rusyander";
        public const string About = "https://github.com/rusyander/cleaner";
        public const string RegKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WindowsProcessCleaner";

        // Имя ресурса, под которым exe приложения зашит в установщик (см. build-installer.bat).
        public const string ResourceName = "app.exe";

        // build.bat не проставляет AssemblyFileVersion, поэтому у собранного exe версия
        // 0.0.0.0. Показывать её в «Программах и компонентах» бессмысленно — берём версию
        // из assemblyIdentity приложения (app.manifest).
        public const string FallbackVersion = "1.0.0";

        public static string DefaultDir(InstallScope scope)
        {
            if (scope == InstallScope.Machine)
                return Path.Combine(Folder(Environment.SpecialFolder.ProgramFiles, @"C:\Program Files"), FolderName);

            // %LOCALAPPDATA%\Programs — место, куда ставятся приложения «только для меня»
            // (так делают VS Code и Teams); запрос UAC туда не нужен.
            return Path.Combine(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData,
                                                    @"C:\Users\Default\AppData\Local"), "Programs"), FolderName);
        }

        public static string DataDir()
        {
            return Path.Combine(Folder(Environment.SpecialFolder.ApplicationData, @"C:\Users\Default\AppData\Roaming"),
                                DataFolderName);
        }

        public static bool IsElevated()
        {
            try
            {
                using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        public static string Folder(Environment.SpecialFolder id, string fallback)
        {
            try
            {
                string p = Environment.GetFolderPath(id);
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return fallback;
        }

        public static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string p = path.Trim().Trim('"');
            try { p = Path.GetFullPath(p); }
            catch { }
            return p.TrimEnd('\\');
        }

        public static bool Inside(string path, string dir)
        {
            string p = Norm(path);
            string d = Norm(dir);
            if (p.Length == 0 || d.Length == 0) return false;
            return p.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
                || p.Equals(d, StringComparison.OrdinalIgnoreCase);
        }

        // Защита от «удалить D:\» — деинсталлятор сносит папку установки рекурсивно, и путь
        // к ней приходит из реестра или из командной строки, то есть его можно подменить.
        public static bool LooksLikeInstallDir(string dir)
        {
            string d = Norm(dir);
            if (d.Length < 4) return false;
            string root;
            try { root = Path.GetPathRoot(d); }
            catch { return false; }
            if (root != null && d.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
            return File.Exists(Path.Combine(d, ExeName)) || File.Exists(Path.Combine(d, UninstallExeName));
        }

        public static string Quote(string path)
        {
            // хвостовой «\» перед закрывающей кавычкой экранирует её и склеивает аргументы
            return "\"" + Norm(path) + "\"";
        }

        public static string Kb(long bytes)
        {
            return (bytes / 1024L).ToString(CultureInfo.InvariantCulture);
        }
    }
}
