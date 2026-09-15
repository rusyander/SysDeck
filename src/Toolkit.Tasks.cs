// SysDeck — страница «Скрипты»: задачи Планировщика — описание в XML, чтение параметров обратно и доступ к Планировщику.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Регистрация идёт через COM-объект Schedule.Service, а не schtasks.exe: он различает «задачи нет» и «нет доступа»
// (задачи SYSTEM обычному пользователю не видны), отдаёт состояние, последний и следующий запуск одним вызовом и не
// требует временного файла. Интервал без Duration в XML — «бесконечно»: так же, как «schtasks /sc MINUTE».
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Xml;

namespace SysDeck.Toolkit
{
    internal sealed class TkTaskSpec
    {
        public string Name, Description, Command, Arguments;
        public bool System;             // SYSTEM с наивысшими правами; иначе текущий пользователь без повышения
        public int RepeatMinutes;       // > 0 — повтор от StartTime
        public bool Daily;              // раз в день в StartTime
        public string StartTime;        // "HH:mm"
        public bool AtLogon, AtBoot;
        public int BootDelaySec;
        public string EventQuery;       // <QueryList>…</QueryList>
        public int EventDelaySec;
        public int Priority = 7;        // 7 — пониженный приоритет процесса, как у schtasks по умолчанию
        public string TimeLimit = "PT30M";
        public int RestartCount;        // перезапуск через минуту после падения
        public bool Enabled = true;
    }

    internal sealed class TkTaskState
    {
        public string Name;
        public bool Exists, Denied, Enabled;
        public int State;               // 1 выключена, 2 в очереди, 3 готова, 4 выполняется
        public DateTime LastRun, NextRun;
        public int LastResult;
        public string Xml;
        public string Error;
    }

    internal interface ITkScheduler
    {
        TkTaskState Query(string name);
        string Register(TkTaskSpec spec);       // null — успех
        string Delete(string name);             // отсутствующая задача — не ошибка
        string SetEnabled(string name, bool on);
        string Run(string name);
    }

    internal static class TkXml
    {
        private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        public static string Build(TkTaskSpec t, DateTime today)
        {
            string account = Environment.UserDomainName + "\\" + Environment.UserName;
            string sid = null;
            try { using (WindowsIdentity id = WindowsIdentity.GetCurrent()) sid = id.User == null ? null : id.User.Value; }
            catch { }
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n");
            sb.Append("<Task version=\"1.4\" xmlns=\"").Append(Ns).Append("\">\r\n");
            sb.Append("  <RegistrationInfo><Author>SysDeck</Author><Description>").Append(Esc(t.Description ?? "")).Append("</Description></RegistrationInfo>\r\n");
            sb.Append("  <Triggers>\r\n");
            if (t.RepeatMinutes > 0 || t.Daily)
            {
                sb.Append("    <").Append(t.Daily ? "CalendarTrigger" : "TimeTrigger").Append(">");
                if (!t.Daily) sb.Append("<Repetition><Interval>PT").Append(t.RepeatMinutes.ToString(CultureInfo.InvariantCulture)).Append("M</Interval><StopAtDurationEnd>false</StopAtDurationEnd></Repetition>");
                sb.Append("<StartBoundary>").Append(Boundary(t.StartTime, today)).Append("</StartBoundary><Enabled>true</Enabled>");
                if (t.Daily) sb.Append("<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>");
                sb.Append("</").Append(t.Daily ? "CalendarTrigger" : "TimeTrigger").Append(">\r\n");
            }
            if (t.AtLogon)
            {
                sb.Append("    <LogonTrigger><Enabled>true</Enabled>");
                if (!t.System) sb.Append("<UserId>").Append(Esc(account)).Append("</UserId>");
                sb.Append("</LogonTrigger>\r\n");
            }
            if (t.AtBoot)
                sb.Append("    <BootTrigger><Enabled>true</Enabled>").Append(Delay(t.BootDelaySec)).Append("</BootTrigger>\r\n");
            if (!string.IsNullOrEmpty(t.EventQuery))
                sb.Append("    <EventTrigger><Enabled>true</Enabled><Subscription>").Append(Esc(t.EventQuery)).Append("</Subscription>")
                  .Append(Delay(t.EventDelaySec)).Append("</EventTrigger>\r\n");
            sb.Append("  </Triggers>\r\n");
            sb.Append("  <Principals><Principal id=\"Author\">");
            if (t.System) sb.Append("<UserId>S-1-5-18</UserId><RunLevel>HighestAvailable</RunLevel>");
            else sb.Append("<UserId>").Append(Esc(string.IsNullOrEmpty(sid) ? account : sid)).Append("</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel>");
            sb.Append("</Principal></Principals>\r\n");
            sb.Append("  <Settings>\r\n");
            sb.Append("    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n");
            sb.Append("    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n");
            sb.Append("    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n");
            sb.Append("    <StartWhenAvailable>true</StartWhenAvailable>\r\n");
            sb.Append("    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>\r\n");
            sb.Append("    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n");
            sb.Append("    <Enabled>").Append(t.Enabled ? "true" : "false").Append("</Enabled>\r\n");
            sb.Append("    <ExecutionTimeLimit>").Append(t.TimeLimit).Append("</ExecutionTimeLimit>\r\n");
            if (t.RestartCount > 0)
                sb.Append("    <RestartOnFailure><Interval>PT1M</Interval><Count>").Append(t.RestartCount.ToString(CultureInfo.InvariantCulture)).Append("</Count></RestartOnFailure>\r\n");
            sb.Append("    <Priority>").Append(t.Priority.ToString(CultureInfo.InvariantCulture)).Append("</Priority>\r\n");
            sb.Append("  </Settings>\r\n");
            sb.Append("  <Actions Context=\"Author\"><Exec><Command>").Append(Esc(t.Command)).Append("</Command>");
            if (!string.IsNullOrEmpty(t.Arguments)) sb.Append("<Arguments>").Append(Esc(t.Arguments)).Append("</Arguments>");
            sb.Append("</Exec></Actions>\r\n");
            sb.Append("</Task>\r\n");
            return sb.ToString();
        }

