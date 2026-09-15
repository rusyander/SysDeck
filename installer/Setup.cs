using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace WpcSetup
{
    // Установщик и деинсталлятор — одна программа: uninstall.exe в папке установки это
    // побайтовая копия setup.exe. Режим выбирается ключами командной строки, поэтому
    // вторую сборку поддерживать не приходится.
    //
    //   setup.exe                                — мастер установки (по умолчанию для себя)
    //   setup.exe /scope:machine /auto /elevated — продолжение после запроса прав
    //   uninstall.exe /uninstall [/quiet]        — удаление (строки в реестре)
    internal static class SetupProgram
    {
        [STAThread]
        private static int Main(string[] argv)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetupArgs a = SetupArgs.Parse(argv);
            try
            {
                return a.Uninstall ? UninstallFlow.Run(a) : InstallFlow.Run(a);
            }
            catch (SetupCancelled)
            {
                return 1;
            }
            catch (Exception ex)
            {
                if (!a.Quiet)
                    MessageBox.Show(ex.Message, L.S("Ошибка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class SetupArgs
    {
        public bool Uninstall;
        public bool Quiet;
        public bool Confirmed;
        public bool SelfDelete;
        public bool Elevated;
        public bool Auto;
        public bool DeleteData;
        public bool Desktop = true;
        public string Dir;
        public InstallScope Scope = InstallScope.PerUser;
        public bool ScopeGiven;

        public static SetupArgs Parse(string[] argv)
        {
            SetupArgs a = new SetupArgs();
            if (argv == null) return a;

            foreach (string raw in argv)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                string s = raw.Trim();
                if (s.Length > 1 && (s[0] == '/' || s[0] == '-')) s = s.Substring(1);

                string name = s, val = null;
                int sep = s.IndexOfAny(new char[] { ':', '=' });
                if (sep > 0)
                {
                    name = s.Substring(0, sep);
                    val = s.Substring(sep + 1).Trim().Trim('"');
                }

                switch (name.ToLowerInvariant())
                {
                    case "uninstall": case "u": a.Uninstall = true; break;
                    case "quiet": case "silent": case "s": a.Quiet = true; break;
                    case "confirmed": a.Confirmed = true; break;
                    case "selfdel": a.SelfDelete = true; break;
                    case "elevated": a.Elevated = true; break;
                    case "auto": a.Auto = true; break;
                    case "install": break;
                    case "data": a.DeleteData = On(val); break;
                    case "desktop": a.Desktop = On(val); break;
                    case "dir": case "d": if (!string.IsNullOrEmpty(val)) a.Dir = val; break;
                    case "allusers": a.Scope = InstallScope.Machine; a.ScopeGiven = true; break;
                    case "scope":
                        a.Scope = string.Equals(val, "machine", StringComparison.OrdinalIgnoreCase)
                            ? InstallScope.Machine : InstallScope.PerUser;
                        a.ScopeGiven = true;
                        break;
                }
            }
            return a;
        }

        private static bool On(string val)
        {
            if (string.IsNullOrEmpty(val)) return true;
            return val != "0" && !string.Equals(val, "no", StringComparison.OrdinalIgnoreCase)
                              && !string.Equals(val, "false", StringComparison.OrdinalIgnoreCase);
        }

        public static string ScopeText(InstallScope scope)
        {
            return scope == InstallScope.Machine ? "machine" : "user";
        }
    }

    internal static class InstallFlow
    {
        public static int Run(SetupArgs a)
        {
            using (SetupForm f = new SetupForm(a))
            {
                Application.Run(f);
                return f.ExitCode;
            }
        }

        // Установка для всех пользователей — единственное место, где нужны права
        // администратора, поэтому UAC спрашивается только здесь и только по флажку.
        public static bool RelaunchElevated(string dir, bool desktop)
        {
            string args = "/install /auto /elevated /scope:machine /desktop:" + (desktop ? "1" : "0")
                        + " /dir:" + Product.Quote(dir);
            return StartSelf(args, true);
        }

        public static bool StartSelf(string args, bool asAdmin)
        {
            ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath, args);
            psi.UseShellExecute = true;
            if (asAdmin) psi.Verb = "runas";
            try
            {
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
                return true;
            }
            catch
            {
                // Отказ в окне UAC приходит сюда же — это не ошибка, а ответ «нет».
                return false;
            }
        }
    }

    internal static class UninstallFlow
    {
        public static int Run(SetupArgs a)
        {
            string dir = !string.IsNullOrEmpty(a.Dir)
                ? Product.Norm(a.Dir)
                : Product.Norm(AppDomain.CurrentDomain.BaseDirectory);
            InstallScope scope = a.ScopeGiven ? a.Scope : Work.DetectScope(dir);

            // Права спрашиваются до вопроса «удалять ли»: иначе пользователь ответил бы
            // на диалог, а потом всё равно увидел UAC.
            if (scope == InstallScope.Machine && !Product.IsElevated())
            {
                if (a.Elevated)
                {
                    if (!a.Quiet)
                        MessageBox.Show(L.S("Программа установлена для всех пользователей — для удаления нужны права администратора.",
                                            "The program is installed for all users - removing it requires administrator rights."),
                                        L.S("Удаление", "Uninstall"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
                string args = "/uninstall /elevated /scope:machine /dir:" + Product.Quote(dir)
                            + (a.Quiet ? " /quiet" : "") + (a.DeleteData ? " /data:1" : "")
                            + (a.Confirmed ? " /confirmed" : "");
                return InstallFlow.StartSelf(args, true) ? 0 : 1;
            }

            bool deleteData = a.DeleteData;
            if (!a.Quiet && !a.Confirmed)
            {
                using (UninstallDialog d = new UninstallDialog(dir))
                {
                    if (d.ShowDialog() != DialogResult.OK) return 1;
                    deleteData = d.DeleteData;
                }
            }

            // Из удаляемой папки работать нельзя: собственный exe заблокирован, и папка
            // останется. Уходим копией в %TEMP% и доделываем оттуда.
            if (!a.SelfDelete && Product.Inside(Application.ExecutablePath, dir))
                return RelaunchFromTemp(a, dir, scope, deleteData) ? 0 : 1;

            int code = DoWork(a, dir, scope, deleteData);
            if (a.SelfDelete) RemoveTempCopy();
            return code;
        }

        private static int DoWork(SetupArgs a, string dir, InstallScope scope, bool deleteData)
        {
            WorkContext c = new WorkContext();
            c.Dir = dir;
            c.Scope = scope;
            c.DeleteData = deleteData;

            if (a.Quiet)
            {
                c.Host = new QuietHost();
                try { Work.Uninstall(c); return 0; }
                catch (SetupCancelled) { return 1; }
                catch { return 1; }
            }

            using (WorkForm f = new WorkForm(L.S("Удаление SysDeck", "Uninstalling SysDeck"),
                                             L.S("Удаление программы…", "Removing the program..."),
                                             Work.Uninstall, c))
            {
                if (f.ShowDialog() != DialogResult.OK) return 1;
            }

            MessageBox.Show(L.S("SysDeck удалён.", "SysDeck has been removed.")
                                + (deleteData
                                    ? "\r\n" + L.S("Настройки и история тоже удалены.", "Settings and history were removed as well.")
                                    : "\r\n" + L.S("Настройки и история остались в %APPDATA%\\SysDeck.",
                                                   "Settings and history are kept in %APPDATA%\\SysDeck.")),
                            L.S("Удаление завершено", "Uninstall complete"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        private static bool RelaunchFromTemp(SetupArgs a, string dir, InstallScope scope, bool deleteData)
        {
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), "wpc-uninstall-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tmp);
                string copy = Path.Combine(tmp, Product.UninstallExeName);
                File.Copy(Application.ExecutablePath, copy, true);

                string args = "/uninstall /selfdel /confirmed /scope:" + SetupArgs.ScopeText(scope)
                            + " /dir:" + Product.Quote(dir)
                            + (a.Quiet ? " /quiet" : "") + (deleteData ? " /data:1" : "")
                            + (Product.IsElevated() ? " /elevated" : "");

                ProcessStartInfo psi = new ProcessStartInfo(copy, args);
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetTempPath();
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
                return true;
            }
            catch (Exception ex)
            {
                if (!a.Quiet)
                    MessageBox.Show(ex.Message, L.S("Ошибка", "Error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        // Копия деинсталлятора в %TEMP% удаляет сама себя: exe работающего процесса не
        // удалить, поэтому уборку делает cmd, переждав наш выход. В строке ровно две
        // кавычки — cmd.exe разбирает такую правильно.
        private static void RemoveTempCopy()
        {
            try
            {
                string dir = Product.Norm(AppDomain.CurrentDomain.BaseDirectory);
                if (!Product.Inside(dir, Path.GetTempPath())) return;

                ProcessStartInfo psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                    "/c ping -n 5 127.0.0.1 > nul & rd /s /q " + Product.Quote(dir));
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;
                psi.WorkingDirectory = Path.GetTempPath();
                Process p = Process.Start(psi);
                if (p != null) p.Dispose();
            }
            catch { }
        }
    }
}
