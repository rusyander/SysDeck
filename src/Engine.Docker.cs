// Windows Process Cleaner — Docker: запуск CLI с таймаутом, поиск vhdx, сжатие диска
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
        // ================= DOCKER =================
        // Отмена и живой статус — как в остальном приложении: раньше у Docker не было ни того,
        // ни другого, и «Сжать диск» на 40 минут выглядел зависанием.
        private volatile bool _cancelDocker;
        public void CancelDockerWork() { _cancelDocker = true; }
        public void ResetDockerCancel() { _cancelDocker = false; _dockerStatus = null; _dockerStep = null; }
        public bool DockerCancelled { get { return _cancelDocker; } }

        private volatile string _dockerStatus;
        private volatile string _dockerStep;
        // Этап длинной операции плюс, если он есть, текущий вывод команды.
        public string DockerStatus
        {
            get
            {
                string step = _dockerStep, cmd = _dockerStatus;
                if (string.IsNullOrEmpty(step)) return cmd;
                return string.IsNullOrEmpty(cmd) ? step : step + "  ·  " + cmd;
            }
        }

        // Сколько ждать конкретную команду. Раньше на ВСЁ было 120 секунд: большой
        // `builder prune` / `system prune` в них не укладывался, docker убивали на середине,
        // а пользователь читал «команда не завершилась за 2 минуты» вместо результата.
        private static int DockerTimeout(string args)
        {
            string a = (args ?? "").ToLowerInvariant();
            if (a.IndexOf("prune", StringComparison.Ordinal) >= 0) return 1800000;
            if (a.StartsWith("version") || a.StartsWith("info")) return 30000;
            if (a.StartsWith("system df") || a.StartsWith("df")) return 120000;
            return 300000;
        }

        // Общий с остальным приложением запуск: потоковое чтение вывода, закрытый stdin,
        // опрос отмены. exit: -1 = CLI не запустился, -2 = не уложился в срок и убит,
        // -3 = остановлено пользователем.
        public string RunCapture(string exe, string args, out int exit)
        {
            return RunCapture(exe, args, DockerTimeout(args), out exit);
        }

        public string RunCapture(string exe, string args, int timeoutMs, out int exit)
        {
            string so;
            string head = exe + " " + args;
            _dockerStatus = head;
            bool ran = RunCapture(exe, args, timeoutMs, out so, out exit, null,
                                  delegate { return _cancelDocker; },
                                  delegate(string line, long ms)
                                  {
                                      string t = (line ?? "").Trim();
                                      if (t.Length > 80) t = t.Substring(0, 80) + "…";
                                      _dockerStatus = head + (t.Length > 0 ? "  ·  " + t : "");
                                  }, null);
            _dockerStatus = null;
            string res = (so ?? "").Trim();
            if (!ran && exit == -1)
                return Tr.S("[ошибка] не удалось запустить ", "[error] failed to start ") + exe
                     + Tr.S("\r\nВозможно, CLI не установлен или отсутствует в PATH.",
                            "\r\nThe CLI may not be installed or is not in PATH.");
            if (exit == RunTimeout)
                res += (res.Length > 0 ? "\r\n" : "")
                     + Tr.S("[ошибка] команда не уложилась в " + (timeoutMs / 60000) + " мин и остановлена",
                            "[error] the command did not finish within " + (timeoutMs / 60000) + " min and was stopped");
            if (exit == RunCancelled)
                res += (res.Length > 0 ? "\r\n" : "") + Tr.S("[остановлено пользователем]", "[stopped by user]");
            return res;
        }

        public string Docker(string args)
        {
            int ec;
            string outp = RunCapture("docker", args, out ec);
            // docker печатает LF; TextBox требует CRLF, иначе строки слипаются
            outp = outp.Replace("\r\n", "\n").Replace("\n", "\r\n");
            return "> docker " + args + "\r\n" + outp + "\r\n";
        }

        // Находит самый большой виртуальный диск Docker (WSL2).
        public string FindDockerVhdx()
        {
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string[] cands = {
                Path.Combine(lad, "Docker\\wsl\\disk\\docker_data.vhdx"),
                Path.Combine(lad, "Docker\\wsl\\data\\ext4.vhdx"),
                Path.Combine(lad, "Docker\\wsl\\main\\ext4.vhdx")
            };
            string best = null; long bestSize = -1;
            foreach (string c in cands)
            {
                try { if (File.Exists(c)) { long s = new FileInfo(c).Length; if (s > bestSize) { bestSize = s; best = c; } } }
                catch { }
            }
            return best;
        }

        private static string Lf(string s) { return s == null ? "" : s.Replace("\r\n", "\n").Replace("\n", "\r\n"); }

        // Что удалить перед сжатием: 0 = ничего, 1 = безопасное (остановленные контейнеры, образы
        // без тега, кэш сборки), 2 = + все неиспользуемые образы, 3 = всё, включая неиспользуемые
        // тома. Раньше кнопка «сжать диск» всегда делала system prune -a --volumes: сжатие этого
        // не требует, а неиспользуемые тома — это базы остановленных проектов, их так не теряют.
        public static string[] DockerPruneCommands(int scope)
        {
            switch (scope)
            {
                case 1: return new string[] { "container prune -f", "image prune -f", "builder prune -a -f" };
                case 2: return new string[] { "container prune -f", "image prune -a -f", "builder prune -a -f" };
                case 3: return new string[] { "system prune -a -f --volumes", "builder prune -a -f" };
                default: return new string[0];
            }
        }

        // ОДНА КНОПКА: удалить выбранное (scope) -> остановить Docker ->
        // сжать vhdx (реально вернуть место Windows) -> перезапустить Docker.
        public string CompactDockerDisk(int scope)
        {
            StringBuilder sb = new StringBuilder();
            int ec;
            // Шаг называется вслух: без этого 40 минут работы выглядели одной строкой
            // «это может занять пару минут». Отмена проверяется на безопасных границах —
            // внутри compact vdisk прерывать нельзя, иначе vhdx останется подключённым.
            _dockerStep = Tr.S("проверяю Docker", "checking Docker");

            // exit -1 = docker.exe не запустился (CLI нет); любой другой ненулевой код —
            // CLI есть, но демон не отвечает: prune невозможен, а сжать диск всё равно можно.
            string ver = RunCapture("docker", "version --format {{.Server.Version}}", out ec);
            if (ec == -1)
                return Tr.S("Docker CLI не найден (docker.exe нет в PATH).", "Docker CLI not found (docker.exe is not in PATH).")
                     + "\r\n" + ver;
            if (ec == 0)
            {
                _dockerStep = Tr.S("считаю занятое место", "measuring usage");
                sb.AppendLine(Tr.S("=== Занято до очистки ===", "=== Usage before cleanup ==="));
                sb.AppendLine(Lf(RunCapture("docker", "system df", out ec)));
                sb.AppendLine();

                // 1) очистка перед сжатием — ровно то, что выбрал пользователь
                string[] cmds = DockerPruneCommands(scope);
                if (cmds.Length > 0)
                {
                    sb.AppendLine(Tr.S("=== Очистка перед сжатием ===", "=== Pruning before compaction ==="));
                    int ci = 0;
                    foreach (string cmd in cmds)
                    {
                        if (_cancelDocker) { sb.AppendLine(Tr.S("[остановлено пользователем]", "[stopped by user]")); return sb.ToString(); }
                        ci++;
                        _dockerStep = Tr.S("очистка ", "pruning ") + ci + "/" + cmds.Length + ": docker " + cmd;
                        sb.AppendLine("> docker " + cmd);
                        sb.AppendLine(Lf(RunCapture("docker", cmd, out ec)));
                    }
                    sb.AppendLine();
                }
            }
            else
            {
                sb.AppendLine(Tr.S("Демон Docker не отвечает — очистка пропущена, будет только сжатие диска.",
                                   "The Docker daemon is not responding — pruning skipped, only the disk will be compacted."));
                sb.AppendLine(Lf(ver));
                sb.AppendLine();
            }

            // 2) сжатие виртуального диска
            string vhdx = FindDockerVhdx();
            long before = 0, after = 0;
            if (vhdx == null)
            {
                sb.AppendLine(Tr.S("Виртуальный диск Docker не найден — сжатие пропущено.",
                                   "Docker virtual disk not found — compaction skipped."));
            }
            else
            {
                try { before = new FileInfo(vhdx).Length; } catch { }
                sb.AppendLine(Tr.S("=== Сжатие диска ===", "=== Compacting disk ==="));
                sb.AppendLine(Tr.S("Диск: ", "Disk: ") + vhdx);
                sb.AppendLine(Tr.S("Размер до сжатия: ", "Size before compaction: ") + FormatBytes(before));
                // остановить процессы Docker Desktop, чтобы освободить файл vhdx
                if (_cancelDocker) { sb.AppendLine(Tr.S("[остановлено пользователем — до остановки Docker]", "[stopped by user — before stopping Docker]")); return sb.ToString(); }
                _dockerStep = Tr.S("останавливаю Docker Desktop и WSL", "stopping Docker Desktop and WSL");
                sb.AppendLine(Tr.S("Остановка Docker Desktop…", "Stopping Docker Desktop…"));
                RunCapture("taskkill", "/F /IM \"Docker Desktop.exe\"", out ec);
                RunCapture("taskkill", "/F /IM com.docker.backend.exe", out ec);
                RunCapture("taskkill", "/F /IM com.docker.build.exe", out ec);
                RunCapture("taskkill", "/F /IM com.docker.dev-envs.exe", out ec);
                sb.AppendLine("> wsl --shutdown");
                RunCapture("wsl", "--shutdown", out ec);
                System.Threading.Thread.Sleep(5000);

                string script = "select vdisk file=\"" + vhdx + "\"\r\n" +
                                "attach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n";
                string scriptPath = Path.Combine(Path.GetTempPath(), "wpc_compact.txt");
                try { File.WriteAllText(scriptPath, script); } catch { }
                _dockerStep = Tr.S("сжимаю диск (прервать уже нельзя)", "compacting the disk (cannot be interrupted)");
                sb.AppendLine("> diskpart compact vdisk …");
                // Сжатие диска на десятки гигабайт идёт дольше двух минут; общий RunCapture с
                // 2-минутным таймаутом убивал diskpart посреди compact, и vhdx оставался
                // подключённым — Docker после этого не стартовал до перезагрузки. Здесь ждём до получаса.
                string dpOut; int dpCode;
                bool dpRan = RunCapture(Path.Combine(Environment.SystemDirectory, "diskpart.exe"), "/s \"" + scriptPath + "\"",
                                        1800000, out dpOut, out dpCode, OemEncoding(), null,
                                        delegate(string line, long ms)
                                        {
                                            string t = (line ?? "").Trim();
                                            _dockerStep = Tr.S("сжимаю диск: ", "compacting the disk: ")
                                                        + (t.Length > 60 ? t.Substring(0, 60) + "…" : t);
                                        }, null);
                if (!string.IsNullOrEmpty(dpOut)) sb.AppendLine(Lf(dpOut.Trim()));
                if (!dpRan)
                    sb.AppendLine(Tr.S("[ошибка] diskpart: ", "[error] diskpart: ") + RunFailText(false, dpCode));
                else if (dpCode != 0)
                    sb.AppendLine(Tr.S("[ошибка] diskpart завершился с кодом ", "[error] diskpart exited with code ") + dpCode);
                try { File.Delete(scriptPath); } catch { }

                after = before;
                try { after = new FileInfo(vhdx).Length; } catch { }
                sb.AppendLine(Tr.S("Размер после сжатия: ", "Size after compaction: ") + FormatBytes(after));
                long freed = before - after;
                sb.AppendLine(Tr.S("✓ Освобождено на диске Windows: ", "✓ Reclaimed on Windows disk: ") +
                              FormatBytes(freed > 0 ? freed : 0));
                if (freed <= 0)
                    sb.AppendLine(Tr.S("(если 0 — полностью закройте Docker Desktop и повторите: файл был занят)",
                                       "(if 0 — fully quit Docker Desktop and retry: the file was locked)"));
            }

            // 3) перезапуск Docker Desktop
            _dockerStep = Tr.S("запускаю Docker Desktop", "starting Docker Desktop");
            sb.AppendLine();
            bool started = StartDockerDesktop();
            sb.AppendLine(started
                ? Tr.S("Docker Desktop запускается…", "Docker Desktop is starting…")
                : Tr.S("Не удалось найти Docker Desktop.exe — запустите Docker вручную.",
                       "Docker Desktop.exe not found — start Docker manually."));
            _dockerStep = null;
            return sb.ToString();
        }

        private bool StartDockerDesktop()
        {
            string[] cands = {
                Path.Combine(_programFiles ?? "", "Docker\\Docker\\Docker Desktop.exe"),
                Path.Combine(_programFilesX86 ?? "", "Docker\\Docker\\Docker Desktop.exe")
            };
            foreach (string c in cands)
            {
                try
                {
                    if (File.Exists(c))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo(c);
                        psi.UseShellExecute = true;
                        using (Process.Start(psi)) { }
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }
    }
}
