using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace WpcSetup
{
    // Пользователь отказался продолжать. Отдельный тип, чтобы отмену не путать с ошибкой:
    // на отмене окно закрывается молча, на ошибке показывает причину.
    internal sealed class SetupCancelled : Exception
    {
        public SetupCancelled() : base("cancelled") { }
    }

    // Работа идёт в фоновом потоке, а спрашивать и писать в журнал приходится в оконном, —
    // поэтому обе операции вынесены за интерфейс: окно переносит их через Invoke, тихий
    // режим (/quiet) отвечает сам.
    internal interface IWorkHost
    {
        void Log(string line);
        DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon);
    }

    // Тихий режим не имеет права ничего спрашивать: QuietUninstallString вызывают средства
    // управления, которым отвечать на диалог некому. Ответ — «да», иначе тихое удаление
    // зависло бы навсегда.
    internal sealed class QuietHost : IWorkHost
    {
        public void Log(string line) { }

        public DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (buttons == MessageBoxButtons.RetryCancel) return DialogResult.Cancel;
            return buttons == MessageBoxButtons.YesNo ? DialogResult.Yes : DialogResult.OK;
        }
    }

    internal sealed class WorkContext
    {
        public string Dir;
        public InstallScope Scope;
        public bool DesktopShortcut;
        public bool DeleteData;
        public IWorkHost Host;

        public void Log(string line) { Host.Log(line); }
    }

    internal static class Work
    {
        // ---------- Запущенные копии ----------

        // Ищем по пути образа, а не по имени процесса: в папке установки может лежать
        // переименованная копия, и она точно так же держит файл.
        public static Process[] RunningIn(string dir)
        {
            List<Process> hit = new List<Process>();
            int self = 0;
            try { self = Process.GetCurrentProcess().Id; }
            catch { }

            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return new Process[0]; }

            foreach (Process p in all)
            {
                bool keep = false;
                try
                {
                    if (p.Id != self && p.Id > 4)
                    {
                        string path = Native.ImagePath(p.Id);
                        keep = path != null && Product.Inside(path, dir);
                    }
                }
                catch { keep = false; }

                if (keep) hit.Add(p);
                else p.Dispose();
            }
            return hit.ToArray();
        }

        // Возвращает управление, только когда в папке никто не работает; отмена — исключение.
        public static void EnsureClosed(WorkContext c)
        {
            while (true)
            {
                Process[] running = RunningIn(c.Dir);
                if (running.Length == 0) return;

                try
                {
                    string names = Describe(running);
                    DialogResult ok = c.Host.Ask(
                        L.S("Программа сейчас запущена и её файлы заняты:", "The application is running and its files are in use:")
                            + "\r\n\r\n" + names + "\r\n\r\n"
                            + L.S("Закрыть её и продолжить?", "Close it and continue?"),
                        L.S("Программа запущена", "Application is running"),
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ok != DialogResult.Yes) throw new SetupCancelled();

                    c.Log(L.S("Закрываю запущенную программу…", "Closing the running application..."));
                    foreach (Process p in running) { try { p.CloseMainWindow(); } catch { } }
                    if (WaitGone(running, 5000)) return;

                    // Окно закрывается в трей (MainForm.FormClosing), поэтому мягкая просьба
                    // приложение не завершает — снимаем процесс. Согласие уже получено выше.
                    foreach (Process p in running) { try { if (!p.HasExited) p.Kill(); } catch { } }
                    if (WaitGone(running, 5000)) return;

                    // Приложение работает от администратора: обычный пользователь его процесс
                    // снять не может, и обещать обратное нельзя — просим закрыть руками.
                    DialogResult again = c.Host.Ask(
                        L.S("Не удалось закрыть программу: она запущена от имени администратора, а установщик — нет.",
                            "Could not close the application: it runs as administrator while the installer does not.")
                            + "\r\n\r\n"
                            + L.S("Закройте её сами — правый клик по значку в трее, «Выход» — и нажмите «Повторить».",
                                  "Close it yourself - right-click the tray icon, \"Exit\" - then press Retry."),
                        L.S("Программа запущена", "Application is running"),
                        MessageBoxButtons.RetryCancel, MessageBoxIcon.Warning);
                    if (again != DialogResult.Retry) throw new SetupCancelled();
                }
                finally
                {
                    foreach (Process p in running) { try { p.Dispose(); } catch { } }
                }
            }
        }

        private static string Describe(Process[] list)
        {
            List<string> lines = new List<string>();
            foreach (Process p in list)
            {
                string path = null;
                try { path = Native.ImagePath(p.Id); }
                catch { }
                try { lines.Add("    " + (path ?? p.ProcessName) + "  (PID " + p.Id + ")"); }
                catch { }
                if (lines.Count >= 6) break;
            }
            return string.Join("\r\n", lines.ToArray());
        }

        private static bool WaitGone(Process[] list, int ms)
        {
            int waited = 0;
            while (waited < ms)
            {
                bool alive = false;
                foreach (Process p in list) { try { if (!p.HasExited) alive = true; } catch { } }
                if (!alive) return true;
                Thread.Sleep(250);
                waited += 250;
            }
            foreach (Process p in list) { try { if (!p.HasExited) return false; } catch { } }
            return true;
        }

        // ---------- Установка ----------

        public static void Install(WorkContext c)
        {
            c.Log(L.S("Папка установки: ", "Install folder: ") + c.Dir);
            EnsureClosed(c);

            Directory.CreateDirectory(c.Dir);
            string exe = Path.Combine(c.Dir, Product.ExeName);
            string uninstaller = Path.Combine(c.Dir, Product.UninstallExeName);

            c.Log(L.S("Распаковка программы…", "Extracting the application..."));
            WriteFile(c, exe, ExtractApp);

            // Деинсталлятор — копия самого установщика: отдельная сборка означала бы второй
            // набор исходников, который рассинхронизируется с первым.
            c.Log(L.S("Установка деинсталлятора…", "Installing the uninstaller..."));
            string self = Application.ExecutablePath;
            WriteFile(c, uninstaller, delegate(Stream dst)
            {
                using (FileStream src = new FileStream(self, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    src.CopyTo(dst);
            });

            // Портативная метка в установленной сборке означала бы данные в Program Files —
            // при повторной установке поверх портативной копии её надо убрать.
            string marker = Path.Combine(c.Dir, "portable.marker");
            if (File.Exists(marker))
            {
                try { File.Delete(marker); c.Log(L.S("Убрана метка портативного режима.", "Portable marker removed.")); }
                catch { }
            }

            string version = ReadVersion(exe);
            c.Log(L.S("Версия: ", "Version: ") + version);

            c.Log(L.S("Ярлыки…", "Shortcuts..."));
            WriteShortcuts(c, exe);

            c.Log(L.S("Запись в список установленных программ…", "Registering in installed programs..."));
            WriteRegistry(c, exe, uninstaller, version);

            c.Log(L.S("Готово.", "Done."));
        }

        private static void ExtractApp(Stream dst)
        {
            using (Stream src = Assembly.GetExecutingAssembly().GetManifestResourceStream(Product.ResourceName))
            {
                if (src == null)
                    throw new FileNotFoundException(L.S("В установщике нет вложенной программы — он собран неправильно.",
                                                        "The installer carries no embedded application - it was built incorrectly."));
                src.CopyTo(dst);
            }
        }

        // Файл может быть занят антивирусом или ещё не отпущенным процессом: короткие
        // повторы дешевле, чем провал установки на ровном месте.
        private static void WriteFile(WorkContext c, string path, Action<Stream> writer)
        {
            int attempt = 0;
            while (true)
            {
                attempt++;
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                        writer(fs);
                    return;
                }
                catch (Exception ex)
                {
                    if (attempt < 4) { Thread.Sleep(400); continue; }
                    DialogResult r = c.Host.Ask(
                        L.S("Не удалось записать файл:", "Could not write the file:") + "\r\n    " + path + "\r\n\r\n"
                            + ex.Message,
                        L.S("Ошибка записи", "Write error"), MessageBoxButtons.RetryCancel, MessageBoxIcon.Error);
                    if (r != DialogResult.Retry) throw new SetupCancelled();
                    attempt = 0;
                }
            }
        }

        public static string ReadVersion(string exe)
        {
            try
            {
                FileVersionInfo fi = FileVersionInfo.GetVersionInfo(exe);
                string v = Clean(fi.FileVersion);
                if (v != null) return v;
                v = Clean(fi.ProductVersion);
                if (v != null) return v;
            }
            catch { }
            return Product.FallbackVersion;
        }

        private static string Clean(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            return v.Length == 0 || v == "0.0.0.0" || v == "0.0.0" ? null : v;
        }

        // ---------- Ярлыки ----------

        private static string StartMenuLnk(InstallScope scope)
        {
            return Path.Combine(Product.Folder(scope == InstallScope.Machine
                                                   ? Environment.SpecialFolder.CommonPrograms
                                                   : Environment.SpecialFolder.Programs, null) ?? "",
                                Product.ShortcutFile);
        }

        private static string DesktopLnk(InstallScope scope)
        {
            return Path.Combine(Product.Folder(scope == InstallScope.Machine
                                                   ? Environment.SpecialFolder.CommonDesktopDirectory
                                                   : Environment.SpecialFolder.DesktopDirectory, null) ?? "",
                                Product.ShortcutFile);
        }

        private static void WriteShortcuts(WorkContext c, string exe)
        {
            string menu = StartMenuLnk(c.Scope);
            try
            {
                Shortcut.Create(menu, exe, c.Dir, Product.Name);
                c.Log("    " + menu);
            }
            catch (Exception ex)
            {
                c.Log(L.S("    не удалось создать ярлык в меню «Пуск»: ", "    could not create the Start menu shortcut: ") + ex.Message);
            }

            string desktop = DesktopLnk(c.Scope);
            if (c.DesktopShortcut)
            {
                try
                {
                    Shortcut.Create(desktop, exe, c.Dir, Product.Name);
                    c.Log("    " + desktop);
                }
                catch (Exception ex)
                {
                    c.Log(L.S("    не удалось создать ярлык на рабочем столе: ", "    could not create the desktop shortcut: ") + ex.Message);
                }
            }
            else
            {
                // Повторная установка со снятым флажком — это просьба ярлык убрать.
                try { if (File.Exists(desktop)) File.Delete(desktop); }
                catch { }
            }
        }

        // ---------- Реестр ----------

        public static RegistryKey Root(InstallScope scope)
        {
            RegistryView view = Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default;
            return RegistryKey.OpenBaseKey(scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
                                           view);
        }

        private static void WriteRegistry(WorkContext c, string exe, string uninstaller, string version)
        {
            long bytes = 0;
            try
            {
                foreach (string f in Directory.GetFiles(c.Dir, "*", SearchOption.AllDirectories))
                {
                    try { bytes += new FileInfo(f).Length; }
                    catch { }
                }
            }
            catch { }

            using (RegistryKey root = Root(c.Scope))
            using (RegistryKey k = root.CreateSubKey(Product.RegKey))
            {
                if (k == null) throw new IOException(L.S("Не удалось создать ключ реестра.", "Could not create the registry key."));
                k.SetValue("DisplayName", Product.Name, RegistryValueKind.String);
                k.SetValue("DisplayVersion", version, RegistryValueKind.String);
                k.SetValue("Publisher", Product.Publisher, RegistryValueKind.String);
                k.SetValue("InstallLocation", c.Dir, RegistryValueKind.String);
                k.SetValue("DisplayIcon", exe, RegistryValueKind.String);
                k.SetValue("UninstallString", Product.Quote(uninstaller) + " /uninstall", RegistryValueKind.String);
                k.SetValue("QuietUninstallString", Product.Quote(uninstaller) + " /uninstall /quiet", RegistryValueKind.String);
                k.SetValue("URLInfoAbout", Product.About, RegistryValueKind.String);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"), RegistryValueKind.String);
                k.SetValue("EstimatedSize", (int)(bytes / 1024L), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        // ---------- Удаление ----------

        public static InstallScope DetectScope(string dir)
        {
            string here = Product.Norm(dir);
            if (here.Equals(Product.Norm(RegisteredDir(InstallScope.PerUser)), StringComparison.OrdinalIgnoreCase))
                return InstallScope.PerUser;
            if (here.Equals(Product.Norm(RegisteredDir(InstallScope.Machine)), StringComparison.OrdinalIgnoreCase))
                return InstallScope.Machine;

            // Записи нет (её уже снесли или ставили руками) — судим по месту.
            string pf = Product.Folder(Environment.SpecialFolder.ProgramFiles, @"C:\Program Files");
            string pf86 = Product.Folder(Environment.SpecialFolder.ProgramFilesX86, @"C:\Program Files (x86)");
            return Product.Inside(dir, pf) || Product.Inside(dir, pf86) ? InstallScope.Machine : InstallScope.PerUser;
        }

        public static string RegisteredDir(InstallScope scope)
        {
            try
            {
                using (RegistryKey root = Root(scope))
                using (RegistryKey k = root.OpenSubKey(Product.RegKey))
                {
                    if (k == null) return null;
                    return k.GetValue("InstallLocation") as string;
                }
            }
            catch { return null; }
        }

        public static void Uninstall(WorkContext c)
        {
            EnsureClosed(c);

            c.Log(L.S("Удаление задачи автозапуска…", "Removing the autostart task..."));
            DeleteTask(c);

            c.Log(L.S("Удаление ярлыков…", "Removing shortcuts..."));
            DeleteFileQuiet(StartMenuLnk(c.Scope), c);
            DeleteFileQuiet(DesktopLnk(c.Scope), c);

            c.Log(L.S("Удаление связи с браузерами и автозапуска загрузок…", "Removing the browser integration and the downloads autostart..."));
            try
            {
                using (RegistryKey software = Registry.CurrentUser.OpenSubKey("Software", true))
                    if (software != null) DeleteUserEntries(software, Product.DataDir(), c.Dir, c.Log);
            }
            catch (Exception ex)
            {
                c.Log("    " + ex.Message);
            }

            c.Log(L.S("Удаление записи из списка установленных программ…", "Removing the installed-programs entry..."));
            try
            {
                using (RegistryKey root = Root(c.Scope))
                    root.DeleteSubKeyTree(Product.RegKey, false);
            }
            catch (Exception ex)
            {
                c.Log("    " + ex.Message);
            }

            c.Log(L.S("Удаление файлов программы…", "Removing program files..."));
            if (Product.LooksLikeInstallDir(c.Dir)) DeleteTree(c.Dir, c);
            else c.Log(L.S("    папка не похожа на установленную копию, пропущено: ",
                           "    the folder does not look like an installation, skipped: ") + c.Dir);

            if (c.DeleteData)
            {
                string data = Product.DataDir();
                c.Log(L.S("Удаление настроек и истории…", "Removing settings and history...") + " " + data);
                if (Directory.Exists(data)) DeleteTree(data, c);
            }

            c.Log(L.S("Готово.", "Done."));
        }

        // Имена повторяют DlBrowsers.HostKeys и DlLauncher.RunValueName в src\: установщик собирается отдельно от программы.
        private static readonly string[] HostKeys =
        {
            @"Google\Chrome\NativeMessagingHosts\org.wpc.downloads",
            @"Microsoft\Edge\NativeMessagingHosts\org.wpc.downloads",
            @"Mozilla\NativeMessagingHosts\org.wpc.downloads",
        };
        private const string RunKey = @"Microsoft\Windows\CurrentVersion\Run";
        private const string DownloadsRunValue = "WindowsProcessCleaner.Downloads";

        // Эти записи в HKCU пишет сама программа, а не установщик. Удаляются только свои: ключ браузера — если ведёт
        // в папку манифестов этой папки данных, автозапуск загрузок — если запускает exe из папки установки.
        // Портативная копия и копия с другой папкой данных пишут другое и остаются нетронутыми.
        internal static void DeleteUserEntries(RegistryKey software, string dataDir, string installDir, Action<string> log)
        {
            string manifests = Path.GetFullPath(Path.Combine(Path.Combine(dataDir, "downloads"), "nmh")).TrimEnd('\\') + "\\";
            foreach (string name in HostKeys)
            {
                string manifest;
                using (RegistryKey k = software.OpenSubKey(name))
                    manifest = k == null ? null : k.GetValue("") as string;
                if (manifest == null || !manifest.StartsWith(manifests, StringComparison.OrdinalIgnoreCase)) continue;
                software.DeleteSubKeyTree(name, false);
                log("    HKCU\\Software\\" + name);
            }

            string exe = "\"" + Path.Combine(installDir, Product.ExeName) + "\"";
            using (RegistryKey run = software.OpenSubKey(RunKey, true))
            {
                string command = run == null ? null : run.GetValue(DownloadsRunValue) as string;
                if (command == null || !command.StartsWith(exe, StringComparison.OrdinalIgnoreCase)) return;
                run.DeleteValue(DownloadsRunValue, false);
                log("    HKCU\\Software\\" + RunKey + " : " + DownloadsRunValue);
            }
        }

        private static void DeleteTask(WorkContext c)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                    "/Delete /TN \"" + Product.TaskName + "\" /F");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    if (p == null) return;
                    string err = p.StandardError.ReadToEnd();
                    p.StandardOutput.ReadToEnd();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return; }

                    // Код 1 — «задача не найдена»: автозапуск просто не включали, это не сбой.
                    if (p.ExitCode != 0 && p.ExitCode != 1)
                        c.Log("    schtasks: " + p.ExitCode + " " + err.Trim());
                }
            }
            catch (Exception ex)
            {
                c.Log("    " + ex.Message);
            }
        }

        private static void DeleteFileQuiet(string path, WorkContext c)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
                File.Delete(path);
                c.Log("    " + path);
            }
            catch (Exception ex)
            {
                c.Log("    " + path + ": " + ex.Message);
            }
        }

        public static void DeleteTree(string dir, WorkContext c)
        {
            if (!Directory.Exists(dir)) return;

            string[] files;
            try { files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories); }
            catch { files = new string[0]; }

            foreach (string f in files)
            {
                try
                {
                    File.SetAttributes(f, FileAttributes.Normal);
                    File.Delete(f);
                }
                catch
                {
                    // Занятый файл не повод оставлять всё остальное: помечаем на удаление
                    // при следующей загрузке (нужны права администратора) и идём дальше.
                    if (Native.MoveFileEx(f, null, Native.MoveFileDelayUntilReboot))
                        c.Log(L.S("    будет удалён после перезагрузки: ", "    will be deleted after a reboot: ") + f);
                    else
                        c.Log(L.S("    не удалось удалить: ", "    could not delete: ") + f);
                }
            }

            try { Directory.Delete(dir, true); }
            catch
            {
                if (Native.MoveFileEx(dir, null, Native.MoveFileDelayUntilReboot))
                    c.Log(L.S("    папка будет удалена после перезагрузки: ", "    the folder will be removed after a reboot: ") + dir);
                else
                    c.Log(L.S("    не удалось удалить папку: ", "    could not remove the folder: ") + dir);
            }
        }
    }
}
