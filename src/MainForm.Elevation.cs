using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace SysDeck
{
    // Окно и повышение прав: один слой между кнопками и Elevation.
    //
    // Правило, из которого всё остальное следует: окно НЕ показывает запрос прав само по
    // себе. Запрос появляется только там, где человек только что нажал кнопку и ждёт
    // результата. Всё, что приложение делает по своему усмотрению — умное ускорение по
    // порогу RAM, автоочистка по таймеру, работа из трея по расписанию, — обходится
    // имеющимися правами или честно пропускается с объяснением: окно UAC, всплывшее
    // посреди чужой работы, пользователь не связывает ни с чем и просто закрывает.
    public partial class MainForm
    {
        private bool Elevated { get { return Elevation.IsElevated; } }

        // Текст, который видит человек, когда прав не хватило, а спрашивать было нельзя.
        private static string NeedAdminNote()
        {
            return Tr.S("нужны права администратора (фоновая работа их не запрашивает)",
                        "administrator rights are required (background work never prompts)");
        }

        private static string DeclinedNote()
        {
            return Tr.S("запрос прав администратора отклонён", "the administrator prompt was declined");
        }

        // ---------- Standby Memory ----------
        // allowPrompt = операцию начал человек. false — это фон (умное ускорение, таймер).
        private Engine.MemResult PurgeStandbyMaybeElevated(bool allowPrompt, Action<string> progress, Func<bool> cancel)
        {
            if (Elevated) return _engine.PurgeStandby();

            Engine.MemResult mr = new Engine.MemResult();
            if (!allowPrompt)
            {
                mr.Ok = false;
                mr.Message = NeedAdminNote();
                return mr;
            }
            ElevJob job = new ElevJob();
            job.Kind = "standby";
            ElevResult r = Elevation.Run(_engine, job, progress, cancel);
            mr.Ok = r.Ok;
            mr.FreedBytes = r.Freed;
            mr.Message = !string.IsNullOrEmpty(r.Message) ? r.Message
                       : r.Declined ? DeclinedNote()
                       : Tr.S("Standby Memory очищена", "Standby Memory purged");
            return mr;
        }

        // ---------- Инструменты ----------
        private bool ToolRunMaybeElevated(ToolItem t, Action<string> log, Func<bool> cancel)
        {
            if (!t.Admin || Elevated)
                return _engine.ToolRun(t.Id, log, cancel);

            ElevJob job = new ElevJob();
            job.Kind = "tool";
            job.Arg = t.Id;
            ElevResult r = Elevation.Run(_engine, job, log, cancel);
            // Помощник копил вывод инструмента у себя: строки состояния уже показаны по ходу
            // дела, а полный журнал дописываем сюда — иначе он пропал бы вместе с процессом.
            if (r.Lines != null) foreach (string line in r.Lines) log(line);
            if (!r.Ok && r.Declined) log(DeclinedNote());
            return r.Ok;
        }

        // ---------- Очистка диска ----------
        // Категория требует прав, если это действие внешней утилиты (pnputil, DISM) или если
        // хоть одна её включённая цель лежит за пределами профиля пользователя. Кэши браузеров
        // и dev-кэши целиком помещаются в профиль — за них UAC не спрашивают вовсе.
        private static bool CategoryNeedsAdmin(CleanCategory c)
        {
            return Elevation.CategoryNeedsAdmin(c);
        }

        private static bool AnyNeedsAdmin(List<CleanCategory> cats)
        {
            if (cats != null) foreach (CleanCategory c in cats) if (CategoryNeedsAdmin(c)) return true;
            return false;
        }


        // Очистка делится надвое: пользовательская часть идёт на месте и без всяких запросов,
        // системная — одним заданием помощнику. Так человек, чистящий кэш браузера, окна UAC
        // не видит вообще, а увидевший его точно знает, за что именно оно спрашивает.
        private CleanResult CleanCategoriesMaybeElevated(List<CleanCategory> sel, bool allowPrompt,
                                                        Action<string> progress, Func<bool> cancel)
        {
            List<CleanCategory> local = new List<CleanCategory>();
            List<CleanCategory> admin = new List<CleanCategory>();
            List<string> items = new List<string>();
            List<string> drivers = new List<string>();
            foreach (CleanCategory c in sel)
            {
                if (Elevated || !CategoryNeedsAdmin(c)) { local.Add(c); continue; }
                admin.Add(c);

                // Категории-действия (DriverStore, WinSxS) целиком принадлежат системе:
                // делить в них нечего.
                if (!string.IsNullOrEmpty(c.Kind))
                {
                    items.Add(c.Id);
                    if (c.Drivers != null)
                        foreach (DriverPackage d in c.Drivers)
                            if (d.Enabled && !string.IsNullOrEmpty(d.Published)) drivers.Add(d.Published);
                    continue;
                }

                List<string> keys = new List<string>();
                CleanCategory mine = Elevation.UserPartOf(c, keys);
                if (mine != null) local.Add(mine);
                StringBuilder sb = new StringBuilder(c.Id);
                foreach (string k in keys) sb.Append('	').Append(k);
                items.Add(sb.ToString());
            }

            CleanResult total = local.Count > 0 ? _engine.CleanCategories(local) : new CleanResult();
            if (items.Count == 0) return total;

            if (!allowPrompt)
            {
                foreach (CleanCategory c in admin)
                    total.Log.Add("SKIP (admin) " + c.Title + " — " + NeedAdminNote());
                return total;
            }

            ElevJob job = new ElevJob();
            job.Kind = "clean";
            job.Items = items.ToArray();
            job.Arg = string.Join("|", drivers.ToArray());

            ElevResult r = Elevation.Run(_engine, job, progress, cancel);
            total.Freed += r.Freed;
            total.FilesDeleted += r.Count;
            total.Errors += r.Errors;
            if (r.Cancelled) total.Cancelled = true;
            if (r.Lines != null) total.Log.AddRange(r.Lines);
            if (!r.Ok)
            {
                string why = r.Declined ? DeclinedNote() : (r.Message ?? "");
                foreach (CleanCategory c in admin) total.Log.Add("SKIP (admin) " + c.Title + " — " + why);
            }
            return total;
        }

        // ---------- Завершение процессов, которым не хватило прав ----------
        // Разовое задание, а не резидентный помощник: «заверши любой pid от администратора»,
        // лежащее в файле команд, — это готовая лазейка для повышения прав, поэтому в набор
        // команд помощника завершение не входит (см. Elevation.Ram.cs). Список приходит уже
        // отфильтрованным — только те, кто пережил обычную попытку.
        private ElevResult KillElevated(List<int> pids, Action<string> progress, Func<bool> cancel)
        {
            ElevJob job = new ElevJob();
            job.Kind = "kill";
            job.Items = RamPidStrings(pids);
            return Elevation.Run(_engine, job, progress, cancel);
        }

        // ---------- Задача автозапуска ----------
        private string ApplyAutostartMaybeElevated(bool enabled)
        {
            if (Elevated) return _engine.ApplyAutostart(enabled);
            ElevJob job = new ElevJob();
            job.Kind = "autostart";
            job.Flag = enabled;
            ElevResult r = Elevation.Run(_engine, job, null, null);
            if (r.Ok) return null;
            return r.Declined ? DeclinedNote() : (r.Message ?? "schtasks");
        }

        // ---------- «Windows: лишнее» ----------
        private static bool DebloatItemNeedsAdmin(DebloatItem it)
        {
            return it.HasKind("svc") || it.HasKind("feature") || it.HasKind("cap")
                || it.HasKind("task") || it.HasKind("onedrive");
        }

        // Весь набор уходит одним заданием: по окну UAC на каждый из полутора сотен пунктов —
        // это не защита, а издевательство, и человек перестал бы читать, что именно он
        // подтверждает.
        private ElevResult DebloatApplyElevated(int action, List<DebloatItem> items, Action<string> progress, Func<bool> cancel)
        {
            ElevJob job = new ElevJob();
            job.Kind = "debloat";
            job.Number = action;
            List<string> ids = new List<string>();
            foreach (DebloatItem it in items) ids.Add(it.Id);
            job.Items = ids.ToArray();
            return Elevation.Run(_engine, job, progress, cancel);
        }
    }
}