        private static string Esc(string s) { return SecurityElement.Escape(s ?? ""); }

        private static string Delay(int sec)
        {
            return sec > 0 ? "<Delay>PT" + sec.ToString(CultureInfo.InvariantCulture) + "S</Delay>" : "";
        }

        // Граница — сегодня в заданное время по местным часам: и повтор, и ежедневный запуск отсчитываются от неё.
        private static string Boundary(string hhmm, DateTime today)
        {
            int h, m;
            if (!TkParam.TryTime(hhmm, out h, out m)) { h = 0; m = 0; }
            return new DateTime(today.Year, today.Month, today.Day, h, m, 0).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        }

        // Что задача делает сейчас: интервал (минуты), время отсчёта, вход, загрузка, событие, аргументы. Для показа и
        // чтобы параметры на странице совпадали с Windows, даже если задачу меняли в Планировщике руками.
        public static Dictionary<string, string> Read(string xml)
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(xml)) return d;
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.LoadXml(xml);
                XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
                ns.AddNamespace("t", Ns);
                XmlNode interval = doc.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:Repetition/t:Interval", ns)
                                   ?? doc.SelectSingleNode("//t:Triggers/t:CalendarTrigger/t:Repetition/t:Interval", ns);
                if (interval != null)
                {
                    TimeSpan ts = XmlConvert.ToTimeSpan(interval.InnerText.Trim());
                    d["minutes"] = ((int)ts.TotalMinutes).ToString(CultureInfo.InvariantCulture);
                }
                XmlNode start = doc.SelectSingleNode("//t:Triggers/t:TimeTrigger/t:StartBoundary", ns)
                                ?? doc.SelectSingleNode("//t:Triggers/t:CalendarTrigger/t:StartBoundary", ns);
                if (start != null)
                {
                    string s = start.InnerText.Trim();
                    int tpos = s.IndexOf('T');
                    if (tpos >= 0 && s.Length >= tpos + 6) d["start"] = s.Substring(tpos + 1, 5);
                }
                if (doc.SelectSingleNode("//t:Triggers/t:CalendarTrigger", ns) != null) d["daily"] = "true";
                if (doc.SelectSingleNode("//t:Triggers/t:LogonTrigger", ns) != null) d["logon"] = "true";
                if (doc.SelectSingleNode("//t:Triggers/t:BootTrigger", ns) != null) d["boot"] = "true";
                if (doc.SelectSingleNode("//t:Triggers/t:EventTrigger", ns) != null) d["event"] = "true";
                XmlNode cmd = doc.SelectSingleNode("//t:Actions/t:Exec/t:Command", ns);
                if (cmd != null) d["command"] = cmd.InnerText.Trim();
                XmlNode args = doc.SelectSingleNode("//t:Actions/t:Exec/t:Arguments", ns);
                if (args != null) d["arguments"] = args.InnerText.Trim();
            }
            catch (Exception) { }
            return d;
        }
    }

    // Планировщик через позднее связывание: ссылка на библиотеку типов не нужна, компилятор тот же, что в Windows.
    internal sealed class TkComScheduler : ITkScheduler
    {
        private const int CreateOrUpdate = 6, LogonInteractiveToken = 3, LogonServiceAccount = 5;
        private const int HrNotFound = unchecked((int)0x80070002), HrDenied = unchecked((int)0x80070005);

        private static object Call(object o, string name, params object[] args)
        {
            try { return o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static object Get(object o, string name)
        {
            try { return o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null); }
            catch (TargetInvocationException ex) { throw ex.InnerException ?? ex; }
        }

        private static object Root()
        {
            Type t = Type.GetTypeFromProgID("Schedule.Service");
            if (t == null) throw new InvalidOperationException("Schedule.Service");
            object svc = Activator.CreateInstance(t);
            Call(svc, "Connect");
            return Call(svc, "GetFolder", "\\");
        }

        private static void Release(object o)
        {
            if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
        }

        public TkTaskState Query(string name)
        {
            TkTaskState s = new TkTaskState();
            s.Name = name;
            object folder = null, task = null;
            try
            {
                folder = Root();
                task = Call(folder, "GetTask", name);
                s.Exists = true;
                s.Enabled = (bool)Get(task, "Enabled");
                s.State = Convert.ToInt32(Get(task, "State"), CultureInfo.InvariantCulture);
                s.LastRun = Stamp(Get(task, "LastRunTime"));
                s.NextRun = Stamp(Get(task, "NextRunTime"));
                s.LastResult = Convert.ToInt32(Get(task, "LastTaskResult"), CultureInfo.InvariantCulture);
                s.Xml = Get(task, "Xml") as string;
            }
            catch (COMException ex)
            {
                if (ex.ErrorCode == HrDenied) { s.Exists = true; s.Denied = true; }
                else if (ex.ErrorCode != HrNotFound) s.Error = ex.Message;
            }
            catch (UnauthorizedAccessException) { s.Exists = true; s.Denied = true; }
            catch (System.IO.FileNotFoundException) { }
            catch (Exception ex) { s.Error = ex.Message; }
            finally { Release(task); Release(folder); }
            return s;
        }

        // 30.12.1899 — «никогда» у Планировщика.
        private static DateTime Stamp(object v)
        {
            if (!(v is DateTime)) return DateTime.MinValue;
            DateTime d = (DateTime)v;
            return d.Year < 2000 ? DateTime.MinValue : d;
        }

        public string Register(TkTaskSpec spec)
        {
            object folder = null, task = null;
            try
            {
                folder = Root();
                string xml = TkXml.Build(spec, DateTime.Today);
                task = spec.System
                    ? Call(folder, "RegisterTask", spec.Name, xml, CreateOrUpdate, "SYSTEM", null, LogonServiceAccount, null)
                    : Call(folder, "RegisterTask", spec.Name, xml, CreateOrUpdate, null, null, LogonInteractiveToken, null);
                return null;
            }
            catch (Exception ex) { return spec.Name + ": " + Why(ex); }
            finally { Release(task); Release(folder); }
        }

        public string Delete(string name)
        {
            object folder = null;
            try
            {
                folder = Root();
                Call(folder, "DeleteTask", name, 0);
                return null;
            }
            catch (System.IO.FileNotFoundException) { return null; }
            catch (COMException ex) { return ex.ErrorCode == HrNotFound ? null : name + ": " + Why(ex); }
            catch (Exception ex) { return name + ": " + Why(ex); }
            finally { Release(folder); }
        }

        public string SetEnabled(string name, bool on)
        {
            object folder = null, task = null;
            try
            {
                folder = Root();
                task = Call(folder, "GetTask", name);
                task.GetType().InvokeMember("Enabled", BindingFlags.SetProperty, null, task, new object[] { on });
                return null;
            }
            catch (Exception ex) { return name + ": " + Why(ex); }
            finally { Release(task); Release(folder); }
        }

        public string Run(string name)
        {
            object folder = null, task = null, running = null;
            try
            {
                folder = Root();
                task = Call(folder, "GetTask", name);
                running = Call(task, "Run", (object)null);
                return null;
            }
            catch (Exception ex) { return name + ": " + Why(ex); }
            finally { Release(running); Release(task); Release(folder); }
        }

        private static string Why(Exception ex)
        {
            if (ex is UnauthorizedAccessException || (ex is COMException && ((COMException)ex).ErrorCode == HrDenied))
                return Tr.S("нет доступа (нужны права администратора)", "access denied (administrator rights needed)");
            return ex.Message;
        }
    }
}
