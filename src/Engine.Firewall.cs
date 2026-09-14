// Windows Process Cleaner — брандмауэр: правило входящих соединений для торрентов.
// Сборка: build.bat (csc.exe из .NET Framework 4.x компилирует все src\*.cs).
//
// Торрент-сессия слушает TCP только после кнопки «Разрешить входящие»: слушатель без правила вызывает окно Windows
// «Безопасность Windows» (процесс PickerHost), а приложение само окон не показывает. UDP (DHT, UDP-трекеры) окна не
// вызывает — проверено 14.09.2026 на Windows 11 зондом: привязка к 0.0.0.0 с приёмом и одна отправка правил не создали.
//
// Правило добавляет элевированный помощник (задание "firewall"). Путь программы он берёт у СЕБЯ — это тот же exe, — а не
// из файла задания: подменив файл, нельзя открыть входящие чужой программе. Затрагиваются только входящие правила,
// привязанные к этому exe: старые (в том числе запрещающие, оставшиеся от закрытого окна Windows) удаляются, вместо них
// одно разрешающее с постоянным именем. Чтение состояния — через COM HNetCfg.FwPolicy2, права для него не нужны.
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace WindowsProcessCleaner
{
    public enum FirewallInbound { Unknown, None, Allowed, Blocked }

    public partial class Engine
    {
        public const string FirewallRuleName = "Windows Process Cleaner (BitTorrent)";

        // Состояние входящих для exe: запрещающее правило побеждает разрешающее (так работает сам брандмауэр).
        // Unknown — служба брандмауэра недоступна или COM не ответил.
        public static FirewallInbound FirewallState(string exePath)
        {
            if (string.IsNullOrEmpty(exePath)) return FirewallInbound.Unknown;
            try
            {
                Type t = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", false);
                if (t == null) return FirewallInbound.Unknown;
                object policy = Activator.CreateInstance(t);
                object rules = ComGet(policy, "Rules");
                bool allowTcp = false, block = false;
                string want = NormalizeExe(exePath);
                string wantName = FileNameOf(want);
                foreach (object rule in (IEnumerable)rules)
                {
                    string app = ComGet(rule, "ApplicationName") as string;
                    // Имя файла сравнивается дёшево, полный канонический путь — только у совпавших (правил сотни).
                    if (app == null) continue;
                    string appName = FileNameOf(app);
                    if (appName.Length > 0 && wantName.Length > 0 && appName != wantName) continue;
                    if (NormalizeExe(app) != want) continue;
                    if (Convert.ToInt32(ComGet(rule, "Direction"), CultureInfo.InvariantCulture) != 1) continue;   // 1 — входящие
                    if (!Convert.ToBoolean(ComGet(rule, "Enabled"), CultureInfo.InvariantCulture)) continue;
                    int action = Convert.ToInt32(ComGet(rule, "Action"), CultureInfo.InvariantCulture);          // 0 — запрет, 1 — разрешение
                    int protocol = Convert.ToInt32(ComGet(rule, "Protocol"), CultureInfo.InvariantCulture);      // 6 — TCP, 256 — любой
                    if (protocol != 6 && protocol != 256) continue;
                    if (action == 0) block = true;
                    else if (action == 1) allowTcp = true;
                }
                return block ? FirewallInbound.Blocked : allowTcp ? FirewallInbound.Allowed : FirewallInbound.None;
            }
            catch { return FirewallInbound.Unknown; }
        }

        // Выполняется с правами администратора (задание "firewall"). allow — открыть, иначе закрыть. null — успех.
        public string FirewallApply(bool allow)
        {
            return FirewallApply(allow, Application.ExecutablePath);
        }

        internal static string FirewallApply(bool allow, string exePath)
        {
            exePath = Native.CanonicalPath(exePath);
            string netsh = Path.Combine(Environment.SystemDirectory, "netsh.exe");
            string so;
            int code;
            // Удаление по program= затрагивает только правила этого exe; «нет подходящих правил» — не ошибка.
            RunCapture(netsh, FirewallDeleteArgs(exePath), 30000, out so, out code, OemEncoding(), null);
            if (!allow)
            {
                FirewallInbound after = FirewallState(exePath);
                return after == FirewallInbound.None || after == FirewallInbound.Unknown ? null : FirewallMessage(so, code);
            }
            bool ran = RunCapture(netsh, FirewallAddArgs(exePath), 30000, out so, out code, OemEncoding(), null);
            if (!ran || code != 0) return FirewallMessage(so, code);
            return FirewallState(exePath) == FirewallInbound.Allowed ? null
                 : Tr.S("правило добавлено, но брандмауэр его не показывает", "the rule was added but the firewall does not list it");
        }

        internal static string FirewallDeleteArgs(string exePath)
        {
            return "advfirewall firewall delete rule name=all dir=in program=" + Quote(exePath);
        }

        internal static string FirewallAddArgs(string exePath)
        {
            return "advfirewall firewall add rule name=" + Quote(FirewallRuleName) + " dir=in action=allow enable=yes profile=any program="
                   + Quote(exePath) + " description=" + Quote("Incoming BitTorrent connections for this program only");
        }

        // В путях Windows кавычек не бывает; путь с кавычкой — не путь, а попытка дописать аргументы netsh.
        private static string Quote(string s)
        {
            if (s.IndexOf('"') >= 0) throw new ArgumentException("quote in a netsh argument");
            return "\"" + s + "\"";
        }

        // Правило может хранить путь с %ProgramFiles%, 8.3-псевдонимами (RUSYAN~1) и в другом регистре — брандмауэр
        // сопоставляет по настоящему файлу, значит и сравнивать надо канонический вид.
        private static string NormalizeExe(string path)
        {
            string p = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
            return Native.CanonicalPath(p).ToUpperInvariant();
        }

        private static string FileNameOf(string path)
        {
            string p = path.Trim().Trim('"');
            int slash = p.LastIndexOfAny(new char[] { '\\', '/' });
            p = slash >= 0 ? p.Substring(slash + 1) : p;
            // Короткое имя файла (WPC-TC~1.EXE) узнаётся только после раскрытия — такие правила сравниваются полностью.
            return p.IndexOf('~') >= 0 ? "" : p.ToUpperInvariant();
        }

        private static string FirewallMessage(string output, int code)
        {
            string text = (output ?? "").Trim();
            if (text.Length > 300) text = text.Substring(0, 300);
            return Tr.S("netsh завершился с кодом ", "netsh exited with code ") + code.ToString(CultureInfo.InvariantCulture)
                   + (text.Length > 0 ? ": " + text : "");
        }

        private static object ComGet(object target, string name)
        {
            return target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }
    }
}
