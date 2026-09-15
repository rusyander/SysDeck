// SysDeck — тесты описания задачи автозапуска (Engine.AutostartTaskXml).
//
// Проверяется только ТЕКСТ задачи: настоящая задача планировщика здесь не создаётся, не
// изменяется и не удаляется. Причина, по которой это вообще проверяется: задача, созданная
// строкой «schtasks /SC ONLOGON», получала умолчания планировщика — лимит выполнения 72 часа
// (через трое суток трей-приложение просто убивали), запрет старта и остановку на батарее.
// Разбор идёт через XmlDocument, а не поиском подстрок: перестановка полей или другой отступ
// не должны ронять тест, а вот пропавший PT0S — должны.

using System;
using System.Xml;

namespace SysDeck.Tests
{
    internal static class AutostartTests
    {
        private const string Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

        internal static void Run()
        {
            string exe = "C:\\Program Files\\Wpc & Co\\SysDeck.exe";
            string xml = Engine.AutostartTaskXml(exe, true);

            XmlDocument doc = new XmlDocument();
            try { doc.LoadXml(xml); }
            catch (XmlException ex)
            {
                T.Check("the autostart task description is well-formed xml", false, ex.Message);
                return;
            }
            T.Check("the autostart task description is well-formed xml", true);
            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", Ns);

            // Ради чего задача вообще описывается файлом, а не командной строкой.
            T.Eq("the autostart task has no execution time limit",
                 "PT0S", Text(doc, ns, "/t:Task/t:Settings/t:ExecutionTimeLimit"));
            T.Eq("the autostart task is not blocked from starting on battery",
                 "false", Text(doc, ns, "/t:Task/t:Settings/t:DisallowStartIfOnBatteries"));
            T.Eq("the autostart task is not stopped when the machine goes on battery",
                 "false", Text(doc, ns, "/t:Task/t:Settings/t:StopIfGoingOnBatteries"));
            T.Eq("the autostart task is never hard-terminated",
                 "false", Text(doc, ns, "/t:Task/t:Settings/t:AllowHardTerminate"));
            T.Eq("a second copy of the app is not started by the task",
                 "IgnoreNew", Text(doc, ns, "/t:Task/t:Settings/t:MultipleInstancesPolicy"));
            T.Eq("a missed logon still starts the app afterwards",
                 "true", Text(doc, ns, "/t:Task/t:Settings/t:StartWhenAvailable"));
            T.Eq("the task does not wait for the machine to go idle",
                 "false", Text(doc, ns, "/t:Task/t:Settings/t:RunOnlyIfIdle"));
            T.Eq("the task does not wait for a network",
                 "false", Text(doc, ns, "/t:Task/t:Settings/t:RunOnlyIfNetworkAvailable"));
            T.Eq("the task is enabled as created",
                 "true", Text(doc, ns, "/t:Task/t:Settings/t:Enabled"));

            // Запуск при входе в систему, а не по расписанию.
            T.Eq("the autostart task is triggered by a logon",
                 "true", Text(doc, ns, "/t:Task/t:Triggers/t:LogonTrigger/t:Enabled"));
            T.Eq("the logon trigger names the current account",
                 Environment.UserDomainName + "\\" + Environment.UserName,
                 Text(doc, ns, "/t:Task/t:Triggers/t:LogonTrigger/t:UserId"));

            // Приложение стартует свёрнутым в трей: иначе окно выскакивало бы при каждом входе.
            T.Eq("the autostart task starts the app in the tray",
                 "/tray", Text(doc, ns, "/t:Task/t:Actions/t:Exec/t:Arguments"));
            T.Eq("the autostart task runs the executable it was given",
                 exe, Text(doc, ns, "/t:Task/t:Actions/t:Exec/t:Command"));
            T.Check("a special character in the executable path is escaped, not written raw",
                    xml.IndexOf("Wpc &amp; Co", StringComparison.Ordinal) >= 0);

            T.Eq("an elevated task asks for the highest available rights",
                 "HighestAvailable", Text(doc, ns, "/t:Task/t:Principals/t:Principal/t:RunLevel"));
            T.Eq("the task runs with the interactive token, not a stored password",
                 "InteractiveToken", Text(doc, ns, "/t:Task/t:Principals/t:Principal/t:LogonType"));

            // Тот же текст без повышения: в такой форме задачу можно создать и без прав админа.
            XmlDocument plain = new XmlDocument();
            plain.LoadXml(Engine.AutostartTaskXml(exe, false));
            XmlNamespaceManager ns2 = new XmlNamespaceManager(plain.NameTable);
            ns2.AddNamespace("t", Ns);
            T.Eq("a task created without elevation asks for least privilege",
                 "LeastPrivilege", Text(plain, ns2, "/t:Task/t:Principals/t:Principal/t:RunLevel"));

            T.Eq("the scheduled task keeps its stable name",
                 "SysDeck", Engine.AutostartTaskName);
        }

        private static string Text(XmlDocument doc, XmlNamespaceManager ns, string xpath)
        {
            XmlNode n = doc.SelectSingleNode(xpath, ns);
            return n == null ? "<missing>" : n.InnerText;
        }
    }
}
