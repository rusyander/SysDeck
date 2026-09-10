// Windows Process Cleaner — права администратора для вкладки «Память».
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Чем этот случай отличается от всех остальных повышений прав в приложении.
//
// Обычная операция (очистка папок, DISM, pnputil) — длинная и одиночная: запустили
// помощника, дождались, забрали результат, помощник умер. Вкладка «Память» устроена
// наоборот: сбросов много, каждый занимает доли секунды, и делаются они подряд, пока
// человек смотрит на схему. Окно UAC на каждое нажатие превратило бы вкладку в
// издевательство, а «поднять права всему приложению» — ровно то, от чего приложение и
// ушло: чистильщик не должен работать администратором целиком.
//
// Поэтому здесь — РЕЗИДЕНТНЫЙ помощник. Одно окно UAC на сеанс работы со вкладкой, после
// чего элевированный процесс живёт рядом и выполняет короткие команды через папку в TEMP.
// Умирает он сам: по флагу остановки, по выходу родителя или по бездействию.
//
// Что помощник умеет и чего НЕ умеет — граница проведена намеренно. Он умеет ровно две
// вещи: команды сброса списков памяти и сброс рабочих наборов перечисленных процессов.
// Обе безобидны даже при худшем раскладе: канал команд лежит в TEMP пользователя, и всё,
// чего добьётся тот, кто в него напишет, — очистка кэшей. Завершения процессов в этом
// наборе НЕТ сознательно: «завершить любой pid с правами администратора» — это готовый
// способ погасить антивирус, и такую команду через файл в TEMP принимать нельзя. Убийство
// защищённых процессов идёт прежним путём — отдельным заданием со своим окном UAC.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    // Сторона окна: запуск, отправка команд, остановка.
    public sealed class RamAgent
    {
        public const string CmdEmpty = "empty";
        public const string CmdTrim = "trim";

        private readonly object _lock = new object();
        private string _dir;
        private Process _proc;
        private int _seq;
        private bool _declined;

        // Пользователь закрыл окно UAC. Спрашивать второй раз без его явного нажатия нельзя.
        public bool Declined { get { lock (_lock) return _declined; } }

        public bool Running
        {
            get
            {
                lock (_lock)
                {
                    if (_proc == null) return false;
                    try { return !_proc.HasExited; }
                    catch { return false; }
                }
            }
        }

        // null — помощник поднят. Иначе строка с причиной; Declined отличает отказ от сбоя.
        public string Start(Engine engine)
        {
            lock (_lock)
            {
                if (_proc != null)
                {
                    try { if (!_proc.HasExited) return null; }
                    catch { }
                    Cleanup();
                }
                _declined = false;

                string dir = null;
                try
                {
                    dir = Path.Combine(Path.GetTempPath(), "wpc-ram-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(dir);

                    ElevJob job = new ElevJob();
                    job.Kind = "ramagent";
                    job.Arg = dir;
                    job.Number = Process.GetCurrentProcess().Id;
                    job.DataDir = engine != null ? engine.DataDir : Engine.DefaultDataDir();
                    job.En = Tr.En;
                    Elevation.WriteJobFile(Path.Combine(dir, "job.json"), job);

                    ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                                                                Elevation.JobSwitch + " \"" + dir + "\"");
                    psi.UseShellExecute = true;
                    psi.Verb = "runas";
                    psi.WindowStyle = ProcessWindowStyle.Hidden;
                    _proc = Process.Start(psi);
                    if (_proc == null)
                    {
                        TryDelete(dir);
                        return Tr.S("не удалось запустить помощника", "failed to start the helper");
                    }
                    _dir = dir;
                    return null;
                }
                catch (Win32Exception ex)
                {
                    if (dir != null) TryDelete(dir);
                    _declined = ex.NativeErrorCode == 1223;
                    return _declined
                        ? Tr.S("запрос прав администратора отклонён", "the administrator prompt was declined")
                        : ex.Message;
                }
                catch (Exception ex)
                {
                    if (dir != null) TryDelete(dir);
                    return ex.Message;
                }
            }
        }

        // Синхронная отправка — вызывается только из фонового потока страницы.
        public RamAction Send(string verb, string arg, int timeoutMs)
        {
            RamAction fail = new RamAction();
            string dir;
            Process proc;
            int n;
            lock (_lock)
            {
                if (_proc == null || _dir == null)
                {
                    fail.Message = Tr.S("помощник не запущен", "the helper is not running");
                    return fail;
                }
                dir = _dir;
                proc = _proc;
                n = ++_seq;
            }
            try { if (proc.HasExited) { fail.Message = Tr.S("помощник завершился", "the helper has exited"); return fail; } }
            catch { }

            string stem = n.ToString("D6", CultureInfo.InvariantCulture);
            string cmd = Path.Combine(dir, "cmd-" + stem + ".txt");
            string res = Path.Combine(dir, "res-" + stem + ".json");
            try
            {
                string tmp = cmd + ".tmp";
                File.WriteAllText(tmp, verb + " " + (arg ?? ""));
                File.Move(tmp, cmd);                     // помощник видит команду только целиком
            }
            catch (Exception ex) { fail.Message = ex.Message; return fail; }

            int waited = 0;
            while (waited < timeoutMs)
            {
                if (File.Exists(res))
                {
                    ElevResult r = Elevation.ReadResultFile(res);
                    try { File.Delete(res); } catch { }
                    if (r == null) { fail.Message = Tr.S("пустой ответ помощника", "the helper answered with nothing"); return fail; }
                    RamAction a = new RamAction();
                    a.Ok = r.Ok;
                    a.Freed = r.Freed;
                    a.Count = r.Count;
                    a.Denied = r.Errors;
                    a.Message = r.Message;
                    return a;
                }
                try { if (proc.HasExited) break; }
                catch { break; }
                Thread.Sleep(50);
                waited += 50;
            }
            try { File.Delete(cmd); } catch { }
            fail.Message = Tr.S("помощник не ответил вовремя", "the helper did not answer in time");
            return fail;
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_dir != null)
                {
                    try { File.WriteAllText(Path.Combine(_dir, "stop.flag"), "1"); } catch { }
                    // Ждать нечего: помощник заметит флаг за десятую долю секунды и уберёт
                    // папку сам. Убить его отсюда всё равно нельзя — он элевированный.
                }
                Cleanup();
            }
        }

        private void Cleanup()
        {
            if (_proc != null) { try { _proc.Dispose(); } catch { } _proc = null; }
            _dir = null;
        }

        private static void TryDelete(string dir)
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    public static partial class Elevation
    {
        // ---------- сторона помощника ----------

        // Цикл резидентного помощника. Возврат из него означает завершение процесса.
        internal static ElevResult RamAgentLoop(Engine e, ElevJob job, Action<string> log, Func<bool> cancel)
        {
            ElevResult done = new ElevResult();
            done.Ok = true;
            string dir = job.Arg;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                done.Ok = false;
                done.Message = "no agent dir";
                return done;
            }

            Process parent = null;
            try { if (job.Number > 0) parent = Process.GetProcessById(job.Number); }
            catch { parent = null; }

            string stop = Path.Combine(dir, "stop.flag");
            // Страховка на случай, если окно исчезло, не оставив ни флага, ни следа: помощник
            // с правами администратора не имеет права висеть в системе бесконечно.
            const int IdleLimitMs = 8 * 60 * 60 * 1000;
            int idle = 0;
            int served = 0;

            // Привилегии берутся один раз на весь сеанс, а не на каждую команду.
            Native.EnablePrivilege("SeProfileSingleProcessPrivilege");
            Native.EnablePrivilege("SeIncreaseQuotaPrivilege");
            log("ram agent ready");

            while (true)
            {
                if (cancel() || File.Exists(stop)) break;
                if (parent != null)
                {
                    try { if (parent.HasExited) break; }
                    catch { break; }
                }

                string[] files;
                try { files = Directory.GetFiles(dir, "cmd-*.txt"); }
                catch { break; }

                if (files.Length == 0)
                {
                    Thread.Sleep(80);
                    idle += 80;
                    if (idle > IdleLimitMs) break;
                    continue;
                }
                idle = 0;
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string f in files)
                {
                    RamAgentServe(e, dir, f);
                    served++;
                }
            }

            done.Count = served;
            done.Message = "ram agent finished, served " + served;
            try { Directory.Delete(dir, true); } catch { }
            return done;
        }

        // Список pid из строки задания. Потолок нужен не ради скорости, а чтобы файл команды
        // не мог превратиться в обход всей системы.
        internal static List<int> RamPidList(string arg)
        {
            List<int> pids = new List<int>();
            if (string.IsNullOrEmpty(arg)) return pids;
            foreach (string s in arg.Split(','))
            {
                int pid;
                if (int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pid) && pid > 0)
                    pids.Add(pid);
                if (pids.Count >= 1024) break;
            }
            return pids;
        }

        // internal, а не private: набор допустимых команд — это граница безопасности канала,
        // и проверяется он прогоном настоящих файлов через настоящий обработчик (область «ram»).
        internal static void RamAgentServe(Engine e, string dir, string cmdPath)
        {
            string name = Path.GetFileNameWithoutExtension(cmdPath);         // cmd-000001
            string stem = name.Length > 4 ? name.Substring(4) : name;
            ElevResult r = new ElevResult();
            try
            {
                string line = File.ReadAllText(cmdPath).Trim();
                try { File.Delete(cmdPath); } catch { }

                int sp = line.IndexOf(' ');
                string verb = sp < 0 ? line : line.Substring(0, sp);
                string arg = sp < 0 ? "" : line.Substring(sp + 1).Trim();

                if (verb == RamAgent.CmdEmpty)
                {
                    int what;
                    if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out what)
                        || !Engine.RamCommandKnown(what))
                    {
                        r.Message = "bad command";
                    }
                    else
                    {
                        RamAction a = e.RamRunEmpty(what);
                        r.Ok = a.Ok; r.Freed = a.Freed; r.Count = a.Count; r.Message = a.Message;
                    }
                }
                else if (verb == RamAgent.CmdTrim)
                {
                    RamAction a = e.RamTrimProcesses(RamPidList(arg));
                    r.Ok = a.Ok; r.Freed = a.Freed; r.Count = a.Count; r.Errors = a.Denied; r.Message = a.Message;
                }
                else r.Message = "unknown verb";
            }
            catch (Exception ex) { r.Ok = false; r.Message = ex.Message; }

            try
            {
                string res = Path.Combine(dir, "res-" + stem + ".json");
                string tmp = res + ".tmp";
                WriteJson(tmp, r);
                File.Move(tmp, res);                    // окно увидит ответ только целиком
            }
            catch { }
        }
    }
}
