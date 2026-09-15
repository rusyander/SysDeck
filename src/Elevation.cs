// SysDeck — повышение прав по требованию (asInvoker + элевированный помощник)
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace SysDeck
{
    // Задание для элевированного помощника. Полей намеренно мало и все они простые:
    // помощник НЕ доверяет содержимому файла и использует его только как фильтр поверх
    // каталога, который строит сам (см. Dispatch).
    [DataContract]
    public class ElevJob
    {
        [DataMember] public string Kind;
        [DataMember] public string Arg;
        [DataMember] public string[] Items;
        [DataMember] public int Number;
        [DataMember] public bool Flag;
        [DataMember] public string DataDir;
        [DataMember] public bool En;
    }

    [DataContract]
    public class ElevResult
    {
        [DataMember] public bool Ok;
        [DataMember] public string Message;
        [DataMember] public long Freed;
        [DataMember] public int Count;
        [DataMember] public int Errors;
        [DataMember] public bool Cancelled;
        [DataMember] public bool Declined;      // пользователь закрыл окно UAC
        [DataMember] public string[] Lines;
    }

    // ================================================================== //
    //  Права администратора поднимаются под операцию, а не под приложение.
    //
    //  Раньше манифест требовал requireAdministrator: одно окно UAC при запуске — и дальше
    //  всё приложение целиком работало с максимальными правами. Для чистильщика это худший
    //  из возможных вариантов: любая ошибка в разборе пути, в правиле winapp2 или в чужом
    //  файле правил исполнялась бы с правами, которых у неё быть не должно.
    //
    //  Теперь окно стартует с обычными правами пользователя, а операции, которым админ
    //  действительно нужен (Standby Memory, DriverStore, DISM, SFC, сброс Windows Update,
    //  системные остатки обновлений, «Windows: лишнее», задача автозапуска), уходят в
    //  отдельный процесс — то же приложение с ключом --elevated-job. Одно окно UAC на
    //  операцию; отказ пользователя — обычный, предусмотренный исход, а не сбой.
    //
    //  Если приложение УЖЕ запущено с правами (его запустил планировщик при входе в систему,
    //  либо пользователь сам выбрал «перезапустить от администратора»), помощник не нужен:
    //  работа выполняется на месте, и никакого окна UAC не появляется вовсе.
    //
    //  Фоновая работа (умное ускорение по порогу RAM, автоочистка по таймеру) окно UAC
    //  не показывает НИКОГДА: всплывающий запрос прав посреди чужой работы — это ровно то
    //  поведение, за которое чистильщики и не любят. Такая операция либо выполняется без
    //  повышения, либо честно пропускается (см. AllowPrompt у вызывающего кода).
    // ================================================================== //
    public static partial class Elevation
    {
        public const string JobSwitch = "--elevated-job";

        private static int _cached = -1;

        public static bool IsElevated
        {
            get
            {
                if (_cached >= 0) return _cached == 1;
                bool ok = false;
                try
                {
                    using (WindowsIdentity id = WindowsIdentity.GetCurrent())
                        ok = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { ok = false; }
                _cached = ok ? 1 : 0;
                return ok;
            }
        }

        private static string _profile;

        // Лежит ли путь внутри профиля пользователя. Всё, что внутри, чистится без всяких
        // прав — это его собственные файлы; всё, что снаружи (C:\Windows, ProgramData,
        // остатки обновлений в корне диска), требует помощника.
        public static bool PathInUserProfile(string path)
        {
            if (string.IsNullOrEmpty(path)) return true;
            if (_profile == null)
            {
                try { _profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'); }
                catch { _profile = ""; }
            }
            if (_profile.Length == 0) return false;
            string p = path.TrimEnd('\\');
            if (string.Equals(p, _profile, StringComparison.OrdinalIgnoreCase)) return true;
            return p.StartsWith(_profile + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // Нужны ли категории права администратора. Живёт здесь, а не в окне, потому что тот же
        // вопрос задаёт тихий режим /auto, у которого никакого окна нет.
        public static bool CategoryNeedsAdmin(CleanCategory c)
        {
            if (c == null) return false;
            if (c.Kind == "driverstore" || c.Kind == "winsxs") return true;
            if (c.Targets != null)
                foreach (CleanTarget t in c.Targets)
                    if (t.Enabled && !t.Guarded && !PathInUserProfile(t.Path)) return true;
            return false;
        }

        // Половина категории, которую можно вычистить без всяких прав: цели внутри профиля
        // пользователя плюс Корзина — она всегда его собственная. Ключи целей, ради которых
        // и запрашиваются права, уходят в adminKeys. Возвращается КОПИЯ: список категорий
        // принадлежит вызывающему, и портить его нельзя. null = чистить без прав нечего.
        // Раньше делили целыми категориями, и одна папка в ProgramData утаскивала под UAC
        // весь «Кэш приложений» — десятки гигабайт, лежащих в профиле и прав не требующих.
        public static CleanCategory UserPartOf(CleanCategory c, List<string> adminKeys)
        {
            CleanCategory u = new CleanCategory();
            u.Id = c.Id; u.Title = c.Title; u.Desc = c.Desc;
            u.Recommended = c.Recommended;
            u.RecycleBin = c.RecycleBin; u.BinEnabled = c.BinEnabled;
            foreach (CleanTarget t in c.Targets)
            {
                if (t.Enabled && !t.Guarded && !PathInUserProfile(t.Path))
                {
                    adminKeys.Add(Engine.TargetKey(c, t));
                    continue;
                }
                u.Targets.Add(t);
            }
            if (u.Targets.Count == 0 && !(u.RecycleBin && u.BinEnabled)) return null;
            return u;
        }

        // Человеческое название работы — для окна UAC пользователь его не увидит, но увидит
        // в подписи страницы: «жду подтверждения прав: очистка системных папок».
        public static string JobTitle(string kind)
        {
            switch (kind)
            {
                case "clean": return Tr.S("очистка системных папок", "cleaning system folders");
                case "standby": return Tr.S("очистка Standby Memory", "purging Standby Memory");
                case "ram": return Tr.S("сброс памяти", "resetting memory");
                case "ramagent": return Tr.S("права для вкладки «Память»", "rights for the Memory page");
                case "tool": return Tr.S("системный инструмент", "system tool");
                case "debloat": return Tr.S("изменение состава Windows", "changing Windows components");
                case "autostart": return Tr.S("задача автозапуска", "the autostart task");
                case "kill": return Tr.S("завершение процессов", "terminating processes");
                case "foldersize": return Tr.S("быстрый режим «Размеров папок»", "fast mode of Folder sizes");
                case "firewall": return Tr.S("входящие соединения торрентов", "incoming torrent connections");
                case "hudtask": return Tr.S("оверлей с правами администратора", "the overlay with administrator rights");
                case "afterburner": return Tr.S("профиль MSI Afterburner", "the MSI Afterburner profile");
                case "nvlimit": return Tr.S("ограничитель кадров драйвера NVIDIA", "the NVIDIA driver frame limiter");
                case "perflog": return Tr.S("подсчёт кадров (FPS) без прав администратора", "frame counting (FPS) without administrator rights");
                case "rebrand": return Tr.S("перенос автозапуска на новое имя программы", "moving autostart to the new program name");
                case "toolkit": return Tr.S("скрипт обслуживания Windows", "a Windows maintenance script");
            }
            return kind;
        }

        // ---------- Сторона окна ----------

        // progress получает строки состояния помощника, cancel опрашивается на отмену.
        // Возврат никогда не null: отказ от UAC — это Ok=false, Declined=true.
        public static ElevResult Run(Engine engine, ElevJob job, Action<string> progress, Func<bool> cancel)
        {
            if (job == null) return Fail("no job");
            if (job.DataDir == null) job.DataDir = engine != null ? engine.DataDir : Engine.DefaultDataDir();
            job.En = Tr.En;

            // Права уже есть — помощник не нужен, работаем на месте и без окна UAC.
            if (IsElevated) return Dispatch(engine, job, progress, cancel);

            string dir = null;
            try
            {
                dir = Path.Combine(Path.GetTempPath(), "wpc-elev-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                WriteJson(Path.Combine(dir, "job.json"), job);
                string progressPath = Path.Combine(dir, "progress.log");
                string cancelPath = Path.Combine(dir, "cancel.flag");
                string resultPath = Path.Combine(dir, "result.json");
                File.WriteAllText(progressPath, "");

                ProcessStartInfo psi = new ProcessStartInfo(Application.ExecutablePath,
                                                            JobSwitch + " \"" + dir + "\"");
                psi.UseShellExecute = true;      // обязателен для verb=runas
                psi.Verb = "runas";
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Process p;
                try { p = Process.Start(psi); }
                catch (Win32Exception ex)
                {
                    // 1223 = ERROR_CANCELLED: пользователь закрыл окно UAC. Это не ошибка
                    // приложения, и говорить о ней нужно спокойно, а не «не удалось».
                    ElevResult d = Fail(ex.NativeErrorCode == 1223
                        ? Tr.S("Запрос прав администратора отклонён.", "The administrator prompt was declined.")
                        : ex.Message);
                    d.Declined = ex.NativeErrorCode == 1223;
                    return d;
                }
                if (p == null) return Fail(Tr.S("не удалось запустить помощника", "failed to start the helper"));

                long sent = 0;
                bool cancelWritten = false;
                using (p)
                {
                    while (!p.WaitForExit(200))
                    {
                        sent = PumpProgress(progressPath, sent, progress);
                        if (!cancelWritten && cancel != null && cancel())
                        {
                            try { File.WriteAllText(cancelPath, "1"); cancelWritten = true; }
                            catch { }
                        }
                    }
                    PumpProgress(progressPath, sent, progress);

                    ElevResult r = ReadJson<ElevResult>(resultPath);
                    if (r == null)
                    {
                        // Помощник умер, не дописав результат: код возврата — единственное,
                        // что о нём известно, и молчать об этом нельзя.
                        r = Fail(Tr.S("помощник завершился с кодом ", "the helper exited with code ") + p.ExitCode);
                    }
                    return r;
                }
            }
            catch (Exception ex) { return Fail(ex.Message); }
            finally
            {
                if (dir != null) try { Directory.Delete(dir, true); } catch { }
            }
        }

        // Помощник пишет строки состояния в файл, окно дочитывает появившееся с прошлого раза:
        // так подпись страницы живёт и во время долгой работы (DISM идёт минутами).
        private static long PumpProgress(string path, long from, Action<string> progress)
        {
            if (progress == null) return from;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length <= from) return from;
                    fs.Seek(from, SeekOrigin.Begin);
                    byte[] buf = new byte[fs.Length - from];
                    int read = fs.Read(buf, 0, buf.Length);
                    string text = Encoding.UTF8.GetString(buf, 0, read);
                    foreach (string line in text.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                        progress(line);
                    return from + read;
                }
            }
            catch { return from; }
        }

        private static ElevResult Fail(string message)
        {
            ElevResult r = new ElevResult();
            r.Ok = false;
            r.Message = message;
            return r;
        }

        // ---------- Сторона помощника ----------

        // Точка входа дочернего процесса: Program.Main отдаёт сюда управление до создания окна.
        public static int Execute(string dir)
        {
            string resultPath = Path.Combine(dir, "result.json");
            ElevResult r;
            StreamWriter log = null;
            try
            {
                ElevJob job = ReadJson<ElevJob>(Path.Combine(dir, "job.json"));
                if (job == null) return 2;
                Tr.En = job.En;

                // Папка данных передаётся заданием: элевированный процесс запускается через
                // службу AppInfo и переменные окружения родителя не наследует, а писать
                // историю и лог очистки он обязан туда же, куда пишет окно.
                if (!string.IsNullOrEmpty(job.DataDir))
                    Environment.SetEnvironmentVariable("SYSDECK_DATA_DIR", job.DataDir);

                string cancelPath = Path.Combine(dir, "cancel.flag");
                log = new StreamWriter(new FileStream(Path.Combine(dir, "progress.log"),
                                                      FileMode.Create, FileAccess.Write, FileShare.ReadWrite));
                log.AutoFlush = true;
                StreamWriter logRef = log;

                Engine engine = new Engine();
                r = Dispatch(engine, job,
                             delegate(string s) { try { logRef.WriteLine(s); } catch { } },
                             delegate { return File.Exists(cancelPath); });
            }
            catch (Exception ex) { r = Fail(ex.Message); }
            finally { if (log != null) try { log.Dispose(); } catch { } }

            try { WriteJson(resultPath, r); }
            catch { return 3; }
            return r != null && r.Ok ? 0 : 1;
        }

        // Единственное место, где задание превращается в работу. Выполняется и в помощнике,
        // и прямо в окне, когда права уже есть, — поэтому оно одно на оба случая.
        private static ElevResult Dispatch(Engine e, ElevJob job, Action<string> log, Func<bool> cancel)
        {
            ElevResult r = new ElevResult();
            if (log == null) log = delegate { };
            if (cancel == null) cancel = delegate { return false; };
            switch (job.Kind)
            {
                case "standby":
                    {
                        Engine.MemResult mr = e.PurgeStandby();
                        r.Ok = mr.Ok; r.Message = mr.Message; r.Freed = mr.FreedBytes;
                        return r;
                    }
                // Разовый сброс памяти: тем же путём, что и всё остальное, — когда права у
                // окна уже есть либо резидентный помощник почему-то не поднялся.
                case "ram":
                    {
                        RamAction a = job.Flag ? e.RamTrimProcesses(RamPidList(job.Arg))
                                               : e.RamRunEmpty(job.Number);
                        r.Ok = a.Ok; r.Freed = a.Freed; r.Count = a.Count; r.Errors = a.Denied; r.Message = a.Message;
                        return r;
                    }
                case "ramagent":
                    return RamAgentLoop(e, job, log, cancel);
                case "kill":
                    {
                        List<int> pids = new List<int>();
                        if (job.Items != null)
                            foreach (string s in job.Items)
                            {
                                int pid;
                                if (int.TryParse(s, out pid)) pids.Add(pid);
                            }
                        long freed;
                        r.Count = e.TerminateMany(pids, out freed, log, cancel);
                        r.Freed = freed;
                        r.Ok = r.Count > 0 || pids.Count == 0;
                        return r;
                    }
                case "autostart":
                    {
                        string err = e.ApplyAutostart(job.Flag);
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                // Задача Планировщика «Размеров папок» с наивысшими правами: создать её может только процесс
                // с правами, а запускать потом — сам пользователь, без окна UAC. Всё остальное (ключ Run,
                // остановка и запуск фонового режима) окно делает само, без прав.
                case "foldersize":
                    {
                        if (job.Flag)
                        {
                            string err = FolderSize.FsAutoStart.CreateScheduledTask();
                            r.Ok = err == null; r.Message = err;
                        }
                        else r.Ok = FolderSize.FsAutoStart.RemoveScheduledTask();
                        return r;
                    }
                // Задача оверлея с правами: только Flag. Путь exe помощник берёт у себя, не из файла задания.
                // Переезд со старого имени: задачи Планировщика и правило брандмауэра. Из задания не читается ничего —
                // помощник сам находит старые задачи этой копии и пересоздаёт их на свой exe.
                // Страница «Скрипты», пункты с задачами SYSTEM и HKLM. Из задания читаются id пункта каталога (только Admin) и
                // параметры, которые нормализуются по каталогу; файлы скриптов помощник берёт из своего exe, а не с диска.
                // Number: 0 установить/сохранить, 1 удалить, 2 включить задачи, 3 выключить.
                case "toolkit":
                    {
                        // Arg — id через запятую (одно окно UAC на все отмеченные пункты с правами), Items[i] — параметры i-го.
                        // Только пункты каталога с Admin: помощник не выполняет с правами то, что окно может само.
                        string[] ids = (job.Arg ?? "").Split(',');
                        Toolkit.TkEnv env = Toolkit.TkEnv.Real();
                        List<string> tkLog = new List<string>();
                        List<string> errs = new List<string>();
                        for (int i = 0; i < ids.Length; i++)
                        {
                            Toolkit.TkItem item = Toolkit.TkCatalog.Find(ids[i]);
                            if (item == null || !item.Admin) { errs.Add("toolkit: " + ids[i]); continue; }
                            Dictionary<string, string> values = Toolkit.TkEngine.Unpack(job.Items != null && job.Items.Length > i ? job.Items[i] : "");
                            tkLog.Add(item.Title);
                            string err = job.Number == 1 ? Toolkit.TkEngine.Remove(item, env, tkLog)
                                       : job.Number == 2 || job.Number == 3 ? Toolkit.TkEngine.SetEnabled(item, values, env, job.Number == 2, tkLog)
                                       : Toolkit.TkEngine.Install(item, values, env, tkLog);
                            if (err != null) errs.Add(item.Title + ": " + err);
                        }
                        r.Ok = errs.Count == 0; r.Message = r.Ok ? null : string.Join("; ", errs.ToArray()); r.Lines = tkLog.ToArray();
                        return r;
                    }
                case "rebrand":
                    {
                        string err = Rebrand.MigrateElevated(e);
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                case "hudtask":
                    {
                        if (job.Flag)
                        {
                            string err = Capture.HudLauncher.CreateTask();
                            r.Ok = err == null; r.Message = err;
                        }
                        else r.Ok = Capture.HudLauncher.RemoveTask();
                        return r;
                    }
                // Группа «Пользователи журналов производительности» для подсчёта кадров: из задания не читается ничего —
                // добавляется пользователь, вошедший в этот сеанс, а не имя из файла (иначе файл добавил бы кого угодно).
                case "perflog":
                    {
                        string err = Capture.HudPerfLog.AddInteractiveUser();
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                // Профиль Afterburner: из задания берётся только номер слота 1–5; путь к Afterburner помощник находит сам
                // в Program Files, файлы cfg — только в его папке Profiles.
                case "afterburner":
                    {
                        string err = job.Number >= 1 && job.Number <= Afterburner.MaxSlot ? Afterburner.Apply(job.Number) : "slot";
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                // Предел кадров драйвера NVIDIA: из задания — число кадров 0–1000 и имя exe без пути (пусто — общий
                // профиль). Имя проверяет NvFrameLimit.ValidExe: путь или не-.exe отклоняются.
                case "nvlimit":
                    {
                        string exe = string.IsNullOrEmpty(job.Arg) ? null : NvFrameLimit.ValidExe(job.Arg);
                        string err = job.Number < 0 || job.Number > NvFrameLimit.MaxFps || (!string.IsNullOrEmpty(job.Arg) && exe == null)
                            ? "bad job" : NvFrameLimit.Set(exe, job.Number);
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                // Правило брандмауэра для торрентов: из задания берётся только Flag (открыть/закрыть). Путь программы
                // помощник знает сам — Arg и Items здесь не читаются, иначе файл задания открыл бы входящие чужому exe.
                case "firewall":
                    {
                        string err = e.FirewallApply(job.Flag);
                        r.Ok = err == null; r.Message = err;
                        return r;
                    }
                case "tool":
                    {
                        List<string> lines = new List<string>();
                        r.Ok = e.ToolRun(job.Arg, delegate(string s) { lines.Add(s); log(s); }, cancel);
                        r.Lines = lines.ToArray();
                        return r;
                    }
                case "debloat":
                    {
                        List<string> lines = new List<string>();
                        List<DebloatItem> items = e.DebloatCatalog();
                        // Задание называет пункты по идентификатору, а сам пункт помощник
                        // берёт из СВОЕГО каталога: подменить состав операции через файл
                        // задания нельзя даже теоретически.
                        Dictionary<string, bool> want = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        if (job.Items != null) foreach (string id in job.Items) want[id] = true;
                        List<DebloatItem> picked = new List<DebloatItem>();
                        foreach (DebloatItem it in items) if (want.ContainsKey(it.Id)) picked.Add(it);
                        e.ResetDebloatCancel();
                        e.DebloatDetect(picked, log);
                        foreach (DebloatItem it in picked)
                        {
                            if (cancel()) { r.Cancelled = true; break; }
                            StringBuilder sb = new StringBuilder();
                            string res = e.DebloatApply(it, job.Number, sb);
                            if (sb.Length > 0) lines.Add(sb.ToString().TrimEnd());
                            if (!string.IsNullOrEmpty(res)) lines.Add(it.Title + ": " + res);
                            log(it.Title);
                            r.Count++;
                        }
                        r.Lines = lines.ToArray();
                        r.Ok = !r.Cancelled;
                        return r;
                    }
                case "clean":
                    {
                        List<CleanCategory> cats = PickCategories(e, job);
                        if (cats.Count == 0) { r.Ok = true; return r; }
                        Thread pump = StartStatusPump(e, log);
                        try
                        {
                            CleanResult cr = e.CleanCategories(cats);
                            r.Ok = true;
                            r.Freed = cr.Freed; r.Count = cr.FilesDeleted; r.Errors = cr.Errors;
                            r.Cancelled = cr.Cancelled;
                            r.Lines = cr.Log != null ? cr.Log.ToArray() : null;
                        }
                        finally { if (pump != null) pump.Abort(); }
                        return r;
                    }
            }
            return Fail("unknown job kind: " + job.Kind);
        }

        // Категории помощник строит САМ и оставляет только те, что названы в задании.
        // Пути из файла задания не используются вовсе: то, чего нет в собственном каталоге
        // приложения, удалено не будет, чем бы файл задания ни оказался.
        internal static List<CleanCategory> PickCategories(Engine e, ElevJob job)
        {
            List<CleanCategory> picked = new List<CleanCategory>();

            // Пункт задания — это либо «id категории» (нужна целиком), либо «id	ключ	ключ»
            // с перечнем тех целей, ради которых права и запрашивались. Ключ — то же
            // Engine.TargetKey, что и в окне, поэтому выдуманный ключ просто ни с чем не
            // совпадёт: перечень умеет ТОЛЬКО сужать собственный каталог помощника.
            Dictionary<string, Dictionary<string, bool>> wantCat =
                new Dictionary<string, Dictionary<string, bool>>(StringComparer.OrdinalIgnoreCase);
            if (job.Items != null)
                foreach (string item in job.Items)
                {
                    if (string.IsNullOrEmpty(item)) continue;
                    string[] parts = item.Split('	');
                    Dictionary<string, bool> keys = null;
                    if (parts.Length > 1)
                    {
                        keys = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 1; i < parts.Length; i++)
                            if (parts[i].Length > 0) keys[parts[i]] = true;
                    }
                    wantCat[parts[0]] = keys;
                }
            if (wantCat.Count == 0) return picked;

            Dictionary<string, bool> wantDriver = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(job.Arg))
                foreach (string s in job.Arg.Split('|'))
                    if (s.Length > 0) wantDriver[s] = true;

            foreach (CleanCategory c in e.BuildCleanCategories())
            {
                Dictionary<string, bool> only;
                if (!wantCat.TryGetValue(c.Id, out only)) continue;
                // Снятые пользователем галочки «Состава» лежат в конфиге, и помощник читает
                // тот же конфиг: выбор переносить через задание не нужно.
                foreach (CleanTarget t in c.Targets)
                    t.Enabled = !e.IsTargetOff(Engine.TargetKey(c, t));
                if (only != null)
                {
                    // Пользовательскую половину категории и Корзину окно вычистило само,
                    // своими правами. Помощнику остаётся ровно то, что вне профиля.
                    c.RecycleBin = false;
                    List<CleanTarget> keep = new List<CleanTarget>();
                    foreach (CleanTarget t in c.Targets)
                        if (only.ContainsKey(Engine.TargetKey(c, t))) keep.Add(t);
                    c.Targets = keep;
                    if (keep.Count == 0) continue;
                }
                if (c.Drivers != null)
                {
                    string err;
                    List<DriverPackage> old = e.OldDriverPackages(out err);
                    c.Drivers.Clear();
                    if (old != null)
                        foreach (DriverPackage d in old)
                            if (wantDriver.ContainsKey(d.Published ?? "")) { d.Enabled = true; c.Drivers.Add(d); }
                }
                picked.Add(c);
            }
            return picked;
        }

        // У очистки нет построчного колбэка — она пишет этап в DiskStatus. Поток-опросчик
        // превращает это в те же строки состояния, что и у остальных работ.
        private static Thread StartStatusPump(Engine e, Action<string> log)
        {
            Thread t = new Thread(delegate()
            {
                string last = null;
                while (true)
                {
                    string s = e.DiskStatus;
                    if (!string.IsNullOrEmpty(s) && s != last) { last = s; log(s); }
                    Thread.Sleep(300);
                }
            });
            t.IsBackground = true;
            t.Start();
            return t;
        }

        // Резидентный помощник вкладки «Память» живёт по своим правилам (см. Elevation.Ram.cs),
        // но файлы задания и ответа пишет тем же сериализатором — иначе форматы разъедутся.
        internal static void WriteJobFile(string path, ElevJob job) { WriteJson(path, job); }
        internal static ElevResult ReadResultFile(string path) { return ReadJson<ElevResult>(path); }

        // ---------- JSON ----------
        private static void WriteJson<T>(string path, T value)
        {
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
                ser.WriteObject(fs, value);
        }

        private static T ReadJson<T>(string path) where T : class
        {
            try
            {
                if (!File.Exists(path)) return null;
                DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                    return ser.ReadObject(fs) as T;
            }
            catch { return null; }
        }
    }
}
